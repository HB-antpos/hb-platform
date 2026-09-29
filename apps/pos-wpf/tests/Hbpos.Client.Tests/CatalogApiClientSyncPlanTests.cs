using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Hbpos.Client.Wpf;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Hbpos.Client.Tests;

public sealed class CatalogApiClientSyncPlanTests
{
    private const string Store = "1042";
    private static readonly DateTimeOffset Timestamp = new(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Sync_plan_request_omits_missing_base_version_and_returns_the_plan()
    {
        var requests = new List<Uri>();
        var client = CreateClient(request =>
        {
            requests.Add(request.RequestUri!);
            return Json(ApiResult<CatalogSyncPlanResponse>.Ok(new CatalogSyncPlanResponse(
                Store, Timestamp, CatalogSyncModes.Full, null, "catalog-v1:b", 3, "lease-1")));
        });

        var plan = await client.GetCatalogSyncPlanAsync(Store, baseCatalogVersion: null);

        Assert.Equal("/api/v1/catalog/sync-plan?storeCode=1042", Assert.Single(requests).PathAndQuery);
        Assert.Equal((CatalogSyncModes.Full, "catalog-v1:b", 3, "lease-1"), (plan.Mode, plan.TargetCatalogVersion, plan.TargetTotal, plan.DownloadLeaseId));
    }

    [Fact]
    public async Task Pinned_page_requests_checksum_v2_with_version_and_lease_and_verifies_the_page()
    {
        var items = new[] { Lookup("P-A", "A", 1m), Lookup("P-B", "B", 2.5m) };
        var requests = new List<Uri>();
        var client = CreateClient(request =>
        {
            requests.Add(request.RequestUri!);
            return Json(ApiResult<CatalogSyncPageResponse>.Ok(Page(items, "catalog-v1:b", "lease-1")));
        });

        var page = await client.GetPinnedSellableItemsPageAsync(Store, "catalog-v1:b", "lease-1", cursor: "0001", pageSize: 5000);

        Assert.Equal(
            "/api/v1/catalog/sellable-items/page?storeCode=1042&cursor=0001&pageSize=5000&catalogVersion=catalog-v1%3Ab&downloadLeaseId=lease-1&checksumVersion=2",
            Assert.Single(requests).PathAndQuery);
        Assert.Equal(["P-A", "P-B"], page.Items.Select(item => item.ProductCode));
    }

    [Theory]
    [InlineData("tampered-price", "CATALOG_PAGE_CHECKSUM_MISMATCH")]
    [InlineData("other-version", "CATALOG_PAGE_VERSION_MISMATCH")]
    [InlineData("other-lease", "CATALOG_PAGE_LEASE_MISMATCH")]
    [InlineData("other-store", "CATALOG_PAGE_STORE_MISMATCH")]
    public async Task Pinned_page_rejects_pages_that_fail_verification(string tamper, string expectedErrorCode)
    {
        var items = new[] { Lookup("P-A", "A", 1m) };
        var page = Page(items, "catalog-v1:b", "lease-1");
        page = tamper switch
        {
            // 摘要按原价计算，下发的价格被改动：必须拒收，不能把错价写进本地目录。
            "tampered-price" => page with { Items = [items[0] with { RetailPrice = 0.01m }] },
            "other-version" => page with { CatalogVersion = "catalog-v1:c" },
            "other-lease" => page with { DownloadLeaseId = "lease-2" },
            "other-store" => page with { StoreCode = "1024" },
            _ => page
        };
        var client = CreateClient(_ => Json(ApiResult<CatalogSyncPageResponse>.Ok(page)));

        var exception = await Assert.ThrowsAsync<CatalogApiException>(() =>
            client.GetPinnedSellableItemsPageAsync(Store, "catalog-v1:b", "lease-1", cursor: null, pageSize: 5000));

        Assert.Equal(expectedErrorCode, exception.ErrorCode);
    }

    [Fact]
    public async Task Delta_page_verifies_checksum_including_deletes()
    {
        var upserts = new[] { Lookup("P-A", "A", 1.5m) };
        var deletes = new[] { new DeletedLookupDto(Store, "b", "B", Timestamp) };
        var delta = new CatalogDeltaPageResponse(
            Store, Timestamp, "catalog-v1:a", "catalog-v1:b", null, upserts, deletes, null, false, 5,
            CatalogPageChecksums.ComputeDeltaPageV1("catalog-v1:a", "catalog-v1:b", upserts, deletes),
            "lease-1");
        var requests = new List<Uri>();
        var responses = new Queue<CatalogDeltaPageResponse>([delta, delta with { DeletedLookups = [] }]);
        var client = CreateClient(request =>
        {
            requests.Add(request.RequestUri!);
            return Json(ApiResult<CatalogDeltaPageResponse>.Ok(responses.Dequeue()));
        });

        var verified = await client.GetCatalogDeltaPageAsync(Store, "catalog-v1:a", "catalog-v1:b", "lease-1", cursor: null, pageSize: 5000);
        // 同一摘要但删除项被丢掉：校验必须失败。
        var exception = await Assert.ThrowsAsync<CatalogApiException>(() =>
            client.GetCatalogDeltaPageAsync(Store, "catalog-v1:a", "catalog-v1:b", "lease-1", cursor: null, pageSize: 5000));

        Assert.Equal(
            "/api/v1/catalog/delta/page?storeCode=1042&baseCatalogVersion=catalog-v1%3Aa&targetCatalogVersion=catalog-v1%3Ab&pageSize=5000&downloadLeaseId=lease-1",
            requests[0].PathAndQuery);
        Assert.Single(verified.DeletedLookups);
        Assert.Equal("CATALOG_PAGE_CHECKSUM_MISMATCH", exception.ErrorCode);
    }

    [Fact]
    public void Catalog_http_client_accepts_gzip_so_the_server_compresses_catalog_pages()
    {
        var services = new ServiceCollection();
        services.AddHbposClientServices(new AppStartupOptions([], PreviewMode: true, InitialScreen: null, InitialCulture: null));
        using var provider = services.BuildServiceProvider();
        var handlerFactory = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        HttpMessageHandler? current = handlerFactory.CreateHandler(nameof(ICatalogApiClient));
        while (current is DelegatingHandler delegatingHandler)
        {
            current = delegatingHandler.InnerHandler;
        }

        var primary = Assert.IsType<SocketsHttpHandler>(current);
        Assert.True(primary.AutomaticDecompression.HasFlag(DecompressionMethods.GZip));
    }

    private static CatalogApiClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return new CatalogApiClient(
            new HttpClient(new StubHttpMessageHandler(handler)) { BaseAddress = new Uri("http://pos.test/") },
            (_, _) => Task.CompletedTask);
    }

    private static CatalogSyncPageResponse Page(CatalogLookupItemDto[] items, string version, string lease)
    {
        return new CatalogSyncPageResponse(
            Store,
            Timestamp,
            null,
            items,
            [],
            null,
            false,
            items.Length,
            version,
            CatalogPageChecksums.ComputeSellablePageV2(items),
            lease);
    }

    private static CatalogLookupItemDto Lookup(string productCode, string lookupCode, decimal price)
    {
        return new CatalogLookupItemDto(
            Store,
            productCode,
            ReferenceCode: null,
            "商品 " + productCode,
            lookupCode,
            lookupCode.ToUpperInvariant(),
            ItemNumber: productCode,
            Barcode: lookupCode,
            RetailPrice: price,
            PriceSourceKind.StoreRetailPrice,
            "store-retail",
            QuantityFactor: 1m,
            UpdatedAt: Timestamp.AddTicks(1234),
            RowVersion: "row");
    }

    private static HttpResponseMessage Json<T>(T value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, WebJson), Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
