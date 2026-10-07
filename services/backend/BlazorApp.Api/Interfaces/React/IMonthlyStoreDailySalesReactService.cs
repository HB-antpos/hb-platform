using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces.React
{
    /// <summary>月度日销售下载页的数据读取：某个月内每家分店逐日的营业额、刷卡、现金。</summary>
    public interface IMonthlyStoreDailySalesReactService
    {
        /// <summary>
        /// 读取一个月的分店逐日数据。
        /// </summary>
        /// <param name="month">月份，格式 yyyy-MM；格式无效抛 ArgumentException。</param>
        /// <param name="branchScope">授权分店范围；null 表示不限制，空列表表示没有可见分店。</param>
        /// <param name="forceRefresh">绕过刷卡/现金汇总缓存重新读取 POSM。</param>
        Task<MonthlyStoreDailySalesDto> GetMonthlyStoreDailySalesAsync(
            string month,
            IReadOnlyCollection<string>? branchScope,
            bool forceRefresh,
            CancellationToken cancellationToken
        );
    }
}
