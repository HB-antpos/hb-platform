using BlazorApp.Api.Filters;
using BlazorApp.Api.Middleware;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 客户端主动取消的请求（RequestAborted 已触发 + OperationCanceledException）不能再按 Error / 500 记录；
/// 服务端自身超时与普通异常保持原有的错误处理。
/// </summary>
public class ClientAbortedRequestLoggingTests
{
    private const string QueryPath = "/api/react/v1/containers/9b1312ee-5740-46a9-98bb-4a7bdf9d4c76/products/query";

    [Fact]
    public void 过滤器_客户端中止时的TaskCanceledException不记Error且返回无正文499()
    {
        var logger = new TestLogger<ApiExceptionFilter>();
        var context = CreateExceptionContext(new TaskCanceledException("A task was canceled."), clientAborted: true);

        new ApiExceptionFilter(logger).OnException(context);

        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<StatusCodeResult>(context.Result);
        Assert.Equal(StatusCodes.Status499ClientClosedRequest, result.StatusCode);
        Assert.DoesNotContain(logger.Entries, entry => entry.LogLevel >= LogLevel.Warning);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.LogLevel);
        Assert.Contains(QueryPath, entry.Message);
    }

    [Fact]
    public void 过滤器_未中止时的OperationCanceledException仍按Error记录并返回500()
    {
        var logger = new TestLogger<ApiExceptionFilter>();
        var context = CreateExceptionContext(new OperationCanceledException("服务端 CTS 超时"), clientAborted: false);

        new ApiExceptionFilter(logger).OnException(context);

        AssertInternalError(context, logger);
    }

    [Fact]
    public void 过滤器_HttpClient超时的TaskCanceledException未中止时仍按Error记录并返回500()
    {
        // HttpClient 超时抛出的是内层为 TimeoutException 的 TaskCanceledException，RequestAborted 不会触发。
        var logger = new TestLogger<ApiExceptionFilter>();
        var exception = new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.",
            new TimeoutException("The operation was canceled.")
        );
        var context = CreateExceptionContext(exception, clientAborted: false);

        new ApiExceptionFilter(logger).OnException(context);

        AssertInternalError(context, logger);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 过滤器_普通异常行为不变_客户端是否中止都按Error记录(bool clientAborted)
    {
        // 客户端恰好断开时，非取消类异常仍是真实故障，不能被新分支吞掉。
        var logger = new TestLogger<ApiExceptionFilter>();
        var context = CreateExceptionContext(new InvalidOperationException("业务校验失败"), clientAborted);

        new ApiExceptionFilter(logger).OnException(context);

        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<JsonResult>(context.Result);
        Assert.Equal(400, result.StatusCode);
        var body = Assert.IsType<ApiResponse<object>>(result.Value);
        Assert.False(body.Success);
        Assert.Equal("INVALID_OPERATION", body.ErrorCode);
        Assert.Equal("业务校验失败", body.Message);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.LogLevel);
        Assert.IsType<InvalidOperationException>(entry.Exception);
    }

    [Fact]
    public void 过滤器_普通未知异常仍返回500并记录Error()
    {
        var logger = new TestLogger<ApiExceptionFilter>();
        var context = CreateExceptionContext(new Exception("未知故障"), clientAborted: false);

        new ApiExceptionFilter(logger).OnException(context);

        AssertInternalError(context, logger);
    }

    [Fact]
    public async Task 中间件_客户端中止时的TaskCanceledException不记Error不外抛并标记499()
    {
        var logger = new TestLogger<ApplicationExceptionLoggingMiddleware>();
        var httpContext = CreateHttpContext(clientAborted: true);
        var middleware = new ApplicationExceptionLoggingMiddleware(
            _ => throw new TaskCanceledException("A task was canceled."),
            logger
        );

        await middleware.InvokeAsync(httpContext);

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, httpContext.Response.StatusCode);
        Assert.DoesNotContain(logger.Entries, entry => entry.LogLevel >= LogLevel.Warning);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.LogLevel);
        Assert.Contains(QueryPath, entry.Message);
    }

    [Fact]
    public async Task 中间件_响应已开始时客户端中止不改状态码也不外抛()
    {
        var logger = new TestLogger<ApplicationExceptionLoggingMiddleware>();
        var httpContext = CreateHttpContext(clientAborted: true);
        httpContext.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        var middleware = new ApplicationExceptionLoggingMiddleware(
            _ => throw new OperationCanceledException(),
            logger
        );

        await middleware.InvokeAsync(httpContext);

        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
        Assert.DoesNotContain(logger.Entries, entry => entry.LogLevel >= LogLevel.Warning);
    }

    [Fact]
    public async Task 中间件_未中止时的OperationCanceledException仍按Error记录并外抛()
    {
        var logger = new TestLogger<ApplicationExceptionLoggingMiddleware>();
        var httpContext = CreateHttpContext(clientAborted: false);
        var middleware = new ApplicationExceptionLoggingMiddleware(
            _ => throw new OperationCanceledException("服务端 CTS 超时"),
            logger
        );

        await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(httpContext));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.LogLevel);
        Assert.IsType<OperationCanceledException>(entry.Exception);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 中间件_普通异常行为不变_仍按Error记录并外抛(bool clientAborted)
    {
        var logger = new TestLogger<ApplicationExceptionLoggingMiddleware>();
        var httpContext = CreateHttpContext(clientAborted);
        var middleware = new ApplicationExceptionLoggingMiddleware(
            _ => throw new InvalidOperationException("管道故障"),
            logger
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(httpContext));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.LogLevel);
        Assert.IsType<InvalidOperationException>(entry.Exception);
    }

    [Theory]
    [InlineData("SqlClient用户取消")]
    [InlineData("SqlClient用户取消_MARS批处理已中止")]
    public void 过滤器_令牌在SqlClient读取中途触发的用户取消SqlException不记Error且返回无正文499(string shape)
    {
        // 生产 09-21～28 销售明细等接口约 18 条：取消在执行中途落地时驱动抛 SqlException 而不是 OCE。
        var logger = new TestLogger<ApiExceptionFilter>();
        var context = CreateExceptionContext(ClientAbortDetectorTests.CreateShape(shape), clientAborted: true);

        new ApiExceptionFilter(logger).OnException(context);

        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<StatusCodeResult>(context.Result);
        Assert.Equal(StatusCodes.Status499ClientClosedRequest, result.StatusCode);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.LogLevel);
    }

    [Fact]
    public void 过滤器_SqlClient命令超时即使客户端已中止仍按Error记录并返回500()
    {
        var logger = new TestLogger<ApiExceptionFilter>();
        var context = CreateExceptionContext(SqlClientExceptionShapes.CommandTimeout(), clientAborted: true);

        new ApiExceptionFilter(logger).OnException(context);

        AssertInternalError(context, logger);
    }

    [Fact]
    public void 过滤器_用户取消形态的SqlException但RequestAborted未触发仍按Error记录并返回500()
    {
        // 服务端自己的 CTS 超时同样产生「Operation cancelled by user」，请求令牌未触发时不能当客户端中止。
        var logger = new TestLogger<ApiExceptionFilter>();
        var context = CreateExceptionContext(SqlClientExceptionShapes.UserCancellation(), clientAborted: false);

        new ApiExceptionFilter(logger).OnException(context);

        AssertInternalError(context, logger);
    }

    [Fact]
    public async Task 中间件_认证阶段SqlClient用户取消的SqlException不记Error不外抛并标记499()
    {
        // 生产 09-28 容器商品查询：认证阶段会话校验被取消，中间件与 Kestrel 各记一条 Error；
        // 现在中间件识别后不再外抛，Kestrel 那条随之消失，只剩 JwtBearerHandler 自身那条（事件里不能 Fail 吞取消）。
        var logger = new TestLogger<ApplicationExceptionLoggingMiddleware>();
        var httpContext = CreateHttpContext(clientAborted: true);
        var middleware = new ApplicationExceptionLoggingMiddleware(
            _ => throw SqlClientExceptionShapes.UserCancellationWithBatchAborted(),
            logger
        );

        await middleware.InvokeAsync(httpContext);

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, httpContext.Response.StatusCode);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.LogLevel);
        Assert.Contains(QueryPath, entry.Message);
    }

    [Fact]
    public async Task 中间件_SqlClient命令超时即使客户端已中止仍按Error记录并外抛()
    {
        var logger = new TestLogger<ApplicationExceptionLoggingMiddleware>();
        var httpContext = CreateHttpContext(clientAborted: true);
        var middleware = new ApplicationExceptionLoggingMiddleware(
            _ => throw SqlClientExceptionShapes.CommandTimeout(),
            logger
        );

        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => middleware.InvokeAsync(httpContext));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.LogLevel);
    }

    private static void AssertInternalError(ExceptionContext context, TestLogger<ApiExceptionFilter> logger)
    {
        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<JsonResult>(context.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        var body = Assert.IsType<ApiResponse<object>>(result.Value);
        Assert.False(body.Success);
        Assert.Equal("INTERNAL_ERROR", body.ErrorCode);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.LogLevel);
        Assert.Same(context.Exception, entry.Exception);
    }

    private static ExceptionContext CreateExceptionContext(Exception exception, bool clientAborted)
    {
        var actionContext = new ActionContext(
            CreateHttpContext(clientAborted),
            new RouteData(),
            new ActionDescriptor()
        );
        return new ExceptionContext(actionContext, new List<IFilterMetadata>()) { Exception = exception };
    }

    private static DefaultHttpContext CreateHttpContext(bool clientAborted)
    {
        // 用手动取消的 CTS 模拟 Kestrel 在客户端断开时触发 RequestAborted。
        var aborted = new CancellationTokenSource();
        if (clientAborted)
        {
            aborted.Cancel();
        }

        var httpContext = new DefaultHttpContext { RequestAborted = aborted.Token };
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.Path = QueryPath;
        return httpContext;
    }

    /// <summary>模拟响应头已发出（HasStarted=true）的响应特性。</summary>
    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }
}
