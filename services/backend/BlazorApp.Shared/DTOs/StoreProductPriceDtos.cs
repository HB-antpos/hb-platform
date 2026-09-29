using System;

namespace BlazorApp.Shared.DTOs
{
    public class StoreProductPriceListDto
    {
        public string? ProductCode { get; set; }
        public string? ProductName { get; set; }
        public string? ProductImage { get; set; }
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
        public string? LocalSupplierCode { get; set; }
        public string? LocalSupplierName { get; set; }
        public int? ProductType { get; set; }
        public int? MiddlePackageQuantity { get; set; }
        public bool IsActive { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }

        public string? StoreCode { get; set; }
        public decimal? StorePurchasePrice { get; set; }
        public decimal? StoreRetailPrice { get; set; }
        public bool IsStoreAutoPricing { get; set; }
        public bool IsStoreSpecialProduct { get; set; }
        public decimal? DiscountRate { get; set; }
    }

    public class StoreProductPriceQueryDto
    {
        public string? StoreCode { get; set; }

        /// <summary>
        /// 多选分店；与 StoreCode 合并去重，选了多个分店时每个商品在每个分店各占一行。
        /// </summary>
        public List<string>? StoreCodes { get; set; }
        public string? Search { get; set; }
        public string? LocalSupplierCode { get; set; }
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 50;
        public string? SortBy { get; set; }
        public string? SortOrder { get; set; }

        public string? ProductName { get; set; }
        public string? ProductCode { get; set; }
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
        public int? ProductType { get; set; }
        public bool? IsActive { get; set; }
        public bool? IsSpecialProduct { get; set; }
        public decimal? PurchasePriceGt { get; set; }
        public decimal? PurchasePriceLt { get; set; }
        public decimal? RetailPriceGt { get; set; }
        public decimal? RetailPriceLt { get; set; }
    }
}
