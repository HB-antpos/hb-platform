using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Devices;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Hbpos.Client.Wpf.Services;

public sealed record MainShellStartupResult(
    PosSessionState Session,
    bool RequiresDeviceRegistration,
    LocalDeviceCache? CachedDevice);

public interface IMainShellStartupService
{
    Task<MainShellStartupResult> EvaluateAsync(
        PosSessionState session,
        bool previewMode,
        CancellationToken cancellationToken = default);

    Task<MainShellStartupResult> EvaluateAfterServerSwitchAsync(
        PosSessionState session,
        CancellationToken cancellationToken = default) =>
        EvaluateAsync(session, previewMode: false, cancellationToken);

    void SetAuthorizedDevice(
        string deviceCode,
        string storeCode,
        string hardwareId,
        string authorizationCode);

    void ClearAuthorization();
}

public sealed class MainShellStartupService(
    ILocalDeviceRepository deviceRepository,
    IDeviceFingerprintService fingerprintService,
    DeviceAuthorizationState deviceAuthorizationState,
    IDeviceApiClient? deviceApiClient = null) : IMainShellStartupService
{
    private const int EnabledDeviceStatus = 1;

    /// <summary>启动验证确定性拒绝时写入本地缓存的状态值（与服务端"未注册"同值）。</summary>
    private const int LocalDeniedDeviceStatus = 3;

    public async Task<MainShellStartupResult> EvaluateAfterServerSwitchAsync(
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        var cachedDevice = await deviceRepository.GetLatestAsync(cancellationToken);
        var hardwareId = fingerprintService.GetHardwareId();
        if (cachedDevice is null ||
            !cachedDevice.IsAllowed ||
            string.IsNullOrWhiteSpace(cachedDevice.AuthorizationCode) ||
            !string.Equals(cachedDevice.HardwareId, hardwareId, StringComparison.OrdinalIgnoreCase) ||
            deviceApiClient is null)
        {
            deviceAuthorizationState.Clear();
            return new MainShellStartupResult(session, true, cachedDevice);
        }

        DeviceVerifyResponse verification;
        try
        {
            verification = await deviceApiClient.VerifyAsync(
                new DeviceVerifyRequest(
                    cachedDevice.DeviceCode,
                    cachedDevice.StoreCode,
                    hardwareId,
                    Environment.MachineName),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or CatalogApiException or JsonException)
        {
            // 热切换后禁止沿用旧服务器的离线授权；目标服务器 Verify 不成功即返回注册页。
            deviceAuthorizationState.Clear();
            ConsoleLog.Write(
                "DeviceServerSwitch",
                $"target verify failed; registration required error={ex.GetType().Name} message={ex.Message}");
            return new MainShellStartupResult(session, true, cachedDevice);
        }

        deviceAuthorizationState.Clear();
        await deviceRepository.SaveAsync(verification, hardwareId, cancellationToken);
        var verifiedDevice = CreateLocalDeviceCache(verification, hardwareId);
        if (verification.DeviceStatus != 1 ||
            !verification.IsAllowed ||
            string.IsNullOrWhiteSpace(verification.AuthorizationCode) ||
            !string.Equals(verification.DeviceCode, cachedDevice.DeviceCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(verification.StoreCode, cachedDevice.StoreCode, StringComparison.OrdinalIgnoreCase))
        {
            return new MainShellStartupResult(session, true, verifiedDevice);
        }

        SetAuthorizedDevice(
            verification.DeviceCode,
            verification.StoreCode,
            hardwareId,
            verification.AuthorizationCode);
        return new MainShellStartupResult(
            session with
            {
                StoreCode = verification.StoreCode,
                StoreName = verification.StoreName,
                DeviceCode = verification.DeviceCode
            },
            false,
            verifiedDevice);
    }

    public async Task<MainShellStartupResult> EvaluateAsync(
        PosSessionState session,
        bool previewMode,
        CancellationToken cancellationToken = default)
    {
        if (previewMode)
        {
            deviceAuthorizationState.Clear();
            return new MainShellStartupResult(session, false, null);
        }

        var cachedDevice = await deviceRepository.GetLatestAsync(cancellationToken);
        var hardwareId = fingerprintService.GetHardwareId();
        if (cachedDevice is not null &&
            deviceApiClient is not null &&
            IsRecoverableLocalDenial(cachedDevice, hardwareId))
        {
            // 旧版本会把网关 HTML/认证页误判为拒绝并写下"本地拒绝（状态 3）"；启动时向服务端复核一次，
            // 只有服务端明确确认同一设备仍启用时才恢复授权。
            return await RecoverLocalDenialAsync(session, cachedDevice, hardwareId, cancellationToken);
        }

        if (cachedDevice is null ||
            !cachedDevice.IsAllowed ||
            string.IsNullOrWhiteSpace(cachedDevice.AuthorizationCode) ||
            !string.Equals(cachedDevice.HardwareId, hardwareId, StringComparison.OrdinalIgnoreCase))
        {
            deviceAuthorizationState.Clear();
            return new MainShellStartupResult(session, true, cachedDevice);
        }

        var startupDevice = cachedDevice;
        if (deviceApiClient is not null)
        {
            DeviceVerifyResponse? verification = null;
            try
            {
                verification = await deviceApiClient.VerifyAsync(
                    new DeviceVerifyRequest(
                        cachedDevice.DeviceCode,
                        cachedDevice.StoreCode,
                        hardwareId,
                        Environment.MachineName),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsDeviceVerifyUnavailable(ex))
            {
                // 传输故障、超时、网关错误页/认证页（DeviceApiUnavailableException）、限流、服务端错误，
                // 以及没有业务错误码的 API 错误，都无法证明服务端拒绝了本设备：使用本地缓存继续离线营业。
                ConsoleLog.Write(
                    "DeviceStartup",
                    $"device authorization verify unavailable; using local cache error={ex.GetType().Name} message={ex.Message}");
            }
            catch (CatalogApiException ex)
            {
                // 只有可解析的 ApiResult 信封携带业务错误码时才是确定性拒绝，持久化拒绝状态以防下次离线复活。
                var deniedVerification = new DeviceVerifyResponse(
                    cachedDevice.DeviceCode,
                    cachedDevice.StoreCode,
                    cachedDevice.StoreName,
                    LocalDeniedDeviceStatus,
                    false,
                    ex.Message);
                deviceAuthorizationState.Clear();
                await deviceRepository.SaveAsync(deniedVerification, hardwareId, CancellationToken.None);
                return new MainShellStartupResult(
                    session,
                    true,
                    CreateLocalDeviceCache(deniedVerification, hardwareId));
            }

            if (verification is not null)
            {
                // 先清除旧内存授权，再持久化服务端结果；保存失败时绝不能恢复已失效的授权。
                deviceAuthorizationState.Clear();
                await deviceRepository.SaveAsync(verification, hardwareId, cancellationToken);
                startupDevice = new LocalDeviceCache(
                    verification.DeviceCode,
                    verification.StoreCode,
                    verification.StoreName,
                    hardwareId,
                    verification.DeviceStatus,
                    verification.IsAllowed,
                    verification.Message,
                    DateTimeOffset.UtcNow,
                    verification.AuthorizationCode);

                // 服务端是设备授权的最终依据；任何非启用、无授权码或身份不一致的响应都回到注册流程。
                if (verification.DeviceStatus != 1 ||
                    !verification.IsAllowed ||
                    string.IsNullOrWhiteSpace(verification.AuthorizationCode) ||
                    !string.Equals(verification.DeviceCode, cachedDevice.DeviceCode, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(verification.StoreCode, cachedDevice.StoreCode, StringComparison.OrdinalIgnoreCase))
                {
                    return new MainShellStartupResult(session, true, startupDevice);
                }
            }
        }

        SetAuthorizedDevice(
            startupDevice.DeviceCode,
            startupDevice.StoreCode,
            startupDevice.HardwareId,
            startupDevice.AuthorizationCode!);

        return new MainShellStartupResult(
            session with
            {
                StoreCode = startupDevice.StoreCode,
                StoreName = startupDevice.StoreName,
                DeviceCode = startupDevice.DeviceCode
            },
            false,
            startupDevice);
    }

    public void SetAuthorizedDevice(
        string deviceCode,
        string storeCode,
        string hardwareId,
        string authorizationCode)
    {
        deviceAuthorizationState.Set(new DeviceAuthorizationContext(
            deviceCode,
            storeCode,
            hardwareId,
            authorizationCode));
    }

    public void ClearAuthorization()
    {
        deviceAuthorizationState.Clear();
    }

    private async Task<MainShellStartupResult> RecoverLocalDenialAsync(
        PosSessionState session,
        LocalDeviceCache cachedDevice,
        string hardwareId,
        CancellationToken cancellationToken)
    {
        deviceAuthorizationState.Clear();
        DeviceVerifyResponse verification;
        try
        {
            verification = await deviceApiClient!.VerifyAsync(
                new DeviceVerifyRequest(
                    cachedDevice.DeviceCode,
                    cachedDevice.StoreCode,
                    hardwareId,
                    Environment.MachineName),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or CatalogApiException or JsonException)
        {
            // 无法拿到服务端明确结论（离线、网关错误、业务错误）时保持本地拒绝，不改写缓存，照常进入注册页。
            ConsoleLog.Write(
                "DeviceStartup",
                $"local device denial recheck unavailable; registration required error={ex.GetType().Name} message={ex.Message}");
            return new MainShellStartupResult(session, true, cachedDevice);
        }

        // 服务端是授权的最终依据：无论结论如何都按服务端返回落盘（停用/待审批等真实状态随之可见）。
        await deviceRepository.SaveAsync(verification, hardwareId, cancellationToken);
        var verifiedDevice = CreateLocalDeviceCache(verification, hardwareId);
        if (!IsEnabledVerificationFor(verification, cachedDevice))
        {
            ConsoleLog.Write(
                "DeviceStartup",
                $"local device denial confirmed by server status={verification.DeviceStatus} allowed={verification.IsAllowed}");
            return new MainShellStartupResult(session, true, verifiedDevice);
        }

        ConsoleLog.Write(
            "DeviceStartup",
            $"local device denial cleared; server confirmed device enabled device={verification.DeviceCode} store={verification.StoreCode}");
        SetAuthorizedDevice(
            verification.DeviceCode,
            verification.StoreCode,
            hardwareId,
            verification.AuthorizationCode!);
        return new MainShellStartupResult(
            session with
            {
                StoreCode = verification.StoreCode,
                StoreName = verification.StoreName,
                DeviceCode = verification.DeviceCode
            },
            false,
            verifiedDevice);
    }

    /// <summary>
    /// 本地拒绝记录的特征：状态 3、不允许、身份完整且硬件指纹与本机一致。
    /// 服务端停用（0）、锁定（2）、待审批（-1）的记录来自服务端结论，不在启动时自动重试。
    /// </summary>
    private static bool IsRecoverableLocalDenial(LocalDeviceCache cachedDevice, string hardwareId)
    {
        return !cachedDevice.IsAllowed &&
            cachedDevice.DeviceStatus == LocalDeniedDeviceStatus &&
            !string.IsNullOrWhiteSpace(cachedDevice.DeviceCode) &&
            !string.IsNullOrWhiteSpace(cachedDevice.StoreCode) &&
            !string.IsNullOrWhiteSpace(hardwareId) &&
            string.Equals(cachedDevice.HardwareId, hardwareId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEnabledVerificationFor(DeviceVerifyResponse verification, LocalDeviceCache expectedDevice)
    {
        return verification.DeviceStatus == EnabledDeviceStatus &&
            verification.IsAllowed &&
            !string.IsNullOrWhiteSpace(verification.AuthorizationCode) &&
            string.Equals(verification.DeviceCode, expectedDevice.DeviceCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(verification.StoreCode, expectedDevice.StoreCode, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 启动验证失败但不足以证明服务端拒绝时返回 true（允许本地缓存离线营业）。
    /// </summary>
    private static bool IsDeviceVerifyUnavailable(Exception exception)
    {
        if (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return true;
        }

        return exception is CatalogApiException apiException &&
            (IsRetryableDeviceApiStatus(apiException.StatusCode) ||
             string.IsNullOrWhiteSpace(apiException.ErrorCode));
    }

    private static bool IsRetryableDeviceApiStatus(HttpStatusCode? statusCode)
    {
        var numericStatus = (int?)statusCode;
        return statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
            numericStatus is >= 500 and <= 599;
    }

    private static LocalDeviceCache CreateLocalDeviceCache(DeviceVerifyResponse verification, string hardwareId)
    {
        return new LocalDeviceCache(
            verification.DeviceCode,
            verification.StoreCode,
            verification.StoreName,
            hardwareId,
            verification.DeviceStatus,
            verification.IsAllowed,
            verification.Message,
            DateTimeOffset.UtcNow,
            verification.AuthorizationCode);
    }
}
