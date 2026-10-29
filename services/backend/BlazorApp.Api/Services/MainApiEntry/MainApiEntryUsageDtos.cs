using BlazorApp.Api.Services.PosApiEntry;

namespace BlazorApp.Api.Services.MainApiEntry;

// 全局 JSON 为 camelCase：status4xx / status5xx 即前端字段名。
// 粒度沿用 PosApiEntryUsageGranularity（day / week / month）；计数子对象沿用 PosApiEntryUsageCountsDto / PosApiEntryUsageEntryDto。

public static class MainApiEntryUsageErrorCodes
{
    public const string InvalidDate = "MAIN_API_ENTRY_USAGE_INVALID_DATE";
    public const string InvalidRange = "MAIN_API_ENTRY_USAGE_INVALID_RANGE";
    public const string RangeTooLarge = "MAIN_API_ENTRY_USAGE_RANGE_TOO_LARGE";
    public const string InvalidGranularity = "MAIN_API_ENTRY_USAGE_INVALID_GRANULARITY";
    public const string InvalidClientKind = "MAIN_API_ENTRY_USAGE_INVALID_CLIENT_KIND";
}

/// <summary>一个时间桶（日 / 周 / 月）。entries 固定按 edge、aws、direct、unknown 四项输出（没有数据为 0），前端无需补零。</summary>
public sealed class MainApiEntryUsageBucketDto
{
    /// <summary>桶起始日（日＝当天，周＝周一，月＝1 号），yyyy-MM-dd，同时是桶的唯一键。</summary>
    public string BucketStart { get; set; } = string.Empty;

    /// <summary>桶结束日（含）：日＝当天，周＝周日，月＝月末。</summary>
    public string BucketEnd { get; set; } = string.Empty;

    /// <summary>桶没有被查询范围完整覆盖（首尾的不完整周 / 月）：只汇总了范围内的日期。</summary>
    public bool Partial { get; set; }

    public PosApiEntryUsageCountsDto Total { get; set; } = new();
    public List<PosApiEntryUsageEntryDto> Entries { get; set; } = [];
}

/// <summary>某一客户端类别在范围内的合计及其各入口占比；share＝该类别请求数 / 范围内总请求数（0~1，总数为 0 时为 0）。</summary>
public sealed class MainApiEntryUsageClientKindDto
{
    /// <summary>web / app / other。</summary>
    public string ClientKind { get; set; } = string.Empty;
    public PosApiEntryUsageCountsDto Total { get; set; } = new();
    public double Share { get; set; }

    /// <summary>该类别内各入口的用量与占比（share 的分母是该类别自身的请求数）。</summary>
    public List<PosApiEntryUsageEntryDto> Entries { get; set; } = [];
}

public sealed class MainApiEntryUsageResultDto
{
    /// <summary>实际生效的起止日期（含），yyyy-MM-dd，悉尼日期。</summary>
    public string StartDate { get; set; } = string.Empty;
    public string EndDate { get; set; } = string.Empty;
    public string Granularity { get; set; } = PosApiEntryUsageGranularity.Day;
    public string TimeZone { get; set; } = "Australia/Sydney";

    /// <summary>实际生效的客户端类别筛选；空串＝全部类别。</summary>
    public string ClientKind { get; set; } = string.Empty;

    /// <summary>范围内合计（受 clientKind 筛选影响）。</summary>
    public PosApiEntryUsageCountsDto Total { get; set; } = new();
    public List<PosApiEntryUsageEntryDto> Entries { get; set; } = [];

    /// <summary>按时间桶连续输出（没有数据的桶也在，全 0），便于前端直接画趋势。</summary>
    public List<MainApiEntryUsageBucketDto> Buckets { get; set; } = [];

    /// <summary>按客户端类别拆分，固定 web、app、other 三项（没有数据为 0）；筛选了 clientKind 时只有该类别有数。</summary>
    public List<MainApiEntryUsageClientKindDto> ClientKinds { get; set; } = [];
}
