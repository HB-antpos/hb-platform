using BlazorApp.Shared.Helper;
using SqlSugar;

namespace BlazorApp.Shared.Models
{
    /// <summary>
    /// 本地供应商自己的商品分类（来自供应商网站，由浏览器扩展采集）。
    /// 供应商 200（Hot Bargain 自营）不使用本表，其供应商分类即仓库分类 <see cref="WarehouseCategory"/>。
    /// </summary>
    [SugarTable("LocalSupplierCategory")]
    public class LocalSupplierCategory : BaseEntity
    {
        [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
        public string CategoryGUID { get; set; } = UuidHelper.GenerateUuid7();

        [SugarColumn(IsNullable = false, Length = 64)]
        public string LocalSupplierCode { get; set; } = string.Empty;

        [SugarColumn(IsNullable = true, Length = 50)]
        public string? ParentGUID { get; set; }

        [SugarColumn(IsNullable = false, Length = 200)]
        public string CategoryName { get; set; } = string.Empty;

        /// <summary>
        /// 站点稳定标识：归一化后的分类页路径（可附带白名单查询参数），同一供应商内唯一。
        /// </summary>
        [SugarColumn(IsNullable = false, Length = 400)]
        public string ExternalKey { get; set; } = string.Empty;

        /// <summary>
        /// 反规范化的完整路径 "A &gt; B &gt; C"，列表展示无需回溯祖先。
        /// </summary>
        [SugarColumn(IsNullable = false, Length = 1000)]
        public string FullPath { get; set; } = string.Empty;

        /// <summary>
        /// 层级深度，根为 0；自动归类取最深的非促销分类。
        /// </summary>
        [SugarColumn(IsNullable = false)]
        public int Depth { get; set; }

        [SugarColumn(IsNullable = true, Length = 1000)]
        public string? SourceUrl { get; set; }

        /// <summary>
        /// 促销/横切分类（Clearance、New 等），不参与自动归类。
        /// </summary>
        [SugarColumn(IsNullable = false)]
        public bool IsPromotional { get; set; }

        /// <summary>
        /// 促销标记来源：pattern（配置规则判定）或 manual（人工切换，重算时不再覆盖）。
        /// </summary>
        [SugarColumn(IsNullable = false, Length = 16)]
        public string PromotionalSource { get; set; } = LocalSupplierCategoryPromotionalSources.Pattern;

        [SugarColumn(IsNullable = true)]
        public int? SortOrder { get; set; }

        [SugarColumn(IsNullable = false)]
        public bool IsActive { get; set; } = true;

        [SugarColumn(IsNullable = false)]
        public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;

        [SugarColumn(IsNullable = false)]
        public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    }

    public static class LocalSupplierCategoryPromotionalSources
    {
        public const string Pattern = "pattern";
        public const string Manual = "manual";
    }
}
