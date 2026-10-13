using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;
using static Hbpos.Client.Tests.SpecialProductsDownloadTestSupport;

namespace Hbpos.Client.Tests;

/// <summary>
/// 工作流 <c>DownloadAsync</c> 新增的「特殊商品列表已就绪」回调（onSpecialItemsReady）的契约，
/// 以及视图模型对该回调的使用。这些用例依赖新增的参数，因此单独成文件：
/// 对照改动前的生产代码时，本文件整体不参与编译，改动前后的行为差异由
/// SpecialProductsDownloadShowsListFirstTests 里只用既有公开接口的用例给出。
/// </summary>
[Collection(ShutdownTimingTestCollection.Name)]
public sealed class SpecialProductsDownloadListReadyCallbackTests
{
    private static readonly TimeSpan Budget = AsyncTestWaitSupport.DefaultTimeout;

    [Fact]
    public async Task DownloadAsync_invokes_the_callback_with_the_downloaded_items_before_the_index_rebuild_starts()
    {
        var scenario = CreateScenario();
        var repository = scenario.Repository;
        var gate = repository.ArmRebuildGate();
        IReadOnlyList<SellableItemDto>? received = null;
        var rebuildReadsAtCallback = -1;
        var indexAlreadyHadNewItemAtCallback = true;

        var download = scenario.Workflow.DownloadAsync(
            "S001",
            onSpecialItemsReady: items =>
            {
                received = items;
                rebuildReadsAtCallback = repository.LoadSellableItemsCallCount;
                indexAlreadyHadNewItemAtCallback = scenario.Index.FindExactMatches("S001", "930002").Count > 0;
            });
        await gate.Started.WaitAsync(Budget);

        // 回调发生时，价格索引的重建（读全店可售商品）还没开始，索引也还是旧的。
        Assert.Equal(0, rebuildReadsAtCallback);
        Assert.False(indexAlreadyHadNewItemAtCallback);
        Assert.Equal(["SKU-001", "SKU-002"], ProductCodes(received!));

        gate.Release();
        var result = await download.WaitAsync(Budget);

        // 方法返回时索引已完整重建，返回值里的列表就是回调给出的那一份。
        Assert.Same(received, result.SpecialItems);
        Assert.Equal("SKU-002", Assert.Single(scenario.Index.FindExactMatches("S001", "930002")).ProductCode);
        Assert.Empty(scenario.Index.FindExactMatches("S001", "930000"));
    }

    [Fact]
    public async Task DownloadAsync_holds_the_service_completed_progress_until_after_the_callback()
    {
        var scenario = CreateScenario();
        var gate = scenario.Repository.ArmRebuildGate();
        var log = new List<string>();
        var progress = new RecordingProgress(log);

        var download = scenario.Workflow.DownloadAsync(
            "S001",
            progress: progress,
            onSpecialItemsReady: _ => log.Add("callback"));
        await gate.Started.WaitAsync(Budget);

        // 服务层的 Completed(100%) 被扣到列表交给调用方之后放出，且不等索引重建：
        // 重建仍被阻塞时，进度已走完，并且是在回调之后。
        Assert.Equal(["progress:Downloading", "callback", "progress:Completed"], log.ToArray());

        gate.Release();
        await download.WaitAsync(Budget);
        Assert.Equal(["progress:Downloading", "callback", "progress:Completed"], log.ToArray());
    }

    [Fact]
    public async Task DownloadAsync_without_a_callback_forwards_the_completed_progress_unchanged()
    {
        var scenario = CreateScenario();
        var log = new List<string>();
        scenario.Repository.BeforeLoadSpecialProductItemsAsync = _ =>
        {
            log.Add("read-special-items");
            return Task.CompletedTask;
        };

        await scenario.Workflow.DownloadAsync("S001", progress: new RecordingProgress(log));

        // 没有回调时不扣进度：Completed 在服务返回时就转发，早于读特殊商品列表（与改动前一致）。
        Assert.Equal(["progress:Downloading", "progress:Completed", "read-special-items"], log.ToArray());
    }

    [Fact]
    public async Task DownloadAsync_callback_exception_does_not_stop_the_index_rebuild()
    {
        var scenario = CreateScenario();
        await scenario.Workflow.EnsureLoadedAsync("S001");
        var log = new List<string>();

        var result = await scenario.Workflow.DownloadAsync(
            "S001",
            progress: new RecordingProgress(log),
            onSpecialItemsReady: _ => throw new InvalidOperationException("ui boom"));

        // 提前显示只是体验优化：回调抛异常不能让索引停在旧状态，被扣住的 Completed 也照常放出。
        Assert.Equal(["SKU-001", "SKU-002"], ProductCodes(result.SpecialItems));
        Assert.Equal("SKU-002", Assert.Single(scenario.Index.FindExactMatches("S001", "930002")).ProductCode);
        Assert.Equal(["progress:Downloading", "progress:Completed"], log.ToArray());
        // 缓存同样已就绪：再次 EnsureLoaded 不再读库。
        var readsBefore = scenario.Repository.LoadSpecialProductItemsCallCount;
        var ensured = await scenario.Workflow.EnsureLoadedAsync("S001");
        Assert.Equal(["SKU-001", "SKU-002"], ProductCodes(ensured.SpecialItems));
        Assert.Equal(readsBefore, scenario.Repository.LoadSpecialProductItemsCallCount);
    }

    [Fact]
    public async Task DownloadAsync_rebuild_failure_after_the_callback_still_releases_the_completed_progress()
    {
        var scenario = CreateScenario();
        var gate = scenario.Repository.ArmRebuildGate();
        gate.Fail(new InvalidOperationException("index boom"));
        var log = new List<string>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scenario.Workflow.DownloadAsync(
                "S001",
                progress: new RecordingProgress(log),
                onSpecialItemsReady: _ => log.Add("callback")));

        Assert.Equal("index boom", exception.Message);
        // 列表先给了调用方，进度没有停在「下载中」。
        Assert.Equal(["progress:Downloading", "callback", "progress:Completed"], log.ToArray());
    }

    [Fact]
    public async Task DownloadAsync_service_failure_neither_calls_back_nor_touches_the_cache()
    {
        var scenario = CreateScenario();
        await scenario.Workflow.EnsureLoadedAsync("S001");
        scenario.Service.DownloadException = new HttpRequestException("net down");
        var log = new List<string>();
        var readsBefore = scenario.Repository.LoadSpecialProductItemsCallCount;

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            scenario.Workflow.DownloadAsync(
                "S001",
                progress: new RecordingProgress(log),
                onSpecialItemsReady: _ => log.Add("callback")));

        // 下载本身失败：失败进度原样透传，不回调，也没有重建索引、不碰缓存。
        Assert.Equal(["progress:Failed"], log.ToArray());
        Assert.Equal(0, scenario.Repository.LoadSellableItemsCallCount);
        var ensured = await scenario.Workflow.EnsureLoadedAsync("S001");
        Assert.Equal(["SKU-OLD"], ProductCodes(ensured.SpecialItems));
        Assert.Equal(readsBefore, scenario.Repository.LoadSpecialProductItemsCallCount);
    }

    [Fact]
    public async Task DownloadAsync_cancel_while_reading_the_special_items_releases_completed_and_skips_the_callback()
    {
        var scenario = CreateScenario();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Repository.BeforeLoadSpecialProductItemsAsync = cancellationToken =>
        {
            reading.TrySetResult();
            return Task.Delay(Timeout.Infinite, cancellationToken);
        };
        var log = new List<string>();
        using var cts = new CancellationTokenSource();

        var download = scenario.Workflow.DownloadAsync(
            "S001",
            cts.Token,
            new RecordingProgress(log),
            _ => log.Add("callback"));
        await reading.Task.WaitAsync(Budget);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        // 数据已下载完成，进度不能卡在「下载中 100%」；列表没读到所以不回调，也不重建索引。
        Assert.Equal(["progress:Downloading", "progress:Completed"], log.ToArray());
        Assert.Equal(0, scenario.Repository.LoadSellableItemsCallCount);
    }

    [Fact]
    public async Task ViewModel_displays_the_callback_list_while_the_workflow_is_still_busy_and_applies_it_once()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = new StubWorkflow { Items = [Alpha, Beta], InvokeCallback = true, HoldAfterCallback = hold.Task };
        var viewModel = CreateViewModel(workflow);
        var changes = 0;
        viewModel.SpecialItems.CollectionChanged += (_, _) => changes++;

        var download = viewModel.DownloadCommand.ExecuteAsync(null);
        await workflow.CallbackInvoked.Task.WaitAsync(Budget);

        // 回调一到就显示；工作流（模拟索引重建）还没返回，页面保持忙碌。
        Assert.Equal(["SKU-001", "SKU-002"], ProductCodes(viewModel.SpecialItems));
        Assert.True(viewModel.IsBusy);
        var changesAfterCallback = changes;
        Assert.True(changesAfterCallback > 0);

        hold.SetResult();
        await download.WaitAsync(Budget);

        // 工作流返回后不再重复应用一遍（不重置分页、不闪烁），并复位忙碌。
        Assert.Equal(changesAfterCallback, changes);
        Assert.False(viewModel.IsBusy);
        Assert.Equal(["SKU-001", "SKU-002"], ProductCodes(viewModel.SpecialItems));
    }

    [Fact]
    public async Task ViewModel_falls_back_to_the_returned_items_when_the_workflow_never_calls_back()
    {
        var workflow = new StubWorkflow { Items = [Alpha, Beta], InvokeCallback = false };
        var viewModel = CreateViewModel(workflow);

        await viewModel.DownloadCommand.ExecuteAsync(null);

        // 不支持回调的工作流（如旧实现、替身）仍按原样在返回后显示列表。
        Assert.Equal(["SKU-001", "SKU-002"], ProductCodes(viewModel.SpecialItems));
        Assert.Equal(["SKU-001", "SKU-002"], viewModel.DisplayedCards.Select(card => card.ProductCode).ToArray());
        Assert.False(viewModel.IsBusy);
    }

    private static SpecialProductsViewModel CreateViewModel(StubWorkflow workflow)
    {
        var repository = new GatedCatalogRepository();
        return new SpecialProductsViewModel(
            new LocalSellableItemIndex(),
            new PosCartService(),
            repository,
            new ScriptedSpecialProductService(repository),
            Session,
            new Hbpos.Client.Wpf.Localization.LocalizationService(),
            () => { },
            workflowService: workflow,
            delayAsync: (_, cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken));
    }

    /// <summary>同步记录进度阶段（不像 Progress&lt;T&gt; 那样异步投递），便于断言与回调的先后顺序。</summary>
    private sealed class RecordingProgress(List<string> log) : IProgress<SpecialProductDownloadProgress>
    {
        public void Report(SpecialProductDownloadProgress value) => log.Add($"progress:{value.Stage}");
    }

    /// <summary>只实现下载：可选地调用回调，再模拟「索引重建」耗时后返回。</summary>
    private sealed class StubWorkflow : ISpecialProductsWorkflowService
    {
        public required IReadOnlyList<SellableItemDto> Items { get; init; }

        public bool InvokeCallback { get; init; }

        public Task? HoldAfterCallback { get; init; }

        public TaskCompletionSource CallbackInvoked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SpecialProductsDownloadWorkflowResult> DownloadAsync(
            string storeCode,
            CancellationToken cancellationToken = default,
            IProgress<SpecialProductDownloadProgress>? progress = null,
            Action<IReadOnlyList<SellableItemDto>>? onSpecialItemsReady = null)
        {
            if (InvokeCallback)
            {
                onSpecialItemsReady?.Invoke(Items);
                CallbackInvoked.TrySetResult();
            }

            if (HoldAfterCallback is not null)
            {
                await HoldAfterCallback.WaitAsync(cancellationToken);
            }

            return new SpecialProductsDownloadWorkflowResult(
                new SpecialProductDownloadResult(storeCode, 1, Items.Count, Items.Count, Items.Count, 0),
                Items);
        }

        public Task<SpecialProductsLoadResult> PreloadAsync(string storeCode, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SpecialProductsLoadResult> EnsureLoadedAsync(string storeCode, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SpecialProductsLoadResult> LoadAsync(string storeCode, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public SpecialProductsSearchResult Search(string storeCode, string searchText) =>
            throw new NotSupportedException();

        public Task<SpecialProductsSearchResult> SearchAsync(
            string storeCode,
            string searchText,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public SpecialProductsAddToCartResult AddToCart(SellableItemDto item) =>
            throw new NotSupportedException();

        public Task<SpecialProductsMutationWorkflowResult> MarkSpecialProductAsync(
            string storeCode,
            string productCode,
            bool isSpecialProduct,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SpecialProductsReorderWorkflowResult?> ReorderAsync(
            string storeCode,
            IReadOnlyList<SellableItemDto> currentItems,
            string productCode,
            int delta,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
