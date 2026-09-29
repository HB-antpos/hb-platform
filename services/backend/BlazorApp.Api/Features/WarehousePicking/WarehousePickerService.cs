using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.WarehousePicking;

public sealed record WarehousePickerEligibility(
    string UserGuid,
    string Name,
    bool IsAllowed,
    bool CanOverwriteMinOrderQuantity,
    string? RoleLabel
);

public interface IWarehousePickerService
{
    Task<WarehousePickingResult<WarehousePickerResolveResultDto>> ResolveBarcodeAsync(
        string? barcode,
        string terminalKey
    );

    Task<WarehousePickerEligibility?> GetEligibilityAsync(string userGuid);
}

/// <summary>
/// 拣货人：扫员工码（员工卡 EmployeeCashierBarcodes 或旧收银条码 CashRegisterUsers）识别员工，
/// 仓库员工 / 仓库经理 / 管理员或持有拣货权限（含别名 Warehouse.Manage、Warehouse.ManageOrders）才能拣货。
/// </summary>
internal sealed class WarehousePickerService(
    SqlSugarContext context,
    IRoleService roleService,
    WarehousePickerTicketProtector ticketProtector
) : IWarehousePickerService
{
    private static readonly string[] WarehouseStaffRoleNames = ["WarehouseStaff", "仓库员工"];

    private readonly ISqlSugarClient _db = context.Db;

    public async Task<WarehousePickingResult<WarehousePickerResolveResultDto>> ResolveBarcodeAsync(
        string? barcode,
        string terminalKey
    )
    {
        var code = barcode?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 50)
        {
            return WarehousePickingResult<WarehousePickerResolveResultDto>.Fail(
                400,
                WarehousePickingErrorCodes.InvalidRequest,
                "员工码不能为空"
            );
        }

        var employeeUserGuids = await _db.Queryable<EmployeeCashierBarcode>()
            .Where(item => item.Barcode == code && item.Status)
            .Select(item => item.UserGUID)
            .ToListAsync();
        var legacyUserGuids = await _db.Queryable<CashRegisterUser>()
            .Where(item => item.UserBarcode == code && item.Status)
            .Select(item => item.UserGUID)
            .ToListAsync();
        if (employeeUserGuids.Count > 0 && legacyUserGuids.Count > 0)
        {
            // 两张表同时有效说明条码唯一性已被破坏，与 POS 扫码登录一致：拒绝而不是任选一个身份。
            return WarehousePickingResult<WarehousePickerResolveResultDto>.Fail(
                409,
                WarehousePickingErrorCodes.PickerBarcodeAmbiguous,
                "该员工码对应多个员工，请联系管理员"
            );
        }

        var userGuids = employeeUserGuids
            .Concat(legacyUserGuids)
            .Select(guid => guid?.Trim())
            .Where(guid => !string.IsNullOrEmpty(guid))
            .Select(guid => guid!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (userGuids.Count == 0)
        {
            // 旧收银条码可能从未关联后台账号，同样视为无法识别。
            return WarehousePickingResult<WarehousePickerResolveResultDto>.Fail(
                404,
                WarehousePickingErrorCodes.PickerBarcodeNotFound,
                "找不到该员工码对应的员工"
            );
        }

        if (userGuids.Count > 1)
        {
            return WarehousePickingResult<WarehousePickerResolveResultDto>.Fail(
                409,
                WarehousePickingErrorCodes.PickerBarcodeAmbiguous,
                "该员工码对应多个员工，请联系管理员"
            );
        }

        var eligibility = await GetEligibilityAsync(userGuids[0]);
        if (eligibility == null)
        {
            return WarehousePickingResult<WarehousePickerResolveResultDto>.Fail(
                404,
                WarehousePickingErrorCodes.PickerBarcodeNotFound,
                "该员工账号不存在或已停用"
            );
        }

        if (!eligibility.IsAllowed)
        {
            return WarehousePickingResult<WarehousePickerResolveResultDto>.Fail(
                403,
                WarehousePickingErrorCodes.PickerNotAllowed,
                $"{eligibility.Name} 没有仓库拣货权限"
            );
        }

        var (ticket, expiresAtUtc) = ticketProtector.Issue(
            new WarehousePickerTicketIdentity(
                eligibility.UserGuid,
                eligibility.Name,
                eligibility.CanOverwriteMinOrderQuantity
            ),
            terminalKey,
            DateTime.UtcNow
        );
        return WarehousePickingResult<WarehousePickerResolveResultDto>.Ok(
            new WarehousePickerResolveResultDto
            {
                PickerUserGuid = eligibility.UserGuid,
                PickerName = eligibility.Name,
                RoleLabel = eligibility.RoleLabel,
                Ticket = ticket,
                ExpiresAtUtc = expiresAtUtc,
            }
        );
    }

    public async Task<WarehousePickerEligibility?> GetEligibilityAsync(string userGuid)
    {
        var user = await _db.Queryable<User>()
            .Where(item => item.UserGUID == userGuid && !item.IsDeleted)
            .Select(item => new UserRow
            {
                UserGUID = item.UserGUID,
                Username = item.Username,
                FullName = item.FullName,
                IsActive = item.IsActive,
            })
            .FirstAsync();
        if (user == null || !user.IsActive)
        {
            return null;
        }

        var snapshot = (await roleService.GetUserPermissionSnapshotAsync(user.UserGUID)).Data;
        var roleNames = snapshot?.RoleNames ?? new List<string>();
        var isAdmin = snapshot?.IsSuperAdmin == true || roleNames.Any(Permissions.IsSuperAdminRole);
        var managerRole = roleNames.FirstOrDefault(role =>
            Permissions.WarehouseManagerRoleNames.Contains(role, StringComparer.OrdinalIgnoreCase)
        );
        var staffRole = roleNames.FirstOrDefault(role =>
            WarehouseStaffRoleNames.Contains(role, StringComparer.OrdinalIgnoreCase)
        );
        // PermissionCodes 已按别名展开：持有 Warehouse.Manage / ManageOrders 的人这里会带上 Warehouse.Picking。
        var hasPickingPermission = snapshot?.PermissionCodes?.Contains(
            Permissions.Warehouse.Picking,
            StringComparer.OrdinalIgnoreCase
        ) == true;

        var name = string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName.Trim();
        return new WarehousePickerEligibility(
            user.UserGUID,
            name,
            isAdmin || managerRole != null || staffRole != null || hasPickingPermission,
            isAdmin || managerRole != null,
            managerRole ?? staffRole ?? (isAdmin ? roleNames.FirstOrDefault(Permissions.IsSuperAdminRole) : null)
        );
    }

    private sealed class UserRow
    {
        public string UserGUID { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string? FullName { get; set; }
        public bool IsActive { get; set; }
    }
}
