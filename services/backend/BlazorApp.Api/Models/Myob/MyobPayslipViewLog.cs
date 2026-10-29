using SqlSugar;

namespace BlazorApp.Api.Models.Myob;

/// <summary>
/// HBweb 库 dbo.MyobPayslipViewLog：谁在何时打开了谁的哪一期工资单。
/// 只记操作人、公司文件、被查看员工（我方 GUID，未绑定时为空）、MYOB 员工卡 UID 与支付日期，不记金额、姓名或工资内容。
/// 表由迁移 20261029.003-myob-payslip 创建；Action / Recipient 两列由 20261029.005-myob-payslip-export 增加，不进 SqlSugarContext 的 tableTypes。
/// </summary>
[SugarTable("MyobPayslipViewLog")]
internal sealed class MyobPayslipViewLog
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string LogGuid { get; set; } = Guid.NewGuid().ToString();

    [SugarColumn(Length = 50)]
    public string ActorUserGuid { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string ConnectionGuid { get; set; } = string.Empty;

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? TargetUserGuid { get; set; }

    [SugarColumn(Length = 50)]
    public string MyobEmployeeUid { get; set; } = string.Empty;

    [SugarColumn(ColumnDataType = "date")]
    public DateTime PaymentDate { get; set; }

    public DateTime ViewedAt { get; set; }

    /// <summary>View / Download / Email（<see cref="BlazorApp.Api.Services.Myob.MyobPayslipLogActions"/>）。</summary>
    [SugarColumn(Length = 20)]
    public string Action { get; set; } = "View";

    /// <summary>仅邮件发送：收件邮箱（含抄送给自己时不记，只记员工侧收件人）；其余动作为空。</summary>
    [SugarColumn(Length = 254, IsNullable = true)]
    public string? Recipient { get; set; }
}
