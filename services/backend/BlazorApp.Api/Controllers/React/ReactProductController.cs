using BlazorApp.Api.Filters;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlazorApp.Api.Utils;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// React Product管理控制器
    /// 提供产品的CRUD操作、分页查询、排序和过滤功能
    /// </summary>
    [ApiController]
    [Route("api/react/v1/products")]
    [Authorize]
    public class ReactProductController : ControllerBase
    {
        private readonly IProductReactService _service;
        private readonly IProductStoreSyncService _productStoreSyncService;
        private readonly IProductHqSyncService _productHqSyncService;
        private readonly IProductPushToHqJobService? _productPushToHqJobService;
        private readonly ICurrentUserManageableStoreScopeService _storeScopeService;
        private readonly IProductSupplierImageBatchUpdateJobService? _supplierImageJobService;
        private readonly IProductStoreSyncJobService? _productStoreSyncJobService;
        private readonly ILogger<ReactProductController> _logger;
        private readonly ICurrentUserService _currentUserService;

        public ReactProductController(
            IProductReactService service,
            IProductStoreSyncService productStoreSyncService,
            IProductHqSyncService productHqSyncService,
            ICurrentUserManageableStoreScopeService storeScopeService,
            ILogger<ReactProductController> logger,
            ICurrentUserService currentUserService,
            IProductSupplierImageBatchUpdateJobService? supplierImageJobService = null,
            IProductStoreSyncJobService? productStoreSyncJobService = null,
            IProductPushToHqJobService? productPushToHqJobService = null
        )
        {
            _service = service;
            _productStoreSyncService = productStoreSyncService;
            _productHqSyncService = productHqSyncService;
            _productPushToHqJobService = productPushToHqJobService;
            _storeScopeService = storeScopeService;
            _supplierImageJobService = supplierImageJobService;
            _productStoreSyncJobService = productStoreSyncJobService;
            _logger = logger;
            _currentUserService = currentUserService;
        }

        /// <summary>
        /// 分页查询商品列表（支持排序和过滤）
        /// </summary>
        /// <param name="query">查询条件</param>
        /// <returns>分页列表</returns>
        [HttpPost("list")]
        [Authorize(Policy = Permissions.PosProducts.View)]
        public async Task<IActionResult> GetPagedList([FromBody] ProductReactFilterDto query)
        {
            try
            {
                var result = await _service.GetPagedListAsync(query, HttpContext.RequestAborted);
                return Ok(
                    new
                    {
                        success = true,
                        data = result.Items,
                        total = result.Total,
                        pageNumber = result.PageNumber,
                        pageSize = result.PageSize,
                    }
                );
            }
            catch (Exception ex) when (ClientAbortDetector.IsClientAbort(ex, HttpContext.RequestAborted))
            {
                // 客户端已断开：不再按 500 记错误；服务端自身超时不满足该条件，仍走下方错误日志。
                return StatusCode(499);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "分页查询商品失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取商品对应的分店价格记录
        /// </summary>
        /// <param name="productCode">商品编码</param>
        /// <returns>分店价格记录列表</returns>
        [HttpGet("{productCode}/store-records")]
        [Authorize(Policy = Permissions.StoreProducts.View)]
        public async Task<IActionResult> GetStoreRecords(string productCode)
        {
            try
            {
                var scope = await _storeScopeService.GetScopeAsync();
                IReadOnlyCollection<string>? accessibleStoreCodes = scope.IsAllowed
                    ? scope.IsAdmin ? null : scope.StoreCodes
                    : Array.Empty<string>();
                var result = await _service.GetStoreRecordsAsync(productCode, accessibleStoreCodes);
                if (result.Success)
                {
                    return Ok(new { success = true, data = result.Data ?? new List<ProductStoreRecordDto>() });
                }

                return BadRequest(new { success = false, message = result.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取商品分店记录失败: {ProductCode}", productCode);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 批量更新商品分店业务字段
        /// </summary>
        /// <param name="productCode">商品编码</param>
        /// <param name="request">批量更新请求</param>
        /// <returns>批量更新结果</returns>
        [HttpPost("{productCode}/store-records/batch-update")]
        [Authorize(Policy = Permissions.StoreProducts.Edit)]
        public async Task<IActionResult> BatchUpdateStoreRecords(
            string productCode,
            [FromBody] BatchUpdateProductStoreRecordsRequest request
        )
        {
            try
            {
                if (request == null)
                {
                    return BadRequest(new { success = false, message = "请求数据不能为空" });
                }

                var scope = await _storeScopeService.GetScopeAsync();
                IReadOnlyCollection<string>? accessibleStoreCodes = scope.IsAllowed
                    ? scope.IsAdmin ? null : scope.StoreCodes
                    : Array.Empty<string>();

                var result = await _service.BatchUpdateStoreRecordsAsync(
                    productCode,
                    request,
                    accessibleStoreCodes
                );

                if (result.Success)
                {
                    return Ok(
                        new
                        {
                            success = true,
                            data = result.Data,
                            successCount = result.Data?.SuccessCount ?? 0,
                            failedCount = result.Data?.FailedCount ?? 0,
                            errors = result.Data?.Errors ?? new List<string>(),
                            message = result.Message,
                        }
                    );
                }

                return BadRequest(new { success = false, message = result.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量更新商品分店记录失败: {ProductCode}", productCode);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取商品详情
        /// </summary>
        /// <param name="productCode">商品编码</param>
        /// <returns>商品详情</returns>
        [HttpGet("{productCode}")]
        [Authorize(Policy = Permissions.PosProducts.View)]
        public async Task<IActionResult> GetById(string productCode)
        {
            try
            {
                var result = await _service.GetByIdAsync(productCode);
                if (result.Success)
                {
                    return Ok(new { success = true, data = result.Data });
                }
                return NotFound(new { success = false, message = result.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取商品详情失败: {ProductCode}", productCode);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 创建商品
        /// </summary>
        /// <param name="dto">创建DTO</param>
        /// <returns>创建结果</returns>
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateProductDto dto)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(new { success = false, message = "请求参数验证失败" });
                }

                var result = await _service.CreateAsync(dto);
                if (result.Success)
                {
                    return Ok(
                        new
                        {
                            success = true,
                            data = result.Data,
                            message = "创建成功",
                        }
                    );
                }
                return BadRequest(new { success = false, message = result.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建商品失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 更新商品
        /// </summary>
        /// <param name="productCode">商品编码</param>
        /// <param name="dto">更新DTO</param>
        /// <returns>更新结果</returns>
        [HttpPut("{productCode}")]
        public async Task<IActionResult> Update(string productCode, [FromBody] UpdateProductDto dto)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(new { success = false, message = "请求参数验证失败" });
                }

                var result = await _service.UpdateAsync(productCode, dto);
                if (result.Success)
                {
                    return Ok(
                        new
                        {
                            success = true,
                            data = result.Data,
                            message = "更新成功",
                        }
                    );
                }
                if (result.ErrorCode == SetChildPurchasePriceMutationLock.BusyErrorCode)
                {
                    return Conflict(result);
                }
                // 带回稳定错误码（如 CATEGORY_SUPPLIER_MISMATCH），前端可据此给出精确提示。
                return BadRequest(new { success = false, message = result.Message, errorCode = result.ErrorCode });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新商品失败: {ProductCode}", productCode);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 删除商品（支持软删除和物理删除）
        /// </summary>
        /// <param name="productCode">商品编码</param>
        /// <param name="mode">删除模式：soft=软删除（默认），hard=物理删除</param>
        /// <returns>删除结果</returns>
        [HttpDelete("{productCode}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Delete(string productCode, [FromQuery] string mode = "soft")
        {
            try
            {
                var isSoftDelete = mode.ToLower() != "hard";
                var result = await _service.DeleteAsync(productCode, isSoftDelete);
                if (result.Success)
                {
                    return Ok(new { success = true, message = result.Message });
                }
                if (result.ErrorCode == SetChildPurchasePriceMutationLock.BusyErrorCode)
                {
                    return Conflict(result);
                }
                return BadRequest(new { success = false, message = result.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除商品失败: {ProductCode}", productCode);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 批量更新商品（使用事务）
        /// </summary>
        /// <param name="request">批量更新请求</param>
        /// <returns>批量操作结果</returns>
        [HttpPost("batch-update")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        public async Task<IActionResult> BatchUpdate([FromBody] BatchUpdateRequest request)
        {
            try
            {
                if (request == null || request.Items == null || !request.Items.Any())
                {
                    return BadRequest(new { success = false, message = "请求数据不能为空" });
                }

                var result = await _service.BatchUpdateAsync(request.Items);
                if (result.Success)
                {
                    return Ok(
                        new
                        {
                            success = true,
                            data = result.Data,
                            successCount = result.Data?.SuccessCount,
                            failedCount = result.Data?.FailedCount,
                            errors = result.Data?.Errors,
                            failureDetails = result.Data?.FailureDetails,
                            message = result.Message,
                        }
                    );
                }
                if (result.ErrorCode == SetChildPurchasePriceMutationLock.BusyErrorCode)
                {
                    return Conflict(result);
                }
                return BadRequest(new { success = false, message = result.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量更新商品失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        [HttpPost("batch-update-supplier-images")]
        [Authorize(Policy = Permissions.PosProducts.Manage)]
        public async Task<IActionResult> BatchUpdateSupplierImages(
            [FromBody] BatchUpdateSupplierImagesRequest request
        )
        {
            try
            {
                if (request == null)
                {
                    return BadRequest(new { success = false, message = "请求参数不能为空" });
                }

                // 兼容入口也统一进入后台任务，避免绕过同供应商互斥。
                var jobService = ResolveSupplierImageJobService();
                var job = await jobService.StartJobAsync(
                    new BatchUpdateSupplierImagesJobRequest
                    {
                        LocalSupplierCode = request.LocalSupplierCode,
                        UrlTemplate = request.UrlTemplate,
                        UpdateHbweb = request.UpdateHbweb,
                        UpdateHq = request.UpdateHq,
                        SaveSupplierImageBaseUrl = request.SaveSupplierImageBaseUrl,
                    },
                    _currentUserService.GetCurrentUserGuid(),
                    _currentUserService.GetCurrentUsername()
                );
                return Ok(new { success = true, data = job, message = "供应商商品图片批量更新任务已提交" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量更新供应商商品图片失败");
                return StatusCode(500, new { success = false, message = "批量更新供应商商品图片失败" });
            }
        }

        [HttpPost("batch-update-supplier-images/job")]
        [Authorize(Policy = Permissions.PosProducts.Manage)]
        public async Task<IActionResult> StartBatchUpdateSupplierImagesJob(
            [FromBody] BatchUpdateSupplierImagesJobRequest request,
            CancellationToken cancellationToken
        )
        {
            try
            {
                if (request == null)
                {
                    return BadRequest(new { success = false, message = "请求参数不能为空" });
                }

                var jobService = ResolveSupplierImageJobService();
                var job = await jobService.StartJobAsync(
                    request,
                    _currentUserService.GetCurrentUserGuid(),
                    _currentUserService.GetCurrentUsername(),
                    cancellationToken
                );
                return Ok(new { success = true, data = job, message = "供应商商品图片批量更新任务已提交" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建供应商商品图片批量更新任务失败");
                return StatusCode(500, new { success = false, message = "创建供应商商品图片批量更新任务失败" });
            }
        }

        [HttpGet("batch-update-supplier-images/job/{jobId}")]
        [Authorize(Policy = Permissions.PosProducts.Manage)]
        public async Task<IActionResult> GetBatchUpdateSupplierImagesJob(
            string jobId,
            CancellationToken cancellationToken
        )
        {
            try
            {
                var jobService = ResolveSupplierImageJobService();
                var job = await jobService.GetJobAsync(jobId, cancellationToken);
                if (job == null)
                {
                    return NotFound(new { success = false, message = "供应商商品图片批量更新任务不存在或已过期" });
                }

                return Ok(new { success = true, data = job });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询供应商商品图片批量更新任务失败: {JobId}", jobId);
                return StatusCode(500, new { success = false, message = "查询供应商商品图片批量更新任务失败" });
            }
        }

        /// <summary>
        /// 批量删除商品（使用事务，支持软删除和物理删除）
        /// </summary>
        /// <param name="request">批量删除请求</param>
        /// <returns>批量操作结果</returns>
        [HttpPost("batch-delete")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> BatchDelete([FromBody] BatchDeleteRequest request)
        {
            try
            {
                if (request == null || request.ProductCodes == null || !request.ProductCodes.Any())
                {
                    return BadRequest(new { success = false, message = "请求数据不能为空" });
                }

                var isSoftDelete = request.Mode?.ToLower() != "hard";
                var result = await _service.BatchDeleteAsync(request.ProductCodes, isSoftDelete);
                if (result.Success)
                {
                    return Ok(
                        new
                        {
                            success = true,
                            data = result.Data,
                            successCount = result.Data?.SuccessCount,
                            failedCount = result.Data?.FailedCount,
                            errors = result.Data?.Errors,
                            failureDetails = result.Data?.FailureDetails,
                            message = result.Message,
                        }
                    );
                }
                if (result.ErrorCode == SetChildPurchasePriceMutationLock.BusyErrorCode)
                {
                    return Conflict(result);
                }
                return BadRequest(new { success = false, message = result.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量删除商品失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        private IProductSupplierImageBatchUpdateJobService ResolveSupplierImageJobService()
        {
            return _supplierImageJobService
                ?? throw new InvalidOperationException("供应商商品图片批量更新任务服务未注册");
        }

        private IProductStoreSyncJobService ResolveProductStoreSyncJobService()
        {
            return _productStoreSyncJobService
                ?? throw new InvalidOperationException("商品同步到分店任务服务未注册");
        }

        private void LogPushToHqBusinessFailure(ApiResponse<PushProductsToHqResult> result)
        {
            // 失败结果历史上既可能放在 Data，也可能放在 Details，这里同时兼容，避免日志统计丢失。
            var failedCount = result.Data?.FailedCount ?? ReadPushToHqIntValue(result.Details, "FailedCount");
            var durationMs = result.Data?.DurationMs ?? ReadPushToHqLongValue(result.Details, "DurationMs");

            // 控制器层只记录请求定位字段和聚合统计，避免把请求明细或商品编码直接打进中心日志。
            _logger.LogWarning(
                "推送商品到HQ业务失败: RequestPath={RequestPath}, TraceId={TraceId}, ErrorCode={ErrorCode}, FailedCount={FailedCount}, DurationMs={DurationMs}, HttpStatusCode={HttpStatusCode}",
                HttpContext?.Request?.Path.Value ?? "unknown",
                HttpContext?.TraceIdentifier ?? string.Empty,
                result.ErrorCode ?? string.Empty,
                failedCount,
                durationMs,
                result.Success ? 200 : 400
            );
        }

        private static int ReadPushToHqIntValue(object? payload, string propertyName)
        {
            var rawValue = payload?.GetType().GetProperty(propertyName)?.GetValue(payload);
            return rawValue switch
            {
                byte value => value,
                short value => value,
                int value => value,
                long value => (int)value,
                _ => 0,
            };
        }

        private static long ReadPushToHqLongValue(object? payload, string propertyName)
        {
            var rawValue = payload?.GetType().GetProperty(propertyName)?.GetValue(payload);
            return rawValue switch
            {
                byte value => value,
                short value => value,
                int value => value,
                long value => value,
                _ => 0,
            };
        }

        /// <summary>
        /// 高级过滤查询商品列表（支持商品信息表与分店价格表的组合过滤）
        /// 所有过滤参数通过 QueryString 传递,支持同时生效
        /// 对货价、零售价使用 BETWEEN 语法,支持闭区间
        /// 分店价格表过滤需先按分店 ID 过滤,再与商品主表 INNER JOIN
        /// 返回 DTO 需包含商品主表全部字段 + 对应分店价格字段
        /// </summary>
        /// <param name="search">搜索关键词（商品名称、货号、条码）,示例: "iPhone"</param>
        /// <param name="localSupplierCode">本地供应商代码过滤,示例: "SUP001"</param>
        /// <param name="isActive">是否启用状态过滤,示例: true</param>
        /// <param name="isSpecialProduct">是否特殊产品过滤,示例: false</param>
        /// <param name="warehouseCategoryGUID">仓库类别GUID过滤,示例: "12345678-1234-1234-1234-123456789012"</param>
        /// <param name="productType">产品类型过滤,示例: 1</param>
        /// <param name="updatedBy">更新人过滤,示例: "admin"</param>
        /// <param name="productPurchasePriceMin">商品主表最低进货价,使用 BETWEEN 语法,示例: 0</param>
        /// <param name="productPurchasePriceMax">商品主表最高进货价,使用 BETWEEN 语法,示例: 999999.99</param>
        /// <param name="productRetailPriceMin">商品主表最低零售价,使用 BETWEEN 语法,示例: 0</param>
        /// <param name="productRetailPriceMax">商品主表最高零售价,使用 BETWEEN 语法,示例: 999999.99</param>
        /// <param name="storeCodes">分店代码数组过滤,与商品主表执行 INNER JOIN,支持多分店,示例: STORE001,STORE002</param>
        /// <param name="storePurchasePriceMin">分店价格表最低进货价,使用 BETWEEN 语法,示例: 0</param>
        /// <param name="storePurchasePriceMax">分店价格表最高进货价,使用 BETWEEN 语法,示例: 999999.99</param>
        /// <param name="storeRetailPriceMin">分店价格表最低零售价,使用 BETWEEN 语法,示例: 0</param>
        /// <param name="storeRetailPriceMax">分店价格表最高零售价,使用 BETWEEN 语法,示例: 999999.99</param>
        /// <param name="storeDiscountRateMin">分店价格最低折扣率,使用 BETWEEN 语法,示例: 0</param>
        /// <param name="storeDiscountRateMax">分店价格最高折扣率,使用 BETWEEN 语法,示例: 1</param>
        /// <param name="storeIsActive">分店价格是否启用过滤,示例: true</param>
        /// <param name="storeIsAutoPricing">分店价格是否自动定价过滤,示例: false</param>
        /// <param name="pageNumber">页码（从1开始）,示例: 1</param>
        /// <param name="pageSize">每页大小,支持值: 20,50,100,200,500,1000,示例: 50</param>
        /// <param name="sortBy">排序字段,支持: ProductCode,ProductName,ProductPurchasePrice,ProductRetailPrice,StorePurchasePrice,StoreRetailPrice,StoreDiscountRate,CreatedAt,UpdatedAt,示例: "StoreRetailPrice"</param>
        /// <param name="sortOrder">排序方向: asc 或 desc,示例: "asc"</param>
        /// <returns>分页列表,包含商品主表全部字段 + 对应分店价格字段</returns>
        [HttpGet("price-filter")]
        public async Task<IActionResult> GetPriceFilteredProducts(
            [FromQuery] string? search = null,
            [FromQuery] string? localSupplierCode = null,
            [FromQuery] bool? isActive = null,
            [FromQuery] bool? isSpecialProduct = null,
            [FromQuery] string? warehouseCategoryGUID = null,
            [FromQuery] int? productType = null,
            [FromQuery] string? updatedBy = null,
            [FromQuery] decimal? productPurchasePriceMin = null,
            [FromQuery] decimal? productPurchasePriceMax = null,
            [FromQuery] decimal? productRetailPriceMin = null,
            [FromQuery] decimal? productRetailPriceMax = null,
            [FromQuery] string storeCodes = "",
            [FromQuery] decimal? storePurchasePriceMin = null,
            [FromQuery] decimal? storePurchasePriceMax = null,
            [FromQuery] decimal? storeRetailPriceMin = null,
            [FromQuery] decimal? storeRetailPriceMax = null,
            [FromQuery] decimal? storeDiscountRateMin = null,
            [FromQuery] decimal? storeDiscountRateMax = null,
            [FromQuery] bool? storeIsActive = null,
            [FromQuery] bool? storeIsAutoPricing = null,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 50,
            [FromQuery] string? sortBy = null,
            [FromQuery] string sortOrder = "asc"
        )
        {
            try
            {
                if (string.IsNullOrWhiteSpace(storeCodes))
                {
                    return BadRequest(
                        new
                        {
                            success = false,
                            message = "分店代码数组不能为空,至少需要提供一个分店代码",
                        }
                    );
                }

                var filter = new ProductPriceFilterDto
                {
                    Search = search,
                    LocalSupplierCode = localSupplierCode,
                    IsActive = isActive,
                    IsSpecialProduct = isSpecialProduct,
                    WarehouseCategoryGUID = warehouseCategoryGUID,
                    ProductType = productType,
                    UpdatedBy = updatedBy,
                    ProductPurchasePriceMin = productPurchasePriceMin,
                    ProductPurchasePriceMax = productPurchasePriceMax,
                    ProductRetailPriceMin = productRetailPriceMin,
                    ProductRetailPriceMax = productRetailPriceMax,
                    StoreCodes = storeCodes
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .ToList(),
                    StorePurchasePriceMin = storePurchasePriceMin,
                    StorePurchasePriceMax = storePurchasePriceMax,
                    StoreRetailPriceMin = storeRetailPriceMin,
                    StoreRetailPriceMax = storeRetailPriceMax,
                    StoreDiscountRateMin = storeDiscountRateMin,
                    StoreDiscountRateMax = storeDiscountRateMax,
                    StoreIsActive = storeIsActive,
                    StoreIsAutoPricing = storeIsAutoPricing,
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                    SortBy = sortBy,
                    SortOrder = sortOrder,
                };

                if (!ModelState.IsValid)
                {
                    return BadRequest(
                        new
                        {
                            success = false,
                            message = "请求参数验证失败",
                            errors = ModelState,
                        }
                    );
                }

                var result = await _service.GetPriceFilteredPagedListAsync(filter);
                return Ok(
                    new
                    {
                        success = true,
                        data = result.Items,
                        total = result.Total,
                        pageNumber = result.PageNumber,
                        pageSize = result.PageSize,
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "高级过滤查询商品失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        #region 请求包装类

        /// <summary>
        /// 批量更新请求
        /// </summary>
        public class BatchUpdateRequest
        {
            public List<BatchUpdateProductReactDto> Items { get; set; } = new();
        }

        /// <summary>
        /// 批量删除请求
        /// </summary>
        public class BatchDeleteRequest
        {
            public List<string> ProductCodes { get; set; } = new();
            /// <summary>
            /// 删除模式：soft=软删除（默认），hard=物理删除
            /// </summary>
            public string? Mode { get; set; } = "soft";
        }

        #endregion

        /// <summary>
        /// 从HQ增量同步商品到本地（先商品主表，再全局一品多码）
        /// 2026-09-29 起停用（HQ → HBweb），统一返回 410。
        /// </summary>
        [HttpPost("sync-from-hq")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncFromHq()
        {
            try
            {
                _logger.LogInformation("收到从HQ同步商品的请求");
                var result = await _productHqSyncService.SyncIncrementalAsync(
                    null,
                    _currentUserService.GetCurrentUserGuid(),
                    _currentUserService.GetCurrentUsername()
                );
                if (result.Success)
                {
                    return Ok(new
                    {
                        success = true,
                        data = result.Data,
                        message = result.Message
                    });
                }
                if (result.ErrorCode == SetChildPurchasePriceMutationLock.BusyErrorCode)
                {
                    return Conflict(result);
                }
                return BadRequest(new { success = false, message = result.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从HQ同步商品失败");
                return StatusCode(500, new { success = false, message = "从HQ同步商品失败：" + ex.Message });
            }
        }

        /// <summary>
        /// 从HQ按选中商品同步到本地，只处理请求中的商品范围。
        /// 2026-09-29 起停用（HQ → HBweb），统一返回 410。
        /// </summary>
        [HttpPost("sync-selected-from-hq")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncSelectedFromHq(
            [FromBody] SyncSelectedProductsFromHqRequest request
        )
        {
            try
            {
                if (request?.ProductCodes == null || !request.ProductCodes.Any())
                {
                    return BadRequest(new { success = false, message = "请选择要同步的商品" });
                }

                var productCodes = request.ProductCodes
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (!productCodes.Any())
                {
                    return BadRequest(new { success = false, message = "请选择有效的商品编码" });
                }

                _logger.LogInformation("从HQ同步选中商品: {Count} 件", productCodes.Count);
                var result = await _productHqSyncService.SyncSelectedFromHqAsync(productCodes);
                if (result.Success)
                {
                    return Ok(result);
                }
                return result.ErrorCode == SetChildPurchasePriceMutationLock.BusyErrorCode
                    ? Conflict(result)
                    : BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "从HQ同步选中商品失败");
                return StatusCode(
                    500,
                    ApiResponse<HqProductSyncResult>.Error(
                        $"从HQ同步选中商品失败: {ex.Message}",
                        "PRODUCT_HQ_SELECTED_SYNC_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 将选中的 POS 商品推送到 HQ，只执行 upsert，不删除 HQ 旧记录。
        /// </summary>
        [HttpPost("push-to-hq")]
        [Authorize(Policy = Permissions.PosProducts.Manage)]
        public async Task<IActionResult> PushToHq([FromBody] PushProductsToHqRequest request)
        {
            try
            {
                var productCodes = request?.ProductCodes ?? new List<string>();
                var items = request?.Items ?? new List<PushProductsToHqItem>();
                if (
                    request == null
                    || (!productCodes.Any() && !items.Any())
                )
                {
                    return BadRequest(new { success = false, message = "请选择要推送的商品" });
                }

                var hasLegacyCodes = productCodes.Any(code => !string.IsNullOrWhiteSpace(code));
                var hasItems = items
                    .Any(item =>
                        item != null
                        && (
                            !string.IsNullOrWhiteSpace(item.ProductCode)
                            || !string.IsNullOrWhiteSpace(item.LocalSupplierCode)
                            || !string.IsNullOrWhiteSpace(item.ItemNumber)
                        )
                    );
                if (!hasLegacyCodes && !hasItems)
                {
                    return BadRequest(new { success = false, message = "请选择有效的推送商品" });
                }

                _logger.LogInformation(
                    "推送商品到HQ: legacyCodes={CodeCount}, items={ItemCount}",
                    productCodes.Count,
                    items.Count
                );
                var result = await _productHqSyncService.PushToHqAsync(request);
                if (!result.Success)
                {
                    LogPushToHqBusinessFailure(result);
                }

                if (result.Success)
                {
                    return Ok(result);
                }
                return result.ErrorCode == SetChildPurchasePriceMutationLock.BusyErrorCode
                        && (result.Data?.SuccessCount ?? 0) == 0
                    ? Conflict(result)
                    : BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "推送商品到HQ失败");
                return StatusCode(
                    500,
                    ApiResponse<PushProductsToHqResult>.Error(
                        $"推送商品到HQ失败: {ex.Message}",
                        "PRODUCT_HQ_PUSH_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 加载商品推送到 HQ 的分店选项，直接来自 HQ 分店表。
        /// </summary>
        [HttpGet("push-to-hq/store-options")]
        [Authorize(Policy = Permissions.PosProducts.Manage)]
        public async Task<IActionResult> GetPushToHqStoreOptions()
        {
            try
            {
                var result = await _productHqSyncService.GetHqStoreOptionsAsync();
                return Ok(ApiResponse<List<ProductHqStoreOptionDto>>.OK(result));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载商品推送HQ分店选项失败");
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ApiResponse<List<ProductHqStoreOptionDto>>.Error("加载商品推送HQ分店选项失败")
                );
            }
        }

        /// <summary>
        /// 创建商品推送到 HQ 的后台任务，HTTP 请求立即返回。
        /// </summary>
        [HttpPost("push-to-hq/jobs")]
        [Authorize(Policy = Permissions.PosProducts.Manage)]
        public async Task<IActionResult> StartPushToHqJob(
            [FromBody] PushProductsToHqRequest request,
            CancellationToken cancellationToken
        )
        {
            try
            {
                var validationError = ValidatePushToHqRequest(request);
                if (validationError != null)
                {
                    return validationError;
                }

                if (_productPushToHqJobService == null)
                {
                    return StatusCode(500, new { success = false, message = "商品推送 HQ 后台服务未启用" });
                }

                var job = await _productPushToHqJobService.StartJobAsync(request, cancellationToken);
                return Ok(new { success = true, data = job, message = "商品推送 HQ 任务已提交" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "提交商品推送 HQ job 失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 查询商品推送到 HQ 的后台任务。
        /// </summary>
        [HttpGet("push-to-hq/jobs/{jobId}")]
        [Authorize(Policy = Permissions.PosProducts.Manage)]
        public async Task<IActionResult> GetPushToHqJob(
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

                if (_productPushToHqJobService == null)
                {
                    return StatusCode(500, new { success = false, message = "商品推送 HQ 后台服务未启用" });
                }

                var job = await _productPushToHqJobService.GetJobAsync(jobId, cancellationToken);
                if (job == null)
                {
                    return NotFound(new { success = false, message = "推送任务不存在或已过期" });
                }

                return Ok(new { success = true, data = job, message = "查询成功" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询商品推送 HQ job 失败: {JobId}", jobId);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        private IActionResult? ValidatePushToHqRequest(PushProductsToHqRequest? request)
        {
            var productCodes = request?.ProductCodes ?? new List<string>();
            var items = request?.Items ?? new List<PushProductsToHqItem>();
            if (request == null || (!productCodes.Any() && !items.Any()))
            {
                return BadRequest(new { success = false, message = "请选择要推送的商品" });
            }

            var hasLegacyCodes = productCodes.Any(code => !string.IsNullOrWhiteSpace(code));
            var hasItems = items.Any(item =>
                item != null
                && (
                    !string.IsNullOrWhiteSpace(item.ProductCode)
                    || !string.IsNullOrWhiteSpace(item.LocalSupplierCode)
                    || !string.IsNullOrWhiteSpace(item.ItemNumber)
                )
            );
            return !hasLegacyCodes && !hasItems
                ? BadRequest(new { success = false, message = "请选择有效的推送商品" })
                : null;
        }

        /// <summary>
        /// 同步商品到分店
        /// </summary>
        [HttpPost("sync-to-stores")]
        [Authorize(Policy = Permissions.PosProducts.Manage)]
        public async Task<IActionResult> SyncProductsToStores(
            [FromBody] SyncProductsToStoresRequest request
        )
        {
            try
            {
                if (request == null)
                {
                    return BadRequest(new { success = false, message = "请求参数不能为空" });
                }

                var forbidden = await ValidateAndNormalizeSyncTargetStoresAsync(request);
                if (forbidden != null)
                {
                    return forbidden;
                }

                _logger.LogInformation(
                    "同步商品到分店: {ProductCount} 个商品, {StoreCount} 个分店",
                    request.ProductCodes?.Count ?? 0,
                    request.StoreCodes?.Count ?? 0
                );

                var result = await _productStoreSyncService.SyncProductsToStoresAsync(request);

                if (result.Success)
                {
                    return Ok(
                        new
                        {
                            success = true,
                            data = result.Data,
                            message = result.Message,
                            failureDetails = result.Details,
                        }
                    );
                }
                else
                {
                    var body = new
                    {
                        success = false,
                        message = result.Message,
                        errorCode = result.ErrorCode,
                        data = result.Data,
                        failureDetails = result.Details,
                    };
                    return result.ErrorCode == SetChildPurchasePriceMutationLock.BusyErrorCode
                        ? Conflict(body)
                        : BadRequest(body);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "同步商品到分店失败");
                return StatusCode(500, new { success = false, message = "同步商品到分店失败" });
            }
        }

        /// <summary>
        /// 创建商品同步到分店后台任务
        /// </summary>
        [HttpPost("sync-to-stores/jobs")]
        [Authorize(Policy = Permissions.PosProducts.Manage)]
        public async Task<IActionResult> StartSyncProductsToStoresJob(
            [FromBody] SyncProductsToStoresRequest request,
            CancellationToken cancellationToken
        )
        {
            try
            {
                if (request == null)
                {
                    return BadRequest(new { success = false, message = "请求参数不能为空" });
                }

                var forbidden = await ValidateAndNormalizeSyncTargetStoresAsync(request);
                if (forbidden != null)
                {
                    return forbidden;
                }

                var jobService = ResolveProductStoreSyncJobService();
                var job = await jobService.StartJobAsync(request, cancellationToken);
                return Ok(new { success = true, data = job, message = "商品同步到分店任务已提交" });
            }
            catch (ProductStoreSyncJobConcurrencyLimitExceededException ex)
            {
                _logger.LogWarning(ex, "创建商品同步到分店任务被限流");
                return StatusCode(
                    429,
                    new
                    {
                        success = false,
                        message = ex.Message,
                        errorCode = ex.ErrorCode,
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建商品同步到分店任务失败");
                return StatusCode(500, new { success = false, message = "创建商品同步到分店任务失败" });
            }
        }

        /// <summary>
        /// 查询商品同步到分店后台任务
        /// </summary>
        [HttpGet("sync-to-stores/jobs/{jobId}")]
        [Authorize(Policy = Permissions.PosProducts.Manage)]
        public async Task<IActionResult> GetSyncProductsToStoresJob(
            string jobId,
            CancellationToken cancellationToken
        )
        {
            try
            {
                var jobService = ResolveProductStoreSyncJobService();
                var job = await jobService.GetJobAsync(jobId, cancellationToken);
                if (job == null)
                {
                    return NotFound(new { success = false, message = "商品同步到分店任务不存在或已过期" });
                }

                return Ok(new { success = true, data = job });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询商品同步到分店任务失败: {JobId}", jobId);
                return StatusCode(500, new { success = false, message = "查询商品同步到分店任务失败" });
            }
        }

        private async Task<IActionResult?> ValidateAndNormalizeSyncTargetStoresAsync(
            SyncProductsToStoresRequest request
        )
        {
            var scope = await _storeScopeService.GetScopeAsync();
            if (!scope.IsAllowed)
            {
                return Forbid();
            }

            // 关键位置：控制器统一收口目标分店，避免 job/兼容入口绕过可管理分店范围。
            var normalizedStoreCodes = NormalizeStoreCodes(request.StoreCodes);
            request.StoreCodes = normalizedStoreCodes;

            if (scope.IsAdmin || normalizedStoreCodes.Count == 0)
            {
                return null;
            }

            var allowedStoreCodes = NormalizeStoreCodes(scope.StoreCodes).ToHashSet(
                StringComparer.OrdinalIgnoreCase
            );
            return normalizedStoreCodes.All(allowedStoreCodes.Contains) ? null : Forbid();
        }

        private static List<string> NormalizeStoreCodes(IEnumerable<string>? storeCodes)
        {
            var normalized = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var storeCode in storeCodes ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(storeCode))
                {
                    continue;
                }

                var trimmed = storeCode.Trim();
                if (seen.Add(trimmed))
                {
                    normalized.Add(trimmed);
                }
            }

            return normalized;
        }
    }
}
