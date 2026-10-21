using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.WarehousePicking;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 拣货保护：退货行（AllocQuantity &lt; 0、订货 0）不拣货——不出现在拣货单、不计入拣货单行数 / 件数 / 进度，
/// 不能被扫码拣货，拣货提交时绝不覆盖退货行的发货数量，也不会把它当成空行软删。
/// 同时验证 AllocQuantity 为 NULL 的历史行仍然照常参与拣货（过滤写法不把 NULL 行误排除）。
/// </summary>
public sealed class StoreOrderReturnPickingProtectionTests : IDisposable
{
    private const string OrderGuid = "order-pick-ret";
    private const string StoreCode = "1013";

    private static readonly WarehousePickerContext Alex = new("user-alex", "Alex Chen", false, "user-alex", null);

    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public StoreOrderReturnPickingProtectionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
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
            typeof(WareHouseOrder),
            typeof(WareHouseOrderDetails),
            typeof(Product),
            typeof(WarehouseProduct),
            typeof(ProductSetCode),
            typeof(StoreMultiCodeProduct),
            typeof(ProductLocation),
            typeof(Location),
            typeof(Store),
            typeof(WarehouseOrderPickSession),
            typeof(WarehouseOrderPickRecord),
            typeof(WarehouseOrderPickParticipant),
            typeof(WarehouseOrderPickStockout),
            typeof(WarehouseOrderPickAssignment)
        );
    }

    [Fact]
    public async Task 拣货单不含退货行_NULL发货数量的历史行照常出现()
    {
        await SeedAsync();

        var sheet = (await CreateService().JoinAsync(OrderGuid, Alex)).Data!;

        var details = sheet.Lines.Select(line => line.DetailGuid).OrderBy(item => item).ToList();
        Assert.Equal(["d-cup", "d-null"], details);
        // 退货行的商品条码也不应进入扫码码表
        Assert.DoesNotContain(sheet.Codes, code => code.DetailGuids.Contains("d-ret"));
    }

    [Fact]
    public async Task 退货行不能被扫码拣货_按没有这一行处理()
    {
        await SeedAsync();
        var service = CreateService();
        await service.JoinAsync(OrderGuid, Alex);

        var result = await service.SetLineTotalAsync(
            OrderGuid,
            "d-ret",
            new WarehousePickingSetTotalRequestDto { Total = 1, ExpectedTotal = 0, ClientRequestId = Guid.NewGuid() },
            Alex
        );

        Assert.False(result.Success);
        Assert.Equal(WarehousePickingErrorCodes.LineNotFound, result.ErrorCode);
        Assert.Empty(await _db.Queryable<WarehouseOrderPickRecord>().ToListAsync());
    }

    [Fact]
    public async Task 提交拣货_不覆盖退货行发货数量_也不把它当空行软删_普通行照常写入()
    {
        await SeedAsync();
        var service = CreateService();
        await service.JoinAsync(OrderGuid, Alex);
        await service.SetLineTotalAsync(
            OrderGuid,
            "d-cup",
            new WarehousePickingSetTotalRequestDto { Total = 24, ExpectedTotal = 0, ClientRequestId = Guid.NewGuid() },
            Alex
        );
        await service.SetLineTotalAsync(
            OrderGuid,
            "d-null",
            new WarehousePickingSetTotalRequestDto { Total = 3, ExpectedTotal = 0, ClientRequestId = Guid.NewGuid() },
            Alex
        );

        var submitted = await service.SubmitAsync(OrderGuid, Alex);

        Assert.True(submitted.Success, submitted.Message);
        // 提交结果只统计要拣的行
        Assert.Equal(2, submitted.Data!.LineCount);
        var details = await _db.Queryable<WareHouseOrderDetails>().ToListAsync();
        var ret = details.Single(item => item.DetailGUID == "d-ret");
        Assert.Equal(-3m, ret.AllocQuantity);
        Assert.False(ret.IsDeleted);
        Assert.Equal("damaged", ret.ReturnReason);
        Assert.Equal(-3m * 12.99m, ret.OEMAmount);
        Assert.Equal(24m, details.Single(item => item.DetailGUID == "d-cup").AllocQuantity);
        Assert.Equal(3m, details.Single(item => item.DetailGUID == "d-null").AllocQuantity);
    }

    [Fact]
    public async Task 拣货单列表的行数与件数不计退货行()
    {
        await SeedAsync();

        var list = await CreateService().ListOrdersAsync("all", null);

        var item = list.Data!.Items.Single(order => order.OrderGuid == OrderGuid);
        Assert.Equal(2, item.LineCount);
        Assert.Equal(36m + 6m, item.TotalQuantity);
    }

    [Fact]
    public async Task 拣货进度不含退货行()
    {
        await SeedAsync();
        var service = CreateService();
        await service.JoinAsync(OrderGuid, Alex);

        var progress = (await service.GetProgressAsync(OrderGuid)).Data!;

        Assert.DoesNotContain(progress.Lines, line => line.DetailGuid == "d-ret");
        Assert.Equal(2, progress.Lines.Count);
    }

    private async Task SeedAsync()
    {
        await _db.Insertable(new Store { StoreGUID = "store-guid", StoreCode = StoreCode, StoreName = "Morayfield" }).ExecuteCommandAsync();
        await _db.Insertable(new WareHouseOrder
        {
            OrderGUID = OrderGuid,
            OrderNo = "2026-1961",
            StoreCode = StoreCode,
            FlowStatus = 3,
            OrderDate = new DateTime(2026, 10, 21, 9, 0, 0),
        }).ExecuteCommandAsync();
        await _db.Insertable(new List<WareHouseOrderDetails>
        {
            Detail("d-cup", "P-CUP", quantity: 36, alloc: 36),
            // 历史行：发货数量为 NULL（过滤不能把它误排除）
            Detail("d-null", "P-NULL", quantity: 6, alloc: null),
            // 退货行：订货 0、发货 -3
            Detail("d-ret", "P-RET", quantity: 0, alloc: -3, returnReason: "damaged"),
        }).ExecuteCommandAsync();
        await _db.Insertable(new List<Product>
        {
            new() { ProductCode = "P-CUP", ItemNumber = "HB20931", Barcode = "9312345678905", ProductName = "Cup", ProductType = 0 },
            new() { ProductCode = "P-NULL", ItemNumber = "HB11111", Barcode = "9312345611111", ProductName = "Legacy", ProductType = 0 },
            new() { ProductCode = "P-RET", ItemNumber = "HB22222", Barcode = "9312345622222", ProductName = "Returned", ProductType = 0 },
        }).ExecuteCommandAsync();
        await _db.Insertable(new List<WarehouseProduct>
        {
            new() { ProductCode = "P-CUP", MinOrderQuantity = 12, IsActive = true },
            new() { ProductCode = "P-NULL", MinOrderQuantity = 1, IsActive = true },
            new() { ProductCode = "P-RET", MinOrderQuantity = 1, IsActive = true },
        }).ExecuteCommandAsync();
    }

    private static WareHouseOrderDetails Detail(
        string detailGuid,
        string productCode,
        decimal quantity,
        decimal? alloc,
        string? returnReason = null
    ) => new()
    {
        DetailGUID = detailGuid,
        OrderGUID = OrderGuid,
        StoreCode = StoreCode,
        ProductCode = productCode,
        Quantity = quantity,
        AllocQuantity = alloc,
        OEMPrice = 12.99m,
        OEMAmount = (alloc ?? 0) * 12.99m,
        ImportPrice = 1m,
        ReturnReason = returnReason,
    };

    private WarehousePickingService CreateService() =>
        new(CreateSqlSugarContext(_db), Mock.Of<IProductWarehouseReactService>(), NullLogger<WarehousePickingService>.Instance);

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }
}
