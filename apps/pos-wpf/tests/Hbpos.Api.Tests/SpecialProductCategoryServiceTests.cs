using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using Hbpos.Api.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Api.Tests;

/// <summary>
/// 分店级特殊商品分类服务：快照（实时读库、只含当前仍是特殊商品的归属）、移入 / 移出分类、分类内排序。
/// 用 SQLite 替身库，建表方式与特殊商品实时查询测试一致，并额外建两张分类表。
/// </summary>
public sealed class SpecialProductCategoryServiceTests : IAsyncLifetime
{
    private readonly SpecialProductsSqliteDatabase database = new();
    private SpecialProductCategoryService service = null!;

    public async Task InitializeAsync()
    {
        var db = database.Client;
        db.CodeFirst.InitTables<StoreSpecialProductCategory, StoreSpecialProductCategoryItem>();
        service = new SpecialProductCategoryService(database.DbContext);

        await SpecialProductsLiveTestData.AddStoreAsync(db, "S01");
        await SpecialProductsLiveTestData.AddStoreAsync(db, "S02");
        await SpecialProductsLiveTestData.AddStoreAsync(db, "S03", isActive: false);
        for (var i = 1; i <= 6; i++)
        {
            await SpecialProductsLiveTestData.AddProductAsync(db, $"P{i}", $"BAR-{i}", $"ITEM-{i}", 10m + i, 1);
        }

        // S01：P1、P2、P3 是特殊商品，P4 不是；P5 只在 S02 是特殊商品；
        // P6 旧行是特殊、最新行不是 -> 不是特殊商品。
        await SpecialProductsLiveTestData.AddStorePriceAsync(db, "S01", "P1", 9m, 10, special: true);
        await SpecialProductsLiveTestData.AddStorePriceAsync(db, "S01", "P2", 9m, 10, special: true);
        await SpecialProductsLiveTestData.AddStorePriceAsync(db, "S01", "P3", 9m, 10, special: true);
        await SpecialProductsLiveTestData.AddStorePriceAsync(db, "S01", "P4", 9m, 10, special: false);
        await SpecialProductsLiveTestData.AddStorePriceAsync(db, "S02", "P5", 9m, 10, special: true);
        await SpecialProductsLiveTestData.AddStorePriceAsync(db, "S01", "P6", 9m, 5, special: true);
        await SpecialProductsLiveTestData.AddStorePriceAsync(db, "S01", "P6", 9m, 20, special: false);
    }

    public async Task DisposeAsync() => await database.DisposeAsync();

    private static readonly DateTime Stamp = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

    private async Task AddCategoryAsync(string guid, string storeCode, string name, int sortOrder, string? color = null, int minutes = 0)
    {
        await database.Client.Insertable(new StoreSpecialProductCategory
        {
            Guid = guid,
            StoreCode = storeCode,
            Name = name,
            Color = color,
            SortOrder = sortOrder,
            CreatedAt = Stamp.AddMinutes(minutes),
            UpdatedAt = Stamp.AddMinutes(minutes)
        }).ExecuteCommandAsync();
    }

    private async Task AddItemAsync(string storeCode, string productCode, string categoryGuid, int sortOrder, int minutes = 0)
    {
        await database.Client.Insertable(new StoreSpecialProductCategoryItem
        {
            StoreCode = storeCode,
            ProductCode = productCode,
            CategoryGuid = categoryGuid,
            SortOrder = sortOrder,
            CreatedAt = Stamp.AddMinutes(minutes),
            UpdatedAt = Stamp.AddMinutes(minutes)
        }).ExecuteCommandAsync();
    }

    private async Task<List<StoreSpecialProductCategoryItem>> ItemRowsAsync(string storeCode) =>
        await database.Client.Queryable<StoreSpecialProductCategoryItem>()
            .Where(x => x.StoreCode == storeCode)
            .ToListAsync();

    private static string[] CodesIn(CatalogSpecialProductCategoriesResponse snapshot, string categoryId) =>
        snapshot.Assignments
            .Where(x => x.CategoryId == categoryId)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.ProductCode, StringComparer.Ordinal)
            .Select(x => x.ProductCode)
            .ToArray();

    // ---------------------------------------------------------------- 快照

    [Fact]
    public async Task Snapshot_is_empty_with_zero_revision_when_store_has_no_categories()
    {
        var snapshot = await service.GetSnapshotAsync("S01", CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("S01", snapshot.StoreCode);
        Assert.Empty(snapshot.Categories);
        Assert.Empty(snapshot.Assignments);
        Assert.Equal("0", snapshot.Revision);
    }

    [Fact]
    public async Task Snapshot_returns_null_for_missing_or_inactive_store()
    {
        Assert.Null(await service.GetSnapshotAsync("NOPE", CancellationToken.None));
        Assert.Null(await service.GetSnapshotAsync("S03", CancellationToken.None));
    }

    [Fact]
    public async Task Snapshot_returns_categories_by_sort_order_and_assignments_by_category_then_order()
    {
        // 故意把 Snack 的 SortOrder 写得比 Fruit 小，且分类 guid 与顺序无关。
        await AddCategoryAsync("C-FRUIT", "S01", "Fruit", 1, "#E65A2F");
        await AddCategoryAsync("C-SNACK", "S01", "Snack", 0);
        await AddCategoryAsync("C-OTHER-STORE", "S02", "Other", 0);
        await AddItemAsync("S01", "P1", "C-FRUIT", 1);
        await AddItemAsync("S01", "P2", "C-FRUIT", 0);
        await AddItemAsync("S01", "P3", "C-SNACK", 0);
        await AddItemAsync("S02", "P5", "C-OTHER-STORE", 0);

        var snapshot = await service.GetSnapshotAsync("S01", CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(new[] { "C-SNACK", "C-FRUIT" }, snapshot.Categories.Select(x => x.CategoryId).ToArray());
        Assert.Equal("#E65A2F", snapshot.Categories[1].Color);
        Assert.Null(snapshot.Categories[0].Color);
        // 他店分类与归属不出现。
        Assert.DoesNotContain(snapshot.Assignments, x => x.CategoryId == "C-OTHER-STORE");
        Assert.Equal(
            new[] { ("P3", "C-SNACK", 0), ("P2", "C-FRUIT", 0), ("P1", "C-FRUIT", 1) },
            snapshot.Assignments.Select(x => (x.ProductCode, x.CategoryId, x.SortOrder)).ToArray());
    }

    [Fact]
    public async Task Snapshot_breaks_sort_order_ties_by_guid_ascending_not_by_name()
    {
        // 并发创建分类会产生相同 SortOrder：次序必须是 Guid 升序（与主后端一致），与名称无关。
        await AddCategoryAsync("C-B", "S01", "Alpha", 0);
        await AddCategoryAsync("C-A", "S01", "Zulu", 0);
        await AddCategoryAsync("C-C", "S01", "Mike", 0);

        var snapshot = await service.GetSnapshotAsync("S01", CancellationToken.None);

        Assert.Equal(new[] { "C-A", "C-B", "C-C" }, snapshot!.Categories.Select(x => x.CategoryId).ToArray());
    }

    [Fact]
    public async Task Snapshot_revision_is_max_updated_ticks_of_categories_and_items()
    {
        await AddCategoryAsync("C1", "S01", "Fruit", 0, minutes: 5);
        await AddItemAsync("S01", "P1", "C1", 0, minutes: 30);
        await AddItemAsync("S01", "P2", "C1", 1, minutes: 10);

        var snapshot = await service.GetSnapshotAsync("S01", CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(Stamp.AddMinutes(30).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), snapshot.Revision);
    }

    [Fact]
    public async Task Snapshot_excludes_assignments_of_products_that_are_no_longer_special_and_restores_them_on_remark()
    {
        await AddCategoryAsync("C1", "S01", "Fruit", 0);
        await AddItemAsync("S01", "P1", "C1", 0);
        await AddItemAsync("S01", "P2", "C1", 1);
        await AddItemAsync("S01", "P4", "C1", 2); // 不是特殊商品
        await AddItemAsync("S01", "P6", "C1", 3); // 最新门店价行不是特殊
        await AddItemAsync("S01", "P5", "C1", 4); // 只在 S02 是特殊商品

        var before = await service.GetSnapshotAsync("S01", CancellationToken.None);
        Assert.Equal(new[] { "P1", "P2" }, CodesIn(before!, "C1"));

        // 取消 P1 的特殊标记：归属行还在表里，但读取时被求交集挡掉。
        await database.Client.Updateable<StoreRetailPrice>()
            .SetColumns(x => x.IsSpecialProduct == false)
            .Where(x => x.StoreCode == "S01" && x.ProductCode == "P1")
            .ExecuteCommandAsync();
        var unmarked = await service.GetSnapshotAsync("S01", CancellationToken.None);
        Assert.Equal(new[] { "P2" }, CodesIn(unmarked!, "C1"));
        Assert.Equal(5, (await ItemRowsAsync("S01")).Count);

        // 重新标记后回到原分类（原序号保留）。
        await database.Client.Updateable<StoreRetailPrice>()
            .SetColumns(x => x.IsSpecialProduct == true)
            .Where(x => x.StoreCode == "S01" && x.ProductCode == "P1")
            .ExecuteCommandAsync();
        var remarked = await service.GetSnapshotAsync("S01", CancellationToken.None);
        Assert.Equal(new[] { "P1", "P2" }, CodesIn(remarked!, "C1"));
    }

    [Fact]
    public async Task Snapshot_excludes_assignments_when_the_price_row_is_inactive_or_deleted()
    {
        await AddCategoryAsync("C1", "S01", "Fruit", 0);
        await AddItemAsync("S01", "P1", "C1", 0);
        await AddItemAsync("S01", "P2", "C1", 1);
        await database.Client.Updateable<StoreRetailPrice>()
            .SetColumns(x => x.IsActive == false)
            .Where(x => x.StoreCode == "S01" && x.ProductCode == "P1")
            .ExecuteCommandAsync();
        await database.Client.Updateable<StoreRetailPrice>()
            .SetColumns(x => x.IsDeleted == true)
            .Where(x => x.StoreCode == "S01" && x.ProductCode == "P2")
            .ExecuteCommandAsync();

        var snapshot = await service.GetSnapshotAsync("S01", CancellationToken.None);

        Assert.Single(snapshot!.Categories);
        Assert.Empty(snapshot.Assignments);
    }

    [Fact]
    public async Task Snapshot_reads_live_data_without_any_cache()
    {
        await AddCategoryAsync("C1", "S01", "Fruit", 0);
        var first = await service.GetSnapshotAsync("S01", CancellationToken.None);
        Assert.Empty(first!.Assignments);

        // 绕开服务直接改库，下一次读取必须立刻可见。
        await AddItemAsync("S01", "P1", "C1", 0);
        var second = await service.GetSnapshotAsync("S01", CancellationToken.None);
        Assert.Equal(new[] { "P1" }, CodesIn(second!, "C1"));
    }

    // ---------------------------------------------------------------- assign

    [Fact]
    public async Task Assign_moves_product_into_category_appended_after_current_special_items()
    {
        await AddCategoryAsync("C1", "S01", "Fruit", 0);
        await AddItemAsync("S01", "P1", "C1", 0);
        await AddItemAsync("S01", "P4", "C1", 7); // 已不是特殊商品，不应顶高「最大序号」

        var result = await service.AssignAsync(
            new CatalogSpecialProductCategoryAssignRequest("S01", "P2", "C1"), "POS-01", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new[] { "P1", "P2" }, CodesIn(result.Response!, "C1"));
        var p2 = result.Response!.Assignments.Single(x => x.ProductCode == "P2");
        Assert.Equal(1, p2.SortOrder);
        // 审计列：新建行记录设备标识，IsDeleted 恒为 false。
        var row = (await ItemRowsAsync("S01")).Single(x => x.ProductCode == "P2");
        Assert.Equal("POS-01", row.CreatedBy);
        Assert.Equal("POS-01", row.UpdatedBy);
        Assert.False(row.IsDeleted);
    }

    [Fact]
    public async Task Assign_into_empty_category_starts_at_zero()
    {
        await AddCategoryAsync("C1", "S01", "Fruit", 0);

        var result = await service.AssignAsync(
            new CatalogSpecialProductCategoryAssignRequest("S01", "P3", "C1"), "POS-01", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, result.Response!.Assignments.Single().SortOrder);
    }

    [Fact]
    public async Task Assign_moves_product_between_categories_and_appends_at_the_end_of_the_target()
    {
        await AddCategoryAsync("C1", "S01", "Fruit", 0);
        await AddCategoryAsync("C2", "S01", "Snack", 1);
        await AddItemAsync("S01", "P1", "C1", 0);
        await AddItemAsync("S01", "P2", "C2", 0);
        await AddItemAsync("S01", "P3", "C2", 1);

        var result = await service.AssignAsync(
            new CatalogSpecialProductCategoryAssignRequest("S01", "P1", "C2"), "POS-01", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(CodesIn(result.Response!, "C1"));
        Assert.Equal(new[] { "P2", "P3", "P1" }, CodesIn(result.Response!, "C2"));
        var row = (await ItemRowsAsync("S01")).Single(x => x.ProductCode == "P1");
        Assert.Equal("C2", row.CategoryGuid);
        Assert.Equal("POS-01", row.UpdatedBy);
    }

    [Fact]
    public async Task Assign_to_the_same_category_is_a_no_op_that_keeps_position()
    {
        await AddCategoryAsync("C1", "S01", "Fruit", 0);
        await AddItemAsync("S01", "P1", "C1", 0);
        await AddItemAsync("S01", "P2", "C1", 1);

        var result = await service.AssignAsync(
            new CatalogSpecialProductCategoryAssignRequest("S01", "P1", "C1"), "POS-01", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new[] { "P1", "P2" }, CodesIn(result.Response!, "C1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Assign_with_empty_category_moves_product_out_to_uncategorized(string? categoryId)
    {
        await AddCategoryAsync("C1", "S01", "Fruit", 0);
        await AddItemAsync("S01", "P1", "C1", 0);
        await AddItemAsync("S01", "P2", "C1", 1);

        var result = await service.AssignAsync(
            new CatalogSpecialProductCategoryAssignRequest("S01", "P1", categoryId), "POS-01", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new[] { "P2" }, CodesIn(result.Response!, "C1"));
        Assert.DoesNotContain(await ItemRowsAsync("S01"), x => x.ProductCode == "P1");

        // 本来就是未分类的特殊商品再移出：幂等成功。
        var again = await service.AssignAsync(
            new CatalogSpecialProductCategoryAssignRequest("S01", "P3", categoryId), "POS-01", CancellationToken.None);
        Assert.True(again.Success);
    }

    [Theory]
    [InlineData("P4")] // 门店有价但不是特殊商品
    [InlineData("P5")] // 只在别的分店是特殊商品
    [InlineData("P6")] // 最新门店价行已取消特殊
    [InlineData("NOPE")] // 本店没有这个商品
    public async Task Assign_rejects_products_that_are_not_special_in_this_store(string productCode)
    {
        await AddCategoryAsync("C1", "S01", "Fruit", 0);

        var result = await service.AssignAsync(
            new CatalogSpecialProductCategoryAssignRequest("S01", productCode, "C1"), "POS-01", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("SPECIAL_PRODUCT_NOT_SPECIAL", result.ErrorCode);
        Assert.Empty(await ItemRowsAsync("S01"));
    }

    [Fact]
    public async Task Assign_rejects_move_out_of_a_product_that_is_no_longer_special()
    {
        await AddCategoryAsync("C1", "S01", "Fruit", 0);
        await AddItemAsync("S01", "P4", "C1", 0);

        var result = await service.AssignAsync(
            new CatalogSpecialProductCategoryAssignRequest("S01", "P4", null), "POS-01", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("SPECIAL_PRODUCT_NOT_SPECIAL", result.ErrorCode);
        Assert.Single(await ItemRowsAsync("S01"));
    }

    [Fact]
    public async Task Assign_rejects_category_of_another_store_or_unknown_category()
    {
        await AddCategoryAsync("C-S02", "S02", "Other", 0);

        var otherStore = await service.AssignAsync(
            new CatalogSpecialProductCategoryAssignRequest("S01", "P1", "C-S02"), "POS-01", CancellationToken.None);
        var unknown = await service.AssignAsync(
            new CatalogSpecialProductCategoryAssignRequest("S01", "P1", "NOPE"), "POS-01", CancellationToken.None);

        Assert.False(otherStore.Success);
        Assert.Equal("SPECIAL_PRODUCT_CATEGORY_NOT_FOUND", otherStore.ErrorCode);
        Assert.False(unknown.Success);
        Assert.Equal("SPECIAL_PRODUCT_CATEGORY_NOT_FOUND", unknown.ErrorCode);
        Assert.Empty(await ItemRowsAsync("S01"));
        Assert.Empty(await ItemRowsAsync("S02"));
    }

    [Fact]
    public async Task Assign_rejects_missing_inactive_store_and_blank_codes()
    {
        var missing = await service.AssignAsync(
            new CatalogSpecialProductCategoryAssignRequest("NOPE", "P1", null), "POS-01", CancellationToken.None);
        var inactive = await service.AssignAsync(
            new CatalogSpecialProductCategoryAssignRequest("S03", "P1", null), "POS-01", CancellationToken.None);
        var noStore = await service.AssignAsync(
            new CatalogSpecialProductCategoryAssignRequest(" ", "P1", null), "POS-01", CancellationToken.None);
        var noProduct = await service.AssignAsync(
            new CatalogSpecialProductCategoryAssignRequest("S01", " ", null), "POS-01", CancellationToken.None);

        Assert.Equal("STORE_NOT_FOUND", missing.ErrorCode);
        Assert.Equal("STORE_NOT_FOUND", inactive.ErrorCode);
        Assert.Equal("STORE_CODE_REQUIRED", noStore.ErrorCode);
        Assert.Equal("PRODUCT_CODE_REQUIRED", noProduct.ErrorCode);
    }

    [Fact]
    public async Task Assign_matches_product_code_case_insensitively_and_stores_canonical_code()
    {
        await AddCategoryAsync("C1", "S01", "Fruit", 0);

        var result = await service.AssignAsync(
            new CatalogSpecialProductCategoryAssignRequest("S01", " p1 ", "C1"), "POS-01", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("P1", result.Response!.Assignments.Single().ProductCode);
    }

    [Fact]
    public async Task Assign_falls_back_to_default_actor_when_none_given()
    {
        await AddCategoryAsync("C1", "S01", "Fruit", 0);

        await service.AssignAsync(
            new CatalogSpecialProductCategoryAssignRequest("S01", "P1", "C1"), " ", CancellationToken.None);

        Assert.Equal("pos-device", (await ItemRowsAsync("S01")).Single().UpdatedBy);
    }

    [Fact]
    public async Task Assign_retries_once_when_a_concurrent_insert_wins_the_primary_key_race()
    {
        await AddCategoryAsync("C1", "S01", "Fruit", 0);
        // 模拟另一台收银机抢先写入：在本次 INSERT 之前先插入同一主键，让 INSERT 真实撞 PK（仅第一次尝试）。
        service.BeforeInsertForTesting = attempt => attempt == 0
            ? database.Client.Insertable(new StoreSpecialProductCategoryItem
            {
                StoreCode = "S01",
                ProductCode = "P1",
                CategoryGuid = "C1",
                SortOrder = 0
            }).ExecuteCommandAsync()
            : Task.CompletedTask;

        var result = await service.AssignAsync(
            new CatalogSpecialProductCategoryAssignRequest("S01", "P1", "C1"), "POS-01", CancellationToken.None);

        // 第一次尝试整体回滚，第二次重读后正常写入，最终只有一行。
        Assert.True(result.Success);
        Assert.Equal(new[] { "P1" }, CodesIn(result.Response!, "C1"));
        Assert.Single(await ItemRowsAsync("S01"));
    }

    [Fact]
    public async Task Assign_returns_assign_conflict_when_the_primary_key_race_persists()
    {
        await AddCategoryAsync("C1", "S01", "Fruit", 0);
        var attempts = new List<int>();
        service.BeforeInsertForTesting = attempt =>
        {
            attempts.Add(attempt);
            return database.Client.Insertable(new StoreSpecialProductCategoryItem
            {
                StoreCode = "S01",
                ProductCode = "P1",
                CategoryGuid = "C1",
                SortOrder = 0
            }).ExecuteCommandAsync();
        };

        var result = await service.AssignAsync(
            new CatalogSpecialProductCategoryAssignRequest("S01", "P1", "C1"), "POS-01", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("SPECIAL_PRODUCT_CATEGORY_ASSIGN_CONFLICT", result.ErrorCode);
        Assert.Equal(new[] { 0, 1 }, attempts);
        Assert.Empty(await ItemRowsAsync("S01"));
    }

    [Fact]
    public async Task Assign_does_not_swallow_unrelated_exceptions()
    {
        await AddCategoryAsync("C1", "S01", "Fruit", 0);
        service.BeforeInsertForTesting = _ => throw new InvalidOperationException("boom");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AssignAsync(
            new CatalogSpecialProductCategoryAssignRequest("S01", "P1", "C1"), "POS-01", CancellationToken.None));
        Assert.Empty(await ItemRowsAsync("S01"));
    }

    // ---------------------------------------------------------------- order

    private async Task SeedThreeInCategoryAsync()
    {
        await AddCategoryAsync("C1", "S01", "Fruit", 0);
        await AddItemAsync("S01", "P1", "C1", 0);
        await AddItemAsync("S01", "P2", "C1", 1);
        await AddItemAsync("S01", "P3", "C1", 2);
    }

    [Fact]
    public async Task Order_rewrites_sort_order_as_contiguous_sequence_and_returns_snapshot()
    {
        await SeedThreeInCategoryAsync();

        var result = await service.ReorderAsync(
            new CatalogSpecialProductCategoryOrderRequest("S01", "C1", ["P1", "P3", "P2"]), "POS-01", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new[] { "P1", "P3", "P2" }, CodesIn(result.Response!, "C1"));
        Assert.Equal(new[] { 0, 1, 2 }, result.Response!.Assignments.Select(x => x.SortOrder).ToArray());
        // 只有序号变了的行才写审计列。
        var rows = await ItemRowsAsync("S01");
        Assert.Equal("POS-01", rows.Single(x => x.ProductCode == "P3").UpdatedBy);
        Assert.Equal("POS-01", rows.Single(x => x.ProductCode == "P2").UpdatedBy);
        Assert.Null(rows.Single(x => x.ProductCode == "P1").UpdatedBy);
    }

    [Fact]
    public async Task Order_normalizes_gapped_sort_numbers_into_zero_based_sequence()
    {
        await AddCategoryAsync("C1", "S01", "Fruit", 0);
        await AddItemAsync("S01", "P1", "C1", 5);
        await AddItemAsync("S01", "P2", "C1", 9);

        var result = await service.ReorderAsync(
            new CatalogSpecialProductCategoryOrderRequest("S01", "C1", ["P1", "P2"]), "POS-01", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new[] { 0, 1 }, result.Response!.Assignments.Select(x => x.SortOrder).ToArray());
    }

    [Fact]
    public async Task Order_ignores_products_that_are_no_longer_special_and_matches_case_insensitively()
    {
        await SeedThreeInCategoryAsync();
        await AddItemAsync("S01", "P4", "C1", 3); // 非特殊商品：既不要求出现、也不被改动

        var result = await service.ReorderAsync(
            new CatalogSpecialProductCategoryOrderRequest("S01", "C1", ["p2", " P1 ", "P3"]), "POS-01", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new[] { "P2", "P1", "P3" }, CodesIn(result.Response!, "C1"));
        Assert.Equal(3, (await ItemRowsAsync("S01")).Single(x => x.ProductCode == "P4").SortOrder);
    }

    public static TheoryData<string[]> ConflictingOrders() => new()
    {
        new[] { "P1", "P2" },                 // 缺一个
        new[] { "P1", "P2", "P3", "P5" },     // 多一个（其他店 / 不在分类里）
        new[] { "P1", "P2", "P2" },           // 重复（长度相同但不是排列）
        new[] { "P1", "P2", "P2", "P3" },     // 重复且变长
        new[] { "P1", "P2", "P4" },           // 换成了分类外的商品
        Array.Empty<string>(),                // 空列表但分类里有商品
    };

    [Theory]
    [MemberData(nameof(ConflictingOrders))]
    public async Task Order_rejects_lists_that_are_not_a_permutation_of_the_current_products(string[] productCodes)
    {
        await SeedThreeInCategoryAsync();

        var result = await service.ReorderAsync(
            new CatalogSpecialProductCategoryOrderRequest("S01", "C1", productCodes), "POS-01", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("SPECIAL_PRODUCT_CATEGORY_ORDER_CONFLICT", result.ErrorCode);
        // 冲突时一个字都不改。
        var rows = (await ItemRowsAsync("S01")).OrderBy(x => x.SortOrder).ToList();
        Assert.Equal(new[] { "P1", "P2", "P3" }, rows.Select(x => x.ProductCode).ToArray());
        Assert.All(rows, x => Assert.Null(x.UpdatedBy));
    }

    [Fact]
    public async Task Order_conflicts_when_a_listed_product_stopped_being_special_meanwhile()
    {
        await SeedThreeInCategoryAsync();
        await database.Client.Updateable<StoreRetailPrice>()
            .SetColumns(x => x.IsSpecialProduct == false)
            .Where(x => x.StoreCode == "S01" && x.ProductCode == "P2")
            .ExecuteCommandAsync();

        var result = await service.ReorderAsync(
            new CatalogSpecialProductCategoryOrderRequest("S01", "C1", ["P1", "P2", "P3"]), "POS-01", CancellationToken.None);

        Assert.Equal("SPECIAL_PRODUCT_CATEGORY_ORDER_CONFLICT", result.ErrorCode);
    }

    [Fact]
    public async Task Order_accepts_empty_list_for_a_category_without_special_products()
    {
        await AddCategoryAsync("C1", "S01", "Fruit", 0);

        var result = await service.ReorderAsync(
            new CatalogSpecialProductCategoryOrderRequest("S01", "C1", []), "POS-01", CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Order_rejects_category_of_another_store_unknown_category_and_missing_store()
    {
        await AddCategoryAsync("C-S02", "S02", "Other", 0);
        await AddItemAsync("S02", "P5", "C-S02", 0);

        var otherStore = await service.ReorderAsync(
            new CatalogSpecialProductCategoryOrderRequest("S01", "C-S02", ["P5"]), "POS-01", CancellationToken.None);
        var unknown = await service.ReorderAsync(
            new CatalogSpecialProductCategoryOrderRequest("S01", "NOPE", []), "POS-01", CancellationToken.None);
        var noStore = await service.ReorderAsync(
            new CatalogSpecialProductCategoryOrderRequest("NOPE", "C-S02", []), "POS-01", CancellationToken.None);
        var noCategory = await service.ReorderAsync(
            new CatalogSpecialProductCategoryOrderRequest("S01", " ", []), "POS-01", CancellationToken.None);

        Assert.Equal("SPECIAL_PRODUCT_CATEGORY_NOT_FOUND", otherStore.ErrorCode);
        Assert.Equal("SPECIAL_PRODUCT_CATEGORY_NOT_FOUND", unknown.ErrorCode);
        Assert.Equal("STORE_NOT_FOUND", noStore.ErrorCode);
        Assert.Equal("CATEGORY_ID_REQUIRED", noCategory.ErrorCode);
    }
}
