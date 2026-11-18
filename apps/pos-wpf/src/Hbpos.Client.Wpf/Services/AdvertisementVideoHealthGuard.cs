namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 客显广告视频的“自我保护”：视频连续打开很慢说明这台机器扛不住（实测 POS_1012_0556 副屏软件渲染 + 解码把整机拖到
/// 付款要 5 秒、本地 SQLite 写入变慢 10 倍，而同一版本的其它收银机完全正常），此时暂停视频广告一段时间，
/// 保证收银不被客显广告拖累；图片 / GIF 广告不受影响，到期后自动再试。
/// 只在 UI 线程使用，不做线程同步；不依赖 WPF 类型，便于直接单测。
/// </summary>
internal sealed class AdvertisementVideoHealthGuard
{
    /// <summary>打开耗时达到这个值就算一次“慢打开”（与客显日志里 slow-open 的阈值一致）。</summary>
    internal static readonly TimeSpan DefaultSlowOpenThreshold = TimeSpan.FromSeconds(3);

    /// <summary>连续几次慢打开触发暂停；正常机器偶发一次慢（磁盘刚唤醒等）不会误触发。</summary>
    internal const int DefaultConsecutiveSlowLimit = 3;

    /// <summary>暂停时长；到期后下一次轮到视频就重新尝试，仍然慢会再暂停一轮。</summary>
    internal static readonly TimeSpan DefaultSuspendDuration = TimeSpan.FromMinutes(30);

    private readonly TimeProvider _timeProvider;
    private readonly int _consecutiveSlowLimit;
    private int _consecutiveSlowOpens;
    private DateTimeOffset? _suspendedUntil;

    internal AdvertisementVideoHealthGuard(
        TimeProvider? timeProvider = null,
        TimeSpan? slowOpenThreshold = null,
        int consecutiveSlowLimit = DefaultConsecutiveSlowLimit,
        TimeSpan? suspendDuration = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        SlowOpenThreshold = slowOpenThreshold ?? DefaultSlowOpenThreshold;
        SuspendDuration = suspendDuration ?? DefaultSuspendDuration;
        _consecutiveSlowLimit = Math.Max(1, consecutiveSlowLimit);
    }

    internal TimeSpan SlowOpenThreshold { get; }

    internal TimeSpan SuspendDuration { get; }

    internal int ConsecutiveSlowLimit => _consecutiveSlowLimit;

    /// <summary>
    /// 现在能不能新开一条视频。暂停期间返回 <see cref="VideoPlaybackPermission.Suspended"/>；
    /// 暂停刚到期的那一次返回 <see cref="VideoPlaybackPermission.Resumed"/>（调用方据此记一条恢复日志，之后恢复为 Allowed）。
    /// </summary>
    internal VideoPlaybackPermission Check()
    {
        if (_suspendedUntil is not { } until)
        {
            return VideoPlaybackPermission.Allowed;
        }

        if (_timeProvider.GetUtcNow() < until)
        {
            return VideoPlaybackPermission.Suspended;
        }

        // 到期：只需清掉暂停标记。慢打开计数在触发暂停时已清零、暂停期间 RecordOpen 又直接忽略，
        // 所以恢复后是从零重新判断，不会因为残留计数让第一次慢打开就又被暂停。
        _suspendedUntil = null;
        return VideoPlaybackPermission.Resumed;
    }

    /// <summary>
    /// 记录一次视频打开耗时。返回 true 表示这一次触发了暂停（调用方记日志并立刻停掉当前视频）。
    /// 只要出现一次快速打开就清零连续计数——要的是“持续很慢”，不是“累计慢过几次”。
    /// </summary>
    internal bool RecordOpen(TimeSpan openElapsed)
    {
        // 暂停期间不会新开视频；个别排队中的旧事件迟到也不重复计数。
        if (_suspendedUntil is not null)
        {
            return false;
        }

        if (openElapsed < SlowOpenThreshold)
        {
            _consecutiveSlowOpens = 0;
            return false;
        }

        _consecutiveSlowOpens++;
        if (_consecutiveSlowOpens < _consecutiveSlowLimit)
        {
            return false;
        }

        _consecutiveSlowOpens = 0;
        _suspendedUntil = _timeProvider.GetUtcNow() + SuspendDuration;
        return true;
    }
}

internal enum VideoPlaybackPermission
{
    Allowed,
    Suspended,
    Resumed,
}
