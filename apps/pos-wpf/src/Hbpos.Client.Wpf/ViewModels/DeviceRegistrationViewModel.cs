using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Devices;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class DeviceRegistrationViewModel : ObservableObject, IScannerInputTarget, IDisposable
{
    public const string PageId = "DeviceActivation";

    private const int PendingDeviceStatus = -1;
    private const string InvalidActivationCodeError = "ACTIVATION_CODE_INVALID_FORMAT";
    private static readonly TimeSpan DefaultApprovalPollingInterval = TimeSpan.FromSeconds(5);

    private readonly IDeviceRegistrationWorkflowService _workflowService;
    private readonly ILocalizationService? _localization;
    private readonly ApiServerSettingsViewModel? _apiServerSettings;
    private readonly IRawScannerService? _rawScannerService;
    private readonly TimeSpan _approvalPollingInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private string? _excludedStoreCode;
    private PendingRegistrationState? _pendingRegistration;
    private CancellationTokenSource? _approvalPollingCancellation;
    private CancellationTokenSource? _manualVerificationCancellation;
    private CancellationTokenSource? _registrationActionCancellation;
    private CancellationTokenSource? _storeLoadCancellation;
    private Task? _approvalPollingTask;
    private long _registrationSessionVersion;
    private bool _isReregisterCancelRequested;
    private string? _previewedActivationCode;
    private bool _disposed;

    [ObservableProperty]
    private StoreSelectionItem? _selectedStore;

    [ObservableProperty]
    private string _hardwareId = string.Empty;

    [ObservableProperty]
    private string _deviceCode = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasPendingRegistration;

    [ObservableProperty]
    private bool _isReregisterMode;

    [ObservableProperty]
    private bool _canCancel;

    [ObservableProperty]
    private string _currentStoreCode = string.Empty;

    [ObservableProperty]
    private string _activationCode = string.Empty;

    [ObservableProperty]
    private bool _isActivationRecoveryPending;

    [ObservableProperty]
    private bool _hasActivationPreview;

    [ObservableProperty]
    private string _previewStoreCode = string.Empty;

    [ObservableProperty]
    private string _previewStoreName = string.Empty;

    [ObservableProperty]
    private string _previewDeviceSystem = string.Empty;

    [ObservableProperty]
    private DateTimeOffset? _previewExpiresAtUtc;

    [ObservableProperty]
    private bool _isLegacyPendingRegistration;

    /// <summary>
    /// 最近一次激活请求是因为网络问题（超时 / 连不上）失败的：注册页在错误提示旁给出「检测网络」入口，
    /// 业务拒绝（开通码无效等）不显示。
    /// </summary>
    [ObservableProperty]
    private bool _hasNetworkProblem;

    public DeviceRegistrationViewModel(
        IDeviceApiClient deviceApiClient,
        ILocalDeviceRepository deviceRepository,
        IDeviceFingerprintService fingerprintService,
        ILocalizationService? localization = null,
        TimeSpan? approvalPollingInterval = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        ApiServerSettingsViewModel? apiServerSettings = null,
        IRawScannerService? rawScannerService = null)
        : this(
            new DeviceRegistrationWorkflowService(deviceApiClient, deviceRepository, fingerprintService, localization),
            localization,
            approvalPollingInterval,
            delayAsync,
            apiServerSettings,
            rawScannerService)
    {
    }

    public DeviceRegistrationViewModel(
        IDeviceRegistrationWorkflowService workflowService,
        ILocalizationService? localization = null,
        TimeSpan? approvalPollingInterval = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        ApiServerSettingsViewModel? apiServerSettings = null,
        IRawScannerService? rawScannerService = null)
    {
        _workflowService = workflowService;
        _localization = localization;
        _apiServerSettings = apiServerSettings;
        _rawScannerService = rawScannerService;
        _approvalPollingInterval = approvalPollingInterval ?? DefaultApprovalPollingInterval;
        _delayAsync = delayAsync ?? Task.Delay;
        _apiServerSettings?.Load();
        if (_localization is not null)
        {
            _localization.CultureChanged += OnCultureChanged;
        }

        DiagnoseNetworkCommand = new RelayCommand(
            () => _apiServerSettings?.Network?.DiagnoseCommand.Execute(null),
            () => _apiServerSettings?.Network is not null);
        RegisterCommand = new AsyncRelayCommand(RegisterAsync, CanRegister);
        VerifyCommand = new AsyncRelayCommand(VerifyAsync, CanVerify);
        PreviewActivationCommand = new AsyncRelayCommand(PreviewActivationAsync, CanPreviewActivation);
        ConfirmActivationCommand = new AsyncRelayCommand(ConfirmActivationAsync, CanConfirmActivation);
        CancelCommand = new RelayCommand(Cancel, CanExecuteCancel);
        if (_apiServerSettings is not null)
        {
            PropertyChangedEventManager.AddHandler(
                _apiServerSettings,
                OnApiServerSettingsPropertyChanged,
                nameof(ApiServerSettingsViewModel.RestartRequired));
        }

        _rawScannerService?.Subscribe(PageId, OnRawBarcodeScanned);
    }

    public ObservableCollection<StoreSelectionItem> Stores { get; } = [];

    public IAsyncRelayCommand RegisterCommand { get; }

    public IAsyncRelayCommand VerifyCommand { get; }

    public IAsyncRelayCommand PreviewActivationCommand { get; }

    public IAsyncRelayCommand ConfirmActivationCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public ApiServerSettingsViewModel ApiServerSettings =>
        _apiServerSettings ?? throw new InvalidOperationException("API server settings are not configured.");

    public string ScannerPageId => PageId;

    public bool IsActivationCodeMode => !IsLegacyPendingRegistration;

    public bool CanEditServerSettings => !IsActivationRecoveryPending;

    public string PreviewStoreDisplay => string.IsNullOrWhiteSpace(PreviewStoreCode)
        ? PreviewStoreName
        : $"{PreviewStoreName} ({PreviewStoreCode})";

    public string StoreTransitionDisplay => IsReregisterMode && !string.IsNullOrWhiteSpace(CurrentStoreCode)
        ? $"{CurrentStoreCode} → {PreviewStoreDisplay}"
        : PreviewStoreDisplay;

    public string PreviewExpiryText => PreviewExpiresAtUtc is null
        ? string.Empty
        : PreviewExpiresAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    internal Task? ApprovalPollingTask => _approvalPollingTask;

    public string TitleText => IsReregisterMode
        ? T("deviceRegistration.title.reregister", "Reregister Device to Another Store")
        : T("deviceRegistration.title", "Device Registration");

    public string RegisterButtonText => IsReregisterMode
        ? T("deviceRegistration.submit.reregister", "Submit Store Switch Reregistration")
        : IsPendingRegistrationStoreSwitch
            ? T("deviceRegistration.submit.switch", "Submit Store Switch Registration")
            : T("deviceRegistration.submit", "Submit Registration");

    public string ActivationPreviewButtonText => T(
        "deviceActivation.preview",
        "Check activation code");

    public string ActivationConfirmButtonText => IsReregisterMode
        ? T("deviceActivation.confirm.rebind", "Confirm store change")
        : T("deviceActivation.confirm.redeem", "Activate device");

    private bool IsPendingRegistrationStoreSwitch =>
        !IsReregisterMode &&
        _pendingRegistration is not null &&
        SelectedStore is not null &&
        !string.Equals(SelectedStore.StoreCode, _pendingRegistration.StoreCode, StringComparison.OrdinalIgnoreCase);

    public event EventHandler<DeviceActivatedEventArgs>? DeviceActivated;

    public event Func<object?, DeviceActivatedEventArgs, Task>? DeviceActivatedAsync;

    public event EventHandler<DeviceReregisteredEventArgs>? DeviceReregistered;

    public event EventHandler? CancelRequested;

    public async Task InitializeAsync(LocalDeviceCache? cachedDevice, CancellationToken cancellationToken = default)
    {
        Prepare(cachedDevice);
        await ResumeActivationOrLoadLegacyAsync(cachedDevice, cancellationToken);
    }

    public void Prepare(LocalDeviceCache? cachedDevice)
    {
        CancelStoreLoad();
        StopApprovalPolling();
        IsReregisterMode = false;
        CanCancel = false;
        _isReregisterCancelRequested = false;
        _excludedStoreCode = null;
        CurrentStoreCode = string.Empty;
        HardwareId = _workflowService.GetHardwareId();
        Stores.Clear();
        SelectedStore = null;
        _pendingRegistration = null;
        IsActivationRecoveryPending = false;
        ClearActivationPreview(clearCode: true);
        IsLegacyPendingRegistration = cachedDevice?.DeviceStatus == PendingDeviceStatus;

        if (cachedDevice is not null)
        {
            DeviceCode = cachedDevice.DeviceCode;
            HasPendingRegistration = cachedDevice.DeviceStatus == PendingDeviceStatus;
            if (cachedDevice.DeviceStatus == PendingDeviceStatus)
            {
                _pendingRegistration = new PendingRegistrationState(
                    cachedDevice.StoreCode,
                    cachedDevice.DeviceCode,
                    cachedDevice.Message ?? string.Empty);
            }
        }
        else
        {
            DeviceCode = string.Empty;
            HasPendingRegistration = false;
        }

        StatusMessage = IsLegacyPendingRegistration
            ? T("deviceRegistration.status.loadingStores", DeviceRegistrationWorkflowService.LoadingStoresMessage)
            : T("deviceActivation.status.scanOrEnter", "Scan or enter the one-time activation code.");
        NotifyCommandState();
    }

    public void PrepareReregister(string currentStoreCode)
    {
        CancelStoreLoad();
        StopApprovalPolling();
        IsReregisterMode = true;
        CanCancel = true;
        _isReregisterCancelRequested = false;
        _excludedStoreCode = currentStoreCode;
        CurrentStoreCode = currentStoreCode.Trim();
        _pendingRegistration = null;
        HardwareId = _workflowService.GetHardwareId();
        Stores.Clear();
        SelectedStore = null;
        DeviceCode = string.Empty;
        HasPendingRegistration = false;
        IsLegacyPendingRegistration = false;
        IsActivationRecoveryPending = false;
        ClearActivationPreview(clearCode: true);
        StatusMessage = T("deviceActivation.status.scanOrEnterRebind", "Scan or enter the target store activation code.");
        NotifyCommandState();
    }

    public async Task ResumeActivationOrLoadLegacyAsync(
        LocalDeviceCache? cachedDevice,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        IsBusy = true;
        var managesRecoveryLock = !IsActivationRecoveryPending;
        try
        {
            if (managesRecoveryLock)
            {
                IsActivationRecoveryPending =
                    await _workflowService.GetPendingActivationRecoveryModeAsync(cancellationToken) is not null;
            }

            var recovery = await _workflowService.RecoverActivationCodeAsync(HardwareId, cancellationToken);
            if (recovery is not null)
            {
                IsActivationRecoveryPending = true;
                IsReregisterMode = recovery.Mode == DeviceActivationRecoveryMode.Rebind;
                CanCancel = false;
                StatusMessage = T("deviceActivation.status.recovering", "Recovering an interrupted device activation...");
                await PersistResultAsync(recovery.ActionResult, cancellationToken);
                IsActivationRecoveryPending = false;
                ActivationCode = string.Empty;
                ClearActivationPreview(clearCode: false);
                await ApplyActionResultAsync(recovery.ActionResult);
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (managesRecoveryLock)
            {
                await RefreshActivationRecoveryLockAsync();
            }

            return;
        }
        catch (Exception ex)
        {
            if (managesRecoveryLock)
            {
                await RefreshActivationRecoveryLockAsync();
            }

            ShowActivationError(ex);
            return;
        }
        finally
        {
            if (!_disposed)
            {
                IsBusy = false;
            }
        }

        if (IsLegacyPendingRegistration)
        {
            await LoadStoresAsync(cachedDevice, cancellationToken);
            return;
        }

        StatusMessage = IsReregisterMode
            ? T("deviceActivation.status.scanOrEnterRebind", "Scan or enter the target store activation code.")
            : T("deviceActivation.status.scanOrEnter", "Scan or enter the one-time activation code.");
    }

    public async Task LoadStoresAsync(LocalDeviceCache? cachedDevice, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        var loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previousLoadCancellation = _storeLoadCancellation;
        _storeLoadCancellation = loadCancellation;
        CancelAndDispose(previousLoadCancellation);
        IsBusy = true;
        StatusMessage = T("deviceRegistration.status.loadingStores", DeviceRegistrationWorkflowService.LoadingStoresMessage);

        try
        {
            var result = await _workflowService.LoadStoresAsync(
                cachedDevice,
                IsReregisterMode,
                _excludedStoreCode,
                loadCancellation.Token);
            if (!IsCurrentStoreLoad(loadCancellation))
            {
                return;
            }

            ApplyLoadResult(result);
        }
        catch (OperationCanceledException) when (loadCancellation.IsCancellationRequested)
        {
            // 页面已关闭、服务器已切换或新一代加载已接管，不再回写旧请求状态。
        }
        catch (Exception ex)
        {
            if (IsCurrentStoreLoad(loadCancellation))
            {
                ShowFailure(ex);
            }
        }
        finally
        {
            if (ReferenceEquals(_storeLoadCancellation, loadCancellation))
            {
                _storeLoadCancellation = null;
                loadCancellation.Dispose();
                if (!_disposed)
                {
                    IsBusy = false;
                    NotifyCommandState();
                }
            }
        }
    }

    partial void OnIsReregisterModeChanged(bool value)
    {
        RaiseLocalizedProperties();
        OnPropertyChanged(nameof(StoreTransitionDisplay));
    }

    partial void OnCurrentStoreCodeChanged(string value) => OnPropertyChanged(nameof(StoreTransitionDisplay));

    partial void OnIsLegacyPendingRegistrationChanged(bool value)
    {
        OnPropertyChanged(nameof(IsActivationCodeMode));
        NotifyCommandState();
    }

    /// <summary>显示激活错误，并记下它是不是网络问题（决定是否出现「检测网络」按钮）。</summary>
    private void ShowActivationError(Exception exception)
    {
        StatusMessage = ResolveActivationError(exception);
        HasNetworkProblem = exception is OperationCanceledException or System.Net.Http.HttpRequestException;
    }

    /// <summary>门店列表 / 注册 / 验证失败：沿用原消息，同时记下是不是网络问题。</summary>
    private void ShowFailure(Exception exception)
    {
        StatusMessage = exception.Message;
        HasNetworkProblem = exception is OperationCanceledException or System.Net.Http.HttpRequestException;
    }

    public IRelayCommand DiagnoseNetworkCommand { get; }

    partial void OnActivationCodeChanged(string value)
    {
        // 改了开通码就是重新来：旧的网络错误入口不再适用。
        HasNetworkProblem = false;
        if (_previewedActivationCode is not null
            && (!DeviceActivationCodeNormalizer.TryNormalize(value, out var normalized)
                || !string.Equals(normalized, _previewedActivationCode, StringComparison.Ordinal)))
        {
            ClearActivationPreview(clearCode: false);
        }

        NotifyCommandState();
    }

    partial void OnHasActivationPreviewChanged(bool value)
    {
        NotifyCommandState();
    }

    partial void OnIsActivationRecoveryPendingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditServerSettings));
        NotifyCommandState();
    }

    partial void OnPreviewStoreCodeChanged(string value)
    {
        OnPropertyChanged(nameof(PreviewStoreDisplay));
        OnPropertyChanged(nameof(StoreTransitionDisplay));
    }

    partial void OnPreviewStoreNameChanged(string value)
    {
        OnPropertyChanged(nameof(PreviewStoreDisplay));
        OnPropertyChanged(nameof(StoreTransitionDisplay));
    }

    partial void OnPreviewExpiresAtUtcChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(PreviewExpiryText));

    partial void OnSelectedStoreChanged(StoreSelectionItem? value)
    {
        StopApprovalPolling();
        ApplyPendingRegistrationSelection(value);
        OnPropertyChanged(nameof(RegisterButtonText));
        NotifyCommandState();
    }

    partial void OnIsBusyChanged(bool value)
    {
        NotifyCommandState();
    }

    partial void OnCanCancelChanged(bool value)
    {
        NotifyCommandState();
    }

    private async Task PreviewActivationAsync()
    {
        if (!CanPreviewActivation()
            || !DeviceActivationCodeNormalizer.TryNormalize(ActivationCode, out var normalizedCode))
        {
            StatusMessage = T(
                "deviceActivation.status.invalidFormat",
                "Scan or enter the complete HBDEV1 activation code.");
            return;
        }

        ActivationCode = normalizedCode;
        StopApprovalPolling();
        var actionCancellation = new CancellationTokenSource();
        var actionToken = actionCancellation.Token;
        _registrationActionCancellation = actionCancellation;
        var sessionVersion = _registrationSessionVersion;
        IsBusy = true;
        ClearActivationPreview(clearCode: false);
        try
        {
            HasNetworkProblem = false;
            StatusMessage = T("deviceActivation.status.previewing", "Checking activation code...");
            var preview = await _workflowService.PreviewActivationCodeAsync(normalizedCode, actionToken);
            if (!IsCurrentActivationAction(sessionVersion, actionCancellation, normalizedCode))
            {
                return;
            }

            _previewedActivationCode = normalizedCode;
            PreviewStoreCode = preview.StoreCode;
            PreviewStoreName = preview.StoreName;
            PreviewDeviceSystem = preview.DeviceSystem;
            PreviewExpiresAtUtc = preview.ExpiresAtUtc;
            HasActivationPreview = true;
            StatusMessage = T(
                IsReregisterMode
                    ? "deviceActivation.status.confirmRebind"
                    : "deviceActivation.status.confirmRedeem",
                IsReregisterMode
                    ? "Confirm the target store before changing this device."
                    : "Confirm the store before activating this device.");
        }
        catch (OperationCanceledException) when (actionToken.IsCancellationRequested)
        {
            // 新扫码、服务器切换或页面关闭已经接管，不回写旧预览。
        }
        catch (Exception ex)
        {
            if (IsCurrentActivationAction(sessionVersion, actionCancellation, normalizedCode))
            {
                ShowActivationError(ex);
            }
        }
        finally
        {
            CompleteRegistrationAction(actionCancellation);
        }
    }

    private async Task ConfirmActivationAsync()
    {
        if (!CanConfirmActivation()
            || _previewedActivationCode is null
            || !DeviceActivationCodeNormalizer.TryNormalize(ActivationCode, out var normalizedCode)
            || !string.Equals(normalizedCode, _previewedActivationCode, StringComparison.Ordinal))
        {
            return;
        }

        StopApprovalPolling();
        var actionCancellation = new CancellationTokenSource();
        var actionToken = actionCancellation.Token;
        _registrationActionCancellation = actionCancellation;
        var sessionVersion = _registrationSessionVersion;
        IsBusy = true;
        IsActivationRecoveryPending = true;
        try
        {
            HasNetworkProblem = false;
            StatusMessage = T(
                IsReregisterMode
                    ? "deviceActivation.status.rebinding"
                    : "deviceActivation.status.redeeming",
                IsReregisterMode ? "Changing this device store..." : "Activating this device...");
            var result = IsReregisterMode
                ? await _workflowService.RebindActivationCodeAsync(normalizedCode, HardwareId, actionToken)
                : await _workflowService.RedeemActivationCodeAsync(normalizedCode, HardwareId, actionToken);
            if (_isReregisterCancelRequested
                || !IsCurrentActivationAction(sessionVersion, actionCancellation, normalizedCode))
            {
                return;
            }

            await PersistResultAsync(result, actionToken);
            if (_isReregisterCancelRequested
                || !IsCurrentActivationAction(sessionVersion, actionCancellation, normalizedCode))
            {
                return;
            }

            // 完整设备凭据已经安全落盘，才从内存清除一次性开通码。
            IsActivationRecoveryPending = false;
            ActivationCode = string.Empty;
            ClearActivationPreview(clearCode: false);
            await ApplyActionResultAsync(result);
        }
        catch (OperationCanceledException) when (actionToken.IsCancellationRequested)
        {
            // 中断状态由加密恢复记录接管；不清码，避免服务端已消费但响应丢失时无法恢复。
            await RefreshActivationRecoveryLockAsync();
        }
        catch (Exception ex)
        {
            await RefreshActivationRecoveryLockAsync();
            if (!_isReregisterCancelRequested
                && IsCurrentActivationAction(sessionVersion, actionCancellation, normalizedCode))
            {
                ShowActivationError(ex);
            }
        }
        finally
        {
            CompleteRegistrationAction(actionCancellation);
        }
    }

    private async Task RegisterAsync()
    {
        if (!CanRegister())
        {
            return;
        }

        if (IsReregisterMode)
        {
            await ReregisterAsync();
            return;
        }

        await RegisterDeviceAsync();
    }

    private async Task RegisterDeviceAsync()
    {
        if (SelectedStore is null)
        {
            return;
        }

        var selectedStore = SelectedStore;
        StopApprovalPolling();
        var actionCancellation = new CancellationTokenSource();
        var actionToken = actionCancellation.Token;
        _registrationActionCancellation = actionCancellation;
        var sessionVersion = _registrationSessionVersion;
        IsBusy = true;
        try
        {
            StatusMessage = T("deviceRegistration.status.submitting", "Submitting device registration...");
            var result = await _workflowService.RegisterAsync(selectedStore, HardwareId, actionToken);
            if (!IsCurrentRegistrationAction(sessionVersion, actionCancellation, selectedStore))
            {
                return;
            }

            await PersistResultAsync(result, actionToken);
            if (!IsCurrentRegistrationAction(sessionVersion, actionCancellation, selectedStore))
            {
                return;
            }

            await ApplyActionResultAsync(result, clearDeviceCodeWhenRejected: true);
        }
        catch (OperationCanceledException) when (actionToken.IsCancellationRequested)
        {
            // 已切换门店或重置流程的注册提交不应覆盖当前注册状态。
        }
        catch (Exception ex)
        {
            if (IsCurrentRegistrationAction(sessionVersion, actionCancellation, selectedStore))
            {
                ShowFailure(ex);
            }
        }
        finally
        {
            if (ReferenceEquals(_registrationActionCancellation, actionCancellation))
            {
                _registrationActionCancellation = null;
                actionCancellation.Dispose();
            }

            IsBusy = false;
        }
    }

    private async Task ReregisterAsync()
    {
        if (SelectedStore is null)
        {
            return;
        }

        var selectedStore = SelectedStore;
        StopApprovalPolling();
        var actionCancellation = new CancellationTokenSource();
        var actionToken = actionCancellation.Token;
        _registrationActionCancellation = actionCancellation;
        var sessionVersion = _registrationSessionVersion;
        IsBusy = true;
        try
        {
            StatusMessage = T("deviceRegistration.status.submittingReregister", "Submitting device reregistration...");
            var result = await _workflowService.ReregisterAsync(selectedStore, HardwareId, actionToken);
            if (_isReregisterCancelRequested || !IsCurrentRegistrationAction(sessionVersion, actionCancellation, selectedStore))
            {
                // 用户已放弃本次更换分店流程，忽略后台返回，避免关闭后的弹窗继续改写界面状态。
                return;
            }

            await PersistResultAsync(result, actionToken);
            if (_isReregisterCancelRequested || !IsCurrentRegistrationAction(sessionVersion, actionCancellation, selectedStore))
            {
                return;
            }

            await ApplyActionResultAsync(result);
        }
        catch (OperationCanceledException) when (actionToken.IsCancellationRequested)
        {
            // 已取消的重新注册提交不应覆盖当前授权分店。
        }
        catch (Exception ex)
        {
            if (_isReregisterCancelRequested || !IsCurrentRegistrationAction(sessionVersion, actionCancellation, selectedStore))
            {
                // 取消后不再把后台错误显示到已关闭流程，当前授权分店继续保持不变。
                return;
            }

            ShowFailure(ex);
        }
        finally
        {
            if (ReferenceEquals(_registrationActionCancellation, actionCancellation))
            {
                _registrationActionCancellation = null;
                actionCancellation.Dispose();
            }

            IsBusy = false;
        }
    }

    private async Task VerifyAsync()
    {
        if (!CanVerify() || SelectedStore is null || string.IsNullOrWhiteSpace(DeviceCode))
        {
            return;
        }

        var selectedStore = SelectedStore;
        var deviceCode = DeviceCode;
        StopApprovalPolling();
        var verificationCancellation = new CancellationTokenSource();
        var verificationToken = verificationCancellation.Token;
        _manualVerificationCancellation = verificationCancellation;
        var sessionVersion = _registrationSessionVersion;
        IsBusy = true;
        try
        {
            StatusMessage = T("deviceRegistration.status.checkingApproval", "Checking device approval...");
            var result = await _workflowService.VerifyAsync(selectedStore, deviceCode, HardwareId, verificationToken);
            if (!IsCurrentManualVerification(sessionVersion, verificationCancellation, selectedStore, deviceCode))
            {
                return;
            }

            await PersistResultAsync(result, verificationToken);
            if (!IsCurrentManualVerification(sessionVersion, verificationCancellation, selectedStore, deviceCode))
            {
                return;
            }

            await ApplyActionResultAsync(result);
        }
        catch (OperationCanceledException) when (verificationToken.IsCancellationRequested)
        {
            // 已切换门店或重置流程的手动验证不应覆盖当前注册状态。
        }
        catch (Exception ex)
        {
            if (IsCurrentManualVerification(sessionVersion, verificationCancellation, selectedStore, deviceCode))
            {
                ShowFailure(ex);
            }
        }
        finally
        {
            if (ReferenceEquals(_manualVerificationCancellation, verificationCancellation))
            {
                _manualVerificationCancellation = null;
                verificationCancellation.Dispose();
            }

            IsBusy = false;
        }
    }

    private void ApplyLoadResult(DeviceRegistrationLoadResult result)
    {
        Stores.Clear();
        foreach (var store in result.Stores)
        {
            Stores.Add(store);
        }

        DeviceCode = result.DeviceCode;
        HasPendingRegistration = result.HasPendingRegistration;
        StatusMessage = result.StatusMessage;
        var pendingStoreCode = _pendingRegistration?.StoreCode ?? result.SelectedStore?.StoreCode;
        if (!IsReregisterMode && result.HasPendingRegistration && !string.IsNullOrWhiteSpace(pendingStoreCode))
        {
            _pendingRegistration = new PendingRegistrationState(
                pendingStoreCode,
                string.IsNullOrWhiteSpace(_pendingRegistration?.DeviceCode) ? result.DeviceCode : _pendingRegistration.DeviceCode,
                string.IsNullOrWhiteSpace(_pendingRegistration?.StatusMessage) ? result.StatusMessage : _pendingRegistration.StatusMessage);
        }

        // 重新注册必须由收银员手动选择目标分店，避免默认选中后误提交。
        SelectedStore = IsReregisterMode ? null : result.SelectedStore;
        NotifyCommandState();
    }

    private async Task ApplyActionResultAsync(
        DeviceRegistrationActionResult result,
        bool clearDeviceCodeWhenRejected = false,
        bool restartPollingWhenPending = true)
    {
        var shouldClearRejectedDeviceCode = clearDeviceCodeWhenRejected
            && !result.HasPendingRegistration
            && !result.ShouldRaiseActivated;

        if (shouldClearRejectedDeviceCode)
        {
            _pendingRegistration = null;
        }

        DeviceCode = shouldClearRejectedDeviceCode ? string.Empty : result.DeviceCode;
        HasPendingRegistration = result.HasPendingRegistration;
        StatusMessage = result.StatusMessage;
        if (!IsReregisterMode)
        {
            if (result.HasPendingRegistration)
            {
                _pendingRegistration = new PendingRegistrationState(
                    result.StoreCode,
                    result.DeviceCode,
                    result.StatusMessage);
            }
            else if (result.ShouldRaiseActivated)
            {
                _pendingRegistration = null;
            }
        }

        if (result.ShouldRaiseActivated || result.ShouldRaiseReregistered || !result.HasPendingRegistration || shouldClearRejectedDeviceCode)
        {
            StopApprovalPolling();
        }
        else if (restartPollingWhenPending)
        {
            RestartApprovalPollingIfNeeded();
        }

        if (result.ShouldRaiseReregistered)
        {
            IsReregisterMode = false;
            CanCancel = false;
            DeviceReregistered?.Invoke(
                this,
                new DeviceReregisteredEventArgs(result.DeviceCode, result.StoreCode, result.StoreName, result.HardwareId));
        }

        if (result.ShouldRaiseActivated)
        {
            var args = new DeviceActivatedEventArgs(
                result.DeviceCode,
                result.StoreCode,
                result.StoreName,
                result.HardwareId,
                result.AuthorizationCode ?? string.Empty,
                result.IsActivationRebind);
            DeviceActivated?.Invoke(this, args);
            if (DeviceActivatedAsync is not null)
            {
                foreach (Func<object?, DeviceActivatedEventArgs, Task> handler in DeviceActivatedAsync.GetInvocationList())
                {
                    await handler(this, args);
                }
            }
        }

        OnPropertyChanged(nameof(RegisterButtonText));
        NotifyCommandState();
    }

    public bool ProcessScannerBarcode(string barcode, string devicePath, string source)
    {
        if (_disposed)
        {
            return false;
        }

        if (IsActivationRecoveryPending)
        {
            // 最终消费已开始后只能重试同一意图，所有新扫码均由当前页吞掉且不得改码。
            return true;
        }

        if (!DeviceActivationCodeNormalizer.TryNormalize(barcode, out var normalizedCode))
        {
            StatusMessage = T(
                "deviceActivation.status.invalidFormat",
                "Scan or enter the complete HBDEV1 activation code.");
            return true;
        }

        if (IsBusy || IsLegacyPendingRegistration)
        {
            return true;
        }

        ActivationCode = normalizedCode;
        PreviewActivationCommand.Execute(null);
        return true;
    }

    private void OnRawBarcodeScanned(RawBarcodeScannedEventArgs args)
    {
        ProcessScannerBarcode(args.Barcode, args.DevicePath, "raw");
    }

    private bool CanPreviewActivation()
    {
        return IsActivationCodeMode
            && !IsBusy
            && !IsActivationRecoveryPending
            && !HasActivationPreview
            && DeviceActivationCodeNormalizer.TryNormalize(ActivationCode, out _)
            && _apiServerSettings?.RestartRequired != true;
    }

    private bool CanConfirmActivation()
    {
        return IsActivationCodeMode
            && !IsBusy
            && HasActivationPreview
            && _previewedActivationCode is not null
            && DeviceActivationCodeNormalizer.TryNormalize(ActivationCode, out var normalizedCode)
            && string.Equals(normalizedCode, _previewedActivationCode, StringComparison.Ordinal)
            && _apiServerSettings?.RestartRequired != true;
    }

    private bool IsCurrentActivationAction(
        long sessionVersion,
        CancellationTokenSource actionCancellation,
        string activationCode)
    {
        return !_disposed
            && sessionVersion == _registrationSessionVersion
            && ReferenceEquals(_registrationActionCancellation, actionCancellation)
            && DeviceActivationCodeNormalizer.TryNormalize(ActivationCode, out var currentCode)
            && string.Equals(currentCode, activationCode, StringComparison.Ordinal);
    }

    private void CompleteRegistrationAction(CancellationTokenSource actionCancellation)
    {
        if (ReferenceEquals(_registrationActionCancellation, actionCancellation))
        {
            _registrationActionCancellation = null;
            actionCancellation.Dispose();
        }

        if (!_disposed)
        {
            IsBusy = false;
        }
    }

    private async Task RefreshActivationRecoveryLockAsync()
    {
        try
        {
            IsActivationRecoveryPending =
                await _workflowService.GetPendingActivationRecoveryModeAsync(CancellationToken.None) is not null;
        }
        catch (DeviceActivationRecoveryUnreadableException)
        {
            IsActivationRecoveryPending = true;
        }
        catch (Exception)
        {
            // 无法证明恢复记录不存在时保持 fail-closed，避免回到旧分店离线营业。
            IsActivationRecoveryPending = true;
        }
    }

    private void ClearActivationPreview(bool clearCode)
    {
        _previewedActivationCode = null;
        HasActivationPreview = false;
        PreviewStoreCode = string.Empty;
        PreviewStoreName = string.Empty;
        PreviewDeviceSystem = string.Empty;
        PreviewExpiresAtUtc = null;
        if (clearCode)
        {
            ActivationCode = string.Empty;
        }
    }

    private string ResolveActivationError(Exception exception)
    {
        if (exception is not CatalogApiException apiException)
        {
            return exception.Message;
        }

        var key = apiException.ErrorCode switch
        {
            InvalidActivationCodeError => "deviceActivation.error.invalid",
            DeviceActivationReasonCodes.NotAvailable => "deviceActivation.error.notAvailable",
            DeviceActivationReasonCodes.PlatformMismatch => "deviceActivation.error.platformMismatch",
            _ => "deviceActivation.error.failed"
        };
        return T(key, apiException.Message);
    }

    private bool CanRegister()
    {
        return !IsBusy &&
               SelectedStore is not null &&
               !HasPendingRegistration &&
               _apiServerSettings?.RestartRequired != true;
    }

    private bool CanVerify()
    {
        return !IsBusy &&
               SelectedStore is not null &&
               !string.IsNullOrWhiteSpace(DeviceCode) &&
               _apiServerSettings?.RestartRequired != true;
    }

    private void OnApiServerSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_apiServerSettings?.RestartRequired == true)
        {
            // 保存新服务器地址后立即停止旧地址上的注册工作，重启前不允许继续提交或验证。
            CancelStoreLoad();
            StopApprovalPolling();
        }
        else
        {
            // 用户撤销地址变更后，按现有 pending 状态恢复唯一的自动审批轮询。
            RestartApprovalPollingIfNeeded();
        }

        NotifyCommandState();
    }

    private bool CanExecuteCancel()
    {
        // preview 前仍可放弃；一旦最终消费留下恢复记录，就只能重试同一意图直至确定终态。
        return CanCancel
            && !IsActivationRecoveryPending
            && (IsReregisterMode || !IsBusy);
    }

    private void ApplyPendingRegistrationSelection(StoreSelectionItem? selectedStore)
    {
        if (_pendingRegistration is null || IsReregisterMode || selectedStore is null)
        {
            return;
        }

        if (string.Equals(selectedStore.StoreCode, _pendingRegistration.StoreCode, StringComparison.OrdinalIgnoreCase))
        {
            DeviceCode = _pendingRegistration.DeviceCode;
            HasPendingRegistration = true;
            StatusMessage = _pendingRegistration.StatusMessage;
            RestartApprovalPollingIfNeeded();
            return;
        }

        StopApprovalPolling();
        DeviceCode = string.Empty;
        HasPendingRegistration = false;
        StatusMessage = T(
            "deviceRegistration.status.switchStore",
            "A different store is selected. Submit a new registration request to switch stores.");
    }

    private void NotifyCommandState()
    {
        RegisterCommand.NotifyCanExecuteChanged();
        VerifyCommand.NotifyCanExecuteChanged();
        PreviewActivationCommand.NotifyCanExecuteChanged();
        ConfirmActivationCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private void RaiseLocalizedProperties()
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(RegisterButtonText));
        OnPropertyChanged(nameof(ActivationPreviewButtonText));
        OnPropertyChanged(nameof(ActivationConfirmButtonText));
    }

    private string T(string key, string fallback)
    {
        return _localization?.T(key) ?? fallback;
    }

    private void RestartApprovalPollingIfNeeded()
    {
        if (_disposed)
        {
            return;
        }

        StopApprovalPolling();
        // 新地址仅在重启后生效，等待重启期间不能重新创建仍访问旧地址的审批轮询。
        if (_apiServerSettings?.RestartRequired == true ||
            IsReregisterMode ||
            !HasPendingRegistration ||
            SelectedStore is null ||
            string.IsNullOrWhiteSpace(DeviceCode))
        {
            return;
        }

        var store = SelectedStore;
        var deviceCode = DeviceCode;
        var hardwareId = HardwareId;
        var pollingCancellation = new CancellationTokenSource();
        var sessionVersion = _registrationSessionVersion;
        _approvalPollingCancellation = pollingCancellation;
        _approvalPollingTask = PollApprovalAsync(store, deviceCode, hardwareId, pollingCancellation, sessionVersion);
    }

    private void StopApprovalPolling()
    {
        _registrationSessionVersion++;
        var pollingCancellation = _approvalPollingCancellation;
        _approvalPollingCancellation = null;
        CancelAndDispose(pollingCancellation);

        var manualVerificationCancellation = _manualVerificationCancellation;
        _manualVerificationCancellation = null;
        CancelAndDispose(manualVerificationCancellation);

        var registrationActionCancellation = _registrationActionCancellation;
        _registrationActionCancellation = null;
        CancelAndDispose(registrationActionCancellation);
    }

    private static void CancelAndDispose(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void CancelStoreLoad()
    {
        var loadCancellation = _storeLoadCancellation;
        if (loadCancellation is null)
        {
            return;
        }

        _storeLoadCancellation = null;
        CancelAndDispose(loadCancellation);
        if (!_disposed)
        {
            IsBusy = false;
        }
    }

    private bool IsCurrentStoreLoad(CancellationTokenSource loadCancellation)
    {
        return !_disposed &&
               ReferenceEquals(_storeLoadCancellation, loadCancellation) &&
               !loadCancellation.IsCancellationRequested;
    }

    private bool IsCurrentApprovalPolling(CancellationTokenSource pollingCancellation, long sessionVersion)
    {
        // 取消令牌是协作式的，旧请求仍可能晚于新轮询返回，因此必须确认结果归属当前会话。
        return ReferenceEquals(_approvalPollingCancellation, pollingCancellation)
            && sessionVersion == _registrationSessionVersion
            && !pollingCancellation.IsCancellationRequested;
    }

    private bool IsCurrentManualVerification(
        long sessionVersion,
        CancellationTokenSource verificationCancellation,
        StoreSelectionItem selectedStore,
        string deviceCode)
    {
        return ReferenceEquals(_manualVerificationCancellation, verificationCancellation)
            && sessionVersion == _registrationSessionVersion
            && !verificationCancellation.IsCancellationRequested
            && string.Equals(SelectedStore?.StoreCode, selectedStore.StoreCode, StringComparison.OrdinalIgnoreCase)
            && string.Equals(DeviceCode, deviceCode, StringComparison.Ordinal);
    }

    private bool IsCurrentRegistrationAction(
        long sessionVersion,
        CancellationTokenSource actionCancellation,
        StoreSelectionItem selectedStore)
    {
        return ReferenceEquals(_registrationActionCancellation, actionCancellation)
            && sessionVersion == _registrationSessionVersion
            && !actionCancellation.IsCancellationRequested
            && string.Equals(SelectedStore?.StoreCode, selectedStore.StoreCode, StringComparison.OrdinalIgnoreCase);
    }

    private static Task PersistResultAsync(
        DeviceRegistrationActionResult result,
        CancellationToken cancellationToken)
    {
        return result.PersistAsync?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }

    private async Task PollApprovalAsync(
        StoreSelectionItem store,
        string deviceCode,
        string hardwareId,
        CancellationTokenSource pollingCancellation,
        long sessionVersion)
    {
        var cancellationToken = pollingCancellation.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // 待审批注册没有服务端推送，按固定间隔轻量检查，直到后台启用或返回终态。
                    await _delayAsync(_approvalPollingInterval, cancellationToken);
                    if (!IsCurrentApprovalPolling(pollingCancellation, sessionVersion))
                    {
                        return;
                    }

                    StatusMessage = T("deviceRegistration.status.checkingApproval", "Checking device approval...");
                    var result = await _workflowService.VerifyAsync(store, deviceCode, hardwareId, cancellationToken);
                    if (!IsCurrentApprovalPolling(pollingCancellation, sessionVersion))
                    {
                        return;
                    }

                    await PersistResultAsync(result, cancellationToken);
                    if (!IsCurrentApprovalPolling(pollingCancellation, sessionVersion))
                    {
                        return;
                    }

                    await ApplyActionResultAsync(result, restartPollingWhenPending: false);
                    if (!result.HasPendingRegistration || result.ShouldRaiseActivated || result.ShouldRaiseReregistered)
                    {
                        return;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (!IsCurrentApprovalPolling(pollingCancellation, sessionVersion))
                    {
                        return;
                    }

                    // 单次轮询失败只提示错误，保留注册页并继续下一轮重试。
                    ShowFailure(ex);
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_approvalPollingCancellation, pollingCancellation))
            {
                _approvalPollingCancellation = null;
                pollingCancellation.Dispose();
            }
        }
    }

    private void Cancel()
    {
        if (CanExecuteCancel())
        {
            CancelStoreLoad();
            StopApprovalPolling();
            if (IsReregisterMode)
            {
                _isReregisterCancelRequested = true;
            }

            CancelRequested?.Invoke(this, EventArgs.Empty);
        }
    }

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

        if (_apiServerSettings is not null)
        {
            PropertyChangedEventManager.RemoveHandler(
                _apiServerSettings,
                OnApiServerSettingsPropertyChanged,
                nameof(ApiServerSettingsViewModel.RestartRequired));
        }

        CancelStoreLoad();
        StopApprovalPolling();
        _rawScannerService?.Unsubscribe(PageId);
        _approvalPollingTask = null;
        DeviceActivated = null;
        DeviceActivatedAsync = null;
        DeviceReregistered = null;
        CancelRequested = null;
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        RaiseLocalizedProperties();
    }
}

public sealed record DeviceActivatedEventArgs(
    string DeviceCode,
    string StoreCode,
    string StoreName,
    string HardwareId,
    string AuthorizationCode = "",
    bool IsReregistered = false);

public sealed record DeviceReregisteredEventArgs(
    string DeviceCode,
    string StoreCode,
    string StoreName,
    string HardwareId);

internal sealed record PendingRegistrationState(
    string StoreCode,
    string DeviceCode,
    string StatusMessage);
