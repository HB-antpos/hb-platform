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
