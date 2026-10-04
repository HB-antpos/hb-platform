using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 邮箱验证码设置 / 找回密码。验证码只存哈希；同一账号同时只有一个有效验证码，发新码即作废旧码。
/// 实体不进 SqlSugarContext 的自动建表清单，由版本号迁移创建。
/// </summary>
[SugarTable("UserPasswordResetCode")]
public sealed class UserPasswordResetCode
{
    /// <summary>店长新建账号或店长重置：员工可能稍后才看邮件，有效期较长。</summary>
    public const string PurposeInvite = "invite";
    /// <summary>员工在登录页自助找回：短有效期。</summary>
    public const string PurposeReset = "reset";

    [SugarColumn(IsPrimaryKey = true, Length = 36)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [SugarColumn(IsNullable = false, Length = 50)]
    public string UserGUID { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, Length = 16)]
    public string Purpose { get; set; } = PurposeReset;

    /// <summary>SHA256(Id:验证码) 的十六进制。</summary>
    [SugarColumn(IsNullable = false, Length = 64)]
    public string CodeHash { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime ExpiresAtUtc { get; set; }

    public int FailedAttempts { get; set; }

    /// <summary>已使用或已被新验证码作废的时间；非空即失效。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "datetime2")]
    public DateTime? ConsumedAtUtc { get; set; }

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>self＝员工自助；其他为发起的店长 / 管理员用户名。</summary>
    [SugarColumn(IsNullable = true, Length = 100)]
    public string? RequestedBy { get; set; }

    [SugarColumn(IsNullable = true, Length = 64)]
    public string? RequestIp { get; set; }
}
