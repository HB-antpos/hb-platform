using BlazorApp.Shared.Helper;
using SqlSugar;

namespace BlazorApp.Shared.Models
{
    /// <summary>
    /// 使用帮助 FAQ 问答。步骤 / 提示 / 规则 / 相关 / 权限都是整体编辑的有序列表，以 JSON 文本存储。
    /// 表由迁移 20261025.001-help-faq 创建，实体刻意不加进 SqlSugarContext 的 tableTypes（启动不得代建表）。
    /// JSON 文本列在 SQL Server 中由迁移定为 nvarchar(max)，实体不写 ColumnDataType（SQLite 测试库不认 max）。
    /// </summary>
    [SugarTable("HelpFaqQuestion")]
    public class HelpFaqQuestion : BaseEntity
    {
        [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
        public string QuestionGUID { get; set; } = UuidHelper.GenerateUuid7();

        [SugarColumn(IsNullable = false, Length = 50)]
        public string GroupGUID { get; set; } = string.Empty;

        /// <summary>问题 id，同时是页面锚点 #faq-&lt;code&gt;，全局唯一，发布后不应修改。</summary>
        [SugarColumn(IsNullable = false, Length = 96)]
        public string Code { get; set; } = string.Empty;

        [SugarColumn(IsNullable = false, Length = 400)]
        public string TitleZh { get; set; } = string.Empty;

        [SugarColumn(IsNullable = false, Length = 400)]
        public string TitleEn { get; set; } = string.Empty;

        [SugarColumn(IsNullable = false, Length = 400)]
        public string WhereZh { get; set; } = string.Empty;

        [SugarColumn(IsNullable = false, Length = 400)]
        public string WhereEn { get; set; } = string.Empty;

        /// <summary>JSON 字符串数组（以 § 开头的元素是小标题）。</summary>
        [SugarColumn(IsNullable = false)]
        public string StepsZh { get; set; } = "[]";

        [SugarColumn(IsNullable = false)]
        public string StepsEn { get; set; } = "[]";

        [SugarColumn(IsNullable = true)]
        public string? TipsZh { get; set; }

        [SugarColumn(IsNullable = true)]
        public string? TipsEn { get; set; }

        [SugarColumn(IsNullable = true)]
        public string? RulesZh { get; set; }

        [SugarColumn(IsNullable = true)]
        public string? RulesEn { get; set; }

        /// <summary>JSON 数组 [{title, body}]。</summary>
        [SugarColumn(IsNullable = true)]
        public string? RelatedZh { get; set; }

        [SugarColumn(IsNullable = true)]
        public string? RelatedEn { get; set; }

        /// <summary>JSON 字符串数组（权限码，仅展示）。</summary>
        [SugarColumn(IsNullable = true)]
        public string? Permissions { get; set; }

        /// <summary>组内排序；页面编号由 Tab 内整体顺序实时计算，不入库。</summary>
        [SugarColumn(IsNullable = false)]
        public int SortOrder { get; set; }

        /// <summary>Draft 从未发布 / Published，见 <see cref="HelpFaqStatuses"/>。</summary>
        [SugarColumn(IsNullable = false, Length = 16)]
        public string Status { get; set; } = HelpFaqStatuses.Draft;

        /// <summary>已发布题再编辑时的整题草稿快照（P2 使用）。公开接口绝不输出。</summary>
        [SugarColumn(IsNullable = true)]
        public string? DraftJson { get; set; }

        [SugarColumn(IsNullable = true)]
        public DateTime? PublishedAt { get; set; }

        /// <summary>乐观并发版本号，仅用于冲突检测，不保留旧内容。</summary>
        [SugarColumn(IsNullable = false)]
        public int Version { get; set; } = 1;
    }

    public static class HelpFaqStatuses
    {
        public const string Draft = "Draft";
        public const string Published = "Published";
    }
}
