using SqlSugar;

namespace BlazorApp.Api.Models.Myob;

/// <summary>
/// HBweb 库 dbo.MyobStoreMapping 的实体：分店 → MYOB 连接（分店所属公司，即默认发薪公司）。
/// StoreCode 为主键：一个分店只对应一个连接，同一分店的工时不会拆给两家公司。
/// 表由迁移 20261027.002-myob-connection 创建，刻意不登记进 SqlSugarContext 的 tableTypes。
/// </summary>
[SugarTable("MyobStoreMapping")]
internal sealed class MyobStoreMapping
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string StoreCode { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string ConnectionGuid { get; set; } = string.Empty;

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? MappedBy { get; set; }

    public DateTime MappedAt { get; set; }
}
