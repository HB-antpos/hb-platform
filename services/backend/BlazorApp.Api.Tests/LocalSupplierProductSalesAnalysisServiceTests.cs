using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public class LocalSupplierProductSalesAnalysisServiceTests : IDisposable
{
    private static readonly DateTime EndDate = DateTime.UtcNow.AddHours(10).Date.AddDays(-1);
    private static readonly DateTime StartDate = EndDate.AddDays(-30);

    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public LocalSupplierProductSalesAnalysisServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();

        _db = new SqlSugarClient(
            new ConnectionConfig
            {
                ConnectionString = _connection.ConnectionString,
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = false,
                InitKeyType = InitKeyType.Attribute,
            }
        );

        _db.CodeFirst.InitTables(
            typeof(Store),
            typeof(UserStore),
            typeof(HBLocalSupplier),
            typeof(Product),
            typeof(StoreRetailPrice),
            typeof(StoreMultiCodeProduct),
            typeof(ProductSetCode),
            typeof(WarehouseCategory),
            typeof(LocalSupplierCategory),
            typeof(LocalSupplierCategoryProductAssignment),
            typeof(StoreLocalSupplierInvoice),
            typeof(StoreLocalSupplierInvoiceDetails),
            typeof(ProductStoreDailySalesStatistic)
        );
    }

    [Fact]
    public async Task GetCandidatesAsync_历史候选不受日期限制()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("P1", "ITM1", "BC1", "Product One");
        await InsertInvoiceWithDetailAsync(
            "h1",
            "B1",
            "SUP",
            "P1",
            EndDate.AddDays(-120),
            quantity: 5m,
            amount: 10m
        );

        var service = CreateService();
        var result = await service.GetCandidatesAsync(
            CreateRequest(),
            new List<string> { "B1" }
        );

        Assert.True(result.Success);
        Assert.Single(result.Data!.Items);
        Assert.Equal("P1", result.Data.Items.Single().ProductCode);
    }

    [Fact]
    public async Task GetSummaryAsync_授权分店隔离()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertStoreAsync("B2", "Branch Two");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("P1", "ITM1", "BC1", "Product One");
        await InsertProductAsync("P2", "ITM2", "BC2", "Product Two");
        await InsertInvoiceWithDetailAsync("h1", "B1", "SUP", "P1", EndDate, 2m, 10m);
        await InsertInvoiceWithDetailAsync("h2", "B2", "SUP", "P2", EndDate, 3m, 20m);

        var service = CreateService();
        var result = await service.GetSummaryAsync(
            CreateRequest(),
            new List<string> { "B1" }
        );

        Assert.True(result.Success);
        var p1 = Assert.Single(result.Data!.Items, row => row.ProductCode == "P1");
        Assert.Equal(2m, p1.PurchaseQuantity);
        var p2 = Assert.Single(result.Data.Items, row => row.ProductCode == "P2");
        Assert.Equal(0m, p2.PurchaseQuantity);
    }

    [Fact]
    public async Task GetSummaryAsync_有效分店明细优先且严格限定授权范围()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertStoreAsync("B2", "Branch Two");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("P1", "ITM1", "BC1", "Product One");
        // 明细店 B1（授权内）覆盖表头店 B2：计入 B1。
        await InsertInvoiceWithDetailAsync(
            "h1",
            "B1",
            "SUP",
            "P1",
            EndDate,
            2m,
            10m,
            detailStoreCode: "B2"
        );
        // 明细店 B2（授权外）覆盖表头店 B1：不计入。
        await InsertInvoiceWithDetailAsync(
            "h2",
            "B2",
            "SUP",
            "P1",
            EndDate,
            5m,
            50m,
            detailStoreCode: "B1"
        );

        var result = await CreateService().GetSummaryAsync(
            CreateRequest(),
            new List<string> { "B1" }
        );

        var p1 = Assert.Single(result.Data!.Items);
        Assert.Equal(2m, p1.PurchaseQuantity);
    }

    [Fact]
    public async Task GetCandidatesAsync_空分店范围返回空()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("P1", "ITM1", "BC1", "Product One");
        await InsertInvoiceWithDetailAsync("h1", "B1", "SUP", "P1", EndDate, 2m, 10m);

        var service = CreateService();
        var result = await service.GetCandidatesAsync(
            CreateRequest(),
            new List<string>()
        );

        Assert.True(result.Success);
        Assert.Empty(result.Data!.Items);
        Assert.Equal(0, result.Data.Total);
    }

    [Fact]
    public async Task GetCandidatesAsync_全分店与空分店缓存键隔离()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("P1", "ITM1", "BC1", "Product One");
        await InsertInvoiceWithDetailAsync("h1", "B1", "SUP", "P1", EndDate, 2m, 10m);

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(cache);
        var allStores = await service.GetCandidatesAsync(CreateRequest(), null);
        var noStores = await service.GetCandidatesAsync(CreateRequest(), new List<string>());

        Assert.Single(allStores.Data!.Items);
        Assert.Empty(noStores.Data!.Items);
    }

    [Fact]
    public async Task GetCandidatesAsync_多选供应商超过100项返回参数校验错误()
    {
        var request = CreateRequest();
        request.Filter.SupplierCodes = Enumerable.Range(1, 101).Select(index => $"SUP-{index}").ToList();

        var result = await CreateService().GetCandidatesAsync(request, new[] { "B1" });

        Assert.False(result.Success);
        Assert.Equal("VALIDATION_ERROR", result.ErrorCode);
        Assert.Contains("供应商不能超过 100 项", result.Message);
    }

    [Fact]
    public async Task GetCandidatesAsync_多选仓库分类超过100项返回参数校验错误()
    {
        var request = CreateRequest();
        request.Filter.WarehouseCategoryGuids = Enumerable.Range(1, 101).Select(index => $"CAT-{index}").ToList();

        var result = await CreateService().GetCandidatesAsync(request, new[] { "B1" });

        Assert.False(result.Success);
        Assert.Equal("VALIDATION_ERROR", result.ErrorCode);
        Assert.Contains("仓库分类不能超过 100 项", result.Message);
    }

    [Fact]
    public void ValidateFilterCodeBudget_分类展开后超过SQL参数预算返回参数校验错误()
    {
        var filter = new LocalSupplierProductSalesAnalysisFilterDto
        {
            SupplierCodes = Enumerable.Range(1, 100).Select(index => $"SUP-{index}").ToList(),
        };

        var exception = Assert.Throws<LocalSupplierProductSalesAnalysisValidationException>(() =>
            LocalSupplierProductSalesAnalysisLogic.ValidateFilterCodeBudget(filter, 1701)
        );

        Assert.Contains("分类和供应商筛选条件过多", exception.Message);
    }

    [Fact]
    public async Task GetSummaryAsync_软删除进货与明细被排除()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("P1", "ITM1", "BC1", "Product One");
        await InsertProductAsync("P2", "ITM2", "BC2", "Product Two");
        await InsertInvoiceWithDetailAsync(
            "h1",
            "B1",
            "SUP",
            "P1",
            EndDate,
            1m,
            1m,
            headerDeleted: true
        );
        await InsertInvoiceWithDetailAsync("h2", "B1", "SUP", "P2", EndDate, 1m, 1m);

        var service = CreateService();
        var result = await service.GetSummaryAsync(
            CreateRequest(),
            new List<string> { "B1" }
        );

        var p1 = Assert.Single(result.Data!.Items, row => row.ProductCode == "P1");
        Assert.Equal(0m, p1.PurchaseQuantity);
        var p2 = Assert.Single(result.Data.Items, row => row.ProductCode == "P2");
        Assert.Equal(1m, p2.PurchaseQuantity);
    }

    [Fact]
    public async Task GetSummaryAsync_期间零值仍返回且动销率为空()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("P1", "ITM1", "BC1", "Product One");
        await InsertInvoiceWithDetailAsync(
            "h1",
            "B1",
            "SUP",
            "P1",
            EndDate.AddDays(-120),
            5m,
            10m
        );

        var service = CreateService();
        var result = await service.GetSummaryAsync(
            CreateRequest(),
            new List<string> { "B1" }
        );

        var row = Assert.Single(result.Data!.Items);
        Assert.Equal("P1", row.ProductCode);
        Assert.Equal(0m, row.PurchaseQuantity);
        Assert.Equal(0m, row.NetSalesQuantity);
        Assert.Null(row.SellThroughRate);
    }

    [Fact]
    public async Task GetSummaryAsync_负销量保留且动销率为百分比()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("P1", "ITM1", "BC1", "Product One");
        await InsertInvoiceWithDetailAsync("h1", "B1", "SUP", "P1", EndDate, 10m, 100m);
        await InsertSaleAsync(EndDate, "B1", "SUP", "P1", -2, -20m);

        var service = CreateService();
        var result = await service.GetSummaryAsync(
            CreateRequest(),
            new List<string> { "B1" }
        );

        var row = Assert.Single(result.Data!.Items);
        Assert.Equal(10m, row.PurchaseQuantity);
        Assert.Equal(-2m, row.NetSalesQuantity);
        Assert.Equal(-20m, row.NetSalesAmount);
        Assert.Equal(-20m, row.SellThroughRate);
    }

    [Fact]
    public async Task GetBranchesAsync_按当前商品排分店()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertStoreAsync("B2", "Branch Two");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("P1", "ITM1", "BC1", "Product One");
        await InsertProductAsync("P2", "ITM2", "BC2", "Product Two");
        await InsertInvoiceWithDetailAsync("h1", "B1", "SUP", "P1", EndDate, 1m, 1m);
        await InsertInvoiceWithDetailAsync("h2", "B1", "SUP", "P2", EndDate, 1m, 1m);
        await InsertSaleAsync(EndDate, "B1", "SUP", "P1", 2, 20m);
        await InsertSaleAsync(EndDate, "B2", "SUP", "P1", 5, 50m);
        await InsertSaleAsync(EndDate, "B1", "SUP", "P2", 100, 1000m);

        var service = CreateService();
        var request = CreateRequest();
        request.CurrentProductCode = "P1";
        var result = await service.GetBranchesAsync(request, new List<string> { "B1", "B2" });

        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.Count);
        Assert.Equal(2m, result.Data.Single(row => row.BranchCode == "B1").NetSalesQuantity);
        Assert.Equal(5m, result.Data.Single(row => row.BranchCode == "B2").NetSalesQuantity);
    }

    [Fact]
    public async Task GetProductDailyAsync_越界商品返回空()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("P1", "ITM1", "BC1", "Product One");
        await InsertInvoiceWithDetailAsync("h1", "B1", "SUP", "P1", EndDate, 1m, 1m);

        var service = CreateService();
        var request = CreateRequest();
        request.CurrentProductCode = "ZZZ";
        var result = await service.GetProductDailyAsync(request, new List<string> { "B1" });

        Assert.True(result.Success);
        Assert.Empty(result.Data!);
    }

    [Fact]
    public async Task GetInvoiceDetailsAsync_供应商明细优先回退表头()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP-D", "Detail Supplier");
        await InsertSupplierAsync("SUP-H", "Header Supplier");
        await InsertProductAsync("P1", "ITM1", "BC1", "Product One");
        await _db.Insertable(
            new StoreLocalSupplierInvoice
            {
                InvoiceGUID = "h1",
                StoreCode = "B1",
                SupplierCode = "SUP-H",
                InboundDate = EndDate,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _db.Insertable(
            new[]
            {
                new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = "d1",
                    InvoiceGUID = "h1",
                    ProductCode = "P1",
                    SupplierCode = "SUP-D",
                    Quantity = 2m,
                    Amount = 10m,
                    IsDeleted = false,
                },
                new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = "d2",
                    InvoiceGUID = "h1",
                    ProductCode = "P1",
                    SupplierCode = "  ",
                    Quantity = 3m,
                    Amount = 15m,
                    IsDeleted = false,
                },
            }
        ).ExecuteCommandAsync();

        var service = CreateService();
        var request = CreateRequest();
        request.CurrentProductCode = "P1";
        var result = await service.GetInvoiceDetailsAsync(request, new List<string> { "B1" });

        Assert.True(result.Success);
        Assert.Equal(
            "SUP-D",
            result.Data!.Items.Single(row => row.DetailGUID == "d1").SupplierCode
        );
        Assert.Equal(
            "SUP-H",
            result.Data.Items.Single(row => row.DetailGUID == "d2").SupplierCode
        );
    }

    [Fact]
    public async Task GetCandidatesAsync_父分类包含子分类()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP", "Supplier One");
        await _db.Insertable(
            new[]
            {
                new WarehouseCategory
                {
                    CategoryGUID = "root",
                    ParentGUID = null,
                    CategoryName = "Root",
                    IsDeleted = false,
                    IsActive = true,
                },
                new WarehouseCategory
                {
                    CategoryGUID = "child",
                    ParentGUID = "root",
                    CategoryName = "Child",
                    IsDeleted = false,
                    IsActive = true,
                },
            }
        ).ExecuteCommandAsync();
        await InsertProductAsync("P1", "ITM1", "BC1", "Product One", "child");
        await InsertInvoiceWithDetailAsync("h1", "B1", "SUP", "P1", EndDate, 1m, 1m);

        var service = CreateService();
        var request = CreateRequest();
        request.Filter.CategoryGuid = "root";
        var result = await service.GetCandidatesAsync(request, new List<string> { "B1" });

        Assert.True(result.Success);
        Assert.Contains(result.Data!.Items, row => row.ProductCode == "P1");
    }

    [Fact]
    public async Task GetSupplierCategoryOptionsAsync_按供应商返回分类树且仅叶分类可选()
    {
        await _db.Insertable(
            new[]
            {
                new LocalSupplierCategory
                {
                    CategoryGUID = "sup-root",
                    LocalSupplierCode = "SUP",
                    CategoryName = "Supplier Root",
                    ExternalKey = "/root",
                    FullPath = "Supplier Root",
                    IsDeleted = false,
                    IsActive = true,
                },
                new LocalSupplierCategory
                {
                    CategoryGUID = "sup-leaf",
                    LocalSupplierCode = "SUP",
                    ParentGUID = "sup-root",
                    CategoryName = "Supplier Leaf",
                    ExternalKey = "/root/leaf",
                    FullPath = "Supplier Root > Supplier Leaf",
                    IsDeleted = false,
                    IsActive = true,
                },
                new LocalSupplierCategory
                {
                    CategoryGUID = "other-leaf",
                    LocalSupplierCode = "OTHER",
                    CategoryName = "Other Leaf",
                    ExternalKey = "/other",
                    FullPath = "Other Leaf",
                    IsDeleted = false,
                    IsActive = true,
                },
                new LocalSupplierCategory
                {
                    CategoryGUID = "dirty-200",
                    LocalSupplierCode = "200",
                    CategoryName = "脏本地分类",
                    FullPath = "脏本地分类",
                    IsDeleted = false,
                    IsActive = true,
                },
            }
        ).ExecuteCommandAsync();
        await _db.Insertable(
            new[]
            {
                new WarehouseCategory
                {
                    CategoryGUID = "warehouse-root",
                    CategoryName = "Warehouse Root",
                    IsDeleted = false,
                    IsActive = true,
                },
                new WarehouseCategory
                {
                    CategoryGUID = "warehouse-leaf",
                    ParentGUID = "warehouse-root",
                    CategoryName = "Warehouse Leaf",
                    ChineseName = "仓库叶分类",
                    IsDeleted = false,
                    IsActive = true,
                },
            }
        ).ExecuteCommandAsync();

        var result = await CreateService().GetSupplierCategoryOptionsAsync(
            new[] { "SUP", "200" },
            new[] { "B1" },
            tree: true
        );

        Assert.True(result.Success);
        Assert.Equal(new[] { "200", "SUP" }, result.Data!.Select(item => item.SupplierCode));
        var warehouseRoot = result.Data!.Single(item => item.Guid == "warehouse-root");
        Assert.False(warehouseRoot.IsSelectable);
        Assert.Equal("warehouse-leaf", warehouseRoot.Children.Single().Guid);
        Assert.True(warehouseRoot.Children.Single().IsSelectable);
        Assert.Equal("warehouse-root", warehouseRoot.Children.Single().ParentGuid);
        var supplierRoot = result.Data!.Single(item => item.Guid == "sup-root");
        Assert.False(supplierRoot.IsSelectable);
        Assert.Equal("sup-leaf", supplierRoot.Children.Single().Guid);
        Assert.Equal("sup-root", supplierRoot.Children.Single().ParentGuid);
        Assert.DoesNotContain(result.Data!, item => item.Guid == "other-leaf");
        Assert.DoesNotContain(result.Data!.SelectMany(item => item.Children), item => item.Guid == "dirty-200");
    }

    [Fact]
    public async Task GetSupplierCategoryOptionsAsync_默认保持叶分类平铺契约()
    {
        await _db.Insertable(new LocalSupplierCategory
        {
            CategoryGUID = "flat-root", LocalSupplierCode = "SUP", CategoryName = "Root", FullPath = "Root",
            IsDeleted = false, IsActive = true,
        }).ExecuteCommandAsync();
        await _db.Insertable(new LocalSupplierCategory
        {
            CategoryGUID = "flat-leaf", LocalSupplierCode = "SUP", ParentGUID = "flat-root", CategoryName = "Leaf",
            FullPath = "Root > Leaf", IsDeleted = false, IsActive = true,
        }).ExecuteCommandAsync();

        var result = await CreateService().GetSupplierCategoryOptionsAsync(new[] { "SUP" }, new[] { "B1" });

        Assert.True(result.Success);
        var item = Assert.Single(result.Data!);
        Assert.Equal("flat-leaf", item.Guid);
        Assert.Equal("Root > Leaf", item.Name);
        Assert.Empty(item.Children);
    }

    [Fact]
    public async Task GetSupplierCategoryOptionsAsync_分类父级成环时仍保留节点()
    {
        await _db.Insertable(new[]
        {
            new LocalSupplierCategory
            {
                CategoryGUID = "cycle-a", LocalSupplierCode = "SUP", ParentGUID = "cycle-b", CategoryName = "A",
                FullPath = "A", IsDeleted = false, IsActive = true,
            },
            new LocalSupplierCategory
            {
                CategoryGUID = "cycle-b", LocalSupplierCode = "SUP", ParentGUID = "cycle-a", CategoryName = "B",
                FullPath = "B", IsDeleted = false, IsActive = true,
            },
        }).ExecuteCommandAsync();

        var result = await CreateService().GetSupplierCategoryOptionsAsync(new[] { "SUP" }, new[] { "B1" }, tree: true);

        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.SelectMany(FlattenCategoryOptions).Count());
    }

    private static IEnumerable<LocalSupplierProductSalesSupplierCategoryOptionDto> FlattenCategoryOptions(
        LocalSupplierProductSalesSupplierCategoryOptionDto item
    )
    {
        yield return item;
        foreach (var child in item.Children.SelectMany(FlattenCategoryOptions))
        {
            yield return child;
        }
    }

    [Fact]
    public async Task GetCandidatesAsync_供应商分类多选同时支持网站分类和供应商200仓库分类()
    {
        await InsertProductAsync("P-SUP", "ITM-SUP", "BC-SUP", "Supplier Product", localSupplierCode: "SUP");
        await InsertProductAsync("P-STALE", "ITM-STALE", "BC-STALE", "Stale Assignment", localSupplierCode: "OTHER");
        await InsertProductAsync("P-200", "ITM-200", "BC-200", "Warehouse Product", "warehouse-leaf", "200");
        await _db.Insertable(
            new[]
            {
                new WarehouseCategory
                {
                    CategoryGUID = "warehouse-root",
                    CategoryName = "Warehouse Root",
                    IsDeleted = false,
                    IsActive = true,
                },
                new WarehouseCategory
                {
                    CategoryGUID = "warehouse-leaf",
                    ParentGUID = "warehouse-root",
                    CategoryName = "Warehouse Leaf",
                    IsDeleted = false,
                    IsActive = true,
                },
            }
        ).ExecuteCommandAsync();
        await _db.Insertable(
            new[]
            {
                new LocalSupplierCategoryProductAssignment
                {
                    ProductCode = "P-SUP",
                    LocalSupplierCode = "SUP",
                    CategoryGUID = "sup-leaf",
                },
                // 商品供应商已变更时，旧归属不得误命中。
                new LocalSupplierCategoryProductAssignment
                {
                    ProductCode = "P-STALE",
                    LocalSupplierCode = "SUP",
                    CategoryGUID = "sup-leaf",
                },
            }
        ).ExecuteCommandAsync();

        var request = CreateRequest();
        request.Filter.SupplierCodes = new List<string> { "SUP", "OTHER", "200" };
        request.Filter.SupplierCategoryGuids = new List<string> { "sup-leaf", "warehouse-root" };
        string? failedSql = null;
        _db.Aop.OnLogExecuting = (sql, _) => failedSql = sql;
        var logger = new RecordingLogger<LocalSupplierProductSalesAnalysisService>();
        var result = await CreateService(logger: logger).GetCandidatesAsync(request, new[] { "B1" });

        Assert.True(
            result.Success,
            $"{logger.LastException?.ToString() ?? result.Message}\nSQL:\n{failedSql}"
        );
        Assert.Equal(
            new[] { "P-200", "P-SUP" },
            result.Data!.Items.Select(item => item.ProductCode).OrderBy(code => code).ToArray()
        );
    }

    [Fact]
    public async Task GetCandidatesAsync_候选来自商品主档无需进货()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("P1", "ITM1", "BC1", "Product One");

        var result = await CreateService().GetCandidatesAsync(
            CreateRequest(),
            new List<string> { "B1" }
        );

        Assert.True(result.Success);
        Assert.Contains(result.Data!.Items, row => row.ProductCode == "P1");
    }

    [Fact]
    public async Task GetSummaryAsync_无进货销量商品仍出现且统计零()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("P1", "ITM1", "BC1", "Product One");

        var result = await CreateService().GetSummaryAsync(
            CreateRequest(),
            new List<string> { "B1" }
        );

        var p1 = Assert.Single(result.Data!.Items);
        Assert.Equal(0m, p1.PurchaseQuantity);
        Assert.Equal(0m, p1.NetSalesQuantity);
    }

    [Fact]
    public async Task GetSummaryAsync_进货金额空回退数量乘进货价()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("P1", "ITM1", "BC1", "Product One");
        await _db.Insertable(
            new StoreLocalSupplierInvoice
            {
                InvoiceGUID = "h1",
                StoreCode = "B1",
                SupplierCode = "SUP",
                InboundDate = EndDate,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _db.Insertable(
            new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "d1",
                InvoiceGUID = "h1",
                ProductCode = "P1",
                Quantity = 3m,
                PurchasePrice = 2m,
                Amount = null,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();

        var result = await CreateService().GetSummaryAsync(
            CreateRequest(),
            new List<string> { "B1" }
        );

        var p1 = Assert.Single(result.Data!.Items);
        Assert.Equal(3m, p1.PurchaseQuantity);
        Assert.Equal(6m, p1.PurchaseAmount);
    }

    [Fact]
    public async Task GetSummaryAsync_有效进货日按入库订单创建三级回退并遵守日期边界()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("P1", "ITM1", "BC1", "Product One");
        await _db.Insertable(
            new[]
            {
                new StoreLocalSupplierInvoice
                {
                    InvoiceGUID = "order-fallback",
                    StoreCode = "B1",
                    SupplierCode = "SUP",
                    InboundDate = null,
                    OrderDate = EndDate,
                    CreatedAt = StartDate.AddDays(-10),
                    IsDeleted = false,
                },
                new StoreLocalSupplierInvoice
                {
                    InvoiceGUID = "inbound-wins",
                    StoreCode = "B1",
                    SupplierCode = "SUP",
                    InboundDate = StartDate.AddDays(-1),
                    OrderDate = EndDate,
                    CreatedAt = EndDate,
                    IsDeleted = false,
                },
                new StoreLocalSupplierInvoice
                {
                    InvoiceGUID = "created-fallback",
                    StoreCode = "B1",
                    SupplierCode = "SUP",
                    InboundDate = null,
                    OrderDate = null,
                    CreatedAt = StartDate,
                    IsDeleted = false,
                },
            }
        ).ExecuteCommandAsync();
        await _db.Insertable(
            new[]
            {
                new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = "d-order",
                    InvoiceGUID = "order-fallback",
                    StoreCode = "B1",
                    ProductCode = "P1",
                    Quantity = 2m,
                    Amount = 4m,
                    IsDeleted = false,
                },
                new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = "d-inbound",
                    InvoiceGUID = "inbound-wins",
                    StoreCode = "B1",
                    ProductCode = "P1",
                    Quantity = 7m,
                    Amount = 14m,
                    IsDeleted = false,
                },
                new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = "d-created",
                    InvoiceGUID = "created-fallback",
                    StoreCode = "B1",
                    ProductCode = "P1",
                    Quantity = 3m,
                    Amount = 6m,
                    IsDeleted = false,
                },
            }
        ).ExecuteCommandAsync();

        var result = await CreateService().GetSummaryAsync(
            CreateRequest(),
            new List<string> { "B1" }
        );

        Assert.True(result.Success);
        var row = Assert.Single(result.Data!.Items);
        Assert.Equal(5m, row.PurchaseQuantity);
        Assert.Equal(10m, row.PurchaseAmount);
    }

    [Fact]
    public async Task BootstrapAsync_返回完整分段并自动选择首个候选()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("P1", "ITM1", "BC1", "Product One");
        await InsertProductAsync("P2", "ITM2", "BC2", "Product Two");
        await InsertInvoiceWithDetailAsync("h1", "B1", "SUP", "P1", EndDate, 2m, 10m);

        var request = CreateRequest();
        request.AutoSelectFirst = true;
        var result = await CreateService().BootstrapAsync(
            request,
            new List<string> { "B1" }
        );

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.False(result.Data!.Partial);
        Assert.Empty(result.Data.SectionErrors);
        Assert.Equal(2, result.Data.Candidates.Total);
        Assert.Equal("P1", result.Data.CurrentProduct!.ProductCode);
        Assert.Equal(2, result.Data.Summary.Total);
        Assert.Single(result.Data.InvoiceDetails.Items);
        Assert.NotEmpty(result.Data.ServerTimings);
    }

    [Fact]
    public async Task BootstrapAsync_成功缓存且forceRefresh提升代际()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("P1", "ITM1", "BC1", "Product One");

        var service = CreateService();
        var request = CreateRequest();
        request.AutoSelectFirst = true;
        var first = await service.BootstrapAsync(request, new List<string> { "B1" });
        var second = await service.BootstrapAsync(request, new List<string> { "B1" });

        Assert.True(first.Success);
        Assert.Same(first.Data, second.Data);

        await InsertProductAsync("P2", "ITM2", "BC2", "Product Two");
        request.ForceRefresh = true;
        var refreshed = await service.BootstrapAsync(request, new List<string> { "B1" });

        Assert.True(refreshed.Success);
        Assert.Equal(2, refreshed.Data!.Candidates.Total);
    }

    [Fact]
    public async Task GetCandidatesAsync_关键词筛选使用字面量判别谓词以命中过滤索引()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("P001", "KI116996", "9328644116996", "Eco Napkin 40x40cm Pk50");
        await InsertProductAsync("P002", "KI200000", "9328644200000", "Paper Straws");

        var sql = new List<string>();
        _db.Aop.OnLogExecuting = (statement, _) => sql.Add(statement);
        var request = CreateRequest();
        request.Filter.Keyword = "ki116996";

        var result = await CreateService().GetCandidatesAsync(
            request,
            new List<string> { "B1" }
        );

        Assert.True(result.Success);
        Assert.Equal(new[] { "P001" }, result.Data!.Items.Select(item => item.ProductCode));
        var productStatements = sql
            .Where(statement => statement.Contains("Product", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(productStatements);
        // 过滤索引只有在判别列为字面量时才会被 SQL Server 选中；参数化即退化为宽表全扫描。
        Assert.All(
            productStatements,
            statement =>
            {
                Assert.Contains("[IsDeleted] = 0 AND [IsActive] = 1", statement);
                Assert.DoesNotContain("@IsDeleted", statement, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("@IsActive", statement, StringComparison.OrdinalIgnoreCase);
            }
        );
    }

    [Fact]
    public async Task GetCandidatesAsync_默认查询在数据库分页且不访问进货表()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP", "Supplier One");
        for (var index = 1; index <= 35; index++)
        {
            await InsertProductAsync(
                $"P{index:D3}",
                $"ITM{index:D3}",
                $"BC{index:D3}",
                $"Product {index:D3}"
            );
        }

        var sql = new List<string>();
        _db.Aop.OnLogExecuting = (statement, _) => sql.Add(statement);
        var request = CreateRequest();
        request.PageNumber = 2;
        request.PageSize = 10;

        var result = await CreateService().GetCandidatesAsync(
            request,
            new List<string> { "B1" }
        );

        Assert.True(result.Success);
        Assert.Equal(35, result.Data!.Total);
        Assert.Equal(10, result.Data.Items.Count);
        Assert.Equal(
            Enumerable.Range(11, 10).Select(index => $"P{index:D3}"),
            result.Data.Items.Select(item => item.ProductCode)
        );
        Assert.DoesNotContain(
            sql,
            statement => statement.Contains("StoreLocalSupplierInvoice", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Contains(
            sql,
            statement =>
                statement.Contains("Product", StringComparison.OrdinalIgnoreCase)
                && (
                    statement.Contains("LIMIT 10 OFFSET 10", StringComparison.OrdinalIgnoreCase)
                    || statement.Contains("LIMIT 10,10", StringComparison.OrdinalIgnoreCase)
                )
        );
    }

    [Fact]
    public async Task GetCandidatesAsync_单据筛选严格按有效授权分店且空白明细店回退表头()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertStoreAsync("B2", "Branch Two");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("P1", "ITM1", "BC1", "Outside Product");
        await InsertProductAsync("P2", "ITM2", "BC2", "Fallback Product");
        await _db.Insertable(
            new[]
            {
                new StoreLocalSupplierInvoice
                {
                    InvoiceGUID = "outside",
                    StoreCode = "B2",
                    SupplierCode = "SUP",
                    InvoiceNo = "MATCH-OUTSIDE",
                    InboundDate = EndDate,
                    IsDeleted = false,
                },
                new StoreLocalSupplierInvoice
                {
                    InvoiceGUID = "fallback",
                    StoreCode = "B1",
                    SupplierCode = "SUP",
                    InvoiceNo = "MATCH-FALLBACK",
                    InboundDate = EndDate,
                    IsDeleted = false,
                },
            }
        ).ExecuteCommandAsync();
        await _db.Insertable(
            new[]
            {
                new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = "outside-detail",
                    InvoiceGUID = "outside",
                    StoreCode = null,
                    ProductCode = "P1",
                    Quantity = 1,
                    IsDeleted = false,
                },
                new StoreLocalSupplierInvoiceDetails
                {
                    DetailGUID = "fallback-detail",
                    InvoiceGUID = "fallback",
                    StoreCode = "  ",
                    ProductCode = "P2",
                    Quantity = 1,
                    IsDeleted = false,
                },
            }
        ).ExecuteCommandAsync();

        var request = CreateRequest();
        request.Filter.DocumentKeyword = "MATCH";
        var result = await CreateService().GetCandidatesAsync(
            request,
            new List<string> { "B1" }
        );

        Assert.True(result.Success);
        var item = Assert.Single(result.Data!.Items);
        Assert.Equal("P2", item.ProductCode);
    }

    [Fact]
    public async Task GetSummaryAsync_空白明细分店回退表头()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("P1", "ITM1", "BC1", "Product One");
        await _db.Insertable(
            new StoreLocalSupplierInvoice
            {
                InvoiceGUID = "h1",
                StoreCode = "B1",
                SupplierCode = "SUP",
                InboundDate = EndDate,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _db.Insertable(
            new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = "d1",
                InvoiceGUID = "h1",
                StoreCode = "  ",
                ProductCode = "P1",
                Quantity = 3,
                Amount = 12,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();

        var result = await CreateService().GetSummaryAsync(
            CreateRequest(),
            new List<string> { "B1" }
        );

        Assert.True(result.Success);
        Assert.Equal(3m, Assert.Single(result.Data!.Items).PurchaseQuantity);
    }

    [Fact]
    public async Task GetProductDailyAsync_同一自然日不同时间在数据库聚合为一行()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("P1", "ITM1", "BC1", "Product One");
        await InsertInvoiceWithDetailAsync("h1", "B1", "SUP", "P1", EndDate.AddHours(8), 2m, 8m);
        await InsertInvoiceWithDetailAsync("h2", "B1", "SUP", "P1", EndDate.AddHours(16), 3m, 12m);
        var request = CreateRequest();
        request.CurrentProductCode = "P1";

        var result = await CreateService().GetProductDailyAsync(
            request,
            new List<string> { "B1" }
        );

        Assert.True(result.Success);
        var day = Assert.Single(result.Data!, row => row.Date.Date == EndDate.Date);
        Assert.Equal(5m, day.PurchaseQuantity);
        Assert.Equal(20m, day.PurchaseAmount);
    }

    [Fact]
    public async Task BootstrapAsync_部分失败不缓存且恢复后可重试成功()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("P1", "ITM1", "BC1", "Product One");
        await InsertInvoiceWithDetailAsync("h1", "B1", "SUP", "P1", EndDate, 2m, 10m);
        _db.DbMaintenance.DropTable(typeof(ProductStoreDailySalesStatistic).Name);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var request = CreateRequest();
        request.AutoSelectFirst = true;

        var partial = await CreateService(cache).BootstrapAsync(
            request,
            new List<string> { "B1" }
        );

        Assert.True(partial.Success);
        Assert.True(partial.Data!.Partial);
        Assert.Contains("summary", partial.Data.SectionErrors.Keys);
        Assert.DoesNotContain("no such table", string.Join(" ", partial.Data.SectionErrors.Values), StringComparison.OrdinalIgnoreCase);

        _db.CodeFirst.InitTables(typeof(ProductStoreDailySalesStatistic));
        var recovered = await CreateService(cache).BootstrapAsync(
            request,
            new List<string> { "B1" }
        );
        Assert.True(recovered.Success);
        Assert.False(recovered.Data!.Partial);
        Assert.Empty(recovered.Data.SectionErrors);
    }

    [Fact]
    public async Task BootstrapAsync_forceRefresh代际跨服务实例共享()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("P1", "ITM1", "BC1", "Product One");
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var request = CreateRequest();
        request.AutoSelectFirst = true;
        var firstService = CreateService(cache);
        var secondService = CreateService(cache);

        var first = await firstService.BootstrapAsync(request, new List<string> { "B1" });
        Assert.Equal(1, first.Data!.Candidates.Total);
        await InsertProductAsync("P2", "ITM2", "BC2", "Product Two");
        request.ForceRefresh = true;
        var refreshed = await firstService.BootstrapAsync(request, new List<string> { "B1" });
        Assert.Equal(2, refreshed.Data!.Candidates.Total);

        request.ForceRefresh = false;
        var observed = await secondService.BootstrapAsync(request, new List<string> { "B1" });
        Assert.Equal(2, observed.Data!.Candidates.Total);
    }

    [Fact]
    public void 快速路径强制索引均纳入健康门控()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string? sourcePath = null;
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "BlazorApp.Api",
                "Services",
                "React",
                "LocalSupplierProductSalesAnalysisService.cs"
            );
            if (File.Exists(candidate))
            {
                sourcePath = candidate;
                break;
            }

            directory = directory.Parent;
        }

        Assert.NotNull(sourcePath);
        var source = File.ReadAllText(sourcePath!);
        var start = source.IndexOf(
            "private async Task EnsureProductKeyHealthAsync()",
            StringComparison.Ordinal
        );
        Assert.True(start >= 0);
        var end = source.IndexOf(
            "private async Task<LocalSupplierProductSalesOptionsDto> ComputeOptionsCoreAsync",
            start,
            StringComparison.Ordinal
        );
        Assert.True(end > start);
        var healthGate = source[start..end];

        Assert.Contains("is_persisted = 1", healthGate, StringComparison.Ordinal);
        foreach (
            var indexName in new[]
            {
                "IX_LSPSA_Product_ProductCode_UUID",
                "IX_LSPSA_Invoice_EffectiveDate_Store_Invoice",
                "IX_LSPSA_Sales_Product_Date",
                "IX_LSPSA_InvoiceDetails_Product_Invoice",
                "IX_LSPSA_InvoiceDetails_Invoice_Product",
                "IX_LSPSA_Sales_Date_Product",
                "IX_ProductStoreDailySalesStatistic_Branch_Product_Date",
                "PK_StoreLocalSupplierInvoice_InvoiceGUID",
            }
        )
        {
            Assert.Contains($"name = N'{indexName}'", healthGate, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GetCandidatesAsync_仅返回有效本地供应商商品主档()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("VALID", "ITEM-OK", "BAR-OK", "Valid Product");
        await _db.Insertable(
            new[]
            {
                new Product
                {
                    UUID = "uuid-deleted",
                    ProductCode = "DELETED",
                    LocalSupplierCode = "SUP",
                    ProductName = "Deleted",
                    IsDeleted = true,
                    IsActive = true,
                },
                new Product
                {
                    UUID = "uuid-inactive",
                    ProductCode = "INACTIVE",
                    LocalSupplierCode = "SUP",
                    ProductName = "Inactive",
                    IsDeleted = false,
                    IsActive = false,
                },
                new Product
                {
                    UUID = "uuid-no-supplier",
                    ProductCode = "NO-SUPPLIER",
                    LocalSupplierCode = "  ",
                    ProductName = "No Supplier",
                    IsDeleted = false,
                    IsActive = true,
                },
                new Product
                {
                    UUID = "uuid-no-code",
                    ProductCode = "  ",
                    LocalSupplierCode = "SUP",
                    ProductName = "No Code",
                    IsDeleted = false,
                    IsActive = true,
                },
            }
        ).ExecuteCommandAsync();

        var result = await CreateService().GetCandidatesAsync(
            CreateRequest(),
            new List<string> { "B1" }
        );

        Assert.True(result.Success);
        Assert.Equal("VALID", Assert.Single(result.Data!.Items).ProductCode);
    }

    [Fact]
    public async Task GetCandidatesAsync_关键字匹配编码货号条码中英文名称且忽略大小写()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP", "Supplier One");
        await _db.Insertable(
            new Product
            {
                UUID = "uuid-local-001",
                ProductCode = "LOCAL-001",
                ItemNumber = "ITEM-KOALA",
                Barcode = "BAR-KOALA",
                ProductName = "考拉玩具",
                EnglishName = "Koala Toy",
                LocalSupplierCode = "SUP",
                IsDeleted = false,
                IsActive = true,
            }
        ).ExecuteCommandAsync();

        foreach (var keyword in new[] { "local-001", "item-koala", "bar-koala", "考拉", "koala toy" })
        {
            var request = CreateRequest();
            request.Filter.Keyword = keyword;
            var result = await CreateService().GetCandidatesAsync(
                request,
                new List<string> { "B1" }
            );
            Assert.True(result.Success);
            Assert.Equal("LOCAL-001", Assert.Single(result.Data!.Items).ProductCode);
        }
    }

    [Fact]
    public async Task GetCandidatesAsync_供应商筛选以商品主档为准而非交易供应商()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP-A", "Supplier A");
        await InsertSupplierAsync("SUP-B", "Supplier B");
        await InsertProductAsync(
            "P1",
            "ITM1",
            "BC1",
            "Product One",
            localSupplierCode: "SUP-A"
        );
        await InsertProductAsync(
            "P2",
            "ITM2",
            "BC2",
            "Product Two",
            localSupplierCode: "SUP-B"
        );
        await InsertInvoiceWithDetailAsync("h1", "B1", "SUP-B", "P1", EndDate, 1m, 5m);

        var request = CreateRequest();
        request.Filter.SupplierCode = "sup-a";
        var result = await CreateService().GetCandidatesAsync(
            request,
            new List<string> { "B1" }
        );

        Assert.True(result.Success);
        Assert.Equal("P1", Assert.Single(result.Data!.Items).ProductCode);
    }

    [Fact]
    public async Task BootstrapAsync_数据库选择裁剪失效编码并迁移被排除当前商品()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("A", "ITM-A", "BC-A", "Product A");
        await InsertProductAsync("B", "ITM-B", "BC-B", "Product B");
        var request = CreateRequest();
        request.Selection = new LocalSupplierProductSalesSelectionDto
        {
            Mode = "allFiltered",
            ExcludedProductCodes = new List<string> { "A", "MISSING" },
        };
        request.CurrentProductCode = "A";
        request.AutoSelectFirst = false;

        var result = await CreateService().BootstrapAsync(
            request,
            new List<string> { "B1" }
        );

        Assert.True(result.Success);
        Assert.False(
            result.Data!.Partial,
            string.Join("; ", result.Data.SectionErrors.Select(error => $"{error.Key}:{error.Value}"))
        );
        Assert.Equal(new[] { "A" }, result.Data.EffectiveSelection.ExcludedProductCodes);
        Assert.Equal("B", result.Data.CurrentProduct!.ProductCode);
        Assert.Equal(new[] { "B" }, result.Data.Summary.Items.Select(row => row.ProductCode));
    }

    [Fact]
    public async Task BootstrapAsync_候选第二页仍从全部筛选结果自动选择首项()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("A", "ITM-A", "BC-A", "Product A");
        await InsertProductAsync("B", "ITM-B", "BC-B", "Product B");
        var request = CreateRequest();
        request.AutoSelectFirst = true;
        request.CandidatePageNumber = 2;
        request.CandidatePageSize = 1;

        var result = await CreateService().BootstrapAsync(
            request,
            new List<string> { "B1" }
        );

        Assert.True(result.Success);
        Assert.Equal("B", Assert.Single(result.Data!.Candidates.Items).ProductCode);
        Assert.Equal("A", result.Data.CurrentProduct!.ProductCode);
    }

    [Fact]
    public async Task GetSummaryAsync_SQL先独立聚合两张事实表再连接商品主档()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("P1", "ITM1", "BC1", "Product One");
        await InsertInvoiceWithDetailAsync("h1", "B1", "SUP", "P1", EndDate, 2m, 10m);
        await InsertSaleAsync(EndDate, "B1", "SUP", "P1", 1, 4m);
        var sql = new List<string>();
        _db.Aop.OnLogExecuting = (statement, _) => sql.Add(statement);

        var result = await CreateService().GetSummaryAsync(
            CreateRequest(),
            new List<string> { "B1" }
        );

        Assert.True(result.Success);
        Assert.Contains(
            sql,
            statement =>
                statement.Contains("StoreLocalSupplierInvoiceDetails", StringComparison.OrdinalIgnoreCase)
                && statement.Contains("ProductStoreDailySalesStatistic", StringComparison.OrdinalIgnoreCase)
                && statement.Contains("LEFT JOIN", StringComparison.OrdinalIgnoreCase)
                && statement.Split("GROUP BY", StringSplitOptions.None).Length >= 4
        );
    }

    [Fact]
    public async Task BootstrapAsync_供应商元数据失败按分段返回且不缓存()
    {
        await InsertStoreAsync("B1", "Branch One");
        await InsertSupplierAsync("SUP", "Supplier One");
        await InsertProductAsync("P1", "ITM1", "BC1", "Product One");
        _db.DbMaintenance.DropTable(typeof(HBLocalSupplier).Name);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var request = CreateRequest();
        request.AutoSelectFirst = true;

        var partial = await CreateService(cache).BootstrapAsync(
            request,
            new List<string> { "B1" }
        );

        Assert.True(partial.Success);
        Assert.True(partial.Data!.Partial);
        Assert.Contains("options", partial.Data.SectionErrors.Keys);
        Assert.Contains("summary", partial.Data.SectionErrors.Keys);
        Assert.DoesNotContain(
            "no such table",
            string.Join(" ", partial.Data.SectionErrors.Values),
            StringComparison.OrdinalIgnoreCase
        );

        _db.CodeFirst.InitTables(typeof(HBLocalSupplier));
        var recovered = await CreateService(cache).BootstrapAsync(
            request,
            new List<string> { "B1" }
        );
        Assert.True(recovered.Success);
        Assert.False(recovered.Data!.Partial);
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (File.Exists(_dbPath))
        {
            try
            {
                File.Delete(_dbPath);
            }
            catch
            {
                // 忽略临时文件清理失败。
            }
        }
    }

    private LocalSupplierProductSalesAnalysisService CreateService(
        IMemoryCache? cache = null,
        ILogger<LocalSupplierProductSalesAnalysisService>? logger = null
    )
    {
        return new LocalSupplierProductSalesAnalysisService(
            CreateSqlSugarContext(_db),
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            logger ?? NullLogger<LocalSupplierProductSalesAnalysisService>.Instance
        );
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public Exception? LastException { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (exception is not null)
            {
                LastException = exception;
            }
        }
    }

    private LocalSupplierProductSalesAnalysisRequest CreateRequest()
    {
        return new LocalSupplierProductSalesAnalysisRequest
        {
            Filter = new LocalSupplierProductSalesAnalysisFilterDto
            {
                StartDate = StartDate,
                EndDate = EndDate,
            },
            Selection = new LocalSupplierProductSalesSelectionDto { Mode = "allFiltered" },
        };
    }

    private async Task InsertStoreAsync(string storeCode, string storeName)
    {
        await _db.Insertable(
            new Store
            {
                StoreGUID = $"guid-{storeCode}",
                StoreCode = storeCode,
                StoreName = storeName,
                IsDeleted = false,
                IsActive = true,
            }
        ).ExecuteCommandAsync();
    }

    private async Task InsertSupplierAsync(string code, string name)
    {
        await _db.Insertable(
            new HBLocalSupplier
            {
                Guid = $"guid-{code}",
                LocalSupplierCode = code,
                Name = name,
                Status = 1,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
    }

    private async Task InsertProductAsync(
        string productCode,
        string itemNumber,
        string barcode,
        string name,
        string? warehouseCategoryGuid = null,
        string localSupplierCode = "SUP"
    )
    {
        await _db.Insertable(
            new Product
            {
                UUID = $"uuid-{productCode}",
                ProductCode = productCode,
                ItemNumber = itemNumber,
                Barcode = barcode,
                ProductName = name,
                ProductImage = $"{productCode}.jpg",
                WarehouseCategoryGUID = warehouseCategoryGuid,
                LocalSupplierCode = localSupplierCode,
                IsDeleted = false,
                IsActive = true,
            }
        ).ExecuteCommandAsync();
    }

    private async Task InsertInvoiceWithDetailAsync(
        string invoiceGuid,
        string storeCode,
        string supplierCode,
        string productCode,
        DateTime inboundDate,
        decimal quantity,
        decimal amount,
        bool headerDeleted = false,
        string? detailStoreCode = null
    )
    {
        await _db.Insertable(
            new StoreLocalSupplierInvoice
            {
                InvoiceGUID = invoiceGuid,
                StoreCode = detailStoreCode ?? storeCode,
                SupplierCode = supplierCode,
                InboundDate = inboundDate,
                IsDeleted = headerDeleted,
            }
        ).ExecuteCommandAsync();
        await _db.Insertable(
            new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = $"detail-{invoiceGuid}",
                InvoiceGUID = invoiceGuid,
                StoreCode = storeCode,
                ProductCode = productCode,
                Quantity = quantity,
                Amount = amount,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
    }

    private async Task InsertSaleAsync(
        DateTime date,
        string branchCode,
        string supplierCode,
        string productCode,
        int quantity,
        decimal amount
    )
    {
        await _db.Insertable(
            new ProductStoreDailySalesStatistic
            {
                Date = date.Date,
                BranchCode = branchCode,
                SupplierCode = supplierCode,
                ProductCode = productCode,
                TotalQuantity = quantity,
                TotalAmount = amount,
            }
        ).ExecuteCommandAsync();
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)
            RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }
}
