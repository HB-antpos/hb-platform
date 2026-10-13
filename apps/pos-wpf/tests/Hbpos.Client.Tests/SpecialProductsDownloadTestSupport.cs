using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

/// <summary>
/// 「下载特殊商品后立刻显示列表」相关测试共用的替身与场景构造。
/// 核心是 <see cref="GatedCatalogRepository"/>：可以把价格索引重建（读全店可售商品）人为阻塞/注入失败，
/// 从而观察「下载写库完成」与「价格索引重建完成」之间页面和缓存的状态。
/// </summary>
internal static class SpecialProductsDownloadTestSupport
{
    public static readonly PosSessionState Session = new("HB POS", "S001", "Main Store", "POS-01", "C001", "Alice", true, 0);

    // 下载前本地库里的特殊商品（页面先显示它）。
    public static SellableItemDto Old => CreateItem("SKU-OLD", "Old", "930000", isSpecialProduct: true);

    // 下载后服务端返回的特殊商品。
    public static SellableItemDto Alpha => CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);

    public static SellableItemDto Beta => CreateItem("SKU-002", "Beta", "930002", isSpecialProduct: true);

    public static SellableItemDto Gamma => CreateItem("SKU-003", "Gamma", "930003", isSpecialProduct: true);

    // 普通商品（非特殊），只出现在全店可售目录里。
    public static SellableItemDto Plain => CreateItem("SKU-100", "Plain", "930100", isSpecialProduct: false);

    public static SellableItemDto CreateItem(
        string productCode,
        string displayName,
        string lookupCode,
        bool isSpecialProduct)
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

    public static string[] ProductCodes(IEnumerable<SellableItemDto> items) =>
        items.Select(item => item.ProductCode).ToArray();

    /// <summary>
    /// 默认的「服务端下载结果」：Alpha、Beta 成为特殊商品，Old 被取消，Plain 保持普通。
    /// 对应 <c>SpecialProductDownloadResult("S001", 1, 2, 2, 2, 1)</c>。
    /// </summary>
    public static void ApplyDefaultDownload(GatedCatalogRepository repository)
    {
        repository.SpecialItems = [Alpha, Beta];
        repository.SellableItems = [Alpha, Beta, Plain];
    }

    public static SpecialProductDownloadResult DefaultDownloadResult { get; } = new("S001", 1, 2, 2, 2, 1);

    /// <summary>价格索引重建的人为阻塞点：重建读全店商品时先通知「已进入」，再等测试放行或注入失败。</summary>
    public sealed class RebuildGate(bool blockSynchronously = false)
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>重建已经走到读全店商品这一步（此时下载写库早已完成）。</summary>
        public Task Started => _started.Task;

        public void Release() => _release.TrySetResult(true);

        public void Fail(Exception exception) => _release.TrySetException(exception);

        internal Task EnterAsync(CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            if (blockSynchronously)
            {
                // 模拟 Microsoft.Data.Sqlite 的「Async」实为同步：直接占住调用线程，
                // 若重建跑在 UI 线程上，UI 线程就被占住直到放行。
                _release.Task.WaitAsync(cancellationToken).GetAwaiter().GetResult();
                return Task.CompletedTask;
            }

            return _release.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// 单线程消息泵：模拟 WPF Dispatcher（SynchronizationContext.Post 的回调在同一线程依次执行）。
    /// 在其中运行的 async 代码，await 的延续会回到这条线程；线程被同步占住时，后续消息（≈ 重绘）排队等待。
    /// </summary>
    public sealed class SingleThreadPump : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public SingleThreadPump()
        {
            var thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                {
                    callback(state);
                }
            })
            {
                IsBackground = true
            };
            thread.Start();
        }

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public Task<T> RunAsync<T>(Func<Task<T>> body)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(
                async _ =>
                {
                    try
                    {
                        completion.SetResult(await body());
                    }
                    catch (Exception ex)
                    {
                        completion.SetException(ex);
                    }
                },
                null);
            return completion.Task;
        }

        public void Dispose() => _queue.CompleteAdding();
    }

    public sealed class GatedCatalogRepository : ILocalCatalogRepository
    {
        private RebuildGate? _gate;

        public IReadOnlyList<SellableItemDto> SellableItems { get; set; } = [];

        public IReadOnlyList<SellableItemDto> SpecialItems { get; set; } = [];

        public IReadOnlyList<SellableItemDto> CodeConflictItems { get; set; } = [];

        public List<string[]> SavedOrders { get; } = [];

        public int LoadSellableItemsCallCount { get; private set; }

        public int LoadSpecialProductItemsCallCount { get; private set; }

        /// <summary>读特殊商品列表前的阻塞点（默认不阻塞）。</summary>
        public Func<CancellationToken, Task>? BeforeLoadSpecialProductItemsAsync { get; set; }

        /// <summary>下一次起，价格索引重建（读全店可售商品）会被该闸门阻塞；一次性，重建进入后即消耗。</summary>
        public RebuildGate ArmRebuildGate(bool blockSynchronously = false)
        {
            var gate = new RebuildGate(blockSynchronously);
            _gate = gate;
            return gate;
        }

        public Task ReplaceSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UpsertSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> DeleteByLookupCodesAsync(string storeCode, IEnumerable<string> lookupCodes, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<SellableItemDto?> FindByLookupCodeAsync(string storeCode, string lookupCode, CancellationToken cancellationToken = default) =>
            Task.FromResult<SellableItemDto?>(null);

        public async Task<IReadOnlyList<SellableItemDto>> LoadSpecialProductItemsAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            LoadSpecialProductItemsCallCount++;
            if (BeforeLoadSpecialProductItemsAsync is not null)
            {
                await BeforeLoadSpecialProductItemsAsync(cancellationToken);
            }

            return SpecialItems;
        }

        public Task SaveSpecialProductOrderAsync(string storeCode, IEnumerable<string> productCodes, CancellationToken cancellationToken = default)
        {
            var ordered = productCodes.ToArray();
            SavedOrders.Add(ordered);
            SpecialItems = ordered
                .Select(code => SpecialItems.First(item => string.Equals(item.ProductCode, code, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            return Task.CompletedTask;
        }

        public Task<int> UpdateSpecialProductFlagAsync(string storeCode, string productCode, bool isSpecialProduct, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<int> ClearSpecialProductFlagsExceptAsync(string storeCode, IEnumerable<string> productCodesToKeep, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<LocalSellableItemCompareRow>> LoadSellableItemComparePageAsync(
            string storeCode,
            string? afterLookupCodeNormalized,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalSellableItemCompareRow>>([]);

        public Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(CancellationToken cancellationToken = default)
        {
            LoadSellableItemsCallCount++;
            return Task.FromResult(SellableItems);
        }

        public async Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(string storeCode, CancellationToken cancellationToken = default)
        {
            LoadSellableItemsCallCount++;
            var gate = Interlocked.Exchange(ref _gate, null);
            if (gate is not null)
            {
                await gate.EnterAsync(cancellationToken);
            }

            return SellableItems
                .Where(item => string.Equals(item.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        public Task<IReadOnlyList<SellableItemDto>> LoadCodeConflictItemsAsync(
            string storeCode,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CodeConflictItems);
    }

    /// <summary>
    /// 模拟下载服务：「写本地库」由 <see cref="OnDownloaded"/> 完成（把服务端数据落进替身仓储），
    /// 并像真实服务一样依次上报 Downloading、Completed(100%)。
    /// </summary>
    public sealed class ScriptedSpecialProductService(GatedCatalogRepository repository) : ISpecialProductService
    {
        public int DownloadCallCount { get; private set; }

        public SpecialProductDownloadResult DownloadResult { get; set; } = DefaultDownloadResult;

        /// <summary>参数为第几次下载（从 1 开始）；默认每次都落成 <see cref="ApplyDefaultDownload"/> 的数据。</summary>
        public Action<GatedCatalogRepository, int> OnDownloaded { get; set; } =
            (repository, _) => ApplyDefaultDownload(repository);

        /// <summary>非 null 时，下载服务本身抛出该异常（模拟下载阶段失败，此时尚未写库）。</summary>
        public Exception? DownloadException { get; set; }

        public SpecialProductMarkResult MarkResult { get; set; } = new([], []);

        public SpecialProductMarkResult PriceResult { get; set; } = new([], []);

        public Task<SpecialProductMarkResult> MarkSpecialProductAsync(
            string storeCode,
            string productCode,
            bool isSpecialProduct,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(MarkResult);

        public Task<SpecialProductMarkResult> UpdateSpecialProductPriceAsync(
            string storeCode,
            string productCode,
            decimal retailPrice,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(PriceResult);

        public Task<SpecialProductDownloadResult> DownloadSpecialProductsAsync(
            string storeCode,
            CancellationToken cancellationToken = default,
            IProgress<SpecialProductDownloadProgress>? progress = null)
        {
            DownloadCallCount++;
            if (DownloadException is not null)
            {
                progress?.Report(Progress(storeCode, SpecialProductDownloadProgressStage.Failed, 0, 0));
                return Task.FromException<SpecialProductDownloadResult>(DownloadException);
            }

            OnDownloaded(repository, DownloadCallCount);
            progress?.Report(Progress(storeCode, SpecialProductDownloadProgressStage.Downloading, DownloadResult.TotalCount, DownloadResult.DownloadedCount));
            progress?.Report(Progress(storeCode, SpecialProductDownloadProgressStage.Completed, DownloadResult.TotalCount, DownloadResult.DownloadedCount));
            return Task.FromResult(DownloadResult);
        }

        private SpecialProductDownloadProgress Progress(
            string storeCode,
            SpecialProductDownloadProgressStage stage,
            int total,
            int downloaded) =>
            new(
                storeCode,
                stage,
                total,
                downloaded,
                Percent: stage == SpecialProductDownloadProgressStage.Failed ? 0 : 100,
                PageCount: DownloadResult.PageCount,
                UpsertedCount: DownloadResult.UpsertedCount,
                UnmarkedCount: DownloadResult.UnmarkedCount,
                ElapsedMilliseconds: 10);
    }

    /// <summary>真实的工作流服务 + 真实视图模型，仓储/下载服务用上面的替身。</summary>
    public sealed class Scenario
    {
        public required GatedCatalogRepository Repository { get; init; }

        public required ScriptedSpecialProductService Service { get; init; }

        public required LocalSellableItemIndex Index { get; init; }

        public required SpecialProductsWorkflowService Workflow { get; init; }

        public required SpecialProductsViewModel ViewModel { get; init; }

        public required LocalizationService Localization { get; init; }

        public string Text(string key, params object[] args) =>
            args.Length == 0
                ? Localization.T(key)
                : string.Format(Localization.CurrentCulture, Localization.T(key), args);
    }

    /// <summary>
    /// 下载前：本地库、内存索引里只有 <see cref="Old"/> 与 <see cref="Plain"/>；
    /// 下载（<see cref="ScriptedSpecialProductService.OnDownloaded"/>）后变成 <see cref="ApplyDefaultDownload"/> 的数据。
    /// </summary>
    public static Scenario CreateScenario(IReadOnlyList<SellableItemDto>? codeConflictItems = null)
    {
        var localization = new LocalizationService();
        var cart = new PosCartService();
        var index = new LocalSellableItemIndex();
        var repository = new GatedCatalogRepository
        {
            SellableItems = [Old, Plain],
            SpecialItems = [Old],
            CodeConflictItems = codeConflictItems ?? []
        };
        index.ReplaceAll(repository.SellableItems);
        var service = new ScriptedSpecialProductService(repository);
        var workflow = new SpecialProductsWorkflowService(index, cart, repository, service);
        var viewModel = new SpecialProductsViewModel(
            index,
            cart,
            repository,
            service,
            Session,
            localization,
            () => { },
            workflowService: workflow,
            // 进度条 5 秒自动隐藏、缩略图延迟启用都用永不触发的延时顶掉，避免干扰断言。
            delayAsync: (_, cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken));
        return new Scenario
        {
            Repository = repository,
            Service = service,
            Index = index,
            Workflow = workflow,
            ViewModel = viewModel,
            Localization = localization
        };
    }
}
