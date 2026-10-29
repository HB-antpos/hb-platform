using System.ComponentModel.DataAnnotations;

namespace BlazorApp.Shared.DTOs
{
    /// <summary>员工个人信息维护（/managed）的筛选下拉：只含调用者管理范围内的分店与可见角色。</summary>
    public sealed class EmployeeProfileManagedFilterOptionsDto
    {
        public List<EmployeeProfileFilterStoreDto> Stores { get; set; } = new();
        public List<EmployeeProfileFilterRoleDto> Roles { get; set; } = new();
    }

    public sealed class EmployeeProfileFilterStoreDto
    {
        public string StoreGuid { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
        public string StoreName { get; set; } = string.Empty;
    }

    public sealed class EmployeeProfileFilterRoleDto
    {
        public string RoleGuid { get; set; } = string.Empty;
        public string RoleName { get; set; } = string.Empty;
    }

    /// <summary>
    /// /managed 详情：非敏感资料明文，敏感资料（银行 / 公积金 / 证件号）只返回脱敏摘要，
    /// 完整敏感值仍只走原 admin 详情接口（需管理员角色 + EmployeeProfiles.View）。
    /// </summary>
    public sealed class EmployeeProfileManagedDetailDto
    {
        public int? EmployeeInfoId { get; set; }
        public string UserGUID { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? FullName { get; set; }
        public bool HasProfile { get; set; }
        public List<string> StoreNames { get; set; } = new();
        public List<string> RoleNames { get; set; } = new();

        public string? Phone { get; set; }
        public string? Gender { get; set; }
        public string? EmploymentType { get; set; }
        public string? Address { get; set; }
        public string? AvatarUrl { get; set; }

        // 敏感资料脱敏摘要：只用于展示与「待补全」判断，不可回写。
        public string? BankBsb { get; set; }
        public string? BankAccountNumber { get; set; }
        public string? SuperannuationCompanyName { get; set; }
        public string? SuperannuationCompanyCode { get; set; }
        public string? SuperannuationAccountNumber { get; set; }
        public string? IdentityType { get; set; }
        public string? IdentityId { get; set; }
        public bool HasBirthday { get; set; }

        /// <summary>调用者是否持有「修改职位类型」权限；没有时前端把员工类型置灰。</summary>
        public bool CanEditEmploymentType { get; set; }

        public DateTime? UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }
    }

    /// <summary>
    /// /managed 保存：只含非敏感白名单字段，结构上就不可能带敏感资料。
    /// 每个字段用 Has* 区分「未传」与「显式清空」：未传的字段不会被改动，避免旧页面误清数据。
    /// </summary>
    public sealed class EmployeeProfileManagedUpsertDto
    {
        private string? _phone;
        private string? _gender;
        private string? _employmentType;
        private string? _address;

        [StringLength(50)]
        public string? Phone
        {
            get => _phone;
            set { _phone = value; HasPhone = true; }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasPhone { get; private set; }

        public string? Gender
        {
            get => _gender;
            set { _gender = value; HasGender = true; }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasGender { get; private set; }

        public string? EmploymentType
        {
            get => _employmentType;
            set { _employmentType = value; HasEmploymentType = true; }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasEmploymentType { get; private set; }

        [StringLength(500)]
        public string? Address
        {
            get => _address;
            set { _address = value; HasAddress = true; }
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasAddress { get; private set; }
    }
}
