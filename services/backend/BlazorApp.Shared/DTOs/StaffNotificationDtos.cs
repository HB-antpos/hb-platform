namespace BlazorApp.Shared.DTOs;

/// <summary>员工通知模板。账号已开通是系统模板，其余是管理员自建的普通通知模板。</summary>
public sealed class StaffNotificationTemplateDto
{
    /// <summary>系统模板固定为 account-activated；自建模板为服务端生成的 GUID。</summary>
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>system / general。</summary>
    public string Kind { get; set; } = "general";
    public string Description { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }

    /// <summary>这个模板必须发邮件（账号已开通要把随机密码发到邮箱）。</summary>
    public bool RequiresEmail { get; set; }

    /// <summary>发送会重置密码并让旧登录失效。</summary>
    public bool IssuesPassword { get; set; }
    public string EmailSubject { get; set; } = string.Empty;
    public string EmailBody { get; set; } = string.Empty;
    public string SmsBody { get; set; } = string.Empty;

    /// <summary>系统模板是否被改过（决定「恢复默认文案」是否可点）。</summary>
    public bool IsCustomized { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public sealed class StaffNotificationTemplateSaveDto
{
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string EmailSubject { get; set; } = string.Empty;
    public string EmailBody { get; set; } = string.Empty;
    public string SmsBody { get; set; } = string.Empty;
}

public sealed class StaffNotificationPreflightRequestDto
{
    public string TemplateKey { get; set; } = string.Empty;
    public List<string> UserGuids { get; set; } = new();
    public bool SendEmail { get; set; } = true;
    public bool SendSms { get; set; }
}

public sealed class StaffNotificationPreflightRecipientDto
{
    public string UserGuid { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string LoginName { get; set; } = string.Empty;
    public string? StoreName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public bool IsActive { get; set; }
    public bool HasLoggedIn { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }

    /// <summary>ready / confirm / blocked。</summary>
    public string Status { get; set; } = "ready";
    public List<string> Reasons { get; set; } = new();
    public bool WillSendEmail { get; set; }
    public bool WillSendSms { get; set; }

    /// <summary>sms_off / sms_unavailable / no_phone / not_mobile；会发短信时为空。</summary>
    public string? SmsSkipReason { get; set; }
    public int SmsSegments { get; set; }
}

public sealed class StaffNotificationPreflightDto
{
    public bool SmsAvailable { get; set; }
    public int SmsUsedLast24h { get; set; }
    public int SmsDailyLimit { get; set; }
    public decimal SmsUnitCostAud { get; set; }
    public List<StaffNotificationPreflightRecipientDto> Recipients { get; set; } = new();
}

public sealed class StaffNotificationPreviewRequestDto
{
    public string TemplateKey { get; set; } = string.Empty;
    public string UserGuid { get; set; } = string.Empty;
}

public sealed class StaffNotificationPreviewDto
{
    public string EmailSubject { get; set; } = string.Empty;

    /// <summary>服务端渲染好的 HTML 片段，密码位置是圆点。前端放进 sandbox iframe 展示。</summary>
    public string EmailHtml { get; set; } = string.Empty;
    public string SmsText { get; set; } = string.Empty;
    public int SmsLength { get; set; }
    public int SmsSegments { get; set; }
    public bool SmsValid { get; set; }
    public string? SmsError { get; set; }
}

public sealed class StaffNotificationSendRequestDto
{
    public string TemplateKey { get; set; } = string.Empty;
    public List<string> UserGuids { get; set; } = new();
    public bool SendEmail { get; set; } = true;
    public bool SendSms { get; set; }

    /// <summary>操作人已确认：选中的人里有登录过的，发放密码会让他们的旧密码失效、所有设备被踢下线。</summary>
    public bool ConfirmReissue { get; set; }
}

public sealed class StaffNotificationRejectedDto
{
    public string UserGuid { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public sealed class StaffNotificationSendResultDto
{
    public string BatchId { get; set; } = string.Empty;
    public string Status { get; set; } = "queued";
    public int TotalCount { get; set; }
    public List<StaffNotificationRejectedDto> Rejected { get; set; } = new();
}

public class StaffNotificationBatchDto
{
    public string Id { get; set; } = string.Empty;
    public string TemplateKey { get; set; } = string.Empty;
    public string TemplateName { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>queued / running / completed / interrupted。</summary>
    public string Status { get; set; } = "queued";

    /// <summary>running / all_sent / partial_failed / all_failed。</summary>
    public string Result { get; set; } = "running";
    public int TotalCount { get; set; }
    public int EmailRequested { get; set; }
    public int EmailSent { get; set; }
    public int EmailFailed { get; set; }
    public int SmsRequested { get; set; }
    public int SmsSent { get; set; }
    public int SmsFailed { get; set; }

    /// <summary>实际发出的短信条数合计。</summary>
    public int SmsSegments { get; set; }
    public decimal SmsCostAud { get; set; }
    public bool CanRetry { get; set; }
}

public sealed class StaffNotificationRecipientResultDto
{
    public string Id { get; set; } = string.Empty;
    public string UserGuid { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? StoreName { get; set; }
    public string? EmailMask { get; set; }
    public string? PhoneMask { get; set; }

    /// <summary>none / skipped / pending / sent / failed。</summary>
    public string EmailStatus { get; set; } = "none";
    public string SmsStatus { get; set; } = "none";
    public string Note { get; set; } = string.Empty;
}

/// <summary>批次详情：批次字段与逐人结果平铺在同一层（与前端契约一致），记录页展开行、结果弹窗和轮询共用。</summary>
public sealed class StaffNotificationBatchDetailDto : StaffNotificationBatchDto
{
    public List<StaffNotificationRecipientResultDto> Recipients { get; set; } = new();
}

public sealed class StaffNotificationBatchQueryDto
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public string? TemplateKey { get; set; }

    /// <summary>running / all_sent / partial_failed / all_failed。</summary>
    public string? Result { get; set; }

    /// <summary>email / sms：只看用了这个渠道的批次。</summary>
    public string? Channel { get; set; }

    /// <summary>匹配操作人登录名或收件员工姓名。</summary>
    public string? Keyword { get; set; }

    /// <summary>YYYY-MM-DD，悉尼日期。</summary>
    public string? From { get; set; }
    public string? To { get; set; }
}
