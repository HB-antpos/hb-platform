namespace BlazorApp.Shared.DTOs
{
    /// <summary>补录的一个班段：门店本地时间，必须在所选工作日当天。</summary>
    public class AttendanceManualEntrySegmentDto
    {
        public DateTime StartLocal { get; set; }
        public DateTime EndLocal { get; set; }
    }

    /// <summary>
    /// 店长 / Admin 为「当天没有任何打卡」的员工补录整天工时（预览与保存共用）。
    /// 当天没有排班时，保存会同时新建一条当天排班（备注「补录：原因」）；已有排班则直接补打卡。
    /// </summary>
    public class CreateAttendanceManualEntryDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string UserGuid { get; set; } = string.Empty;
        public DateTime WorkDate { get; set; }
        public List<AttendanceManualEntrySegmentDto> Segments { get; set; } = new();
        /// <summary>用餐次数覆盖值（0–3），null 表示按时长自动；只在需要新建排班时生效，已有排班沿用排班自己的设置。</summary>
        public int? MealBreakCount { get; set; }
        public string Reason { get; set; } = string.Empty;
        /// <summary>预览与保存之间的服务端状态版本；保存必须带回预览返回的值。</summary>
        public string? PreviewRevision { get; set; }
    }

    public class AttendanceManualEntryPreviewDto
    {
        public string PreviewRevision { get; set; } = string.Empty;
        /// <summary>true = 当天没有排班，保存时会同时新建一条。</summary>
        public bool WillCreateSchedule { get; set; }
        public string? ScheduleGuid { get; set; }
        /// <summary>排班窗口（HH:mm）；新建时取补录班段的最早上班与最晚下班。</summary>
        public string ScheduleStart { get; set; } = string.Empty;
        public string ScheduleEnd { get; set; } = string.Empty;
        /// <summary>实际生效的用餐次数（排班覆盖值或按时长自动）。</summary>
        public int EffectiveMealBreakCount { get; set; }
        /// <summary>补录班段合计的分钟数（未扣餐、未按排班窗口截取）。</summary>
        public int WorkedMinutes { get; set; }
        /// <summary>计薪分钟数：已闭合班段与排班窗口的重叠 − 扣餐；超出窗口的部分不在其中。</summary>
        public int PaidMinutes { get; set; }
        public decimal PaidHours { get; set; }
        /// <summary>超出排班窗口的加班候选分钟数；保存后进「待审批」，批准前不计薪。</summary>
        public int CandidateOvertimeMinutes { get; set; }
    }

    public class AttendanceManualEntryResultDto
    {
        public string ScheduleGuid { get; set; } = string.Empty;
        public bool CreatedSchedule { get; set; }
        public int PunchCount { get; set; }
        public decimal PaidHours { get; set; }
        public int CandidateOvertimeMinutes { get; set; }
    }

    /// <summary>撤销某员工某天的补录：删除补录打卡，并取消由补录新建的排班。</summary>
    public class RevokeAttendanceManualEntryDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string UserGuid { get; set; } = string.Empty;
        public DateTime WorkDate { get; set; }
    }

    /// <summary>工时统计格子上「补」角标的留痕信息。</summary>
    public class AttendancePayrollDayManualEntryDto
    {
        public string? Reason { get; set; }
        /// <summary>补录人账号名（打卡记录的 CreatedBy）。</summary>
        public string? CreatedBy { get; set; }
        public DateTime? CreatedAt { get; set; }
        /// <summary>这次补录同时新建了当天排班（撤销时会一并取消）。</summary>
        public bool CreatedSchedule { get; set; }
    }
}
