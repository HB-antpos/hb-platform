using System.Net;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Devices;

namespace Hbpos.Client.Wpf.Services;

public interface IDeviceRegistrationWorkflowService
{
    string GetHardwareId();

    Task<DeviceRegistrationLoadResult> LoadStoresAsync(
        LocalDeviceCache? cachedDevice,
        bool isReregisterMode,
        string? excludedStoreCode = null,
        CancellationToken cancellationToken = default);

    Task<DeviceRegistrationActionResult> RegisterAsync(
        StoreSelectionItem selectedStore,
        string hardwareId,
        CancellationToken cancellationToken = default);

    Task<DeviceRegistrationActionResult> VerifyAsync(
        StoreSelectionItem selectedStore,
        string deviceCode,
        string hardwareId,
        CancellationToken cancellationToken = default);

    Task<DeviceRegistrationActionResult> ReregisterAsync(
        StoreSelectionItem selectedStore,
        string hardwareId,
        CancellationToken cancellationToken = default);

    Task<DeviceActivationPreviewResult> PreviewActivationCodeAsync(
        string activationCode,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Device activation-code preview is not available.");

    Task<DeviceRegistrationActionResult> RedeemActivationCodeAsync(
        string activationCode,
        string hardwareId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Device activation-code redemption is not available.");

    Task<DeviceRegistrationActionResult> RebindActivationCodeAsync(
        string activationCode,
        string hardwareId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Device activation-code rebinding is not available.");

    Task<DeviceActivationRecoveryResult?> RecoverActivationCodeAsync(
        string hardwareId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<DeviceActivationRecoveryResult?>(null);

    Task<DeviceActivationRecoveryMode?> GetPendingActivationRecoveryModeAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<DeviceActivationRecoveryMode?>(null);

    Task ClearActivationRecoveryAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed record DeviceActivationPreviewResult(
    string ActivationCode,
    string StoreCode,
    string StoreName,
    string DeviceSystem,
    DateTimeOffset ExpiresAtUtc);

public sealed record DeviceActivationRecoveryResult(
    DeviceRegistrationActionResult ActionResult,
    DeviceActivationRecoveryMode Mode);

public sealed record DeviceRegistrationLoadResult(
    IReadOnlyList<StoreSelectionItem> Stores,
    StoreSelectionItem? SelectedStore,
    string DeviceCode,
    bool HasPendingRegistration,
    string StatusMessage);

public sealed record DeviceRegistrationActionResult(
    string DeviceCode,
    string StoreCode,
    string StoreName,
    string HardwareId,
    bool HasPendingRegistration,
    string StatusMessage,
    string? AuthorizationCode,
    bool ShouldRaiseActivated,
    bool ShouldRaiseReregistered)
{
    internal Func<CancellationToken, Task>? PersistAsync { get; init; }

    public bool IsActivationRebind { get; init; }
}

public sealed class DeviceRegistrationWorkflowService(
    IDeviceApiClient deviceApiClient,
    ILocalDeviceRepository deviceRepository,
    IDeviceFingerprintService fingerprintService,
    ILocalizationService? localization = null,
    IDeviceActivationRecoveryStore? activationRecoveryStore = null) : IDeviceRegistrationWorkflowService
{
    private const int PendingDeviceStatus = -1;
    private const string InvalidActivationCodeError = "ACTIVATION_CODE_INVALID_FORMAT";
    private const string BusinessRejectionMarker = "HBPOS_DEVICE_ACTIVATION_BUSINESS_REJECTION";

    public const string LoadingStoresMessage = "Loading stores...";

    public string GetHardwareId()
    {
        return fingerprintService.GetHardwareId();
    }

    public async Task<DeviceActivationPreviewResult> PreviewActivationCodeAsync(
        string activationCode,
        CancellationToken cancellationToken = default)
    {
        if (!DeviceActivationCodeNormalizer.TryNormalize(activationCode, out var normalizedCode))
        {
            throw new CatalogApiException(
                T("deviceActivation.error.invalid", "The activation code format is invalid."),
                errorCode: InvalidActivationCodeError);
        }

        var response = await deviceApiClient.PreviewActivationCodeAsync(
            new DeviceActivationCodePreviewRequest(normalizedCode, DeviceSystems.Windows),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        EnsurePreviewAllowed(response);

        return new DeviceActivationPreviewResult(
            normalizedCode,
            response.StoreCode!,
            response.StoreName!,
            response.DeviceSystem!,
            ToUtcOffset(response.ExpiresAtUtc!.Value));
    }

    public async Task<DeviceRegistrationActionResult> RedeemActivationCodeAsync(
        string activationCode,
        string hardwareId,
        CancellationToken cancellationToken = default)
    {
        var normalizedCode = NormalizeActivationCodeOrThrow(activationCode);
        var recoveryStore = GetActivationRecoveryStore();
        // 仅在用户确认、即将消费开通码时创建恢复记录；preview 永不落盘。
        await recoveryStore.SaveAsync(
            normalizedCode,
            DeviceActivationRecoveryMode.Redeem,
            hardwareId,
            cancellationToken);
        try
        {
            var response = await RedeemActivationCodeCoreAsync(normalizedCode, hardwareId, cancellationToken);
            return CreateActivationActionResult(response, hardwareId, isRebind: false, recoveryStore);
        }
        catch (Exception ex) when (IsDeterministicActivationRejection(ex))
        {
            LogActivationRecoveryCleared("redeem", ex);
            await recoveryStore.ClearAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<DeviceRegistrationActionResult> RebindActivationCodeAsync(
        string activationCode,
        string hardwareId,
        CancellationToken cancellationToken = default)
    {
        var normalizedCode = NormalizeActivationCodeOrThrow(activationCode);
        var recoveryStore = GetActivationRecoveryStore();
        // 当前设备仍有效时走带收银员权限的 rebind；请求前先留下 DPAPI 恢复记录。
        await recoveryStore.SaveAsync(
            normalizedCode,
            DeviceActivationRecoveryMode.Rebind,
            hardwareId,
            cancellationToken);

        try
        {
            var response = await RebindActivationCodeCoreAsync(
                normalizedCode,
                hardwareId,
                cancellationToken);
            return CreateActivationActionResult(response, hardwareId, isRebind: true, recoveryStore);
        }
        catch (Exception ex) when (IsDeterministicActivationRejection(ex))
        {
            LogActivationRecoveryCleared("rebind", ex);
            await recoveryStore.ClearAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<DeviceActivationRecoveryResult?> RecoverActivationCodeAsync(
        string hardwareId,
        CancellationToken cancellationToken = default)
    {
        if (activationRecoveryStore is null)
        {
            return null;
        }

        var recovery = await activationRecoveryStore.GetAsync(cancellationToken);
        if (recovery is null)
        {
            return null;
        }

        if (!string.Equals(recovery.HardwareId, hardwareId, StringComparison.Ordinal))
        {
            // 即使硬件指纹在读取恢复文件后发生漂移，也绝不能向 API 重放或清除原恢复意图。
            throw new DeviceActivationRecoveryUnreadableException();
        }

        // 首次开通可直接匿名恢复；换店必须先重试带旧身份的 rebind，再按明确鉴权失败回退。
        try
        {
            // 换店请求可能根本未到服务端，此时必须先带旧设备凭据重试 rebind；
            // 只有旧身份已因首次提交而失效时，才退回匿名 redeem 幂等取回新凭据。
            var response = recovery.Mode == DeviceActivationRecoveryMode.Rebind
                ? await RebindActivationCodeCoreAsync(
                    recovery.ActivationCode,
                    hardwareId,
                    cancellationToken)
                : await RedeemActivationCodeCoreAsync(
                    recovery.ActivationCode,
                    hardwareId,
                    cancellationToken);
            var actionResult = CreateActivationActionResult(
                response,
                hardwareId,
                recovery.Mode == DeviceActivationRecoveryMode.Rebind,
                activationRecoveryStore);
            return new DeviceActivationRecoveryResult(actionResult, recovery.Mode);
        }
        catch (Exception ex) when (IsDeterministicActivationRejection(ex))
        {
            LogActivationRecoveryCleared(
                recovery.Mode == DeviceActivationRecoveryMode.Rebind ? "recovery-rebind" : "recovery-redeem",
                ex);
            await activationRecoveryStore.ClearAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<DeviceActivationRecoveryMode?> GetPendingActivationRecoveryModeAsync(
        CancellationToken cancellationToken = default)
    {
        if (activationRecoveryStore is null)
        {
            return null;
        }

        return (await activationRecoveryStore.GetAsync(cancellationToken))?.Mode;
    }

    public Task ClearActivationRecoveryAsync(CancellationToken cancellationToken = default)
    {
        return activationRecoveryStore?.ClearAsync(cancellationToken) ?? Task.CompletedTask;
    }

    public async Task<DeviceRegistrationLoadResult> LoadStoresAsync(
        LocalDeviceCache? cachedDevice,
        bool isReregisterMode,
        string? excludedStoreCode = null,
        CancellationToken cancellationToken = default)
    {
        var stores = await deviceApiClient.GetStoresAsync(cancellationToken);
        var visibleStores = stores
            .Where(store => CanShowStore(store, excludedStoreCode))
            .ToArray();

        if (cachedDevice is not null)
        {
            return new DeviceRegistrationLoadResult(
                visibleStores,
                visibleStores.FirstOrDefault(x => string.Equals(x.StoreCode, cachedDevice.StoreCode, StringComparison.OrdinalIgnoreCase))
                    ?? visibleStores.FirstOrDefault(),
                cachedDevice.DeviceCode,
                cachedDevice.DeviceStatus == PendingDeviceStatus,
                cachedDevice.Message ?? T("deviceRegistration.status.pendingApproval", "Device registration is pending approval."));
        }

        return new DeviceRegistrationLoadResult(
            visibleStores,
            visibleStores.FirstOrDefault(),
            string.Empty,
            false,
            isReregisterMode
                ? visibleStores.Length == 0
                    ? T("deviceRegistration.status.noReregisterStores", "No other active stores are available.")
                    : T("deviceRegistration.status.selectReregisterStore", "Select a new store and submit device reregistration.")
                : visibleStores.Length == 0
                    ? T("deviceRegistration.status.noStores", "No active stores are available.")
                    : T("deviceRegistration.status.selectStore", "Select a store and submit this register for approval."));
    }

    public async Task<DeviceRegistrationActionResult> RegisterAsync(
        StoreSelectionItem selectedStore,
        string hardwareId,
        CancellationToken cancellationToken = default)
    {
        var response = await deviceApiClient.RegisterAsync(
            new DeviceRegisterRequest(selectedStore.StoreCode, hardwareId, Environment.MachineName),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var result = CreateActionResult(
            response.DeviceCode,
            response.StoreCode,
            response.StoreName,
            response.DeviceStatus,
            response.IsAllowed,
            response.Message,
            response.AuthorizationCode,
            hardwareId,
            shouldRaiseReregistered: false);

        return ShouldSaveRegisterResponse(response)
            ? result with
            {
                PersistAsync = persistenceCancellationToken =>
                    deviceRepository.SaveAsync(response, hardwareId, persistenceCancellationToken)
            }
            : result;
    }

    public async Task<DeviceRegistrationActionResult> VerifyAsync(
        StoreSelectionItem selectedStore,
        string deviceCode,
        string hardwareId,
        CancellationToken cancellationToken = default)
    {
        var response = await deviceApiClient.VerifyAsync(
            new DeviceVerifyRequest(deviceCode, selectedStore.StoreCode, hardwareId, Environment.MachineName),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var result = CreateActionResult(
            response.DeviceCode,
            response.StoreCode,
            response.StoreName,
            response.DeviceStatus,
            response.IsAllowed,
            response.Message,
            response.AuthorizationCode,
            hardwareId,
            shouldRaiseReregistered: false);

        // 由调用方确认认证结果仍属于当前轮询会话后，才允许写入本地设备缓存。
        return result with
        {
            PersistAsync = persistenceCancellationToken =>
                deviceRepository.SaveAsync(response, hardwareId, persistenceCancellationToken)
        };
    }

    public async Task<DeviceRegistrationActionResult> ReregisterAsync(
        StoreSelectionItem selectedStore,
        string hardwareId,
        CancellationToken cancellationToken = default)
    {
        var response = await deviceApiClient.ReregisterAsync(
            new DeviceReregisterRequest(selectedStore.StoreCode, hardwareId, Environment.MachineName),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var shouldRaiseReregistered = IsAcceptedReregister(response);

        var result = CreateActionResult(
            response.DeviceCode,
            response.StoreCode,
            response.StoreName,
            response.DeviceStatus,
            response.IsAllowed,
            response.Message,
            response.AuthorizationCode,
            hardwareId,
            shouldRaiseReregistered);

        return shouldRaiseReregistered
            ? result with
            {
                PersistAsync = persistenceCancellationToken =>
                    deviceRepository.SaveAsync(response, hardwareId, persistenceCancellationToken)
            }
            : result;
    }

    private DeviceRegistrationActionResult CreateActionResult(
        string deviceCode,
        string storeCode,
        string storeName,
        int deviceStatus,
        bool isAllowed,
        string? message,
        string? authorizationCode,
        string hardwareId,
        bool shouldRaiseReregistered)
    {
        var statusMessage = message ?? (isAllowed
            ? T("deviceRegistration.status.enabled", "Device is enabled.")
            : T("deviceRegistration.status.pendingApproval", "Device registration is pending approval."));
        var shouldRaiseActivated = false;

        if (isAllowed)
        {
            if (string.IsNullOrWhiteSpace(authorizationCode))
            {
                statusMessage = T(
                    "deviceRegistration.status.missingAuthorization",
                    "Device authorization code was not returned. Please verify again.");
            }
            else
            {
                shouldRaiseActivated = true;
            }
        }

        return new DeviceRegistrationActionResult(
            deviceCode,
            storeCode,
            storeName,
            hardwareId,
            deviceStatus == PendingDeviceStatus,
            statusMessage,
            authorizationCode,
            shouldRaiseActivated,
            shouldRaiseReregistered);
    }

    private string T(string key, string fallback)
    {
        return localization?.T(key) ?? fallback;
    }

    private async Task<DeviceActivationCodeRedeemResponse> RedeemActivationCodeCoreAsync(
        string activationCode,
        string hardwareId,
        CancellationToken cancellationToken,
        bool recoveryOnly = false)
    {
        var request = new DeviceActivationCodeRedeemRequest(
            activationCode,
            hardwareId,
            Environment.MachineName,
            DeviceSystems.Windows);
        var response = recoveryOnly
            ? await deviceApiClient.RedeemActivationCodeForRecoveryAsync(request, cancellationToken)
            : await deviceApiClient.RedeemActivationCodeAsync(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureActivationResponseAllowed(response);
        if (recoveryOnly
            && !string.Equals(
                response.ReasonCode,
                DeviceActivationReasonCodes.ActivationRecovered,
                StringComparison.Ordinal))
        {
            throw new CatalogApiException(
                "Device activation recovery returned an unexpected success response.");
        }

        return response;
    }

    private async Task<DeviceActivationCodeRedeemResponse> RebindActivationCodeCoreAsync(
        string activationCode,
        string hardwareId,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await deviceApiClient.RebindActivationCodeAsync(
                new DeviceActivationCodeRebindRequest(activationCode, Environment.MachineName),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureActivationResponseAllowed(response);
            return response;
        }
        catch (CatalogApiException ex) when (ShouldRecoverRebindWithRedeem(ex))
        {
            // rebind 已消费并停用旧身份时，匿名入口仅允许同码同硬件恢复已经生成的新凭据。
            return await RedeemActivationCodeCoreAsync(
                activationCode,
                hardwareId,
                cancellationToken,
                recoveryOnly: true);
        }
    }

    private DeviceRegistrationActionResult CreateActivationActionResult(
        DeviceActivationCodeRedeemResponse response,
        string hardwareId,
        bool isRebind,
        IDeviceActivationRecoveryStore recoveryStore)
    {
        var result = CreateActionResult(
            response.DeviceCode,
            response.StoreCode,
            response.StoreName,
            response.DeviceStatus,
            response.IsAllowed,
            response.Message,
            response.AuthorizationCode,
            hardwareId,
            shouldRaiseReregistered: false);

        return result with
        {
            IsActivationRebind = isRebind,
            PersistAsync = cancellationToken => PersistActivationResultAsync(
                response,
                hardwareId,
                recoveryStore,
                cancellationToken)
        };
    }

    private async Task PersistActivationResultAsync(
        DeviceActivationCodeRedeemResponse response,
        string hardwareId,
        IDeviceActivationRecoveryStore recoveryStore,
        CancellationToken cancellationToken)
    {
        var cacheResponse = new DeviceRegisterResponse(
            response.DeviceCode,
            response.StoreCode,
            response.StoreName,
            response.DeviceStatus,
            response.IsAllowed,
            response.Message,
            response.AuthorizationCode,
            response.ReasonCode);
        await deviceRepository.SaveAsync(cacheResponse, hardwareId, cancellationToken);
        // 完整设备身份与授权码写入现有凭据缓存后，才删除专用恢复文件。
        await recoveryStore.ClearAsync(cancellationToken);
    }

    private IDeviceActivationRecoveryStore GetActivationRecoveryStore()
    {
        return activationRecoveryStore
            ?? throw new InvalidOperationException("Device activation recovery storage is not configured.");
    }

    private static string NormalizeActivationCodeOrThrow(string activationCode)
    {
        if (DeviceActivationCodeNormalizer.TryNormalize(activationCode, out var normalizedCode))
        {
            return normalizedCode;
        }

        throw new CatalogApiException(
            "The activation code format is invalid.",
            errorCode: InvalidActivationCodeError);
    }

    private static void EnsurePreviewAllowed(DeviceActivationCodePreviewResponse response)
    {
        if (!response.IsAllowed)
        {
            throw new CatalogApiException(
                response.Message,
                errorCode: response.ReasonCode);
        }

        if (string.IsNullOrWhiteSpace(response.StoreCode)
            || string.IsNullOrWhiteSpace(response.StoreName)
            || !string.Equals(response.DeviceSystem, DeviceSystems.Windows, StringComparison.Ordinal)
            || response.ExpiresAtUtc is null)
        {
            throw new CatalogApiException("Device activation preview returned incomplete data.");
        }
    }

    private static void EnsureActivationResponseAllowed(DeviceActivationCodeRedeemResponse response)
    {
        if (!response.IsAllowed)
        {
            var rejection = new CatalogApiException(
                response.Message ?? "Device activation was rejected.",
                errorCode: response.ReasonCode);
            rejection.Data[BusinessRejectionMarker] = true;
            throw rejection;
        }

        if (string.IsNullOrWhiteSpace(response.DeviceCode)
            || string.IsNullOrWhiteSpace(response.StoreCode)
            || string.IsNullOrWhiteSpace(response.StoreName)
            || string.IsNullOrWhiteSpace(response.AuthorizationCode))
        {
            throw new CatalogApiException("Device activation returned incomplete credentials.");
        }

        if (!string.Equals(
                response.ReasonCode,
                DeviceActivationReasonCodes.Activated,
                StringComparison.Ordinal)
            && !string.Equals(
                response.ReasonCode,
                DeviceActivationReasonCodes.ActivationRecovered,
                StringComparison.Ordinal))
        {
            throw new CatalogApiException(
                "Device activation returned an unexpected success response.");
        }
    }

    private static bool ShouldRecoverRebindWithRedeem(CatalogApiException exception)
    {
        return exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            || string.Equals(exception.ErrorCode, "DEVICE_AUTH_REQUIRED", StringComparison.Ordinal)
            || string.Equals(exception.ErrorCode, "DEVICE_DISABLED", StringComparison.Ordinal);
    }

    /// <summary>
    /// 服务端确定性拒绝开通码时会清除本机恢复意图（之后不再自动重放），这里留痕便于排查"开通码失效/设备冲突"。
    /// 只记 errorCode 与状态码，开通码本身绝不入日志。
    /// </summary>
    private static void LogActivationRecoveryCleared(string operation, Exception exception)
    {
        var apiException = exception as CatalogApiException;
        var statusCode = apiException?.StatusCode is { } status ? (int)status : (int?)null;
        var errorCode = apiException?.ErrorCode;
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["operation"] = operation,
            ["reason"] = "deterministic-rejection"
        };
        if (!string.IsNullOrWhiteSpace(errorCode))
        {
            properties["errorCode"] = errorCode;
        }

        ConsoleLog.WriteWarning(
            "Device",
            $"activation code rejected by server, clearing local activation recovery intent operation={operation} status={statusCode?.ToString() ?? "-"} errorCode={errorCode ?? "-"}",
            new ApplicationLogContext(StatusCode: statusCode, Properties: properties),
            exception);
    }

    private static bool IsDeterministicActivationRejection(Exception exception)
    {
        if (exception is not CatalogApiException apiException)
        {
            return false;
        }

        // activation 专用接口的结构性 400 表明请求确定未被消费；鉴权、限流、5xx 和空 200 仍属不确定结果。
        if (apiException.StatusCode == HttpStatusCode.BadRequest)
        {
            return true;
        }

        if (!apiException.Data.Contains(BusinessRejectionMarker))
        {
            return false;
        }

        return apiException.ErrorCode is DeviceActivationReasonCodes.ActivationCodeRequired
            or DeviceActivationReasonCodes.NotAvailable
            or DeviceActivationReasonCodes.PlatformMismatch
            or DeviceActivationReasonCodes.StoreUnavailable
            or DeviceActivationReasonCodes.DeviceConflict
            or DeviceActivationReasonCodes.TargetStoreUnchanged
            or DeviceActivationReasonCodes.DeviceStateConflict;
    }

    private static DateTimeOffset ToUtcOffset(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
        return new DateTimeOffset(utc);
    }

    private static bool IsAcceptedReregister(DeviceReregisterResponse response)
    {
        return response.DeviceStatus == PendingDeviceStatus
            && !string.IsNullOrWhiteSpace(response.DeviceCode)
            && !string.IsNullOrWhiteSpace(response.StoreCode);
    }

    private static bool ShouldSaveRegisterResponse(DeviceRegisterResponse response)
    {
        return response.DeviceStatus == PendingDeviceStatus
            || response.IsAllowed && !string.IsNullOrWhiteSpace(response.AuthorizationCode);
    }

    private static bool CanShowStore(StoreSelectionItem store, string? excludedStoreCode)
    {
        // 门店列表只允许启用分店，重新注册时再排除当前分店。
        return store.IsActive
            && (string.IsNullOrWhiteSpace(excludedStoreCode)
                || !string.Equals(store.StoreCode, excludedStoreCode, StringComparison.OrdinalIgnoreCase));
    }
}
