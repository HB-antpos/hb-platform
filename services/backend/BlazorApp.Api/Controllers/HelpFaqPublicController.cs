using BlazorApp.Api.Services.HelpFaq;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace BlazorApp.Api.Controllers;

/// <summary>
/// 使用帮助 FAQ 公开读接口（页面 /support/faq 免登录，移动端「使用帮助」磁贴也只是打开该页）。
/// 只读、只返回已发布内容，绝不输出草稿。
/// </summary>
[ApiController]
[Route("api/support/faq")]
public sealed class HelpFaqPublicController(IHelpFaqPublicService service) : ControllerBase
{
    /// <summary>浏览器 / CDN 缓存 60 秒；过期后带 If-None-Match 重新校验，内容没变就是 304。</summary>
    internal const string CacheControlValue = "public, max-age=60";

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var snapshot = await service.GetPublishedAsync(cancellationToken);

        Response.Headers.CacheControl = CacheControlValue;
        Response.Headers.ETag = snapshot.ETag;

        if (IfNoneMatchMatches(Request.Headers.IfNoneMatch.ToString(), snapshot.ETag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(ApiResponse<HelpFaqPublicDto>.OK(snapshot.Data));
    }

    /// <summary>
    /// If-None-Match 使用弱比较：支持 "*"、逗号分隔的多个 ETag、W/ 前缀
    /// （CDN / 压缩中间件常把强 ETag 改成弱 ETag）。
    /// </summary>
    internal static bool IfNoneMatchMatches(string? headerValue, string currentETag)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return false;
        }

        if (headerValue.Trim() == "*")
        {
            return true;
        }

        if (!EntityTagHeaderValue.TryParse(currentETag, out var current))
        {
            return false;
        }

        if (!EntityTagHeaderValue.TryParseList(new[] { headerValue }, out var candidates))
        {
            return false;
        }

        return candidates.Any(candidate => candidate.Compare(current, useStrongComparison: false));
    }
}
