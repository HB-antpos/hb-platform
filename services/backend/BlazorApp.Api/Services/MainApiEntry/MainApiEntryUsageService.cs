using System.Globalization;
using BlazorApp.Api.Data;
using BlazorApp.Api.Models.PosApiEntry;
using BlazorApp.Api.Services.PosApiEntry;
using BlazorApp.Shared.DTOs;
using SqlSugar;

namespace BlazorApp.Api.Services.MainApiEntry;

/// <summary>
/// 主 API（Web 后台 + 员工 App）三条入口（edge / aws / direct，加上识别不出的 unknown）的用量统计查询。
/// 数据来自 dbo.MainApiEntryUsageDaily（迁移 20261027.001；主 API 自己每 60 秒累加写入）：
/// 日期口径＝悉尼日期；周＝周一起算，月＝自然月，都在查询时对日表汇总，不另建表。
/// 与 <see cref="PosApiEntryUsageService"/> 同构，分桶 / 范围上限等规则直接复用其常量与纯函数。
/// 没有分店维度，所以不做分店范围收敛；权限由控制器的 DeviceRegistration.View 策略把关。
/// </summary>
public interface IMainApiEntryUsageService
{
    Task<ApiResponse<MainApiEntryUsageResultDto>> GetUsageAsync(
        string? startDate,
        string? endDate,
        string? granularity,
        string? clientKind,
        CancellationToken cancellationToken = default);
}

public sealed class MainApiEntryUsageService : IMainApiEntryUsageService
{
    private const string DateFormat = "yyyy-MM-dd";

    private static readonly string[] EntryOrder = PosApiEntryUsageService.EntryOrder;

    private readonly ISqlSugarClient _mainDb;
    private readonly TimeProvider _timeProvider;

    public MainApiEntryUsageService(SqlSugarContext mainContext, TimeProvider? timeProvider = null)
        : this(mainContext.Db, timeProvider)
    {
    }

    internal MainApiEntryUsageService(ISqlSugarClient mainDb, TimeProvider? timeProvider = null)
    {
        _mainDb = mainDb;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ApiResponse<MainApiEntryUsageResultDto>> GetUsageAsync(
        string? startDate,
        string? endDate,
        string? granularity,
        string? clientKind,
        CancellationToken cancellationToken = default)
    {
        // ── 参数校验：全部先于任何查询，非法输入一律 400，不会触发全表扫描 ──
        var normalizedGranularity = string.IsNullOrWhiteSpace(granularity)
            ? PosApiEntryUsageGranularity.Day
            : PosApiEntryUsageGranularity.Normalize(granularity);
        if (normalizedGranularity is null)
        {
            return Fail("granularity 只能是 day、week 或 month", MainApiEntryUsageErrorCodes.InvalidGranularity);
        }

        var requestedKind = string.Empty;
        if (!string.IsNullOrWhiteSpace(clientKind))
        {
            requestedKind = clientKind.Trim().ToLowerInvariant();
            if (!MainApiClientKinds.All.Contains(requestedKind))
            {
                return Fail("clientKind 只能是 web、app 或 other", MainApiEntryUsageErrorCodes.InvalidClientKind);
            }
        }

        var today = MainApiEntryUsageClock.SydneyDate(_timeProvider.GetUtcNow());
        if (!TryResolveRange(startDate, endDate, today, out var start, out var end, out var rangeError))
        {
            return rangeError!;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var startColumn = start.ToDateTime(TimeOnly.MinValue);
        var endColumn = end.ToDateTime(TimeOnly.MinValue);
        var query = _mainDb
            .Queryable<MainApiEntryUsageDaily>()
            .Where(row => row.UsageDate >= startColumn && row.UsageDate <= endColumn);
        if (requestedKind.Length > 0)
        {
            query = query.Where(row => row.ClientKind == requestedKind);
        }

        var rows = await query.ToListAsync();
        return ApiResponse<MainApiEntryUsageResultDto>.OK(
            Aggregate(rows, start, end, normalizedGranularity, requestedKind));
    }

    // ───────────────────────── 参数 ─────────────────────────

    /// <summary>
    /// 解析并校验起止日期：只接受 yyyy-MM-dd；都缺省＝最近 30 天；只给一端时另一端按默认跨度推算；
    /// 起不能晚于止，跨度（含首尾）不超过 <see cref="PosApiEntryUsageService.MaxRangeDays"/> 天。规则与 POS 用量接口一致。
    /// </summary>
    internal static bool TryResolveRange(
        string? startText,
        string? endText,
        DateOnly today,
        out DateOnly start,
        out DateOnly end,
        out ApiResponse<MainApiEntryUsageResultDto>? error)
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
            error = Fail("日期格式必须是 yyyy-MM-dd", MainApiEntryUsageErrorCodes.InvalidDate);
            return false;
        }

        var defaultDays = PosApiEntryUsageService.DefaultRangeDays;
        if (hasStart && hasEnd)
        {
            start = parsedStart;
            end = parsedEnd;
        }
        else if (hasStart)
        {
            start = parsedStart;
            end = start.AddDays(defaultDays - 1);
        }
        else if (hasEnd)
        {
            end = parsedEnd;
            start = end.AddDays(-(defaultDays - 1));
        }
        else
        {
            end = today;
            start = today.AddDays(-(defaultDays - 1));
        }

        if (start > end)
        {
            error = Fail("开始日期不能晚于结束日期", MainApiEntryUsageErrorCodes.InvalidRange);
            return false;
        }

        if (end.DayNumber - start.DayNumber + 1 > PosApiEntryUsageService.MaxRangeDays)
        {
            error = Fail($"查询范围最多 {PosApiEntryUsageService.MaxRangeDays} 天", MainApiEntryUsageErrorCodes.RangeTooLarge);
            return false;
        }

        return true;
    }

    // 合理日期窗口：与 POS 用量接口一致，2020-01-01 ~ 2099-12-31；窗口外一律按非法日期处理，
    // 既挡掉 0001-01-01 之类的垃圾输入，也保证后面的 AddDays / AddMonths 不会越界抛异常。
    private static readonly DateOnly MinDate = new(2020, 1, 1);
    private static readonly DateOnly MaxDate = new(2099, 12, 31);

    private static bool TryParseDate(string text, out DateOnly date) =>
        DateOnly.TryParseExact(text.Trim(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
        && date >= MinDate
        && date <= MaxDate;

    private static ApiResponse<MainApiEntryUsageResultDto> Fail(string message, string errorCode) =>
        ApiResponse<MainApiEntryUsageResultDto>.Error(message, errorCode);

    // ───────────────────────── 汇总 ─────────────────────────

    /// <summary>
    /// 把日表行汇总成「时间桶 × 入口」「范围合计」「客户端类别拆分」。纯函数，便于单测。
    /// 桶先按范围生成（没有数据的桶也在），保证趋势图连续；库里的脏入口值并入 unknown、脏类别值并入 other，不丢请求数。
    /// </summary>
    internal static MainApiEntryUsageResultDto Aggregate(
        IReadOnlyList<MainApiEntryUsageDaily> rows,
        DateOnly start,
        DateOnly end,
        string granularity,
        string clientKind)
    {
        var buckets = new SortedDictionary<DateOnly, (DateOnly Start, DateOnly End, EntryAccumulator Entries)>();
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            var bucketStart = PosApiEntryUsageService.BucketStartOf(day, granularity);
            if (!buckets.ContainsKey(bucketStart))
            {
                buckets[bucketStart] = (
                    bucketStart,
                    PosApiEntryUsageService.BucketEndOf(bucketStart, granularity),
                    new EntryAccumulator());
            }
        }

        var overall = new EntryAccumulator();
        var kinds = MainApiClientKinds.All.ToDictionary(kind => kind, _ => new EntryAccumulator());
        foreach (var row in rows)
        {
            var day = DateOnly.FromDateTime(row.UsageDate);
            // 防御：查询已按范围过滤，这里再挡一次，保证 Aggregate 单独使用时也不越界。
            if (day < start || day > end)
            {
                continue;
            }

            var entry = PosApiEntryUsageService.NormalizeEntry(row.Entry);
            buckets[PosApiEntryUsageService.BucketStartOf(day, granularity)].Entries.Add(entry, row);
            overall.Add(entry, row);
            kinds[MainApiClientKinds.Normalize(row.ClientKind)].Add(entry, row);
        }

        var totalRequests = overall.ToTotal().Requests;
        var result = new MainApiEntryUsageResultDto
        {
            StartDate = start.ToString(DateFormat, CultureInfo.InvariantCulture),
            EndDate = end.ToString(DateFormat, CultureInfo.InvariantCulture),
            Granularity = granularity,
            ClientKind = clientKind,
            Total = overall.ToTotal(),
            Entries = overall.ToEntries(),
        };

        foreach (var bucket in buckets.Values)
        {
            result.Buckets.Add(new MainApiEntryUsageBucketDto
            {
                BucketStart = bucket.Start.ToString(DateFormat, CultureInfo.InvariantCulture),
                BucketEnd = bucket.End.ToString(DateFormat, CultureInfo.InvariantCulture),
                Partial = bucket.Start < start || bucket.End > end,
                Total = bucket.Entries.ToTotal(),
                Entries = bucket.Entries.ToEntries(),
            });
        }

        foreach (var kind in MainApiClientKinds.All)
        {
            var total = kinds[kind].ToTotal();
            result.ClientKinds.Add(new MainApiEntryUsageClientKindDto
            {
                ClientKind = kind,
                Total = total,
                Share = totalRequests > 0 ? (double)total.Requests / totalRequests : 0d,
                Entries = kinds[kind].ToEntries(),
            });
        }

        return result;
    }

    private sealed class EntryAccumulator
    {
        private readonly long[] requests = new long[EntryOrder.Length];
        private readonly long[] status4xx = new long[EntryOrder.Length];
        private readonly long[] status5xx = new long[EntryOrder.Length];

        public void Add(string entry, MainApiEntryUsageDaily row)
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
