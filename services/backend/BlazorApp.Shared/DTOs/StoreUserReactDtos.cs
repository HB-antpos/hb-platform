using System.ComponentModel.DataAnnotations;

namespace BlazorApp.Shared.DTOs
{
    public class StoreUserGridRequestDto
    {
        public string? StoreCode { get; set; }

        public string? Keyword { get; set; }

        public int Status { get; set; } = -1;
    }

    public class StoreUserListDto
    {
        public string UserGuid { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string? FullName { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public int Status { get; set; }
        public string StoreGuid { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
        public string StoreName { get; set; } = string.Empty;
        public List<string> RoleNames { get; set; } = new();
        public DateTime? Birthday { get; set; }
        public string? Gender { get; set; }
        public string? EmploymentType { get; set; }
        public DateTime? LastLoginTime { get; set; }
        public string? LastLoginIp { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        /// <summary>员工还未把店长给的初始/重置密码改成自己的密码。</summary>
        public bool MustChangePassword { get; set; }
    }

    public class StoreUserDetailDto : StoreUserListDto
    {
        public string? AvatarUrl { get; set; }
        public string? IdentityId { get; set; }
        public string? Address { get; set; }
        public string? BankBsb { get; set; }
        public string? BankAccountNumber { get; set; }
        public string? SuperannuationCompanyName { get; set; }
        public string? SuperannuationCompanyCode { get; set; }
        public string? SuperannuationAccountNumber { get; set; }
        public string? Remarks { get; set; }
        /// <summary>仅新建时返回：设置密码邮件已发送（打码邮箱与有效期）。</summary>
        public PasswordSetupEmailResultDto? PasswordSetupEmail { get; set; }
        /// <summary>仅新建时返回：账号已建好但设置密码邮件没发出去的原因，店长可稍后重发。</summary>
        public string? PasswordSetupEmailError { get; set; }
    }

    public class CreateStoreUserDto
    {
        [Required(ErrorMessage = "用户名不能为空")]
        [StringLength(50, MinimumLength = 3, ErrorMessage = "用户名长度必须在3-50个字符之间")]
        public string Username { get; set; } = string.Empty;

        [EmailAddress(ErrorMessage = "邮箱格式不正确")]
        public string? Email { get; set; }

        /// <summary>
        /// 初始密码。SendPasswordSetupEmail=true 时可不传，由员工通过邮件验证码自己设置；
        /// 旧客户端仍按原方式提交初始密码。
        /// </summary>
        [StringLength(100, MinimumLength = 6, ErrorMessage = "密码长度必须在6-100个字符之间")]
        public string? Password { get; set; }

        /// <summary>
        /// 密码格式：raw 表示 HTTPS 原始密码；clientSha256 表示旧客户端 SHA256。
        /// </summary>
        public string PasswordFormat { get; set; } = string.Empty;

        [StringLength(100, ErrorMessage = "姓名长度不能超过100个字符")]
        public string? FullName { get; set; }

        public string? Phone { get; set; }

        [Required(ErrorMessage = "分店代码不能为空")]
        public string StoreCode { get; set; } = string.Empty;

        public int Status { get; set; } = 1;

        public List<string>? RoleNames { get; set; }

        public string? EmploymentType { get; set; }

        /// <summary>员工首次登录须先改密；未传时默认要求（旧客户端建号同样生效）。</summary>
        public bool? RequirePasswordChange { get; set; }

        /// <summary>创建后给员工邮箱发设置密码验证码，店长不再经手密码；此时邮箱必填。</summary>
        public bool? SendPasswordSetupEmail { get; set; }
    }

    public class SendStoreUserPasswordSetupEmailDto
    {
        [Required(ErrorMessage = "分店代码不能为空")]
        public string StoreCode { get; set; } = string.Empty;

        /// <summary>可选：老账号没有可用邮箱时，店长在此填写员工邮箱，先保存再发送。</summary>
        [StringLength(254)]
        public string? Email { get; set; }
    }

    public class UpdateStoreUserDto
    {
        [StringLength(50, MinimumLength = 3, ErrorMessage = "用户名长度必须在3-50个字符之间")]
        public string? Username { get; set; }

        [EmailAddress(ErrorMessage = "邮箱格式不正确")]
        public string? Email { get; set; }

        [StringLength(100, ErrorMessage = "姓名长度不能超过100个字符")]
        public string? FullName { get; set; }

        public string? Phone { get; set; }

        [Required(ErrorMessage = "分店代码不能为空")]
        public string StoreCode { get; set; } = string.Empty;

        public int Status { get; set; } = 1;

        public List<string>? RoleNames { get; set; }
    }

    public class UpdateStoreUserStatusDto
    {
        [Required(ErrorMessage = "分店代码不能为空")]
        public string StoreCode { get; set; } = string.Empty;

        public int Status { get; set; }
    }

    public class UpdateStoreUserPasswordDto
    {
        [Required(ErrorMessage = "分店代码不能为空")]
        public string StoreCode { get; set; } = string.Empty;

        [Required(ErrorMessage = "新密码不能为空")]
        [StringLength(100, MinimumLength = 6, ErrorMessage = "密码长度必须在6-100个字符之间")]
        public string NewPassword { get; set; } = string.Empty;

        /// <summary>
        /// 新密码格式：raw 表示 HTTPS 原始密码；clientSha256 表示旧客户端 SHA256。
        /// </summary>
        public string PasswordFormat { get; set; } = string.Empty;

        /// <summary>重置后员工须先改密；未传时默认要求。</summary>
        public bool? RequirePasswordChange { get; set; }
    }
}
