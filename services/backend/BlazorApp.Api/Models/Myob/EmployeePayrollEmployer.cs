using SqlSugar;

namespace BlazorApp.Api.Models.Myob;

/// <summary>
/// HBweb 库 dbo.EmployeePayrollEmployer：发薪公司覆盖，按「员工 × 上班分店」，按生效日期留历史、只新增不改。
/// 解析规则：取该员工该分店 EffectiveFrom ≤ 日期 的最新一行；ConnectionGuid 非空 → 用它；为空（改回按分店）或没有行 → 用分店所属公司。
/// EffectiveFrom 必须是工资周期起始日（保存时校验），避免周期中途把同一分店的工时拆给两家公司。
/// 表由迁移 20261027.003-myob-employee-binding 创建，不进 SqlSugarContext 的 tableTypes。
/// </summary>
[SugarTable("EmployeePayrollEmployer")]
internal sealed class EmployeePayrollEmployer
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string EmployerGuid { get; set; } = Guid.NewGuid().ToString();

    [SugarColumn(Length = 50)]
    public string UserGuid { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    /// <summary>覆盖到的公司文件；null＝改回按分店所属公司。</summary>
    [SugarColumn(Length = 50, IsNullable = true)]
    public string? ConnectionGuid { get; set; }

    /// <summary>门店本地日期，没有时分。</summary>
    [SugarColumn(ColumnDataType = "date")]
    public DateTime EffectiveFrom { get; set; }

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? CreatedByUserGuid { get; set; }

    public DateTime CreatedAt { get; set; }
}
