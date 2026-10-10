using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

public sealed class SpecialProductsWorkflowServiceTests
{
    [Fact]
    public async Task PreloadAsync_then_EnsureLoadedAsync_reuses_loaded_special_product_cache()
    {
        var repository = new FakeCatalogRepository
        {
            SpecialItems = [CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true)]
        };
        var service = CreateWorkflowService(repository: repository);

        var preload = await service.PreloadAsync("S001");
        var ensured = await service.EnsureLoadedAsync("S001");

        Assert.Equal(1, repository.LoadSpecialProductItemsCallCount);
        Assert.Single(preload.SpecialItems);
        Assert.Single(ensured.SpecialItems);
    }

    [Fact]
    public async Task EnsureLoadedAsync_reuses_inflight_preload_for_same_store()
    {
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new FakeCatalogRepository
        {
            SpecialItems = [CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true)],
            BeforeLoadSpecialProductItemsAsync = () => releaseLoad.Task
        };
        var service = CreateWorkflowService(repository: repository);

        var preloadTask = service.PreloadAsync("S001");
        var ensureTask = service.EnsureLoadedAsync("S001");
        releaseLoad.SetResult();
        await Task.WhenAll(preloadTask, ensureTask);

        Assert.Equal(1, repository.LoadSpecialProductItemsCallCount);
    }

    [Fact]
    public async Task SearchAsync_returns_deduplicated_non_special_results_for_store()
    {
        var duplicateA = CreateItem("SKU-001", "Alpha", "930001");
        var duplicateB = duplicateA with { LookupCode = "ITEM-001", Barcode = "930001" };
        var alreadySpecial = CreateItem("SKU-002", "Beta", "930002", isSpecialProduct: true);
        var otherStore = duplicateA with { StoreCode = "S002" };
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([duplicateA, duplicateB, alreadySpecial, otherStore]);
        var service = CreateWorkflowService(index: index);

        var result = await service.SearchAsync("S001", "930001");

        var item = Assert.Single(result.Items);
        Assert.Equal("SKU-001", item.ProductCode);
        Assert.False(item.IsSpecialProduct);
    }

    [Fact]
    public async Task DownloadAsync_refreshes_index_and_returns_special_items()
    {
        var downloaded = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var repository = new FakeCatalogRepository
        {
            SellableItems = [downloaded],
            SpecialItems = [downloaded]
        };
        var specialProductService = new FakeSpecialProductService
        {
            DownloadResult = new SpecialProductDownloadResult("S001", 1, 1, 1, 1, 0)
        };
        var index = new LocalSellableItemIndex();
        var service = CreateWorkflowService(index, repository, specialProductService);

        var result = await service.DownloadAsync("S001");

        Assert.Equal(1, specialProductService.DownloadCallCount);
        Assert.Single(result.SpecialItems);
        Assert.Contains(index.Search("S001", "930001"), item => item.ProductCode == "SKU-001" && item.IsSpecialProduct);
    }

    [Fact]
    public async Task MarkSpecialProductAsync_upserts_changed_items_without_full_refresh()
    {
        var original = CreateItem("SKU-001", "Alpha", "930001");
        var updated = original with { IsSpecialProduct = true };
        var repository = new FakeCatalogRepository
        {
            SpecialItems = [updated]
        };
        var specialProductService = new FakeSpecialProductService
        {
            MarkResult = new SpecialProductMarkResult([updated], [updated])
        };
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([original]);
        var service = CreateWorkflowService(index, repository, specialProductService);

        var result = await service.MarkSpecialProductAsync("S001", "SKU-001", true);

        Assert.Equal(1, specialProductService.MarkCallCount);
        Assert.Equal(0, repository.LoadSellableItemsCallCount);
        Assert.Equal(0, repository.LoadSpecialProductItemsCallCount);
        Assert.Single(result.SpecialItems);
        Assert.Contains(index.Search("S001", "930001"), item => item.ProductCode == "SKU-001" && item.IsSpecialProduct);
    }

    [Fact]
    public async Task MarkSpecialProductAsync_falls_back_to_full_refresh_when_changed_items_are_empty()
    {
        var updated = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var repository = new FakeCatalogRepository
        {
            SellableItems = [updated],
            SpecialItems = [updated]
        };
        var specialProductService = new FakeSpecialProductService
        {
            MarkResult = new SpecialProductMarkResult([], [updated])
        };
        var index = new LocalSellableItemIndex();
        var service = CreateWorkflowService(index, repository, specialProductService);

        var result = await service.MarkSpecialProductAsync("S001", "SKU-001", true);

        Assert.Equal(1, specialProductService.MarkCallCount);
        Assert.Equal(1, repository.LoadSellableItemsCallCount);
        Assert.Equal(1, repository.LoadSpecialProductItemsCallCount);
        Assert.Single(result.SpecialItems);
        Assert.Contains(index.Search("S001", "930001"), item => item.ProductCode == "SKU-001" && item.IsSpecialProduct);
    }

    [Fact]
    public async Task ReorderAsync_saves_updated_special_product_order()
    {
        var first = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var second = CreateItem("SKU-002", "Beta", "930002", isSpecialProduct: true);
        var repository = new FakeCatalogRepository
        {
            SpecialItems = [first, second]
        };
        var service = CreateWorkflowService(repository: repository);

        var result = await service.ReorderAsync("S001", [first, second], "SKU-001", 1);

        Assert.NotNull(result);
        Assert.Equal(["SKU-002", "SKU-001"], result.SpecialItems.Select(item => item.ProductCode).ToArray());
        Assert.Equal(["SKU-002", "SKU-001"], Assert.Single(repository.SavedOrders));
    }

    [Fact]
    public async Task UpdateSpecialProductPriceAsync_upserts_changed_items_into_price_index()
    {
        var oldItem = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var repricedItem = oldItem with { RetailPrice = 6.5m };
        var index = new LocalSellableItemIndex();
        index.ReplaceAll([oldItem]);
        var repository = new FakeCatalogRepository { SpecialItems = [repricedItem] };
        var specialProductService = new FakeSpecialProductService
        {
            PriceResult = new SpecialProductMarkResult([repricedItem], [repricedItem])
        };
        var service = CreateWorkflowService(index, repository, specialProductService);

        var result = await service.UpdateSpecialProductPriceAsync(" S001 ", " SKU-001 ", 6.5m);

        Assert.Equal(("S001", "SKU-001", 6.5m), specialProductService.LastPriceCall);
        Assert.Equal(6.5m, Assert.Single(result.SpecialItems).RetailPrice);
        Assert.Equal(6.5m, result.RetailPrice);
        // 收银扫码走内存索引，必须立即按新价。
        Assert.Equal(6.5m, Assert.Single(index.Search("S001", "930001")).RetailPrice);
        // 服务端已返回改价后的查码项时不必整库重载。
        Assert.Equal(0, repository.LoadSellableItemsCallCount);
        Assert.Equal(0, repository.LoadSpecialProductItemsCallCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1.5)]
    [InlineData(100000)]
    [InlineData(5.999)]
    public async Task UpdateSpecialProductPriceAsync_rejects_invalid_price_before_calling_the_service(double price)
    {
        var specialProductService = new FakeSpecialProductService();
        var service = CreateWorkflowService(specialProductService: specialProductService);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.UpdateSpecialProductPriceAsync("S001", "SKU-001", (decimal)price));

        Assert.Equal(0, specialProductService.PriceCallCount);
    }

    [Theory]
    [InlineData(0.01, true)]
    [InlineData(99999.99, true)]
    [InlineData(12.5, true)]
    [InlineData(0, false)]
    [InlineData(99999.995, false)]
    [InlineData(0.001, false)]
    public void SpecialProductPriceRules_accepts_only_positive_two_decimal_prices_up_to_the_maximum(double price, bool valid)
    {
        Assert.Equal(valid, SpecialProductPriceRules.IsValid((decimal)price));
    }

    [Fact]
    public void UndoAddToCart_reduces_quantity_removes_the_line_and_ignores_missing_lines()
    {
        var cart = new PosCartService();
        var item = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var service = new SpecialProductsWorkflowService(
            new LocalSellableItemIndex(),
            cart,
            new FakeCatalogRepository(),
            new FakeSpecialProductService());
        var line = cart.AddItem(item);
        cart.AddItem(item);

        var reduced = service.UndoAddToCart(line, 1m);

        Assert.True(reduced.Changed);
        Assert.False(reduced.RemovedLine);
        Assert.Equal(1m, line.Quantity);

        var removed = service.UndoAddToCart(line, 1m);

        Assert.True(removed.Changed);
        Assert.True(removed.RemovedLine);
        Assert.Empty(cart.Lines);

        // 行已经不在购物车里：什么都不做。
        var missing = service.UndoAddToCart(line, 1m);
        Assert.False(missing.Changed);
        Assert.False(missing.RemovedLine);
    }

    [Fact]
    public async Task UpdateSpecialProductPriceAsync_reloads_catalog_when_server_returns_no_items()
    {
        var item = CreateItem("SKU-001", "Alpha", "930001", isSpecialProduct: true);
        var repository = new FakeCatalogRepository
        {
            SellableItems = [item with { RetailPrice = 9m }],
            SpecialItems = [item with { RetailPrice = 9m }]
        };
        var index = new LocalSellableItemIndex();
        var service = CreateWorkflowService(index, repository, new FakeSpecialProductService());

        var result = await service.UpdateSpecialProductPriceAsync("S001", "SKU-001", 9m);

        Assert.Equal(1, repository.LoadSellableItemsCallCount);
        Assert.Equal(1, repository.LoadSpecialProductItemsCallCount);
        Assert.Equal(9m, Assert.Single(result.SpecialItems).RetailPrice);
        Assert.Equal(9m, Assert.Single(index.Search("S001", "930001")).RetailPrice);
    }

    private static SpecialProductsWorkflowService CreateWorkflowService(
        LocalSellableItemIndex? index = null,
        FakeCatalogRepository? repository = null,
        FakeSpecialProductService? specialProductService = null)
    {
        return CreateWorkflowServiceCore(
            index ?? new LocalSellableItemIndex(),
            repository ?? new FakeCatalogRepository(),
            specialProductService ?? new FakeSpecialProductService());
    }

    private static SpecialProductsWorkflowService CreateWorkflowServiceCore(
        LocalSellableItemIndex index,
        FakeCatalogRepository repository,
        FakeSpecialProductService specialProductService)
    {
        return new SpecialProductsWorkflowService(
            index,
            new PosCartService(),
            repository,
            specialProductService);
    }

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

    private sealed class FakeCatalogRepository : ILocalCatalogRepository
    {
        public IReadOnlyList<SellableItemDto> SellableItems { get; set; } = [];

        public IReadOnlyList<SellableItemDto> SpecialItems { get; set; } = [];

        public List<string[]> SavedOrders { get; } = [];

        public int LoadSpecialProductItemsCallCount { get; private set; }

        public int LoadSellableItemsCallCount { get; private set; }

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

        public Task<IReadOnlyList<SellableItemDto>> LoadSpecialProductItemsAsync(string storeCode, CancellationToken cancellationToken = default)
        {
            LoadSpecialProductItemsCallCount++;
            if (BeforeLoadSpecialProductItemsAsync is null)
            {
                return Task.FromResult(SpecialItems);
            }

            return LoadSpecialItemsCoreAsync();

            async Task<IReadOnlyList<SellableItemDto>> LoadSpecialItemsCoreAsync()
            {
                await BeforeLoadSpecialProductItemsAsync();
                return SpecialItems;
            }
        }

        public Task SaveSpecialProductOrderAsync(string storeCode, IEnumerable<string> productCodes, CancellationToken cancellationToken = default)
        {
            SavedOrders.Add(productCodes.ToArray());
            SpecialItems = productCodes
                .Select(code => SpecialItems.First(item => string.Equals(item.ProductCode, code, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            return Task.CompletedTask;
        }

        public Task<int> UpdateSpecialProductFlagAsync(string storeCode, string productCode, bool isSpecialProduct, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task<int> ClearSpecialProductFlagsExceptAsync(string storeCode, IEnumerable<string> productCodesToKeep, CancellationToken cancellationToken = default)
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
        public int MarkCallCount { get; private set; }

        public int DownloadCallCount { get; private set; }

        public SpecialProductMarkResult MarkResult { get; init; } = new([], []);

        public int PriceCallCount { get; private set; }

        public (string StoreCode, string ProductCode, decimal RetailPrice)? LastPriceCall { get; private set; }

        public SpecialProductMarkResult PriceResult { get; init; } = new([], []);

        public Task<SpecialProductMarkResult> UpdateSpecialProductPriceAsync(
            string storeCode,
            string productCode,
            decimal retailPrice,
            CancellationToken cancellationToken = default)
        {
            PriceCallCount++;
            LastPriceCall = (storeCode, productCode, retailPrice);
            return Task.FromResult(PriceResult);
        }

        public SpecialProductDownloadResult DownloadResult { get; init; } =
            new("S001", 0, 0, 0, 0, 0);

        public Task<SpecialProductMarkResult> MarkSpecialProductAsync(
            string storeCode,
            string productCode,
            bool isSpecialProduct,
            CancellationToken cancellationToken = default)
        {
            MarkCallCount++;
            return Task.FromResult(MarkResult);
        }

        public Task<SpecialProductDownloadResult> DownloadSpecialProductsAsync(
            string storeCode,
            CancellationToken cancellationToken = default,
            IProgress<SpecialProductDownloadProgress>? progress = null)
        {
            DownloadCallCount++;
            return Task.FromResult(DownloadResult);
        }
    }
}
