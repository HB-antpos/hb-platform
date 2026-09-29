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
            typeof(DomesticProduct), typeof(WarehouseProduct));
        database.Ado.ExecuteCommand("CREATE TABLE WarehouseProductChangeHistory (Id INTEGER PRIMARY KEY, EventGuid TEXT NOT NULL, ProductCode TEXT NOT NULL, Action TEXT NOT NULL, Source TEXT NOT NULL, SourceReference TEXT NULL, BatchGuid TEXT NULL, ActorUserGuid TEXT NULL, ActorName TEXT NOT NULL, ActorType TEXT NOT NULL, OccurredAtUtc TEXT NOT NULL, ChangesJson TEXT NOT NULL)");
    }

    [Fact]
    public async Task GetAsync_实际日期优先并按日期排序_精确匹配软删记录审计()
    {
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane")).Date;
        database.Insertable(new Store { StoreGUID = "store-1", StoreCode = "S-1", StoreName = "S", Address = "Brisbane QLD 4000" }).ExecuteCommand();
        database.Insertable(new UserStore { UserStoreGUID = "rel-1", UserGUID = "user-1", StoreGUID = "store-1", IsPrimary = false }).ExecuteCommand();

        database.Insertable(new Container { ContainerCode = "C-LATE", ContainerNumber = "LATE", ActualArrivalDate = today.AddDays(8), EstimatedArrivalDate = today.AddDays(-30) }).ExecuteCommand();
        database.Insertable(new Container { ContainerCode = "C-EARLY", ContainerNumber = "EARLY", ActualArrivalDate = today.AddDays(1), EstimatedArrivalDate = today.AddDays(-30) }).ExecuteCommand();
        database.Insertable(new Container { ContainerCode = "C-FALL", ContainerNumber = "FALL", ActualArrivalDate = null, EstimatedArrivalDate = today.AddDays(15) }).ExecuteCommand();
        database.Insertable(new[]
        {
            new ContainerDetail { DetailCode = "D1", ContainerCode = "C-LATE", ProductCode = "P-AUDIT" },
            new ContainerDetail { DetailCode = "D2", ContainerCode = "C-LATE", ProductCode = "P-NONE" },
            new ContainerDetail { DetailCode = "D3", ContainerCode = "C-EARLY", ProductCode = "P-WRONG" },
            new ContainerDetail { DetailCode = "D4", ContainerCode = "C-EARLY", ProductCode = "P-EARLY" },
            new ContainerDetail { DetailCode = "D5", ContainerCode = "C-FALL", ProductCode = "P-FALLBACK" },
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
        Assert.Contains(result.Items, x => x.ProductCode == "P-AUDIT" && x.Basis == "actual");
        Assert.Contains(result.Items, x => x.ProductCode == "P-NONE");
        Assert.Contains(result.Items, x => x.ProductCode == "P-FALLBACK" && x.Basis == "estimated");
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
