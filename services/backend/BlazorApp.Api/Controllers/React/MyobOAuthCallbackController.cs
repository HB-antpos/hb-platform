using BlazorApp.Api.Services.Myob;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// MYOB OAuth 授权回调（Redirect Uri 指向这里，例如 https://hotbargain.vip/api/myob/oauth/callback）。
    /// 这是 MYOB 经浏览器跳回的顶层 GET，不带我方登录令牌，所以必须匿名放行；
    /// 安全性靠 state：它是授权入口生成的、10 分钟内有效的加密令牌，只有拥有 Myob.Connection.Manage 的人才能生成。
    /// 处理完统一 302 回 Web 页面（固定的同源路径，不接受外部传入的跳转地址）。
    /// </summary>
    [ApiController]
    [Route("api/myob/oauth/callback")]
    [AllowAnonymous]
    public class MyobOAuthCallbackController : ControllerBase
    {
        private readonly IMyobConnectionService _service;
        private readonly MyobOptions _options;

        public MyobOAuthCallbackController(IMyobConnectionService service, IOptions<MyobOptions> options)
        {
            _service = service;
            _options = options.Value;
        }

        [HttpGet]
        public async Task<IActionResult> Callback(
            [FromQuery] string? code,
            [FromQuery] string? state,
            [FromQuery] string? businessId,
            [FromQuery] string? error,
            CancellationToken cancellationToken)
        {
            var result = await _service.HandleCallbackAsync(code, state, businessId, error, cancellationToken);
            var path = string.IsNullOrWhiteSpace(_options.WebReturnPath) ? "/pos-admin/myob" : _options.WebReturnPath;
            var target = result.Success
                ? $"{path}?myob=connected&connection={Uri.EscapeDataString(result.ConnectionGuid ?? string.Empty)}"
                : $"{path}?myob=error&code={Uri.EscapeDataString(result.ErrorCode ?? MyobErrorCodes.TokenExchangeFailed)}";
            return Redirect(target);
        }
    }
}
