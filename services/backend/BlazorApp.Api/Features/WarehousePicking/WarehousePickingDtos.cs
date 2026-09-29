namespace BlazorApp.Api.Features.WarehousePicking;

// 仓库订单拣货接口契约；字段名经默认 JSON 序列化为 camelCase，与移动端 warehouse-picking/types.ts 一一对应。

public sealed class WarehousePickerResolveRequestDto
{
    public string? Barcode { get; set; }
}

public sealed class WarehousePickerResolveResultDto
{
    public string PickerUserGuid { get; set; } = string.Empty;
    public string PickerName { get; set; } = string.Empty;
    public string? RoleLabel { get; set; }
    public string Ticket { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
}

public sealed class WarehousePickerRefDto
{
    public string PickerUserGuid { get; set; } = string.Empty;
    public string PickerName { get; set; } = string.Empty;
}

public sealed class WarehousePickingOrderListItemDto
{
    public string OrderGuid { get; set; } = string.Empty;
    public string? OrderNo { get; set; }
    public string? StoreCode { get; set; }
    public string? StoreName { get; set; }
    public DateTime? OrderDate { get; set; }
    public int FlowStatus { get; set; }
    public int LineCount { get; set; }
    public decimal TotalQuantity { get; set; }
    public int PickedLineCount { get; set; }
    public int? SessionStatus { get; set; }
    public List<WarehousePickerRefDto> Pickers { get; set; } = new();
}

public sealed class WarehousePickingOrderCountsDto
{
    public int All { get; set; }
    public int ToPick { get; set; }
    public int Picking { get; set; }
}

public sealed class WarehousePickingOrderListDto
{
    public List<WarehousePickingOrderListItemDto> Items { get; set; } = new();
    public WarehousePickingOrderCountsDto Counts { get; set; } = new();
}

public sealed class WarehousePickingOrderResolveDto
{
    public string OrderGuid { get; set; } = string.Empty;
}

public sealed class WarehousePickingSessionDto
{
    public int Status { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public string StartedByName { get; set; } = string.Empty;
    public DateTime? SubmittedAtUtc { get; set; }
    public string? SubmittedByName { get; set; }
}

public sealed class WarehousePickedByDto
{
    public string PickerUserGuid { get; set; } = string.Empty;
    public string PickerName { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

public sealed class WarehousePickingSetChildDto
{
    public string ProductCode { get; set; } = string.Empty;
    public string? ItemNumber { get; set; }
    public string? Barcode { get; set; }
    public string? ProductName { get; set; }
}

public sealed class WarehousePickingLineDto
{
    public string DetailGuid { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public string? ItemNumber { get; set; }
    public string? Barcode { get; set; }
    public string? ProductName { get; set; }
    public string? ProductImage { get; set; }
    public string? LocationCode { get; set; }
    public decimal OrderedQuantity { get; set; }

    /// <summary>中包数（WarehouseProduct.MinOrderQuantity 原值）；为空或 ≤0 表示未设置，扫码不能按中包累加。</summary>
    public int? MinOrderQuantity { get; set; }

    public bool IsSet { get; set; }
    public List<WarehousePickingSetChildDto> SetChildren { get; set; } = new();
    public int PickedTotal { get; set; }
    public List<WarehousePickedByDto> PickedBy { get; set; } = new();
}

public sealed class WarehousePickingCodeDto
{
    public string Code { get; set; } = string.Empty;

    /// <summary>line：商品类码（主码、货号、多码、套装子码）；location：货位码或货位条码。</summary>
    public string Target { get; set; } = WarehousePickingCodeTargets.Line;

    public List<string> DetailGuids { get; set; } = new();
    public int? MatchedBy { get; set; }

    /// <summary>多码 / 套装子码对应的子项名称，便于扫码后提示“扫到了哪个子项”。</summary>
    public string? Label { get; set; }
}

public static class WarehousePickingCodeTargets
{
    public const string Line = "line";
    public const string Location = "location";
}

public sealed class WarehousePickingParticipantDto
{
    public string PickerUserGuid { get; set; } = string.Empty;
    public string PickerName { get; set; } = string.Empty;
    public DateTime LastActiveAtUtc { get; set; }
    public string? LastDetailGuid { get; set; }
}

public sealed class WarehousePickingSheetDto
{
    public string OrderGuid { get; set; } = string.Empty;
    public string? OrderNo { get; set; }
    public string? StoreCode { get; set; }
    public string? StoreName { get; set; }
    public DateTime? OrderDate { get; set; }
    public int FlowStatus { get; set; }
    public WarehousePickingSessionDto Session { get; set; } = new();
    public List<WarehousePickingLineDto> Lines { get; set; } = new();
    public List<WarehousePickingCodeDto> Codes { get; set; } = new();
    public List<WarehousePickingParticipantDto> Participants { get; set; } = new();
    public DateTime ServerTimeUtc { get; set; }
}

public sealed class WarehousePickingLineProgressDto
{
    public string DetailGuid { get; set; } = string.Empty;
    public int PickedTotal { get; set; }
    public List<WarehousePickedByDto> PickedBy { get; set; } = new();
    public int? MinOrderQuantity { get; set; }
}

public sealed class WarehousePickingProgressDto
{
    public WarehousePickingSessionDto Session { get; set; } = new();
    public List<WarehousePickingLineProgressDto> Lines { get; set; } = new();
    public List<WarehousePickingParticipantDto> Participants { get; set; } = new();
    public DateTime ServerTimeUtc { get; set; }
}

public sealed class WarehousePickingRecordRequestDto
{
    public string? DetailGuid { get; set; }

    /// <summary>1 扫码 / 2 按钮加一个中包 / 3 按钮减一个中包。</summary>
    public int Source { get; set; }

    public string? ScannedCode { get; set; }
    public int? MatchedBy { get; set; }

    /// <summary>显式件数（仅扫码或加一），用于“中包未设置时这次先按 1 件计入”；为空时按当前中包数累加。</summary>
    public int? Pieces { get; set; }

    public Guid ClientRequestId { get; set; }
}

public sealed class WarehousePickingSetTotalRequestDto
{
    public int Total { get; set; }
    public int ExpectedTotal { get; set; }
    public Guid ClientRequestId { get; set; }
}

public sealed class WarehousePickingLineMutationDto
{
    public WarehousePickingLineProgressDto Line { get; set; } = new();
    public int AppliedDelta { get; set; }
    public bool Duplicate { get; set; }
}

public sealed class WarehousePickingMinOrderQuantityRequestDto
{
    public int MinOrderQuantity { get; set; }
}

public sealed class WarehousePickingMinOrderQuantityResultDto
{
    public string ProductCode { get; set; } = string.Empty;
    public int MinOrderQuantity { get; set; }
}

public sealed class WarehousePickingSubmitResultDto
{
    public DateTime SubmittedAtUtc { get; set; }
    public string SubmittedByName { get; set; } = string.Empty;
    public int LineCount { get; set; }
    public int ShortLineCount { get; set; }
    public int OverLineCount { get; set; }
}

public sealed class WarehousePickingCodeLookupDto
{
    public string ProductCode { get; set; } = string.Empty;
    public string? ProductName { get; set; }
    public string? ItemNumber { get; set; }
    public string? Barcode { get; set; }
    public string? LocationCode { get; set; }
    public string? ProductImage { get; set; }
}
