using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

public sealed class LocalCatalogRepositoryTests
{
    [Fact]
    public async Task UpsertSellableItemsAsync_inserts_and_updates_by_store_and_normalized_lookup_code()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            var original = CreateItem("S001", "SKU-001", " abc ", "Original name", 1.25m, "https://images.example/original.jpg", "REF-001");
            var updated = CreateItem("S001", "SKU-001B", "ABC", "Updated name", 2.50m, "https://images.example/updated.jpg", "REF-002");

            await repository.UpsertSellableItemsAsync([original]);
            await repository.UpsertSellableItemsAsync([updated]);

            var items = await repository.LoadSellableItemsAsync();
            var saved = Assert.Single(items);
            Assert.Equal("SKU-001B", saved.ProductCode);
            Assert.Equal("Updated name", saved.DisplayName);
            Assert.Equal("REF-002", saved.ReferenceCode);
            Assert.Equal(2.50m, saved.RetailPrice);
            Assert.Equal("https://images.example/updated.jpg", saved.ProductImage);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ReplaceCodeConflictItemsAsync_replaces_per_store_and_preserves_server_order()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            var fly = CreateItem("S001", "P-FLY", "6405090401470", "EXTENSION Fly Swatter", 8.99m);
            var flower = CreateItem("S001", "P-FLOWER", "6405090401470", "flower", 2.99m);
            var stale = CreateItem("S001", "P-STALE", "STALE-CODE", "Stale", 1m);
            var otherStore = CreateItem("S002", "P-OTHER", "6405090401470", "Other store", 5m);

            await repository.ReplaceCodeConflictItemsAsync("S001", [stale]);
            await repository.ReplaceCodeConflictItemsAsync("S002", [otherStore]);
            // 服务端顺序：首条是目录胜出项；不属于该门店的行被忽略，旧数据整体替换。
            await repository.ReplaceCodeConflictItemsAsync("S001", [fly, flower, otherStore]);

            var s001 = await repository.LoadCodeConflictItemsAsync("S001");
            Assert.Equal(["P-FLY", "P-FLOWER"], s001.Select(item => item.ProductCode));
            Assert.Equal(8.99m, s001[0].RetailPrice);
            Assert.Equal("flower", s001[1].DisplayName);
            Assert.Equal("6405090401470", s001[1].LookupCode);
            Assert.Equal(["P-OTHER"], (await repository.LoadCodeConflictItemsAsync("S002")).Select(item => item.ProductCode));
            // 冲突候选单独存放，不会写进对查询码唯一的主目录表。
            Assert.Empty(await repository.LoadSellableItemsAsync("S001"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Versioned_store_replace_records_the_catalog_version_in_the_same_commit()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.UpsertSellableItemsAsync([CreateItem("S001", "P-OLD", "OLD", "Old", 1m)]);

            await using (var session = await repository.BeginStoreReplaceSessionAsync("S001"))
            {
                await session.StageAsync([CreateItem("S001", "P-A", "A", "A", 1m), CreateItem("S001", "P-B", "b", "B", 2m)]);
                var result = await session.CommitAsync(new LocalCatalogVersionStamp("catalog-v1:b", 2));
                Assert.Equal((2, 1), (result.InsertedCount, result.DeletedCount));
            }

            Assert.Equal("catalog-v1:b", await repository.GetCatalogVersionAsync("S001"));
            Assert.Null(await repository.GetCatalogVersionAsync("S002"));
            Assert.Equal(["A", "b"], (await repository.LoadSellableItemsAsync("S001")).Select(item => item.LookupCode));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Versioned_store_replace_rolls_back_when_the_staged_count_does_not_match_the_version()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await using (var first = await repository.BeginStoreReplaceSessionAsync("S001"))
            {
                await first.StageAsync([CreateItem("S001", "P-OLD", "OLD", "Old", 1m)]);
                await first.CommitAsync(new LocalCatalogVersionStamp("catalog-v1:a", 1));
            }

            await using (var second = await repository.BeginStoreReplaceSessionAsync("S001"))
            {
                // 同一个码在暂存里出现两次只会留一行，条数对不上版本总数，必须整体放弃。
                await second.StageAsync([CreateItem("S001", "P-A", "A", "A", 1m), CreateItem("S001", "P-A2", " a ", "A2", 2m)]);
                await Assert.ThrowsAsync<LocalCatalogVersionConflictException>(() =>
                    second.CommitAsync(new LocalCatalogVersionStamp("catalog-v1:b", 2)));
            }

            Assert.Equal("catalog-v1:a", await repository.GetCatalogVersionAsync("S001"));
            Assert.Equal(["OLD"], (await repository.LoadSellableItemsAsync("S001")).Select(item => item.LookupCode));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Unversioned_store_replace_clears_the_recorded_version()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await using (var versioned = await repository.BeginStoreReplaceSessionAsync("S001"))
            {
                await versioned.StageAsync([CreateItem("S001", "P-A", "A", "A", 1m)]);
                await versioned.CommitAsync(new LocalCatalogVersionStamp("catalog-v1:a", 1));
            }

            await using (var legacy = await repository.BeginStoreReplaceSessionAsync("S001"))
            {
                await legacy.StageAsync([CreateItem("S001", "P-B", "B", "B", 1m)]);
                await legacy.CommitAsync();
            }

            Assert.Null(await repository.GetCatalogVersionAsync("S001"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ApplyCatalogDeltaAsync_applies_upserts_and_deletes_and_moves_the_version_atomically()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await using (var session = await repository.BeginStoreReplaceSessionAsync("S001"))
            {
                await session.StageAsync([CreateItem("S001", "P-A", "A", "A", 1m), CreateItem("S001", "P-B", "B", "B", 2m)]);
                await session.CommitAsync(new LocalCatalogVersionStamp("catalog-v1:a", 2));
            }

            var stale = await Assert.ThrowsAsync<LocalCatalogVersionConflictException>(() =>
                repository.ApplyCatalogDeltaAsync("S001", "catalog-v1:other", "catalog-v1:b", [CreateItem("S001", "P-X", "X", "X", 9m)], ["A"]));
            Assert.Contains("catalog-v1:a", stale.Message, StringComparison.Ordinal);
            Assert.Equal(["A", "B"], (await repository.LoadSellableItemsAsync("S001")).Select(item => item.LookupCode));

            var result = await repository.ApplyCatalogDeltaAsync(
                "S001",
                "catalog-v1:a",
                "catalog-v1:b",
                [CreateItem("S001", "P-B", "B", "B changed", 2.5m), CreateItem("S001", "P-C", "C", "C", 3m)],
                ["a"]);

            Assert.Equal((2, 1, 2), (result.UpsertedCount, result.DeletedCount, result.LocalItemCount));
            Assert.Equal("catalog-v1:b", await repository.GetCatalogVersionAsync("S001"));
            var items = await repository.LoadSellableItemsAsync("S001");
            Assert.Equal(["B", "C"], items.Select(item => item.LookupCode));
            Assert.Equal(2.5m, items[0].RetailPrice);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ReplaceCodeConflictItemsIfChangedAsync_skips_identical_candidates()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            var fly = CreateItem("S001", "P-FLY", "6405090401470", "EXTENSION Fly Swatter", 8.99m);
            var flower = CreateItem("S001", "P-FLOWER", "6405090401470", "flower", 2.99m);

            Assert.True(await repository.ReplaceCodeConflictItemsIfChangedAsync("S001", [fly, flower]));
            Assert.False(await repository.ReplaceCodeConflictItemsIfChangedAsync("S001", [fly, flower]));
            // 备选商品单独改价、顺序变化都算变化。
            Assert.True(await repository.ReplaceCodeConflictItemsIfChangedAsync("S001", [fly, flower with { RetailPrice = 3.49m }]));
            Assert.True(await repository.ReplaceCodeConflictItemsIfChangedAsync("S001", [flower with { RetailPrice = 3.49m }, fly]));
            Assert.Equal(
                ["P-FLOWER", "P-FLY"],
                (await repository.LoadCodeConflictItemsAsync("S001")).Select(item => item.ProductCode));
            Assert.True(await repository.ReplaceCodeConflictItemsIfChangedAsync("S001", []));
            Assert.False(await repository.ReplaceCodeConflictItemsIfChangedAsync("S001", []));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task UpsertSellableItemsAsync_inserts_and_updates_discount_rate()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            var original = CreateItem("S001", "SKU-001", " abc ", "Original name", 1.25m, discountRate: 0.2m);
            var updated = CreateItem("S001", "SKU-001B", "ABC", "Updated name", 2.50m, discountRate: 0.35m);

            await repository.UpsertSellableItemsAsync([original]);
            await repository.UpsertSellableItemsAsync([updated]);

            var items = await repository.LoadSellableItemsAsync();
            var saved = Assert.Single(items);
            Assert.Equal(0.35m, saved.DiscountRate);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task UpsertSellableItemsAsync_persists_special_flag_and_changes_compare_hash()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.UpsertSellableItemsAsync(
            [
                CreateItem("S001", "SKU-001", "abc", "Original name", 1.25m, isSpecialProduct: false)
            ]);
            var before = Assert.Single(await repository.LoadSellableItemComparePageAsync("S001", null, 10));

            await repository.UpsertSellableItemsAsync(
            [
                CreateItem("S001", "SKU-001", "abc", "Original name", 1.25m, isSpecialProduct: true)
            ]);

            var saved = Assert.Single(await repository.LoadSellableItemsAsync());
            var after = Assert.Single(await repository.LoadSellableItemComparePageAsync("S001", null, 10));
            Assert.True(saved.IsSpecialProduct);
            Assert.NotEqual(before.ContentHash, after.ContentHash);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task LoadSpecialProductItemsAsync_uses_local_sort_and_keeps_images()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.UpsertSellableItemsAsync(
            [
                CreateItem("S001", "SKU-001", "abc", "Alpha", 1m, "https://images.example/alpha.jpg", isSpecialProduct: true),
                CreateItem("S001", "SKU-002", "def", "Beta", 2m, "https://images.example/beta.jpg", isSpecialProduct: true),
                CreateItem("S001", "SKU-003", "ghi", "Gamma", 3m, isSpecialProduct: false)
            ]);
            await repository.SaveSpecialProductOrderAsync("S001", ["SKU-002", "SKU-001"]);

            var specialItems = await repository.LoadSpecialProductItemsAsync("S001");

            Assert.Equal(["SKU-002", "SKU-001"], specialItems.Select(x => x.ProductCode).ToArray());
            Assert.All(specialItems, item => Assert.True(item.IsSpecialProduct));
            Assert.Equal("https://images.example/beta.jpg", specialItems[0].ProductImage);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task UpdateSpecialProductFlagAsync_removes_item_from_special_list_and_sort_order()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.UpsertSellableItemsAsync(
            [
                CreateItem("S001", "SKU-001", "abc", "Alpha", 1m, isSpecialProduct: true),
                CreateItem("S001", "SKU-002", "def", "Beta", 2m, isSpecialProduct: true)
            ]);
            await repository.SaveSpecialProductOrderAsync("S001", ["SKU-002", "SKU-001"]);

            var updated = await repository.UpdateSpecialProductFlagAsync("S001", "SKU-002", false);
            var specialItems = await repository.LoadSpecialProductItemsAsync("S001");

            Assert.Equal(1, updated);
            var item = Assert.Single(specialItems);
            Assert.Equal("SKU-001", item.ProductCode);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ClearSpecialProductFlagsExceptAsync_unmarks_missing_products_and_keeps_requested_sort_order()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.UpsertSellableItemsAsync(
            [
                CreateItem("S001", "SKU-001", "abc", "Alpha", 1m, isSpecialProduct: true),
                CreateItem("S001", "SKU-002", "def", "Beta", 2m, isSpecialProduct: true),
                CreateItem("S001", "SKU-003", "ghi", "Gamma", 3m, isSpecialProduct: true)
            ]);
            await repository.SaveSpecialProductOrderAsync("S001", ["SKU-003", "SKU-002", "SKU-001"]);

            var updated = await repository.ClearSpecialProductFlagsExceptAsync("S001", ["SKU-002"]);
            var specialItems = await repository.LoadSpecialProductItemsAsync("S001");

            Assert.Equal(2, updated);
            var item = Assert.Single(specialItems);
            Assert.Equal("SKU-002", item.ProductCode);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task DeleteByLookupCodesAsync_deletes_only_matching_store_and_normalized_lookup_codes()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.UpsertSellableItemsAsync(
            [
                CreateItem("S001", "SKU-001", "abc", "S001 ABC", 1m),
                CreateItem("S001", "SKU-002", "def", "S001 DEF", 2m),
                CreateItem("S002", "SKU-003", "ABC", "S002 ABC", 3m)
            ]);

            var deleted = await repository.DeleteByLookupCodesAsync("S001", [" ABC "]);

            Assert.Equal(1, deleted);
            Assert.Null(await repository.FindByLookupCodeAsync("S001", "abc"));
            Assert.NotNull(await repository.FindByLookupCodeAsync("S001", "def"));
            Assert.NotNull(await repository.FindByLookupCodeAsync("S002", "abc"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task BeginStoreReplaceSessionAsync_commit_replaces_only_selected_store_snapshot_and_preserves_other_stores()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.UpsertSellableItemsAsync(
            [
                CreateItem("S001", "OLD-001", "lookup-001", "Old one", 1m),
                CreateItem("S001", "OLD-002", "lookup-002", "Old two", 2m),
                CreateItem("S002", "KEEP-001", "lookup-keep", "Keep me", 3m)
            ]);

            await using var session = await repository.BeginStoreReplaceSessionAsync("S001");
            await session.StageAsync(
            [
                CreateItem("S001", "NEW-001", "lookup-101", "New one", 10m),
                CreateItem("S001", "NEW-002", "lookup-102", "New two", 20m)
            ]);

            var result = await session.CommitAsync();

            Assert.Equal(2, result.InsertedCount);
            Assert.Equal(2, result.DeletedCount);

            var s001Items = await repository.LoadSellableItemsAsync("S001");
            Assert.Equal(["NEW-001", "NEW-002"], s001Items.Select(item => item.ProductCode).ToArray());
            Assert.Null(await repository.FindByLookupCodeAsync("S001", "lookup-001"));
            Assert.Null(await repository.FindByLookupCodeAsync("S001", "lookup-002"));

            var s002Items = await repository.LoadSellableItemsAsync("S002");
            var preserved = Assert.Single(s002Items);
            Assert.Equal("KEEP-001", preserved.ProductCode);
            Assert.Equal("Keep me", preserved.DisplayName);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task BeginStoreReplaceSessionAsync_dispose_without_commit_leaves_existing_rows_unchanged()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.UpsertSellableItemsAsync(
            [
                CreateItem("S001", "OLD-001", "lookup-001", "Old one", 1m),
                CreateItem("S002", "KEEP-001", "lookup-keep", "Keep me", 3m)
            ]);

            await using (var session = await repository.BeginStoreReplaceSessionAsync("S001"))
            {
                await session.StageAsync(
                [
                    CreateItem("S001", "NEW-001", "lookup-101", "New one", 10m)
                ]);
            }

            var s001Items = await repository.LoadSellableItemsAsync("S001");
            var unchanged = Assert.Single(s001Items);
            Assert.Equal("OLD-001", unchanged.ProductCode);
            Assert.Equal("Old one", unchanged.DisplayName);
            Assert.NotNull(await repository.FindByLookupCodeAsync("S001", "lookup-001"));
            Assert.Null(await repository.FindByLookupCodeAsync("S001", "lookup-101"));

            var s002Items = await repository.LoadSellableItemsAsync("S002");
            Assert.Equal("KEEP-001", Assert.Single(s002Items).ProductCode);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task BeginStoreReplaceSessionAsync_commit_uses_existing_upsert_semantics_for_duplicate_lookup_codes()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.UpsertSellableItemsAsync(
            [
                CreateItem("S001", "OLD-001", "dup-code", "Old item", 1m)
            ]);

            await using var session = await repository.BeginStoreReplaceSessionAsync("S001");
            await session.StageAsync(
            [
                CreateItem("S001", "NEW-001", " dup-code ", "First duplicate", 10m, "https://images.example/first.jpg", "REF-001"),
                CreateItem("S001", "NEW-002", "DUP-CODE", "Second duplicate", 20m, "https://images.example/second.jpg", "REF-002")
            ]);

            var result = await session.CommitAsync();

            Assert.Equal(1, result.InsertedCount);

            var stored = Assert.Single(await repository.LoadSellableItemsAsync("S001"));
            Assert.Equal("NEW-002", stored.ProductCode);
            Assert.Equal("Second duplicate", stored.DisplayName);
            Assert.Equal("REF-002", stored.ReferenceCode);
            Assert.Equal(20m, stored.RetailPrice);
            Assert.Equal("https://images.example/second.jpg", stored.ProductImage);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task LoadSellableItemsAsync_WithStoreCode_ReturnsOnlyThatStore()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.UpsertSellableItemsAsync(
            [
                CreateItem("S001", "SKU-001", "abc", "S001 ABC", 1m),
                CreateItem("S002", "SKU-002", "abc", "S002 ABC", 2m)
            ]);

            var items = await repository.LoadSellableItemsAsync("S002");

            var item = Assert.Single(items);
            Assert.Equal("S002", item.StoreCode);
            Assert.Equal("SKU-002", item.ProductCode);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task LoadSellableItemComparePageAsync_pages_by_store_and_normalized_lookup_code()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.UpsertSellableItemsAsync(
            [
                CreateItem("S001", "SKU-B", "b-code", "B item", 2m),
                CreateItem("S001", "SKU-A", "a-code", "A item", 1m),
                CreateItem("S001", "SKU-C", "c-code", "C item", 3m),
                CreateItem("S002", "SKU-D", "aa-code", "Other store item", 4m)
            ]);

            var firstPage = await repository.LoadSellableItemComparePageAsync("S001", afterLookupCodeNormalized: null, pageSize: 2);
            var secondPage = await repository.LoadSellableItemComparePageAsync("S001", firstPage[^1].LookupCodeNormalized, pageSize: 2);

            Assert.Equal(["A-CODE", "B-CODE"], firstPage.Select(row => row.LookupCodeNormalized).ToArray());
            var finalRow = Assert.Single(secondPage);
            Assert.Equal("C-CODE", finalRow.LookupCodeNormalized);
            Assert.All(firstPage.Concat(secondPage), row =>
            {
                Assert.Equal("S001", row.StoreCode);
                Assert.False(string.IsNullOrWhiteSpace(row.ContentHash));
                Assert.NotNull(row.SyncedAt);
            });
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task FindByLookupCodeAsync_matches_normalized_lookup_within_store()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.UpsertSellableItemsAsync(
            [
                CreateItem("S001", "SKU-001", "  lookup-1 ", "Lookup item", 1m),
                CreateItem("S002", "SKU-002", "LOOKUP-1", "Other store item", 2m)
            ]);

            var found = await repository.FindByLookupCodeAsync("S001", "lookup-1");

            Assert.NotNull(found);
            Assert.Equal("SKU-001", found.ProductCode);
            Assert.Equal("Lookup item", found.DisplayName);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ReplacePromotionRulesAsync_replaces_rules_for_store()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var repository = await CreateRepositoryAsync(databasePath);
            await repository.ReplacePromotionRulesAsync(
                "S001",
                [CreatePromotionRule("PROMO-OLD", "SKU-OLD")]);
            await repository.ReplacePromotionRulesAsync(
                "S002",
                [CreatePromotionRule("PROMO-OTHER", "SKU-OTHER")]);

            await repository.ReplacePromotionRulesAsync(
                "S001",
                [CreatePromotionRule("PROMO-NEW", "SKU-NEW", unitWeight: 2)]);

            var s001Rules = await repository.LoadPromotionRulesAsync("S001");
            var s002Rules = await repository.LoadPromotionRulesAsync("S002");
            var s001Rule = Assert.Single(s001Rules);
            Assert.Equal("PROMO-NEW", s001Rule.PromotionId);
            var product = Assert.Single(s001Rule.Products);
            Assert.Equal("SKU-NEW", product.ProductCode);
            Assert.Equal(2, product.UnitWeight);
            Assert.Equal("PROMO-OTHER", Assert.Single(s002Rules).PromotionId);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task LocalSqliteStore_OpenConnectionAsync_EnablesWalAndBusyTimeout()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            await using var connection = await store.OpenConnectionAsync();

            Assert.Equal("wal", await ReadScalarStringAsync(connection, "PRAGMA journal_mode;"));
            Assert.True(await ReadScalarIntAsync(connection, "PRAGMA busy_timeout;") >= 5000);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task LocalSqliteStore_CheckpointWalAsync_ExecutesPassiveCheckpoint()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            await using (var connection = await store.OpenConnectionAsync())
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE WalCheckpointProbe (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL);
                    INSERT INTO WalCheckpointProbe (Name) VALUES ('probe');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await store.CheckpointWalAsync();

            await using var verifyConnection = await store.OpenConnectionAsync();
            Assert.True(await ReadScalarIntAsync(verifyConnection, "PRAGMA wal_checkpoint(PASSIVE);") >= 0);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    private static async Task<LocalCatalogRepository> CreateRepositoryAsync(string databasePath)
    {
        var store = new LocalSqliteStore(databasePath);
        var schema = new LocalSchemaService(store);
        await schema.InitializeAsync();
        return new LocalCatalogRepository(store);
    }

    private static SellableItemDto CreateItem(
        string storeCode,
        string productCode,
        string lookupCode,
        string displayName,
        decimal retailPrice,
        string? productImage = null,
        string? referenceCode = null,
        decimal? discountRate = null,
        bool isSpecialProduct = false)
    {
        return new SellableItemDto(
            StoreCode: storeCode,
            ProductCode: productCode,
            ReferenceCode: referenceCode,
            DisplayName: displayName,
            LookupCode: lookupCode,
            ItemNumber: productCode,
            Barcode: lookupCode,
            RetailPrice: retailPrice,
            PriceSource: PriceSourceKind.StoreRetailPrice,
            PriceSourceLabel: PriceSourceKind.StoreRetailPrice.ToString(),
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow,
            ProductImage: productImage,
            DiscountRate: discountRate,
            IsSpecialProduct: isSpecialProduct);
    }

    private static CatalogPromotionRuleDto CreatePromotionRule(
        string promotionId,
        string productCode,
        int unitWeight = 1)
    {
        return new CatalogPromotionRuleDto(
            promotionId,
            "Quantity discount",
            IsExclusive: true,
            Priority: 10,
            ApplyQuantity: 2,
            FixedPrice: 15m,
            MaxApplicationsPerOrder: null,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1),
            DateTimeOffset.UtcNow,
            [new CatalogPromotionProductDto(productCode, unitWeight)]);
    }

    private static string CreateTempDatabasePath()
    {
        return Path.Combine(Path.GetTempPath(), $"hbpos-catalog-{Guid.NewGuid():N}.db");
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

    private static async Task<string> ReadScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (await command.ExecuteScalarAsync())?.ToString() ?? string.Empty;
    }

    private static async Task<int> ReadScalarIntAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
