using BlazorApp.Api.Filters;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// React 国内商品管理控制器
    /// 专门为React前端提供的API接口
    /// </summary>
    [ApiController]
    [Route("api/react/v1/domestic-products")]
    [Authorize]
    public class ReactDomesticProductsController : ControllerBase
    {
        private readonly IDomesticProductReactService _domesticProductReactService;
        private readonly IDomesticProductService _domesticProductService;
        private readonly ILogger<ReactDomesticProductsController> _logger;

        public ReactDomesticProductsController(
            IDomesticProductReactService domesticProductReactService,
            IDomesticProductService domesticProductService,
            ILogger<ReactDomesticProductsController> logger
        )
        {
            _domesticProductReactService = domesticProductReactService;
            _domesticProductService = domesticProductService;
            _logger = logger;
        }

        /// <summary>
        /// 获取国内商品数据（react-data-grid 服务端模式）
        /// </summary>
        /// <param name="request">Grid 请求</param>
        /// <returns>国内商品数据</returns>
        [HttpPost("grid")]
        [Authorize(Policy = Permissions.Products.View)]
        public async Task<IActionResult> GetGridData([FromBody] GridRequestDto request)
        {
            try
            {
                _logger.LogInformation(
                    "Grid 请求: StartRow={StartRow}, EndRow={EndRow}, PageSize={PageSize}",
                    request.StartRow,
                    request.EndRow,
                    request.PageSize
                );

                var result = await _domesticProductReactService.GetGridDataAsync(request);

                if (result.Success)
                {
                    return Ok(
                        new
                        {
                            success = true,
                            data = new { items = result.Items, total = result.Total },
                            message = result.Message,
                        }
                    );
                }

                return BadRequest(new { success = false, message = result.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Grid 获取数据失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 创建单个国内商品（React专用）
        /// </summary>
        /// <param name="dto">创建国内商品DTO</param>
        /// <returns>创建的国内商品</returns>
        [HttpPost]
        [Authorize(Policy = Permissions.Products.Create)]
        public async Task<IActionResult> CreateDomesticProduct(
            [FromBody] CreateDomesticProductDto dto
        )
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(
                        ApiResponse<object>.Error(
                            "请求参数验证失败",
                            "VALIDATION_ERROR",
                            ModelState
                        )
                    );
                }

                // 单条创建统一复用国内商品服务，避免在 React 控制器复制业务规则。
                var result = await _domesticProductService.CreateDomesticProductAsync(dto);

                if (result.Success)
                {
                    return StatusCode(StatusCodes.Status201Created, result);
                }

                if (
                    result.ErrorCode == "HB_PRODUCT_NO_EXISTS"
                    || result.ErrorCode == "BARCODE_EXISTS"
                )
                {
                    return Conflict(result);
                }

                return BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建国内商品失败");
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ApiResponse<object>.Error("服务器内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 批量创建国内商品（React专用）
        /// 创建成功后会自动记录到创建日志表
        /// </summary>
        /// <param name="dto">批量创建DTO</param>
        /// <returns>创建结果</returns>
        [HttpPost("batch-create")]
        [Authorize(Policy = Permissions.DomesticPurchase.ManageProducts)]
        public async Task<IActionResult> BatchCreateProducts(
            [FromBody] BatchCreateDomesticProductDto dto
        )
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    var errors = ModelState.Values.SelectMany(v =>
                        v.Errors.Select(e => e.ErrorMessage)
                    );
                    return BadRequest(
                        new
                        {
                            success = false,
                            message = $"输入验证失败: {string.Join(", ", errors)}",
                        }
                    );
                }

                _logger.LogInformation(
                    "批量创建商品: SupplierCode={SupplierCode}, Count={Count}",
                    dto.SupplierCode,
                    dto.Products?.Count ?? 0
                );

                var result = await _domesticProductReactService.BatchCreateDomesticProductsAsync(
                    dto
                );

                if (result.Success)
                {
                    var createdProducts = result.Data ?? new List<DomesticProductDto>();
                    return Ok(
                        new
                        {
                            success = true,
                            data = new
                            {
                                createdProducts = createdProducts,
                                failedProducts = new List<object>(),
                                successCount = createdProducts.Count,
                                failureCount = 0,
                                errors = new List<string>(),
                            },
                            message = $"批量创建完成：成功{createdProducts.Count}条",
                        }
                    );
                }

                return BadRequest(
                    new
                    {
                        success = false,
                        message = result.Message,
                        errorCode = result.ErrorCode,
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量创建国内商品失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 批量验证商品数据
        /// 在实际创建之前，验证数据的有效性
        /// </summary>
        /// <param name="dto">批量创建DTO</param>
        /// <returns>验证结果</returns>
        [HttpPost("batch-validate")]
        [Authorize(Policy = Permissions.DomesticPurchase.ManageProducts)]
        public async Task<IActionResult> BatchValidateProducts(
            [FromBody] BatchCreateDomesticProductDto dto
        )
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    var errors = ModelState.Values.SelectMany(v =>
                        v.Errors.Select(e => e.ErrorMessage)
                    );
                    return BadRequest(
                        new
                        {
                            success = false,
                            message = $"输入验证失败: {string.Join(", ", errors)}",
                        }
                    );
                }

                _logger.LogInformation(
                    "批量验证商品: SupplierCode={SupplierCode}, Count={Count}",
                    dto.SupplierCode,
                    dto.Products?.Count ?? 0
                );

                var result = await _domesticProductReactService.BatchValidateProductsAsync(dto);

                if (result.Success && result.Data != null)
                {
                    return Ok(
                        new
                        {
                            success = true,
                            data = result.Data,
                            message = "验证完成",
                        }
                    );
                }

                return BadRequest(
                    new
                    {
                        success = false,
                        message = result.Message,
                        errorCode = result.ErrorCode,
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量验证商品失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 批量检测商品信息 - 通过货号和供应商编码匹配现有数据
        /// 用于导入前检测哪些商品已存在，哪些是新商品
        /// </summary>
        /// <param name="dto">批量检测DTO</param>
        /// <returns>检测结果，包含匹配的商品信息和差异字段</returns>
        [HttpPost("batch-detect")]
        [Authorize(Policy = Permissions.DomesticPurchase.ManageProducts)]
        public async Task<IActionResult> BatchDetectProducts(
            [FromBody] BatchProductDetectionDto dto
        )
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    var errors = ModelState.Values.SelectMany(v =>
                        v.Errors.Select(e => e.ErrorMessage)
                    );
                    return BadRequest(
                        new
                        {
                            success = false,
                            message = $"输入验证失败: {string.Join(", ", errors)}",
                        }
                    );
                }

                _logger.LogInformation(
                    "批量检测商品: SupplierCode={SupplierCode}, Count={Count}",
                    dto.SupplierCode,
                    dto.Products?.Count ?? 0
                );

                var result = await _domesticProductReactService.BatchDetectProductsAsync(dto);

                if (result.Success)
                {
                    return Ok(
                        new
                        {
                            success = true,
                            data = result.Data,
                            message = "检测完成",
                        }
                    );
                }

                return BadRequest(
                    new
                    {
                        success = false,
                        message = result.Message,
                        errorCode = result.ErrorCode,
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量检测商品失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 商品导入确认（React专用）：前端先调用 batch-detect 查看差异并确认后，提交明确的新建与更新列表进行保存
        /// </summary>
        /// <param name="dto">批量操作DTO（包含 NewProducts 与 UpdateProducts）</param>
        /// <returns>导入（创建/更新）结果</returns>
        [HttpPost("batch-import/confirm")]
        [Authorize(Policy = Permissions.DomesticPurchase.ManageProducts)]
        public async Task<IActionResult> BatchImportConfirm([FromBody] BatchProductOperationDto dto)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    var errors = ModelState.SelectMany(x =>
                        x.Value?.Errors?.Select(e => e.ErrorMessage)
                        ?? System.Linq.Enumerable.Empty<string>()
                    );
                    return BadRequest(
                        new
                        {
                            success = false,
                            message = $"输入验证失败: {string.Join(", ", errors)}",
                        }
                    );
                }

                _logger.LogInformation(
                    "商品导入确认: SupplierCode={SupplierCode}, NewCount={NewCount}, UpdateCount={UpdateCount}",
                    dto.SupplierCode,
                    dto.NewProducts?.Count ?? 0,
                    dto.UpdateProducts?.Count ?? 0
                );

                var result = await _domesticProductReactService.BatchCreateAndUpdateProductsAsync(
                    dto
                );

                if (result.Success)
                {
                    var data =
                        result.Data
                        ?? new BatchProductOperationResultDto
                        {
                            CreatedProducts = new List<DomesticProductDto>(),
                            UpdatedProducts = new List<DomesticProductDto>(),
                            Errors = new List<string>(),
                        };

                    return Ok(
                        new
                        {
                            success = true,
                            data = new
                            {
                                createdProducts = data.CreatedProducts,
                                updatedProducts = data.UpdatedProducts,
                                updatedChanges = data.UpdatedChanges,
                                successCount = (data.CreatedProducts?.Count ?? 0)
                                    + (data.UpdatedProducts?.Count ?? 0),
                                failureCount = data.Errors?.Count ?? 0,
                                errors = data.Errors ?? new List<string>(),
                            },
                            message = $"导入完成：新建{(data.CreatedProducts?.Count ?? 0)}条，更新{(data.UpdatedProducts?.Count ?? 0)}条，失败{(data.Errors?.Count ?? 0)}条",
                        }
                    );
                }

                return BadRequest(
                    new
                    {
                        success = false,
                        message = result.Message,
                        errorCode = result.ErrorCode,
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "商品导入确认失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 更新商品信息（React专用）
        /// </summary>
        /// <param name="productCode">商品编码</param>
        /// <param name="dto">更新DTO</param>
        /// <returns>更新结果</returns>
        [HttpPut("{productCode}")]
        [Authorize(Policy = Permissions.Products.Edit)]
        public async Task<IActionResult> UpdateProduct(
            string productCode,
            [FromBody] UpdateDomesticProductDto dto
        )
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return BadRequest(new { success = false, message = "请求参数验证失败" });
                }

                var result = await _domesticProductReactService.UpdateDomesticProductAsync(
                    productCode,
                    dto
                );

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

                return BadRequest(new { success = false, message = result.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新商品失败: {ProductCode}", productCode);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 批量更新商品（React专用）
        /// </summary>
        /// <param name="dto">批量更新DTO</param>
        /// <returns>更新结果</returns>
        [HttpPut("batch-update")]
        [Authorize(Policy = Permissions.DomesticPurchase.ManageProducts)]
        public async Task<IActionResult> BatchUpdate([FromBody] BatchUpdateDomesticProductsDto dto)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    var errors = ModelState.Values.SelectMany(v =>
                        v.Errors.Select(e => e.ErrorMessage)
                    );
                    return BadRequest(
                        new
                        {
                            success = false,
                            message = $"输入验证失败: {string.Join(", ", errors)}",
                        }
                    );
                }

                _logger.LogInformation("批量更新商品: Count={Count}", dto.Products?.Count ?? 0);

                var result = await _domesticProductReactService.BatchUpdateDomesticProductsAsync(
                    dto
                );

                if (result.Success)
                {
                    if (result.Data == null)
                    {
                        // 成功响应必须带业务结果；缺失 Data 代表 service 契约异常，避免伪装成成功 0 条。
                        _logger.LogError("批量更新商品成功响应缺少 Data");
                        return StatusCode(
                            500,
                            new { success = false, message = "批量更新结果异常，请稍后重试" }
                        );
                    }

                    var data = result.Data;
                    return Ok(
                        new
                        {
                            success = true,
                            data = new
                            {
                                updatedProducts = data.UpdatedProducts,
                                updatedChanges = data.UpdatedChanges,
                                successCount = data.UpdatedProducts?.Count ?? 0,
                                failureCount = data.Errors?.Count ?? 0,
                                errors = data.Errors ?? new List<string>(),
                            },
                            message = $"批量更新完成：成功{(data.UpdatedProducts?.Count ?? 0)}条，失败{(data.Errors?.Count ?? 0)}条",
                        }
                    );
                }

                return BadRequest(
                    new
                    {
                        success = false,
                        message = result.Message,
                        errorCode = result.ErrorCode,
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量更新商品失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 批量更新 HBweb 商品主表 Product.ProductName（React 商品导入专用）
        /// </summary>
        /// <param name="dto">批量更新 DTO</param>
        /// <returns>更新结果</returns>
        [HttpPut("product-master-names")]
        [Authorize(Policy = Permissions.DomesticPurchase.ManageProducts)]
        public async Task<IActionResult> BatchUpdateHbwebProductNames(
            [FromBody] BatchUpdateHbwebProductNamesDto dto
        )
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    var errors = ModelState.Values.SelectMany(v =>
                        v.Errors.Select(e => e.ErrorMessage)
                    );
                    return BadRequest(
                        new
                        {
                            success = false,
                            message = $"输入验证失败: {string.Join(", ", errors)}",
                        }
                    );
                }

                var result = await _domesticProductReactService.BatchUpdateHbwebProductNamesAsync(
                    dto
                );
                if (result.Success)
                {
                    return Ok(
                        new
                        {
                            success = true,
                            data = result.Data,
                            message = result.Message,
                        }
                    );
                }

                return BadRequest(
                    new
                    {
                        success = false,
                        data = result.Data ?? result.Details,
                        message = result.Message,
                        errorCode = result.ErrorCode,
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量更新 HBweb 商品主表商品名称失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 批量删除商品（React专用）
        /// </summary>
        /// <param name="request">批量删除请求</param>
        /// <returns>删除结果</returns>
        [HttpDelete("batch-delete")]
        [Authorize(Policy = Permissions.Products.Delete)]
        public async Task<IActionResult> BatchDelete([FromBody] BatchDeleteRequestDto request)
        {
            try
            {
                _logger.LogInformation("批量删除国内商品: {Count} 件", request.ProductCodes.Count);

                var result = await _domesticProductReactService.BatchDeleteAsync(
                    request.ProductCodes
                );

                if (result.Success)
                {
                    return Ok(
                        new
                        {
                            success = true,
                            message = $"成功删除 {request.ProductCodes.Count} 件商品",
                        }
                    );
                }

                return BadRequest(new { success = false, message = result.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量删除失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 同步选中商品到HBSales数据库
        /// 按照商品编码匹配，只更新非空字段
        /// </summary>
        /// <param name="request">同步请求</param>
        /// <returns>同步结果</returns>
        [HttpPost("sync-to-hbsales")]
        [LegacyDatabaseDisabled(LegacyDatabaseKind.HBSales)]
        [Authorize(Policy = Permissions.DomesticPurchase.ManageProducts)]
        public async Task<IActionResult> SyncToHBSales([FromBody] SyncToHBSalesRequestDto request)
        {
            try
            {
                _logger.LogInformation(
                    "同步商品到HBSales: {Count} 件, IncludeImage: {IncludeImage}",
                    request.ProductCodes.Count,
                    request.IncludeImage
                );

                var result = await _domesticProductReactService.SyncSelectedToHBSalesAsync(
                    request.ProductCodes,
                    request.IncludeImage
                );

                if (result.Success)
                {
                    return Ok(
                        new
                        {
                            success = true,
                            data = result.Data,
                            message = result.Message,
                        }
                    );
                }

                return BadRequest(new { success = false, message = result.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "同步商品到HBSales失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 获取套装商品详情列表
        /// </summary>
        /// <param name="productCode">商品编码</param>
        /// <returns>套装信息列表</returns>
        [HttpGet("{productCode}/set-items")]
        [Authorize(Policy = Permissions.Products.View)]
        public async Task<IActionResult> GetSetItems(string productCode)
        {
            try
            {
                _logger.LogInformation("获取套装商品信息: ProductCode={ProductCode}", productCode);

                var result = await _domesticProductReactService.GetSetItemsAsync(productCode);

                if (result.Success)
                {
                    return Ok(result);
                }

                return BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取套装商品信息失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 更新套装商品信息
        /// </summary>
        /// <param name="productCode">商品编码</param>
        /// <param name="request">更新请求</param>
        /// <returns>更新结果</returns>
        [HttpPut("{productCode}/set-items")]
        [Authorize(Policy = Permissions.Products.Edit)]
        public async Task<IActionResult> UpdateSetItems(
            string productCode,
            [FromBody] UpdateSetItemsRequestDto request
        )
        {
            try
            {
                _logger.LogInformation(
                    "更新套装商品信息: ProductCode={ProductCode}, Items={Count}",
                    productCode,
                    request.Items.Count
                );

                var result = await _domesticProductReactService.UpdateSetItemsAsync(
                    productCode,
                    request.Items
                );

                if (result.Success)
                {
                    return Ok(new { success = true, message = "保存成功" });
                }

                return BadRequest(new { success = false, message = result.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新套装商品信息失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        /// <summary>
        /// 批量创建套装商品（React专用）
        /// 一次性创建多个套装商品，每个商品自动生成统一规格的套装明细
        /// </summary>
        /// <param name="dto">批量创建套装商品DTO</param>
        /// <returns>创建结果</returns>
        [HttpPost("batch-create-set-products")]
        [Authorize(Policy = Permissions.DomesticPurchase.ManageProducts)]
        public async Task<IActionResult> BatchCreateSetProducts(
            [FromBody] BatchCreateSetProductsDto dto
        )
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    var errors = ModelState.SelectMany(x =>
                        x.Value?.Errors?.Select(e => e.ErrorMessage)
                        ?? System.Linq.Enumerable.Empty<string>()
                    );
                    return BadRequest(
                        new
                        {
                            success = false,
                            message = $"输入验证失败: {string.Join(", ", errors)}",
                        }
                    );
                }

                _logger.LogInformation(
                    "批量创建套装商品: SupplierCode={SupplierCode}, Products={ProductCount}, SetType={SetType}",
                    dto.SupplierCode,
                    dto.Products.Count,
                    dto.SetType
                );

                var result = await _domesticProductReactService.BatchCreateSetProductsAsync(dto);

                if (result.Success)
                {
                    var resultData = result.Data;
                    return Ok(
                        new
                        {
                            success = true,
                            data = new
                            {
                                createdProducts = resultData?.CreatedProducts
                                    ?? new List<DomesticProductDto>(),
                                failedProducts = resultData?.FailedProducts ?? new List<object>(),
                                successCount = resultData?.SuccessCount ?? 0,
                                failureCount = resultData?.FailureCount ?? 0,
                                totalSetItems = resultData?.TotalSetItems ?? 0,
                                errors = resultData?.Errors ?? new List<string>(),
                            },
                            message = $"批量创建完成：成功{resultData?.SuccessCount ?? 0}个商品，共{resultData?.TotalSetItems ?? 0}个套装明细",
                        }
                    );
                }

                return BadRequest(
                    new
                    {
                        success = false,
                        message = result.Message,
                        errorCode = result.ErrorCode,
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量创建套装商品失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        [HttpPost("send-to-hq")]
        [LegacyDatabaseDisabled(LegacyDatabaseKind.Hq)]
        [Authorize(Policy = Permissions.DomesticPurchase.ManageProducts)]
        public async Task<IActionResult> SendToHq([FromBody] SyncToHBSalesRequestDto request)
        {
            try
            {
                _logger.LogInformation("发送商品到HQ: {Count} 件", request.ProductCodes.Count);
                var result = await _domesticProductReactService.SendProductsToHqAsync(request.ProductCodes);
                if (result.Success)
                    return Ok(result);
                return BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发送商品到HQ异常");
                return StatusCode(500, ApiResponse<SyncResult>.Error(ex.Message, "SEND_TO_HQ_ERROR"));
            }
        }
    }
}
