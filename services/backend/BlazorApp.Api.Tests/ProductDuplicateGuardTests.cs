using BlazorApp.Api.Services.Products;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 新建商品的条码/货号查重守卫：条码占用 = 存活商品主条码 + 存活分店多码 + 存活一品多码（SetType=2），
/// 不限供应商；已软删商品不占用。背景见 ProductDuplicateGuard 的类注释。
/// </summary>
public sealed class ProductDuplicateGuardTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public ProductDuplicateGuardTests()
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
        _db.CodeFirst.InitTables(typeof(Product), typeof(StoreMultiCodeProduct), typeof(ProductSetCode));
    }

    [Fact]
    public async Task 主条码被其他供应商的存活商品占用_返回占用者摘要()
    {
        await SeedProductAsync("P-OLD", "SUP02", "HB196-021", "Non slip Mat", "9525811960003");

        var owners = await ProductDuplicateGuard.FindBarcodeOwnersAsync(_db, new[] { "9525811960003" });

        var owner = Assert.Single(owners["9525811960003"]);
        Assert.Equal("P-OLD", owner.ProductCode);
        Assert.Equal("HB196-021", owner.ItemNumber);
        Assert.Equal("Non slip Mat", owner.ProductName);
        Assert.Equal("SUP02", owner.LocalSupplierCode);
        Assert.Equal(ProductDuplicateGuard.SourceMainBarcode, owner.Source);
    }

    [Fact]
    public async Task 条码比较忽略大小写和首尾空白()
    {
        await SeedProductAsync("P-ABC", "SUP01", "A1", "字母条码商品", "ABC123");

        var owners = await ProductDuplicateGuard.FindBarcodeOwnersAsync(_db, new[] { "  abc123 " });

        Assert.True(owners.ContainsKey("abc123"));
        Assert.Equal("P-ABC", Assert.Single(owners["abc123"]).ProductCode);
    }

    [Fact]
    public async Task 已软删商品的条码不占用()
    {
        await SeedProductAsync("P-DEL", "SUP02", "X1", "已删商品", "9300000000011", isDeleted: true);

        var owners = await ProductDuplicateGuard.FindBarcodeOwnersAsync(_db, new[] { "9300000000011" });

        Assert.Empty(owners);
    }

    [Fact]
    public async Task 分店多码占用时占用者是主档商品_主档已删则不占用()
    {
        await SeedProductAsync("P-MAIN", "SUP02", "M1", "主档商品", "9300000000100");
        await SeedProductAsync("P-GONE", "SUP02", "M2", "已删主档", "9300000000200", isDeleted: true);
        await SeedMultiCodeAsync("mc-1", "S01", "P-MAIN", "9300000000999");
        await SeedMultiCodeAsync("mc-2", "S01", "P-GONE", "9300000000888");
        await SeedMultiCodeAsync("mc-3", "S01", "P-MAIN", "9300000000777", isDeleted: true);

        var owners = await ProductDuplicateGuard.FindBarcodeOwnersAsync(
            _db,
            new[] { "9300000000999", "9300000000888", "9300000000777" }
        );

        var owner = Assert.Single(owners["9300000000999"]);
        Assert.Equal("P-MAIN", owner.ProductCode);
        Assert.Equal(ProductDuplicateGuard.SourceStoreMultiCode, owner.Source);
        Assert.False(owners.ContainsKey("9300000000888"));
        Assert.False(owners.ContainsKey("9300000000777"));
    }

    [Fact]
    public async Task 一品多码只有SetType为2时才占用条码()
    {
        await SeedProductAsync("P-SET", "SUP02", "S1", "多码主档", "9300000001000");
        await SeedSetCodeAsync("sc-single", "P-SET", "9300000001111", setType: 2);
        await SeedSetCodeAsync("sc-child", "P-SET", "9300000001222", setType: 1);

        var owners = await ProductDuplicateGuard.FindBarcodeOwnersAsync(
            _db,
            new[] { "9300000001111", "9300000001222" }
        );

        Assert.Equal(ProductDuplicateGuard.SourceProductSetCode, Assert.Single(owners["9300000001111"]).Source);
        Assert.False(owners.ContainsKey("9300000001222"));
    }

    [Fact]
    public async Task 同一商品的主条码和多码相同时只记一个占用者()
    {
        await SeedProductAsync("P-DUP", "SUP02", "D1", "自带多码", "9300000002000");
        await SeedMultiCodeAsync("mc-same", "S01", "P-DUP", "9300000002000");

        var owners = await ProductDuplicateGuard.FindBarcodeOwnersAsync(_db, new[] { "9300000002000" });

        var owner = Assert.Single(owners["9300000002000"]);
        Assert.Equal(ProductDuplicateGuard.SourceMainBarcode, owner.Source);
    }

    [Fact]
    public async Task 排除商品自身后不再算占用()
    {
        await SeedProductAsync("P-SELF", "SUP02", "E1", "自己", "9300000003000");

        var owners = await ProductDuplicateGuard.FindBarcodeOwnersAsync(
            _db,
            new[] { "9300000003000" },
            excludeProductCode: "P-SELF"
        );

        Assert.Empty(owners);
    }

    [Fact]
    public async Task 空白条码被忽略_重复入参只查一次()
    {
        await SeedProductAsync("P-1", "SUP02", "I1", "一号", "9300000004000");

        var owners = await ProductDuplicateGuard.FindBarcodeOwnersAsync(
            _db,
            new string?[] { null, "", "   ", "9300000004000", "9300000004000" }
        );

        Assert.Single(owners);
        Assert.Single(owners["9300000004000"]);
    }

    [Fact]
    public async Task 条码超过一个分块时仍能命中后面的占用者()
    {
        // 分块大小 200：第 250 个条码落在第二块。
        var barcodes = Enumerable.Range(0, 250).Select(index => $"93100{index:D8}").ToList();
        await SeedProductAsync("P-LATE", "SUP02", "L1", "靠后的商品", barcodes[249]);

        var owners = await ProductDuplicateGuard.FindBarcodeOwnersAsync(_db, barcodes);

        Assert.Equal("P-LATE", Assert.Single(owners[barcodes[249]]).ProductCode);
        Assert.Single(owners);
    }

    [Fact]
    public async Task 同供应商货号查重只看存活商品且不跨供应商()
    {
        await SeedProductAsync("P-A", "SUP01", "ITEM-1", "甲", "9300000005001");
        await SeedProductAsync("P-B", "SUP01", "ITEM-DEL", "乙", "9300000005002", isDeleted: true);

        Assert.True(await ProductDuplicateGuard.HasSupplierItemNumberAsync(_db, "SUP01", " ITEM-1 "));
        Assert.False(await ProductDuplicateGuard.HasSupplierItemNumberAsync(_db, "SUP02", "ITEM-1"));
        Assert.False(await ProductDuplicateGuard.HasSupplierItemNumberAsync(_db, "SUP01", "ITEM-DEL"));
        Assert.False(await ProductDuplicateGuard.HasSupplierItemNumberAsync(_db, "SUP01", "  "));
    }

    [Fact]
    public void 冲突文案最多列三个占用商品并提示选用已有商品()
    {
        var owners = Enumerable.Range(1, 5)
            .Select(index => new ProductBarcodeOwner($"P{index}", $"IT{index}", $"名称{index}", "SUP02", "MainBarcode"))
            .ToList();

        var message = ProductDuplicateGuard.BuildBarcodeConflictMessage("9300000006000", owners);

        Assert.Contains("条码 9300000006000 已属于商品", message);
        Assert.Contains("货号 IT1 · 名称1（供应商 SUP02）", message);
        Assert.Contains("货号 IT3 · 名称3（供应商 SUP02）", message);
        Assert.DoesNotContain("IT4", message);
        Assert.Contains("等 5 个商品", message);
        Assert.Contains("选用已有商品", message);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

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

    private async Task SeedMultiCodeAsync(string uuid, string storeCode, string productCode, string barcode, bool isDeleted = false)
    {
        var now = DateTime.UtcNow;
        await _db.Insertable(new StoreMultiCodeProduct
        {
            UUID = uuid,
            StoreCode = storeCode,
            ProductCode = productCode,
            MultiCodeProductCode = $"multi-{uuid}",
            StoreMultiCodeProductCode = $"{storeCode}multi-{uuid}",
            MultiBarcode = barcode,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
            IsDeleted = isDeleted,
        }).ExecuteCommandAsync();
    }

    private async Task SeedSetCodeAsync(string setCodeId, string productCode, string barcode, int setType)
    {
        var now = DateTime.UtcNow;
        await _db.Insertable(new ProductSetCode
        {
            SetCodeId = setCodeId,
            ProductCode = productCode,
            SetProductCode = $"set-{setCodeId}",
            SetItemNumber = $"set-item-{setCodeId}",
            SetBarcode = barcode,
            SetQuantity = 1,
            SetType = setType,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }
}
