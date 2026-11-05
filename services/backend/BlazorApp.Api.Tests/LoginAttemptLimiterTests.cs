using BlazorApp.Api.Services;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 登录失败限流：账号 15 分钟内错 5 次锁 15 分钟；同一 IP 15 分钟内错 100 次锁 15 分钟。
/// 用手动推进的时钟，不依赖真实等待。
/// </summary>
public sealed class LoginAttemptLimiterTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private const string PublicIp = "203.0.113.7";

    private static (LoginAttemptLimiter Limiter, ManualTimeProvider Clock) Create()
    {
        var clock = new ManualTimeProvider();
        return (new LoginAttemptLimiter(timeProvider: clock), clock);
    }

    private static void Fail(LoginAttemptLimiter limiter, string account, int times, string? ip = null)
    {
        for (var i = 0; i < times; i++)
        {
            limiter.RecordFailure(account, ip);
        }
    }

    [Fact]
    public void AccountIsLockedOnTheFifthFailure_NotBefore()
    {
        var (limiter, _) = Create();

        Fail(limiter, "alice", 4);
        Assert.Equal(0, limiter.GetLockoutSeconds("alice", null));

        limiter.RecordFailure("alice", null);
        Assert.Equal(15 * 60, limiter.GetLockoutSeconds("alice", null));
    }

    [Fact]
    public void LockoutExpiresAfterFifteenMinutes_AndCountingStartsOver()
    {
        var (limiter, clock) = Create();
        Fail(limiter, "alice", 5);

        clock.Advance(TimeSpan.FromMinutes(14).Add(TimeSpan.FromSeconds(59)));
        Assert.True(limiter.GetLockoutSeconds("alice", null) > 0);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, limiter.GetLockoutSeconds("alice", null));

        // 解锁后重新从零计：再错 4 次仍不锁，第 5 次才锁。
        Fail(limiter, "alice", 4);
        Assert.Equal(0, limiter.GetLockoutSeconds("alice", null));
        limiter.RecordFailure("alice", null);
        Assert.True(limiter.GetLockoutSeconds("alice", null) > 0);
    }

    [Fact]
    public void FailuresDuringLockoutDoNotExtendIt()
    {
        var (limiter, clock) = Create();
        Fail(limiter, "alice", 5);

        clock.Advance(TimeSpan.FromMinutes(10));
        Fail(limiter, "alice", 20);

        // 仍按最初的锁定时刻到期：锁定期内的尝试不累计，否则一直试就永远解不开。
        Assert.Equal(5 * 60, limiter.GetLockoutSeconds("alice", null));
    }

    [Fact]
    public void FailuresOutsideTheWindowDoNotAccumulate()
    {
        var (limiter, clock) = Create();
        Fail(limiter, "alice", 4);

        clock.Advance(TimeSpan.FromMinutes(16));
        limiter.RecordFailure("alice", null);

        Assert.Equal(0, limiter.GetLockoutSeconds("alice", null));
    }

    [Fact]
    public void SuccessfulLoginClearsTheAccountCounter()
    {
        var (limiter, _) = Create();
        Fail(limiter, "alice", 4);

        limiter.RecordSuccess("alice");
        Fail(limiter, "alice", 4);

        Assert.Equal(0, limiter.GetLockoutSeconds("alice", null));
    }

    [Fact]
    public void AccountKeyIgnoresCaseAndSurroundingSpaces()
    {
        var (limiter, _) = Create();

        limiter.RecordFailure("Alice", null);
        limiter.RecordFailure("  ALICE ", null);
        limiter.RecordFailure("alice", null);
        limiter.RecordFailure("aLiCe", null);
        limiter.RecordFailure(" alice", null);

        Assert.True(limiter.GetLockoutSeconds("alice", null) > 0);
    }

    [Fact]
    public void LockingOneAccountDoesNotAffectAnother()
    {
        var (limiter, _) = Create();

        Fail(limiter, "victim", 5);

        Assert.True(limiter.GetLockoutSeconds("victim", null) > 0);
        Assert.Equal(0, limiter.GetLockoutSeconds("bystander", null));
    }

    [Fact]
    public void UnknownUsernamesAreThrottledLikeRealOnes()
    {
        // 不存在的登录名与真实账号同样会被锁，限流不会泄露账号是否存在。
        var (limiter, _) = Create();

        Fail(limiter, "no-such-user", 5);

        Assert.True(limiter.GetLockoutSeconds("no-such-user", null) > 0);
    }

    [Fact]
    public void IpIsLockedAfterHundredFailuresAcrossManyAccounts()
    {
        var (limiter, _) = Create();

        // 一个 IP 对很多账号各试一次（撒网式猜测）：每个账号都没到 5 次，但这个 IP 累计 100 次即锁。
        for (var i = 0; i < 99; i++)
        {
            limiter.RecordFailure($"user{i}", PublicIp);
        }
        Assert.Equal(0, limiter.GetLockoutSeconds("fresh-account", PublicIp));

        limiter.RecordFailure("user99", PublicIp);

        Assert.True(limiter.GetLockoutSeconds("fresh-account", PublicIp) > 0);
        // 别的 IP 不受影响。
        Assert.Equal(0, limiter.GetLockoutSeconds("fresh-account", "198.51.100.1"));
    }

    [Fact]
    public void ModerateMistypingFromOneStoreIpDoesNotLockTheStore()
    {
        // 一个门店 20 名员工共用出口 IP，每人输错两次（共 40 次）：不能把整个门店锁在外面。
        var (limiter, _) = Create();

        for (var staff = 0; staff < 20; staff++)
        {
            Fail(limiter, $"staff{staff}", 2, PublicIp);
        }

        Assert.Equal(0, limiter.GetLockoutSeconds("staff0", PublicIp));
        Assert.Equal(0, limiter.GetLockoutSeconds("someone-else", PublicIp));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void IpIsNotTrackedWhenItCannotIdentifyTheClient(string ip)
    {
        // 反向代理没传真实 IP 时来源会是 unknown / 回环地址：若也按 IP 计，所有人共用一个桶，
        // 一个人输错 100 次就把全公司锁在外面。
        var (limiter, _) = Create();

        for (var i = 0; i < 300; i++)
        {
            limiter.RecordFailure($"user{i}", ip);
        }

        Assert.Equal(0, limiter.GetLockoutSeconds("fresh-account", ip));
    }

    [Fact]
    public void RemainingSecondsReportTheLongerOfAccountAndIpLockouts()
    {
        var (limiter, clock) = Create();
        Fail(limiter, "alice", 5, PublicIp);

        clock.Advance(TimeSpan.FromMinutes(5));

        // 账号还剩 10 分钟；IP 没到 100 次不锁，取账号的剩余时间。
        Assert.Equal(10 * 60, limiter.GetLockoutSeconds("alice", PublicIp));
    }

    [Fact]
    public void CustomOptionsAreHonored()
    {
        var clock = new ManualTimeProvider();
        var limiter = new LoginAttemptLimiter(
            new LoginAttemptLimiterOptions { MaxAccountFailures = 2, Lockout = TimeSpan.FromMinutes(1) },
            clock
        );

        Fail(limiter, "alice", 2);

        Assert.Equal(60, limiter.GetLockoutSeconds("alice", null));
    }
}
