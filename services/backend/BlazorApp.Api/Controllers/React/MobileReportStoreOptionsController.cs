using BlazorApp.Api.Services;
using System.Security.Claims;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React;

/// <summary>移动营业额和商品报表共用的 POS 启用分店范围。</summary>
[ApiController]
[Route("api/react/v1/mobile-reports/store-options")]
[Authorize(Policy = Permissions.Reports.ProductMovementView)]
public sealed class MobileReportStoreOptionsController(
    IProductMovementReportService service,
    IUserService userService,
    IRoleService roleService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetStoreOptions()
    {
        var userGuid = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(userGuid))
            return Forbid();

        // 全店权限取实时角色快照，不能用旧 JWT 中已经撤销的角色扩大分店范围。
        var snapshot = await roleService.GetUserPermissionSnapshotAsync(userGuid);
        if (snapshot?.Success != true || snapshot.Data == null)
            return Forbid();

        var roles = snapshot.Data.RoleNames ?? new List<string>();
        var allStores = roles.Any(role =>
            Permissions.SuperAdminRoleNames.Contains(role, StringComparer.OrdinalIgnoreCase)
            || (!StoreScopePolicy.WarehouseManagerStoreScoped && Permissions.WarehouseManagerRoleNames.Contains(role, StringComparer.OrdinalIgnoreCase)));
        List<string>? storeCodes = null;
        if (!allStores)
        {
            // 读取全部关联店，不依赖用户管理详情所要求的管理分店权限。
            var stores = await userService.GetUserStoresAsync(userGuid);
            if (stores?.Success != true || stores.Data == null)
                return Forbid();
            storeCodes = stores.Data.Select(store => store.StoreCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (storeCodes.Count == 0)
                return Ok(ApiResponse<List<ProductMovementReportStoreOptionDto>>.OK(new()));
        }

        // 复用 IsActive=1、IsDeleted=0 的参数化查询；Web 报表自身权限保持独立。
        var options = await service.GetStoreOptionsAsync(storeCodes);
        return Ok(ApiResponse<List<ProductMovementReportStoreOptionDto>>.OK(options));
    }
}
