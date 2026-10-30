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
        /// <summary>名（Given name）。</summary>
        public string? GivenName { get; set; }
        /// <summary>中间名（Middle name）。</summary>
        public string? MiddleName { get; set; }
        /// <summary>姓（Family name）。</summary>
        public string? FamilyName { get; set; }
        /// <summary>昵称（Nickname）。</summary>
        public string? Nickname { get; set; }
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
        /// <summary>是否允许该账号登录 Web / App；false 时账号仍可用于考勤、排班、收银条码等。老账号默认 true。</summary>
        public bool LoginEnabled { get; set; } = true;
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
        /// 初始密码。SendPasswordSetupEmail=true 时可不传，由系统生成初始密码并发到员工邮箱；
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

        /// <summary>名；与中间名、姓、昵称任一非空时按拆分字段写入，并由「名 / 中间名 / 姓」合成姓名。</summary>
        [StringLength(50, ErrorMessage = "名长度不能超过50个字符")]
        public string? GivenName { get; set; }

        /// <summary>中间名。</summary>
        [StringLength(50, ErrorMessage = "中间名长度不能超过50个字符")]
        public string? MiddleName { get; set; }

        /// <summary>姓。</summary>
        [StringLength(50, ErrorMessage = "姓长度不能超过50个字符")]
        public string? FamilyName { get; set; }

        /// <summary>昵称；不参与姓名合成。</summary>
        [StringLength(50, ErrorMessage = "昵称长度不能超过50个字符")]
        public string? Nickname { get; set; }

        public string? Phone { get; set; }

        /// <summary>生日。店长新增员工时必填（控制器校验），写入员工资料。</summary>
        public DateTime? Birthday { get; set; }

        /// <summary>监护人；生日未满 18 岁时必填，预填进未成年用工合规草稿。</summary>
        public NewStaffGuardianDto? Guardian { get; set; }

        /// <summary>
        /// 由「新建账号」控制器置为 true：服务在权限与门店范围检查之后，按新建员工规则校验生日、电话、监护人。
        /// 不参与 JSON 序列化，客户端无法关闭；批量导入等内部流程不设置，保持原行为。
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool RequireNewStaffProfile { get; set; }

        [Required(ErrorMessage = "分店代码不能为空")]
        public string StoreCode { get; set; } = string.Empty;

        public int Status { get; set; } = 1;

        public List<string>? RoleNames { get; set; }

        public string? EmploymentType { get; set; }

        /// <summary>员工首次登录须先改密；未传时默认要求（旧客户端建号同样生效）。</summary>
        public bool? RequirePasswordChange { get; set; }

        /// <summary>创建后由系统生成初始密码并发到员工邮箱，店长不经手密码；此时邮箱必填，员工首次登录须改密。</summary>
        public bool? SendPasswordSetupEmail { get; set; }

        /// <summary>
        /// 是否允许登录 Web / App。未传时按 true（旧客户端行为不变）；传 false 时只建账号与资料，
        /// 不要求密码、不发初始密码邮件、不要求改密，账号无法登录直到被开启。
        /// </summary>
        public bool? LoginEnabled { get; set; }
    }

    public class SendStoreUserPasswordSetupEmailDto
    {
        [Required(ErrorMessage = "分店代码不能为空")]
        public string StoreCode { get; set; } = string.Empty;

        /// <summary>可选：老账号没有可用邮箱时，店长在此填写员工邮箱，先保存再发送。</summary>
        [StringLength(254)]
        public string? Email { get; set; }

        /// <summary>
        /// true 表示重新生成初始密码并发到员工邮箱（新建员工页的「重新发送」）；
        /// 未传或 false 仍发送设置密码验证码。
        /// </summary>
        public bool? InitialPassword { get; set; }
    }

    public class UpdateStoreUserDto
    {
        [StringLength(50, MinimumLength = 3, ErrorMessage = "用户名长度必须在3-50个字符之间")]
        public string? Username { get; set; }

        [EmailAddress(ErrorMessage = "邮箱格式不正确")]
        public string? Email { get; set; }

        [StringLength(100, ErrorMessage = "姓名长度不能超过100个字符")]
        public string? FullName { get; set; }

        /// <summary>名；null 表示不修改（旧客户端不传即不动），空白表示清空，非空表示设置。名 / 中间名 / 姓任一非 null 时重算姓名。</summary>
        [StringLength(50, ErrorMessage = "名长度不能超过50个字符")]
        public string? GivenName { get; set; }

        /// <summary>中间名；语义同 GivenName。</summary>
        [StringLength(50, ErrorMessage = "中间名长度不能超过50个字符")]
        public string? MiddleName { get; set; }

        /// <summary>姓；语义同 GivenName。</summary>
        [StringLength(50, ErrorMessage = "姓长度不能超过50个字符")]
        public string? FamilyName { get; set; }

        /// <summary>昵称；null 表示不修改，空白表示清空；仅昵称变化不改动姓名。</summary>
        [StringLength(50, ErrorMessage = "昵称长度不能超过50个字符")]
        public string? Nickname { get; set; }

        public string? Phone { get; set; }

        [Required(ErrorMessage = "分店代码不能为空")]
        public string StoreCode { get; set; } = string.Empty;

        public int Status { get; set; } = 1;

        public List<string>? RoleNames { get; set; }

        /// <summary>是否允许登录 Web / App；未传表示不修改，旧客户端编辑资料不会改动登录权限。</summary>
        public bool? LoginEnabled { get; set; }
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
