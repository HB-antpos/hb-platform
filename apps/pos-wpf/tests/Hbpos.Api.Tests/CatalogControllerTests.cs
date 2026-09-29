using System.Security.Claims;
using System.Text.Json;
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

public sealed class CatalogControllerTests
{
    [Fact]
    public void SyncEndpoints_KeepExpectedRoutes()
    {
        Assert.Equal("sellable-items/page", GetHttpGetTemplate(nameof(CatalogController.GetSellableItemsPage)));
        Assert.Equal("sync-plan", GetHttpGetTemplate(nameof(CatalogController.GetCatalogSyncPlan)));
        Assert.Equal("delta/page", GetHttpGetTemplate(nameof(CatalogController.GetCatalogDeltaPage)));
        Assert.Equal("sellable-items/lookup", GetHttpGetTemplate(nameof(CatalogController.LookupSellableItem)));
        Assert.Equal("special-products/page", GetHttpGetTemplate(nameof(CatalogController.GetSpecialProductsPage)));
        Assert.Equal("sellable-items/code-conflicts", GetHttpGetTemplate(nameof(CatalogController.GetCodeConflicts)));
        Assert.Equal("promotions", GetHttpGetTemplate(nameof(CatalogController.GetPromotionRules)));
        Assert.Equal("sellable-items/compare", GetHttpPostTemplate(nameof(CatalogController.CompareSellableItems)));
        Assert.Equal("special-products/mark", GetHttpPostTemplate(nameof(CatalogController.MarkSpecialProduct)));
        Assert.Equal("sellable-items", GetHttpGetTemplate(nameof(CatalogController.GetSellableItems)));
    }

    [Fact]
    public async Task GetCatalogSyncPlan_forwards_base_version_and_wraps_response()
    {
        var expected = new CatalogSyncPlanResponse(
            "S01",
            DateTimeOffset.UnixEpoch,
            CatalogSyncModes.Delta,
            "catalog-v1:base",
            "catalog-v1:target",
            3);
        var service = new FakeCatalogService { SyncPlanResponse = expected };
        var controller = new CatalogController(service);

        var result = await controller.GetCatalogSyncPlan(
            "S01",
            "catalog-v1:base",
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<CatalogSyncPlanResponse>>(ok.Value);
        Assert.Same(expected, apiResult.Data);
        Assert.Equal(("S01", "catalog-v1:base"), service.LastSyncPlanRequest);
    }

    [Fact]
    public void CatalogSyncPlanResponse_serializes_mode_as_stable_lower_camel_case_contract()
    {
        var response = new CatalogSyncPlanResponse(
            "S01",
            DateTimeOffset.UnixEpoch,
            CatalogSyncModes.NoChange,
            "catalog-v1:current",
            "catalog-v1:current",
            3);

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"mode\":\"noChange\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetCatalogDeltaPage_returns_conflict_when_fixed_snapshot_has_expired()
    {
        var controller = new CatalogController(new FakeCatalogService { ThrowDeltaSnapshotExpired = true });

        var result = await controller.GetCatalogDeltaPage(
            "S01",
            "catalog-v1:base",
            "catalog-v1:target",
            cursor: null,
            pageSize: 100,
            cancellationToken: CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<CatalogDeltaPageResponse>>(conflict.Value);
        Assert.Equal("CATALOG_SNAPSHOT_EXPIRED", apiResult.ErrorCode);
    }

    [Fact]
    public async Task Catalog_reads_return_503_when_snapshot_capacity_is_busy()
    {
        var controller = new CatalogController(new FakeCatalogService { ThrowCapacityBusy = true });
        var responses = new ObjectResult?[]
        {
            (await controller.GetSellableItems("S01", null, CancellationToken.None)).Result as ObjectResult,
            (await controller.GetSellableItemsPage("S01", null, null, cancellationToken: CancellationToken.None)).Result as ObjectResult,
            (await controller.GetCatalogDeltaPage("S01", "base", "target", null, cancellationToken: CancellationToken.None)).Result as ObjectResult,
            (await controller.CompareSellableItems(new CatalogCompareRequest("S01", []), CancellationToken.None)).Result as ObjectResult,
            (await controller.GetSpecialProductsPage("S01", null, cancellationToken: CancellationToken.None)).Result as ObjectResult,
            (await controller.GetCodeConflicts("S01", CancellationToken.None)).Result as ObjectResult
        };

        foreach (var response in responses)
        {
            Assert.NotNull(response);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);
            var errorCode = response.Value!.GetType().GetProperty(nameof(ApiResult<object>.ErrorCode))!.GetValue(response.Value);
            Assert.Equal("CATALOG_CAPACITY_BUSY", errorCode);
        }
    }

    [Fact]
    public void CatalogController_RequiresAuthorizationExceptStoreList()
    {
        Assert.NotNull(typeof(CatalogController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .SingleOrDefault());
        Assert.NotNull(typeof(CatalogController)
            .GetMethod(nameof(CatalogController.GetStores))?
            .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: false)
            .SingleOrDefault());
    }

    [Fact]
    public async Task GetSellableItemsPage_ReturnsWrappedServiceResponse()
    {
        var expected = new CatalogSyncPageResponse("S01", DateTimeOffset.UnixEpoch, null, [], [], null, false, 0);
        var service = new FakeCatalogService { PageResponse = expected };
        var controller = new CatalogController(service);

        var result = await controller.GetSellableItemsPage(
            "S01",
            DateTimeOffset.UnixEpoch,
            "cursor-1",
            100,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<CatalogSyncPageResponse>>(ok.Value);
        Assert.True(apiResult.Success);
        Assert.Same(expected, apiResult.Data);
        Assert.Equal(
            ("S01", DateTimeOffset.UnixEpoch, "cursor-1", 100, null, 1),
            service.LastPageRequest);
    }

    [Fact]
    public async Task GetSellableItemsPage_ForwardsPinnedVersionAndChecksumVersion()
    {
        var expected = new CatalogSyncPageResponse("S01", DateTimeOffset.UnixEpoch, null, [], [], null, false, 0);
        var service = new FakeCatalogService { PageResponse = expected };
        var controller = new CatalogController(service);

        var result = await controller.GetSellableItemsPage(
            "S01",
            DateTimeOffset.UnixEpoch,
            "cursor-1",
            100,
            CancellationToken.None,
            catalogVersion: "catalog-v1:pinned",
            checksumVersion: 2);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(
            ("S01", DateTimeOffset.UnixEpoch, "cursor-1", 100, "catalog-v1:pinned", 2),
            service.LastPageRequest);
    }

    [Fact]
    public async Task GetSellableItemsPage_RejectsUnsupportedChecksumVersion()
    {
        var service = new FakeCatalogService();
        var controller = new CatalogController(service);

        var result = await controller.GetSellableItemsPage(
            "S01",
            since: null,
            cursor: null,
            pageSize: 100,
            CancellationToken.None,
            checksumVersion: 3);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<CatalogSyncPageResponse>>(badRequest.Value);
        Assert.Equal("CATALOG_CHECKSUM_VERSION_UNSUPPORTED", apiResult.ErrorCode);
        Assert.Null(service.LastPageRequest);
    }

    [Fact]
    public async Task GetSellableItemsPage_V2ContinuationRequiresCatalogVersion()
    {
        var service = new FakeCatalogService();
        var controller = new CatalogController(service);

        var result = await controller.GetSellableItemsPage(
            "S01",
            since: null,
            cursor: "cursor-1",
            pageSize: 100,
            CancellationToken.None,
            checksumVersion: 2);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<CatalogSyncPageResponse>>(badRequest.Value);
        Assert.Equal("CATALOG_VERSION_REQUIRED", apiResult.ErrorCode);
        Assert.Null(service.LastPageRequest);
    }

    [Fact]
    public async Task GetSellableItemsPage_ReturnsConflictWhenPinnedSnapshotExpired()
    {
        var service = new FakeCatalogService { ThrowSnapshotExpired = true };
        var controller = new CatalogController(service);

        var result = await controller.GetSellableItemsPage(
            "S01",
            since: null,
            cursor: "cursor-1",
            pageSize: 100,
            CancellationToken.None,
            catalogVersion: "catalog-v1:expired",
            checksumVersion: 2);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<CatalogSyncPageResponse>>(conflict.Value);
        Assert.Equal("CATALOG_SNAPSHOT_EXPIRED", apiResult.ErrorCode);
    }

    [Fact]
    public void CatalogSyncPageResponse_keeps_old_constructor_and_json_clients_compatible()
    {
        var constructedByLegacySource = new CatalogSyncPageResponse(
            "S01",
            DateTimeOffset.UnixEpoch,
            null,
            [],
            [],
            null,
            false,
            0);
        var current = new CatalogSyncPageResponse(
            "S01",
            DateTimeOffset.UnixEpoch,
            null,
            [],
            [],
            null,
            false,
            0,
            CatalogVersion: "catalog-v1:test",
            PageChecksum: "sha256-catalog-page-v1:test");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var legacyClient = JsonSerializer.Deserialize<LegacyCatalogSyncPageResponse>(
            JsonSerializer.Serialize(current, options),
            options);
        var currentClientFromLegacyJson = JsonSerializer.Deserialize<CatalogSyncPageResponse>(
            """
            {
              "storeCode": "S01",
              "generatedAt": "1970-01-01T00:00:00+00:00",
              "cursor": null,
              "items": [],
              "deletedLookups": [],
              "nextCursor": null,
              "hasMore": false,
              "totalCount": 0
            }
            """,
            options);

        Assert.Equal(string.Empty, constructedByLegacySource.CatalogVersion);
        Assert.Equal(string.Empty, constructedByLegacySource.PageChecksum);
        Assert.NotNull(legacyClient);
        Assert.Equal("S01", legacyClient.StoreCode);
        Assert.NotNull(currentClientFromLegacyJson);
        Assert.Equal(string.Empty, currentClientFromLegacyJson.CatalogVersion);
        Assert.Equal(string.Empty, currentClientFromLegacyJson.PageChecksum);
    }

    [Fact]
    public async Task GetPromotionRules_ReturnsWrappedServiceResponse()
    {
        var expected = new CatalogPromotionsResponse("S01", DateTimeOffset.UnixEpoch, []);
        var service = new FakeCatalogService { PromotionRulesResponse = expected };
        var controller = new CatalogController(service);

        var result = await controller.GetPromotionRules("S01", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<CatalogPromotionsResponse>>(ok.Value);
        Assert.True(apiResult.Success);
        Assert.Same(expected, apiResult.Data);
        Assert.Equal("S01", service.LastPromotionRulesStoreCode);
    }

    [Fact]
    public async Task GetPromotionRules_ReturnsForbiddenWhenDeviceStoreDoesNotMatch()
    {
        var controller = new CatalogController(new FakeCatalogService());
        SetAuthenticatedDevice(controller, storeCode: "S02", deviceCode: "POS-02");

        var result = await controller.GetPromotionRules("S01", CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        var apiResult = Assert.IsType<ApiResult<CatalogPromotionsResponse>>(forbidden.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("DEVICE_SCOPE_FORBIDDEN", apiResult.ErrorCode);
    }

    [Fact]
    public async Task CompareSellableItems_ReturnsBadRequestWhenStoreCodeMissing()
    {
        var controller = new CatalogController(new FakeCatalogService());

        var result = await controller.CompareSellableItems(
            new CatalogCompareRequest("", []),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<CatalogCompareResponse>>(badRequest.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("STORE_CODE_REQUIRED", apiResult.ErrorCode);
    }

    [Fact]
    public async Task LookupSellableItem_RequiresLookupCodeOrNormalizedKey()
    {
        var controller = new CatalogController(new FakeCatalogService());

        var result = await controller.LookupSellableItem("S01", null, null, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<CatalogLookupResponse>>(badRequest.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("LOOKUP_CODE_REQUIRED", apiResult.ErrorCode);
    }

    [Fact]
    public async Task LookupSellableItem_PassesLookupCodeAndNormalizedKey()
    {
        var expected = new CatalogLookupResponse("S01", " abc-01 ", "ABC01", true, null);
        var service = new FakeCatalogService { LookupResponse = expected };
        var controller = new CatalogController(service);

        var result = await controller.LookupSellableItem(
            "S01",
            " abc-01 ",
            "ABC01",
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<CatalogLookupResponse>>(ok.Value);
        Assert.True(apiResult.Success);
        Assert.Same(expected, apiResult.Data);
        Assert.Equal(("S01", " abc-01 ", "ABC01"), service.LastLookupRequest);
    }

    [Fact]
    public async Task LookupSellableItem_ReturnsStoreNotFoundWhenServiceHasNoStoreIndex()
    {
        var controller = new CatalogController(new FakeCatalogService());

        var result = await controller.LookupSellableItem("S01", "missing", null, CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<CatalogLookupResponse>>(notFound.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("STORE_NOT_FOUND", apiResult.ErrorCode);
    }

    [Fact]
    public async Task GetSellableItemsPage_ReturnsForbiddenWhenDeviceStoreDoesNotMatch()
    {
        var controller = new CatalogController(new FakeCatalogService());
        SetAuthenticatedDevice(controller, storeCode: "S02", deviceCode: "POS-02");

        var result = await controller.GetSellableItemsPage(
            "S01",
            since: null,
            cursor: null,
            pageSize: 100,
            CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        var apiResult = Assert.IsType<ApiResult<CatalogSyncPageResponse>>(forbidden.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("DEVICE_SCOPE_FORBIDDEN", apiResult.ErrorCode);
    }

    [Fact]
    public async Task GetSpecialProductsPage_ReturnsWrappedServiceResponse()
    {
        var expected = new CatalogSpecialProductsPageResponse("S01", DateTimeOffset.UnixEpoch, null, [], null, false, 0);
        var service = new FakeCatalogService { SpecialProductsPageResponse = expected };
        var controller = new CatalogController(service);

        var result = await controller.GetSpecialProductsPage(
            "S01",
            "cursor-1",
            5000,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<CatalogSpecialProductsPageResponse>>(ok.Value);
        Assert.True(apiResult.Success);
        Assert.Same(expected, apiResult.Data);
        Assert.Equal(("S01", "cursor-1", 5000), service.LastSpecialProductsPageRequest);
    }

    [Fact]
    public async Task GetSpecialProductsPage_ReturnsForbiddenWhenDeviceStoreDoesNotMatch()
    {
        var controller = new CatalogController(new FakeCatalogService());
        SetAuthenticatedDevice(controller, storeCode: "S02", deviceCode: "POS-02");

        var result = await controller.GetSpecialProductsPage(
            "S01",
            cursor: null,
            pageSize: 100,
            CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        var apiResult = Assert.IsType<ApiResult<CatalogSpecialProductsPageResponse>>(forbidden.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("DEVICE_SCOPE_FORBIDDEN", apiResult.ErrorCode);
    }

    [Fact]
    public async Task GetCodeConflicts_ReturnsWrappedServiceResponse()
    {
        var expected = new CatalogCodeConflictsResponse("S01", DateTimeOffset.UnixEpoch, true, []);
        var service = new FakeCatalogService { CodeConflictsResponse = expected };
        var controller = new CatalogController(service);

        var result = await controller.GetCodeConflicts("S01", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<CatalogCodeConflictsResponse>>(ok.Value);
        Assert.True(apiResult.Success);
        Assert.Same(expected, apiResult.Data);
        Assert.Equal("S01", service.LastCodeConflictsStoreCode);
    }

    [Fact]
    public async Task GetCodeConflicts_ReturnsForbiddenWhenDeviceStoreDoesNotMatch()
    {
        var service = new FakeCatalogService();
        var controller = new CatalogController(service);
        SetAuthenticatedDevice(controller, storeCode: "S02", deviceCode: "POS-02");

        var result = await controller.GetCodeConflicts("S01", CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        var apiResult = Assert.IsType<ApiResult<CatalogCodeConflictsResponse>>(forbidden.Value);
        Assert.Equal("DEVICE_SCOPE_FORBIDDEN", apiResult.ErrorCode);
        Assert.Null(service.LastCodeConflictsStoreCode);
    }

    [Fact]
    public async Task GetCodeConflicts_ReturnsStoreNotFoundWhenServiceHasNoStoreIndex()
    {
        var controller = new CatalogController(new FakeCatalogService());

        var result = await controller.GetCodeConflicts("S01", CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<CatalogCodeConflictsResponse>>(notFound.Value);
        Assert.Equal("STORE_NOT_FOUND", apiResult.ErrorCode);
    }

    [Fact]
    public async Task MarkSpecialProduct_ReturnsBadRequestWhenProductCodeMissing()
    {
        var controller = new CatalogController(new FakeCatalogService());
        SetAuthenticatedDevice(controller, storeCode: "S01", deviceCode: "POS-01");

        var result = await controller.MarkSpecialProduct(
            new CatalogSpecialProductMarkRequest("S01", "", true),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<CatalogSpecialProductMarkResponse>>(badRequest.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("PRODUCT_CODE_REQUIRED", apiResult.ErrorCode);
    }

    [Fact]
    public async Task MarkSpecialProduct_ReturnsForbiddenWhenDeviceStoreDoesNotMatch()
    {
        var controller = new CatalogController(new FakeCatalogService());
        SetAuthenticatedDevice(controller, storeCode: "S02", deviceCode: "POS-02");

        var result = await controller.MarkSpecialProduct(
            new CatalogSpecialProductMarkRequest("S01", "P01", true),
            CancellationToken.None);

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        var apiResult = Assert.IsType<ApiResult<CatalogSpecialProductMarkResponse>>(forbidden.Value);
        Assert.False(apiResult.Success);
        Assert.Equal("DEVICE_SCOPE_FORBIDDEN", apiResult.ErrorCode);
    }

    [Fact]
    public async Task MarkSpecialProduct_ReturnsWrappedServiceResponse()
    {
        var service = new FakeCatalogService();
        var controller = new CatalogController(service);
        SetAuthenticatedDevice(controller, storeCode: "S01", deviceCode: "POS-01");

        var result = await controller.MarkSpecialProduct(
            new CatalogSpecialProductMarkRequest("S01", "P01", true),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var apiResult = Assert.IsType<ApiResult<CatalogSpecialProductMarkResponse>>(ok.Value);
        Assert.True(apiResult.Success);
        Assert.NotNull(apiResult.Data);
        Assert.Equal("S01", apiResult.Data.StoreCode);
        Assert.Equal("P01", apiResult.Data.ProductCode);
        Assert.True(apiResult.Data.IsSpecialProduct);
        Assert.Equal("POS-01", service.LastSpecialProductUpdatedBy);
        Assert.Equal(new CatalogSpecialProductMarkRequest("S01", "P01", true), service.LastSpecialProductRequest);
    }

    private static string? GetHttpGetTemplate(string methodName)
    {
        return typeof(CatalogController)
            .GetMethod(methodName)?
            .GetCustomAttributes(typeof(HttpGetAttribute), inherit: false)
            .Cast<HttpGetAttribute>()
            .Single()
            .Template;
    }

    private static string? GetHttpPostTemplate(string methodName)
    {
        return typeof(CatalogController)
            .GetMethod(methodName)?
            .GetCustomAttributes(typeof(HttpPostAttribute), inherit: false)
            .Cast<HttpPostAttribute>()
            .Single()
            .Template;
    }

    private static void SetAuthenticatedDevice(
        ControllerBase controller,
        string storeCode,
        string deviceCode)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(DeviceAuthConstants.StoreCodeClaim, storeCode),
            new Claim(DeviceAuthConstants.DeviceCodeClaim, deviceCode),
            new Claim(DeviceAuthConstants.HardwareIdClaim, "HW-001")
        ], DeviceAuthConstants.Scheme);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity)
            }
        };
    }

    private sealed class FakeCatalogService : ICatalogService
    {
        public CatalogSyncPageResponse? PageResponse { get; init; }

        public CatalogSyncPlanResponse? SyncPlanResponse { get; init; }

        public CatalogDeltaPageResponse? DeltaPageResponse { get; init; }

        public CatalogCompareResponse? CompareResponse { get; init; }

        public CatalogLookupResponse? LookupResponse { get; init; }

        public CatalogSpecialProductsPageResponse? SpecialProductsPageResponse { get; init; }

        public CatalogCodeConflictsResponse? CodeConflictsResponse { get; init; }

        public string? LastCodeConflictsStoreCode { get; private set; }

        public CatalogPromotionsResponse? PromotionRulesResponse { get; init; }

        public bool ThrowSnapshotExpired { get; init; }

        public bool ThrowDeltaSnapshotExpired { get; init; }

        public bool ThrowCapacityBusy { get; init; }

        public (
            string StoreCode,
            DateTimeOffset? Since,
            string? Cursor,
            int PageSize,
            string? CatalogVersion,
            int ChecksumVersion)? LastPageRequest { get; private set; }

        public CatalogCompareRequest? LastCompareRequest { get; private set; }

        public (string StoreCode, string? BaseCatalogVersion)? LastSyncPlanRequest { get; private set; }

        public (string StoreCode, string BaseCatalogVersion, string TargetCatalogVersion, string? Cursor, int PageSize)? LastDeltaPageRequest { get; private set; }

        public (string StoreCode, string? LookupCode, string? LookupCodeNormalized)? LastLookupRequest { get; private set; }

        public (string StoreCode, string? Cursor, int PageSize)? LastSpecialProductsPageRequest { get; private set; }

        public string? LastPromotionRulesStoreCode { get; private set; }

        public CatalogSpecialProductMarkRequest? LastSpecialProductRequest { get; private set; }

        public string? LastSpecialProductUpdatedBy { get; private set; }

        public Task<IReadOnlyList<StoreDto>> GetStoresAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<StoreDto>>([]);
        }

        public Task<SellableItemsResponse?> GetSellableItemsAsync(
            string storeCode,
            DateTimeOffset? since,
            CancellationToken cancellationToken)
        {
            return ThrowCapacityBusy
                ? Task.FromException<SellableItemsResponse?>(new CatalogCapacityBusyException("busy"))
                : Task.FromResult<SellableItemsResponse?>(null);
        }

        public Task<CatalogSyncPageResponse?> GetSellableItemsPageAsync(
            string storeCode,
            DateTimeOffset? since,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken)
        {
            LastPageRequest = (storeCode, since, cursor, pageSize, null, 1);
            return Task.FromResult(PageResponse);
        }

        public Task<CatalogSyncPageResponse?> GetSellableItemsPageAsync(
            string storeCode,
            DateTimeOffset? since,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken,
            string? catalogVersion,
            int checksumVersion)
        {
            LastPageRequest = (storeCode, since, cursor, pageSize, catalogVersion, checksumVersion);
            if (ThrowCapacityBusy)
            {
                return Task.FromException<CatalogSyncPageResponse?>(new CatalogCapacityBusyException("busy"));
            }

            return ThrowSnapshotExpired
                ? Task.FromException<CatalogSyncPageResponse?>(
                    new CatalogSnapshotExpiredException(storeCode, catalogVersion ?? string.Empty))
                : Task.FromResult(PageResponse);
        }

        public Task<CatalogSyncPageResponse?> GetSellableItemsPageWithLeaseAsync(
            string storeCode, DateTimeOffset? since, string? cursor, int pageSize,
            CancellationToken cancellationToken, string? catalogVersion, int checksumVersion, string? downloadLeaseId)
        {
            return GetSellableItemsPageAsync(storeCode, since, cursor, pageSize, cancellationToken, catalogVersion, checksumVersion);
        }

        public Task<CatalogSyncPlanResponse?> GetCatalogSyncPlanAsync(
            string storeCode,
            string? baseCatalogVersion,
            CancellationToken cancellationToken)
        {
            LastSyncPlanRequest = (storeCode, baseCatalogVersion);
            return Task.FromResult(SyncPlanResponse);
        }

        public Task<CatalogSyncPlanResponse?> GetCatalogSyncPlanWithLeaseAsync(
            string storeCode, string? baseCatalogVersion, CancellationToken cancellationToken)
        {
            return GetCatalogSyncPlanAsync(storeCode, baseCatalogVersion, cancellationToken);
        }

        public Task<CatalogDeltaPageResponse> GetCatalogDeltaPageAsync(
            string storeCode,
            string baseCatalogVersion,
            string targetCatalogVersion,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken)
        {
            LastDeltaPageRequest = (storeCode, baseCatalogVersion, targetCatalogVersion, cursor, pageSize);
            if (ThrowCapacityBusy)
            {
                return Task.FromException<CatalogDeltaPageResponse>(new CatalogCapacityBusyException("busy"));
            }

            return ThrowDeltaSnapshotExpired
                ? Task.FromException<CatalogDeltaPageResponse>(
                    new CatalogSnapshotExpiredException(storeCode, baseCatalogVersion))
                : Task.FromResult(DeltaPageResponse ?? new CatalogDeltaPageResponse(
                    storeCode,
                    DateTimeOffset.UnixEpoch,
                    baseCatalogVersion,
                    targetCatalogVersion,
                    cursor,
                    [],
                    [],
                    null,
                    false,
                    0,
                    string.Empty));
        }

        public Task<CatalogDeltaPageResponse> GetCatalogDeltaPageWithLeaseAsync(
            string storeCode, string baseCatalogVersion, string targetCatalogVersion, string? cursor,
            int pageSize, CancellationToken cancellationToken, string? downloadLeaseId)
        {
            return GetCatalogDeltaPageAsync(storeCode, baseCatalogVersion, targetCatalogVersion, cursor, pageSize, cancellationToken);
        }

        public Task<CatalogCompareResponse?> CompareSellableItemsAsync(
            CatalogCompareRequest request,
            CancellationToken cancellationToken)
        {
            LastCompareRequest = request;
            return ThrowCapacityBusy
                ? Task.FromException<CatalogCompareResponse?>(new CatalogCapacityBusyException("busy"))
                : Task.FromResult(CompareResponse);
        }

        public Task<CatalogLookupResponse?> LookupSellableItemAsync(
            string storeCode,
            string? lookupCode,
            string? lookupCodeNormalized,
            CancellationToken cancellationToken)
        {
            LastLookupRequest = (storeCode, lookupCode, lookupCodeNormalized);
            return Task.FromResult(LookupResponse);
        }

        public Task<CatalogSpecialProductsPageResponse?> GetSpecialProductsPageAsync(
            string storeCode,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken)
        {
            LastSpecialProductsPageRequest = (storeCode, cursor, pageSize);
            return ThrowCapacityBusy
                ? Task.FromException<CatalogSpecialProductsPageResponse?>(new CatalogCapacityBusyException("busy"))
                : Task.FromResult(SpecialProductsPageResponse);
        }

        public Task<CatalogCodeConflictsResponse?> GetCodeConflictsAsync(
            string storeCode,
            CancellationToken cancellationToken)
        {
            LastCodeConflictsStoreCode = storeCode;
            return ThrowCapacityBusy
                ? Task.FromException<CatalogCodeConflictsResponse?>(new CatalogCapacityBusyException("busy"))
                : Task.FromResult(CodeConflictsResponse);
        }

        public Task<CatalogPromotionsResponse?> GetPromotionRulesAsync(
            string storeCode,
            CancellationToken cancellationToken)
        {
            LastPromotionRulesStoreCode = storeCode;
            return Task.FromResult(PromotionRulesResponse);
        }

        public Task<CatalogSpecialProductMarkServiceResult> MarkSpecialProductAsync(
            CatalogSpecialProductMarkRequest request,
            string updatedBy,
            CancellationToken cancellationToken)
        {
            LastSpecialProductRequest = request;
            LastSpecialProductUpdatedBy = updatedBy;
            return Task.FromResult(CatalogSpecialProductMarkServiceResult.Ok(new CatalogSpecialProductMarkResponse(
                request.StoreCode,
                request.ProductCode,
                request.IsSpecialProduct,
                DateTimeOffset.UnixEpoch,
                [])));
        }
    }

    private sealed record LegacyCatalogSyncPageResponse(
        string StoreCode,
        DateTimeOffset GeneratedAt,
        string? Cursor,
        IReadOnlyList<CatalogLookupItemDto> Items,
        IReadOnlyList<DeletedLookupDto> DeletedLookups,
        string? NextCursor,
        bool HasMore,
        int TotalCount);
}
