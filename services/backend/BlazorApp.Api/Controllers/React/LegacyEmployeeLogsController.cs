using BlazorApp.Api.Features.LegacyEmployeeLogs;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React;

/// <summary>老系统（旧版收银）员工操作日志只读查询，数据源 POSM.dbo.EmployeeLogs。</summary>
[ApiController]
[Route("api/react/legacy-employee-logs")]
[Authorize]
public sealed class LegacyEmployeeLogsController : ControllerBase
{
    private readonly LegacyEmployeeLogQueryService _service;
    private readonly LegacyEmployeeLogReviewService _reviewService;

    public LegacyEmployeeLogsController(LegacyEmployeeLogQueryService service, LegacyEmployeeLogReviewService reviewService)
    {
        _service = service;
        _reviewService = reviewService;
    }

    [HttpGet]
    [Authorize(Policy = Permissions.LegacyEmployeeLogs.View)]
    public async Task<ActionResult<ApiResponse<LegacyEmployeeLogListResultDto>>> GetList(
        [FromQuery] LegacyEmployeeLogQueryDto request,
        CancellationToken cancellationToken
    ) => ToActionResult(await _service.QueryAsync(request, cancellationToken));

    /// <summary>单条日志及同设备前后若干分钟（默认 5，可选 10、15）内的操作。编号放在查询串里：旧数据的编号不保证是规范 GUID。</summary>
    [HttpGet("context")]
    [Authorize(Policy = Permissions.LegacyEmployeeLogs.View)]
    public async Task<ActionResult<ApiResponse<LegacyEmployeeLogContextDto>>> GetContext(
        [FromQuery] string? id,
        [FromQuery] int? windowMinutes,
        CancellationToken cancellationToken
    ) => ToActionResult(await _service.GetContextAsync(id, cancellationToken, windowMinutes));

    /// <summary>按员工汇总：操作数、危险数、异常与待核查数、金额让利。</summary>
    [HttpGet("employee-summary")]
    [Authorize(Policy = Permissions.LegacyEmployeeLogs.View)]
    public async Task<ActionResult<ApiResponse<LegacyEmployeeLogEmployeeSummaryResultDto>>> GetEmployeeSummary(
        [FromQuery] LegacyEmployeeLogEmployeeSummaryQueryDto request,
        CancellationToken cancellationToken
    ) => ToActionResult(await _service.GetEmployeeSummaryAsync(request, cancellationToken));

    /// <summary>核查一条异常操作：确认正常 / 需跟进 / 撤销。版本号不符返回 409。</summary>
    [HttpPost("reviews")]
    [Authorize(Policy = Permissions.LegacyEmployeeLogs.Review)]
    public async Task<ActionResult<ApiResponse<LegacyEmployeeLogReviewDto>>> Review(
        [FromBody] LegacyEmployeeLogReviewRequestDto request,
        CancellationToken cancellationToken
    ) => ToActionResult(await _reviewService.ReviewAsync(request, cancellationToken));

    private ActionResult<ApiResponse<T>> ToActionResult<T>(LegacyEmployeeLogResult<T> result) =>
        result.Status switch
        {
            LegacyEmployeeLogResultStatus.Ok => Ok(ApiResponse<T>.OK(result.Data!)),
            LegacyEmployeeLogResultStatus.Invalid =>
                BadRequest(ApiResponse<T>.Error(result.Message ?? "查询条件无效", "INVALID_QUERY")),
            LegacyEmployeeLogResultStatus.Forbidden =>
                StatusCode(StatusCodes.Status403Forbidden, ApiResponse<T>.Error(result.Message ?? "无权访问", "FORBIDDEN")),
            LegacyEmployeeLogResultStatus.Conflict =>
                Conflict(ApiResponse<T>.Error(result.Message ?? "记录已被修改", "REVIEW_CONFLICT")),
            _ => NotFound(ApiResponse<T>.Error(result.Message ?? "操作日志不存在", "NOT_FOUND")),
        };
}
