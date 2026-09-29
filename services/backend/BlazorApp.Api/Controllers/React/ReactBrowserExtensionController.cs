using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.LocalSupplierCategories;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BlazorApp.Api.Controllers.React;

[ApiController]
[Authorize]
[Route("api/react/v1/browser-extension")]
public sealed class ReactBrowserExtensionController : ControllerBase
{
    private readonly IBrowserExtensionService _service;
    private readonly IBrowserExtensionAccessService _accessService;
    private readonly ILocalSupplierCategoryCaptureService _categoryCaptureService;
    private readonly ILogger<ReactBrowserExtensionController> _logger;

    public ReactBrowserExtensionController(
        IBrowserExtensionService service,
        IBrowserExtensionAccessService accessService,
        ILocalSupplierCategoryCaptureService categoryCaptureService,
        ILogger<ReactBrowserExtensionController> logger
    )
    {
        _service = service;
        _accessService = accessService;
        _categoryCaptureService = categoryCaptureService;
        _logger = logger;
    }

    [HttpGet("release")]
    public async Task<IActionResult> GetRelease()
    {
        if (!await _accessService.CanAccessAsync(User))
        {
            return Forbid();
        }

        return Ok(ApiResponse<BrowserExtensionReleaseDto>.OK(_service.GetRelease(), "查询成功"));
    }

    [HttpGet("supplier-profiles")]
    public async Task<IActionResult> GetSupplierProfiles()
    {
        if (!await _accessService.CanAccessAsync(User))
        {
            return Forbid();
        }

        return Ok(
            ApiResponse<BrowserExtensionSupplierProfilesDto>.OK(
                BrowserExtensionProfileCatalog.FilterProfilesForClient(
                    _service.GetSupplierProfiles(),
                    Request.Headers[BrowserExtensionProfileCatalog.ExtensionVersionHeader]
                        .FirstOrDefault()
                ),
                "查询成功"
            )
        );
    }

    [HttpGet("stores")]
    public async Task<IActionResult> GetEnabledStores()
    {
        if (!await _accessService.CanAccessAsync(User))
        {
            return Forbid();
        }

        try
        {
            var relatedStoreCodes = await _accessService.GetRelatedStoreCodesAsync(User);
            var data = await _service.GetEnabledStoresAsync(relatedStoreCodes.ToList());
            return Ok(ApiResponse<BrowserExtensionStoreOptionsDto>.OK(data, "查询成功"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "浏览器订货助手启用 POS 门店查询失败");
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResponse<BrowserExtensionStoreOptionsDto>.Error(
                    "启用 POS 门店查询失败。",
                    "QUERY_ERROR"
                )
            );
        }
    }

    [HttpPost("supplier-top-sales")]
    [BrowserExtensionInvalidRequestFilter]
    public async Task<IActionResult> GetSupplierTopSales(
        [FromBody] BrowserExtensionSupplierTopSalesRequestDto request
    )
    {
        if (request == null)
        {
            return BadRequest(
                ApiResponse<BrowserExtensionSupplierTopSalesDto>.Error(
                    "请求参数不能为空。",
                    "INVALID_REQUEST"
                )
            );
        }

        // 用户已确认排行榜是公司级视图，因此这里只校验扩展基础权限，不限定当前下拉门店。
        if (!await _accessService.CanAccessAsync(User))
        {
            return Forbid();
        }

        try
        {
            var data = await _service.GetSupplierTopSalesAsync(request);
            return Ok(ApiResponse<BrowserExtensionSupplierTopSalesDto>.OK(data, "查询成功"));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(
                ApiResponse<BrowserExtensionSupplierTopSalesDto>.Error(
                    ex.Message,
                    "INVALID_REQUEST"
                )
            );
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(
                ApiResponse<BrowserExtensionSupplierTopSalesDto>.Error(ex.Message, "NOT_FOUND")
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "浏览器订货助手供应商热销排行查询失败 SupplierCode={SupplierCode} Days={Days}",
                request.SupplierCode,
                request.Days
            );
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResponse<BrowserExtensionSupplierTopSalesDto>.Error(
                    "供应商热销排行查询失败。",
                    "QUERY_ERROR"
                )
            );
        }
    }

    [HttpPost("supplier-product-store-sales")]
    [Authorize(Policy = Permissions.SalesDashboard.SalesDetailView)]
    [BrowserExtensionInvalidRequestFilter]
    public async Task<IActionResult> GetSupplierProductStoreSales(
        [FromBody] BrowserExtensionSupplierProductStoreSalesRequestDto request
    )
    {
        if (request == null)
        {
            return BadRequest(
                ApiResponse<BrowserExtensionSupplierProductStoreSalesDto>.Error(
                    "请求参数不能为空。",
                    "INVALID_REQUEST"
                )
            );
        }

        // 详情与排行榜使用同一公司级门店范围，不受侧栏当前门店选择限制。
        if (!await _accessService.CanAccessAsync(User))
        {
            return Forbid();
        }

        try
        {
            var data = await _service.GetSupplierProductStoreSalesAsync(request);
            return Ok(
                ApiResponse<BrowserExtensionSupplierProductStoreSalesDto>.OK(data, "查询成功")
            );
        }
        catch (BrowserExtensionRankingSnapshotChangedException ex)
        {
            return Conflict(
                ApiResponse<BrowserExtensionSupplierProductStoreSalesDto>.Error(
                    ex.Message,
                    "RANKING_SNAPSHOT_CHANGED"
                )
            );
        }
        catch (ArgumentException ex)
        {
            return BadRequest(
                ApiResponse<BrowserExtensionSupplierProductStoreSalesDto>.Error(
                    ex.Message,
                    "INVALID_REQUEST"
                )
            );
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(
                ApiResponse<BrowserExtensionSupplierProductStoreSalesDto>.Error(
                    ex.Message,
                    "NOT_FOUND"
                )
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "浏览器订货助手商品分店销量查询失败 SupplierCode={SupplierCode} ProductCode={ProductCode} Days={Days}",
                request.SupplierCode,
                request.ProductCode,
                request.Days
            );
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResponse<BrowserExtensionSupplierProductStoreSalesDto>.Error(
                    "商品分店销量查询失败。",
                    "QUERY_ERROR"
                )
            );
        }
    }

    [HttpPost("product-purchase-cycle-summary/batch")]
    public async Task<IActionResult> GetProductSummaries(
        [FromBody] BrowserExtensionProductSummaryBatchRequestDto request
    )
    {
        if (request == null)
        {
            return BadRequest(
                ApiResponse<BrowserExtensionProductSummaryBatchDto>.Error(
                    "请求参数不能为空。",
                    "INVALID_REQUEST"
                )
            );
        }

        if (!await _accessService.CanAccessAsync(User, request.StoreCode))
        {
            return Forbid();
        }

        try
        {
            var data = await _service.GetProductSummariesAsync(request);
            return Ok(ApiResponse<BrowserExtensionProductSummaryBatchDto>.OK(data, "查询成功"));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(
                ApiResponse<BrowserExtensionProductSummaryBatchDto>.Error(
                    ex.Message,
                    "INVALID_REQUEST"
                )
            );
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(
                ApiResponse<BrowserExtensionProductSummaryBatchDto>.Error(
                    ex.Message,
                    "NOT_FOUND"
                )
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "浏览器订货助手批量摘要查询失败 StoreCode={StoreCode} SupplierCode={SupplierCode}",
                request.StoreCode,
                request.SupplierCode
            );
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResponse<BrowserExtensionProductSummaryBatchDto>.Error(
                    "商品采购摘要查询失败。",
                    "QUERY_ERROR"
                )
            );
        }
    }

    [HttpPost("product-purchase-cycles")]
    public async Task<IActionResult> GetPurchaseCycles(
        [FromBody] BrowserExtensionPurchaseCyclesRequestDto request
    )
    {
        if (request == null)
        {
            return BadRequest(
                ApiResponse<BrowserExtensionPurchaseCyclesDto>.Error(
                    "请求参数不能为空。",
                    "INVALID_REQUEST"
                )
            );
        }

        if (!await _accessService.CanAccessAsync(User, request.StoreCode))
        {
            return Forbid();
        }

        try
        {
            var data = await _service.GetPurchaseCyclesAsync(request);
            return Ok(ApiResponse<BrowserExtensionPurchaseCyclesDto>.OK(data, "查询成功"));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(
                ApiResponse<BrowserExtensionPurchaseCyclesDto>.Error(
                    ex.Message,
                    "INVALID_REQUEST"
                )
            );
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(
                ApiResponse<BrowserExtensionPurchaseCyclesDto>.Error(ex.Message, "NOT_FOUND")
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "浏览器订货助手采购周期查询失败 StoreCode={StoreCode} SupplierCode={SupplierCode}",
                request.StoreCode,
                request.SupplierCode
            );
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResponse<BrowserExtensionPurchaseCyclesDto>.Error(
                    "商品采购周期查询失败。",
                    "QUERY_ERROR"
                )
            );
        }
    }

    /// <summary>
    /// 扩展回传一次分类页采集：分类路径 + 该页货号。写入分类树与观察记录并即时重算商品归属。
    /// </summary>
    [HttpPost("supplier-categories/captures")]
    [EnableRateLimiting(BrowserExtensionCaptureRateLimits.PolicyName)]
    [BrowserExtensionInvalidRequestFilter]
    public async Task<IActionResult> CaptureSupplierCategory(
        [FromBody] BrowserExtensionCategoryCaptureRequestDto request,
        CancellationToken cancellationToken
    )
    {
        if (request == null)
        {
            return BadRequest(
                ApiResponse<BrowserExtensionCategoryCaptureResultDto>.Error(
                    "请求参数不能为空。",
                    LocalSupplierCategoryErrorCodes.InvalidRequest
                )
            );
        }

        if (!await _accessService.CanAccessAsync(User))
        {
            return Forbid();
        }

        return await ExecuteCategoryWriteAsync(
            () => _categoryCaptureService.CaptureAsync(request, User.Identity?.Name, cancellationToken),
            request.SupplierCode,
            "供应商分类采集写入失败。"
        );
    }

    /// <summary>
    /// 主动采集开始时上送供应商网站导航树，让空分类与排序也入库。
    /// </summary>
    [HttpPost("supplier-categories/tree-snapshot")]
    [EnableRateLimiting(BrowserExtensionCaptureRateLimits.PolicyName)]
    [BrowserExtensionInvalidRequestFilter]
    public async Task<IActionResult> SubmitSupplierCategoryTreeSnapshot(
        [FromBody] BrowserExtensionCategoryTreeSnapshotRequestDto request,
        CancellationToken cancellationToken
    )
    {
        if (request == null)
        {
            return BadRequest(
                ApiResponse<BrowserExtensionCategoryTreeSnapshotResultDto>.Error(
                    "请求参数不能为空。",
                    LocalSupplierCategoryErrorCodes.InvalidRequest
                )
            );
        }

        if (!await _accessService.CanAccessAsync(User))
        {
            return Forbid();
        }

        return await ExecuteCategoryWriteAsync(
            () => _categoryCaptureService.ApplyTreeSnapshotAsync(request, User.Identity?.Name, cancellationToken),
            request.SupplierCode,
            "供应商分类树写入失败。"
        );
    }

    /// <summary>
    /// 分类写入的统一错误映射：400 校验失败、404 供应商未启用/功能关闭、409 同供应商写入繁忙（可退避重试）。
    /// </summary>
    private async Task<IActionResult> ExecuteCategoryWriteAsync<T>(
        Func<Task<T>> action,
        string? supplierCode,
        string failureMessage
    )
    {
        try
        {
            var data = await action();
            return Ok(ApiResponse<T>.OK(data, "保存成功"));
        }
        catch (LocalSupplierCategoryValidationException ex)
        {
            return BadRequest(ApiResponse<T>.Error(ex.Message, ex.ErrorCode));
        }
        catch (LocalSupplierCategoryFeatureDisabledException ex)
        {
            return NotFound(ApiResponse<T>.Error(ex.Message, LocalSupplierCategoryErrorCodes.FeatureDisabled));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ApiResponse<T>.Error(ex.Message, "NOT_FOUND"));
        }
        catch (LocalSupplierCategoryBusyException ex)
        {
            return Conflict(ApiResponse<T>.Error(ex.Message, LocalSupplierCategoryErrorCodes.SupplierBusy));
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return StatusCode(499);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "浏览器订货助手供应商分类写入失败 SupplierCode={SupplierCode}", supplierCode);
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                ApiResponse<T>.Error(failureMessage, "QUERY_ERROR")
            );
        }
    }
}
