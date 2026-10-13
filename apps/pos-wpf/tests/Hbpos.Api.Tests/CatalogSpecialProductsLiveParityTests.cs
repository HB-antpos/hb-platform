using Hbpos.Api.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Api.Tests;

/// <summary>
/// 特殊商品“实时查询”与“目录工件”算出的特殊商品列表逐项对照（条目、字段、排序、游标、TotalCount、HasMore）。
/// 这些用例是一致性/回归类：同一份数据下新旧实现都应通过，用来证明改成实时读库后结果没有变。
/// </summary>
public sealed class CatalogSpecialProductsLiveParityTests : IAsyncLifetime
{
    private readonly SpecialProductsSqliteDatabase database = new();
    private CatalogService service = null!;

    public async Task InitializeAsync()
    {
        await SpecialProductsLiveTestData.SeedRichCatalogAsync(database.Client);
        service = database.CreateService();
    }

    public async Task DisposeAsync() => await database.DisposeAsync();

    public static TheoryData<string, int> StoresAndPageSizes()
    {
        var data = new TheoryData<string, int>();
        foreach (var store in new[] { "S01", "S02" })
        {
            foreach (var pageSize in new[] { 1, 2, 3, 5, 500, 5000 })
            {
                data.Add(store, pageSize);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(StoresAndPageSizes))]
    public async Task Live_pages_match_artifact_pages_for_every_store_and_page_size(string storeCode, int pageSize)
    {
        var pages = await CatalogSpecialProductsLiveAssertions.AssertLivePagesMatchArtifactAsync(
            service,
            storeCode,
            pageSize);

        Assert.NotEmpty(pages);
        Assert.False(pages[^1].HasMore);
        Assert.Null(pages[^1].NextCursor);
    }

    [Fact]
    public async Task Rich_catalog_resolves_the_expected_special_products()
    {
        var s01 = await GetAllAsync("S01");

        // 手工推导的结果：同时证明造数确实走到了各条同码决胜/过滤路径，而不是空对照。
        Assert.Equal(
            new[] { "SP01", "SP02", "SP03", "SP07", "SP08", "SP11", "SP12", "SP14", "SP17", "SP18", "SP19", "SP21", "SP22B", "SP23", "SP25" },
            s01.Select(x => x.ProductCode).Order(StringComparer.Ordinal).ToArray());
        Assert.All(s01, item => Assert.True(item.IsSpecialProduct));
        Assert.All(s01, item => Assert.Equal("S01", item.StoreCode));

        // SP01：门店价 8.5，折扣率 90 归一为 0.9，来源为分店零售价。
        var sp01 = s01.Single(x => x.ProductCode == "SP01");
        Assert.Equal(8.5m, sp01.RetailPrice);
        Assert.Equal(0.9m, sp01.DiscountRate);
        Assert.Equal(PriceSourceKind.StoreRetailPrice, sp01.PriceSource);
        Assert.Equal("sp01.png", sp01.ProductImage);
        // SP02：门店价为空 -> 回退商品主档价，来源 ProductBase，仍是特殊商品；条码被 OT08 的分店零售价压过，查询码是货号。
        var sp02 = s01.Single(x => x.ProductCode == "SP02");
        Assert.Equal(20m, sp02.RetailPrice);
        Assert.Equal(PriceSourceKind.ProductBase, sp02.PriceSource);
        Assert.Equal("SP02-ITEM", sp02.LookupCodeNormalized);
        // SP03：停用商品仍包含。
        Assert.Equal(25m, s01.Single(x => x.ProductCode == "SP03").RetailPrice);
        // SP07：以最新一行门店价为准。
        Assert.Equal(6m, s01.Single(x => x.ProductCode == "SP07").RetailPrice);
        // SP08：条码被清货码压过，列表里该商品的查询码是货号。
        Assert.Equal("SP08-ITEM", s01.Single(x => x.ProductCode == "SP08").LookupCodeNormalized);
        // SP11：条码被他品压过，列表里是货号行。
        Assert.Equal("SP11-ITEM", s01.Single(x => x.ProductCode == "SP11").LookupCodeNormalized);
        // SP22：两个同码特殊商品只保留更新更晚的 B。
        Assert.DoesNotContain(s01, x => x.ProductCode == "SP22A");
        // SP23：货号被套装码+本店多码压过，条码行仍在。
        Assert.Equal("6900000000023", s01.Single(x => x.ProductCode == "SP23").LookupCodeNormalized);
        // SP25：条码含单引号，被清货码压过，列表里是货号行。
        Assert.Equal("Q25-ITEM", s01.Single(x => x.ProductCode == "SP25").LookupCodeNormalized);

        var s02 = await GetAllAsync("S02");
        Assert.Equal(
            new[] { "SP01", "SP20", "SP24" },
            s02.Select(x => x.ProductCode).Order(StringComparer.Ordinal).ToArray());
        var s02Sp01 = s02.Single(x => x.ProductCode == "SP01");
        Assert.Equal(7.7m, s02Sp01.RetailPrice);
        Assert.Equal(0.85m, s02Sp01.DiscountRate);
    }

    [Fact]
    public async Task Walking_cursors_returns_every_product_once_in_lookup_code_order()
    {
        var all = await GetAllAsync("S01");
        var codes = new List<string>();
        var totalCounts = new HashSet<int>();
        string? cursor = null;
        while (true)
        {
            var page = await service.GetSpecialProductsPageAsync("S01", cursor, 2, CancellationToken.None);
            Assert.NotNull(page);
            Assert.True(page!.Items.Count <= 2);
            codes.AddRange(page.Items.Select(x => x.ProductCode));
            totalCounts.Add(page.TotalCount);
            if (!page.HasMore)
            {
                Assert.Null(page.NextCursor);
                break;
            }

            // 续页游标就是本页末项的 LookupCodeNormalized，且严格递增。
            Assert.Equal(page.Items[^1].LookupCodeNormalized, page.NextCursor);
            cursor = page.NextCursor;
        }

        Assert.Equal(all.Select(x => x.ProductCode), codes);
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(new[] { all.Count }, totalCounts.ToArray());
        Assert.Equal(
            all.Select(x => x.LookupCodeNormalized).Order(StringComparer.Ordinal),
            all.Select(x => x.LookupCodeNormalized));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("6900000000008")]
    [InlineData("6900000000008 ")]
    [InlineData(" 6900000000008")]
    [InlineData("sp08-item")]
    [InlineData("SP08-ITEM")]
    [InlineData("6900000000999")]
    [InlineData("ZZZZZZZZ")]
    [InlineData("~~~~")]
    [InlineData("非法游标")]
    public async Task Cursor_variants_return_the_same_page_as_the_artifact(string? cursor)
    {
        var artifact = await service.BuildCatalogArtifactAsync("S01", CancellationToken.None);
        var expected = artifact!.CatalogIndex.GetSpecialProductsPage(cursor, 3);

        var actual = await service.GetSpecialProductsPageAsync("S01", cursor, 3, CancellationToken.None);

        Assert.NotNull(actual);
        CatalogSpecialProductsLiveAssertions.AssertSamePage(expected, actual!);
    }

    [Fact]
    public async Task Cursor_beyond_last_item_returns_empty_final_page()
    {
        var page = await service.GetSpecialProductsPageAsync("S01", "ZZZZZZZZ", 10, CancellationToken.None);

        Assert.NotNull(page);
        Assert.Empty(page!.Items);
        Assert.False(page.HasMore);
        Assert.Null(page.NextCursor);
        Assert.Equal("ZZZZZZZZ", page.Cursor);
        Assert.Equal(15, page.TotalCount);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4999)]
    [InlineData(5000)]
    [InlineData(5001)]
    [InlineData(int.MaxValue)]
    public async Task Page_size_boundaries_clamp_like_the_artifact(int pageSize)
    {
        // 控制器会先拒绝 1..5000 之外的值；服务层仍按目录工件的口径夹取，保持一致。
        var artifact = await service.BuildCatalogArtifactAsync("S01", CancellationToken.None);
        var expected = artifact!.CatalogIndex.GetSpecialProductsPage(cursor: null, pageSize);

        var actual = await service.GetSpecialProductsPageAsync("S01", null, pageSize, CancellationToken.None);

        Assert.NotNull(actual);
        CatalogSpecialProductsLiveAssertions.AssertSamePage(expected, actual!);
    }

    [Theory]
    [InlineData("S99")]
    [InlineData("S03")]
    [InlineData("S04")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Missing_inactive_or_deleted_store_returns_null_like_the_artifact(string storeCode)
    {
        var artifact = await service.BuildCatalogArtifactAsync(storeCode, CancellationToken.None);
        Assert.Null(artifact);

        var actual = await service.GetSpecialProductsPageAsync(storeCode, null, 500, CancellationToken.None);

        Assert.Null(actual);
    }

    [Fact]
    public async Task Store_code_is_trimmed_before_lookup()
    {
        var expected = await service.GetSpecialProductsPageAsync("S01", null, 500, CancellationToken.None);

        var actual = await service.GetSpecialProductsPageAsync("  S01  ", null, 500, CancellationToken.None);

        Assert.NotNull(actual);
        CatalogSpecialProductsLiveAssertions.AssertSamePage(expected!, actual!);
    }

    [Fact]
    public async Task Store_without_special_products_returns_empty_page()
    {
        await SpecialProductsLiveTestData.AddStoreAsync(database.Client, "S05");
        await SpecialProductsLiveTestData.AddStorePriceAsync(database.Client, "S05", "SP01", 3m, 10, special: false);

        var actual = await service.GetSpecialProductsPageAsync("S05", null, 500, CancellationToken.None);

        Assert.NotNull(actual);
        Assert.Equal("S05", actual!.StoreCode);
        Assert.Null(actual.Cursor);
        Assert.Empty(actual.Items);
        Assert.Null(actual.NextCursor);
        Assert.False(actual.HasMore);
        Assert.Equal(0, actual.TotalCount);
        await CatalogSpecialProductsLiveAssertions.AssertLivePagesMatchArtifactAsync(service, "S05", 500);
    }

    [Fact]
    public async Task Concurrent_requests_all_match_the_artifact()
    {
        var artifact = await service.BuildCatalogArtifactAsync("S01", CancellationToken.None);
        var expected = artifact!.CatalogIndex.GetSpecialProductsPage(cursor: null, pageSize: 5000);

        // 每个请求各自持有自己的上下文（与生产每请求一个 scope 一致），超过进程内并发上限时排队而不是失败。
        var tasks = Enumerable.Range(0, 24).Select(_ =>
            database.CreateService(context: database.CreateExtraDbContext())
                .GetSpecialProductsPageAsync("S01", null, 5000, CancellationToken.None));
        var results = await Task.WhenAll(tasks);

        Assert.All(results, actual =>
        {
            Assert.NotNull(actual);
            CatalogSpecialProductsLiveAssertions.AssertSamePage(expected, actual!);
        });
    }

    [Fact]
    public async Task Large_special_set_spanning_several_query_chunks_matches_the_artifact()
    {
        await using var bulk = new SpecialProductsSqliteDatabase();
        await SpecialProductsLiveTestData.AddStoreAsync(bulk.Client, "S01");
        const int count = 1_300;
        await bulk.Client.Ado.BeginTranAsync();
        for (var i = 0; i < count; i++)
        {
            await SpecialProductsLiveTestData.AddProductAsync(
                bulk.Client, $"BULK{i:D4}", $"BULKBAR-{i:D4}", $"BULKITEM-{i:D4}", 10m + i, 1);
            await SpecialProductsLiveTestData.AddStorePriceAsync(
                bulk.Client, "S01", $"BULK{i:D4}", 5m + i, 10, special: i % 9 != 0);
        }

        for (var i = 0; i < count; i += 50)
        {
            // 每 50 个商品，条码被清货码压过一次，货号被他品压过一次，分布在不同的查询分块里。
            await SpecialProductsLiveTestData.AddClearanceAsync(
                bulk.Client, "S01", "OTHER", $"BULKBAR-{i:D4}", 1m, 12);
            await SpecialProductsLiveTestData.AddProductAsync(
                bulk.Client, $"RIVAL{i:D4}", $"BULKITEM-{i + 1:D4}", null, 1m, 1);
            await SpecialProductsLiveTestData.AddStorePriceAsync(
                bulk.Client, "S01", $"RIVAL{i:D4}", 2m, 30, special: false);
        }

        await bulk.Client.Ado.CommitTranAsync();
        var bulkService = bulk.CreateService();

        var pages = await CatalogSpecialProductsLiveAssertions.AssertLivePagesMatchArtifactAsync(
            bulkService,
            "S01",
            700);

        Assert.True(pages.Count >= 2);
        Assert.True(pages[0].TotalCount > 1_000);
    }

    private async Task<IReadOnlyList<CatalogLookupItemDto>> GetAllAsync(string storeCode)
    {
        var page = await service.GetSpecialProductsPageAsync(storeCode, null, 5000, CancellationToken.None);
        Assert.NotNull(page);
        Assert.False(page!.HasMore);
        return page.Items;
    }
}

internal static class CatalogSpecialProductsLiveAssertions
{
    /// <summary>
    /// 两边从同一个游标起逐页对照：实时查询每一页都必须与目录工件算出的页完全相同
    /// （GeneratedAt 是各自的生成时间，不参与比较）。返回实时查询的全部页。
    /// </summary>
    public static async Task<IReadOnlyList<CatalogSpecialProductsPageResponse>> AssertLivePagesMatchArtifactAsync(
        CatalogService service,
        string storeCode,
        int pageSize)
    {
        var artifact = await service.BuildCatalogArtifactAsync(storeCode, CancellationToken.None);
        Assert.NotNull(artifact);

        var pages = new List<CatalogSpecialProductsPageResponse>();
        string? cursor = null;
        for (var guard = 0; guard < 10_000; guard++)
        {
            var expected = artifact!.CatalogIndex.GetSpecialProductsPage(cursor, pageSize);
            var actual = await service.GetSpecialProductsPageAsync(storeCode, cursor, pageSize, CancellationToken.None);
            Assert.NotNull(actual);
            AssertSamePage(expected, actual!);
            pages.Add(actual!);
            if (!actual!.HasMore)
            {
                return pages;
            }

            cursor = actual.NextCursor;
        }

        throw new InvalidOperationException("分页没有在预期页数内结束。");
    }

    public static void AssertSamePage(CatalogSpecialProductsPageResponse expected, CatalogSpecialProductsPageResponse actual)
    {
        Assert.Equal(expected.StoreCode, actual.StoreCode);
        Assert.Equal(expected.Cursor, actual.Cursor);
        // CatalogLookupItemDto 是 record：逐字段值相等，含 RowVersion（价格、折扣率、特殊标记等字段的摘要）。
        Assert.Equal(expected.Items, actual.Items);
        Assert.Equal(expected.NextCursor, actual.NextCursor);
        Assert.Equal(expected.HasMore, actual.HasMore);
        Assert.Equal(expected.TotalCount, actual.TotalCount);
    }
}
