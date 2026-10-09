using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React;

/// <summary>
/// 卡付款对账异常（只读）：订单入库校验与「已批准会话无订单」对账作业登记的异常，
/// 供运维排查孤儿扣款与异常卡 tender。处理（Resolved/Dismissed）目前通过 SQL 完成。
/// </summary>
[ApiController]
[Route("api/react/v1/card-tender-reconciliation")]
[Authorize(Roles = "Admin,管理员,SuperAdmin,超级管理员")]
public sealed class CardTenderReconciliationController(ICardTenderReconciliationQueryService service) : ControllerBase
{
    [HttpGet("issues")]
    public async Task<ActionResult<ApiResponse<PagedListReactDto<CardTenderReconciliationIssueDto>>>> GetIssues(
        [FromQuery] CardTenderReconciliationQueryDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.GetListAsync(request, cancellationToken);
            return Ok(ApiResponse<PagedListReactDto<CardTenderReconciliationIssueDto>>.OK(result));
        }
        catch (CardTenderReconciliationRequestException exception)
        {
            return BadRequest(ApiResponse<PagedListReactDto<CardTenderReconciliationIssueDto>>.Error(
                exception.Message,
                exception.Code));
        }
    }
}
