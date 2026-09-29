using System.Diagnostics;
using System.Security.Claims;
using Hbpos.Api.Auth;
using Hbpos.Api.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hbpos.Api.Controllers;

[ApiController]
[Route("api/v1/catalog")]
[Authorize]
public sealed class CatalogController(ICatalogService catalogService) : ControllerBase
{
    private const int MaxPageSize = 5000;

    [AllowAnonymous]
    [HttpGet("stores")]
    public async Task<ActionResult<ApiResult<IReadOnlyList<StoreDto>>>> GetStores(
        CancellationToken cancellationToken)
    {
        var stores = await catalogService.GetStoresAsync(cancellationToken);
        return Ok(ApiResult<IReadOnlyList<StoreDto>>.Ok(stores));
    }

    [HttpGet("sellable-items")]
    public async Task<ActionResult<ApiResult<SellableItemsResponse>>> GetSellableItems(
        [FromQuery] string storeCode,
        [FromQuery] DateTimeOffset? since,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return BadRequest(ApiResult<SellableItemsResponse>.Fail("STORE_CODE_REQUIRED", "storeCode 不能为空"));
        }

        if (!this.IsDeviceScopeAllowed(storeCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<SellableItemsResponse>("Device is not authorized for this store.");
        }

        SellableItemsResponse? response;
        try
        {
            response = await catalogService.GetSellableItemsAsync(storeCode, since, cancellationToken);
        }
        catch (CatalogCapacityBusyException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiResult<SellableItemsResponse>.Fail("CATALOG_CAPACITY_BUSY", "catalog download capacity is busy"));
        }
        return response is null
            ? NotFound(ApiResult<SellableItemsResponse>.Fail("STORE_NOT_FOUND", "门店不存在或已停用"))
            : Ok(ApiResult<SellableItemsResponse>.Ok(response));
    }

    [HttpGet("sellable-items/page")]
    public async Task<ActionResult<ApiResult<CatalogSyncPageResponse>>> GetSellableItemsPage(
        [FromQuery] string storeCode,
        [FromQuery] DateTimeOffset? since,
        [FromQuery] string? cursor,
        [FromQuery] int pageSize = 500,
        CancellationToken cancellationToken = default,
        [FromQuery] string? catalogVersion = null,
        [FromQuery] int checksumVersion = 1,
        [FromQuery] string? downloadLeaseId = null)
    {
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return BadRequest(ApiResult<CatalogSyncPageResponse>.Fail("STORE_CODE_REQUIRED", "storeCode is required"));
        }

        if (pageSize <= 0 || pageSize > MaxPageSize)
        {
            return BadRequest(ApiResult<CatalogSyncPageResponse>.Fail("PAGE_SIZE_INVALID", $"pageSize must be between 1 and {MaxPageSize}"));
        }

        if (checksumVersion is not 1 and not 2)
        {
            return BadRequest(ApiResult<CatalogSyncPageResponse>.Fail(
                "CATALOG_CHECKSUM_VERSION_UNSUPPORTED",
                "checksumVersion must be 1 or 2"));
        }

        if (checksumVersion == 2 &&
            !string.IsNullOrWhiteSpace(cursor) &&
            string.IsNullOrWhiteSpace(catalogVersion))
        {
            return BadRequest(ApiResult<CatalogSyncPageResponse>.Fail(
                "CATALOG_VERSION_REQUIRED",
                "catalogVersion is required for checksum v2 continuation pages"));
        }

        if (!this.IsDeviceScopeAllowed(storeCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<CatalogSyncPageResponse>("Device is not authorized for this store.");
        }

        var stopwatch = Stopwatch.StartNew();
        Log($"page request store={storeCode} continuation={!string.IsNullOrWhiteSpace(cursor)} pinned={!string.IsNullOrWhiteSpace(catalogVersion)} pageSize={pageSize} checksumVersion={checksumVersion}");
        CatalogSyncPageResponse? response;
        try
        {
            response = await catalogService.GetSellableItemsPageWithLeaseAsync(
                storeCode,
                since,
                cursor,
                pageSize,
                cancellationToken,
                catalogVersion,
                checksumVersion,
                downloadLeaseId);
        }
        catch (CatalogSnapshotExpiredException)
        {
            stopwatch.Stop();
            Log($"page response store={storeCode} status=409 reason=snapshot-expired elapsedMs={stopwatch.ElapsedMilliseconds}");
            return Conflict(ApiResult<CatalogSyncPageResponse>.Fail(
                "CATALOG_SNAPSHOT_EXPIRED",
                "catalog snapshot expired; restart the download"));
        }
        catch (CatalogCapacityBusyException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiResult<CatalogSyncPageResponse>.Fail("CATALOG_CAPACITY_BUSY", "catalog download capacity is busy"));
        }
        catch (CatalogSnapshotIsolationUnavailableException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiResult<CatalogSyncPageResponse>.Fail(
                    "CATALOG_SNAPSHOT_ISOLATION_UNAVAILABLE",
                    "catalog snapshot isolation is unavailable"));
        }

        stopwatch.Stop();
        Log(response is null
            ? $"page response store={storeCode} status=404 elapsedMs={stopwatch.ElapsedMilliseconds}"
            : $"page response store={response.StoreCode} status=200 items={response.Items.Count} deletedLookups={response.DeletedLookups.Count} hasMore={response.HasMore} continuation={response.NextCursor is not null} elapsedMs={stopwatch.ElapsedMilliseconds}");

        return response is null
            ? NotFound(ApiResult<CatalogSyncPageResponse>.Fail("STORE_NOT_FOUND", "store was not found or inactive"))
            : Ok(ApiResult<CatalogSyncPageResponse>.Ok(response));
    }

    [HttpGet("sync-plan")]
    public async Task<ActionResult<ApiResult<CatalogSyncPlanResponse>>> GetCatalogSyncPlan(
        [FromQuery] string storeCode,
        [FromQuery] string? baseCatalogVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return BadRequest(ApiResult<CatalogSyncPlanResponse>.Fail("STORE_CODE_REQUIRED", "storeCode is required"));
        }

        if (!this.IsDeviceScopeAllowed(storeCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<CatalogSyncPlanResponse>("Device is not authorized for this store.");
        }

        CatalogSyncPlanResponse? response;
        try
        {
            response = await catalogService.GetCatalogSyncPlanWithLeaseAsync(
                storeCode,
                baseCatalogVersion,
                cancellationToken);
        }
        catch (CatalogCapacityBusyException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiResult<CatalogSyncPlanResponse>.Fail("CATALOG_CAPACITY_BUSY", "catalog download capacity is busy"));
        }
        catch (CatalogSnapshotIsolationUnavailableException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiResult<CatalogSyncPlanResponse>.Fail(
                    "CATALOG_SNAPSHOT_ISOLATION_UNAVAILABLE",
                    "catalog snapshot isolation is unavailable"));
        }
        return response is null
            ? NotFound(ApiResult<CatalogSyncPlanResponse>.Fail("STORE_NOT_FOUND", "store was not found or inactive"))
            : Ok(ApiResult<CatalogSyncPlanResponse>.Ok(response));
    }

    [HttpGet("delta/page")]
    public async Task<ActionResult<ApiResult<CatalogDeltaPageResponse>>> GetCatalogDeltaPage(
        [FromQuery] string storeCode,
        [FromQuery] string baseCatalogVersion,
        [FromQuery] string targetCatalogVersion,
        [FromQuery] string? cursor,
        [FromQuery] int pageSize = 500,
        CancellationToken cancellationToken = default,
        [FromQuery] string? downloadLeaseId = null)
    {
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return BadRequest(ApiResult<CatalogDeltaPageResponse>.Fail("STORE_CODE_REQUIRED", "storeCode is required"));
        }

        if (string.IsNullOrWhiteSpace(baseCatalogVersion) || string.IsNullOrWhiteSpace(targetCatalogVersion))
        {
            return BadRequest(ApiResult<CatalogDeltaPageResponse>.Fail(
                "CATALOG_VERSION_REQUIRED",
                "baseCatalogVersion and targetCatalogVersion are required"));
        }

        if (pageSize <= 0 || pageSize > MaxPageSize)
        {
            return BadRequest(ApiResult<CatalogDeltaPageResponse>.Fail("PAGE_SIZE_INVALID", $"pageSize must be between 1 and {MaxPageSize}"));
        }

        if (!this.IsDeviceScopeAllowed(storeCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<CatalogDeltaPageResponse>("Device is not authorized for this store.");
        }

        try
        {
            var response = await catalogService.GetCatalogDeltaPageWithLeaseAsync(
                storeCode,
                baseCatalogVersion,
                targetCatalogVersion,
                cursor,
                pageSize,
                cancellationToken,
                downloadLeaseId);
            return Ok(ApiResult<CatalogDeltaPageResponse>.Ok(response));
        }
        catch (CatalogSnapshotExpiredException)
        {
            // 基准或目标快照任一过期都无法保证 delete 完整性，客户端必须回退全量。
            return Conflict(ApiResult<CatalogDeltaPageResponse>.Fail(
                "CATALOG_SNAPSHOT_EXPIRED",
                "catalog snapshot expired; restart with a full download"));
        }
        catch (CatalogCapacityBusyException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiResult<CatalogDeltaPageResponse>.Fail("CATALOG_CAPACITY_BUSY", "catalog download capacity is busy"));
        }
    }

    [HttpPost("sellable-items/compare")]
    public async Task<ActionResult<ApiResult<CatalogCompareResponse>>> CompareSellableItems(
        [FromBody] CatalogCompareRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(ApiResult<CatalogCompareResponse>.Fail("COMPARE_REQUEST_REQUIRED", "request body is required"));
        }

        if (string.IsNullOrWhiteSpace(request.StoreCode))
        {
            return BadRequest(ApiResult<CatalogCompareResponse>.Fail("STORE_CODE_REQUIRED", "storeCode is required"));
        }

        if (!this.IsDeviceScopeAllowed(request.StoreCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<CatalogCompareResponse>("Device is not authorized for this store.");
        }

        var stopwatch = Stopwatch.StartNew();
        Log($"compare request store={request.StoreCode} localLookups={request.LocalLookups.Count}");
        CatalogCompareResponse? response;
        try
        {
            response = await catalogService.CompareSellableItemsAsync(request, cancellationToken);
        }
        catch (CatalogCapacityBusyException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiResult<CatalogCompareResponse>.Fail("CATALOG_CAPACITY_BUSY", "catalog download capacity is busy"));
        }
        stopwatch.Stop();
        Log(response is null
            ? $"compare response store={request.StoreCode} status=404 elapsedMs={stopwatch.ElapsedMilliseconds}"
            : $"compare response store={response.StoreCode} status=200 upsertedLookups={response.UpsertedLookups.Count} deletedLookups={response.DeletedLookups.Count} hasMore={response.HasMore} elapsedMs={stopwatch.ElapsedMilliseconds}");
        return response is null
            ? NotFound(ApiResult<CatalogCompareResponse>.Fail("STORE_NOT_FOUND", "store was not found or inactive"))
            : Ok(ApiResult<CatalogCompareResponse>.Ok(response));
    }

    [HttpGet("promotions")]
    public async Task<ActionResult<ApiResult<CatalogPromotionsResponse>>> GetPromotionRules(
        [FromQuery] string storeCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return BadRequest(ApiResult<CatalogPromotionsResponse>.Fail("STORE_CODE_REQUIRED", "storeCode is required"));
        }

        if (!this.IsDeviceScopeAllowed(storeCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<CatalogPromotionsResponse>("Device is not authorized for this store.");
        }

        var stopwatch = Stopwatch.StartNew();
        Log($"promotions request store={storeCode}");
        var response = await catalogService.GetPromotionRulesAsync(storeCode, cancellationToken);
        stopwatch.Stop();
        Log(response is null
            ? $"promotions response store={storeCode} status=404 elapsedMs={stopwatch.ElapsedMilliseconds}"
            : $"promotions response store={response.StoreCode} status=200 rules={response.Promotions.Count} elapsedMs={stopwatch.ElapsedMilliseconds}");

        return response is null
            ? NotFound(ApiResult<CatalogPromotionsResponse>.Fail("STORE_NOT_FOUND", "store was not found or inactive"))
            : Ok(ApiResult<CatalogPromotionsResponse>.Ok(response));
    }

    [HttpGet("sellable-items/lookup")]
    public async Task<ActionResult<ApiResult<CatalogLookupResponse>>> LookupSellableItem(
        [FromQuery] string storeCode,
        [FromQuery] string? lookupCode,
        [FromQuery] string? lookupCodeNormalized,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return BadRequest(ApiResult<CatalogLookupResponse>.Fail("STORE_CODE_REQUIRED", "storeCode is required"));
        }

        if (string.IsNullOrWhiteSpace(lookupCode) && string.IsNullOrWhiteSpace(lookupCodeNormalized))
        {
            return BadRequest(ApiResult<CatalogLookupResponse>.Fail("LOOKUP_CODE_REQUIRED", "lookupCode or lookupCodeNormalized is required"));
        }

        if (!this.IsDeviceScopeAllowed(storeCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<CatalogLookupResponse>("Device is not authorized for this store.");
        }

        var stopwatch = Stopwatch.StartNew();
        Log($"lookup request store={storeCode} lookupCode={lookupCode ?? "<null>"} lookupCodeNormalized={lookupCodeNormalized ?? "<null>"}");
        var response = await catalogService.LookupSellableItemAsync(
            storeCode,
            lookupCode,
            lookupCodeNormalized,
            cancellationToken);
        stopwatch.Stop();
        Log(response is null
            ? $"lookup response store={storeCode} status=404 elapsedMs={stopwatch.ElapsedMilliseconds}"
            : $"lookup response store={response.StoreCode} status=200 found={response.Found} elapsedMs={stopwatch.ElapsedMilliseconds}");

        return response is null
            ? NotFound(ApiResult<CatalogLookupResponse>.Fail("STORE_NOT_FOUND", "store was not found or inactive"))
            : Ok(ApiResult<CatalogLookupResponse>.Ok(response));
    }

    /// <summary>
    /// 返回同一查询码对应多个不同商品的全部候选，供新版收银端扫码时弹窗选择；目录分页与扫码回查口径不变。
    /// </summary>
    [HttpGet("sellable-items/code-conflicts")]
    public async Task<ActionResult<ApiResult<CatalogCodeConflictsResponse>>> GetCodeConflicts(
        [FromQuery] string storeCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return BadRequest(ApiResult<CatalogCodeConflictsResponse>.Fail("STORE_CODE_REQUIRED", "storeCode is required"));
        }

        if (!this.IsDeviceScopeAllowed(storeCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<CatalogCodeConflictsResponse>("Device is not authorized for this store.");
        }

        var stopwatch = Stopwatch.StartNew();
        Log($"code conflicts request store={storeCode}");
        CatalogCodeConflictsResponse? response;
        try
        {
            response = await catalogService.GetCodeConflictsAsync(storeCode, cancellationToken);
        }
        catch (CatalogCapacityBusyException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiResult<CatalogCodeConflictsResponse>.Fail("CATALOG_CAPACITY_BUSY", "catalog download capacity is busy"));
        }
        stopwatch.Stop();
        Log(response is null
            ? $"code conflicts response store={storeCode} status=404 elapsedMs={stopwatch.ElapsedMilliseconds}"
            : $"code conflicts response store={response.StoreCode} status=200 available={response.Available} items={response.Items.Count} elapsedMs={stopwatch.ElapsedMilliseconds}");

        return response is null
            ? NotFound(ApiResult<CatalogCodeConflictsResponse>.Fail("STORE_NOT_FOUND", "store was not found or inactive"))
            : Ok(ApiResult<CatalogCodeConflictsResponse>.Ok(response));
    }

    [Authorize(Policy = CashierAuthorizationPolicies.SpecialProductsView)]
    [HttpGet("special-products/page")]
    public async Task<ActionResult<ApiResult<CatalogSpecialProductsPageResponse>>> GetSpecialProductsPage(
        [FromQuery] string storeCode,
        [FromQuery] string? cursor,
        [FromQuery] int pageSize = 500,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return BadRequest(ApiResult<CatalogSpecialProductsPageResponse>.Fail("STORE_CODE_REQUIRED", "storeCode is required"));
        }

        if (pageSize <= 0 || pageSize > MaxPageSize)
        {
            return BadRequest(ApiResult<CatalogSpecialProductsPageResponse>.Fail("PAGE_SIZE_INVALID", $"pageSize must be between 1 and {MaxPageSize}"));
        }

        if (!this.IsDeviceScopeAllowed(storeCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<CatalogSpecialProductsPageResponse>("Device is not authorized for this store.");
        }

        var stopwatch = Stopwatch.StartNew();
        Log($"special products page request store={storeCode} cursor={cursor ?? "<start>"} pageSize={pageSize}");
        CatalogSpecialProductsPageResponse? response;
        try
        {
            response = await catalogService.GetSpecialProductsPageAsync(
                storeCode,
                cursor,
                pageSize,
                cancellationToken);
        }
        catch (CatalogCapacityBusyException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiResult<CatalogSpecialProductsPageResponse>.Fail("CATALOG_CAPACITY_BUSY", "catalog download capacity is busy"));
        }
        stopwatch.Stop();
        Log(response is null
            ? $"special products page response store={storeCode} status=404 elapsedMs={stopwatch.ElapsedMilliseconds}"
            : $"special products page response store={response.StoreCode} status=200 items={response.Items.Count} hasMore={response.HasMore} next={response.NextCursor ?? "<end>"} elapsedMs={stopwatch.ElapsedMilliseconds}");

        return response is null
            ? NotFound(ApiResult<CatalogSpecialProductsPageResponse>.Fail("STORE_NOT_FOUND", "store was not found or inactive"))
            : Ok(ApiResult<CatalogSpecialProductsPageResponse>.Ok(response));
    }

    [Authorize(Policy = CashierAuthorizationPolicies.SpecialProductsManage)]
    [HttpPost("special-products/mark")]
    public async Task<ActionResult<ApiResult<CatalogSpecialProductMarkResponse>>> MarkSpecialProduct(
        [FromBody] CatalogSpecialProductMarkRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(ApiResult<CatalogSpecialProductMarkResponse>.Fail("MARK_REQUEST_REQUIRED", "request body is required"));
        }

        if (string.IsNullOrWhiteSpace(request.StoreCode))
        {
            return BadRequest(ApiResult<CatalogSpecialProductMarkResponse>.Fail("STORE_CODE_REQUIRED", "storeCode is required"));
        }

        if (string.IsNullOrWhiteSpace(request.ProductCode))
        {
            return BadRequest(ApiResult<CatalogSpecialProductMarkResponse>.Fail("PRODUCT_CODE_REQUIRED", "productCode is required"));
        }

        if (!this.IsDeviceScopeAllowed(request.StoreCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<CatalogSpecialProductMarkResponse>("Device is not authorized for this store.");
        }

        var updatedBy = User.FindFirstValue(DeviceAuthConstants.DeviceCodeClaim)
            ?? User.Identity?.Name
            ?? "pos-device";
        var stopwatch = Stopwatch.StartNew();
        Log($"special product mark request store={request.StoreCode} product={request.ProductCode} isSpecialProduct={request.IsSpecialProduct}");
        var response = await catalogService.MarkSpecialProductAsync(request, updatedBy, cancellationToken);
        stopwatch.Stop();
        Log(response.Success && response.Response is not null
            ? $"special product mark response store={response.Response.StoreCode} product={response.Response.ProductCode} status=200 isSpecialProduct={response.Response.IsSpecialProduct} items={response.Response.Items.Count} elapsedMs={stopwatch.ElapsedMilliseconds}"
            : $"special product mark response store={request.StoreCode} product={request.ProductCode} status=failed errorCode={response.ErrorCode ?? "<null>"} elapsedMs={stopwatch.ElapsedMilliseconds}");
        if (response.Success && response.Response is not null)
        {
            return Ok(ApiResult<CatalogSpecialProductMarkResponse>.Ok(response.Response));
        }

        var failed = ApiResult<CatalogSpecialProductMarkResponse>.Fail(
            response.ErrorCode ?? "SPECIAL_PRODUCT_MARK_FAILED",
            response.Message ?? "failed to update special product");
        return response.ErrorCode is "STORE_NOT_FOUND" or "PRODUCT_NOT_FOUND"
            ? NotFound(failed)
            : BadRequest(failed);
    }

    private static void Log(string message)
    {
        Console.WriteLine($"[HBPOS][Api][Catalog] {DateTimeOffset.Now:O} {message}");
    }
}
