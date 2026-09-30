using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 拣货时标记“货位没货”：每张订单每行最多一条，撤销或之后又拣到货时写 ClearedAtUtc 失效，不删行。
/// 标记只说明剩余数量拣不到的原因，不改拣货合计；提交时仍按已拣合计写配货数。
/// </summary>
[SugarTable("WarehouseOrderPickStockout")]
public sealed class WarehouseOrderPickStockout
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
    public string OrderGUID { get; set; } = string.Empty;

    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
    public string DetailGUID { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 50)]
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>标记时该商品的配货位快照（多个以逗号分隔）；未绑定货位时为空。</summary>
    [SugarColumn(IsNullable = true, Length = 200)]
    public string? LocationCode { get; set; }

    /// <summary>见 <see cref="WarehouseOrderPickStockoutReasons"/>。</summary>
    [SugarColumn(IsNullable = false)]
    public int Reason { get; set; }

    /// <summary>标记时本行已拣合计，便于事后核对“缺了多少”。</summary>
    [SugarColumn(IsNullable = false)]
    public int PickedAtMark { get; set; }

    [SugarColumn(IsNullable = false, Length = 50)]
    public string MarkedByUserGuid { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 100)]
    public string MarkedByName { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public DateTime MarkedAtUtc { get; set; }

    /// <summary>为空表示标记仍有效；撤销或之后又拣到货时写入。</summary>
    [SugarColumn(IsNullable = true)]
    public DateTime? ClearedAtUtc { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? ClearedByName { get; set; }
}

public static class WarehouseOrderPickStockoutReasons
{
    /// <summary>货位空了，一件都没有。</summary>
    public const int LocationEmpty = 1;

    /// <summary>货位上放的不是这个商品。</summary>
    public const int WrongProduct = 2;

    /// <summary>有货，但破损不能发。</summary>
    public const int Damaged = 3;

    public static bool IsKnown(int reason) => reason is >= LocationEmpty and <= Damaged;
}
