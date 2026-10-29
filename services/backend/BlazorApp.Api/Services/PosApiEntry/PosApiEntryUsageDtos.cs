namespace BlazorApp.Api.Services.PosApiEntry;

// 全局 JSON 为 camelCase：status4xx / status5xx 即前端字段名。

public static class PosApiEntryUsageGranularity
{
    public const string Day = "day";
    public const string Week = "week";
    public const string Month = "month";

    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim().ToLowerInvariant();
        return trimmed is Day or Week or Month ? trimmed : null;
    }
}

public static class PosApiEntryUsageErrorCodes
{
    public const string InvalidDate = "POS_API_ENTRY_USAGE_INVALID_DATE";
    public const string InvalidRange = "POS_API_ENTRY_USAGE_INVALID_RANGE";
    public const string RangeTooLarge = "POS_API_ENTRY_USAGE_RANGE_TOO_LARGE";
    public const string InvalidGranularity = "POS_API_ENTRY_USAGE_INVALID_GRANULARITY";
}

/// <summary>请求数及其中 4xx / 5xx 数（后两者是 requests 的子集）。</summary>
public sealed class PosApiEntryUsageCountsDto
{
    public long Requests { get; set; }
    public long Status4xx { get; set; }
    public long Status5xx { get; set; }
}

/// <summary>某一入口在某个范围（时间桶 / 汇总 / 单店）内的用量；share＝该入口请求数 / 范围内总请求数（0~1，总数为 0 时为 0）。</summary>
public sealed class PosApiEntryUsageEntryDto
{
    /// <summary>edge / aws / direct / unknown。</summary>
    public string Entry { get; set; } = string.Empty;
    public long Requests { get; set; }
    public long Status4xx { get; set; }
    public long Status5xx { get; set; }
    public double Share { get; set; }
}

/// <summary>一个时间桶（日 / 周 / 月）。entries 固定按 edge、aws、direct、unknown 四项输出（没有数据为 0），前端无需补零。</summary>
public sealed class PosApiEntryUsageBucketDto
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

/// <summary>分店维度明细（范围内合计）。</summary>
public sealed class PosApiEntryUsageStoreDto
{
    /// <summary>分店代码；空串＝未鉴权请求（健康检查、激活等，无法归属分店）。</summary>
    public string StoreCode { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;
    public PosApiEntryUsageCountsDto Total { get; set; } = new();
    public List<PosApiEntryUsageEntryDto> Entries { get; set; } = [];
}

public sealed class PosApiEntryUsageResultDto
{
    /// <summary>实际生效的起止日期（含），yyyy-MM-dd，悉尼日期。</summary>
    public string StartDate { get; set; } = string.Empty;
    public string EndDate { get; set; } = string.Empty;
    public string Granularity { get; set; } = PosApiEntryUsageGranularity.Day;
    public string TimeZone { get; set; } = "Australia/Sydney";

    /// <summary>实际生效的分店筛选；空串＝全部可见分店。</summary>
    public string StoreCode { get; set; } = string.Empty;

    /// <summary>范围内合计。</summary>
    public PosApiEntryUsageCountsDto Total { get; set; } = new();
    public List<PosApiEntryUsageEntryDto> Entries { get; set; } = [];

    /// <summary>按时间桶连续输出（没有数据的桶也在，全 0），便于前端直接画趋势。</summary>
    public List<PosApiEntryUsageBucketDto> Buckets { get; set; } = [];

    /// <summary>分店明细，按请求数降序。</summary>
    public List<PosApiEntryUsageStoreDto> Stores { get; set; } = [];
}
