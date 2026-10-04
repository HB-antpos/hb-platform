using System.Net.Sockets;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// 发票邮件发送服务。
    /// </summary>
    public class InvoiceEmailService : IInvoiceEmailService
    {
        private readonly ILogger<InvoiceEmailService> _logger;
        private readonly IInvoiceEmailSettingsService _settingsService;

        public InvoiceEmailService(
            ILogger<InvoiceEmailService> logger,
            IInvoiceEmailSettingsService settingsService
        )
        {
            _logger = logger;
            _settingsService = settingsService;
        }

        public async Task<ApiResponse<bool>> SendInvoiceAsync(StoreOrderInvoiceEmailMessage message)
        {
            InvoiceEmailOptions options;
            try
            {
                // 指定了发件账号就用该账号，未指定时沿用默认账号。
                options = await _settingsService.GetAccountOptionsAsync(message.FromAccountId);
            }
            catch (InvoiceEmailAccountNotFoundException ex)
            {
                _logger.LogError(
                    ex,
                    "发票邮件指定的发件账号不存在，收件人：{ToEmail}，账号：{FromAccountId}",
                    message.ToEmail,
                    message.FromAccountId
                );
                return ApiResponse<bool>.Error(
                    "所选发件邮箱账号不存在或已被删除，请刷新后重新选择发件账号",
                    "INVOICE_EMAIL_ACCOUNT_NOT_FOUND"
                );
            }
            catch (InvoiceEmailPasswordDecryptException ex)
            {
                _logger.LogError(ex, "发票邮件 SMTP 密码解密失败，收件人：{ToEmail}", message.ToEmail);
                return ApiResponse<bool>.Error(
                    "发票邮件 SMTP 密码解密失败，请重新输入 SMTP 密码后保存发票邮箱配置",
                    "INVOICE_EMAIL_PASSWORD_DECRYPT_FAILED"
                );
            }
            catch (InvoiceEmailDefaultAccountException ex)
            {
                _logger.LogError(ex, "发票邮件默认发件账号配置异常，收件人：{ToEmail}", message.ToEmail);
                return ApiResponse<bool>.Error(
                    "发票邮件默认发件账号配置异常，请在发票邮箱配置中重新设置默认账号",
                    "INVOICE_EMAIL_DEFAULT_ACCOUNT_INVALID"
                );
            }

            return await SendInvoiceAsync(message, options);
        }

        public async Task<ApiResponse<bool>> SendInvoiceAsync(
            StoreOrderInvoiceEmailMessage message,
            InvoiceEmailOptions options
        )
        {
            var configError = GetConfigurationError(options);
            if (configError != null)
            {
                return ApiResponse<bool>.Error(configError, "INVOICE_EMAIL_NOT_CONFIGURED");
            }

            if (message.Attachments.Count == 0)
            {
                return ApiResponse<bool>.Error("发票附件不能为空", "INVOICE_EMAIL_EMPTY_ATTACHMENT");
            }

            if (message.Attachments.Any(attachment => attachment.Bytes.Length == 0))
            {
                return ApiResponse<bool>.Error("发票附件不能为空", "INVOICE_EMAIL_EMPTY_ATTACHMENT");
            }

            var totalAttachmentBytes = message.Attachments.Sum(attachment => attachment.Bytes.LongLength);
            if (totalAttachmentBytes > options.MaxAttachmentBytes)
            {
                return ApiResponse<bool>.Error("发票附件不能超过配置限制", "INVOICE_EMAIL_ATTACHMENT_TOO_LARGE");
            }

            try
            {
                var email = BuildMimeMessage(message, options);

                using var smtpClient = CreateSmtpClient(options);
                var secureSocketOptions = options.UseSsl
                    ? SecureSocketOptions.SslOnConnect
                    : SecureSocketOptions.StartTls;

                await ConnectSmtpClientAsync(smtpClient, options, secureSocketOptions);

                if (!string.IsNullOrWhiteSpace(options.Username))
                {
                    await AuthenticateSmtpClientAsync(
                        smtpClient,
                        options.Username,
                        options.Password ?? string.Empty
                    );
                }

                await SendSmtpMessageAsync(smtpClient, email);
                await DisconnectSmtpClientAsync(smtpClient);

                return ApiResponse<bool>.OK(true, "发票邮件发送成功");
            }
            catch (SslHandshakeException ex)
            {
                _logger.LogError(ex, "发票邮件 TLS 握手失败，收件人：{ToEmail}", message.ToEmail);
                return ApiResponse<bool>.Error(
                    "发票邮件 TLS 握手失败，请检查 SMTP 证书或 InvoiceEmail.CheckCertificateRevocation 配置",
                    "INVOICE_EMAIL_TLS_HANDSHAKE_FAILED"
                );
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                _logger.LogError(
                    ex,
                    "发票邮件 SMTP 连接被拒绝，收件人：{ToEmail}，服务器：{Host}:{Port}",
                    message.ToEmail,
                    options.Host,
                    options.Port
                );
                return ApiResponse<bool>.Error(
                    $"SMTP 连接被拒绝，请检查 SMTP 主机、端口和 SSL 设置：{options.Host}:{options.Port}",
                    "INVOICE_EMAIL_SMTP_CONNECTION_REFUSED"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "发送发票邮件失败，收件人：{ToEmail}", message.ToEmail);
                return ApiResponse<bool>.Error("发票邮件发送失败，请检查 SMTP 配置或稍后重试", "INVOICE_EMAIL_SEND_FAILED");
            }
        }

        protected virtual SmtpClient CreateSmtpClient(InvoiceEmailOptions options)
        {
            // 部分服务器无法完成 CRL/OCSP 查询时，只允许通过配置关闭吊销检查，仍保留证书主体校验。
            return new SmtpClient
            {
                CheckCertificateRevocation = options.CheckCertificateRevocation,
            };
        }

        protected virtual MimeMessage BuildMimeMessage(StoreOrderInvoiceEmailMessage message)
        {
            return BuildMimeMessage(message, _settingsService.GetEffectiveOptionsAsync().GetAwaiter().GetResult());
        }

        protected virtual MimeMessage BuildMimeMessage(
            StoreOrderInvoiceEmailMessage message,
            InvoiceEmailOptions options
        )
        {
            var email = new MimeMessage();
            email.From.Add(
                new MailboxAddress(options.FromName ?? options.FromEmail!, options.FromEmail!)
            );
            email.To.Add(MailboxAddress.Parse(message.ToEmail));
            email.Subject = message.Subject;

            var builder = new BodyBuilder
            {
                TextBody = message.Body,
            };
            foreach (var attachment in message.Attachments)
            {
                var contentType = string.IsNullOrWhiteSpace(attachment.ContentType)
                    ? ContentType.Parse("application/octet-stream")
                    : ContentType.Parse(attachment.ContentType);
                builder.Attachments.Add(attachment.FileName, attachment.Bytes, contentType);
            }

            email.Body = builder.ToMessageBody();
            return email;
        }

        protected virtual Task ConnectSmtpClientAsync(
            SmtpClient smtpClient,
            InvoiceEmailOptions options,
            SecureSocketOptions secureSocketOptions
        )
        {
            var host = options.Host;
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new InvalidOperationException("未配置发票邮件 SMTP Host");
            }

            return smtpClient.ConnectAsync(host, options.Port, secureSocketOptions);
        }

        protected virtual Task AuthenticateSmtpClientAsync(
            SmtpClient smtpClient,
            string username,
            string password
        )
        {
            return smtpClient.AuthenticateAsync(username, password);
        }

        protected virtual Task SendSmtpMessageAsync(SmtpClient smtpClient, MimeMessage email)
        {
            return smtpClient.SendAsync(email);
        }

        protected virtual Task DisconnectSmtpClientAsync(SmtpClient smtpClient)
        {
            return smtpClient.DisconnectAsync(true);
        }

        private static string? GetConfigurationError(InvoiceEmailOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.Host) || options.Port <= 0)
            {
                return "未配置发票邮件 SMTP，请先完成 InvoiceEmail 配置";
            }

            if (string.IsNullOrWhiteSpace(options.FromEmail))
            {
                return "未配置发件邮箱，请先完成 InvoiceEmail 配置";
            }

            if (options.MaxAttachmentBytes <= 0)
            {
                return "InvoiceEmail.MaxAttachmentBytes 配置无效";
            }

            return null;
        }
    }
}
