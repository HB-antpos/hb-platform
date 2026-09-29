using Hbpos.Api.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Api.Tests;

public sealed class CatalogIndexCacheTests
{
    private static readonly DateTimeOffset GeneratedAt = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task GetOrBuildAsync_ReusesSameStoreIndexWithinTtl()
    {
        var timeProvider = new MutableTimeProvider(GeneratedAt);
        var cache = new CatalogIndexCache(timeProvider, TimeSpan.FromMinutes(2));
        var buildCount = 0;

        var first = await cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);
        var second = await cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, buildCount);

        Task<CatalogIndexBuildResult?> BuildAsync(CancellationToken _)
        {
            buildCount++;
            return Task.FromResult<CatalogIndexBuildResult?>(CreateResult("S01"));
        }
    }

    [Fact]
    public async Task GetOrBuildAsync_RebuildsAfterTtlExpires()
    {
        var timeProvider = new MutableTimeProvider(GeneratedAt);
        var cache = new CatalogIndexCache(timeProvider, TimeSpan.FromMinutes(2));
        var buildCount = 0;

        await cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromMinutes(3));
        await cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);

        Assert.Equal(2, buildCount);

        Task<CatalogIndexBuildResult?> BuildAsync(CancellationToken _)
        {
            buildCount++;
            return Task.FromResult<CatalogIndexBuildResult?>(CreateResult("S01"));
        }
    }

    [Fact]
    public async Task GetOrBuildAsync_ReusesPublishedVersionWhenContentIsUnchanged()
    {
        var timeProvider = new MutableTimeProvider(GeneratedAt);
        var cache = new CatalogIndexCache(timeProvider, TimeSpan.FromMinutes(2));
        var buildCount = 0;

        var first = await cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromMinutes(3));
        var rebuilt = await cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);

        Assert.Equal(2, buildCount);
        Assert.Equal("catalog-v1:build-1", first?.CatalogIndex.CatalogVersion);
        Assert.Equal(first?.CatalogIndex.CatalogVersion, rebuilt?.CatalogIndex.CatalogVersion);
        Assert.NotSame(first, rebuilt);

        Task<CatalogIndexBuildResult?> BuildAsync(CancellationToken _)
        {
            buildCount++;
            return Task.FromResult<CatalogIndexBuildResult?>(
                CreateResult(
                    "S01",
                    $"catalog-v1:build-{buildCount}",
                    contentMarker: "unchanged"));
        }
    }

    [Fact]
    public async Task GetOrBuildAsync_DoesNotOutliveSharedBaseDataValidity()
    {
        var timeProvider = new MutableTimeProvider(GeneratedAt);
        var cache = new CatalogIndexCache(timeProvider, TimeSpan.FromMinutes(20));
        var buildCount = 0;

        await cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromMinutes(2));
        await cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);

        Assert.Equal(2, buildCount);

        Task<CatalogIndexBuildResult?> BuildAsync(CancellationToken _)
        {
            buildCount++;
            return Task.FromResult<CatalogIndexBuildResult?>(
                CreateResult(
                    "S01",
                    $"catalog-v1:{buildCount}",
                    sourceValidUntil: GeneratedAt.AddMinutes(1)));
        }
    }

    [Fact]
    public async Task GetOrBuildAsync_StartsTtlAfterSuccessfulLongBuild()
    {
        var timeProvider = new MutableTimeProvider(GeneratedAt);
        var cache = new CatalogIndexCache(timeProvider, TimeSpan.FromMinutes(2));
        var buildCount = 0;

        var first = await cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);
        var immediatelyReused = await cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);

        Assert.Same(first, immediatelyReused);
        Assert.Equal(1, buildCount);

        timeProvider.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
        var rebuilt = await cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);
        var reused = await cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);

        Assert.NotSame(first, rebuilt);
        Assert.Same(rebuilt, reused);
        Assert.Equal(2, buildCount);

        Task<CatalogIndexBuildResult?> BuildAsync(CancellationToken _)
        {
            buildCount++;
            if (buildCount == 1)
            {
                // 模拟首次构建耗时超过 TTL，TTL 应从成功完成时才开始计算。
                timeProvider.Advance(TimeSpan.FromMinutes(3));
            }

            return Task.FromResult<CatalogIndexBuildResult?>(CreateResult("S01"));
        }
    }

    [Fact]
    public async Task GetOrBuildAsync_CacheHitDoesNotExtendCompletedEntryTtl()
    {
        var timeProvider = new MutableTimeProvider(GeneratedAt);
        var cache = new CatalogIndexCache(timeProvider, TimeSpan.FromMinutes(2));
        var buildCount = 0;

        var first = await cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(59));
        var cacheHit = await cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);

        Assert.Same(first, cacheHit);

        timeProvider.Advance(TimeSpan.FromSeconds(2));
        var rebuilt = await cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);
        var reused = await cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);

        Assert.NotSame(first, rebuilt);
        Assert.Same(rebuilt, reused);
        Assert.Equal(2, buildCount);

        Task<CatalogIndexBuildResult?> BuildAsync(CancellationToken _)
        {
            buildCount++;
            return Task.FromResult<CatalogIndexBuildResult?>(CreateResult("S01"));
        }
    }

    [Fact]
    public async Task GetOrBuildAsync_ReturnsLastGoodSnapshotWhileRefreshRuns()
    {
        var timeProvider = new MutableTimeProvider(GeneratedAt);
        var scheduler = new RecordingRefreshScheduler();
        var cache = CreateSnapshotCache(timeProvider, scheduler);
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = await cache.GetOrBuildAsync(
            "S01",
            since: null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(CreateResult("S01", "catalog-v1:old")),
            CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromMinutes(3));

        var stale = cache.GetOrBuildAsync("S01", since: null, RefreshAsync, CancellationToken.None);

        Assert.Same(first, await stale.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, scheduler.QueueCount);
        Assert.False(refreshStarted.Task.IsCompleted);

        var fresh = cache.GetOrBuildFreshAsync(
            "S01",
            since: null,
            RefreshAsync,
            CancellationToken.None);
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var secondStale = await cache.GetOrBuildAsync("S01", since: null, RefreshAsync, CancellationToken.None);
        Assert.Same(first, secondStale);

        allowRefresh.SetResult();
        var refreshed = await fresh;
        Assert.Equal("catalog-v1:new", refreshed?.CatalogIndex.CatalogVersion);

        async Task<CatalogIndexBuildResult?> RefreshAsync(CancellationToken cancellationToken)
        {
            refreshStarted.SetResult();
            await allowRefresh.Task.WaitAsync(cancellationToken);
            return CreateResult("S01", "catalog-v1:new");
        }
    }

    [Fact]
    public async Task ForceRefreshAndPublishAsync_RebuildsFreshEntryAndWaitsForDiskPublication()
    {
        var publishStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPublish = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new BlockingSnapshotStore(publishStarted, allowPublish);
        var cache = new CatalogIndexCache(store);
        var oldResult = CreateResult("S01", "catalog-v1:old", "old");
        var initialBuild = Task.Run(() => cache.GetOrBuildAsync(
            "S01",
            since: null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(oldResult),
            CancellationToken.None));
        await publishStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(initialBuild.IsCompleted);
        allowPublish.TrySetResult();
        await store.PublishCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var current = await initialBuild;
        Assert.Same(oldResult, current);

        store.ResetPublication();
        var allowBuild = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var newResult = CreateResult("S01", "catalog-v1:new", "new");
        var refresh = cache.ForceRefreshAndPublishAsync(
            "S01",
            since: null,
            async cancellationToken =>
            {
                await allowBuild.Task.WaitAsync(cancellationToken);
                return newResult;
            },
            CancellationToken.None);
        allowBuild.TrySetResult();
        await store.PublishStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(refresh.IsCompleted);
        var duringPublish = await cache.GetOrBuildAsync(
            "S01",
            since: null,
            _ => throw new InvalidOperationException("发布期间不应重复构建"),
            CancellationToken.None);
        Assert.Equal(oldResult.CatalogIndex.CatalogVersion, duringPublish?.CatalogIndex.CatalogVersion);

        store.AllowPublish.TrySetResult();
        Assert.Equal(
            newResult.CatalogIndex.CatalogVersion,
            (await refresh.WaitAsync(TimeSpan.FromSeconds(1)))?.CatalogIndex.CatalogVersion);
    }

    [Fact]
    public async Task CatalogIndexCache_RestoresPersistedSnapshotAsActiveEntry()
    {
        using var directory = new TemporaryDirectory();
        var now = DateTimeOffset.UtcNow;
        var store = new GzipCatalogSnapshotStore(directory.Path);
        store.Save(new CatalogPersistedSnapshot(
            "S01",
            Since: null,
            now,
            now.AddHours(1),
            "catalog-v1:restored",
            []));
        var scheduler = new RecordingRefreshScheduler();
        var cache = new CatalogIndexCache(store, scheduler);

        var restored = await cache.GetOrBuildAsync(
            "S01",
            since: null,
            _ => Task.FromException<CatalogIndexBuildResult?>(new InvalidOperationException("不应冷构建")),
            CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal("catalog-v1:restored", restored.CatalogIndex.CatalogVersion);
        Assert.Equal(1, scheduler.QueueCount);
    }

    [Fact]
    public async Task CatalogIndexCache_PersistsCodeConflictsAcrossRestart()
    {
        using var directory = new TemporaryDirectory();
        var store = new GzipCatalogSnapshotStore(directory.Path);
        var built = CreateResult("S01", "catalog-v1:conflicts", "P01");
        IReadOnlyList<SellableItemDto> conflicts =
        [
            built.SellableItems[0],
            built.SellableItems[0] with { ProductCode = "P02", DisplayName = "Other product", RetailPrice = 3m }
        ];
        var firstProcess = new CatalogIndexCache(store);
        await firstProcess.ForceRefreshAndPublishAsync(
            "S01",
            since: null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(built with { CodeConflicts = conflicts }),
            CancellationToken.None);

        // 模拟容器重启：新实例只能从磁盘快照恢复，不允许冷构建。
        var restartedProcess = new CatalogIndexCache(store, new RecordingRefreshScheduler());
        var restored = await restartedProcess.GetOrBuildAsync(
            "S01",
            since: null,
            _ => Task.FromException<CatalogIndexBuildResult?>(new InvalidOperationException("不应冷构建")),
            CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal("catalog-v1:conflicts", restored!.CatalogIndex.CatalogVersion);
        Assert.Equal(conflicts, restored.CodeConflicts);

        // 按固定版本读取（iPad 分页）同样从磁盘加载并常驻，之后的普通读取可能直接复用它。
        var pinnedProcess = new CatalogIndexCache(store, new RecordingRefreshScheduler());
        var pinned = pinnedProcess.GetByVersion("S01", since: null, "catalog-v1:conflicts");
        Assert.NotNull(pinned);
        Assert.Equal(conflicts, pinned!.CodeConflicts);
    }

    [Fact]
    public void Restored_snapshot_without_raw_candidates_refuses_legacy_since_projection()
    {
        using var directory = new TemporaryDirectory();
        var now = DateTimeOffset.UtcNow;
        var store = new GzipCatalogSnapshotStore(directory.Path);
        store.Save(new CatalogPersistedSnapshot(
            "S01", null, now, now.AddHours(1), "catalog-v1:restored",
            [new SellableItemDto("S01", "P01", null, "Item", "ITEM", null, null, 1m,
                PriceSourceKind.ProductBase, "product", 1m, now)]));
        var cache = new CatalogIndexCache(store);

        Assert.Throws<CatalogSnapshotExpiredException>(() =>
            cache.GetByVersion("S01", now.AddMinutes(-1), "catalog-v1:restored"));
    }

    [Fact]
    public async Task InvalidateStore_ReturnsPinnedSnapshotWhileItRefreshes()
    {
        var scheduler = new RecordingRefreshScheduler();
        var cache = CreateSnapshotCache(new MutableTimeProvider(GeneratedAt), scheduler);
        var first = await cache.GetOrBuildAsync(
            "S01",
            since: null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(CreateResult("S01", "catalog-v1:old")),
            CancellationToken.None);
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        cache.InvalidateStore("S01");
        var stale = await cache.GetOrBuildAsync("S01", since: null, RefreshAsync, CancellationToken.None);

        Assert.Same(first, stale);
        Assert.Equal(1, scheduler.QueueCount);
        Assert.False(refreshStarted.Task.IsCompleted);

        var fresh = cache.GetOrBuildFreshAsync(
            "S01",
            since: null,
            RefreshAsync,
            CancellationToken.None);
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(fresh.IsCompleted);
        allowRefresh.SetResult();
        Assert.Equal("catalog-v1:new", (await fresh)!.CatalogIndex.CatalogVersion);

        async Task<CatalogIndexBuildResult?> RefreshAsync(CancellationToken cancellationToken)
        {
            refreshStarted.SetResult();
            await allowRefresh.Task.WaitAsync(cancellationToken);
            return CreateResult("S01", "catalog-v1:new");
        }
    }

    [Fact]
    public async Task CatalogIndexCache_RestoresNewestVersionAsActiveEntry()
    {
        using var directory = new TemporaryDirectory();
        var now = DateTimeOffset.UtcNow;
        var store = new GzipCatalogSnapshotStore(directory.Path);
        store.Save(new CatalogPersistedSnapshot("S01", null, now.AddMinutes(-1), now.AddHours(1), "catalog-v1:old", []));
        store.Save(new CatalogPersistedSnapshot("S01", null, now, now.AddHours(1), "catalog-v1:new", []));
        var scheduler = new RecordingRefreshScheduler();
        var cache = new CatalogIndexCache(store, scheduler);

        var restored = await cache.GetOrBuildAsync(
            "S01",
            since: null,
            _ => Task.FromException<CatalogIndexBuildResult?>(new InvalidOperationException("不应冷构建")),
            CancellationToken.None);

        Assert.Equal("catalog-v1:new", restored?.CatalogIndex.CatalogVersion);
        Assert.Equal(1, scheduler.QueueCount);
    }

    [Fact]
    public async Task Lazy_snapshot_load_skips_a_corrupt_newer_version_and_uses_the_last_good_one()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new RecordingSnapshotStore(
            [
                new CatalogSnapshotDescriptor("S01", null, now, now.AddHours(1), "catalog-v1:new"),
                new CatalogSnapshotDescriptor("S01", null, now.AddMinutes(-1), now.AddHours(1), "catalog-v1:old")
            ],
            version => version == "catalog-v1:new"
                ? throw new InvalidDataException("corrupt")
                : new CatalogPersistedSnapshot("S01", null, now.AddMinutes(-1), now.AddHours(1), version, []));
        var cache = new CatalogIndexCache(store, new RecordingRefreshScheduler());

        var result = await cache.GetOrBuildAsync("S01", null,
            _ => Task.FromException<CatalogIndexBuildResult?>(new InvalidOperationException("不应冷构建")),
            CancellationToken.None);

        Assert.Equal("catalog-v1:old", result!.CatalogIndex.CatalogVersion);
    }

    [Fact]
    public void Refresh_due_lazy_descriptor_remains_available_as_last_good()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new RecordingSnapshotStore(
            [new CatalogSnapshotDescriptor("S01", null, now.AddHours(-2), now.AddMinutes(-1), "catalog-v1:expired")],
            version => new CatalogPersistedSnapshot(
                "S01",
                null,
                now.AddHours(-2),
                now.AddMinutes(-1),
                version,
                []));
        var cache = new CatalogIndexCache(store);

        var restored = cache.GetByVersion("S01", null, "catalog-v1:expired");

        Assert.Equal("catalog-v1:expired", restored?.CatalogIndex.CatalogVersion);
    }

    [Fact]
    public async Task GetOrBuildAsync_DoesNotReuseAcrossStores()
    {
        var timeProvider = new MutableTimeProvider(GeneratedAt);
        var cache = new CatalogIndexCache(timeProvider, TimeSpan.FromMinutes(2));
        var buildCount = 0;

        await cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);
        await cache.GetOrBuildAsync("S02", since: null, BuildAsync, CancellationToken.None);

        Assert.Equal(2, buildCount);

        Task<CatalogIndexBuildResult?> BuildAsync(CancellationToken _)
        {
            buildCount++;
            return Task.FromResult<CatalogIndexBuildResult?>(CreateResult("S01"));
        }
    }

    [Fact]
    public async Task GetOrBuildAsync_LegacySinceDerivesFromTheSameStoreArtifact()
    {
        var cache = new CatalogIndexCache(new MutableTimeProvider(GeneratedAt), TimeSpan.FromMinutes(2));
        var buildCount = 0;
        var items = new SellableItemDto[]
        {
            new("S01", "P-OLD", null, "Old", "OLD", null, null, 1m,
                PriceSourceKind.ProductBase, "product", 1m, GeneratedAt.AddMinutes(-10)),
            new("S01", "P-NEW", null, "New", "NEW", null, null, 1m,
                PriceSourceKind.ProductBase, "product", 1m, GeneratedAt)
        };

        var full = await cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);
        var incremental = await cache.GetOrBuildAsync(
            "S01",
            since: GeneratedAt.AddMinutes(-1),
            BuildAsync,
            CancellationToken.None);

        Assert.Equal(1, buildCount);
        Assert.Equal(["OLD", "NEW"], full!.SellableItems.Select(item => item.LookupCode));
        Assert.Equal(["NEW"], incremental!.SellableItems.Select(item => item.LookupCode));

        Task<CatalogIndexBuildResult?> BuildAsync(CancellationToken _)
        {
            buildCount++;
            return Task.FromResult<CatalogIndexBuildResult?>(new CatalogIndexBuildResult(
                "S01",
                GeneratedAt,
                items,
                new CatalogSellableIndex("S01", GeneratedAt, items, "catalog-v1:full")));
        }
    }

    [Fact]
    public async Task GetOrBuildAsync_LegacySinceDerivationsUseLruCapacityEight()
    {
        var cache = new CatalogIndexCache(new MutableTimeProvider(GeneratedAt), TimeSpan.FromMinutes(2));
        var buildCount = 0;
        var item = new SellableItemDto(
            "S01", "P01", null, "Item", "ITEM", null, null, 1m,
            PriceSourceKind.ProductBase, "product", 1m, GeneratedAt);

        Task<CatalogIndexBuildResult?> BuildAsync(CancellationToken _)
        {
            buildCount++;
            return Task.FromResult<CatalogIndexBuildResult?>(new CatalogIndexBuildResult(
                "S01",
                GeneratedAt,
                [item],
                new CatalogSellableIndex("S01", GeneratedAt, [item], "catalog-v1:full")));
        }

        var oldest = await cache.GetOrBuildAsync(
            "S01", GeneratedAt.AddMinutes(-1), BuildAsync, CancellationToken.None);
        for (var minute = 2; minute <= 9; minute++)
        {
            _ = await cache.GetOrBuildAsync(
                "S01", GeneratedAt.AddMinutes(-minute), BuildAsync, CancellationToken.None);
        }

        var rebuiltOldest = await cache.GetOrBuildAsync(
            "S01", GeneratedAt.AddMinutes(-1), BuildAsync, CancellationToken.None);

        Assert.Equal(1, buildCount);
        Assert.NotSame(oldest, rebuiltOldest);
    }

    [Fact]
    public async Task GetOrBuildAsync_CoalescesConcurrentBuildsForSameStore()
    {
        var timeProvider = new MutableTimeProvider(GeneratedAt);
        var cache = new CatalogIndexCache(timeProvider, TimeSpan.FromMinutes(2));
        var buildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowBuildToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var buildCount = 0;

        var first = cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);
        await buildStarted.Task;
        var second = cache.GetOrBuildAsync(" s01 ", since: null, BuildAsync, CancellationToken.None);

        allowBuildToFinish.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Same(results[0], results[1]);
        Assert.Equal(1, buildCount);

        async Task<CatalogIndexBuildResult?> BuildAsync(CancellationToken _)
        {
            buildCount++;
            buildStarted.SetResult();
            await allowBuildToFinish.Task;
            return CreateResult("S01");
        }
    }

    [Fact]
    public async Task GetOrBuildAsync_WaiterCancellationDoesNotRemoveRunningSharedBuild()
    {
        var cache = new CatalogIndexCache(new MutableTimeProvider(GeneratedAt), TimeSpan.FromMinutes(2));
        var buildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowBuildToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var waiterCancellation = new CancellationTokenSource();
        var buildCount = 0;

        var owner = cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);
        await buildStarted.Task;
        var waiter = cache.GetOrBuildAsync("S01", since: null, BuildAsync, waiterCancellation.Token);

        waiterCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.False(owner.IsCompleted);

        allowBuildToFinish.SetResult();
        var completed = await owner;
        var cached = await cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);

        Assert.Same(completed, cached);
        Assert.Equal(1, buildCount);

        async Task<CatalogIndexBuildResult?> BuildAsync(CancellationToken cancellationToken)
        {
            buildCount++;
            buildStarted.SetResult();
            await allowBuildToFinish.Task.WaitAsync(cancellationToken);
            return CreateResult("S01");
        }
    }

    [Fact]
    public async Task GetOrBuildFreshAsync_FirstWaiterCancellationDoesNotCancelSharedBuild()
    {
        var cache = new CatalogIndexCache(
            new MutableTimeProvider(GeneratedAt),
            TimeSpan.FromMinutes(2));
        var buildStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstWaiterCancellation = new CancellationTokenSource();
        var allowBuildToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var buildCount = 0;

        var first = cache.GetOrBuildFreshAsync(
            "S01",
            since: null,
            async cancellationToken =>
            {
                buildCount++;
                Assert.False(cancellationToken.CanBeCanceled);
                buildStarted.TrySetResult();
                await allowBuildToFinish.Task.WaitAsync(cancellationToken);
                return CreateResult("S01");
            },
            firstWaiterCancellation.Token);
        await buildStarted.Task;

        firstWaiterCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var second = cache.GetOrBuildFreshAsync(
            "S01",
            since: null,
            _ => throw new InvalidOperationException("不应启动第二个共享构建"),
            CancellationToken.None);
        allowBuildToFinish.SetResult();

        Assert.NotNull(await second);
        Assert.Equal(1, buildCount);
    }

    [Fact]
    public async Task GetOrBuildAsync_RebuildsAfterBuildFaults()
    {
        var cache = new CatalogIndexCache(new MutableTimeProvider(GeneratedAt), TimeSpan.FromMinutes(2));
        var buildCount = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None));
        var rebuilt = await cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);

        Assert.NotNull(rebuilt);
        Assert.Equal(2, buildCount);

        Task<CatalogIndexBuildResult?> BuildAsync(CancellationToken _)
        {
            buildCount++;
            return buildCount == 1
                ? Task.FromException<CatalogIndexBuildResult?>(new InvalidOperationException("构建失败"))
                : Task.FromResult<CatalogIndexBuildResult?>(CreateResult("S01"));
        }
    }

    [Fact]
    public async Task GetOrBuildAsync_RebuildsAfterBuildReturnsNull()
    {
        var cache = new CatalogIndexCache(new MutableTimeProvider(GeneratedAt), TimeSpan.FromMinutes(2));
        var buildCount = 0;

        var first = await cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);
        var rebuilt = await cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);

        Assert.Null(first);
        Assert.NotNull(rebuilt);
        Assert.Equal(2, buildCount);

        Task<CatalogIndexBuildResult?> BuildAsync(CancellationToken _)
        {
            buildCount++;
            return Task.FromResult(buildCount == 1 ? null : CreateResult("S01"));
        }
    }

    [Fact]
    public async Task InvalidateStore_DuringBuildPreventsOldOwnerFromPublishingPinnedSnapshot()
    {
        var cache = new CatalogIndexCache(new MutableTimeProvider(GeneratedAt), TimeSpan.FromMinutes(2));
        var buildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowBuildToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var buildCount = 0;

        var oldOwner = cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);
        await buildStarted.Task;

        cache.InvalidateStore("S01");
        allowBuildToFinish.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => oldOwner);
        var rebuilt = await cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);
        var cached = await cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);

        Assert.Same(rebuilt, cached);
        Assert.Equal(2, buildCount);

        async Task<CatalogIndexBuildResult?> BuildAsync(CancellationToken cancellationToken)
        {
            buildCount++;
            if (buildCount == 1)
            {
                buildStarted.SetResult();
                await allowBuildToFinish.Task.WaitAsync(cancellationToken);
            }

            return CreateResult("S01");
        }
    }

    [Fact]
    public async Task GetOrBuildAsync_OwnerCancellationKeepsSharedBuildAliveUntilCompletion()
    {
        var cache = new CatalogIndexCache(new MutableTimeProvider(GeneratedAt), TimeSpan.FromMinutes(2));
        var buildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowBuildToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstCancellation = new CancellationTokenSource();
        var buildCount = 0;

        var first = cache.GetOrBuildAsync("S01", since: null, BuildAsync, firstCancellation.Token);
        await buildStarted.Task;
        var second = cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);

        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        allowBuildToFinish.SetResult();

        var completed = await second;
        var cached = await cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);

        Assert.Same(completed, cached);
        Assert.Equal(1, buildCount);

        async Task<CatalogIndexBuildResult?> BuildAsync(CancellationToken cancellationToken)
        {
            buildCount++;
            buildStarted.SetResult();
            await allowBuildToFinish.Task.WaitAsync(cancellationToken);
            return CreateResult("S01");
        }
    }

    [Fact]
    public async Task InvalidateStore_KeepsCompletedSnapshotAvailableByVersion()
    {
        var cache = CreateSnapshotCache(new MutableTimeProvider(GeneratedAt));
        var completed = await cache.GetOrBuildAsync(
            "S01",
            since: null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(CreateResult("S01")),
            CancellationToken.None);

        cache.InvalidateStore("S01");
        var pinned = cache.GetByVersion(
            "S01",
            since: null,
            completed!.CatalogIndex.CatalogVersion);

        Assert.Same(completed, pinned);
    }

    [Fact]
    public async Task InvalidateStore_DuringBuildDoesNotPinInvalidatedSnapshot()
    {
        var cache = CreateSnapshotCache(new MutableTimeProvider(GeneratedAt));
        var buildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowBuildToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var owner = cache.GetOrBuildAsync("S01", since: null, BuildAsync, CancellationToken.None);
        await buildStarted.Task;
        cache.InvalidateStore("S01");
        allowBuildToFinish.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner);

        var pinned = cache.GetByVersion(
            "S01",
            since: null,
            CreateResult("S01").CatalogIndex.CatalogVersion);

        Assert.Null(pinned);

        async Task<CatalogIndexBuildResult?> BuildAsync(CancellationToken cancellationToken)
        {
            buildStarted.SetResult();
            await allowBuildToFinish.Task.WaitAsync(cancellationToken);
            return CreateResult("S01");
        }
    }

    [Fact]
    public async Task GetByVersion_KeepsLastGoodSnapshotAfterRefreshTime()
    {
        var timeProvider = new MutableTimeProvider(GeneratedAt);
        var cache = CreateSnapshotCache(timeProvider);
        var completed = await cache.GetOrBuildAsync(
            "S01",
            since: null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(CreateResult("S01")),
            CancellationToken.None);
        var version = completed!.CatalogIndex.CatalogVersion;

        timeProvider.Advance(TimeSpan.FromMinutes(29) + TimeSpan.FromSeconds(59));
        Assert.Same(completed, cache.GetByVersion("S01", since: null, version));

        timeProvider.Advance(TimeSpan.FromSeconds(2));
        Assert.Same(completed, cache.GetByVersion("S01", since: null, version));
    }

    [Fact]
    public async Task CompletedSnapshots_KeepOnlyNewestTwoVersionsPerStore()
    {
        var cache = CreateSnapshotCache(new MutableTimeProvider(GeneratedAt));
        var completed = new List<CatalogIndexBuildResult>();

        for (var number = 1; number <= 3; number++)
        {
            var version = $"catalog-v1:{number}";
            var result = await cache.GetOrBuildAsync(
                "S01",
                since: null,
                _ => Task.FromResult<CatalogIndexBuildResult?>(
                    CreateResult("S01", version)),
                CancellationToken.None);

            completed.Add(result!);
            cache.InvalidateStore("S01");
        }

        Assert.Null(cache.GetByVersion("S01", since: null, "catalog-v1:1"));
        Assert.Same(completed[1], cache.GetByVersion("S01", since: null, "catalog-v1:2"));
        Assert.Same(completed[2], cache.GetByVersion("S01", since: null, "catalog-v1:3"));
    }

    [Fact]
    public async Task CompletedSnapshotLimit_IsIndependentPerStore()
    {
        var cache = CreateSnapshotCache(new MutableTimeProvider(GeneratedAt));
        var s01 = await cache.GetOrBuildAsync(
            "S01",
            since: null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(CreateResult("S01", "catalog-v1:s01")),
            CancellationToken.None);
        var s02 = await cache.GetOrBuildAsync(
            "S02",
            since: null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(CreateResult("S02", "catalog-v1:s02")),
            CancellationToken.None);

        cache.InvalidateStore("S01");
        cache.InvalidateStore("S02");

        Assert.Same(s01, cache.GetByVersion("S01", since: null, "catalog-v1:s01"));
        Assert.Same(s02, cache.GetByVersion("S02", since: null, "catalog-v1:s02"));
    }

    [Fact]
    public async Task LegacySince_DoesNotPublishASecondSnapshotForTheSameStore()
    {
        var cache = CreateSnapshotCache(new MutableTimeProvider(GeneratedAt));
        var first = cache.GetOrBuildAsync(
            "S01",
            since: null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(
                CreateResult("S01", "catalog-v1:full")),
            CancellationToken.None);
        var secondSince = GeneratedAt.AddMinutes(-1);
        var second = cache.GetOrBuildAsync(
            "S01",
            secondSince,
            _ => Task.FromResult<CatalogIndexBuildResult?>(
                CreateResult("S01", "catalog-v1:delta")),
            CancellationToken.None);

        var results = await Task.WhenAll(first, second);

        var full = Assert.IsType<CatalogIndexBuildResult>(results[0]);
        var legacySince = Assert.IsType<CatalogIndexBuildResult>(results[1]);
        Assert.Equal("catalog-v1:full", full.CatalogIndex.CatalogVersion);
        Assert.NotEqual(full.CatalogIndex.CatalogVersion, legacySince.CatalogIndex.CatalogVersion);
        Assert.Same(
            full,
            cache.GetByVersion("S01", since: null, "catalog-v1:full"));
        Assert.Null(cache.GetByVersion("S01", since: null, legacySince.CatalogIndex.CatalogVersion));
    }

    [Fact]
    public async Task LegacySince_FiltersSourceCandidatesBeforeResolvingLookupPriority()
    {
        var cache = new CatalogIndexCache(new MutableTimeProvider(GeneratedAt), TimeSpan.FromMinutes(2));
        var since = GeneratedAt.AddMinutes(-5);
        var input = new PriceIndexInput(
            Since: null,
            Products:
            [
                new ProductPriceRecord("P01", "新基础价", null, "X", 10m, GeneratedAt)
            ],
            StoreRetailPrices: [],
            StoreMultiCodeProducts: [],
            StoreClearancePrices:
            [
                // 清仓价优先级更高，但已经早于 since，不能继续遮蔽新的基础价。
                new StoreClearancePriceRecord("P01", "X", 5m, GeneratedAt.AddMinutes(-10))
            ],
            ProductSetCodes: []);
        var fullItems = new PriceIndexBuilder().Build("S01", input);
        var buildCount = 0;

        var full = await cache.GetOrBuildAsync(
            "S01",
            since: null,
            _ =>
            {
                buildCount++;
                return Task.FromResult<CatalogIndexBuildResult?>(new CatalogIndexBuildResult(
                    "S01",
                    GeneratedAt,
                    fullItems,
                    new CatalogSellableIndex("S01", GeneratedAt, fullItems, "catalog-v1:full"),
                    RawPriceIndexInput: input));
            },
            CancellationToken.None);
        var legacy = await cache.GetOrBuildAsync(
            "S01",
            since,
            _ => throw new InvalidOperationException("legacy since 不应触发第二次数据库构建"),
            CancellationToken.None);

        Assert.Equal(1, buildCount);
        Assert.Equal(PriceSourceKind.StoreClearancePrice, Assert.Single(full!.SellableItems).PriceSource);
        var item = Assert.Single(legacy!.SellableItems);
        Assert.Equal(PriceSourceKind.ProductBase, item.PriceSource);
        Assert.Equal(10m, item.RetailPrice);
    }

    [Fact]
    public async Task LegacySince_rebuilds_raw_artifact_after_global_lru_eviction()
    {
        var cache = new CatalogIndexCache(new MutableTimeProvider(GeneratedAt), TimeSpan.FromMinutes(20));
        var since = GeneratedAt.AddMinutes(-5);
        var builds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var storeCode in new[] { "S01", "S02", "S03" })
        {
            await cache.GetOrBuildAsync(storeCode, null, token => Build(storeCode, token), CancellationToken.None);
        }

        var legacy = await cache.GetOrBuildAsync("S01", since, token => Build("S01", token), CancellationToken.None);

        Assert.Equal(2, builds["S01"]);
        Assert.Equal(1, builds["S02"]);
        Assert.Equal(1, builds["S03"]);
        Assert.Equal(PriceSourceKind.ProductBase, Assert.Single(legacy!.SellableItems).PriceSource);

        Task<CatalogIndexBuildResult?> Build(string storeCode, CancellationToken _)
        {
            builds[storeCode] = builds.GetValueOrDefault(storeCode) + 1;
            var input = new PriceIndexInput(
                Since: null,
                Products: [new ProductPriceRecord("P01", "Base", null, "X", 10m, GeneratedAt)],
                StoreRetailPrices: [],
                StoreMultiCodeProducts: [],
                StoreClearancePrices: [new StoreClearancePriceRecord("P01", "X", 5m, GeneratedAt.AddMinutes(-10))],
                ProductSetCodes: []);
            var items = new PriceIndexBuilder().Build(storeCode, input);
            return Task.FromResult<CatalogIndexBuildResult?>(new CatalogIndexBuildResult(
                storeCode, GeneratedAt, items,
                new CatalogSellableIndex(storeCode, GeneratedAt, items, $"catalog-v1:{storeCode}"),
                RawPriceIndexInput: input));
        }
    }

    [Fact]
    public async Task LegacySince_DoesNotEvictTheSingleStoreArtifact()
    {
        var cache = CreateSnapshotCache(new MutableTimeProvider(GeneratedAt));
        var buildCount = 0;
        var first = await cache.GetOrBuildAsync(
            "S01",
            since: null,
            _ => Build("catalog-v1:active"),
            CancellationToken.None);
        await cache.GetOrBuildAsync(
            "S01",
            GeneratedAt.AddMinutes(-1),
            _ => Build("catalog-v1:second"),
            CancellationToken.None);
        await cache.GetOrBuildAsync(
            "S01",
            GeneratedAt.AddMinutes(-2),
            _ => Build("catalog-v1:third"),
            CancellationToken.None);

        var activeAgain = await cache.GetOrBuildAsync(
            "S01",
            since: null,
            _ => Build("catalog-v1:unexpected"),
            CancellationToken.None);

        Assert.Same(first, activeAgain);
        Assert.Equal(1, buildCount);
        Assert.Same(first, cache.GetByVersion("S01", since: null, "catalog-v1:active"));

        Task<CatalogIndexBuildResult?> Build(string version)
        {
            buildCount++;
            return Task.FromResult<CatalogIndexBuildResult?>(
                CreateResult("S01", version));
        }
    }

    [Fact]
    public void Plan_capacity_counts_an_unpinned_target_and_rejects_it_at_the_hard_limit()
    {
        var cache = new CatalogIndexCache(
            new MutableTimeProvider(GeneratedAt),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(30),
            maxSnapshotsPerStore: 8,
            softItemCapacity: 2,
            hardItemCapacity: 3);

        cache.EnsurePlanCapacity(CreateSizedResult("S01", "catalog-v1:fits", 3));
        Assert.Throws<CatalogCapacityBusyException>(() =>
            cache.EnsurePlanCapacity(CreateSizedResult("S01", "catalog-v1:too-large", 4)));
    }

    [Fact]
    public async Task Plan_capacity_preserves_pinned_target_and_existing_lease_when_rejecting_a_new_plan()
    {
        var cache = new CatalogIndexCache(new MutableTimeProvider(GeneratedAt), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30), 8, 1, 3);
        var existing = CreateSizedResult("S01", "catalog-v1:existing", 2);
        await cache.GetOrBuildAsync("S01", null, _ => Task.FromResult<CatalogIndexBuildResult?>(existing), CancellationToken.None);
        await Task.Yield();
        cache.EnsurePlanCapacity(existing);
        Assert.NotNull(cache.GetByVersion("S01", null, "catalog-v1:existing"));

        var lease = cache.DownloadLeases.CreateFull(existing);
        Assert.Throws<CatalogCapacityBusyException>(() =>
            cache.EnsurePlanCapacity(CreateSizedResult("S02", "catalog-v1:new", 2)));
        Assert.Same(existing, cache.DownloadLeases.GetAndTouch(lease.LeaseId, "S01", null, "catalog-v1:existing").Target);
    }

    [Fact]
    public async Task Atomic_lease_admission_allows_only_one_concurrent_unpinned_target()
    {
        var cache = new CatalogIndexCache(new MutableTimeProvider(GeneratedAt), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30), 8, 1, 2);
        var attempts = await Task.WhenAll(
            Task.Run(() => TryCreate("S01", "catalog-v1:one")),
            Task.Run(() => TryCreate("S02", "catalog-v1:two")));

        Assert.Equal(1, attempts.Count(lease => lease is not null));

        CatalogDownloadLease? TryCreate(string storeCode, string version)
        {
            try
            {
                return cache.CreateFullLease(CreateSizedResult(storeCode, version, 2));
            }
            catch (CatalogCapacityBusyException)
            {
                return null;
            }
        }
    }

    [Fact]
    public void Delta_lease_admission_counts_unpinned_baseline_and_target_together()
    {
        var cache = new CatalogIndexCache(new MutableTimeProvider(GeneratedAt), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(30), 8, 1, 3);

        Assert.Throws<CatalogCapacityBusyException>(() => cache.CreateDeltaLease(
            CreateSizedResult("S01", "catalog-v1:base", 2),
            CreateSizedResult("S01", "catalog-v1:target", 2),
            []));
    }

    [Fact]
    public async Task Oversized_normal_build_is_not_published_and_rebuilds()
    {
        var cache = new CatalogIndexCache(new MutableTimeProvider(GeneratedAt), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(30), 8, 1, 3);
        var builds = 0;

        await Assert.ThrowsAsync<CatalogCapacityBusyException>(() => cache.GetOrBuildAsync("S01", null, _ =>
        {
            builds++;
            return Task.FromResult<CatalogIndexBuildResult?>(CreateSizedResult("S01", "catalog-v1:oversized", 4));
        }, CancellationToken.None));
        await Task.Yield();

        Assert.Null(cache.GetByVersion("S01", null, "catalog-v1:oversized"));
        Assert.Equal(0, cache.PinnedItemCountForTests);

        await Assert.ThrowsAsync<CatalogCapacityBusyException>(() => cache.GetOrBuildAsync("S01", null, _ =>
        {
            builds++;
            return Task.FromResult<CatalogIndexBuildResult?>(CreateSizedResult("S01", "catalog-v1:oversized", 4));
        }, CancellationToken.None));
        Assert.Equal(2, builds);
    }

    [Fact]
    public async Task Evicting_unread_store_version_removes_its_active_entry()
    {
        var cache = new CatalogIndexCache(
            new MutableTimeProvider(GeneratedAt),
            TimeSpan.FromMinutes(20),
            TimeSpan.FromMinutes(30),
            maxSnapshotsPerStore: 8,
            softItemCapacity: 1,
            hardItemCapacity: 1);
        var first = CreateSizedResult("S01", "catalog-v1:first", 1);
        var second = CreateSizedResult("S02", "catalog-v1:second", 1);

        await cache.GetOrBuildAsync("S01", null, _ => Task.FromResult<CatalogIndexBuildResult?>(first), CancellationToken.None);
        // 先前门店发布后不再读取它，模拟全门店预构建时的真实访问顺序。
        await cache.GetOrBuildAsync("S02", null, _ => Task.FromResult<CatalogIndexBuildResult?>(second), CancellationToken.None);

        Assert.Equal(1, cache.PinnedVersionCountForTests);
        Assert.Equal(1, cache.ActiveEntryCountForTests);
        Assert.Null(cache.GetByVersion("S01", null, first.CatalogIndex.CatalogVersion));
        Assert.Same(second, cache.GetByVersion("S02", null, second.CatalogIndex.CatalogVersion));
    }

    [Fact]
    public async Task Persisted_store_evicted_for_capacity_can_be_loaded_repeatedly_without_sql_build()
    {
        using var directory = new TemporaryDirectory();
        var store = new GzipCatalogSnapshotStore(directory.Path);
        var scheduler = new RecordingRefreshScheduler();
        var cache = new CatalogIndexCache(
            new MutableTimeProvider(GeneratedAt), TimeSpan.FromMinutes(20), TimeSpan.FromHours(2),
            maxSnapshotsPerStore: 8, store, scheduler, softItemCapacity: 1, hardItemCapacity: 1);
        var first = CreateSizedResult("S01", "catalog-v1:first", 1);
        var second = CreateSizedResult("S02", "catalog-v1:second", 1);
        await cache.GetOrBuildAsync("S01", null, _ => Task.FromResult<CatalogIndexBuildResult?>(first), CancellationToken.None);
        await cache.GetOrBuildAsync("S02", null, _ => Task.FromResult<CatalogIndexBuildResult?>(second), CancellationToken.None);

        for (var round = 0; round < 2; round++)
        {
            Assert.Equal(first.CatalogIndex.CatalogVersion,
                cache.GetByVersion("S01", null, first.CatalogIndex.CatalogVersion)?.CatalogIndex.CatalogVersion);
            var recoveredFirst = await cache.GetOrBuildAsync("S01", null,
                _ => throw new InvalidOperationException("磁盘 LKG 不应触发 SQL 构建"), CancellationToken.None);
            Assert.Equal(first.CatalogIndex.CatalogVersion, recoveredFirst?.CatalogIndex.CatalogVersion);

            Assert.Equal(second.CatalogIndex.CatalogVersion,
                cache.GetByVersion("S02", null, second.CatalogIndex.CatalogVersion)?.CatalogIndex.CatalogVersion);
            var recoveredSecond = await cache.GetOrBuildAsync("S02", null,
                _ => throw new InvalidOperationException("磁盘 LKG 不应触发 SQL 构建"), CancellationToken.None);
            Assert.Equal(second.CatalogIndex.CatalogVersion, recoveredSecond?.CatalogIndex.CatalogVersion);
        }

        // 磁盘恢复仅提供 LKG，不能把它装成未过期的 active：每次仍排后台刷新。
        Assert.Equal(4, scheduler.QueueCount);
        Assert.Equal(1, cache.PinnedVersionCountForTests);
        Assert.Equal(0, cache.ActiveEntryCountForTests);
    }

    [Fact]
    public async Task Evicted_descriptor_does_not_resurrect_version_removed_from_manifest()
    {
        using var directory = new TemporaryDirectory();
        var store = new GzipCatalogSnapshotStore(directory.Path, maxSnapshotsPerStore: 1);
        var cache = new CatalogIndexCache(
            new MutableTimeProvider(GeneratedAt), TimeSpan.FromMinutes(20), TimeSpan.FromHours(2),
            maxSnapshotsPerStore: 8, store, softItemCapacity: 1, hardItemCapacity: 1);
        var first = CreateSizedResult("S01", "catalog-v1:first", 1);
        var second = CreateSizedResult("S02", "catalog-v1:second", 1);
        await cache.GetOrBuildAsync("S01", null, _ => Task.FromResult<CatalogIndexBuildResult?>(first), CancellationToken.None);
        await cache.GetOrBuildAsync("S02", null, _ => Task.FromResult<CatalogIndexBuildResult?>(second), CancellationToken.None);
        Assert.Equal(1, cache.PinnedVersionCountForTests);

        // 模拟同一进程外部维护 manifest：旧版本已不在发布点，轻量索引必须失效。
        store.Save(new CatalogPersistedSnapshot("S01", null, GeneratedAt.AddMinutes(1),
            GeneratedAt.AddHours(2), "catalog-v1:replacement", []));
        Assert.Null(store.Load("S01", null, first.CatalogIndex.CatalogVersion));
        Assert.Null(cache.GetByVersion("S01", null, first.CatalogIndex.CatalogVersion));
        Assert.Null(cache.GetByVersion("S01", null, first.CatalogIndex.CatalogVersion));
    }

    [Fact]
    public async Task Reading_older_version_does_not_replace_newer_disk_lkg()
    {
        using var directory = new TemporaryDirectory();
        var scheduler = new RecordingRefreshScheduler();
        var cache = new CatalogIndexCache(
            new MutableTimeProvider(GeneratedAt), TimeSpan.FromMinutes(20), TimeSpan.FromHours(2),
            maxSnapshotsPerStore: 8, new GzipCatalogSnapshotStore(directory.Path), scheduler,
            softItemCapacity: 2, hardItemCapacity: 2);
        // 两版生成时间相同，必须按原发布顺序判断，不能按最近从磁盘读取的顺序判断。
        var old = CreateResult("S01", "catalog-v1:old");
        var current = CreateResult("S01", "catalog-v1:current");
        await cache.GetOrBuildAsync("S01", null, _ => Task.FromResult<CatalogIndexBuildResult?>(old), CancellationToken.None);
        await cache.ForceRefreshAndPublishAsync("S01", null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(current), CancellationToken.None);
        await cache.GetOrBuildAsync("S02", null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(CreateResult("S02", "catalog-v1:other")),
            CancellationToken.None);

        Assert.Equal(old.CatalogIndex.CatalogVersion,
            cache.GetByVersion("S01", null, old.CatalogIndex.CatalogVersion)?.CatalogIndex.CatalogVersion);
        var lkg = await cache.GetOrBuildAsync("S01", null,
            _ => throw new InvalidOperationException("新版磁盘 LKG 不应触发 SQL 构建"), CancellationToken.None);
        Assert.Equal(current.CatalogIndex.CatalogVersion, lkg?.CatalogIndex.CatalogVersion);
        Assert.Equal(1, scheduler.QueueCount);
    }

    [Fact]
    public async Task Expired_non_latest_persisted_version_is_not_restored_after_capacity_eviction()
    {
        using var directory = new TemporaryDirectory();
        var clock = new MutableTimeProvider(GeneratedAt);
        var cache = new CatalogIndexCache(clock, TimeSpan.FromMinutes(20), TimeSpan.FromHours(1),
            maxSnapshotsPerStore: 8, new GzipCatalogSnapshotStore(directory.Path),
            softItemCapacity: 2, hardItemCapacity: 2);
        var old = CreateResult("S01", "catalog-v1:old");
        var current = CreateResult("S01", "catalog-v1:current");
        await cache.GetOrBuildAsync("S01", null, _ => Task.FromResult<CatalogIndexBuildResult?>(old), CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(30));
        await cache.ForceRefreshAndPublishAsync("S01", null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(current), CancellationToken.None);
        await cache.GetOrBuildAsync("S02", null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(CreateSizedResult("S02", "catalog-v1:other", 1)),
            CancellationToken.None);
        // 旧版先因容量降级到 descriptor，再跨过它的到期时间。
        clock.Advance(TimeSpan.FromMinutes(31));

        Assert.Null(cache.GetByVersion("S01", null, old.CatalogIndex.CatalogVersion));
        Assert.NotNull(cache.GetByVersion("S01", null, current.CatalogIndex.CatalogVersion));
    }

    [Fact]
    public async Task Capacity_rejected_normal_refresh_returns_pinned_last_good_version()
    {
        var clock = new MutableTimeProvider(GeneratedAt);
        var cache = new CatalogIndexCache(
            clock,
            TimeSpan.FromMinutes(20),
            TimeSpan.FromHours(2),
            maxSnapshotsPerStore: 8,
            softItemCapacity: 1,
            hardItemCapacity: 1);
        var lastGood = CreateSizedResult("S01", "catalog-v1:last-good", 1);
        var oversized = CreateSizedResult("S01", "catalog-v1:unpublished", 2);
        await cache.GetOrBuildAsync("S01", null, _ => Task.FromResult<CatalogIndexBuildResult?>(lastGood), CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(21));

        var served = await cache.GetOrBuildAsync("S01", null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(oversized), CancellationToken.None);

        Assert.Same(lastGood, served);
        Assert.Same(lastGood, cache.GetByVersion("S01", null, lastGood.CatalogIndex.CatalogVersion));
        Assert.Null(cache.GetByVersion("S01", null, oversized.CatalogIndex.CatalogVersion));

        var other = CreateSizedResult("S02", "catalog-v1:other", 1);
        await cache.GetOrBuildAsync("S02", null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(other), CancellationToken.None);
        Assert.Equal(1, cache.ActiveEntryCountForTests);
        Assert.Null(cache.GetByVersion("S01", null, lastGood.CatalogIndex.CatalogVersion));
    }

    [Fact]
    public void Oversized_lazy_descriptor_is_not_admitted_to_memory()
    {
        var now = GeneratedAt;
        var store = new RecordingSnapshotStore(
            [new CatalogSnapshotDescriptor("S01", null, now, now.AddHours(1), "catalog-v1:oversized")],
            _ => new CatalogPersistedSnapshot("S01", null, now, now.AddHours(1), "catalog-v1:oversized",
                CreateSizedResult("S01", "catalog-v1:oversized", 4).SellableItems));
        var cache = new CatalogIndexCache(new MutableTimeProvider(now), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(30), 8, store, 1, 3);

        Assert.Null(cache.GetByVersion("S01", null, "catalog-v1:oversized"));
        Assert.Equal(0, cache.PinnedItemCountForTests);
    }

    [Fact]
    public async Task Normal_first_and_force_refresh_join_the_same_store_flight()
    {
        var cache = new CatalogIndexCache();
        var buildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowBuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var buildCount = 0;

        var normal = cache.GetOrBuildAsync("S01", null, Build, CancellationToken.None);
        await buildStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var force = cache.ForceRefreshAndPublishAsync(
            " s01 ",
            null,
            _ => throw new InvalidOperationException("force 不应启动第二个 builder"),
            CancellationToken.None);

        allowBuild.SetResult();
        var results = await Task.WhenAll(normal, force).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, buildCount);
        Assert.Equal(results[0]!.CatalogIndex.CatalogVersion, results[1]!.CatalogIndex.CatalogVersion);

        async Task<CatalogIndexBuildResult?> Build(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref buildCount);
            buildStarted.TrySetResult();
            await allowBuild.Task.WaitAsync(cancellationToken);
            return CreateResult("S01", "catalog-v1:shared");
        }
    }

    [Fact]
    public async Task Cross_store_build_waits_until_previous_snapshot_is_published()
    {
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBuildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new BlockingSnapshotStore(saveStarted, allowSave);
        var cache = new CatalogIndexCache(store);

        var first = cache.ForceRefreshAndPublishAsync("S01", null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(CreateResult("S01", "catalog-v1:first")),
            CancellationToken.None);
        await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = cache.ForceRefreshAndPublishAsync("S02", null, _ =>
        {
            secondBuildStarted.TrySetResult();
            return Task.FromResult<CatalogIndexBuildResult?>(CreateResult("S02", "catalog-v1:second"));
        }, CancellationToken.None);
        Assert.False(secondBuildStarted.Task.IsCompleted);

        allowSave.SetResult();
        Assert.NotNull(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        await secondBuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(await second.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Failed_build_releases_global_gate_for_next_store()
    {
        var firstBuildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBuildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new CatalogIndexCache();

        var first = cache.ForceRefreshAndPublishAsync("S01", null, async token =>
        {
            firstBuildStarted.TrySetResult();
            await allowFailure.Task.WaitAsync(token);
            throw new IOException("build failed");
        }, CancellationToken.None);
        await firstBuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = cache.ForceRefreshAndPublishAsync("S02", null, _ =>
        {
            secondBuildStarted.TrySetResult();
            return Task.FromResult<CatalogIndexBuildResult?>(CreateResult("S02", "catalog-v1:second"));
        }, CancellationToken.None);
        Assert.False(secondBuildStarted.Task.IsCompleted);

        allowFailure.SetResult();
        await Assert.ThrowsAsync<IOException>(() => first);
        await secondBuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(await second.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Queued_build_times_out_without_releasing_owner_gate_and_later_store_can_build()
    {
        var firstBuildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBuildCount = 0;
        var thirdBuildCount = 0;
        var cache = new CatalogIndexCache(
            new MutableTimeProvider(GeneratedAt),
            TimeSpan.FromMinutes(20),
            TimeSpan.FromMinutes(30),
            maxSnapshotsPerStore: 8,
            new RecordingSnapshotStore([], _ => null),
            softItemCapacity: 10,
            hardItemCapacity: 20,
            buildGateWaitTimeout: TimeSpan.FromMilliseconds(100));

        var owner = cache.ForceRefreshAndPublishAsync("S01", null, async token =>
        {
            firstBuildStarted.TrySetResult();
            await allowFirst.Task.WaitAsync(token);
            return CreateResult("S01", "catalog-v1:first");
        }, CancellationToken.None);
        await firstBuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var queued = cache.GetOrBuildAsync("S02", null, _ =>
        {
            Interlocked.Increment(ref secondBuildCount);
            return Task.FromResult<CatalogIndexBuildResult?>(CreateResult("S02", "catalog-v1:second"));
        }, CancellationToken.None);
        await Assert.ThrowsAsync<CatalogCapacityBusyException>(() => queued.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, secondBuildCount);
        Assert.False(owner.IsCompleted);

        allowFirst.SetResult();
        Assert.NotNull(await owner.WaitAsync(TimeSpan.FromSeconds(2)));
        var later = await cache.GetOrBuildAsync("S03", null, _ =>
        {
            Interlocked.Increment(ref thirdBuildCount);
            return Task.FromResult<CatalogIndexBuildResult?>(CreateResult("S03", "catalog-v1:third"));
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(later);
        Assert.Equal(1, thirdBuildCount);
    }

    [Fact]
    public async Task Cancelling_queued_http_waiter_does_not_cancel_shared_build()
    {
        var firstBuildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBuildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var waiterCancellation = new CancellationTokenSource();
        var cache = new CatalogIndexCache();
        var first = cache.ForceRefreshAndPublishAsync("S01", null, async token =>
        {
            firstBuildStarted.TrySetResult();
            await allowFirst.Task.WaitAsync(token);
            return CreateResult("S01", "catalog-v1:first");
        }, CancellationToken.None);
        await firstBuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var queued = cache.GetOrBuildAsync("S02", null, _ =>
        {
            secondBuildStarted.TrySetResult();
            return Task.FromResult<CatalogIndexBuildResult?>(CreateResult("S02", "catalog-v1:second"));
        }, waiterCancellation.Token);
        waiterCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(secondBuildStarted.Task.IsCompleted);

        allowFirst.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        await secondBuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => cache.GetByVersion("S02", null, "catalog-v1:second") is not null,
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Force_first_and_fresh_http_join_the_same_store_flight()
    {
        var cache = new CatalogIndexCache();
        var buildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowBuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var buildCount = 0;

        var force = cache.ForceRefreshAndPublishAsync("S01", null, Build, CancellationToken.None);
        await buildStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var fresh = cache.GetOrBuildFreshAsync(
            "S01",
            null,
            _ => throw new InvalidOperationException("fresh HTTP 不应启动第二个 builder"),
            CancellationToken.None);

        allowBuild.SetResult();
        var results = await Task.WhenAll(force, fresh).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, buildCount);
        Assert.Equal(results[0]!.CatalogIndex.CatalogVersion, results[1]!.CatalogIndex.CatalogVersion);

        async Task<CatalogIndexBuildResult?> Build(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref buildCount);
            buildStarted.TrySetResult();
            await allowBuild.Task.WaitAsync(cancellationToken);
            return CreateResult("S01", "catalog-v1:shared");
        }
    }

    [Fact]
    public async Task Fresh_and_force_waiters_observe_the_same_capacity_failure()
    {
        var cache = new CatalogIndexCache(
            new MutableTimeProvider(GeneratedAt),
            TimeSpan.FromMinutes(20),
            TimeSpan.FromMinutes(30),
            8,
            softItemCapacity: 1,
            hardItemCapacity: 3);
        var buildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowBuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var buildCount = 0;

        var fresh = cache.GetOrBuildFreshAsync("S01", null, Build, CancellationToken.None);
        await buildStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var force = cache.ForceRefreshAndPublishAsync(
            "S01",
            null,
            _ => throw new InvalidOperationException("不应启动第二个 builder"),
            CancellationToken.None);
        allowBuild.SetResult();

        await Assert.ThrowsAsync<CatalogCapacityBusyException>(() => fresh);
        await Assert.ThrowsAsync<CatalogCapacityBusyException>(() => force);
        Assert.Equal(1, buildCount);

        async Task<CatalogIndexBuildResult?> Build(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref buildCount);
            buildStarted.TrySetResult();
            await allowBuild.Task.WaitAsync(cancellationToken);
            return CreateSizedResult("S01", "catalog-v1:oversized", 4);
        }
    }

    [Fact]
    public void Per_store_limit_counts_unique_leased_versions_but_not_duplicate_leases()
    {
        var cache = new CatalogIndexCache(
            new MutableTimeProvider(GeneratedAt),
            TimeSpan.FromMinutes(20),
            TimeSpan.FromMinutes(30),
            maxSnapshotsPerStore: 8,
            softItemCapacity: 100,
            hardItemCapacity: 100);
        var versions = Enumerable.Range(1, 8)
            .Select(number => CreateSizedResult("S01", $"catalog-v1:{number}", 1))
            .ToArray();

        foreach (var version in versions)
        {
            cache.CreateFullLease(version);
        }

        Assert.NotNull(cache.CreateFullLease(versions[0]));
        Assert.Throws<CatalogCapacityBusyException>(() =>
            cache.CreateFullLease(CreateSizedResult("S01", "catalog-v1:9", 1)));
    }

    [Fact]
    public async Task Lease_admission_evicts_an_unleased_old_version_before_rejecting()
    {
        var cache = new CatalogIndexCache(
            new MutableTimeProvider(GeneratedAt),
            TimeSpan.FromMinutes(20),
            TimeSpan.FromMinutes(30),
            maxSnapshotsPerStore: 2,
            softItemCapacity: 100,
            hardItemCapacity: 100);
        var first = await Publish("catalog-v1:1");
        cache.InvalidateStore("S01");
        var second = await Publish("catalog-v1:2");
        cache.CreateFullLease(first);

        var third = CreateSizedResult("S01", "catalog-v1:3", 1);
        cache.CreateFullLease(third);

        Assert.Equal(
            first.CatalogIndex.CatalogVersion,
            cache.GetByVersion("S01", null, first.CatalogIndex.CatalogVersion)?.CatalogIndex.CatalogVersion);
        Assert.Null(cache.GetByVersion("S01", null, second.CatalogIndex.CatalogVersion));

        async Task<CatalogIndexBuildResult> Publish(string version)
        {
            return (await cache.GetOrBuildAsync(
                "S01",
                null,
                _ => Task.FromResult<CatalogIndexBuildResult?>(
                    CreateResult("S01", version, contentMarker: version)),
                CancellationToken.None))!;
        }
    }

    [Fact]
    public async Task Impossible_target_rejection_does_not_evict_existing_snapshot()
    {
        var cache = new CatalogIndexCache(
            new MutableTimeProvider(GeneratedAt),
            TimeSpan.FromMinutes(20),
            TimeSpan.FromMinutes(30),
            8,
            softItemCapacity: 1,
            hardItemCapacity: 3);
        var existing = CreateSizedResult("S01", "catalog-v1:existing", 2);
        await cache.GetOrBuildAsync(
            "S01",
            null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(existing),
            CancellationToken.None);

        Assert.Throws<CatalogCapacityBusyException>(() =>
            cache.EnsurePlanCapacity(CreateSizedResult("S02", "catalog-v1:impossible", 4)));
        Assert.Same(existing, cache.GetByVersion("S01", null, existing.CatalogIndex.CatalogVersion));
    }

    [Fact]
    public async Task Pending_durable_save_reserves_capacity_and_deduplicates_the_same_version()
    {
        var publishStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new BlockingSnapshotStore(publishStarted, allowPublish);
        var cache = new CatalogIndexCache(
            new MutableTimeProvider(GeneratedAt),
            TimeSpan.FromMinutes(20),
            TimeSpan.FromMinutes(30),
            maxSnapshotsPerStore: 8,
            store,
            softItemCapacity: 1,
            hardItemCapacity: 3);
        var pending = CreateSizedResult("S01", "catalog-v1:pending", 2);

        var force = cache.ForceRefreshAndPublishAsync(
            "S01",
            null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(pending),
            CancellationToken.None);
        await publishStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(cache.CreateFullLease(pending));
        Assert.Throws<CatalogCapacityBusyException>(() =>
            cache.CreateFullLease(CreateSizedResult("S02", "catalog-v1:other", 2)));

        allowPublish.SetResult();
        Assert.NotNull(await force.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Invalidated_old_owner_releases_its_pending_reservation_after_persistence_failure()
    {
        var store = new OverlappingSnapshotStore();
        var cache = CreateOverlappingPendingCache(store);
        var pending = CreateSizedResult("S01", "catalog-v1:shared-pending", 2);

        var firstOwner = cache.ForceRefreshAndPublishAsync(
            "S01",
            null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(pending),
            CancellationToken.None);
        await store.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cache.InvalidateStore("S01");
        var secondOwner = cache.ForceRefreshAndPublishAsync(
            "S01",
            null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(pending),
            CancellationToken.None);
        Assert.False(store.SecondSaveStarted.Task.IsCompleted);
        store.CompleteFirst(new IOException("first owner failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => firstOwner);
        await store.SecondSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // 失效 owner 的 reservation 释放发生在后台 flight 处理持久化失败之后，
        // 与 firstOwner 的 Invalidated 结果不是同一线性化点，必须先等待容量状态就绪。
        await WaitUntilAsync(() => cache.PendingSnapshotOwnerCountForTests == 1, TimeSpan.FromSeconds(2));
        Assert.Equal(1, cache.PendingSnapshotOwnerCountForTests);
        Assert.Null(cache.GetByVersion("S01", null, pending.CatalogIndex.CatalogVersion));
        Assert.Throws<CatalogCapacityBusyException>(() =>
            cache.CreateFullLease(CreateSizedResult("S02", "catalog-v1:other", 2)));

        store.CompleteSecond();
        Assert.NotNull(await secondOwner.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.NotNull(cache.GetByVersion("S01", null, pending.CatalogIndex.CatalogVersion));
        Assert.Equal(0, cache.PendingSnapshotOwnerCountForTests);
    }

    [Fact]
    public async Task Invalidated_old_owner_releases_its_pending_reservation_before_same_version_republish()
    {
        var store = new OverlappingSnapshotStore();
        var cache = CreateOverlappingPendingCache(store);
        var pending = CreateSizedResult("S01", "catalog-v1:shared-pending", 2);

        var firstOwner = cache.ForceRefreshAndPublishAsync(
            "S01",
            null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(pending),
            CancellationToken.None);
        await store.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cache.InvalidateStore("S01");
        var secondOwner = cache.ForceRefreshAndPublishAsync(
            "S01",
            null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(pending),
            CancellationToken.None);
        Assert.False(store.SecondSaveStarted.Task.IsCompleted);
        store.CompleteFirst();
        await Assert.ThrowsAsync<InvalidOperationException>(() => firstOwner);
        await store.SecondSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => cache.PendingSnapshotOwnerCountForTests == 1, TimeSpan.FromSeconds(2));
        Assert.Equal(1, cache.PendingSnapshotOwnerCountForTests);

        store.CompleteSecond();
        Assert.NotNull(await secondOwner.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, cache.PendingSnapshotOwnerCountForTests);
        Assert.NotNull(cache.GetByVersion("S01", null, pending.CatalogIndex.CatalogVersion));
    }

    [Fact]
    public async Task Same_version_pending_capacity_uses_the_largest_owner_artifact()
    {
        var store = new OverlappingSnapshotStore();
        var cache = new CatalogIndexCache(
            new MutableTimeProvider(GeneratedAt),
            TimeSpan.FromMinutes(20),
            TimeSpan.FromMinutes(30),
            maxSnapshotsPerStore: 8,
            store,
            softItemCapacity: 1,
            hardItemCapacity: 5);
        var large = CreateSizedResult("S01", "catalog-v1:shared-pending", 4);
        var small = CreateSizedResult("S01", "catalog-v1:shared-pending", 2);

        var firstOwner = cache.ForceRefreshAndPublishAsync(
            "S01",
            null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(large),
            CancellationToken.None);
        await store.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cache.InvalidateStore("S01");
        var secondOwner = cache.ForceRefreshAndPublishAsync(
            "S01",
            null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(small),
            CancellationToken.None);
        Assert.False(store.SecondSaveStarted.Task.IsCompleted);
        Assert.Throws<CatalogCapacityBusyException>(() =>
            cache.CreateFullLease(CreateSizedResult("S02", "catalog-v1:other", 2)));

        store.CompleteFirst(new IOException("first owner failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => firstOwner);
        await store.SecondSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // 失效 flight 的 reservation 释放与 Invalidated 结果不同步；等待持久化失败处理完成后再准入。
        await WaitUntilAsync(() => cache.PendingSnapshotOwnerCountForTests == 1, TimeSpan.FromSeconds(2));
        Assert.NotNull(cache.CreateFullLease(CreateSizedResult("S02", "catalog-v1:other", 2)));

        store.CompleteSecond();
        Assert.NotNull(await secondOwner.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, cache.PendingSnapshotOwnerCountForTests);
    }

    [Fact]
    public async Task Repro_Invalidation_outcome_can_beat_pending_reservation_release()
    {
        var holdFailureProcessing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new OverlappingSnapshotStore(holdFailureProcessing);
        var cache = new CatalogIndexCache(
            new MutableTimeProvider(GeneratedAt),
            TimeSpan.FromMinutes(20),
            TimeSpan.FromMinutes(30),
            maxSnapshotsPerStore: 8,
            store,
            softItemCapacity: 1,
            hardItemCapacity: 5);
        var large = CreateSizedResult("S01", "catalog-v1:shared-pending", 4);
        var small = CreateSizedResult("S01", "catalog-v1:shared-pending", 2);

        var firstOwner = cache.ForceRefreshAndPublishAsync(
            "S01",
            null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(large),
            CancellationToken.None);
        await store.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cache.InvalidateStore("S01");
        var secondOwner = cache.ForceRefreshAndPublishAsync(
            "S01",
            null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(small),
            CancellationToken.None);
        Assert.False(store.SecondSaveStarted.Task.IsCompleted);
        Assert.Throws<CatalogCapacityBusyException>(() =>
            cache.CreateFullLease(CreateSizedResult("S02", "catalog-v1:other", 2)));

        store.CompleteFirst(new IOException("first owner failed"));
        // 失效调用方在 InvalidateStore 时就已经拿到 Invalidated 结果，
        // 而 reservation 释放仍在后台 flight 的持久化失败处理中，两者不是同一线性化点。
        await Assert.ThrowsAsync<InvalidOperationException>(() => firstOwner);
        Assert.Equal(1, cache.PendingSnapshotOwnerCountForTests);

        holdFailureProcessing.SetResult();
        await store.SecondSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => cache.PendingSnapshotOwnerCountForTests == 1, TimeSpan.FromSeconds(2));
        Assert.NotNull(cache.CreateFullLease(CreateSizedResult("S02", "catalog-v1:other", 2)));

        store.CompleteSecond();
        Assert.NotNull(await secondOwner.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, cache.PendingSnapshotOwnerCountForTests);
    }

    private static CatalogIndexCache CreateOverlappingPendingCache(
        ICatalogSnapshotStore store)
    {
        return new CatalogIndexCache(
            new MutableTimeProvider(GeneratedAt),
            TimeSpan.FromMinutes(20),
            TimeSpan.FromMinutes(30),
            maxSnapshotsPerStore: 8,
            store,
            softItemCapacity: 1,
            hardItemCapacity: 3);
    }

    // 等待后台 flight 把可观察状态推进到条件满足；用于失效发布中
    // “调用方 Invalidated 结果”与“reservation 释放/durable pin”两个不同线性化点之间的同步。
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"等待后台状态超时：{timeout}");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public void Lazy_capacity_rejection_keeps_descriptor_for_retry_after_leases_expire()
    {
        var time = new MutableTimeProvider(GeneratedAt);
        var loadCount = 0;
        var descriptor = new CatalogSnapshotDescriptor(
            "S01",
            null,
            GeneratedAt,
            GeneratedAt.AddHours(1),
            "catalog-v1:9");
        var persisted = new CatalogPersistedSnapshot(
            "S01",
            null,
            GeneratedAt,
            GeneratedAt.AddHours(1),
            descriptor.CatalogVersion,
            CreateSizedResult("S01", descriptor.CatalogVersion, 1).SellableItems);
        var store = new RecordingSnapshotStore(
            [descriptor],
            _ =>
            {
                loadCount++;
                return persisted;
            });
        var cache = new CatalogIndexCache(
            time,
            TimeSpan.FromMinutes(20),
            TimeSpan.FromMinutes(30),
            8,
            store,
            softItemCapacity: 100,
            hardItemCapacity: 100);
        foreach (var number in Enumerable.Range(1, 8))
        {
            cache.CreateFullLease(CreateSizedResult("S01", $"catalog-v1:{number}", 1));
        }

        Assert.Null(cache.GetByVersion("S01", null, descriptor.CatalogVersion));
        time.Advance(TimeSpan.FromMinutes(31));
        Assert.NotNull(cache.GetByVersion("S01", null, descriptor.CatalogVersion));
        Assert.Equal(2, loadCount);
    }

    [Fact]
    public void Missing_lazy_body_removes_descriptor_instead_of_reloading_it()
    {
        var loadCount = 0;
        var descriptor = new CatalogSnapshotDescriptor(
            "S01",
            null,
            GeneratedAt,
            GeneratedAt.AddHours(1),
            "catalog-v1:missing");
        var store = new RecordingSnapshotStore(
            [descriptor],
            _ =>
            {
                loadCount++;
                return null;
            });
        var cache = new CatalogIndexCache(store);

        Assert.Null(cache.GetByVersion("S01", null, descriptor.CatalogVersion));
        Assert.Null(cache.GetByVersion("S01", null, descriptor.CatalogVersion));
        Assert.Equal(1, loadCount);
    }

    [Fact]
    public async Task Force_refresh_persists_a_new_version_to_the_real_gzip_store()
    {
        using var directory = new TemporaryDirectory();
        var store = new GzipCatalogSnapshotStore(directory.Path);
        var cache = new CatalogIndexCache(store);
        var result = CreateResult("S01", "catalog-v1:durable");

        var published = await cache.ForceRefreshAndPublishAsync(
            "S01",
            null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(result),
            CancellationToken.None);

        Assert.Equal(result.CatalogIndex.CatalogVersion, published!.CatalogIndex.CatalogVersion);
        Assert.NotNull(store.Load("S01", null, result.CatalogIndex.CatalogVersion));
    }

    [Fact]
    public async Task Force_refresh_propagates_snapshot_persistence_failure()
    {
        var cache = new CatalogIndexCache(new ThrowingSnapshotStore());

        await Assert.ThrowsAsync<IOException>(() =>
            cache.ForceRefreshAndPublishAsync(
                "S01",
                null,
                _ => Task.FromResult<CatalogIndexBuildResult?>(
                    CreateResult("S01", "catalog-v1:not-durable")),
                CancellationToken.None));
        Assert.Null(cache.GetByVersion("S01", null, "catalog-v1:not-durable"));
    }

    [Fact]
    public async Task Active_cas_invalidation_fails_force_waiter_but_keeps_durable_version_pinned()
    {
        var publishStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new BlockingSnapshotStore(publishStarted, allowPublish);
        var cache = new CatalogIndexCache(store);
        var result = CreateResult("S01", "catalog-v1:persisted");

        var force = cache.ForceRefreshAndPublishAsync(
            "S01",
            null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(result),
            CancellationToken.None);
        await publishStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cache.InvalidateStore("S01");
        Assert.Null(cache.GetByVersion("S01", null, result.CatalogIndex.CatalogVersion));
        allowPublish.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => force);
        // durable pin 发生在后台 flight 完成 Save 之后，与 force 的 Invalidated 结果不是同一线性化点。
        await WaitUntilAsync(
            () => cache.GetByVersion("S01", null, result.CatalogIndex.CatalogVersion) is not null,
            TimeSpan.FromSeconds(2));
        Assert.NotNull(cache.GetByVersion("S01", null, result.CatalogIndex.CatalogVersion));
    }

    [Fact]
    public async Task InvalidateStore_during_durable_publication_fails_all_waiters_but_keeps_version_pinned()
    {
        var publishStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new BlockingSnapshotStore(publishStarted, allowPublish);
        var cache = new CatalogIndexCache(store);
        var result = CreateResult("S01", "catalog-v1:invalidated-durable");

        var force = cache.ForceRefreshAndPublishAsync(
            "S01",
            null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(result),
            CancellationToken.None);
        await publishStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var fresh = cache.GetOrBuildFreshAsync(
            "S01",
            null,
            _ => throw new InvalidOperationException("同一 flight 不应重新构建"),
            CancellationToken.None);
        var legacy = cache.GetOrBuildAsync(
            "S01",
            null,
            _ => throw new InvalidOperationException("同一 flight 不应重新构建"),
            CancellationToken.None);

        cache.InvalidateStore("S01");
        Assert.Null(cache.GetByVersion("S01", null, result.CatalogIndex.CatalogVersion));
        allowPublish.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => force);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fresh);
        await Assert.ThrowsAsync<InvalidOperationException>(() => legacy);
        await WaitUntilAsync(
            () => cache.GetByVersion("S01", null, result.CatalogIndex.CatalogVersion) is not null,
            TimeSpan.FromSeconds(2));
        Assert.NotNull(cache.GetByVersion("S01", null, result.CatalogIndex.CatalogVersion));
    }

    [Fact]
    public async Task InvalidateStore_after_durable_pin_before_active_cas_fails_force_waiter()
    {
        var publishStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeCasReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowActiveCas = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new BlockingSnapshotStore(publishStarted, allowPublish);
        var cache = new CatalogIndexCache(store)
        {
            BeforeActivePublicationCasForTests = () =>
            {
                activeCasReady.TrySetResult();
                allowActiveCas.Task.GetAwaiter().GetResult();
            }
        };
        var result = CreateResult("S01", "catalog-v1:active-cas-race");

        var force = cache.ForceRefreshAndPublishAsync(
            "S01",
            null,
            _ => Task.FromResult<CatalogIndexBuildResult?>(result),
            CancellationToken.None);
        await publishStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        allowPublish.SetResult();
        await activeCasReady.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(cache.GetByVersion("S01", null, result.CatalogIndex.CatalogVersion));
        cache.InvalidateStore("S01");
        allowActiveCas.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => force);
        Assert.NotNull(cache.GetByVersion("S01", null, result.CatalogIndex.CatalogVersion));
    }

    [Fact]
    public async Task InvalidateStore_after_generation_capture_before_install_overrides_persistence_failure()
    {
        var cache = new CatalogIndexCache(new ThrowingSnapshotStore());
        cache.AfterStorePublicationGenerationCapturedForTests =
            () => cache.InvalidateStore("S01");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.ForceRefreshAndPublishAsync(
                "S01",
                null,
                _ => Task.FromResult<CatalogIndexBuildResult?>(
                    CreateResult("S01", "catalog-v1:captured-generation")),
                CancellationToken.None));
    }

    [Fact]
    public async Task InvalidateStore_after_generation_capture_before_install_overrides_build_failure()
    {
        var cache = new CatalogIndexCache();
        cache.AfterStorePublicationGenerationCapturedForTests =
            () => cache.InvalidateStore("S01");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.ForceRefreshAndPublishAsync(
                "S01",
                null,
                _ => Task.FromException<CatalogIndexBuildResult?>(
                    new InvalidDataException("构建失败")),
                CancellationToken.None));
    }

    [Fact]
    public async Task Permanent_snapshot_eviction_removes_raw_version_markers()
    {
        var cache = new CatalogIndexCache(
            new MutableTimeProvider(GeneratedAt),
            TimeSpan.FromMinutes(20),
            TimeSpan.FromMinutes(30),
            maxSnapshotsPerStore: 2);

        foreach (var number in Enumerable.Range(1, 3))
        {
            var version = $"catalog-v1:{number}";
            await cache.GetOrBuildAsync(
                "S01",
                null,
                _ => Task.FromResult<CatalogIndexBuildResult?>(CreateRawResult("S01", version)),
                CancellationToken.None);
            cache.InvalidateStore("S01");
        }

        Assert.Equal(2, cache.RawArtifactVersionCountForTests);
    }

    [Fact]
    public async Task Raw_lru_eviction_does_not_break_first_batch_legacy_waiters()
    {
        var store = new CountingBlockingSnapshotStore(expectedSaves: 3);
        var cache = new CatalogIndexCache(
            new MutableTimeProvider(GeneratedAt),
            TimeSpan.FromMinutes(20),
            TimeSpan.FromMinutes(30),
            maxSnapshotsPerStore: 8,
            store,
            softItemCapacity: 100,
            hardItemCapacity: 100);
        var buildCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var since = GeneratedAt.AddMinutes(-5);

        var first = cache.GetOrBuildAsync("S01", since, token => Build("S01", token), CancellationToken.None);
        await store.FirstSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = cache.GetOrBuildAsync("S01", since, token => Build("S01", token), CancellationToken.None);
        var otherOne = cache.GetOrBuildAsync("S02", null, token => Build("S02", token), CancellationToken.None);
        var otherTwo = cache.GetOrBuildAsync("S03", null, token => Build("S03", token), CancellationToken.None);
        store.AllowSaves.SetResult();
        await store.ExpectedSavesStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var results = await Task.WhenAll(first, second, otherOne, otherTwo)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, buildCounts["S01"]);
        Assert.Equal(PriceSourceKind.ProductBase, Assert.Single(results[0]!.SellableItems).PriceSource);
        Assert.Equal(PriceSourceKind.ProductBase, Assert.Single(results[1]!.SellableItems).PriceSource);

        Task<CatalogIndexBuildResult?> Build(string storeCode, CancellationToken _)
        {
            buildCounts[storeCode] = buildCounts.GetValueOrDefault(storeCode) + 1;
            return Task.FromResult<CatalogIndexBuildResult?>(
                CreateRawResult(storeCode, $"catalog-v1:{storeCode}"));
        }
    }

    private static CatalogIndexBuildResult CreateSizedResult(string storeCode, string version, int count)
    {
        var items = Enumerable.Range(0, count)
            .Select(index => new SellableItemDto(storeCode, $"P{index}", null, "Item", $"L{index}", null, null,
                1m, PriceSourceKind.ProductBase, "product", 1m, GeneratedAt))
            .ToArray();
        return new CatalogIndexBuildResult(
            storeCode,
            GeneratedAt,
            items,
            new CatalogSellableIndex(storeCode, GeneratedAt, items, version));
    }

    private static CatalogIndexBuildResult CreateRawResult(string storeCode, string version)
    {
        var input = new PriceIndexInput(
            Since: null,
            Products:
            [
                new ProductPriceRecord($"P-{version}", "Base", null, $"X-{version}", 10m, GeneratedAt)
            ],
            StoreRetailPrices: [],
            StoreMultiCodeProducts: [],
            StoreClearancePrices:
            [
                new StoreClearancePriceRecord(
                    $"P-{version}",
                    $"X-{version}",
                    5m,
                    GeneratedAt.AddMinutes(-10))
            ],
            ProductSetCodes: []);
        var items = new PriceIndexBuilder().Build(storeCode, input);
        return new CatalogIndexBuildResult(
            storeCode,
            GeneratedAt,
            items,
            new CatalogSellableIndex(storeCode, GeneratedAt, items, version),
            RawPriceIndexInput: input);
    }

    private static CatalogIndexCache CreateSnapshotCache(
        MutableTimeProvider timeProvider,
        ICatalogBackgroundRefreshScheduler? scheduler = null)
    {
        return scheduler is null
            ? new CatalogIndexCache(
                timeProvider,
                TimeSpan.FromMinutes(2),
                snapshotTtl: TimeSpan.FromMinutes(30),
                maxSnapshotsPerStore: 2)
            : new CatalogIndexCache(
                timeProvider,
                TimeSpan.FromMinutes(2),
                snapshotTtl: TimeSpan.FromMinutes(30),
                maxSnapshotsPerStore: 2,
                backgroundRefreshScheduler: scheduler);
    }

    private static CatalogIndexBuildResult CreateResult(
        string storeCode,
        string? catalogVersion = null,
        string? contentMarker = null,
        DateTimeOffset? sourceValidUntil = null)
    {
        contentMarker ??= catalogVersion;
        IReadOnlyList<SellableItemDto> items = contentMarker is null
            ? []
            : [new SellableItemDto(
                storeCode,
                contentMarker,
                null,
                contentMarker,
                contentMarker,
                null,
                null,
                1m,
                PriceSourceKind.StoreRetailPrice,
                "门店价",
                1m,
                GeneratedAt)];
        return new CatalogIndexBuildResult(
            storeCode,
            GeneratedAt,
            items,
            new CatalogSellableIndex(storeCode, GeneratedAt, items, catalogVersion),
            sourceValidUntil);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan delta)
        {
            _utcNow = _utcNow.Add(delta);
        }
    }

    private sealed class RecordingRefreshScheduler : ICatalogBackgroundRefreshScheduler
    {
        public int QueueCount { get; private set; }

        public void QueueRefresh(string storeCode)
        {
            QueueCount++;
        }
    }

    private sealed class RecordingSnapshotStore(
        IReadOnlyList<CatalogSnapshotDescriptor> descriptors,
        Func<string, CatalogPersistedSnapshot?> load) : ICatalogSnapshotStore
    {
        public IReadOnlyList<CatalogSnapshotDescriptor> LoadDescriptors(DateTimeOffset now) => descriptors;

        public CatalogPersistedSnapshot? Load(string storeCode, DateTimeOffset? since, string catalogVersion) => load(catalogVersion);

        public IReadOnlyList<CatalogPersistedSnapshot> LoadAll(DateTimeOffset now) => [];

        public void Save(CatalogPersistedSnapshot snapshot) { }

        public void RefreshExpiration(string storeCode, DateTimeOffset? since, string catalogVersion, DateTimeOffset expiresAt) { }
    }

    private sealed class BlockingSnapshotStore(
        TaskCompletionSource publishStarted,
        TaskCompletionSource allowPublish) : ICatalogSnapshotStore
    {
        public TaskCompletionSource PublishStarted { get; private set; } = publishStarted;

        public TaskCompletionSource AllowPublish { get; private set; } = allowPublish;

        public TaskCompletionSource PublishCompleted { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<CatalogSnapshotDescriptor> LoadDescriptors(DateTimeOffset now) => [];

        public CatalogPersistedSnapshot? Load(
            string storeCode,
            DateTimeOffset? since,
            string catalogVersion) => null;

        public IReadOnlyList<CatalogPersistedSnapshot> LoadAll(DateTimeOffset now) => [];

        public void Save(CatalogPersistedSnapshot snapshot)
        {
            PublishStarted.TrySetResult();
            AllowPublish.Task.GetAwaiter().GetResult();
            PublishCompleted.TrySetResult();
        }

        public void RefreshExpiration(
            string storeCode,
            DateTimeOffset? since,
            string catalogVersion,
            DateTimeOffset expiresAt)
        {
            Save(new CatalogPersistedSnapshot(
                storeCode,
                since,
                GeneratedAt,
                expiresAt,
                catalogVersion,
                []));
        }

        public void ResetPublication()
        {
            PublishStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            AllowPublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            PublishCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private sealed class ThrowingSnapshotStore : ICatalogSnapshotStore
    {
        public IReadOnlyList<CatalogSnapshotDescriptor> LoadDescriptors(DateTimeOffset now) => [];

        public CatalogPersistedSnapshot? Load(
            string storeCode,
            DateTimeOffset? since,
            string catalogVersion) => null;

        public IReadOnlyList<CatalogPersistedSnapshot> LoadAll(DateTimeOffset now) => [];

        public void Save(CatalogPersistedSnapshot snapshot)
        {
            throw new IOException("disk unavailable");
        }

        public void RefreshExpiration(
            string storeCode,
            DateTimeOffset? since,
            string catalogVersion,
            DateTimeOffset expiresAt)
        {
            throw new IOException("disk unavailable");
        }
    }

    private sealed class OverlappingSnapshotStore : ICatalogSnapshotStore
    {
        private readonly TaskCompletionSource<Exception?> _firstOutcome =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<Exception?> _secondOutcome =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource? _holdFirstFailure;
        private int _saveCount;

        public OverlappingSnapshotStore(TaskCompletionSource? holdFirstFailure = null)
        {
            _holdFirstFailure = holdFirstFailure;
        }

        public TaskCompletionSource FirstSaveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondSaveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<CatalogSnapshotDescriptor> LoadDescriptors(DateTimeOffset now) => [];

        public CatalogPersistedSnapshot? Load(
            string storeCode,
            DateTimeOffset? since,
            string catalogVersion) => null;

        public IReadOnlyList<CatalogPersistedSnapshot> LoadAll(DateTimeOffset now) => [];

        public void Save(CatalogPersistedSnapshot snapshot)
        {
            var call = Interlocked.Increment(ref _saveCount);
            var outcome = call switch
            {
                1 => WaitForOutcome(FirstSaveStarted, _firstOutcome, isFirstSave: true),
                2 => WaitForOutcome(SecondSaveStarted, _secondOutcome, isFirstSave: false),
                _ => throw new InvalidOperationException("unexpected save")
            };
            if (outcome is not null)
            {
                throw outcome;
            }
        }

        public void RefreshExpiration(
            string storeCode,
            DateTimeOffset? since,
            string catalogVersion,
            DateTimeOffset expiresAt)
        {
            Save(new CatalogPersistedSnapshot(
                storeCode,
                since,
                GeneratedAt,
                expiresAt,
                catalogVersion,
                []));
        }

        public void CompleteFirst(Exception? exception = null)
        {
            _firstOutcome.TrySetResult(exception);
        }

        public void CompleteSecond(Exception? exception = null)
        {
            _secondOutcome.TrySetResult(exception);
        }

        private Exception? WaitForOutcome(
            TaskCompletionSource started,
            TaskCompletionSource<Exception?> outcome,
            bool isFirstSave)
        {
            started.TrySetResult();
            var failure = outcome.Task.GetAwaiter().GetResult();
            if (isFirstSave && failure is not null)
            {
                // 受控复现：让调用方已经拿到 Invalidated 结果时，
                // 后台 flight 仍停留在“已决定失败、尚未释放 reservation”的窗口。
                _holdFirstFailure?.Task.GetAwaiter().GetResult();
            }

            return failure;
        }
    }

    private sealed class CountingBlockingSnapshotStore(int expectedSaves) : ICatalogSnapshotStore
    {
        private int _saveCount;

        public TaskCompletionSource FirstSaveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ExpectedSavesStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowSaves { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<CatalogSnapshotDescriptor> LoadDescriptors(DateTimeOffset now) => [];

        public CatalogPersistedSnapshot? Load(
            string storeCode,
            DateTimeOffset? since,
            string catalogVersion) => null;

        public IReadOnlyList<CatalogPersistedSnapshot> LoadAll(DateTimeOffset now) => [];

        public void Save(CatalogPersistedSnapshot snapshot)
        {
            var count = Interlocked.Increment(ref _saveCount);
            FirstSaveStarted.TrySetResult();
            if (count >= expectedSaves)
            {
                ExpectedSavesStarted.TrySetResult();
            }

            AllowSaves.Task.GetAwaiter().GetResult();
        }

        public void RefreshExpiration(
            string storeCode,
            DateTimeOffset? since,
            string catalogVersion,
            DateTimeOffset expiresAt)
        {
            Save(new CatalogPersistedSnapshot(
                storeCode,
                since,
                GeneratedAt,
                expiresAt,
                catalogVersion,
                []));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hbpos-catalog-cache-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SnapshotsBeyondRetentionArePrunedWhileLatestKnownGoodSurvives()
    {
        // 中文注释：保留期 2 小时、每店 8 版本；验证"先达到者淘汰、最新 LKG 永不受时间淘汰"。
        var clock = new MutableTimeProvider(GeneratedAt);
        var cache = new CatalogIndexCache(
            clock,
            TimeSpan.FromMinutes(2),
            TimeSpan.FromHours(2),
            maxSnapshotsPerStore: 8);

        async Task<CatalogIndexBuildResult?> Publish(string version)
        {
            return await cache.GetOrBuildAsync(
                "S01",
                since: null,
                _ => Task.FromResult<CatalogIndexBuildResult?>(CreateResult("S01", version)),
                CancellationToken.None);
        }

        Assert.NotNull(await Publish("v1"));
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.NotNull(await Publish("v2"));

        // 中文注释：v1（t=2h 过期）超过保留期；v2（t=2h10m 过期）仍在保留期内。
        clock.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromMinutes(59)));
        Assert.NotNull(await Publish("v3"));

        Assert.Null(cache.GetByVersion("S01", since: null, "v1"));
        Assert.NotNull(cache.GetByVersion("S01", since: null, "v2"));
        Assert.NotNull(cache.GetByVersion("S01", since: null, "v3"));

        // 中文注释：再次推进超过保留期：v2、v3 均过期且非最新被淘汰；v4（当前最新）保留。
        clock.Advance(TimeSpan.FromHours(3));
        Assert.NotNull(await Publish("v4"));

        Assert.Null(cache.GetByVersion("S01", since: null, "v2"));
        Assert.Null(cache.GetByVersion("S01", since: null, "v3"));
        Assert.NotNull(cache.GetByVersion("S01", since: null, "v4"));
    }
}
