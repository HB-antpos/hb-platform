using System.Net;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace BlazorApp.Api.Services.Myob;

/// <summary>一封工资单邮件所需的全部内容（正文不含任何金额）。</summary>
public sealed record MyobPayslipEmail(
    string ToEmail,
    string? CcEmail,
    string EmployeeName,
    string CompanyName,
    string PeriodText,
    string PaymentDateText,
    string? Note,
    string Subject,
    string Filename,
    byte[] PdfBytes);

public interface IMyobPayslipEmailSender
{
    Task<ApiResponse<bool>> SendAsync(MyobPayslipEmail email, CancellationToken cancellationToken = default);
}

/// <summary>
/// 工资单邮件：复用发票邮箱后台配置的公司 SMTP 账号（与监护人邮件同一做法，不另起一套发件配置）。
/// 正文只有英文，只说明这是哪一期、附件是 PDF；<b>不写任何金额</b>。日志只记收件域名，不记完整邮箱与附言。
/// </summary>
internal sealed class MyobPayslipEmailSender : IMyobPayslipEmailSender
{
    private readonly IInvoiceEmailSettingsService _settings;
    private readonly ILogger<MyobPayslipEmailSender> _logger;

    public MyobPayslipEmailSender(IInvoiceEmailSettingsService settings, ILogger<MyobPayslipEmailSender> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<ApiResponse<bool>> SendAsync(MyobPayslipEmail email, CancellationToken cancellationToken = default)
    {
        InvoiceEmailOptions options;
        try
        {
            options = await _settings.GetEffectiveOptionsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is InvoiceEmailPasswordDecryptException or InvoiceEmailDefaultAccountException)
        {
            _logger.LogError(ex, "工资单邮件发件账号配置异常");
            return ApiResponse<bool>.Error("公司发件邮箱配置异常，请联系管理员检查发票邮箱配置", MyobPayslipErrorCodes.EmailNotConfigured);
        }

        if (string.IsNullOrWhiteSpace(options.Host) || options.Port <= 0 || string.IsNullOrWhiteSpace(options.FromEmail))
        {
            return ApiResponse<bool>.Error("公司发件邮箱未配置，暂不能发送工资单邮件", MyobPayslipErrorCodes.EmailNotConfigured);
        }

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(options.FromName ?? options.FromEmail, options.FromEmail));
            message.To.Add(MailboxAddress.Parse(email.ToEmail));
            if (!string.IsNullOrWhiteSpace(email.CcEmail))
            {
                message.Cc.Add(MailboxAddress.Parse(email.CcEmail));
            }

            message.Subject = email.Subject;
            var builder = new BodyBuilder { TextBody = BuildText(email), HtmlBody = BuildHtml(email) };
            builder.Attachments.Add(email.Filename, email.PdfBytes, ContentType.Parse("application/pdf"));
            message.Body = builder.ToMessageBody();

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
            // 日志只记异常类型与收件域名，不记完整邮箱、附言或附件内容。
            _logger.LogError(ex, "工资单邮件发送失败，收件域：{Domain}", email.ToEmail.Split('@').LastOrDefault());
            return ApiResponse<bool>.Error("邮件发送失败，请稍后重试", MyobPayslipErrorCodes.EmailSendFailed);
        }
    }

    /// <summary>「Hi Alex,」：取姓名第一段并首字母大写；取不到就用 "Hi,"。</summary>
    internal static string FirstName(string? employeeName)
    {
        var first = (employeeName ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(first) ? string.Empty : char.ToUpperInvariant(first[0]) + first[1..].ToLowerInvariant();
    }

    internal static string BuildText(MyobPayslipEmail e)
    {
        var name = FirstName(e.EmployeeName);
        var hello = name.Length == 0 ? "Hi," : $"Hi {name},";
        var note = string.IsNullOrWhiteSpace(e.Note) ? string.Empty : $"\nMessage from the sender:\n{e.Note.Trim()}\n";
        return
            $"{hello}\n\n" +
            $"Your payslip for the pay period {e.PeriodText} (payment date {e.PaymentDateText}) from {e.CompanyName} is attached as a PDF.\n" +
            "If something looks wrong, please talk to your store manager. Please do not forward this email.\n" +
            note;
    }

    internal static string BuildHtml(MyobPayslipEmail e)
    {
        var name = FirstName(e.EmployeeName);
        var hello = name.Length == 0 ? "Hi," : $"Hi {Encode(name)},";
        var note = string.IsNullOrWhiteSpace(e.Note)
            ? string.Empty
            : $"<p style=\"padding:8px 12px;background:#f5f6f8;border-radius:6px\">{Encode(e.Note.Trim()).Replace("\n", "<br/>")}</p>";
        return
            $"<p>{hello}</p>" +
            $"<p>Your payslip for the pay period <b>{Encode(e.PeriodText)}</b> (payment date {Encode(e.PaymentDateText)}) from <b>{Encode(e.CompanyName)}</b> is attached as a PDF.</p>" +
            "<p style=\"color:#667085\">If something looks wrong, please talk to your store manager. Please do not forward this email.</p>" +
            note;
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
