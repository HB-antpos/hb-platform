using System.Data.Common;
using System.Diagnostics;
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
}

public sealed record LegacyEmployeeLogResult<T>(LegacyEmployeeLogResultStatus Status, T? Data, string? Message = null)
{
    public static LegacyEmployeeLogResult<T> Ok(T data) => new(LegacyEmployeeLogResultStatus.Ok, data);

    public static LegacyEmployeeLogResult<T> Invalid(string message) => new(LegacyEmployeeLogResultStatus.Invalid, default, message);

    public static LegacyEmployeeLogResult<T> Forbidden() => new(LegacyEmployeeLogResultStatus.Forbidden, default, "无权查看该分店的操作日志");

    public static LegacyEmployeeLogResult<T> NotFound() => new(LegacyEmployeeLogResultStatus.NotFound, default, "操作日志不存在");
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

    public LegacyEmployeeLogQueryService(
        ISqlSugarClient posmDb,
        ICurrentUserManageableStoreScopeService storeScopeService,
        ILogger<LegacyEmployeeLogQueryService> logger
    )
    {
        _posmDb = posmDb;
        _storeScopeService = storeScopeService;
        _logger = logger;
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
        if (!await _storeScopeService.CanAccessStoreCodeAsync(query.StoreCode))
        {
            return LegacyEmployeeLogResult<LegacyEmployeeLogListResultDto>.Forbidden();
        }

        var stopwatch = Stopwatch.StartNew();
        var (counts, rows, employees, devices) = await LegacyEmployeeLogSqlServerQuery.ExecuteListAsync(
            GetSqlServerConnection(),
            LegacyEmployeeLogSqlServerQuery.BuildList(query),
            cancellationToken
        );
        // 计数结果已套用除操作类型外的全部条件，按操作类型条件累加即为列表总数，省掉一次 COUNT。
        var operationFilter = new HashSet<string>(query.Operations, StringComparer.OrdinalIgnoreCase);
        var total = counts
            .Where(row => operationFilter.Count == 0 || (row.Operation != null && operationFilter.Contains(row.Operation)))
            .Sum(row => row.Count);

        stopwatch.Stop();
        if (stopwatch.ElapsedMilliseconds >= SlowQueryWarningMilliseconds)
        {
            _logger.LogWarning(
                "[legacy-employee-logs-perf] store={Store} days={Days} device={HasDevice} employees={EmployeeCount} operations={OperationCount} keyword={HasKeyword} total={Total} ms={Ms}",
                query.StoreCode,
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
        });
    }

    public async Task<LegacyEmployeeLogResult<LegacyEmployeeLogContextDto>> GetContextAsync(
        string? id,
        CancellationToken cancellationToken = default
    )
    {
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
            LegacyEmployeeLogSqlServerQuery.BuildNeighbors(target),
            cancellationToken
        );
        var truncated = neighbors.Count > LegacyEmployeeLogSqlServerQuery.ContextRowLimit;
        return LegacyEmployeeLogResult<LegacyEmployeeLogContextDto>.Ok(new LegacyEmployeeLogContextDto
        {
            Target = target,
            WindowMinutes = LegacyEmployeeLogSqlServerQuery.ContextWindowMinutes,
            Neighbors = neighbors.Take(LegacyEmployeeLogSqlServerQuery.ContextRowLimit).ToList(),
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
