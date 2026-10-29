using SqlSugar;

namespace BlazorApp.Api.Models.Myob;

/// <summary>
/// HBweb 库 dbo.MyobEmployeeSyncBatch：一次「从 MYOB 同步资料到我方」写入的批次与撤销快照。
/// ChangesJson 是 <c>List&lt;SyncSnapshotItem&gt;</c>（每项：员工、字段、写入前的我方值、写入后的值），7 天内可整批撤销。
/// 含银行账号等敏感原值，只在后端读写，不会出现在任何接口响应里。
/// 表由迁移 20261029.001-myob-employee-sync-batch 创建，不进 SqlSugarContext 的 tableTypes。
/// </summary>
[SugarTable("MyobEmployeeSyncBatch")]
internal sealed class MyobEmployeeSyncBatch
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string BatchGuid { get; set; } = Guid.NewGuid().ToString();

    [SugarColumn(Length = 50)]
    public string ConnectionGuid { get; set; } = string.Empty;

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? ActorUserGuid { get; set; }

    public DateTime CreatedAt { get; set; }

    public int EmployeeCount { get; set; }

    public int ChangeCount { get; set; }

    public int SensitiveCount { get; set; }

    public string ChangesJson { get; set; } = "[]";

    [SugarColumn(IsNullable = true)]
    public DateTime? UndoneAt { get; set; }

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? UndoneByUserGuid { get; set; }
}
