using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Tests;

/// <summary>
/// 设置页「改了直接保存 / 没保存成功离开要提示」：VM 与真实保存链路的集成（自动保存开启）。
/// </summary>
public sealed partial class SettingsViewModelTests
{
    // 防抖立即放行，测试不依赖真实时间；需要观察防抖的用例自己传入可控延时。
    private static readonly Func<TimeSpan, CancellationToken, Task> NoDelay = (_, _) => Task.CompletedTask;

    [Fact]
    public async Task Autosave_loading_settings_does_not_write_anything_or_mark_drafts()
    {
        var restart = new FakeRestartSettingsStore { Enabled = false };
        var methods = new FakePaymentMethodSettingsService { Current = new PaymentMethodSettings(true, true, false) };
        var receipt = new CountingReceiptPrinterSettingsStore();
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            receiptPrinterSettingsStore: receipt,
            paymentMethodSettingsService: methods,
            systemRestartSettingsStore: restart,
            autoSaveSettings: true,
            autoSaveDelay: NoDelay);

        await viewModel.LoadAsync();
        await Task.Delay(50);

        Assert.Equal(0, restart.SaveCalls);
        Assert.Equal(0, methods.SaveCalls);
        Assert.Equal(0, receipt.SaveCalls);
        Assert.False(viewModel.HasUnsavedSettings);
        Assert.False(viewModel.IsExplicitSaveVisible);
        Assert.Equal("Saved", viewModel.PageSaveStatus);
    }

    [Fact]
    public async Task Autosave_payment_method_toggle_is_saved_immediately_without_the_save_button()
    {
        var methods = new FakePaymentMethodSettingsService();
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            paymentMethodSettingsService: methods,
            autoSaveSettings: true,
            autoSaveDelay: NoDelay);
        await viewModel.LoadAsync();

        viewModel.VoucherEnabled = true;

        await WaitUntilAsync(() => methods.SaveCalls == 1 && viewModel.PaymentMethodsSaveState == "Saved");
        Assert.True(methods.Current.VoucherEnabled);
        Assert.False(viewModel.HasUnsavedSettings);
    }

    [Fact]
    public async Task Autosave_receipt_text_is_saved_once_after_typing_pauses_with_the_latest_value()
    {
        var receipt = new CountingReceiptPrinterSettingsStore();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            receiptPrinterSettingsStore: receipt,
            autoSaveSettings: true,
            autoSaveDelay: async (_, token) => await release.Task.WaitAsync(token));
        await viewModel.LoadAsync();

        viewModel.ReceiptStoreNameText = "Hot";
        viewModel.ReceiptStoreNameText = "Hot Bargain";

        Assert.Equal("Pending", viewModel.ReceiptStoreSaveState);
        Assert.Equal(0, receipt.SaveCalls);
        release.SetResult();

        await WaitUntilAsync(() => viewModel.ReceiptStoreSaveState == "Saved");
        Assert.Equal(1, receipt.SaveCalls);
        Assert.Equal("Hot Bargain", receipt.Settings.StoreName);
    }

    [Fact]
    public async Task Autosave_failed_save_asks_before_leaving_and_stay_keeps_the_page_open()
    {
        var restart = new ThrowingRestartSettingsStore();
        var returned = 0;
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            returnToPos: () => returned++,
            systemRestartSettingsStore: restart,
            autoSaveSettings: true,
            autoSaveDelay: NoDelay);
        await viewModel.LoadAsync();

        viewModel.IsRestartPromptEnabled = false;
        await WaitUntilAsync(() => viewModel.RestartPromptSaveState == "Failed");
        Assert.Equal(1, viewModel.DataMaintenanceUnsavedCount);
        Assert.True(viewModel.CanRetryPageSaves);

        var back = ((IAsyncRelayCommand)viewModel.BackCommand).ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.IsLeaveDialogOpen);
        var item = Assert.Single(viewModel.LeaveDialogItems);
        Assert.True(item.IsFailed);
        Assert.Contains("disk is locked", item.DetailText);
        Assert.True(viewModel.LeaveDialogCanRetry);

        viewModel.LeaveStayCommand.Execute(null);
        await back;

        Assert.Equal(0, returned);
        Assert.False(viewModel.IsLeaveDialogOpen);
        Assert.False(viewModel.IsRestartPromptEnabled);
    }

    [Fact]
    public async Task Autosave_discard_restores_stored_values_and_leaves()
    {
        var restart = new ThrowingRestartSettingsStore();
        var returned = 0;
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            returnToPos: () => returned++,
            systemRestartSettingsStore: restart,
            autoSaveSettings: true,
            autoSaveDelay: NoDelay);
        await viewModel.LoadAsync();
        viewModel.IsRestartPromptEnabled = false;
        await WaitUntilAsync(() => viewModel.RestartPromptSaveState == "Failed");

        var back = ((IAsyncRelayCommand)viewModel.BackCommand).ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.IsLeaveDialogOpen);
        viewModel.LeaveDiscardCommand.Execute(null);
        await back;

        Assert.Equal(1, returned);
        // 存储里仍是开启：界面回到当前生效的值，不再有未保存项。
        Assert.True(viewModel.IsRestartPromptEnabled);
        Assert.False(viewModel.HasUnsavedSettings);
    }

    [Fact]
    public async Task Autosave_retry_in_the_leave_prompt_saves_and_then_leaves()
    {
        var restart = new ThrowingRestartSettingsStore();
        var returned = 0;
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            returnToPos: () => returned++,
            systemRestartSettingsStore: restart,
            autoSaveSettings: true,
            autoSaveDelay: NoDelay);
        await viewModel.LoadAsync();
        viewModel.IsRestartPromptEnabled = false;
        await WaitUntilAsync(() => viewModel.RestartPromptSaveState == "Failed");

        var back = ((IAsyncRelayCommand)viewModel.BackCommand).ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.IsLeaveDialogOpen);
        restart.Fail = false;
        viewModel.LeaveRetryCommand.Execute(null);
        await back;

        Assert.Equal(1, returned);
        Assert.False(restart.Enabled);
        Assert.False(viewModel.HasUnsavedSettings);
    }

    [Fact]
    public async Task Autosave_back_without_changes_leaves_immediately()
    {
        var returned = 0;
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            returnToPos: () => returned++,
            autoSaveSettings: true,
            autoSaveDelay: NoDelay);
        await viewModel.LoadAsync();

        await ((IAsyncRelayCommand)viewModel.BackCommand).ExecuteAsync(null);

        Assert.Equal(1, returned);
        Assert.False(viewModel.IsLeaveDialogOpen);
    }

    [Fact]
    public async Task Autosave_linkly_address_edit_is_a_draft_until_tested_and_saved()
    {
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default with
        {
            Processor = CardProcessorKind.Linkly,
            LinklyHost = "192.168.1.10",
            LinklyPort = 2011
        })
        {
            LinklyTestResult = new LinklyConnectionTestResult(true, "connected")
        };
        using var viewModel = new SettingsViewModel(service, autoSaveSettings: true, autoSaveDelay: NoDelay);
        await viewModel.LoadAsync();
        Assert.Equal("Saved", viewModel.LinklySaveState);

        viewModel.LinklyHostText = "192.168.1.20";
        Assert.Equal("Draft", viewModel.LinklySaveState);
        Assert.True(viewModel.HasLinklyUnsaved);
        Assert.Equal("Saved", viewModel.SquareSaveState);

        // 改回原值：不算未保存。
        viewModel.LinklyHostText = "192.168.1.10";
        Assert.Equal("Saved", viewModel.LinklySaveState);

        viewModel.LinklyHostText = "192.168.1.20";
        await viewModel.TestLinklyCommand.ExecuteAsync(null);
        await viewModel.SaveLinklyCommand.ExecuteAsync(null);

        Assert.Equal("192.168.1.20", service.SavedConfiguration!.LinklyHost);
        Assert.Equal("Saved", viewModel.LinklySaveState);
        Assert.False(viewModel.HasUnsavedSettings);
    }

    [Fact]
    public async Task Autosave_leaving_with_only_drafts_hides_retry_and_discard_restores_the_saved_address()
    {
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default with
        {
            Processor = CardProcessorKind.Linkly,
            LinklyHost = "192.168.1.10",
            LinklyPort = 2011
        });
        var returned = 0;
        using var viewModel = new SettingsViewModel(
            service,
            returnToPos: () => returned++,
            autoSaveSettings: true,
            autoSaveDelay: NoDelay);
        await viewModel.LoadAsync();
        viewModel.LinklyHostText = "10.0.0.5";

        var back = ((IAsyncRelayCommand)viewModel.BackCommand).ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.IsLeaveDialogOpen);
        var item = Assert.Single(viewModel.LeaveDialogItems);
        Assert.False(item.IsFailed);
        Assert.False(viewModel.LeaveDialogCanRetry);

        viewModel.LeaveDiscardCommand.Execute(null);
        await back;

        Assert.Equal(1, returned);
        Assert.Equal("192.168.1.10", viewModel.LinklyHostText);
        Assert.Null(service.SavedConfiguration);
    }

    [Fact]
    public async Task Payment_category_opens_on_payment_methods_and_provider_tab_switches_follow()
    {
        using var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService());
        await viewModel.LoadAsync();

        viewModel.SelectedCategory = SettingsCategory.PaymentTerminal;
        Assert.True(viewModel.IsPaymentMethodsSubTabSelected);
        Assert.False(viewModel.IsProviderSubTabSelected);

        // 服务商 TabControl 被直接切换时，分页跟着走。
        viewModel.SelectedProviderTabIndex = 1;
        Assert.True(viewModel.IsLinklySubTabSelected);

        viewModel.IsSquareSubTabSelected = true;
        Assert.Equal(0, viewModel.SelectedProviderTabIndex);
        Assert.True(viewModel.IsProviderSubTabSelected);

        // 换个分类再回来，从第一个分页开始。
        viewModel.SelectedCategory = SettingsCategory.ReceiptPrinter;
        viewModel.IsReceiptTermsSubTabSelected = true;
        viewModel.SelectedCategory = SettingsCategory.PaymentTerminal;
        Assert.True(viewModel.IsPaymentMethodsSubTabSelected);
        viewModel.SelectedCategory = SettingsCategory.ReceiptPrinter;
        Assert.True(viewModel.IsReceiptHardwareSubTabSelected);
    }

    [Fact]
    public async Task Autosave_reentering_settings_leaves_protected_categories_so_they_are_authorized_again()
    {
        using var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService(), autoSaveSettings: true, autoSaveDelay: NoDelay);
        await viewModel.LoadAsync();
        await ((IAsyncRelayCommand)viewModel.SelectPaymentTerminalCommand).ExecuteAsync(null);
        Assert.Equal(SettingsCategory.PaymentTerminal, viewModel.SelectedCategory);

        // 设置页 VM 会被缓存，下次进入只调 LoadAsync：受权限保护的分类要重新点进去（重新授权）。
        await viewModel.LoadAsync();

        Assert.Equal(SettingsCategory.DataMaintenance, viewModel.SelectedCategory);
    }

    [Fact]
    public void Autosave_is_off_by_default_so_the_explicit_save_buttons_stay()
    {
        using var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService());

        Assert.False(viewModel.IsAutoSaveEnabled);
        Assert.True(viewModel.IsExplicitSaveVisible);
        Assert.False(viewModel.IsPageSaveStatusVisible);
    }

    /// <summary>可计数的小票设置存储（自动保存用例需要知道写了几次）。</summary>
    private sealed class CountingReceiptPrinterSettingsStore : IReceiptPrinterSettingsStore
    {
        public ReceiptPrinterSettings Settings { get; private set; } = ReceiptPrinterSettings.Default;

        public int SaveCalls { get; private set; }

        public Task<ReceiptPrinterSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Settings);

        public Task SaveAsync(ReceiptPrinterSettings settings, CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            Settings = settings;
            return Task.CompletedTask;
        }
    }

    /// <summary>写入会失败（可切换为成功）的重启提醒存储，存储里初始为开启。</summary>
    private sealed class ThrowingRestartSettingsStore : ISystemRestartSettingsStore
    {
        public bool Enabled { get; private set; } = true;

        public bool Fail { get; set; } = true;

        public Task<bool> IsPromptEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult(Enabled);

        public Task SetPromptEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                throw new IOException("disk is locked");
            }

            Enabled = enabled;
            return Task.CompletedTask;
        }

        public Task<DateOnly?> GetLastPromptBusinessDateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<DateOnly?>(null);

        public Task MarkPromptedAsync(DateOnly businessDate, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<DateTimeOffset?> GetScheduledRestartAtAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<DateTimeOffset?>(null);

        public Task SetScheduledRestartAtAsync(DateTimeOffset restartAt, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ClearScheduledRestartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
