using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

// 首页「取单」按钮右上角角标：今天仍可取的挂单数量。
// 这里只管 UI 状态与刷新时机；统计口径（本机 + 共享、去重、门店今天）全部在 IHeldOrderTodayCountService。
public sealed partial class PosTerminalViewModel
{
    /// <summary>
    /// 低频兜底刷新：别的收银机共享/取走挂单，本机只能靠轮询得知。
    /// 这是营业时间的间隔；夜间静默窗口（22:00–06:00）内由 <see cref="BackgroundPollingSchedule"/> 放慢到 5 分钟。
    /// 夜间放慢不影响本机业务：本机挂单 / 取单走 <see cref="RefreshHeldOrderTodayCountAsync"/> 立即重算，不依赖这个定时器。
    /// </summary>
    private static readonly TimeSpan HeldOrderCountRefreshInterval = TimeSpan.FromSeconds(60);

    private const int HeldOrderBadgeMaxCount = 99;

    private readonly IHeldOrderTodayCountService? _heldOrderTodayCountService;

    // 并发控制：_heldOrderCountGate 保护下面四个字段。每次刷新取消上一次并递增代次，
    // 只有代次仍是最新的结果才允许落到界面；Dispose 同样递增代次，晚到的结果一律丢弃。
    private readonly object _heldOrderCountGate = new();
    private CancellationTokenSource? _heldOrderCountCancellation;
    private long _heldOrderCountGeneration;
    private bool _heldOrderCountInFlightForced;
    private bool _heldOrderCountFailureLogged;

    private ITimer? _heldOrderCountTimer;
    // 首页是否正在显示：只有显示时才定时刷新、才响应会话变化刷新（定时器回调在线程池线程读取，故 volatile）。
    private volatile bool _isScreenVisible;
    // 上次已处理的会话范围（门店 / 设备 / 联机 / 是否已登录收银员）：只有它变了才需要重算角标。
    // 「已登录收银员」也算范围：未登录时统计服务不会请求共享挂单（会 401），登录后要立刻补取，
    // 否则夜间 5 分钟的轮询会让角标在登录后最多落后 5 分钟。
    private (string StoreCode, string DeviceCode, bool IsOnline, bool IsCashierSignedIn) _heldOrderCountScope;

    /// <summary>今天仍可取的挂单张数（本机 + 本店共享，按挂单号去重）。</summary>
    [ObservableProperty]
    private int _heldOrderTodayCount;

    /// <summary>角标文字：0 → 空；1–99 → 数字；超过 99 → "99+"。</summary>
    public string HeldOrderBadgeText => FormatHeldOrderBadgeText(HeldOrderTodayCount);

    /// <summary>有挂单才显示角标。</summary>
    public bool IsHeldOrderBadgeVisible => HeldOrderTodayCount > 0;

    /// <summary>仅测试使用：最近一次由本 VM 内部触发的刷新任务，便于等待完成。</summary>
    internal Task? LastHeldOrderCountRefreshTask { get; private set; }

    internal static string FormatHeldOrderBadgeText(int count)
    {
        if (count <= 0)
        {
            return string.Empty;
        }

        return count > HeldOrderBadgeMaxCount
            ? $"{HeldOrderBadgeMaxCount}+"
            : count.ToString(CultureInfo.InvariantCulture);
    }

    partial void OnHeldOrderTodayCountChanged(int value)
    {
        OnPropertyChanged(nameof(HeldOrderBadgeText));
        OnPropertyChanged(nameof(IsHeldOrderBadgeVisible));
    }

    /// <summary>
    /// 立即重算角标。挂单成功、取单成功等「本机状态刚变」的场景调用，会抢占在途的旧刷新；
    /// <paramref name="forceRemoteRefresh"/> 为 true 时绕过远端短缓存。永不抛出：失败只记日志并保留旧值。
    /// </summary>
    public Task RefreshHeldOrderTodayCountAsync(bool forceRemoteRefresh = false)
    {
        return RunHeldOrderCountRefreshAsync(forceRemoteRefresh, coalesceIfRunning: false);
    }

    /// <summary>
    /// 首页变为可见时由导航层调用：立即刷新一次并启动低频刷新（白天 60 秒，夜间静默窗口 5 分钟）。
    /// </summary>
    public void OnScreenShown()
    {
        if (_disposed)
        {
            return;
        }

        var wasVisible = _isScreenVisible;
        _isScreenVisible = true;
        if (_heldOrderTodayCountService is null)
        {
            return;
        }

        StartHeldOrderCountTimer();
        if (!wasVisible)
        {
            // 进入首页的普通刷新：已有刷新在途（例如取单后的强制刷新）就并入，不重复。
            LastHeldOrderCountRefreshTask = RunHeldOrderCountRefreshAsync(
                forceRemoteRefresh: false,
                coalesceIfRunning: true);
        }
    }

    /// <summary>首页离开可见区时由导航层调用：停表，避免后台空转。</summary>
    public void OnScreenHidden()
    {
        _isScreenVisible = false;
        StopHeldOrderCountTimer();
    }

    private void StartHeldOrderCountTimer()
    {
        if (_heldOrderCountTimer is not null)
        {
            return;
        }

        var delay = GetNextHeldOrderCountDelay();
        _heldOrderCountTimer = _timeProvider.CreateTimer(
            OnHeldOrderCountTimerTick,
            null,
            delay,
            delay);
    }

    /// <summary>
    /// 下一次兜底刷新的等待时间：白天 60 秒，夜间静默窗口 5 分钟（窗口结束时立即补一次）。
    /// 用 <see cref="TimeProvider"/> 取本地时间，测试可控。
    /// </summary>
    private TimeSpan GetNextHeldOrderCountDelay() =>
        BackgroundPollingSchedule.GetNextPollDelay(_timeProvider.GetLocalNow(), HeldOrderCountRefreshInterval);

    /// <summary>每次 tick 后按当前本地时间重设间隔，进入 / 离开夜间静默窗口时自动切换。</summary>
    private void RescheduleHeldOrderCountTimer()
    {
        var timer = _heldOrderCountTimer;
        if (timer is null)
        {
            return;
        }

        var delay = GetNextHeldOrderCountDelay();
        try
        {
            timer.Change(delay, delay);
        }
        catch (ObjectDisposedException)
        {
            // 回调线程与离开首页（停表）并发：定时器已释放，无需再调度。
        }
    }

    private void StopHeldOrderCountTimer()
    {
        _heldOrderCountTimer?.Dispose();
        _heldOrderCountTimer = null;
    }

    private void OnHeldOrderCountTimerTick(object? state)
    {
        if (_disposed || !_isScreenVisible)
        {
            return;
        }

        RescheduleHeldOrderCountTimer();

        // 生产环境 Timer 回调在线程池线程，回到 Dispatcher 再刷新（结果写绑定属性）；
        // 测试环境没有 Application，直接在当前线程执行。
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(() => LastHeldOrderCountRefreshTask = RunHeldOrderCountRefreshAsync(
                forceRemoteRefresh: false,
                coalesceIfRunning: true));
            return;
        }

        LastHeldOrderCountRefreshTask = RunHeldOrderCountRefreshAsync(
            forceRemoteRefresh: false,
            coalesceIfRunning: true);
    }

    /// <summary>
    /// 会话的门店 / 设备 / 联机 / 是否已登录收银员变化后重算角标；其余会话字段（待同步数等）变化与角标无关。
    /// </summary>
    private void OnSessionChangedForHeldOrderCount(PosSessionState value)
    {
        var scope = (value.StoreCode, value.DeviceCode, value.IsOnline, IsCashierSignedIn: value.CashierSession is not null);
        var previous = _heldOrderCountScope;
        if (scope == previous)
        {
            return;
        }

        _heldOrderCountScope = scope;
        var storeOrDeviceChanged = scope.StoreCode != previous.StoreCode ||
            scope.DeviceCode != previous.DeviceCode;
        if (storeOrDeviceChanged)
        {
            // 旧门店的数字不能挂在新门店的界面上；在途的旧刷新也一并作废。
            InvalidateHeldOrderCountRefresh();
            HeldOrderTodayCount = 0;
        }

        if (_isScreenVisible && _heldOrderTodayCountService is not null)
        {
            // 换门店 / 设备：旧缓存属于别的门店，强制取最新；仅联机状态翻转时沿用缓存与失败退避，
            // 避免网络抖动反复强刷。无论哪种都抢占在途刷新（它用的是翻转前的联机快照）。
            LastHeldOrderCountRefreshTask = RunHeldOrderCountRefreshAsync(
                forceRemoteRefresh: storeOrDeviceChanged,
                coalesceIfRunning: false);
        }
    }

    private async Task RunHeldOrderCountRefreshAsync(bool forceRemoteRefresh, bool coalesceIfRunning)
    {
        var service = _heldOrderTodayCountService;
        if (service is null || _disposed)
        {
            return;
        }

        CancellationTokenSource cancellation;
        CancellationTokenSource? previous;
        long generation;
        lock (_heldOrderCountGate)
        {
            // 普通刷新（计时器 / 进入首页）遇到在途刷新直接并入，不抢占；
            // 挂单、取单触发的刷新必须抢占，否则在途刷新可能还是挂单前的读数。
            if (coalesceIfRunning && _heldOrderCountCancellation is not null)
            {
                return;
            }

            // 被抢占的在途刷新若带着「强制远端」意图，新一轮继承，别丢。
            forceRemoteRefresh |= _heldOrderCountInFlightForced;
            previous = _heldOrderCountCancellation;
            cancellation = new CancellationTokenSource();
            _heldOrderCountCancellation = cancellation;
            _heldOrderCountInFlightForced = forceRemoteRefresh;
            generation = ++_heldOrderCountGeneration;
        }

        CancelQuietly(previous);

        var session = Session;
        try
        {
            var count = await service.GetTodayCountAsync(session, forceRemoteRefresh, cancellation.Token);

            bool isLatest;
            bool recovered;
            lock (_heldOrderCountGate)
            {
                isLatest = generation == _heldOrderCountGeneration && !_disposed;
                recovered = isLatest && _heldOrderCountFailureLogged;
                if (recovered)
                {
                    _heldOrderCountFailureLogged = false;
                }
            }

            if (!isLatest)
            {
                // 已被更新的刷新或 Dispose 取代：旧结果不得覆盖新值。
                return;
            }

            HeldOrderTodayCount = Math.Max(0, count);
            if (recovered)
            {
                ConsoleLog.WriteInformation(
                    "HeldOrder",
                    $"held order today count refresh recovered storeCode={session.StoreCode} deviceCode={session.DeviceCode}");
            }
        }
        catch (OperationCanceledException)
        {
            // 被新一轮刷新或 Dispose 取消：正常，忽略。
        }
        catch (Exception ex)
        {
            bool shouldLog;
            lock (_heldOrderCountGate)
            {
                // 只在连续失败的第一次记 Warning，避免每分钟刷屏；保留旧值，不弹错。
                shouldLog = generation == _heldOrderCountGeneration &&
                    !_disposed &&
                    !_heldOrderCountFailureLogged;
                if (shouldLog)
                {
                    _heldOrderCountFailureLogged = true;
                }
            }

            if (shouldLog)
            {
                ConsoleLog.WriteWarning(
                    "HeldOrder",
                    $"held order today count refresh failed, keeping previous value storeCode={session.StoreCode} " +
                    $"deviceCode={session.DeviceCode} error={ex.GetType().Name} message={ex.Message}",
                    exception: ex);
            }
        }
        finally
        {
            var ownsCancellation = false;
            lock (_heldOrderCountGate)
            {
                if (generation == _heldOrderCountGeneration)
                {
                    _heldOrderCountCancellation = null;
                    _heldOrderCountInFlightForced = false;
                    ownsCancellation = true;
                }
            }

            // 被取代时由取代者负责 Cancel，这里不能 Dispose，否则对方 Cancel 可能撞上已释放对象。
            if (ownsCancellation)
            {
                cancellation.Dispose();
            }
        }
    }

    /// <summary>作废在途刷新（换门店 / 设备或 Dispose）：递增代次并取消，晚到结果被丢弃。</summary>
    private void InvalidateHeldOrderCountRefresh()
    {
        CancellationTokenSource? inFlight;
        lock (_heldOrderCountGate)
        {
            _heldOrderCountGeneration++;
            inFlight = _heldOrderCountCancellation;
            _heldOrderCountCancellation = null;
            _heldOrderCountInFlightForced = false;
        }

        CancelQuietly(inFlight);
    }

    private void DisposeHeldOrderCount()
    {
        _isScreenVisible = false;
        StopHeldOrderCountTimer();
        InvalidateHeldOrderCountRefresh();
    }

    private static void CancelQuietly(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 对方已结束并释放，无需再取消。
        }
    }
}
