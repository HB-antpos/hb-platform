using SqlSugar;

namespace BlazorApp.Shared.Models
{
    [SugarTable("AttendanceLeaveRequest")]
    public class AttendanceLeaveRequest : BaseEntity
    {
        [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
        public int Id { get; set; }

        [SugarColumn(IsNullable = false, Length = 50)]
        public string LeaveGuid { get; set; } = Guid.NewGuid().ToString();

        [SugarColumn(IsNullable = false, Length = 50)]
        public string StoreCode { get; set; } = string.Empty;

        [SugarColumn(IsNullable = false, Length = 50)]
        public string UserGuid { get; set; } = string.Empty;

        [SugarColumn(IsNullable = false, Length = 30)]
        public string LeaveType { get; set; } = "AnnualLeave";

        [SugarColumn(IsNullable = false)]
        public DateTime StartDate { get; set; }

        [SugarColumn(IsNullable = false)]
        public DateTime EndDate { get; set; }

        [SugarColumn(IsNullable = true)]
        public TimeSpan? StartTime { get; set; }

        [SugarColumn(IsNullable = true)]
        public TimeSpan? EndTime { get; set; }

        [SugarColumn(IsNullable = true, Length = 1000)]
        public string? Reason { get; set; }

        [SugarColumn(IsNullable = true, Length = 500)]
        public string? AttachmentUrl { get; set; }

        [SugarColumn(IsNullable = false, Length = 30)]
        public string Status { get; set; } = "Pending";

        [SugarColumn(IsNullable = true, Length = 50)]
        public string? ReviewedBy { get; set; }

        [SugarColumn(IsNullable = true)]
        public DateTime? ReviewedAt { get; set; }

        [SugarColumn(IsNullable = true, Length = 500)]
        public string? ReviewRemark { get; set; }

        // 以下三列由迁移 20261026.002-attendance-payroll-leave 添加。
        /// <summary>
        /// 带薪工时（申请时算好）：PaidSick = 所选排班工时扣餐；PaidAnnual / AnnualCashOut = 区间内工作日（排除公假）× 8 h。
        /// 旧的无薪类型（AnnualLeave / SickLeave）为 null。
        /// </summary>
        [SugarColumn(IsNullable = true, Length = 6, DecimalDigits = 2)]
        public decimal? PaidHours { get; set; }

        /// <summary>附件地址 JSON 数组（病假医嘱 ≤ 3 张）；旧单字段 AttachmentUrl 同时写第一张，兼容旧客户端。</summary>
        [SugarColumn(IsNullable = true, ColumnDataType = StaticConfig.CodeFirst_BigString)]
        public string? AttachmentUrls { get; set; }

        /// <summary>带薪病假所选排班的 ScheduleGuid JSON 数组。</summary>
        [SugarColumn(IsNullable = true, ColumnDataType = StaticConfig.CodeFirst_BigString)]
        public string? ScheduleGuids { get; set; }
    }
}
