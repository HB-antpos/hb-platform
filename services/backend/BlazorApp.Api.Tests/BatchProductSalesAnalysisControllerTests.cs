using System.Security.Claims;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class BatchProductSalesAnalysisControllerTests
{
    [Fact]
    public async Task Query_受限用户指定未授权门店返回403()
    {
        var service = new Mock<IBatchProductSalesAnalysisService>();
        service.Setup(x => x.QueryAsync(It.IsAny<BatchProductSalesQueryRequestDto>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BatchProductSalesAnalysisForbiddenException());
        var controller = CreateController(service.Object, "User", ["S1"]);

        var result = await controller.Query(new BatchProductSalesQueryRequestDto { ItemNumbers = ["001"], StartDate = DateTime.Today, EndDate = DateTime.Today, StoreCodes = ["S2"] });

        Assert.IsType<ForbidResult>(result);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("WarehouseManager")]
    public async Task Options_实时全店角色传递全店范围(string role)
    {
        IReadOnlyList<string>? captured = ["unexpected"];
        var service = new Mock<IBatchProductSalesAnalysisService>();
        service.Setup(x => x.GetOptionsAsync(It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>?, CancellationToken>((scope, _) => captured = scope)
            .ReturnsAsync(ApiResponse<BatchProductSalesOptionsDto>.OK(new()));

        var result = await CreateController(service.Object, role, ["S1"]).GetOptions();

        Assert.IsType<OkObjectResult>(result);
        Assert.Null(captured);
    }

    [Fact]
    public async Task Options_零门店授权服务拒绝时映射403()
    {
        var service = new Mock<IBatchProductSalesAnalysisService>();
        service.Setup(x => x.GetOptionsAsync(It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new BatchProductSalesAnalysisForbiddenException());
        Assert.IsType<ForbidResult>(await CreateController(service.Object, "User", []).GetOptions());
    }

    [Fact]
    public async Task Options_普通用户无门店传递空范围()
    {
        IReadOnlyList<string>? captured = null;
        var service = new Mock<IBatchProductSalesAnalysisService>();
        service.Setup(x => x.GetOptionsAsync(It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>?, CancellationToken>((scope, _) => captured = scope)
            .ReturnsAsync(ApiResponse<BatchProductSalesOptionsDto>.OK(new()));

        await CreateController(service.Object, "User", []).GetOptions();

        Assert.NotNull(captured);
        Assert.Empty(captured!);
    }

    [Theory]
    [InlineData("options", "User")]
    [InlineData("query", "User")]
    [InlineData("detail", "User")]
    [InlineData("query", "WarehouseManager")]
    public async Task 权限撤销后旧JWT和展开权限不能继续查询(string endpoint, string role)
    {
        var service = new Mock<IBatchProductSalesAnalysisService>(MockBehavior.Strict);
        var controller = CreateController(service.Object, role, ["S1"], hasPermission: false);
        var result = endpoint switch
        {
            "options" => await controller.GetOptions(),
            "detail" => await controller.Detail(new()),
            _ => await controller.Query(new()),
        };
        Assert.IsType<ForbidResult>(result);
        service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("options")]
    [InlineData("query")]
    [InlineData("detail")]
    public async Task 前台货号销量权限按全店范围读取且不受名下门店限制(string endpoint)
    {
        IReadOnlyList<string>? captured = ["unexpected"];
        var service = new Mock<IBatchProductSalesAnalysisService>();
        service.Setup(x => x.GetOptionsAsync(It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>?, CancellationToken>((scope, _) => captured = scope)
            .ReturnsAsync(ApiResponse<BatchProductSalesOptionsDto>.OK(new()));
        service.Setup(x => x.QueryAsync(It.IsAny<BatchProductSalesQueryRequestDto>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<BatchProductSalesQueryRequestDto, IReadOnlyList<string>?, CancellationToken>((_, scope, _) => captured = scope)
            .ReturnsAsync(ApiResponse<BatchProductSalesQueryResultDto>.OK(new()));
        service.Setup(x => x.GetDetailAsync(It.IsAny<BatchProductSalesDetailRequestDto>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<BatchProductSalesDetailRequestDto, IReadOnlyList<string>?, CancellationToken>((_, scope, _) => captured = scope)
            .ReturnsAsync(ApiResponse<BatchProductSalesDetailDto>.OK(new()));
        // 订货员只挂了一家门店，且没有后台销售看板权限，仅凭前台权限即可读取全部分店。
        var controller = CreateController(service.Object, "订货员", ["S1"], permissionCodes: [Permissions.OrderFront.BatchProductSalesView]);

        var result = endpoint switch
        {
            "options" => await controller.GetOptions(),
            "detail" => await controller.Detail(new()),
            _ => await controller.Query(new()),
        };

        Assert.IsType<OkObjectResult>(result);
        Assert.Null(captured);
    }

    [Fact]
    public async Task 仅持有后台权限的普通用户仍按名下门店限制范围()
    {
        IReadOnlyList<string>? captured = null;
        var service = new Mock<IBatchProductSalesAnalysisService>();
        service.Setup(x => x.GetOptionsAsync(It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>?, CancellationToken>((scope, _) => captured = scope)
            .ReturnsAsync(ApiResponse<BatchProductSalesOptionsDto>.OK(new()));

        await CreateController(service.Object, "User", ["S1", "S2"]).GetOptions();

        Assert.Equal(new[] { "S1", "S2" }, captured);
    }

    [Fact]
    public async Task 实时权限快照失败时拒绝查询且不调用数据服务()
    {
        var service = new Mock<IBatchProductSalesAnalysisService>(MockBehavior.Strict);
        var controller = CreateController(service.Object, "User", ["S1"], snapshotSuccess: false);
        Assert.IsType<ForbidResult>(await controller.Query(new()));
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task BranchOverview_锁冲突返回统一409错误码()
    {
        var service = new Mock<IBatchProductSalesAnalysisService>();
        service.Setup(x => x.GetBranchOverviewAsync(It.IsAny<BatchProductSalesBranchOverviewRequestDto>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BatchProductSalesCoverageVersionConflictException());
        var result = await CreateController(service.Object, "User", ["S1"]).BranchOverview(new());
        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Contains("BATCH_PRODUCT_SALES_COVERAGE_VERSION_CONFLICT", System.Text.Json.JsonSerializer.Serialize(conflict.Value));
    }

    [Fact]
    public async Task DiscountOverview_撤销权限时不调用Reader()
    {
        var service = new Mock<IBatchProductSalesAnalysisService>(MockBehavior.Strict);
        var result = await CreateController(service.Object, "User", ["S1"], hasPermission: false).DiscountOverview(new());
        Assert.IsType<ForbidResult>(result);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Purchases_撤销权限时不调用服务()
    {
        var service = new Mock<IBatchProductSalesAnalysisService>(MockBehavior.Strict);
        var result = await CreateController(service.Object, "User", ["S1"], hasPermission: false).Purchases(new());
        Assert.IsType<ForbidResult>(result);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Purchases_受限用户按名下门店范围读取进货量()
    {
        IReadOnlyList<string>? captured = null;
        var service = new Mock<IBatchProductSalesAnalysisService>();
        service.Setup(x => x.GetPurchaseQuantitiesAsync(It.IsAny<BatchProductSalesPurchaseRequestDto>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback<BatchProductSalesPurchaseRequestDto, IReadOnlyList<string>?, CancellationToken>((_, scope, _) => captured = scope)
            .ReturnsAsync(ApiResponse<BatchProductSalesPurchaseResultDto>.OK(new()));

        var result = await CreateController(service.Object, "User", ["S1", "S2"]).Purchases(new() { ProductCodes = ["P1"] });

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(new[] { "S1", "S2" }, captured);
    }

    [Fact]
    public async Task Purchases_参数校验失败返回400()
    {
        var service = new Mock<IBatchProductSalesAnalysisService>();
        service.Setup(x => x.GetPurchaseQuantitiesAsync(It.IsAny<BatchProductSalesPurchaseRequestDto>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BatchProductSalesAnalysisValidationException("日期范围无效，最多 366 天。"));

        var result = await CreateController(service.Object, "User", ["S1"]).Purchases(new());

        Assert.IsType<BadRequestObjectResult>(result);
    }

    private static BatchProductSalesAnalysisController CreateController(IBatchProductSalesAnalysisService service, string role, List<string> stores, bool hasPermission = true, bool snapshotSuccess = true, List<string>? permissionCodes = null)
    {
        // 默认模拟后台销售看板权限；permissionCodes 可改为前台权限或其他组合。
        var exactPermissions = hasPermission ? permissionCodes ?? [Permissions.SalesDashboard.BatchProductSalesView] : [];
        var roles = new Mock<IRoleService>();
        roles.Setup(x => x.GetUserPermissionSnapshotAsync("user-1"))
            .ReturnsAsync(snapshotSuccess ? ApiResponse<UserPermissionSnapshotDto>.OK(new UserPermissionSnapshotDto
            { UserGuid = "user-1", RoleNames = [role], IsSuperAdmin = role == "Admin",
              ExactPermissionCodes = exactPermissions,
              PermissionCodes = [Permissions.SalesDashboard.BatchProductSalesView] })
            : ApiResponse<UserPermissionSnapshotDto>.Error("快照读取失败"));
        var users = new Mock<IUserService>();
        users.Setup(x => x.GetUserByGuidAsync("user-1")).ReturnsAsync(ApiResponse<UserDetailDto>.OK(new UserDetailDto
        {
            UserGUID = "user-1", Stores = stores.Select(code => new UserStoreDto { StoreCode = code }).ToList(),
        }));
        return new BatchProductSalesAnalysisController(service, users.Object, roles.Object, NullLogger<BatchProductSalesAnalysisController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user-1")], "test")) } },
        };
    }
}
