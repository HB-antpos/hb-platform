using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Models;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class ContainerNewProductsServiceSqliteTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"container-new-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient database;

    public ContainerNewProductsServiceSqliteTests()
    {
        database = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={path}", DbType = DbType.Sqlite,
            InitKeyType = InitKeyType.Attribute, IsAutoCloseConnection = false,
        });
        database.CodeFirst.InitTables(typeof(Store), typeof(UserStore), typeof(Container), typeof(ContainerDetail),
            typeof(DomesticProduct), typeof(WarehouseProduct), typeof(Product), typeof(StoreRetailPrice));
        database.Ado.ExecuteCommand("CREATE TABLE WarehouseProductChangeHistory (Id INTEGER PRIMARY KEY, EventGuid TEXT NOT NULL, ProductCode TEXT NOT NULL, Action TEXT NOT NULL, Source TEXT NOT NULL, SourceReference TEXT NULL, BatchGuid TEXT NULL, ActorUserGuid TEXT NULL, ActorName TEXT NOT NULL, ActorType TEXT NOT NULL, OccurredAtUtc TEXT NOT NULL, ChangesJson TEXT NOT NULL)");
    }

    [Fact]
    public async Task GetAsync_实际日期优先并按日期排序_精确匹配软删记录审计()
    {
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane")).Date;
        database.Insertable(new Store { StoreGUID = "store-1", StoreCode = "S-1", StoreName = "S", Address = "Brisbane QLD 4000" }).ExecuteCommand();
        database.Insertable(new UserStore { UserStoreGUID = "rel-1", UserGUID = "user-1", StoreGUID = "store-1", IsPrimary = false }).ExecuteCommand();

        // 窗口按预计到店区间（QLD = 货柜日期 + 3~7 个工作日，结束日跨 9~11 个自然日）算：
        // C-EARLY 12 天前到仓库 → 区间结束日在今天前 1~3 天（按货柜日期算已超 1 周，按到店区间仍应显示）
        database.Insertable(new Container { ContainerCode = "C-LATE", ContainerNumber = "LATE", ActualArrivalDate = today.AddDays(-5), EstimatedArrivalDate = today.AddDays(-30) }).ExecuteCommand();
        database.Insertable(new Container { ContainerCode = "C-EARLY", ContainerNumber = "EARLY", ActualArrivalDate = today.AddDays(-12), EstimatedArrivalDate = today.AddDays(-30) }).ExecuteCommand();
        database.Insertable(new Container { ContainerCode = "C-FALL", ContainerNumber = "FALL", ActualArrivalDate = null, EstimatedArrivalDate = today.AddDays(2) }).ExecuteCommand();
        // 窗口外：区间结束日在 9~11 天前、起始日在 22~24 天后（未来 3 周之外），都不应出现
        database.Insertable(new Container { ContainerCode = "C-OLD", ContainerNumber = "OLD", ActualArrivalDate = today.AddDays(-20) }).ExecuteCommand();
        database.Insertable(new Container { ContainerCode = "C-FAR", ContainerNumber = "FAR", ActualArrivalDate = null, EstimatedArrivalDate = today.AddDays(19) }).ExecuteCommand();
        database.Insertable(new[]
        {
            new ContainerDetail { DetailCode = "D1", ContainerCode = "C-LATE", ProductCode = "P-AUDIT" },
            new ContainerDetail { DetailCode = "D2", ContainerCode = "C-LATE", ProductCode = "P-NONE" },
            new ContainerDetail { DetailCode = "D3", ContainerCode = "C-EARLY", ProductCode = "P-WRONG" },
            new ContainerDetail { DetailCode = "D4", ContainerCode = "C-EARLY", ProductCode = "P-EARLY" },
            new ContainerDetail { DetailCode = "D5", ContainerCode = "C-FALL", ProductCode = "P-FALLBACK" },
            new ContainerDetail { DetailCode = "D6", ContainerCode = "C-OLD", ProductCode = "P-OLD" },
            new ContainerDetail { DetailCode = "D7", ContainerCode = "C-FAR", ProductCode = "P-FAR" },
        }).ExecuteCommand();
        database.Insertable(new[]
        {
            new WarehouseProduct { ProductCode = "P-AUDIT", IsDeleted = true },
            new WarehouseProduct { ProductCode = "P-WRONG", IsDeleted = true },
        }).ExecuteCommand();
        database.Insertable(new WarehouseProductChangeHistory { ProductCode = "P-AUDIT", Source = "ContainerSubmit", Action = "Create", SourceReference = "C-LATE" }).ExecuteCommand();
        database.Insertable(new WarehouseProductChangeHistory { ProductCode = "P-WRONG", Source = "ContainerSubmit", Action = "Create", SourceReference = "OTHER-CONTAINER" }).ExecuteCommand();

        var service = CreateService("user-1");
        var result = await service.GetAsync("S-1");

        Assert.Equal("QLD", result.StateCode);
        Assert.Equal(new[] { "C-EARLY", "C-LATE", "C-LATE", "C-FALL" }, result.Items.Select(x => x.ContainerCode));
        Assert.DoesNotContain(result.Items, x => x.ProductCode == "P-WRONG");
        var storeFrom = DateOnly.FromDateTime(today.AddDays(-7));
        var storeTo = DateOnly.FromDateTime(today.AddDays(21));
        Assert.All(result.Items, x =>
        {
            // 区间与窗口有交集，且结束日 = 起始日 + 4 个工作日
            Assert.True(x.EstimatedStoreArrivalDateEnd >= storeFrom && x.EstimatedStoreArrivalDate <= storeTo);
            Assert.Equal(
                DateOnly.FromDateTime(ContainerNewProductsReactService.AddWeekdays(x.EstimatedStoreArrivalDate.ToDateTime(TimeOnly.MinValue), 4)),
                x.EstimatedStoreArrivalDateEnd);
        });
        Assert.Contains(result.Items, x => x.ProductCode == "P-AUDIT" && x.Basis == "actual");
        Assert.Contains(result.Items, x => x.ProductCode == "P-NONE");
        Assert.Contains(result.Items, x => x.ProductCode == "P-FALLBACK" && x.Basis == "estimated");
    }

    [Fact]
    public async Task GetAsync_NSW到店区间为仓库日期当天至3个工作日_区间跨窗口下界仍显示()
    {
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney")).Date;
        database.Insertable(new Store { StoreGUID = "store-6", StoreCode = "S-6", StoreName = "S", Address = "Sydney NSW 2000" }).ExecuteCommand();
        database.Insertable(new UserStore { UserStoreGUID = "rel-6", UserGUID = "user-6", StoreGUID = "store-6", IsPrimary = false }).ExecuteCommand();
        // C-EDGE：起始日 8 天前已出窗口，结束日（+3 个工作日）在 3~5 天前仍在窗口内；C-GONE：结束日最晚 9 天前，不显示
        database.Insertable(new[]
        {
            new Container { ContainerCode = "C-NOW", ContainerNumber = "NOW", ActualArrivalDate = today },
            new Container { ContainerCode = "C-EDGE", ContainerNumber = "EDGE", ActualArrivalDate = today.AddDays(-8) },
            new Container { ContainerCode = "C-GONE", ContainerNumber = "GONE", ActualArrivalDate = today.AddDays(-14) },
        }).ExecuteCommand();
        database.Insertable(new[]
        {
            new ContainerDetail { DetailCode = "N1", ContainerCode = "C-NOW", ProductCode = "P-NOW" },
            new ContainerDetail { DetailCode = "N2", ContainerCode = "C-EDGE", ProductCode = "P-EDGE" },
            new ContainerDetail { DetailCode = "N3", ContainerCode = "C-GONE", ProductCode = "P-GONE" },
        }).ExecuteCommand();

        var result = await CreateService("user-6").GetAsync("S-6");

        Assert.Equal("NSW", result.StateCode);
        Assert.Equal(new[] { "P-EDGE", "P-NOW" }, result.Items.Select(x => x.ProductCode));
        var now = result.Items.Single(x => x.ProductCode == "P-NOW");
        Assert.Equal(DateOnly.FromDateTime(today), now.EstimatedStoreArrivalDate);
        Assert.Equal(DateOnly.FromDateTime(ContainerNewProductsReactService.AddWeekdays(today, 3)), now.EstimatedStoreArrivalDateEnd);
        var edge = result.Items.Single(x => x.ProductCode == "P-EDGE");
        Assert.Equal(DateOnly.FromDateTime(today.AddDays(-8)), edge.EstimatedStoreArrivalDate);
    }

    [Fact]
    public async Task GetAsync_同一到店日按HB货号排序_无货号排在末尾()
    {
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane")).Date;
        database.Insertable(new Store { StoreGUID = "store-4", StoreCode = "S-4", StoreName = "S", Address = "Brisbane QLD 4000" }).ExecuteCommand();
        database.Insertable(new UserStore { UserStoreGUID = "rel-4", UserGUID = "user-4", StoreGUID = "store-4", IsPrimary = false }).ExecuteCommand();
        database.Insertable(new Container { ContainerCode = "C-SAME", ContainerNumber = "SAME", ActualArrivalDate = today.AddDays(2) }).ExecuteCommand();
        // ProductCode 顺序（A→D）与货号顺序故意相反，确保排序依据是货号而不是 UUID 主键
        database.Insertable(new[]
        {
            new ContainerDetail { DetailCode = "S1", ContainerCode = "C-SAME", ProductCode = "P-A" },
            new ContainerDetail { DetailCode = "S2", ContainerCode = "C-SAME", ProductCode = "P-B", LoadingQuantity = 1920 },
            // 同一货柜同一商品拆成两行明细：只出一张卡片，数量合计
            new ContainerDetail { DetailCode = "S2B", ContainerCode = "C-SAME", ProductCode = "P-B", LoadingQuantity = 480 },
            new ContainerDetail { DetailCode = "S3", ContainerCode = "C-SAME", ProductCode = "P-C" },
            new ContainerDetail { DetailCode = "S4", ContainerCode = "C-SAME", ProductCode = "P-D" },
        }).ExecuteCommand();
        database.Insertable(new[]
        {
            new DomesticProduct { ProductCode = "P-A", HBProductNo = "  " },
            new DomesticProduct { ProductCode = "P-B", HBProductNo = "HB150-574" },
            new DomesticProduct { ProductCode = "P-C", HBProductNo = "hb150-568" },
            new DomesticProduct { ProductCode = "P-D", HBProductNo = "HB038-XM-017" },
        }).ExecuteCommand();

        var result = await CreateService("user-4").GetAsync("S-4");

        Assert.Equal(new[] { "P-D", "P-C", "P-B", "P-A" }, result.Items.Select(x => x.ProductCode));
        Assert.Equal(new string?[] { "HB038-XM-017", "hb150-568", "HB150-574", null }, result.Items.Select(x => x.HbProductNo));
        Assert.Equal(2400m, result.Items.Single(x => x.ProductCode == "P-B").Quantity);
        Assert.Null(result.Items.Single(x => x.ProductCode == "P-A").Quantity);
    }

    [Fact]
    public async Task GetAsync_返回条码与零售价_分店价优先_停用分店价回退主档_未建档取明细价()
    {
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane")).Date;
        database.Insertable(new Store { StoreGUID = "store-5", StoreCode = "S-5", StoreName = "S", Address = "Brisbane QLD 4000" }).ExecuteCommand();
        database.Insertable(new UserStore { UserStoreGUID = "rel-5", UserGUID = "user-5", StoreGUID = "store-5", IsPrimary = false }).ExecuteCommand();
        database.Insertable(new Container { ContainerCode = "C-PRICE", ContainerNumber = "PRICE", ActualArrivalDate = today.AddDays(1) }).ExecuteCommand();
        database.Insertable(new[]
        {
            new ContainerDetail { DetailCode = "R1", ContainerCode = "C-PRICE", ProductCode = "P-STORE", OEMPrice = 1.00m },
            new ContainerDetail { DetailCode = "R2", ContainerCode = "C-PRICE", ProductCode = "P-INACTIVE", OEMPrice = 1.00m },
            // 同一商品多行明细：第一行零售价为 0 视为没有，取下一行的有效价
            new ContainerDetail { DetailCode = "R3", ContainerCode = "C-PRICE", ProductCode = "P-NEW", OEMPrice = 0m },
            new ContainerDetail { DetailCode = "R3B", ContainerCode = "C-PRICE", ProductCode = "P-NEW", OEMPrice = 2.50m },
            new ContainerDetail { DetailCode = "R4", ContainerCode = "C-PRICE", ProductCode = "P-NONE" },
        }).ExecuteCommand();
        database.Insertable(new[]
        {
            new DomesticProduct { ProductCode = "P-STORE", HBProductNo = "HB-1", Barcode = " 9300000000017 " },
            new DomesticProduct { ProductCode = "P-INACTIVE", HBProductNo = "HB-2", Barcode = "  " },
            new DomesticProduct { ProductCode = "P-NEW", HBProductNo = "HB-3", Barcode = "HB0001" },
            new DomesticProduct { ProductCode = "P-NONE", HBProductNo = "HB-4" },
        }).ExecuteCommand();
        database.Insertable(new[]
        {
            new Product { ProductCode = "P-STORE", Barcode = "IGNORED", RetailPrice = 4.99m },
            new Product { ProductCode = "P-INACTIVE", Barcode = "9300000000024", RetailPrice = 3.50m },
            // 已软删的主档不参与
            new Product { ProductCode = "P-NONE", Barcode = "DELETED", RetailPrice = 9.99m, IsDeleted = true },
        }).ExecuteCommand();
        database.Insertable(new[]
        {
            new StoreRetailPrice { StoreCode = "S-5", ProductCode = "P-STORE", StoreRetailPriceValue = 5.99m },
            new StoreRetailPrice { StoreCode = "S-5", ProductCode = "P-INACTIVE", StoreRetailPriceValue = 7.00m, IsActive = false },
            // 其他门店的分店价不应串到本门店
            new StoreRetailPrice { StoreCode = "S-OTHER", ProductCode = "P-NONE", StoreRetailPriceValue = 8.00m },
        }).ExecuteCommand();

        var result = await CreateService("user-5").GetAsync("S-5");

        Assert.Equal(DateOnly.FromDateTime(today), result.LocalToday);
        var byCode = result.Items.ToDictionary(x => x.ProductCode);
        Assert.Equal(("9300000000017", 5.99m), (byCode["P-STORE"].Barcode, byCode["P-STORE"].RetailPrice));
        Assert.Equal(("9300000000024", 3.50m), (byCode["P-INACTIVE"].Barcode, byCode["P-INACTIVE"].RetailPrice));
        Assert.Equal(("HB0001", 2.50m), (byCode["P-NEW"].Barcode, byCode["P-NEW"].RetailPrice));
        Assert.Null(byCode["P-NONE"].Barcode);
        Assert.Null(byCode["P-NONE"].RetailPrice);
    }

    [Fact]
    public async Task GetAsync_带includeExisting时同时返回已有商品并标记新旧_默认只返回新商品()
    {
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane")).Date;
        database.Insertable(new Store { StoreGUID = "store-7", StoreCode = "S-7", StoreName = "S", Address = "Brisbane QLD 4000" }).ExecuteCommand();
        database.Insertable(new UserStore { UserStoreGUID = "rel-7", UserGUID = "user-7", StoreGUID = "store-7", IsPrimary = false }).ExecuteCommand();
        database.Insertable(new Container { ContainerCode = "C-MIX", ContainerNumber = "MIX", ActualArrivalDate = today }).ExecuteCommand();
        database.Insertable(new[]
        {
            new ContainerDetail { DetailCode = "M1", ContainerCode = "C-MIX", ProductCode = "P-FRESH" },
            new ContainerDetail { DetailCode = "M2", ContainerCode = "C-MIX", ProductCode = "P-CREATED" },
            new ContainerDetail { DetailCode = "M3", ContainerCode = "C-MIX", ProductCode = "P-RESTOCK" },
        }).ExecuteCommand();
        // P-CREATED 由本柜提交时新建，仍算新商品；P-RESTOCK 早已在仓库，是已有商品
        database.Insertable(new[]
        {
            new WarehouseProduct { ProductCode = "P-CREATED" },
            new WarehouseProduct { ProductCode = "P-RESTOCK" },
        }).ExecuteCommand();
        database.Insertable(new WarehouseProductChangeHistory { ProductCode = "P-CREATED", Source = "ContainerSubmit", Action = "Create", SourceReference = "C-MIX" }).ExecuteCommand();

        var service = CreateService("user-7");
        var defaultResult = await service.GetAsync("S-7");
        var withExisting = await service.GetAsync("S-7", includeExisting: true);

        Assert.Equal(new[] { "P-CREATED", "P-FRESH" }, defaultResult.Items.Select(x => x.ProductCode).Order());
        Assert.All(defaultResult.Items, x => Assert.True(x.IsNewProduct));
        Assert.Equal(
            new[] { ("P-CREATED", true), ("P-FRESH", true), ("P-RESTOCK", false) },
            withExisting.Items.Select(x => (x.ProductCode, x.IsNewProduct)).OrderBy(x => x.ProductCode));
    }

    [Fact]
    public async Task GetAsync_仅允许UserStore关联门店_不要求Primary()
    {
        database.Insertable(new Store { StoreGUID = "store-2", StoreCode = "S-2", StoreName = "S", Address = "Brisbane QLD 4000" }).ExecuteCommand();
        database.Insertable(new UserStore { UserStoreGUID = "rel-2", UserGUID = "user-2", StoreGUID = "store-2", IsPrimary = false }).ExecuteCommand();
        var service = CreateService("user-2");
        var result = await service.GetAsync("S-2");
        Assert.Equal("S-2", result.StoreCode);
    }

    [Fact]
    public async Task GetAsync_未关联门店返回403异常()
    {
        database.Insertable(new Store { StoreGUID = "store-3", StoreCode = "S-3", StoreName = "S", Address = "Brisbane QLD 4000" }).ExecuteCommand();
        var service = CreateService("user-3");
        await Assert.ThrowsAsync<ContainerNewProductsForbiddenException>(() => service.GetAsync("S-3"));
    }

    private ContainerNewProductsReactService CreateService(string userGuid)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, database);
        var scope = new Mock<ICurrentUserManageableStoreScopeService>();
        scope.Setup(x => x.GetScopeAsync()).ReturnsAsync(new CurrentUserManageableStoreScope { IsAuthenticated = true, UserGuid = userGuid });
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(x => x.GetCurrentUserGuid()).Returns(userGuid);
        return new ContainerNewProductsReactService(context, scope.Object, currentUser.Object);
    }

    public void Dispose()
    {
        database.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(path);
    }
}
