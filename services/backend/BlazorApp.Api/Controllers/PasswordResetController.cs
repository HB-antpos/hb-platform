using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BlazorApp.Api.Controllers
{
    /// <summary>
    /// 登录页「设置 / 忘记密码」：匿名接口，按 IP 限流，响应禁止缓存。
    /// 店长新建 / 重置员工后发出的邀请验证码，也在这里由员工自己完成设置。
    /// </summary>
    [ApiController]
    [Route("api/Auth/password-reset")]
    [AllowAnonymous]
    public sealed class PasswordResetController : ControllerBase
    {
        private readonly IPasswordResetService _service;
        private readonly IClientIpResolver? _clientIpResolver;

        public PasswordResetController(IPasswordResetService service, IClientIpResolver? clientIpResolver = null)
        {
            _service = service;
            _clientIpResolver = clientIpResolver;
        }

        [HttpPost("request")]
        [EnableRateLimiting(PasswordResetRateLimits.PolicyName)]
        public async Task<ApiResponse<bool>> RequestCode([FromBody] PasswordResetRequestDto dto)
        {
            NoStore();
            if (!ModelState.IsValid)
            {
                return ApiResponse<bool>.Error("请求参数验证失败", "VALIDATION_ERROR", ModelState);
            }
            return await _service.RequestSelfServiceAsync(dto.Email, ResolveIp());
        }

        [HttpPost("confirm")]
        [EnableRateLimiting(PasswordResetRateLimits.PolicyName)]
        public async Task<ApiResponse<PasswordResetConfirmResultDto>> Confirm([FromBody] PasswordResetConfirmDto dto)
        {
            NoStore();
            if (!ModelState.IsValid)
            {
                return ApiResponse<PasswordResetConfirmResultDto>.Error("请求参数验证失败", "VALIDATION_ERROR", ModelState);
            }
            return await _service.ConfirmAsync(dto.Email, dto.Code, dto.NewPassword, ResolveIp());
        }

        private string? ResolveIp() =>
            _clientIpResolver?.Resolve(HttpContext) ?? HttpContext.Connection.RemoteIpAddress?.ToString();

        private void NoStore()
        {
            Response.Headers.CacheControl = "no-store";
            Response.Headers["Referrer-Policy"] = "no-referrer";
        }
    }
}
