using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlazorApp.Api.Data;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using SqlSugar;

namespace BlazorApp.Api.Services.HelpFaq;

/// <summary>公开 FAQ 快照：数据 + 由内容摘要生成的强 ETag（含双引号）。</summary>
public sealed record HelpFaqPublicSnapshot(HelpFaqPublicDto Data, string ETag);

public interface IHelpFaqPublicService
{
    /// <summary>读取已发布内容（服务端内存缓存，短 TTL）。</summary>
    Task<HelpFaqPublicSnapshot> GetPublishedAsync(CancellationToken cancellationToken = default);

    /// <summary>清除缓存。P2 的任何写操作（保存 / 发布 / 排序 / 删除）成功后必须调用。</summary>
    void Invalidate();
}

/// <summary>
/// 把库里的已发布 FAQ 投影成公开 DTO 的纯函数集合（无 I/O，便于单测）。
/// 规则：只输出 Status=Published 且未删除的题；无已发布题的分组整组省略；
/// 分组、题按 SortOrder 升序（并列时按 Code 字典序，保证稳定）；只认 pos / mobile 两个 Tab；
/// 绝不读取 DraftJson。
/// </summary>
public static class HelpFaqPublicProjector
{
    public static HelpFaqPublicDto Build(
        IEnumerable<HelpFaqGroup> groups,
        IEnumerable<HelpFaqQuestion> questions,
        Action<HelpFaqQuestion, Exception>? onBadQuestion = null
    )
    {
        var publishedByGroup = questions
            .Where(q => !q.IsDeleted && q.Status == HelpFaqStatuses.Published)
            .GroupBy(q => q.GroupGUID)
            .ToDictionary(g => g.Key, g => g.ToList());

        var dto = new HelpFaqPublicDto();
        foreach (var tab in HelpFaqTabs.All)
        {
            var target = tab == HelpFaqTabs.Pos ? dto.Tabs.Pos : dto.Tabs.Mobile;
            var tabGroups = groups
                .Where(g => !g.IsDeleted && g.Tab == tab)
                .OrderBy(g => g.SortOrder)
                .ThenBy(g => g.Code, StringComparer.Ordinal);

            foreach (var group in tabGroups)
            {
                if (!publishedByGroup.TryGetValue(group.GroupGUID, out var rows))
                {
                    continue;
                }

                var groupDto = new HelpFaqGroupDto
                {
                    Id = group.Code,
                    Title = new HelpFaqTextDto { Zh = group.TitleZh, En = group.TitleEn },
                };

                foreach (
                    var row in rows
                        .OrderBy(q => q.SortOrder)
                        .ThenBy(q => q.Code, StringComparer.Ordinal)
                )
                {
                    try
                    {
                        groupDto.Questions.Add(HelpFaqRowMapper.ToDto(row));
                    }
                    catch (JsonException exception)
                    {
                        // 单题 JSON 损坏只跳过该题，绝不让整个公开页 500。
                        onBadQuestion?.Invoke(row, exception);
                    }
                }

                if (groupDto.Questions.Count > 0)
                {
                    target.Add(groupDto);
                }
            }
        }

        return dto;
    }

    /// <summary>
    /// 内容版本：对 tabs 序列化结果取 SHA-256 前 16 位十六进制。
    /// 内容不变则版本不变（ETag 稳定），任何字段 / 顺序 / 分组标题变化都会改变版本，
    /// 不依赖 P2 写入时是否记得维护 UpdatedAt。
    /// </summary>
    public static string ComputeVersion(HelpFaqPublicDto dto)
    {
        var payload = JsonSerializer.Serialize(dto.Tabs, HelpFaqRowMapper.JsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    public static string ToETag(string version) => $"\"{version}\"";
}

public sealed class HelpFaqPublicService : IHelpFaqPublicService
{
    internal const string CacheKey = "help-faq:public:v1";

    /// <summary>服务端缓存 TTL。P2 写操作会主动 Invalidate，TTL 只是多实例 / 直改库时的兜底。</summary>
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    // 进程内共享：多个请求同时未命中缓存时只放一个去查库；代数用于丢弃"失效发生在查询途中"的过期结果。
    private static readonly SemaphoreSlim LoadGate = new(1, 1);
    private static int _generation;

    private readonly ISqlSugarClient _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<HelpFaqPublicService> _logger;

    public HelpFaqPublicService(
        SqlSugarContext context,
        IMemoryCache cache,
        ILogger<HelpFaqPublicService> logger
    )
        : this(context.Db, cache, logger) { }

    internal HelpFaqPublicService(
        ISqlSugarClient db,
        IMemoryCache cache,
        ILogger<HelpFaqPublicService> logger
    )
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    public async Task<HelpFaqPublicSnapshot> GetPublishedAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (_cache.TryGetValue(CacheKey, out HelpFaqPublicSnapshot? cached) && cached is not null)
        {
            return cached;
        }

        await LoadGate.WaitAsync(cancellationToken);
        try
        {
            // 等锁期间别的请求可能已经加载完成。
            if (_cache.TryGetValue(CacheKey, out cached) && cached is not null)
            {
                return cached;
            }

            var generation = Volatile.Read(ref _generation);
            var snapshot = await LoadAsync(cancellationToken);

            // 查询途中发生过 Invalidate：这份结果可能已过期，只返回给本次请求，不写缓存。
            if (generation == Volatile.Read(ref _generation))
            {
                _cache.Set(CacheKey, snapshot, CacheTtl);
            }

            return snapshot;
        }
        finally
        {
            LoadGate.Release();
        }
    }

    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _cache.Remove(CacheKey);
    }

    private async Task<HelpFaqPublicSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var groups = await _db.Queryable<HelpFaqGroup>().Where(g => g.IsDeleted == false).ToListAsync();
        var questions = await _db.Queryable<HelpFaqQuestion>()
            .Where(q => q.IsDeleted == false && q.Status == HelpFaqStatuses.Published)
            // 公开读取不需要草稿快照，不要把它从库里拉出来。
            .IgnoreColumns(q => q.DraftJson)
            .ToListAsync();

        var dto = HelpFaqPublicProjector.Build(
            groups,
            questions,
            (row, exception) =>
                _logger.LogWarning(
                    exception,
                    "FAQ 问题 {Code} 的 JSON 列损坏，已在公开接口中跳过。",
                    row.Code
                )
        );
        dto.Version = HelpFaqPublicProjector.ComputeVersion(dto);
        return new HelpFaqPublicSnapshot(dto, HelpFaqPublicProjector.ToETag(dto.Version));
    }
}
