using BlazorApp.Shared.Constants;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Cashiers;

namespace Hbpos.Client.Tests;

/// <summary>设置页「业务开关」分类：总部下发开关的锁定 / 离线解锁 / 切换回滚，以及旧页面同名开关的锁定与重读。</summary>
public sealed partial class SettingsViewModelTests
{
    private static readonly Func<TimeSpan, CancellationToken, Task> PosSettingsNoDelay = (_, _) => Task.CompletedTask;

    [Fact]
    public async Task Pos_settings_category_is_absent_and_old_switches_stay_unlocked_without_the_service()
    {
        using var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService());
        await viewModel.LoadAsync();

        // 没有总部设置服务（测试 / 预览）：分类不出现，各旧开关永远是未锁定的本机行为。
        Assert.False(viewModel.IsPosSettingsAvailable);
        Assert.All(
            new[]
            {
                viewModel.ManualCardSettingRow, viewModel.VoucherSettingRow, viewModel.OrderListSettingRow,
                viewModel.BankReceiptSettingRow, viewModel.NoSaleRescanSettingRow, viewModel.ReturnsRescanSettingRow,
                viewModel.AutoRestartSettingRow, viewModel.ZellerUnmatchedRefundSettingRow
            },
            row =>
            {
                Assert.True(row.IsUnlocked);
                Assert.False(row.HasStatus);
            });
    }

    [Fact]
    public async Task Locked_items_are_greyed_out_with_the_source_label_and_old_controls_follow()
    {
        var service = new FakePosSettingsService
        {
            Current = PosView(
                online: true,
                (PosSettingKeys.CashMovementEnabled, true, PosSettingScopes.Store, true),
                (PosSettingKeys.NoSaleRescan, false, PosSettingScopes.Global, true),
                (PosSettingKeys.Voucher, true, PosSettingScopes.Device, true),
                (PosSettingKeys.OrderList, true, PosSettingSources.Default, true))
        };
        using var viewModel = CreatePosSettingsViewModel(service);

        await viewModel.LoadAsync();

        Assert.True(viewModel.IsPosSettingsAvailable);
        Assert.Equal(9, viewModel.PosSettingRows.Count);
        Assert.Equal(PosSettingCatalog.All.Select(item => item.Key), viewModel.PosSettingRows.Select(row => row.Key).Where(PosSettingCatalog.IsKnownKey));

        var cash = viewModel.PosSettingRows.Single(row => row.Key == PosSettingKeys.CashMovementEnabled);
        Assert.True(cash.IsLocked);
        Assert.False(cash.IsUnlocked);
        Assert.True(cash.IsOn);
        Assert.Equal(PosSettingStatusKind.Locked, cash.StatusKind);
        Assert.Equal("Head office · Store", cash.StatusText);
        Assert.Equal("Head office · Global", viewModel.NoSaleRescanSettingRow.StatusText);
        Assert.Equal("Head office · Device", viewModel.VoucherSettingRow.StatusText);
        Assert.Equal("Head office default", viewModel.OrderListSettingRow.StatusText);

        // 旧页面里同名开关的锁定属性。
        Assert.False(viewModel.NoSaleRescanSettingRow.IsUnlocked);
        Assert.False(viewModel.VoucherSettingRow.IsUnlocked);
        Assert.False(viewModel.OrderListSettingRow.IsUnlocked);

        // 没下发的项：不显示标签，开关正常可用。
        Assert.True(viewModel.ManualCardSettingRow.IsUnlocked);
        Assert.False(viewModel.ManualCardSettingRow.HasStatus);
        Assert.False(viewModel.IsPosSettingsOfflineBannerVisible);
    }

    [Fact]
    public async Task Offline_unlocks_managed_items_and_marks_temporary_changes()
    {
        var service = new FakePosSettingsService
        {
            Current = PosView(
                online: false,
                (PosSettingKeys.ReturnsRescan, true, PosSettingScopes.Global, false),
                (PosSettingKeys.BankReceipt, false, PosSettingScopes.Global, false, true))
        };
        using var viewModel = CreatePosSettingsViewModel(service);

        await viewModel.LoadAsync();

        Assert.True(viewModel.IsPosSettingsOfflineBannerVisible);
        Assert.Equal("Editable offline", viewModel.ReturnsRescanSettingRow.StatusText);
        Assert.Equal(PosSettingStatusKind.OfflineEditable, viewModel.ReturnsRescanSettingRow.StatusKind);
        Assert.True(viewModel.ReturnsRescanSettingRow.IsUnlocked);
        Assert.Equal(PosSettingStatusKind.OfflineOverride, viewModel.BankReceiptSettingRow.StatusKind);
        Assert.Equal("Changed offline", viewModel.BankReceiptSettingRow.StatusText);
        Assert.True(viewModel.BankReceiptSettingRow.IsUnlocked);
        // 没下发的项离线时也不显示标签。
        Assert.False(viewModel.ManualCardSettingRow.HasStatus);
    }

    [Fact]
    public async Task Last_synced_text_shows_local_time_or_not_synced_yet()
    {
        var service = new FakePosSettingsService { Current = PosView(online: true) };
        using var viewModel = CreatePosSettingsViewModel(service);
        await viewModel.LoadAsync();
        Assert.Equal("Head office settings · not synced yet", viewModel.PosSettingsLastSyncedText);

        var syncedAt = new DateTimeOffset(2026, 11, 14, 5, 42, 0, TimeSpan.Zero);
        service.Current = new PosSettingsView(service.Current.Items, true, syncedAt);
        service.RaiseChanged();

        await WaitUntilAsync(() => viewModel.PosSettingsLastSyncedText.Contains(syncedAt.ToLocalTime().ToString("HH:mm"), StringComparison.Ordinal));
        Assert.StartsWith("Head office settings · last synced ", viewModel.PosSettingsLastSyncedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Toggling_a_row_calls_TrySetAsync_with_the_cashier_session_as_actor()
    {
        var cashier = new Wpf.Services.CashierSessionContext();
        var session = new CashierSessionDto("C01", "U01", "Cashier", "S01", "POS-01", [], [], ["S01"], false, false, false);
        cashier.SetCurrent(session);
        var service = new FakePosSettingsService { Current = PosView(online: true) };
        using var viewModel = CreatePosSettingsViewModel(service, cashier);
        await viewModel.LoadAsync();

        var row = viewModel.PosSettingRows.Single(item => item.Key == PosSettingKeys.ManualCard);
        row.IsOn = true;
        await viewModel.PosSettingToggleCompletion;

        var call = Assert.Single(service.SetCalls);
        Assert.Equal((PosSettingKeys.ManualCard, true), (call.Key, call.Value));
        Assert.Same(session, call.Actor);
        Assert.True(row.IsOn);
    }

    [Fact]
    public async Task Toggling_a_locked_row_is_rolled_back_with_a_message()
    {
        var service = new FakePosSettingsService
        {
            Current = PosView(online: true, (PosSettingKeys.Voucher, false, PosSettingScopes.Global, true)),
            NextOutcome = PosSettingChangeOutcome.Locked
        };
        using var viewModel = CreatePosSettingsViewModel(service);
        await viewModel.LoadAsync();

        var row = viewModel.PosSettingRows.Single(item => item.Key == PosSettingKeys.Voucher);
        row.IsOn = true;
        await viewModel.PosSettingToggleCompletion;

        Assert.False(row.IsOn);
        Assert.Equal("This setting is controlled by head office and cannot be changed here.", viewModel.StatusMessage);
        Assert.Null(service.SetCalls.Single().Actor);
    }

    [Fact]
    public async Task Toggling_a_row_that_fails_to_save_is_rolled_back_with_a_message()
    {
        var service = new FakePosSettingsService { Current = PosView(online: true), NextOutcome = PosSettingChangeOutcome.Failed };
        using var viewModel = CreatePosSettingsViewModel(service);
        await viewModel.LoadAsync();

        var row = viewModel.PosSettingRows.Single(item => item.Key == PosSettingKeys.OrderList);
        row.IsOn = true;
        await viewModel.PosSettingToggleCompletion;

        Assert.False(row.IsOn);
        Assert.Equal("Could not save the setting. It was not changed.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Sync_now_calls_the_service_and_reports_the_result_in_the_status_bar()
    {
        var service = new FakePosSettingsService
        {
            Current = PosView(online: true),
            SyncResult = new PosSettingsSyncResult(PosSettingsSyncOutcome.Applied)
        };
        using var viewModel = CreatePosSettingsViewModel(service);
        await viewModel.LoadAsync();

        await viewModel.SyncPosSettingsNowCommand.ExecuteAsync(null);

        Assert.Equal(1, service.SyncCalls);
        Assert.Equal("Head office settings updated.", viewModel.StatusMessage);
        Assert.False(viewModel.IsPosSettingsSyncing);
        Assert.True(viewModel.SyncPosSettingsNowCommand.CanExecute(null));
    }

    [Fact]
    public async Task Entering_the_settings_page_refreshes_the_service_and_the_category_can_be_selected()
    {
        var service = new FakePosSettingsService { Current = PosView(online: true) };
        using var viewModel = CreatePosSettingsViewModel(service);
        await viewModel.LoadAsync();
        var refreshesAfterLoad = service.RefreshCalls;
        Assert.True(refreshesAfterLoad >= 1);

        await viewModel.SelectPosSettingsCommand.ExecuteAsync(null);

        Assert.Equal(SettingsCategory.PosSettings, viewModel.SelectedCategory);
        Assert.True(viewModel.IsPosSettingsSelected);
        Assert.Equal("Business switches", viewModel.PageTitleText);
        Assert.Equal(refreshesAfterLoad + 1, service.RefreshCalls);
    }

    [Fact]
    public async Task Changed_event_rereads_the_old_stores_without_triggering_autosave()
    {
        var methods = new FakePaymentMethodSettingsService { Current = new PaymentMethodSettings(false, false, true) };
        var restart = new FakeRestartSettingsStore { Enabled = true };
        var receipt = new CountingReceiptPrinterSettingsStore();
        var setup = new FakeCardTerminalSetupService();
        var service = new FakePosSettingsService { Current = PosView(online: true) };
        using var viewModel = new SettingsViewModel(
            setup,
            receiptPrinterSettingsStore: receipt,
            paymentMethodSettingsService: methods,
            systemRestartSettingsStore: restart,
            autoSaveSettings: true,
            autoSaveDelay: PosSettingsNoDelay,
            posSettingsService: service);
        await viewModel.LoadAsync();
        Assert.False(viewModel.UseManualCard);

        // 模拟同步把总部值写穿到各存储。
        methods.Current = new PaymentMethodSettings(true, true, false);
        restart.Enabled = false;
        await receipt.SaveAsync(receipt.Settings with
        {
            PrintBankReceiptText = true,
            NoSaleCashDrawerRequiresRescan = true,
            ReturnsRequireRescan = true
        });
        await setup.SaveZellerOptionsAsync(true);
        var savesBefore = (methods.SaveCalls, restart.SaveCalls, receipt.SaveCalls);
        service.RaiseChanged();

        await WaitUntilAsync(() =>
            viewModel.UseManualCard && viewModel.VoucherEnabled && !viewModel.ShowOrderList &&
            !viewModel.IsRestartPromptEnabled && viewModel.ReceiptPrintBankReceiptText &&
            viewModel.ReceiptNoSaleCashDrawerRequiresRescan && viewModel.ReceiptReturnsRequireRescan &&
            viewModel.ZellerAllowUnmatchedRefund);

        // 回显存储内容不是用户修改：不能触发任何写回。
        await Task.Delay(50);
        Assert.Equal(savesBefore, (methods.SaveCalls, restart.SaveCalls, receipt.SaveCalls));
        Assert.False(viewModel.HasUnsavedSettings);
    }

    [Fact]
    public async Task Autosave_of_an_unlocked_field_does_not_overwrite_locked_fields_with_stale_values()
    {
        var methods = new FakePaymentMethodSettingsService { Current = new PaymentMethodSettings(false, false, true) };
        var service = new FakePosSettingsService { Current = PosView(online: true) };
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            paymentMethodSettingsService: methods,
            autoSaveSettings: true,
            autoSaveDelay: PosSettingsNoDelay,
            posSettingsService: service);
        await viewModel.LoadAsync();

        // 总部随后锁定「手动刷卡 = 开」：页面上的 UseManualCard 还是旧的 false。
        service.Current = PosView(online: true, (PosSettingKeys.ManualCard, true, PosSettingScopes.Global, true));
        viewModel.VoucherEnabled = true;

        await WaitUntilAsync(() => methods.SaveCalls == 1);
        // 整份写回时，锁定项取服务里的总部值，不被旧草稿覆盖。
        Assert.True(methods.Current.UseManualCard);
        Assert.True(methods.Current.VoucherEnabled);
    }

    [Fact]
    public async Task A_locked_field_change_does_not_trigger_autosave()
    {
        var methods = new FakePaymentMethodSettingsService();
        var service = new FakePosSettingsService
        {
            Current = PosView(online: true, (PosSettingKeys.Voucher, false, PosSettingScopes.Global, true))
        };
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            paymentMethodSettingsService: methods,
            autoSaveSettings: true,
            autoSaveDelay: PosSettingsNoDelay,
            posSettingsService: service);
        await viewModel.LoadAsync();

        viewModel.VoucherEnabled = true;
        await Task.Delay(50);

        Assert.Equal(0, methods.SaveCalls);
        Assert.False(viewModel.HasUnsavedSettings);
    }

    [Fact]
    public async Task Offline_unlocked_old_control_still_saves_the_local_value()
    {
        var methods = new FakePaymentMethodSettingsService();
        var service = new FakePosSettingsService
        {
            Current = PosView(online: false, (PosSettingKeys.Voucher, false, PosSettingScopes.Global, false))
        };
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            paymentMethodSettingsService: methods,
            autoSaveSettings: true,
            autoSaveDelay: PosSettingsNoDelay,
            posSettingsService: service);
        await viewModel.LoadAsync();

        viewModel.VoucherEnabled = true;

        await WaitUntilAsync(() => methods.SaveCalls == 1);
        Assert.True(methods.Current.VoucherEnabled);
    }

    [Fact]
    public async Task Disposing_the_view_model_unsubscribes_from_the_service()
    {
        var methods = new FakePaymentMethodSettingsService { Current = new PaymentMethodSettings(false, false, true) };
        var service = new FakePosSettingsService { Current = PosView(online: true) };
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            paymentMethodSettingsService: methods,
            posSettingsService: service);
        await viewModel.LoadAsync();
        Assert.Equal(1, service.ChangedSubscribers);

        viewModel.Dispose();

        Assert.Equal(0, service.ChangedSubscribers);
    }

    private static SettingsViewModel CreatePosSettingsViewModel(
        FakePosSettingsService service,
        ICashierSessionContext? cashierSessionContext = null) =>
        new(
            new FakeCardTerminalSetupService(),
            localization: new LocalizationService(),
            cashierSessionContext: cashierSessionContext,
            posSettingsService: service);

    /// <summary>按目录顺序造出 9 项状态；没列出的项视为总部未下发。</summary>
    private static PosSettingsView PosView(bool online, params ManagedItem[] managed)
    {
        var items = PosSettingCatalog.All
            .Where(definition => PosSettingCatalog.IsSupportedBy(definition.Key, PosSettingClientKinds.Wpf))
            .Select(definition => managed.FirstOrDefault(item => item.Key == definition.Key) is { Key: not null } match
                ? new PosSettingState(definition.Key, match.Value, match.Source, true, match.Locked, match.IsOverride)
                : new PosSettingState(definition.Key, false, null, false, false, false))
            .ToList();
        return new PosSettingsView(items, online, null);
    }

    /// <summary>总部已下发的一项（测试数据）；支持 4 元组（锁定与否）与 5 元组（含「离线临时修改」）写法。</summary>
    private readonly record struct ManagedItem(string Key, bool Value, string? Source, bool Locked, bool IsOverride = false)
    {
        public static implicit operator ManagedItem((string Key, bool Value, string? Source, bool Locked) item) =>
            new(item.Key, item.Value, item.Source, item.Locked);

        public static implicit operator ManagedItem((string Key, bool Value, string? Source, bool Locked, bool IsOverride) item) =>
            new(item.Key, item.Value, item.Source, item.Locked, item.IsOverride);
    }

    private sealed class FakePosSettingsService : IPosSettingsService
    {
        private EventHandler? _changed;

        public event EventHandler? Changed
        {
            add
            {
                _changed += value;
                ChangedSubscribers++;
            }
            remove
            {
                _changed -= value;
                ChangedSubscribers--;
            }
        }

        public int ChangedSubscribers { get; private set; }

        public PosSettingsView Current { get; set; } = PosSettingsView.Empty;

        public bool IsCashMovementEnabled => Current.Find(PosSettingKeys.CashMovementEnabled)?.Value == true;

        public int RefreshCalls { get; private set; }

        public int SyncCalls { get; private set; }

        public PosSettingChangeOutcome NextOutcome { get; set; } = PosSettingChangeOutcome.Applied;

        public PosSettingsSyncResult SyncResult { get; set; } = new(PosSettingsSyncOutcome.UpToDate);

        public List<(string Key, bool Value, CashierSessionDto? Actor)> SetCalls { get; } = [];

        public void RaiseChanged() => _changed?.Invoke(this, EventArgs.Empty);

        public Task<PosSettingsView> RefreshAsync(CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            return Task.FromResult(Current);
        }

        public Task<PosSettingChangeOutcome> TrySetAsync(
            string key,
            bool value,
            CashierSessionDto? actor,
            CancellationToken cancellationToken = default)
        {
            SetCalls.Add((key, value, actor));
            return Task.FromResult(NextOutcome);
        }

        public Task<PosSettingsSyncResult> SyncNowAsync(CancellationToken cancellationToken = default)
        {
            SyncCalls++;
            return Task.FromResult(SyncResult);
        }

        public Task<PosSettingsSyncResult?> RunScheduledTickAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<PosSettingsSyncResult?>(null);

        public void ReportConnectivity(bool isOnline)
        {
        }
    }
}
