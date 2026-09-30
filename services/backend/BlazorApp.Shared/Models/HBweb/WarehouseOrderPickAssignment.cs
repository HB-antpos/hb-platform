using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 拣货分配：仓库经理把一张订单按走位顺序分段派给不同员工，逐行记录负责人。
/// 只做引导（“派给我的”列表与默认范围），不限制谁能拣；重新分配时整单替换。
/// 每人一段（SegmentNo 从 1 起连续编号），打印的分单条码带段号与分配版本，重新分配后旧分单失效。
/// </summary>
[SugarTable("WarehouseOrderPickAssignment")]
public sealed class WarehouseOrderPickAssignment
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
    public string OrderGUID { get; set; } = string.Empty;

    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
    public string DetailGUID { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 50)]
    public string PickerUserGuid { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 100)]
    public string PickerName { get; set; } = string.Empty;

    /// <summary>第几段（按经理选择的员工顺序，从 1 起连续）。</summary>
    [SugarColumn(IsNullable = false)]
    public int SegmentNo { get; set; }

    /// <summary>分配版本：每次保存递增（取当前秒数与旧版本 +1 的较大值），写进分单条码用来识别旧分单。</summary>
    [SugarColumn(IsNullable = false)]
    public int AssignmentVersion { get; set; }

    [SugarColumn(IsNullable = false, Length = 50)]
    public string AssignedByUserGuid { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 100)]
    public string AssignedByName { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public DateTime AssignedAtUtc { get; set; }
}
