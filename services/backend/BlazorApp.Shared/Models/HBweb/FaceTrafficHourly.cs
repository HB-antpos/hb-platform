using SqlSugar;

namespace BlazorApp.Shared.Models;

/// <summary>
/// 门店分时客流报表表：每个「门店 + 本地日期 + 本地小时」一行，由主 API 后台任务从
/// <see cref="FaceTrafficBucket"/>（按 UTC 整点累加的原始计数桶）换算投影而来，永久保留，不含任何生物信息。
/// <para>
/// 为什么要单独一张表：计数桶按 UTC 整点存，而营业额（POSM sales_order.OrderTime → HourlySalesStatistic）是门店本地墙钟时间，
/// 悉尼 / 墨尔本有夏令时，直接 JOIN 会整体错位 1 小时。本表的 (StoreCode, StatDate, StatHour) 与
/// HourlySalesStatistic 的 (BranchCode, Date, Hour) 同口径，报表可以直接按这三列 JOIN 营业额。
/// </para>
/// <para>
/// 夏令时：开始日本地缺 02 点（没有该行）；结束日本地 02 点出现两次，两个 UTC 小时的计数相加落在同一行（StatHour = 2），
/// 与营业额按 OrderTime.Hour 归并的口径一致。
/// </para>
/// 表由独立迁移 20261016.001-face-traffic-hourly 创建，实体不进自动建表清单（tableTypes）；表结构以迁移为准。
/// 只有 Match 模式的门店才有计数桶，因此只有这些门店才有分时行。
/// </summary>
[SugarTable("FaceTrafficHourly")]
public sealed class FaceTrafficHourly
{
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, Length = 32)]
    public string StoreCode { get; set; } = string.Empty;

    /// <summary>门店本地墙钟日期（只取日期部分）。</summary>
    [SugarColumn(IsPrimaryKey = true, IsNullable = false, ColumnDataType = "date")]
    public DateTime StatDate { get; set; }

    /// <summary>门店本地墙钟小时，0–23。</summary>
    [SugarColumn(IsPrimaryKey = true, IsNullable = false)]
    public byte StatHour { get; set; }

    /// <summary>抓拍的人脸张数（含员工、低质量、调用失败）。</summary>
    [SugarColumn(IsNullable = false)]
    public int CaptureCount { get; set; }

    /// <summary>命中员工的张数。</summary>
    [SugarColumn(IsNullable = false)]
    public int StaffMatchCount { get; set; }

    /// <summary>
    /// 该小时「首次出现的当日新访客」人数（估算）：同一天各小时相加得当日唯一访客数；
    /// 只有开启去重的门店才有值。是估算值（同一个人换角度、戴口罩都可能被算成两个人）。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public int UniqueVisitorCount { get; set; }

    /// <summary>当日已出现过的访客再次出现的次数。</summary>
    [SugarColumn(IsNullable = false)]
    public int RepeatCount { get; set; }

    /// <summary>质量不合格、未调用识别的张数。</summary>
    [SugarColumn(IsNullable = false)]
    public int LowQualityCount { get; set; }

    /// <summary>识别调用失败而丢弃的张数。</summary>
    [SugarColumn(IsNullable = false)]
    public int ApiFailureCount { get; set; }

    /// <summary>
    /// 非员工进店人次 = max(0, CaptureCount − StaffMatchCount − LowQualityCount − ApiFailureCount)。
    /// 含当日重复出现的人（同一个人一天多次进店各算一次），与 UniqueVisitorCount 的「去重」口径不同，也不要和它相加。
    /// 投影时写入（派生列），方便报表直接取用。
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public int VisitCount { get; set; }

    /// <summary>换算本地时间所用的 IANA 时区；门店之后改了时区，历史行仍能追溯当时的换算依据。</summary>
    [SugarColumn(IsNullable = false, Length = 64)]
    public string TimeZoneId { get; set; } = string.Empty;

    /// <summary>该行内容最近一次写入（新建或数值 / 时区变化）的时间；内容没变的重算不会改它。</summary>
    [SugarColumn(IsNullable = false)]
    public DateTime ComputedAtUtc { get; set; }
}
