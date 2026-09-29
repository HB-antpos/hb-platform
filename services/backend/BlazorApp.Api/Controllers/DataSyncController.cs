using BlazorApp.Api.Filters;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers
{
    /// <summary>
    /// 数据同步控制器 - 提供从HQ数据库同步各类数据的API端点
    /// 2026-09-29 起所有 HQ → HBweb 入口标注 <see cref="HqToHbwebSyncDisabledAttribute"/> 返回 410；
    /// 翻译、PostgreSQL 与「国内商品 → HQ」反向同步不属于该方向，保持可用。
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Roles = "Admin")] // 只有管理员可以执行数据同步操作
    public class DataSyncController : ControllerBase
    {
        private readonly DataSyncService _dataSyncService;
        private readonly PostgresDataSyncService _postgresDataSyncService;
        private readonly ILogger<DataSyncController> _logger;

        public DataSyncController(
            DataSyncService dataSyncService,
            PostgresDataSyncService postgresDataSyncService,
            ILogger<DataSyncController> logger
        )
        {
            _dataSyncService = dataSyncService;
            _postgresDataSyncService = postgresDataSyncService;
            _logger = logger;
        }

        private IActionResult CreateContainerSyncFailureResponse(SyncResult result)
        {
            var committedCount = result.AddedCount + result.UpdatedCount + result.DeletedCount;
            var normalizedErrorCount = Math.Max(result.ErrorCount, 1);
            var normalizedBusyErrorCount =
                result.ErrorCode == ContainerMutationLock.BusyErrorCode
                    ? result.BusyErrorCount == 0
                        ? normalizedErrorCount
                        : Math.Clamp(result.BusyErrorCount, 0, normalizedErrorCount)
                    : 0;
            var allBusyWithoutCommit =
                committedCount == 0
                && normalizedBusyErrorCount == normalizedErrorCount;
            if (allBusyWithoutCommit)
            {
                Response.Headers.RetryAfter = "1";
                return Conflict(
                    ApiResponse<SyncResult>.Error(result.Message, result.ErrorCode, result)
                );
            }

            return BadRequest(
                ApiResponse<SyncResult>.Error(result.Message, "SYNC_FAILED", result)
            );
        }

        /// <summary>
        /// 从HQ总部同步供应商数据
        /// 🔄 从总部数据库获取最新供应商信息并更新本地数据库
        /// 这是一个敏感操作，只有Admin角色才能执行
        /// </summary>
        /// <returns>同步结果</returns>
        [HttpPost("sync-suppliers")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncSuppliersFromHq()
        {
            try
            {
                _logger.LogInformation("开始从HQ数据库同步供应商数据");
                var result = await _dataSyncService.SyncSuppliersFromHqAsync();

                if (result.IsSuccess)
                {
                    _logger.LogInformation("供应商数据同步成功");
                    return Ok(ApiResponse<SyncResult>.OK(result, "供应商数据同步成功"));
                }
                else
                {
                    _logger.LogWarning("供应商数据同步失败: {Message}", result.Message);
                    return BadRequest(
                        ApiResponse<SyncResult>.Error(result.Message, "SYNC_FAILED", result)
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "同步供应商数据时发生异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("同步过程中发生内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 从HQ总部同步商品分类数据
        /// 🔄 从总部数据库获取最新商品分类信息并更新本地数据库
        /// 这是一个敏感操作，只有Admin角色才能执行
        /// </summary>
        /// <returns>同步结果</returns>
        [HttpPost("sync-categories")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncCategoriesFromHq()
        {
            try
            {
                _logger.LogInformation("开始从HQ数据库同步商品分类数据");
                var result = await _dataSyncService.SyncCategoriesFromHqAsync();

                if (result.IsSuccess)
                {
                    _logger.LogInformation("商品分类数据同步成功");
                    return Ok(ApiResponse<SyncResult>.OK(result, "商品分类数据同步成功"));
                }
                else
                {
                    _logger.LogWarning("商品分类数据同步失败: {Message}", result.Message);
                    return BadRequest(
                        ApiResponse<SyncResult>.Error(result.Message, "SYNC_FAILED", result)
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "同步商品分类数据时发生异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("同步过程中发生内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 从HQ总部同步商品信息数据
        /// 🔄 从总部数据库获取最新商品信息并更新本地数据库
        /// 这是一个敏感操作，只有Admin角色才能执行
        /// </summary>
        /// <returns>同步结果</returns>
        [HttpPost("sync-products")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncProductsFromHq()
        {
            try
            {
                _logger.LogInformation("开始从HQ数据库同步商品信息数据");
                var result = await _dataSyncService.SyncProductsFromHqAsync();

                if (result.IsSuccess)
                {
                    _logger.LogInformation("商品信息数据同步成功");
                    return Ok(ApiResponse<SyncResult>.OK(result, "商品信息数据同步成功"));
                }
                else
                {
                    _logger.LogWarning("商品信息数据同步失败: {Message}", result.Message);
                    if (
                        result.ErrorCode == SetChildPurchasePriceMutationLock.BusyErrorCode
                    )
                    {
                        var response = ApiResponse<SyncResult>.Error(
                            result.Message,
                            result.ErrorCode,
                            result
                        );
                        return result.TotalCount == 0 ? Conflict(response) : Ok(response);
                    }
                    return BadRequest(
                        ApiResponse<SyncResult>.Error(result.Message, "SYNC_FAILED", result)
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "同步商品信息数据时发生异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("同步过程中发生内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 从HQ总部增量同步商品信息数据
        /// 🔄 基于指定日期，只同步此日期之后更新的商品信息
        /// 这是一个敏感操作，只有Admin角色才能执行
        /// </summary>
        /// <param name="lastUpdateDate">上次更新日期，格式：yyyy-MM-dd</param>
        /// <returns>同步结果</returns>
        [HttpPost("sync-products-incremental")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncProductsIncrementalFromHq(
            [FromQuery] string lastUpdateDate
        )
        {
            try
            {
                // 验证日期格式
                if (
                    string.IsNullOrEmpty(lastUpdateDate)
                    || !DateTime.TryParse(lastUpdateDate, out var parsedDate)
                )
                {
                    return BadRequest(
                        ApiResponse<SyncResult>.Error(
                            "无效的日期格式，请使用 yyyy-MM-dd 格式",
                            "INVALID_DATE_FORMAT"
                        )
                    );
                }

                _logger.LogInformation(
                    "开始从HQ数据库增量同步商品信息数据，上次更新时间: {LastUpdateDate}",
                    parsedDate
                );
                var result = await _dataSyncService.SyncProductsIncrementalFromHqAsync(parsedDate);

                if (result.IsSuccess)
                {
                    _logger.LogInformation("商品信息增量同步成功");
                    return Ok(ApiResponse<SyncResult>.OK(result, "商品信息增量同步成功"));
                }
                else
                {
                    _logger.LogWarning("商品信息增量同步失败: {Message}", result.Message);
                    if (
                        result.ErrorCode == SetChildPurchasePriceMutationLock.BusyErrorCode
                    )
                    {
                        var response = ApiResponse<SyncResult>.Error(
                            result.Message,
                            result.ErrorCode,
                            result
                        );
                        return result.TotalCount == 0 ? Conflict(response) : Ok(response);
                    }
                    return BadRequest(
                        ApiResponse<SyncResult>.Error(result.Message, "SYNC_FAILED", result)
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "增量同步商品信息数据时发生异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error(
                        "增量同步过程中发生内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 从HQ总部同步商品库存数据
        /// 🔄 从总部数据库获取最新商品库存信息并更新本地数据库
        /// 这是一个敏感操作，只有Admin角色才能执行
        /// </summary>
        /// <returns>同步结果</returns>
        [HttpPost("sync-product-stocks")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncProductStocksFromHq()
        {
            try
            {
                _logger.LogInformation("开始从HQ数据库同步商品库存数据");
                var result = await _dataSyncService.SyncProductStocksFromHqAsync();

                if (result.IsSuccess)
                {
                    _logger.LogInformation("商品库存数据同步成功");
                    return Ok(ApiResponse<SyncResult>.OK(result, "商品库存数据同步成功"));
                }
                else
                {
                    _logger.LogWarning("商品库存数据同步失败: {Message}", result.Message);
                    return BadRequest(
                        ApiResponse<SyncResult>.Error(result.Message, "SYNC_FAILED", result)
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "同步商品库存数据时发生异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("同步过程中发生内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 从HQ总部增量同步商品库存数据
        /// 🔄 基于指定日期，只同步此日期之后更新的库存信息
        /// 这是一个敏感操作，只有Admin角色才能执行
        /// </summary>
        /// <param name="lastUpdateDate">上次更新日期，格式：yyyy-MM-dd</param>
        /// <returns>同步结果</returns>
        [HttpPost("sync-product-stocks-incremental")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncProductStocksIncrementalFromHq(
            [FromQuery] string lastUpdateDate
        )
        {
            try
            {
                // 验证日期格式
                if (
                    string.IsNullOrEmpty(lastUpdateDate)
                    || !DateTime.TryParse(lastUpdateDate, out var parsedDate)
                )
                {
                    return BadRequest(
                        ApiResponse<SyncResult>.Error(
                            "无效的日期格式，请使用 yyyy-MM-dd 格式",
                            "INVALID_DATE_FORMAT"
                        )
                    );
                }

                _logger.LogInformation(
                    "开始从HQ数据库增量同步商品库存数据，上次更新时间: {LastUpdateDate}",
                    parsedDate
                );
                var result = await _dataSyncService.SyncProductStocksIncrementalFromHqAsync(
                    parsedDate
                );

                if (result.IsSuccess)
                {
                    _logger.LogInformation("商品库存增量同步成功");
                    return Ok(ApiResponse<SyncResult>.OK(result, "商品库存增量同步成功"));
                }
                else
                {
                    _logger.LogWarning("商品库存增量同步失败: {Message}", result.Message);
                    return BadRequest(
                        ApiResponse<SyncResult>.Error(result.Message, "SYNC_FAILED", result)
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "增量同步商品库存数据时发生异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error(
                        "库存增量同步过程中发生内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 从HQ总部同步货位信息数据
        /// 🔄 从总部数据库获取最新货位信息并更新本地数据库
        /// 这是一个敏感操作，只有Admin角色才能执行
        /// </summary>
        /// <returns>同步结果</returns>
        [HttpPost("sync-locations")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncLocationsFromHq()
        {
            try
            {
                _logger.LogInformation("开始从HQ数据库同步货位信息数据");
                var result = await _dataSyncService.SyncLocationsFromHqAsync();

                if (result.IsSuccess)
                {
                    _logger.LogInformation("货位信息数据同步成功");
                    return Ok(ApiResponse<SyncResult>.OK(result, "货位信息数据同步成功"));
                }
                else
                {
                    _logger.LogWarning("货位信息数据同步失败: {Message}", result.Message);
                    return BadRequest(
                        ApiResponse<SyncResult>.Error(result.Message, "SYNC_FAILED", result)
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "同步货位信息数据时发生异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("同步过程中发生内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 从HQ总部同步货位商品关联数据
        /// 🔄 从总部数据库获取最新货位商品关联信息并更新本地数据库
        /// 这是一个敏感操作，只有Admin角色才能执行
        /// </summary>
        /// <returns>同步结果</returns>
        [HttpPost("sync-product-locations")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncProductLocationsFromHq()
        {
            try
            {
                _logger.LogInformation("开始从HQ数据库同步货位商品关联数据");
                var result = await _dataSyncService.SyncProductLocationsFromHqAsync();

                if (result.IsSuccess)
                {
                    _logger.LogInformation("货位商品关联数据同步成功");
                    return Ok(ApiResponse<SyncResult>.OK(result, "货位商品关联数据同步成功"));
                }
                else
                {
                    _logger.LogWarning("货位商品关联数据同步失败: {Message}", result.Message);
                    return BadRequest(
                        ApiResponse<SyncResult>.Error(result.Message, "SYNC_FAILED", result)
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "同步货位商品关联数据时发生异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("同步过程中发生内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 从HQ总部同步所有数据
        /// 🔄 从总部数据库获取所有最新信息并更新本地数据库
        /// 这是一个敏感操作，只有Admin角色才能执行
        /// </summary>
        /// <returns>同步结果列表</returns>
        [HttpPost("sync-all")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncAllDataFromHq()
        {
            try
            {
                _logger.LogInformation("开始从HQ数据库同步所有数据");

                var results = new List<SyncResult>();

                // 同步供应商数据
                var supplierResult = await _dataSyncService.SyncSuppliersFromHqAsync();
                results.Add(supplierResult);

                // 同步商品分类数据
                var categoryResult = await _dataSyncService.SyncCategoriesFromHqAsync();
                results.Add(categoryResult);

                // 同步商品信息数据
                var productResult = await _dataSyncService.SyncProductsFromHqAsync();
                results.Add(productResult);

                // 同步商品库存数据
                var stockResult = await _dataSyncService.SyncProductStocksFromHqAsync();
                results.Add(stockResult);

                // 同步货位信息数据
                var locationResult = await _dataSyncService.SyncLocationsFromHqAsync();
                results.Add(locationResult);

                // 同步货位商品关联数据
                var productLocationResult =
                    await _dataSyncService.SyncProductLocationsFromHqAsync();
                results.Add(productLocationResult);

                var isSuccess = results.All(r => r.IsSuccess);
                var message = isSuccess ? "所有数据同步成功" : "部分数据同步失败";

                if (isSuccess)
                {
                    _logger.LogInformation("所有数据同步成功");
                    return Ok(ApiResponse<List<SyncResult>>.OK(results, message));
                }
                else
                {
                    _logger.LogWarning("部分数据同步失败");
                    return BadRequest(
                        ApiResponse<List<SyncResult>>.Error(message, "PARTIAL_SYNC_FAILED", results)
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "同步所有数据时发生异常");
                return StatusCode(
                    500,
                    ApiResponse<List<SyncResult>>.Error(
                        "同步过程中发生内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 批量翻译所有商品名称
        /// 🌐 将所有包含中文的商品名称翻译为英文并保存到英文名称字段
        /// </summary>
        /// <returns>翻译结果</returns>
        [HttpPost("translate-all-product-names")]
        public async Task<IActionResult> TranslateAllProductNames()
        {
            try
            {
                _logger.LogInformation("开始批量翻译所有商品名称");
                var result = await _dataSyncService.TranslateAllProductNamesAsync();

                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "商品名称批量翻译成功，翻译了 {Count} 个商品",
                        result.UpdatedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "商品名称翻译成功"));
                }
                else
                {
                    _logger.LogWarning("商品名称翻译失败: {Message}", result.Message);
                    return BadRequest(
                        ApiResponse<SyncResult>.Error(result.Message, "TRANSLATION_FAILED", result)
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "翻译商品名称时发生异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("翻译过程中发生内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 选择性翻译商品名称
        /// 🎯 根据指定条件翻译商品名称
        /// </summary>
        /// <param name="request">翻译请求参数</param>
        /// <returns>翻译结果</returns>
        [HttpPost("translate-product-names")]
        public async Task<IActionResult> TranslateProductNames(
            [FromBody] TranslateProductsRequest request
        )
        {
            try
            {
                _logger.LogInformation(
                    "开始选择性翻译商品名称，模式: {Mode}, 过滤器: {Filter}",
                    request.Mode,
                    request.ProductCodeFilter ?? "无"
                );

                var result = await _dataSyncService.TranslateProductNamesAsync(
                    request.Mode,
                    request.ProductCodeFilter
                );

                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "商品名称选择性翻译成功，翻译了 {Count} 个商品",
                        result.UpdatedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "商品名称翻译成功"));
                }
                else
                {
                    _logger.LogWarning("商品名称翻译失败: {Message}", result.Message);
                    return BadRequest(
                        ApiResponse<SyncResult>.Error(result.Message, "TRANSLATION_FAILED", result)
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "翻译商品名称时发生异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("翻译过程中发生内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 从HQ同步国内商品数据
        /// 🏭 从总部数据库获取国内商品信息并更新本地数据库
        /// </summary>
        /// <returns>同步结果</returns>
        [HttpPost("sync-domestic-products")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncDomesticProductsFromHq()
        {
            try
            {
                _logger.LogInformation("开始从HQ数据库同步国内商品数据");
                var result = await _dataSyncService.SyncDomesticProductsFromHqAsync();

                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "国内商品数据同步成功，新增 {AddedCount} 个商品",
                        result.AddedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "国内商品数据同步成功"));
                }
                else
                {
                    _logger.LogWarning("国内商品数据同步失败: {Message}", result.Message);
                    return BadRequest(
                        ApiResponse<SyncResult>.Error(result.Message, "SYNC_FAILED", result)
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "同步国内商品数据时发生异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("同步过程中发生内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 从HQ同步货号前缀数据
        /// 📝 从总部数据库获取货号前缀信息并更新本地数据库
        /// </summary>
        /// <returns>同步结果</returns>
        [HttpPost("sync-product-prefix-codes")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncProductPrefixCodesFromHq()
        {
            try
            {
                _logger.LogInformation("开始从HQ数据库同步货号前缀数据");
                var result = await _dataSyncService.SyncProductPrefixCodesFromHqAsync();

                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "货号前缀数据同步成功，新增 {AddedCount} 个前缀",
                        result.AddedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "货号前缀数据同步成功"));
                }
                else
                {
                    _logger.LogWarning("货号前缀数据同步失败: {Message}", result.Message);
                    return BadRequest(
                        ApiResponse<SyncResult>.Error(result.Message, "SYNC_FAILED", result)
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "同步货号前缀数据时发生异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("同步过程中发生内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 从HQ同步套装商品数据
        /// 📦 从总部数据库获取套装商品信息并更新本地数据库
        /// </summary>
        /// <returns>同步结果</returns>
        [HttpPost("sync-domestic-set-products")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncDomesticSetProductsFromHq()
        {
            try
            {
                _logger.LogInformation("开始从HQ数据库同步套装商品数据");
                var result = await _dataSyncService.SyncDomesticSetProductsFromHqAsync();

                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "套装商品数据同步成功，新增 {AddedCount} 个套装商品",
                        result.AddedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "套装商品数据同步成功"));
                }
                else
                {
                    _logger.LogWarning("套装商品数据同步失败: {Message}", result.Message);
                    return BadRequest(
                        ApiResponse<SyncResult>.Error(result.Message, "SYNC_FAILED", result)
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "同步套装商品数据时发生异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("同步过程中发生内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 从HQ总部同步货柜信息数据（全量同步）
        /// 🚢 从总部数据库获取货柜主表和明细表信息并更新本地数据库
        /// 这是一个敏感操作，只有Admin角色才能执行
        /// </summary>
        /// <returns>同步结果</returns>
        [HttpPost("sync-containers")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncContainersFromHq()
        {
            try
            {
                _logger.LogInformation("开始从HQ数据库全量同步货柜数据");
                var result = await _dataSyncService.SyncContainersFromHqAsync();

                if (result.IsSuccess)
                {
                    _logger.LogInformation("货柜数据全量同步成功");
                    return Ok(ApiResponse<SyncResult>.OK(result, "货柜数据同步成功"));
                }
                else
                {
                    _logger.LogWarning("货柜数据同步失败: {Message}", result.Message);
                    return CreateContainerSyncFailureResponse(result);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "同步货柜数据时发生异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("同步过程中发生内部错误", "INTERNAL_SERVER_ERROR")
                );
            }
        }

        /// <summary>
        /// 从HQ总部增量同步货柜信息数据
        /// 🔄 基于指定日期，只同步此日期之后更新的货柜信息
        /// 这是一个敏感操作，只有Admin角色才能执行
        /// </summary>
        /// <param name="lastUpdateDate">上次更新日期，格式：yyyy-MM-dd</param>
        /// <returns>同步结果</returns>
        [HttpPost("sync-containers-incremental")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncContainersIncrementalFromHq(
            [FromQuery] string lastUpdateDate
        )
        {
            try
            {
                // 验证日期格式
                if (
                    string.IsNullOrEmpty(lastUpdateDate)
                    || !DateTime.TryParse(lastUpdateDate, out var parsedDate)
                )
                {
                    return BadRequest(
                        ApiResponse<SyncResult>.Error(
                            "无效的日期格式，请使用 yyyy-MM-dd 格式",
                            "INVALID_DATE_FORMAT"
                        )
                    );
                }

                _logger.LogInformation(
                    "开始从HQ数据库增量同步货柜数据，上次更新时间: {LastUpdateDate}",
                    parsedDate
                );
                var result = await _dataSyncService.SyncContainersIncrementalFromHqAsync(
                    parsedDate
                );

                if (result.IsSuccess)
                {
                    _logger.LogInformation("货柜数据增量同步成功");
                    return Ok(ApiResponse<SyncResult>.OK(result, "货柜数据增量同步成功"));
                }
                else
                {
                    _logger.LogWarning("货柜数据增量同步失败: {Message}", result.Message);
                    return CreateContainerSyncFailureResponse(result);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "增量同步货柜数据时发生异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error(
                        "增量同步过程中发生内部错误",
                        "INTERNAL_SERVER_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 测试PostgreSQL数据库连接
        /// 🔗 检测指定的PostgreSQL数据库连接是否正常
        /// </summary>
        /// <returns>连接测试结果</returns>
        [HttpPost("test-postgres-connection")]
        public async Task<IActionResult> TestPostgresConnection()
        {
            try
            {
                _logger.LogInformation("开始测试PostgreSQL数据库连接");
                var result = await _dataSyncService.TestPostgresConnectionAsync();

                if (result.IsSuccess)
                {
                    _logger.LogInformation("PostgreSQL数据库连接测试成功");
                    return Ok(ApiResponse<SyncResult>.OK(result, "PostgreSQL数据库连接正常"));
                }
                else
                {
                    _logger.LogWarning("PostgreSQL数据库连接测试失败: {Message}", result.Message);
                    return BadRequest(
                        ApiResponse<SyncResult>.Error(result.Message, "CONNECTION_FAILED", result)
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PostgreSQL数据库连接测试异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("数据库连接测试异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 同步所有数据到PostgreSQL数据库
        /// 🔄 将SQL Server中的所有数据同步到PostgreSQL数据库
        /// </summary>
        /// <returns>同步结果</returns>
        [HttpPost("sync-all-to-postgres")]
        public async Task<IActionResult> SyncAllDataToPostgres()
        {
            try
            {
                _logger.LogInformation("开始将所有数据同步到PostgreSQL数据库");
                var results = await _postgresDataSyncService.SyncAllDataToPostgresAsync();

                var successCount = results.Count(r => r.IsSuccess);
                var totalCount = results.Count;

                if (successCount == totalCount)
                {
                    _logger.LogInformation($"PostgreSQL数据同步全部成功，共 {totalCount} 项");
                    return Ok(
                        ApiResponse<List<SyncResult>>.OK(
                            results,
                            $"PostgreSQL数据同步成功，{successCount}/{totalCount} 项成功"
                        )
                    );
                }
                else
                {
                    _logger.LogWarning(
                        $"PostgreSQL数据同步部分成功，{successCount}/{totalCount} 项成功"
                    );
                    return Ok(
                        ApiResponse<List<SyncResult>>.OK(
                            results,
                            $"PostgreSQL数据同步部分成功，{successCount}/{totalCount} 项成功"
                        )
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PostgreSQL数据同步失败");
                return StatusCode(
                    500,
                    ApiResponse<List<SyncResult>>.Error("PostgreSQL数据同步异常", "SYNC_ERROR")
                );
            }
        }

        /// <summary>
        /// 重新创建PostgreSQL数据库表结构
        /// ⚠️ 危险操作：将删除PostgreSQL中的所有数据并重新创建表结构
        /// </summary>
        /// <returns>操作结果</returns>
        [HttpPost("recreate-postgres-tables")]
        public async Task<IActionResult> RecreatePostgresTables()
        {
            try
            {
                _logger.LogWarning("开始重新创建PostgreSQL数据库表结构");
                var result = await _postgresDataSyncService.RecreatePostgresTablesAsync();

                if (result.IsSuccess)
                {
                    _logger.LogInformation("PostgreSQL数据库表结构重建成功");
                    return Ok(ApiResponse<SyncResult>.OK(result, result.Message));
                }
                else
                {
                    _logger.LogError("PostgreSQL数据库表结构重建失败: {Message}", result.Message);
                    return BadRequest(
                        ApiResponse<SyncResult>.Error(result.Message, "RECREATE_FAILED", result)
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PostgreSQL数据库表结构重建失败");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("数据库表结构重建异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 从HQ总部同步分店零售价数据
        /// 🚀 使用按分店并发版本，支持500万条数据的高效同步，按分店代码分别并发执行查询和插入
        /// </summary>
        /// <param name="request">同步请求，包含选中的分店代码</param>
        /// <returns>同步结果</returns>
        [HttpPost("store-retail-prices")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncStoreRetailPricesFromHq(
            [FromBody] StoreSyncRequest? request = null
        )
        {
            try
            {
                var selectedStores =
                    request?.SelectedStoreCodes?.Any() == true
                        ? string.Join(", ", request.SelectedStoreCodes)
                        : "全部分店";

                _logger.LogInformation($"🚀 开始按分店并发同步零售价数据 - {selectedStores}");

                // 使用按分店并发版本，默认参数：30个分店并发，每分店20万条
                var result = await _dataSyncService.SyncStoreRetailPricesFromHqConcurrentAsync(
                    request?.SelectedStoreCodes,
                    maxConcurrency: 30,
                    batchSize: 200000
                );

                if (result.IsSuccess)
                {
                    _logger.LogInformation("🎉 按分店并发零售价数据同步成功");
                    return Ok(ApiResponse<SyncResult>.OK(result, "分店零售价数据同步成功"));
                }
                else
                {
                    _logger.LogWarning($"⚠️ 按分店并发零售价数据同步部分失败: {result.Message}");
                    return Ok(
                        ApiResponse<SyncResult>.OK(result, "分店零售价数据同步完成，但有部分失败")
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 按分店并发零售价数据同步失败");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("分店零售价数据同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 从HQ总部同步分店清货价数据
        /// </summary>
        /// <param name="request">同步请求，包含选中的分店代码</param>
        /// <returns>同步结果</returns>
        [HttpPost("store-clearance-prices")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncStoreClearancePricesFromHq(
            [FromBody] StoreSyncRequest? request = null
        )
        {
            try
            {
                var selectedStores =
                    request?.SelectedStoreCodes?.Any() == true
                        ? string.Join(", ", request.SelectedStoreCodes)
                        : "全部分店";
                _logger.LogInformation($"开始从HQ数据库同步分店清货价数据 - {selectedStores}");
                var result = await _dataSyncService.SyncStoreClearancePricesFromHqAsync(
                    request?.SelectedStoreCodes
                );

                if (result.IsSuccess)
                {
                    _logger.LogInformation("分店清货价数据同步成功");
                    return Ok(ApiResponse<SyncResult>.OK(result, "分店清货价数据同步成功"));
                }
                else
                {
                    _logger.LogWarning($"分店清货价数据同步部分失败: {result.Message}");
                    return Ok(
                        ApiResponse<SyncResult>.OK(result, "分店清货价数据同步完成，但有部分失败")
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "分店清货价数据同步失败");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("分店清货价数据同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 从HQ总部同步分店一品多码数据
        /// </summary>
        /// <param name="request">同步请求，包含选中的分店代码</param>
        /// <returns>同步结果</returns>
        [HttpPost("store-multicode-products")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncStoreMultiCodeProductsFromHq(
            [FromBody] StoreSyncRequest? request = null
        )
        {
            try
            {
                var selectedStores =
                    request?.SelectedStoreCodes?.Any() == true
                        ? string.Join(", ", request.SelectedStoreCodes)
                        : "全部分店";
                _logger.LogInformation($"开始从HQ数据库同步分店一品多码数据 - {selectedStores}");
                var result = await _dataSyncService.SyncStoreMultiCodeProductsFromHqAsync(
                    request?.SelectedStoreCodes
                );

                if (result.IsSuccess)
                {
                    _logger.LogInformation("分店一品多码数据同步成功");
                    return Ok(ApiResponse<SyncResult>.OK(result, "分店一品多码数据同步成功"));
                }
                else
                {
                    _logger.LogWarning($"分店一品多码数据同步部分失败: {result.Message}");
                    return Ok(
                        ApiResponse<SyncResult>.OK(result, "分店一品多码数据同步完成，但有部分失败")
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "分店一品多码数据同步失败");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("分店一品多码数据同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 同步分店零售价数据到PostgreSQL数据库
        /// 🔄 将本地SQL Server的分店零售价数据同步到PostgreSQL数据库
        /// </summary>
        /// <param name="request">同步请求，包含选中的分店代码</param>
        /// <returns>同步结果</returns>
        [HttpPost("sync-store-retail-prices-to-postgres")]
        public async Task<IActionResult> SyncStoreRetailPricesToPostgres(
            [FromBody] StoreSyncRequest? request = null
        )
        {
            try
            {
                var selectedStores =
                    request?.SelectedStoreCodes?.Any() == true
                        ? string.Join(", ", request.SelectedStoreCodes)
                        : "全部分店";
                _logger.LogInformation(
                    $"开始将分店零售价数据同步到PostgreSQL数据库 - {selectedStores}"
                );

                var result = await _postgresDataSyncService.SyncStoreRetailPricesToPostgresAsync(
                    request?.SelectedStoreCodes
                );

                if (result.IsSuccess)
                {
                    _logger.LogInformation("分店零售价PostgreSQL同步成功");
                    return Ok(ApiResponse<SyncResult>.OK(result, "分店零售价PostgreSQL同步成功"));
                }
                else
                {
                    _logger.LogWarning("分店零售价PostgreSQL同步失败: {Message}", result.Message);
                    return BadRequest(
                        ApiResponse<SyncResult>.Error(result.Message, "SYNC_FAILED", result)
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "分店零售价PostgreSQL同步失败");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("分店零售价PostgreSQL同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 同步分店清货价数据到PostgreSQL数据库
        /// 🔄 将本地SQL Server的分店清货价数据同步到PostgreSQL数据库
        /// </summary>
        /// <param name="request">同步请求，包含选中的分店代码</param>
        /// <returns>同步结果</returns>
        [HttpPost("sync-store-clearance-prices-to-postgres")]
        public async Task<IActionResult> SyncStoreClearancePricesToPostgres(
            [FromBody] StoreSyncRequest? request = null
        )
        {
            try
            {
                var selectedStores =
                    request?.SelectedStoreCodes?.Any() == true
                        ? string.Join(", ", request.SelectedStoreCodes)
                        : "全部分店";
                _logger.LogInformation(
                    $"开始将分店清货价数据同步到PostgreSQL数据库 - {selectedStores}"
                );

                var result = await _postgresDataSyncService.SyncStoreClearancePricesToPostgresAsync(
                    request?.SelectedStoreCodes
                );

                if (result.IsSuccess)
                {
                    _logger.LogInformation("分店清货价PostgreSQL同步成功");
                    return Ok(ApiResponse<SyncResult>.OK(result, "分店清货价PostgreSQL同步成功"));
                }
                else
                {
                    _logger.LogWarning("分店清货价PostgreSQL同步失败: {Message}", result.Message);
                    return BadRequest(
                        ApiResponse<SyncResult>.Error(result.Message, "SYNC_FAILED", result)
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "分店清货价PostgreSQL同步失败");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("分店清货价PostgreSQL同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 同步分店一品多码数据到PostgreSQL数据库
        /// 🔄 将本地SQL Server的分店一品多码数据同步到PostgreSQL数据库
        /// </summary>
        /// <param name="request">同步请求，包含选中的分店代码</param>
        /// <returns>同步结果</returns>
        [HttpPost("sync-store-multicode-products-to-postgres")]
        public async Task<IActionResult> SyncStoreMultiCodeProductsToPostgres(
            [FromBody] StoreSyncRequest? request = null
        )
        {
            try
            {
                var selectedStores =
                    request?.SelectedStoreCodes?.Any() == true
                        ? string.Join(", ", request.SelectedStoreCodes)
                        : "全部分店";
                _logger.LogInformation(
                    $"开始将分店一品多码数据同步到PostgreSQL数据库 - {selectedStores}"
                );

                var result =
                    await _postgresDataSyncService.SyncStoreMultiCodeProductsToPostgresAsync(
                        request?.SelectedStoreCodes
                    );

                if (result.IsSuccess)
                {
                    _logger.LogInformation("分店一品多码PostgreSQL同步成功");
                    return Ok(ApiResponse<SyncResult>.OK(result, "分店一品多码PostgreSQL同步成功"));
                }
                else
                {
                    _logger.LogWarning("分店一品多码PostgreSQL同步失败: {Message}", result.Message);
                    return BadRequest(
                        ApiResponse<SyncResult>.Error(result.Message, "SYNC_FAILED", result)
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "分店一品多码PostgreSQL同步失败");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error(
                        "分店一品多码PostgreSQL同步异常",
                        "INTERNAL_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 反向同步：将本地国内商品信息同步到HQ数据库
        /// 🔄 将本地DomesticProduct表中的商品信息按照商品编码匹配同步到HQ的CPT_DIC_商品信息字典表
        /// </summary>
        /// <param name="request">反向同步请求，包含最后更新日期</param>
        /// <returns>同步结果</returns>
        [HttpPost("domestic-products-to-hq")]
        public async Task<IActionResult> SyncDomesticProductsToHq(
            [FromBody] ReverseSyncRequest request
        )
        {
            try
            {
                _logger.LogInformation(
                    "开始将本地国内商品信息反向同步到HQ数据库，更新日期: {LastUpdateDate}",
                    request.LastUpdateDate
                );

                var result = await _dataSyncService.SyncDomesticProductsToHqAsync(
                    request.LastUpdateDate
                );

                if (result.IsSuccess)
                {
                    _logger.LogInformation("国内商品信息反向同步成功");
                    return Ok(ApiResponse<SyncResult>.OK(result, "国内商品信息反向同步成功"));
                }
                else
                {
                    _logger.LogWarning("国内商品信息反向同步失败: {Message}", result.Message);
                    return BadRequest(
                        ApiResponse<SyncResult>.Error(result.Message, "SYNC_FAILED", result)
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "国内商品信息反向同步失败");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("国内商品信息反向同步异常", "INTERNAL_ERROR")
                );
            }
        }
    }

    /// <summary>
    /// 翻译商品请求模型
    /// </summary>
    public class TranslateProductsRequest
    {
        public string Mode { get; set; } = "untranslated";
        public string? ProductCodeFilter { get; set; }
    }

    /// <summary>
    /// 分店同步请求模型
    /// </summary>
    public class StoreSyncRequest
    {
        /// <summary>
        /// 选中的分店代码列表
        /// </summary>
        public List<string>? SelectedStoreCodes { get; set; }
    }

    /// <summary>
    /// 反向同步请求模型
    /// </summary>
    public class ReverseSyncRequest
    {
        /// <summary>
        /// 最后更新日期，只同步此日期之后更新的商品
        /// </summary>
        public DateTime LastUpdateDate { get; set; }
    }
}
