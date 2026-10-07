using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Hbpos.Contracts.Linkly;
using Microsoft.Extensions.Hosting;

namespace Hbpos.Client.Wpf.Services;

public interface ILinklySettlementSyncApiClient
{
    Task<LinklySettlementSyncResponse> SyncAsync(
        LinklySettlementSyncRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class LinklySettlementUploadApiException(
    string message,
    HttpStatusCode statusCode,
    string? errorCode = null) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string? ErrorCode { get; } = errorCode;
}

public sealed class LinklySettlementSyncApiClient(HttpClient httpClient) : ILinklySettlementSyncApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<LinklySettlementSyncResponse> SyncAsync(
        LinklySettlementSyncRequest request,
        CancellationToken cancellationToken = default)
    {
        const string requestPath = "api/v1/linkly/settlements/sync";
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage sentResponse;
        try
        {
            sentResponse = await httpClient.PostAsJsonAsync(requestPath, request, JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // 传输层细节（耗时、超时/断网）记 Information；是否告警由上传服务按退避节流决定，避免同一次失败两条 Warning。
            var reason = ex is HttpRequestException
                ? "network"
                : cancellationToken.IsCancellationRequested ? "cancelled" : "timeout";
            LogSync(
                request,
                $"Linkly settlement sync request failed settlementGuid={request.SettlementGuid:D} reason={reason} elapsedMs={stopwatch.ElapsedMilliseconds}",
                statusCode: null,
                errorCode: null,
                stopwatch.ElapsedMilliseconds);
            throw;
        }

        using var response = sentResponse;
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        stopwatch.Stop();
        if (!response.IsSuccessStatusCode)
        {
            var (errorCode, message) = ReadError(content);
            LogSync(
                request,
                $"Linkly settlement sync response settlementGuid={request.SettlementGuid:D} revision={request.ClientRevision} " +
                $"http={(int)response.StatusCode} errorCode={errorCode ?? "<none>"} elapsedMs={stopwatch.ElapsedMilliseconds} " +
                $"body={Truncate(content, 256)}",
                (int)response.StatusCode,
                errorCode,
                stopwatch.ElapsedMilliseconds);
            throw new LinklySettlementUploadApiException(
                message ?? $"Linkly settlement sync failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode,
                errorCode);
        }

        LogSync(
            request,
            $"Linkly settlement sync response settlementGuid={request.SettlementGuid:D} revision={request.ClientRevision} " +
            $"http={(int)response.StatusCode} elapsedMs={stopwatch.ElapsedMilliseconds}",
            (int)response.StatusCode,
            errorCode: null,
            stopwatch.ElapsedMilliseconds);
        var result = JsonSerializer.Deserialize<LinklySettlementSyncResponse>(content, JsonOptions);
        return result ?? throw new LinklySettlementUploadApiException(
            "Linkly settlement sync returned an empty response.",
            response.StatusCode,
            "EMPTY_SYNC_RESPONSE");
    }

    private static void LogSync(
        LinklySettlementSyncRequest request,
        string message,
        int? statusCode,
        string? errorCode,
        long elapsedMs)
    {
        ConsoleLog.WriteInformation(
            "LinklySettlementUpload",
            message,
            new ApplicationLogContext(
                TraceId: request.SettlementGuid.ToString("D"),
                RequestPath: "api/v1/linkly/settlements/sync",
                RequestMethod: "POST",
                StatusCode: statusCode,
                Properties: new Dictionary<string, object?>
                {
                    ["storeCode"] = request.StoreCode,
                    ["deviceCode"] = request.DeviceCode,
                    ["errorCode"] = errorCode,
                    ["elapsedMs"] = elapsedMs
                }));
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<empty>";
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
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
            var errorCode = root.TryGetProperty("code", out var code) ? code.GetString() : null;
            var message = root.TryGetProperty("message", out var text) ? text.GetString() : null;
            return (errorCode, message);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}

public interface ILinklySettlementUploadQueueReader
{
    Task<LinklySettlementUploadOverview> GetOverviewAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LinklySettlementUploadQueueItem>> GetActiveItemsAsync(
        int take = 20,
        CancellationToken cancellationToken = default);
}

public interface ILinklySettlementUploadExecutionService
{
    Task<LinklySettlementUploadExecutionResult> ExecutePendingAsync(
        int batchSize = 20,
        CancellationToken cancellationToken = default);

    Task<LinklySettlementUploadExecutionResult> ExecuteOneAsync(
        Guid settlementGuid,
        CancellationToken cancellationToken = default);
}

public interface ILinklySettlementUploadScheduler
{
    void RequestUpload();
}

public sealed record LinklySettlementUploadExecutionResult(
    int AttemptedCount,
    int UploadedCount,
    int FailedCount,
    int DeferredCount,
    bool WasInterrupted);

public sealed class LinklySettlementUploadService(
    ILocalLinklySettlementRepository settlementRepository,
    ILinklySettlementSyncApiClient apiClient,
    TimeProvider? timeProvider = null) :
    ILinklySettlementUploadQueueReader,
    ILinklySettlementUploadExecutionService
{
    internal static readonly TimeSpan UploadLeaseTimeout = TimeSpan.FromMinutes(2);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public Task<LinklySettlementUploadOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        return settlementRepository.GetUploadOverviewAsync(cancellationToken);
    }

    public Task<IReadOnlyList<LinklySettlementUploadQueueItem>> GetActiveItemsAsync(
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        return settlementRepository.GetActiveUploadItemsAsync(take, cancellationToken);
    }

    public async Task<LinklySettlementUploadExecutionResult> ExecutePendingAsync(
        int batchSize = 20,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        await settlementRepository.RecoverExpiredUploadingAsync(
            now - UploadLeaseTimeout,
            now,
            cancellationToken);
        var settlementGuids = await settlementRepository.GetDueUploadSettlementGuidsAsync(
            Math.Clamp(batchSize, 1, 100),
            now,
            cancellationToken);
        var attempted = 0;
        var uploaded = 0;
        var failed = 0;
        var deferred = 0;
        foreach (var settlementGuid in settlementGuids)
        {
            var result = await ExecuteClaimedAsync(settlementGuid, cancellationToken);
            attempted += result.AttemptedCount;
            uploaded += result.UploadedCount;
            failed += result.FailedCount;
            deferred += result.DeferredCount;
            if (result.WasInterrupted)
            {
                return new LinklySettlementUploadExecutionResult(attempted, uploaded, failed, deferred, true);
            }
        }

        return new LinklySettlementUploadExecutionResult(attempted, uploaded, failed, deferred, false);
    }

    public async Task<LinklySettlementUploadExecutionResult> ExecuteOneAsync(
        Guid settlementGuid,
        CancellationToken cancellationToken = default)
    {
        if (settlementGuid == Guid.Empty)
        {
            return new LinklySettlementUploadExecutionResult(0, 0, 0, 0, false);
        }

        // 手动重试强制 Pending/Rejected 记录立即到期，不改变银行结算结果或快照版本。
        await settlementRepository.ResetUploadForRetryAsync(settlementGuid, clock.GetUtcNow(), cancellationToken);
        return await ExecuteClaimedAsync(settlementGuid, cancellationToken);
    }

    private async Task<LinklySettlementUploadExecutionResult> ExecuteClaimedAsync(
        Guid settlementGuid,
        CancellationToken cancellationToken)
    {
        var attemptedAt = clock.GetUtcNow();
        var lease = await settlementRepository.TryClaimUploadAsync(settlementGuid, attemptedAt, cancellationToken);
        if (lease is null)
        {
            return new LinklySettlementUploadExecutionResult(0, 0, 0, 0, false);
        }

        try
        {
            var response = await apiClient.SyncAsync(ToRequest(lease), cancellationToken);
            if (!response.Accepted && !response.AlreadySynced)
            {
                await settlementRepository.MarkUploadRejectedAsync(
                    settlementGuid,
                    lease.PayloadRevision,
                    "SYNC_NOT_ACCEPTED",
                    "The server did not accept the Linkly settlement sync request.",
                    CancellationToken.None);
                LogRejected(lease, "SYNC_NOT_ACCEPTED", "The server did not accept the Linkly settlement sync request.", statusCode: 200);
                return new LinklySettlementUploadExecutionResult(1, 0, 1, 0, false);
            }

            var revisionAccepted = response.AcceptedRevision == lease.PayloadRevision
                || (response.AlreadySynced && response.AcceptedRevision > lease.PayloadRevision);
            if (!revisionAccepted)
            {
                await settlementRepository.MarkUploadRejectedAsync(
                    settlementGuid,
                    lease.PayloadRevision,
                    "SYNC_REVISION_MISMATCH",
                    "The server accepted a different Linkly settlement revision.",
                    CancellationToken.None);
                LogRejected(
                    lease,
                    "SYNC_REVISION_MISMATCH",
                    $"The server accepted revision {response.AcceptedRevision} instead.",
                    statusCode: 200);
                return new LinklySettlementUploadExecutionResult(1, 0, 1, 0, false);
            }

            await settlementRepository.MarkUploadSucceededAsync(
                settlementGuid,
                lease.PayloadRevision,
                clock.GetUtcNow(),
                CancellationToken.None);
            // 结算上传频率很低（每次结算一次），成功也记一条便于中心日志确认已补传。
            ConsoleLog.WriteInformation(
                "LinklySettlementUpload",
                $"Linkly settlement upload succeeded settlementGuid={settlementGuid:D} revision={lease.PayloadRevision} " +
                $"alreadySynced={response.AlreadySynced} attempt={lease.Settlement.UploadAttemptCount}",
                BuildContext(lease, statusCode: 200, errorCode: null));
            return new LinklySettlementUploadExecutionResult(1, 1, 0, 0, false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient 超时与端点代际取消都保持 Pending；handler 已阻止请求落到切换中的旧端点。
            await MarkPendingAsync(lease, "REQUEST_CANCELED", "The Linkly settlement sync request was canceled or timed out.", exception: ex);
            return new LinklySettlementUploadExecutionResult(1, 0, 0, 1, false);
        }
        catch (LinklySettlementUploadApiException ex) when (IsRetryableConflict(ex))
        {
            // 并发更新/会话未终态属于可预期的暂时状态，只记 Information（不告警）。
            await MarkPendingAsync(lease, ex.ErrorCode!, TrimError(ex.Message), (int)ex.StatusCode, expectedRetry: true);
            return new LinklySettlementUploadExecutionResult(1, 0, 0, 1, false);
        }
        catch (LinklySettlementUploadApiException ex) when (
            ex.StatusCode is
                HttpStatusCode.BadRequest or
                HttpStatusCode.Conflict or
                HttpStatusCode.RequestEntityTooLarge or
                HttpStatusCode.UnprocessableEntity)
        {
            await settlementRepository.MarkUploadRejectedAsync(
                settlementGuid,
                lease.PayloadRevision,
                ex.ErrorCode ?? $"HTTP_{(int)ex.StatusCode}",
                TrimError(ex.Message),
                CancellationToken.None);
            LogRejected(lease, ex.ErrorCode ?? $"HTTP_{(int)ex.StatusCode}", TrimError(ex.Message), (int)ex.StatusCode);
            return new LinklySettlementUploadExecutionResult(1, 0, 1, 0, false);
        }
        catch (LinklySettlementUploadApiException ex) when (
            ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // 设备授权问题会中断整批上传；属于配置/授权状态，按规范记 Warning（不节流），不记 Error。
            await MarkPendingAsync(
                lease,
                ex.ErrorCode ?? $"HTTP_{(int)ex.StatusCode}",
                TrimError(ex.Message),
                (int)ex.StatusCode,
                ex,
                alwaysWarn: true,
                note: "batchInterrupted=true reason=device-authorization");
            return new LinklySettlementUploadExecutionResult(1, 0, 0, 1, true);
        }
        catch (LinklySettlementUploadApiException ex)
        {
            await MarkPendingAsync(lease, ex.ErrorCode ?? $"HTTP_{(int)ex.StatusCode}", TrimError(ex.Message), (int)ex.StatusCode, ex);
            return new LinklySettlementUploadExecutionResult(1, 0, 0, 1, false);
        }
        catch (HttpRequestException ex)
        {
            await MarkPendingAsync(lease, "NETWORK", TrimError(ex.Message), exception: ex);
            return new LinklySettlementUploadExecutionResult(1, 0, 0, 1, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 上传异常必须保持在后台重试，不得阻断结算或打印流程。
            await MarkPendingAsync(lease, "UPLOAD_EXCEPTION", TrimError(ex.Message), exception: ex);
            return new LinklySettlementUploadExecutionResult(1, 0, 0, 1, false);
        }
    }

    /// <summary>
    /// 被标成 Rejected 的结算不会再自动重试（只能在 Sync Center 手动重试），必须在中心日志留下 Warning，
    /// 否则只有到设备上看 Sync Center 才会发现。GUID 放 TraceId、修订号写进消息（属性有白名单，放不进去）。
    /// </summary>
    private static void LogRejected(
        LocalLinklySettlementUploadLease lease,
        string errorCode,
        string errorMessage,
        int statusCode)
    {
        var settlement = lease.Settlement;
        ConsoleLog.WriteWarning(
            "LinklySettlementUpload",
            $"Linkly settlement upload rejected settlementGuid={settlement.SettlementGuid:D} revision={lease.PayloadRevision} " +
            $"errorCode={errorCode} http={statusCode} message={errorMessage}",
            new ApplicationLogContext(
                TraceId: settlement.SettlementGuid.ToString("D"),
                RequestPath: "api/v1/linkly/settlements/sync",
                RequestMethod: "POST",
                StatusCode: statusCode,
                Properties: new Dictionary<string, object?>
                {
                    ["storeCode"] = settlement.StoreCode,
                    ["deviceCode"] = settlement.DeviceCode,
                    ["errorCode"] = errorCode,
                    ["status"] = settlement.Status.ToString()
                }));
    }

    private async Task MarkPendingAsync(
        LocalLinklySettlementUploadLease lease,
        string errorCode,
        string errorMessage,
        int? statusCode = null,
        Exception? exception = null,
        bool expectedRetry = false,
        bool alwaysWarn = false,
        string? note = null)
    {
        var delaySeconds = Math.Min(300, 5 * (1 << Math.Min(lease.Settlement.UploadAttemptCount - 1, 6)));
        await settlementRepository.MarkUploadPendingAsync(
            lease.Settlement.SettlementGuid,
            lease.PayloadRevision,
            clock.GetUtcNow().AddSeconds(delaySeconds),
            errorCode,
            errorMessage,
            CancellationToken.None);
        LogDeferred(lease, errorCode, errorMessage, statusCode, delaySeconds, exception, expectedRetry, alwaysWarn, note);
    }

    /// <summary>
    /// 记录"本次上传延后重试"。反刷屏：第 1、2、4、8… 次失败记 Warning，其余重试与可预期的并发冲突只记 Information；
    /// 设备授权问题（401/403）每次都记 Warning。
    /// </summary>
    private static void LogDeferred(
        LocalLinklySettlementUploadLease lease,
        string errorCode,
        string errorMessage,
        int? statusCode,
        int delaySeconds,
        Exception? exception,
        bool expectedRetry,
        bool alwaysWarn,
        string? note)
    {
        var settlement = lease.Settlement;
        var attempt = settlement.UploadAttemptCount;
        var message =
            $"Linkly settlement upload deferred settlementGuid={settlement.SettlementGuid:D} revision={lease.PayloadRevision} " +
            $"errorCode={errorCode} http={statusCode?.ToString(CultureInfo.InvariantCulture) ?? "<none>"} attempt={attempt} " +
            $"nextRetrySeconds={delaySeconds}{(note is null ? string.Empty : " " + note)} message={Truncate(errorMessage, 256)}";
        var context = BuildContext(lease, statusCode, errorCode, attempt, delaySeconds);
        var warn = alwaysWarn || (!expectedRetry && attempt > 0 && (attempt & (attempt - 1)) == 0);
        if (warn)
        {
            ConsoleLog.WriteWarning("LinklySettlementUpload", message, context, exception);
        }
        else
        {
            ConsoleLog.WriteInformation("LinklySettlementUpload", message, context);
        }
    }

    private static ApplicationLogContext BuildContext(
        LocalLinklySettlementUploadLease lease,
        int? statusCode,
        string? errorCode,
        int? attemptCount = null,
        int? nextRetrySeconds = null)
    {
        var settlement = lease.Settlement;
        return new ApplicationLogContext(
            TraceId: settlement.SettlementGuid.ToString("D"),
            RequestPath: "api/v1/linkly/settlements/sync",
            RequestMethod: "POST",
            StatusCode: statusCode,
            Properties: new Dictionary<string, object?>
            {
                ["storeCode"] = settlement.StoreCode,
                ["deviceCode"] = settlement.DeviceCode,
                ["errorCode"] = errorCode,
                ["status"] = settlement.Status.ToString(),
                ["attemptCount"] = attemptCount ?? settlement.UploadAttemptCount,
                ["nextRetrySeconds"] = nextRetrySeconds
            });
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<empty>";
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static LinklySettlementSyncRequest ToRequest(LocalLinklySettlementUploadLease lease)
    {
        var settlement = lease.Settlement;
        return new LinklySettlementSyncRequest(
            SchemaVersion: 1,
            settlement.SettlementGuid,
            settlement.StoreCode,
            settlement.DeviceCode,
            DateOnly.FromDateTime(settlement.BusinessDate),
            settlement.ConnectionMode,
            settlement.Environment,
            settlement.ProviderSessionId,
            settlement.Status.ToString(),
            settlement.ResponseCode,
            settlement.ResponseText,
            settlement.SettlementData,
            settlement.ReceiptTexts,
            settlement.RequestedAt,
            settlement.CompletedAt,
            settlement.FirstPrintedAt,
            settlement.LastPrintedAt,
            settlement.PrintCount,
            settlement.LastPrintError,
            lease.PayloadRevision,
            settlement.ProviderSubmissionState);
    }

    private static string TrimError(string? message)
    {
        return string.IsNullOrWhiteSpace(message)
            ? "Linkly settlement sync failed."
            : message.Length <= 512 ? message : message[..512];
    }

    private static bool IsRetryableConflict(LinklySettlementUploadApiException exception)
    {
        return exception.StatusCode == HttpStatusCode.Conflict && exception.ErrorCode is
            "SETTLEMENT_SYNC_CONCURRENT_UPDATE" or
            "CLOUD_BACKEND_SESSION_NOT_FOUND" or
            "CLOUD_BACKEND_SESSION_NOT_FINAL";
    }
}

public sealed class LinklySettlementUploadWorker(
    LocalSchemaService schemaService,
    ILinklySettlementUploadExecutionService executionService) : IHostedService, ILinklySettlementUploadScheduler, IDisposable
{
    private readonly SemaphoreSlim signal = new(0, 1);
    private CancellationTokenSource? stopping;
    private Task? executionLoop;
    // 连续失败次数，仅用于日志节流：工作器每 30 秒一轮，持续失败时不能每轮都告警。
    private int consecutiveIterationFailures;

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
            // 已有唤醒信号时合并，避免多次结算造成后台任务堆积。
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
            // 主启动流程完成同一 LocalSchemaService 实例的初始化后才开放上传，避免并发迁移 SQLite schema。
            await schemaService.WaitUntilReadyAsync(cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await signal.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                    await executionService.ExecutePendingAsync(cancellationToken: cancellationToken);
                    if (consecutiveIterationFailures > 0)
                    {
                        ConsoleLog.WriteInformation(
                            "LinklySettlementUpload",
                            $"upload worker iteration recovered after failures={consecutiveIterationFailures}");
                        consecutiveIterationFailures = 0;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // SQLite 锁或瞬时网络异常不能终止工作器；下一轮信号或轮询继续补传。
                    consecutiveIterationFailures++;
                    var failures = consecutiveIterationFailures;
                    // 首次及第 2、4、8… 次连续失败才告警，其余只记 Information。
                    var message = $"upload worker iteration failed error={ex.GetType().Name} consecutiveFailures={failures}";
                    var context = new ApplicationLogContext(
                        Properties: new Dictionary<string, object?> { ["attemptCount"] = failures });
                    if ((failures & (failures - 1)) == 0)
                    {
                        ConsoleLog.WriteWarning("LinklySettlementUpload", message, context, ex);
                    }
                    else
                    {
                        ConsoleLog.WriteInformation("LinklySettlementUpload", message, context);
                    }

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
            // 工作器仅记录退出原因；下一次应用启动会重置 Uploading 并继续补传。
            ConsoleLog.WriteError(
                "LinklySettlementUpload",
                $"upload worker stopped error={ex.GetType().Name}",
                exception: ex);
        }
    }
}
