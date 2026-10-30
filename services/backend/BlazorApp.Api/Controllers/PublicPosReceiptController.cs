using BlazorApp.Api.Features.PosReceipts;
using BlazorApp.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BlazorApp.Api.Controllers;

/// <summary>
/// 客人扫码打开的在线小票（免登录、只读）。
/// 令牌不存在 / 过期 / 被重新生成撤销 / 订单状态不再支持，一律返回同一个空 404：不泄露订单是否存在，也不区分失效原因。
/// 响应 no-store + no-referrer：小票内容与链接不进共享缓存，也不通过 Referer 外泄。
/// </summary>
[ApiController]
[Route("api/public/pos-receipts")]
[AllowAnonymous]
public sealed class PublicPosReceiptController : ControllerBase
{
    private readonly IPosReceiptService _service;
    private readonly ILogger<PublicPosReceiptController> _logger;

    public PublicPosReceiptController(IPosReceiptService service, ILogger<PublicPosReceiptController> logger)
    {
        _service = service;
        _logger = logger;
    }

    [HttpGet("{token}")]
    [EnableRateLimiting(PosReceiptRateLimits.PolicyName)]
    public async Task<IActionResult> Get(string token, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        try
        {
            var result = await _service.GetPublicDocumentAsync(token, cancellationToken);
            return result.Ok ? Ok(result.Data) : NotFound();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 内部错误不向客人暴露细节，也不记令牌（令牌就是凭据）。
            _logger.LogError(ex, "Public POS receipt lookup failed");
            return StatusCode(500);
        }
    }
}
