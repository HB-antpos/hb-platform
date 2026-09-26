using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Services;

/// <summary>
/// 只对白名单内的目录响应启用 gzip 压缩，白名单外的端点保持原有未压缩行为：
/// 1. 商品分页且 checksumVersion=2。v1/WPF 分页即使发送 Accept-Encoding 头也保持未压缩，
///    保证 WPF v1 下载响应与修改前字节级一致；
/// 2. 码冲突候选（整包返回，没有字节级一致约束）。客户端声明 gzip 才压缩，
///    不发送 Accept-Encoding 的旧版 WPF 仍收到未压缩响应。
/// </summary>
public sealed class CatalogResponseCompressionProvider : ResponseCompressionProvider
{
    public CatalogResponseCompressionProvider(
        IServiceProvider services,
        IOptions<ResponseCompressionOptions> options)
        : base(services, options)
    {
    }

    public override bool ShouldCompressResponse(HttpContext context)
    {
        return (IsCatalogV2PageRequest(context) || IsCodeConflictsRequest(context))
               && base.ShouldCompressResponse(context);
    }

    private static bool IsCatalogV2PageRequest(HttpContext context)
    {
        return context.Request.Path.StartsWithSegments(
                   "/api/v1/catalog/sellable-items/page",
                   StringComparison.OrdinalIgnoreCase)
               && string.Equals(
                   context.Request.Query["checksumVersion"].ToString(),
                   "2",
                   StringComparison.Ordinal);
    }

    private static bool IsCodeConflictsRequest(HttpContext context)
    {
        return context.Request.Path.StartsWithSegments(
            "/api/v1/catalog/sellable-items/code-conflicts",
            StringComparison.OrdinalIgnoreCase);
    }
}
