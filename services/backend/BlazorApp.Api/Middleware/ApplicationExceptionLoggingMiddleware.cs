using BlazorApp.Api.Services.Logging;
using BlazorApp.Api.Utils;

namespace BlazorApp.Api.Middleware
{
    public class ApplicationExceptionLoggingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ApplicationExceptionLoggingMiddleware> _logger;

        public ApplicationExceptionLoggingMiddleware(
            RequestDelegate next,
            ILogger<ApplicationExceptionLoggingMiddleware> logger
        )
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (Exception ex) when (ClientAbortDetector.IsClientAbort(ex, context.RequestAborted))
            {
                // 客户端中途断开（如认证阶段会话校验被 RequestAborted 取消，OCE 或 SqlClient 用户取消的 SqlException）：
                // 不是服务端故障，降为 Information 不进 ApplicationLog；也不再向外抛出，避免被 Kestrel 当成未处理异常再记一条。
                // 服务端自身超时不会触发 RequestAborted，仍落入下方分支按 Error 记录并抛出。
                _logger.LogInformation(
                    "客户端已取消请求 - 路径: {Path}, 方法: {Method}, TraceId: {TraceId}, 异常: {ExceptionType}",
                    context.Request.Path,
                    context.Request.Method,
                    context.TraceIdentifier,
                    ex.GetType().Name
                );

                // 响应已开始时状态码不可再改；未开始时与 ApiExceptionFilter 保持一致，标记为 499。
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "请求管道异常 - 路径: {Path}, 方法: {Method}, TraceId: {TraceId}",
                    context.Request.Path,
                    context.Request.Method,
                    context.TraceIdentifier
                );
                throw;
            }
        }
    }
}
