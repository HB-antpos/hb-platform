using BlazorApp.Api.Features.WarehousePicking;
using BlazorApp.Shared.Models;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>拣货「扫到不在本单的码」提示：iOS 相机丢前导 0 时精确无结果才回退变体。</summary>
public sealed class WarehousePickingCodeLookupVariantTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"pick-lookup-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;

    public WarehousePickingCodeLookupVariantTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_dbPath}",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(
            typeof(Product), typeof(ProductSetCode), typeof(ProductLocation), typeof(Location)
        );
    }

    [Theory]
    [InlineData("0810812024527", "810812024527")]
    [InlineData("810812024527", "0810812024527")]
    public async Task 条码前导零写法不同时回退匹配(string stored, string scanned)
    {
        await SeedProduct("P1", "ITEM-1", stored);

        var hit = await WarehousePickingQueries.LookupProductByCodeAsync(_db, scanned);

        Assert.Equal("P1", hit?.ProductCode);
    }

    [Fact]
    public async Task 精确命中优先_12位与13位分属不同商品时各自命中自己()
    {
        await SeedProduct("P12", "A12", "661799090520");
        await SeedProduct("P13", "A13", "0661799090520");

        Assert.Equal("P12", (await WarehousePickingQueries.LookupProductByCodeAsync(_db, "661799090520"))?.ProductCode);
        Assert.Equal("P13", (await WarehousePickingQueries.LookupProductByCodeAsync(_db, "0661799090520"))?.ProductCode);
    }

    [Fact]
    public async Task 其它长度不回退变体()
    {
        await SeedProduct("P1", "ITEM-1", "10812024527");

        Assert.Null(await WarehousePickingQueries.LookupProductByCodeAsync(_db, "010812024527"));
    }

    private Task<int> SeedProduct(string code, string itemNumber, string barcode) =>
        _db.Insertable(new Product
        {
            UUID = $"uuid-{code}",
            ProductCode = code,
            ProductName = $"Product {code}",
            ItemNumber = itemNumber,
            Barcode = barcode,
            IsDeleted = false,
        }).ExecuteCommandAsync();

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}
