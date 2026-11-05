using System.Text.Json.Serialization;
using BlazorApp.Shared.Helper;
using System.ComponentModel.DataAnnotations;

namespace BlazorApp.Shared.DTOs
{
    public class ProductGradeDto
    {
        public string Id { get; set; } = string.Empty;
        public string ProductCode { get; set; } = string.Empty;
        public string Grade { get; set; } = string.Empty;
        public string? SupplierCode { get; set; }
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? SupplierName { get; set; }
        public string? HbProductNo { get; set; }
        public string? ProductName { get; set; }
        public string? ProductImage { get; set; }
        public decimal? DomesticPrice { get; set; }
        public decimal? ImportPrice { get; set; }
        public decimal? OemPrice { get; set; }
        public decimal? RetailPrice { get; set; }
        public bool? WarehouseIsActive { get; set; }
        public int? MinOrderQuantity { get; set; }
        public string? CategoryGuid { get; set; }
        public string? CategoryName { get; set; }
        public string? CategoryChineseName { get; set; }
        public string? Barcode { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public string? UpdatedBy { get; set; }
    }

    public class ProductGradeListQueryDto : PagedQuery
    {
        public string? Search { get; set; }
        public string? Grade { get; set; }
        public string? SupplierCode { get; set; }
        public string? HbProductNo { get; set; }
        public decimal? DomesticPriceMin { get; set; }
        public decimal? DomesticPriceMax { get; set; }
        public decimal? ImportPriceMin { get; set; }
        public decimal? ImportPriceMax { get; set; }
        public decimal? OemPriceMin { get; set; }
        public decimal? OemPriceMax { get; set; }
        public bool? WarehouseIsActive { get; set; }
        public string? CategoryGuid { get; set; }
        public bool? UncategorizedOnly { get; set; }
        public string? SortField { get; set; }
        public string? SortDirection { get; set; }
    }

    public class CreateProductGradeDto
    {
        [Required(ErrorMessage = "商品编码不能为空")]
        public string ProductCode { get; set; } = string.Empty;

        [Required(ErrorMessage = "等级不能为空")]
        [RegularExpression(@"^[A-D]$", ErrorMessage = "等级必须为A/B/C/D")]
        public string Grade { get; set; } = string.Empty;
    }

    public class BatchUpdateGradeDto
    {
        [Required(ErrorMessage = "商品列表不能为空")]
        [MinLength(1, ErrorMessage = "至少选择一个商品")]
        public List<GradeUpdateItem> Items { get; set; } = new();
    }

    public class GradeUpdateItem
    {
        [Required(ErrorMessage = "商品编码不能为空")]
        public string ProductCode { get; set; } = string.Empty;

        [Required(ErrorMessage = "等级不能为空")]
        [RegularExpression(@"^[A-D]$", ErrorMessage = "等级必须为A/B/C/D")]
        public string Grade { get; set; } = string.Empty;
    }

    public class PasteImportGradeDto
    {
        [Required(ErrorMessage = "供应商编码不能为空")]
        public string SupplierCode { get; set; } = string.Empty;

        [Required(ErrorMessage = "货号列表不能为空")]
        public string ProductNumbers { get; set; } = string.Empty;

        [Required(ErrorMessage = "等级不能为空")]
        [RegularExpression(@"^[A-D]$", ErrorMessage = "等级必须为A/B/C/D")]
        public string Grade { get; set; } = string.Empty;
    }

    public class PasteImportPreviewItem
    {
        public string ProductNumber { get; set; } = string.Empty;
        public bool Matched { get; set; }
        public string? ProductCode { get; set; }
        public string? ProductName { get; set; }
        public string? ProductImage { get; set; }
        public string? ExistingGrade { get; set; }
    }

    public class ProductGradeBrief
    {
        public string ProductCode { get; set; } = string.Empty;
        public string Grade { get; set; } = string.Empty;
    }

    public class BatchUpdateGradePriceDto
    {
        [Required(ErrorMessage = "商品列表不能为空")]
        [MinLength(1, ErrorMessage = "至少选择一个商品")]
        public List<string> ProductCodes { get; set; } = new();

        [Required(ErrorMessage = "目标数据库不能为空")]
        public string TargetDatabase { get; set; } = "HBweb";

        public decimal? ImportPrice { get; set; }
        public decimal? OEMPrice { get; set; }
    }

    public class BatchUpdateGradePriceResult
    {
        public int AffectedCount { get; set; }
        public bool Success { get; set; }
        public string? Message { get; set; }
    }
}
