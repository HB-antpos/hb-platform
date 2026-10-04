using System.ComponentModel.DataAnnotations;
using BlazorApp.Shared.Models;

namespace BlazorApp.Shared.DTOs
{
    public class EmployeeProfileQueryDto
    {
        public int Page { get; set; } = 1;

        public int PageSize { get; set; } = 10;

        public string? Search { get; set; }

        public string? EmployeeType { get; set; }

        public bool? HasProfile { get; set; }
    }

    public class EmployeeProfileListItemDto
    {
        public int? EmployeeInfoId { get; set; }
        public string UserGUID { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? FullName { get; set; }
        public bool HasProfile { get; set; }
        public string? Phone { get; set; }
        public string? BankBsb { get; set; }
        public string? BankAccountNumber { get; set; }
        public string? SuperannuationCompanyName { get; set; }
        public string? SuperannuationCompanyCode { get; set; }
        public string? SuperannuationAccountNumber { get; set; }
        public string? Gender { get; set; }
        public string? EmployeeType { get; set; }
        public DateTime? Birthday { get; set; }
        public string? AvatarUrl { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    public class EmployeeProfileDetailDto
    {
        public int? EmployeeInfoId { get; set; }
        public string UserGUID { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? FullName { get; set; }
        public string? Phone { get; set; }
        public string? BankBsb { get; set; }
        public string? BankAccountNumber { get; set; }
        public string? SuperannuationCompanyName { get; set; }
        public string? SuperannuationCompanyCode { get; set; }
        public string? SuperannuationAccountNumber { get; set; }
        public DateTime? Birthday { get; set; }
        public string? Gender { get; set; }
        public string? EmploymentType { get; set; }
        public string? AvatarUrl { get; set; }
        public string? IdentityId { get; set; }
        public string? IdentityType { get; set; }
        public string? IdentityPhotoUrl { get; set; }
        public DateTime? IdentityPhotoUrlExpiresAt { get; set; }
        public string? Address { get; set; }
        public DateTime? CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }
        public int SensitiveRevision { get; set; }
    }

    public class EmployeeProfileUpsertDto
    {
        private string? _email;

        // setter 用于区分旧客户端省略 Email 与显式提交 Email:null。
        [StringLength(254)]
        [EmailAddress]
        public string? Email
        {
            get => _email;
            set
            {
                _email = value;
                HasEmail = true;
            }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasEmail { get; private set; }

        public string? UserGUID { get; set; }

        [StringLength(50)]
        public string? Phone { get; set; }

        [StringLength(20)]
        public string? BankBsb { get; set; }

        [StringLength(50)]
        public string? BankAccountNumber { get; set; }

        [StringLength(200)]
        public string? SuperannuationCompanyName { get; set; }

        [StringLength(100)]
        public string? SuperannuationCompanyCode { get; set; }

        [StringLength(100)]
        public string? SuperannuationAccountNumber { get; set; }

        private DateTime? _birthday;
        /// <summary>
        /// 管理员保存沿用「未传即清空」语义；员工自助保存只在显式传入时校验，生日修改须走敏感资料审核。
        /// </summary>
        public DateTime? Birthday
        {
            get => _birthday;
            set
            {
                _birthday = value;
                HasBirthday = true;
            }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasBirthday { get; private set; }

        public string? Gender { get; set; }

        private string? _employmentType;
        public string? EmploymentType
        {
            get => _employmentType;
            set
            {
                _employmentType = value;
                HasEmploymentType = true;
            }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasEmploymentType { get; private set; }

        [StringLength(500)]
        public string? AvatarUrl { get; set; }

        [StringLength(100)]
        public string? IdentityId { get; set; }

        [StringLength(50)]
        public string? IdentityType { get; set; }

        [StringLength(500)]
        public string? IdentityPhotoUrl { get; set; }

        [StringLength(500)]
        public string? Address { get; set; }

        /// <summary>管理员确认本次敏感资料直改可以原子作废现有待审申请。</summary>
        public bool? ConfirmSupersedePendingSensitiveChangeRequest { get; set; }

        [Range(0, int.MaxValue)]
        public int? ExpectedSensitiveRevision { get; set; }
    }

    public sealed class EmployeeImageUploadSignatureRequest
    {
        [Required]
        public string Kind { get; set; } = string.Empty;
        [Required]
        public string FileName { get; set; } = string.Empty;
        [Required]
        public string ContentType { get; set; } = string.Empty;
        [Range(1, 5 * 1024 * 1024)]
        public long FileSize { get; set; }
    }

    public sealed class EmployeeImageCompleteRequest
    {
        [Required]
        public string Kind { get; set; } = string.Empty;
        [Required]
        public string ObjectKey { get; set; } = string.Empty;
    }

    public sealed class EmployeeCashierBarcodeDto
    {
        public bool Exists { get; set; }
        public string? Barcode { get; set; }
        public string Format { get; set; } = "EAN-13";
        public int PrintCount { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    public sealed class AdminCashierBarcodeRefreshRequest
    {
        private string? _expectedBarcode;

        // setter 用于区分 JSON 中显式传 null 与缺少字段，避免陈旧页面无条件刷新。
        public string? ExpectedBarcode
        {
            get => _expectedBarcode;
            set
            {
                _expectedBarcode = value;
                HasExpectedBarcode = true;
            }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasExpectedBarcode { get; private set; }
    }

    public sealed class EmployeeProfileSensitiveChangeUpsertDto
    {
        [StringLength(20)]
        public string? BankBsb { get; set; }
        [StringLength(50)]
        public string? BankAccountNumber { get; set; }
        [StringLength(200)]
        public string? SuperannuationCompanyName { get; set; }
        [StringLength(100)]
        public string? SuperannuationCompanyCode { get; set; }
        [StringLength(100)]
        public string? SuperannuationAccountNumber { get; set; }
        [StringLength(50)]
        public string? IdentityType { get; set; }
        [StringLength(100)]
        public string? IdentityId { get; set; }

        private DateTime? _birthday;
        /// <summary>生日属于敏感资料；未传入时沿用基线值，旧版客户端提交不会清空生日。</summary>
        public DateTime? Birthday
        {
            get => _birthday;
            set
            {
                _birthday = value;
                HasBirthday = true;
            }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasBirthday { get; private set; }

        [Range(0, int.MaxValue)]
        public int? ExpectedSensitiveRevision { get; set; }
    }

    public sealed class EmployeeProfileSensitiveReviewDto
    {
        [StringLength(1000)]
        public string? Reason { get; set; }
    }

    public sealed class EmployeeProfileSensitiveRejectDto
    {
        [Required]
        [StringLength(1000, MinimumLength = 1)]
        public string Reason { get; set; } = string.Empty;
    }

    public sealed class EmployeeProfileSensitiveChangeQueryDto
    {
        public int Page { get; set; } = 1;
        [Range(1, 100)]
        public int PageSize { get; set; } = 20;
        /// <summary>状态筛选：枚举名（Pending/Approved/…）或 Processed（除 Pending 外的全部终态）。</summary>
        public string? Status { get; set; }
        public string? Search { get; set; }
        /// <summary>按员工精确过滤；仍叠加审核范围，超范围员工返回空列表。</summary>
        [StringLength(50)]
        public string? UserGuid { get; set; }
    }

    public sealed class EmployeeProfileSensitiveWithdrawDto
    {
        /// <summary>可选：员工页面上看到的申请编号；与当前待审申请不一致时拒绝撤回，避免误撤另一台设备的新申请。</summary>
        [Range(1, int.MaxValue)]
        public int? RequestId { get; set; }
    }

    /// <summary>员工本人申请历史条目：只含状态与字段标识，不含任何敏感值。</summary>
    public sealed class EmployeeProfileSensitiveChangeHistoryItemDto
    {
        public int RequestId { get; set; }
        public string Status { get; set; } = string.Empty;
        public List<string> ChangedFields { get; set; } = new();
        public DateTime SubmittedAt { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public string? ReviewReason { get; set; }
    }

    public class EmployeeProfileSensitiveChangeSummaryDto
    {
        public int RequestId { get; set; }
        public string UserGuid { get; set; } = string.Empty;
        public string? Username { get; set; }
        public string Status { get; set; } = string.Empty;
        public int BaseSensitiveRevision { get; set; }
        public DateTime SubmittedAt { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public List<string> ChangedFields { get; set; } = new();
        public List<string> StoreCodes { get; set; } = new();
        public List<string> StoreNames { get; set; } = new();
    }

    public sealed class EmployeeProfileSensitiveSnapshotDto
    {
        public string? BankBsb { get; set; }
        public string? BankAccountNumber { get; set; }
        public string? SuperannuationCompanyName { get; set; }
        public string? SuperannuationCompanyCode { get; set; }
        public string? SuperannuationAccountNumber { get; set; }
        public string? IdentityType { get; set; }
        public string? IdentityId { get; set; }
        public DateTime? Birthday { get; set; }
        public bool HasIdentityPhoto { get; set; }
        public string? IdentityPhotoUrl { get; set; }
    }

    public sealed class EmployeeProfileSensitiveChangeDetailDto
        : EmployeeProfileSensitiveChangeSummaryDto
    {
        public string? BankBsb { get; set; }
        public string? BankAccountNumber { get; set; }
        public string? SuperannuationCompanyName { get; set; }
        public string? SuperannuationCompanyCode { get; set; }
        public string? SuperannuationAccountNumber { get; set; }
        public string? IdentityType { get; set; }
        public string? IdentityId { get; set; }
        public DateTime? Birthday { get; set; }
        public bool HasIdentityPhoto { get; set; }
        public string? IdentityPhotoUrl { get; set; }
        public DateTime? IdentityPhotoUrlExpiresAt { get; set; }
        public string? SubmittedBy { get; set; }
        public string? ReviewedBy { get; set; }
        public string? ReviewReason { get; set; }
        public EmployeeProfileSensitiveSnapshotDto? CurrentSnapshot { get; set; }
    }

    public sealed class EmployeeCashierBarcodePrintConfirmationRequest
    {
        [Required]
        [StringLength(13, MinimumLength = 13)]
        public string Barcode { get; set; } = string.Empty;
        [Required]
        public Guid PrintAttemptId { get; set; }
    }

    public sealed class StoreUserCashierBarcodeEnsureRequest
    {
        [Required]
        [StringLength(50, MinimumLength = 1)]
        public string StoreCode { get; set; } = string.Empty;
    }

    public sealed class StoreUserCashierBarcodePrintConfirmationRequest
    {
        [Required]
        [StringLength(50, MinimumLength = 1)]
        public string StoreCode { get; set; } = string.Empty;
        [Required]
        [StringLength(13, MinimumLength = 13)]
        public string Barcode { get; set; } = string.Empty;
        [Required]
        public Guid PrintAttemptId { get; set; }
    }
}
