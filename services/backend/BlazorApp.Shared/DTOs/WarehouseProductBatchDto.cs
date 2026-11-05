using System.Text.Json.Serialization;
using BlazorApp.Shared.Helper;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 仓库商品批量管理DTO
    /// 用于批量编辑页面的数据传输
    /// </summary>
    public class WarehouseProductBatchDto
    {
        /// <summary>
        /// 商品编码（主键）
        /// </summary>
        [Required(ErrorMessage = "商品编码不能为空")]
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>
        /// 商品名称（来自Product表）
        /// </summary>
        public string? ProductName { get; set; }

        /// <summary>
        /// 货号（来自Product表）
        /// </summary>
        public string? ItemNumber { get; set; }

        /// <summary>
        /// 本地供应商编码（来自Product表）
        /// </summary>
        public string? LocalSupplierCode { get; set; }

        /// <summary>
        /// 供应商名称（来自ChinaSupplier表）
        /// </summary>
        [JsonConverter(typeof(DomesticSupplierInfoJsonConverter))]
        public string? SupplierName { get; set; }

        /// <summary>
        /// 商品图片URL（来自Product表）
        /// </summary>
        public string? ImageUrl { get; set; }

        /// <summary>
        /// 国内价格
        /// </summary>
        [Range(0, double.MaxValue, ErrorMessage = "国内价格不能为负数")]
        public decimal? DomesticPrice { get; set; }

        /// <summary>
        /// 零售价
        /// </summary>
        [Range(0, double.MaxValue, ErrorMessage = "零售价不能为负数")]
        public decimal? OEMPrice { get; set; }

        /// <summary>
        /// 进口价格
        /// </summary>
        [Range(0, double.MaxValue, ErrorMessage = "进口价格不能为负数")]
        public decimal? ImportPrice { get; set; }

        /// <summary>
        /// 库存数量
        /// </summary>
        [Range(0, int.MaxValue, ErrorMessage = "库存数量不能为负数")]
        public int? StockQuantity { get; set; }

        /// <summary>
        /// 最小订货量
        /// </summary>
        [Range(0, int.MaxValue, ErrorMessage = "最小订货量不能为负数")]
        public int? MinOrderQuantity { get; set; }

        /// <summary>
        /// 库存金额（自动计算：库存数量 × 进口价）
        /// </summary>
        public decimal? StockValue { get; set; }

        /// <summary>
        /// 库存预警数
        /// </summary>
        [Range(0, int.MaxValue, ErrorMessage = "库存预警数不能为负数")]
        public int? StockAlertQuantity { get; set; }

        /// <summary>
        /// 单件体积
        /// </summary>
        [Range(0, double.MaxValue, ErrorMessage = "单件体积不能为负数")]
        public decimal? Volume { get; set; }

        /// <summary>
        /// 使用状态（true-启用，false-禁用）
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// 仓位GUID（UI限制只能选1个）
        /// </summary>
        public string? LocationGuid { get; set; }

        /// <summary>
        /// 仓位代码（显示用）
        /// </summary>
        public string? LocationCode { get; set; }

        /// <summary>
        /// 仓位条码（显示用）
        /// </summary>
        public string? LocationBarcode { get; set; }

        /// <summary>
        /// 行版本号（乐观锁，用于并发控制）
        /// </summary>
        public byte[]? RowVersion { get; set; }

        /// <summary>
        /// 前端标记：是否已修改
        /// </summary>
        public bool IsModified { get; set; }

        /// <summary>
        /// 前端标记：是否选中
        /// </summary>
        public bool IsSelected { get; set; }

        /// <summary>
        /// 创建时间
        /// </summary>
        public DateTime? CreatedAt { get; set; }

        /// <summary>
        /// 更新时间
        /// </summary>
        public DateTime? UpdatedAt { get; set; }

        /// <summary>
        /// 计算库存金额（库存数量 × 进口价）
        /// </summary>
        public void CalculateStockValue()
        {
            if (StockQuantity.HasValue && ImportPrice.HasValue)
            {
                StockValue = StockQuantity.Value * ImportPrice.Value;
            }
            else
            {
                StockValue = null;
            }
        }
    }
}

