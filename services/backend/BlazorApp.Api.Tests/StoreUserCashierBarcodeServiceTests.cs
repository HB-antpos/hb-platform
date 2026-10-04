using System.Security.Claims;
using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Data.Sqlite;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class StoreUserCashierBarcodeServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public StoreUserCashierBarcodeServiceTests()
    {
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        _db = CreateDb(autoClose: false);
        _db.CodeFirst.InitTables(
            typeof(User),
            typeof(Role),
            typeof(UserRole),
            typeof(Store),
            typeof(UserStore),
            typeof(CashRegisterUser),
            typeof(CashierBarcodeReservation),
            typeof(EmployeeCashierBarcode),
            typeof(EmployeeCashierBarcodePrintAttempt)
        );
    }

    [Fact]
    public async Task GetAsync_WhenBarcodeDoesNotExist_IsPureRead()
    {
        await SeedAsync();
        var service = CreateService(_db, "manager-1", "manager", ["StoreManager"]);

        var result = await service.GetAsync("staff-1", "S001");

        Assert.True(result.Success);
        Assert.False(result.Data!.Exists);
        Assert.Equal(0, await _db.Queryable<EmployeeCashierBarcode>().CountAsync());
        Assert.Equal(0, await _db.Queryable<CashierBarcodeReservation>().CountAsync());
    }

    [Fact]
    public async Task EnsureAsync_WhenCalledRepeatedly_ReturnsSameActiveBarcodeAndPreservesHistory()
    {
        await SeedAsync();
        await _db.Insertable(new CashRegisterUser
        {
            HGUID = "legacy-staff-1",
            UserGUID = "staff-1",
            UserBarcode = "LEGACY-STAFF-1",
            StoreCode = "S001",
            OperatorUser = "staff",
            LoginRole = "2",
            Remark = string.Empty,
            Status = true,
            Creator = "seed",
            LastModifier = "seed",
            CreateDate = DateTime.UtcNow,
            LastModifyDate = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        var service = CreateService(
            _db,
            "manager-1",
            "manager",
            ["StoreManager"],
            () => "2912345678906"
        );

        var first = await service.EnsureAsync("staff-1", "S001");
        var retry = await service.EnsureAsync("staff-1", "S001");

        Assert.True(first.Success);
        Assert.Equal(first.Data!.Barcode, retry.Data!.Barcode);
        Assert.Equal(1, await _db.Queryable<EmployeeCashierBarcode>().CountAsync());
        Assert.Equal(1, await _db.Queryable<CashierBarcodeReservation>().CountAsync());
        Assert.False((await _db.Queryable<CashRegisterUser>()
            .FirstAsync(item => item.HGUID == "legacy-staff-1")).Status);
        var saved = await _db.Queryable<EmployeeCashierBarcode>().FirstAsync();
        Assert.Equal("manager", saved.UpdatedBy);
    }

    [Fact]
    public async Task EnsureAsync_WhenCalledConcurrently_CreatesOnlyOneBarcode()
    {
        await SeedAsync();
        using var firstDb = CreateDb(autoClose: true);
        using var secondDb = CreateDb(autoClose: true);
        var first = CreateService(
            firstDb,
            "manager-1",
            "manager",
            ["StoreManager"],
            () => "2911111111114"
        );
        var second = CreateService(
            secondDb,
            "manager-1",
            "manager",
            ["StoreManager"],
            () => "2922222222227"
        );

        var results = await Task.WhenAll(
            first.EnsureAsync("staff-1", "S001"),
            second.EnsureAsync("staff-1", "S001")
        );

        Assert.All(results, result => Assert.True(result.Success));
        Assert.Equal(results[0].Data!.Barcode, results[1].Data!.Barcode);
        Assert.Equal(
            1,
            await _db.Queryable<EmployeeCashierBarcode>()
                .CountAsync(item => item.UserGUID == "staff-1" && item.Status)
        );
        Assert.Equal(1, await _db.Queryable<EmployeeCashierBarcode>().CountAsync());
    }

    [Theory]
    [InlineData("manager-1", "manager", "StoreManager", "staff-2", "S002", "FORBIDDEN")]
    [InlineData("manager-1", "manager", "StoreManager", "manager-1", "S001", "FORBIDDEN")]
    [InlineData("readonly-1", "readonly", "User", "staff-1", "S001", "FORBIDDEN")]
    [InlineData("manager-1", "manager", "StoreManager", "inactive-staff", "S001", "CASHIER_BARCODE_INACTIVE")]
    [InlineData("manager-1", "manager", "StoreManager", "protected-manager", "S001", "FORBIDDEN")]
    public async Task EnsureAsync_WhenTargetOrActorIsNotEligible_DoesNotCreate(
        string actorGuid,
        string actorName,
        string actorRole,
        string targetGuid,
        string storeCode,
        string expectedCode
    )
    {
        await SeedAsync();
        var service = CreateService(_db, actorGuid, actorName, [actorRole]);

        var result = await service.EnsureAsync(targetGuid, storeCode);

        Assert.False(result.Success);
        Assert.Equal(expectedCode, result.Code);
        Assert.Equal(0, await _db.Queryable<EmployeeCashierBarcode>().CountAsync());
    }

    [Fact]
    public async Task ConfirmPrintAsync_WhenAttemptBelongsToDifferentTarget_ReturnsConflict()
    {
        await SeedAsync();
        var service = CreateService(_db, "admin-1", "admin", ["Admin"]);
        var first = await service.EnsureAsync("staff-1", "S001");
        var second = await service.EnsureAsync("staff-2", "S002");
        var attemptId = Guid.NewGuid();
        Assert.True((await service.ConfirmPrintAsync(
            "staff-1",
            new StoreUserCashierBarcodePrintConfirmationRequest
            {
                StoreCode = "S001",
                Barcode = first.Data!.Barcode!,
                PrintAttemptId = attemptId,
            }
        )).Success);

        var conflict = await service.ConfirmPrintAsync(
            "staff-2",
            new StoreUserCashierBarcodePrintConfirmationRequest
            {
                StoreCode = "S002",
                Barcode = second.Data!.Barcode!,
                PrintAttemptId = attemptId,
            }
        );

        Assert.False(conflict.Success);
        Assert.Equal("PRINT_ATTEMPT_CONFLICT", conflict.Code);
        Assert.Equal(0, conflict.Data?.PrintCount ?? 0);
    }

    [Fact]
    public async Task ConfirmPrintAsync_WhenSameAttemptIsRetried_IncrementsOnlyOnce()
    {
        await SeedAsync();
        var service = CreateService(_db, "manager-1", "manager", ["StoreManager"]);
        var barcode = (await service.EnsureAsync("staff-1", "S001")).Data!.Barcode!;
        var request = new StoreUserCashierBarcodePrintConfirmationRequest
        {
            StoreCode = "S001",
            Barcode = barcode,
            PrintAttemptId = Guid.NewGuid(),
        };

        var first = await service.ConfirmPrintAsync("staff-1", request);
        var retry = await service.ConfirmPrintAsync("staff-1", request);

        Assert.True(first.Success);
        Assert.True(retry.Success);
        Assert.Equal(1, first.Data!.PrintCount);
        Assert.Equal(1, retry.Data!.PrintCount);
        Assert.Equal(1, await _db.Queryable<EmployeeCashierBarcodePrintAttempt>().CountAsync());
    }

    [Fact]
    public async Task ConfirmPrintAsync_WhenBarcodeWasReplaced_ReturnsChangedWithoutIncrementingCurrent()
    {
        await SeedAsync();
        var service = CreateService(_db, "manager-1", "manager", ["StoreManager"]);
        var old = (await service.EnsureAsync("staff-1", "S001")).Data!.Barcode!;
        await _db.Updateable<EmployeeCashierBarcode>()
            .SetColumns(item => item.Status == false)
            .Where(item => item.UserGUID == "staff-1" && item.Status)
            .ExecuteCommandAsync();
        await _db.Insertable(new EmployeeCashierBarcode
        {
            HGUID = "replacement",
            UserGUID = "staff-1",
            Barcode = "2933333333330",
            PrintCount = 0,
            Status = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();

        var result = await service.ConfirmPrintAsync(
            "staff-1",
            new StoreUserCashierBarcodePrintConfirmationRequest
            {
                StoreCode = "S001",
                Barcode = old,
                PrintAttemptId = Guid.NewGuid(),
            }
        );

        Assert.False(result.Success);
        Assert.Equal("CASHIER_BARCODE_CHANGED", result.Code);
        Assert.Equal(0, (await _db.Queryable<EmployeeCashierBarcode>()
            .FirstAsync(item => item.HGUID == "replacement")).PrintCount);
    }

    [Fact]
    public void Controller_CashierBarcodeEndpointsRequireUsersEdit()
    {
        var methods = typeof(ReactStoreUsersController).GetMethods()
            .Where(method => method.GetCustomAttributes(typeof(HttpMethodAttribute), true).Length > 0)
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>()
                .Any(attribute => attribute.Template?.Contains("cashier-barcode", StringComparison.Ordinal) == true))
            .ToList();

        Assert.Equal(3, methods.Count);
        Assert.All(methods, method => Assert.Contains(
            method.GetCustomAttributes<AuthorizeAttribute>(),
            attribute => attribute.Policy == Permissions.Users.EditStoreStaff
        ));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private StoreUserCashierBarcodeService CreateService(
        ISqlSugarClient db,
        string actorGuid,
        string actorName,
        string[] roles,
        Func<string>? barcodeFactory = null
    )
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, actorGuid),
                        new Claim("userId", actorGuid),
                        new Claim(ClaimTypes.Name, actorName),
                        .. roles.Select(role => new Claim(ClaimTypes.Role, role)),
                    ],
                    "TestAuth"
                )),
            },
        };
        var currentUser = new CurrentUserService(accessor);
        var context = CreateContext(db);
        var scope = new CurrentUserManageableStoreScopeService(context, currentUser, accessor);
        var barcode = new EmployeeCashierBarcodeService(context, currentUser, barcodeFactory);
        return new StoreUserCashierBarcodeService(context, currentUser, scope, accessor, barcode);
    }

    private async Task SeedAsync()
    {
        var now = DateTime.UtcNow;
        await _db.Insertable(new[]
        {
            User("admin-1", "admin", true),
            User("manager-1", "manager", true),
            User("readonly-1", "readonly", true),
            User("staff-1", "staff1", true),
            User("staff-2", "staff2", true),
            User("inactive-staff", "inactive", false),
            User("protected-manager", "protected", true),
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            Role("role-admin", "Admin"),
            Role("role-manager", "StoreManager"),
            Role("role-user", "User"),
            Role("role-staff", "StoreStaff"),
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            UserRole("admin-1", "role-admin"),
            UserRole("manager-1", "role-manager"),
            UserRole("readonly-1", "role-user"),
            UserRole("staff-1", "role-staff"),
            UserRole("staff-2", "role-staff"),
            UserRole("inactive-staff", "role-staff"),
            UserRole("protected-manager", "role-staff"),
            UserRole("protected-manager", "role-manager"),
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new Store { StoreGUID = "store-1", StoreCode = "S001", StoreName = "Store 1", IsActive = true, CreatedAt = now },
            new Store { StoreGUID = "store-2", StoreCode = "S002", StoreName = "Store 2", IsActive = true, CreatedAt = now },
            new Store { StoreGUID = "store-off", StoreCode = "OFF", StoreName = "Off", IsActive = false, CreatedAt = now },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            UserStore("manager-1", "store-1", true),
            UserStore("manager-1", "store-2", false),
            UserStore("staff-1", "store-1", false),
            UserStore("staff-2", "store-2", false),
            UserStore("inactive-staff", "store-1", false),
            UserStore("protected-manager", "store-1", false),
        }).ExecuteCommandAsync();
    }

    private static User User(string guid, string username, bool active) => new()
    {
        UserGUID = guid,
        Username = username,
        Email = $"{username}@example.com",
        PasswordHash = "hash",
        IsActive = active,
    };

    private static Role Role(string guid, string name) => new()
    {
        RoleGUID = guid,
        RoleName = name,
        IsActive = true,
    };

    private static UserRole UserRole(string userGuid, string roleGuid) => new()
    {
        UserRoleGUID = $"{userGuid}-{roleGuid}",
        UserGUID = userGuid,
        RoleGUID = roleGuid,
    };

    private static UserStore UserStore(string userGuid, string storeGuid, bool primary) => new()
    {
        UserStoreGUID = $"{userGuid}-{storeGuid}",
        UserGUID = userGuid,
        StoreGUID = storeGuid,
        IsPrimary = primary,
    };

    private SqlSugarClient CreateDb(bool autoClose) => new(new ConnectionConfig
    {
        ConnectionString = $"Data Source={_dbPath}",
        DbType = DbType.Sqlite,
        IsAutoCloseConnection = autoClose,
        InitKeyType = InitKeyType.Attribute,
    });

    private static SqlSugarContext CreateContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(
            typeof(SqlSugarContext)
        );
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }
}
