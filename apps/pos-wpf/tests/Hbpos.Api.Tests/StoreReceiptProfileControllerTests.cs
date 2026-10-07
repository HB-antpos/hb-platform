using System.Security.Claims;
using System.Text.Json;
using Hbpos.Api.Auth;
using Hbpos.Api.Controllers;
using Hbpos.Api.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Hbpos.Api.Tests;

public sealed class StoreReceiptProfileControllerTests
{
    [Fact]
    public void Endpoint_keeps_expected_route_template()
    {
        var attribute = typeof(StoresController)
            .GetMethod(nameof(StoresController.GetCurrentReceiptProfile))!
            .GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
            .Cast<HttpGetAttribute>()
            .Single();

        Assert.Equal("current/receipt-profile", attribute.Template);
    }

    [Fact]
    public void Controller_requires_device_auth_and_receipt_printer_policy()
    {
        Assert.NotNull(typeof(StoresController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .SingleOrDefault());
        Assert.Equal(
            CashierAuthorizationPolicies.ReceiptPrinter,
            typeof(StoresController)
                .GetMethod(nameof(StoresController.GetCurrentReceiptProfile))!
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
                .Cast<AuthorizeAttribute>()
                .Single()
                .Policy);
    }

    [Fact]
    public void Endpoint_accepts_no_query_or_body_store_code()
    {
        var parameters = typeof(StoresController)
            .GetMethod(nameof(StoresController.GetCurrentReceiptProfile))!
            .GetParameters();

        Assert.Equal(typeof(CancellationToken), Assert.Single(parameters).ParameterType);
    }

    [Fact]
    public async Task GetCurrentReceiptProfile_returns_profile_for_authenticated_store_claim()
    {
        var profile = new StoreReceiptProfileDto(
            "S001",
            "旗舰店",
            "Hot Bargain",
            "1 Queen St",
            "07 3000 0000",
            "12 345 678 901",
            "30 天无理由退换");
        var service = new FakeStoreReceiptProfileService(new StoreReceiptProfileLookupResult(profile));
        var controller = new StoresController(service, new NoopReleaseService());
        SetStoreClaim(controller, "S001");

        var result = await controller.GetCurrentReceiptProfile(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<StoreReceiptProfileDto>>(ok.Value);
        Assert.True(apiResult.Success);
        Assert.Same(profile, apiResult.Data);
        Assert.Equal("S001", service.LastStoreCode);
    }

    [Fact]
    public async Task GetCurrentReceiptProfile_returns_404_when_store_missing_or_disabled()
    {
        var service = new FakeStoreReceiptProfileService(new StoreReceiptProfileLookupResult(
            null,
            StoreReceiptProfileService.StoreNotFoundCode,
            "门店不存在或已停用"));
        var controller = new StoresController(service, new NoopReleaseService());
        SetStoreClaim(controller, "S-MISSING");

        var result = await controller.GetCurrentReceiptProfile(CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<StoreReceiptProfileDto>>(notFound.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("STORE_NOT_FOUND", apiResult.ErrorCode);
    }

    [Fact]
    public async Task GetCurrentReceiptProfile_returns_400_when_control_characters_present()
    {
        var service = new FakeStoreReceiptProfileService(new StoreReceiptProfileLookupResult(
            null,
            StoreReceiptProfileService.InvalidCharactersCode,
            "门店资料包含不可打印控制字符"));
        var controller = new StoresController(service, new NoopReleaseService());
        SetStoreClaim(controller, "S001");

        var result = await controller.GetCurrentReceiptProfile(CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<StoreReceiptProfileDto>>(badRequest.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("STORE_PROFILE_INVALID_CHARACTERS", apiResult.ErrorCode);
    }

    [Fact]
    public async Task GetCurrentReceiptProfile_returns_401_when_store_claim_missing()
    {
        var controller = new StoresController(
            new FakeStoreReceiptProfileService(new StoreReceiptProfileLookupResult(null)),
            new NoopReleaseService());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity())
            }
        };

        var result = await controller.GetCurrentReceiptProfile(CancellationToken.None);

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<StoreReceiptProfileDto>>(unauthorized.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("STORE_CODE_CLAIM_MISSING", apiResult.ErrorCode);
    }

    [Fact]
    public void StoreReceiptProfileDto_serializes_contract_keys_in_camel_case()
    {
        var dto = new StoreReceiptProfileDto(
            "S001",
            "Store One",
            "HB",
            null,
            null,
            null,
            null);
        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"storeCode\":\"S001\"", json, StringComparison.Ordinal);
        Assert.Contains("\"storeName\":\"Store One\"", json, StringComparison.Ordinal);
        Assert.Contains("\"brandName\":\"HB\"", json, StringComparison.Ordinal);
        Assert.Contains("\"address\":null", json, StringComparison.Ordinal);
        Assert.Contains("\"phone\":null", json, StringComparison.Ordinal);
        Assert.Contains("\"abn\":null", json, StringComparison.Ordinal);
        Assert.Contains("\"returnPolicy\":null", json, StringComparison.Ordinal);
    }

    private static void SetStoreClaim(ControllerBase controller, string storeCode)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(DeviceAuthConstants.StoreCodeClaim, storeCode),
            new Claim(DeviceAuthConstants.DeviceCodeClaim, "POS-01")
        ], DeviceAuthConstants.Scheme);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity)
            }
        };
    }

    private sealed class FakeStoreReceiptProfileService(StoreReceiptProfileLookupResult result)
        : IStoreReceiptProfileService
    {
        public string? LastStoreCode { get; private set; }

        public Task<StoreReceiptProfileLookupResult> GetCurrentAsync(
            string storeCode,
            CancellationToken cancellationToken)
        {
            LastStoreCode = storeCode;
            return Task.FromResult(result);
        }
    }

    // 既有的门店资料用例只关心 GET receipt-profile；下发快照的同步/回执另有专门的测试文件覆盖。
    private sealed class NoopReleaseService : IStoreReceiptProfileReleaseService
    {
        public Task<StoreReceiptProfileSyncLookupResult> GetSyncAsync(
            string storeCode,
            int knownVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StoreReceiptProfileAckLookupResult> AckAsync(
            string storeCode,
            string deviceCode,
            string? deviceSystem,
            int version,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
