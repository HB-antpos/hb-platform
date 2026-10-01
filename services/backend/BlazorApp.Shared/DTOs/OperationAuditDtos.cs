namespace BlazorApp.Shared.DTOs;

public sealed class OperationAuditBatchRequestDto
{
    public List<OperationAuditEventDto> Events { get; set; } = [];
}

public sealed class OperationAuditEventDto
{
    public Guid EventId { get; set; }

    public int SchemaVersion { get; set; } = 1;

    public DateTimeOffset OccurredAtUtc { get; set; }

    public string OperationType { get; set; } = string.Empty;

    public string Outcome { get; set; } = string.Empty;

    public string? CashierId { get; set; }

    public string? UserGuid { get; set; }

    public string? CashierName { get; set; }

    public bool IsOfflineCached { get; set; }

    public bool IsEmergencyOverride { get; set; }

    public string StoreCode { get; set; } = string.Empty;

    public string DeviceCode { get; set; } = string.Empty;

    public string? AppVersion { get; set; }

    public string? InstanceId { get; set; }

    public string? OrderGuid { get; set; }

    public string? ReceiptNumber { get; set; }

    public string? CorrelationId { get; set; }

    public string? TraceId { get; set; }

    public string? PaymentMethod { get; set; }

    public string? ReasonCode { get; set; }

    public string? SafeMessage { get; set; }

    public string CurrencyCode { get; set; } = "AUD";

    public decimal? PaymentAmount { get; set; }

    public decimal? BeforeGross { get; set; }

    public decimal? AfterGross { get; set; }

    public decimal? BeforeDiscount { get; set; }

    public decimal? AfterDiscount { get; set; }

    public decimal? BeforeActual { get; set; }

    public decimal? AfterActual { get; set; }

    public decimal? AmountDelta { get; set; }

    public Dictionary<string, string?>? Properties { get; set; }

    // 已发布的手持/iPad 对非订单事件发送 "items": null；nullable 上下文下非可空 List
    // 会被 MVC 隐式必填校验整批 400，客户端再把同批事件永久标为 rejected。
    // 反序列化时把 null 归一为空列表，与 IngestAsync 的 Items ?? [] 语义一致。
    public List<OperationAuditItemDto> Items
    {
        get => items;
        set => items = value ?? [];
    }

    private List<OperationAuditItemDto> items = [];
}

public sealed class OperationAuditItemDto
{
    public string? ProductCode { get; set; }

    public string? ItemNumber { get; set; }

    public string? ReferenceCode { get; set; }

    public string? LookupCode { get; set; }

    public string? DisplayName { get; set; }

    public string? LineKind { get; set; }

    public decimal? BeforeQuantity { get; set; }

    public decimal? AfterQuantity { get; set; }

    public decimal? QuantityDelta { get; set; }

    public decimal? BeforeUnitPrice { get; set; }

    public decimal? AfterUnitPrice { get; set; }

    public decimal? UnitPriceDelta { get; set; }

    public decimal? BeforeDiscountAmount { get; set; }

    public decimal? AfterDiscountAmount { get; set; }

    public decimal? DiscountAmountDelta { get; set; }

    public decimal? BeforeGrossAmount { get; set; }

    public decimal? AfterGrossAmount { get; set; }

    public decimal? GrossAmountDelta { get; set; }

    public decimal? BeforeActualAmount { get; set; }

    public decimal? AfterActualAmount { get; set; }

    public decimal? ActualAmountDelta { get; set; }
}

public sealed class OperationAuditBatchResultDto
{
    public int AcceptedCount { get; set; }

    public int DuplicateCount { get; set; }

    public int RejectedCount { get; set; }

    public List<OperationAuditItemResultDto> Results { get; set; } = [];
}

public sealed class OperationAuditItemResultDto
{
    public Guid EventId { get; set; }

    public string Status { get; set; } = string.Empty;

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }
}
