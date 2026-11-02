using SqlSugar;

namespace BlazorApp.Api.Models;

/// <summary>
/// HBweb 库 dbo.SmsSendLog：短信发送记录，每次尝试发送一行，用于按手机号 / 全站限流和排查花费。
/// 不存手机号明文、也不存短信正文（正文里有验证码与签署链接）：号码只存带密钥的 HMAC 哈希 + 打码号码。
/// 表由迁移 20261030.003-sms-send-log 创建，不进 SqlSugarContext 的 tableTypes（启动不得代建表）。
/// </summary>
[SugarTable("SmsSendLog")]
internal sealed class SmsSendLog
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary><see cref="BlazorApp.Api.Services.Sms.SmsPurposes"/>。</summary>
    [SugarColumn(Length = 40)]
    public string Purpose { get; set; } = string.Empty;

    /// <summary>HMAC-SHA256(LogHashKey, E.164 号码) 的小写十六进制。</summary>
    [SugarColumn(Length = 64)]
    public string PhoneHash { get; set; } = string.Empty;

    /// <summary>打码号码，如 +614*****678。</summary>
    [SugarColumn(Length = 20)]
    public string PhoneMask { get; set; } = string.Empty;

    /// <summary>计费条数（按 160 / 153 字符估算）。</summary>
    public int Segments { get; set; }

    /// <summary><see cref="SmsSendStatuses"/>。</summary>
    [SugarColumn(Length = 20)]
    public string Status { get; set; } = SmsSendStatuses.Sending;

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? ProviderMessageId { get; set; }

    [SugarColumn(Length = 80, IsNullable = true)]
    public string? ErrorCode { get; set; }

    /// <summary>关联的业务主键（只放非个人信息）。</summary>
    [SugarColumn(Length = 100, IsNullable = true)]
    public string? RelatedKey { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [SugarColumn(IsNullable = true)]
    public DateTime? FinishedAtUtc { get; set; }
}

internal static class SmsSendStatuses
{
    public const string Sending = "Sending";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
}
