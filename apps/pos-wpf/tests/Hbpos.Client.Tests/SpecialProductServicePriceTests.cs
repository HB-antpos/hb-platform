using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

/// <summary>
/// 改价服务层：价格只以服务端为准，先在线写入成功才覆盖本地目录；失败时本地目录一行都不能动。
/// </summary>
public sealed class SpecialProductServicePriceTests
{
    [Fact]
    public async Task UpdateSpecialProductPriceAsync_sends_request_then_upserts_returned_items_and_reloads_specials()
    {
        var apiItem = CreateApiItem("SKU-001", "930001", 6.5m);
        var repository = new RecordingRepository();
        var api = new PriceApiClient
        {
            Response = new CatalogSpecialProductPriceResponse("S001", "SKU-001", 6.5m, DateTimeOffset.UtcNow, [apiItem])
        };
        var service = new SpecialProductService(repository, api);

        var result = await service.UpdateSpecialProductPriceAsync("S001", "SKU-001", 6.5m);

        var request = Assert.IsType<CatalogSpecialProductPriceRequest>(api.LastRequest);
        Assert.Equal(("S001", "SKU-001", 6.5m), (request.StoreCode, request.ProductCode, request.RetailPrice));
        var upserted = Assert.Single(repository.UpsertedBatches);
        Assert.Equal(6.5m, Assert.Single(upserted).RetailPrice);
        Assert.Equal(1, repository.LoadSpecialProductCallCount);
        Assert.Equal(6.5m, Assert.Single(result.ChangedItems).RetailPrice);
        Assert.Equal(6.5m, Assert.Single(result.SpecialItems).RetailPrice);
    }

    [Fact]
    public async Task UpdateSpecialProductPriceAsync_leaves_local_catalog_untouched_when_the_server_call_fails()
    {
        var repository = new RecordingRepository();
        var api = new PriceApiClient { Failure = new HttpRequestException("offline") };
        var service = new SpecialProductService(repository, api);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.UpdateSpecialProductPriceAsync("S001", "SKU-001", 6.5m));

        Assert.Equal("offline", error.Message);
        Assert.Empty(repository.UpsertedBatches);
        Assert.Equal(0, repository.LoadSpecialProductCallCount);
    }

    [Theory]
    [InlineData("", "SKU-001")]
    [InlineData("S001", " ")]
    public async Task UpdateSpecialProductPriceAsync_rejects_blank_codes_without_calling_the_server(string storeCode, string productCode)
    {
        var api = new PriceApiClient();
        var service = new SpecialProductService(new RecordingRepository(), api);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            service.UpdateSpecialProductPriceAsync(storeCode, productCode, 6.5m));

        Assert.Null(api.LastRequest);
    }

    private static CatalogLookupItemDto CreateApiItem(string productCode, string lookupCode, decimal price) => new(
        "S001",
        productCode,
        ReferenceCode: null,
        "Alpha",
        lookupCode,
        lookupCode,
        ItemNumber: productCode,
        Barcode: lookupCode,
        price,
        PriceSourceKind.StoreRetailPrice,
        "store-retail",
        QuantityFactor: 1m,
        UpdatedAt: DateTimeOffset.UtcNow,
        RowVersion: null,
        IsSpecialProduct: true);

    private sealed class PriceApiClient : ICatalogApiClient
    {
        public CatalogSpecialProductPriceRequest? LastRequest { get; private set; }

        public CatalogSpecialProductPriceResponse? Response { get; init; }

        public Exception? Failure { get; init; }

        public Task<CatalogSpecialProductPriceResponse> UpdateSpecialProductPriceAsync(
            CatalogSpecialProductPriceRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            if (Failure is not null)
            {
                return Task.FromException<CatalogSpecialProductPriceResponse>(Failure);
            }

            return Task.FromResult(Response!);
        }

        public Task<CatalogSyncPageResponse> GetSellableItemsPageAsync(string storeCode, string? cursor, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CatalogCompareResponse> CompareSellableItemsAsync(CatalogCompareRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CatalogPromotionsResponse> GetPromotionRulesAsync(string storeCode, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CatalogSpecialProductsPageResponse> GetSpecialProductsPageAsync(string storeCode, string? cursor, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CatalogCodeConflictsResponse> GetCodeConflictsAsync(string storeCode, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CatalogLookupResponse?> LookupSellableItemAsync(string storeCode, string lookupCode, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CatalogSpecialProductMarkResponse> MarkSpecialProductAsync(CatalogSpecialProductMarkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingRepository : ILocalCatalogRepository
    {
        private readonly List<SellableItemDto> _stored = [];

        public List<IReadOnlyList<SellableItemDto>> UpsertedBatches { get; } = [];

        public int LoadSpecialProductCallCount { get; private set; }

        public Task UpsertSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default)
        {
            var batch = items.ToArray();
            UpsertedBatches.Add(batch);
            _stored.AddRange(batch);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SellableItemDto>> LoadSpecialProductItemsAsync(string storeCode, CancellationToken cancellationToken = default)
        {
            LoadSpecialProductCallCount++;
            return Task.FromResult<IReadOnlyList<SellableItemDto>>(_stored.Where(item => item.IsSpecialProduct).ToArray());
        }

        public Task ReplaceSellableItemsAsync(IEnumerable<SellableItemDto> items, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> DeleteByLookupCodesAsync(string storeCode, IEnumerable<string> lookupCodes, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SellableItemDto?> FindByLookupCodeAsync(string storeCode, string lookupCode, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SaveSpecialProductOrderAsync(string storeCode, IEnumerable<string> productCodes, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> UpdateSpecialProductFlagAsync(string storeCode, string productCode, bool isSpecialProduct, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> ClearSpecialProductFlagsExceptAsync(string storeCode, IEnumerable<string> productCodesToKeep, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<LocalSellableItemCompareRow>> LoadSellableItemComparePageAsync(string storeCode, string? afterLookupCodeNormalized, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<SellableItemDto>> LoadSellableItemsAsync(string storeCode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
