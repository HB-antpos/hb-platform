using System.Globalization;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Services.React.HolidaySalesReport;

internal sealed record HolidayProductInfo(
    string ProductCode,
    string ItemNumber,
    string ProductName,
    string? ImageUrl,
    string SupplierCode,
    string? SupplierCategory
);

/// <summary>某分店某天某商品的净销售（退款已扣）。</summary>
internal sealed record HolidaySalesRow(string BranchCode, DateTime Date, string ProductCode, decimal Quantity, decimal Amount);

/// <summary>某分店某商品今年进货单汇总。</summary>
internal sealed record HolidayPurchaseRow(string BranchCode, string ProductCode, decimal Quantity, decimal Cost);

/// <summary>一次取数的全部原始数据（缓存单位）；各视图都从它在内存里算出来。</summary>
internal sealed class HolidaySalesDataset
{
    public int Year { get; init; }
    public DateTime HolidayDate { get; init; }
    public DateTime SalesFrom { get; init; }

    /// <summary>已出数的最后一天（含）。早于 <see cref="SalesFrom"/> 表示还没有任何数据。</summary>
    public DateTime SalesThrough { get; init; }
    public IReadOnlyList<HolidayProductInfo> Products { get; init; } = [];
    public IReadOnlyList<HolidaySalesRow> Sales { get; init; } = [];
    public IReadOnlyList<HolidayPurchaseRow> Purchases { get; init; } = [];
    public IReadOnlyDictionary<string, string> StoreNames { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// 节日销售报告的全部计算，纯内存、无数据库依赖。
///
/// 口径要点：
/// - 售罄率 = 售出 ÷ 进货，只统计「该店有进货的商品」的销售，避免没进货的店卖去年存货把比例抬高。
/// - 卖断：某店某款累计销量达到该店今年进货的 95%（进货不足 3 件的不判），卖断日不晚于节日当天才算；
///   提前天数 = 节日当天 − 卖断日，0 天（当天卖完）不算「提前」。
/// - 少卖估算 = 卖断日前 14 天（含）的日均销量 × 提前天数 × 该店该款平均成交价。
/// - 剩余库存 = max(进货 − 季内销量, 0)，按进货均价折成成本；系统没有门店库存，去年剩货不在内。
/// </summary>
internal static class HolidaySalesReportBuilder
{
    internal const decimal SoldOutRatio = 0.95m;
    internal const decimal MinPurchaseForSoldOut = 3m;
    internal const int LostWindowDays = 14;
    internal const decimal SlowSellThroughBelow = 0.35m;
    internal const decimal SlowMinPurchase = 12m;
    /// <summary>四张商品榜各自最多返回的款数；页面先显示 10 个，之后每次多展开 10 个。</summary>
    internal const int ListSize = 50;

    /// <summary>某分店某商品的指标（计算中间量）。</summary>
    private sealed class PairMetric
    {
        public string Branch = string.Empty;
        public string Product = string.Empty;
        public decimal PurchaseQty;
        public decimal PurchaseCost;
        public decimal SoldQty;
        public decimal Amount;
        public Dictionary<DateTime, decimal> QtyByDay = new();
        public int? DaysEarly;
        public DateTime? SoldOutDate;
        public decimal Lost;
        public decimal LeftQty;
        public decimal LeftCost;

        /// <summary>该店有进货才计入售罄率分母 / 分子。</summary>
        public bool Stocked => PurchaseQty > 0;
    }

    /// <summary>可累加的汇总（总计、大类、商品、分店共用）。</summary>
    private sealed class Acc
    {
        public decimal Amount, Qty, PurchaseQty, PurchaseCost, StockedSoldQty, Lost, LeftQty, LeftCost;
        public int EarlyCount, StoreCount, SoldOutStoreCount;
        public HashSet<string> SoldProducts = new();
        public HashSet<string> EarlyProducts = new();
        public List<int> Earlies = new();

        public void Add(PairMetric m)
        {
            Amount += m.Amount;
            Qty += m.SoldQty;
            if (m.SoldQty > 0) SoldProducts.Add(m.Product);
            if (!m.Stocked) return;
            PurchaseQty += m.PurchaseQty;
            PurchaseCost += m.PurchaseCost;
            StockedSoldQty += m.SoldQty;
            Lost += m.Lost;
            LeftQty += m.LeftQty;
            LeftCost += m.LeftCost;
            StoreCount++;
            if (m.DaysEarly is > 0)
            {
                EarlyCount++;
                EarlyProducts.Add(m.Product);
                SoldOutStoreCount++;
                Earlies.Add(m.DaysEarly.Value);
            }
        }

        public decimal? SellThrough => PurchaseQty > 0 ? Math.Round(StockedSoldQty / PurchaseQty, 4) : null;
    }

    internal static HolidaySalesReportDto Build(
        HolidaySalesDataset data,
        IReadOnlyCollection<string>? branchScope,
        string? selectedBranch,
        string? supplierCode
    )
    {
        var products = data.Products
            .Where(p => string.IsNullOrWhiteSpace(supplierCode) || p.SupplierCode == supplierCode)
            .ToDictionary(p => p.ProductCode, p => p, StringComparer.OrdinalIgnoreCase);
        var scope = ToScope(branchScope);
        var pairs = ComputePairs(data, products, scope);

        var visible = string.IsNullOrWhiteSpace(selectedBranch)
            ? pairs
            : pairs.Where(p => string.Equals(p.Branch, selectedBranch.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var single = !string.IsNullOrWhiteSpace(selectedBranch);

        var report = new HolidaySalesReportDto
        {
            Year = data.Year,
            HolidayDate = Fmt(data.HolidayDate),
            SalesFrom = Fmt(data.SalesFrom),
            SalesThrough = Fmt(data.SalesThrough),
            ProductCount = products.Count,
            BranchCode = single ? selectedBranch!.Trim() : null,
            BranchName = single ? NameOf(data, selectedBranch!.Trim()) : null,
        };

        // 日销售额曲线 + 节前 / 节后拆分
        var dailyAmount = new Dictionary<DateTime, decimal>();
        foreach (var row in data.Sales)
        {
            if (!products.ContainsKey(row.ProductCode) || !InScope(scope, row.BranchCode)) continue;
            if (single && !string.Equals(row.BranchCode, selectedBranch!.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            dailyAmount[row.Date.Date] = dailyAmount.GetValueOrDefault(row.Date.Date) + row.Amount;
        }
        for (var day = data.SalesFrom.Date; day <= data.SalesThrough.Date; day = day.AddDays(1))
            report.Daily.Add(new HolidaySalesDailyDto { Date = Fmt(day), Amount = R(dailyAmount.GetValueOrDefault(day)) });

        var total = new Acc();
        foreach (var m in visible) total.Add(m);
        var holiday = data.HolidayDate.Date;
        report.Totals = new HolidaySalesTotalsDto
        {
            Amount = R(total.Amount),
            Quantity = total.Qty,
            SkuCount = total.SoldProducts.Count,
            PurchaseQuantity = total.PurchaseQty,
            PurchaseCost = R(total.PurchaseCost),
            SellThrough = total.SellThrough,
            EarlySoldOutCount = total.EarlyCount,
            EarlySoldOutProductCount = total.EarlyProducts.Count,
            EstimatedLostAmount = R(total.Lost),
            LeftoverQuantity = total.LeftQty,
            LeftoverCost = R(total.LeftCost),
            HolidayDayAmount = R(dailyAmount.GetValueOrDefault(holiday)),
            LastWeekAmount = R(Enumerable.Range(0, 7).Sum(i => dailyAmount.GetValueOrDefault(holiday.AddDays(-i)))),
            AfterHolidayAmount = R(dailyAmount.Where(kv => kv.Key > holiday).Sum(kv => kv.Value)),
        };

        // 供应商构成
        foreach (var group in visible.GroupBy(m => products[m.Product].SupplierCode).OrderByDescending(g => g.Sum(m => m.Amount)))
        {
            var acc = new Acc();
            foreach (var m in group) acc.Add(m);
            report.Suppliers.Add(new HolidaySalesSupplierDto
            {
                SupplierCode = group.Key,
                SupplierName = SupplierName(group.Key),
                Amount = R(acc.Amount),
                Quantity = acc.Qty,
                PurchaseQuantity = acc.PurchaseQty,
            });
        }

        // 大类
        foreach (var group in visible.GroupBy(m => CategoryOf(products[m.Product])).OrderByDescending(g => g.Sum(m => m.Amount)))
        {
            var acc = new Acc();
            foreach (var m in group) acc.Add(m);
            report.Categories.Add(new HolidaySalesCategoryDto
            {
                Key = group.Key,
                Amount = R(acc.Amount),
                Quantity = acc.Qty,
                SkuCount = acc.SoldProducts.Count,
                PurchaseQuantity = acc.PurchaseQty,
                SellThrough = acc.SellThrough,
                EstimatedLostAmount = R(acc.Lost),
                LeftoverQuantity = acc.LeftQty,
                LeftoverCost = R(acc.LeftCost),
            });
        }

        // 商品四张榜
        var productAcc = new Dictionary<string, Acc>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in visible)
        {
            if (!productAcc.TryGetValue(m.Product, out var acc)) productAcc[m.Product] = acc = new Acc();
            acc.Add(m);
        }
        HolidaySalesProductDto ToDto(string code, Acc acc, PairMetric? only) => MapProduct(products[code], acc, only);
        var singlePairs = single ? visible.ToDictionary(m => m.Product, m => m, StringComparer.OrdinalIgnoreCase) : null;
        PairMetric? Only(string code) => singlePairs != null && singlePairs.TryGetValue(code, out var m) ? m : null;

        report.BestSellers = productAcc.Where(kv => kv.Value.Amount > 0)
            .OrderByDescending(kv => kv.Value.Amount).Take(ListSize)
            .Select(kv => ToDto(kv.Key, kv.Value, Only(kv.Key))).ToList();
        report.EarlySoldOut = productAcc
            .Where(kv => kv.Value.EarlyCount > 0 && (single || kv.Value.SoldOutStoreCount >= 2))
            .OrderByDescending(kv => kv.Value.Lost).ThenByDescending(kv => kv.Value.EarlyCount).Take(ListSize)
            .Select(kv => ToDto(kv.Key, kv.Value, Only(kv.Key))).ToList();
        report.SlowSellers = productAcc
            .Where(kv => kv.Value.PurchaseQty >= SlowMinPurchase && (kv.Value.SellThrough ?? 1m) < SlowSellThroughBelow)
            .OrderByDescending(kv => kv.Value.LeftCost).Take(ListSize)
            .Select(kv => ToDto(kv.Key, kv.Value, Only(kv.Key))).ToList();
        report.Overstock = productAcc.Where(kv => kv.Value.LeftQty > 0)
            .OrderByDescending(kv => kv.Value.LeftQty).Take(ListSize)
            .Select(kv => ToDto(kv.Key, kv.Value, Only(kv.Key))).ToList();

        // 分店排行（合计视图）与单店基准
        var branchAcc = pairs.GroupBy(m => m.Branch, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g =>
        {
            var acc = new Acc();
            foreach (var m in g) acc.Add(m);
            return acc;
        }, StringComparer.OrdinalIgnoreCase);
        var afterByBranch = data.Sales
            .Where(r => r.Date.Date > holiday && products.ContainsKey(r.ProductCode) && InScope(scope, r.BranchCode))
            .GroupBy(r => r.BranchCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount), StringComparer.OrdinalIgnoreCase);
        HolidaySalesStoreDto ToStore(string branch, Acc acc) => new()
        {
            BranchCode = branch,
            BranchName = NameOf(data, branch),
            Amount = R(acc.Amount),
            Quantity = acc.Qty,
            PurchaseQuantity = acc.PurchaseQty,
            SellThrough = acc.SellThrough,
            EarlySoldOutCount = acc.EarlyCount,
            EstimatedLostAmount = R(acc.Lost),
            LeftoverCost = R(acc.LeftCost),
            AfterHolidayAmount = R(afterByBranch.GetValueOrDefault(branch)),
        };
        var withSales = branchAcc.Where(kv => kv.Value.Amount > 0).OrderByDescending(kv => kv.Value.Amount).ToList();

        if (!single)
        {
            report.Stores = withSales.Select(kv => ToStore(kv.Key, kv.Value)).ToList();
            report.StoresWithoutSales = branchAcc
                .Where(kv => kv.Value.Amount <= 0 && kv.Value.PurchaseQty > 0)
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => ToStore(kv.Key, kv.Value)).ToList();
        }
        else if (withSales.Count > 0)
        {
            var all = new Acc();
            foreach (var m in pairs) all.Add(m);
            var categorySellThrough = new Dictionary<string, decimal?>();
            foreach (var group in pairs.GroupBy(m => CategoryOf(products[m.Product])))
            {
                var acc = new Acc();
                foreach (var m in group) acc.Add(m);
                categorySellThrough[group.Key] = acc.SellThrough;
            }
            var n = withSales.Count;
            var rank = withSales.FindIndex(kv => string.Equals(kv.Key, selectedBranch!.Trim(), StringComparison.OrdinalIgnoreCase)) + 1;
            report.Benchmark = new HolidaySalesBenchmarkDto
            {
                StoreCount = n,
                Rank = rank,
                AverageAmount = R(withSales.Average(kv => kv.Value.Amount)),
                SellThrough = all.SellThrough,
                AverageEarlySoldOutCount = Math.Round((decimal)withSales.Average(kv => kv.Value.EarlyCount), 1),
                AverageLostAmount = R(withSales.Average(kv => kv.Value.Lost)),
                AverageLeftoverCost = R(withSales.Average(kv => kv.Value.LeftCost)),
                AverageAfterHolidayAmount = R(withSales.Average(kv => afterByBranch.GetValueOrDefault(kv.Key))),
                CategorySellThrough = categorySellThrough,
            };
        }

        return report;
    }

    private static HolidaySalesProductDto MapProduct(HolidayProductInfo info, Acc acc, PairMetric? only) => new()
    {
        ProductCode = info.ProductCode,
        ItemNumber = info.ItemNumber,
        ProductName = info.ProductName,
        ImageUrl = info.ImageUrl,
        SupplierCode = info.SupplierCode,
        CategoryKey = CategoryOf(info),
        Amount = R(acc.Amount),
        Quantity = acc.Qty,
        PurchaseQuantity = acc.PurchaseQty,
        SellThrough = acc.SellThrough,
        StoreCount = acc.StoreCount,
        SoldOutStoreCount = acc.SoldOutStoreCount,
        DaysEarly = acc.Earlies.Count > 0 ? Median(acc.Earlies) : null,
        SoldOutDate = only?.SoldOutDate is { } so && only.DaysEarly is > 0 ? Fmt(so) : null,
        EstimatedLostAmount = R(acc.Lost),
        LeftoverQuantity = acc.LeftQty,
        LeftoverCost = R(acc.LeftCost),
    };

    /// <summary>
    /// 大类明细：该大类的合计、各分店表现（合计视图）与完整商品清单（有销售或有进货的都列，按销售额降序）。
    /// 大类代号不认识返回 null；单店视图只含该店。
    /// </summary>
    internal static HolidaySalesCategoryDetailDto? BuildCategoryDetail(
        HolidaySalesDataset data,
        IReadOnlyCollection<string>? branchScope,
        string? selectedBranch,
        string? supplierCode,
        string categoryKey
    )
    {
        if (!HolidaySalesCategoryRules.AllKeys.Contains(categoryKey)) return null;
        var products = data.Products
            .Where(p => (string.IsNullOrWhiteSpace(supplierCode) || p.SupplierCode == supplierCode) && CategoryOf(p) == categoryKey)
            .ToDictionary(p => p.ProductCode, p => p, StringComparer.OrdinalIgnoreCase);
        var scope = ToScope(branchScope);
        var pairs = ComputePairs(data, products, scope);
        var single = !string.IsNullOrWhiteSpace(selectedBranch);
        var branch = single ? selectedBranch!.Trim() : null;
        var visible = single ? pairs.Where(m => string.Equals(m.Branch, branch, StringComparison.OrdinalIgnoreCase)).ToList() : pairs;

        var total = new Acc();
        foreach (var m in visible) total.Add(m);
        var detail = new HolidaySalesCategoryDetailDto
        {
            Year = data.Year,
            HolidayDate = Fmt(data.HolidayDate),
            BranchCode = branch,
            BranchName = single ? NameOf(data, branch!) : null,
            Category = new HolidaySalesCategoryDto
            {
                Key = categoryKey,
                Amount = R(total.Amount),
                Quantity = total.Qty,
                SkuCount = total.SoldProducts.Count,
                PurchaseQuantity = total.PurchaseQty,
                SellThrough = total.SellThrough,
                EstimatedLostAmount = R(total.Lost),
                LeftoverQuantity = total.LeftQty,
                LeftoverCost = R(total.LeftCost),
            },
        };

        var productAcc = new Dictionary<string, Acc>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in visible)
        {
            if (!productAcc.TryGetValue(m.Product, out var acc)) productAcc[m.Product] = acc = new Acc();
            acc.Add(m);
        }
        var singlePairs = single ? visible.ToDictionary(m => m.Product, m => m, StringComparer.OrdinalIgnoreCase) : null;
        detail.Products = productAcc
            .Where(kv => kv.Value.Amount != 0 || kv.Value.Qty != 0 || kv.Value.PurchaseQty > 0)
            .OrderByDescending(kv => kv.Value.Amount).ThenByDescending(kv => kv.Value.PurchaseQty)
            .Select(kv => MapProduct(products[kv.Key], kv.Value, singlePairs != null && singlePairs.TryGetValue(kv.Key, out var only) ? only : null))
            .ToList();

        if (!single)
        {
            var afterByBranch = data.Sales
                .Where(r => r.Date.Date > data.HolidayDate.Date && products.ContainsKey(r.ProductCode) && InScope(scope, r.BranchCode))
                .GroupBy(r => r.BranchCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount), StringComparer.OrdinalIgnoreCase);
            detail.Stores = visible
                .GroupBy(m => m.Branch, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var acc = new Acc();
                    foreach (var m in g) acc.Add(m);
                    return (Branch: g.Key, Acc: acc);
                })
                .Where(x => x.Acc.Amount != 0 || x.Acc.PurchaseQty > 0)
                .OrderByDescending(x => x.Acc.Amount)
                .Select(x => new HolidaySalesStoreDto
                {
                    BranchCode = x.Branch,
                    BranchName = NameOf(data, x.Branch),
                    Amount = R(x.Acc.Amount),
                    Quantity = x.Acc.Qty,
                    PurchaseQuantity = x.Acc.PurchaseQty,
                    SellThrough = x.Acc.SellThrough,
                    EarlySoldOutCount = x.Acc.EarlyCount,
                    EstimatedLostAmount = R(x.Acc.Lost),
                    LeftoverCost = R(x.Acc.LeftCost),
                    AfterHolidayAmount = R(afterByBranch.GetValueOrDefault(x.Branch)),
                })
                .ToList();
        }
        return detail;
    }

    /// <summary>单个商品的分店明细：每家有进货或有销售的分店一行，按销售额降序；商品不在节日商品里返回 null。</summary>
    internal static HolidaySalesProductStoresDto? BuildProductStores(
        HolidaySalesDataset data,
        IReadOnlyCollection<string>? branchScope,
        string productCode
    )
    {
        var info = data.Products.FirstOrDefault(p => string.Equals(p.ProductCode, productCode, StringComparison.OrdinalIgnoreCase));
        if (info == null) return null;

        var products = new Dictionary<string, HolidayProductInfo>(StringComparer.OrdinalIgnoreCase) { [info.ProductCode] = info };
        var pairs = ComputePairs(data, products, ToScope(branchScope))
            .Where(m => m.Amount != 0 || m.SoldQty != 0 || m.PurchaseQty > 0)
            .OrderByDescending(m => m.Amount).ThenByDescending(m => m.PurchaseQty).ThenBy(m => m.Branch, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var total = new Acc();
        foreach (var m in pairs) total.Add(m);

        return new HolidaySalesProductStoresDto
        {
            Year = data.Year,
            HolidayDate = Fmt(data.HolidayDate),
            ProductCode = info.ProductCode,
            ItemNumber = info.ItemNumber,
            ProductName = info.ProductName,
            ImageUrl = info.ImageUrl,
            SupplierCode = info.SupplierCode,
            CategoryKey = CategoryOf(info),
            StoreCount = total.StoreCount,
            SoldOutStoreCount = total.SoldOutStoreCount,
            Total = new HolidaySalesProductStoreDto
            {
                Amount = R(total.Amount),
                Quantity = total.Qty,
                PurchaseQuantity = total.PurchaseQty,
                SellThrough = total.SellThrough,
                EstimatedLostAmount = R(total.Lost),
                LeftoverQuantity = total.LeftQty,
                LeftoverCost = R(total.LeftCost),
            },
            Stores = pairs.Select(m => new HolidaySalesProductStoreDto
            {
                BranchCode = m.Branch,
                BranchName = NameOf(data, m.Branch),
                Amount = R(m.Amount),
                Quantity = m.SoldQty,
                PurchaseQuantity = m.PurchaseQty,
                SellThrough = m.Stocked ? Math.Round(m.SoldQty / m.PurchaseQty, 4) : null,
                SoldOutDate = m.SoldOutDate is { } so ? Fmt(so) : null,
                DaysEarly = m.DaysEarly,
                EstimatedLostAmount = R(m.Lost),
                LeftoverQuantity = m.LeftQty,
                LeftoverCost = R(m.LeftCost),
                FirstSaleDate = m.QtyByDay.Count > 0 ? Fmt(m.QtyByDay.Keys.Min()) : null,
            }).ToList(),
        };
    }

    /// <summary>大类归并对照：每个大类下，供应商分类 → 款数与季内销售额。</summary>
    internal static HolidaySalesCategoryMapDto BuildCategoryMap(
        HolidaySalesDataset data,
        IReadOnlyCollection<string>? branchScope
    )
    {
        var scope = ToScope(branchScope);
        var amountByProduct = data.Sales
            .Where(r => InScope(scope, r.BranchCode))
            .GroupBy(r => r.ProductCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount), StringComparer.OrdinalIgnoreCase);

        var map = new HolidaySalesCategoryMapDto { Year = data.Year };
        foreach (var group in data.Products.GroupBy(CategoryOf))
        {
            var entries = group
                .GroupBy(p => (p.SupplierCode, Category: string.IsNullOrWhiteSpace(p.SupplierCategory) ? null : p.SupplierCategory.Trim()))
                .Select(g => new HolidaySalesCategoryMapEntryDto
                {
                    SupplierCode = g.Key.SupplierCode,
                    SupplierCategory = g.Key.Category,
                    SkuCount = g.Count(),
                    Amount = R(g.Sum(p => amountByProduct.GetValueOrDefault(p.ProductCode))),
                })
                .OrderByDescending(e => e.Amount).ThenByDescending(e => e.SkuCount)
                .ToList();
            map.Groups.Add(new HolidaySalesCategoryMapGroupDto { Key = group.Key, Amount = R(entries.Sum(e => e.Amount)), Entries = entries });
        }
        map.Groups = map.Groups.OrderByDescending(g => g.Amount).ToList();
        return map;
    }

    /// <summary>把销售与进货合成「分店 × 商品」指标，并判定卖断、估算少卖与剩余。</summary>
    private static List<PairMetric> ComputePairs(
        HolidaySalesDataset data,
        IReadOnlyDictionary<string, HolidayProductInfo> products,
        HashSet<string>? scope
    )
    {
        var pairs = new Dictionary<(string, string), PairMetric>();
        PairMetric Get(string branch, string product)
        {
            // 商品编码统一取主档里的写法，避免销售与进货两边大小写不同被拆成两行。
            var canonical = products[product].ProductCode;
            var key = (branch.Trim().ToUpperInvariant(), canonical);
            if (!pairs.TryGetValue(key, out var m))
                pairs[key] = m = new PairMetric { Branch = branch.Trim(), Product = canonical };
            return m;
        }

        foreach (var row in data.Sales)
        {
            if (!products.ContainsKey(row.ProductCode) || !InScope(scope, row.BranchCode)) continue;
            var m = Get(row.BranchCode, row.ProductCode);
            m.SoldQty += row.Quantity;
            m.Amount += row.Amount;
            m.QtyByDay[row.Date.Date] = m.QtyByDay.GetValueOrDefault(row.Date.Date) + row.Quantity;
        }
        foreach (var row in data.Purchases)
        {
            if (!products.ContainsKey(row.ProductCode) || !InScope(scope, row.BranchCode)) continue;
            var m = Get(row.BranchCode, row.ProductCode);
            m.PurchaseQty += row.Quantity;
            m.PurchaseCost += row.Cost;
        }

        var holiday = data.HolidayDate.Date;
        foreach (var m in pairs.Values)
        {
            if (!m.Stocked) continue;
            m.LeftQty = Math.Max(m.PurchaseQty - m.SoldQty, 0);
            m.LeftCost = m.LeftQty * (m.PurchaseCost / m.PurchaseQty);
            if (m.PurchaseQty < MinPurchaseForSoldOut) continue;

            var threshold = m.PurchaseQty * SoldOutRatio;
            decimal cumulative = 0;
            foreach (var day in m.QtyByDay.Keys.OrderBy(d => d))
            {
                cumulative += m.QtyByDay[day];
                if (cumulative < threshold) continue;
                if (day <= holiday)
                {
                    m.SoldOutDate = day;
                    m.DaysEarly = (holiday - day).Days;
                }
                break;
            }

            if (m.DaysEarly is > 0 && m.SoldOutDate is { } soldOutDate && m.SoldQty > 0)
            {
                var window = Enumerable.Range(0, LostWindowDays).Sum(i => m.QtyByDay.GetValueOrDefault(soldOutDate.AddDays(-i)));
                m.Lost = window / LostWindowDays * m.DaysEarly.Value * (m.Amount / m.SoldQty);
            }
        }
        return pairs.Values.ToList();
    }

    private static HashSet<string>? ToScope(IReadOnlyCollection<string>? scope) =>
        scope == null ? null : new HashSet<string>(scope.Select(c => c.Trim()), StringComparer.OrdinalIgnoreCase);

    private static bool InScope(HashSet<string>? scope, string branch) => scope == null || scope.Contains(branch.Trim());

    private static string CategoryOf(HolidayProductInfo p) => HolidaySalesCategoryRules.Resolve(p.SupplierCategory, p.ProductName);

    private static string NameOf(HolidaySalesDataset data, string branch) =>
        data.StoreNames.TryGetValue(branch, out var name) && !string.IsNullOrWhiteSpace(name) ? name : branch;

    /// <summary>供应商显示名：只有这两家会进入本报告。</summary>
    internal static string SupplierName(string code) => code switch
    {
        HolidaySalesSuppliers.Kfc => "KFC",
        HolidaySalesSuppliers.Brazco => "Brazco",
        _ => code,
    };

    private static int Median(List<int> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    private static decimal R(decimal value) => Math.Round(value, 2);

    private static string Fmt(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>本报告涉及的两家供应商（本地供应商编码）。</summary>
internal static class HolidaySalesSuppliers
{
    internal const string Kfc = "257";
    internal const string Brazco = "243";
}
