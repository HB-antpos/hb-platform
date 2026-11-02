using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Hbpos.Client.Wpf.Services;

public sealed record CatalogAutoSyncOptions(TimeSpan Interval, TimeSpan MaxJitter)
{
    public const string IntervalMinutesConfigurationKey = "CatalogSync:AutoSyncIntervalMinutes";

    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan MaxJitterCap = TimeSpan.FromMinutes(3);

    public static CatalogAutoSyncOptions Disabled { get; } = new(TimeSpan.Zero, TimeSpan.Zero);

    public bool IsEnabled => Interval > TimeSpan.Zero;

    public static CatalogAutoSyncOptions FromConfiguration(IConfiguration configuration)
    {
        var raw = configuration[IntervalMinutesConfigurationKey]?.Trim();
        if (string.IsNullOrWhiteSpace(raw) ||
            !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            return Create(DefaultInterval);
        }

        // 中文注释：0 或负数表示关闭运行中自动同步；过短的间隔抬到 10 分钟，避免全部收银机频繁请求同步计划。
        return minutes <= 0
            ? Disabled
            : Create(TimeSpan.FromMinutes(Math.Max(minutes, MinimumInterval.TotalMinutes)));
    }

    public static CatalogAutoSyncOptions Create(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            return Disabled;
        }

        // 中文注释：每轮额外随机等待最多 1/10 间隔（上限 3 分钟）：凌晨统一重启的收银机不会每次都同一秒请求服务端。
        var jitter = TimeSpan.FromTicks(Math.Min(interval.Ticks / 10, MaxJitterCap.Ticks));
        return new CatalogAutoSyncOptions(interval, jitter);
    }
}

public enum CatalogAutoSyncOutcome
{
    Succeeded,
    Failed,
    FullDownloadDeferred,
    Canceled
}

/// <summary>一次自动同步的启动参数：Reason 只用于日志；AllowFullDownload 为 false 时服务端要求全量就推迟。</summary>
public sealed record CatalogAutoSyncStart(string Reason, bool AllowFullDownload);

/// <summary>
/// 收银端运行中的商品目录自动同步节奏（纯状态机，不发请求、不碰界面，由连通性探测节拍驱动）。
/// 设计要点：
/// - 启动同步之后每隔 <see cref="CatalogAutoSyncOptions.Interval"/> 同步一次；启动、手动、重置、自动任一成功都重新计时；
/// - 启动时离线或运行中断网后恢复联网，下一拍补一次（失败退避期间不提前，避免坏网络反复重试）；
/// - 失败按 5 → 10 → 20 → 30 分钟逐步拉长，成功后恢复正常间隔；
/// - 白天只做无变化 / 增量；服务端要求全量时推迟，并暂停白天的自动检查（基准版本回不来，再问也是全量），
///   等夜间静默窗口（<see cref="BackgroundPollingSchedule.IsQuietHours"/>）空闲时再允许全量，或等启动 / 手动同步成功；
/// - 是否空闲、是否在线、有没有别的同步在跑由调用方每拍传入。
/// </summary>
public sealed class CatalogAutoSyncScheduler(CatalogAutoSyncOptions options, TimeProvider? timeProvider = null)
{
    internal static readonly TimeSpan[] FailureBackoff =
    [
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(20),
        TimeSpan.FromMinutes(30)
    ];

    /// <summary>两次自动同步开始之间的最小间隔：联网状态抖动时补同步也不会更频繁。</summary>
    internal static readonly TimeSpan MinimumAttemptGap = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private bool _armed;
    private bool _inFlight;
    private bool _wasOnline;
    private bool _fullDownloadPending;
    private int _consecutiveFailures;
    private DateTimeOffset _nextDueUtc;
    private DateTimeOffset? _lastAttemptStartedUtc;
    private string _nextReason = "interval";

    public CatalogAutoSyncOptions Options => options;

    public bool IsFullDownloadPending
    {
        get
        {
            lock (_gate)
            {
                return _fullDownloadPending;
            }
        }
    }

    internal DateTimeOffset NextDueUtc
    {
        get
        {
            lock (_gate)
            {
                return _nextDueUtc;
            }
        }
    }

    /// <summary>
    /// 启动同步发起时调用。initialSyncStarted=true：启动同步正在跑，由它的结果重新计时；
    /// false（启动时离线跳过了）：联网后立即补一次。不清除进行中的标记，旧的自动同步仍由它自己的 Complete 收尾。
    /// </summary>
    public void Arm(bool initialSyncStarted)
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            _armed = true;
            _wasOnline = initialSyncStarted;
            _fullDownloadPending = false;
            _consecutiveFailures = 0;
            _lastAttemptStartedUtc = null;
            if (initialSyncStarted)
            {
                ScheduleNormal(now);
            }
            else
            {
                _nextDueUtc = now;
                _nextReason = "startup-offline";
            }
        }
    }

    /// <summary>切换服务器等重新初始化时停用，等下一次 <see cref="Arm"/>。</summary>
    public void Disarm()
    {
        lock (_gate)
        {
            _armed = false;
        }
    }

    /// <summary>启动、手动、重置同步成功：清掉失败计数与待做全量，按正常间隔重新计时。</summary>
    public void RecordSucceeded()
    {
        lock (_gate)
        {
            RecordSucceededCore(_timeProvider.GetUtcNow());
        }
    }

    /// <summary>启动、手动同步失败：和自动同步失败一样逐步退避。</summary>
    public void RecordFailed()
    {
        lock (_gate)
        {
            RecordFailedCore(_timeProvider.GetUtcNow());
        }
    }

    /// <summary>
    /// 每个连通性探测节拍调用一次。返回 null 表示这拍不同步；否则调用方必须启动同步，并在结束后调用 <see cref="Complete"/>。
    /// </summary>
    public CatalogAutoSyncStart? TryBegin(bool isOnline, bool isIdle, bool isSyncActive)
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (_armed && isOnline && !_wasOnline && _consecutiveFailures == 0 && _nextDueUtc > now)
            {
                // 中文注释：恢复联网补一次；失败退避期间不提前，坏网络下的抖动不会把退避打回 5 分钟。
                _nextDueUtc = now;
                _nextReason = "reconnected";
            }

            _wasOnline = isOnline;
            if (!_armed || _inFlight || !options.IsEnabled || !isOnline || !isIdle || isSyncActive)
            {
                return null;
            }

            var isQuietHours = BackgroundPollingSchedule.IsQuietHours(_timeProvider.GetLocalNow());
            if (_fullDownloadPending && !isQuietHours)
            {
                // 中文注释：白天已经推迟过全量，再问服务端也还是全量，留到夜间或启动 / 手动同步。
                return null;
            }

            if (now < _nextDueUtc ||
                (_lastAttemptStartedUtc is { } lastAttempt && now - lastAttempt < MinimumAttemptGap))
            {
                return null;
            }

            _inFlight = true;
            _lastAttemptStartedUtc = now;
            var reason = _fullDownloadPending ? "deferred-full" : _nextReason;
            return new CatalogAutoSyncStart(reason, AllowFullDownload: isQuietHours);
        }
    }

    /// <summary>自动同步结束（含失败、取消、推迟全量）后调用。</summary>
    public void Complete(CatalogAutoSyncOutcome outcome)
    {
        lock (_gate)
        {
            _inFlight = false;
            var now = _timeProvider.GetUtcNow();
            switch (outcome)
            {
                case CatalogAutoSyncOutcome.Succeeded:
                    RecordSucceededCore(now);
                    break;
                case CatalogAutoSyncOutcome.Failed:
                    RecordFailedCore(now);
                    break;
                case CatalogAutoSyncOutcome.FullDownloadDeferred:
                    // 中文注释：服务端正常应答，只是要全量；不算失败，不拉长退避。
                    _consecutiveFailures = 0;
                    _fullDownloadPending = true;
                    ScheduleNormal(now);
                    break;
                case CatalogAutoSyncOutcome.Canceled:
                    // 中文注释：被重置或关机打断，不算失败；最小间隔后再看（重置成功会自己重新计时）。
                    if (_nextDueUtc < now + MinimumAttemptGap)
                    {
                        _nextDueUtc = now + MinimumAttemptGap;
                    }

                    break;
            }
        }
    }

    private void RecordSucceededCore(DateTimeOffset now)
    {
        _consecutiveFailures = 0;
        _fullDownloadPending = false;
        ScheduleNormal(now);
    }

    private void RecordFailedCore(DateTimeOffset now)
    {
        _consecutiveFailures++;
        _nextDueUtc = now + FailureBackoff[Math.Min(_consecutiveFailures, FailureBackoff.Length) - 1];
        _nextReason = "retry";
    }

    private void ScheduleNormal(DateTimeOffset now)
    {
        var jitter = options.MaxJitter > TimeSpan.Zero
            ? TimeSpan.FromTicks(Random.Shared.NextInt64(options.MaxJitter.Ticks + 1))
            : TimeSpan.Zero;
        _nextDueUtc = now + options.Interval + jitter;
        _nextReason = "interval";
    }
}
