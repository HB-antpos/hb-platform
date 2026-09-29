using BlazorApp.Api.Filters;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// 数据同步入口。除「商品-供应商映射 → POSM」两个接口外，其余都是 HQ / HBSales → HBweb 方向，
    /// 已于 2026-09-29 通过 <see cref="HqToHbwebSyncDisabledAttribute"/> 统一返回 410；服务实现暂时保留。
    /// </summary>
    [ApiController]
    [Route("api/react/v1/sync")]
    [Authorize]
    public class DataSyncReactController : ControllerBase
    {
        private readonly IDataSyncFullService _fullSyncService;
        private readonly IDataSyncIncrementalService _incrementalSyncService;
        private readonly IProductHqSyncService _productHqSyncService;
        private readonly ILogger<DataSyncReactController> _logger;
        private readonly ICurrentUserService _currentUserService;

        public DataSyncReactController(
            IDataSyncFullService fullSyncService,
            IDataSyncIncrementalService incrementalSyncService,
            IProductHqSyncService productHqSyncService,
            ILogger<DataSyncReactController> logger,
            ICurrentUserService currentUserService
        )
        {
            _fullSyncService = fullSyncService;
            _incrementalSyncService = incrementalSyncService;
            _productHqSyncService = productHqSyncService;
            _logger = logger;
            _currentUserService = currentUserService;
        }

        /// <summary>
        /// 全量同步商品信息：DIC_商品信息字典表 → HBweb.Product
        /// </summary>
        [HttpPost("products")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncProducts()
        {
            try
            {
                _logger.LogInformation("[ReactSync] 开始全量同步商品信息");
                var productResult = await _productHqSyncService.SyncFullAsync(
                    _currentUserService.GetCurrentUserGuid(),
                    _currentUserService.GetCurrentUsername()
                );
                var result = ToSyncResult(
                    productResult.Data,
                    productResult.Success,
                    productResult.Message,
                    productResult.ErrorCode
                );
                if (productResult.Success)
                {
                    _logger.LogInformation(
                        "[ReactSync] 商品同步成功: 新增{Added}, 更新{Updated}, 错误{Error}",
                        result.AddedCount,
                        result.UpdatedCount,
                        result.ErrorCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "商品同步成功"));
                }
                return CreateSyncFailureResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 商品同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("商品同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 全量同步分店零售价：DIC_商品零售价表 → HBweb.StoreRetailPrice（按分店并发）
        /// </summary>
        [HttpPost("store-retail-prices")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncStoreRetailPrices(
            [FromBody] ReactStoreSyncRequest? request = null
        )
        {
            try
            {
                var storeInfo =
                    request?.SelectedStoreCodes?.Any() == true
                        ? string.Join(", ", request!.SelectedStoreCodes!)
                        : "全部分店";
                _logger.LogInformation("[ReactSync] 开始同步分店零售价：{StoreInfo}", storeInfo);
                var result = await _fullSyncService.SyncStoreRetailPricesFromHqConcurrentAsync(
                    request?.SelectedStoreCodes
                );
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "[ReactSync] 分店零售价同步成功: 新增{Added}, 错误{Error}",
                        result.AddedCount,
                        result.ErrorCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "分店零售价同步成功"));
                }
                return CreateSyncFailureResult(result, "分店零售价同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 分店零售价同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("分店零售价同步异常", "INTERNAL_ERROR")
                );
            }
        }

        [HttpPost("store-multi-code-products")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncStoreMultiCodeProducts(
            [FromBody] ReactStoreSyncRequest? request = null
        )
        {
            try
            {
                var storeInfo =
                    request?.SelectedStoreCodes?.Any() == true
                        ? string.Join(", ", request!.SelectedStoreCodes!)
                        : "全部分店";
                _logger.LogInformation("[ReactSync] 开始同步分店一品多码：{StoreInfo}", storeInfo);
                var result = await _fullSyncService.SyncStoreMultiCodeProductsFromHqConcurrentAsync(
                    request?.SelectedStoreCodes
                );
                if (result.IsSuccess)
                {
                    return Ok(ApiResponse<SyncResult>.OK(result, "分店一品多码同步成功"));
                }
                return CreateSyncFailureResult(result, "分店一品多码同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 分店一品多码同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("分店一品多码同步异常", "INTERNAL_ERROR")
                );
            }
        }

        [HttpPost("product-set-codes")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncProductSetCodes()
        {
            try
            {
                _logger.LogInformation("[ReactSync] 开始同步一品多码 → 套装多码");
                var result = await _fullSyncService.SyncProductSetCodesFromHqAsync();
                if (result.IsSuccess)
                {
                    return Ok(ApiResponse<SyncResult>.OK(result, "套装多码同步成功"));
                }
                return CreateSyncFailureResult(result, "套装多码同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 套装多码同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("套装多码同步异常", "INTERNAL_ERROR")
                );
            }
        }

        [HttpPost("store-clearance-prices")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncStoreClearancePrices(
            [FromBody] ReactStoreSyncRequest? request = null
        )
        {
            try
            {
                var storeInfo =
                    request?.SelectedStoreCodes?.Any() == true
                        ? string.Join(", ", request!.SelectedStoreCodes!)
                        : "全部分店";
                _logger.LogInformation("[ReactSync] 开始同步分店清货价：{StoreInfo}", storeInfo);
                var result = await _fullSyncService.SyncStoreClearancePricesFromHqConcurrentAsync(
                    request?.SelectedStoreCodes
                );
                if (result.IsSuccess)
                {
                    return Ok(ApiResponse<SyncResult>.OK(result, "分店清货价同步成功"));
                }
                return CreateSyncFailureResult(result, "分店清货价同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 分店清货价同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("分店清货价同步异常", "INTERNAL_ERROR")
                );
            }
        }

        [HttpPost("domestic-products")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncDomesticProducts()
        {
            var result = await _fullSyncService.SyncDomesticProductsFromHqAsync();
            return CreateSyncResponse(result, result.Message);
        }

        [HttpPost("domestic-set-products")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncDomesticSetProducts()
        {
            var result = await _fullSyncService.SyncDomesticSetProductsFromHqAsync();
            return CreateSyncResponse(result, result.Message);
        }

        [HttpPost("product-prefix-codes")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncProductPrefixCodes()
        {
            var result = await _fullSyncService.SyncProductPrefixCodesFromHqAsync();
            return CreateSyncResponse(result, result.Message);
        }

        [HttpPost("china-suppliers")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncChinaSuppliers()
        {
            var result = await _fullSyncService.SyncChinaSuppliersFromHqAsync();
            return CreateSyncResponse(result, result.Message);
        }

        [HttpPost("warehouse-categories")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncWarehouseCategories()
        {
            var result = await _fullSyncService.SyncWarehouseCategoriesFromHqAsync();
            return CreateSyncResponse(result, result.Message);
        }

        public class ContainerSyncRequest
        {
            public List<string>? SelectedMasterGuids { get; set; }
            public DateTime? StartDate { get; set; }
        }

        public class IncrementalSyncRequest
        {
            public DateTime? StartDate { get; set; }
        }

        [HttpPost("container-details")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncContainerDetails(
            [FromBody] ContainerSyncRequest? request = null
        )
        {
            var result = await _fullSyncService.SyncContainerDetailsFromHqAsync(
                request?.SelectedMasterGuids
            );
            return CreateSyncResponse(result, result.Message);
        }

        [HttpPost("containers")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncContainers()
        {
            var result = await _fullSyncService.SyncContainersFromHqAsync();
            return CreateSyncResponse(result, result.Message);
        }

        [HttpPost("warehouse-products")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncWarehouseProducts()
        {
            var result = await _fullSyncService.SyncWarehouseProductsFromHqAsync(
                50000,
                10000,
                _currentUserService.GetCurrentUserGuid(),
                _currentUserService.GetCurrentUsername()
            );
            return CreateSyncResponse(result, result.Message);
        }

        [HttpPost("store-local-supplier-invoices")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncStoreLocalSupplierInvoices()
        {
            var result = await _fullSyncService.SyncStoreLocalSupplierInvoicesFromHqAsync();
            return CreateSyncResponse(result, result.Message);
        }

        [HttpPost("store-local-supplier-invoice-details")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncStoreLocalSupplierInvoiceDetails()
        {
            var result = await _fullSyncService.SyncStoreLocalSupplierInvoiceDetailsFromHqAsync();
            return CreateSyncResponse(result, result.Message);
        }

        [HttpPost("store-local-supplier-invoices-all")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncStoreLocalSupplierInvoicesAll()
        {
            var result =
                await _fullSyncService.SyncStoreLocalSupplierInvoicesAndDetailsFromHqAsync();
            return CreateSyncResponse(result, result.Message);
        }

        [HttpPost("warehouse-orders")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncWareHouseOrders()
        {
            var result = await _fullSyncService.SyncWareHouseOrdersFromHqAsync();
            return CreateSyncResponse(result, result.Message);
        }

        [HttpPost("warehouse-order-details")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncWareHouseOrderDetails()
        {
            var result = await _fullSyncService.SyncWareHouseOrderDetailsFromHqAsync();
            return CreateSyncResponse(result, result.Message);
        }

        [HttpPost("warehouse-orders-all")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncWareHouseOrdersAll()
        {
            var result = await _fullSyncService.SyncWareHouseOrdersAllFromHqAsync();
            return CreateSyncResponse(result, result.Message);
        }

        [HttpPost("locations")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncLocations()
        {
            var result = await _fullSyncService.SyncLocationsFromHqAsync();
            return CreateSyncResponse(result, result.Message);
        }

        [HttpPost("product-locations")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncProductLocations()
        {
            var result = await _fullSyncService.SyncProductLocationsFromHqAsync();
            return CreateSyncResponse(result, result.Message);
        }

        /// <summary>
        /// 全量同步收银用户：DIC_收银用户信息表 → HBweb.CashRegisterUser
        /// </summary>
        [HttpPost("cash-register-users")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncCashRegisterUsers()
        {
            try
            {
                _logger.LogInformation("[ReactSync] 开始全量同步收银用户信息");
                var result = await _fullSyncService.SyncCashRegisterUsersFromHqAsync();
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "[ReactSync] 收银用户同步成功: 新增{Added}, 错误{Error}",
                        result.AddedCount,
                        result.ErrorCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "收银用户同步成功"));
                }
                return CreateSyncFailureResult(result, "收银用户同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 收银用户同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("收银用户同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 同步商品-供应商映射表：主数据库 → POSM 数据库
        /// </summary>
        [HttpPost("posm-product-supplier-mappings")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> SyncPosmProductSupplierMappings()
        {
            try
            {
                _logger.LogInformation("[ReactSync] 开始同步商品-供应商映射表到POSM数据库");
                var result = await _fullSyncService.SyncPosmProductSupplierMappingsAsync();
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "[ReactSync] 商品-供应商映射表同步成功: 新增{Added}",
                        result.AddedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "商品-供应商映射表同步成功"));
                }
                return CreateSyncFailureResult(result, "商品-供应商映射表同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 商品-供应商映射表同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("商品-供应商映射表同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 增量同步商品-供应商映射表：主数据库 → POSM 数据库
        /// 根据商品的最后更新时间同步变更的数据
        /// </summary>
        [HttpPost("posm-product-supplier-mappings-incremental")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> SyncPosmProductSupplierMappingsIncremental(
            [FromBody] IncrementalSyncRequest? request = null
        )
        {
            try
            {
                _logger.LogInformation("[ReactSync] 开始增量同步商品-供应商映射表到POSM数据库");
                var result =
                    await _incrementalSyncService.SyncPosmProductSupplierMappingsIncrementalAsync(
                        request?.StartDate
                    );
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "[ReactSync] 商品-供应商映射表增量同步成功: 更新{Updated}, 新增{Added}",
                        result.UpdatedCount,
                        result.AddedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "商品-供应商映射表增量同步成功"));
                }
                return CreateSyncFailureResult(
                    result,
                    "商品-供应商映射表增量同步完成，但存在错误"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 商品-供应商映射表增量同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("商品-供应商映射表增量同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 增量同步进货单主表：HQ → 本地 StoreLocalSupplierInvoice
        /// 基于最近一次成功同步的时间点，默认100天内
        /// </summary>
        [HttpPost("store-local-supplier-invoices-incremental")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncStoreLocalSupplierInvoicesIncremental(
            [FromBody] IncrementalSyncRequest? request = null
        )
        {
            try
            {
                _logger.LogInformation("[ReactSync] 开始增量同步进货单");
                var result =
                    await _incrementalSyncService.SyncStoreLocalSupplierInvoicesFromHqIncrementalAsync(
                        request?.StartDate
                    );
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "[ReactSync] 进货单增量同步成功: 新增{Added}, 更新{Updated}",
                        result.AddedCount,
                        result.UpdatedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "进货单增量同步成功"));
                }
                return CreateSyncFailureResult(result, "进货单增量同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 进货单增量同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("进货单增量同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 增量同步货柜主表：HQ → 本地 Container
        /// 基于最近一次成功同步的时间点，默认100天内
        /// </summary>
        [HttpPost("containers-incremental")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncContainersIncremental(
            [FromBody] IncrementalSyncRequest? request = null
        )
        {
            try
            {
                _logger.LogInformation("[ReactSync] 开始增量同步货柜");
                var result = await _incrementalSyncService.SyncContainersFromHqIncrementalAsync(
                    request?.StartDate
                );
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "[ReactSync] 货柜增量同步成功: 新增{Added}, 更新{Updated}",
                        result.AddedCount,
                        result.UpdatedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "货柜增量同步成功"));
                }
                return CreateSyncFailureResult(result, "货柜增量同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 货柜增量同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("货柜增量同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 增量同步货柜详情：HQ → 本地 ContainerDetail
        /// 基于最近一次成功同步的时间点，默认100天内，支持主表GUID筛选
        /// </summary>
        [HttpPost("container-details-incremental")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncContainerDetailsIncremental(
            [FromBody] ContainerSyncRequest? request = null
        )
        {
            try
            {
                var result =
                    await _incrementalSyncService.SyncContainerDetailsFromHqIncrementalAsync(
                        request?.SelectedMasterGuids,
                        request?.StartDate
                    );
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "[ReactSync] 货柜详情增量同步成功: 新增{Added}, 更新{Updated}",
                        result.AddedCount,
                        result.UpdatedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "货柜详情增量同步成功"));
                }
                return CreateSyncFailureResult(result, "货柜详情增量同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 货柜详情增量同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("货柜详情增量同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 增量同步仓库订单主表：HQ → 本地 WareHouseOrder
        /// 基于最近一次成功同步的时间点，默认100天内
        /// </summary>
        [HttpPost("warehouse-orders-incremental")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncWareHouseOrdersIncremental(
            [FromBody] IncrementalSyncRequest? request = null
        )
        {
            try
            {
                _logger.LogInformation("[ReactSync] 开始增量同步仓库订单");
                var result =
                    await _incrementalSyncService.SyncWareHouseOrdersFromHqIncrementalAsync(
                        request?.StartDate
                    );
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "[ReactSync] 仓库订单增量同步成功: 新增{Added}, 更新{Updated}",
                        result.AddedCount,
                        result.UpdatedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "仓库订单增量同步成功"));
                }
                return CreateSyncFailureResult(result, "仓库订单增量同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 仓库订单增量同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("仓库订单增量同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 增量同步商品信息：HQ 商品字典表 → 本地 Product
        /// 基于最近一次成功同步的时间点，默认100天内
        /// </summary>
        [HttpPost("products-incremental")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncProductsIncremental(
            [FromBody] IncrementalSyncRequest? request = null
        )
        {
            try
            {
                _logger.LogInformation("[ReactSync] 开始增量同步商品信息");
                var productResult = await _productHqSyncService.SyncIncrementalAsync(
                    request?.StartDate,
                    _currentUserService.GetCurrentUserGuid(),
                    _currentUserService.GetCurrentUsername()
                );
                var result = ToSyncResult(
                    productResult.Data,
                    productResult.Success,
                    productResult.Message,
                    productResult.ErrorCode
                );
                if (productResult.Success)
                {
                    _logger.LogInformation(
                        "[ReactSync] 商品信息增量同步成功: 新增{Added}, 更新{Updated}",
                        result.AddedCount,
                        result.UpdatedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "商品信息增量同步成功"));
                }
                return CreateSyncFailureResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 商品信息增量同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("商品信息增量同步异常", "INTERNAL_ERROR")
                );
            }
        }

        private static SyncResult ToSyncResult(
            HqProductSyncResult? source,
            bool success,
            string message,
            string? errorCode
        )
        {
            var result = new SyncResult
            {
                IsSuccess = success,
                Message = message,
                ErrorCode = errorCode,
                StartTime = DateTime.UtcNow,
                EndTime = DateTime.UtcNow,
                AddedCount = source?.ProductsAdded ?? 0,
                UpdatedCount = source?.ProductsUpdated ?? 0,
                DeletedCount = source?.ProductsSoftDeleted ?? 0,
                ErrorCount = source?.Errors.Count ?? (success ? 0 : 1),
                TotalCount =
                    (source?.ProductsAdded ?? 0)
                    + (source?.ProductsUpdated ?? 0)
                    + (source?.ProductsSoftDeleted ?? 0),
            };
            if (!success && errorCode == SetChildPurchasePriceMutationLock.BusyErrorCode)
            {
                result.BusyErrorCount = result.ErrorCount;
            }
            result.Duration = TimeSpan.FromMilliseconds(source?.DurationMs ?? 0);
            return result;
        }

        private IActionResult CreateSyncResponse(SyncResult result, string successMessage)
        {
            return result.IsSuccess
                ? Ok(ApiResponse<SyncResult>.OK(result, successMessage))
                : CreateSyncFailureResult(result);
        }

        private IActionResult CreateSyncFailureResult(
            SyncResult result,
            string? failureMessage = null
        )
        {
            result.IsSuccess = false;
            if (result.ErrorCount <= 0)
            {
                result.ErrorCount = 1;
            }

            var isContainerMutationBusy =
                result.ErrorCode == ContainerMutationLock.BusyErrorCode;
            var isSetChildPurchasePriceBusy =
                result.ErrorCode == SetChildPurchasePriceMutationLock.BusyErrorCode;

            // 兼容尚未填充 BusyErrorCount 的旧服务结果，同时把计数约束在 ErrorCount 内。
            if (
                (isSetChildPurchasePriceBusy || isContainerMutationBusy)
                && result.BusyErrorCount == 0
            )
            {
                result.BusyErrorCount = result.ErrorCount;
            }
            result.BusyErrorCount = Math.Clamp(result.BusyErrorCount, 0, result.ErrorCount);

            var committedCount = result.AddedCount + result.UpdatedCount + result.DeletedCount;
            var allBusyWithoutCommit =
                committedCount == 0
                && result.BusyErrorCount > 0
                && result.BusyErrorCount == result.ErrorCount;
            var responseErrorCode = allBusyWithoutCommit
                ? isContainerMutationBusy
                    ? ContainerMutationLock.BusyErrorCode
                    : SetChildPurchasePriceMutationLock.BusyErrorCode
                : committedCount > 0
                    ? "PARTIAL_FAILURE"
                    : result.BusyErrorCount > 0 && result.BusyErrorCount < result.ErrorCount
                        ? "SYNC_FAILED"
                        : result.ErrorCode ?? "SYNC_FAILED";
            var response = ApiResponse<SyncResult>.FailWithData(
                result,
                failureMessage ?? result.Message,
                responseErrorCode,
                result.Details
            );

            // 只有全部错误均为锁竞争且没有任何已提交写入时返回 409。
            if (allBusyWithoutCommit && HttpContext != null)
            {
                Response.Headers.RetryAfter = "1";
            }
            return allBusyWithoutCommit ? Conflict(response) : Ok(response);
        }

        /// <summary>
        /// 增量同步分店零售价：DIC_商品零售价表 → 本地 StoreRetailPrice
        /// 基于最近一次成功同步的时间点，默认100天内，支持分店筛选
        /// </summary>
        [HttpPost("store-retail-prices-incremental")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncStoreRetailPricesIncremental(
            [FromBody] ReactStoreSyncRequest? request = null
        )
        {
            try
            {
                _logger.LogInformation("[ReactSync] 开始增量同步分店零售价");
                var result =
                    await _incrementalSyncService.SyncStoreRetailPricesFromHqIncrementalAsync(
                        request?.SelectedStoreCodes,
                        request?.StartDate
                    );
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "[ReactSync] 分店零售价增量同步成功: 新增{Added}, 更新{Updated}",
                        result.AddedCount,
                        result.UpdatedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "分店零售价增量同步成功"));
                }
                return CreateSyncFailureResult(result, "分店零售价增量同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 分店零售价增量同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("分店零售价增量同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 增量同步分店一品多码：DIC_分店一品多码表 → 本地 StoreMultiCodeProduct
        /// 基于最近一次成功同步的时间点，默认100天内，支持分店筛选
        /// </summary>
        [HttpPost("store-multi-code-products-incremental")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncStoreMultiCodeProductsIncremental(
            [FromBody] ReactStoreSyncRequest? request = null
        )
        {
            try
            {
                _logger.LogInformation("[ReactSync] 开始增量同步分店一品多码");
                var result =
                    await _incrementalSyncService.SyncStoreMultiCodeProductsFromHqIncrementalAsync(
                        request?.SelectedStoreCodes,
                        request?.StartDate
                    );
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "[ReactSync] 分店一品多码增量同步成功: 新增{Added}, 更新{Updated}",
                        result.AddedCount,
                        result.UpdatedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "分店一品多码增量同步成功"));
                }
                return CreateSyncFailureResult(result, "分店一品多码增量同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 分店一品多码增量同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("分店一品多码增量同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 增量同步套装多码：DIC_一品多码表 → 本地 ProductSetCode
        /// 基于最近一次成功同步的时间点，默认100天内
        /// </summary>
        [HttpPost("product-set-codes-incremental")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncProductSetCodesIncremental(
            [FromBody] IncrementalSyncRequest? request = null
        )
        {
            try
            {
                _logger.LogInformation("[ReactSync] 开始增量同步套装多码");
                var result =
                    await _incrementalSyncService.SyncProductSetCodesFromHqIncrementalAsync(
                        request?.StartDate
                    );
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "[ReactSync] 套装多码增量同步成功: 新增{Added}, 更新{Updated}",
                        result.AddedCount,
                        result.UpdatedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "套装多码增量同步成功"));
                }
                return CreateSyncFailureResult(result, "套装多码增量同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 套装多码增量同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("套装多码增量同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 增量同步分店清货价：DIC_商品清货价表 → 本地 StoreClearancePrice
        /// 基于最近一次成功同步的时间点，默认100天内，支持分店筛选
        /// </summary>
        [HttpPost("store-clearance-prices-incremental")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncStoreClearancePricesIncremental(
            [FromBody] ReactStoreSyncRequest? request = null
        )
        {
            try
            {
                _logger.LogInformation("[ReactSync] 开始增量同步分店清货价");
                var result =
                    await _incrementalSyncService.SyncStoreClearancePricesFromHqIncrementalAsync(
                        request?.SelectedStoreCodes,
                        request?.StartDate
                    );
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "[ReactSync] 分店清货价增量同步成功: 新增{Added}, 更新{Updated}",
                        result.AddedCount,
                        result.UpdatedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "分店清货价增量同步成功"));
                }
                return CreateSyncFailureResult(result, "分店清货价增量同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 分店清货价增量同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("分店清货价增量同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 增量同步国货商品：CPT_DIC_商品信息字典表 → 本地 DomesticProduct
        /// 基于最近一次成功同步的时间点，默认100天内
        /// </summary>
        [HttpPost("domestic-products-incremental")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncDomesticProductsIncremental(
            [FromBody] IncrementalSyncRequest? request = null
        )
        {
            try
            {
                _logger.LogInformation("[ReactSync] 开始增量同步国货商品");
                var result =
                    await _incrementalSyncService.SyncDomesticProductsFromHqIncrementalAsync(
                        request?.StartDate
                    );
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "[ReactSync] 国货商品增量同步成功: 新增{Added}, 更新{Updated}",
                        result.AddedCount,
                        result.UpdatedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "国货商品增量同步成功"));
                }
                return CreateSyncFailureResult(result, "国货商品增量同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 国货商品增量同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("国货商品增量同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 增量同步国货套装：CPT_DIC_商品套装信息表 → 本地 DomesticSetProduct
        /// 基于最近一次成功同步的时间点，默认100天内
        /// </summary>
        [HttpPost("domestic-set-products-incremental")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncDomesticSetProductsIncremental(
            [FromBody] IncrementalSyncRequest? request = null
        )
        {
            try
            {
                _logger.LogInformation("[ReactSync] 开始增量同步国货套装");
                var result =
                    await _incrementalSyncService.SyncDomesticSetProductsFromHqIncrementalAsync(
                        request?.StartDate
                    );
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "[ReactSync] 国货套装增量同步成功: 新增{Added}, 更新{Updated}",
                        result.AddedCount,
                        result.UpdatedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "国货套装增量同步成功"));
                }
                return CreateSyncFailureResult(result, "国货套装增量同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 国货套装增量同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("国货套装增量同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 增量同步商品前缀码：DIC_商品前缀码表 → 本地 ProductPrefixCode
        /// 基于最近一次成功同步的时间点，默认100天内
        /// </summary>
        [HttpPost("product-prefix-codes-incremental")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncProductPrefixCodesIncremental(
            [FromBody] IncrementalSyncRequest? request = null
        )
        {
            try
            {
                _logger.LogInformation("[ReactSync] 开始增量同步商品前缀码");
                var result =
                    await _incrementalSyncService.SyncProductPrefixCodesFromHqIncrementalAsync(
                        request?.StartDate
                    );
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "[ReactSync] 商品前缀码增量同步成功: 新增{Added}, 更新{Updated}",
                        result.AddedCount,
                        result.UpdatedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "商品前缀码增量同步成功"));
                }
                return CreateSyncFailureResult(result, "商品前缀码增量同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 商品前缀码增量同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("商品前缀码增量同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 增量同步国内供应商：CBP_DIC_国内供应商信息表 → 本地 ChinaSupplier
        /// 基于最近一次成功同步的时间点，默认100天内
        /// </summary>
        [HttpPost("china-suppliers-incremental")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncChinaSuppliersIncremental(
            [FromBody] IncrementalSyncRequest? request = null
        )
        {
            try
            {
                _logger.LogInformation("[ReactSync] 开始增量同步国内供应商");
                var result =
                    await _incrementalSyncService.SyncChinaSuppliersFromHqIncrementalAsync(
                        request?.StartDate
                    );
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "[ReactSync] 国内供应商增量同步成功: 新增{Added}, 更新{Updated}",
                        result.AddedCount,
                        result.UpdatedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "国内供应商增量同步成功"));
                }
                return CreateSyncFailureResult(result, "国内供应商增量同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 国内供应商增量同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("国内供应商增量同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 增量同步仓库分类：CBP_DIC_商品分类码表 → 本地 WarehouseCategory
        /// 基于最近一次成功同步的时间点，默认100天内
        /// </summary>
        [HttpPost("warehouse-categories-incremental")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncWarehouseCategoriesIncremental(
            [FromBody] IncrementalSyncRequest? request = null
        )
        {
            try
            {
                _logger.LogInformation("[ReactSync] 开始增量同步仓库分类");
                var result =
                    await _incrementalSyncService.SyncWarehouseCategoriesFromHqIncrementalAsync(
                        request?.StartDate
                    );
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "[ReactSync] 仓库分类增量同步成功: 新增{Added}, 更新{Updated}",
                        result.AddedCount,
                        result.UpdatedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "仓库分类增量同步成功"));
                }
                return CreateSyncFailureResult(result, "仓库分类增量同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 仓库分类增量同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("仓库分类增量同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 增量同步仓库商品：CBP_DIC_商品库存表 → 本地 WarehouseProduct
        /// 基于最近一次成功同步的时间点，默认100天内
        /// </summary>
        [HttpPost("warehouse-products-incremental")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncWarehouseProductsIncremental(
            [FromBody] IncrementalSyncRequest? request = null
        )
        {
            try
            {
                _logger.LogInformation("[ReactSync] 开始增量同步仓库商品");
                var result =
                    await _incrementalSyncService.SyncWarehouseProductsFromHqIncrementalAsync(
                        request?.StartDate
                    );
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "[ReactSync] 仓库商品增量同步成功: 新增{Added}, 更新{Updated}",
                        result.AddedCount,
                        result.UpdatedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "仓库商品增量同步成功"));
                }
                return CreateSyncFailureResult(result, "仓库商品增量同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 仓库商品增量同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("仓库商品增量同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 增量同步进货单详情：RED_进货单详情表Store → 本地 StoreLocalSupplierInvoiceDetails
        /// 基于最近一次成功同步的时间点，默认100天内
        /// </summary>
        [HttpPost("store-local-supplier-invoice-details-incremental")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncStoreLocalSupplierInvoiceDetailsIncremental(
            [FromBody] IncrementalSyncRequest? request = null
        )
        {
            try
            {
                _logger.LogInformation("[ReactSync] 开始增量同步进货单详情");
                var result =
                    await _incrementalSyncService.SyncStoreLocalSupplierInvoiceDetailsFromHqIncrementalAsync(
                        request?.StartDate
                    );
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "[ReactSync] 进货单详情增量同步成功: 新增{Added}, 更新{Updated}",
                        result.AddedCount,
                        result.UpdatedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "进货单详情增量同步成功"));
                }
                return CreateSyncFailureResult(result, "进货单详情增量同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 进货单详情增量同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("进货单详情增量同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 增量同步仓库订单详情：CBP_RED_分店订货单详情表Store → 本地 WareHouseOrderDetail
        /// 基于最近一次成功同步的时间点，默认100天内
        /// </summary>
        [HttpPost("warehouse-order-details-incremental")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncWareHouseOrderDetailsIncremental(
            [FromBody] IncrementalSyncRequest? request = null
        )
        {
            try
            {
                _logger.LogInformation("[ReactSync] 开始增量同步仓库订单详情");
                var result =
                    await _incrementalSyncService.SyncWareHouseOrderDetailsFromHqIncrementalAsync(
                        request?.StartDate
                    );
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "[ReactSync] 仓库订单详情增量同步成功: 新增{Added}, 更新{Updated}",
                        result.AddedCount,
                        result.UpdatedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "仓库订单详情增量同步成功"));
                }
                return CreateSyncFailureResult(result, "仓库订单详情增量同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 仓库订单详情增量同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("仓库订单详情增量同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 增量同步库位：CBP_DIC_货位表 → 本地 Location
        /// 基于最近一次成功同步的时间点，默认100天内
        /// </summary>
        [HttpPost("locations-incremental")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncLocationsIncremental(
            [FromBody] IncrementalSyncRequest? request = null
        )
        {
            try
            {
                _logger.LogInformation("[ReactSync] 开始增量同步库位");
                var result = await _incrementalSyncService.SyncLocationsFromHqIncrementalAsync(
                    request?.StartDate
                );
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "[ReactSync] 库位增量同步成功: 新增{Added}, 更新{Updated}",
                        result.AddedCount,
                        result.UpdatedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "库位增量同步成功"));
                }
                return CreateSyncFailureResult(result, "库位增量同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 库位增量同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("库位增量同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 增量同步商品库位：CBP_RED_商品货位表 → 本地 ProductLocation
        /// 基于最近一次成功同步的时间点，默认100天内
        /// </summary>
        [HttpPost("product-locations-incremental")]
        [Authorize(Roles = "Admin,WarehouseManager")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncProductLocationsIncremental(
            [FromBody] IncrementalSyncRequest? request = null
        )
        {
            try
            {
                _logger.LogInformation("[ReactSync] 开始增量同步商品库位");
                var result =
                    await _incrementalSyncService.SyncProductLocationsFromHqIncrementalAsync(
                        request?.StartDate
                    );
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "[ReactSync] 商品库位增量同步成功: 新增{Added}, 更新{Updated}",
                        result.AddedCount,
                        result.UpdatedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "商品库位增量同步成功"));
                }
                return CreateSyncFailureResult(result, "商品库位增量同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 商品库位增量同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("商品库位增量同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 增量同步收银用户：DIC_收银用户信息表 → 本地 CashRegisterUser
        /// 基于最近一次成功同步的时间点，默认100天内
        /// </summary>
        [HttpPost("cash-register-users-incremental")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncCashRegisterUsersIncremental(
            [FromBody] IncrementalSyncRequest? request = null
        )
        {
            try
            {
                _logger.LogInformation("[ReactSync] 开始增量同步收银用户");
                var result =
                    await _incrementalSyncService.SyncCashRegisterUsersFromHqIncrementalAsync(
                        request?.StartDate
                    );
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "[ReactSync] 收银用户增量同步成功: 新增{Added}, 更新{Updated}",
                        result.AddedCount,
                        result.UpdatedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "收银用户增量同步成功"));
                }
                return CreateSyncFailureResult(result, "收银用户增量同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 收银用户增量同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("收银用户增量同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 全量同步商品分类：HQ DIC_商品分类码表 → 本地 ProductCategory
        /// </summary>
        [HttpPost("product-categories")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncProductCategories(
            [FromBody] ReactStoreSyncRequest? request = null
        )
        {
            try
            {
                _logger.LogInformation("[ReactSync] 开始全量同步商品分类");
                var result = await _fullSyncService.SyncProductCategoriesFromHqAsync();
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "[ReactSync] 商品分类同步成功: 新增{Added}",
                        result.AddedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "商品分类同步成功"));
                }
                return CreateSyncFailureResult(result, "商品分类同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 商品分类同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("商品分类同步异常", "INTERNAL_ERROR")
                );
            }
        }

        /// <summary>
        /// 增量同步商品分类：HQ DIC_商品分类码表 → 本地 ProductCategory
        /// 按 FGC_LastModifyDate 字段增量同步
        /// </summary>
        [HttpPost("product-categories-incremental")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncProductCategoriesIncremental(
            [FromBody] IncrementalSyncRequest? request = null
        )
        {
            try
            {
                _logger.LogInformation("[ReactSync] 开始增量同步商品分类");
                var result =
                    await _incrementalSyncService.SyncProductCategoriesFromHqIncrementalAsync(
                        request?.StartDate
                    );
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "[ReactSync] 商品分类增量同步成功: 新增{Added}, 更新{Updated}",
                        result.AddedCount,
                        result.UpdatedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "商品分类增量同步成功"));
                }
                return CreateSyncFailureResult(result, "商品分类增量同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 商品分类增量同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("商品分类增量同步异常", "INTERNAL_ERROR")
                );
            }
        }

        // 原先误标 [AllowAnonymous]，未登录请求即可改写分店特殊商品标记；停用同时恢复为管理员授权。
        [HttpPost("special-product-from-hq")]
        [Authorize(Roles = "Admin")]
        [HqToHbwebSyncDisabled]
        public async Task<IActionResult> SyncSpecialProductFromHq(
            [FromBody] ReactStoreSyncRequest? request = null
        )
        {
            try
            {
                var storeInfo =
                    request?.SelectedStoreCodes?.Any() == true
                        ? string.Join(", ", request!.SelectedStoreCodes!)
                        : "全部分店";
                _logger.LogInformation("[ReactSync] 开始同步特殊商品标记：{StoreInfo}", storeInfo);
                var result = await _fullSyncService.SyncSpecialProductFromHqAsync(
                    request?.SelectedStoreCodes
                );
                if (result.IsSuccess)
                {
                    _logger.LogInformation(
                        "[ReactSync] 特殊商品标记同步成功: 更新{Updated}",
                        result.UpdatedCount
                    );
                    return Ok(ApiResponse<SyncResult>.OK(result, "特殊商品标记同步成功"));
                }
                return CreateSyncFailureResult(result, "特殊商品标记同步完成，但存在错误");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 特殊商品标记同步异常");
                return StatusCode(
                    500,
                    ApiResponse<SyncResult>.Error("特殊商品标记同步异常", "INTERNAL_ERROR")
                );
            }
        }
    }
}

public class ReactStoreSyncRequest
{
    public List<string>? SelectedStoreCodes { get; set; }
    public DateTime? StartDate { get; set; }
}
