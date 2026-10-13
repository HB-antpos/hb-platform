using System.Net;
using System.Text;
using System.Text.Json;
using BlazorApp.FaceIngest.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BlazorApp.FaceIngest.Tests;

/// <summary>/health 结构，以及节流、token 解析等小工具。</summary>
public sealed class HealthAndUtilityTests
{
    [Fact]
    public async Task Health返回200且只含约定字段_统计随落盘变化()
    {
        using var factory = new FaceIngestAppFactory();
        using var client = factory.CreateClient();

        using (var response = await client.GetAsync("/health"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            var json = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            // 中文注释：P1 按规格新增 cameraSource / cameraSourceReady 两个字段；人脸比对再新增 matcherEnabled / matcherRegion，其余与 P0 相同。
            Assert.Equal(
                ["cameraSource", "cameraSourceReady", "captureEnabled", "matcherEnabled", "matcherRegion", "status", "storedBytes", "storedFileCount"],
                root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
            // 默认没配腾讯云凭据：匹配关闭；地域是出厂默认的白名单地域名。
            Assert.False(root.GetProperty("matcherEnabled").GetBoolean());
            Assert.Equal("ap-guangzhou", root.GetProperty("matcherRegion").GetString());
            Assert.Equal("json", root.GetProperty("cameraSource").GetString());
            Assert.True(root.GetProperty("cameraSourceReady").GetBoolean());
            Assert.Equal("healthy", root.GetProperty("status").GetString());
            Assert.True(root.GetProperty("captureEnabled").GetBoolean());
            Assert.Equal(0, root.GetProperty("storedFileCount").GetInt64());
            Assert.Equal(0, root.GetProperty("storedBytes").GetInt64());
            Assert.DoesNotContain("cam-", json, StringComparison.Ordinal);
            Assert.DoesNotContain("S1001", json, StringComparison.Ordinal);
        }

        using (var response = await client.PostAsync(
                   $"/hik/v1/{factory.FrontCamera.Token}",
                   IngestEndpointTests.CreateContent(Encoding.UTF8.GetBytes("health-body"), "application/octet-stream")))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var response = await client.GetAsync("/health"))
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var capture = Assert.Single(factory.StoredCaptures());
            var expectedBytes = new FileInfo(capture.BodyPath).Length + new FileInfo(capture.MetadataPath).Length;
            Assert.Equal(2, document.RootElement.GetProperty("storedFileCount").GetInt64());
            Assert.Equal(expectedBytes, document.RootElement.GetProperty("storedBytes").GetInt64());
        }
    }

    [Fact]
    public async Task Health反映CaptureEnabled关闭()
    {
        using var factory = new FaceIngestAppFactory(options => options.Settings["FaceIngest:CaptureEnabled"] = "false");
        using var client = factory.CreateClient();

        using var document = JsonDocument.Parse(await client.GetStringAsync("/health"));

        Assert.False(document.RootElement.GetProperty("captureEnabled").GetBoolean());
    }

    [Fact]
    public void 生产日志配置把Microsoft_AspNetCore压到Warning_避免请求行里的token进日志()
    {
        // 中文注释：ASP.NET Core 的 Hosting 诊断日志在 Information 级别会打印完整请求行（含路径里的 token），
        // appsettings.json 必须把它压到 Warning；这里确认测试宿主加载的就是服务项目自带的 appsettings.json。
        using var factory = new FaceIngestAppFactory();

        var configuration = factory.GetService<IConfiguration>();

        Assert.Equal("Warning", configuration["Logging:LogLevel:Microsoft.AspNetCore"]);
        Assert.Equal("/app/App_Data/FaceIngest", new ConfigurationBuilder()
            .SetBasePath(factory.GetService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>().ContentRootPath)
            .AddJsonFile("appsettings.json")
            .Build()["FaceIngest:StorageRootPath"]);
    }

    [Fact]
    public void ThrottledLogGate同key窗口内只放行一次_过窗后带出抑制次数()
    {
        var time = new FakeTimeProvider(FaceIngestAppFactory.DefaultUtcNow);
        var gate = new ThrottledLogGate(time, TimeSpan.FromSeconds(60));

        Assert.True(gate.TryAcquire("203.0.113.1", out var first));
        Assert.Equal(0, first);
        Assert.False(gate.TryAcquire("203.0.113.1", out _));
        Assert.False(gate.TryAcquire("203.0.113.1", out _));
        Assert.True(gate.TryAcquire("203.0.113.2", out _));

        time.Advance(TimeSpan.FromSeconds(60));
        Assert.True(gate.TryAcquire("203.0.113.1", out var suppressed));
        Assert.Equal(2, suppressed);
    }

    [Fact]
    public void ThrottledLogGate来源过多时归入溢出key_内存有上限()
    {
        var time = new FakeTimeProvider(FaceIngestAppFactory.DefaultUtcNow);
        var gate = new ThrottledLogGate(time, TimeSpan.FromSeconds(60), maxKeys: 3);

        Assert.True(gate.TryAcquire("a", out _));
        Assert.True(gate.TryAcquire("b", out _));
        Assert.True(gate.TryAcquire("c", out _));
        Assert.True(gate.TryAcquire("d", out _));
        Assert.False(gate.TryAcquire("e", out _));

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.True(gate.TryAcquire("f", out _));
    }

    [Theory]
    [InlineData("Basic", true)]
    [InlineData("basic", true)]
    [InlineData("Digest", false)]
    public void Basic凭据取password作token_Digest不支持(string scheme, bool expectToken)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = scheme + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:pass:with:colon"));

        var token = IngestTokenResolver.Resolve(context.Request);

        Assert.Equal(expectToken ? "pass:with:colon" : null, token.Value);
        Assert.Equal(expectToken ? TokenSource.Basic : TokenSource.None, token.Source);
    }

    [Fact]
    public void 路径token优先于query与Basic()
    {
        var context = new DefaultHttpContext();
        context.Request.RouteValues["token"] = "path-token";
        context.Request.QueryString = new QueryString("?token=query-token");
        context.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("u:basic-token"));

        var token = IngestTokenResolver.Resolve(context.Request);

        Assert.Equal(new ResolvedToken("path-token", TokenSource.Path), token);
    }
}
