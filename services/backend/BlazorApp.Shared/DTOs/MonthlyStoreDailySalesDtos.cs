namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 月度日销售下载页的数据：某个月内每家分店逐日的营业额、刷卡、现金。
    /// 营业额与营业额日报同一口径（StoreSalesStatistic），刷卡/现金来自 POSM 支付明细。
    /// </summary>
    public class MonthlyStoreDailySalesDto
    {
        /// <summary>月份，格式 yyyy-MM。</summary>
        public string Month { get; set; } = string.Empty;

        /// <summary>该月总天数。</summary>
        public int DaysInMonth { get; set; }

        /// <summary>
        /// 已计入的最后一天（yyyy-MM-dd）：历史月份为月末，当月进行中为业务日的昨天（今天还没统计完）；
        /// 整个月都在未来、没有任何已计入的日子时为 null。
        /// </summary>
        public string? CountedThroughDate { get; set; }

        /// <summary>已计入的天数（从该月 1 日到 CountedThroughDate）。</summary>
        public int CountedDays { get; set; }

        /// <summary>分店列表，授权范围内当月有销售统计的分店，按分店编码升序。</summary>
        public List<MonthlyStoreDailySalesStoreDto> Stores { get; set; } = new();
    }

    /// <summary>单家分店当月的逐日数据。</summary>
    public class MonthlyStoreDailySalesStoreDto
    {
        public string BranchCode { get; set; } = string.Empty;

        public string BranchName { get; set; } = string.Empty;

        /// <summary>从该月 1 日到 CountedThroughDate，每个已计入的日子一条，按日期升序。</summary>
        public List<MonthlyStoreDailySalesDayDto> Days { get; set; } = new();
    }

    /// <summary>
    /// 单店单日数据。空值有明确含义，调用方不能当 0 处理：
    /// Revenue 为 null 表示该日统计尚未发布（缺数）；
    /// Card/Cash/Other 为 null 表示该日营业额存在，但没有可靠的支付方式拆分
    /// （营业额含 HBSales 旧系统来源、而旧系统没有支付方式；或统计尚未追上 POSM 的迟到上传）。
    /// 没有销售的休业日是 0，不是 null。
    /// </summary>
    public class MonthlyStoreDailySalesDayDto
    {
        /// <summary>日期，格式 yyyy-MM-dd。</summary>
        public string Date { get; set; } = string.Empty;

        public decimal? Revenue { get; set; }

        public decimal? Card { get; set; }

        public decimal? Cash { get; set; }

        /// <summary>其他 = 营业额 − 刷卡 − 现金（代金券等），保证三项加起来恒等于营业额。</summary>
        public decimal? Other { get; set; }
    }
}
