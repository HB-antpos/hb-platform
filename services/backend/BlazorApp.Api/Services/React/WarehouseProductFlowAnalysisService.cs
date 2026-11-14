using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SqlSugar;
using System.Diagnostics;
using System.Text.Json;

namespace BlazorApp.Api.Services.React
{
    /// <summary>仓库商品流转分析参数错误。控制器捕获后返回 400。</summary>
    public sealed class WarehouseProductFlowAnalysisValidationException : Exception
    {
        public WarehouseProductFlowAnalysisValidationException(string message)
            : base(message)
        {
        }
    }

    internal sealed class WarehouseProductFlowAnalysisSalesRow
    {
        public string ProductCode { get; set; } = string.Empty;
        public string BranchCode { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public int TotalQuantity { get; set; }
        public decimal TotalAmount { get; set; }
    }

    internal sealed class WarehouseProductFlowAnalysisOrderRow
    {
        public string ProductCode { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
        public string OrderNo { get; set; } = string.Empty;
        public DateTime? OrderDate { get; set; }
        public decimal Quantity { get; set; }
    }

    internal sealed class WarehouseProductFlowAnalysisShipmentRow
    {
        public string ProductCode { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
        public string OrderNo { get; set; } = string.Empty;
        public DateTime? OutboundDate { get; set; }
        public decimal AllocQuantity { get; set; }
    }

    internal sealed class WarehouseProductFlowAnalysisStoreMetadata
    {
        public string BranchName { get; set; } = string.Empty;
        public bool PosEnabled { get; set; }
    }

    internal sealed class WarehouseProductFlowAnalysisContainerRow
    {
        public string ProductCode { get; set; } = string.Empty;
        public string ContainerNumber { get; set; } = string.Empty;
        public DateTime? ArrivalDate { get; set; }
        public decimal LoadingQuantity { get; set; }
        public decimal? InboundUnitPrice { get; set; }
    }

    internal sealed class WarehouseProductFlowAnalysisDailyAggregateRow
    {
        public DateTime Date { get; set; }
        public decimal InboundQuantity { get; set; }
        public decimal OrderedQuantity { get; set; }
        public decimal ShippedQuantity { get; set; }
        public int NetSalesQuantity { get; set; }
        public decimal NetSalesAmount { get; set; }
    }

    internal sealed class WarehouseProductFlowAnalysisPeriodBounds
    {
        public DateTime ContainerStart { get; set; }
        public DateTime ContainerEnd { get; set; }
        public DateTime OrderShipmentStart { get; set; }
        public DateTime OrderShipmentEnd { get; set; }
        public DateTime SalesStart { get; set; }
        public DateTime SalesEnd { get; set; }
    }

    internal sealed class WarehouseProductFlowAnalysisProductInfo
    {
        public string ProductCode { get; set; } = string.Empty;
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
        public string? ProductName { get; set; }
        public string? EnglishName { get; set; }
        public string? ImageUrl { get; set; }
        public string? CategoryGuid { get; set; }
    }

    internal sealed class WarehouseProductFlowAnalysisDomesticInfo
    {
        public string ProductCode { get; set; } = string.Empty;
        public string? SupplierCode { get; set; }
    }

    internal sealed class WarehouseProductFlowAnalysisCategoryHierarchyRow
    {
        public string CategoryGUID { get; set; } = string.Empty;
        public string? CategoryName { get; set; }
        public string? ParentGUID { get; set; }
    }

    internal sealed class WarehouseProductFlowAnalysisQueryContext
    {
        public List<string> AllCodes { get; set; } = new();
        public List<string> FilteredCodes { get; set; } = new();
        public Dictionary<string, WarehouseProductFlowAnalysisProductInfo> ProductByCode { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, WarehouseProductFlowAnalysisDomesticInfo> DomesticByCode { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ChinaNameByCode { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> CategoryNameByGuid { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    // 移动端仓库商品进销查询的单商品聚合实现在 WarehouseProductFlowAnalysisService.ProductInsight.cs，
    // 拆分文件后可直接复用此处的行查询、门店名映射与缓存，避免两套 SQL 造成口径漂移。
    public partial class WarehouseProductFlowAnalysisService : IWarehouseProductFlowAnalysisService
    {
        private readonly SqlSugarContext _context;
        private readonly IMemoryCache _cache;
        private readonly ILogger<WarehouseProductFlowAnalysisService> _logger;

        private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);
        private const int CodeBatchSize = 500;
        private const string CategoryHierarchyCacheKey = "wfpa:category-hierarchy:v1";
        private const string ActiveProductCodeDuplicatesCacheKey =
            "wfpa:active-product-code-duplicates:v1";

        public WarehouseProductFlowAnalysisService(
            SqlSugarContext context,
            IMemoryCache cache,
            ILogger<WarehouseProductFlowAnalysisService> logger
        )
        {
            _context = context;
            _cache = cache;
            _logger = logger;
        }

        public async Task<ApiResponse<WarehouseProductFlowAnalysisOptionsDto>> GetOptionsAsync(
            WarehouseProductFlowAnalysisFilterDto filter,
            List<string>? branchCodes,
            bool forceRefresh = false
        )
        {
            if (forceRefresh)
                _cache.Remove(CategoryHierarchyCacheKey);
            var cacheKey = BuildOptionsCacheKey(filter, branchCodes);
            return await GetOrCreateAsync(cacheKey, forceRefresh, () => QueryOptionsAsync());
        }

        public async Task<ApiResponse<WarehouseProductFlowCandidatePageDto>> GetCandidatesAsync(
            WarehouseProductFlowCandidateRequest request
        )
        {
            var requestedItemNumbers = NormalizeItemNumbers(request.Filter?.ItemNumbers);
            if (requestedItemNumbers.Count > MaxItemNumberFilterCount)
            {
                throw new WarehouseProductFlowAnalysisValidationException(
                    $"批量货号最多 {MaxItemNumberFilterCount} 个，请分批查询"
                );
            }
            var cacheKey = BuildCandidateCacheKey(request);
            return await GetOrCreateAsync(cacheKey, request.ForceRefresh, async () =>
            {
                if (request.ForceRefresh)
                {
                    _cache.Remove(ActiveProductCodeDuplicatesCacheKey);
                    if (request.Filter?.WarehouseCategoryGuids?.Count > 0)
                        _cache.Remove(CategoryHierarchyCacheKey);
                }
                var totalStopwatch = Stopwatch.StartNew();
                var filterStopwatch = Stopwatch.StartNew();
                var filterMilliseconds = 0L;
                var countMilliseconds = 0L;
                var pageMilliseconds = 0L;
                var hydrateMilliseconds = 0L;
                var currentStage = "filter";
                var filter = request.Filter ?? new WarehouseProductFlowAnalysisFilterDto();
                var pageNumber = request.PageNumber <= 0 ? 1 : request.PageNumber;
                var pageSize = request.PageSize <= 0 ? 50 : Math.Min(request.PageSize, 500);
                var requestedCategoryGuids = NormalizeCodes(filter.WarehouseCategoryGuids);
                var requestedSupplierCodes = NormalizeCodes(filter.SupplierCodes);
                var keyword = filter.Keyword?.Trim();
                var documentKeyword = filter.DocumentKeyword?.Trim();
                var hasActiveProductCodeDuplicates = false;
                WarehouseProductFlowItemNumberResolution? itemNumberResolution = null;

                try
                {
                    var warehouseProductQuery = _context
                        .Db.Queryable<WarehouseProduct>()
                        .Where(w => !w.IsDeleted);
                    hasActiveProductCodeDuplicates =
                        await HasActiveProductCodeDuplicatesAsync();

                    if (requestedItemNumbers.Count > 0)
                    {
                        // 批量货号先解析成商品编码，再收敛仓库主档；一个都没命中时直接返回空页和未找到清单。
                        itemNumberResolution = await ResolveItemNumberFilterAsync(
                            requestedItemNumbers,
                            IsAnyItemNumberMatchMode(filter.ItemNumberMatchMode),
                            hasActiveProductCodeDuplicates
                        );
                        if (itemNumberResolution.RankByCode.Count == 0)
                        {
                            return new WarehouseProductFlowCandidatePageDto
                            {
                                Total = 0,
                                PageNumber = pageNumber,
                                PageSize = pageSize,
                                ItemNumberMatch = itemNumberResolution.BuildMatch(Array.Empty<string>()),
                            };
                        }
                        var itemNumberCodes = itemNumberResolution.RankByCode.Keys.ToList();
                        warehouseProductQuery = warehouseProductQuery.Where(warehouseProduct =>
                            itemNumberCodes.Contains(warehouseProduct.ProductCode)
                        );
                    }

                    if (!string.IsNullOrWhiteSpace(documentKeyword))
                    {
                        // 货柜编号只负责收敛候选集；保持历史有效货柜语义且不引入业务日期范围。
                        var documentCandidates = _context
                            .Db.Queryable<ContainerDetail>()
                            .InnerJoin<Container>((detail, container) =>
                                detail.ContainerCode == container.ContainerCode
                            )
                            .Where((detail, container) =>
                                !detail.IsDeleted
                                && !container.IsDeleted
                                && detail.ProductCode != null
                                && (detail.Status == null || detail.Status != 6)
                                && container.ContainerNumber != null
                                && container.ContainerNumber.Contains(documentKeyword)
                                && (container.Status == null || container.Status != 7)
                            )
                            .Select((detail, container) => new { ProductCode = detail.ProductCode! })
                            .Distinct();

                        warehouseProductQuery = warehouseProductQuery
                            .InnerJoin(
                                documentCandidates,
                                (warehouseProduct, candidate) =>
                                    warehouseProduct.ProductCode == candidate.ProductCode
                            )
                            .Select((warehouseProduct, candidate) => warehouseProduct)
                            .MergeTable();
                    }

                    if (!string.IsNullOrWhiteSpace(keyword))
                    {
                        // 先形成匹配商品集合再关联仓库主档，避免 OR + 关联子查询逐行扫描 Product。
                        // 使用相同的确定性主档规则，去重后也不会放大 count 或分页行数。
                        var keywordProductCodes = BuildCandidateProductQuery(hasActiveProductCodeDuplicates)
                            .Where(product =>
                                product.ProductCode!.Contains(keyword)
                                || (product.ItemNumber != null && product.ItemNumber.Contains(keyword))
                                || (product.ProductName != null && product.ProductName.Contains(keyword))
                                || (product.EnglishName != null && product.EnglishName.Contains(keyword))
                                || (product.Barcode != null && product.Barcode.Contains(keyword))
                            )
                            .Select(product => new { ProductCode = product.ProductCode! })
                            .Distinct()
                            .MergeTable();
                        // 常见的货号/条码只命中少量商品：复用匹配编码，避免 count 和分页各扫描一次主档。
                        // 最多预读一个批次；宽泛关键字继续使用完整子查询，不截断结果或生成超长 IN。
                        var matchedCodes = await keywordProductCodes.Clone()
                            .Select(product => product.ProductCode)
                            .Take(CodeBatchSize + 1)
                            .ToListAsync();
                        if (matchedCodes.Count <= CodeBatchSize)
                        {
                            warehouseProductQuery = matchedCodes.Count == 0
                                ? warehouseProductQuery.Where(warehouseProduct =>
                                    warehouseProduct.ProductCode.Contains(keyword))
                                : warehouseProductQuery.Where(warehouseProduct =>
                                    warehouseProduct.ProductCode.Contains(keyword)
                                    || matchedCodes.Contains(warehouseProduct.ProductCode));
                        }
                        else
                        {
                            warehouseProductQuery = warehouseProductQuery
                                .LeftJoin(
                                    keywordProductCodes,
                                    (warehouseProduct, product) =>
                                        warehouseProduct.ProductCode == product.ProductCode
                                )
                                .Where((warehouseProduct, product) =>
                                    warehouseProduct.ProductCode.Contains(keyword)
                                    || product.ProductCode != null
                                )
                                .Select((warehouseProduct, product) => warehouseProduct)
                                .MergeTable();
                        }
                    }

                    if (requestedSupplierCodes.Count > 0)
                    {
                        warehouseProductQuery = warehouseProductQuery.Where(warehouseProduct =>
                            SqlFunc.Subqueryable<DomesticProduct>()
                                .Where(domesticProduct =>
                                    !domesticProduct.IsDeleted
                                    && domesticProduct.ProductCode == warehouseProduct.ProductCode
                                    && domesticProduct.SupplierCode != null
                                    && (requestedSupplierCodes.Contains(domesticProduct.SupplierCode)
                                        || requestedSupplierCodes.Contains(
                                            domesticProduct.SupplierCode.Trim()
                                        ))
                                )
                                .Any()
                        );
                    }

                    if (requestedCategoryGuids.Count > 0)
                    {
                        // 分类树属于主档元数据，不随候选 forceRefresh 重复读取整表。
                        var categories = await GetCategoryHierarchyRowsAsync();
                        var expandedCategoryGuids = ExpandCategoryGuids(
                                categories,
                                requestedCategoryGuids
                            )
                            .ToList();
                        if (hasActiveProductCodeDuplicates)
                        {
                            var categoryProductCodes = BuildCanonicalProductQuery()
                                .Where(product =>
                                    product.WarehouseCategoryGUID != null
                                    && expandedCategoryGuids.Contains(
                                        product.WarehouseCategoryGUID
                                    )
                                )
                                .Select(product => new { ProductCode = product.ProductCode! })
                                .MergeTable();
                            warehouseProductQuery = warehouseProductQuery
                                .InnerJoin(
                                    categoryProductCodes,
                                    (warehouseProduct, product) =>
                                        warehouseProduct.ProductCode == product.ProductCode
                                )
                                .Select((warehouseProduct, product) => warehouseProduct)
                                .MergeTable();
                        }
                        else
                        {
                            warehouseProductQuery = warehouseProductQuery.Where(warehouseProduct =>
                                SqlFunc.Subqueryable<Product>()
                                    .Where(product =>
                                        !product.IsDeleted
                                        && product.ProductCode == warehouseProduct.ProductCode
                                        && product.WarehouseCategoryGUID != null
                                        && expandedCategoryGuids.Contains(
                                            product.WarehouseCategoryGUID
                                        )
                                    )
                                    .Any()
                            );
                        }
                    }

                    filterStopwatch.Stop();
                    filterMilliseconds = filterStopwatch.ElapsedMilliseconds;

                    // count 只统计已过滤的仓库主档；不为计数承担 Product canonical 聚合和排序。
                    currentStage = "count";
                    var countStopwatch = Stopwatch.StartNew();
                    int total;
                    List<string>? itemNumberPageCodes = null;
                    WarehouseProductFlowItemNumberMatchDto? itemNumberMatch = null;
                    if (itemNumberResolution != null)
                    {
                        // 批量货号的结果集有上限（受粘贴个数约束），在内存中按粘贴顺序分页，
                        // 并据最终筛选结果统计已匹配 / 未找到的货号。
                        var resolution = itemNumberResolution;
                        var filteredCodes = NormalizeCodes(
                            await warehouseProductQuery.Clone()
                                .Select(warehouseProduct => warehouseProduct.ProductCode)
                                .ToListAsync()
                        );
                        total = filteredCodes.Count;
                        itemNumberMatch = resolution.BuildMatch(filteredCodes);
                        itemNumberPageCodes = filteredCodes
                            .OrderBy(code => resolution.RankByCode.TryGetValue(code, out var rank) ? rank : int.MaxValue)
                            .ThenBy(code => code, StringComparer.OrdinalIgnoreCase)
                            .Skip((pageNumber - 1) * pageSize)
                            .Take(pageSize)
                            .ToList();
                    }
                    else
                    {
                        total = await warehouseProductQuery.Clone().CountAsync();
                    }
                    countStopwatch.Stop();
                    countMilliseconds = countStopwatch.ElapsedMilliseconds;

                    var candidateProducts = BuildCandidateProductQuery(
                        hasActiveProductCodeDuplicates
                    );
                    var query = warehouseProductQuery
                        .LeftJoin(candidateProducts, (warehouseProduct, product) =>
                            warehouseProduct.ProductCode == product.ProductCode
                        )
                        .LeftJoin<DomesticProduct>((warehouseProduct, product, domesticProduct) =>
                            warehouseProduct.ProductCode == domesticProduct.ProductCode
                            && !domesticProduct.IsDeleted
                        );

                    var descending = string.Equals(
                        request.SortDirection,
                        "desc",
                        StringComparison.OrdinalIgnoreCase
                    );
                    var sortField = string.IsNullOrWhiteSpace(request.SortBy)
                        ? "itemnumber"
                        : request.SortBy.Trim().ToLowerInvariant();

                    if (sortField == "productcode")
                    {
                        query = query.OrderBy(
                            (warehouseProduct, product, domesticProduct) => warehouseProduct.ProductCode,
                            descending ? OrderByType.Desc : OrderByType.Asc
                        );
                    }
                    else if (sortField == "barcode")
                    {
                        query = query
                            .OrderBy(
                                (warehouseProduct, product, domesticProduct) => product.Barcode,
                                descending ? OrderByType.Desc : OrderByType.Asc
                            )
                            .OrderBy(
                                (warehouseProduct, product, domesticProduct) => warehouseProduct.ProductCode,
                                OrderByType.Asc
                            );
                    }
                    else if (sortField == "productname")
                    {
                        query = query
                            .OrderBy(
                                (warehouseProduct, product, domesticProduct) => product.ProductName,
                                descending ? OrderByType.Desc : OrderByType.Asc
                            )
                            .OrderBy(
                                (warehouseProduct, product, domesticProduct) => warehouseProduct.ProductCode,
                                OrderByType.Asc
                            );
                    }
                    else if (sortField == "englishname")
                    {
                        query = query
                            .OrderBy(
                                (warehouseProduct, product, domesticProduct) => product.EnglishName,
                                descending ? OrderByType.Desc : OrderByType.Asc
                            )
                            .OrderBy(
                                (warehouseProduct, product, domesticProduct) => warehouseProduct.ProductCode,
                                OrderByType.Asc
                            );
                    }
                    else if (sortField == "suppliercode")
                    {
                        query = query
                            .OrderBy(
                                (warehouseProduct, product, domesticProduct) => domesticProduct.SupplierCode,
                                descending ? OrderByType.Desc : OrderByType.Asc
                            )
                            .OrderBy(
                                (warehouseProduct, product, domesticProduct) => warehouseProduct.ProductCode,
                                OrderByType.Asc
                            );
                    }
                    else
                    {
                        // 默认排序将空货号固定放在最后，并始终以商品编码作为稳定分页键。
                        query = query
                            .OrderBy(
                                (warehouseProduct, product, domesticProduct) =>
                                    product.ItemNumber == null || product.ItemNumber == string.Empty
                                        ? 1
                                        : 0,
                                OrderByType.Asc
                            )
                            .OrderBy(
                                (warehouseProduct, product, domesticProduct) => product.ItemNumber,
                                descending ? OrderByType.Desc : OrderByType.Asc
                            )
                            .OrderBy(
                                (warehouseProduct, product, domesticProduct) => warehouseProduct.ProductCode,
                                OrderByType.Asc
                            );
                    }

                    currentStage = "page";
                    var pageStopwatch = Stopwatch.StartNew();
                    var pageQuery = query.Clone();
                    if (itemNumberPageCodes != null)
                    {
                        var pageCodes = itemNumberPageCodes;
                        pageQuery = pageQuery.Where((warehouseProduct, product, domesticProduct) =>
                            pageCodes.Contains(warehouseProduct.ProductCode)
                        );
                    }
                    var selectedQuery = pageQuery
                        .Select((warehouseProduct, product, domesticProduct) =>
                            new WarehouseProductFlowCandidateDto
                            {
                                ProductCode = warehouseProduct.ProductCode,
                                ItemNumber = product.ItemNumber,
                                Barcode = product.Barcode,
                                ProductName = product.ProductName,
                                EnglishName = product.EnglishName,
                                ImageUrl = product.ProductImage,
                                CategoryGuid = product.WarehouseCategoryGUID,
                                CategoryName = SqlFunc.Subqueryable<WarehouseCategory>()
                                    .Where(category =>
                                        !category.IsDeleted
                                        && category.CategoryGUID == product.WarehouseCategoryGUID
                                    )
                                    .Select(category => category.CategoryName),
                                SupplierCode = domesticProduct.SupplierCode,
                                SupplierName = SqlFunc.Subqueryable<ChinaSupplier>()
                                    .Where(supplier =>
                                        !supplier.IsDeleted
                                        && supplier.SupplierCode == domesticProduct.SupplierCode
                                    )
                                    .OrderBy(supplier => supplier.IsDeleted ? 1 : 0)
                                    .OrderBy(supplier => supplier.Guid)
                                    .Select(supplier => supplier.SupplierName),
                            }
                        );
                    List<WarehouseProductFlowCandidateDto> items;
                    if (itemNumberPageCodes != null)
                    {
                        // 当前页编码已在内存中按粘贴顺序确定，这里只取明细并按同一顺序排列（同编码只保留一行）。
                        var orderByCode = itemNumberPageCodes
                            .Select((code, index) => (code, index))
                            .ToDictionary(pair => pair.code, pair => pair.index, StringComparer.OrdinalIgnoreCase);
                        items = (await selectedQuery.ToListAsync())
                            .Where(item => orderByCode.ContainsKey(item.ProductCode.Trim()))
                            .GroupBy(item => item.ProductCode.Trim(), StringComparer.OrdinalIgnoreCase)
                            .Select(group => group.First())
                            .OrderBy(item => orderByCode[item.ProductCode.Trim()])
                            .ToList();
                    }
                    else
                    {
                        items = await selectedQuery
                            .Skip((pageNumber - 1) * pageSize)
                            .Take(pageSize)
                            .ToListAsync();
                    }
                    pageStopwatch.Stop();
                    pageMilliseconds = pageStopwatch.ElapsedMilliseconds;

                    currentStage = "hydrate";
                    var hydrateStopwatch = Stopwatch.StartNew();
                    foreach (var item in items)
                    {
                        item.CategoryName = string.IsNullOrWhiteSpace(item.CategoryName)
                            ? null
                            : item.CategoryName.Trim();
                        item.SupplierCode = item.SupplierCode?.Trim();
                        item.SupplierName = string.IsNullOrWhiteSpace(item.SupplierName)
                            ? null
                            : item.SupplierName.Trim();
                    }

                    // 绝大多数供应商代码可走精确匹配；仅为当前页异常空格/缺名代码补查，
                    // 避免在全量分页 SQL 的相关子查询中使用 TRIM 破坏索引计划。
                    var unresolvedSupplierCodes = items
                        .Where(item =>
                            !string.IsNullOrWhiteSpace(item.SupplierCode)
                            && string.IsNullOrWhiteSpace(item.SupplierName)
                        )
                        .Select(item => item.SupplierCode!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var normalizedSupplierNames = new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase
                    );
                    foreach (var batch in BatchCodes(unresolvedSupplierCodes))
                    {
                        var batchCodes = batch;
                        var rows = await _context
                            .Db.Queryable<ChinaSupplier>()
                            .Where(supplier =>
                                supplier.SupplierCode != null
                                && batchCodes.Contains(supplier.SupplierCode.Trim())
                            )
                            .OrderBy(supplier => supplier.IsDeleted ? 1 : 0)
                            .OrderBy(supplier => supplier.Guid)
                            .Select(supplier => new
                            {
                                supplier.SupplierCode,
                                supplier.SupplierName,
                            })
                            .ToListAsync();
                        foreach (var row in rows)
                        {
                            var code = row.SupplierCode?.Trim();
                            if (
                                string.IsNullOrWhiteSpace(code)
                                || normalizedSupplierNames.ContainsKey(code)
                                || string.IsNullOrWhiteSpace(row.SupplierName)
                            )
                            {
                                continue;
                            }
                            normalizedSupplierNames[code] = row.SupplierName.Trim();
                        }
                    }
                    foreach (var item in items.Where(item =>
                        !string.IsNullOrWhiteSpace(item.SupplierCode)
                        && string.IsNullOrWhiteSpace(item.SupplierName)
                    ))
                    {
                        item.SupplierName = normalizedSupplierNames.TryGetValue(
                            item.SupplierCode!,
                            out var normalizedName
                        )
                            ? normalizedName
                            : item.SupplierCode;
                    }
                    hydrateStopwatch.Stop();
                    hydrateMilliseconds = hydrateStopwatch.ElapsedMilliseconds;

                    totalStopwatch.Stop();
                    _logger.LogInformation(
                        "仓库商品候选查询完成 FilterMs={FilterMs} CountMs={CountMs} PageMs={PageMs} HydrateMs={HydrateMs} TotalMs={TotalMs} PageNumber={PageNumber} PageSize={PageSize} HasKeyword={HasKeyword} CategoryFilterCount={CategoryFilterCount} SupplierFilterCount={SupplierFilterCount} HasDocumentKeyword={HasDocumentKeyword} Total={Total} Returned={Returned}",
                        filterMilliseconds,
                        countMilliseconds,
                        pageMilliseconds,
                        hydrateMilliseconds,
                        totalStopwatch.ElapsedMilliseconds,
                        pageNumber,
                        pageSize,
                        !string.IsNullOrWhiteSpace(keyword),
                        requestedCategoryGuids.Count,
                        requestedSupplierCodes.Count,
                        !string.IsNullOrWhiteSpace(documentKeyword),
                        total,
                        items.Count
                    );

                    return new WarehouseProductFlowCandidatePageDto
                    {
                        Items = items,
                        Total = total,
                        PageNumber = pageNumber,
                        PageSize = pageSize,
                        ItemNumberMatch = itemNumberMatch,
                    };
                }
                catch (Exception ex)
                {
                    if (currentStage == "filter" && filterStopwatch.IsRunning)
                    {
                        filterStopwatch.Stop();
                        filterMilliseconds = filterStopwatch.ElapsedMilliseconds;
                    }
                    totalStopwatch.Stop();
                    _logger.LogError(
                        ex,
                        "仓库商品候选查询失败 Stage={Stage} FilterMs={FilterMs} CountMs={CountMs} PageMs={PageMs} HydrateMs={HydrateMs} TotalMs={TotalMs} PageNumber={PageNumber} PageSize={PageSize} HasKeyword={HasKeyword} CategoryFilterCount={CategoryFilterCount} SupplierFilterCount={SupplierFilterCount} HasDocumentKeyword={HasDocumentKeyword}",
                        currentStage,
                        filterMilliseconds,
                        countMilliseconds,
                        pageMilliseconds,
                        hydrateMilliseconds,
                        totalStopwatch.ElapsedMilliseconds,
                        pageNumber,
                        pageSize,
                        !string.IsNullOrWhiteSpace(keyword),
                        requestedCategoryGuids.Count,
                        requestedSupplierCodes.Count,
                        !string.IsNullOrWhiteSpace(documentKeyword)
                    );
                    throw;
                }
            });
        }

        public async Task<ApiResponse<WarehouseProductFlowAnalysisSummaryDto>> GetSummaryAsync(
            WarehouseProductFlowAnalysisRequest request,
            List<string>? branchCodes
        )
        {
            var periods = ValidateRequestPeriods(request);
            var cacheKey = BuildCacheKey(
                "summary",
                request,
                branchCodes,
                (periods.ContainerStart, periods.ContainerEnd),
                (periods.OrderShipmentStart, periods.OrderShipmentEnd),
                (periods.SalesStart, periods.SalesEnd)
            );
            return await GetOrCreateAsync(cacheKey, request.ForceRefresh, async () =>
            {
                var context = await BuildQueryContextAsync(
                    request.Filter ?? new WarehouseProductFlowAnalysisFilterDto(),
                    request.Selection
                );
                var selectedCodes = context.FilteredCodes;
                var metrics = await BuildMetricMapAsync(selectedCodes, branchCodes, periods);
                var rows = BuildProductDtos(context, selectedCodes, metrics);
                var page = SortAndPageProducts(
                    rows,
                    request.PageNumber,
                    request.PageSize,
                    request.SortBy,
                    request.SortDirection
                );
                return new WarehouseProductFlowAnalysisSummaryDto
                {
                    Totals = SumMetrics(rows),
                    CurrentProduct = rows.FirstOrDefault(row =>
                        string.Equals(
                            row.ProductCode,
                            request.CurrentProductCode?.Trim(),
                            StringComparison.OrdinalIgnoreCase
                        )
                    ),
                    Items = page.Items,
                    Total = page.Total,
                    PageNumber = page.PageNumber,
                    PageSize = page.PageSize,
                };
            });
        }

        public async Task<ApiResponse<List<WarehouseProductFlowDailyDto>>> GetProductDailyAsync(
            WarehouseProductFlowAnalysisRequest request,
            List<string>? branchCodes
        )
        {
            var productCode = ResolveCurrentProductCode(request);
            var period = ValidateContainerPeriod(request);
            var cacheKey = BuildCacheKey(
                "product-daily",
                request,
                null,
                period
            );
            return await GetOrCreateAsync(cacheKey, request.ForceRefresh, async () =>
            {
                var containerRows = await QueryContainerRowsAsync(
                    new List<string> { productCode },
                    period.StartDate,
                    period.EndDate
                );
                var aggregates = BuildDailyAggregates(
                    Enumerable.Empty<WarehouseProductFlowAnalysisSalesRow>(),
                    Enumerable.Empty<WarehouseProductFlowAnalysisOrderRow>(),
                    Enumerable.Empty<WarehouseProductFlowAnalysisShipmentRow>(),
                    containerRows
                );
                return BuildDailySeries(aggregates, period.StartDate, period.EndDate);
            });
        }

        public async Task<ApiResponse<List<WarehouseProductFlowDailyDto>>> GetOrderShipmentDailyAsync(
            WarehouseProductFlowAnalysisRequest request,
            List<string>? branchCodes
        )
        {
            var productCode = ResolveCurrentProductCode(request);
            var period = ValidateOrderShipmentPeriod(request);
            var cacheKey = BuildCacheKey("order-shipment-daily", request, branchCodes, period);
            return await GetOrCreateAsync(cacheKey, request.ForceRefresh, async () =>
            {
                var codes = new List<string> { productCode };
                var orderRows = await QueryOrderRowsAsync(codes, branchCodes, period.StartDate, period.EndDate);
                var shipmentRows = await QueryShipmentRowsAsync(codes, branchCodes, period.StartDate, period.EndDate);
                var aggregates = BuildDailyAggregates(
                    Enumerable.Empty<WarehouseProductFlowAnalysisSalesRow>(),
                    orderRows,
                    shipmentRows,
                    Enumerable.Empty<WarehouseProductFlowAnalysisContainerRow>()
                );
                return BuildDailySeries(aggregates, period.StartDate, period.EndDate);
            });
        }

        public async Task<ApiResponse<List<WarehouseProductFlowDailyDto>>> GetSalesDailyAsync(
            WarehouseProductFlowAnalysisRequest request,
            List<string>? branchCodes
        )
        {
            var productCode = ResolveCurrentProductCode(request);
            var period = ValidateSalesPeriod(request);
            var cacheKey = BuildCacheKey("sales-daily", request, branchCodes, period);
            return await GetOrCreateAsync(cacheKey, request.ForceRefresh, async () =>
            {
                var salesRows = await QuerySalesRowsAsync(
                    new List<string> { productCode },
                    branchCodes,
                    period.StartDate,
                    period.EndDate
                );
                var aggregates = BuildDailyAggregates(
                    salesRows,
                    Enumerable.Empty<WarehouseProductFlowAnalysisOrderRow>(),
                    Enumerable.Empty<WarehouseProductFlowAnalysisShipmentRow>(),
                    Enumerable.Empty<WarehouseProductFlowAnalysisContainerRow>()
                );
                return BuildDailySeries(aggregates, period.StartDate, period.EndDate);
            });
        }

        public async Task<ApiResponse<List<WarehouseProductFlowContainerDto>>> GetContainersAsync(
            WarehouseProductFlowAnalysisRequest request,
            List<string>? branchCodes
        )
        {
            var productCode = ResolveCurrentProductCode(request);
            var period = ValidateContainerPeriod(request);
            var cacheKey = BuildCacheKey(
                "containers",
                request,
                null,
                period
            );
            return await GetOrCreateAsync(cacheKey, request.ForceRefresh, async () =>
            {
                var rows = await QueryContainerRowsAsync(
                    new List<string> { productCode },
                    period.StartDate,
                    period.EndDate
                );
                var documentKeyword = request.Filter?.DocumentKeyword?.Trim() ?? string.Empty;
                var supplierName = await GetDomesticSupplierNameAsync(productCode);
                return rows
                    .Where(row =>
                        string.IsNullOrEmpty(documentKeyword)
                        || ContainsIgnoreCase(row.ContainerNumber, documentKeyword)
                    )
                    .OrderBy(row => row.ArrivalDate)
                    .ThenBy(row => row.ContainerNumber, StringComparer.OrdinalIgnoreCase)
                    .Select(row => new WarehouseProductFlowContainerDto
                    {
                        ContainerNumber = row.ContainerNumber,
                        ArrivalDate = row.ArrivalDate,
                        InboundQuantity = row.LoadingQuantity,
                        InboundUnitPrice = row.InboundUnitPrice,
                        SupplierName = supplierName,
                    })
                    .ToList();
            });
        }

        public async Task<ApiResponse<List<WarehouseProductFlowOrderDto>>> GetOrdersAsync(
            WarehouseProductFlowAnalysisRequest request,
            List<string>? branchCodes
        )
        {
            var productCode = ResolveCurrentProductCode(request);
            var period = ValidateOrderShipmentPeriod(request);
            var cacheKey = BuildCacheKey(
                "orders",
                request,
                branchCodes,
                period
            );
            return await GetOrCreateAsync(cacheKey, request.ForceRefresh, async () =>
            {
                var rows = await QueryOrderRowsAsync(
                    new List<string> { productCode },
                    branchCodes,
                    period.StartDate,
                    period.EndDate
                );
                var storeNames = await GetStoreNameMapAsync(rows.Select(row => row.StoreCode));
                return rows
                    .OrderBy(row => row.OrderDate)
                    .ThenBy(row => row.OrderNo, StringComparer.OrdinalIgnoreCase)
                    .Select(row => new WarehouseProductFlowOrderDto
                    {
                        OrderNumber = row.OrderNo,
                        BranchName = storeNames.GetValueOrDefault(row.StoreCode),
                        OrderDate = row.OrderDate,
                        OrderedQuantity = row.Quantity,
                    })
                    .ToList();
            });
        }

        public async Task<ApiResponse<List<WarehouseProductFlowShipmentDto>>> GetShipmentsAsync(
            WarehouseProductFlowAnalysisRequest request,
            List<string>? branchCodes
        )
        {
            var productCode = ResolveCurrentProductCode(request);
            var period = ValidateOrderShipmentPeriod(request);
            var salesPeriod = ValidateSalesPeriod(request);
            var cacheKey = BuildCacheKey(
                "shipments",
                request,
                branchCodes,
                period,
                salesPeriod
            );
            return await GetOrCreateAsync(cacheKey, request.ForceRefresh, async () =>
            {
                var rows = await QueryShipmentRowsAsync(
                    new List<string> { productCode },
                    branchCodes,
                    period.StartDate,
                    period.EndDate
                );
                if (rows.Count == 0)
                    return new List<WarehouseProductFlowShipmentDto>();
                var storeMetadata = await GetStoreMetadataMapAsync(rows.Select(row => row.StoreCode));
                var salesByBranch = (await QuerySalesRowsAsync(
                        new List<string> { productCode },
                        branchCodes,
                        salesPeriod.StartDate,
                        salesPeriod.EndDate
                    ))
                    .GroupBy(row => row.BranchCode, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Sum(row => row.TotalQuantity), StringComparer.OrdinalIgnoreCase);
                return rows
                    .OrderBy(row => row.OutboundDate)
                    .ThenBy(row => row.OrderNo, StringComparer.OrdinalIgnoreCase)
                    .Select(row =>
                    {
                        storeMetadata.TryGetValue(row.StoreCode, out var metadata);
                        return new WarehouseProductFlowShipmentDto
                        {
                            ShipmentNumber = null,
                            OrderNumber = row.OrderNo,
                            BranchName = metadata?.BranchName,
                            ShipmentDate = row.OutboundDate,
                            ShippedQuantity = row.AllocQuantity,
                            BranchCode = string.IsNullOrWhiteSpace(row.StoreCode) ? null : row.StoreCode,
                            PosEnabled = metadata?.PosEnabled,
                            NetSalesQuantity = salesByBranch.GetValueOrDefault(row.StoreCode),
                        };
                    })
                    .ToList();
            });
        }

        public async Task<ApiResponse<List<WarehouseProductFlowBranchDto>>> GetBranchesAsync(
            WarehouseProductFlowAnalysisRequest request,
            List<string>? branchCodes
        )
        {
            var productCode = ResolveCurrentProductCode(request);
            var period = ValidateSalesPeriod(request);
            var cacheKey = BuildCacheKey("branches", request, branchCodes, period);
            return await GetOrCreateAsync(cacheKey, request.ForceRefresh, async () =>
            {
                var salesRows = await QuerySalesRowsAsync(
                    new List<string> { productCode },
                    branchCodes,
                    period.StartDate,
                    period.EndDate
                );

                var branchCodesInRows = salesRows
                    .Select(row => row.BranchCode)
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var storeNames = await GetStoreNameMapAsync(branchCodesInRows);

                return branchCodesInRows
                    .Select(branchCode =>
                    {
                        var netSales = salesRows
                            .Where(row => string.Equals(row.BranchCode, branchCode, StringComparison.OrdinalIgnoreCase))
                            .Aggregate(
                                (quantity: 0, amount: 0m),
                                (acc, row) => (acc.quantity + row.TotalQuantity, acc.amount + row.TotalAmount)
                            );
                        return new WarehouseProductFlowBranchDto
                        {
                            BranchCode = branchCode,
                            BranchName = storeNames.GetValueOrDefault(branchCode),
                            NetSalesQuantity = netSales.quantity,
                            NetSalesAmount = netSales.amount,
                            AverageUnitPrice = CalculateAverageUnitPrice(netSales.quantity, netSales.amount),
                        };
                    })
                    .OrderByDescending(row => row.NetSalesQuantity)
                    .ThenBy(row => row.BranchCode, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            });
        }

        public async Task<ApiResponse<List<WarehouseProductFlowDailyDto>>> GetBranchDailyAsync(
            WarehouseProductFlowAnalysisRequest request,
            List<string>? branchCodes
        )
        {
            var productCode = ResolveCurrentProductCode(request);
            var branchCode = ResolveBranchCode(request);
            var period = ValidateSalesPeriod(request);
            var cacheKey = BuildCacheKey("branch-daily", request, branchCodes, period);
            return await GetOrCreateAsync(cacheKey, request.ForceRefresh, async () =>
            {
                if (branchCodes != null && !branchCodes.Contains(branchCode, StringComparer.OrdinalIgnoreCase))
                    return new List<WarehouseProductFlowDailyDto>();

                var salesRows = await QuerySalesRowsAsync(
                    new List<string> { productCode },
                    branchCodes,
                    period.StartDate,
                    period.EndDate
                );
                var scopedSales = salesRows.Where(row =>
                    string.Equals(row.BranchCode, branchCode, StringComparison.OrdinalIgnoreCase)
                );
                var aggregates = BuildDailyAggregates(
                    scopedSales,
                    Enumerable.Empty<WarehouseProductFlowAnalysisOrderRow>(),
                    Enumerable.Empty<WarehouseProductFlowAnalysisShipmentRow>(),
                    Enumerable.Empty<WarehouseProductFlowAnalysisContainerRow>()
                );
                return BuildDailySeries(aggregates, period.StartDate, period.EndDate);
            });
        }

        private async Task<WarehouseProductFlowAnalysisQueryContext> BuildQueryContextAsync(
            WarehouseProductFlowAnalysisFilterDto filter,
            WarehouseProductFlowAnalysisSelectionDto? selection
        )
        {
            var warehouseProducts = _context
                .Db.Queryable<WarehouseProduct>()
                .Where(w => !w.IsDeleted);
            var allCodes = new List<string>();
            if (string.Equals(selection?.Mode?.Trim(), "included", StringComparison.OrdinalIgnoreCase))
            {
                var includedCodes = NormalizeCodes(selection?.IncludedProductCodes);
                foreach (var batch in BatchCodes(includedCodes))
                {
                    allCodes.AddRange(await warehouseProducts.Clone()
                        .Where(product => batch.Contains(product.ProductCode))
                        .Select(product => product.ProductCode)
                        .ToListAsync());
                }

                // 正常单选仅传回所选编码。未完整命中时保留旧数据的空格/大小写兼容，
                // 仍由下方原有 NormalizeCodes + ApplySelection 判断最终选择范围。
                var matchedCodes = NormalizeCodes(allCodes).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (includedCodes.Any(code => !matchedCodes.Contains(code)))
                    allCodes = await warehouseProducts.Select(product => product.ProductCode).ToListAsync();
            }
            else
            {
                allCodes = await warehouseProducts.Select(product => product.ProductCode).ToListAsync();
            }
            // 选择与主档筛选取交集；先缩小补资料范围，避免单选商品也逐批读取整仓主档。
            // 在编码归一化后沿用原有选择逻辑，保留大小写、空格及排除选择的语义。
            var selectedCodes = ApplySelection(NormalizeCodes(allCodes), selection);
            var context = new WarehouseProductFlowAnalysisQueryContext { AllCodes = selectedCodes };
            if (selectedCodes.Count == 0)
                return context;

            var categories = await _context
                .Db.Queryable<WarehouseCategory>()
                .Where(c => !c.IsDeleted)
                .Select(c => new { c.CategoryGUID, c.CategoryName, c.ParentGUID })
                .ToListAsync();

            foreach (var category in categories)
            {
                if (string.IsNullOrWhiteSpace(category.CategoryGUID))
                    continue;
                context.CategoryNameByGuid[category.CategoryGUID.Trim()] =
                    string.IsNullOrWhiteSpace(category.CategoryName)
                        ? category.CategoryGUID.Trim()
                        : category.CategoryName.Trim();
            }

            await LoadProductInfosAsync(context, selectedCodes);
            await LoadDomesticInfosAsync(context, selectedCodes);

            var requestedCategoryGuids = NormalizeCodes(filter.WarehouseCategoryGuids);
            var requestedSupplierCodes = NormalizeCodes(filter.SupplierCodes);

            var categorySet = requestedCategoryGuids.Count > 0
                ? ExpandCategoryGuids(categories, requestedCategoryGuids)
                : null;
            var supplierSet = requestedSupplierCodes.Count > 0
                ? requestedSupplierCodes.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : null;
            var keyword = filter.Keyword?.Trim() ?? string.Empty;
            var documentKeyword = filter.DocumentKeyword?.Trim() ?? string.Empty;
            // 批量货号与候选列表同一口径：精确匹配货号（any 模式加条码、商品编码）。
            var itemNumbers = NormalizeItemNumbers(filter.ItemNumbers);
            var itemNumberRanks = itemNumbers.Count > 0 ? BuildItemNumberRankMap(itemNumbers) : null;
            var matchAnyItemNumber = IsAnyItemNumberMatchMode(filter.ItemNumberMatchMode);
            var documentProductCodes = string.IsNullOrWhiteSpace(documentKeyword)
                ? null
                : await QueryContainerProductCodesAsync(documentKeyword);

            context.FilteredCodes = context.AllCodes
                .Where(code =>
                    (categorySet == null
                        || (context.ProductByCode.TryGetValue(code, out var product)
                            && product.CategoryGuid != null
                            && categorySet.Contains(product.CategoryGuid)))
                    && (supplierSet == null
                        || (context.DomesticByCode.TryGetValue(code, out var domestic)
                            && domestic.SupplierCode != null
                            && supplierSet.Contains(domestic.SupplierCode)))
                    && (documentProductCodes == null || documentProductCodes.Contains(code))
                    && (string.IsNullOrWhiteSpace(keyword) || MatchesKeyword(context, code, keyword))
                    && (itemNumberRanks == null || MatchesItemNumbers(context, code, itemNumberRanks, matchAnyItemNumber)))
                .ToList();

            return context;
        }

        private async Task LoadProductInfosAsync(
            WarehouseProductFlowAnalysisQueryContext context,
            List<string> codes
        )
        {
            foreach (var batch in BatchCodes(codes))
            {
                var batchCodes = batch;
                // 先限定商品再按 UUID 去重，避免每个批次重复聚合全部 Product 主档。
                var rows = await BuildCanonicalProductQuery(batchCodes)
                    .Select(p => new
                    {
                        p.ProductCode,
                        p.ItemNumber,
                        p.Barcode,
                        p.ProductName,
                        p.EnglishName,
                        p.ProductImage,
                        p.WarehouseCategoryGUID,
                    })
                    .ToListAsync();
                foreach (var row in rows)
                {
                    var code = (row.ProductCode ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(code) || context.ProductByCode.ContainsKey(code))
                        continue;
                    context.ProductByCode[code] = new WarehouseProductFlowAnalysisProductInfo
                    {
                        ProductCode = code,
                        ItemNumber = row.ItemNumber,
                        Barcode = row.Barcode,
                        ProductName = row.ProductName,
                        EnglishName = row.EnglishName,
                        ImageUrl = row.ProductImage,
                        CategoryGuid = row.WarehouseCategoryGUID,
                    };
                }
            }
        }

        private ISugarQueryable<Product> BuildCanonicalProductQuery(List<string>? productCodes = null)
        {
            // ProductCode 不是 Product 的数据库主键；先按最小 UUID 固定一条主档，
            // 保证过滤、计数、分页和补资料都不会因重复 ProductCode 产生重复商品行。
            var productQuery = _context
                .Db.Queryable<Product>()
                .Where(product => !product.IsDeleted && product.ProductCode != null);
            if (productCodes is { Count: > 0 })
            {
                var codes = productCodes;
                productQuery = productQuery.Where(product => codes.Contains(product.ProductCode!));
            }

            var canonicalProductIds = productQuery
                .GroupBy(product => product.ProductCode)
                .Select(product => new
                {
                    ProductCode = product.ProductCode!,
                    UUID = SqlFunc.AggregateMin(product.UUID),
                })
                .MergeTable();

            return _context
                .Db.Queryable<Product>()
                .InnerJoin(
                    canonicalProductIds,
                    (product, canonical) =>
                        product.ProductCode == canonical.ProductCode
                        && product.UUID == canonical.UUID
                )
                .Select((product, canonical) => product)
                .MergeTable();
        }

        private ISugarQueryable<Product> BuildCandidateProductQuery(
            bool hasActiveProductCodeDuplicates,
            List<string>? productCodes = null
        )
        {
            if (hasActiveProductCodeDuplicates)
                return BuildCanonicalProductQuery(productCodes);

            var query = _context
                .Db.Queryable<Product>()
                .Where(product => !product.IsDeleted && product.ProductCode != null);
            if (productCodes is { Count: > 0 })
            {
                var codes = productCodes;
                query = query.Where(product => codes.Contains(product.ProductCode!));
            }
            return query;
        }

        private async Task<bool> HasActiveProductCodeDuplicatesAsync()
        {
            if (_cache.TryGetValue<bool>(ActiveProductCodeDuplicatesCacheKey, out var cached))
                return cached;

            var grouped = _context
                .Db.Queryable<Product>()
                .Where(product => !product.IsDeleted && product.ProductCode != null)
                .GroupBy(product => product.ProductCode)
                .Select(product => new
                {
                    Count = SqlFunc.AggregateCount(product.UUID),
                })
                .MergeTable();
            var hasDuplicates = await grouped.AnyAsync(row => row.Count > 1);
            _cache.Set(
                ActiveProductCodeDuplicatesCacheKey,
                hasDuplicates,
                new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheDuration }
            );
            return hasDuplicates;
        }

        private async Task LoadDomesticInfosAsync(
            WarehouseProductFlowAnalysisQueryContext context,
            List<string> codes
        )
        {
            foreach (var batch in BatchCodes(codes))
            {
                var batchCodes = batch;
                var rows = await _context
                    .Db.Queryable<DomesticProduct>()
                    .Where(dp => !dp.IsDeleted && batchCodes.Contains(dp.ProductCode))
                    .Select(dp => new { dp.ProductCode, dp.SupplierCode })
                    .ToListAsync();
                foreach (var row in rows)
                {
                    var code = row.ProductCode.Trim();
                    if (string.IsNullOrWhiteSpace(code) || context.DomesticByCode.ContainsKey(code))
                        continue;
                    context.DomesticByCode[code] = new WarehouseProductFlowAnalysisDomesticInfo
                    {
                        ProductCode = code,
                        SupplierCode = row.SupplierCode?.Trim(),
                    };
                }
            }

            var supplierCodes = context.DomesticByCode.Values
                .Select(info => info.SupplierCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var batch in BatchCodes(supplierCodes))
            {
                var batchCodes = batch;
                var rows = await _context
                    .Db.Queryable<ChinaSupplier>()
                    .Where(cs =>
                        cs.SupplierCode != null && batchCodes.Contains(cs.SupplierCode.Trim())
                    )
                    .OrderBy(cs => cs.IsDeleted ? 1 : 0)
                    .OrderBy(cs => cs.Guid)
                    .Select(cs => new { cs.SupplierCode, cs.SupplierName })
                    .ToListAsync();
                foreach (var row in rows)
                {
                    var code = (row.SupplierCode ?? string.Empty).Trim();
                    if (
                        string.IsNullOrWhiteSpace(code)
                        || context.ChinaNameByCode.ContainsKey(code)
                        || string.IsNullOrWhiteSpace(row.SupplierName)
                    )
                        continue;
                    context.ChinaNameByCode[code] = row.SupplierName.Trim();
                }
            }
        }

        private async Task<Dictionary<string, WarehouseProductFlowMetricsDto>> BuildMetricMapAsync(
            List<string> codes,
            List<string>? branchCodes,
            WarehouseProductFlowAnalysisPeriodBounds periods
        )
        {
            var salesRows = await QuerySalesRowsAsync(codes, branchCodes, periods.SalesStart, periods.SalesEnd);
            var orderRows = await QueryOrderRowsAsync(
                codes,
                branchCodes,
                periods.OrderShipmentStart,
                periods.OrderShipmentEnd
            );
            var shipmentRows = await QueryShipmentRowsAsync(
                codes,
                branchCodes,
                periods.OrderShipmentStart,
                periods.OrderShipmentEnd
            );
            var containerRows = await QueryContainerRowsAsync(
                codes,
                periods.ContainerStart,
                periods.ContainerEnd
            );

            var map = new Dictionary<string, WarehouseProductFlowMetricsDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var code in codes)
                map[code] = new WarehouseProductFlowMetricsDto();

            foreach (var group in salesRows.GroupBy(row => row.ProductCode, StringComparer.OrdinalIgnoreCase))
            {
                var metric = map[group.Key];
                metric.NetSalesQuantity = group.Sum(row => row.TotalQuantity);
                metric.NetSalesAmount = group.Sum(row => row.TotalAmount);
                metric.AverageUnitPrice = CalculateAverageUnitPrice(metric.NetSalesQuantity, metric.NetSalesAmount);
            }

            foreach (var group in orderRows.GroupBy(row => row.ProductCode, StringComparer.OrdinalIgnoreCase))
                map[group.Key].OrderedQuantity = group.Sum(row => row.Quantity);

            foreach (var group in shipmentRows.GroupBy(row => row.ProductCode, StringComparer.OrdinalIgnoreCase))
                map[group.Key].ShippedQuantity = group.Sum(row => row.AllocQuantity);

            foreach (var group in containerRows.GroupBy(row => row.ProductCode, StringComparer.OrdinalIgnoreCase))
                map[group.Key].InboundQuantity = group.Sum(row => row.LoadingQuantity);

            return map;
        }

        private async Task<List<WarehouseProductFlowAnalysisSalesRow>> QuerySalesRowsAsync(
            List<string> codes,
            List<string>? branchCodes,
            DateTime startDate,
            DateTime endDate
        )
        {
            if (branchCodes is { Count: 0 })
                return new List<WarehouseProductFlowAnalysisSalesRow>();

            var result = new List<WarehouseProductFlowAnalysisSalesRow>();
            foreach (var batch in BatchCodes(codes))
            {
                var batchCodes = batch;
                var query = _context
                    .Db.Queryable<ProductStoreDailySalesStatistic>()
                    .Where(s =>
                        s.Date >= startDate
                        && s.Date < endDate.AddDays(1)
                        && batchCodes.Contains(s.ProductCode)
                    );
                if (branchCodes is { Count: > 0 })
                {
                    query = query.Where(s => branchCodes.Contains(s.BranchCode));
                }
                var rows = await query
                    .Select(s => new
                    {
                        s.Date,
                        s.BranchCode,
                        s.ProductCode,
                        s.TotalQuantity,
                        s.TotalAmount,
                    })
                    .ToListAsync();
                result.AddRange(rows.Select(row => new WarehouseProductFlowAnalysisSalesRow
                {
                    ProductCode = row.ProductCode,
                    BranchCode = row.BranchCode,
                    Date = row.Date,
                    TotalQuantity = row.TotalQuantity,
                    TotalAmount = row.TotalAmount,
                }));
            }
            return result;
        }

        private async Task<List<WarehouseProductFlowAnalysisOrderRow>> QueryOrderRowsAsync(
            List<string> codes,
            List<string>? branchCodes,
            DateTime startDate,
            DateTime endDate
        )
        {
            if (branchCodes is { Count: 0 })
                return new List<WarehouseProductFlowAnalysisOrderRow>();

            var result = new List<WarehouseProductFlowAnalysisOrderRow>();
            foreach (var batch in BatchCodes(codes))
            {
                var batchCodes = batch;
                var query = _context
                    .Db.Queryable<WareHouseOrderDetails>()
                    .InnerJoin<WareHouseOrder>((d, o) => d.OrderGUID == o.OrderGUID)
                    .Where((d, o) =>
                        !d.IsDeleted
                        && !o.IsDeleted
                        && o.FlowStatus > 0
                        && o.OrderDate >= startDate
                        && o.OrderDate < endDate.AddDays(1)
                        && d.ProductCode != null
                        && batchCodes.Contains(d.ProductCode!)
                    );
                if (branchCodes is { Count: > 0 })
                {
                    query = query.Where((d, o) => o.StoreCode != null && branchCodes.Contains(o.StoreCode));
                }
                var rows = await query
                    .Select((d, o) => new
                    {
                        d.ProductCode,
                        StoreCode = o.StoreCode,
                        OrderNo = o.OrderNo,
                        OrderDate = o.OrderDate,
                        d.Quantity,
                    })
                    .ToListAsync();
                result.AddRange(rows.Select(row => new WarehouseProductFlowAnalysisOrderRow
                {
                    ProductCode = row.ProductCode ?? string.Empty,
                    StoreCode = row.StoreCode ?? string.Empty,
                    OrderNo = row.OrderNo ?? string.Empty,
                    OrderDate = row.OrderDate,
                    Quantity = row.Quantity ?? 0m,
                }));
            }
            return result;
        }

        private async Task<List<WarehouseProductFlowAnalysisShipmentRow>> QueryShipmentRowsAsync(
            List<string> codes,
            List<string>? branchCodes,
            DateTime startDate,
            DateTime endDate
        )
        {
            if (branchCodes is { Count: 0 })
                return new List<WarehouseProductFlowAnalysisShipmentRow>();

            var result = new List<WarehouseProductFlowAnalysisShipmentRow>();
            foreach (var batch in BatchCodes(codes))
            {
                var batchCodes = batch;
                var query = _context
                    .Db.Queryable<WareHouseOrderDetails>()
                    .InnerJoin<WareHouseOrder>((d, o) => d.OrderGUID == o.OrderGUID)
                    .Where((d, o) =>
                        !d.IsDeleted
                        && !o.IsDeleted
                        && o.OutboundDate != null
                        && d.AllocQuantity > 0
                        && o.OutboundDate >= startDate
                        && o.OutboundDate < endDate.AddDays(1)
                        && d.ProductCode != null
                        && batchCodes.Contains(d.ProductCode!)
                    );
                if (branchCodes is { Count: > 0 })
                {
                    query = query.Where((d, o) => o.StoreCode != null && branchCodes.Contains(o.StoreCode));
                }
                var rows = await query
                    .Select((d, o) => new
                    {
                        d.ProductCode,
                        StoreCode = o.StoreCode,
                        OrderNo = o.OrderNo,
                        OutboundDate = o.OutboundDate,
                        d.AllocQuantity,
                    })
                    .ToListAsync();
                result.AddRange(rows.Select(row => new WarehouseProductFlowAnalysisShipmentRow
                {
                    ProductCode = row.ProductCode ?? string.Empty,
                    StoreCode = row.StoreCode ?? string.Empty,
                    OrderNo = row.OrderNo ?? string.Empty,
                    OutboundDate = row.OutboundDate,
                    AllocQuantity = row.AllocQuantity ?? 0m,
                }));
            }
            return result;
        }

        private async Task<List<WarehouseProductFlowAnalysisContainerRow>> QueryContainerRowsAsync(
            List<string> codes,
            DateTime startDate,
            DateTime endDate
        )
        {
            var result = new List<WarehouseProductFlowAnalysisContainerRow>();
            var exclusiveEndDate = endDate.AddDays(1);
            foreach (var batch in BatchCodes(codes))
            {
                var batchCodes = batch;
                var rows = await _context
                    .Db.Queryable<ContainerDetail>()
                    .InnerJoin<Container>((cd, c) => cd.ContainerCode == c.ContainerCode)
                    .Where((cd, c) =>
                        !cd.IsDeleted
                        && !c.IsDeleted
                        && (c.Status == null || c.Status != 7)
                        && (cd.Status == null || cd.Status != 6)
                        && cd.ProductCode != null
                        && batchCodes.Contains(cd.ProductCode)
                        && (
                            (c.ActualArrivalDate != null
                                && c.ActualArrivalDate >= startDate
                                && c.ActualArrivalDate < exclusiveEndDate)
                            || (c.ActualArrivalDate == null
                                && c.EstimatedArrivalDate != null
                                && c.EstimatedArrivalDate >= startDate
                                && c.EstimatedArrivalDate < exclusiveEndDate)
                        )
                    )
                    .Select((cd, c) => new
                    {
                        cd.ProductCode,
                        ContainerNumber = c.ContainerNumber,
                        ActualArrivalDate = c.ActualArrivalDate,
                        EstimatedArrivalDate = c.EstimatedArrivalDate,
                        ContainerStatus = c.Status,
                        DetailStatus = cd.Status,
                        cd.LoadingQuantity,
                        cd.LastImportPrice,
                    })
                    .ToListAsync();
                foreach (var row in rows)
                {
                    var arrivalDate = row.ActualArrivalDate ?? row.EstimatedArrivalDate;
                    if (!arrivalDate.HasValue)
                        continue;
                    result.Add(new WarehouseProductFlowAnalysisContainerRow
                    {
                        ProductCode = row.ProductCode ?? string.Empty,
                        ContainerNumber = row.ContainerNumber ?? string.Empty,
                        ArrivalDate = arrivalDate,
                        LoadingQuantity = row.LoadingQuantity ?? 0m,
                        InboundUnitPrice = row.LastImportPrice,
                    });
                }
            }
            return result;
        }

        private async Task<HashSet<string>> QueryContainerProductCodesAsync(string documentKeyword)
        {
            var rows = await _context
                .Db.Queryable<ContainerDetail>()
                .InnerJoin<Container>((cd, c) => cd.ContainerCode == c.ContainerCode)
                .Where((cd, c) =>
                    !cd.IsDeleted
                    && !c.IsDeleted
                    && cd.ProductCode != null
                    && (cd.Status == null || cd.Status != 6)
                    && c.ContainerNumber != null
                    && c.ContainerNumber.Contains(documentKeyword)
                    && (c.Status == null || c.Status != 7)
                )
                .Select((cd, c) => new { cd.ProductCode })
                .Distinct()
                .ToListAsync();

            return rows
                .Select(row => row.ProductCode?.Trim())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private async Task<Dictionary<string, string>> GetStoreNameMapAsync(IEnumerable<string?> storeCodes)
        {
            var codes = NormalizeCodes(storeCodes);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var batch in BatchCodes(codes))
            {
                var batchCodes = batch;
                var rows = await _context
                    .Db.Queryable<Store>()
                    .Where(s => batchCodes.Contains(s.StoreCode))
                    .Select(s => new { s.StoreCode, s.StoreName })
                    .ToListAsync();
                foreach (var row in rows)
                {
                    var code = row.StoreCode.Trim();
                    if (string.IsNullOrWhiteSpace(code) || map.ContainsKey(code))
                        continue;
                    map[code] = string.IsNullOrWhiteSpace(row.StoreName) ? code : row.StoreName.Trim();
                }
            }
            return map;
        }

        private async Task<Dictionary<string, WarehouseProductFlowAnalysisStoreMetadata>> GetStoreMetadataMapAsync(
            IEnumerable<string?> storeCodes
        )
        {
            var codes = NormalizeCodes(storeCodes);
            var map = new Dictionary<string, WarehouseProductFlowAnalysisStoreMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (var batch in BatchCodes(codes))
            {
                var rows = await _context
                    .Db.Queryable<Store>()
                    .Where(s => batch.Contains(s.StoreCode))
                    .Select(s => new { s.StoreCode, s.StoreName, s.IsActive })
                    .ToListAsync();
                foreach (var row in rows)
                {
                    var code = row.StoreCode?.Trim();
                    if (string.IsNullOrWhiteSpace(code) || map.ContainsKey(code))
                        continue;
                    map[code] = new WarehouseProductFlowAnalysisStoreMetadata
                    {
                        BranchName = string.IsNullOrWhiteSpace(row.StoreName) ? code : row.StoreName.Trim(),
                        PosEnabled = row.IsActive,
                    };
                }
            }
            return map;
        }

        private async Task<string?> GetDomesticSupplierNameAsync(string productCode)
        {
            var domestic = await _context
                .Db.Queryable<DomesticProduct>()
                .Where(dp => !dp.IsDeleted && dp.ProductCode == productCode)
                .Select(dp => new { dp.SupplierCode })
                .FirstAsync();
            if (domestic?.SupplierCode == null)
                return null;

            var supplierCode = domestic.SupplierCode.Trim();
            var suppliers = await _context
                .Db.Queryable<ChinaSupplier>()
                .Where(cs => cs.SupplierCode != null && cs.SupplierCode.Trim() == supplierCode)
                .OrderBy(cs => cs.IsDeleted ? 1 : 0)
                .OrderBy(cs => cs.Guid)
                .Select(cs => new { cs.SupplierName })
                .ToListAsync();
            return suppliers
                    .Select(supplier => supplier.SupplierName?.Trim())
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                ?? supplierCode;
        }

        private async Task<WarehouseProductFlowAnalysisOptionsDto> QueryOptionsAsync()
        {
            var categories = await GetCategoryHierarchyRowsAsync();
            var domesticCodes = await _context
                .Db.Queryable<DomesticProduct>()
                .Where(dp => !dp.IsDeleted && dp.SupplierCode != null && dp.SupplierCode != "")
                .Select(dp => new { dp.SupplierCode })
                .Distinct()
                .ToListAsync();
            var codes = NormalizeCodes(domesticCodes.Select(row => row.SupplierCode));
            var nameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var batch in BatchCodes(codes))
            {
                var batchCodes = batch;
                var rows = await _context
                    .Db.Queryable<ChinaSupplier>()
                    .Where(cs =>
                        cs.SupplierCode != null && batchCodes.Contains(cs.SupplierCode.Trim())
                    )
                    .OrderBy(cs => cs.IsDeleted ? 1 : 0)
                    .OrderBy(cs => cs.Guid)
                    .Select(cs => new { cs.SupplierCode, cs.SupplierName })
                    .ToListAsync();
                foreach (var row in rows)
                {
                    var code = (row.SupplierCode ?? string.Empty).Trim();
                    if (
                        string.IsNullOrWhiteSpace(code)
                        || nameMap.ContainsKey(code)
                        || string.IsNullOrWhiteSpace(row.SupplierName)
                    )
                        continue;
                    nameMap[code] = row.SupplierName.Trim();
                }
            }

            return new WarehouseProductFlowAnalysisOptionsDto
            {
                WarehouseCategories = categories
                    .Where(c => !string.IsNullOrWhiteSpace(c.CategoryGUID))
                    .Select(c => new WarehouseCategoryOptionDto
                    {
                        CategoryGuid = c.CategoryGUID!.Trim(),
                        CategoryName = string.IsNullOrWhiteSpace(c.CategoryName)
                            ? c.CategoryGUID!.Trim()
                            : c.CategoryName.Trim(),
                        ParentGuid = c.ParentGUID,
                    })
                    .OrderBy(c => c.CategoryGuid, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                DomesticSuppliers = codes
                    .Select(code => new WarehouseProductFlowSupplierOptionDto
                    {
                        Code = code,
                        Name = nameMap.TryGetValue(code, out var name) ? name : code,
                    })
                    .OrderBy(s => s.Code, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
        }

        private async Task<List<WarehouseProductFlowAnalysisCategoryHierarchyRow>>
            GetCategoryHierarchyRowsAsync()
        {
            if (
                _cache.TryGetValue(
                    CategoryHierarchyCacheKey,
                    out List<WarehouseProductFlowAnalysisCategoryHierarchyRow>? cached
                )
                && cached != null
            )
            {
                return cached;
            }

            var rows = await _context
                .Db.Queryable<WarehouseCategory>()
                .Where(category => !category.IsDeleted)
                .Select(category => new WarehouseProductFlowAnalysisCategoryHierarchyRow
                {
                    CategoryGUID = category.CategoryGUID,
                    CategoryName = category.CategoryName,
                    ParentGUID = category.ParentGUID,
                })
                .ToListAsync();
            _cache.Set(CategoryHierarchyCacheKey, rows, CacheDuration);
            return rows;
        }

        private async Task<ApiResponse<T>> GetOrCreateAsync<T>(
            string cacheKey,
            bool forceRefresh,
            Func<Task<T>> factory
        )
        {
            if (!forceRefresh && _cache.TryGetValue<ApiResponse<T>>(cacheKey, out var cached) && cached != null)
                return cached;

            var data = await factory();
            var response = ApiResponse<T>.OK(data);
            _cache.Set(
                cacheKey,
                response,
                new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheDuration }
            );
            return response;
        }

        private static WarehouseProductFlowAnalysisPeriodBounds ValidateRequestPeriods(
            WarehouseProductFlowAnalysisRequest request
        )
        {
            return ValidateWarehouseProductFlowAnalysisPeriods(
                request.Periods ?? new BlazorApp.Shared.DTOs.WarehouseProductFlowPeriodsDto(),
                DateTime.UtcNow
            );
        }

        private static (DateTime StartDate, DateTime EndDate) ValidateContainerPeriod(
            WarehouseProductFlowAnalysisRequest request
        )
        {
            return ValidatePeriod(request.Periods?.ContainerPeriod, "containerPeriod", DateTime.UtcNow);
        }

        private static (DateTime StartDate, DateTime EndDate) ValidateOrderShipmentPeriod(
            WarehouseProductFlowAnalysisRequest request
        )
        {
            return ValidatePeriod(
                request.Periods?.OrderShipmentPeriod,
                "orderShipmentPeriod",
                DateTime.UtcNow
            );
        }

        private static (DateTime StartDate, DateTime EndDate) ValidateSalesPeriod(
            WarehouseProductFlowAnalysisRequest request
        )
        {
            return ValidatePeriod(request.Periods?.SalesPeriod, "salesPeriod", DateTime.UtcNow);
        }

        internal static WarehouseProductFlowAnalysisPeriodBounds ValidateWarehouseProductFlowAnalysisPeriods(
            BlazorApp.Shared.DTOs.WarehouseProductFlowPeriodsDto periods,
            DateTime utcNow
        )
        {
            var container = ValidatePeriod(periods.ContainerPeriod, "containerPeriod", utcNow);
            var orderShipment = ValidatePeriod(
                periods.OrderShipmentPeriod,
                "orderShipmentPeriod",
                utcNow
            );
            var sales = ValidatePeriod(periods.SalesPeriod, "salesPeriod", utcNow);
            return new WarehouseProductFlowAnalysisPeriodBounds
            {
                ContainerStart = container.StartDate,
                ContainerEnd = container.EndDate,
                OrderShipmentStart = orderShipment.StartDate,
                OrderShipmentEnd = orderShipment.EndDate,
                SalesStart = sales.StartDate,
                SalesEnd = sales.EndDate,
            };
        }

        private static (DateTime StartDate, DateTime EndDate) ValidatePeriod(
            WarehouseProductFlowDatePeriodDto? period,
            string periodName,
            DateTime utcNow
        )
        {
            if (
                period == null
                || period.StartDate == DateTime.MinValue
                || period.EndDate == DateTime.MinValue
            )
            {
                throw new WarehouseProductFlowAnalysisValidationException(
                    $"{periodName} 开始日期和结束日期不能为空。"
                );
            }

            try
            {
                return ValidateWarehouseProductFlowAnalysisDateRange(
                    period.StartDate,
                    period.EndDate,
                    utcNow
                );
            }
            catch (WarehouseProductFlowAnalysisValidationException ex)
            {
                throw new WarehouseProductFlowAnalysisValidationException($"{periodName}: {ex.Message}");
            }
        }

        private static (DateTime StartDate, DateTime EndDate) GetDailySeriesBounds(
            WarehouseProductFlowAnalysisPeriodBounds periods
        )
        {
            return (
                new[]
                {
                    periods.ContainerStart,
                    periods.OrderShipmentStart,
                    periods.SalesStart,
                }.Min(),
                new[]
                {
                    periods.ContainerEnd,
                    periods.OrderShipmentEnd,
                    periods.SalesEnd,
                }.Max()
            );
        }

        private static (DateTime StartDate, DateTime EndDate) GetOrderSalesSeriesBounds(
            WarehouseProductFlowAnalysisPeriodBounds periods
        )
        {
            return (
                new[] { periods.OrderShipmentStart, periods.SalesStart }.Min(),
                new[] { periods.OrderShipmentEnd, periods.SalesEnd }.Max()
            );
        }

        internal static (DateTime StartDate, DateTime EndDate) ValidateWarehouseProductFlowAnalysisDateRange(
            DateTime startDate,
            DateTime endDate,
            DateTime utcNow
        )
        {
            if (startDate == DateTime.MinValue || endDate == DateTime.MinValue)
                throw new WarehouseProductFlowAnalysisValidationException("开始日期和结束日期不能为空。");

            var start = startDate.Date;
            var end = endDate.Date;
            if (start > end)
                throw new WarehouseProductFlowAnalysisValidationException("开始日期不能晚于结束日期。");

            var brisbaneYesterday = utcNow.AddHours(10).Date.AddDays(-1);
            if (end > brisbaneYesterday)
                throw new WarehouseProductFlowAnalysisValidationException("结束日期不能晚于昨天。");

            var dayCount = (int)(end - start).TotalDays + 1;
            if (dayCount > 366)
                throw new WarehouseProductFlowAnalysisValidationException("日期范围不能超过 366 个自然日。");

            return (start, end);
        }

        internal static decimal? CalculateAverageUnitPrice(int quantity, decimal salesAmount)
        {
            return quantity == 0 ? null : salesAmount / quantity;
        }

        internal static decimal? CalculateSellThroughRate(decimal shippedQuantity, int netSalesQuantity)
        {
            return shippedQuantity <= 0 ? null : netSalesQuantity / shippedQuantity * 100m;
        }

        internal static List<string> NormalizeCodes(IEnumerable<string?>? codes)
        {
            return codes?
                    .Select(code => code?.Trim())
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                ?? new List<string>();
        }

        internal static List<List<string>> BatchCodes(IEnumerable<string?>? codes, int batchSize = CodeBatchSize)
        {
            var normalized = NormalizeCodes(codes);
            var batches = new List<List<string>>();
            for (var index = 0; index < normalized.Count; index += batchSize)
            {
                var count = Math.Min(batchSize, normalized.Count - index);
                batches.Add(normalized.GetRange(index, count));
            }
            return batches;
        }

        internal static List<string> ApplySelection(
            IEnumerable<string> codes,
            WarehouseProductFlowAnalysisSelectionDto? selection
        )
        {
            var mode = string.IsNullOrWhiteSpace(selection?.Mode) ? "allFiltered" : selection.Mode.Trim();
            if (string.Equals(mode, "included", StringComparison.OrdinalIgnoreCase))
            {
                var included = NormalizeCodes(selection?.IncludedProductCodes)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return codes.Where(code => included.Contains(code)).ToList();
            }

            var excluded = NormalizeCodes(selection?.ExcludedProductCodes)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return codes.Where(code => !excluded.Contains(code)).ToList();
        }

        internal static string ResolveCurrentProductCode(WarehouseProductFlowAnalysisRequest request)
        {
            var code = request?.CurrentProductCode?.Trim();
            if (string.IsNullOrWhiteSpace(code))
                throw new WarehouseProductFlowAnalysisValidationException("currentProductCode 不能为空。");
            return code!;
        }

        internal static string ResolveBranchCode(WarehouseProductFlowAnalysisRequest request)
        {
            var code = request?.BranchCode?.Trim();
            if (string.IsNullOrWhiteSpace(code))
                throw new WarehouseProductFlowAnalysisValidationException("branch-daily 需要 branchCode。");
            return code!;
        }

        internal static WarehouseProductFlowAnalysisPagedDto<WarehouseProductFlowProductDto> SortAndPageProducts(
            List<WarehouseProductFlowProductDto> rows,
            int pageNumber,
            int pageSize,
            string? sortBy,
            string? sortDirection
        )
        {
            var normalizedPageNumber = pageNumber <= 0 ? 1 : pageNumber;
            var normalizedPageSize = pageSize <= 0 ? 50 : Math.Min(pageSize, 500);
            var ascending = string.Equals(sortDirection, "asc", StringComparison.OrdinalIgnoreCase);
            var sortField = string.IsNullOrWhiteSpace(sortBy) ? "netSalesQuantity" : sortBy.Trim();

            IOrderedEnumerable<WarehouseProductFlowProductDto> ordered = sortField.ToLowerInvariant() switch
            {
                "inboundquantity" => ascending
                    ? rows.OrderBy(row => row.Metrics.InboundQuantity)
                    : rows.OrderByDescending(row => row.Metrics.InboundQuantity),
                "orderedquantity" => ascending
                    ? rows.OrderBy(row => row.Metrics.OrderedQuantity)
                    : rows.OrderByDescending(row => row.Metrics.OrderedQuantity),
                "shippedquantity" => ascending
                    ? rows.OrderBy(row => row.Metrics.ShippedQuantity)
                    : rows.OrderByDescending(row => row.Metrics.ShippedQuantity),
                "netsalesamount" => ascending
                    ? rows.OrderBy(row => row.Metrics.NetSalesAmount)
                    : rows.OrderByDescending(row => row.Metrics.NetSalesAmount),
                "averageunitprice" => ascending
                    ? rows.OrderBy(row => row.Metrics.AverageUnitPrice)
                    : rows.OrderByDescending(row => row.Metrics.AverageUnitPrice),
                "productcode" => ascending
                    ? rows.OrderBy(row => row.ProductCode, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(row => row.ProductCode, StringComparer.OrdinalIgnoreCase),
                "itemnumber" => ascending
                    ? rows.OrderBy(row => string.IsNullOrWhiteSpace(row.ItemNumber))
                        .ThenBy(row => row.ItemNumber, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderBy(row => string.IsNullOrWhiteSpace(row.ItemNumber))
                        .ThenByDescending(row => row.ItemNumber, StringComparer.OrdinalIgnoreCase),
                _ => ascending
                    ? rows.OrderBy(row => row.Metrics.NetSalesQuantity)
                    : rows.OrderByDescending(row => row.Metrics.NetSalesQuantity),
            };
            ordered = ordered.ThenBy(row => row.ProductCode, StringComparer.OrdinalIgnoreCase);

            var items = ordered
                .Skip((normalizedPageNumber - 1) * normalizedPageSize)
                .Take(normalizedPageSize)
                .ToList();
            return new WarehouseProductFlowAnalysisPagedDto<WarehouseProductFlowProductDto>
            {
                Items = items,
                Total = rows.Count,
                PageNumber = normalizedPageNumber,
                PageSize = normalizedPageSize,
            };
        }

        internal static WarehouseProductFlowAnalysisPagedDto<WarehouseProductFlowCandidateDto> SortAndPageCandidates(
            List<WarehouseProductFlowCandidateDto> rows,
            int pageNumber,
            int pageSize,
            string? sortBy,
            string? sortDirection
        )
        {
            var normalizedPageNumber = pageNumber <= 0 ? 1 : pageNumber;
            var normalizedPageSize = pageSize <= 0 ? 50 : Math.Min(pageSize, 500);
            var ascending = !string.Equals(sortDirection, "desc", StringComparison.OrdinalIgnoreCase);
            var sortField = string.IsNullOrWhiteSpace(sortBy) ? "itemnumber" : sortBy.Trim().ToLowerInvariant();

            IOrderedEnumerable<WarehouseProductFlowCandidateDto> ordered = sortField switch
            {
                "itemnumber" => ascending
                    ? rows
                        .OrderBy(row => string.IsNullOrWhiteSpace(row.ItemNumber))
                        .ThenBy(row => row.ItemNumber, StringComparer.OrdinalIgnoreCase)
                    : rows
                        .OrderBy(row => string.IsNullOrWhiteSpace(row.ItemNumber))
                        .ThenByDescending(row => row.ItemNumber, StringComparer.OrdinalIgnoreCase),
                "productcode" => ascending
                    ? rows.OrderBy(row => row.ProductCode, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(row => row.ProductCode, StringComparer.OrdinalIgnoreCase),
                "barcode" => ascending
                    ? rows.OrderBy(row => row.Barcode, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(row => row.Barcode, StringComparer.OrdinalIgnoreCase),
                "productname" => ascending
                    ? rows.OrderBy(row => row.ProductName, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(row => row.ProductName, StringComparer.OrdinalIgnoreCase),
                "englishname" => ascending
                    ? rows.OrderBy(row => row.EnglishName, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(row => row.EnglishName, StringComparer.OrdinalIgnoreCase),
                "suppliercode" => ascending
                    ? rows.OrderBy(row => row.SupplierCode, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(row => row.SupplierCode, StringComparer.OrdinalIgnoreCase),
                _ => ascending
                    ? rows
                        .OrderBy(row => string.IsNullOrWhiteSpace(row.ItemNumber))
                        .ThenBy(row => row.ItemNumber, StringComparer.OrdinalIgnoreCase)
                    : rows
                        .OrderBy(row => string.IsNullOrWhiteSpace(row.ItemNumber))
                        .ThenByDescending(row => row.ItemNumber, StringComparer.OrdinalIgnoreCase),
            };
            ordered = ordered.ThenBy(row => row.ProductCode, StringComparer.OrdinalIgnoreCase);

            var items = ordered
                .Skip((normalizedPageNumber - 1) * normalizedPageSize)
                .Take(normalizedPageSize)
                .ToList();
            return new WarehouseProductFlowAnalysisPagedDto<WarehouseProductFlowCandidateDto>
            {
                Items = items,
                Total = rows.Count,
                PageNumber = normalizedPageNumber,
                PageSize = normalizedPageSize,
            };
        }

        internal static List<WarehouseProductFlowDailyDto> BuildDailySeries(
            IEnumerable<WarehouseProductFlowAnalysisDailyAggregateRow> rows,
            DateTime startDate,
            DateTime endDate
        )
        {
            var byDate = rows
                .GroupBy(row => row.Date.Date)
                .ToDictionary(
                    group => group.Key,
                    group => new WarehouseProductFlowAnalysisDailyAggregateRow
                    {
                        Date = group.Key,
                        InboundQuantity = group.Sum(row => row.InboundQuantity),
                        OrderedQuantity = group.Sum(row => row.OrderedQuantity),
                        ShippedQuantity = group.Sum(row => row.ShippedQuantity),
                        NetSalesQuantity = group.Sum(row => row.NetSalesQuantity),
                        NetSalesAmount = group.Sum(row => row.NetSalesAmount),
                    }
                );

            var series = new List<WarehouseProductFlowDailyDto>();
            for (var date = startDate.Date; date <= endDate.Date; date = date.AddDays(1))
            {
                byDate.TryGetValue(date, out var row);
                series.Add(new WarehouseProductFlowDailyDto
                {
                    Date = date,
                    InboundQuantity = row?.InboundQuantity ?? 0m,
                    OrderedQuantity = row?.OrderedQuantity ?? 0m,
                    ShippedQuantity = row?.ShippedQuantity ?? 0m,
                    NetSalesQuantity = row?.NetSalesQuantity ?? 0,
                    NetSalesAmount = row?.NetSalesAmount ?? 0m,
                    AverageUnitPrice = CalculateAverageUnitPrice(
                        row?.NetSalesQuantity ?? 0,
                        row?.NetSalesAmount ?? 0m
                    ),
                });
            }
            return series;
        }

        private static List<WarehouseProductFlowAnalysisDailyAggregateRow> BuildDailyAggregates(
            IEnumerable<WarehouseProductFlowAnalysisSalesRow> salesRows,
            IEnumerable<WarehouseProductFlowAnalysisOrderRow> orderRows,
            IEnumerable<WarehouseProductFlowAnalysisShipmentRow> shipmentRows,
            IEnumerable<WarehouseProductFlowAnalysisContainerRow> containerRows
        )
        {
            var byDate = new Dictionary<DateTime, WarehouseProductFlowAnalysisDailyAggregateRow>();
            foreach (var group in salesRows.GroupBy(row => row.Date.Date))
            {
                var row = GetOrAddDailyAggregate(byDate, group.Key);
                row.NetSalesQuantity = group.Sum(item => item.TotalQuantity);
                row.NetSalesAmount = group.Sum(item => item.TotalAmount);
            }
            foreach (var group in orderRows.Where(row => row.OrderDate.HasValue).GroupBy(row => row.OrderDate!.Value.Date))
            {
                var row = GetOrAddDailyAggregate(byDate, group.Key);
                row.OrderedQuantity = group.Sum(item => item.Quantity);
            }
            foreach (var group in shipmentRows.Where(row => row.OutboundDate.HasValue).GroupBy(row => row.OutboundDate!.Value.Date))
            {
                var row = GetOrAddDailyAggregate(byDate, group.Key);
                row.ShippedQuantity = group.Sum(item => item.AllocQuantity);
            }
            foreach (var group in containerRows.Where(row => row.ArrivalDate.HasValue).GroupBy(row => row.ArrivalDate!.Value.Date))
            {
                var row = GetOrAddDailyAggregate(byDate, group.Key);
                row.InboundQuantity = group.Sum(item => item.LoadingQuantity);
            }
            return byDate.Values.ToList();
        }

        private static WarehouseProductFlowAnalysisDailyAggregateRow GetOrAddDailyAggregate(
            Dictionary<DateTime, WarehouseProductFlowAnalysisDailyAggregateRow> map,
            DateTime date
        )
        {
            if (!map.TryGetValue(date, out var row))
            {
                row = new WarehouseProductFlowAnalysisDailyAggregateRow { Date = date };
                map[date] = row;
            }
            return row;
        }

        private static List<WarehouseProductFlowProductDto> BuildProductDtos(
            WarehouseProductFlowAnalysisQueryContext context,
            List<string> codes,
            Dictionary<string, WarehouseProductFlowMetricsDto> metrics
        )
        {
            return codes
                .Select(code =>
                {
                    context.ProductByCode.TryGetValue(code, out var product);
                    context.DomesticByCode.TryGetValue(code, out var domestic);
                    var metric = metrics.GetValueOrDefault(code) ?? new WarehouseProductFlowMetricsDto();
                    var categoryName = product?.CategoryGuid != null
                        && context.CategoryNameByGuid.TryGetValue(product.CategoryGuid, out var category)
                            ? category
                            : null;
                    var supplierCode = domestic?.SupplierCode;
                    var supplierName = supplierCode != null
                        && context.ChinaNameByCode.TryGetValue(supplierCode, out var name)
                            ? name
                            : supplierCode;
                    return new WarehouseProductFlowProductDto
                    {
                        ProductCode = code,
                        ItemNumber = product?.ItemNumber,
                        Barcode = product?.Barcode,
                        ProductName = product?.ProductName,
                        EnglishName = product?.EnglishName,
                        ImageUrl = product?.ImageUrl,
                        CategoryGuid = product?.CategoryGuid,
                        CategoryName = categoryName,
                        SupplierCode = supplierCode,
                        SupplierName = supplierName,
                        Metrics = metric,
                    };
                })
                .ToList();
        }

        private static List<WarehouseProductFlowCandidateDto> BuildCandidateDtos(
            WarehouseProductFlowAnalysisQueryContext context,
            List<string> codes
        )
        {
            return codes
                .Select(code =>
                {
                    context.ProductByCode.TryGetValue(code, out var product);
                    context.DomesticByCode.TryGetValue(code, out var domestic);
                    var categoryName = product?.CategoryGuid != null
                        && context.CategoryNameByGuid.TryGetValue(product.CategoryGuid, out var category)
                            ? category
                            : null;
                    var supplierCode = domestic?.SupplierCode;
                    var supplierName = supplierCode != null
                        && context.ChinaNameByCode.TryGetValue(supplierCode, out var name)
                            ? name
                            : supplierCode;
                    return new WarehouseProductFlowCandidateDto
                    {
                        ProductCode = code,
                        ItemNumber = product?.ItemNumber,
                        Barcode = product?.Barcode,
                        ProductName = product?.ProductName,
                        EnglishName = product?.EnglishName,
                        ImageUrl = product?.ImageUrl,
                        CategoryGuid = product?.CategoryGuid,
                        CategoryName = categoryName,
                        SupplierCode = supplierCode,
                        SupplierName = supplierName,
                    };
                })
                .ToList();
        }

        private static WarehouseProductFlowMetricsDto SumMetrics(IEnumerable<WarehouseProductFlowProductDto> rows)
        {
            var list = rows.ToList();
            var netSalesQuantity = list.Sum(row => row.Metrics.NetSalesQuantity);
            var netSalesAmount = list.Sum(row => row.Metrics.NetSalesAmount);
            return new WarehouseProductFlowMetricsDto
            {
                InboundQuantity = list.Sum(row => row.Metrics.InboundQuantity),
                OrderedQuantity = list.Sum(row => row.Metrics.OrderedQuantity),
                ShippedQuantity = list.Sum(row => row.Metrics.ShippedQuantity),
                NetSalesQuantity = netSalesQuantity,
                NetSalesAmount = netSalesAmount,
                AverageUnitPrice = CalculateAverageUnitPrice(netSalesQuantity, netSalesAmount),
            };
        }

        private static bool MatchesKeyword(
            WarehouseProductFlowAnalysisQueryContext context,
            string code,
            string keyword
        )
        {
            if (ContainsIgnoreCase(code, keyword))
                return true;
            if (context.ProductByCode.TryGetValue(code, out var product))
            {
                if (ContainsIgnoreCase(product.ItemNumber, keyword)
                    || ContainsIgnoreCase(product.Barcode, keyword)
                    || ContainsIgnoreCase(product.ProductName, keyword)
                    || ContainsIgnoreCase(product.EnglishName, keyword))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool MatchesItemNumbers(
            WarehouseProductFlowAnalysisQueryContext context,
            string code,
            Dictionary<string, int> itemNumberRanks,
            bool matchAny
        )
        {
            context.ProductByCode.TryGetValue(code, out var product);
            return FindItemNumberRank(code, product?.ItemNumber, product?.Barcode, itemNumberRanks, matchAny) != null;
        }

        private static bool ContainsIgnoreCase(string? value, string keyword)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        }

        private static HashSet<string> ExpandCategoryGuids(
            IEnumerable<dynamic> categories,
            List<string> requestedGuids
        )
        {
            var children = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var allGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var category in categories)
            {
                var guid = (category.CategoryGUID as string)?.Trim();
                if (string.IsNullOrWhiteSpace(guid))
                    continue;
                allGuids.Add(guid);
                var parent = (category.ParentGUID as string)?.Trim();
                if (string.IsNullOrWhiteSpace(parent))
                    continue;
                if (!children.TryGetValue(parent, out var list))
                {
                    list = new List<string>();
                    children[parent] = list;
                }
                list.Add(guid);
            }

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>(requestedGuids.Where(guid => !string.IsNullOrWhiteSpace(guid)));
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!result.Add(current))
                    continue;
                if (children.TryGetValue(current, out var childGuids))
                {
                    foreach (var child in childGuids)
                        queue.Enqueue(child);
                }
            }
            return result;
        }

        private static string BuildOptionsCacheKey(
            WarehouseProductFlowAnalysisFilterDto _,
            List<string>? __
        )
        {
            // options 是固定主数据目录，兼容旧参数但不再随商品筛选或分店范围产生缓存碎片。
            return "wfpa:options:v2";
        }

        private static string BuildBranchScopeCachePart(List<string>? branchCodes)
        {
            if (branchCodes is null)
                return "<all>";

            var normalized = NormalizeCodes(branchCodes)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return normalized.Count == 0 ? "<none>" : string.Join(",", normalized);
        }

        private static string BuildCacheKey(
            string segment,
            WarehouseProductFlowAnalysisRequest request,
            List<string>? branchCodes,
            params (DateTime StartDate, DateTime EndDate)[] ranges
        )
        {
            var filter = request.Filter ?? new WarehouseProductFlowAnalysisFilterDto();
            var selection = request.Selection ?? new WarehouseProductFlowAnalysisSelectionDto();
            var rangePart = string.Join(
                "~",
                ranges.Select(range =>
                    $"{range.StartDate:yyyyMMdd}:{range.EndDate:yyyyMMdd}"
                )
            );
            return JsonSerializer.Serialize(new object?[]
            {
                "wfpa",
                segment,
                rangePart,
                filter.Keyword?.Trim() ?? string.Empty,
                NormalizeCodes(filter.WarehouseCategoryGuids).OrderBy(code => code, StringComparer.OrdinalIgnoreCase).ToArray(),
                NormalizeCodes(filter.SupplierCodes).OrderBy(code => code, StringComparer.OrdinalIgnoreCase).ToArray(),
                filter.DocumentKeyword?.Trim() ?? string.Empty,
                NormalizeItemNumbers(filter.ItemNumbers).ToArray(),
                IsAnyItemNumberMatchMode(filter.ItemNumberMatchMode) ? "any" : "itemNumber",
                selection.Mode?.Trim() ?? string.Empty,
                NormalizeCodes(selection.IncludedProductCodes).OrderBy(code => code, StringComparer.OrdinalIgnoreCase).ToArray(),
                NormalizeCodes(selection.ExcludedProductCodes).OrderBy(code => code, StringComparer.OrdinalIgnoreCase).ToArray(),
                request.CurrentProductCode?.Trim() ?? string.Empty,
                request.BranchCode?.Trim() ?? string.Empty,
                request.PageNumber.ToString(),
                request.PageSize.ToString(),
                request.SortBy?.Trim() ?? string.Empty,
                request.SortDirection?.Trim() ?? string.Empty,
                BuildBranchScopeCachePart(branchCodes)
            });
        }

        private static string BuildCandidateCacheKey(WarehouseProductFlowCandidateRequest request)
        {
            var filter = request.Filter ?? new WarehouseProductFlowAnalysisFilterDto();
            var pageNumber = request.PageNumber <= 0 ? 1 : request.PageNumber;
            var pageSize = request.PageSize <= 0 ? 50 : Math.Min(request.PageSize, 500);
            var requestedSortField = request.SortBy?.Trim().ToLowerInvariant();
            var sortField = requestedSortField is "productcode" or "barcode" or "productname" or "englishname" or "suppliercode" or "itemnumber"
                ? requestedSortField
                : "itemnumber";
            var sortDirection = string.Equals(
                request.SortDirection,
                "desc",
                StringComparison.OrdinalIgnoreCase
            ) ? "desc" : "asc";
            return JsonSerializer.Serialize(new object?[]
            {
                "wfpa",
                "candidates",
                filter.Keyword?.Trim() ?? string.Empty,
                NormalizeCodes(filter.WarehouseCategoryGuids).OrderBy(code => code, StringComparer.OrdinalIgnoreCase).ToArray(),
                NormalizeCodes(filter.SupplierCodes).OrderBy(code => code, StringComparer.OrdinalIgnoreCase).ToArray(),
                filter.DocumentKeyword?.Trim() ?? string.Empty,
                NormalizeItemNumbers(filter.ItemNumbers).ToArray(),
                IsAnyItemNumberMatchMode(filter.ItemNumberMatchMode) ? "any" : "itemNumber",
                pageNumber.ToString(),
                pageSize.ToString(),
                sortField,
                sortDirection
            });
        }
    }
}
