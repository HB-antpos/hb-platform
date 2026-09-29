using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 仓库订单拣货会话：每张分店订单一行。多人可同时加入同一会话一起拣货；
/// 提交后已拣数量写入订单行配货数（AllocQuantity），会话锁定，后续拣货写入一律拒绝。
/// 同一订单的拣货写入以本行加更新锁串行，保证“行合计 = 记录求和”在并发下成立。
/// </summary>
[SugarTable("WarehouseOrderPickSession")]
public sealed class WarehouseOrderPickSession
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 50)]
    public string OrderGUID { get; set; } = string.Empty;

    /// <summary>见 <see cref="WarehouseOrderPickSessionStatuses"/>。</summary>
    [SugarColumn(IsNullable = false)]
    public int Status { get; set; } = WarehouseOrderPickSessionStatuses.Picking;

    [SugarColumn(IsNullable = false)]
    public DateTime StartedAtUtc { get; set; }

    [SugarColumn(IsNullable = true, Length = 50)]
    public string? StartedByUserGuid { get; set; }

    [SugarColumn(IsNullable = false, Length = 100)]
    public string StartedByName { get; set; } = "System";

    [SugarColumn(IsNullable = true)]
    public DateTime? SubmittedAtUtc { get; set; }

    [SugarColumn(IsNullable = true, Length = 50)]
    public string? SubmittedByUserGuid { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? SubmittedByName { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime UpdatedAtUtc { get; set; }
}

public static class WarehouseOrderPickSessionStatuses
{
    /// <summary>拣货中，允许写入拣货记录。</summary>
    public const int Picking = 1;

    /// <summary>已提交，配货数已写回订单，会话只读。</summary>
    public const int Submitted = 2;
}
