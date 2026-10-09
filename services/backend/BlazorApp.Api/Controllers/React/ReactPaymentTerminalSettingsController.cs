using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    [ApiController]
    [Route("api/react/v1/payment-terminal-settings")]
    [Authorize]
    public class ReactPaymentTerminalSettingsController : ControllerBase
    {
        private readonly PaymentTerminalSettingsService _settingsService;
        private readonly ICurrentUserService _currentUserService;

        public ReactPaymentTerminalSettingsController(
            PaymentTerminalSettingsService settingsService,
            ICurrentUserService currentUserService
        )
        {
            _settingsService = settingsService;
            _currentUserService = currentUserService;
        }

        [HttpGet]
        [Authorize(Policy = Permissions.System.ManageSettings)]
        public async Task<IActionResult> Get(
            [FromQuery] string? storeCode,
            CancellationToken cancellationToken
        )
        {
            var result = await _settingsService.GetSettingsAsync(storeCode, cancellationToken);
            return Ok(result);
        }

        [HttpPut("square")]
        [Authorize(Policy = Permissions.System.ManageSettings)]
        public async Task<IActionResult> UpdateSquare(
            [FromBody] UpdateSquareTokenDto request,
            [FromQuery] string? storeCode,
            CancellationToken cancellationToken
        )
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ApiResponse<object>.Error("请求参数验证失败", "VALIDATION_ERROR", ModelState));
            }

            var result = await _settingsService.UpdateSquareTokenAsync(
                request,
                _currentUserService.GetCurrentUsername(),
                storeCode,
                cancellationToken
            );
            return result.Success ? Ok(result) : BadRequest(result);
        }

        [HttpGet("linkly-terminals")]
        [Authorize(Policy = Permissions.System.ManageSettings)]
        public async Task<IActionResult> GetLinklyTerminals(
            [FromQuery] string? storeCode,
            [FromQuery] string? environment,
            CancellationToken cancellationToken
        )
        {
            var result = await _settingsService.GetLinklyTerminalManagementAsync(
                storeCode,
                environment,
                cancellationToken
            );
            return result.Success ? Ok(result) : BadRequest(result);
        }

        [HttpPost("linkly-terminals")]
        [Authorize(Policy = Permissions.System.ManageSettings)]
        public async Task<IActionResult> CreateLinklyTerminal(
            [FromBody] CreateLinklyTerminalDto request,
            CancellationToken cancellationToken
        )
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ApiResponse<object>.Error("请求参数验证失败", "VALIDATION_ERROR", ModelState));
            }

            var result = await _settingsService.CreateLinklyTerminalAsync(
                request,
                _currentUserService.GetCurrentUsername(),
                cancellationToken
            );
            return ToLinklyResult(result);
        }

        [HttpPut("linkly-terminals/{terminalId:guid}")]
        [Authorize(Policy = Permissions.System.ManageSettings)]
        public async Task<IActionResult> UpdateLinklyTerminal(
            Guid terminalId,
            [FromBody] UpdateLinklyTerminalDto request,
            CancellationToken cancellationToken
        )
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ApiResponse<object>.Error("请求参数验证失败", "VALIDATION_ERROR", ModelState));
            }

            var result = await _settingsService.UpdateLinklyTerminalAsync(
                terminalId,
                request,
                _currentUserService.GetCurrentUsername(),
                cancellationToken
            );
            return ToLinklyResult(result);
        }

        [HttpPut("linkly-device-selections/{deviceCode}")]
        [Authorize(Policy = Permissions.System.ManageSettings)]
        public async Task<IActionResult> UpdateLinklyDeviceSelection(
            string deviceCode,
            [FromBody] UpdateLinklyDeviceSelectionDto request,
            CancellationToken cancellationToken
        )
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ApiResponse<object>.Error("请求参数验证失败", "VALIDATION_ERROR", ModelState));
            }

            var result = await _settingsService.SetLinklyDeviceSelectionAsync(
                deviceCode,
                request,
                _currentUserService.GetCurrentUsername(),
                cancellationToken
            );
            return ToLinklyResult(result);
        }

        [HttpDelete("linkly-device-selections/{deviceCode}")]
        [Authorize(Policy = Permissions.System.ManageSettings)]
        public async Task<IActionResult> DeleteLinklyDeviceSelection(
            string deviceCode,
            [FromBody] DeleteLinklyDeviceSelectionDto request,
            CancellationToken cancellationToken
        )
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ApiResponse<object>.Error("请求参数验证失败", "VALIDATION_ERROR", ModelState));
            }

            var result = await _settingsService.DeleteLinklyDeviceSelectionAsync(
                deviceCode,
                request,
                _currentUserService.GetCurrentUsername(),
                cancellationToken
            );
            return ToLinklyResult(result);
        }

        [HttpPost("linkly-activation")]
        [Authorize(Policy = Permissions.System.ManageSettings)]
        public async Task<IActionResult> ActivateLinklyConfiguration(
            [FromBody] ActivateLinklyConfigurationDto request,
            CancellationToken cancellationToken
        )
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ApiResponse<object>.Error("请求参数验证失败", "VALIDATION_ERROR", ModelState));
            }

            var result = await _settingsService.ActivateLinklyConfigurationAsync(
                request,
                _currentUserService.GetCurrentUsername(),
                cancellationToken
            );
            return ToLinklyResult(result);
        }

        private IActionResult ToLinklyResult(ApiResponse<LinklyTerminalManagementDto> result)
        {
            if (result.Success)
            {
                return Ok(result);
            }

            return result.ErrorCode is "LINKLY_TERMINAL_LANE_CONFLICT"
                or "LINKLY_TERMINAL_USERNAME_CONFLICT"
                or "LINKLY_TERMINAL_DISPLAY_NAME_CONFLICT"
                or "LINKLY_TERMINAL_CONFLICT"
                or "LINKLY_TERMINAL_SESSION_ACTIVE"
                or "LINKLY_TERMINAL_REVISION_CONFLICT"
                or "LINKLY_SELECTION_REVISION_CONFLICT"
                or "LINKLY_DEVICE_SELECTION_RELEASE_NOT_ALLOWED"
                or "LINKLY_TERMINAL_ASSIGNMENT_CONFLICT"
                or "LINKLY_CLOUD_LEGACY_PAIRING_IN_PROGRESS"
                or "LINKLY_READY_TERMINAL_REQUIRED"
                or "LINKLY_DEVICE_SELECTION_REQUIRED"
                ? Conflict(result)
                : BadRequest(result);
        }
    }
}
