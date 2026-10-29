using System.Security.Claims;
using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// MYOB 连接管理（Web 页面 /pos-admin/myob）：已授权的公司文件、ABN 维护、验证 / 断开，以及分店 → 公司文件映射。
    /// 全部接口要求 Myob.Connection.Manage（默认不授予任何角色，由管理员手动授权）。
    /// 授权回调不在这里：MYOB 经浏览器跳回的匿名请求见 <see cref="MyobOAuthCallbackController"/>。
    /// </summary>
    [ApiController]
    [Route("api/react/v1/myob")]
    [Authorize(Policy = Permissions.Myob.Connection.Manage)]
    public class MyobController : ControllerBase
    {
        private readonly IMyobConnectionService _service;

        public MyobController(IMyobConnectionService service)
        {
            _service = service;
        }

        [HttpGet("connections")]
        public async Task<IActionResult> ListConnections(CancellationToken cancellationToken) =>
            ToResult(await _service.ListConnectionsAsync(cancellationToken));

        /// <summary>生成 MYOB 授权地址；前端拿到后整页跳转。授权人必须是该公司文件的 Administrator。</summary>
        [HttpPost("connections/authorize-url")]
        public async Task<IActionResult> BuildAuthorizeUrl(CancellationToken cancellationToken) =>
            ToResult(await _service.BuildAuthorizeUrlAsync(CurrentUserGuid(), cancellationToken));

        [HttpPut("connections/{connectionGuid}/abn")]
        public async Task<IActionResult> UpdateAbn(
            string connectionGuid,
            [FromBody] MyobUpdateAbnRequest request,
            CancellationToken cancellationToken) =>
            ToResult(await _service.UpdateAbnAsync(connectionGuid, request?.Abn, cancellationToken));

        [HttpPost("connections/{connectionGuid}/verify")]
        public async Task<IActionResult> Verify(string connectionGuid, CancellationToken cancellationToken) =>
            ToResult(await _service.VerifyAsync(connectionGuid, cancellationToken));

        [HttpPost("connections/{connectionGuid}/disconnect")]
        public async Task<IActionResult> Disconnect(string connectionGuid, CancellationToken cancellationToken) =>
            ToResult(await _service.DisconnectAsync(connectionGuid, cancellationToken));

        [HttpGet("store-mappings")]
        public async Task<IActionResult> GetStoreMappings(CancellationToken cancellationToken) =>
            ToResult(await _service.GetStoreMappingsAsync(cancellationToken));

        [HttpPut("store-mappings")]
        public async Task<IActionResult> UpdateStoreMappings(
            [FromBody] MyobUpdateStoreMappingsRequest request,
            CancellationToken cancellationToken) =>
            ToResult(await _service.UpdateStoreMappingsAsync(
                request ?? new MyobUpdateStoreMappingsRequest(),
                CurrentUserGuid(),
                cancellationToken));

        private string CurrentUserGuid() =>
            User.FindFirst("userId")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? string.Empty;

        // 资源不存在返回 404，其余业务失败返回 400；带数据的失败（如验证未通过）原样返回，页面据此刷新该行状态。
        private IActionResult ToResult<T>(ApiResponse<T> result)
        {
            if (result.Success)
            {
                return Ok(result);
            }

            return result.ErrorCode == MyobErrorCodes.ConnectionNotFound ? NotFound(result) : BadRequest(result);
        }
    }
}
