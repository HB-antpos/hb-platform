using BlazorApp.Api.Features.LegacyEmployeeLogs;
using BlazorApp.Api.Features.LegacyEmployeeLogs.Risk;
using BlazorApp.Shared.DTOs;
using Xunit;
using C = BlazorApp.Api.Features.LegacyEmployeeLogs.Risk.LegacyEmployeeLogRiskCatalog;

namespace BlazorApp.Api.Tests;

/// <summary>异常规则纯函数判定：详情文本样例取自 2026-10-01 生产抽样（1003/1005/1008）。</summary>
public sealed class LegacyEmployeeLogRiskEvaluatorTests
{
    private static readonly DateTime Day = new(2026, 9, 30);
    private static readonly LegacyEmployeeLogRiskOptions Options = new();
    private int _sequence;

    private LegacyLogRow Row(string operation, string time, string? detail = null, string device = "POS_1005_0908", string employee = "E-TOBY", string? id = null) =>
        new(id ?? $"L{++_sequence:D4}", "1005", device, employee, employee, operation, Day + TimeSpan.Parse(time), detail);

    private static LegacyRiskEvaluation Evaluate(IEnumerable<LegacyLogRow> rows, string from = "00:00:00", string to = "1.00:00:00") =>
        LegacyEmployeeLogRiskEvaluator.Evaluate(
            new LegacyRiskEvaluationInput("1005", Day + TimeSpan.Parse(from), Day + TimeSpan.Parse(to), rows.ToList()),
            Options);

    private static List<LegacyRiskFlag> Flags(LegacyRiskEvaluation evaluation, string rule) =>
        evaluation.Flags.Where(flag => flag.RuleCode == rule).ToList();

    private static string Delete(decimal total, string product = "Kids Luigi hat") =>
        $"从购物车删除商品:{product}，编码:PP97640，数量:1，单价:{total:0.00}，总金额:{total:0.00}";

    [Fact]
    public void 开钱箱_前后两分钟内有结账不算_超出窗口才标记并记录身份确认与上一次结账()
    {
        var rows = new[]
        {
            Row(C.Checkout, "10:00:00"),
            Row(C.IdentityConfirm, "10:01:30"),
            Row(C.OpenDrawer, "10:01:30", id: "IN-SALE"),
            Row(C.IdentityConfirm, "10:15:00"),
            Row(C.OpenDrawer, "10:15:00", id: "NO-SALE"),
            Row(C.Checkout, "10:17:01"),
        };

        var flags = Flags(Evaluate(rows), C.Rules.NoSaleDrawer);

        var flag = Assert.Single(flags);
        Assert.Equal("NO-SALE", flag.Target.Id);
        Assert.Equal("10:00:00", flag.Evidence["previousCheckoutAt"]);
        Assert.Equal("15", flag.Evidence["minutesSincePreviousCheckout"]);
        Assert.Equal("10:17:01", flag.Evidence["nextCheckoutAt"]);
        Assert.Equal("true", flag.Evidence["identityConfirmed"]);
    }

    [Fact]
    public void 开钱箱_只看同一设备的结账()
    {
        var rows = new[]
        {
            Row(C.Checkout, "10:00:00", device: "POS_1005_1005"),
            Row(C.OpenDrawer, "10:00:30", id: "OTHER-DEVICE-CHECKOUT"),
        };

        var flag = Assert.Single(Flags(Evaluate(rows), C.Rules.NoSaleDrawer));
        Assert.Equal("false", flag.Evidence["identityConfirmed"]);
        Assert.False(flag.Evidence.ContainsKey("previousCheckoutAt"));
    }

    [Fact]
    public void 结账后删除_金额达标且其间没有开始下一单才标记()
    {
        var rows = new[]
        {
            Row(C.Checkout, "11:00:00"),
            Row(C.DeleteItem, "11:00:19", Delete(10.00m), id: "HIT"),
            Row(C.DeleteItem, "11:00:25", Delete(9.99m), id: "BELOW-MIN"),
            Row(C.Checkout, "12:00:00"),
            Row(C.AddItem, "12:00:10"),
            Row(C.DeleteItem, "12:00:20", Delete(30m), id: "NEXT-ORDER"),
            Row(C.Checkout, "13:00:00"),
            Row(C.DeleteItem, "13:02:01", Delete(30m), id: "TOO-LATE"),
        };

        var flag = Assert.Single(Flags(Evaluate(rows), C.Rules.DeleteAfterCheckout));
        Assert.Equal("HIT", flag.Target.Id);
        Assert.Equal("19", flag.Evidence["secondsAfterCheckout"]);
        Assert.Equal("10.00", flag.Evidence["deletedAmount"]);
        Assert.Equal("Kids Luigi hat", flag.Evidence["product"]);
    }

    [Fact]
    public void 大额折扣_单品整单折扣与改价降幅分别按阈值判定()
    {
        var rows = new[]
        {
            Row(C.ChangeDiscount, "09:00:00", "商品:Piggy Bank，原折扣:0.0%，新折扣:50%", id: "ITEM-50"),
            Row(C.ChangeDiscount, "09:01:00", "商品:Red bull，原折扣:0.0%，新折扣:40%"),
            Row(C.ChangeAllDiscount, "09:02:00", "将所有商品折扣率设置为:80%，购物车商品数:6，原总金额:17.55", id: "CART-80"),
            Row(C.ChangeAllDiscount, "09:03:00", "将所有商品折扣率设置为:30%，购物车商品数:1，原总金额:4.00"),
            Row(C.ChangePrice, "09:04:00", "商品:Double Side Form Tape 18Mm*15m，原价格:2.99，新价格:1.49", id: "PRICE-HALF"),
            Row(C.ChangePrice, "09:05:00", "商品:380g Canvas Frame 40*60*3.5cm，原价格:14.99，新价格:12.99"),
        };

        var flags = Flags(Evaluate(rows), C.Rules.BigDiscount).ToDictionary(flag => flag.Target.Id);

        Assert.Equal(new[] { "CART-80", "ITEM-50", "PRICE-HALF" }, flags.Keys.Order());
        Assert.Equal("item", flags["ITEM-50"].Evidence["kind"]);
        Assert.Equal("0", flags["ITEM-50"].Evidence["previousPercent"]);
        Assert.Equal("Piggy Bank", flags["ITEM-50"].Evidence["product"]);
        Assert.Equal("17.55", flags["CART-80"].Evidence["originalTotal"]);
        Assert.Equal("6", flags["CART-80"].Evidence["itemCount"]);
        Assert.Equal("50.2", flags["PRICE-HALF"].Evidence["percent"]);
    }

    [Fact]
    public void 频繁删除_十分钟内第八次删除标记一次_整段延续不重复标记()
    {
        var rows = Enumerable.Range(0, 10)
            .Select(index => Row(C.DeleteItem, $"14:{index:D2}:00", Delete(2m), id: $"D{index}"))
            .Append(Row(C.DeleteItem, "14:09:30", Delete(2m), employee: "E-OTHER"))
            .ToList();

        var flag = Assert.Single(Flags(Evaluate(rows), C.Rules.BurstDelete));
        Assert.Equal("D7", flag.Target.Id);
        Assert.Equal("10", flag.Evidence["count"]);
        Assert.Equal("14:00:00", flag.Evidence["firstAt"]);
        Assert.Equal("14:09:00", flag.Evidence["lastAt"]);
        Assert.Equal("20.00", flag.Evidence["totalAmount"]);
    }

    [Fact]
    public void 频繁删除_七次或超出窗口都不标记_两段分别标记()
    {
        var seven = Enumerable.Range(0, 7).Select(index => Row(C.DeleteItem, $"08:{index:D2}:00", Delete(1m)));
        var spread = Enumerable.Range(0, 8).Select(index => Row(C.DeleteItem, $"09:{index * 2:D2}:00", Delete(1m)));
        Assert.Empty(Flags(Evaluate(seven.Concat(spread)), C.Rules.BurstDelete));

        var first = Enumerable.Range(0, 8).Select(index => Row(C.DeleteItem, $"15:{index:D2}:00", Delete(1m)));
        var second = Enumerable.Range(0, 8).Select(index => Row(C.DeleteItem, $"16:{index:D2}:00", Delete(1m)));
        Assert.Equal(2, Flags(Evaluate(first.Concat(second)), C.Rules.BurstDelete).Count);
    }

    [Fact]
    public void 频繁删除_目标早于评估窗口时不在本窗口重复标记()
    {
        // 14:00–14:07 已满 8 次（目标 14:07），14:08、14:09 延续同一段：只评估 14:08 之后时不能把 14:08 当成新一段的起点。
        var rows = Enumerable.Range(0, 10).Select(index => Row(C.DeleteItem, $"14:{index:D2}:00", Delete(1m))).ToList();

        Assert.Empty(Flags(Evaluate(rows, from: "14:08:00"), C.Rules.BurstDelete));
    }

    [Fact]
    public void 重复重打印_同一订单当天第三次标记()
    {
        const string order = "01A0F5BF-C743-7437-AAA2-A9E53941770F";
        var rows = new[]
        {
            Row(C.Reprint, "10:00:00", $"重打印订单:{order}，金额:14.96"),
            Row(C.Reprint, "10:05:00", $"重打印订单:{order.ToLowerInvariant()}，金额:14.96"),
            Row(C.Reprint, "10:06:00", "重打印订单:01A0F5BB-5D5D-75D4-90EC-70C24F64BEB0，金额:24.95"),
            Row(C.Reprint, "11:00:00", $"重打印订单:{order}，金额:14.96", id: "THIRD"),
            Row(C.Reprint, "11:30:00", $"重打印订单:{order}，金额:14.96"),
        };

        var flag = Assert.Single(Flags(Evaluate(rows), C.Rules.RepeatReprint));
        Assert.Equal("THIRD", flag.Target.Id);
        Assert.Equal(order, flag.Evidence["orderId"]);
        Assert.Equal("4", flag.Evidence["count"]);
        Assert.Equal("14.96", flag.Evidence["amount"]);
    }

    [Fact]
    public void 非营业时间_每台设备开门前和打烊后各标第一条()
    {
        var rows = new[]
        {
            Row(C.AddItem, "06:40:00", id: "EARLY-1"),
            Row(C.AddItem, "06:50:00"),
            Row(C.AddItem, "07:00:00"),
            Row(C.AddItem, "21:59:59"),
            Row(C.AddItem, "22:00:00", id: "LATE-1"),
            Row(C.AddItem, "06:45:00", device: "POS_1005_1005", id: "OTHER-DEVICE"),
        };

        var flags = Flags(Evaluate(rows), C.Rules.OffHours).ToDictionary(flag => flag.Target.Id);

        Assert.Equal(new[] { "EARLY-1", "LATE-1", "OTHER-DEVICE" }, flags.Keys.Order());
        Assert.Equal("beforeOpen", flags["EARLY-1"].Evidence["segment"]);
        Assert.Equal("2", flags["EARLY-1"].Evidence["count"]);
        Assert.Equal("afterClose", flags["LATE-1"].Evidence["segment"]);
    }

    [Fact]
    public void 只标记评估窗口内的记录()
    {
        var rows = new[]
        {
            Row(C.OpenDrawer, "09:59:59", id: "BEFORE"),
            Row(C.OpenDrawer, "10:00:00", id: "INSIDE"),
            Row(C.OpenDrawer, "11:00:00", id: "AT-END"),
        };

        var flag = Assert.Single(Flags(Evaluate(rows, from: "10:00:00", to: "11:00:00"), C.Rules.NoSaleDrawer));
        Assert.Equal("INSIDE", flag.Target.Id);
    }

    [Fact]
    public void 金额影响_删除改价整单折扣无小票退货可算_单品折扣算不出()
    {
        Assert.Equal(6.99m, LegacyEmployeeLogDetailParser.AmountImpact(C.DeleteItem, Delete(6.99m)));
        Assert.Equal(2.00m, LegacyEmployeeLogDetailParser.AmountImpact(C.ChangePrice, "商品:380g Canvas Frame 40*60*3.5cm，原价格:14.99，新价格:12.99"));
        Assert.Equal(1.76m, LegacyEmployeeLogDetailParser.AmountImpact(C.ChangeAllDiscount, "将所有商品折扣率设置为:10%，购物车商品数:6，原总金额:17.55"));
        Assert.Equal(2.50m, LegacyEmployeeLogDetailParser.AmountImpact(C.NoReceiptReturn, "商品:Festive kids socks，编码:9333527762689，原数量:1，新数量:-1，单价:2.50"));
        Assert.Null(LegacyEmployeeLogDetailParser.AmountImpact(C.ChangeDiscount, "商品:Piggy Bank，原折扣:0.0%，新折扣:10%"));
        // 改价上调不是让利。
        Assert.Null(LegacyEmployeeLogDetailParser.AmountImpact(C.ChangePrice, "商品:X，原价格:2.00，新价格:5.00"));
    }

    [Fact]
    public void 危险操作清单不含重打印()
    {
        Assert.False(C.IsDanger(C.Reprint));
        Assert.True(C.IsDanger(" 开钱箱 "));
        Assert.Equal(7, C.DangerOperations.Count);
    }

    [Fact]
    public void 序列余量覆盖连续删除两倍窗口()
    {
        Assert.Equal(TimeSpan.FromSeconds(1260), LegacyEmployeeLogRiskEvaluator.SequenceMargin(Options));
    }

    [Fact]
    public void 扫描窗口_常规轮回看六小时_到点且当天未跑时按天切深度窗口()
    {
        var now = new DateTime(2026, 10, 1, 5, 0, 0, DateTimeKind.Utc);

        var quick = LegacyEmployeeLogRiskScanWorker.BuildWindows(now, Options, null, out var deep);
        Assert.False(deep);
        var (from, to) = Assert.Single(quick);
        Assert.Equal(new DateTime(2026, 10, 1, 9, 0, 0), from);
        Assert.Equal(new DateTime(2026, 10, 1, 17, 0, 0), to);

        var deepAt = new DateTime(2026, 10, 1, 17, 30, 0, DateTimeKind.Utc);
        var windows = LegacyEmployeeLogRiskScanWorker.BuildWindows(deepAt, Options, null, out deep);
        Assert.True(deep);
        Assert.Equal(8, windows.Count);
        Assert.Equal((new DateTime(2026, 9, 25), new DateTime(2026, 9, 26)), windows[0]);
        Assert.Equal(new DateTime(2026, 10, 2), windows[^1].From);

        LegacyEmployeeLogRiskScanWorker.BuildWindows(deepAt, Options, deepAt.Date, out deep);
        Assert.False(deep);
    }

    [Fact]
    public void Normalize_风险入口规则与核查状态都是封闭取值()
    {
        LegacyEmployeeLogQueryDto Request() => new()
        {
            StoreCodes = ["1005"],
            From = Day,
            To = Day.AddDays(1),
        };

        var defaults = LegacyEmployeeLogSqlServerQuery.Normalize(Request()).Query!;
        Assert.Equal(LegacyEmployeeLogSqlServerQuery.LensAll, defaults.RiskLens);
        Assert.Equal(LegacyEmployeeLogSqlServerQuery.ReviewAll, defaults.ReviewStatus);

        var request = Request();
        request.RiskLens = "ABNORMAL";
        request.RuleCodes = ["nosaledrawer", " burstDelete "];
        request.ReviewStatus = "Pending";
        var query = LegacyEmployeeLogSqlServerQuery.Normalize(request).Query!;
        Assert.Equal(LegacyEmployeeLogSqlServerQuery.LensAbnormal, query.RiskLens);
        Assert.Equal(new[] { C.Rules.NoSaleDrawer, C.Rules.BurstDelete }, query.RuleCodeList);
        Assert.Equal(LegacyEmployeeLogSqlServerQuery.ReviewPending, query.ReviewStatus);

        request = Request();
        request.RiskLens = "risky";
        Assert.Equal("风险入口无效", LegacyEmployeeLogSqlServerQuery.Normalize(request).Error);
        request = Request();
        request.RuleCodes = ["unknown"];
        Assert.Equal("异常规则条件无效", LegacyEmployeeLogSqlServerQuery.Normalize(request).Error);
        request = Request();
        request.ReviewStatus = "done";
        Assert.Equal("核查状态条件无效", LegacyEmployeeLogSqlServerQuery.Normalize(request).Error);
    }

    [Fact]
    public void BuildList_异常入口只收窄当前页与总数_总数放在最后一个结果集()
    {
        var request = new LegacyEmployeeLogQueryDto
        {
            StoreCodes = ["1005"],
            From = Day,
            To = Day.AddDays(1),
            RiskLens = "abnormal",
            RuleCodes = [C.Rules.NoSaleDrawer],
            ReviewStatus = "pending",
        };
        var command = LegacyEmployeeLogSqlServerQuery.BuildList(LegacyEmployeeLogSqlServerQuery.Normalize(request).Query!);

        Assert.Contains("[dbo].[LegacyEmployeeLogFlags]", command.Sql);
        Assert.Contains("f.[RuleCode] IN (@Rule0)", command.Sql);
        Assert.Contains("AND NOT EXISTS (SELECT 1 FROM [dbo].[LegacyEmployeeLogReviews]", command.Sql);
        Assert.EndsWith("SELECT [Rows] FROM @LensCount;", command.Sql.TrimEnd());
        Assert.All(command.Parameters.Where(parameter => parameter.Value is string), parameter =>
            Assert.Equal(System.Data.DbType.AnsiString, parameter.DbType));
        // 按操作类型计数不带异常条件，保持「全部」口径。
        var countStatement = command.Sql[command.Sql.IndexOf("SELECT l.[Operation], COUNT_BIG(*)", StringComparison.Ordinal)..];
        countStatement = countStatement[..countStatement.IndexOf("GROUP BY", StringComparison.Ordinal)];
        Assert.DoesNotContain("LegacyEmployeeLogFlags", countStatement);

        request.RiskLens = "all";
        Assert.DoesNotContain("LegacyEmployeeLogFlags", LegacyEmployeeLogSqlServerQuery.BuildList(LegacyEmployeeLogSqlServerQuery.Normalize(request).Query!).Sql);
    }

    [Fact]
    public void BuildList_异常入口带关键字时在临时表上过滤()
    {
        var request = new LegacyEmployeeLogQueryDto
        {
            StoreCodes = ["1005"],
            From = Day,
            To = Day.AddDays(1),
            RiskLens = "abnormal",
            Keyword = "xmas",
        };
        var sql = LegacyEmployeeLogSqlServerQuery.BuildList(LegacyEmployeeLogSqlServerQuery.Normalize(request).Query!).Sql;

        Assert.Contains("WHERE f.[LogId] = h.[Id]", sql);
        // 开头有一句「临时表已存在先删除」，要和末尾真正的清理比较。
        Assert.True(sql.IndexOf("INSERT INTO @LensCount", StringComparison.Ordinal) < sql.LastIndexOf("DROP TABLE #hb_legacy_log_hits", StringComparison.Ordinal));
    }
}
