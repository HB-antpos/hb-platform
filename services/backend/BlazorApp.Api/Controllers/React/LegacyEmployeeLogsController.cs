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

    public LegacyEmployeeLogsController(LegacyEmployeeLogQueryService service)
    {
        _service = service;
    }

    [HttpGet]
    [Authorize(Policy = Permissions.LegacyEmployeeLogs.View)]
    public async Task<ActionResult<ApiResponse<LegacyEmployeeLogListResultDto>>> GetList(
        [FromQuery] LegacyEmployeeLogQueryDto request,
        CancellationToken cancellationToken
    ) => ToActionResult(await _service.QueryAsync(request, cancellationToken));

    /// <summary>单条日志及同设备前后 5 分钟内的操作。编号放在查询串里：旧数据的编号不保证是规范 GUID。</summary>
    [HttpGet("context")]
    [Authorize(Policy = Permissions.LegacyEmployeeLogs.View)]
    public async Task<ActionResult<ApiResponse<LegacyEmployeeLogContextDto>>> GetContext(
        [FromQuery] string? id,
        CancellationToken cancellationToken
    ) => ToActionResult(await _service.GetContextAsync(id, cancellationToken));

    private ActionResult<ApiResponse<T>> ToActionResult<T>(LegacyEmployeeLogResult<T> result) =>
        result.Status switch
        {
            LegacyEmployeeLogResultStatus.Ok => Ok(ApiResponse<T>.OK(result.Data!)),
            LegacyEmployeeLogResultStatus.Invalid =>
                BadRequest(ApiResponse<T>.Error(result.Message ?? "查询条件无效", "INVALID_QUERY")),
            LegacyEmployeeLogResultStatus.Forbidden =>
                StatusCode(StatusCodes.Status403Forbidden, ApiResponse<T>.Error(result.Message ?? "无权访问", "FORBIDDEN")),
            _ => NotFound(ApiResponse<T>.Error(result.Message ?? "操作日志不存在", "NOT_FOUND")),
        };
}
