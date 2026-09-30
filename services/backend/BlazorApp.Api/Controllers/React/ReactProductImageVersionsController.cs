using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React;

/// <summary>
/// 商品图片版本号：Web 列表页批量查询当前页图片在 COS 上的修改时间，
/// 给最近换过的图片的缩略图地址追加版本参数，绕开 COS 缩略图 30 天强缓存。
/// </summary>
[ApiController]
[Route("api/react/v1/product-images/versions")]
// 多个角色的商品列表都会显示缩略图；接口只返回公开读图片的修改时间，登录即可调用。
[Authorize]
public sealed class ReactProductImageVersionsController(ProductImageVersionService service) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> GetVersions([FromBody] ProductImageVersionsRequest? request)
    {
        var urls = request?.Urls ?? new List<string?>();
        if (urls.Count > ProductImageVersionService.MaxUrlsPerRequest)
        {
            return BadRequest(ApiResponse<ProductImageVersionsResult>.Error(
                $"单次最多查询 {ProductImageVersionService.MaxUrlsPerRequest} 张图片",
                "TOO_MANY_URLS"));
        }

        var versions = await service.GetRecentVersionsAsync(urls);
        return Ok(ApiResponse<ProductImageVersionsResult>.OK(new ProductImageVersionsResult { Versions = versions }));
    }
}

public sealed class ProductImageVersionsRequest
{
    public List<string?>? Urls { get; set; }
}

public sealed class ProductImageVersionsResult
{
    /// <summary>原始图片地址 → 版本号（Last-Modified 的 Unix 秒数），只含最近改过的图片。</summary>
    public Dictionary<string, string> Versions { get; set; } = new();
}
