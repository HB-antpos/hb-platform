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

    [Theory]
    // 直连：对端就是公网 IP。
    [InlineData("49.181.138.32", null, "49.181.138.32")]
    // 本机 nginx / docker 网关转发：采用 nginx 写入的 X-Real-IP（IPv4 与 IPv6）。
    [InlineData("172.19.0.1", "49.181.138.32", "49.181.138.32")]
    [InlineData("127.0.0.1", "124.187.52.242", "124.187.52.242")]
    [InlineData("::ffff:172.19.0.1", "2001:db8::1", "2001:db8::1")]
    // X-Real-IP 是私网 / 无效 / 缺失：识别不出，不能把网关当成用户 IP。
    [InlineData("172.19.0.1", "10.0.0.8", null)]
    [InlineData("172.19.0.1", "not-an-ip", null)]
    [InlineData("172.19.0.1", null, null)]
    [InlineData("172.19.0.1", "fd00::1", null)]
    [InlineData("172.19.0.1", "100.64.1.2", null)]
    public void TryResolvePublic_生产未配置可信代理时用X_Real_IP兜底(string remote, string? realIp, string? expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(remote);
        if (realIp != null) context.Request.Headers["X-Real-IP"] = realIp;
        // 生产现状：解析器对私网对端返回 unknown。
        var resolver = new Mock<IClientIpResolver>();
        resolver.Setup(item => item.Resolve(It.IsAny<HttpContext>())).Returns("unknown");

        Assert.Equal(expected, NetworkDiagnosticsClientIp.TryResolvePublic(context, resolver.Object));
    }

    [Fact]
    public void TryResolvePublic_不信任X_Forwarded_For_防客户端伪造()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("172.19.0.1");
        context.Request.Headers["X-Forwarded-For"] = "8.8.8.8, 49.181.138.32";

        Assert.Null(NetworkDiagnosticsClientIp.TryResolvePublic(context, null));
    }

    [Fact]
    public void TryResolvePublic_解析器已给出公网IP时优先使用()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("172.19.0.1");
        context.Request.Headers["X-Real-IP"] = "1.2.3.4";
        var resolver = new Mock<IClientIpResolver>();
        resolver.Setup(item => item.Resolve(It.IsAny<HttpContext>())).Returns("49.181.138.32");

        Assert.Equal("49.181.138.32", NetworkDiagnosticsClientIp.TryResolvePublic(context, resolver.Object));
    }

    [Fact]
    public void PartitionKey_不同门店出口分到不同桶_识别不出时退回对端地址()
    {
        string KeyFor(string? realIp)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("172.19.0.1");
            if (realIp != null) context.Request.Headers["X-Real-IP"] = realIp;
            return NetworkDiagnosticsClientIp.PartitionKey(context, null);
        }

        var storeA = KeyFor("49.181.138.32");
        var storeB = KeyFor("1.145.38.154");
        Assert.NotEqual(storeA, storeB);
        Assert.Equal(storeA, KeyFor("49.181.138.32"));
        // 全体设备共用一个 unknown 桶是修复前的问题；识别不出时用网关地址，至少不会和有 IP 的设备混桶。
        Assert.Equal("172.19.0.1", KeyFor(null));
        Assert.NotEqual(storeA, KeyFor(null));
    }

    [Fact]
    public async Task Diagnostics_经nginx转发时返回X_Real_IP里的公网IP()
    {
        var resolver = new Mock<IClientIpResolver>();
        resolver.Setup(item => item.Resolve(It.IsAny<HttpContext>())).Returns("unknown");
        var probe = new Mock<INetworkDiagnosticsProbe>();
        probe.Setup(item => item.PingDatabaseAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);
        var controller = new HealthController(Mock.Of<ILogger<HealthController>>(), resolver.Object, probe.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.HttpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("172.19.0.1");
        controller.HttpContext.Request.Headers["X-Real-IP"] = "49.181.138.32";

        var result = await controller.Diagnostics(CancellationToken.None);

        var payload = ToJson(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("49.181.138.32", payload.GetProperty("clientIp").GetString());
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
