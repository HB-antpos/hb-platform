using System.Text.Json;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Api.Models.Planogram;
using BlazorApp.Shared.DTOs;
using SqlSugar;

namespace BlazorApp.Api.Services.Planogram;

/// <summary>
/// 批 3（附录 C）：发布、版本列表、历史版本快照。
/// 版本号规则（与批 2 的保存 / 恢复逻辑一起保证 <c>PlanogramVersion (PlanogramGuid, Version)</c> 唯一）：
/// 草稿的 <c>Version</c> 恒大于最近发布版本号 ——
/// 发布写快照用头的当前 Version；已发布方案被保存后 Version+1 回草稿；归档后恢复时 Version 至少为 PublishedVersion+1。
/// </summary>
public sealed partial class PlanogramPlanService
{
    // 发布冲突时 details 最多带这么多条，免得几千条叠在一起把响应体撑大（message 里写明总数）。
    private const int MaxReportedConflicts = 200;

    // 审计列 PublishedBy 在库里是 nvarchar(100)。
    private const int MaxActorLength = 100;

    // 快照 JSON 的读取选项：字段名大小写不敏感，容忍手工修过的数据；写入沿用主文件的 camelCase 选项（SnapshotJson）。
    private static readonly JsonSerializerOptions LayoutJsonRead = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // ================================================================ 发布

    public async Task<ApiResponse<PlanogramDetailDto>> PublishAsync(
        string guid, PlanogramPublishRequest input, string actor, CancellationToken cancellationToken)
    {
        var header = await FindHeaderAsync(guid);
        if (header is null)
        {
            return NotFound<PlanogramDetailDto>();
        }

        // 事务外的快速预检（不开事务就能拒绝的情形）。判定顺序：
        // 不存在 → 已归档 → rowVersion 缺失 / 过期 → 当前版本已发布 → [事务内] 空方案 → 有冲突 → 写入。
        if (header.Status == PlanogramStatus.Archived)
        {
            return ApiResponse<PlanogramDetailDto>.Error("方案已归档，请先恢复为草稿再发布", PlanogramErrorCodes.Archived);
        }

        if (string.IsNullOrWhiteSpace(input.RowVersion))
        {
            var errors = new PlanogramFieldErrors();
            errors.Add("rowVersion", "rowVersion 不能为空（取自读取方案时返回的 rowVersion）");
            return errors.ToFailure<PlanogramDetailDto>();
        }

        if (!RowVersionMatches(input.RowVersion, header.UpdatedAt))
        {
            return StaleFailure<PlanogramDetailDto>();
        }

        if (await SnapshotExistsAsync(header.Guid, header.Version))
        {
            return AlreadyPublishedFailure(header.Version);
        }

        var previousUpdatedAt = header.UpdatedAt;
        var now = NextUpdatedAt(previousUpdatedAt);
        var publishedBy = Truncate(actor, MaxActorLength);
        var publishedVersion = header.Version;

        // 整个「占住 → 读取 → 校验 → 写快照 → 改头」放在同一个事务里，顺序是关键：
        // 1) 先对方案头做带「UpdatedAt = 读取时的值」条件的更新，既校验没有人在预检之后改过方案，
        //    又在事务期间持有这一行的写锁 —— 保存 / 归档 / 删除都要先改这一行，所以此后它们进不来；
        // 2) 在锁内读明细 / 型号 / 商品并复算冲突：事务内的查询不带 NOLOCK（SqlSugarContext 的 DisableWithNoLockWithTran），
        //    读到的一定是已提交且不会再变的数据，快照不可能固化到别人保存到一半的明细；
        // 3) 写快照，再把头置为已发布。并发发布同一版本时，后到的一方插入快照会撞 (PlanogramGuid, Version) 唯一索引
        //    （SQL Server 上先等先到者提交），转成 PLANOGRAM_ALREADY_PUBLISHED 而不是 500。
        // 任何一步失败都整体回滚（含第 1 步对头的更新），方案原样不动，rowVersion 也不变。
        ApiResponse<PlanogramDetailDto>? failure = null;
        await _db.Ado.BeginTranAsync();
        try
        {
            header.UpdatedAt = now;
            header.UpdatedBy = actor;
            var claim = _db.Updateable(header).UpdateColumns(it => new { it.UpdatedAt, it.UpdatedBy });
            var claimed = previousUpdatedAt is { } previous
                ? await WhereVersionUnchanged(claim, header.Guid, previous).ExecuteCommandAsync()
                : await claim.Where(it => it.Guid == header.Guid).ExecuteCommandAsync();
            if (claimed == 0)
            {
                failure = StaleFailure<PlanogramDetailDto>();
            }
            else
            {
                failure = await PublishLockedAsync(header, actor, publishedBy, publishedVersion, now);
            }

            if (failure is null)
            {
                await _db.Ado.CommitTranAsync();
            }
            else
            {
                await _db.Ado.RollbackTranAsync();
            }
        }
        catch (Exception ex) when (SqlErrorClassifier.IsUniqueViolation(ex))
        {
            await _db.Ado.RollbackTranAsync();
            return AlreadyPublishedFailure(publishedVersion);
        }
        catch
        {
            await _db.Ado.RollbackTranAsync();
            throw;
        }

        if (failure is not null)
        {
            return failure;
        }

        // 从库里重新读：rowVersion 必须是库里实际保存的值（见 SaveAsync 末尾的同样处理）。
        var saved = await GetAsync(header.Guid, cancellationToken);
        return saved.Success
            ? ApiResponse<PlanogramDetailDto>.OK(saved.Data!, $"已发布 v{publishedVersion}")
            : saved;
    }

    /// <summary>
    /// 发布事务里「已占住方案头」之后的部分：复算冲突、写快照、置头。返回 null 表示全部写完（调用方提交），
    /// 返回失败响应表示不能发布（调用方回滚）。
    /// </summary>
    private async Task<ApiResponse<PlanogramDetailDto>?> PublishLockedAsync(
        PlanogramHeader header, string actor, string publishedBy, int publishedVersion, DateTime now)
    {
        // 冲突按「当前存库内容」复算，而不是信任保存时写进头的 ConflictCount：
        // 保存之后商品参数 / 挂钩型号可能又改过，只有现算的结果才反映此刻能不能出图。
        var detail = await BuildDetailAsync(header);
        if (detail is null)
        {
            return ApiResponse<PlanogramDetailDto>.Error("方案引用的货架型号不存在", PlanogramErrorCodes.NotFound);
        }

        if (detail.Items.Count == 0)
        {
            return ApiResponse<PlanogramDetailDto>.Error("方案还没有任何商品，不能发布", PlanogramErrorCodes.Empty);
        }

        // 只有 error 级冲突拦截发布；warn（如超承重）不拦。
        var blocking = detail.Conflicts.Where(conflict => conflict.Severity == PlanogramSeverity.Error).ToList();
        if (blocking.Count > 0)
        {
            var shown = Math.Min(blocking.Count, MaxReportedConflicts);
            var message = blocking.Count > shown
                ? $"方案还有 {blocking.Count} 条冲突（details 列出前 {shown} 条），请先解决再发布"
                : $"方案还有 {blocking.Count} 条冲突，请先解决再发布";
            return ApiResponse<PlanogramDetailDto>.Error(
                message, PlanogramErrorCodes.HasConflicts, blocking.Take(shown).ToList());
        }

        var snapshot = BuildSnapshot(header, detail, now, publishedBy);
        await _db.Insertable(new PlanogramVersion
        {
            PlanogramGuid = header.Guid,
            Version = publishedVersion,
            LayoutJson = JsonSerializer.Serialize(snapshot, SnapshotJson),
            PublishedBy = publishedBy,
            PublishedAt = now,
        }).ExecuteCommandAsync();

        // 头已被本事务占住（见 PublishAsync），这里不再需要版本条件。
        header.Status = PlanogramStatus.Published;
        header.PublishedVersion = publishedVersion;
        header.PublishedAt = now;
        header.PublishedBy = publishedBy;
        header.ConflictCount = detail.Stats.ConflictCount;
        header.LastValidatedAt = now;
        await _db.Updateable(header)
            .UpdateColumns(it => new { it.Status, it.PublishedVersion, it.PublishedAt, it.PublishedBy, it.ConflictCount, it.LastValidatedAt })
            .Where(it => it.Guid == header.Guid)
            .ExecuteCommandAsync();
        return null;
    }

    private async Task<bool> SnapshotExistsAsync(string planGuid, int version) =>
        await _db.Queryable<PlanogramVersion>()
            .Where(row => row.PlanogramGuid == planGuid && row.Version == version)
            .AnyAsync();

    private static ApiResponse<PlanogramDetailDto> AlreadyPublishedFailure(int version) =>
        ApiResponse<PlanogramDetailDto>.Error(
            $"版本 v{version} 已经发布过（可能刚被其他人发布），请刷新；要发布新版本请先修改并保存方案",
            PlanogramErrorCodes.AlreadyPublished);

    /// <summary>
    /// 把「刚复算过的详情」固化成快照：头、所用货架 / 挂钩型号、每个商品发布时刻的名称 / 英文名 / 图片 / 零售价与陈列参数、明细、统计。
    /// 挂钩型号只留方案用到的；明细的 specChanged 对历史版本没有意义，清成 false。
    /// </summary>
    private static PlanogramSnapshotDto BuildSnapshot(
        PlanogramHeader header, PlanogramDetailDto detail, DateTime publishedAt, string publishedBy)
    {
        var usedHooks = detail.Items.Select(item => item.HookModelGuid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new PlanogramSnapshotDto
        {
            Version = header.Version,
            PublishedAt = publishedAt,
            PublishedBy = publishedBy,
            Plan = new PlanogramSnapshotPlanDto
            {
                Guid = header.Guid,
                Name = header.Name,
                Notes = header.Notes,
                BayCount = header.BayCount,
                GapXMm = header.GapXMm,
                GapYMm = header.GapYMm,
            },
            ShelfModel = detail.ShelfModel,
            HookModels = detail.HookModels.Where(hook => usedHooks.Contains(hook.Guid)).ToList(),
            Products = detail.ProductSpecs
                .Where(product => product.Spec is not null)
                .OrderBy(product => product.ProductCode, StringComparer.Ordinal)
                .Select(product => new PlanogramSnapshotProductDto
                {
                    ProductCode = product.ProductCode,
                    ItemNumber = product.ItemNumber,
                    Barcode = product.Barcode,
                    ProductName = product.ProductName,
                    EnglishName = product.EnglishName,
                    ProductImage = product.ProductImage,
                    RetailPrice = product.RetailPrice,
                    Spec = product.Spec!,
                })
                .ToList(),
            Items = detail.Items.Select(item => new PlanogramItemDto
            {
                Guid = item.Guid,
                BayIndex = item.BayIndex,
                Face = item.Face,
                ItemType = item.ItemType,
                ProductCode = item.ProductCode,
                HookModelGuid = item.HookModelGuid,
                HoleCol = item.HoleCol,
                HoleRow = item.HoleRow,
                PerHookQty = item.PerHookQty,
                SortOrder = item.SortOrder,
                SpecChanged = false,
            }).ToList(),
            Stats = detail.Stats,
        };
    }

    // ================================================================ 版本列表 / 历史版本

    public async Task<ApiResponse<List<PlanogramVersionSummaryDto>>> ListVersionsAsync(string guid, CancellationToken cancellationToken)
    {
        var header = await FindHeaderAsync(guid);
        if (header is null)
        {
            return NotFound<List<PlanogramVersionSummaryDto>>();
        }

        // 版本表没有统计列（不为批 3 加迁移），统计从各版本固化的 JSON 里的 stats 读出来：
        // 只解析 stats 一节，不把整个快照反序列化成对象。单个方案的发布次数有限，读全量可以接受。
        var rows = await _db.Queryable<PlanogramVersion>()
            .Where(row => row.PlanogramGuid == header.Guid)
            .OrderBy(row => row.Version, OrderByType.Desc)
            .ToListAsync();

        var list = rows.Select(row =>
        {
            var stats = ReadStatsOnly(row.LayoutJson);
            return new PlanogramVersionSummaryDto
            {
                Version = row.Version,
                PublishedAt = row.PublishedAt,
                PublishedBy = row.PublishedBy,
                ItemCount = stats.ItemCount,
                SkuCount = stats.SkuCount,
                UnitCount = stats.UnitCount,
            };
        }).ToList();
        return ApiResponse<List<PlanogramVersionSummaryDto>>.OK(list);
    }

    public async Task<ApiResponse<PlanogramSnapshotDto>> GetVersionAsync(string guid, int version, CancellationToken cancellationToken)
    {
        var header = await FindHeaderAsync(guid);
        if (header is null)
        {
            return NotFound<PlanogramSnapshotDto>();
        }

        var row = await _db.Queryable<PlanogramVersion>()
            .FirstAsync(item => item.PlanogramGuid == header.Guid && item.Version == version);
        if (row is null)
        {
            return ApiResponse<PlanogramSnapshotDto>.Error($"该方案没有发布过版本 v{version}", PlanogramErrorCodes.NotFound);
        }

        var snapshot = TryParseSnapshot(row.LayoutJson);
        if (snapshot is null)
        {
            // 快照被改坏 / 不是快照格式：明确报错，不抛 500。
            return ApiResponse<PlanogramSnapshotDto>.Error(
                $"版本 v{version} 的发布快照已损坏，无法读取", PlanogramErrorCodes.SnapshotInvalid);
        }

        // 版本号 / 发布时间 / 发布人以版本表这一行为准（与版本列表一致）。
        snapshot.Version = row.Version;
        snapshot.PublishedAt = row.PublishedAt;
        snapshot.PublishedBy = row.PublishedBy;
        return ApiResponse<PlanogramSnapshotDto>.OK(snapshot);
    }

    /// <summary>反序列化快照；格式不对（非 JSON、null、缺方案 / 货架）返回 null。</summary>
    private static PlanogramSnapshotDto? TryParseSnapshot(string? layoutJson)
    {
        if (string.IsNullOrWhiteSpace(layoutJson))
        {
            return null;
        }

        PlanogramSnapshotDto? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<PlanogramSnapshotDto>(layoutJson, LayoutJsonRead);
        }
        catch (JsonException)
        {
            return null;
        }

        // 发布时一定写了方案 guid 和货架型号；缺了就不是我们写的快照（如 "{}"）。
        if (snapshot is null
            || snapshot.Plan is null || string.IsNullOrWhiteSpace(snapshot.Plan.Guid)
            || snapshot.ShelfModel is null || string.IsNullOrWhiteSpace(snapshot.ShelfModel.Guid))
        {
            return null;
        }

        // JSON 里显式写 null 时，非空声明的集合 / 对象也会是 null，统一补成空，调用方不必再判空。
        snapshot.HookModels ??= new List<HookModelDto>();
        snapshot.Products ??= new List<PlanogramSnapshotProductDto>();
        snapshot.Items ??= new List<PlanogramItemDto>();
        snapshot.Stats ??= new PlanogramStatsDto();
        return snapshot;
    }

    /// <summary>只读快照里的 stats 一节；读不出来按全 0 处理（版本仍出现在列表里，打开它时会得到明确的损坏错误）。</summary>
    private static PlanogramStatsDto ReadStatsOnly(string? layoutJson)
    {
        var stats = new PlanogramStatsDto();
        if (string.IsNullOrWhiteSpace(layoutJson))
        {
            return stats;
        }

        try
        {
            using var document = JsonDocument.Parse(layoutJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return stats;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "stats", StringComparison.OrdinalIgnoreCase)
                    || property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var field in property.Value.EnumerateObject())
                {
                    if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out var number))
                    {
                        continue;
                    }

                    if (string.Equals(field.Name, "itemCount", StringComparison.OrdinalIgnoreCase))
                    {
                        stats.ItemCount = number;
                    }
                    else if (string.Equals(field.Name, "skuCount", StringComparison.OrdinalIgnoreCase))
                    {
                        stats.SkuCount = number;
                    }
                    else if (string.Equals(field.Name, "unitCount", StringComparison.OrdinalIgnoreCase))
                    {
                        stats.UnitCount = number;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return new PlanogramStatsDto();
        }

        return stats;
    }

    private static string Truncate(string? value, int max)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= max ? text : text[..max];
    }
}
