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
/// 分店商品价格网格的单店排序：排序键须与 Web 表格列的 dataIndex 对应，排序依据须与列上显示的值一致。
/// </summary>
public sealed class StoreProductPriceGridSortTests : IDisposable
{
    private const string StoreCode = "S01";

    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public StoreProductPriceGridSortTests()
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
        _db.CodeFirst.InitTables(typeof(Product), typeof(StoreRetailPrice), typeof(HBLocalSupplier));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    /// <summary>
    /// 夹具中各排序依据的顺序互不相同，且都不同于默认顺序（商品更新时间倒序 P1、P2、P3、P4），
    /// 所以排序键一旦没被识别而落入默认分支，期望顺序必然对不上。
    /// </summary>
    [Theory]
    [InlineData("storePurchasePrice", "asc", new[] { "P3", "P1", "P4", "P2" })]
    [InlineData("storePurchasePrice", "desc", new[] { "P2", "P4", "P1", "P3" })]
    [InlineData("storeRetailPrice", "asc", new[] { "P2", "P4", "P1", "P3" })]
    [InlineData("storeRetailPrice", "desc", new[] { "P3", "P1", "P4", "P2" })]
    // 旧键保留兼容
    [InlineData("purchasePrice", "asc", new[] { "P3", "P1", "P4", "P2" })]
    [InlineData("retailPrice", "desc", new[] { "P3", "P1", "P4", "P2" })]
    // 按分店价格更新时间，而不是商品主档更新时间（后者升序为 P4、P3、P2、P1）
    [InlineData("updatedAt", "asc", new[] { "P2", "P3", "P1", "P4" })]
    [InlineData("updatedAt", "desc", new[] { "P4", "P1", "P3", "P2" })]
    // 中包数：正确拼写与历史拼写错误的键都可用
    [InlineData("middlePackageQuantity", "asc", new[] { "P4", "P1", "P3", "P2" })]
    [InlineData("middlesackagequantity", "desc", new[] { "P2", "P3", "P1", "P4" })]
    public async Task 单店_排序键按所选分店的价格记录排序(
        string sortBy,
        string sortOrder,
        string[] expectedProductCodes
    )
    {
        await SeedSortFixtureAsync();

        var result = await QueryAsync(sortBy, sortOrder);

        Assert.True(result.Success, result.Message);
        // 其他分店的价格记录不参与连接，总数仍是商品数
        Assert.Equal(4, result.Total);
        Assert.Equal(expectedProductCodes, result.Items!.Select(r => r.ProductCode));
        Assert.All(result.Items!, r => Assert.Equal(StoreCode, r.StoreCode));
    }

    [Fact]
    public async Task 单店_更新时间排序与列上显示的分店价格更新时间一致()
    {
        await SeedSortFixtureAsync();

        var asc = await QueryAsync("updatedAt", "asc");
        var desc = await QueryAsync("updatedAt", "desc");

        var ascTimes = asc.Items!.Select(r => r.UpdatedAt).ToList();
        var descTimes = desc.Items!.Select(r => r.UpdatedAt).ToList();
        Assert.Equal(ascTimes.OrderBy(t => t), ascTimes);
        Assert.Equal(descTimes.OrderByDescending(t => t), descTimes);
        // 显示值来自 S01 的价格记录（8 月），不是商品主档（9 月）
        Assert.Equal(new DateTime(2026, 8, 4, 9, 0, 0), descTimes[0]);
        Assert.Equal(new DateTime(2026, 8, 1, 9, 0, 0), ascTimes[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("localSupplierName")]
    public async Task 单店_未传或不支持的排序键按商品更新时间倒序(string? sortBy)
    {
        await SeedSortFixtureAsync();

        var result = await QueryAsync(sortBy, "asc");

        Assert.True(result.Success, result.Message);
        Assert.Equal(new[] { "P1", "P2", "P3", "P4" }, result.Items!.Select(r => r.ProductCode));
    }

    /// <summary>
    /// 商品主档更新时间：P1 最新 → P4 最早；S01 价格与更新时间各取不同的排列。
    /// S02 的价格顺序与 S01 相反，若误参与排序会打乱结果。
    /// </summary>
    private async Task SeedSortFixtureAsync()
    {
        await SeedProductAsync("P1", new DateTime(2026, 9, 4), middlePackageQuantity: 12);
        await SeedProductAsync("P2", new DateTime(2026, 9, 3), middlePackageQuantity: 48);
        await SeedProductAsync("P3", new DateTime(2026, 9, 2), middlePackageQuantity: 24);
        await SeedProductAsync("P4", new DateTime(2026, 9, 1), middlePackageQuantity: 6);

        await SeedPriceAsync(StoreCode, "P1", 2.0m, 3.99m, new DateTime(2026, 8, 3, 9, 0, 0));
        await SeedPriceAsync(StoreCode, "P2", 4.0m, 1.99m, new DateTime(2026, 8, 1, 9, 0, 0));
        await SeedPriceAsync(StoreCode, "P3", 1.0m, 4.99m, new DateTime(2026, 8, 2, 9, 0, 0));
        await SeedPriceAsync(StoreCode, "P4", 3.0m, 2.99m, new DateTime(2026, 8, 4, 9, 0, 0));

        await SeedPriceAsync("S02", "P1", 3.0m, 2.99m, new DateTime(2026, 7, 2, 9, 0, 0));
        await SeedPriceAsync("S02", "P2", 1.0m, 4.99m, new DateTime(2026, 7, 4, 9, 0, 0));
        await SeedPriceAsync("S02", "P3", 4.0m, 1.99m, new DateTime(2026, 7, 3, 9, 0, 0));
        await SeedPriceAsync("S02", "P4", 2.0m, 3.99m, new DateTime(2026, 7, 1, 9, 0, 0));
    }

    private Task<GridResponseDto<StoreProductPriceListDto>> QueryAsync(string? sortBy, string sortOrder) =>
        CreateService().GetGridDataAsync(new StoreProductPriceQueryDto
        {
            StoreCode = StoreCode,
            SortBy = sortBy,
            SortOrder = sortOrder,
            PageNumber = 1,
            PageSize = 50,
        });

    private StoreProductPriceReactService CreateService() =>
        new(CreateSqlSugarContext(_db), NullLogger<StoreProductPriceReactService>.Instance);

    private async Task SeedProductAsync(string productCode, DateTime updatedAt, int middlePackageQuantity)
    {
        await _db.Insertable(new Product
        {
            UUID = $"product-{productCode}",
            ProductCode = productCode,
            ProductName = productCode,
            ItemNumber = productCode,
            MiddlePackageQuantity = middlePackageQuantity,
            IsActive = true,
            IsDeleted = false,
            UpdatedAt = updatedAt,
        }).ExecuteCommandAsync();
    }

    private async Task SeedPriceAsync(
        string storeCode,
        string productCode,
        decimal purchasePrice,
        decimal retailPrice,
        DateTime updatedAt
    )
    {
        await _db.Insertable(new StoreRetailPrice
        {
            StoreCode = storeCode,
            ProductCode = productCode,
            PurchasePrice = purchasePrice,
            StoreRetailPriceValue = retailPrice,
            UpdatedAt = updatedAt,
            UpdatedBy = "tester",
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        var field = typeof(SqlSugarContext).GetField(
            "_db",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        field!.SetValue(context, db);
        return context;
    }
}
