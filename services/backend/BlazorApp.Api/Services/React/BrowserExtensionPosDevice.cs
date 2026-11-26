using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BlazorApp.Api.Data;
using BlazorApp.Api.Models;
using BlazorApp.Shared.Models.POSM;
using Microsoft.IdentityModel.Tokens;

namespace BlazorApp.Api.Services.React;

/// <summary>
/// HB POS 大屏收银（iPad / 安卓平板）App 内「订货助手」使用的设备身份。
///
/// 中文注释：
/// 1. 设备令牌沿用 <c>token_use=browser_extension</c>，因此自动继承浏览器扩展令牌的全部限制：
///    只能访问 /api/react/v1/browser-extension、按自身 exp 严格过期、员工条码自助 / 通知 / 打包等入口一律拒绝。
/// 2. 设备令牌不代表任何员工账号：不带 sub / userId / NameIdentifier / Name，也不带角色和权限；
///    所有按员工权限放行的接口（例如跨店销量）对设备一律拒绝。
/// 3. 分店只来自签发时服务端核对过的设备登记记录，App 无法自行指定或篡改。
/// </summary>
public static class BrowserExtensionPosDevicePrincipal
{
    public const string TokenUse = "browser_extension";
    public const string PrincipalKindClaim = "hb_principal";
    public const string PrincipalKind = "pos_device";
    public const string DeviceCodeClaim = "pos_device_code";
    public const string StoreCodeClaim = "pos_store_code";
    public const string HardwareIdClaim = "pos_hardware_id";
    public const string DeviceSystemClaim = "pos_device_system";

    /// <summary>只有大屏收银 App（iPadOS 与同一 App 的安卓平板构建）可以使用订货助手。</summary>
    public static readonly IReadOnlySet<string> AllowedDeviceSystems = new HashSet<string>(
        ["iPadOS", "AndroidTablet"],
        StringComparer.OrdinalIgnoreCase
    );

    public static bool IsPosDevice(ClaimsPrincipal? principal) =>
        principal != null
        && principal.HasClaim("token_use", TokenUse)
        && principal.HasClaim(PrincipalKindClaim, PrincipalKind);

    public static bool TryGetIdentity(
        ClaimsPrincipal? principal,
        out BrowserExtensionPosDeviceIdentity identity
    )
    {
        identity = default!;
        if (!IsPosDevice(principal))
        {
            return false;
        }

        var deviceCode = principal!.FindFirst(DeviceCodeClaim)?.Value?.Trim();
        var storeCode = principal.FindFirst(StoreCodeClaim)?.Value?.Trim();
        var hardwareId = principal.FindFirst(HardwareIdClaim)?.Value?.Trim();
        var deviceSystem = principal.FindFirst(DeviceSystemClaim)?.Value?.Trim();
        if (
            string.IsNullOrEmpty(deviceCode)
            || string.IsNullOrEmpty(storeCode)
            || string.IsNullOrEmpty(hardwareId)
            || string.IsNullOrEmpty(deviceSystem)
        )
        {
            return false;
        }

        identity = new BrowserExtensionPosDeviceIdentity(deviceCode, storeCode, hardwareId, deviceSystem);
        return true;
    }

    /// <summary>
    /// JWT 通过签名校验后（OnTokenValidated）对设备令牌的实时复核。
    /// 返回 null：不是设备令牌，交给员工会话的原有校验；true：设备有效；false：必须拒绝。
    ///
    /// 中文注释：必须排在取员工 userGuid 之前，否则设备令牌会因缺少用户标识被拒，
    /// 或被 Name 之类的兜底字段误当成某个账号。设备停用 / 换店后下一次请求立即失效，不等令牌过期。
    /// </summary>
    public static async Task<bool?> ValidateOnTokenValidatedAsync(
        ClaimsPrincipal principal,
        ClaimsIdentity identity,
        Func<IBrowserExtensionPosDeviceDirectory> resolveDirectory,
        CancellationToken cancellationToken
    )
    {
        if (!IsPosDevice(principal))
        {
            return null;
        }

        if (!TryGetIdentity(principal, out var device))
        {
            return false;
        }

        var active = await resolveDirectory().FindActiveAsync(
            device.DeviceCode,
            device.StoreCode,
            device.HardwareId,
            cancellationToken
        );
        if (
            active == null
            || !string.Equals(active.StoreCode, device.StoreCode, StringComparison.OrdinalIgnoreCase)
        )
        {
            return false;
        }

        // 设备不带任何角色 / 权限：即使令牌里被塞了也全部剥掉。
        foreach (
            var stale in identity.Claims
                .Where(claim => claim.Type == ClaimTypes.Role || claim.Type == "permission")
                .ToList()
        )
        {
            identity.RemoveClaim(stale);
        }

        return true;
    }

    /// <summary>
    /// 分类采集等需要记录「谁回传」的地方用的操作者名：设备记成「pos-device:分店:设备号」，员工沿用登录名。
    /// </summary>
    public static string? GetActorName(ClaimsPrincipal? principal) =>
        TryGetIdentity(principal, out var identity)
            ? $"pos-device:{identity.StoreCode}:{identity.DeviceCode}"
            : principal?.Identity?.Name;
}

public sealed record BrowserExtensionPosDeviceIdentity(
    string DeviceCode,
    string StoreCode,
    string HardwareId,
    string DeviceSystem
);

public interface IBrowserExtensionPosDeviceDirectory
{
    /// <summary>
    /// 按「设备号 + 分店 + 硬件识别码」精确查登记记录，仅当设备启用且是大屏收银 App 时返回；其余一律 null。
    /// </summary>
    Task<BrowserExtensionPosDeviceIdentity?> FindActiveAsync(
        string deviceCode,
        string storeCode,
        string hardwareId,
        CancellationToken cancellationToken = default
    );
}

public sealed class BrowserExtensionPosDeviceDirectory(POSMSqlSugarContext context)
    : IBrowserExtensionPosDeviceDirectory
{
    public async Task<BrowserExtensionPosDeviceIdentity?> FindActiveAsync(
        string deviceCode,
        string storeCode,
        string hardwareId,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedDeviceCode = (deviceCode ?? string.Empty).Trim();
        var normalizedStoreCode = (storeCode ?? string.Empty).Trim();
        var normalizedHardwareId = (hardwareId ?? string.Empty).Trim();
        if (
            normalizedDeviceCode.Length == 0
            || normalizedStoreCode.Length == 0
            || normalizedHardwareId.Length == 0
        )
        {
            return null;
        }

        var device = await context.Db.Queryable<POSM_设备注册信息表>()
            .Where(item =>
                item.系统设备编号 == normalizedDeviceCode
                && item.分店代码 == normalizedStoreCode
                && item.设备硬件识别码 == normalizedHardwareId
            )
            .OrderByDescending(item => item.ID)
            .FirstAsync();
        return ToActiveIdentity(device);
    }

    /// <summary>纯函数：登记记录 → 可用的设备身份（停用、非大屏收银 App、缺分店都返回 null）。</summary>
    internal static BrowserExtensionPosDeviceIdentity? ToActiveIdentity(POSM_设备注册信息表? device)
    {
        if (
            device == null
            || device.设备状态 != (int)DeviceStatus.启用
            || string.IsNullOrWhiteSpace(device.分店代码)
            || string.IsNullOrWhiteSpace(device.系统设备编号)
            || string.IsNullOrWhiteSpace(device.设备硬件识别码)
            || !BrowserExtensionPosDevicePrincipal.AllowedDeviceSystems.Contains(
                (device.设备系统 ?? string.Empty).Trim()
            )
        )
        {
            return null;
        }

        return new BrowserExtensionPosDeviceIdentity(
            device.系统设备编号.Trim(),
            device.分店代码.Trim(),
            device.设备硬件识别码.Trim(),
            device.设备系统.Trim()
        );
    }
}

public sealed record BrowserExtensionPosDeviceIssuedToken(string AccessToken, DateTime ExpiresAtUtc);

public interface IBrowserExtensionPosDeviceTokenIssuer
{
    BrowserExtensionPosDeviceIssuedToken Issue(BrowserExtensionPosDeviceIdentity identity);
}

public sealed class BrowserExtensionPosDeviceTokenIssuer(
    IConfiguration configuration,
    TimeProvider? timeProvider = null
) : IBrowserExtensionPosDeviceTokenIssuer
{
    // 与网站会话换来的扩展令牌同为五分钟；设备停用后每次请求都会实时拒绝，不靠令牌过期兜底。
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public BrowserExtensionPosDeviceIssuedToken Issue(BrowserExtensionPosDeviceIdentity identity)
    {
        var settings = configuration.GetSection("Jwt").Get<JwtSettings>()
            ?? throw new InvalidOperationException("Jwt configuration is missing.");
        if (
            string.IsNullOrWhiteSpace(settings.Key)
            || string.IsNullOrWhiteSpace(settings.Issuer)
            || string.IsNullOrWhiteSpace(settings.Audience)
        )
        {
            throw new InvalidOperationException("Jwt configuration is incomplete.");
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var expires = now.Add(Lifetime);
        var token = new JwtSecurityToken(
            issuer: settings.Issuer,
            audience: settings.Audience,
            claims: BuildClaims(identity),
            notBefore: now,
            expires: expires,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.Key)),
                SecurityAlgorithms.HmacSha256
            )
        );
        return new BrowserExtensionPosDeviceIssuedToken(
            new JwtSecurityTokenHandler().WriteToken(token),
            expires
        );
    }

    /// <summary>
    /// 关键逻辑：刻意不放任何员工身份声明（sub / userId / NameIdentifier / Name / role），
    /// 免得下游把设备误当成某个账号去查权限或门店。
    /// </summary>
    public static IReadOnlyList<Claim> BuildClaims(BrowserExtensionPosDeviceIdentity identity) =>
    [
        new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        new("token_use", BrowserExtensionPosDevicePrincipal.TokenUse),
        new(
            BrowserExtensionPosDevicePrincipal.PrincipalKindClaim,
            BrowserExtensionPosDevicePrincipal.PrincipalKind
        ),
        new(BrowserExtensionPosDevicePrincipal.DeviceCodeClaim, identity.DeviceCode),
        new(BrowserExtensionPosDevicePrincipal.StoreCodeClaim, identity.StoreCode),
        new(BrowserExtensionPosDevicePrincipal.HardwareIdClaim, identity.HardwareId),
        new(BrowserExtensionPosDevicePrincipal.DeviceSystemClaim, identity.DeviceSystem),
    ];
}
