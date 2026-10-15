using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 账号「不允许登录 Web / App」的标记：有记录即禁止登录，无记录即允许（老账号全部保持可登录，无需回填）。
/// 账号本身仍可用于考勤、排班、收银条码等不依赖登录的功能，与 [User].IsActive（停用账号）是两件事。
/// 独立建表而不给 [User] 加列：POS API 共用 User 实体查询同一张表，加列会让未迁移环境的 POS 登录失败。
/// 实体不进 SqlSugarContext 的自动建表清单，由版本号迁移创建。
/// </summary>
[SugarTable("UserLoginRestriction")]
public sealed class UserLoginRestriction
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string UserGUID { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false, ColumnDataType = "datetime2")]
    public DateTime RestrictedAtUtc { get; set; }

    [SugarColumn(IsNullable = true, Length = 100)]
    public string? RestrictedBy { get; set; }
}
