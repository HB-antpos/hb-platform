using BlazorApp.Shared.Helper;
using SqlSugar;

namespace BlazorApp.Shared.Models
{
    /// <summary>
    /// HBweb 库 dbo.StoreSpecialProductCategory：分店级「特殊商品分类」。
    /// 分类只属于一家分店（StoreCode），同店名称不重复；收银端（WPF / iPad）在特殊商品页左侧按它分栏。
    /// 表由迁移 20261111.002-store-special-product-category 创建，不进 SqlSugarContext 的 tableTypes（启动不得代建表）。
    /// 继承 BaseEntity 只为复用审计列与自动填充；分类是硬删除，IsDeleted 恒为 false。
    /// </summary>
    [SugarTable("StoreSpecialProductCategory")]
    public class StoreSpecialProductCategory : BaseEntity
    {
        /// <summary>分类主键（UUIDv7 字符串）。</summary>
        [SugarColumn(IsPrimaryKey = true, Length = 50)]
        public string Guid { get; set; } = UuidHelper.GenerateUuid7();

        /// <summary>分店代码，与 StoreRetailPrice.StoreCode 同口径。</summary>
        [SugarColumn(Length = 50)]
        public string StoreCode { get; set; } = string.Empty;

        /// <summary>分类名称，同店不区分大小写不重复，1–30 个字符。</summary>
        [SugarColumn(Length = 50)]
        public string Name { get; set; } = string.Empty;

        /// <summary>分类色块，形如 #E65A2F；空 = 客户端用默认灰。</summary>
        [SugarColumn(Length = 16, IsNullable = true)]
        public string? Color { get; set; }

        /// <summary>分类在本店分类栏中的顺序，从 0 开始，同店唯一。</summary>
        public int SortOrder { get; set; }
    }

    /// <summary>
    /// HBweb 库 dbo.StoreSpecialProductCategoryItem：特殊商品归属的分类（一个商品在一家店只属于一个分类）。
    /// 只存「已归类」的商品；没有行 = 未分类。取消特殊商品标记时不删行（重新标记后仍回到原分类），
    /// 读取方必须再与 StoreRetailPrice.IsSpecialProduct 求交集，才算分类里的商品。
    /// </summary>
    [SugarTable("StoreSpecialProductCategoryItem")]
    public class StoreSpecialProductCategoryItem : BaseEntity
    {
        /// <summary>分店代码。</summary>
        [SugarColumn(IsPrimaryKey = true, Length = 50)]
        public string StoreCode { get; set; } = string.Empty;

        /// <summary>商品编码，与 StoreRetailPrice.ProductCode 同口径。</summary>
        [SugarColumn(IsPrimaryKey = true, Length = 50)]
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>所属分类 StoreSpecialProductCategory.Guid（分类被删时本表对应行一并删除）。</summary>
        [SugarColumn(Length = 50)]
        public string CategoryGuid { get; set; } = string.Empty;

        /// <summary>商品在分类内的顺序，从 0 开始；分类内可重复（读取时再按商品编码稳定排序）。</summary>
        public int SortOrder { get; set; }
    }
}
