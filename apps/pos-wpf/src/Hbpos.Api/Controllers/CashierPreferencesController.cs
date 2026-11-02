using Hbpos.Api.Auth;
using Hbpos.Api.Services;
using Hbpos.Contracts.Cashiers;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hbpos.Api.Controllers;

/// <summary>
/// 当前收银员的个人界面偏好。员工只取自收银员票据（设备认证之外再校验票据的门店/设备范围），只能读写自己的偏好。
/// 偏好表缺失或数据库异常时返回 503 + CASHIER_PREFERENCES_UNAVAILABLE：收银端据此沿用本机主题，不弹首次选择窗。
/// </summary>
[ApiController]
[Route("api/v1/cashiers/me/preferences")]
public sealed class CashierPreferencesController(
    ICashierAuthorizationTicketService ticketService,
    ICashierPreferenceStore preferenceStore,
    TimeProvider timeProvider,
    ILogger<CashierPreferencesController> logger) : ControllerBase
{
    internal const string UnavailableErrorCode = "CASHIER_PREFERENCES_UNAVAILABLE";

    [Authorize]
    [HttpGet("color-theme")]
    public async Task<ActionResult<ApiResult<CashierColorThemePreferenceDto>>> GetColorTheme(
        CancellationToken cancellationToken)
    {
        var ticket = ValidateTicket(out var rejected);
        if (ticket is null)
        {
            return rejected!;
        }

        string? stored;
        try
        {
            stored = await preferenceStore.GetAsync(ticket.UserGuid, CashierPreferenceKeys.WpfColorTheme, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Cashier color theme preference read failed. userGuid={UserGuid}", ticket.UserGuid);
            return Unavailable();
        }

        // 库里若有旧版或手工写入的未知值，按「未设置」返回，由客户端走首次选择。
        return Ok(ApiResult<CashierColorThemePreferenceDto>.Ok(
            new CashierColorThemePreferenceDto(ticket.UserGuid, CashierColorThemes.Normalize(stored))));
    }

    [Authorize]
    [HttpPut("color-theme")]
    public async Task<ActionResult<ApiResult<CashierColorThemePreferenceDto>>> SaveColorTheme(
        [FromBody] CashierColorThemePreferenceUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var ticket = ValidateTicket(out var rejected);
        if (ticket is null)
        {
            return rejected!;
        }

        // 切换收银员后迟到的旧请求带的是上一位员工：拒绝，绝不写到当前票据的员工名下。
        if (!string.Equals(request.UserGuid?.Trim(), ticket.UserGuid, StringComparison.OrdinalIgnoreCase))
        {
            return Conflict(ApiResult<CashierColorThemePreferenceDto>.Fail(
                "CASHIER_PREFERENCE_USER_MISMATCH",
                "偏好所属员工与当前收银员不一致"));
        }

        var theme = CashierColorThemes.Normalize(request.ColorTheme);
        if (theme is null)
        {
            return BadRequest(ApiResult<CashierColorThemePreferenceDto>.Fail(
                "CASHIER_COLOR_THEME_INVALID",
                "不支持的界面配色"));
        }

        try
        {
            await preferenceStore.SetAsync(
                ticket.UserGuid,
                CashierPreferenceKeys.WpfColorTheme,
                theme,
                timeProvider.GetUtcNow().UtcDateTime,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Cashier color theme preference save failed. userGuid={UserGuid}", ticket.UserGuid);
            return Unavailable();
        }

        return Ok(ApiResult<CashierColorThemePreferenceDto>.Ok(
            new CashierColorThemePreferenceDto(ticket.UserGuid, theme)));
    }

    private CashierAuthorizationTicket? ValidateTicket(
        out ActionResult<ApiResult<CashierColorThemePreferenceDto>>? rejected)
    {
        var token = Request.Headers[CashierAuthorizationConstants.HeaderName].ToString();
        var ticket = ticketService.Validate(token);
        if (ticket is null)
        {
            rejected = Unauthorized(ApiResult<CashierColorThemePreferenceDto>.Fail(
                "CASHIER_SESSION_INVALID",
                "收银员会话已失效，请重新登录"));
            return null;
        }

        if (!this.IsDeviceScopeAllowed(ticket.StoreCode, ticket.DeviceCode))
        {
            rejected = DeviceAuthorizationExtensions.DeviceScopeForbidden<CashierColorThemePreferenceDto>(
                "Device is not authorized for this store.");
            return null;
        }

        rejected = null;
        return ticket;
    }

    private ObjectResult Unavailable() =>
        StatusCode(
            StatusCodes.Status503ServiceUnavailable,
            ApiResult<CashierColorThemePreferenceDto>.Fail(UnavailableErrorCode, "员工偏好暂不可用"));
}
