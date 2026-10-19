using System.Text.Json.Serialization;
using BlazorApp.Shared.Helper;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 义乌货柜主表DTO - 基于新的Container模型
    /// </summary>
    public class YiwuContainerDto
    {
        /// <summary>
        /// 货柜编码（主键）
        /// </summary>
        public string ContainerCode { get; set; } = string.Empty;

        /// <summary>
        /// 货柜编号
        /// </summary>
        [Display(Name = "货柜编号")]
        [JsonConverter(typeof(ContainerNumberJsonConverter))]
        public string? ContainerNumber { get; set; }

        /// <summary>
        /// 装柜日期
        /// </summary>
        [Display(Name = "装柜日期")]
        public DateTime? LoadingDate { get; set; }

        /// <summary>
        /// 预计到岸日期
        /// </summary>
        [Display(Name = "预计到岸日期")]
        public DateTime? EstimatedArrivalDate { get; set; }

        /// <summary>
        /// 实际到货日期
        /// </summary>
        [Display(Name = "实际到货日期")]
        public DateTime? ActualArrivalDate { get; set; }

        /// <summary>
        /// 合计件数
        /// </summary>
        [Display(Name = "合计件数")]
        public decimal? TotalPieces { get; set; }

        /// <summary>
        /// 合计数量
        /// </summary>
        [Display(Name = "合计数量")]
        public decimal? TotalQuantity { get; set; }

        /// <summary>
        /// 合计金额
        /// </summary>
        [Display(Name = "合计金额")]
        public decimal? TotalAmount { get; set; }

        /// <summary>
        /// 总体积
        /// </summary>
        [Display(Name = "总体积")]
        public decimal? TotalVolume { get; set; }

        /// <summary>
        /// 成本浮率
        /// </summary>
        [Display(Name = "成本浮率")]
        public decimal? CostFloatRate { get; set; }

        /// <summary>
        /// 汇率
        /// </summary>
        [Display(Name = "汇率")]
        public decimal? ExchangeRate { get; set; }

        /// <summary>
        /// 运费
        /// </summary>
        [Display(Name = "运费")]
        public decimal? ShippingFee { get; set; }

        /// <summary>
        /// 货柜状态
        /// </summary>
        [Display(Name = "状态")]
        public int? Status { get; set; }

        /// <summary>
        /// 备注
        /// </summary>
        [Display(Name = "备注")]
        public string? Remarks { get; set; }

        /// <summary>
        /// 备注2
        /// </summary>
        [Display(Name = "备注2")]
        public string? Remarks2 { get; set; }

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// 更新时间
        /// </summary>
        public DateTime? UpdatedAt { get; set; }

        /// <summary>
        /// 创建者
        /// </summary>
        public string? CreatedBy { get; set; }

        /// <summary>
        /// 更新者
        /// </summary>
        public string? UpdatedBy { get; set; }

        /// <summary>
        /// 是否已删除
        /// </summary>
        public bool IsDeleted { get; set; } = false;

        /// <summary>
        /// 货柜明细列表
        /// </summary>
        public List<YiwuContainerDetailDto> Details { get; set; } = new List<YiwuContainerDetailDto>();

        /// <summary>
        /// 状态显示名称
        /// </summary>
        public string StatusDisplayName => Status switch
        {
            0 => "草稿",
            1 => "已确认",
            2 => "已装柜",
            3 => "运输中",
            4 => "已到港",
            5 => "已清关",
            6 => "已完成",
            7 => "已取消",
            _ => "未知状态"
        };

        /// <summary>
        /// 是否已完成
        /// </summary>
        public bool IsCompleted => Status == 6;

        /// <summary>
        /// 是否可编辑
        /// </summary>
        public bool IsEditable => Status is 0 or 1 or 2 or 3 or 4 or 5 or 6 or 7;

        /// <summary>
        /// 运输天数
        /// </summary>
        public int? ShippingDays
        {
            get
            {
                if (LoadingDate.HasValue && ActualArrivalDate.HasValue)
                {
                    return (ActualArrivalDate.Value - LoadingDate.Value).Days;
                }
                return null;
            }
        }

        /// <summary>
        /// 装载率
        /// </summary>
        public decimal? LoadingRate
        {
            get
            {
                const decimal StandardContainerVolume = 68m; // 标准40尺货柜容量
                if (TotalVolume.HasValue && TotalVolume.Value > 0)
                {
                    return Math.Round(TotalVolume.Value / StandardContainerVolume * 100, 2);
                }
                return null;
            }
        }
    }

    /// <summary>
    /// 义乌货柜明细DTO - 基于新的ContainerDetail模型
    /// </summary>
    public class YiwuContainerDetailDto
    {
        /// <summary>
        /// 明细编码（主键）
        /// </summary>
        public string DetailCode { get; set; } = string.Empty;

        /// <summary>
        /// 货柜编码（外键）
        /// </summary>
        public string ContainerCode { get; set; } = string.Empty;

        /// <summary>
        /// 商品编码（外键）
        /// </summary>
        public string? ProductCode { get; set; }

        /// <summary>
        /// 装柜类型
        /// </summary>
        [Display(Name = "装柜类型")]
        public string? LoadingType { get; set; }

        /// <summary>
        /// 混装组编码
        /// </summary>
        [Display(Name = "混装组编码")]
        public string? MixedGroupCode { get; set; }

        /// <summary>
        /// 商品类型
        /// </summary>
        [Display(Name = "商品类型")]
        public string? ProductType { get; set; }

        /// <summary>
        /// 套装数量
        /// </summary>
        [Display(Name = "套装数量")]
        public decimal? SetQuantity { get; set; }

        /// <summary>
        /// 装柜件数
        /// </summary>
        [Display(Name = "装柜件数")]
        public decimal? LoadingPieces { get; set; }

        /// <summary>
        /// 装柜数量
        /// </summary>
        [Display(Name = "装柜数量")]
        public decimal? LoadingQuantity { get; set; }

        /// <summary>
        /// 国内价格
        /// </summary>
        [Display(Name = "国内价格")]
        public decimal? DomesticPrice { get; set; }

        /// <summary>
        /// 调整浮率
        /// </summary>
        [Display(Name = "调整浮率")]
        public decimal? AdjustmentRate { get; set; }

        /// <summary>
        /// 进口价格
        /// </summary>
        [Display(Name = "进口价格")]
        public decimal? ImportPrice { get; set; }

        /// <summary>
        /// 零售价
        /// </summary>
        [Display(Name = "零售价")]
        public decimal? OEMPrice { get; set; }

        /// <summary>
        /// 单件装箱数
        /// </summary>
        [Display(Name = "单件装箱数")]
        public decimal? PackingQuantity { get; set; }

        /// <summary>
        /// 单件体积
        /// </summary>
        [Display(Name = "单件体积")]
        public decimal? UnitVolume { get; set; }

        /// <summary>
        /// 合计装柜金额
        /// </summary>
        [Display(Name = "合计装柜金额")]
        public decimal? TotalAmount { get; set; }

        /// <summary>
        /// 合计装柜体积
        /// </summary>
        [Display(Name = "合计装柜体积")]
        public decimal? TotalVolume { get; set; }

        /// <summary>
        /// 运输成本
        /// </summary>
        [Display(Name = "运输成本")]
        public decimal? TransportCost { get; set; }

        /// <summary>
        /// 明细状态
        /// </summary>
        [Display(Name = "状态")]
        public int? Status { get; set; }

        /// <summary>
        /// 备注
        /// </summary>
        [Display(Name = "备注")]
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
        /// 创建者
        /// </summary>
        public string? CreatedBy { get; set; }

        /// <summary>
        /// 更新者
        /// </summary>
        public string? UpdatedBy { get; set; }

        /// <summary>
        /// 是否已删除
        /// </summary>
        public bool IsDeleted { get; set; } = false;

        /// <summary>
        /// 关联商品信息
        /// </summary>
        public ProductInfoDto? Product { get; set; }

        /// <summary>
        /// 状态显示名称
        /// </summary>
        public string StatusDisplayName => Status switch
        {
            0 => "正常",
            1 => "已确认",
            2 => "已装柜",
            3 => "已到货",
            4 => "已入库",
            5 => "异常",
            6 => "已取消",
            _ => "未知状态"
        };

        /// <summary>
        /// 实际单价
        /// </summary>
        public decimal? ActualUnitPrice
        {
            get
            {
                if (DomesticPrice.HasValue)
                {
                    var rate = AdjustmentRate ?? 1m;
                    return Math.Round(DomesticPrice.Value * rate, 2);
                }
                return null;
            }
        }

        /// <summary>
        /// 计算总金额
        /// </summary>
        public decimal? CalculatedTotalAmount
        {
            get
            {
                if (LoadingQuantity.HasValue && ActualUnitPrice.HasValue)
                {
                    return Math.Round(LoadingQuantity.Value * ActualUnitPrice.Value, 2);
                }
                return null;
            }
        }

        /// <summary>
        /// 计算总体积
        /// </summary>
        public decimal? CalculatedTotalVolume
        {
            get
            {
                if (LoadingQuantity.HasValue && UnitVolume.HasValue)
                {
                    return Math.Round(LoadingQuantity.Value * UnitVolume.Value, 3);
                }
                return null;
            }
        }

        /// <summary>
        /// 利润率
        /// </summary>
        public decimal? ProfitRate
        {
            get
            {
                if (ImportPrice.HasValue && ActualUnitPrice.HasValue && ActualUnitPrice.Value > 0)
                {
                    return Math.Round((ImportPrice.Value - ActualUnitPrice.Value) / ActualUnitPrice.Value * 100, 2);
                }
                return null;
            }
        }

        /// <summary>
        /// 是否异常
        /// </summary>
        public bool HasException
        {
            get
            {
                return Status == 5 || 
                       LoadingQuantity <= 0 || 
                       DomesticPrice <= 0 ||
                       string.IsNullOrWhiteSpace(ProductCode);
            }
        }

        /// <summary>
        /// 包装箱数
        /// </summary>
        public decimal? PackageBoxes
        {
            get
            {
                if (LoadingQuantity.HasValue && PackingQuantity.HasValue && PackingQuantity.Value > 0)
                {
                    return Math.Ceiling(LoadingQuantity.Value / PackingQuantity.Value);
                }
                return null;
            }
        }
    }

    /// <summary>
    /// 商品信息DTO
    /// </summary>
    public class ProductInfoDto
    {
        /// <summary>
        /// 商品编码
        /// </summary>
        public string? ProductCode { get; set; }

        /// <summary>
        /// HB货号
        /// </summary>
        public string? ItemNumber { get; set; }

        /// <summary>
        /// 条码
        /// </summary>
        public string? Barcode { get; set; }

        /// <summary>
        /// 中文名称
        /// </summary>
        public string? ChineseName { get; set; }

        /// <summary>
        /// 英文名称
        /// </summary>
        public string? EnglishName { get; set; }

        /// <summary>
        /// 商品图片URL
        /// </summary>
        public string? ImageUrl { get; set; }

        /// <summary>
        /// 商品规格
        /// </summary>
        public string? Specification { get; set; }

        /// <summary>
        /// 单位
        /// </summary>
        public string? Unit { get; set; }

        /// <summary>
        /// 零售价（美元）
        /// </summary>
        public decimal? OEMPrice { get; set; }

        /// <summary>
        /// 进口价格（美元）
        /// </summary>
        public decimal? ImportPrice { get; set; }
    }

    /// <summary>
    /// 义乌货柜查询请求DTO
    /// </summary>
    public class YiwuContainerQueryRequest
    {
        /// <summary>
        /// 页码
        /// </summary>
        public int Page { get; set; } = 1;

        /// <summary>
        /// 页大小
        /// </summary>
        public int PageSize { get; set; } = 20;

        /// <summary>
        /// 开始日期
        /// </summary>
        public DateTime? StartDate { get; set; }

        /// <summary>
        /// 结束日期
        /// </summary>
        public DateTime? EndDate { get; set; }

        /// <summary>
        /// 日期类型过滤 - LoadingDate, EstimatedArrivalDate, ActualArrivalDate
        /// </summary>
        public string DateType { get; set; } = "LoadingDate";

        /// <summary>
        /// 货柜编号过滤
        /// </summary>
        public string? ContainerNumberFilter { get; set; }

        /// <summary>
        /// 状态过滤
        /// </summary>
        public int? StatusFilter { get; set; }

        /// <summary>
        /// 商品货号过滤
        /// </summary>
        public string? ItemNumberFilter { get; set; }

        /// <summary>
        /// 排序字段
        /// </summary>
        public string SortBy { get; set; } = "LoadingDate";

        /// <summary>
        /// 排序方向
        /// </summary>
        public string SortDirection { get; set; } = "desc";
    }

    /// <summary>
    /// 义乌货柜列表响应DTO
    /// </summary>
    public class YiwuContainerListResponse
    {
        /// <summary>
        /// 货柜列表
        /// </summary>
        public List<YiwuContainerDto> Containers { get; set; } = new List<YiwuContainerDto>();

        /// <summary>
        /// 总数量
        /// </summary>
        public int TotalCount { get; set; }

        /// <summary>
        /// 页码
        /// </summary>
        public int Page { get; set; }

        /// <summary>
        /// 页大小
        /// </summary>
        public int PageSize { get; set; }

        /// <summary>
        /// 总页数
        /// </summary>
        public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
    }

    /// <summary>
    /// 批量添加义乌货柜明细请求DTO
    /// </summary>
    public class BatchAddYiwuContainerDetailsRequest
    {
        /// <summary>
        /// 货柜编码
        /// </summary>
        public string ContainerCode { get; set; } = string.Empty;

        /// <summary>
        /// 明细列表
        /// </summary>
        public List<BatchYiwuContainerDetailItem> Details { get; set; } = new List<BatchYiwuContainerDetailItem>();
    }

    /// <summary>
    /// 批量添加义乌明细项
    /// </summary>
    public class BatchYiwuContainerDetailItem
    {
        /// <summary>
        /// 商品货号
        /// </summary>
        public string ItemNumber { get; set; } = string.Empty;

        /// <summary>
        /// 装柜件数
        /// </summary>
        public decimal LoadingPieces { get; set; }

        /// <summary>
        /// 备注
        /// </summary>
        public string? Remarks { get; set; }
    }

    /// <summary>
    /// 批量操作响应DTO
    /// </summary>
    public class BatchOperationResponse
    {
        /// <summary>
        /// 是否成功
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 成功数量
        /// </summary>
        public int SuccessCount { get; set; }

        /// <summary>
        /// 失败数量
        /// </summary>
        public int FailedCount { get; set; }

        /// <summary>
        /// 新增数量
        /// </summary>
        public int CreatedCount { get; set; }

        /// <summary>
        /// 更新数量
        /// </summary>
        public int UpdatedCount { get; set; }

        /// <summary>
        /// 错误信息列表
        /// </summary>
        public List<string> Errors { get; set; } = new List<string>();

        /// <summary>
        /// 消息
        /// </summary>
        public string Message { get; set; } = string.Empty;
    }
}
