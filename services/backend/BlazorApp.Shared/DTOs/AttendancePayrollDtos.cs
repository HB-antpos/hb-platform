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
        /// <summary>上班打卡的来源（PosQr 收银机扫码 / ManualAdjustment 店长修改 / ManualEntry 补录 / App …）；页面气泡显示用。</summary>
        public string? InSource { get; set; }
        /// <summary>下班打卡的来源；未闭合为 null。</summary>
        public string? OutSource { get; set; }
        /// <summary>上班打卡的收银机设备码（如 POS_1003_1308），非收银机打卡为 null。不含手机设备标识。</summary>
        public string? DeviceCode { get; set; }
    }

    /// <summary>
    /// 一个排班在工时统计格子上的明细（气泡展示用）：排班窗口、各班段打卡、用餐扣除与加班。
    /// 一天有几个排班就有几项；未打卡的班 Segments 为空。分钟数口径与工资计算完全一致（同一次 CalculateDay 的结果）。
    /// </summary>
    public class AttendancePayrollShiftDto
    {
        /// <summary>排班开始 HH:mm（门店本地）。</summary>
        public string ScheduleStart { get; set; } = string.Empty;
        /// <summary>排班结束 HH:mm；跨午夜的班为次日的时刻。</summary>
        public string ScheduleEnd { get; set; } = string.Empty;
        /// <summary>实际打卡工作分钟（所有闭合班段之和）。</summary>
        public int WorkedMinutes { get; set; }
        /// <summary>落在排班窗口内的分钟。</summary>
        public int InScheduleMinutes { get; set; }
        /// <summary>扣除的用餐分钟。</summary>
        public int MealDeductionMinutes { get; set; }
        /// <summary>批准的用餐加回分钟。</summary>
        public int MealAddBackMinutes { get; set; }
        /// <summary>已批准计入的加班分钟。</summary>
        public int OvertimeMinutes { get; set; }
        /// <summary>该排班最终计薪分钟。</summary>
        public int PaidMinutes { get; set; }
        public List<AttendancePayrollSegmentDto> Segments { get; set; } = new();
    }

    public class AttendancePayrollDayLeaveDto
    {
        /// <summary>PaidSick / PaidAnnual / AnnualCashOut / AnnualLeave / SickLeave。</summary>
        public string Type { get; set; } = string.Empty;
        /// <summary>当天申请的带薪工时；无薪类型为 0。</summary>
        public decimal Hours { get; set; }
        public bool HasCertificate { get; set; }
        public string? LeaveGuid { get; set; }
        /// <summary>店长补录建的假：留痕（原因 / 补录人 / 时间），页面显示「补」角标并允许撤销；员工自己申请的为 null。</summary>
        public AttendancePayrollDayManualEntryDto? ManualEntry { get; set; }
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
        /// <summary>当天待处理的审批类型码（Punch / PunchAdjustment / Overtime / MissingClockOut / MealBreak / Leave / PublicHolidayPay / NoShow）。</summary>
        public List<string> Pending { get; set; } = new();
        /// <summary>当天已批准的审批类型码。</summary>
        public List<string> ApprovedMarks { get; set; } = new();
        public List<AttendancePayrollOtherStoreHoursDto> OtherStores { get; set; } = new();
        public bool IsBirthday { get; set; }
        public List<AttendancePayrollSegmentDto> Segments { get; set; } = new();
        /// <summary>当天每个排班的明细（排班窗口 / 打卡段与来源 / 用餐扣除 / 加班），气泡展示用；没有排班的天为空。</summary>
        public List<AttendancePayrollShiftDto> Shifts { get; set; } = new();
        /// <summary>有排班、排班窗口已过去、却一次打卡都没有（目前按 0 工时计）；可在页面上补录。</summary>
        public bool MissingPunch { get; set; }
        /// <summary>店长已确认缺勤（NoShow 待审已批准）：该班 0 工时，格子显示「缺勤」，MissingPunch 置 false。</summary>
        public bool NoShowConfirmed { get; set; }
        /// <summary>未打卡班的排班窗口（HH:mm），补录表单预填用；只在 MissingPunch 时返回。</summary>
        public string? ScheduleStart { get; set; }
        public string? ScheduleEnd { get; set; }
        /// <summary>这一天有手动补录的打卡时返回留痕（补录人 / 时间 / 原因）。</summary>
        public AttendancePayrollDayManualEntryDto? ManualEntry { get; set; }
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
        /// <summary>员工昵称；页面显示为「全名（昵称）」，没填为 null。</summary>
        public string? Nickname { get; set; }
        /// <summary>FullTime / PartTime / Casual；员工资料没填时为 null。</summary>
        public string? EmploymentType { get; set; }
        public bool IsManager { get; set; }
        public bool IsStoreManager { get; set; }
        /// <summary>未满 21 岁时的周岁（期末那天），21 岁及以上为 null。</summary>
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
        /// <summary>
        /// 生日提醒：这次生日当天年龄 ≤ 21（含刚满 21 岁）→ 要核对按年龄计的时薪。
        /// IsMinor 仍表示「生日当天未满 21 岁」，只用于合规文案；满 21 岁那天 IsMinor 为 false，但 RateReview 为 true。
        /// </summary>
        public bool? RateReview { get; set; }
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
        /// <summary>现金金额，不含补贴：现金工时 × 现金时薪；「公假」「带薪假」两档若有手填金额，则用手填值取代该档的系统值。</summary>
        public decimal Amount { get; set; }
        /// <summary>「公假」档的现金金额明细（系统值 / 手填值 / 是否可改）。</summary>
        public AttendancePayrollCashBucketAmountDto PublicHoliday { get; set; } = new();
        /// <summary>「带薪假」档的现金金额明细。</summary>
        public AttendancePayrollCashBucketAmountDto Leave { get; set; } = new();
    }

    /// <summary>公假 / 带薪假某一档的现金金额：默认是系统算的，财务可以手填覆盖。</summary>
    public class AttendancePayrollCashBucketAmountDto
    {
        /// <summary>这一档现金工时（转账装不下、落到现金的部分）。</summary>
        public decimal Hours { get; set; }
        /// <summary>最终现金金额：已手填则为手填值，否则等于 SystemAmount。</summary>
        public decimal Amount { get; set; }
        /// <summary>系统算的现金金额（现金工时 × 现金时薪），不含手填。</summary>
        public decimal SystemAmount { get; set; }
        /// <summary>手填值是否已生效。</summary>
        public bool IsManual { get; set; }
        /// <summary>库里存着的手填值（即使因为这一档已没有现金工时而不生效也会带出，便于提示）；没有手填为 null。</summary>
        public decimal? ManualAmount { get; set; }
        /// <summary>这一档有没有现金工时：只有有现金工时的档位才允许手填（工时全在转账里的档位没有现金部分，避免重复付）。</summary>
        public bool Editable { get; set; }
        public string? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    public class AttendancePayrollPayRowDto
    {
        public string UserGuid { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        /// <summary>员工昵称；页面显示为「全名（昵称）」，没填为 null。</summary>
        public string? Nickname { get; set; }
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
        /// <summary>NO_RATE / MISSING_TRANSFER_RATE / MISSING_CASH_RATE / BELOW_MINIMUM_RATE / CASH_OVERRIDE_IGNORED（有手填现金金额但该档已没有现金工时，未生效）。</summary>
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
        /// <summary>
        /// 按哪一天取「适用的时薪」（工资周期起始日）；为空时按分店本地今天。
        /// 只影响每人取哪条记录与 NextEffectiveFrom，生日提醒仍按今天判断。
        /// </summary>
        public DateTime? AsOf { get; set; }
    }

    /// <summary>侧栏用：一批分店在某个工资周期是否已提交锁定。</summary>
    public class AttendancePayPeriodStatusQueryDto
    {
        /// <summary>逗号分隔的分店代码；没有权限的分店会被忽略，不报错。</summary>
        public string? StoreCodes { get; set; }
        public DateTime? PeriodStart { get; set; }
        /// <summary>1 或 2；为空按 1。</summary>
        public int? Weeks { get; set; }
    }

    public class AttendancePayPeriodStoreStatusDto
    {
        public string StoreCode { get; set; } = string.Empty;
        /// <summary>该店有已提交的期与所选周期重叠。</summary>
        public bool Locked { get; set; }
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
        /// <summary>
        /// 生日已过（60 天内）、生日当天年龄 ≤ 21（含刚满 21 岁转成人档那天），而当前时薪记录生效早于这次生日：提示核对时薪。
        /// 为 true 时 BirthdayReviewDate / BirthdayReviewAge 一定有值。
        /// </summary>
        public bool BirthdayReviewHint { get; set; }
        /// <summary>需要核对时薪的那次生日（当年的生日日期）。</summary>
        public DateTime? BirthdayReviewDate { get; set; }
        /// <summary>那次生日当天满的岁数（刚满 21 岁时 AgeYears 已是 null，所以要单独给）。</summary>
        public int? BirthdayReviewAge { get; set; }
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
        /// <summary>列表接口：AsOf（或今天）之后最近一条已录入、还没生效的时薪记录的生效日；没有为空。</summary>
        public DateTime? NextEffectiveFrom { get; set; }
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

    public class UpsertAttendancePayPeriodCashOverrideDto : AttendancePayPeriodKeyDto
    {
        public string UserGuid { get; set; } = string.Empty;
        /// <summary>只能是 PublicHoliday / Leave。</summary>
        public string Bucket { get; set; } = string.Empty;
        /// <summary>手填的现金金额（≥ 0，最多两位小数）；null＝清除手填、恢复系统值。</summary>
        public decimal? Amount { get; set; }
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

    public class AttendancePayPeriodCashOverrideDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public DateTime PeriodStart { get; set; }
        public int Weeks { get; set; }
        public string UserGuid { get; set; } = string.Empty;
        public string Bucket { get; set; } = string.Empty;
        /// <summary>保存后的手填金额；已清除（恢复系统值）时为 null。</summary>
        public decimal? Amount { get; set; }
        /// <summary>保存时系统算的这一档现金金额。</summary>
        public decimal SystemAmount { get; set; }
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
