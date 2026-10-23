using BlazorApp.Api.Services.React.SupplierPreorders;
using BlazorApp.Shared.Models;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 供应商节日预订单 PR 2 纯规则：节日关键日期、历史窗口、快照计算（进货批次、销量区间、售罄率、折扣区间合并）、
/// 参考商品打分（Dice 70% + 进价 30%、两道硬门槛、≥0.6 自动带上）、参考取舍与后台任务到期判定。
/// </summary>
public sealed class SupplierPreorderHistoryRulesTests
{
    private static DateTime D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void 关键日期_黑五是感恩节次日_复活节沿用贺卡算法_窗口外的不返回()
    {
        Assert.Equal(D(2025, 11, 28), SupplierPreorderKeyDates.BlackFridayOf(2025));
        Assert.Equal(D(2024, 11, 29), SupplierPreorderKeyDates.BlackFridayOf(2024));
        Assert.Equal(D(2026, 11, 27), SupplierPreorderKeyDates.BlackFridayOf(2026));
        Assert.Equal(D(2026, 4, 5), SupplierPreorderKeyDates.EasterSundayOf(2026));
        Assert.Equal(D(2025, 4, 20), SupplierPreorderKeyDates.EasterSundayOf(2025));

        var christmasWindow = SupplierPreorderKeyDates.InRange(D(2025, 9, 1), D(2026, 1, 18));
        Assert.Equal(
            new[] { "Halloween", "BlackFriday", "ChristmasEve", "Christmas", "BoxingDay" },
            christmasWindow.Select(k => k.Key));
        Assert.Equal(D(2025, 10, 31), christmasWindow[0].Date);

        var halloweenWindow = SupplierPreorderKeyDates.InRange(D(2025, 8, 1), D(2025, 11, 7));
        Assert.Equal(new[] { "Halloween" }, halloweenWindow.Select(k => k.Key));
        Assert.Empty(SupplierPreorderKeyDates.InRange(D(2026, 1, 2), D(2025, 1, 1)));
    }

    [Fact]
    public void 历史窗口_默认与前端一致_填一头用默认补另一头_超长或倒置不算()
    {
        Assert.Equal((D(2025, 9, 1), D(2026, 1, 18)), SupplierPreorderHistoryRules.DefaultWindow("Christmas", 2026));
        Assert.Equal((D(2025, 8, 1), D(2025, 11, 7)), SupplierPreorderHistoryRules.DefaultWindow("Halloween", 2026));
        Assert.Null(SupplierPreorderHistoryRules.DefaultWindow("Easter", 2026));

        Assert.Equal((D(2025, 10, 1), D(2026, 1, 18)), SupplierPreorderHistoryRules.ResolveWindow(D(2025, 10, 1), null, "Christmas", 2026));
        Assert.Null(SupplierPreorderHistoryRules.ResolveWindow(null, null, "Diaries", 2026));
        Assert.Null(SupplierPreorderHistoryRules.ResolveWindow(D(2026, 1, 1), D(2025, 1, 1), "Custom", 2026));
        Assert.Null(SupplierPreorderHistoryRules.ResolveWindow(D(2024, 1, 1), D(2025, 6, 1), "Custom", 2026));
        Assert.Equal((D(2025, 2, 1), D(2025, 4, 30)), SupplierPreorderHistoryRules.ResolveWindow(D(2025, 2, 1), D(2025, 4, 30), "Easter", 2026));
    }

    [Fact]
    public void 快照计算_同单合并为一批_窗口外与零数量剔除_首末日期售罄率_分店大小写空格归一()
    {
        var from = D(2025, 9, 1);
        var to = D(2026, 1, 18);
        var purchases = new[]
        {
            new SupplierPreorderPurchaseFact("1001", "P-A", D(2025, 9, 21), "warehouse", 120, "WO-1"),
            new SupplierPreorderPurchaseFact("1001", "P-A", D(2025, 9, 21), "warehouse", 120, "WO-1"), // 同一单两行
            new SupplierPreorderPurchaseFact("1001 ", "p-a", D(2025, 11, 14), "supplier", 96, "INV-9"),
            new SupplierPreorderPurchaseFact("1001", "P-A", D(2025, 8, 31), "warehouse", 999, "WO-0"),  // 窗口外
            new SupplierPreorderPurchaseFact("1001", "P-A", D(2025, 10, 1), "warehouse", 0, "WO-Z"),    // 零
        };
        var sales = new[]
        {
            Sale("1001", "P-A", D(2025, 10, 3), 5, regular: 5),
            Sale("1001", "P-A", D(2025, 12, 24), 15, regular: 15, original: 4m),
            Sale("1001", "P-A", D(2025, 12, 27), 10, discount: 10, discountPrice: 2m),
            Sale("1001", "P-A", D(2026, 1, 2), -1, unknown: -1),              // 只有退货的一天
            Sale("1002", "P-A", D(2025, 12, 1), 7, regular: 7),
        };

        var result = SupplierPreorderHistoryRules.Build(from, to, sales, purchases);
        var orion = result[("1001", "P-A")];
        Assert.Equal(2, orion.PurchaseBatches);
        Assert.Equal(336m, orion.PurchaseQty);
        Assert.Equal(240m, orion.WarehouseQty);
        Assert.Equal(96m, orion.SupplierQty);
        Assert.Equal(D(2025, 9, 21), orion.PurchaseFirst);
        Assert.Equal(D(2025, 11, 14), orion.PurchaseLast);
        Assert.Equal(29m, orion.SalesQty);
        Assert.Equal(D(2025, 10, 3), orion.SalesFirst);
        Assert.Equal(D(2025, 12, 27), orion.SalesLast);   // 退货日不算「最后销售」
        Assert.Equal(4, orion.Daily.Count);
        Assert.Equal(new[] { 32m, 5m, 0m }, orion.Daily[0]);  // 10-03 距 9-01 是 32 天
        Assert.Equal("partial", orion.DiscountStatus);
        Assert.Equal(10m, orion.DiscountQty);
        Assert.Equal(4m, orion.RetailPrice);
        Assert.Equal(2m, orion.DiscountPriceMin);
        Assert.Equal(0.0863m, SupplierPreorderHistoryRules.SellThrough(orion.SalesQty, orion.PurchaseQty));
        Assert.Equal(0.3333m, SupplierPreorderHistoryRules.DiscountShare(orion.DiscountQty, orion.RegularQty));

        var kawana = result[("1002", "P-A")];
        Assert.Equal(0, kawana.PurchaseBatches);
        Assert.Null(SupplierPreorderHistoryRules.SellThrough(kawana.SalesQty, kawana.PurchaseQty));
        Assert.Equal("complete", kawana.DiscountStatus);
        Assert.Equal(2, result.Count);
    }

    private static SupplierPreorderSalesFact Sale(string store, string product, DateTime date, decimal qty,
        decimal regular = 0, decimal discount = 0, decimal unknown = 0, decimal? original = null, decimal? discountPrice = null) =>
        new(store, product, date, qty, regular, discount, unknown, original, discountPrice);

    [Fact]
    public void 折扣区间_隔着无销量日最多三天合并_中间有正价销量就断开()
    {
        // 12-27→12-31 中间隔 3 天无销量：合并；12-31→01-05 隔 4 天：断开；01-05→01-08 只隔 2 天但 01-07 有正价销量：断开
        var discountDays = new[] { D(2025, 12, 26), D(2025, 12, 27), D(2025, 12, 31), D(2026, 1, 5), D(2026, 1, 8) };
        var regularDays = new[] { D(2026, 1, 7) };
        var ranges = SupplierPreorderHistoryRules.MergeDiscountRanges(discountDays, regularDays);
        Assert.Equal(3, ranges.Count);
        Assert.Equal((D(2025, 12, 26), D(2025, 12, 31)), (ranges[0].From, ranges[0].To));
        Assert.Equal((D(2026, 1, 5), D(2026, 1, 5)), (ranges[1].From, ranges[1].To));
        Assert.Equal((D(2026, 1, 8), D(2026, 1, 8)), (ranges[2].From, ranges[2].To));
    }

    [Fact]
    public void 参考箱数_销量除以箱规四舍五入_没有销量或箱规不给()
    {
        Assert.Equal(16, SupplierPreorderHistoryRules.SuggestedCartons(384, 24));
        Assert.Equal(1, SupplierPreorderHistoryRules.SuggestedCartons(13, 24));
        Assert.Null(SupplierPreorderHistoryRules.SuggestedCartons(0, 24));
        Assert.Null(SupplierPreorderHistoryRules.SuggestedCartons(10, 0));
    }

    [Fact]
    public void 分词_去颜色停用词尺寸件数_HTML实体还原()
    {
        Assert.Equal(new[] { "flocked", "rib", "glitter", "bauble" }.ToHashSet(), SupplierPreorderRefMatcher.Tokenize("Flocked Rib Glitter Bauble 10cm Asst"));
        Assert.Equal(new[] { "bottle", "bag", "embossed" }.ToHashSet(), SupplierPreorderRefMatcher.Tokenize("Bottle Bag Embossed Gold 6pk"));
        Assert.Equal(new[] { "santa", "sack" }.ToHashSet(), SupplierPreorderRefMatcher.Tokenize("Santa &amp; Sack 62x80cm Red/White Pk 2"));
        Assert.Empty(SupplierPreorderRefMatcher.Tokenize(null));
    }

    [Fact]
    public void 打分_两道硬门槛_名称不像或进价差超三成直接排除_不靠另一项凑分()
    {
        // 方案 5.2 的两个反例
        var chicken = SupplierPreorderRefMatcher.ScoreCandidate("Squeeze Screaming Chicken", 2.00m, Candidate("Party Earrings", 2.00m));
        Assert.False(chicken.Eligible);
        var nutcracker = SupplierPreorderRefMatcher.ScoreCandidate("Nutcracker 60cm", 17.14m, Candidate("Nutcracker 25cm", 4.71m));
        Assert.Equal(1m, nutcracker.NameScore);
        Assert.False(nutcracker.Eligible);

        // 同系列换款：名称 0.67、进价差约 7.8% → 0.7×0.6667 + 0.3×0.7386 ≈ 0.688，自动带上
        var bottle = SupplierPreorderRefMatcher.ScoreCandidate("Bottle Bag Embossed", 1.10m, Candidate("Bottle Bag Gold Foil", 1.02m));
        Assert.True(bottle.Eligible);
        Assert.InRange(bottle.Score, 0.6m, 0.75m);

        // 我方没有进价：进价项按中性 0.5
        var noPrice = SupplierPreorderRefMatcher.ScoreCandidate("Metal Gingerbread Bakery Sign", 3m, Candidate("Metal Gingerbread Plaque", null));
        Assert.Equal(0.5m, noPrice.PriceScore);
    }

    private static SupplierPreorderRefCandidate Candidate(string name, decimal? price, string code = "P-X", decimal sales = 10) =>
        new() { ProductCode = code, Name = name, PurchasePrice = price, SalesQty = sales };

    [Fact]
    public void 推荐_最高分不到0点6不带_同分销量高的优先_排除本表已匹配商品()
    {
        var index = new SupplierPreorderRefMatcher.CandidateIndex(new[]
        {
            Candidate("Lantern Flocked Bauble 15cm", 1.50m, "P-1", sales: 5),
            Candidate("Lantern Flocked Bauble 8cm", 1.50m, "P-2", sales: 50),
            Candidate("Glass Ornament Lantern", 1.53m, "P-3"),
        });
        var pick = index.Recommend("Lantern Flocked Bauble 12cm Asst", 1.53m);
        Assert.NotNull(pick);
        Assert.Equal("P-2", pick!.Candidate.ProductCode); // 名称同分，销量高的在前
        Assert.True(pick.Score.Score >= SupplierPreorderRefMatcher.AutoThreshold);

        var excluded = index.Recommend("Lantern Flocked Bauble 12cm Asst", 1.53m, new HashSet<string> { "P-2" });
        Assert.Equal("P-1", excluded!.Candidate.ProductCode);
        Assert.Null(index.Recommend("Musical Santa Sleigh Ride", 43.07m));
        // 「Glass Ornament Lantern」只有一个词重合（Dice 0.33），过不了名称门槛，不进前 3
        Assert.Equal(new[] { "P-2", "P-1" }, index.Top("Lantern Flocked Bauble", 1.5m, 3).Select(p => p.Candidate.ProductCode));
    }

    [Fact]
    public void 参考取舍_本品有销量用本品_否则用参考_只有进货也算本品_都没有为空()
    {
        var payloads = new Dictionary<string, SupplierPreorderHistoryPayload>
        {
            ["OWN-SOLD"] = new() { SalesQty = 3, Daily = new() { new[] { 1m, 3m, 0m } } },
            ["OWN-BOUGHT"] = new() { PurchaseBatches = 1, PurchaseQty = 24 },
        };
        SupplierPreorderHistoryPayload? Lookup(string code) => payloads.GetValueOrDefault(code);

        Assert.Equal(("Own", "OWN-SOLD"), SupplierPreorderRefDecisions.ResolveSource("OWN-SOLD", "REF", Lookup));
        Assert.Equal(("Ref", "REF"), SupplierPreorderRefDecisions.ResolveSource("OWN-BOUGHT", "REF", Lookup));
        Assert.Equal(("Own", "OWN-BOUGHT"), SupplierPreorderRefDecisions.ResolveSource("OWN-BOUGHT", null, Lookup));
        Assert.Equal(("Ref", "REF"), SupplierPreorderRefDecisions.ResolveSource(null, " REF ", Lookup));
        Assert.Null(SupplierPreorderRefDecisions.ResolveSource("NOTHING", null, Lookup));

        var sold = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "OWN-SOLD" };
        Assert.False(SupplierPreorderRefDecisions.NeedsReference("own-sold", sold));
        Assert.True(SupplierPreorderRefDecisions.NeedsReference("OWN-BOUGHT", sold));
        Assert.True(SupplierPreorderRefDecisions.NeedsReference(null, sold));
    }

    [Fact]
    public void 系统推荐写入_没变化不更新_推荐消失时撤掉()
    {
        var now = new DateTime(2026, 2, 20, 0, 0, 0, DateTimeKind.Utc);
        var item = new SupplierPreorderFormItem { Name = "Lantern", Price = 1.53m };
        var pick = new SupplierPreorderRefPick(Candidate("Lantern Flocked", 1.5m, "P-2"), new SupplierPreorderRefScore(0.8123m, 1m, 0.9m, true));

        Assert.True(SupplierPreorderRefDecisions.ApplyAuto(item, pick, now));
        Assert.Equal(("P-2", SupplierPreorderRefSources.Auto, 0.812m, "System"), (item.RefProductCode, item.RefSource, item.RefScore, item.RefSetBy));
        Assert.False(SupplierPreorderRefDecisions.ApplyAuto(item, pick, now.AddDays(1)));
        Assert.True(SupplierPreorderRefDecisions.ApplyAuto(item, null, now));
        Assert.Null(item.RefProductCode);
        Assert.Null(item.RefSource);
        Assert.False(SupplierPreorderRefDecisions.ApplyAuto(item, null, now));
    }

    [Fact]
    public void 后台任务到期_排队立即算_卡住两小时重算_进行中截止前每晚一次_草稿只算首次()
    {
        var now = new DateTime(2026, 2, 20, 1, 0, 0, DateTimeKind.Utc); // 悉尼 2/20 12:00（夏令时 +11）
        var cutoff = SupplierPreorderHistoryService.NightlyCutoffUtc(now);
        Assert.Equal(new DateTime(2026, 2, 19, 16, 0, 0, DateTimeKind.Utc), cutoff); // 悉尼 2/20 03:00

        var open = new SupplierPreorderForm { Status = SupplierPreorderFormStatuses.Open, DeadlineAtUtc = now.AddDays(10) };
        var draft = new SupplierPreorderForm { Status = SupplierPreorderFormStatuses.Draft };
        var closed = new SupplierPreorderForm { Status = SupplierPreorderFormStatuses.Closed };
        SupplierPreorderHistoryService.HistoryMeta Meta(string status, DateTime? attempt, string? rule = SupplierPreorderHistoryRules.RuleVersion) =>
            new() { Status = status, LastAttemptUtc = attempt, RuleVersion = rule };

        Assert.True(SupplierPreorderHistoryService.IsDue(draft, null, now, cutoff));
        Assert.False(SupplierPreorderHistoryService.IsDue(closed, null, now, cutoff));
        Assert.True(SupplierPreorderHistoryService.IsDue(closed, Meta("Queued", null), now, cutoff));
        Assert.False(SupplierPreorderHistoryService.IsDue(open, Meta("Running", now.AddMinutes(-30)), now, cutoff));
        Assert.True(SupplierPreorderHistoryService.IsDue(open, Meta("Running", now.AddHours(-3)), now, cutoff));
        Assert.False(SupplierPreorderHistoryService.IsDue(open, Meta("Ready", cutoff.AddMinutes(5)), now, cutoff));
        Assert.True(SupplierPreorderHistoryService.IsDue(open, Meta("Ready", cutoff.AddMinutes(-5)), now, cutoff));
        Assert.True(SupplierPreorderHistoryService.IsDue(open, Meta("Ready", cutoff.AddMinutes(5), "old-rule"), now, cutoff));
        // 失败的当天不反复重试，等下一晚或手动重算
        Assert.False(SupplierPreorderHistoryService.IsDue(open, Meta("Failed", cutoff.AddMinutes(5)), now, cutoff));
        Assert.False(SupplierPreorderHistoryService.IsDue(draft, Meta("Ready", cutoff.AddMinutes(-60)), now, cutoff));
        // 已过截止不再每晚刷新
        var expired = new SupplierPreorderForm { Status = SupplierPreorderFormStatuses.Open, DeadlineAtUtc = now.AddDays(-1) };
        Assert.False(SupplierPreorderHistoryService.IsDue(expired, Meta("Ready", cutoff.AddDays(-2)), now, cutoff));
    }
}
