using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using AutoMapper;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Mappings.Profiles.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.Products;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 两个直接新建商品的入口（POST /products 的 CreateAsync、POST /products/create-with-prices）的条码防重：
/// 条码被存活商品占用（主条码 / 分店多码，不限供应商）时拒绝新建，409 + 错误码 + 已有商品清单，
/// 由客户端提示用户选用已有商品，不提供「仍然创建」。
/// </summary>
public sealed class ProductDuplicateCreateEntryPointTests : IDisposable
{
    private const string OccupiedBarcode = "9525811960003";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public ProductDuplicateCreateEntryPointTests()
    {
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
            typeof(Product),
            typeof(Store),
            typeof(StoreRetailPrice),
            typeof(StoreMultiCodeProduct),
            typeof(ProductSetCode),
            typeof(DomesticProduct),
            typeof(ChinaSupplier),
            typeof(UserStore),
            typeof(WarehouseCategory),
            typeof(LocalSupplierCategory),
            typeof(LocalSupplierCategoryCapture),
            typeof(LocalSupplierCategoryProductAssignment)
        );
    }

    [Fact]
    public async Task CreateAsync_条码被其他供应商商品占用_拒绝新建并带回已有商品清单()
    {
        await SeedProductAsync("OLD-1", "SUP02", "HB196-021", "Non slip Mat", OccupiedBarcode);

        var response = await CreateProductService().CreateAsync(NewProductDto("NEW-1", "SUP01", "HB196-021", OccupiedBarcode));

        Assert.False(response.Success);
        Assert.Equal(ProductDuplicateGuard.BarcodeExistsErrorCode, response.ErrorCode);
        Assert.Contains("已属于商品", response.Message);
        Assert.Contains("选用已有商品", response.Message);
        var existing = Assert.IsType<object[]>(response.Details);
        var json = ToJson(existing);
        Assert.Contains("\"productCode\":\"OLD-1\"", json);
        Assert.Contains("\"localSupplierCode\":\"SUP02\"", json);
        Assert.Equal(1, await _db.Queryable<Product>().CountAsync());
    }

    [Fact]
    public async Task CreateAsync_条码被分店多码占用_拒绝新建()
    {
        await SeedProductAsync("MAIN-1", "SUP02", "M1", "有多码的商品", "9300000000100");
        await SeedMultiCodeAsync("MAIN-1", OccupiedBarcode);

        var response = await CreateProductService().CreateAsync(NewProductDto("NEW-1", "SUP01", "X1", OccupiedBarcode));

        Assert.False(response.Success);
        Assert.Equal(ProductDuplicateGuard.BarcodeExistsErrorCode, response.ErrorCode);
        Assert.Equal(1, await _db.Queryable<Product>().CountAsync());
    }

    [Fact]
    public async Task CreateAsync_同供应商下货号已存在_拒绝新建()
    {
        await SeedProductAsync("OLD-1", "SUP01", "SAME-ITEM", "已有商品", "9300000000200");

        var response = await CreateProductService().CreateAsync(NewProductDto("NEW-1", "SUP01", "SAME-ITEM", "9300000000201"));

        Assert.False(response.Success);
        Assert.Equal(ProductDuplicateGuard.ItemNumberExistsErrorCode, response.ErrorCode);
        Assert.Equal(1, await _db.Queryable<Product>().CountAsync());
    }

    [Fact]
    public async Task CreateAsync_条码和货号都没有冲突_正常新建()
    {
        await SeedProductAsync("OLD-1", "SUP02", "OTHER", "别的商品", "9300000000300");
        await SeedStoreAsync("S01");

        // 货号与别的供应商相同是允许的（只有同供应商同货号才冲突）。
        var response = await CreateProductService().CreateAsync(NewProductDto("NEW-1", "SUP01", "OTHER", "9300000000301"));

        Assert.True(response.Success, $"{response.ErrorCode} {response.Message}");
        Assert.True(await _db.Queryable<Product>().AnyAsync(x => x.ProductCode == "NEW-1" && x.Barcode == "9300000000301"));
    }

    [Fact]
    public async Task Create控制器_查重冲突映射为409并带错误码和已有商品()
    {
        var owners = new[]
        {
            new ProductBarcodeOwner("OLD-1", "HB196-021", "Non slip Mat", "SUP02", ProductDuplicateGuard.SourceMainBarcode),
        };
        var service = new Mock<IProductReactService>();
        service.Setup(x => x.CreateAsync(It.IsAny<CreateProductDto>())).ReturnsAsync(
            ApiResponse<ProductDto>.Error(
                ProductDuplicateGuard.BuildBarcodeConflictMessage(OccupiedBarcode, owners),
                ProductDuplicateGuard.BarcodeExistsErrorCode,
                ProductDuplicateGuard.ToResponsePayload(owners)
            )
        );
        var controller = new ReactProductController(
            service.Object,
            Mock.Of<IProductStoreSyncService>(),
            Mock.Of<IProductHqSyncService>(),
            Mock.Of<ICurrentUserManageableStoreScopeService>(),
            NullLogger<ReactProductController>.Instance,
            Mock.Of<ICurrentUserService>()
        );

        var result = await controller.Create(NewProductDto("NEW-1", "SUP01", "HB196-021", OccupiedBarcode));

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var json = ToJson(conflict.Value);
        Assert.Contains("\"errorCode\":\"BARCODE_EXISTS\"", json);
        Assert.Contains("\"productCode\":\"OLD-1\"", json);
        Assert.Contains("\"success\":false", json);
    }

    [Fact]
    public async Task CreateWithPrices_普通条码被其他供应商商品占用_返回409带已有商品清单()
    {
        // 普通条码（不是 9529 开头的自动条码）以前完全不查占用，手持/Web 手填可以跨供应商建出同条码的第二条。
        await SeedProductAsync("OLD-1", "SUP02", "HB196-021", "Non slip Mat", OccupiedBarcode);

        var result = await CreateProductsController().CreateWithPrices(NewWithPricesDto("SUP01", "HB196-021", OccupiedBarcode));

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var json = ToJson(conflict.Value);
        Assert.Contains("\"errorCode\":\"BARCODE_EXISTS\"", json);
        Assert.Contains("\"productCode\":\"OLD-1\"", json);
        Assert.Contains("\"itemNumber\":\"HB196-021\"", json);
        Assert.Contains("选用已有商品", json);
        Assert.Equal(1, await _db.Queryable<Product>().CountAsync());
    }

    [Fact]
    public async Task CreateWithPrices_条码被分店多码占用_返回409()
    {
        await SeedProductAsync("MAIN-1", "SUP02", "M1", "有多码的商品", "9300000000100");
        await SeedMultiCodeAsync("MAIN-1", OccupiedBarcode);

        var result = await CreateProductsController().CreateWithPrices(NewWithPricesDto("SUP01", "X1", $" {OccupiedBarcode} "));

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Contains("\"errorCode\":\"BARCODE_EXISTS\"", ToJson(conflict.Value));
        Assert.Equal(1, await _db.Queryable<Product>().CountAsync());
    }

    [Fact]
    public async Task CreateWithPrices_条码只被已软删商品占用_不拦截()
    {
        await SeedProductAsync("GONE-1", "SUP02", "HB196-021", "已删商品", OccupiedBarcode, isDeleted: true);

        var result = await CreateProductsController().CreateWithPrices(NewWithPricesDto("SUP01", "HB196-021", OccupiedBarcode));

        // 通过查重后继续走原有创建流程；这里只断言没有被条码防重 409 拦下。
        var conflict = result as ConflictObjectResult;
        Assert.True(
            conflict == null || !ToJson(conflict.Value).Contains("BARCODE_EXISTS"),
            "已软删商品的条码不应占用"
        );
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    // 默认序列化会把中文转成 \uXXXX，断言文案前先用不转义的编码器。
    private static string ToJson(object? value) =>
        JsonSerializer.Serialize(
            value,
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }
        );

    private static CreateProductDto NewProductDto(string productCode, string supplierCode, string itemNumber, string barcode) =>
        new()
        {
            ProductCode = productCode,
            LocalSupplierCode = supplierCode,
            ItemNumber = itemNumber,
            Barcode = barcode,
            ProductName = "新商品",
            ProductType = 0,
            PurchasePrice = 1m,
            RetailPrice = 2m,
            IsActive = true,
        };

    private static CreateProductWithPricesDto NewWithPricesDto(string supplierCode, string itemNumber, string barcode) =>
        new()
        {
            ProductName = "新商品",
            LocalSupplierCode = supplierCode,
            ItemNumber = itemNumber,
            Barcode = barcode,
            PurchasePrice = 1m,
            RetailPrice = 2m,
            IsAutoPricing = false,
        };

    private async Task SeedProductAsync(
        string productCode,
        string supplierCode,
        string itemNumber,
        string productName,
        string barcode,
        bool isDeleted = false
    )
    {
        var now = DateTime.UtcNow;
        await _db.Insertable(new Product
        {
            UUID = $"uuid-{productCode}",
            ProductCode = productCode,
            LocalSupplierCode = supplierCode,
            ItemNumber = itemNumber,
            ProductName = productName,
            Barcode = barcode,
            ProductType = 0,
            PurchasePrice = 1m,
            RetailPrice = 2m,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
            IsDeleted = isDeleted,
        }).ExecuteCommandAsync();
    }

    private async Task SeedMultiCodeAsync(string productCode, string barcode)
    {
        var now = DateTime.UtcNow;
        await _db.Insertable(new StoreMultiCodeProduct
        {
            UUID = $"mc-{productCode}",
            StoreCode = "S01",
            ProductCode = productCode,
            MultiCodeProductCode = $"multi-{productCode}",
            StoreMultiCodeProductCode = $"S01multi-{productCode}",
            MultiBarcode = barcode,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task SeedStoreAsync(string storeCode)
    {
        await _db.Insertable(new Store
        {
            StoreGUID = $"store-{storeCode}",
            StoreCode = storeCode,
            StoreName = $"分店{storeCode}",
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private ProductReactService CreateProductService() =>
        new(
            CreateSqlSugarContext(_db),
            (HqSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HqSqlSugarContext)),
            new MapperConfiguration(cfg => cfg.AddProfile<ReactProductMappingProfile>(), NullLoggerFactory.Instance)
                .CreateMapper(),
            NullLogger<ProductReactService>.Instance,
            new HttpContextAccessor(),
            new ProductAuditNoopHistoryService(),
            new ProductAuditSystemCurrentUserService()
        );

    private ReactProductsController CreateProductsController() =>
        new(
            CreateSqlSugarContext(_db),
            NullLogger<ReactProductsController>.Instance,
            WarehouseProductChangeHistoryTestDouble.CreateNoop(),
            Mock.Of<ICurrentUserService>(),
            Mock.Of<IProductMaintenanceHqProjectionWriter>()
        );

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }
}
