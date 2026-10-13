namespace BlazorApp.Shared.DTOs
{
    public class StoreProductLookupRequestDto
    {
        public string Keyword { get; set; } = string.Empty;
        public string? StoreCode { get; set; }
    }

    /// <summary>
    /// 扫码价签一次请求结果。在线扫码使用此合同，避免客户端先查候选再查详情。
    /// </summary>
    public class StoreProductScanLabelResultDto
    {
        public List<StoreProductLookupItemDto> Candidates { get; set; } = new();
        public StoreProductDetailDto? Detail { get; set; }
        public StoreProductPrintTargetDto? PrintTarget { get; set; }
    }

    public class StoreProductPrintTargetDto
    {
        /// <summary>product、set、multi 或 clearance。</summary>
        public string Kind { get; set; } = string.Empty;
        public string Barcode { get; set; } = string.Empty;
        public decimal RetailPrice { get; set; }
        public decimal? DiscountRate { get; set; }
        public string ProductCode { get; set; } = string.Empty;
        public string? CodeId { get; set; }
        public string? StoreCode { get; set; }
    }

    public class StoreProductLookupItemDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
        public string? ProductImage { get; set; }
        public string? MatchSource { get; set; }
        public string? MatchValue { get; set; }
        public string? ProductTypeLabel { get; set; }
        public string? Grade { get; set; }
    }

    public class StoreProductDetailDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
        public string? ProductImage { get; set; }
        public int? ProductType { get; set; }
        public string? ProductTypeLabel { get; set; }
        public string? Grade { get; set; }
        public string? LocalSupplierCode { get; set; }
        public string? LocalSupplierName { get; set; }
        public StoreProductStorePriceDto? StorePrice { get; set; }
        public StoreProductClearancePriceDto? ClearancePrice { get; set; }
        public List<StoreProductSetCodeDto> SetCodes { get; set; } = new();
        public List<StoreProductMultiCodeDto> MultiCodes { get; set; } = new();
        public int SetCodeCount { get; set; }
        public int MultiCodeCount { get; set; }
        public bool CodesIncluded { get; set; } = true;
    }

    public class StoreProductCodePageDto<T>
    {
        public List<T> Items { get; set; } = new();
        public int TotalCount { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public bool HasMore { get; set; }
    }

    public class StoreProductStorePriceDto
    {
        public string Uuid { get; set; } = string.Empty;
        public string? StoreCode { get; set; }
        public string? StoreName { get; set; }
        public string? ProductCode { get; set; }
        public string? StoreProductCode { get; set; }
        public string? SupplierCode { get; set; }
        public decimal? PurchasePrice { get; set; }
        public decimal? RetailPrice { get; set; }
        public decimal? DiscountRate { get; set; }
        public bool IsAutoPricing { get; set; }
        public bool IsSpecialProduct { get; set; }
        public bool IsActive { get; set; }
        public decimal? Rate { get; set; }
        public string? StrategySourceLabel { get; set; }
        public string? StrategyRuleLabel { get; set; }
        public ProductHqSyncOperationStatusDto? HqSync { get; set; }
    }

    public class StoreProductMultiCodeDto
    {
        public string Uuid { get; set; } = string.Empty;
        public string SetCodeId { get; set; } = string.Empty;
        public string? StoreCode { get; set; }
        public string? ProductCode { get; set; }
        public string? MultiCodeProductCode { get; set; }
        public string? StoreMultiCodeProductCode { get; set; }
        public string? Barcode { get; set; }
        public decimal? PurchasePrice { get; set; }
        public decimal? RetailPrice { get; set; }
        public decimal? DiscountRate { get; set; }
        public bool IsAutoPricing { get; set; }
        public bool IsSpecialProduct { get; set; }
        public bool IsActive { get; set; }
        public decimal? Rate { get; set; }
        public string? StrategySourceLabel { get; set; }
        public string? StrategyRuleLabel { get; set; }
        public ProductHqSyncOperationStatusDto? HqSync { get; set; }
    }

    public class StoreProductClearancePriceDto
    {
        public string Uuid { get; set; } = string.Empty;
        public string? StoreCode { get; set; }
        public string? StoreName { get; set; }
        public string? ProductCode { get; set; }
        public string? ClearanceBarcode { get; set; }
        public decimal? ClearancePrice { get; set; }
        public ProductHqSyncOperationStatusDto? HqSync { get; set; }
    }

    public class StoreProductSetCodeDto
    {
        public string SetCodeId { get; set; } = string.Empty;
        public string ProductCode { get; set; } = string.Empty;
        public string SetProductCode { get; set; } = string.Empty;
        public string SetItemNumber { get; set; } = string.Empty;
        public string? SetBarcode { get; set; }
        public decimal? SetPurchasePrice { get; set; }
        public decimal? SetRetailPrice { get; set; }
        public int SetQuantity { get; set; }
        public int SetType { get; set; }
        public string? SetTypeDescription { get; set; }
        public bool IsActive { get; set; }
        public ProductHqSyncOperationStatusDto? HqSync { get; set; }
    }

    public class UpdateStoreProductPriceDto
    {
        public decimal? PurchasePrice { get; set; }
        public decimal? RetailPrice { get; set; }
        public decimal? DiscountRate { get; set; }
        public bool? IsAutoPricing { get; set; }
        public bool? IsSpecialProduct { get; set; }
        public bool? IsActive { get; set; }
    }

    public class SyncStoreProductWarehousePriceRequestDto
    {
        public bool ConfirmRetailPrice { get; set; }
        public decimal? ExpectedWarehousePurchasePrice { get; set; }
        public decimal? ExpectedWarehouseRetailPrice { get; set; }
        public decimal? ExpectedStorePurchasePrice { get; set; }
        public decimal? ExpectedStoreRetailPrice { get; set; }
        public decimal? ExpectedDiscountRate { get; set; }
    }

    public class SyncStoreProductWarehousePriceResultDto
    {
        public string Status { get; set; } = string.Empty;
        public bool PurchaseUpdated { get; set; }
        public bool RetailUpdated { get; set; }
        public bool RetailConfirmationRequired { get; set; }
        public StoreProductStorePriceDto? StorePrice { get; set; }
        public decimal? WarehousePurchasePrice { get; set; }
        public decimal? WarehouseRetailPrice { get; set; }
        public decimal? PreviousStorePurchasePrice { get; set; }
        public decimal? PreviousStoreRetailPrice { get; set; }
        public decimal? DiscountRate { get; set; }
        public decimal? PreviousDiscountedRetailPrice { get; set; }
        public decimal? NewDiscountedRetailPrice { get; set; }
        public ProductHqSyncOperationStatusDto? HqSync { get; set; }
    }

    public class EvaluateStoreProductAutoPricingDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public string? StoreCode { get; set; }
        public bool ForceAutoPricing { get; set; }
    }

    public class EvaluateStoreProductAutoPricingResultDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public string? StoreCode { get; set; }
        public string? StorePriceUuid { get; set; }
        public decimal? CurrentRetailPrice { get; set; }
        public decimal? RecalculatedRetailPrice { get; set; }
        public string CurrentRetailPriceFormatted { get; set; } = string.Empty;
        public string RecalculatedRetailPriceFormatted { get; set; } = string.Empty;
        public decimal? DiscountRate { get; set; }
        public bool IsAutoPricing { get; set; }
        public bool HasValidPurchasePrice { get; set; }
        public bool ShouldUpdate { get; set; }
    }

    public class UpdateStoreProductTypeDto
    {
        public int ProductType { get; set; }
        public string? StoreCode { get; set; }
    }

    public class StoreProductTypeUpdateResultDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public int ProductType { get; set; }
        public string? ProductTypeLabel { get; set; }
        public ProductHqSyncOperationStatusDto? HqSync { get; set; }
    }

    /// <summary>修改商品主档中文名称（Product.ProductName，即各端显示的名称）。</summary>
    public class UpdateStoreProductNameDto
    {
        /// <summary>新的中文名称；服务端会去掉首尾空白，不能为空，最长 200 个字符。</summary>
        public string ProductName { get; set; } = string.Empty;

        /// <summary>当前操作所在分店，用于校验账号对该商品的访问范围。</summary>
        public string? StoreCode { get; set; }
    }

    public class StoreProductNameUpdateResultDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        /// <summary>名称与原来相同、没有写库也没有触发 HQ 同步时为 false。</summary>
        public bool Changed { get; set; }
        public ProductHqSyncOperationStatusDto? HqSync { get; set; }
    }

    /// <summary>商品名称的一条修改记录：谁在什么时候把名称从什么改成什么。</summary>
    public class StoreProductNameHistoryItemDto
    {
        public DateTime OccurredAtUtc { get; set; }
        public string ActorName { get; set; } = string.Empty;
        public string? BeforeName { get; set; }
        public string? AfterName { get; set; }
        /// <summary>修改来源，例如 StoreProductMaintenance（移动端商品查询）。</summary>
        public string Source { get; set; } = string.Empty;
    }

    public class StoreProductNameHistoryDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public List<StoreProductNameHistoryItemDto> Items { get; set; } = new();
    }

    public class UpdateStoreProductMultiCodeDto
    {
        public string? Barcode { get; set; }
        public decimal? PurchasePrice { get; set; }
        public decimal? RetailPrice { get; set; }
        public bool? IsAutoPricing { get; set; }
        public bool? IsSpecialProduct { get; set; }
        public bool? IsActive { get; set; }
    }

    public class CreateStoreProductSetCodeDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
        public int ProductType { get; set; }
        public string Barcode { get; set; } = string.Empty;
        public decimal? RetailPrice { get; set; }
        public bool? IsActive { get; set; }
    }

    public class UpdateStoreProductSetCodeDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string Barcode { get; set; } = string.Empty;
        public decimal? RetailPrice { get; set; }
        public bool? IsActive { get; set; }
    }

    public class SaveStoreProductSetCodeSnapshotItemDto
    {
        public string? SetCodeId { get; set; }
        public string Barcode { get; set; } = string.Empty;
        public decimal? RetailPrice { get; set; }
        public int SetType { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public class SaveStoreProductSetCodeSnapshotDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
        public int? ExpectedProductType { get; set; }
        public int ProductType { get; set; }
        public List<SaveStoreProductSetCodeSnapshotItemDto> ExpectedItems { get; set; } = new();
        public List<SaveStoreProductSetCodeSnapshotItemDto> Items { get; set; } = new();
    }

    public class SaveStoreProductSetCodeSnapshotResultDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
        public int ProductType { get; set; }
        public List<StoreProductSetCodeDto> Items { get; set; } = new();
        public ProductHqSyncOperationStatusDto? HqSync { get; set; }
    }

    public class DeleteStoreProductSetCodeResultDto
    {
        public bool Deleted { get; set; }
        public ProductHqSyncOperationStatusDto? HqSync { get; set; }
    }

    public class UpsertStoreProductClearancePriceDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public decimal? ClearancePrice { get; set; }
    }
}
