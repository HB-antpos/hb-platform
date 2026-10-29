using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace BlazorApp.Shared.DTOs
{
    public class BatchUpdateSpecialRequestDto
    {
        public List<string> ProductCodes { get; set; } = new List<string>();
        public bool IsSpecial { get; set; }
    }
    public class StoreRetailPriceListDto
    {
        public string UUID { get; set; } = string.Empty;
        public string? StoreCode { get; set; }
        public string? StoreName { get; set; }
        public string? SupplierCode { get; set; }
        public string? SupplierName { get; set; }
        public string? ProductCode { get; set; }
        public string? ProductName { get; set; }
        public string? ProductImage { get; set; }
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
        public decimal? PurchasePrice { get; set; }
        public decimal? StoreRetailPriceValue { get; set; }
        public decimal? DiscountRate { get; set; }
        public bool IsActive { get; set; }
        public bool IsAutoPricing { get; set; }
        public bool? IsSpecialProduct { get; set; }
        public string? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    public class StoreRetailPriceDetailDto
    {
        public string UUID { get; set; } = string.Empty;
        public string? StoreCode { get; set; }
        public string? StoreName { get; set; }
        public string? SupplierCode { get; set; }
        public string? SupplierName { get; set; }
        public string? ProductCode { get; set; }
        public string? ProductName { get; set; }
        public decimal? PurchasePrice { get; set; }
        public decimal? StoreRetailPriceValue { get; set; }
        public decimal? DiscountRate { get; set; }
        public bool IsActive { get; set; }
        public bool IsAutoPricing { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public string? UpdatedBy { get; set; }
    }

    public class CreateStoreRetailPriceDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string ProductCode { get; set; } = string.Empty;
        public string SupplierCode { get; set; } = string.Empty;
        public decimal? PurchasePrice { get; set; }
        public decimal? StoreRetailPriceValue { get; set; }
        public decimal? DiscountRate { get; set; }
        public bool? IsActive { get; set; }
        public bool? IsAutoPricing { get; set; }
    }

    public class UpdateStoreRetailPriceDto
    {
        public decimal? PurchasePrice { get; set; }
        public decimal? StoreRetailPriceValue { get; set; }
        public decimal? DiscountRate { get; set; }
        public bool? IsActive { get; set; }
        public bool? IsAutoPricing { get; set; }
    }

    public class StoreRetailPriceUpsertItemDto
    {
        public string? UUID { get; set; }
        [Required(ErrorMessage = "StoreCode 不能为空")]
        public string StoreCode { get; set; } = string.Empty;
        [Required(ErrorMessage = "ProductCode 不能为空")]
        public string ProductCode { get; set; } = string.Empty;
        [Required(ErrorMessage = "SupplierCode 不能为空")]
        public string SupplierCode { get; set; } = string.Empty;
        [Range(0, double.MaxValue, ErrorMessage = "PurchasePrice 必须大于或等于 0")]
        public decimal? PurchasePrice { get; set; }
        [Range(0, double.MaxValue, ErrorMessage = "StoreRetailPriceValue 必须大于或等于 0")]
        public decimal? StoreRetailPriceValue { get; set; }
        [Range(0, 0.9999, ErrorMessage = "DiscountRate 必须在 0 到 0.9999 之间（0.2 表示减 20%，1 即全免不允许）")]
        public decimal? DiscountRate { get; set; }
        public bool? IsActive { get; set; }
        public bool? IsAutoPricing { get; set; }
    }

    public class BatchResultDto
    {
        public int Inserted { get; set; }
        public int Updated { get; set; }
        public int Failed { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    public class StoreRetailPriceUpsertForActiveStoresItemDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public decimal? PurchasePrice { get; set; }
        public decimal? StoreRetailPriceValue { get; set; }
        public decimal? DiscountRate { get; set; }
        public bool? IsActive { get; set; }
        public bool? IsAutoPricing { get; set; }
    }

    public class BatchDeleteByProductCodesDto
    {
        public List<string> ProductCodes { get; set; } = new();
        public List<string> StoreCodes { get; set; } = new();
    }

    public class BatchUpdateStoreRetailPriceDto
    {
        /// <summary>
        /// 分店编码
        /// </summary>
        public string StoreCode { get; set; } = string.Empty;
        public List<string> ProductCodes { get; set; } = new List<string>();
        public decimal? PurchasePrice { get; set; }
        public decimal? StoreRetailPriceValue { get; set; }
        public bool? IsAutoPricing { get; set; }
        public bool? IsSpecialProduct { get; set; }
        public decimal? DiscountRate { get; set; }
    }

    public class SyncModeConstants
    {
        public const string Overwrite = "Overwrite";
        public const string OnlyUpdateNull = "OnlyUpdateNull";
    }

    public class SyncToOtherStoresDto
    {
        public List<string> ProductCodes { get; set; } = new List<string>();
        public string SourceStoreCode { get; set; } = string.Empty;
        public List<string> TargetStoreCodes { get; set; } = new List<string>();
        public bool SyncPurchasePrice { get; set; }
        public bool SyncRetailPrice { get; set; }
        public bool SyncIsAutoPricing { get; set; }
        public bool SyncIsSpecialProduct { get; set; }
        public bool SyncDiscountRate { get; set; }
        public string Mode { get; set; } = SyncModeConstants.Overwrite;
    }

    public class SyncRetailPriceFromHqRequest
    {
        public List<string>? SelectedStoreCodes { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
    }

    public class SyncRetailPriceFromHqResult
    {
        public string RequestId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public int AddedCount { get; set; }
        public int UpdatedCount { get; set; }
        public int TotalProcessed { get; set; }
        public long DurationMs { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }
}
