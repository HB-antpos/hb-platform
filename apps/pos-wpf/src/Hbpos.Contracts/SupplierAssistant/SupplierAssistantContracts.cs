namespace Hbpos.Contracts.SupplierAssistant;

/// <summary>
/// 大屏收银 App 内「订货助手」的设备令牌：App 拿它直接调用中心（主 API）的
/// /api/react/v1/browser-extension/* 接口，作用范围只限这台设备绑定的分店。
/// </summary>
public sealed record SupplierAssistantTokenResponse(
    string AccessToken,
    DateTimeOffset ExpiresAtUtc,
    string StoreCode,
    string DeviceCode,
    string ApiOrigin);

public static class SupplierAssistantErrorCodes
{
    /// <summary>只有大屏收银 App（iPadOS / 安卓平板）能用订货助手。</summary>
    public const string DeviceSystemNotSupported = "SUPPLIER_ASSISTANT_DEVICE_SYSTEM_NOT_SUPPORTED";

    /// <summary>中心判定设备未启用、未登记到该分店等，不签发。</summary>
    public const string DeviceNotEligible = "SUPPLIER_ASSISTANT_DEVICE_NOT_ELIGIBLE";

    /// <summary>POS API 未配置中心地址 / 服务令牌，或中心暂时不可用。</summary>
    public const string Unavailable = "SUPPLIER_ASSISTANT_UNAVAILABLE";
}
