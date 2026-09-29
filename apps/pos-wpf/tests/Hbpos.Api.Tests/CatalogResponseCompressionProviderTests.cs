using Hbpos.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Tests;

/// <summary>
/// gzip 只对商品分页且 checksumVersion=2、码冲突候选两条白名单生效；v1/WPF 分页与其他端点保持未压缩，
/// 保证 WPF v1 下载响应与修改前字节级一致。
/// </summary>
public sealed class CatalogResponseCompressionProviderTests
{
    private static CatalogResponseCompressionProvider CreateProvider()
    {
        // 中文注释：与 Program.cs 注册一致：为压缩 provider 注册所需 options，
        // JSON 需显式加入可压缩 MIME 白名单。
        var compressionOptions = new ResponseCompressionOptions
        {
            MimeTypes = ResponseCompressionDefaults.MimeTypes.Append("application/json"),
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.Configure<GzipCompressionProviderOptions>(_ => { });
        services.Configure<BrotliCompressionProviderOptions>(_ => { });
        var provider = new CatalogResponseCompressionProvider(
            services.BuildServiceProvider(),
            Options.Create(compressionOptions));
        return provider;
    }

    private static DefaultHttpContext CreateContext(string path, string query)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Response.ContentType = "application/json";
        return context;
    }

    [Fact]
    public void Compresses_catalog_v2_pages_with_json_content_type()
    {
        var provider = CreateProvider();
        var context = CreateContext(
            "/api/v1/catalog/sellable-items/page",
            "?storeCode=S1&pageSize=5000&checksumVersion=2");

        Assert.True(provider.ShouldCompressResponse(context));
    }

    [Fact]
    public void Does_not_compress_v1_pages_even_with_accept_encoding()
    {
        var provider = CreateProvider();
        var context = CreateContext(
            "/api/v1/catalog/sellable-items/page",
            "?storeCode=S1&pageSize=5000&checksumVersion=1");

        Assert.False(provider.ShouldCompressResponse(context));
    }

    [Fact]
    public void Does_not_compress_catalog_v2_pages_without_checksum_version()
    {
        var provider = CreateProvider();
        var context = CreateContext(
            "/api/v1/catalog/sellable-items/page",
            "?storeCode=S1&pageSize=5000");

        Assert.False(provider.ShouldCompressResponse(context));
    }

    [Theory]
    [InlineData("/api/v1/catalog/sellable-items/code-conflicts", "?storeCode=S1")]
    [InlineData("/API/V1/Catalog/Sellable-Items/Code-Conflicts", "?storeCode=S1")]
    [InlineData("/api/v1/catalog/sellable-items/code-conflicts/", "?storeCode=S1")]
    public void Compresses_code_conflicts_with_json_content_type(string path, string query)
    {
        var provider = CreateProvider();
        var context = CreateContext(path, query);

        Assert.True(provider.ShouldCompressResponse(context));
    }

    [Theory]
    [InlineData("/api/v1/catalog/promotions", "?storeCode=S1&checksumVersion=2")]
    [InlineData("/api/v1/catalog/sellable-items", "?storeCode=S1")]
    [InlineData("/api/v1/catalog/sellable-items/lookup", "?storeCode=S1&lookupCode=123")]
    [InlineData("/api/v1/catalog/sellable-items/code-conflicts-v2", "?storeCode=S1")]
    [InlineData("/api/v1/health", "")]
    public void Does_not_compress_other_endpoints(string path, string query)
    {
        var provider = CreateProvider();
        var context = CreateContext(path, query);

        Assert.False(provider.ShouldCompressResponse(context));
    }

    [Theory]
    [InlineData("/api/v1/catalog/sellable-items/page", "?storeCode=S1&pageSize=5000&checksumVersion=2")]
    [InlineData("/api/v1/catalog/sellable-items/code-conflicts", "?storeCode=S1")]
    public void Does_not_compress_non_compressible_content_type(string path, string query)
    {
        var provider = CreateProvider();
        var context = CreateContext(path, query);
        context.Response.ContentType = "application/octet-stream";

        Assert.False(provider.ShouldCompressResponse(context));
    }
}
