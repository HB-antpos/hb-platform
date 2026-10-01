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

    /// <summary>经理派的负责人与各自行数；没有分配时为空列表。</summary>
    public List<WarehousePickingAssigneeDto> Assignees { get; set; } = new();
}

/// <summary>一段的负责人；PickerUserGuid 为空表示待员工扫分单领取。</summary>
public sealed class WarehousePickingAssigneeDto
{
    public string? PickerUserGuid { get; set; }
    public string? PickerName { get; set; }
    public int LineCount { get; set; }

    /// <summary>第几段（从 1 起）。</summary>
    public int SegmentNo { get; set; }

    /// <summary>该段分单条码（HBSP:订单号/段号/版本）；订单列表里不下发。</summary>
    public string? SlipCode { get; set; }

    // 以下为订单详情“拣货分配”卡片用的进度，订单列表里不下发。
    public string? FirstLocation { get; set; }
    public string? LastLocation { get; set; }
    public decimal? Pieces { get; set; }
    public int? PickedPieces { get; set; }

    /// <summary>已拣齐（含超拣）的品种数。</summary>
    public int? CompletedLineCount { get; set; }

    public int? StockoutLineCount { get; set; }

    /// <summary>负责人在本单最后一次拣货操作时间；还没扫分单开始拣时为空。</summary>
    public DateTime? LastActiveAtUtc { get; set; }
}

public sealed class WarehousePickingAssignmentLineDto
{
    public string DetailGuid { get; set; } = string.Empty;
    public int SegmentNo { get; set; }
}

public sealed class WarehousePickingAssignmentSummariesRequestDto
{
    public List<string> OrderGuids { get; set; } = new();
}

public sealed class WarehousePickingOrderCountsDto
{
    public int All { get; set; }
    public int ToPick { get; set; }
    public int Picking { get; set; }

    /// <summary>派给当前拣货人的订单数；列表请求认不出拣货人（设备未扫员工码）时为空。</summary>
    public int? Mine { get; set; }
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

    /// <summary>仍有效的“货位没货”标记；没有标记时为空（序列化时省略）。</summary>
    public WarehousePickingStockoutDto? Stockout { get; set; }

    /// <summary>经理派的负责人；没有分配时为空。</summary>
    public string? AssigneeUserGuid { get; set; }
    public string? AssigneeName { get; set; }

    /// <summary>所属分段（扫分单后“我的”范围按段号过滤）。</summary>
    public int? AssignmentSegmentNo { get; set; }
}

/// <summary>“货位没货”标记：原因见 WarehouseOrderPickStockoutReasons，谁在什么时候标的、标记时已拣多少。</summary>
public sealed class WarehousePickingStockoutDto
{
    public int Reason { get; set; }
    public string MarkedByName { get; set; } = string.Empty;
    public DateTime MarkedAtUtc { get; set; }
    public int PickedAtMark { get; set; }
}

public sealed class WarehousePickingStockoutRequestDto
{
    /// <summary>1 货位空了 / 2 放的不是这个商品 / 3 有货但破损。</summary>
    public int Reason { get; set; }
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

    /// <summary>随进度轮询下发，一起拣的同事能看到谁把哪一行标成了没货。</summary>
    public WarehousePickingStockoutDto? Stockout { get; set; }

    /// <summary>随进度轮询下发，经理改派后拣货页能同步。</summary>
    public string? AssigneeUserGuid { get; set; }
    public string? AssigneeName { get; set; }
    public int? AssignmentSegmentNo { get; set; }
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

// ---- 拣货分配（仓库经理派单） ----

public sealed class WarehousePickingPickerCandidateDto
{
    public string PickerUserGuid { get; set; } = string.Empty;
    public string PickerName { get; set; } = string.Empty;
    public string? RoleLabel { get; set; }

    /// <summary>手上已派、仍在拣货中的订单数，帮经理平衡工作量。</summary>
    public int ActiveOrderCount { get; set; }
}

/// <summary>一段的输入：员工可空（空＝打印分单后由员工扫码领取）。</summary>
public sealed class WarehousePickingAssignmentPickerInputDto
{
    public string? PickerUserGuid { get; set; }

    /// <summary>该员工分几个品种；全部为空时按品种数平均分。</summary>
    public int? LineCount { get; set; }
}

public sealed class WarehousePickingAssignmentPreviewRequestDto
{
    public List<WarehousePickingAssignmentPickerInputDto> Pickers { get; set; } = new();
}

public sealed class WarehousePickingAssignmentSegmentDto
{
    public string? PickerUserGuid { get; set; }
    public string? PickerName { get; set; }
    public int LineCount { get; set; }
    public decimal Pieces { get; set; }

    /// <summary>这一段在走位顺序上的第一个与最后一个有货位的行；整段都没货位时为空。</summary>
    public string? FirstLocation { get; set; }
    public string? LastLocation { get; set; }

    public int UnlocatedLineCount { get; set; }
    public int IrregularLineCount { get; set; }
    public List<string> DetailGuids { get; set; } = new();
}

public sealed class WarehousePickingAssignmentPreviewDto
{
    public int LineCount { get; set; }
    public decimal Pieces { get; set; }
    public int UnlocatedLineCount { get; set; }
    public int IrregularLineCount { get; set; }
    public List<WarehousePickingAssignmentSegmentDto> Segments { get; set; } = new();
}

public sealed class WarehousePickingAssignmentInputDto
{
    public string? PickerUserGuid { get; set; }
    public List<string> DetailGuids { get; set; } = new();
}

public sealed class WarehousePickingAssignmentSaveRequestDto
{
    public List<WarehousePickingAssignmentInputDto> Assignments { get; set; } = new();
}

public sealed class WarehousePickingAssignmentSummaryDto
{
    public string OrderGuid { get; set; } = string.Empty;
    public int LineCount { get; set; }
    public int UnassignedLineCount { get; set; }
    public List<WarehousePickingAssigneeDto> Assignees { get; set; } = new();
    public string? AssignedByName { get; set; }
    public DateTime? AssignedAtUtc { get; set; }

    /// <summary>每行所属段，订单明细表“负责人”列用。</summary>
    public List<WarehousePickingAssignmentLineDto> Lines { get; set; } = new();
}

public sealed class WarehousePickingBatchAssignRequestDto
{
    public List<string> OrderGuids { get; set; } = new();

    /// <summary>指定员工时按员工人数分段；为空时按 SegmentCount 分段、全部待扫码领取。</summary>
    public List<string> PickerUserGuids { get; set; } = new();

    public int? SegmentCount { get; set; }
}

public sealed class WarehousePickingBatchAssignItemDto
{
    public string OrderGuid { get; set; } = string.Empty;
    public string? OrderNo { get; set; }
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? Message { get; set; }
}

public sealed class WarehousePickingBatchAssignResultDto
{
    public List<WarehousePickingBatchAssignItemDto> Items { get; set; } = new();
}

/// <summary>PDA 扫分单条码的解析结果：打开哪张单、看哪一段。</summary>
public sealed class WarehousePickingSlipResolveDto
{
    public string OrderGuid { get; set; } = string.Empty;
    public string? OrderNo { get; set; }
    public int SegmentNo { get; set; }
    public int SegmentCount { get; set; }

    /// <summary>该段当前负责人；为空表示还没人领取。</summary>
    public string? PickerUserGuid { get; set; }
    public string? PickerName { get; set; }
    public int LineCount { get; set; }

    /// <summary>扫码人就是该段负责人（刚领取或早已领取）。</summary>
    public bool ClaimedByMe { get; set; }

    /// <summary>这次扫码刚把该段领到扫码人名下。</summary>
    public bool ClaimedNow { get; set; }
}

public sealed class WarehousePickingSlipClaimRequestDto
{
    public string? Code { get; set; }
}

/// <summary>经理改某一段的负责人；为空表示释放，回到待领取。</summary>
public sealed class WarehousePickingSegmentPickerRequestDto
{
    public string? PickerUserGuid { get; set; }
}

public sealed class WarehousePickingSlipLineDto
{
    public string DetailGuid { get; set; } = string.Empty;
    public string? LocationCode { get; set; }

    /// <summary>货位所在区与排（如 A、03），打印时换排处插提示行；无货位或编码不规范时为空。</summary>
    public string? Zone { get; set; }
    public string? RowLabel { get; set; }

    public string? ItemNumber { get; set; }
    public string? ProductName { get; set; }
    public string? Barcode { get; set; }
    public decimal OrderedQuantity { get; set; }
    public int? MinOrderQuantity { get; set; }
}

/// <summary>一张分单拣货单（每人一页）。</summary>
public sealed class WarehousePickingSlipDto
{
    public int SegmentNo { get; set; }
    public int SegmentCount { get; set; }
    public string SlipCode { get; set; } = string.Empty;

    /// <summary>负责人；为空时分单上印“待领取”，员工扫码领取。</summary>
    public string? PickerUserGuid { get; set; }
    public string? PickerName { get; set; }
    public int LineCount { get; set; }
    public decimal Pieces { get; set; }
    public string? FirstLocation { get; set; }
    public string? LastLocation { get; set; }
    public List<string> OtherPickerNames { get; set; } = new();
    public List<WarehousePickingSlipLineDto> Lines { get; set; } = new();
}

public sealed class WarehousePickingSlipsDto
{
    public string OrderGuid { get; set; } = string.Empty;
    public string? OrderNo { get; set; }
    public string? StoreCode { get; set; }
    public string? StoreName { get; set; }
    public DateTime? OrderDate { get; set; }
    public string? AssignedByName { get; set; }
    public DateTime? AssignedAtUtc { get; set; }
    public List<WarehousePickingSlipDto> Slips { get; set; } = new();
}
