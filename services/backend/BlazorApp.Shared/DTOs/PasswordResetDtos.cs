using System.ComponentModel.DataAnnotations;

namespace BlazorApp.Shared.DTOs
{
    /// <summary>登录页「设置 / 忘记密码」第一步：按邮箱发送验证码。</summary>
    public sealed class PasswordResetRequestDto
    {
        [Required(ErrorMessage = "请输入邮箱")]
        [StringLength(254)]
        public string Email { get; set; } = string.Empty;
    }

    /// <summary>第二步：邮箱 + 验证码 + 新密码。</summary>
    public sealed class PasswordResetConfirmDto
    {
        [Required(ErrorMessage = "请输入邮箱")]
        [StringLength(254)]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "请输入验证码")]
        [StringLength(16)]
        public string Code { get; set; } = string.Empty;

        [Required(ErrorMessage = "新密码不能为空")]
        [StringLength(100, MinimumLength = 6, ErrorMessage = "密码长度必须在6-100个字符之间")]
        public string NewPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "确认密码不能为空")]
        [Compare(nameof(NewPassword), ErrorMessage = "确认密码与新密码不匹配")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    /// <summary>店长发送设置密码邮件的结果：只回传打码邮箱与有效期，不回传验证码。</summary>
    public sealed class PasswordSetupEmailResultDto
    {
        public string MaskedEmail { get; set; } = string.Empty;
        public DateTime ExpiresAtUtc { get; set; }
    }
}
