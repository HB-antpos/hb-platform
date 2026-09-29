using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 货柜明细「更新已有商品」的分店多码写入：必须按子码逐个展开，子码零售价取子码自己的价格，
/// 不能按「分店+父商品」只改最近一行、把父商品零售价写到子码上，也不能给无子码商品插入空子码行。
/// </summary>
public sealed class StoreMultiCodeUpsertForActiveStoresTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public StoreMultiCodeUpsertForActiveStoresTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(
            typeof(Store),
            typeof(Product),
            typeof(WarehouseProduct),
            typeof(ProductSetCode),
            typeof(StoreRetailPrice),
            typeof(StoreMultiCodeProduct)
        );
    }

    [Fact]
    public async Task 同步零售价时每个分店的每个子码都改为子码自己的零售价()
    {
        await SeedStoresAsync("S01", "S02");
        await SeedProductAsync("P-MULTI", setType: 2, ("P-MULTI-A", 8m), ("P-MULTI-B", 9m));
        // 分店价已漂移：S01 的 A、S02 的 B 与子码价不一致。
        await SeedStoreChildRowAsync("S01", "P-MULTI", "P-MULTI-A", retail: 7m);
        await SeedStoreChildRowAsync("S01", "P-MULTI", "P-MULTI-B", retail: 9m);
        await SeedStoreChildRowAsync("S02", "P-MULTI", "P-MULTI-A", retail: 8m);
        await SeedStoreChildRowAsync("S02", "P-MULTI", "P-MULTI-B", retail: 10m);

        var result = await CreateService().UpsertForActiveStoresAsync(
            new List<StoreMultiCodePriceUpsertForActiveStoresItemDto>
            {
                new() { ProductCode = "P-MULTI", PurchasePrice = 5m, MultiCodeRetailPrice = 12.5m },
            },
            "tester"
        );

        Assert.True(result.Success, result.Message);
        var rows = await StoreChildRowsAsync("P-MULTI");
        Assert.Equal(4, rows.Count);
        Assert.All(rows, row => Assert.NotNull(row.MultiCodeProductCode));
        Assert.All(rows.Where(r => r.MultiCodeProductCode == "P-MULTI-A"), r => Assert.Equal(8m, r.MultiCodeRetailPrice));
        Assert.All(rows.Where(r => r.MultiCodeProductCode == "P-MULTI-B"), r => Assert.Equal(9m, r.MultiCodeRetailPrice));
        Assert.DoesNotContain(rows, r => r.MultiCodeRetailPrice == 12.5m);
    }

    [Fact]
    public async Task 无子码的普通商品不插入分店多码行且请求成功()
    {
        await SeedStoresAsync("S01", "S02");
        await SeedProductAsync("P-PLAIN", setType: null);

        var result = await CreateService().UpsertForActiveStoresAsync(
            new List<StoreMultiCodePriceUpsertForActiveStoresItemDto>
            {
                new() { ProductCode = "P-PLAIN", PurchasePrice = 5m, MultiCodeRetailPrice = 10.99m },
            },
            "tester"
        );

        Assert.True(result.Success, result.Message);
        Assert.Empty(await _db.Queryable<StoreMultiCodeProduct>().ToListAsync());
    }

    [Fact]
    public async Task 分店缺少子码行时按子码补齐且零售价取子码自己的价格()
    {
        await SeedStoresAsync("S01", "S02");
        await SeedProductAsync("P-SET", setType: 1, ("P-SET-A", 6m), ("P-SET-B", 7m));
        await SeedStoreChildRowAsync("S01", "P-SET", "P-SET-A", retail: 6m);
        await SeedStoreChildRowAsync("S01", "P-SET", "P-SET-B", retail: 7m);
        await SeedStoreChildRowAsync("S02", "P-SET", "P-SET-A", retail: 6m);

        // 只勾选分店多码进货价：已存在的子码行零售价不动，缺失的行按子码价补齐。
        var result = await CreateService().UpsertForActiveStoresAsync(
            new List<StoreMultiCodePriceUpsertForActiveStoresItemDto>
            {
                new() { ProductCode = "P-SET", PurchasePrice = 5m },
            },
            "tester"
        );

        Assert.True(result.Success, result.Message);
        var added = Assert.Single(await _db.Queryable<StoreMultiCodeProduct>()
            .Where(x => x.StoreCode == "S02" && x.MultiCodeProductCode == "P-SET-B")
            .ToListAsync());
        Assert.Equal(7m, added.MultiCodeRetailPrice);
        Assert.Equal("S02P-SET-B", added.StoreMultiCodeProductCode);
        Assert.Equal(4, (await StoreChildRowsAsync("P-SET")).Count);
    }

    private async Task SeedStoresAsync(params string[] codes)
    {
        await _db.Insertable(codes.Select(code => new Store
        {
            StoreCode = code,
            StoreName = code,
            IsActive = true,
            IsDeleted = false,
        }).ToList()).ExecuteCommandAsync();
    }

    private async Task SeedProductAsync(string productCode, int? setType, params (string ChildCode, decimal Retail)[] children)
    {
        await _db.Insertable(new Product
        {
            UUID = $"UUID-{productCode}",
            ProductCode = productCode,
            ProductName = productCode,
            ProductType = setType ?? 0,
            PurchasePrice = 4m,
            RetailPrice = 12.5m,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _db.Insertable(new WarehouseProduct
        {
            ProductCode = productCode,
            ImportPrice = 4m,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        var stores = await _db.Queryable<Store>().Select(s => s.StoreCode).ToListAsync();
        await _db.Insertable(stores.Select(store => new StoreRetailPrice
        {
            UUID = $"SRP-{store}-{productCode}",
            StoreCode = store,
            ProductCode = productCode,
            StoreProductCode = store + productCode,
            PurchasePrice = 4m,
            StoreRetailPriceValue = 12.5m,
            IsActive = true,
            IsDeleted = false,
        }).ToList()).ExecuteCommandAsync();
        if (setType == null || children.Length == 0)
        {
            return;
        }

        await _db.Insertable(children.Select(child => new ProductSetCode
        {
            SetCodeId = child.ChildCode,
            ProductCode = productCode,
            SetProductCode = child.ChildCode,
            SetItemNumber = child.ChildCode,
            SetRetailPrice = child.Retail,
            SetQuantity = 1,
            SetType = setType.Value,
            IsActive = true,
            IsDeleted = false,
        }).ToList()).ExecuteCommandAsync();
    }

    private async Task SeedStoreChildRowAsync(string storeCode, string productCode, string childCode, decimal retail)
    {
        await _db.Insertable(new StoreMultiCodeProduct
        {
            UUID = $"SMC-{storeCode}-{childCode}",
            StoreCode = storeCode,
            ProductCode = productCode,
            MultiCodeProductCode = childCode,
            StoreMultiCodeProductCode = storeCode + childCode,
            PurchasePrice = 4m,
            MultiCodeRetailPrice = retail,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private Task<List<StoreMultiCodeProduct>> StoreChildRowsAsync(string productCode) =>
        _db.Queryable<StoreMultiCodeProduct>().Where(x => x.ProductCode == productCode && !x.IsDeleted).ToListAsync();

    private StoreMultiCodePricesReactService CreateService()
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, _db);
        return new StoreMultiCodePricesReactService(context, NullLogger<StoreMultiCodePricesReactService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }
}
