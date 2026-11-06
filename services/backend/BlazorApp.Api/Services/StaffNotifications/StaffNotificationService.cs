using System.Globalization;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Models;
using BlazorApp.Api.Services.Sms;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace BlazorApp.Api.Services.StaffNotifications;

/// <summary>
/// 员工通知应用服务（给控制器用）：模板、预检、预览、排队发送、批次记录与重试。
/// 只做「判断 + 入队」：真正逐人发信由 <see cref="StaffNotificationDispatcher"/> 在后台任务里完成，所以接口立即返回。
/// </summary>
public interface IStaffNotificationService
{
    Task<ApiResponse<List<StaffNotificationTemplateDto>>> ListTemplatesAsync();

    Task<ApiResponse<StaffNotificationTemplateDto>> CreateTemplateAsync(StaffNotificationTemplateSaveDto dto, string actor);

    Task<ApiResponse<StaffNotificationTemplateDto>> UpdateTemplateAsync(string templateKey, StaffNotificationTemplateSaveDto dto, string actor);

    Task<ApiResponse<StaffNotificationTemplateDto>> ResetTemplateAsync(string templateKey, string actor);

    Task<ApiResponse<StaffNotificationPreflightDto>> PreflightAsync(StaffNotificationPreflightRequestDto dto);

    Task<ApiResponse<StaffNotificationPreviewDto>> PreviewAsync(StaffNotificationPreviewRequestDto dto);

    Task<ApiResponse<StaffNotificationSendResultDto>> SendAsync(StaffNotificationSendRequestDto dto, string actorName);

    Task<ApiResponse<PagedResult<StaffNotificationBatchDto>>> ListBatchesAsync(StaffNotificationBatchQueryDto query);

    Task<ApiResponse<StaffNotificationBatchDetailDto>> GetBatchAsync(string batchId);

    Task<ApiResponse<StaffNotificationBatchDetailDto>> RetryAsync(string batchId, string actorName);
}

internal sealed class StaffNotificationService : IStaffNotificationService
{
    public const string ForbiddenCode = "FORBIDDEN";
    public const string ConfirmRequiredCode = "CONFIRM_REQUIRED";
    public const string TemplateDisabledCode = "TEMPLATE_DISABLED";
    public const string NoRecipientsCode = "NO_RECIPIENTS";
    public const string BatchNotFoundCode = "BATCH_NOT_FOUND";
    private const string StoreStaffRoleName = "StoreStaff";

    /// <summary>邮件没发出，所以同一个人的短信也不发：重试时靠这个标记把短信一起恢复。</summary>
    internal const string SmsHeldByEmailFailure = "EMAIL_FAILED";

    private readonly SqlSugarContext _context;
    private readonly ICurrentUserManageableStoreScopeService _scopeService;
    private readonly IStaffNotificationTemplateService _templates;
    private readonly IStaffNotificationLinkProvider _links;
    private readonly ISmsSender _smsSender;
    private readonly SmsOptions _smsOptions;
    private readonly IStaffNotificationSignal _signal;
    private readonly ILogger<StaffNotificationService> _logger;

    public StaffNotificationService(
        SqlSugarContext context,
        ICurrentUserManageableStoreScopeService scopeService,
        IStaffNotificationTemplateService templates,
        IStaffNotificationLinkProvider links,
        ISmsSender smsSender,
        IOptions<SmsOptions> smsOptions,
        IStaffNotificationSignal signal,
        ILogger<StaffNotificationService> logger
    )
    {
        _context = context;
        _scopeService = scopeService;
        _templates = templates;
        _links = links;
        _smsSender = smsSender;
        _smsOptions = smsOptions.Value;
        _signal = signal;
        _logger = logger;
    }

    // ───────────── 模板 ─────────────

    public async Task<ApiResponse<List<StaffNotificationTemplateDto>>> ListTemplatesAsync()
    {
        var templates = await _templates.ListAsync();
        return ApiResponse<List<StaffNotificationTemplateDto>>.OK(
            templates.Select(StaffNotificationTemplateService.ToDto).ToList()
        );
    }

    public Task<ApiResponse<StaffNotificationTemplateDto>> CreateTemplateAsync(StaffNotificationTemplateSaveDto dto, string actor) =>
        _templates.CreateAsync(dto, actor);

    public Task<ApiResponse<StaffNotificationTemplateDto>> UpdateTemplateAsync(string templateKey, StaffNotificationTemplateSaveDto dto, string actor) =>
        _templates.UpdateAsync(templateKey, dto, actor);

    public Task<ApiResponse<StaffNotificationTemplateDto>> ResetTemplateAsync(string templateKey, string actor) =>
        _templates.ResetAsync(templateKey, actor);

    // ───────────── 预检 ─────────────

    public async Task<ApiResponse<StaffNotificationPreflightDto>> PreflightAsync(StaffNotificationPreflightRequestDto dto)
    {
        var prepared = await PrepareAsync(dto.TemplateKey, dto.UserGuids, dto.SendEmail, dto.SendSms);
        if (prepared.Error is not null) return ApiResponse<StaffNotificationPreflightDto>.Error(prepared.Error.Message, prepared.Error.ErrorCode);

        var since = DateTime.UtcNow.AddHours(-24);
        var used = await _context.Db.Queryable<SmsSendLog>().Where(item => item.CreatedAtUtc > since).CountAsync();
        return ApiResponse<StaffNotificationPreflightDto>.OK(new StaffNotificationPreflightDto
        {
            SmsAvailable = _smsSender.IsAvailable,
            SmsUsedLast24h = used,
            SmsDailyLimit = _smsOptions.GlobalDayLimit,
            SmsUnitCostAud = StaffNotificationRules.SmsUnitCostAud,
            Recipients = prepared.Assessments!.Select(item => item.Dto).ToList(),
        });
    }

    // ───────────── 预览 ─────────────

    public async Task<ApiResponse<StaffNotificationPreviewDto>> PreviewAsync(StaffNotificationPreviewRequestDto dto)
    {
        var prepared = await PrepareAsync(dto.TemplateKey, new List<string> { dto.UserGuid ?? string.Empty }, sendEmail: true, sendSms: true, requireEnabled: false);
        if (prepared.Error is not null) return ApiResponse<StaffNotificationPreviewDto>.Error(prepared.Error.Message, prepared.Error.ErrorCode);

        var assessment = prepared.Assessments!.Single();
        // 预览只给有权管理的员工看；员工不存在或越权时不泄露任何资料。
        if (assessment.Dto.Reasons.Contains("not_found") || assessment.Dto.Reasons.Contains("forbidden"))
        {
            return ApiResponse<StaffNotificationPreviewDto>.Error("无权预览该员工", ForbiddenCode);
        }

        var template = prepared.Template!;
        var links = await _links.GetAsync();
        var context = new StaffNotificationRenderContext(
            assessment.Dto.DisplayName,
            assessment.Dto.Email ?? string.Empty,
            assessment.Dto.StoreName ?? string.Empty,
            links.AppLink,
            links.IosUrl,
            links.AndroidUrl
        );

        // 预览里密码永远是圆点：真实密码只在发送那一刻生成，后台任何人都看不到。
        var email = StaffNotificationRenderer.RenderEmail(
            template.EmailSubject,
            template.EmailBody,
            context,
            template.IssuesPassword ? StaffNotificationRenderer.PreviewPassword : null
        );

        var smsText = template.SupportsSms ? StaffNotificationRenderer.RenderSms(template.SmsBody, context) : string.Empty;
        var smsValid = smsText.Length == 0 || SmsMessages.IsGsm7Safe(smsText);
        return ApiResponse<StaffNotificationPreviewDto>.OK(new StaffNotificationPreviewDto
        {
            EmailSubject = email.Subject,
            EmailHtml = email.Html,
            EmailText = email.Text,
            SmsText = smsText,
            SmsLength = smsText.Length,
            SmsSegments = smsText.Length == 0 ? 0 : SmsMessages.SegmentCount(smsText),
            SmsValid = smsValid,
            SmsError = smsValid ? null : "短信含不支持的字符，发送时会被拒绝",
        });
    }

    // ───────────── 发送（入队） ─────────────

    /// <summary>
    /// 「预检 → 建批次」必须串行：连点两次「确认发送」时，两个请求会同时通过「是否已在进行中」检查，
    /// 结果是同一批人被重置两次密码（先到的邮件里的密码立刻作废）。主 API 是单进程部署，进程内锁就够了；
    /// 锁只包住这一小段（几次数据库读写），不包后台逐人发信。
    /// </summary>
    private static readonly SemaphoreSlim SendGate = new(1, 1);

    public async Task<ApiResponse<StaffNotificationSendResultDto>> SendAsync(StaffNotificationSendRequestDto dto, string actorName)
    {
        await SendGate.WaitAsync();
        try
        {
            return await SendCoreAsync(dto, actorName);
        }
        finally
        {
            SendGate.Release();
        }
    }

    private async Task<ApiResponse<StaffNotificationSendResultDto>> SendCoreAsync(StaffNotificationSendRequestDto dto, string actorName)
    {
        var template = await _templates.GetAsync(dto.TemplateKey);
        // 系统模板「账号已开通」必须发邮件（随机密码只走邮件），前端即使传了 sendEmail=false 也以服务端为准。
        var sendEmail = dto.SendEmail || (template?.RequiresEmail ?? false);

        var prepared = await PrepareAsync(dto.TemplateKey, dto.UserGuids, sendEmail, dto.SendSms);
        if (prepared.Error is not null) return ApiResponse<StaffNotificationSendResultDto>.Error(prepared.Error.Message, prepared.Error.ErrorCode);
        if (!sendEmail && !dto.SendSms) return ApiResponse<StaffNotificationSendResultDto>.Error("请至少选择一个发送渠道", "VALIDATION_ERROR");

        var accepted = prepared.Assessments!.Where(item => item.Dto.Status != "blocked").ToList();
        var rejected = prepared.Assessments!
            .Where(item => item.Dto.Status == "blocked")
            .Select(item => new StaffNotificationRejectedDto { UserGuid = item.Dto.UserGuid, Reason = item.Dto.Reasons.FirstOrDefault() ?? "blocked" })
            .ToList();
        if (accepted.Count == 0)
        {
            return ApiResponse<StaffNotificationSendResultDto>.Error("没有可发送的收件人", NoRecipientsCode, rejected);
        }

        // 登录过的员工：发放密码会让他们的旧密码失效、所有设备被踢下线，必须由操作人明确确认。
        if (accepted.Any(item => item.Dto.Reasons.Contains("already_logged_in")) && !dto.ConfirmReissue)
        {
            return ApiResponse<StaffNotificationSendResultDto>.Error("选中的员工里有登录过的账号，发送后他们的旧密码会失效，请先确认", ConfirmRequiredCode);
        }

        var actor = prepared.Actor!;
        var now = DateTime.UtcNow;
        var batch = new StaffNotificationBatch
        {
            TemplateKey = prepared.Template!.Key,
            TemplateName = prepared.Template.Name,
            CreatedBy = string.IsNullOrWhiteSpace(actorName) ? "system" : actorName,
            CreatedByUserGuid = actor.UserGuid,
            SendEmail = sendEmail,
            SendSms = dto.SendSms,
            Status = StaffNotificationBatchStatuses.Queued,
            TotalCount = accepted.Count,
            CreatedAtUtc = now,
        };
        var recipients = accepted.Select((item, index) => BuildRecipient(batch.Id, item, sendEmail, dto.SendSms, index, now)).ToList();

        var db = _context.Db;
        await db.Ado.BeginTranAsync();
        try
        {
            await db.Insertable(batch).ExecuteCommandAsync();
            await db.Insertable(recipients).ExecuteCommandAsync();
            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }

        _signal.Notify();
        _logger.LogInformation(
            "员工通知已排队，批次：{BatchId}，模板：{TemplateKey}，人数：{Count}，操作人：{Actor}",
            batch.Id, batch.TemplateKey, batch.TotalCount, batch.CreatedBy
        );
        return ApiResponse<StaffNotificationSendResultDto>.OK(
            new StaffNotificationSendResultDto
            {
                BatchId = batch.Id,
                Status = batch.Status,
                TotalCount = batch.TotalCount,
                Rejected = rejected,
            },
            "已开始发送"
        );
    }

    private static StaffNotificationRecipient BuildRecipient(string batchId, Assessment item, bool sendEmail, bool sendSms, int index, DateTime now)
    {
        var dto = item.Dto;
        var notes = new List<string>();

        string emailStatus;
        if (!sendEmail) emailStatus = StaffNotificationChannelStatuses.None;
        else if (dto.WillSendEmail) emailStatus = StaffNotificationChannelStatuses.Pending;
        else
        {
            emailStatus = StaffNotificationChannelStatuses.Skipped;
            notes.Add("没有可用邮箱，邮件未发");
        }

        string smsStatus;
        if (!sendSms) smsStatus = StaffNotificationChannelStatuses.None;
        else if (dto.WillSendSms) smsStatus = StaffNotificationChannelStatuses.Pending;
        else
        {
            smsStatus = StaffNotificationChannelStatuses.Skipped;
            notes.Add(SmsSkipText(dto.SmsSkipReason));
        }

        return new StaffNotificationRecipient
        {
            BatchId = batchId,
            UserGuid = dto.UserGuid,
            DisplayName = Truncate(dto.DisplayName, 200),
            StoreName = dto.StoreName is null ? null : Truncate(dto.StoreName, 200),
            EmailMask = string.IsNullOrWhiteSpace(dto.Email) ? null : PasswordResetService.MaskEmail(dto.Email),
            PhoneMask = item.PhoneMask,
            EmailStatus = emailStatus,
            SmsStatus = smsStatus,
            Note = Truncate(string.Join("；", notes), 300),
            SortOrder = index,
            UpdatedAtUtc = now,
        };
    }

    internal static string SmsSkipText(string? reason) => reason switch
    {
        "no_phone" => "没有手机号，短信未发",
        "not_mobile" => "固定电话收不到短信，短信未发",
        "sms_unavailable" => "短信服务未开启，短信未发",
        "sms_off" => "这个模板没有短信版本，短信未发",
        _ => "短信未发",
    };

    // ───────────── 批次记录 ─────────────

    public async Task<ApiResponse<PagedResult<StaffNotificationBatchDto>>> ListBatchesAsync(StaffNotificationBatchQueryDto query)
    {
        var actor = await ResolveActorAsync();
        if (actor.Kind == UserAccessMutationActorKind.Denied)
        {
            return ApiResponse<PagedResult<StaffNotificationBatchDto>>.Error("当前账号无权查看发送记录", ForbiddenCode);
        }

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var db = _context.Db;

        var batches = db.Queryable<StaffNotificationBatch>();
        batches = ApplyVisibility(batches, actor);

        if (!string.IsNullOrWhiteSpace(query.TemplateKey))
        {
            var key = query.TemplateKey.Trim();
            batches = batches.Where(item => item.TemplateKey == key);
        }
        if (string.Equals(query.Channel, "email", StringComparison.OrdinalIgnoreCase)) batches = batches.Where(item => item.SendEmail);
        if (string.Equals(query.Channel, "sms", StringComparison.OrdinalIgnoreCase)) batches = batches.Where(item => item.SendSms);

        var (fromUtc, toUtc) = ParseSydneyRange(query.From, query.To);
        if (fromUtc is not null) { var value = fromUtc.Value; batches = batches.Where(item => item.CreatedAtUtc >= value); }
        if (toUtc is not null) { var value = toUtc.Value; batches = batches.Where(item => item.CreatedAtUtc < value); }

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            batches = batches.Where(item =>
                item.CreatedBy.Contains(keyword)
                || SqlFunc.Subqueryable<StaffNotificationRecipient>()
                    .Where(r => r.BatchId == item.Id && r.DisplayName.Contains(keyword))
                    .Any()
            );
        }

        var result = (query.Result ?? string.Empty).Trim().ToLowerInvariant();
        if (result.Length == 0)
        {
            // 没有「结果」筛选：数据库直接分页，只给当前页的批次汇总逐人状态。
            var total = await batches.CountAsync();
            var pageRows = await batches.OrderByDescending(item => item.CreatedAtUtc)
                .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
            var items = await SummarizeAsync(pageRows);
            return ApiResponse<PagedResult<StaffNotificationBatchDto>>.OK(new PagedResult<StaffNotificationBatchDto>
            {
                Items = items, Total = total, Page = page, PageSize = pageSize,
            });
        }

        // 「结果」是按逐人状态算出来的，没法在 SQL 里直接过滤：取最近 300 个符合其他条件的批次，汇总后再筛选分页。
        var candidates = await batches.OrderByDescending(item => item.CreatedAtUtc).Take(300).ToListAsync();
        var summaries = (await SummarizeAsync(candidates)).Where(item => item.Result == result).ToList();
        return ApiResponse<PagedResult<StaffNotificationBatchDto>>.OK(new PagedResult<StaffNotificationBatchDto>
        {
            Items = summaries.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            Total = summaries.Count,
            Page = page,
            PageSize = pageSize,
        });
    }

    public async Task<ApiResponse<StaffNotificationBatchDetailDto>> GetBatchAsync(string batchId)
    {
        var actor = await ResolveActorAsync();
        var loaded = await LoadVisibleBatchAsync(batchId, actor);
        if (loaded.Error is not null) return ApiResponse<StaffNotificationBatchDetailDto>.Error(loaded.Error.Message, loaded.Error.ErrorCode);
        return ApiResponse<StaffNotificationBatchDetailDto>.OK(await BuildDetailAsync(loaded.Batch!));
    }

    public async Task<ApiResponse<StaffNotificationBatchDetailDto>> RetryAsync(string batchId, string actorName)
    {
        // 与发送共用同一把锁：重试和发送都会重置密码，不能并行判断「是否已在进行中」。
        await SendGate.WaitAsync();
        try
        {
            return await RetryCoreAsync(batchId, actorName);
        }
        finally
        {
            SendGate.Release();
        }
    }

    private async Task<ApiResponse<StaffNotificationBatchDetailDto>> RetryCoreAsync(string batchId, string actorName)
    {
        var actor = await ResolveActorAsync();
        var loaded = await LoadVisibleBatchAsync(batchId, actor);
        if (loaded.Error is not null) return ApiResponse<StaffNotificationBatchDetailDto>.Error(loaded.Error.Message, loaded.Error.ErrorCode);

        var batch = loaded.Batch!;
        if (batch.Status is StaffNotificationBatchStatuses.Queued or StaffNotificationBatchStatuses.Running)
        {
            return ApiResponse<StaffNotificationBatchDetailDto>.Error("这个批次还在发送中，请等它结束后再重试", "BATCH_IN_PROGRESS");
        }

        var db = _context.Db;
        var recipients = await db.Queryable<StaffNotificationRecipient>().Where(item => item.BatchId == batch.Id).ToListAsync();
        var failed = recipients.Where(item =>
            item.EmailStatus == StaffNotificationChannelStatuses.Failed || item.SmsStatus == StaffNotificationChannelStatuses.Failed).ToList();
        if (failed.Count == 0)
        {
            return ApiResponse<StaffNotificationBatchDetailDto>.Error("没有需要重试的失败项", "NOTHING_TO_RETRY");
        }

        // 第一步（只读）：逐个失败行判断能不能重试。重试和新发送一样会重置密码，所以要重新套用发送时的保护，
        // 不能只校验「操作人有没有权管理他」。
        var issuesPassword = batch.TemplateKey == StaffNotificationTemplateDefaults.AccountActivatedKey;
        var busy = issuesPassword
            ? await LoadBusyUserGuidsAsync(failed.Select(item => item.UserGuid).ToList())
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = new List<(StaffNotificationRecipient Row, string? SkipNote)>();
        foreach (var item in failed)
        {
            plans.Add((item, await ExplainRetryBlockAsync(batch, item, actor, issuesPassword, busy)));
        }

        // 第二步（写）：行状态与批次状态在同一个事务里改，要么全改要么都不改；
        // 批次状态用「仍是已结束」做条件翻转，别处先把它排队了就整体回滚，不会留下后台永远不会领取的 pending 行。
        var now = DateTime.UtcNow;
        var retried = 0;
        await db.Ado.BeginTranAsync();
        try
        {
            foreach (var (item, skipNote) in plans)
            {
                if (skipNote is not null)
                {
                    item.Note = Truncate(skipNote, 300);
                    item.UpdatedAtUtc = now;
                    await db.Updateable(item).ExecuteCommandAsync();
                    continue;
                }

                // 邮件失败 → 邮件重发（会重新生成密码）；因邮件失败而被扣住的短信一并恢复。
                // 只有短信失败 → 只补短信，不会重新生成密码。
                if (item.EmailStatus == StaffNotificationChannelStatuses.Failed)
                {
                    item.EmailStatus = StaffNotificationChannelStatuses.Pending;
                    item.EmailError = null;
                    if (item.SmsStatus == StaffNotificationChannelStatuses.Skipped && item.SmsError == SmsHeldByEmailFailure)
                    {
                        item.SmsStatus = StaffNotificationChannelStatuses.Pending;
                        item.SmsError = null;
                    }
                }
                if (item.SmsStatus == StaffNotificationChannelStatuses.Failed)
                {
                    item.SmsStatus = StaffNotificationChannelStatuses.Pending;
                    item.SmsError = null;
                }

                item.Note = string.Empty;
                item.UpdatedAtUtc = now;
                await db.Updateable(item).ExecuteCommandAsync();
                retried++;
            }

            if (retried > 0)
            {
                var retryBatchId = batch.Id;
                var flipped = await db.Updateable<StaffNotificationBatch>()
                    .SetColumns(item => new StaffNotificationBatch { Status = StaffNotificationBatchStatuses.Queued, FinishedAtUtc = null })
                    .Where(item => item.Id == retryBatchId
                        && (item.Status == StaffNotificationBatchStatuses.Completed || item.Status == StaffNotificationBatchStatuses.Interrupted))
                    .ExecuteCommandAsync();
                if (flipped == 0)
                {
                    await db.Ado.RollbackTranAsync();
                    return ApiResponse<StaffNotificationBatchDetailDto>.Error("这个批次的状态刚刚变了，请刷新后再试", "BATCH_IN_PROGRESS");
                }
            }

            await db.Ado.CommitTranAsync();
        }
        catch
        {
            await db.Ado.RollbackTranAsync();
            throw;
        }

        if (retried == 0)
        {
            // 说明已写进每个员工的「说明」列，界面刷新后能看到为什么没有重试。
            return ApiResponse<StaffNotificationBatchDetailDto>.Error("这些失败项都不能自动重试，原因见每个员工的说明", "NOTHING_TO_RETRY");
        }

        batch.Status = StaffNotificationBatchStatuses.Queued;
        batch.FinishedAtUtc = null;
        _signal.Notify();
        _logger.LogInformation("员工通知重试已排队，批次：{BatchId}，重试人数：{Count}，操作人：{Actor}", batch.Id, retried, actorName);
        return ApiResponse<StaffNotificationBatchDetailDto>.OK(await BuildDetailAsync(batch), "已开始重试");
    }

    /// <summary>
    /// 这个失败行现在能不能重试；不能就返回给操作人看的原因（写进「说明」列），能就返回 null。
    /// 邮件重试会重新生成密码、让旧登录失效，所以对发放密码的模板要重新套用发送时的保护：
    /// 操作人仍有权管理他、不是给自己发、他还没有在这批发送之后登录或被重新发放过密码、他不在别的进行中批次里。
    /// 只补短信不会动密码，只校验管理权限。
    /// </summary>
    private async Task<string?> ExplainRetryBlockAsync(
        StaffNotificationBatch batch,
        StaffNotificationRecipient item,
        UserAccessMutationActor actor,
        bool issuesPassword,
        HashSet<string> busy
    )
    {
        var db = _context.Db;
        var decision = await ValidateRecipientAsync(actor, item.UserGuid);
        if (!decision.IsAllowed) return "无权重试该员工";

        var retriesEmail = item.EmailStatus == StaffNotificationChannelStatuses.Failed;
        if (!issuesPassword || !retriesEmail) return null;

        if (string.Equals(actor.UserGuid, item.UserGuid, StringComparison.OrdinalIgnoreCase))
        {
            return "不能给自己重新发放密码";
        }
        if (busy.Contains(item.UserGuid))
        {
            return "该员工已在另一个进行中的批次里，未重试";
        }

        var user = await db.Queryable<User>().FirstAsync(row => row.UserGUID == item.UserGuid && !row.IsDeleted);
        if (user?.LastLoginAt is { } lastLoginAt && lastLoginAt > batch.CreatedAtUtc)
        {
            // 这批发送之后他已经登录过（可能就是用上一封邮件里的密码，或者「结果未知」的那封其实已送达）：
            // 重试会把他踢下线，必须走新发送的二次确认，不能在重试里悄悄做。
            return "该员工在这批发送之后已经登录过，未重试；如确需重新发放，请重新发送（会要求确认）";
        }

        var requirement = await UserPasswordChangeRequirements.GetAsync(db, item.UserGuid);
        if (requirement is not null && requirement.RequiredAtUtc > batch.CreatedAtUtc)
        {
            return "该员工的密码在这之后已被重新发放过，未重试";
        }

        return null;
    }

    // ───────────── 内部：预检数据准备 ─────────────

    private sealed record ServiceError(string Message, string ErrorCode);

    private sealed record Assessment(StaffNotificationPreflightRecipientDto Dto, string? PhoneMask);

    private sealed record Prepared(
        ServiceError? Error,
        StaffNotificationTemplateContent? Template,
        UserAccessMutationActor? Actor,
        List<Assessment>? Assessments
    );

    private sealed record LoadedBatch(ServiceError? Error, StaffNotificationBatch? Batch);

    private async Task<UserAccessMutationActor> ResolveActorAsync() =>
        await UserAccessMutationSecurity.ResolveActorAsync(_context.Db, _scopeService);

    /// <summary>
    /// 操作人能不能给这个员工发通知。在通用的目标校验（高权限账号、等级、分店范围）之上，店长再加一条：
    /// 目标必须是店员角色。发放密码等于重置密码，店长现有的重置入口（StoreUserReactService.LoadManagedUserAsync）只认店员，
    /// 这里不能比它更宽，否则店长能重置「挂在本店、等级更低、但不是店员」的账号。
    /// </summary>
    private async Task<UserAccessMutationDecision> ValidateRecipientAsync(UserAccessMutationActor actor, string userGuid)
    {
        var db = _context.Db;
        var decision = await UserAccessMutationSecurity.ValidateTargetAsync(db, actor, userGuid);
        if (!decision.IsAllowed || !actor.IsStoreManager) return decision;

        var isStoreStaff = await db.Queryable<UserRole>()
            .InnerJoin<Role>((userRole, role) => userRole.RoleGUID == role.RoleGUID)
            .Where((userRole, role) =>
                userRole.UserGUID == userGuid
                && !userRole.IsDeleted
                && !role.IsDeleted
                && role.RoleName == StoreStaffRoleName)
            .AnyAsync();
        return isStoreStaff ? decision : new UserAccessMutationDecision(false, "无权修改该用户", "USER_SCOPE_DENIED");
    }

    /// <summary>
    /// 预检、预览、发送共用的判断：解析模板与操作人，再逐人评估能不能发、要不要确认。
    /// 发送时服务端会重新跑一遍，不信任前端传来的预检结果。
    /// </summary>
    private async Task<Prepared> PrepareAsync(string templateKey, IReadOnlyList<string>? userGuids, bool sendEmail, bool sendSms, bool requireEnabled = true)
    {
        var template = await _templates.GetAsync(templateKey);
        if (template is null) return Fail("模板不存在", StaffNotificationTemplateService.TemplateNotFoundCode);
        if (requireEnabled && !template.IsEnabled) return Fail("这个模板已停用", TemplateDisabledCode);

        var guids = (userGuids ?? Array.Empty<string>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (guids.Count == 0) return Fail("请先选择收件人", "VALIDATION_ERROR");
        if (guids.Count > StaffNotificationRules.MaxRecipientsPerBatch)
        {
            return Fail($"一次最多发送给 {StaffNotificationRules.MaxRecipientsPerBatch} 人", "VALIDATION_ERROR");
        }

        var actor = await ResolveActorAsync();
        if (actor.Kind == UserAccessMutationActorKind.Denied)
        {
            return Fail("当前账号无权发送员工通知", ForbiddenCode);
        }

        var effectiveEmail = sendEmail || template.RequiresEmail;
        var assessments = await AssessAsync(template, actor, guids, effectiveEmail, sendSms);
        return new Prepared(null, template, actor, assessments);

        static Prepared Fail(string message, string code) => new(new ServiceError(message, code), null, null, null);
    }

    private async Task<List<Assessment>> AssessAsync(
        StaffNotificationTemplateContent template,
        UserAccessMutationActor actor,
        List<string> guids,
        bool sendEmail,
        bool sendSms
    )
    {
        var db = _context.Db;
        var users = (await db.Queryable<User>().Where(item => guids.Contains(item.UserGUID) && !item.IsDeleted).ToListAsync())
            .ToDictionary(item => item.UserGUID, StringComparer.OrdinalIgnoreCase);
        var phones = (await db.Queryable<EmployeeProfile>().Where(item => guids.Contains(item.UserGUID) && !item.IsDeleted).ToListAsync())
            .GroupBy(item => item.UserGUID, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Phone).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)), StringComparer.OrdinalIgnoreCase);
        var restricted = await UserLoginRestrictions.GetRestrictedUserGuidsAsync(db, guids);
        var storeNames = await LoadStoreNamesAsync(guids);
        var busy = await LoadBusyUserGuidsAsync(guids);
        var links = await _links.GetAsync();
        var smsAvailable = _smsSender.IsAvailable;

        var result = new List<Assessment>(guids.Count);
        foreach (var guid in guids)
        {
            users.TryGetValue(guid, out var user);
            var dto = new StaffNotificationPreflightRecipientDto { UserGuid = guid };
            string? phoneMask = null;

            if (user is null)
            {
                dto.Status = "blocked";
                dto.Reasons.Add("not_found");
                dto.SmsSkipReason = "sms_off";
                result.Add(new Assessment(dto, null));
                continue;
            }

            // 越权目标：只返回「被拦下」，不透露登录名、邮箱、手机号、最近登录时间等任何资料
            // （否则仓库经理拿到管理员的 GUID 就能通过预检读出对方的联系方式，绕开「仓库经理看不到管理员」的口径）。
            // 给自己发是另一种拦截（self），资料本来就是操作人自己的，照常返回。
            var isSelf = template.IssuesPassword && string.Equals(actor.UserGuid, user.UserGUID, StringComparison.OrdinalIgnoreCase);
            if (!isSelf)
            {
                var decision = await ValidateRecipientAsync(actor, user.UserGUID);
                if (!decision.IsAllowed)
                {
                    dto.Status = "blocked";
                    dto.Reasons.Add("forbidden");
                    dto.SmsSkipReason = "sms_off";
                    result.Add(new Assessment(dto, null));
                    continue;
                }
            }

            var email = (user.Email ?? string.Empty).Trim();
            var deliverable = PasswordResetService.IsDeliverableEmail(email);
            phones.TryGetValue(user.UserGUID, out var rawPhone);
            var phoneOk = AustralianPhoneNumber.TryNormalize(rawPhone, out var e164);
            if (!string.IsNullOrWhiteSpace(rawPhone))
            {
                phoneMask = phoneOk ? AustralianPhoneNumber.Mask(e164) : StaffNotificationRules.MaskRawPhone(rawPhone);
            }

            dto.DisplayName = string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName!;
            dto.LoginName = user.Username;
            dto.StoreName = storeNames.GetValueOrDefault(user.UserGUID);
            dto.Email = deliverable ? email : (string.IsNullOrWhiteSpace(email) ? null : email);
            dto.Phone = string.IsNullOrWhiteSpace(rawPhone) ? null : rawPhone!.Trim();
            dto.IsActive = user.IsActive;
            dto.HasLoggedIn = user.LastLoginAt is not null;
            dto.LastLoginAtUtc = user.LastLoginAt;

            // 渠道能不能发
            dto.WillSendEmail = sendEmail && deliverable;
            dto.SmsSkipReason = !sendSms || !template.SupportsSms ? "sms_off"
                : !smsAvailable ? "sms_unavailable"
                : string.IsNullOrWhiteSpace(rawPhone) ? "no_phone"
                : !phoneOk ? "not_mobile"
                : null;
            dto.WillSendSms = dto.SmsSkipReason is null;
            if (dto.WillSendSms)
            {
                var sms = StaffNotificationRenderer.RenderSms(
                    template.SmsBody,
                    new StaffNotificationRenderContext(dto.DisplayName, email, dto.StoreName ?? string.Empty, links.AppLink, links.IosUrl, links.AndroidUrl)
                );
                dto.SmsSegments = SmsMessages.SegmentCount(sms);
            }

            // 不可发送
            var blocked = new List<string>();
            if (isSelf) blocked.Add("self"); // 给自己发会重置自己的密码并把自己踢下线
            if (!user.IsActive) blocked.Add("account_inactive");
            if (restricted.Contains(user.UserGUID)) blocked.Add("login_disabled");
            if (busy.Contains(user.UserGUID)) blocked.Add("in_progress");
            if (template.RequiresEmail && !deliverable) blocked.Add("no_email");
            if (blocked.Count == 0 && !dto.WillSendEmail && !dto.WillSendSms)
            {
                blocked.Add(sendEmail && !deliverable ? "no_email" : "no_channel");
            }

            // 需确认（默认不勾选，但操作人可以手动勾上）
            var confirm = new List<string>();
            if (template.IssuesPassword && dto.HasLoggedIn) confirm.Add("already_logged_in");
            if (dto.WillSendEmail && StaffNotificationRules.IsSuspiciousEmail(email)) confirm.Add("suspicious_email");

            dto.Reasons.AddRange(blocked);
            dto.Reasons.AddRange(confirm);
            dto.Status = blocked.Count > 0 ? "blocked" : confirm.Count > 0 ? "confirm" : "ready";
            result.Add(new Assessment(dto, phoneMask));
        }

        return result;
    }

    /// <summary>每个员工的主分店名（没有主分店时取第一个关联分店）。</summary>
    private async Task<Dictionary<string, string>> LoadStoreNamesAsync(List<string> guids)
    {
        var db = _context.Db;
        var links = await db.Queryable<UserStore>().Where(item => guids.Contains(item.UserGUID) && !item.IsDeleted).ToListAsync();
        if (links.Count == 0) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var storeGuids = links.Select(item => item.StoreGUID).Distinct().ToList();
        var stores = (await db.Queryable<Store>().Where(item => storeGuids.Contains(item.StoreGUID) && !item.IsDeleted).ToListAsync())
            .ToDictionary(item => item.StoreGUID, item => item.StoreName, StringComparer.OrdinalIgnoreCase);

        return links
            .Where(item => stores.ContainsKey(item.StoreGUID))
            .GroupBy(item => item.UserGUID, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => stores[group.OrderByDescending(item => item.IsPrimary).ThenBy(item => stores[item.StoreGUID], StringComparer.OrdinalIgnoreCase).First().StoreGUID],
                StringComparer.OrdinalIgnoreCase
            );
    }

    /// <summary>已经在「排队 / 发送中」批次里、还有渠道没发完的员工：不能再被加进新批次（避免重复重置密码）。</summary>
    private async Task<HashSet<string>> LoadBusyUserGuidsAsync(List<string> guids)
    {
        var db = _context.Db;
        var activeBatchIds = await db.Queryable<StaffNotificationBatch>()
            .Where(item => item.Status == StaffNotificationBatchStatuses.Queued || item.Status == StaffNotificationBatchStatuses.Running)
            .Select(item => item.Id)
            .ToListAsync();
        if (activeBatchIds.Count == 0) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var busy = await db.Queryable<StaffNotificationRecipient>()
            .Where(item => activeBatchIds.Contains(item.BatchId) && guids.Contains(item.UserGuid)
                && (item.EmailStatus == StaffNotificationChannelStatuses.Pending || item.EmailStatus == StaffNotificationChannelStatuses.Sending
                    || item.SmsStatus == StaffNotificationChannelStatuses.Pending || item.SmsStatus == StaffNotificationChannelStatuses.Sending))
            .Select(item => item.UserGuid)
            .ToListAsync();
        return busy.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // ───────────── 内部：批次可见性与汇总 ─────────────

    /// <summary>
    /// 只有管理员看全部批次；其他人（含仓库经理）只看自己发出的批次。
    /// 仓库经理看不到管理员账号（#1205），批次记录里有收件人的姓名与打码邮箱，所以也不能让他看到管理员发的批次。
    /// </summary>
    private static ISugarQueryable<StaffNotificationBatch> ApplyVisibility(ISugarQueryable<StaffNotificationBatch> query, UserAccessMutationActor actor)
    {
        if (actor.IsSuperAdmin) return query;
        var guid = actor.UserGuid;
        return query.Where(item => item.CreatedByUserGuid == guid);
    }

    private async Task<LoadedBatch> LoadVisibleBatchAsync(string batchId, UserAccessMutationActor actor)
    {
        if (actor.Kind == UserAccessMutationActorKind.Denied)
        {
            return new LoadedBatch(new ServiceError("当前账号无权查看发送记录", ForbiddenCode), null);
        }

        var id = (batchId ?? string.Empty).Trim();
        var batch = await ApplyVisibility(_context.Db.Queryable<StaffNotificationBatch>(), actor).FirstAsync(item => item.Id == id);
        return batch is null
            ? new LoadedBatch(new ServiceError("发送记录不存在", BatchNotFoundCode), null)
            : new LoadedBatch(null, batch);
    }

    private async Task<StaffNotificationBatchDetailDto> BuildDetailAsync(StaffNotificationBatch batch)
    {
        var recipients = await _context.Db.Queryable<StaffNotificationRecipient>()
            .Where(item => item.BatchId == batch.Id)
            .OrderBy(item => item.SortOrder)
            .ToListAsync();

        var summary = Summarize(batch, recipients);
        return new StaffNotificationBatchDetailDto
        {
            Id = summary.Id,
            TemplateKey = summary.TemplateKey,
            TemplateName = summary.TemplateName,
            CreatedBy = summary.CreatedBy,
            CreatedAtUtc = summary.CreatedAtUtc,
            FinishedAtUtc = summary.FinishedAtUtc,
            Status = summary.Status,
            Result = summary.Result,
            TotalCount = summary.TotalCount,
            EmailRequested = summary.EmailRequested,
            EmailSent = summary.EmailSent,
            EmailFailed = summary.EmailFailed,
            SmsRequested = summary.SmsRequested,
            SmsSent = summary.SmsSent,
            SmsFailed = summary.SmsFailed,
            SmsSegments = summary.SmsSegments,
            SmsCostAud = summary.SmsCostAud,
            CanRetry = summary.CanRetry,
            Recipients = recipients.Select(item => new StaffNotificationRecipientResultDto
            {
                Id = item.Id,
                UserGuid = item.UserGuid,
                DisplayName = item.DisplayName,
                StoreName = item.StoreName,
                EmailMask = item.EmailMask,
                PhoneMask = item.PhoneMask,
                EmailStatus = ToPublicStatus(item.EmailStatus),
                SmsStatus = ToPublicStatus(item.SmsStatus),
                Note = item.Note,
            }).ToList(),
        };
    }

    /// <summary>「发送中」对外按「待发送」展示：前端只关心有没有结果。</summary>
    private static string ToPublicStatus(string status) =>
        status == StaffNotificationChannelStatuses.Sending ? StaffNotificationChannelStatuses.Pending : status;

    private async Task<List<StaffNotificationBatchDto>> SummarizeAsync(List<StaffNotificationBatch> batches)
    {
        if (batches.Count == 0) return new List<StaffNotificationBatchDto>();
        var ids = batches.Select(item => item.Id).ToList();
        var recipients = (await _context.Db.Queryable<StaffNotificationRecipient>().Where(item => ids.Contains(item.BatchId)).ToListAsync())
            .GroupBy(item => item.BatchId)
            .ToDictionary(group => group.Key, group => group.ToList());
        return batches.Select(batch => Summarize(batch, recipients.GetValueOrDefault(batch.Id) ?? new List<StaffNotificationRecipient>())).ToList();
    }

    /// <summary>
    /// 汇总一个批次。「请求数」= 原本打算发的人数：邮件不含跳过的；短信包含「因邮件失败而被扣住」的人
    /// （这样「邮件 4/5、短信 1/3」里的 3 与发送前预检的短信条数一致）。
    /// </summary>
    internal static StaffNotificationBatchDto Summarize(StaffNotificationBatch batch, IReadOnlyCollection<StaffNotificationRecipient> recipients)
    {
        static bool Planned(string status, string? error, bool isSms) =>
            status is StaffNotificationChannelStatuses.Pending or StaffNotificationChannelStatuses.Sending
                or StaffNotificationChannelStatuses.Sent or StaffNotificationChannelStatuses.Failed
            || (isSms && status == StaffNotificationChannelStatuses.Skipped && error == SmsHeldByEmailFailure);

        var emailRequested = recipients.Count(item => Planned(item.EmailStatus, item.EmailError, isSms: false));
        var emailSent = recipients.Count(item => item.EmailStatus == StaffNotificationChannelStatuses.Sent);
        var emailFailed = recipients.Count(item => item.EmailStatus == StaffNotificationChannelStatuses.Failed);
        var smsRequested = recipients.Count(item => Planned(item.SmsStatus, item.SmsError, isSms: true));
        var smsSent = recipients.Count(item => item.SmsStatus == StaffNotificationChannelStatuses.Sent);
        var smsFailed = recipients.Count(item => item.SmsStatus == StaffNotificationChannelStatuses.Failed);
        var segments = recipients.Where(item => item.SmsStatus == StaffNotificationChannelStatuses.Sent).Sum(item => item.SmsSegments);

        var finished = batch.Status is StaffNotificationBatchStatuses.Completed or StaffNotificationBatchStatuses.Interrupted;
        var failedTotal = emailFailed + smsFailed;
        var sentTotal = emailSent + smsSent;
        var result = !finished ? "running"
            : failedTotal == 0 ? "all_sent"
            : sentTotal == 0 ? "all_failed"
            : "partial_failed";

        return new StaffNotificationBatchDto
        {
            Id = batch.Id,
            TemplateKey = batch.TemplateKey,
            TemplateName = batch.TemplateName,
            CreatedBy = batch.CreatedBy,
            CreatedAtUtc = batch.CreatedAtUtc,
            FinishedAtUtc = batch.FinishedAtUtc,
            Status = batch.Status,
            Result = result,
            TotalCount = batch.TotalCount,
            EmailRequested = emailRequested,
            EmailSent = emailSent,
            EmailFailed = emailFailed,
            SmsRequested = smsRequested,
            SmsSent = smsSent,
            SmsFailed = smsFailed,
            SmsSegments = segments,
            SmsCostAud = Math.Round(segments * StaffNotificationRules.SmsUnitCostAud, 4),
            CanRetry = finished && failedTotal > 0,
        };
    }

    /// <summary>YYYY-MM-DD（悉尼日期）→ 半开 UTC 区间 [from, to + 1 天)。格式不对的一端当作没填。</summary>
    private static (DateTime? FromUtc, DateTime? ToUtc) ParseSydneyRange(string? from, string? to)
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney"); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.Utc; }

        DateTime? Convert(string? text, int addDays)
        {
            if (!DateTime.TryParseExact(text?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return null;
            var local = DateTime.SpecifyKind(date.AddDays(addDays), DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTimeToUtc(local, zone);
        }

        return (Convert(from, 0), Convert(to, 1));
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
