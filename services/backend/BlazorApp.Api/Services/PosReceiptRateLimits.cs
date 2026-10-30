using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace BlazorApp.Api.Services;

/// <summary>
/// 客人在线小票的匿名接口按可信 IP 限流，挡住对 12 位令牌的暴力枚举。
/// 令牌有 60 位熵，每分钟 30 次的上限下枚举在数学上不可行；正常客人一张小票只会请求一两次。
/// </summary>
public static class PosReceiptRateLimits
{
    public const string PolicyName = "PosReceiptPublic";

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
            await context.HttpContext.Response.WriteAsJsonAsync(new { success = false, message = "Too many requests. Please try again in a minute.", errorCode = "RATE_LIMITED" }, cancellationToken);
        };
    }
}
