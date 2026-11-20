namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 员工端「整店整周排班」：员工查看自己关联分店的已发布排班（只读）。
    /// 这里刻意只放「谁、哪天、几点到几点」：同事的计薪工时、请假类型、备注、合规提醒、打卡状态一律不下发，
    /// 从源头剥掉敏感字段，而不是靠前端不显示。
    /// </summary>
    public class AttendanceStoreWeekDto
    {
        /// <summary>该周周一（yyyy-MM-dd）。</summary>
        public DateTime WeekStartDate { get; set; }

        /// <summary>员工关联的分店（按店名排序）；传了 storeCode 时只有那一家。没有排班的分店也会列出，供前端做分店切换。</summary>
        public List<AttendanceStoreWeekStoreDto> Stores { get; set; } = new();
    }

    public class AttendanceStoreWeekStoreDto
    {
        public string StoreCode { get; set; } = string.Empty;
        public string? StoreName { get; set; }

        /// <summary>该分店这一周是否已有已发布（Active）的班；false 时前端显示「店长还没发布」。</summary>
        public bool Published { get; set; }

        /// <summary>已发布的班次，按日期、开始时间、姓名排好序。</summary>
        public List<AttendanceStoreWeekShiftDto> Shifts { get; set; } = new();
    }

    public class AttendanceStoreWeekShiftDto
    {
        public string UserGuid { get; set; } = string.Empty;

        /// <summary>短名：昵称 → 名 → 全名 → 登录名（与店长排班看板同一顺序）。</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>全名：前端遇到两位同事短名相同时，用它区分。</summary>
        public string? FullName { get; set; }

        public DateTime WorkDate { get; set; }
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }

        /// <summary>是不是当前登录员工自己的班。</summary>
        public bool IsMine { get; set; }
    }
}
