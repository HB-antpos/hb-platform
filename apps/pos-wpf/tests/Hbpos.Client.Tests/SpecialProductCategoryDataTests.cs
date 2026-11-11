using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Common;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

/// <summary>
/// 分店级特殊商品分类的数据层：API 客户端三个调用、本机两张表的覆盖式读写、按分类取商品、
/// 业务服务（SpecialProductService / SpecialProductsWorkflowService）对接口的编排。
/// 服务端接口可能尚未就绪，这里全部用假 HTTP 处理器 / 假客户端，不依赖真实服务端。
/// </summary>
public sealed class SpecialProductCategoryDataTests
{
    private const string Store = "1042";
    private static readonly DateTimeOffset Timestamp = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    // ================= CatalogApiClient =================

    [Fact]
    public async Task Api_client_get_categories_requests_store_and_reads_snapshot()
    {
        var requests = new List<(HttpMethod Method, string PathAndQuery)>();
        var client = CreateClient(request =>
        {
            requests.Add((request.Method, request.RequestUri!.PathAndQuery));
            return Json(ApiResult<CatalogSpecialProductCategoriesResponse>.Ok(Response(
                [new SpecialProductCategoryDto("C1", "气球", "#2563EB", 0)],
                [new SpecialProductCategoryAssignmentDto("P-1", "C1", 0)])));
        });

        var response = await client.GetSpecialProductCategoriesAsync(Store);

        var request = Assert.Single(requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/v1/catalog/special-products/categories?storeCode=1042", request.PathAndQuery);
        Assert.Equal("气球", Assert.Single(response.Categories).Name);
        Assert.Equal("P-1", Assert.Single(response.Assignments).ProductCode);
    }

    [Fact]
    public async Task Api_client_assign_posts_camel_case_body_and_null_category_means_uncategorized()
    {
        var bodies = new List<string>();
        var paths = new List<string>();
        var client = CreateClient(request =>
        {
            paths.Add(request.RequestUri!.PathAndQuery);
            bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return Json(ApiResult<CatalogSpecialProductCategoriesResponse>.Ok(Response([], [])));
        });

        await client.AssignSpecialProductCategoryAsync(new CatalogSpecialProductCategoryAssignRequest(Store, "P-1", "C1"));
        await client.AssignSpecialProductCategoryAsync(new CatalogSpecialProductCategoryAssignRequest(Store, "P-1", null));

        Assert.All(paths, path => Assert.Equal("/api/v1/catalog/special-products/categories/assign", path));
        using (var first = JsonDocument.Parse(bodies[0]))
        {
            Assert.Equal(Store, first.RootElement.GetProperty("storeCode").GetString());
            Assert.Equal("P-1", first.RootElement.GetProperty("productCode").GetString());
            Assert.Equal("C1", first.RootElement.GetProperty("categoryId").GetString());
        }

        using var second = JsonDocument.Parse(bodies[1]);
        Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("categoryId").ValueKind);
    }

    [Fact]
    public async Task Api_client_order_posts_full_permutation()
    {
        string? body = null;
        string? path = null;
        var client = CreateClient(request =>
        {
            path = request.RequestUri!.PathAndQuery;
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(ApiResult<CatalogSpecialProductCategoriesResponse>.Ok(Response([], [])));
        });

        await client.OrderSpecialProductCategoryAsync(
            new CatalogSpecialProductCategoryOrderRequest(Store, "C1", ["P-3", "P-1", "P-2"]));

        Assert.Equal("/api/v1/catalog/special-products/categories/order", path);
        using var document = JsonDocument.Parse(body!);
        Assert.Equal("C1", document.RootElement.GetProperty("categoryId").GetString());
        Assert.Equal(
            ["P-3", "P-1", "P-2"],
            document.RootElement.GetProperty("productCodes").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task Api_client_order_conflict_surfaces_stable_error_code()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(
                    ApiResult<CatalogSpecialProductCategoriesResponse>.Fail("SPECIAL_PRODUCT_CATEGORY_ORDER_CONFLICT", "conflict"),
                    WebJson),
                Encoding.UTF8,
                "application/json"),
        });

        var exception = await Assert.ThrowsAsync<CatalogApiException>(() => client.OrderSpecialProductCategoryAsync(
            new CatalogSpecialProductCategoryOrderRequest(Store, "C1", ["P-1"])));

        Assert.Equal("SPECIAL_PRODUCT_CATEGORY_ORDER_CONFLICT", exception.ErrorCode);
        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
    }

    [Fact]
    public async Task Api_client_interface_defaults_report_old_servers_as_unsupported()
    {
        // 假客户端不实现分类接口时（等同于老服务端 / 旧测试替身），默认实现必须返回「不支持」而不是别的异常，
        // 这样页面能把它当作「没有分类功能」安静处理。
        ICatalogApiClient legacy = new LegacyCatalogApiClient();

        await Assert.ThrowsAsync<CatalogApiException>(() => legacy.GetSpecialProductCategoriesAsync(Store));
        await Assert.ThrowsAsync<CatalogApiException>(() => legacy.AssignSpecialProductCategoryAsync(
            new CatalogSpecialProductCategoryAssignRequest(Store, "P-1", null)));
        await Assert.ThrowsAsync<CatalogApiException>(() => legacy.OrderSpecialProductCategoryAsync(
            new CatalogSpecialProductCategoryOrderRequest(Store, "C1", [])));
    }

    // ================= 本机表：覆盖式读写 =================

    [Fact]
    public async Task Snapshot_round_trips_and_is_sorted_by_server_order()
    {
        var databasePath = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.ReplaceSpecialProductCategorySnapshotAsync(new SpecialProductCategorySnapshot(
                Store,
                [
                    new SpecialProductCategoryDto("C2", "贺卡", null, 1),
                    new SpecialProductCategoryDto("C1", "气球", "#2563EB", 0),
                ],
                [
                    new SpecialProductCategoryAssignmentDto("P-2", "C1", 1),
                    new SpecialProductCategoryAssignmentDto("P-1", "C1", 0),
                    new SpecialProductCategoryAssignmentDto("P-3", "C2", 0),
                ]));

            var loaded = await repository.LoadSpecialProductCategorySnapshotAsync(Store);

            Assert.Equal(["C1", "C2"], loaded.Categories.Select(category => category.CategoryId));
            Assert.Equal("#2563EB", loaded.Categories[0].Color);
            Assert.Null(loaded.Categories[1].Color);
            Assert.Equal(["P-1", "P-2"], loaded.GetOrderedProductCodes("C1"));
            Assert.Equal(["P-3"], loaded.GetOrderedProductCodes("C2"));
            Assert.True(loaded.HasCategories);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Categories_with_equal_sort_order_are_ordered_by_category_id_like_the_server()
    {
        // 服务端并列排序：SortOrder 升序、再 CategoryId 升序，与名称无关（这里名称顺序故意相反）。
        var databasePath = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            var snapshot = new SpecialProductCategorySnapshot(
                Store,
                [
                    new SpecialProductCategoryDto("bbbb0000", "A 名称最靠前", null, 1),
                    new SpecialProductCategoryDto("cccc0000", "C", null, 0),
                    new SpecialProductCategoryDto("aaaa0000", "Z 名称最靠后", null, 1),
                ],
                []);

            Assert.Equal(["cccc0000", "aaaa0000", "bbbb0000"], snapshot.Normalize().Categories.Select(category => category.CategoryId));
            await repository.ReplaceSpecialProductCategorySnapshotAsync(snapshot);
            var loaded = await repository.LoadSpecialProductCategorySnapshotAsync(Store);
            Assert.Equal(["cccc0000", "aaaa0000", "bbbb0000"], loaded.Categories.Select(category => category.CategoryId));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Replace_is_overwrite_per_store_and_leaves_other_stores_alone()
    {
        var databasePath = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.ReplaceSpecialProductCategorySnapshotAsync(new SpecialProductCategorySnapshot(
                Store,
                [new SpecialProductCategoryDto("C1", "旧分类", null, 0)],
                [new SpecialProductCategoryAssignmentDto("P-1", "C1", 0)]));
            await repository.ReplaceSpecialProductCategorySnapshotAsync(new SpecialProductCategorySnapshot(
                "2001",
                [new SpecialProductCategoryDto("X1", "别店分类", null, 0)],
                [new SpecialProductCategoryAssignmentDto("P-9", "X1", 0)]));

            // 本店覆盖成另一套：旧分类与旧归属必须整体消失，不能残留。
            await repository.ReplaceSpecialProductCategorySnapshotAsync(new SpecialProductCategorySnapshot(
                Store,
                [new SpecialProductCategoryDto("C9", "新分类", null, 0)],
                [new SpecialProductCategoryAssignmentDto("P-2", "C9", 0)]));

            var mine = await repository.LoadSpecialProductCategorySnapshotAsync(Store);
            Assert.Equal(["C9"], mine.Categories.Select(category => category.CategoryId));
            Assert.Equal(["P-2"], mine.Assignments.Select(assignment => assignment.ProductCode));
            var other = await repository.LoadSpecialProductCategorySnapshotAsync("2001");
            Assert.Equal(["X1"], other.Categories.Select(category => category.CategoryId));
            Assert.Equal(["P-9"], other.Assignments.Select(assignment => assignment.ProductCode));

            // 空快照（服务端把分类全删了）= 清空本店，功能随之关闭。
            await repository.ReplaceSpecialProductCategorySnapshotAsync(SpecialProductCategorySnapshot.Empty(Store));
            var emptied = await repository.LoadSpecialProductCategorySnapshotAsync(Store);
            Assert.False(emptied.HasCategories);
            Assert.Empty(emptied.Assignments);
            Assert.True((await repository.LoadSpecialProductCategorySnapshotAsync("2001")).HasCategories);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Replace_drops_assignments_to_unknown_categories_and_duplicate_products()
    {
        var databasePath = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.ReplaceSpecialProductCategorySnapshotAsync(new SpecialProductCategorySnapshot(
                Store,
                [
                    new SpecialProductCategoryDto("C1", "气球", null, 0),
                    new SpecialProductCategoryDto("C1", "重复分类", null, 5),
                ],
                [
                    new SpecialProductCategoryAssignmentDto("P-1", "C1", 0),
                    new SpecialProductCategoryAssignmentDto("P-1", "C1", 7),
                    new SpecialProductCategoryAssignmentDto("P-2", "GONE", 0),
                ]));

            var loaded = await repository.LoadSpecialProductCategorySnapshotAsync(Store);

            Assert.Equal("气球", Assert.Single(loaded.Categories).Name);
            var assignment = Assert.Single(loaded.Assignments);
            Assert.Equal(("P-1", "C1", 0), (assignment.ProductCode, assignment.CategoryId, assignment.SortOrder));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Load_snapshot_for_unknown_store_is_empty()
    {
        var databasePath = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(databasePath);

            var loaded = await repository.LoadSpecialProductCategorySnapshotAsync("NOPE");

            Assert.False(loaded.HasCategories);
            Assert.Empty(loaded.Assignments);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    // ================= 按分类取商品 =================

    [Fact]
    public async Task Items_by_category_use_server_sort_order_then_product_code_and_require_special_flag()
    {
        var databasePath = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.UpsertSellableItemsAsync(
            [
                Item("P-A", "A-1", "Alpha", special: true),
                Item("P-B", "B-1", "Bravo", special: true),
                Item("P-C", "C-1", "Charlie", special: true),
                Item("P-D", "D-1", "Delta", special: true),
                // 已取消特殊标记：归属行还在（取消标记不删归属），但读取必须与特殊标记求交集。
                Item("P-E", "E-1", "Echo", special: false),
                // 同一商品的第二个查码行：必须折成一个商品。
                Item("P-A", "A-2", "Alpha", special: true),
            ]);
            await repository.ReplaceSpecialProductCategorySnapshotAsync(new SpecialProductCategorySnapshot(
                Store,
                [new SpecialProductCategoryDto("C1", "气球", null, 0), new SpecialProductCategoryDto("C2", "贺卡", null, 1)],
                [
                    new SpecialProductCategoryAssignmentDto("P-A", "C1", 2),
                    new SpecialProductCategoryAssignmentDto("P-B", "C1", 0),
                    // 与 P-B 同为 SortOrder 0：按商品编码稳定排序，P-B 在前。
                    new SpecialProductCategoryAssignmentDto("P-C", "C1", 0),
                    new SpecialProductCategoryAssignmentDto("P-E", "C1", 1),
                    new SpecialProductCategoryAssignmentDto("P-D", "C2", 0),
                ]));

            var balloons = await repository.LoadSpecialProductItemsByCategoryAsync(Store, "C1");

            Assert.Equal(["P-B", "P-C", "P-A"], balloons.Select(item => item.ProductCode));
            Assert.Equal(["P-D"], (await repository.LoadSpecialProductItemsByCategoryAsync(Store, "C2")).Select(item => item.ProductCode));
            Assert.Empty(await repository.LoadSpecialProductItemsByCategoryAsync(Store, "NOPE"));
            Assert.Empty(await repository.LoadSpecialProductItemsByCategoryAsync("OTHER", "C1"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Items_by_category_uncategorized_keeps_the_existing_local_order()
    {
        var databasePath = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.UpsertSellableItemsAsync(
            [
                Item("P-A", "A-1", "Alpha", special: true),
                Item("P-B", "B-1", "Bravo", special: true),
                Item("P-C", "C-1", "Charlie", special: true),
                Item("P-D", "D-1", "Delta", special: true),
            ]);
            // 本机顺序：C 最前，其次 A；B、D 没有本机顺序，排在后面按名称。
            await repository.SaveSpecialProductOrderAsync(Store, ["P-C", "P-A"]);
            await repository.ReplaceSpecialProductCategorySnapshotAsync(new SpecialProductCategorySnapshot(
                Store,
                [new SpecialProductCategoryDto("C1", "气球", null, 0)],
                [new SpecialProductCategoryAssignmentDto("P-B", "C1", 0)]));

            var uncategorized = await repository.LoadSpecialProductItemsByCategoryAsync(
                Store,
                SpecialProductCategorySnapshot.UncategorizedId);
            var allSpecial = await repository.LoadSpecialProductItemsAsync(Store);

            Assert.Equal(["P-C", "P-A", "P-D"], uncategorized.Select(item => item.ProductCode));
            // 与「全部」里去掉已归类商品后的顺序完全一致。
            Assert.Equal(
                allSpecial.Where(item => item.ProductCode != "P-B").Select(item => item.ProductCode),
                uncategorized.Select(item => item.ProductCode));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    // ================= SpecialProductService =================

    [Fact]
    public async Task Service_refresh_overwrites_local_snapshot_with_server_response()
    {
        var databasePath = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            var api = new RecordingCategoryApi
            {
                Response = Response(
                    [new SpecialProductCategoryDto("C1", "气球", "#112233", 0)],
                    [new SpecialProductCategoryAssignmentDto("P-1", "C1", 0)]),
            };
            var service = new SpecialProductService(repository, api);

            var snapshot = await service.RefreshCategoriesAsync(Store);

            Assert.Equal(Store, api.LastGetStore);
            Assert.Equal("C1", Assert.Single(snapshot.Categories).CategoryId);
            var local = await repository.LoadSpecialProductCategorySnapshotAsync(Store);
            Assert.Equal("气球", Assert.Single(local.Categories).Name);
            Assert.Equal("P-1", Assert.Single(local.Assignments).ProductCode);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Service_failure_keeps_existing_local_snapshot()
    {
        var databasePath = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.ReplaceSpecialProductCategorySnapshotAsync(new SpecialProductCategorySnapshot(
                Store,
                [new SpecialProductCategoryDto("C1", "旧", null, 0)],
                []));
            var api = new RecordingCategoryApi { Failure = new CatalogApiException("boom", HttpStatusCode.ServiceUnavailable) };
            var service = new SpecialProductService(repository, api);

            await Assert.ThrowsAsync<CatalogApiException>(() => service.RefreshCategoriesAsync(Store));
            await Assert.ThrowsAsync<CatalogApiException>(() => service.AssignProductToCategoryAsync(Store, "P-1", "C1"));
            await Assert.ThrowsAsync<CatalogApiException>(() => service.ReorderCategoryProductsAsync(Store, "C1", ["P-1"]));

            // 关键约束：写操作失败时本机快照保持不变（以服务端为准，不做乐观写入）。
            var local = await repository.LoadSpecialProductCategorySnapshotAsync(Store);
            Assert.Equal("旧", Assert.Single(local.Categories).Name);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Service_assign_and_order_send_requests_and_store_returned_snapshot()
    {
        var databasePath = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            var api = new RecordingCategoryApi
            {
                Response = Response(
                    [new SpecialProductCategoryDto("C1", "气球", null, 0)],
                    [
                        new SpecialProductCategoryAssignmentDto("P-2", "C1", 0),
                        new SpecialProductCategoryAssignmentDto("P-1", "C1", 1),
                    ]),
            };
            var service = new SpecialProductService(repository, api);

            await service.AssignProductToCategoryAsync(Store, "P-1", "C1");
            var afterOrder = await service.ReorderCategoryProductsAsync(Store, "C1", ["P-2", "P-1"]);

            Assert.Equal(new CatalogSpecialProductCategoryAssignRequest(Store, "P-1", "C1"), api.LastAssign);
            Assert.Equal(Store, api.LastOrder!.StoreCode);
            Assert.Equal("C1", api.LastOrder.CategoryId);
            Assert.Equal(["P-2", "P-1"], api.LastOrder.ProductCodes);
            Assert.Equal(["P-2", "P-1"], afterOrder.GetOrderedProductCodes("C1"));
            Assert.Equal(
                ["P-2", "P-1"],
                (await repository.LoadSpecialProductCategorySnapshotAsync(Store)).GetOrderedProductCodes("C1"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Service_still_returns_server_snapshot_when_local_save_fails()
    {
        // 服务端已写入成功，本机落库失败不能被当成「操作失败」让收银员重试（会造成重复操作）。
        var repository = DispatchProxy.Create<ILocalCatalogRepository, SaveFailingRepositoryProxy>();
        var api = new RecordingCategoryApi
        {
            Response = Response([new SpecialProductCategoryDto("C1", "气球", null, 0)], []),
        };
        var service = new SpecialProductService(repository, api);

        var snapshot = await service.AssignProductToCategoryAsync(Store, "P-1", "C1");

        Assert.Equal("C1", Assert.Single(snapshot.Categories).CategoryId);
    }

    // ================= SpecialProductsWorkflowService =================

    [Fact]
    public async Task Workflow_loads_local_snapshot_and_delegates_server_calls_with_trimmed_codes()
    {
        var databasePath = CreateTempDatabasePath();
        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.ReplaceSpecialProductCategorySnapshotAsync(new SpecialProductCategorySnapshot(
                Store,
                [new SpecialProductCategoryDto("C1", "气球", null, 0)],
                []));
            var api = new RecordingCategoryApi { Response = Response([new SpecialProductCategoryDto("C1", "气球", null, 0)], []) };
            var workflow = new SpecialProductsWorkflowService(
                new LocalSellableItemIndex(),
                new PosCartService(),
                repository,
                new SpecialProductService(repository, api));

            var local = await workflow.LoadCategorySnapshotAsync(" 1042 ");
            await workflow.AssignProductToCategoryAsync(" 1042 ", " P-1 ", "  ");
            await workflow.ReorderCategoryProductsAsync(" 1042 ", " C1 ", [" P-2 ", "P-1"]);
            var refreshed = await workflow.RefreshCategorySnapshotAsync(" 1042 ");

            Assert.Equal("C1", Assert.Single(local.Categories).CategoryId);
            // 空白分类 Id 视为「未分类」（null），商品编码与店号都去掉首尾空白。
            Assert.Equal(new CatalogSpecialProductCategoryAssignRequest(Store, "P-1", null), api.LastAssign);
            Assert.Equal(["P-2", "P-1"], api.LastOrder!.ProductCodes);
            Assert.Equal("C1", api.LastOrder.CategoryId);
            Assert.NotNull(refreshed);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Workflow_remembers_selected_category_per_store_in_app_settings()
    {
        var settings = new InMemorySettings();
        var workflow = new SpecialProductsWorkflowService(
            new LocalSellableItemIndex(),
            new PosCartService(),
            DispatchProxy.Create<ILocalCatalogRepository, SaveFailingRepositoryProxy>(),
            new LegacySpecialProductService(),
            settings);

        Assert.Null(await workflow.LoadSelectedCategoryIdAsync(Store));

        await workflow.SaveSelectedCategoryIdAsync(Store, " C1 ");
        await workflow.SaveSelectedCategoryIdAsync("2001", SpecialProductCategorySnapshot.UncategorizedId);

        Assert.Equal("C1", await workflow.LoadSelectedCategoryIdAsync(Store));
        Assert.Equal(SpecialProductCategorySnapshot.UncategorizedId, await workflow.LoadSelectedCategoryIdAsync("2001"));

        // 「全部」是默认值：删除记录而不是写空串。
        await workflow.SaveSelectedCategoryIdAsync(Store, null);
        Assert.Null(await workflow.LoadSelectedCategoryIdAsync(Store));
        Assert.DoesNotContain(settings.Values.Keys, key => key.EndsWith(":" + Store, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Workflow_without_settings_repository_does_not_remember_selection()
    {
        var workflow = new SpecialProductsWorkflowService(
            new LocalSellableItemIndex(),
            new PosCartService(),
            DispatchProxy.Create<ILocalCatalogRepository, SaveFailingRepositoryProxy>(),
            new LegacySpecialProductService());

        await workflow.SaveSelectedCategoryIdAsync(Store, "C1");

        Assert.Null(await workflow.LoadSelectedCategoryIdAsync(Store));
    }

    [Fact]
    public async Task Workflow_refresh_is_unsupported_for_services_without_category_support()
    {
        // 旧版测试替身不实现分类接口：默认实现抛 NotSupported，由页面当作「拉取失败」安静吞掉。
        var workflow = new SpecialProductsWorkflowService(
            new LocalSellableItemIndex(),
            new PosCartService(),
            DispatchProxy.Create<ILocalCatalogRepository, SaveFailingRepositoryProxy>(),
            new LegacySpecialProductService());

        await Assert.ThrowsAsync<NotSupportedException>(() => workflow.RefreshCategorySnapshotAsync(Store));
    }

    // ================= 辅助 =================

    private static CatalogApiClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return new CatalogApiClient(
            new HttpClient(new StubHttpMessageHandler(handler)) { BaseAddress = new Uri("http://pos.test/") },
            (_, _) => Task.CompletedTask);
    }

    private static CatalogSpecialProductCategoriesResponse Response(
        IReadOnlyList<SpecialProductCategoryDto> categories,
        IReadOnlyList<SpecialProductCategoryAssignmentDto> assignments) =>
        new(Store, Timestamp, "rev-1", categories, assignments);

    private static HttpResponseMessage Json<T>(T value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, WebJson), Encoding.UTF8, "application/json"),
        };
    }

    private static SellableItemDto Item(string productCode, string lookupCode, string name, bool special) => new(
        Store,
        productCode,
        ReferenceCode: null,
        name,
        lookupCode,
        ItemNumber: productCode,
        Barcode: lookupCode,
        RetailPrice: 1.5m,
        PriceSourceKind.StoreRetailPrice,
        "store-retail",
        QuantityFactor: 1m,
        UpdatedAt: Timestamp,
        ProductImage: null,
        DiscountRate: null,
        IsSpecialProduct: special);

    private static async Task<LocalCatalogRepository> CreateRepositoryAsync(string databasePath)
    {
        var store = new LocalSqliteStore(databasePath);
        await new LocalSchemaService(store).InitializeAsync();
        return new LocalCatalogRepository(store);
    }

    private static string CreateTempDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"hbpos-special-category-{Guid.NewGuid():N}.db");

    private static void DeleteTempDatabase(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }

    /// <summary>实现全部必需方法、不覆盖分类接口：用来确认接口默认实现的行为。</summary>
    private class LegacyCatalogApiClient : ICatalogApiClient
    {
        public Task<CatalogSyncPageResponse> GetSellableItemsPageAsync(string storeCode, string? cursor, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CatalogCompareResponse> CompareSellableItemsAsync(CatalogCompareRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CatalogPromotionsResponse> GetPromotionRulesAsync(string storeCode, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CatalogSpecialProductsPageResponse> GetSpecialProductsPageAsync(string storeCode, string? cursor, int pageSize, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CatalogCodeConflictsResponse> GetCodeConflictsAsync(string storeCode, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CatalogLookupResponse?> LookupSellableItemAsync(string storeCode, string lookupCode, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CatalogSpecialProductMarkResponse> MarkSpecialProductAsync(CatalogSpecialProductMarkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingCategoryApi : LegacyCatalogApiClient, ICatalogApiClient
    {
        public CatalogSpecialProductCategoriesResponse Response { get; init; } =
            new(Store, Timestamp, "rev", [], []);

        public Exception? Failure { get; init; }

        public string? LastGetStore { get; private set; }

        public CatalogSpecialProductCategoryAssignRequest? LastAssign { get; private set; }

        public CatalogSpecialProductCategoryOrderRequest? LastOrder { get; private set; }

        Task<CatalogSpecialProductCategoriesResponse> ICatalogApiClient.GetSpecialProductCategoriesAsync(string storeCode, CancellationToken cancellationToken)
        {
            LastGetStore = storeCode;
            return Failure is null ? Task.FromResult(Response) : Task.FromException<CatalogSpecialProductCategoriesResponse>(Failure);
        }

        Task<CatalogSpecialProductCategoriesResponse> ICatalogApiClient.AssignSpecialProductCategoryAsync(CatalogSpecialProductCategoryAssignRequest request, CancellationToken cancellationToken)
        {
            LastAssign = request;
            return Failure is null ? Task.FromResult(Response) : Task.FromException<CatalogSpecialProductCategoriesResponse>(Failure);
        }

        Task<CatalogSpecialProductCategoriesResponse> ICatalogApiClient.OrderSpecialProductCategoryAsync(CatalogSpecialProductCategoryOrderRequest request, CancellationToken cancellationToken)
        {
            LastOrder = request;
            return Failure is null ? Task.FromResult(Response) : Task.FromException<CatalogSpecialProductCategoriesResponse>(Failure);
        }
    }

    /// <summary>不实现分类方法的业务服务替身，用于确认默认实现。</summary>
    private sealed class LegacySpecialProductService : ISpecialProductService
    {
        public Task<SpecialProductMarkResult> MarkSpecialProductAsync(string storeCode, string productCode, bool isSpecialProduct, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SpecialProductDownloadResult> DownloadSpecialProductsAsync(string storeCode, CancellationToken cancellationToken = default, IProgress<SpecialProductDownloadProgress>? progress = null) => throw new NotSupportedException();
    }

    /// <summary>
    /// 仓储代理：覆盖式写分类快照时抛异常，其余成员一律走接口默认实现（读分类返回空快照等）。
    /// 只用来验证「本机落库失败不影响服务端已成功的结果」与默认实现。
    /// </summary>
    public class SaveFailingRepositoryProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ILocalCatalogRepository.ReplaceSpecialProductCategorySnapshotAsync))
            {
                return Task.FromException(new InvalidOperationException("disk full"));
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private sealed class InMemorySettings : ILocalAppSettingsRepository
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task SetValuesAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default)
        {
            foreach (var pair in values)
            {
                Values[pair.Key] = pair.Value;
            }

            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            Values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
