using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.React;

/// <summary>
/// Executive revenue 页面的一次一致性快照。Branches 用于排行与 KPI，Hourly 和 Weekly
/// 使用 focus 分店范围；三者由同一个已提交读取快照生成。
/// </summary>
public sealed class RevenueReportSnapshotDto
{
    public List<ExecutiveBranchPerformanceDto> Branches { get; set; } = new();

    /// <summary>
    /// 多日区间且最后一天是今天时，单独给出今天与同期对应日的数据，
    /// 前端据此把区间里「今天」这一天换成截至最近完整整点的累计，其余日期仍按全天比较。
    /// 其余情况为 null。
    /// </summary>
    public RevenueLastDaySnapshotDto? LastDay { get; set; }
    public List<ExecutiveHourlyTrafficDto> Hourly { get; set; } = new();
    public List<WeeklyPerformanceHierarchyDto> Weekly { get; set; } = new();

    /// <summary>当前返回的数据是否没有可证明的完整快照。</summary>
    public bool StatisticsPending { get; set; }

    /// <summary>当前期营业额、分时和周层级是否可读。</summary>
    public bool CurrentPeriodPending { get; set; }

    /// <summary>同期数据缺口只影响同比字段，不应隐藏当前期数据。</summary>
    public bool ComparePeriodPending { get; set; }
    public bool HourlyCurrentPending { get; set; }
    public bool HourlyComparePending { get; set; }
    public bool WeeklyComparePending { get; set; }

    /// <summary>已有完整快照可读，但后台正在发布下一版时为 true。</summary>
    public bool RefreshInProgress { get; set; }

    public string StatisticStatus { get; set; } = SalesStatisticRefreshStatus.Pending;
    public string? StatisticMessage { get; set; }
    public DateTime? StatisticUpdatedAt { get; set; }
    public string CacheVersion { get; set; } = string.Empty;
    public int StatisticsExpectedBranchCount { get; set; }
    public int StatisticsSnapshotBranchCount { get; set; }
}

/// <summary>区间最后一天（今天）与同期对应日的分店日统计和分店×小时统计。</summary>
public sealed class RevenueLastDaySnapshotDto
{
    public DateTime Date { get; set; }
    public DateTime? CompareDate { get; set; }

    /// <summary>今天各店的全天日统计（Revenue）与同期对应日全天（RevenueLY），与排行同一范围。</summary>
    public List<ExecutiveBranchPerformanceDto> Branches { get; set; } = new();

    /// <summary>今天（Revenue）与同期对应日（RevenueLY）的分店×小时统计。</summary>
    public List<ExecutiveHourlyTrafficDto> Hourly { get; set; } = new();
}
