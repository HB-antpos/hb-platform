namespace BlazorApp.Api.Features.PosReceipts;

/// <summary>
/// 在线小票配置（配置段 PosReceipt；环境变量 PosReceipt__PublicBaseUrl / PosReceipt__LinkLifetimeDays）。
/// 公开链接的基址必须来自配置，不能取请求 Host：否则请求头被伪造就能让二维码指向钓鱼站点（与监护人签署链接同一约束）。
/// </summary>
public sealed class PosReceiptOptions
{
    public const string SectionName = "PosReceipt";
    public const string DefaultPublicBaseUrl = "https://hotbargain.vip";
    public const int DefaultLinkLifetimeDays = 90;
    public const int MaxLinkLifetimeDays = 365;

    public string PublicBaseUrl { get; set; } = DefaultPublicBaseUrl;

    public int LinkLifetimeDays { get; set; } = DefaultLinkLifetimeDays;

    /// <summary>
    /// 规范化后的公开基址（不带末尾斜杠）：必须是 https、无 query / fragment、路径为空或 "/"，否则回退默认值。
    /// </summary>
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

    /// <summary>链接有效天数：非法或超过上限一律回到默认 90 天，避免配置写错造成永久有效的公开链接。</summary>
    public int ResolveLifetimeDays() =>
        LinkLifetimeDays is >= 1 and <= MaxLinkLifetimeDays ? LinkLifetimeDays : DefaultLinkLifetimeDays;
}
