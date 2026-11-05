using System.Text.Json.Serialization;
using BlazorApp.Shared.Helper;
using System.ComponentModel.DataAnnotations;

namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 商品前缀DTO
    /// </summary>
    public class ProductPrefixCodeDto
    {
        /// <summary>
        /// 前缀编码
        /// </summary>
        public string PrefixCode { get; set; } = string.Empty;

        /// <summary>
        /// 供应商编码
        /// </summary>
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>
        /// 供应商名称
        /// </summary>
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? SupplierName { get; set; }

        /// <summary>
        /// 前缀代码
        /// </summary>
        public string PrefixName { get; set; } = string.Empty;

        /// <summary>
        /// 前缀说明
        /// </summary>
        public string? PrefixDescription { get; set; }

        /// <summary>
        /// 是否启用
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// 状态名称
        /// </summary>
        public string StatusName => IsActive ? "启用" : "禁用";

        /// <summary>
        /// 排序顺序
        /// </summary>
        public int? SortOrder { get; set; }

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// 更新时间
        /// </summary>
        public DateTime UpdatedAt { get; set; }

        /// <summary>
        /// 创建人
        /// </summary>
        public string? CreatedBy { get; set; }

        /// <summary>
        /// 更新人
        /// </summary>
        public string? UpdatedBy { get; set; }
    }

    /// <summary>
    /// 创建商品前缀DTO
    /// </summary>
    public class CreateProductPrefixCodeDto
    {
        /// <summary>
        /// 供应商编码
        /// </summary>
        [Required(ErrorMessage = "供应商编码不能为空")]
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>
        /// 前缀代码
        /// </summary>
        [Required(ErrorMessage = "前缀代码不能为空")]
        [StringLength(10, ErrorMessage = "前缀代码长度不能超过10个字符")]
        [RegularExpression(@"^[A-Za-z0-9]+$", ErrorMessage = "前缀代码只能包含字母和数字")]
        public string PrefixName { get; set; } = string.Empty;

        /// <summary>
        /// 前缀说明
        /// </summary>
        [StringLength(200, ErrorMessage = "前缀说明长度不能超过200个字符")]
        public string? PrefixDescription { get; set; }

        /// <summary>
        /// 是否启用
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// 排序顺序
        /// </summary>
        [Range(0, int.MaxValue, ErrorMessage = "排序顺序不能为负数")]
        public int? SortOrder { get; set; }
    }

    /// <summary>
    /// 更新商品前缀DTO
    /// </summary>
    public class UpdateProductPrefixCodeDto
    {
        /// <summary>
        /// 前缀代码
        /// </summary>
        [Required(ErrorMessage = "前缀代码不能为空")]
        [StringLength(10, ErrorMessage = "前缀代码长度不能超过10个字符")]
        [RegularExpression(@"^[A-Za-z0-9]+$", ErrorMessage = "前缀代码只能包含字母和数字")]
        public string PrefixName { get; set; } = string.Empty;

        /// <summary>
        /// 前缀说明
        /// </summary>
        [StringLength(200, ErrorMessage = "前缀说明长度不能超过200个字符")]
        public string? PrefixDescription { get; set; }

        /// <summary>
        /// 是否启用
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// 排序顺序
        /// </summary>
        [Range(0, int.MaxValue, ErrorMessage = "排序顺序不能为负数")]
        public int? SortOrder { get; set; }
    }

    /// <summary>
    /// 商品前缀查询DTO
    /// </summary>
    public class ProductPrefixCodeQueryDto : PagedQuery
    {
        /// <summary>
        /// 搜索关键词（前缀代码、前缀说明）
        /// </summary>
        public string? Search { get; set; }

        /// <summary>
        /// 供应商编码
        /// </summary>
        public string? SupplierCode { get; set; }

        /// <summary>
        /// 是否启用
        /// </summary>
        public bool? IsActive { get; set; }

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
    /// 商品前缀详情DTO
    /// </summary>
    public class ProductPrefixCodeDetailDto : ProductPrefixCodeDto
    {
        /// <summary>
        /// 供应商信息
        /// </summary>
        public ChinaSupplierDto? Supplier { get; set; }

        /// <summary>
        /// 使用该前缀的商品数量
        /// </summary>
        public int ProductCount { get; set; }
    }

    /// <summary>
    /// 简单商品前缀DTO（用于下拉选择）
    /// </summary>
    public class SimpleProductPrefixCodeDto
    {
        /// <summary>
        /// 前缀编码
        /// </summary>
        public string PrefixCode { get; set; } = string.Empty;

        /// <summary>
        /// 前缀代码
        /// </summary>
        public string PrefixName { get; set; } = string.Empty;

        /// <summary>
        /// 前缀说明
        /// </summary>
        public string? PrefixDescription { get; set; }

        /// <summary>
        /// 显示名称
        /// </summary>
        public string DisplayName => string.IsNullOrWhiteSpace(PrefixDescription) 
            ? PrefixName 
            : $"{PrefixName} - {PrefixDescription}";
    }

    /// <summary>
    /// 批量创建商品前缀DTO
    /// </summary>
    public class BatchCreateProductPrefixCodeDto
    {
        /// <summary>
        /// 供应商编码
        /// </summary>
        [Required(ErrorMessage = "供应商编码不能为空")]
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>
        /// 前缀列表
        /// </summary>
        [Required(ErrorMessage = "前缀列表不能为空")]
        [MinLength(1, ErrorMessage = "至少需要一个前缀")]
        public List<BatchPrefixItem> Prefixes { get; set; } = new();
    }

    /// <summary>
    /// 批量前缀项
    /// </summary>
    public class BatchPrefixItem
    {
        /// <summary>
        /// 前缀代码
        /// </summary>
        [Required(ErrorMessage = "前缀代码不能为空")]
        [RegularExpression(@"^[A-Z]+$", ErrorMessage = "前缀代码只能包含大写字母")]
        public string PrefixName { get; set; } = string.Empty;

        /// <summary>
        /// 前缀说明
        /// </summary>
        public string? PrefixDescription { get; set; }

        /// <summary>
        /// 排序顺序
        /// </summary>
        public int? SortOrder { get; set; }
    }
}
