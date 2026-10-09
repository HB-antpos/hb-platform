using System.Collections.ObjectModel;
using System.Threading;
using BlazorApp.Shared.Constants;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels.Settings;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Stores;
using Hbpos.RemoteMaintenance.Setup;

namespace Hbpos.Client.Wpf.ViewModels;

public enum SettingsCategory
{
    DataMaintenance,
    PaymentTerminal,
    ReceiptPrinter,
    DeviceRegistration,
    RemoteMaintenance
}

public enum LinklySettingsMode
{
    LocalIp,
    CloudDirectSync,
    CloudBackendAsync
}

public sealed partial class LinklyModePriorityItem(LinklySettingsMode mode) : ObservableObject
{
    [ObservableProperty]
    private int _rank;

    public LinklySettingsMode Mode { get; } = mode;

    public string ModeKey => Mode switch
    {
        LinklySettingsMode.CloudDirectSync => "settings.linkly.mode.cloudDirectSync",
        LinklySettingsMode.CloudBackendAsync => "settings.linkly.mode.cloudBackendAsync",
        _ => "settings.linkly.mode.localIp"
    };

    public string HelpKey => Mode switch
    {
        LinklySettingsMode.CloudDirectSync => "settings.linkly.mode.cloudDirectSync.help",
        LinklySettingsMode.CloudBackendAsync => "settings.linkly.mode.cloudBackendAsync.help",
        _ => "settings.linkly.mode.localIp.help"
    };

    public string RoleKey => Rank == 1
        ? "settings.linkly.priority.primary"
        : "settings.linkly.priority.fallback";

    partial void OnRankChanged(int value)
    {
        OnPropertyChanged(nameof(RoleKey));
    }
}

public sealed partial class LinklyCloudTerminalManagementItem(
    LinklyCloudTerminalSummary terminal,
    IReadOnlyList<LinklyCloudAssignableDevice> assignableDevices,
    LinklyCloudAssignableDevice? selectedTargetDevice,
    string? connectionStatus = null,
    Func<string, string>? localize = null,
    DateTimeOffset? lastTestedAt = null) : ObservableObject
{
    public LinklyCloudTerminalSummary Terminal { get; } = terminal;

    public IReadOnlyList<LinklyCloudAssignableDevice> AssignableDevices { get; } = assignableDevices;

    [ObservableProperty]
    private LinklyCloudAssignableDevice? _selectedTargetDevice = selectedTargetDevice;

    [ObservableProperty]
    private string _connectionStatus = connectionStatus ?? terminal.LastHealthStatus ?? string.Empty;

    [ObservableProperty]
    private DateTimeOffset? _lastTestedAt = lastTestedAt ?? terminal.LastHealthAt;

    [ObservableProperty]
    private bool _isTesting;

    public Guid TerminalId => Terminal.TerminalId;
    public int LaneNo => Terminal.LaneNo;
    public string DisplayName => Terminal.DisplayName;
    public string LaneText => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        localize?.Invoke("settings.linkly.cloudBackend.lane") ?? "Lane {0}",
        LaneNo);
    public string PairingStateText => Terminal.PairingState.Trim().ToUpperInvariant() switch
    {
        "READY" => localize?.Invoke("settings.linkly.cloudBackend.stateReady") ?? "Ready",
        "UNPAIRED" => localize?.Invoke("settings.linkly.cloudBackend.stateUnpaired") ?? "Unpaired",
        "NEEDSREPAIR" => localize?.Invoke("settings.linkly.cloudBackend.stateNeedsRepair") ?? "Needs repair",
        "PAIRING" => localize?.Invoke("settings.linkly.cloudBackend.statePairing") ?? "Pairing",
        _ => localize?.Invoke("settings.linkly.cloudBackend.stateUnknown") ?? "Unknown"
    };
    public bool IsBusy => Terminal.IsBusy;
    public bool IsReady => Terminal.IsReady;
    public string? AssignedDeviceCode => Terminal.AssignedDeviceCode;
    public bool SupportsManagement => !string.IsNullOrWhiteSpace(Terminal.TerminalVersion);
    public string LastTestedText => LastTestedAt is null
        ? "—"
        : LastTestedAt.Value.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(LaneText));
        OnPropertyChanged(nameof(PairingStateText));
        OnPropertyChanged(nameof(LastTestedText));
    }

    partial void OnLastTestedAtChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(LastTestedText));
}

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    internal const string DefaultSquareDeviceCodeName = "HBPOS Terminal";

    private readonly ICardTerminalSetupService _setupService;
    private readonly ILocalizationService? _localization;
    private readonly ApiServerSettingsViewModel? _apiServerSettings;
    private readonly Func<CancellationToken, Task>? _downloadCatalogAsync;
    private readonly Func<CancellationToken, Task>? _resetCatalogAsync;
    private readonly Func<CancellationToken, Task>? _resetTestSalesDataAsync;
    private readonly Func<Task<bool>>? _confirmResetTestSalesDataAsync;
    private readonly Func<string, Task<bool>>? _confirmLinklyTerminalAssignmentAsync;
    private readonly Func<Task<DeviceReregistrationStartResult>>? _reregisterDeviceAsync;
    private readonly Func<CancellationToken, Task<AppUpdateCoordinatorResult>>? _checkForAppUpdateAsync;
    private readonly Action? _returnToPos;
    private readonly IReceiptPrinterSettingsStore? _receiptPrinterSettingsStore;
    private readonly IReceiptPrintService? _receiptPrintService;
    private readonly IStoreReceiptProfileApiClient? _storeReceiptProfileApiClient;
    private readonly ICardRecoveryResultDialogService? _cardRecoveryResultDialogService;
    private readonly ICashierSessionContext _cashierSessionContext;
    private readonly bool _enforcePermissions;
    private readonly IOperationAuthorizationService? _operationAuthorizationService;
    private readonly IRemoteMaintenanceService? _remoteMaintenanceService;
    private readonly IOperationAuditLogger? _operationAuditLogger;
    private readonly DataMaintenanceSection _dataMaintenanceSection;
    private readonly ReceiptPrinterSection _receiptPrinterSection;
    private CardTerminalConfiguration _loadedConfiguration = CardTerminalConfiguration.Default;
    private string? _savedSquareLocationId;
    private string? _savedSquareDeviceId;
    private string? _devicesLoadedForLocationId;
    private string _statusKey = "settings.status.ready";
    private object[] _statusArgs = [];
    private string? _statusOverride;
    private string? _linklyTestStatusKey;
    private object[] _linklyTestStatusArgs = [];
    private string? _linklyTestStatusOverride;
    private string? _receiptPrinterTestStatusOverride;
    private string _lastSquareDeviceCodeNameSuggestion = DefaultSquareDeviceCodeName;
    private int _linklySecretStatusVersion;
    private int _linklyCredentialStatusVersion;
    private int _linklyCredentialEditVersion;
    private bool _disposed;
    private bool _hasLinklyCloudPasswordInput;
    private bool _syncingLinkly;
    private bool _syncingSquare;
    private Guid? _persistedLinklyCloudTerminalId;
    private int _linklyTerminalDirectoryGeneration;
    private int _linklyTerminalDirectoryRequestGeneration;
    private bool _isLinklyLineOperationBusy;
    private readonly Dictionary<Guid, int> _linklyTerminalTestGenerations = [];
    private readonly Dictionary<Guid, int> _linklyTerminalAssignmentGenerations = [];
    private readonly Dictionary<LinklyTerminalTestSnapshotKey, LinklyTerminalTestSnapshot> _linklyTerminalTestSnapshots = [];
    private readonly SquareSettingsState _squareState = new();
    private readonly SquareSettingsCoordinator _squareCoordinator;
    private readonly LinklySettingsState _linklyState = new();
    private readonly LinklySettingsCoordinator _linklyCoordinator;

    [ObservableProperty]
    private SettingsCategory _selectedCategory = SettingsCategory.DataMaintenance;

    [ObservableProperty]
    private bool _isSquareSandbox;

    [ObservableProperty]
    private bool _isLinklySandbox;

    [ObservableProperty]
    private bool _isLinklySetupInstructionsOpen;

    [ObservableProperty]
    private bool _hasSavedSquareToken;

    [ObservableProperty]
    private SquareLocationOption? _selectedSquareLocation;

    [ObservableProperty]
    private SquareDeviceOption? _selectedSquareDevice;

    [ObservableProperty]
    private SquareDeviceCodeOption? _selectedSquareDeviceCode;

    [ObservableProperty]
    private string _squareDeviceCodeNameText = DefaultSquareDeviceCodeName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLinklyRecommendedAddress))]
    private string _linklyHostText = CardTerminalConfiguration.Default.LinklyHost;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLinklyRecommendedAddress))]
    private string _linklyPortText = CardTerminalConfiguration.Default.LinklyPort.ToString();

    [ObservableProperty]
    private LinklySettingsMode _selectedLinklyMode = LinklySettingsMode.LocalIp;

    [ObservableProperty]
    private string _linklyPairCodeText = string.Empty;

    [ObservableProperty]
    private string _linklyCloudUsernameText = string.Empty;

    [ObservableProperty]
    private string _linklyCloudPasswordText = string.Empty;

    [ObservableProperty]
    private bool _hasSavedLinklyCloudPassword;

    [ObservableProperty]
    private bool _hasSavedLinklyCloudSecret;

    [ObservableProperty]
    private LinklyCloudTerminalSummary? _selectedLinklyCloudTerminal;

    [ObservableProperty]
    private string _timeoutSecondsText = CardTerminalConfiguration.Default.TerminalTimeoutSeconds.ToString();

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _linklyConnectionSucceeded;

    [ObservableProperty]
    private bool? _linklyPinPadLoggedOn;

    [ObservableProperty]
    private string _linklyTestStatusMessage = string.Empty;

    [ObservableProperty]
    private string _receiptPrinterPortText = ReceiptPrinterSettings.Default.PrinterPort;

    [ObservableProperty]
    private string _receiptBrandNameText = ReceiptPrinterSettings.Default.BrandName;

    [ObservableProperty]
    private string _receiptStoreNameText = string.Empty;

    [ObservableProperty]
    private string _receiptStoreAddressText = string.Empty;

    [ObservableProperty]
    private string _receiptStorePhoneText = string.Empty;

    [ObservableProperty]
    private string _receiptAbnText = string.Empty;

    [ObservableProperty]
    private string _receiptReturnPolicyText = string.Empty;

    // 退款代金券「VOUCHER TERMS」/ 分期小票「INSTALLMENT TERMS」下面的定制正文；留空＝使用默认文案。
    // 与退货政策一样：总部下发后只读，未下发时可在本机编辑。
    [ObservableProperty]
    private string _receiptVoucherTermsText = string.Empty;

    [ObservableProperty]
    private string _receiptInstallmentTermsText = string.Empty;

    [ObservableProperty]
    private bool _receiptPrintBankReceiptText = ReceiptPrinterSettings.Default.PrintBankReceiptText;

    [ObservableProperty]
    private string _receiptPrinterTestStatusMessage = string.Empty;

    public string AppUpdateChannelText { get; }

    public SettingsViewModel(
        ICardTerminalSetupService setupService,
        ILocalizationService? localization = null,
        Func<CancellationToken, Task>? downloadCatalogAsync = null,
        Func<CancellationToken, Task>? resetCatalogAsync = null,
        Func<Task<DeviceReregistrationStartResult>>? reregisterDeviceAsync = null,
        Action? returnToPos = null,
        IReceiptPrinterSettingsStore? receiptPrinterSettingsStore = null,
        IReceiptPrintService? receiptPrintService = null,
        IStoreReceiptProfileApiClient? storeReceiptProfileApiClient = null,
        Func<CancellationToken, Task>? resetTestSalesDataAsync = null,
        Func<Task<bool>>? confirmResetTestSalesDataAsync = null,
        ICardRecoveryResultDialogService? cardRecoveryResultDialogService = null,
        Func<CancellationToken, Task<AppUpdateCoordinatorResult>>? checkForAppUpdateAsync = null,
        string? appUpdateChannel = null,
        ICashierSessionContext? cashierSessionContext = null,
        bool enforcePermissionsWhenNoCashier = false,
        ApiServerSettingsViewModel? apiServerSettings = null,
        IOperationAuthorizationService? operationAuthorizationService = null,
        PosSessionState? session = null,
        IRemoteMaintenanceService? remoteMaintenanceService = null,
        Func<string, Task<bool>>? confirmLinklyTerminalAssignmentAsync = null,
        IPaymentMethodSettingsService? paymentMethodSettingsService = null,
        ICatalogSyncStatusService? catalogSyncStatusService = null,
        IOperationAuditLogger? operationAuditLogger = null,
        IReceiptProfileSyncService? receiptProfileSyncService = null)
    {
        _setupService = setupService;
        _receiptProfileSyncService = receiptProfileSyncService;
        _operationAuditLogger = operationAuditLogger;
        _paymentMethodSettingsService = paymentMethodSettingsService;
        _catalogSyncStatusService = catalogSyncStatusService;
        _localization = localization;
        _apiServerSettings = apiServerSettings;
        _downloadCatalogAsync = downloadCatalogAsync;
        _resetCatalogAsync = resetCatalogAsync;
        _resetTestSalesDataAsync = resetTestSalesDataAsync;
        _confirmResetTestSalesDataAsync = confirmResetTestSalesDataAsync;
        _confirmLinklyTerminalAssignmentAsync = confirmLinklyTerminalAssignmentAsync;
        _reregisterDeviceAsync = reregisterDeviceAsync;
        _checkForAppUpdateAsync = checkForAppUpdateAsync;
        _returnToPos = returnToPos;
        _receiptPrinterSettingsStore = receiptPrinterSettingsStore;
        _receiptPrintService = receiptPrintService;
        _storeReceiptProfileApiClient = storeReceiptProfileApiClient;
        _cardRecoveryResultDialogService = cardRecoveryResultDialogService;
        _cashierSessionContext = cashierSessionContext ?? new CashierSessionContext();
        _enforcePermissions = enforcePermissionsWhenNoCashier;
        _operationAuthorizationService = operationAuthorizationService;
        _remoteMaintenanceService = remoteMaintenanceService;
        Session = session ?? new PosSessionState("HB POS", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false, 0, _cashierSessionContext.CurrentSession);
        AppUpdateChannelText = string.IsNullOrWhiteSpace(appUpdateChannel)
            ? "production"
            : appUpdateChannel.Trim();
        _dataMaintenanceSection = new DataMaintenanceSection(new DataMaintenanceContext(
            IsBusy: () => IsBusy,
            DownloadCatalogAsync: _downloadCatalogAsync,
            ResetCatalogAsync: WithOperationAudit(_resetCatalogAsync, OperationAuditTypes.CatalogReset),
            ResetTestSalesDataAsync: WithOperationAudit(_resetTestSalesDataAsync, OperationAuditTypes.TestSalesDataReset),
            ConfirmResetTestSalesDataAsync: _confirmResetTestSalesDataAsync,
            ReregisterDeviceAsync: _reregisterDeviceAsync,
            CheckForAppUpdateAsync: _checkForAppUpdateAsync,
            RunBusyAsync: (action, operationName) => RunBusyAsync(action, operationName),
            SetStatus: (key, args) => SetStatus(key, args),
            SetStatusOverride: SetStatusOverride));
        _receiptPrinterSection = new ReceiptPrinterSection(new ReceiptPrinterContext(
            SettingsStore: _receiptPrinterSettingsStore,
            PrintService: _receiptPrintService,
            IsBusy: () => IsBusy,
            CreateSettingsFromFields: CreateReceiptPrinterSettingsFromFields,
            ApplySettings: ApplyReceiptPrinterSettings,
            RunBusyAsync: (action, operationName) => RunBusyAsync(action, operationName),
            SetStatus: key => SetStatus(key),
            SetStatusOverride: SetStatusOverride,
            ClearReceiptPrinterTestStatus: () => ReceiptPrinterTestStatusMessage = string.Empty,
            SetReceiptPrinterTestStatus: message =>
            {
                ReceiptPrinterTestStatusMessage = message;
                _receiptPrinterTestStatusOverride = message;
            }));
        _squareCoordinator = new SquareSettingsCoordinator(
            _setupService,
            () => SelectedSquareEnvironment,
            (action, name) => RunBusyAsync(action, name),
            (key, args) => SetStatus(key, args),
            SetStatusOverride,
            RaiseCommandStates);
        _linklyCoordinator = new LinklySettingsCoordinator(
            _setupService,
            _cardRecoveryResultDialogService,
            () => SelectedLinklyEnvironment,
            (action, name) => RunBusyAsync(action, name),
            (key, args) => SetStatus(key, args),
            SetStatusOverride,
            (key, args) => SetLinklyTestStatus(key, args),
            SetLinklyTestStatusOverride,
            ClearLinklyTestStatus,
            () => ResetLinklyConnectionTest(),
            RaiseCommandStates,
            T,
            () => PrimaryLinklyMode,
            item => SelectLinklyPriorityMode(item));
        if (_localization is not null)
        {
            _localization.CultureChanged += OnCultureChanged;
        }

        InitializeCatalogSyncStatus();
        InitializeReceiptProfileSync();

        SelectDataMaintenanceCommand = new RelayCommand(() => SelectedCategory = SettingsCategory.DataMaintenance);
        SelectPaymentTerminalCommand = new AsyncRelayCommand(() => SelectCategoryAsync(SettingsCategory.PaymentTerminal, Permissions.PosTerminal.Settings.PaymentTerminal));
        SelectReceiptPrinterCommand = new AsyncRelayCommand(() => SelectCategoryAsync(SettingsCategory.ReceiptPrinter, Permissions.PosTerminal.Settings.ReceiptPrinter));
        SelectDeviceRegistrationCommand = new AsyncRelayCommand(() => SelectCategoryAsync(SettingsCategory.DeviceRegistration, Permissions.PosTerminal.Settings.DeviceRegistration));
        SelectRemoteMaintenanceCommand = new AsyncRelayCommand(SelectRemoteMaintenanceAsync);
        LoadCommand = new AsyncRelayCommand(LoadAsync);
        SavePaymentMethodsCommand = new AsyncRelayCommand(SavePaymentMethodsAsync, CanSavePaymentMethods);
        LoadLocationsCommand = new AsyncRelayCommand(LoadLocationsAsync, CanLoadLocations);
        LoadDevicesCommand = new AsyncRelayCommand(LoadDevicesAsync, CanLoadDevices);
        SaveSquareCommand = new AsyncRelayCommand(SaveSquareAsync, CanSaveSquare);
        LoadDeviceCodesCommand = new AsyncRelayCommand(LoadDeviceCodesAsync, CanLoadDeviceCodes);
        CreateDeviceCodeCommand = new AsyncRelayCommand(CreateDeviceCodeAsync, CanCreateDeviceCode);
        RefreshDeviceCodeStatusCommand = new AsyncRelayCommand(RefreshDeviceCodeStatusAsync, CanRefreshDeviceCodeStatus);
        PairLinklyCloudCommand = new AsyncRelayCommand(PairLinklyCloudAsync, CanPairLinklyCloud);
        SaveLinklyCloudCredentialCommand = new AsyncRelayCommand(SaveLinklyCloudCredentialAsync, CanSaveLinklyCloudCredential);
        CancelLinklyCloudPairingCommand = new RelayCommand(CancelLinklyCloudPairing, CanCancelLinklyCloudPairing);
        OpenLinklySetupInstructionsCommand = new RelayCommand(() => IsLinklySetupInstructionsOpen = true);
        CloseLinklySetupInstructionsCommand = new RelayCommand(() => IsLinklySetupInstructionsOpen = false);
        TestLinklyCommand = new AsyncRelayCommand(TestLinklyAsync, CanTestLinkly);
        LogonLinklyCommand = new AsyncRelayCommand(LogonLinklyAsync, CanLogonLinkly);
        SaveLinklyCommand = new AsyncRelayCommand(SaveLinklyAsync, CanSaveLinkly);
        MoveLinklyPriorityUpCommand = new RelayCommand<LinklyModePriorityItem>(MoveLinklyPriorityUp, CanMoveLinklyPriorityUp);
        MoveLinklyPriorityDownCommand = new RelayCommand<LinklyModePriorityItem>(MoveLinklyPriorityDown, CanMoveLinklyPriorityDown);
        SelectLinklyPriorityModeCommand = new RelayCommand<LinklyModePriorityItem>(SelectLinklyPriorityMode);
        SaveReceiptPrinterCommand = new AsyncRelayCommand(SaveReceiptPrinterAsync, CanSaveReceiptPrinter);
        TestReceiptPrinterCommand = new AsyncRelayCommand(TestReceiptPrinterAsync, CanTestReceiptPrinter);
        LoadReceiptProfileCommand = new AsyncRelayCommand(LoadReceiptProfileAsync);
        DownloadCatalogCommand = new AsyncRelayCommand(DownloadCatalogAsync, CanDownloadCatalog);
        ResetCatalogCommand = new AsyncRelayCommand(ResetCatalogAsync, CanResetCatalog);
        ResetTestSalesDataCommand = new AsyncRelayCommand(ResetTestSalesDataAsync, CanResetTestSalesData);
        ReregisterDeviceCommand = new AsyncRelayCommand(ReregisterDeviceAsync, CanReregisterDevice);
        CheckForAppUpdateCommand = new AsyncRelayCommand(CheckForAppUpdateAsync, CanCheckForAppUpdate);
        InstallRemoteMaintenanceCommand = new AsyncRelayCommand(InstallRemoteMaintenanceAsync, CanInstallRemoteMaintenance);
        TestLinklyTransactionStatusCommand = new AsyncRelayCommand(TestLinklyTransactionStatusAsync, CanTestLinklyTransactionStatus);
        TestLinklyCloudTerminalCommand = new AsyncRelayCommand<LinklyCloudTerminalManagementItem>(TestLinklyCloudTerminalAsync, CanManageLinklyCloudTerminal);
        UseLinklyCloudTerminalCommand = new AsyncRelayCommand<LinklyCloudTerminalManagementItem>(UseLinklyCloudTerminalAsync, CanAssignLinklyCloudTerminal);
        ChangeLinklyCloudTerminalAssignmentCommand = new AsyncRelayCommand<LinklyCloudTerminalManagementItem>(ChangeLinklyCloudTerminalAssignmentAsync, CanAssignLinklyCloudTerminal);
        UnassignLinklyCloudTerminalCommand = new AsyncRelayCommand<LinklyCloudTerminalManagementItem>(UnassignLinklyCloudTerminalAsync, CanUnassignLinklyCloudTerminal);
        BackCommand = new RelayCommand(ReturnToPos, () => _returnToPos is not null);
        ResetLinklyModePriority(CardTerminalConfiguration.Default.LinklyConnectionModePriority);
        RefreshLocalizedMessages();
    }

    public ApiServerSettingsViewModel ApiServerSettings =>
        _apiServerSettings ?? throw new InvalidOperationException("API server settings are not configured.");

    public PosSessionState Session { get; set; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_localization is not null)
        {
            _localization.CultureChanged -= OnCultureChanged;
        }

        ReleaseCatalogSyncStatus();
        ReleaseReceiptProfileSync();
    }

    public ObservableCollection<SquareLocationOption> SquareLocations { get; } = [];

    public ObservableCollection<SquareDeviceOption> SquareDevices { get; } = [];

    public ObservableCollection<SquareDeviceCodeOption> SquareDeviceCodes { get; } = [];

    public ObservableCollection<LinklyModePriorityItem> LinklyModePriorityItems { get; } = [];

    public ObservableCollection<LinklyCloudTerminalSummary> LinklyCloudTerminals { get; } = [];

    public ObservableCollection<LinklyCloudTerminalManagementItem> LinklyCloudTerminalItems { get; } = [];

    public ObservableCollection<LinklyCloudLineItem> LinklyCloudLines { get; } = [];

    public IReadOnlyList<LinklyCloudAssignableDevice> LinklyCloudDevices { get; private set; } = [];

    public bool IsLinklyCloudLineManagementAvailable { get; private set; }

    public bool IsLinklyCloudLineManagementUnavailable => !IsLinklyCloudLineManagementAvailable;

    public bool CanRefreshLinklyCloudTerminals => !IsBusy && !_isLinklyLineOperationBusy;

    public string LinklyCloudLineManagementMessage => IsLinklyCloudLineManagementAvailable
        ? string.Empty
        : T("settings.linkly.cloudBackend.managementUnavailable");

    public long? LinklyCloudSelectionRevision { get; private set; }

    public bool HasLinklyCloudTerminals => LinklyCloudTerminals.Count > 0;

    public string SelectedLinklyCloudTerminalText => SelectedLinklyCloudTerminal is null
        ? T("settings.linkly.cloudBackend.noTerminal")
        : string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            T("settings.linkly.cloudBackend.terminalDisplay"),
            SelectedLinklyCloudTerminal.LaneNo,
            SelectedLinklyCloudTerminal.DisplayName);

    public void RaiseLinklyCloudPasswordInputChanged(bool hasPassword)
    {
        _hasLinklyCloudPasswordInput = hasPassword;
        Interlocked.Increment(ref _linklyCredentialEditVersion);
        ResetLinklyConnectionTest();
        RaiseCommandStates();
    }

    public IAsyncRelayCommand LoadCommand { get; }

    public IAsyncRelayCommand LoadLocationsCommand { get; }

    public IAsyncRelayCommand LoadDevicesCommand { get; }

    public IAsyncRelayCommand SaveSquareCommand { get; }

    public IAsyncRelayCommand LoadDeviceCodesCommand { get; }

    public IAsyncRelayCommand CreateDeviceCodeCommand { get; }

    public IAsyncRelayCommand RefreshDeviceCodeStatusCommand { get; }

    public IAsyncRelayCommand PairLinklyCloudCommand { get; }

    public IAsyncRelayCommand SaveLinklyCloudCredentialCommand { get; }

    public IRelayCommand CancelLinklyCloudPairingCommand { get; }

    public IRelayCommand OpenLinklySetupInstructionsCommand { get; }

    public IRelayCommand CloseLinklySetupInstructionsCommand { get; }

    public IAsyncRelayCommand TestLinklyCommand { get; }

    public IAsyncRelayCommand LogonLinklyCommand { get; }

    public IAsyncRelayCommand TestLinklyTransactionStatusCommand { get; }

    public IAsyncRelayCommand<LinklyCloudTerminalManagementItem> TestLinklyCloudTerminalCommand { get; }

    public IAsyncRelayCommand<LinklyCloudTerminalManagementItem> UseLinklyCloudTerminalCommand { get; }

    public IAsyncRelayCommand<LinklyCloudTerminalManagementItem> ChangeLinklyCloudTerminalAssignmentCommand { get; }

    public IAsyncRelayCommand<LinklyCloudTerminalManagementItem> UnassignLinklyCloudTerminalCommand { get; }

    public IAsyncRelayCommand SaveLinklyCommand { get; }

    public IRelayCommand<LinklyModePriorityItem> MoveLinklyPriorityUpCommand { get; }

    public IRelayCommand<LinklyModePriorityItem> MoveLinklyPriorityDownCommand { get; }

    public IRelayCommand<LinklyModePriorityItem> SelectLinklyPriorityModeCommand { get; }

    public IRelayCommand SelectDataMaintenanceCommand { get; }

    public IRelayCommand SelectPaymentTerminalCommand { get; }

    public IRelayCommand SelectReceiptPrinterCommand { get; }

    public IRelayCommand SelectDeviceRegistrationCommand { get; }

    public IAsyncRelayCommand SelectRemoteMaintenanceCommand { get; }

    public IAsyncRelayCommand SaveReceiptPrinterCommand { get; }

    public IAsyncRelayCommand TestReceiptPrinterCommand { get; }

    public IAsyncRelayCommand LoadReceiptProfileCommand { get; }

    public IAsyncRelayCommand DownloadCatalogCommand { get; }

    public IAsyncRelayCommand ResetCatalogCommand { get; }

    public IAsyncRelayCommand ResetTestSalesDataCommand { get; }

    public IAsyncRelayCommand ReregisterDeviceCommand { get; }

    public IAsyncRelayCommand CheckForAppUpdateCommand { get; }

    public IAsyncRelayCommand InstallRemoteMaintenanceCommand { get; }

    public IRelayCommand BackCommand { get; }

    public string ScreenTitleText => T("settings.title");

    public string SettingsSubtitleText => SelectedCategory switch
    {
        SettingsCategory.DataMaintenance => T("settings.subtitle.dataMaintenance"),
        SettingsCategory.PaymentTerminal => T("settings.subtitle.paymentTerminal"),
        SettingsCategory.ReceiptPrinter => T("settings.subtitle.receiptPrinter"),
        SettingsCategory.DeviceRegistration => T("settings.subtitle.deviceRegistration"),
        SettingsCategory.RemoteMaintenance => RemoteMaintenanceTitleText,
        _ => T("settings.title")
    };

    public string CardTerminalTitleText => T("settings.subtitle.paymentTerminal");

    public string DataMaintenanceTitleText => T("settings.category.dataMaintenance");

    public string DeviceRegistrationTitleText => T("settings.category.deviceRegistration");

    public string SquareTitleText => T("settings.square.title");

    public string LinklyTitleText => T("settings.linkly.title");

    public string ActivePaymentProviderText => _paymentMethodSettingsService?.Current.UseManualCard == true
        ? T("settings.payment.activeProvider.manual")
        : _loadedConfiguration.Processor == CardProcessorKind.Linkly
        ? T("settings.payment.activeProvider.linkly")
        : T("settings.payment.activeProvider.square");

    public string ActivePaymentProviderDetailText => _paymentMethodSettingsService?.Current.UseManualCard == true
        ? T("settings.payment.activeProvider.manual.detail")
        : _loadedConfiguration.Processor == CardProcessorKind.Linkly
        ? Format(
            "settings.payment.activeProvider.linkly.detail",
            T(GetLinklyModeLocalizationKey(ToSettingsMode(_loadedConfiguration.LinklyConnectionMode))))
        : T("settings.payment.activeProvider.square.detail");

    public string ReceiptPrinterTitleText => T("settings.receiptPrinter.title");

    public bool IsDataMaintenanceSelected => SelectedCategory == SettingsCategory.DataMaintenance;

    public bool IsPaymentTerminalSelected => SelectedCategory == SettingsCategory.PaymentTerminal;

    public bool IsReceiptPrinterSelected => SelectedCategory == SettingsCategory.ReceiptPrinter;

    public bool IsDeviceRegistrationSelected => SelectedCategory == SettingsCategory.DeviceRegistration;

    public bool IsRemoteMaintenanceSelected => SelectedCategory == SettingsCategory.RemoteMaintenance;

    public bool IsRemoteMaintenanceConfigured => _remoteMaintenanceStatus?.IsConfigured == true;

    private string RemoteMaintenanceStatusKey => (_remoteMaintenanceStatus?.ServiceStatus ?? "notInstalled") switch
    {
        "notInstalled" => "notInstalled",
        "running" => "running",
        "starting" => "starting",
        "stopping" => "stopping",
        "stopped" => "stopped",
        "checkFailed" => "checkFailed",
        _ => "unknown"
    };

    public string RemoteMaintenanceStatusText => T("settings.remoteMaintenance.status." + RemoteMaintenanceStatusKey);

    // 服务返回的诊断文本不直接展示，避免切换语言后残留中文或泄露内部路径。
    public string RemoteMaintenanceDetailText => T("settings.remoteMaintenance.detail." +
        (_remoteMaintenanceService is null && _remoteMaintenanceStatus is not null ? "serviceUnavailable" : RemoteMaintenanceStatusKey));

    public string RemoteMaintenanceTitleText => T("settings.remoteMaintenance.title");

    public string RemoteMaintenanceDescriptionText => T("settings.remoteMaintenance.description");

    public string RemoteMaintenanceActionText => T(IsRemoteMaintenanceConfigured
        ? "settings.remoteMaintenance.restore" : "settings.remoteMaintenance.install");

    private RemoteMaintenanceStatus? _remoteMaintenanceStatus;

    private string? _remoteMaintenanceProgressKey;
    private bool _isRemoteMaintenanceInstalling;

    public string RemoteMaintenanceProgressText => _remoteMaintenanceProgressKey is null
        ? string.Empty : T(_remoteMaintenanceProgressKey);

    public bool HasRemoteMaintenanceProgress => _remoteMaintenanceProgressKey is not null;

    public bool IsRemoteMaintenanceInstalling
    {
        get => _isRemoteMaintenanceInstalling;
        private set => SetProperty(ref _isRemoteMaintenanceInstalling, value);
    }

    public bool IsDebugTestSalesDataResetVisible
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }

    public string SquareTokenStatusText => HasSavedSquareToken
        ? T("settings.square.tokenStatus.cached")
        : T("settings.square.tokenStatus.missing");

    public bool IsSquareDeviceCodesSupported => !IsSquareSandbox;

    public bool IsSquareDeviceCodesUnsupported => !IsSquareDeviceCodesSupported;

    public string SquareDeviceCodesUnavailableText => T("settings.square.deviceCodes.unsupported");

    public bool CanChangeEnvironment => !IsBusy && !_isLinklyLineOperationBusy;

    public LinklySettingsMode PrimaryLinklyMode => LinklyModePriorityItems.Count == 0
        ? SelectedLinklyMode
        : LinklyModePriorityItems[0].Mode;

    public bool IsLinklyCloudMode
    {
        get => SelectedLinklyMode is LinklySettingsMode.CloudDirectSync or LinklySettingsMode.CloudBackendAsync;
        set => SelectedLinklyMode = value ? LinklySettingsMode.CloudDirectSync : LinklySettingsMode.LocalIp;
    }

    public bool IsLinklyLocalMode => IsLinklyLocalIpMode;

    public bool IsLinklyLocalIpMode
    {
        get => SelectedLinklyMode == LinklySettingsMode.LocalIp;
        set
        {
            if (value)
            {
                SelectedLinklyMode = LinklySettingsMode.LocalIp;
            }
        }
    }

    public bool IsLinklyCloudDirectSyncMode
    {
        get => SelectedLinklyMode == LinklySettingsMode.CloudDirectSync;
        set
        {
            if (value)
            {
                SelectedLinklyMode = LinklySettingsMode.CloudDirectSync;
            }
        }
    }

    public bool IsLinklyCloudBackendAsyncMode
    {
        get => SelectedLinklyMode == LinklySettingsMode.CloudBackendAsync;
        set
        {
            if (value)
            {
                SelectedLinklyMode = LinklySettingsMode.CloudBackendAsync;
            }
        }
    }

    public bool IsLinklyStandardActionMode => !IsLinklyCloudBackendAsyncMode;

    public bool IsLinklySandboxLocalIpMode =>
        PrimaryLinklyMode == LinklySettingsMode.LocalIp && IsLinklySandbox;

    public string LinklyTestActionText => IsLinklySandboxLocalIpMode
        ? T("settings.linkly.localIp.testVirtualTerminal")
        : T("settings.linkly.test");

    public string LinklyCloudSecretStatusText => HasSavedLinklyCloudSecret
        ? T("settings.linkly.cloud.secretStatus.cached")
        : T("settings.linkly.cloud.secretStatus.missing");

    public string LinklyCloudCredentialStatusText => HasSavedLinklyCloudPassword
        ? T("settings.linkly.cloud.credentialStatus.cached")
        : T("settings.linkly.cloud.credentialStatus.missing");

    public bool CanSaveLinklyCloudCredentialFromView =>
        CanSaveLinklyCloudCredential() && _hasLinklyCloudPasswordInput;

    public bool CanPairLinklyCloudFromView => CanPairLinklyCloud();

    public bool CanCancelLinklyCloudPairingFromView => CanCancelLinklyCloudPairing();

    public CardTerminalEnvironment SelectedSquareEnvironment => IsSquareSandbox
        ? CardTerminalEnvironment.Sandbox
        : CardTerminalEnvironment.Production;

    public CardTerminalEnvironment SelectedLinklyEnvironment => IsLinklySandbox
        ? CardTerminalEnvironment.Sandbox
        : CardTerminalEnvironment.Production;

    public CardTerminalEnvironment SelectedEnvironment => SelectedLinklyEnvironment;

    public async Task LoadAsync()
    {
        // 设置页与设备注册页共享同一个单例；重复进入时由共享 VM 保留等待重启状态。
        _apiServerSettings?.Load();
        await RunBusyAsync(async () =>
        {
            await LoadPaymentMethodsAsync();
            _loadedConfiguration = await _setupService.LoadConfigurationAsync();
            await LoadReceiptPrinterSettingsAsync();
            IsSquareSandbox = _loadedConfiguration.Environment == CardTerminalEnvironment.Sandbox;
            IsLinklySandbox = _loadedConfiguration.Environment == CardTerminalEnvironment.Sandbox;
            LinklyHostText = _loadedConfiguration.LinklyHost;
            LinklyPortText = _loadedConfiguration.LinklyPort.ToString();
            ResetLinklyModePriority(_loadedConfiguration.LinklyConnectionModePriority);
            SelectedLinklyMode = ToSettingsMode(_loadedConfiguration.LinklyConnectionMode);
            if (IsLinklyCloudBackendAsyncMode)
            {
                // Backend Async 的账号、密码与 Secret 只由 Web 管理；POS 仅读取安全终端目录。
                ClearLinklyCloudCredentialFields();
                HasSavedLinklyCloudSecret = false;
                await RefreshLinklyCloudBackendTerminalsCoreAsync(SelectedLinklyEnvironment);
            }
            else
            {
                await LoadLinklyCloudCredentialFieldsAsync(SelectedLinklyEnvironment);
                HasSavedLinklyCloudSecret = _loadedConfiguration.HasProtectedLinklyCloudSecret;
                ClearLinklyCloudTerminalDirectory();
            }
            LinklyPairCodeText = string.Empty;
            TimeoutSecondsText = _loadedConfiguration.TerminalTimeoutSeconds.ToString();
            HasSavedSquareToken = _loadedConfiguration.HasProtectedSquareAccessToken;
            _savedSquareLocationId = _loadedConfiguration.SquareLocationId;
            _savedSquareDeviceId = _loadedConfiguration.SquareDeviceId;
            _devicesLoadedForLocationId = null;
            LinklyConnectionSucceeded = false;
            LinklyPinPadLoggedOn = null;
            ClearLinklyTestStatus();
            SquareLocations.Clear();
            SquareDevices.Clear();
            ResetSquareDeviceCodes();
            SelectedSquareLocation = null;
            SelectedSquareDevice = null;
            SyncSquareInputs();
            SyncLinklyInputs();
            LogSquareSettings(
                $"load settings succeeded squareEnvironment={SelectedSquareEnvironment} linklyEnvironment={SelectedLinklyEnvironment} hasSavedToken={HasSavedSquareToken} savedLocationId={LogValue(_savedSquareLocationId)} savedDeviceId={LogValue(_savedSquareDeviceId)}");
            RaiseActivePaymentProviderProperties();
            SetStatus("settings.status.loaded");
        }, operationName: "load settings");
        await LoadCatalogSyncStatusAsync();
    }

    private async Task LoadLocationsAsync()
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Settings.PaymentTerminal, "load-locations");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        await _squareCoordinator.LoadLocationsAsync(SquareLocations, SquareDevices, SquareDeviceCodes, _squareState);
        SyncSquareState();
    }

    private async Task LoadDevicesAsync()
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Settings.PaymentTerminal, "load-devices");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        await _squareCoordinator.LoadDevicesAsync(SquareDevices, _squareState);
        SyncSquareState();
    }

    private async Task SaveSquareAsync()
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Settings.PaymentTerminal, "save-square");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        await _squareCoordinator.SaveAsync(SquareLocations, SquareDevices, _squareState, LinklyHostText, LinklyPortText, TimeoutSecondsText);
        SyncSquareState();
    }

    private async Task LoadDeviceCodesAsync()
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Settings.PaymentTerminal, "load-device-codes");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        await _squareCoordinator.LoadDeviceCodesAsync(SquareDeviceCodes, _squareState);
        SyncSquareState();
    }

    private async Task CreateDeviceCodeAsync()
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Settings.PaymentTerminal, "create-device-code");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        await _squareCoordinator.CreateDeviceCodeAsync(SquareDeviceCodes, _squareState);
        SyncSquareState();
    }

    private async Task RefreshDeviceCodeStatusAsync()
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Settings.PaymentTerminal, "refresh-device-code-status");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        await _squareCoordinator.RefreshDeviceCodeStatusAsync(SquareDevices, SquareDeviceCodes, _squareState);
        SyncSquareState();
    }

    private async Task TestLinklyAsync()
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Settings.PaymentTerminal, "test-linkly");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        if (PrimaryLinklyMode == LinklySettingsMode.LocalIp)
        {
            if (string.IsNullOrWhiteSpace(LinklyHostText))
            {
                SetLinklyTestValidationStatus("settings.linkly.localIp.hostRequired");
                return;
            }

            if (!int.TryParse(LinklyPortText, out var port) || port is <= 0 or > 65535)
            {
                SetLinklyTestValidationStatus("settings.linkly.localIp.portInvalid");
                return;
            }

            if (!int.TryParse(TimeoutSecondsText, out var timeoutSeconds) || timeoutSeconds <= 0)
            {
                SetLinklyTestValidationStatus("settings.linkly.localIp.timeoutInvalid");
                return;
            }
        }

        var testedInputs = CaptureLinklyTestInputs();
        SyncLinklyInputs();
        await _linklyCoordinator.TestAsync(_linklyState);
        var inputsChangedDuringTest = testedInputs != CaptureLinklyTestInputs();
        SyncLinklyState();
        if (inputsChangedDuringTest)
        {
            ResetLinklyConnectionTest();
            SetLinklyTestValidationStatus("settings.linkly.test.configurationChanged");
        }
    }

    private async Task LogonLinklyAsync()
    {
        using var permissionGrant = await AuthorizeAsync(
            Permissions.PosTerminal.Settings.PaymentTerminal,
            "logon-linkly");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        if (string.IsNullOrWhiteSpace(LinklyHostText))
        {
            SetLinklyTestValidationStatus("settings.linkly.localIp.hostRequired");
            return;
        }

        if (!int.TryParse(LinklyPortText, out var port) || port is <= 0 or > 65535)
        {
            SetLinklyTestValidationStatus("settings.linkly.localIp.portInvalid");
            return;
        }

        if (!int.TryParse(TimeoutSecondsText, out var timeoutSeconds) || timeoutSeconds <= 0)
        {
            SetLinklyTestValidationStatus("settings.linkly.localIp.timeoutInvalid");
            return;
        }

        var testedInputs = CaptureLinklyTestInputs();
        SyncLinklyInputs();
        await _linklyCoordinator.LogonAsync(_linklyState);
        var inputsChangedDuringLogon = testedInputs != CaptureLinklyTestInputs();
        SyncLinklyState();
        if (inputsChangedDuringLogon)
        {
            ResetLinklyConnectionTest();
            SetLinklyTestValidationStatus("settings.linkly.test.configurationChanged");
        }
    }

    private void ShowFailedLastTransactionDialogIfNeeded(LinklyConnectionTestResult result)
    {
        if (result.Succeeded ||
            result.StatusTest is not { } status ||
            !IsFailedLastTransactionStatus(status, result.Message))
        {
            return;
        }

        _cardRecoveryResultDialogService?.Show(new CardRecoveryResultDialogViewModel(
            T("cardRecovery.dialog.title.failedLastTransaction"),
            T("cardRecovery.dialog.message.failedLastTransaction"),
            CardRecoveryResultSeverity.Warning,
            orderGuid: null,
            amount: null,
            sessionId: status.TransactionReference,
            txnRef: status.TxnRef,
            responseCode: status.ResponseCode,
            responseText: status.ResponseText ?? result.Message,
            timestamp: status.Timestamp ?? DateTimeOffset.Now));
    }

    private static bool IsFailedLastTransactionStatus(LinklyStatusTestDetails status, string? message)
    {
        var code = status.ResponseCode?.Trim();
        if (string.Equals(code, "00", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(code, "T0", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ContainsFailureText(status.ResponseText) ||
            ContainsFailureText(message);
    }

    private static bool ContainsFailureText(string? text)
    {
        return !string.IsNullOrWhiteSpace(text) &&
            (text.Contains("DECLINED", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("CANCELLED", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("FAILED", StringComparison.OrdinalIgnoreCase));
    }

    private async Task TestLinklyTransactionStatusAsync()
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Settings.PaymentTerminal, "test-linkly-transaction");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        SyncLinklyInputs();
        await _linklyCoordinator.TestTransactionStatusAsync(_linklyState);
        SyncLinklyState();
    }

    private async Task PairLinklyCloudAsync()
    {
        await PairLinklyCloudAsync(LinklyCloudPasswordText);
    }

    public async Task PairLinklyCloudAsync(string password)
    {
        var passwordSnapshot = password;
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Settings.PaymentTerminal, "pair-linkly-cloud");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        password = passwordSnapshot;
        SyncLinklyInputs();
        if (IsLinklyCloudBackendAsyncMode)
        {
            await PairSelectedLinklyCloudBackendTerminalAsync();
            return;
        }

        await _linklyCoordinator.PairCloudAsync(password, _linklyState);
        SyncLinklyState();
    }

    public async Task RefreshLinklyCloudBackendTerminalsAsync()
    {
        // 公共入口同样拒绝并发刷新，防止代码调用绕过按钮禁用并覆盖在途分配结果。
        if (!CanRefreshLinklyCloudTerminals)
        {
            return;
        }

        if (!IsLinklyCloudBackendAsyncMode)
        {
            ClearLinklyCloudTerminalDirectory();
            return;
        }

        var environment = SelectedLinklyEnvironment;
        var requestGeneration = Interlocked.Increment(ref _linklyTerminalDirectoryRequestGeneration);
        await RunBusyAsync(
            () => RefreshLinklyCloudBackendTerminalsCoreAsync(environment, requestGeneration),
            operationName: "refresh linkly cloud terminal directory");
    }

    public async Task SelectLinklyCloudBackendTerminalAsync(LinklyCloudTerminalSummary? terminal)
    {
        if (terminal is null || !IsLinklyCloudBackendAsyncMode)
        {
            return;
        }

        SelectedLinklyCloudTerminal = terminal;
        if (IsLinklyCloudLineManagementAvailable)
        {
            // 新服务端由每行“使用此线路/变更绑定”显式提交；下拉框仅选择配对目标。
            return;
        }
        if (terminal.TerminalId == _persistedLinklyCloudTerminalId)
        {
            return;
        }

        if (!terminal.IsReady)
        {
            // 未配对终端只是本地配对目标，不能提前写成付款终端；配对成功后再持久选择。
            SetLinklyTestValidationStatus("settings.linkly.cloudBackend.pairBeforeSelect");
            return;
        }

        using var permissionGrant = await AuthorizeAsync(
            Permissions.PosTerminal.Settings.PaymentTerminal,
            "select-linkly-cloud-terminal");
        if (permissionGrant is null)
        {
            RestorePersistedLinklyCloudSelection();
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        var environment = SelectedLinklyEnvironment;
        await RunBusyAsync(async () =>
        {
            try
            {
                var selection = await _setupService.SelectLinklyCloudBackendTerminalAsync(
                    environment,
                    terminal.TerminalId,
                    LinklyCloudSelectionRevision);
                if (selection.TerminalId != terminal.TerminalId ||
                    !string.Equals(selection.Environment, environment.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Linkly terminal selection response identity did not match the requested line.");
                }
                ApplyPersistedLinklyCloudSelection(selection.TerminalId, selection.Revision);
                SetStatusOverride(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    T("settings.linkly.cloudBackend.selected"),
                    terminal.DisplayName));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                ResetLinklyConnectionTest();
                try
                {
                    // PUT 结果未知时不重放写入，只读一次权威目录决定实际付款线路。
                    await RefreshLinklyCloudBackendTerminalsCoreAsync(environment);
                    SetStatus("settings.linkly.cloudBackend.selectionReconciled");
                }
                catch (Exception refreshException) when (refreshException is not OutOfMemoryException and not StackOverflowException)
                {
                    RestorePersistedLinklyCloudSelection();
                    SetStatus("settings.linkly.cloudBackend.selectionReconcileFailed");
                    LogLinklyCloudSettings(
                        $"select line reconciliation failed selectError={ex.GetType().Name} refreshError={refreshException.GetType().Name}");
                }
            }
        }, operationName: "select linkly cloud terminal");
    }

    private LinklyCloudLineItem CreateLinklyCloudLine(
        LinklyCloudTerminalSummary terminal,
        Guid? selectedTerminalId,
        LinklyCloudTerminalManagementItem managementItem)
    {
        var item = new LinklyCloudLineItem(
            terminal,
            T,
            () => IsBusy || _isLinklyLineOperationBusy,
            ToggleLinklyLinePairing,
            line => line.BeginConfirmation(SelectedLinklyEnvironment),
            line => line.BackToCodeEntry(),
            line => line.CloseAndClear(),
            ConfirmLinklyLinePairingAsync,
            TestLinklyLineConnectionAsync,
            UseLinklyCloudLineForPaymentsAsync,
            managementItem,
            IsLinklyCloudLineManagementAvailable);
        item.IsSelected = terminal.TerminalId == selectedTerminalId;
        ApplyInitialLinklyLineConnectionStatus(item, terminal.LastHealthStatus);
        return item;
    }

    private Task UseLinklyCloudLineForPaymentsAsync(LinklyCloudLineItem line)
    {
        if (IsLinklyCloudLineManagementAvailable && line.ManagementItem is { } managementItem)
        {
            // 新目录合同必须经服务器分配、CAS 和付款线路切换；配对动作本身仍不会隐式分配。
            return UseLinklyCloudTerminalAsync(managementItem);
        }

        return SelectLinklyCloudBackendTerminalAsync(line.Terminal);
    }

    private void ToggleLinklyLinePairing(LinklyCloudLineItem item)
    {
        if (item.IsExpanded)
        {
            item.CloseAndClear();
            return;
        }

        CloseLinklyLinePairing();
        item.OpenForCodeEntry();
    }

    public void CloseLinklyLinePairing()
    {
        foreach (var line in LinklyCloudLines)
        {
            line.CloseAndClear();
        }
    }

    private async Task ConfirmLinklyLinePairingAsync(LinklyCloudLineItem item)
    {
        if (IsBusy || _isLinklyLineOperationBusy || item.PairingTarget is not { } target)
        {
            return;
        }

        SetLinklyLineOperationBusy(true);
        item.IsOperationBusy = true;
        try
        {
            using var permissionGrant = await AuthorizeAsync(
                Permissions.PosTerminal.Settings.PaymentTerminal,
                "pair-linkly-cloud-line");
            if (permissionGrant is null || !IsCurrentLinklyPairingTarget(item, target))
            {
                return;
            }

            using var authorizationActivation = permissionGrant.Activate();
            // 授权等待后重新读取权威目录，确保确认页锁定的线路、版本和设备归属仍然有效。
            LinklyCloudTerminalListResponse directory;
            try
            {
                directory = await _setupService.ListLinklyCloudBackendTerminalsAsync(target.Environment);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                item.CloseAndClear();
                SetStatus("settings.linkly.lines.preflightFailed");
                return;
            }
            var latest = directory.Terminals.FirstOrDefault(line => line.TerminalId == target.TerminalId);
            if (!IsCurrentLinklyPairingTarget(item, target) || latest is null ||
                !string.Equals(directory.Environment, target.Environment.ToString(), StringComparison.OrdinalIgnoreCase) ||
                !MatchesLinklyPairingTarget(latest, target))
            {
                item.CloseAndClear();
                SetStatus("settings.linkly.lines.targetChanged");
                return;
            }

            // 一次性配对码在 POST 前清空；配对仅更新该线路，不隐式切换付款线路。
            item.PairCode = string.Empty;
            var result = await _setupService.PairLinklyCloudBackendTerminalAsync(
                target.Environment,
                target.TerminalId,
                target.PairCode);
            if (result.TerminalId != target.TerminalId ||
                !string.Equals(result.Environment, target.Environment.ToString(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(result.DisplayName, target.DisplayName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Linkly terminal pairing response identity did not match the confirmed line.");
            }

            if (IsCurrentLinklyPairingTarget(item, target))
            {
                SetStatus(result.IsReady
                    ? "settings.linkly.lines.pairingSucceeded"
                    : "settings.linkly.lines.pairingRejected",
                    target.DisplayName);
            }
            item.CloseAndClear();
            await RefreshLinklyCloudBackendTerminalsCoreAsync(target.Environment);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            LogLinklyCloudSettings($"pair line failed terminalId={target.TerminalId:D} error={ex.GetType().Name}");
            if (TryGetDefinitiveLinklyPairFailureKey(ex, out var failureKey))
            {
                item.SetConnectionStatus("settings.linkly.lines.connection.notTested", failureKey);
                SetStatus(failureKey);
            }
            else
            {
                item.SetConnectionStatus(
                    "settings.linkly.lines.connection.unknown",
                    "settings.linkly.lines.connection.help.checkTerminal");
                SetStatus("settings.linkly.lines.pairingUnconfirmed", target.DisplayName);
            }
            item.CloseAndClear();
        }
        finally
        {
            item.IsOperationBusy = false;
            SetLinklyLineOperationBusy(false);
        }
    }

    private async Task TestLinklyLineConnectionAsync(LinklyCloudLineItem item)
    {
        if (IsBusy || _isLinklyLineOperationBusy || !IsLinklyCloudBackendAsyncMode || !item.Terminal.IsReady)
        {
            return;
        }

        var environment = SelectedLinklyEnvironment;
        var terminal = item.Terminal;
        SetLinklyLineOperationBusy(true);
        item.IsOperationBusy = true;
        try
        {
            using var permissionGrant = await AuthorizeAsync(
                Permissions.PosTerminal.Settings.PaymentTerminal,
                "test-linkly-cloud-line");
            if (permissionGrant is null || environment != SelectedLinklyEnvironment || !LinklyCloudLines.Contains(item))
            {
                return;
            }

            using var authorizationActivation = permissionGrant.Activate();
            if (item.IsSelected && _persistedLinklyCloudTerminalId == terminal.TerminalId)
            {
                ResetLinklyConnectionTest();
            }

            var result = await _setupService.TestLinklyCloudBackendTerminalAsync(environment, terminal);
            if (!IsLinklyCloudBackendAsyncMode || environment != SelectedLinklyEnvironment || !LinklyCloudLines.Contains(item))
            {
                return;
            }
            if (result.TerminalId != terminal.TerminalId ||
                !string.Equals(result.Environment, environment.ToString(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(result.TerminalVersion, terminal.TerminalVersion, StringComparison.Ordinal) ||
                !string.Equals(result.AssignedDeviceCode, terminal.AssignedDeviceCode, StringComparison.OrdinalIgnoreCase) ||
                result.AssignmentRevision != terminal.AssignmentRevision)
            {
                item.SetConnectionStatus(
                    "settings.linkly.lines.connection.unknown",
                    "settings.linkly.lines.connection.help.checkTerminal");
                return;
            }

            var status = result.Status?.Trim().ToLowerInvariant();
            var connectionStatusKey = result.Succeeded && status == "connected"
                ? "settings.linkly.lines.connection.connected"
                : status == "unknown"
                    ? "settings.linkly.lines.connection.unknown"
                    : "settings.linkly.lines.connection.failed";
            item.SetConnectionStatus(
                connectionStatusKey,
                connectionStatusKey == "settings.linkly.lines.connection.connected"
                    ? null
                    : GetLinklyLineConnectionHelpKey(status));
            if (item.IsSelected && _persistedLinklyCloudTerminalId == terminal.TerminalId)
            {
                var connected = connectionStatusKey == "settings.linkly.lines.connection.connected";
                LinklyConnectionSucceeded = connected;
                _linklyState.ConnectionSucceeded = connected;
                RaiseCommandStates();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            LogLinklyCloudSettings($"test line failed terminalId={terminal.TerminalId:D} error={ex.GetType().Name}");
            item.SetConnectionStatus(
                "settings.linkly.lines.connection.unknown",
                TryGetDefinitiveLinklyPairFailureKey(ex, out var failureKey)
                    ? failureKey
                    : "settings.linkly.lines.connection.help.checkNetwork");
            if (item.IsSelected && _persistedLinklyCloudTerminalId == terminal.TerminalId)
            {
                LinklyConnectionSucceeded = false;
                _linklyState.ConnectionSucceeded = false;
                RaiseCommandStates();
            }
        }
        finally
        {
            item.IsOperationBusy = false;
            SetLinklyLineOperationBusy(false);
        }
    }

    private void SetLinklyLineOperationBusy(bool value)
    {
        if (_isLinklyLineOperationBusy == value)
        {
            return;
        }

        // 配对和检测持有同一目录租约；期间所有会替换或修改线路快照的入口必须同步禁用。
        _isLinklyLineOperationBusy = value;
        OnPropertyChanged(nameof(CanChangeEnvironment));
        OnPropertyChanged(nameof(CanRefreshLinklyCloudTerminals));
        RaiseCommandStates();
        foreach (var line in LinklyCloudLines)
        {
            line.RefreshCommandStates();
        }
    }

    private bool IsCurrentLinklyPairingTarget(LinklyCloudLineItem item, PairingTargetSnapshot target) =>
        IsLinklyCloudBackendAsyncMode &&
        SelectedLinklyEnvironment == target.Environment &&
        LinklyCloudLines.Contains(item) &&
        item.IsConfirming &&
        item.PairingTarget == target &&
        MatchesLinklyPairingTarget(item.Terminal, target);

    private static bool MatchesLinklyPairingTarget(LinklyCloudTerminalSummary terminal, PairingTargetSnapshot target) =>
        terminal.TerminalId == target.TerminalId && terminal.LaneNo == target.LaneNo &&
        string.Equals(terminal.DisplayName, target.DisplayName, StringComparison.Ordinal) &&
        string.Equals(terminal.TerminalVersion, target.TerminalVersion, StringComparison.Ordinal) &&
        string.Equals(terminal.AssignedDeviceCode, target.AssignedDeviceCode, StringComparison.OrdinalIgnoreCase) &&
        terminal.AssignmentRevision == target.AssignmentRevision;

    private static bool TryGetDefinitiveLinklyPairFailureKey(Exception exception, out string key)
    {
        key = exception is LinklyBackendHttpException backendException
            ? backendException.ErrorCode switch
            {
                "LINKLY_CLOUD_BACKEND_PAIR_REJECTED" => "settings.linkly.cloudBackend.pairRejected",
                "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT" => "settings.linkly.cloudBackend.selectionChanged",
                "LINKLY_CLOUD_BACKEND_ACTIVE_TRANSACTION" or "LINKLY_CLOUD_TERMINAL_SESSION_ACTIVE" =>
                    "settings.linkly.cloudBackend.operationBlocked",
                "LINKLY_CLOUD_BACKEND_PAIR_IN_PROGRESS" or "LINKLY_CLOUD_TERMINAL_PAIRING_CONFLICT" =>
                    "settings.linkly.cloudBackend.pairInProgress",
                _ => string.Empty
            }
            : string.Empty;
        return key.Length > 0;
    }

    private static string GetLinklyLineConnectionHelpKey(string? status) => status switch
    {
        "needs-repair" or "needsrepair" => "settings.linkly.lines.connection.help.newPairCode",
        "busy" or "in-progress" => "settings.linkly.lines.connection.help.wait",
        "not-ready" or "unpaired" => "settings.linkly.lines.connection.help.pairFirst",
        "unknown" => "settings.linkly.lines.connection.help.checkNetwork",
        _ => "settings.linkly.lines.connection.help.checkTerminal"
    };

    private static void ApplyInitialLinklyLineConnectionStatus(LinklyCloudLineItem item, string? status)
    {
        if (item.Terminal.IsReady && string.IsNullOrWhiteSpace(item.Terminal.TerminalVersion))
        {
            item.SetConnectionStatus("settings.linkly.lines.connection.unknown", "settings.linkly.lines.refreshRequired");
            return;
        }

        item.SetConnectionStatus(status?.Trim().ToLowerInvariant() switch
        {
            "connected" or "healthy" => "settings.linkly.lines.connection.connected",
            "unhealthy" or "needs-repair" or "needsrepair" => "settings.linkly.lines.connection.failed",
            null or "" => "settings.linkly.lines.connection.notTested",
            _ => "settings.linkly.lines.connection.unknown"
        });
    }

    private bool CanManageLinklyCloudTerminal(LinklyCloudTerminalManagementItem? item) =>
        !IsBusy && !_isLinklyLineOperationBusy && IsLinklyCloudBackendAsyncMode && IsLinklyCloudLineManagementAvailable &&
        item is { SupportsManagement: true, IsReady: true, IsTesting: false };

    private bool CanAssignLinklyCloudTerminal(LinklyCloudTerminalManagementItem? item) =>
        CanManageLinklyCloudTerminal(item) && !item!.IsBusy;

    private bool CanUnassignLinklyCloudTerminal(LinklyCloudTerminalManagementItem? item) =>
        CanAssignLinklyCloudTerminal(item) && !string.IsNullOrWhiteSpace(item!.AssignedDeviceCode);

    private async Task TestLinklyCloudTerminalAsync(LinklyCloudTerminalManagementItem? item)
    {
        if (!CanManageLinklyCloudTerminal(item))
        {
            return;
        }

        using var permissionGrant = await AuthorizeAsync(
            Permissions.PosTerminal.Settings.PaymentTerminal,
            "test-linkly-cloud-terminal");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        var environment = SelectedLinklyEnvironment;
        var directoryGeneration = _linklyTerminalDirectoryGeneration;
        var requestGeneration = _linklyTerminalTestGenerations.TryGetValue(item!.TerminalId, out var current)
            ? current + 1
            : 1;
        _linklyTerminalTestGenerations[item.TerminalId] = requestGeneration;
        item.IsTesting = true;
        item.ConnectionStatus = T("settings.linkly.cloudBackend.testingLine");
        RaiseCommandStates();
        try
        {
            var result = await _setupService.TestLinklyCloudBackendTerminalAsync(environment, item.Terminal);
            if (!IsCurrentTerminalRequest(item, environment, directoryGeneration, requestGeneration, result.TerminalVersion, result.AssignmentRevision))
            {
                return;
            }

            // Status 表达服务端的确定性；不能把 Unknown 或 NeedsRepair 降级成普通不可达。
            item.ConnectionStatus = FormatLinklyTerminalConnectionStatus(result.Status);
            item.LastTestedAt = result.CheckedAt;
            RememberLinklyTerminalTest(environment, item, result.CheckedAt);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            if (IsCurrentTerminalRequest(item, environment, directoryGeneration, requestGeneration, item.Terminal.TerminalVersion, item.Terminal.AssignmentRevision))
            {
                ConsoleLog.WriteError(
                    "Settings",
                    $"test linkly cloud terminal failed terminalId={item.TerminalId} error={ex.GetType().Name} message={ex.Message}",
                    exception: ex);
                item.ConnectionStatus = T("settings.linkly.cloudBackend.testLineFailed");
                item.LastTestedAt = DateTimeOffset.Now;
                RememberLinklyTerminalTest(environment, item, item.LastTestedAt.Value);
            }
        }
        finally
        {
            if (ReferenceEquals(LinklyCloudTerminalItems.FirstOrDefault(candidate => candidate.TerminalId == item.TerminalId), item))
            {
                item.IsTesting = false;
            }
            RaiseCommandStates();
        }
    }

    private string FormatLinklyTerminalConnectionStatus(string? status) => status?.Trim().ToUpperInvariant() switch
    {
        "CONNECTED" => T("settings.linkly.cloudBackend.healthHealthy"),
        "UNREACHABLE" => T("settings.linkly.cloudBackend.healthUnhealthy"),
        "NEEDS-REPAIR" or "NEEDSREPAIR" => T("settings.linkly.cloudBackend.testNeedsRepair"),
        "UNKNOWN" => T("settings.linkly.cloudBackend.testLineFailed"),
        _ => T("settings.linkly.cloudBackend.testLineFailed")
    };

    private bool IsCurrentTerminalRequest(
        LinklyCloudTerminalManagementItem item,
        CardTerminalEnvironment environment,
        int directoryGeneration,
        int requestGeneration,
        string? terminalVersion,
        long assignmentRevision)
    {
        return SelectedLinklyEnvironment == environment &&
            _linklyTerminalDirectoryGeneration == directoryGeneration &&
            _linklyTerminalTestGenerations.TryGetValue(item.TerminalId, out var latestGeneration) &&
            latestGeneration == requestGeneration &&
            ReferenceEquals(LinklyCloudTerminalItems.FirstOrDefault(candidate => candidate.TerminalId == item.TerminalId), item) &&
            string.Equals(item.Terminal.TerminalVersion, terminalVersion, StringComparison.Ordinal) &&
            item.Terminal.AssignmentRevision == assignmentRevision;
    }

    private Task UseLinklyCloudTerminalAsync(LinklyCloudTerminalManagementItem? item)
    {
        var currentDevice = LinklyCloudDevices.FirstOrDefault(device =>
            device.IsAvailable && string.Equals(device.DeviceCode, Session.DeviceCode, StringComparison.OrdinalIgnoreCase));
        return AssignLinklyCloudTerminalAsync(item, currentDevice, requireTarget: true);
    }

    private Task ChangeLinklyCloudTerminalAssignmentAsync(LinklyCloudTerminalManagementItem? item) =>
        AssignLinklyCloudTerminalAsync(item, item?.SelectedTargetDevice, requireTarget: true);

    private Task UnassignLinklyCloudTerminalAsync(LinklyCloudTerminalManagementItem? item) =>
        AssignLinklyCloudTerminalAsync(item, targetDevice: null, requireTarget: false);

    private async Task AssignLinklyCloudTerminalAsync(
        LinklyCloudTerminalManagementItem? item,
        LinklyCloudAssignableDevice? targetDevice,
        bool requireTarget)
    {
        if (!CanAssignLinklyCloudTerminal(item) || (requireTarget && targetDevice is null))
        {
            if (requireTarget && targetDevice is null)
            {
                SetStatusOverride(T("settings.linkly.cloudBackend.assignmentTargetUnavailable"));
            }
            return;
        }

        var environment = SelectedLinklyEnvironment;
        var directoryGeneration = _linklyTerminalDirectoryGeneration;
        var terminalSnapshot = item!.Terminal;
        var devicesSnapshot = LinklyCloudDevices.ToArray();
        var targetOldTerminal = targetDevice?.SelectedTerminalId is Guid targetTerminalId
            ? LinklyCloudTerminalItems.FirstOrDefault(candidate => candidate.TerminalId == targetTerminalId)?.DisplayName
            : null;
        var confirmation = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            T("settings.linkly.cloudBackend.assignmentConfirmMessage"),
            item.DisplayName,
            item.AssignedDeviceCode ?? T("settings.linkly.cloudBackend.unassigned"),
            targetDevice?.DeviceCode ?? T("settings.linkly.cloudBackend.unassigned"),
            targetOldTerminal ?? T("settings.linkly.cloudBackend.unassigned"));
        if (_confirmLinklyTerminalAssignmentAsync is null ||
            !await _confirmLinklyTerminalAssignmentAsync(confirmation))
        {
            return;
        }

        if (!IsCurrentTerminalSnapshot(item, environment, directoryGeneration, terminalSnapshot))
        {
            SetStatusOverride(T("settings.linkly.cloudBackend.assignmentSnapshotChanged"));
            return;
        }

        using var permissionGrant = await AuthorizeAsync(
            Permissions.PosTerminal.Settings.PaymentTerminal,
            "assign-linkly-cloud-terminal");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        if (!IsCurrentTerminalSnapshot(item, environment, directoryGeneration, terminalSnapshot))
        {
            SetStatusOverride(T("settings.linkly.cloudBackend.assignmentSnapshotChanged"));
            return;
        }
        var assignmentGeneration = _linklyTerminalAssignmentGenerations.TryGetValue(item.TerminalId, out var currentGeneration)
            ? currentGeneration + 1
            : 1;
        _linklyTerminalAssignmentGenerations[item.TerminalId] = assignmentGeneration;
        await RunBusyAsync(async () =>
        {
            var result = await _setupService.AssignLinklyCloudBackendTerminalAsync(
                environment,
                terminalSnapshot,
                targetDevice,
                devicesSnapshot,
                Session);
            if (!IsCurrentTerminalSnapshot(item, environment, directoryGeneration, terminalSnapshot) ||
                !_linklyTerminalAssignmentGenerations.TryGetValue(item.TerminalId, out var latestGeneration) ||
                latestGeneration != assignmentGeneration)
            {
                return;
            }

            SetStatusOverride(result.Message);
            if (result.Directory is not null)
            {
                // 写入响应或模糊失败后的只读校准一旦成为权威结果，立即使更早的目录 GET 失效。
                Interlocked.Increment(ref _linklyTerminalDirectoryRequestGeneration);
                ApplyLinklyCloudTerminalDirectory(environment, result.Directory);
            }
        }, operationName: "assign linkly cloud terminal");
    }

    private bool IsCurrentTerminalSnapshot(
        LinklyCloudTerminalManagementItem item,
        CardTerminalEnvironment environment,
        int directoryGeneration,
        LinklyCloudTerminalSummary snapshot)
    {
        return SelectedLinklyEnvironment == environment &&
            _linklyTerminalDirectoryGeneration == directoryGeneration &&
            ReferenceEquals(LinklyCloudTerminalItems.FirstOrDefault(candidate => candidate.TerminalId == item.TerminalId), item) &&
            string.Equals(item.Terminal.TerminalVersion, snapshot.TerminalVersion, StringComparison.Ordinal) &&
            string.Equals(item.Terminal.AssignedDeviceCode, snapshot.AssignedDeviceCode, StringComparison.OrdinalIgnoreCase) &&
            item.Terminal.AssignmentRevision == snapshot.AssignmentRevision;
    }

    private async Task PairSelectedLinklyCloudBackendTerminalAsync()
    {
        if (!ValidateLinklyCloudPairingInput(password: string.Empty) ||
            SelectedLinklyCloudTerminal is not { } terminal)
        {
            return;
        }

        var environment = SelectedLinklyEnvironment;
        var pairCode = LinklyPairCodeText.Trim();
        // Pair Code 仅可用于这一笔请求；在发起 HTTP 前立即从 UI 状态清除。
        LinklyPairCodeText = string.Empty;
        await RunBusyAsync(async () =>
        {
            ClearLinklyTestStatus();
            SetLinklyTestStatusOverride(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                T("settings.linkly.cloudBackend.pairing"), terminal.DisplayName));
            LinklyCloudTerminalPairResponse result;
            try
            {
                result = await _setupService.PairLinklyCloudBackendTerminalAsync(
                    environment,
                    terminal.TerminalId,
                    pairCode);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // 超时、断网和服务端错误都不能假定配对没有生效；先标记未知并刷新权威目录，
                // 绝不以同一 Pair Code 自动重放请求。
                ReplaceLinklyCloudTerminal(terminal with { PairingState = "Unknown", IsReady = false });
                try
                {
                    await RefreshLinklyCloudBackendTerminalsCoreAsync(environment);
                }
                catch
                {
                    // 刷新失败时保留 Unknown，避免把不可证明的配对显示成 Ready。
                }

                // 失败原因必须留在配对操作旁；传输中断不能当作配对码已被拒绝。
                LogLinklyCloudSettings($"pair terminal failed error={ex.GetType().Name}");
                var reason = ex is System.Net.Http.HttpRequestException { StatusCode: not null }
                    ? ex.Message
                    : T("settings.linkly.cloudBackend.pairUnconfirmed");
                var message = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    T("settings.linkly.cloudBackend.pairFailed"), terminal.DisplayName, reason);
                SetLinklyTestStatusOverride(message);
                SetStatusOverride(message);
                return;
            }

            LinklyConnectionSucceeded = result.IsReady;
            try
            {
                // Pair 会推进终端版本；必须以权威目录替换整行，后续测试/分配才能携带新 CAS facts。
                await RefreshLinklyCloudBackendTerminalsCoreAsync(environment);
            }
            catch
            {
                var message = T("settings.linkly.cloudBackend.pairUnconfirmed");
                SetLinklyTestStatusOverride(message);
                SetStatusOverride(message);
                return;
            }

            if (!result.IsReady)
            {
                var message = T("settings.linkly.cloudBackend.pairRejected");
                SetLinklyTestStatusOverride(message);
                SetStatusOverride(message);
                return;
            }

            try
            {
                var selection = await _setupService.SelectLinklyCloudBackendTerminalAsync(
                    environment,
                    terminal.TerminalId,
                    LinklyCloudSelectionRevision);
                ApplyPersistedLinklyCloudSelection(selection.TerminalId, selection.Revision);
            }
            catch
            {
                // 配对已经成功，选择失败时只能提示“已配对但未选中”，不能再次调用 Pair。
                RestorePersistedLinklyCloudSelection();
                var message = T("settings.linkly.cloudBackend.pairedButNotSelected");
                SetLinklyTestStatusOverride(message);
                SetStatusOverride(message);
                return;
            }

            try
            {
                await RefreshLinklyCloudBackendTerminalsCoreAsync(environment);
                var message = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    T("settings.linkly.cloudBackend.pairedAndSelected"), terminal.DisplayName);
                SetLinklyTestStatusOverride(message);
                SetStatusOverride(message);
            }
            catch
            {
                var message = T("settings.linkly.cloudBackend.pairUnconfirmed");
                SetLinklyTestStatusOverride(message);
                SetStatusOverride(message);
            }
        }, operationName: "pair linkly cloud terminal");
    }

    private Task RefreshLinklyCloudBackendTerminalsCoreAsync(CardTerminalEnvironment environment)
    {
        var requestGeneration = Interlocked.Increment(ref _linklyTerminalDirectoryRequestGeneration);
        return RefreshLinklyCloudBackendTerminalsCoreAsync(environment, requestGeneration);
    }

    private async Task RefreshLinklyCloudBackendTerminalsCoreAsync(
        CardTerminalEnvironment environment,
        int requestGeneration)
    {
        var directory = await _setupService.ListLinklyCloudBackendTerminalsAsync(environment);
        if (SelectedLinklyEnvironment != environment ||
            requestGeneration != _linklyTerminalDirectoryRequestGeneration)
        {
            return;
        }
        ApplyLinklyCloudTerminalDirectory(environment, directory);
    }

    private void ApplyLinklyCloudTerminalDirectory(
        CardTerminalEnvironment environment,
        LinklyCloudTerminalListResponse directory)
    {
        Interlocked.Increment(ref _linklyTerminalDirectoryGeneration);
        _linklyTerminalTestGenerations.Clear();
        _linklyTerminalAssignmentGenerations.Clear();
        LinklyCloudTerminals.Clear();
        foreach (var terminal in directory.Terminals.OrderBy(item => item.LaneNo).ThenBy(item => item.DisplayName))
        {
            LinklyCloudTerminals.Add(terminal);
        }

        LinklyCloudDevices = directory.Devices ?? [];
        IsLinklyCloudLineManagementAvailable = directory.Devices is not null &&
            directory.Terminals.All(terminal => !string.IsNullOrWhiteSpace(terminal.TerminalVersion));
        LinklyCloudTerminalItems.Clear();
        LinklyCloudLines.Clear();
        var availableTargets = LinklyCloudDevices.Where(device => device.IsAvailable).ToArray();
        var currentTarget = availableTargets.FirstOrDefault(device =>
            string.Equals(device.DeviceCode, Session.DeviceCode, StringComparison.OrdinalIgnoreCase));
        var currentSnapshotKeys = LinklyCloudTerminals
            .Select(terminal => CreateLinklyTerminalTestSnapshotKey(environment, terminal))
            .ToHashSet();
        foreach (var staleKey in _linklyTerminalTestSnapshots.Keys
                     .Where(key => key.Environment == environment && !currentSnapshotKeys.Contains(key))
                     .ToArray())
        {
            // 同一环境内绑定、分配修订或终端版本一旦变化，旧检测结果立即失效。
            _linklyTerminalTestSnapshots.Remove(staleKey);
        }
        foreach (var terminal in LinklyCloudTerminals)
        {
            var snapshotKey = CreateLinklyTerminalTestSnapshotKey(environment, terminal);
            var hasLocalSnapshot = _linklyTerminalTestSnapshots.TryGetValue(snapshotKey, out var localSnapshot) &&
                (terminal.LastHealthAt is null || terminal.LastHealthAt <= localSnapshot.CheckedAt);
            if (!hasLocalSnapshot && localSnapshot is not null)
            {
                // 服务端已有更新检测时，以服务端为准，避免之后的旧目录重新唤回本地结果。
                _linklyTerminalTestSnapshots.Remove(snapshotKey);
            }
            var managementItem = new LinklyCloudTerminalManagementItem(
                terminal,
                availableTargets,
                currentTarget,
                hasLocalSnapshot
                    ? localSnapshot!.ConnectionStatus
                    : FormatPersistedLinklyHealthStatus(terminal.LastHealthStatus),
                T,
                hasLocalSnapshot ? localSnapshot!.CheckedAt : terminal.LastHealthAt);
            LinklyCloudTerminalItems.Add(managementItem);
            LinklyCloudLines.Add(CreateLinklyCloudLine(terminal, directory.SelectedTerminalId, managementItem));
        }

        var previousPersistedTerminalId = _persistedLinklyCloudTerminalId;
        _persistedLinklyCloudTerminalId = directory.SelectedTerminalId;
        LinklyCloudSelectionRevision = directory.SelectionRevision;
        SelectedLinklyCloudTerminal = directory.SelectedTerminalId is Guid selectedTerminalId
            ? LinklyCloudTerminals.FirstOrDefault(item => item.TerminalId == selectedTerminalId)
            // 服务端未持久选择时保持未选择，避免刷新多终端列表时隐式绑定 Lane 1。
            : null;
        if (previousPersistedTerminalId != _persistedLinklyCloudTerminalId)
        {
            ResetLinklyConnectionTest();
        }
        RaiseLinklyCloudTerminalProperties();
        OnPropertyChanged(nameof(LinklyCloudDevices));
        OnPropertyChanged(nameof(IsLinklyCloudLineManagementAvailable));
        OnPropertyChanged(nameof(IsLinklyCloudLineManagementUnavailable));
        OnPropertyChanged(nameof(LinklyCloudLineManagementMessage));
    }

    private string FormatPersistedLinklyHealthStatus(string? status) => status?.Trim().ToUpperInvariant() switch
    {
        "HEALTHY" => T("settings.linkly.cloudBackend.healthHealthy"),
        "UNHEALTHY" => T("settings.linkly.cloudBackend.healthUnhealthy"),
        _ => string.Empty
    };

    private void RememberLinklyTerminalTest(
        CardTerminalEnvironment environment,
        LinklyCloudTerminalManagementItem item,
        DateTimeOffset checkedAt)
    {
        var key = CreateLinklyTerminalTestSnapshotKey(environment, item.Terminal);
        _linklyTerminalTestSnapshots[key] = new LinklyTerminalTestSnapshot(item.ConnectionStatus, checkedAt);
    }

    private static LinklyTerminalTestSnapshotKey CreateLinklyTerminalTestSnapshotKey(
        CardTerminalEnvironment environment,
        LinklyCloudTerminalSummary terminal) =>
        new(
            environment,
            terminal.TerminalId,
            terminal.TerminalVersion ?? string.Empty,
            terminal.AssignedDeviceCode?.Trim().ToUpperInvariant() ?? string.Empty,
            terminal.AssignmentRevision);

    private sealed record LinklyTerminalTestSnapshot(string ConnectionStatus, DateTimeOffset CheckedAt);

    private readonly record struct LinklyTerminalTestSnapshotKey(
        CardTerminalEnvironment Environment,
        Guid TerminalId,
        string TerminalVersion,
        string AssignedDeviceCode,
        long AssignmentRevision);

    private void ReplaceLinklyCloudTerminal(
        LinklyCloudTerminalSummary replacement,
        bool selectReplacement = true)
    {
        var index = -1;
        for (var candidateIndex = 0; candidateIndex < LinklyCloudTerminals.Count; candidateIndex++)
        {
            if (LinklyCloudTerminals[candidateIndex].TerminalId == replacement.TerminalId)
            {
                index = candidateIndex;
                break;
            }
        }

        if (index >= 0)
        {
            LinklyCloudTerminals[index] = replacement;
        }

        if (selectReplacement)
        {
            SelectedLinklyCloudTerminal = replacement;
        }
        RaiseLinklyCloudTerminalProperties();
    }

    private void ApplyPersistedLinklyCloudSelection(Guid terminalId, long revision)
    {
        var paymentLineChanged = _persistedLinklyCloudTerminalId != terminalId;
        _persistedLinklyCloudTerminalId = terminalId;
        LinklyCloudSelectionRevision = revision;
        SelectedLinklyCloudTerminal = LinklyCloudTerminals.FirstOrDefault(item => item.TerminalId == terminalId)
            ?? SelectedLinklyCloudTerminal;
        foreach (var line in LinklyCloudLines)
        {
            line.IsSelected = line.Terminal.TerminalId == terminalId;
        }
        if (paymentLineChanged)
        {
            // 连接成功只属于完成检测时的付款线路，切线后必须重新验证。
            ResetLinklyConnectionTest();
        }
        RaiseLinklyCloudTerminalProperties();
    }

    private void RestorePersistedLinklyCloudSelection()
    {
        SelectedLinklyCloudTerminal = _persistedLinklyCloudTerminalId is Guid persistedTerminalId
            ? LinklyCloudTerminals.FirstOrDefault(item => item.TerminalId == persistedTerminalId)
            : null;
        foreach (var line in LinklyCloudLines)
        {
            line.IsSelected = line.Terminal.TerminalId == _persistedLinklyCloudTerminalId;
        }
        RaiseLinklyCloudTerminalProperties();
    }

    private void ClearLinklyCloudTerminalDirectory()
    {
        Interlocked.Increment(ref _linklyTerminalDirectoryGeneration);
        Interlocked.Increment(ref _linklyTerminalDirectoryRequestGeneration);
        _linklyTerminalTestGenerations.Clear();
        _linklyTerminalAssignmentGenerations.Clear();
        LinklyCloudTerminals.Clear();
        LinklyCloudTerminalItems.Clear();
        LinklyCloudLines.Clear();
        LinklyCloudDevices = [];
        IsLinklyCloudLineManagementAvailable = false;
        SelectedLinklyCloudTerminal = null;
        _persistedLinklyCloudTerminalId = null;
        LinklyCloudSelectionRevision = null;
        RaiseLinklyCloudTerminalProperties();
        OnPropertyChanged(nameof(LinklyCloudDevices));
        OnPropertyChanged(nameof(IsLinklyCloudLineManagementAvailable));
        OnPropertyChanged(nameof(IsLinklyCloudLineManagementUnavailable));
        OnPropertyChanged(nameof(LinklyCloudLineManagementMessage));
    }

    private void RaiseLinklyCloudTerminalProperties()
    {
        OnPropertyChanged(nameof(LinklyCloudSelectionRevision));
        OnPropertyChanged(nameof(HasLinklyCloudTerminals));
        OnPropertyChanged(nameof(SelectedLinklyCloudTerminalText));
        RaiseCommandStates();
    }

    private bool ValidateLinklyCloudPairingInput()
    {
        return ValidateLinklyCloudPairingInput(LinklyCloudPasswordText);
    }

    private bool ValidateLinklyCloudPairingInput(string password)
    {
        if (IsLinklyCloudBackendAsyncMode)
        {
            if (SelectedLinklyCloudTerminal is null)
            {
                LogLinklyCloudSettings($"pair validation blocked environment={SelectedLinklyEnvironment} reason=missing-terminal");
                SetLinklyTestValidationStatus("settings.linkly.cloudBackend.terminalRequired");
                return false;
            }

            var normalizedPairCode = LinklyPairCodeText.Trim();
            if (normalizedPairCode.Length != 6 || normalizedPairCode.Any(character => !char.IsAsciiDigit(character)))
            {
                LogLinklyCloudSettings($"pair validation blocked environment={SelectedLinklyEnvironment} reason=invalid-pair-code");
                SetLinklyTestValidationStatus("settings.linkly.cloudBackend.pairCodeSixDigits");
                return false;
            }

            return true;
        }

        if (string.IsNullOrWhiteSpace(LinklyCloudUsernameText))
        {
            LogLinklyCloudSettings($"pair validation blocked environment={SelectedLinklyEnvironment} reason=missing-username");
            SetLinklyTestStatus("settings.linklyCloud.usernameRequired");
            SetStatus("settings.linklyCloud.usernameRequired");
            return false;
        }

        if (string.IsNullOrWhiteSpace(password) && !HasSavedLinklyCloudPassword)
        {
            LogLinklyCloudSettings($"pair validation blocked environment={SelectedLinklyEnvironment} reason=missing-password");
            SetLinklyTestStatus("settings.linklyCloud.passwordRequired");
            SetStatus("settings.linklyCloud.passwordRequired");
            return false;
        }

        if (string.IsNullOrWhiteSpace(LinklyPairCodeText))
        {
            LogLinklyCloudSettings($"pair validation blocked environment={SelectedLinklyEnvironment} reason=missing-pair-code");
            SetLinklyTestStatus("settings.linklyCloud.pairCodeRequired");
            SetStatus("settings.linklyCloud.pairCodeRequired");
            return false;
        }

        return true;
    }

    private async Task SaveLinklyCloudCredentialAsync()
    {
        await SaveLinklyCloudCredentialAsync(LinklyCloudPasswordText);
    }

    public async Task SaveLinklyCloudCredentialAsync(string password)
    {
        var passwordSnapshot = password;
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Settings.PaymentTerminal, "save-linkly-cloud-credential");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        password = passwordSnapshot;
        SyncLinklyInputs();
        await _linklyCoordinator.SaveCloudCredentialAsync(password, _linklyState);
        SyncLinklyState();
    }

    private void CancelLinklyCloudPairing()
    {
        SyncLinklyInputs();
        _linklyCoordinator.CancelCloudPairing(_linklyState);
        SyncLinklyState();
    }

    private async Task SaveLinklyAsync()
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Settings.PaymentTerminal, "save-linkly");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        SyncLinklyInputs();
        await _linklyCoordinator.SaveAsync(_linklyState, LinklyModePriorityItems);
        SyncLinklyState();
    }

    private async Task SaveReceiptPrinterAsync()
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Settings.ReceiptPrinter, "save-receipt-printer");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        await _receiptPrinterSection.SaveAsync();
    }

    private async Task TestReceiptPrinterAsync()
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Settings.ReceiptPrinter, "test-receipt-printer");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        await _receiptPrinterSection.TestAsync();
    }

    private async Task LoadReceiptProfileAsync()
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Settings.ReceiptPrinter, "load-receipt-profile");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();

        // 中文注释：本机已应用过总部下发（版本 > 0）时，六个字段只读、按钮是「立即同步」：
        // 直接跑一轮同步并显示结果，不再把资料载入草稿。
        if (IsReceiptProfileManaged)
        {
            await SyncReceiptProfileNowAsync();
            return;
        }

        if (_storeReceiptProfileApiClient is null)
        {
            ReceiptPrinterTestStatusMessage = "Store receipt profile is not configured.";
            return;
        }

        try
        {
            var profile = await _storeReceiptProfileApiClient.GetCurrentAsync();
            var validationError = StoreReceiptProfileValidator.Validate(profile);
            if (validationError is not null)
            {
                ReceiptPrinterTestStatusMessage = validationError;
                return;
            }

            if (profile.Version > 0 && _receiptProfileSyncService is not null)
            {
                // 中文注释：总部已下发但本机还没应用（后台同步还没跑到）：改走同步，
                // 让资料原子写入并进入只读，而不是塞进草稿再靠手工保存（保存后版本仍为 0，随后还会被同步覆盖一次）。
                await SyncReceiptProfileNowAsync();
                return;
            }

            // 中文注释：仅完整替换六个资料字段，不自动保存，也不改动打印机端口/纸宽/语言/切纸等硬件设置。
            ReceiptBrandNameText = profile.BrandName ?? string.Empty;
            ReceiptStoreNameText = profile.StoreName ?? string.Empty;
            ReceiptStoreAddressText = profile.Address ?? string.Empty;
            ReceiptStorePhoneText = profile.Phone ?? string.Empty;
            ReceiptAbnText = profile.Abn ?? string.Empty;
            ReceiptReturnPolicyText = profile.ReturnPolicy ?? string.Empty;
            ReceiptVoucherTermsText = profile.VoucherTerms ?? string.Empty;
            ReceiptInstallmentTermsText = profile.InstallmentTerms ?? string.Empty;
            ReceiptPrinterTestStatusMessage = T("settings.status.receiptProfileLoaded");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 中文注释：网络失败或契约异常不改动任何草稿字段，仅回写错误状态。
            ReceiptPrinterTestStatusMessage = ex.Message;
        }
    }

    private async Task DownloadCatalogAsync(CancellationToken cancellationToken)
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Settings.CatalogDownload, "download-catalog", cancellationToken);
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        await _dataMaintenanceSection.DownloadCatalogAsync(cancellationToken);
    }

    private async Task ResetCatalogAsync(CancellationToken cancellationToken)
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Settings.CatalogReset, "reset-catalog", cancellationToken);
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        await _dataMaintenanceSection.ResetCatalogAsync(cancellationToken);
    }

    private async Task ResetTestSalesDataAsync(CancellationToken cancellationToken)
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Settings.TestDataReset, "reset-test-sales", cancellationToken);
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        await _dataMaintenanceSection.ResetTestSalesDataAsync(cancellationToken);
    }

    private async Task ReregisterDeviceAsync()
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Settings.DeviceRegistration, "reregister-device");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        await _dataMaintenanceSection.ReregisterDeviceAsync();
    }

    private async Task CheckForAppUpdateAsync(CancellationToken cancellationToken)
    {
        using var permissionGrant = await AuthorizeAsync(Permissions.PosTerminal.Settings.AppUpdate, "check-update", cancellationToken);
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        await _dataMaintenanceSection.CheckForAppUpdateAsync(cancellationToken);
    }

    private bool CanLoadLocations()
    {
        return !IsBusy;
    }

    private bool CanLoadDevices()
    {
        return !IsBusy && SelectedSquareLocation is not null;
    }

    private bool CanSaveSquare()
    {
        return !IsBusy &&
            SelectedSquareLocation is not null &&
            SelectedSquareDevice is not null &&
            SquareLocations.Any(location => string.Equals(location.Id, SelectedSquareLocation.Id, StringComparison.OrdinalIgnoreCase)) &&
            SquareDevices.Any(device => string.Equals(device.Id, SelectedSquareDevice.Id, StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(_devicesLoadedForLocationId, SelectedSquareLocation.Id, StringComparison.OrdinalIgnoreCase);
    }

    private bool CanLoadDeviceCodes()
    {
        return !IsBusy && IsSquareDeviceCodesSupported && SelectedSquareLocation is not null;
    }

    private bool CanCreateDeviceCode()
    {
        return !IsBusy &&
            IsSquareDeviceCodesSupported &&
            SelectedSquareLocation is not null &&
            !string.IsNullOrWhiteSpace(SquareDeviceCodeNameText);
    }

    private bool CanRefreshDeviceCodeStatus()
    {
        return !IsBusy && IsSquareDeviceCodesSupported && SelectedSquareDeviceCode is not null;
    }

    private bool CanTestLinkly()
    {
        return !IsBusy && (PrimaryLinklyMode != LinklySettingsMode.CloudDirectSync || HasSavedLinklyCloudSecret);
    }

    private bool CanLogonLinkly()
    {
        return !IsBusy &&
            PrimaryLinklyMode == LinklySettingsMode.LocalIp &&
            LinklyConnectionSucceeded &&
            LinklyPinPadLoggedOn == false;
    }

    private bool CanTestLinklyTransactionStatus()
    {
        return !IsBusy && IsLinklyCloudBackendAsyncMode;
    }

    private bool CanSaveLinkly()
    {
        return !IsBusy && LinklyConnectionSucceeded;
    }

    private bool CanPairLinklyCloud()
    {
        return !IsBusy && IsLinklyCloudMode &&
            (!IsLinklyCloudBackendAsyncMode || SelectedLinklyCloudTerminal is not null);
    }

    private bool CanSaveLinklyCloudCredential()
    {
        return !IsBusy &&
            IsLinklyCloudDirectSyncMode &&
            !string.IsNullOrWhiteSpace(LinklyCloudUsernameText);
    }

    private bool CanCancelLinklyCloudPairing()
    {
        return !IsBusy &&
            IsLinklyCloudMode &&
            (!string.IsNullOrWhiteSpace(LinklyPairCodeText) ||
             (!IsLinklyCloudBackendAsyncMode &&
              (_hasLinklyCloudPasswordInput ||
               !string.IsNullOrWhiteSpace(LinklyCloudPasswordText))));
    }

    private bool CanSaveReceiptPrinter()
    {
        return _receiptPrinterSection.CanSave();
    }

    private bool CanTestReceiptPrinter()
    {
        return _receiptPrinterSection.CanTest();
    }

    private bool CanDownloadCatalog()
    {
        return _dataMaintenanceSection.CanDownloadCatalog();
    }

    private bool CanResetCatalog()
    {
        return _dataMaintenanceSection.CanResetCatalog();
    }

    private bool CanResetTestSalesData()
    {
        return _dataMaintenanceSection.CanResetTestSalesData();
    }

    private bool CanReregisterDevice()
    {
        return _dataMaintenanceSection.CanReregisterDevice();
    }

    private bool CanCheckForAppUpdate()
    {
        return _dataMaintenanceSection.CanCheckForAppUpdate();
    }

    private bool CanInstallRemoteMaintenance()
    {
        return !IsBusy && (!_enforcePermissions || _cashierSessionContext.CurrentSession is not null);
    }

    private async Task SelectRemoteMaintenanceAsync()
    {
        SelectedCategory = SettingsCategory.RemoteMaintenance;
        await LoadRemoteMaintenanceStatusAsync();
    }

    private async Task LoadRemoteMaintenanceStatusAsync()
    {
        if (_remoteMaintenanceService is null)
        {
            _remoteMaintenanceStatus = new RemoteMaintenanceStatus(false, string.Empty, string.Empty, "notInstalled");
        }
        else
        {
            _remoteMaintenanceStatus = await _remoteMaintenanceService.GetStatusAsync();
        }

        OnPropertyChanged(nameof(IsRemoteMaintenanceConfigured));
        OnPropertyChanged(nameof(RemoteMaintenanceActionText));
        OnPropertyChanged(nameof(RemoteMaintenanceStatusText));
        OnPropertyChanged(nameof(RemoteMaintenanceDetailText));
        RaiseCommandStates();
    }

    private async Task InstallRemoteMaintenanceAsync()
    {
        if (_remoteMaintenanceService is null)
        {
            _remoteMaintenanceStatus = new RemoteMaintenanceStatus(false, string.Empty, string.Empty, "notInstalled");
            OnPropertyChanged(nameof(RemoteMaintenanceDetailText));
            return;
        }

        await RunBusyAsync(async () =>
        {
            IsRemoteMaintenanceInstalling = true;
            var acceptingProgress = true;
            SetRemoteMaintenanceProgress("settings.remoteMaintenance.progress.preparing");
            // Progress 在 WPF UI 上下文回调；结束后忽略已排队的旧阶段，避免覆盖最终结果。
            var progress = new Progress<RemoteMaintenanceStage>(stage =>
            {
                if (!acceptingProgress) return;
                SetRemoteMaintenanceProgress(stage switch
                {
                    RemoteMaintenanceStage.DownloadingRustDesk => "settings.remoteMaintenance.progress.downloadingRustDesk",
                    RemoteMaintenanceStage.DownloadingStatusAgent => "settings.remoteMaintenance.progress.downloadingStatusAgent",
                    RemoteMaintenanceStage.DownloadedInstalling => "settings.remoteMaintenance.progress.downloadedInstalling",
                    RemoteMaintenanceStage.Registering => "settings.remoteMaintenance.progress.registering",
                    RemoteMaintenanceStage.Configuring => "settings.remoteMaintenance.progress.configuring",
                    _ => "settings.remoteMaintenance.progress.preparing"
                });
            });
            try
            {
                var result = await _remoteMaintenanceService.InstallAsync(Session, progress: progress);
                acceptingProgress = false;
                // 安装远程维护会在收银机上开放远程控制，按安装结果留痕；结果文案是稳定资源键。
                OperationAuditEvents.RecordAction(
                    _operationAuditLogger,
                    OperationAuditTypes.RemoteMaintenanceInstall,
                    result.Succeeded ? "Succeeded" : "Failed",
                    Session,
                    reasonCode: "SETTINGS",
                    safeMessage: result.Message);
                _remoteMaintenanceStatus = result.Status;
                SetRemoteMaintenanceProgress(result.Message);
                OnPropertyChanged(nameof(IsRemoteMaintenanceConfigured));
                OnPropertyChanged(nameof(RemoteMaintenanceActionText));
                OnPropertyChanged(nameof(RemoteMaintenanceStatusText));
                OnPropertyChanged(nameof(RemoteMaintenanceDetailText));
            }
            catch (OperationCanceledException ex)
            {
                acceptingProgress = false;
                RecordRemoteMaintenanceInstallFailure(ex);
                SetRemoteMaintenanceProgress("settings.status.operationCanceled");
            }
            catch (Exception ex)
            {
                acceptingProgress = false;
                RecordRemoteMaintenanceInstallFailure(ex);
                SetRemoteMaintenanceProgress("settings.remoteMaintenance.result.configurationFailed");
            }
            finally
            {
                acceptingProgress = false;
                IsRemoteMaintenanceInstalling = false;
            }
        }, "install remote maintenance");
    }

    private void RecordRemoteMaintenanceInstallFailure(Exception ex)
    {
        OperationAuditEvents.RecordAction(
            _operationAuditLogger,
            OperationAuditTypes.RemoteMaintenanceInstall,
            "Failed",
            Session,
            reasonCode: "SETTINGS",
            safeMessage: ex.GetType().Name);
    }

    /// <summary>
    /// 给数据维护操作包一层审计：完成记成功，异常记失败后原样抛出，
    /// 由 RunBusyAsync 继续负责状态提示；授权激活期内的记录会附带授权人。
    /// </summary>
    private Func<CancellationToken, Task>? WithOperationAudit(
        Func<CancellationToken, Task>? action,
        string operationType)
    {
        if (action is null)
        {
            return null;
        }

        return async cancellationToken =>
        {
            try
            {
                await action(cancellationToken);
            }
            catch (Exception ex)
            {
                OperationAuditEvents.RecordAction(
                    _operationAuditLogger,
                    operationType,
                    "Failed",
                    Session,
                    reasonCode: "SETTINGS",
                    safeMessage: ex.GetType().Name);
                throw;
            }

            OperationAuditEvents.RecordAction(
                _operationAuditLogger,
                operationType,
                "Succeeded",
                Session,
                reasonCode: "SETTINGS");
        };
    }

    private void SetRemoteMaintenanceProgress(string key)
    {
        _remoteMaintenanceProgressKey = key;
        OnPropertyChanged(nameof(RemoteMaintenanceProgressText));
        OnPropertyChanged(nameof(HasRemoteMaintenanceProgress));
        SetStatus(key);
    }

    private void ReturnToPos()
    {
        // 离开设置页时取消目录维护请求，给无限 HTTP 超时提供真实的用户退出路径。
        DownloadCatalogCommand.Cancel();
        ResetCatalogCommand.Cancel();
        _returnToPos?.Invoke();
    }

    private async Task SelectCategoryAsync(SettingsCategory category, string permissionCode)
    {
        using var permissionGrant = await AuthorizeAsync(permissionCode, $"select-category/{category.ToString().ToLowerInvariant()}");
        if (permissionGrant is null)
        {
            return;
        }

        using var authorizationActivation = permissionGrant.Activate();
        SelectedCategory = category;
    }

    private Task<ViewModelAuthorizationGrant?> AuthorizeAsync(
        string permissionCode,
        string action,
        CancellationToken cancellationToken = default) =>
        ViewModelOperationAuthorization.AuthorizeAsync(
            _operationAuthorizationService,
            TryRequirePermission,
            permissionCode,
            "settings",
            action,
            Session,
            cancellationToken);

    private bool TryRequirePermission(string permissionCode)
    {
        if ((!_enforcePermissions && _cashierSessionContext.CurrentSession is null) ||
            _cashierSessionContext.RequirePermission(permissionCode, out var message))
        {
            return true;
        }

        StatusMessage = message;
        return false;
    }

    private async Task RunBusyAsync(Func<Task> action, string? operationName = null)
    {
        IsBusy = true;
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            if (!string.IsNullOrWhiteSpace(operationName))
            {
                ConsoleLog.Write(ResolveSettingsLogCategory(operationName), $"settings ui {operationName} canceled");
            }
            SetStatus("settings.status.operationCanceled");
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(operationName))
            {
                // 设置页人工操作失败（多为联网/配置问题）记 Warning 并带异常；Linkly 操作归到 LinklySettings 类别，其余保持原 Square 类别。
                ConsoleLog.WriteWarning(
                    ResolveSettingsLogCategory(operationName),
                    $"settings ui {operationName} failed message={LogValue(ex.Message)}",
                    new ApplicationLogContext(
                        Properties: new Dictionary<string, object?>
                        {
                            ["operation"] = operationName,
                            ["screen"] = "Settings"
                        }),
                    ex);
            }
            SetStatusOverride(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaiseLocalizedProperties()
    {
        OnPropertyChanged(nameof(ScreenTitleText));
        OnPropertyChanged(nameof(CardTerminalTitleText));
        OnPropertyChanged(nameof(SettingsSubtitleText));
        OnPropertyChanged(nameof(DataMaintenanceTitleText));
        OnPropertyChanged(nameof(DeviceRegistrationTitleText));
        OnPropertyChanged(nameof(RemoteMaintenanceTitleText));
        OnPropertyChanged(nameof(RemoteMaintenanceDescriptionText));
        OnPropertyChanged(nameof(RemoteMaintenanceActionText));
        OnPropertyChanged(nameof(RemoteMaintenanceStatusText));
        OnPropertyChanged(nameof(RemoteMaintenanceDetailText));
        OnPropertyChanged(nameof(SquareTitleText));
        OnPropertyChanged(nameof(RemoteMaintenanceProgressText));
        OnPropertyChanged(nameof(LinklyTitleText));
        RaiseActivePaymentProviderProperties();
        RaiseCatalogSyncStatusProperties();
        OnPropertyChanged(nameof(LinklyCloudSecretStatusText));
        OnPropertyChanged(nameof(LinklyCloudCredentialStatusText));
        OnPropertyChanged(nameof(LinklyTestActionText));
        OnPropertyChanged(nameof(ReceiptPrinterTitleText));
        OnPropertyChanged(nameof(ReceiptProfileLoadButtonText));
        OnPropertyChanged(nameof(ReceiptProfileManagedHintText));
        OnPropertyChanged(nameof(SquareTokenStatusText));
        OnPropertyChanged(nameof(SquareDeviceCodesUnavailableText));
        foreach (var line in LinklyCloudLines)
        {
            line.RefreshLocalization();
        }
        foreach (var item in LinklyCloudTerminalItems)
        {
            item.RefreshLocalizedText();
            if (item.LastTestedAt == item.Terminal.LastHealthAt)
            {
                item.ConnectionStatus = FormatPersistedLinklyHealthStatus(item.Terminal.LastHealthStatus);
            }
        }
        RefreshLocalizedMessages();
    }

    private void RaiseActivePaymentProviderProperties()
    {
        OnPropertyChanged(nameof(ActivePaymentProviderText));
        OnPropertyChanged(nameof(ActivePaymentProviderDetailText));
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        RaiseLocalizedProperties();
    }

    partial void OnSelectedCategoryChanged(SettingsCategory value)
    {
        OnPropertyChanged(nameof(SettingsSubtitleText));
        OnPropertyChanged(nameof(IsDataMaintenanceSelected));
        OnPropertyChanged(nameof(IsPaymentTerminalSelected));
        OnPropertyChanged(nameof(IsReceiptPrinterSelected));
        OnPropertyChanged(nameof(IsDeviceRegistrationSelected));
        OnPropertyChanged(nameof(IsRemoteMaintenanceSelected));
    }

    private async Task LoadReceiptPrinterSettingsAsync()
    {
        await _receiptPrinterSection.LoadAsync();
    }

    private void ApplyReceiptPrinterSettings(ReceiptPrinterSettings settings)
    {
        ReceiptPrinterPortText = settings.PrinterPort;
        ReceiptBrandNameText = settings.BrandName;
        ReceiptStoreNameText = settings.StoreName;
        ReceiptStoreAddressText = settings.StoreAddress;
        ReceiptStorePhoneText = settings.StorePhone;
        ReceiptAbnText = settings.Abn;
        ReceiptReturnPolicyText = settings.ReturnPolicy;
        ReceiptVoucherTermsText = settings.VoucherTerms;
        ReceiptInstallmentTermsText = settings.InstallmentTerms;
        ReceiptPrintBankReceiptText = settings.PrintBankReceiptText;
        ReceiptProfileVersion = settings.ProfileVersion;
    }

    private ReceiptPrinterSettings CreateReceiptPrinterSettingsFromFields()
    {
        return new ReceiptPrinterSettings(
            ReceiptPrinterPortText,
            ReceiptBrandNameText,
            ReceiptStoreNameText,
            ReceiptStoreAddressText,
            ReceiptStorePhoneText,
            ReceiptAbnText,
            ReceiptReturnPolicyText,
            ReceiptPrinterSettings.Default.CutDistance,
            ReceiptPrintBankReceiptText,
            // 版本只由同步写入；存储层保存时也不会读取它，这里带上仅为让保存后回显与界面状态一致。
            ReceiptProfileVersion,
            ReceiptVoucherTermsText,
            ReceiptInstallmentTermsText);
    }

    partial void OnIsSquareSandboxChanged(bool value)
    {
        if (_syncingSquare) return;
        LogSquareSettings($"square environment changed environment={SelectedSquareEnvironment}");
        SquareLocations.Clear();
        SquareDevices.Clear();
        ResetSquareDeviceCodes();
        _devicesLoadedForLocationId = null;
        SelectedSquareLocation = null;
        SelectedSquareDevice = null;
        // Square 和 Linkly 的编辑环境需要隔离，切换 Square 时不要清空 Linkly Cloud 输入。
        RaiseCommandStates();
        OnPropertyChanged(nameof(SelectedSquareEnvironment));
        OnPropertyChanged(nameof(IsSquareDeviceCodesSupported));
        OnPropertyChanged(nameof(IsSquareDeviceCodesUnsupported));
        OnPropertyChanged(nameof(SquareDeviceCodesUnavailableText));
    }

    partial void OnIsLinklySandboxChanged(bool value)
    {
        if (_syncingLinkly) return;
        SyncLinklyInputs();
        LogLinklyCloudSettings($"linkly environment changed environment={SelectedLinklyEnvironment}");
        ResetLinklyConnectionTest();
        LinklyCloudUsernameText = string.Empty;
        HasSavedLinklyCloudSecret = false;
        HasSavedLinklyCloudPassword = false;
        LinklyCloudPasswordText = string.Empty;
        LinklyPairCodeText = string.Empty;
        // Square 和 Linkly 的编辑环境需要隔离，切换 Linkly 时不要清空 Square 门店和设备列表。
        if (IsLinklyCloudBackendAsyncMode)
        {
            ClearLinklyCloudTerminalDirectory();
            if (!IsBusy)
            {
                _ = RefreshLinklyCloudBackendTerminalsAsync();
            }
        }
        else
        {
            _ = RefreshLinklyCloudSecretStatusAsync(SelectedLinklyEnvironment);
            _ = RefreshLinklyCloudCredentialStatusAsync(SelectedLinklyEnvironment);
        }
        RaiseCommandStates();
        OnPropertyChanged(nameof(SelectedLinklyEnvironment));
        OnPropertyChanged(nameof(SelectedEnvironment));
        RaiseLinklyTestPresentationProperties();
    }

    partial void OnHasSavedSquareTokenChanged(bool value)
    {
        RaiseCommandStates();
        OnPropertyChanged(nameof(SquareTokenStatusText));
    }

    partial void OnSelectedLinklyModeChanged(LinklySettingsMode value)
    {
        if (_syncingLinkly) return;
        SyncLinklyInputs();
        PromoteLinklyModeToPrimary(value);

        ResetLinklyConnectionTest();
        RaiseCommandStates();
        OnPropertyChanged(nameof(IsLinklyCloudMode));
        OnPropertyChanged(nameof(IsLinklyLocalMode));
        OnPropertyChanged(nameof(IsLinklyLocalIpMode));
        OnPropertyChanged(nameof(IsLinklyCloudDirectSyncMode));
        OnPropertyChanged(nameof(IsLinklyCloudBackendAsyncMode));
        OnPropertyChanged(nameof(IsLinklyStandardActionMode));
        RaiseLinklyTestPresentationProperties();
        if (IsLinklyCloudBackendAsyncMode)
        {
            // 模式切换只改变当前付款路径；保留尚未保存的 Direct Sync 草稿，
            // 以便收银员切回该模式时不会丢失已输入的配置。首次加载 Backend
            // 配置仍在 LoadAsync 中清空这些字段，避免读取或展示本地凭据。
            if (!IsBusy)
            {
                _ = RefreshLinklyCloudBackendTerminalsAsync();
            }
        }
        else
        {
            ClearLinklyCloudTerminalDirectory();
        }
    }

    private void SelectLinklyPriorityMode(LinklyModePriorityItem? item)
    {
        if (item is null)
        {
            return;
        }

        // 选择某个模式时同步提升为主模式，保证测试和保存目标一致。
        SelectedLinklyMode = item.Mode;
    }

    private bool CanMoveLinklyPriorityUp(LinklyModePriorityItem? item)
    {
        return _linklyCoordinator.CanMovePriorityUp(item, LinklyModePriorityItems, IsBusy);
    }

    private bool CanMoveLinklyPriorityDown(LinklyModePriorityItem? item)
    {
        return _linklyCoordinator.CanMovePriorityDown(item, LinklyModePriorityItems, IsBusy);
    }

    private void MoveLinklyPriorityUp(LinklyModePriorityItem? item)
    {
        _linklyCoordinator.MovePriorityUp(item, LinklyModePriorityItems, _linklyState);
        SyncLinklyState();
        OnPropertyChanged(nameof(PrimaryLinklyMode));
    }

    private void MoveLinklyPriorityDown(LinklyModePriorityItem? item)
    {
        _linklyCoordinator.MovePriorityDown(item, LinklyModePriorityItems, _linklyState);
        SyncLinklyState();
        OnPropertyChanged(nameof(PrimaryLinklyMode));
    }

    partial void OnLinklyCloudUsernameTextChanged(string value)
    {
        if (_syncingLinkly) return;
        SyncLinklyInputs();
        Interlocked.Increment(ref _linklyCredentialEditVersion);
        ResetLinklyConnectionTest();
        RaiseCommandStates();
    }

    partial void OnLinklyCloudPasswordTextChanged(string value)
    {
        if (_syncingLinkly) return;
        _hasLinklyCloudPasswordInput = !string.IsNullOrWhiteSpace(value);
        SyncLinklyInputs();
        Interlocked.Increment(ref _linklyCredentialEditVersion);
        ResetLinklyConnectionTest();
        RaiseCommandStates();
    }

    partial void OnHasSavedLinklyCloudPasswordChanged(bool value)
    {
        RaiseCommandStates();
        OnPropertyChanged(nameof(LinklyCloudCredentialStatusText));
    }

    partial void OnLinklyPairCodeTextChanged(string value)
    {
        RaiseCommandStates();
    }

    partial void OnHasSavedLinklyCloudSecretChanged(bool value)
    {
        RaiseCommandStates();
        OnPropertyChanged(nameof(LinklyCloudSecretStatusText));
    }

    partial void OnSelectedLinklyCloudTerminalChanged(LinklyCloudTerminalSummary? value)
    {
        RaiseCommandStates();
        OnPropertyChanged(nameof(SelectedLinklyCloudTerminalText));
    }

    private async Task RefreshLinklyCloudSecretStatusAsync(CardTerminalEnvironment environment)
    {
        var version = Interlocked.Increment(ref _linklySecretStatusVersion);
        try
        {
            var hasSecret = await _setupService.HasLinklyCloudSecretAsync(environment);
            if (version == _linklySecretStatusVersion && SelectedLinklyEnvironment == environment)
            {
                HasSavedLinklyCloudSecret = hasSecret;
            }
        }
        catch (Exception ex)
        {
            LogSquareSettings($"refresh linkly cloud secret status failed environment={environment} message={LogValue(ex.Message)}");
        }
    }

    private static void ApplyLinklyCloudCredentialFields(
        LinklyCloudCredentialSettings credential,
        Action<string> setUsername,
        Action<string> setPassword,
        Action<bool> setHasSavedPassword)
    {
        setUsername(credential.Username ?? string.Empty);
        setPassword(string.Empty);
        setHasSavedPassword(credential.HasProtectedPassword);
    }

    private void ClearLinklyCloudCredentialFields()
    {
        LinklyCloudUsernameText = string.Empty;
        LinklyCloudPasswordText = string.Empty;
        HasSavedLinklyCloudPassword = false;
    }

    private async Task LoadLinklyCloudCredentialFieldsAsync(CardTerminalEnvironment environment)
    {
        try
        {
            var credential = await _setupService.LoadLinklyCloudCredentialAsync(environment);
            ApplyLinklyCloudCredentialFields(
                credential,
                username => LinklyCloudUsernameText = username,
                password => { },
                hasSavedPassword => HasSavedLinklyCloudPassword = hasSavedPassword);
        }
        catch (Exception ex)
        {
            LogSquareSettings($"load linkly cloud credential failed environment={environment} message={LogValue(ex.Message)}");
            ClearLinklyCloudCredentialFields();
        }
    }

    private async Task RefreshLinklyCloudCredentialStatusAsync(CardTerminalEnvironment environment)
    {
        var version = Interlocked.Increment(ref _linklyCredentialStatusVersion);
        var editVersion = _linklyCredentialEditVersion;
        try
        {
            var credential = await _setupService.LoadLinklyCloudCredentialAsync(environment);
            // 只有环境和编辑版本都未变化时，才回写异步加载结果，避免覆盖用户正在输入的内容。
            if (version == _linklyCredentialStatusVersion &&
                editVersion == _linklyCredentialEditVersion &&
                SelectedLinklyEnvironment == environment)
            {
                ApplyLinklyCloudCredentialFields(
                    credential,
                    username => LinklyCloudUsernameText = username,
                    password => { },
                    hasSavedPassword => HasSavedLinklyCloudPassword = hasSavedPassword);
            }
        }
        catch (Exception ex)
        {
            LogSquareSettings($"refresh linkly cloud credential status failed environment={environment} message={LogValue(ex.Message)}");
            if (version == _linklyCredentialStatusVersion &&
                editVersion == _linklyCredentialEditVersion &&
                SelectedLinklyEnvironment == environment)
            {
                ClearLinklyCloudCredentialFields();
            }
        }
    }

    partial void OnSelectedSquareLocationChanged(SquareLocationOption? value)
    {
        if (_syncingSquare) return;
        LogSquareSettings($"selected location changed locationId={LogValue(value?.Id)}");
        if (!string.Equals(_devicesLoadedForLocationId, value?.Id, StringComparison.OrdinalIgnoreCase))
        {
            SquareDevices.Clear();
            SelectedSquareDevice = null;
            _devicesLoadedForLocationId = null;
        }

        ResetSquareDeviceCodes();
        RaiseCommandStates();
    }

    partial void OnSelectedSquareDeviceChanged(SquareDeviceOption? value)
    {
        if (_syncingSquare) return;
        SuggestSquareDeviceCodeName(force: false);
        LogSquareSettings($"selected device changed deviceId={LogValue(value?.Id)}");
        if (!IsBusy &&
            value is not null &&
            SelectedSquareLocation is not null &&
            string.Equals(_devicesLoadedForLocationId, SelectedSquareLocation.Id, StringComparison.OrdinalIgnoreCase) &&
            !SquareDeviceIdNormalizer.AreEquivalent(value.Id, _savedSquareDeviceId))
        {
            SetStatus("settings.status.squareDeviceSwitchPendingSave", value.Name);
        }

        RaiseCommandStates();
    }

    partial void OnSelectedSquareDeviceCodeChanged(SquareDeviceCodeOption? value)
    {
        LogSquareSettings($"selected device code changed deviceCodeId={LogValue(value?.Id)} status={LogValue(value?.Status)}");
        RaiseCommandStates();
    }

    partial void OnSquareDeviceCodeNameTextChanged(string value)
    {
        if (_syncingSquare) return;
        SyncSquareInputs();
        RaiseCommandStates();
    }

    partial void OnLinklyHostTextChanged(string value)
    {
        if (_syncingLinkly) return;
        SyncLinklyInputs();
        ResetLinklyConnectionTest();
    }

    partial void OnLinklyPortTextChanged(string value)
    {
        if (_syncingLinkly) return;
        SyncLinklyInputs();
        ResetLinklyConnectionTest();
    }

    /// <summary>本地 IP 模式是否正在使用 Linkly 官方推荐的本机 EFT-Client 地址（127.0.0.1:2011）。</summary>
    public bool IsLinklyRecommendedAddress =>
        string.Equals(LinklyHostText?.Trim(), CardTerminalConfiguration.Default.LinklyHost, StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(LinklyPortText?.Trim(), out var port) &&
        port == CardTerminalConfiguration.Default.LinklyPort;

    // 一键恢复官方推荐地址；与手动输入一样需要点保存才会写入配置。
    [RelayCommand]
    private void UseRecommendedLinklyAddress()
    {
        LinklyHostText = CardTerminalConfiguration.Default.LinklyHost;
        LinklyPortText = CardTerminalConfiguration.Default.LinklyPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    partial void OnTimeoutSecondsTextChanged(string value)
    {
        if (_syncingLinkly) return;
        SyncLinklyInputs();
        ResetLinklyConnectionTest();
    }

    partial void OnLinklyConnectionSucceededChanged(bool value)
    {
        RaiseCommandStates();
    }

    partial void OnLinklyPinPadLoggedOnChanged(bool? value)
    {
        RaiseCommandStates();
    }

    partial void OnIsBusyChanged(bool value)
    {
        RaiseCommandStates();
        OnPropertyChanged(nameof(CanChangeEnvironment));
        OnPropertyChanged(nameof(CanRefreshLinklyCloudTerminals));
        foreach (var line in LinklyCloudLines)
        {
            line.RefreshCommandStates();
        }
    }

    private void RaiseCommandStates()
    {
        SavePaymentMethodsCommand.NotifyCanExecuteChanged();
        LoadLocationsCommand.NotifyCanExecuteChanged();
        LoadDevicesCommand.NotifyCanExecuteChanged();
        SaveSquareCommand.NotifyCanExecuteChanged();
        LoadDeviceCodesCommand.NotifyCanExecuteChanged();
        CreateDeviceCodeCommand.NotifyCanExecuteChanged();
        RefreshDeviceCodeStatusCommand.NotifyCanExecuteChanged();
        PairLinklyCloudCommand.NotifyCanExecuteChanged();
        SaveLinklyCloudCredentialCommand.NotifyCanExecuteChanged();
        CancelLinklyCloudPairingCommand.NotifyCanExecuteChanged();
        TestLinklyCommand.NotifyCanExecuteChanged();
        LogonLinklyCommand.NotifyCanExecuteChanged();
        TestLinklyTransactionStatusCommand.NotifyCanExecuteChanged();
        TestLinklyCloudTerminalCommand.NotifyCanExecuteChanged();
        UseLinklyCloudTerminalCommand.NotifyCanExecuteChanged();
        ChangeLinklyCloudTerminalAssignmentCommand.NotifyCanExecuteChanged();
        UnassignLinklyCloudTerminalCommand.NotifyCanExecuteChanged();
        SaveLinklyCommand.NotifyCanExecuteChanged();
        MoveLinklyPriorityUpCommand.NotifyCanExecuteChanged();
        MoveLinklyPriorityDownCommand.NotifyCanExecuteChanged();
        SaveReceiptPrinterCommand.NotifyCanExecuteChanged();
        TestReceiptPrinterCommand.NotifyCanExecuteChanged();
        DownloadCatalogCommand.NotifyCanExecuteChanged();
        ResetCatalogCommand.NotifyCanExecuteChanged();
        ResetTestSalesDataCommand.NotifyCanExecuteChanged();
        ReregisterDeviceCommand.NotifyCanExecuteChanged();
        CheckForAppUpdateCommand.NotifyCanExecuteChanged();
        InstallRemoteMaintenanceCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSaveLinklyCloudCredentialFromView));
        OnPropertyChanged(nameof(CanPairLinklyCloudFromView));
        OnPropertyChanged(nameof(CanCancelLinklyCloudPairingFromView));
    }

    private void SyncSquareState()
    {
        _syncingSquare = true;
        // Non-triggering fields first.
        HasSavedSquareToken = _squareState.HasSavedToken;
        SquareDeviceCodeNameText = _squareState.DeviceCodeNameText;
        _savedSquareLocationId = _squareState.SavedLocationId;
        _savedSquareDeviceId = _squareState.SavedDeviceId;
        _devicesLoadedForLocationId = _squareState.DevicesLoadedForLocationId;
        _lastSquareDeviceCodeNameSuggestion = _squareState.LastDeviceCodeNameSuggestion;
        _loadedConfiguration = _squareState.LoadedConfiguration;
        _linklyState.LoadedConfiguration = _squareState.LoadedConfiguration;
        // Triggering fields last.
        SelectedSquareLocation = _squareState.SelectedLocation;
        SelectedSquareDevice = _squareState.SelectedDevice;
        SelectedSquareDeviceCode = _squareState.SelectedDeviceCode;
        _syncingSquare = false;
        RaiseActivePaymentProviderProperties();
    }

    private void SyncSquareInputs()
    {
        if (_syncingSquare) return;
        _squareState.HasSavedToken = HasSavedSquareToken;
        _squareState.DeviceCodeNameText = SquareDeviceCodeNameText;
        _squareState.SavedLocationId = _savedSquareLocationId;
        _squareState.SavedDeviceId = _savedSquareDeviceId;
        _squareState.DevicesLoadedForLocationId = _devicesLoadedForLocationId;
        _squareState.LastDeviceCodeNameSuggestion = _lastSquareDeviceCodeNameSuggestion;
        _squareState.LoadedConfiguration = _loadedConfiguration;
        _squareState.SelectedLocation = SelectedSquareLocation;
        _squareState.SelectedDevice = SelectedSquareDevice;
        _squareState.SelectedDeviceCode = SelectedSquareDeviceCode;
    }

    private void SyncLinklyState()
    {
        _syncingLinkly = true;
        // Set non-triggering fields first to avoid re-entrant SyncLinklyInputs() overwrites.
        HasSavedLinklyCloudPassword = _linklyState.HasSavedCloudPassword;
        HasSavedLinklyCloudSecret = _linklyState.HasSavedCloudSecret;
        LinklyConnectionSucceeded = _linklyState.ConnectionSucceeded;
        LinklyPinPadLoggedOn = _linklyState.PinPadLoggedOn;
        LinklyPairCodeText = _linklyState.PairCodeText;
        _loadedConfiguration = _linklyState.LoadedConfiguration;
        _linklySecretStatusVersion = _linklyState.SecretStatusVersion;
        _linklyCredentialStatusVersion = _linklyState.CredentialStatusVersion;
        _linklyCredentialEditVersion = _linklyState.CredentialEditVersion;
        _hasLinklyCloudPasswordInput = _linklyState.HasCloudPasswordInput;
        // Triggering fields last — their On*Changed handlers may call SyncLinklyInputs back.
        SelectedLinklyMode = _linklyState.SelectedMode;
        IsLinklySandbox = _linklyState.IsSandbox;
        LinklyHostText = _linklyState.HostText;
        LinklyPortText = _linklyState.PortText;
        TimeoutSecondsText = _linklyState.TimeoutSecondsText;
        LinklyCloudUsernameText = _linklyState.CloudUsernameText;
        LinklyCloudPasswordText = _linklyState.CloudPasswordText;
        _squareState.LoadedConfiguration = _linklyState.LoadedConfiguration;
        _syncingLinkly = false;
        RaiseActivePaymentProviderProperties();
        RaiseLinklyTestPresentationProperties();
    }

    private void SyncLinklyInputs()
    {
        if (_syncingLinkly) return;
        _linklyState.HasSavedCloudPassword = HasSavedLinklyCloudPassword;
        _linklyState.HasSavedCloudSecret = HasSavedLinklyCloudSecret;
        _linklyState.ConnectionSucceeded = LinklyConnectionSucceeded;
        _linklyState.PinPadLoggedOn = LinklyPinPadLoggedOn;
        _linklyState.PairCodeText = LinklyPairCodeText;
        _linklyState.LoadedConfiguration = _loadedConfiguration;
        _linklyState.SecretStatusVersion = _linklySecretStatusVersion;
        _linklyState.CredentialStatusVersion = _linklyCredentialStatusVersion;
        _linklyState.CredentialEditVersion = _linklyCredentialEditVersion;
        _linklyState.HasCloudPasswordInput = _hasLinklyCloudPasswordInput;
        _linklyState.SelectedMode = SelectedLinklyMode;
        _linklyState.IsSandbox = IsLinklySandbox;
        _linklyState.HostText = LinklyHostText;
        _linklyState.PortText = LinklyPortText;
        _linklyState.TimeoutSecondsText = TimeoutSecondsText;
        _linklyState.CloudUsernameText = LinklyCloudUsernameText;
        _linklyState.CloudPasswordText = LinklyCloudPasswordText;
    }

    private void ResetLinklyConnectionTest()
    {
        _linklyCoordinator.ResetConnectionTest(_linklyState);
        LinklyConnectionSucceeded = _linklyState.ConnectionSucceeded;
        LinklyPinPadLoggedOn = _linklyState.PinPadLoggedOn;
    }

    private void SetStatus(string key, params object[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        _statusOverride = null;
        StatusMessage = Format(key, args);
    }

    private void SetStatusOverride(string statusText)
    {
        _statusOverride = statusText;
        StatusMessage = statusText;
    }

    private void SetLinklyTestStatus(string key, params object[] args)
    {
        _linklyTestStatusKey = key;
        _linklyTestStatusArgs = args;
        _linklyTestStatusOverride = null;
        LinklyTestStatusMessage = Format(key, args);
    }

    private void SetLinklyTestValidationStatus(string key)
    {
        SetStatus(key);
        SetLinklyTestStatus(key);
    }

    private (LinklySettingsMode Mode, CardTerminalEnvironment Environment, string Host, string Port, string Timeout)
        CaptureLinklyTestInputs()
    {
        return (PrimaryLinklyMode, SelectedLinklyEnvironment, LinklyHostText, LinklyPortText, TimeoutSecondsText);
    }

    private void RaiseLinklyTestPresentationProperties()
    {
        OnPropertyChanged(nameof(IsLinklySandboxLocalIpMode));
        OnPropertyChanged(nameof(LinklyTestActionText));
    }

    private void SetLinklyTestStatusOverride(string statusText)
    {
        _linklyTestStatusOverride = statusText;
        LinklyTestStatusMessage = statusText;
    }

    private void ClearLinklyTestStatus()
    {
        _linklyTestStatusKey = null;
        _linklyTestStatusArgs = [];
        _linklyTestStatusOverride = null;
        LinklyTestStatusMessage = string.Empty;
    }

    private void RefreshLocalizedMessages()
    {
        StatusMessage = _statusOverride ?? Format(_statusKey, _statusArgs);
        LinklyTestStatusMessage = _linklyTestStatusOverride
            ?? (_linklyTestStatusKey is null ? string.Empty : Format(_linklyTestStatusKey, _linklyTestStatusArgs));
        if (_receiptPrinterTestStatusOverride is not null)
        {
            ReceiptPrinterTestStatusMessage = _receiptPrinterTestStatusOverride;
        }
    }

    private string T(string key)
    {
        return _localization?.T(key) ?? LocalizationResourceProvider.Instance[key];
    }

    private string Format(string key, params object[] args)
    {
        var template = T(key);
        if (args.Length == 0)
        {
            return template;
        }

        var culture = _localization?.CurrentCulture ?? System.Globalization.CultureInfo.CurrentCulture;
        return string.Format(culture, template, args);
    }

    private static string NormalizeHost(string? host)
    {
        return string.IsNullOrWhiteSpace(host) ? CardTerminalConfiguration.Default.LinklyHost : host.Trim();
    }

    private static int ParsePort(string? text)
    {
        return int.TryParse(text, out var port) && port is > 0 and <= 65535
            ? port
            : CardTerminalConfiguration.Default.LinklyPort;
    }

    private static int ParseTimeoutSeconds(string? text)
    {
        return int.TryParse(text, out var seconds) && seconds > 0
            ? seconds
            : CardTerminalConfiguration.Default.TerminalTimeoutSeconds;
    }

    private static LinklySettingsMode ToSettingsMode(LinklyConnectionMode mode)
    {
        return CardTerminalSettings.NormalizeLinklyConnectionMode(mode) switch
        {
            LinklyConnectionMode.CloudDirectSync => LinklySettingsMode.CloudDirectSync,
            LinklyConnectionMode.CloudBackendAsync => LinklySettingsMode.CloudBackendAsync,
            _ => LinklySettingsMode.LocalIp
        };
    }

    private static string GetLinklyModeLocalizationKey(LinklySettingsMode mode)
    {
        return mode switch
        {
            LinklySettingsMode.CloudDirectSync => "settings.linkly.mode.cloudDirectSync",
            LinklySettingsMode.CloudBackendAsync => "settings.linkly.mode.cloudBackendAsync",
            _ => "settings.linkly.mode.localIp"
        };
    }

    private static LinklyConnectionMode ToConnectionMode(LinklySettingsMode mode)
    {
        return mode switch
        {
            LinklySettingsMode.CloudDirectSync => LinklyConnectionMode.CloudDirectSync,
            LinklySettingsMode.CloudBackendAsync => LinklyConnectionMode.CloudBackendAsync,
            _ => LinklyConnectionMode.LocalIp
        };
    }

    private void ResetLinklyModePriority(IReadOnlyList<LinklyConnectionMode>? priority)
    {
        _linklyCoordinator.ResetModePriority(priority, LinklyModePriorityItems, _linklyState);
        OnPropertyChanged(nameof(PrimaryLinklyMode));
        MoveLinklyPriorityUpCommand.NotifyCanExecuteChanged();
        MoveLinklyPriorityDownCommand.NotifyCanExecuteChanged();
    }

    private void PromoteLinklyModeToPrimary(LinklySettingsMode mode)
    {
        _linklyCoordinator.PromoteModeToPrimary(mode, LinklyModePriorityItems);
        OnPropertyChanged(nameof(PrimaryLinklyMode));
    }

    private IReadOnlyList<LinklyConnectionMode> GetLinklyConnectionModePriority()
    {
        return _linklyCoordinator.GetConnectionModePriority(LinklyModePriorityItems);
    }

    private void RefreshLinklyPriorityRanks()
    {
        _linklyCoordinator.RefreshPriorityRanks(LinklyModePriorityItems);
        MoveLinklyPriorityUpCommand.NotifyCanExecuteChanged();
        MoveLinklyPriorityDownCommand.NotifyCanExecuteChanged();
    }

    private void ResetSquareDeviceCodes()
    {
        SyncSquareInputs();
        _squareCoordinator.ResetDeviceCodes(SquareDeviceCodes, _squareState);
        SyncSquareState();
    }

    private void SuggestSquareDeviceCodeName(bool force)
    {
        SyncSquareInputs();
        _squareCoordinator.SuggestDeviceCodeName(force, _squareState);
        SyncSquareState();
    }

    private static string ResolveSettingsLogCategory(string operationName)
    {
        return operationName.Contains("linkly", StringComparison.OrdinalIgnoreCase) ? "LinklySettings" : "Square";
    }

    private static void LogSquareSettings(string message)
    {
        ConsoleLog.Write("Square", $"settings ui {message}");
    }

    private static void LogLinklyCloudSettings(string message)
    {
        LinklyJsonLog.WriteMessage("LinklyCloud", "settings-ui", $"settings ui {message}");
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value;
    }
}
