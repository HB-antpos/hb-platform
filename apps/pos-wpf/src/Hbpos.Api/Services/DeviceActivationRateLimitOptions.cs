using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

namespace Hbpos.Api.Services;

public static class DeviceActivationRateLimitOptions
{
    public const string PolicyName = "app-review-device-registration";

    // 按来源公网 IP 计数：同一家店的多台设备共用出口 IP，预览与激活都计入，所以额度要留给整店装机。
    public const int PermitLimit = 20;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    public static void Configure(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = async (context, cancellationToken) =>
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            // 告知客户端窗口还剩多久，收银端据此提示「请等几分钟」，而不是让店员反复点击继续占额度。
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                context.HttpContext.Response.Headers.RetryAfter =
                    Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                        .ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            await context.HttpContext.Response.WriteAsJsonAsync(
                new ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Too many device activation requests.",
                    Detail = "Retry after the device activation rate-limit window resets.",
                },
                cancellationToken);
        };
        options.AddPolicy(PolicyName, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                static _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = DeviceActivationRateLimitOptions.PermitLimit,
                    Window = DeviceActivationRateLimitOptions.Window,
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
    }
}
