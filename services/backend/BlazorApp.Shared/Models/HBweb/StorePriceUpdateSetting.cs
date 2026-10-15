using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 价格变更任务的页面设置（单行表，Id 恒为 1）：目前只有「任务超期天数」。
/// 没有这一行时沿用配置 StorePriceUpdateTasks:OverdueDays（默认 7 天）；Web「价格变更任务」页的「设置」按钮写入这一行，
/// 后端读取时带 30 秒缓存、保存时主动清缓存，所以保存后服务端立即生效（App 端在下次刷新汇总时更新），不需要改服务器配置、不需要重启。
/// 实体不进 SqlSugarContext 的自动建表清单，由版本号迁移 20261017.003-store-price-update-setting 创建。
/// </summary>
[SugarTable("StorePriceUpdateSetting")]
public sealed class StorePriceUpdateSetting
{
    /// <summary>单行表的固定主键。</summary>
    public const int SingletonId = 1;

    [SugarColumn(IsPrimaryKey = true, IsIdentity = false)]
    public int Id { get; set; } = SingletonId;

    [SugarColumn(IsNullable = false)]
    public int OverdueDays { get; set; }

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime UpdatedAtUtc { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? UpdatedBy { get; set; }
}
