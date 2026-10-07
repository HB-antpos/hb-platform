using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Health;

namespace Hbpos.Client.Wpf.Services;

public sealed class ApiServerSettingsService
{
    public const string DevelopmentApiBaseAddress = "http://localhost:5159/";
    public const string ReleaseApiBaseAddress = "https://hotbargain.vip/pos-api/";
    internal static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _httpClient;
    private readonly Func<string> _getCurrentAddress;
    private readonly Action<string> _saveUserAddress;

    public ApiServerSettingsService(HttpClient httpClient, ApiRuntimeEndpointState? endpointState = null)
        : this(
            httpClient,
            () => endpointState?.CurrentAddress.AbsoluteUri ?? ServiceRegistration.GetApiBaseAddress().ToString(),
            address => Environment.SetEnvironmentVariable(
                "HBPOS_API_BASE_URL",
                address,
                EnvironmentVariableTarget.User))
    {
    }

    internal ApiServerSettingsService(
        HttpClient httpClient,
        Func<string> getCurrentAddress,
        Action<string> saveUserAddress)
    {
        _httpClient = httpClient;
        _getCurrentAddress = getCurrentAddress;
        _saveUserAddress = saveUserAddress;
    }

    public string GetCurrentAddress()
    {
        var currentAddress = _getCurrentAddress().Trim();
        if (!Uri.TryCreate(currentAddress, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("当前进程服务器地址必须是绝对地址。", nameof(currentAddress));
        }

        // 当前进程可能仍使用旧版允许的地址；只做格式统一，严格策略仅用于新保存值。
        var normalized = uri.AbsoluteUri;
        return normalized.EndsWith('/') ? normalized : normalized + "/";
    }

    public static string NormalizeAddress(string address)
    {
        var normalizedInput = address?.Trim();
        if (!Uri.TryCreate(normalizedInput, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ArgumentException("服务器地址必须是绝对地址。", nameof(address));
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("服务器地址只支持 HTTP 或 HTTPS。", nameof(address));
        }

        // 公网地址必须使用 HTTPS；HTTP 仅允许本机和 RFC1918 局域网 IPv4 服务。
        if (uri.Scheme == Uri.UriSchemeHttp &&
            !uri.IsLoopback &&
            !IsPrivateIpv4Address(uri.Host))
        {
            throw new ArgumentException("公网服务器地址必须使用 HTTPS。", nameof(address));
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) ||
            HasUserInfoSeparator(normalizedInput) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("服务器地址不能包含用户信息、查询参数或片段。", nameof(address));
        }

        var normalized = uri.AbsoluteUri;
        return normalized.EndsWith('/') ? normalized : normalized + "/";
    }

    private static bool IsPrivateIpv4Address(string host)
    {
        if (!System.Net.IPAddress.TryParse(host, out var address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 &&
               (bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168));
    }

    private static bool HasUserInfoSeparator(string address)
    {
        var authorityStart = address.IndexOf("://", StringComparison.Ordinal) + 3;
        var authorityEnd = address.IndexOfAny(['/', '?', '#'], authorityStart);
        var authorityLength = (authorityEnd < 0 ? address.Length : authorityEnd) - authorityStart;
        return address.IndexOf('@', authorityStart, authorityLength) >= 0;
    }

    public async Task<bool> TestConnectionAsync(string address, CancellationToken cancellationToken)
    {
        var baseAddress = new Uri(NormalizeAddress(address), UriKind.Absolute);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectionTimeout);

        // 人工点按"测试连接"触发，低频：成功记 Information、失败记 Warning；只记 scheme://host:port，不记路径与查询串。
        var target = baseAddress.GetLeftPart(UriPartial.Authority);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // 候选健康检查客户端未接入运行时端点处理器，必须始终直达用户输入的地址。
            using var response = await _httpClient.GetAsync(
                new Uri(baseAddress, "api/v1/health"),
                timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                LogTestConnectionResult(target, "http-status", (int)response.StatusCode, stopwatch.ElapsedMilliseconds, null);
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<ApiResult<HealthCheckResponse>>(
                cancellationToken: timeout.Token);
            var isOnline = result?.Success == true && result.Data?.IsOnline == true;
            LogTestConnectionResult(
                target,
                isOnline ? null : "health-not-online",
                (int)response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                null);
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
            LogTestConnectionResult(target, reason, (ex as HttpRequestException)?.StatusCode is { } status ? (int)status : null, stopwatch.ElapsedMilliseconds, ex);
            return false;
        }
    }

    private static void LogTestConnectionResult(
        string target,
        string? failureReason,
        int? statusCode,
        long elapsedMs,
        Exception? exception)
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["elapsedMs"] = elapsedMs,
            ["action"] = "test-connection"
        };
        var context = new ApplicationLogContext(
            RequestPath: "api/v1/health",
            RequestMethod: "GET",
            StatusCode: statusCode,
            Properties: properties);
        if (failureReason is null)
        {
            ConsoleLog.WriteInformation(
                "ApiServerSettings",
                $"api server test connection succeeded target={target} status={statusCode?.ToString() ?? "-"} elapsedMs={elapsedMs}",
                context);
            return;
        }

        properties["reason"] = failureReason;
        ConsoleLog.WriteWarning(
            "ApiServerSettings",
            $"api server test connection failed target={target} reason={failureReason} status={statusCode?.ToString() ?? "-"} elapsedMs={elapsedMs}",
            context,
            exception);
    }

    public void SaveUserAddress(string address)
    {
        _saveUserAddress(NormalizeAddress(address));
    }
}
