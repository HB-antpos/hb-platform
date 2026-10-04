using System.ComponentModel.DataAnnotations;

namespace BlazorApp.Shared.DTOs
{
    /// <summary>
    /// 发票邮件 SMTP 配置展示 DTO。
    /// </summary>
    public class InvoiceEmailSettingsDto
    {
        public List<InvoiceEmailAccountDto> Accounts { get; set; } = new();
    }

    /// <summary>
    /// 发票邮件 SMTP 发件账号展示 DTO。
    /// </summary>
    public class InvoiceEmailAccountDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Host { get; set; }
        public int Port { get; set; }
        public bool UseSsl { get; set; } = true;
        public bool CheckCertificateRevocation { get; set; } = true;
        public string? Username { get; set; }
        public bool HasPassword { get; set; }
        public string? FromEmail { get; set; }
        public string? FromName { get; set; }
        public long MaxAttachmentBytes { get; set; }
        public bool IsDefault { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
    }

    /// <summary>
    /// 发送邮件时可选的发件账号。只暴露名称与发件地址，不含 SMTP 主机、用户名和密码。
    /// </summary>
    public class InvoiceEmailSenderAccountDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string FromEmail { get; set; } = string.Empty;
        public string? FromName { get; set; }
        public bool IsDefault { get; set; }
    }

    /// <summary>
    /// 发票邮件 SMTP 配置保存请求。
    /// </summary>
    public class UpdateInvoiceEmailSettingsDto
    {
        [Required(ErrorMessage = "发件邮箱账号不能为空")]
        [MinLength(1, ErrorMessage = "至少需要配置一个发件邮箱账号")]
        public List<UpdateInvoiceEmailAccountDto> Accounts { get; set; } = new();
    }

    /// <summary>
    /// 发票邮件 SMTP 发件账号保存请求。
    /// </summary>
    public class UpdateInvoiceEmailAccountDto
    {
        [StringLength(50, ErrorMessage = "发件邮箱账号 ID 长度不能超过50个字符")]
        public string? Id { get; set; }

        [Required(ErrorMessage = "发件账号名称不能为空")]
        [StringLength(100, ErrorMessage = "发件账号名称长度不能超过100个字符")]
        public string Name { get; set; } = string.Empty;

        [Required(ErrorMessage = "SMTP 主机不能为空")]
        [StringLength(200, ErrorMessage = "SMTP 主机长度不能超过200个字符")]
        public string Host { get; set; } = string.Empty;

        [Range(1, 65535, ErrorMessage = "SMTP 端口必须在1到65535之间")]
        public int Port { get; set; }

        public bool UseSsl { get; set; } = true;

        public bool CheckCertificateRevocation { get; set; } = true;

        [StringLength(200, ErrorMessage = "SMTP 用户名长度不能超过200个字符")]
        public string? Username { get; set; }

        [StringLength(500, ErrorMessage = "SMTP 密码长度不能超过500个字符")]
        public string? Password { get; set; }

        public bool ClearPassword { get; set; }

        [Required(ErrorMessage = "发件邮箱不能为空")]
        [EmailAddress(ErrorMessage = "发件邮箱格式不正确")]
        [StringLength(200, ErrorMessage = "发件邮箱长度不能超过200个字符")]
        public string FromEmail { get; set; } = string.Empty;

        [StringLength(200, ErrorMessage = "发件人名称长度不能超过200个字符")]
        public string? FromName { get; set; }

        [Range(1, long.MaxValue, ErrorMessage = "附件大小上限必须大于0")]
        public long MaxAttachmentBytes { get; set; } = 5 * 1024 * 1024;

        public bool IsDefault { get; set; }
    }

    /// <summary>
    /// 发票邮件 SMTP 测试请求。
    /// </summary>
    public class TestInvoiceEmailSettingsDto : UpdateInvoiceEmailAccountDto
    {
        [Required(ErrorMessage = "测试收件邮箱不能为空")]
        [EmailAddress(ErrorMessage = "测试收件邮箱格式不正确")]
        [StringLength(100, ErrorMessage = "测试收件邮箱长度不能超过100个字符")]
        public string TestToEmail { get; set; } = string.Empty;
    }
}
