using BlazorApp.Shared.DTOs;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Stores;
using Hbpos.RemoteMaintenance.Setup;

namespace Hbpos.Client.Tests;

[Collection(ShutdownTimingTestCollection.Name)]
public sealed class SettingsViewModelTests
{
    private const string CachedToken = "opaque-settings-square-token";

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public async Task Active_payment_provider_uses_saved_manual_mode_and_preserves_it_while_editing(string culture)
    {
        var localization = new LocalizationService();
        localization.SetCulture(culture);
        var methods = new FakePaymentMethodSettingsService();
        using var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService(), localization,
            paymentMethodSettingsService: methods);
        try
        {
            await viewModel.LoadAsync();
            var automaticTitle = viewModel.ActivePaymentProviderText;
            var automaticDetail = viewModel.ActivePaymentProviderDetailText;
            viewModel.UseManualCard = true;
            Assert.Equal(automaticTitle, viewModel.ActivePaymentProviderText);
            Assert.Equal(automaticDetail, viewModel.ActivePaymentProviderDetailText);

            var notifications = new List<string?>();
            viewModel.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
            await viewModel.SavePaymentMethodsCommand.ExecuteAsync(null);
            Assert.Equal(localization.T("settings.payment.activeProvider.manual"), viewModel.ActivePaymentProviderText);
            Assert.Equal(localization.T("settings.payment.activeProvider.manual.detail"), viewModel.ActivePaymentProviderDetailText);
            Assert.DoesNotContain("[[", viewModel.ActivePaymentProviderText);
            Assert.DoesNotContain("[[", viewModel.ActivePaymentProviderDetailText);
            Assert.Contains(nameof(viewModel.ActivePaymentProviderText), notifications);
            Assert.Contains(nameof(viewModel.ActivePaymentProviderDetailText), notifications);

            viewModel.UseManualCard = false;
            Assert.Equal(localization.T("settings.payment.activeProvider.manual"), viewModel.ActivePaymentProviderText);
            await viewModel.SavePaymentMethodsCommand.ExecuteAsync(null);
            Assert.Equal(automaticTitle, viewModel.ActivePaymentProviderText);
            Assert.Equal(automaticDetail, viewModel.ActivePaymentProviderDetailText);
        }
        finally
        {
            localization.SetCulture("en-US");
        }
    }

    [Fact]
    public async Task Payment_method_settings_load_and_save_without_terminal_test()
    {
        var setup = new FakeCardTerminalSetupService();
        var methods = new FakePaymentMethodSettingsService { Current = new(true, false) };
        using var viewModel = new SettingsViewModel(setup, paymentMethodSettingsService: methods);
        await viewModel.LoadAsync();
        Assert.True(viewModel.UseManualCard);
        Assert.False(viewModel.VoucherEnabled);
        viewModel.UseManualCard = false;
        viewModel.VoucherEnabled = true;
        Assert.True(viewModel.SavePaymentMethodsCommand.CanExecute(null));

        await viewModel.SavePaymentMethodsCommand.ExecuteAsync(null);

        Assert.Equal(new PaymentMethodSettings(false, true), methods.Current);
        Assert.Equal(1, methods.SaveCalls);
        Assert.Equal(0, setup.LinklyTestCallCount);
        Assert.Null(setup.SavedConfiguration);
    }

    [Fact]
    public async Task Payment_method_save_failure_keeps_draft_for_retry()
    {
        var methods = new FakePaymentMethodSettingsService { FailSave = true };
        using var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService(), paymentMethodSettingsService: methods)
        { UseManualCard = true, VoucherEnabled = true };
        await viewModel.SavePaymentMethodsCommand.ExecuteAsync(null);
        Assert.True(viewModel.UseManualCard);
        Assert.True(viewModel.VoucherEnabled);
        Assert.Equal(new PaymentMethodSettings(), methods.Current);
        Assert.Contains("save failed", viewModel.StatusMessage);
        Assert.True(viewModel.SavePaymentMethodsCommand.CanExecute(null));
    }

    [Fact]
    public async Task Payment_method_save_requires_payment_settings_permission()
    {
        var methods = new FakePaymentMethodSettingsService();
        using var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService(),
            enforcePermissionsWhenNoCashier: true, paymentMethodSettingsService: methods)
        { UseManualCard = true, VoucherEnabled = true };
        await viewModel.SavePaymentMethodsCommand.ExecuteAsync(null);
        Assert.Equal(0, methods.SaveCalls);
        Assert.Equal(new PaymentMethodSettings(), methods.Current);
    }

    private sealed class FakePaymentMethodSettingsService : IPaymentMethodSettingsService
    {
        public PaymentMethodSettings Current { get; set; } = new();
        public event EventHandler? Changed;
        public bool FailSave { get; set; }
        public int SaveCalls { get; private set; }
        public Task<PaymentMethodSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public Task SaveAsync(PaymentMethodSettings settings, CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            if (FailSave) throw new IOException("save failed");
            Current = settings;
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void LoadLocationsCommand_allows_backend_token_fetch()
    {
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService());

        Assert.True(viewModel.LoadLocationsCommand.CanExecute(null));
    }

    [Fact]
    public void Settings_defaults_to_data_maintenance_category()
    {
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService());

        Assert.Equal(SettingsCategory.DataMaintenance, viewModel.SelectedCategory);
        Assert.True(viewModel.IsDataMaintenanceSelected);
        Assert.False(viewModel.IsPaymentTerminalSelected);
        Assert.False(viewModel.IsDeviceRegistrationSelected);
    }

    [Fact]
    public void Settings_view_binds_app_update_channel_as_one_way_display_text()
    {
        var xamlPath = Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Screens",
            "SettingsView.xaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains("<Run Text=\"{Binding AppUpdateChannelText, Mode=OneWay}\" />", xaml);
    }

    [Fact]
    public void Settings_redesign_keeps_all_categories_and_operational_bindings()
    {
        var xamlPath = Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Screens",
            "SettingsView.xaml");
        var document = System.Xml.Linq.XDocument.Load(xamlPath);
        var elements = document.Descendants().ToArray();

        var categoryCommands = new[]
        {
            "SelectDataMaintenanceCommand",
            "SelectPaymentTerminalCommand",
            "SelectReceiptPrinterCommand",
            "SelectDeviceRegistrationCommand"
        };
        var categoryButtons = categoryCommands.Select(command => Assert.Single(elements.Where(element =>
            element.Name.LocalName == "Button" &&
            element.Attribute("Command")?.Value.Contains(command, StringComparison.Ordinal) == true))).ToArray();
        var navigationBorder = categoryButtons[0].Ancestors().First(element => element.Name.LocalName == "Border");

        Assert.Equal("0", navigationBorder.Attribute("Grid.Column")?.Value);
        Assert.All(categoryButtons, button => Assert.Same(navigationBorder, button.Ancestors().First(
            element => element.Name.LocalName == "Border")));
        Assert.All(categoryButtons, button => Assert.Contains("Selected", button.Attribute("Tag")?.Value, StringComparison.Ordinal));
        var deviceRegistrationNavigationText = Assert.Single(categoryButtons[3].Descendants().Where(element =>
            element.Name.LocalName == "TextBlock"));
        Assert.Equal(
            "{loc:Loc settings.category.deviceRegistrationNav}",
            deviceRegistrationNavigationText.Attribute("Text")?.Value);

        var paymentSection = Assert.Single(elements.Where(element =>
            element.Name.LocalName == "Grid" &&
            element.Attribute("Visibility")?.Value.Contains("IsPaymentTerminalSelected", StringComparison.Ordinal) == true));
        var contentScrollViewer = paymentSection.Ancestors().First(element => element.Name.LocalName == "ScrollViewer");
        var settingsBodyChildren = navigationBorder.Parent!.Elements().ToArray();
        Assert.True(
            Array.IndexOf(settingsBodyChildren, navigationBorder) < Array.IndexOf(settingsBodyChildren, contentScrollViewer),
            "The category navigation must precede the content scroller so keyboard focus follows the visual order.");

        var providerTabs = paymentSection.Descendants().Where(element => element.Name.LocalName == "TabItem").ToArray();
        Assert.Equal(2, providerTabs.Length);
        Assert.DoesNotContain(providerTabs, tab => tab.Attribute("IsSelected") is not null);
        foreach (var command in new[]
                 {
                     "SaveSquareCommand",
                     "OpenLinklySetupInstructionsCommand",
                     "TestLinklyCommand",
                     "LogonLinklyCommand",
                     "SaveLinklyCommand"
                 })
        {
            Assert.Contains(paymentSection.Descendants(), element =>
                element.Name.LocalName == "Button" &&
                element.Attribute("Command")?.Value.Contains(command, StringComparison.Ordinal) == true);
        }
        var setupInstructionsButton = Assert.Single(paymentSection.Descendants().Where(element =>
            element.Name.LocalName == "Button" &&
            element.Attribute("Command")?.Value.Contains("OpenLinklySetupInstructionsCommand", StringComparison.Ordinal) == true));
        Assert.Same(providerTabs[1], setupInstructionsButton.Ancestors().First(element => element.Name.LocalName == "TabItem"));
        Assert.Equal(3, paymentSection.Descendants().Count(element =>
            element.Name.LocalName == "Border" &&
            element.Attribute("Style")?.Value.Contains("SettingsModeChoiceBorderStyle", StringComparison.Ordinal) == true));
        Assert.True(paymentSection.Descendants().Count(element =>
            element.Name.LocalName == "TextBox" &&
            element.Attribute("Text")?.Value.Contains("TimeoutSecondsText", StringComparison.Ordinal) == true) >= 2);

        var receiptSection = Assert.Single(elements.Where(element =>
            element.Name.LocalName == "Grid" &&
            element.Attribute("Visibility")?.Value.Contains("IsReceiptPrinterSelected", StringComparison.Ordinal) == true));
        Assert.Contains(receiptSection.Descendants(), element =>
            element.Name.LocalName == "TextBlock" &&
            element.Attribute("Text")?.Value == "{Binding ReceiptBrandNameText}");
        Assert.Contains(receiptSection.Descendants(), element =>
            element.Name.LocalName == "TextBlock" &&
            element.Attribute("Text")?.Value == "{Binding ReceiptReturnPolicyText}");
        Assert.Contains(receiptSection.Descendants(), element =>
            element.Name.LocalName == "Button" &&
            element.Attribute("Command")?.Value.Contains("SaveReceiptPrinterCommand", StringComparison.Ordinal) == true);

        var registrationSection = Assert.Single(elements.Where(element =>
            element.Name.LocalName == "Grid" &&
            element.Attribute("Visibility")?.Value.Contains("IsDeviceRegistrationSelected", StringComparison.Ordinal) == true));
        foreach (var binding in new[] { "Session.StoreName", "Session.StoreCode", "Session.DeviceCode" })
        {
            var run = Assert.Single(registrationSection.Descendants().Where(element =>
                element.Name.LocalName == "Run" &&
                element.Attribute("Text")?.Value.Contains(binding, StringComparison.Ordinal) == true));
            Assert.Contains("Mode=OneWay", run.Attribute("Text")!.Value, StringComparison.Ordinal);
        }
        Assert.Contains(registrationSection.Descendants(), element =>
            element.Name.LocalName == "ApiServerSettingsPanel" &&
            element.Attribute("DataContext")?.Value == "{Binding ApiServerSettings}");
        Assert.Contains(registrationSection.Descendants(), element =>
            element.Name.LocalName == "Button" &&
            element.Attribute("Command")?.Value.Contains("ReregisterDeviceCommand", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Settings_redesign_copy_is_available_in_english_and_chinese()
    {
        var localization = new LocalizationService();
        var keys = new[]
        {
            "settings.page.dataMaintenance.description",
            "settings.page.paymentTerminal.description",
            "settings.category.deviceRegistrationNav",
            "settings.linkly.localIp.testSafetyShort",
            "settings.receiptPrinter.preview",
            "settings.deviceRegistration.current",
            "settings.deviceRegistration.active",
            "settings.deviceRegistration.thisDevice",
            "settings.deviceRegistration.beforeContinue",
            "settings.deviceRegistration.checkCart",
            "settings.deviceRegistration.checkPayment",
            "settings.deviceRegistration.checkSync",
            "settings.deviceRegistration.supervisorRequired",
            "settings.deviceRegistration.deviceReuse"
        };

        var repoRoot = FindRepoRoot();
        foreach (var resourceFile in new[] { "SettingsStrings.resx", "SettingsStrings.zh-CN.resx" })
        {
            var resourcePath = Path.Combine(
                repoRoot,
                "apps",
                "pos-wpf",
                "src",
                "Hbpos.Client.Wpf",
                "Resources",
                resourceFile);
            var resourceKeys = System.Xml.Linq.XDocument.Load(resourcePath)
                .Descendants("data")
                .Select(element => element.Attribute("name")?.Value)
                .Where(name => name is not null)
                .ToHashSet(StringComparer.Ordinal);

            Assert.All(keys, key => Assert.Contains(key, resourceKeys));
        }

        foreach (var culture in new[] { "en-US", "zh-CN" })
        {
            localization.SetCulture(culture);
            Assert.All(keys, key => Assert.DoesNotContain("[[", localization.T(key), StringComparison.Ordinal));
        }

        localization.SetCulture("en-US");
        Assert.Equal("Device registration", localization.T("settings.category.deviceRegistrationNav"));
        localization.SetCulture("zh-CN");
        Assert.Equal("设备注册", localization.T("settings.category.deviceRegistrationNav"));
        localization.SetCulture("en-US");
    }

    [Fact]
    public void Payment_terminal_settings_content_supports_vertical_touch_panning()
    {
        var xamlPath = Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Screens",
            "SettingsView.xaml");
        var document = System.Xml.Linq.XDocument.Load(xamlPath);
        var paymentSection = document
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "Grid" &&
                element.Attribute("Visibility")?.Value.Contains(
                    "IsPaymentTerminalSelected",
                    StringComparison.Ordinal) == true);
        var contentScrollViewer = paymentSection
            .Ancestors()
            .First(element => element.Name.LocalName == "ScrollViewer");
        var xamlNamespace = System.Xml.Linq.XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var singleLineInputStyles = new[]
        {
            "SettingsFieldTextBoxStyle",
            "SettingsFieldPasswordBoxStyle"
        }.Select(styleKey => document
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "Style" &&
                element.Attribute(xamlNamespace + "Key")?.Value == styleKey));
        var multilineTextBoxes = document
            .Descendants()
            .Where(element =>
                element.Name.LocalName == "TextBox" &&
                element.Attribute("AcceptsReturn")?.Value == "True")
            .ToArray();

        Assert.Equal("VerticalOnly", contentScrollViewer.Attribute("PanningMode")?.Value);
        Assert.Equal("0.0008", contentScrollViewer.Attribute("PanningDeceleration")?.Value);
        Assert.Equal("1.0", contentScrollViewer.Attribute("PanningRatio")?.Value);
        Assert.Equal("False", contentScrollViewer.Attribute("CanContentScroll")?.Value);
        Assert.Equal("Disabled", contentScrollViewer.Attribute("HorizontalScrollBarVisibility")?.Value);
        Assert.All(singleLineInputStyles, style =>
        {
            var panningModeSetter = style
                .Elements()
                .Single(element => element.Attribute("Property")?.Value == "ScrollViewer.PanningMode");

            Assert.Equal("None", panningModeSetter.Attribute("Value")?.Value);
        });
        // 中文注释：设置页共 4 个多行文本框——小票地址、退货政策，以及总部可下发的代金券使用说明、分期条款。
        // 每个都必须是 VerticalFirst：触屏拖动文本框内部时优先滚文本，滚到头再带动外层设置页；
        // 以后再加多行文本框要同步更新这个数量，同时保证新框也带这个属性。
        Assert.Equal(4, multilineTextBoxes.Length);
        Assert.All(multilineTextBoxes, textBox =>
            Assert.Equal("VerticalFirst", textBox.Attribute("ScrollViewer.PanningMode")?.Value));
    }

    [Fact]
    public void Linkly_local_ip_actions_place_logon_between_test_and_enable()
    {
        var xamlPath = Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Screens",
            "SettingsView.xaml");
        var document = System.Xml.Linq.XDocument.Load(xamlPath);
        var logonButton = document
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "Button" &&
                element.Attribute("Command")?.Value.Contains("LogonLinklyCommand", StringComparison.Ordinal) == true);
        var actionButtons = logonButton.Parent!
            .Elements()
            .Where(element => element.Name.LocalName == "Button")
            .ToArray();

        Assert.Equal("WrapPanel", logonButton.Parent!.Name.LocalName);
        Assert.Equal(3, actionButtons.Length);
        Assert.Contains("TestLinklyCommand", actionButtons[0].Attribute("Command")?.Value, StringComparison.Ordinal);
        Assert.Same(logonButton, actionButtons[1]);
        Assert.Contains("SaveLinklyCommand", actionButtons[2].Attribute("Command")?.Value, StringComparison.Ordinal);
        Assert.Contains("IsLinklyLocalIpMode", logonButton.Attribute("Visibility")?.Value, StringComparison.Ordinal);
        Assert.Contains(
            logonButton.Descendants(),
            element =>
                element.Name.LocalName == "TextBlock" &&
                element.Attribute("Text")?.Value == "{loc:Loc settings.linkly.localIp.logon}");
    }

    [Fact]
    public async Task LoadAsync_loads_shared_api_server_settings()
    {
        var apiServerSettings = new ApiServerSettingsViewModel(
            new ApiServerSettingsService(
                new HttpClient(),
                () => "https://settings.example.com/base",
                _ => { }),
            new LocalizationService());
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            apiServerSettings: apiServerSettings);

        await viewModel.LoadAsync();

        Assert.Same(apiServerSettings, viewModel.ApiServerSettings);
        Assert.Equal("https://settings.example.com/base/", apiServerSettings.ServerAddressText);
    }

    [Fact]
    public void Category_commands_switch_selected_category()
    {
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService());

        viewModel.SelectPaymentTerminalCommand.Execute(null);

        Assert.Equal(SettingsCategory.PaymentTerminal, viewModel.SelectedCategory);
        Assert.False(viewModel.IsDataMaintenanceSelected);
        Assert.True(viewModel.IsPaymentTerminalSelected);

        viewModel.SelectDeviceRegistrationCommand.Execute(null);

        Assert.Equal(SettingsCategory.DeviceRegistration, viewModel.SelectedCategory);
        Assert.True(viewModel.IsDeviceRegistrationSelected);

        viewModel.SelectDataMaintenanceCommand.Execute(null);

        Assert.Equal(SettingsCategory.DataMaintenance, viewModel.SelectedCategory);
        Assert.True(viewModel.IsDataMaintenanceSelected);

        viewModel.SelectReceiptPrinterCommand.Execute(null);

        Assert.Equal(SettingsCategory.ReceiptPrinter, viewModel.SelectedCategory);
        Assert.True(viewModel.IsReceiptPrinterSelected);
    }

    [Fact]
    public void Maintenance_commands_are_disabled_when_services_are_not_configured()
    {
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService());

        Assert.False(viewModel.DownloadCatalogCommand.CanExecute(null));
        Assert.False(viewModel.ResetCatalogCommand.CanExecute(null));
        Assert.False(viewModel.ReregisterDeviceCommand.CanExecute(null));
        Assert.False(viewModel.CheckForAppUpdateCommand.CanExecute(null));
    }

    [Fact]
    public async Task CheckForAppUpdateCommand_calls_injected_update_delegate()
    {
        var checkCallCount = 0;
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            checkForAppUpdateAsync: cancellationToken =>
            {
                Assert.False(cancellationToken.IsCancellationRequested);
                checkCallCount++;
                return Task.FromResult(AppUpdateCoordinatorResult.NoUpdate());
            });

        await viewModel.CheckForAppUpdateCommand.ExecuteAsync(null);

        Assert.Equal(1, checkCallCount);
        Assert.Equal("Current version is already up to date.", viewModel.StatusMessage);
    }

    [Theory]
    [InlineData(AppUpdateCoordinatorStatus.AlreadyRunning, "Software update check is already running.")]
    [InlineData(AppUpdateCoordinatorStatus.OptionalDeclined, "Software update was skipped.")]
    [InlineData(AppUpdateCoordinatorStatus.CheckFailed, "Software update check failed. Please try again later.")]
    [InlineData(AppUpdateCoordinatorStatus.PolicyFailed, "Update policy is unavailable. Please contact an administrator.")]
    public async Task CheckForAppUpdateCommand_uses_coordinator_result_status(
        AppUpdateCoordinatorStatus status,
        string expectedStatus)
    {
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            checkForAppUpdateAsync: _ => Task.FromResult(AppUpdateCoordinatorResult.FromStatus(status)));

        await viewModel.CheckForAppUpdateCommand.ExecuteAsync(null);

        Assert.Equal(expectedStatus, viewModel.StatusMessage);
        Assert.NotEqual("Checking for software updates...", viewModel.StatusMessage);
    }

    [Fact]
    public async Task DownloadCatalogCommand_calls_injected_download_delegate()
    {
        var downloadCallCount = 0;
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            downloadCatalogAsync: cancellationToken =>
            {
                Assert.False(cancellationToken.IsCancellationRequested);
                downloadCallCount++;
                return Task.CompletedTask;
            });

        await viewModel.DownloadCatalogCommand.ExecuteAsync(null);

        Assert.Equal(1, downloadCallCount);
        Assert.Equal("Catalog data download completed.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Data_download_card_shows_catalog_sync_time_and_follows_status_changes()
    {
        var localization = new LocalizationService();
        localization.SetCulture("zh-CN");
        var succeededAt = new DateTimeOffset(2026, 9, 26, 14, 32, 0, TimeSpan.FromHours(8));
        var clock = new CatalogSyncStatusServiceTests.MutableTimeProvider(succeededAt);
        var syncStatus = new CatalogSyncStatusService(
            new CatalogSyncStatusServiceTests.InMemoryAppSettingsRepository(),
            clock);
        var session = new PosSessionState("HB POS", "S01", "Store", "D01", string.Empty, string.Empty, true, 0);
        try
        {
            using var viewModel = new SettingsViewModel(
                new FakeCardTerminalSetupService(),
                localization,
                session: session,
                catalogSyncStatusService: syncStatus);
            await viewModel.LoadAsync();

            Assert.Equal("上次同步", viewModel.CatalogSyncTimeLabelText);
            Assert.Equal("尚未同步", viewModel.CatalogSyncTimeText);
            Assert.False(viewModel.IsCatalogSyncSucceeded);
            Assert.False(viewModel.IsCatalogSyncing);
            Assert.False(viewModel.IsCatalogSyncFailed);

            var notifications = new List<string?>();
            viewModel.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
            syncStatus.MarkStarted("S01");
            Assert.True(viewModel.IsCatalogSyncing);
            Assert.Contains(nameof(viewModel.IsCatalogSyncing), notifications);

            await syncStatus.MarkSucceededAsync("S01");
            var expectedTime = succeededAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            Assert.Equal(expectedTime, viewModel.CatalogSyncTimeText);
            Assert.True(viewModel.IsCatalogSyncSucceeded);
            Assert.False(viewModel.IsCatalogSyncing);

            var failedAt = succeededAt.AddMinutes(8);
            clock.UtcNow = failedAt;
            syncStatus.MarkStarted("S01");
            syncStatus.MarkFailed("S01", "网络连接超时");
            Assert.True(viewModel.IsCatalogSyncFailed);
            Assert.False(viewModel.IsCatalogSyncSucceeded);
            Assert.Equal("上次成功同步", viewModel.CatalogSyncTimeLabelText);
            Assert.Equal(expectedTime, viewModel.CatalogSyncTimeText);
            Assert.Equal(
                $"{failedAt.ToLocalTime():yyyy-MM-dd HH:mm} 同步失败：网络连接超时",
                viewModel.CatalogSyncFailureText);

            localization.SetCulture("en-US");
            Assert.Equal("Last successful sync", viewModel.CatalogSyncTimeLabelText);
            Assert.StartsWith("Sync failed at ", viewModel.CatalogSyncFailureText, StringComparison.Ordinal);
        }
        finally
        {
            localization.SetCulture("en-US");
        }
    }

    [Fact]
    public async Task Settings_reload_shows_persisted_catalog_sync_time()
    {
        var repository = new CatalogSyncStatusServiceTests.InMemoryAppSettingsRepository();
        var succeededAt = new DateTimeOffset(2026, 9, 25, 9, 15, 0, TimeSpan.Zero);
        repository.Values[CatalogSyncStatusService.LastSucceededAtKeyPrefix + "S01"] = succeededAt.ToString("O");
        var session = new PosSessionState("HB POS", "S01", "Store", "D01", string.Empty, string.Empty, true, 0);
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            session: session,
            catalogSyncStatusService: new CatalogSyncStatusService(repository));

        await viewModel.LoadAsync();

        Assert.Equal(succeededAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), viewModel.CatalogSyncTimeText);
        Assert.True(viewModel.IsCatalogSyncSucceeded);
        Assert.Equal("Last sync", viewModel.CatalogSyncTimeLabelText);
    }

    [Fact]
    public void Data_download_card_merges_sync_status_into_single_panel_above_action()
    {
        var xamlPath = Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Screens",
            "SettingsView.xaml");
        var document = System.Xml.Linq.XDocument.Load(xamlPath);
        var xamlNamespace = System.Xml.Linq.XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var downloadButton = Assert.Single(document.Descendants().Where(element =>
            element.Name.LocalName == "Button" &&
            element.Attribute("Command")?.Value == "{Binding DownloadCatalogCommand}"));
        var cardGrid = downloadButton.Parent!;
        var statusPanel = Assert.Single(cardGrid.Elements().Where(element =>
            element.Attribute(xamlNamespace + "Name")?.Value == "CatalogSyncStatusPanel"));
        var panelBindings = statusPanel.Descendants()
            .SelectMany(element => element.Attributes())
            .Select(attribute => attribute.Value)
            .ToArray();

        Assert.Equal("1", statusPanel.Attribute("Grid.Row")?.Value);
        Assert.Equal("2", downloadButton.Attribute("Grid.Row")?.Value);
        foreach (var binding in new[]
                 {
                     "{Binding CatalogSyncTimeLabelText}",
                     "{Binding CatalogSyncTimeText}",
                     "{Binding CatalogSyncFailureText}",
                     "{Binding IsCatalogSyncSucceeded, Converter={StaticResource BoolToVis}}",
                     "{Binding IsCatalogSyncing, Converter={StaticResource BoolToVis}}",
                     "{Binding IsCatalogSyncFailed, Converter={StaticResource BoolToVis}}"
                 })
        {
            Assert.Contains(binding, panelBindings);
        }

        // 原卡片底部的通用状态框已并入同步信息块，页面级提示只保留在底部状态栏。
        Assert.DoesNotContain(cardGrid.Descendants(), element =>
            element.Attribute("Text")?.Value == "{Binding StatusMessage}");
        Assert.Single(document.Descendants().Where(element =>
            element.Name.LocalName == "TextBlock" &&
            element.Attribute("Text")?.Value == "{Binding StatusMessage}"));
    }

    [Fact]
    public async Task ResetCatalogCommand_calls_injected_reset_delegate()
    {
        var resetCallCount = 0;
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            resetCatalogAsync: cancellationToken =>
            {
                Assert.False(cancellationToken.IsCancellationRequested);
                resetCallCount++;
                return Task.CompletedTask;
            });

        await viewModel.ResetCatalogCommand.ExecuteAsync(null);

        Assert.Equal(1, resetCallCount);
        Assert.Equal("Catalog data reset completed.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ResetCatalogCommand_records_catalog_reset_operation_audit()
    {
        var logger = new RecordingOperationAuditLogger();
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            resetCatalogAsync: _ => Task.CompletedTask,
            session: CreateAuditSession(),
            operationAuditLogger: logger);

        await viewModel.ResetCatalogCommand.ExecuteAsync(null);

        // 重置商品目录会清空本机价格与商品，必须留下谁在何时执行的审计。
        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("CATALOG_RESET", auditEvent.OperationType);
        Assert.Equal("Succeeded", auditEvent.Outcome);
        Assert.Equal("SETTINGS", auditEvent.ReasonCode);
        Assert.Equal("C001", auditEvent.CashierId);
    }

    [Fact]
    public async Task ResetCatalogCommand_failure_records_failed_audit_and_keeps_status_handling()
    {
        var logger = new RecordingOperationAuditLogger();
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            resetCatalogAsync: _ => Task.FromException(new InvalidOperationException("catalog store locked")),
            session: CreateAuditSession(),
            operationAuditLogger: logger);

        await viewModel.ResetCatalogCommand.ExecuteAsync(null);

        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("CATALOG_RESET", auditEvent.OperationType);
        Assert.Equal("Failed", auditEvent.Outcome);
        Assert.Equal(nameof(InvalidOperationException), auditEvent.SafeMessage);
        Assert.False(viewModel.IsBusy);
    }

    [Theory]
    [InlineData(true, "Succeeded")]
    [InlineData(false, "Failed")]
    public async Task InstallRemoteMaintenanceCommand_records_install_result_audit(bool succeeded, string expectedOutcome)
    {
        var logger = new RecordingOperationAuditLogger();
        var remoteService = new FakeRemoteMaintenanceService
        {
            InstallResult = new RemoteMaintenanceProvisionResult(
                succeeded,
                succeeded
                    ? "settings.remoteMaintenance.result.configured"
                    : "settings.remoteMaintenance.result.configurationFailed",
                new RemoteMaintenanceStatus(succeeded, succeeded ? "rustdesk-test-id" : string.Empty, string.Empty, succeeded ? "running" : "notInstalled"))
        };
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            session: CreateAuditSession(),
            remoteMaintenanceService: remoteService,
            operationAuditLogger: logger);

        await viewModel.InstallRemoteMaintenanceCommand.ExecuteAsync(null);

        // 安装远程维护会开放远程控制，按真实安装结果留痕。
        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("REMOTE_MAINTENANCE_INSTALL", auditEvent.OperationType);
        Assert.Equal(expectedOutcome, auditEvent.Outcome);
        Assert.Equal(remoteService.InstallResult.Message, auditEvent.SafeMessage);
        Assert.Equal("C001", auditEvent.CashierId);
    }

    [Fact]
    public async Task InstallRemoteMaintenanceCommand_exception_records_failed_audit()
    {
        var logger = new RecordingOperationAuditLogger();
        var remoteService = new FakeRemoteMaintenanceService
        {
            InstallHandler = _ => Task.FromException<RemoteMaintenanceProvisionResult>(new IOException("disk full"))
        };
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            session: CreateAuditSession(),
            remoteMaintenanceService: remoteService,
            operationAuditLogger: logger);

        await viewModel.InstallRemoteMaintenanceCommand.ExecuteAsync(null);

        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("REMOTE_MAINTENANCE_INSTALL", auditEvent.OperationType);
        Assert.Equal("Failed", auditEvent.Outcome);
        Assert.Equal(nameof(IOException), auditEvent.SafeMessage);
    }

    private static PosSessionState CreateAuditSession() =>
        new("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

    private sealed class RecordingOperationAuditLogger : IOperationAuditLogger
    {
        public List<OperationAuditEventDto> Events { get; } = [];

        public void Record(OperationAuditEventDto auditEvent)
        {
            Events.Add(auditEvent);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackCommand_cancels_running_catalog_operation(bool resetCatalog)
    {
        var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration operationCancellationRegistration = default;
        var receivedToken = CancellationToken.None;
        var returnedToPos = false;
        var cancellationRequestedBeforeNavigation = false;
        var cancellationApplied = false;
        Task RunCatalogOperationAsync(CancellationToken cancellationToken)
        {
            receivedToken = cancellationToken;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            operationCancellationRegistration = cancellationToken.Register(() =>
            {
                cancellationApplied = completion.TrySetCanceled(cancellationToken);
                cancellationObserved.TrySetResult();
            });
            operationStarted.TrySetResult();
            return completion.Task;
        }

        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            downloadCatalogAsync: resetCatalog ? null : RunCatalogOperationAsync,
            resetCatalogAsync: resetCatalog ? RunCatalogOperationAsync : null,
            returnToPos: () =>
            {
                cancellationRequestedBeforeNavigation = receivedToken.IsCancellationRequested;
                returnedToPos = true;
            });
        var catalogCommand = resetCatalog
            ? viewModel.ResetCatalogCommand
            : viewModel.DownloadCatalogCommand;

        var execution = catalogCommand.ExecuteAsync(null);
        await operationStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.True(viewModel.BackCommand.CanExecute(null));
        viewModel.BackCommand.Execute(null);
        await cancellationObserved.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        var wasCancellationRequested = receivedToken.IsCancellationRequested;
        await execution.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        operationCancellationRegistration.Dispose();

        Assert.True(returnedToPos);
        Assert.True(cancellationRequestedBeforeNavigation);
        Assert.True(receivedToken.CanBeCanceled);
        Assert.True(wasCancellationRequested);
        Assert.True(cancellationApplied);
        Assert.Equal("Operation canceled.", viewModel.StatusMessage);
    }

    [Fact]
    public void ResetTestSalesDataCommand_matches_build_configuration_when_service_is_configured()
    {
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            resetTestSalesDataAsync: _ => Task.CompletedTask,
            confirmResetTestSalesDataAsync: () => Task.FromResult(true));

#if DEBUG
        Assert.True(viewModel.IsDebugTestSalesDataResetVisible);
        Assert.True(viewModel.ResetTestSalesDataCommand.CanExecute(null));
#else
        Assert.False(viewModel.IsDebugTestSalesDataResetVisible);
        Assert.False(viewModel.ResetTestSalesDataCommand.CanExecute(null));
#endif
    }

    [Fact]
    public async Task ResetTestSalesDataCommand_does_not_call_reset_when_confirmation_is_cancelled()
    {
        var resetCallCount = 0;
        var confirmCallCount = 0;
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            resetTestSalesDataAsync: _ =>
            {
                resetCallCount++;
                return Task.CompletedTask;
            },
            confirmResetTestSalesDataAsync: () =>
            {
                confirmCallCount++;
                return Task.FromResult(false);
            });

        await viewModel.ResetTestSalesDataCommand.ExecuteAsync(null);

#if DEBUG
        Assert.Equal(1, confirmCallCount);
        Assert.Equal("Ready.", viewModel.StatusMessage);
#else
        Assert.Equal(0, confirmCallCount);
        Assert.Equal("Test sales data reset service is not configured.", viewModel.StatusMessage);
#endif
        Assert.Equal(0, resetCallCount);
    }

    [Fact]
    public async Task ResetTestSalesDataCommand_calls_reset_after_confirmation()
    {
        var resetCallCount = 0;
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            resetTestSalesDataAsync: cancellationToken =>
            {
                Assert.False(cancellationToken.IsCancellationRequested);
                resetCallCount++;
                return Task.CompletedTask;
            },
            confirmResetTestSalesDataAsync: () => Task.FromResult(true));

        await viewModel.ResetTestSalesDataCommand.ExecuteAsync(null);

#if DEBUG
        Assert.Equal(1, resetCallCount);
        Assert.Equal("Local test sales data deleted.", viewModel.StatusMessage);
#else
        Assert.Equal(0, resetCallCount);
        Assert.Equal("Test sales data reset service is not configured.", viewModel.StatusMessage);
#endif
    }

    [Fact]
    public async Task ResetTestSalesDataCommand_does_not_call_reset_without_confirmation_delegate()
    {
        var resetCallCount = 0;
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            resetTestSalesDataAsync: _ =>
            {
                resetCallCount++;
                return Task.CompletedTask;
            });

        await viewModel.ResetTestSalesDataCommand.ExecuteAsync(null);

        Assert.Equal(0, resetCallCount);
#if DEBUG
        Assert.Equal("Ready.", viewModel.StatusMessage);
#else
        Assert.Equal("Test sales data reset service is not configured.", viewModel.StatusMessage);
#endif
    }

    [Fact]
    public async Task ReregisterDeviceCommand_calls_injected_reregister_delegate()
    {
        var reregisterCallCount = 0;
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            reregisterDeviceAsync: () =>
            {
                reregisterCallCount++;
                return Task.FromResult(DeviceReregistrationStartResult.StartedWith("Select a new store."));
            });

        await viewModel.ReregisterDeviceCommand.ExecuteAsync(null);

        Assert.Equal(1, reregisterCallCount);
    }

    [Fact]
    public async Task ReregisterDeviceCommand_shows_blocked_reason_on_settings_status()
    {
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            reregisterDeviceAsync: () => Task.FromResult(DeviceReregistrationStartResult.Blocked("存在待同步订单。")));

        await viewModel.ReregisterDeviceCommand.ExecuteAsync(null);

        Assert.Equal("存在待同步订单。", viewModel.StatusMessage);
    }

    [Fact]
    public async Task LoadDevicesCommand_requires_selected_location()
    {
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService(squareAccessToken: CachedToken));

        await viewModel.LoadAsync();

        Assert.False(viewModel.LoadDevicesCommand.CanExecute(null));
    }

    [Fact]
    public async Task LoadAsync_does_not_create_local_square_location_or_device_options()
    {
        var configuration = CardTerminalConfiguration.Default with
        {
            Processor = CardProcessorKind.Square,
            SquareLocationId = "LOCAL-LOC",
            SquareDeviceId = "LOCAL-DEV",
            HasProtectedSquareAccessToken = true
        };
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService(configuration, CachedToken));

        await viewModel.LoadAsync();

        Assert.Empty(viewModel.SquareLocations);
        Assert.Empty(viewModel.SquareDevices);
        Assert.Null(viewModel.SelectedSquareLocation);
        Assert.Null(viewModel.SelectedSquareDevice);
        Assert.False(viewModel.SaveSquareCommand.CanExecute(null));
    }

    [Fact]
    public async Task LoadLocationsCommand_in_sandbox_auto_loads_devices_for_single_location()
    {
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with
            {
                Processor = CardProcessorKind.Square,
                Environment = CardTerminalEnvironment.Sandbox,
                HasProtectedSquareAccessToken = true
            },
            CachedToken);
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();
        await viewModel.LoadLocationsCommand.ExecuteAsync(null);

        Assert.Equal(CardTerminalEnvironment.Sandbox, service.LastListSquareLocationsEnvironment);
        Assert.Equal(CardTerminalEnvironment.Sandbox, service.LastListSquareDevicesEnvironment);
        Assert.Single(viewModel.SquareLocations);
        Assert.Single(viewModel.SquareDevices);
        Assert.NotNull(viewModel.SelectedSquareLocation);
        Assert.Equal("LOC-1", viewModel.SelectedSquareLocation!.Id);
    }

    [Fact]
    public async Task LoadLocationsCommand_in_production_keeps_device_loading_manual()
    {
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with
            {
                Processor = CardProcessorKind.Square,
                Environment = CardTerminalEnvironment.Production,
                HasProtectedSquareAccessToken = true
            },
            CachedToken);
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();
        await viewModel.LoadLocationsCommand.ExecuteAsync(null);

        Assert.Equal(CardTerminalEnvironment.Production, service.LastListSquareLocationsEnvironment);
        Assert.Null(service.LastListSquareDevicesEnvironment);
        Assert.Single(viewModel.SquareLocations);
        Assert.Empty(viewModel.SquareDevices);
        Assert.Null(viewModel.SelectedSquareLocation);
    }

    [Fact]
    public async Task LoadAsync_uses_backend_square_token_status_text()
    {
        var configuredViewModel = new SettingsViewModel(new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with
            {
                Processor = CardProcessorKind.Square,
                HasProtectedSquareAccessToken = true
            }));
        var missingViewModel = new SettingsViewModel(new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with
            {
                Processor = CardProcessorKind.Square,
                HasProtectedSquareAccessToken = false
            }));

        await configuredViewModel.LoadAsync();
        await missingViewModel.LoadAsync();

        Assert.Equal("Square token is configured in HBPOS for this environment.", configuredViewModel.SquareTokenStatusText);
        Assert.Equal("Square token is not configured in HBPOS for this environment.", missingViewModel.SquareTokenStatusText);
    }

    [Fact]
    public async Task SaveSquareCommand_requires_device_loaded_from_square_api()
    {
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService())
        {
            SelectedSquareLocation = new SquareLocationOption("LOC-1", "Main"),
            SelectedSquareDevice = new SquareDeviceOption("DEV-1", "Counter", "AVAILABLE")
        };

        await viewModel.LoadAsync();
        viewModel.SelectedSquareLocation = new SquareLocationOption("LOC-1", "Main");
        viewModel.SelectedSquareDevice = new SquareDeviceOption("DEV-1", "Counter", "AVAILABLE");

        Assert.False(viewModel.SaveSquareCommand.CanExecute(null));
    }

    [Fact]
    public async Task SaveSquareCommand_saves_selected_square_terminal_after_remote_lists_are_loaded()
    {
        var service = new FakeCardTerminalSetupService();
        var viewModel = new SettingsViewModel(service)
        {
            IsSquareSandbox = true,
            TimeoutSecondsText = "45"
        };

        await viewModel.LoadLocationsCommand.ExecuteAsync(null);
        viewModel.SelectedSquareLocation = viewModel.SquareLocations.Single();
        await viewModel.LoadDevicesCommand.ExecuteAsync(null);
        viewModel.SelectedSquareDevice = viewModel.SquareDevices.Single();
        await viewModel.SaveSquareCommand.ExecuteAsync(null);

        Assert.NotNull(service.SavedConfiguration);
        Assert.Equal(CardProcessorKind.Square, service.SavedConfiguration!.Processor);
        Assert.Equal(CardTerminalEnvironment.Sandbox, service.SavedConfiguration.Environment);
        Assert.Equal("LOC-1", service.SavedConfiguration.SquareLocationId);
        Assert.Equal("DEV-1", service.SavedConfiguration.SquareDeviceId);
        Assert.Equal(45, service.SavedConfiguration.TerminalTimeoutSeconds);
        Assert.Null(service.SavedSquareAccessToken);
        Assert.Equal("Square is active for the next card payment.", viewModel.ActivePaymentProviderText);
        Assert.Equal("Square terminal settings saved. The next payment will use Counter.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Square_environment_change_clears_only_square_state()
    {
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService(squareAccessToken: CachedToken))
        {
            LinklyCloudUsernameText = "cloud-user",
            LinklyCloudPasswordText = "cloud-password",
            LinklyPairCodeText = "123456",
            HasSavedLinklyCloudPassword = true,
            HasSavedLinklyCloudSecret = true
        };

        await viewModel.LoadLocationsCommand.ExecuteAsync(null);
        viewModel.SelectedSquareLocation = viewModel.SquareLocations.Single();
        await viewModel.LoadDevicesCommand.ExecuteAsync(null);

        viewModel.IsSquareSandbox = true;

        Assert.Empty(viewModel.SquareLocations);
        Assert.Empty(viewModel.SquareDevices);
        Assert.Null(viewModel.SelectedSquareLocation);
        Assert.Null(viewModel.SelectedSquareDevice);
        Assert.Equal("cloud-user", viewModel.LinklyCloudUsernameText);
        Assert.Equal("cloud-password", viewModel.LinklyCloudPasswordText);
        Assert.Equal("123456", viewModel.LinklyPairCodeText);
        Assert.True(viewModel.HasSavedLinklyCloudPassword);
        Assert.True(viewModel.HasSavedLinklyCloudSecret);
    }

    [Fact]
    public async Task Linkly_environment_change_clears_only_linkly_state()
    {
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService(squareAccessToken: CachedToken))
        {
            LinklyCloudUsernameText = "cloud-user",
            LinklyCloudPasswordText = "cloud-password",
            LinklyPairCodeText = "123456",
            HasSavedLinklyCloudPassword = true,
            HasSavedLinklyCloudSecret = true
        };

        await viewModel.LoadLocationsCommand.ExecuteAsync(null);
        viewModel.SelectedSquareLocation = viewModel.SquareLocations.Single();
        await viewModel.LoadDevicesCommand.ExecuteAsync(null);
        viewModel.SelectedSquareDevice = viewModel.SquareDevices.Single();

        viewModel.IsLinklySandbox = true;

        Assert.Single(viewModel.SquareLocations);
        Assert.Single(viewModel.SquareDevices);
        Assert.NotNull(viewModel.SelectedSquareLocation);
        Assert.NotNull(viewModel.SelectedSquareDevice);
        Assert.Equal(string.Empty, viewModel.LinklyCloudUsernameText);
        Assert.Equal(string.Empty, viewModel.LinklyCloudPasswordText);
        Assert.Equal(string.Empty, viewModel.LinklyPairCodeText);
        Assert.False(viewModel.HasSavedLinklyCloudPassword);
        Assert.False(viewModel.HasSavedLinklyCloudSecret);
    }

    [Fact]
    public async Task Square_and_linkly_commands_use_separate_edit_environments()
    {
        var service = new FakeCardTerminalSetupService(squareAccessToken: CachedToken)
        {
            LinklyCloudTestResult = new LinklyConnectionTestResult(true, "cloud connected")
        };
        var viewModel = new SettingsViewModel(service)
        {
            IsSquareSandbox = true,
            IsLinklyCloudMode = true,
            IsLinklySandbox = false,
            HasSavedLinklyCloudSecret = true
        };

        await viewModel.LoadLocationsCommand.ExecuteAsync(null);
        viewModel.SelectedSquareLocation = viewModel.SquareLocations.Single();
        await viewModel.LoadDevicesCommand.ExecuteAsync(null);
        viewModel.SelectedSquareDevice = viewModel.SquareDevices.Single();
        await viewModel.SaveSquareCommand.ExecuteAsync(null);
        await viewModel.TestLinklyCommand.ExecuteAsync(null);

        Assert.Equal(CardTerminalEnvironment.Sandbox, service.LastListSquareLocationsEnvironment);
        Assert.Equal(CardTerminalEnvironment.Sandbox, service.LastListSquareDevicesEnvironment);
        Assert.Equal(CardTerminalEnvironment.Sandbox, service.LastSaveSquareEnvironment);
        Assert.Equal(CardTerminalEnvironment.Production, service.LastLinklyCloudTestEnvironment);
    }

    [Fact]
    public async Task SaveSquareCommand_saves_normalized_square_terminal_device_id()
    {
        var service = new FakeCardTerminalSetupService(squareAccessToken: CachedToken)
        {
            SquareDevicesResult = [new("device:533CS145C3000413", "Square Terminal 0413", "AVAILABLE")]
        };
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadLocationsCommand.ExecuteAsync(null);
        viewModel.SelectedSquareLocation = viewModel.SquareLocations.Single();
        await viewModel.LoadDevicesCommand.ExecuteAsync(null);
        viewModel.SelectedSquareDevice = viewModel.SquareDevices.Single();
        await viewModel.SaveSquareCommand.ExecuteAsync(null);

        Assert.NotNull(service.SavedConfiguration);
        Assert.Equal("533CS145C3000413", service.SavedConfiguration!.SquareDeviceId);
    }

    [Fact]
    public async Task SaveSquareCommand_switches_to_another_device_in_same_location()
    {
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with
            {
                Processor = CardProcessorKind.Square,
                SquareLocationId = "LOC-1",
                SquareDeviceId = "DEV-1",
                HasProtectedSquareAccessToken = true
            },
            CachedToken)
        {
            SquareDevicesResult =
            [
                new("DEV-1", "Counter 1", "AVAILABLE"),
                new("DEV-2", "Counter 2", "AVAILABLE")
            ]
        };
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();
        await viewModel.LoadLocationsCommand.ExecuteAsync(null);
        viewModel.SelectedSquareLocation = viewModel.SquareLocations.Single();
        await viewModel.LoadDevicesCommand.ExecuteAsync(null);
        viewModel.SelectedSquareDevice = viewModel.SquareDevices.Last();

        Assert.Equal("Selected Counter 2. Save Square to switch the next payment to this terminal.", viewModel.StatusMessage);

        await viewModel.SaveSquareCommand.ExecuteAsync(null);

        Assert.NotNull(service.SavedConfiguration);
        Assert.Equal("DEV-2", service.SavedConfiguration!.SquareDeviceId);
        Assert.Equal("Square terminal settings saved. The next payment will use Counter 2.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Device_code_commands_are_disabled_in_sandbox_mode()
    {
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with
            {
                Environment = CardTerminalEnvironment.Sandbox
            },
            CachedToken));

        await viewModel.LoadAsync();

        Assert.False(viewModel.IsSquareDeviceCodesSupported);
        Assert.False(viewModel.LoadDeviceCodesCommand.CanExecute(null));
        Assert.False(viewModel.CreateDeviceCodeCommand.CanExecute(null));
        Assert.False(viewModel.RefreshDeviceCodeStatusCommand.CanExecute(null));
    }

    [Fact]
    public async Task CreateDeviceCodeCommand_creates_and_selects_new_code()
    {
        var service = new FakeCardTerminalSetupService()
        {
            CreateDeviceCodeResult = new("DC-1", "Counter 3", "PAIR123", "UNPAIRED", "LOC-1", null, DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow)
        };
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadLocationsCommand.ExecuteAsync(null);
        viewModel.SelectedSquareLocation = viewModel.SquareLocations.Single();
        viewModel.SquareDeviceCodeNameText = "Counter 3";
        await viewModel.CreateDeviceCodeCommand.ExecuteAsync(null);

        Assert.NotNull(service.LastCreatedDeviceCodeRequest);
        Assert.Equal("LOC-1", service.LastCreatedDeviceCodeRequest!.Value.LocationId);
        Assert.Equal("Counter 3", service.LastCreatedDeviceCodeRequest.Value.Name);
        Assert.Single(viewModel.SquareDeviceCodes);
        Assert.Equal("PAIR123", viewModel.SelectedSquareDeviceCode!.Code);
        Assert.Equal("Created device code PAIR123 for Counter 3. Enter it on the Square Terminal, then refresh status.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task RefreshDeviceCodeStatusCommand_pairs_and_selects_matching_device_without_saving()
    {
        var service = new FakeCardTerminalSetupService(squareAccessToken: CachedToken)
        {
            SquareDevicesResult =
            [
                new("DEV-1", "Counter 1", "AVAILABLE"),
                new("DEV-2", "Counter 2", "AVAILABLE")
            ],
            GetDeviceCodeResult = new("DC-1", "Counter 2", "PAIR123", "PAIRED", "LOC-1", "DEV-2", DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow)
        };
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadLocationsCommand.ExecuteAsync(null);
        viewModel.SelectedSquareLocation = viewModel.SquareLocations.Single();
        await viewModel.LoadDevicesCommand.ExecuteAsync(null);
        await viewModel.CreateDeviceCodeCommand.ExecuteAsync(null);
        await viewModel.RefreshDeviceCodeStatusCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.SelectedSquareDevice);
        Assert.Equal("DEV-2", viewModel.SelectedSquareDevice!.Id);
        Assert.Null(service.SavedConfiguration);
        Assert.Equal("Device code paired successfully. Counter 2 is selected and ready to save for the next payment.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task RefreshDeviceCodeStatusCommand_matches_devices_api_id_to_device_code_id()
    {
        var service = new FakeCardTerminalSetupService(squareAccessToken: CachedToken)
        {
            SquareDevicesResult =
            [
                new("device:533CS145C3000413", "Square Terminal 0413", "AVAILABLE")
            ],
            GetDeviceCodeResult = new("DC-1", "Square Terminal 0413", "PAIR123", "PAIRED", "LOC-1", "533CS145C3000413", DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow)
        };
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadLocationsCommand.ExecuteAsync(null);
        viewModel.SelectedSquareLocation = viewModel.SquareLocations.Single();
        await viewModel.LoadDevicesCommand.ExecuteAsync(null);
        await viewModel.CreateDeviceCodeCommand.ExecuteAsync(null);
        await viewModel.RefreshDeviceCodeStatusCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.SelectedSquareDevice);
        Assert.Equal("device:533CS145C3000413", viewModel.SelectedSquareDevice!.Id);
        Assert.Null(service.SavedConfiguration);
    }

    [Fact]
    public async Task LoadDevicesCommand_selects_saved_device_when_saved_id_has_devices_api_prefix()
    {
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with
            {
                Processor = CardProcessorKind.Square,
                SquareLocationId = "LOC-1",
                SquareDeviceId = "device:533CS145C3000413",
                HasProtectedSquareAccessToken = true
            },
            CachedToken)
        {
            SquareDevicesResult =
            [
                new("device:533CS145C3000413", "Square Terminal 0413", "AVAILABLE")
            ]
        };
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();
        await viewModel.LoadLocationsCommand.ExecuteAsync(null);
        await viewModel.LoadDevicesCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.SelectedSquareDevice);
        Assert.Equal("device:533CS145C3000413", viewModel.SelectedSquareDevice!.Id);
    }

    [Fact]
    public async Task SaveLinklyCommand_requires_successful_connection_test()
    {
        var service = new FakeCardTerminalSetupService();
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();

        Assert.False(viewModel.SaveLinklyCommand.CanExecute(null));
        Assert.Null(service.SavedConfiguration);
    }

    [Fact]
    public async Task LoadAsync_requires_fresh_linkly_test_even_when_linkly_was_previously_enabled()
    {
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default with
        {
            Processor = CardProcessorKind.Linkly
        });
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();

        Assert.False(viewModel.LinklyConnectionSucceeded);
        Assert.False(viewModel.SaveLinklyCommand.CanExecute(null));
    }

    [Fact]
    public async Task TestLinklyCommand_allows_saving_linkly_as_active_processor()
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyTestResult = new LinklyConnectionTestResult(true, "connected")
        };
        var viewModel = new SettingsViewModel(service)
        {
            LinklyHostText = "192.168.1.10",
            LinklyPortText = "2011",
            TimeoutSecondsText = "180"
        };

        await viewModel.TestLinklyCommand.ExecuteAsync(null);
        await viewModel.SaveLinklyCommand.ExecuteAsync(null);

        Assert.Equal("connected", viewModel.LinklyTestStatusMessage);
        Assert.NotNull(service.SavedConfiguration);
        Assert.Equal(CardProcessorKind.Linkly, service.SavedConfiguration!.Processor);
        Assert.Equal("192.168.1.10", service.SavedConfiguration.LinklyHost);
        Assert.Equal(2011, service.SavedConfiguration.LinklyPort);
        Assert.Equal("ANZ Linkly is active for the next card payment.", viewModel.ActivePaymentProviderText);
        Assert.Equal("ANZ Linkly terminal settings saved.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task TestLinklyCommand_online_but_not_logged_on_enables_save_and_logon()
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyTestResult = new LinklyConnectionTestResult(
                true,
                "Linkly PINpad is online but not logged on to the bank network.",
                PinPadLoggedOn: false)
        };
        var viewModel = new SettingsViewModel(service);

        await viewModel.TestLinklyCommand.ExecuteAsync(null);

        Assert.True(viewModel.LinklyConnectionSucceeded);
        Assert.False(viewModel.LinklyPinPadLoggedOn);
        Assert.True(viewModel.SaveLinklyCommand.CanExecute(null));
        Assert.True(viewModel.LogonLinklyCommand.CanExecute(null));
    }

    [Fact]
    public async Task TestLinklyCommand_already_logged_on_keeps_logon_disabled()
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyTestResult = new LinklyConnectionTestResult(
                true,
                "connected",
                PinPadLoggedOn: true)
        };
        var viewModel = new SettingsViewModel(service);

        await viewModel.TestLinklyCommand.ExecuteAsync(null);

        Assert.True(viewModel.LinklyConnectionSucceeded);
        Assert.True(viewModel.LinklyPinPadLoggedOn);
        Assert.True(viewModel.SaveLinklyCommand.CanExecute(null));
        Assert.False(viewModel.LogonLinklyCommand.CanExecute(null));
    }

    [Fact]
    public async Task LogonLinklyCommand_success_marks_logged_on_without_resaving_configuration()
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyTestResult = new LinklyConnectionTestResult(true, "online", PinPadLoggedOn: false),
            LinklyLogonResult = new LinklyLogonResult(true, "logged on", "00", "APPROVED")
        };
        var viewModel = new SettingsViewModel(service);
        await viewModel.TestLinklyCommand.ExecuteAsync(null);

        await viewModel.LogonLinklyCommand.ExecuteAsync(null);

        Assert.Equal(1, service.LinklyLogonCallCount);
        Assert.True(viewModel.LinklyConnectionSucceeded);
        Assert.True(viewModel.LinklyPinPadLoggedOn);
        Assert.True(viewModel.SaveLinklyCommand.CanExecute(null));
        Assert.False(viewModel.LogonLinklyCommand.CanExecute(null));
        Assert.Equal("logged on", viewModel.LinklyTestStatusMessage);
        Assert.Equal(0, service.SaveLinklyCallCount);
    }

    [Theory]
    [InlineData(false, "logon failed")]
    [InlineData(true, "logon result unknown")]
    public async Task LogonLinklyCommand_failure_or_unknown_keeps_enable_and_retry_available(
        bool resultUnknown,
        string message)
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyTestResult = new LinklyConnectionTestResult(true, "online", PinPadLoggedOn: false),
            LinklyLogonResult = new LinklyLogonResult(false, message, ResultUnknown: resultUnknown)
        };
        var viewModel = new SettingsViewModel(service);
        await viewModel.TestLinklyCommand.ExecuteAsync(null);

        await viewModel.LogonLinklyCommand.ExecuteAsync(null);

        Assert.True(viewModel.LinklyConnectionSucceeded);
        Assert.False(viewModel.LinklyPinPadLoggedOn);
        Assert.True(viewModel.SaveLinklyCommand.CanExecute(null));
        Assert.True(viewModel.LogonLinklyCommand.CanExecute(null));
        Assert.Equal(message, viewModel.LinklyTestStatusMessage);
    }

    [Fact]
    public async Task LogonLinklyCommand_disables_duplicate_click_while_running()
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyTestResult = new LinklyConnectionTestResult(true, "online", PinPadLoggedOn: false),
            LinklyLogonResult = new LinklyLogonResult(true, "logged on")
        };
        var viewModel = new SettingsViewModel(service);
        await viewModel.TestLinklyCommand.ExecuteAsync(null);
        service.BlockNextLinklyLogon();

        var logonTask = viewModel.LogonLinklyCommand.ExecuteAsync(null);
        await service.WaitForLinklyLogonStartAsync();

        Assert.Equal(1, service.LinklyLogonCallCount);
        Assert.False(viewModel.LogonLinklyCommand.CanExecute(null));
        Assert.False(viewModel.SaveLinklyCommand.CanExecute(null));

        service.ReleaseLinklyLogon();
        await logonTask;
    }

    [Fact]
    public async Task LogonLinklyCommand_discards_success_when_endpoint_changes_during_logon()
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyTestResult = new LinklyConnectionTestResult(true, "online", PinPadLoggedOn: false),
            LinklyLogonResult = new LinklyLogonResult(true, "logged on")
        };
        var viewModel = new SettingsViewModel(service);
        await viewModel.TestLinklyCommand.ExecuteAsync(null);
        service.BlockNextLinklyLogon();

        var logonTask = viewModel.LogonLinklyCommand.ExecuteAsync(null);
        await service.WaitForLinklyLogonStartAsync();
        viewModel.LinklyHostText = "127.0.0.2";
        service.ReleaseLinklyLogon();
        await logonTask;

        Assert.False(viewModel.LinklyConnectionSucceeded);
        Assert.Null(viewModel.LinklyPinPadLoggedOn);
        Assert.False(viewModel.SaveLinklyCommand.CanExecute(null));
        Assert.False(viewModel.LogonLinklyCommand.CanExecute(null));
        Assert.Equal(
            "Linkly settings changed during the test. Test the current settings again.",
            viewModel.LinklyTestStatusMessage);
    }

    [Fact]
    public async Task LogonLinklyCommand_permission_denied_does_not_call_terminal()
    {
        var service = new FakeCardTerminalSetupService();
        var viewModel = new SettingsViewModel(
            service,
            enforcePermissionsWhenNoCashier: true)
        {
            LinklyConnectionSucceeded = true,
            LinklyPinPadLoggedOn = false
        };

        await viewModel.LogonLinklyCommand.ExecuteAsync(null);

        Assert.Equal(0, service.LinklyLogonCallCount);
    }

    [Fact]
    public async Task TestLinklyCommand_shows_failed_result_near_linkly_controls()
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyTestResult = new LinklyConnectionTestResult(false, "connection failed")
        };
        var viewModel = new SettingsViewModel(service);

        await viewModel.TestLinklyCommand.ExecuteAsync(null);

        Assert.False(viewModel.LinklyConnectionSucceeded);
        Assert.False(viewModel.SaveLinklyCommand.CanExecute(null));
        Assert.Equal("connection failed", viewModel.LinklyTestStatusMessage);
        Assert.Equal("connection failed", viewModel.StatusMessage);
    }

    [Fact]
    public void LocalIp_sandbox_uses_virtual_pin_pad_text_and_safety_hint()
    {
        var localization = new LocalizationService();
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService(), localization)
        {
            IsLinklySandbox = true
        };

        Assert.True(viewModel.IsLinklySandboxLocalIpMode);
        Assert.Equal("Test virtual PIN pad", viewModel.LinklyTestActionText);

        localization.SetCulture("zh-CN");

        Assert.Equal("\u6D4B\u8BD5\u865A\u62DF\u5237\u5361\u673A", viewModel.LinklyTestActionText);

        viewModel.SelectedLinklyMode = LinklySettingsMode.CloudBackendAsync;

        Assert.False(viewModel.IsLinklySandboxLocalIpMode);
        Assert.Equal("\u6D4B\u8BD5 Linkly", viewModel.LinklyTestActionText);
    }

    [Theory]
    [InlineData("", "2011", "30", "Enter the Linkly Local IP host.")]
    [InlineData("127.0.0.1", "0", "30", "Enter a Linkly port from 1 to 65535.")]
    [InlineData("127.0.0.1", "65536", "30", "Enter a Linkly port from 1 to 65535.")]
    [InlineData("127.0.0.1", "2011", "0", "Enter a Linkly timeout greater than zero seconds.")]
    public async Task TestLinklyCommand_rejects_invalid_local_endpoint_without_calling_terminal(
        string host,
        string port,
        string timeout,
        string expectedMessage)
    {
        var service = new FakeCardTerminalSetupService();
        var viewModel = new SettingsViewModel(service)
        {
            LinklyHostText = host,
            LinklyPortText = port,
            TimeoutSecondsText = timeout
        };

        await viewModel.TestLinklyCommand.ExecuteAsync(null);

        Assert.Equal(0, service.LinklyTestCallCount);
        Assert.Equal(expectedMessage, viewModel.LinklyTestStatusMessage);
        Assert.Equal(expectedMessage, viewModel.StatusMessage);
    }

    [Fact]
    public async Task TestLinklyCommand_passes_valid_local_endpoint_without_silent_defaults()
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyTestResult = new LinklyConnectionTestResult(true, "connected")
        };
        var viewModel = new SettingsViewModel(service)
        {
            LinklyHostText = " 127.0.0.2 ",
            LinklyPortText = "3211",
            TimeoutSecondsText = "17"
        };

        await viewModel.TestLinklyCommand.ExecuteAsync(null);

        Assert.Equal(1, service.LinklyTestCallCount);
        Assert.Equal("127.0.0.2", service.LastLinklyTestHost);
        Assert.Equal(3211, service.LastLinklyTestPort);
        Assert.Equal(TimeSpan.FromSeconds(17), service.LastLinklyTestTimeout);
    }

    [Fact]
    public async Task TestLinklyCommand_shows_running_endpoint_and_disables_duplicate_click()
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyTestResult = new LinklyConnectionTestResult(true, "connected")
        };
        service.BlockNextLinklyTest();
        var viewModel = new SettingsViewModel(service)
        {
            LinklyHostText = "127.0.0.1",
            LinklyPortText = "2011",
            TimeoutSecondsText = "30"
        };

        var testTask = viewModel.TestLinklyCommand.ExecuteAsync(null);
        await service.WaitForLinklyTestStartAsync();

        Assert.Equal("Checking Linkly LocalIp 127.0.0.1:2011...", viewModel.LinklyTestStatusMessage);
        Assert.False(viewModel.TestLinklyCommand.CanExecute(null));
        Assert.Equal(1, service.LinklyTestCallCount);

        service.ReleaseLinklyTest();
        await testTask;

        Assert.Equal("connected", viewModel.LinklyTestStatusMessage);
        Assert.True(viewModel.TestLinklyCommand.CanExecute(null));
    }

    [Fact]
    public async Task TestLinklyCommand_discards_success_when_local_endpoint_changes_during_test()
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyTestResult = new LinklyConnectionTestResult(true, "connected")
        };
        service.BlockNextLinklyTest();
        var viewModel = new SettingsViewModel(service)
        {
            LinklyHostText = "127.0.0.1",
            LinklyPortText = "2011",
            TimeoutSecondsText = "30"
        };

        var testTask = viewModel.TestLinklyCommand.ExecuteAsync(null);
        await service.WaitForLinklyTestStartAsync();
        viewModel.LinklyHostText = "127.0.0.2";

        service.ReleaseLinklyTest();
        await testTask;

        Assert.Equal("127.0.0.1", service.LastLinklyTestHost);
        Assert.Equal("127.0.0.2", viewModel.LinklyHostText);
        Assert.False(viewModel.LinklyConnectionSucceeded);
        Assert.False(viewModel.SaveLinklyCommand.CanExecute(null));
        Assert.Equal(
            "Linkly settings changed during the test. Test the current settings again.",
            viewModel.LinklyTestStatusMessage);
    }

    [Fact]
    public async Task TestLinklyCommand_discards_success_when_mode_changes_during_test()
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyTestResult = new LinklyConnectionTestResult(true, "connected")
        };
        service.BlockNextLinklyTest();
        var viewModel = new SettingsViewModel(service);

        var testTask = viewModel.TestLinklyCommand.ExecuteAsync(null);
        await service.WaitForLinklyTestStartAsync();
        viewModel.SelectedLinklyMode = LinklySettingsMode.CloudBackendAsync;

        service.ReleaseLinklyTest();
        await testTask;

        Assert.Equal(LinklySettingsMode.CloudBackendAsync, viewModel.SelectedLinklyMode);
        Assert.False(viewModel.LinklyConnectionSucceeded);
        Assert.False(viewModel.SaveLinklyCommand.CanExecute(null));
        Assert.Equal(
            "Linkly settings changed during the test. Test the current settings again.",
            viewModel.LinklyTestStatusMessage);
    }

    [Fact]
    public async Task TestLinklyCommand_permission_denied_does_not_call_terminal()
    {
        var service = new FakeCardTerminalSetupService();
        var viewModel = new SettingsViewModel(
            service,
            enforcePermissionsWhenNoCashier: true);

        await viewModel.TestLinklyCommand.ExecuteAsync(null);

        Assert.Equal(0, service.LinklyTestCallCount);
    }

    [Fact]
    public async Task LoadAsync_maps_linkly_configuration_to_three_mode_selection()
    {
        var localViewModel = new SettingsViewModel(new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with { LinklyConnectionMode = LinklyConnectionMode.Local }));
        var cloudViewModel = new SettingsViewModel(new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with { LinklyConnectionMode = LinklyConnectionMode.Cloud }));
        var backendViewModel = new SettingsViewModel(new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync }));

        await localViewModel.LoadAsync();
        await cloudViewModel.LoadAsync();
        await backendViewModel.LoadAsync();

        Assert.Equal(LinklySettingsMode.LocalIp, localViewModel.SelectedLinklyMode);
        Assert.True(localViewModel.IsLinklyLocalIpMode);
        Assert.False(localViewModel.IsLinklyCloudDirectSyncMode);
        Assert.False(localViewModel.IsLinklyCloudBackendAsyncMode);

        Assert.Equal(LinklySettingsMode.CloudDirectSync, cloudViewModel.SelectedLinklyMode);
        Assert.False(cloudViewModel.IsLinklyLocalIpMode);
        Assert.True(cloudViewModel.IsLinklyCloudDirectSyncMode);
        Assert.False(cloudViewModel.IsLinklyCloudBackendAsyncMode);

        Assert.Equal(LinklySettingsMode.CloudBackendAsync, backendViewModel.SelectedLinklyMode);
        Assert.False(backendViewModel.IsLinklyLocalIpMode);
        Assert.False(backendViewModel.IsLinklyCloudDirectSyncMode);
        Assert.True(backendViewModel.IsLinklyCloudBackendAsyncMode);
    }

    [Fact]
    public async Task LoadAsync_uses_saved_linkly_mode_as_first_priority()
    {
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with
            {
                LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync,
                LinklyConnectionModePriority =
                [
                    LinklyConnectionMode.CloudBackendAsync,
                    LinklyConnectionMode.CloudDirectSync,
                    LinklyConnectionMode.LocalIp
                ]
            }));

        await viewModel.LoadAsync();

        Assert.Equal(LinklySettingsMode.CloudBackendAsync, viewModel.PrimaryLinklyMode);
        Assert.Equal(
            [
                LinklySettingsMode.CloudBackendAsync,
                LinklySettingsMode.CloudDirectSync,
                LinklySettingsMode.LocalIp
            ],
            viewModel.LinklyModePriorityItems.Select(item => item.Mode));
    }

    [Fact]
    public async Task MoveLinklyPriorityUpCommand_promotes_fallback_and_save_persists_priority()
    {
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with
            {
                LinklyConnectionMode = LinklyConnectionMode.LocalIp,
                LinklyConnectionModePriority =
                [
                    LinklyConnectionMode.LocalIp,
                    LinklyConnectionMode.CloudDirectSync,
                    LinklyConnectionMode.CloudBackendAsync
                ]
            })
        {
            LinklyCloudTestResult = new LinklyConnectionTestResult(true, "cloud ready")
        };
        service.LinklyCloudSecretStatuses[CardTerminalEnvironment.Production] = true;
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();
        var cloudDirect = viewModel.LinklyModePriorityItems.Single(item => item.Mode == LinklySettingsMode.CloudDirectSync);
        viewModel.MoveLinklyPriorityUpCommand.Execute(cloudDirect);
        await viewModel.TestLinklyCommand.ExecuteAsync(null);
        await viewModel.SaveLinklyCommand.ExecuteAsync(null);

        Assert.Equal(LinklySettingsMode.CloudDirectSync, viewModel.PrimaryLinklyMode);
        Assert.NotNull(service.SavedConfiguration);
        Assert.Equal(LinklyConnectionMode.CloudDirectSync, service.SavedConfiguration!.LinklyConnectionMode);
        Assert.Equal(
            [
                LinklyConnectionMode.CloudDirectSync,
                LinklyConnectionMode.LocalIp,
                LinklyConnectionMode.CloudBackendAsync
            ],
            service.SavedConfiguration.LinklyConnectionModePriority);
    }

    [Fact]
    public async Task SelectLinklyPriorityModeCommand_promotes_mode_so_test_and_save_target_match()
    {
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with
            {
                LinklyConnectionMode = LinklyConnectionMode.LocalIp,
                LinklyConnectionModePriority =
                [
                    LinklyConnectionMode.LocalIp,
                    LinklyConnectionMode.CloudDirectSync,
                    LinklyConnectionMode.CloudBackendAsync
                ]
            })
        {
            LinklyCloudBackendTestResult = new LinklyConnectionTestResult(true, "backend ready")
        };
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();
        var backend = viewModel.LinklyModePriorityItems.Single(item => item.Mode == LinklySettingsMode.CloudBackendAsync);
        viewModel.SelectLinklyPriorityModeCommand.Execute(backend);
        await viewModel.TestLinklyCommand.ExecuteAsync(null);
        await viewModel.SaveLinklyCommand.ExecuteAsync(null);

        Assert.Equal(LinklySettingsMode.CloudBackendAsync, viewModel.PrimaryLinklyMode);
        Assert.Equal(1, service.LinklyCloudBackendTestCallCount);
        Assert.Equal(1, service.SaveLinklyCloudCallCount);
        Assert.NotNull(service.SavedConfiguration);
        Assert.Equal(LinklyConnectionMode.CloudBackendAsync, service.SavedConfiguration!.LinklyConnectionMode);
        Assert.Equal(
            [
                LinklyConnectionMode.CloudBackendAsync,
                LinklyConnectionMode.LocalIp,
                LinklyConnectionMode.CloudDirectSync
            ],
            service.SavedConfiguration.LinklyConnectionModePriority);
    }

    [Theory]
    [InlineData("LocalIp", "LocalIp", 1, 0, false)]
    [InlineData("CloudDirectSync", "CloudDirectSync", 0, 1, true)]
    [InlineData("CloudBackendAsync", "CloudBackendAsync", 0, 1, false)]
    public async Task SaveLinklyCommand_persists_and_restores_selected_three_mode(
        string selectedMode,
        string expectedStoredMode,
        int expectedLocalSaveCount,
        int expectedCloudSaveCount,
        bool hasSavedCloudSecret)
    {
        var service = new FakeCardTerminalSetupService();
        var viewModel = new SettingsViewModel(service)
        {
            SelectedLinklyMode = Enum.Parse<LinklySettingsMode>(selectedMode, ignoreCase: true),
            LinklyConnectionSucceeded = true,
            HasSavedLinklyCloudSecret = hasSavedCloudSecret
        };

        await viewModel.SaveLinklyCommand.ExecuteAsync(null);

        Assert.NotNull(service.SavedConfiguration);
        Assert.Equal(
            Enum.Parse<LinklyConnectionMode>(expectedStoredMode, ignoreCase: true),
            service.SavedConfiguration!.LinklyConnectionMode);
        Assert.Equal(expectedLocalSaveCount, service.SaveLinklyCallCount);
        Assert.Equal(expectedCloudSaveCount, service.SaveLinklyCloudCallCount);

        var restoredViewModel = new SettingsViewModel(service);
        await restoredViewModel.LoadAsync();

        Assert.Equal(
            Enum.Parse<LinklySettingsMode>(selectedMode, ignoreCase: true),
            restoredViewModel.SelectedLinklyMode);
    }

    [Fact]
    public async Task Changing_linkly_mode_clears_test_status_without_clearing_configuration_fields()
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyTestResult = new LinklyConnectionTestResult(true, "local connected")
        };
        var viewModel = new SettingsViewModel(service)
        {
            LinklyHostText = "192.168.1.10",
            LinklyPortText = "2011",
            LinklyCloudUsernameText = "cloud-user",
            LinklyCloudPasswordText = "cloud-password",
            LinklyPairCodeText = "123456",
            HasSavedLinklyCloudPassword = true,
            HasSavedLinklyCloudSecret = true
        };

        await viewModel.TestLinklyCommand.ExecuteAsync(null);
        Assert.True(viewModel.LinklyConnectionSucceeded);
        Assert.Equal("local connected", viewModel.LinklyTestStatusMessage);

        viewModel.SelectedLinklyMode = LinklySettingsMode.CloudBackendAsync;

        Assert.False(viewModel.LinklyConnectionSucceeded);
        Assert.Equal(string.Empty, viewModel.LinklyTestStatusMessage);
        Assert.Equal("192.168.1.10", viewModel.LinklyHostText);
        Assert.Equal("2011", viewModel.LinklyPortText);
        Assert.Equal("cloud-user", viewModel.LinklyCloudUsernameText);
        Assert.Equal("cloud-password", viewModel.LinklyCloudPasswordText);
        Assert.Equal("123456", viewModel.LinklyPairCodeText);
        Assert.True(viewModel.HasSavedLinklyCloudPassword);
        Assert.True(viewModel.HasSavedLinklyCloudSecret);
    }

    [Fact]
    public async Task CloudBackendAsync_mode_allows_backend_test_entry_without_local_secret()
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyCloudBackendTestResult = new LinklyConnectionTestResult(true, "backend accepted")
        };
        var viewModel = new SettingsViewModel(service)
        {
            SelectedLinklyMode = LinklySettingsMode.CloudBackendAsync,
            IsLinklySandbox = true
        };

        Assert.True(viewModel.IsLinklyCloudMode);
        Assert.True(viewModel.IsLinklyCloudBackendAsyncMode);
        Assert.False(viewModel.HasSavedLinklyCloudSecret);
        Assert.True(viewModel.TestLinklyCommand.CanExecute(null));

        await viewModel.TestLinklyCommand.ExecuteAsync(null);
        await viewModel.SaveLinklyCommand.ExecuteAsync(null);

        Assert.Equal("backend accepted", viewModel.LinklyTestStatusMessage);
        Assert.Equal(1, service.LinklyCloudBackendTestCallCount);
        Assert.Equal(0, service.LinklyCloudTestCallCount);
        Assert.NotNull(service.SavedConfiguration);
        Assert.Equal(CardProcessorKind.Linkly, service.SavedConfiguration!.Processor);
        Assert.Equal(CardTerminalEnvironment.Sandbox, service.SavedConfiguration!.Environment);
        Assert.Equal(LinklyConnectionMode.CloudBackendAsync, service.SavedConfiguration.LinklyConnectionMode);
        Assert.Equal("ANZ Linkly is active for the next card payment.", viewModel.ActivePaymentProviderText);
    }

    [Fact]
    public async Task CloudBackendAsync_status_test_command_runs_backend_status_and_updates_status_bar()
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyCloudBackendStatusTestResult = new LinklyConnectionTestResult(false, "DECLINED")
        };
        var viewModel = new SettingsViewModel(service)
        {
            SelectedLinklyMode = LinklySettingsMode.CloudBackendAsync,
            IsLinklySandbox = true
        };
        service.BlockNextLinklyCloudBackendStatusTest();

        Assert.True(viewModel.TestLinklyTransactionStatusCommand.CanExecute(null));

        var execution = viewModel.TestLinklyTransactionStatusCommand.ExecuteAsync(null);

        Assert.False(viewModel.TestLinklyTransactionStatusCommand.CanExecute(null));
        Assert.False(viewModel.TestLinklyCommand.CanExecute(null));

        service.ReleaseLinklyCloudBackendStatusTest();
        await execution;

        Assert.Equal(1, service.LinklyCloudBackendStatusTestCallCount);
        Assert.Equal("DECLINED", viewModel.LinklyTestStatusMessage);
        Assert.Equal("DECLINED", viewModel.StatusMessage);
        Assert.False(viewModel.LinklyConnectionSucceeded);
    }

    [Fact]
    public async Task CloudBackendAsync_status_test_failed_last_transaction_requests_friendly_dialog()
    {
        var localization = new LocalizationService();
        localization.SetCulture("zh-CN");
        var dialogService = new RecordingCardRecoveryResultDialogService();
        var service = new FakeCardTerminalSetupService
        {
            LinklyCloudBackendStatusTestResult = new LinklyConnectionTestResult(
                false,
                "OPERATOR TIMEOUT",
                new LinklyStatusTestDetails(
                    "session-last",
                    new DateTimeOffset(2026, 6, 10, 9, 30, 0, TimeSpan.Zero),
                    "TM",
                    "OPERATOR TIMEOUT",
                    "txn-last"))
        };
        var viewModel = new SettingsViewModel(
            service,
            localization,
            cardRecoveryResultDialogService: dialogService)
        {
            SelectedLinklyMode = LinklySettingsMode.CloudBackendAsync,
            IsLinklySandbox = true
        };

        await viewModel.TestLinklyTransactionStatusCommand.ExecuteAsync(null);

        var dialog = Assert.Single(dialogService.RequestedDialogs);
        Assert.Equal("上一笔刷卡交易未成功", dialog.Title);
        Assert.Equal("session-last", dialog.SessionId);
        Assert.Equal("txn-last", dialog.TxnRef);
        Assert.Equal("TM", dialog.ResponseCode);
        Assert.Equal("OPERATOR TIMEOUT", dialog.ResponseText);
        Assert.False(dialog.CanPrintReceipt);
    }

    [Fact]
    public async Task CloudBackendAsync_status_test_failed_last_transaction_dialog_uses_english_culture()
    {
        var localization = new LocalizationService();
        localization.SetCulture("en-US");
        var dialogService = new RecordingCardRecoveryResultDialogService();
        var service = new FakeCardTerminalSetupService
        {
            LinklyCloudBackendStatusTestResult = new LinklyConnectionTestResult(
                false,
                "OPERATOR TIMEOUT",
                new LinklyStatusTestDetails(
                    "session-last",
                    new DateTimeOffset(2026, 6, 10, 9, 30, 0, TimeSpan.Zero),
                    "TM",
                    "OPERATOR TIMEOUT",
                    "txn-last"))
        };
        var viewModel = new SettingsViewModel(
            service,
            localization,
            cardRecoveryResultDialogService: dialogService)
        {
            SelectedLinklyMode = LinklySettingsMode.CloudBackendAsync,
            IsLinklySandbox = true
        };

        await viewModel.TestLinklyTransactionStatusCommand.ExecuteAsync(null);

        var dialog = Assert.Single(dialogService.RequestedDialogs);
        Assert.Equal("Previous card transaction was not successful", dialog.Title);
        Assert.Contains("Transaction Status", dialog.Message);
    }

    [Fact]
    public async Task CloudBackendAsync_status_test_non_transaction_failure_does_not_request_failed_last_transaction_dialog()
    {
        var dialogService = new RecordingCardRecoveryResultDialogService();
        var service = new FakeCardTerminalSetupService
        {
            LinklyCloudBackendStatusTestResult = new LinklyConnectionTestResult(
                false,
                "LOGON REQUIRED",
                new LinklyStatusTestDetails(
                    "status-session",
                    new DateTimeOffset(2026, 6, 10, 9, 35, 0, TimeSpan.Zero),
                    "91",
                    "LOGON REQUIRED",
                    null))
        };
        var viewModel = new SettingsViewModel(
            service,
            cardRecoveryResultDialogService: dialogService)
        {
            SelectedLinklyMode = LinklySettingsMode.CloudBackendAsync,
            IsLinklySandbox = true
        };

        await viewModel.TestLinklyTransactionStatusCommand.ExecuteAsync(null);

        Assert.Empty(dialogService.RequestedDialogs);
    }

    [Fact]
    public async Task CloudBackendAsync_mode_lists_terminals_and_pairs_selected_terminal_without_credentials()
    {
        var selectedTerminalId = Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with
            {
                LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync,
                Environment = CardTerminalEnvironment.Sandbox
            })
        {
            LinklyCloudTerminalDirectory = new LinklyCloudTerminalListResponse(
                "Sandbox",
                selectedTerminalId,
                5,
                [
                    new LinklyCloudTerminalSummary(
                        selectedTerminalId,
                        1,
                        "Front Counter",
                        "Unpaired",
                        false,
                        false,
                        null,
                        null,
                        "POS-1",
                        5)
                ],
                "Draft",
                [new LinklyCloudAssignableDevice("POS-1", "WPF", true, selectedTerminalId, 5)]),
            LinklyCloudTerminalPairResult = new LinklyCloudTerminalPairResponse(
                selectedTerminalId,
                "Sandbox",
                "Front Counter",
                "Ready",
                true,
                "Terminal paired."),
            LinklyCloudTerminalSelectionResult = new LinklyCloudTerminalSelectionResponse(
                "Sandbox",
                selectedTerminalId,
                6)
        };
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();

        var pairedDirectory = new LinklyCloudTerminalListResponse(
            "Sandbox",
            selectedTerminalId,
            5,
            [new LinklyCloudTerminalSummary(
                selectedTerminalId, 1, "Front Counter", "Ready", false, true,
                null, null, "POS-1", 5, "v-2")],
            "Draft",
            [new LinklyCloudAssignableDevice("POS-1", "WPF", true, selectedTerminalId, 5)]);
        var selectedDirectory = pairedDirectory with
        {
            SelectionRevision = 6,
            Terminals = [new LinklyCloudTerminalSummary(
                selectedTerminalId, 1, "Front Counter", "Ready", false, true,
                null, null, "POS-1", 6, "v-2")],
            Devices = [new LinklyCloudAssignableDevice("POS-1", "WPF", true, selectedTerminalId, 6)]
        };
        service.LinklyCloudTerminalDirectories.Enqueue(pairedDirectory);
        service.LinklyCloudTerminalDirectories.Enqueue(selectedDirectory);

        Assert.False(viewModel.SaveLinklyCloudCredentialCommand.CanExecute(null));
        Assert.True(viewModel.PairLinklyCloudCommand.CanExecute(null));
        Assert.Equal(selectedTerminalId, Assert.Single(viewModel.LinklyCloudTerminals).TerminalId);
        Assert.Equal(selectedTerminalId, viewModel.SelectedLinklyCloudTerminal?.TerminalId);

        viewModel.LinklyPairCodeText = "123456";
        await viewModel.PairLinklyCloudCommand.ExecuteAsync(null);

        Assert.Equal(0, service.PairLinklyCloudCallCount);
        Assert.Equal(selectedTerminalId, service.LastBackendPairTerminalId);
        Assert.Equal("123456", service.LastBackendPairCode);
        Assert.Equal(5, service.LastBackendSelectionExpectedRevision);
        Assert.Equal(6, viewModel.LinklyCloudSelectionRevision);
        Assert.Equal(3, service.BackendDirectoryCallCount);
        Assert.Equal("v-2", Assert.Single(viewModel.LinklyCloudTerminals).TerminalVersion);
        Assert.Contains("Front Counter is paired and selected", viewModel.LinklyTestStatusMessage, StringComparison.Ordinal);
        Assert.Contains("Test Logon", viewModel.LinklyTestStatusMessage, StringComparison.Ordinal);
        Assert.Contains("Enable", viewModel.LinklyTestStatusMessage, StringComparison.Ordinal);
        Assert.False(viewModel.HasSavedLinklyCloudSecret);
    }

    [Fact]
    public async Task CloudBackendAsync_unselected_multi_terminal_refresh_keeps_selection_null()
    {
        var firstTerminalId = Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
        var secondTerminalId = Guid.Parse("cccccccc-1111-2222-3333-bbbbbbbbbbbb");
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with
            {
                LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync,
                Environment = CardTerminalEnvironment.Production
            })
        {
            LinklyCloudTerminalDirectory = new LinklyCloudTerminalListResponse(
                "Production",
                null,
                null,
                [
                    new LinklyCloudTerminalSummary(firstTerminalId, 1, "Front", "Ready", false, true, null, null),
                    new LinklyCloudTerminalSummary(secondTerminalId, 2, "Side", "Ready", false, true, null, null)
                ],
                "Active")
        };
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();

        Assert.Equal(2, viewModel.LinklyCloudTerminals.Count);
        Assert.Null(viewModel.SelectedLinklyCloudTerminal);
        Assert.Null(service.LastBackendSelectionTerminalId);
    }

    [Fact]
    public async Task CloudBackendAsync_unpaired_target_pairs_before_changing_payment_selection()
    {
        var readyTerminalId = Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
        var unpairedTerminalId = Guid.Parse("cccccccc-1111-2222-3333-bbbbbbbbbbbb");
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = new LinklyCloudTerminalListResponse(
                "Production", readyTerminalId, 5,
                [
                    new LinklyCloudTerminalSummary(readyTerminalId, 1, "Front", "Ready", false, true, null, null, "POS-1", 5),
                    new LinklyCloudTerminalSummary(unpairedTerminalId, 2, "Returns", "Unpaired", false, false, null, null)
                ],
                "Active",
                [new LinklyCloudAssignableDevice("POS-1", "WPF", true, readyTerminalId, 5)]),
            LinklyCloudTerminalPairResult = new LinklyCloudTerminalPairResponse(
                unpairedTerminalId, "Production", "Returns", "Ready", true, "paired"),
            LinklyCloudTerminalSelectionResult = new LinklyCloudTerminalSelectionResponse(
                "Production", unpairedTerminalId, 6)
        };
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();
        var pairedDirectory = new LinklyCloudTerminalListResponse(
            "Production", readyTerminalId, 5,
            [
                new LinklyCloudTerminalSummary(readyTerminalId, 1, "Front", "Ready", false, true, null, null, "POS-1", 5, "v-front-1"),
                new LinklyCloudTerminalSummary(unpairedTerminalId, 2, "Returns", "Ready", false, true, null, null, null, 0, "v-returns-2")
            ],
            "Active",
            [new LinklyCloudAssignableDevice("POS-1", "WPF", true, readyTerminalId, 5)]);
        var selectedDirectory = pairedDirectory with
        {
            SelectedTerminalId = unpairedTerminalId,
            SelectionRevision = 6,
            Terminals =
            [
                new LinklyCloudTerminalSummary(readyTerminalId, 1, "Front", "Ready", false, true, null, null, null, 0, "v-front-1"),
                new LinklyCloudTerminalSummary(unpairedTerminalId, 2, "Returns", "Ready", false, true, null, null, "POS-1", 6, "v-returns-2")
            ],
            Devices = [new LinklyCloudAssignableDevice("POS-1", "WPF", true, unpairedTerminalId, 6)]
        };
        service.LinklyCloudTerminalDirectories.Enqueue(pairedDirectory);
        service.LinklyCloudTerminalDirectories.Enqueue(selectedDirectory);
        await viewModel.SelectLinklyCloudBackendTerminalAsync(
            viewModel.LinklyCloudTerminals.Single(item => item.TerminalId == unpairedTerminalId));

        Assert.Equal(unpairedTerminalId, viewModel.SelectedLinklyCloudTerminal?.TerminalId);
        Assert.Null(service.LastBackendSelectionTerminalId);

        viewModel.LinklyPairCodeText = "123456";
        await viewModel.PairLinklyCloudCommand.ExecuteAsync(null);

        Assert.Equal(1, service.BackendPairCallCount);
        Assert.Equal(unpairedTerminalId, service.LastBackendPairTerminalId);
        Assert.Equal(unpairedTerminalId, service.LastBackendSelectionTerminalId);
        Assert.Equal(5, service.LastBackendSelectionExpectedRevision);
        Assert.Equal(unpairedTerminalId, viewModel.SelectedLinklyCloudTerminal?.TerminalId);
        Assert.Equal(6, viewModel.LinklyCloudSelectionRevision);
        Assert.Equal(3, service.BackendDirectoryCallCount);
        Assert.Equal(
            "v-returns-2",
            viewModel.LinklyCloudTerminals.Single(item => item.TerminalId == unpairedTerminalId).TerminalVersion);
    }

    [Fact]
    public async Task CloudBackendAsync_terminal_selection_failure_restores_persisted_terminal()
    {
        var persistedTerminalId = Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
        var otherTerminalId = Guid.Parse("cccccccc-1111-2222-3333-bbbbbbbbbbbb");
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = new LinklyCloudTerminalListResponse(
                "Production", persistedTerminalId, 8,
                [
                    new LinklyCloudTerminalSummary(persistedTerminalId, 1, "Front", "Ready", false, true, null, null),
                    new LinklyCloudTerminalSummary(otherTerminalId, 2, "Side", "Ready", false, true, null, null)
                ],
                "Active"),
            LinklyCloudTerminalSelectionException = new HttpRequestException("selection rejected")
        };
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();
        await viewModel.SelectLinklyCloudBackendTerminalAsync(
            viewModel.LinklyCloudTerminals.Single(item => item.TerminalId == otherTerminalId));

        Assert.Equal(persistedTerminalId, viewModel.SelectedLinklyCloudTerminal?.TerminalId);
        Assert.Equal(8, viewModel.LinklyCloudSelectionRevision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloudBackendAsync_pair_failure_clears_pair_code_and_keeps_unknown_when_refresh_fails(bool serverRejected)
    {
        var terminalId = Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = new LinklyCloudTerminalListResponse(
                "Production", terminalId, 3,
                [new LinklyCloudTerminalSummary(terminalId, 1, "Front", "Unpaired", false, false, null, null)],
                "Draft"),
            LinklyCloudTerminalPairException = serverRejected
                ? new HttpRequestException("Complete the previous transaction first.", null, System.Net.HttpStatusCode.Conflict)
                : new HttpRequestException("pair timeout")
        };
        var viewModel = new SettingsViewModel(service);
        await viewModel.LoadAsync();
        service.LinklyCloudTerminalListExceptions.Enqueue(new HttpRequestException("directory unavailable"));
        viewModel.LinklyPairCodeText = "123456";

        await viewModel.PairLinklyCloudCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.LinklyPairCodeText);
        Assert.Equal(1, service.BackendPairCallCount);
        Assert.Equal("Unknown", viewModel.SelectedLinklyCloudTerminal?.PairingState);
        Assert.False(viewModel.SelectedLinklyCloudTerminal!.IsReady);
        Assert.Contains("Front", viewModel.LinklyTestStatusMessage, StringComparison.Ordinal);
        Assert.Contains(serverRejected ? "Complete the previous transaction first." : "could not be confirmed",
            viewModel.LinklyTestStatusMessage, StringComparison.Ordinal);
        Assert.Equal(viewModel.StatusMessage, viewModel.LinklyTestStatusMessage);
        Assert.DoesNotContain("123456", viewModel.LinklyTestStatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CloudBackendAsync_pair_success_then_selection_failure_does_not_pair_again()
    {
        var terminalId = Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = new LinklyCloudTerminalListResponse(
                "Production", null, 3,
                [new LinklyCloudTerminalSummary(terminalId, 1, "Front", "Unpaired", false, false, null, null)],
                "Draft"),
            LinklyCloudTerminalPairResult = new LinklyCloudTerminalPairResponse(
                terminalId, "Production", "Front", "Ready", true, "paired"),
            LinklyCloudTerminalSelectionException = new HttpRequestException("selection rejected")
        };
        var viewModel = new SettingsViewModel(service);
        await viewModel.LoadAsync();
        await viewModel.SelectLinklyCloudBackendTerminalAsync(
            viewModel.LinklyCloudTerminals.Single(item => item.TerminalId == terminalId));
        viewModel.LinklyPairCodeText = "123456";

        await viewModel.PairLinklyCloudCommand.ExecuteAsync(null);

        Assert.Equal(1, service.BackendPairCallCount);
        Assert.Empty(viewModel.LinklyPairCodeText);
        Assert.Contains("paired but not selected", viewModel.LinklyTestStatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Linkly_line_selection_rejects_mismatched_response_identity(bool wrongEnvironment)
    {
        var selected = new LinklyCloudTerminalSummary(Guid.NewGuid(), 1, "Front", "Ready", false, true, null, null);
        var target = new LinklyCloudTerminalSummary(Guid.NewGuid(), 2, "Side", "Ready", false, true, null, null);
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default with
            { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = new("Production", selected.TerminalId, 4, [selected, target]),
            LinklyCloudTerminalSelectionResult = new(wrongEnvironment ? "Sandbox" : "Production",
                wrongEnvironment ? target.TerminalId : Guid.NewGuid(), 5)
        };
        using var model = new SettingsViewModel(service);
        await model.LoadAsync();
        service.LinklyCloudTerminalDirectories.Enqueue(new(
            "Production", target.TerminalId, 6, [selected, target]));

        await model.SelectLinklyCloudBackendTerminalAsync(target);

        Assert.Equal(target.TerminalId, model.SelectedLinklyCloudTerminal?.TerminalId);
        Assert.Equal(6, model.LinklyCloudSelectionRevision);
        Assert.Equal(1, service.BackendSelectionCallCount);
        Assert.Equal(2, service.BackendDirectoryCallCount);
        Assert.False(model.SaveLinklyCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Linkly_line_selection_transport_failure_reconciles_once_without_replaying_write(bool serverChanged)
    {
        var selected = new LinklyCloudTerminalSummary(Guid.NewGuid(), 1, "Front", "Ready", false, true, null, null);
        var target = new LinklyCloudTerminalSummary(Guid.NewGuid(), 2, "Side", "Ready", false, true, null, null);
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default with
            { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = new("Production", selected.TerminalId, 4, [selected, target]),
            LinklyCloudTerminalSelectionException = new HttpRequestException("response interrupted")
        };
        using var model = new SettingsViewModel(service);
        await model.LoadAsync();
        var authoritativeId = serverChanged ? target.TerminalId : selected.TerminalId;
        service.LinklyCloudTerminalDirectories.Enqueue(new(
            "Production", authoritativeId, 5, [selected, target]));

        await model.SelectLinklyCloudBackendTerminalAsync(target);

        Assert.Equal(authoritativeId, model.SelectedLinklyCloudTerminal?.TerminalId);
        Assert.Equal(5, model.LinklyCloudSelectionRevision);
        Assert.Equal(1, service.BackendSelectionCallCount);
        Assert.Equal(2, service.BackendDirectoryCallCount);
        Assert.Contains("refreshed", model.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Linkly_line_selection_and_reconciliation_failure_fail_closed()
    {
        var selected = new LinklyCloudTerminalSummary(
            Guid.NewGuid(), 1, "Front", "Ready", false, true, null, null, "POS-1", 4, "v-4");
        var target = new LinklyCloudTerminalSummary(
            Guid.NewGuid(), 2, "Side", "Ready", false, true, null, null, "POS-2", 4, "v-4");
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default with
            { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = new("Production", selected.TerminalId, 4, [selected, target]),
            LinklyCloudTerminalSelectionException = new HttpRequestException("response interrupted"),
            LinklyCloudTerminalConnectionTestResult = new(
                selected.TerminalId, "Production", "v-4", "POS-1", 4, true, "connected",
                DateTimeOffset.UtcNow, "connected")
        };
        using var model = new SettingsViewModel(service);
        await model.LoadAsync();
        await model.LinklyCloudLines.Single(line => line.IsSelected).TestConnectionCommand.ExecuteAsync(null);
        Assert.True(model.LinklyConnectionSucceeded);
        service.LinklyCloudTerminalListExceptions.Enqueue(new HttpRequestException("directory unavailable"));

        await model.SelectLinklyCloudBackendTerminalAsync(target);

        Assert.False(model.LinklyConnectionSucceeded);
        Assert.False(model.SaveLinklyCommand.CanExecute(null));
        Assert.Equal(selected.TerminalId, model.SelectedLinklyCloudTerminal?.TerminalId);
        Assert.Equal(1, service.BackendSelectionCallCount);
        Assert.Equal(2, service.BackendDirectoryCallCount);
        Assert.Contains("unknown", model.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Linkly_line_selection_blocks_refresh_until_its_response_is_applied()
    {
        var terminal = new LinklyCloudTerminalSummary(Guid.NewGuid(), 2, "Side", "Ready", false, true, null, null);
        var pending = new TaskCompletionSource<LinklyCloudTerminalSelectionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default with
            { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = new("Production", null, 4, [terminal]),
            PendingBackendSelection = pending
        };
        using var model = new SettingsViewModel(service);
        await model.LoadAsync();
        var line = Assert.Single(model.LinklyCloudLines);
        var selecting = model.SelectLinklyCloudBackendTerminalAsync(terminal);
        Assert.True(model.IsBusy);
        var listCalls = service.BackendDirectoryCallCount;

        await model.RefreshLinklyCloudBackendTerminalsAsync();

        Assert.Equal(listCalls, service.BackendDirectoryCallCount);
        Assert.Same(line, Assert.Single(model.LinklyCloudLines));
        pending.SetResult(new("Production", terminal.TerminalId, 5));
        await selecting;
        Assert.Equal(5, model.LinklyCloudSelectionRevision);
        Assert.Equal(terminal.TerminalId, model.SelectedLinklyCloudTerminal?.TerminalId);
    }

    [Fact]
    public async Task Linkly_line_pairing_does_not_double_submit_or_apply_to_a_new_environment()
    {
        var terminal = new LinklyCloudTerminalSummary(Guid.NewGuid(), 2, "Side", "Unpaired", false, false, null, null);
        var pending = new TaskCompletionSource<LinklyCloudTerminalPairResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default with
            { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = new("Production", null, null, [terminal]),
            PendingBackendPair = pending
        };
        using var model = new SettingsViewModel(service);
        await model.LoadAsync();
        var line = Assert.Single(model.LinklyCloudLines);
        line.TogglePairingCommand.Execute(null);
        line.PairCode = "123456";
        line.NextCommand.Execute(null);
        var pairing = line.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(1, service.BackendPairCallCount);
        Assert.Empty(line.PairCode);
        Assert.False(model.CanChangeEnvironment);
        await line.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(1, service.BackendPairCallCount);
        model.IsLinklySandbox = true;
        pending.SetResult(new(terminal.TerminalId, "Production", "Side", "Ready", true, "paired"));
        await pairing;
        Assert.False(model.LinklyConnectionSucceeded);
        Assert.DoesNotContain(model.LinklyCloudLines, item => item.Terminal.IsReady);
    }

    [Fact]
    public async Task Linkly_line_pairing_blocks_assignment_and_refresh_until_directory_lease_is_released()
    {
        var terminal = new LinklyCloudTerminalSummary(
            Guid.NewGuid(), 2, "Side", "Ready", false, true, null, null, "POS-1", 7, "v-7");
        var directory = new LinklyCloudTerminalListResponse(
            "Production", terminal.TerminalId, 4, [terminal], "Active",
            [new LinklyCloudAssignableDevice("POS-1", "WPF", true, terminal.TerminalId, 4)]);
        var pending = new TaskCompletionSource<LinklyCloudTerminalPairResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default with
            { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = directory,
            PendingBackendPair = pending
        };
        using var model = new SettingsViewModel(service);
        await model.LoadAsync();
        var line = Assert.Single(model.LinklyCloudLines);
        var management = Assert.Single(model.LinklyCloudTerminalItems);
        Assert.True(model.ChangeLinklyCloudTerminalAssignmentCommand.CanExecute(management));
        Assert.True(model.UnassignLinklyCloudTerminalCommand.CanExecute(management));
        Assert.True(model.CanRefreshLinklyCloudTerminals);

        line.TogglePairingCommand.Execute(null);
        line.PairCode = "123456";
        line.NextCommand.Execute(null);
        var pairing = line.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(1, service.BackendPairCallCount);
        Assert.False(model.ChangeLinklyCloudTerminalAssignmentCommand.CanExecute(management));
        Assert.False(model.UnassignLinklyCloudTerminalCommand.CanExecute(management));
        Assert.False(model.CanRefreshLinklyCloudTerminals);
        var listCalls = service.BackendDirectoryCallCount;
        await model.RefreshLinklyCloudBackendTerminalsAsync();
        Assert.Equal(listCalls, service.BackendDirectoryCallCount);

        pending.SetResult(new(
            terminal.TerminalId, "Production", terminal.DisplayName, "Ready", true, "paired"));
        await pairing;

        var refreshed = Assert.Single(model.LinklyCloudTerminalItems);
        Assert.True(model.ChangeLinklyCloudTerminalAssignmentCommand.CanExecute(refreshed));
        Assert.True(model.UnassignLinklyCloudTerminalCommand.CanExecute(refreshed));
        Assert.True(model.CanRefreshLinklyCloudTerminals);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("lane")]
    [InlineData("version")]
    [InlineData("assignment")]
    public async Task Linkly_line_confirmation_rechecks_server_identity_before_consuming_code(string changedField)
    {
        var terminal = new LinklyCloudTerminalSummary(Guid.NewGuid(), 2, "Returns", "Ready", false, true,
            null, null, "POS-02", 3, "v1");
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default with
            { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = new("Production", terminal.TerminalId, 3, [terminal])
        };
        using var model = new SettingsViewModel(service);
        await model.LoadAsync();
        var line = Assert.Single(model.LinklyCloudLines);
        line.TogglePairingCommand.Execute(null);
        line.PairCode = "123456";
        line.NextCommand.Execute(null);
        var changed = changedField switch
        {
            "name" => terminal with { DisplayName = "New checkout" },
            "lane" => terminal with { LaneNo = 8 },
            "version" => terminal with { TerminalVersion = "v2" },
            _ => terminal with { AssignedDeviceCode = "POS-03", AssignmentRevision = 4 }
        };
        service.LinklyCloudTerminalDirectory = service.LinklyCloudTerminalDirectory with { Terminals = [changed] };

        await line.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(0, service.BackendPairCallCount);
        Assert.False(line.IsExpanded);
        Assert.Empty(line.PairCode);
        Assert.Contains("changed", model.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Linkly_line_preflight_network_failure_does_not_submit_pair_code()
    {
        var terminal = new LinklyCloudTerminalSummary(Guid.NewGuid(), 2, "Returns", "Unpaired", false, false, null, null);
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default with
            { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = new("Production", null, null, [terminal])
        };
        using var model = new SettingsViewModel(service);
        await model.LoadAsync();
        var line = Assert.Single(model.LinklyCloudLines);
        line.TogglePairingCommand.Execute(null);
        line.PairCode = "123456";
        line.NextCommand.Execute(null);
        service.LinklyCloudTerminalListExceptions.Enqueue(new HttpRequestException("offline"));

        await line.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(0, service.BackendPairCallCount);
        Assert.Empty(line.PairCode);
        Assert.Contains("No pairing code was submitted", model.StatusMessage);
    }

    [Theory]
    [InlineData("LINKLY_CLOUD_BACKEND_PAIR_REJECTED", "settings.linkly.cloudBackend.pairRejected")]
    [InlineData("LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT", "settings.linkly.cloudBackend.selectionChanged")]
    public async Task Linkly_line_pair_errors_show_specific_localized_help(string errorCode, string resourceKey)
    {
        var localization = new LocalizationService();
        var terminal = new LinklyCloudTerminalSummary(Guid.NewGuid(), 1, "Front", "Ready", false, true, null, null);
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default with
            { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = new("Production", terminal.TerminalId, 1, [terminal]),
            LinklyCloudTerminalPairException = new LinklyBackendHttpException("Do not show raw server details", System.Net.HttpStatusCode.Conflict, errorCode)
        };
        using var model = new SettingsViewModel(service, localization);
        await model.LoadAsync();
        var line = Assert.Single(model.LinklyCloudLines);
        line.TogglePairingCommand.Execute(null);
        line.PairCode = "123456";
        line.NextCommand.Execute(null);
        await line.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(1, service.BackendPairCallCount);
        Assert.Equal(localization.T(resourceKey), line.ConnectionHelpText);
        Assert.False(model.LinklyConnectionSucceeded);
        localization.SetCulture("zh-CN");
        Assert.Equal(localization.T(resourceKey), line.ConnectionHelpText);
        Assert.Equal(localization.T(resourceKey), model.StatusMessage);
    }

    [Fact]
    public async Task Linkly_line_without_version_requires_refresh_before_connection_test()
    {
        var terminal = new LinklyCloudTerminalSummary(Guid.NewGuid(), 1, "Front", "Ready", false, true, "Healthy", null);
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default with
            { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = new("Production", terminal.TerminalId, 1, [terminal])
        };
        using var model = new SettingsViewModel(service);
        await model.LoadAsync();
        var line = Assert.Single(model.LinklyCloudLines);
        Assert.False(line.TestConnectionCommand.CanExecute(null));
        Assert.Contains("Refresh", line.ConnectionHelpText);
    }

    [Fact]
    public async Task Linkly_line_pairing_requires_confirmation_and_does_not_change_payment_line()
    {
        var selectedId = Guid.Parse("aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb");
        var targetId = Guid.Parse("cccccccc-1111-2222-3333-bbbbbbbbbbbb");
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = new LinklyCloudTerminalListResponse(
                "Production", selectedId, 9,
                [
                    new LinklyCloudTerminalSummary(selectedId, 1, "Front", "Ready", false, true, "connected", null),
                    new LinklyCloudTerminalSummary(targetId, 2, "Returns", "Unpaired", false, false, null, null)
                ], "Active"),
            LinklyCloudTerminalPairResult = new LinklyCloudTerminalPairResponse(
                targetId, "Production", "Returns", "Ready", true, "paired")
        };
        var viewModel = new SettingsViewModel(service);
        await viewModel.LoadAsync();
        var target = viewModel.LinklyCloudLines.Single(item => item.Terminal.TerminalId == targetId);

        target.TogglePairingCommand.Execute(null);
        target.PairCode = "123456";
        target.NextCommand.Execute(null);

        Assert.True(target.IsConfirming);
        Assert.Equal(0, service.BackendPairCallCount);

        await target.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(1, service.BackendPairCallCount);
        Assert.Equal(targetId, service.LastBackendPairTerminalId);
        Assert.Equal("123456", service.LastBackendPairCode);
        Assert.Null(service.LastBackendSelectionTerminalId);
        Assert.Equal(selectedId, viewModel.SelectedLinklyCloudTerminal?.TerminalId);
        Assert.True(viewModel.LinklyCloudLines.Single(item => item.Terminal.TerminalId == selectedId).IsSelected);
        Assert.False(target.IsExpanded);
        Assert.Empty(target.PairCode);
    }

    [Fact]
    public async Task Linkly_line_pairing_allows_only_one_open_line_and_rejects_non_ascii_code()
    {
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = new LinklyCloudTerminalListResponse(
                "Production", null, 1,
                [
                    new LinklyCloudTerminalSummary(Guid.NewGuid(), 1, "Front", "Unpaired", false, false, null, null),
                    new LinklyCloudTerminalSummary(Guid.NewGuid(), 2, "Returns", "Unpaired", false, false, null, null)
                ], "Draft")
        };
        var viewModel = new SettingsViewModel(service);
        await viewModel.LoadAsync();
        var first = viewModel.LinklyCloudLines[0];
        var second = viewModel.LinklyCloudLines[1];

        first.TogglePairingCommand.Execute(null);
        first.PairCode = "12345A";
        Assert.False(first.NextCommand.CanExecute(null));

        second.TogglePairingCommand.Execute(null);

        Assert.False(first.IsExpanded);
        Assert.Empty(first.PairCode);
        Assert.True(second.IsEnteringCode);
        Assert.Equal(0, service.BackendPairCallCount);
    }

    [Fact]
    public async Task Linkly_line_connection_test_targets_requested_terminal_without_selecting_it()
    {
        var selectedId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var terminalVersion = "638931456789000000";
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = new LinklyCloudTerminalListResponse(
                "Production", selectedId, 4,
                [
                    new LinklyCloudTerminalSummary(selectedId, 1, "Front", "Ready", false, true, null, null),
                    new LinklyCloudTerminalSummary(targetId, 2, "Returns", "Ready", false, true, null, null, "POS-02", 7, terminalVersion)
                ], "Active"),
            LinklyCloudTerminalConnectionTestResult = new LinklyCloudTerminalConnectionTestResponse(
                targetId, "Production", terminalVersion, "POS-02", 7, true, "connected",
                DateTimeOffset.UtcNow, "connected")
        };
        var viewModel = new SettingsViewModel(service);
        await viewModel.LoadAsync();
        var target = viewModel.LinklyCloudLines.Single(item => item.Terminal.TerminalId == targetId);

        await target.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal(targetId, service.LastBackendConnectionTestTerminalId);
        Assert.Equal(CardTerminalEnvironment.Production, service.LastBackendConnectionTestEnvironment);
        Assert.Null(service.LastBackendSelectionTerminalId);
        Assert.Contains("Connected", target.ConnectionStatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.LinklyConnectionSucceeded);
        Assert.False(viewModel.SaveLinklyCommand.CanExecute(null));
    }

    [Fact]
    public async Task Linkly_selected_line_connection_test_unlocks_save_only_after_verified_success()
    {
        var terminalId = Guid.NewGuid();
        const string terminalVersion = "638931456789000000";
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = new LinklyCloudTerminalListResponse(
                "Production", terminalId, 4,
                [new LinklyCloudTerminalSummary(
                    terminalId, 1, "Front", "Ready", false, true, null, null,
                    "POS-01", 5, terminalVersion)], "Active"),
            LinklyCloudTerminalConnectionTestResult = new LinklyCloudTerminalConnectionTestResponse(
                terminalId, "Production", terminalVersion, "POS-01", 5, true, "connected",
                DateTimeOffset.UtcNow, "connected")
        };
        var viewModel = new SettingsViewModel(service);
        await viewModel.LoadAsync();
        var selected = Assert.Single(viewModel.LinklyCloudLines);

        await selected.TestConnectionCommand.ExecuteAsync(null);

        Assert.True(viewModel.LinklyConnectionSucceeded);
        Assert.True(viewModel.SaveLinklyCommand.CanExecute(null));
    }

    [Fact]
    public async Task Linkly_connection_success_is_cleared_when_payment_line_changes()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        const string version = "638931456789000000";
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = new LinklyCloudTerminalListResponse(
                "Production", firstId, 4,
                [
                    new LinklyCloudTerminalSummary(firstId, 1, "Front", "Ready", false, true, null, null, "POS-01", 5, version),
                    new LinklyCloudTerminalSummary(secondId, 2, "Returns", "Ready", false, true, null, null, "POS-02", 6, version)
                ], "Active"),
            LinklyCloudTerminalConnectionTestResult = new LinklyCloudTerminalConnectionTestResponse(
                firstId, "Production", version, "POS-01", 5, true, "connected",
                DateTimeOffset.UtcNow, "connected"),
            LinklyCloudTerminalSelectionResult = new LinklyCloudTerminalSelectionResponse(
                "Production", secondId, 5)
        };
        var viewModel = new SettingsViewModel(service);
        await viewModel.LoadAsync();
        await viewModel.LinklyCloudLines.Single(item => item.IsSelected).TestConnectionCommand.ExecuteAsync(null);
        Assert.True(viewModel.LinklyConnectionSucceeded);

        await viewModel.LinklyCloudLines.Single(item => item.Terminal.TerminalId == secondId).SelectCommand.ExecuteAsync(null);

        Assert.False(viewModel.LinklyConnectionSucceeded);
        Assert.False(viewModel.SaveLinklyCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Linkly_selected_line_retest_clears_previous_success_on_unknown_or_exception(bool throws)
    {
        var terminalId = Guid.NewGuid();
        const string version = "638931456789000000";
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = new LinklyCloudTerminalListResponse(
                "Production", terminalId, 4,
                [new LinklyCloudTerminalSummary(terminalId, 1, "Front", "Ready", false, true, null, null, "POS-01", 5, version)], "Active"),
            LinklyCloudTerminalConnectionTestResult = new LinklyCloudTerminalConnectionTestResponse(
                terminalId, "Production", version, "POS-01", 5, true, "connected",
                DateTimeOffset.UtcNow, "connected")
        };
        var viewModel = new SettingsViewModel(service);
        await viewModel.LoadAsync();
        var selected = Assert.Single(viewModel.LinklyCloudLines);
        await selected.TestConnectionCommand.ExecuteAsync(null);
        Assert.True(viewModel.LinklyConnectionSucceeded);

        service.LinklyCloudTerminalConnectionTestResult = new LinklyCloudTerminalConnectionTestResponse(
            terminalId, "Production", version, "POS-01", 5, false, "unknown",
            DateTimeOffset.UtcNow, "unknown");
        service.LinklyCloudTerminalConnectionTestException = throws
            ? new HttpRequestException("network unavailable")
            : null;
        await selected.TestConnectionCommand.ExecuteAsync(null);

        Assert.False(viewModel.LinklyConnectionSucceeded);
        Assert.False(viewModel.SaveLinklyCommand.CanExecute(null));
        Assert.True(selected.HasConnectionFailure);
    }

    [Fact]
    public async Task CloudBackendAsync_pair_refreshes_row_with_new_version_before_immediate_management()
    {
        var terminalId = Guid.NewGuid();
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = new LinklyCloudTerminalListResponse(
                "Production", null, 0,
                [new LinklyCloudTerminalSummary(terminalId, 1, "Front", "Unpaired", false, false, null, null, null, 0, "v-1")],
                "Active",
                [new LinklyCloudAssignableDevice("POS-1", "WPF", true, null, 0)]),
            LinklyCloudTerminalPairResult = new LinklyCloudTerminalPairResponse(
                terminalId, "Production", "Front", "Ready", true, "paired"),
            LinklyCloudTerminalSelectionResult = new LinklyCloudTerminalSelectionResponse("Production", terminalId, 2)
        };
        var viewModel = new SettingsViewModel(service);
        await viewModel.LoadAsync();
        await viewModel.SelectLinklyCloudBackendTerminalAsync(Assert.Single(viewModel.LinklyCloudTerminals));
        var pairedDirectory = new LinklyCloudTerminalListResponse(
            "Production", null, 0,
            [new LinklyCloudTerminalSummary(terminalId, 1, "Front", "Ready", false, true, null, null, null, 0, "v-2")],
            "Active",
            [new LinklyCloudAssignableDevice("POS-1", "WPF", true, null, 0)]);
        var selectedDirectory = pairedDirectory with
        {
            SelectedTerminalId = terminalId,
            SelectionRevision = 2,
            Terminals = [new LinklyCloudTerminalSummary(
                terminalId, 1, "Front", "Ready", false, true, null, null, "POS-1", 2, "v-2")],
            Devices = [new LinklyCloudAssignableDevice("POS-1", "WPF", true, terminalId, 2)]
        };
        service.LinklyCloudTerminalDirectories.Enqueue(pairedDirectory);
        service.LinklyCloudTerminalDirectories.Enqueue(selectedDirectory);
        viewModel.LinklyPairCodeText = "123456";

        await viewModel.PairLinklyCloudCommand.ExecuteAsync(null);

        var item = Assert.Single(viewModel.LinklyCloudTerminalItems);
        Assert.True(item.IsReady);
        Assert.Equal(1, service.BackendPairCallCount);
        Assert.Equal(terminalId, service.LastBackendSelectionTerminalId);
        Assert.Equal(0, service.LastBackendSelectionExpectedRevision);
        Assert.Equal(3, service.BackendDirectoryCallCount);
        Assert.Equal(terminalId, viewModel.SelectedLinklyCloudTerminal?.TerminalId);
        Assert.Equal(2, viewModel.LinklyCloudSelectionRevision);
        Assert.Equal("POS-1", item.AssignedDeviceCode);
        Assert.True(viewModel.IsLinklyCloudLineManagementAvailable);
        Assert.Equal("v-2", item.Terminal.TerminalVersion);
        Assert.True(viewModel.TestLinklyCloudTerminalCommand.CanExecute(item));
        Assert.True(viewModel.ChangeLinklyCloudTerminalAssignmentCommand.CanExecute(item));
        Assert.True(viewModel.UnassignLinklyCloudTerminalCommand.CanExecute(item));
    }

    [Fact]
    public async Task CloudBackendAsync_line_test_is_row_scoped_and_late_result_cannot_update_refreshed_row()
    {
        var terminalId = Guid.NewGuid();
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = new LinklyCloudTerminalListResponse(
                "Production", terminalId, 1,
                [new LinklyCloudTerminalSummary(terminalId, 1, "Front", "Ready", false, true, null, null, "POS-1", 3, "v-3")],
                "Active",
                [new LinklyCloudAssignableDevice("POS-1", "WPF", true, terminalId, 1)])
        };
        var pending = new TaskCompletionSource<LinklyCloudTerminalConnectionTestResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.PendingTerminalConnectionTest = pending;
        var viewModel = new SettingsViewModel(service);
        await viewModel.LoadAsync();
        var original = Assert.Single(viewModel.LinklyCloudTerminalItems);

        var testing = viewModel.TestLinklyCloudTerminalCommand.ExecuteAsync(original);
        Assert.True(original.IsTesting);
        await viewModel.RefreshLinklyCloudBackendTerminalsAsync();
        var refreshed = Assert.Single(viewModel.LinklyCloudTerminalItems);
        pending.SetResult(new LinklyCloudTerminalConnectionTestResponse(
            terminalId, "Production", "v-3", "POS-1", 3, true, "connected",
            DateTimeOffset.UtcNow, "Connected"));
        await testing;

        Assert.NotSame(original, refreshed);
        Assert.Empty(refreshed.ConnectionStatus);
        Assert.Null(refreshed.LastTestedAt);
        Assert.False(viewModel.LinklyConnectionSucceeded);
    }

    [Theory]
    [InlineData("connected", false, "Connected")]
    [InlineData("unreachable", true, "Unable to connect")]
    [InlineData("unknown", true, "The test did not complete. Check the network and terminal, then refresh and try again.")]
    [InlineData("needs-repair", true, "This terminal needs to be paired again. Obtain a new 6-digit code and re-pair this line. If it still fails, ask an administrator to check its Cloud credentials.")]
    public async Task CloudBackendAsync_line_test_maps_authoritative_status_to_safe_localized_message(
        string status,
        bool succeeded,
        string expectedMessage)
    {
        var terminalId = Guid.NewGuid();
        var terminal = new LinklyCloudTerminalSummary(
            terminalId, 1, "Front", "Ready", false, true, null, null, "POS-1", 3, "v-3");
        var pending = new TaskCompletionSource<LinklyCloudTerminalConnectionTestResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        pending.SetResult(new LinklyCloudTerminalConnectionTestResponse(
            terminalId, "Production", "v-3", "POS-1", 3, succeeded, status,
            DateTimeOffset.UtcNow, "unsafe server message"));
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = new LinklyCloudTerminalListResponse(
                "Production", terminalId, 3, [terminal], "Active",
                [new LinklyCloudAssignableDevice("POS-1", "WPF", true, terminalId, 3)]),
            PendingTerminalConnectionTest = pending
        };
        var viewModel = new SettingsViewModel(service);
        await viewModel.LoadAsync();

        var item = Assert.Single(viewModel.LinklyCloudTerminalItems);
        await viewModel.TestLinklyCloudTerminalCommand.ExecuteAsync(item);

        Assert.Equal(expectedMessage, item.ConnectionStatus);
        Assert.DoesNotContain("unsafe server message", item.ConnectionStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CloudBackendAsync_refresh_preserves_newer_local_line_test_for_unchanged_snapshot()
    {
        var terminalId = Guid.NewGuid();
        var serverCheckedAt = new DateTimeOffset(2026, 9, 7, 10, 0, 0, TimeSpan.Zero);
        var localCheckedAt = new DateTimeOffset(2026, 9, 10, 10, 47, 0, TimeSpan.Zero);
        var directory = new LinklyCloudTerminalListResponse(
            "Production", terminalId, 7,
            [new LinklyCloudTerminalSummary(
                terminalId, 1, "Front", "Ready", false, true, "Healthy", serverCheckedAt,
                "POS-1", 7, "v-7")],
            "Active",
            [new LinklyCloudAssignableDevice("POS-1", "WPF", true, terminalId, 7)]);
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = directory,
            PendingTerminalConnectionTest = new TaskCompletionSource<LinklyCloudTerminalConnectionTestResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously)
        };
        service.PendingTerminalConnectionTest.SetResult(new LinklyCloudTerminalConnectionTestResponse(
            terminalId, "Production", "v-7", "POS-1", 7, false, "unreachable",
            localCheckedAt, "Unable to connect", null));
        var viewModel = new SettingsViewModel(service);
        await viewModel.LoadAsync();
        var original = Assert.Single(viewModel.LinklyCloudTerminalItems);

        await viewModel.TestLinklyCloudTerminalCommand.ExecuteAsync(original);
        service.LinklyCloudTerminalDirectories.Enqueue(directory);
        await viewModel.RefreshLinklyCloudBackendTerminalsAsync();

        var refreshed = Assert.Single(viewModel.LinklyCloudTerminalItems);
        Assert.NotSame(original, refreshed);
        Assert.Equal("Unable to connect", refreshed.ConnectionStatus);
        Assert.Equal(localCheckedAt, refreshed.LastTestedAt);
        Assert.False(viewModel.LinklyConnectionSucceeded);

        var newerServerCheckedAt = localCheckedAt.AddMinutes(1);
        service.LinklyCloudTerminalDirectories.Enqueue(directory with
        {
            Terminals = [directory.Terminals[0] with { LastHealthAt = newerServerCheckedAt }]
        });
        await viewModel.RefreshLinklyCloudBackendTerminalsAsync();

        var serverRefreshed = Assert.Single(viewModel.LinklyCloudTerminalItems);
        Assert.Equal("Connected", serverRefreshed.ConnectionStatus);
        Assert.Equal(newerServerCheckedAt, serverRefreshed.LastTestedAt);
        Assert.False(viewModel.LinklyConnectionSucceeded);
    }

    [Theory]
    [InlineData("v-8", "POS-1", 7)]
    [InlineData("v-7", "POS-2", 7)]
    [InlineData("v-7", "POS-1", 8)]
    public async Task CloudBackendAsync_refresh_discards_local_line_test_when_snapshot_identity_changes(
        string terminalVersion,
        string assignedDeviceCode,
        long assignmentRevision)
    {
        var terminalId = Guid.NewGuid();
        var checkedAt = new DateTimeOffset(2026, 9, 10, 10, 47, 0, TimeSpan.Zero);
        var initialTerminal = new LinklyCloudTerminalSummary(
            terminalId, 1, "Front", "Ready", false, true, null, null,
            "POS-1", 7, "v-7");
        var directory = new LinklyCloudTerminalListResponse(
            "Production", terminalId, 7, [initialTerminal], "Active",
            [new LinklyCloudAssignableDevice("POS-1", "WPF", true, terminalId, 7)]);
        var pending = new TaskCompletionSource<LinklyCloudTerminalConnectionTestResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        pending.SetResult(new LinklyCloudTerminalConnectionTestResponse(
            terminalId, "Production", "v-7", "POS-1", 7, false, "unreachable",
            checkedAt, "Unable to connect", null));
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = directory,
            PendingTerminalConnectionTest = pending
        };
        var viewModel = new SettingsViewModel(service);
        await viewModel.LoadAsync();
        await viewModel.TestLinklyCloudTerminalCommand.ExecuteAsync(
            Assert.Single(viewModel.LinklyCloudTerminalItems));
        service.LinklyCloudTerminalDirectories.Enqueue(directory with
        {
            Terminals = [initialTerminal with
            {
                TerminalVersion = terminalVersion,
                AssignedDeviceCode = assignedDeviceCode,
                AssignmentRevision = assignmentRevision
            }]
        });

        await viewModel.RefreshLinklyCloudBackendTerminalsAsync();

        var refreshed = Assert.Single(viewModel.LinklyCloudTerminalItems);
        Assert.Empty(refreshed.ConnectionStatus);
        Assert.Null(refreshed.LastTestedAt);
        Assert.False(viewModel.LinklyConnectionSucceeded);
    }

    [Fact]
    public async Task CloudBackendAsync_refresh_is_rejected_while_assignment_is_in_flight()
    {
        var terminalId = Guid.NewGuid();
        var initial = new LinklyCloudTerminalListResponse(
            "Production", null, 1,
            [new LinklyCloudTerminalSummary(terminalId, 1, "Front", "Ready", false, true, null, null, null, 0, "v-1")],
            "Active",
            [new LinklyCloudAssignableDevice("POS-1", "WPF", true, null, 1)]);
        var assigned = new LinklyCloudTerminalListResponse(
            "Production", terminalId, 2,
            [new LinklyCloudTerminalSummary(terminalId, 1, "Front", "Ready", false, true, null, null, "POS-1", 2, "v-2")],
            "Active",
            [new LinklyCloudAssignableDevice("POS-1", "WPF", true, terminalId, 2)]);
        var pendingAssignment = new TaskCompletionSource<LinklyTerminalAssignmentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = initial,
            PendingTerminalAssignment = pendingAssignment
        };
        var session = new PosSessionState("HB POS", "S01", "Store", "POS-1", "C1", "Cashier", true, 0);
        var viewModel = new SettingsViewModel(
            service,
            session: session,
            confirmLinklyTerminalAssignmentAsync: _ => Task.FromResult(true));
        await viewModel.LoadAsync();
        var item = Assert.Single(viewModel.LinklyCloudTerminalItems);

        var assignment = viewModel.UseLinklyCloudTerminalCommand.ExecuteAsync(item);
        Assert.True(viewModel.IsBusy);
        await viewModel.RefreshLinklyCloudBackendTerminalsAsync();
        Assert.Equal(1, service.BackendDirectoryCallCount);

        pendingAssignment.SetResult(new LinklyTerminalAssignmentResult(true, "saved", assigned));
        await assignment;

        Assert.Equal("POS-1", Assert.Single(viewModel.LinklyCloudTerminalItems).AssignedDeviceCode);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task CloudBackendAsync_line_card_use_for_payments_assigns_once_without_legacy_selection()
    {
        var terminalId = Guid.NewGuid();
        var initial = new LinklyCloudTerminalListResponse(
            "Production", null, 4,
            [new LinklyCloudTerminalSummary(terminalId, 1, "Front", "Ready", false, true, null, null, null, 7, "v-7")],
            "Active",
            [new LinklyCloudAssignableDevice("POS-1", "WPF", true, null, 4)]);
        var assigned = initial with
        {
            SelectedTerminalId = terminalId,
            SelectionRevision = 5,
            Terminals = [initial.Terminals[0] with { AssignedDeviceCode = "POS-1", AssignmentRevision = 8 }],
            Devices = [new LinklyCloudAssignableDevice("POS-1", "WPF", true, terminalId, 5)]
        };
        var assignment = new TaskCompletionSource<LinklyTerminalAssignmentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        assignment.SetResult(new LinklyTerminalAssignmentResult(true, "saved", assigned));
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = initial,
            PendingTerminalAssignment = assignment
        };
        var session = new PosSessionState("HB POS", "S01", "Store", "POS-1", "C1", "Cashier", true, 0);
        var viewModel = new SettingsViewModel(
            service,
            session: session,
            confirmLinklyTerminalAssignmentAsync: _ => Task.FromResult(true));
        await viewModel.LoadAsync();

        await Assert.Single(viewModel.LinklyCloudLines).SelectCommand.ExecuteAsync(null);

        Assert.Equal(1, service.BackendAssignmentCallCount);
        Assert.Null(service.LastBackendSelectionTerminalId);
        Assert.Equal(5, viewModel.LinklyCloudSelectionRevision);
        Assert.Equal("POS-1", Assert.Single(viewModel.LinklyCloudLines).Terminal.AssignedDeviceCode);
    }

    [Fact]
    public async Task CloudBackendAsync_line_health_and_test_failure_use_localized_safe_copy()
    {
        var terminalId = Guid.NewGuid();
        var localization = new LocalizationService();
        localization.SetCulture("zh-CN");
        var pending = new TaskCompletionSource<LinklyCloudTerminalConnectionTestResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = new LinklyCloudTerminalListResponse(
                "Production", null, 1,
                [new LinklyCloudTerminalSummary(terminalId, 1, "Front", "Ready", false, true, "Healthy", DateTimeOffset.UtcNow, null, 0, "v-1")],
                "Active",
                [new LinklyCloudAssignableDevice("POS-1", "WPF", true, null, 1)]),
            PendingTerminalConnectionTest = pending
        };
        var viewModel = new SettingsViewModel(service, localization);
        await viewModel.LoadAsync();
        var item = Assert.Single(viewModel.LinklyCloudTerminalItems);
        Assert.Equal("连接正常", item.ConnectionStatus);
        Assert.Equal("线路 1", item.LaneText);
        Assert.Equal("已就绪", item.PairingStateText);

        var testing = viewModel.TestLinklyCloudTerminalCommand.ExecuteAsync(item);
        pending.SetException(new HttpRequestException("internal endpoint detail"));
        await testing;

        Assert.Equal("测试未完成，请检查网络和终端，刷新后重试。", item.ConnectionStatus);
        Assert.DoesNotContain("internal endpoint detail", item.ConnectionStatus, StringComparison.Ordinal);
        localization.SetCulture("en-US");
        Assert.Equal("Lane 1", item.LaneText);
        Assert.Equal("Ready", item.PairingStateText);
    }

    [Fact]
    public async Task CloudBackendAsync_old_directory_contract_disables_line_management()
    {
        var terminalId = Guid.NewGuid();
        var service = new FakeCardTerminalSetupService(
            CardTerminalConfiguration.Default with { LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync })
        {
            LinklyCloudTerminalDirectory = new LinklyCloudTerminalListResponse(
                "Production", terminalId, 1,
                [new LinklyCloudTerminalSummary(terminalId, 1, "Front", "Ready", false, true, null, null)],
                "Active")
        };
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();

        var item = Assert.Single(viewModel.LinklyCloudTerminalItems);
        Assert.False(viewModel.IsLinklyCloudLineManagementAvailable);
        Assert.False(viewModel.TestLinklyCloudTerminalCommand.CanExecute(item));
        Assert.False(viewModel.UseLinklyCloudTerminalCommand.CanExecute(item));
        Assert.NotEmpty(viewModel.LinklyCloudLineManagementMessage);
    }

    [Fact]
    public async Task LinklyCloud_commands_pair_test_and_save_cloud_mode()
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyCloudPairResult = new LinklyConnectionTestResult(true, "paired"),
            LinklyCloudTestResult = new LinklyConnectionTestResult(true, "cloud connected")
        };
        var viewModel = new SettingsViewModel(service)
        {
            IsLinklyCloudMode = true,
            IsLinklySandbox = true,
            LinklyCloudUsernameText = "cloud-user",
            LinklyCloudPasswordText = "cloud-password",
            LinklyPairCodeText = "12345"
        };

        Assert.True(viewModel.PairLinklyCloudCommand.CanExecute(null));

        await viewModel.PairLinklyCloudCommand.ExecuteAsync(null);
        await viewModel.TestLinklyCommand.ExecuteAsync(null);
        await viewModel.SaveLinklyCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasSavedLinklyCloudSecret);
        Assert.Equal("cloud-user", service.LastPairUsername);
        Assert.Equal("cloud-password", service.LastPairPassword);
        Assert.False(service.LastPairSyncBackendTerminalCredential);
        Assert.Equal("cloud connected", viewModel.LinklyTestStatusMessage);
        Assert.NotNull(service.SavedConfiguration);
        Assert.Equal(CardProcessorKind.Linkly, service.SavedConfiguration!.Processor);
        Assert.Equal(CardTerminalEnvironment.Sandbox, service.SavedConfiguration.Environment);
        Assert.Equal(LinklyConnectionMode.CloudDirectSync, service.SavedConfiguration.LinklyConnectionMode);
    }

    [Fact]
    public async Task PairLinklyCloudCommand_shows_prompt_when_pair_code_is_missing()
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyCloudPairResult = new LinklyConnectionTestResult(true, "paired")
        };
        var viewModel = new SettingsViewModel(service)
        {
            IsLinklyCloudMode = true,
            LinklyCloudUsernameText = "cloud-user",
            LinklyCloudPasswordText = "cloud-password"
        };

        Assert.True(viewModel.PairLinklyCloudCommand.CanExecute(null));

        await viewModel.PairLinklyCloudCommand.ExecuteAsync(null);

        Assert.Equal("Enter the Linkly VPP pair code first.", viewModel.LinklyTestStatusMessage);
        Assert.Equal("Enter the Linkly VPP pair code first.", viewModel.StatusMessage);
        Assert.Equal(0, service.PairLinklyCloudCallCount);
    }

    [Fact]
    public async Task SaveLinklyCloudCredentialCommand_saves_test_account_and_clears_password()
    {
        var service = new FakeCardTerminalSetupService();
        var viewModel = new SettingsViewModel(service)
        {
            IsLinklyCloudMode = true,
            IsLinklySandbox = true,
            LinklyCloudUsernameText = "sandbox-user",
            LinklyCloudPasswordText = "sandbox-password"
        };

        Assert.True(viewModel.SaveLinklyCloudCredentialCommand.CanExecute(null));

        await viewModel.SaveLinklyCloudCredentialCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasSavedLinklyCloudPassword);
        Assert.Equal(string.Empty, viewModel.LinklyCloudPasswordText);
        Assert.Equal(CardTerminalEnvironment.Sandbox, service.SavedLinklyCloudCredential?.Environment);
        Assert.Equal("sandbox-user", service.SavedLinklyCloudCredential?.Username);
        Assert.Equal("sandbox-password", service.SavedLinklyCloudCredential?.Password);
        Assert.False(service.LastSaveLinklyCloudCredentialSyncBackend);
        Assert.Equal("Linkly Cloud API test account saved securely.", viewModel.StatusMessage);
        Assert.Equal("Linkly Cloud API test account saved securely.", viewModel.LinklyTestStatusMessage);
    }

    [Fact]
    public void CancelLinklyCloudPairingCommand_clears_pair_code_and_current_password()
    {
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService())
        {
            IsLinklyCloudMode = true,
            LinklyCloudUsernameText = "sandbox-user",
            LinklyCloudPasswordText = "sandbox-password",
            LinklyPairCodeText = "123456"
        };

        Assert.True(viewModel.CancelLinklyCloudPairingCommand.CanExecute(null));

        viewModel.CancelLinklyCloudPairingCommand.Execute(null);

        Assert.Equal("sandbox-user", viewModel.LinklyCloudUsernameText);
        Assert.Equal(string.Empty, viewModel.LinklyCloudPasswordText);
        Assert.Equal(string.Empty, viewModel.LinklyPairCodeText);
        Assert.False(viewModel.CancelLinklyCloudPairingCommand.CanExecute(null));
    }

    [Fact]
    public void CancelLinklyCloudPairingCommand_uses_password_box_input_state_when_password_text_is_empty()
    {
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService())
        {
            IsLinklyCloudMode = true,
            LinklyCloudUsernameText = "sandbox-user"
        };

        viewModel.RaiseLinklyCloudPasswordInputChanged(true);

        Assert.True(viewModel.CanCancelLinklyCloudPairingFromView);
        Assert.True(viewModel.CancelLinklyCloudPairingCommand.CanExecute(null));

        viewModel.CancelLinklyCloudPairingCommand.Execute(null);

        Assert.Equal("sandbox-user", viewModel.LinklyCloudUsernameText);
        Assert.Equal(string.Empty, viewModel.LinklyCloudPasswordText);
        Assert.Equal(string.Empty, viewModel.LinklyPairCodeText);
        Assert.False(viewModel.CanCancelLinklyCloudPairingFromView);
        Assert.False(viewModel.CancelLinklyCloudPairingCommand.CanExecute(null));
    }

    [Fact]
    public async Task LoadAsync_loads_linkly_cloud_username_and_password_status_without_password()
    {
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default with
        {
            Environment = CardTerminalEnvironment.Sandbox,
            LinklyConnectionMode = LinklyConnectionMode.Cloud
        });
        service.LinklyCloudCredentials[CardTerminalEnvironment.Sandbox] =
            new LinklyCloudCredentialSettings("sandbox-user", "sandbox-password", true);
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();

        Assert.Equal("sandbox-user", viewModel.LinklyCloudUsernameText);
        Assert.True(viewModel.HasSavedLinklyCloudPassword);
        Assert.Equal(string.Empty, viewModel.LinklyCloudPasswordText);
    }

    [Fact]
    public async Task LinklyCloud_secret_status_refreshes_when_environment_changes()
    {
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default with
        {
            Environment = CardTerminalEnvironment.Production,
            LinklyConnectionMode = LinklyConnectionMode.Cloud,
            HasProtectedLinklyCloudSecret = true
        });
        service.LinklyCloudSecretStatuses[CardTerminalEnvironment.Production] = true;
        service.LinklyCloudSecretStatuses[CardTerminalEnvironment.Sandbox] = true;
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();
        viewModel.IsLinklySandbox = true;

        await WaitUntilAsync(() => viewModel.HasSavedLinklyCloudSecret);
        Assert.True(viewModel.TestLinklyCommand.CanExecute(null));
    }

    [Fact]
    public async Task LinklyCloud_environment_change_clears_fields_immediately_before_loading_target_state()
    {
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default with
        {
            Environment = CardTerminalEnvironment.Production,
            LinklyConnectionMode = LinklyConnectionMode.Cloud,
            HasProtectedLinklyCloudSecret = true
        })
        {
            LinklyCloudTestResult = new LinklyConnectionTestResult(true, "cloud connected")
        };
        service.LinklyCloudCredentials[CardTerminalEnvironment.Production] =
            new LinklyCloudCredentialSettings("prod-user", "prod-password", true);
        service.LinklyCloudCredentials[CardTerminalEnvironment.Sandbox] =
            new LinklyCloudCredentialSettings("sandbox-user", "sandbox-password", true);
        service.LinklyCloudSecretStatuses[CardTerminalEnvironment.Production] = true;
        service.LinklyCloudSecretStatuses[CardTerminalEnvironment.Sandbox] = true;
        service.BlockNextLinklyCloudCredentialLoad(CardTerminalEnvironment.Sandbox);
        service.BlockNextLinklyCloudSecretStatus(CardTerminalEnvironment.Sandbox);
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();
        viewModel.LinklyCloudPasswordText = "transient-password";
        viewModel.LinklyPairCodeText = "PAIR123";
        await viewModel.TestLinklyCommand.ExecuteAsync(null);

        Assert.Equal("prod-user", viewModel.LinklyCloudUsernameText);
        Assert.Equal("cloud connected", viewModel.LinklyTestStatusMessage);

        viewModel.IsLinklySandbox = true;

        Assert.Equal(string.Empty, viewModel.LinklyCloudUsernameText);
        Assert.Equal(string.Empty, viewModel.LinklyCloudPasswordText);
        Assert.Equal(string.Empty, viewModel.LinklyPairCodeText);
        Assert.False(viewModel.HasSavedLinklyCloudPassword);
        Assert.False(viewModel.HasSavedLinklyCloudSecret);
        Assert.Equal(string.Empty, viewModel.LinklyTestStatusMessage);

        service.ReleaseLinklyCloudCredentialLoad(CardTerminalEnvironment.Sandbox);
        service.ReleaseLinklyCloudSecretStatus(CardTerminalEnvironment.Sandbox);

        await WaitUntilAsync(() =>
            viewModel.LinklyCloudUsernameText == "sandbox-user" &&
            viewModel.HasSavedLinklyCloudPassword &&
            viewModel.HasSavedLinklyCloudSecret);
    }

    [Fact]
    public async Task LinklyCloud_credential_refresh_ignores_stale_results_after_fast_environment_switches()
    {
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default with
        {
            Environment = CardTerminalEnvironment.Production,
            LinklyConnectionMode = LinklyConnectionMode.Cloud,
            HasProtectedLinklyCloudSecret = true
        });
        service.LinklyCloudCredentials[CardTerminalEnvironment.Production] =
            new LinklyCloudCredentialSettings("prod-user", "prod-password", true);
        service.LinklyCloudCredentials[CardTerminalEnvironment.Sandbox] =
            new LinklyCloudCredentialSettings("sandbox-user", "sandbox-password", false);
        service.LinklyCloudSecretStatuses[CardTerminalEnvironment.Production] = true;
        service.LinklyCloudSecretStatuses[CardTerminalEnvironment.Sandbox] = false;
        service.BlockNextLinklyCloudCredentialLoad(CardTerminalEnvironment.Sandbox);
        service.BlockNextLinklyCloudSecretStatus(CardTerminalEnvironment.Sandbox);
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();

        viewModel.IsLinklySandbox = true;
        viewModel.IsLinklySandbox = false;

        await WaitUntilAsync(() =>
            viewModel.LinklyCloudUsernameText == "prod-user" &&
            viewModel.HasSavedLinklyCloudPassword &&
            viewModel.HasSavedLinklyCloudSecret);

        // 旧环境的异步结果在这里才放行，用来验证不会回写当前环境字段。
        service.ReleaseLinklyCloudCredentialLoad(CardTerminalEnvironment.Sandbox);
        service.ReleaseLinklyCloudSecretStatus(CardTerminalEnvironment.Sandbox);
        await Task.Delay(50);

        Assert.False(viewModel.IsLinklySandbox);
        Assert.Equal("prod-user", viewModel.LinklyCloudUsernameText);
        Assert.Equal(string.Empty, viewModel.LinklyCloudPasswordText);
        Assert.True(viewModel.HasSavedLinklyCloudPassword);
        Assert.True(viewModel.HasSavedLinklyCloudSecret);
    }

    [Fact]
    public async Task LinklyCloud_credential_refresh_does_not_overwrite_user_input_in_same_environment()
    {
        var service = new FakeCardTerminalSetupService(CardTerminalConfiguration.Default with
        {
            Environment = CardTerminalEnvironment.Production,
            LinklyConnectionMode = LinklyConnectionMode.Cloud
        });
        service.LinklyCloudCredentials[CardTerminalEnvironment.Production] =
            new LinklyCloudCredentialSettings("prod-user", "prod-password", true);
        service.LinklyCloudCredentials[CardTerminalEnvironment.Sandbox] =
            new LinklyCloudCredentialSettings("sandbox-loaded", "sandbox-password", true);
        service.BlockNextLinklyCloudCredentialLoad(CardTerminalEnvironment.Sandbox);
        var viewModel = new SettingsViewModel(service);

        await viewModel.LoadAsync();
        viewModel.IsLinklySandbox = true;
        viewModel.LinklyCloudUsernameText = "typed-user";
        viewModel.LinklyCloudPasswordText = "typed-password";

        service.ReleaseLinklyCloudCredentialLoad(CardTerminalEnvironment.Sandbox);
        await Task.Delay(50);

        Assert.Equal("typed-user", viewModel.LinklyCloudUsernameText);
        Assert.Equal("typed-password", viewModel.LinklyCloudPasswordText);
    }

    [Fact]
    public async Task SaveLinklyCloudCredentialCommand_does_not_mark_current_environment_after_switch_during_save()
    {
        var service = new FakeCardTerminalSetupService();
        service.BlockNextLinklyCloudCredentialSave();
        var viewModel = new SettingsViewModel(service)
        {
            IsLinklyCloudMode = true,
            IsLinklySandbox = true,
            LinklyCloudUsernameText = "sandbox-user",
            LinklyCloudPasswordText = "sandbox-password"
        };

        var saveTask = viewModel.SaveLinklyCloudCredentialCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.IsBusy);
        Assert.False(viewModel.CanChangeEnvironment);

        viewModel.IsLinklySandbox = false;
        service.ReleaseLinklyCloudCredentialSave();
        await saveTask;

        Assert.Equal(CardTerminalEnvironment.Sandbox, service.SavedLinklyCloudCredential?.Environment);
        Assert.False(viewModel.HasSavedLinklyCloudPassword);
        Assert.Equal(string.Empty, viewModel.LinklyCloudPasswordText);
    }

    [Fact]
    public async Task PairLinklyCloudCommand_does_not_mark_current_environment_after_switch_during_pair()
    {
        var service = new FakeCardTerminalSetupService
        {
            LinklyCloudPairResult = new LinklyConnectionTestResult(true, "paired")
        };
        service.BlockNextLinklyCloudPair();
        var viewModel = new SettingsViewModel(service)
        {
            IsLinklyCloudMode = true,
            IsLinklySandbox = true,
            LinklyCloudUsernameText = "sandbox-user",
            LinklyCloudPasswordText = "sandbox-password",
            LinklyPairCodeText = "12345"
        };

        var pairTask = viewModel.PairLinklyCloudCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.IsBusy);
        Assert.False(viewModel.CanChangeEnvironment);

        viewModel.IsLinklySandbox = false;
        service.ReleaseLinklyCloudPair();
        await pairTask;

        Assert.Equal(1, service.PairLinklyCloudCallCount);
        Assert.True(service.LinklyCloudSecretStatuses[CardTerminalEnvironment.Sandbox]);
        Assert.False(viewModel.HasSavedLinklyCloudSecret);
    }

    [Fact]
    public async Task LoadAsync_loads_receipt_printer_settings_and_save_persists_changes()
    {
        var store = new FakeReceiptPrinterSettingsStore
        {
            Settings = ReceiptPrinterSettings.Default with
            {
                PrinterPort = "COM3",
                BrandName = "HB",
                StoreName = "Sunnybank",
                StoreAddress = "Shop 1",
                StorePhone = "07",
                Abn = "ABN",
                ReturnPolicy = "Returns within 7 days"
            }
        };
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            receiptPrinterSettingsStore: store,
            receiptPrintService: new FakeReceiptPrintService());

        await viewModel.LoadAsync();

        Assert.Equal("COM3", viewModel.ReceiptPrinterPortText);
        Assert.Equal("HB", viewModel.ReceiptBrandNameText);
        Assert.Equal("Sunnybank", viewModel.ReceiptStoreNameText);

        viewModel.ReceiptPrinterPortText = "USB,";
        viewModel.ReceiptStorePhoneText = "0730000000";
        await viewModel.SaveReceiptPrinterCommand.ExecuteAsync(null);

        Assert.NotNull(store.SavedSettings);
        Assert.Equal("USB,", store.SavedSettings!.PrinterPort);
        Assert.Equal("0730000000", store.SavedSettings.StorePhone);
        Assert.True(store.SavedSettings.PrintBankReceiptText);
        Assert.Equal("Receipt printer settings saved.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Receipt_printer_bank_receipt_toggle_loads_and_saves()
    {
        var store = new FakeReceiptPrinterSettingsStore
        {
            Settings = ReceiptPrinterSettings.Default with { PrintBankReceiptText = false }
        };
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            receiptPrinterSettingsStore: store,
            receiptPrintService: new FakeReceiptPrintService());

        await viewModel.LoadAsync();

        Assert.False(viewModel.ReceiptPrintBankReceiptText);

        viewModel.ReceiptPrintBankReceiptText = true;
        await viewModel.SaveReceiptPrinterCommand.ExecuteAsync(null);

        Assert.True(store.SavedSettings?.PrintBankReceiptText);
    }

    [Fact]
    public async Task TestReceiptPrinterCommand_calls_print_service()
    {
        var printService = new FakeReceiptPrintService
        {
            TestResult = new ReceiptPrintResult(true, "Printer test completed.")
        };
        var store = new FakeReceiptPrinterSettingsStore();
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            receiptPrinterSettingsStore: store,
            receiptPrintService: printService);
        viewModel.ReceiptPrinterPortText = "COM7";

        await viewModel.TestReceiptPrinterCommand.ExecuteAsync(null);

        Assert.Equal(1, printService.TestCallCount);
        Assert.Equal("COM7", store.SavedSettings?.PrinterPort);
        Assert.Equal("Printer test completed.", viewModel.ReceiptPrinterTestStatusMessage);
        Assert.Equal("Printer test completed.", viewModel.StatusMessage);
    }

    [Fact]
    public void Localized_properties_and_status_refresh_when_culture_changes()
    {
        var localization = new LocalizationService();
        var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService(), localization);

        Assert.Equal("Settings", viewModel.ScreenTitleText);
        Assert.Equal("Ready.", viewModel.StatusMessage);

        localization.SetCulture("zh-CN");

        Assert.Equal("\u8BBE\u7F6E", viewModel.ScreenTitleText);
        Assert.Equal("\u5C31\u7EEA\u3002", viewModel.StatusMessage);
        Assert.Equal("\u6570\u636E\u7EF4\u62A4", viewModel.DataMaintenanceTitleText);
        Assert.Equal("\u66F4\u6362\u5206\u5E97\u6CE8\u518C", viewModel.DeviceRegistrationTitleText);
    }

    [Theory]
    [InlineData("notInstalled", "notInstalled")]
    [InlineData("running", "running")]
    [InlineData("starting", "starting")]
    [InlineData("stopping", "stopping")]
    [InlineData("stopped", "stopped")]
    [InlineData("checkFailed", "checkFailed")]
    [InlineData("unexpected-internal-status", "unknown")]
    public async Task Remote_maintenance_status_uses_localized_allowlist_text(string serviceStatus, string expectedStatusKey)
    {
        var localization = new LocalizationService();
        var internalDetail = "internal path C:\\secrets\\remote-maintenance.log";
        var remoteService = new FakeRemoteMaintenanceService
        {
            Status = new RemoteMaintenanceStatus(
                serviceStatus is "running" or "starting" or "stopping" or "stopped",
                "rustdesk-test-id",
                "1.4.9",
                serviceStatus,
                internalDetail)
        };
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            localization,
            remoteMaintenanceService: remoteService);

        await viewModel.SelectRemoteMaintenanceCommand.ExecuteAsync(null);

        Assert.Equal(
            localization.T("settings.remoteMaintenance.status." + expectedStatusKey),
            viewModel.RemoteMaintenanceStatusText);
        Assert.Equal(
            localization.T("settings.remoteMaintenance.detail." + expectedStatusKey),
            viewModel.RemoteMaintenanceDetailText);
        Assert.DoesNotContain(internalDetail, viewModel.RemoteMaintenanceStatusText, StringComparison.Ordinal);
        Assert.DoesNotContain(internalDetail, viewModel.RemoteMaintenanceDetailText, StringComparison.Ordinal);
        Assert.DoesNotContain("[[", viewModel.RemoteMaintenanceStatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("[[", viewModel.RemoteMaintenanceDetailText, StringComparison.Ordinal);

        localization.SetCulture("zh-CN");

        Assert.Equal(
            localization.T("settings.remoteMaintenance.status." + expectedStatusKey),
            viewModel.RemoteMaintenanceStatusText);
        Assert.Equal(
            localization.T("settings.remoteMaintenance.detail." + expectedStatusKey),
            viewModel.RemoteMaintenanceDetailText);
        Assert.DoesNotContain(internalDetail, viewModel.RemoteMaintenanceDetailText, StringComparison.Ordinal);
        localization.SetCulture("en-US");
    }

    [Fact]
    public async Task Remote_maintenance_localized_properties_raise_notifications_and_install_result_relocalizes()
    {
        var localization = new LocalizationService();
        var remoteService = new FakeRemoteMaintenanceService
        {
            Status = new RemoteMaintenanceStatus(false, string.Empty, string.Empty, "notInstalled"),
            InstallResult = new RemoteMaintenanceProvisionResult(
                true,
                "settings.remoteMaintenance.result.configured",
                new RemoteMaintenanceStatus(true, "rustdesk-test-id", "1.4.9", "running"))
        };
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            localization,
            remoteMaintenanceService: remoteService);

        await viewModel.SelectRemoteMaintenanceCommand.ExecuteAsync(null);

        Assert.Equal("Remote maintenance", viewModel.RemoteMaintenanceTitleText);
        Assert.Equal(
            localization.T("settings.remoteMaintenance.description"),
            viewModel.RemoteMaintenanceDescriptionText);
        Assert.Contains("RustDesk", viewModel.RemoteMaintenanceDescriptionText, StringComparison.Ordinal);
        Assert.Equal("Install remote maintenance", viewModel.RemoteMaintenanceActionText);
        Assert.Equal("Not installed", viewModel.RemoteMaintenanceStatusText);

        var changedProperties = new List<string>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
            {
                changedProperties.Add(args.PropertyName);
            }
        };

        localization.SetCulture("zh-CN");

        Assert.Equal("远程维护", viewModel.RemoteMaintenanceTitleText);
        Assert.Equal(
            localization.T("settings.remoteMaintenance.description"),
            viewModel.RemoteMaintenanceDescriptionText);
        Assert.Contains("后台状态服务", viewModel.RemoteMaintenanceDescriptionText, StringComparison.Ordinal);
        Assert.Equal("安装远程维护", viewModel.RemoteMaintenanceActionText);
        Assert.Equal("未安装", viewModel.RemoteMaintenanceStatusText);
        Assert.Contains(nameof(SettingsViewModel.RemoteMaintenanceTitleText), changedProperties);
        Assert.Contains(nameof(SettingsViewModel.RemoteMaintenanceDescriptionText), changedProperties);
        Assert.Contains(nameof(SettingsViewModel.RemoteMaintenanceActionText), changedProperties);
        Assert.Contains(nameof(SettingsViewModel.RemoteMaintenanceStatusText), changedProperties);
        Assert.Contains(nameof(SettingsViewModel.RemoteMaintenanceDetailText), changedProperties);

        changedProperties.Clear();
        await viewModel.InstallRemoteMaintenanceCommand.ExecuteAsync(null);

        Assert.Equal(1, remoteService.InstallCallCount);
        Assert.True(viewModel.IsRemoteMaintenanceConfigured);
        Assert.Equal("检查并恢复", viewModel.RemoteMaintenanceActionText);
        Assert.Contains(nameof(SettingsViewModel.IsRemoteMaintenanceConfigured), changedProperties);
        Assert.Contains(nameof(SettingsViewModel.RemoteMaintenanceActionText), changedProperties);
        Assert.Contains(nameof(SettingsViewModel.RemoteMaintenanceStatusText), changedProperties);
        Assert.Contains(nameof(SettingsViewModel.RemoteMaintenanceDetailText), changedProperties);
        Assert.Equal("远程维护已完成配置，状态服务将在后台上报设备状态。", viewModel.StatusMessage);

        localization.SetCulture("en-US");
        Assert.Equal("Check and restore", viewModel.RemoteMaintenanceActionText);
        Assert.Equal(
            "Remote maintenance is configured. The status service will report device status in the background.",
            viewModel.StatusMessage);
    }

    [Fact]
    public async Task Remote_maintenance_shows_download_progress_and_keeps_final_result_after_late_callbacks()
    {
        var localization = new LocalizationService();
        localization.SetCulture("zh-CN");
        var completion = new TaskCompletionSource<RemoteMaintenanceProvisionResult>();
        var remoteService = new FakeRemoteMaintenanceService { InstallHandler = _ => completion.Task };
        using var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService(), localization,
            remoteMaintenanceService: remoteService);
        var context = new RemoteMaintenanceUiContext();
        var previous = SynchronizationContext.Current;
        Task installTask;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            installTask = viewModel.InstallRemoteMaintenanceCommand.ExecuteAsync(null);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        Assert.True(viewModel.IsRemoteMaintenanceInstalling);
        Assert.True(viewModel.HasRemoteMaintenanceProgress);
        Assert.False(viewModel.InstallRemoteMaintenanceCommand.CanExecute(null));
        remoteService.LastProgress!.Report(RemoteMaintenanceStage.DownloadingRustDesk);
        context.RunQueued();
        Assert.Contains("正在后台下载", viewModel.RemoteMaintenanceProgressText);
        Assert.DoesNotContain("下载完成", viewModel.RemoteMaintenanceProgressText);

        remoteService.LastProgress.Report(RemoteMaintenanceStage.DownloadedInstalling);
        context.RunQueued();
        Assert.Contains("已下载完成并通过校验", viewModel.RemoteMaintenanceProgressText);
        Assert.Equal(viewModel.RemoteMaintenanceProgressText, viewModel.StatusMessage);

        completion.SetResult(new RemoteMaintenanceProvisionResult(false,
            "settings.remoteMaintenance.result.installationFailed", remoteService.Status));
        context.RunQueued();
        await installTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        Assert.False(viewModel.IsRemoteMaintenanceInstalling);
        Assert.Contains("但安装未完成", viewModel.RemoteMaintenanceProgressText);

        remoteService.LastProgress.Report(RemoteMaintenanceStage.DownloadingRustDesk);
        context.RunQueued();
        Assert.Contains("但安装未完成", viewModel.RemoteMaintenanceProgressText);
        localization.SetCulture("en-US");
        Assert.Contains("installation did not complete", viewModel.RemoteMaintenanceProgressText);
        Assert.Equal(viewModel.RemoteMaintenanceProgressText, viewModel.StatusMessage);
    }

    [Theory]
    [InlineData("serverDisabled")]
    [InlineData("serverNotReady")]
    [InlineData("serverConfigurationInvalid")]
    [InlineData("serverAuthorizationFailed")]
    [InlineData("serverUnavailable")]
    [InlineData("endpointUnavailable")]
    [InlineData("connectionFailed")]
    [InlineData("preparationTimedOut")]
    [InlineData("installationLocationInvalid")]
    [InlineData("installationPermissionsInvalid")]
    [InlineData("componentsMissing")]
    [InlineData("existingRustDeskUnmanaged")]
    [InlineData("rustDeskSetupIncomplete")]
    [InlineData("rustDeskConfigurationFailed")]
    public async Task Remote_maintenance_failure_is_localized_in_both_status_surfaces(string failure)
    {
        var localization = new LocalizationService();
        localization.SetCulture("zh-CN");
        var key = "settings.remoteMaintenance.result." + failure;
        var remoteService = new FakeRemoteMaintenanceService();
        remoteService.InstallResult = new(false, key, remoteService.Status);
        using var viewModel = new SettingsViewModel(new FakeCardTerminalSetupService(), localization,
            remoteMaintenanceService: remoteService);

        await viewModel.InstallRemoteMaintenanceCommand.ExecuteAsync(null);

        var chinese = viewModel.RemoteMaintenanceProgressText;
        Assert.NotEmpty(chinese);
        Assert.DoesNotContain("settings.", chinese);
        Assert.Equal(chinese, viewModel.StatusMessage);
        Assert.False(viewModel.IsRemoteMaintenanceInstalling);
        Assert.True(viewModel.InstallRemoteMaintenanceCommand.CanExecute(null));

        localization.SetCulture("en-US");
        Assert.NotEmpty(viewModel.RemoteMaintenanceProgressText);
        Assert.DoesNotContain("settings.", viewModel.RemoteMaintenanceProgressText);
        Assert.NotEqual(chinese, viewModel.RemoteMaintenanceProgressText);
        Assert.Equal(viewModel.RemoteMaintenanceProgressText, viewModel.StatusMessage);
        Assert.Equal(1, remoteService.InstallCallCount);
    }

    private sealed class RemoteMaintenanceUiContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Enqueue((callback, state));
        public void RunQueued()
        {
            var previous = Current;
            try
            {
                SetSynchronizationContext(this);
                while (_callbacks.TryDequeue(out var item)) item.Callback(item.State);
            }
            finally { SetSynchronizationContext(previous); }
        }
    }

    private sealed class FakeRemoteMaintenanceService : IRemoteMaintenanceService
    {
        public Func<IProgress<RemoteMaintenanceStage>?, Task<RemoteMaintenanceProvisionResult>>? InstallHandler { get; set; }
        public IProgress<RemoteMaintenanceStage>? LastProgress { get; private set; }
        public RemoteMaintenanceStatus Status { get; set; } =
            new(false, string.Empty, string.Empty, "notInstalled");

        public RemoteMaintenanceProvisionResult InstallResult { get; set; } =
            new(
                false,
                "settings.remoteMaintenance.result.configurationFailed",
                new(false, string.Empty, string.Empty, "notInstalled"));

        public int InstallCallCount { get; private set; }

        public Task<RemoteMaintenanceStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Status);

        public Task<RemoteMaintenanceProvisionResult> InstallAsync(
            PosSessionState session,
            CancellationToken cancellationToken = default,
            IProgress<RemoteMaintenanceStage>? progress = null)
        {
            InstallCallCount++;
            LastProgress = progress;
            if (InstallHandler is not null) return InstallHandler(progress);
            Status = InstallResult.Status;
            return Task.FromResult(InstallResult);
        }
    }

    private sealed class FakeCardTerminalSetupService(
        CardTerminalConfiguration? configuration = null,
        string? squareAccessToken = null) : ICardTerminalSetupService
    {
        private CardTerminalConfiguration _configuration = configuration ?? CardTerminalConfiguration.Default;
        private string? _squareAccessToken = squareAccessToken;

        public CardTerminalConfiguration? SavedConfiguration { get; private set; }

        public string? SavedSquareAccessToken { get; private set; }

        public LinklyConnectionTestResult LinklyTestResult { get; init; } = new(false, "failed");

        public LinklyLogonResult LinklyLogonResult { get; init; } = new(false, "logon failed");

        public LinklyConnectionTestResult LinklyCloudPairResult { get; init; } = new(false, "pair failed");

        public LinklyConnectionTestResult LinklyCloudTestResult { get; init; } = new(false, "cloud failed");

        public LinklyConnectionTestResult LinklyCloudBackendTestResult { get; init; } = new(false, "backend failed");

        public LinklyConnectionTestResult LinklyCloudBackendStatusTestResult { get; init; } = new(false, "status failed");

        public int BackendDirectoryCallCount { get; private set; }
        public TaskCompletionSource<LinklyCloudTerminalSelectionResponse>? PendingBackendSelection { get; init; }
        public TaskCompletionSource<LinklyCloudTerminalPairResponse>? PendingBackendPair { get; init; }

        public LinklyCloudTerminalListResponse LinklyCloudTerminalDirectory { get; set; } =
            new("Production", null, null, []);

        public LinklyCloudTerminalSelectionResponse LinklyCloudTerminalSelectionResult { get; init; } =
            new("Production", Guid.Empty, 1);

        public LinklyCloudTerminalPairResponse LinklyCloudTerminalPairResult { get; init; } =
            new(Guid.Empty, "Production", string.Empty, "Unpaired", false, "Pair failed.");

        public LinklyCloudTerminalConnectionTestResponse LinklyCloudTerminalConnectionTestResult { get; set; } =
            new(Guid.Empty, "Production", string.Empty, null, 0, false, "unknown", DateTimeOffset.UtcNow, "failed");

        public Exception? LinklyCloudTerminalConnectionTestException { get; set; }

        public int LinklyCloudTestCallCount { get; private set; }

        public int LinklyCloudBackendTestCallCount { get; private set; }

        public int LinklyCloudBackendStatusTestCallCount { get; private set; }

        public int LinklyTestCallCount { get; private set; }

        public int LinklyLogonCallCount { get; private set; }

        public string? LastLinklyTestHost { get; private set; }

        public int? LastLinklyTestPort { get; private set; }

        public TimeSpan? LastLinklyTestTimeout { get; private set; }

        public int SaveLinklyCallCount { get; private set; }

        public int SaveLinklyCloudCallCount { get; private set; }

        public Dictionary<CardTerminalEnvironment, bool> LinklyCloudSecretStatuses { get; } = [];

        public Dictionary<CardTerminalEnvironment, LinklyCloudCredentialSettings> LinklyCloudCredentials { get; } = [];

        private readonly Dictionary<CardTerminalEnvironment, TaskCompletionSource<bool>> _pendingLinklyCloudCredentialLoads = [];

        private readonly Dictionary<CardTerminalEnvironment, TaskCompletionSource<bool>> _pendingLinklyCloudSecretStatuses = [];

        private TaskCompletionSource<bool>? _pendingLinklyCloudCredentialSave;

        private TaskCompletionSource<bool>? _pendingLinklyCloudPair;

        private TaskCompletionSource<bool>? _pendingLinklyCloudBackendStatusTest;

        private TaskCompletionSource<bool>? _pendingLinklyTest;

        private TaskCompletionSource<bool>? _pendingLinklyLogon;

        private TaskCompletionSource<bool> _linklyTestStarted = CreatePendingOperation();

        private TaskCompletionSource<bool> _linklyLogonStarted = CreatePendingOperation();

        public (CardTerminalEnvironment Environment, string Username, string Password)? SavedLinklyCloudCredential { get; private set; }

        public string? LastPairUsername { get; private set; }

        public string? LastPairPassword { get; private set; }

        public bool LastPairSyncBackendTerminalCredential { get; private set; }

        public bool LastSaveLinklyCloudCredentialSyncBackend { get; private set; }

        public int PairLinklyCloudCallCount { get; private set; }

        public IReadOnlyList<SquareLocationOption> SquareLocationsResult { get; init; } = [new("LOC-1", "Main")];

        public IReadOnlyList<SquareDeviceOption> SquareDevicesResult { get; set; } = [new("DEV-1", "Counter", "AVAILABLE")];

        public IReadOnlyList<SquareDeviceCodeOption> SquareDeviceCodesResult { get; set; } = [];

        public SquareDeviceCodeOption CreateDeviceCodeResult { get; set; } =
            new("DC-1", "Counter", "PAIR123", "UNPAIRED", "LOC-1", null, DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow);

        public SquareDeviceCodeOption GetDeviceCodeResult { get; set; } =
            new("DC-1", "Counter", "PAIR123", "UNPAIRED", "LOC-1", null, DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow);

        public (string LocationId, string Name)? LastCreatedDeviceCodeRequest { get; private set; }

        public CardTerminalEnvironment? LastListSquareLocationsEnvironment { get; private set; }

        public CardTerminalEnvironment? LastListSquareDevicesEnvironment { get; private set; }

        public CardTerminalEnvironment? LastSaveSquareEnvironment { get; private set; }

        public CardTerminalEnvironment? LastLinklyCloudTestEnvironment { get; private set; }

        public CardTerminalEnvironment? LastLinklyCloudBackendTestEnvironment { get; private set; }

        public CardTerminalEnvironment? LastLinklyCloudBackendStatusTestEnvironment { get; private set; }

        public CardTerminalEnvironment? LastPairLinklyCloudEnvironment { get; private set; }

        public Guid? LastBackendPairTerminalId { get; private set; }

        public int BackendPairCallCount { get; private set; }

        public string? LastBackendPairCode { get; private set; }

        public Guid? LastBackendConnectionTestTerminalId { get; private set; }

        public CardTerminalEnvironment? LastBackendConnectionTestEnvironment { get; private set; }

        public Guid? LastBackendSelectionTerminalId { get; private set; }

        public long? LastBackendSelectionExpectedRevision { get; private set; }

        public int BackendSelectionCallCount { get; private set; }

        public int BackendAssignmentCallCount { get; private set; }

        public Exception? LinklyCloudTerminalSelectionException { get; set; }

        public Exception? LinklyCloudTerminalPairException { get; set; }

        public TaskCompletionSource<LinklyCloudTerminalConnectionTestResponse>? PendingTerminalConnectionTest { get; set; }

        public TaskCompletionSource<LinklyTerminalAssignmentResult>? PendingTerminalAssignment { get; set; }

        public Queue<Exception> LinklyCloudTerminalListExceptions { get; } = [];

        public Queue<LinklyCloudTerminalListResponse> LinklyCloudTerminalDirectories { get; } = [];

        public Task<CardTerminalConfiguration> LoadConfigurationAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_configuration);
        }

        public Task<LinklyCloudTerminalListResponse> ListLinklyCloudBackendTerminalsAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            BackendDirectoryCallCount++;
            if (LinklyCloudTerminalListExceptions.TryDequeue(out var exception))
            {
                return Task.FromException<LinklyCloudTerminalListResponse>(exception);
            }

            if (LinklyCloudTerminalDirectories.TryDequeue(out var directory))
            {
                return Task.FromResult(directory);
            }

            return Task.FromResult(LinklyCloudTerminalDirectory);
        }

        public Task<LinklyCloudTerminalSelectionResponse> SelectLinklyCloudBackendTerminalAsync(
            CardTerminalEnvironment environment,
            Guid terminalId,
            long? expectedRevision,
            CancellationToken cancellationToken = default)
        {
            BackendSelectionCallCount++;
            LastBackendSelectionTerminalId = terminalId;
            LastBackendSelectionExpectedRevision = expectedRevision;
            if (LinklyCloudTerminalSelectionException is { } exception)
            {
                return Task.FromException<LinklyCloudTerminalSelectionResponse>(exception);
            }

            return PendingBackendSelection?.Task ?? Task.FromResult(LinklyCloudTerminalSelectionResult);
        }

        public Task<LinklyCloudTerminalPairResponse> PairLinklyCloudBackendTerminalAsync(
            CardTerminalEnvironment environment,
            Guid terminalId,
            string pairCode,
            CancellationToken cancellationToken = default)
        {
            BackendPairCallCount++;
            LastBackendPairTerminalId = terminalId;
            LastBackendPairCode = pairCode;
            if (LinklyCloudTerminalPairException is { } exception)
            {
                return Task.FromException<LinklyCloudTerminalPairResponse>(exception);
            }

            return PendingBackendPair?.Task ?? Task.FromResult(LinklyCloudTerminalPairResult);
        }

        public Task<LinklyCloudTerminalConnectionTestResponse> TestLinklyCloudBackendTerminalAsync(
            CardTerminalEnvironment environment,
            LinklyCloudTerminalSummary terminal,
            CancellationToken cancellationToken = default)
        {
            LastBackendConnectionTestEnvironment = environment;
            LastBackendConnectionTestTerminalId = terminal.TerminalId;
            if (LinklyCloudTerminalConnectionTestException is { } exception)
            {
                return Task.FromException<LinklyCloudTerminalConnectionTestResponse>(exception);
            }
            return PendingTerminalConnectionTest?.Task ?? Task.FromResult(LinklyCloudTerminalConnectionTestResult);
        }

        public Task<LinklyTerminalAssignmentResult> AssignLinklyCloudBackendTerminalAsync(
            CardTerminalEnvironment environment,
            LinklyCloudTerminalSummary terminal,
            LinklyCloudAssignableDevice? targetDevice,
            IReadOnlyList<LinklyCloudAssignableDevice> devices,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            BackendAssignmentCallCount++;
            return PendingTerminalAssignment?.Task ?? Task.FromResult(
                new LinklyTerminalAssignmentResult(false, "assignment unavailable"));
        }

        public Task<string?> GetSquareAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_squareAccessToken);
        }

        public Task<IReadOnlyList<SquareLocationOption>> ListSquareLocationsAsync(
            string? accessToken,
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            LastListSquareLocationsEnvironment = environment;
            return Task.FromResult(SquareLocationsResult);
        }

        public Task<IReadOnlyList<SquareDeviceOption>> ListSquareDevicesAsync(
            string? accessToken,
            CardTerminalEnvironment environment,
            string locationId,
            CancellationToken cancellationToken = default)
        {
            LastListSquareDevicesEnvironment = environment;
            Assert.Equal("LOC-1", locationId);
            return Task.FromResult(SquareDevicesResult);
        }

        public Task<IReadOnlyList<SquareDeviceCodeOption>> ListSquareDeviceCodesAsync(
            string? accessToken,
            CardTerminalEnvironment environment,
            string locationId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("LOC-1", locationId);
            return Task.FromResult(SquareDeviceCodesResult);
        }

        public Task<SquareDeviceCodeOption> CreateSquareDeviceCodeAsync(
            string? accessToken,
            CardTerminalEnvironment environment,
            string locationId,
            string name,
            CancellationToken cancellationToken = default)
        {
            LastCreatedDeviceCodeRequest = (locationId, name);
            SquareDeviceCodesResult = [CreateDeviceCodeResult, .. SquareDeviceCodesResult];
            return Task.FromResult(CreateDeviceCodeResult);
        }

        public Task<SquareDeviceCodeOption> GetSquareDeviceCodeAsync(
            string? accessToken,
            CardTerminalEnvironment environment,
            string deviceCodeId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("DC-1", deviceCodeId);
            return Task.FromResult(GetDeviceCodeResult);
        }

        public Task SaveSquareAsync(
            CardTerminalConfiguration configuration,
            string? squareAccessToken,
            CancellationToken cancellationToken = default)
        {
            LastSaveSquareEnvironment = configuration.Environment;
            SavedConfiguration = configuration;
            SavedSquareAccessToken = squareAccessToken;
            // 设置页现在以“后端 token 是否已配置”为状态来源，保存配置时沿用调用方传入的状态即可。
            _configuration = configuration;

            if (!string.IsNullOrWhiteSpace(squareAccessToken))
            {
                _squareAccessToken = squareAccessToken;
            }

            return Task.CompletedTask;
        }

        public Task SaveLinklyAsync(
            CardTerminalConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            SaveLinklyCallCount++;
            SavedConfiguration = configuration;
            _configuration = configuration;
            return Task.CompletedTask;
        }

        public async Task<LinklyConnectionTestResult> PairLinklyCloudAsync(
            CardTerminalEnvironment environment,
            string pairCode,
            string? username,
            string? password,
            bool syncBackendTerminalCredential = false,
            CancellationToken cancellationToken = default)
        {
            LastPairLinklyCloudEnvironment = environment;
            if (_pendingLinklyCloudPair is not null)
            {
                using var registration = cancellationToken.Register(
                    static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
                    _pendingLinklyCloudPair);
                await _pendingLinklyCloudPair.Task;
                _pendingLinklyCloudPair = null;
            }

            PairLinklyCloudCallCount++;
            LastPairUsername = username;
            LastPairPassword = password;
            LastPairSyncBackendTerminalCredential = syncBackendTerminalCredential;
            if (LinklyCloudPairResult.Succeeded)
            {
                LinklyCloudSecretStatuses[environment] = true;
                _configuration = _configuration with { HasProtectedLinklyCloudSecret = true };
            }

            return LinklyCloudPairResult;
        }

        public Task<LinklyCloudCredentialSettings> LoadLinklyCloudCredentialAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return WaitForLinklyCloudCredentialLoadAsync(environment, cancellationToken);
        }

        public async Task SaveLinklyCloudCredentialAsync(
            CardTerminalEnvironment environment,
            string username,
            string password,
            bool syncBackendCredential = false,
            CancellationToken cancellationToken = default)
        {
            if (_pendingLinklyCloudCredentialSave is not null)
            {
                using var registration = cancellationToken.Register(
                    static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
                    _pendingLinklyCloudCredentialSave);
                await _pendingLinklyCloudCredentialSave.Task;
                _pendingLinklyCloudCredentialSave = null;
            }

            SavedLinklyCloudCredential = (environment, username, password);
            LastSaveLinklyCloudCredentialSyncBackend = syncBackendCredential;
            LinklyCloudCredentials[environment] = new LinklyCloudCredentialSettings(username, password, true);
        }

        public Task<LinklyConnectionTestResult> TestLinklyCloudConnectionAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            LastLinklyCloudTestEnvironment = environment;
            LinklyCloudTestCallCount++;
            return Task.FromResult(LinklyCloudTestResult);
        }

        public Task<LinklyConnectionTestResult> TestLinklyCloudBackendConnectionAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            LastLinklyCloudBackendTestEnvironment = environment;
            LinklyCloudBackendTestCallCount++;
            return Task.FromResult(LinklyCloudBackendTestResult);
        }

        public async Task<LinklyConnectionTestResult> TestLinklyCloudBackendTransactionStatusAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            LastLinklyCloudBackendStatusTestEnvironment = environment;
            LinklyCloudBackendStatusTestCallCount++;
            if (_pendingLinklyCloudBackendStatusTest is not null)
            {
                await _pendingLinklyCloudBackendStatusTest.Task.WaitAsync(cancellationToken);
            }

            return LinklyCloudBackendStatusTestResult;
        }

        public Task<bool> HasLinklyCloudSecretAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken = default)
        {
            return WaitForLinklyCloudSecretStatusAsync(environment, cancellationToken);
        }

        public void BlockNextLinklyCloudCredentialLoad(CardTerminalEnvironment environment)
        {
            _pendingLinklyCloudCredentialLoads[environment] = CreatePendingOperation();
        }

        public void ReleaseLinklyCloudCredentialLoad(CardTerminalEnvironment environment)
        {
            ReleasePendingOperation(_pendingLinklyCloudCredentialLoads, environment);
        }

        public void BlockNextLinklyCloudSecretStatus(CardTerminalEnvironment environment)
        {
            _pendingLinklyCloudSecretStatuses[environment] = CreatePendingOperation();
        }

        public void ReleaseLinklyCloudSecretStatus(CardTerminalEnvironment environment)
        {
            ReleasePendingOperation(_pendingLinklyCloudSecretStatuses, environment);
        }

        public void BlockNextLinklyCloudCredentialSave()
        {
            _pendingLinklyCloudCredentialSave = CreatePendingOperation();
        }

        public void ReleaseLinklyCloudCredentialSave()
        {
            _pendingLinklyCloudCredentialSave?.TrySetResult(true);
        }

        public void BlockNextLinklyCloudPair()
        {
            _pendingLinklyCloudPair = CreatePendingOperation();
        }

        public void ReleaseLinklyCloudPair()
        {
            _pendingLinklyCloudPair?.TrySetResult(true);
        }

        public void BlockNextLinklyCloudBackendStatusTest()
        {
            _pendingLinklyCloudBackendStatusTest = CreatePendingOperation();
        }

        public void ReleaseLinklyCloudBackendStatusTest()
        {
            _pendingLinklyCloudBackendStatusTest?.TrySetResult(true);
        }

        public void BlockNextLinklyTest()
        {
            _pendingLinklyTest = CreatePendingOperation();
            _linklyTestStarted = CreatePendingOperation();
        }

        public Task WaitForLinklyTestStartAsync()
        {
            return _linklyTestStarted.Task;
        }

        public void ReleaseLinklyTest()
        {
            _pendingLinklyTest?.TrySetResult(true);
        }

        public void BlockNextLinklyLogon()
        {
            _pendingLinklyLogon = CreatePendingOperation();
            _linklyLogonStarted = CreatePendingOperation();
        }

        public Task WaitForLinklyLogonStartAsync()
        {
            return _linklyLogonStarted.Task;
        }

        public void ReleaseLinklyLogon()
        {
            _pendingLinklyLogon?.TrySetResult(true);
        }

        private async Task<LinklyCloudCredentialSettings> WaitForLinklyCloudCredentialLoadAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken)
        {
            if (_pendingLinklyCloudCredentialLoads.TryGetValue(environment, out var pendingOperation))
            {
                using var registration = cancellationToken.Register(
                    static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
                    pendingOperation);
                await pendingOperation.Task;
            }

            return LinklyCloudCredentials.TryGetValue(environment, out var credential)
                ? credential
                : new LinklyCloudCredentialSettings(null, null, false);
        }

        private async Task<bool> WaitForLinklyCloudSecretStatusAsync(
            CardTerminalEnvironment environment,
            CancellationToken cancellationToken)
        {
            if (_pendingLinklyCloudSecretStatuses.TryGetValue(environment, out var pendingOperation))
            {
                using var registration = cancellationToken.Register(
                    static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
                    pendingOperation);
                await pendingOperation.Task;
            }

            if (LinklyCloudSecretStatuses.TryGetValue(environment, out var hasSecret))
            {
                return hasSecret;
            }

            return
                _configuration.Environment == environment &&
                _configuration.HasProtectedLinklyCloudSecret;
        }

        private static TaskCompletionSource<bool> CreatePendingOperation()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static void ReleasePendingOperation(
            IDictionary<CardTerminalEnvironment, TaskCompletionSource<bool>> pendingOperations,
            CardTerminalEnvironment environment)
        {
            if (pendingOperations.Remove(environment, out var pendingOperation))
            {
                pendingOperation.TrySetResult(true);
            }
        }

        public Task SaveLinklyCloudAsync(
            CardTerminalConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            SaveLinklyCloudCallCount++;
            SavedConfiguration = configuration;
            _configuration = configuration;
            return Task.CompletedTask;
        }

        public async Task<LinklyConnectionTestResult> TestLinklyConnectionAsync(
            string host,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            LinklyTestCallCount++;
            LastLinklyTestHost = host;
            LastLinklyTestPort = port;
            LastLinklyTestTimeout = timeout;
            _linklyTestStarted.TrySetResult(true);
            if (_pendingLinklyTest is not null)
            {
                await _pendingLinklyTest.Task.WaitAsync(cancellationToken);
                _pendingLinklyTest = null;
            }

            return LinklyTestResult;
        }

        public async Task<LinklyLogonResult> LogonLinklyAsync(
            string host,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            LinklyLogonCallCount++;
            _linklyLogonStarted.TrySetResult(true);
            if (_pendingLinklyLogon is not null)
            {
                await _pendingLinklyLogon.Task.WaitAsync(cancellationToken);
                _pendingLinklyLogon = null;
            }

            return LinklyLogonResult;
        }
    }

    private sealed class FakeReceiptPrinterSettingsStore : IReceiptPrinterSettingsStore
    {
        public ReceiptPrinterSettings Settings { get; set; } = ReceiptPrinterSettings.Default;

        public ReceiptPrinterSettings? SavedSettings { get; private set; }

        public Task<ReceiptPrinterSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Settings);
        }

        public Task SaveAsync(ReceiptPrinterSettings settings, CancellationToken cancellationToken = default)
        {
            SavedSettings = settings;
            Settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeReceiptPrintService : IReceiptPrintService
    {
        public ReceiptPrintResult TestResult { get; init; } = new(true, "Printer test completed.");

        public int TestCallCount { get; private set; }

        public Task<ReceiptPrintResult> PrintLatestReceiptAsync(
            ReceiptPrintReason reason = ReceiptPrintReason.LastReceipt,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ReceiptPrintResult> PrintReceiptAsync(
            Guid orderGuid,
            ReceiptPrintReason reason = ReceiptPrintReason.Manual,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ReceiptPrintResult> PrintReceiptAsync(
            ReceiptDetails receipt,
            ReceiptPrintReason reason = ReceiptPrintReason.Manual,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ReceiptPrintResult> TestPrinterAsync(CancellationToken cancellationToken = default)
        {
            TestCallCount++;
            return Task.FromResult(TestResult);
        }
    }

    private sealed class RecordingCardRecoveryResultDialogService : ICardRecoveryResultDialogService
    {
        public event EventHandler<CardRecoveryResultDialogViewModel>? DialogRequested;

        public List<CardRecoveryResultDialogViewModel> RequestedDialogs { get; } = [];

        public void Show(CardRecoveryResultDialogViewModel dialog)
        {
            RequestedDialogs.Add(dialog);
            DialogRequested?.Invoke(this, dialog);
        }
    }

    [Fact]
    public async Task Load_receipt_profile_replaces_six_fields_without_saving()
    {
        var apiClient = new FakeStoreReceiptProfileApiClient(new StoreReceiptProfileDto(
            "S001", "Sunnybank", "HB", "Shop 1\nBrisbane", "07 1234", "ABN 1", "Return within 7 days"));
        var settingsStore = new FakeReceiptPrinterSettingsStore();
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            receiptPrinterSettingsStore: settingsStore,
            storeReceiptProfileApiClient: apiClient);

        await viewModel.LoadReceiptProfileCommand.ExecuteAsync(null);

        Assert.Equal("HB", viewModel.ReceiptBrandNameText);
        Assert.Equal("Sunnybank", viewModel.ReceiptStoreNameText);
        Assert.Equal("Shop 1\nBrisbane", viewModel.ReceiptStoreAddressText);
        Assert.Equal("07 1234", viewModel.ReceiptStorePhoneText);
        Assert.Equal("ABN 1", viewModel.ReceiptAbnText);
        Assert.Equal("Return within 7 days", viewModel.ReceiptReturnPolicyText);
        Assert.Equal(ReceiptPrinterSettings.Default.PrinterPort, viewModel.ReceiptPrinterPortText);
        Assert.Null(settingsStore.SavedSettings);
        Assert.Equal("Loaded — save to apply", viewModel.ReceiptPrinterTestStatusMessage);
    }

    [Fact]
    public async Task Load_receipt_profile_failure_keeps_draft_unchanged()
    {
        var apiClient = new FakeStoreReceiptProfileApiClient(exception: new InvalidOperationException("boom"));
        var settingsStore = new FakeReceiptPrinterSettingsStore();
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            receiptPrinterSettingsStore: settingsStore,
            storeReceiptProfileApiClient: apiClient);

        viewModel.ReceiptBrandNameText = "Draft Brand";
        await viewModel.LoadReceiptProfileCommand.ExecuteAsync(null);

        Assert.Equal("Draft Brand", viewModel.ReceiptBrandNameText);
        Assert.Null(settingsStore.SavedSettings);
    }

    [Fact]
    public async Task Load_receipt_profile_rejects_control_characters_and_keeps_draft()
    {
        var apiClient = new FakeStoreReceiptProfileApiClient(new StoreReceiptProfileDto(
            "S001", "Sunnybank", "HB", "Bad\u0001Address", "07", "ABN", null));
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            storeReceiptProfileApiClient: apiClient);

        viewModel.ReceiptStoreAddressText = "Draft Address";
        await viewModel.LoadReceiptProfileCommand.ExecuteAsync(null);

        Assert.Equal("Draft Address", viewModel.ReceiptStoreAddressText);
        Assert.NotEqual("Loaded — save to apply", viewModel.ReceiptPrinterTestStatusMessage);
    }

    [Fact]
    public async Task Load_receipt_profile_explicit_empty_overwrites_draft()
    {
        var apiClient = new FakeStoreReceiptProfileApiClient(new StoreReceiptProfileDto(
            "S001", "Sunnybank", null, null, null, null, null));
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            storeReceiptProfileApiClient: apiClient);

        viewModel.ReceiptBrandNameText = "Draft Brand";
        await viewModel.LoadReceiptProfileCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, viewModel.ReceiptBrandNameText);
        Assert.Equal("Sunnybank", viewModel.ReceiptStoreNameText);
    }

    private sealed class FakeStoreReceiptProfileApiClient : IStoreReceiptProfileApiClient
    {
        private readonly StoreReceiptProfileDto? _profile;
        private readonly Exception? _exception;

        public FakeStoreReceiptProfileApiClient(StoreReceiptProfileDto? profile = null, Exception? exception = null)
        {
            _profile = profile;
            _exception = exception;
        }

        public int GetCurrentCallCount { get; private set; }

        public Task<StoreReceiptProfileDto> GetCurrentAsync(CancellationToken cancellationToken = default)
        {
            GetCurrentCallCount++;
            if (_exception is not null)
            {
                return Task.FromException<StoreReceiptProfileDto>(_exception);
            }

            return Task.FromResult(_profile ?? throw new InvalidOperationException("No profile configured."));
        }

        // 设置页只通过同步服务使用 sync/ack，直接用这个替身的用例不应该走到它们。
        public Task<StoreReceiptProfileSyncDto> GetSyncAsync(int knownVersion, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<StoreReceiptProfileAckResultDto> AckAsync(int version, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    // 模拟存储层的「已下发只读」语义：保存只落硬件设置，资料字段与版本只能由同步写入。
    private sealed class ManagedProfileSettingsStore(ReceiptPrinterSettings initial) : IReceiptPrinterSettingsStore
    {
        public ReceiptPrinterSettings Settings { get; set; } = initial;

        public int SaveCallCount { get; private set; }

        public Task<ReceiptPrinterSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Settings);
        }

        public Task SaveAsync(ReceiptPrinterSettings settings, CancellationToken cancellationToken = default)
        {
            SaveCallCount++;
            Settings = Settings.ProfileVersion > 0
                ? Settings with
                {
                    PrinterPort = settings.PrinterPort,
                    CutDistance = settings.CutDistance,
                    PrintBankReceiptText = settings.PrintBankReceiptText
                }
                : settings with { ProfileVersion = 0 };
            return Task.CompletedTask;
        }
    }

    private sealed class FakeReceiptProfileSyncService : IReceiptProfileSyncService
    {
        public event EventHandler<ReceiptProfileAppliedEventArgs>? ProfileApplied;

        public Func<Task<ReceiptProfileSyncResult>> OnSyncNow { get; set; } =
            () => Task.FromResult(new ReceiptProfileSyncResult(ReceiptProfileSyncOutcome.UpToDate, 1));

        public int SyncNowCallCount { get; private set; }

        public int SubscriberCount => ProfileApplied?.GetInvocationList().Length ?? 0;

        public Task<ReceiptProfileSyncResult> SyncNowAsync(CancellationToken cancellationToken = default)
        {
            SyncNowCallCount++;
            return OnSyncNow();
        }

        public Task<ReceiptProfileSyncResult?> RunScheduledTickAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ReceiptProfileSyncResult?>(null);
        }

        public void RaiseApplied(string storeCode, int version)
        {
            ProfileApplied?.Invoke(this, new ReceiptProfileAppliedEventArgs(storeCode, version));
        }
    }

    private static ReceiptPrinterSettings ManagedSettings(int version, string brand = "HQ Brand") =>
        ReceiptPrinterSettings.Default with
        {
            BrandName = brand,
            StoreName = "Sunnybank",
            StoreAddress = "Shop 1\nBrisbane",
            StorePhone = "07 3000 0000",
            Abn = "12 345 678 901",
            ReturnPolicy = "Return within 7 days",
            VoucherTerms = "HQ voucher terms",
            InstallmentTerms = "HQ installment terms",
            ProfileVersion = version
        };

    [Fact]
    public async Task Receipt_profile_from_headquarters_makes_the_six_fields_read_only_and_relabels_the_button()
    {
        var localization = new LocalizationService();
        var store = new ManagedProfileSettingsStore(ManagedSettings(3));
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            localization,
            receiptPrinterSettingsStore: store);
        try
        {
            localization.SetCulture("en-US");
            await viewModel.LoadAsync();

            Assert.True(viewModel.IsReceiptProfileManaged);
            Assert.Equal(3, viewModel.ReceiptProfileVersion);
            Assert.Equal("HQ Brand", viewModel.ReceiptBrandNameText);
            Assert.Equal("Sync now", viewModel.ReceiptProfileLoadButtonText);
            Assert.Equal("Published by head office. Edit it in Web Store Management.", viewModel.ReceiptProfileManagedHintText);

            localization.SetCulture("zh-CN");
            Assert.Equal("立即同步", viewModel.ReceiptProfileLoadButtonText);
            Assert.Equal("由总部下发，请在 Web 分店管理修改", viewModel.ReceiptProfileManagedHintText);
        }
        finally
        {
            localization.SetCulture("en-US");
        }
    }

    [Fact]
    public async Task Receipt_profile_without_headquarters_release_stays_editable_with_the_original_button()
    {
        var localization = new LocalizationService();
        var store = new ManagedProfileSettingsStore(ManagedSettings(0));
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            localization,
            receiptPrinterSettingsStore: store);
        try
        {
            localization.SetCulture("en-US");
            await viewModel.LoadAsync();

            Assert.False(viewModel.IsReceiptProfileManaged);
            Assert.Equal("Load from Store Profile", viewModel.ReceiptProfileLoadButtonText);
        }
        finally
        {
            localization.SetCulture("en-US");
        }
    }

    [Fact]
    public async Task Read_only_fields_are_not_treated_as_user_edits_when_saving_hardware_settings()
    {
        var store = new ManagedProfileSettingsStore(ManagedSettings(3));
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            receiptPrinterSettingsStore: store);
        await viewModel.LoadAsync();
        viewModel.ReceiptPrinterPortText = "USB,COM9";
        // 界面里的资料字段可能已过期（后台同步在设置页打开期间写入了新版本）。
        store.Settings = store.Settings with { BrandName = "Newer HQ Brand", ProfileVersion = 4 };

        await viewModel.SaveReceiptPrinterCommand.ExecuteAsync(null);

        // 保存只改了硬件设置；界面随后以存储里的真实内容为准（新版本、新品牌），而不是把旧草稿当成已保存。
        Assert.Equal(1, store.SaveCallCount);
        Assert.Equal("USB,COM9", store.Settings.PrinterPort);
        Assert.Equal("Newer HQ Brand", store.Settings.BrandName);
        Assert.Equal(4, store.Settings.ProfileVersion);
        Assert.Equal("Newer HQ Brand", viewModel.ReceiptBrandNameText);
        Assert.Equal(4, viewModel.ReceiptProfileVersion);
        Assert.Equal("USB,COM9", viewModel.ReceiptPrinterPortText);
        Assert.True(viewModel.IsReceiptProfileManaged);
    }

    [Fact]
    public async Task Sync_now_runs_a_sync_round_instead_of_loading_the_store_profile_when_managed()
    {
        var localization = new LocalizationService();
        var store = new ManagedProfileSettingsStore(ManagedSettings(3));
        var apiClient = new FakeStoreReceiptProfileApiClient(new StoreReceiptProfileDto(
            "S001", "Sunnybank", "Store Brand", null, null, null, null));
        var sync = new FakeReceiptProfileSyncService();
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            localization,
            receiptPrinterSettingsStore: store,
            storeReceiptProfileApiClient: apiClient,
            receiptProfileSyncService: sync);
        try
        {
            localization.SetCulture("en-US");
            await viewModel.LoadAsync();

            sync.OnSyncNow = () => Task.FromResult(new ReceiptProfileSyncResult(ReceiptProfileSyncOutcome.UpToDate, 3));
            await viewModel.LoadReceiptProfileCommand.ExecuteAsync(null);
            Assert.Equal("Already up to date (version 3)", viewModel.ReceiptPrinterTestStatusMessage);

            sync.OnSyncNow = () => Task.FromResult(new ReceiptProfileSyncResult(ReceiptProfileSyncOutcome.NeverReleased));
            await viewModel.LoadReceiptProfileCommand.ExecuteAsync(null);
            Assert.Equal("Head office has not published a receipt profile yet", viewModel.ReceiptPrinterTestStatusMessage);

            sync.OnSyncNow = () => Task.FromResult(
                new ReceiptProfileSyncResult(ReceiptProfileSyncOutcome.Failed, Detail: "offline"));
            await viewModel.LoadReceiptProfileCommand.ExecuteAsync(null);
            Assert.Equal("Sync failed: offline", viewModel.ReceiptPrinterTestStatusMessage);

            sync.OnSyncNow = () => Task.FromResult(new ReceiptProfileSyncResult(ReceiptProfileSyncOutcome.Discarded));
            await viewModel.LoadReceiptProfileCommand.ExecuteAsync(null);
            Assert.Equal(
                "The published receipt profile is invalid and was not applied",
                viewModel.ReceiptPrinterTestStatusMessage);

            Assert.Equal(4, sync.SyncNowCallCount);
            // 只读状态下不走旧的「载入到草稿」接口，也不改动任何字段。
            Assert.Equal(0, apiClient.GetCurrentCallCount);
            Assert.Equal("HQ Brand", viewModel.ReceiptBrandNameText);
        }
        finally
        {
            localization.SetCulture("en-US");
        }
    }

    [Fact]
    public async Task Sync_now_that_applies_a_new_version_refreshes_the_fields_from_local_settings()
    {
        var localization = new LocalizationService();
        var store = new ManagedProfileSettingsStore(ManagedSettings(3));
        var sync = new FakeReceiptProfileSyncService();
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            localization,
            receiptPrinterSettingsStore: store,
            receiptProfileSyncService: sync);
        try
        {
            localization.SetCulture("en-US");
            await viewModel.LoadAsync();
            viewModel.ReceiptPrinterPortText = "USB,COM5";
            sync.OnSyncNow = () =>
            {
                store.Settings = ManagedSettings(4, brand: "Brand v4");
                return Task.FromResult(new ReceiptProfileSyncResult(ReceiptProfileSyncOutcome.Applied, 4));
            };

            await viewModel.LoadReceiptProfileCommand.ExecuteAsync(null);

            Assert.Equal("Updated to version 4", viewModel.ReceiptPrinterTestStatusMessage);
            Assert.Equal("Brand v4", viewModel.ReceiptBrandNameText);
            Assert.Equal(4, viewModel.ReceiptProfileVersion);
            // 硬件设置的未保存草稿不受影响。
            Assert.Equal("USB,COM5", viewModel.ReceiptPrinterPortText);
        }
        finally
        {
            localization.SetCulture("en-US");
        }
    }

    [Fact]
    public async Task Load_with_a_published_but_not_yet_applied_profile_syncs_instead_of_filling_the_draft()
    {
        var localization = new LocalizationService();
        var store = new ManagedProfileSettingsStore(ManagedSettings(0) with { BrandName = "Hand Typed" });
        // 总部已下发 v2，但后台同步还没跑到：GET 接口已经返回了快照。
        var apiClient = new FakeStoreReceiptProfileApiClient(new StoreReceiptProfileDto(
            "S001", "Sunnybank", "HQ v2", null, null, null, null, Version: 2));
        var sync = new FakeReceiptProfileSyncService();
        sync.OnSyncNow = () =>
        {
            store.Settings = ManagedSettings(2, brand: "HQ v2");
            return Task.FromResult(new ReceiptProfileSyncResult(ReceiptProfileSyncOutcome.Applied, 2));
        };
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            localization,
            receiptPrinterSettingsStore: store,
            storeReceiptProfileApiClient: apiClient,
            receiptProfileSyncService: sync);
        try
        {
            localization.SetCulture("en-US");
            await viewModel.LoadAsync();
            Assert.False(viewModel.IsReceiptProfileManaged);

            await viewModel.LoadReceiptProfileCommand.ExecuteAsync(null);

            // 走同步原子写入并进入只读，而不是把快照塞进可手工保存的草稿。
            Assert.Equal(1, sync.SyncNowCallCount);
            Assert.True(viewModel.IsReceiptProfileManaged);
            Assert.Equal("HQ v2", viewModel.ReceiptBrandNameText);
            Assert.Equal("Updated to version 2", viewModel.ReceiptPrinterTestStatusMessage);
            Assert.Equal(0, store.SaveCallCount);
        }
        finally
        {
            localization.SetCulture("en-US");
        }
    }

    [Fact]
    public async Task Load_without_a_published_profile_keeps_the_original_draft_behaviour_even_with_a_sync_service()
    {
        var store = new ManagedProfileSettingsStore(ManagedSettings(0));
        var apiClient = new FakeStoreReceiptProfileApiClient(new StoreReceiptProfileDto(
            "S001", "Sunnybank", "Store Brand", "Shop 9", "07 1", "ABN 9", "Policy 9"));
        var sync = new FakeReceiptProfileSyncService();
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            receiptPrinterSettingsStore: store,
            storeReceiptProfileApiClient: apiClient,
            receiptProfileSyncService: sync);
        await viewModel.LoadAsync();

        await viewModel.LoadReceiptProfileCommand.ExecuteAsync(null);

        Assert.Equal(0, sync.SyncNowCallCount);
        Assert.Equal("Store Brand", viewModel.ReceiptBrandNameText);
        Assert.Equal("Shop 9", viewModel.ReceiptStoreAddressText);
        Assert.False(viewModel.IsReceiptProfileManaged);
        Assert.Equal(0, store.SaveCallCount);
    }

    [Fact]
    public async Task Applied_event_refreshes_an_open_settings_page_without_touching_unsaved_hardware_edits()
    {
        var store = new ManagedProfileSettingsStore(ManagedSettings(0));
        var sync = new FakeReceiptProfileSyncService();
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            receiptPrinterSettingsStore: store,
            receiptProfileSyncService: sync);
        await viewModel.LoadAsync();
        viewModel.ReceiptPrinterPortText = "USB,COM7";
        Assert.False(viewModel.IsReceiptProfileManaged);

        store.Settings = ManagedSettings(1, brand: "Pushed Brand");
        sync.RaiseApplied("S001", 1);

        await WaitUntilAsync(() => viewModel.IsReceiptProfileManaged);
        Assert.Equal("Pushed Brand", viewModel.ReceiptBrandNameText);
        Assert.Equal(1, viewModel.ReceiptProfileVersion);
        Assert.Equal("USB,COM7", viewModel.ReceiptPrinterPortText);
    }

    [Fact]
    public async Task Receipt_terms_are_shown_from_local_settings_and_saved_with_the_printer_settings()
    {
        var store = new FakeReceiptPrinterSettingsStore
        {
            Settings = ReceiptPrinterSettings.Default with
            {
                VoucherTerms = "Saved voucher terms",
                InstallmentTerms = "Saved installment\nterms"
            }
        };
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            receiptPrinterSettingsStore: store,
            receiptPrintService: new FakeReceiptPrintService());

        await viewModel.LoadAsync();

        Assert.Equal("Saved voucher terms", viewModel.ReceiptVoucherTermsText);
        Assert.Equal("Saved installment\nterms", viewModel.ReceiptInstallmentTermsText);

        // 未下发时本机可编辑；保存把两段正文带进设置（存储层负责 trim / 空白归一）。
        viewModel.ReceiptVoucherTermsText = "Edited voucher terms";
        viewModel.ReceiptInstallmentTermsText = string.Empty;
        await viewModel.SaveReceiptPrinterCommand.ExecuteAsync(null);

        Assert.NotNull(store.SavedSettings);
        Assert.Equal("Edited voucher terms", store.SavedSettings!.VoucherTerms);
        Assert.Equal(string.Empty, store.SavedSettings.InstallmentTerms);
    }

    [Fact]
    public async Task Receipt_terms_from_headquarters_are_shown_and_managed_together_with_the_other_fields()
    {
        var store = new ManagedProfileSettingsStore(ManagedSettings(3));
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            receiptPrinterSettingsStore: store);

        await viewModel.LoadAsync();

        // 下发后只读由 IsReceiptProfileManaged 驱动（XAML 契约测试保证两个文本框绑定了它）。
        Assert.True(viewModel.IsReceiptProfileManaged);
        Assert.Equal("HQ voucher terms", viewModel.ReceiptVoucherTermsText);
        Assert.Equal("HQ installment terms", viewModel.ReceiptInstallmentTermsText);
    }

    [Fact]
    public async Task Sync_now_that_applies_a_new_version_refreshes_the_terms_text()
    {
        var store = new ManagedProfileSettingsStore(ManagedSettings(3));
        var sync = new FakeReceiptProfileSyncService();
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            receiptPrinterSettingsStore: store,
            receiptProfileSyncService: sync);
        await viewModel.LoadAsync();
        sync.OnSyncNow = () =>
        {
            store.Settings = ManagedSettings(4) with { VoucherTerms = "v4 voucher", InstallmentTerms = string.Empty };
            return Task.FromResult(new ReceiptProfileSyncResult(ReceiptProfileSyncOutcome.Applied, 4));
        };

        await viewModel.LoadReceiptProfileCommand.ExecuteAsync(null);

        // 立即同步后两段正文刷新；总部清空的那段回到空串（＝界面提示的「留空使用默认文案」）。
        Assert.Equal("v4 voucher", viewModel.ReceiptVoucherTermsText);
        Assert.Equal(string.Empty, viewModel.ReceiptInstallmentTermsText);
    }

    [Fact]
    public async Task Applied_event_refreshes_the_terms_text_on_an_open_settings_page()
    {
        var store = new ManagedProfileSettingsStore(ManagedSettings(0) with { VoucherTerms = string.Empty, InstallmentTerms = string.Empty });
        var sync = new FakeReceiptProfileSyncService();
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            receiptPrinterSettingsStore: store,
            receiptProfileSyncService: sync);
        await viewModel.LoadAsync();
        Assert.Equal(string.Empty, viewModel.ReceiptVoucherTermsText);

        store.Settings = ManagedSettings(1) with { VoucherTerms = "Pushed voucher", InstallmentTerms = "Pushed installment" };
        sync.RaiseApplied("S001", 1);

        await WaitUntilAsync(() => viewModel.IsReceiptProfileManaged);
        Assert.Equal("Pushed voucher", viewModel.ReceiptVoucherTermsText);
        Assert.Equal("Pushed installment", viewModel.ReceiptInstallmentTermsText);
    }

    [Fact]
    public async Task Load_receipt_profile_copies_the_terms_text_into_the_draft_and_does_not_save()
    {
        var apiClient = new FakeStoreReceiptProfileApiClient(new StoreReceiptProfileDto(
            "S001",
            "Sunnybank",
            "HB",
            null,
            null,
            null,
            null,
            VoucherTerms: "Store voucher terms",
            InstallmentTerms: "Store installment terms"));
        var settingsStore = new FakeReceiptPrinterSettingsStore();
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            receiptPrinterSettingsStore: settingsStore,
            storeReceiptProfileApiClient: apiClient);
        viewModel.ReceiptVoucherTermsText = "Old draft";

        await viewModel.LoadReceiptProfileCommand.ExecuteAsync(null);

        Assert.Equal("Store voucher terms", viewModel.ReceiptVoucherTermsText);
        Assert.Equal("Store installment terms", viewModel.ReceiptInstallmentTermsText);
        Assert.Null(settingsStore.SavedSettings);
    }

    [Fact]
    public async Task Load_receipt_profile_without_terms_clears_the_terms_draft_back_to_default()
    {
        var apiClient = new FakeStoreReceiptProfileApiClient(new StoreReceiptProfileDto(
            "S001", "Sunnybank", "HB", null, null, null, null));
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            receiptPrinterSettingsStore: new FakeReceiptPrinterSettingsStore(),
            storeReceiptProfileApiClient: apiClient);
        viewModel.ReceiptVoucherTermsText = "Old draft";
        viewModel.ReceiptInstallmentTermsText = "Old draft";

        await viewModel.LoadReceiptProfileCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, viewModel.ReceiptVoucherTermsText);
        Assert.Equal(string.Empty, viewModel.ReceiptInstallmentTermsText);
    }

    [Theory]
    [InlineData("voucher-too-long")]
    [InlineData("installment-too-long")]
    [InlineData("voucher-control-char")]
    [InlineData("installment-control-char")]
    public async Task Load_receipt_profile_rejects_invalid_terms_and_keeps_the_draft(string scenario)
    {
        var profile = scenario switch
        {
            "voucher-too-long" => new StoreReceiptProfileDto("S001", "Sunnybank", "HB", null, null, null, null, VoucherTerms: new string('x', 601)),
            "installment-too-long" => new StoreReceiptProfileDto("S001", "Sunnybank", "HB", null, null, null, null, InstallmentTerms: new string('x', 601)),
            "voucher-control-char" => new StoreReceiptProfileDto("S001", "Sunnybank", "HB", null, null, null, null, VoucherTerms: "Bad\u0001Line"),
            _ => new StoreReceiptProfileDto("S001", "Sunnybank", "HB", null, null, null, null, InstallmentTerms: "Bad\u0001Line")
        };
        using var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            receiptPrinterSettingsStore: new FakeReceiptPrinterSettingsStore(),
            storeReceiptProfileApiClient: new FakeStoreReceiptProfileApiClient(profile));
        viewModel.ReceiptBrandNameText = "Draft Brand";
        viewModel.ReceiptVoucherTermsText = "Draft voucher";

        await viewModel.LoadReceiptProfileCommand.ExecuteAsync(null);

        Assert.Equal("Draft Brand", viewModel.ReceiptBrandNameText);
        Assert.Equal("Draft voucher", viewModel.ReceiptVoucherTermsText);
        Assert.NotEqual("Loaded — save to apply", viewModel.ReceiptPrinterTestStatusMessage);
        // 错误信息只说明哪个字段不合格，不回显内容。
        Assert.DoesNotContain("xxxx", viewModel.ReceiptPrinterTestStatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disposed_settings_view_model_stops_listening_to_applied_events()
    {
        var store = new ManagedProfileSettingsStore(ManagedSettings(0));
        var sync = new FakeReceiptProfileSyncService();
        var viewModel = new SettingsViewModel(
            new FakeCardTerminalSetupService(),
            receiptPrinterSettingsStore: store,
            receiptProfileSyncService: sync);
        await viewModel.LoadAsync();
        Assert.Equal(1, sync.SubscriberCount);

        viewModel.Dispose();

        Assert.Equal(0, sync.SubscriberCount);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, "hb-platform.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to find repository root.");
    }
}
