using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using System.Collections.Concurrent;

namespace Hbpos.Client.Tests;

public sealed class ShellCatalogServiceTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task SyncCatalogAndReloadAsync_ResetCancelsRunningRegularSyncAndDoesNotOverlapWrites()
    {
        var priceIndex = new LocalSellableItemIndex();
        var repository = new FakeLocalCatalogRepository();
        var sync = new CoordinatedCatalogSyncService();
        var service = new ShellCatalogService(priceIndex, repository, sync, new PosCartService());

        var regularTask = service.SyncCatalogAndReloadAsync("S01", forceFullDownload: false);
        await sync.RegularStarted.Task.WaitUntilCompletedAsync(() => sync.Describe(regularTask));

        var resetTask = service.SyncCatalogAndReloadAsync("S01", forceFullDownload: true);
        await sync.ResetStarted.Task.WaitUntilCompletedAsync(() => sync.Describe(regularTask, resetTask));

        sync.ReleaseRegularIfNotCanceled();
        var regularException = await Record.ExceptionAsync(() => regularTask);
        var resetItems = await resetTask;

        Assert.IsAssignableFrom<OperationCanceledException>(regularException);
        Assert.True(sync.RegularCanceled);
        Assert.Equal(1, sync.MaxActiveWrites);
        Assert.Equal(["S01:false", "S01:true"], sync.Calls);
        Assert.Single(resetItems);
        Assert.Equal("RESET-ITEM", Assert.Single(priceIndex.Items).ProductCode);
    }

    [Fact]
    public async Task SyncCatalogAndReloadAsync_reuses_the_loaded_index_when_catalog_and_conflicts_are_unchanged()
    {
        var priceIndex = new LocalSellableItemIndex();
        var repository = new CountingCatalogRepository { Items = [CreateItem("SKU-001")] };
        var sync = new FixedResultCatalogSyncService();
        var service = new ShellCatalogService(priceIndex, repository, sync, new PosCartService());

        sync.Result = new LocalCatalogSyncResult("S01", 0, 1, 1, 0, LocalCatalogSyncModes.Full);
        var first = await service.SyncCatalogAndReloadAsync("S01", forceFullDownload: false);

        // 无变化：不重读目录（库里即使被别处改动，也等下次真正变化时再整体重载）。
        repository.Items = [CreateItem("SKU-RELOADED")];
        sync.Result = new LocalCatalogSyncResult("S01", 0, 0, 0, 0, LocalCatalogSyncModes.NoChange, CatalogChanged: false, CodeConflictsChanged: false);
        var second = await service.SyncCatalogAndReloadAsync("S01", forceFullDownload: false);

        Assert.Same(first, second);
        Assert.Equal(1, repository.LoadCount);
        Assert.Equal("SKU-001", Assert.Single(priceIndex.Items).ProductCode);

        // 只有冲突候选变化也要重建扫码索引，才能把新的备选商品并进来。
        sync.Result = new LocalCatalogSyncResult("S01", 0, 0, 0, 0, LocalCatalogSyncModes.NoChange, CatalogChanged: false, CodeConflictsChanged: true);
        await service.SyncCatalogAndReloadAsync("S01", forceFullDownload: false);

        Assert.Equal(2, repository.LoadCount);
        Assert.Equal("SKU-RELOADED", Assert.Single(priceIndex.Items).ProductCode);
    }

    [Fact]
    public async Task SyncCatalogAndReloadAsync_reloads_when_the_unchanged_result_is_for_a_store_never_loaded()
    {
        var priceIndex = new LocalSellableItemIndex();
        var repository = new CountingCatalogRepository { Items = [CreateItem("SKU-001")] };
        var sync = new FixedResultCatalogSyncService
        {
            Result = new LocalCatalogSyncResult("S01", 0, 0, 0, 0, LocalCatalogSyncModes.NoChange, CatalogChanged: false, CodeConflictsChanged: false)
        };
        var service = new ShellCatalogService(priceIndex, repository, sync, new PosCartService());

        var items = await service.SyncCatalogAndReloadAsync("S01", forceFullDownload: false);

        Assert.Equal(1, repository.LoadCount);
        Assert.Equal("SKU-001", Assert.Single(items).ProductCode);
    }

    [Fact]
    public async Task IsCatalogSyncActive_IsTrueWhileBackgroundSyncIsRunning()
    {
        var priceIndex = new LocalSellableItemIndex();
        var repository = new FakeLocalCatalogRepository();
        var sync = new CoordinatedCatalogSyncService();
        var service = new ShellCatalogService(priceIndex, repository, sync, new PosCartService());

        var regularTask = service.SyncCatalogAndReloadAsync("S01", forceFullDownload: false);
        // 后台同步经 Task.Run 进入线程池后才会触发 RegularStarted，CI 线程池饥饿时这一跳耗时不可控，使用共享预算。
        await sync.RegularStarted.Task.WaitUntilCompletedAsync(() => sync.Describe(regularTask));

        // 假同步服务在 RegularStarted 之后会一直卡住直到 ReleaseRegularIfNotCanceled，
        // 所以"同步进行中"这个状态是被测试自己持有的，不是瞬时窗口。
        Assert.True(service.IsCatalogSyncActive, $"后台同步进行中应为 active：{sync.Describe(regularTask)}");

        sync.ReleaseRegularIfNotCanceled();
        await regularTask.WaitUntilCompletedAsync(() => sync.Describe(regularTask));

        Assert.False(service.IsCatalogSyncActive, $"后台同步结束后应回到 inactive：{sync.Describe(regularTask)}");
    }

    [Fact]
    public async Task SyncCatalogAndReloadAsync_QueuedSyncUsesReloadedIndexCountForComparingProgress()
    {
        var priceIndex = new LocalSellableItemIndex();
        priceIndex.ReplaceAll([CreateItem("INITIAL")]);
        var repository = new FakeLocalCatalogRepository
        {
            Items = [CreateItem("SKU-001"), CreateItem("SKU-002"), CreateItem("SKU-003")]
        };
        var sync = new CoordinatedCatalogSyncService();
        var service = new ShellCatalogService(priceIndex, repository, sync, new PosCartService());
        var reports = new ConcurrentQueue<CatalogSyncProgress>();

        var firstSync = service.SyncCatalogAndReloadAsync("S01", forceFullDownload: false);
        await sync.RegularStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        var secondSync = service.SyncCatalogAndReloadAsync(
            "S01",
            forceFullDownload: false,
            new RecordingProgress<CatalogSyncProgress>(reports));

        Assert.False(sync.SecondRegularStarted.Task.IsCompleted);

        sync.ReleaseRegularIfNotCanceled();
        await sync.SecondRegularStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        await Task.WhenAll(firstSync, secondSync).WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        var comparing = Assert.Single(reports.Where(report =>
            report.Stage == CatalogSyncProgressStage.Comparing));
        Assert.Equal(3, comparing.TotalCount);
    }

    [Fact]
    public async Task LoadLocalCatalogAsync_LoadsAndRebuildsIndexOnBackgroundThread()
    {
        var priceIndex = new LocalSellableItemIndex();
        var repository = new ThreadTrackingLocalCatalogRepository();
        repository.PromotionRules = [CreatePromotionRule()];
        var cart = new PosCartService();
        var cartChangedThreadId = -1;
        cart.CartChanged += (_, _) => cartChangedThreadId = Environment.CurrentManagedThreadId;
        var service = new ShellCatalogService(priceIndex, repository, new CoordinatedCatalogSyncService(), cart);

        var callerThreadId = -1;
        var completion = new TaskCompletionSource<IReadOnlyList<SellableItemDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var context = new SingleThreadSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            callerThreadId = Environment.CurrentManagedThreadId;
            try
            {
                _ = service.LoadLocalCatalogAsync("S01").ContinueWith(loadTask =>
                {
                    try
                    {
                        if (loadTask.IsFaulted)
                        {
                            completion.TrySetException(loadTask.Exception.InnerExceptions);
                            return;
                        }

                        if (loadTask.IsCanceled)
                        {
                            completion.TrySetCanceled();
                            return;
                        }

                        completion.TrySetResult(loadTask.Result);
                    }
                    finally
                    {
                        context.Complete();
                    }
                }, TaskScheduler.FromCurrentSynchronizationContext());

                context.RunOnCurrentThread();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
            }
        });

        thread.Start();
        var loadedItems = await completion.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        thread.Join(AsyncTestWaitSupport.DefaultTimeout);

        Assert.Single(loadedItems);
        Assert.Equal("RESET-ITEM", loadedItems[0].ProductCode);
        Assert.NotEqual(callerThreadId, repository.LoadThreadId);
        Assert.Equal(callerThreadId, cartChangedThreadId);
        Assert.NotEqual(repository.LoadThreadId, cartChangedThreadId);
        Assert.Equal("RESET-ITEM", Assert.Single(priceIndex.Items).ProductCode);
    }

    [Fact]
    public async Task SyncCatalogAndReloadAsync_LoadsPromotionRulesIntoCart()
    {
        var priceIndex = new LocalSellableItemIndex();
        var repository = new FakeLocalCatalogRepository();
        repository.PromotionRules =
        [
            CreatePromotionRule()
        ];
        var cart = new PosCartService();
        var service = new ShellCatalogService(priceIndex, repository, new CoordinatedCatalogSyncService(), cart);

        await service.SyncCatalogAndReloadAsync("S01", forceFullDownload: true);
        var line = cart.AddItem(CreateItem("SKU-001", price: 10m));
        cart.AddItem(CreateItem("SKU-001", price: 10m));

        Assert.True(line.IsAutomaticPromotionDiscount);
        Assert.Equal(5m, line.DiscountAmount);
        Assert.Equal(15m, cart.ActualAmount);
    }

    [Fact]
    public async Task SyncCatalogAndReloadAsync_RecordsSyncStatusForSuccessFailureAndReset()
    {
        var succeededAt = new DateTimeOffset(2026, 9, 26, 4, 32, 0, TimeSpan.Zero);
        var clock = new CatalogSyncStatusServiceTests.MutableTimeProvider(succeededAt);
        var syncStatus = new CatalogSyncStatusService(
            new CatalogSyncStatusServiceTests.InMemoryAppSettingsRepository(),
            clock);
        var sync = new CoordinatedCatalogSyncService();
        var service = new ShellCatalogService(
            new LocalSellableItemIndex(),
            new FakeLocalCatalogRepository(),
            sync,
            new PosCartService(),
            catalogSyncStatus: syncStatus);

        var regularTask = service.SyncCatalogAndReloadAsync("S01", forceFullDownload: false);
        await sync.RegularStarted.Task.WaitUntilCompletedAsync(() => sync.Describe(regularTask));
        Assert.True(syncStatus.GetStatus("S01").IsSyncing);

        // 数据重置取消的常规同步不算失败，重置完成后记为最新成功时间。
        var resetTask = service.SyncCatalogAndReloadAsync("S01", forceFullDownload: true);
        sync.ReleaseRegularIfNotCanceled();
        await Record.ExceptionAsync(() => regularTask);
        await resetTask.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        Assert.Equal(new CatalogSyncStatus(succeededAt, false, null, null), syncStatus.GetStatus("S01"));

        var failedAt = succeededAt.AddHours(1);
        clock.UtcNow = failedAt;
        var failingService = new ShellCatalogService(
            new LocalSellableItemIndex(),
            new FakeLocalCatalogRepository(),
            new FailingCatalogSyncService(),
            new PosCartService(),
            catalogSyncStatus: syncStatus);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => failingService.SyncCatalogAndReloadAsync("S01", forceFullDownload: false));

        Assert.Equal(
            new CatalogSyncStatus(succeededAt, false, failedAt, failure.Message),
            syncStatus.GetStatus("S01"));
    }

    [Fact]
    public async Task LoadLocalCatalogAsync_adds_code_conflict_products_to_price_index_only()
    {
        const string conflictCode = "6405090401470";
        var priceIndex = new LocalSellableItemIndex();
        var fly = CreateItem("P-FLY") with { LookupCode = conflictCode, DisplayName = "EXTENSION Fly Swatter" };
        var flower = CreateItem("P-FLOWER") with { LookupCode = conflictCode, DisplayName = "flower" };
        var repository = new FakeLocalCatalogRepository
        {
            Items = [fly],
            CodeConflictItems = [fly, flower]
        };
        var service = new ShellCatalogService(priceIndex, repository, new CoordinatedCatalogSyncService(), new PosCartService());

        var items = await service.LoadLocalCatalogAsync("S01");

        // 返回值仍是目录本身（商品数等统计口径不变），扫码索引额外带上冲突码的其它商品。
        Assert.Equal([fly], items);
        Assert.Equal(
            ["P-FLOWER", "P-FLY"],
            priceIndex.FindExactMatches("S01", conflictCode).Select(item => item.ProductCode).Order());
    }

    private static SellableItemDto CreateItem(string productCode, decimal price = 1m)
    {
        return new SellableItemDto(
            "S01",
            productCode,
            ReferenceCode: null,
            DisplayName: $"{productCode} item",
            LookupCode: productCode,
            ItemNumber: productCode,
            Barcode: productCode,
            RetailPrice: price,
            PriceSourceKind.StoreRetailPrice,
            "store-retail",
            QuantityFactor: 1m,
            UpdatedAt: Timestamp);
    }

    private static CatalogPromotionRuleDto CreatePromotionRule()
    {
        return new CatalogPromotionRuleDto(
            "PROMO-001",
            "Quantity discount",
            IsExclusive: true,
            Priority: 10,
            ApplyQuantity: 2,
            FixedPrice: 15m,
            MaxApplicationsPerOrder: null,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1),
            DateTimeOffset.UtcNow,
            [new CatalogPromotionProductDto("SKU-001", UnitWeight: 1)]);
    }

    private sealed class CoordinatedCatalogSyncService : ILocalCatalogSyncService
    {
        private readonly TaskCompletionSource _releaseRegular = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeWrites;
        private int _regularCallCount;

        public TaskCompletionSource RegularStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondRegularStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ResetStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Calls { get; } = [];

        public int MaxActiveWrites { get; private set; }

        public bool RegularCanceled { get; private set; }

        public async Task<LocalCatalogSyncResult> FullSyncAsync(
            string storeCode,
            CancellationToken cancellationToken = default,
            IProgress<CatalogSyncProgress>? progress = null,
            bool forceFullDownload = false)
        {
            Calls.Add($"{storeCode}:{forceFullDownload.ToString().ToLowerInvariant()}");
            var activeWrites = Interlocked.Increment(ref _activeWrites);
            MaxActiveWrites = Math.Max(MaxActiveWrites, activeWrites);
            try
            {
                if (forceFullDownload)
                {
                    ResetStarted.SetResult();
                    return new LocalCatalogSyncResult(storeCode, 0, 1, 1, 0);
                }

                if (Interlocked.Increment(ref _regularCallCount) == 1)
                {
                    RegularStarted.SetResult();
                    try
                    {
                        await _releaseRegular.Task.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        RegularCanceled = true;
                        throw;
                    }
                }
                else
                {
                    SecondRegularStarted.SetResult();
                    progress?.Report(new CatalogSyncProgress(
                        storeCode,
                        CatalogSyncProgressStage.Comparing,
                        TotalCount: 0,
                        DownloadedCount: 0,
                        Percent: 0,
                        ComparePages: 1,
                        RemotePages: 0,
                        UpsertedCount: 0,
                        DeletedCount: 0,
                        ElapsedMilliseconds: 1)
                    {
                        ComparedCount = 1
                    });
                }

                return new LocalCatalogSyncResult(storeCode, 1, 1, 0, 0);
            }
            finally
            {
                Interlocked.Decrement(ref _activeWrites);
            }
        }

        public void ReleaseRegularIfNotCanceled()
        {
            _releaseRegular.TrySetResult();
        }

        /// <summary>等待超时或断言失败时输出假同步服务与相关任务的状态，便于从 CI 日志判断卡在哪一步。</summary>
        public string Describe(params Task[] tasks)
        {
            var taskStates = string.Join(", ", tasks.Select(task => task.Status));
            return $"calls=[{string.Join(", ", Calls)}] regularStarted={RegularStarted.Task.IsCompleted} " +
                   $"resetStarted={ResetStarted.Task.IsCompleted} regularCanceled={RegularCanceled} tasks=[{taskStates}]";
        }
    }

    private sealed class FixedResultCatalogSyncService : ILocalCatalogSyncService
    {
        public LocalCatalogSyncResult Result { get; set; } = new("S01", 0, 0, 0, 0);

        public Task<LocalCatalogSyncResult> FullSyncAsync(
            string storeCode,
            CancellationToken cancellationToken = default,
            IProgress<CatalogSyncProgress>? progress = null,
            bool forceFullDownload = false)
        {
            return Task.FromResult(Result);
        }
    }

    private sealed class CountingCatalogRepository : FakeLocalCatalogRepository
    {
        public int LoadCount { get; private set; }

        public override Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return base.LoadSellableItemsAsync(storeCode, cancellationToken);
        }
    }

    private sealed class FailingCatalogSyncService : ILocalCatalogSyncService
    {
        public Task<LocalCatalogSyncResult> FullSyncAsync(
            string storeCode,
            CancellationToken cancellationToken = default,
            IProgress<CatalogSyncProgress>? progress = null,
            bool forceFullDownload = false)
        {
            throw new HttpRequestException("network timeout");
        }
    }

    private sealed class RecordingProgress<T>(ConcurrentQueue<T> reports) : IProgress<T>
    {
        public void Report(T value)
        {
            reports.Enqueue(value);
        }
    }

    private class FakeLocalCatalogRepository : ILocalCatalogRepository
    {
        public IReadOnlyList<CatalogPromotionRuleDto> PromotionRules { get; set; } = [];

        public IReadOnlyList<SellableItemDto> Items { get; set; } = [CreateItem("RESET-ITEM")];

        public IReadOnlyList<SellableItemDto> CodeConflictItems { get; set; } = [];

        public Task<IReadOnlyList<SellableItemDto>> LoadCodeConflictItemsAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CodeConflictItems);
        }

        public Task<ILocalCatalogStoreReplaceSession> BeginStoreReplaceSessionAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ILocalCatalogStoreReplaceSession>(new FakeLocalCatalogStoreReplaceSession());
        }

        public Task ReplaceSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task UpsertSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<int> DeleteByLookupCodesAsync(
            string storeCode,
            IEnumerable<string> lookupCodes,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<SellableItemDto?> FindByLookupCodeAsync(
            string storeCode,
            string lookupCode,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SellableItemDto?>(null);
        }

        public Task<IReadOnlyList<SellableItemDto>> LoadSpecialProductItemsAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SellableItemDto>>([]);
        }

        public Task SaveSpecialProductOrderAsync(
            string storeCode,
            IEnumerable<string> productCodes,
            CancellationToken cancellationToken = default)
        {
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

        public virtual Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(CancellationToken cancellationToken = default)
        {
            return LoadSellableItemsAsync("S01", cancellationToken);
        }

        public virtual Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Items);
        }

        public Task ReplacePromotionRulesAsync(
            string storeCode,
            IEnumerable<CatalogPromotionRuleDto> rules,
            CancellationToken cancellationToken = default)
        {
            PromotionRules = rules.ToArray();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CatalogPromotionRuleDto>> LoadPromotionRulesAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(PromotionRules);
        }

        private sealed class FakeLocalCatalogStoreReplaceSession : ILocalCatalogStoreReplaceSession
        {
            public Task StageAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<LocalCatalogStoreReplaceCommitResult> CommitAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new LocalCatalogStoreReplaceCommitResult(0, 0));
            }

            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ThreadTrackingLocalCatalogRepository : FakeLocalCatalogRepository
    {
        public int LoadThreadId { get; private set; }

        public override Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(
            string storeCode,
            CancellationToken cancellationToken = default)
        {
            LoadThreadId = Environment.CurrentManagedThreadId;
            return base.LoadSellableItemsAsync(storeCode, cancellationToken);
        }
    }

    private sealed class SingleThreadSynchronizationContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

        public override void Post(SendOrPostCallback d, object? state)
        {
            _queue.Add((d, state));
        }

        public void RunOnCurrentThread()
        {
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }

        public void Complete()
        {
            _queue.CompleteAdding();
        }
    }
}
