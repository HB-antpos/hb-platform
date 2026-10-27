using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// 已启用的 MYOB 公司文件列表（只含公司名与 ABN，没有任何授权信息 / 错误详情）。
    /// MYOB 页的「员工绑定」「发薪公司」页签要用它做公司选择，但这两个页签的使用者不一定有 Myob.Connection.Manage，
    /// 所以单独开一个只读接口：连接管理、员工绑定、发薪公司设置三个权限任一即可。
    /// </summary>
    [ApiController]
    [Route("api/react/v1/myob/companies")]
    [Authorize]
    public class MyobCompanyOptionsController : ControllerBase
    {
        private static readonly string[] AllowedPermissions =
        {
            Permissions.Myob.Connection.Manage,
            Permissions.Myob.Employee.Bind,
            Permissions.Attendance.Payroll.Edit,
        };

        private readonly IMyobPayrollEmployerService _service;
        private readonly IAuthorizationService _authorization;

        public MyobCompanyOptionsController(IMyobPayrollEmployerService service, IAuthorizationService authorization)
        {
            _service = service;
            _authorization = authorization;
        }

        [HttpGet]
        public async Task<IActionResult> List()
        {
            foreach (var policy in AllowedPermissions)
            {
                if ((await _authorization.AuthorizeAsync(User, null, policy)).Succeeded)
                {
                    return Ok(ApiResponse<List<MyobPayrollCompanyOptionDto>>.OK(await _service.ListCompaniesAsync()));
                }
            }

            return Forbid();
        }
    }
}
