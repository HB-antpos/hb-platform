using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.ProductInsights;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>以 SQLite 验证季节商品查询的真实分组、两个独立区间、其他分店口径与候选检索。</summary>
public sealed class SeasonalProductInsightQueryServiceTests : IDisposable
{
    private static readonly (DateTime Start, DateTime End) InboundRange = (new DateTime(2026, 8, 15), new DateTime(2026, 9, 23));
    private static readonly (DateTime Start, DateTime End) SalesRange = (new DateTime(2026, 8, 1), new DateTime(2026, 9, 23));

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public SeasonalProductInsightQueryServiceTests()
    {
        _connection = new SqliteConnection($"Data Source={_path}");
        _connection.Open();
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(
            typeof(Store), typeof(Product),
            typeof(StoreLocalSupplierInvoice), typeof(StoreLocalSupplierInvoiceDetails), typeof(StoreRetailPrice),
            typeof(WareHouseOrder), typeof(WareHouseOrderDetails),
            typeof(ProductStoreDailySalesStatistic)
        );
    }

    [Fact]
    public async Task 本地商品_进货与销量按各自区间统计且其他分店只列区间内有进货的分店()
    {
        await SeedStoresAsync();
        await SeedProductAsync("P1", "SC20458", "9300000000001", "L100");
        await SeedInvoiceAsync("before-range", "S1", new DateTime(2026, 8, 10), ("P1", null, 50m));
        await SeedInvoiceAsync("multi-line", "S1", new DateTime(2026, 8, 20), ("P1", null, 30m), ("P1", null, 5m), ("P2", null, 99m));
        // 明细缺商品编码：按同一分店的零售价 UUID 回退到 P1。
        await _db.Insertable(new StoreRetailPrice { UUID = "srp-s1-p1", StoreCode = "S1", ProductCode = "P1", IsActive = true, IsDeleted = false }).ExecuteCommandAsync();
        await SeedInvoiceAsync("fallback", "S1", new DateTime(2026, 9, 8), (null, "srp-s1-p1", 20m));
        // 跨店的同 UUID 映射不算：S1 的明细引用了 S2 的价目行。
        await _db.Insertable(new StoreRetailPrice { UUID = "srp-s2-p1", StoreCode = "S2", ProductCode = "P1", IsActive = true, IsDeleted = false }).ExecuteCommandAsync();
        await SeedInvoiceAsync("cross-store-fallback", "S1", new DateTime(2026, 9, 12), (null, "srp-s2-p1", 9m));
        await SeedInvoiceAsync("deleted", "S1", new DateTime(2026, 9, 9), ("P1", null, 70m), deleted: true);
        await SeedInvoiceAsync("other-store", "S2", new DateTime(2026, 9, 1), ("P1", null, 40m));
        await SeedSaleAsync("S1", "P1", new DateTime(2026, 7, 31), 100, "L100");
        await SeedSaleAsync("S1", "P1", new DateTime(2026, 8, 5), 7, "L100");
        await SeedSaleAsync("S1", "P1", new DateTime(2026, 9, 10), 3, "L100");
        await SeedSaleAsync("S1", "P1", new DateTime(2026, 9, 10), 3, "L200");
        await SeedSaleAsync("S2", "P1", new DateTime(2026, 9, 2), 12, "L100");
        await SeedSaleAsync("S3", "P1", new DateTime(2026, 9, 3), 4, "L100");

        var result = await CreateService().GetAsync("S1", "P1", InboundRange, SalesRange);

        Assert.NotNull(result);
        Assert.Equal("local", result!.Product.SourceType);
        Assert.Equal("2026-08-15", result.Ranges.Inbound.StartDate);
        Assert.Equal("2026-08-01", result.Ranges.Sales.StartDate);
        Assert.Equal(55m, result.Inbound.Quantity);
        Assert.Equal(2, result.Inbound.DocumentCount);
        Assert.Equal(new[] { "fallback", "multi-line" }, result.Inbound.Records.Select(item => item.Id));
        Assert.Equal(35m, result.Inbound.Records[1].Quantity);
        Assert.Equal(13m, result.Sales.Quantity);
        Assert.Equal(new[] { new DateTime(2026, 8, 5), new DateTime(2026, 9, 10) }, result.Sales.Daily.Select(item => item.Date));
        Assert.Equal(new[] { 7, 6 }, result.Sales.Daily.Select(item => item.Quantity));
        Assert.Equal(42m, result.TheoreticalStock);
        var branch = Assert.Single(result.Branches);
        Assert.Equal("S2", branch.StoreCode);
        Assert.Equal("Store 2", branch.StoreName);
        Assert.Equal(40m, branch.InboundQuantity);
        Assert.Equal(12m, branch.SalesQuantity);
        Assert.Equal(28m, branch.TheoreticalStock);
    }

    [Fact]
    public async Task 仓库商品_只按区间内实际出库配货计进货()
    {
        await SeedStoresAsync();
        await SeedProductAsync("W1", "HB386-011", "9300000000009", "200");
        await SeedWarehouseOrderAsync("shipped", "S1", new DateTime(2026, 9, 1), 2, 10m);
        await SeedWarehouseOrderAsync("pending", "S1", null, 1, 9m);
        await SeedWarehouseOrderAsync("before-range", "S1", new DateTime(2026, 8, 1), 2, 6m);
        await SeedWarehouseOrderAsync("other-store", "S2", new DateTime(2026, 9, 5), 2, 8m);
        await SeedSaleAsync("S1", "W1", new DateTime(2026, 9, 2), 3, "200");

        var result = await CreateService().GetAsync("S1", "W1", InboundRange, SalesRange);

        Assert.NotNull(result);
        Assert.Equal("warehouse", result!.Product.SourceType);
        Assert.Equal(10m, result.Inbound.Quantity);
        Assert.Equal("shipped", Assert.Single(result.Inbound.Records).Id);
        Assert.Equal(7m, result.TheoreticalStock);
        var branch = Assert.Single(result.Branches);
        Assert.Equal(("S2", 8m, 0m, 8m), (branch.StoreCode, branch.InboundQuantity, branch.SalesQuantity, branch.TheoreticalStock));
    }

    [Fact]
    public async Task 商品或分店不存在时返回空()
    {
        await SeedStoresAsync();
        await SeedProductAsync("P1", "SC20458", null, "L100");

        Assert.Null(await CreateService().GetAsync("S1", "MISSING", InboundRange, SalesRange));
        Assert.Null(await CreateService().GetAsync("INACTIVE", "P1", InboundRange, SalesRange));
    }

    [Fact]
    public async Task 检索_条码精确命中时只返回条码商品并附当前分店理论存货()
    {
        await SeedStoresAsync();
        await SeedProductAsync("P1", "SC20458", "9300000000001", "L100");
        await SeedProductAsync("P9", "X9300000000001", "0000", "L100");
        await SeedInvoiceAsync("in", "S1", new DateTime(2026, 9, 1), ("P1", null, 12m));
        await SeedSaleAsync("S1", "P1", new DateTime(2026, 9, 2), 5, "L100");
        await SeedSaleAsync("S2", "P1", new DateTime(2026, 9, 2), 50, "L100");

        var result = await CreateService().LookupAsync("S1", " 9300000000001 ", InboundRange, SalesRange);

        Assert.Equal("barcode", result.MatchMode);
        var item = Assert.Single(result.Items);
        Assert.Equal("P1", item.ProductCode);
        Assert.Equal(7m, item.TheoreticalStock);
        Assert.False(result.Truncated);
    }

    [Theory]
    [InlineData("0810812024527", "810812024527")]
    [InlineData("810812024527", "0810812024527")]
    public async Task 检索_条码前导零写法不同时回退匹配(string stored, string scanned)
    {
        await SeedStoresAsync();
        await SeedProductAsync("P1", "SC20458", stored, "L100");

        var result = await CreateService().LookupAsync("S1", scanned, InboundRange, SalesRange);

        Assert.Equal("barcode", result.MatchMode);
        Assert.Equal("P1", Assert.Single(result.Items).ProductCode);
    }

    [Fact]
    public async Task 检索_条码精确命中优先_不把12位与13位当作等价合并()
    {
        await SeedStoresAsync();
        await SeedProductAsync("P12", "A12", "661799090520", "L100");
        await SeedProductAsync("P13", "A13", "0661799090520", "L100");

        var twelve = await CreateService().LookupAsync("S1", "661799090520", InboundRange, SalesRange);
        var thirteen = await CreateService().LookupAsync("S1", "0661799090520", InboundRange, SalesRange);

        Assert.Equal("P12", Assert.Single(twelve.Items).ProductCode);
        Assert.Equal("P13", Assert.Single(thirteen.Items).ProductCode);
    }

    [Fact]
    public async Task 检索_货号不区分大小写包含匹配且完全相等排最前并排除已删除商品()
    {
        await SeedStoresAsync();
        await SeedProductAsync("P1", "SC20458", null, "L100");
        await SeedProductAsync("P2", "SC20412", null, "L100");
        await SeedProductAsync("P3", "SC204", null, "200");
        await SeedProductAsync("P4", "AB999", null, "L100");
        await SeedProductAsync("P5", "SC20499", null, "L100", deleted: true);
        await SeedWarehouseOrderAsync("wh", "S1", new DateTime(2026, 9, 1), 2, 6m, productCode: "P3");

        var result = await CreateService().LookupAsync("S1", "sc204", InboundRange, SalesRange);

        Assert.Equal("itemNumber", result.MatchMode);
        Assert.Equal(new[] { "P3", "P2", "P1" }, result.Items.Select(item => item.ProductCode));
        Assert.Equal(6m, result.Items[0].TheoreticalStock);
        Assert.Equal(0m, result.Items[1].TheoreticalStock);
    }

    [Fact]
    public async Task 检索_超过上限时截断并标记()
    {
        await SeedStoresAsync();
        for (var index = 0; index < SeasonalProductInsightRules.MaxCandidates + 3; index++)
        {
            await SeedProductAsync($"P{index:D2}", $"HB{index:D2}", null, "L100");
        }

        var result = await CreateService().LookupAsync("S1", "HB", InboundRange, SalesRange);

        Assert.True(result.Truncated);
        Assert.Equal(SeasonalProductInsightRules.MaxCandidates, result.Items.Count);
    }

    [Theory]
    [InlineData("2026-09-23", "2026-08-01")]
    [InlineData("2026-08-01", "2026-08-01")]
    [InlineData("2026-07-31", "2025-08-01")]
    [InlineData("2027-03-01", "2026-08-01")]
    public void 季节起点_默认从最近一个8月1日开始(string today, string expected)
    {
        Assert.Equal(DateTime.Parse(expected), SeasonalProductInsightRules.SeasonStart(DateTime.Parse(today)));
    }

    [Theory]
    [InlineData(null, null, true, "2026-08-01", "2026-09-23")]
    [InlineData("2026-08-15", "2026-09-01", true, "2026-08-15", "2026-09-01")]
    [InlineData("2025-09-23", "2026-09-23", true, "2025-09-23", "2026-09-23")]
    [InlineData("2025-09-22", "2026-09-23", false, null, null)]
    [InlineData("2026-09-02", "2026-09-01", false, null, null)]
    [InlineData("2026-09-01", null, false, null, null)]
    [InlineData("2026/09/01", "2026-09-02", false, null, null)]
    public void 区间解析_缺省用默认区间且拒绝半边倒序与超一年(
        string? start, string? end, bool ok, string? expectedStart, string? expectedEnd)
    {
        var fallback = (new DateTime(2026, 8, 1), new DateTime(2026, 9, 23));

        var success = SeasonalProductInsightRules.TryResolveRange(start, end, fallback, out var range, out var error);

        Assert.Equal(ok, success);
        if (ok)
        {
            Assert.Null(error);
            Assert.Equal(DateTime.Parse(expectedStart!), range.Start);
            Assert.Equal(DateTime.Parse(expectedEnd!), range.End);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }

    private SeasonalProductInsightQueryService CreateService() => new(Context(_db), new ServiceCollection().BuildServiceProvider());

    private async Task SeedStoresAsync()
    {
        await _db.Insertable(new[]
        {
            new Store { StoreGUID = "store-1", StoreCode = "S1", StoreName = "Store 1", IsActive = true, IsDeleted = false },
            new Store { StoreGUID = "store-2", StoreCode = "S2", StoreName = "Store 2", IsActive = true, IsDeleted = false },
            new Store { StoreGUID = "store-3", StoreCode = "S3", StoreName = "Store 3", IsActive = true, IsDeleted = false },
            new Store { StoreGUID = "store-4", StoreCode = "INACTIVE", StoreName = "Closed", IsActive = false, IsDeleted = false },
        }).ExecuteCommandAsync();
    }

    private Task SeedProductAsync(string code, string itemNumber, string? barcode, string supplierCode, bool deleted = false) =>
        _db.Insertable(new Product
        {
            UUID = $"uuid-{code}", ProductCode = code, ProductName = $"Product {code}", ItemNumber = itemNumber, Barcode = barcode,
            LocalSupplierCode = supplierCode, IsActive = true, IsDeleted = deleted,
        }).ExecuteCommandAsync();

    private async Task SeedInvoiceAsync(
        string id, string storeCode, DateTime inboundDate,
        params (string? ProductCode, string? StoreProductCode, decimal Quantity)[] lines)
    {
        await SeedInvoiceAsync(id, storeCode, inboundDate, false, lines);
    }

    private async Task SeedInvoiceAsync(
        string id, string storeCode, DateTime inboundDate, (string? ProductCode, string? StoreProductCode, decimal Quantity) line, bool deleted)
    {
        await SeedInvoiceAsync(id, storeCode, inboundDate, deleted, line);
    }

    private async Task SeedInvoiceAsync(
        string id, string storeCode, DateTime inboundDate, bool deleted,
        params (string? ProductCode, string? StoreProductCode, decimal Quantity)[] lines)
    {
        await _db.Insertable(new StoreLocalSupplierInvoice
        {
            InvoiceGUID = id, StoreCode = storeCode, SupplierCode = "L100", InvoiceNo = id, InboundDate = inboundDate, InboundStatus = 2, IsDeleted = deleted,
        }).ExecuteCommandAsync();
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            await _db.Insertable(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = $"{id}-{index}", InvoiceGUID = id, ProductCode = line.ProductCode, StoreProductCode = line.StoreProductCode,
                Quantity = line.Quantity, IsDeleted = false,
            }).ExecuteCommandAsync();
        }
    }

    private async Task SeedWarehouseOrderAsync(
        string id, string storeCode, DateTime? outboundDate, int flowStatus, decimal allocatedQuantity, string productCode = "W1")
    {
        await _db.Insertable(new WareHouseOrder
        {
            OrderGUID = id, StoreCode = storeCode, OrderNo = id, OrderDate = new DateTime(2026, 7, 1), OutboundDate = outboundDate,
            FlowStatus = flowStatus, IsDeleted = false,
        }).ExecuteCommandAsync();
        await _db.Insertable(new WareHouseOrderDetails
        {
            DetailGUID = $"detail-{id}", OrderGUID = id, ProductCode = productCode, Quantity = allocatedQuantity,
            AllocQuantity = allocatedQuantity, IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private Task SeedSaleAsync(string storeCode, string productCode, DateTime date, int quantity, string supplierCode) =>
        _db.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = date, BranchCode = storeCode, SupplierCode = supplierCode, ProductCode = productCode,
            TotalQuantity = quantity, TotalAmount = quantity * 2m, OrderCount = 1, UpdateTime = date,
        }).ExecuteCommandAsync();

    private static SqlSugarContext Context(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext).GetField("_db", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(context, db);
        return context;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_path);
    }
}
