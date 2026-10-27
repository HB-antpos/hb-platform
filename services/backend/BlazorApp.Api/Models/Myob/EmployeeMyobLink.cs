using SqlSugar;

namespace BlazorApp.Api.Models.Myob;

/// <summary>
/// HBweb 库 dbo.EmployeeMyobLink：我方员工 ↔ 某个 MYOB 公司文件里的员工卡。
/// 软解绑：UnboundAt 非空即已解除；「当前有效」的绑定由迁移里的两个过滤唯一索引（UnboundAt IS NULL）保证：
/// 一个员工在一个公司文件里只有一张有效卡，一张卡也只绑一个员工。
/// 表由迁移 20261027.003-myob-employee-binding 创建，不进 SqlSugarContext 的 tableTypes。
/// </summary>
[SugarTable("EmployeeMyobLink")]
internal sealed class EmployeeMyobLink
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string LinkGuid { get; set; } = Guid.NewGuid().ToString();

    [SugarColumn(Length = 50)]
    public string UserGuid { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string ConnectionGuid { get; set; } = string.Empty;

    /// <summary>MYOB Contact/Employee 的 UID。</summary>
    [SugarColumn(Length = 50)]
    public string MyobEmployeeUid { get; set; } = string.Empty;

    /// <summary>绑定时的 MYOB 姓名快照（MYOB 里卡被删除 / 停用后仍可显示）。</summary>
    [SugarColumn(Length = 200, IsNullable = true)]
    public string? MyobName { get; set; }

    /// <summary>Suggested（确认高分建议）/ Manual（人工选卡）。</summary>
    [SugarColumn(Length = 20)]
    public string MatchMethod { get; set; } = "Manual";

    [SugarColumn(IsNullable = true)]
    public int? MatchScore { get; set; }

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? BoundByUserGuid { get; set; }

    public DateTime BoundAt { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? UnboundAt { get; set; }

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? UnboundByUserGuid { get; set; }
}
