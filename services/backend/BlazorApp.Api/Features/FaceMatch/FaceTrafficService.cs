using System.Globalization;
using BlazorApp.Api.Data;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.FaceMatch;

/// <summary>
/// 客流统计与审计查询。统计只读计数桶（没有任何生物信息）：桶按整点 UTC 存，这里按门店时区归到本地营业日 / 小时，
/// 不依赖运行机器的时区。非管理员只能看自己所属门店；「唯一访客数」恒标注为估算。
/// </summary>
public sealed class FaceTrafficService(
    SqlSugarContext context,
    TimeProvider timeProvider
) : IFaceTrafficService
{
    public const int MaxRangeDays = 92;
    public const int DefaultDays = 7;
    public const int MaxAuditPageSize = 100;

    private readonly ISqlSugarClient _db = context.Db;

    public async Task<FaceTrafficDto> GetTrafficAsync(
        string storeCode, string? from, string? to, FaceMatchActor actor, CancellationToken cancellationToken = default)
    {
        var code = storeCode?.Trim();
        if (!BlazorApp.Api.Features.FaceCameras.FaceCameraRules.IsValidStoreCode(code))
        {
            throw FaceMatchErrors.Invalid("store", "请选择门店。");
        }

        var store = await FaceMatchStoreInfo.FindAsync(_db, code!, cancellationToken)
            ?? throw new FaceMatchException(404, "FACE_MATCH_STORE_NOT_FOUND", "门店不存在或已停用。");
        await EnsureStoreAccessAsync(actor, store.StoreCode, cancellationToken);

        var profile = await _db.Queryable<FaceStoreProfile>().FirstAsync(p => p.StoreCode == store.StoreCode, cancellationToken);
        var timeZoneId = FaceMatchRules.ResolveTimeZoneId(profile?.TimeZoneId, store.TimeZoneId, store.Address);
        var today = FaceMatchRules.LocalDate(timeProvider.GetUtcNow().UtcDateTime, timeZoneId);

        if (!FaceMatchRules.TryParseDate(to, out var toDate) || !FaceMatchRules.TryParseDate(from, out var fromDate))
        {
            throw FaceMatchErrors.Invalid("from", "日期必须是 yyyy-MM-dd 格式。");
        }

        var end = toDate ?? today;
        var start = fromDate ?? end.AddDays(-(DefaultDays - 1));
        if (start > end)
        {
            throw FaceMatchErrors.Invalid("from", "开始日期不能晚于结束日期。");
        }

        if ((end - start).TotalDays + 1 > MaxRangeDays)
        {
            throw FaceMatchErrors.Invalid("from", $"一次最多查询 {MaxRangeDays} 天。");
        }

        var startUtc = FaceMatchRules.LocalMidnightToUtc(start, timeZoneId);
        var endUtc = FaceMatchRules.LocalMidnightToUtc(end.AddDays(1), timeZoneId);
        var buckets = await _db.Queryable<FaceTrafficBucket>()
            .Where(b => b.StoreCode == store.StoreCode && b.BucketStartUtc >= startUtc && b.BucketStartUtc < endUtc)
            .ToListAsync(cancellationToken);

        var zone = FaceMatchRules.FindTimeZone(timeZoneId);
        var local = buckets
            .Select(b => (Local: TimeZoneInfo.ConvertTimeFromUtc(FaceMatchRules.AsUtc(b.BucketStartUtc), zone), Bucket: b))
            .ToList();

        var days = new List<FaceTrafficDayDto>();
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            var date = day;
            days.Add(new FaceTrafficDayDto
            {
                Date = FaceMatchRules.FormatDate(date)!,
                Counts = Sum(local.Where(item => item.Local.Date == date).Select(item => item.Bucket)),
            });
        }

        var hours = new List<FaceTrafficHourDto>();
        if (start == end)
        {
            for (var hour = 0; hour < 24; hour++)
            {
                var h = hour;
                hours.Add(new FaceTrafficHourDto
                {
                    Hour = h,
                    Counts = Sum(local.Where(item => item.Local.Hour == h).Select(item => item.Bucket)),
                });
            }
        }

        return new FaceTrafficDto
        {
            StoreCode = store.StoreCode,
            StoreName = store.StoreName,
            TimeZoneId = timeZoneId,
            From = FaceMatchRules.FormatDate(start)!,
            To = FaceMatchRules.FormatDate(end)!,
            Days = days,
            Hours = hours,
            Total = Sum(buckets),
        };
    }

    /// <summary>总览允许的最早锚点日期：防止月 / 周回退时 DateTime 下溢成 500。</summary>
    private static readonly DateTime MinOverviewAnchor = new(2000, 1, 1);

    public async Task<FaceTrafficOverviewDto> GetOverviewAsync(
        string storeCode, string? period, string? anchor, FaceMatchActor actor, CancellationToken cancellationToken = default)
    {
        var code = storeCode?.Trim();
        if (!BlazorApp.Api.Features.FaceCameras.FaceCameraRules.IsValidStoreCode(code))
        {
            throw FaceMatchErrors.Invalid("store", "请选择门店。");
        }

        // period 缺省按 day；其余只认 day / week / month。
        var periodName = string.IsNullOrWhiteSpace(period) ? "day" : period.Trim().ToLowerInvariant();
        if (periodName is not ("day" or "week" or "month"))
        {
            throw FaceMatchErrors.Invalid("period", "统计周期必须是 day、week 或 month。");
        }

        var store = await FaceMatchStoreInfo.FindAsync(_db, code!, cancellationToken)
            ?? throw new FaceMatchException(404, "FACE_MATCH_STORE_NOT_FOUND", "门店不存在或已停用。");
        await EnsureStoreAccessAsync(actor, store.StoreCode, cancellationToken);

        var profile = await _db.Queryable<FaceStoreProfile>().FirstAsync(p => p.StoreCode == store.StoreCode, cancellationToken);
        var timeZoneId = FaceMatchRules.ResolveTimeZoneId(profile?.TimeZoneId, store.TimeZoneId, store.Address);
        var zone = FaceMatchRules.FindTimeZone(timeZoneId);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(FaceMatchRules.AsUtc(timeProvider.GetUtcNow().UtcDateTime), zone);
        var today = nowLocal.Date;

        // anchor 缺省 = 门店本地今天；不能晚于今天（页面不能翻到未来）。
        if (!FaceMatchRules.TryParseDate(anchor, out var anchorDate))
        {
            throw FaceMatchErrors.Invalid("anchor", "日期必须是 yyyy-MM-dd 格式。");
        }

        var anchorDay = anchorDate ?? today;
        if (anchorDay > today)
        {
            throw FaceMatchErrors.Invalid("anchor", "不能查询未来的日期。");
        }

        if (anchorDay < MinOverviewAnchor)
        {
            throw FaceMatchErrors.Invalid("anchor", "日期过早，无法查询。");
        }

        var (from, to) = ResolveOverviewPeriod(periodName, anchorDay);
        var (prevFrom, prevTo) = periodName switch
        {
            "day" => (from.AddDays(-1), from.AddDays(-1)),
            "week" => (from.AddDays(-7), from.AddDays(-1)),
            // 上一整月：to 之前一天就是上月最后一天，再取其所在月。
            _ => ResolveOverviewPeriod("month", from.AddDays(-1)),
        };

        // 一次查询覆盖 [对照期开始, 当期结束 + 1 天)，再在内存里分到两段（两段合计 ≤ 62 天）。
        var startUtc = FaceMatchRules.LocalMidnightToUtc(prevFrom, timeZoneId);
        var endUtc = FaceMatchRules.LocalMidnightToUtc(to.AddDays(1), timeZoneId);
        var buckets = await _db.Queryable<FaceTrafficBucket>()
            .Where(b => b.StoreCode == store.StoreCode && b.BucketStartUtc >= startUtc && b.BucketStartUtc < endUtc)
            .ToListAsync(cancellationToken);

        // 与老接口同一做法：UTC 整点桶换算成门店本地时间后按本地日期 / 小时归并（夏令时结束日 02 点两个 UTC 小时落到同一小时）。
        var byDate = buckets
            .Select(b => (Local: TimeZoneInfo.ConvertTimeFromUtc(FaceMatchRules.AsUtc(b.BucketStartUtc), zone), Bucket: b))
            .GroupBy(item => item.Local.Date)
            .ToDictionary(group => group.Key, group => group.ToList());

        return new FaceTrafficOverviewDto
        {
            StoreCode = store.StoreCode,
            StoreName = store.StoreName,
            TimeZoneId = timeZoneId,
            Period = periodName,
            From = FaceMatchRules.FormatDate(from)!,
            To = FaceMatchRules.FormatDate(to)!,
            PrevFrom = FaceMatchRules.FormatDate(prevFrom)!,
            PrevTo = FaceMatchRules.FormatDate(prevTo)!,
            Today = FaceMatchRules.FormatDate(today)!,
            AsOfLocal = nowLocal.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            IsCurrent = from <= today && today <= to,
            CountingEnabled = profile?.Mode == FaceStoreMode.Match,
            TrafficDedupEnabled = profile?.TrafficDedupEnabled ?? false,
            Current = BuildOverviewSegment(byDate, from, to, periodName, today),
            Previous = BuildOverviewSegment(byDate, prevFrom, prevTo, periodName, today),
        };
    }

    /// <summary>锚点所在周期的完整起止日：day 为当天；week 为周一到周日；month 为 1 日到月末。</summary>
    private static (DateTime From, DateTime To) ResolveOverviewPeriod(string period, DateTime anchor)
    {
        switch (period)
        {
            case "week":
                var monday = anchor.AddDays(-(((int)anchor.DayOfWeek + 6) % 7));
                return (monday, monday.AddDays(6));
            case "month":
                var first = new DateTime(anchor.Year, anchor.Month, 1);
                return (first, first.AddMonths(1).AddDays(-1));
            default:
                return (anchor, anchor);
        }
    }

    private static FaceTrafficOverviewSegmentDto BuildOverviewSegment(
        Dictionary<DateTime, List<(DateTime Local, FaceTrafficBucket Bucket)>> byDate,
        DateTime from, DateTime to, string period, DateTime today)
    {
        var days = new List<FaceTrafficOverviewDayDto>();
        var all = new List<FaceTrafficBucket>();
        // 参与分小时汇总的日子：day 周期取该日（进行中也算，只要有抓拍）；week / month 只取「已完整结束且有抓拍」的日子，
        // 这样客户端用 总和 / HourlyDays 得到的日均不会被今天未走完的小时拉低。
        var hourlyDates = new List<DateTime>();

        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var items = byDate.TryGetValue(day, out var found) ? found : [];
            var dayBuckets = items.Select(item => item.Bucket).ToList();
            all.AddRange(dayBuckets);
            days.Add(new FaceTrafficOverviewDayDto
            {
                Date = FaceMatchRules.FormatDate(day)!,
                Counts = SumOverview(dayBuckets),
            });

            var hasCapture = dayBuckets.Any(b => b.CaptureCount > 0);
            if (hasCapture && (period == "day" || day < today))
            {
                hourlyDates.Add(day);
            }
        }

        var hourItems = hourlyDates.SelectMany(date => byDate[date]).ToList();
        var hours = new List<FaceTrafficOverviewHourDto>();
        for (var hour = 0; hour < 24; hour++)
        {
            var h = hour;
            hours.Add(new FaceTrafficOverviewHourDto
            {
                Hour = h,
                Counts = SumOverview(hourItems.Where(item => item.Local.Hour == h).Select(item => item.Bucket)),
            });
        }

        return new FaceTrafficOverviewSegmentDto
        {
            Days = days,
            Hours = hours,
            Total = SumOverview(all),
            HourlyDays = hourlyDates.Count,
        };
    }

    /// <summary>先按桶累加，再对聚合后的数值算进店人次（与 FaceTrafficHourly.VisitCount 同口径，不对每个桶先取 0 再相加）。</summary>
    private static FaceTrafficOverviewCountsDto SumOverview(IEnumerable<FaceTrafficBucket> buckets)
    {
        long capture = 0, staff = 0, unique = 0, repeat = 0, low = 0, failure = 0;
        foreach (var b in buckets)
        {
            capture += b.CaptureCount;
            staff += b.StaffMatchCount;
            unique += b.UniqueVisitorCount;
            repeat += b.RepeatCount;
            low += b.LowQualityCount;
            failure += b.ApiFailureCount;
        }

        return new FaceTrafficOverviewCountsDto
        {
            VisitCount = Clamp(Math.Max(0, capture - staff - low - failure)),
            CaptureCount = Clamp(capture),
            StaffMatchCount = Clamp(staff),
            UniqueVisitorCount = Clamp(unique),
            RepeatCount = Clamp(repeat),
            LowQualityCount = Clamp(low),
            ApiFailureCount = Clamp(failure),
        };
    }

    public async Task<FaceMatchPageDto<FaceAuditItemDto>> ListAuditAsync(
        string? storeCode, string? action, DateTime? fromUtc, DateTime? toUtc, int page, int pageSize,
        CancellationToken cancellationToken = default)
    {
        var size = Math.Clamp(pageSize <= 0 ? 20 : pageSize, 1, MaxAuditPageSize);
        var number = Math.Max(1, page);
        var code = string.IsNullOrWhiteSpace(storeCode) ? null : storeCode.Trim();
        var act = string.IsNullOrWhiteSpace(action) ? null : action.Trim();
        var fromValue = fromUtc.HasValue ? FaceMatchRules.AsUtc(fromUtc.Value.ToUniversalTime()) : DateTime.MinValue;
        var toValue = toUtc.HasValue ? FaceMatchRules.AsUtc(toUtc.Value.ToUniversalTime()) : DateTime.MaxValue;

        var rows = await _db.Queryable<FaceAuditLog>()
            .WhereIF(code != null, a => a.StoreCode == code)
            .WhereIF(act != null, a => a.Action == act)
            .WhereIF(fromUtc.HasValue, a => a.AtUtc >= fromValue)
            .WhereIF(toUtc.HasValue, a => a.AtUtc < toValue)
            .OrderBy(a => a.AuditId, OrderByType.Desc)
            .Skip((number - 1) * size)
            .Take(size + 1)
            .ToListAsync(cancellationToken);

        return new FaceMatchPageDto<FaceAuditItemDto>
        {
            Items = rows.Take(size).Select(a => new FaceAuditItemDto
            {
                AuditId = a.AuditId,
                AtUtc = FaceMatchRules.AsUtc(a.AtUtc),
                ActorId = a.ActorId,
                ActorName = a.ActorName,
                Action = a.Action,
                StoreCode = a.StoreCode,
                TargetRef = a.TargetRef,
                Result = a.Result,
            }).ToList(),
            Page = number,
            PageSize = size,
            HasMore = rows.Count > size,
        };
    }

    private async Task EnsureStoreAccessAsync(FaceMatchActor actor, string storeCode, CancellationToken cancellationToken)
    {
        if (actor.IsAdmin)
        {
            return;
        }

        var linked = await _db.Queryable<UserStore>()
            .InnerJoin<Store>((link, store) => link.StoreGUID == store.StoreGUID)
            .Where((link, store) => link.UserGUID == actor.Id && !link.IsDeleted && !store.IsDeleted && store.StoreCode == storeCode)
            .AnyAsync(cancellationToken);
        if (!linked)
        {
            throw new FaceMatchException(403, "FACE_MATCH_STORE_FORBIDDEN", "没有权限查看该门店的客流统计。");
        }
    }

    private static FaceTrafficCountsDto Sum(IEnumerable<FaceTrafficBucket> buckets)
    {
        long capture = 0, staff = 0, unique = 0, repeat = 0, low = 0, failure = 0;
        foreach (var b in buckets)
        {
            capture += b.CaptureCount;
            staff += b.StaffMatchCount;
            unique += b.UniqueVisitorCount;
            repeat += b.RepeatCount;
            low += b.LowQualityCount;
            failure += b.ApiFailureCount;
        }

        return new FaceTrafficCountsDto
        {
            CaptureCount = Clamp(capture),
            StaffMatchCount = Clamp(staff),
            UniqueVisitorCount = Clamp(unique),
            RepeatCount = Clamp(repeat),
            LowQualityCount = Clamp(low),
            ApiFailureCount = Clamp(failure),
        };
    }

    private static int Clamp(long value) => (int)Math.Min(value, int.MaxValue);
}
