using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 账号须先修改密码才能继续使用的标记：有记录即需要改密，员工改密成功后删除。
/// 独立建表而不给 [User] 加列：POS API 共用 User 实体查询同一张表，加列会让未迁移环境的 POS 登录失败。
/// 实体不进 SqlSugarContext 的自动建表清单，由版本号迁移创建。
/// </summary>
[SugarTable("UserPasswordChangeRequirement")]
public sealed class UserPasswordChangeRequirement
{
    public const string ReasonCreated = "created";
    public const string ReasonReset = "reset";

    /// <summary>
    /// 系统生成并邮件发出的随机临时密码（新建 / 开通登录）。这类密码只有 4 位、强度低，超过有效期仍未使用就失效；
    /// 与店长手输密码的 created / reset 分开记录，后者不受有效期限制。不加列是因为加列需要显式迁移，
    /// 代码先于迁移上线会让登录路径直接报错。
    /// </summary>
    public const string ReasonCreatedEmailed = "created-emailed";

    /// <summary>系统生成并邮件发出的随机临时密码（店长重置），失效规则同 <see cref="ReasonCreatedEmailed"/>。</summary>
    public const string ReasonResetEmailed = "reset-emailed";

    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string UserGUID { get; set; } = string.Empty;

    /// <summary>
    /// 为什么要求改密：created＝新建账号；reset＝店长或管理员重置密码；
    /// 带 -emailed 后缀表示密码是系统生成并邮件发出的临时密码，受有效期限制。
    /// </summary>
    [SugarColumn(IsNullable = false, Length = 32)]
    public string Reason { get; set; } = ReasonCreated;

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime RequiredAtUtc { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? RequiredBy { get; set; }
}
