using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace BlazorApp.Shared.DTOs
{
    public sealed class LocalSupplierInvoiceFilterOptionDto
    {
        public string Value { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
    }

    public sealed class LocalSupplierInvoiceFilterOptionsDto
    {
        public List<LocalSupplierInvoiceFilterOptionDto> Suppliers { get; set; } = new();
    }

    /// <summary>
    /// 商城只读进货单头；刻意排除审计、客户端标识和导入配置等后台字段。
    /// </summary>
    public sealed class ShopLocalSupplierInvoiceHeaderDto
    {
        public string InvoiceGUID { get; set; } = string.Empty;
        public string? StoreCode { get; set; }
        public string? StoreName { get; set; }
        public string? SupplierCode { get; set; }
        public string? SupplierName { get; set; }
        public string? InvoiceNo { get; set; }
        public DateTime? OrderDate { get; set; }
        public DateTime? InboundDate { get; set; }
        public decimal? TotalAmount { get; set; }
        public decimal? ReceivedTotalAmount { get; set; }
        public int? FlowStatus { get; set; }
        public int? InboundStatus { get; set; }
        public string? Remarks { get; set; }
    }

    /// <summary>
    /// 商城只读进货商品；只保留页面展示所需的商品和价格字段。
    /// </summary>
    public sealed class ShopLocalSupplierInvoiceItemDto
    {
        public string DetailGUID { get; set; } = string.Empty;
        public string? StoreProductCode { get; set; }
        public string? ProductCode { get; set; }
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
        public string? ProductName { get; set; }
        public string? ProductImage { get; set; }
        public string? Specification { get; set; }
        public string? Unit { get; set; }
        public decimal? Quantity { get; set; }
        public decimal? LastPurchasePrice { get; set; }
        public decimal? PurchasePrice { get; set; }
        public decimal? RetailPrice { get; set; }
        public decimal? Amount { get; set; }
        public decimal? NewAutoRetailPrice { get; set; }
    }

    /// <summary>
    /// 分店进货单列表左侧分店栏的计数：某分店在当前其他筛选条件下的单数。
    /// </summary>
    public class LocalSupplierInvoiceStoreCountDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    public class LocalSupplierInvoiceListDto
    {
        public string InvoiceGUID { get; set; } = string.Empty;
        public string? StoreCode { get; set; }
        public string? StoreName { get; set; }
        public string? SupplierCode { get; set; }
        public string? SupplierName { get; set; }
        public string? InvoiceNo { get; set; }
        public int? VoucherType { get; set; }
        public DateTime? OrderDate { get; set; }
        public DateTime? InboundDate { get; set; }
        public decimal? TotalAmount { get; set; }
        public decimal? ReceivedTotalAmount { get; set; }
        public int? FlowStatus { get; set; }
        public int? InboundStatus { get; set; }
        public int PriceIncreaseItemCount { get; set; }
        public int PriceDecreaseItemCount { get; set; }
        /// <summary>当前进货单有效明细均已完成商品检测时为 true。</summary>
        public bool IsProductChecked { get; set; }
        /// <summary>有效明细行数（IsDeleted=false）；没有明细的单为 0。</summary>
        public int DetailCount { get; set; }
        /// <summary>
        /// 未检测的有效明细行数：ExistingProductCount 为 NULL 才算未检测；
        /// 0 是"已检测但商品主档不存在"，不计入此项。
        /// </summary>
        public int UncheckedDetailCount { get; set; }
        /// <summary>
        /// 新品行数：检测为商品主档不存在（ExistingProductCount == 0）且至今未关联主档（ProductCode 为空）的有效明细，
        /// 即仍需新建商品的行；已新建或已同步到总部回填编码的行、NULL（未检测）均不计入此项。
        /// </summary>
        public int NewProductDetailCount { get; set; }
        /// <summary>
        /// 本单新品行数：明细关联的商品由本进货单新建（依据商品变更历史的 Create 记录，历史之前的数据按检测快照兜底）。
        /// </summary>
        public int CreatedHereProductDetailCount { get; set; }
        public string? Remarks { get; set; }
        [JsonConverter(typeof(LocalSupplierInvoiceAuditUtcDateTimeJsonConverter))]
        public DateTime CreatedAt { get; set; }
        public string? CreatedBy { get; set; }

        [JsonConverter(typeof(NullableLocalSupplierInvoiceAuditUtcDateTimeJsonConverter))]
        public DateTime? UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }
    }

    public class LocalSupplierInvoiceDetailDto
    {
        public string InvoiceGUID { get; set; } = string.Empty;
        public string? AppGUID { get; set; }
        public string? PcGUID { get; set; }
        public string? StoreCode { get; set; }
        public string? StoreName { get; set; }
        public string? SupplierCode { get; set; }
        public string? SupplierName { get; set; }
        public string? InvoiceNo { get; set; }
        public int? VoucherType { get; set; }
        public DateTime? OrderDate { get; set; }
        public DateTime? InboundDate { get; set; }
        public decimal? TotalAmount { get; set; }
        public decimal? ReceivedTotalAmount { get; set; }
        public string? VoucherImage { get; set; }
        public string? Remarks { get; set; }
        public string? ImportTemplate { get; set; }
        public int? FlowStatus { get; set; }
        public int? InboundStatus { get; set; }
        [JsonConverter(typeof(LocalSupplierInvoiceAuditUtcDateTimeJsonConverter))]
        public DateTime CreatedAt { get; set; }

        [JsonConverter(typeof(NullableLocalSupplierInvoiceAuditUtcDateTimeJsonConverter))]
        public DateTime? UpdatedAt { get; set; }
    }

    public class LocalSupplierInvoiceItemDto
    {
        /// <summary>当前商品主档类型，仅查询展示；未匹配或主档已删除时为空。</summary>
        public int? ProductType { get; set; }
        public string DetailGUID { get; set; } = string.Empty;
        public string? InvoiceGUID { get; set; }
        public string? StoreCode { get; set; }
        public string? SupplierCode { get; set; }
        public string? ProductTagGUID { get; set; }
        public string? ProductCategoryGUID { get; set; }
        public string? StoreProductCode { get; set; }
        public string? ProductCode { get; set; }
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
        public List<string> AdditionalBarcodes { get; set; } = new();
        [JsonIgnore]
        public string? AdditionalBarcodesJson { get; set; }
        public string? ProductName { get; set; }
        public string? Specification { get; set; }
        public string? Unit { get; set; }
        public decimal? Quantity { get; set; }
        public decimal? LastPurchasePrice { get; set; }
        public decimal? PurchasePrice { get; set; }
        public decimal? RetailPrice { get; set; }
        public decimal? Amount { get; set; }
        public int? ExistingProductCount { get; set; }
        /// <summary>本单新品：明细关联的商品由本进货单新建，口径同列表的 CreatedHereProductDetailCount。</summary>
        public bool IsCreatedByThisInvoice { get; set; }
        public int? BarcodeStatus { get; set; }
        public int? BarcodeMatchCount { get; set; }
        public string? ProductImage { get; set; }
        public int? ActivityType { get; set; }
        public decimal? DiscountRate { get; set; }
        public bool? AutoPricing { get; set; }
        public decimal? PricingFloatRate { get; set; }
        public decimal? NewAutoRetailPrice { get; set; }
        public bool? IsSpecialProduct { get; set; }
        public string? OldStoreProductCode { get; set; }
    }

    public class UpdateLastPurchasePricesRequest
    {
        /// <summary>
        /// 要强制刷新上次进货价的明细；为空时刷新整张进货单。
        /// </summary>
        public List<string>? DetailGuids { get; set; }
    }

    public class UpdateLastPurchasePricesResultDto
    {
        public int Total { get; set; }
        public int Updated { get; set; }
        public int Skipped { get; set; }
        public List<string> Errors { get; set; } = new();
    }

    public class CreateInvoiceRequest
    {
        public string StoreCode { get; set; } = string.Empty;
        public string SupplierCode { get; set; } = string.Empty;
        public string InvoiceNo { get; set; } = string.Empty;
        public DateTime? OrderDate { get; set; }
        public DateTime? InboundDate { get; set; }
        public string? Remarks { get; set; }
        public List<PastedDetailItem> Items { get; set; } = new();
    }

    public class PastedDetailItem
    {
        public string? ItemNumber { get; set; }
        public string? NameOrBarcode { get; set; }
        public string? ProductName { get; set; }
        public string? Barcode { get; set; }
        public int Quantity { get; set; }
        public decimal Price { get; set; }
        public string? StoreProductCode { get; set; }
        public string? ProductCode { get; set; }
        public decimal? LastPurchasePrice { get; set; }
        public decimal? RetailPrice { get; set; }
        public bool? AutoPricing { get; set; }
        public decimal? PricingFloatRate { get; set; }
        public decimal? NewAutoRetailPrice { get; set; }
        public bool? IsSpecialProduct { get; set; }
    }

    public static class LocalSupplierInvoiceImportMatchStatus
    {
        public const string Matched = "matched";
        public const string Missing = "missing";
        public const string NotFound = "not_found";
        public const string MultipleMatches = "multiple_matches";
    }

    public class LocalSupplierInvoiceImportSourceColumnDto
    {
        public string Key { get; set; } = string.Empty;
        public string Header { get; set; } = string.Empty;
    }

    public class LocalSupplierInvoiceImportMappingDto
    {
        public string? ItemNumberColumnKey { get; set; }
        public string? BarcodeColumnKey { get; set; }
        public string? ProductNameColumnKey { get; set; }
        public string? QuantityColumnKey { get; set; }
        public string? PriceColumnKey { get; set; }
    }

    public class LocalSupplierInvoiceImportConfirmMappingDto
    {
        [Required]
        public string ItemNumberColumnKey { get; set; } = string.Empty;

        [Required]
        public string BarcodeColumnKey { get; set; } = string.Empty;

        [Required]
        public string ProductNameColumnKey { get; set; } = string.Empty;

        [Required]
        public string QuantityColumnKey { get; set; } = string.Empty;

        [Required]
        public string PriceColumnKey { get; set; } = string.Empty;
    }

    public class LocalSupplierInvoiceImportLookupOptionDto
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    public class LocalSupplierInvoiceImportMatchDto
    {
        public string Status { get; set; } = LocalSupplierInvoiceImportMatchStatus.Missing;
        public string? RawValue { get; set; }
        public string? SelectedCode { get; set; }
        public string? SelectedName { get; set; }
        public List<LocalSupplierInvoiceImportLookupOptionDto> Candidates { get; set; } = new();
    }

    public class LocalSupplierInvoiceImportHeaderDto
    {
        public string? StoreCode { get; set; }
        public string? StoreName { get; set; }
        public string? SupplierCode { get; set; }
        public string? SupplierName { get; set; }
        public string? InvoiceNo { get; set; }
        public DateTime? OrderDate { get; set; }
        public DateTime? InboundDate { get; set; }
        public string? Remarks { get; set; }
        public LocalSupplierInvoiceImportMatchDto StoreMatch { get; set; } = new();
        public LocalSupplierInvoiceImportMatchDto SupplierMatch { get; set; } = new();
    }

    public class LocalSupplierInvoiceImportLineDto
    {
        public int RowNumber { get; set; }
        public Dictionary<string, string?> RawValues { get; set; } = new();
    }

    public class LocalSupplierInvoiceImportPreviewDto
    {
        public string FileName { get; set; } = string.Empty;
        public string FileType { get; set; } = string.Empty;
        public List<LocalSupplierInvoiceImportSourceColumnDto> SourceColumns { get; set; } = new();
        public LocalSupplierInvoiceImportMappingDto RecommendedMapping { get; set; } = new();
        public LocalSupplierInvoiceImportHeaderDto Header { get; set; } = new();
        public List<LocalSupplierInvoiceImportLineDto> Lines { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public List<string> Errors { get; set; } = new();
    }

    public class LocalSupplierInvoiceImportConfirmRequest
    {
        [Required]
        public List<LocalSupplierInvoiceImportSourceColumnDto> SourceColumns { get; set; } = new();

        [Required]
        public LocalSupplierInvoiceImportHeaderDto Header { get; set; } = new();

        [Required]
        public LocalSupplierInvoiceImportConfirmMappingDto Mapping { get; set; } = new();

        [Required]
        public List<LocalSupplierInvoiceImportLineDto> Lines { get; set; } = new();
    }

    public class LocalSupplierInvoiceImportConfirmResultDto
    {
        public string InvoiceGuid { get; set; } = string.Empty;
        public List<string> Warnings { get; set; } = new();
    }

    public class DetectSupplierItemRequest
    {
        public string StoreCode { get; set; } = string.Empty;
        public string SupplierCode { get; set; } = string.Empty;
        public List<DetectSupplierItem> Items { get; set; } = new();
    }

    public class DetectSupplierItem
    {
        public string? ItemNumber { get; set; }
    }

    public class SupplierItemDetectResult
    {
        public bool Exists { get; set; }
        public string? ProductCode { get; set; }
        public string? StoreProductCode { get; set; }
        public string? ProductName { get; set; }
        public string? ProductImage { get; set; }
        public decimal? CurrentPurchasePrice { get; set; }
        public decimal? CurrentRetailPrice { get; set; }
        public string? Error { get; set; }
    }

    public class DetectBarcodeRequest
    {
        public string StoreCode { get; set; } = string.Empty;
        public List<DetectBarcodeItem> Items { get; set; } = new();
    }

    public class DetectBarcodeItem
    {
        public string? Barcode { get; set; }
    }

    public class BarcodeDetectResult
    {
        public bool Matched { get; set; }
        public int MatchCount { get; set; }
        public bool OverTwo { get; set; }
        public List<string>? ProductCodes { get; set; }
        public List<string>? StoreProductCodes { get; set; }
        public List<string>? ProductNames { get; set; }
        public string? FirstProductImage { get; set; }
        public string? Error { get; set; }
    }

    public class UpdateInvoiceRequest
    {
        public string? StoreCode { get; set; }
        public string? SupplierCode { get; set; }
        public string? InvoiceNo { get; set; }
        public DateTime? OrderDate { get; set; }
        public DateTime? InboundDate { get; set; }
        public string? Remarks { get; set; }
        public string? VoucherImage { get; set; }
        public int? FlowStatus { get; set; }
        public int? InboundStatus { get; set; }
    }

    public class InvoiceDetailUpsertItemDto
    {
        public string? DetailGUID { get; set; }
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
        public List<string> AdditionalBarcodes { get; set; } = new();
        public string? ProductName { get; set; }
        public string? ProductCategoryGUID { get; set; }
        public string? StoreProductCode { get; set; }
        public string? ProductCode { get; set; }
        public decimal? Quantity { get; set; }
        public decimal? LastPurchasePrice { get; set; }
        public decimal? PurchasePrice { get; set; }
        public decimal? RetailPrice { get; set; }
        public decimal? Amount { get; set; }
        public int? ActivityType { get; set; }
        public decimal? DiscountRate { get; set; }
        public bool? AutoPricing { get; set; }
        public decimal? PricingFloatRate { get; set; }
        public decimal? NewAutoRetailPrice { get; set; }
        public bool? IsSpecialProduct { get; set; }
    }

    /// <summary>
    /// 批量编辑进货单明细请求DTO
    /// </summary>
    public class BatchUpdateInvoiceDetailsRequest
    {
        /// <summary>
        /// 要更新的明细列表，批量编辑只需要 DetailGUID。
        /// </summary>
        [Required]
        public List<InvoiceDetailUpsertItemDto> Items { get; set; } = new();

        /// <summary>
        /// 要更新的字段配置；只写入勾选字段。
        /// </summary>
        [Required]
        public UpdateToStorePricesFields EditFields { get; set; } = new();
    }

    /// <summary>
    /// 更新到分店价格请求DTO
    /// </summary>
    public class UpdateToStorePricesRequest
    {
        /// <summary>
        /// 订单GUID
        /// </summary>
        [Required]
        public string InvoiceGuid { get; set; } = string.Empty;

        /// <summary>
        /// 要更新的明细GUID列表
        /// </summary>
        [Required]
        public List<string> DetailGuids { get; set; } = new();

        /// <summary>
        /// 目标分店代码列表
        /// </summary>
        [Required]
        public List<string> TargetStoreCodes { get; set; } = new();

        /// <summary>
        /// 要更新的字段配置
        /// </summary>
        [Required]
        public UpdateToStorePricesFields UpdateFields { get; set; } = new();
    }

    /// <summary>
    /// 更新到分店价格结果DTO
    /// </summary>
    public class UpdateToStorePricesResultDto : BatchResultDto
    {
        public int Skipped { get; set; }
        public int UpdatedPurchasePrices { get; set; }
    }

    public static class LocalSupplierInvoiceBatchUpdateJobStatusConstants
    {
        public const string Running = "Running";
        public const string Succeeded = "Succeeded";
        public const string Failed = "Failed";
    }

    /// <summary>
    /// 本地进货单批量后台任务基础字段。
    /// </summary>
    public abstract class LocalSupplierInvoiceBatchUpdateJobDtoBase
    {
        public string JobId { get; set; } = string.Empty;
        public string InvoiceGuid { get; set; } = string.Empty;
        public List<string> TargetStoreCodes { get; set; } = new();
        public string OperationId { get; set; } = string.Empty;
        public string Status { get; set; } = LocalSupplierInvoiceBatchUpdateJobStatusConstants.Running;
        public bool IsDuplicateRequest { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string? Message { get; set; }
    }

    /// <summary>
    /// 更新到分店价格后台任务状态。
    /// </summary>
    public class LocalSupplierInvoiceUpdateToStorePricesJobDto
        : LocalSupplierInvoiceBatchUpdateJobDtoBase
    {
        public UpdateToStorePricesResultDto? Result { get; set; }
    }

    /// <summary>
    /// 更新HQ商品后台任务状态。
    /// </summary>
    public class LocalSupplierInvoiceUpdateHqProductsJobDto
        : LocalSupplierInvoiceBatchUpdateJobDtoBase
    {
        public UpdateHqProductsResult? Result { get; set; }
    }

    /// <summary>
    /// 粘贴明细后台任务状态。
    /// </summary>
    public class LocalSupplierInvoicePasteDetailsJobDto
        : LocalSupplierInvoiceBatchUpdateJobDtoBase
    {
        public BatchResultDto? Result { get; set; }
    }

    /// <summary>
    /// 商品检测后台任务状态。
    /// </summary>
    public class LocalSupplierInvoiceCheckProductsJobDto
        : LocalSupplierInvoiceBatchUpdateJobDtoBase
    {
        public CheckProductsResponseDto? Result { get; set; }
    }

    /// <summary>
    /// 同步本地进货单明细商品到HQ请求DTO
    /// </summary>
    public class EnsureHqProductsRequest
    {
        /// <summary>
        /// 要同步的明细GUID列表
        /// </summary>
        public List<string> DetailGuids { get; set; } = new();

        /// <summary>
        /// 更新已有商品时只同步这些目标分店
        /// </summary>
        public List<string> TargetStoreCodes { get; set; } = new();

        /// <summary>
        /// 幂等键，当前后端保留字段以兼容前端请求
        /// </summary>
        public string? IdempotencyKey { get; set; }
    }

    /// <summary>
    /// 同步本地进货单明细商品到HQ结果DTO
    /// </summary>
    public class EnsureHqProductsResult
    {
        public int Total { get; set; }
        public int HqExisting { get; set; }
        public int HbwebCreated { get; set; }
        public int HqCreated { get; set; }
        public int HqSynced { get; set; }
        public int HqPurchasePricesUpdated { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public List<EnsureHqProductError> Errors { get; set; } = new();
    }

    /// <summary>
    /// 按指定字段更新HQ商品分店价格请求DTO
    /// </summary>
    public class UpdateHqProductsRequest
    {
        /// <summary>
        /// 要更新的明细GUID列表
        /// </summary>
        [Required]
        public List<string> DetailGuids { get; set; } = new();

        /// <summary>
        /// 目标分店代码列表
        /// </summary>
        [Required]
        public List<string> TargetStoreCodes { get; set; } = new();

        /// <summary>
        /// 要写入HQ的字段配置；只写入为true的字段
        /// </summary>
        [Required]
        public UpdateToStorePricesFields UpdateFields { get; set; } = new();

        /// <summary>
        /// 幂等键，当前后端保留字段以兼容前端请求
        /// </summary>
        public string? IdempotencyKey { get; set; }
    }

    /// <summary>
    /// 按指定字段更新HQ商品分店价格结果DTO
    /// </summary>
    public class UpdateHqProductsResult : EnsureHqProductsResult
    {
        public int Updated { get; set; }
        public int HqRetailPricesUpdated { get; set; }
        public int HqAutoPricingUpdated { get; set; }
        public int HqSpecialProductsUpdated { get; set; }
        public int HqDiscountRatesUpdated { get; set; }
        public int HqProductSetCodesCreated { get; set; }
        public int HqProductSetCodesUpdated { get; set; }
        public int HqStoreMultiCodesCreated { get; set; }
        public int HqStoreMultiCodesUpdated { get; set; }
    }

    /// <summary>
    /// 同步HQ商品逐行错误
    /// </summary>
    public class EnsureHqProductError
    {
        public string DetailGuid { get; set; } = string.Empty;
        public string? StoreCode { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// 更新到分店价格字段配置DTO
    /// </summary>
    public class UpdateToStorePricesFields
    {
        /// <summary>
        /// 是否更新进价
        /// </summary>
        public bool UpdatePurchasePrice { get; set; }

        /// <summary>
        /// 进价值（当UpdatePurchasePrice为true时使用）
        /// </summary>
        public decimal? PurchasePrice { get; set; }

        /// <summary>
        /// 是否更新零售价
        /// </summary>
        public bool UpdateRetailPrice { get; set; }

        /// <summary>
        /// 零售价值（当UpdateRetailPrice为true时使用）
        /// </summary>
        public decimal? RetailPrice { get; set; }

        /// <summary>
        /// 是否更新自动定价标志
        /// </summary>
        public bool UpdateIsAutoPricing { get; set; }

        /// <summary>
        /// 自动定价标志值（当UpdateIsAutoPricing为true时使用）
        /// </summary>
        public bool? IsAutoPricing { get; set; }

        /// <summary>
        /// 是否更新特价产品标志
        /// </summary>
        public bool UpdateIsSpecialProduct { get; set; }

        /// <summary>
        /// 特价产品标志值（当UpdateIsSpecialProduct为true时使用）
        /// </summary>
        public bool? IsSpecialProduct { get; set; }

        /// <summary>
        /// 是否更新折扣率
        /// </summary>
        public bool UpdateDiscountRate { get; set; }

        /// <summary>
        /// 折扣率值（当UpdateDiscountRate为true时使用）
        /// </summary>
        public decimal? DiscountRate { get; set; }
    }

    /// <summary>
    /// 检测商品请求DTO
    /// </summary>
    public class CheckProductsRequest
    {
        /// <summary>
        /// 订单GUID
        /// </summary>
        public string InvoiceGuid { get; set; } = string.Empty;

        /// <summary>
        /// 要检测的明细GUID列表（为空则检测所有）
        /// </summary>
        public List<string>? DetailGuids { get; set; }

        /// <summary>
        /// 为 true 时跳过已执行（ActivityType=99）的明细：重新检测会把操作类型覆盖成默认建议，
        /// 列表批量检测必须保留「已执行」标记。默认 false，单张检测行为不变。
        /// </summary>
        public bool ExcludeExecutedDetails { get; set; }
    }

    /// <summary>
    /// 列表批量商品检测请求：按提交顺序逐张检测。
    /// </summary>
    public class StartBatchCheckProductsRequest
    {
        public List<string> InvoiceGuids { get; set; } = new();
    }

    public static class LocalSupplierInvoiceBatchCheckProductsLimits
    {
        /// <summary>单次批量检测最多进货单数。</summary>
        public const int MaxInvoices = 50;
    }

    /// <summary>批量检测任务整体状态。</summary>
    public static class LocalSupplierInvoiceBatchCheckProductsJobStatusConstants
    {
        public const string Running = "Running";
        public const string Completed = "Completed";
        public const string Cancelled = "Cancelled";
    }

    /// <summary>批量检测中单张进货单的状态。</summary>
    public static class LocalSupplierInvoiceBatchCheckProductsItemStatusConstants
    {
        public const string Queued = "Queued";
        public const string Running = "Running";
        public const string Succeeded = "Succeeded";
        public const string Failed = "Failed";
        public const string Skipped = "Skipped";
    }

    /// <summary>
    /// 列表批量商品检测后台任务状态。
    /// </summary>
    public class LocalSupplierInvoiceBatchCheckProductsJobDto
    {
        public string JobId { get; set; } = string.Empty;
        public string OperationId { get; set; } = string.Empty;
        public string Status { get; set; } = LocalSupplierInvoiceBatchCheckProductsJobStatusConstants.Running;
        public bool IsDuplicateRequest { get; set; }
        /// <summary>前面还有其他批量检测任务在执行，本任务尚未开始。</summary>
        public bool IsWaiting { get; set; }
        public bool CancelRequested { get; set; }
        /// <summary>任务涉及的分店编码，用于查询进度时校验分店范围。</summary>
        public List<string> StoreCodes { get; set; } = new();
        public DateTime CreatedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string? Message { get; set; }
        public int Total { get; set; }
        public int Processed { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
        public List<LocalSupplierInvoiceBatchCheckProductsItemDto> Items { get; set; } = new();
    }

    public class LocalSupplierInvoiceBatchCheckProductsItemDto
    {
        public string InvoiceGuid { get; set; } = string.Empty;
        public string Status { get; set; } = LocalSupplierInvoiceBatchCheckProductsItemStatusConstants.Queued;
        public string? Message { get; set; }
        /// <summary>本次实际检测的明细行数（已执行行不计入）。</summary>
        public int CheckedCount { get; set; }
        public DateTime? CompletedAt { get; set; }
    }

    /// <summary>
    /// 检测商品结果DTO
    /// </summary>
    public class ProductCheckResultDto
    {
        /// <summary>
        /// 明细GUID
        /// </summary>
        public string DetailGuid { get; set; } = string.Empty;

        /// <summary>
        /// 商品状态：0=未检测，1=已存在，2=不存在
        /// </summary>
        public int ProductStatus { get; set; }

        /// <summary>
        /// 条码状态：0=未检测，1=正常，2=异常
        /// </summary>
        public int BarcodeStatus { get; set; }

        /// <summary>
        /// 商品存在数量
        /// </summary>
        public int ExistingProductCount { get; set; }

        /// <summary>
        /// 自动定价
        /// </summary>
        public bool? AutoPricing { get; set; }

        /// <summary>
        /// 是否特殊商品
        /// </summary>
        public bool? IsSpecialProduct { get; set; }

        /// <summary>
        /// 折扣率
        /// </summary>
        public decimal? DiscountRate { get; set; }

        /// <summary>
        /// 分店商品编码
        /// </summary>
        public string? StoreProductCode { get; set; }

        /// <summary>
        /// 上次进货价
        /// </summary>
        public decimal? LastPurchasePrice { get; set; }

        /// <summary>
        /// 定价浮动率
        /// </summary>
        public decimal? PricingFloatRate { get; set; }

        /// <summary>
        /// 新自动零售价
        /// </summary>
        public decimal? NewAutoRetailPrice { get; set; }

        /// <summary>
        /// 商品信息
        /// </summary>
        public ProductCheckInfoDto? ProductInfo { get; set; }

        /// <summary>
        /// 条码匹配数量
        /// </summary>
        public int BarcodeMatchCount { get; set; }

        /// <summary>
        /// 默认操作类型
        /// </summary>
        public int DefaultAction { get; set; }
    }

    /// <summary>
    /// 商品信息DTO（用于检测）
    /// </summary>
    public class ProductCheckInfoDto
    {
        public string? ProductCode { get; set; }
        public string? ProductName { get; set; }
        public decimal? PurchasePrice { get; set; }
        public decimal? RetailPrice { get; set; }
        public string? ProductImage { get; set; }
        public string? StoreProductCode { get; set; }
    }

    /// <summary>
    /// 检测商品响应DTO
    /// </summary>
    public class CheckProductsResponseDto
    {
        /// <summary>
        /// 检测结果列表
        /// </summary>
        public List<ProductCheckResultDto> Results { get; set; } = new();

        /// <summary>
        /// 汇总信息
        /// </summary>
        public CheckProductsSummaryDto Summary { get; set; } = new();
    }

    /// <summary>
    /// 检测商品汇总DTO
    /// </summary>
    public class CheckProductsSummaryDto
    {
        public int Total { get; set; }
        public int ProductExists { get; set; }
        public int ProductNotExists { get; set; }
        public int BarcodeNormal { get; set; }
        public int BarcodeAbnormal { get; set; }
    }

    /// <summary>
    /// 粘贴数据请求DTO
    /// </summary>
    public class PasteDetailsRequest
    {
        /// <summary>
        /// 订单GUID
        /// </summary>
        public string InvoiceGuid { get; set; } = string.Empty;

        /// <summary>
        /// 模式：append=追加，replace=覆盖
        /// </summary>
        public string Mode { get; set; } = "replace";

        /// <summary>
        /// 要粘贴的明细列表
        /// </summary>
        public List<PastedDetailItemDto> Items { get; set; } = new();
    }

    /// <summary>
    /// 粘贴的明细项DTO
    /// </summary>
    public class PastedDetailItemDto
    {
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
        public List<string> AdditionalBarcodes { get; set; } = new();
        public string? ProductName { get; set; }
        public decimal? Quantity { get; set; }
        public decimal? PurchasePrice { get; set; }
        public decimal? NewAutoRetailPrice { get; set; }
        /// <summary>
        /// 零售价（新商品仍默认开启自动定价）
        /// </summary>
        public decimal? RetailPrice { get; set; }
    }

    /// <summary>
    /// 更新明细操作类型请求DTO
    /// </summary>
    public class UpdateDetailActionRequest
    {
        /// <summary>
        /// 操作类型：0=无操作，1=新建商品，2=更新进货价，3=等待操作，4=更新货号，5=添加多码
        /// 99=已执行完成，仅服务端内部写入，客户端不可提交
        /// </summary>
        public int Action { get; set; }
    }

    /// <summary>
    /// 批量更新明细操作类型请求DTO
    /// </summary>
    public class BatchUpdateDetailActionRequest
    {
        /// <summary>
        /// 明细GUID列表
        /// </summary>
        public List<string> DetailGuids { get; set; } = new();

        /// <summary>
        /// 操作类型：0=无操作，1=新建商品，2=更新进货价，3=等待操作，4=更新货号，5=添加多码
        /// 99=已执行完成，仅服务端内部写入，客户端不可提交
        /// </summary>
        public int Action { get; set; }
    }

    /// <summary>
    /// 条码异常匹配商品DTO
    /// </summary>
    public class BarcodeAbnormalMatchedProductDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string SupplierCode { get; set; } = string.Empty;
        public string? SupplierName { get; set; }
        public string? ItemNumber { get; set; }
        public string Barcode { get; set; } = string.Empty;
        public string? ProductImage { get; set; }
        public bool IsMultiCode { get; set; }
        public bool IsBundle { get; set; }
        public int? ProductType { get; set; }
    }

    /// <summary>
    /// 条码异常明细DTO
    /// </summary>
    public class BarcodeAbnormalDetailDto
    {
        public string DetailGuid { get; set; } = string.Empty;
        public string ItemNumber { get; set; } = string.Empty;
        public string Barcode { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public int ProductStatus { get; set; }
        public string? MatchedProductCode { get; set; }
        public List<BarcodeAbnormalMatchedProductDto> MatchedProducts { get; set; } = new();
    }

    /// <summary>
    /// 获取条码异常明细响应DTO
    /// </summary>
    public class GetBarcodeAbnormalDetailsResponse
    {
        public List<BarcodeAbnormalDetailDto> Details { get; set; } = new();
    }

    /// <summary>
    /// 按条码查询匹配商品响应DTO
    /// </summary>
    public class GetProductsByBarcodeResponse
    {
        public string Barcode { get; set; } = string.Empty;
        public List<BarcodeAbnormalMatchedProductDto> MatchedProducts { get; set; } = new();
    }

    /// <summary>
    /// 按商品编码查询匹配商品响应DTO
    /// </summary>
    public class GetProductsByProductCodeResponse
    {
        public string ProductCode { get; set; } = string.Empty;
        public List<BarcodeAbnormalMatchedProductDto> MatchedProducts { get; set; } = new();
    }

    /// <summary>
    /// 检查随货单号是否存在请求DTO
    /// </summary>
    public class CheckInvoiceNoExistsRequest
    {
        public string StoreCode { get; set; } = string.Empty;
        public string SupplierCode { get; set; } = string.Empty;
        public string InvoiceNo { get; set; } = string.Empty;
    }

    /// <summary>
    /// 检查随货单号是否存在响应DTO
    /// </summary>
    public class InvoiceNoCheckResult
    {
        public bool Exists { get; set; }
        public string? ExistingInvoiceNo { get; set; }
        public DateTime? ExistingCreatedAt { get; set; }
    }

    /// <summary>
    /// 明细操作类型枚举
    /// </summary>
    public enum DetailAction
    {
        /// <summary>无操作</summary>
        None = 0,

        /// <summary>新建商品</summary>
        CreateProduct = 1,

        /// <summary>更新进货价</summary>
        UpdatePurchasePrice = 2,

        /// <summary>等待操作</summary>
        WaitForOperation = 3,

        /// <summary>更新货号</summary>
        UpdateItemNumber = 4,

        /// <summary>添加多码</summary>
        AddMultiCode = 5,
    }

    /// <summary>
    /// 批量执行操作请求DTO
    /// </summary>
    public class BatchExecuteActionsRequestDto
    {
        /// <summary>
        /// 进货单GUID
        /// </summary>
        public string InvoiceGuid { get; set; } = string.Empty;

        /// <summary>
        /// 要执行的明细GUID列表
        /// </summary>
        public List<string> DetailGuids { get; set; } = new();

        /// <summary>
        /// 用户确认时看到的明细动作快照
        /// </summary>
        public List<BatchExecuteExpectedActionDto> ExpectedActions { get; set; } = new();

        /// <summary>
        /// 用户确认时的新建商品数量
        /// </summary>
        public int? ConfirmedCreateProductCount { get; set; }

        /// <summary>
        /// 用户确认时间
        /// </summary>
        public DateTime? ConfirmedAt { get; set; }

        /// <summary>
        /// 新商品带副码时，用户选择的主档商品类型。
        /// </summary>
        public List<BatchExecuteNewProductProductTypeSelectionDto> NewProductProductTypeSelections { get; set; } = new();
    }

    /// <summary>
    /// 新商品带副码时的商品类型选择。
    /// </summary>
    public class BatchExecuteNewProductProductTypeSelectionDto
    {
        /// <summary>
        /// 明细GUID
        /// </summary>
        public string DetailGuid { get; set; } = string.Empty;

        /// <summary>
        /// 商品主档类型：1=套装，2=多码。
        /// </summary>
        public int ProductType { get; set; }
    }

    /// <summary>
    /// 批量执行时前端确认的明细动作
    /// </summary>
    public class BatchExecuteExpectedActionDto
    {
        /// <summary>
        /// 明细GUID
        /// </summary>
        public string DetailGuid { get; set; } = string.Empty;

        /// <summary>
        /// 明细操作类型
        /// </summary>
        public int? ActivityType { get; set; }

        /// <summary>
        /// 兼容前端 action 字段命名
        /// </summary>
        public int? Action { get; set; }

        /// <summary>
        /// 取前端传入的动作值，优先使用 ActivityType
        /// </summary>
        public int? GetActionValue()
        {
            return ActivityType ?? Action;
        }
    }

    /// <summary>
    /// 批量执行确认契约不一致详情
    /// </summary>
    public class BatchExecuteConfirmationDetailsDto
    {
        /// <summary>
        /// 请求中的明细数量
        /// </summary>
        public int RequestedDetailCount { get; set; }

        /// <summary>
        /// 数据库当前明细数量
        /// </summary>
        public int CurrentDetailCount { get; set; }

        /// <summary>
        /// 请求确认的新建商品数量
        /// </summary>
        public int? ConfirmedCreateProductCount { get; set; }

        /// <summary>
        /// 数据库当前新建商品数量
        /// </summary>
        public int CurrentCreateProductCount { get; set; }

        /// <summary>
        /// 不一致的明细列表
        /// </summary>
        public List<BatchExecuteConfirmationMismatchDetailDto> MismatchedDetails { get; set; } = new();
    }

    /// <summary>
    /// 批量执行确认契约不一致的明细项
    /// </summary>
    public class BatchExecuteConfirmationMismatchDetailDto
    {
        /// <summary>
        /// 明细GUID
        /// </summary>
        public string DetailGuid { get; set; } = string.Empty;

        /// <summary>
        /// 请求确认的动作
        /// </summary>
        public int? ExpectedAction { get; set; }

        /// <summary>
        /// 数据库当前动作
        /// </summary>
        public int? CurrentAction { get; set; }

        /// <summary>
        /// 不一致原因
        /// </summary>
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// 批量执行操作结果DTO
    /// </summary>
    public class BatchExecuteActionsResultDto
    {
        /// <summary>
        /// 创建商品成功数
        /// </summary>
        public int CreatedProducts { get; set; }

        /// <summary>
        /// 更新进货价成功数
        /// </summary>
        public int UpdatedPurchasePrices { get; set; }

        /// <summary>
        /// 更新货号成功数
        /// </summary>
        public int UpdatedItemNumbers { get; set; }

        /// <summary>
        /// 添加多码成功数
        /// </summary>
        public int AddedMultiCodes { get; set; }

        /// <summary>
        /// 跳过数（无操作/等待操作）
        /// </summary>
        public int Skipped { get; set; }

        /// <summary>
        /// 失败数
        /// </summary>
        public int Failed { get; set; }

        /// <summary>
        /// 错误信息列表
        /// </summary>
        public List<string> Errors { get; set; } = new();
    }

    public class PushToHqRequest
    {
        public List<string> InvoiceGuids { get; set; } = new();
    }

    public class LocalSupplierInvoiceHqSyncRequest
    {
        public List<string>? SelectedStoreCodes { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
    }

    public class LocalSupplierInvoiceHqSyncResult
    {
        public string RequestId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public long DurationMs { get; set; }
        public int InvoiceAddedCount { get; set; }
        public int InvoiceUpdatedCount { get; set; }
        public int DetailAddedCount { get; set; }
        public int DetailUpdatedCount { get; set; }
        public int TotalProcessed { get; set; }
        public List<string> Errors { get; set; } = new();
    }
}
