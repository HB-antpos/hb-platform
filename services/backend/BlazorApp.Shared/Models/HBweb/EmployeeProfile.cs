using SqlSugar;

namespace BlazorApp.Shared.Models
{
    [SugarTable("EmployeeProfile")]
    public class EmployeeProfile : BaseEntity
    {
        [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
        public int EmployeeInfoId { get; set; }

        [SugarColumn(
            IsNullable = false,
            Length = 50,
            UniqueGroupNameList = new[] { "idx_employee_profile_user_guid" }
        )]
        public string UserGUID { get; set; } = string.Empty;

        [SugarColumn(IsNullable = true, Length = 20)]
        public string? BankBSB { get; set; }

        [SugarColumn(IsNullable = true, Length = 50)]
        public string? BankACC { get; set; }

        [SugarColumn(IsNullable = true, Length = 200)]
        public string? SuperannuationCompanyName { get; set; }

        [SugarColumn(IsNullable = true, Length = 100)]
        public string? SuperannuationCompanyCode { get; set; }

        [SugarColumn(IsNullable = true, Length = 100)]
        public string? SuperannuationAccount { get; set; }

        [SugarColumn(IsNullable = true, Length = 50)]
        public string? Phone { get; set; }

        [SugarColumn(IsNullable = true)]
        public DateTime? Birthday { get; set; }

        /// <summary>入职日期（在本雇主处开始工作的日期，只有日期）。空 = 还没确认；零售 Award 青少年「满 6 个月」按它算。</summary>
        [SugarColumn(IsNullable = true, ColumnDataType = "date")]
        public DateTime? HireDate { get; set; }

        [SugarColumn(IsNullable = true)]
        public EmployeeGender? Gender { get; set; }

        [SugarColumn(IsNullable = true)]
        public EmployeeType? EmployeeType { get; set; }

        [SugarColumn(IsNullable = true, Length = 500)]
        public string? AvatarUrl { get; set; }

        [SugarColumn(IsNullable = true, Length = 100)]
        public string? IdentityId { get; set; }

        [SugarColumn(IsNullable = true, Length = 50)]
        public string? IdentityType { get; set; }

        [SugarColumn(IsNullable = true, Length = 500)]
        public string? IdentityPhotoUrl { get; set; }

        [SugarColumn(IsNullable = true, Length = 500)]
        public string? IdentityPhotoObjectKey { get; set; }

        [SugarColumn(IsNullable = true, Length = 500)]
        public string? Address { get; set; }

        /// <summary>正式敏感资料版本；仅正式敏感字段变化时递增。</summary>
        public int SensitiveRevision { get; set; }

        [Navigate(NavigateType.OneToOne, nameof(UserGUID), nameof(User.UserGUID))]
        public User? User { get; set; }
    }

    public enum EmployeeGender
    {
        Unknown = 0,
        Male = 1,
        Female = 2,
        Other = 3,
    }

    public enum EmployeeType
    {
        FullTime = 1,
        PartTime = 2,
        Temporary = 3,
    }
}
