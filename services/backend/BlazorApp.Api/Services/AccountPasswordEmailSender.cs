using System.Net;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace BlazorApp.Api.Services;

public interface IAccountPasswordEmailSender
{
    /// <summary>发送设置 / 重置密码验证码。invite=true 为店长新建或重置后的邀请文案。</summary>
    Task<ApiResponse<bool>> SendPasswordCodeAsync(
        string toEmail,
        string displayName,
        string loginName,
        string code,
        DateTime expiresAtUtc,
        bool invite,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// 员工账号密码邮件：复用发票邮箱后台配置的公司 SMTP 账号（与监护人邮件同一套发件配置）。
/// 正文中英双语，只放登录账号与验证码，不放密码或其他个人资料。
/// </summary>
public sealed class AccountPasswordEmailSender : IAccountPasswordEmailSender
{
    private readonly IInvoiceEmailSettingsService _settings;
    private readonly ILogger<AccountPasswordEmailSender> _logger;

    public AccountPasswordEmailSender(
        IInvoiceEmailSettingsService settings,
        ILogger<AccountPasswordEmailSender> logger
    )
    {
        _settings = settings;
        _logger = logger;
    }

    public Task<ApiResponse<bool>> SendPasswordCodeAsync(
        string toEmail,
        string displayName,
        string loginName,
        string code,
        DateTime expiresAtUtc,
        bool invite,
        CancellationToken cancellationToken = default
    )
    {
        var validFor = FormatValidity(expiresAtUtc);
        var subject = invite
            ? $"Set up your Hot Bargain app password: {code} / 设置 HB 登录密码"
            : $"Reset your Hot Bargain app password: {code} / 重置 HB 登录密码";
        var intro = invite
            ? "An account has been created for you in the Hot Bargain staff app."
            : "We received a request to reset the password of your Hot Bargain staff app account.";
        var introZh = invite
            ? "店长已为你开通 Hot Bargain 员工 App 账号。"
            : "我们收到了重置你 Hot Bargain 员工 App 账号密码的请求。";
        var text =
            $"Hi {displayName},\n\n{intro}\n\n" +
            $"Sign-in name: {loginName}\nVerification code: {code} (valid for {validFor})\n\n" +
            "Open the HB app, tap \"Set up / forgot password\" on the sign-in screen, enter this email address and the code, then choose your own password.\n" +
            "Do not share this code with anyone, including your manager. If you did not expect this email, you can ignore it.\n\n" +
            $"{displayName} 你好：\n{introZh}\n登录账号：{loginName}\n验证码：{code}（{FormatValidityZh(expiresAtUtc)}内有效）\n" +
            "请打开 HB App，在登录页点「设置 / 忘记密码」，输入本邮箱和验证码，然后设置你自己的密码。\n" +
            "请勿把验证码告诉任何人（包括店长）。如果这不是你本人的操作，忽略本邮件即可。\n";
        var html =
            $"<p>Hi {Encode(displayName)},</p><p>{Encode(intro)}</p>" +
            $"<p>Sign-in name: <b>{Encode(loginName)}</b></p>" +
            $"<p>Verification code (valid for {Encode(validFor)}):</p>" +
            $"<p style=\"font-size:24px;font-weight:700;letter-spacing:4px\">{Encode(code)}</p>" +
            "<p>Open the HB app, tap <b>Set up / forgot password</b> on the sign-in screen, enter this email address and the code, then choose your own password.</p>" +
            "<p>Do not share this code with anyone, including your manager. If you did not expect this email, you can ignore it.</p>" +
            $"<hr/><p>{Encode(displayName)} 你好：</p><p>{Encode(introZh)}</p>" +
            $"<p>登录账号：<b>{Encode(loginName)}</b><br/>验证码：<b>{Encode(code)}</b>（{Encode(FormatValidityZh(expiresAtUtc))}内有效）</p>" +
            "<p>请打开 HB App，在登录页点「设置 / 忘记密码」，输入本邮箱和验证码，然后设置你自己的密码。请勿把验证码告诉任何人（包括店长）。</p>";
        return SendAsync(toEmail, subject, text, html, invite ? "password-invite" : "password-reset", cancellationToken);
    }

    private async Task<ApiResponse<bool>> SendAsync(
        string toEmail,
        string subject,
        string text,
        string html,
        string kind,
        CancellationToken cancellationToken
    )
    {
        InvoiceEmailOptions options;
        try
        {
            options = await _settings.GetEffectiveOptionsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is InvoiceEmailPasswordDecryptException or InvoiceEmailDefaultAccountException)
        {
            _logger.LogError(ex, "账号密码邮件发件账号配置异常，类型：{Kind}", kind);
            return ApiResponse<bool>.Error("公司发件邮箱配置异常，请联系管理员检查发票邮箱配置", AccountEmailNotConfiguredCode);
        }

        if (string.IsNullOrWhiteSpace(options.Host) || options.Port <= 0 || string.IsNullOrWhiteSpace(options.FromEmail))
        {
            return ApiResponse<bool>.Error("公司发件邮箱未配置，暂不能发送账号邮件", AccountEmailNotConfiguredCode);
        }

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(options.FromName ?? options.FromEmail, options.FromEmail));
            message.To.Add(MailboxAddress.Parse(toEmail));
            message.Subject = subject;
            message.Body = new BodyBuilder { TextBody = text, HtmlBody = html }.ToMessageBody();

            using var client = new SmtpClient { CheckCertificateRevocation = options.CheckCertificateRevocation };
            await client.ConnectAsync(
                options.Host,
                options.Port,
                options.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls,
                cancellationToken
            );
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
            _logger.LogError(ex, "账号密码邮件发送失败，类型：{Kind}，收件域：{Domain}", kind, toEmail.Split('@').LastOrDefault());
            return ApiResponse<bool>.Error("邮件发送失败，请稍后重试", AccountEmailSendFailedCode);
        }
    }

    public const string AccountEmailNotConfiguredCode = "ACCOUNT_EMAIL_NOT_CONFIGURED";
    public const string AccountEmailSendFailedCode = "ACCOUNT_EMAIL_SEND_FAILED";

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static string FormatValidity(DateTime expiresAtUtc)
    {
        var minutes = Math.Max(1, (int)Math.Round((expiresAtUtc - DateTime.UtcNow).TotalMinutes));
        return minutes >= 120 ? $"{(int)Math.Round(minutes / 60.0)} hours" : $"{minutes} minutes";
    }

    private static string FormatValidityZh(DateTime expiresAtUtc)
    {
        var minutes = Math.Max(1, (int)Math.Round((expiresAtUtc - DateTime.UtcNow).TotalMinutes));
        return minutes >= 120 ? $"{(int)Math.Round(minutes / 60.0)} 小时" : $"{minutes} 分钟";
    }
}
