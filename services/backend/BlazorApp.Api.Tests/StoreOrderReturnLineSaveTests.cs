using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Api.Features.StoreOrders.OrderManagement.Domain;
using BlazorApp.Api.Features.StoreOrders.OrderManagement.Infrastructure;
using BlazorApp.Api.Features.StoreOrders.PasteReplace.Infrastructure;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 订单明细保存层的退货行规则（SQLite 真实落库）：负数发货仅当 有 Warehouse.StoreOrderReturn、订货数量为 0、
/// 原因在码表内、订单状态可编辑 才放行；其余一律中文拒绝并回滚。覆盖单行更新 / 加入 / 批量更新（按商品与按明细两条路径）/ 删除 / 粘贴。
/// </summary>
public sealed class StoreOrderReturnLineSaveTests : IDisposable
{
    private const string OrderGuid = "order-ret";
    private const string OtherOrderGuid = "order-prev";
    private const string StoreCode = "1013";

    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;
    private readonly SqlSugarContext _context;

    public StoreOrderReturnLineSaveTests()
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
            typeof(WarehouseProduct)
        );
        _context = CreateSqlSugarContext(_db);
    }

    // ---------- 新建退货行 ----------

    [Fact]
    public async Task 有权限_单行更新写入负数发货_落库退货行并带原因处置备注()
    {
        await SeedAsync(flowStatus: 1);
        var store = CreateStore(canRecordReturns: true);

        var result = await store.UpdateOrderLineAsync(Update("P-CUP", -3, reason: "wrongItem", disposition: 0, note: "pallet 4"));

        Assert.True(result.Success, result.ErrorMessage);
        var detail = await _db.Queryable<WareHouseOrderDetails>()
            .SingleAsync(item => item.OrderGUID == OrderGuid && item.ProductCode == "P-CUP");
        Assert.Equal(0m, detail.Quantity);
        Assert.Equal(-3m, detail.AllocQuantity);
        Assert.Equal("wrongItem", detail.ReturnReason);
        Assert.Equal(0, detail.ReturnDisposition);
        Assert.Equal("pallet 4", detail.ReturnNote);
        Assert.False(detail.IsDeleted);
        // 订货金额只按订货数量，退货行订货为 0，所以表头进口金额不受影响；发货金额冲减在 OEM 金额上体现为负数。
        Assert.Equal(0m, detail.ImportAmount);
        Assert.Equal(-3m * 12.99m, detail.OEMAmount);
        var order = await _db.Queryable<WareHouseOrder>().SingleAsync(item => item.OrderGUID == OrderGuid);
        Assert.Equal(0m, order.ImportTotalAmount);
    }

    [Fact]
    public async Task 退货行进口价默认取该店该商品最近一次正发货价_忽略退货行与零发货行与其它店()
    {
        await SeedAsync(flowStatus: 1);
        await _db.Insertable(new List<WareHouseOrderDetails>
        {
            // 较早的正发货：4.10
            Prior("d-old", OtherOrderGuid, StoreCode, "P-CUP", alloc: 10, importPrice: 4.10m, createdAt: new DateTime(2026, 9, 1)),
            // 最近一次正发货：4.50（应取这条）
            Prior("d-new", OtherOrderGuid, StoreCode, "P-CUP", alloc: 8, importPrice: 4.50m, createdAt: new DateTime(2026, 10, 1)),
            // 更晚但不算：发货 0、退货行、别的店
            Prior("d-zero", OtherOrderGuid, StoreCode, "P-CUP", alloc: 0, importPrice: 9.99m, createdAt: new DateTime(2026, 10, 10)),
            Prior("d-ret", OtherOrderGuid, StoreCode, "P-CUP", alloc: -2, importPrice: 8.88m, createdAt: new DateTime(2026, 10, 11)),
            Prior("d-other-store", OtherOrderGuid, "9999", "P-CUP", alloc: 5, importPrice: 7.77m, createdAt: new DateTime(2026, 10, 12)),
        }).ExecuteCommandAsync();

        var result = await CreateStore(canRecordReturns: true)
            .UpdateOrderLineAsync(Update("P-CUP", -2, reason: "damaged"));

        Assert.True(result.Success, result.ErrorMessage);
        var detail = await _db.Queryable<WareHouseOrderDetails>()
            .SingleAsync(item => item.OrderGUID == OrderGuid && item.ProductCode == "P-CUP");
        Assert.Equal(4.50m, detail.ImportPrice);
    }

    [Fact]
    public async Task 取不到上次发货价_回落商品进口价_请求带了价格则以请求为准()
    {
        await SeedAsync(flowStatus: 1);
        var store = CreateStore(canRecordReturns: true);

        var fallback = await store.UpdateOrderLineAsync(Update("P-CUP", -1, reason: "damaged"));
        var explicitPrice = await store.UpdateOrderLineAsync(Update("P-SPONGE", -1, reason: "damaged", importPrice: 2.25m));

        Assert.True(fallback.Success, fallback.ErrorMessage);
        Assert.True(explicitPrice.Success, explicitPrice.ErrorMessage);
        var details = await _db.Queryable<WareHouseOrderDetails>().Where(item => item.OrderGUID == OrderGuid).ToListAsync();
        Assert.Equal(5.00m, details.Single(item => item.ProductCode == "P-CUP").ImportPrice);
        Assert.Equal(2.25m, details.Single(item => item.ProductCode == "P-SPONGE").ImportPrice);
    }

    [Fact]
    public async Task 没有权限_负数发货被拒绝_不写入任何行()
    {
        await SeedAsync(flowStatus: 1);

        var result = await CreateStore(canRecordReturns: false)
            .UpdateOrderLineAsync(Update("P-CUP", -3, reason: "damaged"));

        Assert.False(result.Success);
        Assert.Contains("权限", result.ErrorMessage);
        Assert.Empty(await _db.Queryable<WareHouseOrderDetails>().Where(item => item.OrderGUID == OrderGuid).ToListAsync());
    }

    [Fact]
    public async Task 本单该商品已有订货_退货被拒绝_原行不变()
    {
        await SeedAsync(flowStatus: 1);
        await _db.Insertable(Detail("d-cup", "P-CUP", quantity: 6, alloc: 6)).ExecuteCommandAsync();

        var result = await CreateStore(canRecordReturns: true)
            .UpdateOrderLineAsync(Update("P-CUP", -2, reason: "damaged"));

        Assert.False(result.Success);
        Assert.Equal("该商品本单已有订货，请退到另一张订单", result.ErrorMessage);
        var detail = await _db.Queryable<WareHouseOrderDetails>().SingleAsync(item => item.DetailGUID == "d-cup");
        Assert.Equal(6m, detail.AllocQuantity);
        Assert.Null(detail.ReturnReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("changedMind")]
    public async Task 没有原因或原因不在码表_退货被拒绝(string? reason)
    {
        await SeedAsync(flowStatus: 1);

        var result = await CreateStore(canRecordReturns: true)
            .UpdateOrderLineAsync(Update("P-CUP", -2, reason: reason));

        Assert.False(result.Success);
        Assert.Contains("退货原因", result.ErrorMessage);
        Assert.Empty(await _db.Queryable<WareHouseOrderDetails>().Where(item => item.OrderGUID == OrderGuid).ToListAsync());
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    // 已完成（2）只读，退货要加到当前未完成订单或新建订单
    [InlineData(2, false)]
    public async Task 订单状态可编辑才能录入退货(int flowStatus, bool allowed)
    {
        await SeedAsync(flowStatus);

        var result = await CreateStore(canRecordReturns: true)
            .UpdateOrderLineAsync(Update("P-CUP", -2, reason: "quality"));

        Assert.Equal(allowed, result.Success);
        if (!allowed)
        {
            Assert.Equal("Order not found or not editable", result.ErrorMessage);
        }
    }

    [Fact]
    public async Task 退货字段只能跟负数发货一起提交_正数发货带原因被拒绝()
    {
        await SeedAsync(flowStatus: 1);

        var result = await CreateStore(canRecordReturns: true)
            .UpdateOrderLineAsync(Update("P-CUP", 5, reason: "damaged"));

        Assert.False(result.Success);
        Assert.Contains("退货", result.ErrorMessage);
    }

    [Fact]
    public async Task 普通订货编辑不会触发退货权限查询()
    {
        await SeedAsync(flowStatus: 1);
        await _db.Insertable(Detail("d-cup", "P-CUP", quantity: 6, alloc: 6)).ExecuteCommandAsync();
        var permission = new FakeReturnPermission(canRecordReturns: false);

        var result = await CreateStore(permission).UpdateOrderLineAsync(Update("P-CUP", 8));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(0, permission.Calls);
        Assert.Equal(8m, (await _db.Queryable<WareHouseOrderDetails>().SingleAsync(item => item.DetailGUID == "d-cup")).AllocQuantity);
    }

    // ---------- 修改 / 删除已有退货行 ----------

    [Fact]
    public async Task 有权限_按商品批量更新_只改退货数量时沿用原原因()
    {
        await SeedAsync(flowStatus: 1);
        await _db.Insertable(ReturnDetail("d-ret", "P-CUP", alloc: -3, reason: "quality", disposition: 1, note: "mouldy")).ExecuteCommandAsync();

        // 带进口价 → 走按商品编码的通用路径
        var result = await CreateStore(canRecordReturns: true).BatchUpdateOrderLineAsync(
            new BatchUpdateOrderLineInput(
                OrderGuid,
                [new BatchUpdateOrderLineItemInput(null, "P-CUP", -7, 5.5m, false)]
            )
        );

        Assert.True(result.Success, result.ErrorMessage);
        var detail = await _db.Queryable<WareHouseOrderDetails>().SingleAsync(item => item.DetailGUID == "d-ret");
        Assert.Equal(-7m, detail.AllocQuantity);
        Assert.Equal(5.5m, detail.ImportPrice);
        Assert.Equal("quality", detail.ReturnReason);
        Assert.Equal(1, detail.ReturnDisposition);
        Assert.Equal("mouldy", detail.ReturnNote);
        Assert.False(detail.IsDeleted);
    }

    [Fact]
    public async Task 有权限_按明细批量更新_负数不再被一刀切拒绝_且退货行不会被当成空行软删()
    {
        await SeedAsync(flowStatus: 1);
        await _db.Insertable(new List<WareHouseOrderDetails>
        {
            ReturnDetail("d-ret", "P-CUP", alloc: -3, reason: "damaged"),
            Detail("d-extra", "P-SPONGE", quantity: 0, alloc: 4),
        }).ExecuteCommandAsync();

        var result = await CreateStore(canRecordReturns: true).BatchUpdateOrderLineAsync(
            new BatchUpdateOrderLineInput(
                OrderGuid,
                [
                    new BatchUpdateOrderLineItemInput("d-ret", "P-CUP", -5, null, false),
                    // 主动配货行（订货 0）改成退货：需要带原因
                    new BatchUpdateOrderLineItemInput(
                        "d-extra",
                        "P-SPONGE",
                        -1,
                        null,
                        false,
                        new StoreOrderReturnLineRequest("overShipped", null, null)
                    ),
                ]
            )
        );

        Assert.True(result.Success, result.ErrorMessage);
        var details = await _db.Queryable<WareHouseOrderDetails>().ToListAsync();
        var ret = details.Single(item => item.DetailGUID == "d-ret");
        Assert.Equal(-5m, ret.AllocQuantity);
        Assert.False(ret.IsDeleted);
        Assert.Equal("damaged", ret.ReturnReason);
        var extra = details.Single(item => item.DetailGUID == "d-extra");
        Assert.Equal(-1m, extra.AllocQuantity);
        Assert.False(extra.IsDeleted);
        Assert.Equal("overShipped", extra.ReturnReason);
    }

    [Fact]
    public async Task 没有权限_按明细批量更新负数_整批回滚()
    {
        await SeedAsync(flowStatus: 1);
        await _db.Insertable(new List<WareHouseOrderDetails>
        {
            Detail("d-cup", "P-CUP", quantity: 6, alloc: 6),
            Detail("d-extra", "P-SPONGE", quantity: 0, alloc: 4),
        }).ExecuteCommandAsync();

        var result = await CreateStore(canRecordReturns: false).BatchUpdateOrderLineAsync(
            new BatchUpdateOrderLineInput(
                OrderGuid,
                [
                    new BatchUpdateOrderLineItemInput("d-cup", "P-CUP", 7, null, false),
                    new BatchUpdateOrderLineItemInput(
                        "d-extra",
                        "P-SPONGE",
                        -1,
                        null,
                        false,
                        new StoreOrderReturnLineRequest("damaged", null, null)
                    ),
                ]
            )
        );

        Assert.False(result.Success);
        Assert.Contains("权限", result.ErrorMessage);
        var details = await _db.Queryable<WareHouseOrderDetails>().ToListAsync();
        Assert.Equal(6m, details.Single(item => item.DetailGUID == "d-cup").AllocQuantity);
        Assert.Equal(4m, details.Single(item => item.DetailGUID == "d-extra").AllocQuantity);
    }

    [Fact]
    public async Task 退货行清零后软删并清掉退货字段_无权限者不能清零()
    {
        await SeedAsync(flowStatus: 1);
        await _db.Insertable(ReturnDetail("d-ret", "P-CUP", alloc: -3, reason: "nearExpiry", disposition: 1, note: "x")).ExecuteCommandAsync();

        var denied = await CreateStore(canRecordReturns: false).UpdateOrderLineAsync(Update("P-CUP", 0));
        var afterDenied = await _db.Queryable<WareHouseOrderDetails>().SingleAsync(item => item.DetailGUID == "d-ret");
        var allowed = await CreateStore(canRecordReturns: true).UpdateOrderLineAsync(Update("P-CUP", 0));
        var afterAllowed = await _db.Queryable<WareHouseOrderDetails>().SingleAsync(item => item.DetailGUID == "d-ret");

        Assert.False(denied.Success);
        Assert.Equal(-3m, afterDenied.AllocQuantity);
        Assert.False(afterDenied.IsDeleted);
        Assert.True(allowed.Success, allowed.ErrorMessage);
        Assert.True(afterAllowed.IsDeleted);
        Assert.Null(afterAllowed.ReturnReason);
        Assert.Null(afterAllowed.ReturnDisposition);
        Assert.Null(afterAllowed.ReturnNote);
    }

    [Fact]
    public async Task 无权限者只改退货行价格同样被拒绝()
    {
        await SeedAsync(flowStatus: 1);
        await _db.Insertable(ReturnDetail("d-ret", "P-CUP", alloc: -3, reason: "damaged")).ExecuteCommandAsync();

        // 批量里只传价格（Quantity 为空 = 不改发货数量）
        var result = await CreateStore(canRecordReturns: false).BatchUpdateOrderLineAsync(
            new BatchUpdateOrderLineInput(
                OrderGuid,
                [new BatchUpdateOrderLineItemInput(null, "P-CUP", null, 9.99m, false)]
            )
        );

        Assert.False(result.Success);
        Assert.Equal(1m, (await _db.Queryable<WareHouseOrderDetails>().SingleAsync(item => item.DetailGUID == "d-ret")).ImportPrice);
    }

    [Fact]
    public async Task 删除退货行需要权限_且删除后不留退货字段()
    {
        await SeedAsync(flowStatus: 1);
        await _db.Insertable(ReturnDetail("d-ret", "P-CUP", alloc: -3, reason: "damaged")).ExecuteCommandAsync();
        var input = new RemoveOrderLineInput(OrderGuid, "d-ret");

        var denied = await CreateStore(canRecordReturns: false).RemoveOrderLineAsync(input);
        var allowed = await CreateStore(canRecordReturns: true).RemoveOrderLineAsync(input);

        Assert.False(denied.Success);
        Assert.True(allowed.Success, allowed.ErrorMessage);
        var detail = await _db.Queryable<WareHouseOrderDetails>().SingleAsync(item => item.DetailGUID == "d-ret");
        Assert.True(detail.IsDeleted);
        Assert.Null(detail.ReturnReason);
    }

    // ---------- 加入商品路径 ----------

    [Fact]
    public async Task 加入商品_负数数量按设置建退货行_重复提交不叠加()
    {
        await SeedAsync(flowStatus: 1);
        var store = CreateStore(canRecordReturns: true);
        var input = new AddOrderLineInput(OrderGuid, "P-CUP", -4, new StoreOrderReturnLineRequest("damaged", null, null));

        var first = await store.AddOrderLineAsync(input);
        var second = await store.AddOrderLineAsync(input);

        Assert.True(first.Success, first.ErrorMessage);
        Assert.True(second.Success, second.ErrorMessage);
        var detail = await _db.Queryable<WareHouseOrderDetails>().SingleAsync(item => item.OrderGUID == OrderGuid);
        Assert.Equal(-4m, detail.AllocQuantity);
    }

    [Fact]
    public async Task 加入商品_正数数量沿用旧语义_新建行发货数量取最小订货量()
    {
        await SeedAsync(flowStatus: 1);

        var result = await CreateStore(canRecordReturns: false).AddOrderLineAsync(new AddOrderLineInput(OrderGuid, "P-CUP", 1));

        Assert.True(result.Success, result.ErrorMessage);
        var detail = await _db.Queryable<WareHouseOrderDetails>().SingleAsync(item => item.OrderGUID == OrderGuid);
        Assert.Equal(12m, detail.AllocQuantity);
        Assert.Null(detail.ReturnReason);
    }

    [Fact]
    public async Task 加入商品_正数加入到已有退货行被拒绝_不会悄悄抵消退货()
    {
        await SeedAsync(flowStatus: 1);
        await _db.Insertable(ReturnDetail("d-ret", "P-CUP", alloc: -12, reason: "damaged")).ExecuteCommandAsync();

        var result = await CreateStore(canRecordReturns: true).AddOrderLineAsync(new AddOrderLineInput(OrderGuid, "P-CUP", 1));

        Assert.False(result.Success);
        Assert.Contains("退货行", result.ErrorMessage);
        Assert.Equal(-12m, (await _db.Queryable<WareHouseOrderDetails>().SingleAsync(item => item.DetailGUID == "d-ret")).AllocQuantity);
    }

    [Fact]
    public async Task 只含退货行的订单_表头汇总不报错也不被拦下()
    {
        await SeedAsync(flowStatus: 1);
        var store = CreateStore(canRecordReturns: true);

        Assert.True((await store.UpdateOrderLineAsync(Update("P-CUP", -3, reason: "damaged"))).Success);
        Assert.True((await store.UpdateOrderLineAsync(Update("P-SPONGE", -2, reason: "quality"))).Success);

        var order = await _db.Queryable<WareHouseOrder>().SingleAsync(item => item.OrderGUID == OrderGuid);
        // 订货口径的表头：退货行订货为 0，数量 / 进口金额为 0；OEM 金额是发货口径，退货冲减后为负。
        Assert.Equal(0m, order.ImportTotalAmount);
        Assert.Equal(-3m * 12.99m + -2m * 3.99m, order.OEMTotalAmount);
    }

    // ---------- 粘贴 ----------

    [Fact]
    public async Task 粘贴覆盖发货数量_不会碰活的退货行_也不会把它当空行删掉()
    {
        await SeedAsync(flowStatus: 1);
        await _db.Insertable(ReturnDetail("d-ret", "P-CUP", alloc: -3, reason: "damaged")).ExecuteCommandAsync();
        var infrastructure = new PasteReplaceOrderLinesInfrastructure(_context, new HttpContextAccessor());
        var order = await _db.Queryable<WareHouseOrder>().SingleAsync(item => item.OrderGUID == OrderGuid);

        var plan = await infrastructure.PrepareAsync(
            order,
            [new ProductQuantityDto { ProductCode = "P-CUP", Quantity = 0, Action = "replace" }],
            StoreOrderPasteTargetFields.AllocQuantity
        );

        Assert.Empty(plan.DetailGuidsToDelete);
        Assert.Empty(plan.DetailsToUpdate);
        Assert.Empty(plan.DetailsToInsert);
        Assert.Equal(-3m, (await _db.Queryable<WareHouseOrderDetails>().SingleAsync(item => item.DetailGUID == "d-ret")).AllocQuantity);
    }

    [Fact]
    public async Task 粘贴复活已软删的退货行_先清掉退货痕迹当普通行处理()
    {
        await SeedAsync(flowStatus: 1);
        var deletedReturn = ReturnDetail("d-ret", "P-CUP", alloc: -3, reason: "damaged", disposition: 1, note: "old");
        deletedReturn.IsDeleted = true;
        await _db.Insertable(deletedReturn).ExecuteCommandAsync();
        var infrastructure = new PasteReplaceOrderLinesInfrastructure(_context, new HttpContextAccessor());
        var order = await _db.Queryable<WareHouseOrder>().SingleAsync(item => item.OrderGUID == OrderGuid);

        var plan = await infrastructure.PrepareAsync(
            order,
            [new ProductQuantityDto { ProductCode = "P-CUP", Quantity = 6, Action = "replace" }],
            StoreOrderPasteTargetFields.AllocQuantity
        );

        var revived = Assert.Single(plan.DetailsToUpdate);
        Assert.Equal(6m, revived.AllocQuantity);
        Assert.Null(revived.ReturnReason);
        Assert.Null(revived.ReturnDisposition);
        Assert.Null(revived.ReturnNote);
        Assert.False(revived.IsDeleted);
    }

    // ---------- 辅助 ----------

    private async Task SeedAsync(int flowStatus)
    {
        await _db.Insertable(new List<WareHouseOrder>
        {
            new() { OrderGUID = OrderGuid, OrderNo = "2026-1961", StoreCode = StoreCode, FlowStatus = flowStatus, OrderDate = new DateTime(2026, 10, 21) },
            new() { OrderGUID = OtherOrderGuid, OrderNo = "2026-1956", StoreCode = StoreCode, FlowStatus = 2, OrderDate = new DateTime(2026, 10, 1) },
        }).ExecuteCommandAsync();
        await _db.Insertable(new List<Product>
        {
            new() { ProductCode = "P-CUP", ItemNumber = "HB20931", ProductName = "Cup" },
            new() { ProductCode = "P-SPONGE", ItemNumber = "HB18806", ProductName = "Sponge" },
        }).ExecuteCommandAsync();
        await _db.Insertable(new List<WarehouseProduct>
        {
            new() { ProductCode = "P-CUP", OEMPrice = 12.99m, ImportPrice = 5.00m, MinOrderQuantity = 12, IsActive = true },
            new() { ProductCode = "P-SPONGE", OEMPrice = 3.99m, ImportPrice = 1.00m, MinOrderQuantity = 1, IsActive = true },
        }).ExecuteCommandAsync();
    }

    private static UpdateOrderLineInput Update(
        string productCode,
        decimal quantity,
        string? reason = null,
        int? disposition = null,
        string? note = null,
        decimal? importPrice = null
    ) =>
        new(
            OrderGuid,
            productCode,
            quantity,
            importPrice,
            false,
            new StoreOrderReturnLineRequest(reason, disposition, note)
        );

    private static WareHouseOrderDetails Detail(string detailGuid, string productCode, decimal quantity, decimal alloc) => new()
    {
        DetailGUID = detailGuid,
        OrderGUID = OrderGuid,
        StoreCode = StoreCode,
        ProductCode = productCode,
        Quantity = quantity,
        AllocQuantity = alloc,
        OEMPrice = productCode == "P-CUP" ? 12.99m : 3.99m,
        ImportPrice = 1m,
        ImportAmount = quantity,
        OEMAmount = alloc * (productCode == "P-CUP" ? 12.99m : 3.99m),
    };

    private static WareHouseOrderDetails ReturnDetail(
        string detailGuid,
        string productCode,
        decimal alloc,
        string reason,
        int? disposition = null,
        string? note = null
    )
    {
        var detail = Detail(detailGuid, productCode, quantity: 0, alloc);
        detail.ReturnReason = reason;
        detail.ReturnDisposition = disposition;
        detail.ReturnNote = note;
        return detail;
    }

    private static WareHouseOrderDetails Prior(
        string detailGuid,
        string orderGuid,
        string storeCode,
        string productCode,
        decimal alloc,
        decimal importPrice,
        DateTime createdAt
    ) => new()
    {
        DetailGUID = detailGuid,
        OrderGUID = orderGuid,
        StoreCode = storeCode,
        ProductCode = productCode,
        Quantity = Math.Max(alloc, 0),
        AllocQuantity = alloc,
        ImportPrice = importPrice,
        CreatedAt = createdAt,
    };

    private SqlSugarStoreOrderLineCommandStore CreateStore(bool canRecordReturns) =>
        CreateStore(new FakeReturnPermission(canRecordReturns));

    private SqlSugarStoreOrderLineCommandStore CreateStore(IStoreOrderReturnPermission permission)
    {
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.SetupGet(item => item.HttpContext).Returns(new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimsIdentity.DefaultNameClaimType, "warehouse.manager")],
                "test"
            )),
        });
        var actor = new StoreOrderActorContext(accessor.Object);
        var costCoordinator = new StoreOrderProductCostCoordinator(_context);
        var persistence = new StoreOrderManagementPersistence(_context, actor, costCoordinator, permission);
        return new SqlSugarStoreOrderLineCommandStore(
            _context,
            new StoreOrderTransactionExecutor(_context),
            persistence,
            actor,
            costCoordinator,
            Mock.Of<IWarehouseProductChangeHistoryService>()
        );
    }

    /// <summary>测试用权限判定：记录被查询的次数。</summary>
    private sealed class FakeReturnPermission(bool canRecordReturns) : IStoreOrderReturnPermission
    {
        public int Calls { get; private set; }

        public Task<bool> CanRecordReturnsAsync()
        {
            Calls += 1;
            return Task.FromResult(canRecordReturns);
        }
    }

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
