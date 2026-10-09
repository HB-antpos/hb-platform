using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Shared.Models.HBweb
{
    [SugarTable("Advertisement")]
    public class Advertisement : BaseEntity
    {
        [SugarColumn(IsPrimaryKey = true)]
        public string Id { get; set; } = string.Empty;

        [SugarColumn(Length = 200)]
        public string Title { get; set; } = string.Empty;

        [SugarColumn(Length = 1000, IsNullable = true)]
        public string? Description { get; set; }

        [SugarColumn(Length = 20)]
        public string MediaType { get; set; } = string.Empty;

        [SugarColumn(Length = 1000)]
        public string MediaUrl { get; set; } = string.Empty;

        [SugarColumn(Length = 1000, IsNullable = true)]
        public string? ThumbnailUrl { get; set; }

        [SugarColumn(Length = 500)]
        public string ObjectKey { get; set; } = string.Empty;

        [SugarColumn(Length = 255)]
        public string OriginalFileName { get; set; } = string.Empty;

        [SugarColumn(Length = 100)]
        public string ContentType { get; set; } = string.Empty;

        public long FileSize { get; set; }

        public DateTime EffectiveStart { get; set; }

        public DateTime EffectiveEnd { get; set; }

        public bool IsEnabled { get; set; } = true;

        public int SortOrder { get; set; }

        // 以下三列由迁移 20261009.002-advertisement-orientation 加出；特性须与迁移建出的列定义一致，
        // 因为 POS API 启动时会对本表执行 CodeFirst.InitTables（定义不一致会被自动改表或让迁移门禁失败）。
        // 版式：Landscape（横版，只在客显空闲全屏播）/ Portrait（竖版，只在收银右侧播）/ Any（通用，两处都播）。
        // 列为 nvarchar(16) NOT NULL，具名默认约束 DF_Advertisement_Orientation = N'Any'，旧广告一律视为 Any。
        [SugarColumn(Length = 16, IsNullable = false, DefaultValue = AdvertisementOrientations.Any)]
        public string Orientation { get; set; } = AdvertisementOrientations.Any;

        // 素材像素宽高（Web 上传时读取），历史广告为 NULL。
        [SugarColumn(IsNullable = true)]
        public int? MediaWidth { get; set; }

        [SugarColumn(IsNullable = true)]
        public int? MediaHeight { get; set; }

        [SugarColumn(IsIgnore = true)]
        [Navigate(NavigateType.OneToMany, nameof(AdvertisementStore.AdvertisementId))]
        public List<AdvertisementStore> Stores { get; set; } = new();
    }

    /// <summary>
    /// 广告版式取值（库里存 PascalCase，与 CK_Advertisement_Orientation 约束一致）。
    /// </summary>
    public static class AdvertisementOrientations
    {
        public const string Landscape = "Landscape";
        public const string Portrait = "Portrait";
        public const string Any = "Any";

        /// <summary>
        /// 大小写不敏感地规范成 PascalCase；空白视为 Any；不在三种取值内返回 null。
        /// </summary>
        public static string? Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Any;
            }

            var trimmed = value.Trim();
            if (string.Equals(trimmed, Landscape, StringComparison.OrdinalIgnoreCase))
            {
                return Landscape;
            }

            if (string.Equals(trimmed, Portrait, StringComparison.OrdinalIgnoreCase))
            {
                return Portrait;
            }

            return string.Equals(trimmed, Any, StringComparison.OrdinalIgnoreCase) ? Any : null;
        }
    }
}
