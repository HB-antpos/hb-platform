using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.StoreCash;

/// <summary>
/// 现金管理里的日期都是「门店本地日期」：补录范围、T2 的 14 天窗口、今天是否已存款，都不能用服务器或手机时区。
/// 时区口径与考勤、分期订单一致：分店管理里配置的时区优先，其次按地址和名称推导，最后回退悉尼。
/// </summary>
internal static class StoreCashClock
{
    private const string SydneyTimeZoneId = "Australia/Sydney";

    public static string ResolveTimeZoneId(Store? store) => InstallmentOrderStoreTimeZoneResolver.Resolve(store);

    public static DateOnly GetStoreToday(Store? store, DateTimeOffset utcNow)
    {
        var timeZone = FindTimeZone(ResolveTimeZoneId(store));
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, timeZone).DateTime);
    }

    /// <summary>实体里的日期列是 datetime2，只用日期部分，时间恒为 0 点。</summary>
    public static DateTime ToColumn(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

    public static DateOnly FromColumn(DateTime value) => DateOnly.FromDateTime(value);

    /// <summary>数据库读出的 UTC 时间 Kind 是 Unspecified，序列化给客户端前必须标成 UTC，否则前端会按本地时间解析。</summary>
    public static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    public static DateTime? AsUtc(DateTime? value) => value.HasValue ? AsUtc(value.Value) : null;

    private static TimeZoneInfo FindTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return FindSydneyOrUtc();
        }
        catch (InvalidTimeZoneException)
        {
            return FindSydneyOrUtc();
        }
    }

    private static TimeZoneInfo FindSydneyOrUtc()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(SydneyTimeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
