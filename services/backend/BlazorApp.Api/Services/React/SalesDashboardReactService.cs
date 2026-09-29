using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using AutoMapper;
using BlazorApp.Api.Cache;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBSalesRecord;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using SqlSugar;
using ScheduledTaskStatus = BlazorApp.Shared.Models.HBweb.TaskStatus;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// 产品查询结果投影（用于统一类型）
    /// </summary>
    internal class ProductInfo
    {
        public string ProductCode { get; set; } = string.Empty;
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
        public string? ProductImage { get; set; }
        public string? ProductName { get; set; }
        public bool? IsActive { get; set; }
        public int? MinOrderQuantity { get; set; }
    }

    /// <summary>
    /// 商品销量分析参数错误。控制器捕获后返回 400，前端可按 message 识别。
    /// </summary>
    public sealed class ProductSalesAnalysisValidationException : Exception
    {
        public ProductSalesAnalysisValidationException(string message)
            : base(message)
        {
        }
    }

    internal class ProductSalesAggregateRow
    {
        public string ProductCode { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal SalesAmount { get; set; }
    }

    internal class ProductSalesDailyRow
    {
        public DateTime Date { get; set; }
        public int Quantity { get; set; }
        public decimal SalesAmount { get; set; }
    }

    internal class ProductSalesBranchRow
    {
        public string BranchCode { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal SalesAmount { get; set; }
    }

    internal class ProductSalesBranchDailyRow
    {
        public DateTime Date { get; set; }
        public int Quantity { get; set; }
        public decimal SalesAmount { get; set; }
    }

    internal class ProductSalesSupplierRow
    {
        public string ProductCode { get; set; } = string.Empty;
        public string SupplierCode { get; set; } = string.Empty;
    }

    internal sealed class ChinaSupplierProductMapRow
    {
        public string ProductCode { get; set; } = string.Empty;
        public string ChinaSupplierCode { get; set; } = string.Empty;
    }

    internal sealed class ProductSalesProductInfo
    {
        public string ProductCode { get; set; } = string.Empty;
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
        public string? ProductName { get; set; }
        public string? EnglishName { get; set; }
        public string? ImageUrl { get; set; }
    }

    internal sealed class ProductSalesAnalysisQueryContext
    {
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public List<string>? BranchCodes { get; set; }
        public List<string> AustralianSupplierCodes { get; set; } = new();
        public HashSet<string> ChinaSupplierCodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AllChinaSupplierCodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ChinaSupplierNameMap { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ChinaProductMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Keyword { get; set; }
    }

    internal sealed class ProductSalesChinaCatalog
    {
        public HashSet<string> Codes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Names { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class ProductSalesSupplierQueryPlan
    {
        public bool IsUnfiltered { get; init; }
        public List<string> DirectSupplierCodes { get; init; } = new();
        public List<string> LegacyProductCodes { get; init; } = new();
    }

    internal sealed class ProductSalesSupplierResolution
    {
        public HashSet<string> AustralianCodes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ChinaCodes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool ChinaSupplierUnmapped { get; set; }
    }

    internal class BestSellerAggregateRow
    {
        public string ProductCode { get; set; } = string.Empty;
        public int TotalQuantity { get; set; }
        public decimal TotalSalesAmount { get; set; }
        public decimal? TotalCost { get; set; }
        public decimal? GrossProfit { get; set; }
        public string? CostSource { get; set; }
    }

    internal sealed record ProductStatisticStatusSnapshot(
        string Status,
        string? Message,
        string CacheVersion,
        DateTime? UpdatedAt
    );

    internal class BestSellerBranchAggregateRow
    {
        public string ProductCode { get; set; } = string.Empty;
        public string BranchCode { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal SalesAmount { get; set; }
        public decimal? TotalCost { get; set; }
        public decimal? GrossProfit { get; set; }
        public string? CostSource { get; set; }
    }

    internal class ProductReportProductAggregateRow
    {
        public string ProductCode { get; set; } = string.Empty;
        public string? ProductName { get; set; }
        public int Period { get; set; }
        public int Quantity { get; set; }
        public decimal SalesAmount { get; set; }
        public int OrderCount { get; set; }
        public decimal? GrossProfit { get; set; }
        public int StatisticRowCount { get; set; }
        public int CostedRowCount { get; set; }
        public int GrossProfitRowCount { get; set; }
    }

    /// <summary>
    /// 当前期、同期商品聚合后在数据库中合并的分页行。
    /// </summary>
    internal class ProductReportProductCombinedAggregateRow
    {
        public string ProductCode { get; set; } = string.Empty;
        public string? CurrentProductName { get; set; }
        public string? CompareProductName { get; set; }
        public int CurrentQuantity { get; set; }
        public decimal CurrentSalesAmount { get; set; }
        public int CurrentOrderCount { get; set; }
        public decimal? CurrentGrossProfit { get; set; }
        public int CurrentStatisticRowCount { get; set; }
        public int CurrentCostedRowCount { get; set; }
        public int CurrentGrossProfitRowCount { get; set; }
        public int CompareQuantity { get; set; }
        public decimal CompareSalesAmount { get; set; }
        public int CompareOrderCount { get; set; }
        public decimal? CompareGrossProfit { get; set; }
        public int CompareStatisticRowCount { get; set; }
        public int CompareCostedRowCount { get; set; }
        public int CompareGrossProfitRowCount { get; set; }
    }

    internal sealed class CompactSalesBoardCubeRow
    {
        public string BranchCode { get; set; } = string.Empty;
        public string ProductCode { get; set; } = string.Empty;
        /// <summary>日统计行上的供应商编码：旧写法为 200，新写法为直写的国内供应商编码。</summary>
        public string SupplierCode { get; set; } = string.Empty;
        public int TotalQuantity { get; set; }
        public decimal TotalAmount { get; set; }
        /// <summary>该分组最近的销售日，用于同一商品解析出多个国内供应商时取最新归属。</summary>
        public DateTime LastDate { get; set; }
    }

    internal sealed class CompactSalesBoardChinaSupplierRow
    {
        public string SupplierCode { get; set; } = string.Empty;
        public string? SupplierName { get; set; }
        public bool IsDeleted { get; set; }
    }

    internal sealed class CompactSalesBoardProductInfoRow
    {
        public string ProductCode { get; set; } = string.Empty;
        public string? ItemNumber { get; set; }
        public string? ProductName { get; set; }
        public string? ProductImage { get; set; }
    }

    internal sealed record CompactSalesBoardCubeEntity(string Code, string Name);

    internal sealed record CompactSalesBoardCubeProduct(
        string ProductCode,
        string? ItemNumber,
        string? ProductName,
        string? ProductImage,
        int SupplierIndex
    );

    /// <summary>立方体中的一格：一个门店×一个商品在日期范围内的合计，以数组下标引用维度以压缩内存。</summary>
    internal readonly record struct CompactSalesBoardCubeCell(int BranchIndex, int ProductIndex, int Quantity, decimal Amount);

    /// <summary>
    /// 紧凑销售看板的「门店×商品」聚合立方体（仅能还原出国内供应商的国内货：直写行，或已映射的 200 行），按日期范围与统计水位缓存。
    /// 本月约 5.2 万格；缓存后三栏联动、排序、分页都在内存中完成，不再逐次查库。
    /// </summary>
    internal sealed record CompactSalesBoardCube(
        CompactSalesBoardCubeEntity[] Branches,
        CompactSalesBoardCubeEntity[] Suppliers,
        CompactSalesBoardCubeProduct[] Products,
        CompactSalesBoardCubeCell[] Cells,
        Dictionary<string, int> BranchIndex,
        Dictionary<string, int> SupplierIndex,
        Dictionary<string, int> ProductIndex
    )
    {
        public static CompactSalesBoardCube Empty { get; } = new(
            Array.Empty<CompactSalesBoardCubeEntity>(),
            Array.Empty<CompactSalesBoardCubeEntity>(),
            Array.Empty<CompactSalesBoardCubeProduct>(),
            Array.Empty<CompactSalesBoardCubeCell>(),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        );
    }

    /// <summary>一次立方体构建的结果：数据与读取它的同一统计快照的状态（水位、缓存版本）。</summary>
    internal sealed record CompactSalesBoardCubeRead(CompactSalesBoardCube Cube, ProductReportStatisticStatusDto Status);

    internal sealed class CompactSalesBoardStateRow
    {
        public DateTime Date { get; set; }
        public string? Status { get; set; }
        public DateTime? LastAggregatedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public string? SourceProductVersion { get; set; }
    }

    internal class StatisticDateBranchRow
    {
        public DateTime Date { get; set; }
        public string? BranchCode { get; set; }
    }

    internal class StatisticDateBranchDeviceRow : StatisticDateBranchRow
    {
        public string? DeviceCode { get; set; }
    }

    internal class StatisticDateBranchHourRow
    {
        public DateTime Date { get; set; }
        public string? BranchCode { get; set; }
        public int Hour { get; set; }
        public int OrderCount { get; set; }
        public decimal TotalAmount { get; set; }
    }

    internal class ProductBranchAggregateRow
    {
        public string BranchCode { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal SalesAmount { get; set; }
        public decimal? GrossProfit { get; set; }
        public int StatisticRowCount { get; set; }
        public int CostedRowCount { get; set; }
        public int GrossProfitRowCount { get; set; }
    }

    internal class SupplierBranchAggregateRow
    {
        public DateTime? Date { get; set; }
        public string SupplierCode { get; set; } = string.Empty;
        public string? BranchCode { get; set; }
        public decimal TotalAmount { get; set; }
        public int TotalQuantity { get; set; }
        public int? OrderCount { get; set; }
        public decimal? GrossProfit { get; set; }
        public int StatisticRowCount { get; set; }
        public int CostedRowCount { get; set; }
        public int GrossProfitRowCount { get; set; }
    }

    internal class ProductSupplierBranchAggregateRow : SupplierBranchAggregateRow
    {
        public string ProductCode { get; set; } = string.Empty;
    }

    internal class SupplierRankAggregateRow
    {
        public string SupplierCode { get; set; } = string.Empty;
        public decimal TotalAmount { get; set; }
        public int TotalQuantity { get; set; }
        public int OrderCount { get; set; }
        public int StoreCount { get; set; }
        public decimal? GrossProfit { get; set; }
        public int StatisticRowCount { get; set; }
        public int CostedRowCount { get; set; }
        public int GrossProfitRowCount { get; set; }
    }

    internal readonly record struct SupplierCompareMetrics(
        decimal TotalAmount,
        int OrderCount,
        decimal? GrossProfit,
        int StatisticRowCount,
        int CostedRowCount,
        int GrossProfitRowCount
    );

    /// <summary>
    /// 销售仪表板 React 服务
    /// 为 React 前端提供销售统计数据的查询功能
    /// </summary>
    public partial class SalesDashboardReactService : ISalesDashboardReactService
    {
        private const string CompleteCostStatus = "Complete";
        private const string MissingCostStatus = "Missing";
        private const string NoActivityCostStatus = "NoActivity";

        /// <summary>
        /// 统计行、成本覆盖行和毛利覆盖行必须完整对齐才算 Complete；因此实际零毛利仍可为 Complete，
        /// 而没有统计行的期间才是 NoActivity。TotalCost 有值即可覆盖 OpenItem 的空 UnitCostSnapshot。
        /// </summary>
        private static string GetCostStatus(int statisticRowCount, int costedRowCount, int grossProfitRowCount)
        {
            if (statisticRowCount <= 0)
                return NoActivityCostStatus;
            return statisticRowCount == costedRowCount && statisticRowCount == grossProfitRowCount
                ? CompleteCostStatus
                : MissingCostStatus;
        }

        private readonly SqlSugarContext _context;
        private readonly POSMSqlSugarContext _posmContext;
        private readonly IMapper _mapper;
        private readonly ILogger<SalesDashboardReactService> _logger;
        private readonly IMemoryCache _cache;
        private readonly IServiceScopeFactory? _serviceScopeFactory;

        // 测试专用：在 cache miss 捕获代际后、写入缓存前触发，用于交错测试模拟 ClearActiveKeys 切代。
        internal Action? ProductSalesAnalysisCacheWriteInterceptor { get; set; }

        // 测试专用：可控地挂起分店统计补算，验证超时响应不会把部分快照误报为完整。
        internal Func<DateTime, Task>? StoreStatisticsRefreshTestInterceptor { get; set; }

        // 测试专用：模拟分时统计快速任务完成但意外漏写统计行。
        internal Func<DateTime, Task>? HourlyStatisticsRefreshTestInterceptor { get; set; }

        // 测试专用：记录 POSM 销售覆盖查询次数，避免稳定快照路径重复扫描来源明细。
        internal Action? PosmStoreSalesCoverageReadTestInterceptor { get; set; }

        // 测试专用：模拟快速补算后营业额排行来源身份暂不可核验。
        internal Action? ExpectedExecutiveBranchCodesTestInterceptor { get; set; }

        // 测试专用：在分店统计补算键清理完成后触发，避免测试依赖不确定的轮询延迟。
        internal Action? StoreStatisticsRefreshCompletedTestInterceptor { get; set; }

        private static readonly TimeSpan SUMMARY_CACHE_DURATION = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan RANKING_CACHE_DURATION = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan BEST_SELLERS_CACHE_DURATION = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan DETAIL_CACHE_DURATION = TimeSpan.FromMinutes(3);
        // 按统计批次版本做键的完整报表条目：见 ReadCompleteReportAsync。
        private static readonly TimeSpan VERSIONED_REPORT_CACHE_SLIDING = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan VERSIONED_REPORT_CACHE_ABSOLUTE = TimeSpan.FromHours(6);
        private static readonly TimeSpan PRODUCT_SALES_ANALYSIS_CACHE_DURATION = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan SALES_SOURCE_COVERAGE_CACHE_DURATION = TimeSpan.FromSeconds(20);
        // 缺口统计只短等 250ms；快速补算可随本次返回，慢任务转后台，给查询和渲染保留 2 秒预算。
        private static readonly TimeSpan REPORT_STATISTICS_REFRESH_WAIT = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan STATISTICS_RUNNING_STALE_AFTER = TimeSpan.FromMinutes(90);
        private const int REPORT_STATISTICS_REFRESH_MAX_DAYS = 35;
        private const int LEGACY_CHINA_SUPPLIER_PRODUCT_FILTER_LIMIT = 2000;
        private const int PRODUCT_SALES_ANALYSIS_CODE_BATCH_SIZE = 500;
        private const string CHINA_LOCAL_SUPPLIER_CODE = "200";
        private const string CHINA_LOCAL_SUPPLIER_FALLBACK_NAME = "hotbargain";
        private const string REPORT_STATISTICS_CACHE_GENERATION_KEY =
            "SalesDashboard:ReportStatisticsCacheGeneration";
        private static readonly ConcurrentDictionary<string, byte> REPORT_STATISTICS_REFRESHING_KEYS = new();
        private static readonly ConditionalWeakTable<IMemoryCache, object>
            REPORT_STATISTICS_CACHE_GENERATION_LOCKS = new();
        private static readonly ConditionalWeakTable<
            IMemoryCache,
            ConcurrentDictionary<
                string,
                Lazy<Task<Dictionary<DateTime, HashSet<string>>?>>
            >
        > SALES_SOURCE_COVERAGE_READS = new();
        private static readonly ConditionalWeakTable<
            IMemoryCache,
            ConcurrentDictionary<string, Lazy<Task<CompactSalesBoardCubeRead>>>
        > COMPACT_SALES_BOARD_CUBE_BUILDS = new();
        // 立方体按统计水位分代：统计刷新会换缓存键，因此 Fresh 状态下可以放心缓存较长时间。
        private static readonly TimeSpan COMPACT_SALES_BOARD_CUBE_CACHE_DURATION = TimeSpan.FromMinutes(10);
        /// <summary>看板最长区间：两年（含闰日），与销售明细 SalesDetailReportController.MaxReportDays 一致。</summary>
        internal const int CompactSalesBoardMaxDays = 731;
        // 商品明细每页上限与带图导出上限一致（500 行），导出全部结果也按 500 行一页分批读取。
        internal const int CompactSalesBoardMaxPageSize = 500;

        private enum StatisticsRefreshState
        {
            NotNeeded,
            Completed,
            Pending,
        }

        private sealed class ReportStatisticsCacheGenerationState
        {
            public long Value;
        }

        public SalesDashboardReactService(
            SqlSugarContext context,
            POSMSqlSugarContext posmContext,
            IMapper mapper,
            ILogger<SalesDashboardReactService> logger,
            IMemoryCache cache,
            IServiceScopeFactory? serviceScopeFactory = null,
            IConfiguration? configuration = null
        )
        {
            _context = context;
            _posmContext = posmContext;
            _mapper = mapper;
            _logger = logger;
            _cache = cache;
            _serviceScopeFactory = serviceScopeFactory;
            // 回填完成后单独切换读取；新增字段和后台写入可以先于新读取部署。
            _useSupplierRollups = configuration?.GetValue<bool>("Reports:UseSupplierRollups") ?? false;
        }

        /// <summary>
        /// 验证数据库连接和指定实体表是否存在
        /// </summary>
        /// <typeparam name="TEntity">实体类型</typeparam>
        /// <returns>验证成功返回 true，否则返回 false</returns>
        private bool ValidateDatabaseConnection<TEntity>()
        {
            if (_context?.Db == null)
            {
                _logger.LogError("数据库上下文为空 (SqlSugarContext.Db is null)");
                return false;
            }

            try
            {
                var tableName = _context.Db.EntityMaintenance.GetTableName<TEntity>();
                if (!_context.Db.DbMaintenance.IsAnyTable(tableName))
                {
                    _logger.LogError($"表 {tableName} 不存在");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"验证数据库连接或表 {typeof(TEntity).Name} 时出错");
                return false;
            }
        }

        /// <summary>
        /// 验证日期范围的有效性
        /// </summary>
        /// <param name="dateRange">日期范围对象</param>
        /// <exception cref="ArgumentNullException">当 dateRange 为 null 时抛出</exception>
        /// <exception cref="ArgumentException">当日期范围无效时抛出</exception>
        private void ValidateDateRange(DateRangeDto dateRange)
        {
            if (dateRange == null)
                throw new ArgumentNullException(nameof(dateRange));

            if (dateRange.StartDate > dateRange.EndDate)
                throw new ArgumentException("开始日期不能晚于结束日期");

            if (dateRange.CompareStartDate.HasValue && dateRange.CompareEndDate.HasValue)
            {
                if (dateRange.CompareStartDate.Value > dateRange.CompareEndDate.Value)
                    throw new ArgumentException("比较开始日期不能晚于比较结束日期");
            }
        }

        /// <summary>
        /// 获取仪表板汇总数据
        /// 包含总销售额、总销量、订单数、SKU数、客户数、平均订单价值等指标
        /// 支持与对比期间的环比分析
        /// </summary>
        /// <param name="dateRange">日期范围</param>
        /// <returns>仪表板汇总数据</returns>
        public async Task<DashboardSummaryDto?> GetDashboardSummaryAsync(DateRangeDto dateRange)
        {
            try
            {
                ValidateDateRange(dateRange);

                var cacheKey = SalesDashboardCacheKeys.Summary(dateRange, null);

                if (
                    _cache.TryGetValue<DashboardSummaryDto>(cacheKey, out var cachedResult)
                    && cachedResult != null
                )
                {
                    _logger.LogInformation("从缓存获取仪表板汇总数据: {CacheKey}", cacheKey);
                    return cachedResult;
                }

                if (!ValidateDatabaseConnection<DailySalesStatistic>())
                {
                    return null;
                }

                var currentData = await _context
                    .Db.Queryable<DailySalesStatistic>()
                    .Where(s =>
                        s.Date >= dateRange.StartDate.Date && s.Date <= dateRange.EndDate.Date
                    )
                    .Select(s => new
                    {
                        TotalAmount = SqlFunc.AggregateSum(s.TotalAmount),
                        TotalQuantity = SqlFunc.AggregateSum(s.TotalQuantity),
                        OrderCount = SqlFunc.AggregateSum(s.OrderCount),
                        SkuCount = SqlFunc.AggregateSum(s.SkuCount),
                        CustomerCount = SqlFunc.AggregateSum(s.CustomerCount),
                        AvgOrderValue = SqlFunc.AggregateAvg(s.AverageOrderValue),
                    })
                    .FirstAsync();

                var result = new DashboardSummaryDto
                {
                    StartDate = dateRange.StartDate,
                    EndDate = dateRange.EndDate,
                    TotalAmount = currentData.TotalAmount,
                    TotalQuantity = currentData.TotalQuantity,
                    OrderCount = currentData.OrderCount,
                    SkuCount = currentData.SkuCount,
                    CustomerCount = currentData.CustomerCount,
                    AverageOrderValue = currentData.AvgOrderValue,
                };

                if (dateRange.CompareStartDate.HasValue && dateRange.CompareEndDate.HasValue)
                {
                    var compareStartDate = dateRange.CompareStartDate.Value.Date;
                    var compareEndDate = dateRange.CompareEndDate.Value.Date;

                    var compareData = await _context
                        .Db.Queryable<DailySalesStatistic>()
                        .Where(s => s.Date >= compareStartDate && s.Date <= compareEndDate)
                        //  .GroupBy(s => 1)
                        .Select(s => new
                        {
                            TotalAmount = SqlFunc.AggregateSum(s.TotalAmount),
                            TotalQuantity = SqlFunc.AggregateSum(s.TotalQuantity),
                            OrderCount = SqlFunc.AggregateSum(s.OrderCount),
                            SkuCount = SqlFunc.AggregateSum(s.SkuCount),
                            CustomerCount = SqlFunc.AggregateSum(s.CustomerCount),
                            AvgOrderValue = SqlFunc.AggregateAvg(s.AverageOrderValue),
                        })
                        .FirstAsync();

                    result.CompareTotalAmount = compareData.TotalAmount;
                    result.CompareTotalQuantity = compareData.TotalQuantity;
                    result.CompareOrderCount = compareData.OrderCount;
                    result.CompareSkuCount = compareData.SkuCount;
                    result.CompareCustomerCount = compareData.CustomerCount;
                    result.CompareAvgOrderValue = compareData.AvgOrderValue;

                    result.TotalAmountGrowth = CalculateGrowth(
                        currentData.TotalAmount,
                        compareData.TotalAmount
                    );
                    result.TotalQuantityGrowth = CalculateGrowth(
                        currentData.TotalQuantity,
                        compareData.TotalQuantity
                    );
                    result.OrderCountGrowth = CalculateGrowth(
                        currentData.OrderCount,
                        compareData.OrderCount
                    );
                    result.SkuCountGrowth = CalculateGrowth(
                        currentData.SkuCount,
                        compareData.SkuCount
                    );
                    result.CustomerCountGrowth = CalculateGrowth(
                        currentData.CustomerCount,
                        compareData.CustomerCount
                    );
                    result.AvgOrderValueGrowth = CalculateGrowth(
                        currentData.AvgOrderValue,
                        compareData.AvgOrderValue
                    );
                }

                var cacheOptions = new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(SUMMARY_CACHE_DURATION)
                    .SetSlidingExpiration(TimeSpan.FromMinutes(5));

                _cache.Set(cacheKey, result, cacheOptions);
                _logger.LogInformation(
                    "仪表板汇总数据已缓存: {CacheKey}, 过期时间: {Expiration}",
                    cacheKey,
                    DateTime.Now.Add(SUMMARY_CACHE_DURATION)
                );

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetDashboardSummaryAsync failed");
                return null;
            }
        }

        /// <summary>
        /// 获取小时销售数据
        /// 按小时统计销售额、销量、客户数等指标
        /// </summary>
        /// <param name="dateRange">日期范围</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <returns>小时销售数据列表</returns>
        public async Task<List<HourlySalesDto>> GetHourlySalesAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes = null
        )
        {
            try
            {
                ValidateDateRange(dateRange);

                var cacheKey = SalesDashboardCacheKeys.Hourly(dateRange, branchCodes, null);

                if (
                    _cache.TryGetValue<List<HourlySalesDto>>(cacheKey, out var cachedResult)
                    && cachedResult != null
                    && cachedResult.Count != 0
                )
                {
                    _logger.LogInformation("从缓存获取小时销售数据: {CacheKey}", cacheKey);
                    return cachedResult;
                }

                if (!ValidateDatabaseConnection<HourlySalesStatistic>())
                {
                    return new List<HourlySalesDto>();
                }

                var startDate = dateRange.StartDate.Date;
                var endDate = dateRange.EndDate.Date;

                var currentQuery = _context
                    .Db.Queryable<HourlySalesStatistic>()
                    .Where(s => s.BranchCode != "ALL" && s.Date >= startDate && s.Date <= endDate);

                if (branchCodes != null && branchCodes.Any())
                {
                    currentQuery = currentQuery.Where(s => branchCodes.Contains(s.BranchCode!));
                }

                var currentData = await currentQuery
                    .GroupBy(s => new { s.Hour, s.BranchCode })
                    .Select(s => new
                    {
                        Hour = s.Hour,
                        BranchCode = s.BranchCode,

                        TotalAmount = SqlFunc.AggregateSum(s.TotalAmount),
                        TotalQuantity = SqlFunc.AggregateSum(s.TotalQuantity),
                        CustomerCount = SqlFunc.AggregateSum(s.CustomerCount),
                        AvgOrderValue = SqlFunc.AggregateAvg(s.AverageOrderValue),
                    })
                    .OrderBy(s => s.Hour)
                    .ToListAsync();

                Dictionary<(int Hour, string? BranchCode), dynamic> compareDict = new();

                if (dateRange.CompareStartDate.HasValue && dateRange.CompareEndDate.HasValue)
                {
                    var compareStartDate = dateRange.CompareStartDate.Value.Date;
                    var compareEndDate = dateRange.CompareEndDate.Value.Date;

                    var compareQuery = _context
                        .Db.Queryable<HourlySalesStatistic>()
                        .Where(s => s.Date >= compareStartDate && s.Date <= compareEndDate);

                    if (branchCodes != null && branchCodes.Any())
                    {
                        compareQuery = compareQuery.Where(s => branchCodes.Contains(s.BranchCode!));
                    }

                    var compareData = await compareQuery
                        .GroupBy(s => new { s.Hour, s.BranchCode })
                        .Select(s => new
                        {
                            Hour = s.Hour,
                            BranchCode = s.BranchCode,
                            TotalAmount = SqlFunc.AggregateSum(s.TotalAmount),
                            TotalQuantity = SqlFunc.AggregateSum(s.TotalQuantity),
                            CustomerCount = SqlFunc.AggregateSum(s.CustomerCount),
                            AvgOrderValue = SqlFunc.AggregateAvg(s.AverageOrderValue),
                        })
                        .ToListAsync();

                    compareDict = compareData.ToDictionary(
                        d => (d.Hour, d.BranchCode),
                        d => (dynamic)d
                    );
                }

                var result = new List<HourlySalesDto>();

                foreach (var item in currentData)
                {
                    var dto = new HourlySalesDto
                    {
                        Date = startDate,
                        Hour = item.Hour,
                        BranchCode = item.BranchCode,
                        BranchName = item.BranchCode,
                        TotalAmount = item.TotalAmount,
                        TotalQuantity = item.TotalQuantity,
                        CustomerCount = item.CustomerCount,
                        AverageOrderValue = item.AvgOrderValue,
                    };

                    if (
                        item.BranchCode != null
                        && compareDict.TryGetValue(
                            (item.Hour, item.BranchCode),
                            out var compareData
                        )
                    )
                    {
                        dto.CompareTotalAmount = compareData.TotalAmount;
                        dto.CompareTotalQuantity = compareData.TotalQuantity;
                        dto.CompareCustomerCount = compareData.CustomerCount;
                        dto.CompareAvgOrderValue = compareData.AvgOrderValue;
                    }

                    result.Add(dto);
                }

                var cacheOptions = new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(RANKING_CACHE_DURATION)
                    .SetSlidingExpiration(TimeSpan.FromMinutes(5));

                _cache.Set(cacheKey, result, cacheOptions);
                _logger.LogInformation(
                    "小时销售数据已缓存: {CacheKey}, 过期时间: {Expiration}",
                    cacheKey,
                    DateTime.Now.Add(RANKING_CACHE_DURATION)
                );

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetHourlySalesAsync failed");
                return new List<HourlySalesDto>();
            }
        }

        /// <summary>
        /// 获取分店销售排名
        /// 按销售额降序排列，返回前 N 名分店
        /// </summary>
        /// <param name="dateRange">日期范围</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <param name="topN">返回前 N 名，默认 50</param>
        /// <returns>分店销售排名列表</returns>
        public async Task<List<StoreSalesRankDto>> GetStoreSalesRankAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes = null,
            int topN = 50
        )
        {
            try
            {
                ValidateDateRange(dateRange);

                var cacheKey = SalesDashboardCacheKeys.StoreRank(dateRange, branchCodes, topN);

                if (
                    _cache.TryGetValue<List<StoreSalesRankDto>>(cacheKey, out var cachedResult)
                    && cachedResult != null
                    && cachedResult.Count != 0
                )
                {
                    _logger.LogInformation("从缓存获取分店销售排名: {CacheKey}", cacheKey);
                    return cachedResult;
                }

                if (!ValidateDatabaseConnection<StoreSalesStatistic>())
                {
                    return new List<StoreSalesRankDto>();
                }

                var startDate = dateRange.StartDate.Date;
                var endDate = dateRange.EndDate.Date;

                var query = _context
                    .Db.Queryable<StoreSalesStatistic>()
                    .Where(s =>
                        s.BranchCode != null
                        && s.BranchCode != "ALL"
                        && s.Date >= startDate
                        && s.Date <= endDate
                    );

                if (branchCodes != null && branchCodes.Any())
                {
                    query = query.Where(s => branchCodes.Contains(s.BranchCode));
                }

                var currentData = await query
                    .GroupBy(s => new { s.BranchCode, s.BranchName })
                    .Select(s => new
                    {
                        BranchCode = s.BranchCode,
                        BranchName = s.BranchName,
                        TotalAmount = SqlFunc.AggregateSum(s.TotalAmount),
                        TotalQuantity = SqlFunc.AggregateSum(s.TotalQuantity),
                        OrderCount = SqlFunc.AggregateSum(s.OrderCount),
                        CustomerCount = SqlFunc.AggregateSum(s.CustomerCount),
                        AvgOrderValue = SqlFunc.AggregateAvg(s.AverageOrderValue),
                    })
                    .OrderByDescending(s => s.TotalAmount)
                    .Take(topN)
                    .ToListAsync();

                var result = new List<StoreSalesRankDto>();

                // 批量获取对比数据
                Dictionary<string, dynamic> compareDict = new Dictionary<string, dynamic>();

                if (dateRange.CompareStartDate.HasValue && dateRange.CompareEndDate.HasValue)
                {
                    var compareStartDate = dateRange.CompareStartDate.Value.Date;
                    var compareEndDate = dateRange.CompareEndDate.Value.Date;
                    var branchCodesList = currentData.Select(s => s.BranchCode).ToList();

                    if (branchCodesList.Any())
                    {
                        var compareQuery = _context
                            .Db.Queryable<StoreSalesStatistic>()
                            .Where(s =>
                                s.Date >= compareStartDate
                                && s.Date <= compareEndDate
                                && branchCodesList.Contains(s.BranchCode)
                            );

                        if (branchCodes != null && branchCodes.Any())
                        {
                            compareQuery = compareQuery.Where(s =>
                                branchCodes.Contains(s.BranchCode)
                            );
                        }

                        var compareDataList = await compareQuery
                            .GroupBy(s => s.BranchCode)
                            .Select(s => new
                            {
                                BranchCode = s.BranchCode,
                                TotalAmount = SqlFunc.AggregateSum(s.TotalAmount),
                                TotalQuantity = SqlFunc.AggregateSum(s.TotalQuantity),
                                OrderCount = SqlFunc.AggregateSum(s.OrderCount),
                                AvgOrderValue = SqlFunc.AggregateAvg(s.AverageOrderValue),
                            })
                            .ToListAsync();

                        compareDict = compareDataList.ToDictionary(
                            s => s.BranchCode,
                            s => (dynamic)s
                        );
                    }
                }

                foreach (var item in currentData)
                {
                    var dto = new StoreSalesRankDto
                    {
                        StartDate = startDate,
                        EndDate = endDate,
                        BranchCode = item.BranchCode,
                        BranchName = item.BranchName,
                        TotalAmount = item.TotalAmount,
                        TotalQuantity = item.TotalQuantity,
                        OrderCount = item.OrderCount,
                        CustomerCount = item.CustomerCount,
                        AverageOrderValue = item.AvgOrderValue,
                    };

                    if (dateRange.CompareStartDate.HasValue && dateRange.CompareEndDate.HasValue)
                    {
                        decimal compareTotalAmount = 0;
                        int compareTotalQuantity = 0;
                        int compareOrderCount = 0;
                        decimal compareAvgOrderValue = 0;

                        if (compareDict.TryGetValue(item.BranchCode, out var compareData))
                        {
                            compareTotalAmount = compareData.TotalAmount;
                            compareTotalQuantity = compareData.TotalQuantity;
                            compareOrderCount = compareData.OrderCount;
                            compareAvgOrderValue = compareData.AvgOrderValue;
                        }

                        dto.CompareTotalAmount = compareTotalAmount;
                        dto.CompareTotalQuantity = compareTotalQuantity;
                        dto.CompareOrderCount = compareOrderCount;
                        dto.CompareAvgOrderValue = compareAvgOrderValue;

                        dto.TotalAmountGrowth = CalculateGrowth(
                            item.TotalAmount,
                            compareTotalAmount
                        );
                        dto.TotalQuantityGrowth = CalculateGrowth(
                            item.TotalQuantity,
                            compareTotalQuantity
                        );
                        dto.OrderCountGrowth = CalculateGrowth(item.OrderCount, compareOrderCount);
                        dto.AvgOrderValueGrowth = CalculateGrowth(
                            item.AvgOrderValue,
                            compareAvgOrderValue
                        );
                    }

                    result.Add(dto);
                }

                var cacheOptions = new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(RANKING_CACHE_DURATION)
                    .SetSlidingExpiration(TimeSpan.FromMinutes(5));

                _cache.Set(cacheKey, result, cacheOptions);
                _logger.LogInformation(
                    "分店销售排名已缓存: {CacheKey}, 过期时间: {Expiration}",
                    cacheKey,
                    DateTime.Now.Add(RANKING_CACHE_DURATION)
                );

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetStoreSalesRankAsync failed");
                return new List<StoreSalesRankDto>();
            }
        }

        /// <summary>
        /// 获取供应商销售排名
        /// 从日商品分店统计表按供应商汇总
        /// </summary>
        /// <param name="dateRange">日期范围</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <param name="topN">返回前 N 名，默认 20</param>
        /// <param name="supplierCode">供应商代码（可选，用于联动过滤）</param>
        /// <returns>供应商销售排名列表</returns>
        public async Task<List<SupplierSalesRankDto>> GetSupplierSalesRankAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes = null,
            int topN = 200,
            string? supplierCode = null
        )
        {
            var statisticStatus = await GetProductReportStatisticStatusAsync(dateRange);
            return await GetSupplierSalesRankAsync(
                dateRange,
                branchCodes,
                topN,
                supplierCode,
                statisticStatus
            );
        }

        public async Task<List<SupplierSalesRankDto>> GetSupplierSalesRankAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes,
            int topN,
            string? supplierCode,
            ProductReportStatisticStatusDto statisticStatus
        )
        {
            try
            {
                ValidateDateRange(dateRange);
                if (!IsProductStatisticFresh(statisticStatus))
                    return new List<SupplierSalesRankDto>();

                if (_useSupplierRollups)
                    return await GetSupplierRankFromRollupsAsync(dateRange, branchCodes, topN, supplierCode, statisticStatus);

                var cacheKey = SalesDashboardCacheKeys.SupplierRank(
                    dateRange,
                    branchCodes,
                    topN,
                    supplierCode,
                    statisticStatus.CacheVersion
                );

                if (
                    _cache.TryGetValue<List<SupplierSalesRankDto>>(cacheKey, out var cachedResult)
                    && cachedResult != null
                    && cachedResult.Count != 0
                )
                {
                    _logger.LogInformation("从缓存获取供应商销售排名: {CacheKey}", cacheKey);
                    return cachedResult;
                }

                var startDate = dateRange.StartDate.Date;
                var endDate = dateRange.EndDate.Date;
                var supplierCodes = string.IsNullOrWhiteSpace(supplierCode)
                    ? null
                    : new List<string> { supplierCode.Trim() };
                var chinaSupplierCodeSet = await GetChinaSupplierCodeSetAsync();
                var shouldIncludeChinaLocalSupplier = supplierCodes == null
                    || supplierCodes.Any(IsChinaLocalSupplierCode);
                var currentRows = new List<SupplierBranchAggregateRow>();
                var query = await BuildProductReportStatisticQueryAsync(
                    startDate,
                    endDate,
                    branchCodes
                );
                query = ApplyAustralianSupplierStatisticFilter(query, supplierCodes, chinaSupplierCodeSet);
                var rawCurrentRows = await query
                    .GroupBy(s => new { s.Date, s.SupplierCode, s.BranchCode })
                    .Select(s => new SupplierBranchAggregateRow
                    {
                        Date = s.Date,
                        SupplierCode = s.SupplierCode,
                        BranchCode = s.BranchCode,
                        TotalAmount = SqlFunc.AggregateSum(s.TotalAmount),
                        TotalQuantity = SqlFunc.AggregateSum(s.TotalQuantity),
                        OrderCount = SqlFunc.AggregateSum(s.OrderCount),
                        GrossProfit = SqlFunc.AggregateSum(s.GrossProfit),
                        StatisticRowCount = SqlFunc.AggregateCount(s.ProductCode),
                        CostedRowCount = SqlFunc.AggregateCount(s.TotalCost),
                        GrossProfitRowCount = SqlFunc.AggregateCount(s.GrossProfit),
                    })
                    .ToListAsync();
                currentRows = ResolveAustralianSupplierBranchRows(
                    rawCurrentRows,
                    chinaSupplierCodeSet,
                    preserveDate: true
                );
                if (!currentRows.Any())
                {
                    // 商品统计异步生成期间，供应商表先用已存在的供应商分店统计兜底，避免页面空白。
                    currentRows = await QueryAustralianSupplierStoreAggregateRowsAsync(
                        startDate,
                        endDate,
                        branchCodes,
                        supplierCodes
                    );
                }
                currentRows = await AddChinaLocalSupplierFallbackRowsIfMissingAsync(
                    currentRows,
                    startDate,
                    endDate,
                    branchCodes,
                    shouldIncludeChinaLocalSupplier,
                    chinaSupplierCodeSet
                );

                var currentData = BuildSupplierRankAggregates(currentRows)
                    .OrderByDescending(row => row.TotalAmount)
                    .Take(topN)
                    .ToList();

                var currentSupplierCodes = currentData.Select(s => s.SupplierCode).ToList();
                var supplierNameMap = await GetAustralianSupplierNameMapAsync(currentSupplierCodes);
                var compareDict = new Dictionary<string, SupplierCompareMetrics>(
                    StringComparer.OrdinalIgnoreCase
                );

                if (
                    dateRange.CompareStartDate.HasValue
                    && dateRange.CompareEndDate.HasValue
                    && currentSupplierCodes.Any()
                )
                {
                    var compareQuery = await BuildProductReportStatisticQueryAsync(
                        dateRange.CompareStartDate.Value.Date,
                        dateRange.CompareEndDate.Value.Date,
                        branchCodes
                    );
                    compareQuery = ApplyAustralianSupplierStatisticFilter(
                        compareQuery,
                        currentSupplierCodes,
                        chinaSupplierCodeSet
                    );
                    var rawCompareRows = await compareQuery
                            .GroupBy(s => new { s.Date, s.SupplierCode, s.BranchCode })
                            .Select(s => new SupplierBranchAggregateRow
                            {
                                Date = s.Date,
                                SupplierCode = s.SupplierCode,
                                BranchCode = s.BranchCode,
                                TotalAmount = SqlFunc.AggregateSum(s.TotalAmount),
                                OrderCount = SqlFunc.AggregateSum(s.OrderCount),
                                GrossProfit = SqlFunc.AggregateSum(s.GrossProfit),
                                StatisticRowCount = SqlFunc.AggregateCount(s.ProductCode),
                                CostedRowCount = SqlFunc.AggregateCount(s.TotalCost),
                                GrossProfitRowCount = SqlFunc.AggregateCount(s.GrossProfit),
                            })
                            .ToListAsync();
                    var compareRows = ResolveAustralianSupplierBranchRows(
                        rawCompareRows,
                        chinaSupplierCodeSet,
                        preserveDate: true
                    );

                    if (!compareRows.Any())
                    {
                        compareRows = await QueryAustralianSupplierStoreAggregateRowsAsync(
                            dateRange.CompareStartDate.Value.Date,
                            dateRange.CompareEndDate.Value.Date,
                            branchCodes,
                            currentSupplierCodes
                        );
                    }
                    compareRows = await AddChinaLocalSupplierFallbackRowsIfMissingAsync(
                        compareRows,
                        dateRange.CompareStartDate.Value.Date,
                        dateRange.CompareEndDate.Value.Date,
                        branchCodes,
                        currentSupplierCodes.Any(IsChinaLocalSupplierCode),
                        chinaSupplierCodeSet
                    );

                    compareDict = BuildSupplierCompareDict(compareRows, currentSupplierCodes);
                }

                var result = currentData.Select(item =>
                {
                    compareDict.TryGetValue(item.SupplierCode, out var compare);
                    var hasCompare = dateRange.CompareStartDate.HasValue && dateRange.CompareEndDate.HasValue;
                    var dto = new SupplierSalesRankDto
                    {
                        StartDate = startDate,
                        EndDate = endDate,
                        SupplierCode = item.SupplierCode,
                        SupplierName = supplierNameMap.TryGetValue(item.SupplierCode, out var supplierName)
                            ? supplierName
                            : item.SupplierCode,
                        TotalAmount = item.TotalAmount,
                        TotalQuantity = item.TotalQuantity,
                        OrderCount = item.OrderCount,
                        AverageTransaction = item.OrderCount > 0 ? item.TotalAmount / item.OrderCount : 0,
                        StoreCount = item.StoreCount,
                        CompareTotalAmount = hasCompare ? compare.TotalAmount : null,
                        CostStatus = GetCostStatus(item.StatisticRowCount, item.CostedRowCount, item.GrossProfitRowCount),
                        GrossProfit = GetCompleteGrossProfit(
                            item.GrossProfit,
                            item.StatisticRowCount,
                            item.CostedRowCount,
                            item.GrossProfitRowCount
                        ),
                    };
                    dto.GrossMarginRate = CalculateGrossMarginRate(dto.TotalAmount, dto.GrossProfit);

                    if (hasCompare)
                    {
                        dto.CompareCostStatus = GetCostStatus(compare.StatisticRowCount, compare.CostedRowCount, compare.GrossProfitRowCount);
                        dto.CompareOrderCount = compare.OrderCount;
                        dto.CompareAverageTransaction =
                            compare.OrderCount > 0 ? compare.TotalAmount / compare.OrderCount : 0;
                        dto.TotalAmountGrowth = CalculateGrowth(item.TotalAmount, compare.TotalAmount);
                        dto.CompareGrossProfit = GetCompleteGrossProfit(
                            compare.GrossProfit,
                            compare.StatisticRowCount,
                            compare.CostedRowCount,
                            compare.GrossProfitRowCount
                        );
                        dto.CompareGrossMarginRate = CalculateGrossMarginRate(
                            compare.TotalAmount,
                            dto.CompareGrossProfit
                        );
                    }

                    return dto;
                }).ToList();

                var cacheOptions = new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(RANKING_CACHE_DURATION)
                    .SetSlidingExpiration(TimeSpan.FromMinutes(5));

                _cache.Set(cacheKey, result, cacheOptions);
                _logger.LogInformation(
                    "供应商销售排名已缓存: {CacheKey}, 过期时间: {Expiration}",
                    cacheKey,
                    DateTime.Now.Add(RANKING_CACHE_DURATION)
                );

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetSupplierSalesRankAsync failed");
                throw;
            }
        }

        /// <summary>
        /// 获取中国供应商销售排名
        /// 从日商品分店统计表按 POSM 中国供应商映射汇总
        /// </summary>
        /// <param name="dateRange">日期范围</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <param name="topN">返回前 N 名，默认 20</param>
        /// <param name="supplierCode">供应商代码（可选，用于联动过滤）</param>
        /// <returns>中国供应商销售排名列表</returns>
        public async Task<List<ChinaSupplierSalesRankDto>> GetChinaSupplierSalesRankAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes = null,
            int topN = 20,
            string? supplierCode = null
        )
        {
            var statisticStatus = await GetProductReportStatisticStatusAsync(dateRange);
            return await GetChinaSupplierSalesRankAsync(
                dateRange,
                branchCodes,
                topN,
                supplierCode,
                statisticStatus
            );
        }

        public async Task<List<ChinaSupplierSalesRankDto>> GetChinaSupplierSalesRankAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes,
            int topN,
            string? supplierCode,
            ProductReportStatisticStatusDto statisticStatus
        )
        {
            try
            {
                ValidateDateRange(dateRange);
                if (!IsProductStatisticFresh(statisticStatus))
                    return new List<ChinaSupplierSalesRankDto>();

                if (_useSupplierRollups)
                    return await GetChinaSupplierRankFromRollupsAsync(dateRange, branchCodes, topN, supplierCode, statisticStatus);

                var cacheKey = SalesDashboardCacheKeys.ChinaSupplierRank(
                    dateRange,
                    branchCodes,
                    topN,
                    supplierCode,
                    statisticStatus.CacheVersion
                );

                if (
                    _cache.TryGetValue<List<ChinaSupplierSalesRankDto>>(
                        cacheKey,
                        out var cachedResult
                    )
                    && cachedResult != null
                    && cachedResult.Count != 0
                )
                {
                    _logger.LogInformation("从缓存获取中国供应商销售排名: {CacheKey}", cacheKey);
                    return cachedResult;
                }

                var startDate = dateRange.StartDate.Date;
                var endDate = dateRange.EndDate.Date;
                var requestedSupplierCodes = NormalizeCodes(
                    string.IsNullOrWhiteSpace(supplierCode)
                        ? null
                        : new List<string> { supplierCode.Trim() }
                );
                var chinaProductMap = await GetChinaSupplierProductMapAsync(
                    requestedSupplierCodes.Any() ? requestedSupplierCodes : null
                );
                var targetChinaSupplierCodes = requestedSupplierCodes.Any()
                    ? requestedSupplierCodes.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : await GetChinaSupplierCodeSetAsync(chinaProductMap.Values);

                var query = await BuildProductReportStatisticQueryAsync(
                    startDate,
                    endDate,
                    branchCodes
                );
                query = ApplyChinaSupplierStatisticFilter(
                    query,
                    targetChinaSupplierCodes,
                    chinaProductMap,
                    limitLegacy200Products: requestedSupplierCodes.Any()
                );
                var currentRows = await query
                    .GroupBy(s => new { s.ProductCode, s.BranchCode, s.SupplierCode })
                    .Select(s => new ProductSupplierBranchAggregateRow
                    {
                        ProductCode = s.ProductCode,
                        BranchCode = s.BranchCode,
                        SupplierCode = s.SupplierCode,
                        TotalAmount = SqlFunc.AggregateSum(s.TotalAmount),
                        TotalQuantity = SqlFunc.AggregateSum(s.TotalQuantity),
                        OrderCount = SqlFunc.AggregateSum(s.OrderCount),
                        GrossProfit = SqlFunc.AggregateSum(s.GrossProfit),
                        StatisticRowCount = SqlFunc.AggregateCount(s.ProductCode),
                        CostedRowCount = SqlFunc.AggregateCount(s.TotalCost),
                        GrossProfitRowCount = SqlFunc.AggregateCount(s.GrossProfit),
                    })
                    .ToListAsync();

                var resolvedCurrentRows = ResolveChinaSupplierBranchRows(
                    currentRows,
                    chinaProductMap,
                    targetChinaSupplierCodes
                );
                var hasCurrentProductStatisticRows = currentRows.Any()
                    || (
                        requestedSupplierCodes.Any()
                        && await HasChinaProductStatisticRowsAsync(
                            startDate,
                            endDate,
                            branchCodes,
                            targetChinaSupplierCodes
                        )
                    );

                if (!hasCurrentProductStatisticRows)
                {
                    // 商品统计未覆盖当日时，中国供应商表用独立统计表兜底，避免只查到 POSM 映射后返回空。
                    resolvedCurrentRows = await QueryChinaSupplierStoreAggregateRowsAsync(
                        startDate,
                        endDate,
                        branchCodes,
                        targetChinaSupplierCodes
                    );
                }

                var currentData = BuildSupplierRankAggregates(resolvedCurrentRows)
                    .OrderByDescending(row => row.TotalAmount)
                    .Take(topN)
                    .ToList();

                var currentSupplierCodes = currentData.Select(s => s.SupplierCode).ToList();
                var supplierNameMap = await GetChinaSupplierNameMapAsync(currentSupplierCodes);
                var compareDict = new Dictionary<string, SupplierCompareMetrics>(
                    StringComparer.OrdinalIgnoreCase
                );

                if (
                    dateRange.CompareStartDate.HasValue
                    && dateRange.CompareEndDate.HasValue
                    && currentSupplierCodes.Any()
                )
                {
                    var compareQuery = await BuildProductReportStatisticQueryAsync(
                        dateRange.CompareStartDate.Value.Date,
                        dateRange.CompareEndDate.Value.Date,
                        branchCodes
                    );
                    compareQuery = ApplyChinaSupplierStatisticFilter(
                        compareQuery,
                        targetChinaSupplierCodes,
                        chinaProductMap,
                        limitLegacy200Products: requestedSupplierCodes.Any()
                    );
                    var compareRows = await compareQuery
                        .GroupBy(s => new { s.ProductCode, s.BranchCode, s.SupplierCode })
                        .Select(s => new ProductSupplierBranchAggregateRow
                        {
                            ProductCode = s.ProductCode,
                            BranchCode = s.BranchCode,
                            SupplierCode = s.SupplierCode,
                            TotalAmount = SqlFunc.AggregateSum(s.TotalAmount),
                            OrderCount = SqlFunc.AggregateSum(s.OrderCount),
                            GrossProfit = SqlFunc.AggregateSum(s.GrossProfit),
                            StatisticRowCount = SqlFunc.AggregateCount(s.ProductCode),
                            CostedRowCount = SqlFunc.AggregateCount(s.TotalCost),
                            GrossProfitRowCount = SqlFunc.AggregateCount(s.GrossProfit),
                        })
                        .ToListAsync();

                    var resolvedCompareRows = ResolveChinaSupplierBranchRows(
                        compareRows,
                        chinaProductMap,
                        targetChinaSupplierCodes
                    );
                    var hasCompareProductStatisticRows = compareRows.Any()
                        || (
                            requestedSupplierCodes.Any()
                            && await HasChinaProductStatisticRowsAsync(
                                dateRange.CompareStartDate.Value.Date,
                                dateRange.CompareEndDate.Value.Date,
                                branchCodes,
                                targetChinaSupplierCodes
                            )
                        );

                    if (!hasCompareProductStatisticRows)
                    {
                        resolvedCompareRows = await QueryChinaSupplierStoreAggregateRowsAsync(
                            dateRange.CompareStartDate.Value.Date,
                            dateRange.CompareEndDate.Value.Date,
                            branchCodes,
                            currentSupplierCodes
                        );
                    }

                    compareDict = BuildSupplierCompareDict(resolvedCompareRows, currentSupplierCodes);
                }

                var hasCompare = dateRange.CompareStartDate.HasValue && dateRange.CompareEndDate.HasValue;
                var result = currentData.Select(item =>
                {
                    compareDict.TryGetValue(item.SupplierCode, out var compare);
                    var dto = new ChinaSupplierSalesRankDto
                    {
                        StartDate = startDate,
                        EndDate = endDate,
                        SupplierCode = item.SupplierCode,
                        SupplierName = supplierNameMap.TryGetValue(item.SupplierCode, out var supplierName)
                            ? supplierName
                            : item.SupplierCode,
                        TotalAmount = item.TotalAmount,
                        TotalQuantity = item.TotalQuantity,
                        OrderCount = item.OrderCount,
                        AverageTransaction = item.OrderCount > 0 ? item.TotalAmount / item.OrderCount : 0,
                        StoreCount = item.StoreCount,
                        CompareTotalAmount = hasCompare ? compare.TotalAmount : null,
                        CostStatus = GetCostStatus(item.StatisticRowCount, item.CostedRowCount, item.GrossProfitRowCount),
                        GrossProfit = GetCompleteGrossProfit(
                            item.GrossProfit,
                            item.StatisticRowCount,
                            item.CostedRowCount,
                            item.GrossProfitRowCount
                        ),
                    };
                    dto.GrossMarginRate = CalculateGrossMarginRate(dto.TotalAmount, dto.GrossProfit);

                    if (hasCompare)
                    {
                        dto.CompareCostStatus = GetCostStatus(compare.StatisticRowCount, compare.CostedRowCount, compare.GrossProfitRowCount);
                        dto.CompareOrderCount = compare.OrderCount;
                        dto.CompareAverageTransaction =
                            compare.OrderCount > 0 ? compare.TotalAmount / compare.OrderCount : 0;
                        dto.TotalAmountGrowth = CalculateGrowth(item.TotalAmount, compare.TotalAmount);
                        dto.CompareGrossProfit = GetCompleteGrossProfit(
                            compare.GrossProfit,
                            compare.StatisticRowCount,
                            compare.CostedRowCount,
                            compare.GrossProfitRowCount
                        );
                        dto.CompareGrossMarginRate = CalculateGrossMarginRate(
                            compare.TotalAmount,
                            dto.CompareGrossProfit
                        );
                    }

                    return dto;
                }).ToList();

                var cacheOptions = new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(RANKING_CACHE_DURATION)
                    .SetSlidingExpiration(TimeSpan.FromMinutes(5));

                _cache.Set(cacheKey, result, cacheOptions);
                _logger.LogInformation(
                    "中国供应商销售排名已缓存: {CacheKey}, 过期时间: {Expiration}",
                    cacheKey,
                    DateTime.Now.Add(RANKING_CACHE_DURATION)
                );

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetChinaSupplierSalesRankAsync failed");
                throw;
            }
        }

        /// <summary>
        /// 获取指定供应商在所有分店的销售数据
        /// </summary>
        /// <param name="dateRange">日期范围</param>
        /// <param name="supplierCodes">供应商代码列表</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <returns>供应商分店销售数据列表</returns>
        public async Task<List<SupplierStoreSalesDto>> GetSupplierStoreSalesAsync(
            DateRangeDto dateRange,
            List<string> supplierCodes,
            List<string>? branchCodes = null
        )
        {
            var statisticStatus = await GetProductReportStatisticStatusAsync(dateRange);
            return await GetSupplierStoreSalesAsync(
                dateRange,
                supplierCodes,
                branchCodes,
                statisticStatus
            );
        }

        public async Task<List<SupplierStoreSalesDto>> GetSupplierStoreSalesAsync(
            DateRangeDto dateRange,
            List<string> supplierCodes,
            List<string>? branchCodes,
            ProductReportStatisticStatusDto statisticStatus
        )
        {
            try
            {
                var targetSupplierCodes = NormalizeCodes(supplierCodes);
                if (!targetSupplierCodes.Any())
                    throw new ArgumentException("供应商代码不能为空", nameof(supplierCodes));

                ValidateDateRange(dateRange);
                if (!IsProductStatisticFresh(statisticStatus))
                    return new List<SupplierStoreSalesDto>();

                if (_useSupplierRollups)
                    return await GetSupplierStoresFromRollupsAsync(dateRange, supplierCodes, branchCodes, statisticStatus);

                var cacheKey = SalesDashboardCacheKeys.SupplierStore(
                    dateRange,
                    targetSupplierCodes,
                    branchCodes,
                    statisticStatus.CacheVersion
                );

                if (
                    _cache.TryGetValue<List<SupplierStoreSalesDto>>(cacheKey, out var cachedResult)
                    && cachedResult != null
                    && cachedResult.Count != 0
                )
                {
                    _logger.LogInformation("从缓存获取供应商分店销售数据: {CacheKey}", cacheKey);
                    return cachedResult;
                }

                var startDate = dateRange.StartDate.Date;
                var endDate = dateRange.EndDate.Date;
                var chinaSupplierCodeSet = targetSupplierCodes.Any(IsChinaLocalSupplierCode)
                    ? await GetChinaSupplierCodeSetAsync()
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 商品报告统一从日商品分店统计表汇总，避免供应商分店弹窗和商品明细口径不一致。
                var currentQuery = await BuildProductReportStatisticQueryAsync(
                    startDate,
                    endDate,
                    branchCodes
                );
                currentQuery = ApplyAustralianSupplierStatisticFilter(
                    currentQuery,
                    targetSupplierCodes,
                    chinaSupplierCodeSet
                );
                var rawCurrentData = await currentQuery
                    .GroupBy(s => new { s.Date, s.BranchCode, s.SupplierCode })
                    .Select(s => new SupplierBranchAggregateRow
                    {
                        Date = s.Date,
                        BranchCode = s.BranchCode,
                        SupplierCode = s.SupplierCode,
                        TotalAmount = SqlFunc.AggregateSum(s.TotalAmount),
                        TotalQuantity = SqlFunc.AggregateSum(s.TotalQuantity),
                        OrderCount = SqlFunc.AggregateSum(s.OrderCount),
                        GrossProfit = SqlFunc.AggregateSum(s.GrossProfit),
                        StatisticRowCount = SqlFunc.AggregateCount(s.ProductCode),
                        CostedRowCount = SqlFunc.AggregateCount(s.TotalCost),
                        GrossProfitRowCount = SqlFunc.AggregateCount(s.GrossProfit),
                    })
                    .ToListAsync();
                var currentData = ResolveAustralianSupplierBranchRows(
                    rawCurrentData,
                    chinaSupplierCodeSet,
                    preserveDate: true
                );

                if (!currentData.Any())
                {
                    // 商品统计尚未生成时，供应商下钻用供应商分店统计兜底，保证弹窗有同口径数据。
                    currentData = await QueryAustralianSupplierStoreAggregateRowsAsync(
                        startDate,
                        endDate,
                        branchCodes,
                        targetSupplierCodes
                    );
                }
                currentData = await AddChinaLocalSupplierFallbackRowsIfMissingAsync(
                    currentData,
                    startDate,
                    endDate,
                    branchCodes,
                    targetSupplierCodes.Any(IsChinaLocalSupplierCode),
                    chinaSupplierCodeSet
                );

                var currentBranchCodes = currentData
                    .Select(d => d.BranchCode?.Trim())
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var storeDict = await GetStoreNameMapAsync(currentBranchCodes);
                var supplierNameMap = await GetAustralianSupplierNameMapAsync(
                    currentData.Select(d => d.SupplierCode)
                );

                // 批量获取对比数据
                Dictionary<string, SupplierCompareMetrics> compareDict =
                    new Dictionary<string, SupplierCompareMetrics>(
                        StringComparer.OrdinalIgnoreCase
                    );

                if (
                    dateRange.CompareStartDate.HasValue
                    && dateRange.CompareEndDate.HasValue
                    && currentBranchCodes.Any()
                )
                {
                    var compareQuery = await BuildProductReportStatisticQueryAsync(
                        dateRange.CompareStartDate.Value.Date,
                        dateRange.CompareEndDate.Value.Date,
                        currentBranchCodes.ToList()
                    );
                    compareQuery = ApplyAustralianSupplierStatisticFilter(
                        compareQuery,
                        targetSupplierCodes,
                        chinaSupplierCodeSet
                    );
                    var rawCompareDataList = await compareQuery
                        .GroupBy(s => new { s.Date, s.BranchCode, s.SupplierCode })
                        .Select(s => new SupplierBranchAggregateRow
                        {
                            Date = s.Date,
                            BranchCode = s.BranchCode,
                            SupplierCode = s.SupplierCode,
                            TotalAmount = SqlFunc.AggregateSum(s.TotalAmount),
                            OrderCount = SqlFunc.AggregateSum(s.OrderCount),
                            GrossProfit = SqlFunc.AggregateSum(s.GrossProfit),
                            StatisticRowCount = SqlFunc.AggregateCount(s.ProductCode),
                            CostedRowCount = SqlFunc.AggregateCount(s.TotalCost),
                            GrossProfitRowCount = SqlFunc.AggregateCount(s.GrossProfit),
                        })
                        .ToListAsync();
                    var compareDataList = ResolveAustralianSupplierBranchRows(
                        rawCompareDataList,
                        chinaSupplierCodeSet,
                        preserveDate: true
                    );

                    if (!compareDataList.Any())
                    {
                        compareDataList = await QueryAustralianSupplierStoreAggregateRowsAsync(
                            dateRange.CompareStartDate.Value.Date,
                            dateRange.CompareEndDate.Value.Date,
                            currentBranchCodes.ToList(),
                            targetSupplierCodes
                        );
                    }
                    compareDataList = await AddChinaLocalSupplierFallbackRowsIfMissingAsync(
                        compareDataList,
                        dateRange.CompareStartDate.Value.Date,
                        dateRange.CompareEndDate.Value.Date,
                        currentBranchCodes.ToList(),
                        targetSupplierCodes.Any(IsChinaLocalSupplierCode),
                        chinaSupplierCodeSet
                    );

                    compareDict = BuildSupplierBranchCompareDict(compareDataList);
                }

                var hasCompare = dateRange.CompareStartDate.HasValue && dateRange.CompareEndDate.HasValue;
                var result = currentData
                    .Select(item =>
                    {
                        var branchCode = item.BranchCode ?? string.Empty;
                        var supplierCode = item.SupplierCode ?? string.Empty;
                        var orderCount = item.OrderCount ?? 0;
                        var compareKey = $"{branchCode}|{supplierCode}";
                        compareDict.TryGetValue(compareKey, out var compare);

                        var dto = new SupplierStoreSalesDto
                        {
                            StartDate = startDate,
                            EndDate = endDate,
                            SupplierCode = supplierCode,
                            SupplierName = supplierNameMap.TryGetValue(supplierCode, out var supplierName)
                                ? supplierName
                                : supplierCode,
                            BranchCode = branchCode,
                            BranchName = storeDict.TryGetValue(branchCode, out var branchName)
                                ? branchName
                                : branchCode,
                            TotalAmount = item.TotalAmount,
                            TotalQuantity = item.TotalQuantity,
                            // 分店下钻展示同一供应商在单店的客单数和客单价。
                            OrderCount = orderCount,
                            AverageTransaction =
                                orderCount > 0 ? item.TotalAmount / orderCount : 0,
                            CostStatus = GetCostStatus(item.StatisticRowCount, item.CostedRowCount, item.GrossProfitRowCount),
                            GrossProfit = GetCompleteGrossProfit(
                                item.GrossProfit,
                                item.StatisticRowCount,
                                item.CostedRowCount,
                                item.GrossProfitRowCount
                            ),
                        };
                        dto.GrossMarginRate = CalculateGrossMarginRate(dto.TotalAmount, dto.GrossProfit);

                        if (hasCompare)
                        {
                            dto.CompareCostStatus = GetCostStatus(compare.StatisticRowCount, compare.CostedRowCount, compare.GrossProfitRowCount);
                            dto.CompareTotalAmount = compare.TotalAmount;
                            dto.CompareOrderCount = compare.OrderCount;
                            dto.CompareAverageTransaction =
                                compare.OrderCount > 0 ? compare.TotalAmount / compare.OrderCount : 0;
                            dto.TotalAmountGrowth = CalculateGrowth(
                                item.TotalAmount,
                                compare.TotalAmount
                            );
                            dto.CompareGrossProfit = GetCompleteGrossProfit(
                                compare.GrossProfit,
                                compare.StatisticRowCount,
                                compare.CostedRowCount,
                                compare.GrossProfitRowCount
                            );
                            dto.CompareGrossMarginRate = CalculateGrossMarginRate(
                                compare.TotalAmount,
                                dto.CompareGrossProfit
                            );
                        }

                        return dto;
                    })
                    .OrderByDescending(r => r.TotalAmount)
                    .ToList();

                var cacheOptions = new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(RANKING_CACHE_DURATION)
                    .SetSlidingExpiration(TimeSpan.FromMinutes(5));

                _cache.Set(cacheKey, result, cacheOptions);
                _logger.LogInformation(
                    "供应商分店销售数据已缓存: {CacheKey}, 过期时间: {Expiration}",
                    cacheKey,
                    DateTime.Now.Add(RANKING_CACHE_DURATION)
                );

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetSupplierStoreSalesAsync failed");
                throw;
            }
        }

        /// <summary>
        /// 获取指定分店的供应商销售排名
        /// </summary>
        /// <param name="dateRange">日期范围</param>
        /// <param name="branchCodes">分店代码列表</param>
        /// <param name="topN">返回前 N 名，默认 20</param>
        /// <returns>分店供应商销售数据列表</returns>
        public async Task<List<StoreSupplierSalesDto>> GetStoreSupplierSalesAsync(
            DateRangeDto dateRange,
            List<string> branchCodes,
            int topN = 20
        )
        {
            try
            {
                if (branchCodes == null || !branchCodes.Any())
                    throw new ArgumentException("分店代码不能为空", nameof(branchCodes));

                ValidateDateRange(dateRange);

                var cacheKey = SalesDashboardCacheKeys.StoreSupplier(dateRange, branchCodes, topN);

                if (
                    _cache.TryGetValue<List<StoreSupplierSalesDto>>(cacheKey, out var cachedResult)
                    && cachedResult != null
                    && cachedResult.Count != 0
                )
                {
                    _logger.LogInformation("从缓存获取分店供应商销售数据: {CacheKey}", cacheKey);
                    return cachedResult;
                }

                if (!ValidateDatabaseConnection<StoreSupplierSalesDetail>())
                {
                    return new List<StoreSupplierSalesDto>();
                }

                var startDate = dateRange.StartDate.Date;
                var endDate = dateRange.EndDate.Date;

                var query = _context
                    .Db.Queryable<StoreSupplierSalesDetail>()
                    .Where(s =>
                        s.Date >= startDate
                        && s.Date <= endDate
                        && branchCodes.Contains(s.BranchCode)
                    );

                var currentData = await query
                    .GroupBy(s => new
                    {
                        s.BranchCode,
                        s.SupplierCode,
                        s.SupplierName,
                    })
                    .Select(s => new
                    {
                        BranchCode = s.BranchCode,
                        SupplierCode = s.SupplierCode,
                        SupplierName = s.SupplierName ?? s.SupplierCode,
                        TotalAmount = SqlFunc.AggregateSum(s.TotalAmount),
                        TotalQuantity = SqlFunc.AggregateSum(s.TotalQuantity),
                    })
                    .OrderByDescending(s => s.TotalAmount)
                    .ToListAsync();

                var branchCodeList = branchCodes.ToList();
                var stores = await _context
                    .Db.Queryable<Store>()
                    .Where(s => branchCodeList.Contains(s.StoreCode))
                    .ToListAsync();
                var storeDict = stores.ToDictionary(s => s.StoreCode, s => s.StoreName);

                var result = new List<StoreSupplierSalesDto>();

                // 查询中国供应商数据
                var chinaSupplierQuery = _context
                    .Db.Queryable<StoreSupplierSalesDetail>()
                    .Where(s =>
                        s.Date >= startDate
                        && s.Date <= endDate
                        && s.IsDomestic == true
                        && branchCodes.Contains(s.BranchCode)
                    );

                var chinaSupplierDataList = await chinaSupplierQuery
                    .GroupBy(s => new { s.BranchCode })
                    .Select(s => new
                    {
                        BranchCode = s.BranchCode,
                        TotalAmount = SqlFunc.AggregateSum(s.TotalAmount),
                        TotalQuantity = SqlFunc.AggregateSum(s.TotalQuantity),
                    })
                    .ToListAsync();

                var chinaSupplierDict = chinaSupplierDataList.ToDictionary(
                    s => s.BranchCode,
                    s => new { s.TotalAmount, s.TotalQuantity }
                );

                // 批量获取对比数据
                Dictionary<string, decimal> compareDict = new Dictionary<string, decimal>();
                Dictionary<string, decimal> chinaCompareDict = new Dictionary<string, decimal>();

                if (dateRange.CompareStartDate.HasValue && dateRange.CompareEndDate.HasValue)
                {
                    var compareStartDate = dateRange.CompareStartDate.Value.Date;
                    var compareEndDate = dateRange.CompareEndDate.Value.Date;
                    var supplierCodes = currentData.Select(s => s.SupplierCode).ToList();

                    if (supplierCodes.Any())
                    {
                        var compareQuery = _context
                            .Db.Queryable<StoreSupplierSalesDetail>()
                            .Where(s =>
                                s.Date >= compareStartDate
                                && s.Date <= compareEndDate
                                && s.IsDomestic != true
                                && branchCodes.Contains(s.BranchCode)
                                && supplierCodes.Contains(s.SupplierCode)
                            );

                        var compareDataList = await compareQuery
                            .GroupBy(s => s.SupplierCode)
                            .Select(s => new
                            {
                                SupplierCode = s.SupplierCode,
                                TotalAmount = SqlFunc.AggregateSum(s.TotalAmount),
                            })
                            .ToListAsync();

                        compareDict = compareDataList.ToDictionary(
                            s => s.SupplierCode,
                            s => s.TotalAmount
                        );
                    }

                    // 获取中国供应商对比数据（按分店）
                    var chinaCompareQuery = _context
                        .Db.Queryable<StoreSupplierSalesDetail>()
                        .Where(s =>
                            s.Date >= compareStartDate
                            && s.Date <= compareEndDate
                            && s.IsDomestic == true
                            && branchCodes.Contains(s.BranchCode)
                        );

                    var chinaCompareDataList = await chinaCompareQuery
                        .GroupBy(s => new { s.BranchCode })
                        .Select(s => new
                        {
                            BranchCode = s.BranchCode,
                            TotalAmount = SqlFunc.AggregateSum(s.TotalAmount),
                        })
                        .ToListAsync();

                    chinaCompareDict = chinaCompareDataList.ToDictionary(
                        s => s.BranchCode,
                        s => s.TotalAmount
                    );
                }

                var groupedByBranch = currentData.GroupBy(x => x.BranchCode);

                foreach (var branchGroup in groupedByBranch)
                {
                    var topSuppliers = branchGroup.OrderByDescending(x => x.TotalAmount).Take(topN);

                    foreach (var item in topSuppliers)
                    {
                        var branchName = storeDict.TryGetValue(item.BranchCode, out var storeName)
                            ? storeName
                            : item.BranchCode;

                        decimal totalAmount = item.TotalAmount;
                        int totalQuantity = item.TotalQuantity;
                        decimal? compareTotalAmount = null;

                        // 如果供应商代码为"200"，将中国供应商的数据合并到该供应商
                        if (
                            item.SupplierCode == "200"
                            && chinaSupplierDict.TryGetValue(item.BranchCode, out var chinaData)
                        )
                        {
                            totalAmount += chinaData.TotalAmount;
                            totalQuantity += chinaData.TotalQuantity;
                            compareTotalAmount = chinaCompareDict.TryGetValue(
                                item.BranchCode,
                                out var chinaCompare
                            )
                                ? chinaCompare
                                : null;
                        }

                        var dto = new StoreSupplierSalesDto
                        {
                            StartDate = startDate,
                            EndDate = endDate,
                            BranchCode = item.BranchCode,
                            BranchName = branchName,
                            SupplierCode = item.SupplierCode,
                            SupplierName = item.SupplierName,
                            TotalAmount = totalAmount,
                            TotalQuantity = totalQuantity,
                            // 旧门店供应商明细没有成本快照；已有业务行必须明确标记为缺成本。
                            CostStatus = MissingCostStatus,
                        };

                        if (
                            dateRange.CompareStartDate.HasValue && dateRange.CompareEndDate.HasValue
                        )
                        {
                            decimal supplierCompareAmount = 0;
                            if (compareDict.TryGetValue(item.SupplierCode, out var amount))
                            {
                                supplierCompareAmount = amount;
                            }

                            dto.CompareTotalAmount =
                                supplierCompareAmount + (compareTotalAmount ?? 0);
                            // 字典命中代表同期存在业务行，金额为零或负数也不能当作无活动。
                            dto.CompareCostStatus = compareDict.ContainsKey(item.SupplierCode)
                                || (item.SupplierCode == "200" && chinaCompareDict.ContainsKey(item.BranchCode))
                                ? MissingCostStatus
                                : NoActivityCostStatus;
                            dto.TotalAmountGrowth = CalculateGrowth(
                                totalAmount,
                                dto.CompareTotalAmount ?? 0
                            );
                        }

                        result.Add(dto);
                    }
                }

                var cacheOptions = new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(RANKING_CACHE_DURATION)
                    .SetSlidingExpiration(TimeSpan.FromMinutes(5));

                _cache.Set(cacheKey, result, cacheOptions);
                _logger.LogInformation(
                    "分店供应商销售数据已缓存: {CacheKey}, 过期时间: {Expiration}",
                    cacheKey,
                    DateTime.Now.Add(RANKING_CACHE_DURATION)
                );

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetStoreSupplierSalesAsync failed");
                return new List<StoreSupplierSalesDto>();
            }
        }

        /// <summary>
        /// 获取销售产品明细数据（分页）
        /// 从 POSM 数据库的 SalesOrder 和 SalesOrderDetail 表查询
        /// </summary>
        /// <param name="dateRange">日期范围</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <param name="supplierCodes">供应商代码列表（可选）</param>
        /// <param name="pageIndex">页码，从 1 开始</param>
        /// <param name="pageSize">每页大小，默认 100</param>
        /// <returns>分页的产品销售明细</returns>
        public async Task<PagedSalesProductDetailDto> GetSalesProductDetailsAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes = null,
            List<string>? supplierCodes = null,
            int pageIndex = 1,
            int pageSize = 100
        )
        {
            try
            {
                ValidateDateRange(dateRange);

                var cacheKey = SalesDashboardCacheKeys.ProductDetail(
                    dateRange,
                    branchCodes,
                    supplierCodes,
                    pageIndex,
                    pageSize
                );

                if (
                    _cache.TryGetValue<PagedSalesProductDetailDto>(cacheKey, out var cachedResult)
                    && cachedResult != null
                )
                {
                    _logger.LogInformation("从缓存获取产品销售明细: {CacheKey}", cacheKey);
                    return cachedResult;
                }

                var startDate = dateRange.StartDate.Date;
                var endDate = dateRange.EndDate.Date.AddDays(1);

                var filteredSupplierCodes = supplierCodes?
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code.Trim())
                    .Distinct()
                    .ToList();
                var hasSupplierFilter = filteredSupplierCodes?.Count > 0;
                int totalCount;

                if (hasSupplierFilter)
                {
                    var supplierQuery = _posmContext
                        .Db.Queryable<SalesOrder, SalesOrderDetail, PosmProductSupplierMapping>(
                            (o, d, m) =>
                                o.OrderGuid == d.OrderGuid
                                && d.ProductCode == m.ProductCode
                                && !m.IsDeleted
                        )
                        .Where((o, d, m) => o.OrderTime >= startDate && o.OrderTime < endDate);

                    if (branchCodes != null && branchCodes.Any())
                    {
                        supplierQuery = supplierQuery.Where(
                            (o, d, m) => branchCodes.Contains(o.BranchCode ?? string.Empty)
                        );
                    }

                    var requiredSupplierCodes = filteredSupplierCodes!;
                    supplierQuery = supplierQuery.Where(
                        (o, d, m) =>
                            m.ChinaSupplierCode != null
                            && requiredSupplierCodes.Contains(m.ChinaSupplierCode)
                    );

                    totalCount = await supplierQuery.CountAsync();
                    var skip = (pageIndex - 1) * pageSize;

                    var salesData = await supplierQuery
                        .OrderByDescending((o, d, m) => d.Quantity)
                        .Select(
                            (o, d, m) =>
                                new
                                {
                                    ProductCode = d.ProductCode ?? string.Empty,
                                    ProductName = d.ProductName,
                                    Quantity = d.Quantity ?? 0,
                                    ActualAmount = d.ActualAmount ?? 0,
                                    Price = d.Price ?? 0,
                                    Subtotal = d.Subtotal,
                                    Qty = d.Quantity,
                                }
                        )
                        .Skip(skip)
                        .Take(pageSize)
                        .ToListAsync();

                    var productCodes = salesData
                        .Where(s => !string.IsNullOrEmpty(s.ProductCode))
                        .Select(s => s.ProductCode)
                        .Distinct()
                        .ToList();

                    var products = new List<Product>();
                    if (productCodes.Any())
                    {
                        products = await _context
                            .Db.Queryable<Product>()
                            .Where(p => p.ProductCode != null && productCodes.Contains(p.ProductCode))
                            .ToListAsync();
                    }

                    var productDict = products.ToDictionary(p => p.ProductCode!);

                    var data = salesData
                        .Select(s => new SalesProductDetailDto
                        {
                            ProductCode = productDict.TryGetValue(s.ProductCode, out var product)
                                ? product.ItemNumber ?? s.ProductCode
                                : s.ProductCode,
                            ProductImage = productDict.TryGetValue(
                                s.ProductCode,
                                out var productWithImage
                            )
                                ? productWithImage.ProductImage
                                : null,
                            ProductName = s.ProductName,
                            Quantity = s.Qty ?? 0,
                            SalesAmount = s.ActualAmount,
                            UnitPrice = s.Price,
                            OriginalPrice =
                                s.Subtotal.HasValue && s.Qty.HasValue && s.Qty.Value > 0
                                    ? s.Subtotal.Value / s.Qty.Value
                                    : null,
                        })
                        .ToList();

                    var result = new PagedSalesProductDetailDto
                    {
                        Data = data,
                        Total = totalCount,
                        PageIndex = pageIndex,
                        PageSize = pageSize,
                    };

                    var cacheOptions = new MemoryCacheEntryOptions()
                        .SetAbsoluteExpiration(DETAIL_CACHE_DURATION)
                        .SetSlidingExpiration(TimeSpan.FromMinutes(1));

                    _cache.Set(cacheKey, result, cacheOptions);
                    _logger.LogInformation(
                        "产品销售明细已缓存: {CacheKey}, 过期时间: {Expiration}",
                        cacheKey,
                        DateTime.Now.Add(DETAIL_CACHE_DURATION)
                    );

                    return result;
                }
                else
                {
                    var baseQuery = _posmContext
                        .Db.Queryable<SalesOrder, SalesOrderDetail>(
                            (o, d) => o.OrderGuid == d.OrderGuid
                        )
                        .Where((o, d) => o.OrderTime >= startDate && o.OrderTime < endDate);

                    if (branchCodes != null && branchCodes.Any())
                    {
                        baseQuery = baseQuery.Where(
                            (o, d) => branchCodes.Contains(o.BranchCode ?? string.Empty)
                        );
                    }

                    totalCount = await baseQuery.CountAsync();
                    var skip = (pageIndex - 1) * pageSize;

                    var salesData = await baseQuery
                        .OrderByDescending((o, d) => d.Quantity)
                        .Select(
                            (o, d) =>
                                new
                                {
                                    ProductCode = d.ProductCode ?? string.Empty,
                                    ProductName = d.ProductName,
                                    Quantity = d.Quantity ?? 0,
                                    ActualAmount = d.ActualAmount ?? 0,
                                    Price = d.Price ?? 0,
                                    Subtotal = d.Subtotal,
                                    Qty = d.Quantity,
                                }
                        )
                        .Skip(skip)
                        .Take(pageSize)
                        .ToListAsync();

                    var productCodes = salesData
                        .Where(s => !string.IsNullOrEmpty(s.ProductCode))
                        .Select(s => s.ProductCode)
                        .Distinct()
                        .ToList();

                    var products = new List<Product>();
                    if (productCodes.Any())
                    {
                        products = await _context
                            .Db.Queryable<Product>()
                            .Where(p => p.ProductCode != null && productCodes.Contains(p.ProductCode))
                            .ToListAsync();
                    }

                    var productDict = products.ToDictionary(p => p.ProductCode ?? string.Empty);

                    var data = salesData
                        .Select(s => new SalesProductDetailDto
                        {
                            ProductCode = productDict.TryGetValue(s.ProductCode, out var product)
                                ? product.ItemNumber ?? s.ProductCode
                                : s.ProductCode,
                            ProductImage = productDict.TryGetValue(
                                s.ProductCode,
                                out var productWithImage
                            )
                                ? productWithImage.ProductImage
                                : null,
                            ProductName = s.ProductName,
                            Quantity = s.Qty ?? 0,
                            SalesAmount = s.ActualAmount,
                            UnitPrice = s.Price,
                            OriginalPrice =
                                s.Subtotal.HasValue && s.Qty.HasValue && s.Qty.Value > 0
                                    ? s.Subtotal.Value / s.Qty.Value
                                    : null,
                        })
                        .ToList();

                    var result = new PagedSalesProductDetailDto
                    {
                        Data = data,
                        Total = totalCount,
                        PageIndex = pageIndex,
                        PageSize = pageSize,
                    };

                    var cacheOptions = new MemoryCacheEntryOptions()
                        .SetAbsoluteExpiration(DETAIL_CACHE_DURATION)
                        .SetSlidingExpiration(TimeSpan.FromMinutes(1));

                    _cache.Set(cacheKey, result, cacheOptions);
                    _logger.LogInformation(
                        "产品销售明细已缓存: {CacheKey}, 过期时间: {Expiration}",
                        cacheKey,
                        DateTime.Now.Add(DETAIL_CACHE_DURATION)
                    );

                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetSalesProductDetailsAsync failed");
                return new PagedSalesProductDetailDto
                {
                    Data = new List<SalesProductDetailDto>(),
                    Total = 0,
                    PageIndex = pageIndex,
                    PageSize = pageSize,
                };
            }
        }

        /// <summary>
        /// 计算增长率（decimal 类型）
        /// </summary>
        /// <param name="current">当前值</param>
        /// <param name="compare">对比值</param>
        /// <returns>增长率百分比，若对比值为 0 且当前值大于 0 返回 100，否则返回 0</returns>
        private decimal? CalculateGrowth(decimal current, decimal compare)
        {
            if (compare == 0)
                return current > 0 ? 100 : 0;
            return ((current - compare) / compare) * 100;
        }

        /// <summary>
        /// 计算增长率（int 类型）
        /// </summary>
        /// <param name="current">当前值</param>
        /// <param name="compare">对比值</param>
        /// <returns>增长率百分比，若对比值为 0 且当前值大于 0 返回 100，否则返回 0</returns>
        private decimal? CalculateGrowth(int current, int compare)
        {
            if (compare == 0)
                return current > 0 ? 100 : 0;
            return ((decimal)(current - compare) / compare) * 100;
        }

        /// <summary>
        /// 获取增强版销售产品明细数据（含折扣信息，分页）
        /// 支持按本地供应商和中国供应商过滤
        /// </summary>
        /// <param name="dateRange">日期范围</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <param name="localSupplierCodes">本地供应商代码列表（可选）</param>
        /// <param name="chinaSupplierCodes">中国供应商代码列表（可选）</param>
        /// <param name="pageIndex">页码，从 1 开始</param>
        /// <param name="pageSize">每页大小，默认 100</param>
        /// <param name="productSearch">商品货号/条码搜索词（可选）</param>
        /// <param name="chinaSupplierScope">是否限制为全部中国供应商商品</param>
        /// <param name="sortField">排序字段 amount/quantity/unitPrice（可选，默认金额）</param>
        /// <param name="sortOrder">排序方向 asc/desc（可选，默认降序）</param>
        /// <returns>分页的含折扣信息的产品销售明细</returns>
        public async Task<PagedSalesProductDetailWithDiscountDto> GetEnhancedSalesProductDetailsAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes = null,
            List<string>? localSupplierCodes = null,
            List<string>? chinaSupplierCodes = null,
            int pageIndex = 1,
            int pageSize = 100,
            string? productSearch = null,
            bool chinaSupplierScope = false,
            string? sortField = null,
            string? sortOrder = null
        )
        {
            var statisticStatus = await GetProductReportStatisticStatusAsync(dateRange);
            return await GetEnhancedSalesProductDetailsAsync(
                dateRange,
                branchCodes,
                localSupplierCodes,
                chinaSupplierCodes,
                pageIndex,
                pageSize,
                productSearch,
                statisticStatus,
                chinaSupplierScope,
                sortField,
                sortOrder
            );
        }

        public async Task<PagedSalesProductDetailWithDiscountDto> GetEnhancedSalesProductDetailsAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes,
            List<string>? localSupplierCodes,
            List<string>? chinaSupplierCodes,
            int pageIndex,
            int pageSize,
            string? productSearch,
            ProductReportStatisticStatusDto statisticStatus,
            bool chinaSupplierScope = false,
            string? sortField = null,
            string? sortOrder = null
        )
        {
            try
            {
                ValidateDateRange(dateRange);
                pageIndex = Math.Max(1, pageIndex);
                pageSize = Math.Clamp(pageSize, 1, 100);
                // 排序在分页之前完成；三条读取路径共用同一个归一化结果，未知参数一律回退为金额降序。
                var sort = ProductReportSort.Parse(sortField, sortOrder);
                if (!IsProductStatisticFresh(statisticStatus))
                {
                    return new PagedSalesProductDetailWithDiscountDto
                    {
                        Data = new List<SalesProductDetailWithDiscountDto>(),
                        Total = 0,
                        PageIndex = pageIndex,
                        PageSize = pageSize,
                    };
                }
                var normalizedProductSearch = productSearch?.Trim();
                if (string.IsNullOrWhiteSpace(normalizedProductSearch))
                {
                    normalizedProductSearch = null;
                }

                if (_useSupplierRollups && _context.Db.CurrentConnectionConfig.DbType == DbType.SqlServer)
                {
                    // 大范围商品和中国商品共用数据库分页，并在读前读后校验完整统计版本。
                    return await ReadCompleteReportAsync(
                        dateRange,
                        statisticStatus,
                        version => SalesDashboardCacheKeys.EnhancedProductDetail(
                            dateRange, branchCodes, localSupplierCodes, chinaSupplierCodes,
                            pageIndex, pageSize, normalizedProductSearch, version, chinaSupplierScope,
                            sort.CacheToken),
                        service => service.GetEnhancedSalesProductDetailsSqlServerAsync(
                            dateRange, branchCodes, localSupplierCodes, chinaSupplierCodes,
                            pageIndex, pageSize, normalizedProductSearch, chinaSupplierScope, sort),
                        () => new PagedSalesProductDetailWithDiscountDto
                        {
                            Data = new(), Total = 0, PageIndex = pageIndex, PageSize = pageSize,
                        });
                }

                _logger.LogInformation(
                    "[GetEnhancedSalesProductDetailsAsync] Processing request: StartDate={StartDate}, EndDate={EndDate}, CompareStartDate={CompareStartDate}, CompareEndDate={CompareEndDate}, HasSupplierFilter={HasSupplierFilter}, HasProductSearch={HasProductSearch}, ChinaSupplierScope={ChinaSupplierScope}, Sort={Sort}",
                    dateRange.StartDate,
                    dateRange.EndDate,
                    dateRange.CompareStartDate,
                    dateRange.CompareEndDate,
                    chinaSupplierScope
                        || (localSupplierCodes != null && localSupplierCodes.Any())
                        || (chinaSupplierCodes != null && chinaSupplierCodes.Any()),
                    normalizedProductSearch != null,
                    chinaSupplierScope,
                    sort.Token
                );

                var cacheKey = SalesDashboardCacheKeys.EnhancedProductDetail(
                    dateRange,
                    branchCodes,
                    localSupplierCodes,
                    chinaSupplierCodes,
                    pageIndex,
                    pageSize,
                    normalizedProductSearch,
                    statisticStatus.CacheVersion,
                    chinaSupplierScope,
                    sort.CacheToken
                );

                if (
                    _cache.TryGetValue<PagedSalesProductDetailWithDiscountDto>(
                        cacheKey,
                        out var cachedResult
                    )
                    && cachedResult != null
                )
                {
                    _logger.LogInformation("从缓存获取增强产品销售明细: {CacheKey}", cacheKey);
                    return cachedResult;
                }

                // 商品报告统一读取日商品分店统计表，避免订单明细和供应商统计表口径不一致。
                var startDate = dateRange.StartDate.Date;
                var endDate = dateRange.EndDate.Date;
                var compareStartDate = dateRange.CompareStartDate?.Date;
                var compareEndDate = dateRange.CompareEndDate?.Date;
                var normalizedChinaSupplierCodes = NormalizeCodes(chinaSupplierCodes);
                // 显式中国范围优先于遗留本地供应商参数，避免客户端切换时残留参数形成交集。
                var effectiveLocalSupplierCodes = chinaSupplierScope ? null : localSupplierCodes;
                var normalizedLocalSupplierCodes = NormalizeCodes(effectiveLocalSupplierCodes);
                // 默认商品主表不涉及中国供应商映射时，直接在数据库完成当前/同期并集与分页。
                // 200 或中国供应商仍保留旧路径，避免新旧中国货映射口径被改变。
                if (
                    !chinaSupplierScope
                    && !normalizedChinaSupplierCodes.Any()
                    && !normalizedLocalSupplierCodes.Any(IsChinaLocalSupplierCode)
                )
                {
                    var fastPathResult = await GetEnhancedSalesProductDetailsFastPathAsync(
                        startDate,
                        endDate,
                        compareStartDate,
                        compareEndDate,
                        branchCodes,
                        effectiveLocalSupplierCodes,
                        pageIndex,
                        pageSize,
                        normalizedProductSearch,
                        sort
                    );
                    var fastPathCacheOptions = new MemoryCacheEntryOptions()
                        .SetAbsoluteExpiration(DETAIL_CACHE_DURATION)
                        .SetSlidingExpiration(TimeSpan.FromMinutes(1));
                    _cache.Set(cacheKey, fastPathResult, fastPathCacheOptions);
                    return fastPathResult;
                }
                Dictionary<string, string> chinaProductMap;
                if (chinaSupplierScope && !normalizedChinaSupplierCodes.Any())
                {
                    // 中国供应商页未点选具体供应商时，范围应是全部中国供应商，而不是未筛选商品。
                    // 同时收集旧 200 商品映射和直接写入统计表的中国供应商代码。
                    chinaProductMap = await GetChinaSupplierProductMapAsync();
                    normalizedChinaSupplierCodes = (
                        await GetChinaSupplierCodeSetAsync(chinaProductMap.Values)
                    ).ToList();
                }
                else
                {
                    chinaProductMap = normalizedChinaSupplierCodes.Any()
                        ? await GetChinaSupplierProductMapAsync(normalizedChinaSupplierCodes)
                        : new Dictionary<string, string>();
                }
                if (chinaSupplierScope && !normalizedChinaSupplierCodes.Any())
                {
                    // 显式中国范围无法解析到任何供应商时必须返回空，不能退化为未筛选全商品。
                    return new PagedSalesProductDetailWithDiscountDto
                    {
                        Data = new List<SalesProductDetailWithDiscountDto>(),
                        Total = 0,
                        PageIndex = pageIndex,
                        PageSize = pageSize,
                    };
                }
                var currentData = await QueryProductReportProductAggregatesAsync(
                    startDate,
                    endDate,
                    branchCodes,
                    effectiveLocalSupplierCodes,
                    normalizedChinaSupplierCodes,
                    chinaProductMap,
                    normalizedProductSearch
                );

                var compareData = new List<ProductReportProductAggregateRow>();
                if (compareStartDate.HasValue && compareEndDate.HasValue)
                {
                    compareData = await QueryProductReportProductAggregatesAsync(
                        compareStartDate.Value,
                        compareEndDate.Value,
                        branchCodes,
                        effectiveLocalSupplierCodes,
                        normalizedChinaSupplierCodes,
                        chinaProductMap,
                        normalizedProductSearch
                    );
                }

                var currentDataDict = currentData
                    .Where(x => !string.IsNullOrWhiteSpace(x.ProductCode))
                    .ToDictionary(x => x.ProductCode, StringComparer.OrdinalIgnoreCase);
                var compareDataDict = compareData
                    .Where(x => !string.IsNullOrWhiteSpace(x.ProductCode))
                    .ToDictionary(x => x.ProductCode, StringComparer.OrdinalIgnoreCase);
                // 商品报告必须把当前期和同期商品取并集，否则同期独有商品会在主表里消失。
                var productCodes = currentDataDict.Keys
                    .Union(compareDataDict.Keys, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var totalCount = productCodes.Count;
                var skip = (pageIndex - 1) * pageSize;
                var candidates = productCodes.Select(code => new
                {
                    ProductCode = code,
                    Current = currentDataDict.GetValueOrDefault(code),
                    Compare = compareDataDict.GetValueOrDefault(code),
                });
                // 与数据库分页同一口径：本期值 → 同期值 → 商品编码；某期无数据按 0 参与排序。
                decimal SortValue(ProductReportProductAggregateRow? row) =>
                    row is null ? 0m : sort.ValueOf(row.SalesAmount, row.Quantity);
                var orderedCandidates = sort.Ascending
                    ? candidates.OrderBy(x => SortValue(x.Current)).ThenBy(x => SortValue(x.Compare))
                    : candidates.OrderByDescending(x => SortValue(x.Current)).ThenByDescending(x => SortValue(x.Compare));
                var pageRows = orderedCandidates
                    .ThenBy(x => x.ProductCode)
                    .Skip(skip)
                    .Take(pageSize)
                    .ToList();
                var pageProductCodes = pageRows.Select(x => x.ProductCode).ToList();
                var products = pageProductCodes.Any()
                    ? await _context
                        .Db.Queryable<Product>()
                        .Where(p => p.ProductCode != null && pageProductCodes.Contains(p.ProductCode))
                        .Select(p => new ProductInfo
                        {
                            ProductCode = p.ProductCode ?? string.Empty,
                            ItemNumber = p.ItemNumber,
                            ProductImage = p.ProductImage,
                        })
                        .ToListAsync()
                    : new List<ProductInfo>();
                var productDict = products.ToDictionary(p => p.ProductCode, StringComparer.OrdinalIgnoreCase);

                var data = pageRows
                    .Select(row =>
                    {
                        var x = row.Current;
                        var compareData = row.Compare;
                        var result = new SalesProductDetailWithDiscountDto
                        {
                            ProductCode = row.ProductCode,
                            ItemNumber = productDict.TryGetValue(row.ProductCode, out var p)
                                ? (string?)p.ItemNumber
                                : null,
                            ProductImage = productDict.TryGetValue(row.ProductCode, out var p2)
                                ? (string?)p2.ProductImage
                                : null,
                            ProductName = x?.ProductName ?? compareData?.ProductName,
                            Quantity = x?.Quantity ?? 0,
                            DiscountedQuantity = 0,
                            SalesAmount = x?.SalesAmount ?? 0,
                            AverageUnitPrice = x is { Quantity: > 0 } ? x.SalesAmount / x.Quantity : 0,
                            AverageOriginalPrice = null,
                            OrderCount = x?.OrderCount ?? 0,
                            CostStatus = x == null
                                ? NoActivityCostStatus
                                : GetCostStatus(x.StatisticRowCount, x.CostedRowCount, x.GrossProfitRowCount),
                            GrossProfit = x == null
                                ? null
                                : GetCompleteGrossProfit(
                                    x.GrossProfit,
                                    x.StatisticRowCount,
                                    x.CostedRowCount,
                                    x.GrossProfitRowCount
                                ),
                        };
                        result.GrossMarginRate = CalculateGrossMarginRate(
                            result.SalesAmount,
                            result.GrossProfit
                        );

                        if (compareData != null)
                        {
                            result.CompareCostStatus = GetCostStatus(
                                compareData.StatisticRowCount,
                                compareData.CostedRowCount,
                                compareData.GrossProfitRowCount
                            );
                            result.QuantityLY = compareData.Quantity;
                            result.DiscountedQuantityLY = 0;
                            result.SalesAmountLY = compareData.SalesAmount;
                            result.AverageUnitPriceLY =
                                compareData.Quantity > 0 ? compareData.SalesAmount / compareData.Quantity : 0;
                            result.AverageOriginalPriceLY = null;
                            result.OrderCountLY = compareData.OrderCount;
                            result.GrossProfitLY = GetCompleteGrossProfit(
                                compareData.GrossProfit,
                                compareData.StatisticRowCount,
                                compareData.CostedRowCount,
                                compareData.GrossProfitRowCount
                            );
                            result.GrossMarginRateLY = CalculateGrossMarginRate(
                                result.SalesAmountLY,
                                result.GrossProfitLY
                            );
                        }

                        return result;
                    })
                    .ToList();

                var resultPage = new PagedSalesProductDetailWithDiscountDto
                {
                    Data = data,
                    Total = totalCount,
                    PageIndex = pageIndex,
                    PageSize = pageSize,
                };

                var cacheOptions = new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(DETAIL_CACHE_DURATION)
                    .SetSlidingExpiration(TimeSpan.FromMinutes(1));

                _cache.Set(cacheKey, resultPage, cacheOptions);
                _logger.LogInformation(
                    "增强产品销售明细已缓存: {CacheKey}, 过期时间: {Expiration}",
                    cacheKey,
                    DateTime.Now.Add(DETAIL_CACHE_DURATION)
                );

                return resultPage;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetEnhancedSalesProductDetailsAsync failed");
                throw;
            }
        }

        /// <summary>
        /// 获取指定产品在所有分店的销售数据
        /// </summary>
        /// <param name="dateRange">日期范围</param>
        /// <param name="productCode">产品代码</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <returns>产品在各分店的销售数据列表</returns>
        public async Task<List<ProductBranchSalesDto>> GetProductSalesByAllBranchesAsync(
            DateRangeDto dateRange,
            string productCode,
            List<string>? branchCodes = null
        )
        {
            var statisticStatus = await GetProductReportStatisticStatusAsync(dateRange);
            return await GetProductSalesByAllBranchesAsync(
                dateRange,
                productCode,
                branchCodes,
                statisticStatus
            );
        }

        public async Task<List<ProductBranchSalesDto>> GetProductSalesByAllBranchesAsync(
            DateRangeDto dateRange,
            string productCode,
            List<string>? branchCodes,
            ProductReportStatisticStatusDto statisticStatus
        )
        {
            ValidateDateRange(dateRange);
            if (branchCodes != null && NormalizeCodes(branchCodes).Count == 0)
                return new List<ProductBranchSalesDto>();
            if (_useSupplierRollups)
                return await ReadCompleteReportAsync(
                    dateRange, statisticStatus,
                    version => SalesDashboardCacheKeys.ProductBranch(dateRange, productCode, branchCodes, version),
                    service => service.GetProductSalesByAllBranchesCoreAsync(dateRange, productCode, branchCodes, statisticStatus, useCache: false),
                    () => new List<ProductBranchSalesDto>());
            return await GetProductSalesByAllBranchesCoreAsync(dateRange, productCode, branchCodes, statisticStatus);
        }

        private async Task<List<ProductBranchSalesDto>> GetProductSalesByAllBranchesCoreAsync(
            DateRangeDto dateRange,
            string productCode,
            List<string>? branchCodes,
            ProductReportStatisticStatusDto statisticStatus,
            bool useCache = true
        )
        {
            try
            {
                ValidateDateRange(dateRange);
                if (!IsProductStatisticFresh(statisticStatus))
                    return new List<ProductBranchSalesDto>();

                // 完整版本读取由外层校验后统一缓存，不能把旧路径缓存晋升为完整快照。
                var cacheKey = useCache ? SalesDashboardCacheKeys.ProductBranch(
                    dateRange,
                    productCode,
                    branchCodes,
                    statisticStatus.CacheVersion
                ) : string.Empty;

                if (
                    useCache && _cache.TryGetValue<List<ProductBranchSalesDto>>(cacheKey, out var cachedResult)
                    && cachedResult != null
                    && cachedResult.Count != 0
                )
                {
                    _logger.LogInformation("从缓存获取产品各分店销售数据: {CacheKey}", cacheKey);
                    return cachedResult;
                }

                var startDate = dateRange.StartDate.Date;
                var endDate = dateRange.EndDate.Date;
                var compareStartDate = dateRange.CompareStartDate?.Date;
                var compareEndDate = dateRange.CompareEndDate?.Date;

                var salesQuery = await BuildProductReportStatisticQueryAsync(
                    startDate,
                    endDate,
                    branchCodes
                );
                salesQuery = salesQuery.Where(s => s.ProductCode == productCode);

                var salesData = await salesQuery
                    .GroupBy(s => s.BranchCode)
                    .Select(s => new ProductBranchAggregateRow
                    {
                        BranchCode = s.BranchCode ?? string.Empty,
                        Quantity = SqlFunc.AggregateSum(s.TotalQuantity),
                        SalesAmount = SqlFunc.AggregateSum(s.TotalAmount),
                        GrossProfit = SqlFunc.AggregateSum(s.GrossProfit),
                        StatisticRowCount = SqlFunc.AggregateCount(s.ProductCode),
                        CostedRowCount = SqlFunc.AggregateCount(s.TotalCost),
                        GrossProfitRowCount = SqlFunc.AggregateCount(s.GrossProfit),
                    })
                    .ToListAsync();

                var compareSalesData = new List<ProductBranchAggregateRow>();
                if (compareStartDate.HasValue && compareEndDate.HasValue)
                {
                    var compareQuery = await BuildProductReportStatisticQueryAsync(
                        compareStartDate.Value,
                        compareEndDate.Value,
                        branchCodes
                    );
                    var compareRows = await compareQuery
                        .Where(s => s.ProductCode == productCode)
                        .GroupBy(s => s.BranchCode)
                        .Select(s => new ProductBranchAggregateRow
                        {
                            BranchCode = s.BranchCode ?? string.Empty,
                            Quantity = SqlFunc.AggregateSum(s.TotalQuantity),
                            SalesAmount = SqlFunc.AggregateSum(s.TotalAmount),
                            GrossProfit = SqlFunc.AggregateSum(s.GrossProfit),
                            StatisticRowCount = SqlFunc.AggregateCount(s.ProductCode),
                            CostedRowCount = SqlFunc.AggregateCount(s.TotalCost),
                            GrossProfitRowCount = SqlFunc.AggregateCount(s.GrossProfit),
                        })
                        .ToListAsync();
                    compareSalesData = compareRows;
                }

                var currentSalesByBranch = salesData
                    .Where(x => !string.IsNullOrWhiteSpace(x.BranchCode))
                    .ToDictionary(
                        x => x.BranchCode ?? string.Empty,
                        x => new ProductBranchAggregateRow
                        {
                            BranchCode = x.BranchCode,
                            Quantity = x.Quantity,
                            SalesAmount = x.SalesAmount,
                            GrossProfit = x.GrossProfit,
                            StatisticRowCount = x.StatisticRowCount,
                            CostedRowCount = x.CostedRowCount,
                            GrossProfitRowCount = x.GrossProfitRowCount,
                        },
                        StringComparer.OrdinalIgnoreCase
                    );
                var compareSalesByBranch = compareSalesData
                    .Where(x => !string.IsNullOrWhiteSpace(x.BranchCode))
                    .ToDictionary(x => x.BranchCode, StringComparer.OrdinalIgnoreCase);
                // 弹窗按当前期和同期分店取并集，保证同期独有分店也能看到。
                var branchCodeSet = currentSalesByBranch.Keys
                    .Union(compareSalesByBranch.Keys, StringComparer.OrdinalIgnoreCase)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var storeNameMap = await GetStoreNameMapAsync(branchCodeSet);

                var result = branchCodeSet
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(branchCode =>
                    {
                        currentSalesByBranch.TryGetValue(branchCode, out var current);
                        compareSalesByBranch.TryGetValue(branchCode, out var compare);

                        return new ProductBranchSalesDto
                        {
                            BranchCode = branchCode,
                            BranchName = storeNameMap.TryGetValue(branchCode, out var storeName)
                                ? storeName
                                : branchCode,
                            Quantity = current?.Quantity ?? 0,
                            DiscountedQuantity = 0,
                            SalesAmount = current?.SalesAmount ?? 0,
                            CompareQuantity = compare?.Quantity ?? 0,
                            CompareSalesAmount = compare?.SalesAmount ?? 0,
                            AverageUnitPrice =
                                current is { Quantity: > 0 } ? current.SalesAmount / current.Quantity : 0,
                            CompareAverageUnitPrice =
                                compare is { Quantity: > 0 } ? compare.SalesAmount / compare.Quantity : 0,
                            CostStatus = GetCostStatus(
                                current?.StatisticRowCount ?? 0,
                                current?.CostedRowCount ?? 0,
                                current?.GrossProfitRowCount ?? 0
                            ),
                            CompareCostStatus = GetCostStatus(
                                compare?.StatisticRowCount ?? 0,
                                compare?.CostedRowCount ?? 0,
                                compare?.GrossProfitRowCount ?? 0
                            ),
                            GrossProfit = current == null
                                ? null
                                : GetCompleteGrossProfit(
                                    current.GrossProfit,
                                    current.StatisticRowCount,
                                    current.CostedRowCount,
                                    current.GrossProfitRowCount
                                ),
                            CompareGrossProfit = compare == null
                                ? null
                                : GetCompleteGrossProfit(
                                    compare.GrossProfit,
                                    compare.StatisticRowCount,
                                    compare.CostedRowCount,
                                    compare.GrossProfitRowCount
                                ),
                        };
                    })
                    .Select(row =>
                    {
                        row.GrossMarginRate = CalculateGrossMarginRate(row.SalesAmount, row.GrossProfit);
                        row.CompareGrossMarginRate = CalculateGrossMarginRate(
                            row.CompareSalesAmount,
                            row.CompareGrossProfit
                        );
                        return row;
                    })
                    .OrderByDescending(x => x.SalesAmount)
                    .ThenByDescending(x => x.CompareSalesAmount)
                    .ToList();

                if (useCache)
                {
                    var cacheOptions = new MemoryCacheEntryOptions()
                        .SetAbsoluteExpiration(DETAIL_CACHE_DURATION)
                        .SetSlidingExpiration(TimeSpan.FromMinutes(1));

                    _cache.Set(cacheKey, result, cacheOptions);
                    _logger.LogInformation(
                        "产品各分店销售数据已缓存: {CacheKey}, 过期时间: {Expiration}",
                        cacheKey,
                        DateTime.Now.Add(DETAIL_CACHE_DURATION)
                    );
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetProductSalesByAllBranchesAsync failed");
                throw;
            }
        }

        /// <summary>
        /// 获取中国供应商在各分店的销售数据
        /// </summary>
        /// <param name="dateRange">日期范围</param>
        /// <param name="supplierCodes">供应商代码列表（中国供应商代码）</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <returns>中国供应商分店销售数据列表</returns>
        public async Task<List<ChinaSupplierStoreSalesDto>> GetChinaSupplierStoreSalesAsync(
            DateRangeDto dateRange,
            List<string> supplierCodes,
            List<string>? branchCodes = null
        )
        {
            var statisticStatus = await GetProductReportStatisticStatusAsync(dateRange);
            return await GetChinaSupplierStoreSalesAsync(
                dateRange,
                supplierCodes,
                branchCodes,
                statisticStatus
            );
        }

        public async Task<List<ChinaSupplierStoreSalesDto>> GetChinaSupplierStoreSalesAsync(
            DateRangeDto dateRange,
            List<string> supplierCodes,
            List<string>? branchCodes,
            ProductReportStatisticStatusDto statisticStatus
        )
        {
            try
            {
                var targetSupplierCodes = NormalizeCodes(supplierCodes);
                if (!targetSupplierCodes.Any())
                    throw new ArgumentException("供应商代码不能为空", nameof(supplierCodes));

                ValidateDateRange(dateRange);
                if (!IsProductStatisticFresh(statisticStatus))
                    return new List<ChinaSupplierStoreSalesDto>();

                if (_useSupplierRollups)
                    return await GetChinaSupplierStoresFromRollupsAsync(dateRange, supplierCodes, branchCodes, statisticStatus);

                var cacheKey = SalesDashboardCacheKeys.ChinaSupplierStore(
                    dateRange,
                    targetSupplierCodes,
                    branchCodes,
                    statisticStatus.CacheVersion
                );

                if (
                    _cache.TryGetValue<List<ChinaSupplierStoreSalesDto>>(
                        cacheKey,
                        out var cachedResult
                    )
                    && cachedResult != null
                    && cachedResult.Count != 0
                )
                {
                    _logger.LogInformation(
                        "从缓存获取中国供应商分店销售数据: {CacheKey}",
                        cacheKey
                    );
                    return cachedResult;
                }

                var chinaProductMap = await GetChinaSupplierProductMapAsync(targetSupplierCodes);
                var targetChinaSupplierCodes = targetSupplierCodes.ToHashSet(
                    StringComparer.OrdinalIgnoreCase
                );

                var startDate = dateRange.StartDate.Date;
                var endDate = dateRange.EndDate.Date;

                // 中国供应商兼容两种统计编码：新统计直接写中国供应商，旧统计写 200 后按 POSM 商品映射。
                var currentQuery = await BuildProductReportStatisticQueryAsync(
                    startDate,
                    endDate,
                    branchCodes
                );
                currentQuery = ApplyChinaSupplierStatisticFilter(
                    currentQuery,
                    targetChinaSupplierCodes,
                    chinaProductMap,
                    limitLegacy200Products: true
                );
                var currentAggregateRows = await currentQuery
                    .GroupBy(s => new { s.ProductCode, s.BranchCode, s.SupplierCode })
                    .Select(s => new
                    {
                        ProductCode = s.ProductCode,
                        BranchCode = s.BranchCode,
                        SupplierCode = s.SupplierCode,
                        TotalAmount = SqlFunc.AggregateSum(s.TotalAmount),
                        TotalQuantity = SqlFunc.AggregateSum(s.TotalQuantity),
                        OrderCount = SqlFunc.AggregateSum(s.OrderCount),
                        GrossProfit = SqlFunc.AggregateSum(s.GrossProfit),
                        StatisticRowCount = SqlFunc.AggregateCount(s.ProductCode),
                        CostedRowCount = SqlFunc.AggregateCount(s.TotalCost),
                        GrossProfitRowCount = SqlFunc.AggregateCount(s.GrossProfit),
                    })
                    .ToListAsync();
                var currentRows = currentAggregateRows
                    .Select(row => new ProductSupplierBranchAggregateRow
                    {
                        ProductCode = row.ProductCode,
                        BranchCode = row.BranchCode,
                        SupplierCode = row.SupplierCode,
                        TotalAmount = row.TotalAmount,
                        TotalQuantity = row.TotalQuantity,
                        OrderCount = row.OrderCount,
                        GrossProfit = row.GrossProfit,
                        StatisticRowCount = row.StatisticRowCount,
                        CostedRowCount = row.CostedRowCount,
                        GrossProfitRowCount = row.GrossProfitRowCount,
                    })
                    .ToList();

                var currentData = ResolveChinaSupplierBranchRows(
                    currentRows,
                    chinaProductMap,
                    targetChinaSupplierCodes
                );
                var hasCurrentProductStatisticRows = currentRows.Any()
                    || await HasChinaProductStatisticRowsAsync(
                        startDate,
                        endDate,
                        branchCodes,
                        targetChinaSupplierCodes
                    );

                if (!hasCurrentProductStatisticRows)
                {
                    // 商品统计未命中时，下钻弹窗使用中国供应商分店统计表兜底。
                    currentData = await QueryChinaSupplierStoreAggregateRowsAsync(
                        startDate,
                        endDate,
                        branchCodes,
                        targetChinaSupplierCodes
                    );
                }

                var currentBranchCodes = currentData
                    .Select(d => d.BranchCode?.Trim())
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var storeDict = await GetStoreNameMapAsync(currentBranchCodes);
                var supplierNameMap = await GetChinaSupplierNameMapAsync(
                    currentData.Select(d => d.SupplierCode)
                );

                // 中国供应商分店下钻需要同期金额、客单数和客单价，供移动端第二行显示。
                Dictionary<string, SupplierCompareMetrics> compareDict =
                    new Dictionary<string, SupplierCompareMetrics>(
                        StringComparer.OrdinalIgnoreCase
                    );

                if (
                    dateRange.CompareStartDate.HasValue
                    && dateRange.CompareEndDate.HasValue
                    && currentBranchCodes.Any()
                )
                {
                    var compareQuery = await BuildProductReportStatisticQueryAsync(
                        dateRange.CompareStartDate.Value.Date,
                        dateRange.CompareEndDate.Value.Date,
                        currentBranchCodes.ToList()
                    );
                    compareQuery = ApplyChinaSupplierStatisticFilter(
                        compareQuery,
                        targetChinaSupplierCodes,
                        chinaProductMap,
                        limitLegacy200Products: true
                    );
                    var compareAggregateRows = await compareQuery
                        .GroupBy(s => new { s.ProductCode, s.BranchCode, s.SupplierCode })
                        .Select(s => new
                        {
                            ProductCode = s.ProductCode,
                            BranchCode = s.BranchCode,
                            SupplierCode = s.SupplierCode,
                            TotalAmount = SqlFunc.AggregateSum(s.TotalAmount),
                            OrderCount = SqlFunc.AggregateSum(s.OrderCount),
                            GrossProfit = SqlFunc.AggregateSum(s.GrossProfit),
                            StatisticRowCount = SqlFunc.AggregateCount(s.ProductCode),
                            CostedRowCount = SqlFunc.AggregateCount(s.TotalCost),
                            GrossProfitRowCount = SqlFunc.AggregateCount(s.GrossProfit),
                        })
                        .ToListAsync();
                    var compareRows = compareAggregateRows
                        .Select(row => new ProductSupplierBranchAggregateRow
                        {
                            ProductCode = row.ProductCode,
                            BranchCode = row.BranchCode,
                            SupplierCode = row.SupplierCode,
                            TotalAmount = row.TotalAmount,
                            OrderCount = row.OrderCount,
                            GrossProfit = row.GrossProfit,
                            StatisticRowCount = row.StatisticRowCount,
                            CostedRowCount = row.CostedRowCount,
                            GrossProfitRowCount = row.GrossProfitRowCount,
                        })
                        .ToList();

                    var compareDataList = ResolveChinaSupplierBranchRows(
                        compareRows,
                        chinaProductMap,
                        targetChinaSupplierCodes
                    );
                    var hasCompareProductStatisticRows = compareRows.Any()
                        || await HasChinaProductStatisticRowsAsync(
                            dateRange.CompareStartDate.Value.Date,
                            dateRange.CompareEndDate.Value.Date,
                            currentBranchCodes.ToList(),
                            targetChinaSupplierCodes
                        );

                    if (!hasCompareProductStatisticRows)
                    {
                        compareDataList = await QueryChinaSupplierStoreAggregateRowsAsync(
                            dateRange.CompareStartDate.Value.Date,
                            dateRange.CompareEndDate.Value.Date,
                            currentBranchCodes.ToList(),
                            targetChinaSupplierCodes
                        );
                    }

                    compareDict = BuildSupplierBranchCompareDict(compareDataList);
                }

                var hasCompare = dateRange.CompareStartDate.HasValue && dateRange.CompareEndDate.HasValue;
                var result = currentData
                    .Select(item =>
                    {
                        var branchCode = item.BranchCode ?? string.Empty;
                        var supplierCode = item.SupplierCode ?? string.Empty;
                        var orderCount = item.OrderCount ?? 0;
                        var compareKey = $"{branchCode}|{supplierCode}";
                        compareDict.TryGetValue(compareKey, out var compare);

                        var dto = new ChinaSupplierStoreSalesDto
                        {
                            StartDate = startDate,
                            EndDate = endDate,
                            SupplierCode = supplierCode,
                            SupplierName = supplierNameMap.TryGetValue(supplierCode, out var supplierName)
                                ? supplierName
                                : supplierCode,
                            BranchCode = branchCode,
                            BranchName = storeDict.TryGetValue(branchCode, out var branchName)
                                ? branchName
                                : branchCode,
                            TotalAmount = item.TotalAmount,
                            TotalQuantity = item.TotalQuantity,
                            OrderCount = orderCount,
                            AverageTransaction =
                                orderCount > 0 ? item.TotalAmount / orderCount : 0,
                            CostStatus = GetCostStatus(item.StatisticRowCount, item.CostedRowCount, item.GrossProfitRowCount),
                            GrossProfit = GetCompleteGrossProfit(
                                item.GrossProfit,
                                item.StatisticRowCount,
                                item.CostedRowCount,
                                item.GrossProfitRowCount
                            ),
                        };
                        dto.GrossMarginRate = CalculateGrossMarginRate(dto.TotalAmount, dto.GrossProfit);

                        if (hasCompare)
                        {
                            dto.CompareCostStatus = GetCostStatus(compare.StatisticRowCount, compare.CostedRowCount, compare.GrossProfitRowCount);
                            dto.CompareTotalAmount = compare.TotalAmount;
                            dto.CompareOrderCount = compare.OrderCount;
                            dto.CompareAverageTransaction =
                                compare.OrderCount > 0 ? compare.TotalAmount / compare.OrderCount : 0;
                            dto.TotalAmountGrowth = CalculateGrowth(
                                item.TotalAmount,
                                compare.TotalAmount
                            );
                            dto.CompareGrossProfit = GetCompleteGrossProfit(
                                compare.GrossProfit,
                                compare.StatisticRowCount,
                                compare.CostedRowCount,
                                compare.GrossProfitRowCount
                            );
                            dto.CompareGrossMarginRate = CalculateGrossMarginRate(
                                compare.TotalAmount,
                                dto.CompareGrossProfit
                            );
                        }

                        return dto;
                    })
                    .OrderByDescending(r => r.TotalAmount)
                    .ToList();

                var cacheOptions = new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(RANKING_CACHE_DURATION)
                    .SetSlidingExpiration(TimeSpan.FromMinutes(5));

                _cache.Set(cacheKey, result, cacheOptions);
                _logger.LogInformation(
                    "中国供应商分店销售数据已缓存: {CacheKey}, 过期时间: {Expiration}",
                    cacheKey,
                    DateTime.Now.Add(RANKING_CACHE_DURATION)
                );

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetChinaSupplierStoreSalesAsync failed");
                throw;
            }
        }

        /// <summary>
        /// 获取澳洲供应商分店销售明细
        /// </summary>
        /// <param name="dateRange">日期范围</param>
        /// <param name="branchCodes">分店代码列表</param>
        /// <param name="supplierCodes">供应商代码列表</param>
        /// <returns>澳洲供应商分店销售明细列表</returns>
        public async Task<
            List<AustralianSupplierStoreSalesDetailDto>
        > GetAustralianSupplierStoreSalesDetailsAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes = null,
            List<string>? supplierCodes = null
        )
        {
            try
            {
                ValidateDateRange(dateRange);

                // 明细数据不缓存或使用短时间缓存，考虑到数据量可能较大，这里暂不缓存或仅短时缓存
                // 如果需要缓存，需要设计合适的 CacheKey

                if (!ValidateDatabaseConnection<AustralianSupplierStoreSalesDetail>())
                {
                    throw new InvalidOperationException("澳洲供应商分店销售明细表不可用");
                }

                var startDate = dateRange.StartDate.Date;
                var endDate = dateRange.EndDate.Date;

                var query = _context
                    .Db.Queryable<AustralianSupplierStoreSalesDetail>()
                    .Where(s => s.Date >= startDate && s.Date <= endDate);

                if (branchCodes != null && branchCodes.Any())
                {
                    query = query.Where(s => branchCodes.Contains(s.BranchCode));
                }

                if (supplierCodes != null && supplierCodes.Any())
                {
                    query = query.Where(s => supplierCodes.Contains(s.SupplierCode));
                }

                var result = await query
                    .OrderByDescending(s => s.TotalAmount)
                    .Select(s => new AustralianSupplierStoreSalesDetailDto
                    {
                        Date = s.Date,
                        BranchCode = s.BranchCode,
                        SupplierCode = s.SupplierCode,
                        SupplierName = s.SupplierName,
                        TotalAmount = s.TotalAmount,
                        TotalQuantity = s.TotalQuantity,
                        OrderCount = s.OrderCount,
                        UpdateTime = s.UpdateTime,
                    })
                    .ToListAsync();

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetAustralianSupplierStoreSalesDetailsAsync failed");
                throw;
            }
        }

        /// <summary>
        /// 获取中国供应商分店销售明细
        /// </summary>
        /// <param name="dateRange">日期范围</param>
        /// <param name="branchCodes">分店代码列表</param>
        /// <param name="supplierCodes">供应商代码列表</param>
        /// <returns>中国供应商分店销售明细列表</returns>
        public async Task<
            List<ChinaSupplierStoreSalesDetailDto>
        > GetChinaSupplierStoreSalesDetailsAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes = null,
            List<string>? supplierCodes = null
        )
        {
            try
            {
                ValidateDateRange(dateRange);

                if (!ValidateDatabaseConnection<ChinaSupplierStoreSalesDetail>())
                {
                    throw new InvalidOperationException("中国供应商分店销售明细表不可用");
                }

                var startDate = dateRange.StartDate.Date;
                var endDate = dateRange.EndDate.Date;

                var query = _context
                    .Db.Queryable<ChinaSupplierStoreSalesDetail>()
                    .Where(s => s.Date >= startDate && s.Date <= endDate);

                if (branchCodes != null && branchCodes.Any())
                {
                    query = query.Where(s => branchCodes.Contains(s.BranchCode));
                }

                if (supplierCodes != null && supplierCodes.Any())
                {
                    query = query.Where(s => supplierCodes.Contains(s.SupplierCode));
                }

                var result = await query
                    .OrderByDescending(s => s.TotalAmount)
                    .Select(s => new ChinaSupplierStoreSalesDetailDto
                    {
                        Date = s.Date,
                        BranchCode = s.BranchCode,
                        SupplierCode = s.SupplierCode,
                        SupplierName = s.SupplierName,
                        TotalAmount = s.TotalAmount,
                        TotalQuantity = s.TotalQuantity,
                        OrderCount = s.OrderCount,
                        UpdateTime = s.UpdateTime,
                    })
                    .ToListAsync();

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetChinaSupplierStoreSalesDetailsAsync failed");
                throw;
            }
        }

        /// <summary>
        /// 获取分店销售聚合数据
        /// 用于 SalesDetailAnalysisV2 门店分布卡，直接返回分店级别的聚合数据
        /// 避免前端接收大量明细数据后再进行聚合
        /// </summary>
        /// <param name="dateRange">日期范围（当前期）</param>
        /// <param name="compareDateRange">日期范围（去年同期，可选）</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <param name="supplierCodes">供应商代码列表（可选，用于过滤）</param>
        /// <returns>分店销售聚合数据列表</returns>
        public async Task<List<BranchSalesAggregateDto>> GetBranchSalesAggregateAsync(
            DateRangeDto dateRange,
            DateRangeDto? compareDateRange = null,
            List<string>? branchCodes = null,
            List<string>? supplierCodes = null
        )
        {
            try
            {
                ValidateDateRange(dateRange);

                if (!ValidateDatabaseConnection<AustralianSupplierStoreSalesDetail>())
                {
                    return new List<BranchSalesAggregateDto>();
                }

                var startDate = dateRange.StartDate.Date;
                var endDate = dateRange.EndDate.Date;

                var query = _context
                    .Db.Queryable<AustralianSupplierStoreSalesDetail>()
                    .Where(s => s.Date >= startDate && s.Date <= endDate);

                if (branchCodes != null && branchCodes.Any())
                {
                    query = query.Where(s => branchCodes.Contains(s.BranchCode));
                }

                if (supplierCodes != null && supplierCodes.Any())
                {
                    query = query.Where(s => supplierCodes.Contains(s.SupplierCode));
                }

                var currentData = await query
                    .GroupBy(s => s.BranchCode)
                    .Select(s => new
                    {
                        BranchCode = s.BranchCode,
                        TotalRevenue = SqlFunc.AggregateSum(s.TotalAmount),
                        TotalQuantity = SqlFunc.AggregateSum(s.TotalQuantity),
                        OrderCount = SqlFunc.AggregateSum(s.OrderCount),
                    })
                    .ToListAsync();

                var hbRevenueQuery = _context
                    .Db.Queryable<AustralianSupplierStoreSalesDetail>()
                    .Where(s => s.Date >= startDate && s.Date <= endDate);

                if (branchCodes != null && branchCodes.Any())
                {
                    hbRevenueQuery = hbRevenueQuery.Where(s => branchCodes.Contains(s.BranchCode));
                }

                hbRevenueQuery = hbRevenueQuery.Where(s =>
                    s.SupplierCode == "#200" || s.SupplierCode == "200"
                );

                var hbRevenueData = await hbRevenueQuery
                    .GroupBy(s => s.BranchCode)
                    .Select(s => new
                    {
                        BranchCode = s.BranchCode,
                        HbRevenue = SqlFunc.AggregateSum(s.TotalAmount),
                    })
                    .ToListAsync();

                var hbRevenueDict = hbRevenueData.ToDictionary(x => x.BranchCode, x => x.HbRevenue);

                var branchCodesList = currentData.Select(s => s.BranchCode).ToList();

                var branchNameQuery = _context
                    .Db.Queryable<StoreSalesStatistic>()
                    .Where(s => branchCodesList.Contains(s.BranchCode))
                    .Select(s => new { s.BranchCode, s.BranchName })
                    .Distinct();

                var branchNames = await branchNameQuery.ToListAsync();
                var branchNameDict = branchNames.ToDictionary(x => x.BranchCode, x => x.BranchName);

                var result = currentData
                    .Select(s => new BranchSalesAggregateDto
                    {
                        BranchCode = s.BranchCode,
                        BranchName = branchNameDict.GetValueOrDefault(s.BranchCode, s.BranchCode),
                        TotalRevenue = s.TotalRevenue,
                        TotalRevenueLY = 0,
                        TotalQuantity = s.TotalQuantity,
                        TotalQuantityLY = 0,
                        OrderCount = (int)(s.OrderCount ?? 0),
                        OrderCountLY = 0,
                        HbRevenue = hbRevenueDict.GetValueOrDefault(s.BranchCode, 0),
                        HbRevenueLY = 0,
                    })
                    .ToList();

                if (
                    compareDateRange != null
                    && compareDateRange.CompareStartDate.HasValue
                    && compareDateRange.CompareEndDate.HasValue
                )
                {
                    var compareStartDate = compareDateRange.CompareStartDate.Value.Date;
                    var compareEndDate = compareDateRange.CompareEndDate.Value.Date;

                    var compareQuery = _context
                        .Db.Queryable<AustralianSupplierStoreSalesDetail>()
                        .Where(s => s.Date >= compareStartDate && s.Date <= compareEndDate);

                    if (branchCodes != null && branchCodes.Any())
                    {
                        compareQuery = compareQuery.Where(s => branchCodes.Contains(s.BranchCode));
                    }

                    if (supplierCodes != null && supplierCodes.Any())
                    {
                        compareQuery = compareQuery.Where(s =>
                            supplierCodes.Contains(s.SupplierCode)
                        );
                    }

                    var compareData = await compareQuery
                        .GroupBy(s => s.BranchCode)
                        .Select(s => new
                        {
                            BranchCode = s.BranchCode,
                            TotalRevenue = SqlFunc.AggregateSum(s.TotalAmount),
                            TotalQuantity = SqlFunc.AggregateSum(s.TotalQuantity),
                            OrderCount = SqlFunc.AggregateSum(s.OrderCount),
                        })
                        .ToListAsync();

                    var compareHbRevenueQuery = _context
                        .Db.Queryable<AustralianSupplierStoreSalesDetail>()
                        .Where(s => s.Date >= compareStartDate && s.Date <= compareEndDate);

                    if (branchCodes != null && branchCodes.Any())
                    {
                        compareHbRevenueQuery = compareHbRevenueQuery.Where(s =>
                            branchCodes.Contains(s.BranchCode)
                        );
                    }

                    compareHbRevenueQuery = compareHbRevenueQuery.Where(s =>
                        s.SupplierCode == "#200" || s.SupplierCode == "200"
                    );

                    var compareHbRevenueData = await compareHbRevenueQuery
                        .GroupBy(s => s.BranchCode)
                        .Select(s => new
                        {
                            BranchCode = s.BranchCode,
                            HbRevenue = SqlFunc.AggregateSum(s.TotalAmount),
                        })
                        .ToListAsync();

                    var compareHbRevenueDict = compareHbRevenueData.ToDictionary(
                        x => x.BranchCode,
                        x => x.HbRevenue
                    );
                    var compareDataDict = compareData.ToDictionary(x => x.BranchCode);

                    foreach (var item in result)
                    {
                        if (compareDataDict.TryGetValue(item.BranchCode, out var compareItem))
                        {
                            item.TotalRevenueLY = compareItem.TotalRevenue;
                            item.TotalQuantityLY = compareItem.TotalQuantity;
                            item.OrderCountLY = (int)(compareItem.OrderCount ?? 0);
                            item.HbRevenueLY = compareHbRevenueDict.GetValueOrDefault(
                                item.BranchCode,
                                0
                            );
                        }
                    }
                }

                return result.OrderByDescending(r => r.TotalRevenue).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetBranchSalesAggregateAsync failed");
                return new List<BranchSalesAggregateDto>();
            }
        }

        /// <summary>
        /// 获取 Executive Dashboard 分店业绩排名
        /// 用于 Executive Sales Intelligence 页面
        /// 查询分店的销售总额，按销售额排序并计算 YoY
        /// </summary>
        /// <param name="dateRange">日期范围</param>
        /// <param name="topN">返回前N条记录</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <returns>分店业绩排名及统计完整性状态</returns>
        public async Task<ExecutiveBranchPerformanceResultDto> GetExecutiveBranchPerformanceAsync(
            DateRangeDto dateRange,
            int? topN = null,
            List<string>? branchCodes = null
        )
        {
            try
            {
                ValidateDateRange(dateRange);
                var normalizedBranchCodes = NormalizeBranchCodes(branchCodes);
                if (branchCodes != null && normalizedBranchCodes.Count == 0)
                    return new ExecutiveBranchPerformanceResultDto();

                // 管理员的“全部分店”以启用门店目录为展示权威集合；统计表只负责提供有销售的数值。
                var activeStoreNameMap = normalizedBranchCodes.Count == 0
                    ? await GetActiveStoreNameMapAsync()
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var displayBranchCodes = normalizedBranchCodes.Count > 0
                    ? normalizedBranchCodes.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : activeStoreNameMap.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

                var statisticsRefreshState = await EnsureStoreSalesStatisticsAsync(
                    dateRange,
                    normalizedBranchCodes
                );

                return await ReadReportSnapshotAsync(async () =>
                {

                    var compareStartStr = dateRange.CompareStartDate?.ToString("yyyyMMdd") ?? "null";
                    var compareEndStr = dateRange.CompareEndDate?.ToString("yyyyMMdd") ?? "null";
                    var statisticsVersion = await GetStatisticsCacheVersionAsync(dateRange);
                    var cacheKey =
                        $"ExecutiveBranchPerformance_{statisticsVersion}_{dateRange.StartDate:yyyyMMdd}_{dateRange.EndDate:yyyyMMdd}_{compareStartStr}_{compareEndStr}_{topN?.ToString() ?? "all"}_{string.Join(",", displayBranchCodes.OrderBy(code => code, StringComparer.OrdinalIgnoreCase))}";

                    if (
                        statisticsRefreshState == StatisticsRefreshState.NotNeeded
                        &&
                        _cache.TryGetValue<ExecutiveBranchPerformanceResultDto>(
                            cacheKey,
                            out var cachedResult
                        )
                        && cachedResult != null
                    )
                    {
                        _logger.LogInformation("从缓存获取 Executive 分店业绩: {CacheKey}", cacheKey);
                        return cachedResult;
                    }

                    var startDate = dateRange.StartDate.Date;
                    var endDate = dateRange.EndDate.Date;

                    // 使用 StoreSalesStatistic 表查询分店销售数据
                    var branchCurrentQuery = _context
                        .Db.Queryable<StoreSalesStatistic>()
                        .Where(s => s.Date >= startDate && s.Date <= endDate);

                    if (displayBranchCodes.Count > 0)
                    {
                        branchCurrentQuery = branchCurrentQuery.Where(s =>
                            displayBranchCodes.Contains(s.BranchCode)
                        );
                    }
                    else
                    {
                        branchCurrentQuery = branchCurrentQuery.Where(s => false);
                    }

                    var currentData = await branchCurrentQuery
                        .GroupBy(s => s.BranchCode)
                        .Select(s => new
                        {
                            BranchCode = s.BranchCode,
                            BranchName = SqlFunc.AggregateMax(s.BranchName),
                            Revenue = SqlFunc.AggregateSum(s.TotalAmount),
                            OrderCount = SqlFunc.AggregateSum(s.OrderCount),
                        })
                        .ToListAsync();

                    var lyDict = new Dictionary<string, (decimal RevenueLY, int OrderCountLY)>(
                        StringComparer.OrdinalIgnoreCase
                    );
                    if (dateRange.CompareStartDate.HasValue && dateRange.CompareEndDate.HasValue)
                    {
                        var lyStartDate = dateRange.CompareStartDate.Value.Date;
                        var lyEndDate = dateRange.CompareEndDate.Value.Date;

                        var lyQuery = _context
                            .Db.Queryable<StoreSalesStatistic>()
                            .Where(s => s.Date >= lyStartDate && s.Date <= lyEndDate);

                        if (displayBranchCodes.Count > 0)
                        {
                            lyQuery = lyQuery.Where(s => displayBranchCodes.Contains(s.BranchCode));
                        }
                        else
                        {
                            lyQuery = lyQuery.Where(s => false);
                        }

                        var lyData = await lyQuery
                            .GroupBy(s => s.BranchCode)
                            .Select(s => new
                            {
                                BranchCode = s.BranchCode,
                                RevenueLY = SqlFunc.AggregateSum(s.TotalAmount),
                                OrderCountLY = SqlFunc.AggregateSum(s.OrderCount),
                            })
                            .ToListAsync();

                        lyDict = lyData.ToDictionary(
                            s => s.BranchCode,
                            s => (s.RevenueLY, s.OrderCountLY),
                            StringComparer.OrdinalIgnoreCase
                        );
                    }

                    // 构建结果并排序
                    IEnumerable<ExecutiveBranchPerformanceDto> result = currentData
                        .Select(
                            (item, index) =>
                                new ExecutiveBranchPerformanceDto
                                {
                                    Rank = index + 1,
                                    BranchCode = item.BranchCode,
                                    BranchName = string.IsNullOrWhiteSpace(item.BranchName)
                                        ? item.BranchCode
                                        : item.BranchName,
                                    Revenue = item.Revenue,
                                    RevenueLY = lyDict.TryGetValue(item.BranchCode, out var lyItem)
                                        ? lyItem.RevenueLY
                                        : 0,
                                    OrderCount = item.OrderCount,
                                    OrderCountLY = lyDict.TryGetValue(item.BranchCode, out var lyItem2)
                                        ? lyItem2.OrderCountLY
                                        : 0,
                                    // AOV 在内存侧计算，避免统计表出现 0 单数时触发数据库除零。
                                    Aov = item.OrderCount > 0 ? item.Revenue / item.OrderCount : 0,
                                    AovLY = lyDict.TryGetValue(item.BranchCode, out var lyItem3)
                                        && lyItem3.OrderCountLY > 0
                                            ? lyItem3.RevenueLY / lyItem3.OrderCountLY
                                            : 0,
                                }
                        );

                    if (displayBranchCodes.Count > 0)
                    {
                        var snapshotBranchCodes = currentData
                            .Where(item => !string.IsNullOrWhiteSpace(item.BranchCode))
                            .Select(item => item.BranchCode)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var zeroSalesBranchCodes = displayBranchCodes
                            .Where(code => !snapshotBranchCodes.Contains(code))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

                        if (zeroSalesBranchCodes.Count > 0)
                        {
                            // 授权范围是普通用户可见分店的权威集合；统计表不会为零销售分店逐日落行时，仍须保留零值排行行。
                            var branchNameMap = normalizedBranchCodes.Count == 0
                                ? activeStoreNameMap
                                : await GetStoreNameMapAsync(zeroSalesBranchCodes);
                            result = result.Concat(zeroSalesBranchCodes.Select(code =>
                                new ExecutiveBranchPerformanceDto
                                {
                                    BranchCode = code,
                                    BranchName = branchNameMap.TryGetValue(code, out var branchName)
                                        && !string.IsNullOrWhiteSpace(branchName)
                                            ? branchName
                                            : code,
                                    Revenue = 0,
                                    RevenueLY = lyDict.TryGetValue(code, out var lyItem)
                                        ? lyItem.RevenueLY
                                        : 0,
                                    OrderCount = 0,
                                    OrderCountLY = lyDict.TryGetValue(code, out var lyItem2)
                                        ? lyItem2.OrderCountLY
                                        : 0,
                                    Aov = 0,
                                    AovLY = lyDict.TryGetValue(code, out var lyItem3)
                                        && lyItem3.OrderCountLY > 0
                                            ? lyItem3.RevenueLY / lyItem3.OrderCountLY
                                            : 0,
                                }
                            ));
                        }
                    }

                    result = result
                        .OrderByDescending(x => x.Revenue)
                        .ThenBy(x => x.BranchCode, StringComparer.OrdinalIgnoreCase);

                    if (topN.HasValue && topN.Value > 0)
                    {
                        result = result.Take(topN.Value);
                    }

                    var rankedResult = result
                        .Select(
                            (item, index) =>
                                new ExecutiveBranchPerformanceDto
                                {
                                    Rank = index + 1,
                                    BranchCode = item.BranchCode,
                                    BranchName = item.BranchName,
                                    Revenue = item.Revenue,
                                    RevenueLY = item.RevenueLY,
                                    OrderCount = item.OrderCount,
                                    OrderCountLY = item.OrderCountLY,
                                    Aov = item.Aov,
                                    AovLY = item.AovLY,
                                }
                        )
                        .ToList();

                    var expectedBranchCount = displayBranchCodes.Count;
                    if (topN.HasValue && topN.Value > 0)
                    {
                        expectedBranchCount = Math.Min(expectedBranchCount, topN.Value);
                    }
                    var hasSourceBranchIdentityGap = false;
                    var sourceBranchIdentityUnverified = false;
                    if (statisticsRefreshState != StatisticsRefreshState.NotNeeded)
                    {
                        var sourceBranchCodes = await GetExpectedExecutiveBranchCodesAsync(
                            startDate,
                            endDate,
                            normalizedBranchCodes
                        );
                        if (sourceBranchCodes != null)
                        {
                            // 不能只比较行数：授权分店的零值补行会让“少写真实销售分店”伪装成完整。
                            // 必须按实际聚合来源的分店身份核验统计快照，并同时识别缺失和陈旧分店行。
                            var statisticSnapshotBranchCodes = currentData
                                .Where(item => !string.IsNullOrWhiteSpace(item.BranchCode))
                                .Select(item => item.BranchCode)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);
                            var displayedSourceBranchCodes = sourceBranchCodes
                                .Where(displayBranchCodes.Contains)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);
                            hasSourceBranchIdentityGap = !displayedSourceBranchCodes.SetEquals(
                                statisticSnapshotBranchCodes
                            );
                        }
                        else
                        {
                            // 快速补算后的 POSM 来源身份无法核验时，不能证明统计快照完整；必须 fail closed 且禁止缓存。
                            sourceBranchIdentityUnverified = true;
                        }
                    }

                    var response = new ExecutiveBranchPerformanceResultDto
                    {
                        Items = rankedResult,
                        StatisticsPending = statisticsRefreshState == StatisticsRefreshState.Pending
                            || rankedResult.Count < expectedBranchCount
                            || hasSourceBranchIdentityGap
                            || sourceBranchIdentityUnverified,
                        StatisticsExpectedBranchCount = expectedBranchCount,
                        StatisticsSnapshotBranchCount = rankedResult.Count,
                    };

                    // 缓存结果
                    var cacheOptions = new MemoryCacheEntryOptions()
                        .SetAbsoluteExpiration(RANKING_CACHE_DURATION)
                        .SetSlidingExpiration(TimeSpan.FromMinutes(5));

                    if (!response.StatisticsPending)
                    {
                        _cache.Set(cacheKey, response, cacheOptions);
                    }

                    return response;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetExecutiveBranchPerformanceAsync failed");
                throw;
            }
        }

        /// <summary>
        /// 获取 Executive Dashboard 每小时流量密度
        /// 用于 Executive Sales Intelligence 页面的每小时流量展示
        /// </summary>
        /// <param name="dateRange">日期范围</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <returns>每小时流量密度及统计完整性状态</returns>
        public async Task<ExecutiveReportResultDto<ExecutiveHourlyTrafficDto>> GetExecutiveHourlyTrafficAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes = null
        )
        {
            try
            {
                ValidateDateRange(dateRange);
                var normalizedBranchCodes = NormalizeBranchCodes(branchCodes);
                if (branchCodes != null && normalizedBranchCodes.Count == 0)
                    return new ExecutiveReportResultDto<ExecutiveHourlyTrafficDto>();

                var statisticsRefreshState = await EnsureHourlySalesStatisticsAsync(
                    dateRange,
                    normalizedBranchCodes
                );

                return await ReadReportSnapshotAsync(async () =>
                {
                    // 快速补算任务即使已结束，也必须重查来源/统计覆盖；不能把“完成”当作“完整”。
                    // 稳定统计已在 Ensure 中完成一次覆盖核验，此处不重复扫描 POSM，允许尽早命中缓存。
                    var hourlyStatisticsStillMissing = new List<DateTime>();
                    if (statisticsRefreshState == StatisticsRefreshState.Completed)
                    {
                        hourlyStatisticsStillMissing = await GetMissingHourlyStatisticDatesAsync(
                            dateRange.StartDate.Date,
                            dateRange.EndDate.Date,
                            normalizedBranchCodes
                        );
                        if (dateRange.CompareStartDate.HasValue && dateRange.CompareEndDate.HasValue)
                        {
                            hourlyStatisticsStillMissing.AddRange(await GetMissingHourlyStatisticDatesAsync(
                                dateRange.CompareStartDate.Value.Date,
                                dateRange.CompareEndDate.Value.Date,
                                normalizedBranchCodes
                            ));
                        }
                    }
                    var statisticsPending = statisticsRefreshState == StatisticsRefreshState.Pending
                        || hourlyStatisticsStillMissing.Count > 0;

                    var compareStartStr = dateRange.CompareStartDate?.ToString("yyyyMMdd") ?? "null";
                    var compareEndStr = dateRange.CompareEndDate?.ToString("yyyyMMdd") ?? "null";
                    var statisticsVersion = await GetStatisticsCacheVersionAsync(dateRange);
                    var cacheKey =
                        $"ExecutiveHourlyTraffic_{statisticsVersion}_{dateRange.StartDate:yyyyMMdd}_{dateRange.EndDate:yyyyMMdd}_{compareStartStr}_{compareEndStr}_{string.Join(",", normalizedBranchCodes)}";

                    if (
                        statisticsRefreshState == StatisticsRefreshState.NotNeeded
                        &&
                        _cache.TryGetValue<ExecutiveReportResultDto<ExecutiveHourlyTrafficDto>>(
                            cacheKey,
                            out var cachedResult
                        )
                        && cachedResult != null
                        && !cachedResult.StatisticsPending
                    )
                    {
                        _logger.LogInformation("从缓存获取 Executive 每小时流量: {CacheKey}", cacheKey);
                        return cachedResult;
                    }

                    var startDate = dateRange.StartDate.Date;
                    var endDate = dateRange.EndDate.Date;
                    // 管理员的「全部分店」与分店排行同口径：以启用门店目录为准，已停用门店去年的分时不进同期。
                    // 目录为空（未初始化的库）时保持原行为，不按门店收窄；完整性核验仍用原请求范围。
                    var queryBranchCodes = normalizedBranchCodes.Count > 0
                        ? normalizedBranchCodes
                        : (await GetActiveStoreNameMapAsync()).Keys.ToList();

                    var query = _context
                        .Db.Queryable<HourlySalesStatistic>()
                        .Where(s =>
                            s.Date >= startDate
                            && s.Date <= endDate
                            && s.BranchCode != null
                            && s.BranchCode != "ALL"
                        );

                    if (queryBranchCodes.Count > 0)
                    {
                        query = query.Where(s =>
                            s.BranchCode != null && queryBranchCodes.Contains(s.BranchCode)
                        );
                    }

                    var hourlyData = (await query
                        .GroupBy(s => new { s.BranchCode, s.Hour })
                        .Select(s => new
                        {
                            BranchCode = s.BranchCode,
                            BranchName = SqlFunc.AggregateMax(s.BranchName),
                            Hour = s.Hour,
                            Revenue = SqlFunc.AggregateSum(s.TotalAmount),
                            OrderCount = SqlFunc.AggregateSum(s.OrderCount ?? 0),
                        })
                        .OrderBy(s => s.BranchCode)
                        .ToListAsync())
                        .OrderBy(x => x.BranchCode)
                        .ThenBy(x => x.Hour)
                        .ToList();

                    var lyDict = new Dictionary<
                        (string BranchCode, int Hour),
                        (string BranchCode, string BranchName, decimal Revenue, int OrderCount)
                    >();
                    if (dateRange.CompareStartDate.HasValue && dateRange.CompareEndDate.HasValue)
                    {
                        var lyStartDate = dateRange.CompareStartDate.Value.Date;
                        var lyEndDate = dateRange.CompareEndDate.Value.Date;

                        var lyQuery = _context
                            .Db.Queryable<HourlySalesStatistic>()
                            .Where(s =>
                                s.Date >= lyStartDate
                                && s.Date <= lyEndDate
                                && s.BranchCode != null
                                && s.BranchCode != "ALL"
                            );

                        if (queryBranchCodes.Count > 0)
                        {
                            lyQuery = lyQuery.Where(s =>
                                s.BranchCode != null && queryBranchCodes.Contains(s.BranchCode)
                            );
                        }

                        var lyHourlyData = await lyQuery
                            .GroupBy(s => new { s.BranchCode, s.Hour })
                            .Select(s => new
                            {
                                BranchCode = s.BranchCode,
                                BranchName = SqlFunc.AggregateMax(s.BranchName),
                                Hour = s.Hour,
                                RevenueLY = SqlFunc.AggregateSum(s.TotalAmount),
                                OrderCountLY = SqlFunc.AggregateSum(s.OrderCount ?? 0),
                            })
                            .ToListAsync();

                        lyDict = lyHourlyData
                            .Where(row => !string.IsNullOrWhiteSpace(row.BranchCode))
                            .GroupBy(row => (
                                BranchCode: row.BranchCode!.Trim().ToUpperInvariant(),
                                Hour: row.Hour
                            ))
                            .ToDictionary(
                                group => group.Key,
                                group =>
                                {
                                    var branchCode = group.First().BranchCode!.Trim();
                                    var branchName = group
                                        .Select(row => row.BranchName)
                                        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                                        ?? branchCode;
                                    return (
                                        BranchCode: branchCode,
                                        BranchName: branchName,
                                        Revenue: group.Sum(row => row.RevenueLY),
                                        OrderCount: group.Sum(row => row.OrderCountLY)
                                    );
                                }
                            );
                    }

                    var currentDict = hourlyData
                        .Where(row => !string.IsNullOrWhiteSpace(row.BranchCode))
                        .GroupBy(row => (
                            BranchCode: row.BranchCode!.Trim().ToUpperInvariant(),
                            Hour: row.Hour
                        ))
                        .ToDictionary(
                            group => group.Key,
                            group =>
                            {
                                var branchCode = group.First().BranchCode!.Trim();
                                var branchName = group
                                    .Select(row => row.BranchName)
                                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                                    ?? branchCode;
                                return (
                                    BranchCode: branchCode,
                                    BranchName: branchName,
                                    Revenue: group.Sum(row => row.Revenue),
                                    OrderCount: group.Sum(row => row.OrderCount)
                                );
                            }
                        );
                    var unionKeys = currentDict.Keys
                        .Union(lyDict.Keys)
                        .OrderBy(key => key.BranchCode, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(key => key.Hour)
                        .ToList();
                    var items = unionKeys
                        .GroupBy(key => key.BranchCode, StringComparer.OrdinalIgnoreCase)
                        .Select(branchGroup =>
                        {
                            var currentRepresentative = branchGroup
                                .Where(currentDict.ContainsKey)
                                .Select(key => currentDict[key])
                                .FirstOrDefault();
                            var compareRepresentative = branchGroup
                                .Where(lyDict.ContainsKey)
                                .Select(key => lyDict[key])
                                .FirstOrDefault();
                            var branchCode = !string.IsNullOrWhiteSpace(currentRepresentative.BranchCode)
                                ? currentRepresentative.BranchCode
                                : compareRepresentative.BranchCode;
                            var branchName = !string.IsNullOrWhiteSpace(currentRepresentative.BranchName)
                                ? currentRepresentative.BranchName
                                : !string.IsNullOrWhiteSpace(compareRepresentative.BranchName)
                                    ? compareRepresentative.BranchName
                                    : branchCode;
                            var branchMaxRevenue = branchGroup.Max(key =>
                                currentDict.TryGetValue(key, out var current) ? current.Revenue : 0m
                            );
                            var peakThreshold = branchMaxRevenue * 0.8m;

                            return branchGroup.Select(key =>
                            {
                                var current = currentDict.TryGetValue(key, out var currentValue)
                                    ? currentValue
                                    : (BranchCode: branchCode, BranchName: branchName, Revenue: 0m, OrderCount: 0);
                                var compare = lyDict.TryGetValue(key, out var compareValue)
                                    ? compareValue
                                    : (BranchCode: branchCode, BranchName: branchName, Revenue: 0m, OrderCount: 0);
                                return new ExecutiveHourlyTrafficDto
                                {
                                    Hour = $"{key.Hour:D2}:00",
                                    BranchCode = branchCode,
                                    BranchName = branchName,
                                    Revenue = current.Revenue,
                                    RevenueLY = compare.Revenue,
                                    OrderCount = current.OrderCount,
                                    OrderCountLY = compare.OrderCount,
                                    Percentage = branchMaxRevenue > 0
                                        ? (int)(current.Revenue * 100 / branchMaxRevenue)
                                        : 0,
                                    IsPeak = branchMaxRevenue > 0
                                        && current.Revenue >= peakThreshold,
                                };
                            });
                        })
                        .SelectMany(x => x)
                        .ToList();

                    // 缓存结果
                    var cacheOptions = new MemoryCacheEntryOptions()
                        .SetAbsoluteExpiration(RANKING_CACHE_DURATION)
                        .SetSlidingExpiration(TimeSpan.FromMinutes(5));

                    var response = CreateExecutiveReportResult(
                        items,
                        statisticsPending
                    );

                    if (!response.StatisticsPending)
                    {
                        _cache.Set(cacheKey, response, cacheOptions);
                    }

                    return response;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetExecutiveHourlyTrafficAsync failed");
                throw;
            }
        }

        /// <summary>
        /// 获取分店每日营业额
        /// </summary>
        /// <param name="dateRange">日期范围</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <returns>分店每日营业额及统计完整性状态</returns>
        public async Task<ExecutiveReportResultDto<BranchDailyPerformanceDto>> GetBranchDailyPerformanceAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes = null
        )
        {
            try
            {
                ValidateDateRange(dateRange);
                var normalizedBranchCodes = NormalizeBranchCodes(branchCodes);
                if (branchCodes != null && normalizedBranchCodes.Count == 0)
                    return new ExecutiveReportResultDto<BranchDailyPerformanceDto>();

                var statisticsRefreshState = await EnsureStoreSalesStatisticsAsync(
                    dateRange,
                    normalizedBranchCodes
                );

                return await ReadReportSnapshotAsync(async () =>
                {
                    // 与排行相同，分店日表须按 POSM 销售分店身份复核，避免补零行掩盖漏写统计。
                    // 稳定统计已在 Ensure 中完成一次覆盖核验，此处不重复扫描 POSM，允许尽早命中缓存。
                    var storeStatisticsStillMissing = new List<DateTime>();
                    if (statisticsRefreshState == StatisticsRefreshState.Completed)
                    {
                        storeStatisticsStillMissing = await GetMissingStoreStatisticDatesAsync(
                            dateRange.StartDate.Date,
                            dateRange.EndDate.Date,
                            normalizedBranchCodes
                        );
                        if (dateRange.CompareStartDate.HasValue && dateRange.CompareEndDate.HasValue)
                        {
                            storeStatisticsStillMissing.AddRange(await GetMissingStoreStatisticDatesAsync(
                                dateRange.CompareStartDate.Value.Date,
                                dateRange.CompareEndDate.Value.Date,
                                normalizedBranchCodes
                            ));
                        }
                    }
                    var statisticsPending = statisticsRefreshState == StatisticsRefreshState.Pending
                        || storeStatisticsStillMissing.Count > 0;

                    var compareStartStr = dateRange.CompareStartDate?.ToString("yyyyMMdd") ?? "null";
                    var compareEndStr = dateRange.CompareEndDate?.ToString("yyyyMMdd") ?? "null";
                    var statisticsVersion = await GetStatisticsCacheVersionAsync(dateRange);
                    var cacheKey =
                        $"BranchDailyPerformance_{statisticsVersion}_{dateRange.StartDate:yyyyMMdd}_{dateRange.EndDate:yyyyMMdd}_{compareStartStr}_{compareEndStr}_{string.Join(",", normalizedBranchCodes)}";

                    if (
                        statisticsRefreshState == StatisticsRefreshState.NotNeeded
                        &&
                        _cache.TryGetValue<ExecutiveReportResultDto<BranchDailyPerformanceDto>>(
                            cacheKey,
                            out var cachedResult
                        )
                        && cachedResult != null
                        && !cachedResult.StatisticsPending
                    )
                    {
                        _logger.LogInformation("从缓存获取分店每日营业额: {CacheKey}", cacheKey);
                        return cachedResult;
                    }

                    var startDate = dateRange.StartDate.Date;
                    var endDate = dateRange.EndDate.Date;

                    var currentQuery = _context
                        .Db.Queryable<StoreSalesStatistic>()
                        .Where(s => s.Date >= startDate && s.Date <= endDate);

                    if (normalizedBranchCodes.Count > 0)
                    {
                        currentQuery = currentQuery.Where(s =>
                            normalizedBranchCodes.Contains(s.BranchCode)
                        );
                    }

                    var currentData = await currentQuery
                        .GroupBy(s => new { s.Date, s.BranchCode, s.BranchName })
                        .Select(s => new
                        {
                            Date = s.Date,
                            BranchCode = s.BranchCode,
                            BranchName = s.BranchName,
                            Revenue = SqlFunc.AggregateSum(s.TotalAmount),
                            OrderCount = SqlFunc.AggregateSum(s.OrderCount),
                        })
                        .ToListAsync();

                    var lyDict = new Dictionary<
                        (string BranchCode, DateTime Date),
                        (string BranchCode, string BranchName, decimal Revenue, int OrderCount)
                    >();
                    if (dateRange.CompareStartDate.HasValue && dateRange.CompareEndDate.HasValue)
                    {
                        var lyStartDate = dateRange.CompareStartDate.Value.Date;
                        var lyEndDate = dateRange.CompareEndDate.Value.Date;

                        var lyQuery = _context
                            .Db.Queryable<StoreSalesStatistic>()
                            .Where(s => s.Date >= lyStartDate && s.Date <= lyEndDate);

                        if (normalizedBranchCodes.Count > 0)
                        {
                            lyQuery = lyQuery.Where(s => normalizedBranchCodes.Contains(s.BranchCode));
                        }

                        var lyData = await lyQuery
                            .GroupBy(s => new { s.Date, s.BranchCode })
                            .Select(s => new
                            {
                                Date = s.Date,
                                BranchCode = s.BranchCode,
                                BranchName = SqlFunc.AggregateMax(s.BranchName),
                                Revenue = SqlFunc.AggregateSum(s.TotalAmount),
                                OrderCount = SqlFunc.AggregateSum(s.OrderCount),
                            })
                            .ToListAsync();

                        lyDict = lyData
                            .Where(row => !string.IsNullOrWhiteSpace(row.BranchCode))
                            .Select(row => new
                            {
                                Row = row,
                                CurrentDate = startDate.AddDays((row.Date.Date - lyStartDate).Days),
                            })
                            .Where(item => item.CurrentDate >= startDate && item.CurrentDate <= endDate)
                            .GroupBy(item => (
                                BranchCode: item.Row.BranchCode.Trim().ToUpperInvariant(),
                                Date: item.CurrentDate
                            ))
                            .ToDictionary(
                                group => group.Key,
                                group =>
                                {
                                    var branchCode = group.First().Row.BranchCode.Trim();
                                    var branchName = group
                                        .Select(item => item.Row.BranchName)
                                        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                                        ?? branchCode;
                                    return (
                                        BranchCode: branchCode,
                                        BranchName: branchName,
                                        Revenue: group.Sum(item => item.Row.Revenue),
                                        OrderCount: group.Sum(item => item.Row.OrderCount)
                                    );
                                }
                            );
                    }

                    // 按当前区间和对比区间的相同偏移天数配对，兼容同周和同月份规则。
                    var currentDict = currentData
                        .Where(row => !string.IsNullOrWhiteSpace(row.BranchCode))
                        .GroupBy(row => (
                            BranchCode: row.BranchCode.Trim().ToUpperInvariant(),
                            Date: row.Date.Date
                        ))
                        .ToDictionary(
                            group => group.Key,
                            group =>
                            {
                                var branchCode = group.First().BranchCode.Trim();
                                var branchName = group
                                    .Select(row => row.BranchName)
                                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                                    ?? branchCode;
                                return (
                                    BranchCode: branchCode,
                                    BranchName: branchName,
                                    Revenue: group.Sum(row => row.Revenue),
                                    OrderCount: group.Sum(row => row.OrderCount)
                                );
                            }
                        );
                    var items = currentDict.Keys
                        .Union(lyDict.Keys)
                        .Select(key =>
                        {
                            var current = currentDict.TryGetValue(key, out var currentValue)
                                ? currentValue
                                : default;
                            var compare = lyDict.TryGetValue(key, out var compareValue)
                                ? compareValue
                                : default;
                            var branchCode = !string.IsNullOrWhiteSpace(current.BranchCode)
                                ? current.BranchCode
                                : compare.BranchCode;
                            var branchName = !string.IsNullOrWhiteSpace(current.BranchName)
                                ? current.BranchName
                                : !string.IsNullOrWhiteSpace(compare.BranchName)
                                    ? compare.BranchName
                                    : branchCode;

                            return new BranchDailyPerformanceDto
                            {
                                Date = key.Date,
                                BranchCode = branchCode,
                                BranchName = branchName,
                                Revenue = current.Revenue,
                                RevenueLY = compare.Revenue,
                                OrderCount = current.OrderCount,
                                OrderCountLY = compare.OrderCount,
                            };
                        })
                        .OrderBy(row => row.Date)
                        .ThenByDescending(row => row.Revenue)
                        .ThenBy(row => row.BranchCode, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var cacheOptions = new MemoryCacheEntryOptions()
                        .SetAbsoluteExpiration(RANKING_CACHE_DURATION)
                        .SetSlidingExpiration(TimeSpan.FromMinutes(5));

                    var response = CreateExecutiveReportResult(
                        items,
                        statisticsPending
                    );

                    if (!response.StatisticsPending)
                    {
                        _cache.Set(cacheKey, response, cacheOptions);
                    }

                    return response;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetBranchDailyPerformanceAsync failed");
                throw;
            }
        }

        private static ExecutiveReportResultDto<T> CreateExecutiveReportResult<T>(
            List<T> items,
            bool statisticsPending
        )
        {
            // Pending 快照没有可证明的最终条目数，故故意令 expected 大于 snapshot，
            // 防止客户端仅凭数组非空将局部结果展示为完整明细。
            return new ExecutiveReportResultDto<T>
            {
                Items = items,
                StatisticsPending = statisticsPending,
                StatisticsSnapshotItemCount = items.Count,
                StatisticsExpectedItemCount = statisticsPending ? items.Count + 1 : items.Count,
            };
        }

        public async Task<StatisticsFreshnessDto> GetStatisticsFreshnessAsync()
        {
            var latestRun = await _context.Db.Queryable<ScheduledTaskLog>()
                .Where(task => task.TaskType == TaskType.UpdateCurrentHourStatistics)
                .OrderByDescending(task => task.StartedAt)
                .FirstAsync();
            // 调度日志只描述一次尝试；完整报表的最后成功时间必须来自实际发布记录。
            var latestPublished = await _context.Db.Queryable<SalesStatisticRefreshState>()
                .Where(state =>
                    state.StatisticType == SalesStatisticType.RevenueReportPublished
                    && state.Status == SalesStatisticRefreshStatus.Fresh
                    && state.CompletedAtUtc != null
                    && state.Date >= SalesStatisticsBusinessDate.Today()
                    && state.Date < SalesStatisticsBusinessDate.Today().AddDays(1)
                )
                .OrderByDescending(state => state.CompletedAtUtc)
                .FirstAsync();

            var latestRunStatus = latestRun?.Status ?? "NeverRun";
            if (
                latestRun?.Status == ScheduledTaskStatus.Running
                && latestRun.StartedAt <= DateTime.UtcNow.Subtract(STATISTICS_RUNNING_STALE_AFTER)
            )
            {
                // 超时 Running 不能让客户端永久等待；对外降级为 Failed，并留下高等级告警供运维排查。
                latestRunStatus = ScheduledTaskStatus.Failed;
                _logger.LogError(
                    "销售统计任务长时间停留 Running，按失败状态返回。TaskId={TaskId}, StartedAt={StartedAt}",
                    latestRun.Id,
                    latestRun.StartedAt
                );
            }

            return new StatisticsFreshnessDto
            {
                LastSuccessfulAtUtc = latestPublished?.CompletedAtUtc is DateTime completedAt
                    ? DateTime.SpecifyKind(completedAt, DateTimeKind.Utc)
                    : null,
                LatestRunStatus = latestRunStatus,
            };
        }

        private async Task<string> GetStatisticsCacheVersionAsync(DateRangeDto dateRange)
        {
            // 查询范围与当前报表一致，利用 StatisticType + Date 索引避免扫描全历史状态。
            // 每日、每维度和每次真实提交都参与指纹，历史手工补算不会复用旧缓存。
            var startDate = dateRange.StartDate.Date;
            var endDate = dateRange.EndDate.Date;
            var compareStartDate = dateRange.CompareStartDate?.Date;
            var compareEndDate = dateRange.CompareEndDate?.Date;
            var statisticTypes = SalesStatisticType.DailyAlignmentTypes;
            var stateQuery = _context.Db.Queryable<SalesStatisticRefreshState>()
                .Where(state => statisticTypes.Contains(state.StatisticType));
            if (compareStartDate.HasValue && compareEndDate.HasValue)
            {
                var compareStart = compareStartDate.Value;
                var compareEnd = compareEndDate.Value;
                stateQuery = stateQuery.Where(state =>
                    (state.Date >= startDate && state.Date <= endDate)
                    || (state.Date >= compareStart && state.Date <= compareEnd)
                );
            }
            else
            {
                stateQuery = stateQuery.Where(state =>
                    state.Date >= startDate && state.Date <= endDate
                );
            }
            // 缓存指纹只需要真实提交版本字段，避免每个报表请求加载状态行的完整实体列。
            var states = await stateQuery.Select(state => new
            {
                state.Date,
                state.StatisticType,
                state.Status,
                state.LastAggregatedAtUtc,
                state.CompletedAtUtc,
                state.SourceProductVersion,
            }).ToListAsync();
            var versionSource = string.Join(
                "|",
                states
                    .OrderBy(state => state.Date)
                    .ThenBy(state => state.StatisticType, StringComparer.Ordinal)
                    .Select(state =>
                        $"{state.Date:yyyyMMdd}:{state.StatisticType}:{state.Status}:{state.LastAggregatedAtUtc?.Ticks ?? 0}:{state.CompletedAtUtc?.Ticks ?? 0}:{state.SourceProductVersion ?? ""}"
                    )
            );
            var statisticsVersion = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(versionSource))
            );
            var generation = Volatile.Read(ref GetReportStatisticsCacheGenerationState().Value);
            return $"{statisticsVersion}:g{generation}";
        }

        private ReportStatisticsCacheGenerationState GetReportStatisticsCacheGenerationState()
        {
            if (
                _cache.TryGetValue<ReportStatisticsCacheGenerationState>(
                    REPORT_STATISTICS_CACHE_GENERATION_KEY,
                    out var state
                )
                && state != null
            )
            {
                return state;
            }

            // 同一个 IMemoryCache 代表同一个应用实例；加锁只覆盖首次建代际对象，后续递增无锁。
            var gate = REPORT_STATISTICS_CACHE_GENERATION_LOCKS.GetValue(_cache, _ => new object());
            lock (gate)
            {
                if (
                    _cache.TryGetValue<ReportStatisticsCacheGenerationState>(
                        REPORT_STATISTICS_CACHE_GENERATION_KEY,
                        out state
                    )
                    && state != null
                )
                {
                    return state;
                }

                state = new ReportStatisticsCacheGenerationState();
                _cache.Set(
                    REPORT_STATISTICS_CACHE_GENERATION_KEY,
                    state,
                    new MemoryCacheEntryOptions().SetPriority(CacheItemPriority.NeverRemove)
                );
                return state;
            }
        }

        private long AdvanceReportStatisticsCacheGeneration()
        {
            return Interlocked.Increment(ref GetReportStatisticsCacheGenerationState().Value);
        }

        private sealed class WeeklyPerformanceStatisticRow
        {
            public DateTime Date { get; set; }
            public string BranchCode { get; set; } = string.Empty;
            public string BranchName { get; set; } = string.Empty;
            public decimal TotalAmount { get; set; }
            public int TotalQuantity { get; set; }
            public int OrderCount { get; set; }
            public decimal AverageOrderValue { get; set; }
        }

        private sealed class WeeklyPerformanceAlignedRow
        {
            public DateTime Date { get; set; }
            public string BranchCode { get; set; } = string.Empty;
            public string BranchName { get; set; } = string.Empty;
            public decimal TotalAmount { get; set; }
            public int OrderCount { get; set; }
            public decimal AverageOrderValue { get; set; }
            public decimal CompareTotalAmount { get; set; }
            public int CompareOrderCount { get; set; }
            public decimal CompareAverageOrderValue { get; set; }
        }

        /// <summary>
        /// 获取周业绩层级数据
        /// 用于 Executive Sales Intelligence 页面的 Weekly Performance Hierarchy 组件
        /// 返回周→分店→日期三层嵌套结构
        /// </summary>
        /// <param name="dateRange">日期范围</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <returns>周业绩层级数据、统计完整性和缓存版本</returns>
        public async Task<(
            ExecutiveReportResultDto<WeeklyPerformanceHierarchyDto> Report,
            string CacheVersion
        )> GetWeeklyPerformanceHierarchyAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes = null
        )
        {
            try
            {
                ValidateDateRange(dateRange);
                var normalizedBranchCodes = NormalizeCodes(branchCodes)
                    .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (branchCodes != null && normalizedBranchCodes.Count == 0)
                    return (new ExecutiveReportResultDto<WeeklyPerformanceHierarchyDto>(), "scope-empty");

                var statisticsRefreshState = await EnsureWeeklyStoreSalesStatisticsAsync(
                    dateRange,
                    normalizedBranchCodes
                );

                return await ReadReportSnapshotAsync(async () =>
                {
                    var statisticsStillMissing = new List<DateTime>();
                    if (statisticsRefreshState == StatisticsRefreshState.Completed)
                    {
                        statisticsStillMissing = await GetWeeklyMissingStoreStatisticDatesAsync(
                            dateRange.StartDate.Date,
                            dateRange.EndDate.Date,
                            normalizedBranchCodes
                        );
                        if (dateRange.CompareStartDate.HasValue && dateRange.CompareEndDate.HasValue)
                        {
                            statisticsStillMissing.AddRange(await GetWeeklyMissingStoreStatisticDatesAsync(
                                dateRange.CompareStartDate.Value.Date,
                                dateRange.CompareEndDate.Value.Date,
                                normalizedBranchCodes
                            ));
                        }
                    }

                    var statisticsPending = statisticsRefreshState == StatisticsRefreshState.Pending
                        || statisticsStillMissing.Count > 0;
                    var statisticsVersion = await GetStatisticsCacheVersionAsync(dateRange);
                    var items = await ReadWeeklyPerformanceHierarchyAsync(
                        dateRange,
                        branchCodes == null ? null : normalizedBranchCodes,
                        statisticsVersion,
                        statisticsPending
                    );
                    return (CreateExecutiveReportResult(items, statisticsPending), statisticsVersion);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetWeeklyPerformanceHierarchyAsync failed");
                throw;
            }
        }

        private async Task<StatisticsRefreshState> EnsureWeeklyStoreSalesStatisticsAsync(
            DateRangeDto dateRange,
            List<string> branchCodes
        )
        {
            var missingDates = await GetWeeklyMissingStoreStatisticDatesAsync(
                dateRange.StartDate.Date,
                dateRange.EndDate.Date,
                branchCodes
            );

            if (dateRange.CompareStartDate.HasValue && dateRange.CompareEndDate.HasValue)
            {
                missingDates.AddRange(await GetWeeklyMissingStoreStatisticDatesAsync(
                    dateRange.CompareStartDate.Value.Date,
                    dateRange.CompareEndDate.Value.Date,
                    branchCodes
                ));
            }

            // HBSales 历史窗口内的门店统计与商品门店日统计必须全分店原子刷新，保持既有统计口径。
            static bool RequiresAllBranchRefresh(DateTime date) =>
                SalesStatisticsHBSalesHistoryWindow.Includes(date);

            return await RefreshMissingStatisticsAsync(
                "store",
                "分店营业额",
                missingDates,
                branchCodes,
                async (service, date) =>
                {
                    if (StoreStatisticsRefreshTestInterceptor != null)
                    {
                        await StoreStatisticsRefreshTestInterceptor(date);
                        return;
                    }

                    await service.UpdateStoreStatistics(
                        date,
                        RequiresAllBranchRefresh(date)
                            ? null
                            : branchCodes.Count > 0
                                ? branchCodes
                                : null
                    );
                },
                RequiresAllBranchRefresh
            );
        }

        private async Task<List<DateTime>> GetWeeklyMissingStoreStatisticDatesAsync(
            DateTime startDate,
            DateTime endDate,
            List<string> branchCodes
        )
        {
            var missingDates = new List<DateTime>();
            var segmentStart = startDate.Date;
            var normalizedEnd = endDate.Date;
            while (segmentStart <= normalizedEnd)
            {
                var remainingDays = (normalizedEnd - segmentStart).Days;
                var segmentEnd = segmentStart.AddDays(
                    Math.Min(REPORT_STATISTICS_REFRESH_MAX_DAYS - 1, remainingDays)
                );
                missingDates.AddRange(await GetMissingStoreStatisticDatesAsync(
                    segmentStart,
                    segmentEnd,
                    branchCodes
                ));

                if (segmentEnd == normalizedEnd)
                    break;
                segmentStart = segmentEnd.AddDays(1);
            }

            return missingDates.Distinct().OrderBy(date => date).ToList();
        }

        private async Task<List<WeeklyPerformanceHierarchyDto>> ReadWeeklyPerformanceHierarchyAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes,
            string statisticsVersion,
            bool statisticsPending
        )
        {
            try
            {
                ValidateDateRange(dateRange);

                var startDate = dateRange.StartDate.Date;
                var endDate = dateRange.EndDate.Date;
                var normalizedBranchCodes = branchCodes?
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                // 显式空范围代表没有任何可访问分店，绝不能退化成全分店查询。
                if (branchCodes != null && normalizedBranchCodes!.Count == 0)
                    return new List<WeeklyPerformanceHierarchyDto>();

                var hasCompareRange =
                    dateRange.CompareStartDate.HasValue && dateRange.CompareEndDate.HasValue;
                var compareStartDate = hasCompareRange
                    ? dateRange.CompareStartDate!.Value.Date
                    : (DateTime?)null;
                var compareEndDate = hasCompareRange
                    ? dateRange.CompareEndDate!.Value.Date
                    : (DateTime?)null;
                var branchScopeKey = normalizedBranchCodes == null
                    ? "all"
                    : string.Join("|", normalizedBranchCodes.Select(code => $"{code.Length}:{code}"));
                var compareRangeKey = hasCompareRange
                    ? $"{compareStartDate:yyyyMMdd}_{compareEndDate:yyyyMMdd}"
                    : "none";

                var cacheKey =
                    $"WeeklyPerformanceHierarchy_{statisticsVersion}_{startDate:yyyyMMdd}_{endDate:yyyyMMdd}_{compareRangeKey}_{dateRange.CompareMode}_{branchScopeKey}";

                if (
                    !statisticsPending
                    &&
                    _cache.TryGetValue<List<WeeklyPerformanceHierarchyDto>>(
                        cacheKey,
                        out var cachedResult
                    )
                    && cachedResult != null
                )
                {
                    _logger.LogInformation("从缓存获取周业绩层级数据: {CacheKey}", cacheKey);
                    return cachedResult;
                }

                var query = _context
                    .Db.Queryable<StoreSalesStatistic>()
                    .Where(s => s.Date >= startDate && s.Date <= endDate);

                if (normalizedBranchCodes != null)
                {
                    query = query.Where(s => normalizedBranchCodes.Contains(s.BranchCode));
                }

                var currentData = await query
                    .Select(s => new WeeklyPerformanceStatisticRow
                    {
                        Date = s.Date,
                        BranchCode = s.BranchCode,
                        BranchName = s.BranchName,
                        TotalAmount = s.TotalAmount,
                        TotalQuantity = s.TotalQuantity,
                        OrderCount = s.OrderCount,
                        AverageOrderValue = s.AverageOrderValue,
                    })
                    .ToListAsync();

                var compareData = new List<WeeklyPerformanceStatisticRow>();
                if (hasCompareRange)
                {
                    var compareQuery = _context
                        .Db.Queryable<StoreSalesStatistic>()
                        .Where(s => s.Date >= compareStartDate!.Value && s.Date <= compareEndDate!.Value);

                    if (normalizedBranchCodes != null)
                    {
                        compareQuery = compareQuery.Where(s =>
                            normalizedBranchCodes.Contains(s.BranchCode)
                        );
                    }

                    compareData = await compareQuery
                        .Select(s => new WeeklyPerformanceStatisticRow
                        {
                            Date = s.Date,
                            BranchCode = s.BranchCode,
                            BranchName = s.BranchName,
                            TotalAmount = s.TotalAmount,
                            TotalQuantity = s.TotalQuantity,
                            OrderCount = s.OrderCount,
                            AverageOrderValue = s.AverageOrderValue,
                        })
                        .ToListAsync();
                }

                var currentDataByDateAndBranch = currentData.ToDictionary(
                    row => (row.Date.Date, row.BranchCode.Trim().ToUpperInvariant()),
                    row => row
                );
                var compareDataByCurrentDateAndBranch = compareData
                    .Select(row => new
                    {
                        CurrentDate = startDate.AddDays(
                            (row.Date.Date - compareStartDate!.Value).Days
                        ),
                        Row = row,
                    })
                    .Where(item => item.CurrentDate >= startDate && item.CurrentDate <= endDate)
                    .ToDictionary(
                        item => (item.CurrentDate, item.Row.BranchCode.Trim().ToUpperInvariant()),
                        item => item.Row
                    );
                // 同期独有的日期或门店也要映射回本期日期参与构树，本期数值明确补零。
                var alignedData = currentDataByDateAndBranch.Keys
                    .Union(compareDataByCurrentDateAndBranch.Keys)
                    .Select(key =>
                    {
                        currentDataByDateAndBranch.TryGetValue(key, out var current);
                        compareDataByCurrentDateAndBranch.TryGetValue(key, out var compare);
                        var branchCode = current?.BranchCode ?? compare?.BranchCode ?? key.Item2;
                        return new WeeklyPerformanceAlignedRow
                        {
                            Date = key.Item1,
                            BranchCode = branchCode,
                            BranchName = current?.BranchName ?? compare?.BranchName ?? branchCode,
                            TotalAmount = current?.TotalAmount ?? 0,
                            OrderCount = current?.OrderCount ?? 0,
                            AverageOrderValue = current?.AverageOrderValue ?? 0,
                            CompareTotalAmount = compare?.TotalAmount ?? 0,
                            CompareOrderCount = compare?.OrderCount ?? 0,
                            CompareAverageOrderValue = compare?.AverageOrderValue ?? 0,
                        };
                    })
                    .ToList();

                var storeDict = alignedData
                    .GroupBy(s => s.BranchCode)
                    .ToDictionary(g => g.Key, g => g.First().BranchName);

                var weekGroups = alignedData
                    .GroupBy(s => new
                    {
                        Year = ISOWeek.GetYear(s.Date),
                        Week = ISOWeek.GetWeekOfYear(s.Date),
                        s.BranchCode,
                    })
                    .OrderByDescending(g => g.Key.Year)
                    .ThenByDescending(g => g.Key.Week)
                    .ThenByDescending(g => g.Sum(x => x.TotalAmount))
                    .ToList();

                var result = new List<WeeklyPerformanceHierarchyDto>();

                foreach (var weekGroup in weekGroups)
                {
                    var weekKey = $"w{weekGroup.Key.Year}-{weekGroup.Key.Week:D2}";
                    var weekLabel = $"{weekGroup.Key.Year}-W{weekGroup.Key.Week:D2}";

                    var weekRevenue = weekGroup.Sum(x => x.TotalAmount);
                    var weekOrders = weekGroup.Sum(x => x.OrderCount);
                    var weekAov = weekOrders > 0 ? weekRevenue / weekOrders : 0;

                    var weekDto = result.FirstOrDefault(w => w.Key == weekKey);
                    if (weekDto == null)
                    {
                        weekDto = new WeeklyPerformanceHierarchyDto
                        {
                            Key = weekKey,
                            Level = "week",
                            Hierarchy = weekLabel,
                            Revenue = weekRevenue,
                            RevenueLY = 0,
                            Orders = weekOrders,
                            OrdersLY = 0,
                            Aov = weekAov,
                            AovLY = 0,
                            YoYChange = null,
                            Children = new List<WeeklyPerformanceHierarchyDto>(),
                        };
                        result.Add(weekDto);
                    }

                    var branchGroups = weekGroup
                        .GroupBy(s => s.BranchCode)
                        .OrderByDescending(g => g.Sum(x => x.TotalAmount))
                        .ToList();

                    if (weekDto == null)
                    {
                        continue;
                    }

                    foreach (var branchGroup in branchGroups)
                    {
                        var branchCode = branchGroup.Key;
                        var branchName = storeDict.GetValueOrDefault(branchCode, branchCode);

                        var branchRevenue = branchGroup.Sum(x => x.TotalAmount);
                        var branchOrders = branchGroup.Sum(x => x.OrderCount);
                        var branchAov = branchOrders > 0 ? branchRevenue / branchOrders : 0;

                        var branchDto = weekDto.Children!.FirstOrDefault(b =>
                            b.Key == $"{weekKey}-{branchCode}"
                        );
                        if (branchDto == null)
                        {
                            branchDto = new WeeklyPerformanceHierarchyDto
                            {
                                Key = $"{weekKey}-{branchCode}",
                                Level = "branch",
                                Hierarchy = branchName,
                                Revenue = branchRevenue,
                                RevenueLY = 0,
                                Orders = branchOrders,
                                OrdersLY = 0,
                                Aov = branchAov,
                                AovLY = 0,
                                YoYChange = null,
                                Children = new List<WeeklyPerformanceHierarchyDto>(),
                            };
                            weekDto.Children ??= new List<WeeklyPerformanceHierarchyDto>();
                            weekDto.Children.Add(branchDto);
                        }

                        var dateGroups = branchGroup.OrderByDescending(x => x.Date).ToList();

                        foreach (var dateItem in dateGroups)
                        {
                            var dateKey = $"{weekKey}-{branchCode}-{dateItem.Date:yyyyMMdd}";
                            var dateAov =
                                dateItem.OrderCount > 0
                                    ? dateItem.TotalAmount / dateItem.OrderCount
                                    : 0;

                            var dateRevenueLY = dateItem.CompareTotalAmount;
                            var dateOrdersLY = dateItem.CompareOrderCount;
                            var dateAovLY = dateItem.CompareAverageOrderValue;
                            var dateYoYChange =
                                dateRevenueLY > 0
                                    ? ((dateItem.TotalAmount - dateRevenueLY) / dateRevenueLY) * 100
                                    : (decimal?)null;

                            var dateDto = new WeeklyPerformanceHierarchyDto
                            {
                                Key = dateKey,
                                Level = "date",
                                Hierarchy = dateItem.Date.ToString("yyyy-MM-dd"),
                                Revenue = dateItem.TotalAmount,
                                RevenueLY = dateRevenueLY,
                                Orders = dateItem.OrderCount,
                                OrdersLY = dateOrdersLY,
                                Aov = dateAov,
                                AovLY = dateAovLY,
                                YoYChange = dateYoYChange,
                                Children = null,
                            };
                            branchDto.Children!.Add(dateDto);
                        }

                        branchDto.Revenue = branchDto.Children!.Sum(c => c.Revenue);
                        branchDto.Orders = branchDto.Children!.Sum(c => c.Orders);
                        branchDto.RevenueLY = branchDto.Children!.Sum(c => c.RevenueLY);
                        branchDto.OrdersLY = branchDto.Children!.Sum(c => c.OrdersLY);
                        branchDto.Aov =
                            branchDto.Orders > 0 ? branchDto.Revenue / branchDto.Orders : 0;
                        branchDto.AovLY =
                            branchDto.OrdersLY > 0 ? branchDto.RevenueLY / branchDto.OrdersLY : 0;
                        branchDto.YoYChange =
                            branchDto.RevenueLY > 0
                                ? ((branchDto.Revenue - branchDto.RevenueLY) / branchDto.RevenueLY) * 100
                                : (decimal?)null;
                    }

                    weekDto.Revenue = weekDto.Children!.Sum(c => c.Revenue);
                    weekDto.Orders = weekDto.Children!.Sum(c => c.Orders);
                    weekDto.RevenueLY = weekDto.Children!.Sum(c => c.RevenueLY);
                    weekDto.OrdersLY = weekDto.Children!.Sum(c => c.OrdersLY);
                    weekDto.Aov = weekDto.Orders > 0 ? weekDto.Revenue / weekDto.Orders : 0;
                    weekDto.AovLY = weekDto.OrdersLY > 0 ? weekDto.RevenueLY / weekDto.OrdersLY : 0;
                    weekDto.YoYChange =
                        weekDto.RevenueLY > 0
                            ? ((weekDto.Revenue - weekDto.RevenueLY) / weekDto.RevenueLY) * 100
                            : (decimal?)null;

                    if (
                        weekDto.Children!.Count > 0
                        && weekDto.Children!.Any(c => c.Children!.Count > 0)
                    )
                    {
                        var firstBranchWithDates = weekDto.Children!.FirstOrDefault(c =>
                            c.Children!.Count > 0
                        );
                        if (firstBranchWithDates != null)
                        {
                            weekDto.Children = weekDto
                                .Children!.OrderByDescending(c => c.Revenue)
                                .ToList();
                        }
                    }
                }

                var cacheOptions = new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(RANKING_CACHE_DURATION)
                    .SetSlidingExpiration(TimeSpan.FromMinutes(5));

                if (!statisticsPending)
                {
                    _cache.Set(cacheKey, result, cacheOptions);
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ReadWeeklyPerformanceHierarchyAsync failed");
                throw;
            }
        }

        private DateTime GetLastYearSameWeekday(DateTime date)
        {
            var compareDate = date.AddYears(-1);
            var originalDayOfWeek = date.DayOfWeek;
            var compareDayOfWeek = compareDate.DayOfWeek;

            if (originalDayOfWeek != compareDayOfWeek)
            {
                var diff = (int)originalDayOfWeek - (int)compareDayOfWeek;
                if (diff > 3)
                    diff -= 7;
                if (diff < -3)
                    diff += 7;
                compareDate = compareDate.AddDays(diff);
            }

            return compareDate;
        }

        private async Task<StatisticsRefreshState> EnsureStoreSalesStatisticsAsync(
            DateRangeDto dateRange,
            List<string> branchCodes
        )
        {
            var missingDates = await GetMissingStoreStatisticDatesAsync(
                dateRange.StartDate.Date,
                dateRange.EndDate.Date,
                branchCodes
            );

            if (dateRange.CompareStartDate.HasValue && dateRange.CompareEndDate.HasValue)
            {
                missingDates.AddRange(await GetMissingStoreStatisticDatesAsync(
                    dateRange.CompareStartDate.Value.Date,
                    dateRange.CompareEndDate.Value.Date,
                    branchCodes
                ));
            }

            // HBSales 历史窗口内的门店统计与商品门店日统计必须全分店原子刷新，不能沿用报表的分店筛选。
            static bool RequiresAllBranchRefresh(DateTime date) =>
                SalesStatisticsHBSalesHistoryWindow.Includes(date);

            return await RefreshMissingStatisticsAsync(
                "store",
                "分店营业额",
                missingDates,
                branchCodes,
                async (service, date) =>
                {
                    if (StoreStatisticsRefreshTestInterceptor != null)
                    {
                        await StoreStatisticsRefreshTestInterceptor(date);
                        return;
                    }

                    await service.UpdateStoreStatistics(
                        date,
                        RequiresAllBranchRefresh(date)
                            ? null
                            : branchCodes.Count > 0
                                ? branchCodes
                                : null
                    );
                },
                RequiresAllBranchRefresh
            );
        }

        private async Task<StatisticsRefreshState> EnsureHourlySalesStatisticsAsync(
            DateRangeDto dateRange,
            List<string> branchCodes
        )
        {
            var missingDates = await GetMissingHourlyStatisticDatesAsync(
                dateRange.StartDate.Date,
                dateRange.EndDate.Date,
                branchCodes
            );

            if (dateRange.CompareStartDate.HasValue && dateRange.CompareEndDate.HasValue)
            {
                missingDates.AddRange(await GetMissingHourlyStatisticDatesAsync(
                    dateRange.CompareStartDate.Value.Date,
                    dateRange.CompareEndDate.Value.Date,
                    branchCodes
                ));
            }

            return await RefreshMissingStatisticsAsync(
                "hourly",
                "分时营业额",
                missingDates,
                new List<string>(),
                async (service, date) =>
                {
                    if (HourlyStatisticsRefreshTestInterceptor != null)
                    {
                        await HourlyStatisticsRefreshTestInterceptor(date);
                        return;
                    }

                    await service.UpdateHourlyStatistics(date);
                }
            );
        }

        private async Task<List<DateTime>> GetMissingStoreStatisticDatesAsync(
            DateTime startDate,
            DateTime endDate,
            List<string> branchCodes
        )
        {
            return await ReadReportSnapshotAsync(async () =>
            {
                var expectedDates = EnumerateReportDates(startDate, endDate);
                if (expectedDates.Count == 0)
                    return new List<DateTime>();

                // 历史日期的 StoreSales 完成快照已经由后台任务写入状态表；只要统计表确实有行，
                // 这里无需为了每次报表请求再次扫描 POSM/HBSales 原始单据。当天或未认证日期仍走来源身份核验。
                var certifiedDates = await GetCertifiedStoreStatisticDatesAsync(expectedDates);
                var uncertifiedDates = expectedDates
                    .Where(date => !certifiedDates.Contains(date.Date))
                    .ToList();
                if (uncertifiedDates.Count == 0)
                    return new List<DateTime>();

                var missingDates = new List<DateTime>();
                foreach (var segment in EnumerateContiguousDateSegments(uncertifiedDates))
                {
                    missingDates.AddRange(
                        await GetMissingStoreStatisticDatesFromSourceAsync(
                            segment.StartDate,
                            segment.EndDate,
                            branchCodes
                        )
                    );
                }

                return missingDates.Distinct().OrderBy(date => date).ToList();
            });
        }

        private async Task<HashSet<DateTime>> GetCertifiedStoreStatisticDatesAsync(
            IReadOnlyCollection<DateTime> expectedDates
        )
        {
            var historicalDates = expectedDates
                .Select(date => date.Date)
                .Where(date => date < DateTime.Today)
                .Distinct()
                .ToList();
            if (historicalDates.Count == 0)
                return new HashSet<DateTime>();

            var startDate = historicalDates.Min();
            var endExclusive = historicalDates.Max().AddDays(1);
            var certifiedStateDates = await _context.Db.Queryable<SalesStatisticRefreshState>()
                .Where(state =>
                    state.StatisticType == SalesStatisticType.StoreSales
                    && state.Date >= startDate
                    && state.Date < endExclusive
                    && state.Status == SalesStatisticRefreshStatus.Fresh
                    && state.CompletedAtUtc.HasValue
                    && state.LastAggregatedAtUtc.HasValue
                    // 仅有完成标记还不够；没有统计行的日期继续核验零销售或漏写。
                    && SqlFunc.Subqueryable<StoreSalesStatistic>()
                        .Where(statistic =>
                            statistic.Date >= state.Date.Date
                            && statistic.Date < state.Date.Date.AddDays(1)
                        )
                        .Any()
                )
                .Select(state => state.Date)
                .ToListAsync();
            var expectedDateSet = historicalDates.ToHashSet();
            return certifiedStateDates
                .Select(date => date.Date)
                .Where(expectedDateSet.Contains)
                .ToHashSet();
        }

        private async Task<List<DateTime>> GetMissingStoreStatisticDatesFromSourceAsync(
            DateTime startDate,
            DateTime endDate,
            List<string> branchCodes
        )
        {
            var expectedDates = EnumerateReportDates(startDate, endDate);
            if (expectedDates.Count == 0)
                return new List<DateTime>();

            var query = _context.Db.Queryable<StoreSalesStatistic>()
                .Where(s => s.Date >= startDate && s.Date <= endDate);
            if (branchCodes.Count > 0)
            {
                query = query.Where(s => s.BranchCode != null && branchCodes.Contains(s.BranchCode));
            }

            var rows = await query
                .Select(s => new StatisticDateBranchRow { Date = s.Date, BranchCode = s.BranchCode })
                .ToListAsync();

            var expectedSalesBranchesByDate = await GetStoreSalesSourceBranchCodesByDateAsync(
                startDate,
                endDate,
                branchCodes
            );
            if (expectedSalesBranchesByDate == null)
            {
                // 来源覆盖无法确认时，保守触发营业额统计重算，避免真实销售缺口被隐藏。
                return expectedDates;
            }

            return GetMissingStoreStatisticDatesFromRows(
                expectedDates,
                rows,
                expectedSalesBranchesByDate
            );
        }

        private static IEnumerable<(DateTime StartDate, DateTime EndDate)> EnumerateContiguousDateSegments(
            IReadOnlyCollection<DateTime> dates
        )
        {
            DateTime? segmentStart = null;
            DateTime? previousDate = null;
            foreach (var date in dates.Select(value => value.Date).Distinct().OrderBy(value => value))
            {
                if (!segmentStart.HasValue)
                {
                    segmentStart = date;
                    previousDate = date;
                    continue;
                }

                if (date != previousDate!.Value.AddDays(1))
                {
                    yield return (segmentStart.Value, previousDate.Value);
                    segmentStart = date;
                }

                previousDate = date;
            }

            if (segmentStart.HasValue)
                yield return (segmentStart.Value, previousDate!.Value);
        }

        private async Task<List<DateTime>> GetMissingHourlyStatisticDatesAsync(
            DateTime startDate,
            DateTime endDate,
            List<string> branchCodes
        )
        {
            return await ReadReportSnapshotAsync(async () =>
            {
                var expectedDates = EnumerateReportDates(startDate, endDate);
                if (expectedDates.Count == 0)
                    return new List<DateTime>();

                var query = _context.Db.Queryable<HourlySalesStatistic>()
                    .Where(s => s.Date >= startDate && s.Date <= endDate);
                if (branchCodes.Count > 0)
                {
                    query = query.Where(s => s.BranchCode != null && branchCodes.Contains(s.BranchCode));
                }

                var rows = await query
                    .Select(s => new StatisticDateBranchHourRow
                    {
                        Date = s.Date,
                        BranchCode = s.BranchCode,
                        Hour = s.Hour,
                        OrderCount = s.OrderCount ?? 0,
                        TotalAmount = s.TotalAmount,
                    })
                    .ToListAsync();

                // 分时统计与分店日统计同源：HBSales 历史窗口内期望分店必须取 POSM 与 HBSales 并集，
                // 否则只在旧系统有单据的分店会被当成“不该有数据”，缺口永远不会触发补算。
                var expectedSalesBranchesByDate = await GetStoreSalesSourceBranchCodesByDateAsync(
                    startDate,
                    endDate,
                    branchCodes
                );
                if (expectedSalesBranchesByDate == null)
                {
                    // 来源覆盖无法确认时，保守触发重算；不能把局部小时统计误认为完整。
                    return expectedDates;
                }

                return GetMissingHourlyDatesFromRows(
                    expectedDates,
                    rows,
                    expectedSalesBranchesByDate
                );
            });
        }

        private async Task<Dictionary<DateTime, HashSet<string>>?> GetStoreSalesSourceBranchCodesByDateAsync(
            DateTime startDate,
            DateTime endDate,
            List<string> branchCodes
        )
        {
            startDate = startDate.Date;
            endDate = endDate.Date;
            var result = new Dictionary<DateTime, HashSet<string>>();

            // 门店日统计始终聚合 POSM；HBSales 已核验历史窗口还会叠加 HBSales，完整性身份必须取两者并集。
            var posmCoverage = await GetPosmStoreSalesBranchCodesByDateAsync(
                startDate,
                endDate,
                branchCodes
            );
            if (posmCoverage == null)
                return null;
            MergeSalesSourceCoverage(result, posmCoverage);

            var hbSalesStart = startDate > SalesStatisticsHBSalesHistoryWindow.StartDate
                ? startDate
                : SalesStatisticsHBSalesHistoryWindow.StartDate;
            var historyEndDate = SalesStatisticsHBSalesHistoryWindow.EndExclusive.AddDays(-1);
            var hbSalesEnd = endDate < historyEndDate
                ? endDate
                : historyEndDate;
            if (hbSalesStart <= hbSalesEnd)
            {
                var hbSalesCoverage = await GetHbSalesStoreSalesBranchCodesByDateAsync(
                    hbSalesStart,
                    hbSalesEnd,
                    branchCodes
                );
                if (hbSalesCoverage == null)
                    return null;
                MergeSalesSourceCoverage(result, hbSalesCoverage);
            }

            return result;
        }

        private Task<Dictionary<DateTime, HashSet<string>>?> GetPosmStoreSalesBranchCodesByDateAsync(
            DateTime startDate,
            DateTime endDate,
            List<string> branchCodes
        )
        {
            return GetCachedSalesSourceCoverageAsync(
                "POSM",
                startDate,
                endDate,
                branchCodes,
                async () =>
                {
                    PosmStoreSalesCoverageReadTestInterceptor?.Invoke();
                    var nextDate = endDate.Date.AddDays(1);
                    var sourceRows = await _posmContext.Db.Queryable<SalesOrder>()
                        .Where(so =>
                            so.Status != null
                            && (so.Status == 1 || so.Status == 4)
                            && so.OrderTime != null
                            && so.OrderTime >= startDate.Date
                            && so.OrderTime < nextDate
                        )
                        // 最长 35 天的覆盖检查只需要唯一身份组合，避免把每笔订单拉入应用内存。
                        .GroupBy(so => new
                        {
                            Date = so.OrderTime!.Value.Date,
                            so.BranchCode,
                            so.DeviceCode,
                        })
                        .Select(so => new StatisticDateBranchDeviceRow
                        {
                            Date = so.OrderTime!.Value.Date,
                            BranchCode = so.BranchCode,
                            DeviceCode = so.DeviceCode,
                        })
                        .ToListAsync();
                    var deviceBranchMap = await SalesStatisticsProductStoreDailySourceQueries
                        .LoadDeviceBranchMapAsync(
                            _posmContext,
                            sourceRows
                                .Where(row => string.IsNullOrWhiteSpace(row.BranchCode))
                                .Select(row => row.DeviceCode)
                        );
                    var branchSet = branchCodes
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Select(code => code.Trim())
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    // 与日表及分时统计一致：先用设备映射补齐分店，再应用请求的分店范围。
                    return sourceRows
                        .Select(row => new StatisticDateBranchRow
                        {
                            Date = row.Date,
                            BranchCode = SalesStatisticsCodeRules.ResolveBranchCode(
                                row.BranchCode,
                                row.DeviceCode,
                                deviceBranchMap
                            ),
                        })
                        .Where(row =>
                            !string.IsNullOrWhiteSpace(row.BranchCode)
                            && (branchSet.Count == 0 || branchSet.Contains(row.BranchCode))
                        )
                        .ToList();
                }
            );
        }

        private Task<Dictionary<DateTime, HashSet<string>>?> GetHbSalesStoreSalesBranchCodesByDateAsync(
            DateTime startDate,
            DateTime endDate,
            List<string> branchCodes
        )
        {
            return GetCachedSalesSourceCoverageAsync(
                "HBSales",
                startDate,
                endDate,
                branchCodes,
                async () =>
                {
                    if (_serviceScopeFactory == null)
                        throw new InvalidOperationException("HBSales 来源覆盖查询缺少服务作用域");

                    using var scope = _serviceScopeFactory.CreateScope();
                    var hbSalesContext = scope.ServiceProvider.GetService<HBSalesRecordSqlSugarContext>()
                        ?? throw new InvalidOperationException("HBSales 来源覆盖查询缺少数据库上下文");
                    if (hbSalesContext.Db.CurrentConnectionConfig.DbType == DbType.SqlServer)
                    {
                        var sql = BuildHbSalesStoreSalesCoverageSql(hbSalesContext.Db, startDate, endDate, branchCodes);
                        return await hbSalesContext.Db.Ado.SqlQueryAsync<StatisticDateBranchRow>(sql.Key, sql.Value.ToArray());
                    }
                    return await BuildHbSalesStoreSalesCoverageQuery(hbSalesContext.Db, startDate, endDate, branchCodes)
                        .ToListAsync();
                }
            );
        }

        internal static ISugarQueryable<StatisticDateBranchRow> BuildHbSalesStoreSalesCoverageQuery(
            ISqlSugarClient db,
            DateTime startDate,
            DateTime endDate,
            List<string> branchCodes
        )
        {
            var nextDate = endDate.Date.AddDays(1);
            var mainWindowStart = startDate.Date.AddDays(-7);
            var mainWindowEnd = nextDate.AddDays(7);
            var query = db.Queryable<SalesOrderMain>()
                .LeftJoin<SalesOrderDetailRecord>((main, detail) =>
                    main.B销售单号 == detail.B销售单号
                )
                .Where((main, detail) =>
                    detail.B结账日期.HasValue
                    && detail.B结账日期.Value >= startDate.Date
                    && detail.B结账日期.Value < nextDate
                    && main.B结账日期.HasValue
                    && main.B结账日期.Value >= mainWindowStart
                    && main.B结账日期.Value < mainWindowEnd
                    && (main.B单据类型 == null || main.B单据类型.Trim() != "2")
                    && detail.B分店代码 != null
                    && detail.B分店代码.Trim() != ""
                );

            if (branchCodes.Count > 0)
            {
                query = query.Where((main, detail) =>
                    branchCodes.Contains(detail.B分店代码!.Trim())
                );
            }

            return query
                .GroupBy((main, detail) => new
                {
                    Date = detail.B结账日期!.Value.Date,
                    BranchCode = detail.B分店代码!.Trim(),
                })
                .Select((main, detail) => new StatisticDateBranchRow
                {
                    Date = detail.B结账日期!.Value.Date,
                    BranchCode = detail.B分店代码!.Trim(),
                });
        }

        /// <summary>
        /// HBSales 来源覆盖在 SQL Server 上按实际日期重编译。日期窗口随请求逐日变化，
        /// 带参编译的计划会退化为约 40 万逻辑读的联表扫描：2026-09-21 生产实测原句 11.3 秒、
        /// 加 OPTION (RECOMPILE) 后 0.1 秒。分时报表每次打开都要核验去年同日，这条语句决定首屏耗时。
        /// </summary>
        internal static KeyValuePair<string, List<SugarParameter>> BuildHbSalesStoreSalesCoverageSql(
            ISqlSugarClient db,
            DateTime startDate,
            DateTime endDate,
            List<string> branchCodes
        )
        {
            var sql = BuildHbSalesStoreSalesCoverageQuery(db, startDate, endDate, branchCodes).ToSql();
            return new KeyValuePair<string, List<SugarParameter>>(sql.Key + " OPTION (RECOMPILE)", sql.Value);
        }

        private async Task<Dictionary<DateTime, HashSet<string>>?> GetCachedSalesSourceCoverageAsync(
            string source,
            DateTime startDate,
            DateTime endDate,
            List<string> branchCodes,
            Func<Task<List<StatisticDateBranchRow>>> readRowsAsync
        )
        {
            var branchKey = branchCodes.Count > 0
                ? string.Join(",", branchCodes.OrderBy(code => code, StringComparer.OrdinalIgnoreCase))
                : "ALL";
            var cacheKey =
                $"SalesSourceCoverage_{source}_{startDate:yyyyMMdd}_{endDate:yyyyMMdd}_{branchKey}";
            if (
                _cache.TryGetValue<Dictionary<DateTime, string[]>>(cacheKey, out var cachedSnapshot)
                && cachedSnapshot != null
            )
            {
                return CloneSalesSourceCoverage(cachedSnapshot);
            }

            // 同一 IMemoryCache 代表同一应用实例；跨请求共享 single-flight，避免首屏三个组件同时扫来源明细。
            var cacheReads = SALES_SOURCE_COVERAGE_READS.GetValue(
                _cache,
                _ => new ConcurrentDictionary<
                    string,
                    Lazy<Task<Dictionary<DateTime, HashSet<string>>?>>
                >()
            );
            var lazyRead = cacheReads.GetOrAdd(
                cacheKey,
                _ => new Lazy<Task<Dictionary<DateTime, HashSet<string>>?>>(
                    async () =>
                    {
                        if (
                            _cache.TryGetValue<Dictionary<DateTime, string[]>>(
                                cacheKey,
                                out var concurrentSnapshot
                            )
                            && concurrentSnapshot != null
                        )
                        {
                            return CloneSalesSourceCoverage(concurrentSnapshot);
                        }

                        try
                        {
                            var rows = await readRowsAsync();
                            var coverage = rows
                                .Where(row => !string.IsNullOrWhiteSpace(row.BranchCode))
                                .GroupBy(row => row.Date.Date)
                                .ToDictionary(
                                    group => group.Key,
                                    group => group
                                        .Select(row => row.BranchCode!.Trim())
                                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                                );
                            var snapshot = coverage.ToDictionary(
                                pair => pair.Key,
                                pair => pair.Value
                                    .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                                    .ToArray()
                            );
                            _cache.Set(
                                cacheKey,
                                snapshot,
                                new MemoryCacheEntryOptions().SetAbsoluteExpiration(
                                    SALES_SOURCE_COVERAGE_CACHE_DURATION
                                )
                            );
                            return coverage;
                        }
                        catch (Exception ex)
                        {
                            // 失败结果绝不进入缓存；下一个请求必须重新尝试并继续 fail closed。
                            _logger.LogWarning(
                                ex,
                                "读取 {Source} 分店销售覆盖范围失败，保守触发营业额统计重算",
                                source
                            );
                            return null;
                        }
                    },
                    LazyThreadSafetyMode.ExecutionAndPublication
                )
            );

            try
            {
                var coverage = await lazyRead.Value;
                return coverage == null ? null : CloneSalesSourceCoverage(coverage);
            }
            finally
            {
                ((ICollection<
                    KeyValuePair<
                        string,
                        Lazy<Task<Dictionary<DateTime, HashSet<string>>?>>
                    >
                >)cacheReads).Remove(new KeyValuePair<
                    string,
                    Lazy<Task<Dictionary<DateTime, HashSet<string>>?>>
                >(cacheKey, lazyRead));
            }
        }

        private static Dictionary<DateTime, HashSet<string>> CloneSalesSourceCoverage(
            Dictionary<DateTime, string[]> snapshot
        )
        {
            return snapshot.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToHashSet(StringComparer.OrdinalIgnoreCase)
            );
        }

        private static Dictionary<DateTime, HashSet<string>> CloneSalesSourceCoverage(
            Dictionary<DateTime, HashSet<string>> coverage
        )
        {
            return coverage.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToHashSet(StringComparer.OrdinalIgnoreCase)
            );
        }

        private static void MergeSalesSourceCoverage(
            Dictionary<DateTime, HashSet<string>> target,
            Dictionary<DateTime, HashSet<string>> source
        )
        {
            foreach (var (date, branchCodes) in source)
            {
                if (!target.TryGetValue(date, out var existing))
                {
                    target[date] = branchCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    continue;
                }

                existing.UnionWith(branchCodes);
            }
        }

        private async Task<HashSet<string>?> GetExpectedExecutiveBranchCodesAsync(
            DateTime startDate,
            DateTime endDate,
            List<string> branchCodes
        )
        {
            try
            {
                ExpectedExecutiveBranchCodesTestInterceptor?.Invoke();
                var coverage = await GetStoreSalesSourceBranchCodesByDateAsync(
                    startDate,
                    endDate,
                    branchCodes
                );
                return coverage?
                    .Values.SelectMany(codes => codes)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                // Pending 本身仍是权威状态；来源身份读取失败时退回当前快照，避免接口再次失败。
                _logger.LogWarning(ex, "读取营业额排行预期分店身份失败");
                return null;
            }
        }

        private async Task<StatisticsRefreshState> RefreshMissingStatisticsAsync(
            string kind,
            string label,
            IEnumerable<DateTime> missingDates,
            List<string> branchCodes,
            Func<SalesStatisticsJobService, DateTime, Task> refreshAsync,
            Func<DateTime, bool>? requiresAllBranchRefresh = null
        )
        {
            var dates = missingDates
                .Select(date => date.Date)
                .Distinct()
                .OrderBy(date => date)
                .ToList();
            if (dates.Count == 0)
                return StatisticsRefreshState.NotNeeded;

            if (_serviceScopeFactory == null)
            {
                _logger.LogWarning("{Label}统计缺失但无法自动重算，缺少 IServiceScopeFactory", label);
                return StatisticsRefreshState.Pending;
            }

            var branchKey = branchCodes.Count > 0
                ? string.Join(",", branchCodes.OrderBy(code => code, StringComparer.OrdinalIgnoreCase))
                : "ALL";
            var pendingItems = dates
                .Select(date => new
                {
                    Date = date,
                    Key = BuildReportStatisticsRefreshKey(
                        kind,
                        date,
                        requiresAllBranchRefresh?.Invoke(date) == true ? "ALL" : branchKey
                    ),
                })
                .Where(item => REPORT_STATISTICS_REFRESHING_KEYS.TryAdd(item.Key, 0))
                .ToList();

            if (pendingItems.Count == 0)
                return StatisticsRefreshState.Pending;

            var refreshTask = Task.Run(async () =>
            {
                var allSucceeded = true;
                try
                {
                    using var scope = _serviceScopeFactory.CreateScope();
                    var statisticsJobService = scope.ServiceProvider.GetRequiredService<SalesStatisticsJobService>();

                    foreach (var item in pendingItems)
                    {
                        try
                        {
                            // 报表请求只触发缺口日期重算，实际统计口径复用后台统计任务。
                            await refreshAsync(statisticsJobService, item.Date);
                        }
                        catch (Exception ex)
                        {
                            allSucceeded = false;
                            _logger.LogWarning(ex, "{Label}统计自动重算失败: {Date}", label, item.Date);
                        }
                    }
                }
                catch (Exception ex)
                {
                    allSucceeded = false;
                    _logger.LogWarning(ex, "{Label}统计自动重算任务启动失败", label);
                }
                finally
                {
                    if (allSucceeded)
                    {
                        var generation = AdvanceReportStatisticsCacheGeneration();
                        _logger.LogInformation(
                            "{Label}统计自动重算完成，营业额报表缓存切换至代际 {Generation}",
                            label,
                            generation
                        );
                    }

                    foreach (var item in pendingItems)
                    {
                        REPORT_STATISTICS_REFRESHING_KEYS.TryRemove(item.Key, out _);
                    }

                    if (string.Equals(kind, "store", StringComparison.Ordinal))
                    {
                        StoreStatisticsRefreshCompletedTestInterceptor?.Invoke();
                    }
                }

                return allSucceeded;
            });

            var completedTask = await Task.WhenAny(
                refreshTask,
                Task.Delay(REPORT_STATISTICS_REFRESH_WAIT)
            );
            if (completedTask == refreshTask)
            {
                return await refreshTask
                    ? StatisticsRefreshState.Completed
                    : StatisticsRefreshState.Pending;
            }

            // 超过 250ms 后先读取已有快照，刷新状态继续标记为 Pending。
            _logger.LogInformation(
                "{Label}统计缺失，已触发后台重算: {Dates}",
                label,
                string.Join(",", pendingItems.Select(item => item.Date.ToString("yyyy-MM-dd")))
            );
            return StatisticsRefreshState.Pending;
        }

        private static List<DateTime> EnumerateReportDates(DateTime startDate, DateTime endDate)
        {
            startDate = startDate.Date;
            endDate = endDate.Date;
            if (startDate > endDate)
                return new List<DateTime>();

            var days = (endDate - startDate).Days + 1;
            if (days > REPORT_STATISTICS_REFRESH_MAX_DAYS)
                return new List<DateTime>();

            return Enumerable.Range(0, days)
                .Select(offset => startDate.AddDays(offset))
                .ToList();
        }

        private static List<DateTime> GetMissingDatesFromRows(
            List<DateTime> expectedDates,
            List<string> branchCodes,
            IEnumerable<StatisticDateBranchRow> rows,
            Dictionary<DateTime, int>? expectedBranchCounts = null
        )
        {
            if (branchCodes.Count == 0)
            {
                if (expectedBranchCounts != null && expectedBranchCounts.Count > 0)
                {
                    var foundBranchCounts = rows
                        .Where(row =>
                            !string.IsNullOrWhiteSpace(row.BranchCode)
                            && !string.Equals(row.BranchCode, "ALL", StringComparison.OrdinalIgnoreCase)
                        )
                        .GroupBy(row => row.Date.Date)
                        .ToDictionary(
                            group => group.Key,
                            group => group
                                .Select(row => row.BranchCode!)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .Count()
                        );

                    return expectedDates
                        .Where(date =>
                            expectedBranchCounts.TryGetValue(date, out var expectedCount)
                            && foundBranchCounts.GetValueOrDefault(date) < expectedCount
                        )
                        .ToList();
                }

                var datesWithRows = rows.Select(row => row.Date.Date).ToHashSet();
                return expectedDates
                    .Where(date => !datesWithRows.Contains(date))
                    .ToList();
            }

            var branchSet = branchCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var foundByDate = rows
                .Where(row => !string.IsNullOrWhiteSpace((string?)row.BranchCode))
                .GroupBy(row => row.Date.Date)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(row => row.BranchCode!)
                        .Where(code => branchSet.Contains(code))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count()
                );

            return expectedDates
                .Where(date => foundByDate.GetValueOrDefault(date) < branchSet.Count)
                .ToList();
        }

        private static List<DateTime> GetMissingStoreStatisticDatesFromRows(
            List<DateTime> expectedDates,
            IEnumerable<StatisticDateBranchRow> rows,
            Dictionary<DateTime, HashSet<string>> expectedSalesBranchesByDate
        )
        {
            var foundBranchesByDate = rows
                .Where(row =>
                    !string.IsNullOrWhiteSpace(row.BranchCode)
                    && !string.Equals(row.BranchCode, "ALL", StringComparison.OrdinalIgnoreCase)
                )
                .GroupBy(row => row.Date.Date)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(row => row.BranchCode!)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)
                );

            // 零销售分店不会产生统计行；来源与统计身份必须逐日完全一致。
            // 即使来源当天为空，也必须识别残留统计行，避免陈旧快照进入长缓存。
            return expectedDates
                .Where(date => !expectedSalesBranchesByDate
                    .GetValueOrDefault(
                        date,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    )
                    .SetEquals(
                        foundBranchesByDate.GetValueOrDefault(
                            date,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        )
                    ))
                .ToList();
        }

        private static List<DateTime> GetMissingHourlyDatesFromRows(
            List<DateTime> expectedDates,
            IEnumerable<StatisticDateBranchHourRow> rows,
            Dictionary<DateTime, HashSet<string>> expectedSalesBranchesByDate
        )
        {
            var rowList = rows.ToList();
            var missingDates = GetMissingStoreStatisticDatesFromRows(
                    expectedDates,
                    rowList.Select(row => new StatisticDateBranchRow
                    {
                        Date = row.Date,
                        BranchCode = row.BranchCode,
                    }),
                    expectedSalesBranchesByDate
                )
                .ToHashSet();

            foreach (var date in rowList
                         .Where(row =>
                             !string.IsNullOrWhiteSpace(row.BranchCode)
                             && !string.Equals(row.BranchCode, "ALL", StringComparison.OrdinalIgnoreCase)
                             && row.TotalAmount > 0
                             && row.OrderCount <= 0
                         )
                         .Select(row => row.Date.Date))
            {
                // 营业额报表只信营业额统计表；若统计行已有金额却没有客单数，说明该日统计口径需要重算。
                missingDates.Add(date);
            }

            return missingDates
                .OrderBy(date => date)
                .ToList();
        }

        private static string BuildReportStatisticsRefreshKey(
            string kind,
            DateTime date,
            string branchKey
        )
        {
            return $"ReportStatisticsRefresh_{kind}_{date:yyyyMMdd}_{branchKey}";
        }

        private static List<string> NormalizeBranchCodes(List<string>? branchCodes)
        {
            return branchCodes?
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code!.Trim())
                    .Distinct()
                    .ToList()
                ?? new List<string>();
        }

        private static List<string> NormalizeCodes(IEnumerable<string>? codes)
        {
            return codes?
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                ?? new List<string>();
        }

        private async Task<List<string>> GetProductCodesForSearchAsync(string? productSearch)
        {
            if (string.IsNullOrWhiteSpace(productSearch))
                return new List<string>();

            var searchText = productSearch.Trim();
            return (await _context
                    .Db.Queryable<Product>()
                    .Where(p =>
                        (p.ItemNumber != null && p.ItemNumber.Contains(searchText))
                        || (p.Barcode != null && p.Barcode.Contains(searchText))
                    )
                    .Select(p => p.ProductCode ?? string.Empty)
                    .ToListAsync())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal async Task<Dictionary<string, string>> GetChinaSupplierProductMapAsync(
            IEnumerable<string>? chinaSupplierCodes = null
        )
        {
            // 映射同步可能正在写入；显式关闭 NOLOCK，在 POSM 的已提交快照中读取整份映射。
            return await ReadReportSnapshotOnConnectionAsync(_posmContext.Db, async () =>
            {
                var supplierCodes = NormalizeCodes(chinaSupplierCodes);
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                // 未指定国内供应商代码时读取全部 200 映射；否则按每批最多 500 个代码分次 Contains 再合并。
                if (!supplierCodes.Any())
                {
                    var allRows = await _posmContext
                        .Db.Queryable<PosmProductSupplierMapping>()
                        .With(SqlWith.Null)
                        .Where(m =>
                            !m.IsDeleted
                            && m.LocalSupplierCode == "200"
                            && m.ChinaSupplierCode != null
                            && m.ChinaSupplierCode != ""
                        )
                        .Select(m => new ChinaSupplierProductMapRow
                        {
                            ProductCode = m.ProductCode,
                            ChinaSupplierCode = m.ChinaSupplierCode ?? string.Empty,
                        })
                        .ToListAsync();
                    MergeChinaSupplierProductMap(result, allRows);
                    return result;
                }

                foreach (var batch in BatchProductSalesCodes(supplierCodes))
                {
                    var batchCodes = batch.ToList();
                    var rows = await _posmContext
                        .Db.Queryable<PosmProductSupplierMapping>()
                        .With(SqlWith.Null)
                        .Where(m =>
                            !m.IsDeleted
                            && m.LocalSupplierCode == "200"
                            && m.ChinaSupplierCode != null
                            && batchCodes.Contains(m.ChinaSupplierCode)
                        )
                        .Select(m => new ChinaSupplierProductMapRow
                        {
                            ProductCode = m.ProductCode,
                            ChinaSupplierCode = m.ChinaSupplierCode ?? string.Empty,
                        })
                        .ToListAsync();
                    MergeChinaSupplierProductMap(result, rows);
                }

                return result;
            });
        }

        private static void MergeChinaSupplierProductMap(
            Dictionary<string, string> target,
            IEnumerable<ChinaSupplierProductMapRow> rows
        )
        {
            foreach (var row in rows)
            {
                if (
                    !string.IsNullOrWhiteSpace(row.ProductCode)
                    && !string.IsNullOrWhiteSpace(row.ChinaSupplierCode)
                )
                {
                    if (!target.ContainsKey(row.ProductCode))
                        target[row.ProductCode] = row.ChinaSupplierCode;
                }
            }
        }

        private async Task<HashSet<string>> GetChinaSupplierCodeSetAsync(
            IEnumerable<string>? seedCodes = null
        )
        {
            var codes = NormalizeCodes(seedCodes).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // 集合包含停用和软删除的国内供应商：直写行只能靠编码是否属于该集合来识别，
            // 供应商被删除后，它的历史销售在澳洲侧仍要归 200，不能变成一个普通澳洲供应商。
            // 旧 200 行不受影响：它们的国内编码来自调用方传入的映射种子，本来就不看删除标记。
            codes.UnionWith(await ChinaSupplierCodeFamily.LoadChinaSupplierCodesAsync(_context.Db));
            return codes;
        }

        /// <summary>
        /// 商品销量分析专用国内供应商目录：包含停用/软删除记录，保证期间直写历史代码
        /// 仍能按国内供应商口径识别并保留历史名称。
        /// </summary>
        private async Task<ProductSalesChinaCatalog> GetProductSalesChinaCatalogAsync()
        {
            var rows = await _context
                .Db.Queryable<ChinaSupplier>()
                .Where(s => s.SupplierCode != null && s.SupplierCode != "")
                .Select(s => new
                {
                    SupplierCode = s.SupplierCode ?? string.Empty,
                    SupplierName = s.SupplierName ?? string.Empty,
                })
                .ToListAsync();

            var catalog = new ProductSalesChinaCatalog();
            foreach (var group in rows
                .Where(row => !string.IsNullOrWhiteSpace(row.SupplierCode))
                .GroupBy(row => row.SupplierCode.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                var code = group.Key;
                var name = group
                    .Select(row => string.IsNullOrWhiteSpace(row.SupplierName) ? code : row.SupplierName.Trim())
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .First();

                catalog.Codes.Add(code);
                catalog.Names[code] = name;
            }

            return catalog;
        }

        private static string? ResolveChinaSupplierCodeFromStatistic(
            string? statisticSupplierCode,
            string? productCode,
            Dictionary<string, string> chinaProductMap,
            HashSet<string> chinaSupplierCodes
        )
        {
            var supplierCode = statisticSupplierCode?.Trim() ?? string.Empty;
            if (string.Equals(supplierCode, "200", StringComparison.OrdinalIgnoreCase))
            {
                // 旧日统计把中国商品写成本地供应商 200，需要通过 POSM 商品映射还原中国供应商。
                return !string.IsNullOrWhiteSpace(productCode)
                    && chinaProductMap.TryGetValue(productCode, out var mappedSupplierCode)
                    ? mappedSupplierCode
                    : null;
            }

            // 新日统计直接把中国供应商编码写入 SupplierCode。
            return chinaSupplierCodes.Contains(supplierCode) ? supplierCode : null;
        }

        private static bool IsChinaLocalSupplierCode(string? supplierCode)
        {
            return string.Equals(
                supplierCode?.Trim(),
                CHINA_LOCAL_SUPPLIER_CODE,
                StringComparison.OrdinalIgnoreCase
            );
        }

        private static bool HasCompleteGrossProfit(
            int statisticRowCount,
            int costedRowCount,
            int grossProfitRowCount
        )
        {
            // SQL SUM 会忽略 NULL；只有所有参与行均具备成本和毛利时，才能对外返回汇总毛利。
            return statisticRowCount > 0
                && statisticRowCount == costedRowCount
                && statisticRowCount == grossProfitRowCount;
        }

        private static decimal? GetCompleteGrossProfit(
            decimal? grossProfit,
            int statisticRowCount,
            int costedRowCount,
            int grossProfitRowCount
        )
        {
            return HasCompleteGrossProfit(
                statisticRowCount,
                costedRowCount,
                grossProfitRowCount
            )
                ? grossProfit
                : null;
        }

        private static decimal? CalculateGrossMarginRate(decimal salesAmount, decimal? grossProfit)
        {
            return salesAmount > 0m && grossProfit.HasValue
                ? grossProfit.Value / salesAmount
                : null;
        }

        private static string ResolveAustralianSupplierCode(
            string? statisticSupplierCode,
            HashSet<string> chinaSupplierCodes
        )
        {
            var supplierCode = statisticSupplierCode?.Trim() ?? string.Empty;
            if (
                IsChinaLocalSupplierCode(supplierCode)
                || chinaSupplierCodes.Contains(supplierCode)
            )
            {
                return CHINA_LOCAL_SUPPLIER_CODE;
            }

            return supplierCode;
        }

        private static List<SupplierBranchAggregateRow> ResolveAustralianSupplierBranchRows(
            IEnumerable<SupplierBranchAggregateRow> rows,
            HashSet<string> chinaSupplierCodes,
            bool preserveDate = false
        )
        {
            var resolvedRows = rows.Select(row => new SupplierBranchAggregateRow
            {
                Date = row.Date?.Date,
                SupplierCode = ResolveAustralianSupplierCode(row.SupplierCode, chinaSupplierCodes),
                BranchCode = row.BranchCode,
                TotalAmount = row.TotalAmount,
                TotalQuantity = row.TotalQuantity,
                OrderCount = row.OrderCount,
                GrossProfit = row.GrossProfit,
                StatisticRowCount = row.StatisticRowCount,
                CostedRowCount = row.CostedRowCount,
                GrossProfitRowCount = row.GrossProfitRowCount,
            });

            return preserveDate
                ? AggregateSupplierDateBranchRows(resolvedRows)
                : AggregateSupplierBranchRows(resolvedRows);
        }

        private static ISugarQueryable<ProductStoreDailySalesStatistic> ApplyAustralianSupplierStatisticFilter(
            ISugarQueryable<ProductStoreDailySalesStatistic> query,
            IEnumerable<string>? supplierCodes,
            HashSet<string>? chinaSupplierCodes = null
        )
        {
            var codes = NormalizeCodes(supplierCodes);
            if (!codes.Any())
                return query;

            if (!codes.Any(IsChinaLocalSupplierCode))
                return query.Where(s => codes.Contains(s.SupplierCode));

            var localSupplierCodes = codes
                .Where(code => !IsChinaLocalSupplierCode(code))
                .ToList();
            var supplierFilterCodes = localSupplierCodes
                .Concat(new[] { CHINA_LOCAL_SUPPLIER_CODE })
                .Concat(chinaSupplierCodes ?? Enumerable.Empty<string>())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 200 汇总行兼容新旧日统计：旧统计写 200，新统计直写中国供应商编码。
            return query.Where(s => supplierFilterCodes.Contains(s.SupplierCode));
        }

        private static IEnumerable<SupplierBranchAggregateRow> MapChinaSupplierRowsToLocal200(
            IEnumerable<SupplierBranchAggregateRow> rows
        )
        {
            return rows.Select(row => new SupplierBranchAggregateRow
            {
                Date = row.Date?.Date,
                SupplierCode = CHINA_LOCAL_SUPPLIER_CODE,
                BranchCode = row.BranchCode,
                TotalAmount = row.TotalAmount,
                TotalQuantity = row.TotalQuantity,
                OrderCount = row.OrderCount,
                GrossProfit = row.GrossProfit,
                StatisticRowCount = row.StatisticRowCount,
                CostedRowCount = row.CostedRowCount,
                GrossProfitRowCount = row.GrossProfitRowCount,
            });
        }

        private static string NormalizeCoverageBranchKey(string? branchCode)
        {
            return branchCode?.Trim() ?? string.Empty;
        }

        private static string BuildDateBranchCoverageKey(DateTime date, string? branchCode)
        {
            return $"{date:yyyyMMdd}|{NormalizeCoverageBranchKey(branchCode)}";
        }

        private static List<SupplierBranchAggregateRow> FilterUncoveredChinaLocalFallbackRows(
            IEnumerable<SupplierBranchAggregateRow> rows,
            IEnumerable<SupplierBranchAggregateRow> chinaFallbackRows
        )
        {
            var chinaLocalRows = rows
                .Where(row => IsChinaLocalSupplierCode(row.SupplierCode))
                .ToList();
            if (!chinaLocalRows.Any())
                return chinaFallbackRows.ToList();

            var coversAllFallback = chinaLocalRows.Any(row =>
                !row.Date.HasValue && string.IsNullOrWhiteSpace(row.BranchCode)
            );
            if (coversAllFallback)
                return new List<SupplierBranchAggregateRow>();

            var coveredBranches = chinaLocalRows
                .Where(row => !row.Date.HasValue && !string.IsNullOrWhiteSpace(row.BranchCode))
                .Select(row => NormalizeCoverageBranchKey(row.BranchCode))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var coveredDateBranches = chinaLocalRows
                .Where(row => row.Date.HasValue)
                .Select(row => BuildDateBranchCoverageKey(row.Date!.Value.Date, row.BranchCode))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return chinaFallbackRows
                .Where(row =>
                    !coveredBranches.Contains(NormalizeCoverageBranchKey(row.BranchCode))
                    && row.Date.HasValue
                    && !coveredDateBranches.Contains(
                        BuildDateBranchCoverageKey(row.Date.Value.Date, row.BranchCode)
                    )
                )
                .ToList();
        }

        private async Task<List<SupplierBranchAggregateRow>> AddChinaLocalSupplierFallbackRowsIfMissingAsync(
            List<SupplierBranchAggregateRow> rows,
            DateTime startDate,
            DateTime endDate,
            List<string>? branchCodes,
            bool includeChinaLocalSupplier,
            HashSet<string> chinaSupplierCodes
        )
        {
            if (includeChinaLocalSupplier)
            {
                // 中国拆分表和澳洲 200 可能覆盖同一批销售；按日期+分店只补缺片段，避免部分统计时漏数或重复。
                var chinaFallbackRows = await QueryChinaSupplierStoreDateBranchAggregateRowsAsync(
                    startDate,
                    endDate,
                    branchCodes
                );
                var fallbackRows = FilterUncoveredChinaLocalFallbackRows(rows, chinaFallbackRows);
                rows.AddRange(MapChinaSupplierRowsToLocal200(fallbackRows));
            }

            return ResolveAustralianSupplierBranchRows(rows, chinaSupplierCodes);
        }

        private static ISugarQueryable<ProductStoreDailySalesStatistic> ApplyChinaSupplierStatisticFilter(
            ISugarQueryable<ProductStoreDailySalesStatistic> query,
            HashSet<string> targetChinaSupplierCodes,
            Dictionary<string, string> chinaProductMap,
            bool limitLegacy200Products
        )
        {
            var legacyProductCodes = limitLegacy200Products
                ? chinaProductMap.Keys
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();

            if (
                legacyProductCodes.Any()
                && legacyProductCodes.Count <= LEGACY_CHINA_SUPPLIER_PRODUCT_FILTER_LIMIT
            )
            {
                // 指定中国供应商时，旧 200 统计只查该供应商映射商品，避免把当天所有 200 商品拉回内存。
                return query.Where(s =>
                    targetChinaSupplierCodes.Contains(s.SupplierCode)
                    || (s.SupplierCode == "200" && legacyProductCodes.Contains(s.ProductCode))
                );
            }

            if (limitLegacy200Products && !legacyProductCodes.Any())
            {
                // 没有 POSM 商品映射时，200 行无法还原成目标中国供应商，不参与主查询。
                return query.Where(s => targetChinaSupplierCodes.Contains(s.SupplierCode));
            }

            return query.Where(s =>
                s.SupplierCode == "200" || targetChinaSupplierCodes.Contains(s.SupplierCode)
            );
        }

        private async Task<bool> HasChinaProductStatisticRowsAsync(
            DateTime startDate,
            DateTime endDate,
            List<string>? branchCodes,
            HashSet<string> targetChinaSupplierCodes
        )
        {
            var query = await BuildProductReportStatisticQueryAsync(startDate, endDate, branchCodes);
            query = query.Where(s =>
                s.SupplierCode == "200" || targetChinaSupplierCodes.Contains(s.SupplierCode)
            );

            // 只在快路径查不到商品统计时执行，用于区分“统计未生成”和“POSM 映射缺失”。
            return await query.AnyAsync();
        }

        private async Task<PagedSalesProductDetailWithDiscountDto> GetEnhancedSalesProductDetailsFastPathAsync(
            DateTime startDate,
            DateTime endDate,
            DateTime? compareStartDate,
            DateTime? compareEndDate,
            List<string>? branchCodes,
            List<string>? localSupplierCodes,
            int pageIndex,
            int pageSize,
            string? productSearch,
            ProductReportSort sort
        )
        {
            var currentStatisticQuery = await BuildProductReportStatisticQueryAsync(
                startDate,
                endDate,
                branchCodes,
                localSupplierCodes,
                chinaProductCodes: null,
                productSearch,
                chinaSupplierCodes: null
            );
            var periodQueries = new List<ISugarQueryable<ProductReportProductAggregateRow>>
            {
                BuildProductReportProductAggregateQuery(currentStatisticQuery, period: 0),
            };

            if (compareStartDate.HasValue && compareEndDate.HasValue)
            {
                var compareStatisticQuery = await BuildProductReportStatisticQueryAsync(
                    compareStartDate.Value,
                    compareEndDate.Value,
                    branchCodes,
                    localSupplierCodes,
                    chinaProductCodes: null,
                    productSearch,
                    chinaSupplierCodes: null
                );
                periodQueries.Add(BuildProductReportProductAggregateQuery(compareStatisticQuery, period: 1));
            }

            // UnionAll 后再按商品码聚合，确保同期独有商品与当前期商品都留在同一个 SQL 分页集合内。
            var periodAggregateQuery = periodQueries.Count == 1
                ? periodQueries[0]
                : _context.Db.UnionAll(periodQueries.ToArray()).MergeTable();
            var combinedQuery = periodAggregateQuery
                .GroupBy(row => row.ProductCode)
                .Select(row => new ProductReportProductCombinedAggregateRow
                {
                    ProductCode = row.ProductCode,
                    CurrentProductName = SqlFunc.AggregateMax(
                        SqlFunc.IIF(row.Period == 0, row.ProductName, null)
                    ),
                    CompareProductName = SqlFunc.AggregateMax(
                        SqlFunc.IIF(row.Period == 1, row.ProductName, null)
                    ),
                    CurrentQuantity = SqlFunc.AggregateSum(
                        SqlFunc.IIF(row.Period == 0, row.Quantity, 0)
                    ),
                    CurrentSalesAmount = SqlFunc.AggregateSum(
                        SqlFunc.IIF(row.Period == 0, row.SalesAmount, 0m)
                    ),
                    CurrentOrderCount = SqlFunc.AggregateSum(
                        SqlFunc.IIF(row.Period == 0, row.OrderCount, 0)
                    ),
                    CurrentGrossProfit = SqlFunc.AggregateSum(
                        SqlFunc.IIF(row.Period == 0, row.GrossProfit, null)
                    ),
                    CurrentStatisticRowCount = SqlFunc.AggregateSum(
                        SqlFunc.IIF(row.Period == 0, row.StatisticRowCount, 0)
                    ),
                    CurrentCostedRowCount = SqlFunc.AggregateSum(
                        SqlFunc.IIF(row.Period == 0, row.CostedRowCount, 0)
                    ),
                    CurrentGrossProfitRowCount = SqlFunc.AggregateSum(
                        SqlFunc.IIF(row.Period == 0, row.GrossProfitRowCount, 0)
                    ),
                    CompareQuantity = SqlFunc.AggregateSum(
                        SqlFunc.IIF(row.Period == 1, row.Quantity, 0)
                    ),
                    CompareSalesAmount = SqlFunc.AggregateSum(
                        SqlFunc.IIF(row.Period == 1, row.SalesAmount, 0m)
                    ),
                    CompareOrderCount = SqlFunc.AggregateSum(
                        SqlFunc.IIF(row.Period == 1, row.OrderCount, 0)
                    ),
                    CompareGrossProfit = SqlFunc.AggregateSum(
                        SqlFunc.IIF(row.Period == 1, row.GrossProfit, null)
                    ),
                    CompareStatisticRowCount = SqlFunc.AggregateSum(
                        SqlFunc.IIF(row.Period == 1, row.StatisticRowCount, 0)
                    ),
                    CompareCostedRowCount = SqlFunc.AggregateSum(
                        SqlFunc.IIF(row.Period == 1, row.CostedRowCount, 0)
                    ),
                    CompareGrossProfitRowCount = SqlFunc.AggregateSum(
                        SqlFunc.IIF(row.Period == 1, row.GrossProfitRowCount, 0)
                    ),
                })
                .MergeTable();
            var total = await combinedQuery.CountAsync();
            var skip = (long)(pageIndex - 1) * pageSize;
            if (skip >= total || skip >= int.MaxValue)
            {
                return new PagedSalesProductDetailWithDiscountDto
                {
                    Data = new List<SalesProductDetailWithDiscountDto>(),
                    Total = total,
                    PageIndex = pageIndex,
                    PageSize = pageSize,
                };
            }

            // 排序在 CountAsync 之后追加（queryable 会被就地修改）；本期值 → 同期值 → 商品编码，保证翻页无重复无遗漏。
            var direction = sort.Ascending ? OrderByType.Asc : OrderByType.Desc;
            var orderedQuery = sort.Field switch
            {
                ProductReportSortField.Quantity => combinedQuery
                    .OrderBy(row => row.CurrentQuantity, direction)
                    .OrderBy(row => row.CompareQuantity, direction),
                // 均价用固定常量片段排序，避免 SQLite 把 decimal 常量参数按 TEXT 比较以及整数除法截断。
                ProductReportSortField.UnitPrice => combinedQuery
                    .OrderBy(BuildFastPathUnitPriceOrderBy(sort.Ascending)),
                _ => combinedQuery
                    .OrderBy(row => row.CurrentSalesAmount, direction)
                    .OrderBy(row => row.CompareSalesAmount, direction),
            };
            var pageRows = await orderedQuery
                .OrderBy(row => row.ProductCode, OrderByType.Asc)
                .Skip((int)skip)
                .Take(pageSize)
                .ToListAsync();
            var pageProductCodes = pageRows.Select(row => row.ProductCode).ToList();
            var products = pageProductCodes.Any()
                ? await _context
                    .Db.Queryable<Product>()
                    .Where(product =>
                        product.ProductCode != null && pageProductCodes.Contains(product.ProductCode)
                    )
                    .Select(product => new ProductInfo
                    {
                        ProductCode = product.ProductCode ?? string.Empty,
                        ItemNumber = product.ItemNumber,
                        ProductImage = product.ProductImage,
                    })
                    .ToListAsync()
                : new List<ProductInfo>();
            var productDict = products.ToDictionary(product => product.ProductCode, StringComparer.OrdinalIgnoreCase);
            var data = pageRows
                .Select(row =>
                {
                    var hasCurrentData = row.CurrentStatisticRowCount > 0;
                    var hasCompareData = row.CompareStatisticRowCount > 0;
                    var result = new SalesProductDetailWithDiscountDto
                    {
                        ProductCode = row.ProductCode,
                        ItemNumber = productDict.TryGetValue(row.ProductCode, out var product)
                            ? product.ItemNumber
                            : null,
                        ProductImage = productDict.TryGetValue(row.ProductCode, out var productImage)
                            ? productImage.ProductImage
                            : null,
                        ProductName = row.CurrentProductName ?? row.CompareProductName,
                        Quantity = row.CurrentQuantity,
                        DiscountedQuantity = 0,
                        SalesAmount = row.CurrentSalesAmount,
                        AverageUnitPrice = row.CurrentQuantity > 0
                            ? row.CurrentSalesAmount / row.CurrentQuantity
                            : 0,
                        AverageOriginalPrice = null,
                        OrderCount = row.CurrentOrderCount,
                        CostStatus = GetCostStatus(
                            row.CurrentStatisticRowCount,
                            row.CurrentCostedRowCount,
                            row.CurrentGrossProfitRowCount
                        ),
                        GrossProfit = hasCurrentData
                            ? GetCompleteGrossProfit(
                                row.CurrentGrossProfit,
                                row.CurrentStatisticRowCount,
                                row.CurrentCostedRowCount,
                                row.CurrentGrossProfitRowCount
                            )
                            : null,
                        QuantityLY = row.CompareQuantity,
                        DiscountedQuantityLY = 0,
                        SalesAmountLY = row.CompareSalesAmount,
                        AverageUnitPriceLY = row.CompareQuantity > 0
                            ? row.CompareSalesAmount / row.CompareQuantity
                            : 0,
                        AverageOriginalPriceLY = null,
                        OrderCountLY = row.CompareOrderCount,
                        CompareCostStatus = GetCostStatus(
                            row.CompareStatisticRowCount,
                            row.CompareCostedRowCount,
                            row.CompareGrossProfitRowCount
                        ),
                        GrossProfitLY = hasCompareData
                            ? GetCompleteGrossProfit(
                                row.CompareGrossProfit,
                                row.CompareStatisticRowCount,
                                row.CompareCostedRowCount,
                                row.CompareGrossProfitRowCount
                            )
                            : null,
                    };
                    result.GrossMarginRate = CalculateGrossMarginRate(result.SalesAmount, result.GrossProfit);
                    result.GrossMarginRateLY = CalculateGrossMarginRate(
                        result.SalesAmountLY,
                        result.GrossProfitLY
                    );
                    return result;
                })
                .ToList();

            return new PagedSalesProductDetailWithDiscountDto
            {
                Data = data,
                Total = total,
                PageIndex = pageIndex,
                PageSize = pageSize,
            };
        }

        private static ISugarQueryable<ProductReportProductAggregateRow> BuildProductReportProductAggregateQuery(
            ISugarQueryable<ProductStoreDailySalesStatistic> query,
            int period
        )
        {
            return query
                .GroupBy(statistic => statistic.ProductCode)
                .Select(statistic => new ProductReportProductAggregateRow
                {
                    ProductCode = statistic.ProductCode,
                    ProductName = SqlFunc.AggregateMax(statistic.ProductName),
                    Period = period,
                    Quantity = SqlFunc.AggregateSum(statistic.TotalQuantity),
                    SalesAmount = SqlFunc.AggregateSum(statistic.TotalAmount),
                    OrderCount = SqlFunc.AggregateSum(statistic.OrderCount),
                    GrossProfit = SqlFunc.AggregateSum(statistic.GrossProfit),
                    StatisticRowCount = SqlFunc.AggregateCount(statistic.ProductCode),
                    CostedRowCount = SqlFunc.AggregateCount(statistic.TotalCost),
                    GrossProfitRowCount = SqlFunc.AggregateCount(statistic.GrossProfit),
                })
                .MergeTable();
        }

        private async Task<List<ProductReportProductAggregateRow>> QueryProductReportProductAggregatesAsync(
            DateTime startDate,
            DateTime endDate,
            List<string>? branchCodes,
            List<string>? localSupplierCodes,
            List<string> normalizedChinaSupplierCodes,
            Dictionary<string, string> chinaProductMap,
            string? productSearch
        )
        {
            var normalizedProductSearch = productSearch?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedProductSearch))
            {
                normalizedProductSearch = null;
            }

            var searchProductCodes = normalizedProductSearch == null
                ? new List<string>()
                : await GetProductCodesForSearchAsync(normalizedProductSearch);
            var normalizedLocalSupplierCodes = NormalizeCodes(localSupplierCodes);
            if (
                !normalizedChinaSupplierCodes.Any()
                && normalizedLocalSupplierCodes.Any(IsChinaLocalSupplierCode)
            )
            {
                var query = await BuildProductReportStatisticQueryAsync(
                    startDate,
                    endDate,
                    branchCodes,
                    localSupplierCodes: null,
                    chinaProductCodes: null,
                    normalizedProductSearch,
                    chinaSupplierCodes: null
                );
                query = ApplyAustralianSupplierStatisticFilter(
                    query,
                    normalizedLocalSupplierCodes,
                    await GetChinaSupplierCodeSetAsync()
                );
                return await QueryProductReportProductAggregatesAsync(query);
            }
            var targetChinaSupplierCodes = normalizedChinaSupplierCodes.ToHashSet(
                StringComparer.OrdinalIgnoreCase
            );
            if (!targetChinaSupplierCodes.Any())
            {
                var query = await BuildProductReportStatisticQueryAsync(
                    startDate,
                    endDate,
                    branchCodes,
                    localSupplierCodes,
                    chinaProductCodes: null,
                    normalizedProductSearch,
                    chinaSupplierCodes: null
                );
                return await QueryProductReportProductAggregatesAsync(query);
            }

            var legacyProductCodes = chinaProductMap.Keys
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (normalizedProductSearch != null)
            {
                return await QueryChinaSupplierProductAggregatesWithSearchAsync(
                    startDate,
                    endDate,
                    branchCodes,
                    localSupplierCodes,
                    targetChinaSupplierCodes,
                    legacyProductCodes,
                    searchProductCodes,
                    normalizedProductSearch
                );
            }

            if (legacyProductCodes.Count <= LEGACY_CHINA_SUPPLIER_PRODUCT_FILTER_LIMIT)
            {
                var query = await BuildProductReportStatisticQueryAsync(
                    startDate,
                    endDate,
                    branchCodes,
                    localSupplierCodes,
                    chinaProductCodes: null,
                    normalizedProductSearch,
                    chinaSupplierCodes: null
                );
                query = ApplyChinaSupplierStatisticFilter(
                    query,
                    targetChinaSupplierCodes,
                    chinaProductMap,
                    limitLegacy200Products: true
                );
                return await QueryProductReportProductAggregatesAsync(query);
            }

            var rows = new List<ProductReportProductAggregateRow>();
            var directQuery = await BuildProductReportStatisticQueryAsync(
                startDate,
                endDate,
                branchCodes,
                localSupplierCodes,
                chinaProductCodes: null,
                normalizedProductSearch,
                chinaSupplierCodes: null
            );
            directQuery = directQuery.Where(s => targetChinaSupplierCodes.Contains(s.SupplierCode));
            rows.AddRange(await QueryProductReportProductAggregatesAsync(directQuery));

            foreach (var chunk in legacyProductCodes.Chunk(LEGACY_CHINA_SUPPLIER_PRODUCT_FILTER_LIMIT))
            {
                var chunkProductCodes = chunk.ToList();
                if (searchProductCodes.Any())
                {
                    // 大映射 + 宽泛搜索时先在内存求交集，避免同一条 SQL 同时携带两组大 ProductCode IN 参数。
                    chunkProductCodes = chunkProductCodes
                        .Intersect(searchProductCodes, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (!chunkProductCodes.Any())
                        continue;
                }

                var legacyQuery = await BuildProductReportStatisticQueryAsync(
                    startDate,
                    endDate,
                    branchCodes,
                    localSupplierCodes,
                    chinaProductCodes: null,
                    searchProductCodes.Any() ? null : normalizedProductSearch,
                    chinaSupplierCodes: null
                );
                // 大供应商映射超过 SQL Server 参数预算时，旧 200 统计按商品码分批查询再合并。
                legacyQuery = legacyQuery.Where(s =>
                    s.SupplierCode == "200" && chunkProductCodes.Contains(s.ProductCode)
                );
                rows.AddRange(await QueryProductReportProductAggregatesAsync(legacyQuery));
            }

            return MergeProductReportProductAggregates(rows);
        }

        private async Task<List<ProductReportProductAggregateRow>> QueryChinaSupplierProductAggregatesWithSearchAsync(
            DateTime startDate,
            DateTime endDate,
            List<string>? branchCodes,
            List<string>? localSupplierCodes,
            HashSet<string> targetChinaSupplierCodes,
            List<string> legacyProductCodes,
            List<string> searchProductCodes,
            string productSearch
        )
        {
            var rows = new List<ProductReportProductAggregateRow>();
            var searchProductCodeSet = searchProductCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);

            var directProductCodes = new HashSet<string>(searchProductCodeSet, StringComparer.OrdinalIgnoreCase);
            var directBarcodeQuery = await BuildProductReportStatisticQueryAsync(
                startDate,
                endDate,
                branchCodes,
                localSupplierCodes,
                chinaProductCodes: null,
                productSearch: null,
                chinaSupplierCodes: null
            );
            directBarcodeQuery = directBarcodeQuery.Where(s =>
                targetChinaSupplierCodes.Contains(s.SupplierCode)
                && s.Barcode != null
                && s.Barcode.Contains(productSearch)
            );
            foreach (var productCode in await directBarcodeQuery.Select(s => s.ProductCode).ToListAsync())
            {
                if (!string.IsNullOrWhiteSpace(productCode))
                    directProductCodes.Add(productCode.Trim());
            }

            foreach (var chunk in directProductCodes.Chunk(LEGACY_CHINA_SUPPLIER_PRODUCT_FILTER_LIMIT))
            {
                var productCodes = chunk.ToList();
                var directQuery = await BuildProductReportStatisticQueryAsync(
                    startDate,
                    endDate,
                    branchCodes,
                    localSupplierCodes,
                    chinaProductCodes: null,
                    productSearch: null,
                    chinaSupplierCodes: null
                );
                directQuery = directQuery.Where(s =>
                    targetChinaSupplierCodes.Contains(s.SupplierCode)
                    && productCodes.Contains(s.ProductCode)
                );
                rows.AddRange(await QueryProductReportProductAggregatesAsync(directQuery));
            }

            foreach (var chunk in legacyProductCodes.Chunk(LEGACY_CHINA_SUPPLIER_PRODUCT_FILTER_LIMIT))
            {
                var mappedProductCodes = chunk.ToList();
                var targetProductCodes = mappedProductCodes
                    .Where(searchProductCodeSet.Contains)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var legacyBarcodeQuery = await BuildProductReportStatisticQueryAsync(
                    startDate,
                    endDate,
                    branchCodes,
                    localSupplierCodes,
                    chinaProductCodes: null,
                    productSearch: null,
                    chinaSupplierCodes: null
                );
                legacyBarcodeQuery = legacyBarcodeQuery.Where(s =>
                    s.SupplierCode == "200"
                    && mappedProductCodes.Contains(s.ProductCode)
                    && s.Barcode != null
                    && s.Barcode.Contains(productSearch)
                );
                foreach (var productCode in await legacyBarcodeQuery.Select(s => s.ProductCode).ToListAsync())
                {
                    if (!string.IsNullOrWhiteSpace(productCode))
                        targetProductCodes.Add(productCode.Trim());
                }

                if (!targetProductCodes.Any())
                    continue;

                var productCodes = targetProductCodes.ToList();
                var legacyQuery = await BuildProductReportStatisticQueryAsync(
                    startDate,
                    endDate,
                    branchCodes,
                    localSupplierCodes,
                    chinaProductCodes: null,
                    productSearch: null,
                    chinaSupplierCodes: null
                );
                // 搜索命中的商品码先与供应商映射求交集，再分批查询，避免搜索和映射两组大 IN 参数叠加。
                legacyQuery = legacyQuery.Where(s =>
                    s.SupplierCode == "200" && productCodes.Contains(s.ProductCode)
                );
                rows.AddRange(await QueryProductReportProductAggregatesAsync(legacyQuery));
            }

            return MergeProductReportProductAggregates(rows);
        }

        private static async Task<List<ProductReportProductAggregateRow>> QueryProductReportProductAggregatesAsync(
            ISugarQueryable<ProductStoreDailySalesStatistic> query
        )
        {
            return await query
                .GroupBy(s => s.ProductCode)
                .Select(s => new ProductReportProductAggregateRow
                {
                    ProductCode = s.ProductCode,
                    ProductName = SqlFunc.AggregateMax(s.ProductName),
                    Quantity = SqlFunc.AggregateSum(s.TotalQuantity),
                    SalesAmount = SqlFunc.AggregateSum(s.TotalAmount),
                    OrderCount = SqlFunc.AggregateSum(s.OrderCount),
                    GrossProfit = SqlFunc.AggregateSum(s.GrossProfit),
                    StatisticRowCount = SqlFunc.AggregateCount(s.ProductCode),
                    CostedRowCount = SqlFunc.AggregateCount(s.TotalCost),
                    GrossProfitRowCount = SqlFunc.AggregateCount(s.GrossProfit),
                })
                .MergeTable()
                .ToListAsync();
        }

        private static List<ProductReportProductAggregateRow> MergeProductReportProductAggregates(
            IEnumerable<ProductReportProductAggregateRow> rows
        )
        {
            return rows
                .Where(row => !string.IsNullOrWhiteSpace(row.ProductCode))
                .GroupBy(row => row.ProductCode, StringComparer.OrdinalIgnoreCase)
                .Select(group => new ProductReportProductAggregateRow
                {
                    ProductCode = group.Key,
                    ProductName = group
                        .Select(row => row.ProductName)
                        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
                    Quantity = group.Sum(row => row.Quantity),
                    SalesAmount = group.Sum(row => row.SalesAmount),
                    OrderCount = group.Sum(row => row.OrderCount),
                    GrossProfit = group.Sum(row => row.GrossProfit ?? 0m),
                    StatisticRowCount = group.Sum(row => row.StatisticRowCount),
                    CostedRowCount = group.Sum(row => row.CostedRowCount),
                    GrossProfitRowCount = group.Sum(row => row.GrossProfitRowCount),
                })
                .ToList();
        }

        private async Task<List<SupplierBranchAggregateRow>> QueryAustralianSupplierStoreAggregateRowsAsync(
            DateTime startDate,
            DateTime endDate,
            List<string>? branchCodes = null,
            IEnumerable<string>? supplierCodes = null
        )
        {
            var normalizedBranchCodes = NormalizeCodes(branchCodes);
            var normalizedSupplierCodes = NormalizeCodes(supplierCodes);

            var query = _context.Db.Queryable<AustralianSupplierStoreSalesDetail>()
                .Where(s => s.Date >= startDate && s.Date <= endDate);

            if (normalizedBranchCodes.Any())
            {
                query = query.Where(s => normalizedBranchCodes.Contains(s.BranchCode));
            }

            if (normalizedSupplierCodes.Any())
            {
                query = query.Where(s => normalizedSupplierCodes.Contains(s.SupplierCode));
            }

            return await query
                .GroupBy(s => new { s.SupplierCode, s.BranchCode })
                .Select(s => new SupplierBranchAggregateRow
                {
                    SupplierCode = s.SupplierCode,
                    BranchCode = s.BranchCode,
                    TotalAmount = SqlFunc.AggregateSum(s.TotalAmount),
                    TotalQuantity = SqlFunc.AggregateSum(s.TotalQuantity),
                    OrderCount = SqlFunc.AggregateSum(s.OrderCount),
                    // 旧供应商统计没有成本快照；计入缺失行，禁止与新日统计混合后伪造完整毛利。
                    StatisticRowCount = SqlFunc.AggregateCount(s.SupplierCode),
                })
                .ToListAsync();
        }

        private async Task<List<SupplierBranchAggregateRow>> QueryChinaSupplierStoreAggregateRowsAsync(
            DateTime startDate,
            DateTime endDate,
            List<string>? branchCodes = null,
            IEnumerable<string>? supplierCodes = null
        )
        {
            var normalizedBranchCodes = NormalizeCodes(branchCodes);
            var normalizedSupplierCodes = NormalizeCodes(supplierCodes);

            var query = _context.Db.Queryable<ChinaSupplierStoreSalesDetail>()
                .Where(s => s.Date >= startDate && s.Date <= endDate);

            if (normalizedBranchCodes.Any())
            {
                query = query.Where(s => normalizedBranchCodes.Contains(s.BranchCode));
            }

            if (normalizedSupplierCodes.Any())
            {
                query = query.Where(s => normalizedSupplierCodes.Contains(s.SupplierCode));
            }

            return await query
                .GroupBy(s => new { s.SupplierCode, s.BranchCode })
                .Select(s => new SupplierBranchAggregateRow
                {
                    SupplierCode = s.SupplierCode,
                    BranchCode = s.BranchCode,
                    TotalAmount = SqlFunc.AggregateSum(s.TotalAmount),
                    TotalQuantity = SqlFunc.AggregateSum(s.TotalQuantity),
                    OrderCount = SqlFunc.AggregateSum(s.OrderCount),
                    // 旧供应商统计没有成本快照；计入缺失行，禁止与新日统计混合后伪造完整毛利。
                    StatisticRowCount = SqlFunc.AggregateCount(s.SupplierCode),
                })
                .ToListAsync();
        }

        private async Task<List<SupplierBranchAggregateRow>> QueryChinaSupplierStoreDateBranchAggregateRowsAsync(
            DateTime startDate,
            DateTime endDate,
            List<string>? branchCodes = null,
            IEnumerable<string>? supplierCodes = null
        )
        {
            var normalizedBranchCodes = NormalizeCodes(branchCodes);
            var normalizedSupplierCodes = NormalizeCodes(supplierCodes);

            var query = _context.Db.Queryable<ChinaSupplierStoreSalesDetail>()
                .Where(s => s.Date >= startDate && s.Date <= endDate);

            if (normalizedBranchCodes.Any())
            {
                query = query.Where(s => normalizedBranchCodes.Contains(s.BranchCode));
            }

            if (normalizedSupplierCodes.Any())
            {
                query = query.Where(s => normalizedSupplierCodes.Contains(s.SupplierCode));
            }

            return await query
                .GroupBy(s => new { s.Date, s.SupplierCode, s.BranchCode })
                .Select(s => new SupplierBranchAggregateRow
                {
                    Date = s.Date,
                    SupplierCode = s.SupplierCode,
                    BranchCode = s.BranchCode,
                    TotalAmount = SqlFunc.AggregateSum(s.TotalAmount),
                    TotalQuantity = SqlFunc.AggregateSum(s.TotalQuantity),
                    OrderCount = SqlFunc.AggregateSum(s.OrderCount),
                    // 旧供应商统计没有成本快照；计入缺失行，禁止与新日统计混合后伪造完整毛利。
                    StatisticRowCount = SqlFunc.AggregateCount(s.SupplierCode),
                })
                .ToListAsync();
        }

        private static List<SupplierBranchAggregateRow> AggregateSupplierBranchRows(
            IEnumerable<SupplierBranchAggregateRow> rows
        )
        {
            return rows
                .Where(row => !string.IsNullOrWhiteSpace(row.SupplierCode))
                .GroupBy(row => new
                {
                    SupplierCode = row.SupplierCode.Trim(),
                    BranchCode = row.BranchCode?.Trim() ?? string.Empty,
                })
                .Select(group => new SupplierBranchAggregateRow
                {
                    SupplierCode = group.Key.SupplierCode,
                    BranchCode = group.Key.BranchCode,
                    TotalAmount = group.Sum(row => row.TotalAmount),
                    TotalQuantity = group.Sum(row => row.TotalQuantity),
                    OrderCount = group.Sum(row => row.OrderCount ?? 0),
                    GrossProfit = group.Sum(row => row.GrossProfit ?? 0m),
                    StatisticRowCount = group.Sum(row => row.StatisticRowCount),
                    CostedRowCount = group.Sum(row => row.CostedRowCount),
                    GrossProfitRowCount = group.Sum(row => row.GrossProfitRowCount),
                })
                .ToList();
        }

        private static List<SupplierBranchAggregateRow> AggregateSupplierDateBranchRows(
            IEnumerable<SupplierBranchAggregateRow> rows
        )
        {
            return rows
                .Where(row => !string.IsNullOrWhiteSpace(row.SupplierCode))
                .GroupBy(row => new
                {
                    Date = row.Date?.Date,
                    SupplierCode = row.SupplierCode.Trim(),
                    BranchCode = row.BranchCode?.Trim() ?? string.Empty,
                })
                .Select(group => new SupplierBranchAggregateRow
                {
                    Date = group.Key.Date,
                    SupplierCode = group.Key.SupplierCode,
                    BranchCode = group.Key.BranchCode,
                    TotalAmount = group.Sum(row => row.TotalAmount),
                    TotalQuantity = group.Sum(row => row.TotalQuantity),
                    OrderCount = group.Sum(row => row.OrderCount ?? 0),
                    GrossProfit = group.Sum(row => row.GrossProfit ?? 0m),
                    StatisticRowCount = group.Sum(row => row.StatisticRowCount),
                    CostedRowCount = group.Sum(row => row.CostedRowCount),
                    GrossProfitRowCount = group.Sum(row => row.GrossProfitRowCount),
                })
                .ToList();
        }

        private static List<SupplierBranchAggregateRow> ResolveChinaSupplierBranchRows(
            IEnumerable<ProductSupplierBranchAggregateRow> rows,
            Dictionary<string, string> chinaProductMap,
            HashSet<string> targetChinaSupplierCodes
        )
        {
            var resolvedRows = rows
                .Select(row => new SupplierBranchAggregateRow
                {
                    SupplierCode =
                        ResolveChinaSupplierCodeFromStatistic(
                            row.SupplierCode,
                            row.ProductCode,
                            chinaProductMap,
                            targetChinaSupplierCodes
                        ) ?? string.Empty,
                    BranchCode = row.BranchCode,
                    TotalAmount = row.TotalAmount,
                    TotalQuantity = row.TotalQuantity,
                    OrderCount = row.OrderCount,
                    GrossProfit = row.GrossProfit,
                    StatisticRowCount = row.StatisticRowCount,
                    CostedRowCount = row.CostedRowCount,
                    GrossProfitRowCount = row.GrossProfitRowCount,
                })
                .Where(row => !string.IsNullOrWhiteSpace(row.SupplierCode));

            return AggregateSupplierBranchRows(resolvedRows);
        }

        private static List<SupplierRankAggregateRow> BuildSupplierRankAggregates(
            IEnumerable<SupplierBranchAggregateRow> rows
        )
        {
            return rows
                .Where(row => !string.IsNullOrWhiteSpace(row.SupplierCode))
                .GroupBy(row => row.SupplierCode.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => new SupplierRankAggregateRow
                {
                    SupplierCode = group.Key,
                    TotalAmount = group.Sum(row => row.TotalAmount),
                    TotalQuantity = group.Sum(row => row.TotalQuantity),
                    OrderCount = group.Sum(row => row.OrderCount ?? 0),
                    GrossProfit = group.Sum(row => row.GrossProfit ?? 0m),
                    StatisticRowCount = group.Sum(row => row.StatisticRowCount),
                    CostedRowCount = group.Sum(row => row.CostedRowCount),
                    GrossProfitRowCount = group.Sum(row => row.GrossProfitRowCount),
                    StoreCount = group
                        .Select(row => row.BranchCode)
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Select(code => code!.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count(),
                })
                .ToList();
        }

        private static Dictionary<string, SupplierCompareMetrics> BuildSupplierCompareDict(
            IEnumerable<SupplierBranchAggregateRow> rows,
            IEnumerable<string>? allowedSupplierCodes = null
        )
        {
            var allowedSet = NormalizeCodes(allowedSupplierCodes).ToHashSet(
                StringComparer.OrdinalIgnoreCase
            );

            return rows
                .Where(row =>
                    !string.IsNullOrWhiteSpace(row.SupplierCode)
                    && (!allowedSet.Any() || allowedSet.Contains(row.SupplierCode))
                )
                .GroupBy(row => row.SupplierCode.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => new SupplierCompareMetrics(
                        group.Sum(row => row.TotalAmount),
                        group.Sum(row => row.OrderCount ?? 0),
                        group.Sum(row => row.GrossProfit ?? 0m),
                        group.Sum(row => row.StatisticRowCount),
                        group.Sum(row => row.CostedRowCount),
                        group.Sum(row => row.GrossProfitRowCount)
                    ),
                    StringComparer.OrdinalIgnoreCase
                );
        }

        private static Dictionary<string, SupplierCompareMetrics> BuildSupplierBranchCompareDict(
            IEnumerable<SupplierBranchAggregateRow> rows
        )
        {
            return AggregateSupplierBranchRows(rows)
                .ToDictionary(
                    row => $"{row.BranchCode}|{row.SupplierCode}",
                    row => new SupplierCompareMetrics(
                        row.TotalAmount,
                        row.OrderCount ?? 0,
                        row.GrossProfit,
                        row.StatisticRowCount,
                        row.CostedRowCount,
                        row.GrossProfitRowCount
                    ),
                    StringComparer.OrdinalIgnoreCase
                );
        }

        private async Task<ISugarQueryable<ProductStoreDailySalesStatistic>> BuildProductReportStatisticQueryAsync(
            DateTime startDate,
            DateTime endDate,
            List<string>? branchCodes = null,
            List<string>? localSupplierCodes = null,
            List<string>? chinaProductCodes = null,
            string? productSearch = null,
            List<string>? chinaSupplierCodes = null
        )
        {
            var query = _context.Db.Queryable<ProductStoreDailySalesStatistic>()
                .Where(s => s.Date >= startDate && s.Date <= endDate);

            var normalizedBranchCodes = NormalizeCodes(branchCodes);
            if (normalizedBranchCodes.Any())
            {
                query = query.Where(s => normalizedBranchCodes.Contains(s.BranchCode));
            }

            var normalizedLocalSupplierCodes = NormalizeCodes(localSupplierCodes);
            if (normalizedLocalSupplierCodes.Any())
            {
                query = query.Where(s => normalizedLocalSupplierCodes.Contains(s.SupplierCode));
            }

            var normalizedChinaSupplierCodes = NormalizeCodes(chinaSupplierCodes);
            var normalizedChinaProductCodes = NormalizeCodes(chinaProductCodes);
            if (normalizedChinaSupplierCodes.Any() && normalizedChinaProductCodes.Any())
            {
                // 中国供应商商品明细兼容新旧日统计编码，避免排行和下方明细口径分裂。
                query = query.Where(s =>
                    normalizedChinaSupplierCodes.Contains(s.SupplierCode)
                    || (s.SupplierCode == "200" && normalizedChinaProductCodes.Contains(s.ProductCode))
                );
            }
            else if (normalizedChinaSupplierCodes.Any())
            {
                query = query.Where(s => normalizedChinaSupplierCodes.Contains(s.SupplierCode));
            }
            else if (normalizedChinaProductCodes.Any())
            {
                query = query.Where(s =>
                    s.SupplierCode == "200" && normalizedChinaProductCodes.Contains(s.ProductCode)
                );
            }

            var normalizedProductSearch = productSearch?.Trim();
            if (!string.IsNullOrWhiteSpace(normalizedProductSearch))
            {
                var searchText = normalizedProductSearch!;
                var searchProductCodes = await GetProductCodesForSearchAsync(searchText);
                query = searchProductCodes.Any()
                    ? query.Where(s =>
                        searchProductCodes.Contains(s.ProductCode)
                        || (s.Barcode != null && s.Barcode.Contains(searchText))
                    )
                    : query.Where(s => s.Barcode != null && s.Barcode.Contains(searchText));
            }

            return query;
        }

        private async Task<Dictionary<string, string>> GetLocalSupplierNameMapAsync(
            IEnumerable<string> supplierCodes
        )
        {
            var codes = NormalizeCodes(supplierCodes);
            if (!codes.Any())
                return new Dictionary<string, string>();

            var suppliers = await _context
                .Db.Queryable<HBLocalSupplier>()
                .Where(s => !s.IsDeleted && codes.Contains(s.LocalSupplierCode))
                .Select(s => new { s.LocalSupplierCode, s.Name })
                .ToListAsync();

            return suppliers
                .GroupBy(s => s.LocalSupplierCode)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().Name,
                    StringComparer.OrdinalIgnoreCase
                );
        }

        private async Task<Dictionary<string, string>> GetAustralianSupplierNameMapAsync(
            IEnumerable<string> supplierCodes
        )
        {
            var codes = NormalizeCodes(supplierCodes);
            var nameMap = await GetLocalSupplierNameMapAsync(codes);
            if (
                codes.Any(IsChinaLocalSupplierCode)
                && (
                    !nameMap.TryGetValue(CHINA_LOCAL_SUPPLIER_CODE, out var supplierName)
                    || string.IsNullOrWhiteSpace(supplierName)
                )
            )
            {
                // 200 是中国货在澳洲供应商报表中的固定汇总行，资料缺失时仍显示业务名称。
                nameMap[CHINA_LOCAL_SUPPLIER_CODE] = CHINA_LOCAL_SUPPLIER_FALLBACK_NAME;
            }

            return nameMap;
        }

        private async Task<Dictionary<string, string>> GetChinaSupplierNameMapAsync(
            IEnumerable<string> supplierCodes
        )
        {
            var codes = NormalizeCodes(supplierCodes);
            if (!codes.Any())
                return new Dictionary<string, string>();

            var suppliers = await _context
                .Db.Queryable<ChinaSupplier>()
                .Where(s => !s.IsDeleted && s.SupplierCode != null && codes.Contains(s.SupplierCode))
                .Select(s => new
                {
                    SupplierCode = s.SupplierCode ?? string.Empty,
                    SupplierName = s.SupplierName ?? s.SupplierCode ?? string.Empty,
                })
                .ToListAsync();

            return suppliers
                .Where(s => !string.IsNullOrWhiteSpace(s.SupplierCode))
                .GroupBy(s => s.SupplierCode)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().SupplierName,
                    StringComparer.OrdinalIgnoreCase
                );
        }

        private async Task<Dictionary<string, string>> GetStoreNameMapAsync(
            HashSet<string> branchCodes
        )
        {
            if (!branchCodes.Any())
                return new Dictionary<string, string>();

            var stores = await _context
                .Db.Queryable<Store>()
                .Where(s => branchCodes.Contains(s.StoreCode))
                .Select(s => new { s.StoreCode, s.StoreName })
                .ToListAsync();

            return stores.ToDictionary(
                s => s.StoreCode ?? string.Empty,
                s => s.StoreName ?? string.Empty
            );
        }

        private async Task<Dictionary<string, string>> GetActiveStoreNameMapAsync()
        {
            var stores = await _context
                .Db.Queryable<Store>()
                .Where(store =>
                    store.IsActive == true
                    && store.IsDeleted == false
                    && store.StoreCode != null
                    && store.StoreCode != ""
                )
                .Select(store => new { store.StoreCode, store.StoreName })
                .ToListAsync();

            return stores
                .Where(store => !string.IsNullOrWhiteSpace(store.StoreCode))
                .GroupBy(store => store.StoreCode!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(store => store.StoreName)
                        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                        ?? group.Key,
                    StringComparer.OrdinalIgnoreCase
                );
        }

        /// <summary>
        /// 获取紧凑销售看板。
        /// 按日期范围只查询一次「门店×商品」聚合并缓存为立方体，之后分店、国内供应商、商品三栏的交叉筛选、
        /// 关键词、排序和分页都在内存完成。旧实现按 500 个商品编码分批 IN 查询，每批 SQL 文本不同都要重新编译，
        /// 未筛选供应商时一次请求串行 52 条语句、约 3.9 秒。
        /// </summary>
        public async Task<CompactSalesBoardDto> GetCompactSalesBoardAsync(CompactSalesBoardQuery query)
        {
            ArgumentNullException.ThrowIfNull(query);
            var dateRange = query.DateRange;
            ValidateDateRange(dateRange);
            if ((dateRange.EndDate.Date - dateRange.StartDate.Date).TotalDays + 1 > CompactSalesBoardMaxDays)
                throw new ArgumentException($"紧凑销售看板日期范围不能超过 {CompactSalesBoardMaxDays} 天。");

            var pageIndex = Math.Max(1, query.PageIndex);
            var pageSize = Math.Clamp(query.PageSize, 20, CompactSalesBoardMaxPageSize);
            var branchScope = query.BranchCodes == null
                ? null
                : NormalizeCodes(query.BranchCodes).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // 与商品销量分析共用 generation lease：Clear 发生在查询期间时，结果仍返回但不得写回旧代缓存。
            var expectedGeneration = SalesDashboardCacheKeys.CaptureProductSalesAnalysisGeneration();
            // 看板只含当前区间，不带同期；完整性判定与销售明细同一套规则（BuildSalesDetailReportStatus）。
            var boardRange = new DateRangeDto { StartDate = dateRange.StartDate.Date, EndDate = dateRange.EndDate.Date };
            var stateRows = await ReadCompactSalesBoardStatusRowsAsync(boardRange);
            var status = BuildSalesDetailReportStatus(stateRows, boardRange, false);
            var board = new CompactSalesBoardDto
            {
                ProductDetails = new PagedCompactSalesBoardProductDto { PageIndex = pageIndex, PageSize = pageSize },
            };
            ApplyCompactSalesBoardStatus(board, status);

            // 统计未完成或 fail-closed 的空授权范围绝不读取数据，避免把旧统计误展示为当前结果。
            if (status.StatisticStatus != SalesStatisticRefreshStatus.Fresh || branchScope is { Count: 0 })
                return board;

            // SQL Server：在数据库里算完四栏只回几百行（API↔数据库带宽很窄）；其他数据库走下面的内存立方体。
            if (UsesCompactBoardServerAggregation(out var posmDatabase))
            {
                return await GetCompactSalesBoardOnServerAsync(
                    board, posmDatabase, boardRange, status.CacheVersion, branchScope, query, pageIndex, pageSize, expectedGeneration);
            }

            // 立方体缓存键不含授权范围与筛选：所有用户、所有点选共享同一份聚合，授权过滤在下方内存中完成。
            var cacheKey = SalesDashboardCacheKeys.CompactSalesBoardCube(boardRange, status.CacheVersion);
            CompactSalesBoardCube cube;
            if (!query.ForceRefresh
                && _cache.TryGetValue<CompactSalesBoardCube>(cacheKey, out var cachedCube)
                && cachedCube != null)
            {
                cube = cachedCube;
                board.FromCache = true;
            }
            else
            {
                var read = await GetOrBuildCompactSalesBoardCubeAsync(cacheKey, boardRange, stateRows, query.ForceRefresh, expectedGeneration);
                // 构建时在统计快照内重读了状态：期间若有新版本发布，数据与水位都以快照为准。
                ApplyCompactSalesBoardStatus(board, read.Status);
                if (read.Status.StatisticStatus != SalesStatisticRefreshStatus.Fresh)
                    return board;
                cube = read.Cube;
            }

            FillCompactSalesBoard(board, cube, branchScope, query, pageIndex, pageSize);
            await FillCompactSalesBoardBranchTotalsAsync(board.Stores, boardRange);
            return board;
        }

        private static void ApplyCompactSalesBoardStatus(CompactSalesBoardDto board, ProductReportStatisticStatusDto status)
        {
            board.StatisticStatus = status.StatisticStatus;
            board.StatisticMessage = status.StatisticMessage;
            board.StatisticUpdatedAt = status.StatisticUpdatedAt;
        }

        /// <summary>
        /// 读取看板区间内的商品日统计状态行。时间列按 UTC 标注，响应序列化带 Z，
        /// 前端才能换算成本地时间（曾因未标注被当成本地时间，截至时间比销售明细早 10 小时）。
        /// </summary>
        private async Task<List<SalesDetailReportStatusSqlRow>> ReadCompactSalesBoardStatusRowsAsync(DateRangeDto range)
        {
            var startDate = range.StartDate.Date;
            var endExclusive = range.EndDate.Date.AddDays(1);
            var states = await _context.Db.Queryable<SalesStatisticRefreshState>()
                .Where(s => s.StatisticType == SalesStatisticType.ProductStoreDaily && s.Date >= startDate && s.Date < endExclusive)
                .Select(s => new CompactSalesBoardStateRow
                {
                    Date = s.Date,
                    Status = s.Status,
                    LastAggregatedAtUtc = s.LastAggregatedAtUtc,
                    CompletedAtUtc = s.CompletedAtUtc,
                    SourceProductVersion = s.SourceProductVersion,
                })
                .ToListAsync();
            return states
                .Select(s => new SalesDetailReportStatusSqlRow
                {
                    Type = SalesStatisticType.ProductStoreDaily,
                    Date = s.Date,
                    Status = s.Status ?? string.Empty,
                    LastAggregatedAtUtc = s.LastAggregatedAtUtc is DateTime aggregated
                        ? DateTime.SpecifyKind(aggregated, DateTimeKind.Utc)
                        : null,
                    CompletedAtUtc = s.CompletedAtUtc is DateTime completed
                        ? DateTime.SpecifyKind(completed, DateTimeKind.Utc)
                        : null,
                    SourceProductVersion = s.SourceProductVersion,
                })
                .ToList();
        }

        private async Task<CompactSalesBoardCubeRead> GetOrBuildCompactSalesBoardCubeAsync(
            string cacheKey,
            DateRangeDto dateRange,
            List<SalesDetailReportStatusSqlRow> precheckStates,
            bool forceRefresh,
            long expectedGeneration
        )
        {
            // 强制刷新要绕过分片缓存，不能搭上同区间普通请求的构建结果。
            var buildKey = forceRefresh ? cacheKey + ":force" : cacheKey;
            // 同一 IMemoryCache 代表同一应用实例；快速连续点选时共享同一次构建，避免并发重复扫描统计表。
            var builds = COMPACT_SALES_BOARD_CUBE_BUILDS.GetValue(
                _cache,
                _ => new ConcurrentDictionary<string, Lazy<Task<CompactSalesBoardCubeRead>>>(StringComparer.Ordinal)
            );
            var lazyBuild = builds.GetOrAdd(
                buildKey,
                _ => new Lazy<Task<CompactSalesBoardCubeRead>>(
                    async () =>
                    {
                        var read = await BuildCompactSalesBoardCubeAsync(dateRange, precheckStates, forceRefresh, expectedGeneration);
                        // 按快照自身的水位写缓存；快照未完成（发布期间状态变为失败等）的结果不缓存。
                        if (read.Status.StatisticStatus == SalesStatisticRefreshStatus.Fresh)
                        {
                            CacheCompactSalesBoardCube(
                                SalesDashboardCacheKeys.CompactSalesBoardCube(dateRange, read.Status.CacheVersion),
                                read.Cube,
                                expectedGeneration
                            );
                        }
                        return read;
                    },
                    LazyThreadSafetyMode.ExecutionAndPublication
                )
            );

            try
            {
                return await lazyBuild.Value;
            }
            finally
            {
                // 只移除本次等待的那一项；失败的构建不会留在字典里，下一个请求会重新尝试。
                ((ICollection<KeyValuePair<string, Lazy<Task<CompactSalesBoardCubeRead>>>>)builds).Remove(
                    new KeyValuePair<string, Lazy<Task<CompactSalesBoardCubeRead>>>(buildKey, lazyBuild)
                );
            }
        }

        /// <summary>
        /// 构建「门店×商品」聚合立方体。原始聚合按自然月分片缓存（见 CompactBoardSegments），缺的分片与其状态
        /// 在同一个 SNAPSHOT 事务里读取：统计排队/重算期间读到上一版已提交的完整事实，而不是 NOLOCK 下删了一半的当天数据。
        /// 生产实测（2026-09-24）：聚合语句曾按 1 天区间编译的计划被 6 个月复用（行存储，19 秒），
        /// 商品资料用相关子查询回探统计表（774 万逻辑读，30 秒）；现分别改为 RECOMPILE 与按编码点查。
        /// </summary>
        private async Task<CompactSalesBoardCubeRead> BuildCompactSalesBoardCubeAsync(
            DateRangeDto dateRange,
            List<SalesDetailReportStatusSqlRow> precheckStates,
            bool forceRefresh,
            long expectedGeneration
        )
        {
            var startDate = dateRange.StartDate.Date;
            var endExclusive = dateRange.EndDate.Date.AddDays(1);

            // 国内供应商目录只读一次：名称用于展示，编码集合用于识别直写行（含停用和软删除，见 ChinaSupplierCodeFamily）。
            var chinaSupplierRows = await _context.Db.Queryable<ChinaSupplier>()
                .Where(s => s.SupplierCode != null && s.SupplierCode != "")
                .Select(s => new CompactSalesBoardChinaSupplierRow
                {
                    SupplierCode = s.SupplierCode ?? string.Empty,
                    SupplierName = s.SupplierName,
                    IsDeleted = s.IsDeleted,
                })
                .ToListAsync();
            var chinaSupplierCodes = chinaSupplierRows
                .Where(s => !string.IsNullOrWhiteSpace(s.SupplierCode))
                .Select(s => s.SupplierCode.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var chinaFamilyCodes = ChinaSupplierCodeFamily.BuildStatisticFilterCodes(chinaSupplierCodes);

            // 旧 200 行靠 POSM 映射还原国内供应商；直写行自带编码，不依赖映射，所以映射为空也不能直接返回空看板。
            var productSupplierMap = await GetChinaSupplierProductMapAsync();

            var (stateRows, statisticRows) = await ReadCompactSalesBoardSegmentedRowsAsync(
                dateRange,
                precheckStates,
                chinaFamilyCodes,
                forceRefresh,
                expectedGeneration
            );
            var status = BuildSalesDetailReportStatus(stateRows, dateRange, false);
            if (status.StatisticStatus != SalesStatisticRefreshStatus.Fresh)
                return new CompactSalesBoardCubeRead(CompactSalesBoardCube.Empty, status);

            // 逐行还原国内供应商：直写行用行上的编码，旧 200 行查映射。
            // 未映射的 200 商品仍不计入看板（约占国内货金额 14%），与其他国内供应商报表的口径一致。
            var resolvedRows = statisticRows
                .Where(row => !string.IsNullOrWhiteSpace(row.BranchCode) && !string.IsNullOrWhiteSpace(row.ProductCode))
                .Select(row => new
                {
                    Row = row,
                    ChinaSupplierCode = ResolveChinaSupplierCodeFromStatistic(
                        row.SupplierCode,
                        row.ProductCode,
                        productSupplierMap,
                        chinaSupplierCodes
                    )?.Trim(),
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.ChinaSupplierCode))
                .ToList();
            if (resolvedRows.Count == 0)
                return new CompactSalesBoardCubeRead(CompactSalesBoardCube.Empty, status);

            // 立方体里每个商品只挂一个国内供应商。同一商品在区间内解析出多个编码（归属中途变更，极少见）时，
            // 取最近销售日那一条；日期相同时直写行优先于映射，再按编码排序，保证结果稳定。
            var productSupplierCodes = resolvedRows
                .GroupBy(item => item.Row.ProductCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderByDescending(item => item.Row.LastDate)
                        .ThenBy(item => ChinaSupplierCodeFamily.IsLocalSupplierCode(item.Row.SupplierCode))
                        .ThenBy(item => item.ChinaSupplierCode, StringComparer.Ordinal)
                        .First()
                        .ChinaSupplierCode!,
                    StringComparer.OrdinalIgnoreCase
                );

            // 新旧两种写法的行在这里合并回「每个门店×商品一格」，下游按格计数门店动销款数依赖这个不变量。
            var cubeRows = resolvedRows
                .GroupBy(item => (
                    BranchCode: item.Row.BranchCode.Trim().ToUpperInvariant(),
                    ProductCode: item.Row.ProductCode.ToUpperInvariant()
                ))
                .Select(group => new CompactSalesBoardCubeRow
                {
                    BranchCode = group.First().Row.BranchCode,
                    ProductCode = group.First().Row.ProductCode,
                    TotalQuantity = group.Sum(item => item.Row.TotalQuantity),
                    TotalAmount = group.Sum(item => item.Row.TotalAmount),
                })
                .ToList();

            // 只取立方体里实际出现的商品资料（约 1 万个编码），不再回探统计表。
            var productInfoRows = await ReadCompactSalesBoardProductInfoRowsAsync(
                cubeRows.Select(row => row.ProductCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            );
            var productInfoMap = productInfoRows
                .Where(row => !string.IsNullOrWhiteSpace(row.ProductCode))
                .GroupBy(row => row.ProductCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            // 同一编码有多条目录记录时优先用未删除那条的名称；只剩软删除记录时保留历史名称。
            var supplierNameMap = chinaSupplierRows
                .Where(s => !string.IsNullOrWhiteSpace(s.SupplierCode))
                .GroupBy(s => s.SupplierCode.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderBy(s => s.IsDeleted)
                        .Select(s => s.SupplierName)
                        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? group.Key,
                    StringComparer.OrdinalIgnoreCase
                );
            var storeNameMap = await GetStoreNameMapAsync(
                cubeRows.Select(row => row.BranchCode).ToHashSet(StringComparer.OrdinalIgnoreCase)
            );

            var branchIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var supplierIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var productIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var branches = new List<CompactSalesBoardCubeEntity>();
            var suppliers = new List<CompactSalesBoardCubeEntity>();
            var products = new List<CompactSalesBoardCubeProduct>();
            var cells = new CompactSalesBoardCubeCell[cubeRows.Count];
            for (var i = 0; i < cubeRows.Count; i++)
            {
                var row = cubeRows[i];
                var branchCode = row.BranchCode.Trim();
                if (!branchIndex.TryGetValue(branchCode, out var b))
                {
                    b = branches.Count;
                    branchIndex[branchCode] = b;
                    var branchName = storeNameMap.GetValueOrDefault(branchCode);
                    branches.Add(new CompactSalesBoardCubeEntity(
                        branchCode,
                        string.IsNullOrWhiteSpace(branchName) ? branchCode : branchName));
                }

                if (!productIndex.TryGetValue(row.ProductCode, out var p))
                {
                    var supplierCode = productSupplierCodes[row.ProductCode];
                    if (!supplierIndex.TryGetValue(supplierCode, out var s))
                    {
                        s = suppliers.Count;
                        supplierIndex[supplierCode] = s;
                        suppliers.Add(new CompactSalesBoardCubeEntity(
                            supplierCode,
                            supplierNameMap.GetValueOrDefault(supplierCode, supplierCode)));
                    }

                    p = products.Count;
                    productIndex[row.ProductCode] = p;
                    productInfoMap.TryGetValue(row.ProductCode, out var info);
                    products.Add(new CompactSalesBoardCubeProduct(
                        row.ProductCode,
                        info?.ItemNumber,
                        info?.ProductName,
                        info?.ProductImage,
                        s));
                }

                // 统计行在 (Date, BranchCode, SupplierCode, ProductCode) 上唯一，GROUP BY 后每个门店×商品只有一格。
                cells[i] = new CompactSalesBoardCubeCell(b, p, row.TotalQuantity, row.TotalAmount);
            }

            return new CompactSalesBoardCubeRead(
                new CompactSalesBoardCube(
                    branches.ToArray(),
                    suppliers.ToArray(),
                    products.ToArray(),
                    cells,
                    branchIndex,
                    supplierIndex,
                    productIndex),
                status);
        }

        /// <summary>
        /// 区间内国内编码族的「门店×商品×原始供应商编码」聚合，调用方负责放进统计快照事务。
        /// SQL Server 走原生语句：OPTION (RECOMPILE) 按本次区间编译，避免 1 天计划被长区间复用；
        /// 编码族用 OPENJSON 参数传入，SQL 文本不随供应商目录变化。其他数据库（测试用 SQLite）沿用 SqlSugar 查询。
        /// </summary>
        private async Task<List<CompactSalesBoardCubeRow>> ReadCompactSalesBoardStatisticRowsAsync(
            DateTime startDate,
            DateTime endExclusive,
            List<string> chinaFamilyCodes
        )
        {
            if (_context.Db.CurrentConnectionConfig.DbType == DbType.SqlServer)
            {
                return await _context.Db.Ado.SqlQueryAsync<CompactSalesBoardCubeRow>(
                    """
                    SELECT s.[BranchCode], s.[ProductCode], s.[SupplierCode],
                           SUM(s.[TotalQuantity]) AS [TotalQuantity], SUM(s.[TotalAmount]) AS [TotalAmount], MAX(s.[Date]) AS [LastDate]
                    FROM [dbo].[ProductStoreDailySalesStatistic] s
                    WHERE s.[Date] >= @startDate AND s.[Date] < @endExclusive
                      AND s.[SupplierCode] IN (SELECT CONVERT(nvarchar(50), c.[value]) FROM OPENJSON(@supplierCodes) c)
                    GROUP BY s.[BranchCode], s.[ProductCode], s.[SupplierCode]
                    OPTION (RECOMPILE);
                    """,
                    new SugarParameter("@startDate", startDate, System.Data.DbType.DateTime),
                    new SugarParameter("@endExclusive", endExclusive, System.Data.DbType.DateTime),
                    new SugarParameter("@supplierCodes", System.Text.Json.JsonSerializer.Serialize(chinaFamilyCodes)) { Size = -1 }
                );
            }

            return await _context.Db.Queryable<ProductStoreDailySalesStatistic>()
                .Where(s => s.Date >= startDate && s.Date < endExclusive && chinaFamilyCodes.Contains(s.SupplierCode))
                .GroupBy(s => new { s.BranchCode, s.ProductCode, s.SupplierCode })
                .Select(s => new CompactSalesBoardCubeRow
                {
                    BranchCode = s.BranchCode,
                    ProductCode = s.ProductCode,
                    SupplierCode = s.SupplierCode,
                    TotalQuantity = SqlFunc.AggregateSum(s.TotalQuantity),
                    TotalAmount = SqlFunc.AggregateSum(s.TotalAmount),
                    LastDate = SqlFunc.AggregateMax(s.Date),
                })
                .ToListAsync();
        }

        /// <summary>
        /// 按商品编码点查商品资料。SQL Server 在独立非 MARS 连接上用一条 OPENJSON 连接走过滤索引 IX_Product_ProductCode_Active
        /// （生产 1.4 万编码服务端 0.3 秒、6 万逻辑读）；显式 NOLOCK 保持原查询行为，HQ 同步重建 Product 时不被阻塞。
        /// 其他数据库按 500 个一批 Contains；两条路径的 IsDeleted 都写成字面量，参数化会让过滤索引失效。
        /// </summary>
        private async Task<List<CompactSalesBoardProductInfoRow>> ReadCompactSalesBoardProductInfoRowsAsync(
            IReadOnlyCollection<string> productCodes
        )
        {
            if (productCodes.Count == 0)
                return new List<CompactSalesBoardProductInfoRow>();

            if (UsesCompactBoardDedicatedConnection())
            {
                // 独立非 MARS 连接：生产 MARS 连接读 8.8 千行也要约 1 秒（服务端 CPU 0.14 秒）。
                await using var connection = await OpenCompactBoardDedicatedConnectionAsync();
                await using var command = connection.CreateCommand();
                command.CommandTimeout = Math.Max(1, _context.Db.Ado.CommandTimeOut);
                command.CommandText = """
                    SELECT p.[ProductCode], p.[ItemNumber], p.[ProductName], p.[ProductImage]
                    FROM [dbo].[Product] p WITH (NOLOCK)
                    INNER JOIN OPENJSON(@productCodes) c ON CONVERT(nvarchar(50), c.[value]) = p.[ProductCode]
                    WHERE p.[IsDeleted] = 0;
                    """;
                command.Parameters.Add("@productCodes", System.Data.SqlDbType.NVarChar, -1).Value =
                    System.Text.Json.JsonSerializer.Serialize(productCodes);
                var infoRows = new List<CompactSalesBoardProductInfoRow>(productCodes.Count);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    infoRows.Add(new CompactSalesBoardProductInfoRow
                    {
                        ProductCode = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                        ItemNumber = reader.IsDBNull(1) ? null : reader.GetString(1),
                        ProductName = reader.IsDBNull(2) ? null : reader.GetString(2),
                        ProductImage = reader.IsDBNull(3) ? null : reader.GetString(3),
                    });
                }
                return infoRows;
            }

            var rows = new List<CompactSalesBoardProductInfoRow>();
            foreach (var chunk in productCodes.Chunk(500))
            {
                rows.AddRange(await _context.Db.Queryable<Product>()
                    .Where("[IsDeleted] = 0")
                    .Where(p => chunk.Contains(p.ProductCode))
                    .Select(p => new CompactSalesBoardProductInfoRow
                    {
                        ProductCode = p.ProductCode ?? string.Empty,
                        ItemNumber = p.ItemNumber,
                        ProductName = p.ProductName,
                        ProductImage = p.ProductImage,
                    })
                    .ToListAsync());
            }
            return rows;
        }

        private void CacheCompactSalesBoardCube(string cacheKey, CompactSalesBoardCube cube, long expectedGeneration)
        {
            ProductSalesAnalysisCacheWriteInterceptor?.Invoke();
            SalesDashboardCacheKeys.TryExecuteProductSalesAnalysisCacheWrite(
                cacheKey,
                expectedGeneration,
                (registrationToken, expirationToken) =>
                {
                    _cache.Set(
                        cacheKey,
                        cube,
                        BuildProductSalesAnalysisCacheOptions(
                            cacheKey,
                            COMPACT_SALES_BOARD_CUBE_CACHE_DURATION,
                            registrationToken,
                            expirationToken
                        )
                    );
                }
            );
        }

        private const int CompactSelectionNone = -1;
        private const int CompactSelectionMissing = -2;

        private static int ResolveCompactSelection(string? code, Dictionary<string, int> index)
        {
            if (string.IsNullOrWhiteSpace(code))
                return CompactSelectionNone;
            // 选中项在立方体里不存在（例如切换日期后已无销售）时匹配不到任何行，而不是被当作「全部」。
            return index.TryGetValue(code.Trim(), out var position) ? position : CompactSelectionMissing;
        }

        private static bool MatchesCompactSelection(int selection, int position) =>
            selection == CompactSelectionNone || selection == position;

        /// <summary>
        /// 交叉筛选：每栏只受「其他栏」选中项约束，选中行不会把自身所在栏收窄成 1 行；KPI 同时受三者约束。
        /// </summary>
        internal static void FillCompactSalesBoard(
            CompactSalesBoardDto board,
            CompactSalesBoardCube cube,
            HashSet<string>? branchScope,
            CompactSalesBoardQuery query,
            int pageIndex,
            int pageSize
        )
        {
            var selectedBranch = ResolveCompactSelection(query.SelectedBranchCode, cube.BranchIndex);
            var selectedSupplier = ResolveCompactSelection(query.SelectedChinaSupplierCode, cube.SupplierIndex);
            var selectedProduct = ResolveCompactSelection(query.SelectedProductCode, cube.ProductIndex);
            var branchCount = cube.Branches.Length;
            var supplierCount = cube.Suppliers.Length;
            var productCount = cube.Products.Length;

            var allowedBranches = new bool[branchCount];
            for (var b = 0; b < branchCount; b++)
                allowedBranches[b] = branchScope == null || branchScope.Contains(cube.Branches[b].Code);

            var storeAmounts = new decimal[branchCount];
            var storeQuantities = new int[branchCount];
            var storeProductCounts = new int[branchCount];
            var storeHits = new bool[branchCount];
            var supplierAmounts = new decimal[supplierCount];
            var supplierQuantities = new int[supplierCount];
            var supplierProductCounts = new int[supplierCount];
            var supplierHits = new bool[supplierCount];
            var supplierPanelProductSeen = new bool[productCount];
            var productAmounts = new decimal[productCount];
            var productQuantities = new int[productCount];
            var productHits = new bool[productCount];
            var summaryStoreSeen = new bool[branchCount];
            var summarySupplierSeen = new bool[supplierCount];
            var summaryProductSeen = new bool[productCount];
            var summary = new CompactSalesBoardSummaryDto();

            foreach (var cell in cube.Cells)
            {
                if (!allowedBranches[cell.BranchIndex])
                    continue;

                var supplier = cube.Products[cell.ProductIndex].SupplierIndex;
                var branchMatch = MatchesCompactSelection(selectedBranch, cell.BranchIndex);
                var supplierMatch = MatchesCompactSelection(selectedSupplier, supplier);
                var productMatch = MatchesCompactSelection(selectedProduct, cell.ProductIndex);
                summary.OverallAmount += cell.Amount;
                summary.OverallQuantity += cell.Quantity;

                if (supplierMatch && productMatch)
                {
                    storeAmounts[cell.BranchIndex] += cell.Amount;
                    storeQuantities[cell.BranchIndex] += cell.Quantity;
                    // 每个门店×商品在立方体中只有一格，按格计数即门店动销款数。
                    storeProductCounts[cell.BranchIndex]++;
                    storeHits[cell.BranchIndex] = true;
                }

                if (branchMatch && productMatch)
                {
                    supplierAmounts[supplier] += cell.Amount;
                    supplierQuantities[supplier] += cell.Quantity;
                    supplierHits[supplier] = true;
                    if (!supplierPanelProductSeen[cell.ProductIndex])
                    {
                        supplierPanelProductSeen[cell.ProductIndex] = true;
                        supplierProductCounts[supplier]++;
                    }
                }

                if (branchMatch && supplierMatch)
                {
                    productAmounts[cell.ProductIndex] += cell.Amount;
                    productQuantities[cell.ProductIndex] += cell.Quantity;
                    productHits[cell.ProductIndex] = true;
                }

                if (branchMatch && supplierMatch && productMatch)
                {
                    summary.TotalAmount += cell.Amount;
                    summary.TotalQuantity += cell.Quantity;
                    if (!summaryStoreSeen[cell.BranchIndex])
                    {
                        summaryStoreSeen[cell.BranchIndex] = true;
                        summary.StoreCount++;
                    }
                    if (!summarySupplierSeen[supplier])
                    {
                        summarySupplierSeen[supplier] = true;
                        summary.SupplierCount++;
                    }
                    if (!summaryProductSeen[cell.ProductIndex])
                    {
                        summaryProductSeen[cell.ProductIndex] = true;
                        summary.ProductCount++;
                    }
                }
            }

            board.Summary = summary;
            board.Stores = Enumerable.Range(0, branchCount)
                .Where(b => storeHits[b])
                .Select(b => new CompactSalesBoardStoreDto
                {
                    BranchCode = cube.Branches[b].Code,
                    BranchName = cube.Branches[b].Name,
                    TotalAmount = storeAmounts[b],
                    TotalQuantity = storeQuantities[b],
                    DomesticSupplierAmount = storeAmounts[b],
                    ProductCount = storeProductCounts[b],
                })
                .OrderByDescending(row => row.TotalAmount)
                .ThenBy(row => row.BranchCode, StringComparer.OrdinalIgnoreCase)
                .ToList();
            board.ChinaSuppliers = Enumerable.Range(0, supplierCount)
                .Where(s => supplierHits[s])
                .Select(s => new CompactSalesBoardChinaSupplierDto
                {
                    SupplierCode = cube.Suppliers[s].Code,
                    SupplierName = cube.Suppliers[s].Name,
                    TotalAmount = supplierAmounts[s],
                    TotalQuantity = supplierQuantities[s],
                    ProductCount = supplierProductCounts[s],
                })
                .OrderByDescending(row => row.TotalAmount)
                .ThenBy(row => row.SupplierCode, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 关键词只过滤商品栏：空格分隔的每个词都必须命中货号、名称或商品编码之一（不区分大小写）。
            var keywordTerms = (query.Keyword ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Take(8)
                .ToArray();
            var productPositions = Enumerable.Range(0, productCount)
                .Where(p => productHits[p])
                .Where(p => keywordTerms.Length == 0 || keywordTerms.All(term =>
                {
                    var product = cube.Products[p];
                    return (product.ItemNumber?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (product.ProductName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                        || product.ProductCode.Contains(term, StringComparison.OrdinalIgnoreCase);
                }))
                .ToList();

            var sortField = NormalizeCompactSortField(query.SortField);
            var descending = string.IsNullOrWhiteSpace(query.SortOrder)
                ? sortField != CompactSalesBoardQuery.SortByItemNumber
                : !string.Equals(query.SortOrder.Trim(), "asc", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(query.SortOrder.Trim(), "ascend", StringComparison.OrdinalIgnoreCase);
            decimal UnitPrice(int p) => productQuantities[p] > 0 ? productAmounts[p] / productQuantities[p] : 0m;
            int CompareByField(int left, int right) => sortField switch
            {
                CompactSalesBoardQuery.SortByQuantity => productQuantities[left].CompareTo(productQuantities[right]),
                CompactSalesBoardQuery.SortByUnitPrice => UnitPrice(left).CompareTo(UnitPrice(right)),
                CompactSalesBoardQuery.SortByItemNumber => StringComparer.OrdinalIgnoreCase.Compare(
                    cube.Products[left].ItemNumber ?? cube.Products[left].ProductCode,
                    cube.Products[right].ItemNumber ?? cube.Products[right].ProductCode),
                _ => productAmounts[left].CompareTo(productAmounts[right]),
            };
            // 服务端对全部结果排序后再分页；同值按商品编码升序，保证翻页无重复、无遗漏。
            productPositions.Sort((left, right) =>
            {
                var compared = CompareByField(left, right);
                if (compared != 0)
                    return descending ? -compared : compared;
                return StringComparer.OrdinalIgnoreCase.Compare(cube.Products[left].ProductCode, cube.Products[right].ProductCode);
            });

            var total = productPositions.Count;
            var offset = (long)(pageIndex - 1) * pageSize;
            var scopeAmount = 0m;
            for (var p = 0; p < productCount; p++)
            {
                if (productHits[p])
                    scopeAmount += productAmounts[p];
            }
            board.ProductDetails = new PagedCompactSalesBoardProductDto
            {
                Total = total,
                PageIndex = pageIndex,
                PageSize = pageSize,
                ScopeAmount = scopeAmount,
                Data = offset >= total
                    ? new List<CompactSalesBoardProductDto>()
                    : productPositions
                        .Skip((int)offset)
                        .Take(pageSize)
                        .Select(p =>
                        {
                            var product = cube.Products[p];
                            var supplier = cube.Suppliers[product.SupplierIndex];
                            return new CompactSalesBoardProductDto
                            {
                                ProductCode = product.ProductCode,
                                ItemNumber = product.ItemNumber,
                                ProductName = product.ProductName,
                                ProductImage = product.ProductImage,
                                ChinaSupplierCode = supplier.Code,
                                ChinaSupplierName = supplier.Name,
                                TotalQuantity = productQuantities[p],
                                TotalAmount = productAmounts[p],
                                UnitPrice = Math.Round(UnitPrice(p), 4),
                            };
                        })
                        .ToList(),
            };
        }

        private static string NormalizeCompactSortField(string? sortField)
        {
            var normalized = sortField?.Trim();
            foreach (var candidate in new[]
            {
                CompactSalesBoardQuery.SortByAmount,
                CompactSalesBoardQuery.SortByQuantity,
                CompactSalesBoardQuery.SortByUnitPrice,
                CompactSalesBoardQuery.SortByItemNumber,
            })
            {
                if (string.Equals(normalized, candidate, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            return CompactSalesBoardQuery.SortByAmount;
        }

        public async Task<BestSellerResponseDto> GetBestSellersAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes = null,
            int pageIndex = 1,
            int pageSize = 50
        )
        {
            try
            {
                _logger.LogInformation(
                    "[BestSellers] Getting best sellers: DateRange={StartDate}~{EndDate}, BranchCodes={BranchCodes}, Page={Page}, PageSize={PageSize}",
                    dateRange.StartDate, dateRange.EndDate,
                    branchCodes != null ? string.Join(",", branchCodes) : "All",
                    pageIndex, pageSize
                );

                if (branchCodes != null && !branchCodes.Any())
                {
                    return new BestSellerResponseDto
                    {
                        Products = new List<BestSellerProductDto>(),
                        Total = 0,
                        PageIndex = pageIndex,
                        PageSize = pageSize,
                    };
                }

                var startDate = dateRange.StartDate.Date;
                var endDate = dateRange.EndDate.Date;
                // 状态必须先于缓存读取，避免重算或对账失败后继续展示旧的 Fresh 排名。
                var statisticStatus = await GetProductStatisticStatusAsync(startDate, endDate);
                var cacheKey = branchCodes == null
                    && statisticStatus.Status == SalesStatisticRefreshStatus.Fresh
                    ? SalesDashboardCacheKeys.BestSellers(
                        dateRange,
                        pageIndex,
                        pageSize,
                        statisticStatus.CacheVersion
                    )
                    : null;
                if (
                    cacheKey != null
                    && _cache.TryGetValue<BestSellerResponseDto>(cacheKey, out var cachedResult)
                    && cachedResult != null
                )
                {
                    _logger.LogInformation("从缓存获取热销商品全平台排名: {CacheKey}", cacheKey);
                    return cachedResult;
                }

                try
                {
                    var statisticResult = await GetBestSellersFromStatisticsAsync(
                        dateRange,
                        branchCodes,
                        pageIndex,
                        pageSize,
                        statisticStatus
                    );
                    if (ShouldCacheBestSellerResult(statisticResult))
                    {
                        CacheBestSellerResult(cacheKey, statisticResult);
                    }
                    return statisticResult;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[BestSellers] Statistic query failed, POSM fallback disabled");
                    return CreateEmptyBestSellerResponse(
                        pageIndex,
                        pageSize,
                        SalesStatisticRefreshStatus.Failed,
                        $"商品统计查询失败，请先修复或重新生成统计表: {ex.Message}"
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BestSellers] GetBestSellersAsync failed");
                throw;
            }
        }

        private static bool ShouldCacheBestSellerResult(BestSellerResponseDto result)
        {
            // 只缓存已生成且有数据的统计结果，避免短暂空表把热销页空结果固定 30 分钟。
            return result.StatisticStatus == SalesStatisticRefreshStatus.Fresh
                && result.Total > 0
                && result.Products.Any();
        }

        private static BestSellerResponseDto CreateEmptyBestSellerResponse(
            int pageIndex,
            int pageSize,
            string? statisticStatus,
            string? statisticMessage
        )
        {
            return new BestSellerResponseDto
            {
                Products = new List<BestSellerProductDto>(),
                Total = 0,
                PageIndex = pageIndex,
                PageSize = pageSize,
                StatisticStatus = statisticStatus,
                StatisticMessage = statisticMessage,
            };
        }

        private void CacheBestSellerResult(string? cacheKey, BestSellerResponseDto result)
        {
            if (cacheKey == null)
                return;

            var cacheOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(BEST_SELLERS_CACHE_DURATION)
                .SetSlidingExpiration(TimeSpan.FromMinutes(5));

            _cache.Set(cacheKey, result, cacheOptions);
            _logger.LogInformation(
                "热销商品全平台排名已缓存: {CacheKey}, 过期时间: {Expiration}",
                cacheKey,
                DateTime.Now.Add(BEST_SELLERS_CACHE_DURATION)
            );
        }

        private async Task<BestSellerResponseDto> GetBestSellersFromStatisticsAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes,
            int pageIndex,
            int pageSize,
            ProductStatisticStatusSnapshot statisticStatus
        )
        {
            var startDate = dateRange.StartDate.Date;
            var endDate = dateRange.EndDate.Date;
            if (
                statisticStatus.Status != SalesStatisticRefreshStatus.Fresh
                && statisticStatus.Status != SalesStatisticRefreshStatus.Failed
            )
            {
                // 非 Fresh 说明日期范围不完整或水位过期，不能把部分统计当完整排名展示。
                return CreateEmptyBestSellerResponse(
                    pageIndex,
                    pageSize,
                    statisticStatus.Status,
                    statisticStatus.Message ?? "商品统计未完整生成，请先生成商品统计。"
                );
            }

            if (statisticStatus.Status == SalesStatisticRefreshStatus.Failed)
            {
                // 对账失败时保留已生成排名供排查和使用，但不回退 POSM，也不进入缓存。
                _logger.LogWarning(
                    "[BestSellers] Product statistic status failed, returning persisted rows without cache: {Message}",
                    statisticStatus.Message
                );
            }

            // 热销榜只看国内货。日统计里国内货既可能是旧写法的 200，也可能是直写的国内供应商编码，
            // 只认 200 会让直写行整行掉出榜单；主查询和下面两个补充查询必须用同一份编码族。
            var chinaFamilyCodes = ChinaSupplierCodeFamily.BuildStatisticFilterCodes(
                await ChinaSupplierCodeFamily.LoadChinaSupplierCodesAsync(_context.Db)
            );
            var query = _context.Db.Queryable<ProductStoreDailySalesStatistic>()
                .Where(s =>
                    s.Date >= startDate
                    && s.Date <= endDate
                    && chinaFamilyCodes.Contains(s.SupplierCode)
                );

            if (branchCodes != null && branchCodes.Any())
            {
                query = query.Where(s => branchCodes.Contains(s.BranchCode));
            }

            // 数据库分页：热销页优先读取商品分店每日统计表，避免实时扫 POSM 明细。
            var groupedQuery = query
                .GroupBy(s => s.ProductCode)
                .Select(s => new BestSellerAggregateRow
                {
                    ProductCode = s.ProductCode,
                    TotalQuantity = SqlFunc.AggregateSum(s.TotalQuantity),
                    TotalSalesAmount = SqlFunc.AggregateSum(s.TotalAmount),
                    TotalCost = SqlFunc.AggregateSum(s.TotalCost),
                    GrossProfit = SqlFunc.AggregateSum(s.GrossProfit),
                });

            var total = await groupedQuery.Clone().CountAsync();
            if (total <= 0)
            {
                return CreateEmptyBestSellerResponse(
                    pageIndex,
                    pageSize,
                    statisticStatus.Status,
                    statisticStatus.Message
                );
            }

            var pagedData = await query.Clone()
                .GroupBy(s => s.ProductCode)
                .OrderBy(s => SqlFunc.AggregateSum(s.TotalQuantity), OrderByType.Desc)
                .Select(s => new BestSellerAggregateRow
                {
                    ProductCode = s.ProductCode,
                    TotalQuantity = SqlFunc.AggregateSum(s.TotalQuantity),
                    TotalSalesAmount = SqlFunc.AggregateSum(s.TotalAmount),
                    TotalCost = SqlFunc.AggregateSum(s.TotalCost),
                    GrossProfit = SqlFunc.AggregateSum(s.GrossProfit),
                })
                .Skip((pageIndex - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return await BuildBestSellerResponseAsync(
                pagedData,
                total,
                pageIndex,
                pageSize,
                statisticStatus.Status,
                statisticStatus.Message,
                productCodes => GetBranchSalesFromStatisticsAsync(startDate, endDate, branchCodes, productCodes, chinaFamilyCodes),
                productCodes => GetBestSellerInfoFromStatisticsAsync(startDate, endDate, branchCodes, productCodes, chinaFamilyCodes)
            );
        }

        private async Task<BestSellerResponseDto> BuildBestSellerResponseAsync(
            List<BestSellerAggregateRow> pagedData,
            int total,
            int pageIndex,
            int pageSize,
            string? statisticStatus,
            string? statisticMessage,
            Func<List<string>, Task<List<BestSellerBranchAggregateRow>>> loadBranchSales,
            Func<List<string>, Task<Dictionary<string, (string? Barcode, string? ProductName)>>> loadStatisticInfo
        )
        {
            if (!pagedData.Any())
            {
                return new BestSellerResponseDto
                {
                    Products = new List<BestSellerProductDto>(),
                    Total = 0,
                    PageIndex = pageIndex,
                    PageSize = pageSize,
                    StatisticStatus = statisticStatus,
                    StatisticMessage = statisticMessage,
                };
            }

            var productCodes = pagedData
                .Select(x => x.ProductCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct()
                .ToList();
            var productDict = await GetBestSellerProductInfoMapAsync(productCodes);
            var statisticInfo = await loadStatisticInfo(productCodes);
            var branchSalesData = await loadBranchSales(productCodes);
            var branchCodesInPage = branchSalesData
                .Select(x => x.BranchCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct()
                .ToList();
            var storeNameMap = branchCodesInPage.Any()
                ? await GetStoreNameMapAsync(branchCodesInPage.ToHashSet())
                : new Dictionary<string, string>();
            var branchSalesMap = branchSalesData
                .Where(x => !string.IsNullOrWhiteSpace(x.ProductCode) && !string.IsNullOrWhiteSpace(x.BranchCode))
                .GroupBy(x => x.ProductCode)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderByDescending(x => x.Quantity)
                        .ThenBy(x => x.BranchCode)
                        .Select(x => new BestSellerBranchSaleDto
                        {
                            BranchCode = x.BranchCode,
                            BranchName = storeNameMap.TryGetValue(x.BranchCode, out var storeName) ? storeName : x.BranchCode,
                            Quantity = x.Quantity,
                            SalesAmount = x.SalesAmount,
                            TotalCost = x.TotalCost,
                            GrossProfit = x.GrossProfit,
                            GrossMarginRate = x.SalesAmount > 0m && x.GrossProfit.HasValue
                        ? x.GrossProfit.Value / x.SalesAmount
                        : null,
                            CostSource = x.CostSource,
                        })
                        .ToList()
                );

            var products = pagedData.Select((item, index) =>
            {
                productDict.TryGetValue(item.ProductCode, out var productInfo);
                statisticInfo.TryGetValue(item.ProductCode, out var statInfo);
                var branchSales = branchSalesMap.TryGetValue(item.ProductCode, out var rows)
                    ? rows
                    : new List<BestSellerBranchSaleDto>();
                return new BestSellerProductDto
                {
                    ProductCode = item.ProductCode,
                    ItemNumber = productInfo?.ItemNumber,
                    Barcode = !string.IsNullOrWhiteSpace(productInfo?.Barcode)
                        ? productInfo.Barcode
                        : statInfo.Barcode,
                    ProductImage = productInfo?.ProductImage,
                    ProductName = !string.IsNullOrWhiteSpace(productInfo?.ProductName)
                        ? productInfo.ProductName
                        : statInfo.ProductName,
                    Quantity = item.TotalQuantity,
                    SalesAmount = item.TotalSalesAmount,
                    TotalCost = item.TotalCost,
                    GrossProfit = item.GrossProfit,
                    GrossMarginRate = item.TotalSalesAmount > 0m && item.GrossProfit.HasValue
                        ? item.GrossProfit.Value / item.TotalSalesAmount
                        : null,
                    CostSource = ResolveCostSource(branchSales),
                    Rank = (pageIndex - 1) * pageSize + index + 1,
                    IsActive = productInfo?.IsActive,
                    MinOrderQuantity = productInfo?.MinOrderQuantity,
                    BranchSalesCount = branchSales.Count,
                    BranchSales = branchSales,
                    StatisticStatus = statisticStatus,
                };
            }).ToList();

            return new BestSellerResponseDto
            {
                Products = products,
                Total = total,
                PageIndex = pageIndex,
                PageSize = pageSize,
                StatisticStatus = statisticStatus,
                StatisticMessage = statisticMessage,
            };
        }

        private async Task<Dictionary<string, ProductInfo>> GetBestSellerProductInfoMapAsync(List<string> productCodes)
        {
            if (!productCodes.Any())
                return new Dictionary<string, ProductInfo>();

            // 仓库库存表状态：热销展示的上下架和起订量必须以 WarehouseProduct 为准。
            var productsInfo = await _context.Db.Queryable<Product>()
                .LeftJoin<WarehouseProduct>((p, wp) => p.ProductCode == wp.ProductCode && wp.IsDeleted == false)
                .Where(p => p.ProductCode != null && productCodes.Contains(p.ProductCode))
                .Where(p => p.IsDeleted == false)
                .Select((p, wp) => new ProductInfo
                {
                    ProductCode = p.ProductCode ?? string.Empty,
                    ItemNumber = p.ItemNumber,
                    Barcode = p.Barcode,
                    ProductImage = p.ProductImage,
                    ProductName = p.ProductName,
                    IsActive = wp.IsActive,
                    MinOrderQuantity = wp.MinOrderQuantity,
                })
                .ToListAsync();

            return productsInfo
                .Where(x => !string.IsNullOrWhiteSpace(x.ProductCode))
                .GroupBy(x => x.ProductCode)
                .ToDictionary(x => x.Key, x => x.First());
        }

        private async Task<List<BestSellerBranchAggregateRow>> GetBranchSalesFromStatisticsAsync(
            DateTime startDate,
            DateTime endDate,
            List<string>? branchCodes,
            List<string> productCodes,
            List<string> chinaFamilyCodes
        )
        {
            if (!productCodes.Any())
                return new List<BestSellerBranchAggregateRow>();

            var query = _context.Db.Queryable<ProductStoreDailySalesStatistic>()
                .Where(s =>
                    s.Date >= startDate
                    && s.Date <= endDate
                    && chinaFamilyCodes.Contains(s.SupplierCode)
                    && productCodes.Contains(s.ProductCode)
                );
            if (branchCodes != null && branchCodes.Any())
            {
                query = query.Where(s => branchCodes.Contains(s.BranchCode));
            }

            var rows = await query
                .GroupBy(s => new { s.ProductCode, s.BranchCode, s.CostSource })
                .Select(s => new BestSellerBranchAggregateRow
                {
                    ProductCode = s.ProductCode,
                    BranchCode = s.BranchCode,
                    Quantity = SqlFunc.AggregateSum(s.TotalQuantity),
                    SalesAmount = SqlFunc.AggregateSum(s.TotalAmount),
                    TotalCost = SqlFunc.AggregateSum(s.TotalCost),
                    GrossProfit = SqlFunc.AggregateSum(s.GrossProfit),
                    CostSource = s.CostSource,
                })
                .ToListAsync();

            // 分店销量先按成本来源下推聚合，再在内存合并少量 CostSource 分组以保留 Mixed 语义。
            return rows
                .GroupBy(s => new { s.ProductCode, s.BranchCode })
                .Select(group => new BestSellerBranchAggregateRow
                {
                    ProductCode = group.Key.ProductCode,
                    BranchCode = group.Key.BranchCode,
                    Quantity = group.Sum(x => x.Quantity),
                    SalesAmount = group.Sum(x => x.SalesAmount),
                    TotalCost = group.Any(x => x.TotalCost.HasValue) ? group.Sum(x => x.TotalCost ?? 0m) : null,
                    GrossProfit = group.Any(x => x.GrossProfit.HasValue) ? group.Sum(x => x.GrossProfit ?? 0m) : null,
                    CostSource = ResolveCostSource(group.Select(x => x.CostSource)),
                })
                .ToList();
        }

        private async Task<Dictionary<string, (string? Barcode, string? ProductName)>> GetBestSellerInfoFromStatisticsAsync(
            DateTime startDate,
            DateTime endDate,
            List<string>? branchCodes,
            List<string> productCodes,
            List<string> chinaFamilyCodes
        )
        {
            if (!productCodes.Any())
                return new Dictionary<string, (string? Barcode, string? ProductName)>();

            var query = _context.Db.Queryable<ProductStoreDailySalesStatistic>()
                .Where(s =>
                    s.Date >= startDate
                    && s.Date <= endDate
                    && chinaFamilyCodes.Contains(s.SupplierCode)
                    && productCodes.Contains(s.ProductCode)
                );
            if (branchCodes != null && branchCodes.Any())
            {
                query = query.Where(s => branchCodes.Contains(s.BranchCode));
            }

            var rows = await query
                .Select(s => new { s.ProductCode, s.Barcode, s.ProductName })
                .ToListAsync();

            return rows
                .Where(x => !string.IsNullOrWhiteSpace(x.ProductCode))
                .GroupBy(x => x.ProductCode)
                .ToDictionary(
                    x => x.Key,
                    x => (
                        x.Select(row => row.Barcode).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                        x.Select(row => row.ProductName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                    )
                );
        }

        /// <summary>
        /// 只读获取商品日分店统计完整性。调用此方法不会触发统计聚合。
        /// </summary>
        public async Task<ProductReportStatisticStatusDto> GetProductReportStatisticStatusAsync(
            DateRangeDto dateRange
        )
        {
            ValidateDateRange(dateRange);
            if (_useSupplierRollups)
                return await GetSupplierBackedProductReportStatusAsync(dateRange);
            var currentStatus = await GetProductStatisticStatusAsync(
                dateRange.StartDate.Date,
                dateRange.EndDate.Date
            );
            var status = currentStatus;
            if (dateRange.CompareStartDate.HasValue && dateRange.CompareEndDate.HasValue)
            {
                var compareStatus = await GetProductStatisticStatusAsync(
                    dateRange.CompareStartDate.Value.Date,
                    dateRange.CompareEndDate.Value.Date
                );
                status = MergeProductStatisticStatus(currentStatus, compareStatus);
            }
            return new ProductReportStatisticStatusDto
            {
                StatisticStatus = status.Status,
                StatisticMessage = GetPublicProductStatisticMessage(status.Status),
                StatisticUpdatedAt = status.UpdatedAt,
                CacheVersion = status.CacheVersion,
            };
        }

        private static string? GetPublicProductStatisticMessage(string status)
        {
            if (string.Equals(status, SalesStatisticRefreshStatus.Fresh, StringComparison.OrdinalIgnoreCase))
                return null;
            if (string.Equals(status, SalesStatisticRefreshStatus.Failed, StringComparison.OrdinalIgnoreCase))
                return "商品统计处理失败，请稍后重试。";
            if (string.Equals(status, SalesStatisticRefreshStatus.Stale, StringComparison.OrdinalIgnoreCase))
                return "商品统计等待更新。";
            return "商品统计尚未准备完成。";
        }

        private static ProductStatisticStatusSnapshot MergeProductStatisticStatus(
            ProductStatisticStatusSnapshot currentStatus,
            ProductStatisticStatusSnapshot compareStatus
        )
        {
            var cacheVersionSource = $"current:{currentStatus.CacheVersion}|compare:{compareStatus.CacheVersion}";
            var cacheVersion = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(cacheVersionSource))
            );
            // 同期水位只要有一段不可解释，就不能声称整体水位可解释；两段都有时取最旧值。
            DateTime? updatedAt = currentStatus.UpdatedAt.HasValue && compareStatus.UpdatedAt.HasValue
                ? new[] { currentStatus.UpdatedAt.Value, compareStatus.UpdatedAt.Value }.Min()
                : null;

            if (
                string.Equals(currentStatus.Status, SalesStatisticRefreshStatus.Failed, StringComparison.OrdinalIgnoreCase)
                || string.Equals(compareStatus.Status, SalesStatisticRefreshStatus.Failed, StringComparison.OrdinalIgnoreCase)
            )
            {
                return new ProductStatisticStatusSnapshot(
                    SalesStatisticRefreshStatus.Failed,
                    "商品统计处理失败，请稍后重试。",
                    cacheVersion,
                    updatedAt
                );
            }

            if (
                string.Equals(currentStatus.Status, SalesStatisticRefreshStatus.Stale, StringComparison.OrdinalIgnoreCase)
                || string.Equals(compareStatus.Status, SalesStatisticRefreshStatus.Stale, StringComparison.OrdinalIgnoreCase)
            )
            {
                return new ProductStatisticStatusSnapshot(
                    SalesStatisticRefreshStatus.Stale,
                    "商品统计等待更新。",
                    cacheVersion,
                    updatedAt
                );
            }

            if (!IsProductStatisticSnapshotFresh(currentStatus) || !IsProductStatisticSnapshotFresh(compareStatus))
            {
                return new ProductStatisticStatusSnapshot(
                    SalesStatisticRefreshStatus.Pending,
                    "商品统计尚未准备完成。",
                    cacheVersion,
                    updatedAt
                );
            }

            return new ProductStatisticStatusSnapshot(
                SalesStatisticRefreshStatus.Fresh,
                null,
                cacheVersion,
                updatedAt
            );
        }

        private static bool IsProductStatisticSnapshotFresh(ProductStatisticStatusSnapshot status)
        {
            return string.Equals(
                status.Status,
                SalesStatisticRefreshStatus.Fresh,
                StringComparison.OrdinalIgnoreCase
            );
        }

        private static bool IsProductStatisticFresh(ProductReportStatisticStatusDto status)
        {
            return string.Equals(
                status.StatisticStatus,
                SalesStatisticRefreshStatus.Fresh,
                StringComparison.OrdinalIgnoreCase
            );
        }

        private async Task<ProductStatisticStatusSnapshot> GetProductStatisticStatusAsync(
            DateTime startDate,
            DateTime endDate
        )
        {
            var states = await _context.Db.Queryable<SalesStatisticRefreshState>()
                .Where(s =>
                    s.StatisticType == SalesStatisticType.ProductStoreDaily
                    && s.Date >= startDate
                    && s.Date <= endDate
                )
                .ToListAsync();

            if (!states.Any())
            {
                return new ProductStatisticStatusSnapshot(
                    SalesStatisticRefreshStatus.Pending,
                    "商品统计尚未回填完整。",
                    "none",
                    null
                );
            }

            var cacheVersionSource = string.Join(
                "|",
                states
                    .OrderBy(state => state.Date)
                    .Select(state =>
                        // 成本回填复用持久化 SourceProductVersion；即使统计时间未变化，成本变化也必须淘汰旧缓存。
                        $"{state.Date:yyyyMMdd}:{state.Status}:{state.LastAggregatedAtUtc?.Ticks ?? 0}:{state.CompletedAtUtc?.Ticks ?? 0}:{state.SourceProductVersion}"
                    )
            );
            var cacheVersion = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(cacheVersionSource))
            );
            var statisticUpdatedAt = states
                .Where(state => state.CompletedAtUtc.HasValue || state.LastAggregatedAtUtc.HasValue)
                .Select(state => state.CompletedAtUtc ?? state.LastAggregatedAtUtc)
                .DefaultIfEmpty()
                .Max();

            if (states.Any(s => s.Status == SalesStatisticRefreshStatus.Failed))
            {
                return new ProductStatisticStatusSnapshot(
                    SalesStatisticRefreshStatus.Failed,
                    states.FirstOrDefault(s => s.Status == SalesStatisticRefreshStatus.Failed)?.ErrorMessage,
                    cacheVersion,
                    statisticUpdatedAt
                );
            }

            if (states.Any(s => s.Status == SalesStatisticRefreshStatus.Queued || s.Status == SalesStatisticRefreshStatus.Running))
            {
                return new ProductStatisticStatusSnapshot(
                    SalesStatisticRefreshStatus.Pending,
                    "商品统计正在重算中。",
                    cacheVersion,
                    statisticUpdatedAt
                );
            }

            if (states.Any(s => s.Status == SalesStatisticRefreshStatus.Stale))
            {
                return new ProductStatisticStatusSnapshot(
                    SalesStatisticRefreshStatus.Stale,
                    "商品统计正在等待延迟上传数据补算。",
                    cacheVersion,
                    statisticUpdatedAt
                );
            }

            if (states.Any(s => s.Status == SalesStatisticRefreshStatus.Pending))
            {
                return new ProductStatisticStatusSnapshot(
                    SalesStatisticRefreshStatus.Pending,
                    "商品统计正在生成中。",
                    cacheVersion,
                    statisticUpdatedAt
                );
            }

            var expectedDays = (int)(endDate - startDate).TotalDays + 1;
            if (states.Select(s => s.Date.Date).Distinct().Count() < expectedDays)
            {
                return new ProductStatisticStatusSnapshot(
                    SalesStatisticRefreshStatus.Pending,
                    "日期范围内仍有商品统计未生成。",
                    cacheVersion,
                    statisticUpdatedAt
                );
            }

            // Best Sellers 请求链路只读统计状态表；源数据水位由统计任务写入状态，避免页面触发 POSM 重查询。

            // 只有日期覆盖完整且所有状态都明确为 Fresh 时才返回 Fresh。
            // 未知枚举或 Fresh 与未知状态混合必须按 Pending 处理，避免误判为可用指标。
            if (
                !states.All(s =>
                    string.Equals(
                        s.Status,
                        SalesStatisticRefreshStatus.Fresh,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
            )
            {
                return new ProductStatisticStatusSnapshot(
                    SalesStatisticRefreshStatus.Pending,
                    "商品统计状态存在未知或混合状态，暂不返回指标。",
                    cacheVersion,
                    statisticUpdatedAt
                );
            }

            return new ProductStatisticStatusSnapshot(
                SalesStatisticRefreshStatus.Fresh,
                null,
                cacheVersion,
                statisticUpdatedAt
            );
        }

        public async Task<ProductSalesAnalysisResponse<ProductSalesAnalysisOptionsDto>> GetProductSalesAnalysisOptionsAsync(
            ProductSalesAnalysisFilterDto filter,
            List<string>? branchCodes
        )
        {
            var (startDate, endDate) = ValidateProductSalesAnalysisDateRange(
                filter.StartDate,
                filter.EndDate,
                DateTime.UtcNow
            );
            var expectedGeneration = SalesDashboardCacheKeys.CaptureProductSalesAnalysisGeneration();
            var status = await GetProductStatisticStatusAsync(startDate, endDate);
            var emptyOptions = new ProductSalesAnalysisOptionsDto();
            if (status.Status != SalesStatisticRefreshStatus.Fresh || branchCodes is { Count: 0 })
                return CreateProductSalesAnalysisResponse(status, emptyOptions);

            // 选项只受日期、授权分店与统计水位影响，不受关键字或当前供应商过滤影响。
            var cacheKey = SalesDashboardCacheKeys.ProductSalesAnalysisOptions(
                startDate,
                endDate,
                branchCodes,
                status.CacheVersion
            );
            if (
                _cache.TryGetValue<ProductSalesAnalysisResponse<ProductSalesAnalysisOptionsDto>>(
                    cacheKey,
                    out var cached
                )
                && cached != null
            )
            {
                return cached;
            }

            var options = await QueryProductSalesAnalysisOptionsAsync(startDate, endDate, branchCodes);
            var response = CreateProductSalesAnalysisResponse(status, options);
            CacheProductSalesAnalysis(cacheKey, response, expectedGeneration);
            return response;
        }

        private async Task<ProductSalesAnalysisOptionsDto> QueryProductSalesAnalysisOptionsAsync(
            DateTime startDate,
            DateTime endDate,
            List<string>? branchCodes
        )
        {
            // 期间实际出现的供应商与商品编码（仅在授权分店范围），不回扫 POSM 销售明细。
            var statisticQuery = _context
                .Db.Queryable<ProductStoreDailySalesStatistic>()
                .Where(s =>
                    s.Date >= startDate
                    && s.Date <= endDate
                    && s.SupplierCode != ""
                );
            if (branchCodes is { Count: > 0 } allowedBranches)
            {
                statisticQuery = statisticQuery.Where(s => allowedBranches.Contains(s.BranchCode));
            }

            // 直接去重到 SQL 端，避免 366 天每日/分店重复行全量传回内存。
            var periodRows = await statisticQuery
                .Select(s => new { s.SupplierCode, s.ProductCode })
                .Distinct()
                .ToListAsync();

            var periodSupplierCodes = periodRows
                .Select(row => row.SupplierCode.Trim())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 国内供应商目录（含软删除/停用记录）用于识别直写国内代码；主数据只补名称。
            var chinaCatalog = await GetProductSalesChinaCatalogAsync();
            var chinaCodeSet = chinaCatalog.Codes;
            var chinaNameMap = chinaCatalog.Names;

            var australianNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var localSuppliers = await _context
                .Db.Queryable<HBLocalSupplier>()
                .Where(s => s.LocalSupplierCode != "")
                .Select(s => new { s.LocalSupplierCode, s.Name })
                .ToListAsync();
            foreach (var row in localSuppliers)
            {
                var code = row.LocalSupplierCode.Trim();
                if (string.IsNullOrWhiteSpace(code))
                    continue;
                if (!australianNameMap.ContainsKey(code))
                    australianNameMap[code] = string.IsNullOrWhiteSpace(row.Name) ? code : row.Name.Trim();
            }

            // 旧 200 通过 POSM 商品映射还原国内供应商；缺失映射不生成国内选项。
            var legacyProductCodes = periodRows
                .Where(row =>
                    IsChinaLocalSupplierCode(row.SupplierCode)
                    && !string.IsNullOrWhiteSpace(row.ProductCode)
                )
                .Select(row => row.ProductCode.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var legacyChinaMap = legacyProductCodes.Count > 0
                ? await GetChinaSupplierProductMapForProductsAsync(legacyProductCodes)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var chinaCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var australianCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var supplierCode in periodSupplierCodes)
            {
                if (IsChinaLocalSupplierCode(supplierCode))
                    continue; // 旧 200 由商品映射处理
                if (chinaCodeSet.Contains(supplierCode))
                    chinaCodes.Add(supplierCode);
                else
                    australianCodes.Add(supplierCode);
            }

            foreach (var chinaCode in legacyChinaMap.Values)
            {
                if (!string.IsNullOrWhiteSpace(chinaCode))
                    chinaCodes.Add(chinaCode.Trim());
            }

            return new ProductSalesAnalysisOptionsDto
            {
                AustralianSuppliers = australianCodes
                    .Select(code => new ProductSalesSupplierRefDto
                    {
                        Code = code,
                        Name = australianNameMap.TryGetValue(code, out var name) ? name : code,
                    })
                    .OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                ChinaSuppliers = chinaCodes
                    .Select(code => new ProductSalesSupplierRefDto
                    {
                        Code = code,
                        Name = chinaNameMap.TryGetValue(code, out var name) ? name : code,
                    })
                    .OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
        }

        public async Task<
            ProductSalesAnalysisResponse<ProductSalesAnalysisPagedDto<ProductSalesProductRowDto>>
        > GetProductSalesAnalysisCandidatesAsync(
            ProductSalesAnalysisRequest request,
            List<string>? branchCodes
        )
        {
            return await GetProductSalesAnalysisProductsAsync(request, branchCodes, "candidates");
        }

        public async Task<
            ProductSalesAnalysisResponse<ProductSalesAnalysisPagedDto<ProductSalesProductRowDto>>
        > GetProductSalesAnalysisSummaryAsync(
            ProductSalesAnalysisRequest request,
            List<string>? branchCodes,
            bool allowNonFreshData = false
        )
        {
            return await GetProductSalesAnalysisProductsAsync(
                request,
                branchCodes,
                "summary",
                allowNonFreshData
            );
        }

        public async Task<ProductSalesAnalysisResponse<List<ProductSalesDailyDto>>> GetProductSalesAnalysisProductDailyAsync(
            ProductSalesAnalysisRequest request,
            List<string>? branchCodes,
            bool allowNonFreshData = false
        )
        {
            var emptyData = new List<ProductSalesDailyDto>();
            var (startDate, endDate) = ValidateProductSalesAnalysisDateRange(
                request.Filter.StartDate,
                request.Filter.EndDate,
                DateTime.UtcNow
            );
            var status = await GetProductStatisticStatusAsync(startDate, endDate);
            var isFresh = string.Equals(
                status.Status,
                SalesStatisticRefreshStatus.Fresh,
                StringComparison.OrdinalIgnoreCase
            );

            // 空授权分店始终返回空数据，绕过模式也不放宽。
            if (branchCodes is { Count: 0 })
                return CreateProductSalesAnalysisResponse(status, emptyData);

            // 默认 fail-closed；仅 allowNonFreshData=true 时才用当前统计表继续计算。
            if (!isFresh && !allowNonFreshData)
                return CreateProductSalesAnalysisResponse(status, emptyData);

            var context = await BuildProductSalesAnalysisQueryContextAsync(request, branchCodes, startDate, endDate);

            // 非 Fresh 绕过模式不得读取或写入 ProductSalesAnalysis 缓存；Fresh 模式缓存行为不变。
            string? cacheKey = null;
            long expectedGeneration = 0;
            if (isFresh)
            {
                expectedGeneration = SalesDashboardCacheKeys.CaptureProductSalesAnalysisGeneration();
                cacheKey = SalesDashboardCacheKeys.ProductSalesAnalysisProductDaily(
                    request,
                    branchCodes,
                    status.CacheVersion
                );
                if (
                    _cache.TryGetValue<ProductSalesAnalysisResponse<List<ProductSalesDailyDto>>>(
                        cacheKey,
                        out var cached
                    )
                    && cached != null
                )
                {
                    return cached;
                }
            }

            var productCodes = await ResolveScopeProductCodesAsync(request, context);
            if (productCodes.Count == 0)
                return CreateProductSalesAnalysisResponse(status, emptyData);

            var rows = await QueryProductSalesDailyRowsAsync(productCodes, context);
            var series = BuildProductDailySeries(rows, startDate, endDate);
            var response = CreateProductSalesAnalysisResponse(status, series);
            if (isFresh)
                CacheProductSalesAnalysis(cacheKey!, response, expectedGeneration);
            return response;
        }

        public async Task<ProductSalesAnalysisResponse<List<ProductSalesBranchDto>>> GetProductSalesAnalysisBranchesAsync(
            ProductSalesAnalysisRequest request,
            List<string>? branchCodes,
            bool allowNonFreshData = false
        )
        {
            var emptyData = new List<ProductSalesBranchDto>();
            var (startDate, endDate) = ValidateProductSalesAnalysisDateRange(
                request.Filter.StartDate,
                request.Filter.EndDate,
                DateTime.UtcNow
            );
            var status = await GetProductStatisticStatusAsync(startDate, endDate);
            var isFresh = string.Equals(
                status.Status,
                SalesStatisticRefreshStatus.Fresh,
                StringComparison.OrdinalIgnoreCase
            );

            // 空授权分店始终返回空数据，绕过模式也不放宽。
            if (branchCodes is { Count: 0 })
                return CreateProductSalesAnalysisResponse(status, emptyData);

            // 默认 fail-closed；仅 allowNonFreshData=true 时才用当前统计表继续计算。
            if (!isFresh && !allowNonFreshData)
                return CreateProductSalesAnalysisResponse(status, emptyData);

            var context = await BuildProductSalesAnalysisQueryContextAsync(request, branchCodes, startDate, endDate);

            // 非 Fresh 绕过模式不得读取或写入 ProductSalesAnalysis 缓存；Fresh 模式缓存行为不变。
            string? cacheKey = null;
            long expectedGeneration = 0;
            if (isFresh)
            {
                expectedGeneration = SalesDashboardCacheKeys.CaptureProductSalesAnalysisGeneration();
                cacheKey = SalesDashboardCacheKeys.ProductSalesAnalysisBranches(
                    request,
                    branchCodes,
                    status.CacheVersion
                );
                if (
                    _cache.TryGetValue<ProductSalesAnalysisResponse<List<ProductSalesBranchDto>>>(
                        cacheKey,
                        out var cached
                    )
                    && cached != null
                )
                {
                    return cached;
                }
            }

            var productCodes = await ResolveScopeProductCodesAsync(request, context);
            if (productCodes.Count == 0)
                return CreateProductSalesAnalysisResponse(status, emptyData);

            var rows = await QueryProductSalesBranchRowsAsync(productCodes, context);
            var branchCodesInRows = rows.Select(x => x.BranchCode).Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var storeNameMap = await GetStoreNameMapAsync(branchCodesInRows);
            var items = rows
                .OrderBy(x => x.BranchCode, StringComparer.OrdinalIgnoreCase)
                .Select(row => new ProductSalesBranchDto
                {
                    BranchCode = row.BranchCode,
                    BranchName = storeNameMap.TryGetValue(row.BranchCode, out var name) ? name : null,
                    Metrics = BuildProductSalesMetrics(row.Quantity, row.SalesAmount),
                })
                .ToList();

            var response = CreateProductSalesAnalysisResponse(status, items);
            if (isFresh)
                CacheProductSalesAnalysis(cacheKey!, response, expectedGeneration);
            return response;
        }

        public async Task<ProductSalesAnalysisResponse<List<ProductSalesBranchDailyDto>>> GetProductSalesAnalysisBranchDailyAsync(
            ProductSalesAnalysisRequest request,
            List<string>? branchCodes,
            bool allowNonFreshData = false
        )
        {
            var emptyData = new List<ProductSalesBranchDailyDto>();
            var (startDate, endDate) = ValidateProductSalesAnalysisDateRange(
                request.Filter.StartDate,
                request.Filter.EndDate,
                DateTime.UtcNow
            );
            var status = await GetProductStatisticStatusAsync(startDate, endDate);
            var isFresh = string.Equals(
                status.Status,
                SalesStatisticRefreshStatus.Fresh,
                StringComparison.OrdinalIgnoreCase
            );

            // 空授权分店始终返回空数据，绕过模式也不放宽。
            if (branchCodes is { Count: 0 })
                return CreateProductSalesAnalysisResponse(status, emptyData);

            // 默认 fail-closed；仅 allowNonFreshData=true 时才用当前统计表继续计算。
            if (!isFresh && !allowNonFreshData)
                return CreateProductSalesAnalysisResponse(status, emptyData);

            if (string.IsNullOrWhiteSpace(request.BranchCode))
                throw new ProductSalesAnalysisValidationException("branch-daily 需要 branchCode。");

            var context = await BuildProductSalesAnalysisQueryContextAsync(request, branchCodes, startDate, endDate);

            // 非 Fresh 绕过模式不得读取或写入 ProductSalesAnalysis 缓存；Fresh 模式缓存行为不变。
            string? cacheKey = null;
            long expectedGeneration = 0;
            if (isFresh)
            {
                expectedGeneration = SalesDashboardCacheKeys.CaptureProductSalesAnalysisGeneration();
                cacheKey = SalesDashboardCacheKeys.ProductSalesAnalysisBranchDaily(
                    request,
                    branchCodes,
                    status.CacheVersion
                );
                if (
                    _cache.TryGetValue<ProductSalesAnalysisResponse<List<ProductSalesBranchDailyDto>>>(
                        cacheKey,
                        out var cached
                    )
                    && cached != null
                )
                {
                    return cached;
                }
            }

            var productCodes = await ResolveScopeProductCodesAsync(request, context);
            if (productCodes.Count == 0)
                return CreateProductSalesAnalysisResponse(status, emptyData);

            var rows = await QueryProductSalesBranchDailyRowsAsync(productCodes, context, request.BranchCode.Trim());
            var series = BuildBranchDailySeries(rows, startDate, endDate);
            var response = CreateProductSalesAnalysisResponse(status, series);
            if (isFresh)
                CacheProductSalesAnalysis(cacheKey!, response, expectedGeneration);
            return response;
        }

        private async Task<
            ProductSalesAnalysisResponse<ProductSalesAnalysisPagedDto<ProductSalesProductRowDto>>
        > GetProductSalesAnalysisProductsAsync(
            ProductSalesAnalysisRequest request,
            List<string>? branchCodes,
            string cacheSegment,
            bool allowNonFreshData = false
        )
        {
            var emptyPage = new ProductSalesAnalysisPagedDto<ProductSalesProductRowDto>();
            var (startDate, endDate) = ValidateProductSalesAnalysisDateRange(
                request.Filter.StartDate,
                request.Filter.EndDate,
                DateTime.UtcNow
            );
            var status = await GetProductStatisticStatusAsync(startDate, endDate);
            var isFresh = string.Equals(
                status.Status,
                SalesStatisticRefreshStatus.Fresh,
                StringComparison.OrdinalIgnoreCase
            );

            // 空授权分店始终返回空数据，绕过模式也不放宽。
            if (branchCodes is { Count: 0 })
                return CreateProductSalesAnalysisResponse(status, emptyPage);

            // 默认 fail-closed；仅 allowNonFreshData=true 时才用当前统计表继续计算。
            if (!isFresh && !allowNonFreshData)
                return CreateProductSalesAnalysisResponse(status, emptyPage);

            var context = await BuildProductSalesAnalysisQueryContextAsync(request, branchCodes, startDate, endDate);

            // 非 Fresh 绕过模式不得读取或写入 ProductSalesAnalysis 缓存；Fresh 模式缓存行为不变。
            string? cacheKey = null;
            long expectedGeneration = 0;
            if (isFresh)
            {
                expectedGeneration = SalesDashboardCacheKeys.CaptureProductSalesAnalysisGeneration();
                cacheKey = string.Equals(cacheSegment, "summary", StringComparison.OrdinalIgnoreCase)
                    ? SalesDashboardCacheKeys.ProductSalesAnalysisSummary(request, branchCodes, status.CacheVersion)
                    : SalesDashboardCacheKeys.ProductSalesAnalysisCandidates(request, branchCodes, status.CacheVersion);
                if (
                    _cache.TryGetValue<
                        ProductSalesAnalysisResponse<ProductSalesAnalysisPagedDto<ProductSalesProductRowDto>>
                    >(cacheKey, out var cached)
                    && cached != null
                )
                {
                    return cached;
                }
            }

            var queries = await BuildProductSalesAnalysisQueriesAsync(context);
            var isSummary = string.Equals(cacheSegment, "summary", StringComparison.OrdinalIgnoreCase);
            var includedProductCodes = isSummary
                && string.Equals(request.Selection.Mode, "included", StringComparison.OrdinalIgnoreCase)
                    ? request.Selection.IncludedProductCodes
                    : null;
            // 显式选择必须先把商品条件下推到 SQL，并按 500 个代码分批，避免先聚合筛选全集。
            var allAggregates = await QueryProductSalesAggregatesAsync(queries, includedProductCodes);
            // 候选列表始终展示完整筛选结果，只有汇总及下钻接口应用选择语义。
            var selected = isSummary
                ? ApplyProductSalesSelection(
                    allAggregates,
                    request.Selection.Mode,
                    request.Selection.IncludedProductCodes,
                    request.Selection.ExcludedProductCodes
                )
                : allAggregates;
            var paged = SortAndPageProductSalesAggregates(
                selected,
                request.PageNumber,
                request.PageSize,
                request.SortBy,
                request.SortDirection
            );
            var items = await EnrichProductSalesRowsAsync(paged.Items, context);
            var data = new ProductSalesAnalysisPagedDto<ProductSalesProductRowDto>
            {
                Items = items,
                Total = paged.Total,
                PageNumber = paged.PageNumber,
                PageSize = paged.PageSize,
            };

            var response = CreateProductSalesAnalysisResponse(status, data);
            if (isFresh)
                CacheProductSalesAnalysis(cacheKey!, response, expectedGeneration);
            return response;
        }

        internal static (DateTime StartDate, DateTime EndDate) ValidateProductSalesAnalysisDateRange(
            DateTime startDate,
            DateTime endDate,
            DateTime utcNow
        )
        {
            if (startDate == DateTime.MinValue || endDate == DateTime.MinValue)
                throw new ProductSalesAnalysisValidationException("开始日期和结束日期不能为空。");

            var start = startDate.Date;
            var end = endDate.Date;
            if (start > end)
                throw new ProductSalesAnalysisValidationException("开始日期不能晚于结束日期。");

            // 布里斯班无夏令时，固定 UTC+10。
            var brisbaneToday = utcNow.AddHours(10).Date;
            if (end > brisbaneToday)
                throw new ProductSalesAnalysisValidationException("结束日期不能晚于今天。");

            var dayCount = (int)(end - start).TotalDays + 1;
            if (dayCount > 366)
                throw new ProductSalesAnalysisValidationException("日期范围不能超过 366 个自然日。");

            return (start, end);
        }

        internal static decimal? CalculateAverageUnitPrice(int quantity, decimal salesAmount)
        {
            return quantity == 0 ? null : salesAmount / quantity;
        }

        internal static List<ProductSalesAggregateRow> ApplyProductSalesSelection(
            List<ProductSalesAggregateRow> rows,
            string? selectionMode,
            IEnumerable<string>? includedCodes,
            IEnumerable<string>? excludedCodes
        )
        {
            var mode = string.IsNullOrWhiteSpace(selectionMode)
                ? "allFiltered"
                : selectionMode.Trim();

            if (string.Equals(mode, "included", StringComparison.OrdinalIgnoreCase))
            {
                var included = NormalizeCodes(includedCodes).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return rows.Where(row => included.Contains(row.ProductCode)).ToList();
            }

            var excluded = NormalizeCodes(excludedCodes).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return rows.Where(row => !excluded.Contains(row.ProductCode)).ToList();
        }

        internal static ProductSalesAnalysisPagedDto<ProductSalesAggregateRow> SortAndPageProductSalesAggregates(
            List<ProductSalesAggregateRow> rows,
            int pageNumber,
            int pageSize,
            string? sortBy,
            string? sortDirection
        )
        {
            var normalizedPageNumber = pageNumber <= 0 ? 1 : pageNumber;
            var normalizedPageSize = pageSize <= 0 ? 50 : Math.Min(pageSize, 500);
            var ascending = string.Equals(sortDirection, "asc", StringComparison.OrdinalIgnoreCase);
            var sortField = string.IsNullOrWhiteSpace(sortBy) ? "quantity" : sortBy.Trim();

            IOrderedEnumerable<ProductSalesAggregateRow> ordered = sortField.ToLowerInvariant() switch
            {
                "salesamount" => ascending
                    ? rows.OrderBy(row => row.SalesAmount)
                    : rows.OrderByDescending(row => row.SalesAmount),
                "productcode" => ascending
                    ? rows.OrderBy(row => row.ProductCode, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(row => row.ProductCode, StringComparer.OrdinalIgnoreCase),
                _ => ascending
                    ? rows.OrderBy(row => row.Quantity)
                    : rows.OrderByDescending(row => row.Quantity),
            };

            ordered = ordered.ThenBy(row => row.ProductCode, StringComparer.OrdinalIgnoreCase);
            var items = ordered
                .Skip((normalizedPageNumber - 1) * normalizedPageSize)
                .Take(normalizedPageSize)
                .ToList();

            return new ProductSalesAnalysisPagedDto<ProductSalesAggregateRow>
            {
                Items = items,
                Total = rows.Count,
                PageNumber = normalizedPageNumber,
                PageSize = normalizedPageSize,
            };
        }

        internal static List<ProductSalesDailyDto> BuildProductDailySeries(
            IEnumerable<ProductSalesDailyRow> rows,
            DateTime startDate,
            DateTime endDate
        )
        {
            var metricsByDate = rows
                .GroupBy(row => row.Date.Date)
                .ToDictionary(
                    group => group.Key,
                    group => new ProductSalesDailyRow
                    {
                        Date = group.Key,
                        Quantity = group.Sum(x => x.Quantity),
                        SalesAmount = group.Sum(x => x.SalesAmount),
                    }
                );

            var series = new List<ProductSalesDailyDto>();
            for (var date = startDate.Date; date <= endDate.Date; date = date.AddDays(1))
            {
                metricsByDate.TryGetValue(date, out var row);
                series.Add(new ProductSalesDailyDto
                {
                    Date = date,
                    Metrics = BuildProductSalesMetrics(row?.Quantity ?? 0, row?.SalesAmount ?? 0m),
                });
            }

            return series;
        }

        internal static List<ProductSalesBranchDailyDto> BuildBranchDailySeries(
            IEnumerable<ProductSalesBranchDailyRow> rows,
            DateTime startDate,
            DateTime endDate
        )
        {
            var metricsByDate = rows
                .GroupBy(row => row.Date.Date)
                .ToDictionary(
                    group => group.Key,
                    group => new ProductSalesBranchDailyRow
                    {
                        Date = group.Key,
                        Quantity = group.Sum(x => x.Quantity),
                        SalesAmount = group.Sum(x => x.SalesAmount),
                    }
                );

            var series = new List<ProductSalesBranchDailyDto>();
            for (var date = startDate.Date; date <= endDate.Date; date = date.AddDays(1))
            {
                metricsByDate.TryGetValue(date, out var row);
                series.Add(new ProductSalesBranchDailyDto
                {
                    Date = date,
                    Metrics = BuildProductSalesMetrics(row?.Quantity ?? 0, row?.SalesAmount ?? 0m),
                });
            }

            return series;
        }

        private static ProductSalesAnalysisMetricsDto BuildProductSalesMetrics(int quantity, decimal salesAmount)
        {
            return new ProductSalesAnalysisMetricsDto
            {
                Quantity = quantity,
                SalesAmount = salesAmount,
                AverageUnitPrice = CalculateAverageUnitPrice(quantity, salesAmount),
            };
        }

        private static ProductSalesAnalysisResponse<T> CreateProductSalesAnalysisResponse<T>(
            ProductStatisticStatusSnapshot status,
            T? data
        )
        {
            return new ProductSalesAnalysisResponse<T>
            {
                StatisticStatus = status.Status,
                StatisticMessage = status.Message,
                StatisticUpdatedAt = status.UpdatedAt,
                CacheVersion = status.CacheVersion,
                Data = data,
            };
        }

        private void CacheProductSalesAnalysis<T>(
            string cacheKey,
            ProductSalesAnalysisResponse<T> response,
            long expectedGeneration
        )
        {
            ProductSalesAnalysisCacheWriteInterceptor?.Invoke();

            SalesDashboardCacheKeys.TryExecuteProductSalesAnalysisCacheWrite(
                cacheKey,
                expectedGeneration,
                (registrationToken, expirationToken) =>
                {
                    var cacheOptions = BuildProductSalesAnalysisCacheOptions(
                        cacheKey,
                        PRODUCT_SALES_ANALYSIS_CACHE_DURATION,
                        registrationToken,
                        expirationToken
                    );
                    _cache.Set(cacheKey, response, cacheOptions);
                }
            );
        }

        internal static MemoryCacheEntryOptions CreateProductSalesAnalysisCacheOptions(
            string cacheKey,
            TimeSpan absoluteExpiration
        )
        {
            return CreateProductSalesAnalysisCacheOptions(cacheKey, absoluteExpiration, out _);
        }

        internal static MemoryCacheEntryOptions CreateProductSalesAnalysisCacheOptions(
            string cacheKey,
            TimeSpan absoluteExpiration,
            out long registrationToken
        )
        {
            registrationToken = SalesDashboardCacheKeys.RegisterProductSalesAnalysisKey(
                cacheKey,
                out var expirationToken
            );
            return BuildProductSalesAnalysisCacheOptions(
                cacheKey,
                absoluteExpiration,
                registrationToken,
                expirationToken
            );
        }

        private static MemoryCacheEntryOptions BuildProductSalesAnalysisCacheOptions(
            string cacheKey,
            TimeSpan absoluteExpiration,
            long registrationToken,
            IChangeToken expirationToken
        )
        {
            return new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(absoluteExpiration)
                .AddExpirationToken(expirationToken)
                .RegisterPostEvictionCallback(
                    OnProductSalesAnalysisCacheEvicted,
                    new ProductSalesAnalysisCacheEvictionState(cacheKey, registrationToken)
                );
        }

        private static void OnProductSalesAnalysisCacheEvicted(
            object key,
            object? value,
            EvictionReason reason,
            object? state
        )
        {
            if (state is ProductSalesAnalysisCacheEvictionState registration)
            {
                SalesDashboardCacheKeys.UnregisterProductSalesAnalysisKey(
                    registration.CacheKey,
                    registration.RegistrationToken
                );
            }
        }

        private sealed class ProductSalesAnalysisCacheEvictionState
        {
            public ProductSalesAnalysisCacheEvictionState(string cacheKey, long registrationToken)
            {
                CacheKey = cacheKey;
                RegistrationToken = registrationToken;
            }

            public string CacheKey { get; }

            public long RegistrationToken { get; }
        }

        private async Task<ProductSalesAnalysisQueryContext> BuildProductSalesAnalysisQueryContextAsync(
            ProductSalesAnalysisRequest request,
            List<string>? branchCodes,
            DateTime startDate,
            DateTime endDate
        )
        {
            var australianSupplierCodes = NormalizeCodes(request.Filter.AustralianSupplierCodes);
            var chinaSupplierCodes = NormalizeCodes(request.Filter.ChinaSupplierCodes)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var chinaCatalog = await GetProductSalesChinaCatalogAsync();
            var chinaProductMap = chinaSupplierCodes.Any()
                ? await GetChinaSupplierProductMapAsync(chinaSupplierCodes)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            return new ProductSalesAnalysisQueryContext
            {
                StartDate = startDate,
                EndDate = endDate,
                BranchCodes = branchCodes,
                AustralianSupplierCodes = australianSupplierCodes,
                ChinaSupplierCodes = chinaSupplierCodes,
                AllChinaSupplierCodes = chinaCatalog.Codes,
                ChinaSupplierNameMap = chinaCatalog.Names,
                ChinaProductMap = chinaProductMap,
                Keyword = request.Filter.Keyword,
            };
        }

        private Task<ISugarQueryable<ProductStoreDailySalesStatistic>> BuildProductSalesAnalysisBaseQueryAsync(
            ProductSalesAnalysisQueryContext context
        )
        {
            var query = _context
                .Db.Queryable<ProductStoreDailySalesStatistic>()
                .Where(s => s.Date >= context.StartDate && s.Date <= context.EndDate);

            if (context.BranchCodes is { Count: > 0 } branchCodes)
            {
                query = query.Where(s => branchCodes.Contains(s.BranchCode));
            }

            return Task.FromResult(query);
        }

        private async Task<List<ISugarQueryable<ProductStoreDailySalesStatistic>>> BuildProductSalesAnalysisQueriesAsync(
            ProductSalesAnalysisQueryContext context
        )
        {
            var baseQuery = await BuildProductSalesAnalysisBaseQueryAsync(context);
            var supplierQueries = BuildProductSalesSupplierQueries(
                baseQuery,
                BuildProductSalesSupplierQueryPlan(context)
            );

            if (string.IsNullOrWhiteSpace(context.Keyword))
                return supplierQueries;

            return await ApplyProductSalesKeywordFilterAsync(
                supplierQueries,
                baseQuery,
                context.Keyword
            );
        }

        internal static ProductSalesSupplierQueryPlan BuildProductSalesSupplierQueryPlan(
            ProductSalesAnalysisQueryContext context
        )
        {
            var hasAustralian = context.AustralianSupplierCodes.Count > 0;
            var hasChina = context.ChinaSupplierCodes.Count > 0;
            if (!hasAustralian && !hasChina)
            {
                return new ProductSalesSupplierQueryPlan { IsUnfiltered = true };
            }

            var includesAllChina = context.AustralianSupplierCodes.Any(IsChinaLocalSupplierCode);
            var directSupplierCodes = BuildAustralianSupplierFilterCodes(
                    context.AustralianSupplierCodes,
                    context.AllChinaSupplierCodes
                )
                .Concat(context.ChinaSupplierCodes)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var legacyProductCodes = hasChina && !includesAllChina
                ? context.ChinaProductMap.Keys
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();

            return new ProductSalesSupplierQueryPlan
            {
                DirectSupplierCodes = directSupplierCodes,
                LegacyProductCodes = legacyProductCodes,
            };
        }

        private static List<ISugarQueryable<ProductStoreDailySalesStatistic>> BuildProductSalesSupplierQueries(
            ISugarQueryable<ProductStoreDailySalesStatistic> baseQuery,
            ProductSalesSupplierQueryPlan plan
        )
        {
            if (plan.IsUnfiltered)
            {
                return new List<ISugarQueryable<ProductStoreDailySalesStatistic>> { baseQuery };
            }

            var queries = new List<ISugarQueryable<ProductStoreDailySalesStatistic>>();
            foreach (var chunk in plan.DirectSupplierCodes.Chunk(PRODUCT_SALES_ANALYSIS_CODE_BATCH_SIZE))
            {
                var supplierCodes = chunk.ToList();
                queries.Add(baseQuery.Clone().Where(s => supplierCodes.Contains(s.SupplierCode)));
            }

            foreach (var chunk in plan.LegacyProductCodes.Chunk(PRODUCT_SALES_ANALYSIS_CODE_BATCH_SIZE))
            {
                var productCodes = chunk.ToList();
                queries.Add(
                    baseQuery
                        .Clone()
                        .Where(s =>
                            s.SupplierCode == CHINA_LOCAL_SUPPLIER_CODE
                            && productCodes.Contains(s.ProductCode)
                        )
                );
            }

            return queries;
        }

        private static List<string> BuildAustralianSupplierFilterCodes(
            List<string> supplierCodes,
            HashSet<string> chinaSupplierCodes
        )
        {
            if (!supplierCodes.Any(IsChinaLocalSupplierCode))
                return supplierCodes;

            return supplierCodes
                .Where(code => !IsChinaLocalSupplierCode(code))
                .Concat(new[] { CHINA_LOCAL_SUPPLIER_CODE })
                .Concat(chinaSupplierCodes)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task<List<ISugarQueryable<ProductStoreDailySalesStatistic>>> ApplyProductSalesKeywordFilterAsync(
            List<ISugarQueryable<ProductStoreDailySalesStatistic>> supplierQueries,
            ISugarQueryable<ProductStoreDailySalesStatistic> baseQuery,
            string keyword
        )
        {
            var searchText = keyword.Trim();
            var searchProductCodes = (await GetProductCodesForKeywordAsync(searchText))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var statisticBarcodeProductCodes = await baseQuery
                .Clone()
                .Where(s => s.Barcode != null && s.Barcode.Contains(searchText))
                .Select(s => s.ProductCode)
                .Distinct()
                .ToListAsync();
            foreach (var productCode in statisticBarcodeProductCodes)
            {
                if (!string.IsNullOrWhiteSpace(productCode))
                    searchProductCodes.Add(productCode.Trim());
            }

            var queries = new List<ISugarQueryable<ProductStoreDailySalesStatistic>>();
            foreach (var supplierQuery in supplierQueries)
            {
                foreach (var chunk in searchProductCodes.Chunk(PRODUCT_SALES_ANALYSIS_CODE_BATCH_SIZE))
                {
                    var productCodes = chunk.ToList();
                    queries.Add(
                        supplierQuery.Clone().Where(s => productCodes.Contains(s.ProductCode))
                    );
                }
            }

            return queries;
        }

        private async Task<List<string>> GetProductCodesForKeywordAsync(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
                return new List<string>();

            var searchText = keyword.Trim();
            return (await _context
                    .Db.Queryable<Product>()
                    .Where(p =>
                        (p.ItemNumber != null && p.ItemNumber.Contains(searchText))
                        || (p.Barcode != null && p.Barcode.Contains(searchText))
                        || p.ProductName.Contains(searchText)
                        || (p.EnglishName != null && p.EnglishName.Contains(searchText))
                    )
                    .Select(p => p.ProductCode ?? string.Empty)
                    .ToListAsync())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static List<List<string>> BatchProductSalesCodes(IEnumerable<string>? productCodes)
        {
            return NormalizeCodes(productCodes)
                .Chunk(PRODUCT_SALES_ANALYSIS_CODE_BATCH_SIZE)
                .Select(chunk => chunk.ToList())
                .ToList();
        }

        private async Task<List<ProductSalesAggregateRow>> QueryProductSalesAggregatesAsync(
            IEnumerable<ISugarQueryable<ProductStoreDailySalesStatistic>> queries,
            IEnumerable<string>? productCodes = null
        )
        {
            var merged = new Dictionary<string, ProductSalesAggregateRow>(StringComparer.OrdinalIgnoreCase);
            var productCodeBatches = productCodes == null
                ? null
                : BatchProductSalesCodes(productCodes);
            if (productCodeBatches is { Count: 0 })
                return new List<ProductSalesAggregateRow>();

            async Task MergeQueryAsync(ISugarQueryable<ProductStoreDailySalesStatistic> aggregateQuery)
            {
                var rows = await aggregateQuery
                    .GroupBy(s => s.ProductCode)
                    .Select(s => new ProductSalesAggregateRow
                    {
                        ProductCode = s.ProductCode,
                        Quantity = SqlFunc.AggregateSum(s.TotalQuantity),
                        SalesAmount = SqlFunc.AggregateSum(s.TotalAmount),
                    })
                    .ToListAsync();
                MergeProductSalesAggregateRows(merged, rows);
            }

            foreach (var query in queries)
            {
                if (productCodeBatches == null)
                {
                    await MergeQueryAsync(query.Clone());
                    continue;
                }

                foreach (var batch in productCodeBatches)
                {
                    await MergeQueryAsync(
                        query.Clone().Where(s => batch.Contains(s.ProductCode))
                    );
                }
            }

            return merged.Values.ToList();
        }

        internal static List<string> ResolveScopeProductCodes(ProductSalesAnalysisRequest request)
        {
            var mode = string.IsNullOrWhiteSpace(request.Scope?.Mode)
                ? string.Empty
                : request.Scope.Mode.Trim();
            if (string.Equals(mode, "currentProduct", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(request.Scope?.ProductCode))
                    throw new ProductSalesAnalysisValidationException("currentProduct 范围需要 productCode。");

                return new List<string> { request.Scope.ProductCode.Trim() };
            }

            if (string.Equals(mode, "selectedProducts", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(request.Selection.Mode, "included", StringComparison.OrdinalIgnoreCase))
                {
                    return new List<string>();
                }

                return ResolveSelectedProductCodes(request.Selection, Array.Empty<string>());
            }

            throw new ProductSalesAnalysisValidationException("scope.mode 仅支持 currentProduct 或 selectedProducts。");
        }

        internal static List<string> ResolveSelectedProductCodes(
            ProductSalesAnalysisSelectionDto selection,
            IEnumerable<string> filteredProductCodes
        )
        {
            var mode = string.IsNullOrWhiteSpace(selection.Mode)
                ? "allFiltered"
                : selection.Mode.Trim();
            if (string.Equals(mode, "included", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeCodes(selection.IncludedProductCodes);
            }

            if (!string.Equals(mode, "allFiltered", StringComparison.OrdinalIgnoreCase))
            {
                throw new ProductSalesAnalysisValidationException(
                    "selection.mode 仅支持 allFiltered 或 included。"
                );
            }

            var excluded = NormalizeCodes(selection.ExcludedProductCodes)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return NormalizeCodes(filteredProductCodes)
                .Where(code => !excluded.Contains(code))
                .ToList();
        }

        private async Task<List<string>> ResolveScopeProductCodesAsync(
            ProductSalesAnalysisRequest request,
            ProductSalesAnalysisQueryContext context
        )
        {
            if (!string.Equals(request.Scope?.Mode, "selectedProducts", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveScopeProductCodes(request);
            }

            if (string.Equals(request.Selection.Mode, "included", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveSelectedProductCodes(request.Selection, Array.Empty<string>());
            }

            var queries = await BuildProductSalesAnalysisQueriesAsync(context);
            var filteredRows = await QueryProductSalesAggregatesAsync(queries);
            return ResolveSelectedProductCodes(
                request.Selection,
                filteredRows.Select(row => row.ProductCode)
            );
        }

        private async Task<List<ProductSalesDailyRow>> QueryProductSalesDailyRowsAsync(
            List<string> productCodes,
            ProductSalesAnalysisQueryContext context
        )
        {
            var merged = new Dictionary<DateTime, ProductSalesDailyRow>();
            var queries = await BuildProductSalesAnalysisQueriesAsync(context);
            foreach (var query in queries)
            {
                foreach (var chunk in productCodes.Chunk(PRODUCT_SALES_ANALYSIS_CODE_BATCH_SIZE))
                {
                    var rows = await query
                        .Clone()
                        .Where(s => chunk.Contains(s.ProductCode))
                        .GroupBy(s => s.Date)
                        .Select(s => new ProductSalesDailyRow
                        {
                            Date = s.Date,
                            Quantity = SqlFunc.AggregateSum(s.TotalQuantity),
                            SalesAmount = SqlFunc.AggregateSum(s.TotalAmount),
                        })
                        .ToListAsync();
                    MergeProductSalesDailyRows(merged, rows);
                }
            }

            return merged.Values.ToList();
        }

        private async Task<List<ProductSalesBranchRow>> QueryProductSalesBranchRowsAsync(
            List<string> productCodes,
            ProductSalesAnalysisQueryContext context
        )
        {
            var merged = new Dictionary<string, ProductSalesBranchRow>(StringComparer.OrdinalIgnoreCase);
            var queries = await BuildProductSalesAnalysisQueriesAsync(context);
            foreach (var query in queries)
            {
                foreach (var chunk in productCodes.Chunk(PRODUCT_SALES_ANALYSIS_CODE_BATCH_SIZE))
                {
                    var rows = await query
                        .Clone()
                        .Where(s => chunk.Contains(s.ProductCode))
                        .GroupBy(s => s.BranchCode)
                        .Select(s => new ProductSalesBranchRow
                        {
                            BranchCode = s.BranchCode,
                            Quantity = SqlFunc.AggregateSum(s.TotalQuantity),
                            SalesAmount = SqlFunc.AggregateSum(s.TotalAmount),
                        })
                        .ToListAsync();
                    MergeProductSalesBranchRows(merged, rows);
                }
            }

            return merged.Values.ToList();
        }

        private async Task<List<ProductSalesBranchDailyRow>> QueryProductSalesBranchDailyRowsAsync(
            List<string> productCodes,
            ProductSalesAnalysisQueryContext context,
            string branchCode
        )
        {
            var merged = new Dictionary<DateTime, ProductSalesBranchDailyRow>();
            var queries = await BuildProductSalesAnalysisQueriesAsync(context);
            foreach (var query in queries)
            {
                foreach (var chunk in productCodes.Chunk(PRODUCT_SALES_ANALYSIS_CODE_BATCH_SIZE))
                {
                    var rows = await query
                        .Clone()
                        .Where(s => s.BranchCode == branchCode && chunk.Contains(s.ProductCode))
                        .GroupBy(s => s.Date)
                        .Select(s => new ProductSalesBranchDailyRow
                        {
                            Date = s.Date,
                            Quantity = SqlFunc.AggregateSum(s.TotalQuantity),
                            SalesAmount = SqlFunc.AggregateSum(s.TotalAmount),
                        })
                        .ToListAsync();
                    MergeProductSalesBranchDailyRows(merged, rows);
                }
            }

            return merged.Values.ToList();
        }

        private static void MergeProductSalesAggregateRows(
            Dictionary<string, ProductSalesAggregateRow> target,
            IEnumerable<ProductSalesAggregateRow> rows
        )
        {
            foreach (var row in rows)
            {
                if (target.TryGetValue(row.ProductCode, out var existing))
                {
                    existing.Quantity += row.Quantity;
                    existing.SalesAmount += row.SalesAmount;
                }
                else
                {
                    target[row.ProductCode] = new ProductSalesAggregateRow
                    {
                        ProductCode = row.ProductCode,
                        Quantity = row.Quantity,
                        SalesAmount = row.SalesAmount,
                    };
                }
            }
        }

        private static void MergeProductSalesDailyRows(
            Dictionary<DateTime, ProductSalesDailyRow> target,
            IEnumerable<ProductSalesDailyRow> rows
        )
        {
            foreach (var row in rows)
            {
                if (target.TryGetValue(row.Date.Date, out var existing))
                {
                    existing.Quantity += row.Quantity;
                    existing.SalesAmount += row.SalesAmount;
                }
                else
                {
                    target[row.Date.Date] = new ProductSalesDailyRow
                    {
                        Date = row.Date.Date,
                        Quantity = row.Quantity,
                        SalesAmount = row.SalesAmount,
                    };
                }
            }
        }

        private static void MergeProductSalesBranchRows(
            Dictionary<string, ProductSalesBranchRow> target,
            IEnumerable<ProductSalesBranchRow> rows
        )
        {
            foreach (var row in rows)
            {
                if (target.TryGetValue(row.BranchCode, out var existing))
                {
                    existing.Quantity += row.Quantity;
                    existing.SalesAmount += row.SalesAmount;
                }
                else
                {
                    target[row.BranchCode] = new ProductSalesBranchRow
                    {
                        BranchCode = row.BranchCode,
                        Quantity = row.Quantity,
                        SalesAmount = row.SalesAmount,
                    };
                }
            }
        }

        private static void MergeProductSalesBranchDailyRows(
            Dictionary<DateTime, ProductSalesBranchDailyRow> target,
            IEnumerable<ProductSalesBranchDailyRow> rows
        )
        {
            foreach (var row in rows)
            {
                if (target.TryGetValue(row.Date.Date, out var existing))
                {
                    existing.Quantity += row.Quantity;
                    existing.SalesAmount += row.SalesAmount;
                }
                else
                {
                    target[row.Date.Date] = new ProductSalesBranchDailyRow
                    {
                        Date = row.Date.Date,
                        Quantity = row.Quantity,
                        SalesAmount = row.SalesAmount,
                    };
                }
            }
        }

        private async Task<List<ProductSalesProductRowDto>> EnrichProductSalesRowsAsync(
            List<ProductSalesAggregateRow> rows,
            ProductSalesAnalysisQueryContext context
        )
        {
            if (rows.Count == 0)
                return new List<ProductSalesProductRowDto>();

            var productCodes = rows
                .Select(row => row.ProductCode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var productInfos = await GetProductSalesProductInfosAsync(productCodes);
            var supplierRows = await QueryProductSalesSupplierRowsAsync(productCodes, context);
            var chinaProductMap = await GetChinaSupplierProductMapForProductsAsync(productCodes);
            var resolutions = ResolveProductSalesSupplierRows(supplierRows, chinaProductMap, context.AllChinaSupplierCodes);

            var australianCodes = resolutions.Values
                .SelectMany(x => x.AustralianCodes)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var australianNameMap = await GetAustralianSupplierNameMapAsync(australianCodes);
            var chinaNameMap = new Dictionary<string, string>(
                context.ChinaSupplierNameMap,
                StringComparer.OrdinalIgnoreCase
            );

            return rows.Select(row =>
            {
                productInfos.TryGetValue(row.ProductCode, out var productInfo);
                resolutions.TryGetValue(row.ProductCode, out var resolution);
                resolution ??= new ProductSalesSupplierResolution();

                var chinaSuppliers = resolution.ChinaCodes
                    .Select(code => new ProductSalesSupplierRefDto
                    {
                        Code = code,
                        Name = chinaNameMap.TryGetValue(code, out var name) ? name : code,
                    })
                    .OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (resolution.ChinaSupplierUnmapped)
                {
                    chinaSuppliers.Add(new ProductSalesSupplierRefDto
                    {
                        Code = CHINA_LOCAL_SUPPLIER_CODE,
                        Name = "国内供应商未映射",
                    });
                }

                return new ProductSalesProductRowDto
                {
                    ProductCode = row.ProductCode,
                    ItemNumber = productInfo?.ItemNumber,
                    Barcode = productInfo?.Barcode,
                    ProductName = productInfo?.ProductName,
                    EnglishName = productInfo?.EnglishName,
                    ImageUrl = productInfo?.ImageUrl,
                    AustralianSuppliers = resolution.AustralianCodes
                        .Select(code => new ProductSalesSupplierRefDto
                        {
                            Code = code,
                            Name = australianNameMap.TryGetValue(code, out var name) ? name : code,
                        })
                        .OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    ChinaSuppliers = chinaSuppliers,
                    ChinaSupplierUnmapped = resolution.ChinaSupplierUnmapped,
                    Metrics = BuildProductSalesMetrics(row.Quantity, row.SalesAmount),
                };
            }).ToList();
        }

        private async Task<Dictionary<string, ProductSalesProductInfo>> GetProductSalesProductInfosAsync(
            List<string> productCodes
        )
        {
            var result = new Dictionary<string, ProductSalesProductInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var chunk in productCodes.Chunk(500))
            {
                var rows = await _context
                    .Db.Queryable<Product>()
                    .Where(p => p.ProductCode != null && chunk.Contains(p.ProductCode))
                    .Select(p => new ProductSalesProductInfo
                    {
                        ProductCode = p.ProductCode ?? string.Empty,
                        ItemNumber = p.ItemNumber,
                        Barcode = p.Barcode,
                        ProductName = p.ProductName,
                        EnglishName = p.EnglishName,
                        ImageUrl = p.ProductImage,
                    })
                    .ToListAsync();

                foreach (var row in rows.Where(x => !string.IsNullOrWhiteSpace(x.ProductCode)))
                {
                    if (!result.ContainsKey(row.ProductCode))
                        result[row.ProductCode] = row;
                }
            }

            return result;
        }

        private async Task<List<ProductSalesSupplierRow>> QueryProductSalesSupplierRowsAsync(
            List<string> productCodes,
            ProductSalesAnalysisQueryContext context
        )
        {
            var result = new HashSet<(string ProductCode, string SupplierCode)>();
            var baseQuery = _context
                .Db.Queryable<ProductStoreDailySalesStatistic>()
                .Where(s => s.Date >= context.StartDate && s.Date <= context.EndDate);
            if (context.BranchCodes is { Count: > 0 } branchCodes)
            {
                baseQuery = baseQuery.Where(s => branchCodes.Contains(s.BranchCode));
            }

            foreach (var chunk in productCodes.Chunk(500))
            {
                var rows = await baseQuery
                    .Clone()
                    .Where(s => chunk.Contains(s.ProductCode))
                    .GroupBy(s => new { s.ProductCode, s.SupplierCode })
                    .Select(s => new ProductSalesSupplierRow
                    {
                        ProductCode = s.ProductCode,
                        SupplierCode = s.SupplierCode,
                    })
                    .ToListAsync();
                foreach (var row in rows)
                {
                    result.Add((row.ProductCode, row.SupplierCode));
                }
            }

            return result
                .Select(x => new ProductSalesSupplierRow
                {
                    ProductCode = x.ProductCode,
                    SupplierCode = x.SupplierCode,
                })
                .ToList();
        }

        private async Task<Dictionary<string, string>> GetChinaSupplierProductMapForProductsAsync(
            List<string> productCodes
        )
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var chunk in productCodes.Chunk(500))
            {
                var rows = await _posmContext
                    .Db.Queryable<PosmProductSupplierMapping>()
                    .Where(m =>
                        !m.IsDeleted
                        && m.LocalSupplierCode == "200"
                        && m.ChinaSupplierCode != null
                        && m.ChinaSupplierCode != ""
                        && chunk.Contains(m.ProductCode)
                    )
                    .Select(m => new
                    {
                        m.ProductCode,
                        ChinaSupplierCode = m.ChinaSupplierCode ?? string.Empty,
                    })
                    .ToListAsync();

                foreach (var row in rows.Where(x =>
                    !string.IsNullOrWhiteSpace(x.ProductCode)
                    && !string.IsNullOrWhiteSpace(x.ChinaSupplierCode)
                ))
                {
                    if (!result.ContainsKey(row.ProductCode))
                        result[row.ProductCode] = row.ChinaSupplierCode;
                }
            }

            return result;
        }

        private static Dictionary<string, ProductSalesSupplierResolution> ResolveProductSalesSupplierRows(
            IEnumerable<ProductSalesSupplierRow> supplierRows,
            Dictionary<string, string> chinaProductMap,
            HashSet<string> allChinaSupplierCodes
        )
        {
            var result = new Dictionary<string, ProductSalesSupplierResolution>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in supplierRows)
            {
                if (!result.TryGetValue(row.ProductCode, out var resolution))
                {
                    resolution = new ProductSalesSupplierResolution();
                    result[row.ProductCode] = resolution;
                }

                if (IsChinaLocalSupplierCode(row.SupplierCode))
                {
                    if (
                        chinaProductMap.TryGetValue(row.ProductCode, out var mappedCode)
                        && !string.IsNullOrWhiteSpace(mappedCode)
                    )
                    {
                        resolution.ChinaCodes.Add(mappedCode);
                    }
                    else
                    {
                        resolution.ChinaSupplierUnmapped = true;
                    }
                }
                else if (allChinaSupplierCodes.Contains(row.SupplierCode))
                {
                    resolution.ChinaCodes.Add(row.SupplierCode);
                }
                else
                {
                    resolution.AustralianCodes.Add(row.SupplierCode);
                }
            }

            return result;
        }

        private static string? ResolveCostSource(List<BestSellerBranchSaleDto> branchSales)
        {
            if (!branchSales.Any())
                return null;

            return ResolveCostSource(branchSales.Select(x => x.CostSource));
        }

        private static string? ResolveCostSource(IEnumerable<string?> costSources)
        {
            var sources = costSources
                .Where(source => !string.IsNullOrWhiteSpace(source))
                .Select(source => source!)
                .Distinct()
                .ToList();
            if (!sources.Any())
                return null;

            return sources.Count == 1 ? sources[0] : "Mixed";
        }

    }
}
