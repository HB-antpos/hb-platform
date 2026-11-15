using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// MYOB EmployeePayrollDetails「Wage 节」只读探针（临时接口，核对完字段后整体删除）：
    /// 只发 GET，只返回字段路径 / 类型 / 计数与时薪汇总统计，不含姓名、UID、年薪；权限沿用 Myob.Connection.Manage。
    /// 目的：确认真实响应里基础时薪（base rate）放在哪个字段，再写「从 MYOB 获取 base rate」的正式解析。
    /// </summary>
    [ApiController]
    [Route("api/react/v1/myob/employee-payroll/wage-probe")]
    [Authorize(Policy = Permissions.Myob.Connection.Manage)]
    public class MyobWageProbeController : ControllerBase
    {
        private readonly IMyobWageProbeService _service;

        public MyobWageProbeController(IMyobWageProbeService service)
        {
            _service = service;
        }

        [HttpGet]
        public async Task<IActionResult> Run(
            [FromQuery] string connectionGuid,
            [FromQuery] string? employeeDisplayId,
            CancellationToken cancellationToken)
        {
            Response.Headers.CacheControl = "no-store";
            var result = await _service.RunAsync(connectionGuid ?? string.Empty, employeeDisplayId, cancellationToken);
            if (result.Success)
            {
                return Ok(result);
            }

            return result.ErrorCode is MyobErrorCodes.ConnectionNotFound or MyobErrorCodes.ConnectionNotActive
                ? NotFound(result)
                : BadRequest(result);
        }

        /// <summary>
        /// 工资类别（Payroll/PayrollCategory/Wage）只读探测：字段路径 + 各类别标量值（类别名、倍数等公司级设置），
        /// 不含 UID / URI / 个人数据。用来确认周末「倍数」的真实字段名。
        /// </summary>
        [HttpGet("categories")]
        public async Task<IActionResult> RunCategories(
            [FromQuery] string connectionGuid,
            CancellationToken cancellationToken)
        {
            Response.Headers.CacheControl = "no-store";
            var result = await _service.RunCategoriesAsync(connectionGuid ?? string.Empty, cancellationToken);
            if (result.Success)
            {
                return Ok(result);
            }

            return result.ErrorCode is MyobErrorCodes.ConnectionNotFound or MyobErrorCodes.ConnectionNotActive
                ? NotFound(result)
                : BadRequest(result);
        }
    }
}
