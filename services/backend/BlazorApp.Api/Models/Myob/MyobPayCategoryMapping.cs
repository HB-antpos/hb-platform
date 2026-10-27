using SqlSugar;

namespace BlazorApp.Api.Models.Myob;

/// <summary>
/// HBweb 库 dbo.MyobPayCategoryMapping：每个公司文件一行，我方 5 档转账工时对应的 MYOB 工资类别 UID（可空＝未设置）。
/// 表由迁移 20261027.004-myob-pay-category 创建，不进 SqlSugarContext 的 tableTypes。
/// </summary>
[SugarTable("MyobPayCategoryMapping")]
internal sealed class MyobPayCategoryMapping
{
    [SugarColumn(IsPrimaryKey = true, Length = 50)]
    public string ConnectionGuid { get; set; } = string.Empty;

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? WeekdayUid { get; set; }

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? SaturdayUid { get; set; }

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? SundayUid { get; set; }

    /// <summary>病假 → MYOB Personal Leave Pay。</summary>
    [SugarColumn(Length = 50, IsNullable = true)]
    public string? PersonalLeaveUid { get; set; }

    /// <summary>年假 → MYOB Holiday Leave Pay。</summary>
    [SugarColumn(Length = 50, IsNullable = true)]
    public string? HolidayLeaveUid { get; set; }

    public DateTime UpdatedAt { get; set; }

    [SugarColumn(Length = 50, IsNullable = true)]
    public string? UpdatedByUserGuid { get; set; }
}
