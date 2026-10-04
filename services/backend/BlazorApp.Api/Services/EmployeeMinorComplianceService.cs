using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.ComponentModel.DataAnnotations;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services.Attendance;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services;

/// <summary>
/// 未成年用工档案、家长签署和 HR 审核的独立服务。
/// 合规状态只产生提醒，不参与排班发布授权判断。
/// </summary>
public sealed class EmployeeMinorComplianceService
{
    public const string ServerConsentScope = "I consent to the employer collecting and using this signed minor employment record for lawful employment administration, safety and roster risk reminders.";
    public const string VersionConflictCode = "MINOR_COMPLIANCE_VERSION_CONFLICT";
    public const string InvalidGuardianContactCode = "MINOR_COMPLIANCE_GUARDIAN_CONTACT_REQUIRED";
    private static readonly Regex PhonePattern = new(@"^[0-9+() .-]{6,30}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqlSugarContext _context;
    private readonly ICurrentUserService _currentUser;
    private readonly ICurrentUserManageableStoreScopeService _storeScope;
    private readonly ILogger<EmployeeMinorComplianceService> _logger;
    private readonly IEmployeeMinorDocumentStore? _storage;

    public EmployeeMinorComplianceService(SqlSugarContext context, ICurrentUserService currentUser, ILogger<EmployeeMinorComplianceService> logger, ICurrentUserManageableStoreScopeService storeScope, IEmployeeMinorDocumentStore? storage = null)
    { _context = context; _currentUser = currentUser; _logger = logger; _storeScope = storeScope; _storage = storage; }

    public async Task<ApiResponse<EmployeeMinorComplianceDto?>> GetSelfAsync()
    {
        var userGuid = _currentUser.GetCurrentUserGuid();
        if (string.IsNullOrWhiteSpace(userGuid)) return ApiResponse<EmployeeMinorComplianceDto?>.Error("未找到当前用户", "CURRENT_USER_NOT_FOUND");
        var row = await LatestAsync(userGuid);
        return ApiResponse<EmployeeMinorComplianceDto?>.OK(row is null ? null : await MapAsync(row));
    }

    public async Task<ApiResponse<EmployeeMinorComplianceDto>> UpsertSelfAsync(EmployeeMinorComplianceUpsertDto dto)
    {
        var userGuid = _currentUser.GetCurrentUserGuid();
        if (string.IsNullOrWhiteSpace(userGuid)) return ApiResponse<EmployeeMinorComplianceDto>.Error("未找到当前用户", "CURRENT_USER_NOT_FOUND");
        var current = await LatestAsync(userGuid);
        var employmentStore = await ResolveEmploymentStoreAsync(userGuid);
        if (employmentStore is null) return ApiResponse<EmployeeMinorComplianceDto>.Error("未找到员工当前主门店，无法核实工作州", "EMPLOYEE_STORE_REQUIRED");
        var profile = await _context.Db.Queryable<EmployeeProfile>().Where(x => x.UserGUID == userGuid && !x.IsDeleted).FirstAsync();
        if (profile?.Birthday is null) return ApiResponse<EmployeeMinorComplianceDto>.Error("员工正式资料缺少出生日期", "DOB_REQUIRED");
        if (dto.DateOfBirth.HasValue && dto.DateOfBirth.Value.Date != profile.Birthday.Value.Date) return ApiResponse<EmployeeMinorComplianceDto>.Error("出生日期必须与员工正式资料一致", "DOB_MISMATCH");
        if (current is not null && (!dto.ExpectedRevision.HasValue || current.Revision != dto.ExpectedRevision.Value || !dto.ExpectedVersion.HasValue || current.Version != dto.ExpectedVersion.Value))
            return ApiResponse<EmployeeMinorComplianceDto>.Error("档案版本已变化，请刷新后重试", VersionConflictCode);
        var expectedDraftRevision = current?.Revision ?? 0;
        EmployeeMinorCompliance row = null!;
        var transaction = await _context.Db.Ado.UseTranAsync(async () =>
        {
            if (current is not null && current.Status == "draft")
            {
                row = current;
                var oldVerified = row.EducationExemptionVerified;
                Apply(row, dto);
                row.DateOfBirth = profile.Birthday.Value.Date;
                row.EducationExemptionVerified = oldVerified == true && dto.EducationExemptionVerified == true;
                row.Revision = expectedDraftRevision + 1;
                ApplyStoreFacts(row, employmentStore);
                var affected = await _context.Db.Updateable(row).Where(x => x.Id == row.Id && x.Version == row.Version && x.Revision == expectedDraftRevision && x.Status == "draft").ExecuteCommandAsync();
                if (affected != 1) throw new InvalidOperationException(VersionConflictCode);
            }
            else
            {
                if (current is not null)
                {
                    var revoked = await _context.Db.Updateable<EmployeeMinorCompliance>()
                        .SetColumns(x => x.GuardianTokenUsed == true)
                        .SetColumns(x => x.GuardianTokenHash == null)
                        .Where(x => x.Id == current.Id && x.Version == current.Version && x.Revision == current.Revision)
                        .ExecuteCommandAsync();
                    if (revoked != 1) throw new InvalidOperationException(VersionConflictCode);
                }
                row = NewVersion(userGuid, current, dto);
                row.Revision = 1;
                row.DateOfBirth = profile.Birthday.Value.Date;
                // 学校豁免只能由 HR 在后续审核流程确认，员工草稿不能自行声明已核实。
                row.EducationExemptionVerified = false;
                ApplyStoreFacts(row, employmentStore);
                row.Status = "draft";
                row.Id = await _context.Db.Insertable(row).ExecuteReturnIdentityAsync();
            }
            await ReplaceContactsAsync(row.Id, dto.Contacts);
            await AuditAsync(row.Id, "draft_saved", null);
        });
        if (!transaction.IsSuccess) return ApiResponse<EmployeeMinorComplianceDto>.Error(transaction.ErrorMessage ?? "档案保存失败，请刷新后重试", transaction.ErrorMessage == VersionConflictCode ? VersionConflictCode : "SAVE_FAILED");
        return ApiResponse<EmployeeMinorComplianceDto>.OK(await MapAsync(row), "未成年用工草稿已保存");
    }

    public async Task<ApiResponse<EmployeeMinorComplianceInviteResultDto>> InviteGuardianAsync(EmployeeMinorComplianceInviteDto dto)
    {
        var userGuid = _currentUser.GetCurrentUserGuid();
        var row = await GetVersionAsync(userGuid, dto.Version);
        if (row is null) return ApiResponse<EmployeeMinorComplianceInviteResultDto>.Error("档案版本不存在", "NOT_FOUND");
        var latest = await LatestAsync(userGuid);
        if (latest?.Id != row.Id || row.Revision != dto.Revision) return ApiResponse<EmployeeMinorComplianceInviteResultDto>.Error("只能为当前版本发起签字", VersionConflictCode);
        if (row.StateCode is not ("QLD" or "NSW")) return ApiResponse<EmployeeMinorComplianceInviteResultDto>.Error("请先确认工作门店属于 NSW 或 QLD，再选择对应的同意书", "MINOR_COMPLIANCE_STATE_UNCONFIRMED");
        var contactError = ValidateGuardianContact(row);
        if (contactError is not null) return ApiResponse<EmployeeMinorComplianceInviteResultDto>.Error(contactError, InvalidGuardianContactCode);
        var form = Parse<EmployeeMinorCe1FormDto>(row.FormDataJson);
        var school = Parse<EmployeeMinorSchoolCalendarDto>(row.SchoolCalendarJson);
        if (form is null || string.IsNullOrWhiteSpace(form.ChildGivenName) || string.IsNullOrWhiteSpace(form.ChildFamilyName) || string.IsNullOrWhiteSpace(form.EmployerCompanyName)
            || (row.RequiredToBeEnrolled != false && (school is null || string.IsNullOrWhiteSpace(school.SchoolProvider))))
            return ApiResponse<EmployeeMinorComplianceInviteResultDto>.Error("请先完整填写孩子、雇主和学校资料后再邀请签署", "MINOR_COMPLIANCE_FORM_INCOMPLETE");
        var backupError = await ValidateBackupContactsAsync(row);
        if (backupError is not null) return ApiResponse<EmployeeMinorComplianceInviteResultDto>.Error(backupError, "MINOR_COMPLIANCE_BACKUP_CONTACT_REQUIRED");
        if (row.Status != "draft") return ApiResponse<EmployeeMinorComplianceInviteResultDto>.Error("请先保存新版本的草稿，再发起签字；已签版本保留原件", "INVALID_STATUS");
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        row.GuardianTokenHash = Hash(raw);
        row.GuardianTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(Math.Clamp(dto.ExpiryMinutes, 5, 60 * 24 * 7));
        row.GuardianTokenUsed = false;
        row.Status = "awaiting_guardian_signature";
        var inviteTransaction = await _context.Db.Ado.UseTranAsync(async () =>
        {
            var inviteAffected = await _context.Db.Updateable(row).Where(x => x.Id == row.Id && x.Version == dto.Version && x.Revision == dto.Revision && x.Status == "draft").ExecuteCommandAsync();
            if (inviteAffected != 1) throw new InvalidOperationException(VersionConflictCode);
            await AuditAsync(row.Id, "guardian_invite_created", new { expiresAtUtc = row.GuardianTokenExpiresAtUtc });
        });
        if (!inviteTransaction.IsSuccess) return ApiResponse<EmployeeMinorComplianceInviteResultDto>.Error("档案版本或状态已变化，请刷新后重试", VersionConflictCode);
        // 仅返回可由调用方交给邮件/SMS 通道的链接；本服务不会发送真实消息。
        return ApiResponse<EmployeeMinorComplianceInviteResultDto>.OK(new()
        { Id = row.Id, Version = row.Version, Revision = row.Revision, SigningUrl = $"/minor-employment/sign#token={raw}", ExpiresAtUtc = row.GuardianTokenExpiresAtUtc.Value });
    }

    public async Task<ApiResponse<EmployeeMinorComplianceDto>> SignGuardianAsync(string token, EmployeeMinorComplianceGuardianSignDto dto)
    {
        var row = await _context.Db.Queryable<EmployeeMinorCompliance>()
            .Where(x => !x.IsDeleted && x.GuardianTokenHash == Hash(token) && !x.GuardianTokenUsed).FirstAsync();
        if (row is null || row.GuardianTokenExpiresAtUtc < DateTime.UtcNow) return ApiResponse<EmployeeMinorComplianceDto>.Error("签署链接无效或已过期", "GUARDIAN_TOKEN_INVALID");
        var contactError = ValidateGuardianContact(row);
        if (contactError is not null) return ApiResponse<EmployeeMinorComplianceDto>.Error(contactError, InvalidGuardianContactCode);
        var backupError = await ValidateBackupContactsAsync(row);
        if (backupError is not null) return ApiResponse<EmployeeMinorComplianceDto>.Error(backupError, "MINOR_COMPLIANCE_BACKUP_CONTACT_REQUIRED");
        var latest = await LatestAsync(row.UserGUID);
        if (latest?.Id != row.Id) return ApiResponse<EmployeeMinorComplianceDto>.Error("旧版本签署链接已失效", VersionConflictCode);
        if (!string.Equals(Regex.Replace(dto.SignedName?.Trim() ?? string.Empty, @"\s+", " "), Regex.Replace(row.GuardianName.Trim(), @"\s+", " "), StringComparison.OrdinalIgnoreCase))
            return ApiResponse<EmployeeMinorComplianceDto>.Error("签署姓名必须与当前版本的家长/监护人姓名一致", "SIGNER_NAME_MISMATCH");
        if (string.IsNullOrWhiteSpace(dto.SignedName) || row.Version != dto.Version || !dto.ConfirmRelationship || !dto.ConfirmConsent || !dto.ConfirmBackupContact || string.IsNullOrWhiteSpace(dto.SignatureData) || !string.Equals(dto.ConsentScope, ServerConsentScope, StringComparison.Ordinal)) return ApiResponse<EmployeeMinorComplianceDto>.Error("必须确认当前版本、监护关系、备用联系人和固定签署范围", "SIGNATURE_CONFIRMATION_REQUIRED");
        row.GuardianSignedName = dto.SignedName.Trim();
        row.GuardianSignedAtUtc = DateTime.UtcNow;
        row.GuardianSignatureHash = Hash($"{row.Id}:{row.Version}:{row.GuardianSignedName}:{dto.SignatureData}:{dto.ConsentScope}:{row.GuardianSignedAtUtc:O}");
        row.GuardianTokenUsed = true;
        row.Status = "signed_pending_employee_submit";
        try
        {
            var mapped = await MapAsync(row);
            var bytes = EmployeeMinorComplianceDocumentBuilder.Build(EmployeeMinorComplianceDocumentBuilder.FromProfile(mapped, dto.SignatureData, ServerConsentScope));
            if (_storage is null) return ApiResponse<EmployeeMinorComplianceDto>.Error("原件存储未配置，暂不能完成签署", "DOCUMENT_STORAGE_UNAVAILABLE");
            var objectKey = $"minor-employment/{row.UserGUID}/v{row.Version}/{Guid.NewGuid():N}.pdf";
            var upload = await _storage.SaveAsync(objectKey, bytes);
            if (!upload.Success || string.IsNullOrWhiteSpace(upload.Data)) return ApiResponse<EmployeeMinorComplianceDto>.Error("签署原件保存失败", "DOCUMENT_STORAGE_FAILED");
            row.DocumentObjectKey = objectKey;
            row.DocumentSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "未成年用工签署原件生成失败 {ComplianceId}", row.Id);
            return ApiResponse<EmployeeMinorComplianceDto>.Error(ex.Message, "DOCUMENT_BUILD_FAILED");
        }
        var signTransaction = await _context.Db.Ado.UseTranAsync(async () =>
        {
            var affected = await _context.Db.Updateable(row).Where(x => x.Id == row.Id && x.Version == dto.Version && x.Status == "awaiting_guardian_signature" && x.GuardianTokenHash == Hash(token) && !x.GuardianTokenUsed).ExecuteCommandAsync();
            if (affected != 1) throw new InvalidOperationException("GUARDIAN_TOKEN_CONFLICT");
            await AuditAsync(row.Id, "guardian_signed", new { row.GuardianSignedAtUtc });
        });
        if (!signTransaction.IsSuccess)
        {
            // 上传与数据库不在同一事务。先独立确认未被任何版本引用，再清理本次唯一对象，避免误删胜出的签署原件。
            try
            {
                if (_storage != null && !string.IsNullOrWhiteSpace(row.DocumentObjectKey)
                    && !await _context.Db.Queryable<EmployeeMinorCompliance>().AnyAsync(x => x.DocumentObjectKey == row.DocumentObjectKey))
                {
                    var cleanup = await _storage.DeleteAsync(row.DocumentObjectKey);
                    if (!cleanup.Success) _logger.LogError("未完成签署的私有原件待清理 {ObjectKey}", row.DocumentObjectKey);
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "签署失败后的原件引用核验或清理失败 {ObjectKey}", row.DocumentObjectKey); }
            return ApiResponse<EmployeeMinorComplianceDto>.Error("签署链接已被使用或档案已更新", "GUARDIAN_TOKEN_CONFLICT");
        }
        return ApiResponse<EmployeeMinorComplianceDto>.OK(await MapAsync(row), "家长签署已完成");
    }

    public async Task<ApiResponse<EmployeeMinorComplianceDto>> GetGuardianAsync(string token)
    {
        var row = await _context.Db.Queryable<EmployeeMinorCompliance>().Where(x => !x.IsDeleted && x.GuardianTokenHash == Hash(token) && !x.GuardianTokenUsed).FirstAsync();
        if (row is null || row.GuardianTokenExpiresAtUtc < DateTime.UtcNow) return ApiResponse<EmployeeMinorComplianceDto>.Error("签署链接无效或已过期", "GUARDIAN_TOKEN_INVALID");
        var latest = await LatestAsync(row.UserGUID);
        if (latest?.Id != row.Id) return ApiResponse<EmployeeMinorComplianceDto>.Error("签署链接已因新版本失效", VersionConflictCode);
        return ApiResponse<EmployeeMinorComplianceDto>.OK(await MapAsync(row));
    }

    public async Task<ApiResponse<EmployeeMinorComplianceDto>> SubmitAsync(int version)
    {
        var userGuid = _currentUser.GetCurrentUserGuid();
        var row = await GetVersionAsync(userGuid, version);
        if (row is null) return ApiResponse<EmployeeMinorComplianceDto>.Error("档案版本不存在", "NOT_FOUND");
        var latest = await LatestAsync(userGuid);
        if (latest?.Id != row.Id) return ApiResponse<EmployeeMinorComplianceDto>.Error("只能提交当前最新版本", VersionConflictCode);
        var contactError = ValidateGuardianContact(row);
        if (contactError is not null) return ApiResponse<EmployeeMinorComplianceDto>.Error(contactError, InvalidGuardianContactCode);
        var backupError = await ValidateBackupContactsAsync(row);
        if (backupError is not null) return ApiResponse<EmployeeMinorComplianceDto>.Error(backupError, "MINOR_COMPLIANCE_BACKUP_CONTACT_REQUIRED");
        if (row.Status != "signed_pending_employee_submit") return ApiResponse<EmployeeMinorComplianceDto>.Error("必须先完成家长签署", "SIGNATURE_REQUIRED");
        row.Status = "pending_hr_review"; row.SubmittedAtUtc = DateTime.UtcNow; row.SubmittedBy = userGuid;
        var submitTransaction = await _context.Db.Ado.UseTranAsync(async () =>
        {
            var affected = await _context.Db.Updateable(row).Where(x => x.Id == row.Id && x.Version == version && x.Revision == latest.Revision && x.Status == "signed_pending_employee_submit"
                && x.Version == SqlFunc.Subqueryable<EmployeeMinorCompliance>().Where(y => y.UserGUID == x.UserGUID && !y.IsDeleted).Max(y => y.Version)).ExecuteCommandAsync();
            if (affected != 1) throw new InvalidOperationException(VersionConflictCode);
            await AuditAsync(row.Id, "submitted_for_hr_review", null);
        });
        if (!submitTransaction.IsSuccess) return ApiResponse<EmployeeMinorComplianceDto>.Error("档案版本或状态已变化，请刷新后重试", VersionConflictCode);
        return ApiResponse<EmployeeMinorComplianceDto>.OK(await MapAsync(row), "已提交 HR 审核");
    }

    public async Task<ApiResponse<PagedResult<EmployeeMinorComplianceDto>>> GetHrListAsync(string? stateCode, string? status, int page = 1, int pageSize = 20, string? keyword = null)
    {
        var scope = await _storeScope.GetScopeAsync();
        if (!scope.IsAuthenticated || !scope.IsAllowed) return ApiResponse<PagedResult<EmployeeMinorComplianceDto>>.Error("当前账号没有未成年用工审核权限", "FORBIDDEN");
        var q = _context.Db.Queryable<EmployeeMinorCompliance>().Where(x => !x.IsDeleted && x.Version == SqlFunc.Subqueryable<EmployeeMinorCompliance>().Where(y => y.UserGUID == x.UserGUID && !y.IsDeleted).Max(y => y.Version));
        if (!scope.IsAdmin) q = q.Where(x => scope.StoreGuids.Contains(x.StoreGUID ?? string.Empty));
        if (!string.IsNullOrWhiteSpace(stateCode)) q = q.Where(x => x.StateCode == stateCode);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status);
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            // 按单词查找，兼容正式表单中名字和姓氏分别保存的格式。
            foreach (var word in keyword.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(8))
                q = q.Where(x => x.UserGUID.Contains(word) || (x.FormDataJson != null && x.FormDataJson.Contains(word)));
        }
        var total = await q.CountAsync();
        var rows = await q.OrderBy(x => x.SubmittedAtUtc, OrderByType.Desc).Skip((Math.Max(1, page) - 1) * Math.Clamp(pageSize, 1, 100)).Take(Math.Clamp(pageSize, 1, 100)).ToListAsync();
        var items = new List<EmployeeMinorComplianceDto>(); foreach (var row in rows) items.Add(await MapAsync(row));
        return ApiResponse<PagedResult<EmployeeMinorComplianceDto>>.OK(new() { Items = items, Total = total, Page = page, PageSize = pageSize });
    }

    public async Task<ApiResponse<EmployeeMinorComplianceDto>> GetHrDetailAsync(int id) { var row = await _context.Db.Queryable<EmployeeMinorCompliance>().FirstAsync(x => x.Id == id && !x.IsDeleted); if (row is null) return ApiResponse<EmployeeMinorComplianceDto>.Error("档案不存在", "NOT_FOUND"); if (!await CanAccessHrRowAsync(row)) return ApiResponse<EmployeeMinorComplianceDto>.Error("无权访问该门店员工档案", "FORBIDDEN"); return ApiResponse<EmployeeMinorComplianceDto>.OK(await MapAsync(row)); }

    public async Task<ApiResponse<EmployeeMinorComplianceDto>> ReviewAsync(int id, EmployeeMinorComplianceReviewDto dto, bool approve)
    {
        var row = await _context.Db.Queryable<EmployeeMinorCompliance>().FirstAsync(x => x.Id == id && !x.IsDeleted);
        if (row is null) return ApiResponse<EmployeeMinorComplianceDto>.Error("档案不存在", "NOT_FOUND");
        if (!await CanAccessHrRowAsync(row)) return ApiResponse<EmployeeMinorComplianceDto>.Error("无权访问该门店员工档案", "FORBIDDEN");
        var latest = await LatestAsync(row.UserGUID);
        if (latest?.Id != row.Id) return ApiResponse<EmployeeMinorComplianceDto>.Error("只能处理当前最新版本", VersionConflictCode);
        if (row.Version != dto.Version || row.Status != "pending_hr_review") return ApiResponse<EmployeeMinorComplianceDto>.Error("档案版本或状态已变化，请刷新后重试", VersionConflictCode);
        if (!approve && (string.IsNullOrWhiteSpace(dto.Comment) || dto.ReturnFields.Count == 0)) return ApiResponse<EmployeeMinorComplianceDto>.Error("退回必须填写意见并选择待补充字段", "RETURN_COMMENT_AND_FIELDS_REQUIRED");
        row.Status = approve ? "approved" : "returned"; row.ReviewActor = _currentUser.GetCurrentUsername(); row.ReviewedAtUtc = DateTime.UtcNow; row.ReviewComment = dto.Comment?.Trim(); row.ReturnFieldsJson = JsonSerializer.Serialize(dto.ReturnFields, JsonOptions);
        var decision = await _context.Db.Ado.UseTranAsync(async () =>
        {
            var affected = await _context.Db.Updateable(row).Where(x => x.Id == id && x.Version == dto.Version && x.Status == "pending_hr_review"
                && x.Version == SqlFunc.Subqueryable<EmployeeMinorCompliance>().Where(y => y.UserGUID == x.UserGUID && !y.IsDeleted).Max(y => y.Version)).ExecuteCommandAsync();
            if (affected != 1) throw new InvalidOperationException(VersionConflictCode);
            await AuditAsync(id, approve ? "hr_approved" : "hr_returned", new { dto.ReturnFields, dto.Comment });
        });
        if (!decision.IsSuccess) return ApiResponse<EmployeeMinorComplianceDto>.Error("档案版本或状态已变化，请刷新后重试", VersionConflictCode);
        return ApiResponse<EmployeeMinorComplianceDto>.OK(await MapAsync(row), approve ? "审核已通过" : "已退回补充");
    }

    public async Task<ApiResponse<EmployeeMinorComplianceDocumentDto>> GetDocumentAsync(int id)
    {
        var row = await _context.Db.Queryable<EmployeeMinorCompliance>().FirstAsync(x => x.Id == id && !x.IsDeleted);
        if (row is null || string.IsNullOrWhiteSpace(row.DocumentObjectKey)) return ApiResponse<EmployeeMinorComplianceDocumentDto>.Error("原件尚未生成", "DOCUMENT_NOT_READY");
        if (!await CanAccessHrRowAsync(row)) return ApiResponse<EmployeeMinorComplianceDocumentDto>.Error("无权访问该门店原件", "FORBIDDEN");
        return ApiResponse<EmployeeMinorComplianceDocumentDto>.OK(new() { Id = id, Version = row.Version, FileName = $"minor-employment-v{row.Version}.pdf", Sha256 = row.DocumentSha256 ?? string.Empty, DownloadUrl = $"/api/minor-employment/hr/{id}/document/download", ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5) });
    }

    public async Task<ApiResponse<byte[]>> DownloadDocumentAsync(int id)
    {
        var row = await _context.Db.Queryable<EmployeeMinorCompliance>().FirstAsync(x => x.Id == id && !x.IsDeleted);
        if (row is null || string.IsNullOrWhiteSpace(row.DocumentObjectKey)) return ApiResponse<byte[]>.Error("原件尚未生成", "DOCUMENT_NOT_READY");
        if (!await CanAccessHrRowAsync(row)) return ApiResponse<byte[]>.Error("无权访问该门店原件", "FORBIDDEN");
        if (_storage is null) return ApiResponse<byte[]>.Error("原件存储未配置", "DOCUMENT_STORAGE_UNAVAILABLE");
        var result = await _storage.ReadAsync(row.DocumentObjectKey);
        if (!result.Success || result.Data is null) return ApiResponse<byte[]>.Error("原件读取失败", "DOCUMENT_DOWNLOAD_FAILED");
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(result.Data)), row.DocumentSha256, StringComparison.OrdinalIgnoreCase))
            return ApiResponse<byte[]>.Error("原件完整性校验失败，请联系 HR", "DOCUMENT_INTEGRITY_FAILED");
        return result;
    }

    public async Task<ApiResponse<List<EmployeeMinorComplianceHistoryItemDto>>> GetSelfHistoryAsync()
    {
        var userGuid = _currentUser.GetCurrentUserGuid();
        var rows = await _context.Db.Queryable<EmployeeMinorCompliance>().Where(x => x.UserGUID == userGuid && !x.IsDeleted).OrderBy(x => x.Version, OrderByType.Desc).ToListAsync();
        return ApiResponse<List<EmployeeMinorComplianceHistoryItemDto>>.OK(await MapHistoryAsync(rows));
    }

    public async Task<ApiResponse<List<EmployeeMinorComplianceHistoryItemDto>>> GetHrHistoryAsync(int id)
    {
        var current = await _context.Db.Queryable<EmployeeMinorCompliance>().FirstAsync(x => x.Id == id && !x.IsDeleted);
        if (current is null) return ApiResponse<List<EmployeeMinorComplianceHistoryItemDto>>.Error("档案不存在", "NOT_FOUND");
        if (!await CanAccessHrRowAsync(current)) return ApiResponse<List<EmployeeMinorComplianceHistoryItemDto>>.Error("无权访问该门店员工档案", "FORBIDDEN");
        var rows = await _context.Db.Queryable<EmployeeMinorCompliance>().Where(x => x.UserGUID == current.UserGUID && !x.IsDeleted).OrderBy(x => x.Version, OrderByType.Desc).ToListAsync();
        var scope = await _storeScope.GetScopeAsync();
        if (!scope.IsAdmin) rows = rows.Where(x => scope.StoreGuids.Contains(x.StoreGUID ?? string.Empty)).ToList();
        return ApiResponse<List<EmployeeMinorComplianceHistoryItemDto>>.OK(await MapHistoryAsync(rows));
    }

    private async Task<EmployeeMinorCompliance?> LatestAsync(string userGuid) => await _context.Db.Queryable<EmployeeMinorCompliance>().Where(x => x.UserGUID == userGuid && !x.IsDeleted).OrderBy(x => x.Version, OrderByType.Desc).FirstAsync();
    private async Task<EmployeeMinorCompliance?> GetVersionAsync(string userGuid, int version) => await _context.Db.Queryable<EmployeeMinorCompliance>().Where(x => x.UserGUID == userGuid && x.Version == version && !x.IsDeleted).FirstAsync();
    private EmployeeMinorCompliance NewVersion(string userGuid, EmployeeMinorCompliance? prior, EmployeeMinorComplianceUpsertDto dto) { var row = new EmployeeMinorCompliance { UserGUID = userGuid, Version = (prior?.Version ?? 0) + 1 }; Apply(row, dto); return row; }
    private static void ApplyStoreFacts(EmployeeMinorCompliance row, Store store)
    {
        row.StoreGUID = store.StoreGUID;
        row.StoreCode = store.StoreCode;
        row.StoreTimeZoneId = InstallmentOrderStoreTimeZoneResolver.Resolve(store);
        row.StateCode = PublicHolidaySyncHelper.ResolveJurisdictionFromPostcode(PublicHolidaySyncHelper.ExtractPostcodeFromAddress(store.Address))
            ?? (store.Address?.Contains("QLD", StringComparison.OrdinalIgnoreCase) == true ? "QLD" : store.Address?.Contains("NSW", StringComparison.OrdinalIgnoreCase) == true ? "NSW" : "UNKNOWN");
        row.FormType = row.StateCode == "QLD" ? "QLD_CE1" : row.StateCode == "NSW" ? "NSW_COMPANY_CONSENT" : "UNSUPPORTED_STATE";
    }
    private static void Apply(EmployeeMinorCompliance row, EmployeeMinorComplianceUpsertDto dto) { row.StateCode = (dto.StateCode ?? "QLD").Trim().ToUpperInvariant(); row.FormType = (dto.FormType ?? "QLD_CE1").Trim(); row.DateOfBirth = dto.DateOfBirth; row.SchoolName = dto.SchoolName?.Trim(); row.YearLevel = dto.YearLevel?.Trim(); row.CompletedYear10 = dto.CompletedYear10; row.EducationStatus = dto.EducationStatus?.Trim(); row.RequiredToBeEnrolled = dto.RequiredToBeEnrolled; row.EducationExemptionVerified = dto.EducationExemptionVerified; row.ParticipationEndDate = dto.ParticipationEndDate; row.SchoolCalendarJson = JsonSerializer.Serialize(dto.SchoolCalendar, JsonOptions); row.OtherWorkJson = JsonSerializer.Serialize(dto.OtherWork, JsonOptions); row.CommuteJson = JsonSerializer.Serialize(dto.Commute, JsonOptions); row.FormDataJson = JsonSerializer.Serialize(dto.FormData, JsonOptions); row.GuardianName = dto.GuardianName?.Trim() ?? string.Empty; row.GuardianPhone = string.IsNullOrWhiteSpace(dto.GuardianPhone) ? null : dto.GuardianPhone.Trim(); row.GuardianEmail = string.IsNullOrWhiteSpace(dto.GuardianEmail) ? null : dto.GuardianEmail.Trim(); row.GuardianRelationship = dto.GuardianRelationship?.Trim(); }
    private async Task ReplaceContactsAsync(int id, IEnumerable<EmployeeMinorComplianceContactDto> contacts) { await _context.Db.Deleteable<EmployeeMinorComplianceContact>().Where(x => x.ComplianceId == id).ExecuteCommandAsync(); var rows = contacts.Select((x, i) => new EmployeeMinorComplianceContact { ComplianceId = id, ContactType = i == 0 ? "backup" : "additional", FullName = x.FullName.Trim(), Phone = x.Phone.Trim(), Mobile = x.Mobile?.Trim(), Email = x.Email?.Trim(), Address = x.Address?.Trim(), Postcode = x.Postcode?.Trim(), Relationship = x.Relationship?.Trim() }).ToList(); if (rows.Count > 0) await _context.Db.Insertable(rows).ExecuteCommandAsync(); }
    private async Task<List<EmployeeMinorComplianceContact>> ContactsAsync(int id) => await _context.Db.Queryable<EmployeeMinorComplianceContact>().Where(x => x.ComplianceId == id && !x.IsDeleted).ToListAsync();
    private async Task<string?> ValidateBackupContactsAsync(EmployeeMinorCompliance row)
    {
        var contacts = await ContactsAsync(row.Id);
        if (contacts.Count < 1) return "必须填写至少一名独立备用紧急联系人（家长之外）";
        if (contacts.Any(x => string.IsNullOrWhiteSpace(x.FullName) || string.IsNullOrWhiteSpace(x.Phone) || !ValidPhone(x.Phone))) return "备用联系人必须填写有效电话";
        var guardianPhone = NormalizePhone(row.GuardianPhone);
        if (contacts.Any(x => NormalizePhone(x.Phone) == guardianPhone || (!string.IsNullOrWhiteSpace(x.Mobile) && NormalizePhone(x.Mobile) == guardianPhone)
            || string.Equals(x.Email?.Trim(), row.GuardianEmail?.Trim(), StringComparison.OrdinalIgnoreCase))) return "备用联系人必须独立于家长联系人";
        return null;
    }
    private static bool ValidPhone(string? value) => !string.IsNullOrWhiteSpace(value) && PhonePattern.IsMatch(value) && value.Count(char.IsDigit) is >= 8 and <= 15;
    private static string NormalizePhone(string? value)
    {
        var digits = new string(value?.Where(char.IsDigit).ToArray() ?? Array.Empty<char>());
        return digits.StartsWith("61", StringComparison.Ordinal) && digits.Length == 11 ? "0" + digits[2..] : digits;
    }
    private async Task<Store?> ResolveEmploymentStoreAsync(string userGuid) => await _context.Db.Queryable<UserStore>().InnerJoin<Store>((us, store) => us.StoreGUID == store.StoreGUID).Where((us, store) => us.UserGUID == userGuid && us.IsPrimary && !us.IsDeleted && !store.IsDeleted).Select((us, store) => store).FirstAsync();
    private async Task<bool> CanAccessHrRowAsync(EmployeeMinorCompliance row) { var scope = await _storeScope.GetScopeAsync(); return scope.IsAuthenticated && scope.IsAllowed && (scope.IsAdmin || scope.StoreGuids.Contains(row.StoreGUID ?? string.Empty)); }
    private string? ValidateGuardianContact(EmployeeMinorCompliance row) => string.IsNullOrWhiteSpace(row.GuardianName) ? "家长/监护人姓名为必填" : string.IsNullOrWhiteSpace(row.GuardianPhone) || !ValidPhone(row.GuardianPhone) ? "家长电话（手机或固定电话）为必填且格式无效" : string.IsNullOrWhiteSpace(row.GuardianEmail) || !new EmailAddressAttribute().IsValid(row.GuardianEmail) ? "家长邮箱为必填且格式无效" : null;
    private async Task<EmployeeMinorComplianceDto> MapAsync(EmployeeMinorCompliance row)
    {
        var contacts = await _context.Db.Queryable<EmployeeMinorComplianceContact>()
            .Where(x => x.ComplianceId == row.Id && !x.IsDeleted).ToListAsync();
        var form = Parse<EmployeeMinorCe1FormDto>(row.FormDataJson);
        var employeeName = string.Join(" ", new[] { form?.ChildGivenName, form?.ChildFamilyName }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        return new()
        {
            Id = row.Id, UserGUID = row.UserGUID, EmployeeName = string.IsNullOrWhiteSpace(employeeName) ? row.UserGUID : employeeName,
            Version = row.Version, Revision = row.Revision, Status = row.Status, StateCode = row.StateCode, FormType = row.FormType,
            DateOfBirth = row.DateOfBirth, SchoolName = row.SchoolName, YearLevel = row.YearLevel, CompletedYear10 = row.CompletedYear10,
            EducationStatus = row.EducationStatus, RequiredToBeEnrolled = row.RequiredToBeEnrolled,
            EducationExemptionVerified = row.EducationExemptionVerified, ParticipationEndDate = row.ParticipationEndDate,
            StoreGUID = row.StoreGUID, StoreCode = row.StoreCode, StoreTimeZoneId = row.StoreTimeZoneId,
            SchoolCalendar = Parse<EmployeeMinorSchoolCalendarDto>(row.SchoolCalendarJson),
            OtherWork = Parse<EmployeeMinorOtherWorkDto>(row.OtherWorkJson), Commute = Parse<EmployeeMinorCommuteDto>(row.CommuteJson), FormData = form,
            GuardianName = row.GuardianName, GuardianPhone = row.GuardianPhone, GuardianEmail = row.GuardianEmail,
            GuardianRelationship = row.GuardianRelationship, GuardianSignedAtUtc = row.GuardianSignedAtUtc, GuardianSignedName = row.GuardianSignedName,
            ConsentScope = ServerConsentScope, GuardianTokenActive = !row.GuardianTokenUsed && row.GuardianTokenExpiresAtUtc > DateTime.UtcNow,
            DocumentSha256 = row.DocumentSha256, ReviewActor = row.ReviewActor, ReviewedAtUtc = row.ReviewedAtUtc, ReviewComment = row.ReviewComment,
            ReturnFields = Parse<List<string>>(row.ReturnFieldsJson) ?? new(), SubmittedAtUtc = row.SubmittedAtUtc,
            Contacts = contacts.Select(x => new EmployeeMinorComplianceContactDto
            {
                FullName = x.FullName, Phone = x.Phone, Mobile = x.Mobile, Email = x.Email,
                Address = x.Address, Postcode = x.Postcode, Relationship = x.Relationship
            }).ToList()
        };
    }
    private async Task<List<EmployeeMinorComplianceHistoryItemDto>> MapHistoryAsync(IEnumerable<EmployeeMinorCompliance> rows) { var result = new List<EmployeeMinorComplianceHistoryItemDto>(); foreach (var row in rows) { var audits = await _context.Db.Queryable<EmployeeMinorComplianceAudit>().Where(x => x.ComplianceId == row.Id && !x.IsDeleted).OrderBy(x => x.CreatedAt, OrderByType.Asc).ToListAsync(); result.Add(new() { Id = row.Id, Version = row.Version, Status = row.Status, CreatedAt = row.CreatedAt, SubmittedAtUtc = row.SubmittedAtUtc, ReviewedAtUtc = row.ReviewedAtUtc, ReviewComment = row.ReviewComment, DocumentSha256 = row.DocumentSha256, Audits = audits.Select(x => new EmployeeMinorComplianceAuditDto { Action = x.Action, ActorLabel = x.ActorLabel, CreatedAt = x.CreatedAt, Metadata = ParseJson(x.MetadataJson) }).ToList() }); } return result; }
    private static T? Parse<T>(string? json) where T : class => string.IsNullOrWhiteSpace(json) || json == "null" ? null : JsonSerializer.Deserialize<T>(json, JsonOptions);
    private static object? ParseJson(string? json) => string.IsNullOrWhiteSpace(json) || json == "null" ? null : JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);
    private async Task AuditAsync(int id, string action, object? metadata) => await _context.Db.Insertable(new EmployeeMinorComplianceAudit { ComplianceId = id, Action = action, ActorUserGuid = _currentUser.GetCurrentUserGuid(), ActorLabel = _currentUser.GetCurrentUsername(), MetadataJson = metadata is null ? null : JsonSerializer.Serialize(metadata, JsonOptions) }).ExecuteCommandAsync();
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
