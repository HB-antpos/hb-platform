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
using Microsoft.Extensions.Options;
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
    public const string GuardianNotVerifiedCode = "GUARDIAN_EMAIL_NOT_VERIFIED";
    // 监护人邮箱验证码：10 分钟有效、同一链接最多发 5 次、两次间隔 60 秒、错 5 次作废需重发；验证后会话 2 小时。
    internal const int GuardianCodeTtlMinutes = 10;
    internal const int GuardianCodeMaxSends = 5;
    internal const int GuardianCodeResendSeconds = 60;
    internal const int GuardianCodeMaxFailures = 5;
    internal const int GuardianSessionHours = 2;
    private static readonly Regex PhonePattern = new(@"^[0-9+() .-]{6,30}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqlSugarContext _context;
    private readonly ICurrentUserService _currentUser;
    private readonly ICurrentUserManageableStoreScopeService _storeScope;
    private readonly ILogger<EmployeeMinorComplianceService> _logger;
    private readonly IEmployeeMinorDocumentStore? _storage;
    private readonly IMinorGuardianEmailSender? _emailSender;
    private readonly MinorEmploymentOptions _options;

    public EmployeeMinorComplianceService(SqlSugarContext context, ICurrentUserService currentUser, ILogger<EmployeeMinorComplianceService> logger, ICurrentUserManageableStoreScopeService storeScope, IEmployeeMinorDocumentStore? storage = null, IMinorGuardianEmailSender? emailSender = null, IOptions<MinorEmploymentOptions>? options = null)
    { _context = context; _currentUser = currentUser; _logger = logger; _storeScope = storeScope; _storage = storage; _emailSender = emailSender; _options = options?.Value ?? new MinorEmploymentOptions(); }

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
        var formError = ValidateFormCompleteness(row);
        if (formError is not null) return ApiResponse<EmployeeMinorComplianceInviteResultDto>.Error(formError, "MINOR_COMPLIANCE_FORM_INCOMPLETE");
        var backupError = await ValidateBackupContactsAsync(row);
        if (backupError is not null) return ApiResponse<EmployeeMinorComplianceInviteResultDto>.Error(backupError, "MINOR_COMPLIANCE_BACKUP_CONTACT_REQUIRED");
        if (row.Status != "draft") return ApiResponse<EmployeeMinorComplianceInviteResultDto>.Error("请先保存新版本的草稿，再发起签字；已签版本保留原件", "INVALID_STATUS");
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        row.GuardianTokenHash = Hash(raw);
        row.GuardianTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(Math.Clamp(dto.ExpiryMinutes, 5, 60 * 24 * 7));
        row.GuardianTokenUsed = false;
        row.Status = "awaiting_guardian_signature";
        row.GuardianInviteChannel = dto.DeliverByEmail ? "email" : "share";
        row.GuardianInviteEmailSentAtUtc = null;
        ResetGuardianVerification(row);
        var inviteTransaction = await _context.Db.Ado.UseTranAsync(async () =>
        {
            var inviteAffected = await _context.Db.Updateable(row).Where(x => x.Id == row.Id && x.Version == dto.Version && x.Revision == dto.Revision && x.Status == "draft").ExecuteCommandAsync();
            if (inviteAffected != 1) throw new InvalidOperationException(VersionConflictCode);
            await AuditAsync(row.Id, "guardian_invite_created", new { expiresAtUtc = row.GuardianTokenExpiresAtUtc, channel = row.GuardianInviteChannel });
        });
        if (!inviteTransaction.IsSuccess) return ApiResponse<EmployeeMinorComplianceInviteResultDto>.Error("档案版本或状态已变化，请刷新后重试", VersionConflictCode);
        var signingUrl = BuildSigningUrl(raw);
        var result = new EmployeeMinorComplianceInviteResultDto
        {
            Id = row.Id, Version = row.Version, Revision = row.Revision, ExpiresAtUtc = row.GuardianTokenExpiresAtUtc.Value,
            DeliveryChannel = row.GuardianInviteChannel, MaskedGuardianEmail = MaskEmail(row.GuardianEmail),
        };
        if (!dto.DeliverByEmail)
        {
            // 备用方式：员工自己转发链接。监护人打开后仍须用发到监护人邮箱的验证码核验，员工拿到链接也无法代签。
            result.SigningUrl = signingUrl;
            return ApiResponse<EmployeeMinorComplianceInviteResultDto>.OK(result);
        }
        var send = _emailSender is null
            ? ApiResponse<bool>.Error("邮件服务未配置", "GUARDIAN_EMAIL_NOT_CONFIGURED")
            : await _emailSender.SendSigningLinkAsync(row.GuardianEmail!, row.GuardianName, ChildDisplayName(row), signingUrl, row.GuardianTokenExpiresAtUtc.Value);
        if (send.Success)
        {
            // 邮件已直达监护人，不再把链接回传到员工手机。
            var sentAt = DateTime.UtcNow;
            await _context.Db.Updateable<EmployeeMinorCompliance>()
                .SetColumns(x => x.GuardianInviteEmailSentAtUtc == sentAt)
                .Where(x => x.Id == row.Id && x.GuardianTokenHash == row.GuardianTokenHash)
                .ExecuteCommandAsync();
            await AuditAsync(row.Id, "guardian_invite_emailed", new { to = result.MaskedGuardianEmail });
            result.EmailSent = true;
            return ApiResponse<EmployeeMinorComplianceInviteResultDto>.OK(result, "签署链接已发送到监护人邮箱");
        }
        // 发信失败不回滚邀请：链接照样有效，回传给员工转发，验证码仍发到监护人邮箱。
        await AuditAsync(row.Id, "guardian_invite_email_failed", new { send.ErrorCode });
        result.SigningUrl = signingUrl;
        result.EmailError = send.Message;
        return ApiResponse<EmployeeMinorComplianceInviteResultDto>.OK(result, "邮件发送失败，可改为转发链接给监护人");
    }

    public async Task<ApiResponse<EmployeeMinorComplianceDto>> SignGuardianAsync(string token, EmployeeMinorComplianceGuardianSignDto dto, string? sessionKey = null, string? clientIp = null, string? userAgent = null)
    {
        var row = await _context.Db.Queryable<EmployeeMinorCompliance>()
            .Where(x => !x.IsDeleted && x.GuardianTokenHash == Hash(token) && !x.GuardianTokenUsed).FirstAsync();
        if (row is null || row.GuardianTokenExpiresAtUtc < DateTime.UtcNow) return ApiResponse<EmployeeMinorComplianceDto>.Error("签署链接无效或已过期", "GUARDIAN_TOKEN_INVALID");
        if (!IsGuardianSessionValid(row, sessionKey)) return ApiResponse<EmployeeMinorComplianceDto>.Error("请先用发到监护人邮箱的验证码完成验证", GuardianNotVerifiedCode);
        // 监护人现场修改：先合并到当前版本（内存），后面所有校验、签名姓名比对与 PDF 都基于修改后的内容。
        var currentContacts = (await ContactsAsync(row.Id)).Select(ToContactDto).ToList();
        EmployeeMinorGuardianAmendments.Result? amended = null;
        if (dto.Amendments is not null)
        {
            amended = EmployeeMinorGuardianAmendments.Apply(row, currentContacts, dto.Amendments, JsonOptions);
            var calendarError = EmployeeMinorGuardianAmendments.ValidateSchoolCalendar(Parse<EmployeeMinorSchoolCalendarDto>(row.SchoolCalendarJson));
            if (calendarError is not null) return ApiResponse<EmployeeMinorComplianceDto>.Error(calendarError, "MINOR_COMPLIANCE_SCHOOL_CALENDAR_INVALID");
            var formError = ValidateFormCompleteness(row);
            if (formError is not null) return ApiResponse<EmployeeMinorComplianceDto>.Error(formError, "MINOR_COMPLIANCE_FORM_INCOMPLETE");
        }
        var effectiveContacts = amended?.Contacts ?? currentContacts;
        var contactError = ValidateGuardianContact(row);
        if (contactError is not null) return ApiResponse<EmployeeMinorComplianceDto>.Error(contactError, InvalidGuardianContactCode);
        var backupError = ValidateBackupContacts(row, effectiveContacts);
        if (backupError is not null) return ApiResponse<EmployeeMinorComplianceDto>.Error(backupError, "MINOR_COMPLIANCE_BACKUP_CONTACT_REQUIRED");
        if (amended?.HasChanges == true)
        {
            row.Revision += 1;
            row.GuardianAmendedFieldsJson = JsonSerializer.Serialize(amended.Groups, JsonOptions);
        }
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
        var emailVerifiedAtUtc = row.GuardianEmailVerifiedAtUtc;
        row.GuardianSessionHash = null;
        row.GuardianSessionExpiresAtUtc = null;
        try
        {
            var mapped = await MapAsync(row);
            // 联系人尚未落库，PDF 用修改后的名单。
            mapped.Contacts = effectiveContacts;
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
            if (amended?.HasChanges == true)
            {
                if (amended.Contacts is not null) await ReplaceContactsAsync(row.Id, amended.Contacts);
                // 修改前后完整值留在审计，HR 审核时可逐项核对监护人改了什么。
                await AuditAsync(row.Id, "guardian_amended", new
                {
                    groups = amended.Groups,
                    changes = amended.Changes.Select(x => new { x.Group, x.Field, x.Before, x.After }),
                });
            }
            // 签署证据：邮箱核验时间、核验邮箱（打码）、来源 IP 与浏览器标识（截断），供 HR 审核与留档。
            await AuditAsync(row.Id, "guardian_signed", new
            {
                row.GuardianSignedAtUtc,
                amendedGroups = amended?.Groups,
                emailVerifiedAtUtc,
                verifiedEmail = MaskEmail(row.GuardianEmail),
                ip = Truncate(clientIp, 64),
                userAgent = Truncate(userAgent, 200),
            });
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

    public async Task<ApiResponse<EmployeeMinorComplianceDto>> GetGuardianAsync(string token, string? sessionKey = null)
    {
        var (row, error) = await LoadGuardianRowAsync<EmployeeMinorComplianceDto>(token);
        if (row is null) return error!;
        // 未通过邮箱验证前不返回孩子的任何资料，转发出去的链接只能看到打码邮箱。
        if (!IsGuardianSessionValid(row, sessionKey)) return ApiResponse<EmployeeMinorComplianceDto>.Error("请先用发到监护人邮箱的验证码完成验证", GuardianNotVerifiedCode);
        return ApiResponse<EmployeeMinorComplianceDto>.OK(await MapAsync(row));
    }

    /// <summary>监护人打开链接后的会话状态，只含打码邮箱与验证码发送节奏。</summary>
    public async Task<ApiResponse<EmployeeMinorComplianceGuardianSessionDto>> GetGuardianSessionAsync(string token, string? sessionKey = null)
    {
        var (row, error) = await LoadGuardianRowAsync<EmployeeMinorComplianceGuardianSessionDto>(token);
        if (row is null) return error!;
        return ApiResponse<EmployeeMinorComplianceGuardianSessionDto>.OK(MapSession(row, IsGuardianSessionValid(row, sessionKey)));
    }

    /// <summary>向当前版本登记的监护人邮箱发送一次性验证码；验证码只存哈希，与签署 token 绑定。</summary>
    public async Task<ApiResponse<EmployeeMinorComplianceGuardianSessionDto>> SendGuardianCodeAsync(string token)
    {
        var (row, error) = await LoadGuardianRowAsync<EmployeeMinorComplianceGuardianSessionDto>(token);
        if (row is null) return error!;
        var contactError = ValidateGuardianContact(row);
        if (contactError is not null) return ApiResponse<EmployeeMinorComplianceGuardianSessionDto>.Error(contactError, InvalidGuardianContactCode);
        var now = DateTime.UtcNow;
        if (row.GuardianOtpSendCount >= GuardianCodeMaxSends)
            return ApiResponse<EmployeeMinorComplianceGuardianSessionDto>.Error("验证码发送次数已达上限，请联系员工重新发起签署", "GUARDIAN_CODE_SEND_LIMIT");
        if (row.GuardianOtpSentAtUtc.HasValue && row.GuardianOtpSentAtUtc.Value.AddSeconds(GuardianCodeResendSeconds) > now)
            return ApiResponse<EmployeeMinorComplianceGuardianSessionDto>.Error("验证码刚刚发送，请稍后再试", "GUARDIAN_CODE_COOLDOWN", MapSession(row, false));
        if (_emailSender is null) return ApiResponse<EmployeeMinorComplianceGuardianSessionDto>.Error("邮件服务未配置，暂不能发送验证码", "GUARDIAN_EMAIL_NOT_CONFIGURED");
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var expectedSends = row.GuardianOtpSendCount;
        row.GuardianOtpHash = CodeHash(row, code);
        row.GuardianOtpExpiresAtUtc = now.AddMinutes(GuardianCodeTtlMinutes);
        row.GuardianOtpSentAtUtc = now;
        row.GuardianOtpSendCount = expectedSends + 1;
        row.GuardianOtpFailedAttempts = 0;
        // 以发送次数做乐观锁，并发重复点击只有一次生效，避免同时存在两个有效验证码。
        var affected = await _context.Db.Updateable<EmployeeMinorCompliance>()
            .SetColumns(x => new EmployeeMinorCompliance
            {
                GuardianOtpHash = row.GuardianOtpHash,
                GuardianOtpExpiresAtUtc = row.GuardianOtpExpiresAtUtc,
                GuardianOtpSentAtUtc = row.GuardianOtpSentAtUtc,
                GuardianOtpSendCount = row.GuardianOtpSendCount,
                GuardianOtpFailedAttempts = 0,
            })
            .Where(x => x.Id == row.Id && x.GuardianTokenHash == row.GuardianTokenHash && !x.GuardianTokenUsed && x.GuardianOtpSendCount == expectedSends)
            .ExecuteCommandAsync();
        if (affected != 1) return ApiResponse<EmployeeMinorComplianceGuardianSessionDto>.Error("验证码状态已变化，请刷新后重试", "GUARDIAN_CODE_CONFLICT");
        var send = await _emailSender.SendVerificationCodeAsync(row.GuardianEmail!, row.GuardianName, code, row.GuardianOtpExpiresAtUtc.Value);
        await AuditAsync(row.Id, send.Success ? "guardian_code_sent" : "guardian_code_send_failed", new { to = MaskEmail(row.GuardianEmail), attempt = row.GuardianOtpSendCount });
        if (!send.Success) return ApiResponse<EmployeeMinorComplianceGuardianSessionDto>.Error(send.Message, send.ErrorCode ?? "GUARDIAN_EMAIL_SEND_FAILED", MapSession(row, false));
        return ApiResponse<EmployeeMinorComplianceGuardianSessionDto>.OK(MapSession(row, false), $"验证码已发送到 {MaskEmail(row.GuardianEmail)}");
    }

    /// <summary>校验验证码；通过后签发一次性会话密钥（只存哈希），查看与签署都要带上。</summary>
    public async Task<ApiResponse<string>> VerifyGuardianCodeAsync(string token, string code)
    {
        var (row, error) = await LoadGuardianRowAsync<string>(token);
        if (row is null) return error!;
        var now = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(row.GuardianOtpHash) || row.GuardianOtpExpiresAtUtc is null || row.GuardianOtpExpiresAtUtc < now)
            return ApiResponse<string>.Error("验证码已过期，请重新发送", "GUARDIAN_CODE_EXPIRED");
        if (row.GuardianOtpFailedAttempts >= GuardianCodeMaxFailures)
            return ApiResponse<string>.Error("验证码错误次数过多，请重新发送", "GUARDIAN_CODE_LOCKED");
        var normalized = new string((code ?? string.Empty).Where(char.IsDigit).ToArray());
        var matches = normalized.Length == 6 && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(CodeHash(row, normalized)), Encoding.ASCII.GetBytes(row.GuardianOtpHash));
        if (!matches)
        {
            var failed = row.GuardianOtpFailedAttempts;
            await _context.Db.Updateable<EmployeeMinorCompliance>()
                .SetColumns(x => x.GuardianOtpFailedAttempts == failed + 1)
                .Where(x => x.Id == row.Id && x.GuardianOtpHash == row.GuardianOtpHash && x.GuardianOtpFailedAttempts == failed)
                .ExecuteCommandAsync();
            var remaining = Math.Max(0, GuardianCodeMaxFailures - failed - 1);
            return ApiResponse<string>.Error(remaining > 0 ? $"验证码不正确，还可尝试 {remaining} 次" : "验证码错误次数过多，请重新发送", remaining > 0 ? "GUARDIAN_CODE_INVALID" : "GUARDIAN_CODE_LOCKED");
        }
        var sessionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var sessionHash = Hash($"{row.Id}:{sessionKey}");
        var sessionExpires = Min(now.AddHours(GuardianSessionHours), row.GuardianTokenExpiresAtUtc ?? now.AddHours(GuardianSessionHours));
        // 验证码一次性：通过即清空，同一码不能再换第二个会话。
        var affected = await _context.Db.Updateable<EmployeeMinorCompliance>()
            .SetColumns(x => new EmployeeMinorCompliance
            {
                GuardianOtpHash = null,
                GuardianOtpExpiresAtUtc = null,
                GuardianEmailVerifiedAtUtc = now,
                GuardianSessionHash = sessionHash,
                GuardianSessionExpiresAtUtc = sessionExpires,
            })
            .Where(x => x.Id == row.Id && x.GuardianOtpHash == row.GuardianOtpHash && !x.GuardianTokenUsed)
            .ExecuteCommandAsync();
        if (affected != 1) return ApiResponse<string>.Error("验证码已被使用，请重新发送", "GUARDIAN_CODE_EXPIRED");
        await AuditAsync(row.Id, "guardian_email_verified", new { email = MaskEmail(row.GuardianEmail) });
        return ApiResponse<string>.OK(sessionKey, "邮箱验证成功");
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
            // 店长发起的填写请求随员工提交自动完成。
            var completedAt = DateTime.UtcNow;
            await _context.Db.Updateable<EmployeeMinorComplianceRequest>()
                .SetColumns(x => new EmployeeMinorComplianceRequest { Status = EmployeeMinorComplianceRequest.StatusCompleted, CompletedAtUtc = completedAt, CompletedComplianceId = row.Id, UpdatedAt = completedAt })
                .Where(x => x.UserGUID == userGuid && x.Status == EmployeeMinorComplianceRequest.StatusOpen)
                .ExecuteCommandAsync();
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

    // ───────────── 店长发起填写请求 ─────────────

    /// <summary>店长可管理门店里按员工资料生日判定未满 18 岁的员工，附最新档案状态与未完成请求。管理员须指定门店。</summary>
    public async Task<ApiResponse<List<EmployeeMinorManagerCandidateDto>>> GetManagerCandidatesAsync(string? storeCode)
    {
        var scope = await _storeScope.GetScopeAsync();
        if (!scope.IsAuthenticated || !scope.IsAllowed) return ApiResponse<List<EmployeeMinorManagerCandidateDto>>.Error("当前账号没有门店管理权限", "FORBIDDEN");
        if (!string.IsNullOrWhiteSpace(storeCode) && !scope.CanAccessStoreCode(storeCode)) return ApiResponse<List<EmployeeMinorManagerCandidateDto>>.Error("无权查看该门店员工", "FORBIDDEN");
        if (scope.IsAdmin && string.IsNullOrWhiteSpace(storeCode)) return ApiResponse<List<EmployeeMinorManagerCandidateDto>>.Error("请选择门店", "STORE_REQUIRED");
        var storeCodes = string.IsNullOrWhiteSpace(storeCode) ? scope.StoreCodes.ToList() : new List<string> { storeCode.Trim() };
        if (storeCodes.Count == 0) return ApiResponse<List<EmployeeMinorManagerCandidateDto>>.OK(new());
        // 生日早于此日期的已满 18 岁；以悉尼当天为准，避免 UTC 跨日把生日当天算错。
        var today = SydneyToday();
        var adultCutoff = today.AddYears(-18);
        var rows = await _context.Db.Queryable<UserStore>()
            .InnerJoin<Store>((us, st) => us.StoreGUID == st.StoreGUID)
            .InnerJoin<EmployeeProfile>((us, st, ep) => ep.UserGUID == us.UserGUID)
            .InnerJoin<User>((us, st, ep, u) => u.UserGUID == us.UserGUID)
            .Where((us, st, ep, u) => us.IsPrimary && !us.IsDeleted && !st.IsDeleted && !ep.IsDeleted && !u.IsDeleted && u.IsActive
                && storeCodes.Contains(st.StoreCode) && ep.Birthday != null && ep.Birthday > adultCutoff)
            .Select((us, st, ep, u) => new { u.UserGUID, u.FullName, u.Username, st.StoreCode, ep.Birthday })
            .ToListAsync();
        var userGuids = rows.Select(x => x.UserGUID).Distinct().ToArray();
        var compliances = userGuids.Length == 0 ? new List<EmployeeMinorCompliance>() : await _context.Db.Queryable<EmployeeMinorCompliance>()
            .Where(x => userGuids.Contains(x.UserGUID) && !x.IsDeleted).ToListAsync();
        var requests = userGuids.Length == 0 ? new List<EmployeeMinorComplianceRequest>() : await _context.Db.Queryable<EmployeeMinorComplianceRequest>()
            .Where(x => userGuids.Contains(x.UserGUID) && x.Status == EmployeeMinorComplianceRequest.StatusOpen && !x.IsDeleted).ToListAsync();
        var result = rows.Select(x =>
        {
            var latest = compliances.Where(c => c.UserGUID == x.UserGUID).OrderByDescending(c => c.Version).FirstOrDefault();
            var name = string.IsNullOrWhiteSpace(x.FullName) ? x.Username : x.FullName!;
            var open = requests.FirstOrDefault(r => r.UserGUID == x.UserGUID);
            return new EmployeeMinorManagerCandidateDto
            {
                UserGUID = x.UserGUID, EmployeeName = name, StoreCode = x.StoreCode, Birthday = x.Birthday!.Value.Date,
                Age = AgeOn(x.Birthday.Value.Date, today), ComplianceStatus = latest?.Status, ComplianceVersion = latest?.Version,
                StateCode = latest?.StateCode, OpenRequest = open is null ? null : MapRequest(open, name),
            };
        }).OrderBy(x => x.ComplianceStatus == "approved").ThenBy(x => x.EmployeeName).ToList();
        return ApiResponse<List<EmployeeMinorManagerCandidateDto>>.OK(result);
    }

    /// <summary>店长请员工填写未成年用工资料；同一员工只保留一条未完成请求，重复发起直接返回已有请求。</summary>
    public async Task<ApiResponse<EmployeeMinorComplianceRequestDto>> CreateRequestAsync(EmployeeMinorComplianceRequestCreateDto dto)
    {
        var scope = await _storeScope.GetScopeAsync();
        if (!scope.IsAuthenticated || !scope.IsAllowed) return ApiResponse<EmployeeMinorComplianceRequestDto>.Error("当前账号没有门店管理权限", "FORBIDDEN");
        var userGuid = dto.UserGUID?.Trim() ?? string.Empty;
        var store = await ResolveEmploymentStoreAsync(userGuid);
        if (store is null || !scope.CanAccessStoreGuid(store.StoreGUID)) return ApiResponse<EmployeeMinorComplianceRequestDto>.Error("该员工不在你管理的门店", "FORBIDDEN");
        var profile = await _context.Db.Queryable<EmployeeProfile>().Where(x => x.UserGUID == userGuid && !x.IsDeleted).FirstAsync();
        if (profile?.Birthday is null) return ApiResponse<EmployeeMinorComplianceRequestDto>.Error("员工资料缺少出生日期，请先补充", "DOB_REQUIRED");
        if (AgeOn(profile.Birthday.Value.Date, SydneyToday()) >= 18) return ApiResponse<EmployeeMinorComplianceRequestDto>.Error("该员工已满 18 岁，无需填写未成年用工资料", "NOT_MINOR");
        var user = await _context.Db.Queryable<User>().Where(x => x.UserGUID == userGuid && !x.IsDeleted).FirstAsync();
        var name = string.IsNullOrWhiteSpace(user?.FullName) ? user?.Username : user!.FullName;
        var existing = await _context.Db.Queryable<EmployeeMinorComplianceRequest>()
            .Where(x => x.UserGUID == userGuid && x.Status == EmployeeMinorComplianceRequest.StatusOpen && !x.IsDeleted).FirstAsync();
        if (existing is not null) return ApiResponse<EmployeeMinorComplianceRequestDto>.OK(MapRequest(existing, name), "该员工已有未完成的填写请求");
        var row = new EmployeeMinorComplianceRequest
        {
            UserGUID = userGuid, StoreGUID = store.StoreGUID, StoreCode = store.StoreCode, Status = EmployeeMinorComplianceRequest.StatusOpen,
            Note = string.IsNullOrWhiteSpace(dto.Note) ? null : dto.Note.Trim(), DueDate = dto.DueDate?.Date,
            RequestedByUserGuid = _currentUser.GetCurrentUserGuid(), RequestedByName = _currentUser.GetCurrentUsername(), CreatedBy = _currentUser.GetCurrentUsername(),
        };
        try
        {
            row.Id = await _context.Db.Insertable(row).ExecuteReturnIdentityAsync();
        }
        catch (Exception ex) when (ex.Message.Contains("UX_MinorRequest_OpenPerUser", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
        {
            // 两位店长同时发起：唯一索引兜底，返回胜出的那条。
            var winner = await _context.Db.Queryable<EmployeeMinorComplianceRequest>()
                .Where(x => x.UserGUID == userGuid && x.Status == EmployeeMinorComplianceRequest.StatusOpen && !x.IsDeleted).FirstAsync();
            if (winner is null) throw;
            return ApiResponse<EmployeeMinorComplianceRequestDto>.OK(MapRequest(winner, name), "该员工已有未完成的填写请求");
        }
        return ApiResponse<EmployeeMinorComplianceRequestDto>.OK(MapRequest(row, name), "已通知员工填写");
    }

    public async Task<ApiResponse<EmployeeMinorComplianceRequestDto>> CancelRequestAsync(int id)
    {
        var scope = await _storeScope.GetScopeAsync();
        var row = await _context.Db.Queryable<EmployeeMinorComplianceRequest>().FirstAsync(x => x.Id == id && !x.IsDeleted);
        if (row is null || !scope.IsAuthenticated || !scope.IsAllowed || !scope.CanAccessStoreGuid(row.StoreGUID ?? string.Empty))
            return ApiResponse<EmployeeMinorComplianceRequestDto>.Error("请求不存在或无权处理", "FORBIDDEN");
        if (row.Status != EmployeeMinorComplianceRequest.StatusOpen) return ApiResponse<EmployeeMinorComplianceRequestDto>.Error("该请求已完成或已撤销", "REQUEST_CLOSED");
        var now = DateTime.UtcNow;
        var actor = _currentUser.GetCurrentUsername();
        var affected = await _context.Db.Updateable<EmployeeMinorComplianceRequest>()
            .SetColumns(x => new EmployeeMinorComplianceRequest { Status = EmployeeMinorComplianceRequest.StatusCancelled, CancelledAtUtc = now, CancelledBy = actor, UpdatedAt = now })
            .Where(x => x.Id == id && x.Status == EmployeeMinorComplianceRequest.StatusOpen)
            .ExecuteCommandAsync();
        if (affected != 1) return ApiResponse<EmployeeMinorComplianceRequestDto>.Error("该请求已完成或已撤销", "REQUEST_CLOSED");
        row.Status = EmployeeMinorComplianceRequest.StatusCancelled; row.CancelledAtUtc = now; row.CancelledBy = actor;
        return ApiResponse<EmployeeMinorComplianceRequestDto>.OK(MapRequest(row, null), "已撤销");
    }

    /// <summary>员工本人未完成的填写请求，用于工作台待办与资料页提示。</summary>
    public async Task<ApiResponse<List<EmployeeMinorComplianceRequestDto>>> GetSelfRequestsAsync()
    {
        var userGuid = _currentUser.GetCurrentUserGuid();
        if (string.IsNullOrWhiteSpace(userGuid)) return ApiResponse<List<EmployeeMinorComplianceRequestDto>>.Error("未找到当前用户", "CURRENT_USER_NOT_FOUND");
        var rows = await _context.Db.Queryable<EmployeeMinorComplianceRequest>()
            .Where(x => x.UserGUID == userGuid && x.Status == EmployeeMinorComplianceRequest.StatusOpen && !x.IsDeleted)
            .OrderBy(x => x.CreatedAt, OrderByType.Desc).ToListAsync();
        return ApiResponse<List<EmployeeMinorComplianceRequestDto>>.OK(rows.Select(x => MapRequest(x, null)).ToList());
    }

    // ───────────── 监护人链接与验证码辅助 ─────────────

    private async Task<(EmployeeMinorCompliance? Row, ApiResponse<T>? Error)> LoadGuardianRowAsync<T>(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return (null, ApiResponse<T>.Error("签署链接无效或已过期", "GUARDIAN_TOKEN_INVALID"));
        var row = await _context.Db.Queryable<EmployeeMinorCompliance>().Where(x => !x.IsDeleted && x.GuardianTokenHash == Hash(token) && !x.GuardianTokenUsed).FirstAsync();
        if (row is null || row.GuardianTokenExpiresAtUtc < DateTime.UtcNow) return (null, ApiResponse<T>.Error("签署链接无效或已过期", "GUARDIAN_TOKEN_INVALID"));
        var latest = await LatestAsync(row.UserGUID);
        if (latest?.Id != row.Id) return (null, ApiResponse<T>.Error("签署链接已因新版本失效", VersionConflictCode));
        return (row, null);
    }

    private static bool IsGuardianSessionValid(EmployeeMinorCompliance row, string? sessionKey)
    {
        if (string.IsNullOrWhiteSpace(sessionKey) || string.IsNullOrWhiteSpace(row.GuardianSessionHash) || row.GuardianSessionExpiresAtUtc is null || row.GuardianSessionExpiresAtUtc < DateTime.UtcNow) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash($"{row.Id}:{sessionKey}")), Encoding.ASCII.GetBytes(row.GuardianSessionHash));
    }

    private static void ResetGuardianVerification(EmployeeMinorCompliance row)
    {
        row.GuardianOtpHash = null; row.GuardianOtpExpiresAtUtc = null; row.GuardianOtpSentAtUtc = null;
        row.GuardianOtpSendCount = 0; row.GuardianOtpFailedAttempts = 0; row.GuardianEmailVerifiedAtUtc = null;
        row.GuardianSessionHash = null; row.GuardianSessionExpiresAtUtc = null;
        row.GuardianAmendedFieldsJson = null;
    }

    private static EmployeeMinorComplianceGuardianSessionDto MapSession(EmployeeMinorCompliance row, bool verified) => new()
    {
        MaskedGuardianEmail = MaskEmail(row.GuardianEmail) ?? string.Empty, EmailVerified = verified,
        LinkExpiresAtUtc = row.GuardianTokenExpiresAtUtc ?? DateTime.UtcNow, CodeSentAtUtc = row.GuardianOtpSentAtUtc,
        CodeExpiresAtUtc = row.GuardianOtpHash is null ? null : row.GuardianOtpExpiresAtUtc,
        ResendAvailableAtUtc = row.GuardianOtpSentAtUtc?.AddSeconds(GuardianCodeResendSeconds),
        RemainingSends = Math.Max(0, GuardianCodeMaxSends - row.GuardianOtpSendCount),
    };

    private string BuildSigningUrl(string rawToken)
    {
        // token 放在 # 之后，浏览器不会把它发给服务器或写进访问日志 / Referer。
        var baseUrl = (_options.SigningBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        return $"{baseUrl}/minor-employment/sign#token={rawToken}";
    }

    private static string CodeHash(EmployeeMinorCompliance row, string code) => Hash($"{row.Id}:{row.GuardianTokenHash}:{code}");

    internal static string? MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var at = email.IndexOf('@');
        if (at <= 0) return "***";
        var local = email[..at];
        return $"{local[0]}***{email[at..]}";
    }

    private static string ChildDisplayName(EmployeeMinorCompliance row)
    {
        var form = Parse<EmployeeMinorCe1FormDto>(row.FormDataJson);
        var name = string.Join(" ", new[] { form?.ChildGivenName, form?.ChildFamilyName }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return string.IsNullOrWhiteSpace(name) ? "your child" : name;
    }

    private static EmployeeMinorComplianceRequestDto MapRequest(EmployeeMinorComplianceRequest x, string? employeeName) => new()
    {
        Id = x.Id, UserGUID = x.UserGUID, EmployeeName = employeeName, StoreCode = x.StoreCode, Status = x.Status, Note = x.Note,
        DueDate = x.DueDate, RequestedByName = x.RequestedByName, CreatedAt = x.CreatedAt, CompletedAtUtc = x.CompletedAtUtc,
    };

    private static DateTime SydneyToday()
    {
        try { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney")).Date; }
        catch (TimeZoneNotFoundException) { return DateTime.UtcNow.Date; }
    }

    private static int AgeOn(DateTime birthday, DateTime day)
    {
        var age = day.Year - birthday.Year;
        if (birthday.Date > day.AddYears(-age)) age--;
        return age;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static string? Truncate(string? value, int max) => string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max];

    private async Task<EmployeeMinorCompliance?> LatestAsync(string userGuid) => await _context.Db.Queryable<EmployeeMinorCompliance>().Where(x => x.UserGUID == userGuid && !x.IsDeleted).OrderBy(x => x.Version, OrderByType.Desc).FirstAsync();
    private async Task<EmployeeMinorCompliance?> GetVersionAsync(string userGuid, int version) => await _context.Db.Queryable<EmployeeMinorCompliance>().Where(x => x.UserGUID == userGuid && x.Version == version && !x.IsDeleted).FirstAsync();
    private EmployeeMinorCompliance NewVersion(string userGuid, EmployeeMinorCompliance? prior, EmployeeMinorComplianceUpsertDto dto) { var row = new EmployeeMinorCompliance { UserGUID = userGuid, Version = (prior?.Version ?? 0) + 1 }; Apply(row, dto); return row; }
    private static void ApplyStoreFacts(EmployeeMinorCompliance row, Store store)
    {
        row.StoreGUID = store.StoreGUID;
        row.StoreCode = store.StoreCode;
        row.StoreTimeZoneId = InstallmentOrderStoreTimeZoneResolver.Resolve(store);
        // 先认配置时区再按邮编，地址只写区名的门店（如 Bankstown）也能定出州别和表单类型
        row.StateCode = PublicHolidaySyncHelper.ResolveStoreJurisdiction(store.TimeZoneId, store.Address)
            ?? (store.Address?.Contains("QLD", StringComparison.OrdinalIgnoreCase) == true ? "QLD" : store.Address?.Contains("NSW", StringComparison.OrdinalIgnoreCase) == true ? "NSW" : "UNKNOWN");
        row.FormType = row.StateCode == "QLD" ? "QLD_CE1" : row.StateCode == "NSW" ? "NSW_COMPANY_CONSENT" : "UNSUPPORTED_STATE";
    }
    private static void Apply(EmployeeMinorCompliance row, EmployeeMinorComplianceUpsertDto dto) { row.StateCode = (dto.StateCode ?? "QLD").Trim().ToUpperInvariant(); row.FormType = (dto.FormType ?? "QLD_CE1").Trim(); row.DateOfBirth = dto.DateOfBirth; row.SchoolName = dto.SchoolName?.Trim(); row.YearLevel = dto.YearLevel?.Trim(); row.CompletedYear10 = dto.CompletedYear10; row.EducationStatus = dto.EducationStatus?.Trim(); row.RequiredToBeEnrolled = dto.RequiredToBeEnrolled; row.EducationExemptionVerified = dto.EducationExemptionVerified; row.ParticipationEndDate = dto.ParticipationEndDate; row.SchoolCalendarJson = JsonSerializer.Serialize(dto.SchoolCalendar, JsonOptions); row.OtherWorkJson = JsonSerializer.Serialize(dto.OtherWork, JsonOptions); row.CommuteJson = JsonSerializer.Serialize(dto.Commute, JsonOptions); row.FormDataJson = JsonSerializer.Serialize(dto.FormData, JsonOptions); row.GuardianName = dto.GuardianName?.Trim() ?? string.Empty; row.GuardianPhone = string.IsNullOrWhiteSpace(dto.GuardianPhone) ? null : dto.GuardianPhone.Trim(); row.GuardianEmail = string.IsNullOrWhiteSpace(dto.GuardianEmail) ? null : dto.GuardianEmail.Trim(); row.GuardianRelationship = dto.GuardianRelationship?.Trim(); }
    private async Task ReplaceContactsAsync(int id, IEnumerable<EmployeeMinorComplianceContactDto> contacts) { await _context.Db.Deleteable<EmployeeMinorComplianceContact>().Where(x => x.ComplianceId == id).ExecuteCommandAsync(); var rows = contacts.Select((x, i) => new EmployeeMinorComplianceContact { ComplianceId = id, ContactType = i == 0 ? "backup" : "additional", FullName = x.FullName.Trim(), Phone = x.Phone.Trim(), Mobile = x.Mobile?.Trim(), Email = x.Email?.Trim(), Address = x.Address?.Trim(), Postcode = x.Postcode?.Trim(), Relationship = x.Relationship?.Trim() }).ToList(); if (rows.Count > 0) await _context.Db.Insertable(rows).ExecuteCommandAsync(); }
    private async Task<List<EmployeeMinorComplianceContact>> ContactsAsync(int id) => await _context.Db.Queryable<EmployeeMinorComplianceContact>().Where(x => x.ComplianceId == id && !x.IsDeleted).ToListAsync();
    private async Task<string?> ValidateBackupContactsAsync(EmployeeMinorCompliance row) =>
        ValidateBackupContacts(row, (await ContactsAsync(row.Id)).Select(ToContactDto).ToList());
    private static EmployeeMinorComplianceContactDto ToContactDto(EmployeeMinorComplianceContact x) => new()
    { FullName = x.FullName, Phone = x.Phone, Mobile = x.Mobile, Email = x.Email, Address = x.Address, Postcode = x.Postcode, Relationship = x.Relationship };
    /// <summary>孩子姓名、雇主、需在校时的学校名称必填（邀请签署与监护人修改后共用）。</summary>
    private static string? ValidateFormCompleteness(EmployeeMinorCompliance row)
    {
        var form = Parse<EmployeeMinorCe1FormDto>(row.FormDataJson);
        var school = Parse<EmployeeMinorSchoolCalendarDto>(row.SchoolCalendarJson);
        if (form is null || string.IsNullOrWhiteSpace(form.ChildGivenName) || string.IsNullOrWhiteSpace(form.ChildFamilyName) || string.IsNullOrWhiteSpace(form.EmployerCompanyName)
            || (row.RequiredToBeEnrolled != false && (school is null || string.IsNullOrWhiteSpace(school.SchoolProvider))))
            return "请先完整填写孩子、雇主和学校资料后再邀请签署";
        return null;
    }
    private static string? ValidateBackupContacts(EmployeeMinorCompliance row, IReadOnlyList<EmployeeMinorComplianceContactDto> contacts)
    {
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
            GuardianInviteChannel = row.GuardianInviteChannel, GuardianInviteEmailSentAtUtc = row.GuardianInviteEmailSentAtUtc,
            GuardianEmailVerifiedAtUtc = row.GuardianEmailVerifiedAtUtc,
            GuardianAmendedFields = Parse<List<string>>(row.GuardianAmendedFieldsJson) ?? new(),
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
