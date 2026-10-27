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
    /// <summary>导入校对页人工指定的参考商品（按原表行号）；ProductCode 为空表示「不要参考」。</summary>
    public List<SupplierPreorderRefOverrideDto>? RefOverrides { get; set; }
}

public sealed class SupplierPreorderRefOverrideDto
{
    public int RowNo { get; set; }
    public string? ProductCode { get; set; }
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
    /// <summary>去年历史快照的计算状态与窗口。</summary>
    public SupplierPreorderHistoryStatusDto History { get; set; } = new();
    /// <summary>本品自己在窗口内有销量的款数。</summary>
    public int OwnHistoryCount { get; set; }
    public int RefAutoCount { get; set; }
    public int RefManualCount { get; set; }
    /// <summary>本品没有销量、也没有参考商品的款数（需要采购看一眼）。</summary>
    public int NeedRefCount { get; set; }
    /// <summary>新品图片转存 COS 的进度。</summary>
    public SupplierPreorderImageCopyStatusDto ImageCopy { get; set; } = new();
    /// <summary>修订版次数（0 = 原始导入）与最近一次修订时间。</summary>
    public int Revision { get; set; }
    public DateTime? RevisedAtUtc { get; set; }
    /// <summary>已下架（修订版里被删掉）的款数。</summary>
    public int RemovedCount { get; set; }
}

/// <summary>新品图片转存进度：Total = 有原表图片地址的款数；Skipped = 有我方商品图不需要转存；Pending = 还没处理或失败后等重试。</summary>
public sealed class SupplierPreorderImageCopyStatusDto
{
    public int Total { get; set; }
    public int Done { get; set; }
    public int Skipped { get; set; }
    public int Pending { get; set; }
    /// <summary>已失败 3 次、不再自动重试的款数（可点「重试失败」）。</summary>
    public int Failed { get; set; }
    /// <summary>后台没配 COS 图片桶时为 false（页面继续用原表图片地址）。</summary>
    public bool Configured { get; set; } = true;
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
    /// <summary>all / matched / new / warning / needRef（没有去年销量也没有参考）/ ref（有参考商品）/
    /// revised（最近一次修订有变动）/ removed（已下架）。</summary>
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
    /// <summary>本品自己在历史窗口内（全部分店）的销量；快照未算出时为 null。</summary>
    public decimal? OwnSalesQty { get; set; }
    public SupplierPreorderItemRefDto? Ref { get; set; }
    /// <summary>false = 已下架（修订版里被删掉，保留已下的箱数但不计入汇总）。</summary>
    public bool IsActive { get; set; } = true;
    public SupplierPreorderRevisionMarkDto? RevisionMark { get; set; }
}

/// <summary>
/// 最近一次修订对这款商品的改动（只标最近一次；以前的修订不再高亮）。
/// Changes：Added 新增 / Restored 重新上架 / Removed 下架 / Price 改价 / Carton 改箱规 / Name 改名 / Barcode 改条码 / Image 换图。
/// </summary>
public sealed class SupplierPreorderRevisionMarkDto
{
    public int Revision { get; set; }
    public List<string> Changes { get; set; } = new();
    public decimal? PrevPrice { get; set; }
    public int? PrevCartonQty { get; set; }
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
    /// <summary>去年历史快照状态、窗口与节日关键日期（画迷你曲线用）。</summary>
    public SupplierPreorderHistoryStatusDto History { get; set; } = new();
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
    /// <summary>本店下过、但已被修订版下架的商品（箱数保留，不计入汇总）。</summary>
    public List<SupplierPreorderRemovedLineDto> RemovedLines { get; set; } = new();
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
    /// <summary>all / mine（本店已下单）/ others（其他分店已下单）/ new（新品）/ matched（商品表已有）/
    /// lastYearSold（本品去年本店有售，不含参考）/ lastYearDiscounted（本品去年本店打过折，不含参考）/ revised（最近一次修订有变动）。</summary>
    public string? Filter { get; set; }
    /// <summary>seq / priceAsc / priceDesc / othersDesc（其他分店合计箱数多的在前）/ lastYearSalesDesc（去年本店销量，含参考）。</summary>
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
    /// <summary>去年本店数据（本品或参考商品）；null = 无去年记录（或快照还没算出来）。</summary>
    public SupplierPreorderItemHistoryDto? History { get; set; }
    /// <summary>最近一次修订的改动（价格、箱规等），没有为 null。</summary>
    public SupplierPreorderRevisionMarkDto? RevisionMark { get; set; }
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

#region 去年历史与参考商品（PR 2）

public sealed class SupplierPreorderKeyDateDto
{
    public DateTime Date { get; set; }
    /// <summary>Halloween / BlackFriday / ChristmasEve / Christmas / BoxingDay / Easter。</summary>
    public string Key { get; set; } = string.Empty;
}

public sealed class SupplierPreorderDateRangeDto
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
}

/// <summary>订单表的历史快照状态。Status：None 还没排队 / Queued 排队中 / Running 计算中 / Ready 已完成 / Failed 失败 / NoWindow 没有历史窗口。</summary>
public sealed class SupplierPreorderHistoryStatusDto
{
    public string Status { get; set; } = "None";
    public DateTime? ComputedAtUtc { get; set; }
    public DateTime? RequestedAtUtc { get; set; }
    public string? Error { get; set; }
    public DateTime? WindowFrom { get; set; }
    public DateTime? WindowTo { get; set; }
    public List<SupplierPreorderKeyDateDto> KeyDates { get; set; } = new();
}

/// <summary>商品挂的参考商品（管理端、下单端共用）。</summary>
public sealed class SupplierPreorderItemRefDto
{
    /// <summary>Auto 系统推荐 / Manual 人工指定 / Cleared 人工清除（ProductCode 为空）。</summary>
    public string Source { get; set; } = string.Empty;
    public string? ProductCode { get; set; }
    public string? ItemNumber { get; set; }
    public string? Barcode { get; set; }
    public string? Name { get; set; }
    public decimal? PurchasePrice { get; set; }
    public string? ImageUrl { get; set; }
    public decimal? Score { get; set; }
    public string? SetBy { get; set; }
    public DateTime? SetAtUtc { get; set; }
    /// <summary>参考商品在历史窗口内（全部分店）的销量。</summary>
    public decimal? SalesQty { get; set; }
}

/// <summary>
/// 下单列表里一行商品的去年本店数据。Source = Own 本品自己的 / Ref 借用参考商品的（整格紫色、标「参考 XXX」）。
/// HasStoreData = false 表示这个商品去年在别的分店有记录、本店没有。
/// Daily 为稀疏数组：[距窗口起点天数, 净销量, 折扣销量]。
/// </summary>
public sealed class SupplierPreorderItemHistoryDto
{
    public string Source { get; set; } = "Own";
    public string ProductCode { get; set; } = string.Empty;
    public SupplierPreorderItemRefDto? Ref { get; set; }
    public bool HasStoreData { get; set; }
    public DateTime? PurchaseFirst { get; set; }
    public DateTime? PurchaseLast { get; set; }
    public int PurchaseBatches { get; set; }
    public decimal PurchaseQty { get; set; }
    public DateTime? SalesFirst { get; set; }
    public DateTime? SalesLast { get; set; }
    public decimal SalesQty { get; set; }
    public decimal? SellThrough { get; set; }
    /// <summary>complete / partial / unknown / none。</summary>
    public string DiscountStatus { get; set; } = "none";
    public bool HasDiscount { get; set; }
    public decimal? DiscountShare { get; set; }
    public List<SupplierPreorderDateRangeDto> DiscountRanges { get; set; } = new();
    public List<decimal[]> Daily { get; set; } = new();
}

public sealed class SupplierPreorderHistoryBatchDto
{
    public DateTime Date { get; set; }
    /// <summary>warehouse 总仓配货 / supplier 供应商直送（分店进货单）。</summary>
    public string Source { get; set; } = "warehouse";
    public decimal Quantity { get; set; }
    public string? DocumentNo { get; set; }
}

/// <summary>某家分店去年的数据。展开行的本店用全量（含 Daily、Batches）；各分店对比只填汇总数字。</summary>
public sealed class SupplierPreorderStoreHistoryDto
{
    public string StoreCode { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;
    public bool IsCurrent { get; set; }
    public bool HasData { get; set; }
    public DateTime? PurchaseFirst { get; set; }
    public DateTime? PurchaseLast { get; set; }
    public int PurchaseBatches { get; set; }
    public decimal PurchaseQty { get; set; }
    public decimal WarehouseQty { get; set; }
    public decimal SupplierQty { get; set; }
    public DateTime? SalesFirst { get; set; }
    public DateTime? SalesLast { get; set; }
    public decimal SalesQty { get; set; }
    public decimal? SellThrough { get; set; }
    public string DiscountStatus { get; set; } = "none";
    public bool HasDiscount { get; set; }
    public decimal? DiscountShare { get; set; }
    public decimal? RetailPrice { get; set; }
    public decimal? DiscountPriceMin { get; set; }
    public List<SupplierPreorderDateRangeDto> DiscountRanges { get; set; } = new();
    public List<decimal[]> Daily { get; set; } = new();
    public List<SupplierPreorderHistoryBatchDto> Batches { get; set; } = new();
    /// <summary>今年这家店已订箱数（只算开启分店）；对比表用。</summary>
    public int? ThisYearCartons { get; set; }
}

/// <summary>展开行：本店大图 + 小结 + 各分店去年对比（不能看其他分店时 Stores 为 null）。</summary>
public sealed class SupplierPreorderItemHistoryDetailDto
{
    public string ItemGuid { get; set; } = string.Empty;
    /// <summary>Own / Ref / None。</summary>
    public string Source { get; set; } = "None";
    public string? ProductCode { get; set; }
    public SupplierPreorderItemRefDto? Ref { get; set; }
    public DateTime? WindowFrom { get; set; }
    public DateTime? WindowTo { get; set; }
    public List<SupplierPreorderKeyDateDto> KeyDates { get; set; } = new();
    public SupplierPreorderStoreHistoryDto? Store { get; set; }
    /// <summary>去年本店销量 ÷ 箱规（只作参考，不自动填）。</summary>
    public int? SuggestedCartons { get; set; }
    public List<SupplierPreorderStoreHistoryDto>? Stores { get; set; }
}

/// <summary>参考商品候选（推荐或搜索结果）。</summary>
public sealed class SupplierPreorderRefCandidateDto
{
    public string ProductCode { get; set; } = string.Empty;
    public string? ItemNumber { get; set; }
    public string? Barcode { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal? PurchasePrice { get; set; }
    public string? ImageUrl { get; set; }
    public string? LocalSupplierCode { get; set; }
    public decimal SalesQty { get; set; }
    public decimal PurchaseQty { get; set; }
    public decimal Score { get; set; }
    public decimal NameScore { get; set; }
    public decimal PriceScore { get; set; }
    /// <summary>是否过了两个硬门槛（名称 ≥ 0.5、进价差 &lt; 30%）。</summary>
    public bool Eligible { get; set; }
    /// <summary>窗口内没有销量也没有进货：选它也看不到数据。</summary>
    public bool HasHistory { get; set; }
}

public sealed class SupplierPreorderRefCandidatesDto
{
    public DateTime? WindowFrom { get; set; }
    public DateTime? WindowTo { get; set; }
    /// <summary>同供应商、窗口内有记录、过了门槛的前 3 个（没有关键词时）。</summary>
    public List<SupplierPreorderRefCandidateDto> Recommended { get; set; } = new();
    /// <summary>按货号 / 条码 / 名称搜索的结果（有关键词时，不限供应商）。</summary>
    public List<SupplierPreorderRefCandidateDto> SearchResults { get; set; } = new();
}

public sealed class SupplierPreorderSetRefDto
{
    /// <summary>Manual 指定（ProductCode 必填）/ Clear 清除且不再自动带上 / Auto 交回系统推荐（下次计算时重新挑）。</summary>
    public string Mode { get; set; } = "Manual";
    public string? ProductCode { get; set; }
}

/// <summary>导入校对页（订单表还没建）找参考商品：按将要建的订单表的供应商与历史窗口打分。</summary>
public sealed class SupplierPreorderImportRefCandidatesRequestDto
{
    public string LocalSupplierCode { get; set; } = string.Empty;
    public string HolidayType { get; set; } = "Christmas";
    public int SeasonYear { get; set; }
    public DateTime? HistoryFrom { get; set; }
    public DateTime? HistoryTo { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string? Keyword { get; set; }
    /// <summary>已匹配给本订单表其他行的我方商品，不作参考候选。</summary>
    public List<string>? ExcludeProductCodes { get; set; }
}

#endregion

#region 修订版与提醒（PR 3）

public sealed class SupplierPreorderRemovedLineDto
{
    public string ItemGuid { get; set; } = string.Empty;
    public string SupplierItemCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Cartons { get; set; }
}

/// <summary>上传修订版：解析结果（parse-file / parse-pasted 的输出）+ 列映射。SourceFileGuid 为空（粘贴）时不再支持填回原表。</summary>
public sealed class SupplierPreorderRevisionRequestDto
{
    public string? SourceFileGuid { get; set; }
    public string? SheetName { get; set; }
    public int HeaderRowNo { get; set; }
    public string? HeaderSignature { get; set; }
    public SupplierPreorderColumnMappingDto Mapping { get; set; } = new();
    public List<SupplierPreorderParsedRowDto> Rows { get; set; } = new();
}

/// <summary>修订版里一款商品的差异。Kind：Added / Removed / Restored（之前下架又出现）/ Changed / Unchanged。</summary>
public sealed class SupplierPreorderRevisionEntryDto
{
    public string Kind { get; set; } = "Unchanged";
    public string SupplierItemCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ItemGuid { get; set; }
    public int? RowNo { get; set; }
    /// <summary>Price / Carton / Name / Barcode / Image / Cbm / Category。</summary>
    public List<string> Changes { get; set; } = new();
    public decimal? OldPrice { get; set; }
    public decimal? NewPrice { get; set; }
    public int? OldCartonQty { get; set; }
    public int? NewCartonQty { get; set; }
    public string? OldName { get; set; }
    /// <summary>各开启分店已订合计箱数（下架、改箱规时提醒采购）。</summary>
    public int OrderedCartons { get; set; }
    /// <summary>已订了这款的分店名。</summary>
    public List<string> OrderedStores { get; set; } = new();
}

public sealed class SupplierPreorderRevisionCountsDto
{
    public int Total { get; set; }
    public int Added { get; set; }
    public int Removed { get; set; }
    public int Restored { get; set; }
    public int Changed { get; set; }
    public int PriceChanged { get; set; }
    public int CartonChanged { get; set; }
    public int Unchanged { get; set; }
    /// <summary>新表里不能导入（缺货号 / 名称 / 进价 / 箱规）的行，不参与对比。</summary>
    public int Invalid { get; set; }
    /// <summary>下架的商品里已有分店下单的款数。</summary>
    public int RemovedWithOrders { get; set; }
}

public sealed class SupplierPreorderRevisionPreviewDto
{
    public int CurrentRevision { get; set; }
    public SupplierPreorderRevisionCountsDto Counts { get; set; } = new();
    /// <summary>只列有变化的（Added / Removed / Restored / Changed），按原表顺序。</summary>
    public List<SupplierPreorderRevisionEntryDto> Entries { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

/// <summary>提醒未提交分店的结果。</summary>
public sealed class SupplierPreorderRemindResultDto
{
    /// <summary>没提交的开启分店数。</summary>
    public int PendingStoreCount { get; set; }
    /// <summary>实际发出提醒的人数。</summary>
    public int NotifiedUserCount { get; set; }
    /// <summary>没提交、但下单人员授权里没人能替它下单的分店（提醒不到，需采购另行联系）。</summary>
    public List<string> StoresWithoutOrderers { get; set; } = new();
}

#endregion
