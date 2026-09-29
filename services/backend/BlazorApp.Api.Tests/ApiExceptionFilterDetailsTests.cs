using System.Runtime.CompilerServices;
using System.Text.Json;
using BlazorApp.Api.Filters;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 全局异常过滤器的错误响应：非 Development 环境不得把服务器堆栈写进 details；
/// 状态码、errorCode、message 的映射保持不变（400 类仍原样透传业务提示）。
/// </summary>
public class ApiExceptionFilterDetailsTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void 非Development环境500响应不含堆栈(string environmentName)
    {
        var exception = CaptureThrown(new NullReferenceException("内部对象为空"));
        var context = CreateExceptionContext(exception);

        new ApiExceptionFilter(NullLogger<ApiExceptionFilter>.Instance, CreateEnvironment(environmentName))
            .OnException(context);

        var result = AssertErrorResponse(context, 500, "INTERNAL_ERROR", "服务器内部错误", out var body);
        Assert.Null(body.Details);
        // 以序列化后的正文兜底：响应 JSON 里不能出现抛出点的堆栈帧
        Assert.DoesNotContain(nameof(ThrowFromServiceLayer), JsonSerializer.Serialize(result.Value), StringComparison.Ordinal);
    }

    [Fact]
    public void 未注入环境时按非Development处理不含堆栈()
    {
        var exception = CaptureThrown(new Exception("未知故障"));
        var context = CreateExceptionContext(exception);

        // 直接 new（不经 DI）拿不到环境，必须默认安全，不能退回返回堆栈
        new ApiExceptionFilter(NullLogger<ApiExceptionFilter>.Instance).OnException(context);

        AssertErrorResponse(context, 500, "INTERNAL_ERROR", "服务器内部错误", out var body);
        Assert.Null(body.Details);
    }

    [Fact]
    public void Development环境500响应保留堆栈便于本地调试()
    {
        var exception = CaptureThrown(new Exception("未知故障"));
        var context = CreateExceptionContext(exception);

        new ApiExceptionFilter(NullLogger<ApiExceptionFilter>.Instance, CreateEnvironment("Development"))
            .OnException(context);

        AssertErrorResponse(context, 500, "INTERNAL_ERROR", "服务器内部错误", out var body);
        var details = Assert.IsType<string>(body.Details);
        Assert.Equal(exception.StackTrace, details);
        Assert.Contains(nameof(ThrowFromServiceLayer), details, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("argument", 400, "ARGUMENT_ERROR", "货号不能为空")]
    [InlineData("invalid-operation", 400, "INVALID_OPERATION", "订单已提交，不能重复提交")]
    [InlineData("unauthorized", 401, "UNAUTHORIZED", "未经授权的访问")]
    [InlineData("not-found", 404, "NOT_FOUND", "请求的资源不存在")]
    [InlineData("timeout", 408, "TIMEOUT", "请求超时")]
    [InlineData("not-implemented", 501, "NOT_IMPLEMENTED", "功能未实现")]
    [InlineData("other", 500, "INTERNAL_ERROR", "服务器内部错误")]
    public void 生产环境状态码errorCode与message映射保持不变(
        string kind,
        int expectedStatusCode,
        string expectedErrorCode,
        string expectedMessage
    )
    {
        var exception = CaptureThrown(CreateException(kind));
        var context = CreateExceptionContext(exception);

        new ApiExceptionFilter(NullLogger<ApiExceptionFilter>.Instance, CreateEnvironment("Production"))
            .OnException(context);

        AssertErrorResponse(context, expectedStatusCode, expectedErrorCode, expectedMessage, out var body);
        Assert.Null(body.Details);
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    public void 按Program全局注册方式经DI构造时注入真实环境(string environmentName, bool expectStackTrace)
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(CreateEnvironment(environmentName))
            .BuildServiceProvider();
        // 与 Program.cs 的 options.Filters.Add<ApiExceptionFilter>() 相同：由 TypeFilterAttribute 经 ActivatorUtilities 构造
        var factory = Assert.IsAssignableFrom<IFilterFactory>(new MvcOptions().Filters.Add<ApiExceptionFilter>());
        var filter = Assert.IsType<ApiExceptionFilter>(factory.CreateInstance(services));
        var exception = CaptureThrown(new Exception("未知故障"));
        var context = CreateExceptionContext(exception);

        filter.OnException(context);

        AssertErrorResponse(context, 500, "INTERNAL_ERROR", "服务器内部错误", out var body);
        Assert.Equal(expectStackTrace ? exception.StackTrace : null, body.Details);
    }

    private static JsonResult AssertErrorResponse(
        ExceptionContext context,
        int expectedStatusCode,
        string expectedErrorCode,
        string expectedMessage,
        out ApiResponse<object> body
    )
    {
        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<JsonResult>(context.Result);
        Assert.Equal(expectedStatusCode, result.StatusCode);
        Assert.Equal("application/json", result.ContentType);
        body = Assert.IsType<ApiResponse<object>>(result.Value);
        Assert.False(body.Success);
        Assert.Equal(expectedErrorCode, body.ErrorCode);
        Assert.Equal(expectedMessage, body.Message);
        return result;
    }

    private static Exception CreateException(string kind)
    {
        return kind switch
        {
            // 400 类原样透传业务提示，前端靠它展示原因
            "argument" => new ArgumentException("货号不能为空"),
            "invalid-operation" => new InvalidOperationException("订单已提交，不能重复提交"),
            // 其余类型的内部 message 不应出现在响应里，改用固定文案
            "unauthorized" => new UnauthorizedAccessException("内部：令牌签名校验失败"),
            "not-found" => new KeyNotFoundException("内部：字典里没有 key=42"),
            "timeout" => new TimeoutException("内部：SQL 命令超时"),
            "not-implemented" => new NotImplementedException("内部：尚未实现的分支"),
            _ => new Exception("内部：连接串 Server=10.0.0.1"),
        };
    }

    /// <summary>
    /// 真实抛出再捕获：只 new 不抛的异常 StackTrace 为 null，无法区分"没写堆栈"和"堆栈本来就空"。
    /// </summary>
    private static Exception CaptureThrown(Exception exception)
    {
        try
        {
            ThrowFromServiceLayer(exception);
        }
        catch (Exception caught)
        {
            Assert.False(string.IsNullOrEmpty(caught.StackTrace));
            return caught;
        }

        throw new InvalidOperationException("ThrowFromServiceLayer 必须抛出异常");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowFromServiceLayer(Exception exception) => throw exception;

    private static IWebHostEnvironment CreateEnvironment(string environmentName)
    {
        return Mock.Of<IWebHostEnvironment>(environment => environment.EnvironmentName == environmentName);
    }

    private static ExceptionContext CreateExceptionContext(Exception exception)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.Path = "/api/react/v1/orders";
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ExceptionContext(actionContext, new List<IFilterMetadata>()) { Exception = exception };
    }
}
