using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;

namespace Hbpos.Client.Wpf.Services;

public interface IDeviceApiClient
{
    Task<IReadOnlyList<StoreSelectionItem>> GetStoresAsync(CancellationToken cancellationToken = default);

    Task<DeviceRegisterResponse> RegisterAsync(DeviceRegisterRequest request, CancellationToken cancellationToken = default);

    Task<DeviceVerifyResponse> VerifyAsync(DeviceVerifyRequest request, CancellationToken cancellationToken = default);

    Task<DeviceReregisterResponse> ReregisterAsync(DeviceReregisterRequest request, CancellationToken cancellationToken = default);

    Task<DeviceActivationCodePreviewResponse> PreviewActivationCodeAsync(
        DeviceActivationCodePreviewRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Device activation-code preview is not available.");

    Task<DeviceActivationCodeRedeemResponse> RedeemActivationCodeAsync(
        DeviceActivationCodeRedeemRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Device activation-code redemption is not available.");

    Task<DeviceActivationCodeRedeemResponse> RedeemActivationCodeForRecoveryAsync(
        DeviceActivationCodeRedeemRequest request,
        CancellationToken cancellationToken = default) =>
        RedeemActivationCodeAsync(request, cancellationToken);

    Task<DeviceActivationCodeRedeemResponse> RebindActivationCodeAsync(
        DeviceActivationCodeRebindRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Device activation-code rebinding is not available.");
}

public sealed class DeviceApiClient(HttpClient httpClient) : IDeviceApiClient
{
    internal const string ActivationRecoveryOnlyHeader = "X-HBPOS-Activation-Recovery-Only";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<StoreSelectionItem>> GetStoresAsync(CancellationToken cancellationToken = default)
    {
        var stores = await SendLoggedAsync<IReadOnlyList<StoreDto>>(
            "api/v1/catalog/stores",
            HttpMethod.Get,
            token => httpClient.GetAsync("api/v1/catalog/stores", token),
            cancellationToken);
        return stores
            .Where(x => x.IsActive)
            .OrderBy(x => x.StoreName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.StoreCode, StringComparer.OrdinalIgnoreCase)
            .Select(x => new StoreSelectionItem(x.StoreCode, x.StoreName, x.IsActive))
            .ToArray();
    }

    public async Task<DeviceRegisterResponse> RegisterAsync(
        DeviceRegisterRequest request,
        CancellationToken cancellationToken = default)
    {
        return await SendLoggedAsync<DeviceRegisterResponse>(
            "api/v1/devices/register",
            HttpMethod.Post,
            token => httpClient.PostAsJsonAsync("api/v1/devices/register", request, JsonOptions, token),
            cancellationToken);
    }

    public async Task<DeviceVerifyResponse> VerifyAsync(
        DeviceVerifyRequest request,
        CancellationToken cancellationToken = default)
    {
        return await SendLoggedAsync<DeviceVerifyResponse>(
            "api/v1/devices/verify",
            HttpMethod.Post,
            token => httpClient.PostAsJsonAsync("api/v1/devices/verify", request, JsonOptions, token),
            cancellationToken);
    }

    public async Task<DeviceReregisterResponse> ReregisterAsync(
        DeviceReregisterRequest request,
        CancellationToken cancellationToken = default)
    {
        return await SendLoggedAsync<DeviceReregisterResponse>(
            "api/v1/devices/reregister",
            HttpMethod.Post,
            token => httpClient.PostAsJsonAsync("api/v1/devices/reregister", request, JsonOptions, token),
            cancellationToken);
    }

    public async Task<DeviceActivationCodePreviewResponse> PreviewActivationCodeAsync(
        DeviceActivationCodePreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        return await SendLoggedAsync<DeviceActivationCodePreviewResponse>(
            "api/v1/devices/activation-code/preview",
            HttpMethod.Post,
            token => httpClient.PostAsJsonAsync("api/v1/devices/activation-code/preview", request, JsonOptions, token),
            cancellationToken);
    }

    public async Task<DeviceActivationCodeRedeemResponse> RedeemActivationCodeAsync(
        DeviceActivationCodeRedeemRequest request,
        CancellationToken cancellationToken = default)
    {
        return await SendLoggedAsync<DeviceActivationCodeRedeemResponse>(
            "api/v1/devices/activation-code/redeem",
            HttpMethod.Post,
            token => httpClient.PostAsJsonAsync("api/v1/devices/activation-code/redeem", request, JsonOptions, token),
            cancellationToken);
    }

    public async Task<DeviceActivationCodeRedeemResponse> RedeemActivationCodeForRecoveryAsync(
        DeviceActivationCodeRedeemRequest request,
        CancellationToken cancellationToken = default)
    {
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "api/v1/devices/activation-code/redeem")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        httpRequest.Headers.TryAddWithoutValidation(ActivationRecoveryOnlyHeader, "true");
        return await SendLoggedAsync<DeviceActivationCodeRedeemResponse>(
            "api/v1/devices/activation-code/redeem",
            HttpMethod.Post,
            token => httpClient.SendAsync(httpRequest, token),
            cancellationToken,
            operation: "recovery");
    }

    public async Task<DeviceActivationCodeRedeemResponse> RebindActivationCodeAsync(
        DeviceActivationCodeRebindRequest request,
        CancellationToken cancellationToken = default)
    {
        return await SendLoggedAsync<DeviceActivationCodeRedeemResponse>(
            "api/v1/devices/activation-code/rebind",
            HttpMethod.Post,
            token => httpClient.PostAsJsonAsync("api/v1/devices/activation-code/rebind", request, JsonOptions, token),
            cancellationToken);
    }

    private const string LogCategory = "Device";
    private const string VerifyPath = "api/v1/devices/verify";

    // 注册页待审批时每 5 秒轮询 verify：结果不变 / 同一失败重复出现时不再重复记日志。
    // DeviceApiClient 是 typed HttpClient（transient），所以去重状态放静态字段，用 Interlocked 原子替换保证线程安全。
    private static string? _lastVerifyOutcome;

    /// <summary>
    /// 统一发送、读取信封并记录日志。请求体含开通码/授权码，响应含 AuthorizationCode，所以日志只记路径、状态码、耗时与 errorCode，绝不记正文。
    /// </summary>
    private static async Task<T> SendLoggedAsync<T>(
        string path,
        HttpMethod method,
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken,
        string? operation = null)
    {
        var stopwatch = Stopwatch.StartNew();
        int? statusCode = null;
        try
        {
            using var response = await send(cancellationToken);
            statusCode = (int)response.StatusCode;
            var result = await ReadApiResultAsync<T>(response, cancellationToken);
            LogCompleted(path, method, statusCode.Value, stopwatch.ElapsedMilliseconds, operation, result);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方主动取消（离开页面、停止轮询）不算联网失败。
            throw;
        }
        catch (Exception ex)
        {
            LogFailed(path, method, statusCode, stopwatch.ElapsedMilliseconds, operation, ex);
            throw;
        }
    }

    private static void LogCompleted<T>(
        string path,
        HttpMethod method,
        int statusCode,
        long elapsedMs,
        string? operation,
        T result)
    {
        // 只摘取不敏感的状态字段；AuthorizationCode 不进日志。
        var summary = result switch
        {
            DeviceVerifyResponse verify => $" deviceStatus={verify.DeviceStatus} isAllowed={verify.IsAllowed}",
            DeviceRegisterResponse register => $" deviceStatus={register.DeviceStatus} isAllowed={register.IsAllowed} reasonCode={register.ReasonCode ?? "-"}",
            DeviceReregisterResponse reregister => $" deviceStatus={reregister.DeviceStatus} isAllowed={reregister.IsAllowed}",
            DeviceActivationCodePreviewResponse preview => $" isAllowed={preview.IsAllowed} reasonCode={preview.ReasonCode ?? "-"}",
            DeviceActivationCodeRedeemResponse redeem => $" deviceStatus={redeem.DeviceStatus} isAllowed={redeem.IsAllowed} reasonCode={redeem.ReasonCode ?? "-"}",
            IReadOnlyList<StoreDto> stores => $" storeCount={stores.Count}",
            _ => string.Empty
        };

        if (string.Equals(path, VerifyPath, StringComparison.Ordinal)
            && !ShouldLogVerifyOutcome($"ok|{statusCode}|{summary}"))
        {
            return;
        }

        ConsoleLog.WriteInformation(
            LogCategory,
            $"device request completed path={path} status={statusCode} elapsedMs={elapsedMs}{summary}",
            new ApplicationLogContext(
                RequestPath: path,
                RequestMethod: method.Method,
                StatusCode: statusCode,
                Properties: BuildProperties(elapsedMs, operation, errorCode: null, reason: null)));
    }

    private static void LogFailed(
        string path,
        HttpMethod method,
        int? statusCode,
        long elapsedMs,
        string? operation,
        Exception exception)
    {
        string reason;
        string? errorCode = null;
        switch (exception)
        {
            case CatalogApiException apiException:
                reason = "rejected";
                errorCode = apiException.ErrorCode;
                statusCode ??= apiException.StatusCode is { } apiStatus ? (int)apiStatus : (int?)null;
                break;
            case DeviceApiUnavailableException unavailable:
                reason = "unavailable";
                statusCode ??= unavailable.StatusCode is { } unavailableStatus ? (int)unavailableStatus : (int?)null;
                break;
            case OperationCanceledException:
                // 调用方未取消却收到取消：HttpClient 3 秒超时。
                reason = "timeout";
                break;
            case HttpRequestException:
                reason = "network";
                break;
            default:
                reason = "unexpected";
                break;
        }

        if (string.Equals(path, VerifyPath, StringComparison.Ordinal)
            && !ShouldLogVerifyOutcome($"fail|{reason}|{statusCode}|{errorCode}"))
        {
            return;
        }

        ConsoleLog.WriteWarning(
            LogCategory,
            $"device request failed path={path} reason={reason} status={statusCode?.ToString() ?? "-"} errorCode={errorCode ?? "-"} elapsedMs={elapsedMs}",
            new ApplicationLogContext(
                RequestPath: path,
                RequestMethod: method.Method,
                StatusCode: statusCode,
                Properties: BuildProperties(elapsedMs, operation, errorCode, reason)),
            exception);
    }

    private static bool ShouldLogVerifyOutcome(string outcome)
    {
        var previous = Interlocked.Exchange(ref _lastVerifyOutcome, outcome);
        return !string.Equals(previous, outcome, StringComparison.Ordinal);
    }

    /// <summary>仅供测试复位静态去重状态。</summary>
    internal static void ResetLogStateForTests()
    {
        Interlocked.Exchange(ref _lastVerifyOutcome, null);
    }

    private static IReadOnlyDictionary<string, object?> BuildProperties(
        long elapsedMs,
        string? operation,
        string? errorCode,
        string? reason)
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["elapsedMs"] = elapsedMs
        };
        if (!string.IsNullOrWhiteSpace(operation))
        {
            properties["operation"] = operation;
        }

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

    private static async Task<T> ReadApiResultAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var statusCode = response.StatusCode;
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        ApiResult<T>? result = null;

        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                using var document = JsonDocument.Parse(content);
                // 只有带 success 字段的 JSON 对象才是本 API 的 ApiResult 信封；ProblemDetails 或其他 JSON 不算服务端结论。
                if (IsApiResultEnvelope(document.RootElement))
                {
                    result = document.RootElement.Deserialize<ApiResult<T>>(JsonOptions);
                }
            }
            catch (JsonException ex)
            {
                // 非 JSON 正文（nginx 502/503/504 HTML、Wi-Fi 认证页）或信封数据形状不符：响应不是来自可用的 POS API，
                // 按传输故障处理，绝不能让调用方把它当作确定性拒绝（持久化设备拒绝、清除开通码恢复记录等）。
                throw new DeviceApiUnavailableException(statusCode, ex);
            }
        }

        if (result is null)
        {
            if (response.IsSuccessStatusCode || IsGatewayStatus(statusCode))
            {
                // 2xx 却没有信封（空 200、认证页 JSON），或网关类状态没有信封：同样视为服务不可达。
                throw new DeviceApiUnavailableException(statusCode);
            }

            // 400/401/403/409 等由 API 自身产生却不带信封的响应（ASP.NET 模型校验 ProblemDetails、授权策略默认 Forbid 空正文）
            // 保持原有 CatalogApiException 语义：开通码流程据此判断确定性 400 与 rebind→redeem 回退。
            // 这里不带 ErrorCode，启动验证不会据此持久化设备拒绝。
            throw new CatalogApiException(
                $"Device API request failed with HTTP {(int)statusCode}.",
                statusCode);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new CatalogApiException(
                result.Message ?? $"Device API request failed with HTTP {(int)statusCode}.",
                statusCode,
                result.ErrorCode);
        }

        if (result.Data is null)
        {
            throw new CatalogApiException("Device API returned no data.", statusCode, result.ErrorCode);
        }

        return result.Data;
    }

    /// <summary>
    /// 判断 JSON 是否为本 API 的 ApiResult 信封（带布尔 success 字段的对象）；收银员登录/会话刷新客户端共用此口径。
    /// </summary>
    internal static bool IsApiResultEnvelope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "success", StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            }
        }

        return false;
    }

    /// <summary>
    /// 反向代理/网关在后端发布、容器重启或路由未就绪时常返回的状态码；不带 API 信封时不代表服务端业务结论。
    /// </summary>
    private static bool IsGatewayStatus(HttpStatusCode statusCode)
    {
        var numericStatus = (int)statusCode;
        return statusCode is HttpStatusCode.NotFound
                or HttpStatusCode.MethodNotAllowed
                or HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests
            || numericStatus is >= 500 and <= 599;
    }
}

/// <summary>
/// 设备 API 未给出可解析的业务响应（网关 HTML、认证页、空 200 等）。
/// 继承 <see cref="HttpRequestException"/>，让所有调用方沿用"传输故障/服务不可达"的既有处理分支。
/// </summary>
public sealed class DeviceApiUnavailableException : HttpRequestException
{
    public DeviceApiUnavailableException(HttpStatusCode statusCode, Exception? innerException = null)
        : base(
            $"Device service is temporarily unavailable (HTTP {(int)statusCode}, not a POS API response). Check the network and try again.",
            innerException,
            statusCode)
    {
    }
}
