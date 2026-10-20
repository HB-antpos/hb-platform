using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BlazorApp.Api.Features.ProductDescriptions;

/// <summary>
/// 看图生成英文商品名。货柜明细、收银商品管理、国内采购导入的「翻译」按钮共用；
/// 鉴权口径与原 /api/Translation/batch-translate 一致（登录即可），条数由服务端限制。
/// </summary>
[ApiController]
[Route("api/react/v1/product-descriptions")]
[Authorize]
public sealed class ProductDescriptionsController : ControllerBase
{
    private readonly IProductImageDescriptionService _descriptionService;
    private readonly ProductImageDescriptionOptions _options;
    private readonly ILogger<ProductDescriptionsController> _logger;

    public ProductDescriptionsController(
        IProductImageDescriptionService descriptionService,
        IOptions<ProductImageDescriptionOptions> options,
        ILogger<ProductDescriptionsController> logger
    )
    {
        _descriptionService = descriptionService;
        _options = options.Value;
        _logger = logger;
    }

    [HttpPost("from-images")]
    public async Task<IActionResult> DescribeFromImages([FromBody] ProductImageDescriptionRequestDto request)
    {
        var items = request?.Items ?? new List<ProductImageDescriptionItemDto>();
        if (items.Count == 0)
        {
            return BadRequest(new { success = false, message = "商品列表不能为空" });
        }

        var maxItems = Math.Max(1, _options.MaxItemsPerRequest);
        if (items.Count > maxItems)
        {
            return BadRequest(new { success = false, message = $"单次最多处理 {maxItems} 个商品" });
        }

        if (items.Any(item => string.IsNullOrWhiteSpace(item.Key)))
        {
            return BadRequest(new { success = false, message = "每个商品都必须带 key" });
        }

        try
        {
            var result = await _descriptionService.DescribeAsync(items, HttpContext.RequestAborted);
            // 每次调用都会产生模型费用，记下调用人和条数便于对账。
            _logger.LogInformation(
                "看图生成英文名：用户 {User}，共 {Total} 条，看图 {Image}，文字 {Text}，失败 {Failed}",
                User.Identity?.Name,
                items.Count,
                result.ImageCount,
                result.TextCount,
                result.FailedCount
            );
            return Ok(new { success = true, data = result });
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "看图生成英文名失败");
            return StatusCode(500, new { success = false, message = "生成英文名失败，请稍后重试" });
        }
    }
}
