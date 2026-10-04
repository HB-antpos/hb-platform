using BlazorApp.Api.Authorization;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers;

[ApiController]
[Route("api/minor-employment")]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class EmployeeMinorComplianceController : ControllerBase
{
    private readonly EmployeeMinorComplianceService _service;
    public EmployeeMinorComplianceController(EmployeeMinorComplianceService service) => _service = service;

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

    [HttpPost("guardian/preview")]
    [AllowAnonymous]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ApiResponse<EmployeeMinorComplianceDto>> GetGuardian([FromBody] EmployeeMinorComplianceGuardianPreviewDto request)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return await _service.GetGuardianAsync(request.Token);
    }

    [HttpPost("guardian/sign")]
    [AllowAnonymous]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ApiResponse<EmployeeMinorComplianceDto>> SignGuardian([FromBody] EmployeeMinorComplianceGuardianSignRequestDto request)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        return await _service.SignGuardianAsync(request.Token, request.ToSignDto());
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
