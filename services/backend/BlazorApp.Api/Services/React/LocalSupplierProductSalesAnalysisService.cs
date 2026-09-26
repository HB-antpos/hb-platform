using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// 商品主档本地供应商信息（用于候选/选项/汇总与关键字/分类/供应商过滤）。
    /// </summary>
    internal sealed class LocalSupplierProductSalesMasterInfo
    {
        public string ProductCode { get; set; } = string.Empty;
        public string UUID { get; set; } = string.Empty;
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
        public string? ProductName { get; set; }
        public string? EnglishName { get; set; }
        public string? ImageUrl { get; set; }
        public string? WarehouseCategoryGuid { get; set; }
        public string? LocalSupplierCode { get; set; }
    }

    /// <summary>本地商品分析的小规模元数据上下文。</summary>
    internal sealed class LocalSupplierProductSalesMasterContext
    {
        public List<WarehouseCategory> Categories { get; set; } = new();
        public Dictionary<string, string> SupplierNameByCode { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> StoreNameByCode { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> CategoryNameByGuid { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public bool SupplierMetadataFailed { get; set; }
        public bool StoreMetadataFailed { get; set; }
        public bool HasMetadataFailures => SupplierMetadataFailed || StoreMetadataFailed;
    }

    internal sealed class LocalSupplierProductSalesPurchaseAggregateRow
    {
        public string ProductCode { get; set; } = string.Empty;
        public decimal? Quantity { get; set; }
        public decimal? Amount { get; set; }
    }

    internal sealed class LocalSupplierProductSalesSalesAggregateRow
    {
        public string ProductCode { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal Amount { get; set; }
    }

    internal sealed class LocalSupplierProductSalesPurchaseDailyRow
    {
        public DateTime? Date { get; set; }
        public decimal? PurchaseQuantity { get; set; }
        public decimal? PurchaseAmount { get; set; }
    }

    internal sealed class LocalSupplierProductSalesSalesDailyRow
    {
        public DateTime Date { get; set; }
        public decimal NetSalesQuantity { get; set; }
        public decimal NetSalesAmount { get; set; }
    }

    internal sealed class LocalSupplierProductSalesBranchAggregateRow
    {
        public string BranchCode { get; set; } = string.Empty;
        public decimal NetSalesQuantity { get; set; }
        public decimal NetSalesAmount { get; set; }
    }

    internal sealed class LocalSupplierProductSalesDetailRow
    {
        public string DetailGUID { get; set; } = string.Empty;
        public string InvoiceGUID { get; set; } = string.Empty;
        public string? InvoiceNo { get; set; }
        public string? StoreCode { get; set; }
        public string? SupplierCode { get; set; }
        public DateTime? PurchaseDate { get; set; }
        public string ProductCode { get; set; } = string.Empty;
        public string? ProductName { get; set; }
        public decimal? Quantity { get; set; }
        public decimal? PurchasePrice { get; set; }
        public decimal? Amount { get; set; }
    }

    internal sealed class LocalSupplierProductSalesInvoiceHeaderRow
    {
        public string InvoiceGUID { get; set; } = string.Empty;
        public string? InvoiceNo { get; set; }
        public string? Remarks { get; set; }
        public string? StoreCode { get; set; }
        public string? SupplierCode { get; set; }
        public DateTime EffectivePurchaseDate { get; set; }
    }

    internal sealed class LocalSupplierProductSalesProductCodeRow
    {
        public string ProductCode { get; set; } = string.Empty;
    }

    internal sealed class LocalSupplierProductSalesSummarySqlRow
    {
        public string UUID { get; set; } = string.Empty;
        public string ProductCode { get; set; } = string.Empty;
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
        public string? ProductName { get; set; }
        public string? ImageUrl { get; set; }
        public string? WarehouseCategoryGuid { get; set; }
        public string? LocalSupplierCode { get; set; }
        public decimal PurchaseQuantity { get; set; }
        public decimal PurchaseAmount { get; set; }
        public decimal NetSalesQuantity { get; set; }
        public decimal NetSalesAmount { get; set; }
    }

    internal sealed class LocalSupplierProductSalesSummaryTotalsSqlRow
    {
        public int Total { get; set; }
        public decimal PurchaseQuantity { get; set; }
        public decimal PurchaseAmount { get; set; }
        public decimal NetSalesQuantity { get; set; }
        public decimal NetSalesAmount { get; set; }
    }

    internal sealed class LocalSupplierProductSalesSelectionState
    {
        public LocalSupplierProductSalesEffectiveSelectionDto Selection { get; set; } = new();
        public string? CurrentProductCode { get; set; }
    }

    internal sealed class LocalSupplierProductSalesProductKeyHealth
    {
        public int NoDuplicates { get; set; }
        public int HasDirectProductSchema { get; set; }
        public int HasFastSchema { get; set; }
        public int EligibleCount { get; set; }
    }

    internal sealed class LocalSupplierProductSalesCurrentBundle
    {
        public LocalSupplierProductSalesInvoiceDetailPageDto InvoiceDetails { get; set; } = new();
        public List<LocalSupplierProductSalesDailyDto> ProductDaily { get; set; } = new();
        public List<LocalSupplierProductSalesBranchDto> Branches { get; set; } = new();
    }

    /// <summary>
    /// 同一个 IMemoryCache（即同一个 API 进程）内共享的短生命周期协调器。
    /// 服务本身是 Scoped，因此不能把代际和 single-flight 放在服务实例字段里。
    /// </summary>
    internal sealed class LocalSupplierProductSalesCacheCoordinator
    {
        public object Gate { get; } = new();
        public Dictionary<string, long> Generations { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, object> Inflight { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// 澳洲本地商品销量分析服务。
    /// 候选/选项来自商品主档（不访问进货表），统计与明细在数据库端聚合/分页，
    /// 避免商品 × 明细 × 销量扇出。成功结果按授权范围代际缓存 60 秒。
    /// </summary>
    public class LocalSupplierProductSalesAnalysisService : ILocalSupplierProductSalesAnalysisService
    {
        private static readonly TimeSpan SuccessCacheDuration = TimeSpan.FromSeconds(60);
        private const string ProductKeyHealthCacheKey = "lspa:product-key-health:v2";

        private readonly ISqlSugarClient _db;
        private readonly IMemoryCache _cache;
        private readonly ILogger<LocalSupplierProductSalesAnalysisService> _logger;
        private static readonly ConditionalWeakTable<
            IMemoryCache,
            LocalSupplierProductSalesCacheCoordinator
        > CacheCoordinators = new();

        private readonly LocalSupplierProductSalesCacheCoordinator _cacheCoordinator;
        private bool _useDirectUniqueProductPath;
        private bool _hasFastSqlServerSchema;
        private int _directEligibleProductCount;

        public LocalSupplierProductSalesAnalysisService(
            SqlSugarContext context,
            IMemoryCache cache,
            ILogger<LocalSupplierProductSalesAnalysisService> logger
        )
            : this(context.Db, cache, logger) { }

        private LocalSupplierProductSalesAnalysisService(
            ISqlSugarClient db,
            IMemoryCache cache,
            ILogger<LocalSupplierProductSalesAnalysisService> logger
        )
        {
            _db = db;
            _cache = cache;
            _logger = logger;
            _cacheCoordinator = CacheCoordinators.GetValue(
                cache,
                _ => new LocalSupplierProductSalesCacheCoordinator()
            );
        }

        public async Task<ApiResponse<LocalSupplierProductSalesOptionsDto>> GetOptionsAsync(
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            var scopeKey = BuildStoreScopeCachePart(scopedStoreCodes);
            var generation = GetGeneration(scopeKey, forceRefresh: false);
            var cacheKey = $"lspa:options:{generation}:{HashParts(scopeKey)}";
            return await ComputeWithCacheAsync(
                cacheKey,
                scopeKey,
                generation,
                forceRefresh: false,
                () => ComputeOptionsCoreAsync(scopedStoreCodes)
            );
        }

        public async Task<ApiResponse<List<LocalSupplierProductSalesSupplierCategoryOptionDto>>> GetSupplierCategoryOptionsAsync(
            IReadOnlyList<string>? supplierCodes,
            IReadOnlyList<string>? scopedStoreCodes,
            bool tree = false
        )
        {
            if (IsEmptyScope(scopedStoreCodes))
            {
                return ApiResponse<List<LocalSupplierProductSalesSupplierCategoryOptionDto>>.OK(new());
            }

            var selected = LocalSupplierProductSalesAnalysisLogic.NormalizeCodes(supplierCodes);
            if (selected.Count > LocalSupplierProductSalesAnalysisLogic.MaxFilterListCount)
            {
                return ApiResponse<List<LocalSupplierProductSalesSupplierCategoryOptionDto>>.Error("供应商数量不能超过 100 个", "VALIDATION_ERROR");
            }

            // 默认保持旧版叶节点平铺契约；新树形消费者必须显式传 tree=true。
            var categoriesQuery = _db.Queryable<LocalSupplierCategory>()
                .Where(category => category.IsDeleted == false && category.IsActive == true
                    && category.LocalSupplierCode != "200");
            if (selected.Count > 0)
            {
                categoriesQuery = categoriesQuery.Where(category => selected.Contains(category.LocalSupplierCode));
            }
            var categories = await categoriesQuery.ToListAsync();
            var localRows = categories.Select(category => new SupplierCategoryTreeRow
                {
                    SupplierCode = category.LocalSupplierCode,
                    Guid = category.CategoryGUID,
                    ParentGuid = category.ParentGUID,
                    Name = string.IsNullOrWhiteSpace(category.CategoryName) ? category.CategoryGUID : category.CategoryName,
                    FlatName = string.IsNullOrWhiteSpace(category.FullPath) ? category.CategoryName : category.FullPath,
                }).ToList();
            var result = tree ? BuildSupplierCategoryTree(localRows) : BuildFlatLeafOptions(localRows);

            if (selected.Count == 0 || selected.Contains("200", StringComparer.OrdinalIgnoreCase))
            {
                var warehouseCategories = await _db.Queryable<WarehouseCategory>()
                    .Where(category => category.IsDeleted == false && category.IsActive == true)
                    .ToListAsync();
                var warehouseRows = warehouseCategories.Select(category => new SupplierCategoryTreeRow
                    {
                        SupplierCode = "200",
                        Guid = category.CategoryGUID,
                        ParentGuid = category.ParentGUID,
                        Name = category.ChineseName ?? category.CategoryName ?? category.CategoryGUID,
                        FlatName = category.ChineseName ?? category.CategoryName ?? category.CategoryGUID,
                    }).ToList();
                result.AddRange(tree ? BuildSupplierCategoryTree(warehouseRows) : BuildFlatLeafOptions(warehouseRows));
            }

            return ApiResponse<List<LocalSupplierProductSalesSupplierCategoryOptionDto>>.OK(
                result.OrderBy(item => item.SupplierCode, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList()
            );
        }

        private sealed class SupplierCategoryTreeRow
        {
            public string SupplierCode { get; init; } = string.Empty;
            public string Guid { get; init; } = string.Empty;
            public string? ParentGuid { get; init; }
            public string Name { get; init; } = string.Empty;
            public string FlatName { get; init; } = string.Empty;
        }

        private static List<LocalSupplierProductSalesSupplierCategoryOptionDto> BuildFlatLeafOptions(
            IReadOnlyList<SupplierCategoryTreeRow> rows
        )
        {
            var parentGuids = rows.Where(row => !string.IsNullOrWhiteSpace(row.ParentGuid))
                .Select(row => row.ParentGuid!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return rows.Where(row => !parentGuids.Contains(row.Guid))
                .Select(row => new LocalSupplierProductSalesSupplierCategoryOptionDto
                {
                    SupplierCode = row.SupplierCode,
                    Guid = row.Guid,
                    Name = row.FlatName,
                    IsSelectable = true,
                }).ToList();
        }

        private static List<LocalSupplierProductSalesSupplierCategoryOptionDto> BuildSupplierCategoryTree(
            IEnumerable<SupplierCategoryTreeRow> rows
        )
        {
            // 先物化并按「供应商 + GUID」去重：不同供应商的数据不应因 GUID 恰好相同而串树，
            // 后续建边也使用同一复合键，避免重复枚举输入或重复挂载节点。
            var distinctRows = rows
                .Where(row => !string.IsNullOrWhiteSpace(row.Guid))
                .GroupBy(row => row.SupplierCode + "|" + row.Guid, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            var nodes = distinctRows
                .ToDictionary(
                    row => row.SupplierCode + "|" + row.Guid,
                    row => new LocalSupplierProductSalesSupplierCategoryOptionDto
                    {
                        SupplierCode = row.SupplierCode,
                        Guid = row.Guid,
                        ParentGuid = row.ParentGuid,
                        Name = row.Name,
                    },
                    StringComparer.OrdinalIgnoreCase
                );

            foreach (var node in nodes.Values)
            {
                node.IsSelectable = true;
            }

            var roots = new List<LocalSupplierProductSalesSupplierCategoryOptionDto>();
            var parentByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in distinctRows)
            {
                var nodeKey = row.SupplierCode + "|" + row.Guid;
                var parentKey = row.SupplierCode + "|" + row.ParentGuid;
                var createsCycle = false;
                var cursor = parentKey;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (parentByKey.TryGetValue(cursor, out var ancestor) && seen.Add(cursor))
                {
                    if (string.Equals(ancestor, nodeKey, StringComparison.OrdinalIgnoreCase))
                    {
                        createsCycle = true;
                        break;
                    }
                    cursor = ancestor;
                }
                var node = nodes[nodeKey];
                if (!string.IsNullOrWhiteSpace(row.ParentGuid)
                    && nodes.TryGetValue(parentKey, out var parent)
                    && !string.Equals(row.ParentGuid, row.Guid, StringComparison.OrdinalIgnoreCase)
                    && !createsCycle)
                {
                    parent.Children.Add(node);
                    parent.IsSelectable = false;
                    parentByKey[nodeKey] = parentKey;
                }
                else
                {
                    roots.Add(node);
                }
            }

            // Parent nodes remain non-selectable; orphaned parents are roots so no active category disappears.
            void Sort(List<LocalSupplierProductSalesSupplierCategoryOptionDto> items)
            {
                items.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));
                foreach (var item in items)
                {
                    if (item.Children.Count > 0)
                    {
                        item.IsSelectable = false;
                        Sort(item.Children);
                    }
                }
            }
            Sort(roots);
            return roots;
        }

        public async Task<
            ApiResponse<
                LocalSupplierProductSalesPagedDto<LocalSupplierProductSalesCandidateDto>
            >
        > GetCandidatesAsync(
            LocalSupplierProductSalesAnalysisRequest request,
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            var scopeKey = BuildStoreScopeCachePart(scopedStoreCodes);
            var generation = GetGeneration(scopeKey, request.ForceRefresh);
            var cacheKey = BuildCacheKey("candidates", request, scopedStoreCodes, generation);
            return await ComputeWithCacheAsync(
                cacheKey,
                scopeKey,
                generation,
                request.ForceRefresh,
                () => ComputeCandidatesCoreAsync(request, scopedStoreCodes)
            );
        }

        public async Task<ApiResponse<LocalSupplierProductSalesSummaryResponseDto>> GetSummaryAsync(
            LocalSupplierProductSalesAnalysisRequest request,
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            var scopeKey = BuildStoreScopeCachePart(scopedStoreCodes);
            var generation = GetGeneration(scopeKey, request.ForceRefresh);
            var cacheKey = BuildCacheKey("summary", request, scopedStoreCodes, generation);
            return await ComputeWithCacheAsync(
                cacheKey,
                scopeKey,
                generation,
                request.ForceRefresh,
                () => ComputeSummaryCoreAsync(request, scopedStoreCodes)
            );
        }

        public async Task<ApiResponse<List<LocalSupplierProductSalesDailyDto>>> GetProductDailyAsync(
            LocalSupplierProductSalesAnalysisRequest request,
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            var scopeKey = BuildStoreScopeCachePart(scopedStoreCodes);
            var generation = GetGeneration(scopeKey, request.ForceRefresh);
            var cacheKey = BuildCacheKey("product-daily", request, scopedStoreCodes, generation);
            return await ComputeWithCacheAsync(
                cacheKey,
                scopeKey,
                generation,
                request.ForceRefresh,
                () => ComputeProductDailyCoreAsync(request, scopedStoreCodes)
            );
        }

        public async Task<ApiResponse<LocalSupplierProductSalesInvoiceDetailPageDto>>
            GetInvoiceDetailsAsync(
                LocalSupplierProductSalesAnalysisRequest request,
                IReadOnlyList<string>? scopedStoreCodes
            )
        {
            var scopeKey = BuildStoreScopeCachePart(scopedStoreCodes);
            var generation = GetGeneration(scopeKey, request.ForceRefresh);
            var cacheKey = BuildCacheKey("invoice-details", request, scopedStoreCodes, generation);
            return await ComputeWithCacheAsync(
                cacheKey,
                scopeKey,
                generation,
                request.ForceRefresh,
                () => ComputeInvoiceDetailsCoreAsync(request, scopedStoreCodes)
            );
        }

        public async Task<ApiResponse<List<LocalSupplierProductSalesBranchDto>>> GetBranchesAsync(
            LocalSupplierProductSalesAnalysisRequest request,
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            var scopeKey = BuildStoreScopeCachePart(scopedStoreCodes);
            var generation = GetGeneration(scopeKey, request.ForceRefresh);
            var cacheKey = BuildCacheKey("branches", request, scopedStoreCodes, generation);
            return await ComputeWithCacheAsync(
                cacheKey,
                scopeKey,
                generation,
                request.ForceRefresh,
                () => ComputeBranchesCoreAsync(request, scopedStoreCodes)
            );
        }

        public async Task<ApiResponse<List<LocalSupplierProductSalesBranchDailyDto>>>
            GetBranchDailyAsync(
                LocalSupplierProductSalesAnalysisRequest request,
                IReadOnlyList<string>? scopedStoreCodes
            )
        {
            var scopeKey = BuildStoreScopeCachePart(scopedStoreCodes);
            var generation = GetGeneration(scopeKey, request.ForceRefresh);
            var cacheKey = BuildCacheKey("branch-daily", request, scopedStoreCodes, generation);
            return await ComputeWithCacheAsync(
                cacheKey,
                scopeKey,
                generation,
                request.ForceRefresh,
                () => ComputeBranchDailyCoreAsync(request, scopedStoreCodes)
            );
        }

        public async Task<ApiResponse<LocalSupplierProductSalesBootstrapResponseDto>> BootstrapAsync(
            LocalSupplierProductSalesAnalysisRequest request,
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            if (IsEmptyScope(scopedStoreCodes))
            {
                return ApiResponse<LocalSupplierProductSalesBootstrapResponseDto>.OK(
                    new LocalSupplierProductSalesBootstrapResponseDto()
                );
            }

            var scopeKey = BuildStoreScopeCachePart(scopedStoreCodes);
            var generation = GetGeneration(scopeKey, request.ForceRefresh);
            var cacheKey = BuildCacheKey("bootstrap", request, scopedStoreCodes, generation);
            return await ComputeWithCacheAsync(
                cacheKey,
                scopeKey,
                generation,
                request.ForceRefresh,
                () => ComputeBootstrapCoreAsync(request, scopedStoreCodes),
                cacheable: data => !data.Partial
            );
        }

        private async Task<ApiResponse<T>> ComputeWithCacheAsync<T>(
            string cacheKey,
            string scopeKey,
            long generation,
            bool forceRefresh,
            Func<Task<T>> compute,
            Func<T, bool>? cacheable = null
        )
        {
            Lazy<Task<ApiResponse<T>>> lazy;
            lock (_cacheCoordinator.Gate)
            {
                if (
                    !forceRefresh
                    && _cache.TryGetValue<ApiResponse<T>>(cacheKey, out var cached)
                    && cached is not null
                )
                {
                    return cached;
                }

                if (_cacheCoordinator.Inflight.TryGetValue(cacheKey, out var existing))
                {
                    lazy = (Lazy<Task<ApiResponse<T>>>)existing;
                }
                else
                {
                    // 共享计算不绑定任一 HTTP 请求取消令牌；单个客户端取消不会拖垮其他等待者。
                    lazy = new Lazy<Task<ApiResponse<T>>>(() => ExecuteAsync(cacheKey, compute));
                    _cacheCoordinator.Inflight[cacheKey] = lazy;
                }
            }

            try
            {
                var response = await lazy.Value;
                if (
                    response.Success
                    && response.Data is not null
                    && (cacheable?.Invoke(response.Data) ?? true)
                )
                {
                    lock (_cacheCoordinator.Gate)
                    {
                        if (GetGenerationInsideLock(scopeKey) == generation)
                        {
                            _cache.Set(
                                cacheKey,
                                response,
                                new MemoryCacheEntryOptions
                                {
                                    AbsoluteExpirationRelativeToNow = SuccessCacheDuration,
                                }
                            );
                        }
                    }
                }

                return response;
            }
            finally
            {
                lock (_cacheCoordinator.Gate)
                {
                    if (
                        _cacheCoordinator.Inflight.TryGetValue(cacheKey, out var current)
                        && ReferenceEquals(current, lazy)
                    )
                    {
                        _cacheCoordinator.Inflight.Remove(cacheKey);
                    }
                }
            }
        }

        private async Task<ApiResponse<T>> ExecuteAsync<T>(
            string cacheKey,
            Func<Task<T>> compute
        )
        {
            T data;
            try
            {
                data = await compute();
            }
            catch (LocalSupplierProductSalesAnalysisValidationException ex)
            {
                return ApiResponse<T>.Error(ex.Message, "VALIDATION_ERROR");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "本地商品销量分析查询失败 CacheKey={CacheKey}", cacheKey);
                return ApiResponse<T>.Error("本地商品销量分析查询失败", "QUERY_ERROR");
            }

            return ApiResponse<T>.OK(data);
        }

        private async Task<LocalSupplierProductSalesBootstrapResponseDto> ComputeBootstrapCoreAsync(
            LocalSupplierProductSalesAnalysisRequest request,
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            await EnsureProductKeyHealthAsync();
            var (startDate, endDate) = LocalSupplierProductSalesAnalysisLogic.ValidateDateRange(
                request.Filter.StartDate,
                request.Filter.EndDate
            );

            var totalStopwatch = Stopwatch.StartNew();
            var timings = new Dictionary<string, double>();
            var sectionErrors = new Dictionary<string, string>();
            var sectionStateGate = new object();
            var partial = false;

            async Task<T> Section<T>(
                string name,
                string errorMessage,
                T fallback,
                Func<Task<T>> compute
            )
            {
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    return await compute();
                }
                catch (Exception ex)
                {
                    lock (sectionStateGate)
                    {
                        partial = true;
                        sectionErrors[name] = errorMessage;
                    }
                    _logger.LogError(
                        ex,
                        "本地商品销量分析 bootstrap 分段失败 Section={Section}",
                        name
                    );
                    return fallback;
                }
                finally
                {
                    stopwatch.Stop();
                    lock (sectionStateGate)
                    {
                        timings[name] = stopwatch.Elapsed.TotalMilliseconds;
                    }
                }
            }

            // 元数据、候选和选择解析决定整个页面的数据边界，失败时返回整体错误。
            var context = await LoadMasterContextAsync(scopedStoreCodes);
            var filterStopwatch = Stopwatch.StartNew();
            var filteredProducts = BuildFilteredProductQuery(
                context,
                request.Filter,
                scopedStoreCodes
            );
            filterStopwatch.Stop();
            timings["filter"] = filterStopwatch.Elapsed.TotalMilliseconds;

            var effectiveCandidatePage = request.CandidatePageNumber > 0
                ? request.CandidatePageNumber
                : request.PageNumber;
            var canUseFirstCandidate = request.AutoSelectFirst
                && string.IsNullOrWhiteSpace(request.CurrentProductCode)
                && effectiveCandidatePage <= 1
                && string.Equals(
                    request.Selection.Mode,
                    "allFiltered",
                    StringComparison.OrdinalIgnoreCase
                )
                && LocalSupplierProductSalesAnalysisLogic.NormalizeCodes(
                    request.Selection.ExcludedProductCodes
                ).Count == 0;
            var firstCandidateSelection = new LocalSupplierProductSalesEffectiveSelectionDto
            {
                Mode = "allFiltered",
                ExcludedProductCodes = new List<string>(),
            };
            Task<LocalSupplierProductSalesOptionsDto>? preloadedOptionsTask = null;
            Task<LocalSupplierProductSalesSummaryResponseDto>? preloadedSummaryTask = null;

            if (
                _db.CurrentConnectionConfig.DbType == DbType.SqlServer
                && canUseFirstCandidate
            )
            {
                // 默认首屏的汇总与候选互不依赖，提前并发以隐藏候选主档查询耗时。
                var preloadedOptionsWorker = CreateParallelWorker();
                var preloadedSummaryWorker = CreateParallelWorker();
                preloadedOptionsTask = Section(
                    "options",
                    "筛选选项加载失败，请重试。",
                    new LocalSupplierProductSalesOptionsDto(),
                    () => preloadedOptionsWorker.BuildOptionsAsync(context)
                );
                preloadedSummaryTask = Section(
                    "summary",
                    "汇总加载失败，请重试。",
                    new LocalSupplierProductSalesSummaryResponseDto(),
                    () =>
                    {
                        var workerFilteredProducts =
                            preloadedSummaryWorker.BuildFilteredProductQuery(
                                context,
                                request.Filter,
                                scopedStoreCodes
                            );
                        var workerSelectedProducts = ApplySelectionQuery(
                            workerFilteredProducts,
                            firstCandidateSelection
                        );
                        return preloadedSummaryWorker.ComputeSummaryFromQueryAsync(
                            context,
                            request,
                            scopedStoreCodes,
                            startDate,
                            endDate,
                            workerSelectedProducts
                        );
                    }
                );
            }

            var candidateStopwatch = Stopwatch.StartNew();
            var candidates = await QueryCandidatesAsync(filteredProducts, context, request);
            candidateStopwatch.Stop();
            timings["candidates"] = candidateStopwatch.Elapsed.TotalMilliseconds;

            var selectionStopwatch = Stopwatch.StartNew();
            var outcome = canUseFirstCandidate
                ? new LocalSupplierProductSalesSelectionState
                {
                    Selection = firstCandidateSelection,
                    CurrentProductCode = candidates.Items.FirstOrDefault()?.ProductCode,
                }
                : await ResolveSelectionAsync(
                    filteredProducts,
                    request.Selection,
                    request.CurrentProductCode,
                    request.AutoSelectFirst
                );
            var selectedProducts = ApplySelectionQuery(filteredProducts, outcome.Selection);
            var currentProduct = canUseFirstCandidate
                ? candidates.Items.FirstOrDefault()
                : outcome.CurrentProductCode == null
                    ? null
                    : await QueryCandidateAsync(
                        selectedProducts,
                        context,
                        outcome.CurrentProductCode
                    );
            selectionStopwatch.Stop();
            timings["selection"] = selectionStopwatch.Elapsed.TotalMilliseconds;

            LocalSupplierProductSalesOptionsDto options;
            LocalSupplierProductSalesSummaryResponseDto summary;
            var invoiceDetails = new LocalSupplierProductSalesInvoiceDetailPageDto();
            var productDaily = new List<LocalSupplierProductSalesDailyDto>();
            var branches = new List<LocalSupplierProductSalesBranchDto>();
            if (_db.CurrentConnectionConfig.DbType == DbType.SqlServer)
            {
                // SQL Server 下各段使用独立连接并行执行；避免共用连接的线程安全问题。
                var optionsWorker = CreateParallelWorker();
                var summaryWorker = CreateParallelWorker();
                var currentWorker = CreateParallelWorker();
                var detailsWorker = CreateParallelWorker();
                var dailyWorker = CreateParallelWorker();
                var branchesWorker = CreateParallelWorker();
                var optionsTask = preloadedOptionsTask
                    ?? Section(
                        "options",
                        "筛选选项加载失败，请重试。",
                        new LocalSupplierProductSalesOptionsDto(),
                        () => optionsWorker.BuildOptionsAsync(context)
                    );
                var summaryTask = preloadedSummaryTask
                    ?? Section(
                        "summary",
                        "汇总加载失败，请重试。",
                        new LocalSupplierProductSalesSummaryResponseDto(),
                        () =>
                        {
                            var workerFilteredProducts = summaryWorker.BuildFilteredProductQuery(
                                context,
                                request.Filter,
                                scopedStoreCodes
                            );
                            var workerSelectedProducts = ApplySelectionQuery(
                                workerFilteredProducts,
                                outcome.Selection
                            );
                            return summaryWorker.ComputeSummaryFromQueryAsync(
                                context,
                                request,
                                scopedStoreCodes,
                                startDate,
                                endDate,
                                workerSelectedProducts
                            );
                        }
                    );
                async Task<LocalSupplierProductSalesCurrentBundle>
                    ComputeCurrentBundleAfterOptionsAsync()
                {
                    // 汇总会使用日期覆盖索引聚合；先让较短的选项查询完成，避免三个 SQL 段同时争用
                    // 四核数据库。当前商品查询仍与汇总尾段并行，不增加首屏瀑布。
                    await optionsTask;
                    return await currentWorker.ComputeFastSqlServerCurrentBundleAsync(
                        context,
                        request,
                        scopedStoreCodes,
                        startDate,
                        endDate,
                        outcome.CurrentProductCode!
                    );
                }

                var currentBundleTask = outcome.CurrentProductCode != null
                    && _hasFastSqlServerSchema
                    ? ComputeCurrentBundleAfterOptionsAsync()
                    : null;
                Task<LocalSupplierProductSalesInvoiceDetailPageDto> detailsTask;
                Task<List<LocalSupplierProductSalesDailyDto>> dailyTask;
                Task<List<LocalSupplierProductSalesBranchDto>> branchesTask;
                if (outcome.CurrentProductCode == null)
                {
                    detailsTask = Task.FromResult(
                        new LocalSupplierProductSalesInvoiceDetailPageDto()
                    );
                    dailyTask = Task.FromResult(new List<LocalSupplierProductSalesDailyDto>());
                    branchesTask = Task.FromResult(new List<LocalSupplierProductSalesBranchDto>());
                }
                else if (currentBundleTask is not null)
                {
                    detailsTask = Section(
                        "invoiceDetails",
                        "进货明细加载失败，请重试。",
                        new LocalSupplierProductSalesInvoiceDetailPageDto(),
                        async () => (await currentBundleTask).InvoiceDetails
                    );
                    dailyTask = Section(
                        "productDaily",
                        "商品趋势加载失败，请重试。",
                        new List<LocalSupplierProductSalesDailyDto>(),
                        async () => (await currentBundleTask).ProductDaily
                    );
                    branchesTask = Section(
                        "branches",
                        "分店排行加载失败，请重试。",
                        new List<LocalSupplierProductSalesBranchDto>(),
                        async () => (await currentBundleTask).Branches
                    );
                }
                else
                {
                    detailsTask = Section(
                        "invoiceDetails",
                        "进货明细加载失败，请重试。",
                        new LocalSupplierProductSalesInvoiceDetailPageDto(),
                        () => detailsWorker.ComputeInvoiceDetailsFromCodeAsync(
                            context,
                            request,
                            scopedStoreCodes,
                            startDate,
                            endDate,
                            outcome.CurrentProductCode!
                        )
                    );
                    dailyTask = Section(
                        "productDaily",
                        "商品趋势加载失败，请重试。",
                        new List<LocalSupplierProductSalesDailyDto>(),
                        () => dailyWorker.ComputeProductDailyFromCodeAsync(
                            scopedStoreCodes,
                            startDate,
                            endDate,
                            outcome.CurrentProductCode!
                        )
                    );
                    branchesTask = Section(
                        "branches",
                        "分店排行加载失败，请重试。",
                        new List<LocalSupplierProductSalesBranchDto>(),
                        () => branchesWorker.ComputeBranchesFromCodeAsync(
                            context,
                            scopedStoreCodes,
                            startDate,
                            endDate,
                            outcome.CurrentProductCode!
                        )
                    );
                }

                await Task.WhenAll(
                    optionsTask,
                    summaryTask,
                    detailsTask,
                    dailyTask,
                    branchesTask
                );
                options = await optionsTask;
                summary = await summaryTask;
                invoiceDetails = await detailsTask;
                productDaily = await dailyTask;
                branches = await branchesTask;
            }
            else
            {
                // SQLite 内存测试数据库不能复制连接，保持确定性的串行执行。
                options = await Section(
                    "options",
                    "筛选选项加载失败，请重试。",
                    new LocalSupplierProductSalesOptionsDto(),
                    () => BuildOptionsAsync(context)
                );
                summary = await Section(
                    "summary",
                    "汇总加载失败，请重试。",
                    new LocalSupplierProductSalesSummaryResponseDto(),
                    () => ComputeSummaryFromQueryAsync(
                        context,
                        request,
                        scopedStoreCodes,
                        startDate,
                        endDate,
                        selectedProducts
                    )
                );
                if (outcome.CurrentProductCode != null)
                {
                    invoiceDetails = await Section(
                        "invoiceDetails",
                        "进货明细加载失败，请重试。",
                        new LocalSupplierProductSalesInvoiceDetailPageDto(),
                        () => ComputeInvoiceDetailsFromCodeAsync(
                            context,
                            request,
                            scopedStoreCodes,
                            startDate,
                            endDate,
                            outcome.CurrentProductCode!
                        )
                    );
                    productDaily = await Section(
                        "productDaily",
                        "商品趋势加载失败，请重试。",
                        new List<LocalSupplierProductSalesDailyDto>(),
                        () => ComputeProductDailyFromCodeAsync(
                            scopedStoreCodes,
                            startDate,
                            endDate,
                            outcome.CurrentProductCode!
                        )
                    );
                    branches = await Section(
                        "branches",
                        "分店排行加载失败，请重试。",
                        new List<LocalSupplierProductSalesBranchDto>(),
                        () => ComputeBranchesFromCodeAsync(
                            context,
                            scopedStoreCodes,
                            startDate,
                            endDate,
                            outcome.CurrentProductCode!
                        )
                    );
                }
            }

            totalStopwatch.Stop();
            timings["total"] = totalStopwatch.Elapsed.TotalMilliseconds;
            _logger.LogInformation(
                "本地商品销量分析 bootstrap 完成 Scope={ScopeType} ScopeCount={ScopeCount} TotalMs={TotalMs} FilterMs={FilterMs} CandidateMs={CandidateMs} SelectionMs={SelectionMs} OptionsMs={OptionsMs} SummaryMs={SummaryMs} InvoiceMs={InvoiceMs} ProductDailyMs={ProductDailyMs} BranchesMs={BranchesMs} Candidates={CandidateCount} SummaryRows={SummaryCount} InvoiceRows={DetailCount} DailyRows={DailyCount} BranchRows={BranchCount} Partial={Partial}",
                scopedStoreCodes is null ? "all" : "scoped",
                scopedStoreCodes?.Count ?? 0,
                totalStopwatch.ElapsedMilliseconds,
                timings.GetValueOrDefault("filter"),
                timings.GetValueOrDefault("candidates"),
                timings.GetValueOrDefault("selection"),
                timings.GetValueOrDefault("options"),
                timings.GetValueOrDefault("summary"),
                timings.GetValueOrDefault("invoiceDetails"),
                timings.GetValueOrDefault("productDaily"),
                timings.GetValueOrDefault("branches"),
                candidates.Total,
                summary.Total,
                invoiceDetails.Total,
                productDaily.Count,
                branches.Count,
                partial
            );

            return new LocalSupplierProductSalesBootstrapResponseDto
            {
                Options = options,
                Candidates = candidates,
                EffectiveSelection = outcome.Selection,
                CurrentProduct = currentProduct,
                Summary = summary,
                InvoiceDetails = invoiceDetails,
                ProductDaily = productDaily,
                Branches = branches,
                Partial = partial,
                SectionErrors = sectionErrors,
                ServerTimings = timings,
            };
        }

        private LocalSupplierProductSalesAnalysisService CreateParallelWorker()
        {
            var worker = new LocalSupplierProductSalesAnalysisService(
                _db.CopyNew(),
                _cache,
                _logger
            );
            worker._useDirectUniqueProductPath = _useDirectUniqueProductPath;
            worker._hasFastSqlServerSchema = _hasFastSqlServerSchema;
            worker._directEligibleProductCount = _directEligibleProductCount;
            return worker;
        }

        private async Task EnsureProductKeyHealthAsync()
        {
            if (_db.CurrentConnectionConfig.DbType != DbType.SqlServer)
            {
                return;
            }

            if (
                _cache.TryGetValue<LocalSupplierProductSalesProductKeyHealth>(
                    ProductKeyHealthCacheKey,
                    out var cachedHealth
                )
                && cachedHealth is not null
            )
            {
                _useDirectUniqueProductPath =
                    cachedHealth.NoDuplicates == 1
                    && cachedHealth.HasDirectProductSchema == 1;
                _hasFastSqlServerSchema = cachedHealth.HasFastSchema == 1;
                _directEligibleProductCount = cachedHealth.EligibleCount;
                return;
            }

            // 重复编码存在时继续走 canonical 最小 UUID；无重复时在同一 60 秒缓存窗内
            // 直接使用覆盖索引，避免每个分段重复扫描 17 万商品。
            const string sql = @"
;WITH [ProductGroups] AS
(
    SELECT COUNT_BIG(1) AS [EntryCount]
    FROM [dbo].[Product]
    WHERE [IsDeleted] = 0
      AND [IsActive] = 1
      AND [ProductCode] IS NOT NULL
      AND [ProductCode] <> N''
      AND [LocalSupplierCode] IS NOT NULL
      AND [LocalSupplierCode] <> N''
    GROUP BY [ProductCode]
)
SELECT
CASE WHEN ISNULL(MAX([EntryCount]), 0) > 1 THEN 0 ELSE 1 END AS [NoDuplicates],
CASE WHEN EXISTS
      (
          SELECT 1 FROM sys.indexes
          WHERE object_id = OBJECT_ID(N'dbo.Product')
            AND name = N'IX_LSPSA_Product_ProductCode_UUID'
            AND is_disabled = 0
            AND is_hypothetical = 0
      )
THEN 1 ELSE 0 END AS [HasDirectProductSchema],
CASE WHEN EXISTS
      (
          SELECT 1 FROM sys.computed_columns
          WHERE object_id = OBJECT_ID(N'dbo.StoreLocalSupplierInvoice')
            AND name = N'EffectivePurchaseDate'
            AND is_persisted = 1
      )
      AND EXISTS
      (
          SELECT 1 FROM sys.indexes
          WHERE object_id = OBJECT_ID(N'dbo.StoreLocalSupplierInvoice')
            AND name = N'IX_LSPSA_Invoice_EffectiveDate_Store_Invoice'
            AND is_disabled = 0
            AND is_hypothetical = 0
      )
      AND EXISTS
      (
          SELECT 1 FROM sys.indexes
          WHERE object_id = OBJECT_ID(N'dbo.ProductStoreDailySalesStatistic')
            AND name = N'IX_LSPSA_Sales_Product_Date'
            AND is_disabled = 0
            AND is_hypothetical = 0
      )
      AND EXISTS
      (
          SELECT 1 FROM sys.indexes
          WHERE object_id = OBJECT_ID(N'dbo.StoreLocalSupplierInvoiceDetails')
            AND name = N'IX_LSPSA_InvoiceDetails_Product_Invoice'
            AND is_disabled = 0
            AND is_hypothetical = 0
      )
      AND EXISTS
      (
          SELECT 1 FROM sys.indexes
          WHERE object_id = OBJECT_ID(N'dbo.StoreLocalSupplierInvoiceDetails')
            AND name = N'IX_LSPSA_InvoiceDetails_Invoice_Product'
            AND type = 2
            AND is_disabled = 0
            AND is_hypothetical = 0
      )
      AND EXISTS
      (
          SELECT 1 FROM sys.indexes
          WHERE object_id = OBJECT_ID(N'dbo.ProductStoreDailySalesStatistic')
            AND name = N'IX_LSPSA_Sales_Date_Product'
            AND type = 2
            AND is_disabled = 0
            AND is_hypothetical = 0
      )
      AND EXISTS
      (
          SELECT 1 FROM sys.indexes
          WHERE object_id = OBJECT_ID(N'dbo.ProductStoreDailySalesStatistic')
            AND name = N'IX_ProductStoreDailySalesStatistic_Branch_Product_Date'
            AND is_disabled = 0
            AND is_hypothetical = 0
      )
      AND EXISTS
      (
          SELECT 1 FROM sys.indexes
          WHERE object_id = OBJECT_ID(N'dbo.StoreLocalSupplierInvoice')
            AND name = N'PK_StoreLocalSupplierInvoice_InvoiceGUID'
            AND is_disabled = 0
            AND is_hypothetical = 0
      )
THEN 1 ELSE 0 END AS [HasFastSchema],
CONVERT(int, ISNULL(SUM([EntryCount]), 0)) AS [EligibleCount]
FROM [ProductGroups]";
            var values = await _db.Ado.SqlQueryAsync<LocalSupplierProductSalesProductKeyHealth>(
                sql
            );
            var health = values.FirstOrDefault() ?? new LocalSupplierProductSalesProductKeyHealth();
            _useDirectUniqueProductPath =
                health.NoDuplicates == 1 && health.HasDirectProductSchema == 1;
            _hasFastSqlServerSchema = health.HasFastSchema == 1;
            _directEligibleProductCount = health.EligibleCount;
            _cache.Set(
                ProductKeyHealthCacheKey,
                health,
                SuccessCacheDuration
            );
        }

        private async Task<LocalSupplierProductSalesOptionsDto> ComputeOptionsCoreAsync(
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            if (IsEmptyScope(scopedStoreCodes))
            {
                return new LocalSupplierProductSalesOptionsDto();
            }

            var context = await LoadMasterContextAsync(scopedStoreCodes);
            return await BuildOptionsAsync(context);
        }

        private async Task<
            LocalSupplierProductSalesPagedDto<LocalSupplierProductSalesCandidateDto>
        > ComputeCandidatesCoreAsync(
            LocalSupplierProductSalesAnalysisRequest request,
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            if (IsEmptyScope(scopedStoreCodes))
            {
                return new LocalSupplierProductSalesPagedDto<LocalSupplierProductSalesCandidateDto>();
            }

            await EnsureProductKeyHealthAsync();
            var context = await LoadMasterContextAsync(scopedStoreCodes);
            var filteredProducts = BuildFilteredProductQuery(
                context,
                request.Filter,
                scopedStoreCodes
            );
            return await QueryCandidatesAsync(filteredProducts, context, request);
        }

        private async Task<LocalSupplierProductSalesSummaryResponseDto> ComputeSummaryCoreAsync(
            LocalSupplierProductSalesAnalysisRequest request,
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            if (IsEmptyScope(scopedStoreCodes))
            {
                return new LocalSupplierProductSalesSummaryResponseDto();
            }

            await EnsureProductKeyHealthAsync();
            var (startDate, endDate) = LocalSupplierProductSalesAnalysisLogic.ValidateDateRange(
                request.Filter.StartDate,
                request.Filter.EndDate
            );
            var context = await LoadMasterContextAsync(scopedStoreCodes);
            var filteredProducts = BuildFilteredProductQuery(
                context,
                request.Filter,
                scopedStoreCodes
            );
            var effectiveSelection = await CleanSelectionAsync(
                filteredProducts,
                request.Selection
            );
            var selectedProducts = ApplySelectionQuery(filteredProducts, effectiveSelection);
            return await ComputeSummaryFromQueryAsync(
                context,
                request,
                scopedStoreCodes,
                startDate,
                endDate,
                selectedProducts
            );
        }

        private async Task<List<LocalSupplierProductSalesDailyDto>> ComputeProductDailyCoreAsync(
            LocalSupplierProductSalesAnalysisRequest request,
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            if (IsEmptyScope(scopedStoreCodes))
            {
                return new List<LocalSupplierProductSalesDailyDto>();
            }

            await EnsureProductKeyHealthAsync();
            var currentProductCode = RequireCurrentProductCode(request);
            var (startDate, endDate) = LocalSupplierProductSalesAnalysisLogic.ValidateDateRange(
                request.Filter.StartDate,
                request.Filter.EndDate
            );
            var context = await LoadMasterContextAsync(scopedStoreCodes);
            if (!await IsCurrentProductSelectedAsync(context, request, currentProductCode, scopedStoreCodes))
            {
                return new List<LocalSupplierProductSalesDailyDto>();
            }

            return await ComputeProductDailyFromCodeAsync(
                scopedStoreCodes,
                startDate,
                endDate,
                currentProductCode
            );
        }

        private async Task<LocalSupplierProductSalesInvoiceDetailPageDto>
            ComputeInvoiceDetailsCoreAsync(
                LocalSupplierProductSalesAnalysisRequest request,
                IReadOnlyList<string>? scopedStoreCodes
            )
        {
            if (IsEmptyScope(scopedStoreCodes))
            {
                return new LocalSupplierProductSalesInvoiceDetailPageDto();
            }

            await EnsureProductKeyHealthAsync();
            var currentProductCode = RequireCurrentProductCode(request);
            var (startDate, endDate) = LocalSupplierProductSalesAnalysisLogic.ValidateDateRange(
                request.Filter.StartDate,
                request.Filter.EndDate
            );
            var context = await LoadMasterContextAsync(scopedStoreCodes);
            if (!await IsCurrentProductSelectedAsync(context, request, currentProductCode, scopedStoreCodes))
            {
                return new LocalSupplierProductSalesInvoiceDetailPageDto();
            }

            return await ComputeInvoiceDetailsFromCodeAsync(
                context,
                request,
                scopedStoreCodes,
                startDate,
                endDate,
                currentProductCode
            );
        }

        private async Task<List<LocalSupplierProductSalesBranchDto>> ComputeBranchesCoreAsync(
            LocalSupplierProductSalesAnalysisRequest request,
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            if (IsEmptyScope(scopedStoreCodes))
            {
                return new List<LocalSupplierProductSalesBranchDto>();
            }

            await EnsureProductKeyHealthAsync();
            var currentProductCode = RequireCurrentProductCode(request);
            var (startDate, endDate) = LocalSupplierProductSalesAnalysisLogic.ValidateDateRange(
                request.Filter.StartDate,
                request.Filter.EndDate
            );
            var context = await LoadMasterContextAsync(scopedStoreCodes);
            if (!await IsCurrentProductSelectedAsync(context, request, currentProductCode, scopedStoreCodes))
            {
                return new List<LocalSupplierProductSalesBranchDto>();
            }

            return await ComputeBranchesFromCodeAsync(
                context,
                scopedStoreCodes,
                startDate,
                endDate,
                currentProductCode
            );
        }

        private async Task<List<LocalSupplierProductSalesBranchDailyDto>>
            ComputeBranchDailyCoreAsync(
                LocalSupplierProductSalesAnalysisRequest request,
                IReadOnlyList<string>? scopedStoreCodes
            )
        {
            if (IsEmptyScope(scopedStoreCodes))
            {
                return new List<LocalSupplierProductSalesBranchDailyDto>();
            }

            await EnsureProductKeyHealthAsync();
            var currentProductCode = RequireCurrentProductCode(request);
            var branchCode = LocalSupplierProductSalesAnalysisLogic.NormalizeText(request.BranchCode);
            if (string.IsNullOrWhiteSpace(branchCode))
            {
                throw new LocalSupplierProductSalesAnalysisValidationException(
                    "branchCode 不能为空。"
                );
            }

            var (startDate, endDate) = LocalSupplierProductSalesAnalysisLogic.ValidateDateRange(
                request.Filter.StartDate,
                request.Filter.EndDate
            );
            var context = await LoadMasterContextAsync(scopedStoreCodes);
            if (!await IsCurrentProductSelectedAsync(context, request, currentProductCode, scopedStoreCodes))
            {
                return new List<LocalSupplierProductSalesBranchDailyDto>();
            }

            var endDateExclusive = endDate.Date.AddDays(1);
            var stores = NormalizeStoreScopeForQuery(scopedStoreCodes);
            var normalizedProductCode = currentProductCode.Trim().ToUpperInvariant();
            var normalizedBranchCode = branchCode.Trim().ToUpperInvariant();
            var query = _db
                .Queryable<ProductStoreDailySalesStatistic>()
                .Where(row =>
                    row.ProductCode == normalizedProductCode
                    && row.BranchCode == normalizedBranchCode
                    && row.Date >= startDate.Date
                    && row.Date < endDateExclusive
                );
            if (stores is not null)
            {
                query = query.Where(row => stores.Contains(row.BranchCode));
            }

            var rows = await query
                .GroupBy(row => row.Date.Date)
                .Select(row => new LocalSupplierProductSalesSalesDailyRow
                {
                    Date = row.Date.Date,
                    NetSalesQuantity = SqlFunc.AggregateSum(row.TotalQuantity),
                    NetSalesAmount = SqlFunc.AggregateSum(row.TotalAmount),
                })
                .ToListAsync();

            return LocalSupplierProductSalesAnalysisLogic.BuildBranchDailySeries(
                rows.Select(row => new LocalSupplierProductSalesBranchDailyDto
                {
                    Date = row.Date,
                    NetSalesQuantity = row.NetSalesQuantity,
                    NetSalesAmount = row.NetSalesAmount,
                }),
                startDate,
                endDate
            );
        }

        private async Task<LocalSupplierProductSalesSummaryResponseDto> ComputeSummaryFromQueryAsync(
            LocalSupplierProductSalesMasterContext context,
            LocalSupplierProductSalesAnalysisRequest request,
            IReadOnlyList<string>? scopedStoreCodes,
            DateTime startDate,
            DateTime endDate,
            ISugarQueryable<Product> selectedProducts
        )
        {
            if (context.SupplierMetadataFailed)
            {
                throw new InvalidOperationException("供应商元数据不可用。");
            }

            var pageNumber = request.SummaryPageNumber > 0
                ? request.SummaryPageNumber
                : request.PageNumber;
            var pageSize = request.SummaryPageSize > 0 ? request.SummaryPageSize : request.PageSize;
            pageNumber = pageNumber <= 0 ? 1 : pageNumber;
            pageSize = pageSize <= 0 ? 50 : Math.Min(pageSize, 500);

            if (CanUseFastSqlServerSummary(request))
            {
                return await ComputeFastSqlServerSummaryAsync(
                    context,
                    scopedStoreCodes,
                    startDate,
                    endDate,
                    pageNumber,
                    pageSize
                );
            }

            // 进货与销量先在各自子查询中按商品聚合，再与商品主档连接，避免明细×销量扇出。
            // 无商品筛选的 allFiltered 是首屏主路径，事实表直接按商品聚合后再左连主档，
            // 避免在两张事实表内重复展开 17 万商品的 canonical 子查询。
            var broadSelection = IsBroadUnfilteredSelection(request);
            var purchaseAggregates = broadSelection
                ? BuildBroadPurchaseAggregateQuery(scopedStoreCodes, startDate, endDate)
                : BuildPurchaseAggregateQuery(
                    scopedStoreCodes,
                    startDate,
                    endDate,
                    selectedProducts
                );
            var salesAggregates = broadSelection
                ? BuildBroadSalesAggregateQuery(scopedStoreCodes, startDate, endDate)
                : BuildSalesAggregateQuery(
                    scopedStoreCodes,
                    startDate,
                    endDate,
                    selectedProducts
                );
            // 先把选择条件封装进派生表，避免 SqlSugar 在后续多表连接时把未限定的 ProductCode
            // 谓词提升到外层，导致 SQL Server/SQLite 出现列名歧义。
            var selectedProductRows = selectedProducts
                .Clone()
                .Select(product => new Product
                {
                    UUID = product.UUID,
                    ProductCode = product.ProductCode,
                    ItemNumber = product.ItemNumber,
                    Barcode = product.Barcode,
                    ProductName = product.ProductName,
                    ProductImage = product.ProductImage,
                    WarehouseCategoryGUID = product.WarehouseCategoryGUID,
                    LocalSupplierCode = product.LocalSupplierCode,
                })
                .MergeTable();
            var summaryQuery = selectedProductRows
                .LeftJoin(
                    purchaseAggregates,
                    (product, purchase) => product.ProductCode == purchase.ProductCode
                )
                .LeftJoin(
                    salesAggregates,
                    (product, purchase, sales) => product.ProductCode == sales.ProductCode
                )
                .Select(
                    (product, purchase, sales) =>
                        new LocalSupplierProductSalesSummarySqlRow
                        {
                            UUID = product.UUID,
                            ProductCode = product.ProductCode!.Trim(),
                            ItemNumber = product.ItemNumber,
                            Barcode = product.Barcode,
                            ProductName = product.ProductName,
                            ImageUrl = product.ProductImage,
                            WarehouseCategoryGuid = product.WarehouseCategoryGUID,
                            LocalSupplierCode = product.LocalSupplierCode,
                            PurchaseQuantity = purchase.Quantity ?? 0m,
                            PurchaseAmount = purchase.Amount ?? 0m,
                            NetSalesQuantity = SqlFunc.IsNull(sales.Quantity, 0m),
                            NetSalesAmount = SqlFunc.IsNull(sales.Amount, 0m),
                        }
                )
                .MergeTable();

            var totals =
                await summaryQuery
                    .Clone()
                    .Select(row => new LocalSupplierProductSalesSummaryTotalsSqlRow
                    {
                        Total = SqlFunc.AggregateCount(row.ProductCode),
                        PurchaseQuantity = SqlFunc.AggregateSum(row.PurchaseQuantity),
                        PurchaseAmount = SqlFunc.AggregateSum(row.PurchaseAmount),
                        NetSalesQuantity = SqlFunc.AggregateSum(row.NetSalesQuantity),
                        NetSalesAmount = SqlFunc.AggregateSum(row.NetSalesAmount),
                    })
                    .FirstAsync() ?? new LocalSupplierProductSalesSummaryTotalsSqlRow();

            var pageQuery = ApplySummarySort(summaryQuery, request.SortBy, request.SortDirection);
            var sqlRows = await pageQuery
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();
            var rows = sqlRows.Select(row => BuildSummaryRow(context, row)).ToList();

            _logger.LogInformation(
                "本地商品销量分析汇总 商品数={ProductCount} 返回行数={ReturnedCount}",
                totals.Total,
                rows.Count
            );

            return new LocalSupplierProductSalesSummaryResponseDto
            {
                Totals = new LocalSupplierProductSalesSummaryTotalsDto
                {
                    PurchaseQuantity = totals.PurchaseQuantity,
                    PurchaseAmount = totals.PurchaseAmount,
                    NetSalesQuantity = totals.NetSalesQuantity,
                    NetSalesAmount = totals.NetSalesAmount,
                    SellThroughRate =
                        LocalSupplierProductSalesAnalysisLogic.CalculateSellThroughRate(
                            totals.PurchaseQuantity,
                            totals.NetSalesQuantity
                        ),
                },
                Items = rows,
                Total = totals.Total,
                PageNumber = pageNumber,
                PageSize = pageSize,
            };
        }

        private bool CanUseFastSqlServerSummary(
            LocalSupplierProductSalesAnalysisRequest request
        )
        {
            var sortBy = LocalSupplierProductSalesAnalysisLogic.NormalizeText(request.SortBy);
            return _db.CurrentConnectionConfig.DbType == DbType.SqlServer
                && _useDirectUniqueProductPath
                && _hasFastSqlServerSchema
                && IsBroadUnfilteredSelection(request)
                && (sortBy == null
                    || string.Equals(
                        sortBy,
                        "netSalesQuantity",
                        StringComparison.OrdinalIgnoreCase
                    ))
                && !string.Equals(
                    request.SortDirection,
                    "asc",
                    StringComparison.OrdinalIgnoreCase
                );
        }

        private async Task<LocalSupplierProductSalesSummaryResponseDto>
            ComputeFastSqlServerSummaryAsync(
                LocalSupplierProductSalesMasterContext context,
                IReadOnlyList<string>? scopedStoreCodes,
                DateTime startDate,
                DateTime endDate,
                int pageNumber,
                int pageSize
            )
        {
            var stores = NormalizeStoreScopeForQuery(scopedStoreCodes);
            var storeParameterNames = stores is null
                ? new List<string>()
                : stores.Select((_, index) => $"@__lspsaStore{index}").ToList();
            var storeParameters = string.Join(",", storeParameterNames);
            var salesStoreSql = stores is null
                ? string.Empty
                : $"AND [sales].[BranchCode] IN ({storeParameters})";
            // 日统计按日期删除重建，列存会累积删除行；日期覆盖索引只读取本次范围，
            // 避免汇总性能依赖列存清理进度。限店查询仍使用原有分店覆盖索引。
            var salesIndexSql = stores is null
                ? "WITH (INDEX([IX_LSPSA_Sales_Date_Product]))"
                : "WITH (INDEX([IX_ProductStoreDailySalesStatistic_Branch_Product_Date]))";
            var purchaseStoreSql = stores is null
                ? string.Empty
                : $@"AND CASE
                        WHEN [detail].[StoreCode] IS NOT NULL AND [detail].[StoreCode] <> N''
                            THEN [detail].[StoreCode]
                        ELSE ISNULL([invoice].[StoreCode], N'')
                    END IN ({storeParameters})";
            // 本快速路径只运行在 SQL Server；其定长补空格比较使 <> N'' 同时排除
            // 全空格值，配合 C# 输出 Trim 保持原有空值与规范化语义。
            // 按进货单查明细和读取商品页均使用覆盖索引，避免日期范围扩大或冷缓存时逐行回表。
            var sql = $@"
SET NOCOUNT ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;
DECLARE @__lspsaBatchStartedAt datetime2(7) = SYSUTCDATETIME();

SELECT
    [sales].[ProductCode],
    SUM(CONVERT(bigint, [sales].[TotalQuantity])) AS [NetSalesQuantity],
    SUM([sales].[TotalAmount]) AS [NetSalesAmount]
INTO [#LSPSA_Sales]
FROM [dbo].[ProductStoreDailySalesStatistic] AS [sales] {salesIndexSql}
WHERE [sales].[Date] >= @__lspsaStartDate
  AND [sales].[Date] < @__lspsaEndDate
  AND [sales].[ProductCode] IS NOT NULL
  AND [sales].[ProductCode] <> N''
  {salesStoreSql}
GROUP BY [sales].[ProductCode]
OPTION (MAXDOP 2);
DECLARE @__lspsaSalesRows int = @@ROWCOUNT;
DECLARE @__lspsaSalesAggregatedAt datetime2(7) = SYSUTCDATETIME();

CREATE UNIQUE CLUSTERED INDEX [IX_LSPSA_TempSales_Product]
    ON [#LSPSA_Sales] ([ProductCode])
    WITH (MAXDOP = 1);
DECLARE @__lspsaSalesIndexedAt datetime2(7) = SYSUTCDATETIME();

SELECT
    [detail].[ProductCode],
    SUM(CONVERT(decimal(38, 6), ISNULL([detail].[Quantity], 0))) AS [PurchaseQuantity],
    SUM(CONVERT(decimal(38, 6), COALESCE(
        [detail].[Amount],
        ISNULL([detail].[Quantity], 0) * ISNULL([detail].[PurchasePrice], 0)
    ))) AS [PurchaseAmount]
INTO [#LSPSA_Purchase]
FROM [dbo].[StoreLocalSupplierInvoice] AS [invoice]
    WITH (INDEX([IX_LSPSA_Invoice_EffectiveDate_Store_Invoice]))
INNER LOOP JOIN [dbo].[StoreLocalSupplierInvoiceDetails] AS [detail]
    WITH (INDEX([IX_LSPSA_InvoiceDetails_Invoice_Product]))
    ON [detail].[InvoiceGUID] = [invoice].[InvoiceGUID]
WHERE [invoice].[IsDeleted] = 0
  AND [detail].[IsDeleted] = 0
  AND [detail].[ProductCode] IS NOT NULL
  AND [detail].[ProductCode] <> N''
  AND [invoice].[EffectivePurchaseDate] >= @__lspsaStartDate
  AND [invoice].[EffectivePurchaseDate] < @__lspsaEndDate
  {purchaseStoreSql}
GROUP BY [detail].[ProductCode];
DECLARE @__lspsaPurchaseRows int = @@ROWCOUNT;
DECLARE @__lspsaPurchaseAggregatedAt datetime2(7) = SYSUTCDATETIME();

CREATE UNIQUE CLUSTERED INDEX [IX_LSPSA_TempPurchase_Product]
    ON [#LSPSA_Purchase] ([ProductCode])
    WITH (MAXDOP = 1);
DECLARE @__lspsaPurchaseIndexedAt datetime2(7) = SYSUTCDATETIME();

SELECT TOP (0)
    [product].[UUID],
    [product].[ProductCode],
    [product].[ItemNumber],
    [product].[Barcode],
    [product].[ProductName],
    [product].[ProductImage] AS [ImageUrl],
    [product].[WarehouseCategoryGUID] AS [WarehouseCategoryGuid],
    [product].[LocalSupplierCode],
    ISNULL([sales].[NetSalesQuantity], 0) AS [NetSalesQuantity],
    ISNULL([sales].[NetSalesAmount], 0) AS [NetSalesAmount],
    CONVERT(bigint, 0) AS [PageOrder]
INTO [#LSPSA_Page]
FROM [dbo].[Product] AS [product]
LEFT JOIN [#LSPSA_Sales] AS [sales]
    ON [sales].[ProductCode] = [product].[ProductCode];

;WITH [RankedPositiveProducts] AS
(
    SELECT
        [product].[UUID],
        [product].[ProductCode],
        [product].[ItemNumber],
        [product].[Barcode],
        [product].[ProductName],
        [product].[ProductImage] AS [ImageUrl],
        [product].[WarehouseCategoryGUID] AS [WarehouseCategoryGuid],
        [product].[LocalSupplierCode],
        ISNULL([sales].[NetSalesQuantity], 0) AS [NetSalesQuantity],
        ISNULL([sales].[NetSalesAmount], 0) AS [NetSalesAmount],
        ROW_NUMBER() OVER
        (
            ORDER BY
                ISNULL([sales].[NetSalesQuantity], 0) DESC,
                [product].[ProductCode] ASC,
                [product].[UUID] ASC
        ) AS [PageOrder]
    FROM [#LSPSA_Sales] AS [sales]
    INNER JOIN [dbo].[Product] AS [product]
        WITH (INDEX([IX_LSPSA_Product_ProductCode_UUID]))
        ON [sales].[ProductCode] = [product].[ProductCode]
    WHERE [product].[IsDeleted] = 0
      AND [product].[IsActive] = 1
      AND [product].[ProductCode] IS NOT NULL
      AND [product].[ProductCode] <> N''
      AND [product].[LocalSupplierCode] IS NOT NULL
      AND [product].[LocalSupplierCode] <> N''
      AND [sales].[NetSalesQuantity] > 0
)
INSERT INTO [#LSPSA_Page]
SELECT *
FROM [RankedPositiveProducts]
WHERE [PageOrder] > @__lspsaOffset
  AND [PageOrder] <= @__lspsaOffset + @__lspsaPageSize;

IF (SELECT COUNT(1) FROM [#LSPSA_Page]) < @__lspsaPageSize
BEGIN
    DELETE FROM [#LSPSA_Page];
    ;WITH [RankedProducts] AS
    (
        SELECT
            [product].[UUID],
            [product].[ProductCode],
            [product].[ItemNumber],
            [product].[Barcode],
            [product].[ProductName],
            [product].[ProductImage] AS [ImageUrl],
            [product].[WarehouseCategoryGUID] AS [WarehouseCategoryGuid],
            [product].[LocalSupplierCode],
            ISNULL([sales].[NetSalesQuantity], 0) AS [NetSalesQuantity],
            ISNULL([sales].[NetSalesAmount], 0) AS [NetSalesAmount],
            ROW_NUMBER() OVER
            (
                ORDER BY
                    ISNULL([sales].[NetSalesQuantity], 0) DESC,
                    [product].[ProductCode] ASC,
                    [product].[UUID] ASC
            ) AS [PageOrder]
        FROM [dbo].[Product] AS [product]
            WITH (INDEX([IX_LSPSA_Product_ProductCode_UUID]))
        LEFT JOIN [#LSPSA_Sales] AS [sales]
            ON [sales].[ProductCode] = [product].[ProductCode]
        WHERE [product].[IsDeleted] = 0
          AND [product].[IsActive] = 1
          AND [product].[ProductCode] IS NOT NULL
          AND [product].[ProductCode] <> N''
          AND [product].[LocalSupplierCode] IS NOT NULL
          AND [product].[LocalSupplierCode] <> N''
    )
    INSERT INTO [#LSPSA_Page]
    SELECT *
    FROM [RankedProducts]
    WHERE [PageOrder] > @__lspsaOffset
      AND [PageOrder] <= @__lspsaOffset + @__lspsaPageSize;
END;

DECLARE @__lspsaPageReadyAt datetime2(7) = SYSUTCDATETIME();
DECLARE @__lspsaPageRows int = (SELECT COUNT(1) FROM [#LSPSA_Page]);
DECLARE @__lspsaPurchaseQuantity decimal(38, 6) = 0;
DECLARE @__lspsaPurchaseAmount decimal(38, 6) = 0;
DECLARE @__lspsaNetSalesQuantity decimal(38, 6) = 0;
DECLARE @__lspsaNetSalesAmount decimal(38, 6) = 0;

SELECT
    @__lspsaPurchaseQuantity = ISNULL(SUM([purchase].[PurchaseQuantity]), 0),
    @__lspsaPurchaseAmount = ISNULL(SUM([purchase].[PurchaseAmount]), 0),
    @__lspsaNetSalesQuantity = ISNULL(SUM([sales].[NetSalesQuantity]), 0),
    @__lspsaNetSalesAmount = ISNULL(SUM([sales].[NetSalesAmount]), 0)
FROM [dbo].[Product] AS [product]
    WITH (INDEX([IX_LSPSA_Product_ProductCode_UUID]))
LEFT MERGE JOIN [#LSPSA_Sales] AS [sales]
    ON [sales].[ProductCode] = [product].[ProductCode]
LEFT MERGE JOIN [#LSPSA_Purchase] AS [purchase]
    ON [purchase].[ProductCode] = [product].[ProductCode]
WHERE [product].[IsDeleted] = 0
  AND [product].[IsActive] = 1
  AND [product].[ProductCode] IS NOT NULL
  AND [product].[ProductCode] <> N''
  AND [product].[LocalSupplierCode] IS NOT NULL
  AND [product].[LocalSupplierCode] <> N'';

DECLARE @__lspsaTotalsAggregatedAt datetime2(7) = SYSUTCDATETIME();

SELECT
    @__lspsaEligibleProductCount AS [Total],
    @__lspsaPurchaseQuantity AS [PurchaseQuantity],
    @__lspsaPurchaseAmount AS [PurchaseAmount],
    @__lspsaNetSalesQuantity AS [NetSalesQuantity],
    @__lspsaNetSalesAmount AS [NetSalesAmount],
    @__lspsaSalesRows AS [SalesRows],
    @__lspsaPurchaseRows AS [PurchaseRows],
    @__lspsaPageRows AS [PageRows],
    CONVERT(decimal(18, 3), DATEDIFF_BIG(microsecond, @__lspsaBatchStartedAt, @__lspsaSalesAggregatedAt) / 1000.0) AS [SalesAggregateMs],
    CONVERT(decimal(18, 3), DATEDIFF_BIG(microsecond, @__lspsaSalesAggregatedAt, @__lspsaSalesIndexedAt) / 1000.0) AS [SalesIndexMs],
    CONVERT(decimal(18, 3), DATEDIFF_BIG(microsecond, @__lspsaSalesIndexedAt, @__lspsaPurchaseAggregatedAt) / 1000.0) AS [PurchaseAggregateMs],
    CONVERT(decimal(18, 3), DATEDIFF_BIG(microsecond, @__lspsaPurchaseAggregatedAt, @__lspsaPurchaseIndexedAt) / 1000.0) AS [PurchaseIndexMs],
    CONVERT(decimal(18, 3), DATEDIFF_BIG(microsecond, @__lspsaPurchaseIndexedAt, @__lspsaPageReadyAt) / 1000.0) AS [PageMs],
    CONVERT(decimal(18, 3), DATEDIFF_BIG(microsecond, @__lspsaPageReadyAt, @__lspsaTotalsAggregatedAt) / 1000.0) AS [TotalsAggregateMs];

SELECT
    [page].[UUID],
    [page].[ProductCode],
    [page].[ItemNumber],
    [page].[Barcode],
    [page].[ProductName],
    [page].[ImageUrl],
    [page].[WarehouseCategoryGuid],
    [page].[LocalSupplierCode],
    ISNULL([purchase].[PurchaseQuantity], 0) AS [PurchaseQuantity],
    ISNULL([purchase].[PurchaseAmount], 0) AS [PurchaseAmount],
    [page].[NetSalesQuantity],
    [page].[NetSalesAmount]
FROM [#LSPSA_Page] AS [page]
LEFT JOIN [#LSPSA_Purchase] AS [purchase]
    ON [purchase].[ProductCode] = [page].[ProductCode]
ORDER BY [page].[PageOrder] ASC;";

            var connection = (System.Data.Common.DbConnection)_db.Ado.Connection;
            var shouldClose = connection.State != System.Data.ConnectionState.Open;
            if (shouldClose)
            {
                await connection.OpenAsync();
            }

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.CommandTimeout = 30;

                void AddParameter(string name, object value, System.Data.DbType dbType)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = name;
                    parameter.Value = value;
                    parameter.DbType = dbType;
                    command.Parameters.Add(parameter);
                }

                AddParameter(
                    "@__lspsaStartDate",
                    startDate.Date,
                    System.Data.DbType.Date
                );
                AddParameter(
                    "@__lspsaEndDate",
                    endDate.Date.AddDays(1),
                    System.Data.DbType.Date
                );
                AddParameter(
                    "@__lspsaOffset",
                    (pageNumber - 1) * pageSize,
                    System.Data.DbType.Int32
                );
                AddParameter("@__lspsaPageSize", pageSize, System.Data.DbType.Int32);
                AddParameter(
                    "@__lspsaEligibleProductCount",
                    _directEligibleProductCount,
                    System.Data.DbType.Int32
                );
                if (stores is not null)
                {
                    for (var index = 0; index < stores.Count; index++)
                    {
                        AddParameter(
                            storeParameterNames[index],
                            stores[index],
                            System.Data.DbType.String
                        );
                    }
                }

                var sqlStopwatch = Stopwatch.StartNew();
                await using var reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    throw new InvalidOperationException("SQL Server 汇总未返回总计行。");
                }
                var totalsReadyMilliseconds = sqlStopwatch.Elapsed.TotalMilliseconds;

                var total = Convert.ToInt32(reader.GetValue(0));
                var purchaseQuantity = Convert.ToDecimal(reader.GetValue(1));
                var purchaseAmount = Convert.ToDecimal(reader.GetValue(2));
                var netSalesQuantity = Convert.ToDecimal(reader.GetValue(3));
                var netSalesAmount = Convert.ToDecimal(reader.GetValue(4));
                var salesRows = Convert.ToInt32(reader.GetValue(5));
                var purchaseRows = Convert.ToInt32(reader.GetValue(6));
                var pageRows = Convert.ToInt32(reader.GetValue(7));
                var salesAggregateMilliseconds = Convert.ToDecimal(reader.GetValue(8));
                var salesIndexMilliseconds = Convert.ToDecimal(reader.GetValue(9));
                var purchaseAggregateMilliseconds = Convert.ToDecimal(reader.GetValue(10));
                var purchaseIndexMilliseconds = Convert.ToDecimal(reader.GetValue(11));
                var pageMilliseconds = Convert.ToDecimal(reader.GetValue(12));
                var totalsAggregateMilliseconds = Convert.ToDecimal(reader.GetValue(13));
                if (!await reader.NextResultAsync())
                {
                    throw new InvalidOperationException("SQL Server 汇总未返回分页结果集。");
                }

                var sqlRows = new List<LocalSupplierProductSalesSummarySqlRow>();
                while (await reader.ReadAsync())
                {
                    string? NullableString(int ordinal) =>
                        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal));

                    sqlRows.Add(
                        new LocalSupplierProductSalesSummarySqlRow
                        {
                            UUID = Convert.ToString(reader.GetValue(0)) ?? string.Empty,
                            ProductCode = Convert.ToString(reader.GetValue(1))?.Trim()
                                ?? string.Empty,
                            ItemNumber = NullableString(2),
                            Barcode = NullableString(3),
                            ProductName = NullableString(4),
                            ImageUrl = NullableString(5),
                            WarehouseCategoryGuid = NullableString(6),
                            LocalSupplierCode = NullableString(7),
                            PurchaseQuantity = Convert.ToDecimal(reader.GetValue(8)),
                            PurchaseAmount = Convert.ToDecimal(reader.GetValue(9)),
                            NetSalesQuantity = Convert.ToDecimal(reader.GetValue(10)),
                            NetSalesAmount = Convert.ToDecimal(reader.GetValue(11)),
                        }
                    );
                }

                var rows = sqlRows.Select(row => BuildSummaryRow(context, row)).ToList();
                sqlStopwatch.Stop();
                _logger.LogInformation(
                    "本地商品销量分析快速汇总 商品数={ProductCount} 返回行数={ReturnedCount} 销量聚合行数={SalesRows} 进货聚合行数={PurchaseRows} 分页行数={PageRows} 销量聚合毫秒={SalesAggregateMs} 销量临时索引毫秒={SalesIndexMs} 进货聚合毫秒={PurchaseAggregateMs} 进货临时索引毫秒={PurchaseIndexMs} 分页毫秒={PageMs} 总计聚合毫秒={TotalsAggregateMs} 总计就绪毫秒={TotalsReadyMs} 完成毫秒={CompleteMs}",
                    total,
                    rows.Count,
                    salesRows,
                    purchaseRows,
                    pageRows,
                    salesAggregateMilliseconds,
                    salesIndexMilliseconds,
                    purchaseAggregateMilliseconds,
                    purchaseIndexMilliseconds,
                    pageMilliseconds,
                    totalsAggregateMilliseconds,
                    totalsReadyMilliseconds,
                    sqlStopwatch.Elapsed.TotalMilliseconds
                );
                return new LocalSupplierProductSalesSummaryResponseDto
                {
                    Totals = new LocalSupplierProductSalesSummaryTotalsDto
                    {
                        PurchaseQuantity = purchaseQuantity,
                        PurchaseAmount = purchaseAmount,
                        NetSalesQuantity = netSalesQuantity,
                        NetSalesAmount = netSalesAmount,
                        SellThroughRate =
                            LocalSupplierProductSalesAnalysisLogic.CalculateSellThroughRate(
                                purchaseQuantity,
                                netSalesQuantity
                            ),
                    },
                    Items = rows,
                    Total = total,
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                };
            }
            finally
            {
                if (shouldClose)
                {
                    await connection.CloseAsync();
                }
            }
        }

        private async Task<LocalSupplierProductSalesCurrentBundle>
            ComputeFastSqlServerCurrentBundleAsync(
                LocalSupplierProductSalesMasterContext context,
                LocalSupplierProductSalesAnalysisRequest request,
                IReadOnlyList<string>? scopedStoreCodes,
                DateTime startDate,
                DateTime endDate,
                string currentProductCode
            )
        {
            var stores = NormalizeStoreScopeForQuery(scopedStoreCodes);
            var storeParameterNames = stores is null
                ? new List<string>()
                : stores.Select((_, index) => $"@__lspsaCurrentStore{index}").ToList();
            var storeParameters = string.Join(",", storeParameterNames);
            var salesStoreSql = stores is null
                ? string.Empty
                : $"AND [sales].[BranchCode] IN ({storeParameters})";
            var salesIndexSql = stores is null
                ? "WITH (INDEX([IX_LSPSA_Sales_Product_Date]))"
                : "WITH (INDEX([IX_ProductStoreDailySalesStatistic_Branch_Product_Date]))";
            var purchaseStoreSql = stores is null
                ? string.Empty
                : $@"AND CASE
                        WHEN [detail].[StoreCode] IS NOT NULL AND [detail].[StoreCode] <> N''
                            THEN [detail].[StoreCode]
                        ELSE ISNULL([invoice].[StoreCode], N'')
                    END IN ({storeParameters})";
            var pageNumber = request.PageNumber <= 0 ? 1 : request.PageNumber;
            var pageSize = request.PageSize <= 0 ? 50 : Math.Min(request.PageSize, 500);
            var sql = $@"
SET NOCOUNT ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;

SELECT
    CONVERT(date, [sales].[Date]) AS [Date],
    [sales].[BranchCode],
    SUM(CONVERT(decimal(38, 6), [sales].[TotalQuantity])) AS [NetSalesQuantity],
    SUM(CONVERT(decimal(38, 6), [sales].[TotalAmount])) AS [NetSalesAmount]
INTO [#LSPSA_CurrentSales]
FROM [dbo].[ProductStoreDailySalesStatistic] AS [sales] {salesIndexSql}
WHERE [sales].[ProductCode] = @__lspsaCurrentProduct
  AND [sales].[Date] >= @__lspsaCurrentStartDate
  AND [sales].[Date] < @__lspsaCurrentEndDate
  {salesStoreSql}
GROUP BY CONVERT(date, [sales].[Date]), [sales].[BranchCode]
OPTION (RECOMPILE);

SELECT
    [detail].[DetailGUID],
    [detail].[InvoiceGUID],
    [invoice].[InvoiceNo],
    CASE
        WHEN [detail].[StoreCode] IS NOT NULL AND LTRIM(RTRIM([detail].[StoreCode])) <> N''
            THEN LTRIM(RTRIM([detail].[StoreCode]))
        ELSE LTRIM(RTRIM(ISNULL([invoice].[StoreCode], N'')))
    END AS [StoreCode],
    CASE
        WHEN [detail].[SupplierCode] IS NOT NULL AND LTRIM(RTRIM([detail].[SupplierCode])) <> N''
            THEN LTRIM(RTRIM([detail].[SupplierCode]))
        ELSE LTRIM(RTRIM(ISNULL([invoice].[SupplierCode], N'')))
    END AS [SupplierCode],
    [invoice].[EffectivePurchaseDate] AS [PurchaseDate],
    [detail].[ProductCode],
    [detail].[ProductName],
    ISNULL([detail].[Quantity], 0) AS [Quantity],
    [detail].[PurchasePrice],
    COALESCE(
        [detail].[Amount],
        ISNULL([detail].[Quantity], 0) * ISNULL([detail].[PurchasePrice], 0)
    ) AS [Amount]
INTO [#LSPSA_CurrentPurchase]
FROM [dbo].[StoreLocalSupplierInvoiceDetails] AS [detail]
    WITH (INDEX([IX_LSPSA_InvoiceDetails_Product_Invoice]))
INNER LOOP JOIN [dbo].[StoreLocalSupplierInvoice] AS [invoice]
    WITH (INDEX([PK_StoreLocalSupplierInvoice_InvoiceGUID]))
    ON [invoice].[InvoiceGUID] = [detail].[InvoiceGUID]
WHERE [detail].[IsDeleted] = 0
  AND [invoice].[IsDeleted] = 0
  AND [detail].[ProductCode] = @__lspsaCurrentProduct
  AND [invoice].[EffectivePurchaseDate] >= @__lspsaCurrentStartDate
  AND [invoice].[EffectivePurchaseDate] < @__lspsaCurrentEndDate
  {purchaseStoreSql}
OPTION (RECOMPILE);

SELECT COUNT(1) AS [Total]
FROM [#LSPSA_CurrentPurchase];

SELECT
    [DetailGUID], [InvoiceGUID], [InvoiceNo], [StoreCode], [SupplierCode],
    [PurchaseDate], [ProductCode], [ProductName], [Quantity], [PurchasePrice], [Amount]
FROM [#LSPSA_CurrentPurchase]
ORDER BY [PurchaseDate] DESC, [DetailGUID] ASC
OFFSET @__lspsaCurrentOffset ROWS
FETCH NEXT @__lspsaCurrentPageSize ROWS ONLY;

SELECT
    [combined].[Date],
    SUM([combined].[PurchaseQuantity]) AS [PurchaseQuantity],
    SUM([combined].[PurchaseAmount]) AS [PurchaseAmount],
    SUM([combined].[NetSalesQuantity]) AS [NetSalesQuantity],
    SUM([combined].[NetSalesAmount]) AS [NetSalesAmount]
FROM
(
    SELECT
        [PurchaseDate] AS [Date],
        SUM(CONVERT(decimal(38, 6), [Quantity])) AS [PurchaseQuantity],
        SUM(CONVERT(decimal(38, 6), [Amount])) AS [PurchaseAmount],
        CONVERT(decimal(38, 6), 0) AS [NetSalesQuantity],
        CONVERT(decimal(38, 6), 0) AS [NetSalesAmount]
    FROM [#LSPSA_CurrentPurchase]
    GROUP BY [PurchaseDate]

    UNION ALL

    SELECT
        [Date],
        CONVERT(decimal(38, 6), 0),
        CONVERT(decimal(38, 6), 0),
        SUM([NetSalesQuantity]),
        SUM([NetSalesAmount])
    FROM [#LSPSA_CurrentSales]
    GROUP BY [Date]
) AS [combined]
GROUP BY [combined].[Date]
ORDER BY [combined].[Date] ASC;

SELECT
    [BranchCode],
    SUM([NetSalesQuantity]) AS [NetSalesQuantity],
    SUM([NetSalesAmount]) AS [NetSalesAmount]
FROM [#LSPSA_CurrentSales]
GROUP BY [BranchCode]
ORDER BY [NetSalesQuantity] DESC, [BranchCode] ASC;";

            var connection = (System.Data.Common.DbConnection)_db.Ado.Connection;
            var shouldClose = connection.State != System.Data.ConnectionState.Open;
            if (shouldClose)
            {
                await connection.OpenAsync();
            }

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.CommandTimeout = 30;

                void AddParameter(string name, object value, System.Data.DbType dbType)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = name;
                    parameter.Value = value;
                    parameter.DbType = dbType;
                    command.Parameters.Add(parameter);
                }

                AddParameter(
                    "@__lspsaCurrentProduct",
                    currentProductCode.Trim(),
                    System.Data.DbType.String
                );
                AddParameter(
                    "@__lspsaCurrentStartDate",
                    startDate.Date,
                    System.Data.DbType.Date
                );
                AddParameter(
                    "@__lspsaCurrentEndDate",
                    endDate.Date.AddDays(1),
                    System.Data.DbType.Date
                );
                AddParameter(
                    "@__lspsaCurrentOffset",
                    (pageNumber - 1) * pageSize,
                    System.Data.DbType.Int32
                );
                AddParameter(
                    "@__lspsaCurrentPageSize",
                    pageSize,
                    System.Data.DbType.Int32
                );
                if (stores is not null)
                {
                    for (var index = 0; index < stores.Count; index++)
                    {
                        AddParameter(
                            storeParameterNames[index],
                            stores[index],
                            System.Data.DbType.String
                        );
                    }
                }

                await using var reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    throw new InvalidOperationException("SQL Server 当前商品未返回明细总数。");
                }

                var detailTotal = Convert.ToInt32(reader.GetValue(0));
                if (!await reader.NextResultAsync())
                {
                    throw new InvalidOperationException("SQL Server 当前商品未返回明细页。");
                }

                var detailItems = new List<LocalSupplierProductSalesInvoiceDetailDto>();
                while (await reader.ReadAsync())
                {
                    string? NullableString(int ordinal) =>
                        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal));

                    var storeCode = NullableString(3);
                    var supplierCode = NullableString(4);
                    detailItems.Add(
                        new LocalSupplierProductSalesInvoiceDetailDto
                        {
                            DetailGUID = Convert.ToString(reader.GetValue(0)) ?? string.Empty,
                            InvoiceGUID = Convert.ToString(reader.GetValue(1)) ?? string.Empty,
                            InvoiceNo = NullableString(2),
                            StoreCode = storeCode,
                            StoreName = storeCode != null
                                && context.StoreNameByCode.TryGetValue(storeCode, out var storeName)
                                    ? storeName
                                    : null,
                            SupplierCode = supplierCode,
                            SupplierName = supplierCode != null
                                && context.SupplierNameByCode.TryGetValue(
                                    supplierCode,
                                    out var supplierName
                                )
                                    ? supplierName
                                    : null,
                            PurchaseDate = reader.IsDBNull(5)
                                ? null
                                : Convert.ToDateTime(reader.GetValue(5)),
                            ProductCode = Convert.ToString(reader.GetValue(6)) ?? string.Empty,
                            ProductName = NullableString(7),
                            Quantity = Convert.ToDecimal(reader.GetValue(8)),
                            PurchasePrice = reader.IsDBNull(9)
                                ? null
                                : Convert.ToDecimal(reader.GetValue(9)),
                            Amount = Convert.ToDecimal(reader.GetValue(10)),
                        }
                    );
                }

                if (!await reader.NextResultAsync())
                {
                    throw new InvalidOperationException("SQL Server 当前商品未返回日趋势。");
                }

                var dailyRows = new List<LocalSupplierProductSalesDailyDto>();
                while (await reader.ReadAsync())
                {
                    dailyRows.Add(
                        new LocalSupplierProductSalesDailyDto
                        {
                            Date = Convert.ToDateTime(reader.GetValue(0)).Date,
                            PurchaseQuantity = Convert.ToDecimal(reader.GetValue(1)),
                            PurchaseAmount = Convert.ToDecimal(reader.GetValue(2)),
                            NetSalesQuantity = Convert.ToDecimal(reader.GetValue(3)),
                            NetSalesAmount = Convert.ToDecimal(reader.GetValue(4)),
                        }
                    );
                }

                if (!await reader.NextResultAsync())
                {
                    throw new InvalidOperationException("SQL Server 当前商品未返回分店排行。");
                }

                var branchRows = new List<LocalSupplierProductSalesBranchDto>();
                while (await reader.ReadAsync())
                {
                    var branchCode = Convert.ToString(reader.GetValue(0)) ?? string.Empty;
                    var quantity = Convert.ToDecimal(reader.GetValue(1));
                    var amount = Convert.ToDecimal(reader.GetValue(2));
                    branchRows.Add(
                        new LocalSupplierProductSalesBranchDto
                        {
                            BranchCode = branchCode,
                            BranchName = context.StoreNameByCode.TryGetValue(
                                branchCode,
                                out var branchName
                            )
                                ? branchName
                                : null,
                            NetSalesQuantity = quantity,
                            NetSalesAmount = amount,
                            AverageUnitPrice =
                                LocalSupplierProductSalesAnalysisLogic.CalculateAverageUnitPrice(
                                    quantity,
                                    amount
                                ),
                        }
                    );
                }

                return new LocalSupplierProductSalesCurrentBundle
                {
                    InvoiceDetails = new LocalSupplierProductSalesInvoiceDetailPageDto
                    {
                        Items = detailItems,
                        Total = detailTotal,
                        PageNumber = pageNumber,
                        PageSize = pageSize,
                    },
                    ProductDaily =
                        LocalSupplierProductSalesAnalysisLogic.BuildProductDailySeries(
                            dailyRows,
                            startDate,
                            endDate
                        ),
                    Branches = branchRows,
                };
            }
            finally
            {
                if (shouldClose)
                {
                    await connection.CloseAsync();
                }
            }
        }

        private async Task<List<LocalSupplierProductSalesDailyDto>> ComputeProductDailyFromCodeAsync(
            IReadOnlyList<string>? scopedStoreCodes,
            DateTime startDate,
            DateTime endDate,
            string currentProductCode
        )
        {
            var endDateExclusive = endDate.Date.AddDays(1);
            var stores = NormalizeStoreScopeForQuery(scopedStoreCodes);

            var headers = BuildActiveInvoiceHeaderQuery();
            var normalizedProductCode = currentProductCode.Trim().ToUpperInvariant();
            var purchaseQuery = _db
                .Queryable<StoreLocalSupplierInvoiceDetails>()
                .InnerJoin(headers, (detail, header) => detail.InvoiceGUID == header.InvoiceGUID)
                .Where((detail, header) =>
                    detail.IsDeleted == false
                    && detail.ProductCode != null
                    && detail.ProductCode == normalizedProductCode
                    && header.EffectivePurchaseDate >= startDate.Date
                    && header.EffectivePurchaseDate < endDateExclusive
                );
            if (stores is not null)
            {
                purchaseQuery = purchaseQuery.Where((detail, header) =>
                    stores.Contains(
                        SqlFunc.IIF(
                            detail.StoreCode != null && detail.StoreCode.Trim() != "",
                            detail.StoreCode!,
                            SqlFunc.IsNull(header.StoreCode, "")!
                        )
                    )
                );
            }

            var purchaseRows = await purchaseQuery
                .GroupBy((detail, header) =>
                    header.EffectivePurchaseDate.Date
                )
                .Select((detail, header) => new LocalSupplierProductSalesPurchaseDailyRow
                {
                    Date = header.EffectivePurchaseDate.Date,
                    PurchaseQuantity = SqlFunc.AggregateSum(detail.Quantity),
                    PurchaseAmount = SqlFunc.AggregateSum(
                        SqlFunc.IsNull(
                            detail.Amount,
                            SqlFunc.IsNull(detail.Quantity, 0m)
                                * SqlFunc.IsNull(detail.PurchasePrice, 0m)
                        )
                    ),
                })
                .ToListAsync();

            var salesQuery = _db
                .Queryable<ProductStoreDailySalesStatistic>()
                .Where(row =>
                    row.ProductCode == normalizedProductCode
                    && row.Date >= startDate.Date
                    && row.Date < endDateExclusive
                );
            if (stores is not null)
            {
                salesQuery = salesQuery.Where(row => stores.Contains(row.BranchCode));
            }

            var salesRows = await salesQuery
                .GroupBy(row => row.Date.Date)
                .Select(row => new LocalSupplierProductSalesSalesDailyRow
                {
                    Date = row.Date.Date,
                    NetSalesQuantity = SqlFunc.AggregateSum(row.TotalQuantity),
                    NetSalesAmount = SqlFunc.AggregateSum(row.TotalAmount),
                })
                .ToListAsync();

            var purchaseDaily = purchaseRows.ToDictionary(
                row => row.Date!.Value.Date,
                row => (row.PurchaseQuantity ?? 0m, row.PurchaseAmount ?? 0m)
            );
            var salesDaily = salesRows.ToDictionary(
                row => row.Date.Date,
                row => (row.NetSalesQuantity, row.NetSalesAmount)
            );
            var dates = purchaseDaily.Keys.Concat(salesDaily.Keys).Distinct().OrderBy(date => date);
            var rows = dates
                .Select(date =>
                {
                    purchaseDaily.TryGetValue(date, out var purchase);
                    salesDaily.TryGetValue(date, out var sale);
                    return new LocalSupplierProductSalesDailyDto
                    {
                        Date = date,
                        PurchaseQuantity = purchase.Item1,
                        PurchaseAmount = purchase.Item2,
                        NetSalesQuantity = sale.Item1,
                        NetSalesAmount = sale.Item2,
                    };
                })
                .ToList();

            return LocalSupplierProductSalesAnalysisLogic.BuildProductDailySeries(
                rows,
                startDate,
                endDate
            );
        }

        private async Task<LocalSupplierProductSalesInvoiceDetailPageDto>
            ComputeInvoiceDetailsFromCodeAsync(
                LocalSupplierProductSalesMasterContext context,
                LocalSupplierProductSalesAnalysisRequest request,
                IReadOnlyList<string>? scopedStoreCodes,
                DateTime startDate,
                DateTime endDate,
                string currentProductCode
            )
        {
            if (context.SupplierMetadataFailed || context.StoreMetadataFailed)
            {
                throw new InvalidOperationException("进货明细元数据不可用。");
            }

            var endDateExclusive = endDate.Date.AddDays(1);
            var stores = NormalizeStoreScopeForQuery(scopedStoreCodes);
            var headers = BuildActiveInvoiceHeaderQuery();
            var normalizedProductCode = currentProductCode.Trim().ToUpperInvariant();
            var query = _db
                .Queryable<StoreLocalSupplierInvoiceDetails>()
                .InnerJoin(headers, (detail, header) => detail.InvoiceGUID == header.InvoiceGUID)
                .Where((detail, header) =>
                    detail.IsDeleted == false
                    && detail.ProductCode != null
                    && detail.ProductCode == normalizedProductCode
                    && header.EffectivePurchaseDate >= startDate.Date
                    && header.EffectivePurchaseDate < endDateExclusive
                );
            if (stores is not null)
            {
                query = query.Where((detail, header) =>
                    stores.Contains(
                        SqlFunc.IIF(
                            detail.StoreCode != null && detail.StoreCode.Trim() != "",
                            detail.StoreCode!,
                            SqlFunc.IsNull(header.StoreCode, "")!
                        )
                    )
                );
            }

            var pageNumber = request.PageNumber <= 0 ? 1 : request.PageNumber;
            var pageSize = request.PageSize <= 0 ? 50 : Math.Min(request.PageSize, 500);
            var total = await query.Clone().CountAsync();
            var rows = await query
                .OrderBy(
                    (detail, header) => header.EffectivePurchaseDate,
                    OrderByType.Desc
                )
                .OrderBy((detail, header) => detail.DetailGUID, OrderByType.Asc)
                .Select((detail, header) => new LocalSupplierProductSalesDetailRow
                {
                    DetailGUID = detail.DetailGUID,
                    InvoiceGUID = detail.InvoiceGUID!,
                    InvoiceNo = header.InvoiceNo,
                    StoreCode = SqlFunc.IIF(
                        detail.StoreCode != null && detail.StoreCode.Trim() != "",
                        detail.StoreCode!.Trim(),
                        SqlFunc.IsNull(header.StoreCode, "")!.Trim()
                    ),
                    SupplierCode = SqlFunc.IIF(
                        detail.SupplierCode != null && detail.SupplierCode.Trim() != "",
                        detail.SupplierCode!.Trim(),
                        SqlFunc.IsNull(header.SupplierCode, "")!.Trim()
                    ),
                    PurchaseDate = header.EffectivePurchaseDate,
                    ProductCode = detail.ProductCode!,
                    ProductName = detail.ProductName,
                    Quantity = detail.Quantity,
                    PurchasePrice = detail.PurchasePrice,
                    Amount = SqlFunc.IsNull(
                        detail.Amount,
                        SqlFunc.IsNull(detail.Quantity, 0m)
                            * SqlFunc.IsNull(detail.PurchasePrice, 0m)
                    ),
                })
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            var items = rows
                .Select(row => new LocalSupplierProductSalesInvoiceDetailDto
                {
                    DetailGUID = row.DetailGUID,
                    InvoiceGUID = row.InvoiceGUID,
                    InvoiceNo = row.InvoiceNo,
                    StoreCode = row.StoreCode,
                    StoreName =
                        row.StoreCode != null
                        && context.StoreNameByCode.TryGetValue(row.StoreCode, out var storeName)
                            ? storeName
                            : null,
                    SupplierCode = row.SupplierCode,
                    SupplierName =
                        row.SupplierCode != null
                        && context.SupplierNameByCode.TryGetValue(row.SupplierCode, out var supplierName)
                            ? supplierName
                            : null,
                    PurchaseDate = row.PurchaseDate,
                    ProductCode = row.ProductCode,
                    ProductName = row.ProductName,
                    Quantity = row.Quantity ?? 0m,
                    PurchasePrice = row.PurchasePrice,
                    Amount = row.Amount ?? 0m,
                })
                .ToList();

            _logger.LogInformation(
                "本地商品销量分析进货明细 总行数={Total} 返回行数={Count}",
                total,
                items.Count
            );

            return new LocalSupplierProductSalesInvoiceDetailPageDto
            {
                Items = items,
                Total = total,
                PageNumber = pageNumber,
                PageSize = pageSize,
            };
        }

        private async Task<List<LocalSupplierProductSalesBranchDto>> ComputeBranchesFromCodeAsync(
            LocalSupplierProductSalesMasterContext context,
            IReadOnlyList<string>? scopedStoreCodes,
            DateTime startDate,
            DateTime endDate,
            string currentProductCode
        )
        {
            if (context.StoreMetadataFailed)
            {
                throw new InvalidOperationException("分店元数据不可用。");
            }

            var endDateExclusive = endDate.Date.AddDays(1);
            var stores = NormalizeStoreScopeForQuery(scopedStoreCodes);
            var normalizedProductCode = currentProductCode.Trim().ToUpperInvariant();
            var query = _db
                .Queryable<ProductStoreDailySalesStatistic>()
                .Where(row =>
                    row.ProductCode == normalizedProductCode
                    && row.Date >= startDate.Date
                    && row.Date < endDateExclusive
                );
            if (stores is not null)
            {
                query = query.Where(row => stores.Contains(row.BranchCode));
            }

            var rows = await query
                .GroupBy(row => row.BranchCode)
                .Select(row => new LocalSupplierProductSalesBranchAggregateRow
                {
                    BranchCode = row.BranchCode,
                    NetSalesQuantity = SqlFunc.AggregateSum(row.TotalQuantity),
                    NetSalesAmount = SqlFunc.AggregateSum(row.TotalAmount),
                })
                .ToListAsync();

            return rows
                .Select(row => new LocalSupplierProductSalesBranchDto
                {
                    BranchCode = row.BranchCode,
                    BranchName = context.StoreNameByCode.TryGetValue(row.BranchCode, out var name)
                        ? name
                        : null,
                    NetSalesQuantity = row.NetSalesQuantity,
                    NetSalesAmount = row.NetSalesAmount,
                    AverageUnitPrice =
                        LocalSupplierProductSalesAnalysisLogic.CalculateAverageUnitPrice(
                            row.NetSalesQuantity,
                            row.NetSalesAmount
                        ),
                })
                .OrderByDescending(row => row.NetSalesQuantity)
                .ThenBy(row => row.BranchCode, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task<LocalSupplierProductSalesMasterContext> LoadMasterContextAsync(
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            // 分类、供应商和门店名称不随授权范围或分析代际变化，独立缓存 60 秒。
            // forceRefresh 仍刷新业务查询，但不再让每一轮重读三张元数据表。
            const string scopeKey = "metadata";
            const long generation = 0;
            const string cacheKey = "lspa:master:v2";
            var response = await ComputeWithCacheAsync(
                cacheKey,
                scopeKey,
                generation,
                forceRefresh: false,
                () => LoadMasterContextCoreAsync(),
                cacheable: context => !context.HasMetadataFailures
            );
            if (!response.Success || response.Data == null)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(response.Message)
                        ? "本地商品主档加载失败。"
                        : response.Message
                );
            }

            return response.Data;
        }

        private async Task<LocalSupplierProductSalesMasterContext> LoadMasterContextCoreAsync()
        {
            // 这里只缓存分类、供应商和门店名称等小规模元数据；商品主档始终在数据库端过滤和分页。
            var categories = await _db
                .Queryable<WarehouseCategory>()
                .Where(category => category.IsDeleted == false && category.IsActive == true)
                .ToListAsync();
            var context = new LocalSupplierProductSalesMasterContext { Categories = categories };
            foreach (var category in categories)
            {
                context.CategoryNameByGuid[category.CategoryGUID] =
                    category.CategoryName ?? category.ChineseName ?? category.CategoryGUID;
            }

            try
            {
                var suppliers = await _db
                    .Queryable<HBLocalSupplier>()
                    .Where(supplier => supplier.IsDeleted == false)
                    .ToListAsync();
                foreach (var supplier in suppliers)
                {
                    var code = LocalSupplierProductSalesAnalysisLogic.NormalizeText(
                        supplier.LocalSupplierCode
                    );
                    if (code != null && !context.SupplierNameByCode.ContainsKey(code))
                    {
                        context.SupplierNameByCode[code] = supplier.Name ?? string.Empty;
                    }
                }
            }
            catch (Exception ex)
            {
                context.SupplierMetadataFailed = true;
                _logger.LogWarning(ex, "本地商品销量分析供应商元数据加载失败");
            }

            try
            {
                var stores = await _db
                    .Queryable<Store>()
                    .Where(store => store.IsDeleted == false)
                    .ToListAsync();
                foreach (var store in stores)
                {
                    var code = LocalSupplierProductSalesAnalysisLogic.NormalizeText(store.StoreCode);
                    if (code != null && !context.StoreNameByCode.ContainsKey(code))
                    {
                        context.StoreNameByCode[code] = store.StoreName ?? string.Empty;
                    }
                }
            }
            catch (Exception ex)
            {
                context.StoreMetadataFailed = true;
                _logger.LogWarning(ex, "本地商品销量分析分店元数据加载失败");
            }

            return context;
        }

        private ISugarQueryable<LocalSupplierProductSalesInvoiceHeaderRow>
            BuildActiveInvoiceHeaderQuery()
        {
            // 先把三级日期回退投影为派生列，再在外层按自然日过滤/分组。
            // 这样 SQL Server 能与持久化 EffectivePurchaseDate 计算列表达式对齐，
            // SQLite 测试也不会生成对 COALESCE 直接取 Date 的非法 SQL。
            return _db
                .Queryable<StoreLocalSupplierInvoice>()
                .Where(header => header.IsDeleted == false)
                .Select(header => new LocalSupplierProductSalesInvoiceHeaderRow
                {
                    InvoiceGUID = header.InvoiceGUID,
                    InvoiceNo = header.InvoiceNo,
                    Remarks = header.Remarks,
                    StoreCode = header.StoreCode,
                    SupplierCode = header.SupplierCode,
                    EffectivePurchaseDate =
                        header.InboundDate ?? header.OrderDate ?? header.CreatedAt,
                })
                .MergeTable();
        }

        /// <summary>
        /// 与过滤索引 IX_LSPSA_Product_ProductCode_UUID 的 WHERE 定义逐项一致的字面量谓词；
        /// 方括号标识符在 SQL Server 与测试用 SQLite 上均有效。
        /// </summary>
        internal const string EligibleLocalProductLiteralPredicate =
            "[IsDeleted] = 0 AND [IsActive] = 1"
            + " AND [LocalSupplierCode] IS NOT NULL AND [LocalSupplierCode] <> ''"
            + " AND [ProductCode] IS NOT NULL AND [ProductCode] <> ''";

        private static ISugarQueryable<Product> ApplySupplierCategoryToRawProducts(
            ISugarQueryable<Product> query,
            List<string> supplierCategoryGuids,
            List<string> warehouseCategoryGuids)
        {
            if (supplierCategoryGuids.Count == 0) return query;

            // 必须在 MergeTable 前关联原始 Product 别名；否则 SqlSugar 生成的相关子查询
            // 仍引用 product，而外层已经改名为 MergeTable。
            if (warehouseCategoryGuids.Count == 0)
            {
                return query.Where(product =>
                    product.LocalSupplierCode != null
                    && product.LocalSupplierCode != "200"
                    && SqlFunc.Subqueryable<LocalSupplierCategoryProductAssignment>()
                        .Where(assignment =>
                            assignment.ProductCode == product.ProductCode
                            && assignment.LocalSupplierCode == product.LocalSupplierCode
                            && supplierCategoryGuids.Contains(assignment.CategoryGUID)
                        ).Any()
                );
            }

            return query.Where(product =>
                (product.LocalSupplierCode == "200"
                    && product.WarehouseCategoryGUID != null
                    && warehouseCategoryGuids.Contains(product.WarehouseCategoryGUID))
                || (product.LocalSupplierCode != null
                    && product.LocalSupplierCode != "200"
                    && SqlFunc.Subqueryable<LocalSupplierCategoryProductAssignment>()
                        .Where(assignment =>
                            assignment.ProductCode == product.ProductCode
                            && assignment.LocalSupplierCode == product.LocalSupplierCode
                            && supplierCategoryGuids.Contains(assignment.CategoryGUID)
                        ).Any())
            );
        }

        private ISugarQueryable<Product> BuildCanonicalLocalProductQuery(
            List<string> supplierCategoryGuids,
            List<string> warehouseCategoryGuids)
        {
            // ProductCode 不是数据库主键；按 Trim + 不区分大小写编码固定最小 UUID，
            // 让过滤、计数、分页、选择和汇总使用完全一致的去重语义。
            // 判别谓词必须写成字面量：SqlSugar 会把 lambda 里的 false/true/"" 参数化，
            // SQL Server 无法在编译期证明参数化谓词蕴含过滤索引条件，
            // 带关键词/分类/供应商筛选的查询就会退化为两次 Product 宽表聚集扫描（线上实测 4.8 秒）。
            var eligibleProducts = _db
                .Queryable<Product>()
                .Where(EligibleLocalProductLiteralPredicate)
                .Where(product =>
                    product.ProductCode!.Trim() != ""
                    && product.LocalSupplierCode!.Trim() != ""
                );

            if (
                _db.CurrentConnectionConfig.DbType == DbType.SqlServer
                && _useDirectUniqueProductPath
            )
            {
                return ApplySupplierCategoryToRawProducts(
                    eligibleProducts, supplierCategoryGuids, warehouseCategoryGuids
                ).MergeTable();
            }

            // 生产 SQL Server 的编码列使用 CI 排序规则，且导入数据已保证无首尾空格。
            // 使用原始键才能命中过滤索引；SQLite 测试路径仍保留 Trim/Upper 兼容语义。
            if (_db.CurrentConnectionConfig.DbType == DbType.SqlServer)
            {
                var canonicalSqlServerIds = eligibleProducts
                    .GroupBy(product => product.ProductCode)
                    .Select(product => new
                    {
                        ProductCode = product.ProductCode!,
                        UUID = SqlFunc.AggregateMin(product.UUID),
                    })
                    .MergeTable();

                return ApplySupplierCategoryToRawProducts(
                        _db.Queryable<Product>(), supplierCategoryGuids, warehouseCategoryGuids
                    )
                    .InnerJoin(
                        canonicalSqlServerIds,
                        (product, canonical) =>
                            product.UUID == canonical.UUID
                            && product.ProductCode == canonical.ProductCode
                    )
                    .Select((product, canonical) => product)
                    .MergeTable();
            }

            var canonicalIds = eligibleProducts
                .GroupBy(product => product.ProductCode!.Trim().ToUpper())
                .Select(product => new
                {
                    ProductCode = product.ProductCode!.Trim().ToUpper(),
                    UUID = SqlFunc.AggregateMin(product.UUID),
                })
                .MergeTable();

            return ApplySupplierCategoryToRawProducts(
                    _db.Queryable<Product>(), supplierCategoryGuids, warehouseCategoryGuids
                )
                .InnerJoin(
                    canonicalIds,
                    (product, canonical) =>
                        product.UUID == canonical.UUID
                        && product.ProductCode!.Trim().ToUpper() == canonical.ProductCode
                )
                .Select((product, canonical) => product)
                .MergeTable();
        }

        private ISugarQueryable<Product> BuildFilteredProductQuery(
            LocalSupplierProductSalesMasterContext context,
            LocalSupplierProductSalesAnalysisFilterDto filter,
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            var supplierCategoryGuids = LocalSupplierProductSalesAnalysisLogic.NormalizeCodes(
                filter.SupplierCategoryGuids
            );
            var warehouseSupplierCategoryGuids = LocalSupplierProductSalesAnalysisLogic
                .ExpandCategoryGuids(context.Categories, supplierCategoryGuids).ToList();
            var expandedWarehouseCategoryGuids = LocalSupplierProductSalesAnalysisLogic
                .ExpandCategoryGuids(
                    context.Categories,
                    LocalSupplierProductSalesAnalysisLogic.ResolveCategoryGuids(filter)
                );
            LocalSupplierProductSalesAnalysisLogic.ValidateFilterCodeBudget(
                filter,
                warehouseSupplierCategoryGuids.Count + expandedWarehouseCategoryGuids.Count
            );
            var query = BuildCanonicalLocalProductQuery(
                supplierCategoryGuids, warehouseSupplierCategoryGuids
            );

            var keyword = LocalSupplierProductSalesAnalysisLogic.NormalizeText(filter.Keyword);
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                var normalizedKeyword = keyword.ToUpperInvariant();
                if (_db.CurrentConnectionConfig.DbType == DbType.SqlServer)
                {
                    // 合格商品约 17 万行，CI 排序规则下 5 列前导通配 LIKE 单次约 1.4 秒，
                    // 且同一请求内会被计数、分页、汇总重复执行；改为 UPPER + BIN2 逐字节比较后约 0.16 秒。
                    // 随后 MergeTable 封口，避免后续连接进货单派生表时未限定列名产生歧义。
                    return ApplyRemainingProductFilters(
                        query
                            .Where(
                                SqlServerKeywordPredicate,
                                new
                                {
                                    lspaKeywordPattern = BuildSqlServerLikePattern(
                                        normalizedKeyword
                                    ),
                                }
                            )
                            .MergeTable(),
                        context,
                        filter,
                        scopedStoreCodes
                    );
                }

                query = query.Where(product =>
                    product.ProductCode!.Trim().ToUpper().Contains(normalizedKeyword)
                    || (product.ItemNumber != null
                        && product.ItemNumber.Trim().ToUpper().Contains(normalizedKeyword))
                    || (product.Barcode != null
                        && product.Barcode.Trim().ToUpper().Contains(normalizedKeyword))
                    || (product.ProductName != null
                        && product.ProductName.Trim().ToUpper().Contains(normalizedKeyword))
                    || (product.EnglishName != null
                        && product.EnglishName.Trim().ToUpper().Contains(normalizedKeyword))
                );
            }

            return ApplyRemainingProductFilters(query, context, filter, scopedStoreCodes);
        }

        internal const string SqlServerBinaryCollation = "Latin1_General_100_BIN2";

        /// <summary>
        /// SQL Server 关键词谓词：与回退 lambda 同样匹配编码、货号、条码、中英文名称，
        /// 大小写不敏感由 UPPER 保证；BIN2 比较不再做全半角折叠，货号/条码检索不受影响。
        /// </summary>
        internal static readonly string SqlServerKeywordPredicate =
            "(" + SqlServerKeywordColumn("ProductCode")
            + " OR " + SqlServerKeywordColumn("ItemNumber")
            + " OR " + SqlServerKeywordColumn("Barcode")
            + " OR " + SqlServerKeywordColumn("ProductName")
            + " OR " + SqlServerKeywordColumn("EnglishName") + ")";

        /// <summary>
        /// 先转 nvarchar 再套 BIN2：列若是 varchar，直接 COLLATE 到 Latin1 会按 1252 代码页重解释，
        /// 中文名称会变成问号而永远匹配不到；nvarchar 与代码页无关，转换后再逐字节比较才安全。
        /// </summary>
        private static string SqlServerKeywordColumn(string column) =>
            "UPPER(CAST([" + column + "] AS nvarchar(4000))) COLLATE "
            + SqlServerBinaryCollation + " LIKE @lspaKeywordPattern";

        /// <summary>把用户输入里的 LIKE 通配符按方括号转义后包成包含匹配。</summary>
        internal static string BuildSqlServerLikePattern(string normalizedKeyword)
        {
            var escaped = normalizedKeyword
                .Replace("[", "[[]", StringComparison.Ordinal)
                .Replace("%", "[%]", StringComparison.Ordinal)
                .Replace("_", "[_]", StringComparison.Ordinal);
            return "%" + escaped + "%";
        }

        private ISugarQueryable<Product> ApplyRemainingProductFilters(
            ISugarQueryable<Product> query,
            LocalSupplierProductSalesMasterContext context,
            LocalSupplierProductSalesAnalysisFilterDto filter,
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            var categoryGuids = LocalSupplierProductSalesAnalysisLogic.ResolveCategoryGuids(
                filter
            );
            if (categoryGuids.Count > 0)
            {
                var expanded = LocalSupplierProductSalesAnalysisLogic.ExpandCategoryGuids(
                    context.Categories,
                    categoryGuids
                )
                    .Select(guid => guid.ToUpperInvariant())
                    .ToList();
                query = query.Where(product =>
                    product.WarehouseCategoryGUID != null
                    && expanded.Contains(product.WarehouseCategoryGUID.Trim().ToUpper())
                );
            }

            var supplierCodes = LocalSupplierProductSalesAnalysisLogic.ResolveSupplierCodes(
                filter
            );
            if (supplierCodes.Count > 0)
            {
                var supplierSet = supplierCodes
                    .Select(code => code.ToUpperInvariant())
                    .ToList();
                query = query.Where(product =>
                    product.LocalSupplierCode != null
                    && supplierSet.Contains(product.LocalSupplierCode.Trim().ToUpper())
                );
            }

            var documentKeyword = LocalSupplierProductSalesAnalysisLogic.NormalizeText(
                filter.DocumentKeyword
            );
            if (!string.IsNullOrWhiteSpace(documentKeyword))
            {
                var documentCodes = BuildDocumentProductCodeQuery(
                    documentKeyword,
                    scopedStoreCodes
                );
                query = query
                    .InnerJoin(
                        documentCodes,
                        (product, document) => product.ProductCode == document.ProductCode
                    )
                    .Select((product, document) => product)
                    .MergeTable();
            }

            return query;
        }

        private ISugarQueryable<LocalSupplierProductSalesProductCodeRow>
            BuildDocumentProductCodeQuery(
            string documentKeyword,
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            var headers = BuildActiveInvoiceHeaderQuery();
            var query = _db
                .Queryable<StoreLocalSupplierInvoiceDetails>()
                .InnerJoin(headers, (detail, header) => detail.InvoiceGUID == header.InvoiceGUID)
                .Where((detail, header) =>
                    detail.IsDeleted == false
                    && detail.ProductCode != null
                    && (
                        (header.InvoiceNo != null && header.InvoiceNo.Contains(documentKeyword))
                        || (header.Remarks != null && header.Remarks.Contains(documentKeyword))
                    )
                );

            var stores = NormalizeStoreScopeForQuery(scopedStoreCodes);
            if (stores is not null)
            {
                query = query.Where((detail, header) =>
                    stores.Contains(
                        SqlFunc.IIF(
                            detail.StoreCode != null && detail.StoreCode.Trim() != "",
                            detail.StoreCode!.Trim().ToUpper(),
                            SqlFunc.IsNull(header.StoreCode, "")!.Trim().ToUpper()
                        )
                    )
                );
            }

            return query
                .Select((detail, header) => new LocalSupplierProductSalesProductCodeRow
                {
                    ProductCode = detail.ProductCode!,
                })
                .Distinct()
                .MergeTable();
        }

        private async Task<
            LocalSupplierProductSalesPagedDto<LocalSupplierProductSalesCandidateDto>
        > QueryCandidatesAsync(
            ISugarQueryable<Product> filteredProducts,
            LocalSupplierProductSalesMasterContext context,
            LocalSupplierProductSalesAnalysisRequest request
        )
        {
            var pageNumber = request.CandidatePageNumber > 0
                ? request.CandidatePageNumber
                : request.PageNumber;
            var pageSize = request.CandidatePageSize > 0
                ? request.CandidatePageSize
                : request.PageSize;
            pageNumber = pageNumber <= 0 ? 1 : pageNumber;
            pageSize = pageSize <= 0 ? 50 : Math.Min(pageSize, 500);

            if (
                _db.CurrentConnectionConfig.DbType == DbType.SqlServer
                && _useDirectUniqueProductPath
                && IsProductFilterEmpty(request.Filter)
            )
            {
                return await QueryFastSqlServerCandidatesAsync(
                    context,
                    pageNumber,
                    pageSize
                );
            }

            var total = _useDirectUniqueProductPath
                && IsProductFilterEmpty(request.Filter)
                && _directEligibleProductCount >= 0
                    ? _directEligibleProductCount
                    : await filteredProducts.Clone().CountAsync();
            var orderedProducts = filteredProducts.Clone();
            orderedProducts = _db.CurrentConnectionConfig.DbType == DbType.SqlServer
                ? orderedProducts.OrderBy(product => product.ProductCode, OrderByType.Asc)
                : orderedProducts.OrderBy(
                    product => product.ProductCode!.Trim().ToUpper(),
                    OrderByType.Asc
                );
            var rows = await ProjectMasterInfo(
                    orderedProducts
                        .OrderBy(product => product.UUID, OrderByType.Asc)
                )
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return new LocalSupplierProductSalesPagedDto<LocalSupplierProductSalesCandidateDto>
            {
                Items = rows.Select(row => BuildCandidate(context, row)).ToList(),
                Total = total,
                PageNumber = pageNumber,
                PageSize = pageSize,
            };
        }

        private async Task<
            LocalSupplierProductSalesPagedDto<LocalSupplierProductSalesCandidateDto>
        > QueryFastSqlServerCandidatesAsync(
            LocalSupplierProductSalesMasterContext context,
            int pageNumber,
            int pageSize
        )
        {
            const string sql = @"
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;

SELECT
    [product].[UUID],
    LTRIM(RTRIM([product].[ProductCode])) AS [ProductCode],
    [product].[ItemNumber],
    [product].[Barcode],
    [product].[ProductName],
    [product].[EnglishName],
    [product].[ProductImage] AS [ImageUrl],
    [product].[WarehouseCategoryGUID] AS [WarehouseCategoryGuid],
    [product].[LocalSupplierCode]
FROM [dbo].[Product] AS [product]
    WITH (INDEX([IX_LSPSA_Product_ProductCode_UUID]))
WHERE [product].[IsDeleted] = 0
  AND [product].[IsActive] = 1
  AND [product].[ProductCode] IS NOT NULL
  AND [product].[ProductCode] <> N''
  AND [product].[LocalSupplierCode] IS NOT NULL
  AND [product].[LocalSupplierCode] <> N''
ORDER BY [product].[ProductCode] ASC, [product].[UUID] ASC
OFFSET @__lspsaCandidateOffset ROWS
FETCH NEXT @__lspsaCandidatePageSize ROWS ONLY;";
            var rows = await _db.Ado.SqlQueryAsync<LocalSupplierProductSalesMasterInfo>(
                sql,
                new SugarParameter(
                    "@__lspsaCandidateOffset",
                    (pageNumber - 1) * pageSize
                ),
                new SugarParameter("@__lspsaCandidatePageSize", pageSize)
            );
            return new LocalSupplierProductSalesPagedDto<LocalSupplierProductSalesCandidateDto>
            {
                Items = rows.Select(row => BuildCandidate(context, row)).ToList(),
                Total = _directEligibleProductCount,
                PageNumber = pageNumber,
                PageSize = pageSize,
            };
        }

        private async Task<LocalSupplierProductSalesCandidateDto?> QueryCandidateAsync(
            ISugarQueryable<Product> selectedProducts,
            LocalSupplierProductSalesMasterContext context,
            string productCode
        )
        {
            var normalized = productCode.Trim().ToUpperInvariant();
            var rows = await ProjectMasterInfo(
                    selectedProducts
                        .Clone()
                        .Where(product => product.ProductCode == normalized)
                        .OrderBy(product => product.UUID, OrderByType.Asc)
                )
                .Take(1)
                .ToListAsync();
            return rows.Count == 0 ? null : BuildCandidate(context, rows[0]);
        }

        private static ISugarQueryable<LocalSupplierProductSalesMasterInfo> ProjectMasterInfo(
            ISugarQueryable<Product> query
        )
        {
            return query.Select(product => new LocalSupplierProductSalesMasterInfo
            {
                UUID = product.UUID,
                ProductCode = product.ProductCode!.Trim(),
                ItemNumber = product.ItemNumber,
                Barcode = product.Barcode,
                ProductName = product.ProductName,
                EnglishName = product.EnglishName,
                ImageUrl = product.ProductImage,
                WarehouseCategoryGuid = product.WarehouseCategoryGUID,
                LocalSupplierCode = product.LocalSupplierCode,
            });
        }

        private async Task<LocalSupplierProductSalesEffectiveSelectionDto> CleanSelectionAsync(
            ISugarQueryable<Product> filteredProducts,
            LocalSupplierProductSalesSelectionDto? selection
        )
        {
            var includedMode = string.Equals(
                selection?.Mode,
                "included",
                StringComparison.OrdinalIgnoreCase
            );
            var requested = LocalSupplierProductSalesAnalysisLogic.NormalizeCodes(
                includedMode
                    ? selection?.IncludedProductCodes
                    : selection?.ExcludedProductCodes
            );
            var effective = new LocalSupplierProductSalesEffectiveSelectionDto
            {
                Mode = includedMode ? "included" : "allFiltered",
            };
            if (requested.Count == 0)
            {
                if (includedMode)
                {
                    effective.IncludedProductCodes = new List<string>();
                }
                else
                {
                    effective.ExcludedProductCodes = new List<string>();
                }

                return effective;
            }

            var normalizedRequested = requested
                .Select(code => code.ToUpperInvariant())
                .ToList();
            var rows = await filteredProducts
                .Clone()
                .Where(product => normalizedRequested.Contains(product.ProductCode!))
                .Select(product => new LocalSupplierProductSalesProductCodeRow
                {
                    ProductCode = product.ProductCode!.Trim(),
                })
                .ToListAsync();
            var canonicalByCode = rows
                .GroupBy(row => row.ProductCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().ProductCode,
                    StringComparer.OrdinalIgnoreCase
                );
            var cleaned = requested
                .Where(canonicalByCode.ContainsKey)
                .Select(code => canonicalByCode[code])
                .ToList();
            if (includedMode)
            {
                effective.IncludedProductCodes = cleaned;
            }
            else
            {
                effective.ExcludedProductCodes = cleaned;
            }

            return effective;
        }

        private static ISugarQueryable<Product> ApplySelectionQuery(
            ISugarQueryable<Product> filteredProducts,
            LocalSupplierProductSalesSelectionDto selection
        )
        {
            var includedMode = string.Equals(
                selection.Mode,
                "included",
                StringComparison.OrdinalIgnoreCase
            );
            var requested = LocalSupplierProductSalesAnalysisLogic
                .NormalizeCodes(
                    includedMode
                        ? selection.IncludedProductCodes
                        : selection.ExcludedProductCodes
                )
                .Select(code => code.ToUpperInvariant())
                .ToList();
            if (includedMode)
            {
                return requested.Count == 0
                    ? filteredProducts.Clone().Where(_ => false)
                    : filteredProducts
                        .Clone()
                        .Where(product => requested.Contains(product.ProductCode!));
            }

            return requested.Count == 0
                ? filteredProducts.Clone()
                : filteredProducts
                    .Clone()
                    .Where(product => !requested.Contains(product.ProductCode!));
        }

        private async Task<LocalSupplierProductSalesSelectionState> ResolveSelectionAsync(
            ISugarQueryable<Product> filteredProducts,
            LocalSupplierProductSalesSelectionDto? selection,
            string? currentProductCode,
            bool autoSelectFirst
        )
        {
            var effective = await CleanSelectionAsync(filteredProducts, selection);
            var selectedProducts = ApplySelectionQuery(filteredProducts, effective);
            var current = LocalSupplierProductSalesAnalysisLogic.NormalizeText(currentProductCode);
            string? resolvedCurrent = null;
            if (current != null)
            {
                var normalizedCurrent = current.ToUpperInvariant();
                var matches = await selectedProducts
                    .Clone()
                    .Where(product => product.ProductCode == normalizedCurrent)
                    .OrderBy(product => product.UUID, OrderByType.Asc)
                    .Select(product => new LocalSupplierProductSalesProductCodeRow
                    {
                        ProductCode = product.ProductCode!.Trim(),
                    })
                    .Take(1)
                    .ToListAsync();
                resolvedCurrent = matches.FirstOrDefault()?.ProductCode;
            }

            if (resolvedCurrent == null && (autoSelectFirst || current != null))
            {
                var first = await selectedProducts
                    .Clone()
                    .OrderBy(product => product.ProductCode, OrderByType.Asc)
                    .OrderBy(product => product.UUID, OrderByType.Asc)
                    .Select(product => new LocalSupplierProductSalesProductCodeRow
                    {
                        ProductCode = product.ProductCode!.Trim(),
                    })
                    .Take(1)
                    .ToListAsync();
                resolvedCurrent = first.FirstOrDefault()?.ProductCode;
            }

            return new LocalSupplierProductSalesSelectionState
            {
                Selection = effective,
                CurrentProductCode = resolvedCurrent,
            };
        }

        private async Task<bool> IsCurrentProductSelectedAsync(
            LocalSupplierProductSalesMasterContext context,
            LocalSupplierProductSalesAnalysisRequest request,
            string currentProductCode,
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            var filteredProducts = BuildFilteredProductQuery(
                context,
                request.Filter,
                scopedStoreCodes
            );
            var effective = await CleanSelectionAsync(filteredProducts, request.Selection);
            var selectedProducts = ApplySelectionQuery(filteredProducts, effective);
            var normalizedCurrent = currentProductCode.ToUpperInvariant();
            return await selectedProducts.AnyAsync(product =>
                product.ProductCode == normalizedCurrent
            );
        }

        private async Task<LocalSupplierProductSalesOptionsDto> BuildOptionsAsync(
            LocalSupplierProductSalesMasterContext context
        )
        {
            if (context.SupplierMetadataFailed)
            {
                throw new InvalidOperationException("供应商元数据不可用。");
            }

            // SQL Server 主路径直接使用本地商品过滤覆盖索引；避免 ORM 为少量供应商选项
            // 生成额外的 canonical 子查询与实体映射。
            var supplierCodes = _db.CurrentConnectionConfig.DbType == DbType.SqlServer
                && _useDirectUniqueProductPath
                && _hasFastSqlServerSchema
                ? await QueryFastSqlServerSupplierCodesAsync()
                : (
                    await _db
                        .Queryable<Product>()
                        .Where(product =>
                            product.IsDeleted == false
                            && product.IsActive == true
                            && product.ProductCode != null
                            && product.ProductCode != ""
                            && product.ProductCode.Trim() != ""
                            && product.LocalSupplierCode != null
                            && product.LocalSupplierCode != ""
                            && product.LocalSupplierCode.Trim() != ""
                        )
                        .Select(product => new LocalSupplierProductSalesProductCodeRow
                        {
                            ProductCode = product.LocalSupplierCode!.Trim(),
                        })
                        .Distinct()
                        .ToListAsync()
                )
                    .Select(row => row.ProductCode)
                    .ToList();
            var suppliers = supplierCodes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .Select(code => new LocalSupplierProductSalesSupplierOptionDto
                {
                    Code = code,
                    Name = context.SupplierNameByCode.TryGetValue(code, out var name)
                        ? name
                        : null,
                })
                .ToList();
            var categories = context.Categories
                .Select(category => new LocalSupplierProductSalesCategoryOptionDto
                {
                    Guid = category.CategoryGUID,
                    Name = category.CategoryName ?? category.ChineseName,
                })
                .OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new LocalSupplierProductSalesOptionsDto
            {
                WarehouseCategories = categories,
                Suppliers = suppliers,
            };
        }

        private async Task<List<string>> QueryFastSqlServerSupplierCodesAsync()
        {
            const string sql = @"
SET NOCOUNT ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;

SELECT DISTINCT LTRIM(RTRIM([product].[LocalSupplierCode])) AS [SupplierCode]
FROM [dbo].[Product] AS [product]
    WITH (INDEX([IX_LSPSA_Product_ProductCode_UUID]))
WHERE [product].[IsDeleted] = 0
  AND [product].[IsActive] = 1
  AND [product].[ProductCode] IS NOT NULL
  AND [product].[ProductCode] <> N''
  AND [product].[LocalSupplierCode] IS NOT NULL
  AND [product].[LocalSupplierCode] <> N'';";

            var connection = (System.Data.Common.DbConnection)_db.Ado.Connection;
            var shouldClose = connection.State != System.Data.ConnectionState.Open;
            if (shouldClose)
            {
                await connection.OpenAsync();
            }

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.CommandTimeout = 10;
                await using var reader = await command.ExecuteReaderAsync();
                var supplierCodes = new List<string>();
                while (await reader.ReadAsync())
                {
                    if (!reader.IsDBNull(0))
                    {
                        supplierCodes.Add(Convert.ToString(reader.GetValue(0)) ?? string.Empty);
                    }
                }

                return supplierCodes;
            }
            finally
            {
                if (shouldClose)
                {
                    await connection.CloseAsync();
                }
            }
        }

        private ISugarQueryable<LocalSupplierProductSalesPurchaseAggregateRow>
            BuildBroadPurchaseAggregateQuery(
            IReadOnlyList<string>? scopedStoreCodes,
            DateTime startDate,
            DateTime endDate
        )
        {
            var endDateExclusive = endDate.Date.AddDays(1);
            var stores = NormalizeStoreScopeForQuery(scopedStoreCodes);
            var headers = BuildActiveInvoiceHeaderQuery();
            var query = _db
                .Queryable<StoreLocalSupplierInvoiceDetails>()
                .InnerJoin(headers, (detail, header) => detail.InvoiceGUID == header.InvoiceGUID)
                .Where((detail, header) =>
                    detail.IsDeleted == false
                    && detail.ProductCode != null
                    && detail.ProductCode != ""
                    && detail.ProductCode.Trim() != ""
                    && header.EffectivePurchaseDate.Date >= startDate.Date
                    && header.EffectivePurchaseDate.Date < endDateExclusive
                );
            if (stores is not null)
            {
                query = query.Where((detail, header) =>
                    stores.Contains(
                        SqlFunc.IIF(
                            detail.StoreCode != null && detail.StoreCode.Trim() != "",
                            detail.StoreCode!,
                            SqlFunc.IsNull(header.StoreCode, "")!
                        )
                    )
                );
            }

            return query
                .GroupBy((detail, header) => detail.ProductCode)
                .Select((detail, header) => new LocalSupplierProductSalesPurchaseAggregateRow
                {
                    ProductCode = detail.ProductCode!,
                    Quantity = SqlFunc.AggregateSum(detail.Quantity),
                    Amount = SqlFunc.AggregateSum(
                        SqlFunc.IsNull(
                            detail.Amount,
                            SqlFunc.IsNull(detail.Quantity, 0m)
                                * SqlFunc.IsNull(detail.PurchasePrice, 0m)
                        )
                    ),
                })
                .MergeTable();
        }

        private ISugarQueryable<LocalSupplierProductSalesPurchaseAggregateRow>
            BuildPurchaseAggregateQuery(
            IReadOnlyList<string>? scopedStoreCodes,
            DateTime startDate,
            DateTime endDate,
            ISugarQueryable<Product> selectedProducts
        )
        {
            var endDateExclusive = endDate.Date.AddDays(1);
            var stores = NormalizeStoreScopeForQuery(scopedStoreCodes);
            var headers = BuildActiveInvoiceHeaderQuery();
            var selectedCodes = selectedProducts
                .Clone()
                .Select(product => new LocalSupplierProductSalesProductCodeRow
                {
                    ProductCode = product.ProductCode!,
                })
                .MergeTable();
            var query = _db
                .Queryable<StoreLocalSupplierInvoiceDetails>()
                .InnerJoin(headers, (detail, header) => detail.InvoiceGUID == header.InvoiceGUID)
                .InnerJoin(
                    selectedCodes,
                    (detail, header, selected) => detail.ProductCode == selected.ProductCode
                )
                .Where((detail, header, selected) =>
                    detail.IsDeleted == false
                    && detail.ProductCode != null
                    && detail.ProductCode != ""
                    && detail.ProductCode.Trim() != ""
                    && header.EffectivePurchaseDate.Date >= startDate.Date
                    && header.EffectivePurchaseDate.Date < endDateExclusive
                );
            if (stores is not null)
            {
                query = query.Where((detail, header, selected) =>
                    stores.Contains(
                        SqlFunc.IIF(
                            detail.StoreCode != null && detail.StoreCode.Trim() != "",
                            detail.StoreCode!,
                            SqlFunc.IsNull(header.StoreCode, "")!
                        )
                    )
                );
            }

            return query
                .GroupBy((detail, header, selected) => selected.ProductCode)
                .Select((detail, header, selected) => new LocalSupplierProductSalesPurchaseAggregateRow
                {
                    ProductCode = selected.ProductCode,
                    Quantity = SqlFunc.AggregateSum(detail.Quantity),
                    Amount = SqlFunc.AggregateSum(
                        SqlFunc.IsNull(
                            detail.Amount,
                            SqlFunc.IsNull(detail.Quantity, 0m)
                                * SqlFunc.IsNull(detail.PurchasePrice, 0m)
                        )
                    ),
                })
                .MergeTable();
        }

        private ISugarQueryable<LocalSupplierProductSalesSalesAggregateRow>
            BuildBroadSalesAggregateQuery(
            IReadOnlyList<string>? scopedStoreCodes,
            DateTime startDate,
            DateTime endDate
        )
        {
            var endDateExclusive = endDate.Date.AddDays(1);
            var stores = NormalizeStoreScopeForQuery(scopedStoreCodes);
            var query = _db
                .Queryable<ProductStoreDailySalesStatistic>()
                .Where(row =>
                    row.Date >= startDate.Date
                    && row.Date < endDateExclusive
                    && row.ProductCode != null
                    && row.ProductCode != ""
                    && row.ProductCode.Trim() != ""
                );
            if (stores is not null)
            {
                query = query.Where(row => stores.Contains(row.BranchCode));
            }

            return query
                .GroupBy(row => row.ProductCode)
                .Select(row => new LocalSupplierProductSalesSalesAggregateRow
                {
                    ProductCode = row.ProductCode,
                    Quantity = SqlFunc.AggregateSum(row.TotalQuantity),
                    Amount = SqlFunc.AggregateSum(row.TotalAmount),
                })
                .MergeTable();
        }

        private ISugarQueryable<LocalSupplierProductSalesSalesAggregateRow>
            BuildSalesAggregateQuery(
            IReadOnlyList<string>? scopedStoreCodes,
            DateTime startDate,
            DateTime endDate,
            ISugarQueryable<Product> selectedProducts
        )
        {
            var endDateExclusive = endDate.Date.AddDays(1);
            var stores = NormalizeStoreScopeForQuery(scopedStoreCodes);
            var selectedCodes = selectedProducts
                .Clone()
                .Select(product => new LocalSupplierProductSalesProductCodeRow
                {
                    ProductCode = product.ProductCode!,
                })
                .MergeTable();
            var query = _db
                .Queryable<ProductStoreDailySalesStatistic>()
                .InnerJoin(
                    selectedCodes,
                    (row, selected) => row.ProductCode == selected.ProductCode
                )
                .Where((row, selected) =>
                    row.Date >= startDate.Date
                    && row.Date < endDateExclusive
                    && row.ProductCode != null
                    && row.ProductCode != ""
                    && row.ProductCode.Trim() != ""
                );
            if (stores is not null)
            {
                query = query.Where((row, selected) => stores.Contains(row.BranchCode));
            }

            return query
                .GroupBy((row, selected) => selected.ProductCode)
                .Select((row, selected) => new LocalSupplierProductSalesSalesAggregateRow
                {
                    ProductCode = selected.ProductCode,
                    Quantity = SqlFunc.AggregateSum(row.TotalQuantity),
                    Amount = SqlFunc.AggregateSum(row.TotalAmount),
                })
                .MergeTable();
        }

        private static LocalSupplierProductSalesSummaryRowDto BuildSummaryRow(
            LocalSupplierProductSalesMasterContext context,
            LocalSupplierProductSalesSummarySqlRow row
        )
        {
            return new LocalSupplierProductSalesSummaryRowDto
            {
                ProductCode = row.ProductCode,
                ItemNumber = row.ItemNumber,
                Barcode = row.Barcode,
                ProductName = row.ProductName,
                ImageUrl = row.ImageUrl,
                WarehouseCategoryGuid = row.WarehouseCategoryGuid,
                WarehouseCategoryName =
                    row.WarehouseCategoryGuid != null
                    && context.CategoryNameByGuid.TryGetValue(
                        row.WarehouseCategoryGuid,
                        out var categoryName
                    )
                        ? categoryName
                        : null,
                Suppliers = BuildSuppliers(row.LocalSupplierCode, context),
                PurchaseQuantity = row.PurchaseQuantity,
                PurchaseAmount = row.PurchaseAmount,
                NetSalesQuantity = row.NetSalesQuantity,
                NetSalesAmount = row.NetSalesAmount,
                SellThroughRate =
                    LocalSupplierProductSalesAnalysisLogic.CalculateSellThroughRate(
                        row.PurchaseQuantity,
                        row.NetSalesQuantity
                    ),
            };
        }

        private static List<LocalSupplierProductSalesSupplierRefDto> BuildSuppliers(
            string? supplierCode,
            LocalSupplierProductSalesMasterContext context
        )
        {
            if (string.IsNullOrWhiteSpace(supplierCode))
            {
                return new List<LocalSupplierProductSalesSupplierRefDto>();
            }

            return new List<LocalSupplierProductSalesSupplierRefDto>
            {
                new()
                {
                    Code = supplierCode,
                    Name = context.SupplierNameByCode.TryGetValue(supplierCode, out var name)
                        ? name
                        : null,
                },
            };
        }

        private static LocalSupplierProductSalesCandidateDto BuildCandidate(
            LocalSupplierProductSalesMasterContext context,
            LocalSupplierProductSalesMasterInfo info
        )
        {
            return new LocalSupplierProductSalesCandidateDto
            {
                ProductCode = info.ProductCode,
                ItemNumber = info.ItemNumber,
                Barcode = info.Barcode,
                ProductName = info.ProductName,
                ImageUrl = info.ImageUrl,
                WarehouseCategoryGuid = info.WarehouseCategoryGuid,
                WarehouseCategoryName =
                    info.WarehouseCategoryGuid != null
                    && context.CategoryNameByGuid.TryGetValue(
                        info.WarehouseCategoryGuid,
                        out var categoryName
                    )
                        ? categoryName
                        : null,
            };
        }

        private static ISugarQueryable<LocalSupplierProductSalesSummarySqlRow> ApplySummarySort(
            ISugarQueryable<LocalSupplierProductSalesSummarySqlRow> query,
            string? sortBy,
            string? sortDirection
        )
        {
            var orderType = string.Equals(
                sortDirection,
                "asc",
                StringComparison.OrdinalIgnoreCase
            )
                ? OrderByType.Asc
                : OrderByType.Desc;
            var field = string.IsNullOrWhiteSpace(sortBy)
                ? "netsalesquantity"
                : sortBy.Trim().ToLowerInvariant();
            query = field switch
            {
                "purchasequantity" => query.OrderBy(row => row.PurchaseQuantity, orderType),
                "purchaseamount" => query.OrderBy(row => row.PurchaseAmount, orderType),
                "netsalesamount" => query.OrderBy(row => row.NetSalesAmount, orderType),
                "sellthroughrate" => query.OrderBy(
                    row =>
                        SqlFunc.IIF(
                            row.PurchaseQuantity == 0m,
                            0m,
                            row.NetSalesQuantity / row.PurchaseQuantity
                        ),
                    orderType
                ),
                "productcode" => query.OrderBy(row => row.ProductCode, orderType),
                _ => query.OrderBy(row => row.NetSalesQuantity, orderType),
            };
            if (field != "productcode")
            {
                query = query.OrderBy(row => row.ProductCode, OrderByType.Asc);
            }

            return query.OrderBy(row => row.UUID, OrderByType.Asc);
        }

        private static bool IsEmptyScope(IReadOnlyList<string>? scopedStoreCodes)
        {
            return scopedStoreCodes is not null && scopedStoreCodes.Count == 0;
        }

        private static bool IsBroadUnfilteredSelection(
            LocalSupplierProductSalesAnalysisRequest request
        )
        {
            var selection = request.Selection;
            return string.Equals(
                    selection.Mode,
                    "allFiltered",
                    StringComparison.OrdinalIgnoreCase
                )
                && LocalSupplierProductSalesAnalysisLogic.NormalizeCodes(
                    selection.ExcludedProductCodes
                ).Count == 0
                && IsProductFilterEmpty(request.Filter);
        }

        private static bool IsProductFilterEmpty(
            LocalSupplierProductSalesAnalysisFilterDto filter
        )
        {
            return string.IsNullOrWhiteSpace(filter.Keyword)
                && string.IsNullOrWhiteSpace(filter.CategoryGuid)
                && string.IsNullOrWhiteSpace(filter.SupplierCode)
                && string.IsNullOrWhiteSpace(filter.DocumentKeyword)
                && LocalSupplierProductSalesAnalysisLogic.NormalizeCodes(
                    filter.WarehouseCategoryGuids
                ).Count == 0
                && LocalSupplierProductSalesAnalysisLogic.NormalizeCodes(filter.SupplierCodes)
                    .Count == 0
                && LocalSupplierProductSalesAnalysisLogic.NormalizeCodes(filter.SupplierCategoryGuids)
                    .Count == 0;
        }

        private static List<string>? NormalizeStoreScopeForQuery(
            IReadOnlyList<string>? scopedStoreCodes
        )
        {
            return scopedStoreCodes is null
                ? null
                : LocalSupplierProductSalesAnalysisLogic
                    .NormalizeCodes(scopedStoreCodes)
                    .Select(code => code.ToUpperInvariant())
                    .ToList();
        }

        private long GetGeneration(string scopeKey, bool forceRefresh)
        {
            lock (_cacheCoordinator.Gate)
            {
                var current = GetGenerationInsideLock(scopeKey);
                if (!forceRefresh)
                {
                    return current;
                }

                var next = current + 1L;
                _cacheCoordinator.Generations[scopeKey] = next;
                return next;
            }
        }

        private long GetGenerationInsideLock(string scopeKey)
        {
            if (_cacheCoordinator.Generations.TryGetValue(scopeKey, out var generation))
            {
                return generation;
            }

            _cacheCoordinator.Generations[scopeKey] = 0L;
            return 0L;
        }

        private static string BuildStoreScopeCachePart(IReadOnlyList<string>? scopedStoreCodes)
        {
            if (scopedStoreCodes is null)
            {
                return "<all>";
            }

            var normalized = LocalSupplierProductSalesAnalysisLogic
                .NormalizeCodes(scopedStoreCodes)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return normalized.Count == 0 ? "<none>" : string.Join(",", normalized);
        }

        private static string BuildCacheKey(
            string segment,
            LocalSupplierProductSalesAnalysisRequest request,
            IReadOnlyList<string>? scopedStoreCodes,
            long generation
        )
        {
            var scopeKey = BuildStoreScopeCachePart(scopedStoreCodes);
            return "lspa:" + segment + ":" + generation + ":" + HashParts(scopeKey + "|" + BuildRequestSignature(request));
        }

        private static string BuildRequestSignature(LocalSupplierProductSalesAnalysisRequest request)
        {
            static string TextPart(string? value) =>
                LocalSupplierProductSalesAnalysisLogic.NormalizeText(value)?.ToUpperInvariant()
                ?? string.Empty;
            static string CodeListPart(IEnumerable<string>? values) =>
                string.Join(
                    ",",
                    LocalSupplierProductSalesAnalysisLogic
                        .NormalizeCodes(values)
                        .Select(code => code.ToUpperInvariant())
                        .OrderBy(code => code, StringComparer.Ordinal)
                );

            var parts = new[]
            {
                request.Filter.StartDate.ToString("yyyy-MM-dd"),
                request.Filter.EndDate.ToString("yyyy-MM-dd"),
                TextPart(request.Filter.Keyword),
                TextPart(request.Filter.CategoryGuid),
                CodeListPart(request.Filter.WarehouseCategoryGuids),
                TextPart(request.Filter.SupplierCode),
                CodeListPart(request.Filter.SupplierCodes),
                CodeListPart(request.Filter.SupplierCategoryGuids),
                TextPart(request.Filter.DocumentKeyword),
                TextPart(request.Selection.Mode),
                CodeListPart(request.Selection.IncludedProductCodes),
                CodeListPart(request.Selection.ExcludedProductCodes),
                TextPart(request.CurrentProductCode),
                TextPart(request.BranchCode),
                request.PageNumber.ToString(),
                request.PageSize.ToString(),
                request.CandidatePageNumber.ToString(),
                request.CandidatePageSize.ToString(),
                request.SummaryPageNumber.ToString(),
                request.SummaryPageSize.ToString(),
                request.AutoSelectFirst.ToString(),
                TextPart(request.SortBy),
                TextPart(request.SortDirection),
            };
            return string.Join("|", parts);
        }

        private static string HashParts(string value)
        {
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(bytes)[..16];
        }

        private static string RequireCurrentProductCode(
            LocalSupplierProductSalesAnalysisRequest request
        )
        {
            var code = LocalSupplierProductSalesAnalysisLogic.NormalizeText(
                request.CurrentProductCode
            );
            if (string.IsNullOrWhiteSpace(code))
            {
                throw new LocalSupplierProductSalesAnalysisValidationException(
                    "currentProductCode 不能为空。"
                );
            }

            return code;
        }
    }

    /// <summary>本地商品销量分析参数错误。</summary>
    public sealed class LocalSupplierProductSalesAnalysisValidationException : Exception
    {
        public LocalSupplierProductSalesAnalysisValidationException(string message)
            : base(message)
        {
        }
    }

    /// <summary>选择解析结果。</summary>
    public sealed class LocalSupplierProductSalesSelectionOutcome
    {
        public LocalSupplierProductSalesEffectiveSelectionDto Selection { get; set; } = new();
        public List<string> SelectedCodes { get; set; } = new();
        public string? CurrentProductCode { get; set; }
    }

    /// <summary>本地商品销量分析纯逻辑，便于单元测试。</summary>
    public static class LocalSupplierProductSalesAnalysisLogic
    {
        public const int CodeBatchSize = 500;
        public const int MaxFilterListCount = 100;
        // SQL Server 上限为 2100；为日期、关键字、分页和连接条件预留参数空间。
        public const int MaxFilterParameterCount = 1800;

        public static void ValidateFilterCodeBudget(
            LocalSupplierProductSalesAnalysisFilterDto filter,
            int expandedWarehouseCategoryCount
        )
        {
            var supplierCategoryCount = NormalizeCodes(filter.SupplierCategoryGuids).Count;
            var warehouseCategoryCount = NormalizeCodes(filter.WarehouseCategoryGuids).Count;
            var supplierCount = NormalizeCodes(filter.SupplierCodes).Count;
            if (supplierCategoryCount > MaxFilterListCount)
            {
                throw new LocalSupplierProductSalesAnalysisValidationException("供应商分类不能超过 100 项");
            }
            if (warehouseCategoryCount > MaxFilterListCount)
            {
                throw new LocalSupplierProductSalesAnalysisValidationException("仓库分类不能超过 100 项");
            }
            if (supplierCount > MaxFilterListCount)
            {
                throw new LocalSupplierProductSalesAnalysisValidationException("供应商不能超过 100 项");
            }

            var parameterCount = supplierCategoryCount + expandedWarehouseCategoryCount + supplierCount;
            if (parameterCount > MaxFilterParameterCount)
            {
                throw new LocalSupplierProductSalesAnalysisValidationException("分类和供应商筛选条件过多，请减少选择项");
            }
        }
        public const int MaxDateRangeDays = 366;

        public static (DateTime StartDate, DateTime EndDate) ValidateDateRange(
            DateTime startDate,
            DateTime endDate,
            DateTime? utcNow = null
        )
        {
            if (startDate == DateTime.MinValue || endDate == DateTime.MinValue)
            {
                throw new LocalSupplierProductSalesAnalysisValidationException(
                    "开始日期和结束日期不能为空。"
                );
            }

            var start = startDate.Date;
            var end = endDate.Date;
            if (start > end)
            {
                throw new LocalSupplierProductSalesAnalysisValidationException(
                    "开始日期不能晚于结束日期。"
                );
            }

            var brisbaneYesterday = (utcNow ?? DateTime.UtcNow).AddHours(10).Date.AddDays(-1);
            if (end > brisbaneYesterday)
            {
                throw new LocalSupplierProductSalesAnalysisValidationException(
                    "结束日期不能晚于昨天。"
                );
            }

            if ((int)(end - start).TotalDays + 1 > MaxDateRangeDays)
            {
                throw new LocalSupplierProductSalesAnalysisValidationException(
                    "日期范围不能超过 366 个自然日。"
                );
            }

            return (start, end);
        }

        public static DateTime ResolvePurchaseDate(
            DateTime? inboundDate,
            DateTime? orderDate,
            DateTime createdAt
        )
        {
            return (inboundDate ?? orderDate ?? createdAt).Date;
        }

        public static string? ResolveSupplierCode(
            string? detailSupplierCode,
            string? headerSupplierCode
        )
        {
            var detail = NormalizeText(detailSupplierCode);
            if (!string.IsNullOrWhiteSpace(detail))
            {
                return detail;
            }

            return NormalizeText(headerSupplierCode);
        }

        public static decimal ResolvePurchaseAmount(
            decimal? amount,
            decimal? quantity,
            decimal? purchasePrice
        )
        {
            return amount ?? (quantity ?? 0m) * (purchasePrice ?? 0m);
        }

        public static decimal? CalculateSellThroughRate(
            decimal purchaseQuantity,
            decimal netSalesQuantity
        )
        {
            return purchaseQuantity == 0m ? null : netSalesQuantity / purchaseQuantity * 100m;
        }

        public static decimal? CalculateAverageUnitPrice(
            decimal netSalesQuantity,
            decimal netSalesAmount
        )
        {
            return netSalesQuantity == 0m ? null : netSalesAmount / netSalesQuantity;
        }

        public static List<string> ResolveCategoryGuids(
            LocalSupplierProductSalesAnalysisFilterDto filter
        )
        {
            return MergeSingleAndList(filter.CategoryGuid, filter.WarehouseCategoryGuids);
        }

        public static List<string> ResolveSupplierCodes(
            LocalSupplierProductSalesAnalysisFilterDto filter
        )
        {
            return MergeSingleAndList(filter.SupplierCode, filter.SupplierCodes);
        }

        public static HashSet<string> ExpandCategoryGuids(
            IEnumerable<WarehouseCategory> categories,
            IEnumerable<string>? selectedGuids
        )
        {
            var childrenByParent = categories
                .GroupBy(category => category.ParentGUID ?? string.Empty)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(category => category.CategoryGUID).ToList(),
                    StringComparer.OrdinalIgnoreCase
                );

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            foreach (var guid in NormalizeCodes(selectedGuids))
            {
                queue.Enqueue(guid);
            }

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!result.Add(current))
                {
                    continue;
                }

                if (childrenByParent.TryGetValue(current, out var children))
                {
                    foreach (var child in children)
                    {
                        queue.Enqueue(child);
                    }
                }
            }

            return result;
        }

        public static List<string> ApplySelection(
            IEnumerable<string> productCodes,
            LocalSupplierProductSalesSelectionDto selection
        )
        {
            var codes = productCodes.ToList();
            var mode = string.IsNullOrWhiteSpace(selection.Mode)
                ? "allFiltered"
                : selection.Mode.Trim();
            if (string.Equals(mode, "included", StringComparison.OrdinalIgnoreCase))
            {
                var included = NormalizeCodes(selection.IncludedProductCodes)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return codes.Where(included.Contains).ToList();
            }

            var excluded = NormalizeCodes(selection.ExcludedProductCodes)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return codes.Where(code => !excluded.Contains(code)).ToList();
        }

        public static LocalSupplierProductSalesEffectiveSelectionDto CleanSelection(
            LocalSupplierProductSalesSelectionDto? selection,
            ISet<string> validCodes
        )
        {
            var mode = string.IsNullOrWhiteSpace(selection?.Mode)
                ? "allFiltered"
                : selection!.Mode.Trim();
            var effective = new LocalSupplierProductSalesEffectiveSelectionDto { Mode = mode };
            if (string.Equals(mode, "included", StringComparison.OrdinalIgnoreCase))
            {
                effective.IncludedProductCodes = NormalizeCodes(selection?.IncludedProductCodes)
                    .Where(validCodes.Contains)
                    .ToList();
            }
            else
            {
                effective.ExcludedProductCodes = NormalizeCodes(selection?.ExcludedProductCodes)
                    .Where(validCodes.Contains)
                    .ToList();
            }

            return effective;
        }

        public static LocalSupplierProductSalesSelectionOutcome ResolveSelection(
            IEnumerable<string> filteredCodes,
            LocalSupplierProductSalesSelectionDto? selection,
            string? currentProductCode,
            bool autoSelectFirst
        )
        {
            var codes = filteredCodes.ToList();
            var valid = codes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var effective = CleanSelection(selection, valid);
            var selectedCodes = ApplySelection(codes, effective);

            var current = NormalizeText(currentProductCode);
            string? resolvedCurrent = null;
            var selected = selectedCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (current != null && selected.Contains(current))
            {
                resolvedCurrent = codes.First(code =>
                    string.Equals(code, current, StringComparison.OrdinalIgnoreCase)
                );
            }
            else if (autoSelectFirst || current != null)
            {
                resolvedCurrent = selectedCodes.FirstOrDefault();
            }

            return new LocalSupplierProductSalesSelectionOutcome
            {
                Selection = effective,
                SelectedCodes = selectedCodes,
                CurrentProductCode = resolvedCurrent,
            };
        }

        public static List<List<string>> BatchCodes(IEnumerable<string>? codes)
        {
            return NormalizeCodes(codes).Chunk(CodeBatchSize).Select(chunk => chunk.ToList()).ToList();
        }

        public static List<LocalSupplierProductSalesSummaryRowDto> SortSummaryRows(
            IEnumerable<LocalSupplierProductSalesSummaryRowDto> rows,
            string? sortBy,
            string? sortDirection
        )
        {
            var list = rows.ToList();
            var ascending = string.Equals(
                sortDirection,
                "asc",
                StringComparison.OrdinalIgnoreCase
            );
            var field = string.IsNullOrWhiteSpace(sortBy) ? null : sortBy.Trim().ToLowerInvariant();

            IOrderedEnumerable<LocalSupplierProductSalesSummaryRowDto> ordered = field switch
            {
                "purchasequantity" => ascending
                    ? list.OrderBy(row => row.PurchaseQuantity)
                    : list.OrderByDescending(row => row.PurchaseQuantity),
                "purchaseamount" => ascending
                    ? list.OrderBy(row => row.PurchaseAmount)
                    : list.OrderByDescending(row => row.PurchaseAmount),
                "netsalesamount" => ascending
                    ? list.OrderBy(row => row.NetSalesAmount)
                    : list.OrderByDescending(row => row.NetSalesAmount),
                "sellthroughrate" => ascending
                    ? list.OrderBy(row => row.SellThroughRate)
                    : list.OrderByDescending(row => row.SellThroughRate),
                "productcode" => ascending
                    ? list.OrderBy(row => row.ProductCode, StringComparer.OrdinalIgnoreCase)
                    : list.OrderByDescending(row => row.ProductCode, StringComparer.OrdinalIgnoreCase),
                _ => ascending
                    ? list.OrderBy(row => row.NetSalesQuantity)
                    : list.OrderByDescending(row => row.NetSalesQuantity),
            };

            return ordered.ThenBy(row => row.ProductCode, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static LocalSupplierProductSalesSummaryTotalsDto ComputeSummaryTotals(
            IEnumerable<LocalSupplierProductSalesSummaryRowDto> rows
        )
        {
            var list = rows.ToList();
            var purchaseQuantity = list.Sum(row => row.PurchaseQuantity);
            var purchaseAmount = list.Sum(row => row.PurchaseAmount);
            var netSalesQuantity = list.Sum(row => row.NetSalesQuantity);
            var netSalesAmount = list.Sum(row => row.NetSalesAmount);
            return new LocalSupplierProductSalesSummaryTotalsDto
            {
                PurchaseQuantity = purchaseQuantity,
                PurchaseAmount = purchaseAmount,
                NetSalesQuantity = netSalesQuantity,
                NetSalesAmount = netSalesAmount,
                SellThroughRate = CalculateSellThroughRate(purchaseQuantity, netSalesQuantity),
            };
        }

        public static List<LocalSupplierProductSalesDailyDto> BuildProductDailySeries(
            IEnumerable<LocalSupplierProductSalesDailyDto> rows,
            DateTime startDate,
            DateTime endDate
        )
        {
            var byDate = rows
                .GroupBy(row => row.Date.Date)
                .ToDictionary(
                    group => group.Key,
                    group => new
                    {
                        PurchaseQuantity = group.Sum(row => row.PurchaseQuantity),
                        PurchaseAmount = group.Sum(row => row.PurchaseAmount),
                        NetSalesQuantity = group.Sum(row => row.NetSalesQuantity),
                        NetSalesAmount = group.Sum(row => row.NetSalesAmount),
                    }
                );

            var series = new List<LocalSupplierProductSalesDailyDto>();
            for (var date = startDate.Date; date <= endDate.Date; date = date.AddDays(1))
            {
                byDate.TryGetValue(date, out var aggregate);
                var netSalesQuantity = aggregate?.NetSalesQuantity ?? 0m;
                var netSalesAmount = aggregate?.NetSalesAmount ?? 0m;
                series.Add(
                    new LocalSupplierProductSalesDailyDto
                    {
                        Date = date,
                        PurchaseQuantity = aggregate?.PurchaseQuantity ?? 0m,
                        PurchaseAmount = aggregate?.PurchaseAmount ?? 0m,
                        NetSalesQuantity = netSalesQuantity,
                        NetSalesAmount = netSalesAmount,
                        AverageUnitPrice = CalculateAverageUnitPrice(
                            netSalesQuantity,
                            netSalesAmount
                        ),
                    }
                );
            }

            return series;
        }

        public static List<LocalSupplierProductSalesBranchDailyDto> BuildBranchDailySeries(
            IEnumerable<LocalSupplierProductSalesBranchDailyDto> rows,
            DateTime startDate,
            DateTime endDate
        )
        {
            var byDate = rows
                .GroupBy(row => row.Date.Date)
                .ToDictionary(
                    group => group.Key,
                    group => new
                    {
                        NetSalesQuantity = group.Sum(row => row.NetSalesQuantity),
                        NetSalesAmount = group.Sum(row => row.NetSalesAmount),
                    }
                );

            var series = new List<LocalSupplierProductSalesBranchDailyDto>();
            for (var date = startDate.Date; date <= endDate.Date; date = date.AddDays(1))
            {
                byDate.TryGetValue(date, out var aggregate);
                var netSalesQuantity = aggregate?.NetSalesQuantity ?? 0m;
                var netSalesAmount = aggregate?.NetSalesAmount ?? 0m;
                series.Add(
                    new LocalSupplierProductSalesBranchDailyDto
                    {
                        Date = date,
                        NetSalesQuantity = netSalesQuantity,
                        NetSalesAmount = netSalesAmount,
                        AverageUnitPrice = CalculateAverageUnitPrice(
                            netSalesQuantity,
                            netSalesAmount
                        ),
                    }
                );
            }

            return series;
        }

        public static List<string> NormalizeCodes(IEnumerable<string>? codes)
        {
            return codes?
                .Select(NormalizeText)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
        }

        public static string? NormalizeText(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static List<string> MergeSingleAndList(string? single, List<string>? list)
        {
            var values = new List<string>();
            if (!string.IsNullOrWhiteSpace(single))
            {
                values.Add(single.Trim());
            }

            if (list is not null)
            {
                values.AddRange(
                    list
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Select(item => item.Trim())
                );
            }

            return values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
