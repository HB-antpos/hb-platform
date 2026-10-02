using SqlSugar;

namespace BlazorApp.Shared.Models
{
    [SugarTable("AttendanceSchedule")]
    public class AttendanceSchedule : BaseEntity
    {
        [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
        public int Id { get; set; }

        [SugarColumn(IsNullable = false, Length = 50)]
        public string ScheduleGuid { get; set; } = Guid.NewGuid().ToString();

        [SugarColumn(IsNullable = false, Length = 50)]
        public string StoreCode { get; set; } = string.Empty;

        [SugarColumn(IsNullable = false, Length = 50)]
        public string UserGuid { get; set; } = string.Empty;

        [SugarColumn(IsNullable = false)]
        public DateTime WorkDate { get; set; }

        [SugarColumn(IsNullable = false)]
        public TimeSpan StartTime { get; set; }

        [SugarColumn(IsNullable = false)]
        public TimeSpan EndTime { get; set; }

        [SugarColumn(IsNullable = false, Length = 30)]
        public string Status { get; set; } = "Draft";

        [SugarColumn(IsNullable = true, Length = 500)]
        public string? Remark { get; set; }

        /// <summary>
        /// 店长指定的用餐次数（每次 30 分钟）：null 表示按班次时长自动扣（超 4.5 小时 1 次、超 9 小时 2 次），
        /// 有值表示按指定次数扣，0 即取消用餐扣除。工时由移动端计算，后端只存取该覆盖值。
        /// </summary>
        [SugarColumn(IsNullable = true)]
        public int? MealBreakCount { get; set; }
    }
}
