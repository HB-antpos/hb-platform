using System.Text.Json.Serialization;
using BlazorApp.Shared.Helper;
using System;
using System.Collections.Generic;

namespace BlazorApp.Shared.DTOs
{
    public class ReactTableRequestDto
    {
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 100;
        public string? SortBy { get; set; }
        public string SortOrder { get; set; } = "descend";
        public string? GlobalSearch { get; set; }
        public Dictionary<string, string[]>? Filters { get; set; }
        public List<string>? CategoryGuids { get; set; }
        public bool IncludeSubCategories { get; set; } = true;
        public bool UncategorizedOnly { get; set; } = false;
    }

    public class ReactTableResponseDto<T>
    {
        public List<T> Items { get; set; } = new List<T>();
        public int Total { get; set; }
    }

    public class WarehouseProductReactListDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public string? ProductName { get; set; }
        public string? EnglishName { get; set; }
        public string? ItemNumber { get; set; }
        public string? Barcode { get; set; }
        public string? CategoryName { get; set; }
        public List<string> LocationCodes { get; set; } = new List<string>();
        public List<string> LocationBarcodes { get; set; } = new List<string>();
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? SupplierName { get; set; }
        public string? SupplierCode { get; set; }
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? DomesticSupplierName { get; set; }
        public string? DomesticSupplierCode { get; set; }
        public decimal? DomesticPrice { get; set; }
        public decimal? OEMPrice { get; set; }
        public decimal? ImportPrice { get; set; }
        public decimal? Volume { get; set; }
        public bool IsVolumeFallback { get; set; }
        public int? PackingQuantity { get; set; }
        public bool IsPackingQuantityFallback { get; set; }
        public int? MinOrderQuantity { get; set; }
        public bool IsActive { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }
        public string? ProductImage { get; set; }
        /// <summary>商品类型：0-普通，1-套装，2-多码</summary>
        public int ProductType { get; set; }
        public string? LocalSupplierCode { get; set; }
        public string? LocalSupplierName { get; set; }
    }
}
