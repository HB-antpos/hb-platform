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

    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string UserGUID { get; set; } = string.Empty;

    /// <summary>created＝店长新建账号；reset＝店长或管理员重置密码。</summary>
    [SugarColumn(IsNullable = false, Length = 32)]
    public string Reason { get; set; } = ReasonCreated;

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime RequiredAtUtc { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? RequiredBy { get; set; }
}
