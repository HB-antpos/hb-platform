using System.ComponentModel;
using System.Globalization;
using BlazorApp.Shared.Constants;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Cashiers;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

/// <summary>
/// 退货行折扣的反馈统一（接 #741）：数据层本来就拒绝给退货行折扣，但此前收银员得到的反馈不一致——
/// 先弹授权（可能要主管 PIN），授权后百分比 / 快速折扣静默无反应，折扣金额因拿负毛额比较误报「折扣金额过高」，
/// 改数量 / 改价对退货行同样是静默的空结果。现在改为：
/// 1. 命令层（收银页）：选中退货行点折扣 / 改数量 / 改价，
///    在弹授权之前就直接在状态栏给出错误提示 + 错误音，不弹授权；
/// 2. 工作流层：同样的输入直接调用折扣 / 改数量 / 改价方法，返回明确的拒绝提示，而不是静默或误报「折扣金额过高」；
/// 3. 任何一条路径都不改购物车数据。
///
/// 后续改动（含退货行时允许整单折扣、跳过退货行）：整单模式不再被「有退货商品时不能使用整单折扣」拦截，
/// 该提示键与对应拦截已移除；整单折扣的新行为（含退货行时对销售行打折）见 OrderDiscountWithReturnLinesTests，
/// 本文件里原先断言整单被拦的用例已改成断言新行为，并钉住「整单模式下即使选中的是退货行也不被行级提示误拦」。
///
/// 测试分两类（与 PR 描述一致）：
/// 1. 失败对照：钉住本次的新行为，还原生产文件后应当失败（用例名与分区标题里标注「失败对照」）。
/// 2. 特征化：销售行的折扣 / 改数量 / 改价流程、无选中行、空购物车等边界，旧代码上同样通过，只是钉成回归护栏。
///
/// 本文件只使用本次改动前就存在的公开 API（命令、Apply* / Modify* 工作流方法、StatusKey / StatusMessage），
/// 便于在旧代码上编译并跑出失败对照；不使用本次新增的 Get*Rejection 钩子。
/// 本地化走与线程文化无关的 <see cref="LocalizationService.Translate"/>，因此不需要放进文化敏感测试集合。
/// </summary>
public sealed class ReturnLineDiscountFeedbackTests
{
    private const string StoreCode = "S001";

    private const string ReturnLineDiscountBlockedKey = "pos.status.returnLineDiscountBlocked";
    private const string OrderDiscountNoSaleLinesKey = "pos.status.orderDiscountNoSaleLines";
    private const string ReturnLineQuantityBlockedKey = "pos.status.returnLineQuantityBlocked";
    private const string ReturnLinePriceBlockedKey = "pos.status.returnLinePriceBlocked";

    /// <summary>三个折扣入口：折扣金额按钮、折扣百分比按钮、快速折扣档位按钮。</summary>
    public enum DiscountAction
    {
        Amount,
        Percent,
        Quick10,
        Quick50
    }

    // ===================================================================================
    // 一、失败对照：命令层不弹授权（收银页 ViewModel）
    // ===================================================================================

    [Theory]
    [InlineData(DiscountAction.Amount)]
    [InlineData(DiscountAction.Percent)]
    [InlineData(DiscountAction.Quick10)]
    [InlineData(DiscountAction.Quick50)]
    public async Task Line_discount_command_on_selected_return_line_shows_notice_without_asking_authorization(DiscountAction action)
    {
        using var harness = new Harness();
        harness.ViewModel.SelectedCartLine = harness.ReturnLine;
        var before = harness.CaptureCart();

        await harness.ExecuteAsync(action);

        // 授权服务（弹授权 / 主管 PIN 的入口）一次都不能被调用。
        Assert.Empty(harness.Authorization.RequestedPermissions);
        Assert.Equal(harness.Text(ReturnLineDiscountBlockedKey), harness.ViewModel.StatusMessage);
        Assert.Equal(StatusFeedbackKind.Error, harness.ViewModel.StatusFeedbackKind);
        Assert.Equal([UserFeedbackCue.OperationError], harness.Feedback.Cues);
        AssertCartUnchanged(before, harness);
        AssertNoDiscount(harness.ReturnLine);
        AssertNoDiscount(harness.SaleLine);
        // 提示不改变收银员的操作上下文：选中行、键盘输入、整单模式都原样保留。
        Assert.Same(harness.ReturnLine, harness.ViewModel.SelectedCartLine);
        Assert.Equal(harness.KeypadBufferFor(action), harness.ViewModel.KeypadBuffer);
        Assert.False(harness.ViewModel.IsWholeOrderOperation);
        // 没有真实变更，就不产生购物车变更审计。
        Assert.Empty(harness.Audit.Events);
    }

    [Theory]
    [InlineData(DiscountAction.Amount, Permissions.PosTerminal.Sales.OrderManualDiscount, 1)]
    [InlineData(DiscountAction.Percent, Permissions.PosTerminal.Sales.OrderManualDiscount, 3)]
    [InlineData(DiscountAction.Quick10, Permissions.PosTerminal.Sales.OrderQuickDiscount10Percent, 3)]
    [InlineData(DiscountAction.Quick50, Permissions.PosTerminal.Sales.OrderQuickDiscount50Percent, 15)]
    public async Task Order_discount_command_with_return_line_in_cart_asks_order_authorization_then_discounts_sale_line_only(
        DiscountAction action,
        string expectedPermission,
        int expectedDiscount)
    {
        // 预期随新行为改变（原：含退货行时整单折扣在授权前被拦并提示）。
        // 整单模式不看选中行：选中退货行 / 选中销售行 / 未选中三种情况都一样，先走整单折扣原有的权限码授权，
        // 再只对销售行打折；选中的恰好是退货行时，也不会被行级的「退货商品不能修改折扣」误拦。
        foreach (var selectedName in new[] { "return", "sale", "none" })
        {
            using var harness = new Harness();
            harness.ViewModel.IsWholeOrderOperation = true;
            harness.ViewModel.SelectedCartLine = selectedName switch
            {
                "return" => harness.ReturnLine,
                "sale" => harness.SaleLine,
                _ => null
            };

            await harness.ExecuteAsync(action);

            Assert.Equal([expectedPermission], harness.Authorization.RequestedPermissions);
            Assert.Equal(harness.Text("pos.status.orderDiscountUpdated"), harness.ViewModel.StatusMessage);
            Assert.NotEqual(harness.Text(ReturnLineDiscountBlockedKey), harness.ViewModel.StatusMessage);
            Assert.Equal((decimal)expectedDiscount, harness.SaleLine.DiscountAmount);
            AssertNoDiscount(harness.ReturnLine);
            Assert.Equal((decimal)expectedDiscount, harness.Cart.DiscountAmount);
            // 成功路径照旧会把整单模式复位，并产生一条购物车变更审计。
            Assert.False(harness.ViewModel.IsWholeOrderOperation);
            Assert.Single(harness.Audit.Events);
        }
    }

    [Theory]
    [InlineData(DiscountAction.Amount)]
    [InlineData(DiscountAction.Percent)]
    [InlineData(DiscountAction.Quick10)]
    public async Task Line_discount_command_on_cart_with_only_return_lines_shows_notice_instead_of_amount_too_high(DiscountAction action)
    {
        // 只有退货行时合计为负：旧行为里折扣金额会拿正数去比负毛额，误报「折扣金额过高」。
        // 行模式下选中退货行仍然是明确的「退货商品不能修改折扣」；整单模式在只有退货行时的提示见下面的 Order_discount_*_only_return_lines_* 用例。
        using var harness = new Harness(withSaleLine: false);
        harness.ViewModel.IsWholeOrderOperation = false;
        harness.ViewModel.SelectedCartLine = harness.ReturnLine;
        var before = harness.CaptureCart();

        await harness.ExecuteAsync(action);

        Assert.Empty(harness.Authorization.RequestedPermissions);
        Assert.Equal(harness.Text(ReturnLineDiscountBlockedKey), harness.ViewModel.StatusMessage);
        Assert.NotEqual(harness.Text("pos.status.discountAmountTooHigh"), harness.ViewModel.StatusMessage);
        AssertCartUnchanged(before, harness);
        AssertNoDiscount(harness.ReturnLine);
    }

    [Theory]
    [InlineData(DiscountAction.Amount)]
    [InlineData(DiscountAction.Percent)]
    [InlineData(DiscountAction.Quick10)]
    [InlineData(DiscountAction.Quick50)]
    public async Task Order_discount_command_on_cart_with_only_return_lines_shows_notice_without_asking_authorization(DiscountAction action)
    {
        // 产品决定：购物车里只有退货行、没有可打折的销售行时，整单折扣无处可落，拒绝并给明确提示，
        // 而且在弹授权 / 主管 PIN 之前就拦下（三个入口都一样）；不再出现「折扣金额过高」或误导的「整单折扣已应用」。
        // 选中退货行和未选中两种情况结果一样：整单模式不看选中行，提示也不是行级的「退货商品不能修改折扣」。
        foreach (var selectedReturnLine in new[] { true, false })
        {
            using var harness = new Harness(withSaleLine: false);
            harness.ViewModel.IsWholeOrderOperation = true;
            harness.ViewModel.SelectedCartLine = selectedReturnLine ? harness.ReturnLine : null;
            var before = harness.CaptureCart();

            await harness.ExecuteAsync(action);

            Assert.Empty(harness.Authorization.RequestedPermissions);
            Assert.Equal(harness.Text(OrderDiscountNoSaleLinesKey), harness.ViewModel.StatusMessage);
            Assert.NotEqual(harness.Text(ReturnLineDiscountBlockedKey), harness.ViewModel.StatusMessage);
            Assert.NotEqual(harness.Text("pos.status.discountAmountTooHigh"), harness.ViewModel.StatusMessage);
            Assert.NotEqual(harness.Text("pos.status.orderDiscountUpdated"), harness.ViewModel.StatusMessage);
            Assert.Equal(StatusFeedbackKind.Error, harness.ViewModel.StatusFeedbackKind);
            Assert.Equal([UserFeedbackCue.OperationError], harness.Feedback.Cues);
            // 数据完全不变（含修订号），键盘输入原样保留，也没有购物车变更审计。
            // 整单开关是一次性的：按下折扣键就用掉这一次，被拒绝也一样回到默认。
            AssertCartUnchanged(before, harness);
            AssertNoDiscount(harness.ReturnLine);
            Assert.False(harness.ViewModel.IsWholeOrderOperation);
            Assert.Equal(harness.KeypadBufferFor(action), harness.ViewModel.KeypadBuffer);
            Assert.Empty(harness.Audit.Events);
        }
    }

    [Fact]
    public async Task Order_discount_notice_for_only_return_lines_lifts_as_soon_as_a_sale_line_is_added()
    {
        using var harness = new Harness(withSaleLine: false);
        harness.ViewModel.IsWholeOrderOperation = true;

        await harness.ExecuteAsync(DiscountAction.Quick10);
        Assert.Empty(harness.Authorization.RequestedPermissions);
        Assert.Equal(harness.Text(OrderDiscountNoSaleLinesKey), harness.ViewModel.StatusMessage);

        // 加入一个销售行后，同一个入口恢复为先走整单折扣的授权、再对销售行打折，退货行不被牵连。
        // 第一次被拒绝已经用掉一次性整单开关，要重新点亮。
        Assert.False(harness.ViewModel.IsWholeOrderOperation);
        harness.ViewModel.IsWholeOrderOperation = true;
        var sale = harness.Cart.AddItem(CreateItem(lookupCode: "LK-SALE", price: 30m));
        await harness.ExecuteAsync(DiscountAction.Quick10);

        Assert.Equal([Permissions.PosTerminal.Sales.OrderQuickDiscount10Percent], harness.Authorization.RequestedPermissions);
        Assert.Equal(harness.Text("pos.status.orderDiscountUpdated"), harness.ViewModel.StatusMessage);
        Assert.Equal(3m, sale.DiscountAmount);
        AssertNoDiscount(harness.ReturnLine);
    }

    [Fact]
    public async Task Order_discount_no_sale_lines_notice_follows_the_current_ui_language()
    {
        using var harness = new Harness(cultureName: "zh-CN", withSaleLine: false);
        harness.ViewModel.IsWholeOrderOperation = true;

        await harness.ExecuteAsync(DiscountAction.Percent);

        Assert.Equal("购物车里没有可打折的销售商品。", harness.ViewModel.StatusMessage);
        Assert.Empty(harness.Authorization.RequestedPermissions);
    }

    [Fact]
    public async Task Return_line_notice_follows_the_current_ui_language()
    {
        using var harness = new Harness(cultureName: "zh-CN");
        harness.ViewModel.SelectedCartLine = harness.ReturnLine;

        await harness.ExecuteAsync(DiscountAction.Amount);
        Assert.Equal("退货商品不能修改折扣。", harness.ViewModel.StatusMessage);

        Assert.Empty(harness.Authorization.RequestedPermissions);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("1.5")]
    [InlineData("")]
    public async Task Quantity_and_price_commands_on_selected_return_line_show_notice_without_asking_authorization(string keypad)
    {
        using var harness = new Harness();
        harness.ViewModel.SelectedCartLine = harness.ReturnLine;
        var before = harness.CaptureCart();

        harness.ViewModel.KeypadBuffer = keypad;
        await ((IAsyncRelayCommand)harness.ViewModel.ModifySelectedLineQuantityCommand).ExecuteAsync(null);

        // 键盘输入不管合不合法，提示都是「退货商品不能改」：不再因 1.5 误报「数量必须为正整数」，空输入也不再提示「输入有效数值」。
        Assert.Empty(harness.Authorization.RequestedPermissions);
        Assert.Equal(harness.Text(ReturnLineQuantityBlockedKey), harness.ViewModel.StatusMessage);
        Assert.Equal(StatusFeedbackKind.Error, harness.ViewModel.StatusFeedbackKind);
        AssertCartUnchanged(before, harness);

        await ((IAsyncRelayCommand)harness.ViewModel.ModifySelectedLinePriceCommand).ExecuteAsync(null);

        Assert.Empty(harness.Authorization.RequestedPermissions);
        Assert.Equal(harness.Text(ReturnLinePriceBlockedKey), harness.ViewModel.StatusMessage);
        Assert.Equal([UserFeedbackCue.OperationError, UserFeedbackCue.OperationError], harness.Feedback.Cues);
        AssertCartUnchanged(before, harness);
        Assert.False(harness.ReturnLine.IsManualPrice);
        Assert.Empty(harness.Audit.Events);
    }

    // ===================================================================================
    // 二、失败对照：工作流层对退货行折扣 / 改数量 / 改价返回明确提示
    // ===================================================================================

    [Fact]
    public void Workflow_line_discount_on_return_line_returns_explicit_notice_instead_of_silence_or_amount_too_high()
    {
        var (cart, sale, returnLine) = CreateMixedCart();
        var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), cart);
        var before = CaptureCart(cart);

        var amountResult = service.ApplySelectedLineDiscountAmount(returnLine, "1", isWholeOrderOperation: false);
        var percentResult = service.ApplySelectedLineDiscountPercent(returnLine, "10", isWholeOrderOperation: false);
        var quickResult = service.ApplyQuickDiscountPercent(returnLine, "10", isWholeOrderOperation: false);

        // 折扣金额旧行为误报 discountAmountTooHigh（1 > 负毛额），百分比 / 快速折扣旧行为是 StatusKey 为空的静默结果。
        foreach (var result in new[] { amountResult, percentResult, quickResult })
        {
            Assert.Equal(ReturnLineDiscountBlockedKey, result.StatusKey);
            Assert.Null(result.SelectedCartLine);
            Assert.False(result.ClearKeypadBuffer);
            Assert.Null(result.WholeOrderOperation);
        }

        Assert.Equal(before, CaptureCart(cart));
        AssertNoDiscount(returnLine);
        AssertNoDiscount(sale);
    }

    [Fact]
    public void Workflow_line_discount_on_return_line_reports_return_before_checking_keypad_or_percent_range()
    {
        var (cart, _, returnLine) = CreateMixedCart();
        var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), cart);

        // 无论键盘输入、档位对不对，退货行都改不了折扣：「输入有效数值」「0 到 100」都只会误导。
        var emptyKeypad = service.ApplySelectedLineDiscountAmount(returnLine, string.Empty, isWholeOrderOperation: false);
        var invalidKeypad = service.ApplySelectedLineDiscountPercent(returnLine, "abc", isWholeOrderOperation: false);
        var outOfRange = service.ApplySelectedLineDiscountPercent(returnLine, "150", isWholeOrderOperation: false);
        var invalidQuick = service.ApplyQuickDiscountPercent(returnLine, "abc", isWholeOrderOperation: false);

        Assert.All(
            new[] { emptyKeypad, invalidKeypad, outOfRange, invalidQuick },
            result => Assert.Equal(ReturnLineDiscountBlockedKey, result.StatusKey));
        AssertNoDiscount(returnLine);
    }

    [Fact]
    public void Workflow_order_discount_with_return_line_in_cart_applies_to_sale_line_for_every_entry_and_every_selection()
    {
        // 预期随新行为改变（原：含退货行时整单折扣的每个入口都返回「有退货商品时不能使用整单折扣」）。
        // 选中退货行 / 销售行 / 未选中，整单模式的结果都一样：成功、复位整单模式、清键盘，且只有销售行有折扣。
        foreach (var selectedName in new[] { "return", "sale", "none" })
        {
            var (cart, sale, returnLine) = CreateMixedCart();
            var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), cart);
            CartLine? selected = selectedName switch
            {
                "return" => returnLine,
                "sale" => sale,
                _ => null
            };

            var amountResult = service.ApplySelectedLineDiscountAmount(selected, "5", isWholeOrderOperation: true);
            Assert.Equal(5m, sale.DiscountAmount);
            var percentResult = service.ApplySelectedLineDiscountPercent(selected, "10", isWholeOrderOperation: true);
            Assert.Equal(3m, sale.DiscountAmount);
            var quickResult = service.ApplyQuickDiscountPercent(selected, "10", isWholeOrderOperation: true);
            Assert.Equal(3m, sale.DiscountAmount);

            foreach (var result in new[] { amountResult, percentResult, quickResult })
            {
                Assert.Equal("pos.status.orderDiscountUpdated", result.StatusKey);
                Assert.True(result.ClearKeypadBuffer);
                Assert.False(result.WholeOrderOperation);
                Assert.Null(result.SelectedCartLine);
            }

            AssertNoDiscount(returnLine);
            Assert.Equal(1m, returnLine.Quantity);
            Assert.Equal(8m, returnLine.UnitPrice);
            Assert.Equal(19m, cart.ActualAmount);
        }
    }

    [Fact]
    public void Workflow_order_discount_amount_cap_with_return_line_is_sale_gross_not_net_total()
    {
        // 金额上限按销售行毛额合计（30）比较，而不是含退货行负数的合计（22）：
        // 25 超过合计 22 但没超过销售行 30，必须通过；30 恰好等于上限，也通过；30.01 才是「折扣金额过高」。
        var (cart, sale, returnLine) = CreateMixedCart();
        var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), cart);

        Assert.Equal(
            "pos.status.orderDiscountUpdated",
            service.ApplySelectedLineDiscountAmount(sale, "25", isWholeOrderOperation: true).StatusKey);
        Assert.Equal(25m, sale.DiscountAmount);
        Assert.Equal(
            "pos.status.orderDiscountUpdated",
            service.ApplySelectedLineDiscountAmount(sale, "30", isWholeOrderOperation: true).StatusKey);
        Assert.Equal(30m, sale.DiscountAmount);
        Assert.Equal(-8m, cart.ActualAmount);

        var before = CaptureCart(cart);
        Assert.Equal(
            "pos.status.discountAmountTooHigh",
            service.ApplySelectedLineDiscountAmount(sale, "30.01", isWholeOrderOperation: true).StatusKey);
        Assert.Equal(before, CaptureCart(cart));
        AssertNoDiscount(returnLine);
    }

    [Fact]
    public void Workflow_line_discount_on_cart_with_only_return_lines_never_reports_amount_too_high()
    {
        var cart = new PosCartService();
        var returnLine = cart.AddReturnLine(CreateReturnRequest("RET-1", unitPrice: 8m));
        var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), cart);
        var before = CaptureCart(cart);

        // 行模式下选中退货行仍然是明确的拒绝提示；整单模式在只有退货行时的提示见下一个用例。
        Assert.Equal(
            ReturnLineDiscountBlockedKey,
            service.ApplySelectedLineDiscountAmount(returnLine, "1", isWholeOrderOperation: false).StatusKey);

        Assert.Equal(before, CaptureCart(cart));
        AssertNoDiscount(returnLine);
    }

    [Fact]
    public void Workflow_order_discount_on_cart_with_only_return_lines_returns_notice_for_every_entry_and_keeps_data()
    {
        var cart = new PosCartService();
        var returnLine = cart.AddReturnLine(CreateReturnRequest("RET-1", unitPrice: 8m));
        var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), cart);
        var before = CaptureCart(cart);

        // 三个入口、选中退货行 / 未选中：提示先于键盘输入 / 百分比范围 / 金额上限校验，所以无效输入、越界输入也是同一条提示。
        foreach (var selected in new CartLine?[] { returnLine, null })
        {
            var results = new[]
            {
                service.ApplySelectedLineDiscountAmount(selected, "5", isWholeOrderOperation: true),
                service.ApplySelectedLineDiscountAmount(selected, "0", isWholeOrderOperation: true),
                service.ApplySelectedLineDiscountAmount(selected, "abc", isWholeOrderOperation: true),
                service.ApplySelectedLineDiscountAmount(selected, "5000", isWholeOrderOperation: true),
                service.ApplySelectedLineDiscountPercent(selected, "10", isWholeOrderOperation: true),
                service.ApplySelectedLineDiscountPercent(selected, "150", isWholeOrderOperation: true),
                service.ApplyQuickDiscountPercent(selected, "10", isWholeOrderOperation: true),
                service.ApplyQuickDiscountPercent(selected, "50", isWholeOrderOperation: true)
            };

            foreach (var result in results)
            {
                Assert.Equal(OrderDiscountNoSaleLinesKey, result.StatusKey);
                Assert.False(result.ClearKeypadBuffer);
                // 不替收银员退出整单模式（成功路径才会把它复位为 false）。
                Assert.Null(result.WholeOrderOperation);
                Assert.Null(result.SelectedCartLine);
            }
        }

        // 数据完全不变，包括修订号：没有任何购物车变更通知。
        Assert.Equal(before, CaptureCart(cart));
        AssertNoDiscount(returnLine);
    }

    [Fact]
    public void Workflow_order_discount_notice_does_not_change_empty_cart_or_no_return_line_behavior()
    {
        // 特征化：空购物车仍是「请先选择商品行」；没有退货行的购物车（包括只含零价销售行）沿用既有行为，不出现新提示。
        var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), new PosCartService());
        Assert.Equal(
            "pos.status.selectCartLine",
            service.ApplySelectedLineDiscountAmount(null, "5", isWholeOrderOperation: true).StatusKey);
        Assert.Equal(
            "pos.status.selectCartLine",
            service.ApplySelectedLineDiscountPercent(null, "10", isWholeOrderOperation: true).StatusKey);
        Assert.Equal(
            "pos.status.selectCartLine",
            service.ApplyQuickDiscountPercent(null, "10", isWholeOrderOperation: true).StatusKey);

        var zeroPriceCart = new PosCartService();
        var zeroPriceLine = zeroPriceCart.AddItem(CreateItem(productCode: "SKU-ZERO", lookupCode: "LK-ZERO", price: 0m));
        var zeroPriceService = new PosTerminalWorkflowService(new LocalSellableItemIndex(), zeroPriceCart);
        Assert.Equal(
            "pos.status.discountAmountTooHigh",
            zeroPriceService.ApplySelectedLineDiscountAmount(zeroPriceLine, "5", isWholeOrderOperation: true).StatusKey);
        Assert.Equal(
            "pos.status.orderDiscountUpdated",
            zeroPriceService.ApplySelectedLineDiscountPercent(zeroPriceLine, "10", isWholeOrderOperation: true).StatusKey);
        AssertNoDiscount(zeroPriceLine);
    }

    [Fact]
    public void Workflow_quantity_and_price_edit_on_return_line_returns_explicit_notice()
    {
        var (cart, sale, returnLine) = CreateMixedCart();
        var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), cart);
        var before = CaptureCart(cart);

        // 旧行为：键盘值合法时是静默的空结果；1.5 则误报「数量必须为正整数」。
        foreach (var keypad in new[] { "3", "1.5", "0", string.Empty })
        {
            var quantityResult = service.ModifySelectedLineQuantity(returnLine, keypad);
            Assert.Equal(ReturnLineQuantityBlockedKey, quantityResult.StatusKey);
            Assert.False(quantityResult.ClearKeypadBuffer);
            Assert.Null(quantityResult.SelectedCartLine);
        }

        foreach (var keypad in new[] { "9", "0", string.Empty })
        {
            var priceResult = service.ModifySelectedLinePrice(returnLine, keypad);
            Assert.Equal(ReturnLinePriceBlockedKey, priceResult.StatusKey);
            Assert.False(priceResult.ClearKeypadBuffer);
            Assert.Null(priceResult.SelectedCartLine);
        }

        Assert.Equal(before, CaptureCart(cart));
        Assert.Equal(1m, returnLine.Quantity);
        Assert.Equal(8m, returnLine.UnitPrice);
        Assert.False(returnLine.IsManualPrice);
        AssertNoDiscount(returnLine);
        AssertNoDiscount(sale);
    }

    [Fact]
    public void Resource_texts_for_return_line_notices_exist_in_both_languages()
    {
        var english = CultureInfo.GetCultureInfo("en-US");
        var chinese = CultureInfo.GetCultureInfo("zh-CN");

        Assert.Equal("Returned items cannot be discounted.", LocalizationService.Translate(ReturnLineDiscountBlockedKey, english));
        Assert.Equal("退货商品不能修改折扣。", LocalizationService.Translate(ReturnLineDiscountBlockedKey, chinese));
        Assert.Equal(
            "There are no sale items in the cart to discount.",
            LocalizationService.Translate(OrderDiscountNoSaleLinesKey, english));
        Assert.Equal("购物车里没有可打折的销售商品。", LocalizationService.Translate(OrderDiscountNoSaleLinesKey, chinese));
        Assert.Equal("Returned item quantity cannot be changed.", LocalizationService.Translate(ReturnLineQuantityBlockedKey, english));
        Assert.Equal("退货商品不能修改数量。", LocalizationService.Translate(ReturnLineQuantityBlockedKey, chinese));
        Assert.Equal("Returned item price cannot be changed.", LocalizationService.Translate(ReturnLinePriceBlockedKey, english));
        Assert.Equal("退货商品不能修改价格。", LocalizationService.Translate(ReturnLinePriceBlockedKey, chinese));
    }

    // ===================================================================================
    // 三、特征化：销售行的折扣 / 改数量 / 改价流程完全不受影响（旧代码上同样通过）
    // ===================================================================================

    [Theory]
    [InlineData(DiscountAction.Amount, Permissions.PosTerminal.Sales.LineManualDiscount)]
    [InlineData(DiscountAction.Percent, Permissions.PosTerminal.Sales.LineManualDiscount)]
    [InlineData(DiscountAction.Quick10, Permissions.PosTerminal.Sales.LineQuickDiscount10Percent)]
    [InlineData(DiscountAction.Quick50, Permissions.PosTerminal.Sales.LineQuickDiscount50Percent)]
    public async Task Line_discount_on_sale_line_still_asks_authorization_first_then_applies_even_when_cart_has_return_line(
        DiscountAction action,
        string expectedPermission)
    {
        using var harness = new Harness();
        harness.ViewModel.SelectedCartLine = harness.SaleLine;

        await harness.ExecuteAsync(action);

        // 先授权（恰好一次，权限码按入口区分），再应用折扣；退货行不被牵连。
        Assert.Equal([expectedPermission], harness.Authorization.RequestedPermissions);
        Assert.True(harness.SaleLine.HasManualDiscount);
        Assert.Equal(harness.Text("pos.status.lineDiscountUpdated"), harness.ViewModel.StatusMessage);
        var expectedDiscount = action switch
        {
            DiscountAction.Amount => 1m,
            DiscountAction.Percent => 3m,
            DiscountAction.Quick10 => 3m,
            DiscountAction.Quick50 => 15m,
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        Assert.Equal((decimal)expectedDiscount, harness.SaleLine.DiscountAmount);
        AssertNoDiscount(harness.ReturnLine);
        Assert.Single(harness.Audit.Events);
    }

    [Theory]
    [InlineData(DiscountAction.Amount, Permissions.PosTerminal.Sales.OrderManualDiscount, 1)]
    [InlineData(DiscountAction.Percent, Permissions.PosTerminal.Sales.OrderManualDiscount, 3)]
    [InlineData(DiscountAction.Quick10, Permissions.PosTerminal.Sales.OrderQuickDiscount10Percent, 3)]
    public async Task Order_discount_without_return_line_still_asks_authorization_first_then_applies(
        DiscountAction action,
        string expectedPermission,
        int expectedDiscount)
    {
        using var harness = new Harness(withReturnLine: false);
        harness.ViewModel.IsWholeOrderOperation = true;

        await harness.ExecuteAsync(action);

        Assert.Equal([expectedPermission], harness.Authorization.RequestedPermissions);
        Assert.Equal(harness.Text("pos.status.orderDiscountUpdated"), harness.ViewModel.StatusMessage);
        Assert.Equal((decimal)expectedDiscount, harness.SaleLine.DiscountAmount);
        // 成功路径照旧会把整单模式复位。
        Assert.False(harness.ViewModel.IsWholeOrderOperation);
    }

    [Fact]
    public async Task Quantity_and_price_on_sale_line_still_ask_authorization_first_then_apply_even_when_cart_has_return_line()
    {
        using var harness = new Harness();
        harness.ViewModel.SelectedCartLine = harness.SaleLine;

        harness.ViewModel.KeypadBuffer = "3";
        await ((IAsyncRelayCommand)harness.ViewModel.ModifySelectedLineQuantityCommand).ExecuteAsync(null);
        harness.ViewModel.SelectedCartLine = harness.SaleLine;
        harness.ViewModel.KeypadBuffer = "12.5";
        await ((IAsyncRelayCommand)harness.ViewModel.ModifySelectedLinePriceCommand).ExecuteAsync(null);

        Assert.Equal(
            [Permissions.PosTerminal.Sales.ChangeQuantity, Permissions.PosTerminal.Sales.ChangePrice],
            harness.Authorization.RequestedPermissions);
        Assert.Equal(3m, harness.SaleLine.Quantity);
        Assert.Equal(12.5m, harness.SaleLine.UnitPrice);
        Assert.True(harness.SaleLine.IsManualPrice);
        Assert.Equal(1m, harness.ReturnLine.Quantity);
        Assert.Equal(8m, harness.ReturnLine.UnitPrice);
        AssertNoDiscount(harness.ReturnLine);
    }

    [Fact]
    public async Task Blocked_return_line_attempt_leaves_no_state_that_affects_the_next_sale_line_discount()
    {
        using var harness = new Harness();
        harness.ViewModel.SelectedCartLine = harness.ReturnLine;
        await harness.ExecuteAsync(DiscountAction.Percent);
        Assert.Empty(harness.Authorization.RequestedPermissions);

        // 收银员改选销售行后，同一条命令照常先授权再应用。
        harness.ViewModel.SelectedCartLine = harness.SaleLine;
        await harness.ExecuteAsync(DiscountAction.Percent);

        Assert.Equal([Permissions.PosTerminal.Sales.LineManualDiscount], harness.Authorization.RequestedPermissions);
        Assert.Equal(3m, harness.SaleLine.DiscountAmount);
        Assert.Equal(harness.Text("pos.status.lineDiscountUpdated"), harness.ViewModel.StatusMessage);
        AssertNoDiscount(harness.ReturnLine);
    }

    [Fact]
    public async Task Discount_without_selected_line_or_with_empty_cart_keeps_existing_select_line_notice()
    {
        // 未选中行：照旧先授权，再由工作流提示「请先选择商品行」（本次不改这条路径）。
        using var harness = new Harness();
        harness.ViewModel.SelectedCartLine = null;
        harness.ViewModel.KeypadBuffer = "1";
        var before = harness.CaptureCart();

        await harness.ExecuteAsync(DiscountAction.Amount);

        Assert.Equal([Permissions.PosTerminal.Sales.LineManualDiscount], harness.Authorization.RequestedPermissions);
        Assert.Equal(harness.Text("pos.status.selectCartLine"), harness.ViewModel.StatusMessage);
        AssertCartUnchanged(before, harness);

        // 空购物车 + 整单模式：照旧先授权，再提示「请先选择商品行」。
        using var emptyHarness = new Harness(withSaleLine: false, withReturnLine: false);
        emptyHarness.ViewModel.IsWholeOrderOperation = true;
        emptyHarness.ViewModel.KeypadBuffer = "1";

        await emptyHarness.ExecuteAsync(DiscountAction.Amount);

        Assert.Equal([Permissions.PosTerminal.Sales.OrderManualDiscount], emptyHarness.Authorization.RequestedPermissions);
        Assert.Equal(emptyHarness.Text("pos.status.selectCartLine"), emptyHarness.ViewModel.StatusMessage);
        Assert.True(emptyHarness.Cart.IsEmpty);
    }

    [Fact]
    public void Workflow_sale_line_flows_are_unchanged_when_cart_has_return_line()
    {
        var (cart, sale, returnLine) = CreateMixedCart();
        var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), cart);

        // 销售行的输入校验顺序不变：无效键盘值 / 超范围 / 金额过高仍各报各的。
        Assert.Equal(
            "pos.status.invalidKeypadValue",
            service.ApplySelectedLineDiscountPercent(sale, "abc", isWholeOrderOperation: false).StatusKey);
        Assert.Equal(
            "pos.status.discountPercentOutOfRange",
            service.ApplySelectedLineDiscountPercent(sale, "150", isWholeOrderOperation: false).StatusKey);
        Assert.Equal(
            "pos.status.discountAmountTooHigh",
            service.ApplySelectedLineDiscountAmount(sale, "31", isWholeOrderOperation: false).StatusKey);
        Assert.Equal(
            "pos.status.selectCartLine",
            service.ApplySelectedLineDiscountAmount(null, "1", isWholeOrderOperation: false).StatusKey);
        Assert.Equal(
            "pos.status.invalidKeypadValue",
            service.ModifySelectedLineQuantity(sale, string.Empty).StatusKey);
        Assert.Equal(
            "cart.status.quantityMustBeInteger",
            service.ModifySelectedLineQuantity(sale, "1.5").StatusKey);
        Assert.Equal(
            "pos.status.selectCartLine",
            service.ModifySelectedLinePrice(null, "1").StatusKey);

        // 合法输入照常生效，并选中被修改的行、清空键盘。
        var amountResult = service.ApplySelectedLineDiscountAmount(sale, "5", isWholeOrderOperation: false);
        Assert.Equal("pos.status.lineDiscountUpdated", amountResult.StatusKey);
        Assert.Same(sale, amountResult.SelectedCartLine);
        Assert.True(amountResult.ClearKeypadBuffer);
        Assert.Equal(5m, sale.DiscountAmount);

        var quickResult = service.ApplyQuickDiscountPercent(sale, "50", isWholeOrderOperation: false);
        Assert.Equal("pos.status.lineDiscountUpdated", quickResult.StatusKey);
        Assert.Equal(15m, sale.DiscountAmount);

        var quantityResult = service.ModifySelectedLineQuantity(sale, "2");
        Assert.Equal("pos.status.lineQuantityUpdated", quantityResult.StatusKey);
        Assert.Equal(2m, sale.Quantity);

        var priceResult = service.ModifySelectedLinePrice(sale, "40");
        Assert.Equal("pos.status.linePriceUpdated", priceResult.StatusKey);
        Assert.Equal(40m, sale.UnitPrice);

        AssertNoDiscount(returnLine);
        Assert.Equal(1m, returnLine.Quantity);
        Assert.Equal(8m, returnLine.UnitPrice);
    }

    [Fact]
    public void Workflow_order_discount_without_return_line_is_unchanged()
    {
        var cart = new PosCartService();
        var sale = cart.AddItem(CreateItem(lookupCode: "LK-SALE", price: 30m));
        var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), cart);

        Assert.Equal(
            "pos.status.discountAmountTooHigh",
            service.ApplySelectedLineDiscountAmount(sale, "31", isWholeOrderOperation: true).StatusKey);
        Assert.Equal(
            "pos.status.discountPercentOutOfRange",
            service.ApplyQuickDiscountPercent(sale, "150", isWholeOrderOperation: true).StatusKey);

        var result = service.ApplySelectedLineDiscountPercent(sale, "10", isWholeOrderOperation: true);

        Assert.Equal("pos.status.orderDiscountUpdated", result.StatusKey);
        Assert.False(result.WholeOrderOperation);
        Assert.True(result.ClearKeypadBuffer);
        Assert.Equal(3m, sale.DiscountAmount);
    }

    // ===================================================================================
    // 辅助
    // ===================================================================================

    private static PosSessionState Session => new("HB POS", StoreCode, "Main Store", "POS-01", "C001", "Alice", true, 0);

    /// <summary>购物车：销售行 SKU-SALE(30) + 退货行 SKU-RET(8)。</summary>
    private static (PosCartService Cart, CartLine Sale, CartLine Return) CreateMixedCart()
    {
        var cart = new PosCartService();
        var sale = cart.AddItem(CreateItem(lookupCode: "LK-SALE", price: 30m));
        var returnLine = cart.AddReturnLine(CreateReturnRequest("RET-1", lookupCode: "LK-RET", unitPrice: 8m));
        return (cart, sale, returnLine);
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

    /// <summary>
    /// 购物车数据指纹：修订号（任何变更通知都会推进）、合计 / 折扣 / 实收，以及每行的类型、数量、单价、折扣金额 / 百分比 / 来源、
    /// 手工价标记、目录基线。指纹相等即「数据不变」。
    /// </summary>
    private static string CaptureCart(PosCartService cart)
    {
        var lines = cart.Lines.Select(line => string.Join(
            ":",
            line.Kind,
            line.LookupCode,
            line.Quantity,
            line.UnitPrice,
            line.DiscountAmount,
            line.DiscountPercent?.ToString(CultureInfo.InvariantCulture) ?? "-",
            line.DiscountSource,
            line.IsManualPrice,
            line.CatalogDiscountBasisPoints));
        return $"rev={cart.Revision};total={cart.TotalAmount};discount={cart.DiscountAmount};actual={cart.ActualAmount};" +
               string.Join("|", lines);
    }

    private static void AssertCartUnchanged(string before, Harness harness)
    {
        Assert.Equal(before, harness.CaptureCart());
    }

    private static ReturnCartLineRequest CreateReturnRequest(
        string sourceKey,
        string productCode = "SKU-RET",
        string lookupCode = "LK-RET",
        decimal unitPrice = 10m)
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

    private static SellableItemDto CreateItem(
        string productCode = "SKU-SALE",
        string lookupCode = "LK-SALE",
        decimal price = 10m)
    {
        return new SellableItemDto(
            StoreCode: StoreCode,
            ProductCode: productCode,
            ReferenceCode: null,
            DisplayName: $"Item {productCode}",
            LookupCode: lookupCode,
            ItemNumber: productCode,
            Barcode: lookupCode,
            RetailPrice: price,
            PriceSource: PriceSourceKind.StoreRetailPrice,
            PriceSourceLabel: PriceSourceKind.StoreRetailPrice.ToString(),
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 收银页夹具：真实的购物车 / 工作流 / ViewModel，授权服务换成只记录调用的替身（恒批准），
    /// 默认购物车 = 销售行 SKU-SALE(30) + 退货行 SKU-RET(8)。
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly CultureInfo _culture;

        public Harness(string cultureName = "en-US", bool withSaleLine = true, bool withReturnLine = true)
        {
            _culture = CultureInfo.GetCultureInfo(cultureName);
            Cart = new PosCartService();
            Authorization = new RecordingAuthorizationService();
            Feedback = new RecordingUserFeedbackService();
            Audit = new RecordingOperationAuditLogger();
            ViewModel = new PosTerminalViewModel(
                new LocalSellableItemIndex(),
                Cart,
                Session,
                onOpenPayment: null,
                localization: new ResourceLocalization(_culture),
                userFeedbackService: Feedback,
                operationAuditLogger: Audit,
                operationAuthorizationService: Authorization);

            // ViewModel 先建好再加行，与既有收银页测试一致（购物车变更事件驱动列表刷新）。
            if (withSaleLine)
            {
                SaleLine = Cart.AddItem(CreateItem(lookupCode: "LK-SALE", price: 30m));
            }

            if (withReturnLine)
            {
                ReturnLine = Cart.AddReturnLine(CreateReturnRequest("RET-1", lookupCode: "LK-RET", unitPrice: 8m));
            }
        }

        public PosCartService Cart { get; }

        public RecordingAuthorizationService Authorization { get; }

        public RecordingUserFeedbackService Feedback { get; }

        public RecordingOperationAuditLogger Audit { get; }

        public PosTerminalViewModel ViewModel { get; }

        public CartLine SaleLine { get; } = null!;

        public CartLine ReturnLine { get; } = null!;

        public string Text(string key) => LocalizationService.Translate(key, _culture);

        public string CaptureCart() => ReturnLineDiscountFeedbackTests.CaptureCart(Cart);

        /// <summary>每个入口按钮点击前，收银员键盘上输入的值。</summary>
        public string KeypadBufferFor(DiscountAction action) => action switch
        {
            DiscountAction.Amount => "1",
            DiscountAction.Percent => "10",
            _ => string.Empty
        };

        public Task ExecuteAsync(DiscountAction action)
        {
            switch (action)
            {
                case DiscountAction.Amount:
                    ViewModel.KeypadBuffer = KeypadBufferFor(action);
                    return ((IAsyncRelayCommand)ViewModel.ApplySelectedLineDiscountAmountCommand).ExecuteAsync(null);
                case DiscountAction.Percent:
                    ViewModel.KeypadBuffer = KeypadBufferFor(action);
                    return ((IAsyncRelayCommand)ViewModel.ApplySelectedLineDiscountPercentCommand).ExecuteAsync(null);
                case DiscountAction.Quick10:
                    return ((IAsyncRelayCommand<string>)ViewModel.ApplyQuickDiscountPercentCommand).ExecuteAsync("10");
                case DiscountAction.Quick50:
                    return ((IAsyncRelayCommand<string>)ViewModel.ApplyQuickDiscountPercentCommand).ExecuteAsync("50");
                default:
                    throw new ArgumentOutOfRangeException(nameof(action));
            }
        }

        public void Dispose()
        {
            ViewModel.Dispose();
        }
    }

    /// <summary>只记录请求过哪些权限、恒批准的授权服务替身：命令层「是否弹授权」就看它有没有被调用。</summary>
    private sealed class RecordingAuthorizationService : IOperationAuthorizationService
    {
        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

        public event EventHandler? StatusChanged
        {
            add { }
            remove { }
        }

        public string ScannerPageId => "ReturnLineDiscountFeedbackTest";

        public bool IsPromptOpen => false;

        public bool IsBusy => false;

        public string PromptMessage => string.Empty;

        public string StatusMessage => string.Empty;

        public string PermissionCode => string.Empty;

        public string Screen => string.Empty;

        public string Action => string.Empty;

        public IRelayCommand CancelCommand { get; } = new RelayCommand(() => { });

        public List<string> RequestedPermissions { get; } = [];

        public Task<OperationAuthorizationScope?> AuthorizeAsync(
            string permissionCode,
            string screen,
            string action,
            PosSessionState session,
            CancellationToken cancellationToken = default)
        {
            RequestedPermissions.Add(permissionCode);
            var requestingSession = new CashierSessionDto(
                session.CashierId,
                session.CashierId,
                session.CashierName,
                session.StoreCode,
                session.DeviceCode,
                [],
                [],
                [session.StoreCode],
                false,
                false,
                false);
            return Task.FromResult<OperationAuthorizationScope?>(
                new OperationAuthorizationScope(requestingSession, permissionCode, screen, action));
        }

        public bool ProcessScannerBarcode(string barcode) => false;

        public void Cancel()
        {
        }

        public void RevokeAll()
        {
        }
    }

    /// <summary>
    /// 与线程文化无关的本地化替身：直接按固定文化读 resx，不去翻转进程 / 线程默认文化，
    /// 所以不会像 <see cref="LocalizationService"/> 的构造函数那样泄漏文化到其它并行测试。
    /// </summary>
    private sealed class ResourceLocalization(CultureInfo culture) : ILocalizationService
    {
        public IReadOnlyList<CultureInfo> AvailableCultures { get; } = [culture];

        public CultureInfo CurrentCulture => culture;

        public event EventHandler? CultureChanged
        {
            add { }
            remove { }
        }

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

        public void SetCulture(string cultureName) => throw new NotSupportedException();

        public void SetCulture(CultureInfo culture) => throw new NotSupportedException();

        public Task SetCultureAsync(string cultureName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public string T(string key) => LocalizationService.Translate(key, culture);
    }
}
