using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Services;

public sealed class SupplierAssistantOptions
{
    public const string SectionName = "SupplierAssistant";
    internal const string ServiceTokenEnvironmentVariable = "HBPOS_SUPPLIER_ASSISTANT_TOKEN";

    /// <summary>中心（主 API）地址；留空时沿用 AppUpdate:CenterBaseUrl。只接受 HTTPS（loopback 联调除外）。</summary>
    public string? CenterBaseUrl { get; set; }

    /// <summary>
    /// purpose = pos-supplier-assistant-token-issuer 的 hbsvc_ 服务令牌；只能换设备令牌，不能读写业务数据。
    /// 生产通过 secret / 环境变量 HBPOS_SUPPLIER_ASSISTANT_TOKEN 注入。
    /// </summary>
    public string? ServiceApiToken { get; set; }

    /// <summary>App 实际调用订货助手接口的公网地址；留空时取中心地址的 origin。</summary>
    public string? PublicApiOrigin { get; set; }
}

public enum SupplierAssistantTokenOutcome
{
    Issued,
    NotEligible,
    Unavailable,
}

public sealed record SupplierAssistantDeviceIdentity(string DeviceCode, string StoreCode, string HardwareId);

public sealed record SupplierAssistantIssuedToken(
    string AccessToken,
    DateTimeOffset ExpiresAtUtc,
    string StoreCode,
    string DeviceCode,
    string ApiOrigin);

public sealed record SupplierAssistantTokenResult(
    SupplierAssistantTokenOutcome Outcome,
    SupplierAssistantIssuedToken? Token = null);

public interface ISupplierAssistantTokenGateway
{
    Task<SupplierAssistantTokenResult> IssueAsync(
        SupplierAssistantDeviceIdentity device,
        CancellationToken cancellationToken);
}

/// <summary>
/// 凭专用 service token 向中心换设备级订货助手令牌。
///
/// 中文注释：
/// 1. 设备身份已由 POS API 的设备认证（授权码 + 设备号 + 分店 + 硬件码）核实；这里只把核实后的身份转给中心，
///    中心会再按登记记录核对一次，并把分店写进令牌。
/// 2. 中心返回的分店 / 设备号必须与本设备一致，不一致按不可用处理，绝不把别的分店的令牌交给 App。
/// </summary>
public sealed class HttpSupplierAssistantTokenGateway(
    HttpClient httpClient,
    IOptions<SupplierAssistantOptions> options,
    IOptions<AppUpdateOptions> appUpdateOptions,
    ILogger<HttpSupplierAssistantTokenGateway> logger)
    : ISupplierAssistantTokenGateway
{
    internal const string RelativePath = "api/internal/browser-extension/pos-device-tokens";
    internal const string CenterNotEligibleErrorCode = "POS_DEVICE_NOT_ELIGIBLE";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SupplierAssistantTokenResult> IssueAsync(
        SupplierAssistantDeviceIdentity device,
        CancellationToken cancellationToken)
    {
        var configuration = options.Value;
        var baseUrl = ResolveCenterBaseUrl(configuration.CenterBaseUrl)
            ?? ResolveCenterBaseUrl(appUpdateOptions.Value.CenterBaseUrl);
        var serviceToken = ResolveServiceToken(configuration, Environment.GetEnvironmentVariable);
        var apiOrigin = ResolvePublicApiOrigin(configuration.PublicApiOrigin, baseUrl);
        if (baseUrl is null || serviceToken is null || apiOrigin is null)
        {
            logger.LogWarning("Supplier assistant token gateway is not configured.");
            return new SupplierAssistantTokenResult(SupplierAssistantTokenOutcome.Unavailable);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, RelativePath))
            {
                Content = JsonContent.Create(
                    new { device.DeviceCode, device.StoreCode, device.HardwareId },
                    options: JsonOptions)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseCenterResponse(response.StatusCode, body, device, apiOrigin, logger);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Supplier assistant token request timed out.");
            return new SupplierAssistantTokenResult(SupplierAssistantTokenOutcome.Unavailable);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Supplier assistant token transport failed.");
            return new SupplierAssistantTokenResult(SupplierAssistantTokenOutcome.Unavailable);
        }
    }

    internal static SupplierAssistantTokenResult ParseCenterResponse(
        HttpStatusCode statusCode,
        string body,
        SupplierAssistantDeviceIdentity device,
        string apiOrigin,
        ILogger logger)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = document.RootElement;
            if (statusCode == HttpStatusCode.Forbidden
                && root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("errorCode", out var errorCode)
                && errorCode.ValueKind == JsonValueKind.String
                && errorCode.GetString() == CenterNotEligibleErrorCode)
            {
                return new SupplierAssistantTokenResult(SupplierAssistantTokenOutcome.NotEligible);
            }

            if (statusCode != HttpStatusCode.OK
                || root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("success", out var success)
                || success.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
            {
                logger.LogWarning("Supplier assistant token response was rejected status={StatusCode}", (int)statusCode);
                return new SupplierAssistantTokenResult(SupplierAssistantTokenOutcome.Unavailable);
            }

            var accessToken = ReadString(data, "accessToken");
            var storeCode = ReadString(data, "storeCode");
            var deviceCode = ReadString(data, "deviceCode");
            // 中心签发时间是 UTC；万一序列化成不带时区的写法，也按 UTC 解释，不能落到服务器本地时区。
            var rawExpiry = ReadString(data, "accessTokenExpiry");
            if (string.IsNullOrWhiteSpace(accessToken)
                || !DateTimeOffset.TryParse(
                    rawExpiry,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var expiresAt)
                || !string.Equals(storeCode, device.StoreCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(deviceCode, device.DeviceCode, StringComparison.OrdinalIgnoreCase))
            {
                // 关键逻辑：中心回的分店 / 设备号与本设备对不上就丢弃，绝不转交。
                logger.LogWarning("Supplier assistant token response did not match the device.");
                return new SupplierAssistantTokenResult(SupplierAssistantTokenOutcome.Unavailable);
            }

            return new SupplierAssistantTokenResult(
                SupplierAssistantTokenOutcome.Issued,
                new SupplierAssistantIssuedToken(
                    accessToken,
                    expiresAt.ToUniversalTime(),
                    device.StoreCode,
                    device.DeviceCode,
                    apiOrigin));
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Supplier assistant token JSON was invalid.");
            return new SupplierAssistantTokenResult(SupplierAssistantTokenOutcome.Unavailable);
        }
    }

    internal static string? ResolveServiceToken(
        SupplierAssistantOptions configuration,
        Func<string, string?> readEnvironmentVariable)
    {
        var configured = configuration.ServiceApiToken?.Trim();
        if (configured?.StartsWith("hbsvc_", StringComparison.Ordinal) == true)
        {
            return configured;
        }

        // 只认本功能专用的变量，绝不回退到更新决策或 OTA 发布用的令牌。
        var environment = readEnvironmentVariable(SupplierAssistantOptions.ServiceTokenEnvironmentVariable)?.Trim();
        return environment?.StartsWith("hbsvc_", StringComparison.Ordinal) == true ? environment : null;
    }

    internal static Uri? ResolveCenterBaseUrl(string? configuredValue)
    {
        var candidate = configuredValue?.Trim();
        if (string.IsNullOrWhiteSpace(candidate)
            || !Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || (parsed.Scheme != Uri.UriSchemeHttps
                && !(parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback)))
        {
            return null;
        }

        // 服务令牌只允许发往 HTTPS；HTTP 仅保留 loopback 本地联调。
        var normalized = parsed.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? parsed.AbsoluteUri
            : $"{parsed.AbsoluteUri}/";
        return new Uri(normalized, UriKind.Absolute);
    }

    internal static string? ResolvePublicApiOrigin(string? configuredValue, Uri? centerBaseUrl)
    {
        var candidate = string.IsNullOrWhiteSpace(configuredValue)
            ? centerBaseUrl?.GetLeftPart(UriPartial.Authority)
            : configuredValue.Trim();
        if (string.IsNullOrWhiteSpace(candidate)
            || !Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || (parsed.Scheme != Uri.UriSchemeHttps
                && !(parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback)))
        {
            return null;
        }

        // App 只拿 origin（协议 + 主机 + 端口），路径由 App 固定拼 /api/react/v1/browser-extension。
        return parsed.GetLeftPart(UriPartial.Authority);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;
}
