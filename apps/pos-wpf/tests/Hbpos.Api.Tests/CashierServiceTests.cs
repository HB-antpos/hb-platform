using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Models;
using Hbpos.Api.Data;
using Hbpos.Api.Auth;
using Hbpos.Api.Services;
using Hbpos.Contracts.Cashiers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;

namespace Hbpos.Api.Tests;

public sealed class CashierServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _sqliteConnection;
    private readonly SqlSugarClient _db;

    public CashierServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _sqliteConnection = new SqliteConnection($"Data Source={_dbPath}");
        _sqliteConnection.Open();

        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _sqliteConnection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });

        _db.CodeFirst.InitTables(
            typeof(CashRegisterUser),
            typeof(EmployeeCashierBarcode),
            typeof(User),
            typeof(Store),
            typeof(UserStore),
            typeof(Role),
            typeof(UserRole),
            typeof(SysPermission),
            typeof(SysRolePermission),
            typeof(SysUserPermission),
            typeof(SysUserStorePosPermission)
        );
    }

    [Fact]
    public async Task BarcodeLoginAsync_RejectsBlankBarcode()
    {
        await SeedStoreAsync("store-allowed", "S-ALLOWED");
        await SeedUserAsync("user-blank", "Blank User");
        await SeedUserStoreAsync("user-blank", "store-allowed");
        await SeedCashierAsync("cashier-blank", "user-blank", "S-OLD", string.Empty);

        var session = await CreateService().BarcodeLoginAsync(
            new CashierBarcodeLoginRequest("S-ALLOWED", "   ", "POS-1"),
            CancellationToken.None
        );

        // 关键位置：空白条码必须在入库查询前被拒绝，避免历史脏数据产生误登录。
        Assert.Null(session);
    }

    [Fact]
    public async Task BarcodeLoginAsync_按UserStore授权并返回权限快照()
    {
        await SeedStoreAsync("store-allowed", "S-ALLOWED");
        await SeedStoreAsync("store-legacy", "S-LEGACY");
        await SeedUserAsync("user-1", "Alice");
        await SeedUserStoreAsync("user-1", "store-allowed");
        await SeedCashierAsync("cashier-1", " user-1 ", "S-LEGACY", "BARCODE-1");
        await SeedRoleAsync("role-1", "Cashier");
        await SeedUserRoleAsync("user-1", "role-1");
        await SeedRolePermissionAsync("role-1", Permissions.PosTerminal.Sales.AddItem);

        var session = await CreateService().BarcodeLoginAsync(
            new CashierBarcodeLoginRequest("S-ALLOWED", "BARCODE-1", "POS-1"),
            CancellationToken.None
        );

        Assert.NotNull(session);
        Assert.Equal("cashier-1", session.CashierId);
        Assert.Equal("user-1", session.UserGuid);
        Assert.Equal("Alice", session.CashierName);
        Assert.Equal("S-ALLOWED", session.StoreCode);
        Assert.Equal("POS-1", session.DeviceCode);
        Assert.Contains(Permissions.PosTerminal.Sales.AddItem, session.PermissionCodes);
        Assert.Contains("S-ALLOWED", session.AllowedStoreCodes);
        Assert.False(session.IsSuperAdmin);
        Assert.False(session.IsOfflineCached);
        Assert.False(session.IsEmergencyOverride);
        Assert.Equal("test-ticket", session.AuthorizationToken);
        Assert.Equal(DateTimeOffset.Parse("2026-07-15T00:00:00Z"), session.AuthorizationExpiresAtUtc);
    }

    [Fact]
    public async Task BarcodeLoginAsync_普通登录最多执行五次查询()
    {
        await SeedStoreAsync("store-query-count", "S-QUERY");
        await SeedUserAsync("user-query-count", "Query Count Cashier");
        await SeedUserStoreAsync("user-query-count", "store-query-count");
        await SeedCashierAsync("cashier-query-count", "user-query-count", "S-OLD", "QUERY-CODE");
        await SeedRoleAsync("role-query-count", "Cashier");
        await SeedUserRoleAsync("user-query-count", "role-query-count");
        await SeedRolePermissionAsync("role-query-count", Permissions.PosTerminal.Sales.AddItem);
        await SeedUserPermissionAsync("user-query-count", Permissions.PosTerminal.CashDrawer.Open);
        var selectCount = 0;
        _db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) selectCount++;
        };
        var session = await CreateService().BarcodeLoginAsync(
            new CashierBarcodeLoginRequest("S-QUERY", "QUERY-CODE", "POS-1"), CancellationToken.None);
        Assert.NotNull(session);
        Assert.Contains(Permissions.PosTerminal.Sales.AddItem, session.PermissionCodes);
        Assert.Contains(Permissions.PosTerminal.CashDrawer.Open, session.PermissionCodes);
        Assert.True(selectCount <= 5, $"普通登录执行了 {selectCount} 次 SELECT，预期不超过 5 次。");
    }

    [Fact]
    public async Task BarcodeLoginAsync_管理员返回全部权限且拒绝未授权门店()
    {
        await SeedStoreAsync("store-allowed", "S-ALLOWED");
        await SeedStoreAsync("store-blocked", "S-BLOCKED");
        await SeedUserAsync("admin-1", "Admin User");
        await SeedUserStoreAsync("admin-1", "store-allowed");
        await SeedCashierAsync("cashier-admin", "admin-1", "S-OLD", "ADMIN-CODE");
        await SeedRoleAsync("role-admin", "Admin");
        await SeedUserRoleAsync("admin-1", "role-admin");
        await SeedPermissionAsync(Permissions.PosTerminal.Sales.AddItem);
        await SeedPermissionAsync(Permissions.PosTerminal.CashDrawer.Open);

        var blocked = await CreateService().BarcodeLoginAsync(
            new CashierBarcodeLoginRequest("S-BLOCKED", "ADMIN-CODE", "POS-1"),
            CancellationToken.None
        );
        var allowed = await CreateService().BarcodeLoginAsync(
            new CashierBarcodeLoginRequest("S-ALLOWED", "ADMIN-CODE", "POS-1"),
            CancellationToken.None
        );

        Assert.Null(blocked);
        Assert.NotNull(allowed);
        Assert.True(allowed.IsSuperAdmin);
        Assert.Contains(Permissions.PosTerminal.Sales.AddItem, allowed.PermissionCodes);
        Assert.Contains(Permissions.PosTerminal.CashDrawer.Open, allowed.PermissionCodes);
    }

    [Fact]
    public async Task BarcodeLoginAsync_EmployeeBarcodeSurvivesLegacyFullSyncDeleteAndKeepsAuthorizationChecks()
    {
        await SeedStoreAsync("store-allowed", "S-ALLOWED");
        await SeedStoreAsync("store-blocked", "S-BLOCKED");
        await SeedUserAsync("employee-1", "Employee One");
        await SeedUserStoreAsync("employee-1", "store-allowed");
        await _db.Insertable(new EmployeeCashierBarcode
        {
            HGUID = "employee-cashier-1",
            UserGUID = "employee-1",
            Barcode = "2900000000001",
            Status = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        await _db.Deleteable<CashRegisterUser>().ExecuteCommandAsync();

        var blocked = await CreateService().BarcodeLoginAsync(
            new CashierBarcodeLoginRequest("S-BLOCKED", "2900000000001", "POS-1"),
            CancellationToken.None
        );
        var allowed = await CreateService().BarcodeLoginAsync(
            new CashierBarcodeLoginRequest("S-ALLOWED", "2900000000001", "POS-1"),
            CancellationToken.None
        );

        Assert.Null(blocked);
        Assert.NotNull(allowed);
        Assert.Equal("employee-cashier-1", allowed.CashierId);
        Assert.Equal("employee-1", allowed.UserGuid);
    }

    [Fact]
    public async Task BarcodeLoginAsync_ResetStateRejectsOldCodesAndAcceptsNewCodeOnlyForActiveAuthorizedUser()
    {
        await SeedStoreAsync("qr-allowed", "QR-ALLOWED");
        await SeedStoreAsync("qr-blocked", "QR-BLOCKED");
        await SeedUserAsync("qr-user", "QR User");
        await SeedUserStoreAsync("qr-user", "qr-allowed");
        await SeedCashierAsync("qr-legacy", "qr-user", "QR-ALLOWED", "2900000000018");
        // 模拟后台重置提交后的同步状态，验证 POS 消费相同两张表的有效标记。
        await _db.Updateable<CashRegisterUser>()
            .SetColumns(item => item.Status == false)
            .Where(item => item.UserGUID == "qr-user")
            .ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new EmployeeCashierBarcode { HGUID = "qr-old", UserGUID = "qr-user", Barcode = "2900000000025", Status = false, CreatedAt = DateTime.UtcNow },
            new EmployeeCashierBarcode { HGUID = "qr-new", UserGUID = "qr-user", Barcode = "2900000000032", Status = true, CreatedAt = DateTime.UtcNow },
        }).ExecuteCommandAsync();
        var service = CreateService();
        foreach (var oldCode in new[] { "2900000000018", "2900000000025" })
        {
            Assert.Null(await service.BarcodeLoginAsync(
                new CashierBarcodeLoginRequest("QR-ALLOWED", oldCode, "POS-1"), CancellationToken.None));
        }
        var current = await service.BarcodeLoginAsync(
            new CashierBarcodeLoginRequest("QR-ALLOWED", "2900000000032", "POS-1"), CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal("qr-user", current.UserGuid);
        Assert.Null(await service.BarcodeLoginAsync(
            new CashierBarcodeLoginRequest("QR-BLOCKED", "2900000000032", "POS-1"), CancellationToken.None));
        await _db.Updateable<User>().SetColumns(item => item.IsActive == false)
            .Where(item => item.UserGUID == "qr-user").ExecuteCommandAsync();
        Assert.Null(await service.BarcodeLoginAsync(
            new CashierBarcodeLoginRequest("QR-ALLOWED", "2900000000032", "POS-1"), CancellationToken.None));
    }

    [Fact]
    public async Task BarcodeLoginAsync_同一条码双表同时有效时拒绝登录()
    {
        await SeedStoreAsync("store-allowed", "S-ALLOWED");
        await SeedUserAsync("legacy-user", "Legacy User");
        await SeedUserAsync("employee-user", "Employee User");
        await SeedUserStoreAsync("legacy-user", "store-allowed");
        await SeedUserStoreAsync("employee-user", "store-allowed");
        await SeedCashierAsync("legacy-cashier", "legacy-user", "DUPLICATE-CODE", "DUPLICATE-CODE");
        await _db.Insertable(new EmployeeCashierBarcode
        {
            HGUID = "employee-cashier",
            UserGUID = "employee-user",
            Barcode = "DUPLICATE-CODE",
            Status = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();

        var session = await CreateService().BarcodeLoginAsync(
            new CashierBarcodeLoginRequest("S-ALLOWED", "DUPLICATE-CODE", "POS-1"),
            CancellationToken.None
        );

        Assert.Null(session);
    }

    [Fact]
    public async Task BarcodeLoginAsync_身份冲突日志不包含条码或其后四位()
    {
        const string barcode = "9900000000001";
        await SeedStoreAsync("store-allowed", "S-ALLOWED");
        await SeedUserAsync("legacy-user", "Legacy User");
        await SeedUserAsync("employee-user", "Employee User");
        await SeedUserStoreAsync("legacy-user", "store-allowed");
        await SeedUserStoreAsync("employee-user", "store-allowed");
        await SeedCashierAsync("legacy-cashier", "legacy-user", "S-OLD", barcode);
        await _db.Insertable(new EmployeeCashierBarcode
        {
            HGUID = "employee-cashier",
            UserGUID = "employee-user",
            Barcode = barcode,
            Status = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        var logger = new RecordingLogger<CashierService>();

        var session = await CreateService(logger).BarcodeLoginAsync(
            new CashierBarcodeLoginRequest("S-ALLOWED", barcode, "POS-1"),
            CancellationToken.None);

        Assert.Null(session);
        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, message =>
        {
            Assert.DoesNotContain(barcode, message, StringComparison.Ordinal);
            Assert.DoesNotContain(barcode[^4..], message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task BarcodeLoginAsync_按分店应用显式允许拒绝且不影响其他分店()
    {
        await SeedStoreAsync("store-a", "S-A");
        await SeedStoreAsync("store-b", "S-B");
        await SeedUserAsync("user-store-scope", "Scoped Cashier");
        await SeedUserStoreAsync("user-store-scope", "store-a");
        await SeedUserStoreAsync("user-store-scope", "store-b");
        await SeedCashierAsync("cashier-store-scope", "user-store-scope", "S-OLD", "STORE-SCOPE");
        await SeedRoleAsync("role-store-scope", "Cashier");
        await SeedUserRoleAsync("user-store-scope", "role-store-scope");
        await SeedRolePermissionAsync("role-store-scope", Permissions.PosTerminal.Sales.AddItem);
        await SeedStorePermissionAsync(
            "user-store-scope", "store-a", Permissions.PosTerminal.Sales.AddItem, isGranted: false);
        await SeedStorePermissionAsync(
            "user-store-scope", "store-a", Permissions.PosTerminal.Sales.LineQuickDiscount20Percent, isGranted: true);

        var storeA = await CreateService().BarcodeLoginAsync(
            new CashierBarcodeLoginRequest("S-A", "STORE-SCOPE", "POS-1"),
            CancellationToken.None);
        var storeB = await CreateService().BarcodeLoginAsync(
            new CashierBarcodeLoginRequest("S-B", "STORE-SCOPE", "POS-1"),
            CancellationToken.None);

        Assert.NotNull(storeA);
        Assert.DoesNotContain(Permissions.PosTerminal.Sales.AddItem, storeA.PermissionCodes);
        Assert.Contains(Permissions.PosTerminal.Sales.LineQuickDiscount20Percent, storeA.PermissionCodes);
        Assert.NotNull(storeB);
        Assert.Contains(Permissions.PosTerminal.Sales.AddItem, storeB.PermissionCodes);
        Assert.DoesNotContain(Permissions.PosTerminal.Sales.LineQuickDiscount20Percent, storeB.PermissionCodes);
    }

    [Fact]
    public async Task BarcodeLoginAsync_仅在六项新权限齐全时合成旧折扣权限()
    {
        await SeedStoreAsync("store-compat", "S-COMPAT");
        await SeedUserAsync("user-compat", "Compat Cashier");
        await SeedUserStoreAsync("user-compat", "store-compat");
        await SeedCashierAsync("cashier-compat", "user-compat", "S-OLD", "COMPAT-CODE");
        await SeedRoleAsync("role-compat", "Cashier");
        await SeedUserRoleAsync("user-compat", "role-compat");
        foreach (var permissionCode in new[]
        {
            Permissions.PosTerminal.Sales.LineManualDiscount,
            Permissions.PosTerminal.Sales.LineQuickDiscount10Percent,
            Permissions.PosTerminal.Sales.LineQuickDiscount20Percent,
            Permissions.PosTerminal.Sales.LineQuickDiscount30Percent,
            Permissions.PosTerminal.Sales.LineQuickDiscount40Percent,
            Permissions.PosTerminal.Sales.LineQuickDiscount50Percent,
            Permissions.PosTerminal.Sales.OrderManualDiscount
        })
        {
            await SeedRolePermissionAsync("role-compat", permissionCode);
        }

        var session = await CreateService().BarcodeLoginAsync(
            new CashierBarcodeLoginRequest("S-COMPAT", "COMPAT-CODE", "POS-1"),
            CancellationToken.None);

        Assert.NotNull(session);
        Assert.Contains(Permissions.PosTerminal.Sales.LineDiscount, session.PermissionCodes);
        Assert.DoesNotContain(Permissions.PosTerminal.Sales.OrderDiscount, session.PermissionCodes);
    }

    [Fact]
    public async Task RefreshSessionAsync_重新解析门店权限并在用户停用后拒绝()
    {
        await SeedStoreAsync("store-refresh", "S-REFRESH");
        await SeedUserAsync("user-refresh", "Refresh Cashier");
        await SeedUserStoreAsync("user-refresh", "store-refresh");
        await SeedRoleAsync("role-refresh", "Cashier");
        await SeedUserRoleAsync("user-refresh", "role-refresh");
        await SeedRolePermissionAsync("role-refresh", Permissions.PosTerminal.Sales.AddItem);
        var ticket = new CashierAuthorizationTicket(
            "cashier-refresh",
            "user-refresh",
            "S-REFRESH",
            "POS-1",
            DateTimeOffset.UtcNow.AddHours(1));
        var service = CreateService();

        var inherited = await service.RefreshSessionAsync(ticket, CancellationToken.None);
        await SeedStorePermissionAsync(
            "user-refresh", "store-refresh", Permissions.PosTerminal.Sales.AddItem, isGranted: false);
        var denied = await service.RefreshSessionAsync(ticket, CancellationToken.None);
        await _db.Updateable<User>()
            .SetColumns(user => user.IsActive == false)
            .Where(user => user.UserGUID == "user-refresh")
            .ExecuteCommandAsync();
        var inactive = await service.RefreshSessionAsync(ticket, CancellationToken.None);

        Assert.NotNull(inherited);
        Assert.Contains(Permissions.PosTerminal.Sales.AddItem, inherited.PermissionCodes);
        Assert.NotNull(denied);
        Assert.DoesNotContain(Permissions.PosTerminal.Sales.AddItem, denied.PermissionCodes);
        Assert.Null(inactive);
    }

    [Fact]
    public async Task HasAnyPermissionAsync_敏感请求使用当前分店覆盖权限()
    {
        await SeedStoreAsync("store-auth-a", "S-AUTH-A");
        await SeedStoreAsync("store-auth-b", "S-AUTH-B");
        await SeedUserAsync("user-auth", "Authorized Cashier");
        await SeedUserStoreAsync("user-auth", "store-auth-a");
        await SeedUserStoreAsync("user-auth", "store-auth-b");
        await SeedRoleAsync("role-auth", "Cashier");
        await SeedUserRoleAsync("user-auth", "role-auth");
        await SeedRolePermissionAsync("role-auth", Permissions.PosTerminal.Sales.AddItem);
        await SeedStorePermissionAsync(
            "user-auth", "store-auth-a", Permissions.PosTerminal.Sales.AddItem, isGranted: false);
        var service = CreateService();

        var storeA = await service.HasAnyPermissionAsync(
            "user-auth", "S-AUTH-A", [Permissions.PosTerminal.Sales.AddItem], CancellationToken.None);
        var storeB = await service.HasAnyPermissionAsync(
            "user-auth", "S-AUTH-B", [Permissions.PosTerminal.Sales.AddItem], CancellationToken.None);

        Assert.False(storeA);
        Assert.True(storeB);
    }

    [Fact]
    public async Task Hardware_barcode_login_default_implementation_fails_closed()
    {
        ICashierService service = new HardwareDefaultingCashierService();

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            service.BarcodeLoginAsync(
                new CashierBarcodeLoginRequest("S001", "BC-1", "POS-01"),
                "HW-01",
                CancellationToken.None));
    }

    public void Dispose()
    {
        _db.Dispose();
        _sqliteConnection.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            try
            {
                File.Delete(_dbPath);
            }
            catch (IOException)
            {
                // Windows 上 SQLite 清空连接池后仍可能短暂占用文件句柄，不影响登录断言。
            }
        }
    }

    private CashierService CreateService(ILogger<CashierService>? logger = null) => new(
        CreateHbposContext(_db),
        new FakeTicketService(),
        logger ?? NullLogger<CashierService>.Instance);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class FakeTicketService : ICashierAuthorizationTicketService
    {
        private static readonly DateTimeOffset ExpiresAtUtc = DateTimeOffset.Parse("2026-07-15T00:00:00Z");

        public (string Token, DateTimeOffset ExpiresAtUtc) Issue(
            string cashierId,
            string userGuid,
            string storeCode,
            string deviceCode) => ("test-ticket", ExpiresAtUtc);

        public (string Token, DateTimeOffset ExpiresAtUtc) Issue(
            string cashierId,
            string userGuid,
            string storeCode,
            string deviceCode,
            string? hardwareId) => ("test-ticket", ExpiresAtUtc);

        public (string Token, DateTimeOffset ExpiresAtUtc) Issue(
            string cashierId,
            string userGuid,
            string storeCode,
            string deviceCode,
            string? hardwareId,
            DateTimeOffset? barcodeAuthenticatedAtUtc) => ("test-ticket", ExpiresAtUtc);

        public CashierAuthorizationTicket? Validate(string? token) => null;
    }

    private sealed class HardwareDefaultingCashierService : ICashierService
    {
        public Task<CashierSessionDto?> BarcodeLoginAsync(
            CashierBarcodeLoginRequest request,
            CancellationToken cancellationToken) => Task.FromResult<CashierSessionDto?>(null);

        public Task<CashierSessionDto?> RefreshSessionAsync(
            CashierAuthorizationTicket ticket,
            CancellationToken cancellationToken) => Task.FromResult<CashierSessionDto?>(null);

        public Task<bool> HasAnyPermissionAsync(
            string userGuid,
            string storeCode,
            IReadOnlyCollection<string> permissionCodes,
            CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private async Task SeedStoreAsync(string storeGuid, string storeCode)
    {
        await _db.Insertable(new Store
        {
            StoreGUID = storeGuid,
            StoreCode = storeCode,
            StoreName = storeCode,
            IsActive = true,
        }).ExecuteCommandAsync();
    }

    private async Task SeedUserAsync(string userGuid, string username)
    {
        await _db.Insertable(new User
        {
            UserGUID = userGuid,
            Username = username,
            FullName = username,
            Email = $"{userGuid}@example.test",
            PasswordHash = "hash",
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task SeedUserStoreAsync(string userGuid, string storeGuid)
    {
        await _db.Insertable(new UserStore
        {
            UserStoreGUID = $"{userGuid}-{storeGuid}",
            UserGUID = userGuid,
            StoreGUID = storeGuid,
            IsPrimary = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task SeedCashierAsync(
        string hGuid,
        string userGuid,
        string legacyStoreCode,
        string barcode)
    {
        await _db.Insertable(new CashRegisterUser
        {
            HGUID = hGuid,
            UserGUID = userGuid,
            StoreCode = legacyStoreCode,
            OperatorUser = userGuid,
            UserBarcode = barcode,
            LoginRole = "legacy",
            Remark = string.Empty,
            Status = true,
            Creator = "seed",
            LastModifier = "seed",
            CreateDate = DateTime.UtcNow,
            LastModifyDate = DateTime.UtcNow,
        }).ExecuteCommandAsync();
    }

    private async Task SeedRoleAsync(string roleGuid, string roleName)
    {
        await _db.Insertable(new Role
        {
            RoleGUID = roleGuid,
            RoleName = roleName,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task SeedUserRoleAsync(string userGuid, string roleGuid)
    {
        await _db.Insertable(new UserRole
        {
            UserRoleGUID = $"{userGuid}-{roleGuid}",
            UserGUID = userGuid,
            RoleGUID = roleGuid,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task SeedPermissionAsync(string code)
    {
        await _db.Insertable(new SysPermission
        {
            Id = code,
            Code = code,
            Name = code,
            Category = "test",
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task SeedRolePermissionAsync(string roleGuid, string permissionCode)
    {
        await SeedPermissionAsync(permissionCode);
        await _db.Insertable(new SysRolePermission
        {
            Id = $"{roleGuid}-{permissionCode}",
            RoleGuid = roleGuid,
            PermissionCode = permissionCode,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task SeedUserPermissionAsync(string userGuid, string permissionCode)
    {
        await SeedPermissionAsync(permissionCode);
        await _db.Insertable(new SysUserPermission
        {
            Id = $"{userGuid}-{permissionCode}",
            UserGuid = userGuid,
            PermissionCode = permissionCode,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task SeedStorePermissionAsync(
        string userGuid,
        string storeGuid,
        string permissionCode,
        bool isGranted)
    {
        await _db.Insertable(new SysUserStorePosPermission
        {
            Id = $"{userGuid}-{storeGuid}-{permissionCode}",
            UserGuid = userGuid,
            StoreGuid = storeGuid,
            PermissionCode = permissionCode,
            IsGranted = isGranted,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private static HbposSqlSugarContext CreateHbposContext(ISqlSugarClient db)
    {
        var context = (HbposSqlSugarContext)RuntimeHelpers.GetUninitializedObject(
            typeof(HbposSqlSugarContext)
        );
        typeof(HbposSqlSugarContext)
            .GetField("<MainDb>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        typeof(HbposSqlSugarContext)
            .GetField("<PosmDb>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }
}
