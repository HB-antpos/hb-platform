using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Hbpos.Api.Services;

/// <summary>
/// Linkly 云回调（匿名端点）的限流：只限制“没带正确 bearer”的请求。
/// Linkly 的回调来自少数几个出口 IP，且一个门店的一笔交易会连续推送多条 display / receipt，
/// 对携带正确 bearer 的请求限流会误伤真实回调、造成按键提示和回单丢失；
/// 所以带正确 bearer 的请求不限流，其余请求按来源 IP 固定窗口限流，超出直接 429，避免公网扫描刷满日志。
/// </summary>
public sealed class LinklyNotificationRateLimitPolicy(
    IOptions<LinklyCloudBackendAsyncOptions> options) : IRateLimiterPolicy<string>
{
    public const string PolicyName = "linkly-notification-unauthorized";

    /// <summary>每个来源 IP 每分钟允许的无效鉴权请求数。</summary>
    public const int UnauthorizedPermitLimit = 30;

    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected { get; } =
        (context, _) =>
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return ValueTask.CompletedTask;
        };

    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        if (HasConfiguredBearer(httpContext))
        {
            return RateLimitPartition.GetNoLimiter("authorized");
        }

        return RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            static _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = UnauthorizedPermitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            });
    }

    private bool HasConfiguredBearer(HttpContext httpContext)
    {
        var header = httpContext.Request.Headers.Authorization.ToString().Trim();
        if (string.IsNullOrEmpty(header))
        {
            return false;
        }

        var configured = options.Value;
        return Matches(header, configured.ProductionNotificationBearer)
            || Matches(header, configured.SandboxNotificationBearer);
    }

    private static bool Matches(string header, string? bearer)
    {
        if (string.IsNullOrWhiteSpace(bearer))
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes($"Bearer {bearer.Trim()}");
        var actual = Encoding.UTF8.GetBytes(header);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
