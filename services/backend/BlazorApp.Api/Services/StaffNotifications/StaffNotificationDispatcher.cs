using BlazorApp.Api.Data;
using BlazorApp.Api.Models;
using BlazorApp.Api.Services.Background;
using BlazorApp.Api.Services.Sms;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Options;

namespace BlazorApp.Api.Services.StaffNotifications;

/// <summary>入队后叫醒后台任务：避免等下一个轮询节拍，用户点了发送几乎立刻开始处理。</summary>
internal interface IStaffNotificationSignal
{
    void Notify();
}

internal sealed class StaffNotificationSignal : IStaffNotificationSignal
{
    // 容量 1：连续多次 Notify 只算一次唤醒，任务醒来后会一次处理完所有排队批次。
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Notify()
    {
        try { _semaphore.Release(); }
        catch (SemaphoreFullException) { /* 已经有一次唤醒在等了 */ }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        _semaphore.WaitAsync(timeout, cancellationToken);
}

/// <summary>
/// 后台逐人发送。一个批次的处理顺序：领取批次（queued → running）→ 对每个还有待发渠道的员工：
/// 先发邮件（账号已开通走「写密码 → 发信 → 失败回滚」的密码通道）→ 邮件发出后才发短信 → 把结果写回这一行。
/// 每个渠道开发前先把状态改成 sending（领取），崩溃重启后 sending 的行会被判为「结果未知」，
/// 绝不会自动再发一次：重发意味着再重置一次密码，必须由操作人点「重试」。
/// 只存打码邮箱 / 手机号与状态；邮件正文和随机密码不进数据库、不进日志。
/// </summary>
internal sealed class StaffNotificationDispatcher
{
    private readonly SqlSugarContext _context;
    private readonly IPasswordResetService _passwordReset;
    private readonly IAccountPasswordEmailSender _emailSender;
    private readonly ISmsSender _smsSender;
    private readonly IStaffNotificationTemplateService _templates;
    private readonly IStaffNotificationLinkProvider _links;
    private readonly ILogger<StaffNotificationDispatcher> _logger;

    public StaffNotificationDispatcher(
        SqlSugarContext context,
        IPasswordResetService passwordReset,
        IAccountPasswordEmailSender emailSender,
        ISmsSender smsSender,
        IStaffNotificationTemplateService templates,
        IStaffNotificationLinkProvider links,
        ILogger<StaffNotificationDispatcher> logger
    )
    {
        _context = context;
        _passwordReset = passwordReset;
        _emailSender = emailSender;
        _smsSender = smsSender;
        _templates = templates;
        _links = links;
        _logger = logger;
    }

    /// <summary>
    /// 进程启动时调用一次：上个进程退出时正在发送的行改成失败（结果未知，提示核对后重试），
    /// 「运行中」的批次放回队列，继续处理还没领取的行。
    /// </summary>
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        var db = _context.Db;
        var stuck = await db.Queryable<StaffNotificationRecipient>()
            .Where(item => item.EmailStatus == StaffNotificationChannelStatuses.Sending || item.SmsStatus == StaffNotificationChannelStatuses.Sending)
            .ToListAsync();
        var now = DateTime.UtcNow;
        foreach (var row in stuck)
        {
            if (row.EmailStatus == StaffNotificationChannelStatuses.Sending)
            {
                row.EmailStatus = StaffNotificationChannelStatuses.Failed;
                row.EmailError = "INTERRUPTED";
            }
            if (row.SmsStatus == StaffNotificationChannelStatuses.Sending)
            {
                row.SmsStatus = StaffNotificationChannelStatuses.Failed;
                row.SmsError = "INTERRUPTED";
            }
            row.Note = "发送被服务重启打断，结果未知（可能已发出），请核对后再重试";
            row.UpdatedAtUtc = now;
            await db.Updateable(row).ExecuteCommandAsync();
        }

        await db.Updateable<StaffNotificationBatch>()
            .SetColumns(item => new StaffNotificationBatch { Status = StaffNotificationBatchStatuses.Queued })
            .Where(item => item.Status == StaffNotificationBatchStatuses.Running)
            .ExecuteCommandAsync();

        if (stuck.Count > 0)
        {
            _logger.LogWarning("员工通知启动恢复：{Count} 个收件人上次发送被打断，已标记为失败等待人工重试", stuck.Count);
        }
    }

    /// <summary>处理所有排队中的批次，返回处理的批次数。</summary>
    public async Task<int> RunPendingAsync(CancellationToken cancellationToken)
    {
        var processed = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var next = await _context.Db.Queryable<StaffNotificationBatch>()
                .Where(item => item.Status == StaffNotificationBatchStatuses.Queued)
                .OrderBy(item => item.CreatedAtUtc)
                .FirstAsync();
            if (next is null) break;

            await ProcessBatchAsync(next.Id, cancellationToken);
            processed++;
        }

        return processed;
    }

    internal async Task ProcessBatchAsync(string batchId, CancellationToken cancellationToken)
    {
        var db = _context.Db;
        var startedAt = DateTime.UtcNow;

        // 领取批次：只有 queued → running 成功的那一次才处理，避免两个节拍重复处理同一批。
        var claimed = await db.Updateable<StaffNotificationBatch>()
            .SetColumns(item => new StaffNotificationBatch { Status = StaffNotificationBatchStatuses.Running, StartedAtUtc = startedAt })
            .Where(item => item.Id == batchId && item.Status == StaffNotificationBatchStatuses.Queued)
            .ExecuteCommandAsync();
        if (claimed == 0) return;

        var batch = await db.Queryable<StaffNotificationBatch>().FirstAsync(item => item.Id == batchId);
        var template = await _templates.GetAsync(batch.TemplateKey);
        var links = await _links.GetAsync(cancellationToken);

        var pending = await db.Queryable<StaffNotificationRecipient>()
            .Where(item => item.BatchId == batchId
                && (item.EmailStatus == StaffNotificationChannelStatuses.Pending || item.SmsStatus == StaffNotificationChannelStatuses.Pending))
            .OrderBy(item => item.SortOrder)
            .ToListAsync();

        foreach (var recipient in pending)
        {
            if (cancellationToken.IsCancellationRequested) return; // 服务停止：留着 running，下次启动由 RecoverAsync 放回队列

            try
            {
                await ProcessRecipientAsync(batch, template, links, recipient, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // 单个员工出错不能拖垮整批；日志只记异常类型，不记收件人与正文。
                _logger.LogError("员工通知处理单个收件人失败，批次：{BatchId}，异常类型：{ExceptionType}", batchId, ex.GetType().Name);
                await MarkFailedAsync(recipient, "EXCEPTION", "发送出错，请稍后重试");
            }
        }

        var finishedAt = DateTime.UtcNow;
        await db.Updateable<StaffNotificationBatch>()
            .SetColumns(item => new StaffNotificationBatch { Status = StaffNotificationBatchStatuses.Completed, FinishedAtUtc = finishedAt })
            .Where(item => item.Id == batchId)
            .ExecuteCommandAsync();
        _logger.LogInformation("员工通知批次处理完成，批次：{BatchId}，人数：{Count}", batchId, pending.Count);
    }

    private async Task ProcessRecipientAsync(
        StaffNotificationBatch batch,
        StaffNotificationTemplateContent? template,
        StaffNotificationLinks links,
        StaffNotificationRecipient recipient,
        CancellationToken cancellationToken
    )
    {
        var db = _context.Db;
        var notes = new List<string>();

        if (template is null)
        {
            await MarkFailedAsync(recipient, "TEMPLATE_MISSING", "模板已被删除，无法发送");
            return;
        }

        var user = await db.Queryable<User>().FirstAsync(item => item.UserGUID == recipient.UserGuid && !item.IsDeleted);
        if (user is null || !user.IsActive || await UserLoginRestrictions.IsRestrictedAsync(db, recipient.UserGuid))
        {
            // 排队之后账号被停用 / 关闭登录：不再给他发（尤其不能再给停用账号重置密码）。
            await MarkFailedAsync(recipient, "ACCOUNT_UNAVAILABLE", "账号已停用、关闭登录或不存在，未发送");
            return;
        }

        var displayName = string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName!;

        // ── 邮件 ──
        if (recipient.EmailStatus == StaffNotificationChannelStatuses.Pending && await TryClaimAsync(recipient, email: true))
        {
            var outcome = await SendEmailAsync(batch, template, links, recipient, user, displayName, cancellationToken);
            recipient.EmailStatus = outcome.Sent ? StaffNotificationChannelStatuses.Sent : StaffNotificationChannelStatuses.Failed;
            recipient.EmailError = outcome.Sent ? null : Truncate(outcome.ErrorCode, 200);
            if (!outcome.Sent) notes.Add(outcome.Note);
            await SaveAsync(recipient, notes);
        }

        // ── 短信 ──
        if (recipient.SmsStatus == StaffNotificationChannelStatuses.Pending && await TryClaimAsync(recipient, email: false))
        {
            if (template.IssuesPassword && recipient.EmailStatus != StaffNotificationChannelStatuses.Sent)
            {
                // 短信里写着「密码已发到邮箱」，邮件没发出时发这条短信会误导员工；重试邮件时会一起恢复。
                recipient.SmsStatus = StaffNotificationChannelStatuses.Skipped;
                recipient.SmsError = StaffNotificationService.SmsHeldByEmailFailure;
                notes.Add("邮件没有发出，短信也不发");
            }
            else
            {
                var outcome = await SendSmsAsync(batch, template, links, recipient, user, displayName, cancellationToken);
                recipient.SmsStatus = outcome.Sent ? StaffNotificationChannelStatuses.Sent : StaffNotificationChannelStatuses.Failed;
                recipient.SmsError = outcome.Sent ? null : Truncate(outcome.ErrorCode, 200);
                recipient.SmsSegments = outcome.Segments;
                if (!outcome.Sent) notes.Add(outcome.Note);
            }

            await SaveAsync(recipient, notes);
        }
    }

    private sealed record ChannelOutcome(bool Sent, string ErrorCode, string Note, int Segments = 0);

    private async Task<ChannelOutcome> SendEmailAsync(
        StaffNotificationBatch batch,
        StaffNotificationTemplateContent template,
        StaffNotificationLinks links,
        StaffNotificationRecipient recipient,
        User user,
        string displayName,
        CancellationToken cancellationToken
    )
    {
        if (!PasswordResetService.IsDeliverableEmail(user.Email))
        {
            return new ChannelOutcome(false, "NO_EMAIL", "没有可用邮箱，邮件未发");
        }

        ApiResponse<bool> result;
        if (template.IssuesPassword)
        {
            // 走与「新建员工 / 开通登录」同一条密码通道：新密码先落库并打首次改密标记，发信失败会恢复原密码。
            var send = await _passwordReset.SendTemplatedPasswordAsync(
                user.UserGUID,
                batch.CreatedBy,
                mail => ToRendered(StaffNotificationRenderer.RenderEmail(
                    template.EmailSubject,
                    template.EmailBody,
                    new StaffNotificationRenderContext(displayName, mail.ToEmail, recipient.StoreName ?? string.Empty, links.AppLink, links.IosUrl, links.AndroidUrl),
                    mail.Password
                ))
            );
            result = send.Success ? ApiResponse<bool>.OK(true) : ApiResponse<bool>.Error(send.Message, send.ErrorCode);
        }
        else
        {
            var content = StaffNotificationRenderer.RenderEmail(
                template.EmailSubject,
                template.EmailBody,
                new StaffNotificationRenderContext(displayName, user.Email, recipient.StoreName ?? string.Empty, links.AppLink, links.IosUrl, links.AndroidUrl),
                password: null
            );
            result = await _emailSender.SendRenderedAsync(user.Email, content.Subject, content.Text, content.Html, "staff-notification", cancellationToken);
        }

        if (result.Success) return new ChannelOutcome(true, string.Empty, string.Empty);

        var note = template.IssuesPassword ? $"{result.Message}；密码没有改动" : result.Message;
        return new ChannelOutcome(false, result.ErrorCode ?? AccountPasswordEmailSender.AccountEmailSendFailedCode, note);
    }

    private async Task<ChannelOutcome> SendSmsAsync(
        StaffNotificationBatch batch,
        StaffNotificationTemplateContent template,
        StaffNotificationLinks links,
        StaffNotificationRecipient recipient,
        User user,
        string displayName,
        CancellationToken cancellationToken
    )
    {
        var rawPhone = (await _context.Db.Queryable<EmployeeProfile>()
                .Where(item => item.UserGUID == user.UserGUID && !item.IsDeleted)
                .ToListAsync())
            .Select(item => item.Phone)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (!AustralianPhoneNumber.TryNormalize(rawPhone, out var e164))
        {
            return new ChannelOutcome(false, SmsErrorCodes.InvalidPhone, "手机号已变化或不是有效的澳洲手机号，短信未发");
        }

        var body = StaffNotificationRenderer.RenderSms(
            template.SmsBody,
            new StaffNotificationRenderContext(displayName, user.Email, recipient.StoreName ?? string.Empty, links.AppLink, links.IosUrl, links.AndroidUrl)
        );
        // 发件名取批次上记录的那个（发送时选的，重试沿用）；历史批次为空 = 默认发件名。
        var result = await _smsSender.SendAsync(new SmsRequest(SmsPurposes.StaffNotification, e164, body, batch.Id, batch.SmsSenderId), cancellationToken);
        return result.Success
            ? new ChannelOutcome(true, string.Empty, string.Empty, SmsMessages.SegmentCount(body))
            : new ChannelOutcome(false, result.ErrorCode ?? SmsErrorCodes.SendFailed, result.Message);
    }

    /// <summary>领取一个渠道：pending → sending。0 行说明状态已变（被别处处理），这次就不发。</summary>
    private async Task<bool> TryClaimAsync(StaffNotificationRecipient recipient, bool email)
    {
        var db = _context.Db;
        var now = DateTime.UtcNow;
        var id = recipient.Id;
        var affected = email
            ? await db.Updateable<StaffNotificationRecipient>()
                .SetColumns(item => new StaffNotificationRecipient { EmailStatus = StaffNotificationChannelStatuses.Sending, UpdatedAtUtc = now })
                .Where(item => item.Id == id && item.EmailStatus == StaffNotificationChannelStatuses.Pending)
                .ExecuteCommandAsync()
            : await db.Updateable<StaffNotificationRecipient>()
                .SetColumns(item => new StaffNotificationRecipient { SmsStatus = StaffNotificationChannelStatuses.Sending, UpdatedAtUtc = now })
                .Where(item => item.Id == id && item.SmsStatus == StaffNotificationChannelStatuses.Pending)
                .ExecuteCommandAsync();
        if (affected == 0) return false;

        if (email) recipient.EmailStatus = StaffNotificationChannelStatuses.Sending;
        else recipient.SmsStatus = StaffNotificationChannelStatuses.Sending;
        return true;
    }

    private async Task SaveAsync(StaffNotificationRecipient recipient, List<string> notes)
    {
        recipient.Note = Truncate(string.Join("；", notes), 300);
        recipient.UpdatedAtUtc = DateTime.UtcNow;
        await _context.Db.Updateable(recipient).ExecuteCommandAsync();
    }

    /// <summary>把所有还没出结果的渠道标成失败（账号不可用、模板丢失、意外异常）。</summary>
    private async Task MarkFailedAsync(StaffNotificationRecipient recipient, string errorCode, string note)
    {
        if (recipient.EmailStatus is StaffNotificationChannelStatuses.Pending or StaffNotificationChannelStatuses.Sending)
        {
            recipient.EmailStatus = StaffNotificationChannelStatuses.Failed;
            recipient.EmailError = errorCode;
        }
        if (recipient.SmsStatus is StaffNotificationChannelStatuses.Pending or StaffNotificationChannelStatuses.Sending)
        {
            recipient.SmsStatus = StaffNotificationChannelStatuses.Failed;
            recipient.SmsError = errorCode;
        }

        recipient.Note = Truncate(note, 300);
        recipient.UpdatedAtUtc = DateTime.UtcNow;
        await _context.Db.Updateable(recipient).ExecuteCommandAsync();
    }

    private static RenderedEmailContent ToRendered(EmailContent content) => new(content.Subject, content.Text, content.Html);

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}

/// <summary>
/// 员工通知后台任务：等入队信号（最多等 20 秒兜底轮询）→ 处理所有排队批次。
/// 与 <c>ExpoPushBackgroundService</c> 同一套约定：<c>ScheduledTasks:Enabled=false</c> 的进程（例如连着生产库的本机开发实例）
/// 不参与处理，避免本机实例把生产排队的批次领走、用本机网络发信；节拍里的异常按「同类 10 分钟最多记一条」限频，
/// 迁移没执行（表不存在）时不会刷屏，也不会让任务退出。
/// </summary>
internal sealed class StaffNotificationBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly StaffNotificationSignal _signal;
    private readonly IOptions<ScheduledTaskOptions> _scheduledTaskOptions;
    private readonly ILogger<StaffNotificationBackgroundService> _logger;
    private bool _recovered;
    private DateTime _lastFailureLoggedAtUtc = DateTime.MinValue;

    public StaffNotificationBackgroundService(
        IServiceScopeFactory scopeFactory,
        StaffNotificationSignal signal,
        IOptions<ScheduledTaskOptions> scheduledTaskOptions,
        ILogger<StaffNotificationBackgroundService> logger
    )
    {
        _scopeFactory = scopeFactory;
        _signal = signal;
        _scheduledTaskOptions = scheduledTaskOptions;
        _logger = logger;
    }

    /// <summary>启动后的等待时间，避开启动迁移与缓存预热的高峰；测试可缩短。</summary>
    internal TimeSpan StartupDelay { get; init; } = TimeSpan.FromSeconds(15);

    internal TimeSpan IdleWait { get; init; } = TimeSpan.FromSeconds(20);

    internal static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan FailureLogInterval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_scheduledTaskOptions.Value.Enabled)
        {
            _logger.LogInformation("员工通知后台任务已跳过：本进程未参与定时任务调度（ScheduledTasks:Enabled=false）");
            return;
        }

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var wait = await TickAsync(stoppingToken);
                await _signal.WaitAsync(wait, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 服务停止
        }
    }

    /// <summary>跑一个节拍并返回下次最长等待时间。公开给测试直接驱动。</summary>
    internal async Task<TimeSpan> TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<StaffNotificationDispatcher>();
            if (!_recovered)
            {
                await dispatcher.RecoverAsync(cancellationToken);
                _recovered = true;
            }

            await dispatcher.RunPendingAsync(cancellationToken);
            return IdleWait;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return IdleWait;
        }
        catch (Exception ex)
        {
            // 领取批次之后的任何异常（数据库超时、死锁、断连）都可能让批次停在 running、行停在 sending；
            // 这些状态只有 RecoverAsync 会放回队列，所以下个节拍必须重新做一次恢复，否则要重启 API 才能解开。
            // 恢复是幂等的（只处理 running 批次与 sending 行），后台任务只有一个 worker，重复执行是安全的。
            _recovered = false;

            var now = DateTime.UtcNow;
            if (now - _lastFailureLoggedAtUtc >= FailureLogInterval)
            {
                _lastFailureLoggedAtUtc = now;
                // 只记异常类型：迁移没执行时是 SqlException，不需要把 SQL 与表名刷进日志。
                _logger.LogWarning("员工通知后台任务节拍失败，异常类型：{ExceptionType}；请确认已执行迁移 20261106.001-staff-notification", ex.GetType().Name);
            }

            return FailureBackoff;
        }
    }
}
