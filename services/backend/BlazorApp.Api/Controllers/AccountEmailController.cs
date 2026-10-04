using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BlazorApp.Api.Controllers
{
    /// <summary>
    /// 登录用户绑定 / 更换自己的账号邮箱（老账号补邮箱后即可在登录页用邮箱找回密码）。
    /// 只作用于当前登录账号，不需要额外权限码；与找回密码共用 IP 限流。
    /// </summary>
    [ApiController]
    [Route("api/Auth/email-change")]
    [Authorize]
    public sealed class AccountEmailController : ControllerBase
    {
        private readonly IAccountEmailChangeService _service;
        private readonly ICurrentUserService _currentUserService;
        private readonly IClientIpResolver? _clientIpResolver;

        public AccountEmailController(
            IAccountEmailChangeService service,
            ICurrentUserService currentUserService,
            IClientIpResolver? clientIpResolver = null
        )
        {
            _service = service;
            _currentUserService = currentUserService;
            _clientIpResolver = clientIpResolver;
        }

        [HttpPost("request")]
        [EnableRateLimiting(PasswordResetRateLimits.PolicyName)]
        public async Task<ApiResponse<PasswordSetupEmailResultDto>> RequestCode([FromBody] AccountEmailChangeRequestDto dto)
        {
            Response.Headers.CacheControl = "no-store";
            if (!ModelState.IsValid)
            {
                return ApiResponse<PasswordSetupEmailResultDto>.Error("请求参数验证失败", "VALIDATION_ERROR", ModelState);
            }
            var userGuid = _currentUserService.GetCurrentUserGuid();
            if (string.IsNullOrWhiteSpace(userGuid))
            {
                return ApiResponse<PasswordSetupEmailResultDto>.Error("未找到当前用户", "CURRENT_USER_NOT_FOUND");
            }
            return await _service.RequestAsync(userGuid, dto.NewEmail, ResolveIp());
        }

        [HttpPost("confirm")]
        [EnableRateLimiting(PasswordResetRateLimits.PolicyName)]
        public async Task<ApiResponse<AccountEmailChangeResultDto>> Confirm([FromBody] AccountEmailChangeConfirmDto dto)
        {
            Response.Headers.CacheControl = "no-store";
            if (!ModelState.IsValid)
            {
                return ApiResponse<AccountEmailChangeResultDto>.Error("请求参数验证失败", "VALIDATION_ERROR", ModelState);
            }
            var userGuid = _currentUserService.GetCurrentUserGuid();
            if (string.IsNullOrWhiteSpace(userGuid))
            {
                return ApiResponse<AccountEmailChangeResultDto>.Error("未找到当前用户", "CURRENT_USER_NOT_FOUND");
            }
            return await _service.ConfirmAsync(userGuid, dto.NewEmail, dto.Code);
        }

        private string? ResolveIp() =>
            _clientIpResolver?.Resolve(HttpContext) ?? HttpContext.Connection.RemoteIpAddress?.ToString();
    }
}
