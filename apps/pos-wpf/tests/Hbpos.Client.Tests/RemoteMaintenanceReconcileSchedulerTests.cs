using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Cashiers;

namespace Hbpos.Client.Tests;

/// <summary>
/// 对账的触发时机：只在出现可用的普通收银员票据后才去问中心，紧急授权会话与过期票据绝不触发。
/// </summary>
[Collection(ShutdownTimingTestCollection.Name)]
public sealed class RemoteMaintenanceReconcileSchedulerTests
{
    private static CashierSessionDto Session(bool emergency = false, string? token = "ticket", DateTimeOffset? expires = null) =>
        new("C001", "user-guid", "Alice", "S001", "POS-01", [], [], [], false, false, emergency,
            token, expires ?? DateTimeOffset.UtcNow.AddHours(1));

    private static RemoteMaintenanceReconcileScheduler Scheduler(
        RecordingReconciler reconciler, ICashierSessionContext context, TimeSpan? retry = null) =>
        new(reconciler, context)
        {
            StartupDelay = TimeSpan.FromMilliseconds(5),
            PollInterval = TimeSpan.FromMilliseconds(5),
            RetryInterval = retry ?? TimeSpan.FromMinutes(30)
        };

    [Fact]
    public async Task Waits_for_a_cashier_login_before_asking_the_center()
    {
        var context = new FakeSessionContext();
        var reconciler = new RecordingReconciler(RemoteMaintenanceReconcileResult.UpToDate);
        var run = Scheduler(reconciler, context).RunAsync(CancellationToken.None);

        // 登录之前（停在登录界面）不发任何请求。
        await Task.Delay(100);
        Assert.Equal(0, reconciler.Calls);
        Assert.False(run.IsCompleted);

        context.SetCurrent(Session());
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, reconciler.Calls);
    }

    [Fact]
    public async Task Never_triggers_for_an_emergency_session_because_the_gateway_would_clear_it()
    {
        var context = new FakeSessionContext();
        context.SetCurrent(Session(emergency: true));
        var reconciler = new RecordingReconciler(RemoteMaintenanceReconcileResult.UpToDate);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scheduler(reconciler, context).RunAsync(cancel.Token));

        Assert.Equal(0, reconciler.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Never_triggers_without_a_ticket(string? token)
    {
        var context = new FakeSessionContext();
        context.SetCurrent(Session(token: token));
        var reconciler = new RecordingReconciler(RemoteMaintenanceReconcileResult.UpToDate);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scheduler(reconciler, context).RunAsync(cancel.Token));

        Assert.Equal(0, reconciler.Calls);
    }

    [Fact]
    public async Task Never_triggers_with_an_expired_ticket()
    {
        var context = new FakeSessionContext();
        context.SetCurrent(Session(expires: DateTimeOffset.UtcNow.AddMinutes(-1)));
        var reconciler = new RecordingReconciler(RemoteMaintenanceReconcileResult.UpToDate);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scheduler(reconciler, context).RunAsync(cancel.Token));

        Assert.Equal(0, reconciler.Calls);
    }

    [Theory]
    [InlineData(RemoteMaintenanceReconcileResult.UpToDate)]
    [InlineData(RemoteMaintenanceReconcileResult.Reconciled)]
    [InlineData(RemoteMaintenanceReconcileResult.NotApplicable)]
    [InlineData(RemoteMaintenanceReconcileResult.Failed)]
    [InlineData(RemoteMaintenanceReconcileResult.Rejected)]
    public async Task Stops_after_any_conclusive_result(RemoteMaintenanceReconcileResult result)
    {
        var context = new FakeSessionContext();
        context.SetCurrent(Session());
        var reconciler = new RecordingReconciler(result);

        await Scheduler(reconciler, context, retry: TimeSpan.FromMilliseconds(5)).RunAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        // 即使重试间隔很短，已有结论后也不会再来一次（避免每隔几分钟就弹 UAC）。
        await Task.Delay(100);
        Assert.Equal(1, reconciler.Calls);
    }

    [Fact]
    public async Task Retries_after_the_interval_while_the_result_is_deferred()
    {
        var context = new FakeSessionContext();
        context.SetCurrent(Session());
        var reconciler = new RecordingReconciler(RemoteMaintenanceReconcileResult.Deferred, RemoteMaintenanceReconcileResult.UpToDate);

        await Scheduler(reconciler, context, retry: TimeSpan.FromMilliseconds(30)).RunAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, reconciler.Calls);
    }

    [Fact]
    public async Task Does_not_hammer_the_center_before_the_retry_interval_elapses()
    {
        var context = new FakeSessionContext();
        context.SetCurrent(Session());
        var reconciler = new RecordingReconciler(RemoteMaintenanceReconcileResult.Deferred);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scheduler(reconciler, context).RunAsync(cancel.Token));

        Assert.Equal(1, reconciler.Calls);
    }

    // 只关心 CurrentSession；不用真实实现，避免把权限计算和本地设置仓库拖进触发时机的测试。
    private sealed class FakeSessionContext : ICashierSessionContext
    {
        public CashierSessionDto? CurrentSession { get; private set; }
        public void SetCurrent(CashierSessionDto session) => CurrentSession = session;
        public void Clear() => CurrentSession = null;
        public bool TrySetCurrent(CashierSessionDto expected, CashierSessionDto replacement) => throw new NotSupportedException();
        public bool TryClear(CashierSessionDto expected) => throw new NotSupportedException();
        public bool HasPermission(string permissionCode) => throw new NotSupportedException();
        public bool RequirePermission(string permissionCode, out string message) => throw new NotSupportedException();
    }

    private sealed class RecordingReconciler(params RemoteMaintenanceReconcileResult[] results) : IRemoteMaintenanceReconciler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<RemoteMaintenanceReconcileResult> ReconcileAsync(CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref _calls) - 1;
            return Task.FromResult(results[Math.Min(index, results.Length - 1)]);
        }
    }
}
