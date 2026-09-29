using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 拣货参与人：谁加入了哪张订单的拣货、最后一次在哪一行操作。
/// 用于“一起拣”时展示同事在哪个货位、是否仍在拣（最后活动时间）。
/// </summary>
[SugarTable("WarehouseOrderPickParticipant")]
public sealed class WarehouseOrderPickParticipant
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
    public string OrderGUID { get; set; } = string.Empty;

    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
    public string PickerUserGuid { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 100)]
    public string PickerName { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public DateTime JoinedAtUtc { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime LastActiveAtUtc { get; set; }

    [SugarColumn(IsNullable = true, Length = 50)]
    public string? LastDetailGUID { get; set; }
}
