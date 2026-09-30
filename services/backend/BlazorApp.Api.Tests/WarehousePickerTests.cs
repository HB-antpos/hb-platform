using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.WarehousePicking;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>拣货人：扫员工码识别、拣货资格、绑定终端的短期凭证，以及拣货纯规则。</summary>
public sealed class WarehousePickerTests : IDisposable
{
    private const string Terminal = "hw:PDA-HARDWARE-1";

    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;
    private readonly Mock<IRoleService> _roleService = new();
    private readonly WarehousePickerTicketProtector _protector = new(new EphemeralDataProtectionProvider());

    public WarehousePickerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(typeof(User), typeof(EmployeeCashierBarcode));
        // 旧收银用户表历史列很多且 CodeFirst 会把未标可空的列建成 NOT NULL；解析只读这几列，按最小结构建表。
        _db.Ado.ExecuteCommand(
            "CREATE TABLE CashRegisterUsers (Id INTEGER PRIMARY KEY AUTOINCREMENT, HGUID TEXT, StoreCode TEXT, UserGUID TEXT, OperatorUser TEXT, UserBarcode TEXT, Status INTEGER NOT NULL)"
        );
    }

    [Fact]
    public void Ticket_签发后在同一终端有效_换终端无效_过期后返回过期()
    {
        var issuedAt = new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc);
        var (ticket, expiresAt) = _protector.Issue(new WarehousePickerTicketIdentity("user-mia", "Mia Wong", false), Terminal, issuedAt);

        var valid = _protector.Validate(ticket, Terminal, issuedAt.AddHours(1));
        var otherTerminal = _protector.Validate(ticket, "hw:ANOTHER-PDA", issuedAt.AddHours(1));
        var expired = _protector.Validate(ticket, Terminal, expiresAt);
        var tampered = _protector.Validate(ticket[..^4] + "AAAA", Terminal, issuedAt.AddHours(1));

        Assert.Equal(issuedAt.Add(WarehousePickerTicketProtector.Lifetime), expiresAt);
        Assert.Equal(WarehousePickerTicketStatus.Valid, valid.Status);
        Assert.Equal("user-mia", valid.Identity!.UserGuid);
        Assert.Equal("Mia Wong", valid.Identity.Name);
        Assert.Equal(WarehousePickerTicketStatus.Invalid, otherTerminal.Status);
        Assert.Equal(WarehousePickerTicketStatus.Expired, expired.Status);
        Assert.Equal(WarehousePickerTicketStatus.Invalid, tampered.Status);
        Assert.Equal(WarehousePickerTicketStatus.Invalid, _protector.Validate("not-a-ticket", Terminal, issuedAt).Status);
    }

    [Fact]
    public async Task ResolveBarcode_员工卡识别仓库员工并签发绑定终端的凭证()
    {
        await SeedUserAsync("user-mia", "mia", "Mia Wong");
        await _db.Insertable(new EmployeeCashierBarcode
        {
            HGUID = "ecb-1",
            UserGUID = "user-mia",
            Barcode = "2900000000019",
            Status = true,
        }).ExecuteCommandAsync();
        SetupRoles("user-mia", roles: new[] { "WarehouseStaff" }, permissions: Array.Empty<string>());

        var result = await CreateService().ResolveBarcodeAsync(" 2900000000019 ", Terminal);

        Assert.True(result.Success, result.Message);
        Assert.Equal("user-mia", result.Data!.PickerUserGuid);
        Assert.Equal("Mia Wong", result.Data.PickerName);
        Assert.Equal("WarehouseStaff", result.Data.RoleLabel);
        var validation = _protector.Validate(result.Data.Ticket, Terminal, DateTime.UtcNow);
        Assert.Equal(WarehousePickerTicketStatus.Valid, validation.Status);
        Assert.False(validation.Identity!.CanOverwriteMinOrderQuantity);
    }

    [Fact]
    public async Task ResolveBarcode_两张条码表同时命中时拒绝_不任选身份()
    {
        await SeedUserAsync("user-a", "a", "A");
        await SeedUserAsync("user-b", "b", "B");
        await _db.Insertable(new EmployeeCashierBarcode { HGUID = "ecb-1", UserGUID = "user-a", Barcode = "2912345678901", Status = true })
            .ExecuteCommandAsync();
        await InsertLegacyBarcodeAsync("cru-1", "user-b", "2912345678901");

        var result = await CreateService().ResolveBarcodeAsync("2912345678901", Terminal);

        Assert.False(result.Success);
        Assert.Equal(WarehousePickingErrorCodes.PickerBarcodeAmbiguous, result.ErrorCode);
    }

    [Fact]
    public async Task ResolveBarcode_未关联账号或没有仓库权限的员工被拒绝_经理可改中包数()
    {
        await SeedUserAsync("user-store", "store", "Store Staff");
        await SeedUserAsync("user-mgr", "mgr", "Morgan Lee");
        await InsertLegacyBarcodeAsync("cru-legacy", null, "1111111111116");
        await InsertLegacyBarcodeAsync("cru-store", "user-store", "2222222222222");
        await InsertLegacyBarcodeAsync("cru-mgr", "user-mgr", "3333333333338");
        SetupRoles("user-store", roles: new[] { "StoreStaff" }, permissions: new[] { Permissions.Orders.View });
        SetupRoles("user-mgr", roles: new[] { "仓库经理" }, permissions: Array.Empty<string>());
        var service = CreateService();

        var unlinked = await service.ResolveBarcodeAsync("1111111111116", Terminal);
        var storeStaff = await service.ResolveBarcodeAsync("2222222222222", Terminal);
        var manager = await service.ResolveBarcodeAsync("3333333333338", Terminal);

        Assert.Equal(WarehousePickingErrorCodes.PickerBarcodeNotFound, unlinked.ErrorCode);
        Assert.Equal(403, storeStaff.StatusCode);
        Assert.Equal(WarehousePickingErrorCodes.PickerNotAllowed, storeStaff.ErrorCode);
        Assert.True(manager.Success, manager.Message);
        Assert.True(_protector.Validate(manager.Data!.Ticket, Terminal, DateTime.UtcNow).Identity!.CanOverwriteMinOrderQuantity);
    }

    [Fact]
    public async Task Eligibility_持有展开后的拣货权限即可拣货_停用账号返回空()
    {
        await SeedUserAsync("user-perm", "perm", "Perm User");
        await SeedUserAsync("user-off", "off", "Off User", isActive: false);
        SetupRoles("user-perm", roles: new[] { "User" }, permissions: new[] { Permissions.Warehouse.Picking });
        var service = CreateService();

        var eligible = await service.GetEligibilityAsync("user-perm");
        var inactive = await service.GetEligibilityAsync("user-off");

        Assert.True(eligible!.IsAllowed);
        Assert.False(eligible.CanOverwriteMinOrderQuantity);
        Assert.Null(inactive);
    }

    [Fact]
    public async Task ListEligible_仓库角色_角色或本人持有拣货相关权限的在职账号可派单_普通与停用账号不在名单()
    {
        _db.CodeFirst.InitTables(typeof(Role), typeof(UserRole), typeof(SysRolePermission), typeof(SysUserPermission));
        await SeedUserAsync("u-staff", "staff", "Chen Staff");
        await SeedUserAsync("u-mgr", "mgr", "Amy Manager");
        await SeedUserAsync("u-pack", "pack", "Dan Packer");
        await SeedUserAsync("u-direct", "direct", "Bo Direct");
        await SeedUserAsync("u-plain", "plain", "Pat Plain");
        await SeedUserAsync("u-off", "off", "Off Staff", isActive: false);
        await _db.Insertable(new List<Role>
        {
            new() { RoleGUID = "r-staff", RoleName = "仓库员工", IsActive = true },
            new() { RoleGUID = "r-mgr", RoleName = "WarehouseManager", IsActive = true },
            new() { RoleGUID = "r-pack", RoleName = "打包组", IsActive = true },
            new() { RoleGUID = "r-user", RoleName = "User", IsActive = true },
            new() { RoleGUID = "r-old", RoleName = "WarehouseStaff", IsActive = false },
        }).ExecuteCommandAsync();
        await _db.Insertable(new List<UserRole>
        {
            new() { UserGUID = "u-staff", RoleGUID = "r-staff" },
            new() { UserGUID = "u-mgr", RoleGUID = "r-mgr" },
            new() { UserGUID = "u-pack", RoleGUID = "r-pack" },
            new() { UserGUID = "u-plain", RoleGUID = "r-user" },
            new() { UserGUID = "u-plain", RoleGUID = "r-old" },
            new() { UserGUID = "u-off", RoleGUID = "r-staff" },
        }).ExecuteCommandAsync();
        await _db.Insertable(new SysRolePermission { RoleGuid = "r-pack", PermissionCode = Permissions.Warehouse.Picking }).ExecuteCommandAsync();
        await _db.Insertable(new SysUserPermission { UserGuid = "u-direct", PermissionCode = Permissions.Warehouse.ManageOrders }).ExecuteCommandAsync();
        SetupRoles("u-staff", roles: new[] { "仓库员工" }, permissions: Array.Empty<string>());
        SetupRoles("u-mgr", roles: new[] { "WarehouseManager" }, permissions: Array.Empty<string>());
        SetupRoles("u-pack", roles: new[] { "打包组" }, permissions: new[] { Permissions.Warehouse.Picking });
        SetupRoles("u-direct", roles: new[] { "User" }, permissions: new[] { Permissions.Warehouse.ManageOrders });

        var eligible = await CreateService().ListEligibleAsync();

        Assert.Equal(new[] { "u-mgr", "u-direct", "u-staff", "u-pack" }, eligible.Select(item => item.UserGuid).ToArray());
        Assert.All(eligible, item => Assert.True(item.IsAllowed));
        Assert.Equal("WarehouseManager", eligible[0].RoleLabel);
    }

    [Fact]
    public void 权限别名_管理仓库或管理订货的持有人自动具备拣货权限_反向不成立()
    {
        var expanded = Permissions.ExpandPermissionCodes(new[] { Permissions.Warehouse.Manage });
        var fromOrders = Permissions.ExpandPermissionCodes(new[] { Permissions.Warehouse.ManageOrders });
        var fromPicking = Permissions.ExpandPermissionCodes(new[] { Permissions.Warehouse.Picking });

        Assert.Contains(Permissions.Warehouse.Picking, expanded);
        Assert.Contains(Permissions.Warehouse.Picking, fromOrders);
        Assert.DoesNotContain(Permissions.Warehouse.Manage, fromPicking);
        Assert.DoesNotContain(Permissions.Warehouse.ManageOrders, fromPicking);
    }

    [Theory]
    [InlineData(WarehouseOrderPickSources.Scan, null, 12, 0, 12, null)]
    [InlineData(WarehouseOrderPickSources.Increment, null, 12, 24, 12, null)]
    [InlineData(WarehouseOrderPickSources.Scan, 1, null, 0, 1, null)]
    [InlineData(WarehouseOrderPickSources.Scan, null, null, 0, null, WarehousePickingErrorCodes.MinOrderQuantityMissing)]
    [InlineData(WarehouseOrderPickSources.Scan, null, 0, 0, null, WarehousePickingErrorCodes.MinOrderQuantityMissing)]
    [InlineData(WarehouseOrderPickSources.Scan, 0, 12, 0, null, WarehousePickingErrorCodes.InvalidRequest)]
    [InlineData(WarehouseOrderPickSources.Decrement, null, 12, 30, -12, null)]
    [InlineData(WarehouseOrderPickSources.Decrement, null, 12, 5, -5, null)]
    [InlineData(WarehouseOrderPickSources.Decrement, null, 12, 0, null, WarehousePickingErrorCodes.PickedBelowZero)]
    [InlineData(WarehouseOrderPickSources.SetTotal, null, 12, 0, null, WarehousePickingErrorCodes.InvalidRequest)]
    public void ResolveDelta_按来源与服务端中包数计算件数变化(
        int source,
        int? pieces,
        int? minOrderQuantity,
        int currentTotal,
        int? expectedDelta,
        string? expectedError
    )
    {
        var delta = WarehousePickingRules.ResolveDelta(source, pieces, minOrderQuantity, currentTotal);

        Assert.Equal(expectedDelta, delta.Delta);
        Assert.Equal(expectedError, delta.ErrorCode);
    }

    [Fact]
    public void BuildCodeMap_同码多行保留全部行_匹配方式取最高优先级_空码忽略()
    {
        var codes = WarehousePickingRules.BuildCodeMap(new WarehousePickingCodeSource[]
        {
            new(" 931 ", "line", "d-1", WarehouseOrderPickMatchKinds.SetChild, "Small"),
            new("931", "line", "d-2", WarehouseOrderPickMatchKinds.Barcode, null),
            new("931", "line", "d-2", WarehouseOrderPickMatchKinds.Barcode, null),
            new("A-01-01-01", "location", "d-1", null, "A-01-01-01"),
            new("", "line", "d-3", WarehouseOrderPickMatchKinds.Barcode, null),
            new(null, "line", "d-3", WarehouseOrderPickMatchKinds.Barcode, null),
        });

        Assert.Equal(2, codes.Count);
        var shared = Assert.Single(codes, code => code.Code == "931");
        Assert.Equal(new[] { "d-1", "d-2" }, shared.DetailGuids);
        Assert.Equal(WarehouseOrderPickMatchKinds.Barcode, shared.MatchedBy);
        Assert.Single(codes, code => code.Target == "location");
    }

    [Theory]
    [InlineData("HBSO:2026-0418", "2026-0418")]
    [InlineData("hbso: 2026-0418\r", "2026-0418")]
    [InlineData("2026-0418", "2026-0418")]
    [InlineData("HBSO:", null)]
    [InlineData("  ", null)]
    public void ParseOrderCode_识别配货单二维码前缀与手输订单号(string raw, string? expected)
    {
        Assert.Equal(expected, WarehousePickingRules.ParseOrderCode(raw));
    }

    private void SetupRoles(string userGuid, string[] roles, string[] permissions)
    {
        _roleService
            .Setup(service => service.GetUserPermissionSnapshotAsync(userGuid))
            .ReturnsAsync(ApiResponse<UserPermissionSnapshotDto>.OK(new UserPermissionSnapshotDto
            {
                UserGuid = userGuid,
                RoleNames = roles.ToList(),
                PermissionCodes = Permissions.ExpandPermissionCodes(permissions).ToList(),
            }));
    }

    private Task<int> InsertLegacyBarcodeAsync(string hguid, string? userGuid, string barcode) =>
        _db.Ado.ExecuteCommandAsync(
            "INSERT INTO CashRegisterUsers (HGUID, StoreCode, UserGUID, OperatorUser, UserBarcode, Status) VALUES (@hguid, '1013', @userGuid, 'legacy', @barcode, 1)",
            new { hguid, userGuid, barcode }
        );

    private Task SeedUserAsync(string userGuid, string username, string fullName, bool isActive = true) =>
        _db.Insertable(new User
        {
            UserGUID = userGuid,
            Username = username,
            Email = $"{username}@example.invalid",
            PasswordHash = "x",
            FullName = fullName,
            IsActive = isActive,
        }).ExecuteCommandAsync();

    private WarehousePickerService CreateService() =>
        new(CreateSqlSugarContext(_db), _roleService.Object, _protector);

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }
}
