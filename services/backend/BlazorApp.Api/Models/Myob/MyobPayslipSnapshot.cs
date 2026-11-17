using SqlSugar;

namespace BlazorApp.Api.Models.Myob;

/// <summary>
/// HBweb 库 dbo.MyobPayslipSnapshot：MYOB 已发薪工资单的本地副本（只读展示用，从不回写 MYOB）。
/// 由定时同步 / 手动同步从 Report/Payroll/EmployeePayrollAdvice 拉取后落库，列表、明细、PDF、邮件都读这张表，不再实时请求 MYOB。
/// 报表没有工资单 ID，所以用 SlipKey（员工卡 + 支付日 + 周期 + 税前 + 实发的哈希）识别同一张单：金额不同的更正单是另一行。
/// MYOB 里撤销 / 重发的单不物理删除，只标 RemovedAt（下次同步又出现则清空）。
/// 年薪、生日、雇佣分类、银行 BSB / 账号、TFN 仍然不读取、不落库；时薪与 Reference Number（支票号）按产品决定明文保存。
/// 表由迁移 20261117.001-myob-payslip-snapshot 创建，不进 SqlSugarContext 的 tableTypes。
/// </summary>
[SugarTable("MyobPayslipSnapshot")]
internal sealed class MyobPayslipSnapshot
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string SnapshotGuid { get; set; } = Guid.NewGuid().ToString();

    [SugarColumn(Length = 50)]
    public string ConnectionGuid { get; set; } = string.Empty;

    /// <summary>SHA-256 十六进制（64 字符）；与 ConnectionGuid 一起唯一。</summary>
    [SugarColumn(Length = 64)]
    public string SlipKey { get; set; } = string.Empty;

    /// <summary>MYOB 员工卡 UID。</summary>
    [SugarColumn(Length = 50)]
    public string EmployeeUid { get; set; } = string.Empty;

    [SugarColumn(Length = 200, IsNullable = true)]
    public string? EmployeeName { get; set; }

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? EmployeeDisplayId { get; set; }

    [SugarColumn(Length = 200, IsNullable = true)]
    public string? EmployerName { get; set; }

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? EmployerAbn { get; set; }

    [SugarColumn(ColumnDataType = "date")]
    public DateTime PaymentDate { get; set; }

    [SugarColumn(ColumnDataType = "date", IsNullable = true)]
    public DateTime? PeriodStart { get; set; }

    [SugarColumn(ColumnDataType = "date", IsNullable = true)]
    public DateTime? PeriodEnd { get; set; }

    [SugarColumn(Length = 30, IsNullable = true)]
    public string? PayFrequency { get; set; }

    [SugarColumn(Length = 18, DecimalDigits = 2, IsNullable = true)]
    public decimal? GrossPay { get; set; }

    [SugarColumn(Length = 18, DecimalDigits = 2, IsNullable = true)]
    public decimal? NetPay { get; set; }

    [SugarColumn(Length = 200, IsNullable = true)]
    public string? SuperFundName { get; set; }

    [SugarColumn(Length = 18, DecimalDigits = 4, IsNullable = true)]
    public decimal? HourlyRate { get; set; }

    [SugarColumn(Length = 100, IsNullable = true)]
    public string? ChequeNumber { get; set; }

    /// <summary>明细行（类别、类型、小时、费率、金额、YTD）的 JSON 数组。库里是 nvarchar(max)（迁移建表）；这里不写 ColumnDataType，免得 SQLite 测试库建表失败。</summary>
    public string LinesJson { get; set; } = "[]";

    /// <summary>整张单（含明细）内容的 SHA-256；同步时用来判断「同一张单的内容有没有变」。</summary>
    [SugarColumn(Length = 64)]
    public string ContentHash { get; set; } = string.Empty;

    public DateTime FirstSeenAt { get; set; }

    /// <summary>最近一次内容被改写（新增、更正、恢复）的时间；内容没变的同步不改它。</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>MYOB 里已经找不到这张单（撤销 / 重发）的时间；为空表示仍然有效。读取一律排除非空行。</summary>
    [SugarColumn(IsNullable = true)]
    public DateTime? RemovedAt { get; set; }
}
