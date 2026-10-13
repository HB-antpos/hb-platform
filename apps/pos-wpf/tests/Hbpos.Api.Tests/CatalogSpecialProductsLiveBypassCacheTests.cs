using BlazorApp.Shared.Models;
using Hbpos.Api.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Api.Tests;

/// <summary>
/// 证明特殊商品下载绕过整店目录缓存：先让缓存建好，再直接改库（不调用任何 InvalidateStore，
/// 模拟后台管理系统/其他服务/手工改库），特殊商品分页必须立刻返回新数据；
/// 同一时刻目录工件接口仍是旧缓存（对照，证明场景成立且其他目录接口没被改动）。
/// 这些用例是“改库后立刻可见”类：旧实现（读缓存工件）上应当失败。
/// </summary>
public sealed class CatalogSpecialProductsLiveBypassCacheTests : IAsyncLifetime
{
    private readonly SpecialProductsSqliteDatabase database = new();
    private CatalogIndexCache cache = null!;
    private CatalogService service = null!;

    public async Task InitializeAsync()
    {
        var db = database.Client;
        await SpecialProductsLiveTestData.AddStoreAsync(db, "S01");
        // A：只有条码；B：条码+货号；C、D：普通商品。
        await SpecialProductsLiveTestData.AddProductAsync(db, "PA", "BAR-A", null, 10m, 1);
        await SpecialProductsLiveTestData.AddProductAsync(db, "PB", "BAR-B", "ITEM-B", 20m, 1);
        await SpecialProductsLiveTestData.AddProductAsync(db, "PC", "BAR-C", null, 30m, 1);
        await SpecialProductsLiveTestData.AddProductAsync(db, "PD", "BAR-D", null, 40m, 1);
        await SpecialProductsLiveTestData.AddStorePriceAsync(db, "S01", "PA", 9m, 10, special: true);
        await SpecialProductsLiveTestData.AddStorePriceAsync(db, "S01", "PB", 19m, 10, special: true);
        await SpecialProductsLiveTestData.AddStorePriceAsync(db, "S01", "PC", 29m, 10, special: false);
        await SpecialProductsLiveTestData.AddStorePriceAsync(db, "S01", "PD", 39m, 10, special: false);

        cache = new CatalogIndexCache();
        service = database.CreateService(cache);
        // 预热：整店目录工件建好并进入共享缓存（默认 TTL 20 分钟，之后的改库都不会主动清缓存）。
        var warm = await CachedArtifactSpecialCodesAsync();
        Assert.Equal(new[] { "PA", "PB" }, warm);
    }

    public async Task DisposeAsync() => await database.DisposeAsync();

    [Fact]
    public async Task Newly_marked_special_product_appears_without_cache_invalidation()
    {
        await database.Client.Updateable<StoreRetailPrice>()
            .SetColumns(x => x.IsSpecialProduct == true)
            .Where(x => x.StoreCode == "S01" && x.ProductCode == "PC")
            .ExecuteCommandAsync();

        Assert.Equal(new[] { "PA", "PB", "PC" }, await LiveSpecialCodesAsync());
        // 对照：目录工件接口仍是旧缓存。
        Assert.Equal(new[] { "PA", "PB" }, await CachedArtifactSpecialCodesAsync());
    }

    [Fact]
    public async Task Unmarked_special_product_disappears_without_cache_invalidation()
    {
        await database.Client.Updateable<StoreRetailPrice>()
            .SetColumns(x => x.IsSpecialProduct == false)
            .Where(x => x.StoreCode == "S01" && x.ProductCode == "PA")
            .ExecuteCommandAsync();

        Assert.Equal(new[] { "PB" }, await LiveSpecialCodesAsync());
        Assert.Equal(new[] { "PA", "PB" }, await CachedArtifactSpecialCodesAsync());
    }

    [Fact]
    public async Task Changed_special_price_is_visible_without_cache_invalidation()
    {
        var changedAt = SpecialProductsLiveTestData.At(60);
        await database.Client.Updateable<StoreRetailPrice>()
            .SetColumns(x => new StoreRetailPrice
            {
                StoreRetailPriceValue = 21.5m,
                UpdatedAt = changedAt
            })
            .Where(x => x.StoreCode == "S01" && x.ProductCode == "PB")
            .ExecuteCommandAsync();

        var live = await LivePageAsync();
        Assert.Equal(21.5m, live.Items.Single(x => x.ProductCode == "PB").RetailPrice);
        Assert.Equal(changedAt, live.Items.Single(x => x.ProductCode == "PB").UpdatedAt!.Value.UtcDateTime);
        // 对照：缓存里还是旧价。
        var cached = await CachedArtifactSpecialItemsAsync();
        Assert.All(cached.Where(x => x.ProductCode == "PB"), x => Assert.Equal(19m, x.RetailPrice));
    }

    [Fact]
    public async Task Deleted_special_product_disappears_without_cache_invalidation()
    {
        await database.Client.Updateable<Product>()
            .SetColumns(x => x.IsDeleted == true)
            .Where(x => x.ProductCode == "PA")
            .ExecuteCommandAsync();

        Assert.Equal(new[] { "PB" }, await LiveSpecialCodesAsync());
        Assert.Equal(new[] { "PA", "PB" }, await CachedArtifactSpecialCodesAsync());
    }

    [Fact]
    public async Task New_special_product_inserted_directly_is_visible_without_cache_invalidation()
    {
        await SpecialProductsLiveTestData.AddProductAsync(database.Client, "PE", "BAR-E", "ITEM-E", 50m, 70);
        await SpecialProductsLiveTestData.AddStorePriceAsync(database.Client, "S01", "PE", 45m, 70, special: true);

        var live = await LivePageAsync();
        Assert.Equal(new[] { "PA", "PB", "PE" }, live.Items.Select(x => x.ProductCode).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(3, live.TotalCount);
        Assert.Equal(new[] { "PA", "PB" }, await CachedArtifactSpecialCodesAsync());
    }

    [Fact]
    public async Task Competing_clearance_code_added_directly_changes_the_result_without_cache_invalidation()
    {
        // 他品清货码占用了 PA 唯一的条码：同码决胜后 PA 在整店目录里已不可扫，也就不再是特殊商品行。
        await SpecialProductsLiveTestData.AddClearanceAsync(database.Client, "S01", "PC", "BAR-A", 1m, 80);

        Assert.Equal(new[] { "PB" }, await LiveSpecialCodesAsync());
        Assert.Equal(new[] { "PA", "PB" }, await CachedArtifactSpecialCodesAsync());
    }

    [Fact]
    public async Task Live_query_never_reads_writes_or_invalidates_the_catalog_index_cache()
    {
        var throwingCache = new ThrowingCatalogIndexCache();
        var isolated = database.CreateService(throwingCache);

        var page = await isolated.GetSpecialProductsPageAsync("S01", null, 500, CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal(new[] { "PA", "PB" }, page!.Items.Select(x => x.ProductCode).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(0, throwingCache.CallCount);
    }

    [Fact]
    public async Task Other_catalog_endpoints_keep_serving_the_cached_artifact_until_it_is_invalidated()
    {
        await database.Client.Updateable<StoreRetailPrice>()
            .SetColumns(x => x.IsSpecialProduct == true)
            .Where(x => x.StoreCode == "S01" && x.ProductCode == "PC")
            .ExecuteCommandAsync();

        // 目录分页、sync-plan 等仍走缓存工件：改库后未失效前看到的还是旧数据。
        Assert.Equal(new[] { "PA", "PB" }, await CachedArtifactSpecialCodesAsync());

        // 缓存失效逻辑保持不变：失效后重建才看到新数据。
        cache.InvalidateStore("S01");
        Assert.Equal(new[] { "PA", "PB", "PC" }, await CachedArtifactSpecialCodesAsync());
    }

    [Fact]
    public async Task Mid_walk_changes_neither_crash_nor_duplicate_already_returned_items()
    {
        // 再造几个特殊商品，让一页放不下。
        for (var i = 0; i < 6; i++)
        {
            await SpecialProductsLiveTestData.AddProductAsync(database.Client, $"PX{i}", $"BAR-X{i}", null, 5m, 1);
            await SpecialProductsLiveTestData.AddStorePriceAsync(database.Client, "S01", $"PX{i}", 4m, 10, special: true);
        }

        var first = await service.GetSpecialProductsPageAsync("S01", null, 3, CancellationToken.None);
        Assert.NotNull(first);
        Assert.True(first!.HasMore);
        var returned = first.Items.Select(x => x.ProductCode).ToList();

        // 翻页期间：已返回的一项被取消特殊、游标之后的一项被删除、游标之前插入一个新特殊商品、游标之后新增一个。
        await database.Client.Updateable<StoreRetailPrice>()
            .SetColumns(x => x.IsSpecialProduct == false)
            .Where(x => x.StoreCode == "S01" && x.ProductCode == first.Items[0].ProductCode)
            .ExecuteCommandAsync();
        await database.Client.Updateable<Product>()
            .SetColumns(x => x.IsDeleted == true)
            .Where(x => x.ProductCode == "PX5")
            .ExecuteCommandAsync();
        await SpecialProductsLiveTestData.AddProductAsync(database.Client, "PBEFORE", "000-BEFORE", null, 5m, 1);
        await SpecialProductsLiveTestData.AddStorePriceAsync(database.Client, "S01", "PBEFORE", 4m, 10, special: true);
        await SpecialProductsLiveTestData.AddProductAsync(database.Client, "PAFTER", "ZZZ-AFTER", null, 5m, 1);
        await SpecialProductsLiveTestData.AddStorePriceAsync(database.Client, "S01", "PAFTER", 4m, 10, special: true);

        var cursor = first.NextCursor;
        var lookupCodes = first.Items.Select(x => x.LookupCodeNormalized).ToList();
        while (true)
        {
            var page = await service.GetSpecialProductsPageAsync("S01", cursor, 3, CancellationToken.None);
            Assert.NotNull(page);
            returned.AddRange(page!.Items.Select(x => x.ProductCode));
            lookupCodes.AddRange(page.Items.Select(x => x.LookupCodeNormalized));
            if (!page.HasMore)
            {
                break;
            }

            cursor = page.NextCursor;
        }

        // keyset 游标：不重复、整体按查询码升序；游标之后的新增能拿到、已删除的拿不到。
        Assert.Equal(returned.Count, returned.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(lookupCodes.Order(StringComparer.Ordinal), lookupCodes);
        Assert.Contains("PAFTER", returned);
        Assert.DoesNotContain("PX5", returned);
        // 游标之前新插入的 PBEFORE 这一轮不会补发（下次下载会拿到）。
        Assert.DoesNotContain("PBEFORE", returned);
        var next = await service.GetSpecialProductsPageAsync("S01", null, 5000, CancellationToken.None);
        Assert.Contains(next!.Items, x => x.ProductCode == "PBEFORE");
    }

    private async Task<CatalogSpecialProductsPageResponse> LivePageAsync()
    {
        var page = await service.GetSpecialProductsPageAsync("S01", null, 5000, CancellationToken.None);
        Assert.NotNull(page);
        return page!;
    }

    private async Task<string[]> LiveSpecialCodesAsync() =>
        (await LivePageAsync()).Items.Select(x => x.ProductCode).Order(StringComparer.Ordinal).ToArray();

    /// <summary>目录工件接口（分页，走共享缓存）里带特殊标记的行。</summary>
    private async Task<IReadOnlyList<CatalogLookupItemDto>> CachedArtifactSpecialItemsAsync()
    {
        var page = await service.GetSellableItemsPageAsync("S01", null, null, 5000, CancellationToken.None);
        Assert.NotNull(page);
        return page!.Items.Where(x => x.IsSpecialProduct).ToArray();
    }

    private async Task<string[]> CachedArtifactSpecialCodesAsync() =>
        (await CachedArtifactSpecialItemsAsync())
            .Select(x => x.ProductCode)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>任何访问都会计数并抛错，用来证明实时查询完全不碰目录缓存。</summary>
    private sealed class ThrowingCatalogIndexCache : ICatalogIndexCache
    {
        public int CallCount { get; private set; }

        public CatalogDownloadLeaseRegistry DownloadLeases
        {
            get
            {
                CallCount++;
                throw new InvalidOperationException("特殊商品实时查询不应访问目录缓存。");
            }
        }

        public Task<CatalogIndexBuildResult?> GetOrBuildAsync(
            string storeCode, DateTimeOffset? since,
            Func<CancellationToken, Task<CatalogIndexBuildResult?>> buildAsync, CancellationToken cancellationToken) =>
            Fail<Task<CatalogIndexBuildResult?>>();

        public Task<CatalogIndexBuildResult?> GetOrBuildFreshAsync(
            string storeCode, DateTimeOffset? since,
            Func<CancellationToken, Task<CatalogIndexBuildResult?>> buildAsync, CancellationToken cancellationToken) =>
            Fail<Task<CatalogIndexBuildResult?>>();

        public Task<CatalogIndexBuildResult?> ForceRefreshAndPublishAsync(
            string storeCode, DateTimeOffset? since,
            Func<CancellationToken, Task<CatalogIndexBuildResult?>> buildAsync, CancellationToken cancellationToken) =>
            Fail<Task<CatalogIndexBuildResult?>>();

        public CatalogIndexBuildResult? GetByVersion(string storeCode, DateTimeOffset? since, string catalogVersion) =>
            Fail<CatalogIndexBuildResult?>();

        public void InvalidateStore(string storeCode) => Fail<object>();

        public void EnsurePlanCapacity(CatalogIndexBuildResult target) => Fail<object>();

        public CatalogDownloadLease CreateFullLease(CatalogIndexBuildResult target) => Fail<CatalogDownloadLease>();

        public CatalogDownloadLease CreateDeltaLease(
            CatalogIndexBuildResult baseline, CatalogIndexBuildResult target,
            IReadOnlyList<CatalogDeltaOperation> operations) => Fail<CatalogDownloadLease>();

        public CatalogVersionPeek? PeekLatestVersion(string storeCode) => Fail<CatalogVersionPeek?>();

        private T Fail<T>()
        {
            CallCount++;
            throw new InvalidOperationException("特殊商品实时查询不应访问目录缓存。");
        }
    }
}
