using System.Reflection;
using System.Text.Json;
using BlazorApp.Api.Controllers;
using BlazorApp.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

public class HealthControllerNetworkDiagnosticsTests
{
    [Fact]
    public async Task Diagnostics_返回公网IP_请求编号与数据库耗时_且禁止缓存()
    {
        var controller = CreateController("49.181.138.32", databaseLatencyMs: 12);

        var result = await controller.Diagnostics(CancellationToken.None);

        var payload = ToJson(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("healthy", payload.GetProperty("status").GetString());
        Assert.Equal("49.181.138.32", payload.GetProperty("clientIp").GetString());
        Assert.Equal("TRACE-1", payload.GetProperty("requestId").GetString());
        Assert.True(payload.GetProperty("database").GetProperty("ok").GetBoolean());
        Assert.Equal(12, payload.GetProperty("database").GetProperty("latencyMs").GetInt64());
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Diagnostics_IP未识别与数据库不可用时返回null_不报错()
    {
        var controller = CreateController("unknown", databaseLatencyMs: null);

        var result = await controller.Diagnostics(CancellationToken.None);

        var payload = ToJson(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("clientIp").ValueKind);
        Assert.False(payload.GetProperty("database").GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("database").GetProperty("latencyMs").ValueKind);
    }

    [Theory]
    [InlineData(512, 512)]
    [InlineData(1, HealthController.MinDownloadKilobytes)]
    [InlineData(999999, HealthController.MaxDownloadKilobytes)]
    public void Download_大小限制在16到1024KB_随机内容且禁止缓存(int requestedKb, int expectedKb)
    {
        var controller = CreateController("49.181.138.32", databaseLatencyMs: 1);

        var file = Assert.IsType<FileContentResult>(controller.Download(requestedKb));

        Assert.Equal(expectedKb * 1024, file.FileContents.Length);
        Assert.Equal("application/octet-stream", file.ContentType);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        // 随机内容（不是全 0），避免链路压缩让测速虚高。
        Assert.Contains(file.FileContents, value => value != 0);
    }

    [Theory]
    [InlineData(nameof(HealthController.Diagnostics))]
    [InlineData(nameof(HealthController.Download))]
    public void 网络测试接口_免登录且按IP限流(string methodName)
    {
        var method = typeof(HealthController).GetMethod(methodName)!;
        Assert.NotNull(method.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal(
            NetworkDiagnosticsRateLimits.PolicyName,
            method.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName
        );
    }

    private static HealthController CreateController(string clientIp, long? databaseLatencyMs)
    {
        var resolver = new Mock<IClientIpResolver>();
        resolver.Setup(item => item.Resolve(It.IsAny<HttpContext>())).Returns(clientIp);
        var probe = new Mock<INetworkDiagnosticsProbe>();
        probe.Setup(item => item.PingDatabaseAsync(It.IsAny<CancellationToken>())).ReturnsAsync(databaseLatencyMs);
        return new HealthController(Mock.Of<ILogger<HealthController>>(), resolver.Object, probe.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { TraceIdentifier = "TRACE-1" },
            },
        };
    }

    private static JsonElement ToJson(object? value) =>
        JsonSerializer.SerializeToElement(value);
}
