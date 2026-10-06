using System.Globalization;
using BlazorApp.Api.Services.Attendance;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.React;

public static class InstallmentOrderStoreTimeZoneResolver
{
    public const string Brisbane = "Australia/Brisbane";
    public const string Sydney = "Australia/Sydney";
    public const string Melbourne = "Australia/Melbourne";

    public static string Resolve(Store? store)
    {
        if (store == null)
            return Sydney;

        // 分店管理里配置的时区优先；未配置或无效时才按地址/名称推导（与考勤、销售历史口径一致）
        if (StoreTimeZonePolicy.TryNormalize(store.TimeZoneId, out var configuredTimeZone)
            && configuredTimeZone != null)
            return configuredTimeZone;

        var postcode = PublicHolidaySyncHelper.ExtractPostcodeFromAddress(store.Address);
        var jurisdiction = ResolveJurisdiction(postcode);
        if (jurisdiction == "QLD")
            return Brisbane;
        if (jurisdiction == "VIC")
            return Melbourne;
        if (jurisdiction == "NSW")
            return Sydney;

        var storeText = $"{store.StoreCode} {store.StoreName} {store.Address}".ToUpperInvariant();
        if (ContainsAny(storeText, "BRI", "BRISBANE", "QLD", "QUEENSLAND"))
            return Brisbane;
        if (ContainsAny(storeText, "MEL", "MELBOURNE", "VIC", "VICTORIA"))
            return Melbourne;

        // 未识别门店沿用考勤链路的 Sydney 回退规则。
        return Sydney;
    }

    public static (DateTime? StartUtc, DateTime? EndUtc) BuildUtcWindow(
        DateOnly? startDate,
        DateOnly? endDate,
        string timeZoneId
    )
    {
        var timeZone = FindTimeZone(timeZoneId);
        var startUtc = startDate.HasValue
            ? TimeZoneInfo.ConvertTimeToUtc(
                startDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified),
                timeZone
            )
            : (DateTime?)null;
        var endUtc = endDate.HasValue
            ? TimeZoneInfo.ConvertTimeToUtc(
                endDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified),
                timeZone
            )
            : (DateTime?)null;
        return (startUtc, endUtc);
    }

    private static string? ResolveJurisdiction(string? postcode)
    {
        if (!int.TryParse(postcode, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            return null;

        return value switch
        {
            >= 3000 and <= 3999 => "VIC",
            >= 8000 and <= 8999 => "VIC",
            _ => PublicHolidaySyncHelper.ResolveJurisdictionFromPostcode(postcode),
        };
    }

    private static TimeZoneInfo FindTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(Sydney);
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(Sydney);
        }
    }

    private static bool ContainsAny(string source, params string[] values) =>
        values.Any(value => source.Contains(value, StringComparison.Ordinal));
}
