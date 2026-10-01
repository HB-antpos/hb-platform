namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 启动阶段，枚举顺序即实际执行顺序；阶段首尾相接，进入下一阶段即视为上一阶段结束。
/// </summary>
public enum StartupPhase
{
    /// <summary>构建并启动 Host：本地数据库与后台服务。</summary>
    Services = 0,

    /// <summary>加载主题资源、构造主窗口、恢复窗口模式与配色。</summary>
    Interface = 1,

    /// <summary>启动时的更新检查（网络）。</summary>
    Update = 2,

    /// <summary>设备授权校验（网络，离线时回落本地缓存）。</summary>
    Device = 3,

    /// <summary>加载本地商品、特殊商品等收银数据。</summary>
    Catalog = 4,

    /// <summary>显示主窗口并等待首帧绘制完成。</summary>
    Display = 5
}

/// <summary>
/// 启动页的对外控制面。启动页是置顶窗口且运行在独立线程上，不能作为对话框的 owner，
/// 启动阶段要弹出对话框（如启动更新确认）时必须先让它让位。
/// </summary>
public interface IStartupScreen
{
    /// <summary>临时隐藏启动页，并把当前阶段标记为混入了用户等待；释放返回值后恢复显示。</summary>
    IDisposable SuspendForDialog();
}

public sealed record StartupProgressSnapshot(
    double Progress,
    StartupPhase? CurrentPhase,
    TimeSpan Elapsed,
    TimeSpan CurrentPhaseElapsed,
    TimeSpan CurrentPhaseExpected,
    bool IsCompleted);

/// <summary>
/// 线程安全的启动进度追踪器：主线程上报阶段切换，启动页线程按帧读取快照。
/// 进度按"本机历史耗时"给每个阶段分配权重，阶段内随真实经过时间推进，
/// 因此进度条与时间近似成正比；超出预期时渐近放缓，永远不会越过阶段终点。
/// </summary>
public sealed class StartupProgressTracker
{
    public static readonly IReadOnlyList<StartupPhase> Phases = Enum.GetValues<StartupPhase>();

    // 预期内的前 80% 与时间严格成正比；超出后按指数渐近到 95%，并与线性段在 0.8 处导数连续。
    private const double LinearFraction = 0.8;
    private const double AsymptoteSpan = 0.15;

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly IReadOnlyDictionary<StartupPhase, TimeSpan> _expected;
    private readonly double _totalExpectedMs;
    private readonly long _startTimestamp;
    private readonly long?[] _phaseStartTimestamps = new long?[Phases.Count];
    private readonly bool[] _phaseInterrupted = new bool[Phases.Count];
    private StartupPhase? _currentPhase;
    private long? _completedTimestamp;

    public StartupProgressTracker(
        IReadOnlyDictionary<StartupPhase, TimeSpan> expectedDurations,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(expectedDurations);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _expected = Phases.ToDictionary(
            phase => phase,
            phase => expectedDurations.TryGetValue(phase, out var expected) && expected > TimeSpan.Zero
                ? expected
                : StartupTimingProfile.DefaultExpectedDurations[phase]);
        _totalExpectedMs = _expected.Values.Sum(value => value.TotalMilliseconds);
        _startTimestamp = _timeProvider.GetTimestamp();
    }

    public StartupPhase? CurrentPhase
    {
        get
        {
            lock (_gate)
            {
                return _currentPhase;
            }
        }
    }

    public bool IsCompleted
    {
        get
        {
            lock (_gate)
            {
                return _completedTimestamp is not null;
            }
        }
    }

    public TimeSpan GetExpectedDuration(StartupPhase phase) => _expected[phase];

    /// <summary>本次启动是否真正进入过某阶段（跳过的阶段返回 false）。</summary>
    public bool HasEntered(StartupPhase phase)
    {
        lock (_gate)
        {
            return _phaseStartTimestamps[(int)phase] is not null;
        }
    }

    /// <summary>进入某阶段；阶段只能前进，跳过的阶段按零耗时完成，重复或倒退的调用被忽略。</summary>
    public void Enter(StartupPhase phase)
    {
        lock (_gate)
        {
            if (_completedTimestamp is not null || (_currentPhase is { } current && phase <= current))
            {
                return;
            }

            _phaseStartTimestamps[(int)phase] = _timeProvider.GetTimestamp();
            _currentPhase = phase;
        }
    }

    /// <summary>
    /// 标记当前阶段混入了等待用户操作（如启动更新弹窗）的时间，本次不计入本机耗时档案。
    /// </summary>
    public void MarkCurrentPhaseInterrupted()
    {
        lock (_gate)
        {
            // 启动完成后的弹框（如运行期后台更新）与启动耗时无关，不再改动已结束的阶段。
            if (_completedTimestamp is null && _currentPhase is { } current)
            {
                _phaseInterrupted[(int)current] = true;
            }
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _completedTimestamp ??= _timeProvider.GetTimestamp();
        }
    }

    public StartupProgressSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var now = _completedTimestamp ?? _timeProvider.GetTimestamp();
            var elapsed = _timeProvider.GetElapsedTime(_startTimestamp, now);
            if (_completedTimestamp is not null)
            {
                return new StartupProgressSnapshot(1, null, elapsed, TimeSpan.Zero, TimeSpan.Zero, true);
            }

            if (_currentPhase is not { } current)
            {
                return new StartupProgressSnapshot(0, null, elapsed, TimeSpan.Zero, TimeSpan.Zero, false);
            }

            var completedMs = 0d;
            foreach (var phase in Phases)
            {
                if (phase >= current)
                {
                    break;
                }

                completedMs += _expected[phase].TotalMilliseconds;
            }

            var expected = _expected[current];
            var phaseElapsed = _timeProvider.GetElapsedTime(_phaseStartTimestamps[(int)current]!.Value, now);
            var partialMs = expected.TotalMilliseconds *
                PhaseFraction(phaseElapsed.TotalMilliseconds / expected.TotalMilliseconds);
            var progress = Math.Clamp((completedMs + partialMs) / _totalExpectedMs, 0, 1);
            return new StartupProgressSnapshot(progress, current, elapsed, phaseElapsed, expected, false);
        }
    }

    /// <summary>
    /// 本次启动各阶段的实际耗时；只返回真正执行过、且没有混入用户等待的阶段，
    /// 未完成的启动返回空集合，避免把失败或中断的启动写进档案。
    /// </summary>
    public IReadOnlyDictionary<StartupPhase, TimeSpan> GetMeasuredDurations()
    {
        lock (_gate)
        {
            if (_completedTimestamp is not { } completedAt)
            {
                return new Dictionary<StartupPhase, TimeSpan>();
            }

            var durations = new Dictionary<StartupPhase, TimeSpan>();
            for (var index = 0; index < Phases.Count; index++)
            {
                if (_phaseStartTimestamps[index] is not { } start || _phaseInterrupted[index])
                {
                    continue;
                }

                var end = completedAt;
                for (var next = index + 1; next < Phases.Count; next++)
                {
                    if (_phaseStartTimestamps[next] is { } nextStart)
                    {
                        end = nextStart;
                        break;
                    }
                }

                durations[Phases[index]] = _timeProvider.GetElapsedTime(start, end);
            }

            return durations;
        }
    }

    /// <summary>阶段内完成比例：x 为"已用时 / 预期时长"，返回值恒小于 1。</summary>
    internal static double PhaseFraction(double x)
    {
        if (double.IsNaN(x) || x <= 0)
        {
            return 0;
        }

        if (x <= LinearFraction)
        {
            return x;
        }

        return LinearFraction + AsymptoteSpan * (1 - Math.Exp(-(x - LinearFraction) / AsymptoteSpan));
    }

    /// <summary>
    /// 显示值平滑追赶目标值：时间常数 90ms，约 250ms 追上阶段提前完成造成的跳变；只增不减。
    /// </summary>
    internal static double SmoothTowards(double displayed, double target, TimeSpan frameInterval)
    {
        if (target <= displayed)
        {
            return displayed;
        }

        var dtMs = Math.Max(0, frameInterval.TotalMilliseconds);
        var next = displayed + (target - displayed) * (1 - Math.Exp(-dtMs / 90d));
        // 距离目标不足 0.2% 时直接贴合，避免最后一段无限逼近导致迟迟不到 100%。
        return target - next < 0.002 ? target : next;
    }
}
