using System.Collections.Concurrent;
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
    [InlineData("download", "back")]
    [InlineData("add", "back")]
    [InlineData("remove", "back")]
    [InlineData("download", "add-to-cart")]
    public async Task Navigation_cancels_running_remote_operation_before_leaving(string operation, string navigation)
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

        Task navigationExecution = Task.CompletedTask;
        if (navigation == "back")
        {
            Assert.True(viewModel.BackCommand.CanExecute(null));
            viewModel.BackCommand.Execute(null);
        }
        else
        {
            Assert.True(viewModel.AddToCartCommand.CanExecute(item));
            navigationExecution = Assert.IsAssignableFrom<IAsyncRelayCommand<SellableItemDto>>(viewModel.AddToCartCommand)
                .ExecuteAsync(item);
        }
        await WaitUntilAsync(() => navigated);
        await cancellationObserved.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        var wasCancellationRequested = receivedToken.IsCancellationRequested;
        await Task.WhenAll(execution, navigationExecution).WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
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
        Assert.Equal(1, backCallCount);
        Assert.Equal(1, repository.LoadSpecialProductItemsCallCount);
    }

    [Fact]
    public async Task SpecialItemCardCommand_normal_mode_adds_tapped_special_product()
    {
        var cart = new PosCartService();
        var backCallCount = 0;
        var item = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var repository = new FakeCatalogRepository { SpecialItems = [item] };
        var viewModel = CreateViewModel(cart: cart, repository: repository, onBack: () => backCallCount++);
        await viewModel.LoadAsync();

        viewModel.SpecialItemCardCommand.Execute(item);

        var line = Assert.Single(cart.Lines);
        Assert.Equal("SKU-001", line.ProductCode);
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
    public async Task AddToCartCommand_reports_added_cart_line_for_reveal()
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

        var line = Assert.Single(cart.Lines);
        Assert.Same(line, revealedLine);
    }

    [Fact]
    public async Task AddToCartCommand_reports_existing_cart_line_when_quantity_increases()
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

        var line = Assert.Single(cart.Lines);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(2, revealedLines.Count);
        Assert.All(revealedLines, revealedLine => Assert.Same(line, revealedLine));
    }

    [Fact]
    public async Task PreloadAsync_loads_special_products_and_pages_first_twenty()
    {
        var repository = new FakeCatalogRepository { SpecialItems = CreateSpecialItems(21) };
        var viewModel = CreateViewModel(repository: repository);

        await viewModel.PreloadAsync();

        Assert.Equal(1, repository.LoadSpecialProductItemsCallCount);
        Assert.Equal(20, viewModel.PagedSpecialItems.Count);
        Assert.Equal(2, viewModel.TotalPages);
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
        Assert.Equal(20, viewModel.PagedSpecialItems.Count);
        Assert.Equal(2, viewModel.TotalPages);
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
        Assert.Single(viewModel.PagedSpecialItems);
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
        Assert.Equal(20, viewModel.PagedSpecialItems.Count);
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
        Assert.Single(viewModel.PagedSpecialItems);
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
    public async Task LoadAsync_with_more_than_twenty_items_pages_the_special_product_list()
    {
        var repository = new FakeCatalogRepository { SpecialItems = CreateSpecialItems(21) };
        var viewModel = CreateViewModel(repository: repository);

        await viewModel.LoadAsync();

        Assert.Equal(1, viewModel.CurrentPage);
        Assert.Equal(2, viewModel.TotalPages);
        Assert.Equal(
            Enumerable.Range(1, 20).Select(number => $"SKU-{number:000}"),
            viewModel.PagedSpecialItems.Select(item => item.ProductCode));

        viewModel.NextPageCommand.Execute(null);

        Assert.Equal(2, viewModel.CurrentPage);
        Assert.Equal("SKU-021", Assert.Single(viewModel.PagedSpecialItems).ProductCode);

        viewModel.PreviousPageCommand.Execute(null);

        Assert.Equal(1, viewModel.CurrentPage);
        Assert.Equal("SKU-001", viewModel.PagedSpecialItems.First().ProductCode);
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
    public async Task ActivateForEntry_resets_page_edit_search_and_selection()
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
        viewModel.NextPageCommand.Execute(null);
        viewModel.ToggleEditModeCommand.Execute(null);
        viewModel.SearchText = "930999";
        viewModel.SearchCommand.Execute(null);
        viewModel.SpecialItemCardCommand.Execute(viewModel.PagedSpecialItems.Single());

        viewModel.ActivateForEntry();

        Assert.Equal(1, viewModel.CurrentPage);
        Assert.False(viewModel.IsEditMode);
        Assert.Empty(viewModel.SearchText);
        Assert.Empty(viewModel.SearchResults);
        Assert.Null(viewModel.SelectedSearchResult);
        Assert.Null(viewModel.SelectedSpecialItem);
        Assert.Equal("SKU-001", viewModel.PagedSpecialItems.First().ProductCode);
    }

    [Fact]
    public async Task ActivateForEntry_after_edit_add_returns_to_first_page_normal_mode()
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

        Assert.Equal(1, viewModel.CurrentPage);
        Assert.False(viewModel.IsEditMode);
        Assert.Equal(2, viewModel.TotalPages);
        Assert.Equal("SKU-001", viewModel.PagedSpecialItems.First().ProductCode);
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
        viewModel.PagedSpecialItems.CollectionChanged += (_, _) => collectionChangeCount++;

        viewModel.ActivateForEntry();

        Assert.Equal(0, delayCallCount);
        Assert.True(viewModel.AreThumbnailsEnabled);
        Assert.Equal(0, collectionChangeCount);
        Assert.Equal(20, viewModel.PagedSpecialItems.Count);
        Assert.Equal("SKU-001", viewModel.PagedSpecialItems.First().ProductCode);
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
    public async Task MoveDown_across_page_boundary_keeps_moved_item_visible_on_destination_page()
    {
        var repository = new FakeCatalogRepository { SpecialItems = CreateSpecialItems(21) };
        var viewModel = CreateViewModel(repository: repository);
        await viewModel.LoadAsync();
        viewModel.ToggleEditModeCommand.Execute(null);
        var lastItemOnFirstPage = viewModel.PagedSpecialItems.Last();

        await viewModel.MoveDownCommand.ExecuteAsync(lastItemOnFirstPage);

        Assert.Equal(2, viewModel.CurrentPage);
        Assert.Equal("SKU-020", Assert.Single(viewModel.PagedSpecialItems).ProductCode);
        Assert.Equal(
            ["SKU-019", "SKU-021", "SKU-020"],
            Assert.Single(repository.SavedOrders).Skip(18).ToArray());
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
    public async Task Failed_remove_preserves_special_list_page_and_selection_with_error_status()
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
        var selectedItem = viewModel.PagedSpecialItems.Last();
        viewModel.SpecialItemCardCommand.Execute(selectedItem);
        var specialItemCodes = viewModel.SpecialItems.Select(item => item.ProductCode).ToArray();
        var pagedItemCodes = viewModel.PagedSpecialItems.Select(item => item.ProductCode).ToArray();

        await viewModel.RemoveSpecialProductCommand.ExecuteAsync(selectedItem);

        Assert.Equal(specialItemCodes, viewModel.SpecialItems.Select(item => item.ProductCode));
        Assert.Equal(pagedItemCodes, viewModel.PagedSpecialItems.Select(item => item.ProductCode));
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
    public async Task Remove_last_item_on_last_page_returns_to_previous_page()
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
        viewModel.NextPageCommand.Execute(null);
        var lastPageItem = Assert.Single(viewModel.PagedSpecialItems);
        viewModel.SpecialItemCardCommand.Execute(lastPageItem);

        await viewModel.RemoveSpecialProductCommand.ExecuteAsync(lastPageItem);

        Assert.Equal(1, viewModel.CurrentPage);
        Assert.Equal(1, viewModel.TotalPages);
        Assert.Equal(20, viewModel.PagedSpecialItems.Count());
        Assert.DoesNotContain(viewModel.PagedSpecialItems, item => item.ProductCode == "SKU-021");
        Assert.Null(viewModel.SelectedSpecialItem);
    }

    [Fact]
    public async Task Add_item_that_creates_new_page_navigates_to_the_added_item()
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

        Assert.Equal(2, viewModel.CurrentPage);
        Assert.Equal(2, viewModel.TotalPages);
        Assert.Equal("SKU-021", Assert.Single(viewModel.PagedSpecialItems).ProductCode);
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
        Func<IEnumerable<string?>, int, CancellationToken, Task<int>>? thumbnailPreloadAsync = null)
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
            thumbnailPreloadAsync: thumbnailPreloadAsync);
    }

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

        public Func<string, IReadOnlyList<SellableItemDto>, string, int, SpecialProductsReorderWorkflowResult?> ReorderResultFactory { get; init; } =
            (storeCode, items, productCode, _) => new SpecialProductsReorderWorkflowResult(storeCode, items, productCode);

        public SpecialProductsAddToCartResult AddToCart(SellableItemDto item)
        {
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
            IProgress<SpecialProductDownloadProgress>? progress = null)
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
