using BlazorApp.Api.Services;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers
{
    /// <summary>
    /// 员工个人信息维护（Web 页面 / 用户管理抽屉 / 移动端共用）。
    /// 权限：EmployeeProfiles.ViewManagedStore 看、EmployeeProfiles.EditManagedStore 改；
    /// 数据范围（管理员全部分店 / 店长限管理分店 / 排除本人与受保护角色）由 EmployeeProfileManagedService 在后端强制。
    /// </summary>
    [ApiController]
    [Route("api/EmployeeProfiles/managed")]
    [Route("api/employee-profiles/managed")]
    [Authorize]
    public class EmployeeProfilesManagedController : ControllerBase
    {
        private readonly EmployeeProfileManagedService _service;

        public EmployeeProfilesManagedController(EmployeeProfileManagedService service)
        {
            _service = service;
        }

        [HttpGet]
        [EmployeeProfileSensitiveRoles(includeStoreManagers: true)]
        [Authorize(Policy = Permissions.EmployeeProfiles.ViewManagedStore)]
        public async Task<IActionResult> GetList([FromQuery] EmployeeProfileQueryDto query) =>
            MapResult(await _service.GetListAsync(query));

        [HttpGet("filter-options")]
        [EmployeeProfileSensitiveRoles(includeStoreManagers: true)]
        [Authorize(Policy = Permissions.EmployeeProfiles.ViewManagedStore)]
        public async Task<IActionResult> GetFilterOptions() =>
            MapResult(await _service.GetFilterOptionsAsync());

        [HttpGet("{userGuid}")]
        [EmployeeProfileSensitiveRoles(includeStoreManagers: true)]
        [Authorize(Policy = Permissions.EmployeeProfiles.ViewManagedStore)]
        public async Task<IActionResult> GetDetail(string userGuid) =>
            MapResult(await _service.GetDetailAsync(userGuid));

        [HttpPut("{userGuid}")]
        [EmployeeProfileSensitiveRoles(includeStoreManagers: true)]
        [Authorize(Policy = Permissions.EmployeeProfiles.EditManagedStore)]
        public async Task<IActionResult> Update(
            string userGuid,
            [FromBody] EmployeeProfileManagedUpsertDto dto
        )
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(
                    ApiResponse<EmployeeProfileManagedDetailDto>.Error(
                        "请求参数验证失败",
                        "VALIDATION_ERROR",
                        ModelState
                    )
                );
            }
            return MapResult(await _service.UpdateAsync(userGuid, dto));
        }

        // 范围 / 职位类型越权 → 403；用户不存在 → 404；其余沿用 200 + ApiResponse 的既有约定。
        private IActionResult MapResult<T>(ApiResponse<T> result) => result.ErrorCode switch
        {
            EmployeeProfileManagedService.ScopeForbiddenCode =>
                StatusCode(StatusCodes.Status403Forbidden, result),
            EmployeeProfileManagedService.PositionTypeForbiddenCode =>
                StatusCode(StatusCodes.Status403Forbidden, result),
            "USER_NOT_FOUND" => NotFound(result),
            _ => Ok(result),
        };
    }
}
