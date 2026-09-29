using System.Reflection;
using System.Security.Claims;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazorApp.Api.Filters
{
    /// <summary>
    /// 标记已停用的 HQ → HBweb 同步入口：请求通过认证授权后直接返回 410 Gone，不再进入 action。
    /// 只用于「读 HQ / HBSales → 写 HBweb」的入口；HBweb → HQ、HBweb → POSM 方向不得使用本特性。
    /// 背后的同步服务类、DI 注册与定时任务保持不变，后续再统一清理。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class HqToHbwebSyncDisabledAttribute : ActionFilterAttribute
    {
        public const string DisabledMessage = "HQ → HBweb 同步已于 2026-09-29 停用";
        public const string DisabledErrorCode = "HQ_TO_HBWEB_SYNC_DISABLED";

        public HqToHbwebSyncDisabledAttribute()
        {
            // 必须先于 ApiController 自带的 ModelStateInvalidFilter（-2000）运行：
            // 已停用的入口无论请求体是否合法都统一返回 410，而不是先报参数校验错误。
            Order = int.MinValue;
        }

        /// <summary>
        /// 同一 action 兼容两个方向时使用：承载方向的 action 参数名。为空表示整个 action 无条件停用。
        /// </summary>
        public string? ArgumentName { get; set; }

        /// <summary>
        /// 参数对象上表示方向的属性名，与 <see cref="ArgumentName"/> 配合使用。
        /// </summary>
        public string? DirectionProperty { get; set; }

        /// <summary>
        /// 唯一放行的方向值（忽略大小写与首尾空白）。其余取值和空值一律按 HQ → HBweb 拦截，
        /// 因为下游服务可能把空方向默认成 HQ → 本地。
        /// </summary>
        public string? AllowedDirection { get; set; }

        /// <summary>
        /// 是否只拦截部分请求（按参数方向判断），供契约测试区分「整个停用」与「只停用 HQ → HBweb 方向」。
        /// </summary>
        public bool IsConditional => !string.IsNullOrWhiteSpace(ArgumentName);

        public override void OnActionExecuting(ActionExecutingContext context)
        {
            if (!ShouldBlock(context))
            {
                return;
            }

            LogBlockedCall(context);
            context.Result = new ObjectResult(
                ApiResponse<object>.Error(DisabledMessage, DisabledErrorCode)
            )
            {
                StatusCode = StatusCodes.Status410Gone,
            };
        }

        private bool ShouldBlock(ActionExecutingContext context)
        {
            if (!IsConditional)
            {
                return true;
            }

            // 参数缺失（例如请求体为空）时不会发生任何写入，交给 action 自身返回 400。
            if (
                !context.ActionArguments.TryGetValue(ArgumentName!, out var argument)
                || argument is null
            )
            {
                return false;
            }

            var directionValue = argument
                .GetType()
                .GetProperty(
                    DirectionProperty ?? string.Empty,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase
                )
                ?.GetValue(argument)
                ?.ToString()
                ?.Trim();

            // 白名单放行：只有明确的反方向才继续执行，避免空值或新增方向绕过停用。
            return !string.Equals(
                directionValue,
                AllowedDirection?.Trim(),
                StringComparison.OrdinalIgnoreCase
            );
        }

        private static void LogBlockedCall(ActionExecutingContext context)
        {
            var httpContext = context.HttpContext;
            var logger =
                httpContext.RequestServices?.GetService<ILoggerFactory>()
                    ?.CreateLogger<HqToHbwebSyncDisabledAttribute>()
                ?? NullLogger<HqToHbwebSyncDisabledAttribute>.Instance;
            var user = httpContext.User;
            var forwardedFor = httpContext.Request.Headers["X-Forwarded-For"].ToString();

            // Warning 级别会进入应用日志表，便于停用后发现仍在调用这些入口的页面或脚本。
            logger.LogWarning(
                "已停用的 HQ → HBweb 同步入口仍被调用，已返回 410：{Method} {Path}（路由 {RouteTemplate}，Action {Action}），调用者 {UserName}（{UserId}），来源 {RemoteIp}",
                httpContext.Request.Method,
                httpContext.Request.Path.Value,
                context.ActionDescriptor.AttributeRouteInfo?.Template,
                context.ActionDescriptor.DisplayName,
                user?.Identity?.Name ?? "anonymous",
                user?.FindFirst("userId")?.Value ?? user?.FindFirst(ClaimTypes.NameIdentifier)?.Value,
                string.IsNullOrWhiteSpace(forwardedFor)
                    ? httpContext.Connection.RemoteIpAddress?.ToString()
                    : forwardedFor
            );
        }
    }
}
