using BlazorApp.Shared.Helper;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// Request: batch creation
    /// </summary>
    public class CreateDomesticProductBatchRequest
    {
        [Required]
        [JsonPropertyName("supplierCode")]
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>
        /// Optional prefix code, e.g. "001"
        /// </summary>
        [JsonPropertyName("prefixCode")]
        public string? PrefixCode { get; set; }

        [JsonPropertyName("prefixName")]
        public string? PrefixName { get; set; }

        /// <summary>
        /// EAN-13 barcode prefix
        /// </summary>
        [JsonPropertyName("barcodePrefix")]
        public string BarcodePrefix { get; set; } = "9527";

        [JsonPropertyName("items")]
        public List<CreateBatchItemDto> Items { get; set; } = new();
    }

    /// <summary>
    /// Batch item for creation
    /// </summary>
    public class CreateBatchItemDto
    {
        [JsonPropertyName("productName")]
        public string? ProductName { get; set; }

        /// <summary>
        /// 0=Normal, 1=Set
        /// </summary>
        [JsonPropertyName("productType")]
        public int ProductType { get; set; }

        /// <summary>
        /// For Set products
        /// </summary>
        [JsonPropertyName("setQuantity")]
        public int? SetQuantity { get; set; }

        /// <summary>
        /// For Set products (total price)
        /// </summary>
        [JsonPropertyName("setPrice")]
        public decimal? SetPrice { get; set; }

        /// <summary>
        /// OEMPrice for this item
        /// </summary>
        [JsonPropertyName("privateLabelPrice")]
        public decimal? PrivateLabelPrice { get; set; }

        /// <summary>
        /// For Set sub-items (ProductType=2 means sub-item): Parent item's item number
        /// </summary>
        [JsonPropertyName("parentItemNumber")]
        public string? ParentItemNumber { get; set; }

        /// <summary>
        /// For Set sub-items: Sub-item product name
        /// </summary>
        [JsonPropertyName("subItemProductName")]
        public string? SubItemProductName { get; set; }

        /// <summary>
        /// 套装模板创建套数，仅套装商品有效，默认创建 1 套
        /// </summary>
        [JsonPropertyName("createCount")]
        public int? CreateCount { get; set; }

        /// <summary>
        /// 指定套装模板下的子项列表
        /// </summary>
        [JsonPropertyName("subItems")]
        public List<CreateBatchItemDto> SubItems { get; set; } = new();
    }

    /// <summary>
    /// Request: update RRP (batch update)
    /// </summary>
    public class UpdatePrivateLabelPriceRequest
    {
        public List<UpdatePriceItemDto> Items { get; set; } = new();
    }

    /// <summary>
    /// Update price item
    /// </summary>
    public class UpdatePriceItemDto
    {
        [Required]
        public string ProductCode { get; set; } = string.Empty;

        public decimal? PrivateLabelPrice { get; set; }
    }

    /// <summary>
    /// Request: update batch detail item fields
    /// </summary>
    public class UpdateBatchItemsRequest
    {
        public List<UpdateBatchItemDto> Items { get; set; } = new();
    }

    /// <summary>
    /// Batch item field update
    /// </summary>
    public class UpdateBatchItemDto
    {
        [Required]
        public string ProductCode { get; set; } = string.Empty;

        public string? ProductName { get; set; }

        [Range(0, double.MaxValue, ErrorMessage = "零售价不能为负数")]
        public decimal? PrivateLabelPrice { get; set; }
    }

    /// <summary>
    /// Response: batch creation result
    /// </summary>
    public class CreateDomesticProductBatchResponse
    {
        public string BatchNumber { get; set; } = string.Empty;
        public int TotalCreated { get; set; }
        public int NormalProductCount { get; set; }
        public int SetProductCount { get; set; }
        public List<BatchCreatedItemDto> Items { get; set; } = new();
    }

    /// <summary>
    /// Batch created item
    /// </summary>
    public class BatchCreatedItemDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public string HBProductNo { get; set; } = string.Empty;
        public string? Barcode { get; set; }
        public string ProductName { get; set; } = string.Empty;
        public int ProductType { get; set; }
        public decimal? PrivateLabelPrice { get; set; }
        public int? SetQuantity { get; set; }
        public decimal? SetPrice { get; set; }
        public List<SubItemDto> SubItems { get; set; } = new();
    }

    /// <summary>
    /// Sub-item in a set product
    /// </summary>
    public class SubItemDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public string HBProductNo { get; set; } = string.Empty;
        public string? Barcode { get; set; }
        public string ProductName { get; set; } = string.Empty;
        public decimal? PrivateLabelPrice { get; set; }
    }

    /// <summary>
    /// Response: batch list item
    /// </summary>
    public class DomesticProductBatchDto
    {
        public string BatchNumber { get; set; } = string.Empty;
        public string SupplierCode { get; set; } = string.Empty;
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? SupplierName { get; set; }
        public DateTime CreatedTime { get; set; }
        public int NormalProductCount { get; set; }
        public int SetProductCount { get; set; }
        public int TotalCount { get; set; }
        public string? Remark { get; set; }
    }

    /// <summary>
    /// Response: batch detail
    /// </summary>
    public class DomesticProductBatchDetailDto
    {
        public string BatchNumber { get; set; } = string.Empty;
        public string SupplierCode { get; set; } = string.Empty;
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? SupplierName { get; set; }
        public DateTime CreatedTime { get; set; }
        public string? Remark { get; set; }
        public int NormalProductCount { get; set; }
        public int SetProductCount { get; set; }
        public List<BatchDetailItemDto> Items { get; set; } = new();
    }

    /// <summary>
    /// Response: batch export file
    /// </summary>
    public class DomesticProductBatchExportFileDto
    {
        public byte[] Content { get; set; } = Array.Empty<byte>();
        public string ContentType { get; set; } =
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        public string FileName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Batch detail item
    /// </summary>
    public class BatchDetailItemDto
    {
        public string ProductCode { get; set; } = string.Empty;
        public string HBProductNo { get; set; } = string.Empty;
        public string? Barcode { get; set; }
        public string ProductName { get; set; } = string.Empty;

        /// <summary>
        /// 0=Normal, 1=Set, 2=SetSubItem
        /// </summary>
        public int ProductType { get; set; }
        public decimal? PrivateLabelPrice { get; set; }
        public int? SetQuantity { get; set; }
        public decimal? SetPrice { get; set; }

        /// <summary>
        /// For sub-items: Parent product code
        /// </summary>
        public string? ParentProductCode { get; set; }

        /// <summary>
        /// For sub-items: Parent HB product number
        /// </summary>
        public string? ParentHBProductNo { get; set; }
    }
}
