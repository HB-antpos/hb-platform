using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.Services.Zeller;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Tests;

/// <summary>设置页的 Zeller：配对协调器、扫码暂停、设置页的页签与按钮。</summary>
public sealed class ZellerSettingsTests
{
    // ── 扫码暂停 ──

    [Fact]
    public void Suspension_is_reference_counted_and_reports_only_real_changes()
    {
        var suspension = new ScannerInputSuspension();
        var changes = 0;
        suspension.Changed += (_, _) => changes++;

        var first = suspension.Suspend("first");
        var second = suspension.Suspend("second");
        Assert.True(suspension.IsSuspended);
        Assert.Equal(1, changes);

        first.Dispose();
        Assert.True(suspension.IsSuspended);
        Assert.Equal(1, changes);

        second.Dispose();
        Assert.False(suspension.IsSuspended);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Disposing_a_suspension_twice_does_not_release_another_holder()
    {
        var suspension = new ScannerInputSuspension();
        var first = suspension.Suspend("first");
        using var second = suspension.Suspend("second");

        first.Dispose();
        first.Dispose();

        Assert.True(suspension.IsSuspended);
    }

    [Fact]
    public void A_failing_change_handler_does_not_break_the_suspension()
    {
        var suspension = new ScannerInputSuspension();
        suspension.Changed += (_, _) => throw new InvalidOperationException("handler");

        var lease = suspension.Suspend("test");
        Assert.True(suspension.IsSuspended);
        lease.Dispose();

        Assert.False(suspension.IsSuspended);
    }

    // ── 协调器 ──

    [Fact]
    public async Task Check_reports_paired_when_the_sdk_says_so()
    {
        var gateway = new ZellerPairingGateway { Initialise = new ZellerPairingResult(true) };
        var coordinator = Create(gateway);

        var check = await coordinator.CheckAsync();

        Assert.Equal(ZellerPairingState.Paired, check.State);
        Assert.True(check.IsPaired);
    }

    [Theory]
    [InlineData(ZellerErrorType.SetupRequired, ZellerPairingState.NotPaired)]
    [InlineData(ZellerErrorType.Cancelled, ZellerPairingState.Cancelled)]
    [InlineData(ZellerErrorType.NetworkFailure, ZellerPairingState.Failed)]
    [InlineData(ZellerErrorType.OperationFailure, ZellerPairingState.Failed)]
    [InlineData(ZellerErrorType.Unrecognised, ZellerPairingState.Failed)]
    public async Task Check_classifies_unpaired_results(ZellerErrorType errorType, ZellerPairingState expected)
    {
        var gateway = new ZellerPairingGateway { Initialise = new ZellerPairingResult(false, errorType, "detail") };
        var coordinator = Create(gateway);

        var check = await coordinator.CheckAsync();

        Assert.Equal(expected, check.State);
        Assert.False(check.IsPaired);
        Assert.Equal("detail", check.Detail);
    }

    [Fact]
    public async Task Pairing_suspends_the_scanner_for_exactly_the_duration_of_the_call()
    {
        var suspension = new ScannerInputSuspension();
        var duringCall = false;
        var gateway = new ZellerPairingGateway
        {
            OnSetup = () =>
            {
                duringCall = suspension.IsSuspended;
                return Task.FromResult(new ZellerPairingResult(true));
            }
        };
        var coordinator = Create(gateway, suspension: suspension);

        var check = await coordinator.PairAsync();

        Assert.True(duringCall);
        Assert.False(suspension.IsSuspended);
        Assert.True(check.IsPaired);
    }

    [Fact]
    public async Task Pairing_resumes_the_scanner_even_when_the_sdk_throws()
    {
        var suspension = new ScannerInputSuspension();
        var gateway = new ZellerPairingGateway { OnSetup = () => throw new InvalidOperationException("sdk") };
        var coordinator = Create(gateway, suspension: suspension);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PairAsync());

        Assert.False(suspension.IsSuspended);
    }

    [Fact]
    public async Task Checking_does_not_suspend_the_scanner()
    {
        var suspension = new ScannerInputSuspension();
        var duringCall = true;
        var gateway = new ZellerPairingGateway
        {
            OnInitialise = () =>
            {
                duringCall = suspension.IsSuspended;
                return Task.FromResult(new ZellerPairingResult(true));
            }
        };

        await Create(gateway, suspension: suspension).CheckAsync();

        Assert.False(duringCall);
    }

    [Fact]
    public async Task Enable_saves_zeller_and_keeps_every_other_setting()
    {
        var saved = new List<CardTerminalConfiguration>();
        var coordinator = Create(new ZellerPairingGateway(), saved);
        var current = CardTerminalConfiguration.Default with
        {
            Processor = CardProcessorKind.Square,
            Environment = CardTerminalEnvironment.Sandbox,
            SquareLocationId = "L1",
            SquareDeviceId = "D1",
            LinklyHost = "10.0.0.5",
            LinklyPort = 2012,
            TerminalTimeoutSeconds = 75
        };

        var result = await coordinator.EnableAsync(current, new ZellerPairingCheck(ZellerPairingState.Paired));

        Assert.Equal(CardProcessorKind.Zeller, result.Processor);
        Assert.Equal(current with { Processor = CardProcessorKind.Zeller }, result);
        Assert.Equal(result, Assert.Single(saved));
    }

    [Theory]
    [InlineData(ZellerPairingState.NotChecked)]
    [InlineData(ZellerPairingState.NotPaired)]
    [InlineData(ZellerPairingState.Cancelled)]
    [InlineData(ZellerPairingState.Failed)]
    public async Task Enable_is_refused_unless_the_terminal_is_paired(ZellerPairingState state)
    {
        var saved = new List<CardTerminalConfiguration>();
        var coordinator = Create(new ZellerPairingGateway(), saved);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.EnableAsync(CardTerminalConfiguration.Default, new ZellerPairingCheck(state)));

        Assert.Empty(saved);
    }

    private static ZellerSettingsCoordinator Create(
        ZellerPairingGateway gateway,
        List<CardTerminalConfiguration>? saved = null,
        IScannerInputSuspension? suspension = null) =>
        new(
            gateway,
            (configuration, cancellationToken) =>
            {
                saved?.Add(configuration);
                return Task.CompletedTask;
            },
            suspension);

    internal sealed class ZellerPairingGateway : IZellerTerminalGateway
    {
        public ZellerPairingResult Initialise { get; init; } = new(true);

        public Func<Task<ZellerPairingResult>>? OnInitialise { get; init; }

        public Func<Task<ZellerPairingResult>>? OnSetup { get; init; }

        public Func<CancellationToken, Task<ZellerPairingResult>>? OnSetupWithToken { get; init; }

        public Task<ZellerPairingResult> InitialiseAsync(CancellationToken cancellationToken = default) =>
            OnInitialise?.Invoke() ?? Task.FromResult(Initialise);

        public Task<ZellerPairingResult> SetupAsync(CancellationToken cancellationToken = default) =>
            OnSetupWithToken?.Invoke(cancellationToken) ?? OnSetup?.Invoke() ?? Task.FromResult(new ZellerPairingResult(true));

        public Task<ZellerCallResult> PurchaseAsync(ZellerPurchaseRequest request, IProgress<ZellerTerminalEvent>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ZellerCallResult> RefundAsync(ZellerRefundRequest request, IProgress<ZellerTerminalEvent>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ZellerQueryResult> GetTransactionsAsync(string reference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
