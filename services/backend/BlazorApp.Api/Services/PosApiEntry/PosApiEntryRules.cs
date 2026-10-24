using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Services.PosApiEntry;

/// <summary>
/// 收银入口页的纯规则：设备在线口径（与 Web 设备管理页 isDeviceRuntimeOnline 一致）、入口新鲜度与版本门槛。
/// 全部是无状态纯函数，便于单测。
/// </summary>
internal static class PosApiEntryRules
{
    /// <summary>支持「后台下发首选入口」的 WPF 最低版本（需建版 1.0.89）。</summary>
    internal static readonly AppMarketingVersion MinimumWpfVersion = new(1, 0, 89, 0);

    /// <summary>
    /// 入口回显的新鲜度窗口。POS API 对同一设备每 10 分钟才刷新一次 LastApiEntryAt，
    /// 窗口若也取 10 分钟，数据年龄在 0~11 分钟之间波动会让设备每个周期都闪成「未知」；
    /// 因此取 15 分钟（刷新周期 + 余量）。
    /// </summary>
    internal static readonly TimeSpan EntryFreshWindow = TimeSpan.FromMinutes(15);

    // 与 apps/web/src/services/deviceRegistrationService.ts 保持一致：白天 45 秒，夜间静默窗口（心跳间隔 5 分钟）12 分钟。
    internal static readonly TimeSpan OnlineStale = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan QuietOnlineStale = TimeSpan.FromMinutes(12);

    // 夜间静默窗口按澳洲东部时间 21:00–07:00（两侧各放宽 1 小时，覆盖悉尼 / 墨尔本 / 布里斯班的夏令时差）。
    private const int QuietStartHour = 21;
    private const int QuietEndHour = 7;

    private static readonly TimeZoneInfo SydneyTimeZone = ResolveSydneyTimeZone();

    private static TimeZoneInfo ResolveSydneyTimeZone()
    {
        foreach (var id in new[] { "Australia/Sydney", "AUS Eastern Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }

        // 极端环境没有时区数据库：退化为固定 UTC+10（只影响夜间窗口的边界判断）。
        return TimeZoneInfo.CreateCustomTimeZone("AEST", TimeSpan.FromHours(10), "AEST", "AEST");
    }

    /// <summary>设备心跳发生时刻是否落在收银端夜间静默窗口内。</summary>
    internal static bool IsInQuietHours(DateTime heartbeatUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(heartbeatUtc, DateTimeKind.Utc),
            SydneyTimeZone);
        return local.Hour >= QuietStartHour || local.Hour < QuietEndHour;
    }

    /// <summary>在线口径：在线标记为真、有心跳，且心跳没有过期（夜间窗口内的心跳放宽到 12 分钟）。</summary>
    internal static bool IsOnline(bool isOnlineFlag, DateTime? lastHeartbeatUtc, DateTime nowUtc)
    {
        if (!isOnlineFlag || lastHeartbeatUtc is null)
        {
            return false;
        }

        var heartbeat = lastHeartbeatUtc.Value;
        var stale = IsInQuietHours(heartbeat) ? QuietOnlineStale : OnlineStale;
        return nowUtc - heartbeat <= stale;
    }

    /// <summary>
    /// 设备最近一次写入的实际入口：值在白名单内且写入时间在新鲜窗口内才返回，否则 null（未知）。
    /// </summary>
    internal static string? FreshEntry(string? lastApiEntry, DateTime? lastApiEntryAtUtc, DateTime nowUtc)
    {
        var entry = PosApiEntryModes.Normalize(lastApiEntry);
        if (entry is null || lastApiEntryAtUtc is null)
        {
            return null;
        }

        var age = nowUtc - lastApiEntryAtUtc.Value;
        // 写入时间略超前于服务器时钟（多机时钟漂移）按新鲜处理。
        return age <= EntryFreshWindow ? entry : null;
    }

    /// <summary>
    /// 不认后台设置的 WPF：设备系统是 Windows（空值按 Windows，同设备页展示口径）且上报版本低于最低版本；
    /// 从未上报版本的老客户端同样算。手持 / iPad 暂不判断。
    /// </summary>
    internal static bool IsUnsupportedWpf(string? deviceSystem, string? appVersion)
    {
        var system = DeviceRegistrationDeviceSystems.NormalizeForDisplay(deviceSystem);
        if (!string.Equals(system, DeviceRegistrationDeviceSystems.Windows, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !AppMarketingVersion.TryParse(appVersion, out var version)
            || version.CompareTo(MinimumWpfVersion) < 0;
    }
}
