using SqlSugar;

namespace BlazorApp.Api.Models.Myob;

/// <summary>
/// HBweb 库 dbo.MyobTimesheetPush：Timesheet 推送记录，每个员工每次推送 / 跳过 / 撤销一行（方案 docs/plans/2026-10-29-myob-timesheet-push.md 第 4.4 节）。
/// BeforeJson（推送前 MYOB 里的内容）与 AfterJson（我们写入的内容）是 <c>List&lt;MyobTimesheetCell&gt;</c> 的 JSON，只含工资类别 UID、日期、小时，
/// 不含姓名、金额或 MYOB 响应体；撤销就靠 BeforeJson 把 MYOB 恢复原样。
/// 表由迁移 20261030.001-myob-timesheet-push 创建，不进 SqlSugarContext 的 tableTypes。
/// </summary>
[SugarTable("MyobTimesheetPush")]
internal sealed class MyobTimesheetPush
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string PushGuid { get; set; } = Guid.NewGuid().ToString();

    /// <summary>一次「点推送」的所有记录同一个批次（前端分块调用也共用）。</summary>
    [SugarColumn(Length = 50)]
    public string BatchGuid { get; set; } = string.Empty;

    [SugarColumn(Length = 50)]
    public string ConnectionGuid { get; set; } = string.Empty;

    public DateTime PeriodStart { get; set; }

    public int Weeks { get; set; }

    [SugarColumn(Length = 50)]
    public string UserGuid { get; set; } = string.Empty;

    /// <summary>MYOB 员工卡 UID（MYOB 的标识，不是姓名）。</summary>
    [SugarColumn(Length = 50)]
    public string MyobEmployeeUid { get; set; } = string.Empty;

    /// <summary><see cref="BlazorApp.Api.Services.Myob.MyobPushStatuses"/>。</summary>
    [SugarColumn(Length = 30)]
    public string Status { get; set; } = string.Empty;

    /// <summary>见 <see cref="BlazorApp.Api.Services.Myob.MyobTimesheetPushRules.ComputeRequestHash"/>。</summary>
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? RequestHash { get; set; }

    /// <summary>规划里要推送的合计分钟（用于对账展示）。</summary>
    public int PushedMinutes { get; set; }

    [SugarColumn(IsNullable = true)]
    public string? BeforeJson { get; set; }

    [SugarColumn(IsNullable = true)]
    public string? AfterJson { get; set; }

    /// <summary>只存错误码（我方码或 MYOB 错误类型名），不存响应体。</summary>
    [SugarColumn(Length = 80, IsNullable = true)]
    public string? ErrorCode { get; set; }

    [SugarColumn(IsNullable = true)]
    public int? ErrorStatus { get; set; }

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? ActorUserGuid { get; set; }

    public DateTime StartedAt { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? FinishedAt { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? UndoneAt { get; set; }

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? UndoneByUserGuid { get; set; }

    [SugarColumn(Length = 200, IsNullable = true)]
    public string? UndoNote { get; set; }
}
