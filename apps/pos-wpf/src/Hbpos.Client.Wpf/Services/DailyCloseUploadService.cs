using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Hbpos.Contracts.DailyClose;
using Hbpos.Contracts.Orders;
using Microsoft.Extensions.Hosting;

namespace Hbpos.Client.Wpf.Services;

public interface IDailyCloseSyncApiClient
{
    Task<DailyCloseSyncResponse> SyncAsync(
        DailyCloseSyncRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class DailyCloseUploadApiException(
    string message,
    HttpStatusCode statusCode,
    string? errorCode = null) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string? ErrorCode { get; } = errorCode;
}

/// <summary>
/// POST api/v1/daily-closes/sync。HttpClient 由 ServiceRegistration 配置设备授权 handler（Bearer + 设备头），
/// 与 Linkly 结算上传使用同一套授权；服务端错误体统一是 { code, message }。
/// </summary>
public sealed class DailyCloseSyncApiClient(HttpClient httpClient) : IDailyCloseSyncApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DailyCloseSyncResponse> SyncAsync(
        DailyCloseSyncRequest request,
        CancellationToken cancellationToken = default)
    {
        const string requestPath = "api/v1/daily-closes/sync";
        using var response = await httpClient.PostAsJsonAsync(requestPath, request, JsonOptions, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var (errorCode, message) = ReadError(content);
            throw new DailyCloseUploadApiException(
                message ?? $"Daily close sync failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode,
                errorCode);
        }

        var result = JsonSerializer.Deserialize<DailyCloseSyncResponse>(content, JsonOptions);
        return result ?? throw new DailyCloseUploadApiException(
            "Daily close sync returned an empty response.",
            response.StatusCode,
            "EMPTY_SYNC_RESPONSE");
    }

    private static (string? ErrorCode, string? Message) ReadError(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var errorCode = root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : null;
            var message = root.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;
            return (errorCode, message);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}

public interface IDailyCloseUploadExecutionService
{
    Task<DailyCloseUploadExecutionResult> ExecutePendingAsync(
        int batchSize = 20,
        CancellationToken cancellationToken = default);
}

public interface IDailyCloseUploadScheduler
{
    /// <summary>唤醒上传 Worker（保存日结后调用）；多次调用会合并，不会堆积。</summary>
    void RequestUpload();
}

public sealed record DailyCloseUploadExecutionResult(
    int AttemptedCount,
    int UploadedCount,
    int FailedCount,
    int DeferredCount,
    bool WasInterrupted);

/// <summary>本地日结存档 → 上传请求的映射。不修正任何数据：本地行不满足服务端校验时由服务端 400 永久拒绝。</summary>
internal static class DailyCloseSyncRequestMapper
{
    // 与服务端 AppVersion 的长度上限一致；超长会被服务端 400 拒绝，所以这里截断。
    internal const int MaximumAppVersionLength = 64;

    internal static DailyCloseSyncRequest ToRequest(DailyCloseArchive archive, string? appVersion)
    {
        var report = archive.Report;
        return new DailyCloseSyncRequest(
            DailyCloseContractConstants.SchemaVersion,
            archive.DailyCloseGuid,
            report.StoreCode,
            report.DeviceCode,
            DailyCloseContractConstants.ClientKindWpf,
            DateOnly.FromDateTime(report.BusinessDate),
            report.PeriodFrom,
            report.PeriodTo,
            archive.SavedAt,
            report.CashierId,
            report.CashierName,
            NormalizeAppVersion(appVersion),
            report.OrderCount,
            report.ReturnQuantity,
            report.RefundAmount,
            // 三种支付方式恰好各一条；日结存档不保存各支付方式的笔数，所以不发。
            [
                ToTender(report, PaymentMethodKind.Cash, DailyCloseContractConstants.TenderCash),
                ToTender(report, PaymentMethodKind.Card, DailyCloseContractConstants.TenderCard),
                ToTender(report, PaymentMethodKind.Voucher, DailyCloseContractConstants.TenderVoucher)
            ],
            archive.CashCounts
                .Select(count => new DailyCloseCashCountSync(ToCents(count.Value), count.Quantity))
                .ToList(),
            archive.NoteSubtotal,
            archive.CoinSubtotal,
            archive.CountedCashAmount,
            archive.CashDifference);
    }

    /// <summary>本地面额是以元为单位的 decimal（如 100、0.05），换算成分用 decimal 乘 100 取整，避免浮点误差。</summary>
    internal static int ToCents(decimal denominationValue)
    {
        return (int)decimal.Round(denominationValue * 100m, 0, MidpointRounding.AwayFromZero);
    }

    internal static string? NormalizeAppVersion(string? appVersion)
    {
        if (string.IsNullOrWhiteSpace(appVersion))
        {
            return null;
        }

        var normalized = appVersion.Trim();
        return normalized.Length <= MaximumAppVersionLength ? normalized : normalized[..MaximumAppVersionLength];
    }

    private static DailyCloseTenderSync ToTender(DailyCloseReport report, PaymentMethodKind method, string contractMethod)
    {
        var summary = report.PaymentSummaries.FirstOrDefault(item => item.Method == method);
        return new DailyCloseTenderSync(
            contractMethod,
            summary?.SalesAmount ?? 0m,
            summary?.RefundAmount ?? 0m,
            summary?.NetAmount ?? 0m);
    }
}

/// <summary>
/// 日结记录上传：把本机尚未同步的日结存档逐条上传到服务端。
/// 新保存的日结与旧库升级后的全部历史日结都是 Pending，由同一条路径补传；历史行含完整面额与支付方式，
/// 会覆盖服务端按审计事件回填的占位记录。
/// </summary>
public sealed class DailyCloseUploadService(
    ILocalDailyCloseUploadRepository repository,
    IDailyCloseSyncApiClient apiClient,
    DeviceAuthorizationState authorizationState,
    TimeProvider? timeProvider = null,
    string? appVersion = null) : IDailyCloseUploadExecutionService
{
    internal static readonly TimeSpan UploadLeaseTimeout = TimeSpan.FromMinutes(2);

    // 复用客户端日志身份里取程序集 InformationalVersion 的写法，不再另写一份。
    private readonly string? resolvedAppVersion = appVersion ?? ClientLogIdentity.CreateCurrent().AppVersion;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<DailyCloseUploadExecutionResult> ExecutePendingAsync(
        int batchSize = 20,
        CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(batchSize, 1, 100);
        var now = clock.GetUtcNow();
        await repository.RecoverExpiredUploadingAsync(now - UploadLeaseTimeout, now, cancellationToken);

        var attempted = 0;
        var uploaded = 0;
        var failed = 0;
        var deferred = 0;
        // 本次执行里已经处理过的记录：失败的记录会带退避时间离开到期队列，这里再兜底防止
        // 认领阶段异常（例如 SQLite 瞬时锁）让同一条记录反复出现在"取到期"结果里造成死循环。
        var handled = new HashSet<Guid>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 每轮重新读取当前设备授权范围：没有授权（尚未注册/正在重新注册）时不上传，也不消耗尝试次数。
            var scope = authorizationState.Current;
            if (scope is null || string.IsNullOrWhiteSpace(scope.StoreCode) || string.IsNullOrWhiteSpace(scope.DeviceCode))
            {
                break;
            }

            var dueGuids = await repository.GetDueUploadGuidsAsync(
                scope.StoreCode,
                scope.DeviceCode,
                pageSize,
                clock.GetUtcNow(),
                cancellationToken);
            var batch = dueGuids.Where(handled.Add).ToList();
            if (batch.Count == 0)
            {
                // 取空（或剩下的都已处理过）即本轮结束。
                break;
            }

            foreach (var dailyCloseGuid in batch)
            {
                // 每条记录独立处理：一条出异常只记日志并计入"延后"，不影响其余记录。
                DailyCloseUploadExecutionResult result;
                try
                {
                    result = await ExecuteClaimedAsync(dailyCloseGuid, scope, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    ConsoleLog.WriteError(
                        "DailyCloseUpload",
                        $"upload one daily close failed dailyCloseGuid={dailyCloseGuid:D} error={ex.GetType().Name} message={ex.Message}",
                        exception: ex);
                    deferred++;
                    continue;
                }

                attempted += result.AttemptedCount;
                uploaded += result.UploadedCount;
                failed += result.FailedCount;
                deferred += result.DeferredCount;
                if (result.WasInterrupted)
                {
                    // 设备授权问题（401/403）：后面的记录必然同样失败，立即中断本批次。
                    return new DailyCloseUploadExecutionResult(attempted, uploaded, failed, deferred, true);
                }
            }
        }

        return new DailyCloseUploadExecutionResult(attempted, uploaded, failed, deferred, false);
    }

    private async Task<DailyCloseUploadExecutionResult> ExecuteClaimedAsync(
        Guid dailyCloseGuid,
        DeviceAuthorizationContext scope,
        CancellationToken cancellationToken)
    {
        var lease = await repository.TryClaimUploadAsync(
            dailyCloseGuid,
            scope.StoreCode,
            scope.DeviceCode,
            clock.GetUtcNow(),
            cancellationToken);
        if (lease is null)
        {
            // 没抢到（别处已认领/尚未到期/范围不符）：不是失败，也不算一次尝试。
            return new DailyCloseUploadExecutionResult(0, 0, 0, 0, false);
        }

        try
        {
            var archive = await repository.GetArchiveForUploadAsync(dailyCloseGuid, cancellationToken);
            if (archive is null)
            {
                // 记录在认领后被清除（例如测试数据重置）：没有可上传的内容。
                return new DailyCloseUploadExecutionResult(0, 0, 0, 0, false);
            }

            var response = await apiClient.SyncAsync(
                DailyCloseSyncRequestMapper.ToRequest(archive, resolvedAppVersion),
                cancellationToken);
            if (!response.Accepted && !response.AlreadySynced && !response.ReplacedPlaceholder)
            {
                // 200 但没有任何"已收下"标志：响应不可信（服务端契约里 Accepted 恒为 true），
                // 保守地退避重试，而不是把记录永久标成拒绝。
                await MarkPendingAsync(lease, "SYNC_NOT_ACCEPTED", "The server did not accept the daily close sync request.");
                return new DailyCloseUploadExecutionResult(1, 0, 0, 1, false);
            }

            // Accepted / AlreadySynced / ReplacedPlaceholder 都表示服务端已持有这条日结。
            await repository.MarkUploadSucceededAsync(dailyCloseGuid, clock.GetUtcNow(), CancellationToken.None);
            return new DailyCloseUploadExecutionResult(1, 1, 0, 0, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient 超时与端点切换取消都保持 Pending 并退避。
            await MarkPendingAsync(lease, "REQUEST_CANCELED", "The daily close sync request was canceled or timed out.");
            return new DailyCloseUploadExecutionResult(1, 0, 0, 1, false);
        }
        catch (DailyCloseUploadApiException ex) when (IsRetryableConflict(ex))
        {
            await MarkPendingAsync(lease, ex.ErrorCode!, TrimError(ex.Message));
            return new DailyCloseUploadExecutionResult(1, 0, 0, 1, false);
        }
        catch (DailyCloseUploadApiException ex) when (
            ex.StatusCode is
                HttpStatusCode.BadRequest or
                HttpStatusCode.Conflict or
                HttpStatusCode.RequestEntityTooLarge or
                HttpStatusCode.UnprocessableEntity)
        {
            // 数据本身被服务端判定无效或冲突：重试不会改变结果，永久拒绝并记录原因。
            await repository.MarkUploadRejectedAsync(
                dailyCloseGuid,
                ex.ErrorCode ?? $"HTTP_{(int)ex.StatusCode}",
                TrimError(ex.Message),
                CancellationToken.None);
            return new DailyCloseUploadExecutionResult(1, 0, 1, 0, false);
        }
        catch (DailyCloseUploadApiException ex) when (
            ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // 设备授权问题不是这条记录的错：保持 Pending、撤销本次尝试计数，并中断整个批次。
            await repository.ReleaseUploadWithoutAttemptAsync(
                dailyCloseGuid,
                ex.ErrorCode ?? $"HTTP_{(int)ex.StatusCode}",
                TrimError(ex.Message),
                CancellationToken.None);
            return new DailyCloseUploadExecutionResult(1, 0, 0, 1, true);
        }
        catch (DailyCloseUploadApiException ex)
        {
            // 5xx、408、429，以及服务端尚未部署接口时的 404 等：退避重试。
            await MarkPendingAsync(lease, ex.ErrorCode ?? $"HTTP_{(int)ex.StatusCode}", TrimError(ex.Message));
            return new DailyCloseUploadExecutionResult(1, 0, 0, 1, false);
        }
        catch (HttpRequestException ex)
        {
            await MarkPendingAsync(lease, "NETWORK", TrimError(ex.Message));
            return new DailyCloseUploadExecutionResult(1, 0, 0, 1, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 上传异常必须保持在后台重试，不得影响收银流程。
            await MarkPendingAsync(lease, "UPLOAD_EXCEPTION", TrimError(ex.Message));
            return new DailyCloseUploadExecutionResult(1, 0, 0, 1, false);
        }
    }

    /// <summary>退避 5s→10s→…→300s（与 Linkly 结算上传同一公式），按累计尝试次数增长。</summary>
    internal static int GetRetryDelaySeconds(int attemptCount)
    {
        return Math.Min(300, 5 * (1 << Math.Min(Math.Max(attemptCount - 1, 0), 6)));
    }

    private async Task MarkPendingAsync(
        LocalDailyCloseUploadLease lease,
        string errorCode,
        string errorMessage)
    {
        var delaySeconds = GetRetryDelaySeconds(lease.UploadAttemptCount);
        await repository.MarkUploadPendingAsync(
            lease.DailyCloseGuid,
            clock.GetUtcNow().AddSeconds(delaySeconds),
            errorCode,
            errorMessage,
            CancellationToken.None);
    }

    private static string TrimError(string? message)
    {
        return string.IsNullOrWhiteSpace(message)
            ? "Daily close sync failed."
            : message.Length <= 512 ? message : message[..512];
    }

    private static bool IsRetryableConflict(DailyCloseUploadApiException exception)
    {
        // 服务端并发更新冲突是暂时的，重试即可；其它 409（范围冲突、内容冲突）是永久性的。
        return exception.StatusCode == HttpStatusCode.Conflict &&
            string.Equals(exception.ErrorCode, "DAILY_CLOSE_SYNC_CONCURRENT_UPDATE", StringComparison.Ordinal);
    }
}

public sealed class DailyCloseUploadWorker(
    LocalSchemaService schemaService,
    IDailyCloseUploadExecutionService executionService) : IHostedService, IDailyCloseUploadScheduler, IDisposable
{
    private readonly SemaphoreSlim signal = new(0, 1);
    private CancellationTokenSource? stopping;
    private Task? executionLoop;

    // 与 Linkly 结算上传 Worker 一致：在 StartAsync 里自己启动循环，不依赖 BackgroundService.ExecuteAsync
    // （Hosting 10 下 ExecuteAsync 可能不被执行）。
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        executionLoop = RunAsync(stopping.Token);
        RequestUpload();
        return Task.CompletedTask;
    }

    public void RequestUpload()
    {
        try
        {
            signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // 已有唤醒信号时合并，避免多次保存日结造成后台任务堆积。
        }
        catch (ObjectDisposedException)
        {
            // Worker 已随 Host 释放：唤醒是尽力而为，不能向调用方（保存日结）抛异常。
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (stopping is null || executionLoop is null)
        {
            return;
        }

        stopping.Cancel();
        RequestUpload();
        await Task.WhenAny(executionLoop, Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
    }

    public void Dispose()
    {
        stopping?.Dispose();
        signal.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 主启动流程完成同一 LocalSchemaService 实例的初始化后才开放上传，避免并发迁移 SQLite schema
            // （旧库补列也在这一步完成，Worker 之前不会读到缺列的表）。
            await schemaService.WaitUntilReadyAsync(cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // 信号唤醒 + 30 秒轮询：设备授权就绪、退避到期、网络恢复都靠轮询捡起。
                    await signal.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                    await executionService.ExecutePendingAsync(cancellationToken: cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // SQLite 锁或瞬时网络异常不能终止工作器；下一轮信号或轮询继续补传。
                    ConsoleLog.WriteError(
                        "DailyCloseUpload",
                        $"upload worker iteration failed error={ex.GetType().Name} message={ex.Message}",
                        exception: ex);
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host 正常停止。
        }
        catch (Exception ex)
        {
            // 工作器仅记录退出原因；下一次应用启动会回收过期租约并继续补传。
            ConsoleLog.WriteError(
                "DailyCloseUpload",
                $"upload worker stopped error={ex.GetType().Name} message={ex.Message}",
                exception: ex);
        }
    }
}
