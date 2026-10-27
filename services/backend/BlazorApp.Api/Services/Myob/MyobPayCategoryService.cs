using BlazorApp.Api.Data;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Shared.DTOs;
using Microsoft.Extensions.Caching.Memory;
using SqlSugar;

namespace BlazorApp.Api.Services.Myob;

public interface IMyobPayCategoryService
{
    Task<ApiResponse<MyobPayCategoriesDto>> GetAsync(string connectionGuid, bool refresh, CancellationToken cancellationToken);

    Task<ApiResponse<MyobPayCategoriesDto>> SaveAsync(
        string connectionGuid, MyobPayCategoryMappingDto mapping, string actorUserGuid, CancellationToken cancellationToken);

    /// <summary>已保存的映射（没有记录返回空映射）。预览用，不访问 MYOB。</summary>
    Task<MyobPayCategoryMappingDto> GetSavedMappingAsync(string connectionGuid);

    /// <summary>缓存里的 MYOB 工资类别名（UID → 名称）。只读内存缓存，不访问 MYOB；缓存没有就返回空。</summary>
    IReadOnlyDictionary<string, string> GetCachedCategoryNames(string connectionGuid);
}

/// <summary>
/// Timesheet 工资类别映射（方案第 8 节）：我方 5 档转账工时 → MYOB 工资类别。
/// 类别列表实时从 MYOB 读（缓存 10 分钟，「重新读取」绕过缓存）；库里只存 UID，名称不落库。
/// 名称相近的类别会作为「建议」预填，但必须人工保存才生效。
/// </summary>
public sealed class MyobPayCategoryService : IMyobPayCategoryService
{
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    private readonly ISqlSugarClient _db;
    private readonly IMyobApiClient _client;
    private readonly IMyobTokenProvider _tokens;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _time;

    public MyobPayCategoryService(
        SqlSugarContext context,
        IMyobApiClient client,
        IMyobTokenProvider tokens,
        IMemoryCache cache,
        TimeProvider? timeProvider = null)
        : this(context.Db, client, tokens, cache, timeProvider)
    {
    }

    internal MyobPayCategoryService(
        ISqlSugarClient db,
        IMyobApiClient client,
        IMyobTokenProvider tokens,
        IMemoryCache cache,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _client = client;
        _tokens = tokens;
        _cache = cache;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<ApiResponse<MyobPayCategoriesDto>> GetAsync(
        string connectionGuid,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var connection = await FindActiveAsync(connectionGuid);
        if (connection is null)
        {
            return ApiResponse<MyobPayCategoriesDto>.Error("MYOB 连接不存在或未启用", MyobErrorCodes.ConnectionNotActive);
        }

        CategorySnapshot snapshot;
        try
        {
            snapshot = await LoadCategoriesAsync(connection, refresh, cancellationToken);
        }
        catch (MyobApiException ex)
        {
            return ApiResponse<MyobPayCategoriesDto>.Error(ex.Message, ex.Code);
        }

        return ApiResponse<MyobPayCategoriesDto>.OK(await BuildDtoAsync(connection, snapshot));
    }

    public async Task<ApiResponse<MyobPayCategoriesDto>> SaveAsync(
        string connectionGuid,
        MyobPayCategoryMappingDto mapping,
        string actorUserGuid,
        CancellationToken cancellationToken)
    {
        var connection = await FindActiveAsync(connectionGuid);
        if (connection is null)
        {
            return ApiResponse<MyobPayCategoriesDto>.Error("MYOB 连接不存在或未启用", MyobErrorCodes.ConnectionNotActive);
        }

        CategorySnapshot snapshot;
        try
        {
            snapshot = await LoadCategoriesAsync(connection, false, cancellationToken);
        }
        catch (MyobApiException ex)
        {
            return ApiResponse<MyobPayCategoriesDto>.Error(ex.Message, ex.Code);
        }

        var values = ToDictionary(Normalize(mapping));
        var known = snapshot.Categories.Select(item => item.Uid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, uid) in values)
        {
            if (uid is not null && !known.Contains(uid))
            {
                return ApiResponse<MyobPayCategoriesDto>.Error(
                    $"所选工资类别在 MYOB 里不存在（{key}），请重新读取后再选", MyobTimesheetErrorCodes.CategoryNotFound);
            }
        }

        var chosen = values.Values.Where(uid => uid is not null).Select(uid => uid!).ToList();
        if (chosen.Count != chosen.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            return ApiResponse<MyobPayCategoriesDto>.Error(
                "同一个 MYOB 工资类别不能同时映射到两项", MyobTimesheetErrorCodes.CategoryDuplicate);
        }

        var normalized = Normalize(mapping);
        var existing = await _db.Queryable<MyobPayCategoryMapping>().FirstAsync(item => item.ConnectionGuid == connectionGuid);
        var now = _time.GetUtcNow().UtcDateTime;
        var row = existing ?? new MyobPayCategoryMapping { ConnectionGuid = connectionGuid };
        row.WeekdayUid = normalized.WeekdayUid;
        row.SaturdayUid = normalized.SaturdayUid;
        row.SundayUid = normalized.SundayUid;
        row.PersonalLeaveUid = normalized.PersonalLeaveUid;
        row.HolidayLeaveUid = normalized.HolidayLeaveUid;
        row.UpdatedAt = now;
        row.UpdatedByUserGuid = string.IsNullOrWhiteSpace(actorUserGuid) ? null : actorUserGuid;
        if (existing is null)
        {
            await _db.Insertable(row).ExecuteCommandAsync();
        }
        else
        {
            await _db.Updateable(row).ExecuteCommandAsync();
        }

        return ApiResponse<MyobPayCategoriesDto>.OK(await BuildDtoAsync(connection, snapshot), "工资类别映射已保存");
    }

    public async Task<MyobPayCategoryMappingDto> GetSavedMappingAsync(string connectionGuid)
    {
        var row = await _db.Queryable<MyobPayCategoryMapping>().FirstAsync(item => item.ConnectionGuid == connectionGuid);
        return row is null ? new MyobPayCategoryMappingDto() : ToDto(row);
    }

    public IReadOnlyDictionary<string, string> GetCachedCategoryNames(string connectionGuid) =>
        _cache.TryGetValue($"myob:wagecats:{connectionGuid}", out CategorySnapshot? snapshot) && snapshot is not null
            ? snapshot.Categories.GroupBy(item => item.Uid, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>();

    // ───────────────────────── 构建 DTO ─────────────────────────

    private async Task<MyobPayCategoriesDto> BuildDtoAsync(MyobConnection connection, CategorySnapshot snapshot)
    {
        var row = await _db.Queryable<MyobPayCategoryMapping>().FirstAsync(item => item.ConnectionGuid == connection.ConnectionGuid);
        var mapping = row is null ? new MyobPayCategoryMappingDto() : ToDto(row);
        var known = snapshot.Categories.Select(item => item.Uid).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = ToDictionary(mapping)
            .Where(pair => pair.Value is not null && !known.Contains(pair.Value))
            .Select(pair => pair.Key)
            .ToList();
        // 已设置但 MYOB 里找不到的项不算「已映射」。
        var effective = ToDictionary(mapping)
            .Where(pair => pair.Value is not null && known.Contains(pair.Value))
            .ToList();

        return new MyobPayCategoriesDto
        {
            ConnectionGuid = connection.ConnectionGuid,
            ConnectionName = connection.BusinessName,
            FetchedAt = snapshot.FetchedAt,
            Categories = snapshot.Categories
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => new MyobWageCategoryDto { Uid = item.Uid, Name = item.Name, WageType = item.WageType })
                .ToList(),
            Mapping = mapping,
            Suggestions = SuggestMapping(snapshot.Categories, mapping),
            MappedCount = effective.Count,
            Complete = effective.Count == MyobPayCategoryKeys.All.Length,
            MissingKeys = missing,
            UpdatedAt = row?.UpdatedAt,
        };
    }

    /// <summary>
    /// 按名称相近给未设置的项推荐类别（已设置的项不给建议）：
    /// 周六 / 周日 看名称里的 Saturday / Sunday；病假 = Personal Leave（Pay）/ Sick；年假 = Holiday Leave（Pay）/ Annual；
    /// 平日 优先 Base Hourly，其次 Ordinary，再次其他不含周末 / 假期 / 加班字样的计时类别。同一个类别不会被推荐给两项。
    /// </summary>
    internal static MyobPayCategoryMappingDto SuggestMapping(
        IReadOnlyList<MyobWageCategoryRecord> categories,
        MyobPayCategoryMappingDto current)
    {
        var used = ToDictionary(current).Values
            .Where(uid => uid is not null)
            .Select(uid => uid!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suggestion = new MyobPayCategoryMappingDto();

        string? Pick(Func<MyobWageCategoryRecord, int> score)
        {
            var best = categories
                .Where(item => !used.Contains(item.Uid))
                .Select(item => (Item: item, Score: score(item)))
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Item.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (best.Item is null)
            {
                return null;
            }

            used.Add(best.Item.Uid);
            return best.Item.Uid;
        }

        static bool Has(MyobWageCategoryRecord item, string text) =>
            item.Name.Contains(text, StringComparison.OrdinalIgnoreCase);

        // 先挑最没有歧义的几项，再挑平日（平日条件最宽，放最后避免抢走周末 / 假期类别）。
        if (current.SaturdayUid is null)
        {
            suggestion.SaturdayUid = Pick(item => Has(item, "saturday") ? 10 : 0);
        }

        if (current.SundayUid is null)
        {
            suggestion.SundayUid = Pick(item => Has(item, "sunday") ? 10 : 0);
        }

        if (current.PersonalLeaveUid is null)
        {
            suggestion.PersonalLeaveUid = Pick(item =>
                Has(item, "personal leave pay") ? 20
                : Has(item, "personal leave") ? 15
                : Has(item, "sick") ? 10
                : 0);
        }

        if (current.HolidayLeaveUid is null)
        {
            suggestion.HolidayLeaveUid = Pick(item =>
                Has(item, "holiday leave pay") ? 20
                : Has(item, "holiday leave") ? 15
                : Has(item, "annual leave") ? 12
                : Has(item, "holiday pay") ? 10
                : 0);
        }

        if (current.WeekdayUid is null)
        {
            suggestion.WeekdayUid = Pick(item =>
            {
                var name = item.Name;
                var excluded = new[] { "saturday", "sunday", "leave", "holiday", "overtime", "public", "sick", "annual", "bonus", "allowance", "commission" };
                if (excluded.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
                {
                    return 0;
                }

                var hourly = string.Equals(item.WageType, "Hourly", StringComparison.OrdinalIgnoreCase);
                return Has(item, "base hourly") ? 30
                    : Has(item, "ordinary") ? 25
                    : Has(item, "weekday") ? 22
                    : Has(item, "hourly") ? 20
                    : hourly ? 5
                    : 0;
            });
        }

        return suggestion;
    }

    // ───────────────────────── 辅助 ─────────────────────────

    private sealed record CategorySnapshot(DateTime FetchedAt, IReadOnlyList<MyobWageCategoryRecord> Categories);

    private async Task<CategorySnapshot> LoadCategoriesAsync(MyobConnection connection, bool refresh, CancellationToken cancellationToken)
    {
        var key = $"myob:wagecats:{connection.ConnectionGuid}";
        if (!refresh && _cache.TryGetValue(key, out CategorySnapshot? cached) && cached is not null)
        {
            return cached;
        }

        var token = await _tokens.GetAccessTokenAsync(connection.ConnectionGuid, cancellationToken);
        var categories = await _client.GetWageCategoriesAsync(connection.BusinessId, token, cancellationToken);
        var snapshot = new CategorySnapshot(_time.GetUtcNow().UtcDateTime, categories);
        _cache.Set(key, snapshot, CacheLifetime);
        return snapshot;
    }

    private Task<MyobConnection?> FindActiveAsync(string connectionGuid) =>
        _db.Queryable<MyobConnection>()
            .FirstAsync(item => item.ConnectionGuid == connectionGuid && item.Status == MyobConnectionStatuses.Active)!;

    private static MyobPayCategoryMappingDto ToDto(MyobPayCategoryMapping row) => new()
    {
        WeekdayUid = row.WeekdayUid,
        SaturdayUid = row.SaturdayUid,
        SundayUid = row.SundayUid,
        PersonalLeaveUid = row.PersonalLeaveUid,
        HolidayLeaveUid = row.HolidayLeaveUid,
    };

    private static MyobPayCategoryMappingDto Normalize(MyobPayCategoryMappingDto mapping)
    {
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return new MyobPayCategoryMappingDto
        {
            WeekdayUid = Clean(mapping.WeekdayUid),
            SaturdayUid = Clean(mapping.SaturdayUid),
            SundayUid = Clean(mapping.SundayUid),
            PersonalLeaveUid = Clean(mapping.PersonalLeaveUid),
            HolidayLeaveUid = Clean(mapping.HolidayLeaveUid),
        };
    }

    /// <summary>映射 → {类别键: UID}，键用 <see cref="MyobPayCategoryKeys"/>。</summary>
    internal static Dictionary<string, string?> ToDictionary(MyobPayCategoryMappingDto mapping) => new()
    {
        [MyobPayCategoryKeys.Weekday] = mapping.WeekdayUid,
        [MyobPayCategoryKeys.Saturday] = mapping.SaturdayUid,
        [MyobPayCategoryKeys.Sunday] = mapping.SundayUid,
        [MyobPayCategoryKeys.PersonalLeave] = mapping.PersonalLeaveUid,
        [MyobPayCategoryKeys.HolidayLeave] = mapping.HolidayLeaveUid,
    };
}
