using BlazorApp.Api.Features.LegacyEmployeeLogs.Risk;
using BlazorApp.Api.Services.OperationAudits.Risk;
using Xunit;
using C = BlazorApp.Api.Services.OperationAudits.Risk.PosOperationAuditRiskCatalog;

namespace BlazorApp.Api.Tests;

/// <summary>新收银七条异常规则的纯函数判定：每条规则给出命中与不命中的边界用例。</summary>
public sealed class PosOperationAuditRiskEvaluatorTests
{
    private static readonly DateTime Base = new(2026, 10, 1, 2, 0, 0, DateTimeKind.Utc);
    private static readonly LegacyEmployeeLogRiskOptions Options = new();

    private static PosAuditRiskRow Row(
        string type,
        int seconds,
        string device = "POS-1",
        string cashier = "c1",
        string outcome = C.Succeeded,
        string? orderGuid = null,
        decimal? before = null,
        decimal? after = null,
        decimal? payment = null,
        bool emergency = false,
        PosAuditRiskItem? item = null,
        string? reason = null,
        int localOffsetHours = 10
    )
    {
        var utc = Base.AddSeconds(seconds);
        return new PosAuditRiskRow(
            Guid.NewGuid(), "1013", device, cashier, "Cashier " + cashier, type, outcome, reason, orderGuid,
            utc, DateTime.SpecifyKind(utc.AddHours(localOffsetHours), DateTimeKind.Unspecified),
            BeforeActual: before, AfterActual: after, PaymentAmount: payment, IsEmergencyOverride: emergency,
            PrimaryProduct: "Widget", Item: item);
    }

    private static IReadOnlyList<PosAuditRiskFlag> Evaluate(params PosAuditRiskRow[] rows) =>
        PosOperationAuditRiskEvaluator.Evaluate(
            new PosAuditRiskInput("1013", Base.AddHours(-1), Base.AddHours(3), rows), Options);

    [Fact]
    public void 手动开钱箱前后两分钟没有销售_命中开钱箱无交易_自动开钱箱不算()
    {
        var manual = Row(C.CashDrawerOpen, 0, reason: "MANUAL");
        var auto = Row(C.CashDrawerOpen, 1000, orderGuid: "order-1", reason: "PAYMENT_COMPLETE");
        var sale = Row(C.SaleComplete, 1000 - 1, orderGuid: "order-1");

        var flags = Evaluate(manual, auto, sale).Where(flag => flag.RuleCode == C.Rules.NoSaleDrawer).ToList();

        var flag = Assert.Single(flags);
        Assert.Equal(manual.EventId, flag.Target.EventId);
        Assert.Equal("MANUAL", flag.Evidence["reason"]);
        Assert.Equal("12:16:39", flag.Evidence["nextCheckoutAt"]);
    }

    [Fact]
    public void 手动开钱箱两分钟内有退款_不命中()
    {
        var flags = Evaluate(Row(C.CashDrawerOpen, 0), Row(C.ReturnRefundComplete, 90, payment: -5m));

        Assert.DoesNotContain(flags, flag => flag.RuleCode == C.Rules.NoSaleDrawer);
    }

    [Fact]
    public void 收款开始后删除十元以上_命中_完成销售后再删不算()
    {
        var tender = Row(C.PaymentTenderAdd, 0, payment: 30m);
        var remove = Row(C.CartItemRemove, 40, before: 30m, after: 15m);
        var sale = Row(C.SaleComplete, 60, orderGuid: "o1");
        var laterRemove = Row(C.CartItemRemove, 80, before: 20m, after: 0m);

        var flags = Evaluate(tender, remove, sale, laterRemove)
            .Where(flag => flag.RuleCode == C.Rules.DeleteAfterCheckout).ToList();

        var flag = Assert.Single(flags);
        Assert.Equal(remove.EventId, flag.Target.EventId);
        Assert.Equal("15.00", flag.Evidence["deletedAmount"]);
        Assert.Equal("40", flag.Evidence["secondsAfterCheckout"]);
        Assert.Equal("tender", flag.Evidence["anchor"]);
    }

    [Fact]
    public void 收款失败后删除或金额不足十元_不命中收款后删除()
    {
        var failedTender = Row(C.PaymentTenderAdd, 0, outcome: "Failed", payment: 30m);
        var remove = Row(C.CartItemRemove, 20, before: 30m, after: 10m);
        var tender = Row(C.PaymentTenderAdd, 100, payment: 10m);
        var smallRemove = Row(C.CartItemQuantityChange, 110, before: 10m, after: 1m);

        Assert.DoesNotContain(Evaluate(failedTender, remove, tender, smallRemove), flag => flag.RuleCode == C.Rules.DeleteAfterCheckout);
    }

    [Fact]
    public void 单品五折且让利十元以上命中_让利不足十元不命中()
    {
        var big = Row(C.CartLineDiscountChange, 0, before: 40m, after: 20m,
            item: new PosAuditRiskItem("Big Bear", 1m, 40m, 40m, 0m, 20m, 40m));
        var small = Row(C.CartLineDiscountChange, 10, before: 10m, after: 5m,
            item: new PosAuditRiskItem("Small", 1m, 10m, 10m, 0m, 5m, 10m));

        var flag = Assert.Single(Evaluate(big, small).Where(flag => flag.RuleCode == C.Rules.BigDiscount));
        Assert.Equal(big.EventId, flag.Target.EventId);
        Assert.Equal("item", flag.Evidence["kind"]);
        Assert.Equal("50", flag.Evidence["percent"]);
        Assert.Equal("20.00", flag.Evidence["amount"]);
        Assert.Equal("Big Bear", flag.Evidence["product"]);
    }

    [Fact]
    public void 整单折扣与大幅降价_按比例与金额下限命中()
    {
        // 生产样例：54.89 整单打五折（折扣 27.45）；142.81 打六折（40%）不到比例阈值。
        var cart = Row(C.CartOrderDiscountChange, 0, before: 54.89m, after: 27.44m) with { BeforeDiscount = 0m, AfterDiscount = 27.45m, ProductCount = 4 };
        var cartSixty = Row(C.CartOrderDiscountChange, 5, before: 142.81m, after: 85.69m) with { BeforeDiscount = 0m, AfterDiscount = 57.12m };
        // 2.99 → 1.49 降幅 50.2%、10 件让利 15 元命中；生产里的 2.99 → 1.50 只降 49.8%，不算。
        var price = Row(C.CartItemPriceChange, 10, before: 29.90m, after: 14.90m,
            item: new PosAuditRiskItem("Socks", 10m, 2.99m, 1.49m, 0m, 0m, 14.90m));
        var almost = Row(C.CartItemPriceChange, 15, before: 29.90m, after: 15.00m,
            item: new PosAuditRiskItem("Socks", 10m, 2.99m, 1.50m, 0m, 0m, 15.00m));
        var raise = Row(C.CartItemPriceChange, 20, before: 19.47m, after: 20.97m,
            item: new PosAuditRiskItem("Up", 1m, 3.50m, 5.00m, 0m, 0m, 5.00m));

        var flags = Evaluate(cart, cartSixty, price, almost, raise).Where(flag => flag.RuleCode == C.Rules.BigDiscount).ToList();

        Assert.Equal(2, flags.Count);
        var cartFlag = flags.Single(flag => flag.Evidence["kind"] == "cart");
        Assert.Equal(cart.EventId, cartFlag.Target.EventId);
        Assert.Equal("50", cartFlag.Evidence["percent"]);
        Assert.Equal("4", cartFlag.Evidence["itemCount"]);
        var priceFlag = flags.Single(flag => flag.Evidence["kind"] == "price");
        Assert.Equal(price.EventId, priceFlag.Target.EventId);
        Assert.Equal("1.49", priceFlag.Evidence["newPrice"]);
        Assert.Equal("15.00", priceFlag.Evidence["amount"]);
    }

    [Fact]
    public void 同一收银员十分钟内删除八次_只标第八次()
    {
        var deletes = Enumerable.Range(0, 9)
            .Select(index => Row(C.CartItemRemove, index * 30, before: 5m, after: 3m))
            .ToArray();
        var other = Row(C.CartItemRemove, 5, cashier: "c2", before: 5m, after: 0m);

        var flag = Assert.Single(Evaluate([.. deletes, other]).Where(flag => flag.RuleCode == C.Rules.BurstDelete));
        Assert.Equal(deletes[7].EventId, flag.Target.EventId);
        Assert.Equal("9", flag.Evidence["count"]);
        Assert.Equal("18.00", flag.Evidence["totalAmount"]);
    }

    [Fact]
    public void 同一张小票重打印三次_标第三次_换下一单重新计数()
    {
        var sale = Row(C.SaleComplete, 0, orderGuid: "order-a", payment: 12.5m);
        var r1 = Row(C.ReceiptReprint, 10);
        var r2 = Row(C.ReceiptReprint, 20);
        var r3 = Row(C.ReceiptReprint, 30);
        var nextSale = Row(C.SaleComplete, 40, orderGuid: "order-b");
        var r4 = Row(C.ReceiptReprint, 50);
        var r5 = Row(C.ReceiptReprint, 60);

        var flag = Assert.Single(Evaluate(sale, r1, r2, r3, nextSale, r4, r5).Where(flag => flag.RuleCode == C.Rules.RepeatReprint));
        Assert.Equal(r3.EventId, flag.Target.EventId);
        Assert.Equal("order-a", flag.Evidence["orderId"]);
        Assert.Equal("12.50", flag.Evidence["amount"]);
    }

    [Fact]
    public void 营业时间外按门店墙钟判定_每段只标第一条()
    {
        // Base 02:00 UTC = 布里斯班 12:00；往前 6 小时 = 本地 06:00（开门前）。
        var early1 = Row("CASHIER_LOGIN", -6 * 3600);
        var early2 = Row(C.SaleComplete, -6 * 3600 + 60, orderGuid: "o");
        var daytime = Row(C.SaleComplete, 0, orderGuid: "o2");

        var flags = PosOperationAuditRiskEvaluator.Evaluate(
            new PosAuditRiskInput("1013", Base.AddHours(-7), Base.AddHours(1), [early1, early2, daytime]), Options)
            .Where(flag => flag.RuleCode == C.Rules.OffHours).ToList();

        var flag = Assert.Single(flags);
        Assert.Equal(early1.EventId, flag.Target.EventId);
        Assert.Equal("beforeOpen", flag.Evidence["segment"]);
        Assert.Equal("2", flag.Evidence["count"]);
    }

    [Fact]
    public void 紧急覆盖下的每条操作都命中_窗口外的不标()
    {
        var inside = Row(C.CashDrawerOpen, 0, orderGuid: "o", emergency: true, outcome: "Denied");
        var outside = Row(C.SaleComplete, 5 * 3600, orderGuid: "o", emergency: true);

        var flag = Assert.Single(Evaluate(inside, outside).Where(flag => flag.RuleCode == C.Rules.EmergencyOverride));
        Assert.Equal(inside.EventId, flag.Target.EventId);
        Assert.Equal("Denied", flag.Evidence["outcome"]);
    }

    [Theory]
    [InlineData(C.CartItemRemove, null, true)]
    [InlineData(C.CashDrawerOpen, null, true)]
    [InlineData(C.CashDrawerOpen, "order-1", false)]
    [InlineData(C.ReceiptReprint, null, false)]
    [InlineData(C.SaleComplete, "order-1", false)]
    [InlineData("TEST_SALES_DATA_RESET", null, true)]
    public void 危险操作口径_手动开钱箱算_自动开钱箱与重打印不算(string type, string? orderGuid, bool expected) =>
        Assert.Equal(expected, C.IsDanger(type, orderGuid));

    [Fact]
    public void 金额让利_购物车按前后差额_退款按绝对值_失败不计()
    {
        Assert.Equal(15m, C.AmountImpact(C.CartItemRemove, C.Succeeded, 30m, 15m, null));
        Assert.Equal(5m, C.AmountImpact(C.ReturnRefundComplete, C.Succeeded, null, null, -5m));
        Assert.Equal(-1.5m, C.AmountImpact(C.CartItemPriceChange, C.Succeeded, 19.47m, 20.97m, null));
        Assert.Null(C.AmountImpact(C.CartItemRemove, "Failed", 30m, 15m, null));
        Assert.Null(C.AmountImpact(C.SaleComplete, C.Succeeded, 30m, 0m, 30m));
    }
}
