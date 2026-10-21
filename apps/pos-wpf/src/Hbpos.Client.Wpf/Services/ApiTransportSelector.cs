namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 选择实际发请求的传输地址（主备入口），并在连接失败时做带退避的故障转移。
/// 设计要点：
/// - 只对「连接阶段失败」（域名解析 / 建连 / TLS）做切换，请求没有送达服务器，换入口重试是安全的；
/// - 一旦首选入口连接失败，立刻改用备用入口，并在冷却期内一直用备用，避免每个请求都先撞一次失败；
/// - 冷却期结束后让下一个请求去试探首选入口，成功就恢复，失败则冷却时间翻倍（上限 30 分钟）；
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
    private readonly Dictionary<string, GroupState> _groups = new(StringComparer.Ordinal);

    public ApiTransportSelector(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// 返回现在应该使用的传输地址。configured 是当前配置的首选地址。
    /// allowProbe：冷却结束后是否允许这次请求去「试探」首选入口。读请求（GET/HEAD）失败了会当场换入口重试，
    /// 可以试探；写请求（POST/PUT 等）失败了不会重放，不能拿来探路——否则恰好是一次收款 / 同步去撞一个仍然
    /// 坏掉的首选入口。所以写请求传 false，继续走已确认可用的入口，等读请求确认首选入口恢复后再切回。
    /// </summary>
    public Uri Resolve(Uri configured, bool allowProbe = true)
    {
        var candidates = ApiEndpointAliases.GetCandidates(configured);
        if (candidates.Count == 1)
        {
            return configured;
        }

        lock (_gate)
        {
            var state = GetState(configured);
            if (allowProbe && state.ActiveIndex != 0 && _timeProvider.GetUtcNow() >= state.StickyUntil)
            {
                // 冷却结束：回到首选入口试探一次。是否真的恢复由 ReportSuccess / ReportConnectFailure 决定。
                state.ActiveIndex = 0;
            }

            return candidates[Math.Min(state.ActiveIndex, candidates.Count - 1)];
        }
    }

    /// <summary>取一个与 used 不同的备用传输地址；没有备用时返回 null。</summary>
    public Uri? GetFallback(Uri configured, Uri used)
    {
        foreach (var candidate in ApiEndpointAliases.GetCandidates(configured))
        {
            if (!string.Equals(candidate.Host, used.Host, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>上报一次连接阶段失败。仅当失败的是首选入口时才切到备用入口。</summary>
    public void ReportConnectFailure(Uri configured, Uri used)
    {
        var candidates = ApiEndpointAliases.GetCandidates(configured);
        if (candidates.Count == 1 || !IsPrimary(candidates, used))
        {
            return;
        }

        lock (_gate)
        {
            var state = GetState(configured);
            var now = _timeProvider.GetUtcNow();
            if (state.ActiveIndex != 0 && now < state.StickyUntil)
            {
                // 已经处于冷却：这是更早派发出去的并发请求迟到的失败。不再升级退避——否则一次故障会被
                // N 个并发请求一次顶满到 30 分钟；调用方仍会让读请求换备用入口重试。
                return;
            }

            state.ActiveIndex = 1;
            state.StickyUntil = now + BackoffSteps[Math.Min(state.BackoffStep, BackoffSteps.Length - 1)];
            state.BackoffStep = Math.Min(state.BackoffStep + 1, BackoffSteps.Length - 1);
        }
    }

    /// <summary>上报一次成功。首选入口成功说明线路已恢复，清零退避。</summary>
    public void ReportSuccess(Uri configured, Uri used)
    {
        var candidates = ApiEndpointAliases.GetCandidates(configured);
        if (candidates.Count == 1 || !IsPrimary(candidates, used))
        {
            return;
        }

        lock (_gate)
        {
            var state = GetState(configured);
            state.ActiveIndex = 0;
            state.BackoffStep = 0;
        }
    }

    /// <summary>当前是否处于「已切到备用入口」状态（供日志/测试使用）。</summary>
    public bool IsUsingFallback(Uri configured)
    {
        if (ApiEndpointAliases.GetCandidates(configured).Count == 1)
        {
            return false;
        }

        lock (_gate)
        {
            var state = GetState(configured);
            return state.ActiveIndex != 0 && _timeProvider.GetUtcNow() < state.StickyUntil;
        }
    }

    private static bool IsPrimary(IReadOnlyList<Uri> candidates, Uri used)
    {
        return string.Equals(candidates[0].Host, used.Host, StringComparison.OrdinalIgnoreCase);
    }

    private GroupState GetState(Uri configured)
    {
        var key = configured.AbsoluteUri;
        if (!_groups.TryGetValue(key, out var state))
        {
            state = new GroupState();
            _groups[key] = state;
        }

        return state;
    }

    private sealed class GroupState
    {
        public int ActiveIndex;
        public int BackoffStep;
        public DateTimeOffset StickyUntil;
    }
}
