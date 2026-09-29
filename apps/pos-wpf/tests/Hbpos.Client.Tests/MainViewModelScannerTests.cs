using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Globalization;
using System.Reflection;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Constants;
using Hbpos.Client.Wpf;
using Hbpos.Client.Wpf.Converters;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.Services.Facades;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Cashiers;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using Microsoft.Extensions.DependencyInjection;
using InstallmentPaymentDto = Hbpos.Contracts.Installments.InstallmentPaymentDto;
using InstallmentPickupInfoDto = Hbpos.Contracts.Installments.InstallmentPickupInfoDto;

namespace Hbpos.Client.Tests;

[Collection(ProductThumbnailImageSourceConverterTestCollection.Name)]
public sealed class MainViewModelScannerTests
{
    private const string DeviceActivationCode = "HBDEV1-0123456789ABCDEFGHJKMNPQRS-6789ABCDEFGHJKMNPQRSTVWXYZ";

    [Fact]
    public async Task Operation_authorization_prompt_takes_scanner_page_and_routes_both_scan_sources()
    {
        var scanner = new FakeRawScannerService();
        var authorization = new FakeOperationAuthorizationService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            rawScannerService: scanner,
            operationAuthorizationService: authorization);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        authorization.Open();

        Assert.Equal(authorization.ScannerPageId, scanner.ActivePageId);
        var rawActivationCode = DeviceActivationCode.Insert(2, " \t");
        scanner.Emit(rawActivationCode);
        var keyboardActivationCode = DeviceActivationCode.ToLowerInvariant().Insert(4, "\r\n");
        Assert.True(viewModel.TryProcessKeyboardScannerInput(keyboardActivationCode));
        Assert.Equal([rawActivationCode, keyboardActivationCode], authorization.Barcodes);

        authorization.Close();

        Assert.Equal(PosTerminalViewModel.PageId, scanner.ActivePageId);
    }

    [Fact]
    public async Task Ordinary_pages_globally_consume_activation_prefix_from_keyboard_and_raw_before_page_handler()
    {
        var scanner = new FakeRawScannerService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            rawScannerService: scanner);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var target = new RecordingScannerTarget();
        scanner.Subscribe(target.ScannerPageId, args =>
            target.ProcessScannerBarcode(args.Barcode, args.DevicePath, "raw"));
        viewModel.CurrentScreen = target;

        Assert.True(viewModel.TryProcessKeyboardScannerInput("hBdEv1-keyboard-product-like"));
        scanner.Emit("HBDEV1-raw-product-like");

        Assert.Empty(target.Barcodes);
        Assert.Equal(["HBDEV1-raw-product-like"], scanner.InterceptedBarcodes);

        Assert.True(viewModel.TryProcessKeyboardScannerInput("NORMAL-KEYBOARD"));
        scanner.Emit("NORMAL-RAW");

        Assert.Equal(["NORMAL-KEYBOARD", "NORMAL-RAW"], target.Barcodes);
    }

    [Fact]
    public async Task Ordinary_page_keyboard_consumes_activation_prefix_after_removing_ascii_whitespace()
    {
        var scanner = new FakeRawScannerService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            rawScannerService: scanner);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var target = new RecordingScannerTarget();
        viewModel.CurrentScreen = target;

        Assert.True(viewModel.TryProcessKeyboardScannerInput(
            "H B\tD E\nV 1 -keyboard-product-like"));

        Assert.Empty(target.Barcodes);
    }

    [Fact]
    public async Task Ordinary_page_raw_consumes_activation_prefix_after_removing_ascii_whitespace()
    {
        var scanner = new FakeRawScannerService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            rawScannerService: scanner);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var target = new RecordingScannerTarget();
        scanner.Subscribe(target.ScannerPageId, args =>
            target.ProcessScannerBarcode(args.Barcode, args.DevicePath, "raw"));
        viewModel.CurrentScreen = target;
        const string spacedActivationBarcode = "h\tb d\re\vv 1\f-raw-product-like";

        scanner.Emit(spacedActivationBarcode);

        Assert.Empty(target.Barcodes);
        Assert.Equal([spacedActivationBarcode], scanner.InterceptedBarcodes);
    }

    [Fact]
    public async Task Navigation_cancels_pending_operation_authorization_and_restores_new_screen_scanner_page()
    {
        var scanner = new FakeRawScannerService();
        var authorization = new FakeOperationAuthorizationService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            rawScannerService: scanner,
            operationAuthorizationService: authorization);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        authorization.Open();

        viewModel.ShowReturnsCommand.Execute(null);

        Assert.Equal(1, authorization.CancelCount);
        Assert.Equal(ReceiptReturnsViewModel.PageId, scanner.ActivePageId);
    }

    [Fact]
    public async Task Api_server_switch_freezes_scanner_and_common_navigation()
    {
        var scanner = new FakeRawScannerService();
        var authorization = new FakeOperationAuthorizationService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            rawScannerService: scanner,
            operationAuthorizationService: authorization);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var originalScreen = viewModel.CurrentScreen;

        viewModel.IsApiServerSwitching = true;
        viewModel.ShowReturnsCommand.Execute(null);
        var handled = viewModel.TryProcessKeyboardScannerInput("SWITCH-SCAN");

        Assert.True(handled);
        Assert.Same(originalScreen, viewModel.CurrentScreen);
        Assert.Empty(authorization.Barcodes);
        Assert.Null(scanner.ActivePageId);

        viewModel.IsApiServerSwitching = false;

        Assert.Equal(PosTerminalViewModel.PageId, scanner.ActivePageId);
    }

    [Fact]
    public async Task Server_switch_reinitialize_waits_for_post_show_startup_continuation()
    {
        var recoveryCompletion = new TaskCompletionSource<IReadOnlyList<CardRecoveryQueueItem>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var recovery = new FakeCardPaymentRecoveryService
        {
            ListOpenHandler = (_, _) => recoveryCompletion.Task
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cardPaymentRecoveryService: recovery,
            mainShellStartupService: new SwitchReadyMainShellStartupService());

        var reinitialize = viewModel.ReinitializeAfterServerSwitchAsync(CancellationToken.None);
        await WaitUntilAsync(() => recovery.ListOpenCallCount > 0);

        Assert.False(reinitialize.IsCompleted);
        Assert.Equal(0, recovery.CallCount);

        recoveryCompletion.SetResult([]);
        await reinitialize.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
    }

    [Fact]
    public async Task Server_switch_reinitialize_contains_card_recovery_queue_scan_failure()
    {
        var expected = new InvalidOperationException("post-show startup failed");
        var recovery = new FakeCardPaymentRecoveryService
        {
            ListOpenHandler = (_, _) => Task.FromException<IReadOnlyList<CardRecoveryQueueItem>>(expected)
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cardPaymentRecoveryService: recovery,
            mainShellStartupService: new SwitchReadyMainShellStartupService());

        await viewModel.ReinitializeAfterServerSwitchAsync(CancellationToken.None);

        Assert.Equal(1, recovery.ListOpenCallCount);
        Assert.Equal(0, recovery.CallCount);
        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
    }

    [Fact]
    public async Task Server_switch_reinitialize_never_clears_activation_recovery()
    {
        var activationWorkflow = new PendingActivationRecoveryWorkflow(
            new HttpRequestException("Activation result is uncertain"));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            mainShellStartupService: new SwitchReadyMainShellStartupService(),
            deviceRegistrationWorkflowService: activationWorkflow);

        await viewModel.ReinitializeAfterServerSwitchAsync(CancellationToken.None);

        Assert.Equal(0, activationWorkflow.ClearCallCount);
    }

    [Fact]
    public void Expired_emergency_session_is_cleared_by_clock_guard()
    {
        var context = new CashierSessionContext();
        context.SetCurrent(CashierSessionContext.CreateEmergencyOverride(
            "1042",
            "POS-01",
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-07-14T03:00:00Z"),
            "token"));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cashierSessionContext: context);

        viewModel.ExpireEmergencySessionIfNeeded(DateTimeOffset.Parse("2026-07-14T03:00:01Z"));

        Assert.Null(context.CurrentSession);
        Assert.Null(viewModel.Session.CashierSession);
        Assert.Equal(string.Empty, viewModel.Session.CashierId);
        Assert.Equal("紧急登录已到期，请重新登录", viewModel.StatusMessage);
    }

    [Fact]
    public void Active_page_title_defaults_to_pos_before_startup_navigation()
    {
        var viewModel = CreateAuthorizedMainViewModel(new FakeCustomerDisplayWindowService());

        Assert.Null(viewModel.CurrentScreen);
        Assert.Null(viewModel.InstallmentCenter);
        Assert.Equal("POS", viewModel.ActivePageTitleText);
    }

    [Fact]
    public async Task Active_page_title_shows_device_registration_for_unregistered_device()
    {
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            mainShellStartupService: new MainShellStartupService(
                new FakeLocalDeviceRepository(),
                new FakeDeviceFingerprintService(),
                new DeviceAuthorizationState()),
            deviceRegistrationWorkflowService: new DeviceRegistrationWorkflowService(
                new FakeDeviceApiClient(),
                new FakeLocalDeviceRepository(),
                new FakeDeviceFingerprintService()));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.NotNull(viewModel.DeviceRegistration);
        Assert.Same(viewModel.DeviceRegistration, viewModel.CurrentScreen);
        Assert.Equal("Device Registration", viewModel.ActivePageTitleText);
    }

    [Fact]
    public async Task Active_page_title_tracks_navigation_and_culture()
    {
        var viewModel = CreateAuthorizedMainViewModel(new FakeCustomerDisplayWindowService());

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.Equal("POS", viewModel.ActivePageTitleText);

        viewModel.ShowReturnsCommand.Execute(null);
        Assert.Equal("Returns", viewModel.ActivePageTitleText);

        await viewModel.ShowHistoryCommand.ExecuteAsync(null);
        Assert.Equal("History", viewModel.ActivePageTitleText);

        await viewModel.ToggleCultureCommand.ExecuteAsync(null);

        Assert.Equal("\u5386\u53F2", viewModel.ActivePageTitleText);
    }

    [Fact]
    public async Task Active_page_title_shows_card_recovery_center_instead_of_loading()
    {
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cardPaymentRecoveryService: new FakeCardPaymentRecoveryService { OpenItems = [] },
            cashierSessionContext: new CashierSessionContext(),
            cashierLoginService: new FakeCashierLoginService(
                CreateCashierSession(Permissions.PosTerminal.Payment.View)),
            operationAuthorizationService: new GrantingOperationAuthorizationService());
        var startupOptions = new AppStartupOptions([], false, null, null);
        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);
        viewModel.CashierBarcodeInput = "CARD-RECOVERY-TITLE-CASHIER";
        await viewModel.LoginCashierCommand.ExecuteAsync(null);
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        await viewModel.PosTerminal!.OpenCardRecoveryCenterCommand.ExecuteAsync(null);

        var center = Assert.IsType<CardRecoveryCenterViewModel>(viewModel.CurrentScreen);
        Assert.Contains(nameof(MainViewModel.ActivePageTitleText), changedProperties);
        Assert.Equal("Card transaction recovery", viewModel.ActivePageTitleText);

        await viewModel.ToggleCultureCommand.ExecuteAsync(null);

        Assert.Equal("\u5361\u4EA4\u6613\u5F02\u5E38\u4E2D\u5FC3", viewModel.ActivePageTitleText);

        center.BackCommand.Execute(null);

        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.Equal("\u6536\u94F6\u4E3B\u9875", viewModel.ActivePageTitleText);
    }

    [Fact]
    public async Task Language_save_failure_keeps_runtime_culture_and_reports_restart_warning()
    {
        var settings = new FakeSettingsRepository { SetException = new InvalidOperationException("settings unavailable") };
        var viewModel = CreateAuthorizedMainViewModelWithSettings(settingsRepository: settings);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.ToggleCultureCommand.ExecuteAsync(null);

        Assert.Equal("zh-CN", viewModel.SelectedCultureName);
        Assert.Contains("重启后可能恢复", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Language_save_requests_are_ordered_and_latest_culture_wins()
    {
        var firstWriteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = new FakeSettingsRepository
        {
            FirstSetStarted = firstWriteStarted,
            ReleaseFirstSet = releaseFirstWrite
        };
        var viewModel = CreateAuthorizedMainViewModelWithSettings(settingsRepository: settings);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        var command = viewModel.ToggleCultureCommand;
        Assert.True(command.CanExecute(null));
        command.Execute(null);
        var firstToggle = command.ExecutionTask;
        Assert.NotNull(firstToggle);
        await firstWriteStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        Assert.True(command.CanExecute(null));
        command.Execute(null);
        var secondToggle = command.ExecutionTask;
        Assert.NotNull(secondToggle);
        Assert.NotSame(firstToggle, secondToggle);
        releaseFirstWrite.TrySetResult();
        await Task.WhenAll(firstToggle!, secondToggle!);

        Assert.Equal(["zh-CN", "en-US"], settings.SetValues);
        Assert.Equal("en-US", viewModel.SelectedCultureName);
        Assert.DoesNotContain("重启后可能恢复", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Older_language_save_failure_does_not_override_latest_success()
    {
        var firstWriteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = new FakeSettingsRepository
        {
            FirstSetStarted = firstWriteStarted,
            ReleaseFirstSet = releaseFirstWrite,
            FirstSetException = new InvalidOperationException("old zh write failed")
        };
        var viewModel = CreateAuthorizedMainViewModelWithSettings(settingsRepository: settings);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        var command = viewModel.ToggleCultureCommand;
        Assert.True(command.CanExecute(null));
        command.Execute(null);
        var firstToggle = command.ExecutionTask;
        Assert.NotNull(firstToggle);
        await firstWriteStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        Assert.True(command.CanExecute(null));
        command.Execute(null);
        var secondToggle = command.ExecutionTask;
        Assert.NotNull(secondToggle);
        Assert.NotSame(firstToggle, secondToggle);
        releaseFirstWrite.TrySetResult();
        await Task.WhenAll(firstToggle!, secondToggle!);

        Assert.Equal(["zh-CN", "en-US"], settings.SetValues);
        Assert.Equal("en-US", settings.LastPersistedValue);
        Assert.Equal("en-US", viewModel.SelectedCultureName);
        Assert.Equal("en-US", CultureInfo.CurrentCulture.Name);
        Assert.DoesNotContain("重启后可能恢复", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Cashier_login_overlay_stays_open_until_cashier_session_exists()
    {
        var cashierSession = CreateCashierSession(Permissions.PosTerminal.Sales.AddItem);
        var runtimeStatus = new RecordingRuntimeStatusApiClient();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cashierLoginService: new FakeCashierLoginService(cashierSession),
            runtimeStatusApiClient: runtimeStatus);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.True(viewModel.IsCashierLoginOverlayOpen);

        viewModel.CashierBarcodeInput = "BAR-1";
        await viewModel.LoginCashierCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsCashierLoginOverlayOpen);
        Assert.Same(cashierSession, viewModel.Session.CashierSession);
        var report = Assert.Single(runtimeStatus.Reports);
        Assert.False(report.IsOnline);
        Assert.Equal("CASHIER-1", report.CashierId);
        Assert.Equal("Alice", report.CashierName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cashier_refresh_rejection_clears_shell_session_and_opens_login_overlay(
        bool cacheRemovalFails)
    {
        var authorization = new FakeOperationAuthorizationService();
        var runtimeStatus = new RecordingRuntimeStatusApiClient();
        var auditLogger = new RecordingOperationAuditLogger();
        ICashierSessionCacheUpdater cacheUpdater = cacheRemovalFails
            ? new ThrowingCashierSessionCacheUpdater(new InvalidOperationException("cache unavailable"))
            : new NoopCashierSessionCacheUpdater();
        var services = new ServiceCollection();
        services.AddHbposClientServices(new AppStartupOptions([], true, null, null));
        services.AddSingleton<ICashierSessionRefreshApiClient>(
            new FixedCashierSessionRefreshApiClient(CashierSessionRefreshAttempt.OnlineRejected()));
        services.AddSingleton(cacheUpdater);
        services.AddSingleton<IOperationAuthorizationService>(authorization);
        services.AddSingleton<IPosRuntimeStatusApiClient>(runtimeStatus);
        services.AddSingleton<IOperationAuditLogger>(auditLogger);
        using var provider = services.BuildServiceProvider();
        var context = provider.GetRequiredService<ICashierSessionContext>();
        var viewModel = provider.GetRequiredService<MainViewModel>();
        var refreshService = provider.GetRequiredService<CashierSessionRefreshService>();
        var cart = provider.GetRequiredService<IPosCoreServices>().Cart;
        var deviceAuthorization = provider.GetRequiredService<DeviceAuthorizationState>();
        var cashierSession = CreateCashierSession(Permissions.PosTerminal.Sales.AddItem);
        var authorizedDevice = new DeviceAuthorizationContext("POS-01", "1042", "HW-01", "AUTH-01");
        var currentScreen = new object();
        context.SetCurrent(cashierSession);
        deviceAuthorization.Set(authorizedDevice);
        viewModel.Session = CashierSessionContext.ApplyToSession(viewModel.Session, cashierSession);
        viewModel.CurrentScreen = currentScreen;
        viewModel.CashierBarcodeInput = "SHOULD-BE-CLEARED";
        cart.AddItem(CreateItem("1042", "SKU-SESSION", "930SESSION"));
        authorization.Open();

        if (cacheRemovalFails)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => refreshService.RefreshOnceAsync());
        }
        else
        {
            await refreshService.RefreshOnceAsync();
        }
        await WaitUntilAsync(() => viewModel.Session.CashierSession is null);

        Assert.Null(context.CurrentSession);
        Assert.Null(viewModel.Session.CashierSession);
        Assert.Empty(viewModel.Session.CashierId);
        Assert.Empty(viewModel.Session.CashierName);
        Assert.Empty(viewModel.CashierBarcodeInput);
        Assert.True(viewModel.IsCashierLoginOverlayOpen);
        Assert.False(authorization.IsPromptOpen);
        Assert.Equal("Cashier session expired. Sign in again.", viewModel.StatusMessage);
        Assert.Single(cart.Lines);
        Assert.Same(currentScreen, viewModel.CurrentScreen);
        Assert.Same(authorizedDevice, deviceAuthorization.Current);
        Assert.Equal("1042", viewModel.Session.StoreCode);
        Assert.Equal("POS-01", viewModel.Session.DeviceCode);
        var logout = Assert.Single(auditLogger.Events.Where(auditEvent =>
            auditEvent.OperationType == OperationAuditTypes.CashierLogout));
        Assert.Equal("SESSION_REJECTED", logout.ReasonCode);
        var report = Assert.Single(runtimeStatus.Reports);
        Assert.Null(report.CashierId);
        Assert.Null(report.CashierName);
    }

    [Fact]
    public async Task Cashier_refresh_api_unavailable_keeps_shell_and_authorization_sessions()
    {
        var authorization = new FakeOperationAuthorizationService();
        var runtimeStatus = new RecordingRuntimeStatusApiClient();
        var auditLogger = new RecordingOperationAuditLogger();
        var services = new ServiceCollection();
        services.AddHbposClientServices(new AppStartupOptions([], true, null, null));
        services.AddSingleton<ICashierSessionRefreshApiClient>(
            new FixedCashierSessionRefreshApiClient(CashierSessionRefreshAttempt.ApiUnavailable()));
        services.AddSingleton<ICashierSessionCacheUpdater, NoopCashierSessionCacheUpdater>();
        services.AddSingleton<IOperationAuthorizationService>(authorization);
        services.AddSingleton<IPosRuntimeStatusApiClient>(runtimeStatus);
        services.AddSingleton<IOperationAuditLogger>(auditLogger);
        using var provider = services.BuildServiceProvider();
        var context = provider.GetRequiredService<ICashierSessionContext>();
        var viewModel = provider.GetRequiredService<MainViewModel>();
        var refreshService = provider.GetRequiredService<CashierSessionRefreshService>();
        var cashierSession = CreateCashierSession(Permissions.PosTerminal.Sales.AddItem);
        context.SetCurrent(cashierSession);
        viewModel.Session = CashierSessionContext.ApplyToSession(viewModel.Session, cashierSession);
        viewModel.CurrentScreen = new object();
        authorization.Open();

        await refreshService.RefreshOnceAsync();

        Assert.Same(cashierSession, context.CurrentSession);
        Assert.Same(cashierSession, viewModel.Session.CashierSession);
        Assert.False(viewModel.IsCashierLoginOverlayOpen);
        Assert.True(authorization.IsPromptOpen);
        Assert.Empty(runtimeStatus.Reports);
        Assert.Empty(auditLogger.Events);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Queued_cashier_rejection_does_not_clear_a_new_login(bool sameIdentity)
    {
        var auditLogger = new RecordingOperationAuditLogger();
        var services = new ServiceCollection();
        services.AddHbposClientServices(new AppStartupOptions([], true, null, null));
        services.AddSingleton<ICashierSessionRefreshApiClient>(
            new FixedCashierSessionRefreshApiClient(CashierSessionRefreshAttempt.OnlineRejected()));
        services.AddSingleton<ICashierSessionCacheUpdater, NoopCashierSessionCacheUpdater>();
        services.AddSingleton<IOperationAuditLogger>(auditLogger);
        using var provider = services.BuildServiceProvider();
        var context = provider.GetRequiredService<ICashierSessionContext>();
        var refreshService = provider.GetRequiredService<CashierSessionRefreshService>();
        var original = CreateCashierSession(Permissions.PosTerminal.Sales.AddItem) with
        {
            AuthorizationToken = "ticket-a"
        };
        var newer = sameIdentity
            ? original with
            {
                PermissionCodes = [Permissions.PosTerminal.Settings.View],
                AuthorizationToken = "ticket-b"
            }
            : original with
            {
                CashierId = "CASHIER-2",
                UserGuid = "user-2",
                CashierName = "Bob",
                AuthorizationToken = "ticket-b"
            };
        MainViewModel? viewModel = null;
        refreshService.SessionRejected += (_, _) =>
        {
            context.SetCurrent(newer);
            viewModel!.Session = CashierSessionContext.ApplyToSession(viewModel.Session, newer);
        };
        viewModel = provider.GetRequiredService<MainViewModel>();
        context.SetCurrent(original);
        viewModel.Session = CashierSessionContext.ApplyToSession(viewModel.Session, original);
        viewModel.CurrentScreen = new object();

        await refreshService.RefreshOnceAsync();

        Assert.Same(newer, context.CurrentSession);
        Assert.Same(newer, viewModel.Session.CashierSession);
        Assert.False(viewModel.IsCashierLoginOverlayOpen);
        Assert.Empty(auditLogger.Events.Where(auditEvent =>
            auditEvent.OperationType == OperationAuditTypes.CashierLogout));
    }

    [Fact]
    public async Task Disposed_shell_unsubscribes_from_cashier_rejection_notifications()
    {
        var auditLogger = new RecordingOperationAuditLogger();
        var services = new ServiceCollection();
        services.AddHbposClientServices(new AppStartupOptions([], true, null, null));
        services.AddSingleton<ICashierSessionRefreshApiClient>(
            new FixedCashierSessionRefreshApiClient(CashierSessionRefreshAttempt.OnlineRejected()));
        services.AddSingleton<ICashierSessionCacheUpdater, NoopCashierSessionCacheUpdater>();
        services.AddSingleton<IOperationAuditLogger>(auditLogger);
        using var provider = services.BuildServiceProvider();
        var context = provider.GetRequiredService<ICashierSessionContext>();
        var viewModel = provider.GetRequiredService<MainViewModel>();
        var refreshService = provider.GetRequiredService<CashierSessionRefreshService>();
        var cashierSession = CreateCashierSession(Permissions.PosTerminal.Sales.AddItem);
        context.SetCurrent(cashierSession);
        viewModel.Session = CashierSessionContext.ApplyToSession(viewModel.Session, cashierSession);
        viewModel.Dispose();

        await refreshService.RefreshOnceAsync();

        Assert.Null(context.CurrentSession);
        Assert.Same(cashierSession, viewModel.Session.CashierSession);
        Assert.Empty(auditLogger.Events.Where(auditEvent =>
            auditEvent.OperationType == OperationAuditTypes.CashierLogout));
    }

    [Fact]
    public async Task Cashier_login_passes_current_offline_state_to_login_service()
    {
        var login = new RecordingAttemptCashierLoginService(
            CashierLoginResult.Success(CreateCashierSession(Permissions.PosTerminal.Sales.AddItem)));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cashierLoginService: login);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        await viewModel.LoginCashierByBarcodeAsync("BAR-1");

        Assert.False(login.AttemptOnline);
    }

    [Fact]
    public async Task Cashier_login_closes_overlay_before_runtime_status_report_completes()
    {
        var runtimeStatus = new DeferredRuntimeStatusApiClient();
        var cashierSession = CreateCashierSession(Permissions.PosTerminal.Sales.AddItem);
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cashierLoginService: new FakeCashierLoginService(cashierSession),
            runtimeStatusApiClient: runtimeStatus);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        var loginTask = viewModel.LoginCashierByBarcodeAsync("BAR-1");
        await runtimeStatus.Started.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.Same(cashierSession, viewModel.Session.CashierSession);
        Assert.False(viewModel.IsCashierLoginOverlayOpen);
        Assert.False(loginTask.IsCompleted);

        runtimeStatus.Complete();
        await loginTask;
    }

    [Fact]
    public async Task Manual_lock_clears_cashier_reports_status_and_preserves_cart_for_relogin()
    {
        var cart = new PosCartService();
        var cashierContext = new CashierSessionContext();
        var cashierSession = CreateCashierSession(Permissions.PosTerminal.Sales.AddItem);
        var runtimeStatus = new RecordingRuntimeStatusApiClient();
        var auditLogger = new RecordingOperationAuditLogger();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cart: cart,
            cashierSessionContext: cashierContext,
            cashierLoginService: new FakeCashierLoginService(cashierSession),
            runtimeStatusApiClient: runtimeStatus,
            operationAuditLogger: auditLogger);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.LoginCashierByBarcodeAsync("BAR-1");
        cart.AddItem(CreateItem("1042", "SKU-LOCK", "930LOCK"));
        viewModel.CashierBarcodeInput = "SHOULD-BE-CLEARED";

        await viewModel.PosTerminal!.LockCashierCommand.ExecuteAsync(null);

        Assert.Null(cashierContext.CurrentSession);
        Assert.Null(viewModel.Session.CashierSession);
        Assert.Empty(viewModel.Session.CashierId);
        Assert.Empty(viewModel.Session.CashierName);
        Assert.Empty(viewModel.CashierBarcodeInput);
        Assert.True(viewModel.IsCashierLoginOverlayOpen);
        Assert.Equal("Signed out. Sign in again.", viewModel.StatusMessage);
        Assert.Single(cart.Lines);

        var logout = Assert.Single(auditLogger.Events.Where(auditEvent =>
            auditEvent.OperationType == OperationAuditTypes.CashierLogout));
        Assert.Equal("Succeeded", logout.Outcome);
        Assert.Equal("MANUAL_LOCK", logout.ReasonCode);
        Assert.Equal("CASHIER-1", logout.CashierId);

        var lockReport = runtimeStatus.Reports.Last();
        Assert.Null(lockReport.CashierId);
        Assert.Null(lockReport.CashierName);

        await viewModel.LoginCashierByBarcodeAsync("BAR-1");

        Assert.False(viewModel.IsCashierLoginOverlayOpen);
        Assert.Same(cashierSession, viewModel.Session.CashierSession);
        Assert.Single(cart.Lines);
    }

    [Fact]
    public async Task Cashier_login_and_shutdown_logout_record_operation_audits()
    {
        var auditLogger = new RecordingOperationAuditLogger();
        var cashierSession = CreateCashierSession(Permissions.PosTerminal.Sales.AddItem);
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cashierLoginService: new FakeCashierLoginService(cashierSession),
            operationAuditLogger: auditLogger);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        viewModel.CashierBarcodeInput = "BAR-1";
        await viewModel.LoginCashierCommand.ExecuteAsync(null);
        await viewModel.ReportOfflineForShutdownAsync();

        Assert.Collection(
            auditLogger.Events,
            auditEvent =>
            {
                Assert.Equal("CASHIER_LOGIN", auditEvent.OperationType);
                Assert.Equal("Succeeded", auditEvent.Outcome);
                Assert.Equal("CASHIER-1", auditEvent.CashierId);
            },
            auditEvent =>
            {
                Assert.Equal("CASHIER_LOGOUT", auditEvent.OperationType);
                Assert.Equal("Succeeded", auditEvent.Outcome);
        });
    }

    [Fact]
    public async Task Pre_canceled_shutdown_records_cashier_logout_once_across_retries()
    {
        var auditLogger = new RecordingOperationAuditLogger();
        var cashierSession = CreateCashierSession(Permissions.PosTerminal.Sales.AddItem);
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cashierLoginService: new FakeCashierLoginService(cashierSession),
            operationAuditLogger: auditLogger);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        viewModel.CashierBarcodeInput = "BAR-1";
        await viewModel.LoginCashierCommand.ExecuteAsync(null);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => viewModel.ReportOfflineForShutdownAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => viewModel.ReportOfflineForShutdownAsync(cancellation.Token));

        var logoutAudit = Assert.Single(
            auditLogger.Events.Where(auditEvent =>
                auditEvent.OperationType == OperationAuditTypes.CashierLogout));
        Assert.Equal("Succeeded", logoutAudit.Outcome);
        Assert.Equal("CASHIER-1", logoutAudit.CashierId);
        Assert.Equal("APP_SHUTDOWN", logoutAudit.ReasonCode);
    }

    [Fact]
    public async Task Shutdown_offline_report_receives_coordinator_cancellation_token()
    {
        var runtimeStatus = new RecordingRuntimeStatusApiClient();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            runtimeStatusApiClient: runtimeStatus);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        using var cancellation = new CancellationTokenSource();

        await viewModel.ReportOfflineForShutdownAsync(cancellation.Token);

        Assert.True(runtimeStatus.LastCancellationToken.CanBeCanceled);
        Assert.Equal(cancellation.Token, runtimeStatus.LastCancellationToken);
    }

    [Fact]
    public async Task Shutdown_ignores_late_online_connectivity_result()
    {
        var connectivityStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedOnlineResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectivity = new FakeConnectivityApiClient(false);
        var runtimeStatus = new RecordingRuntimeStatusApiClient();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            connectivityApiClient: connectivity,
            runtimeStatusApiClient: runtimeStatus);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        runtimeStatus.Reports.Clear();
        connectivity.CheckOnlineStarted = connectivityStarted;
        connectivity.PendingResponse = delayedOnlineResult;

        var refreshTask = InvokeRefreshOnlineStateAsync(viewModel, autoRetryOrders: true);
        await connectivityStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        viewModel.BeginShutdown();
        var offlineTask = viewModel.ReportOfflineForShutdownAsync();
        Assert.False(offlineTask.IsCompleted);
        delayedOnlineResult.TrySetResult(true);
        await Task.WhenAll(refreshTask, offlineTask);

        Assert.False(viewModel.Session.IsOnline);
        var report = Assert.Single(runtimeStatus.Reports);
        Assert.False(report.IsOnline);
    }

    [Fact]
    public async Task Shutdown_waits_for_ignored_online_report_before_reporting_offline()
    {
        var runtimeStatus = new BlockingOnlineRuntimeStatusApiClient();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            connectivityApiClient: new FakeConnectivityApiClient(true),
            runtimeStatusApiClient: runtimeStatus);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        var refreshTask = InvokeRefreshOnlineStateAsync(viewModel, autoRetryOrders: true);
        await runtimeStatus.OnlineStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        var offlineTask = viewModel.ReportOfflineForShutdownAsync();

        Assert.False(offlineTask.IsCompleted);
        Assert.Empty(runtimeStatus.CompletedReports);

        runtimeStatus.ReleaseOnline.TrySetResult();
        await Task.WhenAll(refreshTask, offlineTask);

        Assert.Equal([true, false], runtimeStatus.CompletedReports.Select(report => report.IsOnline));
    }

    [Fact]
    public async Task Shutdown_budget_exhaustion_never_reports_offline_before_late_online_finishes()
    {
        var runtimeStatus = new BlockingOnlineRuntimeStatusApiClient();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            connectivityApiClient: new FakeConnectivityApiClient(true),
            runtimeStatusApiClient: runtimeStatus);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        var refreshTask = InvokeRefreshOnlineStateAsync(viewModel, autoRetryOrders: true);
        await runtimeStatus.OnlineStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        using var exhaustedBudget = new CancellationTokenSource();
        exhaustedBudget.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => viewModel.ReportOfflineForShutdownAsync(exhaustedBudget.Token));
        Assert.Empty(runtimeStatus.CompletedReports);

        runtimeStatus.ReleaseOnline.TrySetResult();
        await refreshTask;

        var report = Assert.Single(runtimeStatus.CompletedReports);
        Assert.True(report.IsOnline);
    }

    [Fact]
    public async Task Dispose_cancels_lifetime_and_ignores_late_connectivity_result()
    {
        var connectivityStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedOnlineResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectivity = new FakeConnectivityApiClient(false)
        {
            CheckOnlineStarted = connectivityStarted,
            PendingResponse = delayedOnlineResult
        };
        var runtimeStatus = new RecordingRuntimeStatusApiClient();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            connectivityApiClient: connectivity,
            runtimeStatusApiClient: runtimeStatus);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        runtimeStatus.Reports.Clear();

        var refreshTask = InvokeRefreshOnlineStateAsync(viewModel, autoRetryOrders: true);
        await connectivityStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        viewModel.Dispose();
        delayedOnlineResult.TrySetResult(true);
        await refreshTask;

        Assert.False(viewModel.Session.IsOnline);
        Assert.Empty(runtimeStatus.Reports);
    }

    [Fact]
    public async Task Begin_shutdown_and_dispose_do_not_block_on_external_cancellation_callback()
    {
        var viewModel = CreateAuthorizedMainViewModel(new FakeCustomerDisplayWindowService());
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var cancellation = Assert.IsType<CancellationTokenSource>(
            typeof(MainViewModel)
                .GetField("_shutdownCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(viewModel));
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellation.Token.Register(() =>
        {
            callbackStarted.TrySetResult();
            try
            {
                Thread.Sleep(100);
            }
            finally
            {
                callbackCompleted.TrySetResult();
            }
        });

        var beginStartedAt = Stopwatch.GetTimestamp();
        viewModel.BeginShutdown();
        Assert.True(Stopwatch.GetElapsedTime(beginStartedAt) < TimeSpan.FromSeconds(1));
        await callbackStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        var disposeStartedAt = Stopwatch.GetTimestamp();
        viewModel.Dispose();
        Assert.True(Stopwatch.GetElapsedTime(disposeStartedAt) < TimeSpan.FromSeconds(1));

        await callbackCompleted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        var shutdownCancellationTask = Assert.IsAssignableFrom<Task>(
            typeof(MainViewModel)
                .GetField("_shutdownCancellationTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(viewModel));
        await shutdownCancellationTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
    }

    [Theory]
    [InlineData("oom")]
    [InlineData("stack")]
    public async Task Shutdown_offline_step_propagates_fatal_shell_cancellation_instance(string fatalKind)
    {
        Exception fatal = fatalKind == "oom"
            ? new OutOfMemoryException("fatal shell shutdown callback")
            : new StackOverflowException("fatal shell shutdown callback");
        using var viewModel = CreateAuthorizedMainViewModel(new FakeCustomerDisplayWindowService());
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var cancellation = Assert.IsType<CancellationTokenSource>(
            typeof(MainViewModel)
                .GetField("_shutdownCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(viewModel));
        using var registration = cancellation.Token.Register(() => throw fatal);

        var thrown = await Record.ExceptionAsync(() => viewModel.ReportOfflineForShutdownAsync());

        Assert.Same(fatal, thrown);
    }

    [Fact]
    public async Task Begin_shutdown_stops_shell_timers_and_locks_cached_payment()
    {
        using var viewModel = CreateAuthorizedMainViewModel(new FakeCustomerDisplayWindowService());
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var timerFields = new[]
        {
            "_clockTimer",
            "_connectivityTimer",
            "_catalogDownloadHideTimer"
        };
        var timers = timerFields
            .Select(fieldName => Assert.IsType<System.Windows.Threading.DispatcherTimer>(
                typeof(MainViewModel)
                    .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(viewModel)))
            .ToArray();
        foreach (var timer in timers)
        {
            timer.Start();
        }

        viewModel.BeginShutdown();
        viewModel.BeginShutdown();

        Assert.All(timers, timer => Assert.False(timer.IsEnabled));
        Assert.NotNull(viewModel.CashPayment);
        Assert.True(viewModel.CashPayment!.IsPaymentInteractionLocked);
        Assert.False(viewModel.CashPayment.SelectCardCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("oom")]
    [InlineData("stack")]
    public async Task Shutdown_offline_step_propagates_fatal_payment_cancellation_instance(string fatalKind)
    {
        Exception fatal = fatalKind == "oom"
            ? new OutOfMemoryException("fatal payment shutdown callback")
            : new StackOverflowException("fatal payment shutdown callback");
        using var viewModel = CreateAuthorizedMainViewModel(new FakeCustomerDisplayWindowService());
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var payment = Assert.IsType<PaymentViewModel>(viewModel.CashPayment);
        var cardSession = Assert.IsType<CardPaymentSession>(
            typeof(PaymentViewModel)
                .GetField("_cardSession", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(payment));
        var activePayment = cardSession.BeginCardPayment();
        using var registration = activePayment.Token.Register(() => throw fatal);

        try
        {
            var thrown = await Record.ExceptionAsync(() => viewModel.ReportOfflineForShutdownAsync());

            Assert.Same(fatal, thrown);
            Assert.True(payment.IsPaymentInteractionLocked);
        }
        finally
        {
            cardSession.EndCardPayment(activePayment);
        }
    }

    [Theory]
    [InlineData("oom")]
    [InlineData("stack")]
    public async Task Shutdown_coordinator_propagates_payment_fatal_after_runtime_offline_step_timeout(
        string fatalKind)
    {
        Exception fatal = fatalKind == "oom"
            ? new OutOfMemoryException("late fatal payment shutdown callback")
            : new StackOverflowException("late fatal payment shutdown callback");
        using var viewModel = CreateAuthorizedMainViewModel(new FakeCustomerDisplayWindowService());
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var payment = Assert.IsType<PaymentViewModel>(viewModel.CashPayment);
        var cardSession = Assert.IsType<CardPaymentSession>(
            typeof(PaymentViewModel)
                .GetField("_cardSession", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(payment));
        var activePayment = cardSession.BeginCardPayment();
        using var releaseCallback = new ManualResetEventSlim(false);
        using var registration = activePayment.Token.Register(() =>
        {
            releaseCallback.Wait();
            throw fatal;
        });
        var coordinator = new AppShutdownCoordinator(totalBudget: TimeSpan.FromSeconds(1));
        var nextStepCalled = false;
        coordinator.RegisterStep(
            "runtime-offline",
            100,
            TimeSpan.FromMilliseconds(20),
            token => viewModel.ReportOfflineForShutdownAsync(token));
        coordinator.RegisterStep(
            "release-payment-callback",
            200,
            TimeSpan.FromSeconds(1),
            _ =>
            {
                nextStepCalled = true;
                releaseCallback.Set();
                return Task.CompletedTask;
            });

        try
        {
            var thrown = await Record.ExceptionAsync(
                () => Task.Run(() =>
                    App.WaitForShutdownPreparation(coordinator, TimeSpan.FromSeconds(1))));

            Assert.Same(fatal, thrown);
            Assert.True(nextStepCalled);
            Assert.True(payment.IsPaymentInteractionLocked);
        }
        finally
        {
            releaseCallback.Set();
            cardSession.EndCardPayment(activePayment);
        }
    }

    [Fact]
    public async Task Cashier_login_denied_without_established_session_does_not_use_placeholder_employee()
    {
        var auditLogger = new RecordingOperationAuditLogger();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cashierLoginService: new FailedCashierLoginService(),
            operationAuditLogger: auditLogger);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.LoginCashierByBarcodeAsync("EMPLOYEE-BARCODE-SECRET");

        var auditEvent = Assert.Single(auditLogger.Events);
        Assert.Equal("CASHIER_LOGIN", auditEvent.OperationType);
        Assert.Equal("Denied", auditEvent.Outcome);
        Assert.True(string.IsNullOrEmpty(auditEvent.CashierId));
        Assert.True(string.IsNullOrEmpty(auditEvent.UserGuid));
        Assert.True(string.IsNullOrEmpty(auditEvent.CashierName));
        Assert.DoesNotContain("EMPLOYEE-BARCODE-SECRET", auditEvent.SafeMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cashier_login_failure_uses_localized_error_code_instead_of_backend_message()
    {
        var loginResult = CashierLoginResult.Fail("后端原始中文，不应直接显示", "CASHIER_LOGIN_FAILED");
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cashierLoginService: new FixedCashierLoginService(loginResult));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.LoginCashierByBarcodeAsync("EMPLOYEE-BARCODE-SECRET");

        Assert.Equal("Cashier barcode is invalid or disabled.", viewModel.StatusMessage);

        await viewModel.ToggleCultureCommand.ExecuteAsync(null);
        await viewModel.LoginCashierByBarcodeAsync("EMPLOYEE-BARCODE-SECRET");

        Assert.Equal("收银员条码无效或已停用。", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Cashier_login_exception_without_established_session_does_not_use_placeholder_employee()
    {
        var auditLogger = new RecordingOperationAuditLogger();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cashierLoginService: new FailedCashierLoginService(new InvalidOperationException("login unavailable")),
            operationAuditLogger: auditLogger);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => viewModel.LoginCashierByBarcodeAsync("EMPLOYEE-BARCODE-SECRET"));

        var auditEvent = Assert.Single(auditLogger.Events);
        Assert.Equal("CASHIER_LOGIN", auditEvent.OperationType);
        Assert.Equal("Failed", auditEvent.Outcome);
        Assert.True(string.IsNullOrEmpty(auditEvent.CashierId));
        Assert.True(string.IsNullOrEmpty(auditEvent.UserGuid));
        Assert.True(string.IsNullOrEmpty(auditEvent.CashierName));
        Assert.False(string.IsNullOrWhiteSpace(auditEvent.TraceId));
        Assert.False(string.IsNullOrWhiteSpace(auditEvent.CorrelationId));
    }

    [Theory]
    [InlineData(true, "Succeeded")]
    [InlineData(false, "Failed")]
    public async Task History_reprint_records_actual_print_result(bool printSucceeded, string expectedOutcome)
    {
        var orderGuid = Guid.NewGuid();
        var auditLogger = new RecordingOperationAuditLogger();
        var printService = new RecordingReceiptPrintService
        {
            PrintReceiptResult = new ReceiptPrintResult(
                printSucceeded,
                printSucceeded ? "printed" : "printer offline",
                orderGuid)
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            printService,
            operationAuditLogger: auditLogger);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.ShowHistoryCommand.ExecuteAsync(null);
        var history = Assert.IsType<TransactionHistoryViewModel>(viewModel.TransactionHistory);
        history.SelectedOrder = new HistoryOrderListItem(
            orderGuid,
            TransactionHistorySource.LocalOrders,
            "1042",
            "POS-01",
            "Alice",
            DateTimeOffset.UtcNow,
            10m,
            0m,
            10m,
            1,
            "Cash",
            "Synced");

        history.ReprintCommand.Execute(null);
        await WaitUntilAsync(() => auditLogger.Events.Any(auditEvent => auditEvent.OperationType == "RECEIPT_REPRINT"));

        var auditEvent = Assert.Single(auditLogger.Events, auditEvent => auditEvent.OperationType == "RECEIPT_REPRINT");
        Assert.Equal(expectedOutcome, auditEvent.Outcome);
        Assert.Equal("HISTORY", auditEvent.ReasonCode);
        Assert.Equal(orderGuid.ToString("D"), auditEvent.OrderGuid);
        Assert.Equal(printSucceeded ? null : "printer offline", auditEvent.SafeMessage);
        var printCall = Assert.Single(printService.Calls);
        Assert.Equal(ReceiptPrintReason.Reprint, printCall.Reason);
    }

    [Theory]
    [InlineData(true, "Succeeded")]
    [InlineData(false, "Failed")]
    public async Task Remote_history_reprint_prints_loaded_remote_receipt_and_records_result(
        bool printSucceeded,
        string expectedOutcome)
    {
        var orderGuid = Guid.NewGuid();
        var remoteReceipt = new ReceiptDetails(
            orderGuid,
            "1042",
            "POS-02",
            "Remote Cashier",
            DateTimeOffset.UtcNow,
            18m,
            0m,
            18m,
            [new ReceiptPreviewLine("Remote Item", "930220", 1m, 18m, 0m, 18m)],
            [new ReceiptPaymentLine(PaymentMethodKind.Cash, 18m, null)]);
        var printService = new RecordingReceiptPrintService
        {
            PrintReceiptResult = new ReceiptPrintResult(
                printSucceeded,
                printSucceeded ? "printed" : "printer offline",
                orderGuid)
        };
        var auditLogger = new RecordingOperationAuditLogger();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            printService,
            operationAuditLogger: auditLogger,
            remoteOrderHistoryService: new RecordingRemoteOrderHistoryService(remoteReceipt));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.ShowHistoryCommand.ExecuteAsync(null);
        var history = Assert.IsType<TransactionHistoryViewModel>(viewModel.TransactionHistory);
        history.IsOnlineSourceSelected = true;
        await history.LoadAsync();

        Assert.True(history.IsReprintVisible);
        Assert.True(history.ReprintCommand.CanExecute(null));

        history.ReprintCommand.Execute(null);
        await WaitUntilAsync(() => printService.Calls.Count == 1);

        var printCall = Assert.Single(printService.Calls);
        Assert.Equal(orderGuid, printCall.OrderGuid);
        Assert.Equal(ReceiptPrintReason.Reprint, printCall.Reason);
        Assert.Same(remoteReceipt, printCall.Receipt);
        var auditEvent = Assert.Single(
            auditLogger.Events,
            auditEvent => auditEvent.OperationType == "RECEIPT_REPRINT");
        Assert.Equal(expectedOutcome, auditEvent.Outcome);
        Assert.Equal("HISTORY", auditEvent.ReasonCode);
        Assert.Equal(orderGuid.ToString("D"), auditEvent.OrderGuid);
        Assert.Equal(printSucceeded ? null : "printer offline", auditEvent.SafeMessage);
    }

    [Fact]
    public async Task Show_history_navigates_before_remote_detail_finishes_and_keeps_rows()
    {
        var orderGuid = Guid.NewGuid();
        var remoteHistory = new GatedRemoteOrderHistoryService(orderGuid);
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            remoteOrderHistoryService: remoteHistory);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.ShowHistoryCommand.ExecuteAsync(null);
        var history = Assert.IsType<TransactionHistoryViewModel>(viewModel.TransactionHistory);
        history.IsOnlineSourceSelected = true;

        var navigationTask = viewModel.ShowHistoryCommand.ExecuteAsync(null);
        await remoteHistory.DetailsStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        try
        {
            var completed = await Task.WhenAny(navigationTask, Task.Delay(AsyncTestWaitSupport.DefaultTimeout));

            Assert.Same(navigationTask, completed);
            Assert.Same(history, viewModel.CurrentScreen);
            Assert.Equal(orderGuid, Assert.Single(history.Orders).OrderGuid);
        }
        finally
        {
            remoteHistory.DetailsGate.TrySetResult(null);
            await navigationTask;
        }
    }

    [Theory]
    [InlineData(true, "Succeeded")]
    [InlineData(false, "Failed")]
    public async Task Installment_history_reprint_prints_loaded_receipt_and_records_result(
        bool printSucceeded,
        string expectedOutcome)
    {
        var installmentService = new RecordingInstallmentOrderService();
        var installmentOrder = installmentService.SeedRepaymentOrder() with
        {
            UpdatedAt = DateTimeOffset.Now
        };
        installmentService.HistoryOrders = [installmentOrder];
        var printService = new RecordingReceiptPrintService
        {
            PrintReceiptResult = new ReceiptPrintResult(
                printSucceeded,
                printSucceeded ? "printed" : "printer offline",
                installmentOrder.OrderId)
        };
        var auditLogger = new RecordingOperationAuditLogger();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            printService,
            installmentOrderService: installmentService,
            operationAuditLogger: auditLogger);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.ShowHistoryCommand.ExecuteAsync(null);
        var history = Assert.IsType<TransactionHistoryViewModel>(viewModel.TransactionHistory);
        history.SelectedTerminalOption = history.TerminalOptions.Single(option => option.DeviceCode is null);
        history.IsInstallmentSourceSelected = true;
        await history.LoadAsync();

        var selectedOrder = Assert.Single(history.Orders);
        Assert.Equal(installmentOrder.OrderId, selectedOrder.OrderGuid);
        var installmentReceipt = Assert.IsType<ReceiptDetails>(history.SelectedReceipt);
        Assert.True(history.IsReprintVisible);
        Assert.True(history.ReprintCommand.CanExecute(null));

        history.ReprintCommand.Execute(null);
        await WaitUntilAsync(() => printService.Calls.Count == 1);

        var printCall = Assert.Single(printService.Calls);
        Assert.Equal(installmentOrder.OrderId, printCall.OrderGuid);
        Assert.Equal(ReceiptPrintReason.Reprint, printCall.Reason);
        Assert.Same(installmentReceipt, printCall.Receipt);
        var auditEvent = Assert.Single(
            auditLogger.Events,
            auditEvent => auditEvent.OperationType == "RECEIPT_REPRINT");
        Assert.Equal(expectedOutcome, auditEvent.Outcome);
        Assert.Equal("HISTORY", auditEvent.ReasonCode);
        Assert.Equal(installmentOrder.OrderId.ToString("D"), auditEvent.OrderGuid);
        Assert.Equal(printSucceeded ? null : "printer offline", auditEvent.SafeMessage);
    }

    [Theory]
    [InlineData(TransactionHistorySource.RemoteOrders)]
    [InlineData(TransactionHistorySource.InstallmentOrders)]
    public async Task History_reprint_task_cancellation_does_not_escape_event_bridge_and_records_visible_failure(
        TransactionHistorySource source)
    {
        var orderGuid = Guid.NewGuid();
        var remoteReceipt = new ReceiptDetails(
            orderGuid,
            "1042",
            "POS-02",
            "Remote Cashier",
            DateTimeOffset.UtcNow,
            18m,
            0m,
            18m,
            [new ReceiptPreviewLine("Remote Item", "930220", 1m, 18m, 0m, 18m)],
            [new ReceiptPaymentLine(PaymentMethodKind.Cash, 18m, null)]);
        var installmentService = new RecordingInstallmentOrderService();
        IRemoteOrderHistoryService? remoteHistoryService = null;
        if (source == TransactionHistorySource.RemoteOrders)
        {
            remoteHistoryService = new RecordingRemoteOrderHistoryService(remoteReceipt);
        }
        else
        {
            var installmentOrder = installmentService.SeedRepaymentOrder() with
            {
                UpdatedAt = DateTimeOffset.Now
            };
            installmentService.HistoryOrders = [installmentOrder];
            orderGuid = installmentOrder.OrderId;
        }

        var printService = new RecordingReceiptPrintService
        {
            PrintReceiptException = new TaskCanceledException("printer timed out")
        };
        var auditLogger = new RecordingOperationAuditLogger();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            printService,
            installmentOrderService: installmentService,
            operationAuditLogger: auditLogger,
            remoteOrderHistoryService: remoteHistoryService);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.ShowHistoryCommand.ExecuteAsync(null);
        var history = Assert.IsType<TransactionHistoryViewModel>(viewModel.TransactionHistory);
        if (source == TransactionHistorySource.RemoteOrders)
        {
            history.IsOnlineSourceSelected = true;
        }
        else
        {
            history.SelectedTerminalOption = history.TerminalOptions.Single(option => option.DeviceCode is null);
            history.IsInstallmentSourceSelected = true;
        }
        await history.LoadAsync();

        Assert.True(history.ReprintCommand.CanExecute(null));

        var eventBridgeContext = new RecordingSynchronizationContext();
        var originalContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(eventBridgeContext);
            history.ReprintCommand.Execute(null);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(originalContext);
        }
        await WaitUntilAsync(() => auditLogger.Events.Any(
            auditEvent => auditEvent.OperationType == "RECEIPT_REPRINT"));

        Assert.Empty(eventBridgeContext.Exceptions);
        var auditEvent = Assert.Single(
            auditLogger.Events,
            auditEvent => auditEvent.OperationType == "RECEIPT_REPRINT");
        Assert.Equal("Failed", auditEvent.Outcome);
        Assert.Equal("HISTORY_EXCEPTION", auditEvent.ReasonCode);
        Assert.Equal(nameof(TaskCanceledException), auditEvent.SafeMessage);
        Assert.Equal(orderGuid.ToString("D"), auditEvent.OrderGuid);
        Assert.Equal("Receipt print failed: TaskCanceledException", viewModel.StatusMessage);
        Assert.Single(printService.Calls);
    }

    [Fact]
    public async Task Cashier_login_keeps_login_success_when_runtime_status_report_fails()
    {
        var cashierSession = CreateCashierSession(Permissions.PosTerminal.Sales.AddItem);
        var runtimeStatus = new RecordingRuntimeStatusApiClient
        {
            ReportException = new InvalidOperationException("runtime status unavailable")
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cashierLoginService: new FakeCashierLoginService(cashierSession),
            runtimeStatusApiClient: runtimeStatus);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        viewModel.CashierBarcodeInput = "BAR-1";

        await viewModel.LoginCashierCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsCashierLoginOverlayOpen);
        Assert.Same(cashierSession, viewModel.Session.CashierSession);
        Assert.Equal(1, runtimeStatus.CallCount);
    }

    [Fact]
    public async Task Cashier_login_overlay_does_not_cover_device_reregistration_dialog()
    {
        var viewModel = CreateAuthorizedMainViewModel(new FakeCustomerDisplayWindowService());

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        viewModel.IsDeviceReregistrationDialogOpen = true;

        Assert.False(viewModel.IsCashierLoginOverlayOpen);
    }

    [Fact]
    public async Task Active_page_title_uses_payment_mode_for_payment_screen()
    {
        var priceIndex = new LocalSellableItemIndex();
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        var catalogRepo = new FakeCatalogRepository();
        var specialProduct = new FakeSpecialProductService();
        var deviceRepo = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var fingerprint = new FakeDeviceFingerprintService();
        var orderRepo = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var localization = new LocalizationService();
        var viewModel = new MainViewModel(
            new PosCoreServices(priceIndex, cart, checkout, new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), new FakeRawScannerService(), null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(localization, new FakeSettingsRepository()),
            new ShellCatalogService(priceIndex, catalogRepo, new FakeCatalogSyncService()),
            catalogRepo,
            new FakeRemoteLookupRefreshService(),
            specialProduct,
            new MainShellStartupService(deviceRepo, fingerprint, new DeviceAuthorizationState()),
            orderRepo,
            new ShellSyncCenterService(syncQueue),
            localization,
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(orderRepo),
            new CashPaymentWorkflowService(checkout, orderRepo, syncQueue),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), deviceRepo, fingerprint),
            new SpecialProductsWorkflowService(priceIndex, cart, catalogRepo, specialProduct),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                priceIndex,
                cart,
                remoteLookupRefreshAsync,
                reloadCatalogAsync));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        cart.AddReturnLine(new ReturnCartLineRequest(
            "1042",
            "SKU-REFUND-TITLE",
            null,
            "Refund Title Tea",
            "930TITLE1",
            "ITEM-REFUND-TITLE",
            null,
            1m,
            9.9m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-TITLE-1",
            Guid.NewGuid(),
            Guid.NewGuid()));

        viewModel.ShowCashPaymentCommand.Execute(null);
        Assert.Equal("Refund", viewModel.ActivePageTitleText);

        cart.Clear();
        cart.AddItem(CreateItem("1042", "SKU-ZERO-TITLE", "930TITLE2"));
        cart.AddReturnLine(new ReturnCartLineRequest(
            "1042",
            "SKU-ZERO-RET",
            null,
            "Zero Title Return",
            "930TITLE3",
            "ITEM-ZERO-TITLE",
            null,
            1m,
            9.9m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-TITLE-2",
            Guid.NewGuid(),
            Guid.NewGuid()));

        viewModel.ShowCashPaymentCommand.Execute(null);
        Assert.Equal("Zero Settlement", viewModel.ActivePageTitleText);

        await viewModel.ToggleCultureCommand.ExecuteAsync(null);

        cart.Clear();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "1042",
            "SKU-REFUND-TITLE-CN",
            null,
            "Refund Title Tea CN",
            "930TITLE4",
            "ITEM-REFUND-TITLE-CN",
            null,
            1m,
            9.9m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-TITLE-3",
            Guid.NewGuid(),
            Guid.NewGuid()));

        viewModel.ShowCashPaymentCommand.Execute(null);
        Assert.Equal("\u9000\u6B3E", viewModel.ActivePageTitleText);

        cart.Clear();
        cart.AddItem(CreateItem("1042", "SKU-ZERO-TITLE-CN", "930TITLE5"));
        cart.AddReturnLine(new ReturnCartLineRequest(
            "1042",
            "SKU-ZERO-RET-CN",
            null,
            "Zero Title Return CN",
            "930TITLE6",
            "ITEM-ZERO-TITLE-CN",
            null,
            1m,
            9.9m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-TITLE-4",
            Guid.NewGuid(),
            Guid.NewGuid()));

        viewModel.ShowCashPaymentCommand.Execute(null);
        Assert.Equal("\u96F6\u7ED3\u7B97", viewModel.ActivePageTitleText);
    }

    [Fact]
    public async Task Reset_scanner_binding_command_resets_scanner_and_updates_status()
    {
        var scanner = new FakeRawScannerService();
        var priceIndex = new LocalSellableItemIndex();
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        var catalogRepo = new FakeCatalogRepository();
        var specialProduct = new FakeSpecialProductService();
        var deviceRepo = new FakeLocalDeviceRepository();
        var fingerprint = new FakeDeviceFingerprintService();
        var orderRepo = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var localization = new LocalizationService();
        var viewModel = new MainViewModel(
            new PosCoreServices(priceIndex, cart, checkout, new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), scanner, null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(localization, new FakeSettingsRepository()),
            new ShellCatalogService(priceIndex, catalogRepo, new FakeCatalogSyncService()),
            catalogRepo,
            new FakeRemoteLookupRefreshService(),
            specialProduct,
            new MainShellStartupService(deviceRepo, fingerprint, new DeviceAuthorizationState()),
            orderRepo,
            new ShellSyncCenterService(syncQueue),
            localization,
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(orderRepo),
            new CashPaymentWorkflowService(checkout, orderRepo, syncQueue),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), deviceRepo, fingerprint),
            new SpecialProductsWorkflowService(priceIndex, cart, catalogRepo, specialProduct),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                priceIndex,
                cart,
                remoteLookupRefreshAsync,
                reloadCatalogAsync));

        await viewModel.ResetScannerBindingCommand.ExecuteAsync(null);

        Assert.Equal(1, scanner.ResetCount);
        Assert.Equal("Scanner binding reset. Trigger the scanner again to bind the current device.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Reset_scanner_binding_command_requires_device_registration_permission()
    {
        var scanner = new FakeRawScannerService();
        var cashierContext = new CashierSessionContext();
        cashierContext.SetCurrent(CreateCashierSession(Permissions.PosTerminal.Sales.AddItem));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            rawScannerService: scanner,
            cashierSessionContext: cashierContext,
            enforceCashierPermissions: true);

        await viewModel.ResetScannerBindingCommand.ExecuteAsync(null);

        Assert.Equal(0, scanner.ResetCount);
        Assert.False(cashierContext.RequirePermission(Permissions.PosTerminal.Settings.DeviceRegistration, out var deniedMessage));
        Assert.Equal(deniedMessage, viewModel.StatusMessage);
    }

    [Fact]
    public async Task Card_payment_completion_does_not_auto_print_receipt_after_success_screen()
    {
        var printService = new RecordingReceiptPrintService();
        var cashDrawerService = new RecordingCashDrawerService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            printService,
            cashDrawerService: cashDrawerService);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Card);

        InvokePaymentCompleted(viewModel, order);

        await WaitUntilAsync(() => IsShowingCompletedSale(viewModel));
        await Task.Delay(50);
        Assert.Empty(printService.Calls);
        Assert.Equal(0, cashDrawerService.OpenCallCount);
    }

    [Fact]
    public async Task Card_refund_completion_auto_prints_receipt_after_success_screen()
    {
        var printService = new RecordingReceiptPrintService();
        var cashDrawerService = new RecordingCashDrawerService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            printService,
            cashDrawerService: cashDrawerService);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Card) with
        {
            TotalAmount = -10m,
            ActualAmount = -10m,
            Payments =
            [
                new LocalPayment(
                    Guid.NewGuid(),
                    PaymentMethodKind.Card,
                    -10m,
                    "CARD-REFUND-123")
            ]
        };

        InvokePaymentCompleted(viewModel, order);

        await WaitUntilAsync(() => IsShowingCompletedSale(viewModel) && printService.Calls.Count == 1);
        var call = Assert.Single(printService.Calls);
        Assert.Equal(order.OrderGuid, call.OrderGuid);
        Assert.Equal(ReceiptPrintReason.CardAuto, call.Reason);
        Assert.Equal(0, cashDrawerService.OpenCallCount);
    }

    [Fact]
    public async Task Mixed_card_and_voucher_refund_keeps_card_refund_receipt()
    {
        var printService = new RecordingReceiptPrintService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            printService);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Card, PaymentMethodKind.Voucher) with
        {
            TotalAmount = -10m,
            ActualAmount = -10m,
            Payments =
            [
                new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Card, -6m, "CARD-REFUND-123"),
                new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Voucher, -4m, "VOUCHER_REFUND:RF123")
            ]
        };

        InvokePaymentCompleted(viewModel, order);

        await WaitUntilAsync(() => IsShowingCompletedSale(viewModel) && printService.Calls.Count == 1);
        var call = Assert.Single(printService.Calls);
        Assert.Equal(ReceiptPrintReason.CardAuto, call.Reason);
        Assert.Null(call.Receipt!.RefundVoucher);
        var document = new ReceiptTextFormatter().Build(call.Receipt, ReceiptPrinterSettings.Default, order.SoldAt);
        Assert.Contains("TAX INVOICE", document.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("REFUND VOUCHER", document.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Voucher_refund_completion_auto_prints_voucher_after_success_screen()
    {
        var printService = new RecordingReceiptPrintService();
        var cashierContext = new CashierSessionContext();
        cashierContext.SetCurrent(CreateCashierSession(Permissions.PosTerminal.Sales.AddItem));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            printService,
            cashierSessionContext: cashierContext,
            enforceCashierPermissions: true);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Voucher) with
        {
            TotalAmount = -8m,
            ActualAmount = -8m,
            Payments =
            [
                new LocalPayment(
                    Guid.NewGuid(),
                    PaymentMethodKind.Voucher,
                    -8m,
                    "VOUCHER_REFUND:RF123")
            ]
        };

        InvokePaymentCompleted(viewModel, order);

        await WaitUntilAsync(() => IsShowingCompletedSale(viewModel) && printService.Calls.Count == 1);
        var call = Assert.Single(printService.Calls);
        Assert.Equal(order.OrderGuid, call.OrderGuid);
        Assert.Equal("VoucherRefundAuto", call.Reason.ToString());
        Assert.Equal("RF123", call.Receipt!.RefundVoucher!.VoucherCode);
        Assert.Equal(8m, call.Receipt.RefundVoucher.Amount);
        Assert.False(cashierContext.RequirePermission(Permissions.PosTerminal.Receipt.PrintLast, out _));
    }

    [Fact]
    public async Task Pending_voucher_refund_completion_does_not_auto_print()
    {
        var printService = new RecordingReceiptPrintService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            printService);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Voucher) with
        {
            TotalAmount = -8m,
            ActualAmount = -8m,
            Payments =
            [
                new LocalPayment(
                    Guid.NewGuid(),
                    PaymentMethodKind.Voucher,
                    -8m,
                    "VOUCHER_REFUND_PENDING")
            ]
        };

        InvokePaymentCompleted(viewModel, order);

        await WaitUntilAsync(() => IsShowingCompletedSale(viewModel));
        await Task.Delay(50);
        Assert.Empty(printService.Calls);
    }

    [Fact]
    public async Task Voucher_payment_with_remaining_balance_auto_prints_standalone_balance_without_permission()
    {
        var printService = new RecordingReceiptPrintService();
        var cashierContext = new CashierSessionContext();
        cashierContext.SetCurrent(CreateCashierSession(Permissions.PosTerminal.Sales.AddItem));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            printService,
            cashierSessionContext: cashierContext,
            enforceCashierPermissions: true);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Voucher) with
        {
            Payments = [new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Voucher, 10m, "VOUCHER:VC200:LOCK-1:12.34")]
        };

        InvokePaymentCompleted(viewModel, order);

        await WaitUntilAsync(() => printService.Calls.Count == 1);
        var call = Assert.Single(printService.Calls);
        Assert.Equal(ReceiptPrintReason.VoucherBalanceAuto, call.Reason);
        Assert.Equal("VC200", call.Receipt!.VoucherBalance!.VoucherCode);
        Assert.Equal(12.34m, call.Receipt.VoucherBalance.RemainingBalance);
        Assert.Null(call.Receipt.RefundVoucher);
        Assert.False(cashierContext.RequirePermission(Permissions.PosTerminal.Receipt.PrintLast, out _));
    }

    [Fact]
    public async Task Multiple_voucher_balances_print_once_per_normalized_unique_code()
    {
        var printService = new RecordingReceiptPrintService();
        var viewModel = CreateAuthorizedMainViewModel(new FakeCustomerDisplayWindowService(), printService);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Voucher) with
        {
            Payments =
            [
                new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Voucher, 4m, "VOUCHER:VC200:LOCK-1:12.34"),
                new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Voucher, 3m, "VOUCHER: vc200 :LOCK-2:9.00"),
                new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Voucher, 3m, "VOUCHER:VC201:LOCK-3:5.67")
            ]
        };

        InvokePaymentCompleted(viewModel, order);

        await WaitUntilAsync(() => printService.Calls.Count == 2);
        Assert.All(printService.Calls, call => Assert.Equal(ReceiptPrintReason.VoucherBalanceAuto, call.Reason));
        Assert.Equal(["VC200", "VC201"], printService.Calls.Select(call => call.Receipt!.VoucherBalance!.VoucherCode).ToArray());
        Assert.Equal(9.00m, printService.Calls[0].Receipt!.VoucherBalance!.RemainingBalance);
    }

    [Fact]
    public async Task Voucher_balance_auto_print_skips_invalid_zero_and_refund_payments()
    {
        var cases = new[]
        {
            new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Voucher, 10m, "VOUCHER:VC200:LOCK-1:0.00"),
            new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Voucher, 10m, "VOUCHER:VC200:LOCK-1:not-money"),
            new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Voucher, 10m, "VOUCHER:  :LOCK-1:12.34"),
            new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Voucher, -10m, "VOUCHER:VC200:LOCK-1:12.34"),
            new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Cash, 10m, "VOUCHER:VC200:LOCK-1:12.34")
        };

        foreach (var payment in cases)
        {
            var printService = new RecordingReceiptPrintService();
            var viewModel = CreateAuthorizedMainViewModel(new FakeCustomerDisplayWindowService(), printService);
            await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
            var order = CreateReceiptPrintOrder(payment.Method) with { Payments = [payment] };

            InvokePaymentCompleted(viewModel, order);

            await WaitUntilAsync(() => IsShowingCompletedSale(viewModel));
            await Task.Delay(30);
            Assert.Empty(printService.Calls);
        }
    }

    [Fact]
    public async Task Voucher_balance_auto_print_continues_after_failure_and_restores_first_error()
    {
        var printService = new RecordingReceiptPrintService();
        printService.PrintReceiptResults.Enqueue(new ReceiptPrintResult(false, "printer offline"));
        printService.PrintReceiptResults.Enqueue(new ReceiptPrintResult(true, "printed"));
        var viewModel = CreateAuthorizedMainViewModel(new FakeCustomerDisplayWindowService(), printService);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Voucher) with
        {
            Payments =
            [
                new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Voucher, 5m, "VOUCHER:VC200:LOCK-1:12.34"),
                new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Voucher, 5m, "VOUCHER:VC201:LOCK-2:5.67")
            ]
        };

        InvokePaymentCompleted(viewModel, order);

        await WaitUntilAsync(() => printService.Calls.Count == 2);
        Assert.Contains("VC200", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("printer offline", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Card_payment_completion_does_not_auto_print_without_receipt_permission()
    {
        var printService = new RecordingReceiptPrintService();
        var cashierContext = new CashierSessionContext();
        cashierContext.SetCurrent(CreateCashierSession(Permissions.PosTerminal.Sales.AddItem));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            printService,
            cashierSessionContext: cashierContext,
            enforceCashierPermissions: true);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Card);

        InvokePaymentCompleted(viewModel, order);

        await WaitUntilAsync(() => IsShowingCompletedSale(viewModel));
        await Task.Delay(50);
        Assert.Empty(printService.Calls);
        Assert.False(cashierContext.RequirePermission(Permissions.PosTerminal.Receipt.PrintLast, out _));
    }

    [Fact]
    public async Task Cash_payment_completion_does_not_auto_print_receipt()
    {
        var printService = new RecordingReceiptPrintService();
        var cashDrawerService = new RecordingCashDrawerService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            printService,
            cashDrawerService: cashDrawerService);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Cash);

        InvokePaymentCompleted(viewModel, order);

        await WaitUntilAsync(() => IsShowingCompletedSale(viewModel) && cashDrawerService.OpenCallCount == 1);
        await Task.Delay(50);
        Assert.Empty(printService.Calls);
        Assert.Equal(1, cashDrawerService.OpenCallCount);
    }

    [Fact]
    public async Task Payment_page_new_installment_auto_prints_receipt_and_returns_to_pos()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("1042", "SKU-INST-AUTO", "930INST") with { RetailPrice = 80m });
        var printService = new RecordingReceiptPrintService();
        var installmentService = new RecordingInstallmentOrderService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            printService,
            connectivityApiClient: new FakeConnectivityApiClient(true),
            cart: cart,
            installmentOrderService: installmentService);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await InvokeRefreshOnlineStateAsync(viewModel);
        viewModel.ShowCashPaymentCommand.Execute(null);
        var payment = viewModel.CashPayment!;
        payment.IsInstallmentPaymentEnabled = true;
        payment.InstallmentCustomerName = "Alice";
        payment.InstallmentCustomerPhone = "0400111222";
        payment.TenderAmountText = "20";

        await payment.SelectCashCommand.ExecuteAsync(null);
        await payment.ConfirmPaymentCommand.ExecuteAsync(null);

        await WaitUntilAsync(() => ReferenceEquals(viewModel.PosTerminal, viewModel.CurrentScreen) && printService.Calls.Count == 1);
        var call = Assert.Single(printService.Calls);
        Assert.Equal(installmentService.CreatedLocalOrder!.OrderGuid, call.OrderGuid);
        Assert.Equal(ReceiptPrintReason.InstallmentAuto, call.Reason);
        Assert.Empty(cart.Lines);
        Assert.Empty(payment.PaymentTenders);
        Assert.False(payment.IsInstallmentPaymentEnabled);
        Assert.False(payment.IsInstallmentSwitchLocked);
    }

    [Fact]
    public async Task Payment_page_new_installment_returns_to_pos_when_auto_print_fails()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("1042", "SKU-INST-PRINT-FAIL", "930INSF") with { RetailPrice = 80m });
        var printService = new RecordingReceiptPrintService
        {
            PrintReceiptResult = new ReceiptPrintResult(false, "printer offline")
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            printService,
            connectivityApiClient: new FakeConnectivityApiClient(true),
            cart: cart,
            installmentOrderService: new RecordingInstallmentOrderService());
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await InvokeRefreshOnlineStateAsync(viewModel);
        viewModel.ShowCashPaymentCommand.Execute(null);
        var payment = viewModel.CashPayment!;
        payment.IsInstallmentPaymentEnabled = true;
        payment.InstallmentCustomerName = "Alice";
        payment.InstallmentCustomerPhone = "0400111222";
        payment.TenderAmountText = "20";

        await payment.SelectCashCommand.ExecuteAsync(null);
        await payment.ConfirmPaymentCommand.ExecuteAsync(null);

        await WaitUntilAsync(() => ReferenceEquals(viewModel.PosTerminal, viewModel.CurrentScreen) && printService.Calls.Count == 1);
        Assert.Contains("printer offline", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Payment_page_installment_repayment_auto_prints_receipt_and_returns_to_pos()
    {
        var printService = new RecordingReceiptPrintService();
        var installmentService = new RecordingInstallmentOrderService();
        var confirmationDialog = new FakeConfirmationDialogService();
        var order = installmentService.SeedRepaymentOrder();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            printService,
            connectivityApiClient: new FakeConnectivityApiClient(true),
            installmentOrderService: installmentService,
            confirmationDialogService: confirmationDialog);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await InvokeRefreshOnlineStateAsync(viewModel);

        await InvokeShowInstallmentRepaymentAsync(viewModel, order);
        var payment = viewModel.CashPayment!;
        payment.TenderAmountText = "30";

        await payment.SelectCashCommand.ExecuteAsync(null);
        await payment.ConfirmPaymentCommand.ExecuteAsync(null);

        await WaitUntilAsync(() => ReferenceEquals(viewModel.PosTerminal, viewModel.CurrentScreen) && printService.Calls.Count == 1);
        var call = Assert.Single(printService.Calls);
        Assert.Equal(installmentService.CreatedLocalOrder!.OrderGuid, call.OrderGuid);
        Assert.Equal(ReceiptPrintReason.InstallmentAuto, call.Reason);
        Assert.Equal(0, confirmationDialog.ConfirmInstallmentPickupAfterPaidOffCallCount);
        Assert.Empty(payment.PaymentTenders);
        Assert.False(payment.IsInstallmentPaymentEnabled);
        Assert.False(payment.IsInstallmentSwitchLocked);
    }

    [Fact]
    public async Task Payment_page_installment_repayment_paid_off_confirms_pickup_before_auto_print()
    {
        var printService = new RecordingReceiptPrintService();
        var installmentService = new RecordingInstallmentOrderService();
        var confirmationDialog = new FakeConfirmationDialogService { ConfirmInstallmentPickupAfterPaidOffResult = true };
        var order = installmentService.SeedRepaymentOrder();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            printService,
            connectivityApiClient: new FakeConnectivityApiClient(true),
            installmentOrderService: installmentService,
            confirmationDialogService: confirmationDialog);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await InvokeRefreshOnlineStateAsync(viewModel);

        await InvokeShowInstallmentRepaymentAsync(viewModel, order);
        var payment = viewModel.CashPayment!;
        payment.TenderAmountText = "60";

        await payment.SelectCashCommand.ExecuteAsync(null);
        await payment.ConfirmPaymentCommand.ExecuteAsync(null);

        await WaitUntilAsync(() => ReferenceEquals(viewModel.PosTerminal, viewModel.CurrentScreen) && printService.Calls.Count == 1);
        var call = Assert.Single(printService.Calls);
        Assert.Equal(1, confirmationDialog.ConfirmInstallmentPickupAfterPaidOffCallCount);
        Assert.Equal(1, installmentService.ConfirmPickupCallCount);
        Assert.Equal(order.OrderId, installmentService.LastConfirmPickupOrderId);
        Assert.Equal("*** Paid - Picked Up ***", call.Receipt!.StatusText);
        Assert.Contains("Pickup: Confirmed", call.Receipt.ExtraInfoLines!);
        var pickedUpAtText = new DateTimeOffset(2026, 7, 4, 13, 0, 0, TimeSpan.Zero)
            .ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        Assert.Contains($"Picked up at: {pickedUpAtText}", call.Receipt.ExtraInfoLines!);
        Assert.Contains("Picked up by: Alice", call.Receipt.ExtraInfoLines!);
        Assert.Contains("Pickup note: Picked up at POS", call.Receipt.ExtraInfoLines!);
    }

    [Fact]
    public async Task Payment_page_installment_repayment_paid_off_cancelled_pickup_prints_pending_receipt()
    {
        var printService = new RecordingReceiptPrintService();
        var installmentService = new RecordingInstallmentOrderService();
        var confirmationDialog = new FakeConfirmationDialogService { ConfirmInstallmentPickupAfterPaidOffResult = false };
        var order = installmentService.SeedRepaymentOrder();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            printService,
            connectivityApiClient: new FakeConnectivityApiClient(true),
            installmentOrderService: installmentService,
            confirmationDialogService: confirmationDialog);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await InvokeRefreshOnlineStateAsync(viewModel);

        await InvokeShowInstallmentRepaymentAsync(viewModel, order);
        var payment = viewModel.CashPayment!;
        payment.TenderAmountText = "60";

        await payment.SelectCashCommand.ExecuteAsync(null);
        await payment.ConfirmPaymentCommand.ExecuteAsync(null);

        await WaitUntilAsync(() => ReferenceEquals(viewModel.PosTerminal, viewModel.CurrentScreen) && printService.Calls.Count == 1);
        var call = Assert.Single(printService.Calls);
        Assert.Equal(1, confirmationDialog.ConfirmInstallmentPickupAfterPaidOffCallCount);
        Assert.Equal(0, installmentService.ConfirmPickupCallCount);
        Assert.Equal("*** Paid - Pickup Pending ***", call.Receipt!.StatusText);
        Assert.Contains("Pickup: Pending", call.Receipt.ExtraInfoLines!);
    }

    [Fact]
    public async Task Payment_page_new_installment_full_first_payment_confirms_pickup_after_create()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("1042", "SKU-INST-FULL-PICKUP", "930INFP") with { RetailPrice = 80m });
        var printService = new RecordingReceiptPrintService();
        var installmentService = new RecordingInstallmentOrderService();
        var confirmationDialog = new FakeConfirmationDialogService
        {
            ConfirmInstallmentFullFirstPaymentResult = true,
            ConfirmInstallmentPickupAfterPaidOffResult = true
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            printService,
            connectivityApiClient: new FakeConnectivityApiClient(true),
            cart: cart,
            installmentOrderService: installmentService,
            confirmationDialogService: confirmationDialog);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await InvokeRefreshOnlineStateAsync(viewModel);
        viewModel.ShowCashPaymentCommand.Execute(null);
        var payment = viewModel.CashPayment!;
        payment.IsInstallmentPaymentEnabled = true;
        payment.InstallmentCustomerName = "Alice";
        payment.InstallmentCustomerPhone = "0400111222";
        payment.TenderAmountText = "100";

        await payment.SelectCashCommand.ExecuteAsync(null);
        await payment.ConfirmPaymentCommand.ExecuteAsync(null);

        await WaitUntilAsync(() => ReferenceEquals(viewModel.PosTerminal, viewModel.CurrentScreen) && printService.Calls.Count == 1);
        var call = Assert.Single(printService.Calls);
        Assert.Equal(1, confirmationDialog.ConfirmInstallmentFullFirstPaymentCallCount);
        Assert.Equal(1, confirmationDialog.ConfirmInstallmentPickupAfterPaidOffCallCount);
        Assert.Equal(1, installmentService.ConfirmPickupCallCount);
        Assert.Equal(80m, installmentService.CreatedLocalOrder!.DownPaymentAmount);
        Assert.Equal(InstallmentStatus.PickedUp, installmentService.CreatedLocalOrder.Status);
        Assert.Contains("Pickup: Confirmed", call.Receipt!.ExtraInfoLines!);
    }

    [Fact]
    public async Task Mixed_cash_card_payment_completion_opens_cash_drawer_and_does_not_auto_print_receipt()
    {
        var printService = new RecordingReceiptPrintService();
        var cashDrawerService = new RecordingCashDrawerService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            printService,
            cashDrawerService: cashDrawerService);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Cash, PaymentMethodKind.Card);

        InvokePaymentCompleted(viewModel, order);

        await WaitUntilAsync(() =>
            IsShowingCompletedSale(viewModel) &&
            cashDrawerService.OpenCallCount == 1);

        await Task.Delay(50);
        Assert.Equal(1, cashDrawerService.OpenCallCount);
        Assert.Empty(printService.Calls);
    }

    [Fact]
    public async Task Startup_recovers_pending_installments_without_clearing_the_cart()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("1042", "SKU-RECOVERY-CART", "930REC") with { RetailPrice = 12m });
        var installmentService = new RecordingInstallmentOrderService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cart: cart,
            installmentOrderService: installmentService);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.Equal(1, installmentService.RecoverPendingOperationsCallCount);
        Assert.Equal("1042", installmentService.LastRecoverySession?.StoreCode);
        Assert.Single(cart.Lines);
    }

    [Fact]
    public async Task Preview_startup_does_not_recover_pending_installments()
    {
        var installmentService = new RecordingInstallmentOrderService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            installmentOrderService: installmentService);

        await viewModel.InitializeAsync(new AppStartupOptions([], true, null, null));

        Assert.Equal(0, installmentService.RecoverPendingOperationsCallCount);
    }

    [Fact]
    public async Task Payment_completion_plays_checkout_feedback_once()
    {
        var feedback = new RecordingUserFeedbackService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            userFeedbackService: feedback);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Card);

        InvokePaymentCompleted(viewModel, order);
        await WaitUntilAsync(() => IsShowingCompletedSale(viewModel));

        Assert.Equal([UserFeedbackCue.Checkout], feedback.Cues);
    }

    [Fact]
    public async Task Payment_completion_keeps_pos_active_and_next_scan_or_cart_add_dismisses_success_card()
    {
        var scanner = new FakeRawScannerService();
        var cart = new PosCartService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cart: cart,
            rawScannerService: scanner);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var pos = Assert.IsType<PosTerminalViewModel>(viewModel.PosTerminal);

        InvokePaymentCompleted(viewModel, CreateReceiptPrintOrder(PaymentMethodKind.Cash));
        await WaitUntilAsync(() => IsShowingCompletedSale(viewModel));

        // 成功卡片留在收银主页时扫码枪仍由收银页接收，扫任意码即收起卡片、开始下一单。
        Assert.Equal(PosTerminalViewModel.PageId, scanner.ActivePageId);
        scanner.Emit("930999");
        await WaitUntilAsync(() => !pos.IsLastSaleVisible);
        Assert.Same(pos, viewModel.CurrentScreen);

        // 无码商品、取单等其它方式把商品加入购物车同样收起卡片。
        pos.ShowLastSale(viewModel.PaymentSuccess);
        cart.AddItem(CreateItem("1042", "SKU-001", "930110"));
        Assert.False(pos.IsLastSaleVisible);

        cart.Clear();
        pos.ShowLastSale(viewModel.PaymentSuccess);
        pos.DismissLastSaleCommand.Execute(null);
        Assert.False(pos.IsLastSaleVisible);
    }

    [Fact]
    public async Task Payment_completion_post_commit_warning_is_visible_on_the_success_screen()
    {
        var viewModel = CreateAuthorizedMainViewModel(new FakeCustomerDisplayWindowService());
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Card);

        InvokePaymentCompleted(viewModel, order, hasPostCommitWarning: true);
        await WaitUntilAsync(() => IsShowingCompletedSale(viewModel));

        Assert.True(viewModel.PaymentSuccess.HasPostCommitWarning);
        Assert.Equal("Payment completed. Do not take payment again; a follow-up action needs attention.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Payment_completion_feedback_failure_does_not_block_cash_drawer_follow_up()
    {
        var cashDrawerService = new RecordingCashDrawerService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cashDrawerService: cashDrawerService,
            userFeedbackService: new ThrowingUserFeedbackService());
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Cash);

        InvokePaymentCompleted(viewModel, order);

        await WaitUntilAsync(() => cashDrawerService.OpenCallCount == 1);
        Assert.Equal("Payment completed. Do not take payment again; a follow-up action needs attention.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Manually_opening_payment_success_does_not_play_checkout_feedback()
    {
        var feedback = new RecordingUserFeedbackService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            userFeedbackService: feedback);

        await viewModel.ShowPaymentSuccessCommand.ExecuteAsync(null);
        await viewModel.ShowPaymentSuccessCommand.ExecuteAsync(null);

        Assert.Empty(feedback.Cues);
    }

    [Fact]
    public async Task Full_card_tender_auto_completes_payment_and_opens_success_screen()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("1042", "SKU-AUTO-CARD", "930AUTO"));
        var checkout = new CashCheckoutService();
        var orderRepository = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var viewModel = CreateAuthorizedMainViewModelWithPaymentWorkflow(
            cart,
            checkout,
            orderRepository,
            syncQueue,
            new ApprovedCardTerminalClient("CARD-AUTO"));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        viewModel.ShowCashPaymentCommand.Execute(null);

        await viewModel.CashPayment!.SelectCardCommand.ExecuteAsync(null);

        await WaitUntilAsync(() => IsShowingCompletedSale(viewModel));
        Assert.Empty(cart.Lines);
        Assert.Empty(viewModel.CashPayment.PaymentTenders);
    }

    [Fact]
    public async Task Full_card_tender_opens_success_screen_before_pending_sync_refresh_completes()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("1042", "SKU-CARD-SCREEN", "930SCREEN"));
        var checkout = new CashCheckoutService();
        var orderRepository = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var viewModel = CreateAuthorizedMainViewModelWithPaymentWorkflow(
            cart,
            checkout,
            orderRepository,
            syncQueue,
            new ApprovedCardTerminalClient("CARD-SCREEN"));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        // 中文注释：初始化完成后才阻塞同步概览，精确复现支付完成后的刷新窗口。
        syncQueue.Overview = new SyncQueueOverview(1, 0, 0, null);
        syncQueue.OverviewReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        syncQueue.ReleaseOverviewRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.ShowCashPaymentCommand.Execute(null);
        var payment = viewModel.CashPayment!;
        LocalOrder? completedOrder = null;
        payment.PaymentCompleted += (_, e) => completedOrder = e.Order;
        Task? selectCardTask = null;

        try
        {
            selectCardTask = payment.SelectCardCommand.ExecuteAsync(null);
            await syncQueue.OverviewReadStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            await WaitUntilAsync(() => completedOrder is not null);

            Assert.True(IsShowingCompletedSale(viewModel));
            Assert.NotNull(completedOrder);
            Assert.Equal(completedOrder!.OrderGuid, viewModel.PaymentSuccess.TransactionId);
            Assert.NotSame(payment, viewModel.CurrentScreen);
        }
        finally
        {
            syncQueue.ReleaseOverviewRead.TrySetResult();
            if (selectCardTask is not null)
            {
                await selectCardTask;
            }

            if (syncQueue.OverviewReadStarted.Task.IsCompleted)
            {
                await WaitUntilAsync(() => viewModel.PendingUploadCount == 1);
            }
        }
    }

    [Fact]
    public async Task Partial_card_tender_is_blocked_and_back_to_pos_allowed()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("1042", "SKU-PARTIAL-CARD", "930PART"));
        var checkout = new CashCheckoutService();
        var orderRepository = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var viewModel = CreateAuthorizedMainViewModelWithPaymentWorkflow(
            cart,
            checkout,
            orderRepository,
            syncQueue,
            new ApprovedCardTerminalClient("CARD-PART"));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        viewModel.ShowCashPaymentCommand.Execute(null);
        var payment = viewModel.CashPayment!;
        payment.TenderAmountText = "5";

        await payment.SelectCardCommand.ExecuteAsync(null);

        Assert.Same(payment, viewModel.CurrentScreen);
        Assert.Empty(payment.PaymentTenders);
        Assert.Equal("Card must be the final payment tender.", payment.StatusMessage);

        payment.BackToPosCommand.Execute(null);

        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
    }

    [Fact]
    public async Task Cash_refund_completion_opens_cash_drawer()
    {
        var auditLogger = new RecordingOperationAuditLogger();
        var cashDrawerService = new RecordingCashDrawerService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cashDrawerService: cashDrawerService,
            operationAuditLogger: auditLogger);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Cash) with
        {
            TotalAmount = -10m,
            ActualAmount = -10m,
            Payments = [new LocalPayment(Guid.NewGuid(), PaymentMethodKind.Cash, -10m, null)]
        };

        InvokePaymentCompleted(viewModel, order);

        await WaitUntilAsync(() => IsShowingCompletedSale(viewModel) && cashDrawerService.OpenCallCount == 1);

        Assert.Equal(1, cashDrawerService.OpenCallCount);
        var auditEvent = Assert.Single(auditLogger.Events);
        Assert.Equal("CASH_DRAWER_OPEN", auditEvent.OperationType);
        Assert.Equal("Succeeded", auditEvent.Outcome);
        Assert.Equal("PAYMENT_COMPLETE", auditEvent.ReasonCode);
    }

    [Fact]
    public async Task Cash_payment_completion_auto_drawer_requires_cash_drawer_permission()
    {
        var cashDrawerService = new RecordingCashDrawerService();
        var cashierContext = new CashierSessionContext();
        cashierContext.SetCurrent(CreateCashierSession(Permissions.PosTerminal.Sales.AddItem));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cashDrawerService: cashDrawerService,
            cashierSessionContext: cashierContext,
            enforceCashierPermissions: true);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Cash);

        InvokePaymentCompleted(viewModel, order);

        await WaitUntilAsync(() => IsShowingCompletedSale(viewModel));
        await Task.Delay(50);
        Assert.Equal(0, cashDrawerService.OpenCallCount);
        Assert.False(cashierContext.RequirePermission(Permissions.PosTerminal.CashDrawer.Open, out _));
        Assert.Equal("Payment completed. Do not take payment again; a follow-up action needs attention.", viewModel.StatusMessage);
        Assert.True(viewModel.PaymentSuccess.HasPostCommitWarning);
    }

    [Fact]
    public async Task Cash_payment_completion_shows_cash_drawer_failure_without_leaving_success_screen()
    {
        var cashDrawerService = new RecordingCashDrawerService
        {
            Result = new ReceiptPrintResult(false, "drawer offline")
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cashDrawerService: cashDrawerService);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Cash);

        InvokePaymentCompleted(viewModel, order);

        await WaitUntilAsync(() => IsShowingCompletedSale(viewModel) && cashDrawerService.OpenCallCount == 1);

        Assert.Equal(1, cashDrawerService.OpenCallCount);
        Assert.Equal("Payment completed. Do not take payment again; a follow-up action needs attention.", viewModel.StatusMessage);
        Assert.True(viewModel.PaymentSuccess.HasPostCommitWarning);
    }

    [Fact]
    public async Task Payment_success_print_button_prints_current_receipt()
    {
        var printService = new RecordingReceiptPrintService();
        var viewModel = CreateAuthorizedMainViewModel(new FakeCustomerDisplayWindowService(), printService);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Cash);
        viewModel.PaymentSuccess.LoadFromOrder(order);

        viewModel.PaymentSuccess.PrintReceiptCommand.Execute(null);

        await WaitUntilAsync(() => printService.Calls.Count == 1);
        var call = Assert.Single(printService.Calls);
        Assert.Equal(order.OrderGuid, call.OrderGuid);
        Assert.Equal(ReceiptPrintReason.Manual, call.Reason);
    }

    [Fact]
    public async Task Payment_success_print_failure_is_caught_and_reported_without_async_void_handler()
    {
        var printService = new RecordingReceiptPrintService
        {
            // 协调器会把普通异常转换成失败结果；取消类异常才会穿透到事件桥，复现原闪退路径。
            PrintReceiptException = new TaskCanceledException("printer detail must stay out of the UI")
        };
        var viewModel = CreateAuthorizedMainViewModel(new FakeCustomerDisplayWindowService(), printService);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Cash);
        viewModel.PaymentSuccess.LoadFromOrder(order);

        viewModel.PaymentSuccess.PrintReceiptCommand.Execute(null);

        await WaitUntilAsync(() =>
            printService.Calls.Count == 1 &&
            viewModel.StatusMessage.Contains(nameof(TaskCanceledException), StringComparison.Ordinal));
        Assert.Single(printService.Calls);
        Assert.DoesNotContain("printer detail must stay out of the UI", viewModel.StatusMessage, StringComparison.Ordinal);
        var handler = typeof(MainViewModel).GetMethod(
            "OnPaymentSuccessPrintReceiptRequested",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(handler);
        Assert.Null(handler!.GetCustomAttributes(typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute), inherit: false).SingleOrDefault());
    }

    [Fact]
    public void Payment_success_print_button_requires_receipt_permission()
    {
        var printService = new RecordingReceiptPrintService();
        var cashierContext = new CashierSessionContext();
        cashierContext.SetCurrent(CreateCashierSession(Permissions.PosTerminal.Sales.AddItem));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            printService,
            cashierSessionContext: cashierContext,
            enforceCashierPermissions: true);
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Cash);
        viewModel.PaymentSuccess.LoadFromOrder(order);

        viewModel.PaymentSuccess.PrintReceiptCommand.Execute(null);

        Assert.Empty(printService.Calls);
        Assert.False(cashierContext.RequirePermission(Permissions.PosTerminal.Receipt.PrintLast, out var deniedMessage));
        Assert.Equal(deniedMessage, viewModel.StatusMessage);
    }

    [Fact]
    public async Task Pos_terminal_print_last_receipt_command_uses_print_service()
    {
        var printService = new RecordingReceiptPrintService();
        var viewModel = CreateAuthorizedMainViewModel(new FakeCustomerDisplayWindowService(), printService);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        await viewModel.PosTerminal!.PrintLastReceiptCommand.ExecuteAsync(null);

        var call = Assert.Single(printService.Calls);
        Assert.Null(call.OrderGuid);
        Assert.Equal(ReceiptPrintReason.LastReceipt, call.Reason);
    }

    [Fact]
    public async Task Pos_terminal_open_cash_drawer_command_uses_cash_drawer_service()
    {
        var cashDrawerService = new RecordingCashDrawerService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cashDrawerService: cashDrawerService);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        await viewModel.PosTerminal!.OpenCashDrawerCommand.ExecuteAsync(null);

        Assert.Equal(1, cashDrawerService.OpenCallCount);
        Assert.Equal("Cash drawer opened.", viewModel.PosTerminal.StatusMessage);
    }

    [Fact]
    public async Task Pos_terminal_exit_application_command_confirms_closes_customer_display_and_exits()
    {
        var customerDisplayWindow = new FakeCustomerDisplayWindowService();
        var exitService = new RecordingApplicationExitService();
        var confirmationDialog = new FakeConfirmationDialogService { ConfirmExitApplicationResult = true };
        var viewModel = CreateAuthorizedMainViewModel(
            customerDisplayWindow,
            applicationExitService: exitService,
            confirmationDialogService: confirmationDialog);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        await viewModel.PosTerminal!.ExitApplicationCommand.ExecuteAsync(null);

        Assert.Same(confirmationDialog, viewModel.ConfirmationDialog);
        Assert.Equal(1, confirmationDialog.ConfirmExitApplicationCallCount);
        Assert.Equal(1, exitService.ExitCallCount);
        Assert.Equal(CustomerDisplayWindowMode.Closed, customerDisplayWindow.LastSetMode);
    }

    [Fact]
    public async Task Pos_terminal_exit_application_command_does_not_exit_when_cancelled()
    {
        var customerDisplayWindow = new FakeCustomerDisplayWindowService();
        var exitService = new RecordingApplicationExitService();
        var confirmationDialog = new FakeConfirmationDialogService { ConfirmExitApplicationResult = false };
        var viewModel = CreateAuthorizedMainViewModel(
            customerDisplayWindow,
            applicationExitService: exitService,
            confirmationDialogService: confirmationDialog);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var setModeCallCount = customerDisplayWindow.SetModeCallCount;

        await viewModel.PosTerminal!.ExitApplicationCommand.ExecuteAsync(null);

        Assert.Equal(1, confirmationDialog.ConfirmExitApplicationCallCount);
        Assert.Equal(0, exitService.ExitCallCount);
        Assert.Equal(setModeCallCount, customerDisplayWindow.SetModeCallCount);
    }

    [Fact]
    public async Task Main_and_pos_exit_commands_share_one_exit_flow()
    {
        var customerDisplayWindow = new FakeCustomerDisplayWindowService();
        var exitService = new RecordingApplicationExitService();
        var confirmationDialog = new FakeConfirmationDialogService { ConfirmExitApplicationResult = true };
        var viewModel = CreateAuthorizedMainViewModel(
            customerDisplayWindow,
            applicationExitService: exitService,
            confirmationDialogService: confirmationDialog);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        await Task.WhenAll(
            viewModel.ExitApplicationCommand.ExecuteAsync(null),
            viewModel.PosTerminal!.ExitApplicationCommand.ExecuteAsync(null));

        Assert.Equal(1, exitService.ExitCallCount);
    }

    [Fact]
    public async Task InitializeAsync_ShowsDeviceRegistrationWithoutWaitingForStoresOrCatalogLoad()
    {
        var allowCatalogLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanner = new FakeRawScannerService();
        var deviceApi = new FakeDeviceApiClient();
        var catalog = new FakeCatalogRepository
        {
            BeforeLoadSellableItemsAsync = () => allowCatalogLoad.Task
        };
        var priceIndex = new LocalSellableItemIndex();
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        var specialProduct = new FakeSpecialProductService();
        var deviceRepo = new FakeLocalDeviceRepository();
        var fingerprint = new FakeDeviceFingerprintService();
        var orderRepo = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var localization = new LocalizationService();
        var viewModel = new MainViewModel(
            new PosCoreServices(priceIndex, cart, checkout, new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), scanner, null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(localization, new FakeSettingsRepository()),
            new ShellCatalogService(priceIndex, catalog, new FakeCatalogSyncService()),
            catalog,
            new FakeRemoteLookupRefreshService(),
            specialProduct,
            new MainShellStartupService(deviceRepo, fingerprint, new DeviceAuthorizationState()),
            orderRepo,
            new ShellSyncCenterService(syncQueue),
            localization,
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(orderRepo),
            new CashPaymentWorkflowService(checkout, orderRepo, syncQueue),
            new DeviceRegistrationWorkflowService(deviceApi, deviceRepo, fingerprint),
            new SpecialProductsWorkflowService(priceIndex, cart, catalog, specialProduct),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                priceIndex,
                cart,
                remoteLookupRefreshAsync,
                reloadCatalogAsync));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.NotNull(viewModel.DeviceRegistration);
        Assert.Same(viewModel.DeviceRegistration, viewModel.CurrentScreen);
        Assert.Contains("activation code", viewModel.DeviceRegistration.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, deviceApi.GetStoresCallCount);
        Assert.Equal(0, catalog.LoadSellableItemsCallCount);

        scanner.Emit(DeviceActivationCode.Insert(3, "\t \n"));
        await WaitUntilAsync(() =>
            string.Equals(
                viewModel.DeviceRegistration?.ActivationCode,
                DeviceActivationCode,
                StringComparison.Ordinal));

        Assert.Empty(scanner.InterceptedBarcodes);
    }

    [Fact]
    public async Task InitializeAsync_WithPendingRebindRecovery_BlocksOldOfflineCacheUntilRecoverySettles()
    {
        var recoveryWorkflow = new PendingActivationRecoveryWorkflow(
            new HttpRequestException("activation recovery network unavailable"));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            deviceRegistrationWorkflowService: recoveryWorkflow);
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);

        var registration = Assert.IsType<DeviceRegistrationViewModel>(viewModel.DeviceRegistration);
        Assert.Same(registration, viewModel.CurrentScreen);
        Assert.True(registration.IsReregisterMode);
        Assert.False(registration.CanCancel);
        Assert.True(registration.IsBusy);
        Assert.True(registration.IsActivationRecoveryPending);
        Assert.Null(viewModel.PosTerminal);
        Assert.Equal(1, recoveryWorkflow.PendingCheckCallCount);
        Assert.Equal(0, recoveryWorkflow.RecoveryCallCount);

        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.Same(registration, viewModel.CurrentScreen);
        Assert.Null(viewModel.PosTerminal);
        Assert.True(registration.IsBusy);
        Assert.True(registration.IsActivationRecoveryPending);
        Assert.Contains("network unavailable", registration.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, recoveryWorkflow.RecoveryCallCount);
        Assert.Equal(0, recoveryWorkflow.ClearCallCount);
    }

    [Fact]
    public async Task InitializeAsync_WithUnreadableActivationRecovery_BlocksCachedOfflineStartupFailClosed()
    {
        var recoveryWorkflow = new PendingActivationRecoveryWorkflow(
            new InvalidOperationException("recovery must not run"),
            pendingCheckException: new DeviceActivationRecoveryUnreadableException());
        var startupService = new FailIfEvaluatedMainShellStartupService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            mainShellStartupService: startupService,
            deviceRegistrationWorkflowService: recoveryWorkflow);
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);

        var registration = Assert.IsType<DeviceRegistrationViewModel>(viewModel.DeviceRegistration);
        Assert.Same(registration, viewModel.CurrentScreen);
        Assert.False(registration.CanCancel);
        Assert.True(registration.IsBusy);
        Assert.True(registration.IsActivationRecoveryPending);
        Assert.Null(viewModel.PosTerminal);
        Assert.Contains("cannot be read", registration.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, startupService.EvaluateCallCount);
        Assert.Equal(1, recoveryWorkflow.PendingCheckCallCount);
        Assert.Equal(0, recoveryWorkflow.RecoveryCallCount);

        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.Same(registration, viewModel.CurrentScreen);
        Assert.True(registration.IsBusy);
        Assert.Null(viewModel.PosTerminal);
        Assert.Equal(0, startupService.EvaluateCallCount);
        Assert.Equal(0, recoveryWorkflow.RecoveryCallCount);
    }

    [Fact]
    public async Task PendingStartupRecovery_AfterDefinitiveRejection_ReturnsToExistingCachedDeviceRules()
    {
        var recoveryWorkflow = new PendingActivationRecoveryWorkflow(
            new CatalogApiException(
                "activation code is no longer available",
                errorCode: DeviceActivationReasonCodes.NotAvailable),
            removePendingBeforeFailure: true);
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            deviceRegistrationWorkflowService: recoveryWorkflow);
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        Assert.NotNull(viewModel.DeviceRegistration);
        Assert.Null(viewModel.PosTerminal);

        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.Null(viewModel.DeviceRegistration);
        Assert.NotNull(viewModel.PosTerminal);
        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.Equal(1, recoveryWorkflow.RecoveryCallCount);
        Assert.Equal(2, recoveryWorkflow.PendingCheckCallCount);
    }

    [Fact]
    public async Task InitializeAsync_WaitsForStartupCatalogLoadBeforeShowingPos()
    {
        var index = new LocalSellableItemIndex();
        var allowCatalogLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new FakeCatalogRepository
        {
            Items = [CreateItem("1042", "SKU-001", "9528502522381")],
            BeforeLoadSellableItemsAsync = () => allowCatalogLoad.Task
        };
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        var specialProduct = new FakeSpecialProductService();
        var deviceRepo = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var fingerprint = new FakeDeviceFingerprintService();
        var orderRepo = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var localization = new LocalizationService();
        var viewModel = new MainViewModel(
            new PosCoreServices(index, cart, checkout, new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), new FakeRawScannerService(), null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(localization, new FakeSettingsRepository()),
            new ShellCatalogService(index, catalog, new FakeCatalogSyncService()),
            catalog,
            new FakeRemoteLookupRefreshService(),
            specialProduct,
            new MainShellStartupService(deviceRepo, fingerprint, new DeviceAuthorizationState()),
            orderRepo,
            new ShellSyncCenterService(syncQueue),
            localization,
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(orderRepo),
            new CashPaymentWorkflowService(checkout, orderRepo, syncQueue),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), deviceRepo, fingerprint),
            new SpecialProductsWorkflowService(index, cart, catalog, specialProduct),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                index,
                cart,
                remoteLookupRefreshAsync,
                reloadCatalogAsync));

        var startupOptions = new AppStartupOptions([], false, null, null);
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        var initializeTask = viewModel.InitializeAsync(startupOptions);
        await WaitUntilAsync(() => catalog.LoadSellableItemsCallCount > 0);

        Assert.Equal(1, catalog.LoadSellableItemsCallCount);
        Assert.False(initializeTask.IsCompleted);
        Assert.NotSame(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.False(viewModel.IsPosTerminalScreenActive);
        Assert.Empty(index.FindExactMatches("1042", "9528502522381"));

        allowCatalogLoad.SetResult();
        await initializeTask;

        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.Same(viewModel.PosTerminal, viewModel.CachedPosTerminalScreen);
        Assert.Same(viewModel.CashPayment, viewModel.CachedCashPaymentScreen);
        Assert.Same(viewModel.SpecialProducts, viewModel.CachedSpecialProductsScreen);
        Assert.Contains(nameof(MainViewModel.CachedPosTerminalScreen), changedProperties);
        Assert.Contains(nameof(MainViewModel.CachedCashPaymentScreen), changedProperties);
        Assert.Contains(nameof(MainViewModel.CachedSpecialProductsScreen), changedProperties);
        Assert.True(viewModel.IsPosTerminalScreenActive);
        Assert.False(viewModel.IsCashPaymentScreenActive);
        Assert.False(viewModel.IsSpecialProductsScreenActive);
        Assert.False(viewModel.IsFallbackScreenActive);
        Assert.Equal("SKU-001", Assert.Single(index.FindExactMatches("1042", "9528502522381")).ProductCode);

        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.Equal(1, catalog.LoadSellableItemsCallCount);
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_WithAuthorizedDeviceAndConnectivityFailure_KeepsOfflinePosAvailable()
    {
        var authorizationState = new DeviceAuthorizationState();
        var connectivity = new FakeConnectivityApiClient
        {
            CheckOnlineException = new InvalidOperationException("API unavailable")
        };
        var priceIndex = new LocalSellableItemIndex();
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        var catalogRepo = new FakeCatalogRepository { Items = [CreateItem("1042", "SKU-001", "9528502522381")] };
        var specialProduct = new FakeSpecialProductService();
        var deviceRepo = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var fingerprint = new FakeDeviceFingerprintService();
        var orderRepo = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var localization = new LocalizationService();
        var viewModel = new MainViewModel(
            new PosCoreServices(priceIndex, cart, checkout, new FakeLocalSchemaService()),
            new PosInfrastructureFacade(connectivity, new FakeRawScannerService(), null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(localization, new FakeSettingsRepository()),
            new ShellCatalogService(priceIndex, catalogRepo, new FakeCatalogSyncService()),
            catalogRepo,
            new FakeRemoteLookupRefreshService(),
            specialProduct,
            new MainShellStartupService(deviceRepo, fingerprint, authorizationState),
            orderRepo,
            new ShellSyncCenterService(syncQueue),
            localization,
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(orderRepo),
            new CashPaymentWorkflowService(checkout, orderRepo, syncQueue),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), deviceRepo, fingerprint),
            new SpecialProductsWorkflowService(priceIndex, cart, catalogRepo, specialProduct),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                priceIndex,
                cart,
                remoteLookupRefreshAsync,
                reloadCatalogAsync));
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.True(viewModel.IsPosTerminalScreenActive);
        Assert.Equal("1042", viewModel.Session.StoreCode);
        Assert.Equal("POS-001", viewModel.Session.DeviceCode);
        Assert.False(viewModel.Session.IsOnline);
        Assert.NotNull(authorizationState.Current);
        Assert.Equal(1, connectivity.CheckOnlineCallCount);
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_WithCatalogSyncFailure_KeepsLocalCatalogAndPosScreen()
    {
        var index = new LocalSellableItemIndex();
        var catalogSync = new FakeCatalogSyncService
        {
            FullSyncException = new InvalidOperationException("catalog API unavailable")
        };
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        var catalogRepo = new FakeCatalogRepository { Items = [CreateItem("1042", "SKU-001", "9528502522381")] };
        var specialProduct = new FakeSpecialProductService();
        var deviceRepo = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var fingerprint = new FakeDeviceFingerprintService();
        var orderRepo = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var localization = new LocalizationService();
        var viewModel = new MainViewModel(
            new PosCoreServices(index, cart, checkout, new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(true), new FakeRawScannerService(), null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(localization, new FakeSettingsRepository()),
            new ShellCatalogService(index, catalogRepo, catalogSync),
            catalogRepo,
            new FakeRemoteLookupRefreshService(),
            specialProduct,
            new MainShellStartupService(deviceRepo, fingerprint, new DeviceAuthorizationState()),
            orderRepo,
            new ShellSyncCenterService(syncQueue),
            localization,
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(orderRepo),
            new CashPaymentWorkflowService(checkout, orderRepo, syncQueue),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), deviceRepo, fingerprint),
            new SpecialProductsWorkflowService(index, cart, catalogRepo, specialProduct),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                index,
                cart,
                remoteLookupRefreshAsync,
                reloadCatalogAsync));
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);
        await WaitUntilAsync(() => catalogSync.FullSyncCallCount > 0);

        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.True(viewModel.IsPosTerminalScreenActive);
        Assert.Equal("SKU-001", Assert.Single(index.FindExactMatches("1042", "9528502522381")).ProductCode);
        Assert.Equal(1, catalogSync.FullSyncCallCount);
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_WithEmptyLocalCatalog_RunsInitialSyncWithoutStartupTimeout()
    {
        var shellCatalog = new RecordingShellCatalogService
        {
            SyncItems = [CreateItem("1042", "SKU-DOWNLOADED", "9528502522381")]
        };
        var viewModel = CreateMainViewModelWithShellCatalog(
            new FakeCatalogRepository(),
            shellCatalog,
            new FakeConnectivityApiClient(true));
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);
        await WaitUntilAsync(() => shellCatalog.SyncCallCount > 0);

        Assert.Equal(1, shellCatalog.SyncCallCount);
        Assert.False(shellCatalog.LastSyncCancellationToken.CanBeCanceled);
        Assert.Equal("SKU-DOWNLOADED", Assert.Single(viewModel.PosTerminal!.Matches).ProductCode);
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_WithExistingLocalCatalog_RunsBackgroundRefreshWithoutStartupTimeout()
    {
        var shellCatalog = new RecordingShellCatalogService
        {
            SyncItems = [CreateItem("1042", "SKU-REFRESHED", "9528502522381")]
        };
        var viewModel = CreateMainViewModelWithShellCatalog(
            new FakeCatalogRepository { Items = [CreateItem("1042", "SKU-CACHED", "9528502522380")] },
            shellCatalog,
            new FakeConnectivityApiClient(true));
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);
        await WaitUntilAsync(() => shellCatalog.SyncCallCount > 0);

        Assert.Equal(1, shellCatalog.SyncCallCount);
        Assert.False(shellCatalog.LastSyncCancellationToken.CanBeCanceled);
    }

    [Fact]
    public async Task CatalogDownloadProgress_TransitionsFromComparingToDownloadingAndCompleted()
    {
        var syncRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var localItems = Enumerable.Range(1, 200)
            .Select(index => CreateItem("1042", $"SKU-{index:D3}", $"{1_000 + index}"))
            .ToArray();
        var shellCatalog = new RecordingShellCatalogService
        {
            SyncRelease = syncRelease,
            LocalItems = localItems
        };
        var viewModel = CreateMainViewModelWithShellCatalog(
            new FakeCatalogRepository { Items = localItems },
            shellCatalog,
            new FakeConnectivityApiClient(true),
            localItems);
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        var startupTask = viewModel.ContinueStartupAfterShownAsync(startupOptions);
        await WaitUntilAsync(() => shellCatalog.LastProgress is not null);

        shellCatalog.LastProgress!.Report(new CatalogSyncProgress(
            "1042",
            CatalogSyncProgressStage.Comparing,
            TotalCount: 200,
            DownloadedCount: 0,
            Percent: 0,
            ComparePages: 1,
            RemotePages: 0,
            UpsertedCount: 603,
            DeletedCount: 0,
            ElapsedMilliseconds: 85_000)
        {
            ComparedCount = 100
        });
        await WaitUntilAsync(() =>
            viewModel.CatalogDownloadProgressDetailText.Contains("Checked: 100", StringComparison.Ordinal));

        Assert.Equal(50, viewModel.CatalogDownloadProgressValue);
        Assert.Equal("Checking local data 50%", viewModel.CatalogDownloadProgressText);
        Assert.Contains("Checked: 100", viewModel.CatalogDownloadProgressDetailText, StringComparison.Ordinal);
        Assert.Contains("603", viewModel.CatalogDownloadProgressDetailText, StringComparison.Ordinal);

        shellCatalog.LastProgress.Report(new CatalogSyncProgress(
            "1042",
            CatalogSyncProgressStage.Comparing,
            TotalCount: 200,
            DownloadedCount: 0,
            Percent: 0,
            ComparePages: 2,
            RemotePages: 0,
            UpsertedCount: 603,
            DeletedCount: 0,
            ElapsedMilliseconds: 85_500)
        {
            ComparedCount = 199
        });
        await WaitUntilAsync(() =>
            viewModel.CatalogDownloadProgressDetailText.Contains("Checked: 199", StringComparison.Ordinal));

        Assert.Equal(99, viewModel.CatalogDownloadProgressValue);
        Assert.Equal("Checking local data 99%", viewModel.CatalogDownloadProgressText);

        shellCatalog.LastProgress.Report(new CatalogSyncProgress(
            "1042",
            CatalogSyncProgressStage.Downloading,
            TotalCount: 1_000,
            DownloadedCount: 250,
            Percent: 25,
            ComparePages: 1,
            RemotePages: 1,
            UpsertedCount: 603,
            DeletedCount: 0,
            ElapsedMilliseconds: 86_000)
        {
            ComparedCount = 4
        });
        await WaitUntilAsync(() =>
            viewModel.CatalogDownloadProgressDetailText.Contains("250/1000", StringComparison.Ordinal));

        Assert.Equal(25, viewModel.CatalogDownloadProgressValue);
        Assert.Equal("Data download 25%", viewModel.CatalogDownloadProgressText);
        Assert.Contains("250/1000", viewModel.CatalogDownloadProgressDetailText, StringComparison.Ordinal);

        shellCatalog.LastProgress.Report(new CatalogSyncProgress(
            "1042",
            CatalogSyncProgressStage.Completed,
            TotalCount: 1_000,
            DownloadedCount: 1_000,
            Percent: 100,
            ComparePages: 1,
            RemotePages: 2,
            UpsertedCount: 1_000,
            DeletedCount: 0,
            ElapsedMilliseconds: 87_000)
        {
            ComparedCount = 4
        });
        await WaitUntilAsync(() =>
            viewModel.CatalogDownloadProgressText == "Data download complete 100%");

        Assert.Equal(100, viewModel.CatalogDownloadProgressValue);
        Assert.Equal("Data download complete 100%", viewModel.CatalogDownloadProgressText);

        syncRelease.SetResult();
        await startupTask;
    }

    [Fact]
    public async Task CatalogDownloadProgress_WithEmptyLocalCatalog_KeepsLastValueOnFailure()
    {
        var syncRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shellCatalog = new RecordingShellCatalogService
        {
            SyncRelease = syncRelease
        };
        var viewModel = CreateMainViewModelWithShellCatalog(
            new FakeCatalogRepository(),
            shellCatalog,
            new FakeConnectivityApiClient(true));
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        var startupTask = viewModel.ContinueStartupAfterShownAsync(startupOptions);
        await WaitUntilAsync(() => shellCatalog.LastProgress is not null);

        shellCatalog.LastProgress!.Report(new CatalogSyncProgress(
            "1042",
            CatalogSyncProgressStage.Comparing,
            TotalCount: 0,
            DownloadedCount: 0,
            Percent: 0,
            ComparePages: 0,
            RemotePages: 0,
            UpsertedCount: 0,
            DeletedCount: 0,
            ElapsedMilliseconds: 1_000));
        await WaitUntilAsync(() => viewModel.CatalogDownloadProgressText.Contains("0%", StringComparison.Ordinal));

        Assert.Equal(0, viewModel.CatalogDownloadProgressValue);
        Assert.Equal("Checking local data 0%", viewModel.CatalogDownloadProgressText);

        shellCatalog.LastProgress.Report(new CatalogSyncProgress(
            "1042",
            CatalogSyncProgressStage.Downloading,
            TotalCount: 100,
            DownloadedCount: 40,
            Percent: 40,
            ComparePages: 0,
            RemotePages: 1,
            UpsertedCount: 40,
            DeletedCount: 0,
            ElapsedMilliseconds: 2_000));
        await WaitUntilAsync(() =>
            viewModel.CatalogDownloadProgressDetailText.Contains("40/100", StringComparison.Ordinal));

        shellCatalog.LastProgress.Report(new CatalogSyncProgress(
            "1042",
            CatalogSyncProgressStage.Failed,
            TotalCount: 100,
            DownloadedCount: 40,
            Percent: 0,
            ComparePages: 0,
            RemotePages: 1,
            UpsertedCount: 40,
            DeletedCount: 0,
            ElapsedMilliseconds: 3_000,
            ErrorMessage: "catalog API unavailable"));
        await WaitUntilAsync(() =>
            viewModel.CatalogDownloadProgressDetailText == "catalog API unavailable");

        Assert.Equal(40, viewModel.CatalogDownloadProgressValue);
        Assert.Equal("Data download failed", viewModel.CatalogDownloadProgressText);
        Assert.Equal("catalog API unavailable", viewModel.CatalogDownloadProgressDetailText);

        syncRelease.SetResult();
        await startupTask;
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_WithEmptyLocalCatalogSyncFailure_ShowsInitialDownloadFailure()
    {
        var shellCatalog = new RecordingShellCatalogService
        {
            SyncException = new InvalidOperationException("catalog API unavailable")
        };
        var viewModel = CreateMainViewModelWithShellCatalog(
            new FakeCatalogRepository(),
            shellCatalog,
            new FakeConnectivityApiClient(true));
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);
        await WaitUntilAsync(() => shellCatalog.SyncCallCount > 0 && viewModel.StatusMessage.Length > 0);

        Assert.Equal(1, shellCatalog.SyncCallCount);
        Assert.Contains("initial catalog", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("catalog API unavailable", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_WithRegistrationStoreLoadFailure_StaysOnRegistrationScreen()
    {
        var deviceApi = new FakeDeviceApiClient
        {
            GetStoresException = new InvalidOperationException("store API unavailable")
        };
        var priceIndex = new LocalSellableItemIndex();
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        var catalogRepo = new FakeCatalogRepository();
        var specialProduct = new FakeSpecialProductService();
        var deviceRepo = new FakeLocalDeviceRepository
        {
            Latest = new LocalDeviceCache(
                "POS-PENDING",
                "1042",
                "Main Branch",
                "HW-001",
                -1,
                false,
                "Pending approval",
                DateTimeOffset.UtcNow)
        };
        var fingerprint = new FakeDeviceFingerprintService();
        var orderRepo = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var localization = new LocalizationService();
        var viewModel = new MainViewModel(
            new PosCoreServices(priceIndex, cart, checkout, new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), new FakeRawScannerService(), null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(localization, new FakeSettingsRepository()),
            new ShellCatalogService(priceIndex, catalogRepo, new FakeCatalogSyncService()),
            catalogRepo,
            new FakeRemoteLookupRefreshService(),
            specialProduct,
            new MainShellStartupService(deviceRepo, fingerprint, new DeviceAuthorizationState()),
            orderRepo,
            new ShellSyncCenterService(syncQueue),
            localization,
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(orderRepo),
            new CashPaymentWorkflowService(checkout, orderRepo, syncQueue),
            new DeviceRegistrationWorkflowService(deviceApi, deviceRepo, fingerprint),
            new SpecialProductsWorkflowService(priceIndex, cart, catalogRepo, specialProduct),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                priceIndex,
                cart,
                remoteLookupRefreshAsync,
                reloadCatalogAsync));
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.NotNull(viewModel.DeviceRegistration);
        Assert.Same(viewModel.DeviceRegistration, viewModel.CurrentScreen);
        Assert.Equal("store API unavailable", viewModel.DeviceRegistration.StatusMessage);
        Assert.Equal(1, deviceApi.GetStoresCallCount);
        Assert.Null(viewModel.PosTerminal);
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_AfterReinitialize_LoadsStoresForCurrentRegistrationScreen()
    {
        var deviceApi = new FakeDeviceApiClient
        {
            Stores = [new StoreSelectionItem("1042", "Main Branch", true)]
        };
        var priceIndex = new LocalSellableItemIndex();
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        var catalogRepo = new FakeCatalogRepository();
        var specialProduct = new FakeSpecialProductService();
        var deviceRepo = new FakeLocalDeviceRepository
        {
            Latest = new LocalDeviceCache(
                "POS-PENDING",
                "1042",
                "Main Branch",
                "HW-001",
                -1,
                false,
                "Pending approval",
                DateTimeOffset.UtcNow)
        };
        var fingerprint = new FakeDeviceFingerprintService();
        var orderRepo = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var localization = new LocalizationService();
        var viewModel = new MainViewModel(
            new PosCoreServices(priceIndex, cart, checkout, new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), new FakeRawScannerService(), null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(localization, new FakeSettingsRepository()),
            new ShellCatalogService(priceIndex, catalogRepo, new FakeCatalogSyncService()),
            catalogRepo,
            new FakeRemoteLookupRefreshService(),
            specialProduct,
            new MainShellStartupService(deviceRepo, fingerprint, new DeviceAuthorizationState()),
            orderRepo,
            new ShellSyncCenterService(syncQueue),
            localization,
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(orderRepo),
            new CashPaymentWorkflowService(checkout, orderRepo, syncQueue),
            new DeviceRegistrationWorkflowService(deviceApi, deviceRepo, fingerprint),
            new SpecialProductsWorkflowService(priceIndex, cart, catalogRepo, specialProduct),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                priceIndex,
                cart,
                remoteLookupRefreshAsync,
                reloadCatalogAsync));
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        var firstRegistration = Assert.IsType<DeviceRegistrationViewModel>(viewModel.DeviceRegistration);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);
        Assert.Single(firstRegistration.Stores);

        await viewModel.InitializeAsync(startupOptions);
        var currentRegistration = Assert.IsType<DeviceRegistrationViewModel>(viewModel.DeviceRegistration);
        Assert.NotSame(firstRegistration, currentRegistration);

        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.Equal(2, deviceApi.GetStoresCallCount);
        Assert.Single(currentRegistration.Stores);
    }

    [Fact]
    public async Task InitializeAsync_LoadsSpecialProductsDataBeforeNavigatingToPos()
    {
        var catalog = new FakeCatalogRepository
        {
            SpecialItems = [CreateItem("1042", "SKU-SP", "9528502522399")],
            BeforeLoadSpecialProductItemsAsync = async () => await Task.Delay(25)
        };
        var priceIndex = new LocalSellableItemIndex();
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        var specialProduct = new FakeSpecialProductService();
        var deviceRepo = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var fingerprint = new FakeDeviceFingerprintService();
        var orderRepo = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var localization = new LocalizationService();
        var viewModel = new MainViewModel(
            new PosCoreServices(priceIndex, cart, checkout, new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), new FakeRawScannerService(), null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(localization, new FakeSettingsRepository()),
            new ShellCatalogService(priceIndex, catalog, new FakeCatalogSyncService()),
            catalog,
            new FakeRemoteLookupRefreshService(),
            specialProduct,
            new MainShellStartupService(deviceRepo, fingerprint, new DeviceAuthorizationState()),
            orderRepo,
            new ShellSyncCenterService(syncQueue),
            localization,
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(orderRepo),
            new CashPaymentWorkflowService(checkout, orderRepo, syncQueue),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), deviceRepo, fingerprint),
            new SpecialProductsWorkflowService(priceIndex, cart, catalog, specialProduct),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                priceIndex,
                cart,
                remoteLookupRefreshAsync,
                reloadCatalogAsync));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.Equal(1, catalog.LoadSpecialProductItemsCallCount);
        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.Same(viewModel.SpecialProducts, viewModel.CachedSpecialProductsScreen);
        Assert.True(viewModel.IsPosTerminalScreenActive);
        Assert.False(viewModel.IsSpecialProductsScreenActive);
        Assert.False(viewModel.IsFallbackScreenActive);
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_DoesNotWarmSpecialProductThumbnailsInBackground()
    {
        ClearImageCacheForTests();
        var imageBaseUrl = $"https://images.example/{Guid.NewGuid():N}";
        var catalog = new FakeCatalogRepository
        {
            SpecialItems = Enumerable.Range(1, 21)
                .Select(number => CreateSpecialItem(
                    "1042",
                    $"SKU-{number:000}",
                    $"9528502522{number:000}",
                    imageBaseUrl))
                .ToArray()
        };
        var expectedFirstPageImages = catalog.SpecialItems
            .Take(20)
            .Select(item => item.ProductImage!)
            .ToArray();
        var loadedImages = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var priceIndex = new LocalSellableItemIndex();
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        var specialProduct = new FakeSpecialProductService();
        var deviceRepo = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var fingerprint = new FakeDeviceFingerprintService();
        var orderRepo = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var localization = new LocalizationService();
        var viewModel = new MainViewModel(
            new PosCoreServices(priceIndex, cart, checkout, new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), new FakeRawScannerService(), null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(localization, new FakeSettingsRepository()),
            new ShellCatalogService(priceIndex, catalog, new FakeCatalogSyncService()),
            catalog,
            new FakeRemoteLookupRefreshService(),
            specialProduct,
            new MainShellStartupService(deviceRepo, fingerprint, new DeviceAuthorizationState()),
            orderRepo,
            new ShellSyncCenterService(syncQueue),
            localization,
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(orderRepo),
            new CashPaymentWorkflowService(checkout, orderRepo, syncQueue),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), deviceRepo, fingerprint),
            new SpecialProductsWorkflowService(priceIndex, cart, catalog, specialProduct),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                priceIndex,
                cart,
                remoteLookupRefreshAsync,
                reloadCatalogAsync));
        var converter = new ProductThumbnailImageSourceConverter();
        using var remoteImages = ProductThumbnailImageSourceConverter.UseRemoteImageBytesLoaderForTests((uri, _) =>
        {
            loadedImages.AddOrUpdate(uri.AbsoluteUri, 1, (_, count) => count + 1);
            return Task.FromResult(OnePixelPngBytes());
        });

        var startupOptions = new AppStartupOptions([], false, null, null);
        await viewModel.InitializeAsync(startupOptions);
        Assert.Empty(loadedImages);

        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.Same(viewModel.SpecialProducts, viewModel.CachedSpecialProductsScreen);
        Assert.True(viewModel.IsPosTerminalScreenActive);
        Assert.Empty(loadedImages);
        Assert.DoesNotContain(expectedFirstPageImages, ImageCacheContainsForTests);

        await viewModel.PosTerminal!.OpenSpecialProductsCommand.ExecuteAsync(null);
        await viewModel.SpecialProducts!.EnsureLoadedAsync();

        var firstPageItem = viewModel.SpecialProducts.PagedSpecialItems.First();
        Assert.IsType<BitmapImage>(
            converter.Convert(firstPageItem.ProductImage, typeof(BitmapSource), null, CultureInfo.InvariantCulture));
        Assert.Equal(1, loadedImages[firstPageItem.ProductImage!]);
    }

    [Fact]
    public async Task OpenSpecialProductsCommand_SwitchesScreenWithoutWaitingForLocalLoad()
    {
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new FakeCatalogRepository
        {
            SpecialItems = [CreateItem("1042", "SKU-SP", "9528502522399")],
            BeforeLoadSpecialProductItemsAsync = () => releaseLoad.Task
        };
        var viewModel = new MainViewModel(
            new PosCoreServices(new LocalSellableItemIndex(), new PosCartService(), new CashCheckoutService(), new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), new FakeRawScannerService(), null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(new LocalizationService(), new FakeSettingsRepository()),
            new ShellCatalogService(new LocalSellableItemIndex(), catalog, new FakeCatalogSyncService()),
            catalog,
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new MainShellStartupService(new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService(), new DeviceAuthorizationState()),
            new FakeLocalOrderRepository(),
            new ShellSyncCenterService(new FakeSyncQueueRepository()),
            new LocalizationService(),
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(new FakeLocalOrderRepository()),
            new CashPaymentWorkflowService(new CashCheckoutService(), new FakeLocalOrderRepository(), new FakeSyncQueueRepository()),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService()),
            new SpecialProductsWorkflowService(new LocalSellableItemIndex(), new PosCartService(), catalog, new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                new LocalSellableItemIndex(),
                new PosCartService(),
                remoteLookupRefreshAsync,
                reloadCatalogAsync));

        await viewModel.InitializeAsync(new AppStartupOptions([], true, null, null));

        var openTask = viewModel.PosTerminal!.OpenSpecialProductsCommand.ExecuteAsync(null);

        Assert.Same(viewModel.SpecialProducts, viewModel.CurrentScreen);
        Assert.Same(viewModel.SpecialProducts, viewModel.CachedSpecialProductsScreen);
        Assert.False(viewModel.IsPosTerminalScreenActive);
        Assert.True(viewModel.IsSpecialProductsScreenActive);
        Assert.False(viewModel.IsFallbackScreenActive);
        Assert.True(openTask.IsCompleted);
        Assert.Equal(1, catalog.LoadSpecialProductItemsCallCount);

        releaseLoad.SetResult();
        await viewModel.SpecialProducts!.EnsureLoadedAsync();

        Assert.Single(viewModel.SpecialProducts.PagedSpecialItems);
        Assert.Equal(1, catalog.LoadSpecialProductItemsCallCount);
    }

    [Fact]
    public async Task OpenSpecialProductsCommand_reuses_prepared_cached_screen_and_activates_special_host()
    {
        var catalog = new FakeCatalogRepository
        {
            SpecialItems = [CreateItem("1042", "SKU-SP", "9528502522399")]
        };
        var viewModel = new MainViewModel(
            new PosCoreServices(new LocalSellableItemIndex(), new PosCartService(), new CashCheckoutService(), new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), new FakeRawScannerService(), null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(new LocalizationService(), new FakeSettingsRepository()),
            new ShellCatalogService(new LocalSellableItemIndex(), catalog, new FakeCatalogSyncService()),
            catalog,
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new MainShellStartupService(new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService(), new DeviceAuthorizationState()),
            new FakeLocalOrderRepository(),
            new ShellSyncCenterService(new FakeSyncQueueRepository()),
            new LocalizationService(),
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(new FakeLocalOrderRepository()),
            new CashPaymentWorkflowService(new CashCheckoutService(), new FakeLocalOrderRepository(), new FakeSyncQueueRepository()),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService()),
            new SpecialProductsWorkflowService(new LocalSellableItemIndex(), new PosCartService(), catalog, new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                new LocalSellableItemIndex(),
                new PosCartService(),
                remoteLookupRefreshAsync,
                reloadCatalogAsync));
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);
        await WaitUntilAsync(() => viewModel.CachedSpecialProductsScreen is not null);
        var cachedSpecialProducts = viewModel.CachedSpecialProductsScreen;

        await viewModel.PosTerminal!.OpenSpecialProductsCommand.ExecuteAsync(null);

        Assert.Same(cachedSpecialProducts, viewModel.CurrentScreen);
        Assert.Same(cachedSpecialProducts, viewModel.SpecialProducts);
        Assert.False(viewModel.IsPosTerminalScreenActive);
        Assert.True(viewModel.IsSpecialProductsScreenActive);
        Assert.False(viewModel.IsFallbackScreenActive);
    }

    [Fact]
    public async Task BackFromSpecialProducts_keeps_special_host_cached_and_returns_to_pos()
    {
        var priceIndex = new LocalSellableItemIndex();
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        var catalogRepo = new FakeCatalogRepository
        {
            SpecialItems = [CreateItem("1042", "SKU-SP", "9528502522399")]
        };
        var specialProduct = new FakeSpecialProductService();
        var deviceRepo = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var fingerprint = new FakeDeviceFingerprintService();
        var orderRepo = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var localization = new LocalizationService();
        var viewModel = new MainViewModel(
            new PosCoreServices(priceIndex, cart, checkout, new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), new FakeRawScannerService(), null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(localization, new FakeSettingsRepository()),
            new ShellCatalogService(priceIndex, catalogRepo, new FakeCatalogSyncService()),
            catalogRepo,
            new FakeRemoteLookupRefreshService(),
            specialProduct,
            new MainShellStartupService(deviceRepo, fingerprint, new DeviceAuthorizationState()),
            orderRepo,
            new ShellSyncCenterService(syncQueue),
            localization,
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(orderRepo),
            new CashPaymentWorkflowService(checkout, orderRepo, syncQueue),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), deviceRepo, fingerprint),
            new SpecialProductsWorkflowService(priceIndex, cart, catalogRepo, specialProduct),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                priceIndex,
                cart,
                remoteLookupRefreshAsync,
                reloadCatalogAsync));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.PosTerminal!.OpenSpecialProductsCommand.ExecuteAsync(null);
        var cachedSpecialProducts = viewModel.CachedSpecialProductsScreen;

        viewModel.SpecialProducts!.BackCommand.Execute(null);

        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.Same(cachedSpecialProducts, viewModel.CachedSpecialProductsScreen);
        Assert.True(viewModel.IsPosTerminalScreenActive);
        Assert.False(viewModel.IsSpecialProductsScreenActive);
        Assert.False(viewModel.IsFallbackScreenActive);
    }

    [Fact]
    public async Task OpenReturnsCommand_SwitchesToReceiptReturnsScreen()
    {
        var scanner = new FakeRawScannerService();
        var viewModel = new MainViewModel(
            new PosCoreServices(new LocalSellableItemIndex(), new PosCartService(), new CashCheckoutService(), new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), scanner, null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(new LocalizationService(), new FakeSettingsRepository()),
            new ShellCatalogService(new LocalSellableItemIndex(), new FakeCatalogRepository(), new FakeCatalogSyncService()),
            new FakeCatalogRepository(),
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new MainShellStartupService(new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService(), new DeviceAuthorizationState()),
            new FakeLocalOrderRepository(),
            new ShellSyncCenterService(new FakeSyncQueueRepository()),
            new LocalizationService(),
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(new FakeLocalOrderRepository()),
            new CashPaymentWorkflowService(new CashCheckoutService(), new FakeLocalOrderRepository(), new FakeSyncQueueRepository()),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService()),
            new SpecialProductsWorkflowService(new LocalSellableItemIndex(), new PosCartService(), new FakeCatalogRepository(), new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                new LocalSellableItemIndex(),
                new PosCartService(),
                remoteLookupRefreshAsync,
                reloadCatalogAsync));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.True(viewModel.PosTerminal!.OpenReturnsCommand.CanExecute(null));

        viewModel.PosTerminal.OpenReturnsCommand.Execute(null);

        Assert.Same(viewModel.ReceiptReturns, viewModel.CurrentScreen);
        Assert.False(viewModel.IsPosTerminalScreenActive);
        Assert.False(viewModel.IsSpecialProductsScreenActive);
        Assert.True(viewModel.IsFallbackScreenActive);
        Assert.Equal(ReceiptReturnsViewModel.PageId, scanner.ActivePageId);
    }

    [Fact]
    public async Task LeavingReceiptReturnsScreen_resets_unconfirmed_return_state()
    {
        var scanner = new FakeRawScannerService();
        var index = new LocalSellableItemIndex();
        var catalog = new FakeCatalogRepository
        {
            Items = [CreateItem("S001", "SKU-RETURN", "690RET")]
        };
        var viewModel = new MainViewModel(
            new PosCoreServices(index, new PosCartService(), new CashCheckoutService(), new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), scanner, null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(new LocalizationService(), new FakeSettingsRepository()),
            new ShellCatalogService(index, catalog, new FakeCatalogSyncService()),
            catalog,
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new MainShellStartupService(new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("S001") }, new FakeDeviceFingerprintService(), new DeviceAuthorizationState()),
            new FakeLocalOrderRepository(),
            new ShellSyncCenterService(new FakeSyncQueueRepository()),
            new LocalizationService(),
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(new FakeLocalOrderRepository()),
            new CashPaymentWorkflowService(new CashCheckoutService(), new FakeLocalOrderRepository(), new FakeSyncQueueRepository()),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("S001") }, new FakeDeviceFingerprintService()),
            new SpecialProductsWorkflowService(index, new PosCartService(), catalog, new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                index,
                new PosCartService(),
                remoteLookupRefreshAsync,
                reloadCatalogAsync));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await WaitUntilAsync(() => index.FindExactMatches("S001", "690RET").Count == 1);
        viewModel.PosTerminal!.OpenReturnsCommand.Execute(null);
        var returns = viewModel.ReceiptReturns!;
        returns.IsNoReceiptMode = true;
        returns.ScanText = "690RET";
        await returns.LookupCommand.ExecuteAsync(null);
        Assert.Single(returns.PendingLines);

        viewModel.ShowPosCommand.Execute(null);

        Assert.Empty(returns.ScanText);
        Assert.False(returns.IsNoReceiptMode);
        Assert.Empty(returns.PendingLines);
        Assert.Empty(returns.OrderLines);
        Assert.False(returns.ReturnRecordsMayBeStale);
        Assert.Equal("No order loaded", returns.OrderSummaryText);
    }

    [Fact]
    public async Task KeyboardScannerInput_FromSpecialProductsNormalModeIsConsumedWithoutAddingCart()
    {
        var index = new LocalSellableItemIndex();
        var catalog = new FakeCatalogRepository
        {
            Items = [CreateItem("1042", "SKU-001", "319844731768")],
            SpecialItems = [CreateItem("1042", "SKU-SP", "9528502522399")]
        };
        var viewModel = new MainViewModel(
            new PosCoreServices(index, new PosCartService(), new CashCheckoutService(), new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), new FakeRawScannerService(), null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(new LocalizationService(), new FakeSettingsRepository()),
            new ShellCatalogService(index, catalog, new FakeCatalogSyncService()),
            catalog,
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new MainShellStartupService(new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService(), new DeviceAuthorizationState()),
            new FakeLocalOrderRepository(),
            new ShellSyncCenterService(new FakeSyncQueueRepository()),
            new LocalizationService(),
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(new FakeLocalOrderRepository()),
            new CashPaymentWorkflowService(new CashCheckoutService(), new FakeLocalOrderRepository(), new FakeSyncQueueRepository()),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService()),
            new SpecialProductsWorkflowService(index, new PosCartService(), catalog, new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                index,
                new PosCartService(),
                remoteLookupRefreshAsync,
                reloadCatalogAsync));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.PosTerminal!.OpenSpecialProductsCommand.ExecuteAsync(null);

        var processed = viewModel.TryProcessKeyboardScannerInput("319844731768");

        Assert.True(processed);
        Assert.Same(viewModel.SpecialProducts, viewModel.CurrentScreen);
        Assert.Empty(viewModel.PosTerminal!.CartLines);
        Assert.Empty(viewModel.SpecialProducts!.SearchResults);
        Assert.Contains("edit", viewModel.SpecialProducts.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task KeyboardScannerInput_FromSpecialProductsEditModeSearchesCandidatesWithoutAddingCart()
    {
        var index = new LocalSellableItemIndex();
        var catalog = new FakeCatalogRepository
        {
            Items = [CreateItem("1042", "SKU-001", "319844731768")],
            SpecialItems = [CreateItem("1042", "SKU-SP", "9528502522399")]
        };
        var viewModel = new MainViewModel(
            new PosCoreServices(index, new PosCartService(), new CashCheckoutService(), new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), new FakeRawScannerService(), null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(new LocalizationService(), new FakeSettingsRepository()),
            new ShellCatalogService(index, catalog, new FakeCatalogSyncService()),
            catalog,
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new MainShellStartupService(new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService(), new DeviceAuthorizationState()),
            new FakeLocalOrderRepository(),
            new ShellSyncCenterService(new FakeSyncQueueRepository()),
            new LocalizationService(),
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(new FakeLocalOrderRepository()),
            new CashPaymentWorkflowService(new CashCheckoutService(), new FakeLocalOrderRepository(), new FakeSyncQueueRepository()),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService()),
            new SpecialProductsWorkflowService(index, new PosCartService(), catalog, new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                index,
                new PosCartService(),
                remoteLookupRefreshAsync,
                reloadCatalogAsync));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.PosTerminal!.OpenSpecialProductsCommand.ExecuteAsync(null);
        viewModel.SpecialProducts!.ToggleEditModeCommand.Execute(null);

        var processed = viewModel.TryProcessKeyboardScannerInput("319844731768");

        Assert.True(processed);
        Assert.Same(viewModel.SpecialProducts, viewModel.CurrentScreen);
        Assert.Empty(viewModel.PosTerminal.CartLines);
        Assert.Equal("319844731768", viewModel.SpecialProducts.SearchText);
        await WaitUntilAsync(() => viewModel.SpecialProducts.SearchResults.Count == 1);
        var candidate = Assert.Single(viewModel.SpecialProducts.SearchResults);
        Assert.Equal("SKU-001", candidate.ProductCode);
        Assert.Same(candidate, viewModel.SpecialProducts.SelectedSearchResult);
    }

    [Fact]
    public async Task OpenSpecialProductsCommand_ActivatesSpecialProductsScannerPage()
    {
        var scanner = new FakeRawScannerService();
        var viewModel = new MainViewModel(
            new PosCoreServices(new LocalSellableItemIndex(), new PosCartService(), new CashCheckoutService(), new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), scanner, null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(new LocalizationService(), new FakeSettingsRepository()),
            new ShellCatalogService(new LocalSellableItemIndex(), new FakeCatalogRepository
            {
                SpecialItems = [CreateItem("1042", "SKU-SP", "9528502522399")]
            }, new FakeCatalogSyncService()),
            new FakeCatalogRepository
            {
                SpecialItems = [CreateItem("1042", "SKU-SP", "9528502522399")]
            },
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new MainShellStartupService(new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService(), new DeviceAuthorizationState()),
            new FakeLocalOrderRepository(),
            new ShellSyncCenterService(new FakeSyncQueueRepository()),
            new LocalizationService(),
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(new FakeLocalOrderRepository()),
            new CashPaymentWorkflowService(new CashCheckoutService(), new FakeLocalOrderRepository(), new FakeSyncQueueRepository()),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService()),
            new SpecialProductsWorkflowService(new LocalSellableItemIndex(), new PosCartService(), new FakeCatalogRepository
            {
                SpecialItems = [CreateItem("1042", "SKU-SP", "9528502522399")]
            }, new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                new LocalSellableItemIndex(),
                new PosCartService(),
                remoteLookupRefreshAsync,
                reloadCatalogAsync));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.Equal(PosTerminalViewModel.PageId, scanner.ActivePageId);

        await viewModel.PosTerminal!.OpenSpecialProductsCommand.ExecuteAsync(null);

        Assert.Equal(SpecialProductsViewModel.PageId, scanner.ActivePageId);

        viewModel.SpecialProducts!.BackCommand.Execute(null);

        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.Equal(PosTerminalViewModel.PageId, scanner.ActivePageId);
    }

    [Fact]
    public async Task ScannerActivePage_IsClearedForScreensWithoutTarget_and_activates_history_target()
    {
        var scanner = new FakeRawScannerService();
        var index = new LocalSellableItemIndex();
        var catalog = new FakeCatalogRepository
        {
            Items = [CreateItem("1042", "SKU-001", "930110")]
        };
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        var orderRepository = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var viewModel = new MainViewModel(
            new PosCoreServices(index, cart, checkout, new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), scanner, null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(new LocalizationService(), new FakeSettingsRepository()),
            new ShellCatalogService(index, catalog, new FakeCatalogSyncService()),
            catalog,
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new MainShellStartupService(new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService(), new DeviceAuthorizationState()),
            orderRepository,
            new ShellSyncCenterService(syncQueue),
            new LocalizationService(),
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(orderRepository),
            new CashPaymentWorkflowService(checkout, orderRepository, syncQueue),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService()),
            new SpecialProductsWorkflowService(index, cart, catalog, new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                index,
                cart,
                remoteLookupRefreshAsync,
                reloadCatalogAsync));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.Equal(PosTerminalViewModel.PageId, scanner.ActivePageId);

        await WaitUntilAsync(() => index.FindExactMatches("1042", "930110").Count == 1);
        scanner.Emit("930110");
        viewModel.ShowCashPaymentCommand.Execute(null);

        Assert.Same(viewModel.CashPayment, viewModel.CurrentScreen);
        Assert.False(viewModel.IsPosTerminalScreenActive);
        Assert.True(viewModel.IsCashPaymentScreenActive);
        Assert.False(viewModel.IsSpecialProductsScreenActive);
        Assert.False(viewModel.IsFallbackScreenActive);
        Assert.Null(scanner.ActivePageId);

        await viewModel.ShowPaymentSuccessCommand.ExecuteAsync(null);

        // 成功结果以卡片留在收银主页，扫码枪仍交给收银页处理，可直接扫下一单。
        Assert.True(IsShowingCompletedSale(viewModel));
        Assert.Equal(PosTerminalViewModel.PageId, scanner.ActivePageId);

        await viewModel.ShowHistoryCommand.ExecuteAsync(null);

        Assert.Same(viewModel.TransactionHistory, viewModel.CurrentScreen);
        Assert.Equal(TransactionHistoryViewModel.PageId, scanner.ActivePageId);

        var history = Assert.IsType<TransactionHistoryViewModel>(viewModel.TransactionHistory);
        var queryCountBeforeScan = orderRepository.FilteredQueryCallCount;
        var handled = viewModel.TryProcessKeyboardScannerInput("  HISTORY-930110  ");
        var scanLoadTask = history.LoadCommand.ExecutionTask;
        Assert.NotNull(scanLoadTask);
        await scanLoadTask!;

        Assert.True(handled);
        Assert.Equal("HISTORY-930110", history.SearchText);
        Assert.Equal("HISTORY-930110", orderRepository.LastFilteredQuery?.Keyword);
        Assert.Equal(queryCountBeforeScan + 1, orderRepository.FilteredQueryCallCount);
    }

    [Fact]
    public async Task ScannerActivePage_UsesDeviceActivationPageOnRegistrationScreen()
    {
        var scanner = new FakeRawScannerService();
        var viewModel = new MainViewModel(
            new PosCoreServices(new LocalSellableItemIndex(), new PosCartService(), new CashCheckoutService(), new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), scanner, null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(new LocalizationService(), new FakeSettingsRepository()),
            new ShellCatalogService(new LocalSellableItemIndex(), new FakeCatalogRepository(), new FakeCatalogSyncService()),
            new FakeCatalogRepository(),
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new MainShellStartupService(new FakeLocalDeviceRepository(), new FakeDeviceFingerprintService(), new DeviceAuthorizationState()),
            new FakeLocalOrderRepository(),
            new ShellSyncCenterService(new FakeSyncQueueRepository()),
            new LocalizationService(),
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(new FakeLocalOrderRepository()),
            new CashPaymentWorkflowService(new CashCheckoutService(), new FakeLocalOrderRepository(), new FakeSyncQueueRepository()),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), new FakeLocalDeviceRepository(), new FakeDeviceFingerprintService()),
            new SpecialProductsWorkflowService(new LocalSellableItemIndex(), new PosCartService(), new FakeCatalogRepository(), new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                new LocalSellableItemIndex(),
                new PosCartService(),
                remoteLookupRefreshAsync,
                reloadCatalogAsync));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.Same(viewModel.DeviceRegistration, viewModel.CurrentScreen);
        Assert.Equal(DeviceRegistrationViewModel.PageId, scanner.ActivePageId);
    }

    [Fact]
    public async Task RawScannerInput_OnNonScannerScreenIsIgnoredWithoutChangingCartOrScreen()
    {
        var scanner = new FakeRawScannerService();
        var index = new LocalSellableItemIndex();
        var catalog = new FakeCatalogRepository
        {
            Items =
            [
                CreateItem("1042", "SKU-001", "930110"),
                CreateItem("1042", "SKU-002", "930111")
            ]
        };
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        var orderRepository = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var viewModel = new MainViewModel(
            new PosCoreServices(index, cart, checkout, new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), scanner, null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(new LocalizationService(), new FakeSettingsRepository()),
            new ShellCatalogService(index, catalog, new FakeCatalogSyncService()),
            catalog,
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new MainShellStartupService(new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService(), new DeviceAuthorizationState()),
            orderRepository,
            new ShellSyncCenterService(syncQueue),
            new LocalizationService(),
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(orderRepository),
            new CashPaymentWorkflowService(checkout, orderRepository, syncQueue),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService()),
            new SpecialProductsWorkflowService(index, cart, catalog, new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                index,
                cart,
                remoteLookupRefreshAsync,
                reloadCatalogAsync));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await WaitUntilAsync(() => index.FindExactMatches("1042", "930110").Count == 1);
        scanner.Emit("930110");
        viewModel.ShowCashPaymentCommand.Execute(null);
        var screen = viewModel.CurrentScreen;
        var status = viewModel.StatusMessage;
        var line = Assert.Single(viewModel.PosTerminal!.CartLines);

        scanner.Emit("930111");

        Assert.Same(screen, viewModel.CurrentScreen);
        Assert.Null(scanner.ActivePageId);
        Assert.Equal(status, viewModel.StatusMessage);
        Assert.Same(line, Assert.Single(viewModel.PosTerminal.CartLines));
        Assert.Equal(1m, line.Quantity);
    }

    [Fact]
    public async Task CashPaymentScreen_IsCachedAndResetEachTimeItIsOpened()
    {
        var scanner = new FakeRawScannerService();
        var index = new LocalSellableItemIndex();
        var catalog = new FakeCatalogRepository
        {
            Items = [CreateItem("1042", "SKU-001", "930110")]
        };
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        var orderRepository = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var viewModel = new MainViewModel(
            new PosCoreServices(index, cart, checkout, new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), scanner, null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(new LocalizationService(), new FakeSettingsRepository()),
            new ShellCatalogService(index, catalog, new FakeCatalogSyncService()),
            catalog,
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new MainShellStartupService(new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService(), new DeviceAuthorizationState()),
            orderRepository,
            new ShellSyncCenterService(syncQueue),
            new LocalizationService(),
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(orderRepository),
            new CashPaymentWorkflowService(checkout, orderRepository, syncQueue),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService()),
            new SpecialProductsWorkflowService(index, cart, catalog, new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                index,
                cart,
                remoteLookupRefreshAsync,
                reloadCatalogAsync));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await WaitUntilAsync(() => index.FindExactMatches("1042", "930110").Count == 1);
        scanner.Emit("930110");

        viewModel.ShowCashPaymentCommand.Execute(null);
        var firstPaymentScreen = viewModel.CashPayment!;
        firstPaymentScreen.TenderAmountText = "5";
        await firstPaymentScreen.SelectCashCommand.ExecuteAsync(null);

        Assert.Same(firstPaymentScreen, viewModel.CurrentScreen);
        Assert.Same(firstPaymentScreen, viewModel.CachedCashPaymentScreen);
        Assert.Single(firstPaymentScreen.PaymentTenders);

        viewModel.ShowPosCommand.Execute(null);

        Assert.Same(firstPaymentScreen, viewModel.CachedCashPaymentScreen);
        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);

        viewModel.ShowCashPaymentCommand.Execute(null);

        Assert.Same(firstPaymentScreen, viewModel.CashPayment);
        Assert.Same(firstPaymentScreen, viewModel.CurrentScreen);
        Assert.True(viewModel.IsCashPaymentScreenActive);
        Assert.False(viewModel.IsFallbackScreenActive);
        Assert.Empty(firstPaymentScreen.PaymentTenders);
        Assert.True(firstPaymentScreen.IsCashSelected);
        Assert.Equal(string.Empty, firstPaymentScreen.TenderAmountText);
        Assert.Null(scanner.ActivePageId);

        firstPaymentScreen.BackToPosCommand.Execute(null);

        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
    }

    [Fact]
    public async Task BeginDeviceReregistration_ClearsCachedCashPaymentScreen()
    {
        var scanner = new FakeRawScannerService();
        var viewModel = new MainViewModel(
            new PosCoreServices(new LocalSellableItemIndex(), new PosCartService(), new CashCheckoutService(), new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), scanner, null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(new LocalizationService(), new FakeSettingsRepository()),
            new ShellCatalogService(new LocalSellableItemIndex(), new FakeCatalogRepository(), new FakeCatalogSyncService()),
            new FakeCatalogRepository(),
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new MainShellStartupService(new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService(), new DeviceAuthorizationState()),
            new FakeLocalOrderRepository(),
            new ShellSyncCenterService(new FakeSyncQueueRepository()),
            new LocalizationService(),
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(new FakeLocalOrderRepository()),
            new CashPaymentWorkflowService(new CashCheckoutService(), new FakeLocalOrderRepository(), new FakeSyncQueueRepository()),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService()),
            new SpecialProductsWorkflowService(new LocalSellableItemIndex(), new PosCartService(), new FakeCatalogRepository(), new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                new LocalSellableItemIndex(),
                new PosCartService(),
                remoteLookupRefreshAsync,
                reloadCatalogAsync));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.NotNull(viewModel.CachedCashPaymentScreen);

        await InvokePrivateTaskAsync(viewModel, "BeginDeviceReregistrationAsync");

        Assert.Null(viewModel.CachedCashPaymentScreen);
        Assert.Null(viewModel.CashPayment);
        Assert.True(viewModel.IsDeviceReregistrationDialogOpen);
        Assert.NotNull(viewModel.DeviceRegistration);
        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.False(viewModel.IsCashPaymentScreenActive);

        scanner.Emit(DeviceActivationCode.Insert(5, "\r \v"));
        await WaitUntilAsync(() =>
            string.Equals(
                viewModel.DeviceRegistration?.ActivationCode,
                DeviceActivationCode,
                StringComparison.Ordinal));

        Assert.Empty(scanner.InterceptedBarcodes);
    }

    [Fact]
    public async Task Settings_ReregisterDeviceCommand_OpensActivationDialogWithoutLoadingStores()
    {
        var deviceApi = new FakeDeviceApiClient
        {
            Stores =
            [
                new StoreSelectionItem("1042", "Old Store", true),
                new StoreSelectionItem("2042", "New Store", true)
            ]
        };
        var viewModel = CreateAuthorizedMainViewModelWithSettings(deviceApiClient: deviceApi);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.ShowSettingsCommand.ExecuteAsync(null);
        var settings = Assert.IsType<SettingsViewModel>(viewModel.CurrentScreen);

        await settings.ReregisterDeviceCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsDeviceReregistrationDialogOpen);
        Assert.NotNull(viewModel.DeviceRegistration);
        Assert.Same(settings, viewModel.CurrentScreen);
        Assert.Equal(0, deviceApi.GetStoresCallCount);
        Assert.Empty(viewModel.DeviceRegistration.Stores);
        Assert.Null(viewModel.DeviceRegistration.SelectedStore);
        Assert.True(viewModel.DeviceRegistration.IsActivationCodeMode);
        Assert.Contains("activation code", viewModel.DeviceRegistration.StatusMessage, StringComparison.OrdinalIgnoreCase);

        var keyboardActivationCode = DeviceActivationCode.ToLowerInvariant().Insert(6, "\f \t");
        Assert.True(viewModel.TryProcessKeyboardScannerInput(keyboardActivationCode));
        await WaitUntilAsync(() =>
            string.Equals(
                viewModel.DeviceRegistration?.ActivationCode,
                DeviceActivationCode,
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Settings_and_device_registration_share_the_composed_api_server_settings()
    {
        var apiServerSettings = new ApiServerSettingsViewModel(
            new ApiServerSettingsService(
                new HttpClient(),
                () => "https://current.example.com/",
                _ => { }),
            new LocalizationService());
        var deviceApi = new FakeDeviceApiClient
        {
            Stores =
            [
                new StoreSelectionItem("1042", "Old Store", true),
                new StoreSelectionItem("2042", "New Store", true)
            ]
        };
        var viewModel = CreateAuthorizedMainViewModelWithSettings(
            deviceApiClient: deviceApi,
            apiServerSettings: apiServerSettings);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        Assert.Same(apiServerSettings, viewModel.ApiServerSettings);
        Assert.Equal(
            "https://current.example.com/",
            Assert.IsType<ApiServerSettingsViewModel>(viewModel.ApiServerSettings).ServerAddressText);
        await viewModel.ShowSettingsCommand.ExecuteAsync(null);
        var settings = Assert.IsType<SettingsViewModel>(viewModel.CurrentScreen);

        await settings.ReregisterDeviceCommand.ExecuteAsync(null);

        Assert.Same(apiServerSettings, settings.ApiServerSettings);
        Assert.Same(apiServerSettings, Assert.IsType<DeviceRegistrationViewModel>(viewModel.DeviceRegistration).ApiServerSettings);
    }

    [Fact]
    public async Task Settings_ReregisterDeviceCommand_NotifiesDeviceRegistrationForDialogBinding()
    {
        var deviceApi = new FakeDeviceApiClient
        {
            Stores =
            [
                new StoreSelectionItem("1042", "Old Store", true),
                new StoreSelectionItem("2042", "New Store", true)
            ]
        };
        var viewModel = CreateAuthorizedMainViewModelWithSettings(deviceApiClient: deviceApi);
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.ShowSettingsCommand.ExecuteAsync(null);
        var settings = Assert.IsType<SettingsViewModel>(viewModel.CurrentScreen);
        changedProperties.Clear();

        await settings.ReregisterDeviceCommand.ExecuteAsync(null);

        Assert.Contains(nameof(MainViewModel.DeviceRegistration), changedProperties);
        Assert.True(viewModel.IsDeviceReregistrationDialogOpen);
        Assert.NotNull(viewModel.DeviceRegistration);
        Assert.Empty(viewModel.DeviceRegistration.Stores);
        Assert.True(viewModel.DeviceRegistration.IsActivationCodeMode);
        Assert.True(viewModel.DeviceRegistration.CancelCommand.CanExecute(null));

        changedProperties.Clear();
        viewModel.DeviceRegistration.CancelCommand.Execute(null);

        Assert.Contains(nameof(MainViewModel.DeviceRegistration), changedProperties);
        Assert.False(viewModel.IsDeviceReregistrationDialogOpen);
        Assert.Null(viewModel.DeviceRegistration);
    }

    [Fact]
    public async Task Settings_ReregisterDeviceCommand_CancelClosesActivationDialogWithoutStartingStoreLoad()
    {
        var deviceApi = new FakeDeviceApiClient();
        var viewModel = CreateAuthorizedMainViewModelWithSettings(deviceApiClient: deviceApi);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.ShowSettingsCommand.ExecuteAsync(null);
        var settings = Assert.IsType<SettingsViewModel>(viewModel.CurrentScreen);

        await settings.ReregisterDeviceCommand.ExecuteAsync(null);

        Assert.True(viewModel.DeviceRegistration!.CancelCommand.CanExecute(null));
        Assert.Equal(0, deviceApi.GetStoresCallCount);

        viewModel.DeviceRegistration.CancelCommand.Execute(null);

        Assert.False(viewModel.IsDeviceReregistrationDialogOpen);
        Assert.Null(viewModel.DeviceRegistration);
        Assert.Same(settings, viewModel.CurrentScreen);

        Assert.False(viewModel.IsDeviceReregistrationDialogOpen);
        Assert.Null(viewModel.DeviceRegistration);
        Assert.Same(settings, viewModel.CurrentScreen);
    }

    [Fact]
    public async Task Settings_ReregisterDeviceCommand_DoesNotDependOnStoreListAndCanCancel()
    {
        var deviceApi = new FakeDeviceApiClient
        {
            Stores = [new StoreSelectionItem("1042", "Old Store", true)]
        };
        var viewModel = CreateAuthorizedMainViewModelWithSettings(deviceApiClient: deviceApi);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.ShowSettingsCommand.ExecuteAsync(null);
        var settings = Assert.IsType<SettingsViewModel>(viewModel.CurrentScreen);

        await settings.ReregisterDeviceCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsDeviceReregistrationDialogOpen);
        Assert.NotNull(viewModel.DeviceRegistration);
        Assert.Empty(viewModel.DeviceRegistration.Stores);
        Assert.Null(viewModel.DeviceRegistration.SelectedStore);
        Assert.False(viewModel.DeviceRegistration.RegisterCommand.CanExecute(null));
        Assert.Contains("activation code", viewModel.DeviceRegistration.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, deviceApi.GetStoresCallCount);
        Assert.True(viewModel.DeviceRegistration.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task Settings_ReregisterDeviceCommand_WhenSyncPending_ShowsBlockedReason()
    {
        var syncQueue = new FakeSyncQueueRepository { Overview = new SyncQueueOverview(1, 0, 0, null) };
        var viewModel = CreateAuthorizedMainViewModelWithSettings(syncQueueRepository: syncQueue);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.ShowSettingsCommand.ExecuteAsync(null);
        var settings = Assert.IsType<SettingsViewModel>(viewModel.CurrentScreen);

        await settings.ReregisterDeviceCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsDeviceReregistrationDialogOpen);
        Assert.Null(viewModel.DeviceRegistration);
        Assert.Same(settings, viewModel.CurrentScreen);
        Assert.Contains("pending order sync", settings.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task KeyboardScannerInput_OnNonScannerScreenIsConsumedWithoutChangingCartOrScreen()
    {
        var scanner = new FakeRawScannerService();
        var catalog = new FakeCatalogRepository
        {
            Items =
            [
                CreateItem("1042", "SKU-001", "930110"),
                CreateItem("1042", "SKU-002", "930111")
            ]
        };
        var index = new LocalSellableItemIndex();
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        var orderRepository = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var viewModel = new MainViewModel(
            new PosCoreServices(index, cart, checkout, new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), scanner, null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(new LocalizationService(), new FakeSettingsRepository()),
            new ShellCatalogService(index, catalog, new FakeCatalogSyncService()),
            catalog,
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new MainShellStartupService(new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService(), new DeviceAuthorizationState()),
            orderRepository,
            new ShellSyncCenterService(syncQueue),
            new LocalizationService(),
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(orderRepository),
            new CashPaymentWorkflowService(checkout, orderRepository, syncQueue),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService()),
            new SpecialProductsWorkflowService(index, cart, catalog, new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                index,
                cart,
                remoteLookupRefreshAsync,
                reloadCatalogAsync));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await WaitUntilAsync(() => index.FindExactMatches("1042", "930110").Count == 1);
        scanner.Emit("930110");
        viewModel.ShowCashPaymentCommand.Execute(null);
        var screen = viewModel.CurrentScreen;
        var status = viewModel.StatusMessage;
        var line = Assert.Single(viewModel.PosTerminal!.CartLines);

        var processed = viewModel.TryProcessKeyboardScannerInput("930111");

        Assert.True(processed);
        Assert.Same(screen, viewModel.CurrentScreen);
        Assert.Null(scanner.ActivePageId);
        Assert.Equal(status, viewModel.StatusMessage);
        Assert.Same(line, Assert.Single(viewModel.PosTerminal.CartLines));
        Assert.Equal(1m, line.Quantity);
    }

    [Fact]
    public async Task RawScannerInput_FromSpecialProductsEditModeSearchesCandidatesWithoutAddingCart()
    {
        var scanner = new FakeRawScannerService();
        var index = new LocalSellableItemIndex();
        var catalog = new FakeCatalogRepository
        {
            Items = [CreateItem("1042", "SKU-001", "319844731768")],
            SpecialItems = [CreateItem("1042", "SKU-SP", "9528502522399")]
        };
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        var orderRepository = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var viewModel = new MainViewModel(
            new PosCoreServices(index, cart, checkout, new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), scanner, null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(new LocalizationService(), new FakeSettingsRepository()),
            new ShellCatalogService(index, catalog, new FakeCatalogSyncService()),
            catalog,
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new MainShellStartupService(new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService(), new DeviceAuthorizationState()),
            orderRepository,
            new ShellSyncCenterService(syncQueue),
            new LocalizationService(),
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(orderRepository),
            new CashPaymentWorkflowService(checkout, orderRepository, syncQueue),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService()),
            new SpecialProductsWorkflowService(index, cart, catalog, new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                index,
                cart,
                remoteLookupRefreshAsync,
                reloadCatalogAsync));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.PosTerminal!.OpenSpecialProductsCommand.ExecuteAsync(null);
        viewModel.SpecialProducts!.ToggleEditModeCommand.Execute(null);

        scanner.Emit("319844731768");

        Assert.Same(viewModel.SpecialProducts, viewModel.CurrentScreen);
        Assert.Empty(viewModel.PosTerminal.CartLines);
        Assert.Equal("319844731768", viewModel.SpecialProducts.SearchText);
        await WaitUntilAsync(() => viewModel.SpecialProducts.SearchResults.Count == 1);
        Assert.Equal("SKU-001", Assert.Single(viewModel.SpecialProducts.SearchResults).ProductCode);
    }

    [Fact]
    public async Task InitializeAsync_WhenLocalCatalogLoadFails_StillShowsPosWithStatusMessage()
    {
        var catalog = new FakeCatalogRepository
        {
            LoadSellableItemsException = new InvalidOperationException("catalog load failed")
        };
        var viewModel = new MainViewModel(
            new PosCoreServices(new LocalSellableItemIndex(), new PosCartService(), new CashCheckoutService(), new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), new FakeRawScannerService(), null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(new LocalizationService(), new FakeSettingsRepository()),
            new ShellCatalogService(new LocalSellableItemIndex(), catalog, new FakeCatalogSyncService()),
            catalog,
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new MainShellStartupService(new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService(), new DeviceAuthorizationState()),
            new FakeLocalOrderRepository(),
            new ShellSyncCenterService(new FakeSyncQueueRepository()),
            new LocalizationService(),
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(new FakeLocalOrderRepository()),
            new CashPaymentWorkflowService(new CashCheckoutService(), new FakeLocalOrderRepository(), new FakeSyncQueueRepository()),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService()),
            new SpecialProductsWorkflowService(new LocalSellableItemIndex(), new PosCartService(), catalog, new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                new LocalSellableItemIndex(),
                new PosCartService(),
                remoteLookupRefreshAsync,
                reloadCatalogAsync));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await WaitUntilAsync(() => viewModel.StatusMessage.Contains("catalog load failed", StringComparison.Ordinal));

        Assert.Equal(1, catalog.LoadSellableItemsCallCount);
        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.Contains("catalog load failed", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_WhenStartupCatalogLoadIsCanceled_StillShowsPos()
    {
        var index = new LocalSellableItemIndex();
        var catalog = new FakeCatalogRepository
        {
            Items = [CreateItem("1042", "SKU-001", "319844731768")],
            LoadSellableItemsException = new OperationCanceledException("catalog load canceled")
        };
        var viewModel = new MainViewModel(
            new PosCoreServices(index, new PosCartService(), new CashCheckoutService(), new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), new FakeRawScannerService(), null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(new LocalizationService(), new FakeSettingsRepository()),
            new ShellCatalogService(index, catalog, new FakeCatalogSyncService()),
            catalog,
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new MainShellStartupService(new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService(), new DeviceAuthorizationState()),
            new FakeLocalOrderRepository(),
            new ShellSyncCenterService(new FakeSyncQueueRepository()),
            new LocalizationService(),
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(new FakeLocalOrderRepository()),
            new CashPaymentWorkflowService(new CashCheckoutService(), new FakeLocalOrderRepository(), new FakeSyncQueueRepository()),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") }, new FakeDeviceFingerprintService()),
            new SpecialProductsWorkflowService(index, new PosCartService(), catalog, new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                index,
                new PosCartService(),
                remoteLookupRefreshAsync,
                reloadCatalogAsync));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.Equal(1, catalog.LoadSellableItemsCallCount);
        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.True(viewModel.IsPosTerminalScreenActive);
        Assert.Empty(index.FindExactMatches("1042", "319844731768"));
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_WithSecondDisplay_KeepsCustomerDisplayWindowClosed()
    {
        using var logs = new ConsoleLogCapture();
        var customerDisplayWindow = new FakeCustomerDisplayWindowService();
        var viewModel = CreateAuthorizedMainViewModel(customerDisplayWindow);
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.Equal(0, customerDisplayWindow.PrewarmCallCount);
        Assert.Equal(0, customerDisplayWindow.WindowCreationCount);
        Assert.Equal(0, customerDisplayWindow.SetModeCallCount);
        Assert.Equal(CustomerDisplayWindowMode.Closed, customerDisplayWindow.LastSetMode);
        Assert.Equal(CustomerDisplayWindowMode.Closed, viewModel.CustomerDisplayWindowMode);
        Assert.False(viewModel.IsCustomerDisplayOpen);
        Assert.Contains(logs.Lines, line => line.Contains("[CustomerDisplay]") && line.Contains("startup prewarm skipped") && line.Contains("reason=auto-open-disabled"));
        Assert.Contains(logs.Lines, line => line.Contains("[CustomerDisplay]") && line.Contains("post-show open skipped") && line.Contains("reason=auto-open-disabled"));
    }

    [Fact]
    public async Task InitializeAsync_PreloadsSpecialProductsDataBeforeMainWindowShown()
    {
        using var logs = new ConsoleLogCapture();
        var specialProductsWorkflow = new FakeSpecialProductsWorkflowService
        {
            PreloadResult = new SpecialProductsLoadResult("1042", [CreateItem("1042", "SKU-SP", "930001")])
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            specialProductsWorkflowService: specialProductsWorkflow);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.Equal(1, specialProductsWorkflow.PreloadCallCount);
        Assert.Equal("1042", specialProductsWorkflow.LastPreloadStoreCode);
        Assert.Single(viewModel.SpecialProducts!.SpecialItems);
        Assert.Contains(logs.Lines, line => line.Contains("[SpecialProducts]") && line.Contains("startup data preload completed"));
        Assert.DoesNotContain(logs.Lines, line => line.Contains("startup thumbnail preload completed"));
    }

    [Fact]
    public async Task InitializeAsync_SpecialProductsPreloadFailure_DoesNotBlockMainWindow()
    {
        using var logs = new ConsoleLogCapture();
        var specialProductsWorkflow = new FakeSpecialProductsWorkflowService
        {
            PreloadException = new InvalidOperationException("special preload failed")
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            specialProductsWorkflowService: specialProductsWorkflow);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.Equal(1, specialProductsWorkflow.PreloadCallCount);
        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.True(viewModel.IsPosTerminalScreenActive);
        Assert.Contains(logs.Lines, line => line.Contains("[SpecialProducts]") && line.Contains("preload failed"));
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_WhenCalledTwice_DoesNotAutoOpenCustomerDisplayWindow()
    {
        var customerDisplayWindow = new FakeCustomerDisplayWindowService();
        var viewModel = CreateAuthorizedMainViewModel(customerDisplayWindow);
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.Equal(0, customerDisplayWindow.PrewarmCallCount);
        Assert.Equal(0, customerDisplayWindow.WindowCreationCount);
        Assert.Equal(0, customerDisplayWindow.SetModeCallCount);
        Assert.Equal(CustomerDisplayWindowMode.Closed, customerDisplayWindow.LastSetMode);
        Assert.Equal(CustomerDisplayWindowMode.Closed, viewModel.CustomerDisplayWindowMode);
        Assert.False(viewModel.IsCustomerDisplayOpen);
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_DoesNotPreloadSpecialProductsHome()
    {
        using var logs = new ConsoleLogCapture();
        var specialProductsWorkflow = new FakeSpecialProductsWorkflowService
        {
            PreloadResult = new SpecialProductsLoadResult("1042", [CreateItem("1042", "SKU-SP", "930001")])
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            specialProductsWorkflowService: specialProductsWorkflow);
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        var preloadCallCountAfterInitialize = specialProductsWorkflow.PreloadCallCount;
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.Equal(1, preloadCallCountAfterInitialize);
        Assert.Equal(preloadCallCountAfterInitialize, specialProductsWorkflow.PreloadCallCount);
        Assert.Contains(logs.Lines, line => line.Contains("[SpecialProducts]") && line.Contains("startup home preload skipped"));
        Assert.DoesNotContain(logs.Lines, line => line.Contains("startup thumbnail preload completed"));
    }

    [Fact]
    public async Task ContinueStartupAfterShownAsync_WithSingleDisplay_DoesNotAttemptCustomerDisplayWindow()
    {
        var customerDisplayWindow = new FakeCustomerDisplayWindowService
        {
            SetModeResult = new CustomerDisplayWindowResult(
                CustomerDisplayWindowMode.Closed,
                CustomerDisplayWindowService.NoSecondDisplayStatusKey)
        };
        var viewModel = CreateAuthorizedMainViewModel(customerDisplayWindow);
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.Equal(0, customerDisplayWindow.PrewarmCallCount);
        Assert.Equal(0, customerDisplayWindow.WindowCreationCount);
        Assert.Equal(0, customerDisplayWindow.SetModeCallCount);
        Assert.Equal(CustomerDisplayWindowMode.Closed, customerDisplayWindow.LastSetMode);
        Assert.Equal(CustomerDisplayWindowMode.Closed, viewModel.CustomerDisplayWindowMode);
        Assert.False(viewModel.IsCustomerDisplayOpen);
    }

    [Fact]
    public async Task ToggleCustomerDisplayWindow_WithSingleDisplay_ShowsHelpfulStatus()
    {
        var customerDisplayWindow = new FakeCustomerDisplayWindowService
        {
            SetModeResult = new CustomerDisplayWindowResult(
                CustomerDisplayWindowMode.Closed,
                CustomerDisplayWindowService.NoSecondDisplayStatusKey)
        };
        var viewModel = CreateAuthorizedMainViewModel(customerDisplayWindow);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.ToggleCustomerDisplayWindow(null);

        Assert.Equal(1, customerDisplayWindow.SetModeCallCount);
        Assert.Equal(CustomerDisplayWindowMode.Normal, customerDisplayWindow.LastSetMode);
        Assert.Equal(CustomerDisplayWindowMode.Closed, viewModel.CustomerDisplayWindowMode);
        Assert.False(viewModel.IsCustomerDisplayOpen);
        Assert.Equal("No second display detected. Customer display was not opened.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task CustomerDisplayFullscreenRequest_switches_normal_window_to_fullscreen_through_command()
    {
        var customerDisplayWindow = new FakeCustomerDisplayWindowService();
        var viewModel = CreateAuthorizedMainViewModel(customerDisplayWindow);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        viewModel.SetCustomerDisplayWindowMode(CustomerDisplayWindowMode.Normal, owner: null);

        customerDisplayWindow.RaiseFullscreenRequested();
        await (viewModel.ShowCustomerDisplayFullscreenCommand.ExecutionTask ?? Task.CompletedTask);

        Assert.Equal(CustomerDisplayWindowMode.Fullscreen, customerDisplayWindow.LastSetMode);
        Assert.Equal(CustomerDisplayWindowMode.Fullscreen, viewModel.CustomerDisplayWindowMode);
        Assert.Equal("Customer display opened full screen on the second display.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task CustomerDisplayFullscreenRequest_requires_customer_display_permission()
    {
        var customerDisplayWindow = new FakeCustomerDisplayWindowService();
        var cashierContext = new CashierSessionContext();
        cashierContext.SetCurrent(CreateCashierSession(Permissions.PosTerminal.Sales.AddItem));
        var viewModel = CreateAuthorizedMainViewModel(
            customerDisplayWindow,
            cashierSessionContext: cashierContext,
            enforceCashierPermissions: true);
        viewModel.SetCustomerDisplayWindowMode(CustomerDisplayWindowMode.Normal, owner: null);

        customerDisplayWindow.RaiseFullscreenRequested();
        await (viewModel.ShowCustomerDisplayFullscreenCommand.ExecutionTask ?? Task.CompletedTask);

        Assert.Equal(1, customerDisplayWindow.SetModeCallCount);
        Assert.Equal(CustomerDisplayWindowMode.Normal, viewModel.CustomerDisplayWindowMode);
        Assert.False(cashierContext.RequirePermission(Permissions.PosTerminal.CustomerDisplay.Manage, out var deniedMessage));
        Assert.Equal(deniedMessage, viewModel.StatusMessage);
    }

    [Fact]
    public void ToggleCustomerDisplayWindowCommand_requires_customer_display_permission()
    {
        var customerDisplayWindow = new FakeCustomerDisplayWindowService();
        var cashierContext = new CashierSessionContext();
        cashierContext.SetCurrent(CreateCashierSession(Permissions.PosTerminal.Sales.AddItem));
        var viewModel = CreateAuthorizedMainViewModel(
            customerDisplayWindow,
            cashierSessionContext: cashierContext,
            enforceCashierPermissions: true);

        viewModel.ToggleCustomerDisplayWindowCommand.Execute(null);

        Assert.Equal(0, customerDisplayWindow.SetModeCallCount);
        Assert.False(cashierContext.RequirePermission(Permissions.PosTerminal.CustomerDisplay.Manage, out var deniedMessage));
        Assert.Equal(deniedMessage, viewModel.StatusMessage);
    }

    [Fact]
    public async Task ToggleCustomerDisplayWindow_CyclesClosedNormalFullscreenClosed()
    {
        var customerDisplayWindow = new FakeCustomerDisplayWindowService();
        var viewModel = CreateAuthorizedMainViewModel(customerDisplayWindow);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        await viewModel.ToggleCustomerDisplayWindow(null);

        Assert.Equal(CustomerDisplayWindowMode.Normal, customerDisplayWindow.LastSetMode);
        Assert.Equal(CustomerDisplayWindowMode.Normal, viewModel.CustomerDisplayWindowMode);
        Assert.True(viewModel.IsCustomerDisplayOpen);
        Assert.Equal("Customer display opened in a normal window on the second display.", viewModel.StatusMessage);

        await viewModel.ToggleCustomerDisplayWindow(null);

        Assert.Equal(CustomerDisplayWindowMode.Fullscreen, customerDisplayWindow.LastSetMode);
        Assert.Equal(CustomerDisplayWindowMode.Fullscreen, viewModel.CustomerDisplayWindowMode);
        Assert.True(viewModel.IsCustomerDisplayOpen);
        Assert.Equal("Customer display opened full screen on the second display.", viewModel.StatusMessage);

        await viewModel.ToggleCustomerDisplayWindow(null);

        Assert.Equal(CustomerDisplayWindowMode.Closed, customerDisplayWindow.LastSetMode);
        Assert.Equal(CustomerDisplayWindowMode.Closed, viewModel.CustomerDisplayWindowMode);
        Assert.False(viewModel.IsCustomerDisplayOpen);
        Assert.Equal("Customer display closed.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task SetCustomerDisplayWindowMode_PreservesManualNormalFullscreenAndCloseSemantics()
    {
        var customerDisplayWindow = new FakeCustomerDisplayWindowService();
        var viewModel = CreateAuthorizedMainViewModel(customerDisplayWindow);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        viewModel.SetCustomerDisplayWindowMode(CustomerDisplayWindowMode.Normal, owner: null);

        Assert.Equal(CustomerDisplayWindowMode.Normal, customerDisplayWindow.LastSetMode);
        Assert.Equal(CustomerDisplayWindowMode.Normal, viewModel.CustomerDisplayWindowMode);
        Assert.True(viewModel.IsCustomerDisplayOpen);
        Assert.Equal("Customer display opened in a normal window on the second display.", viewModel.StatusMessage);

        viewModel.SetCustomerDisplayWindowMode(CustomerDisplayWindowMode.Fullscreen, owner: null);

        Assert.Equal(CustomerDisplayWindowMode.Fullscreen, customerDisplayWindow.LastSetMode);
        Assert.Equal(CustomerDisplayWindowMode.Fullscreen, viewModel.CustomerDisplayWindowMode);
        Assert.True(viewModel.IsCustomerDisplayOpen);
        Assert.Equal("Customer display opened full screen on the second display.", viewModel.StatusMessage);

        viewModel.SetCustomerDisplayWindowMode(CustomerDisplayWindowMode.Closed, owner: null);

        Assert.Equal(3, customerDisplayWindow.SetModeCallCount);
        Assert.Equal(CustomerDisplayWindowMode.Closed, customerDisplayWindow.LastSetMode);
        Assert.Equal(CustomerDisplayWindowMode.Closed, viewModel.CustomerDisplayWindowMode);
        Assert.False(viewModel.IsCustomerDisplayOpen);
        Assert.Equal("Customer display closed.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task CustomerDisplayWindowClosed_UpdatesOpenState()
    {
        var customerDisplayWindow = new FakeCustomerDisplayWindowService();
        var viewModel = CreateAuthorizedMainViewModel(customerDisplayWindow);
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        viewModel.SetCustomerDisplayWindowMode(CustomerDisplayWindowMode.Fullscreen, owner: null);

        Assert.True(viewModel.IsCustomerDisplayOpen);
        Assert.Equal(CustomerDisplayWindowMode.Fullscreen, viewModel.CustomerDisplayWindowMode);

        customerDisplayWindow.RaiseClosed();

        Assert.False(viewModel.IsCustomerDisplayOpen);
        Assert.Equal(CustomerDisplayWindowMode.Closed, viewModel.CustomerDisplayWindowMode);
    }

    [Fact]
    public async Task ReregisterDevice_WithPendingSync_StaysOnPosAndShowsStatus()
    {
        var priceIndex = new LocalSellableItemIndex();
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        var orderRepository = new FakeLocalOrderRepository();
        var localization = new LocalizationService();
        var catalogRepository = new FakeCatalogRepository();
        var syncQueue = new FakeSyncQueueRepository { Overview = new SyncQueueOverview(1, 0, 0, null) };
        var deviceRepository = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var fingerprintService = new FakeDeviceFingerprintService();
        var viewModel = new MainViewModel(
            new PosCoreServices(priceIndex, cart, checkout, new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), new FakeRawScannerService(), null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(localization, new FakeSettingsRepository()),
            new ShellCatalogService(priceIndex, catalogRepository, new FakeCatalogSyncService()),
            catalogRepository,
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new MainShellStartupService(deviceRepository, fingerprintService, new DeviceAuthorizationState()),
            orderRepository,
            new ShellSyncCenterService(syncQueue),
            localization,
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(orderRepository),
            new CashPaymentWorkflowService(checkout, orderRepository, syncQueue),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), deviceRepository, fingerprintService),
            new SpecialProductsWorkflowService(priceIndex, cart, catalogRepository, new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(priceIndex, cart, remoteLookupRefreshAsync, reloadCatalogAsync));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await viewModel.PosTerminal!.ReregisterDeviceCommand.ExecuteAsync(null);

        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.Contains("pending order sync", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RetrySyncOrderCommand_WithFailedOrder_RetriesSingleOrderAndRefreshesSyncCenter()
    {
        var orderGuid = Guid.NewGuid();
        var item = CreateSyncQueueItem(orderGuid, "Failed");
        var syncQueue = new FakeSyncQueueRepository
        {
            Overview = new SyncQueueOverview(0, 1, 0, "network down"),
            ActiveItems = [item]
        };
        var uploadExecution = new FakeOrderUploadExecutionService
        {
            ExecuteOneResult = new OrderUploadExecutionResult(1, 1, 0),
            OnExecuteOne = _ =>
            {
                syncQueue.Overview = new SyncQueueOverview(0, 0, 0, null);
                syncQueue.ActiveItems = [];
            }
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            syncQueueRepository: syncQueue,
            orderUploadExecutionService: uploadExecution);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.True(viewModel.RetrySyncOrderCommand.CanExecute(item));

        await viewModel.RetrySyncOrderCommand.ExecuteAsync(item);

        Assert.Equal(orderGuid, uploadExecution.LastExecuteOneOrderGuid);
        Assert.Equal(0, viewModel.PendingUploadCount);
        Assert.Equal(0, viewModel.FailedUploadCount);
        Assert.Empty(viewModel.SyncCenterOrders);
        Assert.Contains("1", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetryAllSyncOrdersCommand_IsDisabledWhenOnlySyncingOrdersExist()
    {
        var item = CreateSyncQueueItem(Guid.NewGuid(), "Syncing");
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            syncQueueRepository: new FakeSyncQueueRepository
            {
                Overview = new SyncQueueOverview(0, 0, 1, null),
                ActiveItems = [item]
            });

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        Assert.False(viewModel.RetryAllSyncOrdersCommand.CanExecute(null));
        Assert.False(viewModel.RetrySyncOrderCommand.CanExecute(item));
    }

    [Fact]
    public async Task RetryAllSyncOrdersCommand_WhenRetryClearsFailures_AllowsDeviceReregistration()
    {
        var syncQueue = new FakeSyncQueueRepository
        {
            Overview = new SyncQueueOverview(0, 1, 0, "network down"),
            ActiveItems = [CreateSyncQueueItem(Guid.NewGuid(), "Failed")]
        };
        var uploadExecution = new FakeOrderUploadExecutionService
        {
            ExecutePendingResult = new OrderUploadExecutionResult(1, 1, 0),
            OnExecutePending = () =>
            {
                syncQueue.Overview = new SyncQueueOverview(0, 0, 0, null);
                syncQueue.ActiveItems = [];
            }
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            syncQueueRepository: syncQueue,
            orderUploadExecutionService: uploadExecution);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        Assert.True(viewModel.RetryAllSyncOrdersCommand.CanExecute(null));

        await viewModel.RetryAllSyncOrdersCommand.ExecuteAsync(null);
        await viewModel.PosTerminal!.ReregisterDeviceCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsDeviceReregistrationDialogOpen);
        Assert.NotNull(viewModel.DeviceRegistration);
        Assert.Equal(1, uploadExecution.ExecutePendingCallCount);
    }

    [Fact]
    public async Task RetryAllSyncOrdersCommand_WhenRetryFails_KeepsFailedCountAndError()
    {
        var item = CreateSyncQueueItem(Guid.NewGuid(), "Failed", "network down");
        var syncQueue = new FakeSyncQueueRepository
        {
            Overview = new SyncQueueOverview(0, 1, 0, "network down"),
            ActiveItems = [item]
        };
        var uploadExecution = new FakeOrderUploadExecutionService
        {
            ExecutePendingResult = new OrderUploadExecutionResult(1, 0, 1)
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            syncQueueRepository: syncQueue,
            orderUploadExecutionService: uploadExecution);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        await viewModel.RetryAllSyncOrdersCommand.ExecuteAsync(null);

        Assert.Equal(1, viewModel.FailedUploadCount);
        Assert.Equal("network down", viewModel.LastOrderSyncErrorText);
        Assert.Single(viewModel.SyncCenterOrders);
        Assert.Contains("0", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("1", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_online_check_auto_retries_pending_orders_and_refreshes_sync_center()
    {
        var syncQueue = new FakeSyncQueueRepository
        {
            Overview = new SyncQueueOverview(1, 0, 0, null),
            ActiveItems = [CreateSyncQueueItem(Guid.NewGuid(), "Pending")]
        };
        var uploadExecution = new FakeOrderUploadExecutionService
        {
            ExecutePendingResult = new OrderUploadExecutionResult(1, 1, 0),
            OnExecutePending = () =>
            {
                syncQueue.Overview = new SyncQueueOverview(0, 0, 0, null);
                syncQueue.ActiveItems = [];
            }
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            syncQueueRepository: syncQueue,
            orderUploadExecutionService: uploadExecution,
            connectivityApiClient: new FakeConnectivityApiClient(true));
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.Equal(1, uploadExecution.ExecutePendingCallCount);
        Assert.Equal(0, viewModel.PendingUploadCount);
        Assert.Empty(viewModel.SyncCenterOrders);
    }

    [Fact]
    public async Task Startup_online_check_auto_retries_pending_linkly_settlements_and_refreshes_sync_center()
    {
        var settlementReader = new FakeSettlementUploadQueueReader
        {
            Overview = new LinklySettlementUploadOverview(1, 0, 0, null),
            ActiveItems =
            [
                new LinklySettlementUploadQueueItem(
                    Guid.NewGuid(),
                    "1042",
                    "POS-01",
                    DateTime.Today,
                    LocalLinklySettlementUploadStatus.Pending,
                    DateTimeOffset.UtcNow,
                    1,
                    0,
                    0,
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    null,
                    null)
            ]
        };
        var settlementExecutor = new FakeSettlementUploadExecutionService
        {
            OnExecutePending = () =>
            {
                settlementReader.Overview = new LinklySettlementUploadOverview(0, 0, 0, null);
                settlementReader.ActiveItems = [];
            }
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            connectivityApiClient: new FakeConnectivityApiClient(true),
            linklySettlementUploadQueueReader: settlementReader,
            linklySettlementUploadExecutionService: settlementExecutor);
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.Equal(1, settlementExecutor.ExecutePendingCallCount);
        Assert.Equal(0, viewModel.PendingUploadCount);
        Assert.Empty(viewModel.SyncCenterOrders);
    }

    [Fact]
    public async Task Connectivity_refresh_auto_retries_when_backend_changes_from_offline_to_online()
    {
        var syncQueue = new FakeSyncQueueRepository
        {
            Overview = new SyncQueueOverview(1, 0, 0, null),
            ActiveItems = [CreateSyncQueueItem(Guid.NewGuid(), "Pending")]
        };
        var uploadExecution = new FakeOrderUploadExecutionService
        {
            ExecutePendingResult = new OrderUploadExecutionResult(1, 1, 0),
            OnExecutePending = () =>
            {
                syncQueue.Overview = new SyncQueueOverview(0, 0, 0, null);
                syncQueue.ActiveItems = [];
            }
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            syncQueueRepository: syncQueue,
            orderUploadExecutionService: uploadExecution,
            connectivityApiClient: new FakeConnectivityApiClient(false, true));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await InvokeRefreshOnlineStateAsync(viewModel);
        await InvokeRefreshOnlineStateAsync(viewModel, autoRetryOrders: true);

        Assert.Equal(1, uploadExecution.ExecutePendingCallCount);
        Assert.True(viewModel.Session.IsOnline);
        Assert.Equal(0, viewModel.PendingUploadCount);
    }

    [Fact]
    public async Task Connectivity_refresh_auto_retries_each_online_cycle_without_overwriting_status()
    {
        var syncQueue = new FakeSyncQueueRepository
        {
            Overview = new SyncQueueOverview(1, 0, 0, null),
            ActiveItems = [CreateSyncQueueItem(Guid.NewGuid(), "Pending")]
        };
        var uploadExecution = new FakeOrderUploadExecutionService
        {
            ExecutePendingResult = new OrderUploadExecutionResult(1, 1, 0)
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            syncQueueRepository: syncQueue,
            orderUploadExecutionService: uploadExecution,
            connectivityApiClient: new FakeConnectivityApiClient(true, true));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        viewModel.StatusMessage = "keep this status";
        await InvokeRefreshOnlineStateAsync(viewModel, autoRetryOrders: true);
        await InvokeRefreshOnlineStateAsync(viewModel, autoRetryOrders: true);

        Assert.Equal(2, uploadExecution.ExecutePendingCallCount);
        Assert.Equal("keep this status", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Connectivity_refresh_skips_auto_retry_while_manual_retry_is_running()
    {
        var releaseManualRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manualRetryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var syncQueue = new FakeSyncQueueRepository
        {
            Overview = new SyncQueueOverview(1, 0, 0, null),
            ActiveItems = [CreateSyncQueueItem(Guid.NewGuid(), "Pending")]
        };
        var uploadExecution = new FakeOrderUploadExecutionService
        {
            PendingExecutionStarted = manualRetryStarted,
            ReleasePendingExecution = releaseManualRetry
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            syncQueueRepository: syncQueue,
            orderUploadExecutionService: uploadExecution,
            connectivityApiClient: new FakeConnectivityApiClient(true));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var manualRetryTask = viewModel.RetryAllSyncOrdersCommand.ExecuteAsync(null);
        await manualRetryStarted.Task;

        await InvokeRefreshOnlineStateAsync(viewModel, autoRetryOrders: true);
        releaseManualRetry.SetResult();
        await manualRetryTask;

        Assert.Equal(1, uploadExecution.ExecutePendingCallCount);
    }

    [Fact]
    public async Task Connectivity_refresh_keeps_sync_snapshot_when_auto_retry_fails()
    {
        var item = CreateSyncQueueItem(Guid.NewGuid(), "Failed", "network down");
        var syncQueue = new FakeSyncQueueRepository
        {
            Overview = new SyncQueueOverview(0, 1, 0, "network down"),
            ActiveItems = [item]
        };
        var uploadExecution = new FakeOrderUploadExecutionService
        {
            ExecutePendingException = new InvalidOperationException("still offline")
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            syncQueueRepository: syncQueue,
            orderUploadExecutionService: uploadExecution,
            connectivityApiClient: new FakeConnectivityApiClient(true));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await InvokeRefreshOnlineStateAsync(viewModel, autoRetryOrders: true);

        Assert.Equal(1, uploadExecution.ExecutePendingCallCount);
        Assert.Equal(1, viewModel.FailedUploadCount);
        Assert.Equal("network down", viewModel.LastOrderSyncErrorText);
        Assert.Single(viewModel.SyncCenterOrders);
    }

    [Fact]
    public async Task Connectivity_refresh_without_auto_retry_only_updates_online_state()
    {
        var syncQueue = new FakeSyncQueueRepository
        {
            Overview = new SyncQueueOverview(1, 0, 0, null),
            ActiveItems = [CreateSyncQueueItem(Guid.NewGuid(), "Pending")]
        };
        var uploadExecution = new FakeOrderUploadExecutionService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            syncQueueRepository: syncQueue,
            orderUploadExecutionService: uploadExecution,
            connectivityApiClient: new FakeConnectivityApiClient(true));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        await InvokeRefreshOnlineStateAsync(viewModel);

        Assert.True(viewModel.Session.IsOnline);
        Assert.Equal(0, uploadExecution.ExecutePendingCallCount);
    }

    [Fact]
    public async Task Connectivity_refresh_reports_runtime_status_with_current_cashier()
    {
        var cashierSession = CreateCashierSession(Permissions.PosTerminal.Sales.AddItem);
        var runtimeStatus = new RecordingRuntimeStatusApiClient();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            connectivityApiClient: new FakeConnectivityApiClient(true),
            cashierLoginService: new FakeCashierLoginService(cashierSession),
            runtimeStatusApiClient: runtimeStatus);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        viewModel.CashierBarcodeInput = "BAR-1";
        await viewModel.LoginCashierCommand.ExecuteAsync(null);
        runtimeStatus.Reports.Clear();
        await InvokeRefreshOnlineStateAsync(viewModel);

        var report = Assert.Single(runtimeStatus.Reports);
        Assert.True(report.IsOnline);
        Assert.Equal("CASHIER-1", report.CashierId);
        Assert.Equal("Alice", report.CashierName);
    }

    [Fact]
    public async Task Connectivity_refresh_swallows_auto_retry_snapshot_refresh_failure()
    {
        var syncQueue = new FakeSyncQueueRepository
        {
            Overview = new SyncQueueOverview(1, 0, 0, null),
            ActiveItems = [CreateSyncQueueItem(Guid.NewGuid(), "Pending")]
        };
        var uploadExecution = new FakeOrderUploadExecutionService
        {
            ExecutePendingException = new InvalidOperationException("upload failed"),
            OnBeforeExecutePendingException = () => syncQueue.ThrowOnRead = true
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            syncQueueRepository: syncQueue,
            orderUploadExecutionService: uploadExecution,
            connectivityApiClient: new FakeConnectivityApiClient(true));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var isOnline = await InvokeRefreshOnlineStateAsync(viewModel, autoRetryOrders: true);

        Assert.True(isOnline);
        Assert.Equal(1, uploadExecution.ExecutePendingCallCount);
    }

    [Fact]
    public async Task Card_payment_recovery_checking_result_allows_next_recovery_check()
    {
        var recovery = new FakeCardPaymentRecoveryService(
            Task.FromResult(new CardPaymentRecoveryResult(CardPaymentRecoveryOutcome.Checking, "checking")),
            Task.FromResult(CardPaymentRecoveryResult.None));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cardPaymentRecoveryService: recovery);

        await InvokeRecoverCardPaymentAttemptAsync(viewModel, navigateToPaymentOnDraft: true);
        await InvokeRecoverCardPaymentAttemptAsync(viewModel, navigateToPaymentOnDraft: true);

        Assert.Equal(2, recovery.CallCount);
    }

    [Fact]
    public async Task Card_payment_recovery_concurrent_checks_share_inflight_task()
    {
        var recoveryResult = new TaskCompletionSource<CardPaymentRecoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovery = new FakeCardPaymentRecoveryService(recoveryResult.Task);
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cardPaymentRecoveryService: recovery);

        var first = InvokeRecoverCardPaymentAttemptAsync(viewModel, navigateToPaymentOnDraft: true);
        var second = InvokeRecoverCardPaymentAttemptAsync(viewModel, navigateToPaymentOnDraft: true);
        while (recovery.CallCount == 0)
        {
            await Task.Yield();
        }

        recoveryResult.SetResult(CardPaymentRecoveryResult.None);
        await Task.WhenAll(first, second);

        Assert.Equal(1, recovery.CallCount);
    }

    [Fact]
    public async Task Card_payment_recovery_check_does_not_run_when_opening_payment_with_non_empty_cart()
    {
        var cart = new PosCartService();
        var recovery = new FakeCardPaymentRecoveryService(
            Task.FromResult(new CardPaymentRecoveryResult(CardPaymentRecoveryOutcome.Checking, "checking")),
            Task.FromResult(CardPaymentRecoveryResult.None));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cart: cart,
            cardPaymentRecoveryService: recovery);

        await InvokeRecoverCardPaymentAttemptAsync(viewModel, navigateToPaymentOnDraft: true);
        cart.AddItem(CreateItem("1042", "SKU-CURRENT", "930CURRENT"));

        viewModel.ShowCashPaymentCommand.Execute(null);

        Assert.Equal(1, recovery.CallCount);
        Assert.Equal("Payment", viewModel.ActivePageTitleText);
    }

    [Fact]
    public async Task Startup_card_recovery_scans_count_after_cashier_login_without_recovering_or_dialog()
    {
        var printService = new RecordingReceiptPrintService();
        var cashierSession = CreateCashierSession(Permissions.PosTerminal.Sales.AddItem);
        var recovery = new FakeCardPaymentRecoveryService
        {
            OpenItems =
            [
                new CardRecoveryQueueItem(
                    CardProcessorKind.Linkly,
                    Guid.Parse("40000000-0000-0000-0000-000000000001"),
                    "Sale",
                    12.34m,
                    "1042",
                    "POS-01",
                    "OLD-CASHIER",
                    "Sandbox",
                    "Recovering",
                    DateTimeOffset.UtcNow.AddMinutes(-2),
                    DateTimeOffset.UtcNow.AddMinutes(-1))
            ]
        };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            receiptPrintService: printService,
            cardPaymentRecoveryService: recovery,
            cashierLoginService: new FakeCashierLoginService(cashierSession));
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);

        Assert.True(viewModel.IsCashierLoginOverlayOpen);
        Assert.Equal(0, recovery.CallCount);
        Assert.Equal(0, recovery.ListOpenCallCount);
        Assert.Empty(printService.Calls);
        Assert.False(viewModel.IsCardRecoveryResultDialogOpen);

        viewModel.CashierBarcodeInput = "BAR-1";
        await viewModel.LoginCashierCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsCashierLoginOverlayOpen);
        Assert.Same(cashierSession, viewModel.Session.CashierSession);
        Assert.Equal(0, recovery.CallCount);
        Assert.Equal(1, recovery.ListOpenCallCount);
        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.Equal(1, viewModel.PosTerminal?.CardRecoveryOpenCount);
        Assert.Empty(printService.Calls);
        Assert.False(viewModel.IsCardRecoveryResultDialogOpen);
    }

    [Fact]
    public async Task Card_recovery_status_opens_center_without_changing_current_cart()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("1042", "SKU-CURRENT-RECOVERY", "930CURRENTRECOVERY"));
        var cashierContext = new CashierSessionContext();
        var cashierSession = CreateCashierSession(
            Permissions.PosTerminal.Sales.AddItem,
            Permissions.PosTerminal.Payment.View);
        var authorization = new GrantingOperationAuthorizationService();
        var item = new CardRecoveryQueueItem(
            CardProcessorKind.Linkly,
            Guid.Parse("40000000-0000-0000-0000-000000000002"),
            "Sale",
            12.34m,
            "1042",
            "POS-01",
            "OLD-CASHIER",
            "Sandbox",
            "Recovering",
            DateTimeOffset.UtcNow.AddMinutes(-2),
            DateTimeOffset.UtcNow.AddMinutes(-1));
        var recovery = new FakeCardPaymentRecoveryService { OpenItems = [item] };
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cardPaymentRecoveryService: recovery,
            cart: cart,
            cashierSessionContext: cashierContext,
            cashierLoginService: new FakeCashierLoginService(cashierSession),
            operationAuthorizationService: authorization,
            enforceCashierPermissions: true);
        var startupOptions = new AppStartupOptions([], false, null, null);

        await viewModel.InitializeAsync(startupOptions);
        await viewModel.ContinueStartupAfterShownAsync(startupOptions);
        viewModel.CashierBarcodeInput = "CARD-RECOVERY-CASHIER";
        await viewModel.LoginCashierCommand.ExecuteAsync(null);
        await viewModel.PosTerminal!.OpenCardRecoveryCenterCommand.ExecuteAsync(null);

        var center = Assert.IsType<CardRecoveryCenterViewModel>(viewModel.CurrentScreen);
        var displayedAttempt = Assert.Single(center.OpenAttempts);
        Assert.Equal(item.Processor, displayedAttempt.Processor);
        Assert.Equal(item.AttemptGuid, displayedAttempt.AttemptGuid);
        Assert.Equal(item.Key, displayedAttempt.Key);
        Assert.Equal(item.Amount, displayedAttempt.Amount);
        var displayedRow = Assert.Single(center.OpenAttemptRows);
        Assert.Equal("Card sale", displayedRow.OperationTypeText);
        Assert.Equal("Checking result", displayedRow.StatusText);
        Assert.Equal(item.Key, center.SelectedAttempt?.Key);
        Assert.Equal(1, viewModel.PosTerminal.CardRecoveryOpenCount);
        Assert.Single(cart.Lines);
        Assert.Equal("SKU-CURRENT-RECOVERY", cart.Lines[0].ProductCode);
        Assert.Contains(
            authorization.Requests,
            request => request.PermissionCode == Permissions.PosTerminal.Payment.View &&
                       request.Screen == "card-recovery-center" &&
                       request.Action == "view");

        center.BackCommand.Execute(null);

        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
    }

    [Fact]
    public async Task Card_recovery_center_back_from_unknown_payment_restores_same_locked_payment()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("1042", "SKU-UNKNOWN-BACK", "930UNKNOWNBACK"));
        var checkout = new CashCheckoutService();
        var orderRepository = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var recovery = new FakeCardPaymentRecoveryService { OpenItems = [] };
        var viewModel = CreateAuthorizedMainViewModelWithPaymentWorkflow(
            cart,
            checkout,
            orderRepository,
            syncQueue,
            new UnknownCardTerminalClient(),
            recovery);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        viewModel.Session = viewModel.Session with
        {
            CashierSession = CreateCashierSession(
                Permissions.PosTerminal.Payment.TakeCard,
                Permissions.PosTerminal.Payment.View)
        };
        viewModel.ShowCashPaymentCommand.Execute(null);
        var payment = Assert.IsType<PaymentViewModel>(viewModel.CurrentScreen);

        await payment.SelectCardCommand.ExecuteAsync(null);

        var overlay = Assert.IsType<CardPaymentErrorOverlayViewModel>(payment.CardPaymentErrorOverlay);
        var unknownStatus = payment.StatusMessage;
        var existingTender = new PaymentTender(PaymentMethodKind.Cash, 1m);
        payment.PaymentTenders.Add(existingTender);
        Assert.True(overlay.IsOpen);
        Assert.True(payment.IsPaymentInteractionLocked);
        Assert.False(payment.SelectCardCommand.CanExecute(null));
        Assert.True(payment.CardPaymentErrorPrimaryActionCommand.CanExecute(null));

        await payment.CardPaymentErrorPrimaryActionCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.CurrentScreen is CardRecoveryCenterViewModel);
        var center = Assert.IsType<CardRecoveryCenterViewModel>(viewModel.CurrentScreen);

        center.BackCommand.Execute(null);

        Assert.Same(payment, viewModel.CurrentScreen);
        Assert.Same(payment, viewModel.CashPayment);
        Assert.True(overlay.IsOpen);
        Assert.True(payment.IsPaymentInteractionLocked);
        Assert.False(payment.SelectCardCommand.CanExecute(null));
        Assert.Equal(unknownStatus, payment.StatusMessage);
        Assert.Equal(existingTender, Assert.Single(payment.PaymentTenders));
        Assert.Single(cart.Lines);
        Assert.Equal("SKU-UNKNOWN-BACK", cart.Lines[0].ProductCode);
    }

    [Theory]
    [InlineData(CardProcessorKind.Linkly, true, true, true)]
    [InlineData(CardProcessorKind.Linkly, false, true, true)]
    [InlineData(CardProcessorKind.Linkly, true, false, true)]
    [InlineData(CardProcessorKind.Linkly, true, true, false)]
    [InlineData(CardProcessorKind.Square, true, true, true)]
    [InlineData(CardProcessorKind.Square, false, true, true)]
    [InlineData(CardProcessorKind.Square, true, false, true)]
    [InlineData(CardProcessorKind.Square, true, true, false)]
    public async Task Card_recovery_after_persistence_failure_unlocks_only_matching_order_and_attempt(
        CardProcessorKind processor, bool matchesOrder, bool matchesAttempt, bool matchesProvider)
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("1042", "SKU-PERSIST-RECOVER", "930PERSISTRECOVER"));
        var recovery = new FakeCardPaymentRecoveryService { OpenItems = [] };
        var viewModel = CreateAuthorizedMainViewModelWithPaymentWorkflow(cart,
            new CashCheckoutService(), new FakeLocalOrderRepository(), new FakeSyncQueueRepository(),
            new UnknownCardTerminalClient(), recovery);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        viewModel.Session = viewModel.Session with
        {
            CashierSession = CreateCashierSession(Permissions.PosTerminal.Payment.TakeCard, Permissions.PosTerminal.Payment.View)
        };
        viewModel.ShowCashPaymentCommand.Execute(null);
        var payment = Assert.IsType<PaymentViewModel>(viewModel.CurrentScreen);
        var selectedKey = new CardRecoveryAttemptKey(processor, Guid.NewGuid());
        var expectedOrderGuid = Guid.NewGuid();
        GetCardPaymentSession(payment).SetPersistenceRecoveryOrder(expectedOrderGuid);
        payment.SetCurrentCardRecoveryRequired(true, "Save requires recovery");
        payment.PaymentTenders.Add(new PaymentTender(PaymentMethodKind.Card, 10m, "BANK-APPROVED"));
        Assert.True(payment.OpenCardRecoveryCenterCommand.CanExecute(null));
        payment.OpenCardRecoveryCenterCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.CurrentScreen is CardRecoveryCenterViewModel);
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Card);
        var paymentKeyPrefix = (processor == CardProcessorKind.Linkly) == matchesProvider ? "CARD_ATTEMPT" : "SQUARE_ATTEMPT";
        order = order with
        {
            OrderGuid = matchesOrder ? expectedOrderGuid : Guid.NewGuid(),
            Payments = [order.Payments.Single() with
            {
                IdempotencyKey = $"{paymentKeyPrefix}:{(matchesAttempt ? selectedKey.AttemptGuid : Guid.NewGuid()):N}"
            }]
        };

        await InvokeHandleCardRecoveryCenterResultAsync(viewModel, selectedKey,
            new CardPaymentRecoveryResult(CardPaymentRecoveryOutcome.OrderCompleted, "Recovered", Order: order));

        var completed = matchesOrder && matchesAttempt && matchesProvider;
        Assert.Equal(!completed, payment.IsPaymentInteractionLocked);
        Assert.Equal(!completed, payment.IsCardPaymentRecoveryRequired);
        Assert.Equal(completed, cart.IsEmpty);
        Assert.Equal(completed ? 0 : 1, payment.PaymentTenders.Count);
        if (completed)
        {
            Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        }
    }

    [Fact]
    public async Task Card_recovery_center_order_completed_for_current_unknown_key_clears_payment_when_pending_sync_refresh_fails()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("1042", "SKU-UNKNOWN-COMPLETED", "930UNKNOWNCOMPLETED"));
        var checkout = new CashCheckoutService();
        var orderRepository = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var currentKey = new CardRecoveryAttemptKey(
            CardProcessorKind.Linkly,
            Guid.Parse("40000000-0000-0000-0000-000000000101"));
        var queueItem = new CardRecoveryQueueItem(
            currentKey.Processor,
            currentKey.AttemptGuid,
            "Sale",
            12.34m,
            "1042",
            "POS-01",
            "CASHIER-1",
            "Sandbox",
            "Recovering",
            DateTimeOffset.UtcNow.AddMinutes(-2),
            DateTimeOffset.UtcNow.AddMinutes(-1));
        // 交易在执行恢复前持续可见，不依赖入口执行了几次资格查询。
        var recoveryStarted = false;
        var recovery = new FakeCardPaymentRecoveryService
        {
            // 恢复前的资格复查与页面加载都能看到交易；恢复后的刷新单独模拟失败。
            ListOpenHandler = (_, _) => recoveryStarted
                ? Task.FromException<IReadOnlyList<CardRecoveryQueueItem>>(
                    new InvalidOperationException("queue unavailable"))
                : Task.FromResult<IReadOnlyList<CardRecoveryQueueItem>>([queueItem]),
            TargetedRecoverResult = new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.OrderCompleted,
                "Recovered current payment completed.")
        };
        var viewModel = CreateAuthorizedMainViewModelWithPaymentWorkflow(
            cart,
            checkout,
            orderRepository,
            syncQueue,
            new UnknownCardTerminalClient(),
            recovery);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        viewModel.Session = viewModel.Session with
        {
            CashierSession = CreateCashierSession(
                Permissions.PosTerminal.Payment.TakeCard,
                Permissions.PosTerminal.Payment.View)
        };
        viewModel.ShowCashPaymentCommand.Execute(null);
        var payment = Assert.IsType<PaymentViewModel>(viewModel.CurrentScreen);
        var cardSession = GetCardPaymentSession(payment);
        payment.PaymentTenders.Add(new PaymentTender(PaymentMethodKind.Cash, 1m));

        await cardSession.TryHandleFailedResultAsync(new PaymentTenderAttemptResult(
            false,
            "payment.card.resultUnknown",
            StatusMessage: "Current card result is unknown.",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true),
            RecoveryAttemptKey: currentKey,
            RecoveryOrderGuid: Guid.Parse("40000000-0000-0000-0000-000000000102")));
        var overlay = Assert.IsType<CardPaymentErrorOverlayViewModel>(payment.CardPaymentErrorOverlay);
        Assert.True(cardSession.HasUnknownResult);
        Assert.Equal(currentKey, payment.CreateCardPaymentHandoffRequest().RecoveryAttemptKey);

        await payment.CardPaymentErrorPrimaryActionCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.CurrentScreen is CardRecoveryCenterViewModel);
        var center = Assert.IsType<CardRecoveryCenterViewModel>(viewModel.CurrentScreen);
        var listCallCountBeforeRecovery = recovery.ListOpenCallCount;
        Assert.Equal(currentKey, center.SelectedAttempt?.Key);

        syncQueue.ThrowOnRead = true;
        recoveryStarted = true;
        await center.RecoverCommand.ExecuteAsync(null);

        Assert.Equal(listCallCountBeforeRecovery + 1, recovery.ListOpenCallCount);
        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.Same(payment, viewModel.CashPayment);
        Assert.Equal(
            "Payment completed. Do not take payment again; a follow-up action needs attention.",
            viewModel.StatusMessage);
        Assert.DoesNotContain("Could not check the selected transaction.", center.StatusMessage);
        Assert.False(cardSession.HasUnknownResult);
        Assert.Null(payment.CreateCardPaymentHandoffRequest().RecoveryAttemptKey);
        Assert.False(payment.IsPaymentInteractionLocked);
        Assert.False(overlay.IsOpen);
        Assert.Null(payment.CardPaymentErrorOverlay);
        Assert.Empty(payment.PaymentTenders);
        Assert.Empty(cart.Lines);
    }

    [Theory]
    [InlineData(nameof(MainViewModel.Session))]
    [InlineData(nameof(MainViewModel.StatusMessage))]
    public async Task Card_recovery_center_order_completed_continues_when_initial_main_notification_throws(
        string propertyName)
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("1042", "SKU-UNKNOWN-NOTIFICATION", "930UNKNOWNNOTIFICATION"));
        var checkout = new CashCheckoutService();
        var orderRepository = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var currentKey = new CardRecoveryAttemptKey(
            CardProcessorKind.Linkly,
            Guid.Parse("40000000-0000-0000-0000-000000000123"));
        var queueItem = new CardRecoveryQueueItem(
            currentKey.Processor,
            currentKey.AttemptGuid,
            "Sale",
            45.67m,
            "1042",
            "POS-01",
            "CASHIER-1",
            "Sandbox",
            "Recovering",
            DateTimeOffset.UtcNow.AddMinutes(-2),
            DateTimeOffset.UtcNow.AddMinutes(-1));
        // 交易在执行恢复前持续可见，不依赖入口执行了几次资格查询。
        var recoveryStarted = false;
        var recovery = new FakeCardPaymentRecoveryService
        {
            // 按恢复状态提供队列，避免资格复查次数改变测试的业务前置条件。
            ListOpenHandler = (_, _) => Task.FromResult<IReadOnlyList<CardRecoveryQueueItem>>(
                recoveryStarted ? [] : [queueItem]),
            TargetedRecoverResult = new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.OrderCompleted,
                "Recovered current payment completed.",
                UpdatedSession: new PosSessionState(
                    "HB POS",
                    "1042",
                    "Main Branch",
                    "POS-01",
                    "CASHIER-1",
                    "Alice",
                    true,
                    0,
                    CreateCashierSession(
                        Permissions.PosTerminal.Payment.TakeCard,
                        Permissions.PosTerminal.Payment.View)))
        };
        var viewModel = CreateAuthorizedMainViewModelWithPaymentWorkflow(
            cart,
            checkout,
            orderRepository,
            syncQueue,
            new UnknownCardTerminalClient(),
            recovery);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        viewModel.Session = viewModel.Session with
        {
            CashierSession = CreateCashierSession(
                Permissions.PosTerminal.Payment.TakeCard,
                Permissions.PosTerminal.Payment.View)
        };
        viewModel.ShowCashPaymentCommand.Execute(null);
        var payment = Assert.IsType<PaymentViewModel>(viewModel.CurrentScreen);
        var cardSession = GetCardPaymentSession(payment);
        payment.PaymentTenders.Add(new PaymentTender(PaymentMethodKind.Cash, 1m));

        await cardSession.TryHandleFailedResultAsync(new PaymentTenderAttemptResult(
            false,
            "payment.card.resultUnknown",
            StatusMessage: "Current card result is unknown.",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true),
            RecoveryAttemptKey: currentKey,
            RecoveryOrderGuid: Guid.Parse("40000000-0000-0000-0000-000000000124")));
        var overlay = Assert.IsType<CardPaymentErrorOverlayViewModel>(payment.CardPaymentErrorOverlay);

        await payment.CardPaymentErrorPrimaryActionCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.CurrentScreen is CardRecoveryCenterViewModel);
        var center = Assert.IsType<CardRecoveryCenterViewModel>(viewModel.CurrentScreen);
        var listCallCountBeforeRecovery = recovery.ListOpenCallCount;
        Assert.Equal(currentKey, center.SelectedAttempt?.Key);
        syncQueue.Overview = new SyncQueueOverview(4, 0, 0, null);
        var syncReadCountBeforeRecovery = syncQueue.ReadCount;
        var throwOnce = true;
        void ThrowFromPropertyChanged(object? _, PropertyChangedEventArgs e)
        {
            if (throwOnce && e.PropertyName == propertyName)
            {
                throwOnce = false;
                throw new InvalidOperationException($"{propertyName} subscriber failed");
            }
        }

        viewModel.PropertyChanged += ThrowFromPropertyChanged;
        try
        {
            recoveryStarted = true;
            var commandException = await Record.ExceptionAsync(
                () => center.RecoverCommand.ExecuteAsync(null));

            Assert.Null(commandException);
        }
        finally
        {
            viewModel.PropertyChanged -= ThrowFromPropertyChanged;
        }

        Assert.Equal(listCallCountBeforeRecovery + 1, recovery.ListOpenCallCount);
        Assert.Equal(1, recovery.TargetedRecoverCallCount);
        Assert.False(cardSession.HasUnknownResult);
        Assert.Null(payment.CreateCardPaymentHandoffRequest().RecoveryAttemptKey);
        Assert.False(payment.IsPaymentInteractionLocked);
        Assert.False(overlay.IsOpen);
        Assert.Null(payment.CardPaymentErrorOverlay);
        Assert.Empty(payment.PaymentTenders);
        Assert.Empty(cart.Lines);
        Assert.Empty(viewModel.PosTerminal!.CartLines);
        Assert.Empty(payment.CartLines);
        Assert.True(syncQueue.ReadCount > syncReadCountBeforeRecovery);
        Assert.Equal(4, viewModel.Session.PendingSyncCount);
    }

    [Theory]
    [InlineData(nameof(MainViewModel.Session))]
    [InlineData(nameof(MainViewModel.StatusMessage))]
    [InlineData(nameof(MainViewModel.CurrentScreen))]
    public async Task Card_recovery_center_draft_restored_initial_notification_failure_continues_without_completed_warning(
        string propertyName)
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("1042", "SKU-RECOVER-DRAFT-NOTIFICATION", "930RECOVERDRAFTNOTIFICATION"));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cart: cart);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var restoredTender = new PaymentTender(
            PaymentMethodKind.Card,
            5m,
            "ANZ:RECOVERED-DRAFT",
            IdempotencyKey: "CARD_ATTEMPT:bbbbbbbbccccddddeeeeffffffffffff");
        var result = new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.DraftRestored,
            "Recovered draft for payment.",
            UpdatedSession: viewModel.Session with { PendingSyncCount = 3 },
            RestoredTenders: [restoredTender]);
        var selectedKey = new CardRecoveryAttemptKey(
            CardProcessorKind.Linkly,
            Guid.Parse("40000000-0000-0000-0000-000000000127"));
        var throwOnce = true;
        void ThrowFromPropertyChanged(object? _, PropertyChangedEventArgs e)
        {
            if (throwOnce && e.PropertyName == propertyName)
            {
                throwOnce = false;
                throw new InvalidOperationException($"{propertyName} subscriber failed");
            }
        }

        viewModel.PropertyChanged += ThrowFromPropertyChanged;
        try
        {
            var callbackException = await Record.ExceptionAsync(
                () => InvokeHandleCardRecoveryCenterResultAsync(viewModel, selectedKey, result));

            Assert.Null(callbackException);
        }
        finally
        {
            viewModel.PropertyChanged -= ThrowFromPropertyChanged;
        }

        Assert.Same(viewModel.CashPayment, viewModel.CurrentScreen);
        Assert.True(viewModel.IsCashPaymentScreenActive);
        var tender = Assert.Single(viewModel.CashPayment!.PaymentTenders);
        Assert.Equal(PaymentMethodKind.Card, tender.Method);
        Assert.Equal(5m, tender.Amount);
        Assert.Equal("CARD_ATTEMPT:bbbbbbbbccccddddeeeeffffffffffff", tender.IdempotencyKey);
        Assert.Equal("Recovered draft for payment.", viewModel.StatusMessage);
        Assert.False(viewModel.PaymentSuccess.HasPostCommitWarning);
        Assert.DoesNotContain("Payment completed. Do not take payment again", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Card_recovery_center_unpaid_draft_without_tenders_keeps_restored_cart_and_opens_payment()
    {
        var cart = new PosCartService();
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cart: cart);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        cart.AddItem(CreateItem("1042", "SKU-RECOVERED-UNPAID", "930RECOVEREDUNPAID"));
        var result = new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.DraftRestored,
            "The unpaid order was restored.");
        var selectedKey = new CardRecoveryAttemptKey(
            CardProcessorKind.Linkly,
            Guid.Parse("40000000-0000-0000-0000-000000000128"));

        await InvokeHandleCardRecoveryCenterResultAsync(viewModel, selectedKey, result);

        Assert.Same(viewModel.CashPayment, viewModel.CurrentScreen);
        Assert.True(viewModel.IsCashPaymentScreenActive);
        Assert.Equal("SKU-RECOVERED-UNPAID", Assert.Single(cart.Lines).ProductCode);
        Assert.Empty(viewModel.CashPayment!.PaymentTenders);
    }

    [Fact]
    public async Task Card_recovery_center_order_completed_continues_when_warning_notification_throws()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("1042", "SKU-UNKNOWN-WARNING", "930UNKNOWNWARNING"));
        var throwOnCartChanged = false;
        void ThrowFromCartChanged(object? _, EventArgs __)
        {
            if (throwOnCartChanged)
            {
                throw new InvalidOperationException("cart changed subscriber failed");
            }
        }

        cart.CartChanged += ThrowFromCartChanged;
        try
        {
            var checkout = new CashCheckoutService();
            var orderRepository = new FakeLocalOrderRepository();
            var syncQueue = new FakeSyncQueueRepository();
            var currentKey = new CardRecoveryAttemptKey(
                CardProcessorKind.Linkly,
                Guid.Parse("40000000-0000-0000-0000-000000000125"));
            var queueItem = new CardRecoveryQueueItem(
                currentKey.Processor,
                currentKey.AttemptGuid,
                "Sale",
                56.78m,
                "1042",
                "POS-01",
                "CASHIER-1",
                "Sandbox",
                "Recovering",
                DateTimeOffset.UtcNow.AddMinutes(-2),
                DateTimeOffset.UtcNow.AddMinutes(-1));
            // 交易在执行恢复前持续可见，不依赖入口执行了几次资格查询。
            var recoveryStarted = false;
            var recovery = new FakeCardPaymentRecoveryService
            {
                ListOpenHandler = (_, _) => Task.FromResult<IReadOnlyList<CardRecoveryQueueItem>>(
                    recoveryStarted ? [] : [queueItem]),
                TargetedRecoverResult = new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.OrderCompleted,
                    "Recovered current payment completed.")
            };
            var viewModel = CreateAuthorizedMainViewModelWithPaymentWorkflow(
                cart,
                checkout,
                orderRepository,
                syncQueue,
                new UnknownCardTerminalClient(),
                recovery);

            await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
            viewModel.Session = viewModel.Session with
            {
                CashierSession = CreateCashierSession(
                    Permissions.PosTerminal.Payment.TakeCard,
                    Permissions.PosTerminal.Payment.View)
            };
            viewModel.ShowCashPaymentCommand.Execute(null);
            var payment = Assert.IsType<PaymentViewModel>(viewModel.CurrentScreen);
            var cardSession = GetCardPaymentSession(payment);
            payment.PaymentTenders.Add(new PaymentTender(PaymentMethodKind.Cash, 1m));

            await cardSession.TryHandleFailedResultAsync(new PaymentTenderAttemptResult(
                false,
                "payment.card.resultUnknown",
                StatusMessage: "Current card result is unknown.",
                CardResult: new CardPaymentResultDisposition(
                    CardPaymentTerminalOutcome.ResultUnknown,
                    CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                    PreserveStatus: true),
                RecoveryAttemptKey: currentKey,
                RecoveryOrderGuid: Guid.Parse("40000000-0000-0000-0000-000000000126")));
            var overlay = Assert.IsType<CardPaymentErrorOverlayViewModel>(payment.CardPaymentErrorOverlay);

            await payment.CardPaymentErrorPrimaryActionCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => viewModel.CurrentScreen is CardRecoveryCenterViewModel);
            var center = Assert.IsType<CardRecoveryCenterViewModel>(viewModel.CurrentScreen);
            var listCallCountBeforeRecovery = recovery.ListOpenCallCount;
            Assert.Equal(currentKey, center.SelectedAttempt?.Key);
            syncQueue.Overview = new SyncQueueOverview(4, 0, 0, null);
            var syncReadCountBeforeRecovery = syncQueue.ReadCount;
            void ThrowFromWarningNotification(object? _, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(MainViewModel.StatusMessage) &&
                    viewModel.StatusMessage ==
                    "Payment completed. Do not take payment again; a follow-up action needs attention.")
                {
                    throw new InvalidOperationException("warning subscriber failed");
                }
            }

            viewModel.PropertyChanged += ThrowFromWarningNotification;
            throwOnCartChanged = true;
            try
            {
                recoveryStarted = true;
                var commandException = await Record.ExceptionAsync(
                    () => center.RecoverCommand.ExecuteAsync(null));

                Assert.Null(commandException);
            }
            finally
            {
                viewModel.PropertyChanged -= ThrowFromWarningNotification;
            }

            Assert.Equal(listCallCountBeforeRecovery + 1, recovery.ListOpenCallCount);
            Assert.Equal(1, recovery.TargetedRecoverCallCount);
            Assert.False(cardSession.HasUnknownResult);
            Assert.Null(payment.CreateCardPaymentHandoffRequest().RecoveryAttemptKey);
            Assert.False(payment.IsPaymentInteractionLocked);
            Assert.False(overlay.IsOpen);
            Assert.Null(payment.CardPaymentErrorOverlay);
            Assert.Empty(payment.PaymentTenders);
            Assert.Empty(cart.Lines);
            Assert.Empty(viewModel.PosTerminal!.CartLines);
            Assert.Empty(payment.CartLines);
            Assert.True(syncQueue.ReadCount > syncReadCountBeforeRecovery);
            Assert.Equal(4, viewModel.Session.PendingSyncCount);
        }
        finally
        {
            cart.CartChanged -= ThrowFromCartChanged;
        }
    }

    [Fact]
    public async Task Card_recovery_center_order_completed_keeps_result_when_cart_changed_handler_throws()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("1042", "SKU-UNKNOWN-CART-EVENT", "930UNKNOWNCARTEVENT"));
        var throwOnCartChanged = false;
        void ThrowFromCartChanged(object? _, EventArgs __)
        {
            if (throwOnCartChanged)
            {
                throw new InvalidOperationException("cart changed subscriber failed");
            }
        }

        cart.CartChanged += ThrowFromCartChanged;
        try
        {
            var checkout = new CashCheckoutService();
            var orderRepository = new FakeLocalOrderRepository();
            var syncQueue = new FakeSyncQueueRepository();
            var currentKey = new CardRecoveryAttemptKey(
                CardProcessorKind.Linkly,
                Guid.Parse("40000000-0000-0000-0000-000000000121"));
            var queueItem = new CardRecoveryQueueItem(
                currentKey.Processor,
                currentKey.AttemptGuid,
                "Sale",
                34.56m,
                "1042",
                "POS-01",
                "CASHIER-1",
                "Sandbox",
                "Recovering",
                DateTimeOffset.UtcNow.AddMinutes(-2),
                DateTimeOffset.UtcNow.AddMinutes(-1));
            // 交易在执行恢复前持续可见，不依赖入口执行了几次资格查询。
            var recoveryStarted = false;
            var recovery = new FakeCardPaymentRecoveryService
            {
                ListOpenHandler = (_, _) => Task.FromResult<IReadOnlyList<CardRecoveryQueueItem>>(
                    recoveryStarted ? [] : [queueItem]),
                TargetedRecoverResult = new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.OrderCompleted,
                    "Recovered current payment completed.")
            };
            var viewModel = CreateAuthorizedMainViewModelWithPaymentWorkflow(
                cart,
                checkout,
                orderRepository,
                syncQueue,
                new UnknownCardTerminalClient(),
                recovery);

            await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
            viewModel.Session = viewModel.Session with
            {
                CashierSession = CreateCashierSession(
                    Permissions.PosTerminal.Payment.TakeCard,
                    Permissions.PosTerminal.Payment.View)
            };
            viewModel.ShowCashPaymentCommand.Execute(null);
            var payment = Assert.IsType<PaymentViewModel>(viewModel.CurrentScreen);
            var cardSession = GetCardPaymentSession(payment);
            payment.PaymentTenders.Add(new PaymentTender(PaymentMethodKind.Cash, 1m));

            await cardSession.TryHandleFailedResultAsync(new PaymentTenderAttemptResult(
                false,
                "payment.card.resultUnknown",
                StatusMessage: "Current card result is unknown.",
                CardResult: new CardPaymentResultDisposition(
                    CardPaymentTerminalOutcome.ResultUnknown,
                    CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                    PreserveStatus: true),
                RecoveryAttemptKey: currentKey,
                RecoveryOrderGuid: Guid.Parse("40000000-0000-0000-0000-000000000122")));
            var overlay = Assert.IsType<CardPaymentErrorOverlayViewModel>(payment.CardPaymentErrorOverlay);
            Assert.True(cardSession.HasUnknownResult);
            Assert.Equal(currentKey, payment.CreateCardPaymentHandoffRequest().RecoveryAttemptKey);

            await payment.CardPaymentErrorPrimaryActionCommand.ExecuteAsync(null);
            await WaitUntilAsync(() => viewModel.CurrentScreen is CardRecoveryCenterViewModel);
            var center = Assert.IsType<CardRecoveryCenterViewModel>(viewModel.CurrentScreen);
            var listCallCountBeforeRecovery = recovery.ListOpenCallCount;
            Assert.Equal(currentKey, center.SelectedAttempt?.Key);
            syncQueue.Overview = new SyncQueueOverview(4, 0, 0, null);
            var syncReadCountBeforeRecovery = syncQueue.ReadCount;
            throwOnCartChanged = true;

            recoveryStarted = true;
            var commandException = await Record.ExceptionAsync(
                () => center.RecoverCommand.ExecuteAsync(null));

            Assert.Null(commandException);
            Assert.Equal(listCallCountBeforeRecovery + 1, recovery.ListOpenCallCount);
            Assert.Equal(1, recovery.TargetedRecoverCallCount);
            Assert.Equal(
                "Payment completed. Do not take payment again; a follow-up action needs attention.",
                viewModel.StatusMessage);
            Assert.Equal("Recovered current payment completed.", center.StatusMessage);
            Assert.DoesNotContain("Could not check the selected transaction.", center.StatusMessage);
            Assert.False(cardSession.HasUnknownResult);
            Assert.Null(payment.CreateCardPaymentHandoffRequest().RecoveryAttemptKey);
            Assert.False(payment.IsPaymentInteractionLocked);
            Assert.False(overlay.IsOpen);
            Assert.Null(payment.CardPaymentErrorOverlay);
            Assert.Empty(payment.PaymentTenders);
            Assert.Empty(cart.Lines);
            Assert.Empty(viewModel.PosTerminal!.CartLines);
            Assert.Empty(payment.CartLines);
            Assert.True(syncQueue.ReadCount > syncReadCountBeforeRecovery);
            Assert.Equal(4, viewModel.Session.PendingSyncCount);
        }
        finally
        {
            cart.CartChanged -= ThrowFromCartChanged;
        }
    }

    [Fact]
    public async Task Card_recovery_center_order_completed_for_different_key_keeps_current_unknown_payment_locked_when_pending_sync_refresh_fails()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("1042", "SKU-UNKNOWN-DIFFERENT", "930UNKNOWNDIFFERENT"));
        var checkout = new CashCheckoutService();
        var orderRepository = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var currentKey = new CardRecoveryAttemptKey(
            CardProcessorKind.Linkly,
            Guid.Parse("40000000-0000-0000-0000-000000000111"));
        var historicalKey = new CardRecoveryAttemptKey(
            CardProcessorKind.Linkly,
            Guid.Parse("40000000-0000-0000-0000-000000000112"));
        var queueItem = new CardRecoveryQueueItem(
            historicalKey.Processor,
            historicalKey.AttemptGuid,
            "Sale",
            23.45m,
            "1042",
            "POS-01",
            "CASHIER-OLD",
            "Sandbox",
            "Recovering",
            DateTimeOffset.UtcNow.AddMinutes(-4),
            DateTimeOffset.UtcNow.AddMinutes(-3));
        // 交易在执行恢复前持续可见，不依赖入口执行了几次资格查询。
        var recoveryStarted = false;
        var recovery = new FakeCardPaymentRecoveryService
        {
            ListOpenHandler = (_, _) => Task.FromResult<IReadOnlyList<CardRecoveryQueueItem>>(
                recoveryStarted ? [] : [queueItem]),
            TargetedResolveResult = new CardRecoveryResolutionResult(
                true,
                "Historical payment confirmed.",
                new CardPaymentRecoveryResult(
                    CardPaymentRecoveryOutcome.OrderCompleted,
                    "Historical payment completed."))
        };
        var viewModel = CreateAuthorizedMainViewModelWithPaymentWorkflow(
            cart,
            checkout,
            orderRepository,
            syncQueue,
            new UnknownCardTerminalClient(),
            recovery);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        viewModel.Session = viewModel.Session with
        {
            CashierSession = CreateCashierSession(
                Permissions.PosTerminal.Payment.TakeCard,
                Permissions.PosTerminal.Payment.View)
        };
        viewModel.ShowCashPaymentCommand.Execute(null);
        var payment = Assert.IsType<PaymentViewModel>(viewModel.CurrentScreen);
        var cardSession = GetCardPaymentSession(payment);
        var existingTender = new PaymentTender(PaymentMethodKind.Cash, 1m);
        payment.PaymentTenders.Add(existingTender);

        await cardSession.TryHandleFailedResultAsync(new PaymentTenderAttemptResult(
            false,
            "payment.card.resultUnknown",
            StatusMessage: "Current card result remains unknown.",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true),
            RecoveryAttemptKey: currentKey,
            RecoveryOrderGuid: Guid.Parse("40000000-0000-0000-0000-000000000113")));
        var overlay = Assert.IsType<CardPaymentErrorOverlayViewModel>(payment.CardPaymentErrorOverlay);

        await payment.CardPaymentErrorPrimaryActionCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.CurrentScreen is CardRecoveryCenterViewModel);
        var center = Assert.IsType<CardRecoveryCenterViewModel>(viewModel.CurrentScreen);
        var listCallCountBeforeRecovery = recovery.ListOpenCallCount;
        Assert.Equal(historicalKey, center.SelectedAttempt?.Key);
        center.ResolutionReason = "Bank settlement confirmed";

        syncQueue.ThrowOnRead = true;
        recoveryStarted = true;
        await center.ConfirmPaidCommand.ExecuteAsync(null);
        Assert.Equal(listCallCountBeforeRecovery + 1, recovery.ListOpenCallCount);

        Assert.Same(center, viewModel.CurrentScreen);
        Assert.Equal(
            "Payment completed. Do not take payment again; a follow-up action needs attention.",
            viewModel.StatusMessage);
        Assert.Equal("Historical payment confirmed.", center.StatusMessage);
        Assert.True(cardSession.HasUnknownResult);
        Assert.Equal(currentKey, payment.CreateCardPaymentHandoffRequest().RecoveryAttemptKey);
        Assert.True(payment.IsPaymentInteractionLocked);
        Assert.True(overlay.IsOpen);
        Assert.Equal(existingTender, Assert.Single(payment.PaymentTenders));
        Assert.Single(cart.Lines);
        Assert.Equal("SKU-UNKNOWN-DIFFERENT", cart.Lines[0].ProductCode);

        center.BackCommand.Execute(null);

        Assert.Same(payment, viewModel.CurrentScreen);
        Assert.Same(payment, viewModel.CashPayment);
        Assert.True(cardSession.HasUnknownResult);
        Assert.Equal(currentKey, payment.CreateCardPaymentHandoffRequest().RecoveryAttemptKey);
        Assert.True(payment.IsPaymentInteractionLocked);
        Assert.True(overlay.IsOpen);
        Assert.Equal(existingTender, Assert.Single(payment.PaymentTenders));
        Assert.Single(cart.Lines);
    }

    [Fact]
    public async Task Card_recovery_center_draft_restored_applies_alternative_refund_policy_to_payment_page()
    {
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "1042",
            "SKU-RECOVER-ALTERNATIVE",
            null,
            "Recovered Alternative Refund",
            "930RECOVERALTERNATIVE",
            "ITEM-RECOVER-ALTERNATIVE",
            null,
            1m,
            10m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-RECOVER-ALTERNATIVE",
            Guid.NewGuid(),
            Guid.NewGuid()));
        cart.AddReturnPaymentCapacities(
        [
            new OrderReturnPaymentCapacityDto(
                PaymentMethodKind.Card,
                10m,
                0m,
                10m,
                "SQ:recovered-original-card")
        ]);
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cart: cart,
            paymentMethodSettingsService: new MutablePaymentMethodSettingsService(new(VoucherEnabled: true)));
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var result = new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.DraftRestored,
            "Use cash or voucher for this recovered Square refund.")
        {
            RequiresAlternativeRefundMethod = true
        };

        await InvokeHandleCardRecoveryCenterResultAsync(viewModel, result);

        Assert.Same(viewModel.CashPayment, viewModel.CurrentScreen);
        Assert.NotNull(viewModel.CashPayment);
        Assert.False(viewModel.CashPayment!.SelectCardCommand.CanExecute(null));
        Assert.True(viewModel.CashPayment.SelectCashCommand.CanExecute(null));
        Assert.True(viewModel.CashPayment.SelectVoucherCommand.CanExecute(null));
        Assert.Equal(
            "Use cash or voucher for this recovered Square refund.",
            viewModel.CashPayment.StatusMessage);
    }

    [Fact]
    public async Task Card_recovery_center_draft_projection_failure_keeps_payment_page_locked()
    {
        var cart = new PosCartService();
        cart.AddReturnLine(new ReturnCartLineRequest(
            "1042",
            "SKU-RECOVER-FAIL-CLOSED",
            null,
            "Recovered Refund",
            "930RECOVERFAILCLOSED",
            "ITEM-RECOVER-FAIL-CLOSED",
            null,
            1m,
            10m,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            "RETURN-RECOVER-FAIL-CLOSED",
            Guid.NewGuid(),
            Guid.NewGuid()));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cart: cart);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var payment = Assert.IsType<PaymentViewModel>(viewModel.CashPayment);
        payment.PaymentTenders.CollectionChanged += (_, args) =>
        {
            if (args.NewItems is { Count: > 0 })
            {
                throw new InvalidOperationException("tender projection subscriber failed");
            }
        };
        var result = new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.DraftRestored,
            "Complete the recovered refund.",
            RestoredTenders:
            [
                new PaymentTender(
                    PaymentMethodKind.Cash,
                    -5m,
                    "RECOVERED-CASH-REFUND")
            ]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeHandleCardRecoveryCenterResultAsync(viewModel, result));

        Assert.Equal("tender projection subscriber failed", exception.Message);
        Assert.Same(payment, viewModel.CurrentScreen);
        Assert.True(payment.IsPaymentInteractionLocked);
        Assert.Empty(payment.PaymentTenders);
        Assert.False(payment.ConfirmPaymentCommand.CanExecute(null));
    }

    [Fact]
    public async Task Card_recovery_center_payment_session_notification_failure_keeps_owner_and_lock()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("1042", "SKU-RECOVER-PREPARE-FAIL", "930RECOVERPREPAREFAIL"));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cart: cart);
        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        viewModel.ShowCashPaymentCommand.Execute(null);
        var payment = Assert.IsType<PaymentViewModel>(viewModel.CurrentScreen);
        var cardSession = GetCardPaymentSession(payment);
        payment.Session = payment.Session with { DeviceCode = "POS-STALE" };
        var currentKey = new CardRecoveryAttemptKey(
            CardProcessorKind.Linkly,
            Guid.Parse("40000000-0000-0000-0000-000000000129"));
        var existingTender = new PaymentTender(PaymentMethodKind.Cash, 1m, "ORIGINAL-TENDER");
        payment.PaymentTenders.Add(existingTender);
        await cardSession.TryHandleFailedResultAsync(new PaymentTenderAttemptResult(
            false,
            "payment.card.resultUnknown",
            StatusMessage: "Current card result is unknown.",
            CardResult: new CardPaymentResultDisposition(
                CardPaymentTerminalOutcome.ResultUnknown,
                CardPaymentErrorKind.ActiveSessionRequiresRecovery,
                PreserveStatus: true),
            RecoveryAttemptKey: currentKey,
            RecoveryOrderGuid: Guid.Parse("40000000-0000-0000-0000-000000000130")));
        payment.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PaymentViewModel.Session))
            {
                throw new InvalidOperationException("payment session subscriber failed");
            }
        };
        var result = new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.DraftRestored,
            "Recovered draft must remain locked.",
            RestoredTenders:
            [
                new PaymentTender(PaymentMethodKind.Card, 5m, "RECOVERED-CARD")
            ]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeHandleCardRecoveryCenterResultAsync(viewModel, currentKey, result));

        Assert.Equal("payment session subscriber failed", exception.Message);
        Assert.True(cardSession.HasUnknownResult);
        Assert.Equal(currentKey, payment.CreateCardPaymentHandoffRequest().RecoveryAttemptKey);
        Assert.True(payment.IsPaymentInteractionLocked);
        Assert.Same(existingTender, Assert.Single(payment.PaymentTenders));
    }

    [Fact]
    public async Task Card_payment_recovery_completed_during_startup_prints_card_receipt()
    {
        var printService = new RecordingReceiptPrintService();
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Card);
        var recovery = new FakeCardPaymentRecoveryService(
            Task.FromResult(new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.OrderCompleted,
                "Recovered approved payment.",
                order)));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            receiptPrintService: printService,
            cardPaymentRecoveryService: recovery);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var recovered = await InvokeRecoverCardPaymentAttemptAsync(viewModel, navigateToPaymentOnDraft: false);

        Assert.True(recovered);
        Assert.True(IsShowingCompletedSale(viewModel));
        var call = Assert.Single(printService.Calls);
        Assert.Equal(order.OrderGuid, call.OrderGuid);
        Assert.Equal(ReceiptPrintReason.CardAuto, call.Reason);
        Assert.True(viewModel.IsCardRecoveryResultDialogOpen);
        Assert.NotNull(viewModel.CardRecoveryResultDialog);
        Assert.Equal("Card transaction recovered successfully", viewModel.CardRecoveryResultDialog!.Title);
        Assert.True(viewModel.CardRecoveryResultDialog.CanPrintReceipt);
        Assert.Equal("Print receipt", viewModel.CardRecoveryResultDialog.PrintButtonText);
        Assert.True(viewModel.CardRecoveryResultDialog.HasReceiptPreview);
        Assert.Equal(
            order.OrderGuid.ToString("D"),
            Assert.Single(viewModel.CardRecoveryResultDialog.ReceiptPreviewRows, row => row.IsQrCode).QrCodeValue);

        await viewModel.PrintRecoveredReceiptCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => printService.Calls.Count == 2);
        Assert.Equal(ReceiptPrintReason.CardAuto, printService.Calls[1].Reason);
    }

    [Fact]
    public async Task Card_payment_recovery_post_commit_warning_is_shown_without_relocking_payment()
    {
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Card);
        var recovery = new FakeCardPaymentRecoveryService(
            Task.FromResult(new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.OrderCompleted,
                "Recovered approved payment.",
                order,
                HasPostCommitWarning: true)));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cardPaymentRecoveryService: recovery);

        Assert.True(await InvokeRecoverCardPaymentAttemptAsync(viewModel, navigateToPaymentOnDraft: false));

        Assert.Equal(
            "Payment completed. Do not take payment again; a follow-up action needs attention.",
            viewModel.StatusMessage);
    }

    [Fact]
    public async Task Card_payment_recovery_completion_plays_checkout_feedback_once()
    {
        var feedback = new RecordingUserFeedbackService();
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Card);
        var recovery = new FakeCardPaymentRecoveryService(
            Task.FromResult(new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.OrderCompleted,
                "Recovered approved payment.",
                order)));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cardPaymentRecoveryService: recovery,
            userFeedbackService: feedback);

        Assert.True(await InvokeRecoverCardPaymentAttemptAsync(viewModel, navigateToPaymentOnDraft: false));

        Assert.Equal([UserFeedbackCue.Checkout], feedback.Cues);
    }

    [Fact]
    public async Task Card_payment_recovery_completed_auto_prints_without_receipt_permission_but_manual_print_stays_blocked()
    {
        var printService = new RecordingReceiptPrintService();
        var cashierContext = new CashierSessionContext();
        cashierContext.SetCurrent(CreateCashierSession(Permissions.PosTerminal.Sales.AddItem));
        var order = CreateReceiptPrintOrder(PaymentMethodKind.Card);
        var recovery = new FakeCardPaymentRecoveryService(
            Task.FromResult(new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.OrderCompleted,
                "Recovered approved payment.",
                order)));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            receiptPrintService: printService,
            cardPaymentRecoveryService: recovery,
            cashierSessionContext: cashierContext,
            enforceCashierPermissions: true);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var recovered = await InvokeRecoverCardPaymentAttemptAsync(viewModel, navigateToPaymentOnDraft: false);

        Assert.True(recovered);
        var call = Assert.Single(printService.Calls);
        Assert.Equal(order.OrderGuid, call.OrderGuid);
        Assert.Equal(ReceiptPrintReason.CardAuto, call.Reason);
        Assert.True(viewModel.IsCardRecoveryResultDialogOpen);
        Assert.NotNull(viewModel.CardRecoveryResultDialog);
        Assert.False(viewModel.CardRecoveryResultDialog!.CanPrintReceipt);
        Assert.False(cashierContext.RequirePermission(Permissions.PosTerminal.Receipt.PrintLast, out _));

        await viewModel.PrintRecoveredReceiptCommand.ExecuteAsync(null);
        await Task.Delay(50);
        Assert.Single(printService.Calls);
    }

    [Fact]
    public async Task Card_payment_recovery_draft_restored_during_startup_keeps_pos_screen_and_surfaces_status()
    {
        var cart = new PosCartService();
        var recovery = new FakeCardPaymentRecoveryService((recoveredCart, _, _) =>
        {
            // 模拟恢复服务在返回结果前已经把草稿购物车恢复到当前会话。
            recoveredCart.AddItem(CreateItem("1042", "SKU-RECOVER-STARTUP", "930RECOVERSTARTUP"));
            return Task.FromResult(new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.DraftRestored,
                "Recovered draft during startup."));
        });
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cardPaymentRecoveryService: recovery,
            cart: cart);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var recovered = await InvokeRecoverCardPaymentAttemptAsync(viewModel, navigateToPaymentOnDraft: false);

        Assert.True(recovered);
        Assert.Equal(1, recovery.CallCount);
        Assert.Same(viewModel.PosTerminal, viewModel.CurrentScreen);
        Assert.True(viewModel.IsPosTerminalScreenActive);
        Assert.False(viewModel.IsCashPaymentScreenActive);
        Assert.Equal("Recovered draft during startup.", viewModel.StatusMessage);
        Assert.Single(cart.Lines);
        Assert.True(viewModel.IsCardRecoveryResultDialogOpen);
        Assert.NotNull(viewModel.CardRecoveryResultDialog);
        Assert.Equal("Previous card transaction was not completed", viewModel.CardRecoveryResultDialog!.Title);
        Assert.False(viewModel.CardRecoveryResultDialog.CanPrintReceipt);
    }

    [Fact]
    public async Task Card_payment_recovery_restored_tender_during_startup_opens_payment_screen()
    {
        var cart = new PosCartService();
        var recoveredTender = new PaymentTender(
            PaymentMethodKind.Card,
            5m,
            "ANZ:RECOVERED-PARTIAL",
            IdempotencyKey: "CARD_ATTEMPT:aaaaaaaabbbbccccddddeeeeeeeeeeee");
        var recovery = new FakeCardPaymentRecoveryService((recoveredCart, _, _) =>
        {
            recoveredCart.AddItem(CreateItem("1042", "SKU-RECOVER-PARTIAL", "930RECOVERPARTIAL"));
            return Task.FromResult(new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.DraftRestored,
                "Recovered approved card tender.",
                RestoredTenders: [recoveredTender]));
        });
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cardPaymentRecoveryService: recovery,
            cart: cart);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var recovered = await InvokeRecoverCardPaymentAttemptAsync(viewModel, navigateToPaymentOnDraft: false);

        Assert.True(recovered);
        Assert.Equal(1, recovery.CallCount);
        Assert.Same(viewModel.CashPayment, viewModel.CurrentScreen);
        Assert.True(viewModel.IsCashPaymentScreenActive);
        Assert.False(viewModel.IsPosTerminalScreenActive);
        var tender = Assert.Single(viewModel.CashPayment!.PaymentTenders);
        Assert.Equal(PaymentMethodKind.Card, tender.Method);
        Assert.Equal(5m, tender.Amount);
        Assert.Equal("CARD_ATTEMPT:aaaaaaaabbbbccccddddeeeeeeeeeeee", tender.IdempotencyKey);
        Assert.Equal("Recovered approved card tender.", viewModel.CashPayment.StatusMessage);
        Assert.Single(cart.Lines);
    }

    [Fact]
    public async Task Card_payment_recovery_unknown_result_opens_failure_dialog_without_print_button()
    {
        var recovery = new FakeCardPaymentRecoveryService(
            Task.FromResult(new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                "Manual review required.",
                DialogDetails: new CardPaymentRecoveryDialogDetails(
                    "session-review",
                    "txn-review",
                    "TM",
                    "OPERATOR TIMEOUT",
                    1.25m,
                    new DateTimeOffset(2026, 6, 10, 9, 45, 0, TimeSpan.Zero)))));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cardPaymentRecoveryService: recovery);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var recovered = await InvokeRecoverCardPaymentAttemptAsync(viewModel, navigateToPaymentOnDraft: false);

        Assert.False(recovered);
        Assert.True(viewModel.IsCardRecoveryResultDialogOpen);
        var dialog = viewModel.CardRecoveryResultDialog;
        Assert.NotNull(dialog);
        Assert.Equal("Previous card transaction result is unknown", dialog.Title);
        Assert.Equal("session-review", dialog.SessionId);
        Assert.Equal("txn-review", dialog.TxnRef);
        Assert.Equal("TM", dialog.ResponseCode);
        Assert.False(dialog.CanPrintReceipt);
    }

    [Theory]
    [InlineData(CardPaymentRecoveryOutcome.ActiveSessionApproved, LinklyBankReceiptKind.RecoveredApproved)]
    [InlineData(CardPaymentRecoveryOutcome.ActiveSessionNotPaid, LinklyBankReceiptKind.RecoveredFailed)]
    public async Task Active_card_session_recovery_from_payment_prints_bank_receipt_and_keeps_current_order(
        CardPaymentRecoveryOutcome outcome,
        LinklyBankReceiptKind expectedKind)
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("1042", "SKU-CURRENT-ACTIVE", "930ACTIVESESSION"));
        var bankReceiptPrinter = new RecordingLinklyBankReceiptPrinter();
        var recovery = new FakeCardPaymentRecoveryService(
            Task.FromResult(new CardPaymentRecoveryResult(
                outcome,
                "Previous Linkly transaction resolved.",
                BankReceipt: new CardPaymentRecoveryBankReceipt(
                    "Sandbox",
                    "active-session-1",
                    "RECOVERED BANK RECEIPT",
                    expectedKind,
                    outcome == CardPaymentRecoveryOutcome.ActiveSessionApproved ? "00" : "05",
                    outcome == CardPaymentRecoveryOutcome.ActiveSessionApproved ? "APPROVED" : "DECLINED"))));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cardPaymentRecoveryService: recovery,
            cart: cart,
            linklyBankReceiptPrinter: bankReceiptPrinter);

        var recovered = await InvokeRecoverActiveCardPaymentSessionFromPaymentAsync(viewModel);

        Assert.True(recovered);
        Assert.Equal("Previous Linkly transaction resolved.", viewModel.StatusMessage);
        Assert.Single(cart.Lines);
        Assert.Equal("SKU-CURRENT-ACTIVE", cart.Lines[0].ProductCode);
        var print = Assert.Single(bankReceiptPrinter.Prints);
        Assert.Equal(expectedKind, print.Kind);
        Assert.Equal("RECOVERED BANK RECEIPT", print.ReceiptText);
        Assert.Equal("active-session-1", print.SessionId);
    }

    [Theory]
    [InlineData(true, "did not return receipt text")]
    [InlineData(false, "receipt printer is not available")]
    public async Task Active_card_session_recovery_from_payment_still_continues_when_bank_receipt_cannot_print(
        bool omitBankReceipt,
        string expectedStatus)
    {
        var recovery = new FakeCardPaymentRecoveryService(
            Task.FromResult(new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.ActiveSessionApproved,
                "Previous Linkly transaction resolved.",
                BankReceipt: omitBankReceipt
                    ? null
                    : new CardPaymentRecoveryBankReceipt(
                        "Sandbox",
                        "active-session-1",
                        "RECOVERED BANK RECEIPT",
                        LinklyBankReceiptKind.RecoveredApproved,
                        "00",
                        "APPROVED"))));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cardPaymentRecoveryService: recovery);

        var recovered = await InvokeRecoverActiveCardPaymentSessionFromPaymentAsync(viewModel);

        Assert.True(recovered);
        Assert.Contains(expectedStatus, viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Active_card_session_unknown_from_payment_shows_retry_without_manual_clear()
    {
        var recovery = new FakeCardPaymentRecoveryService(
            Task.FromResult(new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                "Previous Linkly session is still unknown.",
                DialogDetails: new CardPaymentRecoveryDialogDetails(
                    "active-session-unknown",
                    "txn-unknown",
                    null,
                    null,
                    null,
                    DateTimeOffset.Parse("2026-07-01T08:45:26+10:00")))));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cardPaymentRecoveryService: recovery);

        var recoveryTask = StartRecoverActiveCardPaymentSessionFromPaymentAsync(viewModel);
        await WaitUntilAsync(() => viewModel.IsCardRecoveryResultDialogOpen);

        var dialog = viewModel.CardRecoveryResultDialog;
        Assert.NotNull(dialog);
        Assert.True(dialog!.CanRetryRecovery);
        Assert.False(dialog.CanManualConfirm);
        Assert.Equal("Retry recovery", dialog.RetryButtonText);

        viewModel.CloseCardRecoveryResultDialogCommand.Execute(null);

        Assert.False(await recoveryTask);
        Assert.Equal(1, recovery.CallCount);
    }

    [Fact]
    public async Task Active_card_session_retry_from_unknown_queries_again_and_continues_when_resolved()
    {
        var bankReceiptPrinter = new RecordingLinklyBankReceiptPrinter();
        var recovery = new FakeCardPaymentRecoveryService(
            Task.FromResult(new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Unknown,
                "Previous Linkly session is still unknown.",
                DialogDetails: new CardPaymentRecoveryDialogDetails(
                    "active-session-retry",
                    "txn-retry",
                    null,
                    null,
                    null,
                    DateTimeOffset.Parse("2026-07-01T08:45:26+10:00")))),
            Task.FromResult(new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.ActiveSessionApproved,
                "Previous Linkly transaction resolved.",
                BankReceipt: new CardPaymentRecoveryBankReceipt(
                    "Sandbox",
                    "active-session-retry",
                    "RECOVERED BANK RECEIPT",
                    LinklyBankReceiptKind.RecoveredApproved,
                    "00",
                    "APPROVED"))));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cardPaymentRecoveryService: recovery,
            linklyBankReceiptPrinter: bankReceiptPrinter);

        var recoveryTask = StartRecoverActiveCardPaymentSessionFromPaymentAsync(viewModel);
        await WaitUntilAsync(() => viewModel.CardRecoveryResultDialog?.CanRetryRecovery == true);

        viewModel.RetryActiveSessionRecoveryCommand.Execute(null);

        Assert.True(await recoveryTask);
        Assert.Equal(2, recovery.CallCount);
        Assert.False(viewModel.CardRecoveryResultDialog?.CanRetryRecovery == true);
        Assert.Single(bankReceiptPrinter.Prints);
    }

    [Fact]
    public async Task Active_card_session_retry_from_unknown_keeps_dialog_locked_when_still_unknown()
    {
        var firstUnknown = new CardPaymentRecoveryResult(
            CardPaymentRecoveryOutcome.Unknown,
            "Previous Linkly session is still unknown.",
            DialogDetails: new CardPaymentRecoveryDialogDetails(
                "active-session-retry-unknown",
                "txn-retry-unknown",
                null,
                null,
                null,
                DateTimeOffset.Parse("2026-07-01T08:45:26+10:00")));
        var secondUnknown = firstUnknown with { Message = "Previous Linkly session is still unknown after retry." };
        var recovery = new FakeCardPaymentRecoveryService(
            Task.FromResult(firstUnknown),
            Task.FromResult(secondUnknown));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cardPaymentRecoveryService: recovery);

        var recoveryTask = StartRecoverActiveCardPaymentSessionFromPaymentAsync(viewModel);
        await WaitUntilAsync(() => viewModel.CardRecoveryResultDialog?.CanRetryRecovery == true);

        viewModel.RetryActiveSessionRecoveryCommand.Execute(null);

        await WaitUntilAsync(() =>
            recovery.CallCount == 2 &&
            viewModel.CardRecoveryResultDialog?.Message == "Previous Linkly session is still unknown after retry.");
        Assert.True(viewModel.CardRecoveryResultDialog?.CanRetryRecovery);

        viewModel.CloseCardRecoveryResultDialogCommand.Execute(null);

        Assert.False(await recoveryTask);
        Assert.Equal(2, recovery.CallCount);
    }

    [Fact]
    public async Task Active_card_session_checking_does_not_expose_manual_clear_and_keeps_current_order()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("1042", "SKU-CURRENT-MANUAL", "930ACTIVEMANUAL"));
        var recovery = new FakeCardPaymentRecoveryService(
            Task.FromResult(new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.Checking,
                "Previous Linkly session is still pending.",
                DialogDetails: new CardPaymentRecoveryDialogDetails(
                    "active-session-manual",
                    "txn-manual",
                    null,
                    null,
                    null,
                    DateTimeOffset.Parse("2026-07-01T08:45:26+10:00")))));
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cardPaymentRecoveryService: recovery,
            cart: cart);

        var recoveryTask = StartRecoverActiveCardPaymentSessionFromPaymentAsync(viewModel);
        await WaitUntilAsync(() => viewModel.IsCardRecoveryResultDialogOpen);

        Assert.False(viewModel.CardRecoveryResultDialog?.CanManualConfirm);
        viewModel.CloseCardRecoveryResultDialogCommand.Execute(null);

        Assert.False(await recoveryTask);
        Assert.Equal(1, recovery.CallCount);
        Assert.Single(cart.Lines);
        Assert.Equal("SKU-CURRENT-MANUAL", cart.Lines[0].ProductCode);
    }

    [Fact]
    public async Task Card_payment_recovery_draft_restored_is_not_checked_when_opening_payment()
    {
        var cart = new PosCartService();
        cart.AddItem(CreateItem("1042", "SKU-CURRENT-PAYMENT", "930CURRENTPAYMENT"));
        var recovery = new FakeCardPaymentRecoveryService((recoveredCart, _, _) =>
        {
            // 模拟恢复服务把待继续支付的购物车恢复回来，界面应直接收口到支付页。
            recoveredCart.AddItem(CreateItem("1042", "SKU-RECOVER-PAYMENT", "930RECOVERPAYMENT"));
            return Task.FromResult(new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.DraftRestored,
                "Recovered draft for payment."));
        });
        var viewModel = CreateAuthorizedMainViewModel(
            new FakeCustomerDisplayWindowService(),
            cardPaymentRecoveryService: recovery,
            cart: cart);

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));

        viewModel.ShowCashPaymentCommand.Execute(null);

        Assert.Same(viewModel.CashPayment, viewModel.CurrentScreen);
        Assert.True(viewModel.IsCashPaymentScreenActive);
        Assert.False(viewModel.IsPosTerminalScreenActive);
        Assert.Equal(0, recovery.CallCount);
        Assert.Equal("Payment", viewModel.ActivePageTitleText);
        Assert.Single(cart.Lines);
    }

    [Fact]
    public async Task ReregisterDevice_RebindSuccess_ReplacesAuthorizationAndClosesDialog()
    {
        var authorizationState = new DeviceAuthorizationState();
        var deviceApi = new FakeDeviceApiClient
        {
            Stores =
            [
                new StoreSelectionItem("1042", "Old Store", true),
                new StoreSelectionItem("2042", "New Store", true)
            ],
            ActivationPreviewResponse = new DeviceActivationCodePreviewResponse(
                true,
                null,
                "2042",
                "New Store",
                DeviceSystems.Windows,
                DateTime.UtcNow.AddMinutes(15),
                "Ready"),
            ActivationRebindResponse = AllowedActivationResponse("POS-NEW", "2042", "New Store", "AUTH-NEW")
        };
        var priceIndex = new LocalSellableItemIndex();
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        var orderRepository = new FakeLocalOrderRepository();
        var localization = new LocalizationService();
        var catalogRepository = new FakeCatalogRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var deviceRepository = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var fingerprintService = new FakeDeviceFingerprintService();
        var activationRecoveryStore = new InMemoryActivationRecoveryStore();
        var viewModel = new MainViewModel(
            new PosCoreServices(priceIndex, cart, checkout, new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), new FakeRawScannerService(), null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(localization, new FakeSettingsRepository()),
            new ShellCatalogService(priceIndex, catalogRepository, new FakeCatalogSyncService()),
            catalogRepository,
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new MainShellStartupService(deviceRepository, fingerprintService, authorizationState),
            orderRepository,
            new ShellSyncCenterService(syncQueue),
            localization,
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(orderRepository),
            new CashPaymentWorkflowService(checkout, orderRepository, syncQueue),
            new DeviceRegistrationWorkflowService(
                deviceApi,
                deviceRepository,
                fingerprintService,
                activationRecoveryStore: activationRecoveryStore),
            new SpecialProductsWorkflowService(priceIndex, cart, catalogRepository, new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(priceIndex, cart, remoteLookupRefreshAsync, reloadCatalogAsync));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        Assert.NotNull(authorizationState.Current);

        await viewModel.PosTerminal!.ReregisterDeviceCommand.ExecuteAsync(null);
        viewModel.DeviceRegistration!.ActivationCode = DeviceActivationCode;
        await viewModel.DeviceRegistration.PreviewActivationCommand.ExecuteAsync(null);
        await viewModel.DeviceRegistration.ConfirmActivationCommand.ExecuteAsync(null);

        var newAuthorization = Assert.IsType<DeviceAuthorizationContext>(authorizationState.Current);
        Assert.Equal("POS-NEW", newAuthorization.DeviceCode);
        Assert.Equal("2042", newAuthorization.StoreCode);
        Assert.Equal("2042", viewModel.Session.StoreCode);
        Assert.False(viewModel.IsDeviceReregistrationDialogOpen);
        Assert.Null(viewModel.DeviceRegistration);
        Assert.Equal(DeviceActivationCode, deviceApi.LastActivationRebindRequest?.ActivationCode);
        Assert.Null(activationRecoveryStore.Pending);
    }

    [Fact]
    public async Task ReregisterDevice_CancelWhileSubmittingKeepsCurrentAuthorization()
    {
        var authorizationState = new DeviceAuthorizationState();
        var pendingReregister = new TaskCompletionSource<DeviceActivationCodeRedeemResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deviceApi = new FakeDeviceApiClient
        {
            ActivationPreviewResponse = new DeviceActivationCodePreviewResponse(
                true,
                null,
                "2042",
                "New Store",
                DeviceSystems.Windows,
                DateTime.UtcNow.AddMinutes(15),
                "Ready"),
            PendingActivationRebindResponse = pendingReregister
        };
        var priceIndex = new LocalSellableItemIndex();
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        var orderRepository = new FakeLocalOrderRepository();
        var localization = new LocalizationService();
        var catalogRepository = new FakeCatalogRepository();
        var syncQueue = new FakeSyncQueueRepository();
        var deviceRepository = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var fingerprintService = new FakeDeviceFingerprintService();
        var activationRecoveryStore = new InMemoryActivationRecoveryStore();
        var viewModel = new MainViewModel(
            new PosCoreServices(priceIndex, cart, checkout, new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), new FakeRawScannerService(), null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(localization, new FakeSettingsRepository()),
            new ShellCatalogService(priceIndex, catalogRepository, new FakeCatalogSyncService()),
            catalogRepository,
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new MainShellStartupService(deviceRepository, fingerprintService, authorizationState),
            orderRepository,
            new ShellSyncCenterService(syncQueue),
            localization,
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(orderRepository),
            new CashPaymentWorkflowService(checkout, orderRepository, syncQueue),
            new DeviceRegistrationWorkflowService(
                deviceApi,
                deviceRepository,
                fingerprintService,
                activationRecoveryStore: activationRecoveryStore),
            new SpecialProductsWorkflowService(priceIndex, cart, catalogRepository, new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(priceIndex, cart, remoteLookupRefreshAsync, reloadCatalogAsync));

        await viewModel.InitializeAsync(new AppStartupOptions([], false, null, null));
        var originalAuthorization = Assert.IsType<DeviceAuthorizationContext>(authorizationState.Current);

        await viewModel.PosTerminal!.ReregisterDeviceCommand.ExecuteAsync(null);
        var registration = Assert.IsType<DeviceRegistrationViewModel>(viewModel.DeviceRegistration);
        registration.ActivationCode = DeviceActivationCode;
        await registration.PreviewActivationCommand.ExecuteAsync(null);
        var submitTask = registration.ConfirmActivationCommand.ExecuteAsync(null);
        await deviceApi.WaitForActivationRebindStartedAsync();

        Assert.True(registration.IsActivationRecoveryPending);
        Assert.False(registration.CancelCommand.CanExecute(null));

        registration.CancelCommand.Execute(null);
        var secondActivationCode = DeviceActivationCode[..^1] + "0";
        Assert.True(registration.ProcessScannerBarcode(secondActivationCode, "scanner-2", "raw"));

        Assert.True(viewModel.IsDeviceReregistrationDialogOpen);
        Assert.Same(registration, viewModel.DeviceRegistration);
        Assert.Equal(DeviceActivationCode, registration.ActivationCode);

        pendingReregister.SetException(new HttpRequestException("Response lost after submit"));
        await submitTask;
        Assert.True(registration.ProcessScannerBarcode(secondActivationCode, "scanner-2", "raw"));

        Assert.Same(originalAuthorization, authorizationState.Current);
        Assert.NotNull(viewModel.PosTerminal);
        Assert.True(viewModel.IsDeviceReregistrationDialogOpen);
        Assert.Same(registration, viewModel.DeviceRegistration);
        Assert.True(registration.IsActivationRecoveryPending);
        Assert.False(registration.CancelCommand.CanExecute(null));
        Assert.Equal(DeviceActivationCode, registration.ActivationCode);
        Assert.True(registration.ConfirmActivationCommand.CanExecute(null));
        await registration.ConfirmActivationCommand.ExecuteAsync(null);
        Assert.Equal(DeviceActivationCode, deviceApi.LastActivationRebindRequest?.ActivationCode);
        Assert.Equal(DeviceActivationRecoveryMode.Rebind, activationRecoveryStore.Pending?.Mode);
    }

    private static LocalDeviceCache CreateAllowedDevice(string storeCode)
    {
        return new LocalDeviceCache(
            "POS-001",
            storeCode,
            "Main Store",
            "HW-001",
            1,
            true,
            null,
            DateTimeOffset.UtcNow,
            "AUTH-001");
    }

    private static DeviceActivationCodeRedeemResponse AllowedActivationResponse(
        string deviceCode,
        string storeCode,
        string storeName,
        string authorizationCode)
    {
        return new DeviceActivationCodeRedeemResponse(
            deviceCode,
            storeCode,
            storeName,
            1,
            true,
            "Enabled",
            authorizationCode,
            DeviceActivationReasonCodes.Activated);
    }

    private static SellableItemDto CreateItem(string storeCode, string productCode, string lookupCode)
    {
        return new SellableItemDto(
            storeCode,
            productCode,
            null,
            "Test Item",
            lookupCode,
            null,
            lookupCode,
            9.9m,
            PriceSourceKind.StoreRetailPrice,
            "Store price",
            1m,
            DateTimeOffset.UtcNow,
            null);
    }

    private static SellableItemDto CreateSpecialItem(
        string storeCode,
        string productCode,
        string lookupCode,
        string imageBaseUrl)
    {
        return CreateItem(storeCode, productCode, lookupCode) with
        {
            ProductImage = $"{imageBaseUrl}/{productCode}.jpg",
            IsSpecialProduct = true
        };
    }

    private static CashierSessionDto CreateCashierSession(params string[] permissionCodes) =>
        new(
            "CASHIER-1",
            "user-1",
            "Alice",
            "1042",
            "POS-01",
            ["Cashier"],
            permissionCodes,
            ["1042"],
            IsSuperAdmin: false,
            IsOfflineCached: false,
            IsEmergencyOverride: false);

    private static MainViewModel CreateAuthorizedMainViewModel(
        FakeCustomerDisplayWindowService customerDisplayWindow,
        IReceiptPrintService? receiptPrintService = null,
        FakeSyncQueueRepository? syncQueueRepository = null,
        IOrderUploadExecutionService? orderUploadExecutionService = null,
        ICashDrawerService? cashDrawerService = null,
        IApplicationExitService? applicationExitService = null,
        IConfirmationDialogService? confirmationDialogService = null,
        IConnectivityApiClient? connectivityApiClient = null,
        ISpecialProductsWorkflowService? specialProductsWorkflowService = null,
        ICardPaymentRecoveryService? cardPaymentRecoveryService = null,
        PosCartService? cart = null,
        IRawScannerService? rawScannerService = null,
        ICashierSessionContext? cashierSessionContext = null,
        ICashierLoginService? cashierLoginService = null,
        IPosRuntimeStatusApiClient? runtimeStatusApiClient = null,
        ILinklyBankReceiptPrinter? linklyBankReceiptPrinter = null,
        IInstallmentOrderService? installmentOrderService = null,
        IOperationAuditLogger? operationAuditLogger = null,
        IOperationAuthorizationService? operationAuthorizationService = null,
        IUserFeedbackService? userFeedbackService = null,
        IMainShellStartupService? mainShellStartupService = null,
        bool enforceCashierPermissions = false,
        ILinklySettlementUploadQueueReader? linklySettlementUploadQueueReader = null,
        ILinklySettlementUploadExecutionService? linklySettlementUploadExecutionService = null,
        IRemoteOrderHistoryService? remoteOrderHistoryService = null,
        IDeviceRegistrationWorkflowService? deviceRegistrationWorkflowService = null,
        IPaymentMethodSettingsService? paymentMethodSettingsService = null)
    {
        var priceIndex = new LocalSellableItemIndex();
        var effectiveCart = cart ?? new PosCartService();
        var checkout = new CashCheckoutService();
        var catalogRepository = new FakeCatalogRepository();
        var syncQueue = syncQueueRepository ?? new FakeSyncQueueRepository();
        var orderRepository = new FakeLocalOrderRepository();
        var localization = new LocalizationService();
        var deviceRepository = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var fingerprintService = new FakeDeviceFingerprintService();
        return new MainViewModel(
            new PosCoreServices(priceIndex, effectiveCart, checkout, new FakeLocalSchemaService()),
            new PosInfrastructureFacade(
                connectivityApiClient ?? new FakeConnectivityApiClient(),
                rawScannerService ?? new FakeRawScannerService(),
                userFeedbackService: userFeedbackService,
                applicationExitService: applicationExitService,
                confirmationDialogService: confirmationDialogService),
            new PaymentTerminalFacade(
                voucherApiClient: null,
                cardTerminalClient: null,
                cardTerminalSetupService: null,
                linklyTerminalDialogPresenter: null,
                cardPaymentRecoveryService: cardPaymentRecoveryService,
                cardRecoveryResultDialogService: null,
                linklyFallbackPromptCoordinator: null),
            new PrintFacade(receiptPrintService, receiptPrinterSettingsStore: null, receiptTextFormatter: null, linklyBankReceiptPrinter: linklyBankReceiptPrinter),
            new ShellCultureService(localization, new FakeSettingsRepository()),
            new ShellCatalogService(priceIndex, catalogRepository, new FakeCatalogSyncService()),
            catalogRepository,
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            mainShellStartupService ?? new MainShellStartupService(
                deviceRepository,
                fingerprintService,
                new DeviceAuthorizationState()),
            orderRepository,
            new ShellSyncCenterService(syncQueue),
            localization,
            new CustomerDisplayOrchestrator(customerDisplayWindow),
            new ReceiptQueryService(orderRepository),
            new CashPaymentWorkflowService(checkout, orderRepository, syncQueue),
            deviceRegistrationWorkflowService
                ?? new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), deviceRepository, fingerprintService),
            specialProductsWorkflowService ?? new SpecialProductsWorkflowService(priceIndex, effectiveCart, catalogRepository, new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                priceIndex,
                effectiveCart,
                remoteLookupRefreshAsync,
                reloadCatalogAsync),
            orderUploadExecutionService: orderUploadExecutionService,
            cashDrawerService: cashDrawerService,
            installmentOrderService: installmentOrderService,
            cashierSessionContext: cashierSessionContext,
            cashierLoginService: cashierLoginService,
            runtimeStatusApiClient: runtimeStatusApiClient,
            operationAuditLogger: operationAuditLogger,
            operationAuthorizationService: operationAuthorizationService,
            enforceCashierPermissions: enforceCashierPermissions,
            linklySettlementUploadQueueReader: linklySettlementUploadQueueReader,
            linklySettlementUploadExecutionService: linklySettlementUploadExecutionService,
            remoteOrderHistoryService: remoteOrderHistoryService,
            paymentMethodSettingsService: paymentMethodSettingsService);
    }

    private static MainViewModel CreateMainViewModelWithShellCatalog(
        FakeCatalogRepository catalogRepository,
        IShellCatalogService shellCatalogService,
        IConnectivityApiClient connectivityApiClient,
        IEnumerable<SellableItemDto>? indexedItems = null)
    {
        var localization = new LocalizationService();
        var priceIndex = new LocalSellableItemIndex();
        priceIndex.ReplaceAll(indexedItems ?? []);
        var cart = new PosCartService();
        var checkout = new CashCheckoutService();
        var deviceRepository = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var fingerprintService = new FakeDeviceFingerprintService();
        var orderRepository = new FakeLocalOrderRepository();
        var syncQueue = new FakeSyncQueueRepository();

        return new MainViewModel(
            new PosCoreServices(priceIndex, cart, checkout, new FakeLocalSchemaService()),
            new PosInfrastructureFacade(connectivityApiClient, new FakeRawScannerService(), null, null, null),
            new PaymentTerminalFacade(null, null, null, null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(localization, new FakeSettingsRepository()),
            shellCatalogService,
            catalogRepository,
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new MainShellStartupService(deviceRepository, fingerprintService, new DeviceAuthorizationState()),
            orderRepository,
            new ShellSyncCenterService(syncQueue),
            localization,
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(orderRepository),
            new CashPaymentWorkflowService(checkout, orderRepository, syncQueue),
            new DeviceRegistrationWorkflowService(new FakeDeviceApiClient(), deviceRepository, fingerprintService),
            new SpecialProductsWorkflowService(priceIndex, cart, catalogRepository, new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                priceIndex,
                cart,
                remoteLookupRefreshAsync,
                reloadCatalogAsync));
    }

    private static async Task<bool> InvokeRecoverCardPaymentAttemptAsync(
        MainViewModel viewModel,
        bool navigateToPaymentOnDraft)
    {
        var method = typeof(MainViewModel).GetMethod(
            "RecoverCardPaymentAttemptAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(bool)],
            modifiers: null);
        Assert.NotNull(method);
        var task = (Task<bool>)method!.Invoke(viewModel, [navigateToPaymentOnDraft])!;
        return await task;
    }

    private static async Task InvokeHandleCardRecoveryCenterResultAsync(
        MainViewModel viewModel,
        CardRecoveryAttemptKey selectedKey,
        CardPaymentRecoveryResult result)
    {
        var method = typeof(MainViewModel).GetMethod(
            "HandleCardRecoveryCenterResultAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(CardRecoveryAttemptKey), typeof(CardPaymentRecoveryResult)],
            modifiers: null);
        Assert.NotNull(method);
        await (Task)method!.Invoke(viewModel, [selectedKey, result])!;
    }

    private static Task InvokeHandleCardRecoveryCenterResultAsync(
        MainViewModel viewModel,
        CardPaymentRecoveryResult result)
    {
        var selectedKey = viewModel.CachedCashPaymentScreen?
            .CreateCardPaymentHandoffRequest()
            .RecoveryAttemptKey ??
            new CardRecoveryAttemptKey(
                CardProcessorKind.Linkly,
                Guid.Parse("40000000-0000-0000-0000-000000000199"));
        return InvokeHandleCardRecoveryCenterResultAsync(viewModel, selectedKey, result);
    }

    private sealed class RecordingOperationAuditLogger : IOperationAuditLogger
    {
        public List<OperationAuditEventDto> Events { get; } = [];

        public void Record(OperationAuditEventDto auditEvent)
        {
            Events.Add(auditEvent);
        }
    }

    private static async Task<bool> InvokeRecoverActiveCardPaymentSessionFromPaymentAsync(MainViewModel viewModel)
    {
        return await StartRecoverActiveCardPaymentSessionFromPaymentAsync(viewModel);
    }

    private static Task<bool> StartRecoverActiveCardPaymentSessionFromPaymentAsync(MainViewModel viewModel)
    {
        var presenterField = typeof(MainViewModel).GetField(
            "_cardRecoveryPresenter",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(presenterField);
        var presenter = presenterField!.GetValue(viewModel);
        Assert.NotNull(presenter);
        var method = presenter!.GetType().GetMethod(
            "RecoverActiveCardPaymentSessionFromPaymentAsync",
            BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);
        var task = (Task<bool>)method!.Invoke(presenter, [])!;
        return task;
    }

    private static async Task<bool> InvokeRefreshOnlineStateAsync(MainViewModel viewModel)
    {
        var method = typeof(MainViewModel).GetMethod(
            "RefreshOnlineStateAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(CancellationToken)],
            modifiers: null);
        Assert.NotNull(method);
        var task = (Task<bool>)method!.Invoke(viewModel, [CancellationToken.None])!;
        return await task;
    }

    private static async Task<bool> InvokeRefreshOnlineStateAsync(MainViewModel viewModel, bool autoRetryOrders)
    {
        var method = typeof(MainViewModel).GetMethod(
            "RefreshOnlineStateAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(CancellationToken), typeof(bool)],
            modifiers: null);
        Assert.NotNull(method);
        var task = (Task<bool>)method!.Invoke(viewModel, [CancellationToken.None, autoRetryOrders])!;
        return await task;
    }

    private static async Task InvokeShowInstallmentRepaymentAsync(MainViewModel viewModel, InstallmentOrderSummary order)
    {
        var navigatorField = typeof(MainViewModel).GetField("_screenNavigator", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(navigatorField);
        var navigator = navigatorField!.GetValue(viewModel);
        Assert.NotNull(navigator);
        var method = navigator!.GetType().GetMethod("ShowInstallmentRepaymentAsync", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);
        var task = (Task)method!.Invoke(navigator, [order])!;
        await task;
    }

    private static CardPaymentSession GetCardPaymentSession(PaymentViewModel payment)
    {
        var field = typeof(PaymentViewModel).GetField(
            "_cardSession",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<CardPaymentSession>(field!.GetValue(payment));
    }

    private static MainViewModel CreateAuthorizedMainViewModelWithPaymentWorkflow(
        PosCartService cart,
        CashCheckoutService checkout,
        ILocalOrderRepository orderRepository,
        ISyncQueueRepository syncQueue,
        ICardTerminalClient cardTerminalClient,
        ICardPaymentRecoveryService? cardPaymentRecoveryService = null)
    {
        var priceIndex = new LocalSellableItemIndex();
        var catalogRepository = new FakeCatalogRepository();
        var localization = new LocalizationService();
        var workflow = new CashPaymentWorkflowService(
            checkout,
            orderRepository,
            syncQueue,
            cardTerminalClient: cardTerminalClient);

        return new MainViewModel(
            new PosCoreServices(priceIndex, cart, checkout, new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), new FakeRawScannerService(), null, null, null),
            new PaymentTerminalFacade(null, cardTerminalClient, null, null, cardPaymentRecoveryService, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(localization, new FakeSettingsRepository()),
            new ShellCatalogService(priceIndex, catalogRepository, new FakeCatalogSyncService()),
            catalogRepository,
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new MainShellStartupService(
                new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") },
                new FakeDeviceFingerprintService(),
                new DeviceAuthorizationState()),
            orderRepository,
            new ShellSyncCenterService(syncQueue),
            localization,
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(orderRepository),
            workflow,
            new DeviceRegistrationWorkflowService(
                new FakeDeviceApiClient(),
                new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") },
                new FakeDeviceFingerprintService()),
            new SpecialProductsWorkflowService(priceIndex, cart, catalogRepository, new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                priceIndex,
                cart,
                remoteLookupRefreshAsync,
                reloadCatalogAsync),
            operationAuthorizationService: cardPaymentRecoveryService is null
                ? null
                : new GrantingOperationAuthorizationService());
    }

    private static MainViewModel CreateAuthorizedMainViewModelWithSettings(
        FakeDeviceApiClient? deviceApiClient = null,
        FakeSyncQueueRepository? syncQueueRepository = null,
        ApiServerSettingsViewModel? apiServerSettings = null,
        FakeSettingsRepository? settingsRepository = null)
    {
        settingsRepository ??= new FakeSettingsRepository();
        var catalogRepository = new FakeCatalogRepository();
        var orderRepository = new FakeLocalOrderRepository();
        var deviceRepository = new FakeLocalDeviceRepository { Latest = CreateAllowedDevice("1042") };
        var fingerprintService = new FakeDeviceFingerprintService();
        var deviceApi = deviceApiClient ?? new FakeDeviceApiClient();
        var localization = new LocalizationService();
        var cart = new PosCartService();
        var priceIndex = new LocalSellableItemIndex();
        var checkout = new CashCheckoutService();
        var syncQueue = syncQueueRepository ?? new FakeSyncQueueRepository();

        return new MainViewModel(
            new PosCoreServices(priceIndex, cart, checkout, new FakeLocalSchemaService()),
            new PosInfrastructureFacade(new FakeConnectivityApiClient(), new FakeRawScannerService(), null, null, null),
            new PaymentTerminalFacade(null, null, new FakeCardTerminalSetupService(), null, null, null, null),
            new PrintFacade(null, null, null),
            new ShellCultureService(localization, settingsRepository),
            new ShellCatalogService(priceIndex, catalogRepository, new FakeCatalogSyncService()),
            catalogRepository,
            new FakeRemoteLookupRefreshService(),
            new FakeSpecialProductService(),
            new MainShellStartupService(deviceRepository, fingerprintService, new DeviceAuthorizationState()),
            orderRepository,
            new ShellSyncCenterService(syncQueue),
            localization,
            new CustomerDisplayOrchestrator(new FakeCustomerDisplayWindowService()),
            new ReceiptQueryService(orderRepository),
            new CashPaymentWorkflowService(checkout, orderRepository, syncQueue),
            new DeviceRegistrationWorkflowService(deviceApi, deviceRepository, fingerprintService),
            new SpecialProductsWorkflowService(priceIndex, cart, catalogRepository, new FakeSpecialProductService()),
            (remoteLookupRefreshAsync, reloadCatalogAsync) => new PosTerminalWorkflowService(
                priceIndex,
                cart,
                remoteLookupRefreshAsync,
                reloadCatalogAsync),
            apiServerSettings: apiServerSettings);
    }

    private static SyncQueueListItem CreateSyncQueueItem(Guid entityId, string status, string? errorMessage = null)
    {
        return new SyncQueueListItem(
            entityId,
            "Order",
            status,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            errorMessage,
            12.30m);
    }

    private static LocalOrder CreateReceiptPrintOrder(params PaymentMethodKind[] paymentMethods)
    {
        var orderGuid = Guid.NewGuid();
        var methods = paymentMethods.Length == 0 ? [PaymentMethodKind.Cash] : paymentMethods;
        var paymentAmount = decimal.Round(10m / methods.Length, 2, MidpointRounding.AwayFromZero);
        return new LocalOrder(
            orderGuid,
            "1042",
            "POS-01",
            "C001",
            "Alice",
            DateTimeOffset.UtcNow,
            10m,
            0m,
            10m,
            [
                new LocalOrderLine(
                    Guid.NewGuid(),
                    "SKU-001",
                    null,
                    "Receipt Item",
                    "930110",
                    "ITEM-1",
                    1m,
                    10m,
                    0m,
                    10m,
                    PriceSourceKind.StoreRetailPrice)
            ],
            methods
                .Select(paymentMethod => new LocalPayment(
                    Guid.NewGuid(),
                    paymentMethod,
                    paymentAmount,
                    paymentMethod == PaymentMethodKind.Card ? "CARD-123" : null,
                    paymentMethod == PaymentMethodKind.Card
                        ? [
                            new CardTransactionDto(
                                "Linkly",
                                "TXN-1",
                                "AUTH-1",
                                "VISA",
                                411111,
                                "****1111",
                                "M1",
                                "00",
                                "APPROVED",
                                "123456",
                                DateTimeOffset.UtcNow,
                                paymentAmount,
                                "APPROVED CARD RECEIPT")
                        ]
                        : null))
                .ToArray());
    }

    private static bool IsShowingCompletedSale(MainViewModel viewModel) =>
        viewModel.PosTerminal is { } pos &&
        ReferenceEquals(viewModel.CurrentScreen, pos) &&
        pos.IsLastSaleVisible &&
        ReferenceEquals(pos.LastSale, viewModel.PaymentSuccess);

    private static void InvokePaymentCompleted(MainViewModel viewModel, LocalOrder order, bool hasPostCommitWarning = false)
    {
        var method = typeof(MainViewModel).GetMethod("OnPaymentCompleted", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(viewModel, [null, new PaymentCompletedEventArgs(order, order.ActualAmount, 0m, hasPostCommitWarning)]);
    }

    private static byte[] OnePixelPngBytes()
    {
        return Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4////fwAJ+wP9KobjigAAAABJRU5ErkJggg==");
    }

    private static async Task InvokePrivateTaskAsync(object target, string methodName)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = method!.Invoke(target, null) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private static void ClearImageCacheForTests()
    {
        ClearConcurrentDictionaryField("Cache");
        ClearConcurrentDictionaryField("FailedCache");
        ClearConcurrentDictionaryField("LoggedDiagnostics");
    }

    private static int GetImageCacheCountForTests()
    {
        var field = typeof(ProductThumbnailImageSourceConverter).GetField("Cache", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var countProperty = field!.FieldType.GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(countProperty);
        return (int)countProperty!.GetValue(field.GetValue(null))!;
    }

    private static bool ImageCacheContainsForTests(string sourceText)
    {
        var field = typeof(ProductThumbnailImageSourceConverter).GetField("Cache", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);

        var cache = field!.GetValue(null);
        var containsKeyMethod = field.FieldType.GetMethod("ContainsKey", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(containsKeyMethod);
        return (bool)containsKeyMethod!.Invoke(cache, [$"72|{sourceText}"])!;
    }

    private static void ClearConcurrentDictionaryField(string fieldName)
    {
        var field = typeof(ProductThumbnailImageSourceConverter).GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(field);

        var clearMethod = field!.FieldType.GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(clearMethod);
        clearMethod!.Invoke(field.GetValue(null), null);
    }

    private sealed class FakeRawScannerService : IRawScannerService
    {
        private readonly Dictionary<string, Action<RawBarcodeScannedEventArgs>> _handlers = [];
        private Func<RawBarcodeScannedEventArgs, bool>? _globalBarcodeInterceptor;
        private readonly List<string> _interceptedBarcodes = [];

        public bool IsActive { get; private set; }

        public int ResetCount { get; private set; }

        public string? ActivePageId { get; private set; }

        public IReadOnlyList<string> InterceptedBarcodes => _interceptedBarcodes;

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public void Subscribe(string pageId, Action<RawBarcodeScannedEventArgs> handler)
        {
            _handlers[pageId] = handler;
        }

        public void Unsubscribe(string pageId)
        {
            _handlers.Remove(pageId);
        }

        public void SetActivePage(string? pageId)
        {
            ActivePageId = pageId;
        }

        public void SetGlobalBarcodeInterceptor(Func<RawBarcodeScannedEventArgs, bool>? interceptor)
        {
            _globalBarcodeInterceptor = interceptor;
        }

        public void Start(IntPtr hwnd)
        {
            IsActive = true;
        }

        public void Stop()
        {
            IsActive = false;
        }

        public void ClearPendingInput()
        {
        }

        public Task ResetBindingAsync(CancellationToken cancellationToken = default)
        {
            ResetCount++;
            return Task.CompletedTask;
        }

        public IntPtr ProcessWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            return IntPtr.Zero;
        }

        public void Emit(string barcode, DateTimeOffset? scannedAt = null)
        {
            var args = new RawBarcodeScannedEventArgs(
                barcode,
                "scanner-device",
                scannedAt ?? DateTimeOffset.Now);
            if (_globalBarcodeInterceptor?.Invoke(args) == true)
            {
                _interceptedBarcodes.Add(barcode);
                return;
            }

            if (ActivePageId is not null && _handlers.TryGetValue(ActivePageId, out var handler))
            {
                handler(args);
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingScannerTarget : IScannerInputTarget
    {
        public string ScannerPageId => "RecordingScannerTarget";

        public List<string> Barcodes { get; } = [];

        public bool ProcessScannerBarcode(string barcode, string devicePath, string source)
        {
            Barcodes.Add(barcode);
            return true;
        }
    }

    private sealed class GrantingOperationAuthorizationService : IOperationAuthorizationService
    {
        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

        public event EventHandler? StatusChanged
        {
            add { }
            remove { }
        }

        public List<(string PermissionCode, string Screen, string Action)> Requests { get; } = [];
        public string ScannerPageId => "CardRecoveryAuthorizationTest";
        public bool IsPromptOpen => false;
        public bool IsBusy => false;
        public string PromptMessage => string.Empty;
        public string StatusMessage => string.Empty;
        public string PermissionCode => string.Empty;
        public string Screen => string.Empty;
        public string Action => string.Empty;
        public IRelayCommand CancelCommand { get; } = new RelayCommand(() => { });

        public Task<OperationAuthorizationScope?> AuthorizeAsync(
            string permissionCode,
            string screen,
            string action,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((permissionCode, screen, action));
            if (session.CashierSession is null)
            {
                return Task.FromResult<OperationAuthorizationScope?>(null);
            }

            var scope = new OperationAuthorizationScope(
                session.CashierSession,
                permissionCode,
                screen,
                action);
            scope.SetAuthorizingSession(session.CashierSession);
            return Task.FromResult<OperationAuthorizationScope?>(scope);
        }

        public bool ProcessScannerBarcode(string barcode) => false;
        public void Cancel() { }
        public void RevokeAll() { }
    }

    private sealed class FakeOperationAuthorizationService : IOperationAuthorizationService
    {
        private bool _isPromptOpen;

        public FakeOperationAuthorizationService()
        {
            CancelCommand = new RelayCommand(Cancel);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public event EventHandler? StatusChanged;

        public string ScannerPageId => "OperationAuthorizationTest";

        public bool IsPromptOpen => _isPromptOpen;

        public bool IsBusy => false;

        public string PromptMessage => string.Empty;

        public string StatusMessage => string.Empty;

        public string PermissionCode => Permissions.PosTerminal.Sales.AddItem;

        public string Screen => "POS";

        public string Action => "Add item";

        public IRelayCommand CancelCommand { get; }

        public List<string> Barcodes { get; } = [];

        public int CancelCount { get; private set; }

        public Task<OperationAuthorizationScope?> AuthorizeAsync(
            string permissionCode,
            string screen,
            string action,
            PosSessionState session,
            CancellationToken cancellationToken = default) => Task.FromResult<OperationAuthorizationScope?>(null);

        public bool ProcessScannerBarcode(string barcode)
        {
            Barcodes.Add(barcode);
            return true;
        }

        public void Cancel()
        {
            if (!_isPromptOpen)
            {
                return;
            }

            CancelCount++;
            Close();
        }

        public void RevokeAll() => Cancel();

        public void Open()
        {
            _isPromptOpen = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPromptOpen)));
        }

        public void Close()
        {
            _isPromptOpen = false;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPromptOpen)));
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeLocalSchemaService : ILocalSchemaService
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSettingsRepository : ILocalAppSettingsRepository
    {
        public Exception? SetException { get; init; }

        public Exception? FirstSetException { get; init; }

        public TaskCompletionSource? FirstSetStarted { get; init; }

        public TaskCompletionSource? ReleaseFirstSet { get; init; }

        public List<string> SetValues { get; } = [];

        public string? LastPersistedValue { get; private set; }

        private int _setCount;

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }

        public async Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            SetValues.Add(value);
            var setCount = Interlocked.Increment(ref _setCount);
            if (setCount == 1)
            {
                FirstSetStarted?.TrySetResult();
                if (ReleaseFirstSet is not null)
                {
                    await ReleaseFirstSet.Task;
                }
            }

            if (setCount == 1 && FirstSetException is not null)
            {
                throw FirstSetException;
            }

            if (SetException is not null)
            {
                throw SetException;
            }

            LastPersistedValue = value;
        }

        public Task SetValuesAsync(
            IReadOnlyDictionary<string, string> values,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCashierLoginService(CashierSessionDto session) : ICashierLoginService
    {
        public Task<CashierLoginResult> LoginAsync(
            string storeCode,
            string deviceCode,
            string userBarcode,
            bool attemptOnline = true,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CashierLoginResult.Success(session));
        }
    }

    private sealed class FixedCashierSessionRefreshApiClient(CashierSessionRefreshAttempt attempt)
        : ICashierSessionRefreshApiClient
    {
        public Task<CashierSessionRefreshAttempt> RefreshAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(attempt);
    }

    private sealed class NoopCashierSessionCacheUpdater : ICashierSessionCacheUpdater
    {
        public Task UpdateCachedSessionAsync(
            CashierSessionDto session,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveCachedSessionAsync(
            CashierSessionDto session,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ThrowingCashierSessionCacheUpdater(Exception exception)
        : ICashierSessionCacheUpdater
    {
        public Task UpdateCachedSessionAsync(
            CashierSessionDto session,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveCachedSessionAsync(
            CashierSessionDto session,
            CancellationToken cancellationToken = default) => Task.FromException(exception);
    }

    private sealed class RecordingAttemptCashierLoginService(CashierLoginResult result) : ICashierLoginService
    {
        public bool? AttemptOnline { get; private set; }

        public Task<CashierLoginResult> LoginAsync(
            string storeCode,
            string deviceCode,
            string userBarcode,
            bool attemptOnline = true,
            CancellationToken cancellationToken = default)
        {
            AttemptOnline = attemptOnline;
            return Task.FromResult(result);
        }
    }

    private sealed class FixedCashierLoginService(CashierLoginResult result) : ICashierLoginService
    {
        public Task<CashierLoginResult> LoginAsync(
            string storeCode,
            string deviceCode,
            string userBarcode,
            bool attemptOnline = true,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(result);
        }
    }

    private sealed class FailedCashierLoginService(Exception? exception = null) : ICashierLoginService
    {
        public Task<CashierLoginResult> LoginAsync(
            string storeCode,
            string deviceCode,
            string userBarcode,
            bool attemptOnline = true,
            CancellationToken cancellationToken = default)
        {
            if (exception is not null)
            {
                return Task.FromException<CashierLoginResult>(exception);
            }

            return Task.FromResult(CashierLoginResult.Fail("Login denied"));
        }
    }

    private sealed class FakeCardTerminalSetupService : ICardTerminalSetupService
    {
        public Task<CardTerminalConfiguration> LoadConfigurationAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CardTerminalConfiguration.Default);
        }

        public Task<string?> GetSquareAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<IReadOnlyList<SquareLocationOption>> ListSquareLocationsAsync(
            string? accessToken,
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SquareLocationOption>>([]);
        }

        public Task<IReadOnlyList<SquareDeviceOption>> ListSquareDevicesAsync(
            string? accessToken,
            CardTerminalEnvironment environment,
            string locationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SquareDeviceOption>>([]);
        }

        public Task<IReadOnlyList<SquareDeviceCodeOption>> ListSquareDeviceCodesAsync(
            string? accessToken,
            CardTerminalEnvironment environment,
            string locationId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SquareDeviceCodeOption>>([]);
        }

        public Task<SquareDeviceCodeOption> CreateSquareDeviceCodeAsync(
            string? accessToken,
            CardTerminalEnvironment environment,
            string locationId,
            string name,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<SquareDeviceCodeOption> GetSquareDeviceCodeAsync(
            string? accessToken,
            CardTerminalEnvironment environment,
            string deviceCodeId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task SaveSquareAsync(
            CardTerminalConfiguration configuration,
            string? squareAccessToken,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SaveLinklyAsync(
            CardTerminalConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<LinklyConnectionTestResult> TestLinklyConnectionAsync(
            string host,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LinklyConnectionTestResult(false, "not tested"));
        }

        public Task<LinklyConnectionTestResult> PairLinklyCloudAsync(
            CardTerminalEnvironment environment,
            string pairCode,
            string? username,
            string? password,
            bool syncBackendTerminalCredential = false,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LinklyConnectionTestResult(false, "not tested"));
        }

        public Task<LinklyCloudCredentialSettings> LoadLinklyCloudCredentialAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LinklyCloudCredentialSettings(null, null, false));
        }

        public Task SaveLinklyCloudCredentialAsync(
            CardTerminalEnvironment environment,
            string username,
            string password,
            bool syncBackendCredential = false,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<LinklyConnectionTestResult> TestLinklyCloudConnectionAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LinklyConnectionTestResult(false, "not tested"));
        }

        public Task<LinklyConnectionTestResult> TestLinklyCloudBackendConnectionAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LinklyConnectionTestResult(false, "not tested"));
        }

        public Task<LinklyConnectionTestResult> TestLinklyCloudBackendTransactionStatusAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LinklyConnectionTestResult(false, "not tested"));
        }

        public Task<bool> HasLinklyCloudSecretAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task SaveLinklyCloudAsync(
            CardTerminalConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class ApprovedCardTerminalClient(string reference) : ICardTerminalClient
    {
        public Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PaymentAuthorizationResult(true, reference, AuthorizedAmount: amount));
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PaymentAuthorizationResult(true, $"REFUND:{originalReference}", AuthorizedAmount: amount));
        }
    }

    private sealed class UnknownCardTerminalClient : ICardTerminalClient
    {
        public Task<PaymentAuthorizationResult> AuthorizeAsync(
            decimal amount,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PaymentAuthorizationResult(
                false,
                Message: "Card result is unknown.",
                ResultUnknown: true));
        }

        public Task<PaymentAuthorizationResult> RefundAsync(
            decimal amount,
            PosSessionState session,
            string? originalReference,
            CancellationToken cancellationToken = default)
        {
            return AuthorizeAsync(amount, session, cancellationToken);
        }
    }

    private sealed class FakeCatalogRepository : ILocalCatalogRepository
    {
        public IReadOnlyList<SellableItemDto> Items { get; init; } = [];

        public IReadOnlyList<SellableItemDto> SpecialItems { get; init; } = [];

        public Exception? LoadSellableItemsException { get; init; }

        public int LoadSellableItemsCallCount { get; private set; }

        public int LoadSpecialProductItemsCallCount { get; private set; }

        public Func<Task>? BeforeLoadSellableItemsAsync { get; init; }

        public Func<Task>? BeforeLoadSpecialProductItemsAsync { get; init; }

        public Task ReplaceSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task UpsertSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<int> DeleteByLookupCodesAsync(string storeCode, IEnumerable<string> lookupCodes, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<SellableItemDto?> FindByLookupCodeAsync(string storeCode, string lookupCode, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SellableItemDto?>(null);
        }

        public Task<IReadOnlyList<SellableItemDto>> LoadSpecialProductItemsAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            LoadSpecialProductItemsCallCount++;
            if (BeforeLoadSpecialProductItemsAsync is not null)
            {
                return LoadSpecialProductItemsCoreAsync();
            }

            return Task.FromResult<IReadOnlyList<SellableItemDto>>(SpecialItems);

            async Task<IReadOnlyList<SellableItemDto>> LoadSpecialProductItemsCoreAsync()
            {
                await BeforeLoadSpecialProductItemsAsync();
                return SpecialItems;
            }
        }

        public Task SaveSpecialProductOrderAsync(
            string storeCode,
            IEnumerable<string> productCodes,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<int> UpdateSpecialProductFlagAsync(
            string storeCode,
            string productCode,
            bool isSpecialProduct,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<int> ClearSpecialProductFlagsExceptAsync(
            string storeCode,
            IEnumerable<string> productCodesToKeep,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<IReadOnlyList<LocalSellableItemCompareRow>> LoadSellableItemComparePageAsync(
            string storeCode,
            string? afterLookupCodeNormalized,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            var rows = Items
                .Where(item => string.Equals(item.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase))
                .Select(item => new LocalSellableItemCompareRow(
                    item.StoreCode,
                    item.LookupCode,
                    item.ProductCode,
                    item.UpdatedAt))
                .OrderBy(row => row.LookupCodeNormalized, StringComparer.Ordinal)
                .Where(row => string.IsNullOrWhiteSpace(afterLookupCodeNormalized)
                    || string.Compare(row.LookupCodeNormalized, afterLookupCodeNormalized, StringComparison.Ordinal) > 0)
                .Take(pageSize)
                .ToArray();
            return Task.FromResult<IReadOnlyList<LocalSellableItemCompareRow>>(rows);
        }

        public Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(CancellationToken cancellationToken = default)
        {
            LoadSellableItemsCallCount++;
            if (BeforeLoadSellableItemsAsync is not null)
            {
                return LoadSellableItemsCoreAsync(Items);
            }

            return LoadSellableItemsException is null
                ? Task.FromResult(Items)
                : Task.FromException<IReadOnlyList<SellableItemDto>>(LoadSellableItemsException);
        }

        public Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(string storeCode, CancellationToken cancellationToken = default)
        {
            LoadSellableItemsCallCount++;
            var storeItems = Items
                .Where(item => string.Equals(item.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (BeforeLoadSellableItemsAsync is not null)
            {
                return LoadSellableItemsCoreAsync(storeItems);
            }

            return LoadSellableItemsException is null
                ? Task.FromResult<IReadOnlyList<SellableItemDto>>(storeItems)
                : Task.FromException<IReadOnlyList<SellableItemDto>>(LoadSellableItemsException);
        }

        private async Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsCoreAsync(IReadOnlyList<SellableItemDto> items)
        {
            await BeforeLoadSellableItemsAsync!();
            if (LoadSellableItemsException is not null)
            {
                throw LoadSellableItemsException;
            }

            return items;
        }
    }

    private sealed class FakeCatalogSyncService : ILocalCatalogSyncService
    {
        public int FullSyncCallCount { get; private set; }

        public Exception? FullSyncException { get; init; }

        public Task<LocalCatalogSyncResult> FullSyncAsync(
            string storeCode,
            CancellationToken cancellationToken = default,
            IProgress<CatalogSyncProgress>? progress = null,
            bool forceFullDownload = false)
        {
            FullSyncCallCount++;
            return FullSyncException is null
                ? Task.FromResult(new LocalCatalogSyncResult(storeCode, 0, 0, 0, 0))
                : Task.FromException<LocalCatalogSyncResult>(FullSyncException);
        }
    }

    private sealed class RecordingShellCatalogService : IShellCatalogService
    {
        public IReadOnlyList<SellableItemDto> LocalItems { get; init; } = [];

        public IReadOnlyList<SellableItemDto> SyncItems { get; init; } = [];

        public Exception? SyncException { get; init; }

        public TaskCompletionSource? SyncRelease { get; init; }

        public int SyncCallCount { get; private set; }

        public CancellationToken LastSyncCancellationToken { get; private set; }

        public IProgress<CatalogSyncProgress>? LastProgress { get; private set; }

        public bool IsCatalogSyncActive => false;

        public Task ReplacePreviewCatalogAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SellableItemDto>> LoadLocalCatalogAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(LocalItems);
        }

        public async Task<IReadOnlyList<SellableItemDto>> SyncCatalogAndReloadAsync(
            string storeCode,
            bool forceFullDownload,
            IProgress<CatalogSyncProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            SyncCallCount++;
            LastSyncCancellationToken = cancellationToken;
            LastProgress = progress;
            if (SyncRelease is not null)
            {
                await SyncRelease.Task.WaitAsync(cancellationToken);
            }

            if (SyncException is not null)
            {
                throw SyncException;
            }

            return SyncItems;
        }
    }

    private sealed class FakeRemoteLookupRefreshService : IRemoteLookupRefreshService
    {
        public Task<RemoteLookupRefreshResult> RefreshLookupAsync(
            string storeCode,
            string lookupCode,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new RemoteLookupRefreshResult(storeCode, lookupCode, false, null, 0));
        }
    }

    private sealed class FakeSpecialProductService : ISpecialProductService
    {
        public Task<SpecialProductMarkResult> MarkSpecialProductAsync(
            string storeCode,
            string productCode,
            bool isSpecialProduct,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SpecialProductMarkResult([], []));
        }

        public Task<SpecialProductDownloadResult> DownloadSpecialProductsAsync(
            string storeCode,
            CancellationToken cancellationToken = default,
            IProgress<SpecialProductDownloadProgress>? progress = null)
        {
            return Task.FromResult(new SpecialProductDownloadResult(storeCode, 0, 0, 0, 0, 0));
        }
    }

    private sealed class FakeConnectivityApiClient(params bool[] responses) : IConnectivityApiClient
    {
        private readonly Queue<bool> _responses = new(responses);

        public Exception? CheckOnlineException { get; init; }

        public int CheckOnlineCallCount { get; private set; }

        public TaskCompletionSource? CheckOnlineStarted { get; set; }

        public TaskCompletionSource<bool>? PendingResponse { get; set; }

        public Task<bool> CheckOnlineAsync(CancellationToken cancellationToken = default)
        {
            CheckOnlineCallCount++;
            CheckOnlineStarted?.TrySetResult();
            if (CheckOnlineException is not null)
            {
                return Task.FromException<bool>(CheckOnlineException);
            }

            return PendingResponse?.Task ?? Task.FromResult(_responses.Count > 0 && _responses.Dequeue());
        }
    }

    private sealed class RecordingRuntimeStatusApiClient : IPosRuntimeStatusApiClient
    {
        public List<PosRuntimeStatusReport> Reports { get; } = [];

        public int CallCount { get; private set; }

        public Exception? ReportException { get; init; }

        public CancellationToken LastCancellationToken { get; private set; }

        public Task ReportAsync(
            PosRuntimeStatusReport report,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastCancellationToken = cancellationToken;
            if (ReportException is not null)
            {
                return Task.FromException(ReportException);
            }

            Reports.Add(report);
            return Task.CompletedTask;
        }
    }

    private sealed class SwitchReadyMainShellStartupService : IMainShellStartupService
    {
        public Task<MainShellStartupResult> EvaluateAsync(
            PosSessionState session,
            bool previewMode,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CreateResult(session));
        }

        public Task<MainShellStartupResult> EvaluateAfterServerSwitchAsync(
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CreateResult(session));
        }

        public void SetAuthorizedDevice(
            string deviceCode,
            string storeCode,
            string hardwareId,
            string authorizationCode)
        {
        }

        public void ClearAuthorization()
        {
        }

        private static MainShellStartupResult CreateResult(PosSessionState session)
        {
            var cashier = CreateCashierSession();
            return new MainShellStartupResult(
                session with
                {
                    StoreCode = cashier.StoreCode,
                    StoreName = "Main Branch",
                    DeviceCode = cashier.DeviceCode,
                    CashierId = cashier.CashierId,
                    CashierName = cashier.CashierName,
                    CashierSession = cashier
                },
                RequiresDeviceRegistration: false,
                CachedDevice: null);
        }
    }

    private sealed class DeferredRuntimeStatusApiClient : IPosRuntimeStatusApiClient
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ReportAsync(
            PosRuntimeStatusReport report,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await _completion.Task.WaitAsync(cancellationToken);
        }

        public void Complete() => _completion.TrySetResult();
    }

    private sealed class FakeLocalDeviceRepository : ILocalDeviceRepository
    {
        public LocalDeviceCache? Latest { get; init; }

        public Task<LocalDeviceCache?> GetLatestAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Latest);
        }

        public Task SaveAsync(DeviceRegisterResponse response, string hardwareId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SaveAsync(DeviceVerifyResponse response, string hardwareId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SaveAsync(DeviceReregisterResponse response, string hardwareId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDeviceApiClient : IDeviceApiClient
    {
        public int GetStoresCallCount { get; private set; }

        public IReadOnlyList<StoreSelectionItem> Stores { get; init; } = [];

        public TaskCompletionSource<IReadOnlyList<StoreSelectionItem>>? PendingStoresResult { get; init; }

        public Exception? GetStoresException { get; init; }

        public DeviceReregisterResponse? ReregisterResponse { get; init; }

        public TaskCompletionSource<DeviceReregisterResponse>? PendingReregisterResponse { get; init; }

        public DeviceActivationCodePreviewResponse? ActivationPreviewResponse { get; init; }

        public DeviceActivationCodeRedeemResponse? ActivationRedeemResponse { get; init; }

        public DeviceActivationCodeRedeemResponse? ActivationRebindResponse { get; init; }

        public TaskCompletionSource<DeviceActivationCodeRedeemResponse>? PendingActivationRebindResponse { get; init; }

        private TaskCompletionSource ReregisterStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource ActivationRebindStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DeviceReregisterRequest? LastReregisterRequest { get; private set; }

        public DeviceActivationCodePreviewRequest? LastActivationPreviewRequest { get; private set; }

        public DeviceActivationCodeRedeemRequest? LastActivationRedeemRequest { get; private set; }

        public DeviceActivationCodeRebindRequest? LastActivationRebindRequest { get; private set; }

        public Task<IReadOnlyList<StoreSelectionItem>> GetStoresAsync(CancellationToken cancellationToken = default)
        {
            GetStoresCallCount++;
            if (PendingStoresResult is not null)
            {
                return PendingStoresResult.Task;
            }

            return GetStoresException is null
                ? Task.FromResult(Stores)
                : Task.FromException<IReadOnlyList<StoreSelectionItem>>(GetStoresException);
        }

        public Task<DeviceRegisterResponse> RegisterAsync(DeviceRegisterRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DeviceRegisterResponse(string.Empty, string.Empty, string.Empty, 0, false, null, null));
        }

        public Task<DeviceVerifyResponse> VerifyAsync(DeviceVerifyRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DeviceVerifyResponse(string.Empty, string.Empty, string.Empty, 0, false, null, null));
        }

        public Task<DeviceReregisterResponse> ReregisterAsync(DeviceReregisterRequest request, CancellationToken cancellationToken = default)
        {
            LastReregisterRequest = request;
            ReregisterStarted.TrySetResult();
            return PendingReregisterResponse?.Task
                ?? Task.FromResult(ReregisterResponse ?? new DeviceReregisterResponse("POS-NEW", request.TargetStoreCode, "New Store", -1, false, "Pending approval"));
        }

        public Task WaitForReregisterStartedAsync() => ReregisterStarted.Task;

        public Task<DeviceActivationCodePreviewResponse> PreviewActivationCodeAsync(
            DeviceActivationCodePreviewRequest request,
            CancellationToken cancellationToken = default)
        {
            LastActivationPreviewRequest = request;
            return Task.FromResult(ActivationPreviewResponse ?? new DeviceActivationCodePreviewResponse(
                false,
                DeviceActivationReasonCodes.NotAvailable,
                null,
                null,
                null,
                null,
                "Code unavailable"));
        }

        public Task<DeviceActivationCodeRedeemResponse> RedeemActivationCodeAsync(
            DeviceActivationCodeRedeemRequest request,
            CancellationToken cancellationToken = default)
        {
            LastActivationRedeemRequest = request;
            return Task.FromResult(ActivationRedeemResponse ?? AllowedActivationResponse(
                "POS-NEW",
                "2042",
                "New Store",
                "AUTH-NEW"));
        }

        public Task<DeviceActivationCodeRedeemResponse> RebindActivationCodeAsync(
            DeviceActivationCodeRebindRequest request,
            CancellationToken cancellationToken = default)
        {
            LastActivationRebindRequest = request;
            ActivationRebindStarted.TrySetResult();
            return PendingActivationRebindResponse?.Task
                ?? Task.FromResult(ActivationRebindResponse ?? AllowedActivationResponse(
                    "POS-NEW",
                    "2042",
                    "New Store",
                    "AUTH-NEW"));
        }

        public Task WaitForActivationRebindStartedAsync() => ActivationRebindStarted.Task;
    }

    private sealed class InMemoryActivationRecoveryStore : IDeviceActivationRecoveryStore
    {
        public DeviceActivationRecovery? Pending { get; private set; }

        public Task<DeviceActivationRecovery?> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Pending);
        }

        public Task SaveAsync(
            string activationCode,
            DeviceActivationRecoveryMode mode,
            string hardwareId,
            CancellationToken cancellationToken = default)
        {
            Pending = new DeviceActivationRecovery(
                activationCode,
                mode,
                "https://hotbargain.vip/pos-api/",
                hardwareId,
                DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Pending = null;
            return Task.CompletedTask;
        }
    }

    private sealed class PendingActivationRecoveryWorkflow(
        Exception recoveryException,
        bool removePendingBeforeFailure = false,
        Exception? pendingCheckException = null) : IDeviceRegistrationWorkflowService
    {
        private bool _hasPending = true;

        public int PendingCheckCallCount { get; private set; }

        public int RecoveryCallCount { get; private set; }

        public int ClearCallCount { get; private set; }

        public string GetHardwareId() => "HW-001";

        public Task<DeviceActivationRecoveryMode?> GetPendingActivationRecoveryModeAsync(
            CancellationToken cancellationToken = default)
        {
            PendingCheckCallCount++;
            if (pendingCheckException is not null)
            {
                return Task.FromException<DeviceActivationRecoveryMode?>(pendingCheckException);
            }

            return Task.FromResult<DeviceActivationRecoveryMode?>(
                _hasPending ? DeviceActivationRecoveryMode.Rebind : null);
        }

        public Task<DeviceActivationRecoveryResult?> RecoverActivationCodeAsync(
            string hardwareId,
            CancellationToken cancellationToken = default)
        {
            RecoveryCallCount++;
            if (removePendingBeforeFailure)
            {
                _hasPending = false;
            }
            return Task.FromException<DeviceActivationRecoveryResult?>(recoveryException);
        }

        public Task ClearActivationRecoveryAsync(CancellationToken cancellationToken = default)
        {
            ClearCallCount++;
            return Task.CompletedTask;
        }

        public Task<DeviceRegistrationLoadResult> LoadStoresAsync(
            LocalDeviceCache? cachedDevice,
            bool isReregisterMode,
            string? excludedStoreCode = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DeviceRegistrationActionResult> RegisterAsync(
            StoreSelectionItem selectedStore,
            string hardwareId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DeviceRegistrationActionResult> VerifyAsync(
            StoreSelectionItem selectedStore,
            string deviceCode,
            string hardwareId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DeviceRegistrationActionResult> ReregisterAsync(
            StoreSelectionItem selectedStore,
            string hardwareId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FailIfEvaluatedMainShellStartupService : IMainShellStartupService
    {
        public int EvaluateCallCount { get; private set; }

        public Task<MainShellStartupResult> EvaluateAsync(
            PosSessionState session,
            bool previewMode,
            CancellationToken cancellationToken = default)
        {
            EvaluateCallCount++;
            return Task.FromException<MainShellStartupResult>(
                new InvalidOperationException("Cached startup rules must remain blocked."));
        }

        public void SetAuthorizedDevice(
            string deviceCode,
            string storeCode,
            string hardwareId,
            string authorizationCode)
        {
        }

        public void ClearAuthorization()
        {
        }
    }

    private sealed class FakeDeviceFingerprintService : IDeviceFingerprintService
    {
        public string GetHardwareId()
        {
            return "HW-001";
        }
    }

    private sealed class FakeLocalOrderRepository : ILocalOrderRepository
    {
        public int FilteredQueryCallCount { get; private set; }

        public LocalOrderHistoryQuery? LastFilteredQuery { get; private set; }

        public Task SavePendingOrderAsync(LocalOrder order, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(
            LocalOrderHistoryQuery query,
            int take = 50,
            CancellationToken cancellationToken = default)
        {
            FilteredQueryCallCount++;
            LastFilteredQuery = query;
            return Task.FromResult<IReadOnlyList<LocalOrderSummary>>([]);
        }

        public Task<LocalOrder?> GetOrderAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalOrder?>(null);
        }
    }

    private sealed class RecordingInstallmentOrderService : IInstallmentOrderService
    {
        public LocalInstallmentOrder? CreatedLocalOrder { get; private set; }

        public IReadOnlyList<InstallmentOrderSummary> HistoryOrders { get; set; } = [];

        public int RecoverPendingOperationsCallCount { get; private set; }

        public PosSessionState? LastRecoverySession { get; private set; }

        public int ConfirmPickupCallCount { get; private set; }

        public Guid? LastConfirmPickupOrderId { get; private set; }

        public InstallmentOrderSummary SeedRepaymentOrder()
        {
            var guid = Guid.NewGuid();
            var createdAt = new DateTimeOffset(2026, 7, 4, 12, 30, 0, TimeSpan.Zero);
            CreatedLocalOrder = new LocalInstallmentOrder(
                guid,
                guid,
                "IO-REPAY",
                "1042",
                "POS-01",
                "C001",
                "Alice",
                "Bob",
                "0400222333",
                createdAt,
                createdAt,
                80m,
                20m,
                20m,
                20m,
                60m,
                InstallmentStatus.Active,
                [
                    new InstallmentLineDto(
                        Guid.NewGuid(),
                        "SKU-INST-REPAY",
                        null,
                        "Repayment Tea",
                        "930REP",
                        1m,
                        80m,
                        0m,
                        80m,
                        "SKU-INST-REPAY")
                ],
                [
                    new InstallmentPaymentDto(
                        Guid.NewGuid(),
                        PaymentMethodKind.Cash,
                        20m,
                        null,
                        InstallmentPaymentStatus.Recorded,
                        createdAt,
                        "C001",
                        "POS-01",
                        null,
                        "SEED")
                ],
                null);
            return ToSummary(CreatedLocalOrder);
        }

        private static InstallmentOrderSummary ToSummary(LocalInstallmentOrder order)
        {
            return new InstallmentOrderSummary(
                order.InstallmentGuid,
                order.InstallmentNumber,
                order.CustomerName,
                order.CustomerPhone,
                order.TotalAmount,
                order.DownPaymentAmount,
                order.PaidAmount,
                order.BalanceAmount,
                0,
                order.BalanceAmount > 0m,
                order.BalanceAmount == 0m && order.PickupInfo is null,
                order.BalanceAmount > 0m,
                order.BalanceAmount > 0m,
                order.PickupInfo is not null ? "Picked up" : order.BalanceAmount > 0m ? "Pending repayment" : "Ready for pickup",
                order.DeviceCode,
                order.UpdatedAt);
        }

        public Task<IReadOnlyList<InstallmentOrderSummary>> GetOrdersAsync(PosSessionState session, CancellationToken cancellationToken = default) =>
            Task.FromResult(HistoryOrders);

        public Task<IReadOnlyList<InstallmentOrderSummary>> SearchAsync(PosSessionState session, string? keyword, CancellationToken cancellationToken = default) =>
            Task.FromResult(HistoryOrders);

        public Task<LocalInstallmentOrder?> GetLocalOrderAsync(Guid installmentGuid, CancellationToken cancellationToken = default) =>
            Task.FromResult(CreatedLocalOrder?.InstallmentGuid == installmentGuid ? CreatedLocalOrder : null);

        public Task<LocalInstallmentOrder?> GetOrderDetailsAsync(
            PosSessionState session,
            Guid installmentGuid,
            CancellationToken cancellationToken = default) =>
            GetLocalOrderAsync(installmentGuid, cancellationToken);

        public Task<IReadOnlyList<InstallmentOperationRecoveryResult>> RecoverPendingOperationsAsync(
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            RecoverPendingOperationsCallCount++;
            LastRecoverySession = session;
            return Task.FromResult<IReadOnlyList<InstallmentOperationRecoveryResult>>([]);
        }

        public Task<InstallmentWriteResult<InstallmentCreateResponse>> CreateAsync(PosSessionState session, InstallmentCreateRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentWriteResult<InstallmentAppendPaymentResponse>> AppendPaymentAsync(PosSessionState session, InstallmentAppendPaymentRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentWriteResult<InstallmentConfirmPickupResponse>> ConfirmPickupAsync(PosSessionState session, InstallmentConfirmPickupRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentWriteResult<InstallmentCancelResponse>> CancelWithRefundAsync(PosSessionState session, InstallmentCancelRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentWriteResult<InstallmentVoidResponse>> VoidCancelAsync(PosSessionState session, InstallmentVoidRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentOrderCreateResult> CreateOrderAsync(InstallmentOrderCreateRequest request, CancellationToken cancellationToken = default)
        {
            var guid = Guid.NewGuid();
            var paidAmount = request.DownPaymentAmount;
            var balanceAmount = request.CartSnapshot.ActualAmount - paidAmount;
            var status = balanceAmount <= 0m ? InstallmentStatus.PaidOff : InstallmentStatus.Active;
            CreatedLocalOrder = new LocalInstallmentOrder(
                guid,
                guid,
                "IO-AUTO",
                request.Session.StoreCode,
                request.Session.DeviceCode,
                request.Session.CashierId,
                request.Session.CashierName,
                request.CustomerName,
                request.CustomerPhone,
                new DateTimeOffset(2026, 7, 4, 12, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 4, 12, 30, 0, TimeSpan.Zero),
                request.CartSnapshot.ActualAmount,
                20m,
                paidAmount,
                paidAmount,
                balanceAmount,
                status,
                request.CartSnapshot.Lines.Select(line => new InstallmentLineDto(
                    Guid.NewGuid(),
                    line.ProductCode,
                    line.ReferenceCode,
                    line.DisplayName,
                    line.LookupCode,
                    line.Quantity,
                    line.UnitPrice,
                    line.DiscountAmount,
                    line.ActualAmount,
                    line.ItemNumber)).ToList(),
                [
                    new InstallmentPaymentDto(
                        request.DownPayment.PaymentGuid,
                        request.DownPayment.Method,
                        paidAmount,
                        request.DownPayment.Reference,
                        InstallmentPaymentStatus.Recorded,
                        new DateTimeOffset(2026, 7, 4, 12, 30, 0, TimeSpan.Zero),
                        request.Session.CashierId,
                        request.Session.DeviceCode,
                        request.DownPayment.CardTransactions,
                        request.DownPayment.IdempotencyKey)
                ],
                null);

            return Task.FromResult(new InstallmentOrderCreateResult(
                true,
                "分期单已创建。",
                new InstallmentOrderSummary(
                    guid,
                    CreatedLocalOrder.InstallmentNumber,
                    CreatedLocalOrder.CustomerName,
                    CreatedLocalOrder.CustomerPhone,
                    CreatedLocalOrder.TotalAmount,
                    CreatedLocalOrder.DownPaymentAmount,
                    CreatedLocalOrder.PaidAmount,
                    CreatedLocalOrder.BalanceAmount,
                    0,
                    CreatedLocalOrder.BalanceAmount > 0m,
                    CreatedLocalOrder.BalanceAmount == 0m,
                    CreatedLocalOrder.BalanceAmount > 0m,
                    CreatedLocalOrder.BalanceAmount > 0m,
                    CreatedLocalOrder.BalanceAmount > 0m ? "待补款" : "待提货",
                    CreatedLocalOrder.DeviceCode,
                    CreatedLocalOrder.UpdatedAt)));
        }

        public Task<InstallmentOrderActionResult> AddRepaymentAsync(InstallmentOrderRepaymentRequest request, CancellationToken cancellationToken = default)
        {
            if (CreatedLocalOrder is null || CreatedLocalOrder.InstallmentGuid != request.InstallmentGuid)
            {
                return Task.FromResult(new InstallmentOrderActionResult(false, "分期单不存在。"));
            }

            var recordedAt = new DateTimeOffset(2026, 7, 4, 12, 45, 0, TimeSpan.Zero);
            var paidAmount = CreatedLocalOrder.PaidAmount + request.Payment.Amount;
            var balanceAmount = Math.Max(0m, CreatedLocalOrder.TotalAmount - paidAmount);
            CreatedLocalOrder = CreatedLocalOrder with
            {
                UpdatedAt = recordedAt,
                PaidAmount = paidAmount,
                BalanceAmount = balanceAmount,
                Status = balanceAmount == 0m ? InstallmentStatus.PaidOff : InstallmentStatus.Active,
                Payments =
                [
                    .. CreatedLocalOrder.Payments,
                    new InstallmentPaymentDto(
                        request.Payment.PaymentGuid,
                        request.Payment.Method,
                        request.Payment.Amount,
                        request.Payment.Reference,
                        InstallmentPaymentStatus.Recorded,
                        recordedAt,
                        request.Session.CashierId,
                        request.Session.DeviceCode,
                        request.Payment.CardTransactions,
                        request.Payment.IdempotencyKey)
                ]
            };

            return Task.FromResult(new InstallmentOrderActionResult(true, "补款已记录。", ToSummary(CreatedLocalOrder)));
        }

        public Task<InstallmentOrderActionResult> CancelWithRefundAsync(Guid orderId, PosSessionState session, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentOrderActionResult> VoidCancelAsync(Guid orderId, PosSessionState session, string? reason = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallmentOrderActionResult> ConfirmPickupAsync(Guid orderId, PosSessionState session, CancellationToken cancellationToken = default)
        {
            ConfirmPickupCallCount++;
            LastConfirmPickupOrderId = orderId;
            if (CreatedLocalOrder is null || CreatedLocalOrder.InstallmentGuid != orderId)
            {
                return Task.FromResult(new InstallmentOrderActionResult(false, "分期单不存在。"));
            }

            CreatedLocalOrder = CreatedLocalOrder with
            {
                UpdatedAt = new DateTimeOffset(2026, 7, 4, 13, 0, 0, TimeSpan.Zero),
                Status = InstallmentStatus.PickedUp,
                PickupInfo = new InstallmentPickupInfoDto(
                    new DateTimeOffset(2026, 7, 4, 13, 0, 0, TimeSpan.Zero),
                    session.CashierName,
                    "Picked up at POS")
            };

            return Task.FromResult(new InstallmentOrderActionResult(true, "已确认提货。", ToSummary(CreatedLocalOrder)));
        }
    }

    private sealed class RecordingReceiptPrintService : IReceiptPrintService
    {
        public List<ReceiptPrintCall> Calls { get; } = [];

        public ReceiptPrintResult? PrintReceiptResult { get; init; }

        public Exception? PrintReceiptException { get; init; }

        public Queue<ReceiptPrintResult> PrintReceiptResults { get; } = new();

        public Task<ReceiptPrintResult> PrintLatestReceiptAsync(
            ReceiptPrintReason reason = ReceiptPrintReason.LastReceipt,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new ReceiptPrintCall(null, reason, null));
            return Task.FromResult(new ReceiptPrintResult(true, "printed"));
        }

        public Task<ReceiptPrintResult> PrintReceiptAsync(
            Guid orderGuid,
            ReceiptPrintReason reason = ReceiptPrintReason.Manual,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new ReceiptPrintCall(orderGuid, reason, null));
            if (PrintReceiptException is not null)
            {
                return Task.FromException<ReceiptPrintResult>(PrintReceiptException);
            }

            return Task.FromResult(PrintReceiptResult ?? new ReceiptPrintResult(true, "printed", orderGuid));
        }

        public Task<ReceiptPrintResult> PrintReceiptAsync(
            ReceiptDetails receipt,
            ReceiptPrintReason reason = ReceiptPrintReason.Manual,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new ReceiptPrintCall(receipt.OrderGuid, reason, receipt));
            if (PrintReceiptException is not null)
            {
                return Task.FromException<ReceiptPrintResult>(PrintReceiptException);
            }

            var result = PrintReceiptResults.Count > 0
                ? PrintReceiptResults.Dequeue()
                : PrintReceiptResult ?? new ReceiptPrintResult(true, "printed", receipt.OrderGuid);
            return Task.FromResult(result);
        }

        public Task<ReceiptPrintResult> TestPrinterAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add(new ReceiptPrintCall(null, ReceiptPrintReason.Test, null));
            return Task.FromResult(new ReceiptPrintResult(true, "tested"));
        }
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<Exception> _exceptions = new();

        public IReadOnlyCollection<Exception> Exceptions => _exceptions.ToArray();

        public override void Post(SendOrPostCallback callback, object? state)
        {
            try
            {
                callback(state);
            }
            catch (Exception ex)
            {
                _exceptions.Enqueue(ex);
            }
        }
    }

    private sealed class RecordingRemoteOrderHistoryService(ReceiptDetails receipt) : IRemoteOrderHistoryService
    {
        public Task<RemoteOrderHistoryResult> QueryAsync(
            RemoteOrderHistoryQuery query,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new RemoteOrderHistoryResult(
            [
                new RemoteOrderHistorySummary(
                    receipt.OrderGuid,
                    receipt.StoreCode,
                    receipt.DeviceCode,
                    receipt.CashierName,
                    receipt.SoldAt,
                    receipt.TotalAmount,
                    receipt.DiscountAmount,
                    receipt.ActualAmount,
                    receipt.Lines.Count,
                    "Cash",
                    "Synced")
            ]));
        }

        public Task<ReceiptDetails?> GetDetailsAsync(
            Guid orderGuid,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ReceiptDetails?>(
                orderGuid == receipt.OrderGuid ? receipt : null);
        }

        public Task<OrderReturnContextDto?> GetReturnContextAsync(
            Guid orderGuid,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<OrderReturnContextDto?>(null);
        }

        public Task<OrderReturnRecordCreateResponse> CreateReturnRecordsAsync(
            OrderReturnRecordCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class GatedRemoteOrderHistoryService(Guid orderGuid) : IRemoteOrderHistoryService
    {
        public TaskCompletionSource DetailsStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ReceiptDetails?> DetailsGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RemoteOrderHistoryResult> QueryAsync(
            RemoteOrderHistoryQuery query,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new RemoteOrderHistoryResult(
            [
                new RemoteOrderHistorySummary(
                    orderGuid,
                    query.StoreCode,
                    query.DeviceCode ?? "POS-02",
                    "Remote Cashier",
                    DateTimeOffset.UtcNow,
                    18m,
                    0m,
                    18m,
                    1,
                    "Cash",
                    "Synced")
            ]));
        }

        public Task<ReceiptDetails?> GetDetailsAsync(Guid requestedOrderGuid, CancellationToken cancellationToken = default)
        {
            DetailsStarted.TrySetResult();
            return DetailsGate.Task;
        }

        public Task<OrderReturnContextDto?> GetReturnContextAsync(Guid requestedOrderGuid, CancellationToken cancellationToken = default) =>
            Task.FromResult<OrderReturnContextDto?>(null);

        public Task<OrderReturnRecordCreateResponse> CreateReturnRecordsAsync(
            OrderReturnRecordCreateRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new OrderReturnRecordCreateResponse(request.ReturnOrderGuid, []));
    }

    private sealed class RecordingLinklyBankReceiptPrinter : ILinklyBankReceiptPrinter
    {
        public List<BankReceiptPrintCall> Prints { get; } = [];

        public Task<ReceiptPrintResult> PrintAsync(
            string environment,
            string sessionId,
            string receiptText,
            LinklyBankReceiptKind kind = LinklyBankReceiptKind.SignatureRequired,
            string? cardType = null,
            string? maskedCardNumber = null,
            string? responseCode = null,
            string? responseText = null,
            CancellationToken cancellationToken = default)
        {
            Prints.Add(new BankReceiptPrintCall(environment, sessionId, receiptText, kind, responseCode, responseText));
            return Task.FromResult(new ReceiptPrintResult(true, "printed"));
        }
    }

    private sealed class RecordingCashDrawerService : ICashDrawerService
    {
        public int OpenCallCount { get; private set; }

        public ReceiptPrintResult Result { get; init; } = new(true, "Cash drawer opened.");

        public Task<ReceiptPrintResult> OpenAsync(CancellationToken cancellationToken = default)
        {
            OpenCallCount++;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingApplicationExitService : IApplicationExitService
    {
        public int ExitCallCount { get; private set; }

        public void Exit()
        {
            ExitCallCount++;
        }
    }

    private sealed class FakeConfirmationDialogService : IConfirmationDialogService, IConfirmationDialogPresenter
    {
        public bool ConfirmExitApplicationResult { get; init; }

        public bool ConfirmInstallmentFullFirstPaymentResult { get; init; }

        public bool ConfirmInstallmentPickupAfterPaidOffResult { get; init; }

        public int ConfirmExitApplicationCallCount { get; private set; }

        public int ConfirmInstallmentFullFirstPaymentCallCount { get; private set; }

        public int ConfirmInstallmentPickupAfterPaidOffCallCount { get; private set; }

        public bool IsOpen => false;

        public string TitleText => string.Empty;

        public string MessageText => string.Empty;

        public string ConfirmButtonText => string.Empty;

        public string CancelButtonText => string.Empty;

        public bool IsDestructive => false;

        public IRelayCommand ConfirmCommand { get; } = new RelayCommand(() => { });

        public IRelayCommand CancelCommand { get; } = new RelayCommand(() => { });

        public Task<bool> ConfirmExitApplicationAsync()
        {
            ConfirmExitApplicationCallCount++;
            return Task.FromResult(ConfirmExitApplicationResult);
        }

        public Task<bool> ConfirmResetTestSalesDataAsync()
        {
            return Task.FromResult(false);
        }

        public Task<bool> ConfirmInstallmentFullFirstPaymentAsync()
        {
            ConfirmInstallmentFullFirstPaymentCallCount++;
            return Task.FromResult(ConfirmInstallmentFullFirstPaymentResult);
        }

        public Task<bool> ConfirmInstallmentPickupAfterPaidOffAsync()
        {
            ConfirmInstallmentPickupAfterPaidOffCallCount++;
            return Task.FromResult(ConfirmInstallmentPickupAfterPaidOffResult);
        }

        public Task<bool> ConfirmLinklySettlementAsync(DateTime businessDate)
        {
            return Task.FromResult(false);
        }

        public Task<bool> ConfirmHeldOrderCancellationAsync()
        {
            return Task.FromResult(false);
        }

        public Task<bool> ConfirmOrderDateRangeReuploadAsync(
            int orderCount,
            int batchCount,
            DateTime dateFrom,
            DateTime dateTo)
        {
            return Task.FromResult(false);
        }
    }

    private sealed record ReceiptPrintCall(Guid? OrderGuid, ReceiptPrintReason Reason, ReceiptDetails? Receipt);

    private sealed record BankReceiptPrintCall(
        string Environment,
        string SessionId,
        string ReceiptText,
        LinklyBankReceiptKind Kind,
        string? ResponseCode,
        string? ResponseText);

    private sealed class FakeSyncQueueRepository : ISyncQueueRepository
    {
        public SyncQueueOverview Overview { get; set; } = new(0, 0, 0, null);

        public IReadOnlyList<SyncQueueListItem> ActiveItems { get; set; } = [];

        public bool ThrowOnRead { get; set; }

        public int ThrowOnReadAfterCount { get; set; } = -1;

        public int ReadCount => _readCount;

        public TaskCompletionSource? OverviewReadStarted { get; set; }

        public TaskCompletionSource? ReleaseOverviewRead { get; set; }

        private int _readCount;

        public Task<int> CountPendingAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfConfigured();
            return Task.FromResult(Overview.PendingCount);
        }

        public async Task<SyncQueueOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfConfigured();
            OverviewReadStarted?.TrySetResult();
            if (ReleaseOverviewRead is not null)
            {
                // 中文注释：默认不阻塞；仅由并发回归测试控制支付完成后的同步刷新窗口。
                await ReleaseOverviewRead.Task.WaitAsync(cancellationToken);
            }

            return Overview;
        }

        public Task<IReadOnlyList<SyncQueueListItem>> GetActiveItemsAsync(int take = 20, CancellationToken cancellationToken = default)
        {
            ThrowIfConfigured();
            return Task.FromResult(ActiveItems);
        }

        private void ThrowIfConfigured()
        {
            _readCount++;
            if (ThrowOnRead || (ThrowOnReadAfterCount >= 0 && _readCount > ThrowOnReadAfterCount))
            {
                throw new InvalidOperationException("sync queue read failed");
            }
        }
    }

    private sealed class FakeOrderUploadExecutionService : IOrderUploadExecutionService
    {
        public OrderUploadExecutionResult ExecuteOneResult { get; init; } = new(1, 1, 0);

        public OrderUploadExecutionResult ExecutePendingResult { get; init; } = new(1, 1, 0);

        public Exception? ExecutePendingException { get; init; }

        public TaskCompletionSource? PendingExecutionStarted { get; init; }

        public TaskCompletionSource? ReleasePendingExecution { get; init; }

        public Guid? LastExecuteOneOrderGuid { get; private set; }

        public int ExecutePendingCallCount { get; private set; }

        public Action<Guid>? OnExecuteOne { get; init; }

        public Action? OnExecutePending { get; init; }

        public Action? OnBeforeExecutePendingException { get; init; }

        public Task<OrderUploadExecutionResult> ExecuteOneAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            LastExecuteOneOrderGuid = orderGuid;
            OnExecuteOne?.Invoke(orderGuid);
            return Task.FromResult(ExecuteOneResult);
        }

        public async Task<OrderUploadExecutionResult> ExecutePendingAsync(int batchSize = 20, CancellationToken cancellationToken = default)
        {
            ExecutePendingCallCount++;
            PendingExecutionStarted?.TrySetResult();
            if (ReleasePendingExecution is not null)
            {
                await ReleasePendingExecution.Task;
            }

            if (ExecutePendingException is not null)
            {
                OnBeforeExecutePendingException?.Invoke();
                throw ExecutePendingException;
            }

            OnExecutePending?.Invoke();
            return ExecutePendingResult;
        }
    }

    private sealed class FakeSettlementUploadQueueReader : ILinklySettlementUploadQueueReader
    {
        public LinklySettlementUploadOverview Overview { get; set; } = new(0, 0, 0, null);

        public IReadOnlyList<LinklySettlementUploadQueueItem> ActiveItems { get; set; } = [];

        public Task<LinklySettlementUploadOverview> GetOverviewAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Overview);

        public Task<IReadOnlyList<LinklySettlementUploadQueueItem>> GetActiveItemsAsync(
            int take = 20,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ActiveItems);
    }

    private sealed class FakeSettlementUploadExecutionService : ILinklySettlementUploadExecutionService
    {
        public int ExecutePendingCallCount { get; private set; }

        public Action? OnExecutePending { get; init; }

        public Task<LinklySettlementUploadExecutionResult> ExecutePendingAsync(
            int batchSize = 20,
            CancellationToken cancellationToken = default)
        {
            ExecutePendingCallCount++;
            OnExecutePending?.Invoke();
            return Task.FromResult(new LinklySettlementUploadExecutionResult(1, 1, 0, 0, false));
        }

        public Task<LinklySettlementUploadExecutionResult> ExecuteOneAsync(
            Guid settlementGuid,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LinklySettlementUploadExecutionResult(1, 1, 0, 0, false));
    }

    private sealed class FakeCustomerDisplayWindowService : ICustomerDisplayWindowService
    {
        public CustomerDisplayWindowResult SetModeResult { get; init; } = new(
            CustomerDisplayWindowMode.Fullscreen,
            CustomerDisplayWindowService.OpenedFullscreenStatusKey);

        public bool IsOpen => Mode != CustomerDisplayWindowMode.Closed;

        public CustomerDisplayWindowMode Mode { get; private set; } = CustomerDisplayWindowMode.Closed;

        public int OpenCallCount { get; private set; }

        public int PrewarmCallCount { get; private set; }

        public int ToggleCallCount { get; private set; }

        public int SetModeCallCount { get; private set; }

        public int WindowCreationCount { get; private set; }

        public CustomerDisplayWindowMode LastSetMode { get; private set; } = CustomerDisplayWindowMode.Closed;

        public event EventHandler? Closed;

        public event EventHandler? FullscreenRequested;

        public void RaiseFullscreenRequested() => FullscreenRequested?.Invoke(this, EventArgs.Empty);

        public void Prewarm(CustomerDisplayViewModel viewModel)
        {
            PrewarmCallCount++;
            EnsureWindowCreated();
        }

        public CustomerDisplayWindowResult Open(CustomerDisplayViewModel viewModel, Window? owner)
        {
            OpenCallCount++;
            return SetMode(CustomerDisplayWindowMode.Fullscreen, viewModel, owner);
        }

        public CustomerDisplayWindowResult Toggle(CustomerDisplayViewModel viewModel, Window? owner)
        {
            ToggleCallCount++;
            var targetMode = Mode == CustomerDisplayWindowMode.Closed
                ? CustomerDisplayWindowMode.Fullscreen
                : CustomerDisplayWindowMode.Closed;
            return SetMode(targetMode, viewModel, owner);
        }

        public CustomerDisplayWindowResult SetMode(CustomerDisplayWindowMode mode, CustomerDisplayViewModel viewModel, Window? owner)
        {
            SetModeCallCount++;
            LastSetMode = mode;

            var result = SetModeResult.StatusMessageKey == CustomerDisplayWindowService.NoSecondDisplayStatusKey
                ? SetModeResult
                : CreateSuccessfulResult(mode);
            if (result.Mode == CustomerDisplayWindowMode.Closed)
            {
                _hasWindow = false;
            }
            else
            {
                EnsureWindowCreated();
            }

            Mode = result.Mode;
            return result;
        }

        public void RaiseClosed()
        {
            _hasWindow = false;
            Mode = CustomerDisplayWindowMode.Closed;
            Closed?.Invoke(this, EventArgs.Empty);
        }

        private bool _hasWindow;

        private void EnsureWindowCreated()
        {
            if (_hasWindow)
            {
                return;
            }

            _hasWindow = true;
            WindowCreationCount++;
        }

        private static CustomerDisplayWindowResult CreateSuccessfulResult(CustomerDisplayWindowMode mode)
        {
            return mode switch
            {
                CustomerDisplayWindowMode.Normal => new CustomerDisplayWindowResult(
                    CustomerDisplayWindowMode.Normal,
                    CustomerDisplayWindowService.OpenedNormalStatusKey),
                CustomerDisplayWindowMode.Fullscreen => new CustomerDisplayWindowResult(
                    CustomerDisplayWindowMode.Fullscreen,
                    CustomerDisplayWindowService.OpenedFullscreenStatusKey),
                _ => new CustomerDisplayWindowResult(
                    CustomerDisplayWindowMode.Closed,
                    CustomerDisplayWindowService.ClosedStatusKey)
            };
        }
    }

    private sealed class FakeSpecialProductsWorkflowService : ISpecialProductsWorkflowService
    {
        public SpecialProductsLoadResult PreloadResult { get; init; } = new("1042", []);

        public Exception? PreloadException { get; init; }

        public int PreloadCallCount { get; private set; }

        public string? LastPreloadStoreCode { get; private set; }

        public Task<SpecialProductsLoadResult> PreloadAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            PreloadCallCount++;
            LastPreloadStoreCode = storeCode;
            return PreloadException is null
                ? Task.FromResult(PreloadResult)
                : Task.FromException<SpecialProductsLoadResult>(PreloadException);
        }

        public Task<SpecialProductsLoadResult> EnsureLoadedAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PreloadResult with { StoreCode = storeCode });
        }

        public Task<SpecialProductsLoadResult> LoadAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PreloadResult with { StoreCode = storeCode });
        }

        public SpecialProductsSearchResult Search(string storeCode, string searchText)
        {
            return new SpecialProductsSearchResult(storeCode, searchText, []);
        }

        public Task<SpecialProductsSearchResult> SearchAsync(
            string storeCode,
            string searchText,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SpecialProductsSearchResult(storeCode, searchText, []));
        }

        public SpecialProductsAddToCartResult AddToCart(SellableItemDto item)
        {
            return new SpecialProductsAddToCartResult(new CartLine(item), 1);
        }

        public Task<SpecialProductsDownloadWorkflowResult> DownloadAsync(
            string storeCode,
            CancellationToken cancellationToken = default,
            IProgress<SpecialProductDownloadProgress>? progress = null)
        {
            return Task.FromResult(new SpecialProductsDownloadWorkflowResult(
                new SpecialProductDownloadResult(storeCode, 0, 0, 0, 0, 0),
                []));
        }

        public Task<SpecialProductsMutationWorkflowResult> MarkSpecialProductAsync(
            string storeCode,
            string productCode,
            bool isSpecialProduct,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new SpecialProductsMutationWorkflowResult(
                storeCode,
                productCode,
                isSpecialProduct,
                []));
        }

        public Task<SpecialProductsReorderWorkflowResult?> ReorderAsync(
            string storeCode,
            IReadOnlyList<SellableItemDto> currentItems,
            string productCode,
            int delta,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SpecialProductsReorderWorkflowResult?>(null);
        }
    }

    private sealed class FakeCardPaymentRecoveryService : ICardPaymentRecoveryService
    {
        private readonly Queue<Func<PosCartService, PosSessionState, CancellationToken, Task<CardPaymentRecoveryResult>>> _results;

        public FakeCardPaymentRecoveryService()
            : this(Array.Empty<Task<CardPaymentRecoveryResult>>())
        {
        }

        public FakeCardPaymentRecoveryService(params Task<CardPaymentRecoveryResult>[] results)
        {
            _results = new Queue<Func<PosCartService, PosSessionState, CancellationToken, Task<CardPaymentRecoveryResult>>>(
                results.Select(result => new Func<PosCartService, PosSessionState, CancellationToken, Task<CardPaymentRecoveryResult>>(
                    (PosCartService cart, PosSessionState session, CancellationToken cancellationToken) => result)));
        }

        public FakeCardPaymentRecoveryService(params Func<PosCartService, PosSessionState, CancellationToken, Task<CardPaymentRecoveryResult>>[] results)
        {
            _results = new Queue<Func<PosCartService, PosSessionState, CancellationToken, Task<CardPaymentRecoveryResult>>>(results);
        }

        public int CallCount { get; private set; }

        public int ListOpenCallCount { get; private set; }

        public int TargetedRecoverCallCount { get; private set; }

        public IReadOnlyList<CardRecoveryQueueItem> OpenItems { get; init; } = [];

        public Func<PosSessionState, CancellationToken, Task<IReadOnlyList<CardRecoveryQueueItem>>>? ListOpenHandler { get; init; }

        public CardPaymentRecoveryResult TargetedRecoverResult { get; init; } = CardPaymentRecoveryResult.None;

        public CardRecoveryResolutionResult TargetedResolveResult { get; init; } =
            new(false, "Targeted resolution unavailable.", LockRetained: true);

        public Task<IReadOnlyList<CardRecoveryQueueItem>> ListOpenAsync(
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            ListOpenCallCount++;
            return ListOpenHandler?.Invoke(session, cancellationToken) ?? Task.FromResult(OpenItems);
        }

        public Task<CardPaymentRecoveryResult> RecoverLatestAsync(
            PosCartService cart,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return _results.Count > 0
                ? _results.Dequeue()(cart, session, cancellationToken)
                : Task.FromResult(CardPaymentRecoveryResult.None);
        }

        public Task<CardPaymentRecoveryResult> RecoverActiveSessionAsync(
            PosCartService cart,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return _results.Count > 0
                ? _results.Dequeue()(cart, session, cancellationToken)
                : Task.FromResult(CardPaymentRecoveryResult.None);
        }

        public Task<CardPaymentRecoveryResult> ManuallyClearActiveSessionAsync(
            string sessionId,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new CardPaymentRecoveryResult(
                CardPaymentRecoveryOutcome.ActiveSessionManuallyCleared,
                "Previous Linkly session manually cleared.",
                DialogDetails: new CardPaymentRecoveryDialogDetails(
                    sessionId,
                    null,
                    null,
                    null,
                    null,
                    DateTimeOffset.Now)));
        }

        public Task<CardPaymentRecoveryResult> RecoverAsync(
            CardRecoveryAttemptKey key,
            PosCartService cart,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            TargetedRecoverCallCount++;
            return Task.FromResult(TargetedRecoverResult);
        }

        public Task<CardRecoveryResolutionResult> ResolveAsync(
            CardRecoveryAttemptKey key,
            CardRecoverySupervisorDecision decision,
            string reason,
            string? evidence,
            string? reference,
            PosCartService cart,
            PosSessionState session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(TargetedResolveResult);
    }

    private sealed class RecordingUserFeedbackService : IUserFeedbackService
    {
        public List<UserFeedbackCue> Cues { get; } = [];

        public void Play(UserFeedbackCue cue)
        {
            Cues.Add(cue);
        }
    }

    private sealed class BlockingOnlineRuntimeStatusApiClient : IPosRuntimeStatusApiClient
    {
        public TaskCompletionSource OnlineStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseOnline { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<PosRuntimeStatusReport> CompletedReports { get; } = [];

        public async Task ReportAsync(
            PosRuntimeStatusReport report,
            CancellationToken cancellationToken = default)
        {
            if (report.IsOnline)
            {
                OnlineStarted.TrySetResult();
                // 模拟旧客户端忽略取消：关机必须等待该上报终态，不能抢先发 offline。
                await ReleaseOnline.Task;
            }

            CompletedReports.Add(report);
        }
    }

    private sealed class ThrowingUserFeedbackService : IUserFeedbackService
    {
        public void Play(UserFeedbackCue cue) => throw new InvalidOperationException("speaker unavailable");
    }

    private sealed class ConsoleLogCapture : IDisposable
    {
        private readonly List<string> _lines = [];

        public ConsoleLogCapture()
        {
            ConsoleLog.LineWritten += OnLineWritten;
        }

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return _lines.ToArray();
                }
            }
        }

        public void Dispose()
        {
            ConsoleLog.LineWritten -= OnLineWritten;
        }

        private void OnLineWritten(string line)
        {
            lock (_lines)
            {
                _lines.Add(line);
            }
        }
    }
}
