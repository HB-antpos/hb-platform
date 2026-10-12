using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Promotions;

namespace Hbpos.Client.Tests;

/// <summary>
/// 退货行折扣保护核查：购物车里的退货行（CartLineKind.Return）不能被任何路径写入折扣。
/// 覆盖路径：手工行折扣 / 整单折扣 / 两套自动促销引擎 / 目录重载 / 远程复核 / 加购合并 /
/// 特价商品页加购与撤销 / 改数量与改价 / 本地挂单取单 / 共享挂单取单 / 支付恢复发布。
///
/// 测试分两类（与 PR 描述一致）：
/// 1. 失败对照：钉住本次修复的缺口，还原生产文件后应当失败（见 UpdateLineFromRemote_by_line_reference_*）。
/// 2. 特征化：路径本来就已保护，旧代码上同样通过，只是把「退货行折扣始终为 0」钉成回归护栏。
/// </summary>
public sealed class ReturnLineDiscountGuardTests
{
    private const string StoreCode = "S001";

    // ===================================================================================
    // 一、失败对照（修复前应失败）
    // ===================================================================================

    [Fact]
    public void UpdateLineFromRemote_by_line_reference_rejects_return_line_and_keeps_price_metadata_and_discount()
    {
        // 缺口：按行引用的重载没有退货行守卫，只靠唯一的生产调用方（PosTerminalWorkflowService）事先过滤。
        // 一旦有人传入退货行，UpdateFrom 会把退货单价、商品编码、显示名、价格来源改成目录值。
        var cart = new PosCartService();
        var returnLine = cart.AddReturnLine(CreateReturnRequest("RET-1", unitPrice: 8m));
        var revisionBefore = cart.Revision;

        var applied = cart.UpdateLineFromRemote(
            returnLine,
            CreateItem(
                productCode: "SKU-OTHER",
                lookupCode: "LK-OTHER",
                price: 99m,
                displayName: "Remote renamed",
                priceSource: PriceSourceKind.ProductBase,
                discountRate: 0.5m));

        Assert.False(applied);
        Assert.Equal(revisionBefore, cart.Revision);
        Assert.Equal("SKU-A", returnLine.ProductCode);
        Assert.Equal("LK-A1", returnLine.LookupCode);
        Assert.Equal("Item SKU-A", returnLine.DisplayName);
        Assert.Equal(8m, returnLine.UnitPrice);
        Assert.Equal(PriceSourceKind.StoreRetailPrice, returnLine.PriceSource);
        AssertNoDiscount(returnLine);
        Assert.Equal(-8m, cart.ActualAmount);
    }

    [Fact]
    public void UpdateLineFromRemote_by_line_reference_updates_sale_line_even_when_return_line_shares_lookup_code()
    {
        // 守卫只能挡退货行，同码的销售行照常被远程复核更新（防止守卫写得过宽）。
        var cart = new PosCartService();
        var returnLine = cart.AddReturnLine(CreateReturnRequest("RET-1", unitPrice: 8m));
        var saleLine = cart.AddItem(CreateItem(price: 10m));
        var remote = CreateItem(price: 12m, discountRate: 0.25m);

        Assert.False(cart.UpdateLineFromRemote(returnLine, remote));
        Assert.True(cart.UpdateLineFromRemote(saleLine, remote));

        Assert.Equal(12m, saleLine.UnitPrice);
        Assert.Equal(3m, saleLine.DiscountAmount);
        Assert.Equal(8m, returnLine.UnitPrice);
        AssertNoDiscount(returnLine);
    }

    // ===================================================================================
    // 二、特征化：手工行折扣 / 整单折扣
    // ===================================================================================

    [Fact]
    public void Manual_line_discount_is_rejected_for_return_line_and_leaves_no_trace()
    {
        var cart = new PosCartService();
        var sale = cart.AddItem(CreateItem(lookupCode: "LK-A1", price: 10m));
        var returnLine = cart.AddReturnLine(CreateReturnRequest("RET-1", unitPrice: 8m));
        var revisionBefore = cart.Revision;

        Assert.False(cart.SetLineDiscountAmount(returnLine, 1m));
        Assert.False(cart.SetLineDiscountAmount(returnLine, 0m));
        Assert.False(cart.SetLineDiscountAmount(returnLine, 100m));
        Assert.False(cart.SetLineDiscountPercent(returnLine, 10m));
        Assert.False(cart.SetLineDiscountPercent(returnLine, 0m));
        Assert.False(cart.SetLineDiscountPercent(returnLine, 100m));

        // 被拒绝的操作不能触发任何购物车通知，也不能留下折扣来源/金额。
        Assert.Equal(revisionBefore, cart.Revision);
        AssertNoDiscount(returnLine);
        Assert.Equal(0m, sale.DiscountAmount);
        Assert.Equal(0m, cart.DiscountAmount);
        Assert.Equal(2m, cart.ActualAmount);
    }

    [Fact]
    public void Cart_line_discount_setters_throw_for_return_line()
    {
        // 第二层保护：即使绕过 PosCartService 直接调用 CartLine 的折扣写入口，也会因锁定行而抛异常。
        var cart = new PosCartService();
        var returnLine = cart.AddReturnLine(CreateReturnRequest("RET-1", unitPrice: 8m));

        Assert.Throws<InvalidOperationException>(() => returnLine.SetDiscountAmount(1m));
        Assert.Throws<InvalidOperationException>(() => returnLine.SetDiscountPercent(10m));
        Assert.Throws<InvalidOperationException>(() => returnLine.SetOrderDiscountAmount(1m));
        Assert.Throws<InvalidOperationException>(() => returnLine.SetPromotionDiscountAmount(1m));
        Assert.Throws<InvalidOperationException>(() => returnLine.SetAutomaticPromotionDiscountAmount(1m));
        AssertNoDiscount(returnLine);
    }

    [Fact]
    public void Whole_order_discount_is_rejected_for_the_entire_cart_when_a_return_line_exists()
    {
        var cart = new PosCartService();
        var sale = cart.AddItem(CreateItem(lookupCode: "LK-A1", price: 30m));
        var returnLine = cart.AddReturnLine(CreateReturnRequest("RET-1", unitPrice: 8m));
        var revisionBefore = cart.Revision;

        Assert.False(cart.SetOrderDiscountAmount(5m));
        Assert.False(cart.SetOrderDiscountAmount(0m));
        Assert.False(cart.SetOrderDiscountPercent(10m));
        Assert.False(cart.SetOrderDiscountPercent(0m));

        // 整单折扣是「全有或全无」：有退货行时销售行也不会被部分分摊。
        Assert.Equal(revisionBefore, cart.Revision);
        AssertNoDiscount(returnLine);
        AssertNoDiscount(sale);
        Assert.Equal(22m, cart.ActualAmount);
    }

    [Fact]
    public void Order_discount_taken_before_the_return_line_was_added_never_lands_on_return_line()
    {
        // 先整单折扣，再加退货行：整单折扣已经按行固化在销售行上，之后任何刷新都不会再分摊给退货行。
        var cart = new PosCartService();
        var saleA = cart.AddItem(CreateItem(lookupCode: "LK-A1", price: 10m));
        var saleB = cart.AddItem(CreateItem(productCode: "SKU-B", lookupCode: "LK-B1", price: 30m));
        Assert.True(cart.SetOrderDiscountAmount(8m));
        Assert.Equal(8m, cart.DiscountAmount);

        var returnLine = cart.AddReturnLine(CreateReturnRequest("RET-1", unitPrice: 8m));
        AssertNoDiscount(returnLine);

        // 后续销售行改数量、加购、删行都会刷新折扣；退货行仍然没有折扣。
        Assert.True(cart.SetLineQuantity(saleA, 2m));
        AssertNoDiscount(returnLine);
        cart.AddItem(CreateItem(productCode: "SKU-C", lookupCode: "LK-C1", price: 5m));
        AssertNoDiscount(returnLine);
        Assert.True(cart.RemoveLine(saleB));
        AssertNoDiscount(returnLine);

        // 此时再要整单折扣仍被拒绝。
        Assert.False(cart.SetOrderDiscountAmount(1m));
        AssertNoDiscount(returnLine);
    }

    [Fact]
    public void Workflow_line_discount_commands_reject_return_line_but_still_discount_sale_line()
    {
        var cart = new PosCartService();
        var sale = cart.AddItem(CreateItem(lookupCode: "LK-A1", price: 10m));
        var returnLine = cart.AddReturnLine(CreateReturnRequest("RET-1", unitPrice: 8m));
        var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), cart);

        var amountResult = service.ApplySelectedLineDiscountAmount(returnLine, "1", isWholeOrderOperation: false);
        var percentResult = service.ApplySelectedLineDiscountPercent(returnLine, "10", isWholeOrderOperation: false);
        var quickResult = service.ApplyQuickDiscountPercent(returnLine, "10", isWholeOrderOperation: false);

        // 不断言拒绝时的具体提示（是否静默属于待产品拍板的语义），只钉「不成功、不选中、无折扣」。
        foreach (var result in new[] { amountResult, percentResult, quickResult })
        {
            Assert.NotEqual("pos.status.lineDiscountUpdated", result.StatusKey);
            Assert.Null(result.SelectedCartLine);
        }

        AssertNoDiscount(returnLine);
        Assert.Equal(0m, cart.DiscountAmount);

        // 对照：同一购物车里的销售行折扣命令照常生效，且不会牵连退货行。
        var saleResult = service.ApplySelectedLineDiscountPercent(sale, "10", isWholeOrderOperation: false);
        Assert.Equal("pos.status.lineDiscountUpdated", saleResult.StatusKey);
        Assert.Equal(1m, sale.DiscountAmount);
        AssertNoDiscount(returnLine);
    }

    [Fact]
    public void Workflow_whole_order_discount_commands_reject_cart_with_return_line()
    {
        var cart = new PosCartService();
        var sale = cart.AddItem(CreateItem(lookupCode: "LK-A1", price: 30m));
        var returnLine = cart.AddReturnLine(CreateReturnRequest("RET-1", unitPrice: 8m));
        var service = new PosTerminalWorkflowService(new LocalSellableItemIndex(), cart);

        var amountResult = service.ApplySelectedLineDiscountAmount(returnLine, "5", isWholeOrderOperation: true);
        var percentResult = service.ApplySelectedLineDiscountPercent(returnLine, "10", isWholeOrderOperation: true);
        var quickResult = service.ApplyQuickDiscountPercent(returnLine, "10", isWholeOrderOperation: true);

        foreach (var result in new[] { amountResult, percentResult, quickResult })
        {
            Assert.NotEqual("pos.status.orderDiscountUpdated", result.StatusKey);
        }

        AssertNoDiscount(returnLine);
        AssertNoDiscount(sale);
        Assert.Equal(0m, cart.DiscountAmount);
    }

    [Fact]
    public void Pos_terminal_discount_commands_leave_selected_return_line_discount_free()
    {
        // 视图模型层：收银员选中退货行后点「折扣金额 / 折扣% / 快速折扣」（行模式与整单模式），
        // 命令照常走授权 -> 工作流 -> 购物车，最终购物车拒绝，退货行与销售行都没有折扣。
        var cart = new PosCartService();
        var viewModel = new PosTerminalViewModel(new LocalSellableItemIndex(), cart, Session, onOpenPayment: null);
        var sale = cart.AddItem(CreateItem(lookupCode: "LK-SALE", price: 30m));
        var returnLine = cart.AddReturnLine(CreateReturnRequest("RET-1", lookupCode: "LK-RET", unitPrice: 8m));
        viewModel.SelectedCartLine = returnLine;

        foreach (var isWholeOrder in new[] { false, true })
        {
            viewModel.IsWholeOrderOperation = isWholeOrder;
            viewModel.KeypadBuffer = "1";
            viewModel.ApplySelectedLineDiscountAmountCommand.Execute(null);
            viewModel.KeypadBuffer = "10";
            viewModel.ApplySelectedLineDiscountPercentCommand.Execute(null);
            viewModel.ApplyQuickDiscountPercentCommand.Execute("10");

            AssertNoDiscount(returnLine);
            AssertNoDiscount(sale);
            Assert.Equal(0m, viewModel.DiscountAmount);
        }

        // 对照：选中销售行后同一条命令照常生效，且仍不牵连退货行。
        viewModel.IsWholeOrderOperation = false;
        viewModel.SelectedCartLine = sale;
        viewModel.KeypadBuffer = "10";
        viewModel.ApplySelectedLineDiscountPercentCommand.Execute(null);
        Assert.Equal(3m, sale.DiscountAmount);
        AssertNoDiscount(returnLine);
    }

    // ===================================================================================
    // 三、特征化：自动促销（旧引擎 / 新引擎 / 目录重载）
    // ===================================================================================

    [Fact]
    public void Legacy_engine_excludes_return_line_from_bundles_whether_rules_are_set_before_or_after_the_lines()
    {
        // 两个销售单位 + 夹在中间的同商品退货单位：二选一满 15 的规则只能在两个销售单位之间成组，
        // 若退货单位被计入，第一组会变成 [销售, 退货]，折扣会落到退货行上。
        var rule = CreateLegacyRule("PROMO-2FOR15", applyQuantity: 2, fixedPrice: 15m, "SKU-A");

        var rulesFirst = new PosCartService();
        rulesFirst.SetAutomaticPromotionRules([rule]);
        var (saleA1, returnA, saleA2) = AddTwoSalesAroundOneReturn(rulesFirst);
        AssertBundleOnlyOnSaleLines(rulesFirst, saleA1, returnA, saleA2);

        // 目录重载：行已存在，规则后挂上（ShellCatalogService 每次目录刷新都会走这条）。
        var rulesAfter = new PosCartService();
        (saleA1, returnA, saleA2) = AddTwoSalesAroundOneReturn(rulesAfter);
        AssertNoDiscount(returnA);
        Assert.Equal(0m, saleA1.DiscountAmount);
        rulesAfter.SetAutomaticPromotionRules([rule]);
        AssertBundleOnlyOnSaleLines(rulesAfter, saleA1, returnA, saleA2);

        // 目录重载为空规则集：销售行促销被清掉，退货行仍然没有折扣。
        rulesAfter.SetAutomaticPromotionRules([]);
        Assert.Equal(0m, saleA1.DiscountAmount);
        AssertNoDiscount(returnA);
    }

    [Fact]
    public void Legacy_engine_keeps_return_line_clean_after_sale_line_edits_and_return_quantity_merge()
    {
        var cart = new PosCartService();
        cart.SetAutomaticPromotionRules([CreateLegacyRule("PROMO-2FOR15", 2, 15m, "SKU-A")]);
        var (saleA1, returnA, saleA2) = AddTwoSalesAroundOneReturn(cart);

        // 同一来源再退一件（合并到同一退货行）会触发全量刷新，退货行仍无折扣。
        var merged = cart.AddReturnLine(CreateReturnRequest("RET-1", unitPrice: 8m));
        Assert.Same(returnA, merged);
        Assert.Equal(2m, returnA.Quantity);
        AssertNoDiscount(returnA);

        // 销售行改数量 / 改价 / 加减号，每次都会重算促销；退货行始终没有折扣。
        Assert.True(cart.SetLineQuantity(saleA1, 3m));
        AssertNoDiscount(returnA);
        Assert.True(cart.IncreaseLine(saleA2));
        AssertNoDiscount(returnA);
        Assert.True(cart.DecreaseLine(saleA2));
        AssertNoDiscount(returnA);
        Assert.True(cart.SetLineUnitPrice(saleA1, 20m));
        AssertNoDiscount(returnA);
        Assert.True(cart.RemoveLine(saleA2));
        AssertNoDiscount(returnA);
        Assert.Equal(0m, returnA.DiscountAmount);
    }

    [Fact]
    public async Task Unified_engine_never_produces_or_applies_a_discount_for_return_line()
    {
        var now = DateTimeOffset.UtcNow;
        var repository = new FakeLocalPromotionRepository();
        repository.SeedStore(StoreCode, [CreateEngineRule("PROMO-2FOR15", now, "SKU-A", applyQuantity: 2, fixedPrice: 15m)]);
        var cart = new PosCartService();
        var (saleA1, returnA, saleA2) = AddTwoSalesAroundOneReturn(cart);

        var discounts = await new PromotionEvaluationService(repository).EvaluateAsync(cart.Lines, StoreCode, now);

        Assert.DoesNotContain(discounts, discount => ReferenceEquals(discount.Line, returnA));
        Assert.Equal(2.5m, Assert.Single(discounts, discount => ReferenceEquals(discount.Line, saleA1)).DiscountAmount);
        Assert.Equal(2.5m, Assert.Single(discounts, discount => ReferenceEquals(discount.Line, saleA2)).DiscountAmount);

        cart.ApplyPromotionDiscounts(discounts);
        Assert.Equal(2.5m, saleA1.DiscountAmount);
        Assert.Equal(2.5m, saleA2.DiscountAmount);
        AssertNoDiscount(returnA);

        // 第二层保护：即使调用方硬塞一条退货行折扣，购物车落点也会丢弃它，且不动已有的销售行促销。
        cart.ApplyPromotionDiscounts(
        [
            .. discounts,
            new PromotionLineDiscount(returnA, 3m)
        ]);
        AssertNoDiscount(returnA);
        Assert.Equal(2.5m, saleA1.DiscountAmount);
        Assert.Equal(2.5m, saleA2.DiscountAmount);

        // 空结果会清掉全部旧促销折扣，退货行依旧没有折扣。
        cart.ApplyPromotionDiscounts([]);
        Assert.Equal(0m, saleA1.DiscountAmount);
        AssertNoDiscount(returnA);
    }

    [Fact]
    public async Task Unified_engine_evaluates_cart_with_only_return_lines_to_nothing()
    {
        var now = DateTimeOffset.UtcNow;
        var repository = new FakeLocalPromotionRepository();
        repository.SeedStore(StoreCode, [CreateEngineRule("PROMO-1FOR1", now, "SKU-A", applyQuantity: 1, fixedPrice: 1m)]);
        var cart = new PosCartService();
        var returnLine = cart.AddReturnLine(CreateReturnRequest("RET-1", unitPrice: 8m));

        var discounts = await new PromotionEvaluationService(repository).EvaluateAsync(cart.Lines, StoreCode, now);

        Assert.Empty(discounts);
        AssertNoDiscount(returnLine);
    }

    // ===================================================================================
    // 四、特征化：目录折扣基线 / 远程复核 / 加购合并 / 特价商品页
    // ===================================================================================

    [Fact]
    public void Remote_review_by_lookup_code_never_targets_return_line()
    {
        var cart = new PosCartService();
        var returnLine = cart.AddReturnLine(CreateReturnRequest("RET-1", unitPrice: 8m));
        var remote = CreateItem(price: 12m, discountRate: 0.25m);

        // 购物车里只有退货行时，按条码的远程复核找不到可更新的行。
        Assert.False(cart.UpdateLineFromRemote(remote));
        Assert.False(cart.UpdateLineFromRemote(StoreCode, "LK-A1", remote));
        Assert.Equal(8m, returnLine.UnitPrice);
        AssertNoDiscount(returnLine);

        // 同码销售行存在时，只有销售行被更新并带上目录折扣基线。
        var sale = cart.AddItem(CreateItem(price: 10m));
        Assert.True(cart.UpdateLineFromRemote(StoreCode, "LK-A1", remote));
        Assert.Equal(12m, sale.UnitPrice);
        Assert.Equal(2500, sale.CatalogDiscountBasisPoints);
        Assert.Equal(8m, returnLine.UnitPrice);
        Assert.Equal(0, returnLine.CatalogDiscountBasisPoints);
        AssertNoDiscount(returnLine);
    }

    [Fact]
    public async Task Workflow_remote_price_review_updates_new_sale_line_but_leaves_return_line_with_same_lookup_code_alone()
    {
        // 后台远程复核（PosTerminalWorkflowService.UpdateMatchingCartLinesFromRemote）按商品身份匹配行，
        // 退货行与销售行商品/条码/门店完全相同，必须靠 IsReturnLine 过滤才不会被改写。
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var catalogItem = CreateItem(lookupCode: "LK-REMOTE", price: 10m);
        index.ReplaceAll([catalogItem]);
        var returnLine = cart.AddReturnLine(CreateReturnRequest("RET-1", lookupCode: "LK-REMOTE", unitPrice: 8m));
        var remoteItem = catalogItem with { RetailPrice = 12m, DiscountRate = 0.2m, DisplayName = "Remote renamed" };
        var refreshApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new PosTerminalWorkflowService(
            index,
            cart,
            remoteLookupRefreshAsync: (storeCode, lookupCode, _) => Task.FromResult(new RemoteLookupRefreshResult(
                storeCode,
                lookupCode,
                Found: true,
                remoteItem,
                DeletedCount: 0)));
        service.CatalogReloaded += (_, _) => refreshApplied.TrySetResult();

        service.AddSelectedItem(Session, catalogItem, clearScanText: true, closeMatchesPopup: true, operation: "manual-select-match");
        await WaitUntilAsync(() => refreshApplied.Task.IsCompleted);

        var saleLine = Assert.Single(cart.Lines, line => !line.IsReturnLine);
        Assert.Equal(12m, saleLine.UnitPrice);
        Assert.Equal(2.4m, saleLine.DiscountAmount);
        Assert.Equal(8m, returnLine.UnitPrice);
        Assert.Equal("Item SKU-A", returnLine.DisplayName);
        AssertNoDiscount(returnLine);
    }

    [Fact]
    public void Adding_the_same_product_never_merges_into_or_updates_return_line()
    {
        var cart = new PosCartService();
        var returnLine = cart.AddReturnLine(CreateReturnRequest("RET-1", unitPrice: 8m));

        // 手动加购 / 扫码连续加购都只能新建销售行，目录折扣基线只落在新销售行上。
        var sale = cart.AddItem(CreateItem(price: 10m, discountRate: 0.2m));
        var consecutiveSale = cart.AddConsecutiveItem(CreateItem(price: 10m, discountRate: 0.2m));
        Assert.Same(sale, consecutiveSale);
        Assert.Equal(2m, sale.Quantity);
        Assert.Equal(4m, sale.DiscountAmount);

        Assert.Equal(1m, returnLine.Quantity);
        Assert.Equal(8m, returnLine.UnitPrice);
        AssertNoDiscount(returnLine);

        // 退货行是最后一行时，连续扫码不能把销售商品并进退货行。
        var cart2 = new PosCartService();
        var lastReturn = cart2.AddReturnLine(CreateReturnRequest("RET-2", unitPrice: 8m));
        var newSale = cart2.AddConsecutiveItem(CreateItem(price: 10m, discountRate: 0.2m));
        Assert.NotSame(lastReturn, newSale);
        Assert.Equal(1m, lastReturn.Quantity);
        AssertNoDiscount(lastReturn);
        Assert.Equal(2m, newSale.DiscountAmount);
    }

    [Fact]
    public void Special_products_add_and_undo_only_touch_the_added_sale_line()
    {
        var cart = new PosCartService();
        var returnLine = cart.AddReturnLine(CreateReturnRequest("RET-1", unitPrice: 8m));
        // AddToCart / UndoAddToCart 只用到购物车，目录仓储与特价服务在这两个入口不会被访问。
        var workflow = new SpecialProductsWorkflowService(new LocalSellableItemIndex(), cart, null!, null!);
        var special = CreateItem(price: 10m, discountRate: 0.2m, isSpecialProduct: true);

        var firstAdd = workflow.AddToCart(special);
        var secondAdd = workflow.AddToCart(special);
        Assert.Same(firstAdd.Line, secondAdd.Line);
        Assert.False(firstAdd.Line.IsReturnLine);
        Assert.Equal(2m, firstAdd.Line.Quantity);
        Assert.Equal(4m, firstAdd.Line.DiscountAmount);
        AssertNoDiscount(returnLine);

        // 撤销：先减回数量，再整行删除；退货行全程不变。
        var partialUndo = workflow.UndoAddToCart(firstAdd.Line, addedQuantity: 1m);
        Assert.True(partialUndo.Changed);
        Assert.False(partialUndo.RemovedLine);
        Assert.Equal(2m, firstAdd.Line.DiscountAmount);
        AssertNoDiscount(returnLine);

        var fullUndo = workflow.UndoAddToCart(firstAdd.Line, addedQuantity: 1m);
        Assert.True(fullUndo.RemovedLine);
        Assert.Same(returnLine, Assert.Single(cart.Lines));
        Assert.Equal(8m, returnLine.UnitPrice);
        AssertNoDiscount(returnLine);
    }

    // ===================================================================================
    // 五、特征化：改数量 / 改价 / 加减号
    // ===================================================================================

    [Fact]
    public void Quantity_and_price_edits_are_rejected_for_return_line_and_do_not_touch_its_discount()
    {
        var cart = new PosCartService();
        cart.SetAutomaticPromotionRules([CreateLegacyRule("PROMO-2FOR15", 2, 15m, "SKU-A")]);
        var (_, returnLine, _) = AddTwoSalesAroundOneReturn(cart);
        var revisionBefore = cart.Revision;

        Assert.False(cart.IncreaseLine(returnLine));
        Assert.False(cart.DecreaseLine(returnLine));
        Assert.False(cart.SetLineQuantity(returnLine, 3m));
        Assert.False(cart.SetLineUnitPrice(returnLine, 9m));

        Assert.Equal(revisionBefore, cart.Revision);
        Assert.Equal(1m, returnLine.Quantity);
        Assert.Equal(8m, returnLine.UnitPrice);
        Assert.False(returnLine.IsManualPrice);
        AssertNoDiscount(returnLine);
    }

    // ===================================================================================
    // 六、特征化：取单 / 快照还原 / 支付恢复
    // ===================================================================================

    [Fact]
    public void RestoreSnapshot_drops_every_discount_state_carried_by_return_line_snapshot()
    {
        // 快照里的退货行即使带着各种来源的折扣（旧版本 bug、手改库、旧快照缺来源字段），
        // 还原时也只按退货请求重建，折扣一律忽略。
        var cart = new PosCartService();
        var snapshot = CreateReturnSnapshotCarryingDiscounts();

        cart.RestoreSnapshot(snapshot);

        Assert.Equal(5, cart.Lines.Count);
        Assert.All(cart.Lines, line =>
        {
            Assert.True(line.IsReturnLine);
            AssertNoDiscount(line);
        });
        Assert.Equal(0m, cart.DiscountAmount);
        Assert.Equal(-40m, cart.ActualAmount);
    }

    [Fact]
    public void RestoreSnapshot_with_rules_does_not_apply_promotion_to_restored_return_lines_even_after_later_refresh()
    {
        var rule = CreateLegacyRule("PROMO-1FOR1", applyQuantity: 1, fixedPrice: 1m, "SKU-A");
        var cart = new PosCartService();
        cart.SetAutomaticPromotionRules([rule]);
        cart.RestoreSnapshot(CreateReturnSnapshotCarryingDiscounts());

        // 还原本身不跑促销；之后的目录重载 / 新加销售行触发全量刷新，退货行仍然没有折扣。
        cart.SetAutomaticPromotionRules([rule]);
        var sale = cart.AddItem(CreateItem(lookupCode: "LK-SALE", price: 10m));

        Assert.Equal(9m, sale.DiscountAmount);
        Assert.All(cart.Lines.Where(line => line.IsReturnLine), AssertNoDiscount);
    }

    [Fact]
    public void Recovery_publication_ignores_discount_state_carried_by_return_line_snapshot()
    {
        // 支付恢复发布：先在隔离购物车里 RestoreSnapshot 物化再发布，路径与普通还原一致。
        var cart = new PosCartService();

        var published = cart.TryPublishRecoverySnapshot(
            Guid.NewGuid(),
            cart.Revision,
            CreateReturnSnapshotCarryingDiscounts());

        Assert.True(published.Succeeded);
        Assert.Equal(5, cart.Lines.Count);
        Assert.All(cart.Lines, AssertNoDiscount);
        Assert.Equal(0m, cart.DiscountAmount);
    }

    [Fact]
    public void Shared_sale_restore_rejects_snapshot_with_discounted_return_line_and_leaves_cart_empty()
    {
        var cart = new PosCartService();

        Assert.Throws<InvalidOperationException>(() =>
            cart.RestoreSharedSaleSnapshot(CreateReturnSnapshotCarryingDiscounts()));

        Assert.True(cart.IsEmpty);
    }

    [Fact]
    public void Return_line_added_after_shared_sale_restore_gets_no_discount()
    {
        // 共享取单后购物车里是带冻结折扣的销售行；之后再加退货行（退货页可以随时往购物车加），
        // 折扣刷新不能把销售行的折扣或促销带到退货行上。
        var cart = new PosCartService();
        var snapshot = new PosCartSnapshot(
        [
            new PosCartLineSnapshot(
                StoreCode,
                "SKU-A",
                null,
                "Item SKU-A",
                "LK-SHARED",
                "SKU-A",
                null,
                1m,
                10m,
                1m,
                10m,
                PriceSourceKind.StoreRetailPrice,
                PriceSourceKind.StoreRetailPrice.ToString(),
                DiscountSource: PosCartLineDiscountSource.Manual)
        ]);
        cart.RestoreSharedSaleSnapshot(snapshot with { SharedHeldOrderClaimId = Guid.NewGuid() });

        var returnLine = cart.AddReturnLine(CreateReturnRequest("RET-1", unitPrice: 8m));
        AssertNoDiscount(returnLine);

        Assert.False(cart.SetLineDiscountPercent(returnLine, 10m));
        Assert.False(cart.SetOrderDiscountPercent(10m));
        AssertNoDiscount(returnLine);
    }

    [Fact]
    public async Task Local_suspended_order_round_trip_keeps_return_line_discount_free_and_ignores_tampered_stored_discount()
    {
        var rule = CreateLegacyRule("PROMO-2FOR15", 2, 15m, "SKU-A");
        var cart = new PosCartService();
        cart.SetAutomaticPromotionRules([rule]);
        var (saleA1, returnA, saleA2) = AddTwoSalesAroundOneReturn(cart);
        AssertBundleOnlyOnSaleLines(cart, saleA1, returnA, saleA2);
        var repository = new InMemorySuspendedOrderRepository();
        var service = new SuspendedOrderService(repository, cart);

        // 挂单：快照里的退货行没有任何折扣字段。
        var suspended = await service.SuspendCurrentOrderAsync(Session);
        var storedReturn = Assert.Single(suspended.Lines, line => line.Kind == CartLineKind.Return);
        Assert.Equal(0m, storedReturn.DiscountAmount);
        Assert.Null(storedReturn.DiscountPercent);
        Assert.Equal(PosCartLineDiscountSource.None, storedReturn.DiscountSource);
        Assert.Equal(0, storedReturn.CatalogDiscountBasisPoints);

        // 取单：促销已冻结在销售行上，退货行没有折扣；取单之后的目录重载 / 编辑也不会写到退货行。
        await service.RecallOrderAsync(suspended.SuspendedOrderGuid);
        var recalledReturn = Assert.Single(cart.Lines, line => line.IsReturnLine);
        AssertNoDiscount(recalledReturn);
        Assert.Equal(5m, cart.DiscountAmount);
        cart.SetAutomaticPromotionRules([rule]);
        AssertNoDiscount(recalledReturn);
        var recalledSale = cart.Lines.First(line => !line.IsReturnLine);
        Assert.True(cart.SetLineQuantity(recalledSale, 2m));
        AssertNoDiscount(recalledReturn);

        // 库里的退货行被人为写入折扣（旧 bug / 手改库）：取单只按退货请求重建，折扣被丢弃。
        cart.Clear();
        var tamperedOrderGuid = Guid.NewGuid();
        var tamperedLines = suspended.Lines
            .Select(line => line with
            {
                SuspendedOrderLineGuid = Guid.NewGuid(),
                SuspendedOrderGuid = tamperedOrderGuid
            })
            .Select(line => line.Kind == CartLineKind.Return
                ? line with
                {
                    DiscountAmount = 2m,
                    DiscountPercent = 25m,
                    DiscountSource = PosCartLineDiscountSource.Manual,
                    CatalogDiscountBasisPoints = 2500
                }
                : line)
            .ToArray();
        await repository.SaveAsync(suspended with
        {
            SuspendedOrderGuid = tamperedOrderGuid,
            Status = SuspendedOrderStatus.Pending,
            Lines = tamperedLines
        });

        await service.RecallOrderAsync(tamperedOrderGuid);

        AssertNoDiscount(Assert.Single(cart.Lines, line => line.IsReturnLine));
    }

    // ===================================================================================
    // 辅助
    // ===================================================================================

    private static PosSessionState Session => new("HB POS", StoreCode, "Main Store", "POS-01", "C001", "Alice", true, 0);

    /// <summary>「完全没有折扣」：金额、百分比、来源、目录基线、各类标记全部为空。退货行必须始终满足；个别用例也拿它断言销售行未被牵连。</summary>
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
        // 退货行的实收 = 毛额（均为负数），没有折扣抵扣。
        Assert.Equal(line.GrossAmount, line.ActualAmount);
    }

    /// <summary>
    /// 购物车顺序：销售 A1(10) -> 退货 A(8，同商品) -> 销售 A2(10)；
    /// 三行商品编码相同、查询码不同，所以不会被合并。
    /// </summary>
    private static (CartLine SaleA1, CartLine ReturnA, CartLine SaleA2) AddTwoSalesAroundOneReturn(PosCartService cart)
    {
        var saleA1 = cart.AddItem(CreateItem(lookupCode: "LK-A1", price: 10m));
        var returnA = cart.AddReturnLine(CreateReturnRequest("RET-1", unitPrice: 8m));
        var saleA2 = cart.AddItem(CreateItem(lookupCode: "LK-A2", price: 10m));
        return (saleA1, returnA, saleA2);
    }

    private static void AssertBundleOnlyOnSaleLines(PosCartService cart, CartLine saleA1, CartLine returnA, CartLine saleA2)
    {
        // 两个销售单位凑成一组：20 -> 15，折扣 5 对半落到两个销售行。
        Assert.Equal(2.5m, saleA1.DiscountAmount);
        Assert.Equal(2.5m, saleA2.DiscountAmount);
        Assert.True(saleA1.IsAutomaticPromotionDiscount);
        Assert.True(saleA2.IsAutomaticPromotionDiscount);
        AssertNoDiscount(returnA);
        Assert.Equal(5m, cart.DiscountAmount);
        Assert.Equal(7m, cart.ActualAmount);
    }

    /// <summary>五条退货行快照，每条带一种不同的折扣状态；数量 1、单价 8，合计 -40。</summary>
    private static PosCartSnapshot CreateReturnSnapshotCarryingDiscounts()
    {
        PosCartLineSnapshot ReturnLine(
            string key,
            decimal discountAmount,
            decimal? discountPercent,
            PosCartLineDiscountSource source,
            int catalogBasisPoints) =>
            new(
                StoreCode,
                "SKU-A",
                null,
                "Item SKU-A",
                $"LK-{key}",
                "SKU-A",
                null,
                1m,
                8m,
                discountAmount,
                discountPercent,
                PriceSourceKind.StoreRetailPrice,
                PriceSourceKind.StoreRetailPrice.ToString(),
                CartLineKind.Return,
                key,
                Guid.NewGuid(),
                Guid.NewGuid(),
                null,
                source,
                false,
                catalogBasisPoints);

        return new PosCartSnapshot(
        [
            ReturnLine("MANUAL-AMOUNT", 3m, null, PosCartLineDiscountSource.Manual, 0),
            ReturnLine("MANUAL-PERCENT", 2m, 25m, PosCartLineDiscountSource.Manual, 0),
            ReturnLine("PROMOTION", 2m, null, PosCartLineDiscountSource.Promotion, 0),
            ReturnLine("CATALOG", 1.6m, 20m, PosCartLineDiscountSource.Catalog, 2000),
            // 旧快照没有来源字段：销售行会把它当手工折扣还原，退货行不能。
            ReturnLine("NO-SOURCE", 2m, null, PosCartLineDiscountSource.None, 0)
        ]);
    }

    private static ReturnCartLineRequest CreateReturnRequest(
        string sourceKey,
        string productCode = "SKU-A",
        string lookupCode = "LK-A1",
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
        string productCode = "SKU-A",
        string lookupCode = "LK-A1",
        decimal price = 10m,
        string? displayName = null,
        PriceSourceKind priceSource = PriceSourceKind.StoreRetailPrice,
        decimal? discountRate = null,
        bool isSpecialProduct = false)
    {
        return new SellableItemDto(
            StoreCode: StoreCode,
            ProductCode: productCode,
            ReferenceCode: null,
            DisplayName: displayName ?? $"Item {productCode}",
            LookupCode: lookupCode,
            ItemNumber: productCode,
            Barcode: lookupCode,
            RetailPrice: price,
            PriceSource: priceSource,
            PriceSourceLabel: priceSource.ToString(),
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow,
            ProductImage: null,
            DiscountRate: discountRate,
            IsSpecialProduct: isSpecialProduct);
    }

    private static CatalogPromotionRuleDto CreateLegacyRule(
        string promotionId,
        int applyQuantity,
        decimal fixedPrice,
        params string[] productCodes)
    {
        return new CatalogPromotionRuleDto(
            promotionId,
            "Quantity discount",
            IsExclusive: true,
            Priority: 0,
            applyQuantity,
            fixedPrice,
            MaxApplicationsPerOrder: null,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1),
            DateTimeOffset.UtcNow,
            productCodes.Select(productCode => new CatalogPromotionProductDto(productCode, 1)).ToArray());
    }

    private static PromotionRuleDto CreateEngineRule(
        string id,
        DateTimeOffset asOf,
        string productCode,
        int applyQuantity,
        decimal fixedPrice)
    {
        return new PromotionRuleDto(
            id,
            $"Rule {id}",
            asOf.AddDays(-1).UtcDateTime,
            asOf.AddDays(1).UtcDateTime,
            true,
            10,
            applyQuantity,
            fixedPrice,
            null,
            [new PromotionRuleProductDto(productCode, 1)]);
    }

    private sealed class FakeLocalPromotionRepository : ILocalPromotionRepository
    {
        private readonly Dictionary<string, IReadOnlyList<PromotionRuleDto>> _rulesByStore = new(StringComparer.OrdinalIgnoreCase);

        public Task ReplaceStoreRulesAsync(
            string storeCode,
            PromotionRulesResponse response,
            CancellationToken cancellationToken = default)
        {
            _rulesByStore[storeCode] = response.Rules;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PromotionRuleDto>> GetActiveRulesAsync(
            string storeCode,
            DateTimeOffset asOf,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                _rulesByStore.TryGetValue(storeCode, out var rules)
                    ? rules
                    : (IReadOnlyList<PromotionRuleDto>)[]);
        }

        public void SeedStore(string storeCode, IReadOnlyList<PromotionRuleDto> rules)
        {
            _rulesByStore[storeCode] = rules;
        }
    }

    /// <summary>只在内存里保存挂单：本测试关心的是购物车往返，不需要真实 SQLite。</summary>
    private sealed class InMemorySuspendedOrderRepository : ISuspendedOrderRepository
    {
        private readonly Dictionary<Guid, SuspendedOrder> _orders = [];

        public Task SaveAsync(SuspendedOrder order, CancellationToken cancellationToken = default)
        {
            _orders[order.SuspendedOrderGuid] = order;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SuspendedOrderSummary>> GetPendingAsync(
            string storeCode,
            string? deviceCode = null,
            string? keyword = null,
            int take = 100,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<SuspendedOrder?> GetAsync(Guid suspendedOrderGuid, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_orders.GetValueOrDefault(suspendedOrderGuid));
        }

        public Task MarkStatusAsync(
            Guid suspendedOrderGuid,
            SuspendedOrderStatus status,
            CancellationToken cancellationToken = default)
        {
            _orders[suspendedOrderGuid] = _orders[suspendedOrderGuid] with { Status = status };
            return Task.CompletedTask;
        }
    }
}
