using System.Globalization;
using BlazorApp.Api.Services.React;

namespace BlazorApp.Api.Services.OperationAudits;

/// <summary>
/// 操作日志的门店时区快照，以及「门店本地时间范围 → 各时区 UTC 区间」的换算。
/// 门店时区以 <see cref="InstallmentOrderStoreTimeZoneResolver"/> 为唯一口径：分店管理里配置的优先，
/// 没配置才按地址 / 名称推断，仍识别不出时回退悉尼。
/// </summary>
public sealed class OperationAuditStoreTimeZones
{
    public const string DefaultTimeZoneId = InstallmentOrderStoreTimeZoneResolver.Sydney;

    /// <summary>没有门店时区数据时（测试替身、查询失败）一律按默认时区（悉尼）。</summary>
    public static OperationAuditStoreTimeZones Empty { get; } = new(new Dictionary<string, string>());

    private static readonly string[] LocalTimeFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd HH:mm",
    ];

    private readonly Dictionary<string, string> _byStore;

    public OperationAuditStoreTimeZones(IReadOnlyDictionary<string, string> storeTimeZones)
    {
        _byStore = new Dictionary<string, string>(storeTimeZones, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>某个时区在一个本地时间范围内对应的 UTC 区间，以及它覆盖的门店。</summary>
    /// <param name="TimeZoneId">IANA 时区 ID。</param>
    /// <param name="StoreCodes">
    /// 非默认段：这个时区的门店编码；默认段：需要排除的「其他时区」门店编码（为空表示所有门店都属于默认段）。
    /// </param>
    /// <param name="IsDefault">是否默认段（覆盖所有未配置为其他时区的门店，包括门店表里没有的编码）。</param>
    public sealed record Segment(
        string TimeZoneId,
        IReadOnlyList<string> StoreCodes,
        bool IsDefault,
        DateTime FromUtc,
        DateTime ToUtc);

    /// <summary>门店所在时区；未知门店回退默认时区。</summary>
    public string Resolve(string? storeCode) =>
        !string.IsNullOrWhiteSpace(storeCode) && _byStore.TryGetValue(storeCode.Trim(), out var timeZoneId)
            ? timeZoneId
            : DefaultTimeZoneId;

    /// <summary>
    /// 解析前端传来的门店本地时间（不带时区，如 2026-10-27T16:20:24）。
    /// 带 Z 或偏移的字符串一律视为无效，避免把 UTC 误当成门店墙钟。
    /// </summary>
    public static bool TryParseLocal(string? value, out DateTime local)
    {
        local = default;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return DateTime.TryParseExact(
            text,
            LocalTimeFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out local);
    }

    /// <summary>
    /// 把「每家门店各自的本地 [fromLocal, toLocal]」拆成按时区分组的 UTC 区间：
    /// 非默认时区的门店各成一段，其余门店合成默认段。区间两端都包含。
    /// </summary>
    public IReadOnlyList<Segment> BuildSegments(DateTime fromLocal, DateTime toLocal)
    {
        var fromUnspecified = DateTime.SpecifyKind(fromLocal, DateTimeKind.Unspecified);
        var toUnspecified = DateTime.SpecifyKind(toLocal, DateTimeKind.Unspecified);

        var segments = new List<Segment>();
        var others = _byStore
            .Where(pair => !string.Equals(pair.Value, DefaultTimeZoneId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();

        foreach (var group in others)
        {
            var zone = FindTimeZone(group.Key);
            segments.Add(new Segment(
                group.Key,
                group.Select(pair => pair.Key).OrderBy(code => code, StringComparer.Ordinal).ToList(),
                false,
                ToUtc(fromUnspecified, zone),
                ToUtc(toUnspecified, zone)));
        }

        var defaultZone = FindTimeZone(DefaultTimeZoneId);
        segments.Add(new Segment(
            DefaultTimeZoneId,
            others.SelectMany(group => group.Select(pair => pair.Key)).OrderBy(code => code, StringComparer.Ordinal).ToList(),
            true,
            ToUtc(fromUnspecified, defaultZone),
            ToUtc(toUnspecified, defaultZone)));
        return segments;
    }

    /// <summary>
    /// 墙钟 → UTC。夏令时开始时「不存在的一小时」顺延一小时；结束时「重复的一小时」取后一次（标准时间），
    /// 与 Web 端 operationLogsTime.ts 的规则一致。
    /// </summary>
    private static DateTime ToUtc(DateTime unspecifiedLocal, TimeZoneInfo zone)
    {
        var local = zone.IsInvalidTime(unspecifiedLocal) ? unspecifiedLocal.AddHours(1) : unspecifiedLocal;
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(local, zone), DateTimeKind.Utc);
    }

    private static TimeZoneInfo FindTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(DefaultTimeZoneId);
        }
    }
}
