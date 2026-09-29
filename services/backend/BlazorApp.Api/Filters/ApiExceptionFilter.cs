using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Filters
{
    public class ApiExceptionFilter : IExceptionFilter
    {
        private readonly ILogger<ApiExceptionFilter> _logger;

        public ApiExceptionFilter(ILogger<ApiExceptionFilter> logger)
        {
            _logger = logger;
        }

        public void OnException(ExceptionContext context)
        {
            // 客户端主动中止（前端切标签时 abort 上一个请求、关闭页面、断网）不是服务端故障：
            // 必须同时满足「异常是 OperationCanceledException（含 TaskCanceledException）」且「RequestAborted 已触发」，
            // 服务端自己的 CTS 超时、HttpClient 超时不会触发 RequestAborted，仍走下方按错误记录的原有逻辑。
            if (context.Exception is OperationCanceledException
                && context.HttpContext.RequestAborted.IsCancellationRequested)
            {
                // 降为 Information，低于 ApplicationLog 的 Warning 门槛，不再淹没真正的 500。
                _logger.LogInformation(
                    "客户端已取消请求 - 路径: {Path}, 方法: {Method}, 异常: {ExceptionType}",
                    context.HttpContext.Request.Path,
                    context.HttpContext.Request.Method,
                    context.Exception.GetType().Name
                );

                // 客户端已断开，正文无人接收：沿用项目既有的 499（nginx「客户端关闭请求」）约定，不写正文。
                context.Result = new StatusCodeResult(StatusCodes.Status499ClientClosedRequest);
                context.ExceptionHandled = true;
                return;
            }

            var (statusCode, errorCode, message) = GetExceptionDetails(context.Exception);

            _logger.LogError(
                context.Exception,
                "API异常 - 路径: {Path}, 方法: {Method}, 状态码: {StatusCode}, 错误: {Message}",
                context.HttpContext.Request.Path,
                context.HttpContext.Request.Method,
                statusCode,
                context.Exception.Message
            );

            var response = ApiResponse<object>.Error(message, errorCode, context.Exception.StackTrace);

            context.Result = new JsonResult(response)
            {
                StatusCode = statusCode,
                ContentType = "application/json"
            };

            context.ExceptionHandled = true;
        }

        private static (int statusCode, string errorCode, string message) GetExceptionDetails(Exception exception)
        {
            return exception switch
            {
                ArgumentException argEx => (400, "ARGUMENT_ERROR", argEx.Message),
                UnauthorizedAccessException => (401, "UNAUTHORIZED", "未经授权的访问"),
                KeyNotFoundException => (404, "NOT_FOUND", "请求的资源不存在"),
                InvalidOperationException invalidOpEx => (400, "INVALID_OPERATION", invalidOpEx.Message),
                TimeoutException => (408, "TIMEOUT", "请求超时"),
                NotImplementedException => (501, "NOT_IMPLEMENTED", "功能未实现"),
                _ => (500, "INTERNAL_ERROR", "服务器内部错误")
            };
        }
    }
}
