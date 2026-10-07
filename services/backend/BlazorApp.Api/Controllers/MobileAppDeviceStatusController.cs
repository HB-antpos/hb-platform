using System.Security.Claims;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers
{
    [ApiController]
    [Route("api/mobile/app-device-status")]
    public class MobileAppDeviceStatusController : ControllerBase
    {
        private readonly MobileAppDeviceStatusService _statusService;
        private readonly IDeviceRegistrationService _deviceRegistrationService;
        private readonly SqlSugarContext _dbContext;
        private readonly ILogger<MobileAppDeviceStatusController> _logger;

        public MobileAppDeviceStatusController(
            MobileAppDeviceStatusService statusService,
            IDeviceRegistrationService deviceRegistrationService,
            SqlSugarContext dbContext,
            ILogger<MobileAppDeviceStatusController> logger
        )
        {
            _statusService = statusService;
            _deviceRegistrationService = deviceRegistrationService;
            _dbContext = dbContext;
            _logger = logger;
        }

        [HttpPost("heartbeat")]
        [AllowAnonymous]
        public async Task<IActionResult> Heartbeat([FromBody] MobileAppDeviceHeartbeatDto dto)
        {
            var authContext = await ResolveHeartbeatAuthContextAsync(dto);
            if (authContext == null)
            {
                return Unauthorized(
                    ApiResponse<object>.Error("未授权的 App 设备心跳", "UNAUTHORIZED")
                );
            }

            var result = await _statusService.UpsertHeartbeatAsync(dto, authContext);
            if (!result.Success)
            {
                return BadRequest(result);
            }

            return Ok(result);
        }

        [HttpGet("paged")]
        [Authorize(Policy = Permissions.DeviceRegistration.View)]
        public async Task<IActionResult> GetPaged([FromQuery] MobileAppDeviceStatusQueryDto query)
        {
            var result = await _statusService.GetPagedAsync(query);
            return Ok(result);
        }

        [HttpGet("summary")]
        [Authorize(Policy = Permissions.DeviceRegistration.View)]
        public async Task<IActionResult> GetSummary([FromQuery] MobileAppDeviceStatusQueryDto query)
        {
            var result = await _statusService.GetSummaryAsync(query);
            return Ok(result);
        }

        [HttpGet("version-distribution")]
        [Authorize(Policy = Permissions.DeviceRegistration.View)]
        public async Task<IActionResult> GetVersionDistribution(
            [FromQuery] MobileAppDeviceStatusQueryDto query
        )
        {
            var result = await _statusService.GetVersionDistributionAsync(query);
            return Ok(result);
        }

        private async Task<MobileAppDeviceHeartbeatAuthContext?> ResolveHeartbeatAuthContextAsync(
            MobileAppDeviceHeartbeatDto dto
        )
        {
            var isDeviceSessionVerified = await ValidateDeviceSessionAsync(dto);
            if (User.Identity?.IsAuthenticated == true)
            {
                return await ResolveBearerAuthContextAsync(isDeviceSessionVerified);
            }

            return isDeviceSessionVerified
                ? new MobileAppDeviceHeartbeatAuthContext("device", null, null, null, true)
                : null;
        }

        private async Task<MobileAppDeviceHeartbeatAuthContext?> ResolveBearerAuthContextAsync(
            bool isDeviceSessionVerified
        )
        {
            var userGuid = GetUserGuid(User);
            if (string.IsNullOrWhiteSpace(userGuid))
            {
                _logger.LogWarning("App 设备心跳 Bearer token 缺少用户标识");
                return null;
            }

            var user = await _dbContext.Db.Queryable<User>()
                .FirstAsync(item => item.UserGUID == userGuid && !item.IsDeleted);
            if (user == null)
            {
                _logger.LogWarning("App 设备心跳用户不存在或已删除: {UserGuid}", userGuid);
                return null;
            }

            // 关键逻辑：最近登录用户只信服务端 token 和用户表，客户端 payload 不允许传用户身份。
            return new MobileAppDeviceHeartbeatAuthContext(
                isDeviceSessionVerified ? "bearer-device" : "bearer",
                user.UserGUID,
                user.Username,
                user.FullName,
                isDeviceSessionVerified
            );
        }

        private async Task<bool> ValidateDeviceSessionAsync(
            MobileAppDeviceHeartbeatDto dto
        )
        {
            var headerHardwareId = Request.Headers["X-Device-Id"].FirstOrDefault();
            var authCode = Request.Headers["X-Auth-Code"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(headerHardwareId) || string.IsNullOrWhiteSpace(authCode))
            {
                return false;
            }

            if (
                !string.Equals(
                    headerHardwareId.Trim(),
                    dto.HardwareId?.Trim(),
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                _logger.LogWarning(
                    "App 设备心跳硬件 ID 与设备会话不一致，Header: {HeaderHardwareId}, Payload: {PayloadHardwareId}",
                    headerHardwareId,
                    dto.HardwareId
                );
                return false;
            }

            return await _deviceRegistrationService.ValidateDeviceAuthCodeAsync(
                headerHardwareId,
                authCode
            );
        }

        private static string? GetUserGuid(ClaimsPrincipal user)
        {
            return user.FindFirst("userId")?.Value
                ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? user.FindFirst("userGuid")?.Value
                ?? user.FindFirst("uid")?.Value
                ?? user.FindFirst(ClaimTypes.Name)?.Value
                ?? user.FindFirst("sub")?.Value;
        }
    }
}
