using SqlSugar;

namespace BlazorApp.Api.Models.Myob;

/// <summary>
/// HBweb 库 dbo.MyobPayslipSyncState：每个公司文件一行，记工资单同步到哪了、最近一次是否成功、谁正在同步。
/// 表由迁移 20261117.001-myob-payslip-snapshot 创建，不进 SqlSugarContext 的 tableTypes。
/// </summary>
[SugarTable("MyobPayslipSyncState")]
internal sealed class MyobPayslipSyncState
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string ConnectionGuid { get; set; } = string.Empty;

    /// <summary>最近一次「整段同步完整成功」的时间（UTC）；为空 = 还从没成功过（页面显示首次同步中）。</summary>
    [SugarColumn(IsNullable = true)]
    public DateTime? LastSuccessAt { get; set; }

    /// <summary>最近一次开始同步的时间（UTC），成功失败都记；失败后按它隔一段时间再重试。</summary>
    [SugarColumn(IsNullable = true)]
    public DateTime? LastAttemptAt { get; set; }

    /// <summary>已完整对账过的最早支付日期：本地库里这一天起的工资单是完整的；为空 = 历史还没回填。</summary>
    [SugarColumn(ColumnDataType = "date", IsNullable = true)]
    public DateTime? CoveredFrom { get; set; }

    /// <summary>最近一次失败的原因（只含状态码级别的描述，不含 MYOB 响应体与薪资）；成功后清空。</summary>
    [SugarColumn(Length = 300, IsNullable = true)]
    public string? LastError { get; set; }

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? LastErrorCode { get; set; }

    /// <summary>同步租约到期时间（UTC）：租约有效期内别的进程 / 请求不得再对这家公司同步，防止后台任务与手动同步、新旧容器重叠时并发写。</summary>
    [SugarColumn(IsNullable = true)]
    public DateTime? LeaseUntil { get; set; }

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? LeaseOwner { get; set; }
}
