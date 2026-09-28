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
/// 分店商品价格网格的多分店查询：每个商品在每个所选分店各占一行，按商品换算分页。
/// </summary>
public sealed class StoreProductPriceMultiStoreGridTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public StoreProductPriceMultiStoreGridTests()
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

    [Fact]
    public async Task 多分店_每个商品在每个所选分店各占一行_缺价格的分店也占一行()
    {
        await SeedSupplierAsync("SUP1", "一号供应商");
        await SeedProductAsync("P1", "A-001", "SUP1");
        await SeedProductAsync("P2", "A-002", "SUP1");
        await SeedPriceAsync("S02", "P1", 2.5m, 5.99m, isAutoPricing: true);
        await SeedPriceAsync("S01", "P1", 2.0m, 4.99m);
        await SeedPriceAsync("S01", "P2", 1.0m, 1.99m);
        // 未选中的分店与已删除的价格记录都不应出现
        await SeedPriceAsync("S03", "P1", 9m, 9m);
        await SeedPriceAsync("S02", "P2", 8m, 8m, isDeleted: true);

        var result = await CreateService().GetGridDataAsync(new StoreProductPriceQueryDto
        {
            // 顺序、空白与重复不影响结果：分店按编码排序并去重
            StoreCodes = new List<string> { "S02", " S01 ", "S01" },
            SortBy = "itemNumber",
            SortOrder = "asc",
            PageNumber = 1,
            PageSize = 50,
        });

        Assert.True(result.Success, result.Message);
        Assert.Equal(4, result.Total);
        var rows = result.Items!;
        Assert.Equal(
            new[] { "P1@S01", "P1@S02", "P2@S01", "P2@S02" },
            rows.Select(r => $"{r.ProductCode}@{r.StoreCode}")
        );

        Assert.Equal(4.99m, rows[0].StoreRetailPrice);
        Assert.Equal(5.99m, rows[1].StoreRetailPrice);
        Assert.True(rows[1].IsStoreAutoPricing);
        Assert.Equal("一号供应商", rows[1].LocalSupplierName);
        Assert.Equal(1.99m, rows[2].StoreRetailPrice);

        // P2 在 S02 只有已删除记录：行仍在，价格为空
        Assert.Null(rows[3].StoreRetailPrice);
        Assert.Null(rows[3].StorePurchasePrice);
        Assert.False(rows[3].IsStoreAutoPricing);
        Assert.Equal("A-002", rows[3].ItemNumber);
    }

    [Fact]
    public async Task 多分店_分页从商品中间切开时只返回本页的行_总数为商品数乘分店数()
    {
        for (var i = 1; i <= 5; i++)
        {
            await SeedProductAsync($"P{i}", $"A-00{i}");
            await SeedPriceAsync("S01", $"P{i}", i, i * 10m);
            await SeedPriceAsync("S02", $"P{i}", i, i * 10m + 1);
            await SeedPriceAsync("S03", $"P{i}", i, i * 10m + 2);
        }

        var service = CreateService();
        StoreProductPriceQueryDto Query(int page) => new()
        {
            StoreCodes = new List<string> { "S01", "S02", "S03" },
            SortBy = "itemNumber",
            SortOrder = "asc",
            PageNumber = page,
            PageSize = 4,
        };

        var page2 = await service.GetGridDataAsync(Query(2));
        Assert.True(page2.Success, page2.Message);
        Assert.Equal(15, page2.Total);
        // 第 2 页是第 5–8 行：P2 的 S02、S03 与 P3 的 S01、S02
        Assert.Equal(
            new[] { "P2@S02", "P2@S03", "P3@S01", "P3@S02" },
            page2.Items!.Select(r => $"{r.ProductCode}@{r.StoreCode}")
        );
        Assert.Equal(new decimal?[] { 21m, 22m, 30m, 31m }, page2.Items!.Select(r => r.StoreRetailPrice));

        // 逐页拼起来正好覆盖全部「商品 × 分店」且不重复
        var allKeys = new List<string>();
        for (var page = 1; page <= 4; page++)
        {
            var pageResult = await service.GetGridDataAsync(Query(page));
            allKeys.AddRange(pageResult.Items!.Select(r => $"{r.ProductCode}@{r.StoreCode}"));
        }

        Assert.Equal(15, allKeys.Count);
        Assert.Equal(15, allKeys.Distinct().Count());

        var beyond = await service.GetGridDataAsync(Query(5));
        Assert.True(beyond.Success);
        Assert.Empty(beyond.Items!);
        Assert.Equal(15, beyond.Total);
    }

    [Fact]
    public async Task 多分店_排序值相同时按商品编码稳定分页()
    {
        // 默认按商品更新时间倒序；更新时间相同的商品必须有稳定次序，否则按商品换算的分页会重复或遗漏
        var sameTime = new DateTime(2026, 9, 1, 8, 0, 0);
        foreach (var code in new[] { "P3", "P1", "P2" })
        {
            await SeedProductAsync(code, code, updatedAt: sameTime);
        }

        var service = CreateService();
        var keys = new List<string>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await service.GetGridDataAsync(new StoreProductPriceQueryDto
            {
                StoreCodes = new List<string> { "S01", "S02" },
                PageNumber = page,
                PageSize = 2,
            });
            keys.AddRange(result.Items!.Select(r => $"{r.ProductCode}@{r.StoreCode}"));
        }

        Assert.Equal(
            new[] { "P1@S01", "P1@S02", "P2@S01", "P2@S02", "P3@S01", "P3@S02" },
            keys
        );
    }

    [Fact]
    public async Task 只选一个分店时与单店查询结果一致()
    {
        await SeedProductAsync("P1", "A-001");
        await SeedProductAsync("P2", "A-002");
        await SeedPriceAsync("S01", "P1", 2.0m, 4.99m);

        var service = CreateService();
        var viaList = await service.GetGridDataAsync(new StoreProductPriceQueryDto
        {
            StoreCodes = new List<string> { "S01" },
            SortBy = "itemNumber",
            SortOrder = "asc",
            PageNumber = 1,
            PageSize = 50,
        });
        var viaSingle = await service.GetGridDataAsync(new StoreProductPriceQueryDto
        {
            StoreCode = "S01",
            SortBy = "itemNumber",
            SortOrder = "asc",
            PageNumber = 1,
            PageSize = 50,
        });

        Assert.True(viaList.Success, viaList.Message);
        Assert.Equal(2, viaList.Total);
        Assert.Equal(
            viaSingle.Items!.Select(r => $"{r.ProductCode}:{r.StoreCode}:{r.StoreRetailPrice}"),
            viaList.Items!.Select(r => $"{r.ProductCode}:{r.StoreCode}:{r.StoreRetailPrice}")
        );
        Assert.Equal(4.99m, viaList.Items![0].StoreRetailPrice);
    }

    [Fact]
    public async Task 多分店_不支持价格区间筛选()
    {
        await SeedProductAsync("P1", "A-001");

        var result = await CreateService().GetGridDataAsync(new StoreProductPriceQueryDto
        {
            StoreCodes = new List<string> { "S01", "S02" },
            RetailPriceGt = 1m,
            PageNumber = 1,
            PageSize = 50,
        });

        Assert.False(result.Success);
        Assert.Contains("价格区间", result.Message);
    }

    [Fact]
    public void 合并单选与多选分店_去空白去重并排序()
    {
        var codes = StoreProductPriceReactService.ResolveGridStoreCodes(new StoreProductPriceQueryDto
        {
            StoreCode = "S03",
            StoreCodes = new List<string> { " ", "s03", "S01", "S01 " },
        });

        Assert.Equal(new[] { "S01", "S03" }, codes);
        Assert.Empty(StoreProductPriceReactService.ResolveGridStoreCodes(new StoreProductPriceQueryDto()));
    }

    private StoreProductPriceReactService CreateService() =>
        new(CreateSqlSugarContext(_db), NullLogger<StoreProductPriceReactService>.Instance);

    private async Task SeedSupplierAsync(string code, string name)
    {
        await _db.Insertable(new HBLocalSupplier
        {
            Guid = Guid.NewGuid().ToString("N"),
            LocalSupplierCode = code,
            Name = name,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task SeedProductAsync(
        string productCode,
        string itemNumber,
        string? supplierCode = null,
        DateTime? updatedAt = null
    )
    {
        await _db.Insertable(new Product
        {
            UUID = $"product-{productCode}",
            ProductCode = productCode,
            ProductName = productCode,
            ItemNumber = itemNumber,
            LocalSupplierCode = supplierCode,
            IsActive = true,
            IsDeleted = false,
            UpdatedAt = updatedAt ?? DateTime.Now,
        }).ExecuteCommandAsync();
    }

    private async Task SeedPriceAsync(
        string storeCode,
        string productCode,
        decimal purchasePrice,
        decimal retailPrice,
        bool isAutoPricing = false,
        bool isDeleted = false
    )
    {
        await _db.Insertable(new StoreRetailPrice
        {
            StoreCode = storeCode,
            ProductCode = productCode,
            PurchasePrice = purchasePrice,
            StoreRetailPriceValue = retailPrice,
            IsAutoPricing = isAutoPricing,
            IsDeleted = isDeleted,
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
