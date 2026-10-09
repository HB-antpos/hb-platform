using System.Collections.Concurrent;

namespace Hbpos.Api.Logging;

/// <summary>
/// 按固定键限频的日志闸门：同一个键在窗口内只放行一次，其余只累计被抑制的次数，放行时一并带出。
/// 用于匿名端点的鉴权失败告警：既要让中心日志（Warning+）看得到“回调 bearer 配错了”，
/// 又不能让互联网上的任意请求把日志文件和中心日志刷满。键必须来自有限集合（环境名、固定原因），不能带请求里的自由文本。
/// </summary>
public sealed class LogThrottle(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, State> states = new(StringComparer.Ordinal);

    public static LogThrottle Shared { get; } = new();

    /// <summary>窗口内第一次调用返回 true，并通过 <paramref name="suppressedSinceLast"/> 给出上个窗口被抑制的次数。</summary>
    public bool TryAcquire(string key, TimeSpan window, out int suppressedSinceLast)
    {
        var now = clock.GetUtcNow();
        var state = states.GetOrAdd(key, _ => new State());
        lock (state)
        {
            if (state.LastLoggedAt is { } last && now - last < window)
            {
                state.Suppressed++;
                suppressedSinceLast = 0;
                return false;
            }

            suppressedSinceLast = state.Suppressed;
            state.Suppressed = 0;
            state.LastLoggedAt = now;
            return true;
        }
    }

    /// <summary>仅供测试：清空所有键，避免静态共享实例让用例互相影响。</summary>
    internal void ResetForTests() => states.Clear();

    private sealed class State
    {
        public DateTimeOffset? LastLoggedAt { get; set; }

        public int Suppressed { get; set; }
    }
}
