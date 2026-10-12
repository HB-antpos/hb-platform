using System.Collections.Concurrent;

namespace BlazorApp.FaceIngest;

/// <summary>
/// 按 key 节流日志：同一 key 在一个窗口期（默认 60 秒）内只放行第一条，其余计入「被抑制次数」，
/// 下一次放行时把这段时间被抑制的次数一并带出。用于匿名端点被扫描、错 token 摄像机断网续传反复重推、
/// 磁盘满持续丢弃等场景，避免刷爆容器日志。
/// </summary>
internal sealed class ThrottledLogGate
{
    /// <summary>key 数量超过上限后不再新建条目，统一计入这个溢出 key，防止被大量伪造来源撑爆内存。</summary>
    internal const string OverflowKey = "*overflow*";

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _window;
    private readonly int _maxKeys;

    public ThrottledLogGate(TimeProvider timeProvider, TimeSpan window, int maxKeys = 4096)
    {
        _timeProvider = timeProvider;
        _window = window;
        _maxKeys = maxKeys;
    }

    /// <summary>
    /// 是否放行这条日志。放行时 <paramref name="suppressedSinceLastLog"/> 为上次放行以来被抑制的条数。
    /// </summary>
    public bool TryAcquire(string key, out long suppressedSinceLastLog)
    {
        var now = _timeProvider.GetTimestamp();
        if (!_entries.ContainsKey(key) && _entries.Count >= _maxKeys)
        {
            PruneExpired(now);
            if (_entries.Count >= _maxKeys)
            {
                key = OverflowKey;
            }
        }

        var entry = _entries.GetOrAdd(key, static _ => new Entry());
        lock (entry)
        {
            if (!entry.HasLogged || _timeProvider.GetElapsedTime(entry.WindowStartTimestamp, now) >= _window)
            {
                suppressedSinceLastLog = entry.Suppressed;
                entry.Suppressed = 0;
                entry.WindowStartTimestamp = now;
                entry.HasLogged = true;
                return true;
            }

            entry.Suppressed++;
            suppressedSinceLastLog = 0;
            return false;
        }
    }

    private void PruneExpired(long now)
    {
        foreach (var pair in _entries)
        {
            bool expired;
            lock (pair.Value)
            {
                expired = _timeProvider.GetElapsedTime(pair.Value.WindowStartTimestamp, now) >= _window;
            }

            if (expired && pair.Key != OverflowKey)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed class Entry
    {
        public bool HasLogged;
        public long WindowStartTimestamp;
        public long Suppressed;
    }
}
