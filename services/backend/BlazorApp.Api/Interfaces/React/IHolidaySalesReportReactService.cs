using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces.React
{
    /// <summary>节日销售报告（万圣节）：KFC / Brazco 节日商品的销售、进货、卖断与滞销。</summary>
    public interface IHolidaySalesReportReactService
    {
        /// <summary>
        /// 生成报告。
        /// </summary>
        /// <param name="year">节日所在年份；不合法抛 ArgumentException。</param>
        /// <param name="branchScope">授权分店范围；null 表示不限制，空列表表示没有可见分店。</param>
        /// <param name="branchCode">只看这一家店（单店视图）；null 表示授权范围内全部分店合计。</param>
        /// <param name="supplierCode">只看某家供应商（257 KFC / 243 Brazco）；null 表示两家合计。</param>
        /// <param name="forceRefresh">绕过原始数据缓存重新读库。</param>
        Task<HolidaySalesReportDto> GetReportAsync(
            int year,
            IReadOnlyCollection<string>? branchScope,
            string? branchCode,
            string? supplierCode,
            bool forceRefresh,
            CancellationToken cancellationToken
        );

        /// <summary>单个商品的分店明细（榜单钻取）；商品不在节日商品范围内返回 null。</summary>
        Task<HolidaySalesProductStoresDto?> GetProductStoresAsync(
            int year,
            IReadOnlyCollection<string>? branchScope,
            string productCode,
            CancellationToken cancellationToken
        );

        /// <summary>
        /// 大类明细（大类表里点大类钻取）：该大类的合计、各分店表现与完整商品清单。
        /// 大类代号不认识返回 null；传 branchCode 只看这一家店。
        /// </summary>
        Task<HolidaySalesCategoryDetailDto?> GetCategoryDetailAsync(
            int year,
            IReadOnlyCollection<string>? branchScope,
            string? branchCode,
            string? supplierCode,
            string categoryKey,
            CancellationToken cancellationToken
        );

        /// <summary>大类归并对照（供应商分类落到哪个大类）。</summary>
        Task<HolidaySalesCategoryMapDto> GetCategoryMapAsync(
            int year,
            IReadOnlyCollection<string>? branchScope,
            CancellationToken cancellationToken
        );
    }
}
