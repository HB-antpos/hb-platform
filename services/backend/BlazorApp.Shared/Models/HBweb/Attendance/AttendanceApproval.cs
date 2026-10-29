using SqlSugar;

namespace BlazorApp.Shared.Models
{
    [SugarTable("AttendanceApproval")]
    public class AttendanceApproval : BaseEntity
    {
        [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
        public int Id { get; set; }

        [SugarColumn(IsNullable = false, Length = 50)]
        public string ApprovalGuid { get; set; } = Guid.NewGuid().ToString();

        [SugarColumn(IsNullable = false, Length = 30)]
        public string SourceType { get; set; } = "Punch";

        [SugarColumn(IsNullable = false, Length = 50)]
        public string SourceGuid { get; set; } = string.Empty;

        [SugarColumn(IsNullable = false, Length = 50)]
        public string StoreCode { get; set; } = string.Empty;

        [SugarColumn(IsNullable = false, Length = 50)]
        public string ApplicantUserGuid { get; set; } = string.Empty;

        [SugarColumn(IsNullable = true, Length = 50)]
        public string? ReviewerUserGuid { get; set; }

        [SugarColumn(IsNullable = false, Length = 30)]
        public string ReviewStatus { get; set; } = "Pending";

        [SugarColumn(IsNullable = true, Length = 500)]
        public string? ReviewRemark { get; set; }

        [SugarColumn(IsNullable = true)]
        public DateTime? ReviewedAt { get; set; }

        [SugarColumn(IsNullable = true)]
        public int? CandidateOvertimeMinutes { get; set; }

        [SugarColumn(IsNullable = true)]
        public int? ApprovedOvertimeMinutes { get; set; }

        /// <summary>
        /// 本人审批：持 Attendance.Approval.ReviewSelf 的审核人处理了自己的申请。列由迁移 20261026.001 添加。
        /// </summary>
        [SugarColumn(IsNullable = false)]
        public bool IsSelfReview { get; set; }
    }
}
