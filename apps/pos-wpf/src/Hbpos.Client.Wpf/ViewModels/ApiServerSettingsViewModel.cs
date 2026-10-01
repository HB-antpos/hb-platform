using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class ApiServerSettingsViewModel : ObservableObject
{
    private readonly ApiServerSettingsService _settingsService;
    private readonly ILocalizationService _localization;
    private readonly IApiServerSwitchCoordinator? _switchCoordinator;
    private readonly IOperationAuditLogger? _operationAuditLogger;
    private readonly ICashierSessionContext? _cashierSessionContext;

    [ObservableProperty]
    private string _serverAddressText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _restartRequired;

    public ApiServerSettingsViewModel(
        ApiServerSettingsService settingsService,
        ILocalizationService localization,
        IApiServerSwitchCoordinator? switchCoordinator = null,
        IOperationAuditLogger? operationAuditLogger = null,
        ICashierSessionContext? cashierSessionContext = null)
    {
        _settingsService = settingsService;
        _localization = localization;
        _switchCoordinator = switchCoordinator;
        _operationAuditLogger = operationAuditLogger;
        _cashierSessionContext = cashierSessionContext;
        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, CanRun);
        SaveCommand = new AsyncRelayCommand(SaveAsync, CanRun);
        UseDevelopmentAddressCommand = new RelayCommand(
            () => ServerAddressText = ApiServerSettingsService.DevelopmentApiBaseAddress,
            () => !IsBusy);
        UseReleaseAddressCommand = new RelayCommand(
            () => ServerAddressText = ApiServerSettingsService.ReleaseApiBaseAddress,
            () => !IsBusy);
    }

    public IAsyncRelayCommand TestConnectionCommand { get; }

    public IAsyncRelayCommand SaveCommand { get; }

    public IRelayCommand UseDevelopmentAddressCommand { get; }

    public IRelayCommand UseReleaseAddressCommand { get; }

    public void Load()
    {
        // 用户级地址要到重启后才生效；重启前再次进入页面必须保留待生效地址和阻断标记。
        if (RestartRequired)
        {
            return;
        }

        ServerAddressText = _settingsService.GetCurrentAddress();
        RestartRequired = false;
        SetStatus("settings.serverAddress.status.ready");
    }

    partial void OnServerAddressTextChanged(string value)
    {
        RaiseCommandStates();
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
                // 上传按上传时的服务器与设备身份发送，切换成功后记录的事件会进入新服务器。
                RecordServerChange("Succeeded", "RUNTIME_SWITCH", actor, previousAddress, ServerAddressText);
                SetStatus("settings.serverAddress.status.switched");
                break;
            case ApiServerSwitchStatus.SameAddress:
                ServerAddressText = _settingsService.GetCurrentAddress();
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
