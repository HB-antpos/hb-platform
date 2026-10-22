namespace Hbpos.Contracts.Cashiers;

public static class CashierAuthorizationConstants
{
    public const string HeaderName = "X-HBPOS-Cashier-Authorization";
}

public sealed record CashierBarcodeLoginRequest(
    string StoreCode,
    string UserBarcode,
    string DeviceCode);

public sealed record CashierSessionDto(
    string CashierId,
    string UserGuid,
    string CashierName,
    string StoreCode,
    string DeviceCode,
    string[] Roles,
    string[] PermissionCodes,
    string[] AllowedStoreCodes,
    bool IsSuperAdmin,
    bool IsOfflineCached,
    bool IsEmergencyOverride,
    string? AuthorizationToken = null,
    DateTimeOffset? AuthorizationExpiresAtUtc = null,
    string? EmergencyGrantId = null,
    string? Username = null,
    string? Nickname = null,
    string? FamilyName = null,
    string? GivenName = null);
