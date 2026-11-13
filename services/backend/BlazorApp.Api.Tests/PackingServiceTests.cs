using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.Packing;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 扫码打包：每只箱 / 托盘有所属地点（分店或总仓），店间调货靠「发出 → 对方扫码签收」换所属地点。
/// 覆盖装箱、封箱、组托盘、去向、发出、签收（含托盘标签丢失、数量核对、逐箱核对）、位置查询与数据范围。
/// </summary>
public sealed class PackingServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 11, 13, 4, 30, 0, TimeSpan.Zero));

    private static readonly PackingOwner Sunnybank = new("1013", "Sunnybank", false);
    private static readonly PackingOwner Inala = new("1020", "Inala", false);
    private static readonly PackingOwner Perth = new("1030", "Perth", false);
    private static readonly PackingOwner Warehouse = new(PackOwners.Warehouse, "HB TEST WAREHOUSE", true);

    private static PackingOperator Staff(string guid, string name, params PackingOwner[] owners) => new(guid, name, false, false, owners);

    private static readonly PackingOperator Lily = Staff("u-lily", "Lily", Sunnybank);
    private static readonly PackingOperator Tom = Staff("u-tom", "Tom", Sunnybank);
    private static readonly PackingOperator SunnyManager = new("u-mgr", "Morgan", true, false, new[] { Sunnybank });
    private static readonly PackingOperator Ian = Staff("u-ian", "Ian", Inala);
    private static readonly PackingOperator InalaManager = new("u-imgr", "Iris", true, false, new[] { Inala });
    private static readonly PackingOperator Pete = Staff("u-pete", "Pete", Perth);
    private static readonly PackingOperator Wendy = Staff("u-wendy", "Wendy", Warehouse);
    private static readonly PackingOperator Admin = new("u-admin", "Admin", true, true, new[] { Sunnybank, Inala, Perth, Warehouse });

    public PackingServiceTests()
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
            typeof(Location),
            typeof(Store),
            typeof(PackCarton),
            typeof(PackCartonItem),
            typeof(PackPallet),
            typeof(PackCarrier),
            typeof(PackLocation),
            typeof(PackEvent),
            typeof(PackSequence)
        );
        SeedMasterData();
    }

    // ---- 建箱：编号、所属地点、去向 ----

    [Fact]
    public async Task 建箱_箱号按所属地点当地日期加当日流水_发出地取店名()
    {
        var first = await CreateService().CreateCartonAsync(Lily, new PackingCreateCartonRequestDto());
        var second = await CreateService().CreateCartonAsync(Tom, new PackingCreateCartonRequestDto());

        Assert.True(first.Success, first.Message);
        Assert.Equal("BX2611130001", first.Data!.CartonNo);
        Assert.Equal("BX2611130002", second.Data!.CartonNo);
        Assert.Equal(PackCartonStatuses.Packing, first.Data.Status);
        Assert.Equal("1013", first.Data.OwnerCode);
        Assert.Equal("Sunnybank", first.Data.OwnerName);
        Assert.Equal("Sunnybank", first.Data.OriginName);
        Assert.Equal("Lily", first.Data.CreatedByName);
        Assert.Equal(PackDestinationTypes.Unassigned, first.Data.Destination.DestinationType);
    }

    [Fact]
    public async Task 建箱_日期按各分店自己的时区_总仓取配置时区()
    {
        // UTC 11-13 15:30：布里斯班（UTC+10）已是 11-14 01:30，珀斯（UTC+8）还是 11-13 23:30。
        _time.Now = new DateTimeOffset(2026, 11, 13, 15, 30, 0, TimeSpan.Zero);

        var brisbane = await CreateService().CreateCartonAsync(Lily, new PackingCreateCartonRequestDto());
        var perth = await CreateService().CreateCartonAsync(Pete, new PackingCreateCartonRequestDto());
        var warehouse = await CreateService().CreateCartonAsync(Wendy, new PackingCreateCartonRequestDto());

        Assert.Equal("BX2611140001", brisbane.Data!.CartonNo);
        Assert.Equal("BX2611130001", perth.Data!.CartonNo);
        Assert.Equal("BX2611140002", warehouse.Data!.CartonNo);
        Assert.Equal("HB TEST WAREHOUSE", warehouse.Data.OriginName);
        Assert.Equal(PackOwners.Warehouse, warehouse.Data.OwnerCode);
    }

    [Fact]
    public async Task 建箱_多地点账号必须指定所属地点_不能在没有权限的地点建箱()
    {
        var missing = await CreateService().CreateCartonAsync(Admin, new PackingCreateCartonRequestDto());
        var notAllowed = await CreateService().CreateCartonAsync(Lily, new PackingCreateCartonRequestDto { OwnerCode = "1020" });
        var ok = await CreateService().CreateCartonAsync(Admin, new PackingCreateCartonRequestDto { OwnerCode = "1020" });

        Assert.Equal(PackingErrorCodes.OwnerRequired, missing.ErrorCode);
        Assert.Equal(403, notAllowed.StatusCode);
        Assert.Equal(PackingErrorCodes.OwnerNotAllowed, notAllowed.ErrorCode);
        Assert.Equal("Inala", ok.Data!.OwnerName);
        Assert.Equal(1, await _db.Queryable<PackCarton>().CountAsync());
    }

    [Fact]
    public async Task 建箱_去向校验_不能发给自己_分店要存在_总仓不能发总仓_运输公司要存在()
    {
        var self = await CreateService().CreateCartonAsync(Lily, Dest(PackDestinationTypes.Store, "1013"));
        var unknownStore = await CreateService().CreateCartonAsync(Lily, Dest(PackDestinationTypes.Store, "9999"));
        var noStore = await CreateService().CreateCartonAsync(Lily, Dest(PackDestinationTypes.Store, null));
        var warehouseToWarehouse = await CreateService().CreateCartonAsync(Wendy, Dest(PackDestinationTypes.Warehouse, null));
        var unknownCarrier = await CreateService().CreateCartonAsync(Lily, Dest(PackDestinationTypes.Store, "1020", "no-such-carrier"));

        foreach (var result in new[] { self, unknownStore, noStore, warehouseToWarehouse, unknownCarrier })
        {
            Assert.Equal(PackingErrorCodes.DestinationInvalid, result.ErrorCode);
        }

        Assert.Equal(0, await _db.Queryable<PackCarton>().CountAsync());
    }

    [Fact]
    public async Task 建箱_去向可以是其他分店或总仓_本地存放和未指定时运输公司被清空()
    {
        var carrier = (await CreateService().SaveCarrierAsync(Lily, null, new PackingCarrierSaveRequestDto { Name = "Border Express" })).Data!;

        var toStore = await CreateService().CreateCartonAsync(Lily, Dest(PackDestinationTypes.Store, "1020", carrier.CarrierGuid));
        var toWarehouse = await CreateService().CreateCartonAsync(Lily, Dest(PackDestinationTypes.Warehouse, null));
        var local = await CreateService().CreateCartonAsync(Lily, Dest(PackDestinationTypes.Local, "1020", carrier.CarrierGuid));

        Assert.Equal("Inala", toStore.Data!.Destination.Name);
        Assert.Equal("Border Express", toStore.Data.Destination.CarrierName);
        Assert.Equal(PackOwners.Warehouse, toWarehouse.Data!.Destination.Code);
        Assert.Equal("HB TEST WAREHOUSE", toWarehouse.Data.Destination.Name);
        Assert.Equal(PackDestinationTypes.Local, local.Data!.Destination.DestinationType);
        Assert.Null(local.Data.Destination.Code);
        Assert.Null(local.Data.Destination.CarrierGuid);
    }

    // ---- 扫码装箱 ----

    [Fact]
    public async Task 扫码_同一商品累加_条码货号都认_刚扫的排在最前()
    {
        var service = CreateService();
        var carton = (await service.CreateCartonAsync(Lily, new PackingCreateCartonRequestDto())).Data!;

        await service.ScanIntoCartonAsync(Lily, carton.CartonGuid, Scan("9345678123457"));
        _time.Advance(TimeSpan.FromSeconds(5));
        await service.ScanIntoCartonAsync(Lily, carton.CartonGuid, Scan("HB24102", 12));
        _time.Advance(TimeSpan.FromSeconds(5));
        var result = await service.ScanIntoCartonAsync(Lily, carton.CartonGuid, Scan("HB23817"));

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
        var carton = (await service.CreateCartonAsync(Lily, new PackingCreateCartonRequestDto())).Data!;

        var result = await service.ScanIntoCartonAsync(Lily, carton.CartonGuid, Scan("SET-MUG-6"));

        Assert.True(result.Success, result.Message);
        Assert.Equal("P-MUG", Assert.Single(result.Data!.Items).ProductCode);
        Assert.Equal(1, result.Data.PieceCount);
    }

    [Fact]
    public async Task 扫码_本店多码可用_别的分店设的多码不认()
    {
        var service = CreateService();
        var carton = (await service.CreateCartonAsync(Lily, new PackingCreateCartonRequestDto())).Data!;

        var own = await service.ScanIntoCartonAsync(Lily, carton.CartonGuid, Scan("MULTI-MUG", 3));
        var other = await service.ScanIntoCartonAsync(Lily, carton.CartonGuid, Scan("INALA-ONLY-MULTI"));

        Assert.True(own.Success, own.Message);
        Assert.Equal(3, Assert.Single(own.Data!.Items).Quantity);
        Assert.Equal(404, other.StatusCode);
        Assert.Equal(PackingErrorCodes.CodeNotFound, other.ErrorCode);
    }

    [Fact]
    public async Task 扫码_iOS去掉前导0的条码仍能认出商品()
    {
        var service = CreateService();
        var carton = (await service.CreateCartonAsync(Lily, new PackingCreateCartonRequestDto())).Data!;

        var result = await service.ScanIntoCartonAsync(Lily, carton.CartonGuid, Scan("936012345678"));

        Assert.True(result.Success, result.Message);
        Assert.Equal("P-PLANT", Assert.Single(result.Data!.Items).ProductCode);
    }

    [Fact]
    public async Task 扫码_找不到的码返回404_一码多品返回候选_选定后带商品编码提交成功()
    {
        var service = CreateService();
        var carton = (await service.CreateCartonAsync(Lily, new PackingCreateCartonRequestDto())).Data!;

        var unknown = await service.ScanIntoCartonAsync(Lily, carton.CartonGuid, Scan("NOPE"));
        var ambiguous = await service.ScanIntoCartonAsync(Lily, carton.CartonGuid, Scan("DUP-CODE"));
        var chosen = await service.ScanIntoCartonAsync(Lily, carton.CartonGuid, new PackingScanRequestDto { Code = "DUP-CODE", ProductCode = "P-DUP-B", Quantity = 3 });

        Assert.Equal(PackingErrorCodes.CodeNotFound, unknown.ErrorCode);
        Assert.Equal(409, ambiguous.StatusCode);
        Assert.Equal(PackingErrorCodes.CodeAmbiguous, ambiguous.ErrorCode);
        Assert.Equal(new[] { "P-DUP-A", "P-DUP-B" }, ambiguous.Data!.Candidates!.Select(item => item.ProductCode).OrderBy(code => code));
        Assert.Equal(3, Assert.Single(chosen.Data!.Items).Quantity);
    }

    [Fact]
    public async Task 改数量_设为0移除商品_超出范围拒绝()
    {
        var service = CreateService();
        var carton = await CartonWithAsync(service, Lily, ("HB23817", 5), ("HB24102", 2));

        var tooMany = await service.SetCartonItemQuantityAsync(Lily, carton.CartonGuid, "P-MUG", 100001);
        var changed = await service.SetCartonItemQuantityAsync(Lily, carton.CartonGuid, "P-MUG", 24);
        var removed = await service.SetCartonItemQuantityAsync(Lily, carton.CartonGuid, "P-CANDLE", 0);

        Assert.Equal(PackingErrorCodes.QuantityOutOfRange, tooMany.ErrorCode);
        Assert.Equal(24, changed.Data!.Items.Single(item => item.ProductCode == "P-MUG").Quantity);
        Assert.Equal("P-MUG", Assert.Single(removed.Data!.Items).ProductCode);
        Assert.Equal(24, removed.Data.PieceCount);
    }

    // ---- 封箱、重新开箱、作废 ----

    [Fact]
    public async Task 封箱_空箱不能封_封箱后不能再扫码或改数量_重复封箱幂等且不改封箱人()
    {
        var service = CreateService();
        var empty = (await service.CreateCartonAsync(Lily, new PackingCreateCartonRequestDto())).Data!;
        var carton = await CartonWithAsync(service, Lily, ("HB23817", 24));

        var sealEmpty = await service.SealCartonAsync(Lily, empty.CartonGuid, new PackingSealRequestDto());
        var sealedResult = await service.SealCartonAsync(Tom, carton.CartonGuid, new PackingSealRequestDto { WeightKg = 4.2m });
        var sealAgain = await service.SealCartonAsync(Lily, carton.CartonGuid, new PackingSealRequestDto());
        var scan = await service.ScanIntoCartonAsync(Lily, carton.CartonGuid, Scan("HB23817"));
        var setQuantity = await service.SetCartonItemQuantityAsync(Lily, carton.CartonGuid, "P-MUG", 1);

        Assert.Equal(PackingErrorCodes.CartonEmpty, sealEmpty.ErrorCode);
        Assert.True(sealedResult.Success, sealedResult.Message);
        Assert.Equal(PackCartonStatuses.Sealed, sealedResult.Data!.Status);
        Assert.Equal("Tom", sealedResult.Data.SealedByName);
        Assert.Equal(4.2m, sealedResult.Data.WeightKg);
        Assert.True(sealAgain.Success);
        Assert.Equal("Tom", sealAgain.Data!.SealedByName);
        Assert.Equal(PackingErrorCodes.CartonNotPacking, scan.ErrorCode);
        Assert.Equal(PackingErrorCodes.CartonNotPacking, setQuantity.ErrorCode);
        Assert.Equal(24, (await service.GetCartonAsync(Lily, carton.CartonGuid)).Data!.PieceCount);
    }

    [Fact]
    public async Task 重新开箱_只有店长可以_箱号不变可继续装_并留下操作记录()
    {
        var service = CreateService();
        var carton = (await service.CreateCartonAsync(Lily, new PackingCreateCartonRequestDto())).Data!;
        _time.Advance(TimeSpan.FromMinutes(1));
        await service.ScanIntoCartonAsync(Lily, carton.CartonGuid, Scan("HB23817", 24));
        await service.SealCartonAsync(Lily, carton.CartonGuid, new PackingSealRequestDto());
        _time.Advance(TimeSpan.FromMinutes(1));

        var byStaff = await service.ReopenCartonAsync(Lily, carton.CartonGuid);
        var byManager = await service.ReopenCartonAsync(SunnyManager, carton.CartonGuid);
        var scan = await service.ScanIntoCartonAsync(Lily, carton.CartonGuid, Scan("HB23817"));
        var events = await service.ListEventsAsync(Lily, PackTargetTypes.Carton, carton.CartonGuid);

        Assert.Equal(403, byStaff.StatusCode);
        Assert.True(byManager.Success, byManager.Message);
        Assert.Equal(carton.CartonNo, byManager.Data!.CartonNo);
        Assert.Equal(PackCartonStatuses.Packing, byManager.Data.Status);
        Assert.Null(byManager.Data.SealedByName);
        Assert.Equal(25, scan.Data!.PieceCount);
        Assert.Equal(
            new[] { PackEventActions.Reopened, PackEventActions.Sealed, PackEventActions.Created },
            events.Data!.Select(item => item.Action)
        );
        Assert.Equal("Morgan", events.Data![0].OperatorName);
    }

    [Fact]
    public async Task 作废_只有装箱中的箱可以作废_作废的箱不出现在列表里()
    {
        var service = CreateService();
        var packing = await CartonWithAsync(service, Lily, ("HB23817", 1));
        var sealedCarton = await SealedCartonAsync(service, Lily, ("HB24102", 1));

        var voidSealed = await service.VoidCartonAsync(Lily, sealedCarton.CartonGuid);
        var voidPacking = await service.VoidCartonAsync(Lily, packing.CartonGuid);
        var list = await service.ListCartonsAsync(Lily, null, "all", null, false);

        Assert.Equal(PackingErrorCodes.CartonNotPacking, voidSealed.ErrorCode);
        Assert.True(voidPacking.Success, voidPacking.Message);
        Assert.Equal(sealedCarton.CartonNo, Assert.Single(list.Data!.Items).CartonNo);
        Assert.Equal(0, list.Data.PackingCount);
        Assert.Equal(1, list.Data.StoredCount);
    }

    // ---- 数据范围：只看本店 ----

    [Fact]
    public async Task 数据范围_别的分店看不到也改不了本店的箱_按找不到处理()
    {
        var service = CreateService();
        var carton = await SealedCartonAsync(service, Lily, ("HB23817", 24));

        var get = await service.GetCartonAsync(Ian, carton.CartonGuid);
        var scan = await service.ScanIntoCartonAsync(Ian, carton.CartonGuid, Scan("HB23817"));
        var seal = await service.SealCartonAsync(Ian, carton.CartonGuid, new PackingSealRequestDto());
        var reopen = await service.ReopenCartonAsync(InalaManager, carton.CartonGuid);
        var location = await service.SetCartonLocationAsync(Ian, carton.CartonGuid, null);
        var events = await service.ListEventsAsync(Ian, PackTargetTypes.Carton, carton.CartonGuid);
        var resolve = await service.ResolveCodeAsync(Ian, carton.CartonNo);
        var inalaList = await service.ListCartonsAsync(Ian, null, "all", null, false);

        foreach (var result in new[] { get, scan, seal, reopen, location })
        {
            Assert.Equal(404, result.StatusCode);
            Assert.Equal(PackingErrorCodes.CartonNotFound, result.ErrorCode);
        }

        Assert.Equal(404, events.StatusCode);
        Assert.Equal(404, resolve.StatusCode);
        Assert.Empty(inalaList.Data!.Items);
        Assert.Equal(PackCartonStatuses.Sealed, (await service.GetCartonAsync(Lily, carton.CartonGuid)).Data!.Status);
    }

    [Fact]
    public async Task 数据范围_列表必须指定本账号有权限的分店()
    {
        var service = CreateService();

        var notAllowed = await service.ListCartonsAsync(Lily, "1020", "all", null, false);
        var adminMissing = await service.ListPalletsAsync(Admin, null, "all", null);
        var adminOk = await service.ListCartonsAsync(Admin, "1020", "all", null, false);

        Assert.Equal(PackingErrorCodes.OwnerNotAllowed, notAllowed.ErrorCode);
        Assert.Equal(PackingErrorCodes.OwnerRequired, adminMissing.ErrorCode);
        Assert.True(adminOk.Success);
    }

    [Fact]
    public async Task 箱列表_按状态筛选_关键字可以是箱号片段或商品货号_可只看自己建的()
    {
        var service = CreateService();
        var mugCarton = await SealedCartonAsync(service, Lily, ("HB23817", 24));
        var candleCarton = await CartonWithAsync(service, Lily, ("HB24102", 6));
        await service.CreateCartonAsync(Tom, new PackingCreateCartonRequestDto());

        var byItem = await service.ListCartonsAsync(Lily, null, "all", "HB23817", false);
        var byNo = await service.ListCartonsAsync(Lily, null, "all", "130002", false);
        var packing = await service.ListCartonsAsync(Lily, null, "packing", null, false);
        var stored = await service.ListCartonsAsync(Lily, null, "stored", null, false);
        var mine = await service.ListCartonsAsync(Tom, null, "all", null, true);

        Assert.Equal(mugCarton.CartonNo, Assert.Single(byItem.Data!.Items).CartonNo);
        Assert.Equal(candleCarton.CartonNo, Assert.Single(byNo.Data!.Items).CartonNo);
        Assert.Equal(2, packing.Data!.Items.Count);
        Assert.Equal(mugCarton.CartonNo, Assert.Single(stored.Data!.Items).CartonNo);
        Assert.Equal("Tom", Assert.Single(mine.Data!.Items).CreatedByName);
    }

    // ---- 托盘 ----

    [Fact]
    public async Task 组托盘_托盘号按日期加流水_只能加已封箱的箱_同一只箱重复扫按成功处理_小写箱号也认()
    {
        var service = CreateService();
        var pallet = (await service.CreatePalletAsync(Lily, new PackingCreatePalletRequestDto())).Data!;
        var packing = await CartonWithAsync(service, Lily, ("HB23817", 1));
        var sealedCarton = await SealedCartonAsync(service, Lily, ("HB23817", 24), ("HB24102", 6));

        var addPacking = await service.AddCartonToPalletAsync(Lily, pallet.PalletGuid, new PackingAddCartonRequestDto { Code = packing.CartonNo });
        var added = await service.AddCartonToPalletAsync(Lily, pallet.PalletGuid, new PackingAddCartonRequestDto { Code = sealedCarton.CartonNo.ToLowerInvariant() });
        var addedAgain = await service.AddCartonToPalletAsync(Lily, pallet.PalletGuid, new PackingAddCartonRequestDto { Code = sealedCarton.CartonNo });

        Assert.Equal("PL261113001", pallet.PalletNo);
        Assert.Equal(PackingErrorCodes.CartonNotSealed, addPacking.ErrorCode);
        Assert.Equal(packing.CartonNo, addPacking.Data!.BlockedCarton!.CartonNo);
        Assert.True(added.Success, added.Message);
        Assert.True(addedAgain.Success, addedAgain.Message);
        Assert.Equal(1, addedAgain.Data!.CartonCount);
        Assert.Equal(2, addedAgain.Data.SkuCount);
        Assert.Equal(30, addedAgain.Data.PieceCount);
        Assert.Equal(pallet.PalletNo, addedAgain.Data.Cartons[0].PalletNo);
    }

    [Fact]
    public async Task 组托盘_别的分店的箱不能加_箱在别的托盘上拦下_去向不一致拦下_只有店长可以放行()
    {
        var service = CreateService();
        var toInala = (await service.CreatePalletAsync(Lily, PalletDest(PackDestinationTypes.Store, "1020"))).Data!;
        var toPerth = (await service.CreatePalletAsync(Lily, PalletDest(PackDestinationTypes.Store, "1030"))).Data!;
        var inalaCarton = await SealedCartonAsync(service, Lily, Dest(PackDestinationTypes.Store, "1020"), ("HB23817", 24));
        await service.AddCartonToPalletAsync(Lily, toInala.PalletGuid, new PackingAddCartonRequestDto { Code = inalaCarton.CartonNo });
        var inalaCarton2 = await SealedCartonAsync(service, Lily, Dest(PackDestinationTypes.Store, "1020"), ("HB24102", 6));
        var foreign = await SealedCartonAsync(service, Ian, ("HB23817", 1));

        var onOther = await service.AddCartonToPalletAsync(Lily, toPerth.PalletGuid, new PackingAddCartonRequestDto { Code = inalaCarton.CartonNo });
        var mismatch = await service.AddCartonToPalletAsync(Lily, toPerth.PalletGuid, new PackingAddCartonRequestDto { Code = inalaCarton2.CartonNo });
        var staffForce = await service.AddCartonToPalletAsync(Lily, toPerth.PalletGuid, new PackingAddCartonRequestDto { Code = inalaCarton2.CartonNo, AllowDestinationMismatch = true });
        var managerForce = await service.AddCartonToPalletAsync(SunnyManager, toPerth.PalletGuid, new PackingAddCartonRequestDto { Code = inalaCarton2.CartonNo, AllowDestinationMismatch = true });
        var otherStoreCarton = await service.AddCartonToPalletAsync(Lily, toPerth.PalletGuid, new PackingAddCartonRequestDto { Code = foreign.CartonNo });

        Assert.Equal(PackingErrorCodes.CartonOnOtherPallet, onOther.ErrorCode);
        Assert.Equal(PackingErrorCodes.DestinationMismatch, mismatch.ErrorCode);
        Assert.Equal("Inala", mismatch.Data!.BlockedCarton!.Destination.Name);
        Assert.Equal(PackingErrorCodes.DestinationMismatch, staffForce.ErrorCode);
        Assert.True(managerForce.Success, managerForce.Message);
        // 上了托盘的箱去向跟随托盘。
        Assert.Equal("Perth", Assert.Single(managerForce.Data!.Cartons).Destination.Name);
        Assert.Equal(PackingErrorCodes.CartonNotFound, otherStoreCarton.ErrorCode);
    }

    [Fact]
    public async Task 组托盘_箱没指定去向时可以上任何托盘_上托盘清掉箱自己的位置_移出后恢复为散箱()
    {
        var service = CreateService();
        await CreateLocationAsync(service, "后仓 A 架");
        var pallet = (await service.CreatePalletAsync(Lily, PalletDest(PackDestinationTypes.Store, "1020"))).Data!;
        var carton = await SealedCartonAsync(service, Lily, ("HB23817", 24));
        await service.SetCartonLocationAsync(Lily, carton.CartonGuid, "后仓 A 架");

        var added = await service.AddCartonToPalletAsync(Lily, pallet.PalletGuid, new PackingAddCartonRequestDto { Code = carton.CartonNo });
        var reopen = await service.ReopenCartonAsync(SunnyManager, carton.CartonGuid);
        var changeDestination = await service.UpdateCartonHeaderAsync(Lily, carton.CartonGuid, Header(PackDestinationTypes.Store, "1030"));
        var removed = await service.RemoveCartonFromPalletAsync(Lily, pallet.PalletGuid, carton.CartonGuid);
        var loose = (await service.GetCartonAsync(Lily, carton.CartonGuid)).Data!;

        Assert.True(added.Success, added.Message);
        Assert.Null(added.Data!.Cartons[0].LocationCode);
        Assert.Equal(PackingErrorCodes.CartonOnPallet, reopen.ErrorCode);
        Assert.Equal(PackingErrorCodes.CartonOnPallet, changeDestination.ErrorCode);
        Assert.True(removed.Success, removed.Message);
        Assert.Empty(removed.Data!.Cartons);
        Assert.Null(loose.PalletGuid);
        Assert.Equal(PackDestinationTypes.Unassigned, loose.Destination.DestinationType);
    }

    [Fact]
    public async Task 封托_空托盘不能封_封托后不能加箱或移出_店长重新开托后可以()
    {
        var service = CreateService();
        var pallet = (await service.CreatePalletAsync(Lily, new PackingCreatePalletRequestDto())).Data!;
        var first = await SealedCartonAsync(service, Lily, ("HB23817", 24));
        var second = await SealedCartonAsync(service, Lily, ("HB24102", 6));

        var sealEmpty = await service.SealPalletAsync(Lily, pallet.PalletGuid, new PackingSealRequestDto());
        await service.AddCartonToPalletAsync(Lily, pallet.PalletGuid, new PackingAddCartonRequestDto { Code = first.CartonNo });
        var sealedResult = await service.SealPalletAsync(Lily, pallet.PalletGuid, new PackingSealRequestDto { WeightKg = 42.5m });
        var addAfterSeal = await service.AddCartonToPalletAsync(Lily, pallet.PalletGuid, new PackingAddCartonRequestDto { Code = second.CartonNo });
        var removeAfterSeal = await service.RemoveCartonFromPalletAsync(Lily, pallet.PalletGuid, first.CartonGuid);
        var reopenByStaff = await service.ReopenPalletAsync(Lily, pallet.PalletGuid);
        var reopened = await service.ReopenPalletAsync(SunnyManager, pallet.PalletGuid);
        var addAfterReopen = await service.AddCartonToPalletAsync(Lily, pallet.PalletGuid, new PackingAddCartonRequestDto { Code = second.CartonNo });

        Assert.Equal(PackingErrorCodes.PalletEmpty, sealEmpty.ErrorCode);
        Assert.Equal(PackPalletStatuses.Sealed, sealedResult.Data!.Status);
        Assert.Equal(42.5m, sealedResult.Data.WeightKg);
        Assert.Equal(PackingErrorCodes.PalletNotBuilding, addAfterSeal.ErrorCode);
        Assert.Equal(PackingErrorCodes.PalletNotBuilding, removeAfterSeal.ErrorCode);
        Assert.Equal(403, reopenByStaff.StatusCode);
        Assert.True(reopened.Success, reopened.Message);
        Assert.Equal(2, addAfterReopen.Data!.CartonCount);
    }

    // ---- 发出 ----

    [Fact]
    public async Task 发出散箱_必须先封箱且去向是其他分店或总仓_在托盘上的箱要发整个托盘()
    {
        var service = CreateService();
        var packing = await CartonWithAsync(service, Lily, Dest(PackDestinationTypes.Store, "1020"), ("HB23817", 24));
        var noDestination = await SealedCartonAsync(service, Lily, ("HB23817", 24));
        var local = await SealedCartonAsync(service, Lily, Dest(PackDestinationTypes.Local, null), ("HB23817", 24));
        var onPallet = await SealedCartonAsync(service, Lily, Dest(PackDestinationTypes.Store, "1020"), ("HB23817", 24));
        var pallet = (await service.CreatePalletAsync(Lily, new PackingCreatePalletRequestDto())).Data!;
        await service.AddCartonToPalletAsync(Lily, pallet.PalletGuid, new PackingAddCartonRequestDto { Code = onPallet.CartonNo });

        var notSealed = await service.DispatchCartonAsync(Lily, packing.CartonGuid);
        var noDest = await service.DispatchCartonAsync(Lily, noDestination.CartonGuid);
        var localDest = await service.DispatchCartonAsync(Lily, local.CartonGuid);
        var palletCarton = await service.DispatchCartonAsync(Lily, onPallet.CartonGuid);

        Assert.Equal(PackingErrorCodes.CartonNotSealed, notSealed.ErrorCode);
        Assert.Equal(PackingErrorCodes.NotShippable, noDest.ErrorCode);
        Assert.Equal(PackingErrorCodes.NotShippable, localDest.ErrorCode);
        Assert.Equal(PackingErrorCodes.CartonOnPallet, palletCarton.ErrorCode);
    }

    [Fact]
    public async Task 发出散箱_变成在路上_所属地点仍是发出方_发出后发出方不能再修改_标签发出地取发出时地点()
    {
        var service = CreateService();
        var carton = await SealedCartonAsync(service, Lily, Dest(PackDestinationTypes.Store, "1020"), ("HB23817", 24));

        var dispatched = await service.DispatchCartonAsync(Lily, carton.CartonGuid);
        var scan = await service.ScanIntoCartonAsync(Lily, carton.CartonGuid, Scan("HB23817"));
        var reopen = await service.ReopenCartonAsync(SunnyManager, carton.CartonGuid);
        var header = await service.UpdateCartonHeaderAsync(Lily, carton.CartonGuid, Header(PackDestinationTypes.Store, "1030"));
        var senderList = await service.ListCartonsAsync(Lily, null, "outgoing", null, false);

        Assert.True(dispatched.Success, dispatched.Message);
        Assert.Equal(PackCartonStatuses.InTransit, dispatched.Data!.Status);
        Assert.Equal("1013", dispatched.Data.OwnerCode);
        Assert.Equal("Lily", dispatched.Data.DispatchedByName);
        Assert.NotNull(dispatched.Data.DispatchedAtUtc);
        Assert.Equal(PackingErrorCodes.CartonInTransit, scan.ErrorCode);
        Assert.Equal(PackingErrorCodes.CartonInTransit, reopen.ErrorCode);
        Assert.Equal(PackingErrorCodes.CartonInTransit, header.ErrorCode);
        Assert.Equal(carton.CartonNo, Assert.Single(senderList.Data!.Items).CartonNo);
        Assert.Equal(1, senderList.Data.OutgoingCount);
        Assert.Equal(0, senderList.Data.StoredCount);
    }

    [Fact]
    public async Task 发出托盘_必须已封托且有去向_托盘上的箱一起在路上_去向写进每只箱()
    {
        var service = CreateService();
        var carrier = (await service.SaveCarrierAsync(Lily, null, new PackingCarrierSaveRequestDto { Name = "Border Express" })).Data!;
        var pallet = (await service.CreatePalletAsync(Lily, new PackingCreatePalletRequestDto())).Data!;
        var first = await SealedCartonAsync(service, Lily, ("HB23817", 24));
        var second = await SealedCartonAsync(service, Lily, ("HB24102", 6));
        await service.AddCartonToPalletAsync(Lily, pallet.PalletGuid, new PackingAddCartonRequestDto { Code = first.CartonNo });
        await service.AddCartonToPalletAsync(Lily, pallet.PalletGuid, new PackingAddCartonRequestDto { Code = second.CartonNo });

        var notSealed = await service.DispatchPalletAsync(Lily, pallet.PalletGuid);
        await service.SealPalletAsync(Lily, pallet.PalletGuid, new PackingSealRequestDto());
        var noDestination = await service.DispatchPalletAsync(Lily, pallet.PalletGuid);
        await service.UpdatePalletHeaderAsync(Lily, pallet.PalletGuid, new PackingPalletHeaderRequestDto
        {
            DestinationType = PackDestinationTypes.Store,
            DestinationCode = "1020",
            CarrierGuid = carrier.CarrierGuid,
            ConsignmentNo = " BEX 4471 0932 18 ",
        });
        var dispatched = await service.DispatchPalletAsync(Lily, pallet.PalletGuid);
        var cartons = await _db.Queryable<PackCarton>().Where(carton => carton.PalletGUID == pallet.PalletGuid).ToListAsync();

        Assert.Equal(PackingErrorCodes.PalletNotSealed, notSealed.ErrorCode);
        Assert.Equal(PackingErrorCodes.NotShippable, noDestination.ErrorCode);
        Assert.True(dispatched.Success, dispatched.Message);
        Assert.Equal(PackPalletStatuses.InTransit, dispatched.Data!.Status);
        Assert.Equal("BEX 4471 0932 18", dispatched.Data.ConsignmentNo);
        Assert.Equal(2, dispatched.Data.InTransitCartonCount);
        Assert.All(cartons, carton =>
        {
            Assert.Equal(PackCartonStatuses.InTransit, carton.Status);
            Assert.Equal("1020", carton.DestinationCode);
            Assert.Equal(carrier.CarrierGuid, carton.CarrierGUID);
            Assert.Equal("1013", carton.OwnerCode);
        });
        var edit = await service.UpdatePalletHeaderAsync(Lily, pallet.PalletGuid, new PackingPalletHeaderRequestDto());
        Assert.Equal(PackingErrorCodes.PalletInTransit, edit.ErrorCode);
    }

    // ---- 签收 ----

    [Fact]
    public async Task 签收散箱_只有收货方可以签_第三方店看不到在路上的箱_签收后所属地点换成接收方并记位置()
    {
        var service = CreateService();
        await CreateLocationAsync(service, "后仓 A 架", InalaManager);
        var carton = await SealedCartonAsync(service, Lily, Dest(PackDestinationTypes.Store, "1020"), ("HB23817", 24));
        await service.DispatchCartonAsync(Lily, carton.CartonGuid);

        var incoming = await service.ListIncomingAsync(Ian, null);
        var sender = await service.ReceiveCartonAsync(Lily, carton.CartonGuid, new PackingReceiveCartonRequestDto());
        var stranger = await service.GetCartonAsync(Pete, carton.CartonGuid);
        var strangerReceive = await service.ReceiveCartonAsync(Pete, carton.CartonGuid, new PackingReceiveCartonRequestDto());
        var badLocation = await service.ReceiveCartonAsync(Ian, carton.CartonGuid, new PackingReceiveCartonRequestDto { LocationCode = "不存在的位置" });
        var received = await service.ReceiveCartonAsync(Ian, carton.CartonGuid, new PackingReceiveCartonRequestDto { LocationCode = "后仓 A 架" });
        var again = await service.ReceiveCartonAsync(Ian, carton.CartonGuid, new PackingReceiveCartonRequestDto());

        var pending = Assert.Single(incoming.Data!.Pending);
        Assert.Equal("carton", pending.Kind);
        Assert.Equal(carton.CartonNo, pending.No);
        Assert.Equal("Sunnybank", pending.OriginName);
        Assert.Equal(24, pending.PieceCount);
        Assert.Equal(PackingErrorCodes.NotForYou, sender.ErrorCode);
        Assert.Equal(404, stranger.StatusCode);
        Assert.Equal(404, strangerReceive.StatusCode);
        Assert.Equal(PackingErrorCodes.LocationNotFound, badLocation.ErrorCode);
        Assert.True(received.Success, received.Message);
        Assert.Equal("1020", received.Data!.OwnerCode);
        Assert.Equal(PackCartonStatuses.Sealed, received.Data.Status);
        Assert.Equal("后仓 A 架", received.Data.LocationCode);
        Assert.Equal("Ian", received.Data.ReceivedByName);
        Assert.Equal(PackDestinationTypes.Local, received.Data.Destination.DestinationType);
        Assert.Equal("Sunnybank", received.Data.OriginName);
        Assert.Equal(PackingErrorCodes.NotInTransit, again.ErrorCode);

        // 签收后：接收方「本地存放」里有它，待签收清空；发出方在「已发出」里看到对方已签收。
        var inalaList = await service.ListCartonsAsync(Ian, null, "stored", null, false);
        var inalaIncoming = await service.ListIncomingAsync(Ian, null);
        var sunnyOutgoing = await service.ListCartonsAsync(Lily, null, "outgoing", null, false);
        Assert.Equal(carton.CartonNo, Assert.Single(inalaList.Data!.Items).CartonNo);
        Assert.Empty(inalaIncoming.Data!.Pending);
        Assert.Equal(carton.CartonNo, Assert.Single(inalaIncoming.Data.Recent).No);
        Assert.Equal("1020", Assert.Single(sunnyOutgoing.Data!.Items).OwnerCode);
        Assert.Equal(0, (await service.ListCartonsAsync(Lily, null, "stored", null, false)).Data!.StoredCount);
    }

    [Fact]
    public async Task 整托签收_必须先数箱_数量对不上返回409且一只箱都不签_数对后全部签收并换所属地点()
    {
        var service = CreateService();
        var pallet = await DispatchedPalletAsync(service, 3);

        var missingCount = await service.ReceivePalletAsync(Ian, pallet.PalletGuid, new PackingReceivePalletRequestDto());
        var wrongCount = await service.ReceivePalletAsync(Ian, pallet.PalletGuid, new PackingReceivePalletRequestDto { ExpectedCartonCount = 2 });
        var sender = await service.ReceivePalletAsync(Lily, pallet.PalletGuid, new PackingReceivePalletRequestDto { ExpectedCartonCount = 3 });
        var stillInTransit = await _db.Queryable<PackCarton>().Where(carton => carton.PalletGUID == pallet.PalletGuid && carton.Status == PackCartonStatuses.InTransit).CountAsync();
        var received = await service.ReceivePalletAsync(Ian, pallet.PalletGuid, new PackingReceivePalletRequestDto { ExpectedCartonCount = 3 });

        Assert.Equal(PackingErrorCodes.CountMismatch, missingCount.ErrorCode);
        Assert.Equal(PackingErrorCodes.CountMismatch, wrongCount.ErrorCode);
        Assert.Equal(409, wrongCount.StatusCode);
        Assert.Equal(3, wrongCount.Data!.ActualInTransitCartonCount);
        Assert.Equal(PackingErrorCodes.NotForYou, sender.ErrorCode);
        Assert.Equal(3, stillInTransit);
        Assert.True(received.Success, received.Message);
        Assert.Equal("1020", received.Data!.OwnerCode);
        Assert.Equal(PackPalletStatuses.Sealed, received.Data.Status);
        Assert.Equal(0, received.Data.InTransitCartonCount);
        Assert.All(received.Data.Cartons, carton => Assert.Equal("1020", carton.OwnerCode));
        Assert.Equal(3, (await service.ListCartonsAsync(Ian, null, "stored", null, false)).Data!.StoredCount);
        Assert.Equal(1, (await service.ListPalletsAsync(Ian, null, "stored", null)).Data!.StoredCount);
    }

    [Fact]
    public async Task 托盘标签丢了_扫托盘上任意一只箱的标签找到整个托盘_整托签收时记录通过箱标签()
    {
        var service = CreateService();
        var pallet = await DispatchedPalletAsync(service, 3);
        var scannedCarton = pallet.Cartons[1];

        var lookup = await service.LookupForReceiveAsync(Ian, scannedCarton.CartonNo.ToLowerInvariant());
        var received = await service.ReceivePalletAsync(
            Ian,
            lookup.Data!.Pallet!.PalletGuid,
            new PackingReceivePalletRequestDto { ExpectedCartonCount = 3, ViaCartonNo = scannedCarton.CartonNo }
        );
        var events = await service.ListEventsAsync(Ian, PackTargetTypes.Pallet, pallet.PalletGuid);

        Assert.Equal("pallet", lookup.Data.Kind);
        Assert.True(lookup.Data.ViaCartonLabel);
        Assert.Equal(scannedCarton.CartonNo, lookup.Data.ScannedCartonNo);
        Assert.Equal(3, lookup.Data.InTransitCartonCount);
        Assert.True(lookup.Data.CanReceive);
        Assert.Equal(pallet.PalletNo, lookup.Data.Pallet.PalletNo);
        Assert.True(received.Success, received.Message);
        Assert.Contains(events.Data!, item => item.Action == PackEventActions.Received && item.Detail!.Contains("通过箱标签", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 扫码签收查找_扫托盘标签_托盘不是发给你或已签收时不能签_散箱返回箱本身()
    {
        var service = CreateService();
        var pallet = await DispatchedPalletAsync(service, 2);
        var loose = await SealedCartonAsync(service, Lily, Dest(PackDestinationTypes.Store, "1020"), ("HB23817", 5));
        await service.DispatchCartonAsync(Lily, loose.CartonGuid);

        var byPallet = await service.LookupForReceiveAsync(Ian, pallet.PalletNo);
        var looseLookup = await service.LookupForReceiveAsync(Ian, loose.CartonNo);
        var senderLookup = await service.LookupForReceiveAsync(Lily, pallet.PalletNo);
        var stranger = await service.LookupForReceiveAsync(Pete, pallet.PalletNo);
        var unknown = await service.LookupForReceiveAsync(Ian, "NOPE");
        await service.ReceivePalletAsync(Ian, pallet.PalletGuid, new PackingReceivePalletRequestDto { ExpectedCartonCount = 2 });
        var afterReceive = await service.LookupForReceiveAsync(Ian, pallet.PalletNo);

        Assert.Equal("pallet", byPallet.Data!.Kind);
        Assert.False(byPallet.Data.ViaCartonLabel);
        Assert.True(byPallet.Data.CanReceive);
        Assert.Equal("carton", looseLookup.Data!.Kind);
        Assert.True(looseLookup.Data.CanReceive);
        Assert.False(senderLookup.Data!.CanReceive);
        Assert.Equal(PackingErrorCodes.NotForYou, senderLookup.Data.BlockReason);
        Assert.Equal(404, stranger.StatusCode);
        Assert.Equal(404, unknown.StatusCode);
        Assert.False(afterReceive.Data!.CanReceive);
        Assert.Equal(PackingErrorCodes.NotInTransit, afterReceive.Data.BlockReason);
    }

    [Fact]
    public async Task 逐箱签收_部分签收后托盘仍在路上显示已签收箱数_最后一只签完托盘才换所属地点()
    {
        var service = CreateService();
        var pallet = await DispatchedPalletAsync(service, 3);
        var guids = pallet.Cartons.Select(carton => carton.CartonGuid).ToList();

        var partial = await service.ReceivePalletAsync(Ian, pallet.PalletGuid, new PackingReceivePalletRequestDto { CartonGuids = new List<string> { guids[0] } });
        var second = await service.ReceiveCartonAsync(Ian, guids[1], new PackingReceiveCartonRequestDto());
        var unknownCarton = await service.ReceivePalletAsync(Ian, pallet.PalletGuid, new PackingReceivePalletRequestDto { CartonGuids = new List<string> { "not-on-pallet" } });
        var midway = (await service.ListIncomingAsync(Ian, null)).Data!;
        var last = await service.ReceivePalletAsync(Ian, pallet.PalletGuid, new PackingReceivePalletRequestDto { CartonGuids = new List<string> { guids[2] } });

        Assert.True(partial.Success, partial.Message);
        Assert.Equal(PackPalletStatuses.InTransit, partial.Data!.Status);
        Assert.Equal("1013", partial.Data.OwnerCode);
        Assert.Equal(2, partial.Data.InTransitCartonCount);
        Assert.True(second.Success, second.Message);
        Assert.Equal("1020", second.Data!.OwnerCode);
        Assert.Equal(PackingErrorCodes.CartonNotFound, unknownCarton.ErrorCode);
        var pending = Assert.Single(midway.Pending);
        Assert.Equal("pallet", pending.Kind);
        Assert.Equal(3, pending.CartonCount);
        Assert.Equal(2, pending.ReceivedCartonCount);
        Assert.True(last.Success, last.Message);
        Assert.Equal(PackPalletStatuses.Sealed, last.Data!.Status);
        Assert.Equal("1020", last.Data.OwnerCode);
        Assert.Empty((await service.ListIncomingAsync(Ian, null)).Data!.Pending);
    }

    [Fact]
    public async Task 接收方可以再转发_标签上的发出地变成转发方()
    {
        var service = CreateService();
        var carton = await SealedCartonAsync(service, Lily, Dest(PackDestinationTypes.Store, "1020"), ("HB23817", 24));
        await service.DispatchCartonAsync(Lily, carton.CartonGuid);
        await service.ReceiveCartonAsync(Ian, carton.CartonGuid, new PackingReceiveCartonRequestDto());

        var header = await service.UpdateCartonHeaderAsync(Ian, carton.CartonGuid, Header(PackDestinationTypes.Store, "1030"));
        var dispatched = await service.DispatchCartonAsync(Ian, carton.CartonGuid);
        var received = await service.ReceiveCartonAsync(Pete, carton.CartonGuid, new PackingReceiveCartonRequestDto());

        Assert.True(header.Success, header.Message);
        Assert.Equal("Inala", dispatched.Data!.OriginName);
        Assert.Equal("1020", dispatched.Data.OriginCode);
        Assert.Equal("1030", received.Data!.OwnerCode);
        Assert.Equal("Inala", received.Data.OriginName);
    }

    [Fact]
    public async Task 分店发往总仓_仓库账号签收_总仓发往分店_分店签收()
    {
        var service = CreateService();
        var toWarehouse = await SealedCartonAsync(service, Lily, Dest(PackDestinationTypes.Warehouse, null), ("HB23817", 12));
        await service.DispatchCartonAsync(Lily, toWarehouse.CartonGuid);
        var fromWarehouse = await SealedCartonAsync(service, Wendy, Dest(PackDestinationTypes.Store, "1013"), ("HB24102", 6));
        await service.DispatchCartonAsync(Wendy, fromWarehouse.CartonGuid);

        var warehouseIncoming = await service.ListIncomingAsync(Wendy, null);
        var received = await service.ReceiveCartonAsync(Wendy, toWarehouse.CartonGuid, new PackingReceiveCartonRequestDto { LocationCode = "A-03" });
        var storeIncoming = await service.ListIncomingAsync(Lily, null);
        var storeReceived = await service.ReceiveCartonAsync(Lily, fromWarehouse.CartonGuid, new PackingReceiveCartonRequestDto());

        Assert.Equal(toWarehouse.CartonNo, Assert.Single(warehouseIncoming.Data!.Pending).No);
        Assert.Equal(PackOwners.Warehouse, received.Data!.OwnerCode);
        // 总仓的位置用现有货位表（货位编码）。
        Assert.Equal("A-03", received.Data.LocationCode);
        Assert.Equal("HB TEST WAREHOUSE", storeReceived.Data!.OriginName);
        Assert.Equal(fromWarehouse.CartonNo, Assert.Single(storeIncoming.Data!.Pending).No);
        Assert.Equal("1013", storeReceived.Data.OwnerCode);
    }

    [Fact]
    public async Task 待签收计数_首页显示发给本店还没签收的托盘和散箱批数()
    {
        var service = CreateService();
        await DispatchedPalletAsync(service, 2);
        var loose = await SealedCartonAsync(service, Lily, Dest(PackDestinationTypes.Store, "1020"), ("HB23817", 5));
        await service.DispatchCartonAsync(Lily, loose.CartonGuid);
        var toPerth = await SealedCartonAsync(service, Lily, Dest(PackDestinationTypes.Store, "1030"), ("HB23817", 5));
        await service.DispatchCartonAsync(Lily, toPerth.CartonGuid);

        var inala = await service.ListCartonsAsync(Ian, null, "all", null, false);
        var perth = await service.ListCartonsAsync(Pete, null, "all", null, false);
        var sender = await service.ListCartonsAsync(Lily, null, "all", null, false);

        Assert.Equal(2, inala.Data!.IncomingCount);
        Assert.Equal(1, perth.Data!.IncomingCount);
        Assert.Equal(0, sender.Data!.IncomingCount);
    }

    // ---- 位置查询 ----

    [Fact]
    public async Task 位置查询_按本店存放_在路上_对方已签收分组_店员看不到其他分店_店长可以跨店查()
    {
        var service = CreateService();
        await CreateLocationAsync(service, "后仓 A 架");
        var stored = await SealedCartonAsync(service, Lily, ("HB23817", 48), ("HB24102", 6));
        await service.SetCartonLocationAsync(Lily, stored.CartonGuid, "后仓 A 架");
        var transit = await SealedCartonAsync(service, Lily, Dest(PackDestinationTypes.Store, "1030"), ("HB23817", 24));
        await service.DispatchCartonAsync(Lily, transit.CartonGuid);
        var gone = await SealedCartonAsync(service, Lily, Dest(PackDestinationTypes.Store, "1020"), ("HB23817", 12));
        await service.DispatchCartonAsync(Lily, gone.CartonGuid);
        await service.ReceiveCartonAsync(Ian, gone.CartonGuid, new PackingReceiveCartonRequestDto());
        var inalaOwn = await SealedCartonAsync(service, Ian, ("HB23817", 100));
        var voided = await CartonWithAsync(service, Lily, ("HB23817", 99));
        await service.VoidCartonAsync(Lily, voided.CartonGuid);

        var staff = await service.LocateProductAsync(Lily, null, "9345678123457", false);
        var allByStaff = await service.LocateProductAsync(Lily, null, "9345678123457", true);
        var allByManager = await service.LocateProductAsync(SunnyManager, null, "9345678123457", true);
        var receiver = await service.LocateProductAsync(Pete, null, "HB23817", false);
        var missing = await service.LocateProductAsync(Lily, null, "NOPE", false);

        Assert.True(staff.Success, staff.Message);
        Assert.Equal("HB23817", staff.Data!.Product.ItemNumber);
        Assert.Equal(new[] { "stored", "transit", "elsewhere" }, staff.Data.Rows.Select(row => row.Group));
        Assert.Equal(new[] { 48, 24, 12 }, staff.Data.Rows.Select(row => row.Quantity));
        Assert.Equal("后仓 A 架", staff.Data.Rows[0].LocationCode);
        Assert.Equal("Inala", staff.Data.Rows[2].OwnerName);
        Assert.Equal(84, staff.Data.PackedQuantity);
        // 分店不显示仓库库存对照。
        Assert.Null(staff.Data.StockQuantity);
        Assert.Equal(403, allByStaff.StatusCode);
        Assert.Contains(allByManager.Data!.Rows, row => row.CartonNo == inalaOwn.CartonNo && row.Group == "other");
        Assert.DoesNotContain(allByManager.Data.Rows, row => row.CartonNo == voided.CartonNo);
        // 发往珀斯、在路上的箱，珀斯也能看到（待签收）。
        Assert.Equal(transit.CartonNo, Assert.Single(receiver.Data!.Rows).CartonNo);
        Assert.Equal("transit", receiver.Data.Rows[0].Group);
        Assert.Equal(404, missing.StatusCode);
    }

    [Fact]
    public async Task 位置查询_总仓带出仓库库存对照_托盘上的箱取托盘位置()
    {
        var service = CreateService();
        var pallet = (await service.CreatePalletAsync(Wendy, new PackingCreatePalletRequestDto())).Data!;
        var carton = await SealedCartonAsync(service, Wendy, ("HB23817", 48));
        await service.AddCartonToPalletAsync(Wendy, pallet.PalletGuid, new PackingAddCartonRequestDto { Code = carton.CartonNo });
        await service.SetPalletLocationAsync(Wendy, pallet.PalletGuid, "2000000000031");

        var located = await service.LocateProductAsync(Wendy, null, "9345678123457", false);

        var row = Assert.Single(located.Data!.Rows);
        Assert.Equal("A-03", row.LocationCode);
        Assert.Equal(pallet.PalletNo, row.PalletNo);
        Assert.Equal(340, located.Data.StockQuantity);
        Assert.Equal(48, located.Data.PackingQuantity);
    }

    // ---- 存放位置、运输公司、识别 ----

    [Fact]
    public async Task 存放位置_只有店长可以维护_同店名称唯一_位置条码可用于选择_别的分店的位置不能用()
    {
        var service = CreateService();

        var byStaff = await service.SaveLocationAsync(Lily, null, new PackingLocationSaveRequestDto { Name = "后仓 A 架" });
        var created = await service.SaveLocationAsync(SunnyManager, null, new PackingLocationSaveRequestDto { Name = "  后仓 A 架 " });
        var duplicate = await service.SaveLocationAsync(SunnyManager, null, new PackingLocationSaveRequestDto { Name = "后仓 A 架" });
        var second = await service.SaveLocationAsync(SunnyManager, null, new PackingLocationSaveRequestDto { Name = "阁楼" });
        var inalaSame = await service.SaveLocationAsync(InalaManager, null, new PackingLocationSaveRequestDto { Name = "后仓 A 架" });
        var warehouse = await service.SaveLocationAsync(Admin, null, new PackingLocationSaveRequestDto { OwnerCode = PackOwners.Warehouse, Name = "X" });

        Assert.Equal(403, byStaff.StatusCode);
        Assert.Equal("后仓 A 架", created.Data!.Name);
        Assert.Equal("LC000001", created.Data.Barcode);
        Assert.Equal(PackingErrorCodes.LocationNameTaken, duplicate.ErrorCode);
        Assert.Equal("LC000002", second.Data!.Barcode);
        // 不同分店可以有同名位置，条码仍然全局唯一。
        Assert.True(inalaSame.Success, inalaSame.Message);
        Assert.Equal("LC000003", inalaSame.Data!.Barcode);
        Assert.Equal(PackingErrorCodes.InvalidRequest, warehouse.ErrorCode);

        var carton = await SealedCartonAsync(service, Lily, ("HB23817", 24));
        var byBarcode = await service.SetCartonLocationAsync(Lily, carton.CartonGuid, "LC000002");
        var otherStoreBarcode = await service.SetCartonLocationAsync(Lily, carton.CartonGuid, "LC000003");
        var list = await service.ListLocationsAsync(Lily, null, false);
        var warehouseList = await service.ListLocationsAsync(Wendy, null, false);

        Assert.Equal("阁楼", byBarcode.Data!.LocationCode);
        Assert.Equal(PackingErrorCodes.LocationNotFound, otherStoreBarcode.ErrorCode);
        Assert.Equal(new[] { "后仓 A 架", "阁楼" }.OrderBy(name => name, StringComparer.Ordinal), list.Data!.Select(location => location.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Contains(warehouseList.Data!, location => location.Name == "A-03" && location.Barcode == "2000000000031");
    }

    [Fact]
    public async Task 存放位置_停用后不能再选_改名后条码不变()
    {
        var service = CreateService();
        var created = (await service.SaveLocationAsync(SunnyManager, null, new PackingLocationSaveRequestDto { Name = "后仓 A 架" })).Data!;
        var carton = await SealedCartonAsync(service, Lily, ("HB23817", 24));

        var renamed = await service.SaveLocationAsync(SunnyManager, created.LocationGuid, new PackingLocationSaveRequestDto { Name = "后仓 B 架" });
        var staffEdit = await service.SaveLocationAsync(Lily, created.LocationGuid, new PackingLocationSaveRequestDto { Name = "x" });
        var inalaEdit = await service.SaveLocationAsync(InalaManager, created.LocationGuid, new PackingLocationSaveRequestDto { Name = "x" });
        await service.SaveLocationAsync(SunnyManager, created.LocationGuid, new PackingLocationSaveRequestDto { Name = "后仓 B 架", IsActive = false });
        var afterDisable = await service.SetCartonLocationAsync(Lily, carton.CartonGuid, "后仓 B 架");
        var activeList = await service.ListLocationsAsync(Lily, null, false);
        var allList = await service.ListLocationsAsync(SunnyManager, null, true);

        Assert.Equal(created.Barcode, renamed.Data!.Barcode);
        Assert.Equal(403, staffEdit.StatusCode);
        Assert.Equal(404, inalaEdit.StatusCode);
        Assert.Equal(PackingErrorCodes.LocationNotFound, afterDisable.ErrorCode);
        Assert.Empty(activeList.Data!);
        Assert.False(Assert.Single(allList.Data!).IsActive);
    }

    [Fact]
    public async Task 货位_不存在的位置拒绝_传空清除()
    {
        var service = CreateService();
        await CreateLocationAsync(service, "后仓 A 架");
        var carton = await SealedCartonAsync(service, Lily, ("HB23817", 24));

        var unknown = await service.SetCartonLocationAsync(Lily, carton.CartonGuid, "Z-99");
        var set = await service.SetCartonLocationAsync(Lily, carton.CartonGuid, "后仓 A 架");
        var cleared = await service.SetCartonLocationAsync(Lily, carton.CartonGuid, " ");

        Assert.Equal(PackingErrorCodes.LocationNotFound, unknown.ErrorCode);
        Assert.Equal("后仓 A 架", set.Data!.LocationCode);
        Assert.Null(cleared.Data!.LocationCode);
    }

    [Fact]
    public async Task 运输公司_任何人可以现场新增_只有管理员可以改名停用_名称不能重复_停用后默认列表不再出现()
    {
        var service = CreateService();
        var toll = (await service.SaveCarrierAsync(Lily, null, new PackingCarrierSaveRequestDto { Name = "Toll" })).Data!;

        var duplicate = await service.SaveCarrierAsync(Lily, null, new PackingCarrierSaveRequestDto { Name = "Toll" });
        var blank = await service.SaveCarrierAsync(Lily, null, new PackingCarrierSaveRequestDto { Name = "  " });
        var staffEdit = await service.SaveCarrierAsync(SunnyManager, toll.CarrierGuid, new PackingCarrierSaveRequestDto { Name = "Toll", IsActive = false });
        var adminEdit = await service.SaveCarrierAsync(Admin, toll.CarrierGuid, new PackingCarrierSaveRequestDto { Name = "Toll", IsActive = false });
        var active = await service.ListCarriersAsync(includeInactive: false);
        var all = await service.ListCarriersAsync(includeInactive: true);

        Assert.Equal(PackingErrorCodes.CarrierNameTaken, duplicate.ErrorCode);
        Assert.Equal(PackingErrorCodes.InvalidRequest, blank.ErrorCode);
        Assert.Equal(403, staffEdit.StatusCode);
        Assert.True(adminEdit.Success, adminEdit.Message);
        Assert.Empty(active.Data!);
        Assert.False(Assert.Single(all.Data!).IsActive);
    }

    [Fact]
    public async Task 识别扫到的码_箱号和托盘号按前缀分开_商品码返回404交回客户端()
    {
        var service = CreateService();
        var carton = (await service.CreateCartonAsync(Lily, new PackingCreateCartonRequestDto())).Data!;
        var pallet = (await service.CreatePalletAsync(Lily, new PackingCreatePalletRequestDto())).Data!;

        var cartonResolved = await service.ResolveCodeAsync(Lily, $" {carton.CartonNo.ToLowerInvariant()} ");
        var palletResolved = await service.ResolveCodeAsync(Lily, pallet.PalletNo);
        var product = await service.ResolveCodeAsync(Lily, "9345678123457");
        var unknownCarton = await service.ResolveCodeAsync(Lily, "BX0000000000");

        Assert.Equal("carton", cartonResolved.Data!.Kind);
        Assert.Equal(carton.CartonGuid, cartonResolved.Data.Guid);
        Assert.Equal("pallet", palletResolved.Data!.Kind);
        Assert.Equal(pallet.PalletGuid, palletResolved.Data.Guid);
        Assert.Equal(404, product.StatusCode);
        Assert.Equal(404, unknownCarton.StatusCode);
    }

    [Fact]
    public async Task 总仓按订货单装箱_订货单只对总仓开放_箱明细带出配货数量与其他箱已装数量()
    {
        var service = CreateService();
        var storeWithOrder = await service.CreateCartonAsync(Lily, new PackingCreateCartonRequestDto { OrderGuid = "order-1" });
        var other = (await service.CreateCartonAsync(Wendy, new PackingCreateCartonRequestDto { OrderGuid = "order-1" })).Data!;
        await service.ScanIntoCartonAsync(Wendy, other.CartonGuid, Scan("HB23817", 12));
        var carton = (await service.CreateCartonAsync(Wendy, new PackingCreateCartonRequestDto { OrderGuid = "order-1" })).Data!;
        await service.ScanIntoCartonAsync(Wendy, carton.CartonGuid, Scan("HB23817", 24));
        var detail = (await service.ScanIntoCartonAsync(Wendy, carton.CartonGuid, Scan("HB24102", 6))).Data!;
        var warehouseOrders = await service.SearchOrdersAsync(Wendy, "2026-04");
        var storeOrders = await service.SearchOrdersAsync(Lily, "2026-04");

        Assert.Equal(PackingErrorCodes.InvalidRequest, storeWithOrder.ErrorCode);
        // 默认去向是订货单的分店。
        Assert.Equal("Sunnybank", carton.Destination.Name);
        var mug = detail.Items.Single(item => item.ProductCode == "P-MUG");
        Assert.Equal(36m, mug.OrderQuantity);
        Assert.Equal(12, mug.PackedInOtherCartons);
        Assert.Equal(24m, detail.Items.Single(item => item.ProductCode == "P-CANDLE").OrderQuantity);
        Assert.Equal("order-1", Assert.Single(warehouseOrders.Data!).OrderGuid);
        Assert.Empty(storeOrders.Data!);
    }

    [Fact]
    public async Task 范围接口前提_账号只有一个地点时创建托盘不用指定_管理员的跨店托盘列表按所选分店区分()
    {
        var service = CreateService();
        var lilyPallet = (await service.CreatePalletAsync(Lily, new PackingCreatePalletRequestDto())).Data!;
        var inalaPallet = (await service.CreatePalletAsync(Admin, new PackingCreatePalletRequestDto { OwnerCode = "1020" })).Data!;

        var sunny = await service.ListPalletsAsync(Admin, "1013", "all", null);
        var inala = await service.ListPalletsAsync(Admin, "1020", "all", null);
        var strangerGet = await service.GetPalletAsync(Pete, lilyPallet.PalletGuid);

        Assert.Equal(lilyPallet.PalletNo, Assert.Single(sunny.Data!.Items).PalletNo);
        Assert.Equal(inalaPallet.PalletNo, Assert.Single(inala.Data!.Items).PalletNo);
        Assert.Equal(1, sunny.Data.BuildingCount);
        Assert.Equal(404, strangerGet.StatusCode);
    }

    // ---- 测试辅助 ----

    private static PackingCreateCartonRequestDto Dest(int type, string? code, string? carrierGuid = null) => new()
    {
        DestinationType = type,
        DestinationCode = code,
        CarrierGuid = carrierGuid,
    };

    private static PackingCreatePalletRequestDto PalletDest(int type, string? code, string? carrierGuid = null) => new()
    {
        DestinationType = type,
        DestinationCode = code,
        CarrierGuid = carrierGuid,
    };

    private static PackingCartonHeaderRequestDto Header(int type, string? code, string? carrierGuid = null) => new()
    {
        DestinationType = type,
        DestinationCode = code,
        CarrierGuid = carrierGuid,
    };

    private static PackingScanRequestDto Scan(string code, int? quantity = null) => new() { Code = code, Quantity = quantity };

    private async Task CreateLocationAsync(IPackingService service, string name, PackingOperator? manager = null)
    {
        var result = await service.SaveLocationAsync(manager ?? SunnyManager, null, new PackingLocationSaveRequestDto { Name = name });
        Assert.True(result.Success, result.Message);
    }

    private Task<PackingCartonDetailDto> CartonWithAsync(IPackingService service, PackingOperator op, params (string Code, int Quantity)[] items) =>
        CartonWithAsync(service, op, new PackingCreateCartonRequestDto(), items);

    private Task<PackingCartonDetailDto> CartonWithAsync(IPackingService service, PackingOperator op, PackingCreateCartonRequestDto request, params (string Code, int Quantity)[] items) =>
        BuildCartonAsync(service, op, request, items);

    private async Task<PackingCartonDetailDto> BuildCartonAsync(IPackingService service, PackingOperator op, PackingCreateCartonRequestDto request, (string Code, int Quantity)[] items)
    {
        var carton = (await service.CreateCartonAsync(op, request)).Data!;
        foreach (var (code, quantity) in items)
        {
            var scan = await service.ScanIntoCartonAsync(op, carton.CartonGuid, Scan(code, quantity));
            Assert.True(scan.Success, scan.Message);
            carton = scan.Data!;
        }

        return carton;
    }

    private Task<PackingCartonDetailDto> SealedCartonAsync(IPackingService service, PackingOperator op, params (string Code, int Quantity)[] items) =>
        SealedCartonAsync(service, op, new PackingCreateCartonRequestDto(), items);

    private async Task<PackingCartonDetailDto> SealedCartonAsync(IPackingService service, PackingOperator op, PackingCreateCartonRequestDto request, params (string Code, int Quantity)[] items)
    {
        var carton = await BuildCartonAsync(service, op, request, items);
        var sealedResult = await service.SealCartonAsync(op, carton.CartonGuid, new PackingSealRequestDto());
        Assert.True(sealedResult.Success, sealedResult.Message);
        return sealedResult.Data!;
    }

    /// <summary>Sunnybank 发往 Inala 的一个托盘：封好 cartonCount 只箱、封托、发出，返回发出后的托盘详情。</summary>
    private async Task<PackingPalletDetailDto> DispatchedPalletAsync(IPackingService service, int cartonCount)
    {
        var pallet = (await service.CreatePalletAsync(Lily, PalletDest(PackDestinationTypes.Store, "1020"))).Data!;
        for (var index = 0; index < cartonCount; index++)
        {
            var carton = await SealedCartonAsync(service, Lily, ("HB23817", 10 + index));
            var added = await service.AddCartonToPalletAsync(Lily, pallet.PalletGuid, new PackingAddCartonRequestDto { Code = carton.CartonNo });
            Assert.True(added.Success, added.Message);
        }

        var sealedResult = await service.SealPalletAsync(Lily, pallet.PalletGuid, new PackingSealRequestDto());
        Assert.True(sealedResult.Success, sealedResult.Message);
        var dispatched = await service.DispatchPalletAsync(Lily, pallet.PalletGuid);
        Assert.True(dispatched.Success, dispatched.Message);
        return dispatched.Data!;
    }

    private void SeedMasterData()
    {
        _db.Insertable(new List<Store>
        {
            new() { StoreGUID = "store-1013", StoreCode = "1013", StoreName = "Sunnybank", TimeZoneId = "Australia/Brisbane" },
            new() { StoreGUID = "store-1020", StoreCode = "1020", StoreName = "Inala", TimeZoneId = "Australia/Brisbane" },
            new() { StoreGUID = "store-1030", StoreCode = "1030", StoreName = "Perth", TimeZoneId = "Australia/Perth" },
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
        _db.Insertable(new List<StoreMultiCodeProduct>
        {
            new() { StoreCode = "1013", ProductCode = "P-MUG", MultiBarcode = "MULTI-MUG" },
            new() { StoreCode = "1020", ProductCode = "P-MUG", MultiBarcode = "INALA-ONLY-MULTI" },
        }).ExecuteCommand();
        _db.Insertable(new List<Location>
        {
            new() { LocationGuid = "loc-a03", LocationType = 2, LocationCode = "A-03", LocationBarcode = "2000000000031", Status = 1 },
            new() { LocationGuid = "loc-b12", LocationType = 2, LocationCode = "B-12", LocationBarcode = "2000000000123", Status = 1 },
        }).ExecuteCommand();
        _db.Insertable(new WareHouseOrder { OrderGUID = "order-1", OrderNo = "2026-0412", StoreCode = "1013", FlowStatus = 3, OrderDate = new DateTime(2026, 11, 12, 9, 0, 0) }).ExecuteCommand();
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

    private IPackingService CreateService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Packing:WarehouseName"] = "HB TEST WAREHOUSE",
                ["Packing:TimeZoneId"] = "Australia/Brisbane",
            })
            .Build();
        return new PackingService(CreateSqlSugarContext(_db), configuration, NullLogger<PackingService>.Instance, _time);
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
