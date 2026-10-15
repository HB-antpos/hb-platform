using System.Text.Json.Serialization;

namespace BlazorApp.Shared.DTOs;

/// <summary>
/// 由服务端写入者构造的商品修改上下文；HTTP客户端不能直接提交该对象。
/// </summary>
public sealed class WarehouseProductChangeHistoryContextDto
{
    public string? Action { get; init; }
    public string? Source { get; init; }
    public string? SourceReference { get; init; }
    public Guid? BatchGuid { get; init; }
    public string? ActorUserGuid { get; init; }
    public string? ActorName { get; init; }
    public string? ActorType { get; init; }
    public DateTime? OccurredAtUtc { get; init; }

    /// <summary>
    /// 为 true 时，本次改动不触发分店价格更新任务（操作人取消勾选「生成价格任务」）。
    /// 默认 false，已有调用保持原行为。
    /// </summary>
    public bool SkipPriceTasks { get; init; }
}

/// <summary>
/// 仓库商品跨 Product、WarehouseProduct、DomesticProduct 的统一有效快照。
/// </summary>
public sealed record WarehouseProductChangeSnapshotDto
{
    public string ProductCode { get; init; } = string.Empty;

    // 三张主档各自的原始字段值只用于服务端差异计算。不能只保留一个固定优先级的
    // “有效值”，否则较低优先级表被直接修改时会被另一张表的旧值遮蔽而漏审计。
    [JsonIgnore]
    public WarehouseProductChangeSourceValuesDto? WarehouseSource { get; init; }

    [JsonIgnore]
    public WarehouseProductChangeSourceValuesDto? ProductSource { get; init; }

    [JsonIgnore]
    public WarehouseProductChangeSourceValuesDto? DomesticSource { get; init; }

    // 仅供审计服务把列表更新时间与实际历史事件对齐，不参与 ChangesJson 差异计算。
    [JsonIgnore]
    public bool WarehouseProductExists { get; init; }

    [JsonIgnore]
    public DateTime? WarehouseUpdatedAt { get; init; }

    [JsonIgnore]
    public string? WarehouseUpdatedBy { get; init; }

    public decimal? DomesticPrice { get; init; }
    public decimal? ImportPrice { get; init; }
    public decimal? RetailPrice { get; init; }

    /// <summary>仓库建议折扣（减免比例 0~1）。来自 ProductSuggestedDiscount 旁表，null 表示未设置。</summary>
    public decimal? SuggestedDiscountRate { get; init; }
    public string? LocalSupplierCode { get; init; }
    public string? DomesticSupplierCode { get; init; }
    public string? ProductName { get; init; }
    public string? EnglishName { get; init; }
    public string? ItemNumber { get; init; }
    public string? Barcode { get; init; }
    public int? ProductType { get; init; }
    public string? ProductCategoryGuid { get; init; }
    public string? WarehouseCategoryGuid { get; init; }
    public int? MiddlePackageQuantity { get; init; }
    public int? MiddlePackQuantity { get; init; }
    public int? PackingQuantity { get; init; }
    public decimal? Volume { get; init; }
    public int? MinOrderQuantity { get; init; }
    public string? ProductImage { get; init; }
    public bool? IsAutoPricing { get; init; }
    public bool? IsActive { get; init; }
}

/// <summary>
/// 单张商品主档的原始审计字段。统一事件会对同一语义字段逐来源检查，最终只输出一条字段差异。
/// </summary>
public sealed record WarehouseProductChangeSourceValuesDto
{
    public decimal? DomesticPrice { get; init; }
    public decimal? ImportPrice { get; init; }
    public decimal? RetailPrice { get; init; }
    public string? LocalSupplierCode { get; init; }
    public string? DomesticSupplierCode { get; init; }
    public string? ProductName { get; init; }
    public string? EnglishName { get; init; }
    public string? ItemNumber { get; init; }
    public string? Barcode { get; init; }
    public int? ProductType { get; init; }
    public string? ProductCategoryGuid { get; init; }
    public string? WarehouseCategoryGuid { get; init; }
    public int? MiddlePackageQuantity { get; init; }
    public int? MiddlePackQuantity { get; init; }
    public int? PackingQuantity { get; init; }
    public decimal? Volume { get; init; }
    public int? MinOrderQuantity { get; init; }
    public string? ProductImage { get; init; }
    public bool? IsAutoPricing { get; init; }
    public bool? IsActive { get; init; }
}

public sealed class WarehouseProductChangeHistoryProductSummaryDto
{
    public string ProductCode { get; init; } = string.Empty;
    public string? ItemNumber { get; init; }
    public string? Barcode { get; init; }
    public string? ProductName { get; init; }
    public string? EnglishName { get; init; }
    public string? LocalSupplierCode { get; init; }
    public string? DomesticSupplierCode { get; init; }
}

public sealed class WarehouseProductChangeItemDto
{
    public string FieldKey { get; init; } = string.Empty;
    public string ValueType { get; init; } = "string";

    // 审计必须区分“原来为空”和“空字符串”，所以即使为 null 也保留 JSON 属性。
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? BeforeValue { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? AfterValue { get; init; }
}

public sealed class WarehouseProductChangeHistoryEventDto
{
    public Guid EventGuid { get; init; }
    public string Action { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string? SourceReference { get; init; }
    public Guid? BatchGuid { get; init; }
    public string? ActorUserGuid { get; init; }
    public string ActorName { get; init; } = string.Empty;
    public string ActorType { get; init; } = string.Empty;
    public DateTime OccurredAtUtc { get; init; }
    public List<WarehouseProductChangeItemDto> Changes { get; init; } = [];
}

public sealed class WarehouseProductChangeHistoryPageDto
{
    public WarehouseProductChangeHistoryProductSummaryDto ProductSummary { get; init; } = new();
    public int PageNumber { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages { get; init; }
    public List<WarehouseProductChangeHistoryEventDto> Events { get; init; } = [];
}
