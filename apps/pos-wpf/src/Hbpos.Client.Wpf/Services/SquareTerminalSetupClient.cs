using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Square;

namespace Hbpos.Client.Wpf.Services;

public sealed record SquareLocationOption(string Id, string Name);

public sealed record SquareDeviceOption(string Id, string Name, string? Status, string? StatusDisplayName = null);

public sealed record SquareDeviceCodeOption(
    string Id,
    string Name,
    string Code,
    string Status,
    string? LocationId,
    string? DeviceId,
    DateTimeOffset? PairBy,
    DateTimeOffset? CreatedAt);

public interface ISquareTerminalSetupClient
{
    Task<IReadOnlyList<SquareLocationOption>> ListLocationsAsync(
        string accessToken,
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SquareDeviceOption>> ListDevicesAsync(
        string accessToken,
        CardTerminalEnvironment environment,
        string locationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SquareDeviceCodeOption>> ListDeviceCodesAsync(
        string accessToken,
        CardTerminalEnvironment environment,
        string locationId,
        CancellationToken cancellationToken = default);

    Task<SquareDeviceCodeOption> CreateDeviceCodeAsync(
        string accessToken,
        CardTerminalEnvironment environment,
        string locationId,
        string name,
        CancellationToken cancellationToken = default);

    Task<SquareDeviceCodeOption> GetDeviceCodeAsync(
        string accessToken,
        CardTerminalEnvironment environment,
        string deviceCodeId,
        CancellationToken cancellationToken = default);
}

public sealed class SquareTerminalSetupClient(HttpClient httpClient) : ISquareTerminalSetupClient
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<SquareLocationOption>> ListLocationsAsync(
        string accessToken,
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        // 兼容旧接口签名；Square token 改由 Hbpos API 在服务端读取，客户端不再发送 token。
        _ = accessToken;

        using var response = await SendAsync(
            HttpMethod.Get,
            $"api/v1/square/locations?environment={Uri.EscapeDataString(environment.ToString())}",
            body: null,
            cancellationToken);

        var result = await ReadApiResultAsync<List<SquareLocationDto>>(response, "locations", cancellationToken);
        return result.Select(location => new SquareLocationOption(location.Id, location.Name)).ToArray();
    }

    public async Task<IReadOnlyList<SquareDeviceOption>> ListDevicesAsync(
        string accessToken,
        CardTerminalEnvironment environment,
        string locationId,
        CancellationToken cancellationToken = default)
    {
        _ = accessToken;
        if (string.IsNullOrWhiteSpace(locationId))
        {
            throw new ArgumentException("Location id is required.", nameof(locationId));
        }

        var relativeUrl =
            $"api/v1/square/devices?environment={Uri.EscapeDataString(environment.ToString())}&locationId={Uri.EscapeDataString(locationId.Trim())}";
        using var response = await SendAsync(
            HttpMethod.Get,
            relativeUrl,
            body: null,
            cancellationToken);

        var result = await ReadApiResultAsync<List<SquareDeviceDto>>(response, "devices", cancellationToken);
        var devices = result
            .Select(device => new SquareDeviceOption(
                device.Id,
                string.IsNullOrWhiteSpace(device.Name) ? device.Id : device.Name,
                device.Status,
                string.IsNullOrWhiteSpace(device.Status) ? null : device.Status))
            .ToArray();

        return environment == CardTerminalEnvironment.Sandbox
            ? AppendSandboxCheckoutDevices(devices)
            : devices;
    }

    public async Task<IReadOnlyList<SquareDeviceCodeOption>> ListDeviceCodesAsync(
        string accessToken,
        CardTerminalEnvironment environment,
        string locationId,
        CancellationToken cancellationToken = default)
    {
        _ = accessToken;
        if (string.IsNullOrWhiteSpace(locationId))
        {
            throw new ArgumentException("Location id is required.", nameof(locationId));
        }

        var relativeUrl =
            $"api/v1/square/device-codes?environment={Uri.EscapeDataString(environment.ToString())}&locationId={Uri.EscapeDataString(locationId.Trim())}";
        using var response = await SendAsync(
            HttpMethod.Get,
            relativeUrl,
            body: null,
            cancellationToken);

        var result = await ReadApiResultAsync<List<SquareDeviceCodeDto>>(response, "device codes", cancellationToken);
        return result.Select(MapDeviceCode).ToArray();
    }

    public async Task<SquareDeviceCodeOption> CreateDeviceCodeAsync(
        string accessToken,
        CardTerminalEnvironment environment,
        string locationId,
        string name,
        CancellationToken cancellationToken = default)
    {
        _ = accessToken;
        if (string.IsNullOrWhiteSpace(locationId))
        {
            throw new ArgumentException("Location id is required.", nameof(locationId));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Device code name is required.", nameof(name));
        }

        using var response = await SendAsync(
            HttpMethod.Post,
            "api/v1/square/device-codes",
            new SquareCreateDeviceCodeRequest(
                environment.ToString(),
                Guid.NewGuid().ToString("N"),
                locationId.Trim(),
                name.Trim()),
            cancellationToken);

        var result = await ReadApiResultAsync<SquareDeviceCodeDto>(response, "create device code", cancellationToken);
        return MapDeviceCode(result);
    }

    public async Task<SquareDeviceCodeOption> GetDeviceCodeAsync(
        string accessToken,
        CardTerminalEnvironment environment,
        string deviceCodeId,
        CancellationToken cancellationToken = default)
    {
        _ = accessToken;
        if (string.IsNullOrWhiteSpace(deviceCodeId))
        {
            throw new ArgumentException("Device code id is required.", nameof(deviceCodeId));
        }

        using var response = await SendAsync(
            HttpMethod.Get,
            $"api/v1/square/device-codes/{Uri.EscapeDataString(deviceCodeId.Trim())}?environment={Uri.EscapeDataString(environment.ToString())}",
            body: null,
            cancellationToken);

        var result = await ReadApiResultAsync<SquareDeviceCodeDto>(response, "device code", cancellationToken);
        return MapDeviceCode(result);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativeUrl,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, relativeUrl);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        try
        {
            return await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException ||
            (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // 设置页 Square 接口断网或 HttpClient 超时（调用方取消不记）。
            var queryIndex = relativeUrl.IndexOf('?', StringComparison.Ordinal);
            var requestPath = queryIndex < 0 ? relativeUrl : relativeUrl[..queryIndex];
            ConsoleLog.WriteWarning(
                "Square",
                $"settings Square API {method.Method} {requestPath} {(ex is HttpRequestException ? "network failure" : "timed out")}",
                new ApplicationLogContext(RequestPath: requestPath, RequestMethod: method.Method),
                ex);
            throw;
        }
    }

    /// <summary>
    /// 设置页 Square 接口返回失败时记 Warning（只记操作名、状态码与服务端说明，不记正文：设备配对码在响应体里）。
    /// </summary>
    private static void LogApiFailure(string operationName, int statusCode, string reason, string? serverMessage, Exception? exception = null)
    {
        var message = serverMessage is null || serverMessage.Length <= 256 ? serverMessage : serverMessage[..256];
        ConsoleLog.WriteWarning(
            "Square",
            $"settings Square {operationName} request failed http={statusCode} reason={reason}" +
            (string.IsNullOrWhiteSpace(message) ? string.Empty : $" message={message}"),
            new ApplicationLogContext(
                StatusCode: statusCode,
                Properties: new Dictionary<string, object?>
                {
                    ["operation"] = operationName,
                    ["reason"] = reason
                }),
            exception);
    }

    private static async Task<T> ReadApiResultAsync<T>(
        HttpResponseMessage response,
        string operationName,
        CancellationToken cancellationToken)
    {
        var content = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken);

        ApiResult<T>? result = null;
        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                result = JsonSerializer.Deserialize<ApiResult<T>>(content, JsonOptions);
            }
            catch (JsonException ex)
            {
                LogApiFailure(operationName, (int)response.StatusCode, "invalid-json", serverMessage: null, ex);
                throw new InvalidOperationException($"Square {operationName} API returned invalid JSON.", ex);
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            LogApiFailure(operationName, (int)response.StatusCode, "http-error", result?.Message);
            throw new SquareApiException(
                string.IsNullOrWhiteSpace(result?.Message)
                    ? $"Square {operationName} request failed with HTTP {(int)response.StatusCode}."
                    : $"Square {operationName} request failed with HTTP {(int)response.StatusCode}: {result.Message}",
                response.StatusCode);
        }

        if (result is null)
        {
            LogApiFailure(operationName, (int)response.StatusCode, "empty-response", serverMessage: null);
            throw new InvalidOperationException($"Square {operationName} API returned an empty response.");
        }

        if (!result.Success)
        {
            LogApiFailure(operationName, (int)response.StatusCode, "failure-response", result.Message);
            throw new SquareApiException(
                string.IsNullOrWhiteSpace(result.Message)
                    ? $"Square {operationName} API returned a failure response."
                    : result.Message,
                response.StatusCode);
        }

        if (result.Data is null)
        {
            LogApiFailure(operationName, (int)response.StatusCode, "missing-data", result.Message);
            throw new InvalidOperationException($"Square {operationName} API returned no data.");
        }

        return result.Data;
    }

    private static SquareDeviceCodeOption MapDeviceCode(SquareDeviceCodeDto deviceCode)
    {
        return new SquareDeviceCodeOption(
            deviceCode.Id,
            deviceCode.Name ?? deviceCode.Id,
            deviceCode.Code ?? string.Empty,
            deviceCode.Status ?? string.Empty,
            deviceCode.LocationId,
            deviceCode.DeviceId,
            PairBy: null,
            CreatedAt: null);
    }

    private static IReadOnlyList<SquareDeviceOption> AppendSandboxCheckoutDevices(IReadOnlyList<SquareDeviceOption> devices)
    {
        var merged = devices.ToList();
        foreach (var sandboxDevice in SquareSandboxTerminalDeviceIds.CheckoutDevices)
        {
            if (merged.Any(device => SquareSandboxTerminalDeviceIds.AreSameDeviceId(device.Id, sandboxDevice.DeviceId)))
            {
                continue;
            }

            // Sandbox 设备列表追加官方 checkout 测试 ID，让收银端可以通过选择设备控制成功、取消、超时等模拟结果。
            merged.Add(new SquareDeviceOption(
                sandboxDevice.DeviceId,
                sandboxDevice.Name,
                SquareSandboxTerminalDeviceIds.TestDeviceStatus));
        }

        return merged;
    }
}

public sealed class SquareApiException : InvalidOperationException
{
    public SquareApiException(string message, System.Net.HttpStatusCode statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public System.Net.HttpStatusCode StatusCode { get; }

    public bool IsAuthenticationFailure =>
        StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden;
}
