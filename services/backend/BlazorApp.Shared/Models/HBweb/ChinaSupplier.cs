using System.Text.Json.Serialization;
using BlazorApp.Shared.Helper;
using SqlSugar;
using System;

namespace BlazorApp.Shared.Models
{
    /// <summary>
    /// 国内供应商信息表，存储国内供应商的基本信息
    /// </summary>
    [SugarTable("ChinaSupplier")]
    public class ChinaSupplier : BaseEntity
    {
        /// <summary>
        /// 全局唯一标识符
        /// </summary>
        [SugarColumn(IsPrimaryKey = true)]
        public string? Guid { get; set; }

        /// <summary>
        /// 供应商代码
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public string? SupplierCode { get; set; }

        /// <summary>
        /// 供应商名称
        /// </summary>
        [SugarColumn(IsNullable = true)]
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? SupplierName { get; set; }

        /// <summary>
        /// 店铺/商店编号
        /// </summary>
        [SugarColumn(IsNullable = true)]
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? ShopNumber { get; set; }

        /// <summary>
        /// 联系人姓名
        /// </summary>
        [SugarColumn(IsNullable = true)]
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? ContactPerson { get; set; }

        /// <summary>
        /// 电话号码
        /// </summary>
        [SugarColumn(IsNullable = true)]
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? Phone { get; set; }

        /// <summary>
        /// 邮箱地址
        /// </summary>
        [SugarColumn(IsNullable = true)]
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? Email { get; set; }

        /// <summary>
        /// 门店照片URL/路径
        /// </summary>
        [SugarColumn(IsNullable = true)]
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? StorefrontPhoto { get; set; }

        /// <summary>
        /// 附加备注或说明
        /// </summary>
        [SugarColumn(IsNullable = true)]
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? Remarks { get; set; }

        /// <summary>
        /// 状态标识（0: 禁用, 1: 启用）
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public int? Status { get; set; }

        /// <summary>
        /// 记录创建人
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public string? FGC_Creator { get; set; }

        /// <summary>
        /// 创建日期
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public string? FGC_CreateDate { get; set; }

        /// <summary>
        /// 最后修改人
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public string? FGC_LastModifier { get; set; }

        /// <summary>
        /// 最后修改日期
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public string? FGC_LastModifyDate { get; set; }

        /// <summary>
        /// 行版本，用于并发控制
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public string? FGC_Rowversion { get; set; }

        /// <summary>
        /// 更新帮助字段
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public string? FGC_UpdateHelp { get; set; }

        //导航属性
        [Navigate(NavigateType.OneToMany, nameof(ProductPrefixCode.SupplierCode), nameof(ProductPrefixCode.SupplierCode))]
        public List<ProductPrefixCode>? ProductPrefixCodes { get; set; }

        //商品信息
        [Navigate(NavigateType.OneToMany, nameof(DomesticProduct.SupplierCode), nameof(DomesticProduct.SupplierCode))]
        public List<DomesticProduct>? DomesticProducts { get; set; }

    }
}