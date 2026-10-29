using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// MYOB 历史工资单（Web 页面 /pos-admin/myob「工资单」页签，只读，不会往 MYOB 写任何东西）。
    /// 权限在服务里按 Myob.Payslip.ViewAllStores / Myob.Payslip.ViewManagedStore 判断（任一满足即可，范围不同），
    /// 每个接口都重新判断，不靠前端隐藏；控制器层只要求已登录。工资单含薪资等敏感信息：响应一律 no-store。
    /// </summary>
    [ApiController]
    [Route("api/react/v1/myob/payslips")]
    [Authorize]
    public class MyobPayslipController : ControllerBase
    {
        private readonly IMyobPayslipService _service;

        public MyobPayslipController(IMyobPayslipService service)
        {
            _service = service;
        }

        /// <summary>当前账号可见的公司文件下拉。</summary>
        [HttpGet("companies")]
        public async Task<IActionResult> GetCompanies(CancellationToken cancellationToken)
        {
            NoStore();
            return ToResult(await _service.GetCompaniesAsync(cancellationToken));
        }

        /// <summary>
        /// 工资单列表：from / to 按支付日期（含首尾，默认最近 90 天，最长 24 个月）；connectionGuid 为空 = 所有可见公司；
        /// storeCode 按分店过滤；onlyUnassigned 只看无法归到分店的（仅全部分店权限）；refresh=true 绕过 10 分钟缓存。
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> List([FromQuery] MyobPayslipQuery query, CancellationToken cancellationToken)
        {
            NoStore();
            return ToResult(await _service.ListAsync(query ?? new MyobPayslipQuery(), cancellationToken));
        }

        /// <summary>单张工资单明细；成功打开会写一条查看记录（只记谁看了谁哪一期，不含金额）。</summary>
        [HttpGet("detail")]
        public async Task<IActionResult> GetDetail([FromQuery] MyobPayslipDetailRequest request, CancellationToken cancellationToken)
        {
            NoStore();
            return ToResult(await _service.GetDetailAsync(request ?? new MyobPayslipDetailRequest(), cancellationToken));
        }

        /// <summary>这张工资单对应周期里「我方考勤」按分店的计薪工时（仅供参考，抽屉里点击后才请求；不写查看记录）。</summary>
        [HttpGet("hours")]
        public async Task<IActionResult> GetHours([FromQuery] MyobPayslipDetailRequest request, CancellationToken cancellationToken)
        {
            NoStore();
            return ToResult(await _service.GetHoursAsync(request ?? new MyobPayslipDetailRequest(), cancellationToken));
        }

        /// <summary>
        /// 下载 MYOB 默认样式的工资单 PDF（需 Myob.Payslip.Download，且看得到这张工资单；会写一条 Download 查看记录）。
        /// 成功返回文件流；失败返回 JSON（403 / 404 / 400），前端据 Content-Type 区分。
        /// </summary>
        [HttpGet("pdf")]
        public async Task<IActionResult> DownloadPdf([FromQuery] MyobPayslipDetailRequest request, CancellationToken cancellationToken)
        {
            NoStore();
            var result = await _service.DownloadPdfAsync(request ?? new MyobPayslipDetailRequest(), cancellationToken);
            if (!result.Success || result.Data is null)
            {
                return ToResult(result);
            }

            return File(result.Data.Content, "application/pdf", result.Data.Filename);
        }

        /// <summary>邮件弹窗预填（需 Myob.Payslip.Email）：档案邮箱、主题、文件名、当前登录人邮箱。</summary>
        [HttpGet("email-target")]
        public async Task<IActionResult> GetEmailTarget([FromQuery] MyobPayslipDetailRequest request, CancellationToken cancellationToken)
        {
            NoStore();
            return ToResult(await _service.GetEmailTargetAsync(request ?? new MyobPayslipDetailRequest(), cancellationToken));
        }

        /// <summary>把工资单 PDF 邮件发给员工（需 Myob.Payslip.Email；每次一个收件人；会写 Email 查看记录）。</summary>
        [HttpPost("email")]
        public async Task<IActionResult> SendEmail([FromBody] MyobPayslipEmailRequest request, CancellationToken cancellationToken)
        {
            NoStore();
            return ToResult(await _service.SendEmailAsync(request ?? new MyobPayslipEmailRequest(), cancellationToken));
        }

        private void NoStore() => Response.Headers.CacheControl = "no-store";

        // 无权限 403；找不到 404（越权与不存在不区分）；其余业务失败 400。
        private IActionResult ToResult<T>(ApiResponse<T> result)
        {
            if (result.Success)
            {
                return Ok(result);
            }

            return result.ErrorCode switch
            {
                MyobPayslipErrorCodes.Forbidden => StatusCode(StatusCodes.Status403Forbidden, result),
                MyobPayslipErrorCodes.NotFound => NotFound(result),
                MyobPayslipErrorCodes.PdfFailed or MyobPayslipErrorCodes.EmailSendFailed => StatusCode(StatusCodes.Status502BadGateway, result),
                _ => BadRequest(result),
            };
        }
    }
}
