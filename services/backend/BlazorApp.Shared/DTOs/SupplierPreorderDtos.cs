namespace BlazorApp.Shared.DTOs;

// 供应商节日预订单接口契约（前端类型在 apps/web/src/types/supplierPreorder.ts，两边字段一一对应，JSON 为 camelCase）。

#region 导入

/// <summary>列映射：值为解析结果 Rows[].Cells 的列下标（0 起）；null 表示原表没有这一列。</summary>
public sealed class SupplierPreorderColumnMappingDto
{
    public int? ItemCode { get; set; }
    public int? Name { get; set; }
    public int? Price { get; set; }
    public int? CartonQty { get; set; }
    /// <summary>进价备用列：主进价列为空时按行退回（Dats：PNP PRICE 为空时用 BULK PRICE）。</summary>
    public int? PriceFallback { get; set; }
    /// <summary>箱规备用列：主箱规列为空时按行退回（Dats：Inner 为空时用 Outer）。</summary>
    public int? CartonQtyFallback { get; set; }
    public int? Barcode { get; set; }
    public int? Unit { get; set; }
    public int? CartonCbm { get; set; }
    public int? Image { get; set; }
    public int? Category { get; set; }
    public int? PrevYearItemCode { get; set; }
    public int? Notes { get; set; }
    public int? Seq { get; set; }
    /// <summary>原表里填下单数量的列（如 KFC「Cartons Ordered」、Brazco「ORDER # CARTONS HERE」），填回原表时写合计。</summary>
    public int? OrderQty { get; set; }
}

public sealed class SupplierPreorderParsedColumnDto
{
    public int Index { get; set; }
    /// <summary>原表列字母（A、B…）；粘贴导入时为空。</summary>
    public string? Letter { get; set; }
    public string Header { get; set; } = string.Empty;
    public List<string> SampleValues { get; set; } = new();
}

public sealed class SupplierPreorderParsedRowDto
{
    /// <summary>原表行号（1 起）；粘贴导入时为粘贴内容里的行号。</summary>
    public int RowNo { get; set; }
    public List<string?> Cells { get; set; } = new();
}

public sealed class SupplierPreorderHeaderInfoDto
{
    public string? Title { get; set; }
    public string? QuoteNo { get; set; }
    public DateTime? OfferDate { get; set; }
    /// <summary>原表写的截止日期（只有日期，前端按悉尼时间当天 23:59 处理）。</summary>
    public DateTime? DeadlineDate { get; set; }
    public string? PriceTerms { get; set; }
    public string? ContainerType { get; set; }
    public int? SeasonYear { get; set; }
    public string? HolidayType { get; set; }
}

public sealed class SupplierPreorderParseResultDto
{
    /// <summary>上传文件已暂存的 Id（建表时带回，用于「填回原表」导出）；粘贴导入时为空。</summary>
    public string? SourceFileGuid { get; set; }
    public string? FileName { get; set; }
    public string SheetName { get; set; } = string.Empty;
    /// <summary>表头最后一行的行号（1 起）；粘贴导入时为表头在粘贴内容里的行号。</summary>
    public int HeaderRowNo { get; set; }
    public string HeaderSignature { get; set; } = string.Empty;
    public List<SupplierPreorderParsedColumnDto> Columns { get; set; } = new();
    public SupplierPreorderColumnMappingDto SuggestedMapping { get; set; } = new();
    /// <summary>建议的填回单位：Cartons / Pieces。</summary>
    public string SuggestedExportQtyUnit { get; set; } = "Cartons";
    /// <summary>映射来自该供应商上次保存的配置（而不是按表头同义词猜的）。</summary>
    public bool MappingFromProfile { get; set; }
    public List<SupplierPreorderParsedRowDto> Rows { get; set; } = new();
    public int SkippedRowCount { get; set; }
    public List<string> SkippedSamples { get; set; } = new();
    public SupplierPreorderHeaderInfoDto HeaderInfo { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public sealed class SupplierPreorderParsePastedRequestDto
{
    public string? LocalSupplierCode { get; set; }
    /// <summary>粘贴内容按行、按制表符拆好的二维数组（前端拆分，保留空单元格）。</summary>
    public List<List<string?>> Rows { get; set; } = new();
}

public sealed class SupplierPreorderPreviewRequestDto
{
    public string LocalSupplierCode { get; set; } = string.Empty;
    public SupplierPreorderColumnMappingDto Mapping { get; set; } = new();
    public List<SupplierPreorderParsedRowDto> Rows { get; set; } = new();
}

/// <summary>Matched 已匹配 / New 新品（商品表里没有）/ Warning 已匹配但有提示 / Invalid 不能导入（缺货号、名称、价格或箱规）。</summary>
public sealed class SupplierPreorderPreviewItemDto
{
    public int RowNo { get; set; }
    public int Seq { get; set; }
    public string SupplierItemCode { get; set; } = string.Empty;
    public string? Barcode { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Unit { get; set; }
    public int CartonQty { get; set; }
    public decimal? CartonCbm { get; set; }
    public decimal Price { get; set; }
    public string? Category { get; set; }
    public string? PrevYearItemCode { get; set; }
    public string? ImageUrl { get; set; }
    public string? Notes { get; set; }
    public string MatchMethod { get; set; } = "None";
    public string? MatchedProductCode { get; set; }
    public string? MatchedItemNumber { get; set; }
    public string? MatchedProductName { get; set; }
    public decimal? LastPurchasePrice { get; set; }
    /// <summary>与商品表进价相比的变动比例（0.059 = 上涨 5.9%）。</summary>
    public decimal? PriceChangeRate { get; set; }
    public string Status { get; set; } = "New";
    public List<string> Issues { get; set; } = new();
}

public sealed class SupplierPreorderPreviewCountsDto
{
    public int Total { get; set; }
    public int Matched { get; set; }
    public int New { get; set; }
    public int Warning { get; set; }
    public int Invalid { get; set; }
    public int PriceChanged { get; set; }
}

public sealed class SupplierPreorderPreviewResultDto
{
    public List<SupplierPreorderPreviewItemDto> Items { get; set; } = new();
    public SupplierPreorderPreviewCountsDto Counts { get; set; } = new();
}

public sealed class SupplierPreorderCreateFormDto
{
    public string LocalSupplierCode { get; set; } = string.Empty;
    public string HolidayType { get; set; } = "Christmas";
    public int SeasonYear { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? QuoteNo { get; set; }
    public DateTime? OfferDate { get; set; }
    public DateTime? DeadlineAtUtc { get; set; }
    public string? ContainerType { get; set; }
    public decimal? ContainerCapacityCbm { get; set; }
    public string? PriceTerms { get; set; }
    public DateTime? HistoryFrom { get; set; }
    public DateTime? HistoryTo { get; set; }
    public string ExportQtyUnit { get; set; } = "Cartons";
    public string? Notes { get; set; }
    public string? SourceFileGuid { get; set; }
    public string? SheetName { get; set; }
    public int HeaderRowNo { get; set; }
    public string? HeaderSignature { get; set; }
    public SupplierPreorderColumnMappingDto Mapping { get; set; } = new();
    public List<SupplierPreorderParsedRowDto> Rows { get; set; } = new();
}

#endregion

#region 订单表（管理）

public sealed class SupplierPreorderFormSummaryDto
{
    public string FormGuid { get; set; } = string.Empty;
    public string LocalSupplierCode { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public string HolidayType { get; set; } = string.Empty;
    public int SeasonYear { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? QuoteNo { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? DeadlineAtUtc { get; set; }
    public int ItemCount { get; set; }
    public int EnabledStoreCount { get; set; }
    public int SubmittedStoreCount { get; set; }
    /// <summary>已开启、有下单但未提交的分店数。</summary>
    public int DraftStoreCount { get; set; }
    public int TotalCartons { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal TotalCbm { get; set; }
    public bool HasSourceFile { get; set; }
    public string ExportQtyUnit { get; set; } = "Cartons";
    public DateTime CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
}

public sealed class SupplierPreorderFormDetailDto
{
    public SupplierPreorderFormSummaryDto Summary { get; set; } = new();
    public DateTime? OfferDate { get; set; }
    public string? ContainerType { get; set; }
    public decimal? ContainerCapacityCbm { get; set; }
    public string? PriceTerms { get; set; }
    public DateTime? HistoryFrom { get; set; }
    public DateTime? HistoryTo { get; set; }
    public string? Notes { get; set; }
    public int MatchedCount { get; set; }
    public int NewCount { get; set; }
    public int WarningCount { get; set; }
    public List<string> Categories { get; set; } = new();
}

public sealed class SupplierPreorderFormQueryDto
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public string? Keyword { get; set; }
    public string? Status { get; set; }
}

public sealed class SupplierPreorderUpdateFormDto
{
    public string Title { get; set; } = string.Empty;
    public string HolidayType { get; set; } = "Christmas";
    public int SeasonYear { get; set; }
    public string? QuoteNo { get; set; }
    public DateTime? OfferDate { get; set; }
    public DateTime? DeadlineAtUtc { get; set; }
    public string? ContainerType { get; set; }
    public decimal? ContainerCapacityCbm { get; set; }
    public string? PriceTerms { get; set; }
    public DateTime? HistoryFrom { get; set; }
    public DateTime? HistoryTo { get; set; }
    public string ExportQtyUnit { get; set; } = "Cartons";
    public string? Notes { get; set; }
}

public sealed class SupplierPreorderSetStatusDto
{
    /// <summary>Draft / Open / Closed。</summary>
    public string Status { get; set; } = string.Empty;
}

public sealed class SupplierPreorderFormItemQueryDto
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public string? Keyword { get; set; }
    /// <summary>all / matched / new / warning。</summary>
    public string? Match { get; set; }
}

public sealed class SupplierPreorderFormItemDto
{
    public string ItemGuid { get; set; } = string.Empty;
    public int Seq { get; set; }
    public string SupplierItemCode { get; set; } = string.Empty;
    public string? Barcode { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Unit { get; set; }
    public int CartonQty { get; set; }
    public decimal? CartonCbm { get; set; }
    public decimal Price { get; set; }
    public string? Category { get; set; }
    public string? ImageUrl { get; set; }
    public string? ImageSourceUrl { get; set; }
    public string MatchMethod { get; set; } = "None";
    public string? MatchedProductCode { get; set; }
    public string? MatchNote { get; set; }
    public int TotalCartons { get; set; }
}

public sealed class SupplierPreorderStoreRefDto
{
    public string StoreGuid { get; set; } = string.Empty;
    public string StoreCode { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;
}

/// <summary>订单表 × 分店：开启状态与下单进度。OrderStatus = None（未开始）/ Draft / Submitted。</summary>
public sealed class SupplierPreorderFormStoreStatusDto
{
    public string StoreGuid { get; set; } = string.Empty;
    public string StoreCode { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string OrderStatus { get; set; } = "None";
    public DateTime? SubmittedAtUtc { get; set; }
    public string? SubmittedBy { get; set; }
    public DateTime? LastEditedAtUtc { get; set; }
    public string? LastEditedBy { get; set; }
    public int ItemCount { get; set; }
    public int Cartons { get; set; }
    public int Pieces { get; set; }
    public decimal Amount { get; set; }
    public decimal Cbm { get; set; }
    /// <summary>下单人员授权里能替这家店下单的人（显示名）。</summary>
    public List<string> Orderers { get; set; } = new();
}

public sealed class SupplierPreorderUpdateFormStoresDto
{
    public List<string> StoreGuids { get; set; } = new();
}

public sealed class SupplierPreorderCopyStoresResultDto
{
    public string? SourceFormGuid { get; set; }
    public string? SourceTitle { get; set; }
    public List<SupplierPreorderFormStoreStatusDto> Stores { get; set; } = new();
}

public sealed class SupplierPreorderMatrixQueryDto
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public string? Keyword { get; set; }
    public bool OnlyOrdered { get; set; } = true;
}

public sealed class SupplierPreorderMatrixRowDto
{
    public string ItemGuid { get; set; } = string.Empty;
    public int Seq { get; set; }
    public string SupplierItemCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public decimal Price { get; set; }
    public int CartonQty { get; set; }
    public decimal? CartonCbm { get; set; }
    /// <summary>键为 StoreGuid，值为箱数；没下单的分店不出现。</summary>
    public Dictionary<string, int> StoreCartons { get; set; } = new();
    public int TotalCartons { get; set; }
    public int TotalPieces { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal TotalCbm { get; set; }
}

public sealed class SupplierPreorderMatrixTotalsDto
{
    public Dictionary<string, int> StoreCartons { get; set; } = new();
    public Dictionary<string, int> StorePieces { get; set; } = new();
    public Dictionary<string, decimal> StoreAmount { get; set; } = new();
    public Dictionary<string, decimal> StoreCbm { get; set; } = new();
    public int OrderedItemCount { get; set; }
    public int TotalCartons { get; set; }
    public int TotalPieces { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal TotalCbm { get; set; }
}

public sealed class SupplierPreorderMatrixDto
{
    public List<SupplierPreorderStoreRefDto> Stores { get; set; } = new();
    public PagedResult<SupplierPreorderMatrixRowDto> Rows { get; set; } = new();
    public SupplierPreorderMatrixTotalsDto Totals { get; set; } = new();
}

public sealed class SupplierPreorderSupplierOptionDto
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

#endregion

#region 下单人员授权

public sealed class SupplierPreorderOrdererDto
{
    public string UserGuid { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public bool IsActive { get; set; }
    public string? PrimaryStoreName { get; set; }
    public bool CanViewOtherStores { get; set; }
    public List<SupplierPreorderStoreRefDto> Stores { get; set; } = new();
    /// <summary>是否有「预订单下单」入口权限（SupplierPreorder.Order）；没有时看不到菜单。</summary>
    public bool HasOrderEntryPermission { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

public sealed class SupplierPreorderOrderersOverviewDto
{
    public List<SupplierPreorderOrdererDto> Orderers { get; set; } = new();
    /// <summary>启用的分店里还没有任何人可下单的。</summary>
    public List<SupplierPreorderStoreRefDto> StoresWithoutOrderer { get; set; } = new();
}

public sealed class SupplierPreorderSaveOrdererDto
{
    public List<string> StoreGuids { get; set; } = new();
    public bool CanViewOtherStores { get; set; }
}

public sealed class SupplierPreorderUserCandidateDto
{
    public string UserGuid { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public string? PrimaryStoreName { get; set; }
    public bool AlreadyOrderer { get; set; }
}

public sealed class SupplierPreorderAuthLogDto
{
    public string LogGuid { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string? TargetUserName { get; set; }
    public string? FormGuid { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public string? OperatorName { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class SupplierPreorderAuthLogQueryDto
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public string? UserGuid { get; set; }
    public string? FormGuid { get; set; }
}

#endregion

#region 下单

public sealed class SupplierPreorderMyStoreDto
{
    public string StoreGuid { get; set; } = string.Empty;
    public string StoreCode { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;
    public bool CanEdit { get; set; }
    /// <summary>不能编辑时的原因（中文），如「已过截止时间」「该订单表未开启本店」。</summary>
    public string? ReadOnlyReason { get; set; }
    public string OrderStatus { get; set; } = "None";
    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? LastEditedAtUtc { get; set; }
    public string? LastEditedBy { get; set; }
    public int Cartons { get; set; }
    public decimal Amount { get; set; }
}

public sealed class SupplierPreorderOrderingFormDto
{
    public string FormGuid { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public string HolidayType { get; set; } = string.Empty;
    public int SeasonYear { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? QuoteNo { get; set; }
    public DateTime? OfferDate { get; set; }
    public DateTime? DeadlineAtUtc { get; set; }
    public string? ContainerType { get; set; }
    public decimal? ContainerCapacityCbm { get; set; }
    public string? PriceTerms { get; set; }
    public int ItemCount { get; set; }
    /// <summary>对普通下单人是否已锁定（已截止或已过截止时间）。</summary>
    public bool IsLocked { get; set; }
    public List<SupplierPreorderMyStoreDto> MyStores { get; set; } = new();
}

public sealed class SupplierPreorderOrderingContextDto
{
    public SupplierPreorderOrderingFormDto Form { get; set; } = new();
    public bool IsManager { get; set; }
    public bool CanViewOtherStores { get; set; }
    public List<string> Categories { get; set; } = new();
    public DateTime ServerNowUtc { get; set; }
}

public sealed class SupplierPreorderStoreCbmDto
{
    public string StoreName { get; set; } = string.Empty;
    public decimal Cbm { get; set; }
}

public sealed class SupplierPreorderOrderingSummaryDto
{
    public string StoreGuid { get; set; } = string.Empty;
    public string OrderStatus { get; set; } = "None";
    public int ItemCount { get; set; }
    public int Cartons { get; set; }
    public int Pieces { get; set; }
    public decimal Amount { get; set; }
    public decimal Cbm { get; set; }
    public DateTime? LastEditedAtUtc { get; set; }
    public string? LastEditedBy { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    /// <summary>全部分店合计体积（所有下单人都能看到合计）。</summary>
    public decimal AllStoresCbm { get; set; }
    /// <summary>各分店体积；只有可查看其他分店的人才返回，否则为 null。</summary>
    public List<SupplierPreorderStoreCbmDto>? StoreCbms { get; set; }
}

public sealed class SupplierPreorderOrderingItemQueryDto
{
    public string StoreGuid { get; set; } = string.Empty;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public string? Keyword { get; set; }
    public string? Category { get; set; }
    /// <summary>all / mine（本店已下单）/ others（其他分店已下单）/ new（新品）/ matched（商品表已有）。</summary>
    public string? Filter { get; set; }
    /// <summary>seq / priceAsc / priceDesc / othersDesc（其他分店合计箱数多的在前）。</summary>
    public string? Sort { get; set; }
}

public sealed class SupplierPreorderOtherStoreCartonsDto
{
    public string StoreName { get; set; } = string.Empty;
    public int Cartons { get; set; }
}

public sealed class SupplierPreorderOrderingItemDto
{
    public string ItemGuid { get; set; } = string.Empty;
    public int Seq { get; set; }
    public string SupplierItemCode { get; set; } = string.Empty;
    public string? Barcode { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Unit { get; set; }
    public int CartonQty { get; set; }
    public decimal? CartonCbm { get; set; }
    public decimal Price { get; set; }
    public string? Category { get; set; }
    public string? ImageUrl { get; set; }
    public string? ImageSourceUrl { get; set; }
    public bool IsNew { get; set; }
    public int MyCartons { get; set; }
    /// <summary>其他分店已下单；不能查看其他分店时为 null。</summary>
    public List<SupplierPreorderOtherStoreCartonsDto>? OtherStores { get; set; }
}

public sealed class SupplierPreorderSaveLineDto
{
    public int Cartons { get; set; }
}

public sealed class SupplierPreorderSaveLineResultDto
{
    public string ItemGuid { get; set; } = string.Empty;
    public int Cartons { get; set; }
    public SupplierPreorderOrderingSummaryDto Summary { get; set; } = new();
}

#endregion
