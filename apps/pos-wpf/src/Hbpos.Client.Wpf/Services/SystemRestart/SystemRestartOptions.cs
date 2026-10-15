using Microsoft.Extensions.Configuration;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 「日结后提示重启收银机」的数值参数。全部来自 appsettings 的 <c>Maintenance:RestartPrompt:*</c>，不做界面输入；
/// 非法或越界的值一律回落到默认值，避免配置写错导致永远不提示或倒计时变成 0。
/// </summary>
public sealed record SystemRestartOptions(
    int UptimeThresholdDays,
    int CountdownSeconds,
    int UploadWaitSeconds,
    int SystemShutdownDelaySeconds)
{
    public const int DefaultUptimeThresholdDays = 7;
    public const int DefaultCountdownSeconds = 30;
    public const int DefaultUploadWaitSeconds = 30;

    // 中文注释：交给 shutdown.exe 的 /t 秒数。应用已先完成收尾再调命令，这 15 秒只是应用退出的缓冲，
    // 同时也是「应用退出卡住时系统仍会重启」的兜底。
    public const int DefaultSystemShutdownDelaySeconds = 15;

    public const string SectionName = "Maintenance:RestartPrompt";

    public static SystemRestartOptions Default { get; } = new(
        DefaultUptimeThresholdDays,
        DefaultCountdownSeconds,
        DefaultUploadWaitSeconds,
        DefaultSystemShutdownDelaySeconds);

    /// <summary>开机时长阈值。</summary>
    public TimeSpan UptimeThreshold => TimeSpan.FromDays(UptimeThresholdDays);

    public static SystemRestartOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new SystemRestartOptions(
            ReadInt(configuration, "UptimeDays", DefaultUptimeThresholdDays, min: 1, max: 365),
            ReadInt(configuration, "CountdownSeconds", DefaultCountdownSeconds, min: 5, max: 300),
            ReadInt(configuration, "UploadWaitSeconds", DefaultUploadWaitSeconds, min: 0, max: 300),
            ReadInt(configuration, "SystemShutdownDelaySeconds", DefaultSystemShutdownDelaySeconds, min: 5, max: 120));
    }

    private static int ReadInt(IConfiguration configuration, string name, int fallback, int min, int max)
    {
        var raw = configuration[$"{SectionName}:{name}"]?.Trim();
        return int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            && value >= min
            && value <= max
            ? value
            : fallback;
    }
}
