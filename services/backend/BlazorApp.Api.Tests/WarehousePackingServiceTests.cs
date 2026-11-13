using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.WarehousePacking;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 仓库扫码打包：商品 → 箱 →（可选）托盘；位置挂在箱 / 托盘上，商品位置由箱明细推导。
/// </summary>
public sealed class WarehousePackingServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 11, 13, 4, 30, 0, TimeSpan.Zero));

    private static readonly WarehousePackingOperator Lily = new("user-lily", "Lily", false);
    private static readonly WarehousePackingOperator Tom = new("user-tom", "Tom", false);
    private static readonly WarehousePackingOperator Manager = new("user-mgr", "Morgan", true);

    public WarehousePackingServiceTests()
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
            typeof(Location),
            typeof(Store),
            typeof(WarehousePackCarton),
            typeof(WarehousePackCartonItem),
            typeof(WarehousePackPallet),
            typeof(WarehousePackCarrier),
            typeof(WarehousePackEvent),
            typeof(WarehousePackSequence)
        );
        SeedMasterData();
    }

    [Fact]
    public async Task 建箱_箱号按仓库当地日期加当日流水_发出地取配置()
    {
        var first = await CreateService().CreateCartonAsync(new WarehousePackingCreateCartonRequestDto(), Lily);
        var second = await CreateService().CreateCartonAsync(new WarehousePackingCreateCartonRequestDto(), Tom);

        Assert.True(first.Success, first.Message);
        Assert.Equal("BX2611130001", first.Data!.CartonNo);
        Assert.Equal("BX2611130002", second.Data!.CartonNo);
        Assert.Equal(WarehousePackCartonStatuses.Packing, first.Data.Status);
        Assert.Equal("HB TEST WAREHOUSE", first.Data.OriginName);
        Assert.Equal("Lily", first.Data.CreatedByName);
        Assert.Equal(WarehousePackDestinationTypes.Unassigned, first.Data.Destination.DestinationType);
    }

    [Fact]
    public async Task 建箱_UTC还是前一天而仓库当地已过零点时_箱号用当地日期()
    {
        // 布里斯班 UTC+10：UTC 11-12 15:00 是当地 11-13 01:00，早班建的箱应编到 11-13。
        _time.Now = new DateTimeOffset(2026, 11, 12, 15, 0, 0, TimeSpan.Zero);

        var carton = await CreateService().CreateCartonAsync(new WarehousePackingCreateCartonRequestDto(), Lily);

        Assert.Equal("BX2611130001", carton.Data!.CartonNo);
    }

    [Fact]
    public async Task 建箱_关联订货单且未指定去向时_去向默认为订货单分店()
    {
        var carton = await CreateService().CreateCartonAsync(
            new WarehousePackingCreateCartonRequestDto { OrderGuid = "order-1" },
            Lily
        );

        Assert.True(carton.Success, carton.Message);
        Assert.Equal(WarehousePackDestinationTypes.Store, carton.Data!.Destination.DestinationType);
        Assert.Equal("1013", carton.Data.Destination.StoreCode);
        Assert.Equal("Sunnybank", carton.Data.Destination.StoreName);
        Assert.Equal("2026-0412", carton.Data.OrderNo);
    }

    [Fact]
    public async Task 建箱_去向为运输公司但没选公司_或分店不存在_都拒绝()
    {
        var noCarrier = await CreateService().CreateCartonAsync(
            new WarehousePackingCreateCartonRequestDto { DestinationType = WarehousePackDestinationTypes.Carrier },
            Lily
        );
        var unknownStore = await CreateService().CreateCartonAsync(
            new WarehousePackingCreateCartonRequestDto { DestinationType = WarehousePackDestinationTypes.Store, DestinationStoreCode = "9999" },
            Lily
        );

        Assert.Equal(WarehousePackingErrorCodes.DestinationInvalid, noCarrier.ErrorCode);
        Assert.Equal(WarehousePackingErrorCodes.DestinationInvalid, unknownStore.ErrorCode);
        Assert.Equal(0, await _db.Queryable<WarehousePackCarton>().CountAsync());
    }

    [Fact]
    public async Task 扫码_同一商品累加_条码货号都认_刚扫的排在最前()
    {
        var service = CreateService();
        var carton = (await service.CreateCartonAsync(new WarehousePackingCreateCartonRequestDto(), Lily)).Data!;

        await service.ScanIntoCartonAsync(carton.CartonGuid, new WarehousePackingScanRequestDto { Code = "9345678123457" }, Lily);
        _time.Advance(TimeSpan.FromSeconds(5));
        await service.ScanIntoCartonAsync(carton.CartonGuid, new WarehousePackingScanRequestDto { Code = "HB24102", Quantity = 12 }, Lily);
        _time.Advance(TimeSpan.FromSeconds(5));
        var result = await service.ScanIntoCartonAsync(carton.CartonGuid, new WarehousePackingScanRequestDto { Code = "HB23817" }, Lily);

        Assert.True(result.Success, result.Message);
        Assert.Equal("P-MUG", result.Data!.ChangedProductCode);
        Assert.Equal(2, result.Data.SkuCount);
        Assert.Equal(14, result.Data.PieceCount);
        Assert.Equal(new[] { "P-MUG", "P-CANDLE" }, result.Data.Items.Select(item => item.ProductCode));
        var mug = result.Data.Items[0];
        Assert.Equal(2, mug.Quantity);
        Assert.Equal("Ceramic Mug 350ml", mug.ProductName);
        Assert.Equal(12, mug.MiddlePackageQuantity);
        Assert.Equal(48, mug.PackingQuantity);
    }

    [Fact]
    public async Task 扫码_套装码只认出母商品_每扫一次加一件不按套装数量加倍()
    {
        var service = CreateService();
        var carton = (await service.CreateCartonAsync(new WarehousePackingCreateCartonRequestDto(), Lily)).Data!;

        var result = await service.ScanIntoCartonAsync(carton.CartonGuid, new WarehousePackingScanRequestDto { Code = "SET-MUG-6" }, Lily);

        Assert.True(result.Success, result.Message);
        Assert.Equal("P-MUG", Assert.Single(result.Data!.Items).ProductCode);
        Assert.Equal(1, result.Data.PieceCount);
    }

    [Fact]
    public async Task 扫码_iOS去掉前导0的条码仍能认出商品()
    {
        var service = CreateService();
        var carton = (await service.CreateCartonAsync(new WarehousePackingCreateCartonRequestDto(), Lily)).Data!;

        var result = await service.ScanIntoCartonAsync(carton.CartonGuid, new WarehousePackingScanRequestDto { Code = "936012345678" }, Lily);

        Assert.True(result.Success, result.Message);
        Assert.Equal("P-PLANT", Assert.Single(result.Data!.Items).ProductCode);
    }

    [Fact]
    public async Task 扫码_找不到的码返回404_一码多品返回候选_选定后带商品编码提交成功()
    {
        var service = CreateService();
        var carton = (await service.CreateCartonAsync(new WarehousePackingCreateCartonRequestDto(), Lily)).Data!;

        var unknown = await service.ScanIntoCartonAsync(carton.CartonGuid, new WarehousePackingScanRequestDto { Code = "NOPE" }, Lily);
        var ambiguous = await service.ScanIntoCartonAsync(carton.CartonGuid, new WarehousePackingScanRequestDto { Code = "DUP-CODE" }, Lily);
        var chosen = await service.ScanIntoCartonAsync(
            carton.CartonGuid,
            new WarehousePackingScanRequestDto { Code = "DUP-CODE", ProductCode = "P-DUP-B", Quantity = 3 },
            Lily
        );

        Assert.Equal(404, unknown.StatusCode);
        Assert.Equal(WarehousePackingErrorCodes.CodeNotFound, unknown.ErrorCode);
        Assert.Equal(409, ambiguous.StatusCode);
        Assert.Equal(WarehousePackingErrorCodes.CodeAmbiguous, ambiguous.ErrorCode);
        Assert.Equal(new[] { "P-DUP-A", "P-DUP-B" }, ambiguous.Data!.Candidates!.Select(item => item.ProductCode).OrderBy(code => code));
        Assert.True(chosen.Success, chosen.Message);
        Assert.Equal(3, Assert.Single(chosen.Data!.Items).Quantity);
    }

    [Fact]
    public async Task 改数量_设为0移除商品_超出范围拒绝()
    {
        var service = CreateService();
        var carton = await CartonWithAsync(service, ("HB23817", 5), ("HB24102", 2));

        var tooMany = await service.SetCartonItemQuantityAsync(carton.CartonGuid, "P-MUG", 100001, Lily);
        var changed = await service.SetCartonItemQuantityAsync(carton.CartonGuid, "P-MUG", 24, Lily);
        var removed = await service.SetCartonItemQuantityAsync(carton.CartonGuid, "P-CANDLE", 0, Lily);

        Assert.Equal(WarehousePackingErrorCodes.QuantityOutOfRange, tooMany.ErrorCode);
        Assert.Equal(24, changed.Data!.Items.Single(item => item.ProductCode == "P-MUG").Quantity);
        Assert.Equal("P-MUG", Assert.Single(removed.Data!.Items).ProductCode);
        Assert.Equal(24, removed.Data.PieceCount);
    }

    [Fact]
    public async Task 封箱_空箱不能封_封箱后不能再扫码或改数量_重复封箱幂等()
    {
        var service = CreateService();
        var empty = (await service.CreateCartonAsync(new WarehousePackingCreateCartonRequestDto(), Lily)).Data!;
        var carton = await CartonWithAsync(service, ("HB23817", 24));

        var sealEmpty = await service.SealCartonAsync(empty.CartonGuid, new WarehousePackingSealRequestDto(), Lily);
        var sealedResult = await service.SealCartonAsync(carton.CartonGuid, new WarehousePackingSealRequestDto { WeightKg = 4.2m }, Tom);
        var sealAgain = await service.SealCartonAsync(carton.CartonGuid, new WarehousePackingSealRequestDto(), Lily);
        var scan = await service.ScanIntoCartonAsync(carton.CartonGuid, new WarehousePackingScanRequestDto { Code = "HB23817" }, Lily);
        var setQuantity = await service.SetCartonItemQuantityAsync(carton.CartonGuid, "P-MUG", 1, Lily);

        Assert.Equal(WarehousePackingErrorCodes.CartonEmpty, sealEmpty.ErrorCode);
        Assert.True(sealedResult.Success, sealedResult.Message);
        Assert.Equal(WarehousePackCartonStatuses.Sealed, sealedResult.Data!.Status);
        Assert.Equal("Tom", sealedResult.Data.SealedByName);
        Assert.Equal(4.2m, sealedResult.Data.WeightKg);
        Assert.True(sealAgain.Success);
        // 第二次封箱不改封箱人：标签上的打包人仍是真正封箱的那位。
        Assert.Equal("Tom", sealAgain.Data!.SealedByName);
        Assert.Equal(WarehousePackingErrorCodes.CartonNotPacking, scan.ErrorCode);
        Assert.Equal(WarehousePackingErrorCodes.CartonNotPacking, setQuantity.ErrorCode);
        Assert.Equal(24, (await service.GetCartonAsync(carton.CartonGuid)).Data!.PieceCount);
    }

    [Fact]
    public async Task 重新开箱_箱号不变可继续装_并留下操作记录()
    {
        var service = CreateService();
        var carton = (await service.CreateCartonAsync(new WarehousePackingCreateCartonRequestDto(), Lily)).Data!;
        _time.Advance(TimeSpan.FromMinutes(1));
        await service.ScanIntoCartonAsync(carton.CartonGuid, new WarehousePackingScanRequestDto { Code = "HB23817", Quantity = 24 }, Lily);
        await service.SealCartonAsync(carton.CartonGuid, new WarehousePackingSealRequestDto(), Lily);
        _time.Advance(TimeSpan.FromMinutes(1));

        var reopened = await service.ReopenCartonAsync(carton.CartonGuid, Manager);
        var scan = await service.ScanIntoCartonAsync(carton.CartonGuid, new WarehousePackingScanRequestDto { Code = "HB23817" }, Lily);
        var events = await service.ListEventsAsync(WarehousePackTargetTypes.Carton, carton.CartonGuid);

        Assert.True(reopened.Success, reopened.Message);
        Assert.Equal(carton.CartonNo, reopened.Data!.CartonNo);
        Assert.Equal(WarehousePackCartonStatuses.Packing, reopened.Data.Status);
        Assert.Null(reopened.Data.SealedByName);
        Assert.Equal(25, scan.Data!.PieceCount);
        // 操作记录最新的在前。
        Assert.Equal(
            new[] { WarehousePackEventActions.Reopened, WarehousePackEventActions.Sealed, WarehousePackEventActions.Created },
            events.Data!.Select(item => item.Action)
        );
        Assert.Equal("Morgan", events.Data![0].OperatorName);
    }

    [Fact]
    public async Task 关联订货单_箱明细带出配货数量与其他箱已装数量_订货单外的商品没有配货数()
    {
        var service = CreateService();
        var other = (await service.CreateCartonAsync(new WarehousePackingCreateCartonRequestDto { OrderGuid = "order-1" }, Tom)).Data!;
        await service.ScanIntoCartonAsync(other.CartonGuid, new WarehousePackingScanRequestDto { Code = "HB23817", Quantity = 12 }, Tom);
        var voided = (await service.CreateCartonAsync(new WarehousePackingCreateCartonRequestDto { OrderGuid = "order-1" }, Tom)).Data!;
        await service.ScanIntoCartonAsync(voided.CartonGuid, new WarehousePackingScanRequestDto { Code = "HB23817", Quantity = 99 }, Tom);
        await service.VoidCartonAsync(voided.CartonGuid, Tom);
        var carton = (await service.CreateCartonAsync(new WarehousePackingCreateCartonRequestDto { OrderGuid = "order-1" }, Lily)).Data!;
        await service.ScanIntoCartonAsync(carton.CartonGuid, new WarehousePackingScanRequestDto { Code = "HB23817", Quantity = 24 }, Lily);
        await service.ScanIntoCartonAsync(carton.CartonGuid, new WarehousePackingScanRequestDto { Code = "HB24102", Quantity = 6 }, Lily);
        var detail = (await service.ScanIntoCartonAsync(carton.CartonGuid, new WarehousePackingScanRequestDto { Code = "HB21960", Quantity = 8 }, Lily)).Data!;

        var mug = detail.Items.Single(item => item.ProductCode == "P-MUG");
        // 配货数已填（36）时用配货数，不用订货数（48）；作废的箱不计入其他箱已装。
        Assert.Equal(36m, mug.OrderQuantity);
        Assert.Equal(12, mug.PackedInOtherCartons);
        // 还没配货（配货数为空）时退回订货数。
        Assert.Equal(24m, detail.Items.Single(item => item.ProductCode == "P-CANDLE").OrderQuantity);
        Assert.Null(detail.Items.Single(item => item.ProductCode == "P-PLANT").OrderQuantity);
    }

    [Fact]
    public async Task 作废_只有装箱中的箱可以作废_作废的箱不出现在列表里()
    {
        var service = CreateService();
        var packing = await CartonWithAsync(service, ("HB23817", 1));
        var sealedCarton = await SealedCartonAsync(service, ("HB24102", 1));

        var voidSealed = await service.VoidCartonAsync(sealedCarton.CartonGuid, Lily);
        var voidPacking = await service.VoidCartonAsync(packing.CartonGuid, Lily);
        var list = await service.ListCartonsAsync("all", null, null);

        Assert.Equal(WarehousePackingErrorCodes.CartonNotPacking, voidSealed.ErrorCode);
        Assert.True(voidPacking.Success, voidPacking.Message);
        Assert.Equal(sealedCarton.CartonNo, Assert.Single(list.Data!.Items).CartonNo);
        Assert.Equal(0, list.Data.PackingCount);
        Assert.Equal(1, list.Data.SealedCount);
        Assert.Equal(1, list.Data.LooseSealedCount);
    }

    [Fact]
    public async Task 箱列表_按状态筛选_关键字可以是箱号片段或商品货号_可只看自己建的()
    {
        var service = CreateService();
        var mugCarton = await SealedCartonAsync(service, ("HB23817", 24));
        var candleCarton = await CartonWithAsync(service, ("HB24102", 6));
        await service.CreateCartonAsync(new WarehousePackingCreateCartonRequestDto(), Tom);

        var byItem = await service.ListCartonsAsync("all", "HB23817", null);
        var byNo = await service.ListCartonsAsync("all", "130002", null);
        var packing = await service.ListCartonsAsync("packing", null, null);
        var mine = await service.ListCartonsAsync("all", null, "user-tom");

        Assert.Equal(mugCarton.CartonNo, Assert.Single(byItem.Data!.Items).CartonNo);
        Assert.Equal(candleCarton.CartonNo, Assert.Single(byNo.Data!.Items).CartonNo);
        Assert.Equal(2, packing.Data!.Items.Count);
        Assert.Equal("Tom", Assert.Single(mine.Data!.Items).CreatedByName);
    }

    [Fact]
    public async Task 组托盘_托盘号按日期加流水_只能加已封箱的箱_同一只箱重复扫按成功处理()
    {
        var service = CreateService();
        var pallet = (await service.CreatePalletAsync(new WarehousePackingDestinationRequestDto(), Lily)).Data!;
        var packing = await CartonWithAsync(service, ("HB23817", 1));
        var sealedCarton = await SealedCartonAsync(service, ("HB23817", 24), ("HB24102", 6));

        var addPacking = await service.AddCartonToPalletAsync(pallet.PalletGuid, new WarehousePackingAddCartonRequestDto { Code = packing.CartonNo }, Lily);
        // 扫码枪可能送小写：箱号按大写匹配。
        var added = await service.AddCartonToPalletAsync(pallet.PalletGuid, new WarehousePackingAddCartonRequestDto { Code = sealedCarton.CartonNo.ToLowerInvariant() }, Lily);
        var addedAgain = await service.AddCartonToPalletAsync(pallet.PalletGuid, new WarehousePackingAddCartonRequestDto { Code = sealedCarton.CartonNo }, Lily);

        Assert.Equal("PL261113001", pallet.PalletNo);
        Assert.Equal(409, addPacking.StatusCode);
        Assert.Equal(WarehousePackingErrorCodes.CartonNotSealed, addPacking.ErrorCode);
        Assert.Equal(packing.CartonNo, addPacking.Data!.BlockedCarton!.CartonNo);
        Assert.True(added.Success, added.Message);
        Assert.True(addedAgain.Success, addedAgain.Message);
        Assert.Equal(1, addedAgain.Data!.CartonCount);
        Assert.Equal(2, addedAgain.Data.SkuCount);
        Assert.Equal(30, addedAgain.Data.PieceCount);
        Assert.Equal(sealedCarton.CartonNo, Assert.Single(addedAgain.Data.Cartons).CartonNo);
        Assert.Equal(pallet.PalletNo, addedAgain.Data.Cartons[0].PalletNo);
    }

    [Fact]
    public async Task 组托盘_箱已在别的托盘上时拦下_去向不一致时拦下_只有经理可以放行()
    {
        var service = CreateService();
        var sunnybankPallet = (await service.CreatePalletAsync(StoreDestination("1013"), Lily)).Data!;
        var inalaPallet = (await service.CreatePalletAsync(StoreDestination("1020"), Lily)).Data!;
        var sunnybankCarton = await SealedCartonAsync(service, StoreDestination("1013"), ("HB23817", 24));
        await service.AddCartonToPalletAsync(sunnybankPallet.PalletGuid, new WarehousePackingAddCartonRequestDto { Code = sunnybankCarton.CartonNo }, Lily);
        var secondSunnybankCarton = await SealedCartonAsync(service, StoreDestination("1013"), ("HB24102", 6));

        var onOtherPallet = await service.AddCartonToPalletAsync(inalaPallet.PalletGuid, new WarehousePackingAddCartonRequestDto { Code = sunnybankCarton.CartonNo }, Lily);
        var mismatch = await service.AddCartonToPalletAsync(inalaPallet.PalletGuid, new WarehousePackingAddCartonRequestDto { Code = secondSunnybankCarton.CartonNo }, Lily);
        var staffForce = await service.AddCartonToPalletAsync(
            inalaPallet.PalletGuid,
            new WarehousePackingAddCartonRequestDto { Code = secondSunnybankCarton.CartonNo, AllowDestinationMismatch = true },
            Lily
        );
        var managerForce = await service.AddCartonToPalletAsync(
            inalaPallet.PalletGuid,
            new WarehousePackingAddCartonRequestDto { Code = secondSunnybankCarton.CartonNo, AllowDestinationMismatch = true },
            Manager
        );

        Assert.Equal(WarehousePackingErrorCodes.CartonOnOtherPallet, onOtherPallet.ErrorCode);
        Assert.Equal(WarehousePackingErrorCodes.DestinationMismatch, mismatch.ErrorCode);
        Assert.Equal("Sunnybank", mismatch.Data!.BlockedCarton!.Destination.StoreName);
        Assert.Equal(WarehousePackingErrorCodes.DestinationMismatch, staffForce.ErrorCode);
        Assert.True(managerForce.Success, managerForce.Message);
        // 上了托盘的箱去向跟随托盘。
        Assert.Equal("Inala", Assert.Single(managerForce.Data!.Cartons).Destination.StoreName);
    }

    [Fact]
    public async Task 组托盘_箱没指定去向时可以直接上任何托盘_移出后恢复为散箱()
    {
        var service = CreateService();
        var pallet = (await service.CreatePalletAsync(StoreDestination("1013"), Lily)).Data!;
        var carton = await SealedCartonAsync(service, ("HB23817", 24));
        await service.SetCartonLocationAsync(carton.CartonGuid, "A-03", Lily);

        var added = await service.AddCartonToPalletAsync(pallet.PalletGuid, new WarehousePackingAddCartonRequestDto { Code = carton.CartonNo }, Lily);
        var reopen = await service.ReopenCartonAsync(carton.CartonGuid, Manager);
        var changeDestination = await service.UpdateCartonHeaderAsync(
            carton.CartonGuid,
            new WarehousePackingCartonHeaderRequestDto { DestinationType = WarehousePackDestinationTypes.Store, DestinationStoreCode = "1020" },
            Lily
        );
        var removed = await service.RemoveCartonFromPalletAsync(pallet.PalletGuid, carton.CartonGuid, Lily);
        var loose = (await service.GetCartonAsync(carton.CartonGuid)).Data!;

        Assert.True(added.Success, added.Message);
        // 上托盘后位置以托盘为准：箱自己的暂存货位被清掉。
        Assert.Null(added.Data!.Cartons[0].LocationCode);
        Assert.Equal(WarehousePackingErrorCodes.CartonOnPallet, reopen.ErrorCode);
        Assert.Equal(WarehousePackingErrorCodes.CartonOnPallet, changeDestination.ErrorCode);
        Assert.True(removed.Success, removed.Message);
        Assert.Empty(removed.Data!.Cartons);
        Assert.Null(loose.PalletGuid);
        Assert.Equal(WarehousePackDestinationTypes.Unassigned, loose.Destination.DestinationType);
    }

    [Fact]
    public async Task 封托_空托盘不能封_封托后不能加箱或移出_重新开托后可以()
    {
        var service = CreateService();
        var pallet = (await service.CreatePalletAsync(new WarehousePackingDestinationRequestDto(), Lily)).Data!;
        var first = await SealedCartonAsync(service, ("HB23817", 24));
        var second = await SealedCartonAsync(service, ("HB24102", 6));

        var sealEmpty = await service.SealPalletAsync(pallet.PalletGuid, new WarehousePackingSealRequestDto(), Lily);
        await service.AddCartonToPalletAsync(pallet.PalletGuid, new WarehousePackingAddCartonRequestDto { Code = first.CartonNo }, Lily);
        var sealedResult = await service.SealPalletAsync(pallet.PalletGuid, new WarehousePackingSealRequestDto { WeightKg = 42.5m }, Lily);
        var addAfterSeal = await service.AddCartonToPalletAsync(pallet.PalletGuid, new WarehousePackingAddCartonRequestDto { Code = second.CartonNo }, Lily);
        var removeAfterSeal = await service.RemoveCartonFromPalletAsync(pallet.PalletGuid, first.CartonGuid, Lily);
        var reopened = await service.ReopenPalletAsync(pallet.PalletGuid, Manager);
        var addAfterReopen = await service.AddCartonToPalletAsync(pallet.PalletGuid, new WarehousePackingAddCartonRequestDto { Code = second.CartonNo }, Lily);

        Assert.Equal(WarehousePackingErrorCodes.PalletEmpty, sealEmpty.ErrorCode);
        Assert.True(sealedResult.Success, sealedResult.Message);
        Assert.Equal(WarehousePackPalletStatuses.Sealed, sealedResult.Data!.Status);
        Assert.Equal(42.5m, sealedResult.Data.WeightKg);
        Assert.Equal(WarehousePackingErrorCodes.PalletNotBuilding, addAfterSeal.ErrorCode);
        Assert.Equal(WarehousePackingErrorCodes.PalletNotBuilding, removeAfterSeal.ErrorCode);
        Assert.True(reopened.Success, reopened.Message);
        Assert.Equal(2, addAfterReopen.Data!.CartonCount);
    }

    [Fact]
    public async Task 托盘去向_交运输公司并指定最终收货分店_封托后仍可补运单号()
    {
        var service = CreateService();
        var carrier = (await service.SaveCarrierAsync(null, new WarehousePackingCarrierSaveRequestDto { Name = "Border Express", Remark = "北昆州线" })).Data!;
        var pallet = (await service.CreatePalletAsync(new WarehousePackingDestinationRequestDto(), Lily)).Data!;
        var carton = await SealedCartonAsync(service, ("HB23817", 24));
        await service.AddCartonToPalletAsync(pallet.PalletGuid, new WarehousePackingAddCartonRequestDto { Code = carton.CartonNo }, Lily);
        await service.SealPalletAsync(pallet.PalletGuid, new WarehousePackingSealRequestDto(), Lily);

        var updated = await service.UpdatePalletHeaderAsync(
            pallet.PalletGuid,
            new WarehousePackingPalletHeaderRequestDto
            {
                DestinationType = WarehousePackDestinationTypes.Carrier,
                CarrierGuid = carrier.CarrierGuid,
                DestinationStoreCode = "1020",
                ConsignmentNo = " BEX 4471 0932 18 ",
                Remark = "FRAGILE",
            },
            Lily
        );

        Assert.True(updated.Success, updated.Message);
        Assert.Equal("Border Express", updated.Data!.Destination.CarrierName);
        Assert.Equal("Inala", updated.Data.Destination.StoreName);
        Assert.Equal("BEX 4471 0932 18", updated.Data.ConsignmentNo);
        Assert.Equal("FRAGILE", updated.Data.Remark);
        Assert.Equal("Border Express", updated.Data.Cartons[0].Destination.CarrierName);
    }

    [Fact]
    public async Task 运输公司_名称不能重复_停用后默认列表不再出现()
    {
        var service = CreateService();
        var toll = (await service.SaveCarrierAsync(null, new WarehousePackingCarrierSaveRequestDto { Name = "Toll" })).Data!;

        var duplicate = await service.SaveCarrierAsync(null, new WarehousePackingCarrierSaveRequestDto { Name = "Toll" });
        var blank = await service.SaveCarrierAsync(null, new WarehousePackingCarrierSaveRequestDto { Name = "  " });
        var renameSelf = await service.SaveCarrierAsync(toll.CarrierGuid, new WarehousePackingCarrierSaveRequestDto { Name = "Toll", IsActive = false });
        var active = await service.ListCarriersAsync(includeInactive: false);
        var all = await service.ListCarriersAsync(includeInactive: true);

        Assert.Equal(WarehousePackingErrorCodes.CarrierNameTaken, duplicate.ErrorCode);
        Assert.Equal(WarehousePackingErrorCodes.InvalidRequest, blank.ErrorCode);
        Assert.True(renameSelf.Success, renameSelf.Message);
        Assert.Empty(active.Data!);
        Assert.False(Assert.Single(all.Data!).IsActive);
    }

    [Fact]
    public async Task 位置查询_按商品列出所在箱托盘货位与数量_作废的箱不计_托盘上的箱取托盘货位()
    {
        var service = CreateService();
        var pallet = (await service.CreatePalletAsync(StoreDestination("1013"), Lily)).Data!;
        var onPallet = await SealedCartonAsync(service, ("HB23817", 48), ("HB24102", 6));
        await service.AddCartonToPalletAsync(pallet.PalletGuid, new WarehousePackingAddCartonRequestDto { Code = onPallet.CartonNo }, Lily);
        await service.SetPalletLocationAsync(pallet.PalletGuid, "2000000000031", Lily);
        var loose = await SealedCartonAsync(service, ("HB23817", 24));
        await service.SetCartonLocationAsync(loose.CartonGuid, "B-12", Lily);
        var voided = await CartonWithAsync(service, ("HB23817", 99));
        await service.VoidCartonAsync(voided.CartonGuid, Lily);

        var located = await service.LocateProductAsync("9345678123457");
        var missing = await service.LocateProductAsync("NOPE");

        Assert.True(located.Success, located.Message);
        var data = located.Data!;
        Assert.Equal("HB23817", data.Product.ItemNumber);
        Assert.Equal(72, data.PackedQuantity);
        Assert.Equal(2, data.CartonCount);
        Assert.Equal(1, data.PalletCount);
        Assert.Equal(340, data.StockQuantity);
        var palletRow = data.Rows.Single(row => row.CartonNo == onPallet.CartonNo);
        // 货位条码换成货位编码；箱在托盘上时位置取托盘的。
        Assert.Equal("A-03", palletRow.LocationCode);
        Assert.Equal(pallet.PalletNo, palletRow.PalletNo);
        Assert.Equal("Sunnybank", palletRow.Destination.StoreName);
        Assert.Equal(48, palletRow.Quantity);
        var looseRow = data.Rows.Single(row => row.CartonNo == loose.CartonNo);
        Assert.Equal("B-12", looseRow.LocationCode);
        Assert.Null(looseRow.PalletNo);
        Assert.Equal(404, missing.StatusCode);
    }

    [Fact]
    public async Task 货位_不存在的货位拒绝_传空清除()
    {
        var service = CreateService();
        var carton = await SealedCartonAsync(service, ("HB23817", 24));

        var unknown = await service.SetCartonLocationAsync(carton.CartonGuid, "Z-99", Lily);
        var set = await service.SetCartonLocationAsync(carton.CartonGuid, "B-12", Lily);
        var cleared = await service.SetCartonLocationAsync(carton.CartonGuid, " ", Lily);

        Assert.Equal(WarehousePackingErrorCodes.LocationNotFound, unknown.ErrorCode);
        Assert.Equal("B-12", set.Data!.LocationCode);
        Assert.Null(cleared.Data!.LocationCode);
    }

    [Fact]
    public async Task 识别扫到的码_箱号和托盘号按前缀分开_商品码返回404交回客户端()
    {
        var service = CreateService();
        var carton = (await service.CreateCartonAsync(new WarehousePackingCreateCartonRequestDto(), Lily)).Data!;
        var pallet = (await service.CreatePalletAsync(new WarehousePackingDestinationRequestDto(), Lily)).Data!;

        var cartonResolved = await service.ResolveCodeAsync($" {carton.CartonNo.ToLowerInvariant()} ");
        var palletResolved = await service.ResolveCodeAsync(pallet.PalletNo);
        var product = await service.ResolveCodeAsync("9345678123457");
        var unknownCarton = await service.ResolveCodeAsync("BX0000000000");

        Assert.Equal("carton", cartonResolved.Data!.Kind);
        Assert.Equal(carton.CartonGuid, cartonResolved.Data.Guid);
        Assert.Equal("pallet", palletResolved.Data!.Kind);
        Assert.Equal(pallet.PalletGuid, palletResolved.Data.Guid);
        Assert.Equal(404, product.StatusCode);
        Assert.Equal(404, unknownCarton.StatusCode);
    }

    [Fact]
    public async Task 订货单选择_只列已提交和配货中的订货单并带分店名()
    {
        var orders = await CreateService().SearchOrdersAsync("2026-04");

        var order = Assert.Single(orders.Data!);
        Assert.Equal("order-1", order.OrderGuid);
        Assert.Equal("Sunnybank", order.StoreName);
    }

    private static WarehousePackingCreateCartonRequestDto StoreDestination(string storeCode) => new()
    {
        DestinationType = WarehousePackDestinationTypes.Store,
        DestinationStoreCode = storeCode,
    };

    private Task<WarehousePackingCartonDetailDto> CartonWithAsync(IWarehousePackingService service, params (string Code, int Quantity)[] items) =>
        CartonWithAsync(service, new WarehousePackingCreateCartonRequestDto(), items);

    private async Task<WarehousePackingCartonDetailDto> CartonWithAsync(
        IWarehousePackingService service,
        WarehousePackingCreateCartonRequestDto request,
        params (string Code, int Quantity)[] items
    )
    {
        var carton = (await service.CreateCartonAsync(request, Lily)).Data!;
        foreach (var (code, quantity) in items)
        {
            var scan = await service.ScanIntoCartonAsync(carton.CartonGuid, new WarehousePackingScanRequestDto { Code = code, Quantity = quantity }, Lily);
            Assert.True(scan.Success, scan.Message);
            carton = scan.Data!;
        }

        return carton;
    }

    private Task<WarehousePackingCartonDetailDto> SealedCartonAsync(IWarehousePackingService service, params (string Code, int Quantity)[] items) =>
        SealedCartonAsync(service, new WarehousePackingCreateCartonRequestDto(), items);

    private async Task<WarehousePackingCartonDetailDto> SealedCartonAsync(
        IWarehousePackingService service,
        WarehousePackingCreateCartonRequestDto request,
        params (string Code, int Quantity)[] items
    )
    {
        var carton = await CartonWithAsync(service, request, items);
        var sealedResult = await service.SealCartonAsync(carton.CartonGuid, new WarehousePackingSealRequestDto(), Lily);
        Assert.True(sealedResult.Success, sealedResult.Message);
        return sealedResult.Data!;
    }

    private void SeedMasterData()
    {
        _db.Insertable(new List<Store>
        {
            new() { StoreGUID = "store-1013", StoreCode = "1013", StoreName = "Sunnybank" },
            new() { StoreGUID = "store-1020", StoreCode = "1020", StoreName = "Inala" },
        }).ExecuteCommand();
        _db.Insertable(new List<Product>
        {
            ProductRow("P-MUG", "HB23817", "9345678123457", "Ceramic Mug 350ml", middlePack: 12),
            ProductRow("P-CANDLE", "HB24102", "9345678241023", "Scented Candle 200g", middlePack: 6),
            ProductRow("P-PLANT", "HB21960", "0936012345678", "Artificial Plant", middlePack: null),
            ProductRow("P-DUP-A", "HB30001", "DUP-CODE", "Duplicate A", middlePack: null),
            ProductRow("P-DUP-B", "HB30002", "DUP-CODE", "Duplicate B", middlePack: null),
        }).ExecuteCommand();
        _db.Insertable(new List<WarehouseProduct>
        {
            new() { ProductCode = "P-MUG", StockQuantity = 340, PackingQuantity = 48, IsActive = true },
            new() { ProductCode = "P-CANDLE", StockQuantity = 80, PackingQuantity = 24, IsActive = true },
        }).ExecuteCommand();
        _db.Insertable(new ProductSetCode
        {
            ProductCode = "P-MUG",
            SetProductCode = "P-MUG-SET6",
            SetItemNumber = "HB23817-6",
            SetBarcode = "SET-MUG-6",
            SetQuantity = 6,
            SetType = 2,
        }).ExecuteCommand();
        _db.Insertable(new List<Location>
        {
            new() { LocationGuid = "loc-a03", LocationType = 2, LocationCode = "A-03", LocationBarcode = "2000000000031", Status = 1 },
            new() { LocationGuid = "loc-b12", LocationType = 2, LocationCode = "B-12", LocationBarcode = "2000000000123", Status = 1 },
        }).ExecuteCommand();
        _db.Insertable(new List<WareHouseOrder>
        {
            new() { OrderGUID = "order-1", OrderNo = "2026-0412", StoreCode = "1013", FlowStatus = 3, OrderDate = new DateTime(2026, 11, 12, 9, 0, 0) },
            // 已完成的订货单不再出现在装箱的订货单选择里。
            new() { OrderGUID = "order-done", OrderNo = "2026-0400", StoreCode = "1020", FlowStatus = 2, OrderDate = new DateTime(2026, 11, 1, 9, 0, 0) },
        }).ExecuteCommand();
        _db.Insertable(new List<WareHouseOrderDetails>
        {
            new() { DetailGUID = "d-mug", OrderGUID = "order-1", StoreCode = "1013", ProductCode = "P-MUG", Quantity = 48, AllocQuantity = 36 },
            new() { DetailGUID = "d-candle", OrderGUID = "order-1", StoreCode = "1013", ProductCode = "P-CANDLE", Quantity = 24, AllocQuantity = null },
        }).ExecuteCommand();
    }

    private static Product ProductRow(string productCode, string itemNumber, string barcode, string name, int? middlePack) => new()
    {
        ProductCode = productCode,
        ItemNumber = itemNumber,
        Barcode = barcode,
        ProductName = name,
        MiddlePackageQuantity = middlePack,
    };

    private IWarehousePackingService CreateService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WarehousePacking:OriginName"] = "HB TEST WAREHOUSE",
                ["WarehousePacking:TimeZoneId"] = "Australia/Brisbane",
            })
            .Build();
        return new WarehousePackingService(
            CreateSqlSugarContext(_db),
            configuration,
            NullLogger<WarehousePackingService>.Instance,
            _time
        );
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

    /// <summary>可手动拨动的时钟：箱号里的日期、「刚扫的排最前」都依赖时间。</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public void Advance(TimeSpan delta) => Now = Now.Add(delta);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
