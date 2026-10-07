namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 下班打卡时员工对「缺休息」的声明。旧版 App 不带此字段，服务端按「已休息」处理，不生成审批。
    /// </summary>
    public class AttendanceMealDeclarationDto
    {
        /// <summary>声明没休息的次数；0＝缺的几次都休息了。服务端以自己重算的缺口为上限截断。</summary>
        public int NotTakenCount { get; set; }

        public string? Reason { get; set; }
    }

    /// <summary>开始/结束休息的请求。</summary>
    public class AttendanceMealBreakRequestDto
    {
        public string StoreCode { get; set; } = string.Empty;
    }

    public class AttendanceMealBreakDto
    {
        public string BreakGuid { get; set; } = string.Empty;
        public DateTime StartUtc { get; set; }
        public DateTime? EndUtc { get; set; }
    }

    /// <summary>某个排班此刻的用餐状态：移动端据此显示休息横幅、休息计时和下班前的确认。</summary>
    public class AttendanceMealStateDto
    {
        public string ScheduleGuid { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;

        /// <summary>排班的有效用餐次数（店长指定值，否则按时长默认）；0 表示不提醒、不检查。</summary>
        public int EffectiveMealBreakCount { get; set; }

        /// <summary>已有的休息：满 10 分钟的休息记录 + 班段间满 30 分钟的空档 + 此前下班声明已处理的次数。</summary>
        public int HandledCount { get; set; }

        /// <summary>按已工作时长此刻应有的用餐次数（累计满 4 小时 × k 才要求第 k 次，不超过有效次数）。</summary>
        public int RequiredCount { get; set; }

        /// <summary>此刻下班会被判为最后一次下班（班段数到上限或已到排班结束）；班段中间的下班不检查用餐。</summary>
        public bool ClockOutWouldBeFinal { get; set; }

        /// <summary>此刻下班会缺几次休息；仅最后一次下班才非 0，&gt; 0 时新版 App 在扫码前先问员工是否休息。</summary>
        public int MissingCountIfClockOutNow { get; set; }

        public bool HasOpenBreak { get; set; }
        public DateTime? OpenBreakStartedAtUtc { get; set; }

        /// <summary>下一次用餐提醒时间（连续工作满 4 小时）；null 表示无需提醒（已休息够、休息中、未上班或不用餐）。</summary>
        public DateTime? NextReminderAtUtc { get; set; }

        public DateTime ServerTimeUtc { get; set; }
        public List<AttendanceMealBreakDto> Breaks { get; set; } = new();
    }

    /// <summary>下班打卡产生的用餐声明；声明没休息时同时有一条待审的 MealBreak 审批。</summary>
    public class AttendanceMealClaimDto
    {
        public string ClaimGuid { get; set; } = string.Empty;
        public string ScheduleGuid { get; set; } = string.Empty;
        public DateTime WorkDate { get; set; }
        public int ExpectedCount { get; set; }
        public int RecordedCount { get; set; }
        public int MissingCount { get; set; }
        public int NotTakenCount { get; set; }
        public int ClaimedMinutes { get; set; }
        public int? ApprovedMinutes { get; set; }

        /// <summary>None / Pending / Approved / Rejected / Cancelled。</summary>
        public string Status { get; set; } = "None";

        public string? Reason { get; set; }
    }
}
