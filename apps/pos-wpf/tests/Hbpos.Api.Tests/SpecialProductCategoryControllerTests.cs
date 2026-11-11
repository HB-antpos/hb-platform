using System.Reflection;
using System.Security.Claims;
using Hbpos.Api.Auth;
using Hbpos.Api.Controllers;
using Hbpos.Api.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Hbpos.Api.Tests;

/// <summary>特殊商品分类三个接口的路由、授权策略、设备范围、请求校验与错误码到 HTTP 状态的映射。</summary>
public sealed class SpecialProductCategoryControllerTests
{
    private static readonly CatalogSpecialProductCategoriesResponse EmptySnapshot =
        new("S01", DateTimeOffset.UnixEpoch, "0", [], []);

    // ---------------------------------------------------------------- 路由与策略

    [Fact]
    public void Endpoints_use_expected_routes_and_verbs()
    {
        Assert.Equal("special-products/categories", Template<HttpGetAttribute>(nameof(CatalogController.GetSpecialProductCategories)));
        Assert.Equal("special-products/categories/assign", Template<HttpPostAttribute>(nameof(CatalogController.AssignSpecialProductCategory)));
        Assert.Equal("special-products/categories/order", Template<HttpPostAttribute>(nameof(CatalogController.OrderSpecialProductCategory)));
    }

    [Fact]
    public void Read_needs_view_policy_and_writes_need_manage_policy()
    {
        Assert.Equal(CashierAuthorizationPolicies.SpecialProductsView, Policy(nameof(CatalogController.GetSpecialProductCategories)));
        Assert.Equal(CashierAuthorizationPolicies.SpecialProductsManage, Policy(nameof(CatalogController.AssignSpecialProductCategory)));
        Assert.Equal(CashierAuthorizationPolicies.SpecialProductsManage, Policy(nameof(CatalogController.OrderSpecialProductCategory)));
    }

    // ---------------------------------------------------------------- GET

    [Fact]
    public async Task Get_wraps_service_snapshot()
    {
        var service = new FakeCategoryService { Snapshot = EmptySnapshot };
        var controller = NewController("S01", "POS-01");

        var result = await controller.GetSpecialProductCategories("S01", service, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var api = Assert.IsType<ApiResult<CatalogSpecialProductCategoriesResponse>>(ok.Value);
        Assert.True(api.Success);
        Assert.Same(EmptySnapshot, api.Data);
        Assert.Equal("S01", service.LastStoreCode);
    }

    [Fact]
    public async Task Get_returns_bad_request_when_store_code_missing_and_not_found_when_store_unknown()
    {
        var controller = NewController("S01", "POS-01");

        var missing = await controller.GetSpecialProductCategories(" ", new FakeCategoryService(), CancellationToken.None);
        var unknown = await controller.GetSpecialProductCategories("S01", new FakeCategoryService { Snapshot = null }, CancellationToken.None);

        Assert.Equal("STORE_CODE_REQUIRED", ErrorOf<BadRequestObjectResult>(missing));
        Assert.Equal("STORE_NOT_FOUND", ErrorOf<NotFoundObjectResult>(unknown));
    }

    [Fact]
    public async Task Get_returns_forbidden_when_device_store_does_not_match()
    {
        var service = new FakeCategoryService { Snapshot = EmptySnapshot };
        var controller = NewController("S02", "POS-02");

        var result = await controller.GetSpecialProductCategories("S01", service, CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Equal("DEVICE_SCOPE_FORBIDDEN", Assert.IsType<ApiResult<CatalogSpecialProductCategoriesResponse>>(forbidden.Value).ErrorCode);
        Assert.Null(service.LastStoreCode);
    }

    // ---------------------------------------------------------------- assign

    [Fact]
    public async Task Assign_forwards_request_with_device_code_as_actor()
    {
        var service = new FakeCategoryService { WriteResult = SpecialProductCategoryServiceResult.Ok(EmptySnapshot) };
        var controller = NewController("S01", "POS-01");
        var request = new CatalogSpecialProductCategoryAssignRequest("S01", "P1", "C1");

        var result = await controller.AssignSpecialProductCategory(request, service, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(EmptySnapshot, Assert.IsType<ApiResult<CatalogSpecialProductCategoriesResponse>>(ok.Value).Data);
        Assert.Same(request, service.LastAssign);
        Assert.Equal("POS-01", service.LastActor);
    }

    [Fact]
    public async Task Assign_validates_request_and_device_scope_before_calling_service()
    {
        var service = new FakeCategoryService { WriteResult = SpecialProductCategoryServiceResult.Ok(EmptySnapshot) };
        var controller = NewController("S01", "POS-01");

        Assert.Equal("ASSIGN_REQUEST_REQUIRED",
            ErrorOf<BadRequestObjectResult>(await controller.AssignSpecialProductCategory(null, service, CancellationToken.None)));
        Assert.Equal("STORE_CODE_REQUIRED",
            ErrorOf<BadRequestObjectResult>(await controller.AssignSpecialProductCategory(
                new CatalogSpecialProductCategoryAssignRequest(" ", "P1", null), service, CancellationToken.None)));
        Assert.Equal("PRODUCT_CODE_REQUIRED",
            ErrorOf<BadRequestObjectResult>(await controller.AssignSpecialProductCategory(
                new CatalogSpecialProductCategoryAssignRequest("S01", "", null), service, CancellationToken.None)));

        var foreign = await controller.AssignSpecialProductCategory(
            new CatalogSpecialProductCategoryAssignRequest("S09", "P1", null), service, CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(foreign.Result).StatusCode);
        Assert.Null(service.LastAssign);
    }

    [Theory]
    [InlineData("SPECIAL_PRODUCT_NOT_SPECIAL", StatusCodes.Status400BadRequest)]
    [InlineData("SPECIAL_PRODUCT_CATEGORY_NOT_FOUND", StatusCodes.Status404NotFound)]
    [InlineData("STORE_NOT_FOUND", StatusCodes.Status404NotFound)]
    [InlineData("SPECIAL_PRODUCT_CATEGORY_ORDER_CONFLICT", StatusCodes.Status409Conflict)]
    [InlineData("SPECIAL_PRODUCT_CATEGORY_ASSIGN_CONFLICT", StatusCodes.Status409Conflict)]
    public async Task Assign_maps_service_errors_to_http_status(string errorCode, int expectedStatus)
    {
        var service = new FakeCategoryService { WriteResult = SpecialProductCategoryServiceResult.Fail(errorCode, "msg") };
        var controller = NewController("S01", "POS-01");

        var result = await controller.AssignSpecialProductCategory(
            new CatalogSpecialProductCategoryAssignRequest("S01", "P1", "C1"), service, CancellationToken.None);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(expectedStatus, objectResult.StatusCode);
        var api = Assert.IsType<ApiResult<CatalogSpecialProductCategoriesResponse>>(objectResult.Value);
        Assert.False(api.Success);
        Assert.Equal(errorCode, api.ErrorCode);
    }

    // ---------------------------------------------------------------- order

    [Fact]
    public async Task Order_forwards_request_with_device_code_as_actor()
    {
        var service = new FakeCategoryService { WriteResult = SpecialProductCategoryServiceResult.Ok(EmptySnapshot) };
        var controller = NewController("S01", "POS-01");
        var request = new CatalogSpecialProductCategoryOrderRequest("S01", "C1", ["P1", "P2"]);

        var result = await controller.OrderSpecialProductCategory(request, service, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(request, service.LastOrder);
        Assert.Equal("POS-01", service.LastActor);
    }

    [Fact]
    public async Task Order_validates_request_and_device_scope_before_calling_service()
    {
        var service = new FakeCategoryService { WriteResult = SpecialProductCategoryServiceResult.Ok(EmptySnapshot) };
        var controller = NewController("S01", "POS-01");

        Assert.Equal("ORDER_REQUEST_REQUIRED",
            ErrorOf<BadRequestObjectResult>(await controller.OrderSpecialProductCategory(null, service, CancellationToken.None)));
        Assert.Equal("STORE_CODE_REQUIRED",
            ErrorOf<BadRequestObjectResult>(await controller.OrderSpecialProductCategory(
                new CatalogSpecialProductCategoryOrderRequest("", "C1", []), service, CancellationToken.None)));
        Assert.Equal("CATEGORY_ID_REQUIRED",
            ErrorOf<BadRequestObjectResult>(await controller.OrderSpecialProductCategory(
                new CatalogSpecialProductCategoryOrderRequest("S01", " ", []), service, CancellationToken.None)));
        Assert.Equal("PRODUCT_CODES_REQUIRED",
            ErrorOf<BadRequestObjectResult>(await controller.OrderSpecialProductCategory(
                new CatalogSpecialProductCategoryOrderRequest("S01", "C1", null!), service, CancellationToken.None)));

        var foreign = await controller.OrderSpecialProductCategory(
            new CatalogSpecialProductCategoryOrderRequest("S09", "C1", []), service, CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(foreign.Result).StatusCode);
        Assert.Null(service.LastOrder);
    }

    [Fact]
    public async Task Order_conflict_is_returned_as_409_with_stable_error_code()
    {
        var service = new FakeCategoryService
        {
            WriteResult = SpecialProductCategoryServiceResult.Fail("SPECIAL_PRODUCT_CATEGORY_ORDER_CONFLICT", "refresh")
        };
        var controller = NewController("S01", "POS-01");

        var result = await controller.OrderSpecialProductCategory(
            new CatalogSpecialProductCategoryOrderRequest("S01", "C1", ["P1"]), service, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Equal("SPECIAL_PRODUCT_CATEGORY_ORDER_CONFLICT",
            Assert.IsType<ApiResult<CatalogSpecialProductCategoriesResponse>>(conflict.Value).ErrorCode);
    }

    // ---------------------------------------------------------------- 辅助

    private static CatalogController NewController(string storeCode, string deviceCode)
    {
        // 控制器构造参数只有 ICatalogService，分类接口通过 [FromServices] 注入服务，这里用空实现占位。
        var controller = new CatalogController(null!);
        var identity = new ClaimsIdentity(
        [
            new Claim(DeviceAuthConstants.StoreCodeClaim, storeCode),
            new Claim(DeviceAuthConstants.DeviceCodeClaim, deviceCode),
            new Claim(DeviceAuthConstants.HardwareIdClaim, "HW-001")
        ], DeviceAuthConstants.Scheme);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private static string? ErrorOf<TObjectResult>(ActionResult<ApiResult<CatalogSpecialProductCategoriesResponse>> result)
        where TObjectResult : ObjectResult
    {
        var objectResult = Assert.IsType<TObjectResult>(result.Result);
        return Assert.IsType<ApiResult<CatalogSpecialProductCategoriesResponse>>(objectResult.Value).ErrorCode;
    }

    private static string? Template<TAttribute>(string methodName) where TAttribute : Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute =>
        typeof(CatalogController).GetMethod(methodName)!
            .GetCustomAttributes<TAttribute>(inherit: false)
            .Single()
            .Template;

    private static string? Policy(string methodName) =>
        typeof(CatalogController).GetMethod(methodName)!
            .GetCustomAttributes<AuthorizeAttribute>(inherit: false)
            .Single()
            .Policy;

    private sealed class FakeCategoryService : ISpecialProductCategoryService
    {
        public CatalogSpecialProductCategoriesResponse? Snapshot { get; init; }

        public SpecialProductCategoryServiceResult WriteResult { get; init; } =
            SpecialProductCategoryServiceResult.Fail("UNSET", "unset");

        public string? LastStoreCode { get; private set; }

        public CatalogSpecialProductCategoryAssignRequest? LastAssign { get; private set; }

        public CatalogSpecialProductCategoryOrderRequest? LastOrder { get; private set; }

        public string? LastActor { get; private set; }

        public Task<CatalogSpecialProductCategoriesResponse?> GetSnapshotAsync(
            string storeCode,
            CancellationToken cancellationToken)
        {
            LastStoreCode = storeCode;
            return Task.FromResult(Snapshot);
        }

        public Task<SpecialProductCategoryServiceResult> AssignAsync(
            CatalogSpecialProductCategoryAssignRequest request,
            string updatedBy,
            CancellationToken cancellationToken)
        {
            LastAssign = request;
            LastActor = updatedBy;
            return Task.FromResult(WriteResult);
        }

        public Task<SpecialProductCategoryServiceResult> ReorderAsync(
            CatalogSpecialProductCategoryOrderRequest request,
            string updatedBy,
            CancellationToken cancellationToken)
        {
            LastOrder = request;
            LastActor = updatedBy;
            return Task.FromResult(WriteResult);
        }
    }
}
