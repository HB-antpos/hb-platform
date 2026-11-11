using System.Net;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

/// <summary>
/// 特殊商品页「分店级分类」的视图模型行为：分类栏、按分类显示、筛选 / 已在购物车与分类的组合语义、
/// 编辑模式「移到分类」、分类内顺序走服务器（含冲突）、「全部 / 未分类」仍走本机顺序、没有分类时与旧版完全一致。
/// 用真实的 SpecialProductsWorkflowService，只把仓储、业务服务（模拟服务端）和设置换成内存假实现。
/// </summary>
[Collection(ShutdownTimingTestCollection.Name)]
public sealed class SpecialProductCategoryViewModelTests
{
    private const string Store = "S001";
    private const string Balloons = "C1";
    private const string Cards = "C2";

    // ================= 没有分类 =================

    [Fact]
    public async Task Without_any_category_the_page_behaves_exactly_as_before()
    {
        var harness = Create(DefaultItems(), snapshot: null);
        var viewModel = harness.ViewModel;

        await viewModel.EnsureLoadedAsync();

        Assert.False(viewModel.HasCategories);
        Assert.False(viewModel.IsCategoryScopeActive);
        Assert.Empty(viewModel.CategoryEntries);
        Assert.Equal(["P1", "P2", "P3", "P4", "P5", "P6"], Codes(viewModel));
        // 筛选条第一个标签保持原文案。
        Assert.Equal("All 6", viewModel.AllChipText);
        Assert.Equal("No special products match.", viewModel.FilterEmptyText);

        // 编辑模式：上移仍只改本机顺序，不碰服务器，也不出现「移到分类」。
        viewModel.ToggleEditModeCommand.Execute(null);
        var second = viewModel.SpecialItems[1];
        viewModel.SpecialItemCardCommand.Execute(second);
        Assert.False(viewModel.OpenCategoryPickerCommand.CanExecute(second));
        await viewModel.MoveUpCommand.ExecuteAsync(second);

        Assert.Equal(["P2", "P1", "P3", "P4", "P5", "P6"], Assert.Single(harness.Repository.SavedOrders));
        Assert.Empty(harness.Service.OrderCalls);
        Assert.Empty(harness.Service.AssignCalls);
        // 选中项文案也是旧格式（不带分类名）。
        Assert.Equal("Selected: Bravo · No. 1", viewModel.SelectedPositionText);
    }

    // ================= 分类栏 =================

    [Fact]
    public async Task Category_bar_lists_all_categories_with_counts_and_hides_empty_uncategorized()
    {
        var harness = Create(DefaultItems(), DefaultSnapshot());
        var viewModel = harness.ViewModel;

        await viewModel.EnsureLoadedAsync();

        Assert.True(viewModel.HasCategories);
        Assert.Equal(
            [(null, "All", 6), (Balloons, "Balloons", 2), (Cards, "Cards", 1), ("uncategorized", "Uncategorized", 3)],
            viewModel.CategoryEntries.Select(entry => (entry.Id, entry.Name, entry.Count)));
        // 默认选中「全部」，网格就是现有完整列表（本机顺序）。
        Assert.True(viewModel.CategoryEntries[0].IsSelected);
        Assert.Null(viewModel.CurrentCategoryId);
        Assert.Equal(["P1", "P2", "P3", "P4", "P5", "P6"], Codes(viewModel));
        Assert.Equal("#2563EB", viewModel.CategoryEntries[1].ColorHex);
        Assert.False(viewModel.CategoryEntries[2].HasColor);

        // 所有商品都归了类 → 「未分类」这一行消失。
        var allCategorized = Create(
            DefaultItems(),
            new SpecialProductCategorySnapshot(
                Store,
                [new SpecialProductCategoryDto(Balloons, "Balloons", null, 0)],
                DefaultItems().Select((item, index) => new SpecialProductCategoryAssignmentDto(item.ProductCode, Balloons, index)).ToArray()));
        await allCategorized.ViewModel.EnsureLoadedAsync();
        Assert.DoesNotContain(allCategorized.ViewModel.CategoryEntries, entry => entry.Id == "uncategorized");
    }

    [Fact]
    public async Task Invalid_category_color_is_ignored()
    {
        var snapshot = new SpecialProductCategorySnapshot(
            Store,
            [new SpecialProductCategoryDto(Balloons, "Balloons", "red; drop table", 0)],
            []);
        var harness = Create(DefaultItems(), snapshot);

        await harness.ViewModel.EnsureLoadedAsync();

        Assert.Null(harness.ViewModel.CategoryEntries[1].ColorHex);
    }

    [Fact]
    public async Task Selecting_a_category_shows_its_products_in_server_order_and_remembers_it()
    {
        var harness = Create(DefaultItems(), DefaultSnapshot());
        var viewModel = harness.ViewModel;
        await viewModel.EnsureLoadedAsync();
        var scrollRequests = 0;
        viewModel.ScrollToTopRequested += (_, _) => scrollRequests++;

        viewModel.SelectCategoryCommand.Execute(Entry(viewModel, Balloons));

        // 服务器顺序 P3, P1（与本机顺序 P1, P3 不同）。
        Assert.Equal(["P3", "P1"], Codes(viewModel));
        Assert.Equal(Balloons, viewModel.CurrentCategoryId);
        Assert.True(viewModel.IsRealCategoryScope);
        Assert.Equal("Balloons 2", viewModel.AllChipText);
        Assert.Equal([1, 2], viewModel.DisplayedCards.Select(card => card.Position));
        Assert.Equal(1, scrollRequests);
        Assert.Equal(Balloons, harness.Settings.Values[SettingKey]);
        Assert.Single(viewModel.CategoryEntries, entry => entry.IsSelected);
        Assert.True(Entry(viewModel, Balloons).IsSelected);

        // 切回「全部」：恢复完整本机顺序，并清掉记忆。
        viewModel.SelectCategoryCommand.Execute(Entry(viewModel, null));
        Assert.Equal(["P1", "P2", "P3", "P4", "P5", "P6"], Codes(viewModel));
        Assert.Equal("All 6", viewModel.AllChipText);
        Assert.DoesNotContain(SettingKey, harness.Settings.Values.Keys);
    }

    [Fact]
    public async Task Uncategorized_view_shows_products_without_category_in_local_order()
    {
        var harness = Create(DefaultItems(), DefaultSnapshot());
        var viewModel = harness.ViewModel;
        await viewModel.EnsureLoadedAsync();

        viewModel.SelectCategoryCommand.Execute(Entry(viewModel, "uncategorized"));

        Assert.Equal(["P4", "P5", "P6"], Codes(viewModel));
        Assert.Equal("Uncategorized 3", viewModel.AllChipText);
        Assert.True(viewModel.IsCategoryScopeActive);
        Assert.False(viewModel.IsRealCategoryScope);
    }

    [Fact]
    public async Task Empty_category_shows_its_own_empty_text()
    {
        var snapshot = new SpecialProductCategorySnapshot(
            Store,
            [new SpecialProductCategoryDto("C3", "Empty one", null, 0)],
            []);
        var harness = Create(DefaultItems(), snapshot);
        var viewModel = harness.ViewModel;
        await viewModel.EnsureLoadedAsync();

        viewModel.SelectCategoryCommand.Execute(Entry(viewModel, "C3"));

        Assert.Empty(viewModel.DisplayedCards);
        Assert.True(viewModel.IsFilterEmpty);
        Assert.Equal("No special products in this category.", viewModel.FilterEmptyText);
    }

    // ================= 筛选 / 已在购物车 与分类的组合语义 =================

    [Fact]
    public async Task Filter_text_always_searches_all_special_products_and_clearing_returns_to_the_category()
    {
        var harness = Create(DefaultItems(), DefaultSnapshot());
        var viewModel = harness.ViewModel;
        await viewModel.EnsureLoadedAsync();
        viewModel.SelectCategoryCommand.Execute(Entry(viewModel, Balloons));

        // Bravo (P2) 在 Cards 分类里：筛选框不受当前分类（Balloons）限制。
        viewModel.FilterText = "Bravo";
        Assert.Equal(["P2"], Codes(viewModel));

        // 货号、条码同样在全部特殊商品里找；未分类的 Echo (P5) 也能搜到。
        viewModel.FilterText = "930005";
        Assert.Equal(["P5"], Codes(viewModel));

        viewModel.FilterText = "nothing matches";
        Assert.Empty(viewModel.DisplayedCards);
        Assert.Equal("No special products match.", viewModel.FilterEmptyText);

        // 清空后回到原分类（含服务器顺序），当前分类没有被筛选改掉。
        viewModel.FilterText = string.Empty;
        Assert.Equal(["P3", "P1"], Codes(viewModel));
        Assert.Equal(Balloons, viewModel.CurrentCategoryId);
    }

    [Fact]
    public async Task In_cart_filter_is_cross_category_and_combines_with_filter_text_by_intersection()
    {
        var harness = Create(DefaultItems(), DefaultSnapshot());
        var viewModel = harness.ViewModel;
        await viewModel.EnsureLoadedAsync();
        viewModel.SelectCategoryCommand.Execute(Entry(viewModel, Balloons));
        // P2 在 Cards 分类、P4 未分类，都不在当前分类 Balloons 里。
        harness.Cart.AddItem(harness.Items.Single(item => item.ProductCode == "P2"));
        harness.Cart.AddItem(harness.Items.Single(item => item.ProductCode == "P4"));

        viewModel.ShowInCartCommand.Execute(null);
        // 「已在购物车」标签跨分类：显示全部特殊商品里在购物车的，而不是当前分类里的。
        Assert.Equal(["P2", "P4"], Codes(viewModel));
        Assert.Equal(2, viewModel.InCartCount);
        Assert.Equal("Balloons 2", viewModel.AllChipText);

        // 与筛选框同时生效取交集。
        viewModel.FilterText = "Delta";
        Assert.Equal(["P4"], Codes(viewModel));
        viewModel.FilterText = "Alpha";
        Assert.Empty(viewModel.DisplayedCards);
        viewModel.FilterText = string.Empty;

        // 关掉「已在购物车」回到当前分类。
        viewModel.ShowAllCommand.Execute(null);
        Assert.Equal(["P3", "P1"], Codes(viewModel));
    }

    [Fact]
    public async Task Tapping_a_category_clears_filter_text_and_in_cart_filter()
    {
        var harness = Create(DefaultItems(), DefaultSnapshot());
        var viewModel = harness.ViewModel;
        await viewModel.EnsureLoadedAsync();
        harness.Cart.AddItem(harness.Items[1]);
        viewModel.FilterText = "Bravo";
        viewModel.ShowInCartCommand.Execute(null);

        viewModel.SelectCategoryCommand.Execute(Entry(viewModel, Cards));

        Assert.Equal(string.Empty, viewModel.FilterText);
        Assert.False(viewModel.IsCartFilterActive);
        Assert.Equal(["P2"], Codes(viewModel));
        Assert.Equal(Cards, viewModel.CurrentCategoryId);
    }

    // ================= 记忆当前分类 =================

    [Fact]
    public async Task Saved_category_is_restored_on_entry_and_a_deleted_one_falls_back_to_all()
    {
        var restored = Create(DefaultItems(), DefaultSnapshot(), savedCategory: Cards);
        await restored.ViewModel.EnsureLoadedAsync();
        Assert.Equal(Cards, restored.ViewModel.CurrentCategoryId);
        Assert.Equal(["P2"], Codes(restored.ViewModel));
        Assert.Equal("Cards 1", restored.ViewModel.AllChipText);

        var deleted = Create(DefaultItems(), DefaultSnapshot(), savedCategory: "GONE");
        await deleted.ViewModel.EnsureLoadedAsync();
        Assert.Null(deleted.ViewModel.CurrentCategoryId);
        Assert.Equal(6, deleted.ViewModel.DisplayedCards.Count);
        // 回到「全部」的结果也记到本机：下次不再去找一个已删除的分类。
        Assert.DoesNotContain(SettingKey, deleted.Settings.Values.Keys);
    }

    [Fact]
    public async Task Saved_uncategorized_falls_back_to_all_when_nothing_is_uncategorized_any_more()
    {
        var snapshot = new SpecialProductCategorySnapshot(
            Store,
            [new SpecialProductCategoryDto(Balloons, "Balloons", null, 0)],
            DefaultItems().Select((item, index) => new SpecialProductCategoryAssignmentDto(item.ProductCode, Balloons, index)).ToArray());
        var harness = Create(DefaultItems(), snapshot, savedCategory: "uncategorized");

        await harness.ViewModel.EnsureLoadedAsync();

        Assert.Null(harness.ViewModel.CurrentCategoryId);
        Assert.DoesNotContain(SettingKey, harness.Settings.Values.Keys);
    }

    [Fact]
    public async Task Server_removing_every_category_turns_the_feature_off_and_resets_the_selection()
    {
        var harness = Create(DefaultItems(), DefaultSnapshot(), savedCategory: Balloons);
        await harness.ViewModel.EnsureLoadedAsync();
        Assert.Equal(Balloons, harness.ViewModel.CurrentCategoryId);

        // 手机上把分类全删了：下次联网刷新（这里用「刷新」按钮之外的下载路径触发）后整个功能关闭，列表回到完整。
        harness.Service.Server = SpecialProductCategorySnapshot.Empty(Store);
        await harness.ViewModel.DownloadCommand.ExecuteAsync(null);

        Assert.False(harness.ViewModel.HasCategories);
        Assert.Null(harness.ViewModel.CurrentCategoryId);
        Assert.Empty(harness.ViewModel.CategoryEntries);
        Assert.Equal(6, harness.ViewModel.DisplayedCards.Count);
        Assert.Equal("All 6", harness.ViewModel.AllChipText);
    }

    [Fact]
    public async Task Changing_store_resets_the_category_state()
    {
        var harness = Create(DefaultItems(), DefaultSnapshot(), savedCategory: Balloons);
        await harness.ViewModel.EnsureLoadedAsync();

        harness.ViewModel.Session = harness.ViewModel.Session with { StoreCode = "S002" };

        Assert.False(harness.ViewModel.HasCategories);
        Assert.Null(harness.ViewModel.CurrentCategoryId);
    }

    // ================= 拉取分类快照 =================

    [Fact]
    public async Task Entering_the_page_online_pulls_the_server_snapshot_in_the_background_and_saves_it_locally()
    {
        var harness = Create(DefaultItems(), localSnapshot: null, serverSnapshot: DefaultSnapshot());

        await harness.ViewModel.EnsureLoadedAsync();
        await WaitUntilAsync(() => harness.ViewModel.HasCategories);

        Assert.Equal(1, harness.Service.RefreshCalls);
        Assert.Equal(4, harness.ViewModel.CategoryEntries.Count);
        Assert.True(harness.Repository.LocalSnapshot.HasCategories);
    }

    [Fact]
    public async Task Pull_failure_keeps_the_list_and_the_local_categories_untouched()
    {
        var harness = Create(DefaultItems(), DefaultSnapshot());
        harness.Service.RefreshFailure = new CatalogApiException("server down", HttpStatusCode.ServiceUnavailable);

        await harness.ViewModel.EnsureLoadedAsync();

        // 本机快照仍然生效，列表照常显示，也不出现「加载失败」提示。
        Assert.Equal(1, harness.Service.RefreshCalls);
        Assert.True(harness.ViewModel.HasCategories);
        Assert.Equal(6, harness.ViewModel.DisplayedCards.Count);
        Assert.DoesNotContain("failed", harness.ViewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unsupported_server_is_treated_like_a_failed_pull()
    {
        var harness = Create(DefaultItems(), snapshot: null);
        harness.Service.RefreshFailure = new NotSupportedException("old server");

        await harness.ViewModel.EnsureLoadedAsync();

        Assert.False(harness.ViewModel.HasCategories);
        Assert.Equal(6, harness.ViewModel.DisplayedCards.Count);
    }

    [Fact]
    public async Task Offline_uses_the_local_snapshot_and_does_not_call_the_server()
    {
        var harness = Create(DefaultItems(), DefaultSnapshot(), online: false);

        await harness.ViewModel.EnsureLoadedAsync();

        Assert.Equal(0, harness.Service.RefreshCalls);
        Assert.True(harness.ViewModel.HasCategories);
        harness.ViewModel.SelectCategoryCommand.Execute(Entry(harness.ViewModel, Balloons));
        Assert.Equal(["P3", "P1"], Codes(harness.ViewModel));
    }

    [Fact]
    public async Task Download_pulls_categories_together_with_the_special_products()
    {
        var harness = Create(DefaultItems(), localSnapshot: null, serverSnapshot: DefaultSnapshot());

        await harness.ViewModel.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.Service.RefreshCalls);
        Assert.Equal(1, harness.Service.DownloadCalls);
        Assert.True(harness.ViewModel.HasCategories);
        Assert.Equal(["P3", "P1"], harness.Repository.LocalSnapshot.GetOrderedProductCodes(Balloons));
    }

    [Fact]
    public async Task Download_still_succeeds_when_the_category_pull_fails()
    {
        var harness = Create(DefaultItems(), snapshot: null);
        harness.Service.RefreshFailure = new CatalogApiException("boom", HttpStatusCode.BadGateway);

        await harness.ViewModel.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.Service.DownloadCalls);
        Assert.Equal(6, harness.ViewModel.DisplayedCards.Count);
        Assert.Contains("download", harness.ViewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Snapshot_for_another_store_is_ignored()
    {
        var harness = Create(
            DefaultItems(),
            localSnapshot: null,
            serverSnapshot: DefaultSnapshot() with { StoreCode = "S999" });

        await harness.ViewModel.EnsureLoadedAsync();

        Assert.False(harness.ViewModel.HasCategories);
    }

    // ================= 编辑模式：移到分类 =================

    [Fact]
    public async Task Move_to_category_picker_marks_the_current_category_and_offers_uncategorized()
    {
        var harness = Create(DefaultItems(), DefaultSnapshot());
        var viewModel = harness.ViewModel;
        await viewModel.EnsureLoadedAsync();
        viewModel.ToggleEditModeCommand.Execute(null);
        var delta = harness.Items.Single(item => item.ProductCode == "P4");
        var alpha = harness.Items.Single(item => item.ProductCode == "P1");
        viewModel.SpecialItemCardCommand.Execute(delta);

        viewModel.OpenCategoryPickerCommand.Execute(delta);

        Assert.True(viewModel.IsCategoryPickerOpen);
        Assert.Equal("Delta", viewModel.CategoryPickerProductName);
        Assert.Equal(
            [("Balloons", false), ("Cards", false), ("Uncategorized", true)],
            viewModel.CategoryChoices.Select(choice => (choice.Name, choice.IsCurrent)));
        Assert.Null(viewModel.CategoryChoices[^1].CategoryId);

        // 已归类的商品：当前分类打勾。
        viewModel.CloseCategoryPickerCommand.Execute(null);
        Assert.False(viewModel.IsCategoryPickerOpen);
        Assert.Empty(viewModel.CategoryChoices);
        viewModel.OpenCategoryPickerCommand.Execute(alpha);
        Assert.Equal(
            [("Balloons", true), ("Cards", false), ("Uncategorized", false)],
            viewModel.CategoryChoices.Select(choice => (choice.Name, choice.IsCurrent)));
    }

    [Fact]
    public async Task Picking_a_category_saves_to_the_server_and_applies_the_returned_snapshot()
    {
        var harness = Create(DefaultItems(), DefaultSnapshot());
        var viewModel = harness.ViewModel;
        await viewModel.EnsureLoadedAsync();
        viewModel.ToggleEditModeCommand.Execute(null);
        var delta = harness.Items.Single(item => item.ProductCode == "P4");
        viewModel.SpecialItemCardCommand.Execute(delta);
        viewModel.OpenCategoryPickerCommand.Execute(delta);

        await viewModel.PickCategoryCommand.ExecuteAsync(viewModel.CategoryChoices[0]);

        Assert.Equal(("P4", Balloons), Assert.Single(harness.Service.AssignCalls));
        Assert.False(viewModel.IsCategoryPickerOpen);
        Assert.False(viewModel.IsBusy);
        // 数量立即对齐服务器快照：Balloons 3、未分类 2；本机快照也被覆盖。
        Assert.Equal([6, 3, 1, 2], viewModel.CategoryEntries.Select(entry => entry.Count));
        Assert.Contains("Delta", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("Balloons", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(["P3", "P1", "P4"], harness.Repository.LocalSnapshot.GetOrderedProductCodes(Balloons));
        // 在「全部」里移动，商品仍在视图里，选中保持。
        Assert.Equal("P4", viewModel.SelectedSpecialItem?.ProductCode);
    }

    [Fact]
    public async Task Moving_a_product_out_of_the_viewed_category_removes_it_from_the_grid_and_clears_selection()
    {
        var harness = Create(DefaultItems(), DefaultSnapshot());
        var viewModel = harness.ViewModel;
        await viewModel.EnsureLoadedAsync();
        viewModel.SelectCategoryCommand.Execute(Entry(viewModel, Balloons));
        viewModel.ToggleEditModeCommand.Execute(null);
        var alpha = harness.Items.Single(item => item.ProductCode == "P1");
        viewModel.SpecialItemCardCommand.Execute(alpha);
        viewModel.OpenCategoryPickerCommand.Execute(alpha);

        // 最后一项「未分类」：把商品移出分类。
        await viewModel.PickCategoryCommand.ExecuteAsync(viewModel.CategoryChoices[^1]);

        Assert.Equal(("P1", (string?)null), Assert.Single(harness.Service.AssignCalls));
        Assert.Equal(["P3"], Codes(viewModel));
        Assert.Null(viewModel.SelectedSpecialItem);
        Assert.Equal("Balloons 1", viewModel.AllChipText);
    }

    [Fact]
    public async Task Moving_the_last_uncategorized_product_while_viewing_uncategorized_falls_back_to_all()
    {
        var items = DefaultItems();
        var snapshot = new SpecialProductCategorySnapshot(
            Store,
            [new SpecialProductCategoryDto(Balloons, "Balloons", null, 0)],
            items.Take(5).Select((item, index) => new SpecialProductCategoryAssignmentDto(item.ProductCode, Balloons, index)).ToArray());
        var harness = Create(items, snapshot);
        var viewModel = harness.ViewModel;
        await viewModel.EnsureLoadedAsync();
        viewModel.SelectCategoryCommand.Execute(Entry(viewModel, "uncategorized"));
        Assert.Equal(["P6"], Codes(viewModel));
        viewModel.ToggleEditModeCommand.Execute(null);
        viewModel.SpecialItemCardCommand.Execute(items[5]);
        viewModel.OpenCategoryPickerCommand.Execute(items[5]);

        await viewModel.PickCategoryCommand.ExecuteAsync(viewModel.CategoryChoices[0]);

        Assert.Null(viewModel.CurrentCategoryId);
        Assert.Equal(6, viewModel.DisplayedCards.Count);
        Assert.DoesNotContain(viewModel.CategoryEntries, entry => entry.Id == "uncategorized");
    }

    [Fact]
    public async Task Picking_the_current_category_changes_nothing()
    {
        var harness = Create(DefaultItems(), DefaultSnapshot());
        var viewModel = harness.ViewModel;
        await viewModel.EnsureLoadedAsync();
        viewModel.ToggleEditModeCommand.Execute(null);
        var alpha = harness.Items.Single(item => item.ProductCode == "P1");
        viewModel.OpenCategoryPickerCommand.Execute(alpha);

        await viewModel.PickCategoryCommand.ExecuteAsync(viewModel.CategoryChoices[0]);

        Assert.Empty(harness.Service.AssignCalls);
        Assert.False(viewModel.IsCategoryPickerOpen);
    }

    [Fact]
    public async Task Move_to_category_requires_online_and_edit_mode()
    {
        var harness = Create(DefaultItems(), DefaultSnapshot(), online: false);
        var viewModel = harness.ViewModel;
        await viewModel.EnsureLoadedAsync();
        var alpha = harness.Items.Single(item => item.ProductCode == "P1");

        // 浏览模式不可用。
        Assert.False(viewModel.OpenCategoryPickerCommand.CanExecute(alpha));
        viewModel.ToggleEditModeCommand.Execute(null);
        // 离线不可用，直接执行也不会打开弹层，并给出联网提示。
        Assert.False(viewModel.OpenCategoryPickerCommand.CanExecute(alpha));
        viewModel.OpenCategoryPickerCommand.Execute(alpha);
        Assert.False(viewModel.IsCategoryPickerOpen);
        Assert.Contains("online", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);

        viewModel.Session = viewModel.Session with { IsOnline = true };
        Assert.True(viewModel.OpenCategoryPickerCommand.CanExecute(alpha));
    }

    [Fact]
    public async Task Going_offline_while_the_picker_is_open_blocks_the_write()
    {
        var harness = Create(DefaultItems(), DefaultSnapshot());
        var viewModel = harness.ViewModel;
        await viewModel.EnsureLoadedAsync();
        viewModel.ToggleEditModeCommand.Execute(null);
        var delta = harness.Items.Single(item => item.ProductCode == "P4");
        viewModel.OpenCategoryPickerCommand.Execute(delta);
        viewModel.Session = viewModel.Session with { IsOnline = false };

        await viewModel.PickCategoryCommand.ExecuteAsync(viewModel.CategoryChoices[0]);

        Assert.Empty(harness.Service.AssignCalls);
        Assert.Contains("online", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Failed_move_keeps_the_page_unchanged_and_reports_the_error()
    {
        var harness = Create(DefaultItems(), DefaultSnapshot());
        var viewModel = harness.ViewModel;
        await viewModel.EnsureLoadedAsync();
        viewModel.ToggleEditModeCommand.Execute(null);
        var delta = harness.Items.Single(item => item.ProductCode == "P4");
        viewModel.OpenCategoryPickerCommand.Execute(delta);
        harness.Service.AssignFailure = new CatalogApiException("network unavailable", HttpStatusCode.ServiceUnavailable);

        await viewModel.PickCategoryCommand.ExecuteAsync(viewModel.CategoryChoices[0]);

        Assert.Contains("network unavailable", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal([6, 2, 1, 3], viewModel.CategoryEntries.Select(entry => entry.Count));
        Assert.False(viewModel.IsBusy);
        // 普通网络错误不触发额外刷新。
        Assert.Equal(1, harness.Service.RefreshCalls);
    }

    [Theory]
    [InlineData("SPECIAL_PRODUCT_CATEGORY_NOT_FOUND")]
    [InlineData("SPECIAL_PRODUCT_NOT_SPECIAL")]
    public async Task Stale_category_errors_refresh_the_snapshot(string errorCode)
    {
        var harness = Create(DefaultItems(), DefaultSnapshot());
        var viewModel = harness.ViewModel;
        await viewModel.EnsureLoadedAsync();
        viewModel.ToggleEditModeCommand.Execute(null);
        var delta = harness.Items.Single(item => item.ProductCode == "P4");
        viewModel.OpenCategoryPickerCommand.Execute(delta);
        harness.Service.AssignFailure = new CatalogApiException("gone", HttpStatusCode.NotFound, errorCode);

        await viewModel.PickCategoryCommand.ExecuteAsync(viewModel.CategoryChoices[0]);

        Assert.Equal(2, harness.Service.RefreshCalls);
    }

    [Fact]
    public async Task Assign_conflict_refreshes_the_snapshot_and_tells_the_cashier_to_retry()
    {
        var harness = Create(DefaultItems(), DefaultSnapshot());
        var viewModel = harness.ViewModel;
        await viewModel.EnsureLoadedAsync();
        viewModel.ToggleEditModeCommand.Execute(null);
        var delta = harness.Items.Single(item => item.ProductCode == "P4");
        viewModel.OpenCategoryPickerCommand.Execute(delta);
        // 另一台收银机刚把 P4 归进了 Cards：服务端对本机的首次归类返回冲突。
        harness.Service.Server = harness.Service.Server with
        {
            Assignments = [.. harness.Service.Server.Assignments, new SpecialProductCategoryAssignmentDto("P4", Cards, 1)],
        };
        harness.Service.AssignFailure = new CatalogApiException(
            "assign conflict",
            HttpStatusCode.Conflict,
            "SPECIAL_PRODUCT_CATEGORY_ASSIGN_CONFLICT");

        await viewModel.PickCategoryCommand.ExecuteAsync(viewModel.CategoryChoices[0]);

        // 入口拉取 1 次 + 冲突后刷新 1 次；页面对齐服务器（Cards 现在有 2 个，未分类 2 个），提示重新操作。
        Assert.Equal(2, harness.Service.RefreshCalls);
        Assert.Equal([6, 2, 2, 2], viewModel.CategoryEntries.Select(entry => entry.Count));
        Assert.Contains("try again", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Could not move", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.False(viewModel.IsBusy);
    }

    // ================= 分类内顺序：走服务器 =================

    [Fact]
    public async Task Move_up_and_down_in_a_real_category_send_the_full_new_order_to_the_server()
    {
        var harness = Create(DefaultItems(), DefaultSnapshot());
        var viewModel = harness.ViewModel;
        await viewModel.EnsureLoadedAsync();
        viewModel.SelectCategoryCommand.Execute(Entry(viewModel, Balloons));
        viewModel.ToggleEditModeCommand.Execute(null);
        var alpha = harness.Items.Single(item => item.ProductCode == "P1");
        viewModel.SpecialItemCardCommand.Execute(alpha);
        Assert.Equal("Selected: Alpha · Balloons · No. 2", viewModel.SelectedPositionText);

        await viewModel.MoveUpCommand.ExecuteAsync(alpha);

        var call = Assert.Single(harness.Service.OrderCalls);
        Assert.Equal(Balloons, call.CategoryId);
        Assert.Equal(["P1", "P3"], call.ProductCodes);
        Assert.Equal(["P1", "P3"], Codes(viewModel));
        // 顺序走服务器，不动本机顺序表。
        Assert.Empty(harness.Repository.SavedOrders);
        Assert.Contains("server", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.IsBusy);
        Assert.Equal("Selected: Alpha · Balloons · No. 1", viewModel.SelectedPositionText);
        // 到头了：上移 / 移到最前置灰，下移可用。
        Assert.False(viewModel.MoveUpCommand.CanExecute(alpha));
        Assert.False(viewModel.MoveToFrontCommand.CanExecute(alpha));
        Assert.True(viewModel.MoveDownCommand.CanExecute(alpha));

        await viewModel.MoveDownCommand.ExecuteAsync(alpha);

        Assert.Equal(["P3", "P1"], harness.Service.OrderCalls[1].ProductCodes);
        Assert.Equal(["P3", "P1"], Codes(viewModel));
    }

    [Fact]
    public async Task Move_to_front_in_a_real_category_puts_the_product_first_on_the_server()
    {
        var items = DefaultItems();
        var snapshot = new SpecialProductCategorySnapshot(
            Store,
            [new SpecialProductCategoryDto(Balloons, "Balloons", null, 0)],
            [
                new SpecialProductCategoryAssignmentDto("P2", Balloons, 0),
                new SpecialProductCategoryAssignmentDto("P4", Balloons, 1),
                new SpecialProductCategoryAssignmentDto("P6", Balloons, 2),
            ]);
        var harness = Create(items, snapshot);
        var viewModel = harness.ViewModel;
        await viewModel.EnsureLoadedAsync();
        viewModel.SelectCategoryCommand.Execute(Entry(viewModel, Balloons));
        viewModel.ToggleEditModeCommand.Execute(null);
        var echo = items[5];
        viewModel.SpecialItemCardCommand.Execute(echo);

        await viewModel.MoveToFrontCommand.ExecuteAsync(echo);

        Assert.Equal(["P6", "P2", "P4"], Assert.Single(harness.Service.OrderCalls).ProductCodes);
        Assert.Equal(["P6", "P2", "P4"], Codes(viewModel));
    }

    [Fact]
    public async Task Server_order_is_preserved_for_products_missing_from_the_local_list()
    {
        // 服务器的分类里有 P9，但本机特殊商品列表里还没有它（还没下载）：
        // 发给服务器的排列必须包含它（否则会被判冲突），上移目标取视图里看得见的邻居。
        var snapshot = new SpecialProductCategorySnapshot(
            Store,
            [new SpecialProductCategoryDto(Balloons, "Balloons", null, 0)],
            [
                new SpecialProductCategoryAssignmentDto("P1", Balloons, 0),
                new SpecialProductCategoryAssignmentDto("P9", Balloons, 1),
                new SpecialProductCategoryAssignmentDto("P3", Balloons, 2),
            ]);
        var harness = Create(DefaultItems(), snapshot);
        harness.Service.Server = snapshot;
        var viewModel = harness.ViewModel;
        await viewModel.EnsureLoadedAsync();
        viewModel.SelectCategoryCommand.Execute(Entry(viewModel, Balloons));
        Assert.Equal(["P1", "P3"], Codes(viewModel));
        viewModel.ToggleEditModeCommand.Execute(null);
        var charlie = harness.Items.Single(item => item.ProductCode == "P3");
        viewModel.SpecialItemCardCommand.Execute(charlie);

        await viewModel.MoveUpCommand.ExecuteAsync(charlie);

        // P3 挪到视图里邻居 P1 的位置之前，隐藏的 P9 仍在排列里。
        Assert.Equal(["P3", "P1", "P9"], Assert.Single(harness.Service.OrderCalls).ProductCodes);
    }

    [Fact]
    public async Task Order_conflict_refreshes_the_snapshot_and_tells_the_cashier_to_retry()
    {
        var harness = Create(DefaultItems(), DefaultSnapshot());
        var viewModel = harness.ViewModel;
        await viewModel.EnsureLoadedAsync();
        viewModel.SelectCategoryCommand.Execute(Entry(viewModel, Balloons));
        viewModel.ToggleEditModeCommand.Execute(null);
        var alpha = harness.Items.Single(item => item.ProductCode == "P1");
        viewModel.SpecialItemCardCommand.Execute(alpha);
        // 另一台收银机刚把 P4 加进了 Balloons：本机排列缺一个商品 → 服务端判冲突。
        harness.Service.Server = harness.Service.Server with
        {
            Assignments = [.. harness.Service.Server.Assignments, new SpecialProductCategoryAssignmentDto("P4", Balloons, 2)],
        };

        await viewModel.MoveUpCommand.ExecuteAsync(alpha);

        Assert.Equal(2, harness.Service.RefreshCalls);
        // 刷新后页面对齐服务器最新状态（Balloons 现在有 3 个），提示重新调整。
        Assert.Equal(["P3", "P1", "P4"], Codes(viewModel));
        Assert.Contains("refreshed", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task Other_order_failures_report_the_error_without_changing_the_page()
    {
        var harness = Create(DefaultItems(), DefaultSnapshot());
        var viewModel = harness.ViewModel;
        await viewModel.EnsureLoadedAsync();
        viewModel.SelectCategoryCommand.Execute(Entry(viewModel, Balloons));
        viewModel.ToggleEditModeCommand.Execute(null);
        var alpha = harness.Items.Single(item => item.ProductCode == "P1");
        viewModel.SpecialItemCardCommand.Execute(alpha);
        harness.Service.OrderFailure = new CatalogApiException("timeout", HttpStatusCode.GatewayTimeout);

        await viewModel.MoveUpCommand.ExecuteAsync(alpha);

        Assert.Contains("timeout", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(["P3", "P1"], Codes(viewModel));
        Assert.Equal(1, harness.Service.RefreshCalls);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task Offline_disables_category_ordering_but_not_the_local_order_in_all()
    {
        var harness = Create(DefaultItems(), DefaultSnapshot(), online: false);
        var viewModel = harness.ViewModel;
        await viewModel.EnsureLoadedAsync();
        viewModel.ToggleEditModeCommand.Execute(null);
        var charlie = harness.Items.Single(item => item.ProductCode == "P3");
        viewModel.SpecialItemCardCommand.Execute(charlie);

        // 「全部」：沿用本机顺序，离线可用。
        Assert.True(viewModel.MoveUpCommand.CanExecute(charlie));

        viewModel.SelectCategoryCommand.Execute(Entry(viewModel, Balloons));
        viewModel.SpecialItemCardCommand.Execute(harness.Items.Single(item => item.ProductCode == "P1"));
        var alpha = harness.Items.Single(item => item.ProductCode == "P1");

        // 真实分类：顺序在服务器上，离线置灰。
        Assert.False(viewModel.MoveUpCommand.CanExecute(alpha));
        Assert.False(viewModel.MoveDownCommand.CanExecute(harness.Items.Single(item => item.ProductCode == "P3")));
        Assert.False(viewModel.MoveToFrontCommand.CanExecute(alpha));
        await viewModel.MoveUpCommand.ExecuteAsync(alpha);
        Assert.Empty(harness.Service.OrderCalls);
    }

    // ================= 全部 / 未分类：沿用本机顺序 =================

    [Fact]
    public async Task Move_in_all_keeps_the_local_order_behavior()
    {
        var harness = Create(DefaultItems(), DefaultSnapshot());
        var viewModel = harness.ViewModel;
        await viewModel.EnsureLoadedAsync();
        viewModel.ToggleEditModeCommand.Execute(null);
        var charlie = harness.Items.Single(item => item.ProductCode == "P3");
        viewModel.SpecialItemCardCommand.Execute(charlie);

        await viewModel.MoveUpCommand.ExecuteAsync(charlie);

        Assert.Equal(["P1", "P3", "P2", "P4", "P5", "P6"], Assert.Single(harness.Repository.SavedOrders));
        Assert.Empty(harness.Service.OrderCalls);
        Assert.Equal(["P1", "P3", "P2", "P4", "P5", "P6"], Codes(viewModel));
    }

    [Fact]
    public async Task Move_in_uncategorized_steps_over_visible_neighbours_but_saves_the_local_order()
    {
        // 本机顺序里未分类商品（P4、P5、P6）与已归类商品交错排列。
        var items = DefaultItems();
        var interleaved = new[] { items[0], items[3], items[1], items[4], items[2], items[5] };
        var harness = Create(interleaved, DefaultSnapshot());
        var viewModel = harness.ViewModel;
        await viewModel.EnsureLoadedAsync();
        viewModel.SelectCategoryCommand.Execute(Entry(viewModel, "uncategorized"));
        Assert.Equal(["P4", "P5", "P6"], Codes(viewModel));
        viewModel.ToggleEditModeCommand.Execute(null);
        var echo = items[4];
        viewModel.SpecialItemCardCommand.Execute(echo);

        await viewModel.MoveUpCommand.ExecuteAsync(echo);

        // 视图里 P5 上移一格 = 越过 P4；全量本机顺序里它们之间隔着 P2，所以实际挪了两格。
        Assert.Equal(["P1", "P5", "P4", "P2", "P3", "P6"], Assert.Single(harness.Repository.SavedOrders));
        Assert.Equal(["P5", "P4", "P6"], Codes(viewModel));
        Assert.Empty(harness.Service.OrderCalls);

        await viewModel.MoveToFrontCommand.ExecuteAsync(items[5]);
        Assert.Equal(["P6", "P5", "P4"], Codes(viewModel));
        Assert.Equal(2, harness.Repository.SavedOrders.Count);
    }

    // ================= 其他 =================

    [Fact]
    public async Task Entering_the_page_again_keeps_the_category_but_clears_filters_and_the_picker()
    {
        var harness = Create(DefaultItems(), DefaultSnapshot());
        var viewModel = harness.ViewModel;
        await viewModel.EnsureLoadedAsync();
        viewModel.SelectCategoryCommand.Execute(Entry(viewModel, Balloons));
        viewModel.ToggleEditModeCommand.Execute(null);
        viewModel.OpenCategoryPickerCommand.Execute(harness.Items[0]);
        viewModel.ToggleEditModeCommand.Execute(null);
        Assert.False(viewModel.IsCategoryPickerOpen);
        viewModel.FilterText = "Bravo";

        viewModel.ActivateForEntry();

        Assert.Equal(Balloons, viewModel.CurrentCategoryId);
        Assert.Equal(string.Empty, viewModel.FilterText);
        Assert.Equal(["P3", "P1"], Codes(viewModel));
    }

    [Fact]
    public async Task Leaving_the_page_closes_the_picker()
    {
        var harness = Create(DefaultItems(), DefaultSnapshot());
        var viewModel = harness.ViewModel;
        await viewModel.EnsureLoadedAsync();
        viewModel.ToggleEditModeCommand.Execute(null);
        viewModel.OpenCategoryPickerCommand.Execute(harness.Items[0]);
        Assert.True(viewModel.IsCategoryPickerOpen);

        viewModel.BackCommand.Execute(null);

        Assert.False(viewModel.IsCategoryPickerOpen);
    }

    [Fact]
    public async Task Built_in_category_names_follow_the_ui_language()
    {
        var localization = new LocalizationService();
        var harness = Create(DefaultItems(), DefaultSnapshot(), localization: localization);
        await harness.ViewModel.EnsureLoadedAsync();

        try
        {
            localization.SetCulture(LocalizationService.ChineseCultureName);

            Assert.Equal("全部", harness.ViewModel.CategoryEntries[0].Name);
            Assert.Equal("未分类", harness.ViewModel.CategoryEntries[^1].Name);
            Assert.Equal("移到分类", harness.ViewModel.MoveToCategoryText);
        }
        finally
        {
            localization.SetCulture(LocalizationService.DefaultCultureName);
        }
    }

    // ================= 夹具 =================

    private static string SettingKey => "SpecialProducts:SelectedCategory:" + Store;

    private static string[] Codes(SpecialProductsViewModel viewModel) =>
        viewModel.DisplayedCards.Select(card => card.ProductCode).ToArray();

    private static SpecialCategoryEntryViewModel Entry(SpecialProductsViewModel viewModel, string? id) =>
        viewModel.CategoryEntries.Single(entry => entry.Id == id);

    /// <summary>本机顺序 P1..P6（Alpha..Foxtrot）；条码 930001..930006。</summary>
    private static SellableItemDto[] DefaultItems() =>
        new[] { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot" }
            .Select((name, index) => CreateItem($"P{index + 1}", name, $"93000{index + 1}"))
            .ToArray();

    /// <summary>Balloons：P3、P1（服务器顺序，与本机顺序相反）；Cards：P2；未分类：P4、P5、P6。</summary>
    private static SpecialProductCategorySnapshot DefaultSnapshot() => new(
        Store,
        [
            new SpecialProductCategoryDto(Balloons, "Balloons", "#2563EB", 0),
            new SpecialProductCategoryDto(Cards, "Cards", null, 1),
        ],
        [
            new SpecialProductCategoryAssignmentDto("P3", Balloons, 0),
            new SpecialProductCategoryAssignmentDto("P1", Balloons, 1),
            new SpecialProductCategoryAssignmentDto("P2", Cards, 0),
        ]);

    private static SellableItemDto CreateItem(string productCode, string displayName, string lookupCode) => new(
        Store,
        productCode,
        ReferenceCode: null,
        displayName,
        lookupCode,
        ItemNumber: productCode,
        Barcode: lookupCode,
        RetailPrice: 1.25m,
        PriceSourceKind.StoreRetailPrice,
        "store-retail",
        QuantityFactor: 1m,
        UpdatedAt: DateTimeOffset.UtcNow,
        ProductImage: null,
        DiscountRate: null,
        IsSpecialProduct: true);

    private static Harness Create(
        IReadOnlyList<SellableItemDto> items,
        SpecialProductCategorySnapshot? snapshot,
        bool online = true,
        string? savedCategory = null,
        LocalizationService? localization = null)
    {
        return Create(items, snapshot, snapshot, online, savedCategory, localization);
    }

    /// <summary>localSnapshot 是本机库里已有的快照，serverSnapshot 是服务端当前的快照（联网拉取 / 调整时用）。</summary>
    private static Harness Create(
        IReadOnlyList<SellableItemDto> items,
        SpecialProductCategorySnapshot? localSnapshot,
        SpecialProductCategorySnapshot? serverSnapshot,
        bool online = true,
        string? savedCategory = null,
        LocalizationService? localization = null)
    {
        var repository = new CategoryTestRepository
        {
            SpecialItems = items,
            LocalSnapshot = localSnapshot ?? SpecialProductCategorySnapshot.Empty(Store),
        };
        var service = new CategoryTestService(repository)
        {
            Server = serverSnapshot ?? SpecialProductCategorySnapshot.Empty(Store),
            DownloadItemCount = items.Count,
        };
        var settings = new InMemorySettings();
        if (savedCategory is not null)
        {
            settings.Values[SettingKey] = savedCategory;
        }

        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var workflow = new SpecialProductsWorkflowService(index, cart, repository, service, settings);
        var session = new PosSessionState("HB POS", Store, "Main Store", "POS-01", "C001", "Alice", online, 0);
        var viewModel = new SpecialProductsViewModel(
            index,
            cart,
            repository,
            service,
            session,
            localization ?? new LocalizationService(),
            () => { },
            workflowService: workflow);
        return new Harness(viewModel, repository, service, settings, cart, items);
    }

    private sealed record Harness(
        SpecialProductsViewModel ViewModel,
        CategoryTestRepository Repository,
        CategoryTestService Service,
        InMemorySettings Settings,
        PosCartService Cart,
        IReadOnlyList<SellableItemDto> Items);

    private sealed class InMemorySettings : ILocalAppSettingsRepository
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task SetValuesAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default)
        {
            foreach (var pair in values)
            {
                Values[pair.Key] = pair.Value;
            }

            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            Values.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class CategoryTestRepository : ILocalCatalogRepository
    {
        public IReadOnlyList<SellableItemDto> SpecialItems { get; set; } = [];

        public SpecialProductCategorySnapshot LocalSnapshot { get; set; } = SpecialProductCategorySnapshot.Empty(Store);

        public List<string[]> SavedOrders { get; } = [];

        public Task<SpecialProductCategorySnapshot> LoadSpecialProductCategorySnapshotAsync(string storeCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(LocalSnapshot);

        public Task ReplaceSpecialProductCategorySnapshotAsync(SpecialProductCategorySnapshot snapshot, CancellationToken cancellationToken = default)
        {
            LocalSnapshot = snapshot;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SellableItemDto>> LoadSpecialProductItemsAsync(string storeCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(SpecialItems);

        public Task SaveSpecialProductOrderAsync(string storeCode, IEnumerable<string> productCodes, CancellationToken cancellationToken = default)
        {
            SavedOrders.Add(productCodes.ToArray());
            return Task.CompletedTask;
        }

        public Task ReplaceSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpsertSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> DeleteByLookupCodesAsync(string storeCode, IEnumerable<string> lookupCodes, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<SellableItemDto?> FindByLookupCodeAsync(string storeCode, string lookupCode, CancellationToken cancellationToken = default) => Task.FromResult<SellableItemDto?>(null);

        public Task<int> UpdateSpecialProductFlagAsync(string storeCode, string productCode, bool isSpecialProduct, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<int> ClearSpecialProductFlagsExceptAsync(string storeCode, IEnumerable<string> productCodesToKeep, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<IReadOnlyList<LocalSellableItemCompareRow>> LoadSellableItemComparePageAsync(string storeCode, string? afterLookupCodeNormalized, int pageSize, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalSellableItemCompareRow>>([]);

        public Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SellableItemDto>>([]);

        public Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(string storeCode, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SellableItemDto>>([]);
    }

    /// <summary>模拟服务端：持有「服务器快照」，按真实接口语义处理移到分类与分类内排序（排列不符时抛冲突）。</summary>
    private sealed class CategoryTestService(CategoryTestRepository repository) : ISpecialProductService
    {
        public SpecialProductCategorySnapshot Server { get; set; } = SpecialProductCategorySnapshot.Empty(Store);

        public int DownloadItemCount { get; init; }

        public int RefreshCalls { get; private set; }

        public int DownloadCalls { get; private set; }

        public List<(string ProductCode, string? CategoryId)> AssignCalls { get; } = [];

        public List<(string CategoryId, string[] ProductCodes)> OrderCalls { get; } = [];

        public Exception? RefreshFailure { get; set; }

        public Exception? AssignFailure { get; set; }

        public Exception? OrderFailure { get; set; }

        public Task<SpecialProductCategorySnapshot> RefreshCategoriesAsync(string storeCode, CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            if (RefreshFailure is not null)
            {
                return Task.FromException<SpecialProductCategorySnapshot>(RefreshFailure);
            }

            return Task.FromResult(Publish());
        }

        public Task<SpecialProductCategorySnapshot> AssignProductToCategoryAsync(string storeCode, string productCode, string? categoryId, CancellationToken cancellationToken = default)
        {
            AssignCalls.Add((productCode, categoryId));
            if (AssignFailure is not null)
            {
                return Task.FromException<SpecialProductCategorySnapshot>(AssignFailure);
            }

            var assignments = Server.Assignments
                .Where(assignment => assignment.ProductCode != productCode)
                .ToList();
            if (categoryId is not null)
            {
                var next = assignments
                    .Where(assignment => assignment.CategoryId == categoryId)
                    .Select(assignment => assignment.SortOrder)
                    .DefaultIfEmpty(-1)
                    .Max() + 1;
                assignments.Add(new SpecialProductCategoryAssignmentDto(productCode, categoryId, next));
            }

            Server = Server with { Assignments = assignments };
            return Task.FromResult(Publish());
        }

        public Task<SpecialProductCategorySnapshot> ReorderCategoryProductsAsync(string storeCode, string categoryId, IReadOnlyList<string> productCodes, CancellationToken cancellationToken = default)
        {
            OrderCalls.Add((categoryId, productCodes.ToArray()));
            if (OrderFailure is not null)
            {
                return Task.FromException<SpecialProductCategorySnapshot>(OrderFailure);
            }

            // 与真实服务端一致：必须恰好是该分类当前全部商品的一个排列，否则冲突。
            var current = Server.Assignments.Where(assignment => assignment.CategoryId == categoryId).Select(assignment => assignment.ProductCode);
            if (!current.OrderBy(code => code, StringComparer.Ordinal).SequenceEqual(productCodes.OrderBy(code => code, StringComparer.Ordinal)))
            {
                return Task.FromException<SpecialProductCategorySnapshot>(new CatalogApiException(
                    "order conflict",
                    HttpStatusCode.Conflict,
                    "SPECIAL_PRODUCT_CATEGORY_ORDER_CONFLICT"));
            }

            var assignments = Server.Assignments.Where(assignment => assignment.CategoryId != categoryId).ToList();
            for (var index = 0; index < productCodes.Count; index++)
            {
                assignments.Add(new SpecialProductCategoryAssignmentDto(productCodes[index], categoryId, index));
            }

            Server = Server with { Assignments = assignments };
            return Task.FromResult(Publish());
        }

        public Task<SpecialProductMarkResult> MarkSpecialProductAsync(string storeCode, string productCode, bool isSpecialProduct, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SpecialProductDownloadResult> DownloadSpecialProductsAsync(string storeCode, CancellationToken cancellationToken = default, IProgress<SpecialProductDownloadProgress>? progress = null)
        {
            DownloadCalls++;
            return Task.FromResult(new SpecialProductDownloadResult(storeCode, 1, DownloadItemCount, DownloadItemCount, DownloadItemCount, 0));
        }

        /// <summary>服务端返回快照的同时，真实服务会覆盖本机（这里直接写假仓储）。</summary>
        private SpecialProductCategorySnapshot Publish()
        {
            repository.LocalSnapshot = Server;
            return Server;
        }
    }
}
