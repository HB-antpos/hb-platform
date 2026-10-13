using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.Containers;

/// <summary>
/// 货柜「到仓库日期」口径的唯一实现：移动端「HB新品」页（windowBasis=warehouse）与 Web 订货前台「即将上新」页共用，
/// 保证两处在同一份数据上选出完全相同的货柜集合。
/// <list type="bullet">
/// <item>到仓库日期 = 实际到货日期，没有时用预计到岸日期兜底；两者都为空的货柜没有到仓库日期，不出现；</item>
/// <item>窗口 = 本地今天的前 <see cref="PastDays"/> 天（含）到后 <see cref="FutureDays"/> 天（含），上界为开区间；</item>
/// <item>想调整「过去 1 周 / 未来 3 周」只改 <see cref="PastDays"/> 与 <see cref="FutureDays"/> 这两个常量；
/// 注意移动端默认的到店口径（旧版 App、每日新品推送）也用同一个窗口日期，改常量会同时影响它们。</item>
/// </list>
/// 注意：本类不得依赖 <c>Services.React</c>（Feature 切片也要能引用），只放纯逻辑与查询条件。
/// </summary>
public static class ContainerWarehouseArrival
{
    /// <summary>窗口向过去延伸的天数（含第 -7 天）。</summary>
    public const int PastDays = 7;

    /// <summary>窗口向未来延伸的天数（含第 +21 天，即「未来 3 周」）。</summary>
    public const int FutureDays = 21;

    private const string SydneyTimeZoneId = "Australia/Sydney";
    private const string BrisbaneTimeZoneId = "Australia/Brisbane";

    /// <summary>
    /// 到仓库日期窗口 [From, ToExclusive)：本地今天 -<see cref="PastDays"/> 天的 00:00 起（含），
    /// 到本地今天 +<see cref="FutureDays"/> 天的次日 00:00 止（不含），因此「今天 +21 天」当天含时间部分的记录也在窗口内。
    /// </summary>
    public static (DateTime From, DateTime ToExclusive) BuildWindow(DateTime localToday) =>
        (localToday.Date.AddDays(-PastDays), localToday.Date.AddDays(FutureDays + 1));

    /// <summary>
    /// 货柜到仓库日期（只取日期部分）：实际到货日期优先，没有时用预计到岸日期；两者都为空返回 null。
    /// </summary>
    public static DateTime? ResolveDate(DateTime? actualArrivalDate, DateTime? estimatedArrivalDate) =>
        (actualArrivalDate ?? estimatedArrivalDate)?.Date;

    /// <summary>
    /// 到仓库日期是否落在窗口 [from, toExclusive) 内。与 <see cref="WhereWarehouseArrivalIn"/> 的 SQL 条件语义完全一致
    /// （窗口边界是 00:00，所以按原始值比较与按日期比较等价），测试用它作为 SQL 条件的对照基准。
    /// </summary>
    public static bool IsInWindow(DateTime? actualArrivalDate, DateTime? estimatedArrivalDate, DateTime from, DateTime toExclusive)
    {
        var effective = actualArrivalDate ?? estimatedArrivalDate;
        return effective.HasValue && effective.Value >= from && effective.Value < toExclusive;
    }

    /// <summary>
    /// 候选货柜条件：未删除，且到仓库日期落在 [from, toExclusive) 内。
    /// 写成「实际日期非空且在窗口内，或实际日期为空且预计日期非空且在窗口内」，与 <see cref="IsInWindow"/> 等价。
    /// 移动端（含默认的到店口径做货柜粗筛时）与 Web 即将上新都用这一份条件，不要在别处再手写一遍。
    /// </summary>
    public static ISugarQueryable<Container> WhereWarehouseArrivalIn(
        this ISugarQueryable<Container> query,
        DateTime from,
        DateTime toExclusive
    ) =>
        query
            .Where(x => !x.IsDeleted)
            .Where(x =>
                (x.ActualArrivalDate != null && x.ActualArrivalDate >= from && x.ActualArrivalDate < toExclusive)
                || (x.ActualArrivalDate == null && x.EstimatedArrivalDate != null && x.EstimatedArrivalDate >= from && x.EstimatedArrivalDate < toExclusive));

    /// <summary>
    /// 门店所在州的本地「今天」：QLD 用 Australia/Brisbane（不实行夏令时），其余按 NSW 用 Australia/Sydney。
    /// <paramref name="nowUtc"/> 必须是 UTC 时间。
    /// </summary>
    public static DateTime GetLocalToday(string stateCode, DateTime nowUtc) =>
        TimeZoneInfo.ConvertTimeFromUtc(
            nowUtc,
            TimeZoneInfo.FindSystemTimeZoneById(stateCode == "QLD" ? BrisbaneTimeZoneId : SydneyTimeZoneId)).Date;

    /// <summary>
    /// 悉尼本地「今天」，与移动端 NSW 门店取日期的函数是同一个。
    /// Web 即将上新是不分门店的全局列表，容器所在时区不确定（不能用 <see cref="DateTime.Today"/>），统一按悉尼日历日算窗口与缓存键。
    /// </summary>
    public static DateTime GetSydneyToday(DateTime nowUtc) => GetLocalToday("NSW", nowUtc);

    /// <summary>
    /// 按到仓库日期升序排列；同一天再按货柜编号、最后按货柜主键（均为序号比较）排，保证顺序稳定；
    /// 没有到仓库日期的排在最后（正常不会出现，候选条件已排除）。
    /// </summary>
    public static IOrderedEnumerable<T> OrderByWarehouseArrival<T>(
        IEnumerable<T> source,
        Func<T, DateTime?> actualArrivalDate,
        Func<T, DateTime?> estimatedArrivalDate,
        Func<T, string?> containerNumber,
        Func<T, string?> containerId
    ) =>
        source
            .OrderBy(x => ResolveDate(actualArrivalDate(x), estimatedArrivalDate(x)) ?? DateTime.MaxValue)
            .ThenBy(x => containerNumber(x) ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(x => containerId(x) ?? string.Empty, StringComparer.Ordinal);

    /// <summary>
    /// Web 即将上新的最终列表：按 HGUID 去重（空 HGUID 丢弃，同一 HGUID 保留第一条）后，按到仓库日期升序稳定排序。
    /// 货柜主键本身唯一，去重只是防御（例如日后换成多段查询合并时不会重复出现）。
    /// </summary>
    public static List<ContainerMainDto> OrderForDisplay(IEnumerable<ContainerMainDto> containers)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var distinct = containers.Where(x => !string.IsNullOrWhiteSpace(x.HGUID) && seen.Add(x.HGUID)).ToList();
        return OrderByWarehouseArrival(distinct, x => x.实际到货日期, x => x.预计到岸日期, x => x.货柜编号, x => x.HGUID).ToList();
    }
}
