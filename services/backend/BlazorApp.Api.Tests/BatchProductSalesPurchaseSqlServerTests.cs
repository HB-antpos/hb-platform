using BlazorApp.Shared.Models;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 进货量 SQL 在隔离 SQL Server 上真实执行：OPENJSON、临时表与 EffectivePurchaseDate 持久化列 SQLite 都不支持。
/// 表结构只建查询用到的列，口径与生产一致。
/// </summary>
public sealed partial class BatchProductSalesAnalysisSqlServerIntegrationTests
{
    [BatchSalesSqlServerFact]
    public async Task Purchases_SQLServer_总仓配货与供应商进货按口径合计()
    {
        await PreparePurchaseSchemaAsync();
        await _catalog!.Ado.ExecuteCommandAsync("""
INSERT INTO [WareHouseOrder] ([OrderGUID], [StoreCode], [OrderDate], [OutboundDate], [FlowStatus], [IsDeleted], [CreatedAt]) VALUES
    -- 已完成：订单日期在区间前，但按出库日期计入
    (N'W1', N'S1', '2026-08-28T10:00:00', '2026-09-02', 2, 0, '2026-08-28'),
    -- 草稿不算进货
    (N'W2', N'S1', '2026-09-05T10:00:00', NULL, 0, 0, '2026-09-05'),
    -- 配货中未出库：按订单日期计入，区间末日 23 点仍在区间内
    (N'W3', N'S2', '2026-09-30T23:00:00', NULL, 3, 0, '2026-09-30'),
    -- 出库日期在区间之后
    (N'W4', N'S1', '2026-09-25T10:00:00', '2026-10-01', 2, 0, '2026-09-25'),
    -- 不在请求分店范围
    (N'W5', N'S3', '2026-09-10T10:00:00', '2026-09-10', 2, 0, '2026-09-10'),
    -- 已删除
    (N'W6', N'S1', '2026-09-10T10:00:00', '2026-09-10', 2, 1, '2026-09-10');
INSERT INTO [WareHouseOrderDetails] ([DetailGUID], [OrderGUID], [ProductCode], [Quantity], [AllocQuantity], [IsDeleted]) VALUES
    (N'WD1', N'W1', N'P1', 30, 24, 0),
    (N'WD2', N'W1', N'P2', 10, 0, 0),
    (N'WD3', N'W1', N'P3', 10, NULL, 0),
    (N'WD4', N'W2', N'P1', 100, 100, 0),
    (N'WD5', N'W3', N'P1', 6, 6, 0),
    (N'WD6', N'W4', N'P1', 40, 40, 0),
    (N'WD7', N'W5', N'P1', 50, 50, 0),
    (N'WD8', N'W6', N'P1', 60, 60, 0),
    (N'WD9', N'W1', N'P1', 70, 70, 1);
INSERT INTO [StoreLocalSupplierInvoice] ([InvoiceGUID], [StoreCode], [OrderDate], [InboundDate], [CreatedAt], [IsDeleted]) VALUES
    -- 无入库日期：按订单日期 09-10 计入
    (N'I1', N'S1', '2026-09-10', NULL, '2026-09-10', 0),
    -- 入库日期优先：08-31 在区间之前
    (N'I2', N'S2', '2026-09-03', '2026-08-31', '2026-09-03', 0),
    -- 已删除
    (N'I3', N'S2', '2026-09-12', NULL, '2026-09-12', 1);
INSERT INTO [StoreLocalSupplierInvoiceDetails] ([DetailGUID], [InvoiceGUID], [ProductCode], [StoreProductCode], [Quantity], [IsDeleted]) VALUES
    (N'ID1', N'I1', N'P1', N'SRP-P1', 12, 0),
    -- 明细缺商品编码：按零售价 UUID 回查到 P2
    (N'ID2', N'I1', N'', N'SRP-P2', 5, 0),
    -- 未请求的商品
    (N'ID3', N'I1', N'P9', NULL, 7, 0),
    -- 已删除明细
    (N'ID4', N'I1', N'P1', NULL, 99, 1),
    (N'ID5', N'I2', N'P1', NULL, 8, 0),
    (N'ID6', N'I3', N'P1', NULL, 9, 0);
INSERT INTO [StoreRetailPrice] ([UUID], [ProductCode], [IsDeleted]) VALUES
    (N'SRP-P1', N'P-OTHER', 0),
    (N'SRP-P2', N'P2', 0);
""");
        await _catalog.Insertable(new Store[]
        {
            new() { StoreCode = "S1", StoreName = "一店", IsDeleted = false },
            new() { StoreCode = "S2", StoreName = "二店", IsDeleted = false },
            new() { StoreCode = "S3", StoreName = "三店", IsDeleted = false },
        }).ExecuteCommandAsync();

        var response = await CreateAnalysisService().GetPurchaseQuantitiesAsync(new()
        {
            StartDate = new DateTime(2026, 9, 1), EndDate = new DateTime(2026, 9, 30),
            StoreCodes = ["S1", "S2"], ProductCodes = ["P1", "P2", "P3"],
        }, null);

        var rows = response.Data!.Rows
            .OrderBy(row => row.ProductCode, StringComparer.Ordinal).ThenBy(row => row.BranchCode, StringComparer.Ordinal)
            .Select(row => (row.ProductCode, row.BranchCode, row.WarehouseQuantity, row.SupplierQuantity)).ToList();
        // 明细有商品编码时不看零售价（SRP-P1 指向别的商品也不影响）；P3 配货数量为空、全为 0 的组合不返回。
        Assert.Equal(
            [("P1", "S1", 24m, 12m), ("P1", "S2", 6m, 0m), ("P2", "S1", 0m, 5m)],
            rows);
        Assert.Equal(["S1", "S2"], response.Data.StoreCodes);
        Assert.Equal(["P1", "P2", "P3"], response.Data.ProductCodes);
    }

    [BatchSalesSqlServerFact]
    public async Task Purchases_SQLServer_受限用户只读取授权分店()
    {
        await PreparePurchaseSchemaAsync();
        await _catalog!.Ado.ExecuteCommandAsync("""
INSERT INTO [WareHouseOrder] ([OrderGUID], [StoreCode], [OrderDate], [OutboundDate], [FlowStatus], [IsDeleted], [CreatedAt]) VALUES
    (N'W1', N'S1', '2026-09-02', '2026-09-02', 2, 0, '2026-09-02'),
    (N'W2', N'S2', '2026-09-02', '2026-09-02', 2, 0, '2026-09-02');
INSERT INTO [WareHouseOrderDetails] ([DetailGUID], [OrderGUID], [ProductCode], [Quantity], [AllocQuantity], [IsDeleted]) VALUES
    (N'WD1', N'W1', N'P1', 3, 3, 0),
    (N'WD2', N'W2', N'P1', 4, 4, 0);
""");
        await _catalog.Insertable(new Store[]
        {
            new() { StoreCode = "S1", StoreName = "一店", IsDeleted = false },
            new() { StoreCode = "S2", StoreName = "二店", IsDeleted = false },
        }).ExecuteCommandAsync();

        var response = await CreateAnalysisService().GetPurchaseQuantitiesAsync(new()
        {
            StartDate = new DateTime(2026, 9, 1), EndDate = new DateTime(2026, 9, 30), ProductCodes = ["P1"],
        }, ["S1"]);

        var row = Assert.Single(response.Data!.Rows);
        Assert.Equal(("P1", "S1", 3m), (row.ProductCode, row.BranchCode, row.WarehouseQuantity));
    }

    private async Task PreparePurchaseSchemaAsync()
    {
        await PrepareAnalysisSchemaAsync();
        await _catalog!.Ado.ExecuteCommandAsync("""
CREATE TABLE [WareHouseOrder] (
    [OrderGUID] nvarchar(255) NOT NULL PRIMARY KEY, [StoreCode] nvarchar(50) NULL, [OrderDate] datetime NULL,
    [OutboundDate] datetime NULL, [FlowStatus] int NULL, [IsDeleted] bit NULL, [CreatedAt] datetime NULL);
CREATE TABLE [WareHouseOrderDetails] (
    [DetailGUID] nvarchar(50) NOT NULL PRIMARY KEY, [OrderGUID] nvarchar(50) NULL, [ProductCode] nvarchar(50) NULL,
    [Quantity] decimal(18, 2) NULL, [AllocQuantity] decimal(18, 2) NULL, [IsDeleted] bit NULL);
CREATE TABLE [StoreLocalSupplierInvoice] (
    [InvoiceGUID] nvarchar(255) NOT NULL PRIMARY KEY, [StoreCode] nvarchar(50) NULL, [OrderDate] datetime NULL,
    [InboundDate] datetime NULL, [CreatedAt] datetime NOT NULL, [IsDeleted] bit NOT NULL,
    [EffectivePurchaseDate] AS CONVERT(date, COALESCE([InboundDate], [OrderDate], [CreatedAt])) PERSISTED);
CREATE TABLE [StoreLocalSupplierInvoiceDetails] (
    [DetailGUID] nvarchar(50) NOT NULL PRIMARY KEY, [InvoiceGUID] nvarchar(50) NULL, [ProductCode] nvarchar(50) NULL,
    [StoreProductCode] nvarchar(50) NULL, [Quantity] decimal(18, 4) NULL, [IsDeleted] bit NOT NULL);
CREATE TABLE [StoreRetailPrice] (
    [UUID] nvarchar(50) NOT NULL PRIMARY KEY, [ProductCode] nvarchar(50) NULL, [IsDeleted] bit NOT NULL);
""");
    }
}
