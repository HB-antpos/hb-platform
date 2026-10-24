namespace BlazorApp.Api.Features.FaceMatch;

// 移动端「客流统计」页专用的总览契约。与老接口的 FaceTrafficDto / FaceTrafficCountsDto 完全独立（不复用、不改动），
// 避免影响 Web 表格与既有测试的整体相等断言。

/// <summary>总览用计数：比老接口多一个派生的「进店人次」。</summary>
public sealed record FaceTrafficOverviewCountsDto
{
    /// <summary>非员工进店人次 = max(0, 抓拍 − 员工 − 低质量 − 识别失败)；含当日重复到访，与 UniqueVisitorCount 的去重口径不同，不要相加。</summary>
    public int VisitCount { get; init; }

    public int CaptureCount { get; init; }
    public int StaffMatchCount { get; init; }

    /// <summary>估算值：只有开启去重的门店才有值；周 / 月内跨天的同一个人会被重复计算。</summary>
    public int UniqueVisitorCount { get; init; }

    public int RepeatCount { get; init; }
    public int LowQualityCount { get; init; }
    public int ApiFailureCount { get; init; }
}

public sealed record FaceTrafficOverviewDayDto
{
    /// <summary>门店本地营业日 yyyy-MM-dd（周期内每一天都有，含未来日，计数为 0）。</summary>
    public string Date { get; init; } = string.Empty;

    public FaceTrafficOverviewCountsDto Counts { get; init; } = new();
}

public sealed record FaceTrafficOverviewHourDto
{
    /// <summary>门店本地小时 0–23。</summary>
    public int Hour { get; init; }

    public FaceTrafficOverviewCountsDto Counts { get; init; } = new();
}

/// <summary>一个周期（当期或对照期）的数据。</summary>
public sealed record FaceTrafficOverviewSegmentDto
{
    public IReadOnlyList<FaceTrafficOverviewDayDto> Days { get; init; } = [];

    /// <summary>
    /// 始终 24 项。day：该日的分小时计数；week / month：周期内「已完整结束且当天有抓拍」的日子各小时计数之和（客户端除以 HourlyDays 得日均）。
    /// </summary>
    public IReadOnlyList<FaceTrafficOverviewHourDto> Hours { get; init; } = [];

    public FaceTrafficOverviewCountsDto Total { get; init; } = new();

    /// <summary>参与 Hours 汇总的天数：day 为 1（该日有抓拍）或 0；week / month 为符合条件的日子个数。</summary>
    public int HourlyDays { get; init; }
}

public sealed record FaceTrafficOverviewDto
{
    public string StoreCode { get; init; } = string.Empty;
    public string? StoreName { get; init; }
    public string TimeZoneId { get; init; } = string.Empty;

    /// <summary>day / week / month。</summary>
    public string Period { get; init; } = string.Empty;

    /// <summary>当期完整周期 yyyy-MM-dd：day 为同一天；week 为周一至周日；month 为 1 日至月末。</summary>
    public string From { get; init; } = string.Empty;
    public string To { get; init; } = string.Empty;

    /// <summary>对照周期：前一天 / 上一整周 / 上一整月。</summary>
    public string PrevFrom { get; init; } = string.Empty;
    public string PrevTo { get; init; } = string.Empty;

    /// <summary>门店本地今天 yyyy-MM-dd；客户端据此判断未来日，不使用手机日期。</summary>
    public string Today { get; init; } = string.Empty;

    /// <summary>取数时刻的门店本地时间 yyyy-MM-ddTHH:mm。</summary>
    public string AsOfLocal { get; init; } = string.Empty;

    /// <summary>当期包含今天（数据仍在增长）。</summary>
    public bool IsCurrent { get; init; }

    /// <summary>门店档案模式为 Match（才会累计客流计数桶）；无档案或 Off / Capture 为 false，此时数据多为 0。</summary>
    public bool CountingEnabled { get; init; }

    /// <summary>门店是否开启访客去重；关闭时 UniqueVisitorCount 无意义，页面不应展示。</summary>
    public bool TrafficDedupEnabled { get; init; }

    public FaceTrafficOverviewSegmentDto Current { get; init; } = new();
    public FaceTrafficOverviewSegmentDto Previous { get; init; } = new();
}
