using System.Collections.Concurrent;
using BlazorApp.Shared.DTOs;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

[Collection(ShutdownTimingTestCollection.Name)]
public sealed class SpecialProductsViewModelTests
{
    [Fact]
    public void Dispose_stops_receiving_culture_changes()
    {
        var localization = new LocalizationService();
        var viewModel = new SpecialProductsViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            new FakeCatalogRepository(),
            new FakeSpecialProductService(),
            Session,
            localization,
            () => { });
        var localizedChangeCount = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SpecialProductsViewModel.TitleText))
            {
                localizedChangeCount++;
            }
        };

        try
        {
            localization.SetCulture(LocalizationService.ChineseCultureName);
            Assert.Equal(1, localizedChangeCount);

            viewModel.Dispose();
            viewModel.Dispose();
            localization.SetCulture(LocalizationService.DefaultCultureName);

            Assert.Equal(1, localizedChangeCount);
        }
        finally
        {
            viewModel.Dispose();
            localization.SetCulture(LocalizationService.DefaultCultureName);
        }
    }

    [Theory]
    [InlineData("download")]
    [InlineData("add")]
    [InlineData("remove")]
    public async Task Navigation_cancels_running_remote_operation_before_leaving(string operation)
    {
        var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration operationCancellationRegistration = default;
        var receivedToken = CancellationToken.None;
        var navigated = false;
        var cancellationRequestedBeforeNavigation = false;
        var cancellationApplied = false;
        Task<TResult> WaitForCancellationAsync<TResult>(CancellationToken cancellationToken)
        {
            receivedToken = cancellationToken;
            var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            operationCancellationRegistration = cancellationToken.Register(() =>
            {
                cancellationApplied = completion.TrySetCanceled(cancellationToken);
                cancellationObserved.TrySetResult();
            });
            operationStarted.TrySetResult();
            return completion.Task;
        }

        var workflow = new FakeSpecialProductsWorkflowService
        {
            DownloadAsyncOverride = cancellationToken =>
                WaitForCancellationAsync<SpecialProductsDownloadWorkflowResult>(cancellationToken),
            MarkAsyncOverride = (isSpecialProduct, cancellationToken) =>
                WaitForCancellationAsync<SpecialProductsMutationWorkflowResult>(cancellationToken)
        };
        var viewModel = CreateViewModel(
            workflow: workflow,
            onBack: () =>
            {
                cancellationRequestedBeforeNavigation = receivedToken.IsCancellationRequested;
                navigated = true;
            });
        var item = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: operation == "remove");
        if (operation != "download")
        {
            viewModel.ToggleEditModeCommand.Execute(null);
        }

        var execution = operation switch
        {
            "download" => viewModel.DownloadCommand.ExecuteAsync(null),
            "add" => viewModel.AddSpecialProductCommand.ExecuteAsync(item),
            _ => viewModel.RemoveSpecialProductCommand.ExecuteAsync(item)
        };
        await operationStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.True(viewModel.BackCommand.CanExecute(null));
        viewModel.BackCommand.Execute(null);
        await WaitUntilAsync(() => navigated);
        await cancellationObserved.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        var wasCancellationRequested = receivedToken.IsCancellationRequested;
        await execution.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        operationCancellationRegistration.Dispose();

        Assert.True(navigated);
        Assert.True(cancellationRequestedBeforeNavigation);
        Assert.True(receivedToken.CanBeCanceled);
        Assert.True(wasCancellationRequested);
        Assert.True(cancellationApplied);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task LoadAsync_shows_special_products_from_local_cache()
    {
        var repository = new FakeCatalogRepository
        {
            SpecialItems = [CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true)]
        };
        var viewModel = CreateViewModel(repository: repository);

        await viewModel.LoadAsync();

        var item = Assert.Single(viewModel.SpecialItems);
        Assert.Equal("SKU-001", item.ProductCode);
        Assert.True(item.IsSpecialProduct);
    }

    [Fact]
    public async Task AddToCartCommand_adds_tapped_special_product()
    {
        var cart = new PosCartService();
        var backCallCount = 0;
        var item = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var repository = new FakeCatalogRepository { SpecialItems = [item] };
        var viewModel = CreateViewModel(cart: cart, repository: repository, onBack: () => backCallCount++);
        await viewModel.LoadAsync();

        viewModel.AddToCartCommand.Execute(item);

        var line = Assert.Single(cart.Lines);
        Assert.Equal("SKU-001", line.ProductCode);
        Assert.Equal(1m, line.Quantity);
        // 加购后留在本页，方便连续点选；只有点「返回收银」才离开。
        Assert.Equal(0, backCallCount);
        Assert.Equal(1, repository.LoadSpecialProductItemsCallCount);
    }

    [Fact]
    public async Task AddToCartCommand_records_cart_add_operation_audit()
    {
        var cart = new PosCartService();
        var logger = new RecordingOperationAuditLogger();
        var item = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var repository = new FakeCatalogRepository { SpecialItems = [item] };
        var viewModel = CreateViewModel(cart: cart, repository: repository, operationAuditLogger: logger);
        await viewModel.LoadAsync();

        viewModel.AddToCartCommand.Execute(item);

        // 特价区绕过收银台购物车变更入口直接加购，必须单独留下加购审计。
        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("CART_ITEM_ADD", auditEvent.OperationType);
        Assert.Equal("Succeeded", auditEvent.Outcome);
        Assert.Equal("special-products", auditEvent.ReasonCode);
        Assert.Equal("C001", auditEvent.CashierId);
        var auditItem = Assert.Single(auditEvent.Items);
        Assert.Equal("SKU-001", auditItem.ProductCode);
        Assert.Equal(0m, auditItem.BeforeQuantity);
        Assert.Equal(1m, auditItem.AfterQuantity);
    }

    [Fact]
    public async Task SpecialItemCardCommand_normal_mode_adds_tapped_special_product_and_returns_to_cart_directly()
    {
        var cart = new PosCartService();
        var backCallCount = 0;
        CartLine? revealedLine = null;
        var item = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var repository = new FakeCatalogRepository { SpecialItems = [item] };
        var viewModel = CreateViewModel(
            cart: cart,
            repository: repository,
            onBack: () => backCallCount++,
            onCartLineAdded: line => revealedLine = line);
        await viewModel.LoadAsync();

        viewModel.SpecialItemCardCommand.Execute(item);

        // 点卡片直接加购并回到收银台：不停留页内、不弹确认，购物车行被定位出来。
        var line = Assert.Single(cart.Lines);
        Assert.Equal("SKU-001", line.ProductCode);
        Assert.Equal(1, backCallCount);
        Assert.Same(line, revealedLine);
        // 返回时清掉页内提示条与撤销状态，下次进入是干净页面。
        Assert.False(viewModel.IsToastVisible);
        Assert.False(viewModel.UndoAddCommand.CanExecute(null));
    }

    [Fact]
    public async Task SpecialItemCardCommand_rapid_double_tap_adds_only_one_unit_until_page_is_entered_again()
    {
        var cart = new PosCartService();
        var backCallCount = 0;
        var revealedLines = new List<CartLine>();
        var item = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var repository = new FakeCatalogRepository { SpecialItems = [item] };
        var viewModel = CreateViewModel(
            cart: cart,
            repository: repository,
            onBack: () => backCallCount++,
            onCartLineAdded: revealedLines.Add);
        await viewModel.LoadAsync();

        viewModel.SpecialItemCardCommand.Execute(item);
        viewModel.SpecialItemCardCommand.Execute(item);

        // 快速连点同一商品（含离开页面后残留的点击事件）只加一件、只返回一次。
        Assert.Equal(1m, Assert.Single(cart.Lines).Quantity);
        Assert.Equal(1, backCallCount);
        Assert.Single(revealedLines);

        // 下一次重新进入特殊商品页后恢复可点。
        viewModel.ActivateForEntry();
        viewModel.SpecialItemCardCommand.Execute(item);

        Assert.Equal(2m, Assert.Single(cart.Lines).Quantity);
        Assert.Equal(2, backCallCount);
    }

    [Fact]
    public async Task SpecialItemCardCommand_failed_add_stays_on_page_shows_message_and_allows_retry()
    {
        var cart = new PosCartService();
        var backCallCount = 0;
        var item = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var workflow = new FakeSpecialProductsWorkflowService
        {
            LoadResult = new SpecialProductsLoadResult("S001", [item]),
            AddToCartException = new InvalidOperationException("item unavailable")
        };
        var logger = new RecordingOperationAuditLogger();
        var viewModel = CreateViewModel(
            cart: cart,
            workflow: workflow,
            onBack: () => backCallCount++,
            operationAuditLogger: logger);
        await viewModel.LoadAsync();

        viewModel.SpecialItemCardCommand.Execute(item);

        // 失败：不返回收银台，状态栏与提示条都给出明确原因（提示条不带撤销），并留下失败审计。
        Assert.Equal(0, backCallCount);
        Assert.Contains("item unavailable", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.True(viewModel.IsToastVisible);
        Assert.Contains("item unavailable", viewModel.ToastText, StringComparison.Ordinal);
        Assert.False(viewModel.CanUndoToast);
        var failedAudit = Assert.Single(logger.Events);
        Assert.Equal("Failed", failedAudit.Outcome);

        // 失败不会把页面锁死：问题消除后再点一次即可加购并返回。
        workflow.AddToCartException = null;
        viewModel.SpecialItemCardCommand.Execute(item);

        Assert.Equal(1, backCallCount);
    }

    [Fact]
    public async Task SpecialItemCardCommand_edit_mode_selects_without_adding_to_cart()
    {
        var cart = new PosCartService();
        var backCallCount = 0;
        var item = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var repository = new FakeCatalogRepository { SpecialItems = [item] };
        var viewModel = CreateViewModel(cart: cart, repository: repository, onBack: () => backCallCount++);
        await viewModel.LoadAsync();
        viewModel.ToggleEditModeCommand.Execute(null);

        viewModel.SpecialItemCardCommand.Execute(item);

        Assert.Same(item, viewModel.SelectedSpecialItem);
        Assert.Empty(cart.Lines);
        Assert.Equal(0, backCallCount);
    }

    [Fact]
    public async Task AddToCartCommand_writes_diagnostic_log()
    {
        var cart = new PosCartService();
        var item = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var repository = new FakeCatalogRepository { SpecialItems = [item] };
        var viewModel = CreateViewModel(cart: cart, repository: repository);
        var logs = new ConcurrentQueue<string>();
        await viewModel.LoadAsync();

        using var logCapture = CaptureClientLog(logs);
        viewModel.AddToCartCommand.Execute(item);

        Assert.True(HasLog(logs, "operation=add-to-cart"));
        Assert.True(HasLog(logs, "productCode=SKU-001"));
        Assert.True(HasLog(logs, "cartLines=1"));
        Assert.True(HasLog(logs, "totalElapsedMs="));
    }

    [Fact]
    public async Task AddToCartCommand_reports_added_cart_line_for_reveal_when_returning_to_cashier()
    {
        var cart = new PosCartService();
        CartLine? revealedLine = null;
        var item = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var repository = new FakeCatalogRepository { SpecialItems = [item] };
        var viewModel = CreateViewModel(
            cart: cart,
            repository: repository,
            onCartLineAdded: line => revealedLine = line);
        await viewModel.LoadAsync();

        viewModel.AddToCartCommand.Execute(item);

        // 留在页内时不定位购物车行；返回收银后才定位到最后加入的那一行。
        Assert.Null(revealedLine);
        viewModel.BackCommand.Execute(null);

        var line = Assert.Single(cart.Lines);
        Assert.Same(line, revealedLine);
    }

    [Fact]
    public async Task AddToCartCommand_reveals_existing_cart_line_once_after_quantity_increases()
    {
        var cart = new PosCartService();
        var revealedLines = new List<CartLine>();
        var item = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var repository = new FakeCatalogRepository { SpecialItems = [item] };
        var viewModel = CreateViewModel(
            cart: cart,
            repository: repository,
            onCartLineAdded: revealedLines.Add);
        await viewModel.LoadAsync();

        viewModel.AddToCartCommand.Execute(item);
        viewModel.AddToCartCommand.Execute(item);
        viewModel.BackCommand.Execute(null);

        var line = Assert.Single(cart.Lines);
        Assert.Equal(2m, line.Quantity);
        var revealedLine = Assert.Single(revealedLines);
        Assert.Same(line, revealedLine);
    }

    [Fact]
    public async Task PreloadAsync_loads_special_products_and_shows_all_cards_without_paging()
    {
        var repository = new FakeCatalogRepository { SpecialItems = CreateSpecialItems(21) };
        var viewModel = CreateViewModel(repository: repository);

        await viewModel.PreloadAsync();

        Assert.Equal(1, repository.LoadSpecialProductItemsCallCount);
        Assert.Equal(21, viewModel.DisplayedCards.Count);
        Assert.False(viewModel.HasMoreToShow);
    }

    [Fact]
    public async Task PreloadAsync_populates_collections_from_workflow_service()
    {
        var workflow = new FakeSpecialProductsWorkflowService
        {
            PreloadResult = new SpecialProductsLoadResult("S001", CreateSpecialItems(21))
        };
        var viewModel = CreateViewModel(workflow: workflow);

        await viewModel.PreloadAsync();

        Assert.Equal(1, workflow.PreloadCallCount);
        Assert.Equal(21, viewModel.DisplayedCards.Count);
        Assert.Equal(21, viewModel.SpecialItems.Count);
    }

    [Fact]
    public async Task PreloadAsync_loads_special_products_without_thumbnail_preload()
    {
        var thumbnailPreloadCallCount = 0;
        var viewModel = CreateViewModel(
            repository: new FakeCatalogRepository { SpecialItems = CreateSpecialItems(2) },
            thumbnailPreloadAsync: (_, _, _) =>
            {
                thumbnailPreloadCallCount++;
                return Task.FromResult(0);
            });

        await viewModel.PreloadAsync();

        Assert.Equal(2, viewModel.SpecialItems.Count);
        Assert.Equal(0, thumbnailPreloadCallCount);
    }

    [Fact]
    public async Task EnsureLoadedAsync_after_preload_does_not_reload_local_cache()
    {
        var repository = new FakeCatalogRepository { SpecialItems = CreateSpecialItems(1) };
        var viewModel = CreateViewModel(repository: repository);

        await viewModel.PreloadAsync();
        await viewModel.EnsureLoadedAsync();

        Assert.Equal(1, repository.LoadSpecialProductItemsCallCount);
    }

    [Fact]
    public async Task EnsureLoadedAsync_reuses_inflight_preload()
    {
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new FakeCatalogRepository
        {
            SpecialItems = CreateSpecialItems(1),
            BeforeLoadSpecialProductItemsAsync = () => releaseLoad.Task
        };
        var viewModel = CreateViewModel(repository: repository);

        var preloadTask = viewModel.PreloadAsync();
        var ensureTask = viewModel.EnsureLoadedAsync();
        releaseLoad.SetResult();
        await Task.WhenAll(preloadTask, ensureTask);

        Assert.Equal(1, repository.LoadSpecialProductItemsCallCount);
        Assert.Single(viewModel.DisplayedCards);
    }

    [Fact]
    public async Task RefreshCommand_forces_reload_after_preload()
    {
        var repository = new FakeCatalogRepository { SpecialItems = CreateSpecialItems(1) };
        var viewModel = CreateViewModel(repository: repository);

        await viewModel.PreloadAsync();
        await viewModel.EnsureLoadedAsync();
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, repository.LoadSpecialProductItemsCallCount);
    }

    [Fact]
    public async Task PreloadFirstPageThumbnailsAsync_warms_product_thumbnail_cache_for_first_page_only()
    {
        var repository = new FakeCatalogRepository { SpecialItems = CreateSpecialItems(21) };
        IReadOnlyList<string?> preloadedSources = [];
        var viewModel = CreateViewModel(
            repository: repository,
            thumbnailPreloadAsync: (sources, decodePixelWidth, _) =>
            {
                Assert.Equal(72, decodePixelWidth);
                preloadedSources = sources.ToArray();
                return Task.FromResult(preloadedSources.Count);
            });

        await viewModel.PreloadFirstPageThumbnailsAsync();

        Assert.Equal(20, preloadedSources.Count);
        Assert.Equal(repository.SpecialItems.Take(20).Select(item => item.ProductImage), preloadedSources);
        Assert.Equal(21, viewModel.DisplayedCards.Count);
    }

    [Fact]
    public async Task PreloadFirstPageThumbnailsAsync_ignores_empty_images_without_failing_data_preload()
    {
        var repository = new FakeCatalogRepository
        {
            SpecialItems =
            [
                CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true) with { ProductImage = null },
                CreateItem("SKU-002", "Beta", "930002", isSpecialProduct: true) with { ProductImage = "   " }
            ]
        };
        var preloadCallCount = 0;
        var viewModel = CreateViewModel(
            repository: repository,
            thumbnailPreloadAsync: (_, _, _) =>
            {
                preloadCallCount++;
                return Task.FromResult(0);
            });

        await viewModel.PreloadFirstPageThumbnailsAsync();

        Assert.Equal(0, preloadCallCount);
        Assert.Equal(2, viewModel.SpecialItems.Count);
    }

    [Fact]
    public async Task PreloadFirstPageThumbnailsAsync_keeps_loaded_items_when_thumbnail_preload_fails()
    {
        var repository = new FakeCatalogRepository { SpecialItems = CreateSpecialItems(1) };
        var viewModel = CreateViewModel(
            repository: repository,
            thumbnailPreloadAsync: (_, _, _) => throw new IOException("thumbnail failed"));

        await viewModel.PreloadFirstPageThumbnailsAsync();

        Assert.Single(viewModel.SpecialItems);
        Assert.Single(viewModel.DisplayedCards);
    }

    [Fact]
    public async Task PreloadFirstPageThumbnailsAsync_does_not_throw_when_special_products_are_empty()
    {
        var preloadCallCount = 0;
        var viewModel = CreateViewModel(
            repository: new FakeCatalogRepository { SpecialItems = [] },
            thumbnailPreloadAsync: (_, _, _) =>
            {
                preloadCallCount++;
                return Task.FromResult(0);
            });

        await viewModel.PreloadFirstPageThumbnailsAsync();

        Assert.Equal(0, preloadCallCount);
        Assert.Empty(viewModel.SpecialItems);
    }

    [Fact]
    public async Task Offline_edit_commands_are_disabled_and_do_not_call_backend()
    {
        var item = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var repository = new FakeCatalogRepository { SpecialItems = [item] };
        var service = new FakeSpecialProductService();
        var viewModel = CreateViewModel(
            repository: repository,
            service: service,
            session: Session with { IsOnline = false });
        await viewModel.LoadAsync();
        viewModel.ToggleEditModeCommand.Execute(null);

        await viewModel.AddSpecialProductCommand.ExecuteAsync(item);
        await viewModel.RemoveSpecialProductCommand.ExecuteAsync(item);

        Assert.False(viewModel.AddSpecialProductCommand.CanExecute(item));
        Assert.False(viewModel.RemoveSpecialProductCommand.CanExecute(item));
        Assert.Equal(0, service.CallCount);
    }

    [Fact]
    public async Task MoveDown_saves_local_order_without_calling_backend()
    {
        var first = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var second = CreateItem("SKU-002", "Beta", "930002", isSpecialProduct: true);
        var repository = new FakeCatalogRepository { SpecialItems = [first, second] };
        var service = new FakeSpecialProductService();
        var viewModel = CreateViewModel(repository: repository, service: service);
        await viewModel.LoadAsync();
        viewModel.ToggleEditModeCommand.Execute(null);

        await viewModel.MoveDownCommand.ExecuteAsync(first);

        Assert.Equal(["SKU-002", "SKU-001"], viewModel.SpecialItems.Select(x => x.ProductCode).ToArray());
        Assert.Equal(["SKU-002", "SKU-001"], Assert.Single(repository.SavedOrders));
        Assert.Equal(0, service.CallCount);
    }

    [Fact]
    public async Task Selected_item_commands_reorder_and_remove_selected_special_product()
    {
        var first = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var second = CreateItem("SKU-002", "Beta", "930002", isSpecialProduct: true);
        var repository = new FakeCatalogRepository { SpecialItems = [first, second] };
        var service = new FakeSpecialProductService
        {
            OnMark = (productCode, isSpecialProduct) =>
            {
                if (!isSpecialProduct)
                {
                    repository.SpecialItems = repository.SpecialItems
                        .Where(item => item.ProductCode != productCode)
                        .ToArray();
                }
            }
        };
        var viewModel = CreateViewModel(repository: repository, service: service);
        await viewModel.LoadAsync();
        viewModel.ToggleEditModeCommand.Execute(null);

        viewModel.SpecialItemCardCommand.Execute(second);
        await viewModel.MoveUpCommand.ExecuteAsync(viewModel.SelectedSpecialItem);
        await viewModel.RemoveSpecialProductCommand.ExecuteAsync(viewModel.SelectedSpecialItem);

        Assert.Equal(["SKU-002", "SKU-001"], Assert.Single(repository.SavedOrders));
        Assert.Equal(("S001", "SKU-002", false), service.LastCall);
        Assert.DoesNotContain(viewModel.SpecialItems, item => item.ProductCode == "SKU-002");
    }

    [Fact]
    public async Task LoadAsync_with_more_than_one_chunk_shows_first_hundred_then_show_more()
    {
        var repository = new FakeCatalogRepository { SpecialItems = CreateSpecialItems(101) };
        var viewModel = CreateViewModel(repository: repository);

        await viewModel.LoadAsync();

        Assert.Equal(100, viewModel.DisplayedCards.Count);
        Assert.True(viewModel.HasMoreToShow);
        Assert.Equal("SKU-001", viewModel.DisplayedCards.First().ProductCode);
        Assert.True(viewModel.ShowMoreCommand.CanExecute(null));

        viewModel.ShowMoreCommand.Execute(null);

        Assert.Equal(101, viewModel.DisplayedCards.Count);
        Assert.False(viewModel.HasMoreToShow);
        Assert.False(viewModel.ShowMoreCommand.CanExecute(null));
    }

    [Fact]
    public void Edit_mode_is_off_by_default_and_can_be_toggled()
    {
        var viewModel = CreateViewModel();

        Assert.False(viewModel.IsEditMode);

        viewModel.ToggleEditModeCommand.Execute(null);

        Assert.True(viewModel.IsEditMode);

        viewModel.ToggleEditModeCommand.Execute(null);

        Assert.False(viewModel.IsEditMode);
    }

    [Fact]
    public async Task ActivateForEntry_resets_filter_edit_search_toast_and_selection()
    {
        var repository = new FakeCatalogRepository { SpecialItems = CreateSpecialItems(21) };
        var searchItem = CreateItem("SKU-999", "Search Item", "930999");
        var workflow = new FakeSpecialProductsWorkflowService
        {
            SearchItems = [searchItem],
            LoadResult = new SpecialProductsLoadResult("S001", repository.SpecialItems)
        };
        var viewModel = CreateViewModel(workflow: workflow);
        await viewModel.LoadAsync();
        viewModel.FilterText = "Item 021";
        viewModel.ToggleEditModeCommand.Execute(null);
        viewModel.SearchText = "930999";
        viewModel.SearchCommand.Execute(null);
        viewModel.SpecialItemCardCommand.Execute(viewModel.SpecialItems.Last());

        viewModel.ActivateForEntry();

        Assert.False(viewModel.IsEditMode);
        Assert.Empty(viewModel.FilterText);
        Assert.False(viewModel.IsCartFilterActive);
        Assert.False(viewModel.IsToastVisible);
        Assert.False(viewModel.IsPriceEditorOpen);
        Assert.Empty(viewModel.SearchText);
        Assert.Empty(viewModel.SearchResults);
        Assert.Null(viewModel.SelectedSearchResult);
        Assert.Null(viewModel.SelectedSpecialItem);
        Assert.Equal(21, viewModel.DisplayedCards.Count);
        Assert.Equal("SKU-001", viewModel.DisplayedCards.First().ProductCode);
    }

    [Fact]
    public async Task ActivateForEntry_after_edit_add_returns_to_normal_mode_with_all_items()
    {
        var initialItems = CreateSpecialItems(20);
        var addedItem = CreateItem("SKU-021", "Item 021", "930021");
        var repository = new FakeCatalogRepository { SpecialItems = initialItems };
        var service = new FakeSpecialProductService
        {
            OnMark = (productCode, isSpecialProduct) =>
            {
                if (isSpecialProduct && productCode == addedItem.ProductCode)
                {
                    repository.SpecialItems = [.. initialItems, addedItem with { IsSpecialProduct = true }];
                }
            }
        };
        var viewModel = CreateViewModel(repository: repository, service: service);
        await viewModel.LoadAsync();
        viewModel.ToggleEditModeCommand.Execute(null);

        await viewModel.AddSpecialProductCommand.ExecuteAsync(addedItem);
        viewModel.ActivateForEntry();

        Assert.False(viewModel.IsEditMode);
        Assert.Equal(21, viewModel.DisplayedCards.Count);
        Assert.Equal("SKU-001", viewModel.DisplayedCards.First().ProductCode);
    }

    [Fact]
    public async Task ActivateForEntry_disables_thumbnails_immediately_and_reenables_after_delay_when_page_is_cold()
    {
        var thumbnailDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = CreateViewModel(
            repository: new FakeCatalogRepository { SpecialItems = CreateSpecialItems(1) },
            delayAsync: (_, cancellationToken) => thumbnailDelay.Task.WaitAsync(cancellationToken));

        Assert.True(viewModel.AreThumbnailsEnabled);

        viewModel.ActivateForEntry();

        Assert.False(viewModel.AreThumbnailsEnabled);
        thumbnailDelay.SetResult();
        await WaitUntilAsync(() => viewModel.AreThumbnailsEnabled);
        Assert.True(viewModel.AreThumbnailsEnabled);
    }

    [Fact]
    public async Task ActivateForEntry_latest_delay_controls_when_thumbnails_are_reenabled_for_cold_page()
    {
        var thumbnailDelays = new List<TaskCompletionSource>();
        var viewModel = CreateViewModel(
            repository: new FakeCatalogRepository { SpecialItems = CreateSpecialItems(1) },
            delayAsync: (_, cancellationToken) =>
            {
                var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                thumbnailDelays.Add(delay);
                return delay.Task.WaitAsync(cancellationToken);
            });

        viewModel.ActivateForEntry();
        var firstDelay = Assert.Single(thumbnailDelays);
        Assert.False(viewModel.AreThumbnailsEnabled);

        viewModel.ActivateForEntry();

        Assert.Equal(2, thumbnailDelays.Count);
        var secondDelay = thumbnailDelays[1];
        Assert.False(viewModel.AreThumbnailsEnabled);

        firstDelay.SetResult();
        await Task.Delay(50);
        Assert.False(viewModel.AreThumbnailsEnabled);

        secondDelay.SetResult();
        await WaitUntilAsync(() => viewModel.AreThumbnailsEnabled);
        Assert.True(viewModel.AreThumbnailsEnabled);
    }

    [Fact]
    public async Task ActivateForEntry_keeps_ready_first_page_and_thumbnails_warm()
    {
        var delayCallCount = 0;
        var viewModel = CreateViewModel(
            repository: new FakeCatalogRepository { SpecialItems = CreateSpecialItems(20) },
            delayAsync: (_, _) =>
            {
                delayCallCount++;
                return Task.CompletedTask;
            });
        await viewModel.LoadAsync();
        var collectionChangeCount = 0;
        viewModel.DisplayedCards.CollectionChanged += (_, _) => collectionChangeCount++;

        viewModel.ActivateForEntry();

        Assert.Equal(0, delayCallCount);
        Assert.True(viewModel.AreThumbnailsEnabled);
        // 序列未变时不重建网格，缩略图不会重新解码。
        Assert.Equal(0, collectionChangeCount);
        Assert.Equal(20, viewModel.DisplayedCards.Count);
        Assert.Equal("SKU-001", viewModel.DisplayedCards.First().ProductCode);
    }

    [Fact]
    public void ScannerBarcode_when_edit_mode_is_off_is_consumed_without_searching()
    {
        var item = CreateItem("SKU-001", "Alpha", "930001");
        var workflow = new FakeSpecialProductsWorkflowService
        {
            SearchItems = [item]
        };
        var viewModel = CreateViewModel(workflow: workflow);

        var processed = viewModel.ProcessScannerBarcode("930001", "scanner-device", "test");

        Assert.True(processed);
        Assert.Equal(0, workflow.SearchCallCount);
        Assert.Equal(0, workflow.MarkCallCount);
        Assert.Empty(viewModel.SearchResults);
        Assert.Contains("edit", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScannerBarcode_when_edit_mode_is_on_searches_and_selects_candidate_without_marking()
    {
        var item = CreateItem("SKU-001", "Alpha", "930001");
        var workflow = new FakeSpecialProductsWorkflowService
        {
            SearchItems = [item]
        };
        var viewModel = CreateViewModel(workflow: workflow);
        viewModel.ToggleEditModeCommand.Execute(null);

        var processed = viewModel.ProcessScannerBarcode("930001", "scanner-device", "test");

        Assert.True(processed);
        Assert.Equal("930001", viewModel.SearchText);
        Assert.Equal(1, workflow.SearchCallCount);
        Assert.Equal("930001", workflow.LastSearchText);
        Assert.Equal("SKU-001", Assert.Single(viewModel.SearchResults).ProductCode);
        Assert.Same(item, viewModel.SelectedSearchResult);
        Assert.Equal(0, workflow.MarkCallCount);
        Assert.Contains("Alpha", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScannerBarcode_search_applies_only_the_latest_result()
    {
        var firstSearchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newItem = CreateItem("SKU-NEW", "New Candidate", "930NEW");
        var workflow = new FakeSpecialProductsWorkflowService
        {
            SearchAsyncOverride = async (_, query, cancellationToken) =>
            {
                if (query == "old")
                {
                    firstSearchStarted.TrySetResult();
                    // 在取消异常里记录，而不是另注册回调：Cancel() 先执行 Task.Delay 的回调，其续延若被空闲线程
                    // 立刻执行，using 会在观察回调运行前注销它，导致取消已发生却永远观察不到。
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        firstCancellationObserved.TrySetResult();
                        throw;
                    }
                }

                return new SpecialProductsSearchResult("S001", query, [newItem]);
            }
        };
        using var viewModel = CreateViewModel(workflow: workflow);
        viewModel.ToggleEditModeCommand.Execute(null);

        Assert.True(viewModel.ProcessScannerBarcode("old", "scanner-device", "raw"));
        await firstSearchStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        Assert.True(viewModel.ProcessScannerBarcode("new", "scanner-device", "raw"));

        await WaitUntilAsync(() => viewModel.SearchResults.Count == 1);
        await firstCancellationObserved.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.Equal("new", workflow.LastSearchText);
        Assert.Equal(newItem.ProductCode, Assert.Single(viewModel.SearchResults).ProductCode);
    }

    [Theory]
    [InlineData("HBPOSE1-K1-AA-BB")]
    [InlineData("HBPOSE2-compact-token")]
    public void ScannerBarcode_emergency_token_is_consumed_without_product_search(string token)
    {
        var workflow = new FakeSpecialProductsWorkflowService();
        var viewModel = CreateViewModel(workflow: workflow);
        viewModel.ToggleEditModeCommand.Execute(null);
        viewModel.SearchText = "existing-query";

        var processed = viewModel.ProcessScannerBarcode(token, "scanner-device", "raw");

        Assert.True(processed);
        Assert.Equal("existing-query", viewModel.SearchText);
        Assert.Equal(0, workflow.SearchCallCount);
        Assert.Empty(viewModel.SearchResults);
    }

    [Fact]
    public async Task MoveToFront_saves_order_with_selected_item_first_and_keeps_it_selected()
    {
        var repository = new FakeCatalogRepository { SpecialItems = CreateSpecialItems(3) };
        var viewModel = CreateViewModel(repository: repository);
        await viewModel.LoadAsync();
        viewModel.ToggleEditModeCommand.Execute(null);
        var third = viewModel.SpecialItems.Last();
        viewModel.SpecialItemCardCommand.Execute(third);

        Assert.True(viewModel.MoveToFrontCommand.CanExecute(third));
        await viewModel.MoveToFrontCommand.ExecuteAsync(third);

        Assert.Equal(["SKU-003", "SKU-001", "SKU-002"], Assert.Single(repository.SavedOrders));
        Assert.Equal("SKU-003", viewModel.DisplayedCards.First().ProductCode);
        Assert.Equal(1, viewModel.DisplayedCards.First().Position);
        Assert.Equal("SKU-003", viewModel.SelectedSpecialItem?.ProductCode);
        Assert.False(viewModel.MoveToFrontCommand.CanExecute(viewModel.SelectedSpecialItem));
    }

    [Fact]
    public async Task Online_add_calls_service_and_reloads_local_cache()
    {
        var item = CreateItem("SKU-001", "Alpha", "930001");
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([item]);
        var repository = new FakeCatalogRepository
        {
            SellableItems = [item with { IsSpecialProduct = true }],
            SpecialItems = [item with { IsSpecialProduct = true }]
        };
        var markedItem = item with { IsSpecialProduct = true };
        var service = new FakeSpecialProductService
        {
            MarkResultFactory = (_, _) => new SpecialProductMarkResult([markedItem], [markedItem])
        };
        var viewModel = CreateViewModel(index, repository: repository, service: service);
        viewModel.ToggleEditModeCommand.Execute(null);

        await viewModel.AddSpecialProductCommand.ExecuteAsync(item);

        Assert.Equal(1, service.CallCount);
        Assert.Equal(("S001", "SKU-001", true), service.LastCall);
        Assert.Equal(0, repository.LoadSellableItemsCallCount);
        Assert.Contains(viewModel.SpecialItems, x => x.ProductCode == "SKU-001" && x.IsSpecialProduct);
    }

    [Fact]
    public async Task Online_add_uses_workflow_result_to_refresh_special_items()
    {
        var item = CreateItem("SKU-021", "Item 021", "930021");
        var workflow = new FakeSpecialProductsWorkflowService
        {
            MarkResultFactory = (_, _, isSpecialProduct) => new SpecialProductsMutationWorkflowResult(
                "S001",
                "SKU-021",
                isSpecialProduct,
                [item with { IsSpecialProduct = true }])
        };
        var viewModel = CreateViewModel(workflow: workflow);
        viewModel.ToggleEditModeCommand.Execute(null);

        await viewModel.AddSpecialProductCommand.ExecuteAsync(item);

        Assert.Equal(1, workflow.MarkCallCount);
        Assert.Equal(("S001", "SKU-021", true), workflow.LastMarkCall);
        Assert.Contains(viewModel.SpecialItems, specialItem => specialItem.ProductCode == "SKU-021" && specialItem.IsSpecialProduct);
    }

    [Fact]
    public async Task Online_add_successfully_clears_scanned_candidate_without_researching()
    {
        var existingItem = CreateItem("SKU-001", "Existing", "930001", isSpecialProduct: true);
        var scannedItem = CreateItem("SKU-002", "Scanned", "930002");
        var workflow = new FakeSpecialProductsWorkflowService
        {
            LoadResult = new SpecialProductsLoadResult("S001", [existingItem]),
            SearchItems = [scannedItem],
            MarkResultFactory = (_, _, isSpecialProduct) => new SpecialProductsMutationWorkflowResult(
                "S001",
                scannedItem.ProductCode,
                isSpecialProduct,
                [existingItem, scannedItem with { IsSpecialProduct = true }])
        };
        var viewModel = CreateViewModel(workflow: workflow);
        await viewModel.LoadAsync();
        viewModel.ToggleEditModeCommand.Execute(null);
        viewModel.SearchText = scannedItem.LookupCode;
        await viewModel.SearchCommand.ExecuteAsync(null);

        await viewModel.AddSpecialProductCommand.ExecuteAsync(scannedItem);

        Assert.Contains(viewModel.SpecialItems, item => item.ProductCode == scannedItem.ProductCode && item.IsSpecialProduct);
        Assert.Empty(viewModel.SearchResults);
        Assert.Null(viewModel.SelectedSearchResult);
        Assert.Equal(string.Empty, viewModel.SearchText);
        Assert.Equal(1, workflow.SearchCallCount);
        Assert.Contains("Scanned is now a special product.", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_add_preserves_special_selection_and_scanned_candidate_with_error_status()
    {
        var existingItem = CreateItem("SKU-001", "Existing", "930001", isSpecialProduct: true);
        var scannedItem = CreateItem("SKU-002", "Scanned", "930002");
        var workflow = new FakeSpecialProductsWorkflowService
        {
            LoadResult = new SpecialProductsLoadResult("S001", [existingItem]),
            SearchItems = [scannedItem],
            MarkAsyncOverride = (_, _) => Task.FromException<SpecialProductsMutationWorkflowResult>(
                new InvalidOperationException("network unavailable"))
        };
        var viewModel = CreateViewModel(workflow: workflow);
        await viewModel.LoadAsync();
        viewModel.ToggleEditModeCommand.Execute(null);
        viewModel.SpecialItemCardCommand.Execute(existingItem);
        viewModel.SearchText = scannedItem.LookupCode;
        await viewModel.SearchCommand.ExecuteAsync(null);

        await viewModel.AddSpecialProductCommand.ExecuteAsync(scannedItem);

        Assert.Equal(existingItem.ProductCode, Assert.Single(viewModel.SpecialItems).ProductCode);
        Assert.Equal(existingItem.ProductCode, viewModel.SelectedSpecialItem?.ProductCode);
        Assert.Equal(scannedItem.LookupCode, viewModel.SearchText);
        Assert.Equal(scannedItem.ProductCode, Assert.Single(viewModel.SearchResults).ProductCode);
        Assert.Equal(scannedItem.ProductCode, viewModel.SelectedSearchResult?.ProductCode);
        Assert.Contains("network unavailable", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_remove_preserves_special_list_cards_and_selection_with_error_status()
    {
        var specialItems = CreateSpecialItems(2);
        var workflow = new FakeSpecialProductsWorkflowService
        {
            LoadResult = new SpecialProductsLoadResult("S001", specialItems),
            MarkAsyncOverride = (_, _) => Task.FromException<SpecialProductsMutationWorkflowResult>(
                new InvalidOperationException("network unavailable"))
        };
        var viewModel = CreateViewModel(workflow: workflow);
        await viewModel.LoadAsync();
        viewModel.ToggleEditModeCommand.Execute(null);
        var selectedItem = viewModel.SpecialItems.Last();
        viewModel.SpecialItemCardCommand.Execute(selectedItem);
        var specialItemCodes = viewModel.SpecialItems.Select(item => item.ProductCode).ToArray();
        var displayedItemCodes = viewModel.DisplayedCards.Select(card => card.ProductCode).ToArray();

        await viewModel.RemoveSpecialProductCommand.ExecuteAsync(selectedItem);

        Assert.Equal(specialItemCodes, viewModel.SpecialItems.Select(item => item.ProductCode));
        Assert.Equal(displayedItemCodes, viewModel.DisplayedCards.Select(card => card.ProductCode));
        Assert.Equal(selectedItem.ProductCode, viewModel.SelectedSpecialItem?.ProductCode);
        Assert.Contains("network unavailable", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Online_add_writes_mark_diagnostic_log()
    {
        var item = CreateItem("SKU-001", "Alpha", "930001");
        var index = new LocalSellableItemIndex();
        var logs = new ConcurrentQueue<string>();
        index.ReplaceAll([item]);
        var repository = new FakeCatalogRepository
        {
            SellableItems = [item with { IsSpecialProduct = true }],
            SpecialItems = [item with { IsSpecialProduct = true }]
        };
        var viewModel = CreateViewModel(index, repository: repository);
        viewModel.ToggleEditModeCommand.Execute(null);

        using var logCapture = CaptureClientLog(logs);
        await viewModel.AddSpecialProductCommand.ExecuteAsync(item);

        Assert.True(HasLog(logs, "operation=mark"));
        Assert.True(HasLog(logs, "productCode=SKU-001"));
        Assert.True(HasLog(logs, "serviceElapsedMs="));
        Assert.True(HasLog(logs, "pageRefreshElapsedMs="));
    }

    [Fact]
    public async Task Remove_item_drops_its_card_and_clears_selection()
    {
        var initialItems = CreateSpecialItems(21);
        var repository = new FakeCatalogRepository { SpecialItems = initialItems };
        var service = new FakeSpecialProductService
        {
            OnMark = (_, isSpecialProduct) =>
            {
                if (!isSpecialProduct)
                {
                    repository.SpecialItems = initialItems.Take(20).ToArray();
                }
            }
        };
        var viewModel = CreateViewModel(repository: repository, service: service);
        await viewModel.LoadAsync();
        viewModel.ToggleEditModeCommand.Execute(null);
        var lastItem = viewModel.SpecialItems.Last();
        viewModel.SpecialItemCardCommand.Execute(lastItem);

        await viewModel.RemoveSpecialProductCommand.ExecuteAsync(lastItem);

        Assert.Equal(20, viewModel.DisplayedCards.Count);
        Assert.DoesNotContain(viewModel.DisplayedCards, card => card.ProductCode == "SKU-021");
        Assert.Null(viewModel.SelectedSpecialItem);
    }

    [Fact]
    public async Task Add_item_appends_card_and_requests_focus_on_it()
    {
        var initialItems = CreateSpecialItems(20);
        var addedItem = CreateItem("SKU-021", "Item 021", "930021");
        var repository = new FakeCatalogRepository { SpecialItems = initialItems };
        var service = new FakeSpecialProductService
        {
            OnMark = (productCode, isSpecialProduct) =>
            {
                if (isSpecialProduct && productCode == addedItem.ProductCode)
                {
                    repository.SpecialItems = [.. initialItems, addedItem with { IsSpecialProduct = true }];
                }
            }
        };
        var viewModel = CreateViewModel(repository: repository, service: service);
        await viewModel.LoadAsync();
        viewModel.ToggleEditModeCommand.Execute(null);
        var focusRequests = new List<string>();
        viewModel.FocusProductRequested += (_, productCode) => focusRequests.Add(productCode);

        await viewModel.AddSpecialProductCommand.ExecuteAsync(addedItem);

        Assert.Equal("SKU-021", viewModel.DisplayedCards.Last().ProductCode);
        Assert.Equal(21, viewModel.DisplayedCards.Last().Position);
        Assert.Equal(["SKU-021"], focusRequests);
    }

    [Fact]
    public async Task Offline_download_does_not_call_service()
    {
        var service = new FakeSpecialProductService();
        var viewModel = CreateViewModel(
            service: service,
            session: Session with { IsOnline = false });

        await viewModel.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(0, service.DownloadCallCount);
        Assert.Contains("online", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Online_download_calls_service_and_updates_progress()
    {
        var item = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var repository = new FakeCatalogRepository
        {
            SellableItems = [item],
            SpecialItems = [item]
        };
        var service = new FakeSpecialProductService
        {
            DownloadResult = new SpecialProductDownloadResult("S001", 1, 1, 1, 1, 0)
        };
        var viewModel = CreateViewModel(repository: repository, service: service);

        await viewModel.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(1, service.DownloadCallCount);
        Assert.True(viewModel.IsDownloadProgressVisible);
        Assert.Equal(100d, viewModel.DownloadProgressValue);
        Assert.Contains("Downloaded", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Online_download_hides_progress_after_completion_delay()
    {
        var item = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var repository = new FakeCatalogRepository
        {
            SellableItems = [item],
            SpecialItems = [item]
        };
        var hideDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeSpecialProductService
        {
            DownloadResult = new SpecialProductDownloadResult("S001", 1, 1, 1, 1, 0)
        };
        var viewModel = CreateViewModel(
            repository: repository,
            service: service,
            delayAsync: (_, cancellationToken) => hideDelay.Task.WaitAsync(cancellationToken));

        await viewModel.DownloadCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsDownloadProgressVisible);
        hideDelay.SetResult();
        await WaitUntilAsync(() => !viewModel.IsDownloadProgressVisible);
        Assert.False(viewModel.IsDownloadProgressVisible);
    }

    // ================= 卡片角标、购物车联动、筛选 =================

    [Fact]
    public async Task Cards_show_cart_quantity_and_follow_cart_changes()
    {
        var cart = new PosCartService();
        var alpha = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var beta = CreateItem("SKU-002", "Beta", "930002", isSpecialProduct: true);
        var repository = new FakeCatalogRepository { SpecialItems = [alpha, beta] };
        var viewModel = CreateViewModel(cart: cart, repository: repository);
        await viewModel.LoadAsync();
        var alphaCard = viewModel.DisplayedCards.Single(card => card.ProductCode == "SKU-001");
        Assert.False(alphaCard.IsInCart);

        var line = cart.AddItem(alpha);
        cart.AddItem(alpha);

        Assert.True(alphaCard.IsInCart);
        Assert.Equal(2m, alphaCard.CartQuantity);
        Assert.Equal("×2", alphaCard.CartQuantityText);
        Assert.False(viewModel.DisplayedCards.Single(card => card.ProductCode == "SKU-002").IsInCart);
        Assert.Equal(1, viewModel.InCartCount);

        cart.RemoveLine(line);

        Assert.False(alphaCard.IsInCart);
        Assert.Equal(0, viewModel.InCartCount);
    }

    [Fact]
    public async Task FilterText_filters_cards_by_name_item_number_and_lookup_code()
    {
        var alpha = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var beta = CreateItem("SKU-002", "Beta", "930002", isSpecialProduct: true);
        var viewModel = CreateViewModel(repository: new FakeCatalogRepository { SpecialItems = [alpha, beta] });
        await viewModel.LoadAsync();

        viewModel.FilterText = "beta";
        Assert.Equal(["SKU-002"], viewModel.DisplayedCards.Select(card => card.ProductCode));

        viewModel.FilterText = "930001";
        Assert.Equal(["SKU-001"], viewModel.DisplayedCards.Select(card => card.ProductCode));

        viewModel.FilterText = "sku-002";
        Assert.Equal(["SKU-002"], viewModel.DisplayedCards.Select(card => card.ProductCode));

        viewModel.FilterText = "no-such-product";
        Assert.Empty(viewModel.DisplayedCards);
        Assert.True(viewModel.IsFilterEmpty);
        Assert.False(viewModel.IsSpecialListEmpty);

        viewModel.FilterText = string.Empty;
        Assert.Equal(2, viewModel.DisplayedCards.Count);
        Assert.False(viewModel.IsFilterEmpty);
    }

    [Fact]
    public async Task Cart_filter_shows_only_products_in_cart_and_tracks_cart_changes()
    {
        var cart = new PosCartService();
        var alpha = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var beta = CreateItem("SKU-002", "Beta", "930002", isSpecialProduct: true);
        var viewModel = CreateViewModel(
            cart: cart,
            repository: new FakeCatalogRepository { SpecialItems = [alpha, beta] });
        await viewModel.LoadAsync();
        var line = cart.AddItem(alpha);

        viewModel.ShowInCartCommand.Execute(null);

        Assert.True(viewModel.IsCartFilterActive);
        Assert.Equal(["SKU-001"], viewModel.DisplayedCards.Select(card => card.ProductCode));
        Assert.Contains("1", viewModel.InCartChipText, StringComparison.Ordinal);

        // 筛选开着时购物车清空，列表随之为空，而不是保留过期卡片。
        cart.RemoveLine(line);
        Assert.Empty(viewModel.DisplayedCards);

        viewModel.ShowAllCommand.Execute(null);
        Assert.False(viewModel.IsCartFilterActive);
        Assert.Equal(2, viewModel.DisplayedCards.Count);
    }

    [Fact]
    public async Task Cart_summary_lists_latest_lines_total_and_empty_state()
    {
        var cart = new PosCartService();
        var alpha = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var beta = CreateItem("SKU-002", "Beta", "930002", isSpecialProduct: true);
        var viewModel = CreateViewModel(
            cart: cart,
            repository: new FakeCatalogRepository { SpecialItems = [alpha, beta] });
        await viewModel.LoadAsync();
        Assert.True(viewModel.IsCartEmpty);
        Assert.Empty(viewModel.CartSummaryLines);

        cart.AddItem(alpha);
        cart.AddItem(beta);

        Assert.False(viewModel.IsCartEmpty);
        Assert.True(viewModel.HasCartLines);
        // 摘要按最近加入在前。
        Assert.Equal(["Beta", "Alpha"], viewModel.CartSummaryLines.Select(line => line.DisplayName));
        Assert.Equal("$2.50", viewModel.CartTotalText);
        Assert.Equal("$1.25", viewModel.CartSummaryLines[0].AmountText);
        Assert.Contains("2", viewModel.CartCountText, StringComparison.Ordinal);

        cart.Clear();

        Assert.True(viewModel.IsCartEmpty);
        Assert.Empty(viewModel.CartSummaryLines);
        Assert.Equal("$0.00", viewModel.CartTotalText);
    }

    // ================= 加购提示与撤销 =================

    [Fact]
    public async Task AddToCart_shows_toast_with_undo_and_highlights_the_card()
    {
        var cart = new PosCartService();
        var item = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var viewModel = CreateViewModel(cart: cart, repository: new FakeCatalogRepository { SpecialItems = [item] });
        await viewModel.LoadAsync();

        viewModel.AddToCartCommand.Execute(item);

        Assert.True(viewModel.IsToastVisible);
        Assert.True(viewModel.CanUndoToast);
        Assert.Contains("Alpha", viewModel.ToastText, StringComparison.Ordinal);
        Assert.True(viewModel.UndoAddCommand.CanExecute(null));
        Assert.True(viewModel.DisplayedCards.Single().IsJustAdded);

        viewModel.DismissToastCommand.Execute(null);
        Assert.False(viewModel.IsToastVisible);
        Assert.False(viewModel.CanUndoToast);
    }

    [Fact]
    public async Task Toast_and_highlight_clear_after_their_delays()
    {
        var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var viewModel = CreateViewModel(
            repository: new FakeCatalogRepository { SpecialItems = [item] },
            delayAsync: (_, cancellationToken) => delay.Task.WaitAsync(cancellationToken));
        await viewModel.LoadAsync();

        viewModel.AddToCartCommand.Execute(item);
        Assert.True(viewModel.IsToastVisible);

        delay.SetResult();
        await WaitUntilAsync(() => !viewModel.IsToastVisible);
        await WaitUntilAsync(() => !viewModel.DisplayedCards.Single().IsJustAdded);
        Assert.False(viewModel.IsToastVisible);
    }

    [Fact]
    public async Task UndoAdd_reduces_quantity_when_line_holds_more_than_the_last_add()
    {
        var cart = new PosCartService();
        var logger = new RecordingOperationAuditLogger();
        var item = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var viewModel = CreateViewModel(
            cart: cart,
            repository: new FakeCatalogRepository { SpecialItems = [item] },
            operationAuditLogger: logger);
        await viewModel.LoadAsync();
        viewModel.AddToCartCommand.Execute(item);
        viewModel.AddToCartCommand.Execute(item);

        viewModel.UndoAddCommand.Execute(null);

        Assert.Equal(1m, Assert.Single(cart.Lines).Quantity);
        Assert.False(viewModel.IsToastVisible);
        Assert.False(viewModel.UndoAddCommand.CanExecute(null));
        var undoEvent = logger.Events.Last();
        Assert.Equal("CART_ITEM_QUANTITY_CHANGE", undoEvent.OperationType);
        Assert.Equal("special-products-undo", undoEvent.ReasonCode);
        Assert.Contains("Alpha", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UndoAdd_removes_the_line_when_it_was_the_only_unit_and_skips_reveal()
    {
        var cart = new PosCartService();
        var logger = new RecordingOperationAuditLogger();
        var revealed = new List<CartLine>();
        var item = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var viewModel = CreateViewModel(
            cart: cart,
            repository: new FakeCatalogRepository { SpecialItems = [item] },
            onCartLineAdded: revealed.Add,
            operationAuditLogger: logger);
        await viewModel.LoadAsync();
        viewModel.AddToCartCommand.Execute(item);

        viewModel.UndoAddCommand.Execute(null);
        viewModel.BackCommand.Execute(null);

        Assert.Empty(cart.Lines);
        Assert.Equal("CART_ITEM_REMOVE", logger.Events.Last().OperationType);
        // 撤销后购物车里没有这一行，返回收银时不应再定位。
        Assert.Empty(revealed);
    }

    [Fact]
    public async Task UndoAdd_is_a_noop_when_the_cart_line_is_already_gone()
    {
        var cart = new PosCartService();
        var logger = new RecordingOperationAuditLogger();
        var item = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var viewModel = CreateViewModel(
            cart: cart,
            repository: new FakeCatalogRepository { SpecialItems = [item] },
            operationAuditLogger: logger);
        await viewModel.LoadAsync();
        viewModel.AddToCartCommand.Execute(item);
        var auditCountBefore = logger.Events.Count;
        cart.RemoveLine(Assert.Single(cart.Lines));

        viewModel.UndoAddCommand.Execute(null);

        Assert.Equal(auditCountBefore, logger.Events.Count);
        Assert.False(viewModel.IsToastVisible);
        Assert.Empty(cart.Lines);
    }

    // ================= 编辑模式：选中位置与移除确认 =================

    [Fact]
    public async Task Edit_mode_selection_reports_position_and_marks_only_the_selected_card()
    {
        var viewModel = CreateViewModel(repository: new FakeCatalogRepository { SpecialItems = CreateSpecialItems(3) });
        await viewModel.LoadAsync();
        viewModel.ToggleEditModeCommand.Execute(null);

        viewModel.SpecialItemCardCommand.Execute(viewModel.SpecialItems[1]);

        Assert.Contains("Item 002", viewModel.SelectedPositionText, StringComparison.Ordinal);
        Assert.Contains("2", viewModel.SelectedPositionText, StringComparison.Ordinal);
        Assert.Equal(["SKU-002"], viewModel.DisplayedCards.Where(card => card.IsSelected).Select(card => card.ProductCode));
        Assert.Equal([1, 2, 3], viewModel.DisplayedCards.Select(card => card.Position));
    }

    [Fact]
    public async Task BeginRemove_asks_for_confirmation_on_the_card_and_cancel_clears_it()
    {
        var viewModel = CreateViewModel(repository: new FakeCatalogRepository { SpecialItems = CreateSpecialItems(2) });
        await viewModel.LoadAsync();
        viewModel.ToggleEditModeCommand.Execute(null);
        var first = viewModel.SpecialItems[0];
        var second = viewModel.SpecialItems[1];
        viewModel.SpecialItemCardCommand.Execute(first);

        Assert.True(viewModel.BeginRemoveCommand.CanExecute(first));
        viewModel.BeginRemoveCommand.Execute(first);

        Assert.True(viewModel.DisplayedCards[0].IsRemoveConfirming);
        Assert.False(viewModel.DisplayedCards[1].IsRemoveConfirming);
        Assert.Contains("Item 001", viewModel.RemoveConfirmPromptText, StringComparison.Ordinal);

        viewModel.CancelRemoveCommand.Execute(first);
        Assert.All(viewModel.DisplayedCards, card => Assert.False(card.IsRemoveConfirming));

        // 确认态跟随选中：换选另一张后，上一张的确认提示必须收起。
        viewModel.BeginRemoveCommand.Execute(first);
        viewModel.SpecialItemCardCommand.Execute(second);
        Assert.All(viewModel.DisplayedCards, card => Assert.False(card.IsRemoveConfirming));
    }

    // ================= 改价（必须在线）=================

    private static async Task<(SpecialProductsViewModel ViewModel, FakeSpecialProductsWorkflowService Workflow, RecordingOperationAuditLogger Logger, SellableItemDto Item, PosCartService Cart)>
        CreatePriceEditorScenarioAsync(
            bool online = true,
            Exception? priceException = null,
            Func<string, string, decimal, SpecialProductsPriceWorkflowResult>? priceResult = null)
    {
        var cart = new PosCartService();
        var logger = new RecordingOperationAuditLogger();
        var item = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var workflow = new FakeSpecialProductsWorkflowService
        {
            LoadResult = new SpecialProductsLoadResult("S001", [item]),
            PriceException = priceException,
            PriceResultOverride = priceResult ?? ((store, product, price) => new SpecialProductsPriceWorkflowResult(
                store,
                product,
                price,
                [item with { RetailPrice = price }]))
        };
        var viewModel = CreateViewModel(
            cart: cart,
            workflow: workflow,
            session: Session with { IsOnline = online },
            operationAuditLogger: logger);
        await viewModel.LoadAsync();
        viewModel.ToggleEditModeCommand.Execute(null);
        viewModel.SpecialItemCardCommand.Execute(item);
        return (viewModel, workflow, logger, item, cart);
    }

    [Fact]
    public async Task OpenPriceEditor_is_disabled_offline_and_never_opens()
    {
        var (viewModel, workflow, _, item, _) = await CreatePriceEditorScenarioAsync(online: false);

        Assert.False(viewModel.OpenPriceEditorCommand.CanExecute(item));
        viewModel.OpenPriceEditorCommand.Execute(item);

        Assert.False(viewModel.IsPriceEditorOpen);
        Assert.Equal(0, workflow.PriceCallCount);
    }

    [Fact]
    public async Task OpenPriceEditor_requires_edit_mode()
    {
        var item = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var viewModel = CreateViewModel(workflow: new FakeSpecialProductsWorkflowService
        {
            LoadResult = new SpecialProductsLoadResult("S001", [item])
        });
        await viewModel.LoadAsync();

        Assert.False(viewModel.OpenPriceEditorCommand.CanExecute(item));
        viewModel.OpenPriceEditorCommand.Execute(item);

        Assert.False(viewModel.IsPriceEditorOpen);
    }

    [Fact]
    public async Task OpenPriceEditor_prefills_current_price_and_shows_product()
    {
        var (viewModel, _, _, item, _) = await CreatePriceEditorScenarioAsync();

        viewModel.OpenPriceEditorCommand.Execute(item);

        Assert.True(viewModel.IsPriceEditorOpen);
        Assert.Equal("Alpha", viewModel.PriceEditorProductName);
        Assert.Equal("$1.25", viewModel.PriceEditorCurrentPriceText);
        Assert.Equal("1.25", viewModel.PriceEditorInputText);
        Assert.Equal("$1.25", viewModel.PriceEditorNewPriceText);
        // 价格没变不能保存。
        Assert.False(viewModel.SavePriceCommand.CanExecute(null));
    }

    [Fact]
    public async Task Price_keypad_replaces_prefill_then_enforces_digit_and_decimal_limits()
    {
        var (viewModel, _, _, item, _) = await CreatePriceEditorScenarioAsync();
        viewModel.OpenPriceEditorCommand.Execute(item);

        viewModel.PriceKeyCommand.Execute("5");
        Assert.Equal("5", viewModel.PriceEditorInputText);

        viewModel.PriceKeyCommand.Execute(".");
        viewModel.PriceKeyCommand.Execute(".");
        Assert.Equal("5.", viewModel.PriceEditorInputText);

        viewModel.PriceKeyCommand.Execute("9");
        viewModel.PriceKeyCommand.Execute("9");
        viewModel.PriceKeyCommand.Execute("9");
        Assert.Equal("5.99", viewModel.PriceEditorInputText);
        Assert.Equal("$5.99", viewModel.PriceEditorNewPriceText);
        Assert.True(viewModel.SavePriceCommand.CanExecute(null));

        viewModel.PriceBackspaceCommand.Execute(null);
        Assert.Equal("5.9", viewModel.PriceEditorInputText);

        viewModel.PriceClearCommand.Execute(null);
        Assert.Equal(string.Empty, viewModel.PriceEditorInputText);
        Assert.False(viewModel.SavePriceCommand.CanExecute(null));

        // 小数点开头补 0；前导 0 后接数字会被替换；整数最多 5 位。
        viewModel.PriceKeyCommand.Execute(".");
        Assert.Equal("0.", viewModel.PriceEditorInputText);
        viewModel.PriceClearCommand.Execute(null);
        viewModel.PriceKeyCommand.Execute("0");
        viewModel.PriceKeyCommand.Execute("7");
        Assert.Equal("7", viewModel.PriceEditorInputText);
        foreach (var digit in new[] { "1", "2", "3", "4", "5" })
        {
            viewModel.PriceKeyCommand.Execute(digit);
        }

        Assert.Equal("71234", viewModel.PriceEditorInputText);
    }

    [Fact]
    public async Task SavePrice_calls_workflow_refreshes_cards_audits_and_closes_the_editor()
    {
        var (viewModel, workflow, logger, item, _) = await CreatePriceEditorScenarioAsync();
        viewModel.OpenPriceEditorCommand.Execute(item);
        viewModel.PriceKeyCommand.Execute("5");
        viewModel.PriceKeyCommand.Execute(".");
        viewModel.PriceKeyCommand.Execute("9");
        viewModel.PriceKeyCommand.Execute("9");

        await viewModel.SavePriceCommand.ExecuteAsync(null);

        Assert.Equal(1, workflow.PriceCallCount);
        Assert.Equal(("S001", "SKU-001", 5.99m), workflow.LastPriceCall);
        Assert.False(viewModel.IsPriceEditorOpen);
        Assert.Equal(5.99m, viewModel.DisplayedCards.Single().RetailPrice);
        Assert.Contains("$5.99", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.False(viewModel.IsBusy);

        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("SPECIAL_PRODUCT_PRICE_CHANGE", auditEvent.OperationType);
        Assert.Equal("Succeeded", auditEvent.Outcome);
        Assert.Equal("PRICE_CHANGE", auditEvent.ReasonCode);
        Assert.Equal(4.74m, auditEvent.AmountDelta);
        var auditItem = Assert.Single(auditEvent.Items);
        Assert.Equal("SKU-001", auditItem.ProductCode);
        Assert.Equal(1.25m, auditItem.BeforeUnitPrice);
        Assert.Equal(5.99m, auditItem.AfterUnitPrice);
    }

    [Fact]
    public async Task SavePrice_failure_keeps_editor_open_shows_error_and_audits_failure()
    {
        var (viewModel, workflow, logger, item, _) = await CreatePriceEditorScenarioAsync(
            priceException: new InvalidOperationException("network unavailable"));
        viewModel.OpenPriceEditorCommand.Execute(item);
        viewModel.PriceKeyCommand.Execute("8");

        await viewModel.SavePriceCommand.ExecuteAsync(null);

        Assert.Equal(1, workflow.PriceCallCount);
        Assert.True(viewModel.IsPriceEditorOpen);
        Assert.True(viewModel.HasPriceEditorError);
        Assert.Contains("network unavailable", viewModel.PriceEditorErrorText, StringComparison.Ordinal);
        Assert.Contains("network unavailable", viewModel.StatusMessage, StringComparison.Ordinal);
        // 失败时本地价格保持不变，可直接重试。
        Assert.Equal(1.25m, viewModel.DisplayedCards.Single().RetailPrice);
        Assert.False(viewModel.IsBusy);

        var auditEvent = Assert.Single(logger.Events);
        Assert.Equal("SPECIAL_PRODUCT_PRICE_CHANGE", auditEvent.OperationType);
        Assert.Equal("Failed", auditEvent.Outcome);
        Assert.Equal(0m, auditEvent.AmountDelta);
        Assert.Equal("InvalidOperationException", auditEvent.SafeMessage);
    }

    [Fact]
    public async Task SavePrice_rejects_unchanged_or_invalid_price_without_calling_backend()
    {
        var (viewModel, workflow, logger, item, _) = await CreatePriceEditorScenarioAsync();
        viewModel.OpenPriceEditorCommand.Execute(item);

        // 保存按钮在价格未变 / 非法时本就不可用；直接执行命令也不能绕过校验。
        await viewModel.SavePriceCommand.ExecuteAsync(null);
        viewModel.PriceClearCommand.Execute(null);
        await viewModel.SavePriceCommand.ExecuteAsync(null);
        viewModel.PriceKeyCommand.Execute("0");
        await viewModel.SavePriceCommand.ExecuteAsync(null);

        Assert.Equal(0, workflow.PriceCallCount);
        Assert.Empty(logger.Events);
        Assert.True(viewModel.IsPriceEditorOpen);
    }

    [Fact]
    public async Task SavePrice_when_product_is_in_cart_warns_that_the_cart_line_keeps_the_old_price()
    {
        var (viewModel, _, _, item, cart) = await CreatePriceEditorScenarioAsync();
        var line = cart.AddItem(item);
        viewModel.OpenPriceEditorCommand.Execute(item);
        viewModel.PriceKeyCommand.Execute("3");

        await viewModel.SavePriceCommand.ExecuteAsync(null);

        Assert.Equal(1.25m, line.UnitPrice);
        Assert.Equal(3m, viewModel.DisplayedCards.Single().RetailPrice);
        Assert.Contains("cart", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("old price", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SavePrice_goes_offline_after_opening_is_blocked()
    {
        var item = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var workflow = new FakeSpecialProductsWorkflowService
        {
            LoadResult = new SpecialProductsLoadResult("S001", [item])
        };
        var viewModel = CreateViewModel(workflow: workflow);
        await viewModel.LoadAsync();
        viewModel.ToggleEditModeCommand.Execute(null);
        viewModel.OpenPriceEditorCommand.Execute(item);
        viewModel.PriceKeyCommand.Execute("9");
        Assert.True(viewModel.SavePriceCommand.CanExecute(null));

        // 弹窗打开后网络掉线：保存必须被拦下，且不发请求。
        viewModel.Session = viewModel.Session with { IsOnline = false };

        Assert.False(viewModel.SavePriceCommand.CanExecute(null));
        await viewModel.SavePriceCommand.ExecuteAsync(null);
        Assert.Equal(0, workflow.PriceCallCount);
    }

    [Fact]
    public async Task Back_closes_the_price_editor()
    {
        var backCount = 0;
        var cart = new PosCartService();
        var item = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var workflow = new FakeSpecialProductsWorkflowService
        {
            LoadResult = new SpecialProductsLoadResult("S001", [item])
        };
        var viewModel = CreateViewModel(cart: cart, workflow: workflow, onBack: () => backCount++);
        await viewModel.LoadAsync();
        viewModel.ToggleEditModeCommand.Execute(null);
        viewModel.OpenPriceEditorCommand.Execute(item);
        Assert.True(viewModel.IsPriceEditorOpen);

        viewModel.BackCommand.Execute(null);

        Assert.False(viewModel.IsPriceEditorOpen);
        Assert.Equal(1, backCount);
    }

    private static SpecialProductsViewModel CreateViewModel(
        LocalSellableItemIndex? index = null,
        PosCartService? cart = null,
        FakeCatalogRepository? repository = null,
        FakeSpecialProductService? service = null,
        FakeSpecialProductsWorkflowService? workflow = null,
        PosSessionState? session = null,
        Action? onBack = null,
        Action<CartLine>? onCartLineAdded = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Func<IEnumerable<string?>, int, CancellationToken, Task<int>>? thumbnailPreloadAsync = null,
        IOperationAuditLogger? operationAuditLogger = null)
    {
        return new SpecialProductsViewModel(
            index ?? new LocalSellableItemIndex(),
            cart ?? new PosCartService(),
            repository ?? new FakeCatalogRepository(),
            service ?? new FakeSpecialProductService(),
            session ?? Session,
            new LocalizationService(),
            onBack ?? (() => { }),
            onCartLineAdded,
            workflow,
            delayAsync: delayAsync,
            thumbnailPreloadAsync: thumbnailPreloadAsync,
            operationAuditLogger: operationAuditLogger);
    }

    private sealed class RecordingOperationAuditLogger : IOperationAuditLogger
    {
        public List<OperationAuditEventDto> Events { get; } = [];

        public void Record(OperationAuditEventDto auditEvent)
        {
            Events.Add(auditEvent);
        }
    }

    private static IReadOnlyList<SellableItemDto> DisplayedItems(SpecialProductsViewModel viewModel) =>
        viewModel.DisplayedCards.Select(card => card.Item).ToArray();

    private static PosSessionState Session => new("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

    private static SellableItemDto CreateItem(
        string productCode,
        string displayName,
        string lookupCode,
        bool isSpecialProduct = false)
    {
        return new SellableItemDto(
            "S001",
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
            ProductImage: $"https://images.example/{productCode}.jpg",
            DiscountRate: null,
            IsSpecialProduct: isSpecialProduct);
    }

    private static SellableItemDto[] CreateSpecialItems(int count)
    {
        return Enumerable.Range(1, count)
            .Select(number => CreateItem(
                $"SKU-{number:000}",
                $"Item {number:000}",
                $"930{number:000}",
                isSpecialProduct: true))
            .ToArray();
    }

    private sealed class FakeCatalogRepository : ILocalCatalogRepository
    {
        public IReadOnlyList<SellableItemDto> SellableItems { get; init; } = [];

        public IReadOnlyList<SellableItemDto> SpecialItems { get; set; } = [];

        public List<string[]> SavedOrders { get; } = [];

        public int LoadSellableItemsCallCount { get; private set; }

        public int LoadSpecialProductItemsCallCount { get; private set; }

        public Func<Task>? BeforeLoadSpecialProductItemsAsync { get; init; }

        public Task ReplaceSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task UpsertSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<int> DeleteByLookupCodesAsync(string storeCode, IEnumerable<string> lookupCodes, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<SellableItemDto?> FindByLookupCodeAsync(string storeCode, string lookupCode, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SellableItemDto?>(null);
        }

        public Task<IReadOnlyList<SellableItemDto>> LoadSpecialProductItemsAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            LoadSpecialProductItemsCallCount++;
            if (BeforeLoadSpecialProductItemsAsync is not null)
            {
                return LoadSpecialProductItemsCoreAsync();
            }

            return Task.FromResult(SpecialItems);

            async Task<IReadOnlyList<SellableItemDto>> LoadSpecialProductItemsCoreAsync()
            {
                await BeforeLoadSpecialProductItemsAsync();
                return SpecialItems;
            }
        }

        public Task SaveSpecialProductOrderAsync(
            string storeCode,
            IEnumerable<string> productCodes,
            CancellationToken cancellationToken = default)
        {
            SavedOrders.Add(productCodes.ToArray());
            return Task.CompletedTask;
        }

        public Task<int> UpdateSpecialProductFlagAsync(
            string storeCode,
            string productCode,
            bool isSpecialProduct,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<int> ClearSpecialProductFlagsExceptAsync(
            string storeCode,
            IEnumerable<string> productCodesToKeep,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<IReadOnlyList<LocalSellableItemCompareRow>> LoadSellableItemComparePageAsync(
            string storeCode,
            string? afterLookupCodeNormalized,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalSellableItemCompareRow>>([]);
        }

        public Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(CancellationToken cancellationToken = default)
        {
            LoadSellableItemsCallCount++;
            return Task.FromResult(SellableItems);
        }

        public Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(string storeCode, CancellationToken cancellationToken = default)
        {
            LoadSellableItemsCallCount++;
            return Task.FromResult<IReadOnlyList<SellableItemDto>>(
                SellableItems.Where(item => string.Equals(item.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase)).ToArray());
        }
    }

    private sealed class FakeSpecialProductService : ISpecialProductService
    {
        public int CallCount { get; private set; }

        public int DownloadCallCount { get; private set; }

        public (string StoreCode, string ProductCode, bool IsSpecialProduct)? LastCall { get; private set; }

        public SpecialProductDownloadResult DownloadResult { get; init; } =
            new("S001", 0, 0, 0, 0, 0);

        public Action<string, bool>? OnMark { get; init; }

        public Func<string, bool, SpecialProductMarkResult>? MarkResultFactory { get; init; }

        public int PriceCallCount { get; private set; }

        public (string StoreCode, string ProductCode, decimal RetailPrice)? LastPriceCall { get; private set; }

        public Func<string, decimal, SpecialProductMarkResult>? PriceResultFactory { get; init; }

        public Exception? PriceException { get; init; }

        public Task<SpecialProductMarkResult> UpdateSpecialProductPriceAsync(
            string storeCode,
            string productCode,
            decimal retailPrice,
            CancellationToken cancellationToken = default)
        {
            PriceCallCount++;
            LastPriceCall = (storeCode, productCode, retailPrice);
            if (PriceException is not null)
            {
                return Task.FromException<SpecialProductMarkResult>(PriceException);
            }

            return Task.FromResult(PriceResultFactory?.Invoke(productCode, retailPrice) ?? new SpecialProductMarkResult([], []));
        }

        public Task<SpecialProductMarkResult> MarkSpecialProductAsync(
            string storeCode,
            string productCode,
            bool isSpecialProduct,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastCall = (storeCode, productCode, isSpecialProduct);
            OnMark?.Invoke(productCode, isSpecialProduct);
            return Task.FromResult(MarkResultFactory?.Invoke(productCode, isSpecialProduct) ?? new SpecialProductMarkResult([], []));
        }

        public Task<SpecialProductDownloadResult> DownloadSpecialProductsAsync(
            string storeCode,
            CancellationToken cancellationToken = default,
            IProgress<SpecialProductDownloadProgress>? progress = null)
        {
            DownloadCallCount++;
            progress?.Report(new SpecialProductDownloadProgress(
                storeCode,
                SpecialProductDownloadProgressStage.Downloading,
                1,
                1,
                100,
                1,
                1,
                0,
                10));
            progress?.Report(new SpecialProductDownloadProgress(
                storeCode,
                SpecialProductDownloadProgressStage.Completed,
                DownloadResult.TotalCount,
                DownloadResult.DownloadedCount,
                100,
                DownloadResult.PageCount,
                DownloadResult.UpsertedCount,
                DownloadResult.UnmarkedCount,
                20));
            return Task.FromResult(DownloadResult);
        }
    }

    private sealed class FakeSpecialProductsWorkflowService : ISpecialProductsWorkflowService
    {
        public int PreloadCallCount { get; private set; }

        public int EnsureLoadedCallCount { get; private set; }

        public int LoadCallCount { get; private set; }

        public int MarkCallCount { get; private set; }

        public int SearchCallCount { get; private set; }

        public string? LastSearchText { get; private set; }

        public (string StoreCode, string ProductCode, bool IsSpecialProduct)? LastMarkCall { get; private set; }

        public SpecialProductsLoadResult PreloadResult { get; init; } = new("S001", []);

        public SpecialProductsLoadResult EnsureLoadedResult { get; init; } = new("S001", []);

        public SpecialProductsLoadResult LoadResult { get; init; } = new("S001", []);

        public IReadOnlyList<SellableItemDto> SearchItems { get; init; } = [];

        public SpecialProductsDownloadWorkflowResult DownloadResult { get; init; } =
            new(new SpecialProductDownloadResult("S001", 0, 0, 0, 0, 0), []);

        public Func<CancellationToken, Task<SpecialProductsDownloadWorkflowResult>>? DownloadAsyncOverride { get; init; }

        public Func<bool, CancellationToken, Task<SpecialProductsMutationWorkflowResult>>? MarkAsyncOverride { get; init; }

        public Func<string, string, CancellationToken, Task<SpecialProductsSearchResult>>? SearchAsyncOverride { get; init; }

        public Func<string, string, bool, SpecialProductsMutationWorkflowResult> MarkResultFactory { get; init; } =
            (storeCode, productCode, isSpecialProduct) => new SpecialProductsMutationWorkflowResult(
                storeCode,
                productCode,
                isSpecialProduct,
                []);

        public int PriceCallCount { get; private set; }

        public (string StoreCode, string ProductCode, decimal RetailPrice)? LastPriceCall { get; private set; }

        public Func<string, string, decimal, SpecialProductsPriceWorkflowResult> PriceResultFactory { get; init; } =
            (storeCode, productCode, retailPrice) => new SpecialProductsPriceWorkflowResult(
                storeCode,
                productCode,
                retailPrice,
                []);

        public Exception? PriceException { get; init; }

        public Func<string, string, decimal, SpecialProductsPriceWorkflowResult>? PriceResultOverride { get; init; }

        public Func<string, IReadOnlyList<SellableItemDto>, string, int, SpecialProductsReorderWorkflowResult?> ReorderResultFactory { get; init; } =
            (storeCode, items, productCode, _) => new SpecialProductsReorderWorkflowResult(storeCode, items, productCode);

        // 非空时 AddToCart 抛出该异常，用来模拟加购失败。
        public Exception? AddToCartException { get; set; }

        public SpecialProductsAddToCartResult AddToCart(SellableItemDto item)
        {
            if (AddToCartException is not null)
            {
                throw AddToCartException;
            }

            return new SpecialProductsAddToCartResult(new CartLine(item), 1);
        }

        public Task<SpecialProductsLoadResult> PreloadAsync(string storeCode, CancellationToken cancellationToken = default)
        {
            PreloadCallCount++;
            return Task.FromResult(PreloadResult);
        }

        public Task<SpecialProductsLoadResult> EnsureLoadedAsync(string storeCode, CancellationToken cancellationToken = default)
        {
            EnsureLoadedCallCount++;
            return Task.FromResult(EnsureLoadedResult);
        }

        public Task<SpecialProductsLoadResult> LoadAsync(string storeCode, CancellationToken cancellationToken = default)
        {
            LoadCallCount++;
            return Task.FromResult(LoadResult);
        }

        public SpecialProductsSearchResult Search(string storeCode, string searchText)
        {
            SearchCallCount++;
            LastSearchText = searchText;
            return new SpecialProductsSearchResult(storeCode, searchText, SearchItems);
        }

        public Task<SpecialProductsSearchResult> SearchAsync(
            string storeCode,
            string searchText,
            CancellationToken cancellationToken = default)
        {
            SearchCallCount++;
            LastSearchText = searchText;
            return SearchAsyncOverride?.Invoke(storeCode, searchText, cancellationToken)
                ?? Task.FromResult(new SpecialProductsSearchResult(storeCode, searchText, SearchItems));
        }

        public Task<SpecialProductsDownloadWorkflowResult> DownloadAsync(
            string storeCode,
            CancellationToken cancellationToken = default,
            IProgress<SpecialProductDownloadProgress>? progress = null,
            Action<IReadOnlyList<SellableItemDto>>? onSpecialItemsReady = null)
        {
            if (DownloadAsyncOverride is not null)
            {
                return DownloadAsyncOverride(cancellationToken);
            }

            return Task.FromResult(DownloadResult);
        }

        public Task<SpecialProductsMutationWorkflowResult> MarkSpecialProductAsync(
            string storeCode,
            string productCode,
            bool isSpecialProduct,
            CancellationToken cancellationToken = default)
        {
            MarkCallCount++;
            LastMarkCall = (storeCode, productCode, isSpecialProduct);
            if (MarkAsyncOverride is not null)
            {
                return MarkAsyncOverride(isSpecialProduct, cancellationToken);
            }

            return Task.FromResult(MarkResultFactory(storeCode, productCode, isSpecialProduct));
        }

        public Task<SpecialProductsPriceWorkflowResult> UpdateSpecialProductPriceAsync(
            string storeCode,
            string productCode,
            decimal retailPrice,
            CancellationToken cancellationToken = default)
        {
            PriceCallCount++;
            LastPriceCall = (storeCode, productCode, retailPrice);
            if (PriceException is not null)
            {
                return Task.FromException<SpecialProductsPriceWorkflowResult>(PriceException);
            }

            return Task.FromResult((PriceResultOverride ?? PriceResultFactory)(storeCode, productCode, retailPrice));
        }

        public Task<SpecialProductsReorderWorkflowResult?> ReorderAsync(
            string storeCode,
            IReadOnlyList<SellableItemDto> currentItems,
            string productCode,
            int delta,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ReorderResultFactory(storeCode, currentItems, productCode, delta));
        }
    }

    private static IDisposable CaptureClientLog(ConcurrentQueue<string> lines)
    {
        void Capture(string line)
        {
            lines.Enqueue(line);
        }

        ConsoleLog.LineWritten += Capture;
        return new DisposableAction(() => ConsoleLog.LineWritten -= Capture);
    }

    private static bool HasLog(ConcurrentQueue<string> lines, string text)
    {
        return lines.Any(line => line.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class DisposableAction(Action dispose) : IDisposable
    {
        public void Dispose()
        {
            dispose();
        }
    }
}
