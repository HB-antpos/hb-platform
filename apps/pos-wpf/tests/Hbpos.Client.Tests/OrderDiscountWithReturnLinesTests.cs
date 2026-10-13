using System.Globalization;
using System.Text.Json;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

/// <summary>
/// 购物车含退货行时允许设置整单折扣：折扣只作用于销售行，退货行不参与、不被分摊、保持无折扣。
/// 这涉及付款金额，所以除了购物车本身，也逐项核对下游：付款金额（现金 / 刷卡 / 券 / 混合 / 退款）、
/// 小票、订单上传请求、本地挂单、共享挂单、支付恢复快照。
///
/// 测试分两类（与 PR 描述一致）：
/// 1. 失败对照（新行为）：钉住本次的新行为，还原生产文件后应当失败。凡是「先加退货行再设整单折扣」的用例都属于这一类，
///    因为旧代码在购物车含退货行时直接拒绝整单折扣。
/// 2. 特征化：旧代码上同样通过，钉住不能变的行为——「先设整单折扣再加退货行」这条旧代码本来就走得通的顺序、
///    无退货行时的整单折扣、行级折扣对退货行的拒绝。参数化用例里 DiscountThenReturn 行是特征化，ReturnThenDiscount 行是新行为。
///
/// 本文件只使用本次改动前就存在的公开 API，便于在旧代码上编译并跑出失败对照。
/// 「只有退货行、没有可打折的销售行」时整单折扣已由产品拍板：拒绝并提示，且在授权之前拦下、数据不变。
/// 购物车层的拒绝在本文件，工作流 / 收银页命令层的提示与不弹授权见 ReturnLineDiscountFeedbackTests。
/// </summary>
public sealed class OrderDiscountWithReturnLinesTests
{
    private const string StoreCode = "S001";
    private const string OrderDiscountNoSaleLinesKey = "pos.status.orderDiscountNoSaleLines";

    /// <summary>两种顺序必须得到同样的结果：先整单折扣再加退货行 / 先加退货行再整单折扣。</summary>
    public enum BuildOrder
    {
        DiscountThenReturn,
        ReturnThenDiscount
    }

    // ===================================================================================
    // 一、购物车层：分摊口径、上限、顺序一致
    // ===================================================================================

    [Theory]
    [InlineData(BuildOrder.DiscountThenReturn)]
    [InlineData(BuildOrder.ReturnThenDiscount)]
    public void Order_discount_amount_is_allocated_across_sale_lines_only_and_return_line_stays_discount_free(BuildOrder order)
    {
        // 销售 A(10) + 销售 B(30) + 退货 R(8)，整单折扣 8：只在 A、B 之间按毛额 10:30 分摊 -> 2、6。
        // 若退货行（毛额 -8）被算进分母（合计 32），A 会分到 2.5。
        var std = BuildStandard(order, cart => cart.SetOrderDiscountAmount(8m));

        Assert.Equal(2m, std.A.DiscountAmount);
        Assert.Equal(6m, std.B.DiscountAmount);
        AssertNoDiscount(std.R);
        Assert.Equal(32m, std.Cart.TotalAmount);
        Assert.Equal(8m, std.Cart.DiscountAmount);
        // 应付 = 销售行折后合计 32 + 退货行 -8 = 24。
        Assert.Equal(24m, std.Cart.ActualAmount);
        Assert.Equal(-8m, std.R.ActualAmount);
    }

    [Theory]
    [InlineData(BuildOrder.DiscountThenReturn)]
    [InlineData(BuildOrder.ReturnThenDiscount)]
    public void Order_discount_percent_uses_sale_gross_as_base_not_net_total(BuildOrder order)
    {
        // 10% 的基数是销售行毛额合计 40（折扣 4），不是含退货行的合计 32（折扣 3.2）。
        var std = BuildStandard(order, cart => cart.SetOrderDiscountPercent(10m));

        Assert.Equal(1m, std.A.DiscountAmount);
        Assert.Equal(3m, std.B.DiscountAmount);
        AssertNoDiscount(std.R);
        Assert.Equal(4m, std.Cart.DiscountAmount);
        Assert.Equal(28m, std.Cart.ActualAmount);
    }

    [Fact]
    public void Order_discount_amount_cap_is_sale_gross_total_not_net_total()
    {
        // 销售行毛额 40，含退货行的合计只有 32。
        var std = BuildStandard(BuildOrder.ReturnThenDiscount, cart => cart.SetOrderDiscountAmount(35m));
        var cart = std.Cart;

        // 35 超过含退货行的合计 32，但没超过销售行毛额 40，必须通过。
        Assert.Equal(35m, cart.DiscountAmount);

        // 恰好等于上限也通过：两个销售行全额折扣，只剩退货行的 -8（净退款）。
        Assert.True(cart.SetOrderDiscountAmount(40m));
        Assert.Equal(10m, std.A.DiscountAmount);
        Assert.Equal(30m, std.B.DiscountAmount);
        Assert.Equal(-8m, cart.ActualAmount);
        AssertNoDiscount(std.R);

        // 超出上限、负数、百分比越界一律拒绝，且不改任何数据。
        var before = Fingerprint(cart);
        Assert.False(cart.SetOrderDiscountAmount(40.01m));
        Assert.False(cart.SetOrderDiscountAmount(-0.01m));
        Assert.False(cart.SetOrderDiscountPercent(100.01m));
        Assert.False(cart.SetOrderDiscountPercent(-1m));
        Assert.Equal(before, Fingerprint(cart));
    }

    [Fact]
    public void Order_discount_100_percent_with_return_line_leaves_only_the_refund()
    {
        var std = BuildStandard(BuildOrder.ReturnThenDiscount, cart => cart.SetOrderDiscountPercent(100m));

        Assert.Equal(10m, std.A.DiscountAmount);
        Assert.Equal(30m, std.B.DiscountAmount);
        AssertNoDiscount(std.R);
        Assert.Equal(-8m, std.Cart.ActualAmount);
    }

    [Theory]
    [InlineData(BuildOrder.DiscountThenReturn)]
    [InlineData(BuildOrder.ReturnThenDiscount)]
    public void Order_discount_zero_clears_sale_line_discounts_and_leaves_return_line_alone(BuildOrder order)
    {
        var std = BuildStandard(order, cart => cart.SetOrderDiscountAmount(8m));

        Assert.True(std.Cart.SetOrderDiscountAmount(0m));

        AssertNoDiscount(std.A);
        AssertNoDiscount(std.B);
        AssertNoDiscount(std.R);
        Assert.Equal(32m, std.Cart.ActualAmount);
        Assert.Equal(32m, std.Cart.TotalAmount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Order_discount_remainder_goes_to_last_sale_line_wherever_the_return_line_sits(int returnIndex)
    {
        // 三个 10 元的销售行，整单折扣 10：3.33 + 3.33，余数落在最后一个销售行 3.34；退货行放在哪个位置都一样。
        var cart = new PosCartService();
        var sales = new List<CartLine>();
        CartLine? returnLine = null;
        for (var i = 0; i <= 3; i++)
        {
            if (i == returnIndex)
            {
                returnLine = cart.AddReturnLine(ReturnRequest("RET-1"));
            }

            if (i < 3)
            {
                sales.Add(cart.AddItem(Item($"S{i}", 10m)));
            }
        }

        Assert.True(cart.SetOrderDiscountAmount(10m));

        Assert.Equal([3.33m, 3.33m, 3.34m], sales.Select(line => line.DiscountAmount).ToArray());
        Assert.Equal(10m, cart.DiscountAmount);
        AssertNoDiscount(returnLine!);
    }

    public static TheoryData<string> DifferentialScenarioNames =>
    [
        "plain-amount",
        "plain-percent",
        "mixed-sources-amount",
        "mixed-sources-percent",
        "promotion-amount",
        "zero-after-percent",
        "full-100-percent",
        "tiny-percent-rounding-to-zero"
    ];

    [Theory]
    [MemberData(nameof(DifferentialScenarioNames))]
    public void Order_discount_on_sale_lines_is_identical_with_or_without_a_return_line_wherever_it_is_added(string scenarioName)
    {
        // 差分对照：同样的销售行 + 同样的整单折扣，不带退货行时的结果是基准；
        // 退货行放在最前 / 中间 / 最后（先加退货行再折扣），或先折扣再加退货行，销售行的折扣金额、百分比、来源都必须与基准完全一致。
        // 场景覆盖手工行折扣、目录折扣、自动促销、自由商品、零价行，确认被整单折扣覆盖的口径没有因为退货行而变。
        var scenario = GetScenario(scenarioName);
        var (baseCart, baseLines, _) = BuildScenario(scenario, returnIndex: null, discountFirst: false);
        var expected = baseLines.Select(Signature).ToArray();
        var lineCount = scenario.Lines.Count;

        var variants = Enumerable.Range(0, lineCount + 1)
            .Select(index => (Index: (int?)index, DiscountFirst: false))
            .Append((Index: (int?)lineCount, DiscountFirst: true));
        foreach (var (index, discountFirst) in variants)
        {
            var (cart, saleLines, returnLine) = BuildScenario(scenario, index, discountFirst);

            Assert.Equal(expected, saleLines.Select(Signature).ToArray());
            AssertNoDiscount(returnLine!);
            Assert.Equal(baseCart.DiscountAmount, cart.DiscountAmount);
            // 应付 = 基准应付（销售行折后合计）+ 退货行的负数。
            Assert.Equal(baseCart.ActualAmount + returnLine!.ActualAmount, cart.ActualAmount);
        }
    }

    [Fact]
    public void Order_discount_without_return_line_keeps_the_established_allocation()
    {
        // 特征化：无退货行时整单折扣的分摊与余数口径不变（与上面差分对照的基准是同一组数字，写死以防两边一起漂）。
        var amountCart = new PosCartService();
        var amountLines = AddPlainLines(amountCart);
        Assert.True(amountCart.SetOrderDiscountAmount(5.55m));
        Assert.Equal([1.22m, 3.87m, 0.46m], amountLines.Select(line => line.DiscountAmount).ToArray());
        Assert.Equal(5.55m, amountCart.DiscountAmount);

        var percentCart = new PosCartService();
        var percentLines = AddPlainLines(percentCart);
        Assert.True(percentCart.SetOrderDiscountPercent(13.5m));
        Assert.Equal([1.05m, 3.33m, 0.40m], percentLines.Select(line => line.DiscountAmount).ToArray());
        Assert.Equal(4.78m, percentCart.DiscountAmount);
    }

    [Fact]
    public void Edits_after_order_discount_keep_fixed_line_amounts_and_never_touch_return_lines()
    {
        // 整单折扣一次性固化成各销售行的金额折扣，之后改数量 / 改价 / 加行 / 删行不会重新分摊：
        // 数量或价格变大折扣不变，变小则被夹到新毛额；新加的行没有折扣；总折扣是各行折扣之和，而不是用户当初输入的数。
        // 这是无退货行时就有的既有语义，这里用无退货行的孪生购物车逐步对照，确认含退货行时一模一样，并且退货行全程无折扣。
        var std = BuildStandard(BuildOrder.ReturnThenDiscount, cart => cart.SetOrderDiscountAmount(8m));
        var cart = std.Cart;
        var twin = new PosCartService();
        twin.AddItem(Item("A", 10m));
        twin.AddItem(Item("B", 30m));
        Assert.True(twin.SetOrderDiscountAmount(8m));
        var returnTotal = 8m;

        void AssertMatchesTwin()
        {
            Assert.Equal(
                twin.Lines.Select(Signature).ToArray(),
                cart.Lines.Where(line => !line.IsReturnLine).Select(Signature).ToArray());
            Assert.All(cart.Lines.Where(line => line.IsReturnLine), AssertNoDiscount);
            Assert.Equal(twin.DiscountAmount, cart.DiscountAmount);
            Assert.Equal(twin.ActualAmount - returnTotal, cart.ActualAmount);
        }

        CartLine Line(PosCartService c, string sku) => Assert.Single(c.Lines, line => line.LookupCode == $"LK-{sku}");

        AssertMatchesTwin();
        Assert.Equal(8m, cart.DiscountAmount);

        // 数量变大：折扣仍是固化的 2，没有重新分摊。
        Assert.True(cart.SetLineQuantity(Line(cart, "A"), 3m));
        Assert.True(twin.SetLineQuantity(Line(twin, "A"), 3m));
        Assert.Equal(2m, Line(cart, "A").DiscountAmount);
        AssertMatchesTwin();

        // 新加销售行：没有折扣。
        cart.AddItem(Item("C", 5m));
        twin.AddItem(Item("C", 5m));
        Assert.Equal(0m, Line(cart, "C").DiscountAmount);
        AssertMatchesTwin();

        // B 改价到 1：折扣 6 被夹到新毛额 1，总折扣随之缩小。
        Assert.True(cart.SetLineUnitPrice(Line(cart, "B"), 1m));
        Assert.True(twin.SetLineUnitPrice(Line(twin, "B"), 1m));
        Assert.Equal(1m, Line(cart, "B").DiscountAmount);
        Assert.Equal(3m, cart.DiscountAmount);
        AssertMatchesTwin();

        Assert.True(cart.IncreaseLine(Line(cart, "C")));
        Assert.True(twin.IncreaseLine(Line(twin, "C")));
        Assert.True(cart.DecreaseLine(Line(cart, "A")));
        Assert.True(twin.DecreaseLine(Line(twin, "A")));
        AssertMatchesTwin();

        Assert.True(cart.RemoveLine(Line(cart, "A")));
        Assert.True(twin.RemoveLine(Line(twin, "A")));
        Assert.Equal(1m, cart.DiscountAmount);
        AssertMatchesTwin();

        // 再加一个退货行：销售行不变，两个退货行都没有折扣。
        var secondReturn = cart.AddReturnLine(ReturnRequest("RET-2", productCode: "SKU-R2", lookupCode: "LK-R2", unitPrice: 6m));
        returnTotal += 6m;
        AssertNoDiscount(secondReturn);
        AssertMatchesTwin();

        // 销售行在折扣后重设整单折扣：只覆盖销售行，退货行依旧没有折扣。
        Assert.True(cart.SetOrderDiscountAmount(2m));
        Assert.True(twin.SetOrderDiscountAmount(2m));
        AssertMatchesTwin();
    }

    [Fact]
    public void Removing_every_sale_line_leaves_no_order_discount_residue_for_later_lines()
    {
        // 整单折扣没有独立于行的状态：销售行全删后购物车只剩退货行，之后再加销售行不会冒出折扣。
        var std = BuildStandard(BuildOrder.ReturnThenDiscount, cart => cart.SetOrderDiscountAmount(8m));
        Assert.True(std.Cart.RemoveLine(std.A));
        Assert.True(std.Cart.RemoveLine(std.B));

        Assert.Equal(0m, std.Cart.DiscountAmount);
        Assert.Equal(-8m, std.Cart.ActualAmount);
        AssertNoDiscount(std.R);

        var later = std.Cart.AddItem(Item("LATER", 12m));
        AssertNoDiscount(later);
        Assert.Equal(4m, std.Cart.ActualAmount);
    }

    [Fact]
    public void Order_discount_on_cart_with_only_return_lines_is_rejected_and_changes_nothing()
    {
        // 产品决定：只有退货行、没有可打折的销售行时整单折扣一律拒绝。
        // 拒绝时数据与修订号都不动（不再出现「什么都没改却通知了购物车变更」）。
        var cart = new PosCartService();
        var returnLine = cart.AddReturnLine(ReturnRequest("RET-1"));
        var before = Fingerprint(cart);

        foreach (var attempt in new Func<PosCartService, bool>[]
                 {
                     c => c.SetOrderDiscountAmount(0m),
                     c => c.SetOrderDiscountAmount(5m),
                     c => c.SetOrderDiscountPercent(0m),
                     c => c.SetOrderDiscountPercent(10m),
                     c => c.SetOrderDiscountPercent(100m)
                 })
        {
            Assert.False(attempt(cart));

            Assert.Equal(before, Fingerprint(cart));
            AssertNoDiscount(returnLine);
            Assert.Equal(0m, cart.DiscountAmount);
            Assert.Equal(-8m, cart.TotalAmount);
            Assert.Equal(-8m, cart.ActualAmount);
        }

        // 没有留下任何潜伏的整单折扣状态：之后加进来的销售行没有折扣；有了销售行，同样的入口立刻可用。
        var sale = cart.AddItem(Item("LATER", 12m));
        AssertNoDiscount(sale);
        AssertNoDiscount(returnLine);
        Assert.Equal(4m, cart.ActualAmount);
        Assert.True(cart.SetOrderDiscountAmount(2m));
        Assert.Equal(2m, sale.DiscountAmount);
        AssertNoDiscount(returnLine);
        Assert.Equal(2m, cart.ActualAmount);
    }

    [Fact]
    public void Order_discount_is_also_rejected_when_the_only_sale_lines_are_zero_priced()
    {
        // 「没有可打折的销售行」包含销售行全是零价（毛额 0）的情况：整单折扣同样无处可落，工作流给同一条提示。
        var cart = new PosCartService();
        var zeroPrice = cart.AddItem(Item("ZERO", 0m));
        var returnLine = cart.AddReturnLine(ReturnRequest("RET-1"));
        var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), cart);
        var before = Fingerprint(cart);

        Assert.False(cart.SetOrderDiscountAmount(0m));
        Assert.False(cart.SetOrderDiscountPercent(10m));
        Assert.Equal(
            OrderDiscountNoSaleLinesKey,
            service.ApplySelectedLineDiscountPercent(zeroPrice, "10", isWholeOrderOperation: true).StatusKey);
        Assert.Equal(
            OrderDiscountNoSaleLinesKey,
            service.GetDiscountRejection(returnLine, isWholeOrderOperation: true)?.StatusKey);

        Assert.Equal(before, Fingerprint(cart));
        AssertNoDiscount(zeroPrice);
        AssertNoDiscount(returnLine);
    }

    [Fact]
    public void Order_discount_rejection_for_no_sale_lines_does_not_touch_carts_without_return_lines()
    {
        // 特征化：没有退货行时既有行为不变——只含零价销售行的购物车，百分比 / 金额 0 照旧被接受（无数据变化），不走新的拒绝。
        var cart = new PosCartService();
        var zeroPrice = cart.AddItem(Item("ZERO", 0m));

        Assert.True(cart.SetOrderDiscountPercent(10m));
        Assert.True(cart.SetOrderDiscountAmount(0m));
        Assert.False(cart.SetOrderDiscountAmount(5m));
        AssertNoDiscount(zeroPrice);

        // 空购物车照旧拒绝（工作流层提示「请先选择商品行」）。
        var empty = new PosCartService();
        Assert.False(empty.SetOrderDiscountAmount(0m));
        Assert.False(empty.SetOrderDiscountPercent(10m));
    }

    [Theory]
    [InlineData(BuildOrder.DiscountThenReturn)]
    [InlineData(BuildOrder.ReturnThenDiscount)]
    public void Line_level_discounts_are_still_rejected_for_return_lines_even_after_an_order_discount(BuildOrder order)
    {
        // 特征化：退货行的行级折扣 / 改数量 / 改价仍然被拒绝，不因为整单折扣放开而松动。
        var std = BuildStandard(order, cart => cart.SetOrderDiscountAmount(8m));
        var before = Fingerprint(std.Cart);

        Assert.False(std.Cart.SetLineDiscountAmount(std.R, 1m));
        Assert.False(std.Cart.SetLineDiscountPercent(std.R, 10m));
        Assert.False(std.Cart.SetLineQuantity(std.R, 3m));
        Assert.False(std.Cart.SetLineUnitPrice(std.R, 9m));
        Assert.False(std.Cart.IncreaseLine(std.R));

        Assert.Equal(before, Fingerprint(std.Cart));
        Assert.Throws<InvalidOperationException>(() => std.R.SetOrderDiscountAmount(1m));
        AssertNoDiscount(std.R);
    }

    // ===================================================================================
    // 二、工作流与收银页：整单模式一律放行，行模式仍拦退货行
    // ===================================================================================

    [Fact]
    public void Discount_gate_lets_whole_order_mode_through_whatever_line_is_selected_but_still_blocks_return_line_in_line_mode()
    {
        var std = BuildStandard(BuildOrder.ReturnThenDiscount, cart => cart.SetOrderDiscountAmount(0m));
        var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), std.Cart);

        // 整单模式：选中退货行 / 销售行 / 未选中都放行（返回 null），尤其选中退货行也不能被行级提示误拦。
        Assert.Null(service.GetDiscountRejection(std.R, isWholeOrderOperation: true));
        Assert.Null(service.GetDiscountRejection(std.A, isWholeOrderOperation: true));
        Assert.Null(service.GetDiscountRejection(null, isWholeOrderOperation: true));

        // 行模式：选中退货行仍给明确提示；选中销售行 / 未选中放行。
        Assert.Equal(
            "pos.status.returnLineDiscountBlocked",
            service.GetDiscountRejection(std.R, isWholeOrderOperation: false)?.StatusKey);
        Assert.Null(service.GetDiscountRejection(std.A, isWholeOrderOperation: false));
        Assert.Null(service.GetDiscountRejection(null, isWholeOrderOperation: false));
    }

    [Fact]
    public void Workflow_whole_order_discount_with_return_line_validates_keypad_range_and_cap_in_the_usual_order()
    {
        var std = BuildStandard(BuildOrder.ReturnThenDiscount, cart => cart.SetOrderDiscountAmount(0m));
        var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), std.Cart);
        var before = Fingerprint(std.Cart);

        Assert.Equal(
            "pos.status.invalidKeypadValue",
            service.ApplySelectedLineDiscountPercent(std.R, "abc", isWholeOrderOperation: true).StatusKey);
        Assert.Equal(
            "pos.status.invalidKeypadValue",
            service.ApplySelectedLineDiscountAmount(std.R, string.Empty, isWholeOrderOperation: true).StatusKey);
        Assert.Equal(
            "pos.status.discountPercentOutOfRange",
            service.ApplySelectedLineDiscountPercent(std.R, "150", isWholeOrderOperation: true).StatusKey);
        Assert.Equal(
            "pos.status.discountPercentOutOfRange",
            service.ApplyQuickDiscountPercent(std.R, "150", isWholeOrderOperation: true).StatusKey);
        // 上限是销售行毛额 40：40.01 报「折扣金额过高」，不会按含退货行的合计 32 误报或误放。
        Assert.Equal(
            "pos.status.discountAmountTooHigh",
            service.ApplySelectedLineDiscountAmount(std.R, "40.01", isWholeOrderOperation: true).StatusKey);

        Assert.Equal(before, Fingerprint(std.Cart));
    }

    [Fact]
    public void Pos_terminal_whole_order_discount_updates_displayed_totals_and_keeps_return_line_free()
    {
        var cart = new PosCartService();
        var viewModel = new PosTerminalViewModel(new LocalSellableItemIndex(), cart, Session, onOpenPayment: null);
        var sale = cart.AddItem(Item("SALE", 30m));
        var returnLine = cart.AddReturnLine(ReturnRequest("RET-1"));

        // 选中退货行点整单折扣：整单模式不看选中行，照常对销售行打折。
        viewModel.SelectedCartLine = returnLine;
        viewModel.IsWholeOrderOperation = true;
        viewModel.KeypadBuffer = "5";
        viewModel.ApplySelectedLineDiscountAmountCommand.Execute(null);

        Assert.Equal(5m, sale.DiscountAmount);
        AssertNoDiscount(returnLine);
        Assert.Equal(22m, viewModel.TotalAmount);
        Assert.Equal(5m, viewModel.DiscountAmount);
        Assert.Equal(17m, viewModel.ActualAmount);
        Assert.False(viewModel.IsWholeOrderOperation);

        // 再来一次百分比 + 快速折扣：每次整单折扣都重新覆盖销售行，退货行始终没有折扣。
        viewModel.IsWholeOrderOperation = true;
        viewModel.KeypadBuffer = "10";
        viewModel.ApplySelectedLineDiscountPercentCommand.Execute(null);
        Assert.Equal(3m, sale.DiscountAmount);
        viewModel.IsWholeOrderOperation = true;
        viewModel.ApplyQuickDiscountPercentCommand.Execute("50");
        Assert.Equal(15m, sale.DiscountAmount);
        AssertNoDiscount(returnLine);
        Assert.Equal(7m, viewModel.ActualAmount);
    }

    // ===================================================================================
    // 三、下游：付款金额（CashCheckoutService，所有收款路径的最终落点）
    // ===================================================================================

    [Theory]
    [InlineData(BuildOrder.DiscountThenReturn)]
    [InlineData(BuildOrder.ReturnThenDiscount)]
    public void Payment_order_amounts_reconcile_for_cash_card_voucher_and_mixed_tenders(BuildOrder order)
    {
        var checkout = new CashCheckoutService();

        // 现金刚好、现金找零、刷卡、券、三种混合：应付都是 24 = 销售行折后 32 + 退货行 -8。
        var cashExact = checkout.CreatePaymentOrder(NewStandardCart(order), Session, [Tender(PaymentMethodKind.Cash, 24m)], 24m);
        AssertOrder(cashExact.Order, expectedPayments: [(PaymentMethodKind.Cash, 24m)]);
        Assert.Equal(0m, cashExact.ChangeAmount);

        var cashChange = checkout.CreatePaymentOrder(NewStandardCart(order), Session, [Tender(PaymentMethodKind.Cash, 30m)], 30m);
        AssertOrder(cashChange.Order, expectedPayments: [(PaymentMethodKind.Cash, 24m)]);
        Assert.Equal(30m, cashChange.TenderedAmount);
        Assert.Equal(6m, cashChange.ChangeAmount);

        var card = checkout.CreatePaymentOrder(NewStandardCart(order), Session, [Tender(PaymentMethodKind.Card, 24m)], 0m);
        AssertOrder(card.Order, expectedPayments: [(PaymentMethodKind.Card, 24m)]);

        var voucher = checkout.CreatePaymentOrder(NewStandardCart(order), Session, [Tender(PaymentMethodKind.Voucher, 24m)], 0m);
        AssertOrder(voucher.Order, expectedPayments: [(PaymentMethodKind.Voucher, 24m)]);

        var mixed = checkout.CreatePaymentOrder(
            NewStandardCart(order),
            Session,
            [Tender(PaymentMethodKind.Card, 10m), Tender(PaymentMethodKind.Voucher, 4m), Tender(PaymentMethodKind.Cash, 10m)],
            10m);
        AssertOrder(
            mixed.Order,
            expectedPayments: [(PaymentMethodKind.Card, 10m), (PaymentMethodKind.Voucher, 4m), (PaymentMethodKind.Cash, 10m)]);

        // 少付 / 非现金超付仍被拒绝：应付金额就是 24，不会因为含退货行而放宽或收紧。
        Assert.Throws<InvalidOperationException>(() =>
            checkout.CreatePaymentOrder(NewStandardCart(order), Session, [Tender(PaymentMethodKind.Cash, 23m)], 23m));
        Assert.Throws<InvalidOperationException>(() =>
            checkout.CreatePaymentOrder(NewStandardCart(order), Session, [Tender(PaymentMethodKind.Card, 25m)], 0m));
    }

    [Theory]
    [InlineData(BuildOrder.DiscountThenReturn)]
    [InlineData(BuildOrder.ReturnThenDiscount)]
    public void Cash_due_is_rounded_from_the_net_amount_of_a_discounted_mixed_cart(BuildOrder order)
    {
        // 整单折扣 8.03 -> 应付 23.97；现金按 5 分取整，应收 23.95，订单里记的现金付款仍是 23.97，不多不少。
        var cart = BuildStandard(order, c => c.SetOrderDiscountAmount(8.03m)).Cart;
        Assert.Equal(23.97m, cart.ActualAmount);

        var result = new CashCheckoutService().CreatePaymentOrder(cart, Session, [Tender(PaymentMethodKind.Cash, 23.95m)], 23.95m);

        Assert.Equal(23.97m, result.Order.ActualAmount);
        var payment = Assert.Single(result.Order.Payments);
        Assert.Equal(PaymentMethodKind.Cash, payment.Method);
        Assert.Equal(23.97m, payment.Amount);
        Assert.Equal(0m, result.ChangeAmount);
        Assert.Equal(8.03m, result.Order.DiscountAmount);
        Assert.Equal(8.03m, result.Order.Lines.Where(line => line.Kind == OrderLineKind.Sale).Sum(line => line.DiscountAmount));
    }

    [Theory]
    [InlineData(BuildOrder.DiscountThenReturn)]
    [InlineData(BuildOrder.ReturnThenDiscount)]
    public void Net_refund_and_net_zero_paths_work_for_a_discounted_mixed_cart(BuildOrder order)
    {
        var checkout = new CashCheckoutService();

        // 整单折扣把销售行全部抵掉：应付 -8，只剩净退款；刷卡 / 现金退款都按负数。
        Func<PosCartService> refundCart = () => BuildStandard(order, c => c.SetOrderDiscountAmount(40m)).Cart;
        Assert.Equal(-8m, refundCart().ActualAmount);
        var cardRefund = checkout.CreatePaymentOrder(refundCart(), Session, [Tender(PaymentMethodKind.Card, -8m)], -8m);
        Assert.Equal(-8m, cardRefund.Order.ActualAmount);
        Assert.Equal([(PaymentMethodKind.Card, -8m)], cardRefund.Order.Payments.Select(p => (p.Method, p.Amount)).ToArray());
        var cashRefund = checkout.CreatePaymentOrder(refundCart(), Session, [Tender(PaymentMethodKind.Cash, -8m)], -8m);
        Assert.Equal([(PaymentMethodKind.Cash, -8m)], cashRefund.Order.Payments.Select(p => (p.Method, p.Amount)).ToArray());
        // 净退款时正数付款金额一律拒绝。
        Assert.Throws<InvalidOperationException>(() =>
            checkout.CreatePaymentOrder(refundCart(), Session, [Tender(PaymentMethodKind.Cash, 8m)], 8m));

        // 折扣让销售行折后恰好等于退货额：净额 0，换货单，不允许带付款。
        var zeroCart = BuildStandard(order, c => c.SetOrderDiscountAmount(32m)).Cart;
        Assert.Equal(0m, zeroCart.ActualAmount);
        var zero = checkout.CreatePaymentOrder(zeroCart, Session, [], 0m);
        Assert.Empty(zero.Order.Payments);
        Assert.Equal(32m, zero.Order.DiscountAmount);
        Assert.Throws<InvalidOperationException>(() =>
            checkout.CreatePaymentOrder(
                BuildStandard(order, c => c.SetOrderDiscountAmount(32m)).Cart,
                Session,
                [Tender(PaymentMethodKind.Cash, 1m)],
                1m));
    }

    // ===================================================================================
    // 四、下游：真实收款流程 -> 本地订单库 -> 小票 -> 订单上传请求
    // ===================================================================================

    [Theory]
    [InlineData(BuildOrder.DiscountThenReturn)]
    [InlineData(BuildOrder.ReturnThenDiscount)]
    public async Task Cash_checkout_persists_order_and_feeds_receipt_and_upload_request_consistently(BuildOrder order)
    {
        var databasePath = NewDatabasePath("checkout");
        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var orders = new LocalOrderRepository(store);
            var uploadRepository = new LocalOrderUploadRepository(store);
            var cart = NewStandardCart(order);
            var workflow = new CashPaymentWorkflowService(new CashCheckoutService(), orders, new SyncQueueRepository(store));

            var completed = await workflow.CompleteAsync(cart, Session, "24");

            // 真实收款流程：落库、清购物车；订单头、行、付款与购物车一致。
            Assert.True(cart.IsEmpty);
            AssertOrder(completed.Order, expectedPayments: [(PaymentMethodKind.Cash, 24m)]);

            // 本地 SQLite 往返：读出来的订单与刚收款的订单在折扣、实收、行类型上一致。
            var persisted = await orders.GetOrderAsync(completed.Order.OrderGuid)
                ?? throw new InvalidOperationException("Order was not persisted.");
            AssertOrder(persisted, expectedPayments: [(PaymentMethodKind.Cash, 24m)]);

            // 小票：销售行各有一行 Dis，退货行没有；小计 / 折扣 / 合计对得上。
            AssertReceipt(persisted);

            // 订单上传请求：折扣字段直接来自订单行，退货行折扣为 0 且保留退货标记与原单引用。
            var apiClient = new CapturingOrderSyncApiClient();
            await new OrderUploadService(orders, apiClient, uploadRepository).UploadOrderAsync(persisted.OrderGuid);
            var request = Assert.Single(apiClient.Requests);
            Assert.Equal(32m, request.TotalAmount);
            Assert.Equal(8m, request.DiscountAmount);
            Assert.Equal(24m, request.ActualAmount);
            Assert.Equal(
                [(OrderLineKind.Sale, 10m, 2m, 8m), (OrderLineKind.Sale, 30m, 6m, 24m), (OrderLineKind.Return, 8m, 0m, -8m)],
                request.Lines.Select(line => (line.Kind, line.UnitPrice, line.DiscountAmount, line.ActualAmount)).ToArray());
            var returnLine = Assert.Single(request.Lines, line => line.Kind == OrderLineKind.Return);
            Assert.Equal("RET-1", returnLine.ReturnSourceKey);
            Assert.NotNull(returnLine.OriginalOrderGuid);
            Assert.NotNull(returnLine.OriginalOrderDetailGuid);
            // 上传 DTO 自洽：行折扣之和 = 订单折扣，行实收之和 = 订单实收 = 付款之和。
            Assert.Equal(request.DiscountAmount, request.Lines.Sum(line => line.DiscountAmount));
            Assert.Equal(request.ActualAmount, request.Lines.Sum(line => line.ActualAmount));
            Assert.Equal(request.ActualAmount, request.Payments.Sum(payment => payment.Amount));
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    // ===================================================================================
    // 五、下游：本地挂单、共享挂单、支付恢复快照
    // ===================================================================================

    [Theory]
    [InlineData(BuildOrder.DiscountThenReturn)]
    [InlineData(BuildOrder.ReturnThenDiscount)]
    public async Task Local_suspended_order_round_trip_keeps_order_discount_and_discount_free_return_line(BuildOrder order)
    {
        var databasePath = NewDatabasePath("suspend");
        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var cart = NewStandardCart(order);
            var service = new SuspendedOrderService(new SuspendedOrderRepository(store), cart);
            var expectedLines = cart.Lines.Select(Signature).ToArray();

            var suspended = await service.SuspendCurrentOrderAsync(Session);

            Assert.True(cart.IsEmpty);
            Assert.Equal(32m, suspended.TotalAmount);
            Assert.Equal(8m, suspended.DiscountAmount);
            Assert.Equal(24m, suspended.ActualAmount);
            var storedReturn = Assert.Single(suspended.Lines, line => line.Kind == CartLineKind.Return);
            Assert.Equal(0m, storedReturn.DiscountAmount);
            Assert.Null(storedReturn.DiscountPercent);
            Assert.Equal(PosCartLineDiscountSource.None, storedReturn.DiscountSource);
            Assert.Equal(-8m, storedReturn.ActualAmount);

            // 取单（走真实 SQLite 往返）：销售行折扣、退货行无折扣、总额都原样还原。
            await service.RecallOrderAsync(suspended.SuspendedOrderGuid);

            Assert.Equal(expectedLines, cart.Lines.Select(Signature).ToArray());
            Assert.All(cart.Lines.Where(line => line.IsReturnLine), AssertNoDiscount);
            Assert.Equal(32m, cart.TotalAmount);
            Assert.Equal(8m, cart.DiscountAmount);
            Assert.Equal(24m, cart.ActualAmount);

            // 取单之后继续编辑：销售行折扣仍是固化金额，退货行仍然无折扣。
            var recalledA = Assert.Single(cart.Lines, line => line.LookupCode == "LK-A");
            Assert.True(cart.SetLineQuantity(recalledA, 2m));
            Assert.Equal(2m, recalledA.DiscountAmount);
            Assert.All(cart.Lines.Where(line => line.IsReturnLine), AssertNoDiscount);
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Theory]
    [InlineData(BuildOrder.DiscountThenReturn)]
    [InlineData(BuildOrder.ReturnThenDiscount)]
    public async Task Shared_held_order_mapping_of_discounted_mixed_cart_is_blocked_by_return_line_and_stays_local(BuildOrder order)
    {
        var databasePath = NewDatabasePath("shared-hold");
        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var cart = NewStandardCart(order);
            var service = new SuspendedOrderService(new SuspendedOrderRepository(store), cart);
            var suspended = await service.SuspendCurrentOrderAsync(Session);
            var mapper = new SharedHeldOrderMapper();

            // 共享挂单只支持普通销售行：含退货行的挂单一律以退货行原因阻断、保留本地，
            // 与整单折扣无关——不会走到促销规则对应检查，所以也不会出现 PromotionRulesMismatch。
            var withoutRules = mapper.Map(suspended, frozenPromotionRules: null, revision: 1);
            var withRules = mapper.Map(suspended, [PromotionRule("PROMO-UNRELATED", "SKU-NONE")], revision: 1);

            foreach (var result in new[] { withoutRules, withRules })
            {
                Assert.Null(result.Payload);
                Assert.Equal(SharedHeldOrderMappingReasons.ReturnLine, result.Block?.Reason);
            }
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Shared_held_order_round_trip_of_sale_only_cart_with_order_discount_restores_line_amounts()
    {
        // 特征化：销售行带整单折扣（不含退货行）的共享挂单照常可发布、可取单还原，金额逐行一致。
        var databasePath = NewDatabasePath("shared-hold-sale");
        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var cart = new PosCartService();
            cart.AddItem(Item("A", 10m));
            cart.AddItem(Item("B", 30m));
            Assert.True(cart.SetOrderDiscountAmount(8m));
            var service = new SuspendedOrderService(new SuspendedOrderRepository(store), cart);
            var suspended = await service.SuspendCurrentOrderAsync(Session);

            var mapped = new SharedHeldOrderMapper().Map(suspended, frozenPromotionRules: null, revision: 1);

            Assert.Null(mapped.Block);
            var snapshot = new SharedHeldOrderReverseMapper().Map(mapped.Payload!, StoreCode);
            var restored = new PosCartService();
            restored.RestoreSharedSaleSnapshot(snapshot with { SharedHeldOrderClaimId = Guid.NewGuid() });
            Assert.Equal([2m, 6m], restored.Lines.Select(line => line.DiscountAmount).ToArray());
            Assert.Equal(8m, restored.DiscountAmount);
            Assert.Equal(32m, restored.ActualAmount);
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Theory]
    [InlineData(BuildOrder.DiscountThenReturn)]
    [InlineData(BuildOrder.ReturnThenDiscount)]
    public void Card_recovery_draft_round_trip_keeps_order_discount_and_discount_free_return_line(BuildOrder order)
    {
        // 支付恢复快照：购物车快照随刷卡草稿序列化成 JSON，崩溃后在隔离购物车里物化，再发布到空购物车。
        var cart = NewStandardCart(order);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var expectedLines = cart.Lines.Select(Signature).ToArray();
        var draft = new CardPaymentOrderDraft(
            Guid.NewGuid(),
            Session,
            cart.CreateSnapshot(),
            [],
            cart.ActualAmount,
            cart.ActualAmount,
            "P",
            null,
            DateTimeOffset.Parse("2026-10-13T00:00:00Z"));

        var prepared = CardRecoveryCartMaterializer.TryPrepare(JsonSerializer.Serialize(draft, options), options, out var materialized);

        Assert.True(prepared);
        var recovered = new PosCartService();
        var publication = recovered.TryPublishRecoverySnapshot(Guid.NewGuid(), recovered.Revision, materialized!.CartSnapshot);
        Assert.True(publication.Succeeded);
        Assert.Equal(expectedLines, recovered.Lines.Select(Signature).ToArray());
        Assert.All(recovered.Lines.Where(line => line.IsReturnLine), AssertNoDiscount);
        Assert.Equal(32m, recovered.TotalAmount);
        Assert.Equal(8m, recovered.DiscountAmount);
        Assert.Equal(24m, recovered.ActualAmount);
        Assert.Equal(materialized.ActualAmount, recovered.ActualAmount);
    }

    // ===================================================================================
    // 辅助
    // ===================================================================================

    private static PosSessionState Session => new("HB POS", StoreCode, "Main Store", "POS-01", "C001", "Alice", true, 0);

    private sealed record StandardCart(PosCartService Cart, CartLine A, CartLine B, CartLine R);

    /// <summary>
    /// 标准购物车：销售 A(10) + 销售 B(30) + 退货 R(8)，合计 32；
    /// 整单折扣 8 时 A 分到 2、B 分到 6，应付 24。DiscountThenReturn 先设整单折扣再加退货行，ReturnThenDiscount 反之。
    /// </summary>
    private static StandardCart BuildStandard(BuildOrder order, Func<PosCartService, bool> discount)
    {
        var cart = new PosCartService();
        var a = cart.AddItem(Item("A", 10m));
        var b = cart.AddItem(Item("B", 30m));
        CartLine r;
        if (order == BuildOrder.DiscountThenReturn)
        {
            Assert.True(discount(cart));
            r = cart.AddReturnLine(ReturnRequest("RET-1"));
        }
        else
        {
            r = cart.AddReturnLine(ReturnRequest("RET-1"));
            Assert.True(discount(cart));
        }

        return new StandardCart(cart, a, b, r);
    }

    private static PosCartService NewStandardCart(BuildOrder order)
    {
        return BuildStandard(order, cart => cart.SetOrderDiscountAmount(8m)).Cart;
    }

    private static PaymentTender Tender(PaymentMethodKind method, decimal amount) => new(method, amount);

    /// <summary>标准购物车 + 整单折扣 8 收款后的订单：头、行、付款三处金额必须对得上。</summary>
    private static void AssertOrder(
        LocalOrder order,
        (PaymentMethodKind Method, decimal Amount)[] expectedPayments)
    {
        Assert.Equal(32m, order.TotalAmount);
        Assert.Equal(8m, order.DiscountAmount);
        Assert.Equal(24m, order.ActualAmount);

        var sales = order.Lines.Where(line => line.Kind == OrderLineKind.Sale).OrderBy(line => line.UnitPrice).ToArray();
        Assert.Equal([2m, 6m], sales.Select(line => line.DiscountAmount).ToArray());
        Assert.Equal([8m, 24m], sales.Select(line => line.ActualAmount).ToArray());
        var returnLine = Assert.Single(order.Lines, line => line.Kind == OrderLineKind.Return);
        Assert.Equal(0m, returnLine.DiscountAmount);
        Assert.Equal(-8m, returnLine.ActualAmount);
        Assert.Equal("RET-1", returnLine.ReturnSourceKey);

        Assert.Equal(order.DiscountAmount, order.Lines.Sum(line => line.DiscountAmount));
        Assert.Equal(order.ActualAmount, order.Lines.Sum(line => line.ActualAmount));
        Assert.Equal(
            expectedPayments,
            order.Payments.Select(payment => (payment.Method, payment.Amount)).ToArray());
        Assert.Equal(order.ActualAmount, order.Payments.Sum(payment => payment.Amount));
    }

    private static void AssertReceipt(LocalOrder order)
    {
        var receipt = ReceiptQueryService.CreateReceipt(order);
        Assert.Equal(32m, receipt.TotalAmount);
        Assert.Equal(8m, receipt.DiscountAmount);
        Assert.Equal(24m, receipt.ActualAmount);
        Assert.Equal(
            [(10m, 2m, 8m), (30m, 6m, 24m), (8m, 0m, -8m)],
            receipt.Lines.Select(line => (line.UnitPrice, line.DiscountAmount, line.ActualAmount)).ToArray());

        var text = new ReceiptTextFormatter().Build(receipt, ReceiptPrinterSettings.Default).PlainText;
        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None);
        // 每个有折扣的销售行下面一行 Dis，退货行下面没有；汇总行的小计 / 折扣 / 合计与订单头一致。
        // 行折扣行是「Dis」后跟空白；汇总行「Discount」以 Dis 开头但不是行折扣，要排除。
        var discountLines = lines.Where(IsLineDiscountRow).ToArray();
        Assert.Equal(2, discountLines.Length);
        Assert.Contains(discountLines, line => line.Contains("-$2.00", StringComparison.Ordinal));
        Assert.Contains(discountLines, line => line.Contains("-$6.00", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("Subtotal", StringComparison.Ordinal) && line.Contains("$32.00", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("Discount", StringComparison.Ordinal) && line.Contains("-$8.00", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("Total(inc GST)", StringComparison.Ordinal) && line.Contains("$24.00", StringComparison.Ordinal));
        var returnRowIndex = Array.FindIndex(lines, line => line.Contains("LK-R", StringComparison.Ordinal) && line.Contains("$-8.00", StringComparison.Ordinal));
        Assert.True(returnRowIndex >= 0, "退货行应当以 $-8.00 出现在小票上（既有的负数金额写法）：" + Environment.NewLine + text);
        Assert.False(
            returnRowIndex + 1 < lines.Length && IsLineDiscountRow(lines[returnRowIndex + 1]),
            "退货行下面不应出现折扣行。");
    }

    private static bool IsLineDiscountRow(string receiptLine)
    {
        var trimmed = receiptLine.TrimStart();
        return trimmed.StartsWith("Dis", StringComparison.Ordinal) && trimmed.Length > 3 && char.IsWhiteSpace(trimmed[3]);
    }

    /// <summary>「完全没有折扣」：金额、百分比、来源、目录基线、各类标记全部为空；实收等于毛额。</summary>
    private static void AssertNoDiscount(CartLine line)
    {
        Assert.Equal(0m, line.DiscountAmount);
        Assert.Null(line.DiscountPercent);
        Assert.Equal(CartLineDiscountSource.None, line.DiscountSource);
        Assert.Equal(0, line.CatalogDiscountBasisPoints);
        Assert.False(line.HasDiscount);
        Assert.False(line.HasManualDiscount);
        Assert.False(line.IsAutomaticPromotionDiscount);
        Assert.False(line.IsCatalogDiscount);
        Assert.Equal(line.GrossAmount, line.ActualAmount);
    }

    /// <summary>行折扣签名：查询码 + 折扣金额 + 百分比 + 来源。差分对照用它判断两个购物车里同一销售行的折扣状态是否完全一致。</summary>
    private static string Signature(CartLine line)
    {
        // 金额统一按最多 4 位小数输出：SQLite 往返后 decimal 的小数位数会变（10 与 10.0），不能让位数差异冒充数据差异。
        return string.Join(
            "|",
            line.LookupCode,
            line.Quantity.ToString("0.####", CultureInfo.InvariantCulture),
            line.UnitPrice.ToString("0.####", CultureInfo.InvariantCulture),
            line.DiscountAmount.ToString("0.####", CultureInfo.InvariantCulture),
            line.DiscountPercent?.ToString("0.####", CultureInfo.InvariantCulture) ?? "-",
            line.DiscountSource);
    }

    private static string Fingerprint(PosCartService cart)
    {
        var lines = cart.Lines.Select(line => string.Join(
            ":",
            line.Kind,
            Signature(line),
            line.IsManualPrice,
            line.CatalogDiscountBasisPoints));
        return $"rev={cart.Revision};total={cart.TotalAmount};discount={cart.DiscountAmount};actual={cart.ActualAmount};" +
               string.Join("|", lines);
    }

    private static string NewDatabasePath(string tag)
    {
        return Path.Combine(Path.GetTempPath(), $"hbpos-order-discount-return-{tag}-{Guid.NewGuid():N}.db");
    }

    // ----- 差分对照的场景 -----

    private sealed record Scenario(
        IReadOnlyList<CatalogPromotionRuleDto> Rules,
        IReadOnlyList<Func<PosCartService, CartLine>> Lines,
        Func<PosCartService, bool> Discount,
        string ReturnProductCode = "SKU-RET");

    private static Scenario GetScenario(string name)
    {
        return name switch
        {
            "plain-amount" => new Scenario([], PlainLines, cart => cart.SetOrderDiscountAmount(5.55m)),
            "plain-percent" => new Scenario([], PlainLines, cart => cart.SetOrderDiscountPercent(13.5m)),
            "mixed-sources-amount" => new Scenario([], MixedSourceLines, cart => cart.SetOrderDiscountAmount(17.77m)),
            "mixed-sources-percent" => new Scenario([], MixedSourceLines, cart => cart.SetOrderDiscountPercent(33.3m)),
            "promotion-amount" => new Scenario(
                [PromotionRule("PROMO-2FOR15", "SKU-A")],
                PromotionLines,
                cart => cart.SetOrderDiscountAmount(4m),
                ReturnProductCode: "SKU-A"),
            "zero-after-percent" => new Scenario(
                [],
                MixedSourceLines,
                cart => cart.SetOrderDiscountPercent(50m) && cart.SetOrderDiscountPercent(0m)),
            "full-100-percent" => new Scenario([], MixedSourceLines, cart => cart.SetOrderDiscountPercent(100m)),
            // 0.01% 在很小的金额上舍入为 0 分：仍须冻结 Manual + 0，防止目录折扣重新生效。
            "tiny-percent-rounding-to-zero" => new Scenario([], TinyLines, cart => cart.SetOrderDiscountPercent(0.01m)),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, null)
        };
    }

    private static readonly IReadOnlyList<Func<PosCartService, CartLine>> PlainLines =
    [
        cart => cart.AddItem(Item("P1", 7.77m)),
        cart =>
        {
            var line = cart.AddItem(Item("P2", 12.34m));
            Assert.True(cart.SetLineQuantity(line, 2m));
            return line;
        },
        cart =>
        {
            var line = cart.AddItem(Item("P3", 0.99m));
            Assert.True(cart.SetLineQuantity(line, 3m));
            return line;
        }
    ];

    private static readonly IReadOnlyList<Func<PosCartService, CartLine>> MixedSourceLines =
    [
        cart => cart.AddItem(Item("M1", 20m)),
        // 目录折扣 20%（整单折扣会把它覆盖成手工来源）。
        cart => cart.AddItem(Item("M2", 15m, discountRate: 0.2m)),
        // 手工行折扣 10%。
        cart =>
        {
            var line = cart.AddItem(Item("M3", 12.5m));
            Assert.True(cart.SetLineDiscountPercent(line, 10m));
            return line;
        },
        // 自由商品。
        cart => cart.AddOpenItem(Item("M4", 0m), 5.55m),
        // 零价行：毛额为 0，不参与分摊。
        cart => cart.AddItem(Item("M5", 0m)),
        cart =>
        {
            var line = cart.AddItem(Item("M6", 4.33m));
            Assert.True(cart.SetLineQuantity(line, 3m));
            return line;
        }
    ];

    private static readonly IReadOnlyList<Func<PosCartService, CartLine>> PromotionLines =
    [
        cart => cart.AddItem(Item("A1", 10m, productCode: "SKU-A")),
        cart => cart.AddItem(Item("A2", 10m, productCode: "SKU-A")),
        cart => cart.AddItem(Item("A3", 30m, productCode: "SKU-OTHER"))
    ];

    private static readonly IReadOnlyList<Func<PosCartService, CartLine>> TinyLines =
    [
        cart => cart.AddItem(Item("T1", 0.01m, discountRate: 1m)),
        cart => cart.AddItem(Item("T2", 0.01m)),
        cart => cart.AddItem(Item("T3", 0.02m, discountRate: 0.5m))
    ];

    private static List<CartLine> AddPlainLines(PosCartService cart)
    {
        return PlainLines.Select(add => add(cart)).ToList();
    }

    /// <summary>
    /// 按场景建购物车。returnIndex 为 null 表示不带退货行（基准）；否则在第 returnIndex 个销售行之前插入退货行，
    /// discountFirst 为 true 时先把全部销售行加完、设整单折扣，最后才加退货行。
    /// </summary>
    private static (PosCartService Cart, List<CartLine> SaleLines, CartLine? ReturnLine) BuildScenario(
        Scenario scenario,
        int? returnIndex,
        bool discountFirst)
    {
        var cart = new PosCartService();
        cart.SetAutomaticPromotionRules(scenario.Rules);
        var saleLines = new List<CartLine>();
        CartLine? returnLine = null;
        var request = ReturnRequest("RET-1", productCode: scenario.ReturnProductCode);

        for (var i = 0; i <= scenario.Lines.Count; i++)
        {
            if (!discountFirst && returnIndex == i)
            {
                returnLine = cart.AddReturnLine(request);
            }

            if (i < scenario.Lines.Count)
            {
                saleLines.Add(scenario.Lines[i](cart));
            }
        }

        Assert.True(scenario.Discount(cart));
        if (discountFirst && returnIndex is not null)
        {
            returnLine = cart.AddReturnLine(request);
        }

        return (cart, saleLines, returnLine);
    }

    // ----- 数据构造 -----

    private static SellableItemDto Item(
        string sku,
        decimal price,
        decimal? discountRate = null,
        string? productCode = null)
    {
        return new SellableItemDto(
            StoreCode: StoreCode,
            ProductCode: productCode ?? $"SKU-{sku}",
            ReferenceCode: null,
            DisplayName: $"Item {sku}",
            LookupCode: $"LK-{sku}",
            ItemNumber: sku,
            Barcode: $"LK-{sku}",
            RetailPrice: price,
            PriceSource: PriceSourceKind.StoreRetailPrice,
            PriceSourceLabel: PriceSourceKind.StoreRetailPrice.ToString(),
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow,
            ProductImage: null,
            DiscountRate: discountRate);
    }

    private static ReturnCartLineRequest ReturnRequest(
        string sourceKey,
        string productCode = "SKU-RET",
        string lookupCode = "LK-R",
        decimal unitPrice = 8m)
    {
        return new ReturnCartLineRequest(
            StoreCode,
            productCode,
            null,
            $"Item {productCode}",
            lookupCode,
            productCode,
            null,
            1m,
            unitPrice,
            PriceSourceKind.StoreRetailPrice,
            PriceSourceKind.StoreRetailPrice.ToString(),
            sourceKey,
            Guid.NewGuid(),
            Guid.NewGuid());
    }

    private static CatalogPromotionRuleDto PromotionRule(string promotionId, params string[] productCodes)
    {
        return new CatalogPromotionRuleDto(
            promotionId,
            "Quantity discount",
            IsExclusive: true,
            Priority: 0,
            ApplyQuantity: 2,
            FixedPrice: 15m,
            MaxApplicationsPerOrder: null,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1),
            DateTimeOffset.UtcNow,
            productCodes.Select(productCode => new CatalogPromotionProductDto(productCode, 1)).ToArray());
    }

    /// <summary>捕获订单上传请求的接口桩：直接看 OrderUploadService 序列化出去的折扣字段。</summary>
    private sealed class CapturingOrderSyncApiClient : IOrderSyncApiClient
    {
        public List<OrderSyncRequest> Requests { get; } = [];

        public Task<OrderSyncResponse> SyncAsync(OrderSyncRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new OrderSyncResponse(request.OrderGuid, true, false, "Synced"));
        }
    }
}
