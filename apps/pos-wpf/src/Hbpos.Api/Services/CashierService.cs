using System.Diagnostics;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Constants;
using Hbpos.Api.Auth;
using Hbpos.Api.Data;
using Hbpos.Contracts.Cashiers;

namespace Hbpos.Api.Services;

public interface ICashierService
{
    Task<CashierSessionDto?> BarcodeLoginAsync(
        CashierBarcodeLoginRequest request,
        CancellationToken cancellationToken);

    Task<CashierSessionDto?> BarcodeLoginAsync(
        CashierBarcodeLoginRequest request,
        string? hardwareId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(hardwareId))
        {
            throw new NotSupportedException("Hardware-bound cashier login requires an explicit implementation.");
        }

        return BarcodeLoginAsync(request, cancellationToken);
    }

    Task<bool> HasAnyPermissionAsync(
        string userGuid,
        string storeCode,
        IReadOnlyCollection<string> permissionCodes,
        CancellationToken cancellationToken);

    Task<CashierSessionDto?> RefreshSessionAsync(
        CashierAuthorizationTicket ticket,
        CancellationToken cancellationToken);
}

public sealed class CashierService(
    HbposSqlSugarContext dbContext,
    ICashierAuthorizationTicketService ticketService,
    ILogger<CashierService> logger) : ICashierService
{
    private static readonly string[] LineDiscountPermissions =
    [
        Permissions.PosTerminal.Sales.LineManualDiscount,
        Permissions.PosTerminal.Sales.LineQuickDiscount10Percent,
        Permissions.PosTerminal.Sales.LineQuickDiscount20Percent,
        Permissions.PosTerminal.Sales.LineQuickDiscount30Percent,
        Permissions.PosTerminal.Sales.LineQuickDiscount40Percent,
        Permissions.PosTerminal.Sales.LineQuickDiscount50Percent,
    ];

    private static readonly string[] OrderDiscountPermissions =
    [
        Permissions.PosTerminal.Sales.OrderManualDiscount,
        Permissions.PosTerminal.Sales.OrderQuickDiscount10Percent,
        Permissions.PosTerminal.Sales.OrderQuickDiscount20Percent,
        Permissions.PosTerminal.Sales.OrderQuickDiscount30Percent,
        Permissions.PosTerminal.Sales.OrderQuickDiscount40Percent,
        Permissions.PosTerminal.Sales.OrderQuickDiscount50Percent,
    ];

    public async Task<CashierSessionDto?> BarcodeLoginAsync(
        CashierBarcodeLoginRequest request,
        CancellationToken cancellationToken) => await BarcodeLoginAsync(request, null, cancellationToken);

    public async Task<CashierSessionDto?> BarcodeLoginAsync(
        CashierBarcodeLoginRequest request,
        string? hardwareId,
        CancellationToken cancellationToken)
    {
        var userBarcode = request.UserBarcode?.Trim();
        var storeCode = request.StoreCode?.Trim();
        var deviceCode = request.DeviceCode?.Trim();
        if (string.IsNullOrWhiteSpace(userBarcode) ||
            string.IsNullOrWhiteSpace(storeCode) ||
            string.IsNullOrWhiteSpace(deviceCode))
        {
            return null;
        }

        var totalStopwatch = Stopwatch.StartNew();
        var timings = new CashierLoginTimings();
        try
        {
        var identityStopwatch = Stopwatch.StartNew();
        var legacyQuery = dbContext.MainDb.Queryable<CashRegisterUser>()
            .Where(x => x.UserBarcode == userBarcode && x.Status)
            .Select(x => new CashierIdentityCandidate
            {
                SourceKind = 1,
                HGUID = x.HGUID,
                Id = x.Id,
                UserGUID = x.UserGUID,
            });
        var employeeQuery = dbContext.MainDb.Queryable<EmployeeCashierBarcode>()
            .Where(x => x.Barcode == userBarcode && x.Status)
            .Select(x => new CashierIdentityCandidate
            {
                SourceKind = 2,
                HGUID = x.HGUID,
                Id = 0,
                UserGUID = x.UserGUID,
            });

        // 关键逻辑：一次往返同时查询两种收银身份，仍严格拒绝双表冲突。
        var identityCandidates = await dbContext.MainDb
            .UnionAll(legacyQuery, employeeQuery)
            .ToListAsync(cancellationToken);
        timings.IdentityMs = identityStopwatch.ElapsedMilliseconds;
        var cashier = identityCandidates.FirstOrDefault(x => x.SourceKind == 1);
        var employeeCashier = identityCandidates.FirstOrDefault(x => x.SourceKind == 2);
        if (cashier is not null && employeeCashier is not null)
        {
            // 关键逻辑：双表同时有效表示数据唯一性已损坏，禁止任意选择身份继续授权。
            logger.LogCritical(
                "收银条码同时命中 legacy 与 employee 表，已拒绝登录。Legacy={LegacyHguid}, Employee={EmployeeHguid}",
                cashier.HGUID,
                employeeCashier.HGUID);
            return null;
        }
        var cashierUserGuid = cashier?.UserGUID?.Trim() ?? employeeCashier?.UserGUID?.Trim();
        if (string.IsNullOrWhiteSpace(cashierUserGuid))
        {
            return null;
        }

        var cashierId = employeeCashier?.HGUID
            ?? (string.IsNullOrWhiteSpace(cashier!.HGUID) ? cashier.Id.ToString() : cashier.HGUID);
        return await CreateSessionAsync(
            cashierId,
            cashierUserGuid,
            storeCode,
            deviceCode,
            cancellationToken,
            timings,
            hardwareId);
        }
        finally
        {
            logger.LogInformation(
                "收银扫码登录耗时。IdentityMs={IdentityMs}, StoreMs={StoreMs}, PermissionMs={PermissionMs}, TotalMs={TotalMs}",
                timings.IdentityMs,
                timings.StoreMs,
                timings.PermissionMs,
                totalStopwatch.ElapsedMilliseconds);
        }
    }

    public Task<CashierSessionDto?> RefreshSessionAsync(
        CashierAuthorizationTicket ticket,
        CancellationToken cancellationToken)
    {
        return CreateSessionAsync(
            ticket.CashierId,
            ticket.UserGuid,
            ticket.StoreCode,
            ticket.DeviceCode,
            cancellationToken,
            hardwareId: ticket.HardwareId,
            barcodeAuthenticatedAtUtc: ticket.BarcodeAuthenticatedAtUtc,
            preserveBarcodeAuthenticationTime: true);
    }

    public async Task<bool> HasAnyPermissionAsync(
        string userGuid,
        string storeCode,
        IReadOnlyCollection<string> permissionCodes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userGuid) ||
            string.IsNullOrWhiteSpace(storeCode) ||
            permissionCodes.Count == 0)
        {
            return false;
        }

        var user = await dbContext.MainDb.Queryable<User>()
            .FirstAsync(x => x.UserGUID == userGuid && x.IsActive && !x.IsDeleted, cancellationToken);
        if (user is null)
        {
            return false;
        }

        var allowedStores = await dbContext.MainDb.Queryable<UserStore>()
            .InnerJoin<Store>((us, s) => us.StoreGUID == s.StoreGUID)
            .Where((us, s) =>
                us.UserGUID == userGuid &&
                !us.IsDeleted &&
                s.IsActive &&
                !s.IsDeleted)
            .Select((us, s) => new { s.StoreGUID, s.StoreCode })
            .ToListAsync(cancellationToken);
        var allowedStore = allowedStores.FirstOrDefault(store =>
            string.Equals(store.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase));
        if (allowedStore is null)
        {
            return false;
        }

        // 每次敏感请求都重新读取角色和权限，停用、调店或撤权立即生效。
        var snapshot = await GetPermissionSnapshotAsync(userGuid, allowedStore.StoreGUID, cancellationToken);
        return snapshot.IsSuperAdmin ||
            snapshot.PermissionCodes.Intersect(permissionCodes, StringComparer.OrdinalIgnoreCase).Any();
    }

    private async Task<CashierPermissionSnapshot> GetPermissionSnapshotAsync(
        string userGuid,
        string storeGuid,
        CancellationToken cancellationToken)
    {
        var roleEntries = await dbContext.MainDb.Queryable<UserRole>()
            .InnerJoin<Role>((ur, r) => ur.RoleGUID == r.RoleGUID)
            .Where((ur, r) =>
                ur.UserGUID == userGuid
                && !ur.IsDeleted
                && r.IsActive
                && !r.IsDeleted)
            .Select((ur, r) => new { r.RoleGUID, r.RoleName })
            .ToListAsync(cancellationToken);
        var roleNames = roleEntries
            .Select(x => x.RoleName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var isSuperAdmin = roleNames.Any(Permissions.IsSuperAdminRole);

        string[] permissionCodes;
        if (isSuperAdmin)
        {
            permissionCodes = (await dbContext.MainDb.Queryable<SysPermission>()
                    .Where(x => !x.IsDeleted)
                    .Select(x => x.Code)
                    .ToListAsync(cancellationToken))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        else
        {
            var roleGuids = roleEntries
                .Select(x => x.RoleGUID)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var directPermissionQuery = dbContext.MainDb.Queryable<SysUserPermission>()
                .Where(x => x.UserGuid == userGuid && !x.IsDeleted)
                .Select(x => new CashierPermissionCandidate { PermissionCode = x.PermissionCode });
            List<CashierPermissionCandidate> permissionEntries;
            if (roleGuids.Count == 0)
            {
                permissionEntries = await directPermissionQuery.ToListAsync(cancellationToken);
            }
            else
            {
                var rolePermissionQuery = dbContext.MainDb.Queryable<SysRolePermission>()
                    .Where(x => roleGuids.Contains(x.RoleGuid) && !x.IsDeleted)
                    .Select(x => new CashierPermissionCandidate { PermissionCode = x.PermissionCode });
                // 关键逻辑：角色权限与用户直授权限合并为一次数据库往返。
                permissionEntries = await dbContext.MainDb
                    .UnionAll(rolePermissionQuery, directPermissionQuery)
                    .ToListAsync(cancellationToken);
            }

            // 关键逻辑：权限别名展开保持和后台 RoleService 快照一致。
            permissionCodes = Permissions.ExpandPermissionCodes(
                    permissionEntries.Select(x => x.PermissionCode))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        if (!isSuperAdmin)
        {
            var storeOverrides = await dbContext.MainDb.Queryable<SysUserStorePosPermission>()
                .Where(x =>
                    x.UserGuid == userGuid &&
                    x.StoreGuid == storeGuid &&
                    !x.IsDeleted)
                .Select(x => new { x.PermissionCode, x.IsGranted })
                .ToListAsync(cancellationToken);
            var effectivePermissions = permissionCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var storeOverride in storeOverrides)
            {
                // 只允许分店覆盖 POS 权限，避免历史脏数据影响后台权限。
                if (!storeOverride.PermissionCode.StartsWith(
                        "Permissions.PosTerminal.",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (storeOverride.IsGranted)
                {
                    effectivePermissions.Add(storeOverride.PermissionCode);
                }
                else
                {
                    effectivePermissions.Remove(storeOverride.PermissionCode);
                }
            }

            permissionCodes = effectivePermissions.ToArray();
        }

        permissionCodes = AddLegacyDiscountCompatibility(permissionCodes);
        return new CashierPermissionSnapshot(roleNames, permissionCodes, isSuperAdmin);
    }

    private async Task<CashierSessionDto?> CreateSessionAsync(
        string cashierId,
        string userGuid,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken,
        CashierLoginTimings? timings = null,
        string? hardwareId = null,
        DateTimeOffset? barcodeAuthenticatedAtUtc = null,
        bool preserveBarcodeAuthenticationTime = false)
    {
        var storeStopwatch = Stopwatch.StartNew();
        // 关键逻辑：用户状态与全部允许门店一次读取，减少远程数据库往返。
        var userStores = await dbContext.MainDb.Queryable<User>()
            .InnerJoin<UserStore>((user, userStore) => user.UserGUID == userStore.UserGUID)
            .InnerJoin<Store>((user, userStore, store) => userStore.StoreGUID == store.StoreGUID)
            .Where((user, userStore, store) =>
                user.UserGUID == userGuid && user.IsActive && !user.IsDeleted &&
                !userStore.IsDeleted && store.IsActive && !store.IsDeleted)
            .Select((user, userStore, store) => new CashierUserStoreCandidate
            {
                Username = user.Username,
                FullName = user.FullName,
                Nickname = user.Nickname,
                FamilyName = user.FamilyName,
                GivenName = user.GivenName,
                StoreGuid = store.StoreGUID,
                StoreCode = store.StoreCode,
            })
            .ToListAsync(cancellationToken);
        if (timings is not null) timings.StoreMs = storeStopwatch.ElapsedMilliseconds;
        var currentStore = userStores.FirstOrDefault(store =>
            string.Equals(store.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase));
        if (currentStore is null)
        {
            return null;
        }

        var permissionStopwatch = Stopwatch.StartNew();
        var snapshot = await GetPermissionSnapshotAsync(userGuid, currentStore.StoreGuid, cancellationToken);
        if (timings is not null) timings.PermissionMs = permissionStopwatch.ElapsedMilliseconds;
        var user = userStores[0];
        // 新签发票据绑定设备硬件；旧入口可传空值并继续保持原有会话兼容。
        var authorization = preserveBarcodeAuthenticationTime
            ? ticketService.Issue(
                cashierId,
                userGuid,
                storeCode,
                deviceCode,
                hardwareId,
                barcodeAuthenticatedAtUtc)
            : ticketService.Issue(cashierId, userGuid, storeCode, deviceCode, hardwareId);
        return new CashierSessionDto(
            cashierId,
            userGuid,
            string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName,
            storeCode,
            deviceCode,
            snapshot.RoleNames,
            snapshot.PermissionCodes,
            userStores
                .Select(store => store.StoreCode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            snapshot.IsSuperAdmin,
            IsOfflineCached: false,
            IsEmergencyOverride: false,
            AuthorizationToken: authorization.Token,
            AuthorizationExpiresAtUtc: authorization.ExpiresAtUtc,
            // 小票员工显示名由客户端按「昵称 → 名 → 账号（邮箱取 @ 前）+ 姓」格式化（见 WPF ReceiptStaffName），
            // 这里只原样下发各段；CashierName 仍是全名（订单、审计沿用，老账号没有姓名拆分字段时客户端据此拆）。
            Username: user.Username,
            Nickname: user.Nickname,
            FamilyName: user.FamilyName,
            GivenName: user.GivenName);
    }

    private static string[] AddLegacyDiscountCompatibility(IEnumerable<string> permissionCodes)
    {
        var effectivePermissions = permissionCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (LineDiscountPermissions.All(effectivePermissions.Contains))
        {
            effectivePermissions.Add(Permissions.PosTerminal.Sales.LineDiscount);
        }

        if (OrderDiscountPermissions.All(effectivePermissions.Contains))
        {
            effectivePermissions.Add(Permissions.PosTerminal.Sales.OrderDiscount);
        }

        return effectivePermissions.ToArray();
    }

    private sealed record CashierPermissionSnapshot(
        string[] RoleNames,
        string[] PermissionCodes,
        bool IsSuperAdmin);

    private sealed class CashierIdentityCandidate
    {
        public int SourceKind { get; set; }
        public string HGUID { get; set; } = string.Empty;
        public int Id { get; set; }
        public string? UserGUID { get; set; }
    }

    private sealed class CashierUserStoreCandidate
    {
        public string Username { get; set; } = string.Empty;
        public string? FullName { get; set; }
        public string? Nickname { get; set; }
        public string? FamilyName { get; set; }
        public string? GivenName { get; set; }
        public string StoreGuid { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
    }

    private sealed class CashierPermissionCandidate
    {
        public string PermissionCode { get; set; } = string.Empty;
    }

    private sealed class CashierLoginTimings
    {
        public long IdentityMs { get; set; }
        public long StoreMs { get; set; }
        public long PermissionMs { get; set; }
    }
}
