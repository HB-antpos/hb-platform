using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Services.React.SupplierPreorders;

/// <summary>节日关键日期（画在日销曲线上的紫色虚线）。Key 供前端翻译：Halloween / BlackFriday / ChristmasEve / Christmas / BoxingDay / Easter。</summary>
public sealed record SupplierPreorderKeyDate(DateTime Date, string Key);

/// <summary>
/// 节日关键日期辅助类：万圣节 10/31、黑五（美国感恩节 = 11 月第 4 个周四的次日）、平安夜 12/24、圣诞 12/25、节礼日 12/26，
/// 复活节周日沿用贺卡日历的算法（SeasonalCardHolidayCalendar）。窗口内落在哪天就返回哪天，不看订单表是哪个节日——
/// 圣诞窗口（9/1–次年 1 月中）本来就同时包含万圣节和黑五，店长需要看到这些前后的销量起伏。
/// </summary>
public static class SupplierPreorderKeyDates
{
    public const string Halloween = "Halloween";
    public const string BlackFriday = "BlackFriday";
    public const string ChristmasEve = "ChristmasEve";
    public const string Christmas = "Christmas";
    public const string BoxingDay = "BoxingDay";
    public const string Easter = "Easter";

    /// <summary>美国感恩节（11 月第 4 个周四）的次日。</summary>
    public static DateTime BlackFridayOf(int year)
    {
        var first = new DateTime(year, 11, 1);
        var offset = ((int)DayOfWeek.Thursday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + 21 + 1);
    }

    public static DateTime EasterSundayOf(int year) =>
        SeasonalCardHolidayCalendar.GetHolidayDate(SeasonalCardType.Easter, year).ToDateTime(TimeOnly.MinValue);

    /// <summary>[from, to] 闭区间内的全部关键日期，按日期升序。</summary>
    public static List<SupplierPreorderKeyDate> InRange(DateTime from, DateTime to)
    {
        var start = from.Date;
        var end = to.Date;
        var result = new List<SupplierPreorderKeyDate>();
        if (end < start)
        {
            return result;
        }

        for (var year = start.Year; year <= end.Year; year++)
        {
            result.Add(new(new DateTime(year, 10, 31), Halloween));
            result.Add(new(BlackFridayOf(year), BlackFriday));
            result.Add(new(new DateTime(year, 12, 24), ChristmasEve));
            result.Add(new(new DateTime(year, 12, 25), Christmas));
            result.Add(new(new DateTime(year, 12, 26), BoxingDay));
            result.Add(new(EasterSundayOf(year), Easter));
        }

        return result.Where(k => k.Date >= start && k.Date <= end).OrderBy(k => k.Date).ToList();
    }
}

/// <summary>历史快照里的一批进货（总仓配货 = warehouse，分店供应商进货单 = supplier）。</summary>
public sealed class SupplierPreorderHistoryBatch
{
    public DateTime Date { get; set; }
    public string Source { get; set; } = "warehouse";
    public decimal Quantity { get; set; }
    public string? DocumentNo { get; set; }
}

/// <summary>连续的折扣日合并成的一段（闭区间）。</summary>
public sealed class SupplierPreorderDateRange
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
}

/// <summary>
/// 一家分店 × 一个商品在历史窗口内的数据，序列化后存进 SupplierPreorderHistorySnapshot.PayloadJson。
/// Daily 是稀疏数组：每项 [距窗口起点的天数, 当天净销量, 当天折扣销量]，只存有销量（或退货）的日子。
/// </summary>
public sealed class SupplierPreorderHistoryPayload
{
    public DateTime? PurchaseFirst { get; set; }
    public DateTime? PurchaseLast { get; set; }
    public int PurchaseBatches { get; set; }
    public decimal PurchaseQty { get; set; }
    public decimal WarehouseQty { get; set; }
    public decimal SupplierQty { get; set; }
    public List<SupplierPreorderHistoryBatch> Batches { get; set; } = new();

    public DateTime? SalesFirst { get; set; }
    public DateTime? SalesLast { get; set; }
    public decimal SalesQty { get; set; }
    public List<decimal[]> Daily { get; set; } = new();

    /// <summary>complete 全部已判定 / partial 部分日子未判定 / unknown 都未判定 / none 没有销量。</summary>
    public string DiscountStatus { get; set; } = "none";
    public decimal DiscountQty { get; set; }
    public decimal RegularQty { get; set; }
    public decimal UnknownQty { get; set; }
    public List<SupplierPreorderDateRange> DiscountRanges { get; set; } = new();
    /// <summary>窗口内原价（未打折时的零售价）最高值。</summary>
    public decimal? RetailPrice { get; set; }
    /// <summary>窗口内折后价最低值。</summary>
    public decimal? DiscountPriceMin { get; set; }

    public bool HasSales => SalesQty != 0 || Daily.Count > 0;
    public bool HasAny => HasSales || PurchaseBatches > 0;
}

/// <summary>一行进货事实：某店某商品某天某来源的数量（同一单据已合并）。</summary>
public sealed record SupplierPreorderPurchaseFact(string StoreCode, string ProductCode, DateTime Date, string Source, decimal Quantity, string? DocumentNo);

/// <summary>一行销售事实：某店某商品某天的净销量与折扣分类（来自日统计 + batch-product-sales-discount-v3 日快照）。</summary>
public sealed record SupplierPreorderSalesFact(
    string StoreCode,
    string ProductCode,
    DateTime Date,
    decimal Quantity,
    decimal RegularQuantity,
    decimal DiscountQuantity,
    decimal UnknownQuantity,
    decimal? OriginalPriceMax,
    decimal? DiscountPriceMin);

/// <summary>历史口径（纯函数，便于单测）：窗口、快照计算、售罄率、折扣区间合并。</summary>
public static class SupplierPreorderHistoryRules
{
    /// <summary>规则版本：口径变化时改这里，旧快照会被视为过期并重算。</summary>
    public const string RuleVersion = "supplier-preorder-history-v1";

    /// <summary>窗口最长天数，防止误填几年的范围把后台任务拖垮。</summary>
    public const int MaxWindowDays = 400;

    /// <summary>折扣日之间隔着的「无销量日」不超过这个天数时合并成一段（中间有正价销量则一定断开）。</summary>
    public const int DiscountGapDays = 3;

    /// <summary>与前端 getDefaultHistoryRange 一致：圣诞 = 去年 9/1 ~ 今年 1/18；万圣节 = 去年 8/1 ~ 去年 11/7；其他节日没有默认。</summary>
    public static (DateTime From, DateTime To)? DefaultWindow(string holidayType, int seasonYear)
    {
        if (seasonYear < 2000 || seasonYear > 2100)
        {
            return null;
        }

        var lastYear = seasonYear - 1;
        return holidayType switch
        {
            "Christmas" => (new DateTime(lastYear, 9, 1), new DateTime(seasonYear, 1, 18)),
            "Halloween" => (new DateTime(lastYear, 8, 1), new DateTime(lastYear, 11, 7)),
            _ => null,
        };
    }

    /// <summary>订单表上填了就用填的（缺一头用默认补），否则用默认；超长或倒置时返回 null（不计算）。</summary>
    public static (DateTime From, DateTime To)? ResolveWindow(DateTime? historyFrom, DateTime? historyTo, string holidayType, int seasonYear)
    {
        var fallback = DefaultWindow(holidayType, seasonYear);
        var from = historyFrom?.Date ?? fallback?.From;
        var to = historyTo?.Date ?? fallback?.To;
        if (from is null || to is null || to.Value < from.Value || (to.Value - from.Value).TotalDays + 1 > MaxWindowDays)
        {
            return null;
        }

        return (from.Value, to.Value);
    }

    /// <summary>售罄率 = 销量 ÷ 进货量；没有进货时为 null（不显示），超过 100% 照实显示（期初有库存）。</summary>
    public static decimal? SellThrough(decimal salesQty, decimal purchaseQty) =>
        purchaseQty > 0 ? Math.Round(salesQty / purchaseQty, 4) : null;

    /// <summary>折扣销量占比 = 折扣销量 ÷ 已判定销量（正价 + 折扣）；没有已判定销量时为 null。</summary>
    public static decimal? DiscountShare(decimal discountQty, decimal regularQty)
    {
        var known = discountQty + regularQty;
        return known > 0 ? Math.Round(discountQty / known, 4) : null;
    }

    /// <summary>参考箱数 = 去年销量 ÷ 箱规（四舍五入，至少 0）；只作参考，不自动填。</summary>
    public static int? SuggestedCartons(decimal salesQty, int cartonQty) =>
        cartonQty > 0 && salesQty > 0 ? (int)Math.Round(salesQty / cartonQty, MidpointRounding.AwayFromZero) : null;

    /// <summary>
    /// 把销售、进货事实按（分店, 商品）汇总成快照。分店代码去空格后比较，大小写不敏感；只返回有数据的组合。
    /// </summary>
    public static Dictionary<(string StoreCode, string ProductCode), SupplierPreorderHistoryPayload> Build(
        DateTime windowFrom,
        DateTime windowTo,
        IEnumerable<SupplierPreorderSalesFact> sales,
        IEnumerable<SupplierPreorderPurchaseFact> purchases)
    {
        var start = windowFrom.Date;
        var end = windowTo.Date;
        var comparer = new StoreProductComparer();
        var salesByKey = sales
            .Where(f => f.Date.Date >= start && f.Date.Date <= end && !string.IsNullOrWhiteSpace(f.StoreCode) && !string.IsNullOrWhiteSpace(f.ProductCode))
            .GroupBy(f => (Norm(f.StoreCode), Norm(f.ProductCode)), comparer)
            .ToDictionary(g => g.Key, g => g.ToList(), comparer);
        var purchasesByKey = purchases
            .Where(f => f.Date.Date >= start && f.Date.Date <= end && f.Quantity != 0 && !string.IsNullOrWhiteSpace(f.StoreCode) && !string.IsNullOrWhiteSpace(f.ProductCode))
            .GroupBy(f => (Norm(f.StoreCode), Norm(f.ProductCode)), comparer)
            .ToDictionary(g => g.Key, g => g.ToList(), comparer);

        var result = new Dictionary<(string StoreCode, string ProductCode), SupplierPreorderHistoryPayload>(comparer);
        foreach (var key in salesByKey.Keys.Union(purchasesByKey.Keys, comparer))
        {
            var payload = new SupplierPreorderHistoryPayload();
            ApplyPurchases(payload, purchasesByKey.GetValueOrDefault(key) ?? new());
            ApplySales(payload, salesByKey.GetValueOrDefault(key) ?? new(), start);
            if (payload.HasAny)
            {
                result[key] = payload;
            }
        }

        return result;
    }

    private static void ApplyPurchases(SupplierPreorderHistoryPayload payload, List<SupplierPreorderPurchaseFact> facts)
    {
        // 同一天、同一来源、同一单据合并为一批（同一张单可能有多行同一商品）
        var batches = facts
            .GroupBy(f => (f.Date.Date, f.Source, f.DocumentNo ?? string.Empty))
            .Select(g => new SupplierPreorderHistoryBatch
            {
                Date = g.Key.Item1,
                Source = g.Key.Source,
                Quantity = g.Sum(f => f.Quantity),
                DocumentNo = string.IsNullOrEmpty(g.Key.Item3) ? null : g.Key.Item3,
            })
            .Where(b => b.Quantity != 0)
            .OrderBy(b => b.Date)
            .ThenBy(b => b.Source, StringComparer.Ordinal)
            .ToList();
        payload.Batches = batches;
        payload.PurchaseBatches = batches.Count;
        payload.PurchaseQty = batches.Sum(b => b.Quantity);
        payload.WarehouseQty = batches.Where(b => b.Source == "warehouse").Sum(b => b.Quantity);
        payload.SupplierQty = batches.Where(b => b.Source == "supplier").Sum(b => b.Quantity);
        payload.PurchaseFirst = batches.Count > 0 ? batches[0].Date : null;
        payload.PurchaseLast = batches.Count > 0 ? batches[^1].Date : null;
    }

    private static void ApplySales(SupplierPreorderHistoryPayload payload, List<SupplierPreorderSalesFact> facts, DateTime windowStart)
    {
        var days = facts
            .GroupBy(f => f.Date.Date)
            .Select(g => new
            {
                Date = g.Key,
                Quantity = g.Sum(f => f.Quantity),
                Regular = g.Sum(f => f.RegularQuantity),
                Discount = g.Sum(f => f.DiscountQuantity),
                Unknown = g.Sum(f => f.UnknownQuantity),
                Original = g.Max(f => f.OriginalPriceMax),
                DiscountPrice = g.Min(f => f.DiscountPriceMin),
            })
            .Where(d => d.Quantity != 0 || d.Discount != 0 || d.Regular != 0)
            .OrderBy(d => d.Date)
            .ToList();
        payload.Daily = days.Select(d => new[] { (decimal)(d.Date - windowStart).Days, d.Quantity, d.Discount }).ToList();
        payload.SalesQty = days.Sum(d => d.Quantity);
        var selling = days.Where(d => d.Quantity > 0).ToList();
        payload.SalesFirst = selling.Count > 0 ? selling[0].Date : null;
        payload.SalesLast = selling.Count > 0 ? selling[^1].Date : null;
        payload.RegularQty = days.Sum(d => d.Regular);
        payload.DiscountQty = days.Sum(d => d.Discount);
        payload.UnknownQty = days.Sum(d => d.Unknown);
        payload.RetailPrice = days.Max(d => d.Original);
        payload.DiscountPriceMin = days.Where(d => d.Discount > 0).Min(d => d.DiscountPrice);
        payload.DiscountStatus = days.Count == 0
            ? "none"
            : payload.UnknownQty == 0
                ? "complete"
                : payload.RegularQty != 0 || payload.DiscountQty != 0 ? "partial" : "unknown";
        payload.DiscountRanges = MergeDiscountRanges(
            days.Where(d => d.Discount > 0).Select(d => d.Date),
            days.Where(d => d.Regular > 0).Select(d => d.Date));
    }

    /// <summary>
    /// 折扣日合并成区间：相邻两个折扣日之间如果有正价销量就断开；只隔着无销量的日子且不超过 DiscountGapDays 天时合并。
    /// </summary>
    public static List<SupplierPreorderDateRange> MergeDiscountRanges(IEnumerable<DateTime> discountDays, IEnumerable<DateTime> regularDays)
    {
        var regular = regularDays.Select(d => d.Date).ToHashSet();
        var result = new List<SupplierPreorderDateRange>();
        foreach (var day in discountDays.Select(d => d.Date).Distinct().OrderBy(d => d))
        {
            if (result.Count > 0)
            {
                var last = result[^1];
                var gap = (day - last.To).Days - 1;
                var hasRegularBetween = Enumerable.Range(1, Math.Max(0, gap)).Any(offset => regular.Contains(last.To.AddDays(offset)));
                if (gap <= DiscountGapDays && !hasRegularBetween)
                {
                    last.To = day;
                    continue;
                }
            }

            result.Add(new SupplierPreorderDateRange { From = day, To = day });
        }

        return result;
    }

    internal static string Norm(string value) => value.Trim();

    internal sealed class StoreProductComparer : IEqualityComparer<(string StoreCode, string ProductCode)>
    {
        public bool Equals((string StoreCode, string ProductCode) x, (string StoreCode, string ProductCode) y) =>
            string.Equals(x.StoreCode, y.StoreCode, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.ProductCode, y.ProductCode, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string StoreCode, string ProductCode) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.StoreCode),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.ProductCode));
    }
}
