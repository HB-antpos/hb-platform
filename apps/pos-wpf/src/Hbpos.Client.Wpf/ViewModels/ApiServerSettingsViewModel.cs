using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.Services.NetworkDiagnostics;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class ApiServerSettingsViewModel : ObservableObject
{
    private readonly ApiServerSettingsService _settingsService;
    private readonly ILocalizationService _localization;
    private readonly IApiServerSwitchCoordinator? _switchCoordinator;
    private readonly IOperationAuditLogger? _operationAuditLogger;
    private readonly ICashierSessionContext? _cashierSessionContext;
    private readonly ApiTransportSelector? _transportSelector;

    [ObservableProperty]
    private string _serverAddressText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _restartRequired;

    // 只读展示：后台设置的首选入口 / 当前实际入口 / 是否回退中。当前地址不在别名组内（本机开发地址等）时为空。
    [ObservableProperty]
    private string _entryStatusText = string.Empty;

    // 最近一次加载或保存成功的地址：输入框与它不同即「改了还没保存」，设置页离开时据此提示。
    private string _addressBaseline = string.Empty;

    public ApiServerSettingsViewModel(
        ApiServerSettingsService settingsService,
        ILocalizationService localization,
        IApiServerSwitchCoordinator? switchCoordinator = null,
        IOperationAuditLogger? operationAuditLogger = null,
        ICashierSessionContext? cashierSessionContext = null,
        INetworkDiagnosticsService? networkDiagnostics = null,
        INetworkFixService? networkFixes = null,
        ApiTransportSelector? transportSelector = null)
    {
        _transportSelector = transportSelector;
        _settingsService = settingsService;
        _localization = localization;
        _switchCoordinator = switchCoordinator;
        _operationAuditLogger = operationAuditLogger;
        _cashierSessionContext = cashierSessionContext;
        // 网络检测需要两个服务都在；缺一个（例如部分单元测试）就不显示这一块。
        Network = networkDiagnostics is not null && networkFixes is not null
            ? new NetworkDiagnosticsViewModel(networkDiagnostics, networkFixes, localization, () => ServerAddressText)
            : null;
        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, CanRun);
        SaveCommand = new AsyncRelayCommand(SaveAsync, CanRun);
        UseDevelopmentAddressCommand = new RelayCommand(
            () => ServerAddressText = ApiServerSettingsService.DevelopmentApiBaseAddress,
            () => !IsBusy);
        UseReleaseAddressCommand = new RelayCommand(
            () => ServerAddressText = ApiServerSettingsService.ReleaseApiBaseAddress,
            () => !IsBusy);
    }

    /// <summary>网络检测与一键修复；服务不可用时为 null，面板隐藏对应区域。</summary>
    public NetworkDiagnosticsViewModel? Network { get; }

    public bool HasNetworkDiagnostics => Network is not null;

    public bool HasEntryStatus => !string.IsNullOrEmpty(EntryStatusText);

    partial void OnEntryStatusTextChanged(string value) => OnPropertyChanged(nameof(HasEntryStatus));

    /// <summary>
    /// 刷新入口状态（只读，不改变任何冷却状态）。入口会随线路故障转移变化，所以进入页面和每次测试连接后都刷新。
    /// </summary>
    public void RefreshEntryStatus()
    {
        EntryStatusText = BuildEntryStatusText();
        // 同步刷新拆分后的首选 / 当前 / 回退三个字段（设置页两行展示用），数据来源与上面相同。
        RefreshEntryParts();
    }

    private string BuildEntryStatusText()
    {
        if (_transportSelector is null)
        {
            return string.Empty;
        }

        try
        {
            var status = _transportSelector.GetStatus(new Uri(_settingsService.GetCurrentAddress(), UriKind.Absolute));
            if (status.ActiveMode is null)
            {
                return string.Empty;
            }

            return string.Format(
                _localization.CurrentCulture,
                _localization.T("settings.serverAddress.entry.line"),
                _localization.T($"settings.serverAddress.entry.{status.PreferredMode}"),
                _localization.T($"settings.serverAddress.entry.{status.ActiveMode}"),
                status.UsingFallback ? _localization.T("settings.serverAddress.entry.fallback") : string.Empty);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            // 展示用的附加信息，任何异常都不能影响设置页。
            return string.Empty;
        }
    }

    public IAsyncRelayCommand TestConnectionCommand { get; }

    public IAsyncRelayCommand SaveCommand { get; }

    public IRelayCommand UseDevelopmentAddressCommand { get; }

    public IRelayCommand UseReleaseAddressCommand { get; }

    /// <summary>地址输入框里有没有尚未保存（或保存未成功）的修改。</summary>
    public bool HasUnsavedAddress => !AddressesMatch(ServerAddressText, _addressBaseline);

    /// <summary>放弃未保存的地址修改，输入框回到最近一次加载 / 保存成功的地址。</summary>
    public void DiscardUnsavedAddress()
    {
        if (HasUnsavedAddress)
        {
            ServerAddressText = _addressBaseline;
        }
    }

    public void Load()
    {
        // 用户级地址要到重启后才生效；重启前再次进入页面必须保留待生效地址和阻断标记。
        if (RestartRequired)
        {
            return;
        }

        ServerAddressText = _settingsService.GetCurrentAddress();
        SetAddressBaseline(ServerAddressText);
        RestartRequired = false;
        SetStatus("settings.serverAddress.status.ready");
        RefreshEntryStatus();
    }

    partial void OnServerAddressTextChanged(string value)
    {
        RaiseCommandStates();
        OnPropertyChanged(nameof(HasUnsavedAddress));
    }

    private void SetAddressBaseline(string address)
    {
        _addressBaseline = address;
        OnPropertyChanged(nameof(HasUnsavedAddress));
    }

    /// <summary>比较时忽略首尾空白和末尾斜杠这类不改变含义的差别；无法规整时按原文比较。</summary>
    private static bool AddressesMatch(string? left, string? right)
    {
        static string Canonical(string? value)
        {
            var trimmed = value?.Trim() ?? string.Empty;
            try
            {
                return ApiServerSettingsService.NormalizeAddress(trimmed);
            }
            catch (ArgumentException)
            {
                return trimmed;
            }
        }

        return string.Equals(Canonical(left), Canonical(right), StringComparison.OrdinalIgnoreCase);
    }

    partial void OnIsBusyChanged(bool value)
    {
        RaiseCommandStates();
    }

    private bool CanRun()
    {
        return !IsBusy && !string.IsNullOrWhiteSpace(ServerAddressText);
    }

    private async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        SetStatus("settings.serverAddress.status.testing");
        try
        {
            var succeeded = await _settingsService.TestConnectionAsync(ServerAddressText, cancellationToken);
            SetStatus(succeeded
                ? "settings.serverAddress.status.testSucceeded"
                : "settings.serverAddress.status.testFailed");
        }
        catch (ArgumentException)
        {
            SetStatus("settings.serverAddress.status.invalid");
        }
        finally
        {
            IsBusy = false;
            RefreshEntryStatus();
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            if (_switchCoordinator is not null)
            {
                await SwitchRuntimeAsync(cancellationToken);
                return;
            }

            string normalized;
            try
            {
                normalized = ApiServerSettingsService.NormalizeAddress(ServerAddressText);
            }
            catch (ArgumentException)
            {
                SetStatus("settings.serverAddress.status.invalid");
                return;
            }

            SetStatus("settings.serverAddress.status.testing");

            // 保存前先确认目标服务在线，避免把不可用地址写入用户环境变量。
            if (!await _settingsService.TestConnectionAsync(normalized, cancellationToken))
            {
                SetStatus("settings.serverAddress.status.testFailed");
                return;
            }

            var currentAddress = _settingsService.GetCurrentAddress();
            var actor = _cashierSessionContext?.CurrentSession;
            try
            {
                _settingsService.SaveUserAddress(normalized);
            }
            catch (Exception ex) when (ex is
                ArgumentException or
                System.IO.IOException or
                UnauthorizedAccessException or
                System.Security.SecurityException)
            {
                // 持久化失败不是地址校验失败，避免异步命令故障或误导用户修改合法地址。
                RecordServerChange("Failed", "SAVE_FAILED", actor, currentAddress, normalized);
                SetStatus("settings.serverAddress.status.saveFailed");
                return;
            }

            ServerAddressText = normalized;
            SetAddressBaseline(normalized);
            RestartRequired = !string.Equals(
                normalized,
                currentAddress,
                StringComparison.Ordinal);
            if (RestartRequired)
            {
                RecordServerChange("Succeeded", "SAVED_RESTART_REQUIRED", actor, currentAddress, normalized);
            }
            SetStatus(RestartRequired
                ? "settings.serverAddress.status.savedRestartRequired"
                : "settings.serverAddress.status.saved");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaiseCommandStates()
    {
        TestConnectionCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        UseDevelopmentAddressCommand.NotifyCanExecuteChanged();
        UseReleaseAddressCommand.NotifyCanExecuteChanged();
    }

    private async Task SwitchRuntimeAsync(CancellationToken cancellationToken)
    {
        SetStatus("settings.serverAddress.status.switching");
        // 切换提交后会清空收银员会话，操作人和原地址必须在切换前捕获。
        var actor = _cashierSessionContext?.CurrentSession;
        var previousAddress = _settingsService.GetCurrentAddress();
        var targetAddress = ServerAddressText;
        var result = await _switchCoordinator!.SwitchAsync(ServerAddressText, cancellationToken);
        RestartRequired = false;
        switch (result.Status)
        {
            case ApiServerSwitchStatus.Success:
                ServerAddressText = _settingsService.GetCurrentAddress();
                SetAddressBaseline(ServerAddressText);
                // 上传按上传时的服务器与设备身份发送，切换成功后记录的事件会进入新服务器。
                RecordServerChange("Succeeded", "RUNTIME_SWITCH", actor, previousAddress, ServerAddressText);
                SetStatus("settings.serverAddress.status.switched");
                break;
            case ApiServerSwitchStatus.SameAddress:
                ServerAddressText = _settingsService.GetCurrentAddress();
                SetAddressBaseline(ServerAddressText);
                SetStatus("settings.serverAddress.status.sameAddress");
                break;
            case ApiServerSwitchStatus.Blocked:
                RecordServerChange("Denied", "BLOCKED", actor, previousAddress, targetAddress);
                SetStatus(result.BlockReason ?? "settings.serverAddress.status.blocked");
                break;
            case ApiServerSwitchStatus.PostCommitFailed:
                RecordServerChange("Failed", "POST_COMMIT_FAILED", actor, previousAddress, _settingsService.GetCurrentAddress());
                SetStatus("settings.serverAddress.status.postCommitFailed");
                break;
            default:
                RecordServerChange("Failed", "PRE_COMMIT_FAILED", actor, previousAddress, targetAddress);
                SetStatus(result.ErrorMessage?.StartsWith("settings.", StringComparison.Ordinal) == true
                    ? result.ErrorMessage
                    : "settings.serverAddress.status.preCommitFailed");
                break;
        }
    }

    /// <summary>
    /// 切换服务器地址决定收银数据发往哪个后台，属于系统级高风险操作；地址只含主机与路径，不含凭据。
    /// </summary>
    private void RecordServerChange(
        string outcome,
        string reasonCode,
        Hbpos.Contracts.Cashiers.CashierSessionDto? actor,
        string previousAddress,
        string targetAddress)
    {
        OperationAuditEvents.RecordActorAction(
            _operationAuditLogger,
            OperationAuditTypes.ApiServerChange,
            outcome,
            actor,
            reasonCode,
            $"{previousAddress} -> {targetAddress}");
    }

    private void SetStatus(string key)
    {
        StatusMessage = _localization.T(key);
    }
}
