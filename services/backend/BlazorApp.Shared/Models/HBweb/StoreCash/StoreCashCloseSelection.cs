using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 某分店某设备某营业日「纳入现金池的日结存档」的手动选择记录，只追加。
/// 没有记录、或当前记录的 Mode = Default 时，按默认规则取该设备当天 savedAt 最新的一份；
/// Mode = Manual 时取 CloseIdsJson 里选定的若干份求和。每次手选或恢复默认都新写一行，旧的当前行置为非当前，历史可追溯。
/// 选定后又出现更新的存档不会自动切换：以 LatestSavedAtUtcAtSelection 比较识别「选择可能过期」，交人确认。
/// 实体不进 SqlSugarContext 的自动建表清单，由版本号迁移 20261008.001-store-cash-management 创建。
/// </summary>
[SugarTable("StoreCashCloseSelection")]
public sealed class StoreCashCloseSelection
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
    public string SelectionGuid { get; set; } = Guid.NewGuid().ToString();

    [SugarColumn(IsNullable = false, Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime BusinessDate { get; set; }

    [SugarColumn(IsNullable = false, Length = 50)]
    public string DeviceCode { get; set; } = string.Empty;

    /// <summary>Default / Manual。</summary>
    [SugarColumn(IsNullable = false, Length = 20)]
    public string Mode { get; set; } = "Manual";

    /// <summary>Manual 时选定的日结存档编号（JSON 字符串数组）；Default 时为空数组。</summary>
    [SugarColumn(IsNullable = false, Length = 1000)]
    public string CloseIdsJson { get; set; } = "[]";

    /// <summary>选定时该设备当天最新存档的保存时间，用来识别之后有没有更新的存档。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "datetime2")]
    public DateTime? LatestSavedAtUtcAtSelection { get; set; }

    /// <summary>所选存档统计区间重叠时置位：可能把同一天的现金重复计入。</summary>
    [SugarColumn(IsNullable = false)]
    public bool OverlapWarning { get; set; }

    [SugarColumn(IsNullable = false, Length = 500)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>每个（分店、营业日、设备）至多一条当前记录，由过滤唯一索引保证。</summary>
    [SugarColumn(IsNullable = false)]
    public bool IsCurrent { get; set; } = true;

    [SugarColumn(IsNullable = false, Length = 50)]
    public string SelectedByUserGuid { get; set; } = string.Empty;

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? SelectedByName { get; set; }

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime SelectedAtUtc { get; set; }
}
