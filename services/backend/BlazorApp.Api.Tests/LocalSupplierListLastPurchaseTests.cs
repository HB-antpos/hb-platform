using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 澳洲供应商列表：最近进货（按分店范围）、图片模板过滤、按最近进货排序。
/// </summary>
public sealed class LocalSupplierListLastPurchaseTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public LocalSupplierListLastPurchaseTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables<HBLocalSupplier, StoreLocalSupplierInvoice, Store, UserStore>();
    }

    [Fact]
    public async Task 最近进货取订单日期最大的一张_同日取创建时间最新_忽略已删除()
    {
        await SeedSupplierAsync("AUS-A", "Alpha");
        await SeedSupplierAsync("AUS-NONE", "No Invoice");
        await SeedStoreAsync("S1", "Store One");
        await SeedStoreAsync("S2", "Store Two");
        await SeedStoreAsync("S3", "Store Three");
        await SeedInvoiceAsync("AUS-A", "S1", new DateTime(2026, 10, 1), new DateTime(2026, 10, 1, 9, 0, 0));
        await SeedInvoiceAsync("AUS-A", "S2", new DateTime(2026, 10, 5), new DateTime(2026, 10, 5, 8, 0, 0));
        await SeedInvoiceAsync("AUS-A", "S3", new DateTime(2026, 10, 5), new DateTime(2026, 10, 5, 18, 0, 0));
        await SeedInvoiceAsync("AUS-A", "S1", new DateTime(2026, 10, 20), new DateTime(2026, 10, 20), isDeleted: true);
        await SeedInvoiceAsync("AUS-A", "S1", null, new DateTime(2026, 10, 22));

        var result = await CreateService().GetSuppliersAsync(1, 20, null, null, null, null, null, null);

        var alpha = Assert.Single(result.Items!, x => x.LocalSupplierCode == "AUS-A");
        Assert.Equal(new DateTime(2026, 10, 5), alpha.LastPurchaseDate);
        Assert.Equal("S3", alpha.LastPurchaseStoreCode);
        Assert.Equal("Store Three", alpha.LastPurchaseStoreName);

        var none = Assert.Single(result.Items!, x => x.LocalSupplierCode == "AUS-NONE");
        Assert.Null(none.LastPurchaseDate);
        Assert.Null(none.LastPurchaseStoreCode);
        Assert.Null(none.LastPurchaseStoreName);
    }

    [Fact]
    public async Task 受限分店只在自己的分店里找最近进货_找不到返回null()
    {
        await SeedSupplierAsync("AUS-A", "Alpha");
        await SeedStoreAsync("S1", "Store One");
        await SeedStoreAsync("S2", "Store Two");
        await SeedInvoiceAsync("AUS-A", "S1", new DateTime(2026, 9, 1), new DateTime(2026, 9, 1));
        await SeedInvoiceAsync("AUS-A", "S2", new DateTime(2026, 10, 1), new DateTime(2026, 10, 1));

        var service = CreateService();
        var onlyS1 = await service.GetSuppliersAsync(1, 20, null, null, null, null, null, new[] { "S1" });
        var item = Assert.Single(onlyS1.Items!);
        Assert.Equal(new DateTime(2026, 9, 1), item.LastPurchaseDate);
        Assert.Equal("S1", item.LastPurchaseStoreCode);
        Assert.Equal("Store One", item.LastPurchaseStoreName);

        var otherStore = await service.GetSuppliersAsync(1, 20, null, null, null, null, null, new[] { "S9" });
        Assert.Null(Assert.Single(otherStore.Items!).LastPurchaseDate);

        var noStore = await service.GetSuppliersAsync(1, 20, null, null, null, null, null, Array.Empty<string>());
        Assert.Null(Assert.Single(noStore.Items!).LastPurchaseDate);
    }

    [Fact]
    public async Task 图片模板过滤_configured与missing_非法值不过滤()
    {
        await SeedSupplierAsync("AUS-IMG", "With Image", "https://img.example.com/{code}.jpg");
        await SeedSupplierAsync("AUS-NULL", "Null Image", null);
        await SeedSupplierAsync("AUS-EMPTY", "Empty Image", "");
        await SeedSupplierAsync("AUS-BLANK", "Blank Image", "   ");

        var service = CreateService();
        var configured = await service.GetSuppliersAsync(1, 20, null, null, null, null, "configured", null);
        Assert.Equal(new[] { "AUS-IMG" }, configured.Items!.Select(x => x.LocalSupplierCode));
        Assert.Equal(1, configured.Total);

        var missing = await service.GetSuppliersAsync(1, 20, null, null, null, null, "Missing", null);
        Assert.Equal(
            new[] { "AUS-BLANK", "AUS-EMPTY", "AUS-NULL" },
            missing.Items!.Select(x => x.LocalSupplierCode).OrderBy(x => x, StringComparer.Ordinal)
        );
        Assert.Equal(3, missing.Total);

        var invalid = await service.GetSuppliersAsync(1, 20, null, null, null, null, "whatever", null);
        Assert.Equal(4, invalid.Total);
    }

    [Fact]
    public async Task 按最近进货排序_无进货的始终排最后_并支持分页()
    {
        await SeedSupplierAsync("AUS-OLD", "Old");
        await SeedSupplierAsync("AUS-NEW", "New");
        await SeedSupplierAsync("AUS-NONE", "None");
        await SeedStoreAsync("S1", "Store One");
        await SeedInvoiceAsync("AUS-OLD", "S1", new DateTime(2026, 1, 1), new DateTime(2026, 1, 1));
        await SeedInvoiceAsync("AUS-NEW", "S1", new DateTime(2026, 6, 1), new DateTime(2026, 6, 1));

        var service = CreateService();
        var desc = await service.GetSuppliersAsync(1, 20, null, null, "lastPurchaseDate", "descend", null, null);
        Assert.Equal(new[] { "AUS-NEW", "AUS-OLD", "AUS-NONE" }, desc.Items!.Select(x => x.LocalSupplierCode));
        Assert.Equal(new DateTime(2026, 6, 1), desc.Items![0].LastPurchaseDate);

        var asc = await service.GetSuppliersAsync(1, 20, null, null, "lastpurchasedate", "ascend", null, null);
        Assert.Equal(new[] { "AUS-OLD", "AUS-NEW", "AUS-NONE" }, asc.Items!.Select(x => x.LocalSupplierCode));

        var page2 = await service.GetSuppliersAsync(2, 2, null, null, "lastpurchasedate", "descend", null, null);
        Assert.Equal(3, page2.Total);
        Assert.Equal(new[] { "AUS-NONE" }, page2.Items!.Select(x => x.LocalSupplierCode));

        // 受限分店看不到 S1 的单时，按最近进货排序退化为名称排序，且全部为 null。
        var scoped = await service.GetSuppliersAsync(1, 20, null, null, "lastpurchasedate", "descend", null, new[] { "S9" });
        Assert.All(scoped.Items!, x => Assert.Null(x.LastPurchaseDate));
        Assert.Equal(3, scoped.Items!.Count);
    }

    [Fact]
    public async Task 控制器按当前用户分店范围计算最近进货()
    {
        await SeedSupplierAsync("AUS-A", "Alpha");
        var s1 = await SeedStoreAsync("S1", "Store One");
        await SeedStoreAsync("S2", "Store Two");
        await SeedInvoiceAsync("AUS-A", "S1", new DateTime(2026, 9, 1), new DateTime(2026, 9, 1));
        await SeedInvoiceAsync("AUS-A", "S2", new DateTime(2026, 10, 1), new DateTime(2026, 10, 1));
        await _db.Insertable(new UserStore { UserGUID = "user-1", StoreGUID = s1.StoreGUID }).ExecuteCommandAsync();

        var restricted = await InvokeGetListAsync(new Claim("userId", "user-1"), new Claim(ClaimTypes.Role, "StoreManager"));
        Assert.Equal("S1", Assert.Single(restricted).LastPurchaseStoreCode);

        var manager = await InvokeGetListAsync(new Claim("userId", "user-2"), new Claim(ClaimTypes.Role, "WarehouseManager"));
        Assert.Equal("S2", Assert.Single(manager).LastPurchaseStoreCode);

        var unbound = await InvokeGetListAsync(new Claim("userId", "user-3"), new Claim(ClaimTypes.Role, "StoreManager"));
        Assert.Null(Assert.Single(unbound).LastPurchaseDate);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private async Task<List<LocalSupplierDto>> InvokeGetListAsync(params Claim[] claims)
    {
        var context = CreateSqlSugarContext(_db);
        var controller = new LocalSuppliersController(
            CreateService(context),
            NullLogger<LocalSuppliersController>.Instance,
            context
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")),
                },
            },
        };

        var result = Assert.IsType<OkObjectResult>(await controller.GetList());
        var data = result.Value!.GetType().GetProperty("data")!.GetValue(result.Value)!;
        var items = data.GetType().GetProperty("items")!.GetValue(data);
        return Assert.IsType<List<LocalSupplierDto>>(items);
    }

    private LocalSupplierReactService CreateService(SqlSugarContext? context = null)
    {
        return new LocalSupplierReactService(
            context ?? CreateSqlSugarContext(_db),
            CreateHqSqlSugarContext(_db),
            NullLogger<LocalSupplierReactService>.Instance
        );
    }

    private Task SeedSupplierAsync(string code, string name, string? imageBaseUrl = null)
    {
        return _db.Insertable(new HBLocalSupplier
        {
            Guid = $"guid-{code}",
            LocalSupplierCode = code,
            Name = name,
            Status = 1,
            ImageBaseUrl = imageBaseUrl,
            CreatedAt = DateTime.UtcNow,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task<Store> SeedStoreAsync(string code, string name)
    {
        var store = new Store { StoreGUID = $"store-{code}", StoreCode = code, StoreName = name };
        await _db.Insertable(store).ExecuteCommandAsync();
        return store;
    }

    private Task SeedInvoiceAsync(
        string supplierCode,
        string storeCode,
        DateTime? orderDate,
        DateTime createdAt,
        bool isDeleted = false
    )
    {
        return _db.Insertable(new StoreLocalSupplierInvoice
        {
            InvoiceGUID = Guid.NewGuid().ToString("N"),
            SupplierCode = supplierCode,
            StoreCode = storeCode,
            OrderDate = orderDate,
            CreatedAt = createdAt,
            IsDeleted = isDeleted,
        }).ExecuteCommandAsync();
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    private static HqSqlSugarContext CreateHqSqlSugarContext(ISqlSugarClient db)
    {
        // 列表查询不碰 HQ 库，这里只为满足构造参数。
        var context = (HqSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HqSqlSugarContext));
        typeof(HqSqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        typeof(HqSqlSugarContext)
            .GetField("<Configuration>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, new ConfigurationBuilder().Build());
        return context;
    }
}
