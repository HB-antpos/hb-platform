using System.Collections.Concurrent;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using static Hbpos.Client.Tests.SpecialProductsDownloadTestSupport;

namespace Hbpos.Client.Tests;

/// <summary>
/// 特殊商品页「下载特殊商品」：下载写完本地库后，列表要立刻显示，不能等整体重建价格索引（商品多时要好几秒）。
/// 用例分两类，测试名前缀标注：
/// <list type="bullet">
/// <item><description><c>NewBehavior_*</c>：新行为，改动前的生产代码上应当失败（重建被阻塞时列表还没出现 / 缓存回退旧值）。</description></item>
/// <item><description><c>Characterization_*</c>：特征化/回归，语义没变，改动前后都应通过。</description></item>
/// </list>
/// 本文件只用工作流与视图模型的既有公开接口（不依赖新增的 onSpecialItemsReady 参数），
/// 以便把生产代码还原到改动前后做对照；依赖新参数的契约用例在 SpecialProductsDownloadListReadyCallbackTests。
/// </summary>
[Collection(ShutdownTimingTestCollection.Name)]
public sealed class SpecialProductsDownloadShowsListFirstTests
{
    private static readonly TimeSpan Budget = AsyncTestWaitSupport.DefaultTimeout;

    // ============================ 新行为（旧代码应失败） ============================

    [Fact]
    public async Task NewBehavior_download_shows_the_downloaded_list_while_the_price_index_is_still_rebuilding()
    {
        var scenario = CreateScenario();
        var viewModel = scenario.ViewModel;
        await viewModel.LoadAsync();
        Assert.Equal(["SKU-OLD"], ProductCodes(viewModel.SpecialItems));
        var gate = scenario.Repository.ArmRebuildGate();

        var download = viewModel.DownloadCommand.ExecuteAsync(null);
        await gate.Started.WaitAsync(Budget);

        // 此刻下载已写完本地库，价格索引重建被阻塞：页面上的列表必须已经是下载结果。
        Assert.Equal(["SKU-001", "SKU-002"], ProductCodes(viewModel.SpecialItems));
        Assert.Equal(["SKU-001", "SKU-002"], viewModel.DisplayedCards.Select(card => card.ProductCode).ToArray());
        // 重建还没完成：内存索引仍是旧的（扫不到新商品），页面保持忙碌，下载与编辑切换都不可用。
        Assert.Empty(scenario.Index.FindExactMatches("S001", "930002"));
        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.DownloadCommand.CanExecute(null));
        Assert.False(viewModel.ToggleEditModeCommand.CanExecute(null));

        gate.Release();
        await download.WaitAsync(Budget);

        // 放行后索引完整重建，页面复位，列表不因重建完成而变化。
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.DownloadCommand.CanExecute(null));
        Assert.Equal(["SKU-001", "SKU-002"], ProductCodes(viewModel.SpecialItems));
        Assert.Equal("SKU-002", Assert.Single(scenario.Index.FindExactMatches("S001", "930002")).ProductCode);
        Assert.Equal(scenario.Text("specialProducts.status.downloadCompleted", 2, 1), viewModel.StatusMessage);
    }

    [Fact]
    public async Task NewBehavior_download_progress_shows_complete_only_after_the_list_is_visible()
    {
        var scenario = CreateScenario();
        var viewModel = scenario.ViewModel;
        await viewModel.LoadAsync();
        var completedText = scenario.Text("specialProducts.download.completed", 100);
        // 记录每次「下载完成 100%」文案出现时，页面列表里已有几项。
        var itemCountWhenCompletedShown = new ConcurrentQueue<int>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.DownloadProgressText) &&
                viewModel.DownloadProgressText == completedText)
            {
                itemCountWhenCompletedShown.Enqueue(viewModel.SpecialItems.Count);
            }
        };
        var gate = scenario.Repository.ArmRebuildGate();

        var download = viewModel.DownloadCommand.ExecuteAsync(null);
        await gate.Started.WaitAsync(Budget);
        await WaitUntilAsync(() => viewModel.DownloadProgressText == completedText);

        // 重建仍被阻塞时进度条已是「完成」，那一刻列表必须已是下载到的 2 项，而不是下载前的 1 项。
        Assert.NotEmpty(itemCountWhenCompletedShown);
        Assert.All(itemCountWhenCompletedShown, count => Assert.Equal(2, count));

        gate.Release();
        await download.WaitAsync(Budget);
        Assert.Equal(100d, viewModel.DownloadProgressValue);
    }

    [Fact]
    public async Task NewBehavior_download_that_leaves_no_special_products_empties_the_list_before_the_index_rebuild_finishes()
    {
        var scenario = CreateScenario();
        var viewModel = scenario.ViewModel;
        scenario.Service.DownloadResult = new SpecialProductDownloadResult("S001", 1, 0, 0, 0, 1);
        scenario.Service.OnDownloaded = (repository, _) =>
        {
            repository.SpecialItems = [];
            repository.SellableItems = [Plain, Old with { IsSpecialProduct = false }];
        };
        await viewModel.LoadAsync();
        Assert.Equal(["SKU-OLD"], ProductCodes(viewModel.SpecialItems));
        var gate = scenario.Repository.ArmRebuildGate();

        var download = viewModel.DownloadCommand.ExecuteAsync(null);
        await gate.Started.WaitAsync(Budget);

        Assert.Empty(viewModel.SpecialItems);
        Assert.Empty(viewModel.DisplayedCards);
        Assert.True(viewModel.IsBusy);

        gate.Release();
        await download.WaitAsync(Budget);

        Assert.False(viewModel.IsBusy);
        Assert.Empty(viewModel.SpecialItems);
        Assert.Equal(scenario.Text("specialProducts.status.downloadCompleted", 0, 1), viewModel.StatusMessage);
    }

    [Fact]
    public async Task NewBehavior_index_rebuild_failure_keeps_the_new_list_reports_failure_and_releases_busy()
    {
        var scenario = CreateScenario();
        var viewModel = scenario.ViewModel;
        await viewModel.LoadAsync();
        var gate = scenario.Repository.ArmRebuildGate();

        var download = viewModel.DownloadCommand.ExecuteAsync(null);
        await gate.Started.WaitAsync(Budget);
        gate.Fail(new InvalidOperationException("index boom"));
        await download.WaitAsync(Budget);

        // 列表是本地库里最新的特殊商品（数据是对的），不因随后的重建失败而回退或清空。
        Assert.Equal(["SKU-001", "SKU-002"], ProductCodes(viewModel.SpecialItems));
        // 失败走既有的「下载失败」状态栏提示，页面不卡在忙碌。
        Assert.Equal(scenario.Text("specialProducts.status.downloadFailed", "index boom"), viewModel.StatusMessage);
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.DownloadCommand.CanExecute(null));
    }

    [Fact]
    public async Task NewBehavior_cancel_during_index_rebuild_keeps_the_new_list_and_releases_busy()
    {
        var scenario = CreateScenario();
        var viewModel = scenario.ViewModel;
        await viewModel.LoadAsync();
        var gate = scenario.Repository.ArmRebuildGate();

        var download = viewModel.DownloadCommand.ExecuteAsync(null);
        await gate.Started.WaitAsync(Budget);
        viewModel.DownloadCommand.Cancel();
        await download.WaitAsync(Budget);

        Assert.Equal(["SKU-001", "SKU-002"], ProductCodes(viewModel.SpecialItems));
        Assert.StartsWith(
            scenario.Text("specialProducts.status.downloadFailed", string.Empty).TrimEnd(),
            viewModel.StatusMessage);
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.DownloadCommand.CanExecute(null));
    }

    [Fact]
    public async Task NewBehavior_index_rebuild_does_not_occupy_the_ui_thread_so_the_applied_list_can_render()
    {
        // 仓储的「Async」读取实为同步（Microsoft.Data.Sqlite），重建若跑在 UI 线程上会一直占住它，
        // 刚 Apply 的列表要等重建结束才能重绘。这里用单线程消息泵模拟 Dispatcher，重建读库同步阻塞调用线程：
        // 列表 Apply 后投递的「下一条消息」（≈ 重绘）必须在重建仍被阻塞时就能被处理。
        using var pump = new SingleThreadPump();
        var gateReady = new TaskCompletionSource<RebuildGate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextMessageProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var itemsSeenWhenProcessed = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = pump.RunAsync(async () =>
        {
            var scenario = CreateScenario();
            var viewModel = scenario.ViewModel;
            await viewModel.LoadAsync();
            gateReady.SetResult(scenario.Repository.ArmRebuildGate(blockSynchronously: true));
            viewModel.SpecialItems.CollectionChanged += (_, _) =>
                SynchronizationContext.Current!.Post(
                    _ =>
                    {
                        itemsSeenWhenProcessed.TrySetResult(ProductCodes(viewModel.SpecialItems));
                        nextMessageProcessed.TrySetResult();
                    },
                    null);

            await viewModel.DownloadCommand.ExecuteAsync(null);
            return viewModel.IsBusy;
        });

        var gate = await gateReady.Task.WaitAsync(Budget);
        try
        {
            // 放行重建之前，这条「下一条消息」就应当被处理，且处理时页面已是下载结果。
            var processed = await Task.WhenAny(nextMessageProcessed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(nextMessageProcessed.Task, processed);
            Assert.Equal(["SKU-001", "SKU-002"], await itemsSeenWhenProcessed.Task);
            Assert.False(run.IsCompleted);
        }
        finally
        {
            gate.Release();
        }

        Assert.False(await run.WaitAsync(Budget));
    }

    [Fact]
    public async Task NewBehavior_failed_index_rebuild_invalidates_the_cache_so_the_next_ensure_loaded_rereads_the_local_database()
    {
        var scenario = CreateScenario();
        var workflow = scenario.Workflow;
        var repository = scenario.Repository;
        // 先让缓存持有下载前的旧列表。
        var cached = await workflow.EnsureLoadedAsync("S001");
        Assert.Equal(["SKU-OLD"], ProductCodes(cached.SpecialItems));
        var gate = repository.ArmRebuildGate();
        gate.Fail(new InvalidOperationException("index boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.DownloadAsync("S001"));

        // 本地库已是新数据：重新进入页面（EnsureLoaded）不能再拿旧缓存把页面盖回下载前的样子。
        var ensured = await workflow.EnsureLoadedAsync("S001");
        Assert.Equal(["SKU-001", "SKU-002"], ProductCodes(ensured.SpecialItems));
    }

    [Fact]
    public async Task NewBehavior_ensure_loaded_while_the_index_is_rebuilding_returns_the_new_list_and_the_cache_is_final_afterwards()
    {
        var scenario = CreateScenario();
        var workflow = scenario.Workflow;
        var repository = scenario.Repository;
        await workflow.EnsureLoadedAsync("S001");
        var gate = repository.ArmRebuildGate();

        var download = workflow.DownloadAsync("S001");
        await gate.Started.WaitAsync(Budget);

        // 重建期间的缓存不能还是下载前的旧列表。
        var duringRebuild = await workflow.EnsureLoadedAsync("S001");
        Assert.Equal(["SKU-001", "SKU-002"], ProductCodes(duringRebuild.SpecialItems));

        gate.Release();
        var result = await download.WaitAsync(Budget);
        Assert.Equal(["SKU-001", "SKU-002"], ProductCodes(result.SpecialItems));

        // 重建成功后缓存就绪：再次 EnsureLoaded 直接命中缓存，不再读库。
        var readsBeforeEnsure = repository.LoadSpecialProductItemsCallCount;
        var afterRebuild = await workflow.EnsureLoadedAsync("S001");
        Assert.Equal(["SKU-001", "SKU-002"], ProductCodes(afterRebuild.SpecialItems));
        Assert.Equal(readsBeforeEnsure, repository.LoadSpecialProductItemsCallCount);
    }

    // ============================ 特征化 / 回归（旧代码应通过） ============================

    [Fact]
    public async Task Characterization_download_ends_idle_with_a_fully_rebuilt_index_special_flags_and_code_conflict_candidates()
    {
        // 码冲突：同一查码 930001 下除 Alpha 外还有另一商品，重建必须像目录加载一样把备选商品合并回索引。
        var conflict = CreateItem("SKU-009", "Conflict", "930001", isSpecialProduct: false);
        var scenario = CreateScenario(codeConflictItems: [conflict]);
        var viewModel = scenario.ViewModel;
        await viewModel.LoadAsync();

        await viewModel.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(["SKU-001", "SKU-002"], ProductCodes(viewModel.SpecialItems));
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.DownloadCommand.CanExecute(null));
        Assert.True(viewModel.IsDownloadProgressVisible);
        Assert.Equal(100d, viewModel.DownloadProgressValue);
        Assert.Equal(scenario.Text("specialProducts.status.downloadCompleted", 2, 1), viewModel.StatusMessage);
        // 索引按下载后的目录整体重建：旧特殊商品已不在，新特殊商品带标记，普通商品不带标记。
        Assert.Empty(scenario.Index.FindExactMatches("S001", "930000"));
        Assert.True(Assert.Single(scenario.Index.FindExactMatches("S001", "930002")).IsSpecialProduct);
        Assert.False(Assert.Single(scenario.Index.FindExactMatches("S001", "930100")).IsSpecialProduct);
        var conflictMatches = scenario.Index.FindExactMatches("S001", "930001");
        Assert.Equal(["SKU-001", "SKU-009"], ProductCodes(conflictMatches).Order().ToArray());
        // 编辑模式在下载后仍可切换。
        Assert.True(viewModel.ToggleEditModeCommand.CanExecute(null));
        viewModel.ToggleEditModeCommand.Execute(null);
        Assert.True(viewModel.IsEditMode);
    }

    [Fact]
    public async Task Characterization_download_with_zero_total_ends_with_an_empty_list_and_idle_state()
    {
        var scenario = CreateScenario();
        var viewModel = scenario.ViewModel;
        scenario.Service.DownloadResult = new SpecialProductDownloadResult("S001", 1, 0, 0, 0, 1);
        scenario.Service.OnDownloaded = (repository, _) =>
        {
            repository.SpecialItems = [];
            repository.SellableItems = [Plain, Old with { IsSpecialProduct = false }];
        };
        await viewModel.LoadAsync();

        await viewModel.DownloadCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.SpecialItems);
        Assert.Empty(viewModel.DisplayedCards);
        Assert.False(viewModel.IsBusy);
        Assert.Equal(scenario.Text("specialProducts.status.downloadCompleted", 0, 1), viewModel.StatusMessage);
        Assert.False(Assert.Single(scenario.Index.FindExactMatches("S001", "930000")).IsSpecialProduct);
    }

    [Fact]
    public async Task Characterization_download_button_stays_disabled_until_the_rebuild_finishes_and_the_download_runs_once()
    {
        var scenario = CreateScenario();
        var viewModel = scenario.ViewModel;
        await viewModel.LoadAsync();
        var gate = scenario.Repository.ArmRebuildGate();

        var first = viewModel.DownloadCommand.ExecuteAsync(null);
        await gate.Started.WaitAsync(Budget);

        // 界面按钮遵循 CanExecute：整个下载（含索引重建）期间不可再点，因此不会叠加出第二次下载。
        Assert.False(viewModel.DownloadCommand.CanExecute(null));
        Assert.Equal(1, scenario.Service.DownloadCallCount);

        gate.Release();
        await first.WaitAsync(Budget);
        Assert.True(viewModel.DownloadCommand.CanExecute(null));
        Assert.Equal(1, scenario.Service.DownloadCallCount);
        Assert.Equal(1, scenario.Repository.LoadSellableItemsCallCount);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task Characterization_two_consecutive_downloads_each_rebuild_once_and_show_the_latest_list()
    {
        var scenario = CreateScenario();
        var viewModel = scenario.ViewModel;
        scenario.Service.OnDownloaded = (repository, call) =>
        {
            if (call == 1)
            {
                ApplyDefaultDownload(repository);
            }
            else
            {
                repository.SpecialItems = [Beta, Gamma];
                repository.SellableItems = [Beta, Gamma, Plain];
            }
        };
        await viewModel.LoadAsync();

        await viewModel.DownloadCommand.ExecuteAsync(null);
        Assert.Equal(["SKU-001", "SKU-002"], ProductCodes(viewModel.SpecialItems));
        await viewModel.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(["SKU-002", "SKU-003"], ProductCodes(viewModel.SpecialItems));
        Assert.Equal(2, scenario.Service.DownloadCallCount);
        Assert.Equal(2, scenario.Repository.LoadSellableItemsCallCount);
        Assert.Empty(scenario.Index.FindExactMatches("S001", "930001"));
        Assert.True(Assert.Single(scenario.Index.FindExactMatches("S001", "930003")).IsSpecialProduct);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task Characterization_download_after_a_cancelled_download_does_not_stack_and_ends_with_the_latest_data()
    {
        var scenario = CreateScenario();
        var viewModel = scenario.ViewModel;
        scenario.Service.OnDownloaded = (repository, call) =>
        {
            if (call == 1)
            {
                ApplyDefaultDownload(repository);
            }
            else
            {
                repository.SpecialItems = [Gamma];
                repository.SellableItems = [Gamma, Plain];
            }
        };
        await viewModel.LoadAsync();

        // 第一次下载在索引重建阶段被取消。
        var gate = scenario.Repository.ArmRebuildGate();
        var cancelled = viewModel.DownloadCommand.ExecuteAsync(null);
        await gate.Started.WaitAsync(Budget);
        viewModel.DownloadCommand.Cancel();
        await cancelled.WaitAsync(Budget);
        Assert.False(viewModel.IsBusy);

        // 第二次下载正常完成：以第二次数据为准，索引只按第二次的目录重建，不叠加第一次的残留。
        await viewModel.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(2, scenario.Service.DownloadCallCount);
        Assert.Equal(2, scenario.Repository.LoadSellableItemsCallCount);
        Assert.Equal(["SKU-003"], ProductCodes(viewModel.SpecialItems));
        Assert.False(viewModel.IsBusy);
        Assert.Equal(scenario.Text("specialProducts.status.downloadCompleted", 2, 1), viewModel.StatusMessage);
        Assert.Empty(scenario.Index.FindExactMatches("S001", "930001"));
        Assert.True(Assert.Single(scenario.Index.FindExactMatches("S001", "930003")).IsSpecialProduct);
    }

    [Fact]
    public async Task Characterization_download_updates_the_cache_so_ensure_loaded_does_not_reread_the_database()
    {
        var scenario = CreateScenario();
        var workflow = scenario.Workflow;
        await workflow.EnsureLoadedAsync("S001");

        var result = await workflow.DownloadAsync("S001");
        var readsAfterDownload = scenario.Repository.LoadSpecialProductItemsCallCount;
        var ensured = await workflow.EnsureLoadedAsync("S001");

        Assert.Equal(["SKU-001", "SKU-002"], ProductCodes(result.SpecialItems));
        Assert.Equal(["SKU-001", "SKU-002"], ProductCodes(ensured.SpecialItems));
        Assert.Equal(readsAfterDownload, scenario.Repository.LoadSpecialProductItemsCallCount);
    }

    [Fact]
    public async Task Characterization_mark_remove_price_and_reorder_after_a_download_still_update_incrementally()
    {
        var scenario = CreateScenario();
        var workflow = scenario.Workflow;
        var repository = scenario.Repository;
        var service = scenario.Service;
        await workflow.DownloadAsync("S001");
        var rebuildsAfterDownload = repository.LoadSellableItemsCallCount;

        // 设为特殊：只对受影响商品 Upsert。
        var plainAsSpecial = Plain with { IsSpecialProduct = true };
        repository.SpecialItems = [Alpha, Beta, plainAsSpecial];
        service.MarkResult = new SpecialProductMarkResult([plainAsSpecial], [Alpha, Beta, plainAsSpecial]);
        var marked = await workflow.MarkSpecialProductAsync("S001", "SKU-100", true);
        Assert.Equal(["SKU-001", "SKU-002", "SKU-100"], ProductCodes(marked.SpecialItems));
        Assert.True(Assert.Single(scenario.Index.FindExactMatches("S001", "930100")).IsSpecialProduct);

        // 改价：收银扫码立即按新价。
        var repriced = plainAsSpecial with { RetailPrice = 6.5m };
        repository.SpecialItems = [Alpha, Beta, repriced];
        service.PriceResult = new SpecialProductMarkResult([repriced], [Alpha, Beta, repriced]);
        var priced = await workflow.UpdateSpecialProductPriceAsync("S001", "SKU-100", 6.5m);
        Assert.Equal(6.5m, priced.SpecialItems.Single(item => item.ProductCode == "SKU-100").RetailPrice);
        Assert.Equal(6.5m, Assert.Single(scenario.Index.FindExactMatches("S001", "930100")).RetailPrice);

        // 移除：特殊标记摘掉。
        var unmarked = repriced with { IsSpecialProduct = false };
        repository.SpecialItems = [Alpha, Beta];
        service.MarkResult = new SpecialProductMarkResult([unmarked], [Alpha, Beta]);
        var removed = await workflow.MarkSpecialProductAsync("S001", "SKU-100", false);
        Assert.Equal(["SKU-001", "SKU-002"], ProductCodes(removed.SpecialItems));
        Assert.False(Assert.Single(scenario.Index.FindExactMatches("S001", "930100")).IsSpecialProduct);

        // 排序：写库并返回新顺序，缓存同步。
        var reordered = await workflow.ReorderAsync("S001", [Alpha, Beta], "SKU-002", -1);
        Assert.NotNull(reordered);
        Assert.Equal(["SKU-002", "SKU-001"], ProductCodes(reordered.SpecialItems));
        Assert.Equal(["SKU-002", "SKU-001"], Assert.Single(repository.SavedOrders));
        Assert.Equal(["SKU-002", "SKU-001"], ProductCodes((await workflow.EnsureLoadedAsync("S001")).SpecialItems));

        // 这些增量操作都没有触发整体重建。
        Assert.Equal(rebuildsAfterDownload, repository.LoadSellableItemsCallCount);
    }
}
