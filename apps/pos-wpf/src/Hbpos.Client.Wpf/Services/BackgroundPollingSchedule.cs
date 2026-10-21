namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 后台轮询节奏：营业时间保持原频率，夜间静默窗口内降频。
/// 收银机整夜开着时，每 15 秒仍会发连通性检查、运行状态心跳和考勤二维码密钥登记，
/// 夜里没有任何顾客操作，这些请求只是白白占用网络和服务器（也是 EdgeOne 请求额度的主要消耗）。
/// 设计要点：
/// - 只调整「周期性后台轮询」的间隔，不停止它们：保留离线恢复、待上传订单重试和运行状态上报，只是变慢；
/// - 付款等关键路径本来就会主动检查在线状态，不依赖这个定时器，所以夜间降频不影响收款；
/// - 窗口按设备本地时间判断（门店收银机的系统时间就是门店所在地时间），窗口结束后自动恢复原频率。
/// </summary>
public static class BackgroundPollingSchedule
{
    /// <summary>夜间静默窗口起点（设备本地时间，含）。</summary>
    internal static readonly TimeSpan QuietStart = TimeSpan.FromHours(22);

    /// <summary>夜间静默窗口终点（设备本地时间，不含）。</summary>
    internal static readonly TimeSpan QuietEnd = TimeSpan.FromHours(6);

    /// <summary>夜间静默窗口内的轮询间隔。</summary>
    public static readonly TimeSpan QuietInterval = TimeSpan.FromMinutes(5);

    /// <summary>给定的本地时间是否处于夜间静默窗口（跨午夜：22:00 ≤ t 或 t &lt; 06:00）。</summary>
    public static bool IsQuietHours(DateTimeOffset localNow)
    {
        var timeOfDay = localNow.TimeOfDay;
        return timeOfDay >= QuietStart || timeOfDay < QuietEnd;
    }

    /// <summary>
    /// 返回当前应使用的轮询间隔：夜间静默窗口内取 <see cref="QuietInterval"/>（但绝不比原间隔更短），其余时间用原间隔。
    /// </summary>
    public static TimeSpan GetInterval(DateTimeOffset localNow, TimeSpan normalInterval)
    {
        return IsQuietHours(localNow) && normalInterval < QuietInterval
            ? QuietInterval
            : normalInterval;
    }
}
