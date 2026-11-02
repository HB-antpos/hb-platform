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

    // 员工本人不再填写、不再邀请监护人：改由店长 / HR 发送链接，家长在签署页补全资料并签字。
    // 旧版 App 仍可能调用这两个接口，直接返回说明，不改动任何数据。
    [HttpPut("me")]
    [Authorize(Policy = Permissions.EmployeeProfiles.Edit)]
    public ApiResponse<EmployeeMinorComplianceDto> Upsert([FromBody] EmployeeMinorComplianceUpsertDto dto) =>
        ApiResponse<EmployeeMinorComplianceDto>.Error(StaffOnlyMessage, EmployeeMinorComplianceService.StaffInviteOnlyCode);

    [HttpPost("me/guardian-invite")]
    [Authorize(Policy = Permissions.EmployeeProfiles.Edit)]
    public ApiResponse<EmployeeMinorComplianceInviteResultDto> Invite([FromBody] EmployeeMinorComplianceInviteDto dto) =>
        ApiResponse<EmployeeMinorComplianceInviteResultDto>.Error(StaffOnlyMessage, EmployeeMinorComplianceService.StaffInviteOnlyCode);

    private const string StaffOnlyMessage = "未成年用工资料改由店长或 HR 发送链接给家长填写并签字，员工本人无需操作";

    [HttpPost("me/submit")]
    [Authorize(Policy = Permissions.EmployeeProfiles.Edit)]
    public Task<ApiResponse<EmployeeMinorComplianceDto>> Submit([FromQuery] int version) => _service.SubmitAsync(version);

    [HttpGet("me/history")]
    [Authorize(Policy = Permissions.EmployeeProfiles.View)]
    public Task<ApiResponse<List<EmployeeMinorComplianceHistoryItemDto>>> SelfHistory() => _service.GetSelfHistoryAsync();

    /// <summary>员工本人不再需要填写：旧版 App 的「店长请你填写」待办一律返回空。</summary>
    [HttpGet("me/requests")]
    [Authorize(Policy = Permissions.EmployeeProfiles.View)]
    public ApiResponse<List<EmployeeMinorComplianceRequestDto>> SelfRequests() => ApiResponse<List<EmployeeMinorComplianceRequestDto>>.OK(new());

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

    // 店长 / HR 发送家长签字链接：手机「人员管理」与 Web「员工个人信息维护」共用，沿用店长端排班权限口径，门店范围由服务校验。
    [HttpGet("staff")]
    [Authorize(Policy = Permissions.Attendance.Schedule.ViewStore)]
    public Task<ApiResponse<EmployeeMinorStaffComplianceListDto>> StaffList([FromQuery] string? storeCode, [FromQuery] string? stateCode, [FromQuery] string? keyword, [FromQuery] bool needsActionOnly = false)
        => _service.GetStaffComplianceListAsync(storeCode, stateCode, keyword, needsActionOnly);

    [HttpGet("staff/{userGuid}")]
    [Authorize(Policy = Permissions.Attendance.Schedule.ViewStore)]
    public Task<ApiResponse<EmployeeMinorStaffComplianceDetailDto>> StaffDetail(string userGuid) => _service.GetStaffComplianceAsync(userGuid);

    [HttpPost("staff/{userGuid}/guardian-link")]
    [Authorize(Policy = Permissions.Attendance.Schedule.EditManagedStore)]
    public Task<ApiResponse<EmployeeMinorGuardianLinkSendResultDto>> SendGuardianLink(string userGuid, [FromBody] EmployeeMinorGuardianLinkSendDto dto) => _service.SendGuardianLinkAsync(userGuid, dto);

    [HttpPost("staff/{userGuid}/guardian-link/revoke")]
    [Authorize(Policy = Permissions.Attendance.Schedule.EditManagedStore)]
    public Task<ApiResponse<EmployeeMinorStaffComplianceDto>> RevokeGuardianLink(string userGuid, [FromBody] EmployeeMinorGuardianLinkRevokeDto dto) => _service.RevokeGuardianLinkAsync(userGuid, dto);

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
