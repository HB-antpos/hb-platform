using System;

namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 更新货柜信息DTO
    /// </summary>
    public class UpdateContainerDto
    {
        /// <summary>
        /// 货柜编号
        /// </summary>
        public string? 货柜编号 { get; set; }

        /// <summary>
        /// 装柜日期
        /// </summary>
        public DateTime? 装柜日期 { get; set; }

        /// <summary>
        /// 预计到岸日期
        /// </summary>
        public DateTime? 预计到岸日期 { get; set; }

        /// <summary>
        /// 实际到货日期
        /// </summary>
        public DateTime? 实际到货日期 { get; set; }

        /// <summary>
        /// 为 true 时把预计到岸日期清空；不能与 预计到岸日期 同时传值
        /// </summary>
        public bool? ClearEstimatedArrivalDate { get; set; }

        /// <summary>
        /// 为 true 时把实际到货日期清空；不能与 实际到货日期 同时传值
        /// </summary>
        public bool? ClearActualArrivalDate { get; set; }

        /// <summary>
        /// 汇率
        /// </summary>
        public decimal? 汇率 { get; set; }

        /// <summary>
        /// 运费
        /// </summary>
        public decimal? 运费 { get; set; }

        /// <summary>
        /// 备注
        /// </summary>
        public string? 备注 { get; set; }

        /// <summary>
        /// 货柜状态
        /// </summary>
        public int? 状态 { get; set; }
    }
}


