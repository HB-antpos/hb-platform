using System.Net;
using System.Net.Http.Json;
using System.Xml.Linq;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.Services.NetworkDiagnostics;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Health;

namespace Hbpos.Client.Tests;

public sealed class DeviceRegistrationViewModelTests
{
    private const string ActivationCode = "HBDEV1-0123456789ABCDEFGHJKMNPQRS-6789ABCDEFGHJKMNPQRSTVWXYZ";

    [Fact]
    public void Dispose_stops_receiving_culture_changes()
    {
        var localization = new LocalizationService();
        var viewModel = new DeviceRegistrationViewModel(
            new StubDeviceRegistrationWorkflowService(),
            localization);
        var localizedChangeCount = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DeviceRegistrationViewModel.TitleText))
            {
                localizedChangeCount++;
            }
        };

        try
        {
            localization.SetCulture(LocalizationService.ChineseCultureName);
            Assert.Equal(1, localizedChangeCount);

            viewModel.Dispose();
            viewModel.Dispose();
            localization.SetCulture(LocalizationService.DefaultCultureName);

            Assert.Equal(1, localizedChangeCount);
        }
        finally
        {
            viewModel.Dispose();
            localization.SetCulture(LocalizationService.DefaultCultureName);
        }
    }

    [Fact]
    public async Task Saving_new_server_address_blocks_activation_until_restart()
    {
        var workflow = new StubDeviceRegistrationWorkflowService();
        var apiServerSettings = CreateApiServerSettings();
        var viewModel = new DeviceRegistrationViewModel(
            workflow,
            apiServerSettings: apiServerSettings);
        await viewModel.InitializeAsync(cachedDevice: null);
        viewModel.ActivationCode = ActivationCode;
        var activationStateChangeCount = 0;
        viewModel.PreviewActivationCommand.CanExecuteChanged += (_, _) => activationStateChangeCount++;

        Assert.Same(apiServerSettings, viewModel.ApiServerSettings);
        Assert.True(viewModel.PreviewActivationCommand.CanExecute(null));

        apiServerSettings.ServerAddressText = "https://new.example.com";
        await apiServerSettings.SaveCommand.ExecuteAsync(null);

        Assert.True(apiServerSettings.RestartRequired);
        Assert.False(viewModel.PreviewActivationCommand.CanExecute(null));
        Assert.False(viewModel.ConfirmActivationCommand.CanExecute(null));
        Assert.True(activationStateChangeCount > 0);

        // 重复加载父页面和共享设置不能解除等待重启期间的注册阻断。
        apiServerSettings.Load();
        await viewModel.InitializeAsync(cachedDevice: null);

        viewModel.ActivationCode = ActivationCode;
        Assert.False(viewModel.PreviewActivationCommand.CanExecute(null));
        await viewModel.PreviewActivationCommand.ExecuteAsync(null);
        Assert.Equal(0, workflow.PreviewCallCount);
    }

    [Fact]
    public async Task A_network_failure_while_checking_the_code_offers_the_network_check_and_a_rejection_does_not()
    {
        var workflow = new StubDeviceRegistrationWorkflowService
        {
            PreviewException = new TaskCanceledException("timeout")
        };
        var viewModel = new DeviceRegistrationViewModel(workflow);
        await viewModel.InitializeAsync(cachedDevice: null);
        viewModel.ActivationCode = ActivationCode;

        await viewModel.PreviewActivationCommand.ExecuteAsync(null);
        Assert.True(viewModel.HasNetworkProblem);

        // 改开通码就是重新来：入口收起。
        viewModel.ActivationCode = ActivationCode + "X";
        Assert.False(viewModel.HasNetworkProblem);

        // 服务端明确拒绝（开通码无效等）不是网络问题。
        workflow.PreviewException = new CatalogApiException("rejected", HttpStatusCode.BadRequest, "DEVICE_ACTIVATION_NOT_AVAILABLE");
        viewModel.ActivationCode = ActivationCode;
        await viewModel.PreviewActivationCommand.ExecuteAsync(null);
        Assert.False(viewModel.HasNetworkProblem);
    }

    [Fact]
    public async Task The_registration_page_diagnose_button_starts_the_shared_network_check()
    {
        var diagnostics = new CountingDiagnostics();
        var service = new ApiServerSettingsService(
            new HttpClient(new StubHttpMessageHandler()),
            () => "https://current.example.com/",
            _ => { });
        var apiServerSettings = new ApiServerSettingsViewModel(
            service,
            new LocalizationService(),
            networkDiagnostics: diagnostics,
            networkFixes: new NoFixes());
        var viewModel = new DeviceRegistrationViewModel(
            new StubDeviceRegistrationWorkflowService(),
            apiServerSettings: apiServerSettings);
        await viewModel.InitializeAsync(cachedDevice: null);

        Assert.True(viewModel.DiagnoseNetworkCommand.CanExecute(null));
        await apiServerSettings.Network!.DiagnoseCommand.ExecuteAsync(null);

        Assert.Equal(1, diagnostics.RunCalls);
        Assert.True(apiServerSettings.Network.HasResult);
    }

    [Fact]
    public void Without_network_services_the_diagnose_button_is_unavailable()
    {
        var viewModel = new DeviceRegistrationViewModel(
            new StubDeviceRegistrationWorkflowService(),
            apiServerSettings: CreateApiServerSettings());

        Assert.False(viewModel.DiagnoseNetworkCommand.CanExecute(null));
    }

    [Fact]
    public void Registration_view_has_the_network_check_button_next_to_both_status_messages()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "apps", "pos-wpf", "src", "Hbpos.Client.Wpf", "Views", "Screens", "DeviceRegistrationView.xaml"));

        var buttons = XDocument.Parse(xaml)
            .Descendants()
            .Where(element =>
                element.Name.LocalName == "Button" &&
                element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "AutomationProperties.AutomationId" &&
                    attribute.Value == "RegistrationDiagnoseNetworkButton"))
            .ToArray();

        Assert.Equal(2, buttons.Length);
        Assert.All(buttons, button =>
        {
            Assert.Equal("{Binding DiagnoseNetworkCommand}", button.Attribute("Command")?.Value);
            Assert.Contains("HasNetworkProblem", button.Attribute("Visibility")?.Value);
        });
    }

    private sealed class CountingDiagnostics : INetworkDiagnosticsService
    {
        public int RunCalls { get; private set; }

        public Task<NetworkDiagnosisReport> RunAsync(Uri apiBaseAddress, CancellationToken cancellationToken = default)
        {
            RunCalls++;
            var m = new NetworkMeasurements { Reply = ReplyOutcome.Online, SystemDnsMs = 30, PublicDnsMs = 30, DirectReplyMs = 200 };
            return Task.FromResult(new NetworkDiagnosisReport(
                apiBaseAddress.ToString(),
                DateTimeOffset.UnixEpoch,
                TimeSpan.FromSeconds(1),
                NetworkDiagnosisRules.BuildSteps(m),
                NetworkDiagnosisRules.Evaluate(m),
                m,
                "test"));
        }
    }

    private sealed class NoFixes : INetworkFixService
    {
        public NetworkFixPlan? Plan(NetworkFixKind kind) => null;

        public Task<NetworkFixResult> ApplyAsync(NetworkFixPlan plan, CancellationToken cancellationToken = default) =>
            Task.FromResult(NetworkFixResult.Done);

        public NetworkFixRecord? GetApplied(NetworkFixKind kind) => null;

        public Task<NetworkFixResult> UndoAsync(NetworkFixKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult(NetworkFixResult.Done);
    }

    [Fact]
    public void Registration_and_settings_views_use_the_shared_server_settings_panel()
    {
        var viewsRoot = Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views");
        var deviceRegistrationXaml = File.ReadAllText(Path.Combine(viewsRoot, "Screens", "DeviceRegistrationView.xaml"));
        var settingsXaml = File.ReadAllText(Path.Combine(viewsRoot, "Screens", "SettingsView.xaml"));
        var sharedPanelXaml = File.ReadAllText(Path.Combine(viewsRoot, "Controls", "ApiServerSettingsPanel.xaml"));
        var mainWindowXaml = File.ReadAllText(Path.Combine(viewsRoot, "..", "MainWindow.xaml"));
        const string panelBinding = "<controls:ApiServerSettingsPanel DataContext=\"{Binding ApiServerSettings}\"";

        Assert.Contains(panelBinding, deviceRegistrationXaml);
        Assert.Contains(panelBinding, settingsXaml);
        Assert.True(
            deviceRegistrationXaml.IndexOf(panelBinding, StringComparison.Ordinal) <
            deviceRegistrationXaml.IndexOf("SelectedItem=\"{Binding SelectedStore", StringComparison.Ordinal));
        Assert.True(
            settingsXaml.IndexOf("IsDeviceRegistrationSelected", StringComparison.Ordinal) <
            settingsXaml.IndexOf(panelBinding, StringComparison.Ordinal));
        Assert.True(
            settingsXaml.IndexOf(panelBinding, StringComparison.Ordinal) <
            settingsXaml.IndexOf("ReregisterDeviceCommand", StringComparison.Ordinal));
        Assert.Contains("settings.serverAddress.address", sharedPanelXaml);

        var cashierLoginOverlay = XDocument.Parse(mainWindowXaml)
            .Descendants()
            .Single(element => element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "AutomationProperties.AutomationId" &&
                attribute.Value == "CashierLoginOverlay"));
        // 登录弹窗左栏的「服务器地址」是个开关，选中后右栏换成共享的服务器设置面板；默认收起。
        Assert.Contains(
            cashierLoginOverlay.Descendants(),
            element =>
                element.Name.LocalName == "ToggleButton" &&
                element.Attributes().Any(attribute => attribute.Name.LocalName == "Name" && attribute.Value == "CashierLoginServerToggle"));
        var serverPanel = cashierLoginOverlay
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "ApiServerSettingsPanel" &&
                element.Attribute("DataContext")?.Value == "{Binding ApiServerSettings}");
        var panelHost = serverPanel.Ancestors().First(element => element.Name.LocalName == "ScrollViewer");
        Assert.Contains(
            panelHost.Descendants(),
            element =>
                element.Name.LocalName == "Setter" &&
                element.Attribute("Property")?.Value == "Visibility" &&
                element.Attribute("Value")?.Value == "Collapsed");

        var settingsViewModelSource = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "ViewModels",
            "SettingsViewModel.cs"));
        Assert.Contains(
            "SelectCategoryAsync(SettingsCategory.DeviceRegistration, Permissions.PosTerminal.Settings.DeviceRegistration)",
            settingsViewModelSource);
    }

    [Fact]
    public void Registration_view_wraps_centered_card_in_vertical_scroll_container()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Screens",
            "DeviceRegistrationView.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var scrollViewer = Assert.Single(document.Descendants(presentation + "ScrollViewer").Where(
            element => (string?)element.Attribute(x + "Name") == "RegistrationScrollViewer"));
        Assert.Equal("Auto", (string?)scrollViewer.Attribute("VerticalScrollBarVisibility"));
        Assert.Equal("Disabled", (string?)scrollViewer.Attribute("HorizontalScrollBarVisibility"));

        var viewportGrid = Assert.Single(scrollViewer.Elements(presentation + "Grid"));
        Assert.Equal(
            "{Binding ElementName=RegistrationScrollViewer, Path=ViewportHeight}",
            (string?)viewportGrid.Attribute("MinHeight"));
        var card = Assert.Single(viewportGrid.Elements(presentation + "Border"));
        Assert.Equal("Center", (string?)card.Attribute("VerticalAlignment"));
        var commands = card.Descendants(presentation + "Button")
            .Select(button => (string?)button.Attribute("Command"))
            .Where(command => command is not null)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("{Binding PreviewActivationCommand}", commands);
        Assert.Contains("{Binding ConfirmActivationCommand}", commands);
        Assert.Contains("{Binding VerifyCommand}", commands);
        Assert.Contains("PreviewDeviceSystem", File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Screens",
            "DeviceRegistrationView.xaml")));
        Assert.Contains("StoreTransitionDisplay", File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Screens",
            "DeviceRegistrationView.xaml")));
    }

    [Fact]
    public void Activation_code_inputs_become_read_only_while_recovery_is_pending()
    {
        var repoRoot = FindRepoRoot();
        var registrationDocument = XDocument.Load(Path.Combine(
            repoRoot,
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Screens",
            "DeviceRegistrationView.xaml"));
        var mainWindowDocument = XDocument.Load(Path.Combine(
            repoRoot,
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "MainWindow.xaml"));

        AssertActivationInputIsRecoveryLocked(registrationDocument, "DeviceActivationCodeInput");
        AssertActivationInputIsRecoveryLocked(mainWindowDocument, "DeviceRebindActivationCodeInput");
        var serverSettingsPanel = Assert.Single(registrationDocument.Descendants().Where(element =>
            element.Name.LocalName == "ApiServerSettingsPanel"));
        Assert.Equal(
            "{Binding CanEditServerSettings}",
            serverSettingsPanel.Attribute("IsEnabled")?.Value);
    }

    [Fact]
    public void Server_settings_header_uses_constrained_grid_for_wrapping_description()
    {
        var document = XDocument.Load(Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Controls",
            "ApiServerSettingsPanel.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var headerGrid = Assert.Single(document.Descendants(presentation + "Grid").Where(
            element => (string?)element.Attribute("Margin") == "0,0,0,12"));
        var columns = Assert.Single(headerGrid.Elements(presentation + "Grid.ColumnDefinitions"))
            .Elements(presentation + "ColumnDefinition")
            .ToArray();
        Assert.Equal(["Auto", "*"], columns.Select(column => (string?)column.Attribute("Width")));

        var icon = Assert.Single(headerGrid.Elements().Where(element => element.Name.LocalName == "PackIcon"));
        Assert.Equal("0", (string?)icon.Attribute("Grid.Column"));
        var textColumn = Assert.Single(headerGrid.Elements(presentation + "StackPanel"));
        Assert.Equal("1", (string?)textColumn.Attribute("Grid.Column"));
        var description = Assert.Single(textColumn.Elements(presentation + "TextBlock").Where(
            element => ((string?)element.Attribute("Text"))?.Contains("settings.serverAddress.description", StringComparison.Ordinal) == true));
        Assert.Equal("Wrap", (string?)description.Attribute("TextWrapping"));
    }

    private static ApiServerSettingsViewModel CreateApiServerSettings()
    {
        var service = new ApiServerSettingsService(
            new HttpClient(new StubHttpMessageHandler()),
            () => "https://current.example.com/",
            _ => { });
        return new ApiServerSettingsViewModel(service, new LocalizationService());
    }

    private static HttpResponseMessage OnlineResponse()
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(ApiResult<HealthCheckResponse>.Ok(
                new HealthCheckResponse(true, DateTimeOffset.UnixEpoch, "ok")))
        };
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

    private static void AssertActivationInputIsRecoveryLocked(XDocument document, string automationId)
    {
        var input = Assert.Single(document.Descendants().Where(element =>
            element.Name.LocalName == "TextBox" &&
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "AutomationProperties.AutomationId" &&
                attribute.Value == automationId)));
        Assert.Equal(
            "{Binding IsActivationRecoveryPending}",
            input.Attribute("IsReadOnly")?.Value);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(OnlineResponse());
        }
    }

    private sealed class StubDeviceRegistrationWorkflowService : IDeviceRegistrationWorkflowService
    {
        private static readonly StoreSelectionItem Store = new("1002", "Lutwyche", true);

        public int RegisterCallCount { get; private set; }

        public int VerifyCallCount { get; private set; }

        public int PreviewCallCount { get; private set; }

        public Exception? PreviewException { get; set; }

        public string GetHardwareId() => "HW-001";

        public Task<DeviceRegistrationLoadResult> LoadStoresAsync(
            LocalDeviceCache? cachedDevice,
            bool isReregisterMode,
            string? excludedStoreCode = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DeviceRegistrationLoadResult(
                [Store],
                Store,
                "POS-001",
                false,
                "Ready"));
        }

        public Task<DeviceRegistrationActionResult> RegisterAsync(
            StoreSelectionItem selectedStore,
            string hardwareId,
            CancellationToken cancellationToken = default)
        {
            RegisterCallCount++;
            return Task.FromResult(EmptyActionResult());
        }

        public Task<DeviceRegistrationActionResult> VerifyAsync(
            StoreSelectionItem selectedStore,
            string deviceCode,
            string hardwareId,
            CancellationToken cancellationToken = default)
        {
            VerifyCallCount++;
            return Task.FromResult(EmptyActionResult());
        }

        public Task<DeviceRegistrationActionResult> ReregisterAsync(
            StoreSelectionItem selectedStore,
            string hardwareId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(EmptyActionResult());
        }

        public Task<DeviceActivationPreviewResult> PreviewActivationCodeAsync(
            string activationCode,
            CancellationToken cancellationToken = default)
        {
            PreviewCallCount++;
            if (PreviewException is not null)
            {
                throw PreviewException;
            }

            return Task.FromResult(new DeviceActivationPreviewResult(
                activationCode,
                "1002",
                "Lutwyche",
                "Windows",
                DateTimeOffset.UtcNow.AddMinutes(15)));
        }

        private static DeviceRegistrationActionResult EmptyActionResult()
        {
            return new DeviceRegistrationActionResult(
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                false,
                string.Empty,
                null,
                false,
                false);
        }
    }
}
