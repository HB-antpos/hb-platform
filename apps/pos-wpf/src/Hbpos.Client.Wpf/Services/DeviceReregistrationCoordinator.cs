using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.ViewModels;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 设备重新注册流程协调器：承接重注册前置检查（preview 模式、购物车非空、订单同步状态、未结刷卡交易），
/// 以及重注册提交后的授权清理。
/// MainViewModel 保留 DeviceRegistration 绑定、IsDeviceReregistrationDialogOpen 通知和 VM 生命周期管理。
/// </summary>
internal sealed class DeviceReregistrationCoordinator
{
    private readonly IMainShellStartupService _mainShellStartupService;
    private readonly ILocalizationService _localization;
    private readonly IShellSyncCenterService _shellSyncCenterService;
    private readonly PosCartService _cart;
    private readonly Action<ShellSyncCenterSnapshot> _applySyncCenterSnapshot;
    private readonly Action<string> _setStatusMessage;
    private readonly Func<bool> _isPreviewMode;
    private readonly Func<CancellationToken, Task<bool>>? _hasUnresolvedCardAttemptsAsync;

    public DeviceReregistrationCoordinator(
        IMainShellStartupService mainShellStartupService,
        ILocalizationService localization,
        IShellSyncCenterService shellSyncCenterService,
        PosCartService cart,
        Action<ShellSyncCenterSnapshot> applySyncCenterSnapshot,
        Action<string> setStatusMessage,
        Func<bool> isPreviewMode,
        Func<CancellationToken, Task<bool>>? hasUnresolvedCardAttemptsAsync = null)
    {
        _mainShellStartupService = mainShellStartupService;
        _localization = localization;
        _shellSyncCenterService = shellSyncCenterService;
        _cart = cart;
        _applySyncCenterSnapshot = applySyncCenterSnapshot;
        _setStatusMessage = setStatusMessage;
        _isPreviewMode = isPreviewMode;
        _hasUnresolvedCardAttemptsAsync = hasUnresolvedCardAttemptsAsync;
    }

    /// <summary>
    /// 检查是否允许开始重注册。返回 null 表示可以继续，返回非 null 的 Blocked 结果表示被阻止。
    /// </summary>
    public async Task<DeviceReregistrationStartResult?> CheckCanBeginAsync()
    {
        if (_isPreviewMode())
        {
            _setStatusMessage(_localization.T("main.reregister.previewUnsupported"));
            return DeviceReregistrationStartResult.Blocked(_localization.T("main.reregister.previewUnsupported"));
        }

        if (!_cart.IsEmpty)
        {
            _setStatusMessage(_localization.T("main.reregister.cartNotEmpty"));
            return DeviceReregistrationStartResult.Blocked(_localization.T("main.reregister.cartNotEmpty"));
        }

        var syncSnapshot = await _shellSyncCenterService.GetSnapshotAsync();
        var overview = syncSnapshot.Overview;
        if (overview.PendingCount > 0 || overview.FailedCount > 0 || overview.SyncingCount > 0)
        {
            _setStatusMessage(_localization.T("main.reregister.syncPending"));
            _applySyncCenterSnapshot(syncSnapshot);
            return DeviceReregistrationStartResult.Blocked(_localization.T("main.reregister.syncPending"));
        }

        // 未结卡交易按“门店 + 设备号 + 环境”存放：重注册后设备号变化，旧记录会从所有恢复入口消失，
        // 而服务端旧会话还占着那台实体刷卡机。所以有未结记录时必须先在异常中心处理完再重注册。
        // 检查本身失败时按失败关闭，不能因为读不出来就放行。
        if (_hasUnresolvedCardAttemptsAsync is not null)
        {
            bool hasUnresolvedCardAttempts;
            try
            {
                hasUnresolvedCardAttempts = await _hasUnresolvedCardAttemptsAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
            {
                ConsoleLog.Write(
                    "CardRecovery",
                    $"device reregistration card attempt check failed error={ex.GetType().Name}: {ex.Message}");
                _setStatusMessage(_localization.T("main.reregister.cardRecoveryUnknown"));
                return DeviceReregistrationStartResult.Blocked(_localization.T("main.reregister.cardRecoveryUnknown"));
            }

            if (hasUnresolvedCardAttempts)
            {
                _setStatusMessage(_localization.T("main.reregister.cardRecoveryPending"));
                return DeviceReregistrationStartResult.Blocked(_localization.T("main.reregister.cardRecoveryPending"));
            }
        }

        return null; // 可以继续
    }

    /// <summary>
    /// 清除设备授权（重注册提交后调用）。
    /// </summary>
    public void ClearAuthorization() => _mainShellStartupService.ClearAuthorization();

    /// <summary>
    /// 取消重注册的状态消息。
    /// </summary>
    public string CancelStatusMessage => _localization.T("main.reregister.cancelled");

    /// <summary>
    /// 提交重注册的状态消息。
    /// </summary>
    public string SubmittedStatusMessage => _localization.T("main.reregister.submitted");
}
