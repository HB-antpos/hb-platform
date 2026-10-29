using SqlSugar;

namespace BlazorApp.Api.Models.Myob;

/// <summary>
/// HBweb 库 dbo.MyobEmployeeIgnore：「MYOB 有、我方没有」名单里被人工忽略的员工卡（如已离职未停用、别家公司的人）。
/// (ConnectionGuid, MyobEmployeeUid) 联合主键；取消忽略就是删行。
/// 表由迁移 20261027.003-myob-employee-binding 创建，不进 SqlSugarContext 的 tableTypes。
/// </summary>
[SugarTable("MyobEmployeeIgnore")]
internal sealed class MyobEmployeeIgnore
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string ConnectionGuid { get; set; } = string.Empty;

    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string MyobEmployeeUid { get; set; } = string.Empty;

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? IgnoredByUserGuid { get; set; }

    public DateTime IgnoredAt { get; set; }
}
