using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Models.Linkly;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React;

[ApiController]
[Route("api/react/v1/linkly-settlements")]
[Authorize(Roles = "Admin,管理员,SuperAdmin,超级管理员")]
public sealed class LinklySettlementsController(ILinklySettlementQueryService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedListReactDto<LinklySettlementListItemDto>>>> GetList(
        [FromQuery] LinklySettlementQueryDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.GetListAsync(request, cancellationToken);
            return Ok(ApiResponse<PagedListReactDto<LinklySettlementListItemDto>>.OK(result));
        }
        catch (LinklySettlementRequestException exception)
        {
            return BadRequest(ApiResponse<PagedListReactDto<LinklySettlementListItemDto>>.Error(
                exception.Message,
                exception.Code));
        }
    }

    [HttpGet("devices")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<LinklySettlementDeviceOptionDto>>>> GetDeviceOptions(
        CancellationToken cancellationToken)
    {
        var result = await service.GetDeviceOptionsAsync(cancellationToken);
        return Ok(ApiResponse<IReadOnlyList<LinklySettlementDeviceOptionDto>>.OK(result));
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<ApiResponse<LinklySettlementDetailDto>>> GetDetail(
        long id,
        CancellationToken cancellationToken)
    {
        var result = await service.GetDetailAsync(id, cancellationToken);
        return result is null
            ? NotFound(ApiResponse<LinklySettlementDetailDto>.Error("Linkly 结算记录不存在。", "NOT_FOUND"))
            : Ok(ApiResponse<LinklySettlementDetailDto>.OK(result));
    }

    [HttpPost("export")]
    public async Task<IActionResult> Export(
        [FromBody] LinklySettlementQueryDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.ExportAsync(request, cancellationToken);
            return File(result.Content, result.ContentType, result.FileName);
        }
        catch (LinklySettlementExportChangedException exception)
        {
            return Conflict(ApiResponse<object>.Error(exception.Message, exception.Code));
        }
        catch (LinklySettlementRequestException exception)
        {
            return BadRequest(ApiResponse<object>.Error(exception.Message, exception.Code));
        }
    }
}
