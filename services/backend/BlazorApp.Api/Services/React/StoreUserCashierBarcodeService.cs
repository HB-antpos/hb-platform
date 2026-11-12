using System.Security.Claims;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Http;

namespace BlazorApp.Api.Services.React;

public sealed class StoreUserCashierBarcodeService
{
    private readonly SqlSugarContext _context;
    private readonly ICurrentUserService _currentUser;
    private readonly ICurrentUserManageableStoreScopeService _scopeService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly EmployeeCashierBarcodeService _barcodeService;

    public StoreUserCashierBarcodeService(
        SqlSugarContext context,
        ICurrentUserService currentUser,
        ICurrentUserManageableStoreScopeService scopeService,
        IHttpContextAccessor httpContextAccessor,
        EmployeeCashierBarcodeService barcodeService
    )
    {
        _context = context;
        _currentUser = currentUser;
        _scopeService = scopeService;
        _httpContextAccessor = httpContextAccessor;
        _barcodeService = barcodeService;
    }

    public async Task<ApiResponse<EmployeeCashierBarcodeDto>> GetAsync(
        string userGuid,
        string? storeCode
    )
    {
        var denied = await ValidateAccessAsync(userGuid, storeCode);
        return denied ?? await _barcodeService.GetForUserAsync(userGuid);
    }

    public async Task<ApiResponse<EmployeeCashierBarcodeDto>> EnsureAsync(
        string userGuid,
        string? storeCode
    )
    {
        var denied = await ValidateAccessAsync(userGuid, storeCode);
        return denied ?? await _barcodeService.EnsureForUserAsync(userGuid);
    }

    public async Task<ApiResponse<EmployeeCashierBarcodeDto>> ConfirmPrintAsync(
        string userGuid,
        StoreUserCashierBarcodePrintConfirmationRequest request
    )
    {
        var denied = await ValidateAccessAsync(userGuid, request.StoreCode);
        if (denied is not null)
        {
            return denied;
        }
        return await _barcodeService.ConfirmPrintForUserAsync(
            userGuid,
            new EmployeeCashierBarcodePrintConfirmationRequest
            {
                Barcode = request.Barcode,
                PrintAttemptId = request.PrintAttemptId,
            }
        );
    }

    private async Task<ApiResponse<EmployeeCashierBarcodeDto>?> ValidateAccessAsync(
        string userGuid,
        string? storeCode
    )
    {
        var principal = _httpContextAccessor.HttpContext?.User;
        var scope = await _scopeService.GetScopeAsync();
        if (
            principal?.Identity?.IsAuthenticated != true
            || !scope.IsAllowed
            || string.IsNullOrWhiteSpace(_currentUser.GetCurrentUserGuid())
        )
        {
            return Forbidden(scope.Message);
        }

        var isAdmin = HasAnyRole(principal, Permissions.SuperAdminRoleNames);
        // 开关打开后仓库经理与店长同口径（按自己「管理」的分店），见 StoreScopePolicy。
        var isStoreManager = StoreScopePolicy.IsStoreScopedManager(principal);
        // scope.IsAdmin 还包含仓库角色；指定员工条码只允许真实管理员或普通店长账号操作。
        if ((!isAdmin && !isStoreManager) || (isAdmin && !scope.IsAdmin))
        {
            return Forbidden("当前账号没有员工条码管理权限");
        }

        var normalizedStoreCode = storeCode?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedStoreCode))
        {
            return ApiResponse<EmployeeCashierBarcodeDto>.Error(
                "分店不可用",
                "STORE_NOT_AVAILABLE"
            );
        }
        if (!isAdmin && !scope.CanAccessStoreCode(normalizedStoreCode))
        {
            return Forbidden("没有权限管理该分店员工");
        }

        var store = await _context.Db.Queryable<Store>()
            .FirstAsync(item => item.StoreCode == normalizedStoreCode && !item.IsDeleted);
        if (store is null || !store.IsActive)
        {
            return ApiResponse<EmployeeCashierBarcodeDto>.Error(
                "分店未启用 POS 或不存在",
                "STORE_NOT_AVAILABLE"
            );
        }

        var target = await _context.Db.Queryable<User>()
            .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
        if (target is null)
        {
            return ApiResponse<EmployeeCashierBarcodeDto>.Error("用户不存在", "USER_NOT_FOUND");
        }
        if (!target.IsActive)
        {
            return ApiResponse<EmployeeCashierBarcodeDto>.Error(
                "员工账号已停用",
                "CASHIER_BARCODE_INACTIVE"
            );
        }

        var hasStoreRelation = await _context.Db.Queryable<UserStore>()
            .AnyAsync(item =>
                item.UserGUID == userGuid
                && item.StoreGUID == store.StoreGUID
                && !item.IsDeleted
            );
        if (!hasStoreRelation)
        {
            return Forbidden("员工不属于指定分店");
        }

        var roleNames = await _context.Db.Queryable<UserRole, Role>((userRole, role) =>
                userRole.RoleGUID == role.RoleGUID)
            .Where((userRole, role) =>
                userRole.UserGUID == userGuid
                && !userRole.IsDeleted
                && !role.IsDeleted
                && role.IsActive
            )
            .Select((userRole, role) => role.RoleName)
            .ToListAsync();
        if (!roleNames.Any(role => Permissions.EmployeeRoleNames.Contains(
                role,
                StringComparer.OrdinalIgnoreCase
            )))
        {
            return Forbidden("目标账号不是普通员工");
        }

        if (!isAdmin)
        {
            // 关键逻辑：普通店长只能管理其主分店的普通员工，不能给本人或高权限目标创建身份码。
            if (userGuid.Equals(scope.UserGuid, StringComparison.OrdinalIgnoreCase)
                || roleNames.Any(role => Permissions.HighPrivilegeRoleNames.Contains(
                    role,
                    StringComparer.OrdinalIgnoreCase
                )))
            {
                return Forbidden("没有权限管理该员工条码");
            }
        }

        return null;
    }

    private static bool HasAnyRole(ClaimsPrincipal principal, IReadOnlyCollection<string> roles) =>
        principal.Claims.Any(claim =>
            claim.Type == ClaimTypes.Role
            && roles.Contains(claim.Value, StringComparer.OrdinalIgnoreCase)
        );

    private static ApiResponse<EmployeeCashierBarcodeDto> Forbidden(string? message) =>
        ApiResponse<EmployeeCashierBarcodeDto>.Error(
            string.IsNullOrWhiteSpace(message) ? "没有权限管理员工条码" : message,
            "FORBIDDEN"
        );
}
