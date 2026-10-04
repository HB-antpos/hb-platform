using BlazorApp.Api.Services;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React;

[ApiController]
[Route("api/react/attendance/minor-reminders")]
[Authorize]
public sealed class MinorEmploymentRemindersController(MinorEmploymentReminderService service) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = Permissions.Attendance.Schedule.ViewStore)]
    public Task<ApiResponse<PagedResult<EmployeeMinorReminderDto>>> List([FromQuery] string? storeCode, [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 30) => service.ListAsync(storeCode, status, page, pageSize);

    [HttpPost("{id:int}/action")]
    [Authorize(Policy = Permissions.Attendance.Schedule.EditManagedStore)]
    public Task<ApiResponse<EmployeeMinorReminderDto>> Action(int id, [FromBody] EmployeeMinorReminderActionDto dto) => service.ActAsync(id, dto);
}
