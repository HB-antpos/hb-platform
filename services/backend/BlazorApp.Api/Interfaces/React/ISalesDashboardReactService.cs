using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces.React
{
    public interface ISalesDashboardReactService
    {
        Task<DashboardSummaryDto?> GetDashboardSummaryAsync(DateRangeDto dateRange);

        Task<List<HourlySalesDto>> GetHourlySalesAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes = null
        );

        Task<List<StoreSalesRankDto>> GetStoreSalesRankAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes = null,
            int topN = 50
        );

        Task<List<SupplierSalesRankDto>> GetSupplierSalesRankAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes = null,
            int topN = 20,
            string? supplierCode = null
        );

        Task<List<SupplierSalesRankDto>> GetSupplierSalesRankAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes,
            int topN,
            string? supplierCode,
            ProductReportStatisticStatusDto statisticStatus
        );

        Task<List<ChinaSupplierSalesRankDto>> GetChinaSupplierSalesRankAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes = null,
            int topN = 20,
            string? supplierCode = null
        );

        Task<List<ChinaSupplierSalesRankDto>> GetChinaSupplierSalesRankAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes,
            int topN,
            string? supplierCode,
            ProductReportStatisticStatusDto statisticStatus
        );

        Task<List<SupplierStoreSalesDto>> GetSupplierStoreSalesAsync(
            DateRangeDto dateRange,
            List<string> supplierCodes,
            List<string>? branchCodes = null
        );

        Task<List<SupplierStoreSalesDto>> GetSupplierStoreSalesAsync(
            DateRangeDto dateRange,
            List<string> supplierCodes,
            List<string>? branchCodes,
            ProductReportStatisticStatusDto statisticStatus
        );

        Task<List<StoreSupplierSalesDto>> GetStoreSupplierSalesAsync(
            DateRangeDto dateRange,
            List<string> branchCodes,
            int topN = 20
        );

        Task<PagedSalesProductDetailDto> GetSalesProductDetailsAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes = null,
            List<string>? supplierCodes = null,
            int pageIndex = 1,
            int pageSize = 100
        );

        Task<PagedSalesProductDetailWithDiscountDto> GetEnhancedSalesProductDetailsAsync(
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
        );

        Task<PagedSalesProductDetailWithDiscountDto> GetEnhancedSalesProductDetailsAsync(
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
        );

        Task<List<ProductBranchSalesDto>> GetProductSalesByAllBranchesAsync(
            DateRangeDto dateRange,
            string productCode,
            List<string>? branchCodes = null
        );

        Task<List<ProductBranchSalesDto>> GetProductSalesByAllBranchesAsync(
            DateRangeDto dateRange,
            string productCode,
            List<string>? branchCodes,
            ProductReportStatisticStatusDto statisticStatus
        );

        Task<List<ChinaSupplierStoreSalesDto>> GetChinaSupplierStoreSalesAsync(
            DateRangeDto dateRange,
            List<string> supplierCodes,
            List<string>? branchCodes = null
        );

        Task<List<ChinaSupplierStoreSalesDto>> GetChinaSupplierStoreSalesAsync(
            DateRangeDto dateRange,
            List<string> supplierCodes,
            List<string>? branchCodes,
            ProductReportStatisticStatusDto statisticStatus
        );

        /// <summary>
        /// 按分店汇总全部中国供应商的销售额（移动端「分店中国货占比」的分子）。
        /// branchCodes 为 null 表示不限分店；显式空列表表示无授权分店，返回空结果。
        /// </summary>
        Task<List<ChinaSupplierBranchTotalDto>> GetChinaSupplierBranchTotalsAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes,
            ProductReportStatisticStatusDto statisticStatus
        );

        /// <summary>
        /// 获取商品日分店统计完整性与缓存版本；不会触发聚合。
        /// </summary>
        Task<ProductReportStatisticStatusDto> GetProductReportStatisticStatusAsync(
            DateRangeDto dateRange
        );

        Task<SalesDetailSectionResultDto> GetSalesDetailColumnsAsync(
            DateRangeDto dateRange,
            SalesDetailKind kind,
            SalesDetailSection section,
            List<string>? branchCodes = null,
            string? selectedBranchCode = null,
            string? selectedSupplierCode = null,
            string? selectedProductCode = null,
            string? search = null,
            int pageIndex = 1,
            int pageSize = 20,
            ProductReportStatisticStatusDto? statisticStatus = null,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 在同一完整统计快照中读取销售明细各栏，避免逐栏重复查询。
        /// </summary>
        Task<ProductReportResponseDto<SalesDetailReportDto>> GetSalesDetailReportAsync(
            DateRangeDto dateRange,
            SalesDetailKind kind,
            List<string>? branchCodes = null,
            string? selectedBranchCode = null,
            string? selectedSupplierCode = null,
            string? selectedProductCode = null,
            string? search = null,
            int pageIndex = 1,
            int pageSize = 20,
            IReadOnlyCollection<SalesDetailSection>? sections = null,
            CancellationToken cancellationToken = default
        );

        Task<ProductReportResponseDto<SalesDetailReportDto>> GetSalesDetailReportFilteredAsync(
            DateRangeDto dateRange, SalesDetailKind kind, List<string>? branchCodes = null,
            string? selectedBranchCode = null, string? selectedSupplierCode = null,
            string? selectedProductCode = null, string? search = null, int pageIndex = 1, int pageSize = 20,
            IReadOnlyCollection<SalesDetailSection>? sections = null, CancellationToken cancellationToken = default,
            List<string>? selectedSupplierCodes = null, List<string>? supplierCategoryGuids = null,
            List<string>? warehouseCategoryGuids = null);

        /// <summary>
        /// 一次读取营业额、分店、时段及周层级，读取过程不触发统计重建。
        /// </summary>
        Task<BlazorApp.Api.Services.React.RevenueReportSnapshotDto> GetRevenueReportSnapshotAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes = null,
            List<string>? focusBranchCodes = null,
            int? topN = null,
            CancellationToken cancellationToken = default
        );

        Task<
            List<AustralianSupplierStoreSalesDetailDto>
        > GetAustralianSupplierStoreSalesDetailsAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes = null,
            List<string>? supplierCodes = null
        );

        Task<List<ChinaSupplierStoreSalesDetailDto>> GetChinaSupplierStoreSalesDetailsAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes = null,
            List<string>? supplierCodes = null
        );

        /// <summary>
        /// 获取 Executive Dashboard 分店业绩排名
        /// 用于 Executive Sales Intelligence 页面
        /// </summary>
        /// <param name="dateRange">日期范围</param>
        /// <param name="topN">返回前N条记录；为空返回全部</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <returns>分店业绩排名及统计完整性状态</returns>
        Task<ExecutiveBranchPerformanceResultDto> GetExecutiveBranchPerformanceAsync(
            DateRangeDto dateRange,
            int? topN = null,
            List<string>? branchCodes = null
        );

        /// <summary>
        /// 获取 Executive Dashboard 每小时流量密度
        /// 用于 Executive Sales Intelligence 页面的每小时流量展示
        /// </summary>
        /// <param name="dateRange">日期范围</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <returns>每小时流量密度及统计完整性状态</returns>
        Task<ExecutiveReportResultDto<ExecutiveHourlyTrafficDto>> GetExecutiveHourlyTrafficAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes = null
        );

        /// <summary>
        /// 获取分店每日营业额
        /// 用于移动端周/月营业额下钻明细
        /// </summary>
        /// <param name="dateRange">日期范围</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <returns>分店每日营业额及统计完整性状态</returns>
        Task<ExecutiveReportResultDto<BranchDailyPerformanceDto>> GetBranchDailyPerformanceAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes = null
        );

        /// <summary>
        /// 获取销售统计最近成功时间和最新运行状态
        /// </summary>
        Task<StatisticsFreshnessDto> GetStatisticsFreshnessAsync();

        /// <summary>
        /// 获取周业绩层级数据
        /// 用于 Executive Sales Intelligence 页面的 Weekly Performance Hierarchy 组件
        /// 返回周→分店→日期三层嵌套结构
        /// </summary>
        /// <param name="dateRange">日期范围</param>
        /// <param name="branchCodes">分店代码列表（可选）</param>
        /// <returns>周业绩层级数据列表</returns>
        Task<(ExecutiveReportResultDto<WeeklyPerformanceHierarchyDto> Report, string CacheVersion)>
            GetWeeklyPerformanceHierarchyAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes = null
        );

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
        Task<List<BranchSalesAggregateDto>> GetBranchSalesAggregateAsync(
            DateRangeDto dateRange,
            DateRangeDto? compareDateRange = null,
            List<string>? branchCodes = null,
            List<string>? supplierCodes = null
        );

        /// <summary>
        /// 获取紧凑销售看板；调用方传入的 query.BranchCodes 必须已经过授权解析。
        /// </summary>
        Task<CompactSalesBoardDto> GetCompactSalesBoardAsync(CompactSalesBoardQuery query);

        /// <summary>
        /// 获取 Best Sellers 商品列表（销量排名）
        /// 用于前端 StoreFront 的 Best Sellers 页面
        /// </summary>
        /// <param name="dateRange">日期范围</param>
        /// <param name="branchCodes">分店代码列表（可选，为空则查所有可访问分店）</param>
        /// <param name="pageIndex">页码</param>
        /// <param name="pageSize">每页记录数</param>
        /// <returns>Best Sellers 分页响应</returns>
        Task<BestSellerResponseDto> GetBestSellersAsync(
            DateRangeDto dateRange,
            List<string>? branchCodes = null,
            int pageIndex = 1,
            int pageSize = 50
        );

        /// <summary>
        /// 获取商品销量分析可选供应商。
        /// </summary>
        Task<ProductSalesAnalysisResponse<ProductSalesAnalysisOptionsDto>> GetProductSalesAnalysisOptionsAsync(
            ProductSalesAnalysisFilterDto filter,
            List<string>? branchCodes
        );

        Task<
            ProductSalesAnalysisResponse<ProductSalesAnalysisPagedDto<ProductSalesProductRowDto>>
        > GetProductSalesAnalysisCandidatesAsync(
            ProductSalesAnalysisRequest request,
            List<string>? branchCodes
        );

        Task<
            ProductSalesAnalysisResponse<ProductSalesAnalysisPagedDto<ProductSalesProductRowDto>>
        > GetProductSalesAnalysisSummaryAsync(
            ProductSalesAnalysisRequest request,
            List<string>? branchCodes,
            bool allowNonFreshData = false
        );

        Task<ProductSalesAnalysisResponse<List<ProductSalesDailyDto>>> GetProductSalesAnalysisProductDailyAsync(
            ProductSalesAnalysisRequest request,
            List<string>? branchCodes,
            bool allowNonFreshData = false
        );

        Task<ProductSalesAnalysisResponse<List<ProductSalesBranchDto>>> GetProductSalesAnalysisBranchesAsync(
            ProductSalesAnalysisRequest request,
            List<string>? branchCodes,
            bool allowNonFreshData = false
        );

        Task<ProductSalesAnalysisResponse<List<ProductSalesBranchDailyDto>>> GetProductSalesAnalysisBranchDailyAsync(
            ProductSalesAnalysisRequest request,
            List<string>? branchCodes,
            bool allowNonFreshData = false
        );
    }
}
