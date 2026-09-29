namespace BlazorApp.Shared.DTOs;

/// <summary>移动端「App 安装」页：两端最新正式版本及可生成二维码的安装地址，任一端暂无版本时为 null。</summary>
public sealed class MobileAppInstallLinksDto
{
    public MobileAppInstallIosDto? Ios { get; init; }
    public MobileAppInstallAndroidDto? Android { get; init; }
}

public sealed class MobileAppInstallIosDto
{
    public string Version { get; init; } = string.Empty;
    public string BuildNumber { get; init; } = string.Empty;
    public string AppStoreUrl { get; init; } = string.Empty;
    public DateTime AppleVerifiedAtUtc { get; init; }
}

public sealed class MobileAppInstallAndroidDto
{
    public string EasBuildId { get; init; } = string.Empty;
    public string? AppVersion { get; init; }
    public string? AppBuildVersion { get; init; }
    /// <summary>本次构建的直接下载地址：优先腾讯云镜像，其次 EAS 产物；客户端可再换成稳定入口。</summary>
    public string DownloadUrl { get; init; } = string.Empty;
    public long? ArtifactSize { get; init; }
    public DateTime? CompletedAt { get; init; }
}
