using BlazorApp.Api.Authorization;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BlazorApp.Api.Controllers;

[ApiController]
[Route("api/minor-employment")]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class EmployeeMinorComplianceController : ControllerBase
{
    private readonly EmployeeMinorComplianceService _service;
    private readonly IClientIpResolver? _clientIp;
    public EmployeeMinorComplianceController(EmployeeMinorComplianceService service, IClientIpResolver? clientIp = null) { _service = service; _clientIp = clientIp; }

    [HttpGet("me")]
    [Authorize(Policy = Permissions.EmployeeProfiles.View)]
    public Task<ApiResponse<EmployeeMinorComplianceDto?>> GetSelf() => _service.GetSelfAsync();

    [HttpPut("me")]
    [Authorize(Policy = Permissions.EmployeeProfiles.Edit)]
    public Task<ApiResponse<EmployeeMinorComplianceDto>> Upsert([FromBody] EmployeeMinorComplianceUpsertDto dto) => _service.UpsertSelfAsync(dto);

    [HttpPost("me/guardian-invite")]
    [Authorize(Policy = Permissions.EmployeeProfiles.Edit)]
    public Task<ApiResponse<EmployeeMinorComplianceInviteResultDto>> Invite([FromBody] EmployeeMinorComplianceInviteDto dto) => _service.InviteGuardianAsync(dto);

    [HttpPost("me/submit")]
    [Authorize(Policy = Permissions.EmployeeProfiles.Edit)]
    public Task<ApiResponse<EmployeeMinorComplianceDto>> Submit([FromQuery] int version) => _service.SubmitAsync(version);

    [HttpGet("me/history")]
    [Authorize(Policy = Permissions.EmployeeProfiles.View)]
    public Task<ApiResponse<List<EmployeeMinorComplianceHistoryItemDto>>> SelfHistory() => _service.GetSelfHistoryAsync();

    /// <summary>员工本人未完成的店长填写请求。</summary>
    [HttpGet("me/requests")]
    [Authorize(Policy = Permissions.EmployeeProfiles.View)]
    public Task<ApiResponse<List<EmployeeMinorComplianceRequestDto>>> SelfRequests() => _service.GetSelfRequestsAsync();

    // 店长端：沿用排班提醒待办的权限口径（查看门店排班 / 编辑可管理门店排班），门店范围由服务按可管理门店校验。
    [HttpGet("manager/candidates")]
    [Authorize(Policy = Permissions.Attendance.Schedule.ViewStore)]
    public Task<ApiResponse<List<EmployeeMinorManagerCandidateDto>>> ManagerCandidates([FromQuery] string? storeCode) => _service.GetManagerCandidatesAsync(storeCode);

    [HttpPost("manager/requests")]
    [Authorize(Policy = Permissions.Attendance.Schedule.EditManagedStore)]
    public Task<ApiResponse<EmployeeMinorComplianceRequestDto>> CreateRequest([FromBody] EmployeeMinorComplianceRequestCreateDto dto) => _service.CreateRequestAsync(dto);

    [HttpPost("manager/requests/{id:int}/cancel")]
    [Authorize(Policy = Permissions.Attendance.Schedule.EditManagedStore)]
    public Task<ApiResponse<EmployeeMinorComplianceRequestDto>> CancelRequest(int id) => _service.CancelRequestAsync(id);

    [HttpPost("guardian/session")]
    [AllowAnonymous]
    [EnableRateLimiting(MinorGuardianRateLimits.PolicyName)]
    public async Task<ApiResponse<EmployeeMinorComplianceGuardianSessionDto>> GuardianSession([FromBody] EmployeeMinorComplianceGuardianPreviewDto request)
    {
        NoStore();
        return await _service.GetGuardianSessionAsync(request.Token, request.SessionKey);
    }

    [HttpPost("guardian/send-code")]
    [AllowAnonymous]
    [EnableRateLimiting(MinorGuardianRateLimits.PolicyName)]
    public async Task<ApiResponse<EmployeeMinorComplianceGuardianSessionDto>> GuardianSendCode([FromBody] EmployeeMinorComplianceGuardianPreviewDto request)
    {
        NoStore();
        return await _service.SendGuardianCodeAsync(request.Token);
    }

    [HttpPost("guardian/verify")]
    [AllowAnonymous]
    [EnableRateLimiting(MinorGuardianRateLimits.PolicyName)]
    public async Task<ApiResponse<string>> GuardianVerify([FromBody] EmployeeMinorComplianceGuardianVerifyDto request)
    {
        NoStore();
        return await _service.VerifyGuardianCodeAsync(request.Token, request.Code);
    }

    [HttpPost("guardian/preview")]
    [AllowAnonymous]
    [EnableRateLimiting(MinorGuardianRateLimits.PolicyName)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ApiResponse<EmployeeMinorComplianceDto>> GetGuardian([FromBody] EmployeeMinorComplianceGuardianPreviewDto request)
    {
        NoStore();
        return await _service.GetGuardianAsync(request.Token, request.SessionKey);
    }

    [HttpPost("guardian/sign")]
    [AllowAnonymous]
    [EnableRateLimiting(MinorGuardianRateLimits.PolicyName)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ApiResponse<EmployeeMinorComplianceDto>> SignGuardian([FromBody] EmployeeMinorComplianceGuardianSignRequestDto request)
    {
        NoStore();
        var ip = _clientIp?.Resolve(HttpContext) ?? HttpContext.Connection.RemoteIpAddress?.ToString();
        return await _service.SignGuardianAsync(request.Token, request.ToSignDto(), request.SessionKey, ip, Request.Headers.UserAgent.ToString());
    }

    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }

    [HttpGet("hr")]
    [EmployeeProfileSensitiveRoles]
    [Authorize(Policy = Permissions.EmployeeProfiles.View)]
    public Task<ApiResponse<PagedResult<EmployeeMinorComplianceDto>>> GetHrList([FromQuery] string? stateCode, [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? keyword = null) => _service.GetHrListAsync(stateCode, status, page, pageSize, keyword);

    [HttpGet("hr/{id:int}")]
    [EmployeeProfileSensitiveRoles]
    [Authorize(Policy = Permissions.EmployeeProfiles.View)]
    public Task<ApiResponse<EmployeeMinorComplianceDto>> GetHrDetail(int id) => _service.GetHrDetailAsync(id);

    [HttpGet("hr/{id:int}/history")]
    [EmployeeProfileSensitiveRoles]
    [Authorize(Policy = Permissions.EmployeeProfiles.View)]
    public Task<ApiResponse<List<EmployeeMinorComplianceHistoryItemDto>>> HrHistory(int id) => _service.GetHrHistoryAsync(id);

    [HttpPost("hr/{id:int}/approve")]
    [EmployeeProfileSensitiveRoles]
    [Authorize(Policy = Permissions.EmployeeProfiles.Edit)]
    public Task<ApiResponse<EmployeeMinorComplianceDto>> Approve(int id, [FromBody] EmployeeMinorComplianceReviewDto dto) => _service.ReviewAsync(id, dto, true);

    [HttpPost("hr/{id:int}/return")]
    [EmployeeProfileSensitiveRoles]
    [Authorize(Policy = Permissions.EmployeeProfiles.Edit)]
    public Task<ApiResponse<EmployeeMinorComplianceDto>> Return(int id, [FromBody] EmployeeMinorComplianceReviewDto dto) => _service.ReviewAsync(id, dto, false);

    [HttpGet("hr/{id:int}/document")]
    [EmployeeProfileSensitiveRoles]
    [Authorize(Policy = Permissions.EmployeeProfiles.View)]
    public Task<ApiResponse<EmployeeMinorComplianceDocumentDto>> Document(int id) => _service.GetDocumentAsync(id);

    [HttpGet("hr/{id:int}/document/download")]
    [EmployeeProfileSensitiveRoles]
    [Authorize(Policy = Permissions.EmployeeProfiles.View)]
    public async Task<IActionResult> DownloadDocument(int id)
    {
        var result = await _service.DownloadDocumentAsync(id);
        if (!result.Success || result.Data is null) return NotFound(result);
        return File(result.Data, "application/pdf", $"minor-employment-{id}.pdf");
    }
}
