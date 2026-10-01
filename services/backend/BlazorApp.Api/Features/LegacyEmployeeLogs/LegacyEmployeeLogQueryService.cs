using System.Data.Common;
using System.Diagnostics;
using BlazorApp.Api.Features.LegacyEmployeeLogs.Risk;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.DTOs;
using SqlSugar;

namespace BlazorApp.Api.Features.LegacyEmployeeLogs;

public enum LegacyEmployeeLogResultStatus
{
    Ok,
    Invalid,
    Forbidden,
    NotFound,
    Conflict,
}

public sealed record LegacyEmployeeLogResult<T>(LegacyEmployeeLogResultStatus Status, T? Data, string? Message = null)
{
    public static LegacyEmployeeLogResult<T> Ok(T data) => new(LegacyEmployeeLogResultStatus.Ok, data);

    public static LegacyEmployeeLogResult<T> Invalid(string message) => new(LegacyEmployeeLogResultStatus.Invalid, default, message);

    public static LegacyEmployeeLogResult<T> Forbidden() => new(LegacyEmployeeLogResultStatus.Forbidden, default, "无权查看该分店的操作日志");

    public static LegacyEmployeeLogResult<T> NotFound() => new(LegacyEmployeeLogResultStatus.NotFound, default, "操作日志不存在");

    public static LegacyEmployeeLogResult<T> Conflict() => new(LegacyEmployeeLogResultStatus.Conflict, default, "该记录已被他人核查或修改，请刷新后再操作");
}

/// <summary>
/// 老系统操作日志只读查询：分店必选，并按当前账号可管理分店收口（管理员与仓库管理员可看全部分店）。
/// </summary>
public sealed class LegacyEmployeeLogQueryService
{
    /// <summary>总耗时达到该值时记 Warning，进入中心日志表，便于发现索引缺失或计划退化。</summary>
    private const long SlowQueryWarningMilliseconds = 2000;

    private readonly ISqlSugarClient _posmDb;
    private readonly ICurrentUserManageableStoreScopeService _storeScopeService;
    private readonly ILogger<LegacyEmployeeLogQueryService> _logger;
    private readonly int _keywordScanRowLimit;

    public LegacyEmployeeLogQueryService(
        ISqlSugarClient posmDb,
        ICurrentUserManageableStoreScopeService storeScopeService,
        ILogger<LegacyEmployeeLogQueryService> logger,
        int keywordScanRowLimit = LegacyEmployeeLogSqlServerQuery.DefaultKeywordScanRowLimit
    )
    {
        _posmDb = posmDb;
        _storeScopeService = storeScopeService;
        _logger = logger;
        _keywordScanRowLimit = keywordScanRowLimit;
    }

    public async Task<LegacyEmployeeLogResult<LegacyEmployeeLogListResultDto>> QueryAsync(
        LegacyEmployeeLogQueryDto request,
        CancellationToken cancellationToken = default
    )
    {
        var (query, error) = LegacyEmployeeLogSqlServerQuery.Normalize(request);
        if (query == null)
        {
            return LegacyEmployeeLogResult<LegacyEmployeeLogListResultDto>.Invalid(error!);
        }
        // 多选时每家分店都必须在当前账号可管理范围内，任一越权整次拒绝，不静默剔除。
        var scope = await _storeScopeService.GetScopeAsync();
        if (!scope.IsAllowed || query.StoreCodes.Any(code => !scope.CanAccessStoreCode(code)))
        {
            return LegacyEmployeeLogResult<LegacyEmployeeLogListResultDto>.Forbidden();
        }

        var stopwatch = Stopwatch.StartNew();
        var connection = GetSqlServerConnection();
        var abnormal = query.RiskLens == LegacyEmployeeLogSqlServerQuery.LensAbnormal;
        var listQuery = query.RiskLens == LegacyEmployeeLogSqlServerQuery.LensDanger
            ? query with { Operations = DangerOperationsWithin(query.Operations) }
            : query;
        var page = await LegacyEmployeeLogSqlServerQuery.ExecuteListAsync(
            connection,
            LegacyEmployeeLogSqlServerQuery.BuildList(listQuery, _keywordScanRowLimit),
            hasKeywordGuard: query.Keyword != null,
            _keywordScanRowLimit,
            cancellationToken,
            hasLensTotal: abnormal
        );
        if (page.RejectedScanRows is { } scanRows)
        {
            return LegacyEmployeeLogResult<LegacyEmployeeLogListResultDto>.Invalid(
                $"详情关键字需要逐条读取记录，当前范围约 {scanRows:N0} 条，超过 {_keywordScanRowLimit:N0} 条上限；请减少分店、缩短时间，或先选定员工 / 设备后再搜索");
        }
        var (counts, rows, employees, devices) = (page.Counts, page.Rows, page.Employees, page.Devices);
        // 计数结果已套用除操作类型外的全部条件，按操作类型条件累加即为列表总数，省掉一次 COUNT；
        // 异常入口另有命中标记的计数。
        var operationFilter = new HashSet<string>(listQuery.Operations, StringComparer.OrdinalIgnoreCase);
        var total = abnormal
            ? (int)(page.LensTotal ?? 0)
            : counts
                .Where(row => operationFilter.Count == 0 || (row.Operation != null && operationFilter.Contains(row.Operation)))
                .Sum(row => row.Count);

        await LegacyEmployeeLogSqlServerQuery.EnrichAsync(connection, rows, cancellationToken);
        var risk = await LegacyEmployeeLogSqlServerQuery.ExecuteRiskSummaryAsync(
            connection,
            LegacyEmployeeLogSqlServerQuery.BuildRiskSummary(query),
            cancellationToken
        );
        var riskSummary = new LegacyEmployeeLogRiskSummaryDto
        {
            DangerTotal = counts.Where(row => LegacyEmployeeLogRiskCatalog.IsDanger(row.Operation)).Sum(row => row.Count),
            AbnormalTotal = risk.Total,
            PendingReview = risk.Pending,
            AbnormalEmployees = risk.Employees,
            AbnormalByRule = risk.ByRule
                .OrderBy(rule => LegacyEmployeeLogRiskCatalog.AllRules.ToList().IndexOf(rule.RuleCode))
                .ToList(),
        };

        stopwatch.Stop();
        if (stopwatch.ElapsedMilliseconds >= SlowQueryWarningMilliseconds)
        {
            _logger.LogWarning(
                "[legacy-employee-logs-perf] stores={Stores} days={Days} device={HasDevice} employees={EmployeeCount} operations={OperationCount} keyword={HasKeyword} total={Total} ms={Ms}",
                string.Join(",", query.StoreCodes),
                Math.Ceiling((query.ToExclusive - query.From).TotalDays),
                query.DeviceCode != null,
                query.EmployeeIds.Count,
                query.Operations.Count,
                query.Keyword != null,
                total,
                stopwatch.ElapsedMilliseconds
            );
        }

        return LegacyEmployeeLogResult<LegacyEmployeeLogListResultDto>.Ok(new LegacyEmployeeLogListResultDto
        {
            Items = rows,
            Total = total,
            PageNumber = query.PageNumber,
            PageSize = query.PageSize,
            OperationCounts = counts.OrderByDescending(row => row.Count).ThenBy(row => row.Operation, StringComparer.Ordinal).ToList(),
            Employees = employees
                .OrderBy(row => row.EmployeeName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.EmployeeId, StringComparer.Ordinal)
                .ToList(),
            Devices = devices.OrderBy(row => row.DeviceCode, StringComparer.OrdinalIgnoreCase).ToList(),
            RiskSummary = riskSummary,
        });
    }

    /// <summary>危险入口：用户选了操作类型时取与危险清单的交集；交集为空时用一个不会出现的占位值，让列表为空而计数照常。</summary>
    private static IReadOnlyList<string> DangerOperationsWithin(IReadOnlyList<string> selected)
    {
        if (selected.Count == 0)
        {
            return LegacyEmployeeLogRiskCatalog.DangerOperations;
        }
        var within = selected.Where(LegacyEmployeeLogRiskCatalog.IsDanger).ToList();
        return within.Count > 0 ? within : ["\u0001"];
    }

    public async Task<LegacyEmployeeLogResult<LegacyEmployeeLogEmployeeSummaryResultDto>> GetEmployeeSummaryAsync(
        LegacyEmployeeLogEmployeeSummaryQueryDto request,
        CancellationToken cancellationToken = default
    )
    {
        var (query, error) = LegacyEmployeeLogSqlServerQuery.Normalize(new LegacyEmployeeLogQueryDto
        {
            StoreCodes = request.StoreCodes,
            From = request.From,
            To = request.To,
            DeviceCode = request.DeviceCode,
        });
        if (query == null)
        {
            return LegacyEmployeeLogResult<LegacyEmployeeLogEmployeeSummaryResultDto>.Invalid(error!);
        }
        var scope = await _storeScopeService.GetScopeAsync();
        if (!scope.IsAllowed || query.StoreCodes.Any(code => !scope.CanAccessStoreCode(code)))
        {
            return LegacyEmployeeLogResult<LegacyEmployeeLogEmployeeSummaryResultDto>.Forbidden();
        }

        var employees = await LegacyEmployeeLogSqlServerQuery.ExecuteEmployeeSummaryAsync(
            GetSqlServerConnection(),
            LegacyEmployeeLogSqlServerQuery.BuildEmployeeSummary(query),
            cancellationToken
        );
        // 默认排序：异常多的在前，其次危险占比高的在前；前端可再按金额等重排。
        var ordered = employees
            .OrderByDescending(row => row.AbnormalCount)
            .ThenByDescending(row => row.Total == 0 ? 0d : (double)row.DangerCount / row.Total)
            .ThenBy(row => row.EmployeeName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return LegacyEmployeeLogResult<LegacyEmployeeLogEmployeeSummaryResultDto>.Ok(new LegacyEmployeeLogEmployeeSummaryResultDto
        {
            Employees = ordered,
            Total = ordered.Sum(row => row.Total),
            DangerTotal = ordered.Sum(row => row.DangerCount),
        });
    }

    public async Task<LegacyEmployeeLogResult<LegacyEmployeeLogContextDto>> GetContextAsync(
        string? id,
        CancellationToken cancellationToken = default,
        int? windowMinutes = null
    )
    {
        var window = windowMinutes ?? LegacyEmployeeLogSqlServerQuery.ContextWindowMinutes;
        if (!LegacyEmployeeLogSqlServerQuery.AllowedContextWindows.Contains(window))
        {
            return LegacyEmployeeLogResult<LegacyEmployeeLogContextDto>.Invalid("前后操作时间窗口只支持 5、10、15 分钟");
        }
        var normalizedId = id?.Trim();
        if (string.IsNullOrEmpty(normalizedId) || normalizedId.Length > 255)
        {
            return LegacyEmployeeLogResult<LegacyEmployeeLogContextDto>.Invalid("日志编号无效");
        }

        var connection = GetSqlServerConnection();
        var target = (await LegacyEmployeeLogSqlServerQuery.ExecuteItemsAsync(
            connection,
            LegacyEmployeeLogSqlServerQuery.BuildTarget(normalizedId),
            cancellationToken
        )).FirstOrDefault();
        if (target == null)
        {
            return LegacyEmployeeLogResult<LegacyEmployeeLogContextDto>.NotFound();
        }
        // 先取行再校验分店：没有分店的日志只有管理员范围才可见。
        if (string.IsNullOrWhiteSpace(target.StoreCode)
            || !await _storeScopeService.CanAccessStoreCodeAsync(target.StoreCode))
        {
            return LegacyEmployeeLogResult<LegacyEmployeeLogContextDto>.Forbidden();
        }

        var neighbors = await LegacyEmployeeLogSqlServerQuery.ExecuteItemsAsync(
            connection,
            LegacyEmployeeLogSqlServerQuery.BuildNeighbors(target, window),
            cancellationToken
        );
        var truncated = neighbors.Count > LegacyEmployeeLogSqlServerQuery.ContextRowLimit;
        var kept = neighbors.Take(LegacyEmployeeLogSqlServerQuery.ContextRowLimit).ToList();
        // 目标行也在邻居里（同一编号），一起补风险信息；目标单独对象同样补上，供移动端详情直接取用。
        await LegacyEmployeeLogSqlServerQuery.EnrichAsync(connection, [target, .. kept], cancellationToken);
        return LegacyEmployeeLogResult<LegacyEmployeeLogContextDto>.Ok(new LegacyEmployeeLogContextDto
        {
            Target = target,
            WindowMinutes = window,
            Neighbors = kept,
            Truncated = truncated,
        });
    }

    private DbConnection GetSqlServerConnection()
    {
        // EmployeeLogs 只存在于生产 POSM（SQL Server），查询依赖专用索引与 T-SQL 语法，不提供 SQLite 路径。
        if (_posmDb.CurrentConnectionConfig.DbType != DbType.SqlServer)
        {
            throw new NotSupportedException("老系统操作日志只支持 SQL Server 上的 POSM 库。");
        }
        return (DbConnection)_posmDb.Ado.Connection;
    }
}
