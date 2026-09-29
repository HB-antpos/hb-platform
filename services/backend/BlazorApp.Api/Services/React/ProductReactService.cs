using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.LocalSupplierCategories;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HqEntities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// Product React专用服务实现
    /// 提供Product表的CRUD操作、分页查询、排序和过滤功能
    /// </summary>
    public class ProductReactService : IProductReactService
    {
        private readonly ISqlSugarClient _db;
        private readonly HqSqlSugarContext _hqContext;
        private readonly IMapper _mapper;
        private readonly ILogger<ProductReactService> _logger;
        private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor _httpContextAccessor;
        private readonly IWarehouseProductChangeHistoryService _changeHistoryService;
        private readonly ICurrentUserService _currentUserService;
        private const int ProductImageMaxLength = 200;
        private const int BatchImageMaxErrorDetails = 100;
        private const int ProductHqSyncReadBatchSize = 1000;
        private const int ProductHqSyncWriteBatchSize = 200;
        private const string ProductHqSyncLockResource = "HB:ProductReactService:SyncProductsFromHq";
        private static readonly SemaphoreSlim ProductHqSyncSemaphore = new(1, 1);
        private const string DefaultLocalSupplierCode = "200";

        public ProductReactService(
            SqlSugarContext context,
            HqSqlSugarContext hqContext,
            IMapper mapper,
            ILogger<ProductReactService> logger,
            Microsoft.AspNetCore.Http.IHttpContextAccessor httpContextAccessor,
            IWarehouseProductChangeHistoryService changeHistoryService,
            ICurrentUserService currentUserService
        )
        {
            _db = context.Db;
            _hqContext = hqContext;
            _mapper = mapper;
            _logger = logger;
            _httpContextAccessor = httpContextAccessor;
            _changeHistoryService = changeHistoryService;
            _currentUserService = currentUserService;
        }

        /// <summary>
        /// 商品列表查询投影，承接分页前的分店记录数量筛选和排序。
        /// </summary>
        private sealed class ProductListQueryRow
        {
            public string ProductCode { get; set; } = string.Empty;
            public string ProductCategoryGUID { get; set; } = string.Empty;
            public string? LocalSupplierCode { get; set; }
            public string? DomesticSupplierCode { get; set; }
            public string? DomesticSupplierName { get; set; }
            public string? ItemNumber { get; set; }
            public string? Barcode { get; set; }
            public string? ProductName { get; set; }
            public int? ProductType { get; set; }
            public int? MiddlePackageQuantity { get; set; }
            public decimal? PurchasePrice { get; set; }
            public decimal? RetailPrice { get; set; }
            public bool IsAutoPricing { get; set; }
            public string? ProductImage { get; set; }
            public bool IsActive { get; set; }
            public bool IsSpecialProduct { get; set; }
            public string? WarehouseCategoryGUID { get; set; }
            public DateTime? CreatedAt { get; set; }
            public DateTime? UpdatedAt { get; set; }
            public string? UpdatedBy { get; set; }
            public int StoreRecordCount { get; set; }
        }

        private static string NormalizeLocalSupplierCode(string? value)
        {
            // 本地商品未选择供应商时统一归到默认供应商 200，避免空值进入商品和分店价格链路。
            return string.IsNullOrWhiteSpace(value) ? DefaultLocalSupplierCode : value.Trim();
        }

        private async Task<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>
            CaptureProductSnapshotsAsync(
                IEnumerable<string> productCodes,
                CancellationToken cancellationToken = default
            )
        {
            return await _changeHistoryService.CaptureSnapshotsAsync(
                productCodes,
                cancellationToken
            );
        }

        private async Task RecordProductChangesAsync(
            IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto> beforeSnapshots,
            IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto> afterSnapshots,
            string action,
            string source,
            Guid batchGuid,
            DateTime occurredAtUtc,
            string? actorUserGuid = null,
            string? actorName = null,
            CancellationToken cancellationToken = default
        )
        {
            // 未显式传入服务端身份时读取当前请求；真正计划任务通过 actorName=System 显式标记。
            var resolvedActorName = string.IsNullOrWhiteSpace(actorName)
                ? _currentUserService.GetCurrentUsername()
                : actorName.Trim();
            if (string.IsNullOrWhiteSpace(resolvedActorName))
            {
                resolvedActorName = "System";
            }

            var resolvedActorGuid = string.IsNullOrWhiteSpace(actorUserGuid)
                ? _currentUserService.GetCurrentUserGuid()
                : actorUserGuid.Trim();
            var isSystem = string.IsNullOrWhiteSpace(resolvedActorGuid)
                && string.Equals(
                    resolvedActorName,
                    "System",
                    StringComparison.OrdinalIgnoreCase
                );
            await _changeHistoryService.RecordChangesAsync(
                beforeSnapshots,
                afterSnapshots,
                new WarehouseProductChangeHistoryContextDto
                {
                    Action = action,
                    Source = source,
                    BatchGuid = batchGuid,
                    ActorUserGuid = string.IsNullOrWhiteSpace(resolvedActorGuid)
                        ? null
                        : resolvedActorGuid,
                    ActorName = resolvedActorName,
                    ActorType = isSystem ? "System" : "User",
                    OccurredAtUtc = occurredAtUtc,
                },
                cancellationToken
            );
        }

        private static IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>
            RebindProductSnapshot(
                IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto> snapshots,
                string oldProductCode,
                string newProductCode
            )
        {
            if (
                string.Equals(oldProductCode, newProductCode, StringComparison.OrdinalIgnoreCase)
                || !snapshots.TryGetValue(oldProductCode, out var snapshot)
            )
            {
                return snapshots;
            }

            // 事件归到新编码，但保留旧快照中的编码值，让字段差异明确显示 old -> new。
            return new Dictionary<string, WarehouseProductChangeSnapshotDto>(
                new[]
                {
                    new KeyValuePair<string, WarehouseProductChangeSnapshotDto>(
                        newProductCode,
                        snapshot
                    ),
                },
                StringComparer.OrdinalIgnoreCase
            );
        }

        /// <summary>
        /// 商品分店记录数预聚合结果，避免商品列表分页时对分店价格表逐行相关计数。
        /// </summary>
        private sealed class ProductStoreRecordCountRow
        {
            public string ProductCode { get; set; } = string.Empty;
            public int StoreRecordCount { get; set; }
        }

        /// <summary>
        /// 国内供应商映射行：每个 ProductCode 至多一行，保证映射唯一性不产生重复商品行。
        /// </summary>
        private sealed class DomesticProductMappingRow
        {
            public string ProductCode { get; set; } = string.Empty;
            public string? DomesticSupplierCode { get; set; }
            public string? DomesticSupplierName { get; set; }
        }

        #region 筛选辅助方法

        /// <summary>
        /// 应用文本筛选到查询
        /// </summary>
        private void ApplyTextFilter(
            ref ISugarQueryable<Product> query,
            string? value,
            TextFilterType filterType,
            string fieldName
        )
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            var keyword = value.Trim().ToLower();
            var paramName = $"p_{fieldName}";

            switch (filterType)
            {
                case TextFilterType.equals:
                    query = query.Where(
                        $"LOWER({fieldName}) = @{paramName}",
                        new Dictionary<string, object> { { paramName, keyword } }
                    );
                    break;
                case TextFilterType.notEquals:
                    query = query.Where(
                        $"LOWER({fieldName}) != @{paramName}",
                        new Dictionary<string, object> { { paramName, keyword } }
                    );
                    break;
                case TextFilterType.startsWith:
                    query = query.Where(
                        $"LOWER({fieldName}) LIKE @{paramName}",
                        new Dictionary<string, object> { { paramName, keyword + "%" } }
                    );
                    break;
                case TextFilterType.endsWith:
                    query = query.Where(
                        $"LOWER({fieldName}) LIKE @{paramName}",
                        new Dictionary<string, object> { { paramName, "%" + keyword } }
                    );
                    break;
                case TextFilterType.notContains:
                    query = query.Where(
                        $"LOWER({fieldName}) NOT LIKE @{paramName}",
                        new Dictionary<string, object> { { paramName, "%" + keyword + "%" } }
                    );
                    break;
                case TextFilterType.contains:
                default:
                    query = query.Where(
                        $"LOWER({fieldName}) LIKE @{paramName}",
                        new Dictionary<string, object> { { paramName, "%" + keyword + "%" } }
                    );
                    break;
            }
        }

        /// <summary>
        /// 应用数字范围筛选到查询（decimal类型）
        /// </summary>
        private void ApplyNumberFilter(
            ref ISugarQueryable<Product> query,
            decimal? minValue,
            decimal? maxValue,
            NumberFilterType filterType,
            string fieldName
        )
        {
            if (!minValue.HasValue && !maxValue.HasValue)
                return;

            switch (filterType)
            {
                case NumberFilterType.equals when minValue.HasValue:
                    query = query.Where($"{fieldName} = @value", new Dictionary<string, object> { { "value", minValue.Value } });
                    break;
                case NumberFilterType.notEquals when minValue.HasValue:
                    query = query.Where($"{fieldName} != @value", new Dictionary<string, object> { { "value", minValue.Value } });
                    break;
                case NumberFilterType.greaterThan when minValue.HasValue:
                    query = query.Where($"{fieldName} > @value", new Dictionary<string, object> { { "value", minValue.Value } });
                    break;
                case NumberFilterType.greaterThanOrEqual when minValue.HasValue:
                    query = query.Where($"{fieldName} >= @value", new Dictionary<string, object> { { "value", minValue.Value } });
                    break;
                case NumberFilterType.lessThan when minValue.HasValue:
                    query = query.Where($"{fieldName} < @value", new Dictionary<string, object> { { "value", minValue.Value } });
                    break;
                case NumberFilterType.lessThanOrEqual when minValue.HasValue:
                    query = query.Where($"{fieldName} <= @value", new Dictionary<string, object> { { "value", minValue.Value } });
                    break;
                case NumberFilterType.between:
                    if (minValue.HasValue)
                        query = query.Where($"{fieldName} >= @minValue", new Dictionary<string, object> { { "minValue", minValue.Value } });
                    if (maxValue.HasValue)
                        query = query.Where($"{fieldName} <= @maxValue", new Dictionary<string, object> { { "maxValue", maxValue.Value } });
                    break;
            }
        }

        /// <summary>
        /// 应用数字范围筛选到查询（int类型）
        /// </summary>
        private void ApplyNumberFilter(
            ref ISugarQueryable<Product> query,
            int? minValue,
            int? maxValue,
            NumberFilterType filterType,
            string fieldName
        )
        {
            if (!minValue.HasValue && !maxValue.HasValue)
                return;

            switch (filterType)
            {
                case NumberFilterType.equals when minValue.HasValue:
                    query = query.Where($"{fieldName} = @value", new Dictionary<string, object> { { "value", minValue.Value } });
                    break;
                case NumberFilterType.notEquals when minValue.HasValue:
                    query = query.Where($"{fieldName} != @value", new Dictionary<string, object> { { "value", minValue.Value } });
                    break;
                case NumberFilterType.greaterThan when minValue.HasValue:
                    query = query.Where($"{fieldName} > @value", new Dictionary<string, object> { { "value", minValue.Value } });
                    break;
                case NumberFilterType.greaterThanOrEqual when minValue.HasValue:
                    query = query.Where($"{fieldName} >= @value", new Dictionary<string, object> { { "value", minValue.Value } });
                    break;
                case NumberFilterType.lessThan when minValue.HasValue:
                    query = query.Where($"{fieldName} < @value", new Dictionary<string, object> { { "value", minValue.Value } });
                    break;
                case NumberFilterType.lessThanOrEqual when minValue.HasValue:
                    query = query.Where($"{fieldName} <= @value", new Dictionary<string, object> { { "value", minValue.Value } });
                    break;
                case NumberFilterType.between:
                    if (minValue.HasValue)
                        query = query.Where($"{fieldName} >= @minValue", new Dictionary<string, object> { { "minValue", minValue.Value } });
                    if (maxValue.HasValue)
                        query = query.Where($"{fieldName} <= @maxValue", new Dictionary<string, object> { { "maxValue", maxValue.Value } });
                    break;
            }
        }

        /// <summary>
        /// 应用日期范围筛选；结束时间按“不包含”处理，前端传入次日零点即可覆盖整天。
        /// </summary>
        private void ApplyDateRangeFilter(
            ref ISugarQueryable<Product> query,
            DateTime? from,
            DateTime? toExclusive,
            string fieldName
        )
        {
            if (!from.HasValue && !toExclusive.HasValue)
                return;

            if (from.HasValue)
                query = query.Where($"{fieldName} >= @from", new Dictionary<string, object> { { "from", from.Value } });
            if (toExclusive.HasValue)
                query = query.Where($"{fieldName} < @toExclusive", new Dictionary<string, object> { { "toExclusive", toExclusive.Value } });
        }

        private static List<T> NormalizeColumnFilterValues<T>(IEnumerable<T>? values)
        {
            return values?.Distinct().ToList() ?? new List<T>();
        }

        #endregion

        /// <summary>
        /// 分页查询商品列表（支持排序和过滤）
        /// </summary>
        public async Task<PagedListReactDto<ProductDto>> GetPagedListAsync(
            ProductReactFilterDto query
        )
        {
            try
            {
                var q = _db.Queryable<Product>();

                // 应用过滤条件
                if (!string.IsNullOrWhiteSpace(query.Search))
                {
                    if (_db.CurrentConnectionConfig.DbType == DbType.SqlServer)
                    {
                        // Product 约 17 万行，CI 排序规则下多列前导通配 LIKE 单次约 1.4 秒，
                        // 且计数与取页各执行一次；UPPER + BIN2 逐字节比较约 0.2 秒。
                        // 谓词与通配符转义复用本地商品销售分析已在生产验证过的实现，
                        // 覆盖编码、货号、条码、中英文名称，与页面搜索框的提示文案一致。
                        q = q.Where(
                            LocalSupplierProductSalesAnalysisService.SqlServerKeywordPredicate,
                            new
                            {
                                lspaKeywordPattern =
                                    LocalSupplierProductSalesAnalysisService.BuildSqlServerLikePattern(
                                        query.Search.Trim().ToUpperInvariant()
                                    ),
                            }
                        );
                    }
                    else
                    {
                        // 非 SQL Server（测试用 SQLite）保留 lambda 写法，匹配列与上面保持一致。
                        var keyword = query.Search.Trim().ToLower();
                        q = q.Where(p =>
                            (p.ProductName != null && p.ProductName.ToLower().Contains(keyword))
                            || (p.EnglishName != null && p.EnglishName.ToLower().Contains(keyword))
                            || (p.ProductCode != null && p.ProductCode.ToLower().Contains(keyword))
                            || (p.ItemNumber != null && p.ItemNumber.ToLower().Contains(keyword))
                            || (p.Barcode != null && p.Barcode.ToLower().Contains(keyword))
                        );
                    }
                }

                if (!string.IsNullOrWhiteSpace(query.LocalSupplierCode))
                {
                    q = q.Where(p => p.LocalSupplierCode == query.LocalSupplierCode);
                }
                else
                {
                    var supplierCodes = NormalizeColumnFilterValues(query.LocalSupplierCodes)
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Select(code => code.Trim())
                        .ToList();
                    if (supplierCodes.Count > 0)
                    {
                        q = q.Where(p => p.LocalSupplierCode != null && supplierCodes.Contains(p.LocalSupplierCode));
                    }
                }

                if (query.IsActive.HasValue)
                {
                    q = q.Where(p => p.IsActive == query.IsActive.Value);
                }
                else
                {
                    var isActiveValues = NormalizeColumnFilterValues(query.IsActiveValues);
                    if (isActiveValues.Count == 1)
                    {
                        q = q.Where(p => p.IsActive == isActiveValues[0]);
                    }
                }

                var isAutoPricingValues = NormalizeColumnFilterValues(query.IsAutoPricingValues);
                if (isAutoPricingValues.Count == 1)
                {
                    q = q.Where(p => p.IsAutoPricing == isAutoPricingValues[0]);
                }

                if (query.IsSpecialProduct.HasValue)
                {
                    q = q.Where(p => p.IsSpecialProduct == query.IsSpecialProduct.Value);
                }

                var warehouseCategoryGuids = NormalizeColumnFilterValues(query.WarehouseCategoryGUIDs)
                    .Where(guid => !string.IsNullOrWhiteSpace(guid))
                    .Select(guid => guid.Trim())
                    .ToList();
                if (warehouseCategoryGuids.Count > 0)
                {
                    // 复数过滤有值时优先；未设置仓库分类的商品不参与匹配。
                    q = q.Where(p =>
                        p.WarehouseCategoryGUID != null
                        && warehouseCategoryGuids.Contains(p.WarehouseCategoryGUID)
                    );
                }
                else if (!string.IsNullOrWhiteSpace(query.WarehouseCategoryGUID))
                {
                    q = q.Where(p => p.WarehouseCategoryGUID == query.WarehouseCategoryGUID);
                }

                if (query.ProductCategoryGUIDs != null && query.ProductCategoryGUIDs.Count > 0)
                {
                    q = q.Where(p =>
                        p.ProductCategoryGUID != null
                        && query.ProductCategoryGUIDs.Contains(p.ProductCategoryGUID)
                    );
                }

                q = await ApplySupplierCategoryFiltersAsync(q, query);

                if (query.ProductType.HasValue)
                {
                    if (query.ProductType.Value == 0)
                    {
                        // 顶部“单品”筛选需要与列头“普通商品”保持一致：历史空值也视为普通商品。
                        q = q.Where(p => SqlFunc.IsNull(p.ProductType, 0) == 0);
                    }
                    else
                    {
                        q = q.Where(p => p.ProductType == query.ProductType.Value);
                    }
                }
                else
                {
                    var productTypeValues = NormalizeColumnFilterValues(query.ProductTypeValues)
                        .Select(value => (int?)value)
                        .ToList();
                    if (productTypeValues.Count > 0)
                    {
                        // ProductType 为空时页面按“普通商品”展示，筛选普通商品时必须一并纳入。
                        q = q.Where(p => productTypeValues.Contains(SqlFunc.IsNull(p.ProductType, 0)));
                    }
                }

                #region 文本字段高级筛选

                ApplyTextFilter(ref q, query.ProductCode, query.ProductCodeFilterType, "ProductCode");
                ApplyTextFilter(ref q, query.ItemNumber, query.ItemNumberFilterType, "ItemNumber");
                ApplyTextFilter(ref q, query.Barcode, query.BarcodeFilterType, "Barcode");
                ApplyTextFilter(
                    ref q,
                    query.ProductName,
                    query.ProductNameFilterType,
                    "ProductName"
                );
                ApplyTextFilter(ref q, query.UpdatedBy, query.UpdatedByFilterType, "UpdatedBy");

                #endregion

                #region 日期字段高级筛选

                ApplyDateRangeFilter(ref q, query.CreatedAtFrom, query.CreatedAtToExclusive, "CreatedAt");
                ApplyDateRangeFilter(ref q, query.UpdatedAtFrom, query.UpdatedAtToExclusive, "UpdatedAt");

                #endregion

                #region 数字字段高级筛选

                ApplyNumberFilter(
                    ref q,
                    query.PurchasePriceMin,
                    query.PurchasePriceMax,
                    query.PurchasePriceFilterType,
                    "PurchasePrice"
                );
                ApplyNumberFilter(
                    ref q,
                    query.RetailPriceMin,
                    query.RetailPriceMax,
                    query.RetailPriceFilterType,
                    "RetailPrice"
                );
                ApplyNumberFilter(
                    ref q,
                    query.MiddlePackageQuantityMin,
                    query.MiddlePackageQuantityMax,
                    query.MiddlePackageQuantityFilterType,
                    "MiddlePackageQuantity"
                );

                #endregion

                var domesticSupplierCodes = NormalizeColumnFilterValues(query.DomesticSupplierCodes)
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code.Trim())
                    .ToList();

                // 关键位置：分店记录数与国内供应商只有在参与筛选或排序时，才必须在分页前对全表预聚合。
                // 其余请求（含无筛选打开页面）先在 Product 单表上计数、排序、取页，
                // 再只为当前页的商品补这两项，避免每次请求都把 StoreRetailPrice 整表聚合两遍。
                var sortFieldForRouting = query.SortBy?.ToLower();
                var requiresPrePagingAggregates =
                    domesticSupplierCodes.Count > 0
                    || query.StoreRecordCountMin.HasValue
                    || query.StoreRecordCountMax.HasValue
                    || sortFieldForRouting == "domesticsuppliercode"
                    || sortFieldForRouting == "storerecordcount"
                    || query.PageSize > MaxPageSizeForPageScopedAggregates;
                if (!requiresPrePagingAggregates)
                {
                    return await GetPagedListWithPageScopedAggregatesAsync(q, query);
                }

                var storeRecordCounts = _db.Queryable<StoreRetailPrice>()
                    .Where(record => record.IsDeleted == false && record.ProductCode != null)
                    .GroupBy(record => record.ProductCode)
                    .Select(record => new ProductStoreRecordCountRow
                    {
                        ProductCode = record.ProductCode ?? string.Empty,
                        StoreRecordCount = SqlFunc.AggregateCount(record.UUID),
                    })
                    .MergeTable();

                // 国内供应商映射：DomesticProduct 和 ChinaSupplier 都必须有效；
                // Product 在外层左连接该映射，因此无映射、任一端软删或供应商不存在时商品仍保留。
                // 按 ProductCode 聚合可避免重复供应商资料造成重复商品行。
                var domesticMappings = _db.Queryable<DomesticProduct>()
                    .InnerJoin<ChinaSupplier>(
                        (dp, cs) => dp.SupplierCode == cs.SupplierCode && cs.IsDeleted == false
                    )
                    .Where((dp, cs) => dp.IsDeleted == false)
                    .GroupBy(dp => dp.ProductCode)
                    .Select(
                        (dp, cs) =>
                            new DomesticProductMappingRow
                            {
                                ProductCode = dp.ProductCode,
                                DomesticSupplierCode = SqlFunc.AggregateMax(dp.SupplierCode),
                                DomesticSupplierName = SqlFunc.AggregateMax(cs.SupplierName),
                            }
                    )
                    .MergeTable();

                // 关键位置：分店记录数必须来自预聚合结果，避免分页、筛选和排序反复触发逐行相关计数。
                var projectedQuery = q
                    .LeftJoin(storeRecordCounts, (p, count) => p.ProductCode == count.ProductCode)
                    .LeftJoin(
                        domesticMappings,
                        (p, count, domestic) => p.ProductCode == domestic.ProductCode
                    )
                    .Select((p, count, domestic) => new ProductListQueryRow
                    {
                        ProductCode = p.ProductCode ?? string.Empty,
                        ProductCategoryGUID = p.ProductCategoryGUID ?? string.Empty,
                        LocalSupplierCode = p.LocalSupplierCode,
                        DomesticSupplierCode = domestic.DomesticSupplierCode,
                        DomesticSupplierName = domestic.DomesticSupplierName,
                        ItemNumber = p.ItemNumber,
                        Barcode = p.Barcode,
                        ProductName = p.ProductName ?? string.Empty,
                        ProductType = p.ProductType,
                        MiddlePackageQuantity = p.MiddlePackageQuantity,
                        PurchasePrice = p.PurchasePrice,
                        RetailPrice = p.RetailPrice,
                        IsAutoPricing = p.IsAutoPricing,
                        ProductImage = p.ProductImage,
                        IsActive = p.IsActive,
                        IsSpecialProduct = p.IsSpecialProduct,
                        WarehouseCategoryGUID = p.WarehouseCategoryGUID,
                        CreatedAt = p.CreatedAt,
                        UpdatedAt = p.UpdatedAt,
                        UpdatedBy = p.UpdatedBy,
                        StoreRecordCount = SqlFunc.IsNull(count.StoreRecordCount, 0),
                    })
                    .MergeTable();

                if (domesticSupplierCodes.Count > 0)
                {
                    projectedQuery = projectedQuery.Where(item =>
                        item.DomesticSupplierCode != null
                        && domesticSupplierCodes.Contains(item.DomesticSupplierCode)
                    );
                }

                if (query.StoreRecordCountMin.HasValue)
                {
                    projectedQuery = projectedQuery.Where(item =>
                        item.StoreRecordCount >= query.StoreRecordCountMin.Value
                    );
                }

                if (query.StoreRecordCountMax.HasValue)
                {
                    projectedQuery = projectedQuery.Where(item =>
                        item.StoreRecordCount <= query.StoreRecordCountMax.Value
                    );
                }

                // 应用排序
                if (!string.IsNullOrWhiteSpace(query.SortBy))
                {
                    var isDesc = query.SortOrder?.ToLower() == "desc";
                    var sortField = query.SortBy.ToLower();

                    switch (sortField)
                    {
                        case "productcode":
                            projectedQuery = isDesc
                                ? projectedQuery.OrderBy(p => p.ProductCode, OrderByType.Desc)
                                : projectedQuery.OrderBy(p => p.ProductCode, OrderByType.Asc);
                            break;
                        case "productname":
                            projectedQuery = isDesc
                                ? projectedQuery.OrderBy(p => p.ProductName, OrderByType.Desc)
                                : projectedQuery.OrderBy(p => p.ProductName, OrderByType.Asc);
                            break;
                        case "itemnumber":
                            projectedQuery = isDesc
                                ? projectedQuery.OrderBy(p => p.ItemNumber, OrderByType.Desc)
                                : projectedQuery.OrderBy(p => p.ItemNumber, OrderByType.Asc);
                            break;
                        case "barcode":
                            projectedQuery = isDesc
                                ? projectedQuery.OrderBy(p => p.Barcode, OrderByType.Desc)
                                : projectedQuery.OrderBy(p => p.Barcode, OrderByType.Asc);
                            break;
                        case "localsuppliercode":
                            projectedQuery = isDesc
                                ? projectedQuery.OrderBy(p => p.LocalSupplierCode, OrderByType.Desc)
                                : projectedQuery.OrderBy(p => p.LocalSupplierCode, OrderByType.Asc);
                            break;
                        case "domesticsuppliercode":
                            projectedQuery = isDesc
                                ? projectedQuery
                                    .OrderBy(p => p.DomesticSupplierCode, OrderByType.Desc)
                                    .OrderBy(p => p.ProductCode, OrderByType.Asc)
                                : projectedQuery
                                    .OrderBy(p => p.DomesticSupplierCode, OrderByType.Asc)
                                    .OrderBy(p => p.ProductCode, OrderByType.Asc);
                            break;
                        case "warehousecategoryguid":
                            projectedQuery = isDesc
                                ? projectedQuery
                                    .OrderBy(p => p.WarehouseCategoryGUID, OrderByType.Desc)
                                    .OrderBy(p => p.ProductCode, OrderByType.Asc)
                                : projectedQuery
                                    .OrderBy(p => p.WarehouseCategoryGUID, OrderByType.Asc)
                                    .OrderBy(p => p.ProductCode, OrderByType.Asc);
                            break;
                        case "productcategoryguid":
                            projectedQuery = isDesc
                                ? projectedQuery.OrderBy(p => p.ProductCategoryGUID, OrderByType.Desc)
                                : projectedQuery.OrderBy(p => p.ProductCategoryGUID, OrderByType.Asc);
                            break;
                        case "retailprice":
                            projectedQuery = isDesc
                                ? projectedQuery.OrderBy(p => p.RetailPrice, OrderByType.Desc)
                                : projectedQuery.OrderBy(p => p.RetailPrice, OrderByType.Asc);
                            break;
                        case "purchaseprice":
                            projectedQuery = isDesc
                                ? projectedQuery.OrderBy(p => p.PurchasePrice, OrderByType.Desc)
                                : projectedQuery.OrderBy(p => p.PurchasePrice, OrderByType.Asc);
                            break;
                        case "producttype":
                            projectedQuery = isDesc
                                ? projectedQuery.OrderBy(p => p.ProductType, OrderByType.Desc)
                                : projectedQuery.OrderBy(p => p.ProductType, OrderByType.Asc);
                            break;
                        case "isautopricing":
                            projectedQuery = isDesc
                                ? projectedQuery.OrderBy(p => p.IsAutoPricing, OrderByType.Desc)
                                : projectedQuery.OrderBy(p => p.IsAutoPricing, OrderByType.Asc);
                            break;
                        case "isactive":
                            projectedQuery = isDesc
                                ? projectedQuery.OrderBy(p => p.IsActive, OrderByType.Desc)
                                : projectedQuery.OrderBy(p => p.IsActive, OrderByType.Asc);
                            break;
                        case "storerecordcount":
                            projectedQuery = isDesc
                                ? projectedQuery
                                    .OrderBy(p => p.StoreRecordCount, OrderByType.Desc)
                                    .OrderBy(p => p.ProductCode, OrderByType.Asc)
                                : projectedQuery
                                    .OrderBy(p => p.StoreRecordCount, OrderByType.Asc)
                                    .OrderBy(p => p.ProductCode, OrderByType.Asc);
                            break;
                        case "createdat":
                            projectedQuery = isDesc
                                ? projectedQuery.OrderBy(p => p.CreatedAt, OrderByType.Desc)
                                : projectedQuery.OrderBy(p => p.CreatedAt, OrderByType.Asc);
                            break;
                        case "updatedat":
                            projectedQuery = isDesc
                                ? projectedQuery.OrderBy(p => p.UpdatedAt, OrderByType.Desc)
                                : projectedQuery.OrderBy(p => p.UpdatedAt, OrderByType.Asc);
                            break;
                        default:
                            projectedQuery = projectedQuery.OrderBy(
                                p => p.UpdatedAt,
                                OrderByType.Desc
                            );
                            break;
                    }
                }
                else
                {
                    projectedQuery = projectedQuery.OrderBy(p => p.UpdatedAt, OrderByType.Desc);
                }

                // 获取总数
                var total = await projectedQuery.CountAsync();

                // 分页
                var items = await projectedQuery.Skip((query.PageNumber - 1) * query.PageSize)
                    .Take(query.PageSize)
                    .Select(p => new ProductDto
                    {
                        ProductCode = p.ProductCode ?? string.Empty,
                        ProductCategoryGUID = p.ProductCategoryGUID ?? string.Empty,
                        LocalSupplierCode = p.LocalSupplierCode,
                        DomesticSupplierCode = p.DomesticSupplierCode,
                        DomesticSupplierName = p.DomesticSupplierName,
                        ItemNumber = p.ItemNumber,
                        Barcode = p.Barcode,
                        ProductName = p.ProductName ?? string.Empty,
                        ProductType = p.ProductType,
                        MiddlePackageQuantity = p.MiddlePackageQuantity,
                        PurchasePrice = p.PurchasePrice,
                        RetailPrice = p.RetailPrice,
                        IsAutoPricing = p.IsAutoPricing,
                        ProductImage = p.ProductImage,
                        IsActive = p.IsActive,
                        IsSpecialProduct = p.IsSpecialProduct,
                        WarehouseCategoryGUID = p.WarehouseCategoryGUID,
                        CreatedAt = p.CreatedAt,
                        UpdatedAt = p.UpdatedAt,
                        UpdatedBy = p.UpdatedBy,
                        StoreRecordCount = p.StoreRecordCount,
                    })
                    .ToListAsync();
                // 供应商分类只为当前页回填，不参与分页前的计数与排序。
                await ProductSupplierCategoryProjection.FillAsync(_db, items);

                return new PagedListReactDto<ProductDto>
                {
                    Items = items,
                    Total = total,
                    PageNumber = query.PageNumber,
                    PageSize = query.PageSize,
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "分页查询商品失败");
                throw;
            }
        }

        /// <summary>
        /// 当前页补聚合的页大小上限：超过后 IN 列表过长，退回分页前预聚合的原路径。
        /// </summary>
        private const int MaxPageSizeForPageScopedAggregates = 500;

        /// <summary>
        /// 商品列表快路径：计数、排序、取页都只在 Product 单表上完成，
        /// 分店记录数与国内供应商映射只针对当前页的商品编码聚合。
        /// 两个聚合的右侧键都按 ProductCode 分组后唯一，左连接不会改变商品行数，
        /// 因此单表计数与原先对联接结果计数等价。
        /// </summary>
        private async Task<PagedListReactDto<ProductDto>> GetPagedListWithPageScopedAggregatesAsync(
            ISugarQueryable<Product> filteredProducts,
            ProductReactFilterDto query
        )
        {
            var total = await filteredProducts.Clone().CountAsync();

            var sortedProducts = ApplyProductListSort(filteredProducts, query.SortBy, query.SortOrder);
            var sortField = (query.SortBy ?? string.Empty).ToLower();
            if (
                !string.IsNullOrWhiteSpace(query.Search)
                && sortField != "productcode"
                && sortField != "warehousecategoryguid"
            )
            {
                // 关键位置：带关键词时追加商品编码作次级排序键。
                // 否则优化器会为凑满一页沿更新时间等单列索引逐行回表检查关键词，
                // 生产实测冷门或零命中关键词要 975 毫秒、70 万次逻辑读；
                // 追加后索引给不出该顺序，改为一次扫描加排序，稳定在约 0.2 秒，也让同值行的分页顺序确定。
                sortedProducts = sortedProducts.OrderBy(p => p.ProductCode, OrderByType.Asc);
            }

            var items = await sortedProducts
                .Skip((query.PageNumber - 1) * query.PageSize)
                .Take(query.PageSize)
                .Select(p => new ProductDto
                {
                    ProductCode = p.ProductCode ?? string.Empty,
                    ProductCategoryGUID = p.ProductCategoryGUID ?? string.Empty,
                    LocalSupplierCode = p.LocalSupplierCode,
                    ItemNumber = p.ItemNumber,
                    Barcode = p.Barcode,
                    ProductName = p.ProductName ?? string.Empty,
                    ProductType = p.ProductType,
                    MiddlePackageQuantity = p.MiddlePackageQuantity,
                    PurchasePrice = p.PurchasePrice,
                    RetailPrice = p.RetailPrice,
                    IsAutoPricing = p.IsAutoPricing,
                    ProductImage = p.ProductImage,
                    IsActive = p.IsActive,
                    IsSpecialProduct = p.IsSpecialProduct,
                    WarehouseCategoryGUID = p.WarehouseCategoryGUID,
                    CreatedAt = p.CreatedAt,
                    UpdatedAt = p.UpdatedAt,
                    UpdatedBy = p.UpdatedBy,
                })
                .ToListAsync();

            var pageProductCodes = items
                .Select(item => item.ProductCode)
                .Where(code => !string.IsNullOrEmpty(code))
                .Distinct()
                .ToList();
            if (pageProductCodes.Count > 0)
            {
                var storeRecordCounts = await _db.Queryable<StoreRetailPrice>()
                    .Where(record =>
                        record.IsDeleted == false
                        && record.ProductCode != null
                        && pageProductCodes.Contains(record.ProductCode)
                    )
                    .GroupBy(record => record.ProductCode)
                    .Select(record => new ProductStoreRecordCountRow
                    {
                        ProductCode = record.ProductCode ?? string.Empty,
                        StoreRecordCount = SqlFunc.AggregateCount(record.UUID),
                    })
                    .ToListAsync();

                // 与原路径相同的有效映射口径：DomesticProduct 与 ChinaSupplier 都未软删。
                var domesticMappings = await _db.Queryable<DomesticProduct>()
                    .InnerJoin<ChinaSupplier>(
                        (dp, cs) => dp.SupplierCode == cs.SupplierCode && cs.IsDeleted == false
                    )
                    .Where((dp, cs) => dp.IsDeleted == false && pageProductCodes.Contains(dp.ProductCode))
                    .GroupBy(dp => dp.ProductCode)
                    .Select(
                        (dp, cs) =>
                            new DomesticProductMappingRow
                            {
                                ProductCode = dp.ProductCode,
                                DomesticSupplierCode = SqlFunc.AggregateMax(dp.SupplierCode),
                                DomesticSupplierName = SqlFunc.AggregateMax(cs.SupplierName),
                            }
                    )
                    .ToListAsync();

                // 数据库联接按不区分大小写的排序规则匹配编码，内存回填保持同样口径；
                // 用累加而非 ToDictionary，避免区分大小写的库把同一编码分成多组时抛重复键。
                var countByCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in storeRecordCounts)
                {
                    countByCode[row.ProductCode] =
                        countByCode.GetValueOrDefault(row.ProductCode) + row.StoreRecordCount;
                }

                var domesticByCode = new Dictionary<string, DomesticProductMappingRow>(
                    StringComparer.OrdinalIgnoreCase
                );
                foreach (var row in domesticMappings)
                {
                    domesticByCode.TryAdd(row.ProductCode, row);
                }

                foreach (var item in items)
                {
                    item.StoreRecordCount = countByCode.GetValueOrDefault(item.ProductCode);
                    if (domesticByCode.TryGetValue(item.ProductCode, out var domestic))
                    {
                        item.DomesticSupplierCode = domestic.DomesticSupplierCode;
                        item.DomesticSupplierName = domestic.DomesticSupplierName;
                    }
                }
            }

            await ProductSupplierCategoryProjection.FillAsync(_db, items);

            return new PagedListReactDto<ProductDto>
            {
                Items = items,
                Total = total,
                PageNumber = query.PageNumber,
                PageSize = query.PageSize,
            };
        }

        /// <summary>
        /// 快路径的排序：字段与方向口径和分页前预聚合路径一致，未识别字段落回更新时间倒序。
        /// 国内供应商、分店记录数两项排序依赖聚合结果，不会进入这里。
        /// </summary>
        private static ISugarQueryable<Product> ApplyProductListSort(
            ISugarQueryable<Product> products,
            string? sortBy,
            string? sortOrder
        )
        {
            var direction = sortOrder?.ToLower() == "desc" ? OrderByType.Desc : OrderByType.Asc;
            return (sortBy ?? string.Empty).ToLower() switch
            {
                "productcode" => products.OrderBy(p => p.ProductCode, direction),
                "productname" => products.OrderBy(p => p.ProductName, direction),
                "itemnumber" => products.OrderBy(p => p.ItemNumber, direction),
                "barcode" => products.OrderBy(p => p.Barcode, direction),
                "localsuppliercode" => products.OrderBy(p => p.LocalSupplierCode, direction),
                "warehousecategoryguid" => products
                    .OrderBy(p => p.WarehouseCategoryGUID, direction)
                    .OrderBy(p => p.ProductCode, OrderByType.Asc),
                "productcategoryguid" => products.OrderBy(p => p.ProductCategoryGUID, direction),
                "retailprice" => products.OrderBy(p => p.RetailPrice, direction),
                "purchaseprice" => products.OrderBy(p => p.PurchasePrice, direction),
                "producttype" => products.OrderBy(p => p.ProductType, direction),
                "isautopricing" => products.OrderBy(p => p.IsAutoPricing, direction),
                "isactive" => products.OrderBy(p => p.IsActive, direction),
                "createdat" => products.OrderBy(p => p.CreatedAt, direction),
                "updatedat" => products.OrderBy(p => p.UpdatedAt, direction),
                _ => products.OrderBy(p => p.UpdatedAt, OrderByType.Desc),
            };
        }

        /// <summary>
        /// 供应商分类筛选：200 商品按仓库分类，其他供应商按有效归属（归属登记的供应商须与商品当前供应商一致）。
        /// 使用 EXISTS 子查询作用在 Product 单表查询上，不改变列表快/慢路径的判定。
        /// </summary>
        private async Task<ISugarQueryable<Product>> ApplySupplierCategoryFiltersAsync(
            ISugarQueryable<Product> q,
            ProductReactFilterDto query
        )
        {
            var hotBargain = DefaultLocalSupplierCode;
            var requestedGuids = NormalizeColumnFilterValues(query.SupplierCategoryGUIDs)
                .Where(guid => !string.IsNullOrWhiteSpace(guid))
                .Select(guid => guid.Trim())
                .ToList();
            if (requestedGuids.Count > 0)
            {
                var (supplierGuids, warehouseGuids) = await LocalSupplierCategoryFilter.ExpandAsync(
                    _db,
                    requestedGuids
                );
                if (supplierGuids.Count == 0 && warehouseGuids.Count == 0)
                {
                    // 所选分类均不存在：返回空结果而不是忽略条件。
                    q = q.Where("1 = 0");
                }
                else if (warehouseGuids.Count == 0)
                {
                    q = q.Where(p =>
                        SqlFunc.Subqueryable<LocalSupplierCategoryProductAssignment>()
                            .Where(a =>
                                a.ProductCode == p.ProductCode
                                && a.LocalSupplierCode == p.LocalSupplierCode
                                && supplierGuids.Contains(a.CategoryGUID)
                            )
                            .Any()
                    );
                }
                else if (supplierGuids.Count == 0)
                {
                    q = q.Where(p =>
                        (p.LocalSupplierCode == hotBargain || p.LocalSupplierCode == null || p.LocalSupplierCode == "")
                        && p.WarehouseCategoryGUID != null
                        && warehouseGuids.Contains(p.WarehouseCategoryGUID)
                    );
                }
                else
                {
                    q = q.Where(p =>
                        SqlFunc.Subqueryable<LocalSupplierCategoryProductAssignment>()
                            .Where(a =>
                                a.ProductCode == p.ProductCode
                                && a.LocalSupplierCode == p.LocalSupplierCode
                                && supplierGuids.Contains(a.CategoryGUID)
                            )
                            .Any()
                        || (
                            (p.LocalSupplierCode == hotBargain || p.LocalSupplierCode == null || p.LocalSupplierCode == "")
                            && p.WarehouseCategoryGUID != null
                            && warehouseGuids.Contains(p.WarehouseCategoryGUID)
                        )
                    );
                }
            }

            if (query.SupplierCategoryUnassignedOnly == true)
            {
                q = q.Where(p =>
                    (
                        (p.LocalSupplierCode == hotBargain || p.LocalSupplierCode == null || p.LocalSupplierCode == "")
                        && (p.WarehouseCategoryGUID == null || p.WarehouseCategoryGUID == "")
                    )
                    || (
                        p.LocalSupplierCode != null
                        && p.LocalSupplierCode != ""
                        && p.LocalSupplierCode != hotBargain
                        && SqlFunc.Subqueryable<LocalSupplierCategoryProductAssignment>()
                            .Where(a =>
                                a.ProductCode == p.ProductCode && a.LocalSupplierCode == p.LocalSupplierCode
                            )
                            .NotAny()
                    )
                );
            }

            return q;
        }

        /// <summary>
        /// 获取商品对应的分店价格记录
        /// </summary>
        public async Task<ApiResponse<List<ProductStoreRecordDto>>> GetStoreRecordsAsync(
            string productCode,
            IReadOnlyCollection<string>? accessibleStoreCodes
        )
        {
            try
            {
                if (string.IsNullOrWhiteSpace(productCode))
                {
                    return new ApiResponse<List<ProductStoreRecordDto>>
                    {
                        Success = false,
                        Message = "商品编码不能为空",
                        Data = new List<ProductStoreRecordDto>(),
                    };
                }

                var trimmedProductCode = productCode.Trim();
                var query = _db.Queryable<StoreRetailPrice>()
                    .LeftJoin<Store>((price, store) =>
                        price.StoreCode == store.StoreCode && store.IsDeleted == false
                    )
                    .Where((price, store) =>
                        price.ProductCode == trimmedProductCode && price.IsDeleted == false
                    );

                if (accessibleStoreCodes is not null)
                {
                    var storeCodes = accessibleStoreCodes
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Select(item => item.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (!storeCodes.Any())
                    {
                        return new ApiResponse<List<ProductStoreRecordDto>>
                        {
                            Success = true,
                            Data = new List<ProductStoreRecordDto>(),
                        };
                    }

                    query = query.Where((price, store) =>
                        price.StoreCode != null && storeCodes.Contains(price.StoreCode)
                    );
                }

                var records = await query
                    .OrderBy(
                        (price, store) => SqlFunc.IIF(
                            SqlFunc.IsNullOrEmpty(store.StoreName),
                            price.StoreCode,
                            store.StoreName
                        ),
                        OrderByType.Asc
                    )
                    .OrderBy((price, store) => price.StoreCode, OrderByType.Asc)
                    .Select((price, store) => new ProductStoreRecordDto
                    {
                        StoreCode = price.StoreCode,
                        StoreName = store.StoreName,
                        StoreProductCode = price.StoreProductCode,
                        PurchasePrice = price.PurchasePrice,
                        StoreRetailPriceValue = price.StoreRetailPriceValue,
                        DiscountRate = price.DiscountRate,
                        IsActive = price.IsActive,
                        IsAutoPricing = price.IsAutoPricing,
                        IsSpecialProduct = price.IsSpecialProduct,
                        UpdatedAt = price.UpdatedAt,
                        UpdatedBy = price.UpdatedBy,
                    })
                    .ToListAsync();

                return new ApiResponse<List<ProductStoreRecordDto>>
                {
                    Success = true,
                    Data = records,
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取商品分店记录失败: {ProductCode}", productCode);
                return new ApiResponse<List<ProductStoreRecordDto>>
                {
                    Success = false,
                    Message = "获取商品分店记录失败",
                    Data = new List<ProductStoreRecordDto>(),
                };
            }
        }

        /// <summary>
        /// 批量更新商品分店业务字段
        /// </summary>
        public async Task<ApiResponse<BatchOperationReactResult>> BatchUpdateStoreRecordsAsync(
            string productCode,
            BatchUpdateProductStoreRecordsRequest request,
            IReadOnlyCollection<string>? accessibleStoreCodes
        )
        {
            var result = new BatchOperationReactResult();

            try
            {
                if (string.IsNullOrWhiteSpace(productCode))
                {
                    return ApiResponse<BatchOperationReactResult>.Error("商品编码不能为空");
                }

                if (request == null)
                {
                    return ApiResponse<BatchOperationReactResult>.Error("请求数据不能为空");
                }

                var targetStoreCodes = NormalizeStoreCodes(request.StoreCodes);
                if (!targetStoreCodes.Any())
                {
                    return ApiResponse<BatchOperationReactResult>.Error("至少选择一个分店");
                }

                if (request.Changes == null || !request.Changes.HasAnyChanges())
                {
                    return ApiResponse<BatchOperationReactResult>.Error("至少指定一个需要修改的字段");
                }

                var trimmedProductCode = productCode.Trim();
                var accessibleStoreCodeSet = BuildStoreCodeSet(accessibleStoreCodes);
                var currentUser = _httpContextAccessor.HttpContext?.User?.Identity?.Name ?? "System";

                // 先按商品 + 分店 + 软删状态收敛记录，再逐个处理失败原因，保证前端能拿到逐分店反馈。
                var query = _db.Queryable<StoreRetailPrice>()
                    .Where(item =>
                        item.ProductCode == trimmedProductCode
                        && item.IsDeleted == false
                        && targetStoreCodes.Contains(item.StoreCode!)
                    );

                if (accessibleStoreCodeSet != null)
                {
                    if (accessibleStoreCodeSet.Count == 0)
                    {
                        foreach (var storeCode in targetStoreCodes)
                        {
                            result.FailedCount++;
                            result.Errors.Add($"{storeCode}: 当前用户不可修改该分店");
                        }

                        return ApiResponse<BatchOperationReactResult>.OK(
                            result,
                            BuildBatchUpdateStoreRecordsMessage(result)
                        );
                    }

                    query = query.Where(item => accessibleStoreCodeSet.Contains(item.StoreCode!));
                }

                var existingRecords = await query.ToListAsync();
                var recordMap = existingRecords
                    .Where(item => !string.IsNullOrWhiteSpace(item.StoreCode))
                    .GroupBy(item => item.StoreCode!.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

                foreach (var storeCode in targetStoreCodes)
                {
                    if (accessibleStoreCodeSet != null && !accessibleStoreCodeSet.Contains(storeCode))
                    {
                        result.FailedCount++;
                        result.Errors.Add($"{storeCode}: 当前用户不可修改该分店");
                        continue;
                    }

                    if (!recordMap.TryGetValue(storeCode, out var record))
                    {
                        result.FailedCount++;
                        result.Errors.Add($"{storeCode}: 分店记录不存在或已删除");
                        continue;
                    }

                    await ExecuteStoreRecordPartialUpdateAsync(
                        trimmedProductCode,
                        storeCode,
                        request.Changes,
                        currentUser
                    );
                    result.SuccessCount++;
                }

                return ApiResponse<BatchOperationReactResult>.OK(
                    result,
                    BuildBatchUpdateStoreRecordsMessage(result)
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量更新商品分店记录失败: {ProductCode}", productCode);
                return ApiResponse<BatchOperationReactResult>.Error($"批量更新商品分店记录失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 根据ProductCode获取商品详情
        /// </summary>
        public async Task<ApiResponse<ProductDto>> GetByIdAsync(string productCode)
        {
            try
            {
                var product = await _db.Queryable<Product>()
                    .Where(p => p.ProductCode == productCode)
                    .FirstAsync();

                if (product == null)
                {
                    return new ApiResponse<ProductDto> { Success = false, Message = "商品不存在" };
                }

                var domesticSupplier = await _db.Queryable<DomesticProduct>()
                    .InnerJoin<ChinaSupplier>((dp, cs) => dp.SupplierCode == cs.SupplierCode)
                    .Where((dp, cs) =>
                        dp.ProductCode == productCode
                        && dp.IsDeleted == false
                        && cs.IsDeleted == false
                    )
                    .GroupBy(dp => dp.ProductCode)
                    .Select((dp, cs) => new DomesticProductMappingRow
                    {
                        ProductCode = dp.ProductCode,
                        DomesticSupplierCode = SqlFunc.AggregateMax(dp.SupplierCode),
                        DomesticSupplierName = SqlFunc.AggregateMax(cs.SupplierName),
                    })
                    .FirstAsync();

                var dto = new ProductDto
                {
                    ProductCode = product.ProductCode ?? string.Empty,
                    ProductCategoryGUID = product.ProductCategoryGUID ?? string.Empty,
                    LocalSupplierCode = product.LocalSupplierCode,
                    DomesticSupplierCode = domesticSupplier?.DomesticSupplierCode,
                    DomesticSupplierName = domesticSupplier?.DomesticSupplierName,
                    ItemNumber = product.ItemNumber,
                    Barcode = product.Barcode,
                    ProductName = product.ProductName,
                    ProductType = product.ProductType,
                    MiddlePackageQuantity = product.MiddlePackageQuantity,
                    PurchasePrice = product.PurchasePrice,
                    RetailPrice = product.RetailPrice,
                    IsAutoPricing = product.IsAutoPricing,
                    ProductImage = product.ProductImage,
                    IsActive = product.IsActive,
                    IsSpecialProduct = product.IsSpecialProduct,
                    WarehouseCategoryGUID = product.WarehouseCategoryGUID,
                    UpdatedAt = product.UpdatedAt,
                    UpdatedBy = product.UpdatedBy,
                };
                await ProductSupplierCategoryProjection.FillAsync(_db, new[] { dto });

                return new ApiResponse<ProductDto> { Success = true, Data = dto };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取商品详情失败: {ProductCode}", productCode);
                return new ApiResponse<ProductDto>
                {
                    Success = false,
                    Message = $"获取商品详情失败: {ex.Message}",
                };
            }
        }

        /// <summary>
        /// 创建商品（事务内插入 Product，并为全部启用分店默认创建 StoreRetailPrice）
        /// </summary>
        public async Task<ApiResponse<ProductDto>> CreateAsync(CreateProductDto dto)
        {
            try
            {
                var product = new Product
                {
                    ProductCode = dto.ProductCode,
                    ProductCategoryGUID = dto.ProductCategoryGUID,
                    LocalSupplierCode = NormalizeLocalSupplierCode(dto.LocalSupplierCode),
                    ItemNumber = dto.ItemNumber,
                    Barcode = dto.Barcode,
                    ProductName = dto.ProductName,
                    ProductType = dto.ProductType,
                    MiddlePackageQuantity = dto.MiddlePackageQuantity,
                    PurchasePrice = dto.PurchasePrice,
                    RetailPrice = dto.RetailPrice,
                    IsAutoPricing = dto.IsAutoPricing,
                    ProductImage = dto.ProductImage,
                    IsActive = dto.IsActive,
                    IsSpecialProduct = dto.IsSpecialProduct,
                    WarehouseCategoryGUID = dto.WarehouseCategoryGUID,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now,
                };
                var auditBatchGuid = Guid.NewGuid();
                var auditOccurredAtUtc = DateTime.UtcNow;

                var createTransaction = await _db.Ado.UseTranAsync(async () =>
                {
                    // 商品主档新建会占用商品编码这一身份；Update 总闸与普通成本 Shared 兼容，
                    // 但会阻止同一商品被另一身份变更并发写入。
                    await SetChildPurchasePriceMutationLock.AcquireProductIdentitiesWithinBudgetAsync(
                        _db,
                        new[] { product.ProductCode }
                    );
                    var beforeSnapshots = await CaptureProductSnapshotsAsync(
                        new[] { product.ProductCode ?? string.Empty }
                    );
                    await _db.Insertable(product).ExecuteCommandAsync();

                    var storeCodes = await _db.Queryable<Store>()
                        .Where(s => s.IsActive == true && s.IsDeleted == false)
                        .Select(s => s.StoreCode)
                        .ToListAsync();

                    var supplierCode = product.LocalSupplierCode;
                    var now = DateTime.Now;
                    var currentUser =
                        _httpContextAccessor.HttpContext?.User?.Identity?.Name ?? "System";

                    var storePriceList = new List<StoreRetailPrice>();
                    foreach (var storeCode in storeCodes ?? Enumerable.Empty<string>())
                    {
                        if (string.IsNullOrWhiteSpace(storeCode))
                            continue;

                        storePriceList.Add(
                            new StoreRetailPrice
                            {
                                UUID = UuidHelper.GenerateUuid7(),
                                StoreCode = storeCode,
                                ProductCode = product.ProductCode,
                                StoreProductCode =
                                    storeCode + (product.ProductCode ?? string.Empty),
                                SupplierCode = supplierCode,
                                PurchasePrice = product.PurchasePrice,
                                StoreRetailPriceValue = product.RetailPrice,
                                DiscountRate = null,
                                IsActive = product.IsActive,
                                IsAutoPricing = product.IsAutoPricing,
                                IsSpecialProduct = product.IsSpecialProduct,
                                CreatedAt = now,
                                UpdatedAt = now,
                                CreatedBy = currentUser,
                                UpdatedBy = currentUser,
                                IsDeleted = false,
                            }
                        );
                    }

                    if (storePriceList.Count > 0)
                        await _db.Insertable(storePriceList).ExecuteCommandAsync();

                    // 非 200 新商品若其货号已被扩展采集过，立即按网站分类自动归类。
                    await new LocalSupplierCategoryAssignmentService(_db).ApplyProductEditAsync(
                        new LocalSupplierCategoryAssignmentService.ProductEditContext(
                            product.ProductCode ?? string.Empty,
                            OldProductCode: null,
                            OldSupplierCode: null,
                            NewSupplierCode: product.LocalSupplierCode,
                            OldItemNumber: null,
                            NewItemNumber: product.ItemNumber,
                            RequestedCategoryGuid: null,
                            ClearRequested: false,
                            Actor: currentUser
                        )
                    );

                    var afterSnapshots = await CaptureProductSnapshotsAsync(
                        new[] { product.ProductCode ?? string.Empty }
                    );
                    await RecordProductChangesAsync(
                        beforeSnapshots,
                        afterSnapshots,
                        "Create",
                        "ProductReact.Create",
                        auditBatchGuid,
                        auditOccurredAtUtc
                    );
                });
                if (!createTransaction.IsSuccess)
                {
                    throw createTransaction.ErrorException
                        ?? new InvalidOperationException("创建商品事务失败");
                }

                var resultDto = await GetByIdAsync(product.ProductCode);
                return resultDto;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建商品失败");
                return new ApiResponse<ProductDto>
                {
                    Success = false,
                    Message = $"创建商品失败: {ex.Message}",
                };
            }
        }

        /// <summary>
        /// 更新商品（改码时级联更新 StoreMultiCodeProduct、StoreRetailPrice、ProductSetCode 的 ProductCode）
        /// </summary>
        public async Task<ApiResponse<ProductDto>> UpdateAsync(
            string productCode,
            UpdateProductDto dto
        )
        {
            try
            {
                var product = await _db.Queryable<Product>()
                    .Where(p => p.ProductCode == productCode)
                    .FirstAsync();

                if (product == null)
                {
                    return new ApiResponse<ProductDto> { Success = false, Message = "商品不存在" };
                }

                var newProductCode = dto.ProductCode?.Trim();
                var isCodeChange =
                    !string.IsNullOrEmpty(newProductCode) && newProductCode != productCode;
                var auditBatchGuid = Guid.NewGuid();
                var auditOccurredAtUtc = DateTime.UtcNow;

                if (isCodeChange)
                {
                    var codeChangeTransaction = await _db.Ado.UseTranAsync(async () =>
                    {
                        // 改码同时占用旧、新商品编码，避免两个身份变更交叉写入。
                        var lockScope = await SetChildPurchasePriceMutationLock.AcquireProductIdentitiesWithinBudgetAsync(
                            _db,
                            new[] { productCode, newProductCode! }
                        );
                        product = await _db.Queryable<Product>()
                            .Where(p => p.ProductCode == productCode && !p.IsDeleted)
                            .FirstAsync();
                        if (product == null)
                        {
                            throw new InvalidOperationException("商品在更新期间已被删除，请重试");
                        }
                        var beforeSnapshots = await CaptureProductSnapshotsAsync(
                            new[] { productCode, newProductCode! }
                        );
                        await UpdateProductAndCascadeProductCodeAsync(
                            productCode,
                            product,
                            dto,
                            newProductCode!
                        );
                        var recalculation = await new SetChildPurchasePriceService(
                            _db
                        ).RecalculateLockedAsync(
                            lockScope,
                            new[] { newProductCode! },
                            storeCodes: null,
                            updatedBy: _httpContextAccessor.HttpContext?.User?.Identity?.Name
                                ?? "System"
                        );
                        EnsureSetChildPurchasePriceRecalculated(
                            recalculation,
                            new[] { newProductCode! }
                        );
                        var afterSnapshots = await CaptureProductSnapshotsAsync(
                            new[] { newProductCode! }
                        );
                        await RecordProductChangesAsync(
                            RebindProductSnapshot(beforeSnapshots, productCode, newProductCode!),
                            afterSnapshots,
                            "Update",
                            "ProductReact.Update",
                            auditBatchGuid,
                            auditOccurredAtUtc
                        );
                    });
                    if (!codeChangeTransaction.IsSuccess)
                    {
                        throw codeChangeTransaction.ErrorException
                            ?? new InvalidOperationException("商品更新事务失败");
                    }
                    return await GetByIdAsync(newProductCode!);
                }

                var updateTransaction = await _db.Ado.UseTranAsync(async () =>
                {
                    // 商品主档编辑可修改供应商、货号和条码，因此也作为身份变更互斥。
                    var lockScope = await SetChildPurchasePriceMutationLock.AcquireProductIdentitiesWithinBudgetAsync(
                        _db,
                        new[] { productCode }
                    );
                    product = await _db.Queryable<Product>()
                        .Where(p => p.ProductCode == productCode && !p.IsDeleted)
                        .FirstAsync();
                    if (product == null)
                    {
                        throw new InvalidOperationException("商品在更新期间已被删除，请重试");
                    }
                    var beforeSnapshots = await CaptureProductSnapshotsAsync(
                        new[] { productCode }
                    );
                    var oldSupplierCode = product.LocalSupplierCode;
                    var oldItemNumber = product.ItemNumber;

                    // 未改码：仅更新 Product；StoreRetailPrice 只同步镜像字段，不进入统一商品快照。
                    product.ProductCategoryGUID = dto.ProductCategoryGUID;
                    product.LocalSupplierCode = NormalizeLocalSupplierCode(dto.LocalSupplierCode);
                    product.ItemNumber = dto.ItemNumber;
                    product.Barcode = dto.Barcode;
                    product.ProductName = dto.ProductName;
                    product.ProductType = dto.ProductType;
                    product.MiddlePackageQuantity = dto.MiddlePackageQuantity;
                    product.PurchasePrice = dto.PurchasePrice;
                    product.RetailPrice = dto.RetailPrice;
                    product.IsAutoPricing = dto.IsAutoPricing;
                    product.ProductImage = dto.ProductImage;
                    product.IsActive = dto.IsActive;
                    product.IsSpecialProduct = dto.IsSpecialProduct;
                    product.WarehouseCategoryGUID = dto.WarehouseCategoryGUID;
                    product.UpdatedAt = DateTime.Now;
                    var currentUser =
                        _httpContextAccessor.HttpContext?.User?.Identity?.Name ?? "System";
                    product.UpdatedBy = currentUser;

                    await _db.Updateable(product).ExecuteCommandAsync();

                    await _db.Updateable<StoreRetailPrice>()
                        .SetColumns(srp => srp.IsAutoPricing == product.IsAutoPricing)
                        .Where(srp => srp.ProductCode == product.ProductCode)
                        .ExecuteCommandAsync();

                    // 供应商分类联动：人工指定锁定、清空恢复自动、换供应商或改货号重算；校验失败回滚整个编辑。
                    await new LocalSupplierCategoryAssignmentService(_db).ApplyProductEditAsync(
                        new LocalSupplierCategoryAssignmentService.ProductEditContext(
                            productCode,
                            OldProductCode: null,
                            OldSupplierCode: oldSupplierCode,
                            NewSupplierCode: product.LocalSupplierCode,
                            OldItemNumber: oldItemNumber,
                            NewItemNumber: product.ItemNumber,
                            RequestedCategoryGuid: dto.SupplierCategoryGUID,
                            ClearRequested: dto.ClearSupplierCategory,
                            Actor: currentUser
                        )
                    );

                    var recalculation = await new SetChildPurchasePriceService(
                        _db
                    ).RecalculateLockedAsync(
                        lockScope,
                        new[] { productCode },
                        storeCodes: null,
                        updatedBy: currentUser
                    );
                    EnsureSetChildPurchasePriceRecalculated(
                        recalculation,
                        new[] { productCode }
                    );

                    var afterSnapshots = await CaptureProductSnapshotsAsync(
                        new[] { productCode }
                    );
                    await RecordProductChangesAsync(
                        beforeSnapshots,
                        afterSnapshots,
                        "Update",
                        "ProductReact.Update",
                        auditBatchGuid,
                        auditOccurredAtUtc
                    );
                });
                if (!updateTransaction.IsSuccess)
                {
                    throw updateTransaction.ErrorException
                        ?? new InvalidOperationException("商品更新事务失败");
                }

                return await GetByIdAsync(productCode);
            }
            catch (Exception ex) when (
                SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out var conflict)
            )
            {
                _logger.LogWarning(ex, "更新商品遇到套装成本业务锁冲突: {ProductCode}", productCode);
                return ApiResponse<ProductDto>.Error(
                    conflict!.Message,
                    SetChildPurchasePriceMutationLock.BusyErrorCode
                );
            }
            catch (LocalSupplierCategoryValidationException ex)
            {
                return ApiResponse<ProductDto>.Error(ex.Message, ex.ErrorCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新商品失败: {ProductCode}", productCode);
                return new ApiResponse<ProductDto>
                {
                    Success = false,
                    Message = $"更新商品失败: {ex.Message}",
                };
            }
        }

        private async Task UpdateProductAndCascadeProductCodeAsync(
            string oldProductCode,
            Product product,
            UpdateProductDto dto,
            string newProductCode
        )
        {
            var oldSupplierCode = product.LocalSupplierCode;
            var oldItemNumber = product.ItemNumber;
            product.ProductCode = newProductCode;
            product.ProductCategoryGUID = dto.ProductCategoryGUID;
            product.LocalSupplierCode = NormalizeLocalSupplierCode(dto.LocalSupplierCode);
            product.ItemNumber = dto.ItemNumber;
            product.Barcode = dto.Barcode;
            product.ProductName = dto.ProductName;
            product.ProductType = dto.ProductType;
            product.MiddlePackageQuantity = dto.MiddlePackageQuantity;
            product.PurchasePrice = dto.PurchasePrice;
            product.RetailPrice = dto.RetailPrice;
            product.IsAutoPricing = dto.IsAutoPricing;
            product.ProductImage = dto.ProductImage;
            product.IsActive = dto.IsActive;
            product.IsSpecialProduct = dto.IsSpecialProduct;
            product.WarehouseCategoryGUID = dto.WarehouseCategoryGUID;
            product.UpdatedAt = DateTime.Now;
            var currentUser = _httpContextAccessor.HttpContext?.User?.Identity?.Name ?? "System";
            product.UpdatedBy = currentUser;

            await _db.Updateable(product).ExecuteCommandAsync();

            await _db.Updateable<StoreRetailPrice>()
                .SetColumns(srp => srp.IsAutoPricing == product.IsAutoPricing)
                .Where(srp => srp.ProductCode == newProductCode)
                .ExecuteCommandAsync();

            await _db.Updateable<StoreMultiCodeProduct>()
                .SetColumns(m => m.ProductCode == newProductCode)
                .Where(m => m.ProductCode == oldProductCode)
                .ExecuteCommandAsync();

            await _db.Updateable<StoreRetailPrice>()
                .SetColumns(s => s.ProductCode == newProductCode)
                .Where(s => s.ProductCode == oldProductCode)
                .ExecuteCommandAsync();

            await _db.Updateable<ProductSetCode>()
                .SetColumns(psc => psc.ProductCode == newProductCode)
                .Where(psc => psc.ProductCode == oldProductCode)
                .ExecuteCommandAsync();

            // 供应商分类归属随商品编码迁移，再按本次编辑联动（人工指定/清空/换供应商/改货号）。
            await new LocalSupplierCategoryAssignmentService(_db).ApplyProductEditAsync(
                new LocalSupplierCategoryAssignmentService.ProductEditContext(
                    newProductCode,
                    OldProductCode: oldProductCode,
                    OldSupplierCode: oldSupplierCode,
                    NewSupplierCode: product.LocalSupplierCode,
                    OldItemNumber: oldItemNumber,
                    NewItemNumber: product.ItemNumber,
                    RequestedCategoryGuid: dto.SupplierCategoryGUID,
                    ClearRequested: dto.ClearSupplierCategory,
                    Actor: currentUser
                )
            );
        }

        /// <summary>
        /// 删除商品（支持软删除和物理删除）
        /// </summary>
        /// <param name="productCode">商品编码</param>
        /// <param name="isSoftDelete">true=软删除，false=物理删除</param>
        public async Task<ApiResponse<bool>> DeleteAsync(
            string productCode,
            bool isSoftDelete = true
        )
        {
            try
            {
                var productDeleted = 0;
                var transaction = await _db.Ado.UseTranAsync(async () =>
                {
                    await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                        _db,
                        new[] { productCode }
                    );
                    productDeleted = await CascadeDeleteProductAsync(productCode, isSoftDelete);
                });
                if (!transaction.IsSuccess)
                {
                    throw transaction.ErrorException
                        ?? new InvalidOperationException("删除商品事务失败");
                }

                var deleteType = isSoftDelete ? "软删除" : "物理删除";
                return new ApiResponse<bool>
                {
                    Success = productDeleted > 0,
                    Data = productDeleted > 0,
                    Message = productDeleted > 0 ? $"{deleteType}成功" : "商品不存在",
                };
            }
            catch (Exception ex) when (
                SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out var conflict)
            )
            {
                _logger.LogWarning(ex, "删除商品获取套装成本业务锁失败: {ProductCode}", productCode);
                return ApiResponse<bool>.Error(
                    conflict!.Message,
                    SetChildPurchasePriceMutationLock.BusyErrorCode
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除商品失败: {ProductCode}", productCode);
                return new ApiResponse<bool>
                {
                    Success = false,
                    Message = $"删除商品失败: {ex.Message}",
                };
            }
        }

        /// <summary>
        /// 级联删除商品及其关联表（ProductSetCode、StoreMultiCodeProduct、StoreRetailPrice、Product）
        /// 支持软删除（标记IsDeleted）和物理删除
        /// </summary>
        /// <param name="productCode">商品编码</param>
        /// <param name="isSoftDelete">true=软删除，false=物理删除</param>
        /// <returns>删除的 Product 行数（0 或 1）</returns>
        private async Task<int> CascadeDeleteProductAsync(string productCode, bool isSoftDelete)
        {
            if (string.IsNullOrWhiteSpace(productCode))
                return 0;

            var now = DateTime.Now;
            var currentUser = _httpContextAccessor.HttpContext?.User?.Identity?.Name ?? "System";

            if (isSoftDelete)
            {
                // 软删除：标记 IsDeleted = true

                // 1. 套装关系以父商品+子项为业务键，删除父商品时不能仅凭子项键影响其他父商品。
                await _db.Updateable<ProductSetCode>()
                    .SetColumns(psc => psc.IsDeleted == true)
                    .Where(psc => psc.ProductCode == productCode)
                    .ExecuteCommandAsync();

                // 2. 软删除 StoreMultiCodeProduct
                await _db.Updateable<StoreMultiCodeProduct>()
                    .SetColumns(m => m.IsDeleted == true)
                    .Where(m => m.ProductCode == productCode)
                    .ExecuteCommandAsync();

                // 3. 软删除 StoreRetailPrice
                await _db.Updateable<StoreRetailPrice>()
                    .SetColumns(s => s.IsDeleted == true)
                    .Where(s => s.ProductCode == productCode)
                    .ExecuteCommandAsync();

                // 4. 软删除 Product
                var productRows = await _db.Updateable<Product>()
                    .SetColumns(p => p.IsDeleted == true)
                    .Where(p => p.ProductCode == productCode)
                    .ExecuteCommandAsync();

                return productRows;
            }
            else
            {
                // 物理删除：彻底从数据库删除

                // 1. ProductSetCode：只删除当前父商品关系，避免跨父商品复用子项键时误删。
                await _db.Deleteable<ProductSetCode>()
                    .Where(psc => psc.ProductCode == productCode)
                    .ExecuteCommandAsync();

                // 2. StoreMultiCodeProduct
                await _db.Deleteable<StoreMultiCodeProduct>()
                    .Where(m => m.ProductCode == productCode)
                    .ExecuteCommandAsync();

                // 3. StoreRetailPrice
                await _db.Deleteable<StoreRetailPrice>()
                    .Where(s => s.ProductCode == productCode)
                    .ExecuteCommandAsync();

                // 4. 供应商分类归属（软删除保留，便于恢复商品时沿用）
                await new LocalSupplierCategoryAssignmentService(_db).DeleteAssignmentsAsync(
                    new[] { productCode }
                );

                // 5. Product
                var productRows = await _db.Deleteable<Product>()
                    .Where(p => p.ProductCode == productCode)
                    .ExecuteCommandAsync();

                return productRows;
            }
        }

        private static List<string> NormalizeStoreCodes(IEnumerable<string>? storeCodes)
        {
            return storeCodes?
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? new List<string>();
        }

        private static HashSet<string>? BuildStoreCodeSet(IReadOnlyCollection<string>? storeCodes)
        {
            if (storeCodes == null)
            {
                return null;
            }

            return NormalizeStoreCodes(storeCodes).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private async Task ExecuteStoreRecordPartialUpdateAsync(
            string productCode,
            string storeCode,
            BatchUpdateProductStoreRecordChangesDto changes,
            string updatedBy
        )
        {
            var now = DateTime.Now;
            var update = _db.Updateable<StoreRetailPrice>()
                .SetColumns(record => record.UpdatedBy == updatedBy)
                .SetColumns(record => record.UpdatedAt == now)
                .Where(record =>
                    record.ProductCode == productCode
                    && record.StoreCode == storeCode
                    && record.IsDeleted == false
                );

            // 只写入请求显式勾选的业务字段，避免整实体更新覆盖未传字段。
            if (changes.PurchasePrice.HasValue)
                update = update.SetColumns(record => record.PurchasePrice == changes.PurchasePrice.Value);
            if (changes.StoreRetailPriceValue.HasValue)
                update = update.SetColumns(record => record.StoreRetailPriceValue == changes.StoreRetailPriceValue.Value);
            if (changes.DiscountRate.HasValue)
                update = update.SetColumns(record => record.DiscountRate == changes.DiscountRate.Value);
            if (changes.IsAutoPricing.HasValue)
                update = update.SetColumns(record => record.IsAutoPricing == changes.IsAutoPricing.Value);
            if (changes.IsSpecialProduct.HasValue)
                update = update.SetColumns(record => record.IsSpecialProduct == changes.IsSpecialProduct.Value);
            if (changes.IsActive.HasValue)
                update = update.SetColumns(record => record.IsActive == changes.IsActive.Value);

            await update.ExecuteCommandAsync();
        }

        private static string BuildBatchUpdateStoreRecordsMessage(BatchOperationReactResult result)
        {
            return $"批量更新分店记录完成: 成功{result.SuccessCount}条，失败{result.FailedCount}条";
        }

        /// <summary>
        /// 批量更新商品（使用事务）
        /// </summary>
        public async Task<ApiResponse<BatchOperationReactResult>> BatchUpdateAsync(
            List<BatchUpdateProductReactDto> items
        )
        {
            var result = new BatchOperationReactResult();
            var auditBatchGuid = Guid.NewGuid();
            var auditOccurredAtUtc = DateTime.UtcNow;

            try
            {
                // 使用事务
                var transaction = await _db.Ado.UseTranAsync(async () =>
                {
                    var productCodes = items
                        .Select(item => item.ProductCode)
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    SetChildPurchasePriceLockScope? lockScope = null;
                    if (productCodes.Count > 0)
                    {
                        // 批量主档更新允许修改本地供应商，必须与同商品身份变更互斥；
                        // Update 总闸仍允许无关商品的普通成本 Shared 锁并行。
                        lockScope = await SetChildPurchasePriceMutationLock.AcquireProductIdentitiesWithinBudgetAsync(
                            _db,
                            productCodes
                        );
                    }
                    var beforeSnapshots = await CaptureProductSnapshotsAsync(productCodes);

                    foreach (var item in items)
                    {
                        try
                        {
                            var product = await _db.Queryable<Product>()
                                .Where(p => p.ProductCode == item.ProductCode)
                                .FirstAsync();

                            if (product == null)
                            {
                                result.Errors.Add($"商品不存在: {item.ProductCode}");
                                result.FailedCount++;
                                continue;
                            }

                            var oldSupplierCode = product.LocalSupplierCode;

                            // 只更新提供的字段
                            if (item.ProductName != null)
                                product.ProductName = item.ProductName;
                            if (item.EnglishName != null)
                                product.EnglishName = item.EnglishName;
                            if (item.RetailPrice.HasValue)
                                product.RetailPrice = item.RetailPrice;
                            if (item.PurchasePrice.HasValue)
                                product.PurchasePrice = item.PurchasePrice;
                            if (item.IsActive.HasValue)
                                product.IsActive = item.IsActive.Value;
                            if (item.MiddlePackageQuantity.HasValue)
                                product.MiddlePackageQuantity = item.MiddlePackageQuantity;
                            if (item.IsAutoPricing.HasValue)
                                product.IsAutoPricing = item.IsAutoPricing.Value;
                            if (item.ProductCategoryGUID != null)
                                product.ProductCategoryGUID = item.ProductCategoryGUID;
                            if (item.LocalSupplierCode != null)
                                product.LocalSupplierCode = NormalizeLocalSupplierCode(item.LocalSupplierCode);

                            product.UpdatedAt = DateTime.Now;
                            var currentUser =
                                _httpContextAccessor.HttpContext?.User?.Identity?.Name ?? "System";
                            product.UpdatedBy = currentUser;

                            var categoryAssignments = new LocalSupplierCategoryAssignmentService(_db);
                            var touchesSupplierCategory =
                                item.LocalSupplierCode != null
                                || !string.IsNullOrWhiteSpace(item.SupplierCategoryGUID)
                                || item.ClearSupplierCategory == true;
                            // 先校验人工指定的分类，失败时本商品不写入任何字段。
                            if (
                                !string.IsNullOrWhiteSpace(item.SupplierCategoryGUID)
                                && !LocalSupplierCategoryConstants.IsHotBargain(product.LocalSupplierCode)
                            )
                            {
                                await categoryAssignments.ValidateManualCategoryAsync(
                                    product.LocalSupplierCode,
                                    item.SupplierCategoryGUID
                                );
                            }

                            await _db.Updateable(product).ExecuteCommandAsync();

                            if (touchesSupplierCategory)
                            {
                                await categoryAssignments.ApplyProductEditAsync(
                                    new LocalSupplierCategoryAssignmentService.ProductEditContext(
                                        product.ProductCode ?? item.ProductCode,
                                        OldProductCode: null,
                                        OldSupplierCode: oldSupplierCode,
                                        NewSupplierCode: product.LocalSupplierCode,
                                        OldItemNumber: product.ItemNumber,
                                        NewItemNumber: product.ItemNumber,
                                        RequestedCategoryGuid: item.SupplierCategoryGUID,
                                        ClearRequested: item.ClearSupplierCategory == true,
                                        Actor: currentUser
                                    )
                                );
                            }

                            if (item.IsAutoPricing.HasValue)
                            {
                                await _db.Updateable<StoreRetailPrice>()
                                    .SetColumns(srp =>
                                        srp.IsAutoPricing == item.IsAutoPricing.Value
                                    )
                                    .Where(srp => srp.ProductCode == item.ProductCode)
                                    .ExecuteCommandAsync();
                            }

                            result.SuccessCount++;
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add($"{item.ProductCode}: {ex.Message}");
                            result.FailedCount++;
                            _logger.LogError(
                                ex,
                                "批量更新单个商品失败: {ProductCode}",
                                item.ProductCode
                            );
                        }
                    }

                    if (lockScope != null)
                    {
                        var recalculation = await new SetChildPurchasePriceService(
                            _db
                        ).RecalculateLockedAsync(
                            lockScope,
                            productCodes,
                            storeCodes: null,
                            updatedBy: _httpContextAccessor.HttpContext?.User?.Identity?.Name
                                ?? "System"
                        );
                        EnsureSetChildPurchasePriceRecalculated(
                            recalculation,
                            productCodes
                        );
                    }

                    var afterSnapshots = await CaptureProductSnapshotsAsync(productCodes);
                    await RecordProductChangesAsync(
                        beforeSnapshots,
                        afterSnapshots,
                        "Update",
                        "ProductReact.BatchUpdate",
                        auditBatchGuid,
                        auditOccurredAtUtc
                    );
                });
                if (!transaction.IsSuccess)
                {
                    throw transaction.ErrorException
                        ?? new InvalidOperationException("批量更新商品事务失败");
                }

                return new ApiResponse<BatchOperationReactResult>
                {
                    Success = true,
                    Data = result,
                    Message =
                        $"批量更新完成: 成功{result.SuccessCount}条，失败{result.FailedCount}条",
                };
            }
            catch (Exception ex) when (
                SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out var conflict)
            )
            {
                // 外层事务已回滚，循环内的成功计数不能继续对外宣称写入成功。
                result.SuccessCount = 0;
                result.FailedCount = 0;
                result.Errors.Clear();
                result.FailureDetails.Clear();
                var message = conflict!.Message;
                foreach (var item in items ?? new List<BatchUpdateProductReactDto>())
                {
                    var itemKey = string.IsNullOrWhiteSpace(item.ProductCode)
                        ? "(空商品编码)"
                        : item.ProductCode.Trim();
                    result.FailedCount++;
                    result.Errors.Add($"{itemKey}: {message}");
                    result.FailureDetails.Add(new BatchOperationFailureDto
                    {
                        ItemKey = itemKey,
                        Message = message,
                        ErrorCode = SetChildPurchasePriceMutationLock.BusyErrorCode,
                    });
                }

                // 即使请求为空也保留一条结构化失败，避免客户端丢失事务回滚原因。
                if (result.FailureDetails.Count == 0)
                {
                    result.FailedCount = 1;
                    result.Errors.Add(message);
                    result.FailureDetails.Add(new BatchOperationFailureDto
                    {
                        Message = message,
                        ErrorCode = SetChildPurchasePriceMutationLock.BusyErrorCode,
                    });
                }

                _logger.LogWarning(ex, "批量更新商品遇到套装成本业务锁冲突，事务已回滚");
                return new ApiResponse<BatchOperationReactResult>
                {
                    Success = false,
                    Message = message,
                    ErrorCode = SetChildPurchasePriceMutationLock.BusyErrorCode,
                    Data = result,
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量更新商品事务失败");
                // 外层事务失败时，同批已累积的成功数对应的写入均已回滚。
                result.SuccessCount = 0;
                result.FailedCount = 0;
                result.Errors.Clear();
                result.FailureDetails.Clear();
                var message = $"批量更新失败: {ex.Message}";
                foreach (var item in items ?? new List<BatchUpdateProductReactDto>())
                {
                    var itemKey = string.IsNullOrWhiteSpace(item.ProductCode)
                        ? "(空商品编码)"
                        : item.ProductCode.Trim();
                    result.FailedCount++;
                    result.Errors.Add($"{itemKey}: {message}");
                    result.FailureDetails.Add(new BatchOperationFailureDto
                    {
                        ItemKey = itemKey,
                        Message = message,
                    });
                }

                if (result.FailureDetails.Count == 0)
                {
                    result.FailedCount = 1;
                    result.Errors.Add(message);
                    result.FailureDetails.Add(new BatchOperationFailureDto { Message = message });
                }

                return new ApiResponse<BatchOperationReactResult>
                {
                    Success = false,
                    Message = message,
                    Data = result,
                };
            }
        }

        public Task<ApiResponse<BatchUpdateSupplierImagesResult>> BatchUpdateSupplierImagesAsync(
            BatchUpdateSupplierImagesRequest request
        ) => BatchUpdateSupplierImagesAsync(request, null, null);

        public async Task<ApiResponse<BatchUpdateSupplierImagesResult>> BatchUpdateSupplierImagesAsync(
            BatchUpdateSupplierImagesRequest request,
            string? actorUserGuid,
            string? actorName
        )
        {
            var result = new BatchUpdateSupplierImagesResult();
            var supplierCode = request.LocalSupplierCode?.Trim();
            var template = request.UrlTemplate?.Trim();

            if (string.IsNullOrWhiteSpace(supplierCode))
                return ApiResponse<BatchUpdateSupplierImagesResult>.Error("请选择供应商", "SUPPLIER_REQUIRED", result);
            if (string.IsNullOrWhiteSpace(template) || !template.Contains("{itemNumber}"))
                return ApiResponse<BatchUpdateSupplierImagesResult>.Error("图片基础 URL 必须包含 {itemNumber}", "IMAGE_TEMPLATE_INVALID", result);
            if (!request.UpdateHbweb && !request.UpdateHq && !request.SaveSupplierImageBaseUrl)
                return ApiResponse<BatchUpdateSupplierImagesResult>.Error("请选择写入目标", "IMAGE_TARGET_REQUIRED", result);

            var supplier = await _db.Queryable<HBLocalSupplier>()
                .FirstAsync(row => row.LocalSupplierCode == supplierCode && !row.IsDeleted);
            if (supplier == null)
                return ApiResponse<BatchUpdateSupplierImagesResult>.Error("供应商不存在", "SUPPLIER_NOT_FOUND", result);

            var products = await _db.Queryable<Product>()
                .Where(row => row.LocalSupplierCode == supplierCode && !row.IsDeleted)
                .Select(row => new Product
                {
                    UUID = row.UUID,
                    ProductCode = row.ProductCode,
                    ItemNumber = row.ItemNumber,
                    ProductImage = row.ProductImage,
                })
                .ToListAsync();
            result.TotalCount = products.Count;
            if (products.Count == 0 && !request.SaveSupplierImageBaseUrl)
                return ApiResponse<BatchUpdateSupplierImagesResult>.OK(result, "该供应商没有可更新商品");

            var now = DateTime.UtcNow;
            var currentUser = string.IsNullOrWhiteSpace(actorName)
                ? _currentUserService.GetCurrentUsername()
                : actorName.Trim();
            if (string.IsNullOrWhiteSpace(currentUser))
            {
                currentUser = "System";
            }
            var validProducts = new List<(Product Product, string ImageUrl)>();
            foreach (var product in products)
            {
                if (string.IsNullOrWhiteSpace(product.ItemNumber))
                {
                    result.SkippedCount++;
                    result.Errors.Add($"{product.ProductCode}: 货号为空，已跳过");
                    continue;
                }

                var imageUrl = BuildSupplierImageUrl(template, supplierCode, product.ItemNumber);
                if (imageUrl.Length > ProductImageMaxLength)
                {
                    result.SkippedCount++;
                    result.Errors.Add($"{product.ProductCode}: 图片地址超过 {ProductImageMaxLength} 字符，已跳过");
                    continue;
                }

                validProducts.Add((product, imageUrl));
            }

            var auditBatchGuid = Guid.NewGuid();
            var auditOccurredAtUtc = DateTime.UtcNow;
            if (request.UpdateHbweb || request.SaveSupplierImageBaseUrl)
            {
                await _db.Ado.BeginTranAsync();
                try
                {
                    var beforeSnapshots = request.UpdateHbweb
                        ? await CaptureProductSnapshotsAsync(
                            validProducts
                                .Select(item => item.Product.ProductCode ?? string.Empty)
                        )
                        : new Dictionary<string, WarehouseProductChangeSnapshotDto>(
                            StringComparer.OrdinalIgnoreCase
                        );

                    if (request.SaveSupplierImageBaseUrl)
                    {
                        supplier.ImageBaseUrl = template;
                        supplier.UpdatedAt = now;
                        supplier.UpdatedBy = currentUser;
                        await _db.Updateable(supplier)
                            .UpdateColumns(row => new
                            {
                                row.ImageBaseUrl,
                                row.UpdatedAt,
                                row.UpdatedBy,
                            })
                            .ExecuteCommandAsync();
                    }

                    if (request.UpdateHbweb)
                    {
                        var hbwebCandidates = validProducts
                            .Where(item => string.IsNullOrWhiteSpace(item.Product.ProductImage))
                            .ToList();
                        result.HbwebSkippedExistingImageCount += validProducts.Count - hbwebCandidates.Count;

                        // 关键位置：Hbweb 只补空图片，已有图的商品保持原图片和原审计字段不动。
                        foreach (var item in hbwebCandidates)
                        {
                            // 写入条件再次限制“图片仍为空”，避免读写间被其他任务补图后又被本任务覆盖。
                            var affected = await _db.Updateable<Product>()
                                .SetColumns(row => new Product
                                {
                                    ProductImage = item.ImageUrl,
                                    UpdatedAt = now,
                                    UpdatedBy = currentUser,
                                })
                                .Where(row =>
                                    row.UUID == item.Product.UUID
                                    && (row.ProductImage == null || row.ProductImage.Trim() == string.Empty))
                                .ExecuteCommandAsync();

                            if (affected > 0)
                            {
                                result.HbwebUpdatedCount += affected;
                            }
                            else
                            {
                                result.HbwebSkippedExistingImageCount++;
                            }
                        }
                    }

                    if (request.UpdateHbweb)
                    {
                        var afterSnapshots = await CaptureProductSnapshotsAsync(
                            validProducts
                                .Select(item => item.Product.ProductCode ?? string.Empty)
                        );
                        await RecordProductChangesAsync(
                            beforeSnapshots,
                            afterSnapshots,
                            "Update",
                            "ProductReact.SupplierImage",
                            auditBatchGuid,
                            auditOccurredAtUtc,
                            actorUserGuid,
                            actorName
                        );
                    }

                    await _db.Ado.CommitTranAsync();
                }
                catch (Exception ex)
                {
                    await _db.Ado.RollbackTranAsync();
                    _logger.LogError(ex, "批量更新供应商商品图片到 Hbweb 失败: {SupplierCode}", supplierCode);
                    AddBatchImageError(result, ex.Message);
                    LimitBatchImageErrors(result);
                    return ApiResponse<BatchUpdateSupplierImagesResult>.Error("更新 Hbweb 商品图片失败", "HBWEB_IMAGE_UPDATE_ERROR", result);
                }
            }

            if (request.UpdateHq && validProducts.Count > 0)
            {
                var hqDb = _hqContext.Db;
                try
                {
                    foreach (var batch in validProducts.Chunk(ProductHqSyncReadBatchSize))
                    {
                        var batchList = batch.ToList();
                        var productCodes = batchList
                            .Select(item => item.Product.ProductCode)
                            .Where(code => !string.IsNullOrWhiteSpace(code))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        var hqRows = await hqDb.Queryable<DIC_商品信息字典表>()
                            .Where(row => productCodes.Contains(row.H商品编码))
                            .Select(row => new DIC_商品信息字典表
                            {
                                ID = row.ID,
                                H商品编码 = row.H商品编码,
                                H商品图片 = row.H商品图片,
                            })
                            .ToListAsync();
                        var hqRowsByProductCode = hqRows
                            .Where(row => !string.IsNullOrWhiteSpace(row.H商品编码))
                            .GroupBy(row => row.H商品编码!, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

                        foreach (var item in batchList)
                        {
                            if (string.IsNullOrWhiteSpace(item.Product.ProductCode))
                            {
                                result.HqFailedCount++;
                                AddBatchImageError(result, "存在缺少商品编码的商品，未更新图片");
                                continue;
                            }

                            if (!hqRowsByProductCode.TryGetValue(item.Product.ProductCode, out var matchedRows))
                            {
                                result.HqFailedCount++;
                                AddBatchImageError(result, $"{item.Product.ProductCode}: HQ 商品不存在，未更新图片");
                                continue;
                            }

                            if (matchedRows.Count != 1)
                            {
                                result.HqFailedCount++;
                                AddBatchImageError(result, $"{item.Product.ProductCode}: HQ 商品编码重复 {matchedRows.Count} 条，未更新图片");
                                continue;
                            }

                            var target = matchedRows[0];
                            // 关键位置：HQ 和 Hbweb 独立判断，这里只要 HQ 为空就补图，不受 Hbweb 是否已有图影响。
                            if (!string.IsNullOrWhiteSpace(target.H商品图片))
                            {
                                result.HqSkippedExistingImageCount++;
                                continue;
                            }

                            var affected = await hqDb.Updateable<DIC_商品信息字典表>()
                                .SetColumns(row => new DIC_商品信息字典表
                                {
                                    H商品图片 = item.ImageUrl,
                                    FGC_LastModifier = currentUser,
                                    FGC_LastModifyDate = now,
                                })
                                .Where(row =>
                                    row.ID == target.ID
                                    && (row.H商品图片 == null || row.H商品图片.Trim() == string.Empty))
                                .ExecuteCommandAsync();
                            if (affected > 0)
                            {
                                result.HqUpdatedCount += affected;
                            }
                            else
                            {
                                var currentImage = await hqDb.Queryable<DIC_商品信息字典表>()
                                    .Where(row => row.ID == target.ID)
                                    .Select(row => row.H商品图片)
                                    .FirstAsync();
                                if (!string.IsNullOrWhiteSpace(currentImage))
                                {
                                    result.HqSkippedExistingImageCount++;
                                }
                                else
                                {
                                    result.HqFailedCount++;
                                    AddBatchImageError(result, $"{item.Product.ProductCode}: HQ 商品不存在，未更新图片");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "批量更新供应商商品图片到 HQ 失败: {SupplierCode}", supplierCode);
                    AddBatchImageError(result, ex.Message);
                    if (request.UpdateHbweb || request.SaveSupplierImageBaseUrl)
                    {
                        result.HqFailedCount += Math.Max(
                            0,
                            validProducts.Count
                                - result.HqUpdatedCount
                                - result.HqSkippedExistingImageCount
                                - result.HqFailedCount
                        );
                        LimitBatchImageErrors(result);
                        return ApiResponse<BatchUpdateSupplierImagesResult>.OK(result, "批量更新供应商商品图片部分完成，HQ 更新失败");
                    }

                    LimitBatchImageErrors(result);
                    return ApiResponse<BatchUpdateSupplierImagesResult>.Error("更新 HQ 商品图片失败", "HQ_IMAGE_UPDATE_ERROR", result);
                }
            }

            LimitBatchImageErrors(result);
            return ApiResponse<BatchUpdateSupplierImagesResult>.OK(result, "批量更新供应商商品图片完成");
        }

        private static void AddBatchImageError(BatchUpdateSupplierImagesResult result, string message)
        {
            result.Errors.Add(message);
        }

        private static void LimitBatchImageErrors(BatchUpdateSupplierImagesResult result)
        {
            if (result.Errors.Count <= BatchImageMaxErrorDetails)
            {
                return;
            }

            var hiddenCount = result.Errors.Count - (BatchImageMaxErrorDetails - 1);
            result.Errors = result.Errors
                .Take(BatchImageMaxErrorDetails - 1)
                .ToList();
            result.Errors.Add($"还有 {hiddenCount} 条错误未显示");
        }

        private static string BuildSupplierImageUrl(string template, string supplierCode, string itemNumber)
        {
            // 图片模板只替换明确允许的占位符，避免误改供应商自定义 URL 的其他内容。
            return template
                .Replace("{supplierCode}", Uri.EscapeDataString(supplierCode))
                .Replace("{itemNumber}", Uri.EscapeDataString(itemNumber));
        }

        /// <summary>
        /// 批量删除商品（使用事务，支持软删除和物理删除）
        /// </summary>
        /// <param name="productCodes">商品编码列表</param>
        /// <param name="isSoftDelete">true=软删除，false=物理删除</param>
        public async Task<ApiResponse<BatchOperationReactResult>> BatchDeleteAsync(
            List<string> productCodes,
            bool isSoftDelete = true
        )
        {
            var result = new BatchOperationReactResult();
            var deleteType = isSoftDelete ? "软删除" : "物理删除";

            foreach (var code in productCodes)
            {
                if (string.IsNullOrWhiteSpace(code))
                {
                    AddBatchDeleteFailure(result, code, "商品编码不能为空");
                    continue;
                }

                try
                {
                    await _db.Ado.BeginTranAsync();
                    try
                    {
                        await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                            _db,
                            new[] { code }
                        );
                        var productExists = await _db.Queryable<Product>()
                            .Where(product =>
                                product.ProductCode == code && product.IsDeleted == false
                            )
                            .AnyAsync();
                        if (!productExists)
                        {
                            await _db.Ado.RollbackTranAsync();
                            AddBatchDeleteFailure(result, code, $"商品不存在: {code}");
                            continue;
                        }

                        var productDeleted = await CascadeDeleteProductAsync(code, isSoftDelete);
                        if (productDeleted <= 0)
                        {
                            throw new InvalidOperationException("商品在等待业务锁期间已变更");
                        }

                        await _db.Ado.CommitTranAsync();
                        result.SuccessCount++;
                    }
                    catch
                    {
                        if (_db.Ado.Transaction != null)
                        {
                            await _db.Ado.RollbackTranAsync();
                        }
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    var errorCode = SetChildPurchasePriceMutationLock.TryResolveConflict(
                        ex,
                        out var conflict
                    )
                        ? SetChildPurchasePriceMutationLock.BusyErrorCode
                        : null;
                    var message = $"{code}: {conflict?.Message ?? ex.Message}";
                    AddBatchDeleteFailure(result, code, message, errorCode);
                    _logger.LogError(
                        ex,
                        "批量{DeleteType}单个商品失败: {ProductCode}",
                        deleteType,
                        code
                    );
                }
            }

            var containsBusyFailure = result.FailureDetails.Any(failure =>
                string.Equals(
                    failure.ErrorCode,
                    SetChildPurchasePriceMutationLock.BusyErrorCode,
                    StringComparison.Ordinal
                )
            );
            if (result.SuccessCount == 0 && containsBusyFailure)
            {
                return new ApiResponse<BatchOperationReactResult>
                {
                    Success = false,
                    ErrorCode = SetChildPurchasePriceMutationLock.BusyErrorCode,
                    Message = "套装子项成本正在被其他操作更新，请稍后重试",
                    Data = result,
                };
            }

            return new ApiResponse<BatchOperationReactResult>
            {
                Success = true,
                Data = result,
                Message =
                    $"批量{deleteType}完成: 成功{result.SuccessCount}条，失败{result.FailedCount}条",
            };
        }

        private static void AddBatchDeleteFailure(
            BatchOperationReactResult result,
            string? itemKey,
            string message,
            string? errorCode = null
        )
        {
            result.FailedCount++;
            result.Errors.Add(message);
            result.FailureDetails.Add(new BatchOperationFailureDto
            {
                ItemKey = itemKey ?? string.Empty,
                Message = message,
                ErrorCode = errorCode,
            });
        }

        /// <summary>
        /// 高级过滤查询商品列表（支持商品信息表与分店价格表的组合过滤）
        /// 性能优化:使用 INNER JOIN + 索引 + 分页,确保大数据量下的响应速度
        /// </summary>
        public async Task<PagedProductPriceListDto> GetPriceFilteredPagedListAsync(
            ProductPriceFilterDto filter
        )
        {
            try
            {
                var sw = Stopwatch.StartNew();

                // 与商品主表 INNER JOIN
                var query = _db.Queryable<Product>()
                    .InnerJoin<StoreRetailPrice>((p, sp) => p.ProductCode == sp.ProductCode)
                    .InnerJoin<Store>((p, sp, s) => sp.StoreCode == s.StoreCode)
                    .LeftJoin<HBLocalSupplier>(
                        (p, sp, s, ls) => sp.SupplierCode == ls.LocalSupplierCode
                    );

                var filterStart = sw.ElapsedMilliseconds;

                // 应用分店过滤条件(优化:先过滤StoreCode)
                if (filter.StoreCodes != null && filter.StoreCodes.Any())
                {
                    query = query.Where((p, sp, s, ls) =>
                        sp.StoreCode != null && filter.StoreCodes.Contains(sp.StoreCode)
                    );
                }

                // 应用商品主表过滤条件
                if (!string.IsNullOrWhiteSpace(filter.Search))
                {
                    var keyword = filter.Search.Trim();
                    query = query.Where(
                        (p, sp, s, ls) =>
                            (p.ProductName != null && p.ProductName.Contains(keyword))
                            || (p.ProductCode != null && p.ProductCode.Contains(keyword))
                            || (p.ItemNumber != null && p.ItemNumber.Contains(keyword))
                            || (p.Barcode != null && p.Barcode.Contains(keyword))
                    );
                }

                if (!string.IsNullOrWhiteSpace(filter.LocalSupplierCode))
                {
                    query = query.Where(
                        (p, sp, s, ls) => p.LocalSupplierCode == filter.LocalSupplierCode
                    );
                }

                if (filter.IsActive.HasValue)
                {
                    query = query.Where((p, sp, s, ls) => p.IsActive == filter.IsActive.Value);
                }

                if (filter.IsSpecialProduct.HasValue)
                {
                    query = query.Where(
                        (p, sp, s, ls) => p.IsSpecialProduct == filter.IsSpecialProduct.Value
                    );
                }

                if (!string.IsNullOrWhiteSpace(filter.WarehouseCategoryGUID))
                {
                    query = query.Where(
                        (p, sp, s, ls) => p.WarehouseCategoryGUID == filter.WarehouseCategoryGUID
                    );
                }

                if (filter.ProductType.HasValue)
                {
                    query = query.Where(
                        (p, sp, s, ls) => p.ProductType == filter.ProductType.Value
                    );
                }

                if (!string.IsNullOrWhiteSpace(filter.UpdatedBy))
                {
                    var name = filter.UpdatedBy.Trim();
                    query = query.Where(
                        (p, sp, s, ls) => p.UpdatedBy != null && p.UpdatedBy.Contains(name)
                    );
                }

                // 商品主表价格区间过滤 (使用 BETWEEN 语法,支持闭区间)
                if (filter.ProductPurchasePriceMin.HasValue)
                {
                    query = query.Where(
                        (p, sp, s, ls) => p.PurchasePrice >= filter.ProductPurchasePriceMin.Value
                    );
                }

                if (filter.ProductPurchasePriceMax.HasValue)
                {
                    query = query.Where(
                        (p, sp, s, ls) => p.PurchasePrice <= filter.ProductPurchasePriceMax.Value
                    );
                }

                if (filter.ProductRetailPriceMin.HasValue)
                {
                    query = query.Where(
                        (p, sp, s, ls) => p.RetailPrice >= filter.ProductRetailPriceMin.Value
                    );
                }

                if (filter.ProductRetailPriceMax.HasValue)
                {
                    query = query.Where(
                        (p, sp, s, ls) => p.RetailPrice <= filter.ProductRetailPriceMax.Value
                    );
                }

                // 分店价格表过滤条件
                if (filter.StorePurchasePriceMin.HasValue)
                {
                    query = query.Where(
                        (p, sp, s, ls) => sp.PurchasePrice >= filter.StorePurchasePriceMin.Value
                    );
                }

                if (filter.StorePurchasePriceMax.HasValue)
                {
                    query = query.Where(
                        (p, sp, s, ls) => sp.PurchasePrice <= filter.StorePurchasePriceMax.Value
                    );
                }

                if (filter.StoreRetailPriceMin.HasValue)
                {
                    query = query.Where(
                        (p, sp, s, ls) =>
                            sp.StoreRetailPriceValue >= filter.StoreRetailPriceMin.Value
                    );
                }

                if (filter.StoreRetailPriceMax.HasValue)
                {
                    query = query.Where(
                        (p, sp, s, ls) =>
                            sp.StoreRetailPriceValue <= filter.StoreRetailPriceMax.Value
                    );
                }

                if (filter.StoreDiscountRateMin.HasValue)
                {
                    query = query.Where(
                        (p, sp, s, ls) => sp.DiscountRate >= filter.StoreDiscountRateMin.Value
                    );
                }

                if (filter.StoreDiscountRateMax.HasValue)
                {
                    query = query.Where(
                        (p, sp, s, ls) => sp.DiscountRate <= filter.StoreDiscountRateMax.Value
                    );
                }

                if (filter.StoreIsActive.HasValue)
                {
                    query = query.Where(
                        (p, sp, s, ls) => sp.IsActive == filter.StoreIsActive.Value
                    );
                }

                if (filter.StoreIsAutoPricing.HasValue)
                {
                    query = query.Where(
                        (p, sp, s, ls) => sp.IsAutoPricing == filter.StoreIsAutoPricing.Value
                    );
                }

                _logger.LogDebug(
                    "过滤条件应用耗时: {ElapsedMs}ms",
                    sw.ElapsedMilliseconds - filterStart
                );

                // 应用排序
                if (!string.IsNullOrWhiteSpace(filter.SortBy))
                {
                    var isDesc = filter.SortOrder?.ToLower() == "desc";
                    var sortField = filter.SortBy.ToLower();

                    switch (sortField)
                    {
                        case "productcode":
                            query = isDesc
                                ? query.OrderBy((p, sp, s, ls) => p.ProductCode, OrderByType.Desc)
                                : query.OrderBy((p, sp, s, ls) => p.ProductCode, OrderByType.Asc);
                            break;
                        case "productname":
                            query = isDesc
                                ? query.OrderBy((p, sp, s, ls) => p.ProductName, OrderByType.Desc)
                                : query.OrderBy((p, sp, s, ls) => p.ProductName, OrderByType.Asc);
                            break;
                        case "itemnumber":
                            query = isDesc
                                ? query.OrderBy((p, sp, s, ls) => p.ItemNumber, OrderByType.Desc)
                                : query.OrderBy((p, sp, s, ls) => p.ItemNumber, OrderByType.Asc);
                            break;
                        case "productpurchaseprice":
                            query = isDesc
                                ? query.OrderBy((p, sp, s, ls) => p.PurchasePrice, OrderByType.Desc)
                                : query.OrderBy((p, sp, s, ls) => p.PurchasePrice, OrderByType.Asc);
                            break;
                        case "productretailprice":
                            query = isDesc
                                ? query.OrderBy((p, sp, s, ls) => p.RetailPrice, OrderByType.Desc)
                                : query.OrderBy((p, sp, s, ls) => p.RetailPrice, OrderByType.Asc);
                            break;
                        case "storepurchaseprice":
                            query = isDesc
                                ? query.OrderBy(
                                    (p, sp, s, ls) => sp.PurchasePrice,
                                    OrderByType.Desc
                                )
                                : query.OrderBy(
                                    (p, sp, s, ls) => sp.PurchasePrice,
                                    OrderByType.Asc
                                );
                            break;
                        case "storeretailprice":
                            query = isDesc
                                ? query.OrderBy(
                                    (p, sp, s, ls) => sp.StoreRetailPriceValue,
                                    OrderByType.Desc
                                )
                                : query.OrderBy(
                                    (p, sp, s, ls) => sp.StoreRetailPriceValue,
                                    OrderByType.Asc
                                );
                            break;
                        case "storediscountrate":
                            query = isDesc
                                ? query.OrderBy((p, sp, s, ls) => sp.DiscountRate, OrderByType.Desc)
                                : query.OrderBy((p, sp, s, ls) => sp.DiscountRate, OrderByType.Asc);
                            break;
                        case "createdat":
                            query = isDesc
                                ? query.OrderBy((p, sp, s, ls) => p.CreatedAt, OrderByType.Desc)
                                : query.OrderBy((p, sp, s, ls) => p.CreatedAt, OrderByType.Asc);
                            break;
                        case "updatedat":
                            query = isDesc
                                ? query.OrderBy((p, sp, s, ls) => p.UpdatedAt, OrderByType.Desc)
                                : query.OrderBy((p, sp, s, ls) => p.UpdatedAt, OrderByType.Asc);
                            break;
                        default:
                            query = query.OrderBy((p, sp, s, ls) => p.UpdatedAt, OrderByType.Desc);
                            break;
                    }
                }
                else
                {
                    query = query.OrderBy((p, sp, s, ls) => p.UpdatedAt, OrderByType.Desc);
                }

                var countStart = sw.ElapsedMilliseconds;
                var totalQuery = query.Clone();
                var total = await totalQuery.CountAsync();
                _logger.LogDebug(
                    "Count查询耗时: {ElapsedMs}ms, 总数: {Total}",
                    sw.ElapsedMilliseconds - countStart,
                    total
                );

                var selectStart = sw.ElapsedMilliseconds;
                var items = await query
                    .Skip((filter.PageNumber - 1) * filter.PageSize)
                    .Take(filter.PageSize)
                    .Select<ProductPriceListItemDto>(
                        "p.UUID,p.ProductCode,p.ItemNumber,p.Barcode,p.ProductName,p.EnglishName,p.LocalSupplierCode,p.ProductType,p.MiddlePackageQuantity,p.PurchasePrice as ProductPurchasePrice,p.RetailPrice as ProductRetailPrice,p.IsAutoPricing,p.IsSpecialProduct,p.IsActive,p.ProductImage,p.WarehouseCategoryGUID,p.UpdatedBy,p.UpdatedAt,sp.StoreCode,s.StoreName,sp.StoreProductCode,sp.SupplierCode,ls.Name as SupplierName,sp.PurchasePrice as StorePurchasePrice,sp.StoreRetailPriceValue as StoreRetailPrice,sp.DiscountRate as StoreDiscountRate,sp.IsActive as StoreIsActive,sp.IsAutoPricing as StoreIsAutoPricing"
                    )
                    .ToListAsync();

                _logger.LogDebug(
                    "Select查询耗时: {ElapsedMs}ms, 返回: {Count}",
                    sw.ElapsedMilliseconds - selectStart,
                    items.Count
                );

                var elapsedMs = sw.ElapsedMilliseconds;
                _logger.LogInformation(
                    "高级过滤查询完成, 耗时: {ElapsedMs}ms, 总数: {Total}, 返回: {Count}",
                    elapsedMs,
                    total,
                    items.Count
                );

                // 性能警告
                if (elapsedMs > 500)
                {
                    _logger.LogWarning(
                        "高级过滤查询性能警告: 耗时 {ElapsedMs}ms 超过500ms阈值",
                        elapsedMs
                    );
                }

                return new PagedProductPriceListDto
                {
                    Items = items,
                    Total = total,
                    PageNumber = filter.PageNumber,
                    PageSize = filter.PageSize,
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "高级过滤查询商品失败");
                throw;
            }
        }

        #region 从HQ同步商品

        /// <summary>
        /// 从HQ同步商品到本地（含增删改 + 关联表同步）
        /// 按更新日期比对，只更新HQ侧更新的商品
        /// </summary>
        /// <returns>同步结果</returns>
        public Task<ApiResponse<HqProductSyncResult>> SyncProductsFromHqAsync()
        {
            return SyncProductsFromHqAsync(null, null);
        }

        public async Task<ApiResponse<HqProductSyncResult>> SyncProductsFromHqAsync(
            string? actorUserGuid,
            string? actorName
        )
        {
            if (!await ProductHqSyncSemaphore.WaitAsync(0))
            {
                return ApiResponse<HqProductSyncResult>.Error(
                    "已有商品HQ同步任务正在执行，请稍后再试"
                );
            }

            var result = new HqProductSyncResult();
            var startTime = DateTime.Now;
            var errors = new List<string>();
            var auditBatchGuid = Guid.NewGuid();
            var auditOccurredAtUtc = DateTime.UtcNow;
            var originalTimeout = _db.Ado.CommandTimeOut;
            var databaseLockAcquired = false;
            var shouldCloseDatabaseLockConnection = false;
            var transactionStarted = false;
            _db.Ado.CommandTimeOut = 300;

            try
            {
                var databaseLock = await TryAcquireProductHqSyncDatabaseLockAsync();
                databaseLockAcquired = databaseLock.Acquired;
                shouldCloseDatabaseLockConnection = databaseLock.ShouldCloseConnection;
                if (!databaseLockAcquired)
                {
                    return ApiResponse<HqProductSyncResult>.Error(
                        "已有商品HQ同步任务正在执行，请稍后再试"
                    );
                }

                _logger.LogInformation("开始从HQ同步商品数据...");

                _hqContext.CheckConnection();

                // 旧全量入口没有商品过滤，必须在读取本地快照前取得全局业务锁，
                // 防止同步期间用旧实体覆盖其他入口刚写入的成本来源。
                _db.Ado.BeginTran();
                transactionStarted = true;
                var childCostLockScope = await SetChildPurchasePriceMutationLock.AcquireAllAsync(_db);

                _logger.LogInformation("阶段一+二：并发构建轻量索引...");
                var localDataTask = Task.Run(async () =>
                {
                    var stores = await _db.Queryable<Store>()
                        .Where(s => s.IsActive && !s.IsDeleted)
                        .ToListAsync();
                    var indexList = await _db.Queryable<Product>()
                        .Where(p => p.ProductCode != null)
                        .Select(p => new
                        {
                            p.ProductCode,
                            p.UpdatedAt,
                            p.IsDeleted,
                        })
                        .ToListAsync();
                    return (stores, indexList);
                });

                var hqIndexTask = _hqContext
                    .DIC_商品信息字典表Db.AsQueryable()
                    .Where(p => p.H使用状态 == true && !string.IsNullOrEmpty(p.H商品编码))
                    .Select(p => new { p.H商品编码, p.FGC_LastModifyDate })
                    .ToListAsync();

                await Task.WhenAll(localDataTask, hqIndexTask);

                var (activeStores, localIndexList) = await localDataTask;
                var hqIndexList = await hqIndexTask;

                var localIndex = localIndexList
                    .GroupBy(x => x.ProductCode!)
                    .ToDictionary(g => g.Key, g => g.First());
                var localProductCodeSet = localIndex.Keys.ToHashSet();
                var hqIndexDict = hqIndexList.ToDictionary(p => p.H商品编码!, p => p.FGC_LastModifyDate);

                _logger.LogInformation(
                    "本地商品数: {Count}, 激活分店数: {StoreCount}, HQ商品数: {HqCount}",
                    localIndex.Count,
                    activeStores.Count,
                    hqIndexDict.Count
                );

                result.TotalHqProducts = hqIndexDict.Count;

                _logger.LogInformation("阶段三：轻量比对分类...");
                var toAddCodes = new List<string>();
                var toUpdateCodes = new List<string>();

                foreach (var (code, hqModifyDate) in hqIndexDict)
                {
                    if (!localProductCodeSet.Contains(code))
                    {
                        toAddCodes.Add(code);
                    }
                    else if (
                        localIndex[code].IsDeleted
                        || hqModifyDate > (localIndex[code].UpdatedAt ?? DateTime.MinValue)
                    )
                    {
                        toUpdateCodes.Add(code);
                    }
                }

                var toDeleteCodes = localIndex.Keys
                    .Where(code => !localIndex[code].IsDeleted && !hqIndexDict.ContainsKey(code))
                    .ToList();

                _logger.LogInformation(
                    "待新增: {AddCount}, 待更新: {UpdateCount}, 待删除: {DeleteCount}",
                    toAddCodes.Count,
                    toUpdateCodes.Count,
                    toDeleteCodes.Count
                );

                _logger.LogInformation("阶段四：按需加载完整数据...");
                var neededCodes = toAddCodes.Concat(toUpdateCodes).ToList();
                var hqFullDict = new Dictionary<string, DIC_商品信息字典表>();
                foreach (var codeBatch in neededCodes.Chunk(ProductHqSyncReadBatchSize))
                {
                    var batch = await _hqContext
                        .DIC_商品信息字典表Db.AsQueryable()
                        .Where(p => codeBatch.Contains(p.H商品编码!))
                        .ToListAsync();
                    foreach (var item in batch)
                    {
                        if (item.H商品编码 != null)
                            hqFullDict[item.H商品编码] = item;
                    }
                }

                var toUpdateProductsDict = new Dictionary<string, Product>();
                foreach (var codeBatch in toUpdateCodes.Chunk(ProductHqSyncReadBatchSize))
                {
                    var batch = await _db.Queryable<Product>()
                        .Where(p => codeBatch.Contains(p.ProductCode!))
                        .ToListAsync();
                    foreach (var item in batch)
                    {
                        if (item.ProductCode != null)
                            toUpdateProductsDict[item.ProductCode] = item;
                    }
                }

                var toAdd = toAddCodes.Where(c => hqFullDict.ContainsKey(c))
                    .Select(c => hqFullDict[c]).ToList();
                var toUpdate = toUpdateCodes
                    .Where(c => hqFullDict.ContainsKey(c) && toUpdateProductsDict.ContainsKey(c))
                    .Select(c => (hqFullDict[c], toUpdateProductsDict[c]))
                    .ToList();
                int processedPage = 0;

                foreach (var batch in toAdd.Chunk(ProductHqSyncWriteBatchSize))
                {
                    var batchProductCodes = batch
                            .Select(item => item.H商品编码)
                            .Where(code => !string.IsNullOrWhiteSpace(code))
                            .Select(code => code!)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        var beforeSnapshots = await CaptureProductSnapshotsAsync(batchProductCodes);
                        var now = DateTime.UtcNow;
                        var batchHqByCode = batch
                            .Where(hq => !string.IsNullOrEmpty(hq.H商品编码))
                            .ToDictionary(hq => hq.H商品编码!, hq => hq);
                        var newProducts = batch.Select(hq => _mapper.Map<Product>(hq)).ToList();
                        foreach (var p in newProducts)
                        {
                            p.CreatedAt = now;
                            p.UpdatedAt = now;
                            p.IsDeleted = false;
                            if (
                                p.ProductCode != null
                                && batchHqByCode.TryGetValue(p.ProductCode, out var hqProduct)
                            )
                            {
                                p.EnglishName = Truncate(hqProduct.H大写名称, 200);
                            }
                        }
                        await BulkInsertAsync(newProducts, ProductHqSyncWriteBatchSize);
                        result.ProductsAdded += newProducts.Count;

                        await SyncTouchedProductAssociationsAsync(
                            newProducts,
                            batchHqByCode,
                            activeStores,
                            now,
                            result
                        );

                        var afterSnapshots = await CaptureProductSnapshotsAsync(batchProductCodes);
                        await RecordProductChangesAsync(
                            beforeSnapshots,
                            afterSnapshots,
                            "Create",
                            "ProductReact.HqSync",
                            auditBatchGuid,
                            auditOccurredAtUtc,
                            actorUserGuid,
                            actorName
                        );

                    processedPage++;
                    if (processedPage % 5 == 0)
                    {
                        await Task.Delay(500);
                        _logger.LogInformation(
                            "新增进度: {Processed}/{Total}",
                            processedPage * ProductHqSyncWriteBatchSize,
                            toAdd.Count
                        );
                    }
                }

                processedPage = 0;
                foreach (var batch in toUpdate.Chunk(ProductHqSyncWriteBatchSize))
                {
                    var batchProductCodes = batch
                            .Select(item => item.Item1.H商品编码)
                            .Where(code => !string.IsNullOrWhiteSpace(code))
                            .Select(code => code!)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        var beforeSnapshots = await CaptureProductSnapshotsAsync(batchProductCodes);
                        var now = DateTime.UtcNow;
                        foreach (var (hqProduct, localProduct) in batch)
                        {
                            var uuid = localProduct.UUID;
                            var createdAt = localProduct.CreatedAt;
                            var createdBy = localProduct.CreatedBy;
                            _mapper.Map(hqProduct, localProduct);
                            localProduct.UUID = uuid;
                            localProduct.CreatedAt = createdAt;
                            localProduct.CreatedBy = createdBy;
                            localProduct.EnglishName = Truncate(hqProduct.H大写名称, 200);
                            localProduct.UpdatedAt = now;
                            localProduct.IsDeleted = false;
                        }
                        var productsToUpdate = batch.Select(b => b.Item2).ToList();
                        await _db.Updateable(productsToUpdate)
                            .UpdateColumns(p => new
                            {
                                p.ProductName,
                                p.EnglishName,
                                p.ItemNumber,
                                p.Barcode,
                                p.ProductCategoryGUID,
                                p.LocalSupplierCode,
                                p.ProductImage,
                                p.ProductType,
                                p.MiddlePackageQuantity,
                                p.PurchasePrice,
                                p.RetailPrice,
                                p.IsActive,
                                p.IsAutoPricing,
                                p.IsSpecialProduct,
                                p.WarehouseCategoryGUID,
                                p.UpdatedAt,
                                p.IsDeleted,
                            })
                            .ExecuteCommandAsync();
                        result.ProductsUpdated += productsToUpdate.Count;
                        var batchHqByCode = batch
                            .Where(item => !string.IsNullOrEmpty(item.Item1.H商品编码))
                            .ToDictionary(item => item.Item1.H商品编码!, item => item.Item1);
                        await SyncTouchedProductAssociationsAsync(
                            productsToUpdate,
                            batchHqByCode,
                            activeStores,
                            now,
                            result
                        );

                        var afterSnapshots = await CaptureProductSnapshotsAsync(batchProductCodes);
                        await RecordProductChangesAsync(
                            beforeSnapshots,
                            afterSnapshots,
                            "Update",
                            "ProductReact.HqSync",
                            auditBatchGuid,
                            auditOccurredAtUtc,
                            actorUserGuid,
                            actorName
                        );
                    processedPage++;
                    if (processedPage % 5 == 0)
                    {
                        await Task.Delay(500);
                        _logger.LogInformation(
                            "更新进度: {Processed}/{Total}",
                            processedPage * ProductHqSyncWriteBatchSize,
                            toUpdate.Count
                        );
                    }
                }

                _logger.LogInformation("阶段五：处理多余商品（HQ没有的软删除）...");

                _logger.LogInformation("待删除商品数: {Count}", toDeleteCodes.Count);
                result.TotalLocalProducts = localIndex.Count;

                processedPage = 0;
                foreach (var codeBatch in toDeleteCodes.Chunk(ProductHqSyncWriteBatchSize))
                {
                    var productCodes = codeBatch.ToList();
                    var protectedSetRows = await _db.Queryable<ProductSetCode>()
                        .Where(p =>
                            productCodes.Contains(p.ProductCode)
                            && p.SetType == 1
                        )
                        .ToListAsync();
                    var protectedSetKeys = protectedSetRows
                        .Select(row => BuildKey(row.ProductCode, row.SetProductCode))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var protectedStoreMultiIds = (await _db.Queryable<StoreMultiCodeProduct>()
                            .Where(p => productCodes.Contains(p.ProductCode!))
                            .ToListAsync())
                        .Where(row =>
                            protectedSetKeys.Contains(
                                BuildKey(row.ProductCode, row.MultiCodeProductCode)
                            )
                        )
                        .Select(row => row.UUID)
                        .Where(uuid => !string.IsNullOrWhiteSpace(uuid))
                        .Select(uuid => uuid!)
                        .ToList();
                    var retailPricesDeleted = await _db.Queryable<StoreRetailPrice>()
                            .Where(p => productCodes.Contains(p.ProductCode!))
                            .CountAsync();
                        var setCodesDeleted = await _db.Queryable<ProductSetCode>()
                            .Where(p =>
                                productCodes.Contains(p.ProductCode!)
                                && p.SetType != 1
                            )
                            .CountAsync();
                        var multiCodesDeleted = protectedStoreMultiIds.Count == 0
                            ? await _db.Queryable<StoreMultiCodeProduct>()
                                .Where(p => productCodes.Contains(p.ProductCode!))
                                .CountAsync()
                            : await _db.Queryable<StoreMultiCodeProduct>()
                                .Where(p =>
                                    productCodes.Contains(p.ProductCode!)
                                    && !protectedStoreMultiIds.Contains(p.UUID)
                                )
                                .CountAsync();

                        await _db.Updateable<Product>()
                            .SetColumns(p => p.IsDeleted == true)
                            .Where(p => productCodes.Contains(p.ProductCode!))
                            .ExecuteCommandAsync();

                        await _db.Updateable<StoreRetailPrice>()
                            .SetColumns(p => p.IsDeleted == true)
                            .Where(p => productCodes.Contains(p.ProductCode!))
                            .ExecuteCommandAsync();

                        await _db.Updateable<ProductSetCode>()
                            .SetColumns(p => p.IsDeleted == true)
                            .Where(p =>
                                productCodes.Contains(p.ProductCode!)
                                && p.SetType != 1
                            )
                            .ExecuteCommandAsync();

                        if (protectedStoreMultiIds.Count == 0)
                        {
                            await _db.Updateable<StoreMultiCodeProduct>()
                                .SetColumns(p => p.IsDeleted == true)
                                .Where(p => productCodes.Contains(p.ProductCode!))
                                .ExecuteCommandAsync();
                        }
                        else
                        {
                            await _db.Updateable<StoreMultiCodeProduct>()
                                .SetColumns(p => p.IsDeleted == true)
                                .Where(p =>
                                    productCodes.Contains(p.ProductCode!)
                                    && !protectedStoreMultiIds.Contains(p.UUID)
                                )
                                .ExecuteCommandAsync();
                        }

                        result.ProductsDeleted += productCodes.Count;
                        result.StoreRetailPricesDeleted += retailPricesDeleted;
                        result.ProductSetCodesDeleted += setCodesDeleted;
                        result.StoreMultiCodesDeleted += multiCodesDeleted;

                    processedPage++;
                    if (processedPage % 5 == 0)
                    {
                        await Task.Delay(500);
                        _logger.LogInformation(
                            "删除进度: {Processed}/{Total}",
                            processedPage * ProductHqSyncWriteBatchSize,
                            toDeleteCodes.Count
                        );
                    }
                }

                var affectedProductCodes = toAddCodes
                    .Concat(toUpdateCodes)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (affectedProductCodes.Count > 0)
                {
                    var recalculationActor = ResolveSetChildPurchasePriceActor(actorName);
                    var recalculation = await new SetChildPurchasePriceService(_db)
                        .RecalculateLockedAsync(
                            childCostLockScope,
                            Array.Empty<string>(),
                            null,
                            recalculationActor
                        );
                    EnsureSetChildPurchasePriceRecalculated(
                        recalculation,
                        affectedProductCodes
                    );
                }

                _db.Ado.CommitTran();
                transactionStarted = false;

                result.Errors = errors;
                result.DurationMs = (long)(DateTime.Now - startTime).TotalMilliseconds;

                _logger.LogInformation(
                    "同步完成！新增: {Added}, 更新: {Updated}, 删除: {Deleted}, 零售价软删: {RetailDeleted}, 套装软删: {SetDeleted}, 多码软删: {MultiDeleted}, 耗时: {Duration}ms",
                    result.ProductsAdded,
                    result.ProductsUpdated,
                    result.ProductsDeleted,
                    result.StoreRetailPricesDeleted,
                    result.ProductSetCodesDeleted,
                    result.StoreMultiCodesDeleted,
                    result.DurationMs
                );

                return ApiResponse<HqProductSyncResult>.OK(
                    result,
                    $"同步完成！新增: {result.ProductsAdded}, 更新: {result.ProductsUpdated}, 删除: {result.ProductsDeleted}"
                );
            }
            catch (Exception ex)
            {
                if (transactionStarted)
                {
                    try
                    {
                        _db.Ado.RollbackTran();
                    }
                    catch (Exception rollbackException)
                    {
                        _logger.LogError(rollbackException, "从HQ同步商品外层事务回滚失败");
                    }
                }

                // 任一批次或历史写入失败时，数据库已整体回滚，返回计数也必须归零。
                result.ProductsAdded = 0;
                result.ProductsUpdated = 0;
                result.ProductsDeleted = 0;
                result.StoreRetailPricesCreated = 0;
                result.StoreRetailPricesUpdated = 0;
                result.StoreRetailPricesDeleted = 0;
                result.ProductSetCodesCreated = 0;
                result.ProductSetCodesUpdated = 0;
                result.ProductSetCodesDeleted = 0;
                result.StoreMultiCodesCreated = 0;
                result.StoreMultiCodesUpdated = 0;
                result.StoreMultiCodesDeleted = 0;
                result.Errors = new List<string> { ex.Message };
                result.DurationMs = (long)(DateTime.Now - startTime).TotalMilliseconds;
                _logger.LogError(ex, "从HQ同步商品失败");
                return ApiResponse<HqProductSyncResult>.Error(
                    "从HQ同步商品失败: " + ex.Message,
                    "PRODUCT_HQ_SYNC_ERROR",
                    result
                );
            }
            finally
            {
                if (databaseLockAcquired)
                {
                    try
                    {
                        await ReleaseProductHqSyncDatabaseLockAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "释放商品HQ同步互斥锁失败");
                    }
                }

                if (shouldCloseDatabaseLockConnection)
                {
                    _db.Ado.Connection.Close();
                }

                _db.Ado.CommandTimeOut = originalTimeout;
                ProductHqSyncSemaphore.Release();
            }
        }

        private string ResolveSetChildPurchasePriceActor(string? actorName)
        {
            var resolved = string.IsNullOrWhiteSpace(actorName)
                ? _currentUserService.GetCurrentUsername()
                : actorName.Trim();
            return string.IsNullOrWhiteSpace(resolved) ? "System" : resolved;
        }

        private static void EnsureSetChildPurchasePriceRecalculated(
            SetChildPurchasePriceWritebackResultDto recalculation,
            IEnumerable<string> productCodes
        )
        {
            if (
                recalculation.ProductSetCode.SkippedGroupCount == 0
                && recalculation.StoreMultiCodeProduct.SkippedGroupCount == 0
            )
            {
                return;
            }

            var affectedCodes = string.Join(
                ", ",
                productCodes.Distinct(StringComparer.OrdinalIgnoreCase)
            );
            var reasons = string.Join(
                "；",
                recalculation.Errors.Select(error =>
                    $"{error.TableName}/{error.StoreCode ?? "总部"}/{error.ProductCode}: {error.Reason}"
                )
            );
            throw new InvalidOperationException(
                $"HQ 同步后的套装子项成本无法完整重算，主商品: {affectedCodes}。{reasons}"
            );
        }

        private async Task<(bool Acquired, bool ShouldCloseConnection)> TryAcquireProductHqSyncDatabaseLockAsync()
        {
            if (_db.CurrentConnectionConfig.DbType != DbType.SqlServer)
            {
                return (true, false);
            }

            var shouldCloseConnection = _db.Ado.Connection.State != System.Data.ConnectionState.Open;
            if (shouldCloseConnection)
            {
                _db.Ado.Connection.Open();
            }

            // SQL Server 会话锁用于防止多实例部署时重复触发同一类 HQ 商品同步。
            try
            {
                var lockResult = await _db.Ado.SqlQuerySingleAsync<int>(
                    """
                    DECLARE @Result INT;
                    EXEC @Result = sys.sp_getapplock
                        @Resource = @Resource,
                        @LockMode = N'Exclusive',
                        @LockOwner = N'Session',
                        @LockTimeout = 0;
                    SELECT @Result;
                    """,
                    new SugarParameter("@Resource", ProductHqSyncLockResource)
                );

                if (lockResult < 0)
                {
                    if (shouldCloseConnection)
                    {
                        _db.Ado.Connection.Close();
                    }

                    return (false, false);
                }

                return (true, shouldCloseConnection);
            }
            catch
            {
                if (shouldCloseConnection)
                {
                    _db.Ado.Connection.Close();
                }

                throw;
            }
        }

        private async Task ReleaseProductHqSyncDatabaseLockAsync()
        {
            if (_db.CurrentConnectionConfig.DbType != DbType.SqlServer)
            {
                return;
            }

            await _db.Ado.ExecuteCommandAsync(
                """
                EXEC sys.sp_releaseapplock
                    @Resource = @Resource,
                    @LockOwner = N'Session';
                """,
                new SugarParameter("@Resource", ProductHqSyncLockResource)
            );
        }

        private async Task SyncTouchedProductAssociationsAsync(
            List<Product> touchedProducts,
            Dictionary<string, DIC_商品信息字典表> hqProductsByCode,
            List<Store> activeStores,
            DateTime now,
            HqProductSyncResult result
        )
        {
            var productCodes = touchedProducts
                .Select(p => p.ProductCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!)
                .Distinct()
                .ToList();

            if (productCodes.Count == 0)
            {
                return;
            }

            var activeStoreCodes = activeStores
                .Select(s => s.StoreCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!)
                .ToHashSet();

            var hqRetailPrices = await _hqContext.Db.Queryable<DIC_商品零售价表>()
                .Where(r =>
                    productCodes.Contains(r.H商品编码)
                    && r.H使用状态 == true
                    && !string.IsNullOrEmpty(r.H分店代码)
                )
                .ToListAsync();
            hqRetailPrices = hqRetailPrices
                .Where(r => activeStoreCodes.Contains(r.H分店代码))
                .ToList();

            var hqMultiCodes = await _hqContext.Db.Queryable<DIC_分店一品多码表>()
                .Where(m =>
                    productCodes.Contains(m.H商品编码!)
                    && m.H使用状态 == true
                    && !string.IsNullOrEmpty(m.H分店代码)
                )
                .ToListAsync();
            hqMultiCodes = hqMultiCodes
                .Where(m => m.H分店代码 != null && activeStoreCodes.Contains(m.H分店代码))
                .ToList();

            await UpsertStoreRetailPricesAsync(productCodes, hqRetailPrices, now, result);
            await UpsertStoreMultiCodesAsync(productCodes, hqMultiCodes, now, result);
            await UpsertProductSetCodesAsync(productCodes, hqMultiCodes, now, result);
        }

        private async Task BulkInsertAsync<T>(List<T> rows, int pageSize)
            where T : class, new()
        {
            if (rows.Count == 0)
            {
                return;
            }

            if (_db.CurrentConnectionConfig.DbType == DbType.SqlServer)
            {
                // HQ 同步写入量较大，SQL Server 使用 BulkCopy 避免生成超长 INSERT 语句导致超时。
                await _db.Fastest<T>().PageSize(pageSize).BulkCopyAsync(rows);
                return;
            }

            await _db.Insertable(rows).ExecuteCommandAsync();
        }

        private async Task BulkUpdateAsync<T>(List<T> rows, int pageSize)
            where T : class, new()
        {
            if (rows.Count == 0)
            {
                return;
            }

            if (_db.CurrentConnectionConfig.DbType == DbType.SqlServer)
            {
                // 关联表一次可能触达多分店数据，BulkUpdate 可以降低长事务内的单条 SQL 压力。
                await _db.Fastest<T>().PageSize(pageSize).BulkUpdateAsync(rows);
                return;
            }

            await _db.Updateable(rows).ExecuteCommandAsync();
        }

        private async Task UpsertStoreRetailPricesAsync(
            List<string> productCodes,
            List<DIC_商品零售价表> hqRetailPrices,
            DateTime now,
            HqProductSyncResult result
        )
        {
            var localRows = await _db.Queryable<StoreRetailPrice>()
                .Where(row => productCodes.Contains(row.ProductCode!))
                .ToListAsync();
            var byGuid = localRows
                .Where(row => !string.IsNullOrWhiteSpace(row.UUID))
                .GroupBy(row => row.UUID)
                .ToDictionary(group => group.Key, group => group.First());
            var byBusinessKey = localRows
                .Where(row =>
                    !string.IsNullOrWhiteSpace(row.StoreCode)
                    && !string.IsNullOrWhiteSpace(row.ProductCode)
                )
                .GroupBy(row => BuildKey(row.StoreCode, row.ProductCode))
                .ToDictionary(group => group.Key, group => group.First());

            var insertRows = new List<StoreRetailPrice>();
            var updateRows = new List<StoreRetailPrice>();
            var touchedIds = new HashSet<string>();

            foreach (var hqRow in hqRetailPrices)
            {
                var businessKey = BuildKey(hqRow.H分店代码, hqRow.H商品编码);
                var localRow = FindByGuidOrBusinessKey(hqRow.HGUID, byGuid, byBusinessKey, businessKey);

                if (localRow == null)
                {
                    localRow = new StoreRetailPrice
                    {
                        UUID = NormalizeId(hqRow.HGUID) ?? UuidHelper.GenerateUuid7(),
                        CreatedAt = now,
                    };
                    insertRows.Add(localRow);
                }
                else
                {
                    updateRows.Add(localRow);
                }

                await NormalizeStoreRetailPriceIdAsync(localRow, hqRow.HGUID, byGuid);
                localRow.StoreCode = hqRow.H分店代码;
                localRow.ProductCode = hqRow.H商品编码;
                localRow.StoreProductCode = hqRow.H分店商品编码;
                localRow.SupplierCode = hqRow.H供应商编码;
                localRow.PurchasePrice = hqRow.H进货价;
                localRow.StoreRetailPriceValue = hqRow.H分店零售价;
                localRow.DiscountRate = hqRow.H折扣率;
                localRow.IsActive = hqRow.H使用状态;
                localRow.IsAutoPricing = hqRow.H是否自动定价;
                localRow.IsSpecialProduct = hqRow.H是否特殊商品;
                localRow.IsDeleted = false;
                localRow.UpdatedAt = now;
                touchedIds.Add(localRow.UUID);
            }

            var missingRows = localRows
                .Where(row => !row.IsDeleted && !touchedIds.Contains(row.UUID))
                .ToList();
            foreach (var row in missingRows)
            {
                row.IsDeleted = true;
                row.UpdatedAt = now;
                updateRows.Add(row);
            }

            if (insertRows.Count > 0)
            {
                await BulkInsertAsync(insertRows, ProductHqSyncWriteBatchSize);
                result.StoreRetailPricesCreated += insertRows.Count;
            }
            if (updateRows.Count > 0)
            {
                await BulkUpdateAsync(updateRows, ProductHqSyncWriteBatchSize);
                result.StoreRetailPricesDeleted += missingRows.Count;
            }
        }

        private async Task UpsertStoreMultiCodesAsync(
            List<string> productCodes,
            List<DIC_分店一品多码表> hqMultiCodes,
            DateTime now,
            HqProductSyncResult result
        )
        {
            var localRows = await _db.Queryable<StoreMultiCodeProduct>()
                .Where(row => productCodes.Contains(row.ProductCode!))
                .ToListAsync();
            var protectedSetKeys = (await _db.Queryable<ProductSetCode>()
                    .Where(row =>
                        row.SetType == 1
                        && productCodes.Contains(row.ProductCode)
                    )
                    .ToListAsync())
                .Where(row => !string.IsNullOrWhiteSpace(row.SetProductCode))
                .Select(row => BuildKey(row.ProductCode, row.SetProductCode))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var byGuid = localRows
                .Where(row => !string.IsNullOrWhiteSpace(row.UUID))
                .GroupBy(row => row.UUID)
                .ToDictionary(group => group.Key, group => group.First());
            var byBusinessKey = localRows
                .Where(row =>
                    !string.IsNullOrWhiteSpace(row.StoreCode)
                    && !string.IsNullOrWhiteSpace(row.ProductCode)
                    && !string.IsNullOrWhiteSpace(row.MultiBarcode)
                )
                .GroupBy(row => BuildKey(row.StoreCode, row.ProductCode, row.MultiBarcode))
                .ToDictionary(group => group.Key, group => group.First());
            var deletedFallbackByProductStore = localRows
                .Where(row =>
                    row.IsDeleted
                    && !string.IsNullOrWhiteSpace(row.StoreCode)
                    && !string.IsNullOrWhiteSpace(row.ProductCode)
                )
                .GroupBy(row => BuildKey(row.StoreCode, row.ProductCode))
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.First());

            var insertRows = new List<StoreMultiCodeProduct>();
            var updateRows = new List<StoreMultiCodeProduct>();
            var touchedIds = new HashSet<string>();

            foreach (var hqRow in hqMultiCodes)
            {
                var productCode = hqRow.H商品编码 ?? string.Empty;
                var multiCodeProductCode = ResolveMultiCodeProductCode(hqRow, productCode);
                if (protectedSetKeys.Contains(BuildKey(productCode, multiCodeProductCode)))
                {
                    // 旧 HQ 同步只能维护普通多码，不能覆盖套装子项的门店派生成本。
                    continue;
                }
                var businessKey = BuildKey(hqRow.H分店代码, hqRow.H商品编码, hqRow.H多条形码);
                var localRow = FindByGuidOrBusinessKey(hqRow.HGUID, byGuid, byBusinessKey, businessKey);
                if (
                    localRow == null
                    && deletedFallbackByProductStore.TryGetValue(
                        BuildKey(hqRow.H分店代码, productCode),
                        out var deletedFallback
                    )
                )
                {
                    // 软删恢复场景下 HQ 可能同时修正条码，兜底复用唯一旧行避免重复插入。
                    localRow = deletedFallback;
                }

                if (localRow == null)
                {
                    localRow = new StoreMultiCodeProduct
                    {
                        UUID = NormalizeId(hqRow.HGUID) ?? UuidHelper.GenerateUuid7(),
                        CreatedAt = now,
                    };
                    insertRows.Add(localRow);
                }
                else
                {
                    updateRows.Add(localRow);
                }

                await NormalizeStoreMultiCodeIdAsync(localRow, hqRow.HGUID, byGuid);
                localRow.StoreCode = hqRow.H分店代码;
                localRow.ProductCode = productCode;
                localRow.MultiCodeProductCode = multiCodeProductCode;
                localRow.StoreMultiCodeProductCode = hqRow.H分店多码商品编码;
                localRow.MultiBarcode = hqRow.H多条形码;
                // HQ 普通多码属于 Type2 关系时，外部成本不是最终值；保留为空以便推送回退到已校正主成本。
                localRow.PurchasePrice = null;
                localRow.MultiCodeRetailPrice = hqRow.H一品多码零售价;
                localRow.DiscountRate = hqRow.H折扣率;
                localRow.IsActive = hqRow.H使用状态 ?? true;
                localRow.IsAutoPricing = hqRow.H是否自动定价 ?? false;
                localRow.IsSpecialProduct = hqRow.H是否特殊商品 ?? false;
                localRow.IsDeleted = false;
                localRow.UpdatedAt = now;
                touchedIds.Add(localRow.UUID);
            }

            var missingRows = localRows
                .Where(row =>
                    !row.IsDeleted
                    && !touchedIds.Contains(row.UUID)
                    && !protectedSetKeys.Contains(
                        BuildKey(row.ProductCode, row.MultiCodeProductCode)
                    )
                )
                .ToList();
            foreach (var row in missingRows)
            {
                row.IsDeleted = true;
                row.UpdatedAt = now;
                updateRows.Add(row);
            }

            if (insertRows.Count > 0)
            {
                await BulkInsertAsync(insertRows, ProductHqSyncWriteBatchSize);
                result.StoreMultiCodesCreated += insertRows.Count;
            }
            if (updateRows.Count > 0)
            {
                await BulkUpdateAsync(updateRows, ProductHqSyncWriteBatchSize);
                result.StoreMultiCodesDeleted += missingRows.Count;
            }
        }

        private async Task UpsertProductSetCodesAsync(
            List<string> productCodes,
            List<DIC_分店一品多码表> hqMultiCodes,
            DateTime now,
            HqProductSyncResult result
        )
        {
            var sourcePreflight = ProductSetCodeIdentityResolver.PreflightSource(
                hqMultiCodes,
                row => row.HGUID,
                row => row.H商品编码,
                row => !string.IsNullOrWhiteSpace(row.H多码商品编码)
                    ? row.H多码商品编码
                    : row.H商品编码,
                row => row.FGC_LastModifyDate,
                row => row.ID
            );
            foreach (var conflict in sourcePreflight.Conflicts)
            {
                var message = conflict.ToErrorMessage();
                if (!result.Errors.Contains(message))
                {
                    result.Errors.Add(message);
                }
            }
            hqMultiCodes = sourcePreflight.Rows.ToList();

            // 旧全量入口持有全局业务锁；GUID 仍可能命中本批商品范围外的旧父关系，
            // 双身份解析必须覆盖全表，缺失清理则继续严格限制在本批商品范围内。
            var identityRows = await _db.Queryable<ProductSetCode>().ToListAsync();
            var productCodeSet = productCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var localRows = identityRows
                .Where(row => productCodeSet.Contains(row.ProductCode))
                .ToList();
            var identityIndex = ProductSetCodeIdentityResolver.CreateIndex(identityRows);
            var byGuid = identityRows
                .Select(row => new
                {
                    Key = ProductSetCodeIdentityResolver.Normalize(row.SetCodeId),
                    Row = row,
                })
                .Where(item => item.Key != null)
                .GroupBy(item => item.Key!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().Row,
                    StringComparer.OrdinalIgnoreCase
                );
            var deletedFallbackByProduct = localRows
                .Where(row =>
                    row.IsDeleted
                    && row.SetType != 1
                    && !string.IsNullOrWhiteSpace(row.ProductCode)
                )
                .GroupBy(
                    row => ProductSetCodeIdentityResolver.Normalize(row.ProductCode)!,
                    StringComparer.OrdinalIgnoreCase
                )
                .Where(group => group.Count() == 1)
                .ToDictionary(
                    group => group.Key,
                    group => group.First(),
                    StringComparer.OrdinalIgnoreCase
                );
            var preservedIds = identityRows
                .Where(row =>
                {
                    var identity = ProductSetCodeIdentityResolver.CreateIdentity(row);
                    return (identity.Guid != null
                            && sourcePreflight.ConflictingGuids.Contains(identity.Guid))
                        || (identity.BusinessKey != null
                            && sourcePreflight.ConflictingBusinessKeys.Contains(
                                identity.BusinessKey
                            ));
                })
                .Select(row => row.SetCodeId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var insertRows = new List<ProductSetCode>();
            var updateRows = new List<ProductSetCode>();
            var pendingInsertRows = new HashSet<ProductSetCode>(ReferenceEqualityComparer.Instance);
            var queuedUpdateRows = new HashSet<ProductSetCode>(ReferenceEqualityComparer.Instance);
            var touchedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var hqRow in hqMultiCodes)
            {
                var productCode = ProductSetCodeIdentityResolver.Normalize(hqRow.H商品编码)
                    ?? string.Empty;
                var setProductCode = ProductSetCodeIdentityResolver.Normalize(
                        ResolveMultiCodeProductCode(hqRow, productCode)
                    )
                    ?? productCode;
                var hqGuid = ProductSetCodeIdentityResolver.Normalize(hqRow.HGUID);
                var resolution = identityIndex.Resolve(hqGuid, productCode, setProductCode);
                if (resolution.Kind == ProductSetCodeIdentityMatchKind.Conflict)
                {
                    // 先保留 GUID 与父子键命中的双方；不得进入 Type1、唯一软删兜底或 Last-wins。
                    var conflictMessage =
                        "旧 ProductSetCode Upsert 身份冲突，已保留原记录："
                        + $"GUID={resolution.Guid ?? "(空)"}，"
                        + $"业务键={(resolution.BusinessKey == null ? "(空)" : ProductSetCodeIdentityResolver.FormatBusinessKey(resolution.BusinessKey))}，"
                        + $"本地记录={ProductSetCodeIdentityResolver.FormatLocalRecords(resolution.AllMatches)}";
                    if (!result.Errors.Contains(conflictMessage))
                    {
                        result.Errors.Add(conflictMessage);
                    }
                    foreach (var matchedRow in resolution.AllMatches)
                    {
                        preservedIds.Add(matchedRow.SetCodeId);
                    }
                    continue;
                }

                var localRow = resolution.MatchedRow;
                if (localRow?.SetType == 1)
                {
                    // 只有确认不存在交叉身份后，才允许 Type1 人工套装保护短路。
                    continue;
                }
                if (
                    localRow == null
                    && resolution.Kind == ProductSetCodeIdentityMatchKind.None
                    && deletedFallbackByProduct.TryGetValue(productCode, out var deletedFallback)
                    && !preservedIds.Contains(deletedFallback.SetCodeId)
                )
                {
                    // None 同时证明目标 GUID/业务键未占用，此时才允许唯一 Type2 旧行安全迁移并复活。
                    localRow = deletedFallback;
                    deletedFallbackByProduct.Remove(productCode);
                }

                var isNew = localRow == null;
                if (localRow == null)
                {
                    localRow = new ProductSetCode
                    {
                        SetCodeId = hqGuid ?? UuidHelper.GenerateUuid7(),
                        CreatedAt = now,
                    };
                }

                var previousIdentity = ProductSetCodeIdentityResolver.CreateIdentity(localRow);
                if (!isNew)
                {
                    await NormalizeProductSetCodeIdAsync(localRow, hqGuid, byGuid);
                }
                localRow.ProductCode = productCode;
                localRow.SetProductCode = setProductCode;
                localRow.SetItemNumber = setProductCode;
                localRow.SetBarcode = hqRow.H多条形码;
                // HQ Type2 的成本只是外部输入，不是本地最终成本；关系完整后由统一成本链路决定。
                localRow.SetPurchasePrice = null;
                localRow.SetRetailPrice = hqRow.H一品多码零售价;
                localRow.SetType = 2;
                localRow.SetQuantity = localRow.SetQuantity <= 0 ? 1 : localRow.SetQuantity;
                localRow.IsActive = hqRow.H使用状态 ?? true;
                localRow.IsDeleted = false;
                localRow.UpdatedAt = now;
                touchedIds.Add(localRow.SetCodeId);

                if (isNew)
                {
                    insertRows.Add(localRow);
                    pendingInsertRows.Add(localRow);
                    identityIndex.Add(localRow);
                    var insertedGuid = ProductSetCodeIdentityResolver.Normalize(localRow.SetCodeId);
                    if (insertedGuid != null)
                    {
                        byGuid[insertedGuid] = localRow;
                    }
                }
                else
                {
                    identityIndex.Reindex(localRow, previousIdentity);
                    if (
                        !pendingInsertRows.Contains(localRow)
                        && queuedUpdateRows.Add(localRow)
                    )
                    {
                        updateRows.Add(localRow);
                    }
                }
            }

            var missingRows = localRows
                .Where(row =>
                    !row.IsDeleted
                    && !touchedIds.Contains(row.SetCodeId)
                    && !preservedIds.Contains(row.SetCodeId)
                    && row.SetType != 1
                )
                .ToList();
            foreach (var row in missingRows)
            {
                row.IsDeleted = true;
                row.UpdatedAt = now;
                updateRows.Add(row);
            }

            if (insertRows.Count > 0)
            {
                await BulkInsertAsync(insertRows, ProductHqSyncWriteBatchSize);
                result.ProductSetCodesCreated += insertRows.Count;
            }
            if (updateRows.Count > 0)
            {
                await BulkUpdateAsync(updateRows, ProductHqSyncWriteBatchSize);
                result.ProductSetCodesDeleted += missingRows.Count;
            }
        }

        private static T? FindByGuidOrBusinessKey<T>(
            string? hguid,
            Dictionary<string, T> byGuid,
            Dictionary<string, T> byBusinessKey,
            string businessKey
        )
            where T : class
        {
            var normalizedGuid = NormalizeId(hguid);
            if (normalizedGuid != null && byGuid.TryGetValue(normalizedGuid, out var guidMatch))
            {
                return guidMatch;
            }

            return byBusinessKey.TryGetValue(businessKey, out var businessMatch)
                ? businessMatch
                : null;
        }

        private async Task NormalizeStoreRetailPriceIdAsync(
            StoreRetailPrice localRow,
            string? hguid,
            Dictionary<string, StoreRetailPrice> byGuid
        )
        {
            var normalizedGuid = NormalizeId(hguid);
            if (
                normalizedGuid == null
                || localRow.UUID == normalizedGuid
                || byGuid.ContainsKey(normalizedGuid)
            )
            {
                return;
            }

            var oldId = localRow.UUID;
            await _db.Ado.ExecuteCommandAsync(
                "UPDATE [StoreRetailPrice] SET [UUID] = @NewId WHERE [UUID] = @OldId",
                new SugarParameter("@NewId", normalizedGuid),
                new SugarParameter("@OldId", oldId)
            );
            localRow.UUID = normalizedGuid;
            byGuid[normalizedGuid] = localRow;
        }

        private async Task NormalizeStoreMultiCodeIdAsync(
            StoreMultiCodeProduct localRow,
            string? hguid,
            Dictionary<string, StoreMultiCodeProduct> byGuid
        )
        {
            var normalizedGuid = NormalizeId(hguid);
            if (
                normalizedGuid == null
                || localRow.UUID == normalizedGuid
                || byGuid.ContainsKey(normalizedGuid)
            )
            {
                return;
            }

            var oldId = localRow.UUID;
            await _db.Ado.ExecuteCommandAsync(
                "UPDATE [StoreMultiCodeProduct] SET [UUID] = @NewId WHERE [UUID] = @OldId",
                new SugarParameter("@NewId", normalizedGuid),
                new SugarParameter("@OldId", oldId)
            );
            localRow.UUID = normalizedGuid;
            byGuid[normalizedGuid] = localRow;
        }

        private async Task NormalizeProductSetCodeIdAsync(
            ProductSetCode localRow,
            string? hguid,
            Dictionary<string, ProductSetCode> byGuid
        )
        {
            var normalizedGuid = NormalizeId(hguid);
            if (
                normalizedGuid == null
                || localRow.SetCodeId == normalizedGuid
                || byGuid.ContainsKey(normalizedGuid)
            )
            {
                return;
            }

            var oldId = localRow.SetCodeId;
            await _db.Ado.ExecuteCommandAsync(
                "UPDATE [ProductSetCode] SET [SetCodeId] = @NewId WHERE [SetCodeId] = @OldId",
                new SugarParameter("@NewId", normalizedGuid),
                new SugarParameter("@OldId", oldId)
            );
            byGuid.Remove(oldId);
            localRow.SetCodeId = normalizedGuid;
            byGuid[normalizedGuid] = localRow;
        }

        private static string ResolveMultiCodeProductCode(
            DIC_分店一品多码表 hqRow,
            string fallbackProductCode
        )
        {
            return !string.IsNullOrWhiteSpace(hqRow.H多码商品编码)
                ? hqRow.H多码商品编码!
                : fallbackProductCode;
        }

        private static string BuildKey(params string?[] parts)
        {
            return string.Join("\u001F", parts.Select(part => part ?? string.Empty));
        }

        private static string? NormalizeId(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static string? Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            return value.Length <= maxLength ? value : value[..maxLength];
        }

        #endregion
    }
}
