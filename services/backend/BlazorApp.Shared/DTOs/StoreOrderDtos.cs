using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace BlazorApp.Shared.DTOs
{
  /// <summary>
  /// 订货页面商品DTO
  /// </summary>
  public class StoreOrderProductDto
  {
    public string ProductCode { get; set; } = string.Empty;
    public string? ItemNumber { get; set; }
    public string? Barcode { get; set; }
    public string? ProductName { get; set; }
    public string? ProductImage { get; set; }
    public string? CategoryName { get; set; }
    public string? WarehouseCategoryGUID { get; set; }
    public string? LocalSupplierCode { get; set; }
    public string? LocalSupplierName { get; set; }
    public string? DomesticSupplierCode { get; set; }
    public string? DomesticSupplierName { get; set; }

    /// <summary>
    /// 零售价 (订货价格)
    /// </summary>
    public decimal? OEMPrice { get; set; }

    /// <summary>
    /// 最小订货量
    /// </summary>
    public int MinOrderQuantity { get; set; } = 1;

    /// <summary>
    /// 库存数量
    /// </summary>
    public int StockQuantity { get; set; }

    /// <summary>
    /// 是否有库存
    /// </summary>
    public bool IsInStock => StockQuantity > 0;

    /// <summary>
    /// 包装数量
    /// </summary>
    public int? PackQty { get; set; }

    /// <summary>
    /// 进口价格 (参考)
    /// </summary>
    public decimal? ImportPrice { get; set; }

    public string? Grade { get; set; }
  }

  /// <summary>
  /// 订货页面查询过滤DTO
  /// </summary>
  public class StoreOrderFilterDto
  {
    /// <summary>
    /// 分店代码（用于缓存分区；商品列表仍为仓库维度时可选不传）
    /// </summary>
    public string? StoreCode { get; set; }

    /// <summary>
    /// 货号 (精确或模糊匹配)
    /// </summary>
    public string? ItemNumber { get; set; }

    /// <summary>
    /// 商品名称 (模糊匹配)
    /// </summary>
    public string? ProductName { get; set; }

    /// <summary>
    /// 分类GUID
    /// </summary>
    public string? CategoryGUID { get; set; }

    /// <summary>
    /// 澳洲供应商代码过滤。
    /// </summary>
    public string? LocalSupplierCode { get; set; }

    /// <summary>
    /// 国内供应商代码过滤。
    /// </summary>
    public string? SupplierCode { get; set; }

    /// <summary>
    /// 是否排除仓库库存表中已有未删除记录的商品。
    /// </summary>
    public bool ExcludeExistingWarehouseProducts { get; set; }

    /// <summary>
    /// 后台订货快速加入专用：允许查询上下架商品，但仍必须排除已删除商品。
    /// </summary>
    public bool IncludeInactiveWarehouseProducts { get; set; } = false;

    /// <summary>
    /// 排除指定订货单中已有未删除明细的商品。
    /// </summary>
    public string? ExcludeOrderGUID { get; set; }

    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 24;

    /// <summary>
    /// 排序字段: Default, PriceAsc, PriceDesc, Name
    /// </summary>
    public string SortBy { get; set; } = "Default";

    /// <summary>
    /// 列排序方向；旧调用未传时保持升序默认，PriceDesc 等兼容排序仍按原语义处理。
    /// </summary>
    public bool SortDescending { get; set; }

    public string? Grade { get; set; }

    /// <summary>
    /// 商品选择弹窗列头筛选条件。
    /// </summary>
    public StoreOrderProductColumnFiltersDto? ColumnFilters { get; set; }
  }

  /// <summary>
  /// 订货页面商品列表列头筛选条件。
  /// </summary>
  public class StoreOrderProductColumnFiltersDto
  {
    public string? ItemNumber { get; set; }
    public string? ProductName { get; set; }
    public string? SupplierKeyword { get; set; }
    public string? Barcode { get; set; }
    public int? StockQuantityMin { get; set; }
    public int? StockQuantityMax { get; set; }
    public int? MinOrderQuantityMin { get; set; }
    public int? MinOrderQuantityMax { get; set; }
    public decimal? ImportPriceMin { get; set; }
    public decimal? ImportPriceMax { get; set; }
  }

  /// <summary>
  /// 批量查询订货商品请求
  /// </summary>
  public class StoreOrderBatchLookupRequestDto
  {
    public List<string> Codes { get; set; } = new();
  }

  /// <summary>
  /// 批量查询订货商品结果
  /// </summary>
  public class StoreOrderBatchLookupItemDto
  {
    public string LookupCode { get; set; } = string.Empty;
    public StoreOrderProductDto? Product { get; set; }
  }

  /// <summary>
  /// 扫码查询订货商品请求
  /// </summary>
  public class StoreOrderScanLookupRequestDto
  {
    [Required]
    public string Barcode { get; set; } = string.Empty;

    public string? StoreCode { get; set; }
  }

  /// <summary>
  /// 扫码查询订货商品结果
  /// </summary>
  public class StoreOrderScanLookupResultDto
  {
    public string Barcode { get; set; } = string.Empty;
    public string? MatchType { get; set; }
    public List<StoreOrderProductDto> Items { get; set; } = new();
  }

  /// <summary>
  /// 发票邮件最近一次成功发送信息。
  /// </summary>
  public class StoreOrderInvoiceEmailSentInfoDto
  {
    public bool HasSent { get; set; }
    public DateTime? SentAt { get; set; }
    public string? ToEmail { get; set; }
    public string? JobId { get; set; }
  }

  /// <summary>
  /// 购物车DTO (FlowStatus=0 的 WareHouseOrder)
  /// </summary>
  public class StoreOrderCartDto
  {
    public string OrderGUID { get; set; } = string.Empty;
    public string? OrderNo { get; set; }
    public string? StoreCode { get; set; }
    public string? StoreName { get; set; }
    public decimal TotalAmount { get; set; }
    public int TotalQuantity { get; set; }

    /// <summary>
    /// 总订货进口金额
    /// </summary>
    public decimal TotalImportAmount { get; set; }

    /// <summary>
    /// 总发货/发票进口金额
    /// </summary>
    public decimal TotalAllocatedImportAmount { get; set; }

    /// <summary>
    /// 总体积 (m³)
    /// </summary>
    public decimal TotalVolume { get; set; }

  /// <summary>
  /// 总订货体积 (m³)
  /// </summary>
  public decimal TotalOrderVolume { get; set; }

  /// <summary>
  /// 总发货体积 (m³)
  /// </summary>
  public decimal TotalAllocVolume { get; set; }

    /// <summary>
    /// 运费
    /// </summary>
    public decimal? ShippingFee { get; set; }

    public string? Remarks { get; set; }

    /// <summary>
    /// 门店地址
    /// </summary>
    public string? StoreAddress { get; set; }

    /// <summary>
    /// 分店联系邮箱
    /// </summary>
    public string? StoreContactEmail { get; set; }

    public DateTime? OrderDate { get; set; }

    public DateTime? OutboundDate { get; set; }

    public int TotalAllocQuantity { get; set; }

    public int TotalSKU { get; set; }

    /// <summary>
    /// 流程状态（0=草稿, 1=已提交, 2=已完成, 3=配货中）
    /// </summary>
    public int? FlowStatus { get; set; }

    /// <summary>
    /// 发票邮件最近一次成功发送信息。
    /// </summary>
    public StoreOrderInvoiceEmailSentInfoDto InvoiceEmailSentInfo { get; set; } = new();

    public List<StoreOrderCartItemDto> Items { get; set; } = new();
  }

  /// <summary>
  /// 扫码购物车写入后的轻量汇总，只承载页面顶部数量和金额需要的字段。
  /// </summary>
  public class StoreOrderCartMutationSummaryDto
  {
    public string OrderGUID { get; set; } = string.Empty;
    public string StoreCode { get; set; } = string.Empty;
    public long CartRevision { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal TotalImportAmount { get; set; }
    public int TotalQuantity { get; set; }
    public int TotalSku { get; set; }
  }

  /// <summary>
  /// 扫码加购/改数量的轻量响应：只返回变更行和购物车摘要，避免整车明细 reload。
  /// </summary>
  public class StoreOrderCartMutationResultDto
  {
    public StoreOrderCartMutationSummaryDto Summary { get; set; } = new();
    public StoreOrderCartItemDto? ChangedItem { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public bool Removed { get; set; }
  }

  /// <summary>
  /// 扫码查询并加购请求。只用于 scan-lookup-add 合并接口，普通加购接口不受影响。
  /// </summary>
  public class StoreOrderScanLookupAddRequestDto
  {
    [Required]
    public string StoreCode { get; set; } = string.Empty;

    [Required]
    public string Barcode { get; set; } = string.Empty;

    public decimal? Quantity { get; set; }
  }

  /// <summary>
  /// 扫码查询并加购响应：0/多命中只返回候选，单命中才返回轻量购物车变更。
  /// </summary>
  public class StoreOrderScanLookupAddResultDto
  {
    public string Barcode { get; set; } = string.Empty;
    public string? MatchType { get; set; }
    public List<StoreOrderProductDto> Items { get; set; } = new();
    public bool Added { get; set; }
    public StoreOrderCartMutationResultDto? Cart { get; set; }
  }

  /// <summary>
  /// 订货明细交互页查询参数。
  /// </summary>
  public class StoreOrderDetailQueryDto
  {
    public const int DefaultPageNumber = 1;
    public const int DefaultPageSize = 200;
    public const int MaxPageSize = 1000;

    public int PageNumber { get; set; } = DefaultPageNumber;
    public int PageSize { get; set; } = DefaultPageSize;
    public string? Keyword { get; set; }
    public string? StatFilter { get; set; }
    public string? ItemNumber { get; set; }
    public string? ProductName { get; set; }
    public string? Barcode { get; set; }
    public string? LocationCode { get; set; }
    public decimal? QuantityMin { get; set; }
    public decimal? QuantityMax { get; set; }
    public decimal? AllocQuantityMin { get; set; }
    public decimal? AllocQuantityMax { get; set; }
    public decimal? ImportPriceMin { get; set; }
    public decimal? ImportPriceMax { get; set; }
    public bool? IsActive { get; set; }
    public string? SortBy { get; set; }
    public bool SortDescending { get; set; }
  }

  /// <summary>
  /// 首次货柜进货价基准差异查询参数。
  /// </summary>
  public class StoreOrderImportPriceVarianceQueryDto
  {
    public string? Keyword { get; set; }
    public string? StoreCode { get; set; }
    public List<string>? StoreCodes { get; set; }
    public string? OrderNo { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string? SupplierCode { get; set; }
    public string? VarianceDirection { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string? SortBy { get; set; }
    public bool SortDescending { get; set; } = true;
  }

  /// <summary>
  /// 首次货柜进货价基准差异商品汇总。
  /// </summary>
  public class StoreOrderImportPriceVarianceItemDto
  {
    public string ProductCode { get; set; } = string.Empty;
    public string? ItemNumber { get; set; }
    public string? ProductName { get; set; }
    public string? ProductImage { get; set; }
    public string? SupplierCode { get; set; }
    public string? SupplierName { get; set; }
    public decimal? DomesticPrice { get; set; }
    public decimal? WarehouseImportPrice { get; set; }
    public decimal? UnitVolume { get; set; }
    public int? PackingQuantity { get; set; }
    public decimal FirstContainerImportPrice { get; set; }
    public decimal AllocQuantityTotal { get; set; }
    public decimal OriginalImportAmountTotal { get; set; }
    public decimal BaselineImportAmountTotal { get; set; }
    public decimal VarianceAmountTotal { get; set; }
    public int DetailCount { get; set; }
    public string? FirstContainerCode { get; set; }
    public string? FirstContainerNumber { get; set; }
    public DateTime? FirstContainerDate { get; set; }
  }

  /// <summary>
  /// 首次货柜价差异统计页更新仓库当前国内价格请求。
  /// </summary>
  public class StoreOrderImportPriceVarianceDomesticPriceUpdateDto
  {
    public string? ProductCode { get; set; }
    public decimal? DomesticPrice { get; set; }
  }

  /// <summary>
  /// 首次货柜价差异统计页更新仓库当前国内价格结果。
  /// </summary>
  public class StoreOrderImportPriceVarianceDomesticPriceUpdateResultDto
  {
    public string ProductCode { get; set; } = string.Empty;
    public decimal DomesticPrice { get; set; }
  }

  /// <summary>
  /// 首次货柜价差异统计页更新仓库当前进货价格请求。
  /// </summary>
  public class StoreOrderImportPriceVarianceWarehouseImportPriceUpdateDto
  {
    public string? ProductCode { get; set; }
    public decimal? WarehouseImportPrice { get; set; }
  }

  /// <summary>
  /// 首次货柜价差异统计页更新仓库当前进货价格结果。
  /// </summary>
  public class StoreOrderImportPriceVarianceWarehouseImportPriceUpdateResultDto
  {
    public string ProductCode { get; set; } = string.Empty;
    public decimal WarehouseImportPrice { get; set; }
  }

  /// <summary>
  /// 首次货柜价差异统计页批量更新仓库当前进货价格请求。
  /// </summary>
  public class StoreOrderImportPriceVarianceWarehouseImportPriceBatchUpdateDto
  {
    public List<string>? ProductCodes { get; set; }
    public decimal? WarehouseImportPrice { get; set; }
  }

  /// <summary>
  /// 首次货柜价差异统计页批量更新仓库当前进货价格结果。
  /// </summary>
  public class StoreOrderImportPriceVarianceWarehouseImportPriceBatchUpdateResultDto
  {
    public int UpdatedCount { get; set; }
    public decimal WarehouseImportPrice { get; set; }
    public List<string> ProductCodes { get; set; } = new();
  }

  /// <summary>
  /// 首次货柜进货价基准差异单商品明细查询参数。
  /// </summary>
  public class StoreOrderImportPriceVarianceDetailQueryDto : StoreOrderImportPriceVarianceQueryDto
  {
    public string? ProductCode { get; set; }
  }

  /// <summary>
  /// 首次货柜进货价基准差异订单明细。
  /// </summary>
  public class StoreOrderImportPriceVarianceDetailItemDto
  {
    public string OrderGUID { get; set; } = string.Empty;
    public string DetailGUID { get; set; } = string.Empty;
    public string? OrderNo { get; set; }
    public DateTime? OrderDate { get; set; }
    public string? StoreCode { get; set; }
    public string? StoreName { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string? ItemNumber { get; set; }
    public string? ProductName { get; set; }
    public decimal OrderImportPrice { get; set; }
    public decimal FirstContainerImportPrice { get; set; }
    public decimal AllocQuantity { get; set; }
    public decimal OriginalImportAmount { get; set; }
    public decimal BaselineImportAmount { get; set; }
    public decimal VarianceAmount { get; set; }
    public string? FirstContainerCode { get; set; }
    public string? FirstContainerNumber { get; set; }
    public DateTime? FirstContainerDate { get; set; }
  }

  /// <summary>
  /// 首次货柜进货价基准差异汇总。
  /// </summary>
  public class StoreOrderImportPriceVarianceSummaryDto
  {
    public int TotalRows { get; set; }
    public decimal OriginalImportAmountTotal { get; set; }
    public decimal BaselineImportAmountTotal { get; set; }
    public decimal VarianceAmountTotal { get; set; }
  }

  /// <summary>
  /// 首次货柜进货价基准差异国内供应商汇总。
  /// </summary>
  public class StoreOrderImportPriceVarianceSupplierSummaryDto
  {
    public string? SupplierCode { get; set; }
    public string? SupplierName { get; set; }
    public int ProductCount { get; set; }
    public int DetailCount { get; set; }
    public decimal OriginalImportAmountTotal { get; set; }
    public decimal BaselineImportAmountTotal { get; set; }
    public decimal IncreaseVarianceAmountTotal { get; set; }
    public decimal DecreaseVarianceAmountTotal { get; set; }
    public decimal VarianceAmountTotal { get; set; }
  }

  /// <summary>
  /// 首次货柜进货价基准差异分页结果。
  /// </summary>
  public class StoreOrderImportPriceVarianceResultDto
  {
    public List<StoreOrderImportPriceVarianceItemDto> Items { get; set; } = new();
    public int Total { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public StoreOrderImportPriceVarianceSummaryDto Summary { get; set; } = new();
    public List<StoreOrderImportPriceVarianceSupplierSummaryDto> SupplierSummaries { get; set; } = new();
  }

  /// <summary>
  /// 首次货柜进货价基准差异订单明细分页结果。
  /// </summary>
  public class StoreOrderImportPriceVarianceDetailResultDto
  {
    public List<StoreOrderImportPriceVarianceDetailItemDto> Items { get; set; } = new();
    public int Total { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public StoreOrderImportPriceVarianceSummaryDto Summary { get; set; } = new();
  }

  /// <summary>
  /// 订货明细交互页 DTO，保留订单头和整单汇总，只分页返回当前页明细。
  /// </summary>
  public class StoreOrderDetailDto : StoreOrderCartDto
  {
    public int Total { get; set; }
    public int ItemsTotal { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = StoreOrderDetailQueryDto.DefaultPageSize;

    /// <summary>
    /// 整单“订货未配货”的行数。
    /// </summary>
    public int OrderedNotShippedCount { get; set; }

    /// <summary>
    /// 整单“主动配货”的行数。
    /// </summary>
    public int ShippedWithoutOrderCount { get; set; }
  }

  /// <summary>
  /// 购物车明细DTO
  /// </summary>
  public class StoreOrderCartItemDto
  {
    public string DetailGUID { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public string? ItemNumber { get; set; }
    public string? Barcode { get; set; }
    public string? Grade { get; set; }
    public string? ProductName { get; set; }
    public string? ProductImage { get; set; }
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public decimal? AllocQuantity { get; set; }
    public decimal Amount { get; set; }

    /// <summary>
    /// 进口价格
    /// </summary>
    public decimal ImportPrice { get; set; }

    /// <summary>
    /// 订货进口金额
    /// </summary>
    public decimal ImportAmount { get; set; }

    /// <summary>
    /// 发货/发票进口金额
    /// </summary>
    public decimal AllocatedImportAmount { get; set; }

    /// <summary>
    /// 单件体积 (m³)
    /// </summary>
    public decimal? Volume { get; set; }

    /// <summary>
    /// 小计体积 (m³)
    /// </summary>
    public decimal? TotalVolume { get; set; }

  /// <summary>
  /// 订货体积 (m³)
  /// </summary>
  public decimal? OrderVolume { get; set; }

  /// <summary>
  /// 发货体积 (m³)
  /// </summary>
  public decimal? AllocVolume { get; set; }

    /// <summary>
    /// 最小订货量
    /// </summary>
    public int MinOrderQuantity { get; set; } = 1;

    /// <summary>
    /// 是否上架
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// 仓库登记的后续供货计划（WarehouseProductSupplyPlans）。仅在已下架且有未关闭供货说明时有值，
    /// 前端据此区分「暂停供货，提交时保留」与「不再供应，请删除」。
    /// </summary>
    public string? SupplyPlan { get; set; }

    /// <summary>
    /// 货位代码 (配货位)
    /// </summary>
    public string? LocationCode { get; set; }

    /// <summary>
    /// 零售价 (RRP)
    /// </summary>
    public decimal? RRP { get; set; }
  }

  /// <summary>
  /// 添加到购物车请求
  /// </summary>
  public class AddToCartRequestDto
  {
    [Required]
    public string StoreCode { get; set; } = string.Empty;

    [Required]
    public string ProductCode { get; set; } = string.Empty;

    [Required]
    public decimal Quantity { get; set; }

    public decimal? ImportPrice { get; set; }
  }

  /// <summary>
  /// 移除购物车项请求
  /// </summary>
  public class RemoveFromCartRequestDto
  {
    [Required]
    public string StoreCode { get; set; } = string.Empty;

    [Required]
    public string DetailGUID { get; set; } = string.Empty;
  }

  /// <summary>
  /// 清空购物车请求
  /// </summary>
  public class ClearCartRequestDto
  {
    [Required]
    public string StoreCode { get; set; } = string.Empty;
  }

  /// <summary>
  /// 提交订单请求
  /// </summary>
  public class SubmitStoreOrderRequestDto
  {
    [Required]
    public string StoreCode { get; set; } = string.Empty;

    public string? Remarks { get; set; }

    /// <summary>
    /// 仅由已完成权限校验的仓库后台写入，禁止客户端通过 JSON 绕过 Preorder 门禁。
    /// </summary>
    [JsonIgnore]
    public bool BypassPreorderGate { get; set; }
  }

  /// <summary>
  /// 提交订单结果：在供货的行进单，已暂停供货的行保留在购物车里。
  /// </summary>
  public class SubmitStoreOrderResultDto
  {
    /// <summary>
    /// 已提交订单的 GUID（即原购物车 GUID）。提交失败时为空。
    /// </summary>
    public string? OrderGUID { get; set; }

    public string? OrderNo { get; set; }

    /// <summary>
    /// 实际进单的行数。
    /// </summary>
    public int SubmittedLineCount { get; set; }

    /// <summary>
    /// 因暂停供货而保留在购物车里的行数。
    /// </summary>
    public int KeptLineCount { get; set; }

    /// <summary>
    /// 承接保留行的新购物车 GUID；没有保留行时为空。
    /// </summary>
    public string? KeptCartOrderGUID { get; set; }

    public List<SubmitStoreOrderKeptLineDto> KeptLines { get; set; } = new();
  }

  /// <summary>
  /// 提交时保留在购物车里的一行。
  /// </summary>
  public class SubmitStoreOrderKeptLineDto
  {
    public string DetailGUID { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public string? ItemNumber { get; set; }
    public string? ProductName { get; set; }
    public decimal Quantity { get; set; }

    /// <summary>
    /// 后续供货计划（WarehouseProductSupplyPlans）；为 Discontinued 时前端提示删除。无说明时为空。
    /// </summary>
    public string? SupplyPlan { get; set; }
  }

  /// <summary>
  /// 动态数据请求 (历史订单 + 购物车数量)
  /// </summary>
  public class StoreOrderDynamicDataRequestDto
  {
    [Required]
    public string StoreCode { get; set; } = string.Empty;
    public List<string> ProductCodes { get; set; } = new();

    /// <summary>
    /// 是否包含最近来货后的销量；默认保持旧调用的完整动态数据行为。
    /// </summary>
    public bool IncludeSales { get; set; } = true;
  }

  /// <summary>
  /// 商品动态数据 (历史订单 + 购物车数量)
  /// </summary>
  public class StoreOrderDynamicDataDto
  {
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>
    /// 最近订货日期
    /// </summary>
    public DateTime? LastOrderDate { get; set; }

    /// <summary>
    /// 最近订货数量
    /// </summary>
    public decimal? LastQuantity { get; set; }

    /// <summary>
    /// 最近配货数量
    /// </summary>
    public decimal? LastAllocQuantity { get; set; }

    /// <summary>
    /// 当前购物车数量
    /// </summary>
    public decimal CartQuantity { get; set; }

    /// <summary>
    /// 最近来货后的销售数量；分店不存在、停用或没有可用来货日期时为 null。
    /// </summary>
    public int? SalesQuantitySinceLastArrival { get; set; }
  }

  /// <summary>
  /// 商品仓库订货发货记录请求。
  /// </summary>
  public class StoreOrderProductOrderHistoryRequestDto
  {
    [Required]
    public string StoreCode { get; set; } = string.Empty;

    [Required]
    public string ProductCode { get; set; } = string.Empty;

    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
  }

  /// <summary>
  /// 商品仓库订货发货记录行（按订单聚合，同订单重复商品行已汇总）。
  /// </summary>
  public class StoreOrderProductOrderHistoryItemDto
  {
    public string OrderGUID { get; set; } = string.Empty;
    public string? OrderNo { get; set; }
    public DateTime? OrderDate { get; set; }
    public DateTime? OutboundDate { get; set; }
    public int? FlowStatus { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? AllocQuantity { get; set; }
  }

  /// <summary>
  /// 查询最近来货后销售明细请求。
  /// </summary>
  public class StoreOrderSalesSinceLastArrivalRequestDto
  {
    [Required]
    public string StoreCode { get; set; } = string.Empty;

    [Required]
    public string ProductCode { get; set; } = string.Empty;

    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
  }

  /// <summary>
  /// 最近来货后销售明细行。
  /// </summary>
  public class StoreOrderSalesSinceLastArrivalItemDto
  {
    public DateTime Date { get; set; }
    public int SalesQuantity { get; set; }
    public decimal? AveragePrice { get; set; }
  }

  /// <summary>
  /// 最近来货后销售查询结果。
  /// </summary>
  public class StoreOrderSalesSinceLastArrivalResultDto
  {
    public string StoreCode { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    public DateTime? LastArrivalDate { get; set; }
    public DateTime? EndDate { get; set; }
    public int TotalSalesQuantity { get; set; }
    public int TotalCount { get; set; }
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public List<StoreOrderSalesSinceLastArrivalItemDto> Items { get; set; } = new();
  }

  /// <summary>
  /// 批量查询商品最近一次来货后的销售数量请求。
  /// </summary>
  public class StoreOrderSalesSinceLastArrivalSummaryRequestDto
  {
    [Required]
    public string StoreCode { get; set; } = string.Empty;
    public List<string> ProductCodes { get; set; } = new();
  }

  /// <summary>
  /// 批量查询商品最近一次来货后的销售数量结果。
  /// </summary>
  public class StoreOrderSalesSinceLastArrivalSummaryItemDto
  {
    public string ProductCode { get; set; } = string.Empty;
    public int? SalesQuantitySinceLastArrival { get; set; }
  }

  /// <summary>
  /// 商品统一订货/发货历史与销售明细合并时间轴请求。
  /// </summary>
  public class StoreOrderProductActivityHistoryRequestDto
  {
    [Required]
    public string StoreCode { get; set; } = string.Empty;

    [Required]
    public string ProductCode { get; set; } = string.Empty;

    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 30;

    /// <summary>
    /// all / order / sales；非法值按 all 处理。
    /// </summary>
    public string RecordType { get; set; } = "all";
  }

  /// <summary>
  /// 商品统一时间轴行：订单行为空销售字段，销售行为空订单字段。
  /// </summary>
  public class StoreOrderProductActivityHistoryItemDto
  {
    /// <summary>
    /// "order"、"sales" 或 "salesSubtotal"。
    /// </summary>
    public string RecordType { get; set; } = "order";

    /// <summary>
    /// 统一时间轴日期；订单使用 OrderDate（为空返回 null 并置底），销售使用统计日期。
    /// </summary>
    public DateTime? RecordDate { get; set; }

    public string? OrderGUID { get; set; }
    public string? OrderNo { get; set; }
    public DateTime? OrderDate { get; set; }
    public DateTime? OutboundDate { get; set; }
    public int? FlowStatus { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? AllocQuantity { get; set; }

    public int? SalesQuantity { get; set; }
    public decimal? AveragePrice { get; set; }
    public DateTime? PeriodStartDate { get; set; }
    public DateTime? PeriodEndDate { get; set; }
  }

  /// <summary>
  /// 商品统一订货/发货历史与销售明细合并时间轴结果。
  /// </summary>
  public class StoreOrderProductActivityHistoryResultDto
  {
    public string StoreCode { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public DateTime? LastArrivalDate { get; set; }
    public DateTime? EndDate { get; set; }
    public decimal? LatestOrderQuantity { get; set; }
    public decimal? LatestAllocQuantity { get; set; }
    public int TotalSalesQuantity { get; set; }
    public int Total { get; set; }
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public List<StoreOrderProductActivityHistoryItemDto> Items { get; set; } = new();
  }

  /// <summary>
  /// 订单列表过滤DTO
  /// </summary>
  public class StoreOrderListFilterDto
  {
    public string? Keyword { get; set; }

    /// <summary>
    /// 商品维度关键字：按货号、条码或商品名筛选包含该商品的订单。
    /// 与 Keyword 相互独立，两者同时传入时取交集。
    /// </summary>
    public string? ProductKeyword { get; set; }
    public string? StoreCode { get; set; }
    public List<string>? StoreCodes { get; set; }
    public DateTime? StartDate { get; set;}
    public DateTime? EndDate { get; set; }
    public List<int>? StatusList { get; set; }
    public StoreOrderListColumnFilterDto? ColumnFilters { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string? SortBy { get; set; }
    public bool? SortDescending { get; set; }
  }

  /// <summary>
  /// 分店订货列表列头过滤 DTO。
  /// </summary>
  public class StoreOrderListColumnFilterDto
  {
    public string? OrderNo { get; set; }
    public DateTime? OutboundDateStart { get; set; }
    public DateTime? OutboundDateEnd { get; set; }
    public decimal? TotalQuantityMin { get; set; }
    public decimal? TotalQuantityMax { get; set; }
    public decimal? TotalOrderAmountMin { get; set; }
    public decimal? TotalOrderAmountMax { get; set; }
    public decimal? TotalOrderVolumeMin { get; set; }
    public decimal? TotalOrderVolumeMax { get; set; }
    public decimal? TotalAllocVolumeMin { get; set; }
    public decimal? TotalAllocVolumeMax { get; set; }
    public decimal? TotalAllocQuantityMin { get; set; }
    public decimal? TotalAllocQuantityMax { get; set; }
    public decimal? ImportTotalAmountMin { get; set; }
    public decimal? ImportTotalAmountMax { get; set; }
    public string? Remarks { get; set; }
    public DateTime? CreatedAtStart { get; set; }
    public DateTime? CreatedAtEnd { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAtStart { get; set; }
    public DateTime? UpdatedAtEnd { get; set; }
  }

  /// <summary>
  /// 订单列表项DTO
  /// </summary>
  public class StoreOrderListItemDto
  {
    public string OrderGUID { get; set; } = string.Empty;
    public string OrderNo { get; set; } = string.Empty;
    public string? StoreCode { get; set; }
    public string? StoreName { get; set; }
    public DateTime? OrderDate { get; set; }
    public DateTime? OutboundDate { get; set; }
    public int FlowStatus { get; set; }

    /// <summary>
    /// 预计销售金额 (Order Qty * OEMPrice)
    /// </summary>
    public decimal TotalAmount { get; set; }

    public decimal OEMTotalAmount { get; set; }

    /// <summary>
    /// 发货金额 (Alloc Qty * OEMPrice)
    /// </summary>
    public decimal ImportTotalAmount { get; set; }

    /// <summary>
    /// 订货金额 (Order Qty * OEMPrice)
    /// </summary>
    public decimal TotalOrderAmount { get; set; }

    public int TotalQuantity { get; set; }

    /// <summary>
    /// 发货数量 (Alloc Qty)
    /// </summary>
    public int TotalAllocQuantity { get; set; }

  /// <summary>
  /// 订货体积 (m³)
  /// </summary>
  public decimal TotalOrderVolume { get; set; }

  /// <summary>
  /// 发货体积 (m³)
  /// </summary>
  public decimal TotalAllocVolume { get; set; }

    public string? Remarks { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 创建者
    /// </summary>
    public string? CreatedBy { get; set; }

    /// <summary>
    /// 更新时间
    /// </summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// 更新者
    /// </summary>
    public string? UpdatedBy { get; set; }
  }

  /// <summary>
  /// 创建订单请求
  /// </summary>
  public class CreateStoreOrderDto
  {
    [Required]
    public string StoreCode { get; set; } = string.Empty;

    public string? Remarks { get; set; }

    /// <summary>
    /// 仅由已完成权限校验的仓库后台写入，禁止客户端通过 JSON 绕过 Preorder 门禁。
    /// </summary>
    [JsonIgnore]
    public bool BypassPreorderGate { get; set; }
  }

  /// <summary>
  /// 订单中未能匹配本地分店的分店标识聚合。
  /// </summary>
  public class UnmatchedStoreOrderGroupDto
  {
    public string SourceStoreCode { get; set; } = string.Empty;
    public string? SourceStoreName { get; set; }
    public int OrderCount { get; set; }
    public DateTime? LatestOrderDate { get; set; }
  }

  /// <summary>
  /// 将订单旧分店标识映射到目标本地分店编码。
  /// </summary>
  public class StoreOrderStoreCodeMappingDto
  {
    [Required]
    public string SourceStoreCode { get; set; } = string.Empty;

    [Required]
    public string TargetStoreCode { get; set; } = string.Empty;
  }

  /// <summary>
  /// 批量修复订单分店标识请求。
  /// </summary>
  public class BatchMapStoreOrderStoreCodeDto
  {
    public List<StoreOrderStoreCodeMappingDto> Mappings { get; set; } = new();
  }

  /// <summary>
  /// 单个旧分店标识修复结果。
  /// </summary>
  public class StoreOrderStoreCodeMappingResultItemDto
  {
    public string SourceStoreCode { get; set; } = string.Empty;
    public string TargetStoreCode { get; set; } = string.Empty;
    public int UpdatedCount { get; set; }
    public int SkippedCount { get; set; }
  }

  /// <summary>
  /// 批量修复订单分店标识结果。
  /// </summary>
  public class BatchMapStoreOrderStoreCodeResultDto
  {
    public int UpdatedCount { get; set; }
    public int SkippedCount { get; set; }
    public List<StoreOrderStoreCodeMappingResultItemDto> Items { get; set; } = new();
  }

  /// <summary>
  /// 添加商品到指定订单请求
  /// </summary>
  public class AddOrderLineDto
  {
    [Required]
    public string OrderGUID { get; set; } = string.Empty;

    [Required]
    public string ProductCode { get; set; } = string.Empty;

    [Required]
    public decimal Quantity { get; set; }

    public decimal? ImportPrice { get; set; }
  }

  public class ProductQuantityDto
  {
    public string ProductCode { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal? ImportPrice { get; set; }
    public string? Action { get; set; }
  }

  /// <summary>
  /// 批量更新订单行请求 (数量或价格)
  /// </summary>
  public class BatchUpdateOrderLineDto
  {
    [Required]
    public string OrderGUID { get; set; } = string.Empty;

    public List<BatchUpdateItemDto> Items { get; set; } = new();
  }

  public class BatchUpdateItemDto
  {
    public string? DetailGUID { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public decimal? Quantity { get; set; }
    public decimal? ImportPrice { get; set; }

    /// <summary>
    /// 是否把本次进口价同步到商品主档和分店进货价；为空/false 时只保存订单明细。
    /// </summary>
    public bool? SyncImportPrice { get; set; }
  }

  /// <summary>
  /// 从仓库商品表刷新订单明细进口价请求。
  /// </summary>
  public class RefreshStoreOrderImportPricesDto
  {
    [Required]
    public string OrderGUID { get; set; } = string.Empty;

    /// <summary>
    /// 为空表示刷新整单全部未删除明细。
    /// </summary>
    public List<string>? DetailGUIDs { get; set; }
  }

  /// <summary>
  /// 从仓库商品表刷新订单明细进口价结果。
  /// </summary>
  public class RefreshStoreOrderImportPricesResultDto
  {
    public int UpdatedCount { get; set; }
    public int UnchangedCount { get; set; }
    public int SkippedCount { get; set; }
    public int MissingWarehousePriceCount { get; set; }
  }

  public static class StoreOrderPasteTargetFields
  {
    public const string Quantity = "quantity";
    public const string AllocQuantity = "allocQuantity";
  }

  public static class StoreOrderPasteActions
  {
    public const string Replace = "replace";
    public const string Append = "append";
    public const string Skip = "skip";
  }

  /// <summary>
  /// 批量添加商品请求
  /// </summary>
  public class BatchAddOrderLineDto
  {
    [Required]
    public string OrderGUID { get; set; } = string.Empty;

    public List<ProductQuantityDto> Items { get; set; } = new();
  }

  /// <summary>
  /// Excel 粘贴覆盖订单行请求
  /// </summary>
  public class PasteReplaceOrderLinesDto
  {
    [Required]
    public string OrderGUID { get; set; } = string.Empty;

    [Required]
    public string TargetField { get; set; } = StoreOrderPasteTargetFields.Quantity;

    public List<ProductQuantityDto> Items { get; set; } = new();
  }

  /// <summary>
  /// 更新订单行请求
  /// </summary>
  public class UpdateOrderLineDto
  {
    [Required]
    public string OrderGUID { get; set; } = string.Empty;

    [Required]
    public string ProductCode { get; set; } = string.Empty;

    [Required]
    public decimal Quantity { get; set; }

    public decimal? ImportPrice { get; set; }

    /// <summary>
    /// 是否把本次进口价同步到商品主档和分店进货价；为空/false 时只保存订单明细。
    /// </summary>
    public bool? SyncImportPrice { get; set; }
  }

  /// <summary>
  /// 软删除订单行请求
  /// </summary>
  public class RemoveOrderLineDto
  {
    [Required]
    public string OrderGUID { get; set; } = string.Empty;

    [Required]
    public string DetailGUID { get; set; } = string.Empty;
  }

  /// <summary>
  /// 更新订单头请求
  /// </summary>
  public class UpdateOrderHeaderDto
  {
    public string OrderGuid { get; set; } = string.Empty;
    public string? Remarks { get; set; }
    public decimal? ShippingFee { get; set; }
    public DateTime? OrderDate { get; set; }
    public string? StoreCode { get; set; }
  }

  /// <summary>
  /// 更新订单出库日期请求
  /// </summary>
  public class UpdateOrderOutboundDateDto
  {
    [Required]
    public string OrderGuid { get; set; } = string.Empty;

    public DateTime? OutboundDate { get; set; }

    public bool CompleteOrder { get; set; }
  }
}

/// <summary>
/// 更新商品状态请求
/// </summary>
public class UpdateProductStatusDto
{
  [Required]
  public string ProductCode { get; set; } = string.Empty;

  [Required]
  public bool IsActive { get; set; }

  /// <summary>下架时随请求登记的供货说明；上架时忽略。旧客户端不传，分店端按“后续计划待确认”展示。</summary>
  public BlazorApp.Shared.DTOs.WarehouseProductSupplyNoticeInputDto? SupplyNotice { get; set; }
}

/// <summary>
/// 批量更新商品状态请求
/// </summary>
public class BatchUpdateProductStatusDto
{
  [Required]
  public List<string> ProductCodes { get; set; } = new();

  [Required]
  public bool IsActive { get; set; }

  /// <summary>下架时随请求登记的供货说明；上架时忽略。旧客户端不传，分店端按“后续计划待确认”展示。</summary>
  public BlazorApp.Shared.DTOs.WarehouseProductSupplyNoticeInputDto? SupplyNotice { get; set; }
}

/// <summary>
/// 复制订单请求
/// </summary>
public class CopyOrderDto
{
  [Required]
  public string SourceOrderGUID { get; set; } = string.Empty;

  [Required]
  public string TargetStoreCode { get; set; } = string.Empty;

  public bool CopyOrderQuantity { get; set; } = false;

  public bool CopyAllocQuantity { get; set; } = false;

  /// <summary>
  /// 仅由已完成权限校验的仓库后台写入，禁止客户端通过 JSON 绕过 Preorder 门禁。
  /// </summary>
  [JsonIgnore]
  public bool BypassPreorderGate { get; set; }
}

/// <summary>
/// 复制订单结果
/// </summary>
public class CopyOrderResultDto
{
    public string OrderGUID { get; set; } = string.Empty;
    public string OrderNo { get; set; } = string.Empty;
}

/// <summary>
/// 同步缺失订单的返回结果
/// </summary>
public class SyncMissingOrdersResultDto
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// HQ 同步模式；旧缺失订单同步为空。
    /// </summary>
    public string? Mode { get; set; }

    /// <summary>
    /// HQ 冲突处理策略；旧缺失订单同步为空。
    /// </summary>
    public StoreOrderHqSyncConflictStrategy? ConflictStrategy { get; set; }

    /// <summary>
    /// 同步任务标识。
    /// </summary>
    public string? RunId { get; set; }

    public int OrdersSynced { get; set; }

    public int DetailsSynced { get; set; }

    public int OrdersUpdated { get; set; }

    public int DetailsUpdated { get; set; }

    public int OrdersSoftDeleted { get; set; }

    public int DetailsSoftDeleted { get; set; }

    /// <summary>
    /// LatestWins 下因本地时间更新而跳过的订单数。
    /// </summary>
    public int SkippedOrdersBecauseLocalNewer { get; set; }

    /// <summary>
    /// LatestWins 下因本地时间更新而跳过的明细数。
    /// </summary>
    public int SkippedDetailsBecauseLocalNewer { get; set; }

    public int HqOrderCount { get; set; }

    public int HqDetailCount { get; set; }

    public int ShadowRowCount { get; set; }

    public long DurationMs { get; set; }

    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// 同步缺失订单的请求参数
/// </summary>
public class SyncMissingOrdersRequestDto
{
    /// <summary>
    /// 分店代码（旧字段，可选；不传则同步所有分店）
    /// </summary>
    public string? StoreCode { get; set; }

    /// <summary>
    /// 分店代码集合（新字段，可选；优先级高于 StoreCode）
    /// </summary>
    public List<string>? StoreCodes { get; set; }
}

/// <summary>
/// 分店订货 HQ 同步模式。
/// </summary>
public enum StoreOrderHqSyncMode
{
    Full = 1,
    Incremental = 2,
}

/// <summary>
/// 分店订货 HQ 同步冲突策略。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StoreOrderHqSyncConflictStrategy
{
    HqWins = 1,
    LatestWins = 2,
}

/// <summary>
/// 分店订货 HQ 同步请求。
/// </summary>
public class StoreOrderHqSyncRequestDto
{
    /// <summary>
    /// 旧字段兼容：单个分店/外购客户代码。
    /// </summary>
    public string? StoreCode { get; set; }

    /// <summary>
    /// 分店/外购客户代码集合；优先级高于 StoreCode。
    /// </summary>
    public List<string>? StoreCodes { get; set; }

    /// <summary>
    /// 增量同步开始时间；为空时后端默认最近 30 天。
    /// </summary>
    public DateTime? StartDate { get; set; }

    /// <summary>
    /// 增量同步结束时间；为空时后端默认当前时间。
    /// </summary>
    public DateTime? EndDate { get; set; }

    /// <summary>
    /// 增量同步冲突策略；默认使用 LatestWins。
    /// </summary>
    public StoreOrderHqSyncConflictStrategy ConflictStrategy { get; set; } =
        StoreOrderHqSyncConflictStrategy.LatestWins;
}

/// <summary>
/// 分店订货 HQ 同步结果。
/// </summary>
public class StoreOrderHqSyncResultDto : SyncMissingOrdersResultDto
{
}

/// <summary>
/// 订货缺失订单同步 job 状态常量
/// </summary>
public static class StoreOrderSyncJobStatusConstants
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
}

/// <summary>
/// 订货缺失订单同步 job 状态
/// </summary>
public class StoreOrderSyncJobDto
{
    /// <summary>
    /// job 标识
    /// </summary>
    public string JobId { get; set; } = string.Empty;

    /// <summary>
    /// job 状态
    /// </summary>
    public string Status { get; set; } = StoreOrderSyncJobStatusConstants.Running;

    /// <summary>
    /// HQ 同步模式；旧缺失订单同步为空。
    /// </summary>
    public string? Mode { get; set; }

    /// <summary>
    /// HQ 冲突处理策略；旧缺失订单同步为空。
    /// </summary>
    public StoreOrderHqSyncConflictStrategy? ConflictStrategy { get; set; }

    /// <summary>
    /// 参与同步的分店集合
    /// </summary>
    public List<string> StoreCodes { get; set; } = new();

    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    /// <summary>
    /// 是否命中了运行中去重
    /// </summary>
    public bool IsDuplicateRequest { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 完成时间
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// 终态过期时间
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// 同步结果
    /// </summary>
    public SyncMissingOrdersResultDto? Result { get; set; }
}

/// <summary>
/// 更新订单状态请求
/// </summary>
public class UpdateOrderStatusDto
{
    /// <summary>
    /// 订单GUID
    /// </summary>
    [Required]
    public string OrderGUID { get; set; } = string.Empty;

    /// <summary>
    /// 新状态 (1=Submitted, 2=Completed)
    /// </summary>
    [Required]
    public int NewStatus { get; set; }
}

/// <summary>
/// 批量更新订单状态请求
/// </summary>
public class BatchUpdateOrderStatusDto
{
    /// <summary>
    /// 订单GUID列表
    /// </summary>
    [Required]
    public List<string> OrderGUIDs { get; set; } = new();

    /// <summary>
    /// 新状态 (1=Submitted, 2=Completed)
    /// </summary>
    [Required]
    public int NewStatus { get; set; }
}

/// <summary>
/// 更新订单关联分店联系信息请求
/// </summary>
public class UpdateStoreOrderStoreContactDto
{
    /// <summary>
    /// 订单 GUID
    /// </summary>
    [Required]
    public string OrderGUID { get; set; } = string.Empty;

    /// <summary>
    /// 分店代码
    /// </summary>
    [Required]
    public string StoreCode { get; set; } = string.Empty;

    /// <summary>
    /// 分店地址
    /// </summary>
    [StringLength(500, ErrorMessage = "地址长度不能超过500个字符")]
    public string? Address { get; set; }

    /// <summary>
    /// 分店联系邮箱
    /// </summary>
    [EmailAddress(ErrorMessage = "联系邮箱格式不正确")]
    [StringLength(100, ErrorMessage = "联系邮箱长度不能超过100个字符")]
    public string? ContactEmail { get; set; }
}

/// <summary>
/// 订单关联分店联系信息
/// </summary>
public class StoreOrderStoreContactDto
{
    /// <summary>
    /// 订单 GUID
    /// </summary>
    public string OrderGUID { get; set; } = string.Empty;

    /// <summary>
    /// 分店代码
    /// </summary>
    public string StoreCode { get; set; } = string.Empty;

    /// <summary>
    /// 分店地址
    /// </summary>
    public string? Address { get; set; }

    /// <summary>
    /// 分店联系邮箱
    /// </summary>
    public string? ContactEmail { get; set; }
}

/// <summary>
/// 发送分店订货发票邮件请求
/// </summary>
public class SendStoreOrderInvoiceEmailDto
{
    /// <summary>
    /// 订单 GUID
    /// </summary>
    [Required]
    public string OrderGUID { get; set; } = string.Empty;

    /// <summary>
    /// 收件邮箱
    /// </summary>
    [Required]
    [EmailAddress(ErrorMessage = "收件邮箱格式不正确")]
    [StringLength(100, ErrorMessage = "收件邮箱长度不能超过100个字符")]
    public string ToEmail { get; set; } = string.Empty;

    /// <summary>
    /// 邮件主题
    /// </summary>
    [StringLength(200, ErrorMessage = "邮件主题长度不能超过200个字符")]
    public string? Subject { get; set; }

    /// <summary>
    /// 邮件正文
    /// </summary>
    [StringLength(10000, ErrorMessage = "邮件正文长度不能超过10000个字符")]
    public string? Body { get; set; }
}

/// <summary>
/// 分店订货发票邮件 job 状态常量
/// </summary>
public static class StoreOrderInvoiceEmailJobStatusConstants
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
}

/// <summary>
/// 分店订货发票邮件 job 状态
/// </summary>
public class StoreOrderInvoiceEmailJobDto
{
    /// <summary>
    /// job 标识
    /// </summary>
    public string JobId { get; set; } = string.Empty;

    /// <summary>
    /// job 状态
    /// </summary>
    public string Status { get; set; } = StoreOrderInvoiceEmailJobStatusConstants.Queued;

    /// <summary>
    /// 后端执行消息
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// 订单 GUID
    /// </summary>
    public string OrderGUID { get; set; } = string.Empty;

    /// <summary>
    /// 收件邮箱
    /// </summary>
    public string ToEmail { get; set; } = string.Empty;

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 完成时间
    /// </summary>
    public DateTime? CompletedAt { get; set; }
}

namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// Excel 粘贴导入后台 job 状态常量
    /// </summary>
    public static class StoreOrderPasteReplaceJobStatusConstants
    {
        public const string Queued = "Queued";
        public const string Running = "Running";
        public const string Succeeded = "Succeeded";
        public const string Failed = "Failed";
    }

    /// <summary>
    /// Excel 粘贴导入后台 job 状态
    /// </summary>
    public class StoreOrderPasteReplaceJobDto
    {
        /// <summary>
        /// job 标识
        /// </summary>
        public string JobId { get; set; } = string.Empty;

        /// <summary>
        /// job 状态
        /// </summary>
        public string Status { get; set; } = StoreOrderPasteReplaceJobStatusConstants.Queued;

        /// <summary>
        /// 后端执行消息
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// 订单 GUID
        /// </summary>
        public string OrderGUID { get; set; } = string.Empty;

        /// <summary>
        /// 写入目标字段
        /// </summary>
        public string TargetField { get; set; } = StoreOrderPasteTargetFields.Quantity;

        /// <summary>
        /// 前端提交的总行数
        /// </summary>
        public int TotalCount { get; set; }

        /// <summary>
        /// 实际提交给同步导入服务的行数
        /// </summary>
        public int ImportedCount { get; set; }

        /// <summary>
        /// 被后端跳过的行数
        /// </summary>
        public int SkippedCount { get; set; }

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// 完成时间
        /// </summary>
        public DateTime? CompletedAt { get; set; }
    }
}

/// <summary>
/// 发票邮件文本翻译请求。
/// </summary>
public class StoreOrderInvoiceEmailTextTranslationRequestDto
{
    public string OrderGUID { get; set; } = string.Empty;
    public string TargetLanguage { get; set; } = string.Empty;
    public string? Subject { get; set; }
    public string? Body { get; set; }
}

/// <summary>
/// 发票邮件文本翻译结果。
/// </summary>
public class StoreOrderInvoiceEmailTextTranslationResultDto
{
    public string? Subject { get; set; }
    public string? Body { get; set; }
}

/// <summary>
/// 发票邮件发送消息
/// </summary>
public class StoreOrderInvoiceEmailMessage
{
    /// <summary>
    /// 收件邮箱
    /// </summary>
    public string ToEmail { get; set; } = string.Empty;

    /// <summary>
    /// 邮件主题
    /// </summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// 邮件正文
    /// </summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// 邮件附件集合
    /// </summary>
    public List<StoreOrderInvoiceEmailAttachment> Attachments { get; set; } = new();
}

/// <summary>
/// 发票邮件附件
/// </summary>
public class StoreOrderInvoiceEmailAttachment
{
    /// <summary>
    /// 附件文件名
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// 附件 MIME 类型
    /// </summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>
    /// 附件内容
    /// </summary>
    public byte[] Bytes { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// 发票邮件附件生成结果
/// </summary>
public class StoreOrderInvoiceAttachmentBundle
{
    public string OrderGUID { get; set; } = string.Empty;
    public string? OrderNo { get; set; }
    public string? StoreCode { get; set; }
    public List<StoreOrderInvoiceEmailAttachment> Attachments { get; set; } = new();
}
