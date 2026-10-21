using System;
using SqlSugar;

namespace BlazorApp.Shared.Models
{
    /// <summary>
    /// 分店订货单详情表（仓库订单详情）
    /// 功能：记录订货单每一行商品明细（商品、数量、价格与金额等）
    /// 来源：HQ表 CBP_RED_分店订单详情表 同步落库
    /// </summary>
    [SugarTable("WareHouseOrderDetails")]
    public class WareHouseOrderDetails : BaseEntity
    {
        /// <summary>
        /// 详情GUID（主键）
        /// 映射：HQ详情 HGUID；本地新增时自动生成
        /// </summary>
        [SugarColumn(IsPrimaryKey = true, IsNullable = false)]
        public string DetailGUID { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// 关联主表GUID（OrderGUID）
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 50)]
        public string? OrderGUID { get; set; }

        /// <summary>
        /// 分店代码
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 50)]
        public string? StoreCode { get; set; }

        /// <summary>
        /// 分店商品编码
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 50)]
        public string? StoreProductCode { get; set; }

        /// <summary>
        /// 商品编码
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 50)]
        public string? ProductCode { get; set; }

        /// <summary>
        /// 数量
        /// </summary>
        [SugarColumn(IsNullable = true, DecimalDigits = 2)]
        public decimal? Quantity { get; set; }

        /// <summary>
        /// 配货数量
        /// </summary>
        [SugarColumn(IsNullable = true, DecimalDigits = 2)]
        public decimal? AllocQuantity { get; set; }

        /// <summary>
        /// 退货原因码（仅退货行有值）：damaged / wrongItem / overShipped / quality / nearExpiry / other。
        /// 退货行 = AllocQuantity &lt; 0 且 Quantity = 0。
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 32)]
        public string? ReturnReason { get; set; }

        /// <summary>
        /// 退货处置（仅作记录，不联动库存）：0 回仓可售，1 报损不回库。
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public int? ReturnDisposition { get; set; }

        /// <summary>
        /// 退货备注（仅退货行）。
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 200)]
        public string? ReturnNote { get; set; }

        /// <summary>
        /// 上次成本
        /// </summary>
        [SugarColumn(IsNullable = true, DecimalDigits = 2)]
        public decimal? LastCost { get; set; }

        /// <summary>
        /// 进口价格
        /// </summary>
        [SugarColumn(IsNullable = true, DecimalDigits = 2)]
        public decimal? ImportPrice { get; set; }

        /// <summary>
        /// 合计进口金额
        /// </summary>
        [SugarColumn(IsNullable = true, DecimalDigits = 2)]
        public decimal? ImportAmount { get; set; }

        /// <summary>
        /// 零售价
        /// </summary>
        [SugarColumn(IsNullable = true, DecimalDigits = 2)]
        public decimal? OEMPrice { get; set; }

        /// <summary>
        /// 合计零售价金额
        /// </summary>
        [SugarColumn(IsNullable = true, DecimalDigits = 2)]
        public decimal? OEMAmount { get; set; }
    }
}
