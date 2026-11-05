using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 同步其它分店时一并更新仓库价（SyncWarehouse）：
/// 不更新的话，其它分店扫码仍会提示「是否同步仓库价格」，且被同步的分店会立刻被判成「需改价」。
/// </summary>
public sealed class StoreSyncWarehousePriceTests : IDisposable
{
    private const string ProductCode = "P001";
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;
    private readonly PriceNotificationSummaryAccessor _summary = new();
    private readonly Mock<IStoreProductMaintenanceReactService> _maintenance = new(MockBehavior.Strict);

    public StoreSyncWarehousePriceTests()
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
            typeof(Product),
            typeof(WarehouseProduct),
            typeof(DomesticProduct),
            typeof(Store),
            typeof(StoreRetailPrice),
            typeof(ProductHqSyncOutbox)
        );
        SqliteWarehouseProductChangeHistoryTable.Create(_db);
        StorePriceUpdateTaskSchemaMigrator.EnsureAsync(_db, NullLogger.Instance).GetAwaiter().GetResult();

        // 仓库商品：供应商 200，Product / WarehouseProduct / DomesticProduct 三张主档的零售价都是 10。
        _db.Insertable(
                new Product
                {
                    ProductCode = ProductCode,
                    ProductName = "保温杯",
                    ItemNumber = "HB-1",
                    RetailPrice = 10m,
                    LocalSupplierCode = "200",
                }
            )
            .ExecuteCommand();
        _db.Insertable(new WarehouseProduct { ProductCode = ProductCode, OEMPrice = 10m, ImportPrice = 5m })
            .ExecuteCommand();
        _db.Insertable(new DomesticProduct { ProductCode = ProductCode, OEMPrice = 10m, ImportPrice = 5m })
            .ExecuteCommand();

        // 1001 = 来源分店（店长把零售价改成 12、折扣 0.1）；1002/1003 = 被同步；1004 = 没被勾选。
        foreach (var store in new[] { "1001", "1002", "1003", "1004" })
        {
            InsertStore(store);
        }

        InsertStorePrice("1001", 12m, 0.1m);
        InsertStorePrice("1002", 10m, null);
        InsertStorePrice("1003", 10m, null);
        InsertStorePrice("1004", 10m, null);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public async Task 同步零售价_仓库三张主档一起更新并留一条分店同步审计()
    {
        var result = await CreateService().SyncToOtherStoresAsync(NewDto(retail: true), "店长小王");

        Assert.True(result.Success, result.Message);
        Assert.Equal(12m, await WarehouseRetailAsync());
        Assert.Equal(12m, (await _db.Queryable<Product>().FirstAsync(p => p.ProductCode == ProductCode)).RetailPrice);
        Assert.Equal(12m, (await _db.Queryable<DomesticProduct>().FirstAsync(d => d.ProductCode == ProductCode)).OEMPrice);
        Assert.Equal(12m, await StoreRetailAsync("1002"));
        Assert.Equal(12m, await StoreRetailAsync("1003"));

        // 仓库价是全局价，必须留审计：来源 StoreSync、来源分店 1001、操作人是店长。
        Assert.Equal(1, _db.Ado.GetInt("SELECT COUNT(*) FROM WarehouseProductChangeHistory WHERE ProductCode='P001'"));
        Assert.Equal("StoreSync", _db.Ado.GetString("SELECT Source FROM WarehouseProductChangeHistory WHERE ProductCode='P001'"));
        Assert.Equal("1001", _db.Ado.GetString("SELECT SourceReference FROM WarehouseProductChangeHistory WHERE ProductCode='P001'"));
        Assert.Equal("店长小王", _db.Ado.GetString("SELECT ActorName FROM WarehouseProductChangeHistory WHERE ProductCode='P001'"));

        var data = Assert.IsType<SyncToOtherStoresResultDto>(result.Data);
        Assert.True(data.WarehouseUpdated);
        Assert.Equal(12m, data.WarehouseRetailPrice);
    }

    [Fact]
    public async Task 被同步的分店只收到待换标签_未选中且价格不同的分店收到需改价()
    {
        var result = await CreateService().SyncToOtherStoresAsync(NewDto(retail: true), "店长小王");
        Assert.True(result.Success, result.Message);

        var tasks = await _db.Queryable<StorePriceUpdateTask>().ToListAsync();
        // 被同步的 1002/1003：价格已等于新仓库价，不能再出现「需改价」（这正是扫码弹窗要消除的提示）。
        Assert.All(
            tasks.Where(t => t.StoreCode is "1002" or "1003"),
            task => Assert.Equal(StorePriceUpdateTaskKinds.LabelOnly, task.Kind)
        );
        // 来源分店价 == 新仓库价，没有任务。
        Assert.DoesNotContain(tasks, t => t.StoreCode == "1001");
        // 没被勾选且价格不同的 1004：按新仓库价收到「需改价」，来源标为分店同步。
        var unselected = Assert.Single(tasks, t => t.StoreCode == "1004");
        Assert.Equal(StorePriceUpdateTaskKinds.PriceUpdate, unselected.Kind);
        Assert.Equal(12m, unselected.TargetRetailPrice);
        Assert.Equal("StoreSync", unselected.InitiatorSource);
        Assert.Equal("1001", unselected.InitiatorReference);
    }

    [Fact]
    public async Task 目标分店都没有价格记录时_仍让未选中的分店按新仓库价对账()
    {
        InsertStore("1005"); // 有分店档案但没有该商品的价格记录

        var dto = NewDto(retail: true);
        dto.TargetStoreCodes = new List<string> { "1005" };
        var result = await CreateService().SyncToOtherStoresAsync(dto, "店长小王");

        Assert.True(result.Success, result.Message);
        Assert.Equal(12m, await WarehouseRetailAsync());
        var tasks = await _db.Queryable<StorePriceUpdateTask>().ToListAsync();
        Assert.Equal(new[] { "1002", "1003", "1004" }, tasks.Select(t => t.StoreCode).OrderBy(c => c));
        Assert.All(tasks, t => Assert.Equal(StorePriceUpdateTaskKinds.PriceUpdate, t.Kind));
    }

    [Fact]
    public async Task 非供应商200的商品不改仓库价()
    {
        await _db.Updateable<Product>()
            .SetColumns(p => p.LocalSupplierCode == "300")
            .Where(p => p.ProductCode == ProductCode)
            .ExecuteCommandAsync();

        var result = await CreateService().SyncToOtherStoresAsync(NewDto(retail: true), "店长小王");

        Assert.True(result.Success, result.Message);
        Assert.Equal(10m, await WarehouseRetailAsync());
        Assert.Equal(12m, await StoreRetailAsync("1002")); // 分店价照常同步
        Assert.Equal(0, _db.Ado.GetInt("SELECT COUNT(*) FROM WarehouseProductChangeHistory"));
        Assert.False(Assert.IsType<SyncToOtherStoresResultDto>(result.Data).WarehouseUpdated);
    }

    [Fact]
    public async Task 不带SyncWarehouse的调用_如Web批量入口_不改仓库且响应不变()
    {
        var dto = NewDto(retail: true);
        dto.SyncWarehouse = false;

        var result = await CreateService().SyncToOtherStoresAsync(dto, "管理员");

        Assert.True(result.Success, result.Message);
        Assert.Equal(10m, await WarehouseRetailAsync());
        Assert.Equal(12m, await StoreRetailAsync("1002"));
        Assert.Equal(0, _db.Ado.GetInt("SELECT COUNT(*) FROM WarehouseProductChangeHistory"));
        Assert.Null(result.Data); // Web 响应保持原样
    }

    [Fact]
    public async Task OnlyUpdateNull模式不拿分店的值去改仓库()
    {
        var dto = NewDto(retail: true);
        dto.Mode = SyncModeConstants.OnlyUpdateNull;

        var result = await CreateService().SyncToOtherStoresAsync(dto, "店长小王");

        Assert.True(result.Success, result.Message);
        Assert.Equal(10m, await WarehouseRetailAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task 来源分店零售价无效时不写仓库零售价(decimal sourceRetail)
    {
        await _db.Updateable<StoreRetailPrice>()
            .SetColumns(p => p.StoreRetailPriceValue == sourceRetail)
            .Where(p => p.StoreCode == "1001")
            .ExecuteCommandAsync();

        var result = await CreateService().SyncToOtherStoresAsync(NewDto(retail: true), "店长小王");

        Assert.True(result.Success, result.Message);
        Assert.Equal(10m, await WarehouseRetailAsync());
        Assert.Equal(0, _db.Ado.GetInt("SELECT COUNT(*) FROM WarehouseProductChangeHistory"));
    }

    [Fact]
    public async Task 同步折扣_仓库未设建议折扣时跟随来源分店新建()
    {
        var result = await CreateService().SyncToOtherStoresAsync(NewDto(discount: true), "店长小王");

        Assert.True(result.Success, result.Message);
        Assert.Equal(0.1m, await SuggestedDiscountAsync());
        Assert.Equal(10m, await WarehouseRetailAsync()); // 没勾零售价就不动零售价
        Assert.Equal(1, _db.Ado.GetInt("SELECT COUNT(*) FROM WarehouseProductChangeHistory WHERE ProductCode='P001'"));
        var data = Assert.IsType<SyncToOtherStoresResultDto>(result.Data);
        Assert.True(data.WarehouseUpdated);
        Assert.Equal(0.1m, data.WarehouseSuggestedDiscountRate);
    }

    [Fact]
    public async Task 同步折扣_来源分店无折扣且仓库未设建议折扣时不凭空新建()
    {
        await _db.Updateable<StoreRetailPrice>()
            .SetColumns(p => p.DiscountRate == (decimal?)null)
            .Where(p => p.StoreCode == "1001")
            .ExecuteCommandAsync();

        var result = await CreateService().SyncToOtherStoresAsync(NewDto(discount: true), "店长小王");

        Assert.True(result.Success, result.Message);
        Assert.Null(await SuggestedDiscountAsync());
        Assert.Equal(0, _db.Ado.GetInt("SELECT COUNT(*) FROM ProductSuggestedDiscount"));
        Assert.False(Assert.IsType<SyncToOtherStoresResultDto>(result.Data).WarehouseUpdated);
    }

    [Fact]
    public async Task 同步折扣_仓库已设建议折扣而来源分店无折扣时改为明确无折扣()
    {
        await _db.Insertable(
                new ProductSuggestedDiscount { ProductCode = ProductCode, SuggestedDiscountRate = 0.3m }
            )
            .ExecuteCommandAsync();
        await _db.Updateable<StoreRetailPrice>()
            .SetColumns(p => p.DiscountRate == (decimal?)null)
            .Where(p => p.StoreCode == "1001")
            .ExecuteCommandAsync();

        var result = await CreateService().SyncToOtherStoresAsync(NewDto(discount: true), "店长小王");

        Assert.True(result.Success, result.Message);
        Assert.Equal(0m, await SuggestedDiscountAsync());
    }

    [Fact]
    public async Task 价格已经一致时不重复写仓库也不产生审计()
    {
        await _db.Updateable<WarehouseProduct>()
            .SetColumns(w => w.OEMPrice == 12m)
            .Where(w => w.ProductCode == ProductCode)
            .ExecuteCommandAsync();
        await _db.Updateable<Product>()
            .SetColumns(p => p.RetailPrice == 12m)
            .Where(p => p.ProductCode == ProductCode)
            .ExecuteCommandAsync();
        await _db.Updateable<DomesticProduct>()
            .SetColumns(d => d.OEMPrice == 12m)
            .Where(d => d.ProductCode == ProductCode)
            .ExecuteCommandAsync();

        var result = await CreateService().SyncToOtherStoresAsync(NewDto(retail: true), "店长小王");

        Assert.True(result.Success, result.Message);
        Assert.Equal(0, _db.Ado.GetInt("SELECT COUNT(*) FROM WarehouseProductChangeHistory"));
        var data = Assert.IsType<SyncToOtherStoresResultDto>(result.Data);
        Assert.False(data.WarehouseUpdated);
        Assert.Equal(12m, data.WarehouseRetailPrice);
    }

    [Fact]
    public async Task 没有审计服务时跳过仓库价更新_宁可少同步也不留无审计的全局价变动()
    {
        var service = new StoreProductPriceReactService(
            CreateContext(),
            NullLogger<StoreProductPriceReactService>.Instance,
            CreateTaskService(),
            changeHistoryService: null
        );

        var result = await service.SyncToOtherStoresAsync(NewDto(retail: true), "店长小王");

        Assert.True(result.Success, result.Message);
        Assert.Equal(10m, await WarehouseRetailAsync());
        Assert.Equal(12m, await StoreRetailAsync("1002"));
    }

    [Fact]
    public async Task 审计写入失败时仓库价与分店价一起回滚()
    {
        var history = new Mock<IWarehouseProductChangeHistoryService>();
        history
            .Setup(x => x.CaptureSnapshotsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, WarehouseProductChangeSnapshotDto>());
        history
            .Setup(x => x.RecordChangesAsync(
                It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                It.IsAny<WarehouseProductChangeHistoryContextDto>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("审计写入失败"));

        var service = new StoreProductPriceReactService(
            CreateContext(),
            NullLogger<StoreProductPriceReactService>.Instance,
            CreateTaskService(),
            history.Object
        );

        var result = await service.SyncToOtherStoresAsync(NewDto(retail: true), "店长小王");

        Assert.False(result.Success);
        Assert.Equal(10m, await WarehouseRetailAsync());
        Assert.Equal(10m, await StoreRetailAsync("1002"));
        Assert.Equal(10m, await StoreRetailAsync("1003"));
    }

    // ---------------------------------------------------------------- 夹具

    private SyncToOtherStoresDto NewDto(bool retail = false, bool discount = false) =>
        new()
        {
            ProductCodes = new List<string> { ProductCode },
            SourceStoreCode = "1001",
            TargetStoreCodes = new List<string> { "1002", "1003" },
            SyncRetailPrice = retail,
            SyncDiscountRate = discount,
            Mode = SyncModeConstants.Overwrite,
            SyncWarehouse = true,
        };

    private StoreProductPriceReactService CreateService()
    {
        var taskService = CreateTaskService();
        var history = new WarehouseProductChangeHistoryService(
            CreateContext(),
            NullLogger<WarehouseProductChangeHistoryService>.Instance,
            Mock.Of<ICurrentUserService>(),
            taskService
        );
        return new StoreProductPriceReactService(
            CreateContext(),
            NullLogger<StoreProductPriceReactService>.Instance,
            taskService,
            history
        );
    }

    private StorePriceUpdateTaskService CreateTaskService()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var provider = new Mock<IServiceProvider>();
        provider
            .Setup(p => p.GetService(typeof(IStoreProductMaintenanceReactService)))
            .Returns(_maintenance.Object);
        StorePriceUpdateTaskService? service = null;
        // 与生产一致：审计服务依赖任务服务，任务服务再通过容器延迟取回审计服务。
        provider
            .Setup(p => p.GetService(typeof(IWarehouseProductChangeHistoryService)))
            .Returns(() => new WarehouseProductChangeHistoryService(
                CreateContext(),
                NullLogger<WarehouseProductChangeHistoryService>.Instance,
                Mock.Of<ICurrentUserService>(),
                service));
        service = new StorePriceUpdateTaskService(
            CreateContext(),
            NullLogger<StorePriceUpdateTaskService>.Instance,
            configuration,
            _summary,
            provider.Object
        );
        return service;
    }

    private SqlSugarContext CreateContext()
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, _db);
        return context;
    }

    private void InsertStore(string storeCode) =>
        _db.Insertable(new Store { StoreCode = storeCode, StoreName = $"分店{storeCode}", IsActive = true })
            .ExecuteCommand();

    private void InsertStorePrice(string storeCode, decimal retail, decimal? discount) =>
        _db.Insertable(
                new StoreRetailPrice
                {
                    StoreCode = storeCode,
                    ProductCode = ProductCode,
                    StoreProductCode = storeCode + ProductCode,
                    StoreRetailPriceValue = retail,
                    DiscountRate = discount,
                    IsActive = true,
                }
            )
            .ExecuteCommand();

    private async Task<decimal?> WarehouseRetailAsync() =>
        (await _db.Queryable<WarehouseProduct>().FirstAsync(w => w.ProductCode == ProductCode)).OEMPrice;

    private async Task<decimal?> StoreRetailAsync(string storeCode) =>
        (await _db.Queryable<StoreRetailPrice>().FirstAsync(p => p.StoreCode == storeCode && p.ProductCode == ProductCode))
            .StoreRetailPriceValue;

    private async Task<decimal?> SuggestedDiscountAsync() =>
        (await _db.Queryable<ProductSuggestedDiscount>().FirstAsync(d => d.ProductCode == ProductCode))
            ?.SuggestedDiscountRate;
}
