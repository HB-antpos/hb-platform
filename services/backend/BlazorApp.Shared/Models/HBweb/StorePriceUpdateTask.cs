using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 分店价格更新任务（移动端"价格更新"通知的数据来源）。
/// 同一「分店 + 商品」同一时刻最多一条 Pending 任务。
/// </summary>
[SugarTable("StorePriceUpdateTask")]
public sealed class StorePriceUpdateTask
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true, IsNullable = false)]
    public long Id { get; set; }

    [SugarColumn(IsNullable = false, Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 50)]
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>Pending / Completed / Cancelled，见 <see cref="StorePriceUpdateTaskStatuses"/>。</summary>
    [SugarColumn(IsNullable = false, Length = 20)]
    public string Status { get; set; } = StorePriceUpdateTaskStatuses.Pending;

    /// <summary>
    /// PriceUpdate（需改价）/ LabelOnly（待换标签）。由实时值推导后落库，
    /// 仅为了让 Web 报表可以直接 GROUP BY，不作为业务判断的唯一依据。
    /// </summary>
    [SugarColumn(IsNullable = false, Length = 20)]
    public string Kind { get; set; } = StorePriceUpdateTaskKinds.PriceUpdate;

    /// <summary>货架基准：任务创建时分店的旧值，也就是货架标签上印的价格。</summary>
    [SugarColumn(IsNullable = true)]
    public decimal? ShelfRetailPrice { get; set; }

    [SugarColumn(IsNullable = true, DecimalDigits = 4)]
    public decimal? ShelfDiscountRate { get; set; }

    /// <summary>目标值：仓库当前零售价与建议折扣（折扣为 null 表示不比较）。</summary>
    [SugarColumn(IsNullable = true)]
    public decimal? TargetRetailPrice { get; set; }

    [SugarColumn(IsNullable = true, DecimalDigits = 4)]
    public decimal? TargetDiscountRate { get; set; }

    /// <summary>最近一次评估时分店的实际值，供列表与报表直接展示。</summary>
    [SugarColumn(IsNullable = true)]
    public decimal? StoreRetailPrice { get; set; }

    [SugarColumn(IsNullable = true, DecimalDigits = 4)]
    public decimal? StoreDiscountRate { get; set; }

    [SugarColumn(IsNullable = false, Length = 100)]
    public string InitiatorName { get; set; } = "System";

    /// <summary>发起来源代码（WarehouseProducts / MobileWarehouse / StoreSync ...），中文名由前端文案映射。</summary>
    [SugarColumn(IsNullable = false, Length = 80)]
    public string InitiatorSource { get; set; } = "Unknown";

    /// <summary>来源补充信息，例如同步其它分店时的来源分店代码。</summary>
    [SugarColumn(IsNullable = true, Length = 200)]
    public string? InitiatorReference { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime InitiatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>同一任务被刷新的次数（仓库多次改价）。</summary>
    [SugarColumn(IsNullable = false)]
    public int ChangeCount { get; set; } = 1;

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? PriceAppliedBy { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? PriceAppliedAtUtc { get; set; }

    /// <summary>Printed / MarkedReplaced / KeptStorePrice，见 <see cref="StorePriceUpdateTaskCompletionModes"/>。</summary>
    [SugarColumn(IsNullable = true, Length = 30)]
    public string? CompletionMode { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? CompletedBy { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? CompletedAtUtc { get; set; }

    [SugarColumn(IsNullable = false)]
    public int LabelPrintCount { get; set; }

    /// <summary>通知页改价时入队的 HQ 同步 outbox OperationKey；HQ 同步关闭时为 null。</summary>
    [SugarColumn(IsNullable = true, Length = 200)]
    public string? HqSyncOperationKey { get; set; }

    [SugarColumn(IsNullable = true, Length = 40)]
    public string? CancelReason { get; set; }

    /// <summary>手动撤销人（仅 <see cref="StorePriceUpdateTaskCancelReasons.ManualCancel"/> 时有值，系统自动取消为 null）。</summary>
    [SugarColumn(IsNullable = true, Length = 100)]
    public string? CancelledBy { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? CancelledAtUtc { get; set; }

    [SugarColumn(IsNullable = true, Length = 200)]
    public string? CancelNote { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [SugarColumn(IsNullable = false)]
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public static class StorePriceUpdateTaskStatuses
{
    public const string Pending = "Pending";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
}

public static class StorePriceUpdateTaskKinds
{
    public const string PriceUpdate = "PriceUpdate";
    public const string LabelOnly = "LabelOnly";
}

public static class StorePriceUpdateTaskCompletionModes
{
    public const string Printed = "Printed";
    public const string MarkedReplaced = "MarkedReplaced";
    public const string KeptStorePrice = "KeptStorePrice";

    /// <summary>通知页改价后分店价恰好回到货架价：标签本来就是对的，无需换标签。</summary>
    public const string PriceAligned = "PriceAligned";
}

public static class StorePriceUpdateTaskCancelReasons
{
    /// <summary>目标价回到货架价（仓库改回去）。</summary>
    public const string Reverted = "Reverted";

    /// <summary>分店商品被标记为特殊商品或记录已不存在。</summary>
    public const string NoLongerApplicable = "NoLongerApplicable";

    /// <summary>管理员在 Web 监控页手动撤销（任务本身不需要再执行）。</summary>
    public const string ManualCancel = "ManualCancel";
}
