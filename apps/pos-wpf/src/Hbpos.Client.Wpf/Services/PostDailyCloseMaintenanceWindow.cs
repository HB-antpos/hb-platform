namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 日结后的提前维护窗口：当天营业日的日结保存成功后打开，到夜间窗口结束（默认次日 08:00）关闭。
/// 窗口打开且收银员已登出（日结后会自动登出）时，WPF 无人值守安装与 RustDesk 升级不必等到 18:00（周四 20:00），
/// 照样要过各自调度器的连续两轮收银空闲判定。只存在内存里：应用重开本来就要重新登录，过了夜间窗口也自然作废。
/// </summary>
public interface IPostDailyCloseMaintenanceWindow
{
    /// <summary>日结保存成功后调用（可在任意线程）；只有营业日是本机今天才打开，补做往日日结不会提前。</summary>
    void Open(DateTime businessDate);

    /// <summary>窗口打开中、还没到关闭时间，且当前没有收银员登录。</summary>
    bool IsOpen { get; }
}

public sealed class PostDailyCloseMaintenanceWindow(
    AppUpdateUnattendedInstallOptions windowOptions,
    ICashierSessionContext cashierSessionContext,
    TimeProvider? timeProvider = null) : IPostDailyCloseMaintenanceWindow
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private DateTimeOffset? _closesAtUtc;

    public void Open(DateTime businessDate)
    {
        // 夜间安装被配置关闭（off / Debug 构建）时，日结后也不提前安装。
        if (!windowOptions.IsEnabled)
        {
            return;
        }

        var localNow = _clock.GetLocalNow();
        if (businessDate.Date != localNow.Date)
        {
            ConsoleLog.WriteInformation(
                "PostDailyCloseWindow",
                $"not opened: businessDate={businessDate:yyyy-MM-dd} is not today={localNow:yyyy-MM-dd}");
            return;
        }

        var closesAt = NextWindowEnd(localNow, _clock.LocalTimeZone, windowOptions.WindowEnd);
        lock (_gate)
        {
            _closesAtUtc = closesAt.ToUniversalTime();
        }

        ConsoleLog.WriteInformation("PostDailyCloseWindow", $"opened until={closesAt:O}");
    }

    public bool IsOpen
    {
        get
        {
            DateTimeOffset? closesAtUtc;
            lock (_gate)
            {
                closesAtUtc = _closesAtUtc;
            }

            // 收银员又登录（日结后继续收银、或自动登出被取消）时暂停提前窗口，登出后恢复；夜间窗口不受影响。
            return closesAtUtc is { } closes &&
                _clock.GetUtcNow() < closes &&
                cashierSessionContext.CurrentSession is null;
        }
    }

    /// <summary>本机时间下一个窗口结束时刻（严格晚于现在）：08:00 之前日结取今天 08:00，否则取明天 08:00。</summary>
    internal static DateTimeOffset NextWindowEnd(DateTimeOffset localNow, TimeZoneInfo timeZone, TimeOnly windowEnd)
    {
        var today = DateTime.SpecifyKind(localNow.Date + windowEnd.ToTimeSpan(), DateTimeKind.Unspecified);
        var end = localNow.DateTime < today ? today : today.AddDays(1);
        return new DateTimeOffset(end, timeZone.GetUtcOffset(end));
    }
}
