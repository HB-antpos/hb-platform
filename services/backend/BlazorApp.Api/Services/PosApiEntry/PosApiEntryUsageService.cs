using System.Globalization;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Models.PosApiEntry;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.PosApiEntry;

/// <summary>
/// 收银端 POS API 三条入口（edge / aws / direct，加上识别不出的 unknown）的用量统计查询。
/// 数据来自 dbo.PosApiEntryUsageDaily（迁移 20261026.004；Hbpos.Api 每 60 秒累加写入）：
/// 日期口径＝悉尼日期；周＝周一起算，月＝自然月，都在查询时对日表汇总，不另建表。
/// </summary>
public interface IPosApiEntryUsageService
{
    Task<ApiResponse<PosApiEntryUsageResultDto>> GetUsageAsync(
        string? startDate,
        string? endDate,
        string? granularity,
        string? storeCode,
        CancellationToken cancellationToken = default);
}

public sealed class PosApiEntryUsageService : IPosApiEntryUsageService
{
    /// <summary>查询范围上限（含首尾共 400 天）：日表一年约 5 万行，再长没有意义且会拖慢查询。</summary>
    public const int MaxRangeDays = 400;

    /// <summary>未传日期时的默认范围：含今天在内的最近 30 天。</summary>
    public const int DefaultRangeDays = 30;

    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>输出顺序固定：三条线路在前，识别不出的 unknown 最后。</summary>
    internal static readonly string[] EntryOrder =
    [
        PosApiEntryModes.Edge,
        PosApiEntryModes.Aws,
        PosApiEntryModes.Direct,
        UnknownEntry,
    ];

    internal const string UnknownEntry = "unknown";

    private readonly ISqlSugarClient _mainDb;
    private readonly ICurrentUserManageableStoreScopeService _storeScopeService;
    private readonly TimeProvider _timeProvider;

    public PosApiEntryUsageService(
        SqlSugarContext mainContext,
        ICurrentUserManageableStoreScopeService storeScopeService,
        TimeProvider? timeProvider = null)
        : this(mainContext.Db, storeScopeService, timeProvider)
    {
    }

    internal PosApiEntryUsageService(
        ISqlSugarClient mainDb,
        ICurrentUserManageableStoreScopeService storeScopeService,
        TimeProvider? timeProvider = null)
    {
        _mainDb = mainDb;
        _storeScopeService = storeScopeService;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ApiResponse<PosApiEntryUsageResultDto>> GetUsageAsync(
        string? startDate,
        string? endDate,
        string? granularity,
        string? storeCode,
        CancellationToken cancellationToken = default)
    {
        // ── 参数校验：全部先于任何查询，非法输入一律 400，不会触发全表扫描 ──
        var normalizedGranularity = string.IsNullOrWhiteSpace(granularity)
            ? PosApiEntryUsageGranularity.Day
            : PosApiEntryUsageGranularity.Normalize(granularity);
        if (normalizedGranularity is null)
        {
            return Fail("granularity 只能是 day、week 或 month", PosApiEntryUsageErrorCodes.InvalidGranularity);
        }

        var today = SalesStatisticsBusinessDate.GetBusinessDate(_timeProvider.GetUtcNow());
        if (!TryResolveRange(startDate, endDate, DateOnly.FromDateTime(today), out var start, out var end, out var rangeError))
        {
            return rangeError!;
        }

        var scope = await _storeScopeService.GetScopeAsync();
        if (!scope.IsAllowed)
        {
            return Fail(
                scope.Message.Length > 0 ? scope.Message : "当前账号没有可管理分店",
                PosApiEntryErrorCodes.ScopeForbidden);
        }

        var requestedStore = storeCode?.Trim() ?? string.Empty;
        if (requestedStore.Length > 0 && !scope.CanAccessStoreCode(requestedStore))
        {
            return Fail("无权查看该分店的收银入口用量", PosApiEntryErrorCodes.StoreForbidden);
        }

        // 非管理员且没有可管理分店：直接返回空结果，不查库。
        var scopedCodes = scope.IsAdmin ? null : scope.StoreCodes.ToList();
        if (scopedCodes is { Count: 0 })
        {
            return ApiResponse<PosApiEntryUsageResultDto>.OK(
                Aggregate([], start, end, normalizedGranularity, requestedStore, new Dictionary<string, string>()));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var startColumn = start.ToDateTime(TimeOnly.MinValue);
        var endColumn = end.ToDateTime(TimeOnly.MinValue);
        var query = _mainDb
            .Queryable<PosApiEntryUsageDaily>()
            .Where(row => row.UsageDate >= startColumn && row.UsageDate <= endColumn);
        if (requestedStore.Length > 0)
        {
            query = query.Where(row => row.StoreCode == requestedStore);
        }
        else if (scopedCodes is not null)
        {
            // 非管理员只看自己可管理分店；「未归属分店」（空串）的请求只有管理员能看。
            query = query.Where(row => scopedCodes.Contains(row.StoreCode));
        }

        var rows = await query.ToListAsync();

        // 分店名：只查结果里出现的分店代码（不过滤停用 / 已删除，历史用量仍要能认出是哪家店）。
        var storeCodes = rows
            .Select(row => row.StoreCode)
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var storeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (storeCodes.Count > 0)
        {
            var stores = await _mainDb
                .Queryable<Store>()
                .Where(store => storeCodes.Contains(store.StoreCode))
                .Select(store => new { store.StoreCode, store.StoreName })
                .ToListAsync();
            foreach (var store in stores)
            {
                storeNames.TryAdd(store.StoreCode, store.StoreName);
            }
        }

        return ApiResponse<PosApiEntryUsageResultDto>.OK(
            Aggregate(rows, start, end, normalizedGranularity, requestedStore, storeNames));
    }

    // ───────────────────────── 参数 ─────────────────────────

    /// <summary>
    /// 解析并校验起止日期：只接受 yyyy-MM-dd；都缺省＝最近 30 天；只给一端时另一端按默认跨度推算；
    /// 起不能晚于止，跨度（含首尾）不超过 <see cref="MaxRangeDays"/> 天。
    /// </summary>
    internal static bool TryResolveRange(
        string? startText,
        string? endText,
        DateOnly today,
        out DateOnly start,
        out DateOnly end,
        out ApiResponse<PosApiEntryUsageResultDto>? error)
    {
        start = default;
        end = default;
        error = null;

        var hasStart = !string.IsNullOrWhiteSpace(startText);
        var hasEnd = !string.IsNullOrWhiteSpace(endText);
        DateOnly parsedStart = default;
        DateOnly parsedEnd = default;
        if ((hasStart && !TryParseDate(startText!, out parsedStart))
            || (hasEnd && !TryParseDate(endText!, out parsedEnd)))
        {
            error = Fail("日期格式必须是 yyyy-MM-dd", PosApiEntryUsageErrorCodes.InvalidDate);
            return false;
        }

        if (hasStart && hasEnd)
        {
            start = parsedStart;
            end = parsedEnd;
        }
        else if (hasStart)
        {
            start = parsedStart;
            end = start.AddDays(DefaultRangeDays - 1);
        }
        else if (hasEnd)
        {
            end = parsedEnd;
            start = end.AddDays(-(DefaultRangeDays - 1));
        }
        else
        {
            end = today;
            start = today.AddDays(-(DefaultRangeDays - 1));
        }

        if (start > end)
        {
            error = Fail("开始日期不能晚于结束日期", PosApiEntryUsageErrorCodes.InvalidRange);
            return false;
        }

        if (end.DayNumber - start.DayNumber + 1 > MaxRangeDays)
        {
            error = Fail($"查询范围最多 {MaxRangeDays} 天", PosApiEntryUsageErrorCodes.RangeTooLarge);
            return false;
        }

        return true;
    }

    // 合理日期窗口：功能 2026 年才上线，上限给到 2099；窗口外一律按非法日期处理，
    // 既挡掉 0001-01-01 之类的垃圾输入，也保证后面的 AddDays / AddMonths 不会越界抛异常。
    private static readonly DateOnly MinDate = new(2020, 1, 1);
    private static readonly DateOnly MaxDate = new(2099, 12, 31);

    private static bool TryParseDate(string text, out DateOnly date) =>
        DateOnly.TryParseExact(text.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
        && date >= MinDate
        && date <= MaxDate;

    private static ApiResponse<PosApiEntryUsageResultDto> Fail(string message, string errorCode) =>
        ApiResponse<PosApiEntryUsageResultDto>.Error(message, errorCode);

    // ───────────────────────── 汇总 ─────────────────────────

    /// <summary>
    /// 把日表行汇总成「时间桶 × 入口」「范围合计」「分店明细」。纯函数，便于单测。
    /// 桶先按范围生成（没有数据的桶也在），保证趋势图连续；库里的脏入口值并入 unknown，不丢请求数。
    /// </summary>
    internal static PosApiEntryUsageResultDto Aggregate(
        IReadOnlyList<PosApiEntryUsageDaily> rows,
        DateOnly start,
        DateOnly end,
        string granularity,
        string storeCode,
        IReadOnlyDictionary<string, string> storeNames)
    {
        // 桶：键＝桶起始日。
        var buckets = new SortedDictionary<DateOnly, BucketAccumulator>();
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            var bucketStart = BucketStartOf(day, granularity);
            if (!buckets.ContainsKey(bucketStart))
            {
                buckets[bucketStart] = new BucketAccumulator(bucketStart, BucketEndOf(bucketStart, granularity));
            }
        }

        var overall = new EntryAccumulator();
        var stores = new Dictionary<string, EntryAccumulator>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var day = DateOnly.FromDateTime(row.UsageDate);
            // 防御：查询已按范围过滤，这里再挡一次，保证 Aggregate 单独使用时也不越界。
            if (day < start || day > end)
            {
                continue;
            }

            var entry = NormalizeEntry(row.Entry);
            buckets[BucketStartOf(day, granularity)].Add(entry, row);
            overall.Add(entry, row);
            var storeKey = row.StoreCode ?? string.Empty;
            if (!stores.TryGetValue(storeKey, out var storeAccumulator))
            {
                storeAccumulator = new EntryAccumulator();
                stores[storeKey] = storeAccumulator;
            }

            storeAccumulator.Add(entry, row);
        }

        var result = new PosApiEntryUsageResultDto
        {
            StartDate = start.ToString(DateFormat, CultureInfo.InvariantCulture),
            EndDate = end.ToString(DateFormat, CultureInfo.InvariantCulture),
            Granularity = granularity,
            StoreCode = storeCode,
            Total = overall.ToTotal(),
            Entries = overall.ToEntries(),
        };

        foreach (var bucket in buckets.Values)
        {
            result.Buckets.Add(new PosApiEntryUsageBucketDto
            {
                BucketStart = bucket.Start.ToString(DateFormat, CultureInfo.InvariantCulture),
                BucketEnd = bucket.End.ToString(DateFormat, CultureInfo.InvariantCulture),
                Partial = bucket.Start < start || bucket.End > end,
                Total = bucket.Entries.ToTotal(),
                Entries = bucket.Entries.ToEntries(),
            });
        }

        result.Stores = stores
            .Select(pair => new PosApiEntryUsageStoreDto
            {
                StoreCode = pair.Key,
                StoreName = pair.Key.Length > 0 && storeNames.TryGetValue(pair.Key, out var name) ? name : string.Empty,
                Total = pair.Value.ToTotal(),
                Entries = pair.Value.ToEntries(),
            })
            .OrderByDescending(store => store.Total.Requests)
            .ThenBy(store => store.StoreCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return result;
    }

    internal static string NormalizeEntry(string? entry) => PosApiEntryModes.Normalize(entry) ?? UnknownEntry;

    /// <summary>桶起始日：日＝当天，周＝所在周的周一，月＝当月 1 号。</summary>
    internal static DateOnly BucketStartOf(DateOnly day, string granularity) => granularity switch
    {
        PosApiEntryUsageGranularity.Week => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
        PosApiEntryUsageGranularity.Month => new DateOnly(day.Year, day.Month, 1),
        _ => day,
    };

    /// <summary>桶结束日（含）：日＝当天，周＝周日，月＝月末。</summary>
    internal static DateOnly BucketEndOf(DateOnly bucketStart, string granularity) => granularity switch
    {
        PosApiEntryUsageGranularity.Week => bucketStart.AddDays(6),
        PosApiEntryUsageGranularity.Month => bucketStart.AddMonths(1).AddDays(-1),
        _ => bucketStart,
    };

    private sealed class BucketAccumulator(DateOnly start, DateOnly end)
    {
        public DateOnly Start { get; } = start;
        public DateOnly End { get; } = end;
        public EntryAccumulator Entries { get; } = new();

        public void Add(string entry, PosApiEntryUsageDaily row) => Entries.Add(entry, row);
    }

    private sealed class EntryAccumulator
    {
        private readonly long[] requests = new long[EntryOrder.Length];
        private readonly long[] status4xx = new long[EntryOrder.Length];
        private readonly long[] status5xx = new long[EntryOrder.Length];

        public void Add(string entry, PosApiEntryUsageDaily row)
        {
            var index = Array.IndexOf(EntryOrder, entry);
            requests[index] += row.RequestCount;
            status4xx[index] += row.Status4xxCount;
            status5xx[index] += row.Status5xxCount;
        }

        public PosApiEntryUsageCountsDto ToTotal() => new()
        {
            Requests = requests.Sum(),
            Status4xx = status4xx.Sum(),
            Status5xx = status5xx.Sum(),
        };

        public List<PosApiEntryUsageEntryDto> ToEntries()
        {
            var total = requests.Sum();
            var list = new List<PosApiEntryUsageEntryDto>(EntryOrder.Length);
            for (var i = 0; i < EntryOrder.Length; i++)
            {
                list.Add(new PosApiEntryUsageEntryDto
                {
                    Entry = EntryOrder[i],
                    Requests = requests[i],
                    Status4xx = status4xx[i],
                    Status5xx = status5xx[i],
                    Share = total > 0 ? (double)requests[i] / total : 0d,
                });
            }

            return list;
        }
    }
}
