using System.Security.Claims;
using BlazorApp.Api.Services.Logging;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers
{
    /// <summary>
    /// 移动端诊断日志补传：App 不内置长期日志 Key，改由已登录用户用自己的 token 上传本机暂存的诊断记录
    /// （如上次登录卡住时各阶段的进度），写入中心日志 HbwebExpo 项目。
    /// </summary>
    [ApiController]
    [Route("api/mobile/diagnostics")]
    [Authorize]
    public class MobileDiagnosticsController : ControllerBase
    {
        internal const string ProjectCode = "HbwebExpo";
        internal const string SourceType = "Mobile";
        internal const int MaxLogsPerRequest = 20;
        internal const int MaxRequestBodyBytes = 256 * 1024;

        private readonly ApplicationLogService _service;
        private readonly ApplicationLogRateLimiter _rateLimiter;
        private readonly ILogger<MobileDiagnosticsController> _logger;

        public MobileDiagnosticsController(
            ApplicationLogService service,
            ApplicationLogRateLimiter rateLimiter,
            ILogger<MobileDiagnosticsController> logger
        )
        {
            _service = service;
            _rateLimiter = rateLimiter;
            _logger = logger;
        }

        [HttpPost("logs")]
        [RequestSizeLimit(MaxRequestBodyBytes)]
        public async Task<ActionResult<ApiResponse<ApplicationLogIngestResultDto>>> UploadLogs(
            [FromBody] ApplicationLogIngestRequestDto? request
        )
        {
            var userGuid = User.FindFirst("userId")?.Value
                ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrWhiteSpace(userGuid))
                return Unauthorized(ApiResponse<object>.Error("缺少用户标识", "UNAUTHORIZED"));

            // 总开关沿用中心日志项目配置：compose 中 HbwebExpo 未启用时整条通道关闭。
            if (!_service.IsProjectEnabled(ProjectCode))
                return StatusCode(
                    StatusCodes.Status403Forbidden,
                    ApiResponse<object>.Error("移动端诊断日志未启用", "MOBILE_DIAGNOSTICS_DISABLED")
                );

            if (request?.Logs is { Count: > MaxLogsPerRequest })
                return BadRequest(
                    ApiResponse<object>.Error(
                        $"单次最多上传 {MaxLogsPerRequest} 条诊断日志",
                        "LOG_INGEST_INVALID"
                    )
                );

            var actualPayloadBytes = Request.ContentLength is >= 0 and <= MaxRequestBodyBytes
                ? Request.ContentLength.Value
                : (long?)null;
            if (
                !_rateLimiter.TryConsumeRequestBudget(
                    ProjectCode,
                    actualPayloadBytes ?? MaxRequestBodyBytes,
                    out var requestRateLimitMessage
                )
            )
                return StatusCode(
                    StatusCodes.Status429TooManyRequests,
                    ApiResponse<object>.Error(requestRateLimitMessage, "LOG_INGEST_RATE_LIMITED")
                );

            if (
                !_rateLimiter.TryValidateIngestRequest(
                    request,
                    actualPayloadBytes,
                    out _,
                    out var validationMessage
                )
            )
                return BadRequest(ApiResponse<object>.Error(validationMessage, "LOG_INGEST_INVALID"));

            if (!_rateLimiter.TryConsumeLogBudget(ProjectCode, request!.Logs.Count, out var rateLimitMessage))
                return StatusCode(
                    StatusCodes.Status429TooManyRequests,
                    ApiResponse<object>.Error(rateLimitMessage, "LOG_INGEST_RATE_LIMITED")
                );

            // 关键逻辑：项目、来源与用户身份只信服务端 token，客户端自报的同名字段一律覆盖。
            var userName = User.FindFirst(ClaimTypes.Name)?.Value ?? User.Identity?.Name;
            foreach (var log in request.Logs)
            {
                if (log == null)
                    continue;
                log.ProjectCode = ProjectCode;
                log.SourceType = SourceType;
                log.UserId = userGuid;
                log.UserName = userName;
                log.ClientIp = null;
            }

            try
            {
                var result = await _service.IngestAsync(
                    ProjectCode,
                    request,
                    HttpContext.Connection.RemoteIpAddress?.ToString()
                );
                return Ok(ApiResponse<ApplicationLogIngestResultDto>.OK(result, "诊断日志写入成功"));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ApiResponse<object>.Error(ex.Message, "LOG_INGEST_INVALID"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "移动端诊断日志写入失败: {UserGuid}", userGuid);
                return StatusCode(
                    StatusCodes.Status500InternalServerError,
                    ApiResponse<object>.Error("诊断日志写入失败", "LOG_INGEST_FAILED")
                );
            }
        }
    }
}
