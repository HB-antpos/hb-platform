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

    /// <summary>
    /// 考勤二维码面板状态正常时的后台刷新间隔（登记密钥 / 校时 / 连通性检查）。
    /// 二维码本身在本机每秒签发，可信时间用「服务器时间锚点 + 本机流逝时间」推算，不需要 15 秒一次的登记；
    /// 白天夜里都用这个间隔。
    /// </summary>
    public static readonly TimeSpan QrPanelHealthyRefreshInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 二维码面板尚未启用成功、离线、启用失败或需要重新校时的快速重试间隔：
    /// 保证「启用失败」「离线」之类的提示在网络恢复后尽快消失，不会因为放慢到 5 分钟而多挂好几分钟。
    /// </summary>
    public static readonly TimeSpan QrPanelRetryRefreshInterval = TimeSpan.FromSeconds(15);

    /// <summary>二维码面板下一次刷新应等待多久：状态健康时 5 分钟，否则 15 秒重试。</summary>
    public static TimeSpan GetQrPanelRefreshInterval(bool healthy)
    {
        return healthy ? QrPanelHealthyRefreshInterval : QrPanelRetryRefreshInterval;
    }

    /// <summary>给定的本地时间是否处于夜间静默窗口（跨午夜：22:00 ≤ t 或 t &lt; 06:00）。</summary>
    public static bool IsQuietHours(DateTimeOffset localNow)
    {
        var timeOfDay = localNow.TimeOfDay;
        return timeOfDay >= QuietStart || timeOfDay < QuietEnd;
    }

    /// <summary>下一次轮询延迟的下限，避免静默窗口结束瞬间算出接近 0 的延迟而空转。</summary>
    internal static readonly TimeSpan MinimumPollDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 「每次 tick 后重算下一次延迟」的轮询（挂单角标、收银员会话刷新）使用：
    /// - 白天：原间隔；
    /// - 夜间静默窗口：<see cref="QuietInterval"/>（绝不比原间隔更短），但不会越过窗口终点——
    ///   延迟被截到「距 06:00 的剩余时间」，保证窗口一结束就立刻补一次轮询并恢复原间隔，
    ///   而不是最多再等 5 分钟（早上开门第一单前权限 / 挂单数据已是最新）。
    /// 夜间最多每 5 分钟一次，窗口末尾那次可能更短，但不低于 <see cref="MinimumPollDelay"/>。
    /// </summary>
    public static TimeSpan GetNextPollDelay(DateTimeOffset localNow, TimeSpan normalInterval)
    {
        var interval = GetInterval(localNow, normalInterval);
        if (!IsQuietHours(localNow))
        {
            return interval;
        }

        var timeOfDay = localNow.TimeOfDay;
        // 跨午夜：22:00 之后要等到次日 06:00；00:00–06:00 直接等到当天 06:00。
        var untilQuietEnd = timeOfDay >= QuietStart
            ? TimeSpan.FromDays(1) - timeOfDay + QuietEnd
            : QuietEnd - timeOfDay;
        var delay = untilQuietEnd < interval ? untilQuietEnd : interval;
        return delay < MinimumPollDelay ? MinimumPollDelay : delay;
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
