using System.Text.Json.Serialization;
using BlazorApp.Shared.Helper;
using System.ComponentModel.DataAnnotations;

namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 国内供应商查询DTO
    /// </summary>
    public class ChinaSupplierQueryDto
    {
        /// <summary>
        /// 页码，从1开始
        /// </summary>
        public int Page { get; set; } = 1;
        
        /// <summary>
        /// 每页大小
        /// </summary>
        public int PageSize { get; set; } = 20;
        
        /// <summary>
        /// 搜索关键字
        /// </summary>
        public string? Search { get; set; }
        
        /// <summary>
        /// 状态过滤条件
        /// </summary>
        public int? Status { get; set; }
        
        /// <summary>
        /// 供应商代码过滤条件
        /// </summary>
        public string? SupplierCode { get; set; }
        
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
    /// 创建国内供应商DTO
    /// </summary>
    public class CreateChinaSupplierDto
    {
        /// <summary>
        /// 供应商代码
        /// </summary>
        [Required(ErrorMessage = "供应商代码不能为空")]
        [StringLength(50, ErrorMessage = "供应商代码长度不能超过50个字符")]
        public string SupplierCode { get; set; } = string.Empty;

        /// <summary>
        /// 供应商名称
        /// </summary>
        [Required(ErrorMessage = "供应商名称不能为空")]
        [StringLength(200, ErrorMessage = "供应商名称长度不能超过200个字符")]
        public string SupplierName { get; set; } = string.Empty;

        /// <summary>
        /// 店铺号
        /// </summary>
        [StringLength(50, ErrorMessage = "店铺号长度不能超过50个字符")]
        public string? ShopNumber { get; set; }

        /// <summary>
        /// 联系人
        /// </summary>
        [StringLength(100, ErrorMessage = "联系人长度不能超过100个字符")]
        public string? ContactPerson { get; set; }

        /// <summary>
        /// 电话
        /// </summary>
        [StringLength(20, ErrorMessage = "电话长度不能超过20个字符")]
        public string? Phone { get; set; }

        /// <summary>
        /// 邮箱
        /// </summary>
        [EmailAddress(ErrorMessage = "邮箱格式不正确")]
        [StringLength(100, ErrorMessage = "邮箱长度不能超过100个字符")]
        public string? Email { get; set; }

        /// <summary>
        /// 店面照片URL
        /// </summary>
        [StringLength(500, ErrorMessage = "店面照片URL长度不能超过500个字符")]
        public string? StorefrontPhoto { get; set; }

        /// <summary>
        /// 备注信息
        /// </summary>
        [StringLength(1000, ErrorMessage = "备注长度不能超过1000个字符")]
        public string? Remarks { get; set; }

        /// <summary>
        /// 状态（0=禁用，1=启用）
        /// </summary>
        [Range(0, 1, ErrorMessage = "状态必须为0（禁用）或1（启用）")]
        public int Status { get; set; } = 1;
    }

    /// <summary>
    /// 更新国内供应商DTO
    /// </summary>
    public class UpdateChinaSupplierDto : CreateChinaSupplierDto
    {
    }

    /// <summary>
    /// 国内供应商DTO
    /// </summary>
    public class ChinaSupplierDto
    {
        /// <summary>
        /// 供应商唯一标识符
        /// </summary>
        public string? Guid { get; set; }
        
        /// <summary>
        /// 供应商代码
        /// </summary>
        public string? SupplierCode { get; set; }
        
        /// <summary>
        /// 供应商名称
        /// </summary>
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? SupplierName { get; set; }
        
        /// <summary>
        /// 店铺号
        /// </summary>
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? ShopNumber { get; set; }
        
        /// <summary>
        /// 联系人
        /// </summary>
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? ContactPerson { get; set; }
        
        /// <summary>
        /// 电话
        /// </summary>
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? Phone { get; set; }
        
        /// <summary>
        /// 邮箱
        /// </summary>
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? Email { get; set; }
        
        /// <summary>
        /// 店面照片URL
        /// </summary>
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? StorefrontPhoto { get; set; }
        
        /// <summary>
        /// 备注信息
        /// </summary>
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? Remarks { get; set; }
        
        /// <summary>
        /// 状态
        /// </summary>
        public int? Status { get; set; }
        
        /// <summary>
        /// 状态文本描述
        /// </summary>
        public string StatusText => Status switch
        {
            0 => "禁用",
            1 => "启用",
            _ => "未知"
        };
        
        /// <summary>
        /// 创建人
        /// </summary>
        public string? FGC_Creator { get; set; }
        
        /// <summary>
        /// 创建日期
        /// </summary>
        public string? FGC_CreateDate { get; set; }
        
        /// <summary>
        /// 最后修改人
        /// </summary>
        public string? FGC_LastModifier { get; set; }
        
        /// <summary>
        /// 最后修改日期
        /// </summary>
        public string? FGC_LastModifyDate { get; set; }
    }

    /// <summary>
    /// 国内供应商详情DTO
    /// </summary>
    public class ChinaSupplierDetailDto : ChinaSupplierDto
    {
        /// <summary>
        /// 订单数量
        /// </summary>
        public int OrderCount { get; set; }
        
        /// <summary>
        /// 订单总金额
        /// </summary>
        public decimal TotalOrderAmount { get; set; }
    }

    /// <summary>
    /// 同步供应商到HBSales数据库结果DTO
    /// </summary>
    public class SyncToHbSalesResultDto
    {
        public int TotalProcessed { get; set; }
        public int SuccessCount { get; set; }
        public int FailCount { get; set; }
        public int InsertedCount { get; set; }
        public int UpdatedCount { get; set; }
        public List<string> Errors { get; set; } = new();
    }
}