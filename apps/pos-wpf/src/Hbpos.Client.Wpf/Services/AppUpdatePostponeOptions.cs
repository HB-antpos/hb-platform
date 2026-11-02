using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Hbpos.Client.Wpf.Services;

// 中文注释：运行中强制更新提示的“稍后安装”：每次推迟多久再提醒、同一版本最多推迟几次。
public sealed record AppUpdatePostponeOptions(TimeSpan Interval, int MaxCount)
{
    public const string IntervalMinutesConfigurationKey = "AppUpdate:ForcePostponeMinutes";

    public const string MaxCountConfigurationKey = "AppUpdate:ForcePostponeMaxCount";

    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(30);

    public const int DefaultMaxCount = 3;

    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(5);

    public static AppUpdatePostponeOptions Default { get; } = new(DefaultInterval, DefaultMaxCount);

    public bool IsEnabled => MaxCount > 0 && Interval > TimeSpan.Zero;

    public static AppUpdatePostponeOptions FromConfiguration(IConfiguration configuration)
    {
        var interval = DefaultInterval;
        var rawMinutes = configuration[IntervalMinutesConfigurationKey]?.Trim();
        if (!string.IsNullOrWhiteSpace(rawMinutes) &&
            int.TryParse(rawMinutes, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) &&
            minutes > 0)
        {
            // 中文注释：过短的间隔抬到 5 分钟，避免推迟后马上又弹遮罩。
            interval = TimeSpan.FromMinutes(Math.Max(minutes, MinimumInterval.TotalMinutes));
        }

        var maxCount = DefaultMaxCount;
        var rawMaxCount = configuration[MaxCountConfigurationKey]?.Trim();
        if (!string.IsNullOrWhiteSpace(rawMaxCount) &&
            int.TryParse(rawMaxCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            // 中文注释：0 表示不允许推迟，恢复为只能安装。
            maxCount = Math.Max(count, 0);
        }

        return new AppUpdatePostponeOptions(interval, maxCount);
    }
}
