using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace BlazorApp.Api.Services;

/// <summary>
/// 移动端「网络测试」的匿名诊断接口（诊断信息、下载测速）按可信 IP 限流。
/// 一次完整测试只调用诊断 1 次 + 下载 1 次；同一门店多台设备共用出口 IP，
/// 每分钟 30 次足够正常排障，又能挡住把下载接口当流量放大器反复拉取。
/// </summary>
public static class NetworkDiagnosticsRateLimits
{
    public const string PolicyName = "NetworkDiagnostics";

    public static void Configure(RateLimiterOptions options)
    {
        // 分桶键不能直接用 IClientIpResolver：生产里它几乎总是 unknown，全体设备会共用一个桶。
        options.AddPolicy(PolicyName, context => RateLimitPartition.GetFixedWindowLimiter(
            NetworkDiagnosticsClientIp.PartitionKey(context, context.RequestServices.GetService<IClientIpResolver>()),
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
            await context.HttpContext.Response.WriteAsJsonAsync(new { success = false, message = "测试过于频繁，请一分钟后再试", errorCode = "RATE_LIMITED" }, cancellationToken);
        };
    }
}
