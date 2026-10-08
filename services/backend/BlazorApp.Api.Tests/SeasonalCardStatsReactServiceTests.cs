using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class SeasonalCardStatsReactServiceTests : IDisposable
{
    private static readonly DateTime BaseTime = new(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _dbPath;
    private readonly SqliteConnection _sqliteConnection;
    private readonly SqlSugarClient _db;

    public SeasonalCardStatsReactServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _sqliteConnection = new SqliteConnection($"Data Source={_dbPath}");
        _sqliteConnection.Open();

        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _sqliteConnection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });

        _db.CodeFirst.InitTables(typeof(Store), typeof(SeasonalCardRemainingSubmission));
    }

    [Fact]
    public async Task GetSummaryAsync_每个供应商取最新一批_多供应商相加_排除测试店与仓库()
    {
        await SeedStoresAsync(("1013", "Orion", true), ("1014", "Kawana", true), ("1006", "HB Warehouse", true),
            ("1042", "TestStore", true), ("1099", "Closed", false));
        // 1013：供应商 A 先报 150/96/40/12，后覆盖为 120/80/36/12；供应商 B 报 10/0/0/0。
        await SeedBatchAsync("1013", "SUP-A", "Supplier A", "a-old", 0, 150, 96, 40, 12);
        await SeedBatchAsync("1013", "SUP-A", "Supplier A", "a-new", 5, 120, 80, 36, 12);
        await SeedBatchAsync("1013", "SUP-B", "Supplier B", "b-1", 1, 10, 0, 0, 0);
        // 仓库与测试店的数据不计入统计。
        await SeedBatchAsync("1006", "SUP-A", "Supplier A", "w-1", 1, 999, 0, 0, 0);
        await SeedBatchAsync("1042", "SUP-A", "Supplier A", "t-1", 1, 999, 0, 0, 0);
        var service = CreateService();

        var result = await service.GetSummaryAsync(new SeasonalCardStatsQueryDto
        {
            SeasonYear = 2026,
            CardType = SeasonalCardType.Christmas,
        });

        Assert.True(result.Success, result.Message);
        var summary = result.Data!;
        Assert.Equal(new[] { "1013", "1014" }, summary.Stores.Select(item => item.StoreCode));
        Assert.Equal(new[] { "1006", "1042" }, summary.ExcludedStores.Select(item => item.StoreCode));
        Assert.Equal(1, summary.FilledStoreCount);
        Assert.Equal(1, summary.UnfilledStoreCount);
        Assert.Equal("1014", Assert.Single(summary.UnfilledStores).StoreCode);

        var orion = summary.Stores.Single(item => item.StoreCode == "1013");
        Assert.True(orion.IsFilled);
        Assert.Equal(130, orion.Prices.Single(item => item.PriceOption == SeasonalCardPriceOptionType.FixedOneDollar).Quantity);
        Assert.Equal(258, orion.TotalQuantity);
        Assert.Equal(130m + 160m + 108m + 54m, orion.TotalAmount);
        Assert.Equal(new[] { "Supplier A", "Supplier B" }, orion.Suppliers.Select(item => item.SupplierName));
        Assert.Equal(BaseTime.AddMinutes(5), orion.LastSubmittedAt);
        Assert.Equal(258, summary.TotalQuantity);

        var supplierA = summary.SupplierTotals.Single(item => item.LocalSupplierCode == "SUP-A");
        Assert.Equal(248, supplierA.Quantity);
        Assert.Equal(1, supplierA.StoreCount);
        Assert.Equal(4, summary.PriceTotals.Count);
    }

    [Fact]
    public async Task GetSummaryAsync_供应商与价格筛选只影响数量_不改变填报状态()
    {
        await SeedStoresAsync(("1013", "Orion", true), ("1014", "Kawana", true));
        await SeedBatchAsync("1013", "SUP-A", "Supplier A", "a-1", 0, 120, 80, 36, 12);
        await SeedBatchAsync("1014", "SUP-B", "Supplier B", "b-1", 0, 10, 20, 0, 0);
        var service = CreateService();

        var result = await service.GetSummaryAsync(new SeasonalCardStatsQueryDto
        {
            SeasonYear = 2026,
            CardType = SeasonalCardType.Christmas,
            LocalSupplierCode = "SUP-A",
            PriceOption = SeasonalCardPriceOptionType.FixedTwoDollars,
        });

        var summary = result.Data!;
        // 1014 只卖供应商 B，按 A 筛选后数量为 0，但仍算已填报。
        Assert.Equal(2, summary.FilledStoreCount);
        Assert.Empty(summary.UnfilledStores);
        var kawana = summary.Stores.Single(item => item.StoreCode == "1014");
        Assert.True(kawana.IsFilled);
        Assert.Equal(0, kawana.TotalQuantity);
        Assert.Equal(80, summary.TotalQuantity);
        Assert.Equal(80, summary.PriceTotals.Single(item => item.PriceOption == SeasonalCardPriceOptionType.FixedTwoDollars).Quantity);
        Assert.Equal(0, summary.PriceTotals.Single(item => item.PriceOption == SeasonalCardPriceOptionType.FixedOneDollar).Quantity);
    }

    [Fact]
    public async Task GetSummaryAsync_批量填报前的历史行按价格各取最新_归入未指定供应商()
    {
        await SeedStoresAsync(("1013", "Orion", true));
        await SeedRowAsync("1013", null, null, null, "c-1", SeasonalCardPriceOptionType.FixedOneDollar, 1m, 30, 0);
        await SeedRowAsync("1013", null, null, null, "c-1", SeasonalCardPriceOptionType.FixedOneDollar, 1m, 25, 2);
        await SeedRowAsync("1013", null, null, null, "c-2", SeasonalCardPriceOptionType.FixedTwoDollars, 2m, 7, 1);
        var service = CreateService();

        var summary = (await service.GetSummaryAsync(new SeasonalCardStatsQueryDto
        {
            SeasonYear = 2026,
            CardType = SeasonalCardType.Christmas,
        })).Data!;

        var orion = Assert.Single(summary.Stores);
        Assert.Equal(32, orion.TotalQuantity);
        var supplier = Assert.Single(summary.SupplierTotals);
        Assert.Null(supplier.LocalSupplierCode);
        Assert.Equal("未指定供应商", supplier.SupplierName);
    }

    [Fact]
    public async Task GetSummaryAsync_配置可覆盖排除名单()
    {
        await SeedStoresAsync(("1006", "HB Warehouse", true), ("1013", "Orion", true));
        var service = CreateService(new Dictionary<string, string?>
        {
            ["SeasonalCards:StatsExcludedStoreCodes:0"] = "1013",
        });

        var summary = (await service.GetSummaryAsync(new SeasonalCardStatsQueryDto
        {
            SeasonYear = 2026,
            CardType = SeasonalCardType.Christmas,
        })).Data!;

        Assert.Equal("1006", Assert.Single(summary.Stores).StoreCode);
        Assert.Equal("1013", Assert.Single(summary.ExcludedStores).StoreCode);
    }

    [Fact]
    public async Task GetStoreDetailAsync_历史标出当前生效批次_带去年同节日合计()
    {
        await SeedStoresAsync(("1013", "Orion", true));
        await SeedBatchAsync("1013", "SUP-A", "Supplier A", "a-old", 0, 150, 96, 40, 12);
        await SeedBatchAsync("1013", "SUP-A", "Supplier A", "a-new", 5, 120, 80, 36, 12);
        await SeedBatchAsync("1013", "SUP-B", "Supplier B", "b-1", 1, 10, 0, 0, 0);
        await SeedBatchAsync("1013", "SUP-A", "Supplier A", "a-2025", -400, 200, 100, 40, 4, seasonYear: 2025);
        var service = CreateService();

        var result = await service.GetStoreDetailAsync("1013", new SeasonalCardStatsStoreDetailQueryDto
        {
            SeasonYear = 2026,
            CardType = SeasonalCardType.Christmas,
        });

        Assert.True(result.Success, result.Message);
        var detail = result.Data!;
        Assert.Equal(258, detail.TotalQuantity);
        Assert.Equal(new[] { "Supplier A", "Supplier B" }, detail.CurrentBatches.Select(item => item.SupplierName));
        Assert.Equal(new[] { "a-new", "b-1", "a-old" }, detail.History.Select(item => item.BatchGuid));
        Assert.Equal(new[] { true, true, false }, detail.History.Select(item => item.IsCurrent));
        Assert.Equal(4, detail.History[0].Lines.Count);
        Assert.Equal(344, detail.PreviousYearTotalQuantity);
    }

    [Fact]
    public async Task GetStoreDetailAsync_分店不存在_返回错误()
    {
        var service = CreateService();

        var result = await service.GetStoreDetailAsync("9999", new SeasonalCardStatsStoreDetailQueryDto
        {
            SeasonYear = 2026,
            CardType = SeasonalCardType.Christmas,
        });

        Assert.Equal("STORE_NOT_FOUND", result.ErrorCode);
    }

    public void Dispose()
    {
        _db.Dispose();
        _sqliteConnection.Dispose();

        if (File.Exists(_dbPath))
        {
            SqliteTempFileCleanup.DeleteIfExists(_dbPath);
        }
    }

    private SeasonalCardStatsReactService CreateService(Dictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();
        return new SeasonalCardStatsReactService(CreateSqlSugarContext(_db), configuration);
    }

    private async Task SeedStoresAsync(params (string Code, string Name, bool IsActive)[] stores)
    {
        await _db.Insertable(stores
            .Select(store => new Store
            {
                StoreGUID = $"store-{store.Code}",
                StoreCode = store.Code,
                StoreName = store.Name,
                IsActive = store.IsActive,
                CreatedAt = DateTime.UtcNow,
            })
            .ToList()).ExecuteCommandAsync();
    }

    private async Task SeedBatchAsync(
        string storeCode,
        string supplierCode,
        string supplierName,
        string batchGuid,
        int minutes,
        int one,
        int two,
        int three,
        int other,
        int seasonYear = 2026
    )
    {
        await SeedRowAsync(storeCode, supplierCode, supplierName, batchGuid, "c-1", SeasonalCardPriceOptionType.FixedOneDollar, 1m, one, minutes, seasonYear);
        await SeedRowAsync(storeCode, supplierCode, supplierName, batchGuid, "c-2", SeasonalCardPriceOptionType.FixedTwoDollars, 2m, two, minutes, seasonYear);
        await SeedRowAsync(storeCode, supplierCode, supplierName, batchGuid, "c-3", SeasonalCardPriceOptionType.FixedThreeDollars, 3m, three, minutes, seasonYear);
        await SeedRowAsync(storeCode, supplierCode, supplierName, batchGuid, "c-other", SeasonalCardPriceOptionType.Other, other == 0 ? 0m : 4.5m, other, minutes, seasonYear);
    }

    private async Task SeedRowAsync(
        string storeCode,
        string? supplierCode,
        string? supplierName,
        string? batchGuid,
        string catalogGuid,
        SeasonalCardPriceOptionType priceOption,
        decimal unitPrice,
        int quantity,
        int minutes,
        int seasonYear = 2026
    )
    {
        await _db.Insertable(new SeasonalCardRemainingSubmission
        {
            SubmissionGuid = Guid.NewGuid().ToString(),
            StoreCode = storeCode,
            CatalogGuid = catalogGuid,
            CardType = SeasonalCardType.Christmas,
            PriceOption = priceOption,
            PriceLabel = priceOption.ToString(),
            UnitPrice = unitPrice,
            SeasonYear = seasonYear,
            RemainingQuantity = quantity,
            SubmittedAt = BaseTime.AddMinutes(minutes),
            SubmittedByUserGuid = "manager-user",
            SubmittedByName = "manager",
            LocalSupplierCode = supplierCode,
            SupplierName = supplierName,
            BatchGuid = batchGuid,
            CreatedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }
}
