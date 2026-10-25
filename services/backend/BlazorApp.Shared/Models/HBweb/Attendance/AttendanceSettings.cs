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
    }
}
