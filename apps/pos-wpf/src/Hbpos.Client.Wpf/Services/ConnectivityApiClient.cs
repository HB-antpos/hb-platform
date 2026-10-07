using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Health;

namespace Hbpos.Client.Wpf.Services;

public interface IConnectivityApiClient
{
    Task<bool> CheckOnlineAsync(CancellationToken cancellationToken = default);
}

public sealed class ConnectivityApiClient(HttpClient httpClient) : IConnectivityApiClient
{
    private const string HealthPath = "api/v1/health";

    public async Task<bool> CheckOnlineAsync(CancellationToken cancellationToken = default)
    {
        int? statusCode = null;
        try
        {
            using var response = await httpClient.GetAsync(HealthPath, cancellationToken);
            statusCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                ConnectivityLogState.RecordOffline("http-status", statusCode, exception: null);
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<ApiResult<HealthCheckResponse>>(
                cancellationToken);
            var isOnline = result?.Success == true && result.Data?.IsOnline == true;
            if (isOnline)
            {
                ConnectivityLogState.RecordOnline();
            }
            else
            {
                ConnectivityLogState.RecordOffline("health-not-online", statusCode, exception: null);
            }

            return isOnline;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            var reason = ex switch
            {
                TaskCanceledException => "timeout",
                JsonException => "unparsable-response",
                _ => "network"
            };
            ConnectivityLogState.RecordOffline(reason, statusCode, ex);
            return false;
        }
    }
}

/// <summary>
/// 连接探测的日志状态机：只在 在线→离线 / 离线→在线 切换时各记一条，离线期间不重复记录。
/// ConnectivityApiClient 是 typed HttpClient（AddHttpClient 注册为 transient），主界面与考勤面板各持有一个实例、
/// 都按约 15 秒频率探测同一个 API，所以状态必须是进程级静态的，并用 lock 保证并发探测下只记一次切换。
/// </summary>
internal static class ConnectivityLogState
{
    private const string Category = "Connectivity";
    private const string HealthPath = "api/v1/health";
    private static readonly object Gate = new();
    private static bool _isOffline;
    private static DateTimeOffset _offlineSinceUtc;
    private static int _failedChecks;

    internal static void RecordOffline(string reason, int? statusCode, Exception? exception)
    {
        lock (Gate)
        {
            if (_isOffline)
            {
                _failedChecks++;
                return;
            }

            _isOffline = true;
            _offlineSinceUtc = DateTimeOffset.UtcNow;
            _failedChecks = 1;
        }

        // 仅这一次切换日志带异常（堆栈），离线期间的后续失败只计数。
        ConsoleLog.WriteWarning(
            Category,
            $"api went offline reason={reason} status={statusCode?.ToString() ?? "-"} error={exception?.GetType().Name ?? "-"}",
            new ApplicationLogContext(
                RequestPath: HealthPath,
                RequestMethod: "GET",
                StatusCode: statusCode,
                Properties: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["reason"] = reason,
                    ["status"] = "offline"
                }),
            exception);
    }

    internal static void RecordOnline()
    {
        long offlineSeconds;
        int failedChecks;
        lock (Gate)
        {
            if (!_isOffline)
            {
                return;
            }

            offlineSeconds = (long)(DateTimeOffset.UtcNow - _offlineSinceUtc).TotalSeconds;
            failedChecks = _failedChecks;
            _isOffline = false;
            _failedChecks = 0;
        }

        ConsoleLog.WriteInformation(
            Category,
            $"api back online offlineSeconds={offlineSeconds} failedChecks={failedChecks}",
            new ApplicationLogContext(
                RequestPath: HealthPath,
                RequestMethod: "GET",
                Properties: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["status"] = "online",
                    ["offlineSeconds"] = offlineSeconds,
                    ["attemptCount"] = failedChecks
                }));
    }

    /// <summary>仅供测试复位进程级状态。</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _isOffline = false;
            _failedChecks = 0;
            _offlineSinceUtc = default;
        }
    }
}
