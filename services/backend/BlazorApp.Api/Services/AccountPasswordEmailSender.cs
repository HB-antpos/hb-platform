using System.Net;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Api.Services.StaffNotifications;
using BlazorApp.Shared.DTOs;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
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

    /// <summary>
    /// 把系统生成的随机密码发到员工本人邮箱；员工首次登录必须改密。
    /// isReset=false 为店长新建员工的「账号已创建」文案，isReset=true 为店长重置密码的「密码已重置、旧密码已失效」文案。
    /// </summary>
    Task<ApiResponse<bool>> SendInitialPasswordAsync(
        string toEmail,
        string displayName,
        string loginName,
        string initialPassword,
        bool isReset = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// 发送已按模板渲染好的邮件（员工通知）：同样复用发票邮箱后台配置的公司 SMTP 账号。
    /// kind 只进日志，不会输出正文，所以随机密码不会进入日志。
    /// </summary>
    Task<ApiResponse<bool>> SendRenderedAsync(
        string toEmail,
        string subject,
        string text,
        string html,
        string kind,
        CancellationToken cancellationToken = default
    );

    /// <summary>发送绑定 / 更换邮箱验证码到待验证的新邮箱。</summary>
    Task<ApiResponse<bool>> SendEmailChangeCodeAsync(
        string toEmail,
        string displayName,
        string loginName,
        string code,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
/// 员工账号密码邮件：复用发票邮箱后台配置的公司 SMTP 账号（与监护人邮件同一套发件配置）。
/// 正文只有英文，只放登录账号与验证码（新建员工时为一次性初始密码），不放其他个人资料。
/// 「账号已创建」类邮件（新建员工的初始密码、设置密码邀请）末尾附 App 下载短链：新员工手机上还没有 App；
/// 重置密码类邮件不附（员工已经装过）。
/// 正文拼装在 internal 的 Build* 纯函数里，测试会断言它们不含中文字符。
/// </summary>
public sealed class AccountPasswordEmailSender : IAccountPasswordEmailSender
{
    private readonly IInvoiceEmailSettingsService _settings;
    private readonly ILogger<AccountPasswordEmailSender> _logger;

    // 下载短链的基址只来自配置（与员工通知模板的 {{app_link}} 同一来源），不取请求 Host，防止伪造请求头把员工引到钓鱼站点。
    private readonly string _appLink;

    public AccountPasswordEmailSender(
        IInvoiceEmailSettingsService settings,
        IOptions<StaffNotificationOptions> staffNotificationOptions,
        ILogger<AccountPasswordEmailSender> logger
    )
    {
        _settings = settings;
        _appLink = staffNotificationOptions.Value.ResolveAppLink();
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
        var content = BuildPasswordCode(displayName, loginName, code, FormatValidity(expiresAtUtc), invite, _appLink);
        return SendAsync(toEmail, content, invite ? "password-invite" : "password-reset", cancellationToken);
    }

    public Task<ApiResponse<bool>> SendInitialPasswordAsync(
        string toEmail,
        string displayName,
        string loginName,
        string initialPassword,
        bool isReset = false,
        CancellationToken cancellationToken = default
    )
    {
        // 日志只记类型，SendAsync 不会输出正文，初始密码不会进入日志。
        var content = BuildInitialPassword(displayName, loginName, initialPassword, isReset, _appLink);
        return SendAsync(toEmail, content, isReset ? "reset-password" : "initial-password", cancellationToken);
    }

    public Task<ApiResponse<bool>> SendRenderedAsync(
        string toEmail,
        string subject,
        string text,
        string html,
        string kind,
        CancellationToken cancellationToken = default
    ) => SendAsync(toEmail, new EmailContent(subject, text, html), kind, cancellationToken);

    public Task<ApiResponse<bool>> SendEmailChangeCodeAsync(
        string toEmail,
        string displayName,
        string loginName,
        string code,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken = default
    )
    {
        var content = BuildEmailChangeCode(displayName, loginName, code, FormatValidity(expiresAtUtc));
        return SendAsync(toEmail, content, "email-change", cancellationToken);
    }

    internal static EmailContent BuildPasswordCode(
        string displayName,
        string loginName,
        string code,
        string validFor,
        bool invite,
        string? appLink = null
    )
    {
        var subject = invite
            ? $"Set up your Hot Bargain app password: {code}"
            : $"Reset your Hot Bargain app password: {code}";
        var intro = invite
            ? "An account has been created for you in the Hot Bargain staff app."
            : "We received a request to reset the password of your Hot Bargain staff app account.";
        // 只有「账号已创建」的邀请邮件附下载入口；找回密码的员工已经装了 App。
        var (downloadText, downloadHtml) = BuildDownloadLine(invite ? appLink : null);
        var text =
            $"Hi {displayName},\n\n{intro}\n\n" +
            $"Sign-in name: {loginName}\nVerification code: {code} (valid for {validFor})\n\n" +
            "Open the HB app, tap \"Set up / forgot password\" on the sign-in screen, enter this email address and the code, then choose your own password.\n" +
            downloadText +
            "Do not share this code with anyone, including your manager. If you did not expect this email, you can ignore it.\n";
        var html =
            $"<p>Hi {Encode(displayName)},</p><p>{Encode(intro)}</p>" +
            $"<p>Sign-in name: <b>{Encode(loginName)}</b></p>" +
            $"<p>Verification code (valid for {Encode(validFor)}):</p>" +
            $"<p style=\"font-size:24px;font-weight:700;letter-spacing:4px\">{Encode(code)}</p>" +
            "<p>Open the HB app, tap <b>Set up / forgot password</b> on the sign-in screen, enter this email address and the code, then choose your own password.</p>" +
            downloadHtml +
            "<p>Do not share this code with anyone, including your manager. If you did not expect this email, you can ignore it.</p>";
        return new EmailContent(subject, text, html);
    }

    /// <summary>
    /// 「下载 App」一行（纯文本 + HTML）。appLink 为空时两者都是空串，正文与加链接之前完全一致。
    /// 链接指向公开短链页：iPhone 自动跳 App Store、安卓下载最新 APK、电脑显示二维码，所以一条链接够用。
    /// </summary>
    private static (string Text, string Html) BuildDownloadLine(string? appLink)
    {
        if (string.IsNullOrWhiteSpace(appLink)) return (string.Empty, string.Empty);
        var link = appLink.Trim();
        return (
            $"Don't have the app yet? Get the latest HB app (iPhone and Android): {link}\n",
            $"<p>Don't have the app yet? Get the latest HB app (iPhone and Android): <a href=\"{Encode(link)}\">{Encode(link)}</a></p>"
        );
    }

    /// <summary>
    /// 随机密码邮件正文。isReset=false 是新建员工 / 开通登录的「账号已创建」文案；
    /// isReset=true 是店长重置密码：必须明说旧密码已失效（员工的旧密码和旧设备登录在送达时就被作废了），
    /// 且「没想到会收到这封邮件」时应联系店长，而不是「忽略」——忽略的话员工会被锁在外面。
    /// 两种文案都保留 "choose your own password"，英文单语测试依赖它。
    /// appLink 只在 isReset=false（账号刚创建，员工手机上还没有 App）时写进正文。
    /// </summary>
    internal static EmailContent BuildInitialPassword(
        string displayName,
        string loginName,
        string initialPassword,
        bool isReset = false,
        string? appLink = null
    )
    {
        var (downloadText, downloadHtml) = BuildDownloadLine(isReset ? null : appLink);
        var subject = isReset ? "Your Hot Bargain app password has been reset" : "Your Hot Bargain app account";
        var intro = isReset
            ? "Your manager has reset the password of your Hot Bargain staff app account. Your old password no longer works."
            : "An account has been created for you in the Hot Bargain staff app.";
        var passwordLabel = isReset ? "New password" : "Initial password";
        var closing = isReset
            ? "Do not share this password with anyone, including your manager. If you did not expect this email, please let your manager know."
            : "Do not share this password with anyone, including your manager. If you did not expect this email, you can ignore it.";
        var text =
            $"Hi {displayName},\n\n{intro}\n\n" +
            $"Sign-in name: {loginName}\n{passwordLabel}: {initialPassword}\n\n" +
            "Sign in to the HB app with these details. You will be asked to choose your own password the first time you sign in.\n" +
            downloadText +
            $"{closing}\n";
        var html =
            $"<p>Hi {Encode(displayName)},</p><p>{Encode(intro)}</p>" +
            $"<p>Sign-in name: <b>{Encode(loginName)}</b></p>" +
            $"<p>{Encode(passwordLabel)}:</p>" +
            $"<p style=\"font-size:24px;font-weight:700;letter-spacing:2px;font-family:Consolas,monospace\">{Encode(initialPassword)}</p>" +
            "<p>Sign in to the HB app with these details. You will be asked to choose your own password the first time you sign in.</p>" +
            downloadHtml +
            $"<p>{Encode(closing)}</p>";
        return new EmailContent(subject, text, html);
    }

    internal static EmailContent BuildEmailChangeCode(string displayName, string loginName, string code, string validFor)
    {
        var subject = $"Confirm your email for the Hot Bargain app: {code}";
        var text =
            $"Hi {displayName},\n\n" +
            $"You asked to use this email address for your Hot Bargain staff app account ({loginName}).\n" +
            $"Verification code: {code} (valid for {validFor})\n\n" +
            "Enter this code in the HB app to finish. After that you can reset your password with this email on the sign-in screen.\n" +
            "If you did not ask for this, ignore this email and your account will not change.\n";
        var html =
            $"<p>Hi {Encode(displayName)},</p>" +
            $"<p>You asked to use this email address for your Hot Bargain staff app account (<b>{Encode(loginName)}</b>).</p>" +
            $"<p>Verification code (valid for {Encode(validFor)}):</p>" +
            $"<p style=\"font-size:24px;font-weight:700;letter-spacing:4px\">{Encode(code)}</p>" +
            "<p>Enter this code in the HB app to finish. After that you can reset your password with this email on the sign-in screen. If you did not ask for this, ignore this email and your account will not change.</p>";
        return new EmailContent(subject, text, html);
    }

    private async Task<ApiResponse<bool>> SendAsync(
        string toEmail,
        EmailContent content,
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
            message.Subject = content.Subject;
            message.Body = new BodyBuilder { TextBody = content.Text, HtmlBody = content.Html }.ToMessageBody();

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
}
