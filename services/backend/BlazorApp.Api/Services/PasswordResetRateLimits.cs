using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace BlazorApp.Api.Services;

/// <summary>
/// 设置 / 找回密码的匿名接口按可信 IP 限流，挡住批量探测邮箱与暴力猜验证码。
/// 每个验证码自身另有错 5 次作废、同账号 60 秒发送冷却，这里只做入口级兜底。
/// </summary>
public static class PasswordResetRateLimits
{
    public const string PolicyName = "PasswordReset";

    public static void Configure(RateLimiterOptions options)
    {
        options.AddPolicy(PolicyName, context => RateLimitPartition.GetFixedWindowLimiter(
            context.RequestServices.GetService<IClientIpResolver>()?.Resolve(context)
                ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10, Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0, AutoReplenishment = true,
            }));

        // 保留之前注册的限流响应，只接管本策略的 429 文案。
        var previousOnRejected = options.OnRejected;
        options.OnRejected = async (context, cancellationToken) =>
        {
            if (context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName != PolicyName)
            {
                if (previousOnRejected is not null) await previousOnRejected(context, cancellationToken);
                return;
            }
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.HttpContext.Response.Headers.RetryAfter = "60";
            await context.HttpContext.Response.WriteAsJsonAsync(new { success = false, message = "操作过于频繁，请一分钟后再试", errorCode = "RATE_LIMITED" }, cancellationToken);
        };
    }
}
