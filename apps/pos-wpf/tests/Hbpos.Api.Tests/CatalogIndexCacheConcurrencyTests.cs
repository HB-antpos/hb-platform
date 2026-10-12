using Hbpos.Api.Services;
using Hbpos.Contracts.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hbpos.Api.Tests;

/// <summary>
/// 目录缓存并发行为回归：懒加载移出全局锁并单飞、容量拒绝不回退旧版本、构建期间被失效后立即重建、
/// 内容未变沿用索引实例、快照续期不重算整文件哈希。全部用确定性闸门，不靠 sleep。
/// </summary>
public sealed class CatalogIndexCacheConcurrencyTests
{
    // 用真实当前时间：测试里的快照描述走系统时钟的过期清理，固定的过去日期会被当成已过期。
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Concurrent_requests_for_the_same_lazy_version_load_the_snapshot_once()
    {
        var store = new GatedSnapshotStore([Descriptor("S01", "catalog-v1:a", Now)]);
        var cache = new CatalogIndexCache(store, new NoopScheduler());

        var callers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => cache.GetByVersion("S01", null, "catalog-v1:a")))
            .ToArray();
        Assert.True(store.LoadEntered.Wait(Wait));
        store.Release.Set();
        var results = await Task.WhenAll(callers).WaitAsync(Wait);

        // 单飞：不论后到的调用方是在加载中等待还是加载完成后命中驻留，磁盘只被读取一次，且大家拿到同一个不可变实例。
        Assert.Equal(1, store.LoadCount);
        Assert.All(results, result => Assert.Same(results[0], result));
        Assert.NotNull(results[0]);
    }

    [Fact]
    public async Task Lazy_load_in_progress_does_not_block_peek_or_lease_creation_for_other_stores()
    {
        var store = new GatedSnapshotStore(
        [
            Descriptor("S01", "catalog-v1:a", Now),
            Descriptor("S02", "catalog-v1:b", Now)
        ]);
        var cache = new CatalogIndexCache(store, new NoopScheduler());

        var slowLoad = Task.Run(() => cache.GetByVersion("S01", null, "catalog-v1:a"));
        try
        {
            Assert.True(store.LoadEntered.Wait(Wait));

            // 加载卡在锁外：热路径（PeekLatestVersion、CreateFullLease）必须立即完成。
            var hotPath = Task.Run(() =>
            {
                var peek = cache.PeekLatestVersion("S02");
                var lease = cache.CreateFullLease(CreateResult("S02", "catalog-v1:lease", 1));
                return (peek, lease);
            });
            var (peeked, leased) = await hotPath.WaitAsync(Wait);

            Assert.Equal("catalog-v1:b", peeked?.CatalogVersion);
            Assert.NotNull(leased);
            Assert.False(slowLoad.IsCompleted);
        }
        finally
        {
            store.Release.Set();
        }

        Assert.NotNull(await slowLoad.WaitAsync(Wait));
    }

    [Fact]
    public async Task Capacity_rejected_lazy_snapshot_does_not_fall_back_to_older_versions_or_reload_while_backing_off()
    {
        var time = new MutableTimeProvider(Now);
        var store = new GatedSnapshotStore(
        [
            Descriptor("S01", "catalog-v1:new", Now),
            Descriptor("S01", "catalog-v1:old", Now.AddMinutes(-5))
        ])
        { ReleaseImmediately = true };
        var cache = new CatalogIndexCache(
            time, TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(30), 8, store, new NoopScheduler(),
            softItemCapacity: 100, hardItemCapacity: 100);
        // 8 张租约占满该店版本数上限：任何新驻留版本都会被容量拒绝（与损坏不同，数据本身是好的）。
        foreach (var number in Enumerable.Range(1, 8))
        {
            cache.CreateFullLease(CreateResult("S01", $"catalog-v1:leased-{number}", 1));
        }

        // 触发 GetLatestSnapshot（无驻留且有描述）。构建结果同样被拒，最终抛容量繁忙，这里只关心加载次数。
        await Assert.ThrowsAsync<CatalogCapacityBusyException>(() => cache.GetOrBuildAsync(
            "S01", null, _ => Task.FromResult<CatalogIndexBuildResult?>(CreateResult("S01", "catalog-v1:built", 1)),
            CancellationToken.None));

        // 只尝试最新版本一次；被拒后既不回退去解压更旧的版本，退避期内重试也不再解压。
        Assert.Equal(["catalog-v1:new"], store.LoadedVersions);

        // 描述保留：租约释放后（这里用推进时间越过退避 + 清掉租约占位的方式）仍可加载。
        Assert.Null(cache.GetByVersion("S01", null, "catalog-v1:new"));
        Assert.Equal(1, store.LoadCount);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(cache.GetByVersion("S01", null, "catalog-v1:new"));
        Assert.Equal(2, store.LoadCount);
    }

    [Fact]
    public async Task Corrupt_newest_snapshot_is_dropped_once_and_the_next_newest_is_used()
    {
        var store = new GatedSnapshotStore(
        [
            Descriptor("S01", "catalog-v1:new", Now),
            Descriptor("S01", "catalog-v1:old", Now.AddMinutes(-5))
        ])
        { ReleaseImmediately = true, CorruptVersions = { "catalog-v1:new" } };
        var cache = new CatalogIndexCache(store, new NoopScheduler());

        var first = await cache.GetOrBuildAsync("S01", null, ThrowingBuilder, CancellationToken.None);
        var second = await cache.GetOrBuildAsync("S01", null, ThrowingBuilder, CancellationToken.None);

        // 损坏（区别于容量拒绝）：跳过并移除描述，退回次新版本；之后不再重复解压损坏的那份。
        Assert.Equal("catalog-v1:old", first?.CatalogIndex.CatalogVersion);
        Assert.Same(first, second);
        Assert.Equal(1, store.LoadedVersions.Count(version => version == "catalog-v1:new"));

        static Task<CatalogIndexBuildResult?> ThrowingBuilder(CancellationToken _) =>
            Task.FromException<CatalogIndexBuildResult?>(new InvalidOperationException("不应冷构建"));
    }

    [Fact]
    public async Task Force_refresh_invalidated_during_build_fails_with_a_marked_invalidation_error()
    {
        var cache = new CatalogIndexCache();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var refresh = cache.ForceRefreshAndPublishAsync("S01", null, async token =>
        {
            started.SetResult();
            await allow.Task.WaitAsync(token);
            return CreateResult("S01", "catalog-v1:build", 1);
        }, CancellationToken.None);
        await started.Task.WaitAsync(Wait);
        cache.InvalidateStore("S01");
        allow.SetResult();

        // 异常类型保持 InvalidOperationException（既有合同），但带标记，后台刷新服务据此识别为"需要立即重建"。
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => refresh);
        Assert.True(CatalogIndexCache.IsBuildInvalidated(exception));
        Assert.False(CatalogIndexCache.IsBuildInvalidated(new InvalidOperationException("普通失败")));
    }

    [Fact]
    public async Task Background_refresh_requeues_an_invalidated_build_immediately_without_backoff_or_counting_a_failure()
    {
        var delays = new List<TimeSpan>();
        var attempts = 0;
        var services = new ServiceCollection();
        services.AddScoped<ICatalogIndexRefreshWorker>(_ => new DelegateWorker(_ =>
        {
            // 前两次构建期间被失效，第三次成功。
            if (Interlocked.Increment(ref attempts) <= 2)
            {
                throw CatalogIndexCache.CreateBuildInvalidatedException();
            }

            return Task.CompletedTask;
        }));
        await using var provider = services.BuildServiceProvider();
        using var service = new CatalogBackgroundRefreshService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CatalogBackgroundRefreshService>.Instance,
            (delay, _) =>
            {
                lock (delays)
                {
                    delays.Add(delay);
                }

                return Task.CompletedTask;
            });
        await service.StartAsync(CancellationToken.None);
        try
        {
            await service.QueueRefreshAsync("S01").WaitAsync(Wait);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(3, attempts);
        // 没有走失败退避（15/30/60 分钟），请求也没有因此失败。
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Background_refresh_falls_back_to_the_failure_path_after_repeated_invalidation()
    {
        var delays = new List<TimeSpan>();
        var attempts = 0;
        var services = new ServiceCollection();
        services.AddScoped<ICatalogIndexRefreshWorker>(_ => new DelegateWorker(_ =>
        {
            // 前 4 次（首次 + 3 次立即重建）都被失效；之后成功。
            if (Interlocked.Increment(ref attempts) <= 4)
            {
                throw CatalogIndexCache.CreateBuildInvalidatedException();
            }

            return Task.CompletedTask;
        }));
        await using var provider = services.BuildServiceProvider();
        using var service = new CatalogBackgroundRefreshService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CatalogBackgroundRefreshService>.Instance,
            (delay, _) =>
            {
                lock (delays)
                {
                    delays.Add(delay);
                }

                return Task.CompletedTask;
            });
        await service.StartAsync(CancellationToken.None);
        try
        {
            await service.QueueRefreshAsync("S01").WaitAsync(Wait);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // 立即重建有上限：第 4 次仍被失效后，才按现有失败路径退避一次（首档 15 分钟），随后成功。
        Assert.Equal(5, attempts);
        Assert.Equal([TimeSpan.FromMinutes(15)], delays);
    }

    [Fact]
    public async Task Unchanged_content_reuses_the_index_instance_and_keeps_its_page_cache()
    {
        var time = new MutableTimeProvider(Now);
        var cache = new CatalogIndexCache(time, TimeSpan.FromMinutes(2));
        var buildCount = 0;

        var first = await cache.GetOrBuildAsync("S01", null, BuildAsync, CancellationToken.None);
        first!.CatalogIndex.WarmUpStandardV2Page(0);
        var checksumBefore = first.CatalogIndex.GetPage(null, 5000, checksumVersion: 2).PageChecksum;
        time.Advance(TimeSpan.FromMinutes(3));
        var rebuilt = await cache.GetOrBuildAsync("S01", null, BuildAsync, CancellationToken.None);

        Assert.Equal(2, buildCount);
        // 内容没变：沿用上一版版本号与同一个索引实例（页摘要缓存随之保留），不再新建 CatalogSellableIndex。
        Assert.Same(first.CatalogIndex, rebuilt!.CatalogIndex);
        Assert.Equal(first.CatalogIndex.CatalogVersion, rebuilt.CatalogIndex.CatalogVersion);
        Assert.Equal(checksumBefore, rebuilt.CatalogIndex.GetPage(null, 5000, checksumVersion: 2).PageChecksum);
        // 会随本次构建变化的字段仍然更新：来源有效期决定过期时间。
        Assert.Equal(Now.AddMinutes(90), rebuilt.SourceValidUntil);

        Task<CatalogIndexBuildResult?> BuildAsync(CancellationToken _)
        {
            buildCount++;
            return Task.FromResult<CatalogIndexBuildResult?>(CreateResult(
                "S01", $"catalog-v1:build-{buildCount}", 3,
                sourceValidUntil: Now.AddMinutes(buildCount == 1 ? 60 : 90)));
        }
    }

    [Fact]
    public void Refresh_expiration_does_not_rehash_a_body_whose_fingerprint_is_already_verified()
    {
        using var directory = new TemporaryDirectory();
        var store = new GzipCatalogSnapshotStore(directory.Path);
        var snapshot = new CatalogPersistedSnapshot(
            "S01", null, Now, Now.AddHours(1), "catalog-v1:one", CreateResult("S01", "catalog-v1:one", 3).SellableItems);
        store.Save(snapshot);
        var bodyPath = Assert.Single(Directory.GetFiles(directory.Path, "*.json.gz", SearchOption.AllDirectories));

        // 以独占方式占住正文：任何读取正文内容（重算哈希）的动作都会 IOException，
        // 只读元数据（长度、修改时间）不受影响。续期成功即证明没有重读整份文件。
        using (new FileStream(bodyPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.RefreshExpiration("S01", null, "catalog-v1:one", Now.AddHours(5));
        }

        Assert.Equal(Now.AddHours(5), Assert.Single(store.LoadDescriptors(Now)).ExpiresAt);
    }

    [Fact]
    public void Refresh_expiration_after_restart_verifies_the_body_hash_once_and_remembers_it()
    {
        using var directory = new TemporaryDirectory();
        new GzipCatalogSnapshotStore(directory.Path).Save(new CatalogPersistedSnapshot(
            "S01", null, Now, Now.AddHours(1), "catalog-v1:one", CreateResult("S01", "catalog-v1:one", 3).SellableItems));
        var bodyPath = Assert.Single(Directory.GetFiles(directory.Path, "*.json.gz", SearchOption.AllDirectories));
        // 新实例没有指纹（模拟进程重启）：第一次续期必须完整校验哈希。
        var restarted = new GzipCatalogSnapshotStore(directory.Path);

        using (new FileStream(bodyPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => restarted.RefreshExpiration("S01", null, "catalog-v1:one", Now.AddHours(2)));
        }

        restarted.RefreshExpiration("S01", null, "catalog-v1:one", Now.AddHours(2));
        using (new FileStream(bodyPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            restarted.RefreshExpiration("S01", null, "catalog-v1:one", Now.AddHours(3));
        }

        Assert.Equal(Now.AddHours(3), Assert.Single(restarted.LoadDescriptors(Now)).ExpiresAt);
    }

    private static CatalogSnapshotDescriptor Descriptor(string store, string version, DateTimeOffset generatedAt) =>
        new(store, null, generatedAt, Now.AddHours(10), version);

    private static CatalogIndexBuildResult CreateResult(
        string storeCode,
        string catalogVersion,
        int itemCount,
        DateTimeOffset? sourceValidUntil = null)
    {
        var items = CreateItems(storeCode, itemCount);
        return new CatalogIndexBuildResult(
            storeCode,
            Now,
            items,
            new CatalogSellableIndex(storeCode, Now, items, catalogVersion),
            sourceValidUntil);
    }

    private static IReadOnlyList<SellableItemDto> CreateItems(string storeCode, int itemCount) =>
        Enumerable.Range(1, itemCount)
            .Select(number => new SellableItemDto(
                storeCode, $"P{number:D4}", null, $"Item {number}", $"B{number:D4}", null, null, 1m,
                PriceSourceKind.StoreRetailPrice, "门店价", 1m, Now))
            .ToArray();

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hbpos-catalog-concurrency-{Guid.NewGuid():N}");
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

    private sealed class DelegateWorker(Func<CancellationToken, Task> refreshAsync) : ICatalogIndexRefreshWorker
    {
        public Task RefreshCatalogIndexAsync(string storeCode, CancellationToken cancellationToken) =>
            refreshAsync(cancellationToken);
    }

    private sealed class NoopScheduler : ICatalogBackgroundRefreshScheduler
    {
        public void QueueRefresh(string storeCode)
        {
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    /// <summary>可由闸门控制 Load 何时返回的快照仓库，并记录每次加载的版本。</summary>
    private sealed class GatedSnapshotStore(IReadOnlyList<CatalogSnapshotDescriptor> descriptors) : ICatalogSnapshotStore
    {
        private readonly List<string> _loadedVersions = [];
        private int _loadCount;

        public ManualResetEventSlim LoadEntered { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public bool ReleaseImmediately { get; init; }

        public HashSet<string> CorruptVersions { get; } = [];

        public int LoadCount => Volatile.Read(ref _loadCount);

        public IReadOnlyList<string> LoadedVersions
        {
            get
            {
                lock (_loadedVersions)
                {
                    return _loadedVersions.ToArray();
                }
            }
        }

        public IReadOnlyList<CatalogSnapshotDescriptor> LoadDescriptors(DateTimeOffset now) => descriptors;

        public CatalogPersistedSnapshot? Load(string storeCode, DateTimeOffset? since, string catalogVersion)
        {
            Interlocked.Increment(ref _loadCount);
            lock (_loadedVersions)
            {
                _loadedVersions.Add(catalogVersion);
            }

            LoadEntered.Set();
            if (!ReleaseImmediately)
            {
                // 防泄漏的上限；正常由测试 Release.Set() 放行。
                Release.Wait(TimeSpan.FromSeconds(30));
            }

            if (CorruptVersions.Contains(catalogVersion))
            {
                throw new InvalidDataException("corrupt");
            }

            var descriptor = descriptors.First(candidate => candidate.CatalogVersion == catalogVersion);
            return new CatalogPersistedSnapshot(
                storeCode, since, descriptor.GeneratedAt, descriptor.ExpiresAt, catalogVersion,
                CreateItems(storeCode, 1));
        }

        public IReadOnlyList<CatalogPersistedSnapshot> LoadAll(DateTimeOffset now) => [];

        public void Save(CatalogPersistedSnapshot snapshot)
        {
        }

        public void RefreshExpiration(string storeCode, DateTimeOffset? since, string catalogVersion, DateTimeOffset expiresAt)
        {
        }
    }
}
