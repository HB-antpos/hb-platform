using System.Collections.Concurrent;
using System.Net;

namespace BlazorApp.Api.Services;

/// <summary>
/// 登录失败限流：挡住对账号密码的在线猜测，尤其是邮件发出的 4 位随机临时密码（约 506 万种组合）。
/// 登录接口原先没有任何失败计数 / 锁定，只要知道登录名就能一直猜。
/// </summary>
public interface ILoginAttemptLimiter
{
    /// <summary>
    /// 登录前调用：账号或来源 IP 处于锁定期时返回还需等待的秒数（向上取整），否则返回 0。
    /// 锁定期内调用方必须直接拒绝、不要再验证密码——否则锁定形同虚设（攻击者继续猜，猜中就放行）。
    /// </summary>
    int GetLockoutSeconds(string accountKey, string? clientIp);

    /// <summary>只有「用户名或密码错误」这类凭据失败才记一次；账号未开通登录、临时密码过期等不算猜测。</summary>
    void RecordFailure(string accountKey, string? clientIp);

    /// <summary>账号登录成功：清掉该账号的失败计数。不动 IP 计数——门店共用同一个出口 IP，别人登录成功不代表有人没在猜。</summary>
    void RecordSuccess(string accountKey);
}

/// <summary>登录限流参数。默认值：同一账号 15 分钟内错 5 次锁 15 分钟；同一 IP 15 分钟内错 100 次锁 15 分钟。</summary>
public sealed class LoginAttemptLimiterOptions
{
    /// <summary>同一账号（登录名）在窗口内允许的失败次数，达到即锁定。</summary>
    public int MaxAccountFailures { get; init; } = 5;

    /// <summary>
    /// 同一来源 IP 在窗口内允许的失败次数，达到即锁定。刻意放得很宽：一个门店的多名员工共用一个出口 IP，
    /// 这里只防「一个 IP 对很多账号各试一次」的撒网式猜测，不能误伤正常的输错密码。
    /// </summary>
    public int MaxIpFailures { get; init; } = 100;

    /// <summary>失败计数的统计窗口：窗口内没攒够次数，过了窗口就从零开始。</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>触发限流后的锁定时长，到期自动解除。</summary>
    public TimeSpan Lockout { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// 进程内的登录失败计数。故意不落库：不需要新表和迁移（加表 / 加列一旦代码先于迁移上线，登录路径会直接报错），
/// 重启后计数清零也没关系——攻击者无法让服务重启。代价是多实例部署时各实例各计各的（上限按实例数放大）。
/// 账号按「登录名输入值（去空格、转小写）」计，同一个人用用户名和邮箱两种写法最多各一份额度（×2），
/// 对 4 位密码的枚举量级（每天几百次）没有实质影响。
/// </summary>
public sealed class LoginAttemptLimiter : ILoginAttemptLimiter
{
    private sealed class Counter
    {
        public int Failures;
        public DateTimeOffset WindowStart;
        public DateTimeOffset? LockedUntil;
    }

    /// <summary>每记录这么多次失败，顺带清一遍已过期的条目，防止攻击者用随机用户名撑大内存。</summary>
    private const int SweepEveryWrites = 512;

    private readonly ConcurrentDictionary<string, Counter> _counters = new(StringComparer.Ordinal);
    private readonly LoginAttemptLimiterOptions _options;
    private readonly TimeProvider _time;
    private int _writesSinceSweep;

    public LoginAttemptLimiter(LoginAttemptLimiterOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new LoginAttemptLimiterOptions();
        _time = timeProvider ?? TimeProvider.System;
    }

    public int GetLockoutSeconds(string accountKey, string? clientIp)
    {
        var now = _time.GetUtcNow();
        var wait = RemainingSeconds(AccountKey(accountKey), now);
        var ipKey = IpKey(clientIp);
        if (ipKey != null)
        {
            wait = Math.Max(wait, RemainingSeconds(ipKey, now));
        }
        return wait;
    }

    public void RecordFailure(string accountKey, string? clientIp)
    {
        var now = _time.GetUtcNow();
        Bump(AccountKey(accountKey), _options.MaxAccountFailures, now);
        var ipKey = IpKey(clientIp);
        if (ipKey != null)
        {
            Bump(ipKey, _options.MaxIpFailures, now);
        }

        if (Interlocked.Increment(ref _writesSinceSweep) >= SweepEveryWrites)
        {
            Interlocked.Exchange(ref _writesSinceSweep, 0);
            Sweep(now);
        }
    }

    public void RecordSuccess(string accountKey) => _counters.TryRemove(AccountKey(accountKey), out _);

    private int RemainingSeconds(string key, DateTimeOffset now)
    {
        if (!_counters.TryGetValue(key, out var counter))
        {
            return 0;
        }
        lock (counter)
        {
            return counter.LockedUntil is { } until && until > now
                ? (int)Math.Ceiling((until - now).TotalSeconds)
                : 0;
        }
    }

    private void Bump(string key, int maxFailures, DateTimeOffset now)
    {
        var counter = _counters.GetOrAdd(key, _ => new Counter { WindowStart = now });
        lock (counter)
        {
            // 锁定期内的尝试不再累计：调用方此时本就不会验证密码，累计只会把锁定无限延长。
            if (counter.LockedUntil is { } until && until > now)
            {
                return;
            }

            // 窗口已过（含锁定刚到期）：从零开始，新的一轮。
            if (now - counter.WindowStart >= _options.Window)
            {
                counter.Failures = 0;
                counter.WindowStart = now;
                counter.LockedUntil = null;
            }

            counter.Failures++;
            if (counter.Failures >= maxFailures)
            {
                counter.LockedUntil = now + _options.Lockout;
            }
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var pair in _counters)
        {
            bool stale;
            lock (pair.Value)
            {
                var locked = pair.Value.LockedUntil is { } until && until > now;
                stale = !locked && now - pair.Value.WindowStart >= _options.Window;
            }
            if (stale)
            {
                _counters.TryRemove(pair.Key, out _);
            }
        }
    }

    private static string AccountKey(string accountKey) => "a:" + (accountKey ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// 来源 IP 无法确定（unknown）或是回环地址（反向代理没传真实 IP 时的兜底）时不按 IP 计：
    /// 否则所有人共用同一个桶，一个人输错 100 次就把全公司锁在外面。
    /// </summary>
    private static string? IpKey(string? clientIp)
    {
        var ip = clientIp?.Trim();
        if (string.IsNullOrEmpty(ip) || string.Equals(ip, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (IPAddress.TryParse(ip, out var parsed) && IPAddress.IsLoopback(parsed))
        {
            return null;
        }
        return "ip:" + ip.ToLowerInvariant();
    }
}
