namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 店长 / Admin 在工时统计里为「忘记申请」的员工补录带薪假（预览与保存共用）。
    /// - PaidSick：只选员工「有排班、没打卡」的日期（Dates），工时以当天排班为准（排班工时扣餐）；
    /// - PaidAnnual：按 StartDate～EndDate，合计小时数 = TotalHours（不填则默认 区间内工作日（排除周末与公共假期）× 8 h），
    ///   合计平均摊到区间内每个工作日；RestsOnLeaveDays=false 表示员工当天照常上班（不标不能上班、不查打卡），年假另外加算。
    /// 年假发放（AnnualCashOut）已取消，不再接收新的补录。
    /// 补录是事后记录：直接生效（不走审批），不受「提前申请 / 生病后 N 天内提交」限制，医嘱可以不传，但原因必填。
    /// </summary>
    public class CreateAttendanceManualLeaveDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string UserGuid { get; set; } = string.Empty;
        /// <summary>PaidSick / PaidAnnual。</summary>
        public string LeaveType { get; set; } = string.Empty;
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        /// <summary>带薪年假的合计小时数（整段区间的总数，不是每天）；为空 = 区间内工作日 × 8 h。病假不用。</summary>
        public decimal? TotalHours { get; set; }
        /// <summary>带薪年假当天员工是否休息；为空按休息（与历史一致）。false = 照常上班、年假另外加算。病假不用。</summary>
        public bool? RestsOnLeaveDays { get; set; }
        /// <summary>带薪病假要补录的日期（每天必须有本店已发布、没有打卡的排班）；年假类型不用，改用 StartDate～EndDate。</summary>
        public List<DateTime>? Dates { get; set; }
        public string Reason { get; set; } = string.Empty;
        /// <summary>预览与保存之间的服务端状态版本；保存必须带回预览返回的值。</summary>
        public string? PreviewRevision { get; set; }
    }

    /// <summary>预览里每一天的带薪工时（年假为合计摊出来的每天小时数；周末 / 公共假期为 0，页面上灰显）。</summary>
    public class AttendanceManualLeaveDayDto
    {
        public DateTime Date { get; set; }
        public decimal Hours { get; set; }
    }

    public class AttendanceManualLeavePreviewDto
    {
        public string PreviewRevision { get; set; } = string.Empty;
        public string LeaveType { get; set; } = string.Empty;
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public decimal PaidHours { get; set; }
        /// <summary>带薪年假的默认合计 = 区间内工作日 × 8 h，页面据此预填合计小时数；病假为 0。</summary>
        public decimal DefaultPaidHours { get; set; }
        /// <summary>带薪年假当天是否休息（预览回显）；病假恒为 true。</summary>
        public bool RestsOnLeaveDays { get; set; } = true;
        public List<AttendanceManualLeaveDayDto> Days { get; set; } = new();
        /// <summary>病假没有医嘱：保存后工时表该格会标「无医嘱」，便于事后核对。</summary>
        public bool NoCertificate { get; set; }
    }

    public class AttendanceManualLeaveResultDto
    {
        public string LeaveGuid { get; set; } = string.Empty;
        public decimal PaidHours { get; set; }
    }

    /// <summary>撤销一条补录的假（只能撤销补录建的；员工自己申请、审批通过的假走审批页）。</summary>
    public class RevokeAttendanceManualLeaveDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string LeaveGuid { get; set; } = string.Empty;
    }
}
