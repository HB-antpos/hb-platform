using System.Text.Json.Serialization;
using BlazorApp.Shared.Helper;
using System.ComponentModel.DataAnnotations;

namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 国内供应商DTO
    /// </summary>
    public class DomesticSupplierDto
    {
        /// <summary>
        /// 供应商GUID
        /// </summary>
        public string Guid { get; set; } = string.Empty;

        /// <summary>
        /// 供应商编码（HB+3位序号或手动输入）
        /// </summary>
        [Required(ErrorMessage = "供应商编码不能为空")]
        [StringLength(20, ErrorMessage = "供应商编码长度不能超过20个字符")]
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>
        /// 供应商名称
        /// </summary>
        [Required(ErrorMessage = "供应商名称不能为空")]
        [StringLength(100, ErrorMessage = "供应商名称长度不能超过100个字符")]
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string SupplierName { get; set; } = string.Empty;

        /// <summary>
        /// 商铺编号
        /// </summary>
        [StringLength(50, ErrorMessage = "商铺编号长度不能超过50个字符")]
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? ShopNumber { get; set; }

        /// <summary>
        /// 联系人
        /// </summary>
        [StringLength(50, ErrorMessage = "联系人长度不能超过50个字符")]
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? ContactPerson { get; set; }

        /// <summary>
        /// 联系电话
        /// </summary>
        [StringLength(20, ErrorMessage = "联系电话长度不能超过20个字符")]
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? Phone { get; set; }

        /// <summary>
        /// 邮箱地址
        /// </summary>
        [EmailAddress(ErrorMessage = "邮箱格式不正确")]
        [StringLength(100, ErrorMessage = "邮箱长度不能超过100个字符")]
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? Email { get; set; }

        /// <summary>
        /// 地址
        /// </summary>
        [StringLength(200, ErrorMessage = "地址长度不能超过200个字符")]
        public string? Address { get; set; }

        /// <summary>
        /// 商户门头照片
        /// </summary>
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? StorefrontPhoto { get; set; }

        /// <summary>
        /// 备注
        /// </summary>
        [StringLength(500, ErrorMessage = "备注长度不能超过500个字符")]
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? Remarks { get; set; }

        /// <summary>
        /// 状态（1=启用, 0=禁用）
        /// </summary>
        public int Status { get; set; } = 1;

        /// <summary>
        /// 是否启用
        /// </summary>
        public bool IsActive => Status == 1;

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// 更新时间
        /// </summary>
        public DateTime UpdatedAt { get; set; }

        /// <summary>
        /// 创建者
        /// </summary>
        public string? CreatedBy { get; set; }

        /// <summary>
        /// 最后修改者
        /// </summary>
        public string? UpdatedBy { get; set; }
    }

    /// <summary>
    /// 创建国内供应商请求DTO
    /// </summary>
    public class CreateDomesticSupplierDto
    {
        /// <summary>
        /// 供应商编码（HB+3位序号或手动输入）
        /// </summary>
        [Required(ErrorMessage = "供应商编码不能为空")]
        [StringLength(20, ErrorMessage = "供应商编码长度不能超过20个字符")]
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>
        /// 供应商名称
        /// </summary>
        [Required(ErrorMessage = "供应商名称不能为空")]
        [StringLength(100, ErrorMessage = "供应商名称长度不能超过100个字符")]
        public string SupplierName { get; set; } = string.Empty;

        /// <summary>
        /// 商铺编号
        /// </summary>
        [StringLength(50, ErrorMessage = "商铺编号长度不能超过50个字符")]
        public string? ShopNumber { get; set; }

        /// <summary>
        /// 联系人
        /// </summary>
        [StringLength(50, ErrorMessage = "联系人长度不能超过50个字符")]
        public string? ContactPerson { get; set; }

        /// <summary>
        /// 联系电话
        /// </summary>
        [StringLength(20, ErrorMessage = "联系电话长度不能超过20个字符")]
        public string? Phone { get; set; }

        /// <summary>
        /// 邮箱地址
        /// </summary>
        [EmailAddress(ErrorMessage = "邮箱格式不正确")]
        [StringLength(100, ErrorMessage = "邮箱长度不能超过100个字符")]
        public string? Email { get; set; }

        /// <summary>
        /// 地址
        /// </summary>
        [StringLength(200, ErrorMessage = "地址长度不能超过200个字符")]
        public string? Address { get; set; }

        /// <summary>
        /// 商户门头照片
        /// </summary>
        public string? StorefrontPhoto { get; set; }

        /// <summary>
        /// 备注
        /// </summary>
        [StringLength(500, ErrorMessage = "备注长度不能超过500个字符")]
        public string? Remarks { get; set; }

        /// <summary>
        /// 状态（1=启用, 0=禁用）
        /// </summary>
        public int Status { get; set; } = 1;
    }

    /// <summary>
    /// 更新国内供应商请求DTO
    /// </summary>
    public class UpdateDomesticSupplierDto
    {
        /// <summary>
        /// 供应商编码（HB+3位序号或手动输入）
        /// </summary>
        [Required(ErrorMessage = "供应商编码不能为空")]
        [StringLength(20, ErrorMessage = "供应商编码长度不能超过20个字符")]
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>
        /// 供应商名称
        /// </summary>
        [Required(ErrorMessage = "供应商名称不能为空")]
        [StringLength(100, ErrorMessage = "供应商名称长度不能超过100个字符")]
        public string SupplierName { get; set; } = string.Empty;

        /// <summary>
        /// 商铺编号
        /// </summary>
        [StringLength(50, ErrorMessage = "商铺编号长度不能超过50个字符")]
        public string? ShopNumber { get; set; }

        /// <summary>
        /// 联系人
        /// </summary>
        [StringLength(50, ErrorMessage = "联系人长度不能超过50个字符")]
        public string? ContactPerson { get; set; }

        /// <summary>
        /// 联系电话
        /// </summary>
        [StringLength(20, ErrorMessage = "联系电话长度不能超过20个字符")]
        public string? Phone { get; set; }

        /// <summary>
        /// 邮箱地址
        /// </summary>
        [EmailAddress(ErrorMessage = "邮箱格式不正确")]
        [StringLength(100, ErrorMessage = "邮箱长度不能超过100个字符")]
        public string? Email { get; set; }

        /// <summary>
        /// 地址
        /// </summary>
        [StringLength(200, ErrorMessage = "地址长度不能超过200个字符")]
        public string? Address { get; set; }

        /// <summary>
        /// 商户门头照片
        /// </summary>
        public string? StorefrontPhoto { get; set; }

        /// <summary>
        /// 备注
        /// </summary>
        [StringLength(500, ErrorMessage = "备注长度不能超过500个字符")]
        public string? Remarks { get; set; }

        /// <summary>
        /// 状态（1=启用, 0=禁用）
        /// </summary>
        public int Status { get; set; } = 1;
    }

    /// <summary>
    /// 国内供应商查询参数DTO
    /// </summary>
    public class DomesticSupplierQueryDto
    {
        /// <summary>
        /// 搜索关键词（供应商编码或名称）
        /// </summary>
        public string? Keyword { get; set; }

        /// <summary>
        /// 状态筛选（null=全部，1=启用，0=禁用）
        /// </summary>
        public int? Status { get; set; }

        /// <summary>
        /// 页码（从1开始）
        /// </summary>
        [Range(1, int.MaxValue, ErrorMessage = "页码必须大于0")]
        public int Page { get; set; } = 1;

        /// <summary>
        /// 每页数量
        /// </summary>
        [Range(1, 100, ErrorMessage = "每页数量必须在1-100之间")]
        public int PageSize { get; set; } = 20;

        /// <summary>
        /// 排序字段
        /// </summary>
        public string? SortField { get; set; } = "CreatedAt";

        /// <summary>
        /// 排序方向（asc, desc）
        /// </summary>
        public string? SortDirection { get; set; } = "desc";
    }
}
