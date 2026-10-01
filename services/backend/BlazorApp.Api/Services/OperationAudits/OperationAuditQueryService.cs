using System.Security.Claims;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services.OperationAudits.Risk;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.POSM;
using Microsoft.AspNetCore.Http;
using SqlSugar;

namespace BlazorApp.Api.Services.OperationAudits;

public enum OperationAuditDetailAccessStatus
{
    Found,
    NotFound,
    Forbidden,
}

public sealed class OperationAuditDetailQueryResult
{
    public OperationAuditDetailAccessStatus Status { get; init; }

    public OperationAuditDetailDto? Data { get; init; }
}

public sealed record OperationAuditDetailQueryResult<T>(OperationAuditDetailAccessStatus Status, T? Data)
{
    public static OperationAuditDetailQueryResult<T> Found(T data) => new(OperationAuditDetailAccessStatus.Found, data);

    public static OperationAuditDetailQueryResult<T> NotFound() => new(OperationAuditDetailAccessStatus.NotFound, default);

    public static OperationAuditDetailQueryResult<T> Forbidden() => new(OperationAuditDetailAccessStatus.Forbidden, default);
}

public sealed class OperationAuditQueryService
{
    private enum DeviceSystemFilter
    {
        Windows,
        IpadOs,
        Unknown,
        Invalid,
    }

    /// <summary>
    /// 过滤条件分组：列表套用全部；结果计数忽略结果类与风险入口；风险计数另外忽略操作类型；
    /// 按员工汇总只保留分店、时间、设备与关键字等基础条件。
    /// </summary>
    [Flags]
    private enum FilterScope
    {
        Base = 0,
        Outcome = 1,
        OperationType = 2,
        Risk = 4,
        Cashier = 8,
        All = Outcome | OperationType | Risk | Cashier,
    }

    /// <summary>详情前后窗口只允许 5 / 10 / 15 分钟，与老收银一致。</summary>
    private static readonly int[] AllowedContextWindows = [5, 10, 15];
    private const int ContextNeighborLimit = 200;

    /// <summary>SQL IN 列表分批大小，避免参数过多。</summary>
    private const int IdBatchSize = 500;

    private static readonly IReadOnlyList<string> AdminRoleAliases =
        Permissions.SuperAdminRoleNames;
    private static readonly string[] StoreManagerRoleAliases = ["StoreManager", "店长", "经理"];

    private readonly ISqlSugarClient _db;
    private readonly ICurrentUserManageableStoreScopeService _storeScopeService;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public OperationAuditQueryService(
        ISqlSugarClient db,
        ICurrentUserManageableStoreScopeService storeScopeService,
        IHttpContextAccessor httpContextAccessor
    )
    {
        _db = db;
        _storeScopeService = storeScopeService;
        _httpContextAccessor = httpContextAccessor;
    }

    public async Task<PagedListReactDto<OperationAuditListItemDto>> QueryAsync(
        OperationAuditQueryDto request,
        DateTime? utcNow = null
    )
    {
        var pageNumber = Math.Max(1, request.PageNumber);
        var pageSize = Math.Clamp(request.PageSize <= 0 ? 20 : request.PageSize, 1, 200);
        var empty = CreateEmptyPage(pageNumber, pageSize);
        var access = await ResolveAccessAsync();
        if (!access.IsAllowed)
        {
            return empty;
        }

        var now = DateTime.SpecifyKind(utcNow ?? DateTime.UtcNow, DateTimeKind.Utc);
        var query = BuildFilteredQuery(request, access, now, FilterScope.All);
        if (query == null)
        {
            return empty;
        }

        var total = await query.CountAsync();
        var rows = await ApplySort(query, request.SortBy, request.SortOrder)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var items = rows.Select(MapListItem).ToList();
        await EnrichAsync(items);
        return new PagedListReactDto<OperationAuditListItemDto>
        {
            Items = items,
            Total = total,
            PageNumber = pageNumber,
            PageSize = pageSize,
        };
    }

    /// <summary>
    /// 汇总计数：与列表共用门店权限与基础筛选，但忽略 Outcome / IsEmergencyOverride / IsOfflineCached，
    /// 因为移动端把这些计数当作“快捷过滤”入口，必须反映未按结果过滤前的基数。
    /// </summary>
    public async Task<OperationAuditSummaryDto> GetSummaryAsync(
        OperationAuditQueryDto request,
        DateTime? utcNow = null
    )
    {
        var summary = new OperationAuditSummaryDto();
        var access = await ResolveAccessAsync();
        if (!access.IsAllowed)
        {
            return summary;
        }

        var now = DateTime.SpecifyKind(utcNow ?? DateTime.UtcNow, DateTimeKind.Utc);
        // SqlSugar 的 Where 会就地修改查询对象，为避免各计数条件互相叠加，每次计数都重新构建基础查询。
        ISugarQueryable<PosOperationAudit>? Build() =>
            BuildFilteredQuery(request, access, now, FilterScope.OperationType | FilterScope.Cashier);

        var baseQuery = Build();
        if (baseQuery == null)
        {
            return summary;
        }

        summary.Total = await baseQuery.CountAsync();
        summary.Succeeded = await Build()!.Where(item => item.Outcome == "Succeeded").CountAsync();
        summary.Denied = await Build()!.Where(item => item.Outcome == "Denied").CountAsync();
        summary.Failed = await Build()!.Where(item => item.Outcome == "Failed").CountAsync();
        summary.EmergencyOverride = await Build()!.Where(item => item.IsEmergencyOverride).CountAsync();
        summary.OfflineCached = await Build()!.Where(item => item.IsOfflineCached).CountAsync();

        // 风险入口计数与老收银同口径：再忽略操作类型，避免「已选某个操作类型」时入口基数跟着变小。
        ISugarQueryable<PosOperationAudit>? BuildRisk() =>
            BuildFilteredQuery(request, access, now, FilterScope.Cashier);
        summary.DangerTotal = await ApplyDanger(BuildRisk()!).CountAsync();
        var abnormal = await LoadAbnormalAsync(BuildRisk()!);
        summary.AbnormalTotal = abnormal.Count;
        summary.PendingReview = abnormal.Count(item => item.Pending);
        summary.AbnormalEmployees = abnormal.Select(item => EmployeeKey(item.CashierId, item.CashierName)).Distinct().Count();
        summary.AbnormalByRule = CountByRule(abnormal);
        return summary;
    }

    /// <summary>
    /// 按员工（收银员编号）汇总：操作数、危险数、异常与待核查数、金额让利。
    /// 只套用分店、时间、设备与关键字等基础条件；收银员、操作类型、结果类与风险入口条件不参与。
    /// </summary>
    public async Task<OperationAuditEmployeeSummaryResultDto> GetEmployeeSummaryAsync(
        OperationAuditQueryDto request,
        DateTime? utcNow = null
    )
    {
        var result = new OperationAuditEmployeeSummaryResultDto();
        var access = await ResolveAccessAsync();
        if (!access.IsAllowed)
        {
            return result;
        }

        var now = DateTime.SpecifyKind(utcNow ?? DateTime.UtcNow, DateTimeKind.Utc);
        ISugarQueryable<PosOperationAudit>? Build() => BuildFilteredQuery(request, access, now, FilterScope.Base);
        if (Build() == null)
        {
            return result;
        }

        // 分组只回小结果：收银员 × 分店 × 设备 × 操作类型 的计数。
        var groups = await Build()!
            .GroupBy(item => new { item.CashierId, item.StoreCode, item.DeviceCode, item.OperationType })
            .Select(item => new
            {
                item.CashierId,
                item.StoreCode,
                item.DeviceCode,
                item.OperationType,
                Count = SqlFunc.AggregateCount(item.EventId),
            })
            .ToListAsync();
        var names = await Build()!
            .GroupBy(item => item.CashierId)
            .Select(item => new { item.CashierId, CashierName = SqlFunc.AggregateMax(item.CashierName) })
            .ToListAsync();
        var manualDrawers = await Build()!
            .Where(item => item.OperationType == PosOperationAuditRiskCatalog.CashDrawerOpen && (item.OrderGuid == null || item.OrderGuid == ""))
            .GroupBy(item => item.CashierId)
            .Select(item => new { item.CashierId, Count = SqlFunc.AggregateCount(item.EventId) })
            .ToListAsync();
        // 金额让利只看会改变应收的危险操作，每店每天几十条，直接取行在内存里算。
        var amountTypes = PosOperationAuditRiskCatalog.CartAmountOperations
            .Append(PosOperationAuditRiskCatalog.ReturnRefundComplete)
            .ToArray();
        var amountRows = await Build()!
            .Where(item => amountTypes.Contains(item.OperationType))
            .Select(item => new
            {
                item.CashierId,
                item.OperationType,
                item.Outcome,
                item.BeforeActual,
                item.AfterActual,
                item.PaymentAmount,
            })
            .ToListAsync();
        var abnormal = await LoadAbnormalAsync(Build()!);

        var dangerTypes = new HashSet<string>(PosOperationAuditRiskCatalog.DangerOperations, StringComparer.Ordinal);
        var nameMap = names.ToDictionary(item => item.CashierId ?? string.Empty, item => item.CashierName);
        var drawerMap = manualDrawers.ToDictionary(item => item.CashierId ?? string.Empty, item => item.Count);
        var employees = groups
            .GroupBy(item => item.CashierId ?? string.Empty)
            .Select(group =>
            {
                var cashierId = group.Key;
                var flagged = abnormal.Where(item => (item.CashierId ?? string.Empty) == cashierId).ToList();
                return new OperationAuditEmployeeSummaryDto
                {
                    CashierId = string.IsNullOrEmpty(cashierId) ? null : cashierId,
                    CashierName = nameMap.GetValueOrDefault(cashierId),
                    StoreCodes = group.Select(item => item.StoreCode).Where(code => !string.IsNullOrWhiteSpace(code))
                        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList(),
                    DeviceCodes = group.Select(item => item.DeviceCode).Where(code => !string.IsNullOrWhiteSpace(code))
                        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList(),
                    Total = group.Sum(item => item.Count),
                    DangerCount = group.Where(item => dangerTypes.Contains(item.OperationType)).Sum(item => item.Count)
                        + drawerMap.GetValueOrDefault(cashierId),
                    AbnormalCount = flagged.Count,
                    PendingReview = flagged.Count(item => item.Pending),
                    AbnormalByRule = CountByRule(flagged),
                    AmountImpact = amountRows
                        .Where(item => (item.CashierId ?? string.Empty) == cashierId)
                        .Sum(item => PosOperationAuditRiskCatalog.AmountImpact(
                            item.OperationType, item.Outcome, item.BeforeActual, item.AfterActual, item.PaymentAmount) ?? 0m),
                };
            })
            // 待核查多的排前面，其次异常数、危险数，最后按操作数。
            .OrderByDescending(item => item.PendingReview)
            .ThenByDescending(item => item.AbnormalCount)
            .ThenByDescending(item => item.DangerCount)
            .ThenByDescending(item => item.Total)
            .ThenBy(item => item.CashierName, StringComparer.Ordinal)
            .ToList();
        result.Employees = employees;
        result.Total = employees.Sum(item => item.Total);
        result.DangerTotal = employees.Sum(item => item.DangerCount);
        return result;
    }

    /// <summary>单条事件及同店同设备前后若干分钟（默认 5，可选 10、15）内的操作，按时间升序。</summary>
    public async Task<OperationAuditDetailQueryResult<OperationAuditContextDto>> GetContextAsync(Guid eventId, int? windowMinutes)
    {
        var window = windowMinutes is { } requested && AllowedContextWindows.Contains(requested) ? requested : AllowedContextWindows[0];
        var row = await _db.Queryable<PosOperationAudit>().FirstAsync(item => item.EventId == eventId);
        if (row == null)
        {
            return OperationAuditDetailQueryResult<OperationAuditContextDto>.NotFound();
        }
        var access = await ResolveAccessAsync();
        if (!access.CanAccess(row.StoreCode))
        {
            return OperationAuditDetailQueryResult<OperationAuditContextDto>.Forbidden();
        }

        var from = row.OccurredAtUtc.AddMinutes(-window);
        var to = row.OccurredAtUtc.AddMinutes(window);
        var neighbors = await _db.Queryable<PosOperationAudit>()
            .Where(item => item.StoreCode == row.StoreCode && item.DeviceCode == row.DeviceCode
                && item.OccurredAtUtc >= from && item.OccurredAtUtc <= to)
            .OrderBy(item => item.OccurredAtUtc)
            .OrderBy(item => item.EventId)
            .Take(ContextNeighborLimit + 1)
            .ToListAsync();
        var truncated = neighbors.Count > ContextNeighborLimit;
        var items = neighbors.Take(ContextNeighborLimit).Select(MapListItem).ToList();
        if (items.All(item => item.EventId != eventId))
        {
            // 截断时目标可能不在前 200 条里，补回并保持时间顺序。
            items.Add(MapListItem(row));
            items = items.OrderBy(item => item.OccurredAtUtc).ThenBy(item => item.EventId).ToList();
        }
        await EnrichAsync(items);
        return OperationAuditDetailQueryResult<OperationAuditContextDto>.Found(new OperationAuditContextDto
        {
            Target = items.First(item => item.EventId == eventId),
            WindowMinutes = window,
            Neighbors = items,
            Truncated = truncated,
        });
    }

    /// <summary>核查前的访问判断：事件存在且在当前账号的分店范围内。</summary>
    internal async Task<(OperationAuditDetailAccessStatus Status, PosOperationAudit? Row)> FindAccessibleAsync(Guid eventId)
    {
        var row = await _db.Queryable<PosOperationAudit>().FirstAsync(item => item.EventId == eventId);
        if (row == null)
        {
            return (OperationAuditDetailAccessStatus.NotFound, null);
        }
        var access = await ResolveAccessAsync();
        return access.CanAccess(row.StoreCode)
            ? (OperationAuditDetailAccessStatus.Found, row)
            : (OperationAuditDetailAccessStatus.Forbidden, row);
    }

    private sealed record AbnormalEvent(Guid EventId, string? CashierId, string? CashierName, IReadOnlyList<string> RuleCodes, bool Pending);

    /// <summary>
    /// 取过滤范围内命中有效异常规则的事件及其规则、是否待核查。异常每店每天几十条，
    /// 先取事件编号，再按编号分批取规则与核查结论，避免在聚合里嵌子查询（SQL Server 报错 130）。
    /// </summary>
    private async Task<List<AbnormalEvent>> LoadAbnormalAsync(ISugarQueryable<PosOperationAudit> query)
    {
        var events = await query
            .Where(item => SqlFunc.Subqueryable<PosOperationAuditFlag>()
                .Where(flag => flag.EventId == item.EventId && flag.RetractedAtUtc == null)
                .Any())
            .Select(item => new { item.EventId, item.CashierId, item.CashierName })
            .ToListAsync();
        if (events.Count == 0)
        {
            return [];
        }
        var ids = events.Select(item => item.EventId).ToList();
        var (flags, reviews) = await LoadRiskAsync(ids);
        return events
            .Select(item => new AbnormalEvent(
                item.EventId,
                item.CashierId,
                item.CashierName,
                flags.TryGetValue(item.EventId, out var rows) ? rows.Select(flag => flag.RuleCode).ToList() : [],
                !reviews.ContainsKey(item.EventId)))
            .Where(item => item.RuleCodes.Count > 0)
            .ToList();
    }

    private static List<OperationAuditRuleCountDto> CountByRule(IEnumerable<AbnormalEvent> events) =>
        events
            .SelectMany(item => item.RuleCodes.Distinct(StringComparer.Ordinal))
            .GroupBy(code => code, StringComparer.Ordinal)
            .Select(group => new OperationAuditRuleCountDto { RuleCode = group.Key, Count = group.Count() })
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.RuleCode, StringComparer.Ordinal)
            .ToList();

    private static string EmployeeKey(string? cashierId, string? cashierName) =>
        !string.IsNullOrWhiteSpace(cashierId) ? cashierId : cashierName ?? string.Empty;

    /// <summary>有效规则命中（未撤回）与有效核查结论（确认正常 / 需跟进），按事件编号分组。</summary>
    private async Task<(Dictionary<Guid, List<PosOperationAuditFlag>> Flags, Dictionary<Guid, PosOperationAuditReview> Reviews)> LoadRiskAsync(
        IReadOnlyList<Guid> eventIds)
    {
        var flags = new List<PosOperationAuditFlag>();
        var reviews = new List<PosOperationAuditReview>();
        foreach (var batch in eventIds.Distinct().Chunk(IdBatchSize))
        {
            var ids = batch.ToList();
            flags.AddRange(await _db.Queryable<PosOperationAuditFlag>()
                .Where(flag => ids.Contains(flag.EventId) && flag.RetractedAtUtc == null)
                .ToListAsync());
            reviews.AddRange(await _db.Queryable<PosOperationAuditReview>()
                .Where(review => ids.Contains(review.EventId) && (review.Result == ReviewNormal || review.Result == ReviewFollowUp))
                .ToListAsync());
        }
        return (
            flags.GroupBy(flag => flag.EventId).ToDictionary(group => group.Key, group => group.OrderBy(flag => flag.RuleCode, StringComparer.Ordinal).ToList()),
            reviews.ToDictionary(review => review.EventId)
        );
    }

    /// <summary>给列表行补上异常命中与核查结论（每页最多 200 条，两次按编号查询）。</summary>
    private async Task EnrichAsync(List<OperationAuditListItemDto> items)
    {
        if (items.Count == 0)
        {
            return;
        }
        var (flags, reviews) = await LoadRiskAsync(items.Select(item => item.EventId).ToList());
        foreach (var item in items)
        {
            if (flags.TryGetValue(item.EventId, out var rows))
            {
                item.Flags = rows.Select(flag => new OperationAuditFlagDto
                {
                    RuleCode = flag.RuleCode,
                    Evidence = ParseEvidence(flag.EvidenceJson),
                    DetectedAtUtc = AsUtc(flag.DetectedAtUtc),
                }).ToList();
            }
            if (reviews.TryGetValue(item.EventId, out var review))
            {
                item.Review = MapReview(review);
            }
        }
    }

    internal const byte ReviewRevoked = 0;
    internal const byte ReviewNormal = 1;
    internal const byte ReviewFollowUp = 2;

    internal static OperationAuditReviewDto? MapReview(PosOperationAuditReview? review) =>
        review == null || review.Result is not (ReviewNormal or ReviewFollowUp)
            ? null
            : new OperationAuditReviewDto
            {
                Result = review.Result == ReviewNormal ? "normal" : "followUp",
                Note = review.Note,
                ReviewedByName = review.ReviewedByName,
                ReviewedAtUtc = AsUtc(review.ReviewedAtUtc),
                Version = review.Version,
            };

    private static Dictionary<string, string> ParseEvidence(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>();
        }
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (System.Text.Json.JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>危险操作：类型在危险清单内，或不关联订单的手动开钱箱。</summary>
    private static ISugarQueryable<PosOperationAudit> ApplyDanger(ISugarQueryable<PosOperationAudit> query)
    {
        var dangerTypes = PosOperationAuditRiskCatalog.DangerOperations.ToArray();
        return query.Where(item =>
            dangerTypes.Contains(item.OperationType)
            || (item.OperationType == PosOperationAuditRiskCatalog.CashDrawerOpen && (item.OrderGuid == null || item.OrderGuid == "")));
    }

    /// <summary>
    /// 构建列表与汇总共用的过滤查询；返回 null 表示条件本身已决定结果为空（时间范围倒置、越权门店、非法设备系统）。
    /// includeOutcomeFilters 为 false 时跳过 Outcome / IsEmergencyOverride / IsOfflineCached 三个“结果类”过滤。
    /// 分页与排序不在这里处理。
    /// </summary>
    private ISugarQueryable<PosOperationAudit>? BuildFilteredQuery(
        OperationAuditQueryDto request,
        OperationAuditStoreAccess access,
        DateTime now,
        FilterScope scope
    )
    {
        var fromUtc = request.FromUtc?.UtcDateTime ?? now.AddDays(-7);
        var toUtc = request.ToUtc?.UtcDateTime ?? now;
        if (fromUtc > toUtc)
        {
            return null;
        }

        var query = _db.Queryable<PosOperationAudit>()
            .Where(item => item.OccurredAtUtc >= fromUtc && item.OccurredAtUtc <= toUtc);

        if (!access.IsAdmin)
        {
            query = query.Where(item => access.StoreCodes.Contains(item.StoreCode));
        }

        // 单店旧参数与多选合并；非管理员只保留可管理分店，请求的分店全部越权时结果为空。
        var requestedStores = (request.StoreCodes ?? [])
            .Append(request.StoreCode)
            .Select(TrimToNull)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (requestedStores.Count > 0)
        {
            var stores = access.IsAdmin
                ? requestedStores
                : requestedStores.Where(code => access.StoreCodes.Contains(code, StringComparer.OrdinalIgnoreCase)).ToList();
            if (stores.Count == 0)
            {
                return null;
            }

            query = stores.Count == 1
                ? query.Where(item => item.StoreCode == stores[0])
                : query.Where(item => stores.Contains(item.StoreCode));
        }

        var cashierKeyword = scope.HasFlag(FilterScope.Cashier) ? TrimToNull(request.CashierKeyword) : null;
        var cashierId = scope.HasFlag(FilterScope.Cashier) ? TrimToNull(request.CashierId) : null;
        if (cashierId != null)
        {
            query = query.Where(item => item.CashierId == cashierId);
        }

        if (cashierKeyword != null)
        {
            query = query.Where(item =>
                (item.CashierId != null && item.CashierId.Contains(cashierKeyword))
                || (item.UserGuid != null && item.UserGuid.Contains(cashierKeyword))
                || (item.CashierName != null && item.CashierName.Contains(cashierKeyword))
            );
        }

        var deviceCode = TrimToNull(request.DeviceCode);
        if (deviceCode != null)
        {
            query = query.Where(item => item.DeviceCode == deviceCode);
        }

        var deviceSystem = ParseDeviceSystemFilter(request.DeviceSystem);
        if (deviceSystem == DeviceSystemFilter.Invalid)
        {
            return null;
        }

        query = deviceSystem switch
        {
            DeviceSystemFilter.Windows => query.Where(item => item.DeviceSystem == "Windows"),
            DeviceSystemFilter.IpadOs => query.Where(item => item.DeviceSystem == "iPadOS"),
            DeviceSystemFilter.Unknown => query.Where(item => item.DeviceSystem == null || item.DeviceSystem == ""),
            _ => query,
        };

        if (scope.HasFlag(FilterScope.OperationType))
        {
            var operationTypes = (request.OperationTypes ?? [])
                .Append(request.OperationType)
                .Select(TrimToNull)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (operationTypes.Count == 1)
            {
                var operationType = operationTypes[0];
                query = query.Where(item => item.OperationType == operationType);
            }
            else if (operationTypes.Count > 1)
            {
                query = query.Where(item => operationTypes.Contains(item.OperationType));
            }
        }

        if (scope.HasFlag(FilterScope.Risk))
        {
            query = ApplyRiskLens(query, request);
        }

        if (scope.HasFlag(FilterScope.Outcome))
        {
            var outcome = TrimToNull(request.Outcome);
            if (outcome != null)
            {
                query = query.Where(item => item.Outcome == outcome);
            }

            if (request.IsEmergencyOverride is { } isEmergencyOverride)
            {
                query = query.Where(item => item.IsEmergencyOverride == isEmergencyOverride);
            }

            if (request.IsOfflineCached is { } isOfflineCached)
            {
                query = query.Where(item => item.IsOfflineCached == isOfflineCached);
            }
        }

        var orderGuid = TrimToNull(request.OrderGuid);
        if (orderGuid != null)
        {
            query = query.Where(item => item.OrderGuid == orderGuid);
        }

        var keyword = TrimToNull(request.Keyword);
        if (keyword != null)
        {
            query = query.Where(item =>
                (item.OrderGuid != null && item.OrderGuid.Contains(keyword))
                || (item.ReceiptNumber != null && item.ReceiptNumber.Contains(keyword))
                || (item.CorrelationId != null && item.CorrelationId.Contains(keyword))
                || (item.TraceId != null && item.TraceId.Contains(keyword))
                || (item.ReasonCode != null && item.ReasonCode.Contains(keyword))
                || (item.SafeMessage != null && item.SafeMessage.Contains(keyword))
                || (item.CashierId != null && item.CashierId.Contains(keyword))
                || (item.CashierName != null && item.CashierName.Contains(keyword))
                || item.DeviceCode.Contains(keyword)
            );
        }

        var productKeyword = TrimToNull(request.ProductKeyword);
        if (productKeyword != null)
        {
            // 商品检索固定从子表 EXISTS 过滤，父事件仍只返回一条，避免多商品动作被展开。
            query = query.Where(parent =>
                SqlFunc.Subqueryable<PosOperationAuditItem>()
                    .Where(item =>
                        item.EventId == parent.EventId
                        && (
                            (item.ProductCode != null && item.ProductCode.Contains(productKeyword))
                            || (item.ItemNumber != null && item.ItemNumber.Contains(productKeyword))
                            || (item.ReferenceCode != null && item.ReferenceCode.Contains(productKeyword))
                            || (item.LookupCode != null && item.LookupCode.Contains(productKeyword))
                            || (item.DisplayName != null && item.DisplayName.Contains(productKeyword))
                        )
                    )
                    .Any()
            );
        }

        return query;
    }

    /// <summary>
    /// 风险入口：danger 按危险操作过滤；abnormal 只看有有效规则命中的事件，可再按规则与核查状态收窄。
    /// 未知入口按 all 处理。
    /// </summary>
    private static ISugarQueryable<PosOperationAudit> ApplyRiskLens(ISugarQueryable<PosOperationAudit> query, OperationAuditQueryDto request)
    {
        var lens = TrimToNull(request.RiskLens)?.ToLowerInvariant();
        if (lens == "danger")
        {
            return ApplyDanger(query);
        }
        if (lens != "abnormal")
        {
            return query;
        }

        var rules = (request.RuleCodes ?? [])
            .Select(TrimToNull)
            .OfType<string>()
            .Where(code => PosOperationAuditRiskCatalog.AllRules.Contains(code, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        query = rules.Count == 0
            ? query.Where(item => SqlFunc.Subqueryable<PosOperationAuditFlag>()
                .Where(flag => flag.EventId == item.EventId && flag.RetractedAtUtc == null)
                .Any())
            : query.Where(item => SqlFunc.Subqueryable<PosOperationAuditFlag>()
                .Where(flag => flag.EventId == item.EventId && flag.RetractedAtUtc == null && rules.Contains(flag.RuleCode))
                .Any());

        return TrimToNull(request.ReviewStatus)?.ToLowerInvariant() switch
        {
            "pending" => query.Where(item => SqlFunc.Subqueryable<PosOperationAuditReview>()
                .Where(review => review.EventId == item.EventId && (review.Result == ReviewNormal || review.Result == ReviewFollowUp))
                .NotAny()),
            "reviewed" => query.Where(item => SqlFunc.Subqueryable<PosOperationAuditReview>()
                .Where(review => review.EventId == item.EventId && (review.Result == ReviewNormal || review.Result == ReviewFollowUp))
                .Any()),
            "followup" => query.Where(item => SqlFunc.Subqueryable<PosOperationAuditReview>()
                .Where(review => review.EventId == item.EventId && review.Result == ReviewFollowUp)
                .Any()),
            _ => query,
        };
    }

    private static ISugarQueryable<PosOperationAudit> ApplySort(
        ISugarQueryable<PosOperationAudit> query,
        string? sortBy,
        string? sortOrder
    )
    {
        var normalizedField = TrimToNull(sortBy)?.ToLowerInvariant();
        var normalizedOrder = TrimToNull(sortOrder)?.ToLowerInvariant();
        if (normalizedOrder is not ("asc" or "desc"))
        {
            normalizedField = null;
        }

        var orderByType = normalizedOrder == "asc" ? OrderByType.Asc : OrderByType.Desc;

        // 排序字段固定白名单并由表达式生成 SQL；任何非法组合都回退到默认时间倒序。
        return normalizedField switch
        {
            "occurredatutc" => query
                .OrderBy(item => item.OccurredAtUtc, orderByType)
                .OrderBy(item => item.EventId, orderByType),
            "storecode" => query
                .OrderBy(item => item.StoreCode, orderByType)
                .OrderBy(item => item.OccurredAtUtc, OrderByType.Desc)
                .OrderBy(item => item.EventId, OrderByType.Desc),
            "operationtype" => query
                .OrderBy(item => item.OperationType, orderByType)
                .OrderBy(item => item.OccurredAtUtc, OrderByType.Desc)
                .OrderBy(item => item.EventId, OrderByType.Desc),
            "amountdelta" => query
                // 金额为空始终排在末尾，升降序只影响非空金额。
                .OrderBy(item => item.AmountDelta == null ? 1 : 0, OrderByType.Asc)
                .OrderBy(item => item.AmountDelta, orderByType)
                .OrderBy(item => item.OccurredAtUtc, OrderByType.Desc)
                .OrderBy(item => item.EventId, OrderByType.Desc),
            "devicecode" => query
                .OrderBy(item => item.DeviceCode, orderByType)
                .OrderBy(item => item.OccurredAtUtc, OrderByType.Desc)
                .OrderBy(item => item.EventId, OrderByType.Desc),
            "outcome" => query
                .OrderBy(item => item.Outcome, orderByType)
                .OrderBy(item => item.OccurredAtUtc, OrderByType.Desc)
                .OrderBy(item => item.EventId, OrderByType.Desc),
            _ => query
                .OrderBy(item => item.OccurredAtUtc, OrderByType.Desc)
                .OrderBy(item => item.EventId, OrderByType.Desc),
        };
    }

    public async Task<OperationAuditDetailQueryResult> GetDetailAsync(Guid eventId)
    {
        var row = await _db.Queryable<PosOperationAudit>()
            .FirstAsync(item => item.EventId == eventId);
        if (row == null)
        {
            return new OperationAuditDetailQueryResult
            {
                Status = OperationAuditDetailAccessStatus.NotFound,
            };
        }

        var access = await ResolveAccessAsync();
        if (!access.CanAccess(row.StoreCode))
        {
            return new OperationAuditDetailQueryResult
            {
                Status = OperationAuditDetailAccessStatus.Forbidden,
            };
        }

        var items = await _db.Queryable<PosOperationAuditItem>()
            .Where(item => item.EventId == eventId)
            .OrderBy(item => item.LineIndex)
            .ToListAsync();
        var detail = MapDetail(row);
        detail.Items = items.Select(MapDetailItem).ToList();
        var enriched = new List<OperationAuditListItemDto> { detail };
        await EnrichAsync(enriched);
        return new OperationAuditDetailQueryResult
        {
            Status = OperationAuditDetailAccessStatus.Found,
            Data = detail,
        };
    }

    private async Task<OperationAuditStoreAccess> ResolveAccessAsync()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return OperationAuditStoreAccess.Denied;
        }

        if (HasAnyRole(user, AdminRoleAliases))
        {
            return OperationAuditStoreAccess.Admin;
        }

        if (!HasAnyRole(user, StoreManagerRoleAliases))
        {
            return OperationAuditStoreAccess.Denied;
        }

        var scope = await _storeScopeService.GetScopeAsync();
        var storeCodes = scope.StoreCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return scope.IsAllowed && storeCodes.Length > 0
            ? new OperationAuditStoreAccess(true, false, storeCodes)
            : OperationAuditStoreAccess.Denied;
    }

    private static bool HasAnyRole(ClaimsPrincipal user, IEnumerable<string> aliases) =>
        user.Claims.Any(claim =>
            claim.Type == ClaimTypes.Role
            && aliases.Any(alias => alias.Equals(claim.Value, StringComparison.OrdinalIgnoreCase))
        );

    private static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DeviceSystemFilter? ParseDeviceSystemFilter(string? value)
    {
        var normalized = TrimToNull(value);
        if (normalized is null)
        {
            return null;
        }

        if (string.Equals(normalized, "Windows", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceSystemFilter.Windows;
        }

        if (string.Equals(normalized, "iPadOS", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceSystemFilter.IpadOs;
        }

        return string.Equals(normalized, "Unknown", StringComparison.OrdinalIgnoreCase)
            ? DeviceSystemFilter.Unknown
            : DeviceSystemFilter.Invalid;
    }

    private static string? NormalizeStoredDeviceSystem(string? value)
    {
        var normalized = TrimToNull(value);
        if (normalized is null)
        {
            return null;
        }

        if (string.Equals(normalized, "Windows", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows";
        }

        return string.Equals(normalized, "iPadOS", StringComparison.OrdinalIgnoreCase)
            ? "iPadOS"
            : normalized;
    }

    private static PagedListReactDto<OperationAuditListItemDto> CreateEmptyPage(
        int pageNumber,
        int pageSize
    ) => new()
    {
        PageNumber = pageNumber,
        PageSize = pageSize,
    };

    private static OperationAuditListItemDto MapListItem(PosOperationAudit row) => new()
    {
        EventId = row.EventId,
        SchemaVersion = row.SchemaVersion,
        OccurredAtUtc = AsUtc(row.OccurredAtUtc),
        ReceivedAtUtc = AsUtc(row.ReceivedAtUtc),
        OperationType = row.OperationType,
        Outcome = row.Outcome,
        CashierId = row.CashierId,
        UserGuid = row.UserGuid,
        CashierName = row.CashierName,
        IsOfflineCached = row.IsOfflineCached,
        IsEmergencyOverride = row.IsEmergencyOverride,
        StoreCode = row.StoreCode,
        DeviceCode = row.DeviceCode,
        DeviceSystem = NormalizeStoredDeviceSystem(row.DeviceSystem),
        AppVersion = row.AppVersion,
        InstanceId = row.InstanceId,
        OrderGuid = row.OrderGuid,
        ReceiptNumber = row.ReceiptNumber,
        CorrelationId = row.CorrelationId,
        TraceId = row.TraceId,
        PaymentMethod = row.PaymentMethod,
        ReasonCode = row.ReasonCode,
        SafeMessage = row.SafeMessage,
        CurrencyCode = row.CurrencyCode,
        PaymentAmount = row.PaymentAmount,
        BeforeGross = row.BeforeGross,
        AfterGross = row.AfterGross,
        BeforeDiscount = row.BeforeDiscount,
        AfterDiscount = row.AfterDiscount,
        BeforeActual = row.BeforeActual,
        AfterActual = row.AfterActual,
        AmountDelta = row.AmountDelta,
        ProductCount = row.ProductCount,
        PrimaryProduct = row.PrimaryProduct,
        IsDanger = PosOperationAuditRiskCatalog.IsDanger(row.OperationType, row.OrderGuid),
        AmountImpact = PosOperationAuditRiskCatalog.IsDanger(row.OperationType, row.OrderGuid)
            ? PosOperationAuditRiskCatalog.AmountImpact(row.OperationType, row.Outcome, row.BeforeActual, row.AfterActual, row.PaymentAmount)
            : null,
    };

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static OperationAuditDetailDto MapDetail(PosOperationAudit row)
    {
        var listItem = MapListItem(row);
        return new OperationAuditDetailDto
        {
            EventId = listItem.EventId,
            SchemaVersion = listItem.SchemaVersion,
            OccurredAtUtc = listItem.OccurredAtUtc,
            ReceivedAtUtc = listItem.ReceivedAtUtc,
            OperationType = listItem.OperationType,
            Outcome = listItem.Outcome,
            CashierId = listItem.CashierId,
            UserGuid = listItem.UserGuid,
            CashierName = listItem.CashierName,
            IsOfflineCached = listItem.IsOfflineCached,
            IsEmergencyOverride = listItem.IsEmergencyOverride,
            StoreCode = listItem.StoreCode,
            DeviceCode = listItem.DeviceCode,
            DeviceSystem = listItem.DeviceSystem,
            AppVersion = listItem.AppVersion,
            InstanceId = listItem.InstanceId,
            OrderGuid = listItem.OrderGuid,
            ReceiptNumber = listItem.ReceiptNumber,
            CorrelationId = listItem.CorrelationId,
            TraceId = listItem.TraceId,
            PaymentMethod = listItem.PaymentMethod,
            ReasonCode = listItem.ReasonCode,
            SafeMessage = listItem.SafeMessage,
            CurrencyCode = listItem.CurrencyCode,
            PaymentAmount = listItem.PaymentAmount,
            BeforeGross = listItem.BeforeGross,
            AfterGross = listItem.AfterGross,
            BeforeDiscount = listItem.BeforeDiscount,
            AfterDiscount = listItem.AfterDiscount,
            BeforeActual = listItem.BeforeActual,
            AfterActual = listItem.AfterActual,
            AmountDelta = listItem.AmountDelta,
            ProductCount = listItem.ProductCount,
            PrimaryProduct = listItem.PrimaryProduct,
            IsDanger = listItem.IsDanger,
            AmountImpact = listItem.AmountImpact,
            PropertiesJson = row.PropertiesJson,
        };
    }

    private static OperationAuditDetailItemDto MapDetailItem(PosOperationAuditItem row) => new()
    {
        EventId = row.EventId,
        LineIndex = row.LineIndex,
        ProductCode = row.ProductCode,
        ItemNumber = row.ItemNumber,
        ReferenceCode = row.ReferenceCode,
        LookupCode = row.LookupCode,
        DisplayName = row.DisplayName,
        LineKind = row.LineKind,
        BeforeQuantity = row.BeforeQuantity,
        AfterQuantity = row.AfterQuantity,
        QuantityDelta = row.QuantityDelta,
        BeforeUnitPrice = row.BeforeUnitPrice,
        AfterUnitPrice = row.AfterUnitPrice,
        UnitPriceDelta = row.UnitPriceDelta,
        BeforeDiscountAmount = row.BeforeDiscountAmount,
        AfterDiscountAmount = row.AfterDiscountAmount,
        DiscountAmountDelta = row.DiscountAmountDelta,
        BeforeGrossAmount = row.BeforeGrossAmount,
        AfterGrossAmount = row.AfterGrossAmount,
        GrossAmountDelta = row.GrossAmountDelta,
        BeforeActualAmount = row.BeforeActualAmount,
        AfterActualAmount = row.AfterActualAmount,
        ActualAmountDelta = row.ActualAmountDelta,
    };

    private sealed record OperationAuditStoreAccess(
        bool IsAllowed,
        bool IsAdmin,
        IReadOnlyList<string> StoreCodes
    )
    {
        public static OperationAuditStoreAccess Denied { get; } = new(false, false, []);

        public static OperationAuditStoreAccess Admin { get; } = new(true, true, []);

        public bool CanAccess(string storeCode) =>
            IsAllowed
            && (
                IsAdmin
                || StoreCodes.Contains(storeCode, StringComparer.OrdinalIgnoreCase)
            );
    }
}
