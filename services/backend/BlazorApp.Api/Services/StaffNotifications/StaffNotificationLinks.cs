using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.DTOs;
using Microsoft.Extensions.Options;

namespace BlazorApp.Api.Services.StaffNotifications;

/// <summary>
/// 员工通知配置（配置段 StaffNotification；环境变量 StaffNotification__PublicBaseUrl）。
/// 邮件 / 短信里的下载链接基址必须来自配置，不能取请求 Host：请求头被伪造就能让员工收到指向钓鱼站点的链接
/// （与监护人签署链接、在线小票同一约束）。默认值就是生产域名，不配也能用。
/// </summary>
public sealed class StaffNotificationOptions
{
    public const string SectionName = "StaffNotification";
    public const string DefaultPublicBaseUrl = "https://hotbargain.vip";

    public string PublicBaseUrl { get; set; } = DefaultPublicBaseUrl;

    /// <summary>规范化后的公开基址（不带末尾斜杠）：必须是 https、无 query / fragment / 用户信息、路径为空或 "/"，否则回退默认值。</summary>
    public string ResolveBaseUrl()
    {
        if (Uri.TryCreate(PublicBaseUrl?.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment)
            && (uri.AbsolutePath == "/" || string.IsNullOrEmpty(uri.AbsolutePath))
            && string.IsNullOrEmpty(uri.UserInfo))
        {
            return uri.GetLeftPart(UriPartial.Authority);
        }

        return DefaultPublicBaseUrl;
    }

    /// <summary>
    /// 公开下载短链页 /app（手机打开自动跳商店 / 最新 APK，电脑显示二维码），永远指向最新版。
    /// 员工通知模板的 {{app_link}} 与新建员工的账号邮件共用这一个地址，避免两处各拼一份。
    /// </summary>
    public string ResolveAppLink() => $"{ResolveBaseUrl()}/app";
}

/// <summary>模板里 {{app_link}} / {{download_buttons}} 用到的三个地址。</summary>
internal sealed record StaffNotificationLinks(string AppLink, string IosUrl, string AndroidUrl);

internal interface IStaffNotificationLinkProvider
{
    Task<StaffNotificationLinks> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 下载入口：
/// - AppLink：公开短链页 /app（手机打开自动跳商店，电脑显示二维码），永远指向最新版，模板不必跟着版本改；
/// - Android：稳定的匿名地址，每次 302 到最新 APK；
/// - iOS：登记版本时向 Apple 核验过的 App Store 链接；取不到就退回短链页，保证邮件里的按钮永远能点。
/// </summary>
internal sealed class StaffNotificationLinkProvider : IStaffNotificationLinkProvider
{
    private const string AppStoreStorefront = "au";
    private const string AndroidDownloadPath = "/api/mobile-app-builds/android-latest/download?profile=production";

    private readonly IIosAppStoreReleaseService _iosReleases;
    private readonly StaffNotificationOptions _options;
    private readonly ILogger<StaffNotificationLinkProvider> _logger;

    public StaffNotificationLinkProvider(
        IIosAppStoreReleaseService iosReleases,
        IOptions<StaffNotificationOptions> options,
        ILogger<StaffNotificationLinkProvider> logger
    )
    {
        _iosReleases = iosReleases;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<StaffNotificationLinks> GetAsync(CancellationToken cancellationToken = default)
    {
        var baseUrl = _options.ResolveBaseUrl();
        var appLink = _options.ResolveAppLink();
        var androidUrl = $"{baseUrl}{AndroidDownloadPath}";
        var iosUrl = appLink;

        try
        {
            var result = await _iosReleases.GetAsync(
                new IosAppStoreReleaseQuery { App = AppUpdateApps.MobileIos, Storefront = AppStoreStorefront }
            );
            // 与「App 安装」页同一口径：取核验时间最新、且有商店链接的一条。
            var storeUrl = result.Success
                ? result.Data?
                    .Where(item => !string.IsNullOrWhiteSpace(item.AppStoreUrl))
                    .OrderByDescending(item => item.AppleVerifiedAtUtc)
                    .Select(item => item.AppStoreUrl)
                    .FirstOrDefault()
                : null;
            if (IsTrustedStoreUrl(storeUrl))
            {
                iosUrl = storeUrl!;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 取不到商店链接不能挡住发信：按钮退回短链页即可。日志只记异常类型。
            _logger.LogWarning("员工通知读取 iOS 商店链接失败，按钮退回短链页，异常类型：{ExceptionType}", ex.GetType().Name);
        }

        return new StaffNotificationLinks(appLink, iosUrl, androidUrl);
    }

    /// <summary>只放行 https 的 apple.com 链接：数据库里的值虽然是内部登记的，但要进邮件按钮，多一道防线不吃亏。</summary>
    internal static bool IsTrustedStoreUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttps
            && (uri.Host.Equals("apps.apple.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".apple.com", StringComparison.OrdinalIgnoreCase));
    }
}
