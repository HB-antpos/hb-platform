using System.Runtime.CompilerServices;
using System.Security.Claims;
using AutoMapper;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.ProductInsights;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class StoreProductInsightsControllerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public StoreProductInsightsControllerTests()
    {
        _connection = new SqliteConnection($"Data Source={_path}");
        _connection.Open();
        _db = new SqlSugarClient(new ConnectionConfig { ConnectionString = _connection.ConnectionString, DbType = DbType.Sqlite, IsAutoCloseConnection = false, InitKeyType = InitKeyType.Attribute });
        _db.CodeFirst.InitTables(typeof(User), typeof(UserStore), typeof(Store), typeof(Product), typeof(HBLocalSupplier), typeof(ProductStoreDailySalesStatistic), typeof(StoreRetailPrice), typeof(StoreLocalSupplierInvoice), typeof(StoreLocalSupplierInvoiceDetails), typeof(WareHouseOrder), typeof(WareHouseOrderDetails));
    }

    [Fact]
    public async Task 过期Manager声明不能越过实时分店范围()
    {
        await SeedStoreAndProductAsync();
        await SeedUserStoreAsync("u1", "S1");
        var roles = Snapshot();
        roles.Setup(service => service.GetUserPermissionSnapshotAsync("u1")).ReturnsAsync(ApiResponse<UserPermissionSnapshotDto>.OK(new()));

        var result = await LoggedInController(roles.Object, "u1", "Manager").GetStoreInsight("S2", "P1", "2026-09-14", "2026-09-14", default);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task 不传日期时days越界返回400()
    {
        await SeedStoreAndProductAsync();
        var roles = Snapshot();
        roles.Setup(service => service.GetUserPermissionSnapshotAsync("u1")).ReturnsAsync(ApiResponse<UserPermissionSnapshotDto>.OK(new() { RoleNames = new() { "Admin" } }));

        var result = await LoggedInController(roles.Object, "u1", null).GetStoreInsight("S2", "P1", null, null, 0, default);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task 有效实时Admin角色允许全店读取()
    {
        await SeedStoreAndProductAsync();
        var roles = Snapshot();
        roles.Setup(service => service.GetUserPermissionSnapshotAsync("u1")).ReturnsAsync(ApiResponse<UserPermissionSnapshotDto>.OK(new() { RoleNames = new() { "Admin" } }));

        var result = await LoggedInController(roles.Object, "u1", null).GetStoreInsight("S2", "P1", "2026-09-14", "2026-09-14", default);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task 普通分店用户被拒绝跨店读取()
    {
        await SeedStoreAndProductAsync();
        await SeedUserStoreAsync("u1", "S1");
        var roles = Snapshot();
        roles.Setup(service => service.GetUserPermissionSnapshotAsync("u1")).ReturnsAsync(ApiResponse<UserPermissionSnapshotDto>.OK(new() { RoleNames = new() { "StoreStaff" } }));

        var result = await LoggedInController(roles.Object, "u1", null).GetStoreInsight("S2", "P1", "2026-09-14", "2026-09-14", default);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task 实时角色快照失败时拒绝而不退化到JWT角色()
    {
        var roles = Snapshot();
        roles.Setup(service => service.GetUserPermissionSnapshotAsync("u1"))
            .ReturnsAsync(ApiResponse<UserPermissionSnapshotDto>.Error("unavailable"));

        var result = await LoggedInController(roles.Object, "u1", "Admin")
            .GetStoreInsight("S2", "P1", "2026-09-14", "2026-09-14", default);

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task 绑定设备即使授权有效也不能跨店读取()
    {
        var devices = new Mock<IDeviceRegistrationService>(MockBehavior.Strict);
        devices.Setup(service => service.ValidateDeviceAuthCodeAsync("device-1", "secret")).ReturnsAsync(true);
        devices.Setup(service => service.GetDeviceByHardwareIdAsync("device-1")).ReturnsAsync(new POSM_设备注册信息表());
        var mapper = new Mock<IMapper>(MockBehavior.Strict);
        mapper.Setup(value => value.Map<DeviceDataDto>(It.IsAny<POSM_设备注册信息表>()))
            .Returns(new DeviceDataDto { HardwareId = "device-1", Status = 1, StoreCode = "S1" });
        var roles = Snapshot();
        var controller = CreateController(devices.Object, mapper.Object, roles.Object);
        controller.ControllerContext.HttpContext.Request.Headers["X-Device-Id"] = "device-1";
        controller.ControllerContext.HttpContext.Request.Headers["X-Auth-Code"] = "secret";

        var result = await controller.GetStoreInsight("S2", "P1", "2026-09-14", "2026-09-14", default);

        Assert.IsType<ForbidResult>(result);
    }

    private StoreProductInsightsController LoggedInController(IRoleService roles, string userGuid, string? staleRole)
    {
        var controller = CreateController(new Mock<IDeviceRegistrationService>(MockBehavior.Loose).Object, new Mock<IMapper>(MockBehavior.Loose).Object, roles);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userGuid) };
        if (staleRole != null) claims.Add(new Claim(ClaimTypes.Role, staleRole));
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        return controller;
    }

    private StoreProductInsightsController CreateController(IDeviceRegistrationService devices, IMapper mapper, IRoleService roles) => new(
        new StoreProductInsightQueryService(Context(_db), new ServiceCollection().BuildServiceProvider()), devices, roles, mapper, Context(_db), NullLogger<StoreProductInsightsController>.Instance)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
    };

    private static Mock<IRoleService> Snapshot() => new(MockBehavior.Strict);

    private async Task SeedStoreAndProductAsync()
    {
        await _db.Insertable(new Store { StoreGUID = "store-1", StoreCode = "S1", StoreName = "One", IsActive = true, IsDeleted = false }).ExecuteCommandAsync();
        await _db.Insertable(new Store { StoreGUID = "store-2", StoreCode = "S2", StoreName = "Two", IsActive = true, IsDeleted = false }).ExecuteCommandAsync();
        await _db.Insertable(new HBLocalSupplier { Guid = "sup-1", LocalSupplierCode = "L1", Name = "Supplier", IsDeleted = false }).ExecuteCommandAsync();
        await _db.Insertable(new Product { UUID = "product-1", ProductCode = "P1", ProductName = "Product", LocalSupplierCode = "L1", IsDeleted = false }).ExecuteCommandAsync();
    }

    private Task SeedUserStoreAsync(string userGuid, string storeCode) => _db.Insertable(new UserStore { UserGUID = userGuid, StoreGUID = storeCode == "S1" ? "store-1" : "store-2", IsDeleted = false }).ExecuteCommandAsync();

    private static SqlSugarContext Context(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext).GetField("_db", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(context, db);
        return context;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_path);
    }
}
