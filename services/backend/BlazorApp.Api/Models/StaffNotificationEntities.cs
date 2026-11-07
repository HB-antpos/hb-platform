using SqlSugar;

namespace BlazorApp.Api.Models;

/// <summary>
/// HBweb 库 dbo.StaffNotificationTemplate：员工通知模板。
/// 系统模板（账号已开通）的默认文案写在代码里，只有被管理员改过才会有一行；自建模板每个一行。
/// 表由迁移 20261106.001-staff-notification 创建，不进 SqlSugarContext 的 tableTypes（启动不得代建表）。
/// </summary>
[SugarTable("StaffNotificationTemplate")]
internal sealed class StaffNotificationTemplate
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>模板键：系统模板是固定字符串（如 account-activated），自建模板是 GUID。唯一。</summary>
    [SugarColumn(Length = 60)]
    public string TemplateKey { get; set; } = string.Empty;

    [SugarColumn(Length = 100)]
    public string Name { get; set; } = string.Empty;

    /// <summary><see cref="StaffNotificationTemplateKinds"/>。</summary>
    [SugarColumn(Length = 20)]
    public string Kind { get; set; } = StaffNotificationTemplateKinds.General;

    public bool IsEnabled { get; set; } = true;

    [SugarColumn(Length = 200)]
    public string EmailSubject { get; set; } = string.Empty;

    [SugarColumn(Length = 4000)]
    public string EmailBody { get; set; } = string.Empty;

    [SugarColumn(Length = 500)]
    public string SmsBody { get; set; } = string.Empty;

    [SugarColumn(Length = 100)]
    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [SugarColumn(Length = 100)]
    public string UpdatedBy { get; set; } = string.Empty;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// HBweb 库 dbo.StaffNotificationBatch：一次发送一个批次，后台任务按批次逐人发送。
/// 表由迁移 20261106.001-staff-notification 创建，不进 SqlSugarContext 的 tableTypes。
/// </summary>
[SugarTable("StaffNotificationBatch")]
internal sealed class StaffNotificationBatch
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [SugarColumn(Length = 60)]
    public string TemplateKey { get; set; } = string.Empty;

    /// <summary>发送当时的模板名称快照，模板之后被改名也不影响记录。</summary>
    [SugarColumn(Length = 100)]
    public string TemplateName { get; set; } = string.Empty;

    /// <summary>操作人登录名。</summary>
    [SugarColumn(Length = 100)]
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>操作人 UserGUID：非管理员只能看到自己发出的批次，靠它过滤。</summary>
    [SugarColumn(Length = 50)]
    public string CreatedByUserGuid { get; set; } = string.Empty;

    public bool SendEmail { get; set; }

    public bool SendSms { get; set; }

    /// <summary>
    /// 这批短信用的发件名（发送时从白名单里选的，「重试失败项」沿用同一个）。
    /// 没发短信的批次、以及迁移 20261107.001 之前的历史批次为空，发送时按默认发件名处理。
    /// </summary>
    [SugarColumn(Length = 32, IsNullable = true)]
    public string? SmsSenderId { get; set; }

    /// <summary><see cref="StaffNotificationBatchStatuses"/>。</summary>
    [SugarColumn(Length = 20)]
    public string Status { get; set; } = StaffNotificationBatchStatuses.Queued;

    public int TotalCount { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [SugarColumn(IsNullable = true)]
    public DateTime? StartedAtUtc { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? FinishedAtUtc { get; set; }
}

/// <summary>
/// HBweb 库 dbo.StaffNotificationRecipient：批次里每个员工一行。
/// 只存姓名、打码邮箱 / 手机号和两个渠道的状态；不存邮箱 / 手机号明文，也不存邮件正文与随机密码。
/// 真正发送时再按 UserGuid 读取员工当前的邮箱与手机号。
/// 表由迁移 20261106.001-staff-notification 创建，不进 SqlSugarContext 的 tableTypes。
/// </summary>
[SugarTable("StaffNotificationRecipient")]
internal sealed class StaffNotificationRecipient
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [SugarColumn(Length = 50)]
    public string BatchId { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string UserGuid { get; set; } = string.Empty;

    [SugarColumn(Length = 200)]
    public string DisplayName { get; set; } = string.Empty;

    [SugarColumn(Length = 200, IsNullable = true)]
    public string? StoreName { get; set; }

    /// <summary>打码邮箱，如 m***@example.com。</summary>
    [SugarColumn(Length = 200, IsNullable = true)]
    public string? EmailMask { get; set; }

    /// <summary>打码手机号，如 +614*****156。</summary>
    [SugarColumn(Length = 20, IsNullable = true)]
    public string? PhoneMask { get; set; }

    /// <summary><see cref="StaffNotificationChannelStatuses"/>。</summary>
    [SugarColumn(Length = 20)]
    public string EmailStatus { get; set; } = StaffNotificationChannelStatuses.None;

    [SugarColumn(Length = 200, IsNullable = true)]
    public string? EmailError { get; set; }

    [SugarColumn(Length = 20)]
    public string SmsStatus { get; set; } = StaffNotificationChannelStatuses.None;

    [SugarColumn(Length = 200, IsNullable = true)]
    public string? SmsError { get; set; }

    /// <summary>实际发出的短信计费条数（没发出为 0）。</summary>
    public int SmsSegments { get; set; }

    /// <summary>失败 / 跳过原因（中文，给操作人看），成功为空串。</summary>
    [SugarColumn(Length = 300)]
    public string Note { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

internal static class StaffNotificationTemplateKinds
{
    /// <summary>系统模板：不可删除，文案可改；账号已开通会发放随机密码。</summary>
    public const string System = "system";

    /// <summary>自建模板：普通通知，不能发放密码。</summary>
    public const string General = "general";
}

internal static class StaffNotificationBatchStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";

    /// <summary>
    /// 预留：当前实现里被服务重启打断的批次会放回 queued 继续处理（已领取未出结果的行标成失败等人工重试），
    /// 不会进入这个状态；保留它是为了与前端契约一致，轮询逻辑把 completed / interrupted 都当作「已结束」。
    /// </summary>
    public const string Interrupted = "interrupted";
}

internal static class StaffNotificationChannelStatuses
{
    /// <summary>本批次没勾这个渠道。</summary>
    public const string None = "none";

    /// <summary>勾了但这个人发不了（没手机号、邮件没发出所以短信也不发等）。</summary>
    public const string Skipped = "skipped";

    public const string Pending = "pending";

    /// <summary>已领取、正在发送。对外按 pending 展示；进程重启后把它判为中断。</summary>
    public const string Sending = "sending";

    public const string Sent = "sent";
    public const string Failed = "failed";
}
