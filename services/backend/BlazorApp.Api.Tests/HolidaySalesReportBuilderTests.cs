using BlazorApp.Api.Services.React.HolidaySalesReport;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>节日销售报告：大类归并规则与「售罄率 / 卖断 / 少卖 / 剩余」口径。</summary>
public sealed class HolidaySalesReportBuilderTests
{
    private static readonly DateTime Holiday = new(2026, 10, 31);

    [Theory]
    // 品名含 Costume 一律服装，即使供应商分类是主题系列或装饰类
    [InlineData("Skeleton", "Costume G.I.D Skeleton Kids", HolidaySalesCategoryRules.Costume)]
    [InlineData("Dress Up & Accessories", "Boo Bling Charm Earrings Asst", HolidaySalesCategoryRules.Costume)]
    [InlineData("Halloween Accessories", "Glitter Glasses Asst", HolidaySalesCategoryRules.Accessory)]
    [InlineData("Skeletons & Lawn Scares", "Hanging Skeleton 80cm", HolidaySalesCategoryRules.Decoration)]
    [InlineData("Light Up & Projectors", "LED Pumpkin", HolidaySalesCategoryRules.Animated)]
    [InlineData("Inflatable decorations", "Inflatable Rotate Eye Palm 2M", HolidaySalesCategoryRules.Animated)]
    [InlineData("Trick or Treat & Party", "Pumpkin Bucket 18cm", HolidaySalesCategoryRules.Party)]
    [InlineData("Special Effects & Make Up", "Horror Face Paint Asst", HolidaySalesCategoryRules.MakeUp)]
    [InlineData("Wigs, headbands and hats", "Witch Hat", HolidaySalesCategoryRules.MaskWig)]
    // 供应商分类是主题系列（Witch / Zombie）说明不了商品类型时，按品名关键词
    [InlineData("Witch", "Fake Blood 30mL", HolidaySalesCategoryRules.MakeUp)]
    [InlineData("Zombie", "Latex Mask Zombie", HolidaySalesCategoryRules.MaskWig)]
    [InlineData(null, "Hanging Ghost 90cm", HolidaySalesCategoryRules.Decoration)]
    // 什么都认不出来时归配件·道具
    [InlineData(null, "Mystery Thing", HolidaySalesCategoryRules.Accessory)]
    [InlineData("", "", HolidaySalesCategoryRules.Accessory)]
    public void CategoryRules_Resolve(string? supplierCategory, string name, string expected)
    {
        Assert.Equal(expected, HolidaySalesCategoryRules.Resolve(supplierCategory, name));
    }

    [Fact]
    public void Build_SoldOutBeforeHoliday_CountsEarlyAndEstimatesLostSales()
    {
        // 1013 进 100 件，10/20 卖 50、10/25 卖 45，累计 95 件正好达到 95% → 10/25 卖断，提前 6 天
        var data = Dataset(
            sales:
            [
                Sale("1013", new DateTime(2026, 10, 20), "A", 50, 500m),
                Sale("1013", new DateTime(2026, 10, 25), "A", 45, 450m),
            ],
            purchases: [Buy("1013", "A", 100, 300m)]);

        var report = HolidaySalesReportBuilder.Build(data, null, "1013", null);

        var item = Assert.Single(report.EarlySoldOut);
        Assert.Equal(6, item.DaysEarly);
        Assert.Equal("2026-10-25", item.SoldOutDate);
        // 14 天窗口内（10/12~10/25）共 95 件 → 日均 95/14，× 提前 6 天 × 均价 10 元
        Assert.Equal(Math.Round(95m / 14m * 6m * 10m, 2), item.EstimatedLostAmount);
        Assert.Equal(1, report.Totals.EarlySoldOutCount);
        Assert.Equal(0.95m, report.Totals.SellThrough);
        Assert.Equal(5m, report.Totals.LeftoverQuantity);
        Assert.Equal(15m, report.Totals.LeftoverCost); // 5 件 × 进价 3 元
    }

    [Fact]
    public void Build_SoldOutOnHolidayItself_IsNotEarly()
    {
        var data = Dataset(
            sales: [Sale("1013", Holiday, "A", 100, 1000m)],
            purchases: [Buy("1013", "A", 100, 300m)]);

        var report = HolidaySalesReportBuilder.Build(data, null, "1013", null);

        Assert.Empty(report.EarlySoldOut);
        Assert.Equal(0, report.Totals.EarlySoldOutCount);
    }

    [Fact]
    public void Build_PurchaseUnderThreeUnits_NeverJudgedSoldOut()
    {
        var data = Dataset(
            sales: [Sale("1013", new DateTime(2026, 10, 1), "A", 2, 20m)],
            purchases: [Buy("1013", "A", 2, 6m)]);

        Assert.Empty(HolidaySalesReportBuilder.Build(data, null, "1013", null).EarlySoldOut);
    }

    [Fact]
    public void Build_AllStoresView_EarlySoldOutNeedsAtLeastTwoStores()
    {
        var data = Dataset(
            sales:
            [
                Sale("1013", new DateTime(2026, 10, 25), "A", 100, 1000m),
                Sale("1012", new DateTime(2026, 10, 26), "A", 100, 1000m),
                Sale("1013", new DateTime(2026, 10, 24), "B", 100, 1000m),
            ],
            purchases:
            [
                Buy("1013", "A", 100, 300m), Buy("1012", "A", 100, 300m), Buy("1013", "B", 100, 300m),
            ]);

        var all = HolidaySalesReportBuilder.Build(data, null, null, null);

        // A 两家店卖断入榜；B 只有一家店卖断，合计视图里不算「库存不够」
        var item = Assert.Single(all.EarlySoldOut);
        Assert.Equal("A", item.ProductCode);
        Assert.Equal(2, item.SoldOutStoreCount);
        Assert.Equal(3, all.Totals.EarlySoldOutCount); // 店次：A 两家 + B 一家
        Assert.Equal(2, all.Totals.EarlySoldOutProductCount); // 款数：A、B

        var single = HolidaySalesReportBuilder.Build(data, null, "1013", null);
        Assert.Equal(2, single.EarlySoldOut.Count); // 单店视图：本店卖断的都列出
    }

    [Fact]
    public void Build_SellThrough_IgnoresSalesFromStoresWithoutPurchase()
    {
        // 1004 没有今年进货却卖了 20 件（去年存货）：不能把售罄率抬高
        var data = Dataset(
            sales:
            [
                Sale("1013", new DateTime(2026, 10, 20), "A", 50, 500m),
                Sale("1004", new DateTime(2026, 10, 20), "A", 20, 200m),
            ],
            purchases: [Buy("1013", "A", 100, 300m)]);

        var report = HolidaySalesReportBuilder.Build(data, null, null, null);

        Assert.Equal(0.5m, report.Totals.SellThrough);
        Assert.Equal(70m, report.Totals.Quantity); // 销量和销售额仍然包含它
        Assert.Equal(700m, report.Totals.Amount);
    }

    [Fact]
    public void Build_BranchScope_ExcludesOtherStores()
    {
        var data = Dataset(
            sales: [Sale("1013", new DateTime(2026, 10, 20), "A", 10, 100m), Sale("1012", new DateTime(2026, 10, 20), "A", 5, 50m)],
            purchases: [Buy("1013", "A", 20, 60m), Buy("1012", "A", 20, 60m)]);

        var report = HolidaySalesReportBuilder.Build(data, ["1012"], null, null);

        Assert.Equal(50m, report.Totals.Amount);
        Assert.Equal("1012", Assert.Single(report.Stores).BranchCode);
    }

    [Fact]
    public void Build_Daily_FillsEveryDayAndSplitsAroundHoliday()
    {
        var data = Dataset(
            sales:
            [
                Sale("1013", new DateTime(2026, 10, 25), "A", 1, 10m),
                Sale("1013", Holiday, "A", 4, 40m),
                Sale("1013", new DateTime(2026, 11, 2), "A", 2, 20m),
            ],
            purchases: []);

        var report = HolidaySalesReportBuilder.Build(data, null, null, null);

        Assert.Equal(95, report.Daily.Count); // 8/1 ~ 11/3
        Assert.Equal(0m, report.Daily.First(d => d.Date == "2026-08-01").Amount);
        Assert.Equal(40m, report.Totals.HolidayDayAmount);
        Assert.Equal(50m, report.Totals.LastWeekAmount); // 10/25 ~ 10/31
        Assert.Equal(20m, report.Totals.AfterHolidayAmount);
    }

    [Fact]
    public void Build_SingleStore_ReturnsBenchmarkAndNoStoreRanking()
    {
        var data = Dataset(
            sales:
            [
                Sale("1013", new DateTime(2026, 10, 20), "A", 90, 900m),
                Sale("1012", new DateTime(2026, 10, 20), "A", 10, 100m),
            ],
            purchases: [Buy("1013", "A", 100, 300m), Buy("1012", "A", 100, 300m)]);

        var report = HolidaySalesReportBuilder.Build(data, null, "1012", null);

        Assert.Empty(report.Stores);
        Assert.Equal("1012", report.BranchCode);
        Assert.Equal("Glendale", report.BranchName);
        Assert.NotNull(report.Benchmark);
        Assert.Equal(2, report.Benchmark!.StoreCount);
        Assert.Equal(2, report.Benchmark.Rank);
        Assert.Equal(500m, report.Benchmark.AverageAmount);
        Assert.Equal(0.5m, report.Benchmark.SellThrough);
    }

    [Fact]
    public void Build_SlowAndOverstock_ListLeftoverProducts()
    {
        var data = Dataset(
            sales: [Sale("1013", new DateTime(2026, 10, 20), "B", 5, 50m), Sale("1013", new DateTime(2026, 10, 20), "A", 90, 900m)],
            purchases: [Buy("1013", "B", 40, 200m), Buy("1013", "A", 100, 300m)]);

        var report = HolidaySalesReportBuilder.Build(data, null, null, null);

        // B 进 40 售 5：售罄率 12.5%（<35%，进货 ≥12）→ 滞销；剩 35 件最多 → 库存很多第一
        Assert.Equal("B", Assert.Single(report.SlowSellers).ProductCode);
        Assert.Equal("B", report.Overstock[0].ProductCode);
        Assert.Equal("A", report.BestSellers[0].ProductCode);
    }

    [Fact]
    public void Build_ProductBoards_CapAtListSizeAndKeepOrder()
    {
        // 60 款商品都有销售、都进了货且卖得很差：四张榜里除「提前卖完」外都应只取前 50，并按各自的排序键降序
        var products = Enumerable.Range(1, 60)
            .Select(i => new HolidayProductInfo($"P{i:00}", $"XH{i:0000}", $"Item {i}", null, HolidaySalesSuppliers.Kfc, "Decorations"))
            .ToList();
        var sales = products.Select((p, i) => Sale("1013", new DateTime(2026, 10, 20), p.ProductCode, 1, 10m + i)).ToList();
        var purchases = products.Select(p => Buy("1013", p.ProductCode, 20, 60m)).ToList();
        var data = new HolidaySalesDataset
        {
            Year = 2026,
            HolidayDate = Holiday,
            SalesFrom = new DateTime(2026, 8, 1),
            SalesThrough = new DateTime(2026, 11, 3),
            Products = products,
            Sales = sales,
            Purchases = purchases,
            StoreNames = new Dictionary<string, string> { ["1013"] = "Orion" },
        };

        var report = HolidaySalesReportBuilder.Build(data, null, null, null);

        Assert.Equal(HolidaySalesReportBuilder.ListSize, report.BestSellers.Count);
        Assert.Equal("P60", report.BestSellers[0].ProductCode); // 销售额最高的排第一（10 + 59）
        Assert.Equal(report.BestSellers.Select(p => p.Amount).OrderByDescending(a => a), report.BestSellers.Select(p => p.Amount));
        Assert.Equal(HolidaySalesReportBuilder.ListSize, report.SlowSellers.Count); // 进货 20 件只卖 1 件：售罄率 5%
        Assert.Equal(HolidaySalesReportBuilder.ListSize, report.Overstock.Count);
        Assert.Equal(60, report.Totals.SkuCount); // 榜单截断不影响合计
    }

    [Fact]
    public void BuildProductStores_ListsEveryStoreWithStockOrSales()
    {
        // 1013：进 100 卖 95（10/25 卖断，提前 6 天）；1012：进 100 只卖 10；1004：没进货却卖了 20；1020：只进货没销售
        var data = Dataset(
            sales:
            [
                Sale("1013", new DateTime(2026, 10, 20), "A", 50, 500m),
                Sale("1013", new DateTime(2026, 10, 25), "A", 45, 450m),
                Sale("1012", new DateTime(2026, 10, 22), "A", 10, 100m),
                Sale("1004", new DateTime(2026, 10, 21), "A", 20, 200m),
                Sale("1013", new DateTime(2026, 10, 20), "B", 5, 50m),
            ],
            purchases: [Buy("1013", "A", 100, 300m), Buy("1012", "A", 100, 300m), Buy("1020", "A", 12, 36m), Buy("1013", "B", 9, 27m)]);

        var result = HolidaySalesReportBuilder.BuildProductStores(data, null, "a")!; // 商品编码忽略大小写

        Assert.Equal("A", result.ProductCode);
        Assert.Equal(new[] { "1013", "1004", "1012", "1020" }, result.Stores.Select(s => s.BranchCode)); // 按销售额降序，没销售的排最后
        var orion = result.Stores[0];
        Assert.Equal("Orion", orion.BranchName);
        Assert.Equal(0.95m, orion.SellThrough);
        Assert.Equal("2026-10-25", orion.SoldOutDate);
        Assert.Equal(6, orion.DaysEarly);
        Assert.Equal("2026-10-20", orion.FirstSaleDate);
        Assert.Equal(15m, orion.LeftoverCost); // 剩 5 件 × 进价 3
        Assert.Null(result.Stores[1].SellThrough); // 1004 没有进货：售罄率不适用
        Assert.Null(result.Stores[1].SoldOutDate);
        Assert.Null(result.Stores[3].FirstSaleDate); // 1020 只进货没销售
        Assert.Equal(12m, result.Stores[3].LeftoverQuantity);
        // 合计的售罄率只算有进货的店：(95 + 10 + 0) / (100 + 100 + 12)
        Assert.Equal(Math.Round(105m / 212m, 4), result.Total.SellThrough);
        Assert.Equal(3, result.StoreCount);
        Assert.Equal(1, result.SoldOutStoreCount);
    }

    [Fact]
    public void BuildProductStores_RespectsBranchScopeAndUnknownProduct()
    {
        var data = Dataset(
            sales: [Sale("1013", new DateTime(2026, 10, 20), "A", 5, 50m), Sale("1012", new DateTime(2026, 10, 20), "A", 3, 30m)],
            purchases: [Buy("1013", "A", 10, 30m), Buy("1012", "A", 10, 30m)]);

        var scoped = HolidaySalesReportBuilder.BuildProductStores(data, ["1012"], "A")!;

        Assert.Equal("1012", Assert.Single(scoped.Stores).BranchCode);
        Assert.Equal(30m, scoped.Total.Amount);
        Assert.Empty(HolidaySalesReportBuilder.BuildProductStores(data, Array.Empty<string>(), "A")!.Stores);
        Assert.Null(HolidaySalesReportBuilder.BuildProductStores(data, null, "NOPE"));
    }

    [Fact]
    public void BuildCategoryDetail_ReturnsTotalsStoresAndFullProductList()
    {
        // A（服装）：1013 进 100 卖 95（节前卖断）、1012 进 100 卖 10；B（装饰）不属于本大类
        var data = Dataset(
            sales:
            [
                Sale("1013", new DateTime(2026, 10, 20), "A", 50, 500m),
                Sale("1013", new DateTime(2026, 10, 25), "A", 45, 450m),
                Sale("1012", new DateTime(2026, 10, 22), "A", 10, 100m),
                Sale("1013", new DateTime(2026, 11, 2), "A", 1, 10m),
                Sale("1013", new DateTime(2026, 10, 20), "B", 7, 70m),
            ],
            purchases: [Buy("1013", "A", 100, 300m), Buy("1012", "A", 100, 300m), Buy("1013", "B", 9, 27m)]);

        var detail = HolidaySalesReportBuilder.BuildCategoryDetail(data, null, null, null, HolidaySalesCategoryRules.Costume)!;

        Assert.Equal(HolidaySalesCategoryRules.Costume, detail.Category.Key);
        Assert.Equal(1060m, detail.Category.Amount); // 只含 A：500 + 450 + 100 + 10
        Assert.Equal("A", Assert.Single(detail.Products).ProductCode);
        Assert.Equal(new[] { "1013", "1012" }, detail.Stores.Select(s => s.BranchCode)); // 按销售额降序
        Assert.Equal(1, detail.Stores[0].EarlySoldOutCount);
        Assert.Equal(10m, detail.Stores[0].AfterHolidayAmount); // 节后 11/2 的销售
        Assert.Null(detail.BranchCode);
    }

    [Fact]
    public void BuildCategoryDetail_SingleStoreHasNoStoreListAndOnlyThatStoreProducts()
    {
        var data = Dataset(
            sales: [Sale("1013", new DateTime(2026, 10, 20), "A", 5, 50m), Sale("1012", new DateTime(2026, 10, 20), "A", 3, 30m)],
            purchases: [Buy("1013", "A", 10, 30m), Buy("1012", "A", 10, 30m)]);

        var detail = HolidaySalesReportBuilder.BuildCategoryDetail(data, null, "1012", null, HolidaySalesCategoryRules.Costume)!;

        Assert.Equal("1012", detail.BranchCode);
        Assert.Equal("Glendale", detail.BranchName);
        Assert.Empty(detail.Stores);
        Assert.Equal(30m, detail.Category.Amount);
        Assert.Equal(30m, Assert.Single(detail.Products).Amount);
    }

    [Fact]
    public void BuildCategoryDetail_UnknownCategoryReturnsNullAndScopeIsRespected()
    {
        var data = Dataset(
            sales: [Sale("1013", new DateTime(2026, 10, 20), "A", 5, 50m), Sale("1012", new DateTime(2026, 10, 20), "A", 3, 30m)],
            purchases: []);

        Assert.Null(HolidaySalesReportBuilder.BuildCategoryDetail(data, null, null, null, "Nope"));
        var scoped = HolidaySalesReportBuilder.BuildCategoryDetail(data, ["1012"], null, null, HolidaySalesCategoryRules.Costume)!;
        Assert.Equal("1012", Assert.Single(scoped.Stores).BranchCode);
        Assert.Equal(30m, scoped.Category.Amount);
        var supplier = HolidaySalesReportBuilder.BuildCategoryDetail(data, null, null, HolidaySalesSuppliers.Brazco, HolidaySalesCategoryRules.Costume)!;
        Assert.Empty(supplier.Products); // A 是 KFC 的，按 Brazco 过滤后没有
    }

    [Fact]
    public void Build_SupplierFilter_OnlyKeepsThatSupplier()
    {
        var data = Dataset(
            sales: [Sale("1013", new DateTime(2026, 10, 20), "A", 10, 100m), Sale("1013", new DateTime(2026, 10, 20), "B", 5, 50m)],
            purchases: []);

        var report = HolidaySalesReportBuilder.Build(data, null, null, HolidaySalesSuppliers.Brazco);

        Assert.Equal(50m, report.Totals.Amount);
        Assert.Equal(1, report.ProductCount);
    }

    [Fact]
    public void BuildCategoryMap_GroupsSupplierCategoriesUnderBigCategory()
    {
        var data = Dataset(
            sales: [Sale("1013", new DateTime(2026, 10, 20), "A", 10, 100m)],
            purchases: []);

        var map = HolidaySalesReportBuilder.BuildCategoryMap(data, null);

        var costume = map.Groups.Single(g => g.Key == HolidaySalesCategoryRules.Costume);
        Assert.Equal(100m, costume.Amount);
        Assert.Equal("Kids costumes", Assert.Single(costume.Entries).SupplierCategory);
        Assert.Contains(map.Groups, g => g.Key == HolidaySalesCategoryRules.Decoration);
    }

    private static HolidaySalesDataset Dataset(
        IReadOnlyList<HolidaySalesRow> sales,
        IReadOnlyList<HolidayPurchaseRow> purchases
    ) => new()
    {
        Year = 2026,
        HolidayDate = Holiday,
        SalesFrom = new DateTime(2026, 8, 1),
        SalesThrough = new DateTime(2026, 11, 3),
        Products =
        [
            new HolidayProductInfo("A", "XH0001", "Costume Pirate Kids", "https://img/A.jpg", HolidaySalesSuppliers.Kfc, "Kids costumes"),
            new HolidayProductInfo("B", "HW0002", "Hanging Ghost 90cm", null, HolidaySalesSuppliers.Brazco, "Hanging Decoration"),
        ],
        Sales = sales,
        Purchases = purchases,
        StoreNames = new Dictionary<string, string> { ["1013"] = "Orion", ["1012"] = "Glendale", ["1004"] = "Campbelltown" },
    };

    private static HolidaySalesRow Sale(string branch, DateTime date, string product, decimal qty, decimal amount) =>
        new(branch, date, product, qty, amount);

    private static HolidayPurchaseRow Buy(string branch, string product, decimal qty, decimal cost) =>
        new(branch, product, qty, cost);
}
