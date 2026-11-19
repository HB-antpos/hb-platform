using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hbpos.Contracts.AppUpdates;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Services;

public sealed record PosIpadNativeUpdateDecisionRequest(
    string StoreCode,
    string? Version,
    string? Build);

// DeviceCode / HardwareId 来自设备认证 claim，供中心按「设备 → 分店 → 基线」三层决定 OTA 版本；
// 带默认值是为了兼容只传分店的旧调用方，缺失时中心自动跳过设备层。
public sealed record PosIpadOtaUpdateDecisionRequest(
    string StoreCode,
    string? RuntimeVersion,
    string? CurrentUpdateId,
    string? CurrentUpdateGroupId,
    string? DeviceCode = null,
    string? HardwareId = null);

public sealed record PosIpadNativeUpdateDecision(
    string State,
    string PolicyVersion,
    string? MinimumSupportedVersion,
    string? LatestVersion,
    string? AppStoreUrl,
    string? ReleaseMessage);

public interface IPosIpadUpdateDecisionGateway
{
    Task<PosIpadNativeUpdateDecision?> GetNativeDecisionAsync(
        PosIpadNativeUpdateDecisionRequest request,
        CancellationToken cancellationToken);

    Task<PosIpadOtaUpdateResponse?> GetOtaDecisionAsync(
        PosIpadOtaUpdateDecisionRequest request,
        CancellationToken cancellationToken);
}

public sealed class HttpPosIpadUpdateDecisionGateway(
    HttpClient httpClient,
    IOptions<AppUpdateOptions> options,
    ILogger<HttpPosIpadUpdateDecisionGateway> logger)
    : IPosIpadUpdateDecisionGateway
{
    internal const string DecisionReadTokenEnvironmentVariable =
        "HBPOS_APP_UPDATE_DECISION_READ_TOKEN";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> NativeDecisionFields = new(
        [
            "state",
            "policyVersion",
            "latestVersion",
            "minimumSupportedVersion",
            "appStoreUrl",
            "releaseMessage"
        ],
        StringComparer.Ordinal);
    private static readonly HashSet<string> OtaDecisionFields = new(
        [
            "state",
            "policyVersion",
            "channel",
            "runtimeVersion",
            "iosUpdateId",
            "updateGroupId",
            "releaseMessage"
        ],
        StringComparer.Ordinal);
    private static readonly Regex MarketingVersionPattern = new(
        "^v?\\d+(?:\\.\\d+){0,3}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ReleaseChannelPattern = new(
        "^pos-ipad-release-[a-z0-9](?:[a-z0-9._-]*[a-z0-9])?$",
        RegexOptions.Compiled);

    public Task<PosIpadNativeUpdateDecision?> GetNativeDecisionAsync(
        PosIpadNativeUpdateDecisionRequest request,
        CancellationToken cancellationToken) =>
        PostDecisionAsync<PosIpadNativeUpdateDecision>(
            "api/internal/app-update-decisions/pos-ipad/native",
            request,
            NativeDecisionFields,
            IsValidNativeDecisionJson,
            IsValidNativeDecision,
            cancellationToken);

    public Task<PosIpadOtaUpdateResponse?> GetOtaDecisionAsync(
        PosIpadOtaUpdateDecisionRequest request,
        CancellationToken cancellationToken) =>
        PostDecisionAsync<PosIpadOtaUpdateResponse>(
            "api/internal/app-update-decisions/pos-ipad/ota",
            request,
            OtaDecisionFields,
            IsValidOtaDecisionJson,
            IsValidOtaDecision,
            cancellationToken);

    private async Task<TDecision?> PostDecisionAsync<TDecision>(
        string relativePath,
        object requestBody,
        IReadOnlySet<string> expectedFields,
        Func<JsonElement, bool> validateJson,
        Func<TDecision, bool> validate,
        CancellationToken cancellationToken)
        where TDecision : class
    {
        var baseUrl = ResolveCenterBaseUrl(options.Value.CenterBaseUrl);
        var serviceToken = ResolveServiceToken(options.Value);
        if (baseUrl is null || serviceToken is null)
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(baseUrl, relativePath))
            {
                Content = JsonContent.Create(requestBody, options: JsonOptions)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "iPad update decision request failed path={Path} status={StatusCode}",
                    relativePath,
                    (int)response.StatusCode);
                return null;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(
                cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                responseStream,
                cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("success", out var success)
                || success.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("data", out var data)
                || !HasExactProperties(data, expectedFields)
                || !validateJson(data))
            {
                logger.LogWarning(
                    "iPad update decision response was invalid path={Path}",
                    relativePath);
                return null;
            }

            var decision = data.Deserialize<TDecision>(JsonOptions);
            if (decision is null || !validate(decision))
            {
                logger.LogWarning(
                    "iPad update decision response was invalid path={Path}",
                    relativePath);
                return null;
            }

            return decision;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "iPad update decision request timed out path={Path}",
                relativePath);
            return null;
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(
                exception,
                "iPad update decision transport failed path={Path}",
                relativePath);
            return null;
        }
        catch (JsonException exception)
        {
            logger.LogWarning(
                exception,
                "iPad update decision JSON was invalid path={Path}",
                relativePath);
            return null;
        }
    }

    private static Uri? ResolveCenterBaseUrl(string? configuredValue)
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

        // 中文注释：服务令牌只允许发往 HTTPS；HTTP 仅保留 loopback 本地联调。
        var normalized = parsed.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? parsed.AbsoluteUri
            : $"{parsed.AbsoluteUri}/";
        return new Uri(normalized, UriKind.Absolute);
    }

    private static string? ResolveServiceToken(AppUpdateOptions configuration)
        => ResolveServiceToken(
            configuration,
            Environment.GetEnvironmentVariable);

    internal static string? ResolveServiceToken(
        AppUpdateOptions configuration,
        Func<string, string?> readEnvironmentVariable)
    {
        var configured = configuration.ServiceApiToken?.Trim();
        if (configured?.StartsWith("hbsvc_", StringComparison.Ordinal) == true)
        {
            return configured;
        }

        // 读取决策的窄权限令牌不得回退到 Mobile OTA publisher 的旧变量。
        var environment = readEnvironmentVariable(
            DecisionReadTokenEnvironmentVariable)?.Trim();
        return environment?.StartsWith("hbsvc_", StringComparison.Ordinal) == true
            ? environment
            : null;
    }

    private static bool IsValidNativeDecision(PosIpadNativeUpdateDecision decision)
    {
        if (!IsValidState(decision.State)
            || string.IsNullOrWhiteSpace(decision.PolicyVersion))
        {
            return false;
        }

        return string.Equals(decision.State, "none", StringComparison.Ordinal)
            || (!string.IsNullOrWhiteSpace(decision.LatestVersion)
                && IsTrustedAppStoreUrl(decision.AppStoreUrl));
    }

    private static bool IsValidOtaDecision(PosIpadOtaUpdateResponse decision)
    {
        if (!IsValidState(decision.State)
            || string.IsNullOrWhiteSpace(decision.PolicyVersion))
        {
            return false;
        }

        return string.Equals(decision.State, "none", StringComparison.Ordinal)
            || (!string.IsNullOrWhiteSpace(decision.Channel)
                && !string.IsNullOrWhiteSpace(decision.RuntimeVersion)
                && !string.IsNullOrWhiteSpace(decision.IosUpdateId)
                && !string.IsNullOrWhiteSpace(decision.UpdateGroupId));
    }

    private static bool IsValidState(string? state) =>
        string.Equals(state, "none", StringComparison.Ordinal)
        || string.Equals(state, "optional", StringComparison.Ordinal)
        || string.Equals(state, "required", StringComparison.Ordinal);

    private static bool IsTrustedAppStoreUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var url)
        && url.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(url.UserInfo)
        && (string.Equals(url.Host, "apps.apple.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(url.Host, "itunes.apple.com", StringComparison.OrdinalIgnoreCase));

    private static bool HasExactProperties(
        JsonElement value,
        IReadOnlySet<string> expectedFields)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var count = 0;
        foreach (var property in value.EnumerateObject())
        {
            count++;
            if (!expectedFields.Contains(property.Name))
            {
                return false;
            }
        }

        return count == expectedFields.Count;
    }

    private static bool IsValidNativeDecisionJson(JsonElement decision)
    {
        if (!TryGetString(decision, "state", out var state)
            || !TryGetString(decision, "policyVersion", out var policyVersion)
            || !IsNullableNormalizedString(decision, "releaseMessage"))
        {
            return false;
        }

        if (string.Equals(state, "none", StringComparison.Ordinal))
        {
            return string.Equals(policyVersion, "none", StringComparison.Ordinal)
                && IsJsonNull(decision, "latestVersion")
                && IsJsonNull(decision, "minimumSupportedVersion")
                && IsJsonNull(decision, "appStoreUrl")
                && IsJsonNull(decision, "releaseMessage");
        }

        if ((!string.Equals(state, "optional", StringComparison.Ordinal)
                && !string.Equals(state, "required", StringComparison.Ordinal))
            || !IsActivePolicyVersion(policyVersion)
            || !TryGetMarketingVersion(decision, "latestVersion")
            || !TryGetString(decision, "appStoreUrl", out var appStoreUrl)
            || !IsTrustedAppStoreUrl(appStoreUrl)
            || !IsNullableMarketingVersion(decision, "minimumSupportedVersion"))
        {
            return false;
        }

        return !string.Equals(state, "required", StringComparison.Ordinal)
            || !IsJsonNull(decision, "minimumSupportedVersion");
    }

    private static bool IsValidOtaDecisionJson(JsonElement decision)
    {
        if (!TryGetString(decision, "state", out var state)
            || !TryGetString(decision, "policyVersion", out var policyVersion)
            || !IsNullableNormalizedString(decision, "releaseMessage"))
        {
            return false;
        }

        if (string.Equals(state, "none", StringComparison.Ordinal))
        {
            return string.Equals(policyVersion, "none", StringComparison.Ordinal)
                && IsJsonNull(decision, "channel")
                && IsJsonNull(decision, "runtimeVersion")
                && IsJsonNull(decision, "iosUpdateId")
                && IsJsonNull(decision, "updateGroupId")
                && IsJsonNull(decision, "releaseMessage");
        }

        return (string.Equals(state, "optional", StringComparison.Ordinal)
                || string.Equals(state, "required", StringComparison.Ordinal))
            && IsActivePolicyVersion(policyVersion)
            && TryGetString(decision, "channel", out var channel)
            && channel.Length <= 120
            && ReleaseChannelPattern.IsMatch(channel)
            && TryGetString(decision, "runtimeVersion", out var runtimeVersion)
            && runtimeVersion.Length <= 120
            && TryGetCanonicalGuid(decision, "iosUpdateId")
            && TryGetCanonicalGuid(decision, "updateGroupId");
    }

    private static bool TryGetString(
        JsonElement value,
        string propertyName,
        out string text)
    {
        text = string.Empty;
        return value.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && (text = property.GetString() ?? string.Empty).Length > 0
            && string.Equals(text, text.Trim(), StringComparison.Ordinal);
    }

    private static bool IsJsonNull(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.Null;

    private static bool IsNullableNormalizedString(
        JsonElement value,
        string propertyName) =>
        IsJsonNull(value, propertyName)
        || TryGetString(value, propertyName, out _);

    private static bool TryGetMarketingVersion(
        JsonElement value,
        string propertyName) =>
        TryGetString(value, propertyName, out var version)
        && MarketingVersionPattern.IsMatch(version);

    private static bool IsNullableMarketingVersion(
        JsonElement value,
        string propertyName) =>
        IsJsonNull(value, propertyName)
        || TryGetMarketingVersion(value, propertyName);

    private static bool IsActivePolicyVersion(string value) =>
        long.TryParse(value, out var parsed)
        && parsed > 0
        && string.Equals(
            parsed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            value,
            StringComparison.Ordinal);

    private static bool TryGetCanonicalGuid(
        JsonElement value,
        string propertyName) =>
        TryGetString(value, propertyName, out var text)
        && Guid.TryParse(text, out var parsed)
        && string.Equals(parsed.ToString(), text, StringComparison.OrdinalIgnoreCase);
}
