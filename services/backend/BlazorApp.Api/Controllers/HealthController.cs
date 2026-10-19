using BlazorApp.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BlazorApp.Api.Controllers
{
    /// <summary>
    /// 健康检查控制器
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class HealthController : ControllerBase
    {
        /// <summary>下载测速默认大小（KB），与移动端网络测试一致。</summary>
        public const int DefaultDownloadKilobytes = 512;
        /// <summary>下载测速上限（KB）：防止匿名接口被当成大流量下载源。</summary>
        public const int MaxDownloadKilobytes = 1024;
        public const int MinDownloadKilobytes = 16;

        private readonly ILogger<HealthController> _logger;
        private readonly IClientIpResolver _clientIpResolver;
        private readonly INetworkDiagnosticsProbe _diagnosticsProbe;

        public HealthController(
            ILogger<HealthController> logger,
            IClientIpResolver clientIpResolver,
            INetworkDiagnosticsProbe diagnosticsProbe
        )
        {
            _logger = logger;
            _clientIpResolver = clientIpResolver;
            _diagnosticsProbe = diagnosticsProbe;
        }

        /// <summary>
        /// 健康检查端点（无需认证）
        /// </summary>
        [HttpGet]
        public IActionResult Get()
        {
            _logger.LogInformation("健康检查被调用");
            return Ok(
                new
                {
                    status = "healthy",
                    timestamp = DateTime.UtcNow,
                    message = "API服务器正在正常运行",
                }
            );
        }

        /// <summary>
        /// 移动端「网络测试」诊断（无需登录，按 IP 限流）：
        /// 返回服务器看到的客户端公网 IP（排障时可直接拿去 nginx 日志里查这台设备的请求）、
        /// 服务器时间、请求编号和数据库往返耗时。不返回任何业务数据或账号信息。
        /// </summary>
        [HttpGet("diagnostics")]
        [AllowAnonymous]
        [EnableRateLimiting(NetworkDiagnosticsRateLimits.PolicyName)]
        public async Task<IActionResult> Diagnostics(CancellationToken cancellationToken)
        {
            var clientIp = _clientIpResolver.Resolve(HttpContext);
            var databaseLatencyMs = await _diagnosticsProbe.PingDatabaseAsync(cancellationToken);
            Response.Headers.CacheControl = "no-store";
            return Ok(
                new
                {
                    status = "healthy",
                    serverTimeUtc = DateTime.UtcNow,
                    requestId = HttpContext.TraceIdentifier,
                    // 解析不出公网 IP 时返回 null，移动端显示「服务器未识别」，不把代理地址当成用户 IP。
                    clientIp = string.Equals(clientIp, "unknown", StringComparison.OrdinalIgnoreCase) ? null : clientIp,
                    database = new
                    {
                        ok = databaseLatencyMs.HasValue,
                        latencyMs = databaseLatencyMs,
                    },
                }
            );
        }

        /// <summary>
        /// 移动端「网络测试」下载测速（无需登录，按 IP 限流）：返回指定大小的随机字节。
        /// 用随机内容而不是全 0，避免链路上的压缩让测出的速度虚高；禁止缓存，保证每次真实下载。
        /// </summary>
        [HttpGet("download")]
        [AllowAnonymous]
        [EnableRateLimiting(NetworkDiagnosticsRateLimits.PolicyName)]
        public IActionResult Download([FromQuery] int kb = DefaultDownloadKilobytes)
        {
            var size = Math.Clamp(kb, MinDownloadKilobytes, MaxDownloadKilobytes) * 1024;
            var buffer = new byte[size];
            Random.Shared.NextBytes(buffer);
            Response.Headers.CacheControl = "no-store";
            return File(buffer, "application/octet-stream");
        }

        /// <summary>
        /// 认证测试端点（需要认证）
        /// </summary>
        [HttpGet("auth")]
        [Microsoft.AspNetCore.Authorization.Authorize]
        public IActionResult AuthTest()
        {
            _logger.LogInformation("认证测试被调用");
            var user = HttpContext.User;
            var claims = user.Claims.Select(c => new { c.Type, c.Value }).ToList();

            return Ok(
                new
                {
                    status = "authenticated",
                    timestamp = DateTime.UtcNow,
                    user = user.Identity?.Name,
                    claims = claims,
                }
            );
        }
    }
}
