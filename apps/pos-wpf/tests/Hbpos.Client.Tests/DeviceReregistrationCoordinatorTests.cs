using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Tests;

// M15：设备重新注册前检查未结卡交易。未结卡交易按“门店 + 设备号 + 环境”存放，
// 重注册后设备号变化，旧记录会从所有恢复入口消失，而服务端旧会话还占着那台实体刷卡机。
public sealed class DeviceReregistrationCoordinatorTests
{
    [Fact]
    public async Task Reregistration_is_allowed_when_nothing_blocks_it()
    {
        var harness = new Harness(hasUnresolvedCardAttempts: _ => Task.FromResult(false));

        var blocked = await harness.Coordinator.CheckCanBeginAsync();

        Assert.Null(blocked);
        Assert.Equal(1, harness.CardCheckCount);
        Assert.Empty(harness.StatusMessages);
    }

    [Fact]
    public async Task Unresolved_card_attempts_block_reregistration_with_a_clear_message()
    {
        var harness = new Harness(hasUnresolvedCardAttempts: _ => Task.FromResult(true));

        var blocked = await harness.Coordinator.CheckCanBeginAsync();

        Assert.NotNull(blocked);
        Assert.False(blocked!.Started);
        Assert.Equal(
            "Resolve the unfinished card transactions in Card Recovery before changing store registration.",
            blocked.StatusMessage);
        Assert.Equal([blocked.StatusMessage], harness.StatusMessages);
    }

    [Fact]
    public async Task Unreadable_card_attempt_queue_fails_closed()
    {
        var harness = new Harness(
            hasUnresolvedCardAttempts: _ => throw new InvalidOperationException("sqlite busy"));

        var blocked = await harness.Coordinator.CheckCanBeginAsync();

        Assert.NotNull(blocked);
        Assert.False(blocked!.Started);
        Assert.Contains("Could not check", blocked.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Card_attempts_are_checked_after_the_cheaper_local_checks()
    {
        var harness = new Harness(
            hasUnresolvedCardAttempts: _ => Task.FromResult(true),
            previewMode: true);

        var blocked = await harness.Coordinator.CheckCanBeginAsync();

        Assert.Contains("preview", blocked!.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.CardCheckCount);
    }

    [Fact]
    public async Task Coordinator_without_a_card_check_keeps_the_previous_behavior()
    {
        var harness = new Harness(hasUnresolvedCardAttempts: null);

        Assert.Null(await harness.Coordinator.CheckCanBeginAsync());
    }

    [Theory]
    [InlineData("main.reregister.cardRecoveryPending")]
    [InlineData("main.reregister.cardRecoveryUnknown")]
    [InlineData("linkly.recovery.openAttemptBlocksPayment")]
    [InlineData("cardRecovery.auto.recoveredOrders")]
    public void Card_recovery_gate_messages_are_localized_in_both_languages(string key)
    {
        var localization = new LocalizationService();
        var english = localization.T(key);
        localization.SetCulture("zh-CN");
        var chinese = localization.T(key);

        Assert.NotEqual($"[[{key}]]", english);
        Assert.NotEqual($"[[{key}]]", chinese);
        Assert.NotEqual(english, chinese);
        Assert.Matches("[一-鿿]", chinese);
    }

    private sealed class Harness
    {
        public Harness(
            Func<CancellationToken, Task<bool>>? hasUnresolvedCardAttempts,
            bool previewMode = false)
        {
            Coordinator = new DeviceReregistrationCoordinator(
                new NoopStartupService(),
                new LocalizationService(),
                new EmptySyncCenter(),
                new PosCartService(),
                _ => { },
                StatusMessages.Add,
                () => previewMode,
                hasUnresolvedCardAttempts is null
                    ? null
                    : cancellationToken =>
                    {
                        CardCheckCount++;
                        return hasUnresolvedCardAttempts(cancellationToken);
                    });
        }

        public DeviceReregistrationCoordinator Coordinator { get; }

        public List<string> StatusMessages { get; } = [];

        public int CardCheckCount { get; private set; }
    }

    private sealed class EmptySyncCenter : IShellSyncCenterService
    {
        public Task<ShellSyncCenterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ShellSyncCenterSnapshot(new SyncQueueOverview(0, 0, 0, null), []));
    }

    private sealed class NoopStartupService : IMainShellStartupService
    {
        public Task<MainShellStartupResult> EvaluateAsync(
            PosSessionState session,
            bool previewMode,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void SetAuthorizedDevice(
            string deviceCode,
            string storeCode,
            string hardwareId,
            string authorizationCode) => throw new NotSupportedException();

        public void ClearAuthorization() => throw new NotSupportedException();
    }
}
