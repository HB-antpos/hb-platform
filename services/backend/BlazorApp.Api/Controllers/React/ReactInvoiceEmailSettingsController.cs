using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// React 发票邮件 SMTP 配置控制器。
    /// </summary>
    [ApiController]
    [Route("api/react/v1/invoice-email-settings")]
    [Authorize]
    public class ReactInvoiceEmailSettingsController : ControllerBase
    {
        private readonly IInvoiceEmailSettingsService _settingsService;
        private readonly IInvoiceEmailService _invoiceEmailService;
        private readonly ICurrentUserService _currentUserService;
        private readonly IStoreOrderAccessPolicy _storeOrderAccessPolicy;
        private readonly ILogger<ReactInvoiceEmailSettingsController> _logger;

        public ReactInvoiceEmailSettingsController(
            IInvoiceEmailSettingsService settingsService,
            IInvoiceEmailService invoiceEmailService,
            ICurrentUserService currentUserService,
            IStoreOrderAccessPolicy storeOrderAccessPolicy,
            ILogger<ReactInvoiceEmailSettingsController> logger
        )
        {
            _settingsService = settingsService;
            _invoiceEmailService = invoiceEmailService;
            _currentUserService = currentUserService;
            _storeOrderAccessPolicy = storeOrderAccessPolicy;
            _logger = logger;
        }

        /// <summary>
        /// 发送发票邮件时可选的发件账号列表，只返回名称与发件地址。
        /// </summary>
        [HttpGet("sender-accounts")]
        public async Task<IActionResult> GetSenderAccounts(CancellationToken cancellationToken)
        {
            // 与发送发票邮件接口同一权限门槛：能编辑订单（即能发发票）的人才能看到可选发件账号。
            var decision = await _storeOrderAccessPolicy.RequireOrderManagementEditAsync();
            if (decision.IsForbidden)
            {
                return Forbid();
            }

            var accounts = await _settingsService.GetSenderAccountsAsync(cancellationToken);
            return Ok(ApiResponse<List<InvoiceEmailSenderAccountDto>>.OK(accounts, "查询成功"));
        }

        [HttpGet]
        [Authorize(Policy = Permissions.System.ManageSettings)]
        public async Task<IActionResult> Get(CancellationToken cancellationToken)
        {
            var result = await _settingsService.GetSettingsAsync(cancellationToken);
            return Ok(result);
        }

        [HttpPut]
        [Authorize(Policy = Permissions.System.ManageSettings)]
        public async Task<IActionResult> Update(
            [FromBody] UpdateInvoiceEmailSettingsDto request,
            CancellationToken cancellationToken
        )
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ApiResponse<object>.Error("请求参数验证失败", "VALIDATION_ERROR", ModelState));
            }

            var result = await _settingsService.UpdateSettingsAsync(
                request,
                _currentUserService.GetCurrentUsername(),
                cancellationToken
            );
            return result.Success ? Ok(result) : BadRequest(result);
        }

        [HttpPost("test")]
        [Authorize(Policy = Permissions.System.ManageSettings)]
        public async Task<IActionResult> Test(
            [FromBody] TestInvoiceEmailSettingsDto request,
            CancellationToken cancellationToken
        )
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ApiResponse<object>.Error("请求参数验证失败", "VALIDATION_ERROR", ModelState));
            }

            try
            {
                var options = await _settingsService.BuildTransientOptionsAsync(request, cancellationToken);
                var result = await _invoiceEmailService.SendInvoiceAsync(
                    new StoreOrderInvoiceEmailMessage
                    {
                        ToEmail = request.TestToEmail.Trim(),
                        Subject = "Invoice email SMTP test",
                        Body = "This is a test email from HB Admin Platform.",
                        Attachments = new List<StoreOrderInvoiceEmailAttachment>
                        {
                            new()
                            {
                                FileName = "smtp-test.txt",
                                ContentType = "text/plain",
                                Bytes = System.Text.Encoding.UTF8.GetBytes("SMTP test"),
                            },
                        },
                    },
                    options
                );

                return result.Success ? Ok(result) : BadRequest(result);
            }
            catch (InvoiceEmailPasswordDecryptException ex)
            {
                _logger.LogError(ex, "测试发票邮件 SMTP 配置时解密已保存密码失败");
                return BadRequest(ApiResponse<bool>.Error(
                    "发票邮件 SMTP 密码解密失败，请重新输入 SMTP 密码后保存发票邮箱配置",
                    "INVOICE_EMAIL_PASSWORD_DECRYPT_FAILED"
                ));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "测试发票邮件 SMTP 配置失败");
                return StatusCode(500, ApiResponse<bool>.Error("测试邮件发送失败", "INVOICE_EMAIL_TEST_FAILED"));
            }
        }
    }
}
