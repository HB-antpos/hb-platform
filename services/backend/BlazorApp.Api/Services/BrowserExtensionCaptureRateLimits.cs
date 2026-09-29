using System.Security.Claims;
using System.Threading.RateLimiting;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.RateLimiting;

namespace BlazorApp.Api.Services;

/// <summary>
/// 扩展分类采集写入的限流：按登录用户分区，每分钟 120 次，主动采集的限速（默认 1.5 秒一页）远低于此上限。
/// </summary>
public static class BrowserExtensionCaptureRateLimits
{
    public const string PolicyName = "browser-extension-category-capture";
    public const string RateLimitedErrorCode = "SUPPLIER_CATEGORY_RATE_LIMITED";

    internal const int PermitLimit = 120;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static void Configure(RateLimiterOptions options)
    {
        options.AddPolicy(
            PolicyName,
            context => RateLimitPartition.GetFixedWindowLimiter(
                ResolvePartitionKey(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    AutoReplenishment = true,
                    PermitLimit = PermitLimit,
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    Window = Window,
                }
            )
        );

        // 串联之前注册的拒绝回调，避免覆盖其他限流策略的响应。
        var previousOnRejected = options.OnRejected;
        options.OnRejected = async (context, cancellationToken) =>
        {
            var policyName = context
                .HttpContext.GetEndpoint()
                ?.Metadata.GetMetadata<EnableRateLimitingAttribute>()
                ?.PolicyName;
            if (!string.Equals(policyName, PolicyName, StringComparison.Ordinal))
            {
                if (previousOnRejected is not null)
                {
                    await previousOnRejected(context, cancellationToken);
                }
                return;
            }

            if (context.HttpContext.Response.HasStarted)
            {
                return;
            }

            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.HttpContext.Response.Headers.RetryAfter = "60";
            await context.HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<object>.Error("供应商分类采集过于频繁，请稍后重试", RateLimitedErrorCode),
                cancellationToken
            );
        };
    }

    internal static string ResolvePartitionKey(HttpContext context)
    {
        var userKey = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.User.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(userKey))
        {
            return $"user:{userKey}";
        }

        var resolver = context.RequestServices.GetService<IClientIpResolver>();
        var clientIp = resolver?.Resolve(context);
        if (string.IsNullOrWhiteSpace(clientIp))
        {
            clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        }
        return $"ip:{clientIp}";
    }
}
