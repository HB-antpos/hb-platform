using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.Performance;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace BlazorApp.Api.Services.React;

/// <summary>
/// 使用短数据库租约领取商品 HQ outbox，并在本地事务外调用具体执行器。
/// </summary>
public sealed class ProductHqSyncOutboxWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProductHqSyncOutboxOptions _options;
    private readonly ILogger<ProductHqSyncOutboxWorker> _logger;
    private readonly string _ownerInstanceId;

    public ProductHqSyncOutboxWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<ProductHqSyncOutboxOptions> options,
        ILogger<ProductHqSyncOutboxWorker> logger
    )
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
        _ownerInstanceId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !LegacyDatabaseGate.HqEnabled)
        {
            _logger.LogInformation("商品 HQ outbox worker 已由配置关闭");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await ProcessNextAsync(stoppingToken))
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "商品 HQ outbox 轮询失败，将在下一周期重试");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(Math.Clamp(_options.PollIntervalSeconds, 1, 300)),
                stoppingToken
            );
        }
    }

    private async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        ProductHqSyncOutboxExecutionClaim? executionClaim;
        using (var claimScope = _scopeFactory.CreateScope())
        {
            var db = claimScope.ServiceProvider.GetRequiredService<SqlSugarContext>().Db;
            executionClaim = await TryClaimNextWithExecutionLockAsync(
                db,
                _ownerInstanceId,
                DateTime.UtcNow,
                _options,
                cancellationToken,
                _logger
            );
        }

        if (executionClaim == null)
        {
            return false;
        }
        await using var heldExecutionClaim = executionClaim;
        var workItem = executionClaim.WorkItem;

        PerformanceOperationalRunBridge.Publish(
            PerformanceOperationalRunTransition.Started(
                workItem.OperationKey,
                "hq",
                "product-sync-outbox",
                DateTime.UtcNow,
                attempt: workItem.AttemptCount
            )
        );

        using var renewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var renewalTask = RenewLeaseLoopAsync(workItem, renewalCancellation.Token);
        ProductHqSyncOutboxExecutionResult executionResult;
        try
        {
            using var executorScope = _scopeFactory.CreateScope();
            var executor = executorScope.ServiceProvider.GetRequiredService<IProductHqSyncOutboxExecutor>();
            executionResult = await executor.ExecuteAsync(workItem, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 原始异常仅进入受控服务端日志，数据库和 API 只保留稳定错误码与安全文案。
            _logger.LogError(
                ex,
                "执行商品 HQ outbox 失败: OperationId={OperationId}, Attempt={Attempt}",
                workItem.OperationKey,
                workItem.AttemptCount
            );
            executionResult = ProductHqSyncOutboxExecutionResult.Retryable(
                "PRODUCT_HQ_SYNC_EXECUTION_ERROR",
                "HQ 同步暂时失败，系统将自动重试"
            );
        }
        finally
        {
            renewalCancellation.Cancel();
            try
            {
                await renewalTask;
            }
            catch (OperationCanceledException) when (renewalCancellation.IsCancellationRequested)
            {
                // 正常停止续租。
            }
        }

        bool applied;
        using (var completionScope = _scopeFactory.CreateScope())
        {
            var db = completionScope.ServiceProvider.GetRequiredService<SqlSugarContext>().Db;
            applied = await ApplyResultAsync(
                db,
                workItem,
                executionResult,
                DateTime.UtcNow,
                _options,
                cancellationToken
            );
        }

        if (!applied)
        {
            _logger.LogWarning(
                "商品 HQ outbox 完成写入被 fencing token 拒绝: OperationId={OperationId}",
                workItem.OperationKey
            );
            return true;
        }

        var performanceStatus = executionResult.Disposition switch
        {
            ProductHqSyncOutboxExecutionDisposition.Success => "success",
            ProductHqSyncOutboxExecutionDisposition.Blocked => "failure",
            _ => "retry_wait",
        };
        PerformanceOperationalRunBridge.Publish(
            PerformanceOperationalRunTransition.Completed(
                workItem.OperationKey,
                "hq",
                "product-sync-outbox",
                performanceStatus,
                DateTime.UtcNow,
                workItem.AttemptCount
            )
        );
        return true;
    }

    private async Task RenewLeaseLoopAsync(
        ProductHqSyncOutboxWorkItemDto workItem,
        CancellationToken cancellationToken
    )
    {
        var leaseSeconds = Math.Clamp(_options.LeaseSeconds, 10, 3600);
        var renewalSeconds = Math.Clamp(
            _options.LeaseRenewalSeconds,
            1,
            Math.Max(1, leaseSeconds / 2)
        );
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(renewalSeconds));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SqlSugarContext>().Db;
                var renewed = await RenewLeaseAsync(
                    db,
                    workItem,
                    DateTime.UtcNow,
                    _options,
                    cancellationToken
                );
                if (renewed == 0)
                {
                    _logger.LogWarning(
                        "商品 HQ outbox 租约已失效，等待当前执行结束后由 fencing token 拒绝旧结果: OperationId={OperationId}",
                        workItem.OperationKey
                    );
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "商品 HQ outbox 租约续期失败: OperationId={OperationId}",
                    workItem.OperationKey
                );
            }
        }
    }

    internal static async Task<ProductHqSyncOutboxWorkItemDto?> TryClaimNextAsync(
        ISqlSugarClient db,
        string ownerInstanceId,
        DateTime utcNow,
        ProductHqSyncOutboxOptions options,
        CancellationToken cancellationToken = default,
        ILogger? logger = null
    )
    {
        var executionClaim = await TryClaimNextWithExecutionLockAsync(
            db,
            ownerInstanceId,
            utcNow,
            options,
            cancellationToken,
            logger
        );
        if (executionClaim == null)
        {
            return null;
        }

        await executionClaim.DisposeAsync();
        return executionClaim.WorkItem;
    }

    internal static async Task<ProductHqSyncOutboxExecutionClaim?> TryClaimNextWithExecutionLockAsync(
        ISqlSugarClient db,
        string ownerInstanceId,
        DateTime utcNow,
        ProductHqSyncOutboxOptions options,
        CancellationToken cancellationToken = default,
        ILogger? logger = null
    )
    {
        var now = AsUtc(utcNow);
        var owner = Normalize(ownerInstanceId, 200, "worker");
        var pageSize = Math.Clamp(options.ClaimBatchSize, 1, 200);
        DateTime? afterCreatedAt = null;
        DateTime? afterNextAttemptAtUtc = null;
        Guid? afterId = null;

        while (true)
        {
            var query = db.Queryable<ProductHqSyncOutbox>()
                .Where(item =>
                    !item.IsDeleted
                    && (
                        (
                            (
                                item.Status == ProductHqSyncOutboxStatuses.Pending
                                || item.Status == ProductHqSyncOutboxStatuses.Retrying
                            )
                            && item.NextAttemptAtUtc <= now
                        )
                        || (
                            item.Status == ProductHqSyncOutboxStatuses.Processing
                            && item.LeaseExpiresAtUtc != null
                            && item.LeaseExpiresAtUtc <= now
                        )
                    )
                    && !SqlFunc.Subqueryable<ProductHqSyncOutbox>()
                        .Where(prior =>
                            !prior.IsDeleted
                            && prior.ProductCode == item.ProductCode
                            && (
                                prior.Status == ProductHqSyncOutboxStatuses.Pending
                                || prior.Status == ProductHqSyncOutboxStatuses.Processing
                                || prior.Status == ProductHqSyncOutboxStatuses.Retrying
                            )
                            && (
                                prior.CreatedAt < item.CreatedAt
                                || (
                                    prior.CreatedAt == item.CreatedAt
                                    && prior.Id.CompareTo(item.Id) < 0
                                )
                            )
                        )
                        .Any()
                );
            if (afterCreatedAt.HasValue && afterNextAttemptAtUtc.HasValue && afterId.HasValue)
            {
                var cursorCreatedAt = afterCreatedAt.Value;
                var cursorNextAttemptAtUtc = afterNextAttemptAtUtc.Value;
                var cursorId = afterId.Value;
                query = query.Where(item =>
                    item.CreatedAt > cursorCreatedAt
                    || (
                        item.CreatedAt == cursorCreatedAt
                        && (
                            item.NextAttemptAtUtc > cursorNextAttemptAtUtc
                            || (
                                item.NextAttemptAtUtc == cursorNextAttemptAtUtc
                                && item.Id.CompareTo(cursorId) > 0
                            )
                        )
                    )
                );
            }

            var candidates = await query
                .OrderBy(item => item.CreatedAt)
                .OrderBy(item => item.NextAttemptAtUtc)
                .OrderBy(item => item.Id)
                .Take(pageSize)
                .ToListAsync(cancellationToken);
            if (candidates.Count == 0)
            {
                return null;
            }

            foreach (var candidate in candidates)
            {
                var executionLock = await ProductHqSyncProductExecutionLock.TryAcquireAsync(
                    db,
                    candidate.ProductCode,
                    cancellationToken
                );
                if (executionLock == null)
                {
                    continue;
                }

                var lockTransferred = false;
                try
                {
                    // 任何 scope 都必须等待同一商品更早的 active mutation 安全完成。
                    var isStillEarliestActive = await db.Queryable<ProductHqSyncOutbox>()
                        .Where(item =>
                            !item.IsDeleted
                            && item.Id == candidate.Id
                            && (
                                item.Status == ProductHqSyncOutboxStatuses.Pending
                                || item.Status == ProductHqSyncOutboxStatuses.Processing
                                || item.Status == ProductHqSyncOutboxStatuses.Retrying
                            )
                            && !SqlFunc.Subqueryable<ProductHqSyncOutbox>()
                                .Where(prior =>
                                    !prior.IsDeleted
                                    && prior.ProductCode == item.ProductCode
                                    && (
                                        prior.Status == ProductHqSyncOutboxStatuses.Pending
                                        || prior.Status == ProductHqSyncOutboxStatuses.Processing
                                        || prior.Status == ProductHqSyncOutboxStatuses.Retrying
                                    )
                                    && (
                                        prior.CreatedAt < item.CreatedAt
                                        || (
                                            prior.CreatedAt == item.CreatedAt
                                            && prior.Id.CompareTo(item.Id) < 0
                                        )
                                    )
                                )
                                .Any()
                        )
                        .AnyAsync(cancellationToken);
                    if (!isStillEarliestActive)
                    {
                        continue;
                    }

                    var leaseToken = Guid.NewGuid();
                    var leaseExpires = now.AddSeconds(Math.Clamp(options.LeaseSeconds, 10, 3600));
                    var claimed = await db.Updateable<ProductHqSyncOutbox>()
                        .SetColumns(item => item.Status == ProductHqSyncOutboxStatuses.Processing)
                        .SetColumns(item =>
                            item.AttemptCount
                            == SqlFunc.IIF(
                                item.AttemptCount < int.MaxValue,
                                item.AttemptCount + 1,
                                int.MaxValue
                            )
                        )
                        .SetColumns(item => item.LeaseOwner == owner)
                        .SetColumns(item => item.LeaseToken == leaseToken)
                        .SetColumns(item => item.LeaseExpiresAtUtc == leaseExpires)
                        .SetColumns(item => item.LastAttemptAtUtc == now)
                        .SetColumns(item => item.UpdatedAt == now)
                        .Where(item =>
                            item.Id == candidate.Id
                            && (
                                (
                                    (
                                        item.Status == ProductHqSyncOutboxStatuses.Pending
                                        || item.Status == ProductHqSyncOutboxStatuses.Retrying
                                    )
                                    && item.NextAttemptAtUtc <= now
                                )
                                || (
                                    item.Status == ProductHqSyncOutboxStatuses.Processing
                                    && item.LeaseExpiresAtUtc != null
                                    && item.LeaseExpiresAtUtc <= now
                                )
                            )
                        )
                        .ExecuteCommandAsync(cancellationToken);
                    if (claimed == 0)
                    {
                        continue;
                    }

                    var row = await db.Queryable<ProductHqSyncOutbox>()
                        .Where(item => item.Id == candidate.Id && item.LeaseToken == leaseToken)
                        .FirstAsync(cancellationToken);
                    if (row != null)
                    {
                        try
                        {
                            var workItem = ProductHqSyncOutboxQueue.ToWorkItem(row);
                            lockTransferred = true;
                            return new ProductHqSyncOutboxExecutionClaim(workItem, executionLock);
                        }
                        catch (JsonException ex)
                        {
                            logger?.LogError(
                                ex,
                                "商品 HQ outbox 持久化数据无效，已阻断任务: OperationId={OperationId}",
                                row.OperationKey
                            );
                            var blocked = await BlockInvalidPayloadAsync(
                                db,
                                row,
                                now,
                                cancellationToken
                            );
                            if (!blocked)
                            {
                                logger?.LogWarning(
                                    "商品 HQ outbox 无效数据阻断被 fencing token 拒绝: OperationId={OperationId}",
                                    row.OperationKey
                                );
                            }
                        }
                    }
                }
                finally
                {
                    if (!lockTransferred)
                    {
                        await executionLock.DisposeAsync();
                    }
                }
            }

            if (candidates.Count < pageSize)
            {
                return null;
            }

            var lastCandidate = candidates[^1];
            afterCreatedAt = lastCandidate.CreatedAt;
            afterNextAttemptAtUtc = lastCandidate.NextAttemptAtUtc;
            afterId = lastCandidate.Id;
        }
    }

    private static async Task<bool> BlockInvalidPayloadAsync(
        ISqlSugarClient db,
        ProductHqSyncOutbox row,
        DateTime utcNow,
        CancellationToken cancellationToken
    )
    {
        var updated = await db.Updateable<ProductHqSyncOutbox>()
            .SetColumns(item => new ProductHqSyncOutbox
            {
                Status = ProductHqSyncOutboxStatuses.Blocked,
                NextAttemptAtUtc = utcNow,
                LeaseOwner = null,
                LeaseToken = null,
                LeaseExpiresAtUtc = null,
                CompletedAtUtc = utcNow,
                LastErrorCode = "PRODUCT_HQ_SYNC_OUTBOX_PAYLOAD_INVALID",
                LastErrorMessage = "HQ 同步任务数据无效，需要人工处理",
                UpdatedAt = utcNow,
                UpdatedBy = "product-hq-sync-worker",
            })
            .Where(item =>
                item.Id == row.Id
                && item.Status == ProductHqSyncOutboxStatuses.Processing
                && item.LeaseOwner == row.LeaseOwner
                && item.LeaseToken == row.LeaseToken
            )
            .ExecuteCommandAsync(cancellationToken);
        return updated == 1;
    }

    internal static Task<int> RenewLeaseAsync(
        ISqlSugarClient db,
        ProductHqSyncOutboxWorkItemDto workItem,
        DateTime utcNow,
        ProductHqSyncOutboxOptions options,
        CancellationToken cancellationToken = default
    )
    {
        var now = AsUtc(utcNow);
        var leaseExpires = now.AddSeconds(Math.Clamp(options.LeaseSeconds, 10, 3600));
        return db.Updateable<ProductHqSyncOutbox>()
            .SetColumns(item => item.LeaseExpiresAtUtc == leaseExpires)
            .SetColumns(item => item.UpdatedAt == now)
            .Where(item =>
                item.Id == workItem.OutboxId
                && item.Status == ProductHqSyncOutboxStatuses.Processing
                && item.LeaseOwner == workItem.LeaseOwner
                && item.LeaseToken == workItem.LeaseToken
            )
            .ExecuteCommandAsync(cancellationToken);
    }

    internal static async Task<bool> ApplyResultAsync(
        ISqlSugarClient db,
        ProductHqSyncOutboxWorkItemDto workItem,
        ProductHqSyncOutboxExecutionResult result,
        DateTime utcNow,
        ProductHqSyncOutboxOptions options,
        CancellationToken cancellationToken = default
    )
    {
        var now = AsUtc(utcNow);
        var status = result.Disposition switch
        {
            ProductHqSyncOutboxExecutionDisposition.Success =>
                ProductHqSyncOutboxStatuses.Succeeded,
            ProductHqSyncOutboxExecutionDisposition.Blocked =>
                ProductHqSyncOutboxStatuses.Blocked,
            _ => ProductHqSyncOutboxStatuses.Retrying,
        };
        var isTerminal = status is ProductHqSyncOutboxStatuses.Succeeded
            or ProductHqSyncOutboxStatuses.Blocked;
        var nextAttempt = status == ProductHqSyncOutboxStatuses.Retrying
            ? now.Add(Backoff(workItem.AttemptCount, options))
            : now;
        var safeErrorCode = result.Disposition == ProductHqSyncOutboxExecutionDisposition.Success
            ? null
            : NormalizeErrorCode(result.ErrorCode);
        var safeMessage = Normalize(
            result.Message,
            500,
            status == ProductHqSyncOutboxStatuses.Retrying
                ? "HQ 同步暂时失败，系统将自动重试"
                : status == ProductHqSyncOutboxStatuses.Blocked
                    ? "HQ 同步需要人工处理"
                    : "HQ 同步完成"
        );

        var updated = await db.Updateable<ProductHqSyncOutbox>()
            .SetColumns(item => new ProductHqSyncOutbox
            {
                Status = status,
                NextAttemptAtUtc = nextAttempt,
                LeaseOwner = null,
                LeaseToken = null,
                LeaseExpiresAtUtc = null,
                CompletedAtUtc = isTerminal ? now : null,
                LastErrorCode = safeErrorCode,
                LastErrorMessage = safeMessage,
                UpdatedAt = now,
            })
            .Where(item =>
                item.Id == workItem.OutboxId
                && item.Status == ProductHqSyncOutboxStatuses.Processing
                && item.LeaseOwner == workItem.LeaseOwner
                && item.LeaseToken == workItem.LeaseToken
            )
            .ExecuteCommandAsync(cancellationToken);
        return updated == 1;
    }

    internal static TimeSpan Backoff(int attemptCount, ProductHqSyncOutboxOptions options)
    {
        var baseSeconds = Math.Clamp(options.BaseRetryDelaySeconds, 1, 3600);
        var maxSeconds = Math.Clamp(options.MaxRetryDelaySeconds, baseSeconds, 3600);
        var exponent = Math.Clamp(attemptCount - 1, 0, 20);
        var seconds = Math.Min(maxSeconds, baseSeconds * Math.Pow(2, exponent));
        return TimeSpan.FromSeconds(seconds);
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static string Normalize(string? value, int maxLength, string fallback)
    {
        var normalized = value?.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            normalized = fallback;
        }
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static string NormalizeErrorCode(string? value)
    {
        var normalized = value?.Trim();
        if (
            string.IsNullOrEmpty(normalized)
            || normalized.Length > 120
            || normalized.Any(character =>
                !(character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')
            )
        )
        {
            return "PRODUCT_HQ_SYNC_FAILED";
        }

        return normalized;
    }
}

internal sealed class ProductHqSyncOutboxExecutionClaim : IAsyncDisposable
{
    private readonly IAsyncDisposable _executionLock;

    internal ProductHqSyncOutboxExecutionClaim(
        ProductHqSyncOutboxWorkItemDto workItem,
        IAsyncDisposable executionLock
    )
    {
        WorkItem = workItem;
        _executionLock = executionLock;
    }

    internal ProductHqSyncOutboxWorkItemDto WorkItem { get; }

    public ValueTask DisposeAsync() => _executionLock.DisposeAsync();
}

/// <summary>
/// 在整个 HQ 外部写入期间持有的商品级执行锁。SQL Server 使用独立连接的 session lock，
/// 因此 outbox 短租约续期失败不会让另一实例并发进入同一商品的执行器。
/// </summary>
internal sealed class ProductHqSyncProductExecutionLock : IAsyncDisposable
{
    internal const string SqlServerAcquireSql = """
        DECLARE @Result int;
        EXEC @Result = sys.sp_getapplock
            @Resource = @Resource,
            @LockMode = N'Exclusive',
            @LockOwner = N'Session',
            @LockTimeout = 0;
        SELECT @Result;
        """;

    private const string SqlServerReleaseSql = """
        DECLARE @Result int;
        EXEC @Result = sys.sp_releaseapplock
            @Resource = @Resource,
            @LockOwner = N'Session';
        SELECT @Result;
        """;

    private static readonly SemaphoreSlim NonSqlServerGate = new(1, 1);
    private readonly string? _resource;
    private readonly SqlConnection? _connection;
    private readonly SemaphoreSlim? _semaphore;
    private bool _disposed;

    private ProductHqSyncProductExecutionLock(
        string? resource,
        SqlConnection? connection,
        SemaphoreSlim? semaphore
    )
    {
        _resource = resource;
        _connection = connection;
        _semaphore = semaphore;
    }

    internal static async Task<ProductHqSyncProductExecutionLock?> TryAcquireAsync(
        ISqlSugarClient db,
        string productCode,
        CancellationToken cancellationToken
    )
    {
        if (db.CurrentConnectionConfig.DbType != SqlSugar.DbType.SqlServer)
        {
            var acquired = await NonSqlServerGate.WaitAsync(0, cancellationToken);
            return acquired
                ? new ProductHqSyncProductExecutionLock(null, null, NonSqlServerGate)
                : null;
        }

        var resource = BuildResource(productCode);
        var connection = new SqlConnection(db.CurrentConnectionConfig.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = CreateCommand(
                connection,
                SqlServerAcquireSql,
                resource,
                Math.Clamp(db.Ado.CommandTimeOut, 1, 30)
            );
            var resultValue = await command.ExecuteScalarAsync(cancellationToken);
            var result = resultValue is null or DBNull
                ? -999
                : Convert.ToInt32(resultValue, CultureInfo.InvariantCulture);
            if (result < 0)
            {
                await connection.DisposeAsync();
                return null;
            }

            return new ProductHqSyncProductExecutionLock(resource, connection, null);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_semaphore != null)
        {
            _semaphore.Release();
            return;
        }

        if (_connection == null || _resource == null)
        {
            return;
        }

        try
        {
            if (_connection.State == ConnectionState.Open)
            {
                await using var command = CreateCommand(
                    _connection,
                    SqlServerReleaseSql,
                    _resource,
                    commandTimeoutSeconds: 5
                );
                var resultValue = await command.ExecuteScalarAsync(CancellationToken.None);
                var result = resultValue is null or DBNull
                    ? -999
                    : Convert.ToInt32(resultValue, CultureInfo.InvariantCulture);
                if (result < 0)
                {
                    SqlConnection.ClearPool(_connection);
                }
            }
        }
        catch
        {
            // 未确认释放 session lock 的物理连接绝不能回到连接池。
            SqlConnection.ClearPool(_connection);
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }

    internal static string BuildResource(string productCode)
    {
        var normalized = productCode.Trim().ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32];
        return $"ProductHqSyncOutbox:Execute:{hash}";
    }

    private static SqlCommand CreateCommand(
        SqlConnection connection,
        string sql,
        string resource,
        int commandTimeoutSeconds
    )
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = commandTimeoutSeconds;
        command.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value = resource;
        return command;
    }
}
