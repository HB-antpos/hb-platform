using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using AutoMapper;
using BlazorApp.Api.Filters;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// React 前端专用：仅限 Product 与 WarehouseProduct 的商品检测/更新/新建控制器
    /// </summary>
    [ApiController]
    [Route("api/react/v1/product-warehouse")]
    [Authorize]
    public class ReactProductWarehouseController : ControllerBase
    {
        private readonly IProductWarehouseReactService _service;
        private readonly IWarehouseProductHqSyncJobService _hqSyncJobService;
        private readonly IWarehouseProductBatchUpdateJobService _batchUpdateJobService;
        private readonly ILogger<ReactProductWarehouseController> _logger;
        private readonly IDeviceRegistrationService _deviceRegistrationService;
        private readonly IMapper _mapper;
        private readonly TencentCloudUploadService _uploadService;
        private readonly IWarehouseProductChangeHistoryService _changeHistoryService;
        private readonly ICurrentUserService _currentUserService;
        private readonly IProductHqSyncService _productHqSyncService;

        public ReactProductWarehouseController(
            IProductWarehouseReactService service,
            IWarehouseProductHqSyncJobService hqSyncJobService,
            IWarehouseProductBatchUpdateJobService batchUpdateJobService,
            ILogger<ReactProductWarehouseController> logger,
            IDeviceRegistrationService deviceRegistrationService,
            IMapper mapper,
            TencentCloudUploadService uploadService,
            IWarehouseProductChangeHistoryService changeHistoryService,
            ICurrentUserService currentUserService,
            IProductHqSyncService productHqSyncService
        )
        {
            _service = service;
            _hqSyncJobService = hqSyncJobService;
            _batchUpdateJobService = batchUpdateJobService;
            _logger = logger;
            _deviceRegistrationService = deviceRegistrationService;
            _mapper = mapper;
            _uploadService = uploadService;
            _changeHistoryService = changeHistoryService;
            _currentUserService = currentUserService;
            _productHqSyncService = productHqSyncService;
        }

        [HttpGet("mobile/lookup")]
        [AllowAnonymous]
        public async Task<IActionResult> LookupMobile([FromQuery] string keyword)
        {
            var access = await ResolveReadAccessAsync();
            if (!access.IsAllowed)
            {
                return Unauthorized(new { success = false, message = access.Message });
            }

            if (string.IsNullOrWhiteSpace(keyword))
            {
                return BadRequest(new { success = false, message = "查询关键字不能为空" });
            }

            try
            {
                var items = await _service.LookupMobileProductsAsync(keyword);
                return Ok(new { success = true, data = items, message = "查询成功" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "移动端仓库商品查询失败: {Keyword}", keyword);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        [HttpGet("mobile/{productCode}")]
        [AllowAnonymous]
        public async Task<IActionResult> GetMobileProduct(string productCode)
        {
            var access = await ResolveReadAccessAsync();
            if (!access.IsAllowed)
            {
                return Unauthorized(new { success = false, message = access.Message });
            }

            try
            {
                var item = await _service.GetMobileProductAsync(productCode);
                if (item == null)
                {
                    return NotFound(new { success = false, message = "商品不存在" });
                }

                return Ok(new { success = true, data = item, message = "获取成功" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取移动端仓库商品详情失败: {ProductCode}", productCode);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        [HttpPatch("mobile/{productCode}")]
        [Authorize(Roles = "Admin,WarehouseManager,WarehouseStaff")]
        public async Task<IActionResult> PatchMobileProduct(
            string productCode,
            [FromBody] WarehouseMobileProductPatchDto dto
        )
        {
            try
            {
                if (dto == null)
                {
                    return BadRequest(new { success = false, message = "请求参数不能为空" });
                }

                // 供货说明录入有误属于用户可修正的输入问题，返回 400 和具体文案，而不是落到下方的 500。
                if (dto.SupplyNotice != null)
                {
                    var (_, noticeError) =
                        BlazorApp.Api.Features.SupplyNotices.WarehouseProductSupplyNoticeRules.Normalize(
                            dto.SupplyNotice
                        );
                    if (noticeError != null)
                    {
                        return BadRequest(new { success = false, message = noticeError });
                    }
                }

                var item = await _service.PatchMobileProductAsync(productCode, dto, GetCurrentUsername());
                if (item == null)
                {
                    return NotFound(new { success = false, message = "商品不存在" });
                }

                return Ok(new { success = true, data = item, message = "保存成功" });
            }
            catch (Exception ex)
                when (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
            {
                _logger.LogWarning(ex, "更新移动端仓库商品遇到套装成本锁冲突: {ProductCode}", productCode);
                return BuildSetChildPurchasePriceBusy();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新移动端仓库商品失败: {ProductCode}", productCode);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        [HttpPut("mobile/{productCode}/location")]
        [AllowAnonymous]
        public async Task<IActionResult> SetMobileProductLocation(
            string productCode,
            [FromBody] SetWarehouseProductLocationDto dto
        )
        {
            try
            {
                var access = await ResolveWriteAccessAsync();
                if (!access.IsAllowed)
                {
                    Console.WriteLine(
                        $"[ReactProductWarehouse.SetMobileProductLocation] 授权失败 ProductCode={productCode}, Message={access.Message}"
                    );
                    return Unauthorized(new { success = false, message = access.Message });
                }

                if (dto == null)
                {
                    Console.WriteLine(
                        $"[ReactProductWarehouse.SetMobileProductLocation] 请求参数为空 ProductCode={productCode}"
                    );
                    return BadRequest(new { success = false, message = "请求参数不能为空" });
                }

                Console.WriteLine(
                    $"[ReactProductWarehouse.SetMobileProductLocation] 收到绑定请求 ProductCode={productCode}, LocationGuid={dto.LocationGuid}"
                );

                var item = await _service.SetMobileProductLocationAsync(productCode, dto.LocationGuid);
                if (item == null)
                {
                    Console.WriteLine(
                        $"[ReactProductWarehouse.SetMobileProductLocation] 绑定失败：商品不存在 ProductCode={productCode}, LocationGuid={dto.LocationGuid}"
                    );
                    return NotFound(new { success = false, message = "商品不存在" });
                }

                Console.WriteLine(
                    $"[ReactProductWarehouse.SetMobileProductLocation] 绑定成功 ProductCode={productCode}, LocationGuid={dto.LocationGuid}, SavedLocationCode={item.LocationCode}"
                );
                return Ok(new { success = true, data = item, message = "货位更新成功" });
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine(
                    $"[ReactProductWarehouse.SetMobileProductLocation] 绑定失败 ProductCode={productCode}, LocationGuid={dto?.LocationGuid}, Message={ex.Message}"
                );
                _logger.LogWarning(ex, "更新移动端商品货位参数无效: {ProductCode}", productCode);
                return BadRequest(new { success = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[ReactProductWarehouse.SetMobileProductLocation] 绑定异常 ProductCode={productCode}, LocationGuid={dto?.LocationGuid}, Error={ex}"
                );
                _logger.LogError(ex, "更新移动端商品货位失败: {ProductCode}", productCode);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        [HttpPost("mobile/{productCode}/image-upload-signature")]
        [Authorize(Roles = "Admin,WarehouseManager,WarehouseStaff")]
        public IActionResult GetMobileImageUploadSignature(
            string productCode,
            [FromBody] DirectUploadRequest request
        )
        {
            try
            {
                if (request == null || string.IsNullOrWhiteSpace(request.FileName))
                {
                    return BadRequest(new { success = false, message = "文件名不能为空" });
                }

                var objectKey =
                    request.ObjectKey
                    ?? $"warehouse/mobile/{productCode}/{Path.GetFileNameWithoutExtension(request.FileName)}_{DateTime.Now:yyMMddHHmmss}{Path.GetExtension(request.FileName)}";

                var signature = _uploadService.GetDirectUploadSignature(
                    objectKey,
                    request.ContentType,
                    request.FileSize
                );

                return Ok(new { success = true, data = signature, message = "签名生成成功" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "生成仓库商品图片上传签名失败: {ProductCode}", productCode);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        [HttpGet("mobile/{productCode}/print-payload")]
        [AllowAnonymous]
        public async Task<IActionResult> GetMobileProductPrintPayload(
            string productCode,
            [FromQuery] string type = "product"
        )
        {
            var access = await ResolveReadAccessAsync();
            if (!access.IsAllowed)
            {
                return Unauthorized(new { success = false, message = access.Message });
            }

            try
            {
                if (string.Equals(type, "location", StringComparison.OrdinalIgnoreCase))
                {
                    var locationPayload = await _service.GetMobileLocationPrintPayloadAsync(productCode);
                    if (locationPayload == null)
                    {
                        return NotFound(new { success = false, message = "货位标签数据不存在" });
                    }

                    return Ok(new { success = true, data = locationPayload, message = "获取成功" });
                }

                var productPayload = await _service.GetMobileProductPrintPayloadAsync(productCode);
                if (productPayload == null)
                {
                    return NotFound(new { success = false, message = "商品标签数据不存在" });
                }

                return Ok(new { success = true, data = productPayload, message = "获取成功" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取仓库标签打印数据失败: {ProductCode}", productCode);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        [HttpPost("detect")]
        [Authorize(Roles = "Admin,WarehouseManager,User")]
        public async Task<IActionResult> Detect([FromBody] DetectRequest request)
        {
            try
            {
                if (request == null || request.Items == null || !request.Items.Any())
                    return BadRequest(new { success = false, message = "请求数据不能为空" });

                var data = await _service.DetectAsync(request.Items);
                return Ok(
                    new
                    {
                        success = true,
                        data,
                        message = "检测完成",
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检测商品失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        [HttpPost("batch-update")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> BatchUpdate([FromBody] BatchUpdateRequest request)
        {
            try
            {
                if (request == null || request.Items == null || !request.Items.Any())
                    return BadRequest(new { success = false, message = "请求数据不能为空" });

                if (request.SyncStorePurchasePrice.HasValue)
                {
                    // 兼容旧的明细级 DTO，同时允许前端按本次批量动作统一关闭分店进货价同步。
                    foreach (var item in request.Items)
                        item.SyncStorePurchasePrice ??= request.SyncStorePurchasePrice;
                }

                if (request.SyncImageToHq && !request.GenerateImageUrls)
                {
                    return BadRequest(
                        new
                        {
                            success = false,
                            message = "同步 HQ 图片前必须启用图片地址生成",
                        }
                    );
                }

                var options = new WarehouseProductBatchUpdateOptionsDto
                {
                    GenerateImageUrls = request.GenerateImageUrls,
                    ImageBaseUrl = request.ImageBaseUrl,
                    SyncImageToHq = request.SyncImageToHq,
                    SupplyNotice = request.SupplyNotice,
                };
                if (options.GenerateImageUrls)
                {
                    if (
                        !WarehouseProductBatchImageUrlBuilder.TryNormalizeBaseUrl(
                            options.ImageBaseUrl,
                            out var normalizedBaseUrl,
                            out var imageBaseUrlError
                        )
                    )
                    {
                        return BadRequest(
                            new { success = false, message = imageBaseUrlError }
                        );
                    }
                    options.ImageBaseUrl = normalizedBaseUrl;
                }

                var currentUsername = GetCurrentUsername();
                WarehouseProductBatchUpdateResultDto resp;
                // 带供货说明的下架也必须走带选项的重载，否则说明会在旧重载里被丢弃。
                if (options.GenerateImageUrls || options.SupplyNotice != null)
                {
                    resp = await _service.BatchUpdateAsync(
                        request.Items,
                        currentUsername,
                        options
                    );
                }
                else
                {
                    // 旧请求继续走原重载，避免改变其他页面与测试替身的既有契约。
                    var legacyResult = await _service.BatchUpdateAsync(
                        request.Items,
                        currentUsername
                    );
                    resp = new WarehouseProductBatchUpdateResultDto
                    {
                        Success = legacyResult.Success,
                        Message = legacyResult.Message,
                        SuccessCount = legacyResult.SuccessCount,
                        FailedCount = legacyResult.FailedCount,
                        SkippedCount = legacyResult.SkippedCount,
                        Errors = legacyResult.Errors,
                        SkippedItems = legacyResult.SkippedItems,
                    };
                }

                if (options.SyncImageToHq)
                {
                    if (!resp.Success)
                    {
                        resp.HqImageSync = new ProductHqImageSyncResultDto
                        {
                            Requested = true,
                            Success = false,
                            ErrorCode = "HQ_IMAGE_SYNC_LOCAL_UPDATE_FAILED",
                            Errors = new List<string>
                            {
                                "本地图片更新失败，未执行 HQ 图片同步",
                            },
                        };
                    }
                    else if (resp.ImageUpdates.Count == 0)
                    {
                        resp.HqImageSync = new ProductHqImageSyncResultDto
                        {
                            Requested = true,
                            Success = false,
                            ErrorCode = "HQ_IMAGE_SYNC_NO_LOCAL_IMAGES",
                            Errors = new List<string>
                            {
                                "没有本地成功更新的图片可同步至 HQ",
                            },
                        };
                    }
                    else
                    {
                        resp.HqImageSync = await _productHqSyncService.SyncProductImagesAsync(
                            resp.ImageUpdates,
                            currentUsername
                        );
                        if (!resp.HqImageSync.Success)
                        {
                            resp.Message = "本地更新完成，HQ 图片同步存在失败";
                        }
                    }
                }

                return Ok(
                    new
                    {
                        success = resp.Success,
                        message = resp.Message,
                        successCount = resp.SuccessCount,
                        failedCount = resp.FailedCount,
                        errors = resp.Errors,
                        imageUpdatedCount = resp.ImageUpdatedCount,
                        hqImageSync = resp.HqImageSync,
                    }
                );
            }
            catch (Exception ex)
                when (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
            {
                _logger.LogWarning(ex, "批量更新遇到套装成本锁冲突");
                return BuildSetChildPurchasePriceBusy(
                    request?.Items.Select((item, index) =>
                        !string.IsNullOrWhiteSpace(item.ProductCode)
                            ? item.ProductCode!
                            : !string.IsNullOrWhiteSpace(item.ItemNumber)
                                ? item.ItemNumber!
                                : $"#{index + 1}"
                    )
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量更新失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 创建仓库商品批量修改后台任务。
        /// </summary>
        [HttpPost("batch-update/jobs")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> StartBatchUpdateJob(
            [FromBody] BatchUpdateRequest request,
            CancellationToken cancellationToken
        )
        {
            try
            {
                if (request == null || request.Items == null || !request.Items.Any())
                {
                    return BadRequest(new { success = false, message = "请求数据不能为空" });
                }

                var job = await _batchUpdateJobService.StartJobAsync(
                    new WarehouseProductBatchUpdateJobRequestDto
                    {
                        Items = request.Items,
                        SyncStorePurchasePrice = request.SyncStorePurchasePrice,
                        GenerateImageUrls = request.GenerateImageUrls,
                        ImageBaseUrl = request.ImageBaseUrl,
                        SyncImageToHq = request.SyncImageToHq,
                        SupplyNotice = request.SupplyNotice,
                    },
                    GetCurrentUsername(),
                    cancellationToken
                );
                return Ok(
                    new
                    {
                        success = true,
                        data = job,
                        message = job.IsDuplicateRequest
                            ? "相同批量修改任务正在后台执行"
                            : "仓库商品批量修改任务已提交",
                    }
                );
            }
            catch (WarehouseProductBatchUpdateQueueFullException ex)
            {
                return StatusCode(
                    StatusCodes.Status429TooManyRequests,
                    new { success = false, message = ex.Message }
                );
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "提交仓库商品批量修改后台任务失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 查询仓库商品批量修改后台任务。
        /// </summary>
        [HttpGet("batch-update/jobs/{jobId}")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> GetBatchUpdateJob(
            string jobId,
            CancellationToken cancellationToken
        )
        {
            try
            {
                if (string.IsNullOrWhiteSpace(jobId))
                {
                    return BadRequest(new { success = false, message = "jobId 不能为空" });
                }

                var job = await _batchUpdateJobService.GetJobAsync(jobId, cancellationToken);
                if (job == null)
                {
                    return NotFound(
                        new
                        {
                            success = false,
                            message = "批量修改任务不存在、已过期或服务已重启",
                        }
                    );
                }

                return Ok(new { success = true, data = job, message = "查询成功" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询仓库商品批量修改后台任务失败: {JobId}", jobId);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        [HttpPost("batch-create")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> BatchCreate([FromBody] BatchCreateRequest request)
        {
            try
            {
                if (request == null || request.Items == null || !request.Items.Any())
                    return BadRequest(new { success = false, message = "请求数据不能为空" });

                var resp = await _service.BatchCreateAsync(
                    request.Items,
                    true,
                    GetCurrentUsername()
                );
                return Ok(
                    new
                    {
                        success = resp.Success,
                        message = resp.Message,
                        successCount = resp.SuccessCount,
                        failedCount = resp.FailedCount,
                        skippedCount = resp.SkippedCount,
                        errors = resp.Errors,
                        skippedItems = resp.SkippedItems,
                    }
                );
            }
            catch (Exception ex)
                when (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
            {
                _logger.LogWarning(ex, "批量创建遇到套装成本锁冲突");
                return BuildSetChildPurchasePriceBusy(
                    request?.Items.Select((item, index) =>
                        !string.IsNullOrWhiteSpace(item.ProductCode)
                            ? item.ProductCode!
                            : !string.IsNullOrWhiteSpace(item.ItemNumber)
                                ? item.ItemNumber!
                                : $"#{index + 1}"
                    )
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量创建失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        [HttpPost("table")]
        [Authorize(Roles = "Admin,WarehouseManager,User")]
        public async Task<IActionResult> Table([FromBody] ReactTableRequestDto request)
        {
            try
            {
                var data = await _service.GetAntdTableDataAsync(request);
                return Ok(
                    new
                    {
                        success = true,
                        data = data.Items,
                        total = data.Total,
                    }
                );
            }
            catch (Exception ex)
            {
                // 中心日志在 LogError 调用时读取 Response.StatusCode，必须先设置最终状态。
                Response.StatusCode = StatusCodes.Status500InternalServerError;
                var diagnostic = ex as WarehouseProductTableQueryException;
                var requestSnapshot = diagnostic?.Request;
                var timings =
                    diagnostic?.Timings
                    ?? new WarehouseProductTableTimingSnapshot(0, 0, 0, 0, 0, 0, 0);
                _logger.LogError(
                    ex,
                    "表格数据获取失败: stage={Stage} pageNumber={PageNumber} pageSize={PageSize} categoryCount={CategoryCount} filterCount={FilterCount} keywordType={KeywordType} keywordLength={KeywordLength} sortBy={SortBy} sortOrder={SortOrder} candidateMs={CandidateMs} countMs={CountMs} pageMs={PageMs} locationMs={LocationMs} rowsMs={RowsMs} mapMs={MapMs} totalMs={TotalMs}",
                    diagnostic?.FailedStage ?? "unknown",
                    requestSnapshot?.PageNumber ?? request.Page,
                    requestSnapshot?.PageSize ?? request.PageSize,
                    requestSnapshot?.CategoryCount ?? request.CategoryGuids?.Count ?? 0,
                    requestSnapshot?.FilterCount ?? request.Filters?.Count ?? 0,
                    requestSnapshot?.KeywordType
                        ?? (string.IsNullOrWhiteSpace(request.GlobalSearch) ? "none" : "unknown"),
                    requestSnapshot?.KeywordLength ?? request.GlobalSearch?.Trim().Length ?? 0,
                    requestSnapshot?.SortBy ?? "unknown",
                    requestSnapshot?.SortOrder ?? "unknown",
                    timings.CandidateMs,
                    timings.CountMs,
                    timings.PageMs,
                    timings.LocationMs,
                    timings.RowsMs,
                    timings.MapMs,
                    timings.TotalMs
                );
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    new { success = false, message = "服务器内部错误" }
                );
            }
        }

        [HttpPost("create-single")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> CreateSingle(
            [FromBody] CreateSingleProductRequestDto request
        )
        {
            try
            {
                if (request == null)
                    return BadRequest(new { success = false, message = "请求数据不能为空" });

                var resp = await _service.CreateSingleProductAsync(request, GetCurrentUsername());
                return Ok(
                    new
                    {
                        success = resp.Success,
                        message = resp.Message,
                        productCode = resp.ProductCode,
                        itemNumber = resp.ItemNumber,
                        barcode = resp.Barcode,
                        barcodeExists = resp.BarcodeExists,
                        warnings = resp.Warnings,
                    }
                );
            }
            catch (Exception ex)
                when (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
            {
                _logger.LogWarning(ex, "新建单个商品遇到套装成本锁冲突");
                return BuildSetChildPurchasePriceBusy();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "新建单个商品失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        [HttpPost("domestic-not-in-warehouse")]
        [Authorize(Roles = "Admin,WarehouseManager,User")]
        public async Task<IActionResult> DomesticNotInWarehouse(
            [FromBody] GetDomesticProductsNotInWarehouseRequestDto request
        )
        {
            try
            {
                if (request == null)
                    return BadRequest(new { success = false, message = "请求数据不能为空" });

                var data = await _service.GetDomesticProductsNotInWarehouseAsync(request);
                return Ok(
                    new
                    {
                        success = true,
                        data = data.Items,
                        total = data.Total,
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取国内商品不在仓库列表失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        [HttpPost("import-from-domestic")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> ImportFromDomestic(
            [FromBody] ImportFromDomesticRequestDto request
        )
        {
            try
            {
                if (request == null)
                    return BadRequest(new { success = false, message = "请求数据不能为空" });

                var resp = await _service.ImportFromDomesticAsync(request, GetCurrentUsername());
                return Ok(
                    new
                    {
                        success = resp.Success,
                        message = resp.Message,
                        successCount = resp.SuccessCount,
                        failedCount = resp.FailedCount,
                        results = resp.Results,
                    }
                );
            }
            catch (Exception ex)
                when (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
            {
                _logger.LogWarning(ex, "从国内商品导入遇到套装成本锁冲突");
                return BuildSetChildPurchasePriceBusy(request?.ProductCodes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从国内商品导入失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        private async Task<WarehouseReadAccessContext> ResolveReadAccessAsync()
        {
            if (User?.Identity?.IsAuthenticated == true)
            {
                if (HasAnyRole("Admin", "WarehouseManager", "WarehouseStaff"))
                {
                    return new WarehouseReadAccessContext { IsAllowed = true };
                }

                return new WarehouseReadAccessContext { IsAllowed = false, Message = "当前账号没有仓库访问权限" };
            }

            return await ResolveDeviceAccessAsync();
        }

        private async Task<WarehouseReadAccessContext> ResolveWriteAccessAsync()
        {
            if (User?.Identity?.IsAuthenticated == true)
            {
                if (HasAnyRole("Admin", "WarehouseManager", "WarehouseStaff"))
                {
                    return new WarehouseReadAccessContext { IsAllowed = true };
                }

                return new WarehouseReadAccessContext { IsAllowed = false, Message = "当前账号没有仓库绑定权限" };
            }

            return await ResolveDeviceAccessAsync();
        }

        private async Task<WarehouseReadAccessContext> ResolveDeviceAccessAsync()
        {
            var hardwareId = Request.Headers["X-Device-Id"].FirstOrDefault();
            var authCode = Request.Headers["X-Auth-Code"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(hardwareId) || string.IsNullOrWhiteSpace(authCode))
            {
                return new WarehouseReadAccessContext { IsAllowed = false, Message = "未登录且缺少设备授权信息" };
            }

            var isValid = await _deviceRegistrationService.ValidateDeviceAuthCodeAsync(hardwareId, authCode);
            if (!isValid)
            {
                return new WarehouseReadAccessContext { IsAllowed = false, Message = "设备授权无效" };
            }

            var deviceEntity = await _deviceRegistrationService.GetDeviceByHardwareIdAsync(hardwareId);
            if (deviceEntity == null)
            {
                return new WarehouseReadAccessContext { IsAllowed = false, Message = "设备不存在" };
            }

            var device = _mapper.Map<DeviceDataDto>(deviceEntity);
            if (device.Status != 1)
            {
                return new WarehouseReadAccessContext { IsAllowed = false, Message = "设备未启用" };
            }

            return new WarehouseReadAccessContext { IsAllowed = true };
        }

        private bool HasAnyRole(params string[] roles)
        {
            return roles.Any(role =>
                User?.Claims.Any(claim =>
                    claim.Type == ClaimTypes.Role
                    && claim.Value.Equals(role, StringComparison.OrdinalIgnoreCase)
                ) == true
            );
        }

        private sealed class WarehouseReadAccessContext
        {
            public bool IsAllowed { get; set; }
            public string Message { get; set; } = "未授权";
        }

        [HttpPost("non-hb-not-in-warehouse")]
        [Authorize(Roles = "Admin,WarehouseManager,User")]
        public async Task<IActionResult> NonHotbargainNotInWarehouse(
            [FromBody] GetNonHotbargainProductsNotInWarehouseRequestDto request
        )
        {
            try
            {
                if (request == null)
                    return BadRequest(new { success = false, message = "请求数据不能为空" });

                var data = await _service.GetNonHotbargainProductsNotInWarehouseAsync(request);
                return Ok(
                    new
                    {
                        success = true,
                        data = data.Items,
                        total = data.Total,
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取非Hotbargain商品不在仓库列表失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        [HttpPost("import-non-hb")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> ImportNonHotbargain(
            [FromBody] ImportNonHotbargainRequestDto request
        )
        {
            try
            {
                if (request == null)
                    return BadRequest(new { success = false, message = "请求数据不能为空" });

                var resp = await _service.ImportNonHotbargainProductsAsync(
                    request,
                    GetCurrentUsername()
                );
                return Ok(
                    new
                    {
                        success = resp.Success,
                        message = resp.Message,
                        successCount = resp.SuccessCount,
                        failedCount = resp.FailedCount,
                        results = resp.Results,
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导入非Hotbargain商品失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 仓库商品完整更新（六表 + 国内商品联动）
        /// </summary>
        [HttpPut("{productCode}/full-update")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> FullUpdate(
            string productCode,
            [FromBody] WarehouseProductFullUpdateDto dto
        )
        {
            try
            {
                if (string.IsNullOrWhiteSpace(productCode))
                    return BadRequest(new { success = false, message = "商品编码不能为空" });
                if (dto == null)
                    return BadRequest(new { success = false, message = "请求数据不能为空" });

                var resp = await _service.FullUpdateAsync(productCode, dto, GetCurrentUsername());
                if (resp.Success)
                    return Ok(new { success = true, message = resp.Message });
                return BadRequest(new { success = false, message = resp.Message });
            }
            catch (Exception ex)
                when (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
            {
                _logger.LogWarning(ex, "仓库商品完整更新遇到套装成本锁冲突 ProductCode={ProductCode}", productCode);
                return BuildSetChildPurchasePriceBusy();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "仓库商品完整更新失败 ProductCode={ProductCode}", productCode);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 仓库商品窄列 PATCH：一次只允许更新一个非负字段（MinOrderQuantity/DomesticPrice/ImportPrice/OEMPrice）。
        /// </summary>
        [HttpPatch("{productCode}")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> Patch(
            string productCode,
            [FromBody] WarehouseProductPatchDto dto
        )
        {
            try
            {
                if (string.IsNullOrWhiteSpace(productCode))
                    return BadRequest(new { success = false, message = "商品编码不能为空" });
                if (dto == null)
                    return BadRequest(new { success = false, message = "请求数据不能为空" });
                var validationError = WarehouseProductPatchDto.Validate(dto);
                if (validationError != null)
                    return BadRequest(new { success = false, message = validationError });

                var resp = await _service.PatchAsync(productCode, dto, GetCurrentUsername());
                if (resp == null)
                    return NotFound(new { success = false, message = "商品不存在" });
                if (!resp.Success)
                    return BadRequest(new { success = false, message = resp.Message });
                return Ok(new { success = true, message = resp.Message });
            }
            catch (Exception ex)
                when (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
            {
                _logger.LogWarning(ex, "更新仓库商品遇到套装成本锁冲突: {ProductCode}", productCode);
                return BuildSetChildPurchasePriceBusy();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新仓库商品失败: {ProductCode}", productCode);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        [HttpPost("batch-toggle-active")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> BatchToggleActive(
            [FromBody] BatchToggleWarehouseProductsActiveRequestDto request
        )
        {
            try
            {
                if (request == null || request.ProductCodes == null || !request.ProductCodes.Any())
                    return BadRequest(new { success = false, message = "商品编码不能为空" });

                var resp = await _service.BatchToggleActiveAsync(request, GetCurrentUsername());
                if (resp.Success)
                {
                    return Ok(
                        new
                        {
                            success = true,
                            message = resp.Message,
                            successCount = resp.SuccessCount,
                            failedCount = resp.FailedCount,
                            errors = resp.Errors,
                        }
                    );
                }

                return BadRequest(
                    new
                    {
                        success = false,
                        message = resp.Message,
                        successCount = resp.SuccessCount,
                        failedCount = resp.FailedCount,
                        errors = resp.Errors,
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "仓库商品批量上下架失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 从 HQ 全量同步仓库商品库存
        /// 2026-09-29 起停用（HQ → HBweb，会改写 WarehouseProduct），统一返回 410。
        /// </summary>
        [HttpPost("sync-from-hq")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncFromHq()
        {
            try
            {
                // 这里返回统一响应，便于前端沿用现有同步结果处理。
                var result = await _service.SyncFromHqAsync(
                    _currentUserService.GetCurrentUserGuid(),
                    _currentUserService.GetCurrentUsername()
                );
                var message = result.IsSuccess ? "仓库商品同步成功" : "仓库商品同步完成，但存在错误";
                return Ok(ApiResponse<SyncResult>.OK(result, message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从HQ同步仓库商品库存失败");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("仓库商品同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 创建从 HQ 同步仓库商品库存的后台任务
        /// 2026-09-29 起停用（HQ → HBweb），统一返回 410；下方查询任务状态的只读接口保留。
        /// </summary>
        [HttpPost("sync-from-hq/jobs")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> StartSyncFromHqJob(
            [FromBody] WarehouseProductHqSyncJobRequestDto request,
            CancellationToken cancellationToken
        )
        {
            try
            {
                if (request == null)
                {
                    return BadRequest(new { success = false, message = "请求参数不能为空" });
                }

                if (string.IsNullOrWhiteSpace(request.OperationId))
                {
                    return BadRequest(new { success = false, message = "operationId 不能为空" });
                }

                var job = await _hqSyncJobService.StartJobAsync(
                    request,
                    _currentUserService.GetCurrentUserGuid(),
                    _currentUserService.GetCurrentUsername(),
                    cancellationToken
                );
                return Ok(new { success = true, data = job, message = "仓库商品同步任务已提交" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "提交仓库商品 HQ 同步 job 失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 查询从 HQ 同步仓库商品库存的后台任务
        /// </summary>
        [HttpGet("sync-from-hq/jobs/{jobId}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetSyncFromHqJob(
            string jobId,
            CancellationToken cancellationToken
        )
        {
            try
            {
                if (string.IsNullOrWhiteSpace(jobId))
                {
                    return BadRequest(new { success = false, message = "jobId 不能为空" });
                }

                var job = await _hqSyncJobService.GetJobAsync(jobId, cancellationToken);
                if (job == null)
                {
                    return NotFound(new { success = false, message = "同步任务不存在或已过期" });
                }

                return Ok(new { success = true, data = job, message = "查询成功" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询仓库商品 HQ 同步 job 失败: {JobId}", jobId);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取商品条码对应套装价/进货价列表（商品类型≠0 时编辑弹窗用）
        /// </summary>
        [HttpGet("{productCode}/barcode-prices")]
        [Authorize(Roles = "Admin,WarehouseManager,User")]
        public async Task<IActionResult> GetBarcodePrices(string productCode)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(productCode))
                    return BadRequest(new { success = false, message = "商品编码不能为空" });
                var list = await _service.GetBarcodePricesAsync(productCode);
                return Ok(new { success = true, data = list });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取条码价列表失败 ProductCode={ProductCode}", productCode);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 查询仓库商品主档字段修改历史。返回字段保持前端约定的扁平 data 契约。
        /// </summary>
        [HttpGet("{productCode}/change-history")]
        [Authorize(Policy = Permissions.Warehouse.ManageProducts)]
        public async Task<IActionResult> GetChangeHistory(
            string productCode,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 20,
            CancellationToken cancellationToken = default
        )
        {
            if (string.IsNullOrWhiteSpace(productCode))
            {
                return BadRequest(new { success = false, message = "商品编码不能为空" });
            }

            if (pageNumber < 1 || pageSize is < 1 or > 100)
            {
                return BadRequest(new { success = false, message = "分页参数无效" });
            }

            try
            {
                var page = await _changeHistoryService.GetChangeHistoryAsync(
                    productCode,
                    pageNumber,
                    pageSize,
                    cancellationToken
                );
                var data = new
                {
                    productCode = page.ProductSummary.ProductCode,
                    itemNumber = page.ProductSummary.ItemNumber,
                    productName = page.ProductSummary.ProductName,
                    pageNumber = page.PageNumber,
                    pageSize = page.PageSize,
                    total = page.TotalCount,
                    events = page.Events,
                };
                return Ok(new { success = true, data, message = "查询成功" });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询仓库商品修改历史失败: {ProductCode}", productCode);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        private string GetCurrentUsername()
        {
            // 控制器沿用仓库现有惯例传递认证用户名；非 HTTP 调用由服务层回退 System。
            return User.Identity?.Name ?? "System";
        }

        private IActionResult BuildSetChildPurchasePriceBusy(
            IEnumerable<string>? itemKeys = null
        )
        {
            var keys = itemKeys?
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key.Trim())
                .ToList() ?? new List<string>();
            const string message = "套装子项成本正在被其他操作更新，请稍后重试";

            if (keys.Count == 0)
            {
                return Conflict(
                    new
                    {
                        success = false,
                        message,
                        errorCode = SetChildPurchasePriceMutationLock.BusyErrorCode,
                    }
                );
            }

            var failureDetails = keys
                .Select(key => new BatchOperationFailureDto
                {
                    ItemKey = key,
                    Message = message,
                    ErrorCode = SetChildPurchasePriceMutationLock.BusyErrorCode,
                })
                .ToList();
            return Conflict(
                new
                {
                    success = false,
                    message,
                    errorCode = SetChildPurchasePriceMutationLock.BusyErrorCode,
                    data = new
                    {
                        successCount = 0,
                        failedCount = failureDetails.Count,
                        errors = failureDetails.Select(detail => $"{detail.ItemKey}: {detail.Message}"),
                        failureDetails,
                    },
                }
            );
        }

        #region 请求包装类
        public class DetectRequest
        {
            public List<DetectionItemDto> Items { get; set; } = new();
        }

        public class BatchUpdateRequest
        {
            public List<UpdateItemDto> Items { get; set; } = new();

            /// <summary>
            /// 是否同步更新分店进货价；为空时保持旧行为。
            /// </summary>
            public bool? SyncStorePurchasePrice { get; set; }

            /// <summary>
            /// 是否按数据库中的货号批量覆盖商品图片地址。
            /// </summary>
            public bool GenerateImageUrls { get; set; }

            /// <summary>
            /// 图片目录基础地址；启用图片生成时必填。
            /// </summary>
            public string? ImageBaseUrl { get; set; }

            /// <summary>
            /// 本地提交后是否只同步 HQ 的 H商品图片字段。
            /// </summary>
            public bool SyncImageToHq { get; set; }

            /// <summary>
            /// 显式设为下架的商品随请求登记的供货说明；上架或未设状态的行忽略。
            /// 旧客户端不传时保持原状（分店端按“后续计划待确认”展示），必填只在 Web 前端强制。
            /// </summary>
            public WarehouseProductSupplyNoticeInputDto? SupplyNotice { get; set; }
        }

        public class BatchCreateRequest
        {
            public List<CreateItemDto> Items { get; set; } = new();
        }
        #endregion
    }
}
