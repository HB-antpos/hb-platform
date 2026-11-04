using System.Globalization;
using System.Text.Json;
using BlazorApp.Api.Data;
using BlazorApp.Api.Models.Planogram;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.Planogram;

public interface IPlanogramPlanService
{
    Task<ApiResponse<PagedResult<PlanogramListItemDto>>> ListAsync(
        string? shelfModelGuid, int? status, string? keyword, int page, int pageSize, CancellationToken cancellationToken);

    Task<ApiResponse<PlanogramDetailDto>> CreateAsync(
        PlanogramCreateRequest input, string actor, CancellationToken cancellationToken);

    Task<ApiResponse<PlanogramDetailDto>> GetAsync(string guid, CancellationToken cancellationToken);

    /// <summary>整体保存头 + 明细（rowVersion 乐观并发、明细整体替换、服务端复算冲突）。</summary>
    Task<ApiResponse<PlanogramDetailDto>> SaveAsync(
        string guid, PlanogramSaveRequest input, string actor, CancellationToken cancellationToken);

    /// <summary>只校验不落库：按请求体里的货架 / 节数 / 间距 / 明细算冲突与统计。</summary>
    Task<ApiResponse<PlanogramValidateResultDto>> ValidateAsync(
        string guid, PlanogramSaveRequest input, CancellationToken cancellationToken);

    Task<ApiResponse<PlanogramDetailDto>> DuplicateAsync(
        string guid, PlanogramDuplicateRequest input, string actor, CancellationToken cancellationToken);

    Task<ApiResponse<PlanogramListItemDto>> ArchiveAsync(string guid, string actor, CancellationToken cancellationToken);

    Task<ApiResponse<PlanogramListItemDto>> RestoreAsync(string guid, string actor, CancellationToken cancellationToken);

    Task<ApiResponse<object>> DeleteAsync(string guid, CancellationToken cancellationToken);

    // ---- 批 3（附录 C）：发布、版本快照、JSON 导入导出。实现在 PlanogramPlanService.Publish.cs / .Transfer.cs。

    /// <summary>发布：复算冲突后固化快照，写 <c>PlanogramVersion</c> 并把头置为已发布（同一事务）。</summary>
    Task<ApiResponse<PlanogramDetailDto>> PublishAsync(
        string guid, PlanogramPublishRequest input, string actor, CancellationToken cancellationToken);

    /// <summary>历史版本列表，按版本倒序。</summary>
    Task<ApiResponse<List<PlanogramVersionSummaryDto>>> ListVersionsAsync(string guid, CancellationToken cancellationToken);

    /// <summary>读取某个历史版本的快照（供按当时数据重出 PDF）。</summary>
    Task<ApiResponse<PlanogramSnapshotDto>> GetVersionAsync(string guid, int version, CancellationToken cancellationToken);

    /// <summary>导出方案 JSON（用型号编码与商品货号，不带 guid）。</summary>
    Task<ApiResponse<PlanogramExportDto>> ExportAsync(string guid, CancellationToken cancellationToken);

    /// <summary>导入方案 JSON：创建新的草稿方案（version=1）。</summary>
    Task<ApiResponse<PlanogramDetailDto>> ImportAsync(
        PlanogramImportRequest input, string actor, CancellationToken cancellationToken);
}

/// <summary>
/// 陈列方案（批 2，契约见 docs/plans/2026-11-04-planogram-shelf-display-designer.md 附录 B.2）：
/// 列表 / 新建 / 读取 / 整体保存 / 校验 / 复制 / 归档 / 恢复 / 删除。
/// 权限在控制器按方法区分（GET 为 Planogram.View，保存 / 校验 / 复制为 Planogram.Edit，归档 / 恢复 / 删除为 Planogram.Manage），
/// 这里不判断权限；方案不绑定分店，也不接分店范围服务。
/// 几何与统计全部交给 <see cref="PlanogramLayoutValidator"/>（纯函数，与前端共用同一份夹具）。
/// 批 3（发布 / 版本快照 / 导入导出，附录 C）拆在同一个 partial 类的 Publish.cs / Transfer.cs 里，共用本文件的保存校验与查询辅助。
/// </summary>
public sealed partial class PlanogramPlanService : IPlanogramPlanService
{
    /// <summary>单个方案的明细上限。</summary>
    internal const int MaxItems = 2000;

    private const int MaxBayCount = 12;
    private const int DefaultGapMm = 10;
    private const int MaxGapMm = 200;
    private const int MaxPageSize = 100;

    // 坐标 / 节号的合理范围：越界值本身是「冲突」而不是请求错误，这里只防 int 溢出与明显的垃圾值。
    private const int MaxCoordinate = 100000;
    private const int MaxPerHookQty = 9999;

    // 明细逐条错误最多报这么多条，免得 2000 条全错时响应体过大。
    private const int MaxReportedItemErrors = 50;

    // IN 子句每批上限：SQL Server 单条语句最多 2100 个参数。
    private const int InClauseChunk = 500;

    // 乐观并发靠 UpdatedAt：每次写入 UpdatedAt 至少比旧值大 MinUpdateStep，相邻两个版本因此至少相差 10 ms。
    // 但 SqlSugar 把 DateTime 参数按 DbType.DateTime（约 3.33 ms 精度，会四舍五入）发给 SQL Server，
    // 而列是 datetime2(7)，「UpdatedAt = @参数」这种精确等值在个别时间戳上会落空（实测：真实时钟下第一次保存就误报过期）。
    // 所以条件更新用 ±VersionWindow 窗口代替精确相等：窗口（±3 ms）小于相邻版本的最小间隔（10 ms 减去两次舍入误差仍 > 6 ms），
    // 窗口内只可能是同一个版本，语义等同精确相等。rowVersion 令牌本身仍是两次「从库里读出的值」的精确字符串比较。
    private static readonly TimeSpan MinUpdateStep = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan VersionWindow = TimeSpan.FromMilliseconds(3);

    private static readonly JsonSerializerOptions SnapshotJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ISqlSugarClient _db;
    private readonly TimeProvider _time;
    private readonly PlanogramModelService _models;
    private readonly ProductDisplaySpecService _specs;

    public PlanogramPlanService(SqlSugarContext context, TimeProvider? timeProvider = null)
        : this(context.Db, timeProvider)
    {
    }

    internal PlanogramPlanService(ISqlSugarClient db, TimeProvider? timeProvider = null)
    {
        _db = db;
        _time = timeProvider ?? TimeProvider.System;
        _models = new PlanogramModelService(db, _time);
        _specs = new ProductDisplaySpecService(db, _time);
    }

    // ================================================================ 列表

    public async Task<ApiResponse<PagedResult<PlanogramListItemDto>>> ListAsync(
        string? shelfModelGuid, int? status, string? keyword, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (status is not null && status is not (PlanogramStatus.Draft or PlanogramStatus.Published or PlanogramStatus.Archived))
        {
            var errors = new PlanogramFieldErrors();
            errors.Add("status", "状态只能是 0（草稿）、1（已发布）或 2（已归档）");
            return errors.ToFailure<PagedResult<PlanogramListItemDto>>();
        }

        page = Math.Max(page, 1);
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, MaxPageSize);

        var query = _db.Queryable<PlanogramHeader>().Where(plan => plan.IsDeleted == false);
        var shelfGuid = shelfModelGuid?.Trim();
        if (!string.IsNullOrEmpty(shelfGuid))
        {
            query = query.Where(plan => plan.ShelfModelGuid == shelfGuid);
        }

        if (status is { } wanted)
        {
            var wantedStatus = (byte)wanted;
            query = query.Where(plan => plan.Status == wantedStatus);
        }

        var key = keyword?.Trim();
        if (!string.IsNullOrEmpty(key))
        {
            // 关键字匹配方案名称，以及明细里商品的货号 / 条码 / 名称 / 英文名（联 Product）。
            var planGuidsByProduct = await _db.Queryable<PlanogramItem, Product>((item, product) => item.ProductCode == product.ProductCode)
                .Where((item, product) =>
                    product.ProductCode!.Contains(key)
                    || product.ItemNumber!.Contains(key)
                    || product.Barcode!.Contains(key)
                    || product.ProductName.Contains(key)
                    || product.EnglishName!.Contains(key))
                .GroupBy((item, product) => item.PlanogramGuid)
                .Select((item, product) => item.PlanogramGuid)
                .ToListAsync();
            query = planGuidsByProduct.Count == 0
                ? query.Where(plan => plan.Name.Contains(key))
                : query.Where(plan => plan.Name.Contains(key) || planGuidsByProduct.Contains(plan.Guid));
        }

        RefAsync<int> total = 0;
        var headers = await query
            .OrderBy(plan => plan.UpdatedAt, OrderByType.Desc)
            .OrderBy(plan => plan.Name)
            .ToPageListAsync(page, pageSize, total);

        var rows = await BuildListItemsAsync(headers);
        return ApiResponse<PagedResult<PlanogramListItemDto>>.OK(new PagedResult<PlanogramListItemDto>
        {
            Items = rows,
            Total = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    // ================================================================ 新建

    public async Task<ApiResponse<PlanogramDetailDto>> CreateAsync(
        PlanogramCreateRequest input, string actor, CancellationToken cancellationToken)
    {
        var errors = new PlanogramFieldErrors();
        var name = errors.RequiredText("name", "方案名称", input.Name, 100);
        var notes = errors.OptionalText("notes", "备注", input.Notes, 500);
        errors.IntRange("bayCount", "节数", input.BayCount, 1, MaxBayCount);
        var gapX = input.GapXMm ?? DefaultGapMm;
        var gapY = input.GapYMm ?? DefaultGapMm;
        errors.IntRange("gapXMm", "左右间距", gapX, 0, MaxGapMm);
        errors.IntRange("gapYMm", "上下间距", gapY, 0, MaxGapMm);
        var shelf = await ResolveShelfAsync(input.ShelfModelGuid, null, errors);
        if (errors.Any)
        {
            return errors.ToFailure<PlanogramDetailDto>();
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var header = new PlanogramHeader
        {
            Name = name,
            Notes = notes,
            GapXMm = gapX,
            GapYMm = gapY,
            ShelfModelGuid = shelf!.Guid,
            BayCount = input.BayCount,
            Status = PlanogramStatus.Draft,
            Version = 1,
            ConflictCount = 0,
            LastValidatedAt = now,
            CreatedAt = now,
            CreatedBy = actor,
            UpdatedAt = now,
            UpdatedBy = actor,
        };
        await _db.Insertable(header).ExecuteCommandAsync();
        return await GetAsync(header.Guid, cancellationToken);
    }

    // ================================================================ 读取

    public async Task<ApiResponse<PlanogramDetailDto>> GetAsync(string guid, CancellationToken cancellationToken)
    {
        var header = await FindHeaderAsync(guid);
        if (header is null)
        {
            return NotFound<PlanogramDetailDto>();
        }

        var detail = await BuildDetailAsync(header);
        return detail is null
            ? ApiResponse<PlanogramDetailDto>.Error("方案引用的货架型号不存在", PlanogramErrorCodes.NotFound)
            : ApiResponse<PlanogramDetailDto>.OK(detail);
    }

    // ================================================================ 整体保存

    public async Task<ApiResponse<PlanogramDetailDto>> SaveAsync(
        string guid, PlanogramSaveRequest input, string actor, CancellationToken cancellationToken)
    {
        var header = await FindHeaderAsync(guid);
        if (header is null)
        {
            return NotFound<PlanogramDetailDto>();
        }

        // 判定顺序：不存在 → 已归档 → rowVersion 过期 → 字段校验 → 写入。
        if (header.Status == PlanogramStatus.Archived)
        {
            return ApiResponse<PlanogramDetailDto>.Error("方案已归档，请先恢复为草稿再保存", PlanogramErrorCodes.Archived);
        }

        if (!string.IsNullOrWhiteSpace(input.RowVersion) && !RowVersionMatches(input.RowVersion, header.UpdatedAt))
        {
            return StaleFailure<PlanogramDetailDto>();
        }

        var outcome = await PrepareAsync(input, guid, header.ShelfModelGuid, requireName: true);
        if (string.IsNullOrWhiteSpace(input.RowVersion))
        {
            outcome.Errors.Add("rowVersion", "rowVersion 不能为空（取自读取方案时返回的 rowVersion）");
        }

        if (outcome.Errors.Any || outcome.Plan is null)
        {
            return outcome.Errors.ToFailure<PlanogramDetailDto>();
        }

        var plan = outcome.Plan;
        var lookups = await LoadLookupsAsync(plan.Items.Select(item => item.ProductCode), plan.Items.Select(item => item.HookModelGuid));
        var layout = Evaluate(plan, lookups);

        var previousUpdatedAt = header.UpdatedAt;
        var now = NextUpdatedAt(previousUpdatedAt);

        // 已发布的方案被保存：版本号 +1、回到草稿（再次发布才会生成新快照）。
        var wasPublished = header.Status == PlanogramStatus.Published;
        header.Name = plan.Name;
        header.Notes = plan.Notes;
        header.ShelfModelGuid = plan.Shelf.Guid;
        header.BayCount = plan.BayCount;
        header.GapXMm = plan.GapXMm;
        header.GapYMm = plan.GapYMm;
        header.ConflictCount = layout.Stats.ConflictCount;
        header.LastValidatedAt = now;
        header.UpdatedAt = now;
        header.UpdatedBy = actor;
        if (wasPublished)
        {
            header.Status = PlanogramStatus.Draft;
            header.Version += 1;
        }

        // 不变量：草稿的版本号恒大于最近发布版本号，否则下次发布会撞上 PlanogramVersion (PlanogramGuid, Version) 唯一索引。
        // 上面的「已发布保存 +1」与恢复归档时的 +1 在正常流程下已保证；这里兜底直连库等造成的异常状态（草稿版本号 ≤ 最近发布版本号）。
        if (header.PublishedVersion is { } publishedVersion && header.Version <= publishedVersion)
        {
            header.Version = publishedVersion + 1;
        }

        var rows = plan.Items.Select(item => ToEntity(guid, item, lookups)).ToList();

        // 头与明细在同一个事务里写：头的更新带「UpdatedAt = 读取时的值」条件，别人抢先保存了则 0 行受影响 → 回滚报过期。
        var stale = false;
        await _db.Ado.BeginTranAsync();
        try
        {
            var update = _db.Updateable(header).UpdateColumns(it => new
            {
                it.Name,
                it.Notes,
                it.ShelfModelGuid,
                it.BayCount,
                it.GapXMm,
                it.GapYMm,
                it.Status,
                it.Version,
                it.ConflictCount,
                it.LastValidatedAt,
                it.UpdatedAt,
                it.UpdatedBy,
            });
            var affected = previousUpdatedAt is { } previous
                ? await WhereVersionUnchanged(update, guid, previous).ExecuteCommandAsync()
                : await update.Where(it => it.Guid == guid).ExecuteCommandAsync();
            if (affected == 0)
            {
                stale = true;
                await _db.Ado.RollbackTranAsync();
            }
            else
            {
                await _db.Deleteable<PlanogramItem>().Where(it => it.PlanogramGuid == guid).ExecuteCommandAsync();
                if (rows.Count > 0)
                {
                    // 每批 100 行 × 12 列 < SQL Server 2100 参数上限。
                    await _db.Insertable(rows).PageSize(100).ExecuteCommandAsync();
                }

                await _db.Ado.CommitTranAsync();
            }
        }
        catch
        {
            await _db.Ado.RollbackTranAsync();
            throw;
        }

        if (stale)
        {
            return StaleFailure<PlanogramDetailDto>();
        }

        // 从库里重新读：rowVersion 必须是库里实际保存的值（数据库可能比 C# 的 DateTime 精度低），否则下一次保存会误报过期。
        var saved = await GetAsync(guid, cancellationToken);
        return saved.Success
            ? ApiResponse<PlanogramDetailDto>.OK(saved.Data!, "已保存")
            : saved;
    }

    // ================================================================ 只校验

    public async Task<ApiResponse<PlanogramValidateResultDto>> ValidateAsync(
        string guid, PlanogramSaveRequest input, CancellationToken cancellationToken)
    {
        var header = await FindHeaderAsync(guid);
        if (header is null)
        {
            return NotFound<PlanogramValidateResultDto>();
        }

        // 名称 / 备注 / rowVersion 与校验无关，不检查。
        var outcome = await PrepareAsync(input, guid, header.ShelfModelGuid, requireName: false);
        if (outcome.Errors.Any || outcome.Plan is null)
        {
            return outcome.Errors.ToFailure<PlanogramValidateResultDto>();
        }

        var plan = outcome.Plan;
        var lookups = await LoadLookupsAsync(plan.Items.Select(item => item.ProductCode), plan.Items.Select(item => item.HookModelGuid));
        var layout = Evaluate(plan, lookups);
        return ApiResponse<PlanogramValidateResultDto>.OK(new PlanogramValidateResultDto
        {
            Conflicts = layout.Conflicts,
            Stats = layout.Stats,
        });
    }

    // ================================================================ 复制

    public async Task<ApiResponse<PlanogramDetailDto>> DuplicateAsync(
        string guid, PlanogramDuplicateRequest input, string actor, CancellationToken cancellationToken)
    {
        var source = await FindHeaderAsync(guid);
        if (source is null)
        {
            return NotFound<PlanogramDetailDto>();
        }

        var errors = new PlanogramFieldErrors();
        var name = errors.RequiredText("name", "新方案名称", input.Name, 100);
        if (errors.Any)
        {
            return errors.ToFailure<PlanogramDetailDto>();
        }

        var sourceItems = await _db.Queryable<PlanogramItem>().Where(item => item.PlanogramGuid == guid).ToListAsync();
        var shelf = await _db.Queryable<PlanogramShelfModel>().FirstAsync(model => model.Guid == source.ShelfModelGuid);
        var conflictCount = 0;
        if (shelf is not null)
        {
            var lookups = await LoadLookupsAsync(sourceItems.Select(item => item.ProductCode), sourceItems.Select(item => item.HookModelGuid));
            conflictCount = Evaluate(source, shelf, sourceItems, lookups).Stats.ConflictCount;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var copy = new PlanogramHeader
        {
            Name = name,
            Notes = source.Notes,
            Tags = source.Tags,
            GapXMm = source.GapXMm,
            GapYMm = source.GapYMm,
            ShelfModelGuid = source.ShelfModelGuid,
            BayCount = source.BayCount,
            Status = PlanogramStatus.Draft,
            Version = 1,
            ConflictCount = conflictCount,
            LastValidatedAt = now,
            CreatedAt = now,
            CreatedBy = actor,
            UpdatedAt = now,
            UpdatedBy = actor,
        };
        var copiedItems = sourceItems.Select(item => new PlanogramItem
        {
            // guid 全新；其余原样复制（含保存时的尺寸快照：复制不应抹掉「参数已变」提示）。
            PlanogramGuid = copy.Guid,
            BayIndex = item.BayIndex,
            Face = item.Face,
            ItemType = item.ItemType,
            ProductCode = item.ProductCode,
            HookModelGuid = item.HookModelGuid,
            HoleCol = item.HoleCol,
            HoleRow = item.HoleRow,
            PerHookQty = item.PerHookQty,
            SpecSnapshotJson = item.SpecSnapshotJson,
            SortOrder = item.SortOrder,
        }).ToList();

        await InsertNewPlanAsync(copy, copiedItems);
        return await GetAsync(copy.Guid, cancellationToken);
    }

    /// <summary>新方案（头 + 明细）同一事务写入：复制与导入共用。</summary>
    private async Task InsertNewPlanAsync(PlanogramHeader header, List<PlanogramItem> items)
    {
        await _db.Ado.BeginTranAsync();
        try
        {
            await _db.Insertable(header).ExecuteCommandAsync();
            if (items.Count > 0)
            {
                // 每批 100 行 × 12 列 < SQL Server 2100 参数上限。
                await _db.Insertable(items).PageSize(100).ExecuteCommandAsync();
            }

            await _db.Ado.CommitTranAsync();
        }
        catch
        {
            await _db.Ado.RollbackTranAsync();
            throw;
        }
    }

    // ================================================================ 归档 / 恢复 / 删除

    public Task<ApiResponse<PlanogramListItemDto>> ArchiveAsync(string guid, string actor, CancellationToken cancellationToken) =>
        ChangeStatusAsync(guid, actor, archive: true);

    public Task<ApiResponse<PlanogramListItemDto>> RestoreAsync(string guid, string actor, CancellationToken cancellationToken) =>
        ChangeStatusAsync(guid, actor, archive: false);

    /// <summary>
    /// 归档：任意非归档状态 → 已归档；恢复：已归档 → 草稿。重复操作是幂等的空操作（不改 rowVersion）。
    /// 恢复一个发布过的方案时，若版本号没有高于最近发布版本，则 +1：保证「草稿的版本号 &gt; 已发布版本号」，
    /// 下次发布不会撞上快照表 (PlanogramGuid, Version) 的唯一索引。
    /// </summary>
    private async Task<ApiResponse<PlanogramListItemDto>> ChangeStatusAsync(string guid, string actor, bool archive)
    {
        var header = await FindHeaderAsync(guid);
        if (header is null)
        {
            return NotFound<PlanogramListItemDto>();
        }

        var alreadyThere = archive
            ? header.Status == PlanogramStatus.Archived
            : header.Status != PlanogramStatus.Archived;
        if (!alreadyThere)
        {
            var previousUpdatedAt = header.UpdatedAt;
            header.Status = archive ? (byte)PlanogramStatus.Archived : (byte)PlanogramStatus.Draft;
            if (!archive && header.PublishedVersion is { } published && header.Version <= published)
            {
                header.Version = published + 1;
            }

            header.UpdatedAt = NextUpdatedAt(previousUpdatedAt);
            header.UpdatedBy = actor;
            var update = _db.Updateable(header).UpdateColumns(it => new { it.Status, it.Version, it.UpdatedAt, it.UpdatedBy });
            var affected = previousUpdatedAt is { } previous
                ? await WhereVersionUnchanged(update, guid, previous).ExecuteCommandAsync()
                : await update.Where(it => it.Guid == guid).ExecuteCommandAsync();
            if (affected == 0)
            {
                return StaleFailure<PlanogramListItemDto>();
            }
        }

        var fresh = await FindHeaderAsync(guid);
        if (fresh is null)
        {
            return NotFound<PlanogramListItemDto>();
        }

        var rows = await BuildListItemsAsync(new List<PlanogramHeader> { fresh });
        return ApiResponse<PlanogramListItemDto>.OK(rows[0], archive ? "已归档" : "已恢复为草稿");
    }

    public async Task<ApiResponse<object>> DeleteAsync(string guid, CancellationToken cancellationToken)
    {
        var header = await FindHeaderAsync(guid);
        if (header is null)
        {
            return NotFound<object>();
        }

        // 仅「草稿且从未发布」可删：发布过的（哪怕现在回到草稿）有历史快照，只能归档。
        var neverPublished = header.PublishedVersion is null && header.PublishedAt is null;
        var hasSnapshots = await _db.Queryable<PlanogramVersion>().Where(version => version.PlanogramGuid == guid).AnyAsync();
        if (header.Status != PlanogramStatus.Draft || !neverPublished || hasSnapshots)
        {
            return ApiResponse<object>.Error("只有草稿且从未发布过的方案才能删除，其它方案请归档", PlanogramErrorCodes.NotDeletable);
        }

        // 加锁顺序统一为「先方案头、后明细」：保存 / 发布都是先改头再碰明细，删除若反过来先删明细，
        // 与它们并发时会形成死锁环（SQL Server 杀掉一方报 1205）。
        // 头的删除再带一遍「仍是草稿且从未发布」的条件：上面的检查与这里之间若有人刚好发布了，
        // 删除会等发布提交后发现条件不成立（0 行），整体回滚报 NOT_DELETABLE，不会把刚发布的方案连同明细删掉。
        var draft = (byte)PlanogramStatus.Draft;
        await _db.Ado.BeginTranAsync();
        try
        {
            var deleted = await _db.Deleteable<PlanogramHeader>()
                .Where(plan => plan.Guid == header.Guid && plan.Status == draft && plan.PublishedVersion == null && plan.PublishedAt == null)
                .ExecuteCommandAsync();
            if (deleted == 0)
            {
                await _db.Ado.RollbackTranAsync();
                return ApiResponse<object>.Error("只有草稿且从未发布过的方案才能删除，其它方案请归档", PlanogramErrorCodes.NotDeletable);
            }

            await _db.Deleteable<PlanogramItem>().Where(item => item.PlanogramGuid == header.Guid).ExecuteCommandAsync();
            await _db.Ado.CommitTranAsync();
        }
        catch
        {
            await _db.Ado.RollbackTranAsync();
            throw;
        }

        return ApiResponse<object>.CreateSuccess("已删除");
    }

    // ================================================================ 请求校验（保存 / 校验共用）

    private sealed record PreparedItem(
        string Guid, int BayIndex, string ProductCode, string HookModelGuid, int HoleCol, int HoleRow, int? PerHookQty, int SortOrder);

    private sealed record PreparedPlan(
        string Name, string? Notes, PlanogramShelfModel Shelf, int BayCount, int GapXMm, int GapYMm, List<PreparedItem> Items);

    private sealed record PrepareOutcome(PlanogramFieldErrors Errors, PreparedPlan? Plan);

    /// <summary>
    /// 校验并归一化请求：节数 1–12、间距 0–200、货架存在（换成别的货架时必须启用）、
    /// 明细 ≤ 2000 条且 productCode 必须存在于 Product、hookModelGuid 必须存在、guid 不重复也不能是别的方案的明细。
    /// 节号 / 孔位越界不在这里拒绝，而是由几何校验作为冲突返回（减少节数、换货架后要能带着红标继续保存）。
    /// </summary>
    private async Task<PrepareOutcome> PrepareAsync(PlanogramSaveRequest input, string planGuid, string? currentShelfGuid, bool requireName)
    {
        var errors = new PlanogramFieldErrors();
        var name = string.Empty;
        string? notes = null;
        if (requireName)
        {
            name = errors.RequiredText("name", "方案名称", input.Name, 100);
            notes = errors.OptionalText("notes", "备注", input.Notes, 500);
        }

        errors.IntRange("bayCount", "节数", input.BayCount, 1, MaxBayCount);
        var gapX = RequiredGap(errors, "gapXMm", "左右间距", input.GapXMm);
        var gapY = RequiredGap(errors, "gapYMm", "上下间距", input.GapYMm);
        var shelf = await ResolveShelfAsync(input.ShelfModelGuid, currentShelfGuid, errors);
        var items = await PrepareItemsAsync(input.Items, planGuid, errors);
        if (errors.Any || shelf is null)
        {
            return new PrepareOutcome(errors, null);
        }

        return new PrepareOutcome(errors, new PreparedPlan(name, notes, shelf, input.BayCount, gapX, gapY, items));
    }

    private static int RequiredGap(PlanogramFieldErrors errors, string field, string label, int? value)
    {
        if (value is null)
        {
            errors.Add(field, $"{label}不能为空");
            return DefaultGapMm;
        }

        errors.IntRange(field, label, value.Value, 0, MaxGapMm);
        return value.Value;
    }

    /// <summary>货架型号必须存在；与当前方案所用型号不同时（新建，或换货架）还必须是启用状态。</summary>
    private async Task<PlanogramShelfModel?> ResolveShelfAsync(string? shelfModelGuid, string? currentShelfGuid, PlanogramFieldErrors errors)
    {
        var guid = (shelfModelGuid ?? string.Empty).Trim();
        if (guid.Length == 0)
        {
            errors.Add("shelfModelGuid", "货架型号不能为空");
            return null;
        }

        var shelf = await _db.Queryable<PlanogramShelfModel>().FirstAsync(model => model.Guid == guid);
        if (shelf is null)
        {
            errors.Add("shelfModelGuid", "货架型号不存在");
            return null;
        }

        var keepingCurrent = currentShelfGuid is not null && string.Equals(currentShelfGuid, shelf.Guid, StringComparison.Ordinal);
        if (!shelf.IsActive && !keepingCurrent)
        {
            errors.Add("shelfModelGuid", "货架型号已停用，不能用于新建方案或切换到它");
            return null;
        }

        return shelf;
    }

    private async Task<List<PreparedItem>> PrepareItemsAsync(List<PlanogramItemDto>? items, string planGuid, PlanogramFieldErrors errors)
    {
        var prepared = new List<PreparedItem>();
        if (items is null)
        {
            errors.Add("items", "明细 items 不能缺省（没有明细请传空数组）");
            return prepared;
        }

        if (items.Count > MaxItems)
        {
            errors.Add("items", $"一个方案最多 {MaxItems} 条明细，当前 {items.Count} 条");
            return prepared;
        }

        var reported = 0;
        void ItemError(int index, string field, string message)
        {
            if (reported++ < MaxReportedItemErrors)
            {
                errors.Add($"items[{index}].{field}", $"第 {index + 1} 条明细：{message}");
            }
        }

        var seenGuids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < items.Count; index++)
        {
            var dto = items[index];
            if (dto is null)
            {
                ItemError(index, "guid", "明细不能为空");
                continue;
            }

            var itemGuid = (dto.Guid ?? string.Empty).Trim();
            if (itemGuid.Length == 0)
            {
                itemGuid = UuidHelper.GenerateUuid7();
            }
            else if (itemGuid.Length > 50)
            {
                ItemError(index, "guid", "guid 不能超过 50 个字符");
            }

            if (!seenGuids.Add(itemGuid))
            {
                ItemError(index, "guid", $"guid「{itemGuid}」重复");
            }

            if (dto.Face != 0)
            {
                ItemError(index, "face", "face 目前只能是 0（单面）");
            }

            if (dto.ItemType != 1)
            {
                ItemError(index, "itemType", "itemType 目前只能是 1（挂钩商品）");
            }

            if (Math.Abs((long)dto.BayIndex) > MaxCoordinate)
            {
                ItemError(index, "bayIndex", "节号超出合理范围");
            }

            if (Math.Abs((long)dto.HoleCol) > MaxCoordinate)
            {
                ItemError(index, "holeCol", "孔位列超出合理范围");
            }

            if (Math.Abs((long)dto.HoleRow) > MaxCoordinate)
            {
                ItemError(index, "holeRow", "孔位行超出合理范围");
            }

            var productCode = (dto.ProductCode ?? string.Empty).Trim();
            if (productCode.Length == 0)
            {
                ItemError(index, "productCode", "商品货号不能为空");
            }
            else if (productCode.Length > 50)
            {
                ItemError(index, "productCode", "商品货号不能超过 50 个字符");
            }

            var hookGuid = (dto.HookModelGuid ?? string.Empty).Trim();
            if (hookGuid.Length == 0)
            {
                ItemError(index, "hookModelGuid", "挂钩型号不能为空");
            }
            else if (hookGuid.Length > 50)
            {
                ItemError(index, "hookModelGuid", "挂钩型号 Guid 不能超过 50 个字符");
            }

            if (dto.PerHookQty is { } qty && (qty < 1 || qty > MaxPerHookQty))
            {
                ItemError(index, "perHookQty", $"每钩挂数必须在 1～{MaxPerHookQty} 之间（留空表示按规则）");
            }

            prepared.Add(new PreparedItem(
                itemGuid, dto.BayIndex, productCode, hookGuid, dto.HoleCol, dto.HoleRow, dto.PerHookQty, dto.SortOrder));
        }

        if (reported > MaxReportedItemErrors)
        {
            errors.Add("items", $"另有 {reported - MaxReportedItemErrors} 处明细错误未列出");
        }

        if (reported > 0)
        {
            // 明细自身的结构性错误先报完，再去查库，避免对垃圾输入做大量查询。
            return prepared;
        }

        // 商品必须存在于 Product（不存在直接报校验错误）；货号以主档里的写法为准（SQL Server 不区分大小写）。
        var products = await QueryChunkedAsync(
            prepared.Select(item => item.ProductCode).ToList(),
            async chunk => await _db.Queryable<Product>().Where(product => chunk.Contains(product.ProductCode!)).Select(product => product.ProductCode!).ToListAsync());
        var canonicalProducts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in products)
        {
            canonicalProducts.TryAdd(code, code);
        }

        var hooks = await QueryChunkedAsync(
            prepared.Select(item => item.HookModelGuid).ToList(),
            async chunk => await _db.Queryable<PlanogramHookModel>().Where(hook => chunk.Contains(hook.Guid)).Select(hook => hook.Guid).ToListAsync());
        var canonicalHooks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hookGuid in hooks)
        {
            canonicalHooks.TryAdd(hookGuid, hookGuid);
        }

        var reportedProducts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reportedHooks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < prepared.Count; index++)
        {
            var item = prepared[index];
            if (canonicalProducts.TryGetValue(item.ProductCode, out var canonicalCode))
            {
                item = item with { ProductCode = canonicalCode };
            }
            else if (reportedProducts.Add(item.ProductCode))
            {
                ItemError(index, "productCode", $"商品「{item.ProductCode}」不存在");
            }

            if (canonicalHooks.TryGetValue(item.HookModelGuid, out var canonicalHook))
            {
                item = item with { HookModelGuid = canonicalHook };
            }
            else if (reportedHooks.Add(item.HookModelGuid))
            {
                ItemError(index, "hookModelGuid", $"挂钩型号「{item.HookModelGuid}」不存在");
            }

            prepared[index] = item;
        }

        // 明细 guid 是主键：不能撞上别的方案的明细（本方案自己的旧明细会被整体替换，不算）。
        var taken = await QueryChunkedAsync(
            prepared.Select(item => item.Guid).ToList(),
            async chunk => await _db.Queryable<PlanogramItem>()
                .Where(item => chunk.Contains(item.Guid) && item.PlanogramGuid != planGuid)
                .Select(item => item.Guid)
                .ToListAsync());
        if (taken.Count > 0)
        {
            errors.Add("items", $"明细 guid「{taken[0]}」已被其它方案使用");
        }

        if (reported > MaxReportedItemErrors)
        {
            errors.Add("items", $"另有 {reported - MaxReportedItemErrors} 处明细错误未列出");
        }

        return prepared;
    }

    // ================================================================ 校验输入 / 实体映射

    private static LayoutShelfInput ToShelfInput(PlanogramShelfModel shelf) => new(
        shelf.BayWidthMm, shelf.TotalHeightMm, shelf.UprightWidthMm, shelf.BaseTopHeightMm,
        shelf.HolePitchXMm, shelf.HolePitchYMm, shelf.FirstHoleLeftMm, shelf.FirstHoleTopMm,
        shelf.HoleCols, shelf.HoleRows);

    private static LayoutItemInput ToLayoutItem(PreparedItem item) => new(
        item.Guid, item.BayIndex, item.ProductCode, item.HookModelGuid, item.HoleCol, item.HoleRow, item.PerHookQty);

    private static LayoutItemInput ToLayoutItem(PlanogramItem item) => new(
        item.Guid, item.BayIndex, item.ProductCode, item.HookModelGuid, item.HoleCol, item.HoleRow, item.PerHookQty);

    /// <summary>保存时的商品尺寸快照：宽 / 高 / 厚 + 吊孔偏移（吊孔偏移也决定商品在钩上的位置）。</summary>
    private sealed record SpecSnapshot(int WidthMm, int HeightMm, int DepthMm, int HangHoleOffsetMm);

    private static PlanogramItem ToEntity(string planGuid, PreparedItem item, LayoutLookups lookups)
    {
        string? snapshot = null;
        if (lookups.Specs.TryGetValue(item.ProductCode, out var spec))
        {
            snapshot = JsonSerializer.Serialize(
                new SpecSnapshot(spec.WidthMm, spec.HeightMm, spec.DepthMm, spec.HangHoleOffsetMm), SnapshotJson);
        }

        return new PlanogramItem
        {
            Guid = item.Guid,
            PlanogramGuid = planGuid,
            BayIndex = item.BayIndex,
            Face = 0,
            ItemType = 1,
            ProductCode = item.ProductCode,
            HookModelGuid = item.HookModelGuid,
            HoleCol = item.HoleCol,
            HoleRow = item.HoleRow,
            PerHookQty = item.PerHookQty,
            SpecSnapshotJson = snapshot,
            SortOrder = item.SortOrder,
        };
    }

    /// <summary>快照与当前参数不一致（含参数已被清除）→ 「参数已变」。没有快照（保存时就缺参数）不算变。</summary>
    private static bool IsSpecChanged(PlanogramItem item, LayoutLookups lookups)
    {
        if (string.IsNullOrWhiteSpace(item.SpecSnapshotJson))
        {
            return false;
        }

        SpecSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<SpecSnapshot>(item.SpecSnapshotJson, SnapshotJson);
        }
        catch (JsonException)
        {
            return false;
        }

        if (snapshot is null)
        {
            return false;
        }

        if (!lookups.Specs.TryGetValue(item.ProductCode, out var spec))
        {
            return true;
        }

        return snapshot.WidthMm != spec.WidthMm
            || snapshot.HeightMm != spec.HeightMm
            || snapshot.DepthMm != spec.DepthMm
            || snapshot.HangHoleOffsetMm != spec.HangHoleOffsetMm;
    }

    // ================================================================ 查询辅助

    /// <summary>商品参数 / 挂钩型号 / 零售价三张查询表（按需批量加载，键不区分大小写）。</summary>
    private sealed class LayoutLookups
    {
        public Dictionary<string, ProductDisplaySpec> Specs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PlanogramHookModel> Hooks { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, decimal> Prices { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, LayoutHookInput> HookInputs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, LayoutSpecInput> SpecInputs { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<LayoutLookups> LoadLookupsAsync(IEnumerable<string> productCodes, IEnumerable<string> hookGuids)
    {
        var lookups = new LayoutLookups();
        var codes = productCodes.Distinct(StringComparer.Ordinal).ToList();
        var guids = hookGuids.Distinct(StringComparer.Ordinal).ToList();

        var specRows = await QueryChunkedAsync(codes,
            async chunk => await _db.Queryable<ProductDisplaySpec>().Where(spec => chunk.Contains(spec.ProductCode)).ToListAsync());
        foreach (var spec in specRows)
        {
            lookups.Specs[spec.ProductCode] = spec;
        }

        var hookRows = await QueryChunkedAsync(guids,
            async chunk => await _db.Queryable<PlanogramHookModel>().Where(hook => chunk.Contains(hook.Guid)).ToListAsync());
        foreach (var hook in hookRows)
        {
            lookups.Hooks[hook.Guid] = hook;
        }

        // 零售价只取两列，避免把商品主档的宽行整个读出来。
        var priceRows = await QueryChunkedAsync(codes,
            async chunk => await _db.Queryable<Product>()
                .Where(product => chunk.Contains(product.ProductCode!))
                .Select(product => new Product { ProductCode = product.ProductCode, RetailPrice = product.RetailPrice })
                .ToListAsync());
        foreach (var row in priceRows)
        {
            lookups.Prices.TryAdd(row.ProductCode!, row.RetailPrice ?? 0m);
        }

        foreach (var (guid, hook) in lookups.Hooks)
        {
            lookups.HookInputs[guid] = new LayoutHookInput(
                hook.HasPriceTagArm, hook.ArmLengthMm, hook.DropMm, hook.TipReserveMm, hook.TagWidthMm, hook.TagHeightMm,
                hook.TagBottomAboveArmMm, hook.HoleSpanX, hook.HoleSpanY, hook.LoadKg);
        }

        foreach (var (code, spec) in lookups.Specs)
        {
            lookups.SpecInputs[code] = new LayoutSpecInput(
                spec.WidthMm, spec.HeightMm, spec.DepthMm, spec.HangHoleOffsetMm, spec.UnitWeightG,
                ProductDisplaySpecService.ParseOverrides(spec.HookQtyOverridesJson),
                lookups.Prices.GetValueOrDefault(code));
        }

        return lookups;
    }

    /// <summary>按「已校验归一化的请求」算冲突与统计（保存 / 校验 / 导入共用）。</summary>
    private static PlanogramLayoutResult Evaluate(PreparedPlan plan, LayoutLookups lookups) =>
        PlanogramLayoutValidator.Validate(
            ToShelfInput(plan.Shelf), lookups.HookInputs, lookups.SpecInputs,
            plan.Items.Select(ToLayoutItem).ToList(), plan.BayCount, new LayoutGaps(plan.GapXMm, plan.GapYMm));

    private static PlanogramLayoutResult Evaluate(
        PlanogramHeader header, PlanogramShelfModel shelf, IReadOnlyList<PlanogramItem> items, LayoutLookups lookups) =>
        PlanogramLayoutValidator.Validate(
            ToShelfInput(shelf), lookups.HookInputs, lookups.SpecInputs,
            items.Select(ToLayoutItem).ToList(), header.BayCount, new LayoutGaps(header.GapXMm, header.GapYMm));

    private static async Task<List<T>> QueryChunkedAsync<T>(IReadOnlyCollection<string> keys, Func<List<string>, Task<List<T>>> query)
    {
        var result = new List<T>();
        foreach (var chunk in keys.Distinct(StringComparer.Ordinal).Chunk(InClauseChunk))
        {
            result.AddRange(await query(chunk.ToList()));
        }

        return result;
    }

    private async Task<PlanogramHeader?> FindHeaderAsync(string guid)
    {
        var key = (guid ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            return null;
        }

        return await _db.Queryable<PlanogramHeader>().FirstAsync(plan => plan.Guid == key && plan.IsDeleted == false);
    }

    private async Task<List<PlanogramItem>> LoadItemsAsync(string planGuid)
    {
        var items = await _db.Queryable<PlanogramItem>().Where(item => item.PlanogramGuid == planGuid).ToListAsync();
        return OrderItems(items);
    }

    /// <summary>明细的稳定顺序：排序号 → 节 → 行 → 列 → guid。</summary>
    private static List<PlanogramItem> OrderItems(IEnumerable<PlanogramItem> items) =>
        items.OrderBy(item => item.SortOrder)
            .ThenBy(item => item.BayIndex)
            .ThenBy(item => item.HoleRow)
            .ThenBy(item => item.HoleCol)
            .ThenBy(item => item.Guid, StringComparer.Ordinal)
            .ToList();

    // ================================================================ 详情 / 列表行构建

    /// <summary>读取时复算冲突与统计、比对尺寸快照；货架型号查不到（直连库删除等异常）返回 null。</summary>
    private async Task<PlanogramDetailDto?> BuildDetailAsync(PlanogramHeader header)
    {
        var shelfDto = await _models.GetShelfModelDtoAsync(header.ShelfModelGuid);
        var shelf = await _db.Queryable<PlanogramShelfModel>().FirstAsync(model => model.Guid == header.ShelfModelGuid);
        if (shelfDto is null || shelf is null)
        {
            return null;
        }

        var items = await LoadItemsAsync(header.Guid);
        var lookups = await LoadLookupsAsync(items.Select(item => item.ProductCode), items.Select(item => item.HookModelGuid));
        var layout = Evaluate(header, shelf, items, lookups);

        var itemDtos = items.Select(item => new PlanogramItemDto
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
            SpecChanged = IsSpecChanged(item, lookups),
        }).ToList();

        return new PlanogramDetailDto
        {
            Header = ToListItem(header, shelf.Name, layout.Stats),
            RowVersion = ToRowVersion(header.UpdatedAt),
            Items = itemDtos,
            ShelfModel = shelfDto,
            HookModels = await _models.GetHookModelDtosForPlanAsync(items.Select(item => item.HookModelGuid).ToList()),
            ProductSpecs = await _specs.GetRowsByCodesAsync(items.Select(item => item.ProductCode).ToList()),
            Conflicts = layout.Conflicts,
            Stats = layout.Stats,
        };
    }

    /// <summary>
    /// 列表行：统计字段（条数 / 款数 / 件数 / 占板率 / 冲突数）按当前数据现算，不依赖保存时的快照 ——
    /// 商品参数改了之后列表上的冲突数也是准的。表里没有这些统计列，所以不需要为列表加迁移。
    /// </summary>
    private async Task<List<PlanogramListItemDto>> BuildListItemsAsync(List<PlanogramHeader> headers)
    {
        if (headers.Count == 0)
        {
            return new List<PlanogramListItemDto>();
        }

        var planGuids = headers.Select(plan => plan.Guid).ToList();
        var allItems = await QueryChunkedAsync(planGuids,
            async chunk => await _db.Queryable<PlanogramItem>().Where(item => chunk.Contains(item.PlanogramGuid)).ToListAsync());
        var itemsByPlan = allItems
            .GroupBy(item => item.PlanogramGuid, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => OrderItems(group), StringComparer.Ordinal);

        var shelfRows = await QueryChunkedAsync(headers.Select(plan => plan.ShelfModelGuid).ToList(),
            async chunk => await _db.Queryable<PlanogramShelfModel>().Where(model => chunk.Contains(model.Guid)).ToListAsync());
        var shelves = shelfRows.ToDictionary(shelf => shelf.Guid, StringComparer.Ordinal);

        var lookups = await LoadLookupsAsync(allItems.Select(item => item.ProductCode), allItems.Select(item => item.HookModelGuid));

        return headers.Select(header =>
        {
            var items = itemsByPlan.GetValueOrDefault(header.Guid) ?? new List<PlanogramItem>();
            if (!shelves.TryGetValue(header.ShelfModelGuid, out var shelf))
            {
                // 货架型号查不到（直连库删除等异常）：只给能确定的计数，冲突数退回保存时的值。
                return ToListItem(header, string.Empty, new PlanogramStatsDto
                {
                    ItemCount = items.Count,
                    SkuCount = items.Select(item => item.ProductCode).Distinct(StringComparer.Ordinal).Count(),
                    ConflictCount = header.ConflictCount,
                });
            }

            return ToListItem(header, shelf.Name, Evaluate(header, shelf, items, lookups).Stats);
        }).ToList();
    }

    private static PlanogramListItemDto ToListItem(PlanogramHeader header, string shelfName, PlanogramStatsDto stats) => new()
    {
        Guid = header.Guid,
        Name = header.Name,
        Notes = header.Notes,
        ShelfModelGuid = header.ShelfModelGuid,
        ShelfModelName = shelfName,
        BayCount = header.BayCount,
        GapXMm = header.GapXMm,
        GapYMm = header.GapYMm,
        Status = header.Status,
        Version = header.Version,
        PublishedVersion = header.PublishedVersion,
        PublishedAt = header.PublishedAt,
        ItemCount = stats.ItemCount,
        SkuCount = stats.SkuCount,
        UnitCount = stats.UnitCount,
        FillPercent = stats.FillPercent,
        ConflictCount = stats.ConflictCount,
        HasUnpublishedChanges = header.Status == PlanogramStatus.Draft && header.PublishedVersion is not null,
        UpdatedAt = header.UpdatedAt,
        UpdatedBy = header.UpdatedBy,
    };

    // ================================================================ rowVersion / 通用失败

    /// <summary>
    /// 乐观并发令牌：updatedAt 的 ISO 8601 往返格式（7 位小数 = .NET 刻度，如 2026-11-04T03:00:00.1234567Z）。
    /// 每次写入都会让 updatedAt 变大，所以令牌随之变化；前端把它当不透明字符串原样带回即可。
    /// </summary>
    internal static string ToRowVersion(DateTime? updatedAt) =>
        DateTime.SpecifyKind(updatedAt ?? DateTime.MinValue, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    private static bool RowVersionMatches(string? token, DateTime? updatedAt) =>
        string.Equals(token?.Trim(), ToRowVersion(updatedAt), StringComparison.Ordinal);

    /// <summary>
    /// 条件更新的「版本没变」条件：Guid 相同且 UpdatedAt 落在读取时的值 ±<see cref="VersionWindow"/> 内（原因见字段注释）。
    /// </summary>
    private static IUpdateable<PlanogramHeader> WhereVersionUnchanged(IUpdateable<PlanogramHeader> update, string guid, DateTime previous)
    {
        var from = previous - VersionWindow;
        var to = previous + VersionWindow;
        return update.Where(it => it.Guid == guid && it.UpdatedAt >= from && it.UpdatedAt <= to);
    }

    /// <summary>新的 UpdatedAt：当前时间，但至少比旧值大一个步长（时钟回拨 / 同一瞬间连续保存时 rowVersion 也必须变）。</summary>
    private DateTime NextUpdatedAt(DateTime? previous)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return previous is { } before && now <= before + MinUpdateStep ? before + MinUpdateStep : now;
    }

    private static ApiResponse<T> NotFound<T>() =>
        ApiResponse<T>.Error("陈列方案不存在", PlanogramErrorCodes.NotFound);

    private static ApiResponse<T> StaleFailure<T>() =>
        ApiResponse<T>.Error("方案已被其他人修改，请刷新后再保存", PlanogramErrorCodes.Stale);
}
