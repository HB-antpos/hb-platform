using System.Security.Claims;
using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// 发薪公司设置（Web 页面 /pos-admin/myob「发薪公司」页签）：按「员工 × 上班分店」查看与修改由哪家公司发薪。
    /// 权限沿用已有的 Attendance.Payroll.Edit，并按当前账号可管理的分店过滤 / 校验。
    /// </summary>
    [ApiController]
    [Route("api/react/v1/myob/payroll-employers")]
    [Authorize(Policy = Permissions.Attendance.Payroll.Edit)]
    public class MyobPayrollEmployerController : ControllerBase
    {
        private readonly IMyobPayrollEmployerService _service;

        public MyobPayrollEmployerController(IMyobPayrollEmployerService service)
        {
            _service = service;
        }

        [HttpGet]
        public async Task<IActionResult> List(
            [FromQuery] string? keyword,
            [FromQuery] string? storeCode,
            [FromQuery] bool crossCompanyOnly,
            CancellationToken cancellationToken) =>
            ToResult(await _service.ListAsync(keyword, storeCode, crossCompanyOnly, cancellationToken));

        [HttpGet("{userGuid}")]
        public async Task<IActionResult> GetDetail(string userGuid, CancellationToken cancellationToken) =>
            ToResult(await _service.GetDetailAsync(userGuid, cancellationToken));

        [HttpPut("{userGuid}")]
        public async Task<IActionResult> Save(
            string userGuid,
            [FromBody] MyobSavePayrollEmployersRequest request,
            CancellationToken cancellationToken) =>
            ToResult(await _service.SaveAsync(
                userGuid, request ?? new MyobSavePayrollEmployersRequest(), CurrentUserGuid(), cancellationToken));

        private string CurrentUserGuid() =>
            User.FindFirst("userId")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? string.Empty;

        private IActionResult ToResult<T>(ApiResponse<T> result)
        {
            if (result.Success)
            {
                return Ok(result);
            }

            return result.ErrorCode == MyobPayrollErrorCodes.Forbidden ? StatusCode(StatusCodes.Status403Forbidden, result) : BadRequest(result);
        }
    }
}
