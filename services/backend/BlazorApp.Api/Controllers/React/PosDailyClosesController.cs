using BlazorApp.Api.Models.DailyClose;
using BlazorApp.Api.Services.DailyCloses;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React;

/// <summary>
/// 日结记录只读查询：收银端（WPF、手持、iPad）上传的日结与现金盘点明细。
/// 权限用独立顶层码 DailyCloseRecords.View，不能挂在 Permissions.PosTerminal.* 下（该前缀会被当作收银机权限下发）。
/// 分店范围由查询服务按账号收口：管理员看全部，店长只看自己关联的分店。
/// </summary>
[ApiController]
[Route("api/react/v1/pos-daily-closes")]
[Authorize(Policy = Permissions.DailyCloseRecords.View)]
public sealed class PosDailyClosesController(IDailyCloseQueryService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<DailyCloseListResultDto>>> GetList(
        [FromQuery] DailyCloseQueryDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.GetListAsync(request, cancellationToken);
            return Ok(ApiResponse<DailyCloseListResultDto>.OK(result));
        }
        catch (DailyCloseRequestException exception)
        {
            return BadRequest(ApiResponse<DailyCloseListResultDto>.Error(exception.Message, exception.Code));
        }
    }

    /// <summary>记录不存在与无分店权限都返回 404，不泄露存在性。</summary>
    [HttpGet("{dailyCloseGuid:guid}")]
    public async Task<ActionResult<ApiResponse<DailyCloseDetailDto>>> GetDetail(
        Guid dailyCloseGuid,
        CancellationToken cancellationToken)
    {
        var result = await service.GetDetailAsync(dailyCloseGuid, cancellationToken);
        return result is null
            ? NotFound(ApiResponse<DailyCloseDetailDto>.Error("日结记录不存在", "NOT_FOUND"))
            : Ok(ApiResponse<DailyCloseDetailDto>.OK(result));
    }
}
