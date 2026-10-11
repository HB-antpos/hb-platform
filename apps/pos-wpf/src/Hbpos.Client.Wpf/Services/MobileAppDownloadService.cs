using System.Net.Http;
using System.Text.Json;

namespace Hbpos.Client.Wpf.Services;

/// <summary>安卓安装包在总部的发布状态；<see cref="Unknown"/> 表示没能联网确认。</summary>
public enum MobileAppAndroidAvailability
{
    Unknown,
    Available,
    NotPublished
}

/// <summary>
/// 员工 App 的安装入口信息。两个二维码链接是固定的公网地址，不联网也能得到；
/// 联网只用来补充版本号、安装包大小，以及确认安卓包是否已发布。
/// </summary>
public sealed record MobileAppInstallInfo(
    string IosUrl,
    string? IosVersion,
    string AndroidUrl,
    MobileAppAndroidAvailability AndroidAvailability,
    string? AndroidVersion,
    string? AndroidBuild,
    long? AndroidSizeBytes,
    bool MetadataLoaded);

public interface IMobileAppDownloadService
{
    /// <summary>不联网即可得到的二维码链接（版本信息为空）。</summary>
    MobileAppInstallInfo GetLocalLinks();

    /// <summary>向总部查询版本信息；任何网络或解析失败都退回本地链接，不抛异常（调用方主动取消除外）。</summary>
    Task<MobileAppInstallInfo> RefreshAsync(CancellationToken cancellationToken = default);
}

public sealed class MobileAppDownloadService : IMobileAppDownloadService
{
    public const string DefaultCenterBaseUrl = "https://hotbargain.vip/";

    // 总部尚未返回（或离线）时使用的 App Store 公开链接：App 在澳洲区上架，应用 ID 固定不变。
    internal const string FallbackIosAppStoreUrl = "https://apps.apple.com/au/app/hbgroup/id6786073002";

    // 匿名下载入口：每次访问都会 302 到当前最新的安装包，二维码因此不会随版本更新而过期。
    private const string AndroidDownloadPath = "api/mobile-app-builds/android-latest/download?profile=production";
    private const string AndroidMetadataPath = "api/mobile-app-builds/android-latest?profile=production&integrity=sha256-v1";

    // 用 0.0.0 询问 iOS 更新策略，只为拿到总部登记并经 Apple 核验的商店地址与最新版本号。
    private const string IosDecisionPath = "api/app-updates/mobile-ios?version=0.0.0&build=0";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;

    public MobileAppDownloadService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _baseUri = httpClient.BaseAddress ?? new Uri(DefaultCenterBaseUrl);
    }

    /// <summary>不依赖 HttpClient 的本地链接，ViewModel 在没有服务时也用它。</summary>
    public static MobileAppInstallInfo BuildLocalLinks(Uri baseUri) =>
        new(
            FallbackIosAppStoreUrl,
            IosVersion: null,
            new Uri(baseUri, AndroidDownloadPath).AbsoluteUri,
            MobileAppAndroidAvailability.Unknown,
            AndroidVersion: null,
            AndroidBuild: null,
            AndroidSizeBytes: null,
            MetadataLoaded: false);

    public MobileAppInstallInfo GetLocalLinks() => BuildLocalLinks(_baseUri);

    public async Task<MobileAppInstallInfo> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var local = GetLocalLinks();
        var iosTask = FetchAsync(IosDecisionPath, cancellationToken);
        var androidTask = FetchAsync(AndroidMetadataPath, cancellationToken);
        var iosJson = await iosTask.ConfigureAwait(false);
        var androidJson = await androidTask.ConfigureAwait(false);

        var result = local;
        var loaded = false;

        if (iosJson is not null && TryReadData(iosJson, out var iosData) && iosData.ValueKind == JsonValueKind.Object)
        {
            loaded = true;
            var storeUrl = ReadString(iosData, "appStoreUrl");
            result = result with
            {
                IosUrl = IsAppStoreUrl(storeUrl) ? storeUrl! : result.IosUrl,
                IosVersion = ReadString(iosData, "latestVersion")
            };
        }

        if (androidJson is not null && TryReadData(androidJson, out var androidData))
        {
            loaded = true;
            if (androidData.ValueKind == JsonValueKind.Object)
            {
                result = result with
                {
                    AndroidAvailability = MobileAppAndroidAvailability.Available,
                    AndroidVersion = ReadString(androidData, "appVersion"),
                    AndroidBuild = ReadString(androidData, "appBuildVersion"),
                    AndroidSizeBytes = ReadLong(androidData, "artifactSize")
                };
            }
            else if (androidData.ValueKind == JsonValueKind.Null)
            {
                // 接口成功但 data 为空：总部当前没有可下载的安卓正式包。
                result = result with { AndroidAvailability = MobileAppAndroidAvailability.NotPublished };
            }
        }

        return result with { MetadataLoaded = loaded };
    }

    private async Task<JsonDocument?> FetchAsync(string relativePath, CancellationToken cancellationToken)
    {
        // 单个请求自带超时：门店网络差时设置页不能一直转圈，超时按「没联上」处理。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await _httpClient
                .GetAsync(new Uri(_baseUri, relativePath), timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return JsonDocument.Parse(body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool TryReadData(JsonDocument document, out JsonElement data)
    {
        data = default;
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("success", out var success)
            || success.ValueKind != JsonValueKind.True)
        {
            return false;
        }

        return root.TryGetProperty("data", out data);
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static long? ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    // 二维码内容来自网络响应，只接受 Apple 商店的 https 地址，避免被改成别的站点。
    private static bool IsAppStoreUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (string.Equals(uri.Host, "apps.apple.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "itunes.apple.com", StringComparison.OrdinalIgnoreCase));
}
