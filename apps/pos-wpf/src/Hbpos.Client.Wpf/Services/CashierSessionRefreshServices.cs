using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Hbpos.Contracts.Cashiers;
using Hbpos.Contracts.Common;
using Microsoft.Extensions.Hosting;

namespace Hbpos.Client.Wpf.Services;

public sealed record CashierSessionRefreshAttempt(
    CashierSessionDto? Session,
    bool IsApiUnavailable,
    bool IsOnlineRejected)
{
    public static CashierSessionRefreshAttempt Refreshed(CashierSessionDto session) =>
        new(session, false, false);

    public static CashierSessionRefreshAttempt ApiUnavailable() => new(null, true, false);

    public static CashierSessionRefreshAttempt OnlineRejected() => new(null, false, true);

    /// <summary>仅用于日志的诊断信息（HTTP 状态码、服务端 errorCode、失败原因、异常），不参与业务判定。</summary>
    public int? StatusCode { get; init; }

    public string? ErrorCode { get; init; }

    public string? Reason { get; init; }

    public Exception? Error { get; init; }
}

public interface ICashierSessionRefreshApiClient
{
    Task<CashierSessionRefreshAttempt> RefreshAsync(CancellationToken cancellationToken = default);
}

public sealed class CashierSessionRejectedEventArgs(CashierSessionDto rejectedSession) : EventArgs
{
    public CashierSessionDto RejectedSession { get; } = rejectedSession;
}

public sealed class CashierSessionRefreshApiClient(HttpClient httpClient)
    : ICashierSessionRefreshApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CashierSessionRefreshAttempt> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        int? statusCode = null;
        try
        {
            using var response = await httpClient.GetAsync("api/v1/cashiers/session", cancellationToken);
            statusCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                // 判定口径保持不变；只把状态码带出去供日志使用。
                return IsServiceUnavailable(response.StatusCode)
                    ? CashierSessionRefreshAttempt.ApiUnavailable() with { StatusCode = statusCode, Reason = "http-status" }
                    : CashierSessionRefreshAttempt.OnlineRejected() with
                    {
                        StatusCode = statusCode,
                        ErrorCode = await TryReadErrorCodeAsync(response, cancellationToken),
                        Reason = "http-status"
                    };
            }

            // 2xx 只有带 ApiResult 信封才是服务端结论：空对象、null、Wi-Fi 认证页 JSON 等不是 POS API 的响应，
            // 按不可用处理，绝不能据此踢下线并删除离线登录缓存（与 DeviceApiClient 口径一致）。
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(content);
            if (!DeviceApiClient.IsApiResultEnvelope(document.RootElement))
            {
                return CashierSessionRefreshAttempt.ApiUnavailable() with
                {
                    StatusCode = statusCode,
                    Reason = "not-api-envelope"
                };
            }

            var result = document.RootElement.Deserialize<ApiResult<CashierSessionDto>>(JsonOptions)!;
            if (!result.Success)
            {
                return CashierSessionRefreshAttempt.OnlineRejected() with
                {
                    StatusCode = statusCode,
                    ErrorCode = result.ErrorCode,
                    Reason = "success-false"
                };
            }

            // success=true 却没有会话数据属于形状异常，不是拒绝。
            return result.Data is not null
                ? CashierSessionRefreshAttempt.Refreshed(result.Data)
                : CashierSessionRefreshAttempt.ApiUnavailable() with
                {
                    StatusCode = statusCode,
                    Reason = "missing-session-data"
                };
        }
        catch (JsonException ex)
        {
            return CashierSessionRefreshAttempt.ApiUnavailable() with
            {
                StatusCode = statusCode,
                Reason = "unparsable-response",
                Error = ex
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return CashierSessionRefreshAttempt.ApiUnavailable() with
            {
                StatusCode = statusCode,
                Reason = ex is TaskCanceledException
                    ? cancellationToken.IsCancellationRequested ? "canceled" : "timeout"
                    : "network",
                Error = ex
            };
        }
    }

    /// <summary>被拒响应尽力读取 errorCode；正文不是 ApiResult、读取失败或被取消都忽略，确保被拒判定不变。</summary>
    private static async Task<string?> TryReadErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var failed = await response.Content.ReadFromJsonAsync<ApiResult<CashierSessionDto>>(cancellationToken);
            return failed?.ErrorCode;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException or InvalidOperationException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// 服务端 cashiers/session 只用 401（票据无效/已吊销/设备认证失败）和 403（设备越权）表达会话真实失效；
    /// 404/405 只会来自后端发布、网关切换或路由未就绪，与 DeviceApiClient.IsGatewayStatus 口径一致按不可用处理，
    /// 避免把门店收银员批量踢下线并清掉离线登录缓存。
    /// </summary>
    private static bool IsServiceUnavailable(HttpStatusCode statusCode)
    {
        var numericStatusCode = (int)statusCode;
        return numericStatusCode >= 500 ||
            statusCode is HttpStatusCode.NotFound
                or HttpStatusCode.MethodNotAllowed
                or HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests;
    }
}

public sealed class CashierSessionRefreshService(
    ICashierSessionRefreshApiClient apiClient,
    ICashierSessionContext sessionContext,
    ICashierSessionCacheUpdater cacheUpdater)
{
    // 服务是单例，60 秒刷新一次：不可用状态只在切换时记日志，用 lock 保护（刷新循环与界面触发可能并发）。
    private readonly object _availabilityLogGate = new();
    private bool _isUnavailableLogged;
    private int _unavailableAttempts;
    private DateTimeOffset _unavailableSinceUtc;

    public event EventHandler<CashierSessionRejectedEventArgs>? SessionRejected;

    public async Task RefreshOnceAsync(CancellationToken cancellationToken = default)
    {
        var currentSession = sessionContext.CurrentSession;
        if (currentSession is null || currentSession.IsEmergencyOverride)
        {
            return;
        }

        var attempt = await apiClient.RefreshAsync(cancellationToken);
        LogAvailabilityTransition(attempt, currentSession);
        if (attempt.Session is not null)
        {
            if (!ReferenceEquals(sessionContext.CurrentSession, currentSession))
            {
                return;
            }

            // 先原子替换加密缓存，再发布新快照；缓存失败时继续保留上一个有效会话。
            await cacheUpdater.UpdateCachedSessionAsync(attempt.Session, cancellationToken);
            if (!sessionContext.TrySetCurrent(currentSession, attempt.Session))
            {
                var newerSession = sessionContext.CurrentSession;
                if (newerSession is not null &&
                    !newerSession.IsEmergencyOverride &&
                    HasSameCacheIdentity(currentSession, newerSession))
                {
                    // 同一身份已重新登录时，旧响应可能刚覆盖同一个缓存键，必须写回新会话。
                    await cacheUpdater.UpdateCachedSessionAsync(newerSession, cancellationToken);
                }
            }
            return;
        }

        if (attempt.IsOnlineRejected)
        {
            // CAS 只清除被拒会话；缓存清理按票据版本执行，不会误删同身份的新登录缓存。
            var sessionCleared = sessionContext.TryClear(currentSession);
            // 被拒会踢下线并删除离线缓存，属于用户可感知的强动作，必须留痕（状态码写清楚，便于区分 401/403/404）。
            ConsoleLog.WriteWarning(
                "CashierSession",
                $"cashier session rejected by server; signing out and removing offline cache status={attempt.StatusCode?.ToString() ?? "-"} errorCode={attempt.ErrorCode ?? "-"} reason={attempt.Reason ?? "-"} cashierId={currentSession.CashierId} store={currentSession.StoreCode} device={currentSession.DeviceCode} sessionCleared={sessionCleared}",
                new ApplicationLogContext(
                    RequestPath: "api/v1/cashiers/session",
                    RequestMethod: "GET",
                    StatusCode: attempt.StatusCode,
                    UserId: currentSession.CashierId,
                    Properties: CreateLogProperties(currentSession, attempt.ErrorCode, attempt.Reason)));
            try
            {
                await cacheUpdater.RemoveCachedSessionAsync(currentSession, cancellationToken);
            }
            finally
            {
                if (sessionCleared)
                {
                    NotifySessionRejected(currentSession);
                }
            }
        }
    }

    private void LogAvailabilityTransition(CashierSessionRefreshAttempt attempt, CashierSessionDto currentSession)
    {
        // 调用方取消不算服务不可用。
        if (attempt.IsApiUnavailable && string.Equals(attempt.Reason, "canceled", StringComparison.Ordinal))
        {
            return;
        }

        string? message = null;
        var isWarning = false;
        long offlineSeconds = 0;
        int failedAttempts = 0;
        lock (_availabilityLogGate)
        {
            if (attempt.IsApiUnavailable)
            {
                _unavailableAttempts++;
                if (!_isUnavailableLogged)
                {
                    _isUnavailableLogged = true;
                    _unavailableSinceUtc = DateTimeOffset.UtcNow;
                    isWarning = true;
                    message = $"cashier session refresh unavailable; keeping last session snapshot status={attempt.StatusCode?.ToString() ?? "-"} reason={attempt.Reason ?? "-"} cashierId={currentSession.CashierId}";
                }
            }
            else if (_isUnavailableLogged)
            {
                // 恢复（刷新成功或得到明确拒绝）时记一次恢复日志，带离线时长与失败次数。
                offlineSeconds = (long)(DateTimeOffset.UtcNow - _unavailableSinceUtc).TotalSeconds;
                failedAttempts = _unavailableAttempts;
                _isUnavailableLogged = false;
                _unavailableAttempts = 0;
                message = $"cashier session refresh reachable again offlineSeconds={offlineSeconds} failedAttempts={failedAttempts} cashierId={currentSession.CashierId}";
            }
            else
            {
                _unavailableAttempts = 0;
            }
        }

        if (message is null)
        {
            return;
        }

        if (isWarning)
        {
            ConsoleLog.WriteWarning(
                "CashierSession",
                message,
                new ApplicationLogContext(
                    RequestPath: "api/v1/cashiers/session",
                    RequestMethod: "GET",
                    StatusCode: attempt.StatusCode,
                    UserId: currentSession.CashierId,
                    Properties: CreateLogProperties(currentSession, attempt.ErrorCode, attempt.Reason)),
                attempt.Error);
            return;
        }

        var properties = CreateLogProperties(currentSession, errorCode: null, reason: "recovered");
        properties["offlineSeconds"] = offlineSeconds;
        properties["attemptCount"] = failedAttempts;
        ConsoleLog.WriteInformation(
            "CashierSession",
            message,
            new ApplicationLogContext(
                RequestPath: "api/v1/cashiers/session",
                RequestMethod: "GET",
                UserId: currentSession.CashierId,
                Properties: properties));
    }

    private static Dictionary<string, object?> CreateLogProperties(
        CashierSessionDto session,
        string? errorCode,
        string? reason)
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["storeCode"] = session.StoreCode,
            ["deviceCode"] = session.DeviceCode
        };
        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            properties["errorCode"] = errorCode;
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            properties["reason"] = reason;
        }

        return properties;
    }

    private void NotifySessionRejected(CashierSessionDto rejectedSession)
    {
        var handlers = SessionRejected;
        if (handlers is null)
        {
            return;
        }

        var eventArgs = new CashierSessionRejectedEventArgs(rejectedSession);
        foreach (EventHandler<CashierSessionRejectedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception ex)
            {
                // 界面通知失败不能阻断被拒票据的缓存清理或后台刷新循环。
                ConsoleLog.WriteError(
                    "CashierSession",
                    "收银员会话失效通知处理失败。",
                    exception: ex);
            }
        }
    }

    private static bool HasSameCacheIdentity(
        CashierSessionDto left,
        CashierSessionDto right)
    {
        return string.Equals(left.UserGuid, right.UserGuid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.StoreCode, right.StoreCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.DeviceCode, right.DeviceCode, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class CashierSessionRefreshHostedService(
    CashierSessionRefreshService refreshService) : BackgroundService
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await refreshService.RefreshOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // 网络或缓存暂时失败时保留最后快照，下一个周期继续重试。
                ConsoleLog.WriteError("CashierSession", "收银员权限刷新失败，将在下个周期重试。", exception: ex);
            }
        }
    }
}
