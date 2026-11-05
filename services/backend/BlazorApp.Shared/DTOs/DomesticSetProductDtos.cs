using System.Text.Json.Serialization;
using BlazorApp.Shared.Helper;
using System.ComponentModel.DataAnnotations;

namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 套装商品DTO
    /// </summary>
    public class DomesticSetProductDto
    {
        /// <summary>
        /// 套装商品编码
        /// </summary>
        public string SetProductCode { get; set; } = string.Empty;

        /// <summary>
        /// 商品编码
        /// </summary>
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>
        /// 商品货号
        /// </summary>
        public string? ProductNo { get; set; }

        /// <summary>
        /// 套装货号
        /// </summary>
        public string SetProductNo { get; set; } = string.Empty;

        /// <summary>
        /// 套装条码
        /// </summary>
        public string? SetBarcode { get; set; }

        /// <summary>
        /// 国内价格
        /// </summary>
        public decimal? DomesticPrice { get; set; }

        /// <summary>
        /// 进口价格
        /// </summary>
        public decimal? ImportPrice { get; set; }

        /// <summary>
        /// 零售价
        /// </summary>
        public decimal? OEMPrice { get; set; }

        /// <summary>
        /// 备注信息
        /// </summary>
        public string? Remarks { get; set; }

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// 更新时间
        /// </summary>
        public DateTime? UpdatedAt { get; set; }

        /// <summary>
        /// 创建人
        /// </summary>
        public string? CreatedBy { get; set; }

        /// <summary>
        /// 更新人
        /// </summary>
        public string? UpdatedBy { get; set; }

        /// <summary>
        /// 是否已删除（软删除标记）
        /// </summary>
        public bool IsDeleted { get; set; }

        /// <summary>
        /// 商品名称（来自关联商品）
        /// </summary>
        public string? ProductName { get; set; }

        /// <summary>
        /// 供应商编码（来自关联商品）
        /// </summary>
        public string? SupplierCode { get; set; }

        /// <summary>
        /// 供应商名称（来自关联商品）
        /// </summary>
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? SupplierName { get; set; }

        /// <summary>
        /// 关联的国内商品信息
        /// </summary>
        public DomesticProductDto? DomesticProduct { get; set; }
    }

    /// <summary>
    /// 创建套装商品DTO
    /// </summary>
    public class CreateDomesticSetProductDto
    {
        /// <summary>
        /// 商品编码
        /// </summary>
        [Required(ErrorMessage = "商品编码不能为空")]
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>
        /// 商品货号（可选，如果不提供则使用商品的HB货号）
        /// </summary>
        [StringLength(50, ErrorMessage = "商品货号长度不能超过50个字符")]
        public string? ProductNo { get; set; }

        /// <summary>
        /// 套装货号（可选，如果不提供则自动生成）
        /// </summary>
        [StringLength(50, ErrorMessage = "套装货号长度不能超过50个字符")]
        public string? SetProductNo { get; set; }

        /// <summary>
        /// 套装条码（可选，如果不提供则自动生成）
        /// </summary>
        [StringLength(50, ErrorMessage = "套装条码长度不能超过50个字符")]
        public string? SetBarcode { get; set; }

        /// <summary>
        /// 国内价格
        /// </summary>
        [Range(0, double.MaxValue, ErrorMessage = "国内价格不能为负数")]
        public decimal? DomesticPrice { get; set; }

        /// <summary>
        /// 进口价格
        /// </summary>
        [Range(0, double.MaxValue, ErrorMessage = "进口价格不能为负数")]
        public decimal? ImportPrice { get; set; }

        /// <summary>
        /// 零售价
        /// </summary>
        [Range(0, double.MaxValue, ErrorMessage = "零售价不能为负数")]
        public decimal? OEMPrice { get; set; }

        /// <summary>
        /// 备注信息
        /// </summary>
        [StringLength(1000, ErrorMessage = "备注信息长度不能超过1000个字符")]
        public string? Remarks { get; set; }
    }

    /// <summary>
    /// 更新套装商品DTO
    /// </summary>
    public class UpdateDomesticSetProductDto
    {
        /// <summary>
        /// 国内价格
        /// </summary>
        [Range(0, double.MaxValue, ErrorMessage = "国内价格不能为负数")]
        public decimal? DomesticPrice { get; set; }

        /// <summary>
        /// 进口价格
        /// </summary>
        [Range(0, double.MaxValue, ErrorMessage = "进口价格不能为负数")]
        public decimal? ImportPrice { get; set; }

        /// <summary>
        /// 零售价
        /// </summary>
        [Range(0, double.MaxValue, ErrorMessage = "零售价不能为负数")]
        public decimal? OEMPrice { get; set; }

        /// <summary>
        /// 备注信息
        /// </summary>
        [StringLength(1000, ErrorMessage = "备注信息长度不能超过1000个字符")]
        public string? Remarks { get; set; }
    }

    /// <summary>
    /// 套装商品查询DTO
    /// </summary>
    public class DomesticSetProductQueryDto : PagedQuery
    {
        /// <summary>
        /// 搜索关键词（套装货号、套装条码）
        /// </summary>
        public string? Search { get; set; }

        /// <summary>
        /// 商品编码
        /// </summary>
        public string? ProductCode { get; set; }

        /// <summary>
        /// 供应商编码
        /// </summary>
        public string? SupplierCode { get; set; }

        /// <summary>
        /// 价格范围 - 最小值
        /// </summary>
        public decimal? MinPrice { get; set; }

        /// <summary>
        /// 价格范围 - 最大值
        /// </summary>
        public decimal? MaxPrice { get; set; }

        /// <summary>
        /// 排序字段
        /// </summary>
        public string? SortField { get; set; }

        /// <summary>
        /// 排序方向 (asc/desc)
        /// </summary>
        public string? SortDirection { get; set; }
    }

    /// <summary>
    /// 套装商品详情DTO
    /// </summary>
    public class DomesticSetProductDetailDto : DomesticSetProductDto
    {
        /// <summary>
        /// 关联的商品信息
        /// </summary>
        public DomesticProductDto? Product { get; set; }

        /// <summary>
        /// 关联的国内商品详细信息
        /// </summary>
        public DomesticProductDetailDto? DomesticProductDetail { get; set; }
    }

    /// <summary>
    /// 批量创建套装商品DTO
    /// </summary>
    public class BatchCreateDomesticSetProductDto
    {
        /// <summary>
        /// 商品编码
        /// </summary>
        [Required(ErrorMessage = "商品编码不能为空")]
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>
        /// 套装商品数量
        /// </summary>
        [Range(1, 100, ErrorMessage = "套装商品数量必须在1-100之间")]
        public int Count { get; set; } = 1;

        /// <summary>
        /// 套装商品列表
        /// </summary>
        [Required(ErrorMessage = "套装商品列表不能为空")]
        [MinLength(1, ErrorMessage = "至少需要一个套装商品")]
        public List<BatchSetProductItem> SetProducts { get; set; } = new();
    }

    /// <summary>
    /// 批量套装商品项
    /// </summary>
    public class BatchSetProductItem
    {
        /// <summary>
        /// 国内价格
        /// </summary>
        public decimal? DomesticPrice { get; set; }

        /// <summary>
        /// 进口价格
        /// </summary>
        public decimal? ImportPrice { get; set; }

        /// <summary>
        /// 零售价
        /// </summary>
        public decimal? OEMPrice { get; set; }

        /// <summary>
        /// 备注信息
        /// </summary>
        public string? Remarks { get; set; }
    }
}
