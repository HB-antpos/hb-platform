using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services.Attendance;
using BlazorApp.Api.Services.Sms;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services;

/// <summary>
/// 店长 / HR 发起的家长签字链接（手机「人员管理 → 员工详情」与 Web「员工个人信息维护 → 未成年用工合规」共用）。
/// 员工本人不再填写或邀请；门店只录入监护人联系方式，孩子、学校等资料由家长在签署页补全后签字。
/// </summary>
public sealed partial class EmployeeMinorComplianceService
{
    public const string StaffInviteOnlyCode = "MINOR_GUARDIAN_INVITE_BY_STAFF_ONLY";
    internal const string StageNotSent = "not_sent";
    internal const string StageAwaitingGuardian = "awaiting_guardian";
    internal const string StageLinkExpired = "link_expired";
    internal const string StagePendingReview = "pending_review";
    internal const string StageReturned = "returned";
    internal const string StageApproved = "approved";
    private const string DeliveryAuditAction = "guardian_link_delivery";

    private bool SmsAvailable => _smsSender is { IsAvailable: true };

    /// <summary>可管理门店里未满 18 岁的在职员工及其合规概况；管理员不传门店时看全部门店。</summary>
    public async Task<ApiResponse<EmployeeMinorStaffComplianceListDto>> GetStaffComplianceListAsync(string? storeCode, string? stateCode, string? keyword, bool needsActionOnly)
    {
        var scope = await _storeScope.GetScopeAsync();
        if (!scope.IsAuthenticated || !scope.IsAllowed) return ApiResponse<EmployeeMinorStaffComplianceListDto>.Error("当前账号没有门店管理权限", "FORBIDDEN");
        if (!string.IsNullOrWhiteSpace(storeCode) && !scope.CanAccessStoreCode(storeCode)) return ApiResponse<EmployeeMinorStaffComplianceListDto>.Error("无权查看该门店员工", "FORBIDDEN");
        var items = await LoadStaffItemsAsync(scope, storeCode?.Trim(), null);
        // 红点按门店范围算，不受关键词与状态筛选影响。
        var needsActionCount = items.Count(x => x.NeedsAction);
        if (!string.IsNullOrWhiteSpace(stateCode)) items = items.Where(x => string.Equals(x.StateCode, stateCode.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var words = keyword.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(8).ToArray();
            items = items.Where(x => words.All(w => x.EmployeeName.Contains(w, StringComparison.OrdinalIgnoreCase) || (x.Username?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false))).ToList();
        }
        if (needsActionOnly) items = items.Where(x => x.NeedsAction).ToList();
        return ApiResponse<EmployeeMinorStaffComplianceListDto>.OK(new() { Items = items, NeedsActionCount = needsActionCount, SmsAvailable = SmsAvailable });
    }

    /// <summary>单个员工的合规概况；不是未成年（或没有出生日期）时 Item 为空，界面不显示这一节。</summary>
    public async Task<ApiResponse<EmployeeMinorStaffComplianceDetailDto>> GetStaffComplianceAsync(string userGuid)
    {
        var scope = await _storeScope.GetScopeAsync();
        if (!scope.IsAuthenticated || !scope.IsAllowed) return ApiResponse<EmployeeMinorStaffComplianceDetailDto>.Error("当前账号没有门店管理权限", "FORBIDDEN");
        var store = await ResolveEmploymentStoreAsync(userGuid?.Trim() ?? string.Empty);
        if (store is null) return ApiResponse<EmployeeMinorStaffComplianceDetailDto>.OK(new() { SmsAvailable = SmsAvailable });
        if (!await CanAccessStaffAsync(scope, userGuid!.Trim())) return ApiResponse<EmployeeMinorStaffComplianceDetailDto>.Error("该员工不在你管理的门店", "FORBIDDEN");
        var items = await LoadStaffItemsAsync(scope, store.StoreCode, userGuid!.Trim());
        return ApiResponse<EmployeeMinorStaffComplianceDetailDto>.OK(new() { Item = items.FirstOrDefault(), SmsAvailable = SmsAvailable });
    }

    /// <summary>
    /// 发送（或重新发送）家长签字链接。邮件、短信各自独立：一路失败不回滚另一路，也不回滚链接；
    /// 有任何一路失败就回传链接供复制转发。重新发送会作废旧链接。
    /// </summary>
    public async Task<ApiResponse<EmployeeMinorGuardianLinkSendResultDto>> SendGuardianLinkAsync(string userGuid, EmployeeMinorGuardianLinkSendDto dto)
    {
        userGuid = userGuid?.Trim() ?? string.Empty;
        var scope = await _storeScope.GetScopeAsync();
        if (!scope.IsAuthenticated || !scope.IsAllowed) return Fail("当前账号没有门店管理权限", "FORBIDDEN");
        var store = await ResolveEmploymentStoreAsync(userGuid);
        if (store is null) return Fail("未找到员工当前主门店，无法确定工作州", "EMPLOYEE_STORE_REQUIRED");
        if (!await CanAccessStaffAsync(scope, userGuid)) return Fail("该员工不在你管理的门店", "FORBIDDEN");
        var profile = await _context.Db.Queryable<EmployeeProfile>().Where(x => x.UserGUID == userGuid && !x.IsDeleted).FirstAsync();
        if (profile?.Birthday is null) return Fail("员工资料缺少出生日期，请先补充", "DOB_REQUIRED");
        if (AgeOn(profile.Birthday.Value.Date, SydneyToday()) >= 18) return Fail("该员工已满 18 岁，无需家长签字", "NOT_MINOR");
        var user = await _context.Db.Queryable<User>().Where(x => x.UserGUID == userGuid && !x.IsDeleted).FirstAsync();
        if (user is null) return Fail("员工不存在", "NOT_FOUND");

        // ── 发送方式与监护人联系方式 ──
        if (!dto.SendEmail && !dto.SendSms) return Fail("请至少开启一种发送方式（邮件或短信）", "MINOR_GUARDIAN_CHANNEL_REQUIRED");
        var guardianName = dto.GuardianName?.Trim() ?? string.Empty;
        var email = string.IsNullOrWhiteSpace(dto.GuardianEmail) ? null : dto.GuardianEmail.Trim();
        var phone = string.IsNullOrWhiteSpace(dto.GuardianPhone) ? null : dto.GuardianPhone.Trim();
        if (guardianName.Length == 0) return Fail("请填写监护人姓名", InvalidGuardianContactCode);
        if (phone is null || !ValidPhone(phone)) return Fail("请填写有效的监护人电话", InvalidGuardianContactCode);
        if (dto.SendEmail && email is null) return Fail("开启邮件发送时必须填写监护人邮箱", InvalidGuardianContactCode);
        if (email is not null && !new EmailAddressAttribute().IsValid(email)) return Fail("监护人邮箱格式无效", InvalidGuardianContactCode);
        if (dto.SendSms)
        {
            // 先查短信能不能用，再动数据：否则会留下一个验证码发不出去的链接。
            if (!SmsAvailable) return Fail("短信服务未开启，请关闭短信开关改用邮件", GuardianSmsNotConfiguredCode);
            if (!AustralianPhoneNumber.TryNormalize(phone, out _)) return Fail("监护人电话不是澳洲手机号（04 开头），收不到短信", GuardianSmsPhoneInvalidCode);
        }

        // ── 版本与状态 ──
        var latest = await LatestAsync(userGuid);
        if (latest is null ? dto.ExpectedVersion.HasValue : latest.Version != dto.ExpectedVersion || latest.Revision != dto.ExpectedRevision)
            return Fail("资料已被其他人更新，请刷新后重试", VersionConflictCode);
        var stage = latest is null ? StageNotSent : ResolveStage(latest, DateTime.UtcNow);
        if (stage == StagePendingReview) return Fail("家长已签字，正在等待 HR 审核，不能重新发送", "INVALID_STATUS");
        if (stage == StageApproved) return Fail("已审核通过，无需再发送", "INVALID_STATUS");

        // draft / awaiting 原地更新；没有记录或已退回则新建一个版本（退回版本保留原件与审核意见）。
        var inPlace = latest is not null && latest.Status is "draft" or "awaiting_guardian_signature";
        var expectedRevision = latest?.Revision ?? 0;
        var expectedStatus = latest?.Status;
        EmployeeMinorCompliance row;
        List<EmployeeMinorComplianceContactDto>? copiedContacts = null;
        if (inPlace) row = latest!;
        else
        {
            row = latest is null ? NewStaffDraft(userGuid, user, profile.Phone, profile.Address, store) : CloneAsNewVersion(latest);
            if (latest is not null) copiedContacts = (await ContactsAsync(latest.Id)).Select(ToContactDto).ToList();
        }
        row.GuardianName = guardianName;
        row.GuardianRelationship = string.IsNullOrWhiteSpace(dto.GuardianRelationship) ? null : dto.GuardianRelationship.Trim();
        row.GuardianEmail = email;
        row.GuardianPhone = phone;
        row.DateOfBirth = profile.Birthday.Value.Date;
        ApplyStoreFacts(row, store);
        if (row.StateCode is not ("QLD" or "NSW")) return Fail("无法确定员工所在门店属于 NSW 还是 QLD，请先在分店管理里补全门店地址或时区", "MINOR_COMPLIANCE_STATE_UNCONFIRMED");

        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        row.GuardianTokenHash = Hash(raw);
        row.GuardianTokenExpiresAtUtc = DateTime.UtcNow.AddDays(Math.Clamp(dto.ExpiryDays, 1, 7));
        row.GuardianTokenUsed = false;
        row.Status = "awaiting_guardian_signature";
        // 验证码去向：开了短信就走短信；短信没发出去时下面会改回邮件。
        row.GuardianInviteChannel = dto.SendSms ? GuardianChannelSms : GuardianChannelEmail;
        row.GuardianInviteEmailSentAtUtc = null;
        ResetGuardianVerification(row);
        row.Revision = inPlace ? expectedRevision + 1 : 1;

        var transaction = await _context.Db.Ado.UseTranAsync(async () =>
        {
            if (inPlace)
            {
                // 乐观锁：两个人同时发送只有一个成功，旧链接在这一步作废（token 哈希被替换）。
                var affected = await _context.Db.Updateable(row)
                    .Where(x => x.Id == row.Id && x.Version == row.Version && x.Revision == expectedRevision && x.Status == expectedStatus)
                    .ExecuteCommandAsync();
                if (affected != 1) throw new InvalidOperationException(VersionConflictCode);
            }
            else
            {
                if (latest is not null)
                {
                    var revoked = await _context.Db.Updateable<EmployeeMinorCompliance>()
                        .SetColumns(x => x.GuardianTokenUsed == true)
                        .SetColumns(x => x.GuardianTokenHash == null)
                        .Where(x => x.Id == latest.Id && x.Version == latest.Version && x.Revision == expectedRevision && x.Status == expectedStatus)
                        .ExecuteCommandAsync();
                    if (revoked != 1) throw new InvalidOperationException(VersionConflictCode);
                }
                row.Id = await _context.Db.Insertable(row).ExecuteReturnIdentityAsync();
                if (copiedContacts is { Count: > 0 }) await ReplaceContactsAsync(row.Id, copiedContacts);
            }
            await AuditAsync(row.Id, "guardian_link_sent_by_staff", new
            {
                expiresAtUtc = row.GuardianTokenExpiresAtUtc, sendEmail = dto.SendEmail, sendSms = dto.SendSms,
                email = MaskEmail(email), phone = MaskPhone(phone),
            });
            // 员工不再自己填写：店长以前发起的「请员工填写」请求随之关闭。
            var completedAt = DateTime.UtcNow;
            await _context.Db.Updateable<EmployeeMinorComplianceRequest>()
                .SetColumns(x => new EmployeeMinorComplianceRequest { Status = EmployeeMinorComplianceRequest.StatusCompleted, CompletedAtUtc = completedAt, CompletedComplianceId = row.Id, UpdatedAt = completedAt })
                .Where(x => x.UserGUID == userGuid && x.Status == EmployeeMinorComplianceRequest.StatusOpen)
                .ExecuteCommandAsync();
        });
        if (!transaction.IsSuccess) return Fail("资料已被其他人更新，请刷新后重试", VersionConflictCode);

        // ── 投递（事务外）：两路各自记录，失败不回滚链接 ──
        var signingUrl = BuildSigningUrl(raw);
        var result = new EmployeeMinorGuardianLinkSendResultDto
        {
            ExpiresAtUtc = row.GuardianTokenExpiresAtUtc!.Value, EmailRequested = dto.SendEmail, SmsRequested = dto.SendSms,
        };
        if (dto.SendSms)
        {
            var body = SmsMessages.GuardianSigningLink(ChildDisplayName(row), signingUrl, FormatSydneyWithZone(row.GuardianTokenExpiresAtUtc.Value));
            var sms = await _smsSender!.SendAsync(new SmsRequest(SmsPurposes.GuardianSigningLink, phone, body, row.Id.ToString()));
            result.SmsSent = sms.Success;
            if (!sms.Success) result.SmsError = sms.Message;
        }
        // 短信没发出去但开了邮件：验证码改走邮件，否则家长打开邮件里的链接也收不到验证码。
        var codeBySms = dto.SendSms && result.SmsSent;
        if (dto.SendEmail)
        {
            var mail = _emailSender is null
                ? ApiResponse<bool>.Error("邮件服务未配置", "GUARDIAN_EMAIL_NOT_CONFIGURED")
                : await _emailSender.SendSigningLinkAsync(email!, guardianName, ChildDisplayName(row), signingUrl, row.GuardianTokenExpiresAtUtc.Value, codeBySms);
            result.EmailSent = mail.Success;
            if (!mail.Success) result.EmailError = mail.Message;
        }
        // 只开短信却没发出去时仍留在短信：家长拿到转发的链接后，验证码照样发到手机。
        var codeChannel = dto.SendSms && (result.SmsSent || !dto.SendEmail) ? GuardianChannelSms : GuardianChannelEmail;
        var anySent = result.EmailSent || result.SmsSent;
        var sentAt = anySent ? DateTime.UtcNow : (DateTime?)null;
        await _context.Db.Updateable<EmployeeMinorCompliance>()
            .SetColumns(x => new EmployeeMinorCompliance { GuardianInviteChannel = codeChannel, GuardianInviteEmailSentAtUtc = sentAt })
            .Where(x => x.Id == row.Id && x.GuardianTokenHash == row.GuardianTokenHash)
            .ExecuteCommandAsync();
        row.GuardianInviteChannel = codeChannel;
        row.GuardianInviteEmailSentAtUtc = sentAt;
        await AuditAsync(row.Id, DeliveryAuditAction, new
        {
            emailRequested = dto.SendEmail, emailSent = result.EmailSent, emailError = result.EmailError is null ? null : "failed",
            smsRequested = dto.SendSms, smsSent = result.SmsSent, smsError = result.SmsError is null ? null : "failed",
            codeChannel,
        });
        result.CodeChannel = codeChannel;
        if ((dto.SendEmail && !result.EmailSent) || (dto.SendSms && !result.SmsSent)) result.SigningUrl = signingUrl;
        result.Item = (await LoadStaffItemsAsync(scope, store.StoreCode, userGuid)).FirstOrDefault() ?? new();
        var message = !anySent ? "邮件和短信都没发出去，可以复制链接手动转给家长"
            : result.SigningUrl is not null ? "部分发送失败，可以复制链接手动转给家长"
            : dto.SendEmail && dto.SendSms ? "已发送到家长邮箱和手机" : dto.SendSms ? "已发送到家长手机" : "已发送到家长邮箱";
        return ApiResponse<EmployeeMinorGuardianLinkSendResultDto>.OK(result, message);

        static ApiResponse<EmployeeMinorGuardianLinkSendResultDto> Fail(string message, string code) => ApiResponse<EmployeeMinorGuardianLinkSendResultDto>.Error(message, code);
    }

    /// <summary>作废尚未签字的链接，档案回到「未发送」。</summary>
    public async Task<ApiResponse<EmployeeMinorStaffComplianceDto>> RevokeGuardianLinkAsync(string userGuid, EmployeeMinorGuardianLinkRevokeDto dto)
    {
        userGuid = userGuid?.Trim() ?? string.Empty;
        var scope = await _storeScope.GetScopeAsync();
        if (!scope.IsAuthenticated || !scope.IsAllowed) return ApiResponse<EmployeeMinorStaffComplianceDto>.Error("当前账号没有门店管理权限", "FORBIDDEN");
        var store = await ResolveEmploymentStoreAsync(userGuid);
        if (store is null || !await CanAccessStaffAsync(scope, userGuid)) return ApiResponse<EmployeeMinorStaffComplianceDto>.Error("该员工不在你管理的门店", "FORBIDDEN");
        var latest = await LatestAsync(userGuid);
        if (latest is null || latest.Version != dto.ExpectedVersion || latest.Revision != dto.ExpectedRevision)
            return ApiResponse<EmployeeMinorStaffComplianceDto>.Error("资料已被其他人更新，请刷新后重试", VersionConflictCode);
        if (latest.Status != "awaiting_guardian_signature") return ApiResponse<EmployeeMinorStaffComplianceDto>.Error("当前没有待签字的链接", "INVALID_STATUS");
        var affected = await _context.Db.Updateable<EmployeeMinorCompliance>()
            .SetColumns(x => new EmployeeMinorCompliance
            {
                Status = "draft", GuardianTokenHash = null, GuardianTokenUsed = true, Revision = latest.Revision + 1,
                GuardianOtpHash = null, GuardianOtpExpiresAtUtc = null, GuardianSessionHash = null, GuardianSessionExpiresAtUtc = null,
            })
            .Where(x => x.Id == latest.Id && x.Version == latest.Version && x.Revision == latest.Revision && x.Status == "awaiting_guardian_signature")
            .ExecuteCommandAsync();
        if (affected != 1) return ApiResponse<EmployeeMinorStaffComplianceDto>.Error("资料已被其他人更新，请刷新后重试", VersionConflictCode);
        await AuditAsync(latest.Id, "guardian_link_revoked", null);
        var item = (await LoadStaffItemsAsync(scope, store.StoreCode, userGuid)).FirstOrDefault() ?? new();
        return ApiResponse<EmployeeMinorStaffComplianceDto>.OK(item, "链接已作废");
    }

    /// <summary>界面阶段：只看最新版本的状态与链接是否还有效。</summary>
    internal static string ResolveStage(EmployeeMinorCompliance row, DateTime utcNow) => row.Status switch
    {
        "awaiting_guardian_signature" => !row.GuardianTokenUsed && row.GuardianTokenExpiresAtUtc > utcNow ? StageAwaitingGuardian : StageLinkExpired,
        "signed_pending_employee_submit" or "pending_hr_review" => StagePendingReview,
        "returned" => StageReturned,
        "approved" => StageApproved,
        _ => StageNotSent,
    };

    /// <summary>按员工主门店与出生日期列出未满 18 岁的在职员工；onlyUserGuid 不为空时只取这一位。</summary>
    private async Task<List<EmployeeMinorStaffComplianceDto>> LoadStaffItemsAsync(CurrentUserManageableStoreScope scope, string? storeCode, string? onlyUserGuid)
    {
        // 生日晚于此日期的才未满 18 岁；以悉尼当天为准，避免 UTC 跨日把生日当天算错。
        var today = SydneyToday();
        var adultCutoff = today.AddYears(-18);
        var allowedCodes = scope.IsAdmin ? null : scope.StoreCodes.ToList();
        if (allowedCodes is { Count: 0 }) return new();
        var links = await _context.Db.Queryable<UserStore>()
            .InnerJoin<Store>((us, st) => us.StoreGUID == st.StoreGUID)
            .InnerJoin<EmployeeProfile>((us, st, ep) => ep.UserGUID == us.UserGUID)
            .InnerJoin<User>((us, st, ep, u) => u.UserGUID == us.UserGUID)
            .Where((us, st, ep, u) => !us.IsDeleted && !st.IsDeleted && !ep.IsDeleted && !u.IsDeleted && u.IsActive
                && ep.Birthday != null && ep.Birthday > adultCutoff)
            .WhereIF(!string.IsNullOrWhiteSpace(onlyUserGuid), (us, st, ep, u) => us.UserGUID == onlyUserGuid)
            .Select((us, st, ep, u) => new { u.UserGUID, u.FullName, u.Username, st.StoreCode, st.StoreName, st.StoreGUID, st.TimeZoneId, st.Address, ep.Birthday, us.IsPrimary, us.AssignedAt })
            .ToListAsync();
        // 普通店员的分店关联是 IsPrimary=false（IsPrimary 表示「可管理」）：按员工分组，
        // 工作门店取主分店、否则最早关联的分店；任一关联分店在范围内就能看到，按门店筛选时看是否关联了该店。
        var rows = links.GroupBy(x => x.UserGUID)
            .Where(g => allowedCodes is null || g.Any(x => allowedCodes.Contains(x.StoreCode, StringComparer.OrdinalIgnoreCase)))
            .Where(g => string.IsNullOrWhiteSpace(storeCode) || g.Any(x => string.Equals(x.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase)))
            .Select(g => g.OrderByDescending(x => x.IsPrimary).ThenBy(x => x.AssignedAt).First())
            .ToList();
        var userGuids = rows.Select(x => x.UserGUID).ToArray();
        if (userGuids.Length == 0) return new();
        var compliances = await _context.Db.Queryable<EmployeeMinorCompliance>().Where(x => userGuids.Contains(x.UserGUID) && !x.IsDeleted).ToListAsync();
        var latestByUser = compliances.GroupBy(x => x.UserGUID).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Version).First());
        var latestIds = latestByUser.Values.Select(x => x.Id).ToArray();
        var deliveries = latestIds.Length == 0 ? new List<EmployeeMinorComplianceAudit>() : await _context.Db.Queryable<EmployeeMinorComplianceAudit>()
            .Where(x => latestIds.Contains(x.ComplianceId) && x.Action == DeliveryAuditAction && !x.IsDeleted).ToListAsync();
        var lastDeliveryById = deliveries.GroupBy(x => x.ComplianceId).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).First());
        var now = DateTime.UtcNow;
        var result = rows.Select(x =>
        {
            latestByUser.TryGetValue(x.UserGUID, out var latest);
            var state = latest?.StateCode is "QLD" or "NSW" ? latest.StateCode
                : PublicHolidaySyncHelper.ResolveStoreJurisdiction(x.TimeZoneId, x.Address)
                  ?? (x.Address?.Contains("QLD", StringComparison.OrdinalIgnoreCase) == true ? "QLD" : x.Address?.Contains("NSW", StringComparison.OrdinalIgnoreCase) == true ? "NSW" : "UNKNOWN");
            var stage = latest is null ? StageNotSent : ResolveStage(latest, now);
            var item = new EmployeeMinorStaffComplianceDto
            {
                UserGUID = x.UserGUID, EmployeeName = string.IsNullOrWhiteSpace(x.FullName) ? x.Username : x.FullName!, Username = x.Username,
                StoreCode = x.StoreCode, StoreName = x.StoreName, StateCode = state,
                FormType = state == "QLD" ? "QLD_CE1" : state == "NSW" ? "NSW_COMPANY_CONSENT" : "UNSUPPORTED_STATE",
                Birthday = x.Birthday!.Value.Date, Age = AgeOn(x.Birthday.Value.Date, today),
                ComplianceId = latest?.Id, Version = latest?.Version, Revision = latest?.Revision, ComplianceStatus = latest?.Status,
                Stage = stage,
                NeedsAction = stage is StageNotSent or StageLinkExpired or StageReturned,
                CanSend = stage is StageNotSent or StageAwaitingGuardian or StageLinkExpired or StageReturned,
                CanRevoke = stage == StageAwaitingGuardian,
                GuardianName = string.IsNullOrWhiteSpace(latest?.GuardianName) ? null : latest!.GuardianName,
                GuardianRelationship = latest?.GuardianRelationship, GuardianEmail = latest?.GuardianEmail, GuardianPhone = latest?.GuardianPhone,
                MaskedGuardianEmail = MaskEmail(latest?.GuardianEmail), MaskedGuardianPhone = MaskAnyPhone(latest?.GuardianPhone),
                LinkExpiresAtUtc = stage is StageAwaitingGuardian or StageLinkExpired ? latest!.GuardianTokenExpiresAtUtc : null,
                GuardianSignedAtUtc = latest?.GuardianSignedAtUtc,
                ReviewComment = stage == StageReturned ? latest!.ReviewComment : null,
                ReturnFields = stage == StageReturned ? Parse<List<string>>(latest!.ReturnFieldsJson) ?? new() : new(),
            };
            if (latest is not null && lastDeliveryById.TryGetValue(latest.Id, out var delivery))
            {
                item.LastSentAtUtc = delivery.CreatedAt;
                var meta = ParseDelivery(delivery.MetadataJson);
                item.LastSentEmail = meta.EmailRequested ? meta.EmailSent : null;
                item.LastSentSms = meta.SmsRequested ? meta.SmsSent : null;
            }
            return item;
        })
        // 需要处理的排前面，其余按阶段与姓名排。
        .OrderByDescending(x => x.NeedsAction).ThenBy(x => StageOrder(x.Stage)).ThenBy(x => x.EmployeeName, StringComparer.OrdinalIgnoreCase).ToList();
        return result;
    }

    /// <summary>员工任一关联分店在当前账号的门店范围内即可操作（管理员不限）。</summary>
    private async Task<bool> CanAccessStaffAsync(CurrentUserManageableStoreScope scope, string userGuid)
    {
        if (scope.IsAdmin) return true;
        var storeGuids = await _context.Db.Queryable<UserStore>()
            .InnerJoin<Store>((us, st) => us.StoreGUID == st.StoreGUID)
            .Where((us, st) => us.UserGUID == userGuid && !us.IsDeleted && !st.IsDeleted)
            .Select((us, st) => st.StoreGUID).ToListAsync();
        return storeGuids.Any(scope.CanAccessStoreGuid);
    }

    private static int StageOrder(string stage) => stage switch
    {
        StageNotSent => 0, StageLinkExpired => 1, StageReturned => 2, StageAwaitingGuardian => 3, StagePendingReview => 4, _ => 5,
    };

    /// <summary>列表里的打码电话：澳洲手机号按 E.164 打码，其余（如固话）只留后 3 位。</summary>
    private static string? MaskAnyPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var masked = MaskPhone(phone);
        if (masked is not null) return masked;
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length <= 3 ? "***" : $"***{digits[^3..]}";
    }

    private sealed record DeliveryMeta(bool EmailRequested, bool EmailSent, bool SmsRequested, bool SmsSent);

    private static DeliveryMeta ParseDelivery(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(false, false, false, false);
        try
        {
            using var doc = JsonDocument.Parse(json);
            bool Read(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            return new(Read("emailRequested"), Read("emailSent"), Read("smsRequested"), Read("smsSent"));
        }
        catch (JsonException) { return new(false, false, false, false); }
    }

    /// <summary>
    /// 门店第一次发送时新建的草稿：孩子姓名与联系方式取员工资料，雇主取门店资料；学校、通勤等留空由家长补全。
    /// </summary>
    private static EmployeeMinorCompliance NewStaffDraft(string userGuid, User user, string? childPhone, string? childAddress, Store? store)
    {
        var (given, family) = SplitName(user);
        var brand = string.IsNullOrWhiteSpace(store?.BrandName) ? null : store!.BrandName;
        var form = new EmployeeMinorCe1FormDto
        {
            ChildGivenName = given, ChildFamilyName = family, ChildPhone = childPhone, ChildAddress = childAddress,
            ChildEmail = string.IsNullOrWhiteSpace(user.Email) ? null : user.Email,
            // 门店资料没有法定公司名，先用品牌名；HR 审核时可在原件里核对。
            EmployerCompanyName = brand ?? "Hot Bargain",
            EmployerTradingName = brand ?? store?.StoreName ?? "Hot Bargain",
            EmployerAddress = store?.Address, EmployerPhone = store?.Phone, EmployerEmail = store?.ContactEmail,
        };
        var row = new EmployeeMinorCompliance
        {
            UserGUID = userGuid, Version = 1, Status = "draft",
            FormDataJson = JsonSerializer.Serialize(form, JsonOptions),
            EducationExemptionVerified = false,
        };
        if (store is not null) ApplyStoreFacts(row, store);
        else { row.StateCode = "UNKNOWN"; row.FormType = "UNSUPPORTED_STATE"; } // 发送时再按员工门店补齐州别与表单
        return row;
    }

    /// <summary>
    /// 新建员工（未满 18 岁）时，在建号的同一个事务里预建未成年合规草稿并带上监护人，状态「未发送」；
    /// 之后在员工详情 / Web 页签里直接发送签字链接，不用再录一遍监护人。
    /// </summary>
    internal static async Task InsertDraftForNewStaffAsync(ISqlSugarClient db, User user, string? childPhone, DateTime birthday, Store? store, NewStaffGuardianDto guardian, string? actorGuid, string actorLabel)
    {
        var row = NewStaffDraft(user.UserGUID, user, childPhone, null, store);
        row.Revision = 1;
        row.DateOfBirth = birthday.Date;
        row.GuardianName = guardian.Name!.Trim();
        row.GuardianRelationship = guardian.Relationship?.Trim();
        row.GuardianPhone = guardian.Phone!.Trim();
        row.GuardianEmail = guardian.Email!.Trim();
        row.CreatedBy = actorLabel;
        row.Id = await db.Insertable(row).ExecuteReturnIdentityAsync();
        await db.Insertable(new EmployeeMinorComplianceAudit
        {
            ComplianceId = row.Id, Action = "draft_created_with_new_staff", ActorUserGuid = actorGuid, ActorLabel = actorLabel,
        }).ExecuteCommandAsync();
    }

    /// <summary>退回后重新发送：复制上一版的全部资料成新版本，签字、审核与原件字段清空。</summary>
    private static EmployeeMinorCompliance CloneAsNewVersion(EmployeeMinorCompliance prior) => new()
    {
        UserGUID = prior.UserGUID, Version = prior.Version + 1, Status = "draft",
        StateCode = prior.StateCode, FormType = prior.FormType, DateOfBirth = prior.DateOfBirth,
        SchoolName = prior.SchoolName, YearLevel = prior.YearLevel, CompletedYear10 = prior.CompletedYear10,
        EducationStatus = prior.EducationStatus, RequiredToBeEnrolled = prior.RequiredToBeEnrolled,
        // 学校豁免核实只对上一版有效，新版本要 HR 重新确认。
        EducationExemptionVerified = false, ParticipationEndDate = prior.ParticipationEndDate,
        SchoolCalendarJson = prior.SchoolCalendarJson, OtherWorkJson = prior.OtherWorkJson, CommuteJson = prior.CommuteJson,
        FormDataJson = prior.FormDataJson,
    };

    private static (string? Given, string? Family) SplitName(User user)
    {
        if (!string.IsNullOrWhiteSpace(user.GivenName) || !string.IsNullOrWhiteSpace(user.FamilyName))
            return (user.GivenName?.Trim(), user.FamilyName?.Trim());
        var parts = (user.FullName ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => (null, null),
            1 => (parts[0], null),
            _ => (string.Join(' ', parts[..^1]), parts[^1]),
        };
    }
}
