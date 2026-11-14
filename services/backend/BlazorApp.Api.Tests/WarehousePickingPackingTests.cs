using System.Security.Claims;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Features.Packing;
using BlazorApp.Api.Features.WarehousePicking;
using BlazorApp.Api.Interfaces.React;
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
/// 二期：仓库拣货与装箱联动。边拣边装（拣货记录与箱同一事务）、先拣后装（绝对数、天然幂等）、
/// 提交对账（空箱自动作废 / 未封箱 / 数量不一致）、装箱进度汇总；没有任何箱时提交与老版 App 的行为完全不变。
/// </summary>
public sealed class WarehousePickingPackingTests : IDisposable
{
    private const string OrderGuid = "order-1";
    private const string OtherOrderGuid = "order-2";
    private const string DraftOrderGuid = "order-draft";
    private const string StoreCode = "1013";

    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    private static readonly WarehousePickerContext Alex = new("user-alex", "Alex Chen", false, "user-alex", null);
    private static readonly WarehousePickerContext Mia = new("user-mia", "Mia Wong", false, null, "PDA-01");

    public WarehousePickingPackingTests()
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
        _db.CodeFirst.InitTables(PickingPackingTestSupport.PackTables);
        Seed();
    }

    // ---- 边拣边装：一次请求同时写拣货记录与箱 ----

    [Fact]
    public async Task 边拣边装_一次扫码同时写记录与箱_件数等于中包数_记录带箱号()
    {
        var service = await JoinedServiceAsync();
        var carton = await NewCartonAsync(service);

        var first = await service.AppendRecordAsync(OrderGuid, Scan("d-cup", carton), Alex);
        var second = await service.AppendRecordAsync(OrderGuid, Scan("d-cup", carton), Alex);

        Assert.True(first.Success, first.Message);
        Assert.Equal(12, first.Data!.AppliedDelta);
        Assert.Equal(12, first.Data.Line.PickedTotal);
        Assert.Equal(carton, first.Data.Carton!.CartonGuid);
        Assert.StartsWith("BX", first.Data.Carton.CartonNo);
        Assert.Equal(1, first.Data.Carton.SkuCount);
        Assert.Equal(12, first.Data.Carton.PieceCount);
        Assert.Equal(12, first.Data.Carton.ItemPieces);
        Assert.Equal(24, second.Data!.Line.PickedTotal);
        Assert.Equal(24, second.Data.Carton!.ItemPieces);
        Assert.Equal(24, await ItemQuantityAsync(carton, "P-CUP"));
        var records = await _db.Queryable<WarehouseOrderPickRecord>().ToListAsync();
        Assert.Equal(2, records.Count);
        Assert.All(records, record => Assert.Equal(carton, record.CartonGUID));
    }

    [Fact]
    public async Task 边拣边装_显式件数同步到箱_箱上记录的创建人是拣货人()
    {
        var service = await JoinedServiceAsync();
        var carton = await NewCartonAsync(service);
        var request = Scan("d-sponge", carton);
        request.Pieces = 5;

        var result = await service.AppendRecordAsync(OrderGuid, request, Alex);

        Assert.True(result.Success, result.Message);
        Assert.Equal(5, result.Data!.Carton!.ItemPieces);
        Assert.Equal(5, await ItemQuantityAsync(carton, "P-SPONGE"));
        var row = await _db.Queryable<PackCarton>().SingleAsync(item => item.CartonGUID == carton);
        Assert.Equal(PackOwners.Warehouse, row.OwnerCode);
        Assert.Equal(OrderGuid, row.OrderGUID);
        Assert.Equal("user-alex", row.CreatedByUserGuid);
        Assert.Equal("Alex Chen", row.CreatedByName);
    }

    [Fact]
    public async Task 边拣边装_不带箱的老请求行为不变_不建箱不写箱号()
    {
        var service = await JoinedServiceAsync();

        var result = await service.AppendRecordAsync(OrderGuid, Scan("d-cup", null), Alex);

        Assert.True(result.Success, result.Message);
        Assert.Equal(12, result.Data!.Line.PickedTotal);
        Assert.Null(result.Data.Carton);
        Assert.Null((await _db.Queryable<WarehouseOrderPickRecord>().SingleAsync()).CartonGUID);
        Assert.Equal(0, await _db.Queryable<PackCarton>().CountAsync());
        Assert.Equal(0, await _db.Queryable<PackCartonItem>().CountAsync());
    }

    [Fact]
    public async Task 边拣边装_箱不存在_失败且没有拣货记录也没有箱变化()
    {
        var service = await JoinedServiceAsync();

        var result = await service.AppendRecordAsync(OrderGuid, Scan("d-cup", "no-such-carton"), Alex);

        Assert.False(result.Success);
        Assert.Equal(404, result.StatusCode);
        Assert.Equal(PackingErrorCodes.CartonNotFound, result.ErrorCode);
        Assert.Equal(0, await _db.Queryable<WarehouseOrderPickRecord>().CountAsync());
        Assert.Equal(0, await _db.Queryable<PackCartonItem>().CountAsync());
        // 客户端据此纠正：带回这一行当前的拣货合计。
        Assert.Equal(0, result.Data!.Line.PickedTotal);
    }

    [Fact]
    public async Task 边拣边装_别的订单的箱不能装_失败且两边都没有变化()
    {
        var service = await JoinedServiceAsync();
        var otherCarton = await NewCartonAsync(service, OtherOrderGuid);

        var result = await service.AppendRecordAsync(OrderGuid, Scan("d-cup", otherCarton), Alex);

        Assert.False(result.Success);
        Assert.Equal(PackingErrorCodes.CartonNotFound, result.ErrorCode);
        Assert.Equal(0, await _db.Queryable<WarehouseOrderPickRecord>().CountAsync());
        Assert.Equal(0, await _db.Queryable<PackCartonItem>().CountAsync());
    }

    [Fact]
    public async Task 边拣边装_分店持有的箱不能装_即使挂着本订单()
    {
        var service = await JoinedServiceAsync();
        await _db.Insertable(new PackCarton
        {
            CartonGUID = "store-carton",
            CartonNo = "BX2611140900",
            Status = PackCartonStatuses.Packing,
            OwnerCode = StoreCode,
            OriginCode = StoreCode,
            OriginName = "Morayfield",
            OrderGUID = OrderGuid,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedByUserGuid = "u-store",
            CreatedByName = "Lily",
            UpdatedAtUtc = DateTime.UtcNow,
        }).ExecuteCommandAsync();

        var result = await service.AppendRecordAsync(OrderGuid, Scan("d-cup", "store-carton"), Alex);

        Assert.Equal(PackingErrorCodes.CartonNotFound, result.ErrorCode);
        Assert.Equal(0, await _db.Queryable<WarehouseOrderPickRecord>().CountAsync());
        Assert.Equal(0, await _db.Queryable<PackCartonItem>().CountAsync());
    }

    [Fact]
    public async Task 边拣边装_已封箱不能再装_失败且记录与箱都不变()
    {
        var service = await JoinedServiceAsync();
        var carton = await NewCartonAsync(service);
        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", carton), Alex);
        Assert.True((await service.SealCartonAsync(OrderGuid, carton, new WarehousePickingSealCartonRequestDto(), Alex)).Success);

        var result = await service.AppendRecordAsync(OrderGuid, Scan("d-cup", carton), Alex);

        Assert.False(result.Success);
        Assert.Equal(409, result.StatusCode);
        Assert.Equal(PackingErrorCodes.CartonNotPacking, result.ErrorCode);
        Assert.Equal(1, await _db.Queryable<WarehouseOrderPickRecord>().CountAsync());
        Assert.Equal(12, await ItemQuantityAsync(carton, "P-CUP"));
    }

    [Fact]
    public async Task 边拣边装_减一个中包从箱里扣_记录写负数()
    {
        var service = await JoinedServiceAsync();
        var carton = await NewCartonAsync(service);
        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", carton), Alex);
        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", carton), Alex);

        var result = await service.AppendRecordAsync(OrderGuid, Button("d-cup", WarehouseOrderPickSources.Decrement, carton), Alex);

        Assert.True(result.Success, result.Message);
        Assert.Equal(-12, result.Data!.AppliedDelta);
        Assert.Equal(12, result.Data.Line.PickedTotal);
        Assert.Equal(12, result.Data.Carton!.ItemPieces);
        Assert.Equal(12, await ItemQuantityAsync(carton, "P-CUP"));
        Assert.Equal(3, await _db.Queryable<WarehouseOrderPickRecord>().CountAsync());
    }

    [Fact]
    public async Task 边拣边装_箱里不够减_409_CARTON_QTY_INSUFFICIENT_拣货记录也不写()
    {
        var service = await JoinedServiceAsync();
        var cartonA = await NewCartonAsync(service);
        var cartonB = await NewCartonAsync(service);
        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", cartonA), Alex);
        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", cartonB), Alex);
        // 先拣后装把 A 箱改成 6 件，此时从 A 箱减一个中包（12 件）不够。
        Assert.True((await service.SetCartonItemAsync(OrderGuid, cartonA, "P-CUP", new WarehousePickingSetCartonItemRequestDto { Quantity = 6 }, Alex)).Success);

        var result = await service.AppendRecordAsync(OrderGuid, Button("d-cup", WarehouseOrderPickSources.Decrement, cartonA), Alex);

        Assert.False(result.Success);
        Assert.Equal(409, result.StatusCode);
        Assert.Equal(PackingErrorCodes.CartonQtyInsufficient, result.ErrorCode);
        Assert.Equal(2, await _db.Queryable<WarehouseOrderPickRecord>().CountAsync());
        Assert.Equal(6, await ItemQuantityAsync(cartonA, "P-CUP"));
        Assert.Equal(12, await ItemQuantityAsync(cartonB, "P-CUP"));
        // 带回这一行与箱的现状，界面提示「这只箱里只有 6 件」。
        Assert.Equal(24, result.Data!.Line.PickedTotal);
        Assert.Equal(6, result.Data.Carton!.ItemPieces);
    }

    [Fact]
    public async Task 边拣边装_改总数的差额同步到箱_调小后箱里不够时整体失败()
    {
        var service = await JoinedServiceAsync();
        var carton = await NewCartonAsync(service);

        var up = await service.SetLineTotalAsync(OrderGuid, "d-cup", SetTotal(24, 0, carton), Alex);
        var down = await service.SetLineTotalAsync(OrderGuid, "d-cup", SetTotal(18, 24, carton), Alex);
        // 不带箱的拣货再加 12：拣货合计 30，箱里只有 18。
        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", null), Alex);
        var tooMuch = await service.SetLineTotalAsync(OrderGuid, "d-cup", SetTotal(0, 30, carton), Alex);

        Assert.True(up.Success, up.Message);
        Assert.Equal(24, up.Data!.Carton!.ItemPieces);
        Assert.True(down.Success, down.Message);
        Assert.Equal(-6, down.Data!.AppliedDelta);
        Assert.Equal(18, down.Data.Carton!.ItemPieces);
        Assert.False(tooMuch.Success);
        Assert.Equal(PackingErrorCodes.CartonQtyInsufficient, tooMuch.ErrorCode);
        Assert.Equal(18, await ItemQuantityAsync(carton, "P-CUP"));
        Assert.Equal(30, (await service.GetProgressAsync(OrderGuid)).Data!.Lines.Single(line => line.DetailGuid == "d-cup").PickedTotal);
    }

    [Fact]
    public async Task 边拣边装_同一请求编号重放只返回上次结果_不会再装一次箱()
    {
        var service = await JoinedServiceAsync();
        var carton = await NewCartonAsync(service);
        var request = Scan("d-cup", carton);

        var first = await service.AppendRecordAsync(OrderGuid, request, Alex);
        var replay = await service.AppendRecordAsync(OrderGuid, request, Alex);

        Assert.True(replay.Success, replay.Message);
        Assert.True(replay.Data!.Duplicate);
        Assert.Equal(first.Data!.AppliedDelta, replay.Data.AppliedDelta);
        Assert.Equal(12, replay.Data.Line.PickedTotal);
        // 重放回显上次装的那只箱现状，件数没有翻倍。
        Assert.Equal(carton, replay.Data.Carton!.CartonGuid);
        Assert.Equal(12, replay.Data.Carton.ItemPieces);
        Assert.Equal(12, await ItemQuantityAsync(carton, "P-CUP"));
        Assert.Equal(1, await _db.Queryable<WarehouseOrderPickRecord>().CountAsync());
    }

    [Fact]
    public async Task 边拣边装_改总数同一请求编号重放也不会再装箱()
    {
        var service = await JoinedServiceAsync();
        var carton = await NewCartonAsync(service);
        var request = SetTotal(24, 0, carton);

        await service.SetLineTotalAsync(OrderGuid, "d-cup", request, Alex);
        var replay = await service.SetLineTotalAsync(OrderGuid, "d-cup", request, Alex);

        Assert.True(replay.Data!.Duplicate);
        Assert.Equal(24, replay.Data.Carton!.ItemPieces);
        Assert.Equal(24, await ItemQuantityAsync(carton, "P-CUP"));
    }

    // ---- 提交对账 ----

    [Fact]
    public async Task 提交_订单没有任何箱_行为与旧版一致_响应里没有箱数()
    {
        var service = await JoinedServiceAsync();
        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", null), Alex);

        var result = await service.SubmitAsync(OrderGuid, Alex);

        Assert.True(result.Success, result.Message);
        Assert.Null(result.Data!.CartonCount);
        Assert.Null(result.Data.PackedPieces);
        Assert.Null(result.Data.OpenCartons);
        Assert.Null(result.Data.Mismatches);
        var detail = await _db.Queryable<WareHouseOrderDetails>().SingleAsync(item => item.DetailGUID == "d-cup");
        Assert.Equal(12, detail.AllocQuantity);
    }

    [Fact]
    public async Task 提交_空箱自动作废_写操作记录_其余一致则通过并带箱数与装箱件数()
    {
        var service = await JoinedServiceAsync();
        var emptyCarton = await NewCartonAsync(service);
        var carton = await NewCartonAsync(service);
        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", carton), Alex);
        await service.AppendRecordAsync(OrderGuid, Scan("d-sponge", carton), Alex);
        Assert.True((await service.SealCartonAsync(OrderGuid, carton, new WarehousePickingSealCartonRequestDto(), Alex)).Success);

        var result = await service.SubmitAsync(OrderGuid, Mia);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, result.Data!.CartonCount);
        Assert.Equal(18, result.Data.PackedPieces);
        var voided = await _db.Queryable<PackCarton>().SingleAsync(item => item.CartonGUID == emptyCarton);
        Assert.Equal(PackCartonStatuses.Voided, voided.Status);
        var voidEvent = await _db.Queryable<PackEvent>()
            .SingleAsync(item => item.TargetGUID == emptyCarton && item.Action == PackEventActions.Voided);
        Assert.Equal("提交拣货时自动作废空箱", voidEvent.Detail);
        Assert.Equal("Mia Wong", voidEvent.OperatorName);
        // 订单行配货数照常写入，订单状态不动（仍是配货中）。
        var cup = await _db.Queryable<WareHouseOrderDetails>().SingleAsync(item => item.DetailGUID == "d-cup");
        Assert.Equal(12, cup.AllocQuantity);
        Assert.Equal(3, (await _db.Queryable<WareHouseOrder>().SingleAsync(item => item.OrderGUID == OrderGuid)).FlowStatus);
        Assert.Equal(WarehouseOrderPickSessionStatuses.Submitted, (await _db.Queryable<WarehouseOrderPickSession>().SingleAsync()).Status);
    }

    [Fact]
    public async Task 提交_只有空箱时自动作废后按没有箱处理_沿用旧行为()
    {
        var service = await JoinedServiceAsync();
        var emptyCarton = await NewCartonAsync(service);
        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", null), Alex);

        var result = await service.SubmitAsync(OrderGuid, Alex);

        Assert.True(result.Success, result.Message);
        Assert.Null(result.Data!.CartonCount);
        Assert.Equal(PackCartonStatuses.Voided, (await _db.Queryable<PackCarton>().SingleAsync(item => item.CartonGUID == emptyCarton)).Status);
    }

    [Fact]
    public async Task 提交_还有没封的箱_409_PACKING_OPEN_CARTONS_带回未封箱列表_整体回滚()
    {
        var service = await JoinedServiceAsync();
        var emptyCarton = await NewCartonAsync(service);
        var open = await NewCartonAsync(service);
        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", open), Alex);

        var result = await service.SubmitAsync(OrderGuid, Alex);

        Assert.False(result.Success);
        Assert.Equal(409, result.StatusCode);
        Assert.Equal(WarehousePickingErrorCodes.PackingOpenCartons, result.ErrorCode);
        var item = Assert.Single(result.Data!.OpenCartons!);
        Assert.Equal(open, item.CartonGuid);
        Assert.Equal(1, item.SkuCount);
        Assert.Equal(12, item.PieceCount);
        // 提交失败整个事务回滚：会话仍在拣货中、配货数没写、空箱的自动作废也一并撤销。
        Assert.Equal(WarehouseOrderPickSessionStatuses.Picking, (await _db.Queryable<WarehouseOrderPickSession>().SingleAsync()).Status);
        Assert.Null((await _db.Queryable<WareHouseOrderDetails>().SingleAsync(detail => detail.DetailGUID == "d-cup")).AllocQuantity);
        Assert.Equal(PackCartonStatuses.Packing, (await _db.Queryable<PackCarton>().SingleAsync(carton => carton.CartonGUID == emptyCarton)).Status);
    }

    [Fact]
    public async Task 提交_拣货数与装箱数对不上_409_PACKING_NOT_RECONCILED_差异商品逐个列出()
    {
        var service = await JoinedServiceAsync();
        var carton = await NewCartonAsync(service);
        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", carton), Alex);
        // 又拣了一个中包但没装箱：拣 24、装 12。
        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", null), Alex);
        Assert.True((await service.SealCartonAsync(OrderGuid, carton, new WarehousePickingSealCartonRequestDto(), Alex)).Success);

        var result = await service.SubmitAsync(OrderGuid, Alex);

        Assert.False(result.Success);
        Assert.Equal(409, result.StatusCode);
        Assert.Equal(WarehousePickingErrorCodes.PackingNotReconciled, result.ErrorCode);
        var mismatch = Assert.Single(result.Data!.Mismatches!);
        Assert.Equal("P-CUP", mismatch.ProductCode);
        Assert.Equal("Stainless Steel Tumbler", mismatch.ProductName);
        Assert.Equal("HB20931", mismatch.ItemNumber);
        Assert.Equal(new[] { "d-cup" }, mismatch.DetailGuids);
        Assert.Equal(24, mismatch.Picked);
        Assert.Equal(12, mismatch.Packed);
        Assert.Equal(12, mismatch.Diff);
        Assert.Equal(WarehouseOrderPickSessionStatuses.Picking, (await _db.Queryable<WarehouseOrderPickSession>().SingleAsync()).Status);
        Assert.Null((await _db.Queryable<WareHouseOrderDetails>().SingleAsync(detail => detail.DetailGUID == "d-cup")).AllocQuantity);
    }

    [Fact]
    public async Task 提交_箱里装多了或装了没拣的商品_差异为负_商品不在订单上时订单行为空()
    {
        var service = await JoinedServiceAsync();
        await _db.Insertable(new Product { ProductCode = "P-EXTRA", ItemNumber = "HB99999", Barcode = "9312345999999", ProductName = "Extra" }).ExecuteCommandAsync();
        var carton = await NewCartonAsync(service);
        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", carton), Alex);
        // 先拣后装：杯子在箱里装到 20（拣货只有 12），另外装了订单上没有的商品。
        await service.SetCartonItemAsync(OrderGuid, carton, "P-CUP", new WarehousePickingSetCartonItemRequestDto { Quantity = 20 }, Alex);
        await service.SetCartonItemAsync(OrderGuid, carton, "P-EXTRA", new WarehousePickingSetCartonItemRequestDto { Quantity = 3 }, Alex);
        Assert.True((await service.SealCartonAsync(OrderGuid, carton, new WarehousePickingSealCartonRequestDto(), Alex)).Success);

        var result = await service.SubmitAsync(OrderGuid, Alex);

        Assert.Equal(WarehousePickingErrorCodes.PackingNotReconciled, result.ErrorCode);
        var cup = result.Data!.Mismatches!.Single(item => item.ProductCode == "P-CUP");
        Assert.Equal(-8, cup.Diff);
        var extra = result.Data.Mismatches!.Single(item => item.ProductCode == "P-EXTRA");
        Assert.Empty(extra.DetailGuids);
        Assert.Equal(0, extra.Picked);
        Assert.Equal(3, extra.Packed);
        Assert.Equal(-3, extra.Diff);
    }

    [Fact]
    public async Task 提交_按装箱数改拣货数后再提交通过()
    {
        var service = await JoinedServiceAsync();
        var carton = await NewCartonAsync(service);
        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", carton), Alex);
        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", null), Alex);
        Assert.True((await service.SealCartonAsync(OrderGuid, carton, new WarehousePickingSealCartonRequestDto(), Alex)).Success);
        Assert.Equal(WarehousePickingErrorCodes.PackingNotReconciled, (await service.SubmitAsync(OrderGuid, Alex)).ErrorCode);

        // 拣货端「按装箱数改拣货数」：对差异行调现有 SetTotal（不带 CartonGuid）。
        var fix = await service.SetLineTotalAsync(OrderGuid, "d-cup", SetTotal(12, 24, null), Alex);
        var submit = await service.SubmitAsync(OrderGuid, Alex);

        Assert.True(fix.Success, fix.Message);
        Assert.True(submit.Success, submit.Message);
        Assert.Equal(1, submit.Data!.CartonCount);
        Assert.Equal(12, submit.Data.PackedPieces);
    }

    // ---- 装箱进度 ----

    [Fact]
    public async Task 装箱进度_按商品汇总订货拣货已装_箱列表带isMine与对账状态()
    {
        var service = await JoinedServiceAsync();
        var cartonA = await NewCartonAsync(service, picker: Alex);
        var cartonB = await NewCartonAsync(service, picker: Mia);
        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", cartonA), Alex);

        var progress = (await service.GetPackProgressAsync(OrderGuid, Alex)).Data!;

        Assert.Equal(OrderGuid, progress.OrderGuid);
        Assert.Equal("2026-0412", progress.OrderNo);
        Assert.Equal("Morayfield", progress.StoreName);
        Assert.Equal(3, progress.FlowStatus);
        Assert.Equal(WarehouseOrderPickSessionStatuses.Picking, progress.SessionStatus);
        var cup = progress.Lines.Single(line => line.ProductCode == "P-CUP");
        Assert.Equal(36, cup.OrderedPieces);
        Assert.Equal(12, cup.PickedPieces);
        Assert.Equal(12, cup.PackedPieces);
        Assert.Equal("Stainless Steel Tumbler", cup.ProductName);
        Assert.Equal("HB20931", cup.ItemNumber);
        var sponge = progress.Lines.Single(line => line.ProductCode == "P-SPONGE");
        Assert.Equal((12m, 0, 0), (sponge.OrderedPieces, sponge.PickedPieces, sponge.PackedPieces));
        Assert.Equal(2, progress.Cartons.Count);
        Assert.True(progress.Cartons.Single(carton => carton.CartonGuid == cartonA).IsMine);
        Assert.False(progress.Cartons.Single(carton => carton.CartonGuid == cartonB).IsMine);
        Assert.Equal("Alex Chen", progress.Cartons.Single(carton => carton.CartonGuid == cartonA).CreatedByName);
        Assert.Equal(2, progress.OpenCartonCount);
        Assert.Equal(1, progress.EmptyCartonCount);
        Assert.Equal(0, progress.SealedCartonCount);
        Assert.False(progress.Reconciled);

        // 别的拣货人看：isMine 反过来；认不出拣货人时全为 false。
        var asMia = (await service.GetPackProgressAsync(OrderGuid, Mia)).Data!;
        Assert.False(asMia.Cartons.Single(carton => carton.CartonGuid == cartonA).IsMine);
        Assert.True(asMia.Cartons.Single(carton => carton.CartonGuid == cartonB).IsMine);
        Assert.All((await service.GetPackProgressAsync(OrderGuid, null)).Data!.Cartons, carton => Assert.False(carton.IsMine));

        // 封掉 A 箱：空箱 B 不拦对账。
        Assert.True((await service.SealCartonAsync(OrderGuid, cartonA, new WarehousePickingSealCartonRequestDto(), Alex)).Success);
        var sealedProgress = (await service.GetPackProgressAsync(OrderGuid, Alex)).Data!;
        Assert.Equal(1, sealedProgress.SealedCartonCount);
        Assert.Equal(1, sealedProgress.OpenCartonCount);
        Assert.True(sealedProgress.Reconciled);
    }

    [Fact]
    public async Task 装箱进度_作废的箱不返回_订单不存在404_会话已提交时拣货数仍按记录合计()
    {
        var service = await JoinedServiceAsync();
        var voidedCarton = await NewCartonAsync(service);
        var voidResult = await PickingPackingTestSupport.PackingService(_db)
            .VoidCartonAsync(PickingPackingTestSupport.AccessService(_db).WarehousePickerOperator("user-alex", "Alex Chen"), voidedCarton);
        Assert.True(voidResult.Success, voidResult.Message);
        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", null), Alex);
        Assert.True((await service.SubmitAsync(OrderGuid, Alex)).Success);

        var progress = (await service.GetPackProgressAsync(OrderGuid, Alex)).Data!;
        var missing = await service.GetPackProgressAsync("no-such-order", Alex);

        Assert.Empty(progress.Cartons);
        Assert.Equal(WarehouseOrderPickSessionStatuses.Submitted, progress.SessionStatus);
        Assert.Equal(12, progress.Lines.Single(line => line.ProductCode == "P-CUP").PickedPieces);
        Assert.Equal(404, missing.StatusCode);
        Assert.Equal(WarehousePickingErrorCodes.OrderNotFound, missing.ErrorCode);
    }

    [Fact]
    public async Task 装箱进度_装箱中的箱带明细_不论创建人_已封的箱不带_箱按创建时间升序再按箱号()
    {
        var service = await JoinedServiceAsync();
        var cartonA = await NewCartonAsync(service, picker: Alex);
        var cartonB = await NewCartonAsync(service, picker: Mia);
        var cartonC = await NewCartonAsync(service, picker: Alex);
        var cartonD = await NewCartonAsync(service, picker: Mia);
        await service.AppendRecordAsync(OrderGuid, Scan("d-sponge", cartonA), Alex);
        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", cartonA), Alex);
        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", cartonB), Mia);
        Assert.True((await service.SealCartonAsync(OrderGuid, cartonB, new WarehousePickingSealCartonRequestDto(), Mia)).Success);
        // 先拣后装：Mia 的装箱中的箱 D 装了 5 件杯子（不是 Alex 建的，Alex 也要看得到明细）。
        Assert.True((await service.SetCartonItemAsync(OrderGuid, cartonD, "P-CUP", new WarehousePickingSetCartonItemRequestDto { Quantity = 5 }, Mia)).Success);
        // 箱号 B 比 A 大，但创建时间最早：排在最前。
        var earliest = DateTime.UtcNow.AddHours(-2);
        await _db.Updateable<PackCarton>().SetColumns(carton => carton.CreatedAtUtc == earliest).Where(carton => carton.CartonGUID == cartonB).ExecuteCommandAsync();
        var rows = await _db.Queryable<PackCarton>().ToListAsync();

        var cartons = (await service.GetPackProgressAsync(OrderGuid, Alex)).Data!.Cartons;

        Assert.Equal(new[] { cartonB, cartonA, cartonC, cartonD }, cartons.Select(carton => carton.CartonGuid));
        Assert.All(cartons, carton => Assert.Equal(rows.Single(row => row.CartonGUID == carton.CartonGuid).CreatedAtUtc, carton.CreatedAtUtc));
        // 已封箱：不带明细。
        Assert.Null(cartons.Single(carton => carton.CartonGuid == cartonB).Items);
        // 装箱中：带明细，按商品编码排序；空箱是空数组（不是缺失），客户端据此区分「装箱中的空箱」。
        var itemsA = cartons.Single(carton => carton.CartonGuid == cartonA).Items!;
        Assert.Equal(new[] { ("P-CUP", 12), ("P-SPONGE", 6) }, itemsA.Select(item => (item.ProductCode, item.Quantity)));
        Assert.Empty(cartons.Single(carton => carton.CartonGuid == cartonC).Items!);
        var itemsD = cartons.Single(carton => carton.CartonGuid == cartonD).Items!;
        Assert.Equal(("P-CUP", 5), (Assert.Single(itemsD).ProductCode, itemsD[0].Quantity));
        // 别的拣货人（或认不出拣货人）看到的明细一样。
        var asMia = (await service.GetPackProgressAsync(OrderGuid, Mia)).Data!.Cartons;
        Assert.Equal(cartons.Select(carton => carton.CartonGuid), asMia.Select(carton => carton.CartonGuid));
        Assert.Equal(2, asMia.Single(carton => carton.CartonGuid == cartonA).Items!.Count);
        Assert.Null((await service.GetPackProgressAsync(OrderGuid, null)).Data!.Cartons.Single(carton => carton.CartonGuid == cartonB).Items);
    }

    [Fact]
    public async Task 装箱进度_订单上没有的商品只要装进箱就出现在lines里_订货数为0()
    {
        var service = await JoinedServiceAsync();
        await _db.Insertable(new Product { ProductCode = "P-EXTRA", ItemNumber = "HB99999", Barcode = "9312345999999", ProductName = "Extra" }).ExecuteCommandAsync();
        var carton = await NewCartonAsync(service);
        Assert.True((await service.SetCartonItemAsync(OrderGuid, carton, "P-EXTRA", new WarehousePickingSetCartonItemRequestDto { Quantity = 3 }, Alex)).Success);

        var lines = (await service.GetPackProgressAsync(OrderGuid, Alex)).Data!.Lines;

        var extra = lines.Single(line => line.ProductCode == "P-EXTRA");
        Assert.Equal(0m, extra.OrderedPieces);
        Assert.Equal(0, extra.PickedPieces);
        Assert.Equal(3, extra.PackedPieces);
        Assert.Equal("Extra", extra.ProductName);
        Assert.Equal("HB99999", extra.ItemNumber);
        // 订单上的商品照常都在。
        Assert.Contains(lines, line => line.ProductCode == "P-CUP" && line.OrderedPieces == 36m);
        Assert.Contains(lines, line => line.ProductCode == "P-SPONGE" && line.OrderedPieces == 12m);
    }

    // ---- 新建箱 ----

    [Fact]
    public async Task 新建箱_默认去向是订货单的分店_总仓持有_关联订货单_创建人是拣货人()
    {
        var service = await JoinedServiceAsync();

        var result = await service.CreateCartonAsync(OrderGuid, new WarehousePickingCreateCartonRequestDto(), Alex);

        Assert.True(result.Success, result.Message);
        var carton = result.Data!;
        Assert.Equal(PackOwners.Warehouse, carton.OwnerCode);
        Assert.Equal(OrderGuid, carton.OrderGuid);
        Assert.Equal("2026-0412", carton.OrderNo);
        Assert.Equal(PackDestinationTypes.Store, carton.Destination.DestinationType);
        Assert.Equal(StoreCode, carton.Destination.Code);
        Assert.Equal("Alex Chen", carton.CreatedByName);
        Assert.Equal(PackCartonStatuses.Packing, carton.Status);
        var created = await _db.Queryable<PackEvent>().SingleAsync(item => item.TargetGUID == carton.CartonGuid);
        Assert.Equal("Alex Chen", created.OperatorName);
        Assert.Equal("user-alex", created.OperatorUserGuid);
    }

    [Fact]
    public async Task 新建箱_可以指定其他去向_草稿订单与不存在的订单被拒绝()
    {
        var service = await JoinedServiceAsync();
        await _db.Insertable(new Store { StoreGUID = "store-guid-2", StoreCode = "1020", StoreName = "Inala" }).ExecuteCommandAsync();

        var custom = await service.CreateCartonAsync(
            OrderGuid,
            new WarehousePickingCreateCartonRequestDto { DestinationType = PackDestinationTypes.Store, DestinationCode = "1020" },
            Alex
        );
        var draft = await service.CreateCartonAsync(DraftOrderGuid, new WarehousePickingCreateCartonRequestDto(), Alex);
        var missing = await service.CreateCartonAsync("no-such-order", new WarehousePickingCreateCartonRequestDto(), Alex);

        Assert.Equal("1020", custom.Data!.Destination.Code);
        Assert.Equal(409, draft.StatusCode);
        Assert.Equal(WarehousePickingErrorCodes.OrderNotPickable, draft.ErrorCode);
        Assert.Equal(404, missing.StatusCode);
        Assert.Equal(WarehousePickingErrorCodes.OrderNotFound, missing.ErrorCode);
        Assert.Equal(1, await _db.Queryable<PackCarton>().CountAsync());
    }

    // ---- 封箱（可上托盘） ----

    [Fact]
    public async Task 封箱_返回箱详情供打印标签_重复封箱幂等_别单的箱找不到()
    {
        var service = await JoinedServiceAsync();
        var carton = await NewCartonAsync(service);
        var otherCarton = await NewCartonAsync(service, OtherOrderGuid);
        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", carton), Alex);

        var sealedResult = await service.SealCartonAsync(OrderGuid, carton, new WarehousePickingSealCartonRequestDto { WeightKg = 6.5m }, Alex);
        var again = await service.SealCartonAsync(OrderGuid, carton, new WarehousePickingSealCartonRequestDto(), Alex);
        var wrongOrder = await service.SealCartonAsync(OrderGuid, otherCarton, new WarehousePickingSealCartonRequestDto(), Alex);

        Assert.True(sealedResult.Success, sealedResult.Message);
        Assert.Equal(PackCartonStatuses.Sealed, sealedResult.Data!.Status);
        Assert.Equal(6.5m, sealedResult.Data.WeightKg);
        Assert.Equal("Alex Chen", sealedResult.Data.SealedByName);
        Assert.Equal(12, sealedResult.Data.PieceCount);
        Assert.True(again.Success, again.Message);
        Assert.Equal(PackingErrorCodes.CartonNotFound, wrongOrder.ErrorCode);
        Assert.Equal(PackCartonStatuses.Packing, (await _db.Queryable<PackCarton>().SingleAsync(item => item.CartonGUID == otherCarton)).Status);
    }

    [Fact]
    public async Task 封箱_带托盘时同一事务里上托盘_托盘不可用时整体回滚_箱仍在装箱中()
    {
        var service = await JoinedServiceAsync();
        var carton = await NewCartonAsync(service);
        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", carton), Alex);
        var packing = PickingPackingTestSupport.PackingService(_db);
        var op = PickingPackingTestSupport.AccessService(_db).WarehousePickerOperator("user-alex", "Alex Chen");
        var pallet = (await packing.CreatePalletAsync(op, new PackingCreatePalletRequestDto { OwnerCode = PackOwners.Warehouse, DestinationType = PackDestinationTypes.Store, DestinationCode = StoreCode })).Data!;

        var noPallet = await service.SealCartonAsync(OrderGuid, carton, new WarehousePickingSealCartonRequestDto { PalletGuid = "no-such-pallet" }, Alex);
        var rolledBack = (await _db.Queryable<PackCarton>().SingleAsync(item => item.CartonGUID == carton)).Status;
        var onPallet = await service.SealCartonAsync(OrderGuid, carton, new WarehousePickingSealCartonRequestDto { PalletGuid = pallet.PalletGuid }, Alex);

        // 托盘不存在：错误原样返回，封箱也撤销。
        Assert.False(noPallet.Success);
        Assert.Equal(404, noPallet.StatusCode);
        Assert.Equal(PackingErrorCodes.PalletNotFound, noPallet.ErrorCode);
        Assert.Equal(PackCartonStatuses.Packing, rolledBack);
        Assert.True(onPallet.Success, onPallet.Message);
        Assert.Equal(PackCartonStatuses.Sealed, onPallet.Data!.Status);
        Assert.Equal(pallet.PalletGuid, onPallet.Data.PalletGuid);
        Assert.Equal(pallet.PalletNo, onPallet.Data.PalletNo);
        Assert.Equal(1, (await packing.GetPalletAsync(op, pallet.PalletGuid)).Data!.CartonCount);
    }

    [Fact]
    public async Task 封箱_托盘已封托_原样返回托盘错误_整体回滚()
    {
        var service = await JoinedServiceAsync();
        var carton = await NewCartonAsync(service);
        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", carton), Alex);
        var packing = PickingPackingTestSupport.PackingService(_db);
        var op = PickingPackingTestSupport.AccessService(_db).WarehousePickerOperator("user-alex", "Alex Chen");
        var pallet = (await packing.CreatePalletAsync(op, new PackingCreatePalletRequestDto { OwnerCode = PackOwners.Warehouse, DestinationType = PackDestinationTypes.Store, DestinationCode = StoreCode })).Data!;
        var otherCarton = await NewCartonAsync(service);
        await service.AppendRecordAsync(OrderGuid, Scan("d-sponge", otherCarton), Alex);
        Assert.True((await service.SealCartonAsync(OrderGuid, otherCarton, new WarehousePickingSealCartonRequestDto { PalletGuid = pallet.PalletGuid }, Alex)).Success);
        Assert.True((await packing.SealPalletAsync(op, pallet.PalletGuid, new PackingSealRequestDto())).Success);

        var result = await service.SealCartonAsync(OrderGuid, carton, new WarehousePickingSealCartonRequestDto { PalletGuid = pallet.PalletGuid }, Alex);

        Assert.False(result.Success);
        Assert.Equal(409, result.StatusCode);
        Assert.Equal(PackingErrorCodes.PalletNotBuilding, result.ErrorCode);
        Assert.Equal(PackCartonStatuses.Packing, (await _db.Queryable<PackCarton>().SingleAsync(item => item.CartonGUID == carton)).Status);
    }

    // ---- 先拣后装 ----

    [Fact]
    public async Task 先拣后装_绝对数幂等_同一请求连发两次结果相同_超装只提示不拦截()
    {
        var service = await JoinedServiceAsync();
        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", null), Alex);
        var carton = await NewCartonAsync(service);
        var set12 = new WarehousePickingSetCartonItemRequestDto { Quantity = 12 };

        var first = await service.SetCartonItemAsync(OrderGuid, carton, "P-CUP", set12, Alex);
        var second = await service.SetCartonItemAsync(OrderGuid, carton, "P-CUP", set12, Alex);

        Assert.True(first.Success, first.Message);
        Assert.True(second.Success, second.Message);
        Assert.Equal(12, first.Data!.Carton.Items.Single().Quantity);
        Assert.Equal(first.Data.Carton.PieceCount, second.Data!.Carton.PieceCount);
        Assert.Equal(first.Data.Carton.Items.Single().Quantity, second.Data.Carton.Items.Single().Quantity);
        Assert.False(first.Data.OverPacked);
        Assert.Equal(0, first.Data.OverPackedBy);
        Assert.Equal(12, await ItemQuantityAsync(carton, "P-CUP"));

        // 装 20、拣货 12：超装 8，只提示，写入照常成功。
        var over = await service.SetCartonItemAsync(OrderGuid, carton, "P-CUP", new WarehousePickingSetCartonItemRequestDto { Quantity = 20 }, Alex);
        Assert.True(over.Success, over.Message);
        Assert.True(over.Data!.OverPacked);
        Assert.Equal(8, over.Data.OverPackedBy);
        Assert.Equal(20, await ItemQuantityAsync(carton, "P-CUP"));

        // 0 = 移除；重复移除仍然成功。
        var removed = await service.SetCartonItemAsync(OrderGuid, carton, "P-CUP", new WarehousePickingSetCartonItemRequestDto { Quantity = 0 }, Alex);
        var removedAgain = await service.SetCartonItemAsync(OrderGuid, carton, "P-CUP", new WarehousePickingSetCartonItemRequestDto { Quantity = 0 }, Alex);
        Assert.True(removed.Success, removed.Message);
        Assert.True(removedAgain.Success, removedAgain.Message);
        Assert.Empty(removed.Data!.Carton.Items);
        Assert.False(removed.Data.OverPacked);
        Assert.Equal(0, await ItemQuantityAsync(carton, "P-CUP"));
    }

    [Fact]
    public async Task 先拣后装_会话已提交的订单同样可用_不影响已写入的配货数()
    {
        var service = await JoinedServiceAsync();
        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", null), Alex);
        Assert.True((await service.SubmitAsync(OrderGuid, Alex)).Success);
        var carton = await NewCartonAsync(service);

        var result = await service.SetCartonItemAsync(OrderGuid, carton, "P-CUP", new WarehousePickingSetCartonItemRequestDto { Quantity = 12 }, Alex);

        Assert.True(result.Success, result.Message);
        Assert.Equal(12, await ItemQuantityAsync(carton, "P-CUP"));
        Assert.Equal(12, (await _db.Queryable<WareHouseOrderDetails>().SingleAsync(item => item.DetailGUID == "d-cup")).AllocQuantity);
        // 拣货记录会话已锁定，边拣边装的写入仍被拒绝。
        var append = await service.AppendRecordAsync(OrderGuid, Scan("d-cup", carton), Alex);
        Assert.Equal(WarehousePickingErrorCodes.SessionSubmitted, append.ErrorCode);
        Assert.Equal(12, await ItemQuantityAsync(carton, "P-CUP"));
    }

    [Fact]
    public async Task 先拣后装_商品不存在_别单的箱_已封箱_数量越界都被拒绝且不写箱()
    {
        var service = await JoinedServiceAsync();
        var carton = await NewCartonAsync(service);
        var otherCarton = await NewCartonAsync(service, OtherOrderGuid);
        var set = new WarehousePickingSetCartonItemRequestDto { Quantity = 5 };

        var noProduct = await service.SetCartonItemAsync(OrderGuid, carton, "P-NOPE", set, Alex);
        var wrongOrder = await service.SetCartonItemAsync(OrderGuid, otherCarton, "P-CUP", set, Alex);
        var negative = await service.SetCartonItemAsync(OrderGuid, carton, "P-CUP", new WarehousePickingSetCartonItemRequestDto { Quantity = -1 }, Alex);
        Assert.True((await service.SetCartonItemAsync(OrderGuid, carton, "P-CUP", set, Alex)).Success);
        Assert.True((await service.SealCartonAsync(OrderGuid, carton, new WarehousePickingSealCartonRequestDto(), Alex)).Success);
        var sealedCarton = await service.SetCartonItemAsync(OrderGuid, carton, "P-CUP", new WarehousePickingSetCartonItemRequestDto { Quantity = 6 }, Alex);

        Assert.Equal(PackingErrorCodes.CodeNotFound, noProduct.ErrorCode);
        Assert.Equal(PackingErrorCodes.CartonNotFound, wrongOrder.ErrorCode);
        Assert.Equal(PackingErrorCodes.QuantityOutOfRange, negative.ErrorCode);
        Assert.Equal(PackingErrorCodes.CartonNotPacking, sealedCarton.ErrorCode);
        Assert.Equal(0, await ItemQuantityAsync(otherCarton, "P-CUP"));
        Assert.Equal(5, await ItemQuantityAsync(carton, "P-CUP"));
    }

    // ---- 打包操作人 ----

    [Fact]
    public void 仓库拣货人操作人_只能操作总仓_不是经理也不是管理员()
    {
        var op = PickingPackingTestSupport.AccessService(_db).WarehousePickerOperator("user-alex", "  Alex Chen ");

        Assert.Equal("user-alex", op.UserGuid);
        Assert.Equal("Alex Chen", op.Name);
        Assert.False(op.IsManager);
        Assert.False(op.IsAdmin);
        var owner = Assert.Single(op.Owners);
        Assert.Equal(PackOwners.Warehouse, owner.Code);
        Assert.Equal("HB TEST WAREHOUSE", owner.Name);
        Assert.True(owner.IsWarehouse);
        Assert.True(op.Has(PackOwners.Warehouse));
        Assert.False(op.Has(StoreCode));
    }

    // ---- 夹具 ----

    private async Task<WarehousePickingService> JoinedServiceAsync()
    {
        var service = PickingPackingTestSupport.PickingService(_db, Mock.Of<IProductWarehouseReactService>());
        var joined = await service.JoinAsync(OrderGuid, Alex);
        Assert.True(joined.Success, joined.Message);
        return service;
    }

    private async Task<string> NewCartonAsync(WarehousePickingService service, string orderGuid = OrderGuid, WarehousePickerContext? picker = null)
    {
        var result = await service.CreateCartonAsync(orderGuid, new WarehousePickingCreateCartonRequestDto(), picker ?? Alex);
        Assert.True(result.Success, result.Message);
        return result.Data!.CartonGuid;
    }

    private async Task<int> ItemQuantityAsync(string cartonGuid, string productCode)
    {
        var item = await _db.Queryable<PackCartonItem>()
            .Where(row => row.CartonGUID == cartonGuid && row.ProductCode == productCode)
            .FirstAsync();
        return item?.Quantity ?? 0;
    }

    private static WarehousePickingRecordRequestDto Scan(string detailGuid, string? cartonGuid) => new()
    {
        DetailGuid = detailGuid,
        Source = WarehouseOrderPickSources.Scan,
        ClientRequestId = Guid.NewGuid(),
        CartonGuid = cartonGuid,
    };

    private static WarehousePickingRecordRequestDto Button(string detailGuid, int source, string? cartonGuid) => new()
    {
        DetailGuid = detailGuid,
        Source = source,
        ClientRequestId = Guid.NewGuid(),
        CartonGuid = cartonGuid,
    };

    private static WarehousePickingSetTotalRequestDto SetTotal(int total, int expected, string? cartonGuid) => new()
    {
        Total = total,
        ExpectedTotal = expected,
        ClientRequestId = Guid.NewGuid(),
        CartonGuid = cartonGuid,
    };

    private void Seed()
    {
        _db.Insertable(new Store { StoreGUID = "store-guid", StoreCode = StoreCode, StoreName = "Morayfield" }).ExecuteCommand();
        _db.Insertable(new List<WareHouseOrder>
        {
            new() { OrderGUID = OrderGuid, OrderNo = "2026-0412", StoreCode = StoreCode, FlowStatus = 1, OrderDate = new DateTime(2026, 11, 12, 9, 0, 0) },
            new() { OrderGUID = OtherOrderGuid, OrderNo = "2026-0413", StoreCode = StoreCode, FlowStatus = 3, OrderDate = new DateTime(2026, 11, 12, 10, 0, 0) },
            new() { OrderGUID = DraftOrderGuid, OrderNo = "2026-0414", StoreCode = StoreCode, FlowStatus = 0, OrderDate = new DateTime(2026, 11, 12, 11, 0, 0) },
        }).ExecuteCommand();
        _db.Insertable(new List<WareHouseOrderDetails>
        {
            Detail("d-cup", OrderGuid, "P-CUP", 36),
            Detail("d-sponge", OrderGuid, "P-SPONGE", 12),
            Detail("d2-cup", OtherOrderGuid, "P-CUP", 12),
        }).ExecuteCommand();
        _db.Insertable(new List<Product>
        {
            new() { ProductCode = "P-CUP", ItemNumber = "HB20931", Barcode = "9312345678905", ProductName = "Stainless Steel Tumbler", ProductType = 0 },
            new() { ProductCode = "P-SPONGE", ItemNumber = "HB18806", Barcode = "9312345612343", ProductName = "Dish Sponge 6 Pack", ProductType = 0 },
        }).ExecuteCommand();
        _db.Insertable(new List<WarehouseProduct>
        {
            new() { ProductCode = "P-CUP", MinOrderQuantity = 12, IsActive = true },
            new() { ProductCode = "P-SPONGE", MinOrderQuantity = 6, IsActive = true },
        }).ExecuteCommand();
    }

    private static WareHouseOrderDetails Detail(string detailGuid, string orderGuid, string productCode, decimal quantity) => new()
    {
        DetailGUID = detailGuid,
        OrderGUID = orderGuid,
        StoreCode = StoreCode,
        ProductCode = productCode,
        Quantity = quantity,
        OEMPrice = 1m,
        ImportPrice = 1m,
    };

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }
}

/// <summary>分店按订单签收的两个接口：账号范围由 IPackingAccessService 算出，控制器只做映射。</summary>
public sealed class PackingOrderReceiveControllerTests
{
    private readonly Mock<IPackingService> _packing = new();
    private readonly Mock<IPackingAccessService> _access = new();

    private static readonly PackingOperator Lily = new("u-lily", "Lily", false, false, new[] { new PackingOwner("1013", "Sunnybank", false) });

    [Fact]
    public async Task 账号没有打包权限时返回访问服务的拒绝_不调用服务()
    {
        _access
            .Setup(service => service.ResolveAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(PackingAccessResult.Denied(403, PackingErrorCodes.NotAllowed, "当前账号没有可以打包的分店或仓库"));

        var get = await CreateController().GetOrderReceive("order-1");
        var post = await CreateController().ReceiveOrder("order-1", new PackingReceiveOrderRequestDto { ExpectedCartonCount = 2 });

        Assert.Equal(403, StatusOf(get));
        Assert.Equal(403, StatusOf(post));
        Assert.Equal(PackingErrorCodes.NotAllowed, ErrorCodeOf(post));
        _packing.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task 查询与签收按当前操作人调用服务_订单号去首尾空格_实到箱数原样传入()
    {
        AllowLily();
        _packing
            .Setup(service => service.GetOrderReceiveAsync(Lily, "order-1"))
            .ReturnsAsync(PackingResult<PackingOrderReceiveDto>.Ok(new PackingOrderReceiveDto { OrderGuid = "order-1" }));
        _packing
            .Setup(service => service.ReceiveOrderAsync(Lily, "order-1", 3))
            .ReturnsAsync(PackingResult<PackingOrderReceiveDto>.Ok(new PackingOrderReceiveDto { OrderGuid = "order-1", ReceivedCount = 3 }));
        _packing
            .Setup(service => service.ReceiveOrderAsync(Lily, "order-1", null))
            .ReturnsAsync(PackingResult<PackingOrderReceiveDto>.Ok(new PackingOrderReceiveDto { OrderGuid = "order-1" }));

        var get = await CreateController().GetOrderReceive(" order-1 ");
        var post = await CreateController().ReceiveOrder("order-1", new PackingReceiveOrderRequestDto { ExpectedCartonCount = 3 });
        var noBody = await CreateController().ReceiveOrder("order-1", null);

        Assert.Equal(200, StatusOf(get));
        Assert.Equal(200, StatusOf(post));
        Assert.Equal(200, StatusOf(noBody));
        _packing.Verify(service => service.ReceiveOrderAsync(Lily, "order-1", 3), Times.Once);
        _packing.Verify(service => service.ReceiveOrderAsync(Lily, "order-1", null), Times.Once);
    }

    [Fact]
    public async Task 数量对不上时原样映射409与错误码_并带回箱数据()
    {
        AllowLily();
        _packing
            .Setup(service => service.ReceiveOrderAsync(Lily, "order-1", 1))
            .ReturnsAsync(PackingResult<PackingOrderReceiveDto>.Fail(
                409,
                PackingErrorCodes.CountMismatch,
                "实到 1 箱，系统记录是 2 箱",
                new PackingOrderReceiveDto { OrderGuid = "order-1", ActualInTransitCartonCount = 2 }
            ));

        var result = await CreateController().ReceiveOrder("order-1", new PackingReceiveOrderRequestDto { ExpectedCartonCount = 1 });

        Assert.Equal(409, StatusOf(result));
        Assert.Equal(PackingErrorCodes.CountMismatch, ErrorCodeOf(result));
        var data = ((ObjectResult)result).Value!.GetType().GetProperty("data")!.GetValue(((ObjectResult)result).Value) as PackingOrderReceiveDto;
        Assert.Equal(2, data!.ActualInTransitCartonCount);
    }

    private void AllowLily() =>
        _access
            .Setup(service => service.ResolveAsync(It.IsAny<ClaimsPrincipal>()))
            .ReturnsAsync(PackingAccessResult.Allowed(Lily));

    private PackingController CreateController() =>
        new(_packing.Object, _access.Object, NullLogger<PackingController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private static int StatusOf(IActionResult result) => result switch
    {
        ObjectResult objectResult => objectResult.StatusCode ?? 200,
        StatusCodeResult statusCodeResult => statusCodeResult.StatusCode,
        _ => throw new InvalidOperationException(result.GetType().Name),
    };

    private static string? ErrorCodeOf(IActionResult result)
    {
        var value = (result as ObjectResult)?.Value;
        return value?.GetType().GetProperty("errorCode")?.GetValue(value) as string;
    }
}
