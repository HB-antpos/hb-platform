using Hbpos.Client.Wpf.Services.Zeller;

namespace Hbpos.Client.Tests;

/// <summary>同一条 Zeller 尝试同一时刻只能由一方处理；释放后别人才能接手。</summary>
public sealed class ZellerAttemptActivityTests
{
    [Fact]
    public void An_attempt_can_only_be_entered_once_at_a_time()
    {
        var activity = new ZellerAttemptActivity();
        var attempt = Guid.NewGuid();

        using var first = activity.TryEnter(attempt);
        var second = activity.TryEnter(attempt);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.True(activity.IsActive(attempt));
        Assert.NotNull(activity.TryEnter(Guid.NewGuid()));
    }

    [Fact]
    public void Releasing_lets_someone_else_enter_and_releasing_twice_is_harmless()
    {
        var activity = new ZellerAttemptActivity();
        var attempt = Guid.NewGuid();
        var first = activity.TryEnter(attempt)!;
        first.Dispose();

        using var second = activity.TryEnter(attempt);
        first.Dispose();

        Assert.NotNull(second);
        Assert.True(activity.IsActive(attempt));
    }

    [Fact]
    public void Only_one_of_many_concurrent_callers_gets_in()
    {
        var activity = new ZellerAttemptActivity();
        var attempt = Guid.NewGuid();

        var leases = Enumerable.Range(0, 32).AsParallel().Select(_ => activity.TryEnter(attempt)).ToArray();

        Assert.Single(leases, lease => lease is not null);
    }
}
