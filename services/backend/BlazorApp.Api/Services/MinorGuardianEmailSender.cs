using System.Net;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace BlazorApp.Api.Services;

/// <summary>未成年用工签署链接的站点配置；可用环境变量 MinorEmployment__SigningBaseUrl 覆盖。</summary>
public sealed class MinorEmploymentOptions
{
    public const string SectionName = "MinorEmployment";

    /// <summary>监护人签署页所在站点（不含路径）。邮件里必须是绝对地址，不能取请求 Host，防止被伪造成钓鱼链接。</summary>
    public string SigningBaseUrl { get; set; } = "https://hotbargain.vip";
}

public interface IMinorGuardianEmailSender
{
    Task<ApiResponse<bool>> SendSigningLinkAsync(string toEmail, string guardianName, string childName, string signingUrl, DateTime expiresAtUtc, CancellationToken cancellationToken = default);

    Task<ApiResponse<bool>> SendVerificationCodeAsync(string toEmail, string guardianName, string code, DateTime expiresAtUtc, CancellationToken cancellationToken = default);
}

/// <summary>
/// 监护人邮件：复用发票邮箱后台配置的公司 SMTP 账号（不另起一套发件配置）。
/// 正文只有英文，只放签署链接或验证码，不放孩子的学校、生日等资料。
/// 正文拼装在 internal 的 Build* 纯函数里，测试会断言它们不含中文字符。
/// </summary>
public sealed class MinorGuardianEmailSender : IMinorGuardianEmailSender
{
    private readonly IInvoiceEmailSettingsService _settings;
    private readonly ILogger<MinorGuardianEmailSender> _logger;

    public MinorGuardianEmailSender(IInvoiceEmailSettingsService settings, ILogger<MinorGuardianEmailSender> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public Task<ApiResponse<bool>> SendSigningLinkAsync(string toEmail, string guardianName, string childName, string signingUrl, DateTime expiresAtUtc, CancellationToken cancellationToken = default)
    {
        var content = BuildSigningLink(guardianName, childName, signingUrl, FormatSydney(expiresAtUtc));
        return SendAsync(toEmail, content.Subject, content.Text, content.Html, "signing-link", cancellationToken);
    }

    public Task<ApiResponse<bool>> SendVerificationCodeAsync(string toEmail, string guardianName, string code, DateTime expiresAtUtc, CancellationToken cancellationToken = default)
    {
        var minutes = Math.Max(1, (int)Math.Round((expiresAtUtc - DateTime.UtcNow).TotalMinutes));
        var content = BuildVerificationCode(guardianName, code, minutes);
        return SendAsync(toEmail, content.Subject, content.Text, content.Html, "verification-code", cancellationToken);
    }

    internal static EmailContent BuildSigningLink(string guardianName, string childName, string signingUrl, string expiresText)
    {
        var subject = $"Parent/guardian consent for {childName} – Hot Bargain";
        var text =
            $"Dear {guardianName},\n\n" +
            $"{childName} works (or will work) at Hot Bargain. Before they can be rostered, we need a parent or guardian to review and sign the minor employment consent form.\n\n" +
            $"Open this link to review and sign (valid until {expiresText}, Sydney time):\n{signingUrl}\n\n" +
            "For your security, a one-time verification code will be sent to this email address after you open the link.\n" +
            "If you did not expect this email, please ignore it or contact the store manager.\n";
        var html =
            $"<p>Dear {Encode(guardianName)},</p>" +
            $"<p>{Encode(childName)} works (or will work) at Hot Bargain. Before they can be rostered, we need a parent or guardian to review and sign the minor employment consent form.</p>" +
            $"<p><a href=\"{Encode(signingUrl)}\">Review and sign the consent form</a><br/>Valid until {Encode(expiresText)} (Sydney time).</p>" +
            "<p>For your security, a one-time verification code will be sent to this email address after you open the link. If you did not expect this email, please ignore it or contact the store manager.</p>";
        return new EmailContent(subject, text, html);
    }

    internal static EmailContent BuildVerificationCode(string guardianName, string code, int minutes)
    {
        var subject = $"Your verification code: {code}";
        var text =
            $"Dear {guardianName},\n\nYour verification code for the Hot Bargain consent form is: {code}\n" +
            $"It expires in {minutes} minutes. Do not share this code with anyone, including the employee.\n";
        var html =
            $"<p>Dear {Encode(guardianName)},</p><p>Your verification code for the Hot Bargain consent form is:</p>" +
            $"<p style=\"font-size:24px;font-weight:700;letter-spacing:4px\">{Encode(code)}</p>" +
            $"<p>It expires in {minutes} minutes. Do not share this code with anyone, including the employee.</p>";
        return new EmailContent(subject, text, html);
    }

    private async Task<ApiResponse<bool>> SendAsync(string toEmail, string subject, string text, string html, string kind, CancellationToken cancellationToken)
    {
        InvoiceEmailOptions options;
        try
        {
            options = await _settings.GetEffectiveOptionsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is InvoiceEmailPasswordDecryptException or InvoiceEmailDefaultAccountException)
        {
            _logger.LogError(ex, "监护人邮件发件账号配置异常，类型：{Kind}", kind);
            return ApiResponse<bool>.Error("公司发件邮箱配置异常，请联系管理员检查发票邮箱配置", "GUARDIAN_EMAIL_NOT_CONFIGURED");
        }

        if (string.IsNullOrWhiteSpace(options.Host) || options.Port <= 0 || string.IsNullOrWhiteSpace(options.FromEmail))
        {
            return ApiResponse<bool>.Error("公司发件邮箱未配置，暂不能发送监护人邮件", "GUARDIAN_EMAIL_NOT_CONFIGURED");
        }

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(options.FromName ?? options.FromEmail, options.FromEmail));
            message.To.Add(MailboxAddress.Parse(toEmail));
            message.Subject = subject;
            message.Body = new BodyBuilder { TextBody = text, HtmlBody = html }.ToMessageBody();

            using var client = new SmtpClient { CheckCertificateRevocation = options.CheckCertificateRevocation };
            await client.ConnectAsync(options.Host, options.Port, options.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, cancellationToken);
            if (!string.IsNullOrWhiteSpace(options.Username))
            {
                await client.AuthenticateAsync(options.Username, options.Password ?? string.Empty, cancellationToken);
            }
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            return ApiResponse<bool>.OK(true, "邮件已发送");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 日志只记类型与收件域名，不记完整邮箱与验证码。
            _logger.LogError(ex, "监护人邮件发送失败，类型：{Kind}，收件域：{Domain}", kind, toEmail.Split('@').LastOrDefault());
            return ApiResponse<bool>.Error("邮件发送失败，请稍后重试", "GUARDIAN_EMAIL_SEND_FAILED");
        }
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static string FormatSydney(DateTime utc)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone).ToString("dd/MM/yyyy HH:mm");
        }
        catch (TimeZoneNotFoundException)
        {
            return utc.ToString("dd/MM/yyyy HH:mm") + " UTC";
        }
    }
}
