using BlazorApp.Api.Features.LegacyEmployeeLogs;
using BlazorApp.Api.Services.OperationAudits;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React;

[ApiController]
[Route("api/react/pos-operation-audits")]
[Authorize(Policy = Permissions.PosTerminal.Audit.View)]
public sealed class PosOperationAuditController : ControllerBase
{
    private readonly OperationAuditQueryService _service;
    private readonly OperationAuditReviewService _reviewService;

    public PosOperationAuditController(OperationAuditQueryService service, OperationAuditReviewService reviewService)
    {
        _service = service;
        _reviewService = reviewService;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedListReactDto<OperationAuditListItemDto>>>> GetList(
        [FromQuery] OperationAuditQueryDto request
    )
    {
        var result = await _service.QueryAsync(request);
        return Ok(ApiResponse<PagedListReactDto<OperationAuditListItemDto>>.OK(result));
    }

    /// <summary>
    /// 汇总计数接口：沿用列表的门店权限与基础筛选，但忽略 Outcome / IsEmergencyOverride / IsOfflineCached，
    /// 供移动端展示“快捷过滤”入口的基数。
    /// </summary>
    [HttpGet("summary")]
    public async Task<ActionResult<ApiResponse<OperationAuditSummaryDto>>> GetSummary(
        [FromQuery] OperationAuditQueryDto request
    )
    {
        var result = await _service.GetSummaryAsync(request);
        return Ok(ApiResponse<OperationAuditSummaryDto>.OK(result));
    }

    /// <summary>按收银员汇总：操作数、危险数、异常与待核查数、金额让利。只套用分店、时间、设备与关键字等基础条件。</summary>
    [HttpGet("employee-summary")]
    public async Task<ActionResult<ApiResponse<OperationAuditEmployeeSummaryResultDto>>> GetEmployeeSummary(
        [FromQuery] OperationAuditQueryDto request
    )
    {
        var result = await _service.GetEmployeeSummaryAsync(request);
        return Ok(ApiResponse<OperationAuditEmployeeSummaryResultDto>.OK(result));
    }

    /// <summary>单条事件及同设备前后若干分钟（默认 5，可选 10、15）内的操作。</summary>
    [HttpGet("{eventId:guid}/context")]
    public async Task<ActionResult<ApiResponse<OperationAuditContextDto>>> GetContext(
        Guid eventId,
        [FromQuery] int? windowMinutes
    )
    {
        var result = await _service.GetContextAsync(eventId, windowMinutes);
        return result.Status switch
        {
            OperationAuditDetailAccessStatus.Found => Ok(ApiResponse<OperationAuditContextDto>.OK(result.Data!)),
            OperationAuditDetailAccessStatus.Forbidden => Forbid(),
            _ => NotFound(ApiResponse<OperationAuditContextDto>.Error("操作日志不存在", "NOT_FOUND")),
        };
    }

    /// <summary>
    /// 核查一条异常操作：确认正常 / 需跟进 / 撤销，版本号不符返回 409。
    /// 与老收银共用「核查员工操作日志异常」权限（LegacyEmployeeLogs.Review）：
    /// 新权限不能挂在 Permissions.PosTerminal.* 下，该前缀会被当作收银端权限下发到收银机。
    /// </summary>
    [HttpPost("reviews")]
    [Authorize(Policy = Permissions.LegacyEmployeeLogs.Review)]
    public async Task<ActionResult<ApiResponse<OperationAuditReviewDto>>> Review(
        [FromBody] OperationAuditReviewRequestDto request
    )
    {
        var result = await _reviewService.ReviewAsync(request);
        return result.Status switch
        {
            LegacyEmployeeLogResultStatus.Ok => Ok(ApiResponse<OperationAuditReviewDto>.OK(result.Data!)),
            LegacyEmployeeLogResultStatus.Invalid =>
                BadRequest(ApiResponse<OperationAuditReviewDto>.Error(result.Message ?? "核查参数无效", "INVALID_QUERY")),
            LegacyEmployeeLogResultStatus.Forbidden =>
                StatusCode(StatusCodes.Status403Forbidden, ApiResponse<OperationAuditReviewDto>.Error(result.Message ?? "无权访问", "FORBIDDEN")),
            LegacyEmployeeLogResultStatus.Conflict =>
                Conflict(ApiResponse<OperationAuditReviewDto>.Error(result.Message ?? "记录已被修改", "REVIEW_CONFLICT")),
            _ => NotFound(ApiResponse<OperationAuditReviewDto>.Error(result.Message ?? "操作日志不存在", "NOT_FOUND")),
        };
    }

    [HttpGet("{eventId:guid}")]
    public async Task<ActionResult<ApiResponse<OperationAuditDetailDto>>> GetDetail(Guid eventId)
    {
        var result = await _service.GetDetailAsync(eventId);
        return result.Status switch
        {
            OperationAuditDetailAccessStatus.Found =>
                Ok(ApiResponse<OperationAuditDetailDto>.OK(result.Data!)),
            OperationAuditDetailAccessStatus.Forbidden => Forbid(),
            _ => NotFound(ApiResponse<OperationAuditDetailDto>.Error("操作日志不存在", "NOT_FOUND")),
        };
    }
}
