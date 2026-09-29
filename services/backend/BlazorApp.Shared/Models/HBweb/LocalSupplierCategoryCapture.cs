using SqlSugar;

namespace BlazorApp.Shared.Models
{
    /// <summary>
    /// 采集观察记录：某供应商货号在某分类页出现过。保留全部观察以便促销标记或规则变化后重算归类。
    /// 复合主键即业务唯一键，重复采集只累加计数，天然幂等。
    /// </summary>
    [SugarTable("LocalSupplierCategoryCapture")]
    public class LocalSupplierCategoryCapture
    {
        [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 64)]
        public string LocalSupplierCode { get; set; } = string.Empty;

        /// <summary>
        /// 归一化（去空格、大写）后的页面货号；GFA 236 页面编码即商品编码。
        /// </summary>
        [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
        public string ItemNumber { get; set; } = string.Empty;

        [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
        public string CategoryGUID { get; set; } = string.Empty;

        [SugarColumn(IsNullable = false)]
        public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;

        [SugarColumn(IsNullable = false)]
        public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

        [SugarColumn(IsNullable = false)]
        public int SeenCount { get; set; } = 1;

        [SugarColumn(IsNullable = true, Length = 1000)]
        public string? LastSourceUrl { get; set; }

        [SugarColumn(IsNullable = false, Length = 16)]
        public string LastMode { get; set; } = "passive";

        [SugarColumn(IsNullable = true, Length = 100)]
        public string? CapturedBy { get; set; }
    }
}
