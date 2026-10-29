using BlazorApp.Shared.Helper;
using SqlSugar;

namespace BlazorApp.Shared.Models
{
    /// <summary>
    /// 使用帮助 FAQ 分组（公开页 /support/faq 的章节）。
    /// 表由迁移 20261025.001-help-faq 创建，实体刻意不加进 SqlSugarContext 的 tableTypes（启动不得代建表）。
    /// </summary>
    [SugarTable("HelpFaqGroup")]
    public class HelpFaqGroup : BaseEntity
    {
        [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
        public string GroupGUID { get; set; } = UuidHelper.GenerateUuid7();

        /// <summary>所属标签页：pos（收银端）/ mobile（移动端），见 <see cref="HelpFaqTabs"/>。</summary>
        [SugarColumn(IsNullable = false, Length = 16)]
        public string Tab { get; set; } = HelpFaqTabs.Pos;

        /// <summary>分组 id（如 sale、people），同一 Tab 内唯一。</summary>
        [SugarColumn(IsNullable = false, Length = 64)]
        public string Code { get; set; } = string.Empty;

        [SugarColumn(IsNullable = false, Length = 200)]
        public string TitleZh { get; set; } = string.Empty;

        [SugarColumn(IsNullable = false, Length = 200)]
        public string TitleEn { get; set; } = string.Empty;

        [SugarColumn(IsNullable = false)]
        public int SortOrder { get; set; }
    }

    public static class HelpFaqTabs
    {
        public const string Pos = "pos";
        public const string Mobile = "mobile";

        /// <summary>公开接口只认这两个 Tab，且按此顺序输出。</summary>
        public static readonly string[] All = [Pos, Mobile];
    }
}
