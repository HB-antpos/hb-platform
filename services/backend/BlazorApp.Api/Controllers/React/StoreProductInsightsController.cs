using System.Globalization;
using System.Security.Claims;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.ProductInsights;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SqlSugar;

namespace BlazorApp.Api.Controllers.React;

/// <summary>移动端单店商品进销查询；读取权限与商品维护查询保持一致。</summary>
[ApiController]
[Route("api/react/v1/product-insights")]
[AllowAnonymous]
public sealed class StoreProductInsightsController(
    StoreProductInsightQueryService queryService,
    IDeviceRegistrationService deviceRegistrationService,
    IRoleService roleService,
    IMapper mapper,
    SqlSugarContext context,
    ILogger<StoreProductInsightsController> logger
) : ControllerBase
{
    private readonly ISqlSugarClient _db = context.Db;

    [HttpGet("store")]
    public async Task<IActionResult> GetStoreInsight(
        [FromQuery] string? storeCode,
        [FromQuery] string? productCode,
        [FromQuery] string? startDate,
        [FromQuery] string? endDate,
        [FromQuery] int? days = null,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(storeCode) || string.IsNullOrWhiteSpace(productCode))
        {
            return BadRequest(new { success = false, message = "storeCode 和 productCode 不能为空" });
        }

        var access = await ResolveAccessContextAsync(cancellationToken);
        if (!access.IsAllowed)
        {
            return Unauthorized(ApiResponse<StoreProductInsightDto>.Error(access.Message));
        }

        var normalizedStoreCode = storeCode.Trim();
        if (!StoreProductInsightRules.CanAccessStore(access.StoreCodes, normalizedStoreCode))
        {
            return Forbid();
        }

        var range = await ResolveRangeAsync(normalizedStoreCode, startDate, endDate, days, cancellationToken);
        if (range.Error != null)
        {
            return BadRequest(new { success = false, message = range.Error });
        }
        if (!range.Value.HasValue)
        {
            return NotFound(new { success = false, message = "分店不存在或未启用" });
        }

        try
        {
            var result = await queryService.GetAsync(
                normalizedStoreCode,
                productCode.Trim(),
                range.Value.Value.StartDate,
                range.Value.Value.EndDate,
                cancellationToken
            );
            if (result == null)
            {
                return NotFound(new { success = false, message = "商品不存在，或分店不存在/未启用" });
            }

            return Ok(new { success = true, data = result });
        }
        catch (Exception ex)
        {
            // 查询异常必须返回失败，不能将数据源故障伪装成零销量或无进货。
            logger.LogError(ex, "查询单店商品进销失败 StoreCode={StoreCode} ProductCode={ProductCode}", normalizedStoreCode, productCode);
            return StatusCode(500, new { success = false, message = "商品进销数据暂时无法读取" });
        }
    }

    private async Task<(DateTime StartDate, DateTime EndDate)?> ResolveDefaultRangeAsync(string storeCode, int days, CancellationToken cancellationToken) =>
        await queryService.GetDefaultRangeAsync(storeCode, days, cancellationToken);

    private async Task<( (DateTime StartDate, DateTime EndDate)? Value, string? Error)> ResolveRangeAsync(
        string storeCode, string? startDate, string? endDate, int? days, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(startDate) && string.IsNullOrWhiteSpace(endDate))
        {
            // 不传日期时按门店「今天」倒推 days 天；旧版客户端不带 days，仍保持 90 天。
            var resolvedDays = days ?? StoreProductInsightRules.DefaultRangeDays;
            if (!StoreProductInsightRules.IsValidDefaultRangeDays(resolvedDays))
            {
                return (null, $"days 必须在 1 到 {StoreProductInsightRules.MaxDefaultRangeDays} 之间");
            }
            return (await ResolveDefaultRangeAsync(storeCode, resolvedDays, cancellationToken), null);
        }
        if (string.IsNullOrWhiteSpace(startDate) || string.IsNullOrWhiteSpace(endDate)
            || !DateTime.TryParseExact(startDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedStart)
            || !DateTime.TryParseExact(endDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedEnd))
        {
            return (null, "startDate 和 endDate 必须同时为 YYYY-MM-DD");
        }
        if (!StoreProductInsightRules.IsValidRange(parsedStart, parsedEnd))
        {
            return (null, "startDate 不能晚于 endDate");
        }
        return ((parsedStart.Date, parsedEnd.Date), null);
    }

    // 与 ReactStoreProductMaintenanceController 保持相同的登录用户、设备与门店范围模型。
    private async Task<StoreAccessContext> ResolveAccessContextAsync(CancellationToken cancellationToken)
    {
        if (User?.Identity?.IsAuthenticated == true)
        {
            try
            {
                var userGuid = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                var actorLabel = User.Identity?.Name;
                if (string.IsNullOrWhiteSpace(userGuid) && !string.IsNullOrWhiteSpace(actorLabel))
                {
                    userGuid = await _db.Queryable<User>()
                        .Where(user => user.Username == actorLabel && !user.IsDeleted)
                        .Select(user => user.UserGUID)
                        .FirstAsync();
                }
                if (string.IsNullOrWhiteSpace(userGuid))
                {
                    return StoreAccessContext.Denied("未找到当前用户信息");
                }

                // 提升到全店的角色以实时快照为唯一来源，撤销后的旧 JWT 声明不能继续扩大范围。
                var snapshot = await roleService.GetUserPermissionSnapshotAsync(userGuid);
                if (snapshot?.Success != true || snapshot.Data == null)
                {
                    return StoreAccessContext.Denied("读取当前角色权限失败");
                }
                if (HasElevatedStoreAccess(snapshot.Data.RoleNames))
                {
                    return StoreAccessContext.Allowed(null);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var storeCodes = await _db.Queryable<UserStore>()
                    .InnerJoin<Store>((userStore, store) => userStore.StoreGUID == store.StoreGUID)
                    .Where((userStore, store) => userStore.UserGUID == userGuid && !userStore.IsDeleted && !store.IsDeleted)
                    .Select((userStore, store) => store.StoreCode)
                    .ToListAsync();
                return StoreAccessContext.Allowed(storeCodes);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "解析单店商品进销登录用户门店权限失败");
                return StoreAccessContext.Denied("解析当前用户分店权限失败");
            }
        }

        var hardwareId = Request.Headers["X-Device-Id"].FirstOrDefault();
        var authCode = Request.Headers["X-Auth-Code"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(hardwareId) || string.IsNullOrWhiteSpace(authCode))
        {
            return StoreAccessContext.Denied("未登录且缺少设备授权信息");
        }
        if (!await deviceRegistrationService.ValidateDeviceAuthCodeAsync(hardwareId, authCode))
        {
            return StoreAccessContext.Denied("设备授权无效");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var deviceEntity = await deviceRegistrationService.GetDeviceByHardwareIdAsync(hardwareId);
        if (deviceEntity == null)
        {
            return StoreAccessContext.Denied("设备不存在");
        }
        var device = mapper.Map<DeviceDataDto>(deviceEntity);
        return device.Status == 1 && !string.IsNullOrWhiteSpace(device.StoreCode)
            ? StoreAccessContext.Allowed([device.StoreCode])
            : StoreAccessContext.Denied("设备未启用或未绑定分店");
    }

    internal static bool HasElevatedStoreAccess(IEnumerable<string>? roleNames)
    {
        return (roleNames ?? Array.Empty<string>()).Any(role =>
            Permissions.SuperAdminRoleNames.Contains(role, StringComparer.OrdinalIgnoreCase)
            || role.Equals("Manager", StringComparison.OrdinalIgnoreCase)
            || role.Equals("WarehouseManager", StringComparison.OrdinalIgnoreCase)
            || role.Equals("WarehouseStaff", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record StoreAccessContext(bool IsAllowed, List<string>? StoreCodes, string Message)
    {
        public static StoreAccessContext Allowed(List<string>? storeCodes) => new(true, storeCodes, string.Empty);
        public static StoreAccessContext Denied(string message) => new(false, null, message);
    }
}
