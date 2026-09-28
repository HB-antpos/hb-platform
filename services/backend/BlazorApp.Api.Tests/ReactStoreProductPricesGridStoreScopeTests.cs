using System.Reflection;
using System.Security.Claims;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 分店商品价格网格必须按当前用户可访问分店做范围校验：
/// 前端只列出可访问分店，但店长直接改请求里的 StoreCode 也不能查看其他分店价格。
/// </summary>
public sealed class ReactStoreProductPricesGridStoreScopeTests
{
    private const string UserGuid = "store-manager-guid";

    [Fact]
    public void Grid路由要求分店商品查看权限()
    {
        var method = typeof(ReactStoreProductPricesController).GetMethod(nameof(ReactStoreProductPricesController.Grid))!;

        Assert.Equal("grid", method.GetCustomAttribute<HttpPostAttribute>()!.Template);
        Assert.Equal(Permissions.StoreProducts.View, method.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }

    [Fact]
    public async Task 店长查询自己关联的分店时放行且编码不区分大小写()
    {
        var (controller, service, users) = Create(roles: "店长");
        SetupUserStores(users, Store("S1", isPrimary: true));
        SetupGrid(service, "s1");

        var result = Assert.IsType<OkObjectResult>(await controller.Grid(Query("s1")));

        Assert.True(ReadProperty<bool>(result.Value, "success"));
        service.VerifyAll();
    }

    [Fact]
    public async Task 店长查询已关联但非主分店时同样放行()
    {
        // Web 前端把 isPrimary 当作 isManageable，只列主分店；后端按全部关联分店放行，前端选项是后端范围的子集，不会误拦。
        var (controller, service, users) = Create(roles: "经理");
        SetupUserStores(users, Store("S1", isPrimary: true), Store("S2", isPrimary: false));
        SetupGrid(service, "S2");

        var result = Assert.IsType<OkObjectResult>(await controller.Grid(Query("S2")));

        Assert.True(ReadProperty<bool>(result.Value, "success"));
        service.VerifyAll();
    }

    [Fact]
    public async Task 店长查询未关联分店时返回403且不查询价格()
    {
        var (controller, service, users) = Create(roles: "StoreManager");
        SetupUserStores(users, Store("S1", isPrimary: true));

        Assert.IsType<ForbidResult>(await controller.Grid(Query("S9")));

        service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("管理员")]
    [InlineData("WarehouseManager")]
    [InlineData("仓库经理")]
    [InlineData("WarehouseStaff")]
    [InlineData("仓库员工")]
    public async Task 不限分店的角色直接放行且不读取用户分店(string role)
    {
        var (controller, service, users) = Create(roles: role);
        SetupGrid(service, "S9");

        var result = Assert.IsType<OkObjectResult>(await controller.Grid(Query("S9")));

        Assert.True(ReadProperty<bool>(result.Value, "success"));
        service.VerifyAll();
        users.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task 超级管理员别名按用户分店接口返回的分店放行()
    {
        // 前端把 SuperAdmin 视为不限分店；后端 IsInRole 名单里没有它，
        // 会落到 GetUserStoresAsync，而该接口对超级管理员别名返回全部未删除分店。
        var (controller, service, users) = Create(roles: "SuperAdmin");
        SetupUserStores(users, Store("S1", isPrimary: false), Store("S9", isPrimary: false));
        SetupGrid(service, "S9");

        Assert.IsType<OkObjectResult>(await controller.Grid(Query("S9")));

        service.VerifyAll();
        users.VerifyAll();
    }

    [Fact]
    public async Task 缺少用户标识时拒绝()
    {
        var (controller, service, users) = Create(roles: "店长", userGuid: null);

        Assert.IsType<ForbidResult>(await controller.Grid(Query("S1")));

        service.VerifyNoOtherCalls();
        users.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task 读取用户分店失败时拒绝而不是放行()
    {
        var (controller, service, users) = Create(roles: "店长");
        users.Setup(x => x.GetUserStoresAsync(UserGuid))
            .ReturnsAsync(ApiResponse<List<UserStoreDto>>.Error("获取用户分店失败", "GET_USER_STORES_FAILED"));

        Assert.IsType<ForbidResult>(await controller.Grid(Query("S1")));

        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task 未选择分店时保持原提示且不读取用户分店()
    {
        var (controller, service, users) = Create(roles: "店长");

        var result = Assert.IsType<OkObjectResult>(await controller.Grid(Query("  ")));

        Assert.False(ReadProperty<bool>(result.Value, "success"));
        Assert.Equal("请选择分店", ReadProperty<string>(result.Value, "message"));
        service.VerifyNoOtherCalls();
        users.VerifyNoOtherCalls();
    }

    private static StoreProductPriceQueryDto Query(string storeCode) => new() { StoreCode = storeCode };

    private static UserStoreDto Store(string storeCode, bool isPrimary) => new()
    {
        StoreGUID = $"guid-{storeCode}",
        StoreCode = storeCode,
        StoreName = storeCode,
        IsActive = true,
        IsPrimary = isPrimary,
    };

    private static void SetupUserStores(Mock<IUserService> users, params UserStoreDto[] stores)
    {
        users.Setup(x => x.GetUserStoresAsync(UserGuid))
            .ReturnsAsync(ApiResponse<List<UserStoreDto>>.OK(stores.ToList()));
    }

    private static void SetupGrid(Mock<IStoreProductPriceReactService> service, string storeCode)
    {
        service.Setup(x => x.GetGridDataAsync(It.Is<StoreProductPriceQueryDto>(query => query.StoreCode == storeCode)))
            .ReturnsAsync(GridResponseDto<StoreProductPriceListDto>.OK(new List<StoreProductPriceListDto>(), 0));
    }

    private static T ReadProperty<T>(object? value, string name)
    {
        Assert.NotNull(value);
        var property = value!.GetType().GetProperty(name);
        Assert.NotNull(property);
        return Assert.IsType<T>(property!.GetValue(value));
    }

    private static (
        ReactStoreProductPricesController Controller,
        Mock<IStoreProductPriceReactService> Service,
        Mock<IUserService> Users
    ) Create(string roles, string? userGuid = UserGuid)
    {
        var service = new Mock<IStoreProductPriceReactService>(MockBehavior.Strict);
        var users = new Mock<IUserService>(MockBehavior.Strict);

        var claims = new List<Claim> { new(ClaimTypes.Role, roles) };
        if (userGuid != null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userGuid));
        }

        var controller = new ReactStoreProductPricesController(
            service.Object,
            Mock.Of<IStoreRetailPriceReactService>(),
            users.Object,
            NullLogger<ReactStoreProductPricesController>.Instance,
            Mock.Of<IStorePriceTransferJobService>()
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
                },
            },
        };
        return (controller, service, users);
    }
}
