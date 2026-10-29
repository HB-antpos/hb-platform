using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// MYOB 工资单报表探针（临时接口）：用已授权公司文件只读核对 Report/Payroll/EmployeePayrollAdvice 的真实字段与筛选写法，
    /// 方案 docs/plans/2026-10-29-myob-payslip-history.md 第 1.3 节。只返回结构与计数，不含任何姓名 / 金额。
    /// 权限沿用 Myob.Connection.Manage；核对完成后整个控制器与服务一并删除。
    /// </summary>
    [ApiController]
    [Route("api/react/v1/myob/payslip-probe")]
    [Authorize(Policy = Permissions.Myob.Connection.Manage)]
    public class MyobPayslipProbeController : ControllerBase
    {
        private readonly IMyobPayslipProbeService _service;

        public MyobPayslipProbeController(IMyobPayslipProbeService service)
        {
            _service = service;
        }

        [HttpGet]
        public async Task<IActionResult> Run([FromQuery] string connectionGuid, CancellationToken cancellationToken)
        {
            // 结果虽然不含敏感取值，仍不让浏览器 / 中间层缓存。
            Response.Headers.CacheControl = "no-store";
            var result = await _service.RunAsync(connectionGuid ?? string.Empty, cancellationToken);
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
