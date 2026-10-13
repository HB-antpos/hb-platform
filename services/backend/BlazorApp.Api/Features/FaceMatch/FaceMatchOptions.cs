using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace BlazorApp.Api.Features.FaceMatch;

/// <summary>
/// 配置节 <c>FaceMatch</c>（环境变量 <c>FaceMatch__*</c>）：门店人脸比对服务（主 API 管理端）。
/// 全部默认关闭：没有地域或管理凭据时功能整体关闭（管理接口返回 503，后台任务空转）。
/// </summary>
public sealed class FaceMatchOptions
{
    public const string SectionName = "FaceMatch";

    public FaceMatchTencentOptions Tencent { get; set; } = new();
    public FaceMatchUnwelcomeOptions Unwelcome { get; set; } = new();

    /// <summary>Capture 模式的 FaceCaptureEvent / FaceCaptureImage 行保留天数（设计 13.4）；小于 1 视为 7。</summary>
    public int CaptureRetentionDays { get; set; } = 7;

    /// <summary>员工登记照的腾讯云质量控制等级（0 不控制、1 低 … 4 很高）；默认 2。</summary>
    public int EnrollmentQualityControl { get; set; } = 2;

    /// <summary>后台任务总开关（紧急停用用）；默认开启，但没有门店启用 / 没有管理凭据时各任务本身就是空转。</summary>
    public bool BackgroundEnabled { get; set; } = true;

    /// <summary>后台任务节拍（秒）；小于 10 视为 10。</summary>
    public int BackgroundTickSeconds { get; set; } = 60;
}

/// <summary>
/// 腾讯云人脸识别（IAI）管理客户端配置。凭据只认 AdminSecretId / AdminSecretKey，
/// <b>绝不回退</b>到 TencentCloud:SecretId / SecretKey 主密钥（设计第 15 节）。
/// </summary>
public sealed class FaceMatchTencentOptions
{
    /// <summary>处理地域，只允许 <see cref="FaceMatchSettings.AllowedRegions"/>；缺省或不在白名单时功能整体关闭，不回退任何别的地域。</summary>
    public string? Region { get; set; }

    public string? AdminSecretId { get; set; }
    public string? AdminSecretKey { get; set; }
}

public sealed class FaceMatchUnwelcomeOptions
{
    /// <summary>复核页「较高」档位的相似度下限（0–100）；标定前取偏严的默认值，试点标定后由配置覆盖。</summary>
    public decimal HighBandScore { get; set; } = 90m;
}

/// <summary>功能关闭的原因（也是 GET /config 的 disabledReason 取值）。</summary>
public static class FaceMatchDisabledReasons
{
    public const string RegionMissing = "region-missing";
    public const string RegionNotAllowed = "region-not-allowed";
    public const string CredentialsMissing = "credentials-missing";
    public const string CredentialsInvalid = "credentials-invalid";
}

/// <summary>
/// 解析后的运行配置快照。ToString 被覆盖：record 默认会把所有成员（含密钥）打印出来，不能被日志解构带出。
/// </summary>
public sealed record FaceMatchResolvedSettings(
    bool Enabled,
    string? DisabledReason,
    string? Region,
    string? SecretId,
    string? SecretKey)
{
    public override string ToString() => $"FaceMatchResolvedSettings {{ Enabled = {Enabled}, Reason = {DisabledReason}, Region = {Region}, *** }}";
}

public static partial class FaceMatchSettings
{
    /// <summary>地域白名单（设计 13.2）。</summary>
    public static readonly IReadOnlyList<string> AllowedRegions = ["ap-guangzhou", "ap-shanghai", "ap-singapore"];

    // SecretId 会进 Authorization 请求头：限定字符集，杜绝换行等头注入。
    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SecretIdPattern();

    /// <summary>
    /// 解析配置。顺序：地域（缺省 / 不在白名单即关闭）→ 管理凭据（缺失 / 只配一个 / 含非法字符即关闭）。
    /// 不读取 TencentCloud:* 主密钥，也不回退到别的地域。
    /// </summary>
    public static FaceMatchResolvedSettings Resolve(FaceMatchOptions options)
    {
        var region = options.Tencent.Region?.Trim();
        if (string.IsNullOrEmpty(region))
        {
            return Disabled(FaceMatchDisabledReasons.RegionMissing);
        }

        // 精确匹配白名单（区分大小写，不做「修正」）：配置写错就关闭，不静默改成别的地域。
        if (!AllowedRegions.Contains(region, StringComparer.Ordinal))
        {
            return Disabled(FaceMatchDisabledReasons.RegionNotAllowed);
        }

        var secretId = options.Tencent.AdminSecretId?.Trim();
        var secretKey = options.Tencent.AdminSecretKey?.Trim();
        if (string.IsNullOrEmpty(secretId) || string.IsNullOrEmpty(secretKey))
        {
            return Disabled(FaceMatchDisabledReasons.CredentialsMissing, region);
        }

        if (!SecretIdPattern().IsMatch(secretId) || secretKey.Any(char.IsControl))
        {
            return Disabled(FaceMatchDisabledReasons.CredentialsInvalid, region);
        }

        return new FaceMatchResolvedSettings(true, null, region, secretId, secretKey);
    }

    private static FaceMatchResolvedSettings Disabled(string reason, string? region = null) =>
        new(false, reason, region, null, null);
}

/// <summary>每次调用都读最新配置（IOptionsMonitor），改环境变量重启后生效。</summary>
public interface IFaceMatchSettingsProvider
{
    FaceMatchOptions Options { get; }
    FaceMatchResolvedSettings Resolved { get; }
}

internal sealed class FaceMatchSettingsProvider(IOptionsMonitor<FaceMatchOptions> monitor) : IFaceMatchSettingsProvider
{
    public FaceMatchOptions Options => monitor.CurrentValue;
    public FaceMatchResolvedSettings Resolved => FaceMatchSettings.Resolve(monitor.CurrentValue);
}
