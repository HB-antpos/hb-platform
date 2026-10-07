using System.Security.Claims;
using BlazorApp.Shared.DTOs;
using Hbpos.Api.Auth;
using Hbpos.Api.Logging;
using Hbpos.Api.Services;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.OperationAudits;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Hbpos.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/operation-audits")]
public sealed class OperationAuditsController(
    IOperationAuditIngestService ingestService,
    ILogger<OperationAuditsController>? logger = null) : ControllerBase
{
    internal const int MaximumBatchSize = 100;
    internal const long MaximumRequestBytes = 4L * 1024 * 1024;

    [HttpGet]
    [Authorize(Policy = CashierAuthorizationPolicies.OperationAuditView)]
    public async Task<ActionResult<OperationAuditReadListDto>> List(
        [FromQuery] string? keyword,
        [FromQuery] int limit,
        [FromServices] IOperationAuditReadService readService,
        CancellationToken cancellationToken)
    {
        if (!TryGetDeviceScope(out var storeCode, out var deviceCode))
        {
            return Unauthorized(new
            {
                code = "DEVICE_AUTH_REQUIRED",
                message = "Device scope claims are required."
            });
        }

        var result = await readService.ListAsync(
            storeCode,
            deviceCode,
            keyword,
            Math.Clamp(limit <= 0 ? MaximumBatchSize : limit, 1, MaximumBatchSize),
            cancellationToken);
        return Ok(result);
    }

    [HttpGet("{eventId:guid}")]
    [Authorize(Policy = CashierAuthorizationPolicies.OperationAuditView)]
    public async Task<ActionResult<OperationAuditReadRecordDto>> Detail(
        Guid eventId,
        [FromServices] IOperationAuditReadService readService,
        CancellationToken cancellationToken)
    {
        if (!TryGetDeviceScope(out var storeCode, out var deviceCode))
        {
            return Unauthorized(new
            {
                code = "DEVICE_AUTH_REQUIRED",
                message = "Device scope claims are required."
            });
        }

        var result = await readService.GetAsync(
            storeCode,
            deviceCode,
            eventId,
            cancellationToken);
        return result is null
            ? NotFound(new { code = "AUDIT_NOT_FOUND", message = "Operation audit was not found." })
            : Ok(result);
    }

    [HttpPost("batch")]
    [RequestSizeLimit(MaximumRequestBytes)]
    public async Task<ActionResult<OperationAuditBatchResultDto>> Batch(
        [FromBody] OperationAuditBatchRequestDto? request,
        CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return Unauthorized(new { code = "DEVICE_AUTH_REQUIRED", message = "Device authorization is required." });
        }

        var storeCode = User.FindFirstValue(DeviceAuthConstants.StoreCodeClaim);
        var deviceCode = User.FindFirstValue(DeviceAuthConstants.DeviceCodeClaim);
        var deviceSystem = User.FindFirstValue(DeviceAuthConstants.DeviceSystemClaim);
        if (string.IsNullOrWhiteSpace(storeCode) || string.IsNullOrWhiteSpace(deviceCode))
        {
            return Unauthorized(new { code = "DEVICE_AUTH_REQUIRED", message = "Device scope claims are required." });
        }

        if (Request.ContentLength > MaximumRequestBytes)
        {
            LogBatchRejected(StatusCodes.Status413PayloadTooLarge, "PAYLOAD_TOO_LARGE", storeCode, deviceCode, eventCount: null);
            return StatusCode(StatusCodes.Status413PayloadTooLarge, new
            {
                code = "PAYLOAD_TOO_LARGE",
                message = "Request body must not exceed 4 MiB."
            });
        }

        if (request?.Events is null || request.Events.Count == 0)
        {
            LogBatchRejected(StatusCodes.Status400BadRequest, "EVENTS_REQUIRED", storeCode, deviceCode, eventCount: 0);
            return BadRequest(new { code = "EVENTS_REQUIRED", message = "At least one event is required." });
        }

        if (request.Events.Count > MaximumBatchSize)
        {
            LogBatchRejected(StatusCodes.Status400BadRequest, "BATCH_TOO_LARGE", storeCode, deviceCode, request.Events.Count);
            return BadRequest(new { code = "BATCH_TOO_LARGE", message = "A batch can contain at most 100 events." });
        }

        if (request.Events.Any(static item => item is null))
        {
            LogBatchRejected(StatusCodes.Status400BadRequest, "EVENT_REQUIRED", storeCode, deviceCode, request.Events.Count);
            return BadRequest(new { code = "EVENT_REQUIRED", message = "Batch events cannot contain null." });
        }

        // 门店和终端以认证 claims 为准；请求体只允许完全匹配，不能被客户端静默改写。
        if (request.Events.Any(item =>
                !string.Equals(item.StoreCode, storeCode, StringComparison.Ordinal) ||
                !string.Equals(item.DeviceCode, deviceCode, StringComparison.Ordinal)))
        {
            LogBatchRejected(StatusCodes.Status403Forbidden, "DEVICE_SCOPE_FORBIDDEN", storeCode, deviceCode, request.Events.Count);
            return Forbid();
        }

        var result = await ingestService.IngestAsync(
            request,
            storeCode,
            deviceCode,
            cancellationToken,
            deviceSystem);
        if (result.RejectedCount > 0)
        {
            // 单条事件被拒仍返回 200，客户端视为已处理不再重传；不记日志就再也查不到丢了哪些审计事件。
            var rejected = result.Results.Where(static item => item.Status == "rejected").ToList();
            logger?.LogWarning(
                RejectionEventIds.Create("OperationAudit", StatusCodes.Status200OK, "EVENTS_REJECTED"),
                "Operation audit batch rejected events store={StoreCode} device={DeviceCode} rejected={RejectedCount} total={TotalCount} codes={ErrorCodes} firstEventId={FirstEventId}",
                storeCode,
                deviceCode,
                result.RejectedCount,
                request.Events.Count,
                string.Join(",", rejected.Select(static item => item.ErrorCode).Distinct()),
                rejected.FirstOrDefault()?.EventId);
        }

        return Ok(result);
    }

    private void LogBatchRejected(int statusCode, string code, string storeCode, string deviceCode, int? eventCount)
    {
        logger?.LogWarning(
            RejectionEventIds.Create("OperationAudit", statusCode, code),
            "Operation audit batch rejected status={StatusCode} code={Code} store={StoreCode} device={DeviceCode} events={EventCount}",
            statusCode,
            code,
            storeCode,
            deviceCode,
            eventCount);
    }

    private bool TryGetDeviceScope(out string storeCode, out string deviceCode)
    {
        storeCode = User.FindFirstValue(DeviceAuthConstants.StoreCodeClaim) ?? string.Empty;
        deviceCode = User.FindFirstValue(DeviceAuthConstants.DeviceCodeClaim) ?? string.Empty;
        return User.Identity?.IsAuthenticated == true
            && !string.IsNullOrWhiteSpace(storeCode)
            && !string.IsNullOrWhiteSpace(deviceCode);
    }
}
