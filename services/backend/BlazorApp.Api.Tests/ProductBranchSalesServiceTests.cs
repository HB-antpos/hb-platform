using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.ProductInsights;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class ProductBranchSalesServiceTests : IDisposable
{
    private readonly string _mainPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
    private readonly string _posmPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _mainConnection;
    private readonly SqliteConnection _posmConnection;
    private readonly SqlSugarClient _mainDb;
    private readonly SqlSugarClient _posmDb;

    public ProductBranchSalesServiceTests()
    {
        _mainConnection = new SqliteConnection($"Data Source={_mainPath}");
        _posmConnection = new SqliteConnection($"Data Source={_posmPath}");
        _mainConnection.Open();
        _posmConnection.Open();
        _mainDb = new SqlSugarClient(Config(_mainConnection.ConnectionString));
        _posmDb = new SqlSugarClient(Config(_posmConnection.ConnectionString));
        _mainDb.CodeFirst.InitTables(
            typeof(Store), typeof(ProductStoreDailySalesStatistic), typeof(Product), typeof(HBLocalSupplier),
            typeof(StoreLocalSupplierInvoice), typeof(StoreLocalSupplierInvoiceDetails), typeof(StoreRetailPrice),
            typeof(WareHouseOrder), typeof(WareHouseOrderDetails)
        );
        _posmDb.CodeFirst.InitTables(typeof(POSM_设备注册信息表));
    }

    [Fact]
    public async Task 启用POS全集补齐零销量并排除非POS和停用POS门店()
    {
        await SeedStoreAsync("S1", "Alpha");
        await SeedStoreAsync("S2", "Beta");
        await SeedStoreAsync("S3", "No POS");
        await SeedStoreAsync("S4", "Disabled POS");
        await SeedPosAsync("S1", enabled: true);
        await SeedPosAsync("S2", enabled: true);
        await SeedPosAsync("S4", enabled: false);
        await SeedSaleAsync("S1", new DateTime(2026, 9, 1), 5, 12.5m, new DateTime(2026, 9, 4, 2, 3, 4, DateTimeKind.Utc));
        await SeedSaleAsync("S3", new DateTime(2026, 9, 1), 99, 999m, new DateTime(2026, 9, 4, 3, 0, 0, DateTimeKind.Utc));

        var result = await CreateService().GetAsync(" P1 ", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 1), null);

        Assert.Equal("all-pos", result.Scope);
        Assert.Equal(2, result.TotalPosStoreCount);
        Assert.Equal(2, result.IncludedStoreCount);
        Assert.Equal(5, result.Quantity);
        Assert.Equal(12.5m, result.Amount);
        Assert.Collection(result.Rows,
            row => { Assert.Equal("S1", row.StoreCode); Assert.Equal(5, row.Quantity); Assert.Equal(12.5m, row.Amount); },
            row => { Assert.Equal("S2", row.StoreCode); Assert.Equal(0, row.Quantity); Assert.Equal(0m, row.Amount); });
        Assert.Equal(new DateTime(2026, 9, 4, 2, 3, 4, DateTimeKind.Utc), result.SalesStatisticLastUpdatedAt);
    }

    [Fact]
    public async Task 授权范围只聚合允许的POS门店且保留完整POS总数()
    {
        await SeedStoreAsync("S1", "Alpha");
        await SeedStoreAsync("S2", "Beta");
        await SeedPosAsync("S1", enabled: true);
        await SeedPosAsync("S2", enabled: true);
        await SeedSaleAsync("S1", new DateTime(2026, 9, 1), 5, 10m, DateTime.UtcNow);
        await SeedSaleAsync("S2", new DateTime(2026, 9, 1), 8, 16m, DateTime.UtcNow);

        var result = await CreateService().GetAsync("P1", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 1), new[] { "s2", "S999" });

        Assert.Equal("authorized-pos", result.Scope);
        Assert.Equal(2, result.TotalPosStoreCount);
        Assert.Equal(1, result.IncludedStoreCount);
        Assert.Equal(8, result.Quantity);
        Assert.Equal(16m, result.Amount);
        Assert.Equal("S2", Assert.Single(result.Rows).StoreCode);
    }

    [Fact]
    public async Task 日期首尾都按业务日包含且非法范围抛出错误()
    {
        await SeedStoreAsync("S1", "Alpha");
        await SeedPosAsync("S1", enabled: true);
        await SeedSaleAsync("S1", new DateTime(2026, 9, 1), 2, 4m, DateTime.UtcNow);
        await SeedSaleAsync("S1", new DateTime(2026, 9, 2), 3, 6m, DateTime.UtcNow);
        await SeedSaleAsync("S1", new DateTime(2026, 9, 3), 7, 14m, DateTime.UtcNow);

        var result = await CreateService().GetAsync("P1", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 2), null);

        Assert.Equal(5, result.Quantity);
        Assert.Equal(10m, result.Amount);
        await Assert.ThrowsAsync<ArgumentException>(() => CreateService().GetAsync("P1", new DateOnly(2026, 9, 3), new DateOnly(2026, 9, 2), null));
    }

    [Fact]
    public async Task 本地商品按分店汇总进货单数量_口径与单店一致且不串店()
    {
        await SeedProductAsync("L100");
        await SeedStoreAsync("S1", "Alpha");
        await SeedStoreAsync("S2", "Beta");
        await SeedStoreAsync("S3", "Gamma");
        await SeedPosAsync("S1", enabled: true);
        await SeedPosAsync("S2", enabled: true);
        await SeedPosAsync("S3", enabled: true);
        await _mainDb.Insertable(new[]
        {
            new StoreRetailPrice { UUID = "price-s1", StoreCode = "S1", ProductCode = "P1", IsActive = true, IsDeleted = false },
            new StoreRetailPrice { UUID = "price-s2", StoreCode = "S2", ProductCode = "P1", IsActive = true, IsDeleted = false },
        }).ExecuteCommandAsync();
        // S1：直连商品码 4 + 同店价目映射回退 3；另有已删明细、区间外单据不计。
        await SeedInvoiceAsync("inv-1", "S1", new DateTime(2026, 9, 2), null,
            Detail("d1", "P1", 4), Detail("d2", "", 3, "price-s1"), Detail("d3", "P1", 50, deleted: true));
        await SeedInvoiceAsync("inv-old", "S1", new DateTime(2026, 8, 31), null, Detail("d4", "P1", 80));
        // S2：无入库日时回退订单日；S2 单据引用 S1 的价目 UUID 不能被回退算入。
        await SeedInvoiceAsync("inv-2", "S2", null, new DateTime(2026, 9, 3), Detail("d5", "P1", 6), Detail("d6", null, 99, "price-s1"));
        await SeedSaleAsync("S1", new DateTime(2026, 9, 2), 5, 10m, DateTime.UtcNow);

        var result = await CreateService().GetAsync("P1", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 3), null);

        Assert.Equal("local", result.PurchaseSource);
        Assert.Equal(13m, result.PurchaseQuantity);
        Assert.Equal(7m, result.Rows.Single(row => row.StoreCode == "S1").PurchaseQuantity);
        Assert.Equal(6m, result.Rows.Single(row => row.StoreCode == "S2").PurchaseQuantity);
        Assert.Equal(0m, result.Rows.Single(row => row.StoreCode == "S3").PurchaseQuantity);

        // 同一分店在单店进销页看到的进货合计必须与分店表一致。
        var single = await new StoreProductInsightQueryService(Context(_mainDb), new ServiceCollection().BuildServiceProvider())
            .GetAsync("S1", "P1", new DateTime(2026, 9, 1), new DateTime(2026, 9, 3));
        Assert.Equal(7m, single!.Purchases.Quantity);
    }

    [Fact]
    public async Task 仓库商品按已出库配货数量与出库日汇总()
    {
        await SeedProductAsync("200");
        await SeedStoreAsync("S1", "Alpha");
        await SeedStoreAsync("S2", "Beta");
        await SeedPosAsync("S1", enabled: true);
        await SeedPosAsync("S2", enabled: true);
        await SeedWarehouseOrderAsync("o1", "S1", new DateTime(2026, 8, 20), new DateTime(2026, 9, 2), 2, 12, 10);
        await SeedWarehouseOrderAsync("o2", "S1", new DateTime(2026, 9, 2), null, 3, 8, 8);
        await SeedWarehouseOrderAsync("o3", "S2", new DateTime(2026, 9, 1), new DateTime(2026, 9, 4), 2, 5, 5);

        var result = await CreateService().GetAsync("P1", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 3), null);

        Assert.Equal("warehouse", result.PurchaseSource);
        Assert.Equal(10m, result.PurchaseQuantity);
        Assert.Equal(10m, result.Rows.Single(row => row.StoreCode == "S1").PurchaseQuantity);
        Assert.Equal(0m, result.Rows.Single(row => row.StoreCode == "S2").PurchaseQuantity);
    }

    [Fact]
    public async Task 授权范围外分店的进货不计入合计()
    {
        await SeedProductAsync("L100");
        await SeedStoreAsync("S1", "Alpha");
        await SeedStoreAsync("S2", "Beta");
        await SeedPosAsync("S1", enabled: true);
        await SeedPosAsync("S2", enabled: true);
        await SeedInvoiceAsync("inv-1", "S1", new DateTime(2026, 9, 1), null, Detail("d1", "P1", 4));
        await SeedInvoiceAsync("inv-2", "S2", new DateTime(2026, 9, 1), null, Detail("d2", "P1", 9));

        var result = await CreateService().GetAsync("P1", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 1), new[] { "S2" });

        Assert.Equal(9m, result.PurchaseQuantity);
        Assert.Equal(9m, Assert.Single(result.Rows).PurchaseQuantity);
    }

    private ProductBranchSalesService CreateService() => new(
        Context(_mainDb),
        PosmContext(_posmDb),
        new StoreProductInsightQueryService(Context(_mainDb), new ServiceCollection().BuildServiceProvider()),
        TimeProvider.System
    );

    private Task SeedProductAsync(string supplierCode) => _mainDb.Insertable(new Product
    {
        UUID = Guid.NewGuid().ToString(), ProductCode = "P1", ProductName = "Product", LocalSupplierCode = supplierCode, IsActive = true, IsDeleted = false,
    }).ExecuteCommandAsync();

    private async Task SeedInvoiceAsync(string id, string storeCode, DateTime? inboundDate, DateTime? orderDate, params StoreLocalSupplierInvoiceDetails[] details)
    {
        await _mainDb.Insertable(new StoreLocalSupplierInvoice
        {
            InvoiceGUID = id, StoreCode = storeCode, SupplierCode = "L100", InvoiceNo = id, InboundDate = inboundDate, OrderDate = orderDate, InboundStatus = 1, IsDeleted = false,
        }).ExecuteCommandAsync();
        foreach (var detail in details)
        {
            detail.InvoiceGUID = id;
            await _mainDb.Insertable(detail).ExecuteCommandAsync();
        }
    }

    private static StoreLocalSupplierInvoiceDetails Detail(string id, string? productCode, decimal quantity, string? storeProductCode = null, bool deleted = false) => new()
    {
        DetailGUID = id, ProductCode = productCode, StoreProductCode = storeProductCode, Quantity = quantity, IsDeleted = deleted,
    };

    private async Task SeedWarehouseOrderAsync(string id, string storeCode, DateTime orderDate, DateTime? outboundDate, int status, decimal quantity, decimal allocated)
    {
        await _mainDb.Insertable(new WareHouseOrder
        {
            OrderGUID = id, StoreCode = storeCode, OrderNo = id, OrderDate = orderDate, OutboundDate = outboundDate, FlowStatus = status, IsDeleted = false,
        }).ExecuteCommandAsync();
        await _mainDb.Insertable(new WareHouseOrderDetails
        {
            DetailGUID = $"detail-{id}", OrderGUID = id, ProductCode = "P1", Quantity = quantity, AllocQuantity = allocated, IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private Task SeedStoreAsync(string code, string name) => _mainDb.Insertable(new Store
    {
        StoreGUID = Guid.NewGuid().ToString(), StoreCode = code, StoreName = name, IsActive = true, IsDeleted = false,
    }).ExecuteCommandAsync();

    private Task SeedPosAsync(string storeCode, bool enabled) => _posmDb.Insertable(new POSM_设备注册信息表
    {
        设备硬件识别码 = Guid.NewGuid().ToString(), 系统设备编号 = Guid.NewGuid().ToString("N"), 分店代码 = storeCode,
        设备类型 = "POS", 设备系统 = "Windows", 设备状态 = enabled ? 1 : 0, 设备授权码 = "test",
    }).ExecuteCommandAsync();

    private Task SeedSaleAsync(string storeCode, DateTime date, int quantity, decimal amount, DateTime updatedAt) =>
        _mainDb.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = date, BranchCode = storeCode, SupplierCode = "S", ProductCode = "P1", TotalQuantity = quantity,
            TotalAmount = amount, OrderCount = 1, UpdateTime = updatedAt,
        }).ExecuteCommandAsync();

    private static ConnectionConfig Config(string connectionString) => new()
    {
        ConnectionString = connectionString, DbType = DbType.Sqlite, IsAutoCloseConnection = false,
        InitKeyType = InitKeyType.Attribute,
    };

    private static SqlSugarContext Context(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext).GetField("_db", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(context, db);
        return context;
    }

    private static POSMSqlSugarContext PosmContext(ISqlSugarClient db)
    {
        var context = (POSMSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(POSMSqlSugarContext));
        typeof(POSMSqlSugarContext).GetField("_db", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(context, db);
        return context;
    }

    public void Dispose()
    {
        _mainConnection.Dispose();
        _posmConnection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_mainPath);
        SqliteTempFileCleanup.DeleteIfExists(_posmPath);
    }
}
