using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces.React
{
    /// <summary>
    /// 发票邮件 SMTP 配置服务接口。
    /// </summary>
    public interface IInvoiceEmailSettingsService
    {
        Task<ApiResponse<InvoiceEmailSettingsDto>> GetSettingsAsync(
            CancellationToken cancellationToken = default
        );

        Task<ApiResponse<InvoiceEmailSettingsDto>> UpdateSettingsAsync(
            UpdateInvoiceEmailSettingsDto request,
            string? updatedBy,
            CancellationToken cancellationToken = default
        );

        Task<InvoiceEmailOptions> GetEffectiveOptionsAsync(
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 按发件账号 ID 取发送配置；ID 为空时等同默认账号。
        /// </summary>
        Task<InvoiceEmailOptions> GetAccountOptionsAsync(
            string? accountId,
            CancellationToken cancellationToken = default
        );

        /// <summary>
        /// 列出发送邮件时可选的发件账号（不含 SMTP 凭据）。
        /// </summary>
        Task<List<InvoiceEmailSenderAccountDto>> GetSenderAccountsAsync(
            CancellationToken cancellationToken = default
        );

        Task<InvoiceEmailOptions> BuildTransientOptionsAsync(
            TestInvoiceEmailSettingsDto request,
            CancellationToken cancellationToken = default
        );
    }
}
