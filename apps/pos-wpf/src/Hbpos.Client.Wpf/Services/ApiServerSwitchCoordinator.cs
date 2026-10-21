using System.IO;

namespace Hbpos.Client.Wpf.Services;

public enum ApiServerSwitchStatus
{
    Success,
    SameAddress,
    Blocked,
    PreCommitFailed,
    PostCommitFailed
}

public sealed record ApiServerSwitchResult(
    ApiServerSwitchStatus Status,
    string? BlockReason = null,
    string? ErrorMessage = null);

public sealed record ApiServerSwitchSafetySnapshot(
    int CartCount,
    bool IsCardPaymentInProgress,
    bool IsPaymentInteractionLocked,
    int PaymentTenderCount,
    int PendingSyncCount,
    int FailedSyncCount,
    int SyncingCount,
    int PendingOperationAuditCount,
    bool HasPendingActivationRecovery)
{
    public static ApiServerSwitchSafetySnapshot Safe { get; } = new(0, false, false, 0, 0, 0, 0, 0, false);

    public string? GetBlockReason()
    {
        if (HasPendingActivationRecovery)
        {
            return "settings.serverAddress.blocked.deviceActivation";
        }

        if (CartCount > 0)
        {
            return "settings.serverAddress.blocked.cart";
        }

        if (IsCardPaymentInProgress || IsPaymentInteractionLocked || PaymentTenderCount > 0)
        {
            return "settings.serverAddress.blocked.payment";
        }

        // 同一后端数据库的两个 API 入口允许携带积压切换，稍后由新端点继续上传。
        return null;
    }
}

public interface IApiServerSwitchCoordinator
{
    Task<ApiServerSwitchResult> SwitchAsync(
        string targetAddress,
        CancellationToken cancellationToken = default);
}

internal interface IApiServerSwitchRuntime
{
    Task<ApiServerSwitchSafetySnapshot> GetSafetySnapshotAsync(CancellationToken cancellationToken);

    Task<object> PrepareAsync(string targetAddress, CancellationToken cancellationToken);

    Task<object> BeginTransitionAsync(
        string targetAddress,
        object preparedSwitch,
        CancellationToken cancellationToken);

    Task<ApiServerSwitchSafetySnapshot> GetFinalSafetySnapshotAsync(
        object transition,
        CancellationToken cancellationToken);

    bool Commit(object transition);

    void Abort(object transition);

    Task PostCommitAsync(CancellationToken cancellationToken);
}

public sealed class ApiServerSwitchCoordinator : IApiServerSwitchCoordinator
{
    private readonly ApiServerSettingsService _settingsService;
    private readonly ApiRuntimeEndpointState _endpointState;
    private readonly IApiServerSwitchRuntime _runtime;
    private readonly SemaphoreSlim _switchGate = new(1, 1);

    internal ApiServerSwitchCoordinator(
        ApiServerSettingsService settingsService,
        ApiRuntimeEndpointState endpointState,
        IApiServerSwitchRuntime runtime)
    {
        _settingsService = settingsService;
        _endpointState = endpointState;
        _runtime = runtime;
    }

    public async Task<ApiServerSwitchResult> SwitchAsync(
        string targetAddress,
        CancellationToken cancellationToken = default)
    {
        await _switchGate.WaitAsync(cancellationToken);
        try
        {
            string normalized;
            try
            {
                normalized = ApiServerSettingsService.NormalizeAddress(targetAddress);
            }
            catch (ArgumentException ex)
            {
                return new ApiServerSwitchResult(ApiServerSwitchStatus.PreCommitFailed, ErrorMessage: ex.Message);
            }

            var previousAddress = _endpointState.CurrentAddress.AbsoluteUri;
            // 同一台服务器的别名入口（例如 pos.hotbargain.vip 与 hotbargain.vip）互相切换不是换服务器：
            // 不能走会清理登录身份、重建本地库的完整切换流程，直接按"地址未变"处理。
            if (string.Equals(normalized, previousAddress, StringComparison.Ordinal)
                || ApiEndpointAliases.SameIdentity(
                    new Uri(normalized, UriKind.Absolute),
                    _endpointState.CurrentAddress))
            {
                return new ApiServerSwitchResult(ApiServerSwitchStatus.SameAddress);
            }

            var safety = await _runtime.GetSafetySnapshotAsync(cancellationToken);
            var blockReason = safety.GetBlockReason();
            if (blockReason is not null)
            {
                return new ApiServerSwitchResult(ApiServerSwitchStatus.Blocked, blockReason);
            }

            if (!await _settingsService.TestConnectionAsync(normalized, cancellationToken))
            {
                return new ApiServerSwitchResult(
                    ApiServerSwitchStatus.PreCommitFailed,
                    ErrorMessage: "settings.serverAddress.status.testFailed");
            }

            object? transition = null;
            var committed = false;
            var addressSaved = false;
            try
            {
                var preparedSwitch = await _runtime.PrepareAsync(normalized, cancellationToken);
                transition = await _runtime.BeginTransitionAsync(normalized, preparedSwitch, cancellationToken);
                var finalSafety = await _runtime.GetFinalSafetySnapshotAsync(transition, cancellationToken);
                var finalBlockReason = finalSafety.GetBlockReason();
                if (finalBlockReason is not null)
                {
                    _runtime.Abort(transition);
                    transition = null;
                    return new ApiServerSwitchResult(ApiServerSwitchStatus.Blocked, finalBlockReason);
                }

                // HTTP 端点只在全部请求屏障关闭后发布；Commit 后禁止回滚到旧环境。
                _settingsService.SaveUserAddress(normalized);
                addressSaved = true;
                if (!_runtime.Commit(transition))
                {
                    _runtime.Abort(transition);
                    transition = null;
                    TrySaveAddress(previousAddress);
                    return new ApiServerSwitchResult(
                        ApiServerSwitchStatus.PreCommitFailed,
                        ErrorMessage: "settings.serverAddress.status.preCommitFailed");
                }

                committed = true;
            }
            catch (OperationCanceledException) when (!committed && cancellationToken.IsCancellationRequested)
            {
                if (transition is not null)
                {
                    _runtime.Abort(transition);
                }

                if (addressSaved)
                {
                    TrySaveAddress(previousAddress);
                }

                throw;
            }
            catch (Exception ex) when (!committed)
            {
                if (transition is not null)
                {
                    _runtime.Abort(transition);
                }

                if (addressSaved)
                {
                    TrySaveAddress(previousAddress);
                }

                return new ApiServerSwitchResult(ApiServerSwitchStatus.PreCommitFailed, ErrorMessage: ex.Message);
            }

            try
            {
                // 提交后即使调用方取消，也必须完成身份清理和新环境重建，绝不恢复旧会话。
                await _runtime.PostCommitAsync(CancellationToken.None);
                return new ApiServerSwitchResult(ApiServerSwitchStatus.Success);
            }
            catch (Exception ex)
            {
                return new ApiServerSwitchResult(ApiServerSwitchStatus.PostCommitFailed, ErrorMessage: ex.Message);
            }
        }
        finally
        {
            _switchGate.Release();
        }
    }

    private void TrySaveAddress(string address)
    {
        try
        {
            _settingsService.SaveUserAddress(address);
        }
        catch (Exception ex) when (ex is
            ArgumentException or
            IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            ConsoleLog.Write("ApiServerSwitch", $"pre-commit address recovery failed error={ex.Message}");
        }
    }
}
