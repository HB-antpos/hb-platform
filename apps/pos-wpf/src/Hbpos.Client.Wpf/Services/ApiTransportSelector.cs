namespace Hbpos.Client.Wpf.Services;

/// <summary>入口选择器的只读快照（设置页展示、日志使用）。</summary>
/// <param name="PreferredMode">后台设置的首选入口代号（edge / aws / direct；未设置时为 edge）。</param>
/// <param name="ActiveMode">此刻读请求会走的入口代号；当前地址不在别名组内时为 null。</param>
/// <param name="UsingFallback">首选入口是否正在冷却、已回退到备用入口。</param>
public sealed record ApiTransportStatus(string PreferredMode, string? ActiveMode, bool UsingFallback);

/// <summary>
/// 选择实际发请求的传输地址（多入口），并在连接失败时做带退避的故障转移。
/// 设计要点：
/// - 候选顺序＝后台下发的首选 + 其余按固定优先级 aws → edge → direct（见 <see cref="ApiEntryModes.GetOrder"/>）；
/// - 每个入口各自记冷却与退避（2/4/8/16/30 分钟）：连接阶段失败（域名解析 / 建连 / TLS）进入冷却，成功清零；
/// - 取址＝顺序中第一个不在冷却的入口；全部在冷却则取冷却最早结束的；
/// - 冷却结束的入口由读请求（GET/HEAD）去试探，写请求（POST/PUT 等）失败了不会重放，不能拿来探路；
/// - 首选入口变化时清零各入口的冷却与退避，下一个读请求直接走新首选；
/// - 不在别名组内的地址只有一个候选，完全不参与切换。
/// 状态是进程内共享的（注册为单例），线程安全。
/// </summary>
public sealed class ApiTransportSelector
{
    private static readonly TimeSpan[] BackoffSteps =
    [
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(4),
        TimeSpan.FromMinutes(8),
        TimeSpan.FromMinutes(16),
        TimeSpan.FromMinutes(30),
    ];

    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();

    // 按规范身份（别名折算后的地址）分组，每组内按主机名记各入口的状态。
    private readonly Dictionary<string, Dictionary<string, EntryState>> _groups = new(StringComparer.Ordinal);

    // 已规范化的首选入口代号（edge / aws / direct）；后台未设置＝edge。
    private string _preferredMode = ApiEntryModes.Default;

    public ApiTransportSelector(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>当前生效的首选入口代号（未设置时为 edge）。</summary>
    public string PreferredMode
    {
        get
        {
            lock (_gate)
            {
                return _preferredMode;
            }
        }
    }

    /// <summary>
    /// 设置后台下发的首选入口。null＝后台未设置，按 edge；不认识的取值一律忽略、保持当前设置不变。
    /// 生效的首选真的变了（null 与 "edge" 视为相同）才清零各入口的冷却与退避——
    /// 每 60 秒重复下发同一个值时不能把正在冷却的坏线路重新放出来。
    /// </summary>
    /// <returns>true＝取值被接受（含与当前相同）；false＝不认识，已忽略。</returns>
    public bool SetPreferredMode(string? mode)
    {
        string normalized;
        if (mode is null)
        {
            normalized = ApiEntryModes.Default;
        }
        else if (!ApiEntryModes.TryParse(mode, out normalized))
        {
            return false;
        }

        lock (_gate)
        {
            if (!string.Equals(_preferredMode, normalized, StringComparison.Ordinal))
            {
                _preferredMode = normalized;
                _groups.Clear();
            }
        }

        return true;
    }

    /// <summary>
    /// 返回现在应该使用的传输地址。configured 是当前配置的地址（只提供协议 / 端口 / 路径，入口顺序由首选入口决定）。
    /// allowProbe：这次请求是否允许去「试探」冷却已结束、尚未确认恢复的入口。读请求（GET/HEAD）失败了会当场换入口重试，
    /// 可以试探；写请求（POST/PUT 等）失败了不会重放，不能拿来探路——否则恰好是一次收款 / 同步去撞一个仍然
    /// 坏掉的入口。所以写请求传 false，优先走已确认可用（没有失败记录）的入口，等读请求确认恢复后再切回。
    /// </summary>
    public Uri Resolve(Uri configured, bool allowProbe = true)
    {
        lock (_gate)
        {
            var candidates = ApiEndpointAliases.GetCandidates(configured, _preferredMode);
            if (candidates.Count == 1)
            {
                return configured;
            }

            var states = GetStates(configured);
            var now = _timeProvider.GetUtcNow();

            // 1) 顺序中第一个可用的入口：读请求接受「冷却已结束待试探」的入口，写请求只接受没有失败记录的入口。
            foreach (var candidate in candidates)
            {
                var state = GetState(states, candidate);
                if (state.IsCooling(now))
                {
                    continue;
                }

                if (allowProbe || state.IsClean)
                {
                    return candidate;
                }
            }

            // 2) 写请求在没有「干净」入口时，退而求其次用顺序中第一个冷却已结束的入口（它至少不在已知故障期内）。
            foreach (var candidate in candidates)
            {
                if (!GetState(states, candidate).IsCooling(now))
                {
                    return candidate;
                }
            }

            // 3) 全部在冷却：取冷却最早结束的（并列时取顺序靠前的），总得有一个入口去尝试。
            var earliest = candidates[0];
            var earliestUntil = GetState(states, earliest).CooldownUntil;
            foreach (var candidate in candidates.Skip(1))
            {
                var until = GetState(states, candidate).CooldownUntil;
                if (until < earliestUntil)
                {
                    earliest = candidate;
                    earliestUntil = until;
                }
            }

            return earliest;
        }
    }

    /// <summary>
    /// 取「顺序中紧接在 used 之后」的备用传输地址（used 已是最后一个时绕回开头）；没有备用时返回 null。
    /// 优先跳过仍在冷却的入口（已知坏的线路重试没有意义）；全都在冷却时才取紧接着的下一个。
    /// 调用方只用它换一次重试，不会把所有候选逐个试一遍。
    /// </summary>
    public Uri? GetFallback(Uri configured, Uri used)
    {
        lock (_gate)
        {
            var candidates = ApiEndpointAliases.GetCandidates(configured, _preferredMode);
            if (candidates.Count == 1)
            {
                return null;
            }

            var usedIndex = IndexOfHost(candidates, used);
            var states = GetStates(configured);
            var now = _timeProvider.GetUtcNow();
            Uri? next = null;
            for (var step = 1; step <= candidates.Count; step++)
            {
                var candidate = candidates[(usedIndex + step) % candidates.Count];
                if (string.Equals(candidate.Host, used.Host, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                next ??= candidate;
                if (!GetState(states, candidate).IsCooling(now))
                {
                    return candidate;
                }
            }

            return next;
        }
    }

    /// <summary>上报一次连接阶段失败：该入口进入冷却，并按阶梯升级退避。</summary>
    public void ReportConnectFailure(Uri configured, Uri used)
    {
        lock (_gate)
        {
            var candidates = ApiEndpointAliases.GetCandidates(configured, _preferredMode);
            if (candidates.Count == 1 || IndexOfHost(candidates, used) < 0)
            {
                return;
            }

            var state = GetState(GetStates(configured), used);
            var now = _timeProvider.GetUtcNow();
            if (state.IsCooling(now))
            {
                // 已经处于冷却：这是更早派发出去的并发请求迟到的失败。不再升级退避——否则一次故障会被
                // N 个并发请求一次顶满到 30 分钟；调用方仍会让读请求换备用入口重试。
                return;
            }

            state.CooldownUntil = now + BackoffSteps[Math.Min(state.BackoffStep, BackoffSteps.Length - 1)];
            state.BackoffStep = Math.Min(state.BackoffStep + 1, BackoffSteps.Length - 1);
            state.HasFailed = true;
        }
    }

    /// <summary>上报一次成功：该入口线路可用，清零它的冷却与退避。</summary>
    public void ReportSuccess(Uri configured, Uri used)
    {
        lock (_gate)
        {
            var candidates = ApiEndpointAliases.GetCandidates(configured, _preferredMode);
            if (candidates.Count == 1 || IndexOfHost(candidates, used) < 0)
            {
                return;
            }

            var states = GetStates(configured);
            if (states.TryGetValue(used.Host, out var state))
            {
                state.Reset();
            }
        }
    }

    /// <summary>当前是否处于「首选入口冷却中、已切到备用入口」状态（供日志/测试使用）。</summary>
    public bool IsUsingFallback(Uri configured)
    {
        lock (_gate)
        {
            var candidates = ApiEndpointAliases.GetCandidates(configured, _preferredMode);
            if (candidates.Count == 1)
            {
                return false;
            }

            return GetState(GetStates(configured), candidates[0]).IsCooling(_timeProvider.GetUtcNow());
        }
    }

    /// <summary>
    /// 只读状态：后台设置的首选、此刻读请求会走的入口、是否回退中。不改变任何冷却状态。
    /// </summary>
    public ApiTransportStatus GetStatus(Uri configured)
    {
        lock (_gate)
        {
            var candidates = ApiEndpointAliases.GetCandidates(configured, _preferredMode);
            if (candidates.Count == 1)
            {
                return new ApiTransportStatus(_preferredMode, null, false);
            }

            var states = GetStates(configured);
            var now = _timeProvider.GetUtcNow();
            var active = candidates.FirstOrDefault(candidate => !GetState(states, candidate).IsCooling(now)) ?? candidates[0];
            return new ApiTransportStatus(
                _preferredMode,
                ApiEntryModes.ModeOfHost(active.Host),
                GetState(states, candidates[0]).IsCooling(now));
        }
    }

    private static int IndexOfHost(IReadOnlyList<Uri> candidates, Uri used)
    {
        for (var index = 0; index < candidates.Count; index++)
        {
            if (string.Equals(candidates[index].Host, used.Host, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    // 调用方必须已持有 _gate。
    private Dictionary<string, EntryState> GetStates(Uri configured)
    {
        // 按规范身份分组：同一台服务器的三个别名共用同一份状态，与 configured 具体写成哪个别名无关。
        var key = ApiEndpointAliases.Canonicalize(configured).AbsoluteUri;
        if (!_groups.TryGetValue(key, out var states))
        {
            states = new Dictionary<string, EntryState>(StringComparer.OrdinalIgnoreCase);
            _groups[key] = states;
        }

        return states;
    }

    private static EntryState GetState(Dictionary<string, EntryState> states, Uri entry)
    {
        if (!states.TryGetValue(entry.Host, out var state))
        {
            state = new EntryState();
            states[entry.Host] = state;
        }

        return state;
    }

    private sealed class EntryState
    {
        public int BackoffStep;
        public bool HasFailed;
        public DateTimeOffset CooldownUntil = DateTimeOffset.MinValue;

        /// <summary>从未失败（或失败后已确认恢复）：写请求可以放心使用。</summary>
        public bool IsClean => !HasFailed;

        public bool IsCooling(DateTimeOffset now) => now < CooldownUntil;

        public void Reset()
        {
            BackoffStep = 0;
            HasFailed = false;
            CooldownUntil = DateTimeOffset.MinValue;
        }
    }
}
