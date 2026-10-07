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
        try
        {
            var response = await httpClient.GetAsync("api/v1/cashiers/session", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return IsServiceUnavailable(response.StatusCode)
                    ? CashierSessionRefreshAttempt.ApiUnavailable()
                    : CashierSessionRefreshAttempt.OnlineRejected();
            }

            // 2xx 只有带 ApiResult 信封才是服务端结论：空对象、null、Wi-Fi 认证页 JSON 等不是 POS API 的响应，
            // 按不可用处理，绝不能据此踢下线并删除离线登录缓存（与 DeviceApiClient 口径一致）。
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(content);
            if (!DeviceApiClient.IsApiResultEnvelope(document.RootElement))
            {
                return CashierSessionRefreshAttempt.ApiUnavailable();
            }

            var result = document.RootElement.Deserialize<ApiResult<CashierSessionDto>>(JsonOptions)!;
            if (!result.Success)
            {
                return CashierSessionRefreshAttempt.OnlineRejected();
            }

            // success=true 却没有会话数据属于形状异常，不是拒绝。
            return result.Data is not null
                ? CashierSessionRefreshAttempt.Refreshed(result.Data)
                : CashierSessionRefreshAttempt.ApiUnavailable();
        }
        catch (JsonException)
        {
            return CashierSessionRefreshAttempt.ApiUnavailable();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return CashierSessionRefreshAttempt.ApiUnavailable();
        }
    }

    private static bool IsServiceUnavailable(HttpStatusCode statusCode)
    {
        var numericStatusCode = (int)statusCode;
        return numericStatusCode >= 500 ||
            statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
    }
}

public sealed class CashierSessionRefreshService(
    ICashierSessionRefreshApiClient apiClient,
    ICashierSessionContext sessionContext,
    ICashierSessionCacheUpdater cacheUpdater)
{
    public event EventHandler<CashierSessionRejectedEventArgs>? SessionRejected;

    public async Task RefreshOnceAsync(CancellationToken cancellationToken = default)
    {
        var currentSession = sessionContext.CurrentSession;
        if (currentSession is null || currentSession.IsEmergencyOverride)
        {
            return;
        }

        var attempt = await apiClient.RefreshAsync(cancellationToken);
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
