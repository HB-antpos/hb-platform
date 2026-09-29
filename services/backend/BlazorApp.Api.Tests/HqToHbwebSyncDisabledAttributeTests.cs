using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Filters;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// HQ → HBweb 同步停用过滤器：命中即返回 410 统一失败包络并记录 Warning；双向入口只放行白名单方向。
/// </summary>
public class HqToHbwebSyncDisabledAttributeTests
{
    [Fact]
    public void 无条件停用时返回410统一失败包络并记录Warning()
    {
        var loggerProvider = new CapturingLoggerProvider();
        using var services = new ServiceCollection()
            .AddLogging(builder => builder.AddProvider(loggerProvider))
            .BuildServiceProvider();
        var context = CreateContext(services: services);

        new HqToHbwebSyncDisabledAttribute().OnActionExecuting(context);

        AssertGone(context.Result);
        var entry = Assert.Single(loggerProvider.Entries);
        Assert.Equal(typeof(HqToHbwebSyncDisabledAttribute).FullName, entry.Category);
        Assert.Equal(LogLevel.Warning, entry.Level);
        // 日志需带路由与调用者，便于在应用日志里定位仍在调用的页面或脚本
        Assert.Contains("POST /api/react/v1/sync/products", entry.Message, StringComparison.Ordinal);
        Assert.Contains("api/react/v1/sync/products", entry.Message, StringComparison.Ordinal);
        Assert.Contains("DataSyncReactController.SyncProducts", entry.Message, StringComparison.Ordinal);
        Assert.Contains("tester", entry.Message, StringComparison.Ordinal);
        Assert.Contains("user-guid-001", entry.Message, StringComparison.Ordinal);
        Assert.Contains("10.0.0.8", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 没有请求级服务容器时仍返回410()
    {
        var context = CreateContext();

        new HqToHbwebSyncDisabledAttribute().OnActionExecuting(context);

        AssertGone(context.Result);
    }

    [Fact]
    public void 停用过滤器先于ApiController模型校验执行()
    {
        // ApiController 的 ModelStateInvalidFilter 排序为 -2000；停用入口不应先报参数错误。
        Assert.True(new HqToHbwebSyncDisabledAttribute().Order < -2000);
    }

    [Theory]
    [InlineData("LocalToHq", false)]
    [InlineData("  localtohq  ", false)]
    [InlineData("LOCALTOHQ", false)]
    [InlineData("HqToLocal", true)]
    [InlineData("hqtolocal", true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData(null, true)]
    [InlineData("SomethingNew", true)]
    public void 双向入口只放行本地到HQ方向(string? direction, bool expectBlocked)
    {
        var context = CreateContext(
            new Dictionary<string, object?>
            {
                ["request"] = new StorePriceTransferRequest { Direction = direction! },
            }
        );

        CreateStorePriceTransferAttribute().OnActionExecuting(context);

        if (expectBlocked)
        {
            AssertGone(context.Result);
        }
        else
        {
            Assert.Null(context.Result);
        }
    }

    [Fact]
    public void 双向入口在请求体缺失时交给Action自身校验()
    {
        var withNullArgument = CreateContext(
            new Dictionary<string, object?> { ["request"] = null }
        );
        var withoutArgument = CreateContext(new Dictionary<string, object?>());

        CreateStorePriceTransferAttribute().OnActionExecuting(withNullArgument);
        CreateStorePriceTransferAttribute().OnActionExecuting(withoutArgument);

        Assert.Null(withNullArgument.Result);
        Assert.Null(withoutArgument.Result);
    }

    [Fact]
    public async Task Mvc管道中停用入口返回410且不会执行同步服务()
    {
        var fixture = new PipelineFixture();
        using var server = fixture.CreateServer();
        using var client = server.CreateClient();

        using var response = await client.PostAsync("/api/react/v1/sync/products", null);

        await AssertGoneResponseAsync(response);
        fixture.FullSyncService.VerifyNoOtherCalls();
        fixture.IncrementalSyncService.VerifyNoOtherCalls();
        fixture.ProductHqSyncService.VerifyNoOtherCalls();
        Assert.Contains(
            fixture.LoggerProvider.Entries,
            entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains("/api/react/v1/sync/products", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task Mvc管道中请求体非法的停用入口也返回410而不是400()
    {
        var fixture = new PipelineFixture();
        using var server = fixture.CreateServer();
        using var client = server.CreateClient();

        using var response = await client.PostAsync(
            "/api/react/v1/sync/store-retail-prices",
            new StringContent("{\"selectedStoreCodes\":123}", Encoding.UTF8, "application/json")
        );

        await AssertGoneResponseAsync(response);
        fixture.FullSyncService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Mvc管道中POSM映射同步仍然执行()
    {
        var fixture = new PipelineFixture();
        fixture.FullSyncService
            .Setup(service => service.SyncPosmProductSupplierMappingsAsync())
            .ReturnsAsync(new SyncResult { IsSuccess = true, AddedCount = 1 });
        using var server = fixture.CreateServer();
        using var client = server.CreateClient();

        using var response = await client.PostAsync(
            "/api/react/v1/sync/posm-product-supplier-mappings",
            null
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        fixture.FullSyncService.Verify(
            service => service.SyncPosmProductSupplierMappingsAsync(),
            Times.Once
        );
    }

    [Theory]
    [InlineData("{\"direction\":\"HqToLocal\",\"sourceStoreCode\":\"1001\",\"targetStoreCode\":\"1002\"}")]
    [InlineData("{\"sourceStoreCode\":\"1001\",\"targetStoreCode\":\"1002\"}")]
    public async Task Mvc管道中分店价格同步的HQ到本地方向返回410(string body)
    {
        var fixture = new PipelineFixture();
        using var server = fixture.CreateServer();
        using var client = server.CreateClient();

        using var response = await client.PostAsync(
            "/api/react/v1/store-product-prices/store-price-transfer-jobs",
            new StringContent(body, Encoding.UTF8, "application/json")
        );

        await AssertGoneResponseAsync(response);
        fixture.StorePriceTransferJobService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Mvc管道中分店价格同步的本地到HQ方向仍然提交任务()
    {
        var fixture = new PipelineFixture();
        fixture.StorePriceTransferJobService
            .Setup(service => service.StartJobAsync(
                It.Is<StorePriceTransferRequest>(request =>
                    request.Direction == StorePriceTransferDirectionConstants.LocalToHq
                ),
                "tester",
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(new StorePriceTransferJobDto { JobId = "job-001", Message = "已提交" });
        using var server = fixture.CreateServer();
        using var client = server.CreateClient();

        using var response = await client.PostAsync(
            "/api/react/v1/store-product-prices/store-price-transfer-jobs",
            new StringContent(
                "{\"direction\":\"LocalToHq\",\"sourceStoreCode\":\"1001\",\"targetStoreCode\":\"1002\"}",
                Encoding.UTF8,
                "application/json"
            )
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        fixture.StorePriceTransferJobService.Verify(
            service => service.StartJobAsync(
                It.IsAny<StorePriceTransferRequest>(),
                "tester",
                It.IsAny<CancellationToken>()
            ),
            Times.Once
        );
    }

    private static HqToHbwebSyncDisabledAttribute CreateStorePriceTransferAttribute()
    {
        // 与 ReactStoreProductPricesController.StartStorePriceTransferJob 上的配置保持一致
        return new HqToHbwebSyncDisabledAttribute
        {
            ArgumentName = "request",
            DirectionProperty = nameof(StorePriceTransferRequest.Direction),
            AllowedDirection = StorePriceTransferDirectionConstants.LocalToHq,
        };
    }

    private static ActionExecutingContext CreateContext(
        IDictionary<string, object?>? arguments = null,
        IServiceProvider? services = null
    )
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.Path = "/api/react/v1/sync/products";
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.8");
        httpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(
                new[]
                {
                    new Claim(ClaimTypes.Name, "tester"),
                    new Claim("userId", "user-guid-001"),
                },
                "test"
            )
        );
        if (services is not null)
        {
            httpContext.RequestServices = services;
        }

        var actionDescriptor = new ControllerActionDescriptor
        {
            DisplayName = "DataSyncReactController.SyncProducts",
            AttributeRouteInfo = new AttributeRouteInfo { Template = "api/react/v1/sync/products" },
        };
        return new ActionExecutingContext(
            new ActionContext(httpContext, new RouteData(), actionDescriptor),
            new List<IFilterMetadata>(),
            arguments ?? new Dictionary<string, object?>(),
            controller: new object()
        );
    }

    private static void AssertGone(IActionResult? result)
    {
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status410Gone, objectResult.StatusCode);
        var body = Assert.IsType<ApiResponse<object>>(objectResult.Value);
        Assert.False(body.Success);
        Assert.Equal(HqToHbwebSyncDisabledAttribute.DisabledMessage, body.Message);
        Assert.Equal("HQ → HBweb 同步已于 2026-09-29 停用", body.Message);
        Assert.Equal(HqToHbwebSyncDisabledAttribute.DisabledErrorCode, body.ErrorCode);
    }

    private static async Task AssertGoneResponseAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(
            HqToHbwebSyncDisabledAttribute.DisabledMessage,
            root.GetProperty("message").GetString()
        );
        Assert.Equal(
            HqToHbwebSyncDisabledAttribute.DisabledErrorCode,
            root.GetProperty("errorCode").GetString()
        );
    }

    /// <summary>
    /// 真实 MVC 管道：注册 API 程序集全部控制器，依赖全部用严格 Mock，
    /// 过滤器一旦漏拦就会调用到 Mock 并在 VerifyNoOtherCalls 处失败。
    /// </summary>
    private sealed class PipelineFixture
    {
        public Mock<IDataSyncFullService> FullSyncService { get; } = new(MockBehavior.Strict);
        public Mock<IDataSyncIncrementalService> IncrementalSyncService { get; } = new(MockBehavior.Strict);
        public Mock<IProductHqSyncService> ProductHqSyncService { get; } = new(MockBehavior.Strict);
        public Mock<IStorePriceTransferJobService> StorePriceTransferJobService { get; } = new(MockBehavior.Strict);
        public CapturingLoggerProvider LoggerProvider { get; } = new();

        public TestServer CreateServer()
        {
            return new TestServer(
                new WebHostBuilder()
                    .ConfigureServices(services =>
                    {
                        services.AddLogging(builder => builder.AddProvider(LoggerProvider));
                        services.AddAuthorization();
                        services.AddSingleton(FullSyncService.Object);
                        services.AddSingleton(IncrementalSyncService.Object);
                        services.AddSingleton(ProductHqSyncService.Object);
                        services.AddSingleton(Mock.Of<ICurrentUserService>());
                        services.AddSingleton(StorePriceTransferJobService.Object);
                        services.AddSingleton(Mock.Of<IStoreProductPriceReactService>());
                        services.AddSingleton(Mock.Of<IStoreRetailPriceReactService>());
                        services.AddSingleton(Mock.Of<IUserService>());
                        services.AddControllers().AddApplicationPart(
                            typeof(DataSyncReactController).Assembly
                        );
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.Use(
                            async (context, next) =>
                            {
                                context.User = new ClaimsPrincipal(
                                    new ClaimsIdentity(
                                        new[]
                                        {
                                            new Claim(ClaimTypes.Name, "tester"),
                                            new Claim(ClaimTypes.NameIdentifier, "user-guid-001"),
                                            new Claim(ClaimTypes.Role, "Admin"),
                                        },
                                        "test"
                                    )
                                );
                                await next();
                            }
                        );
                        app.UseAuthorization();
                        app.UseEndpoints(endpoints => endpoints.MapControllers());
                    })
            );
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

        public void Dispose() { }

        private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> entries)
            : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            )
            {
                // 只收集停用过滤器自己的日志，避免框架日志干扰断言
                if (category == typeof(HqToHbwebSyncDisabledAttribute).FullName)
                {
                    entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception)));
                }
            }
        }
    }

    private sealed record LogEntry(string Category, LogLevel Level, string Message);
}
