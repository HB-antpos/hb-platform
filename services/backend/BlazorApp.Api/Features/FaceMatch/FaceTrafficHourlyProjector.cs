using BlazorApp.Api.Data;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.FaceMatch;

/// <summary>一轮投影的结果（用于日志与测试断言）。</summary>
/// <param name="SchemaMissing">分时表或计数桶表还不存在（迁移未执行），本轮什么都没做。</param>
/// <param name="StoresProcessed">本轮实际处理（读过桶并对比过分时行）的门店数。</param>
/// <param name="StoresFailed">处理失败的门店数（已记 Warning，下一轮重试）。</param>
public sealed record FaceTrafficHourlyProjection(
    bool SchemaMissing, int StoresProcessed, int StoresFailed, int RowsInserted, int RowsUpdated, int RowsDeleted)
{
    public static readonly FaceTrafficHourlyProjection Missing = new(true, 0, 0, 0, 0, 0);
    public static readonly FaceTrafficHourlyProjection Idle = new(false, 0, 0, 0, 0, 0);

    public int RowsChanged => RowsInserted + RowsUpdated + RowsDeleted;

    public FaceTrafficHourlyProjection Add(FaceTrafficHourlyProjection other) => new(
        SchemaMissing || other.SchemaMissing,
        StoresProcessed + other.StoresProcessed,
        StoresFailed + other.StoresFailed,
        RowsInserted + other.RowsInserted,
        RowsUpdated + other.RowsUpdated,
        RowsDeleted + other.RowsDeleted);
}

/// <summary>
/// 门店分时客流投影：把 <see cref="FaceTrafficBucket"/>（按 UTC 整点累加的原始计数桶）换算成
/// <see cref="FaceTrafficHourly"/>（门店本地「日期 + 小时」，与营业额 HourlySalesStatistic 同口径），供报表直接 JOIN 营业额。
/// 不调用腾讯云、不读写任何生物信息，只在主库内做「读桶 → 求和 → 写分时行」。
/// <para>
/// 幂等重算、不依赖增量水位：对一个本地日期窗口，用窗口内全部 UTC 桶按 (本地日期, 本地小时) 重新求和，
/// 与库里已有的分时行逐行对比，只写有变化的行（新增 / 整行覆盖 / 删除不再产生的行）。
/// 所以接收端晚到的计数、门店改时区后的重算都自然正确；内容没变的重算不写库。
/// </para>
/// <para>
/// 夏令时：UTC 整点到本地整点的换算用 <see cref="TimeZoneInfo.ConvertTimeFromUtc"/>。开始日本地缺 02 点（没有该行）；
/// 结束日本地 02 点出现两次，两个 UTC 小时的计数<b>相加</b>落在同一行（不是后写覆盖），与营业额按 OrderTime.Hour 归并一致。
/// </para>
/// <para>
/// 时区口径：行里的 TimeZoneId 记当次换算所用时区。门店改时区后，重算窗口（最近 3 个本地日）内的行会被新时区覆盖（包括删除新时区下
/// 不再产生的行），窗口之外的历史行保持原样、仍记着当时的时区，不会被追溯改写。
/// </para>
/// 表由迁移 20261016.001-face-traffic-hourly 创建；迁移未执行时本类返回 <see cref="FaceTrafficHourlyProjection.SchemaMissing"/>，不抛异常。
/// </summary>
public sealed class FaceTrafficHourlyProjector(
    SqlSugarContext context,
    TimeProvider timeProvider,
    ILogger<FaceTrafficHourlyProjector> logger)
{
    /// <summary>定期重算覆盖的本地日数（含门店本地的今天）。</summary>
    public const int RecentLocalDays = 3;

    // 批写分块：分时行 12 列，100 行 = 1200 个参数，低于 SQL Server 单条语句 2100 个参数的上限。
    internal const int WriteChunkSize = 100;

    // IN (...) 列表的分块。
    private const int InListChunkSize = 500;

    private readonly ISqlSugarClient _db = context.Db;

    /// <summary>分时表和计数桶表都已存在（迁移已执行）。查询的是系统目录，不缓存，迁移执行后下一轮就能感知。</summary>
    public bool IsSchemaReady() =>
        _db.DbMaintenance.IsAnyTable("FaceTrafficHourly", false)
        && _db.DbMaintenance.IsAnyTable("FaceTrafficBucket", false);

    /// <summary>
    /// 后台任务入口：对每个最近有计数桶的门店，重算「最近 <see cref="RecentLocalDays"/> 个门店本地日」涉及的全部小时。
    /// 最近几天没有任何计数桶时只是一条廉价查询。单个门店失败只记 Warning、不影响其他门店。
    /// </summary>
    public async Task<FaceTrafficHourlyProjection> ProjectRecentAsync(CancellationToken cancellationToken)
    {
        if (!IsSchemaReady())
        {
            return FaceTrafficHourlyProjection.Missing;
        }

        var nowUtc = Now();
        // 预筛：窗口最早的一刻也不会早于「现在 − (RecentLocalDays + 1) 天」，留一天余量覆盖时区偏移。
        var cutoff = nowUtc.AddDays(-(RecentLocalDays + 1));
        var storeCodes = await _db.Queryable<FaceTrafficBucket>()
            .Where(b => b.BucketStartUtc >= cutoff)
            .GroupBy(b => b.StoreCode)
            .Select(b => b.StoreCode)
            .ToListAsync(cancellationToken);
        if (storeCodes.Count == 0)
        {
            return FaceTrafficHourlyProjection.Idle;
        }

        var zones = await LoadStoreZonesAsync(storeCodes, cancellationToken);
        var result = FaceTrafficHourlyProjection.Idle;
        foreach (var storeCode in storeCodes.OrderBy(code => code, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timeZoneId = zones[storeCode];
            try
            {
                var today = FaceMatchRules.LocalDate(nowUtc, timeZoneId);
                var change = await ProjectStoreAsync(
                    storeCode, timeZoneId, today.AddDays(-(RecentLocalDays - 1)), today, nowUtc, cancellationToken);
                result = result.Add(change);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "分时客流投影失败，下一轮重试: Store={StoreCode}", storeCode);
                result = result.Add(new FaceTrafficHourlyProjection(false, 0, 1, 0, 0, 0));
            }
        }

        return result;
    }

    /// <summary>
    /// 补齐历史：对每个门店，最早的计数桶比最早的分时行还早时（首次部署、迁移晚于计数开始），
    /// 把「最早计数桶所在本地日 → 最早分时行的前一天」补出来；该门店一行分时行都没有时补到门店本地今天。
    /// 只会写出原本不存在的日期，已有分时行的历史日期保持原样（不会因门店后来改了时区而被追溯改写）。
    /// 后台任务只在进程启动后的第一次成功投影之后调用一次。
    /// </summary>
    public async Task<FaceTrafficHourlyProjection> BackfillMissingHistoryAsync(CancellationToken cancellationToken)
    {
        if (!IsSchemaReady())
        {
            return FaceTrafficHourlyProjection.Missing;
        }

        var earliestBuckets = await _db.Queryable<FaceTrafficBucket>()
            .GroupBy(b => b.StoreCode)
            .Select(b => new StoreEarliest { StoreCode = b.StoreCode, Earliest = SqlFunc.AggregateMin(b.BucketStartUtc) })
            .ToListAsync(cancellationToken);
        if (earliestBuckets.Count == 0)
        {
            return FaceTrafficHourlyProjection.Idle;
        }

        var earliestHourly = (await _db.Queryable<FaceTrafficHourly>()
                .GroupBy(h => h.StoreCode)
                .Select(h => new StoreEarliest { StoreCode = h.StoreCode, Earliest = SqlFunc.AggregateMin(h.StatDate) })
                .ToListAsync(cancellationToken))
            .ToDictionary(row => row.StoreCode, row => row.Earliest.Date, StringComparer.Ordinal);

        var nowUtc = Now();
        var zones = await LoadStoreZonesAsync(earliestBuckets.Select(row => row.StoreCode).ToList(), cancellationToken);
        var result = FaceTrafficHourlyProjection.Idle;
        foreach (var row in earliestBuckets.OrderBy(r => r.StoreCode, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timeZoneId = zones[row.StoreCode];
            try
            {
                var first = FaceMatchRules.LocalDate(FaceMatchRules.AsUtc(row.Earliest), timeZoneId);
                var last = earliestHourly.TryGetValue(row.StoreCode, out var hourlyFrom)
                    ? hourlyFrom.AddDays(-1)
                    : FaceMatchRules.LocalDate(nowUtc, timeZoneId);
                if (first > last)
                {
                    continue;
                }

                result = result.Add(await ProjectStoreAsync(row.StoreCode, timeZoneId, first, last, nowUtc, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "分时客流历史补齐失败: Store={StoreCode}", row.StoreCode);
                result = result.Add(new FaceTrafficHourlyProjection(false, 0, 1, 0, 0, 0));
            }
        }

        return result;
    }

    /// <summary>
    /// 手工回填 / 重算：对门店本地日期区间 [fromDate, toDate]（含两端）整段重算。<paramref name="storeCode"/> 为空表示所有有计数桶或有分时行的门店。
    /// 区间内的行以重算结果为准（含删除不再产生的行）；区间之外不动。与定期重算用同一套逻辑，可以重复执行。
    /// 任何门店失败都会在处理完其余门店后抛出 <see cref="AggregateException"/>，调用方不会误以为成功。
    /// </summary>
    public async Task<FaceTrafficHourlyProjection> RebuildAsync(
        string? storeCode, DateTime fromDate, DateTime toDate, CancellationToken cancellationToken)
    {
        var from = fromDate.Date;
        var to = toDate.Date;
        if (from > to)
        {
            throw new ArgumentException("开始日期不能晚于结束日期。", nameof(fromDate));
        }

        if (!IsSchemaReady())
        {
            return FaceTrafficHourlyProjection.Missing;
        }

        List<string> storeCodes;
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            var fromBuckets = await _db.Queryable<FaceTrafficBucket>().GroupBy(b => b.StoreCode).Select(b => b.StoreCode).ToListAsync(cancellationToken);
            var endExclusive = to.AddDays(1);
            var fromHourly = await _db.Queryable<FaceTrafficHourly>()
                .Where(h => h.StatDate >= from && h.StatDate < endExclusive)
                .GroupBy(h => h.StoreCode)
                .Select(h => h.StoreCode)
                .ToListAsync(cancellationToken);
            storeCodes = fromBuckets.Union(fromHourly, StringComparer.Ordinal).OrderBy(code => code, StringComparer.Ordinal).ToList();
        }
        else
        {
            storeCodes = [storeCode.Trim()];
        }

        var nowUtc = Now();
        var zones = await LoadStoreZonesAsync(storeCodes, cancellationToken);
        var result = FaceTrafficHourlyProjection.Idle;
        var failures = new List<Exception>();
        foreach (var code in storeCodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                result = result.Add(await ProjectStoreAsync(code, zones[code], from, to, nowUtc, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "分时客流重算失败: Store={StoreCode}", code);
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException($"分时客流重算有 {failures.Count} 个门店失败。", failures);
        }

        return result;
    }

    /// <summary>
    /// 只读查询：本地日期区间 [fromDate, toDate] 内的分时行（按日期、门店、小时排序），供将来的报表使用；<paramref name="storeCode"/> 为空表示全部门店。
    /// 不对外暴露 HTTP 接口。
    /// </summary>
    public async Task<List<FaceTrafficHourly>> QueryAsync(
        string? storeCode, DateTime fromDate, DateTime toDate, CancellationToken cancellationToken)
    {
        var from = fromDate.Date;
        var to = toDate.Date;
        var code = string.IsNullOrWhiteSpace(storeCode) ? null : storeCode.Trim();
        var endExclusive = to.AddDays(1);
        var rows = await _db.Queryable<FaceTrafficHourly>()
            .Where(h => h.StatDate >= from && h.StatDate < endExclusive)
            .WhereIF(code != null, h => h.StoreCode == code)
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            // date 列读出来 Kind 不定，统一成只含日期部分。
            row.StatDate = row.StatDate.Date;
        }

        // 排序在内存里按规范化后的值做（结果集很小，且不依赖 date 列在各数据库里的文本 / 类型表示）。
        rows = rows
            .OrderBy(row => row.StatDate)
            .ThenBy(row => row.StoreCode, StringComparer.Ordinal)
            .ThenBy(row => row.StatHour)
            .ToList();
        return rows;
    }

    // ---------- 内部 ----------

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// 重算一个门店在本地日期区间 [fromDate, toDate] 内的分时行：读区间内的全部 UTC 桶 → 按本地日期 + 小时求和 →
    /// 与已有分时行对比 → 在一个事务里写差异（删除 / 覆盖 / 新增）。没有差异时不写库。
    /// </summary>
    private async Task<FaceTrafficHourlyProjection> ProjectStoreAsync(
        string storeCode, string timeZoneId, DateTime fromDate, DateTime toDate, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var zone = FaceMatchRules.FindTimeZone(timeZoneId);
        // 本地日期区间 → UTC 区间：本地 fromDate 0 点（含）到 toDate 次日 0 点（不含）。
        var startUtc = FaceMatchRules.LocalMidnightToUtc(fromDate, timeZoneId);
        var endUtc = FaceMatchRules.LocalMidnightToUtc(toDate.AddDays(1), timeZoneId);
        var buckets = await _db.Queryable<FaceTrafficBucket>()
            .Where(b => b.StoreCode == storeCode && b.BucketStartUtc >= startUtc && b.BucketStartUtc < endUtc)
            .ToListAsync(cancellationToken);

        var desired = Aggregate(buckets, zone, storeCode, timeZoneId, nowUtc)
            .Where(pair => pair.Key.Date >= fromDate && pair.Key.Date <= toDate)
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        // 日期区间一律写成「>= 起始日 且 < 结束日次日」的半开区间，不用 <= 结束日：SQLite 测试库把日期存成文本，等值 / 闭区间边界比较不可靠。
        var endExclusive = toDate.AddDays(1);
        var existing = (await _db.Queryable<FaceTrafficHourly>()
                .Where(h => h.StoreCode == storeCode && h.StatDate >= fromDate && h.StatDate < endExclusive)
                .ToListAsync(cancellationToken))
            .ToDictionary(row => (row.StatDate.Date, row.StatHour));

        var inserts = new List<FaceTrafficHourly>();
        var updates = new List<FaceTrafficHourly>();
        foreach (var (key, row) in desired)
        {
            if (!existing.TryGetValue(key, out var current))
            {
                inserts.Add(row);
            }
            else if (!SameContent(current, row))
            {
                updates.Add(row);
            }
        }

        // 新时区 / 桶变化后不再产生的行：区间内以重算结果为准，必须删掉，否则会留着旧时区下的幽灵行。
        var deletes = existing.Where(pair => !desired.ContainsKey(pair.Key)).Select(pair => pair.Value).ToList();

        var change = new FaceTrafficHourlyProjection(false, 1, 0, inserts.Count, updates.Count, deletes.Count);
        if (change.RowsChanged == 0)
        {
            return change;
        }

        // 「整行覆盖」= 在同一个事务里先删旧行、再插入新行：被覆盖的行（updates）和不再产生的行（deletes）一起按「门店 + 日期 + 小时」删除，
        // 然后一次性插入 inserts 与 updates 的新内容。删除按日期区间 + 小时列表写，而不是按主键逐行比较，
        // 这样与 date 列在不同数据库里的表示方式无关（SQLite 测试库把日期存成文本，主键等值比较不可靠）。
        var staleKeys = deletes.Concat(updates)
            .Select(row => (Date: row.StatDate.Date, row.StatHour))
            .ToList();
        var replacements = inserts.Concat(updates).ToList();
        var transaction = await _db.Ado.UseTranAsync(async () =>
        {
            foreach (var day in staleKeys.GroupBy(key => key.Date))
            {
                var date = day.Key;
                var nextDate = date.AddDays(1);
                var hours = day.Select(key => key.StatHour).Distinct().ToList();
                await _db.Deleteable<FaceTrafficHourly>()
                    .Where(h => h.StoreCode == storeCode && h.StatDate >= date && h.StatDate < nextDate && hours.Contains(h.StatHour))
                    .ExecuteCommandAsync(cancellationToken);
            }

            foreach (var chunk in replacements.Chunk(WriteChunkSize))
            {
                await _db.Insertable(chunk.ToList()).ExecuteCommandAsync(cancellationToken);
            }
        });
        if (!transaction.IsSuccess)
        {
            throw transaction.ErrorException ?? new InvalidOperationException("写入分时客流失败。");
        }

        return change;
    }

    /// <summary>
    /// 把 UTC 整点桶按门店本地「日期 + 小时」求和成分时行。同一个 (日期, 小时) 对应多个 UTC 小时时（夏令时结束日本地 02 点出现两次）
    /// 计数相加，绝不能后写覆盖。VisitCount = max(0, 抓拍 − 员工 − 低质量 − 调用失败)，由累加后的数字派生，不会为负。
    /// </summary>
    internal static Dictionary<(DateTime Date, byte Hour), FaceTrafficHourly> Aggregate(
        IEnumerable<FaceTrafficBucket> buckets, TimeZoneInfo zone, string storeCode, string timeZoneId, DateTime computedAtUtc)
    {
        var sums = new Dictionary<(DateTime Date, byte Hour), Sums>();
        foreach (var bucket in buckets)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(FaceMatchRules.AsUtc(bucket.BucketStartUtc), zone);
            var key = (local.Date, (byte)local.Hour);
            if (!sums.TryGetValue(key, out var sum))
            {
                sum = new Sums();
                sums[key] = sum;
            }

            sum.Add(bucket);
        }

        return sums.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToRow(storeCode, pair.Key.Date, pair.Key.Hour, timeZoneId, computedAtUtc));
    }

    private static bool SameContent(FaceTrafficHourly a, FaceTrafficHourly b) =>
        a.CaptureCount == b.CaptureCount
        && a.StaffMatchCount == b.StaffMatchCount
        && a.UniqueVisitorCount == b.UniqueVisitorCount
        && a.RepeatCount == b.RepeatCount
        && a.LowQualityCount == b.LowQualityCount
        && a.ApiFailureCount == b.ApiFailureCount
        && a.VisitCount == b.VisitCount
        && string.Equals(a.TimeZoneId, b.TimeZoneId, StringComparison.Ordinal);

    /// <summary>
    /// 门店实际时区：档案 → 门店表 → 州推导 → 悉尼（与 <c>FaceTrafficService.GetTrafficAsync</c> 同一入口）。
    /// 门店表里找不到（或已被删除）的编码按悉尼兜底，仍然投影——计数桶是真实数据，不能因为门店行缺失而丢。
    /// </summary>
    private async Task<Dictionary<string, string>> LoadStoreZonesAsync(
        IReadOnlyCollection<string> storeCodes, CancellationToken cancellationToken)
    {
        var codes = storeCodes.Distinct(StringComparer.Ordinal).ToList();
        var profiles = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var stores = new Dictionary<string, StoreZoneRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in codes.Chunk(InListChunkSize))
        {
            var part = chunk.ToList();
            foreach (var profile in await _db.Queryable<FaceStoreProfile>()
                         .Where(p => part.Contains(p.StoreCode))
                         .Select(p => new StoreZoneRow { StoreCode = p.StoreCode, TimeZoneId = p.TimeZoneId })
                         .ToListAsync(cancellationToken))
            {
                profiles[profile.StoreCode] = profile.TimeZoneId;
            }

            var rows = await _db.Queryable<Store>()
                .Where(s => part.Contains(s.StoreCode))
                .Select(s => new StoreZoneRow { StoreCode = s.StoreCode, TimeZoneId = s.TimeZoneId, Address = s.Address, IsDeleted = s.IsDeleted })
                .ToListAsync(cancellationToken);
            // 同编码有已删除与未删除两行时优先未删除的。
            foreach (var row in rows.OrderBy(r => r.IsDeleted))
            {
                stores.TryAdd(row.StoreCode, row);
            }
        }

        return codes.ToDictionary(
            code => code,
            code =>
            {
                stores.TryGetValue(code, out var store);
                profiles.TryGetValue(code, out var profileZone);
                return FaceMatchRules.ResolveTimeZoneId(profileZone, store?.TimeZoneId, store?.Address);
            },
            StringComparer.Ordinal);
    }

    private sealed class StoreZoneRow
    {
        public string StoreCode { get; set; } = string.Empty;
        public string? TimeZoneId { get; set; }
        public string? Address { get; set; }
        public bool IsDeleted { get; set; }
    }

    /// <summary>每店最早的时间点（计数桶的 UTC 整点，或分时行的本地日期）。</summary>
    private sealed class StoreEarliest
    {
        public string StoreCode { get; set; } = string.Empty;
        public DateTime Earliest { get; set; }
    }

    /// <summary>同一 (本地日期, 本地小时) 下多个 UTC 桶的累加器（long 防溢出，落行时再夹到 int）。</summary>
    private sealed class Sums
    {
        private long _capture, _staff, _unique, _repeat, _low, _failure;

        public void Add(FaceTrafficBucket bucket)
        {
            // 必须是累加：夏令时结束日本地 02 点对应两个 UTC 小时，两者的计数都要保留。
            _capture += bucket.CaptureCount;
            _staff += bucket.StaffMatchCount;
            _unique += bucket.UniqueVisitorCount;
            _repeat += bucket.RepeatCount;
            _low += bucket.LowQualityCount;
            _failure += bucket.ApiFailureCount;
        }

        public FaceTrafficHourly ToRow(string storeCode, DateTime date, byte hour, string timeZoneId, DateTime computedAtUtc) => new()
        {
            StoreCode = storeCode,
            StatDate = date,
            StatHour = hour,
            CaptureCount = Clamp(_capture),
            StaffMatchCount = Clamp(_staff),
            UniqueVisitorCount = Clamp(_unique),
            RepeatCount = Clamp(_repeat),
            LowQualityCount = Clamp(_low),
            ApiFailureCount = Clamp(_failure),
            // 非员工进店人次：员工、质量不合格、调用失败的张数都不是「进店的顾客」；各计数独立累加，相减可能为负，夹到 0。
            VisitCount = Clamp(Math.Max(0, _capture - _staff - _low - _failure)),
            TimeZoneId = timeZoneId,
            ComputedAtUtc = computedAtUtc,
        };

        private static int Clamp(long value) => (int)Math.Clamp(value, 0, int.MaxValue);
    }
}
