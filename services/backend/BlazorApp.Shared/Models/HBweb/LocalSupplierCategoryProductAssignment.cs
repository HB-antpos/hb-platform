using SqlSugar;

namespace BlazorApp.Shared.Models
{
    /// <summary>
    /// 商品与供应商分类的一对一归属。
    /// 独立成表而不加在 Product 上：HQ 商品同步会清表重建或整行覆盖 Product，加列会被静默清空。
    /// </summary>
    [SugarTable("LocalSupplierCategoryProductAssignment")]
    public class LocalSupplierCategoryProductAssignment
    {
        [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>
        /// 归属写入时商品的供应商。读取时与 Product.LocalSupplierCode 比对，不一致视为未归类，
        /// 这样 HQ 同步改了商品供应商也不会显示错误分类。
        /// </summary>
        [SugarColumn(IsNullable = false, Length = 64)]
        public string LocalSupplierCode { get; set; } = string.Empty;

        [SugarColumn(IsNullable = false, Length = 50)]
        public string CategoryGUID { get; set; } = string.Empty;

        /// <summary>
        /// website（按采集自动归类）或 manual（人工指定，自动重算不覆盖）。
        /// </summary>
        [SugarColumn(IsNullable = false, Length = 16)]
        public string Source { get; set; } = "website";

        [SugarColumn(IsNullable = true, Length = 50)]
        public string? ItemNumberKey { get; set; }

        [SugarColumn(IsNullable = false)]
        public DateTime AssignedAt { get; set; } = DateTime.UtcNow;

        [SugarColumn(IsNullable = true, Length = 100)]
        public string? AssignedBy { get; set; }
    }
}
