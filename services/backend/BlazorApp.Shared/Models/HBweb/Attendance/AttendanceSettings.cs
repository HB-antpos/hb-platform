using SqlSugar;

namespace BlazorApp.Shared.Models
{
    [SugarTable("AttendanceSettings")]
    public class AttendanceSettings : BaseEntity
    {
        [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
        public int Id { get; set; }

        [SugarColumn(IsNullable = false)]
        public int LateGraceMinutes { get; set; } = 5;

        [SugarColumn(IsNullable = false)]
        public int EarlyLeaveGraceMinutes { get; set; } = 5;

        [SugarColumn(IsNullable = false)]
        public bool AllowNoSchedulePunch { get; set; } = true;

        [SugarColumn(IsNullable = false)]
        public bool RequireApprovalForLate { get; set; } = true;

        [SugarColumn(IsNullable = false)]
        public bool RequireApprovalForEarlyLeave { get; set; } = true;

        [SugarColumn(IsNullable = false)]
        public bool RequireApprovalForNoSchedule { get; set; } = true;

        [SugarColumn(IsNullable = false)]
        public bool RequireApprovalForDuplicate { get; set; } = true;

        /// <summary>员工每天每店的打卡段数上限（默认 4）。以下三列由迁移 20261026.001 添加。</summary>
        [SugarColumn(IsNullable = false)]
        public int EmployeeSegmentLimit { get; set; } = 4;

        /// <summary>管理本店的店长每天每店的打卡段数上限（默认 6）。</summary>
        [SugarColumn(IsNullable = false)]
        public int ManagerSegmentLimit { get; set; } = 6;

        /// <summary>门店时区 00:00 是否为未闭合班段补系统下班卡（默认开）。</summary>
        [SugarColumn(IsNullable = false)]
        public bool AutoClockOutAtMidnight { get; set; } = true;

        // 以下十列由迁移 20261026.002-attendance-payroll-leave 添加（规格第 1 节）。
        /// <summary>工时统计默认周期周数（1 或 2）。</summary>
        [SugarColumn(IsNullable = false)]
        public int DefaultPeriodWeeks { get; set; } = 1;

        /// <summary>双周起始日（周一）；两周周期从它起每 14 天一期，与发薪周期对齐。null 时按 2026-10-05。</summary>
        [SugarColumn(IsNullable = true, ColumnDataType = "date")]
        public DateTime? FortnightAnchorDate { get; set; }

        /// <summary>带薪公假提醒：回看周数（默认 10）。</summary>
        [SugarColumn(IsNullable = false)]
        public int PublicHolidayLookbackWeeks { get; set; } = 10;

        /// <summary>带薪公假提醒：回看窗口内同一星期几排班次数门槛（默认 7）。</summary>
        [SugarColumn(IsNullable = false)]
        public int PublicHolidayMinCount { get; set; } = 7;

        /// <summary>带薪病假：生病当天起几天内提交（默认 4）。</summary>
        [SugarColumn(IsNullable = false)]
        public int SickLeaveSubmitDays { get; set; } = 4;

        /// <summary>带薪年假休息：至少提前几周（默认 4）。</summary>
        [SugarColumn(IsNullable = false)]
        public int AnnualLeaveLeadWeeks { get; set; } = 4;

        /// <summary>带薪年假休息：单次最多几周（默认 2）。</summary>
        [SugarColumn(IsNullable = false)]
        public int AnnualLeaveMaxWeeks { get; set; } = 2;

        /// <summary>年假发放（照常上班）：至少提前几天（默认 2）。</summary>
        [SugarColumn(IsNullable = false)]
        public int AnnualCashOutLeadDays { get; set; } = 2;

        /// <summary>年假发放：单次最多几周（默认 4）。</summary>
        [SugarColumn(IsNullable = false)]
        public int AnnualCashOutMaxWeeks { get; set; } = 4;

        /// <summary>生日提前几天提醒（默认 3）。</summary>
        [SugarColumn(IsNullable = false)]
        public int BirthdayRemindDays { get; set; } = 3;
    }
}
