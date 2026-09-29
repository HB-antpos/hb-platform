using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Filters
{
    public class ApiExceptionFilter : IExceptionFilter
    {
        private readonly ILogger<ApiExceptionFilter> _logger;
        private readonly IWebHostEnvironment? _environment;

        // environment 设为可选：全局注册时由 DI 注入真实环境；直接 new（如单元测试）拿不到环境时为 null，按非 Development 处理，默认不外泄堆栈。
        public ApiExceptionFilter(ILogger<ApiExceptionFilter> logger, IWebHostEnvironment? environment = null)
        {
            _logger = logger;
            _environment = environment;
        }

        public void OnException(ExceptionContext context)
        {
            var (statusCode, errorCode, message) = GetExceptionDetails(context.Exception);

            _logger.LogError(
                context.Exception,
                "API异常 - 路径: {Path}, 方法: {Method}, 状态码: {StatusCode}, 错误: {Message}",
                context.HttpContext.Request.Path,
                context.HttpContext.Request.Method,
                statusCode,
                context.Exception.Message
            );

            var response = ApiResponse<object>.Error(message, errorCode, BuildErrorDetails(context.Exception));

            context.Result = new JsonResult(response)
            {
                StatusCode = statusCode,
                ContentType = "application/json"
            };

            context.ExceptionHandled = true;
        }

        /// <summary>
        /// 错误响应的 details：堆栈只在 Development 返回，方便本地调试；其他环境一律为 null，
        /// 完整异常已由上方 LogError 写入 ApplicationLog（带 TraceId、路径、用户），排查走服务端日志。
        /// 不在 details 里放请求标识：移动端 extractApiErrorMessage 会把 details 字符串优先当提示文案展示，会盖住 message。
        /// </summary>
        private object? BuildErrorDetails(Exception exception)
        {
            return _environment?.IsDevelopment() == true ? exception.StackTrace : null;
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
