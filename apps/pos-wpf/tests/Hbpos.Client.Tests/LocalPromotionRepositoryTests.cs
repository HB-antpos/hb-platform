using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Promotions;
using Microsoft.Data.Sqlite;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Tests;

public sealed class LocalPromotionRepositoryTests
{
    [Fact]
    public async Task Local_schema_service_creates_local_promotion_tables_and_indexes()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);

            await schema.InitializeAsync();

            await using var connection = await store.OpenConnectionAsync();
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'LocalPromotions';"));
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'LocalPromotionProducts';"));
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_LocalPromotions_Store_EffectiveRange';"));
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_LocalPromotionProducts_Store_ProductCode';"));
            // promotion 链路自己的明细表与索引：此前它误用了 catalog 链路的 LocalPromotionProducts，
            // 两条链路各自“按门店全删再全插”，在同一次同步里互相清空。
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'LocalPromotionRuleProducts';"));
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_LocalPromotionRuleProducts_Store_ProductCode';"));
            Assert.Equal(1, await ReadScalarIntAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_LocalPromotionsTable_Store_EffectiveRange';"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Catalog_and_promotion_caches_do_not_clear_each_other()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var catalogRepository = new LocalCatalogRepository(store);
            var promotionRepository = new LocalPromotionRepository(store);
            var current = DateTimeOffset.Parse("2026-06-13T12:00:00Z");

            // 按 LocalCatalogSyncService 的真实顺序：catalog 链路先写，promotion 链路紧接着写。
            // 修复前两者共用 LocalPromotionRules + LocalPromotionProducts 且各自“按门店全删再全插”，
            // 后写的一方会把先写的一方整店清空，收银端因此拿到另一条链路口径的规则。
            await catalogRepository.ReplacePromotionRulesAsync(
                "S01",
                [
                    new CatalogPromotionRuleDto(
                        "PROMO-CATALOG",
                        "Catalog rule",
                        true,
                        0,
                        2,
                        5m,
                        null,
                        current.AddDays(-1),
                        current.AddDays(1),
                        current,
                        [new CatalogPromotionProductDto("SKU-CATALOG", 1)])
                ]);
            await promotionRepository.ReplaceStoreRulesAsync(
                "S01",
                CreateRulesResponse("S01", current, CreateRule("PROMO-RULES", current, ["SKU-RULES"])));

            var catalogRules = await catalogRepository.LoadPromotionRulesAsync("S01");
            var promotionRules = await promotionRepository.GetActiveRulesAsync("S01", current);

            Assert.Equal("PROMO-CATALOG", Assert.Single(catalogRules).PromotionId);
            Assert.Equal("PROMO-RULES", Assert.Single(promotionRules).Id);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ReplaceStoreRulesAsync_replaces_only_selected_store_rules()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            var current = DateTimeOffset.Parse("2026-06-13T12:00:00Z");

            await repository.ReplaceStoreRulesAsync("S01", CreateRulesResponse("S01", current, CreateRule("PROMO-OLD-S01", current, ["SKU-001"])));
            await repository.ReplaceStoreRulesAsync("S02", CreateRulesResponse("S02", current, CreateRule("PROMO-KEEP-S02", current, ["SKU-002"])));

            await repository.ReplaceStoreRulesAsync("S01", CreateRulesResponse("S01", current, CreateRule("PROMO-NEW-S01", current, ["SKU-003", "SKU-004"])));

            var s01Rules = await repository.GetActiveRulesAsync("S01", current);
            var s02Rules = await repository.GetActiveRulesAsync("S02", current);

            var s01Rule = Assert.Single(s01Rules);
            Assert.Equal("PROMO-NEW-S01", s01Rule.Id);
            Assert.Equal(["SKU-003", "SKU-004"], s01Rule.Products.Select(item => item.ProductCode).ToArray());

            var s02Rule = Assert.Single(s02Rules);
            Assert.Equal("PROMO-KEEP-S02", s02Rule.Id);
            Assert.Equal(["SKU-002"], s02Rule.Products.Select(item => item.ProductCode).ToArray());
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetActiveRulesAsync_returns_only_rules_effective_at_requested_time()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            var asOf = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
            var response = new PromotionRulesResponse(
                "S01",
                asOf,
                [
                    CreateRule(
                        "PROMO-ACTIVE-HIGH",
                        asOf,
                        ["SKU-001"],
                        priority: 20,
                        effectiveStart: asOf.AddDays(-1).UtcDateTime,
                        effectiveEnd: asOf.AddDays(1).UtcDateTime),
                    CreateRule(
                        "PROMO-EXPIRED",
                        asOf,
                        ["SKU-002"],
                        priority: 10,
                        effectiveStart: asOf.AddDays(-5).UtcDateTime,
                        effectiveEnd: asOf.AddSeconds(-1).UtcDateTime),
                    CreateRule(
                        "PROMO-FUTURE",
                        asOf,
                        ["SKU-003"],
                        priority: 30,
                        effectiveStart: asOf.AddSeconds(1).UtcDateTime,
                        effectiveEnd: asOf.AddDays(2).UtcDateTime)
                ]);

            await repository.ReplaceStoreRulesAsync("S01", response);

            var activeRules = await repository.GetActiveRulesAsync("S01", asOf);

            var activeRule = Assert.Single(activeRules);
            Assert.Equal("PROMO-ACTIVE-HIGH", activeRule.Id);
            Assert.Equal(["SKU-001"], activeRule.Products.Select(item => item.ProductCode).ToArray());
            Assert.Equal(20, activeRule.Priority);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ReplaceStoreRulesAsync_when_product_insert_fails_rolls_back_and_keeps_previous_rules()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            var current = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
            await repository.ReplaceStoreRulesAsync(
                "S01",
                CreateRulesResponse("S01", current, CreateRule("PROMO-OLD", current, ["SKU-OLD-1", "SKU-OLD-2"])));

            var duplicateProductRule = CreateRule("PROMO-NEW", current, ["SKU-DUP", "SKU-DUP"]);

            await Assert.ThrowsAnyAsync<Exception>(() =>
                repository.ReplaceStoreRulesAsync(
                    "S01",
                    CreateRulesResponse("S01", current, duplicateProductRule)));

            var activeRules = await repository.GetActiveRulesAsync("S01", current);

            var activeRule = Assert.Single(activeRules);
            Assert.Equal("PROMO-OLD", activeRule.Id);
            Assert.Equal(["SKU-OLD-1", "SKU-OLD-2"], activeRule.Products.Select(item => item.ProductCode).ToArray());
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetActiveRulesAsync_serves_repeat_reads_from_memory_until_store_rules_are_replaced()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = await CreateStoreAsync(databasePath);
            var repository = new LocalPromotionRepository(store);
            var current = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
            await repository.ReplaceStoreRulesAsync(
                "S01",
                CreateRulesResponse("S01", current, CreateRule("PROMO-A", current, ["SKU-A"])));

            Assert.Equal("PROMO-A", Assert.Single(await repository.GetActiveRulesAsync("S01", current)).Id);

            // 绕过仓储直接清空促销表：命中缓存时不再读库，仍返回上一次读到的规则。
            await ExecuteSqlAsync(store, "DELETE FROM LocalPromotionRuleProducts; DELETE FROM LocalPromotions;");
            var cachedRule = Assert.Single(await repository.GetActiveRulesAsync(" S01 ", current.AddMinutes(1)));
            Assert.Equal("PROMO-A", cachedRule.Id);
            Assert.Equal(["SKU-A"], cachedRule.Products.Select(item => item.ProductCode).ToArray());

            // 通过仓储写促销表后缓存失效，下一次读取看到新规则。
            await repository.ReplaceStoreRulesAsync(
                "S01",
                CreateRulesResponse("S01", current, CreateRule("PROMO-B", current, ["SKU-B"])));
            var replacedRule = Assert.Single(await repository.GetActiveRulesAsync("S01", current));
            Assert.Equal("PROMO-B", replacedRule.Id);
            Assert.Equal(["SKU-B"], replacedRule.Products.Select(item => item.ProductCode).ToArray());
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetActiveRulesAsync_filters_cached_rules_by_as_of_time_with_inclusive_bounds_and_sql_order()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = await CreateStoreAsync(databasePath);
            var repository = new LocalPromotionRepository(store);
            var asOf = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
            await repository.ReplaceStoreRulesAsync(
                "S01",
                new PromotionRulesResponse(
                    "S01",
                    asOf,
                    [
                        CreateRule("PROMO-EXPIRED", asOf, ["SKU-1"], priority: 50, effectiveStart: asOf.AddDays(-5).UtcDateTime, effectiveEnd: asOf.AddSeconds(-1).UtcDateTime),
                        CreateRule("PROMO-LATE-START", asOf, ["SKU-2"], priority: 10, effectiveStart: asOf.AddHours(-1).UtcDateTime, effectiveEnd: asOf.AddDays(1).UtcDateTime),
                        CreateRule("PROMO-EARLY-START", asOf, ["SKU-3"], priority: 10, effectiveStart: asOf.AddHours(-2).UtcDateTime, effectiveEnd: asOf.AddDays(1).UtcDateTime),
                        CreateRule("PROMO-HIGH", asOf, ["SKU-4"], priority: 20, effectiveStart: asOf.AddDays(-1).UtcDateTime, effectiveEnd: asOf.UtcDateTime),
                        CreateRule("PROMO-FUTURE", asOf, ["SKU-5"], priority: 30, effectiveStart: asOf.AddSeconds(1).UtcDateTime, effectiveEnd: asOf.AddDays(2).UtcDateTime)
                    ]));

            // 首次读取把整店规则载入内存；之后清空表，后续不同 asOf 的结果只能来自内存过滤。
            var atAsOf = await repository.GetActiveRulesAsync("S01", asOf);
            await ExecuteSqlAsync(store, "DELETE FROM LocalPromotionRuleProducts; DELETE FROM LocalPromotions;");

            // 与原 SQL 一致：起止时间都含边界；按 Priority DESC、EffectiveStart ASC、PromotionId ASC 排序。
            Assert.Equal(
                ["PROMO-HIGH", "PROMO-EARLY-START", "PROMO-LATE-START"],
                atAsOf.Select(rule => rule.Id).ToArray());
            Assert.Equal(
                ["PROMO-FUTURE", "PROMO-EARLY-START", "PROMO-LATE-START"],
                (await repository.GetActiveRulesAsync("S01", asOf.AddSeconds(1))).Select(rule => rule.Id).ToArray());
            Assert.Equal(
                ["PROMO-EXPIRED", "PROMO-HIGH", "PROMO-EARLY-START", "PROMO-LATE-START"],
                (await repository.GetActiveRulesAsync("S01", asOf.AddSeconds(-1))).Select(rule => rule.Id).ToArray());
            Assert.Equal(
                ["PROMO-FUTURE"],
                (await repository.GetActiveRulesAsync("S01", asOf.AddDays(1).AddTicks(1))).Select(rule => rule.Id).ToArray());
            Assert.Empty(await repository.GetActiveRulesAsync("S01", asOf.AddDays(3)));

            // 非 UTC 的 asOf 先换算成 UTC 再比较。
            var sameInstantInSydney = asOf.ToOffset(TimeSpan.FromHours(10));
            Assert.Equal(
                atAsOf.Select(rule => rule.Id).ToArray(),
                (await repository.GetActiveRulesAsync("S01", sameInstantInSydney)).Select(rule => rule.Id).ToArray());
            var high = (await repository.GetActiveRulesAsync("S01", asOf)).First();
            Assert.Equal(asOf.UtcDateTime, high.EffectiveEnd);
            Assert.Equal(DateTimeKind.Utc, high.EffectiveEnd.Kind);
            Assert.Equal(["SKU-4"], high.Products.Select(item => item.ProductCode).ToArray());
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetActiveRulesAsync_returns_collections_that_cannot_change_the_cached_rules()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            var current = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
            await repository.ReplaceStoreRulesAsync(
                "S01",
                CreateRulesResponse("S01", current, CreateRule("PROMO-A", current, ["SKU-1", "SKU-2"])));

            var first = await repository.GetActiveRulesAsync("S01", current);
            var tampered = CreateRule("PROMO-TAMPERED", current, ["SKU-X"]);
            // 调用方即使把返回值当可写集合改动，也只能改到自己的副本。
            if (first is IList<PromotionRuleDto> { IsReadOnly: false } rules)
            {
                rules[0] = tampered;
            }

            if (first[0].Products is IList<PromotionRuleProductDto> { IsReadOnly: false } products)
            {
                products[0] = new PromotionRuleProductDto("SKU-X", 99);
            }

            var second = await repository.GetActiveRulesAsync("S01", current);

            var rule = Assert.Single(second);
            Assert.Equal("PROMO-A", rule.Id);
            Assert.Equal(["SKU-1", "SKU-2"], rule.Products.Select(item => item.ProductCode).ToArray());
            Assert.NotSame(first, second);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetActiveRulesAsync_reloads_rules_when_active_local_database_switches()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hbpos-promotions-switch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var state = new ApiRuntimeEndpointState("https://first.example.com/pos-api/");
            var resolver = new ApiEndpointDatabasePartitionResolver(root, state.CurrentAddress.AbsoluteUri);
            var store = new LocalSqliteStore(state, resolver);
            var schema = new LocalSchemaService(store);
            await schema.EnsureInitializedAsync();
            var repository = new LocalPromotionRepository(store);
            var current = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
            await repository.ReplaceStoreRulesAsync(
                "S01",
                CreateRulesResponse("S01", current, CreateRule("PROMO-FIRST-DB", current, ["SKU-1"])));
            Assert.Equal("PROMO-FIRST-DB", Assert.Single(await repository.GetActiveRulesAsync("S01", current)).Id);

            // 切到另一服务器的本地库：缓存按活动库路径区分，不能继续返回旧库的规则。
            var prepared = await store.PrepareSwitchAsync("https://second.example.com/pos-api/", CancellationToken.None);
            store.Switch(prepared);
            await schema.EnsureInitializedAsync();
            Assert.Empty(await repository.GetActiveRulesAsync("S01", current));

            await repository.ReplaceStoreRulesAsync(
                "S01",
                CreateRulesResponse("S01", current, CreateRule("PROMO-SECOND-DB", current, ["SKU-2"])));
            Assert.Equal("PROMO-SECOND-DB", Assert.Single(await repository.GetActiveRulesAsync("S01", current)).Id);

            // 回滚到原库后重新读原库内容。
            store.Rollback(prepared);
            Assert.Equal("PROMO-FIRST-DB", Assert.Single(await repository.GetActiveRulesAsync("S01", current)).Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetActiveRulesAsync_loads_from_database_off_the_caller_context_and_then_hits_memory()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            var current = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
            await repository.ReplaceStoreRulesAsync(
                "S01",
                CreateRulesResponse("S01", current, CreateRule("PROMO-A", current, ["SKU-A"])));
            var loads = new System.Collections.Concurrent.ConcurrentQueue<(int ThreadId, bool OnUiContext)>();
            repository.StoreRulesLoadingForTests = () => loads.Enqueue((
                Environment.CurrentManagedThreadId,
                SingleThreadSynchronizationContextTestSupport.IsOnUiContext));

            // 扫码后的促销重算在 UI 线程调用：首次（未命中）读库必须离开 UI 线程，命中后不再读库。
            var ((firstRules, secondRules), uiThreadId) = await SingleThreadSynchronizationContextTestSupport.RunAsync(async () =>
            {
                var first = await repository.GetActiveRulesAsync("S01", current);
                var second = await repository.GetActiveRulesAsync("S01", current.AddMinutes(5));
                return (first, second);
            });

            var load = Assert.Single(loads);
            Assert.NotEqual(uiThreadId, load.ThreadId);
            Assert.False(load.OnUiContext);
            Assert.Equal("PROMO-A", Assert.Single(firstRules).Id);
            Assert.Equal("PROMO-A", Assert.Single(secondRules).Id);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task GetActiveRulesAsync_does_not_cache_a_load_that_started_before_rules_were_replaced()
    {
        var databasePath = CreateTempDatabasePath();
        var firstLoadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirstLoad = new ManualResetEventSlim();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            var current = DateTimeOffset.Parse("2026-06-13T12:00:00Z");
            await repository.ReplaceStoreRulesAsync(
                "S01",
                CreateRulesResponse("S01", current, CreateRule("PROMO-OLD", current, ["SKU-OLD"])));
            var loadCount = 0;
            repository.StoreRulesLoadingForTests = () =>
            {
                if (Interlocked.Increment(ref loadCount) == 1)
                {
                    // 第一次未命中的读库停在线程池里：模拟扫码触发的读库尚未返回时，目录同步提交了新规则。
                    firstLoadEntered.TrySetResult();
                    releaseFirstLoad.Wait(AsyncTestWaitSupport.DefaultTimeout);
                }
            };

            var inFlightRead = repository.GetActiveRulesAsync("S01", current);
            await firstLoadEntered.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

            // 读库进行中提交新规则并失效缓存；这次读取在失效之前就已开始（取到的是旧版本号）。
            await repository.ReplaceStoreRulesAsync(
                "S01",
                CreateRulesResponse("S01", current, CreateRule("PROMO-NEW", current, ["SKU-NEW"])));
            releaseFirstLoad.Set();
            await inFlightRead.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

            // 生产中读库与提交的先后不可控，失效前开始的读取可能读到旧规则，所以版本号防护对这类读取一律不写回缓存。
            // 钩子在读库之前执行，这里读到什么不重要；只看下一次读取是否重新读库（钩子第二次被调用）并拿到新规则。
            var afterReplace = Assert.Single(await repository.GetActiveRulesAsync("S01", current));
            Assert.Equal("PROMO-NEW", afterReplace.Id);
            Assert.Equal(["SKU-NEW"], afterReplace.Products.Select(item => item.ProductCode).ToArray());
            Assert.Equal(2, Volatile.Read(ref loadCount));

            // 失效之后开始的读取照常写回缓存，再读命中内存、不再读库。
            Assert.Equal("PROMO-NEW", Assert.Single(await repository.GetActiveRulesAsync("S01", current.AddMinutes(1))).Id);
            Assert.Equal(2, Volatile.Read(ref loadCount));
        }
        finally
        {
            // 断言失败时也放行挂起的读库，避免线程池线程一直阻塞到超时。
            releaseFirstLoad.Set();
            DeleteTempDatabase(databasePath);
        }
    }

    private static async Task<LocalSqliteStore> CreateStoreAsync(string databasePath)
    {
        var store = new LocalSqliteStore(databasePath);
        await new LocalSchemaService(store).InitializeAsync();
        return store;
    }

    private static async Task ExecuteSqlAsync(LocalSqliteStore store, string sql)
    {
        await using var connection = await store.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<LocalPromotionRepository> CreateRepositoryAsync(string databasePath)
    {
        var store = new LocalSqliteStore(databasePath);
        var schema = new LocalSchemaService(store);
        await schema.InitializeAsync();
        return new LocalPromotionRepository(store);
    }

    private static PromotionRulesResponse CreateRulesResponse(
        string storeCode,
        DateTimeOffset generatedAt,
        params PromotionRuleDto[] rules)
    {
        return new PromotionRulesResponse(storeCode, generatedAt, rules);
    }

    private static PromotionRuleDto CreateRule(
        string id,
        DateTimeOffset generatedAt,
        IEnumerable<string> productCodes,
        int priority = 10,
        bool isExclusive = false,
        int applyQuantity = 2,
        decimal fixedPrice = 9.99m,
        int? maxApplicationsPerOrder = 1,
        DateTime? effectiveStart = null,
        DateTime? effectiveEnd = null)
    {
        return new PromotionRuleDto(
            id,
            $"Rule {id}",
            effectiveStart ?? generatedAt.AddDays(-1).UtcDateTime,
            effectiveEnd ?? generatedAt.AddDays(1).UtcDateTime,
            isExclusive,
            priority,
            applyQuantity,
            fixedPrice,
            maxApplicationsPerOrder,
            productCodes.Select((productCode, index) => new PromotionRuleProductDto(productCode, index + 1)).ToArray());
    }

    private static string CreateTempDatabasePath()
    {
        return Path.Combine(Path.GetTempPath(), $"hbpos-promotions-{Guid.NewGuid():N}.db");
    }

    private static void DeleteTempDatabase(string databasePath)
    {
        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task<int> ReadScalarIntAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
