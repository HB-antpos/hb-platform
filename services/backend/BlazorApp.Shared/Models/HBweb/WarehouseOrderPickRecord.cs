using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 拣货记录：只追加、不修改。订单行的已拣数 = 该行全部记录的 QuantityDelta 之和（件）。
/// 扫码、按钮加减、手动改总数都各写一条；手动改总数写的是与当前合计的差额。
/// ClientRequestId 唯一，客户端网络重试不会重复计数。
/// </summary>
[SugarTable("WarehouseOrderPickRecord")]
public sealed class WarehouseOrderPickRecord
{
    /// <summary>
    /// UUIDv7 的 32 位十六进制串：按时间递增，聚集主键顺序追加；由写入方（拣货服务）插入时生成。
    /// 本项目同时编译 net8.0 供 WPF 客户端引用，Guid.CreateVersion7 是 .NET 9 API，不能在这里做默认值。
    /// 不用 long 自增，SQLite 测试库的 CodeFirst 建不出 long 自增主键。
    /// </summary>
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 32)]
    public string RecordGUID { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 50)]
    public string OrderGUID { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 50)]
    public string DetailGUID { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 50)]
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>本次变化的件数，可为负（减一个中包、手动调小）。</summary>
    [SugarColumn(IsNullable = false)]
    public int QuantityDelta { get; set; }

    /// <summary>见 <see cref="WarehouseOrderPickSources"/>。</summary>
    [SugarColumn(IsNullable = false)]
    public int Source { get; set; }

    /// <summary>扫到的原始码（仅扫码来源），用于追溯“扫的是哪个子码 / 多码”。</summary>
    [SugarColumn(IsNullable = true, Length = 100)]
    public string? ScannedCode { get; set; }

    /// <summary>扫码按什么匹配上的，见 <see cref="WarehouseOrderPickMatchKinds"/>。</summary>
    [SugarColumn(IsNullable = true)]
    public int? MatchedBy { get; set; }

    /// <summary>当时使用的中包数快照；中包数事后被修改也能解释当时加了多少。</summary>
    [SugarColumn(IsNullable = true)]
    public int? MinOrderQuantityAtPick { get; set; }

    [SugarColumn(IsNullable = false, Length = 50)]
    public string PickerUserGuid { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 100)]
    public string PickerName { get; set; } = string.Empty;

    /// <summary>实际登录的账号；扫员工码拣货时与拣货人不同，设备会话时为空。</summary>
    [SugarColumn(IsNullable = true, Length = 50)]
    public string? AuthUserGuid { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? DeviceCode { get; set; }

    [SugarColumn(IsNullable = false)]
    public Guid ClientRequestId { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime CreatedAtUtc { get; set; }
}

public static class WarehouseOrderPickSources
{
    public const int Scan = 1;
    public const int Increment = 2;
    public const int Decrement = 3;
    public const int SetTotal = 4;

    public static bool IsKnown(int source) => source is >= Scan and <= SetTotal;
}

public static class WarehouseOrderPickMatchKinds
{
    public const int Barcode = 1;
    public const int ItemNumber = 2;
    public const int ProductCode = 3;
    public const int MultiCode = 4;
    public const int SetChild = 5;
}
