namespace BlazorApp.Shared.DTOs
{
    // 工时统计 / 工资表 / 个人时薪表 / 带薪假选项接口（规格第 2 节；字段与 Web 卡 C、移动端卡 D 的约定对齐）。
    // 工时一律十进制小时、最多 2 位小数；日期是门店本地日期。

    public class AttendancePayrollQueryDto
    {
        public string? StoreCode { get; set; }
        /// <summary>期起始日；为空时取今天所在的期（两周按双周起始日对齐）。</summary>
        public DateTime? PeriodStart { get; set; }
        /// <summary>1 或 2；为空时取设置的默认周数。</summary>
        public int? Weeks { get; set; }
        /// <summary>true 时把待审的加班候选、用餐加回也算进当天工时（只预览，工资表永远只计已审核）。</summary>
        public bool IncludePending { get; set; }
        /// <summary>true 时把员工在其他分店的工时并进本表（查看参数，不改数据）。</summary>
        public bool MergeStores { get; set; }
    }

    public class AttendancePayrollTierHoursDto
    {
        public decimal Weekday { get; set; }
        public decimal Saturday { get; set; }
        public decimal Sunday { get; set; }
        public decimal PublicHoliday { get; set; }
    }

    public class AttendancePayrollPeriodDayDto
    {
        public DateTime Date { get; set; }
        /// <summary>ISO 星期：1 = 周一 … 7 = 周日。</summary>
        public int IsoWeekday { get; set; }
        public bool IsPublicHoliday { get; set; }
        public string? HolidayName { get; set; }
    }

    public class AttendancePayrollSegmentDto
    {
        /// <summary>HH:mm（门店本地）。</summary>
        public string Start { get; set; } = string.Empty;
        /// <summary>HH:mm；系统午夜自动下班为 24:00，未闭合为 null。</summary>
        public string? End { get; set; }
        public bool IsAutoClockOut { get; set; }
    }

    public class AttendancePayrollDayLeaveDto
    {
        /// <summary>PaidSick / PaidAnnual / AnnualCashOut / AnnualLeave / SickLeave。</summary>
        public string Type { get; set; } = string.Empty;
        /// <summary>当天申请的带薪工时；无薪类型为 0。</summary>
        public decimal Hours { get; set; }
        public bool HasCertificate { get; set; }
        public string? LeaveGuid { get; set; }
    }

    public class AttendancePayrollDayPublicHolidayPayDto
    {
        /// <summary>Pending / Confirmed / NotApplicable。</summary>
        public string State { get; set; } = string.Empty;
        public decimal? Hours { get; set; }
        public int Count { get; set; }
        public string? ApprovalGuid { get; set; }
    }

    public class AttendancePayrollOtherStoreHoursDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string? StoreName { get; set; }
        public decimal Hours { get; set; }
    }

    public class AttendancePayrollEmployeeDayDto
    {
        public DateTime Date { get; set; }
        /// <summary>当天计薪工时；当天没有排班也没有打卡时为 null。</summary>
        public decimal? PaidHours { get; set; }
        public int? PaidMinutes { get; set; }
        public AttendancePayrollTierHoursDto ByTier { get; set; } = new();
        public bool HasSchedule { get; set; }
        public bool IsScheduled { get; set; }
        public bool IsUnavailable { get; set; }
        public bool IsPublicHoliday { get; set; }
        public string? HolidayName { get; set; }
        public AttendancePayrollDayLeaveDto? Leave { get; set; }
        public AttendancePayrollDayPublicHolidayPayDto? PublicHolidayPay { get; set; }
        /// <summary>当天待处理的审批类型码（Punch / PunchAdjustment / Overtime / MissingClockOut / MealBreak / Leave / PublicHolidayPay）。</summary>
        public List<string> Pending { get; set; } = new();
        /// <summary>当天已批准的审批类型码。</summary>
        public List<string> ApprovedMarks { get; set; } = new();
        public List<AttendancePayrollOtherStoreHoursDto> OtherStores { get; set; } = new();
        public bool IsBirthday { get; set; }
        public List<AttendancePayrollSegmentDto> Segments { get; set; } = new();
    }

    public class AttendancePayrollHoursTotalsDto
    {
        public decimal Weekday { get; set; }
        public decimal Saturday { get; set; }
        public decimal Sunday { get; set; }
        public decimal PublicHoliday { get; set; }
        /// <summary>带薪假合计（申请工时）= 病假 + 年假 + 年假发放 + 带薪公假。</summary>
        public decimal Leave { get; set; }
        public decimal Total { get; set; }
        public decimal Sick { get; set; }
        public decimal Annual { get; set; }
        public decimal CashOut { get; set; }
        public decimal PublicHolidayPay { get; set; }
    }

    public class AttendancePayrollHoursEmployeeDto
    {
        public string UserGuid { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        /// <summary>FullTime / PartTime / Casual；员工资料没填时为 null。</summary>
        public string? EmploymentType { get; set; }
        public bool IsManager { get; set; }
        public bool IsStoreManager { get; set; }
        /// <summary>未成年时的周岁（期末那天），成年为 null。</summary>
        public int? AgeYears { get; set; }
        public DateTime? Birthday { get; set; }
        public int SegmentLimit { get; set; }
        public bool HasOtherStores { get; set; }
        public int PendingCount { get; set; }
        public decimal LeaveHours { get; set; }
        public List<AttendancePayrollEmployeeDayDto> Days { get; set; } = new();
        public AttendancePayrollHoursTotalsDto Totals { get; set; } = new();
    }

    public class AttendancePayrollOtherStoreItemDto
    {
        public string UserGuid { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public string StoreCode { get; set; } = string.Empty;
        public string? StoreName { get; set; }
        public decimal Hours { get; set; }
    }

    /// <summary>提醒条（结构化字段，前端按 i18n 组句）。</summary>
    public class AttendancePayrollAlertDto
    {
        /// <summary>MultiStore / PublicHolidayPay / Birthday。</summary>
        public string Type { get; set; } = string.Empty;
        public string? UserGuid { get; set; }
        public string? UserName { get; set; }
        public DateTime? Date { get; set; }
        public List<AttendancePayrollOtherStoreItemDto> OtherStoreItems { get; set; } = new();
        public string? EmploymentType { get; set; }
        public string? HolidayName { get; set; }
        public int? OrdinaryCount { get; set; }
        public int? LookbackWeeks { get; set; }
        public int? MinCount { get; set; }
        public decimal? Hours { get; set; }
        public string? ApprovalGuid { get; set; }
        public int? AgeYears { get; set; }
        public bool? IsMinor { get; set; }
    }

    public class AttendancePayrollHoursDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string? StoreName { get; set; }
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public int Weeks { get; set; }
        /// <summary>期起始日的 ISO 周号。</summary>
        public int WeekNumber { get; set; }
        /// <summary>Open / Submitted。</summary>
        public string PeriodStatus { get; set; } = "Open";
        public int PendingCount { get; set; }
        public bool IncludePending { get; set; }
        public bool MergeStores { get; set; }
        public List<AttendancePayrollPeriodDayDto> Days { get; set; } = new();
        public List<AttendancePayrollHoursEmployeeDto> Employees { get; set; } = new();
        public List<AttendancePayrollAlertDto> Alerts { get; set; } = new();
        public AttendancePayrollHoursTotalsDto Totals { get; set; } = new();
    }

    public class AttendancePayrollBucketHoursDto
    {
        public decimal Sunday { get; set; }
        public decimal Saturday { get; set; }
        public decimal Leave { get; set; }
        public decimal PublicHoliday { get; set; }
        public decimal Weekday { get; set; }
    }

    public class AttendancePayrollPayHoursDto
    {
        public decimal Weekday { get; set; }
        public decimal Saturday { get; set; }
        public decimal Sunday { get; set; }
        public decimal PublicHoliday { get; set; }
        /// <summary>带薪假（年假按财务核定工时）。</summary>
        public decimal Leave { get; set; }
        public decimal Total { get; set; }
    }

    public class AttendancePayrollAnnualDetailDto
    {
        /// <summary>PaidAnnual / AnnualCashOut。</summary>
        public string LeaveType { get; set; } = string.Empty;
        public decimal RequestedHours { get; set; }
        /// <summary>财务核定工时；没有核定记录时等于申请工时。</summary>
        public decimal ApprovedHours { get; set; }
        public bool IsFinanceApproved { get; set; }
    }

    public class AttendancePayrollLeaveDetailDto
    {
        public decimal Sick { get; set; }
        public decimal PublicHolidayPay { get; set; }
        public AttendancePayrollAnnualDetailDto Annual { get; set; } = new() { LeaveType = "PaidAnnual" };
        public AttendancePayrollAnnualDetailDto CashOut { get; set; } = new() { LeaveType = "AnnualCashOut" };
    }

    public class AttendancePayrollTransferDto
    {
        public AttendancePayrollBucketHoursDto Buckets { get; set; } = new();
        public decimal Hours { get; set; }
        public decimal Amount { get; set; }
    }

    public class AttendancePayrollCashDto
    {
        public AttendancePayrollBucketHoursDto Buckets { get; set; } = new();
        public decimal Hours { get; set; }
        public decimal? Rate { get; set; }
        /// <summary>现金工时 × 现金时薪，不含补贴。</summary>
        public decimal Amount { get; set; }
    }

    public class AttendancePayrollPayRowDto
    {
        public string UserGuid { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string? EmploymentType { get; set; }
        public int? AgeYears { get; set; }
        /// <summary>Transfer / Cash / Mixed；还没有时薪记录时为 null。</summary>
        public string? PayMode { get; set; }
        public decimal FixedTransferHoursPerWeek { get; set; }
        public AttendancePayrollPayHoursDto Hours { get; set; } = new();
        public AttendancePayrollLeaveDetailDto LeaveDetail { get; set; } = new();
        public AttendancePayrollTransferDto Transfer { get; set; } = new();
        public AttendancePayrollCashDto Cash { get; set; } = new();
        public decimal Allowance { get; set; }
        public decimal GrossPay { get; set; }
        public string? Note { get; set; }
        /// <summary>NO_RATE / MISSING_TRANSFER_RATE / MISSING_CASH_RATE / BELOW_MINIMUM_RATE。</summary>
        public List<string> Warnings { get; set; } = new();
    }

    public class AttendancePayrollPayDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public int Weeks { get; set; }
        public string PeriodStatus { get; set; } = "Open";
        public int PendingCount { get; set; }
        public DateTime? SubmittedAt { get; set; }
        public string? SubmittedBy { get; set; }
        public List<AttendancePayrollPayRowDto> Rows { get; set; } = new();
        public decimal TotalGrossPay { get; set; }
    }

    public class AttendancePayRateQueryDto
    {
        public string? StoreCode { get; set; }
        public string? Keyword { get; set; }
        /// <summary>true 时只返回「公假转账价与平日转账价不一致」的员工（财务据此给他们新建时薪记录）。</summary>
        public bool PublicHolidayMismatchOnly { get; set; }
    }

    public class AttendancePayRateDto
    {
        public string? RateGuid { get; set; }
        public string UserGuid { get; set; } = string.Empty;
        public string? UserName { get; set; }
        public string? EmploymentType { get; set; }
        public bool IsManager { get; set; }
        public int? AgeYears { get; set; }
        public DateTime? Birthday { get; set; }
        /// <summary>未成年人生日已过、而当前时薪记录生效早于这次生日：提示核对时薪。</summary>
        public bool BirthdayReviewHint { get; set; }
        public string? PayMode { get; set; }
        public decimal FixedTransferHoursPerWeek { get; set; }
        public decimal? TransferWeekdayRate { get; set; }
        public decimal? TransferSaturdayRate { get; set; }
        public decimal? TransferSundayRate { get; set; }
        public decimal? TransferPublicHolidayRate { get; set; }
        /// <summary>
        /// 公假转账价与平日转账价不一致（全现金模式恒为 false）。公司规定公假转账价 = 平日转账价；
        /// 历史记录不改（保证已锁定周期重算结果不变），不一致的由财务新建一条时薪记录纠正。
        /// </summary>
        public bool PublicHolidayRateMismatch { get; set; }
        public decimal? CashRate { get; set; }
        public decimal CashAllowancePerPeriod { get; set; }
        public decimal? MinimumRate { get; set; }
        public DateTime? EffectiveFrom { get; set; }
        public string? Remark { get; set; }
        public DateTime? CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
    }

    /// <summary>新增一条时薪生效记录（只新增，不改历史）。</summary>
    public class CreateEmployeePayRateDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string UserGuid { get; set; } = string.Empty;
        public string PayMode { get; set; } = "Transfer";
        public decimal FixedTransferHoursPerWeek { get; set; }
        public decimal? TransferWeekdayRate { get; set; }
        public decimal? TransferSaturdayRate { get; set; }
        public decimal? TransferSundayRate { get; set; }
        public decimal? TransferPublicHolidayRate { get; set; }
        public decimal? CashRate { get; set; }
        public decimal CashAllowancePerPeriod { get; set; }
        public decimal? MinimumRate { get; set; }
        public DateTime EffectiveFrom { get; set; }
        public string? Remark { get; set; }
    }

    public class AttendancePayPeriodKeyDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public DateTime PeriodStart { get; set; }
        public int Weeks { get; set; } = 1;
    }

    public class UpsertAttendancePayPeriodNoteDto : AttendancePayPeriodKeyDto
    {
        public string UserGuid { get; set; } = string.Empty;
        /// <summary>空字符串＝清除备注。</summary>
        public string? Note { get; set; }
    }

    public class UpsertAttendancePayPeriodLeaveApprovalDto : AttendancePayPeriodKeyDto
    {
        public string UserGuid { get; set; } = string.Empty;
        /// <summary>只能是 PaidAnnual / AnnualCashOut。</summary>
        public string LeaveType { get; set; } = string.Empty;
        public decimal ApprovedHours { get; set; }
    }

    public class UnlockAttendancePayPeriodDto : AttendancePayPeriodKeyDto
    {
        public string Reason { get; set; } = string.Empty;
    }

    public class AttendancePayPeriodDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public DateTime PeriodStart { get; set; }
        public int Weeks { get; set; }
        public string Status { get; set; } = "Open";
        public DateTime? SubmittedAt { get; set; }
        public string? SubmittedBy { get; set; }
        public DateTime? UnlockedAt { get; set; }
        public string? UnlockedBy { get; set; }
        public string? UnlockReason { get; set; }
    }

    public class AttendancePayPeriodNoteDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public DateTime PeriodStart { get; set; }
        public int Weeks { get; set; }
        public string UserGuid { get; set; } = string.Empty;
        public string Note { get; set; } = string.Empty;
    }

    public class AttendancePayPeriodLeaveApprovalDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public DateTime PeriodStart { get; set; }
        public int Weeks { get; set; }
        public string UserGuid { get; set; } = string.Empty;
        public string LeaveType { get; set; } = string.Empty;
        public decimal RequestedHours { get; set; }
        public decimal ApprovedHours { get; set; }
    }

    /// <summary>员工本人申请带薪假前的规则参数与候选日期（员工读不到考勤设置接口）。</summary>
    public class AttendanceLeaveOptionsDto
    {
        public DateTime Today { get; set; }
        public string StoreCode { get; set; } = string.Empty;
        public string? StoreName { get; set; }
        public string? EmployeeName { get; set; }
        public string? EmploymentType { get; set; }
        public bool PaidLeaveEligible { get; set; }
        public AttendanceLeaveRulesDto Rules { get; set; } = new();
        public List<AttendanceLeaveHolidayDto> PublicHolidays { get; set; } = new();
        /// <summary>本人在该店已发布（Active）排班的日期。</summary>
        public List<DateTime> PublishedScheduleDates { get; set; } = new();
        public List<AttendanceLeaveScheduleDto> SickLeaveCandidates { get; set; } = new();
    }

    public class AttendanceLeaveRulesDto
    {
        public int SickLeaveSubmitDays { get; set; }
        public int AnnualLeaveLeadWeeks { get; set; }
        public int AnnualLeaveMaxWeeks { get; set; }
        public int AnnualCashOutLeadDays { get; set; }
        public int AnnualCashOutMaxWeeks { get; set; }
        public int AnnualLeaveHoursPerWorkday { get; set; }
        public int MaxCertificatePhotos { get; set; }
    }

    public class AttendanceLeaveHolidayDto
    {
        public DateTime Date { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
