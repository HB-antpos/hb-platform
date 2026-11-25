using System.ComponentModel.DataAnnotations;

namespace Hbpos.Contracts.Devices;

public sealed record DeviceVerifyRequest(
    string DeviceCode,
    string StoreCode,
    string? HardwareId = null,
    string? TerminalName = null,
    string? DeviceSystem = null);

public sealed record DeviceVerifyResponse(
    string DeviceCode,
    string StoreCode,
    string StoreName,
    int DeviceStatus,
    bool IsAllowed,
    string? Message = null,
    string? AuthorizationCode = null,
    bool ExactIdentityMatched = false);

public sealed record DeviceRegisterRequest(
    string StoreCode,
    string HardwareId,
    string? TerminalName = null,
    string? DeviceSystem = null,
    [StringLength(128, MinimumLength = 16)] string? ProvisioningCode = null);

public static class DeviceSystems
{
    public const string Windows = "Windows";

    public const string IpadOs = "iPadOS";

    public const string Ios = "iOS";

    public const string Android = "Android";

    /// <summary>
    /// 安卓平板上运行的大屏收银 App（与 iPadOS 同一个 App 的安卓构建），业务类别等同 iPadOS，不是手持端。
    /// </summary>
    public const string AndroidTablet = "AndroidTablet";

    public static bool TryNormalize(string? value, out string normalized)
    {
        var candidate = (value ?? string.Empty).Trim();
        if (candidate.Length == 0 || string.Equals(candidate, Windows, StringComparison.OrdinalIgnoreCase))
        {
            normalized = Windows;
            return true;
        }

        if (string.Equals(candidate, IpadOs, StringComparison.OrdinalIgnoreCase))
        {
            normalized = IpadOs;
            return true;
        }

        // handheld 平台属于跨端安全合同，必须按原始请求精确匹配，不能接受空白或大小写变体。
        if (string.Equals(value, Ios, StringComparison.Ordinal))
        {
            normalized = Ios;
            return true;
        }

        if (string.Equals(value, Android, StringComparison.Ordinal))
        {
            normalized = Android;
            return true;
        }

        // 安卓平板大屏收银同样属于跨端安全合同，只接受精确大小写，避免与手持 Android 混淆。
        if (string.Equals(value, AndroidTablet, StringComparison.Ordinal))
        {
            normalized = AndroidTablet;
            return true;
        }

        normalized = string.Empty;
        return false;
    }

    public static bool IsIpadOs(string? value) =>
        string.Equals(value?.Trim(), IpadOs, StringComparison.OrdinalIgnoreCase);

    public static bool RequiresExactHardwareId(string? value) =>
        TryNormalize(value, out var normalized)
        && !string.Equals(normalized, Windows, StringComparison.Ordinal);
}

public sealed record DeviceRegisterResponse(
    string DeviceCode,
    string StoreCode,
    string StoreName,
    int DeviceStatus,
    bool IsAllowed,
    string? Message = null,
    string? AuthorizationCode = null,
    string? ReasonCode = null);

public sealed record DeviceReregisterRequest(
    string TargetStoreCode,
    string HardwareId,
    string? TerminalName = null);

public sealed record DeviceReregisterResponse(
    string DeviceCode,
    string StoreCode,
    string StoreName,
    int DeviceStatus,
    bool IsAllowed,
    string? Message = null,
    string? AuthorizationCode = null);

public sealed record DeviceRegistrationResetRequest(Guid OperationId);

public sealed record DeviceRegistrationResetResponse(
    Guid OperationId,
    string DeviceCode,
    string StoreCode,
    DateTime DisabledAtUtc);
