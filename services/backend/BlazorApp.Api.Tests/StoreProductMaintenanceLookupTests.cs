using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class StoreProductMaintenanceLookupTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"lookup-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly StoreProductMaintenanceReactService _service;

    public StoreProductMaintenanceLookupTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_dbPath}",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(
            typeof(Product), typeof(ProductGrade), typeof(ProductSetCode), typeof(StoreClearancePrice),
            typeof(Store), typeof(StoreRetailPrice), typeof(StoreMultiCodeProduct), typeof(HBLocalSupplier)
        );
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, _db);
        _service = new StoreProductMaintenanceReactService(
            context, NullLogger<StoreProductMaintenanceReactService>.Instance,
            Mock.Of<IAutoPricingService>(), _cache, Mock.Of<IWarehouseProductChangeHistoryService>(),
            Mock.Of<ICurrentUserService>(), Mock.Of<IProductMaintenanceHqProjectionWriter>());
    }

    public static IEnumerable<object[]> SpacePairs()
    {
        foreach (var space in "\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u202f\u205f\u3000")
        {
            yield return new object[] { $"ttete{space}stest", "ttete stest" };
            yield return new object[] { "ttete stest", $"ttete{space}stest" };
        }
    }

    [Theory]
    [MemberData(nameof(SpacePairs))]
    [InlineData("ttete\u2006\u00a0stest", "ttete  stest")]
    [InlineData("ttete\u2006middle stest", "ttete middle\u3000stest")]
    public async Task Lookup_不同空格双向匹配且保留原货号(string stored, string query)
    {
        await SeedProduct("product", stored);
        var item = Assert.Single(await Lookup(query));
        Assert.Equal("product", item.ProductCode);
        Assert.Equal("ItemNumber", item.MatchSource);
        Assert.Equal(stored, item.ItemNumber);
        Assert.Equal(stored, (await _db.Queryable<Product>().SingleAsync()).ItemNumber);
    }

    [Fact]
    public async Task Lookup_返回全部等价货号且排除删除商品()
    {
        await SeedProduct("ordinary", "ttete stest");
        await SeedProduct("unicode", "ttete\u2006stest");
        await SeedProduct("deleted", "ttete\u2006stest", deleted: true);
        var matches = await Lookup("ttete stest");
        Assert.Equal(new[] { "ordinary", "unicode" }, matches.Select(x => x.ProductCode).OrderBy(x => x));
    }

    [Theory]
    [InlineData("ttetestest")]
    [InlineData("ttete  stest")]
    [InlineData("ttete\tstest")]
    [InlineData("ttete\nstest")]
    [InlineData("ttete\u200bstest")]
    public async Task Lookup_不合并空格或放宽其他字符(string query)
    {
        await SeedProduct("product", "ttete\u2006stest");
        Assert.Empty(await Lookup(query));
    }

    [Theory]
    [InlineData("A%_[\\'", "Aother")]
    [InlineData("ABC", "ABCD")]
    public async Task Lookup_货号特殊字符按字面值匹配(string prefix, string otherPrefix)
    {
        await SeedProduct("expected", prefix + "\u2006B");
        await SeedProduct("other", otherPrefix + " B");
        Assert.Equal("expected", Assert.Single(await Lookup(prefix + " B")).ProductCode);
    }

    [Fact]
    public async Task Lookup_条码保持精确匹配并保留清货门店范围()
    {
        await SeedProduct("product", "item", "bar\u2006code");
        await _db.Insertable(new ProductSetCode { ProductCode = "product", SetBarcode = "set\u2006code" }).ExecuteCommandAsync();
        await _db.Insertable(new StoreClearancePrice { ProductCode = "product", StoreCode = "allowed", ClearanceBarcode = "clear\u2006code" }).ExecuteCommandAsync();
        Assert.Equal("ProductBarcode", Assert.Single(await Lookup("bar\u2006code")).MatchSource);
        Assert.Equal("SetBarcode", Assert.Single(await Lookup("set\u2006code")).MatchSource);
        Assert.Equal("ClearanceBarcode", Assert.Single(await Lookup("clear\u2006code")).MatchSource);
        Assert.Empty(await Lookup("bar code"));
        Assert.Empty(await Lookup("set code"));
        Assert.Empty(await Lookup("clear code"));
        Assert.Empty(await Lookup("clear\u2006code", new List<string> { "other" }));
        Assert.Empty(await Lookup("item", new List<string>()));
    }

    [Fact]
    public async Task Lookup_普通无空格货号和条码保持可查询()
    {
        await SeedProduct("product", "ITEM123", "9529260910023");
        Assert.Equal("ItemNumber", Assert.Single(await Lookup("  ITEM123  ")).MatchSource);
        Assert.Equal("ProductBarcode", Assert.Single(await Lookup("9529260910023")).MatchSource);
    }

    [Fact]
    public async Task Lookup_一次往返返回所有命中来源及最新商品资料()
    {
        await SeedProduct("product", "shared-code", "shared-code");
        await _db.Insertable(new ProductGrade { ProductCode = "product", Grade = "A" }).ExecuteCommandAsync();
        await _db.Insertable(new ProductSetCode
        {
            ProductCode = "product", SetItemNumber = "set-item", SetBarcode = "shared-code",
        }).ExecuteCommandAsync();
        await _db.Insertable(new StoreClearancePrice
        {
            ProductCode = "product", StoreCode = "allowed", ClearanceBarcode = "shared-code",
        }).ExecuteCommandAsync();

        var queries = 0;
        _db.Aop.OnLogExecuting = (_, _) => queries++;
        var matches = await Lookup("shared-code");
        Assert.Equal(1, queries);
        Assert.Equal(4, matches.Count);
        Assert.All(matches, item =>
        {
            Assert.Equal("product", item.ProductName);
            Assert.Equal("A", item.Grade);
        });
        Assert.Equal("set-item", matches.Single(item => item.MatchSource == "SetBarcode").ItemNumber);
        Assert.Equal("shared-code", matches.Single(item => item.MatchSource == "ClearanceBarcode").ItemNumber);

        await _db.Updateable<Product>().SetColumns(product => product.ProductName == "new-name")
            .Where(product => product.ProductCode == "product").ExecuteCommandAsync();
        queries = 0;
        Assert.All(await Lookup("shared-code"), item => Assert.Equal("new-name", item.ProductName));
        Assert.Equal(1, queries);
    }

    [Fact]
    public async Task Lookup_排除孤立和已删除商品的套码及清货码()
    {
        await SeedProduct("deleted", "item", deleted: true);
        foreach (var code in new[] { "deleted", "missing" })
        {
            await _db.Insertable(new ProductSetCode { ProductCode = code, SetBarcode = "shared-code" }).ExecuteCommandAsync();
            await _db.Insertable(new StoreClearancePrice
            {
                ProductCode = code, StoreCode = "allowed", ClearanceBarcode = "shared-code",
            }).ExecuteCommandAsync();
        }
        var queries = 0;
        _db.Aop.OnLogExecuting = (_, _) => queries++;
        Assert.Empty(await Lookup("shared-code"));
        Assert.Equal(1, queries);
    }

    [Fact]
    public async Task Lookup_无门店权限时不执行商品查询()
    {
        var queries = 0;
        _db.Aop.OnLogExecuting = (_, _) => queries++;
        Assert.Empty(await Lookup("shared-code", new List<string>()));
        Assert.Equal(0, queries);
    }

    [Fact]
    public async Task ScanLabel_多商品歧义只返回候选且不自动选择()
    {
        await SeedProduct("product", "ITEM123", "9529260910023");
        await SeedProduct("product-2", "ITEM456", "9529260910023");
        var response = await _service.ScanLabelAsync(
            new StoreProductLookupRequestDto { Keyword = "9529260910023" },
            null
        );

        Assert.True(response.Success, response.Message);
        Assert.NotNull(response.Data);
        Assert.Equal(2, response.Data!.Candidates.Count);
        Assert.Null(response.Data.Detail);
        Assert.Null(response.Data.PrintTarget);
    }

    [Fact]
    public async Task ScanLabel_主商品有门店价返回原价和折扣率打印目标()
    {
        var detail = new StoreProductDetailDto
        {
            ProductCode = "product",
            Barcode = "9529260910023",
            StorePrice = new StoreProductStorePriceDto
            {
                Uuid = "price-1",
                StoreCode = "allowed",
                RetailPrice = 1.50m,
                DiscountRate = 0.80m,
            },
        };
        var method = typeof(StoreProductMaintenanceReactService).GetMethod(
            "BuildScanLabelPrintTargetAsync",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(method);
        var task = (Task<StoreProductPrintTargetDto?>)method!.Invoke(
            _service,
            new object?[] { "9529260910023", "ProductBarcode", detail, "allowed", new List<string> { "allowed" } }
        )!;
        var target = await task;

        Assert.NotNull(target);
        Assert.Equal("product", target!.Kind);
        Assert.Equal("9529260910023", target.Barcode);
        Assert.Equal(1.50m, target.RetailPrice);
        Assert.Equal(0.80m, target.DiscountRate);
        Assert.Equal("price-1", target.CodeId);
    }

    [Fact]
    public async Task ScanLabel_多码套码为空时使用SetCodeId匹配门店投影()
    {
        await _db.Insertable(new ProductSetCode
        {
            SetCodeId = "set-1", ProductCode = "product-2", SetProductCode = string.Empty,
            SetBarcode = "set-barcode", SetRetailPrice = 4m, IsActive = true,
        }).ExecuteCommandAsync();
        await _db.Insertable(new StoreMultiCodeProduct
        {
            UUID = "multi-1", StoreCode = "allowed", ProductCode = "product-2",
            MultiCodeProductCode = "set-1", MultiBarcode = "set-barcode",
            MultiCodeRetailPrice = 3.20m, DiscountRate = 0.75m, IsActive = true,
        }).ExecuteCommandAsync();

        // 多码标签取主档门店价与主档折扣，不取投影行的 MultiCodeRetailPrice / DiscountRate。
        var detail = new StoreProductDetailDto
        {
            ProductCode = "product-2",
            ProductType = 2,
            StorePrice = new StoreProductStorePriceDto
            {
                Uuid = "price-2", StoreCode = "allowed", RetailPrice = 5.00m, DiscountRate = 0.10m,
            },
        };
        var method = typeof(StoreProductMaintenanceReactService).GetMethod(
            "BuildScanLabelPrintTargetAsync", BindingFlags.Instance | BindingFlags.NonPublic
        );
        var task = (Task<StoreProductPrintTargetDto?>)method!.Invoke(
            _service,
            new object?[] { "set-barcode", "SetBarcode", detail, "allowed", new List<string> { "allowed" } }
        )!;
        var target = await task;

        Assert.NotNull(target);
        Assert.Equal("multi", target!.Kind);
        Assert.Equal("multi-1", target.CodeId);
        Assert.Equal(5.00m, target.RetailPrice);
        Assert.Equal(0.10m, target.DiscountRate);

        await _db.Updateable<StoreMultiCodeProduct>()
            .SetColumns(x => new StoreMultiCodeProduct { IsActive = false })
            .Where(x => x.UUID == "multi-1")
            .ExecuteCommandAsync();
        var inactiveTask = (Task<StoreProductPrintTargetDto?>)method.Invoke(
            _service,
            new object?[] { "set-barcode", "SetBarcode", detail, "allowed", new List<string> { "allowed" } }
        )!;
        Assert.Null(await inactiveTask);

        await _db.Updateable<StoreMultiCodeProduct>()
            .SetColumns(x => new StoreMultiCodeProduct { IsActive = true, MultiBarcode = "new-barcode" })
            .Where(x => x.UUID == "multi-1")
            .ExecuteCommandAsync();
        var oldBarcodeTask = (Task<StoreProductPrintTargetDto?>)method.Invoke(
            _service,
            new object?[] { "set-barcode", "SetBarcode", detail, "allowed", new List<string> { "allowed" } }
        )!;
        Assert.Null(await oldBarcodeTask);
    }

    [Fact]
    public async Task ScanLabel_套装子项打印门店套装价且不带折扣()
    {
        await _db.Insertable(new ProductSetCode
        {
            SetCodeId = "set-9", ProductCode = "product-3", SetProductCode = "set-product-9",
            SetBarcode = "set-barcode-9", SetRetailPrice = 4m, IsActive = true,
        }).ExecuteCommandAsync();

        var detail = new StoreProductDetailDto
        {
            ProductCode = "product-3",
            ProductType = 1,
            StorePrice = new StoreProductStorePriceDto
            {
                Uuid = "price-3", StoreCode = "allowed", RetailPrice = 9.99m, DiscountRate = 0.30m,
            },
        };
        var method = typeof(StoreProductMaintenanceReactService).GetMethod(
            "BuildScanLabelPrintTargetAsync", BindingFlags.Instance | BindingFlags.NonPublic
        );
        Task<StoreProductPrintTargetDto?> Build() => (Task<StoreProductPrintTargetDto?>)method!.Invoke(
            _service,
            new object?[] { "set-barcode-9", "SetBarcode", detail, "allowed", new List<string> { "allowed" } }
        )!;

        // 无门店套装行：全局套装价，不带主档折扣。
        var globalTarget = await Build();
        Assert.NotNull(globalTarget);
        Assert.Equal("set", globalTarget!.Kind);
        Assert.Equal("set-barcode-9", globalTarget.Barcode);
        Assert.Equal("set-9", globalTarget.CodeId);
        Assert.Equal(4m, globalTarget.RetailPrice);
        Assert.Null(globalTarget.DiscountRate);

        // 有门店套装行：用门店套装价（与收银端一致），仍不带折扣。
        await _db.Insertable(new StoreMultiCodeProduct
        {
            UUID = "store-set-9", StoreCode = "allowed", ProductCode = "product-3",
            MultiCodeProductCode = "set-product-9", MultiBarcode = "set-barcode-9",
            MultiCodeRetailPrice = 3.50m, DiscountRate = 0.20m, IsActive = true,
        }).ExecuteCommandAsync();
        var storeTarget = await Build();
        Assert.NotNull(storeTarget);
        Assert.Equal("set", storeTarget!.Kind);
        Assert.Equal(3.50m, storeTarget.RetailPrice);
        Assert.Null(storeTarget.DiscountRate);
    }

    private Task<int> SeedProduct(string code, string itemNumber, string? barcode = null, bool deleted = false) =>
        _db.Insertable(new Product { ProductCode = code, ProductName = code, ItemNumber = itemNumber, Barcode = barcode, IsDeleted = deleted }).ExecuteCommandAsync();

    private async Task<List<StoreProductLookupItemDto>> Lookup(string query, List<string>? scope = null)
    {
        var response = await _service.LookupAsync(new StoreProductLookupRequestDto { Keyword = query }, scope ?? new List<string> { "allowed" });
        Assert.True(response.Success, response.Message);
        return Assert.IsType<List<StoreProductLookupItemDto>>(response.Data);
    }

    public void Dispose()
    {
        _db.Dispose();
        _cache.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }
}
