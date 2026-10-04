using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace BlazorApp.Api.Services;

/// <summary>
/// 监护人签署页的匿名接口（会话、发验证码、校验、查看、签署）按可信 IP 限流，挡住对 token 与验证码的暴力尝试。
/// 每个验证码自身另有错 5 次作废的上限，这里只做入口级兜底。
/// </summary>
public static class MinorGuardianRateLimits
{
    public const string PolicyName = "MinorGuardian";

    public static void Configure(RateLimiterOptions options)
    {
        options.AddPolicy(PolicyName, context => RateLimitPartition.GetFixedWindowLimiter(
            context.RequestServices.GetService<IClientIpResolver>()?.Resolve(context)
                ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30, Window = TimeSpan.FromMinutes(1),
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
