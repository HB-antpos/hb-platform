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
/// 扫码打包「空箱标」：批量预先创建、预先打印标签的箱。
/// 覆盖批量创建（连续箱号 / 批内序号 / 去向 / 幂等重放 / 张数越界 / 300 上限 / 数据范围）、回报已打印、启用、作废未用空箱标、
/// 装入商品自动启用、箱列表（filter=blank 与其它筛选排除空箱标、blankCount / packingCount）、批次列表与详情。
/// 拣货端关联（attach）与提交对账的用例在 WarehousePickingPackingTests。
/// </summary>
public sealed class PackBlankCartonTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 11, 13, 4, 30, 0, TimeSpan.Zero));

    private static readonly PackingOwner Sunnybank = new("1013", "Sunnybank", false);
    private static readonly PackingOwner Inala = new("1020", "Inala", false);
    private static readonly PackingOwner Warehouse = new(PackOwners.Warehouse, "HB TEST WAREHOUSE", true);

    private static readonly PackingOperator Lily = new("u-lily", "Lily", false, false, new[] { Sunnybank });
    private static readonly PackingOperator Tom = new("u-tom", "Tom", false, false, new[] { Sunnybank });
    private static readonly PackingOperator Ian = new("u-ian", "Ian", false, false, new[] { Inala });
    private static readonly PackingOperator Wendy = new("u-wendy", "Wendy", false, false, new[] { Warehouse });
    private static readonly PackingOperator Admin = new("u-admin", "Admin", true, true, new[] { Sunnybank, Inala, Warehouse });

    public PackBlankCartonTests()
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
            typeof(Store)
        );
        _db.CodeFirst.InitTables(PickingPackingTestSupport.PackTables);
        SeedMasterData();
    }

    // ---- 批量创建 ----

    [Fact]
    public async Task 批量创建_箱号连续_批内序号从1起_未指定去向_不带订货单_写Created事件()
    {
        var result = await CreateService().CreateCartonBatchAsync(Lily, Batch(5));

        Assert.True(result.Success, result.Message);
        var data = result.Data!;
        Assert.False(data.Duplicate);
        Assert.Equal(5, data.Count);
        Assert.Equal("BX2611130001", data.FirstCartonNo);
        Assert.Equal("BX2611130005", data.LastCartonNo);
        Assert.Equal(new[] { "BX2611130001", "BX2611130002", "BX2611130003", "BX2611130004", "BX2611130005" }, data.Cartons!.Select(carton => carton.CartonNo));
        Assert.Equal(new int?[] { 1, 2, 3, 4, 5 }, data.Cartons!.Select(carton => carton.BatchSeq));
        Assert.All(data.Cartons!, carton =>
        {
            Assert.True(carton.IsBlank);
            Assert.Equal(data.BatchGuid, carton.BatchGuid);
            Assert.Equal(5, carton.BatchCount);
            Assert.Null(carton.ActivatedAtUtc);
            Assert.Equal(0, carton.LabelPrintCount);
            Assert.Equal(PackCartonStatuses.Packing, carton.Status);
            Assert.Equal("1013", carton.OwnerCode);
            Assert.Equal("Sunnybank", carton.OriginName);
            Assert.Equal(PackDestinationTypes.Unassigned, carton.Destination.DestinationType);
            Assert.Null(carton.OrderGuid);
            Assert.Equal("Lily", carton.CreatedByName);
        });

        var batch = await _db.Queryable<PackCartonBatch>().SingleAsync();
        Assert.Equal(data.BatchGuid, batch.BatchGUID);
        Assert.Equal(5, batch.Count);
        Assert.Equal("BX2611130001", batch.FirstCartonNo);
        Assert.Equal("BX2611130005", batch.LastCartonNo);
        Assert.Equal("1013", batch.OwnerCode);
        Assert.Equal("u-lily", batch.CreatedByUserGuid);
        var events = await _db.Queryable<PackEvent>().Where(item => item.Action == PackEventActions.Created).ToListAsync();
        Assert.Equal(5, events.Count);
        Assert.All(events, item => Assert.Equal("Lily", item.OperatorName));
    }

    [Fact]
    public async Task 批量创建_与普通建箱共用流水_号段不重叠_下一批接着往下()
    {
        var service = CreateService();
        var first = await service.CreateCartonBatchAsync(Lily, Batch(3));
        var normal = await service.CreateCartonAsync(Tom, new PackingCreateCartonRequestDto());
        var second = await service.CreateCartonBatchAsync(Lily, Batch(2));

        Assert.Equal("BX2611130003", first.Data!.LastCartonNo);
        Assert.Equal("BX2611130004", normal.Data!.CartonNo);
        Assert.Equal("BX2611130005", second.Data!.FirstCartonNo);
        Assert.Equal("BX2611130006", second.Data.LastCartonNo);
        // 普通箱没有批次信息。
        Assert.False(normal.Data.IsBlank);
        Assert.Null(normal.Data.BatchGuid);
        Assert.Equal(0, normal.Data.LabelPrintCount);
    }

    [Fact]
    public async Task 批量创建_可以指定去向_每只箱都带上_去向无效时整批不建且不占号()
    {
        var service = CreateService();
        var carrier = (await service.SaveCarrierAsync(Admin, null, new PackingCarrierSaveRequestDto { Name = "Border Express" })).Data!;

        var toStore = await service.CreateCartonBatchAsync(Lily, Batch(2, destinationType: PackDestinationTypes.Store, destinationCode: "1020", carrierGuid: carrier.CarrierGuid));
        Assert.True(toStore.Success, toStore.Message);
        Assert.All(toStore.Data!.Cartons!, carton =>
        {
            Assert.Equal(PackDestinationTypes.Store, carton.Destination.DestinationType);
            Assert.Equal("1020", carton.Destination.Code);
            Assert.Equal("Inala", carton.Destination.Name);
            Assert.Equal(carrier.CarrierGuid, carton.Destination.CarrierGuid);
        });
        var batchRow = await _db.Queryable<PackCartonBatch>().SingleAsync();
        Assert.Equal(PackDestinationTypes.Store, batchRow.DestinationType);
        Assert.Equal("1020", batchRow.DestinationCode);

        var self = await service.CreateCartonBatchAsync(Lily, Batch(2, destinationType: PackDestinationTypes.Store, destinationCode: "1013"));
        var unknown = await service.CreateCartonBatchAsync(Lily, Batch(2, destinationType: PackDestinationTypes.Store, destinationCode: "9999"));
        var warehouseToWarehouse = await service.CreateCartonBatchAsync(Wendy, Batch(2, destinationType: PackDestinationTypes.Warehouse));
        Assert.All(new[] { self, unknown, warehouseToWarehouse }, result =>
        {
            Assert.False(result.Success);
            Assert.Equal(400, result.StatusCode);
            Assert.Equal(PackingErrorCodes.DestinationInvalid, result.ErrorCode);
        });

        // 失败的批次什么都没写，也没有占号：下一只箱紧接第一批的 BX…0002。
        Assert.Equal(1, await _db.Queryable<PackCartonBatch>().CountAsync());
        var next = await service.CreateCartonAsync(Lily, new PackingCreateCartonRequestDto());
        Assert.Equal("BX2611130003", next.Data!.CartonNo);
    }

    [Fact]
    public async Task 批量创建_同一请求编号重放_返回已有批次_duplicate为true_不再建箱也不占号()
    {
        var service = CreateService();
        var request = Batch(3);

        var first = await service.CreateCartonBatchAsync(Lily, request);
        var replay = await service.CreateCartonBatchAsync(Lily, request);

        Assert.True(first.Success, first.Message);
        Assert.False(first.Data!.Duplicate);
        Assert.True(replay.Success, replay.Message);
        Assert.True(replay.Data!.Duplicate);
        Assert.Equal(first.Data.BatchGuid, replay.Data.BatchGuid);
        Assert.Equal(first.Data.FirstCartonNo, replay.Data.FirstCartonNo);
        Assert.Equal(first.Data.Cartons!.Select(carton => carton.CartonGuid), replay.Data.Cartons!.Select(carton => carton.CartonGuid));
        Assert.Equal(3, await _db.Queryable<PackCarton>().CountAsync());
        Assert.Equal(1, await _db.Queryable<PackCartonBatch>().CountAsync());
        var next = await service.CreateCartonAsync(Lily, new PackingCreateCartonRequestDto());
        Assert.Equal("BX2611130004", next.Data!.CartonNo);

        // 重放时即使张数 / 去向不同，也按请求编号返回已有批次（客户端重试的请求体本来就一样，这里只验证以编号为准）。
        var changed = await service.CreateCartonBatchAsync(Lily, Batch(50, request.ClientRequestId));
        Assert.True(changed.Data!.Duplicate);
        Assert.Equal(3, changed.Data.Count);
        Assert.Equal(4, await _db.Queryable<PackCarton>().CountAsync());
    }

    [Fact]
    public async Task 批量创建_请求编号被别的地点用过_409且不泄露批次()
    {
        var service = CreateService();
        var request = Batch(2);
        await service.CreateCartonBatchAsync(Lily, request);

        var other = await service.CreateCartonBatchAsync(Ian, request);

        Assert.False(other.Success);
        Assert.Equal(409, other.StatusCode);
        Assert.Equal(PackingErrorCodes.InvalidRequest, other.ErrorCode);
        Assert.Null(other.Data);
        Assert.Equal(2, await _db.Queryable<PackCarton>().CountAsync());
    }

    [Fact]
    public async Task 批量创建_张数必须1到100_请求编号必填()
    {
        var service = CreateService();

        foreach (var count in new[] { 0, -1, 101, 1000 })
        {
            var result = await service.CreateCartonBatchAsync(Lily, Batch(count));
            Assert.False(result.Success);
            Assert.Equal(400, result.StatusCode);
            Assert.Equal(PackingErrorCodes.QuantityOutOfRange, result.ErrorCode);
        }

        var noRequestId = await service.CreateCartonBatchAsync(Lily, Batch(3, Guid.Empty));
        Assert.Equal(400, noRequestId.StatusCode);
        Assert.Equal(PackingErrorCodes.InvalidRequest, noRequestId.ErrorCode);
        Assert.Equal(0, await _db.Queryable<PackCarton>().CountAsync());

        var one = await service.CreateCartonBatchAsync(Lily, Batch(1));
        var hundred = await service.CreateCartonBatchAsync(Lily, Batch(100));
        Assert.True(one.Success, one.Message);
        Assert.True(hundred.Success, hundred.Message);
        Assert.Equal(101, await _db.Queryable<PackCarton>().CountAsync());
    }

    [Fact]
    public async Task 批量创建_未启用空箱标最多300张_超出409带现有数与上限_启用或作废后释放额度()
    {
        var service = CreateService();
        var guids = new List<string>();
        for (var batch = 0; batch < 3; batch++)
        {
            var created = await service.CreateCartonBatchAsync(Wendy, Batch(100));
            Assert.True(created.Success, created.Message);
            guids.AddRange(created.Data!.Cartons!.Select(carton => carton.CartonGuid));
        }

        var over = await service.CreateCartonBatchAsync(Wendy, Batch(1));
        Assert.False(over.Success);
        Assert.Equal(409, over.StatusCode);
        Assert.Equal(PackingErrorCodes.BlankLimitExceeded, over.ErrorCode);
        Assert.Equal(300, over.Data!.Existing);
        Assert.Equal(300, over.Data.Limit);
        Assert.Null(over.Data.BatchGuid);
        Assert.Equal(300, await _db.Queryable<PackCarton>().CountAsync());
        Assert.Equal(3, await _db.Queryable<PackCartonBatch>().CountAsync());

        // 别的地点不受影响（额度按地点算）。
        var other = await service.CreateCartonBatchAsync(Lily, Batch(5));
        Assert.True(other.Success, other.Message);

        // 启用一张 → 额度空出 1：再建 2 张仍超限，建 1 张通过。
        Assert.True((await service.ActivateCartonAsync(Wendy, guids[0])).Success);
        var tooMany = await service.CreateCartonBatchAsync(Wendy, Batch(2));
        Assert.Equal(PackingErrorCodes.BlankLimitExceeded, tooMany.ErrorCode);
        Assert.Equal(299, tooMany.Data!.Existing);
        var fits = await service.CreateCartonBatchAsync(Wendy, Batch(1));
        Assert.True(fits.Success, fits.Message);

        // 作废 3 张未用的 → 再释放 3 张额度。
        var voided = await service.VoidBlankCartonsAsync(Wendy, guids.Skip(1).Take(3).ToList());
        Assert.Equal(3, voided.Data!.Voided.Count);
        var afterVoid = await service.CreateCartonBatchAsync(Wendy, Batch(3));
        Assert.True(afterVoid.Success, afterVoid.Message);
    }

    [Fact]
    public async Task 批量创建_被拒绝的上限请求不占号_号段不留空洞()
    {
        var service = CreateService();
        for (var batch = 0; batch < 3; batch++)
        {
            Assert.True((await service.CreateCartonBatchAsync(Wendy, Batch(100))).Success);
        }

        Assert.False((await service.CreateCartonBatchAsync(Wendy, Batch(10))).Success);

        var sequence = await _db.Queryable<PackSequence>().SingleAsync();
        Assert.Equal(300, sequence.LastValue);
    }

    [Fact]
    public async Task 批量创建_数据范围_不能在没有权限的地点建_多地点账号必须指定地点()
    {
        var service = CreateService();

        var notAllowed = await service.CreateCartonBatchAsync(Lily, Batch(2, ownerCode: "1020"));
        var missing = await service.CreateCartonBatchAsync(Admin, Batch(2));
        var ok = await service.CreateCartonBatchAsync(Admin, Batch(2, ownerCode: "1020"));

        Assert.Equal(403, notAllowed.StatusCode);
        Assert.Equal(PackingErrorCodes.OwnerNotAllowed, notAllowed.ErrorCode);
        Assert.Equal(PackingErrorCodes.OwnerRequired, missing.ErrorCode);
        Assert.True(ok.Success, ok.Message);
        Assert.All(ok.Data!.Cartons!, carton => Assert.Equal("1020", carton.OwnerCode));
        Assert.Equal(2, await _db.Queryable<PackCarton>().CountAsync());
    }

    // ---- 批次列表 / 详情 ----

    [Fact]
    public async Task 批次列表_按创建时间倒序_已打印未启用作废三个计数_限制条数()
    {
        var service = CreateService();
        var older = (await service.CreateCartonBatchAsync(Lily, Batch(4))).Data!;
        _time.Advance(TimeSpan.FromMinutes(5));
        var newer = (await service.CreateCartonBatchAsync(Tom, Batch(3))).Data!;
        _time.Advance(TimeSpan.FromMinutes(5));
        await service.CreateCartonBatchAsync(Ian, Batch(2));

        // older：打印 3 张（含最后会被作废的 1 张）、启用 1 张、作废 1 张。
        var olderGuids = older.Cartons!.Select(carton => carton.CartonGuid).ToList();
        await service.MarkLabelsPrintedAsync(Lily, new[] { olderGuids[0], olderGuids[1], olderGuids[3] });
        await service.ActivateCartonAsync(Lily, olderGuids[1]);
        await service.VoidBlankCartonsAsync(Lily, new[] { olderGuids[3] });

        var list = await service.ListBatchesAsync(Lily, null, null);

        Assert.True(list.Success, list.Message);
        Assert.Equal(new[] { newer.BatchGuid, older.BatchGuid }, list.Data!.Batches.Select(batch => batch.BatchGuid));
        var newerRow = list.Data.Batches[0];
        Assert.Equal(3, newerRow.Count);
        Assert.Equal("Tom", newerRow.CreatedByName);
        Assert.Equal(0, newerRow.PrintedCount);
        Assert.Equal(3, newerRow.UnactivatedCount);
        Assert.Equal(0, newerRow.VoidedCount);
        var olderRow = list.Data.Batches[1];
        Assert.Equal(4, olderRow.Count);
        Assert.Equal("BX2611130001", olderRow.FirstCartonNo);
        Assert.Equal("BX2611130004", olderRow.LastCartonNo);
        Assert.Equal(2, olderRow.PrintedCount); // 作废的那张不计
        Assert.Equal(2, olderRow.UnactivatedCount); // 4 张 - 1 张启用 - 1 张作废
        Assert.Equal(1, olderRow.VoidedCount);

        var limited = await service.ListBatchesAsync(Lily, null, 1);
        Assert.Single(limited.Data!.Batches);
        var clampedLow = await service.ListBatchesAsync(Lily, null, 0);
        Assert.Single(clampedLow.Data!.Batches);
        var forbidden = await service.ListBatchesAsync(Lily, "1020", null);
        Assert.Equal(403, forbidden.StatusCode);
    }

    [Fact]
    public async Task 批次详情_按批内序号升序_不含作废的箱_范围外与不存在都是404_BATCH_NOT_FOUND()
    {
        var service = CreateService();
        var batch = (await service.CreateCartonBatchAsync(Lily, Batch(4))).Data!;
        var guids = batch.Cartons!.Select(carton => carton.CartonGuid).ToList();
        await service.VoidBlankCartonsAsync(Lily, new[] { guids[1] });

        var detail = await service.GetBatchAsync(Lily, batch.BatchGuid!);

        Assert.True(detail.Success, detail.Message);
        Assert.Equal(new int?[] { 1, 3, 4 }, detail.Data!.Cartons.Select(carton => carton.BatchSeq));
        Assert.Equal(4, detail.Data.Batch.Count);
        Assert.Equal(1, detail.Data.Batch.VoidedCount);
        Assert.Equal(3, detail.Data.Batch.UnactivatedCount);

        var outOfScope = await service.GetBatchAsync(Ian, batch.BatchGuid!);
        var missing = await service.GetBatchAsync(Lily, "no-such-batch");
        Assert.Equal(404, outOfScope.StatusCode);
        Assert.Equal(PackingErrorCodes.BatchNotFound, outOfScope.ErrorCode);
        Assert.Equal(PackingErrorCodes.BatchNotFound, missing.ErrorCode);
    }

    // ---- 回报标签已打印 ----

    [Fact]
    public async Task 回报已打印_次数累加_记录时间_不动更新时间_重复调用不报错()
    {
        var service = CreateService();
        var batch = (await service.CreateCartonBatchAsync(Lily, Batch(3))).Data!;
        var guids = batch.Cartons!.Select(carton => carton.CartonGuid).ToList();
        var createdAt = (await _db.Queryable<PackCarton>().FirstAsync(carton => carton.CartonGUID == guids[0])).UpdatedAtUtc;
        _time.Advance(TimeSpan.FromMinutes(10));

        var first = await service.MarkLabelsPrintedAsync(Lily, guids);
        _time.Advance(TimeSpan.FromMinutes(10));
        var second = await service.MarkLabelsPrintedAsync(Lily, new[] { guids[0], guids[0] });

        Assert.True(first.Success, first.Message);
        Assert.Equal(3, first.Data!.Updated);
        Assert.Empty(first.Data.Skipped);
        Assert.Equal(1, second.Data!.Updated); // 请求里重复的编号只处理一次
        var rows = await _db.Queryable<PackCarton>().ToListAsync();
        Assert.Equal(2, rows.Single(row => row.CartonGUID == guids[0]).LabelPrintCount);
        Assert.Equal(1, rows.Single(row => row.CartonGUID == guids[1]).LabelPrintCount);
        Assert.Equal(_time.Now.UtcDateTime, rows.Single(row => row.CartonGUID == guids[0]).LabelPrintedAtUtc);
        Assert.Equal(createdAt, rows.Single(row => row.CartonGUID == guids[0]).UpdatedAtUtc);
        var summary = (await service.GetCartonAsync(Lily, guids[0])).Data!;
        Assert.Equal(2, summary.LabelPrintCount);
        Assert.NotNull(summary.LabelPrintedAtUtc);
    }

    [Fact]
    public async Task 回报已打印_范围外_不存在_已作废进skipped_其余照常更新()
    {
        var service = CreateService();
        var mine = (await service.CreateCartonBatchAsync(Lily, Batch(2))).Data!.Cartons!.Select(carton => carton.CartonGuid).ToList();
        var inala = (await service.CreateCartonBatchAsync(Ian, Batch(1))).Data!.Cartons![0].CartonGuid;
        await service.VoidBlankCartonsAsync(Lily, new[] { mine[1] });

        var result = await service.MarkLabelsPrintedAsync(Lily, new[] { mine[0], mine[1], inala, "no-such-carton" });

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, result.Data!.Updated);
        var skipped = result.Data.Skipped.ToDictionary(item => item.CartonGuid, item => item.Reason);
        Assert.Equal(PackingErrorCodes.SkipVoided, skipped[mine[1]]);
        Assert.Equal("VOIDED", skipped[mine[1]]);
        Assert.Equal("NOT_FOUND", skipped[inala]);
        Assert.Equal("NOT_FOUND", skipped["no-such-carton"]);
        // 别的地点的箱没被改。
        Assert.Null((await _db.Queryable<PackCarton>().FirstAsync(carton => carton.CartonGUID == inala)).LabelPrintCount);
    }

    [Fact]
    public async Task 回报已打印_空列表或超过100只_400()
    {
        var service = CreateService();
        var tooMany = Enumerable.Range(0, 101).Select(index => $"c-{index}").ToList();

        var empty = await service.MarkLabelsPrintedAsync(Lily, Array.Empty<string>());
        var none = await service.MarkLabelsPrintedAsync(Lily, null);
        var over = await service.MarkLabelsPrintedAsync(Lily, tooMany);
        var exactly = await service.MarkLabelsPrintedAsync(Lily, tooMany.Take(100).ToList());

        Assert.Equal(400, empty.StatusCode);
        Assert.Equal(400, none.StatusCode);
        Assert.Equal(400, over.StatusCode);
        Assert.Equal(PackingErrorCodes.InvalidRequest, over.ErrorCode);
        Assert.True(exactly.Success);
        Assert.Equal(100, exactly.Data!.Skipped.Count);
    }

    // ---- 启用 ----

    [Fact]
    public async Task 启用_空箱标置启用时间_幂等_普通箱直接返回详情()
    {
        var service = CreateService();
        var blank = (await service.CreateCartonBatchAsync(Lily, Batch(2))).Data!.Cartons![0];
        var normal = (await service.CreateCartonAsync(Lily, new PackingCreateCartonRequestDto())).Data!;

        var first = await service.ActivateCartonAsync(Lily, blank.CartonGuid);
        _time.Advance(TimeSpan.FromMinutes(5));
        var again = await service.ActivateCartonAsync(Lily, blank.CartonGuid);
        var normalResult = await service.ActivateCartonAsync(Lily, normal.CartonGuid);

        Assert.True(first.Success, first.Message);
        Assert.False(first.Data!.IsBlank);
        Assert.Equal(new DateTime(2026, 11, 13, 4, 30, 0, DateTimeKind.Utc), first.Data.ActivatedAtUtc);
        Assert.Equal(blank.BatchGuid, first.Data.BatchGuid);
        Assert.True(again.Success);
        Assert.Equal(first.Data.ActivatedAtUtc, again.Data!.ActivatedAtUtc); // 重复启用不改时间
        Assert.True(normalResult.Success);
        Assert.False(normalResult.Data!.IsBlank);
        Assert.Null(normalResult.Data.ActivatedAtUtc);
        // 启用只写一次事件。
        Assert.Equal(1, await _db.Queryable<PackEvent>().CountAsync(item => item.TargetGUID == blank.CartonGuid && item.Action == PackEventActions.HeaderChanged));
    }

    [Fact]
    public async Task 启用_已封箱_已作废_409_CARTON_NOT_PACKING_范围外与不存在404()
    {
        var service = CreateService();
        var blankVoided = (await service.CreateCartonBatchAsync(Lily, Batch(1))).Data!.Cartons![0];
        await service.VoidBlankCartonsAsync(Lily, new[] { blankVoided.CartonGuid });
        var sealedCarton = await SealedNormalCartonAsync(service, Lily);
        var inala = (await service.CreateCartonBatchAsync(Ian, Batch(1))).Data!.Cartons![0];

        var voided = await service.ActivateCartonAsync(Lily, blankVoided.CartonGuid);
        var sealedResult = await service.ActivateCartonAsync(Lily, sealedCarton.CartonGuid);
        var outOfScope = await service.ActivateCartonAsync(Lily, inala.CartonGuid);
        var missing = await service.ActivateCartonAsync(Lily, "no-such-carton");

        Assert.Equal(409, voided.StatusCode);
        Assert.Equal(PackingErrorCodes.CartonNotPacking, voided.ErrorCode);
        Assert.Equal(409, sealedResult.StatusCode);
        Assert.Equal(PackingErrorCodes.CartonNotPacking, sealedResult.ErrorCode);
        Assert.Equal(404, outOfScope.StatusCode);
        Assert.Equal(PackingErrorCodes.CartonNotFound, outOfScope.ErrorCode);
        Assert.Equal(PackingErrorCodes.CartonNotFound, missing.ErrorCode);
        Assert.Null((await _db.Queryable<PackCarton>().FirstAsync(carton => carton.CartonGUID == inala.CartonGuid)).ActivatedAtUtc);
    }

    [Fact]
    public async Task 封箱_空箱标即使已启用也仍是空箱_409_CARTON_EMPTY()
    {
        var service = CreateService();
        var blank = (await service.CreateCartonBatchAsync(Lily, Batch(1))).Data!.Cartons![0];

        var unactivated = await service.SealCartonAsync(Lily, blank.CartonGuid, new PackingSealRequestDto());
        await service.ActivateCartonAsync(Lily, blank.CartonGuid);
        var activated = await service.SealCartonAsync(Lily, blank.CartonGuid, new PackingSealRequestDto());

        Assert.Equal(PackingErrorCodes.CartonEmpty, unactivated.ErrorCode);
        Assert.Equal(PackingErrorCodes.CartonEmpty, activated.ErrorCode);
        Assert.Equal(409, activated.StatusCode);
    }

    // ---- 装入商品自动启用 ----

    [Fact]
    public async Task 装入商品_扫码把空箱标自动启用_之后算装箱中_可以封箱()
    {
        var service = CreateService();
        var blank = (await service.CreateCartonBatchAsync(Lily, Batch(2))).Data!.Cartons![0];

        var scan = await service.ScanIntoCartonAsync(Lily, blank.CartonGuid, new PackingScanRequestDto { Code = "HB23817", Quantity = 6 });

        Assert.True(scan.Success, scan.Message);
        Assert.False(scan.Data!.IsBlank);
        Assert.Equal(new DateTime(2026, 11, 13, 4, 30, 0, DateTimeKind.Utc), scan.Data.ActivatedAtUtc);
        Assert.Equal(blank.BatchGuid, scan.Data.BatchGuid);
        Assert.Equal(2, scan.Data.BatchCount);
        Assert.Equal(6, scan.Data.PieceCount);
        var stored = await _db.Queryable<PackCarton>().FirstAsync(carton => carton.CartonGUID == blank.CartonGuid);
        Assert.NotNull(stored.ActivatedAtUtc);
        var list = (await service.ListCartonsAsync(Lily, null, "packing", null, false)).Data!;
        Assert.Equal(blank.CartonGuid, Assert.Single(list.Items).CartonGuid);
        Assert.Equal(1, list.PackingCount);
        Assert.Equal(1, list.BlankCount); // 批内另一张还没启用
        var sealedResult = await service.SealCartonAsync(Lily, blank.CartonGuid, new PackingSealRequestDto());
        Assert.True(sealedResult.Success, sealedResult.Message);
    }

    [Fact]
    public async Task 装入商品_按订货单装箱路径也会启用_先拣后装与边拣边装共用的写入入口()
    {
        var service = CreateService();
        var batch = (await service.CreateCartonBatchAsync(Wendy, Batch(2))).Data!.Cartons!;
        // 防御性状态：空箱标带着订货单但还没启用（正常流程不会出现，关联订货单时已经启用）。
        foreach (var carton in batch)
        {
            await _db.Updateable<PackCarton>().SetColumns(item => item.OrderGUID == "order-1").Where(item => item.CartonGUID == carton.CartonGuid).ExecuteCommandAsync();
        }

        var adjust = await service.AdjustOrderCartonItemAsync(Wendy, "order-1", batch[0].CartonGuid, "P-MUG", 3);
        var setQuantity = await service.SetOrderCartonItemQuantityAsync(Wendy, "order-1", batch[1].CartonGuid, "P-MUG", 5);

        Assert.True(adjust.Success, adjust.Message);
        Assert.True(setQuantity.Success, setQuantity.Message);
        Assert.False(adjust.Data!.IsBlank);
        Assert.NotNull(adjust.Data.ActivatedAtUtc);
        Assert.False(setQuantity.Data!.IsBlank);
        Assert.NotNull(setQuantity.Data.ActivatedAtUtc);
        Assert.Equal(2, await _db.Queryable<PackCarton>().CountAsync(carton => carton.ActivatedAtUtc != null));
    }

    [Fact]
    public async Task 装入商品_减到0或删除不会启用_关联订货单的表头修改会启用()
    {
        var service = CreateService();
        var batch = (await service.CreateCartonBatchAsync(Wendy, Batch(2))).Data!.Cartons!;

        // 箱里没有这个商品：设数量失败，空箱标保持未启用。
        var setMissing = await service.SetCartonItemQuantityAsync(Wendy, batch[0].CartonGuid, "P-MUG", 0);
        Assert.False(setMissing.Success);
        Assert.Null((await _db.Queryable<PackCarton>().FirstAsync(carton => carton.CartonGUID == batch[0].CartonGuid)).ActivatedAtUtc);

        // 只改去向（不关联订货单）不启用；关联订货单就是用上了，启用。
        var onlyDestination = await service.UpdateCartonHeaderAsync(Wendy, batch[0].CartonGuid, new PackingCartonHeaderRequestDto { DestinationType = PackDestinationTypes.Store, DestinationCode = "1013" });
        Assert.True(onlyDestination.Success, onlyDestination.Message);
        Assert.True(onlyDestination.Data!.IsBlank);
        Assert.Equal(PackDestinationTypes.Store, onlyDestination.Data.Destination.DestinationType);
        var withOrder = await service.UpdateCartonHeaderAsync(Wendy, batch[1].CartonGuid, new PackingCartonHeaderRequestDto { OrderGuid = "order-1" });
        Assert.True(withOrder.Success, withOrder.Message);
        Assert.False(withOrder.Data!.IsBlank);
        Assert.Equal("order-1", withOrder.Data.OrderGuid);
    }

    // ---- 作废未用空箱标 ----

    [Fact]
    public async Task 作废未用空箱标_只作废未启用的空箱_其余全部skipped并给出原因()
    {
        var service = CreateService();
        var blanks = (await service.CreateCartonBatchAsync(Lily, Batch(6))).Data!.Cartons!.Select(carton => carton.CartonGuid).ToList();
        var inala = (await service.CreateCartonBatchAsync(Ian, Batch(1))).Data!.Cartons![0].CartonGuid;
        var normal = (await service.CreateCartonAsync(Lily, new PackingCreateCartonRequestDto())).Data!;
        var withItems = (await service.ScanIntoCartonAsync(Lily, blanks[1], new PackingScanRequestDto { Code = "HB23817", Quantity = 1 })).Data!;
        await service.ActivateCartonAsync(Lily, blanks[2]);
        var sealedCarton = (await service.ScanIntoCartonAsync(Lily, blanks[3], new PackingScanRequestDto { Code = "HB23817", Quantity = 1 })).Data!;
        Assert.True((await service.SealCartonAsync(Lily, sealedCarton.CartonGuid, new PackingSealRequestDto())).Success);
        await service.VoidBlankCartonsAsync(Lily, new[] { blanks[4] });

        var result = await service.VoidBlankCartonsAsync(
            Lily,
            new[] { blanks[0], blanks[1], blanks[2], blanks[3], blanks[4], blanks[5], normal.CartonGuid, inala, "no-such-carton" }
        );

        Assert.True(result.Success, result.Message);
        Assert.Equal(new[] { blanks[0], blanks[5] }.OrderBy(guid => guid, StringComparer.Ordinal), result.Data!.Voided);
        var skipped = result.Data.Skipped.ToDictionary(item => item.CartonGuid, item => item.Reason);
        Assert.Equal("NOT_BLANK", skipped[blanks[1]]); // 有商品（已启用）
        Assert.Equal("NOT_BLANK", skipped[blanks[2]]); // 显式启用过
        Assert.Equal("NOT_BLANK", skipped[blanks[3]]); // 已封箱
        Assert.Equal("NOT_FOUND", skipped[blanks[4]]); // 已经作废
        Assert.Equal("NOT_BLANK", skipped[normal.CartonGuid]); // 普通箱不能用它作废
        Assert.Equal("NOT_FOUND", skipped[inala]); // 范围外
        Assert.Equal("NOT_FOUND", skipped["no-such-carton"]);
        var rows = await _db.Queryable<PackCarton>().ToListAsync();
        Assert.Equal(PackCartonStatuses.Voided, rows.Single(row => row.CartonGUID == blanks[0]).Status);
        Assert.Equal(PackCartonStatuses.Packing, rows.Single(row => row.CartonGUID == normal.CartonGuid).Status);
        Assert.Equal(PackCartonStatuses.Packing, rows.Single(row => row.CartonGUID == blanks[1]).Status);
        Assert.Equal(PackCartonStatuses.Sealed, rows.Single(row => row.CartonGUID == blanks[3]).Status);
        Assert.Equal(PackCartonStatuses.Packing, rows.Single(row => row.CartonGUID == inala).Status);
        var voidEvent = await _db.Queryable<PackEvent>().FirstAsync(item => item.TargetGUID == blanks[0] && item.Action == PackEventActions.Voided);
        Assert.Equal("作废未用空箱标", voidEvent.Detail);
        Assert.Equal("Lily", voidEvent.OperatorName);
        Assert.Equal(withItems.CartonGuid, blanks[1]);
    }

    [Fact]
    public async Task 作废未用空箱标_空列表或超过100只_400()
    {
        var service = CreateService();

        var empty = await service.VoidBlankCartonsAsync(Lily, Array.Empty<string>());
        var over = await service.VoidBlankCartonsAsync(Lily, Enumerable.Range(0, 101).Select(index => $"c-{index}").ToList());

        Assert.Equal(400, empty.StatusCode);
        Assert.Equal(400, over.StatusCode);
    }

    // ---- 列表与计数 ----

    [Fact]
    public async Task 箱列表_filter为blank只给未启用空箱标_其它筛选包括全部都排除_blankCount与packingCount分开算()
    {
        var service = CreateService();
        var blanks = (await service.CreateCartonBatchAsync(Lily, Batch(4))).Data!.Cartons!;
        var normal = (await service.CreateCartonAsync(Lily, new PackingCreateCartonRequestDto())).Data!;
        // 启用一张：它就成了普通的装箱中箱。
        await service.ScanIntoCartonAsync(Lily, blanks[0].CartonGuid, new PackingScanRequestDto { Code = "HB23817", Quantity = 1 });
        // 别的店的空箱标不出现。
        await service.CreateCartonBatchAsync(Ian, Batch(2));

        var blank = (await service.ListCartonsAsync(Lily, null, "blank", null, false)).Data!;
        var all = (await service.ListCartonsAsync(Lily, null, "all", null, false)).Data!;
        var defaultFilter = (await service.ListCartonsAsync(Lily, null, null, null, false)).Data!;
        var packing = (await service.ListCartonsAsync(Lily, null, "packing", null, false)).Data!;
        var mine = (await service.ListCartonsAsync(Lily, null, "all", null, true)).Data!;
        var stored = (await service.ListCartonsAsync(Lily, null, "stored", null, false)).Data!;

        Assert.Equal(new[] { blanks[1].CartonNo, blanks[2].CartonNo, blanks[3].CartonNo }, blank.Items.Select(item => item.CartonNo));
        Assert.All(blank.Items, item => Assert.True(item.IsBlank));
        var expectedLive = new[] { blanks[0].CartonGuid, normal.CartonGuid }.OrderBy(guid => guid, StringComparer.Ordinal);
        foreach (var list in new[] { all, defaultFilter, packing, mine })
        {
            Assert.Equal(expectedLive, list.Items.Select(item => item.CartonGuid).OrderBy(guid => guid, StringComparer.Ordinal));
            Assert.DoesNotContain(list.Items, item => item.IsBlank);
        }

        Assert.Empty(stored.Items);
        foreach (var list in new[] { blank, all, packing, stored })
        {
            Assert.Equal(2, list.PackingCount); // 启用的那张 + 普通箱，不含 3 张未启用空箱标
            Assert.Equal(3, list.BlankCount);
        }
    }

    [Fact]
    public async Task 箱列表_关键字搜索也遵守空箱标分类_作废的空箱标哪里都不出现()
    {
        var service = CreateService();
        var blanks = (await service.CreateCartonBatchAsync(Lily, Batch(3))).Data!.Cartons!;
        await service.VoidBlankCartonsAsync(Lily, new[] { blanks[2].CartonGuid });

        var allSearch = (await service.ListCartonsAsync(Lily, null, "all", blanks[0].CartonNo, false)).Data!;
        var blankSearch = (await service.ListCartonsAsync(Lily, null, "blank", blanks[0].CartonNo, false)).Data!;
        var blankAll = (await service.ListCartonsAsync(Lily, null, "blank", null, false)).Data!;

        Assert.Empty(allSearch.Items);
        Assert.Equal(blanks[0].CartonGuid, Assert.Single(blankSearch.Items).CartonGuid);
        Assert.Equal(2, blankAll.Items.Count);
        Assert.Equal(2, blankAll.BlankCount); // 作废的不计
        Assert.Equal(0, blankAll.PackingCount);
    }

    [Fact]
    public async Task 箱列表_空箱标最多300张全部返回_不受普通列表200只上限影响()
    {
        var service = CreateService();
        for (var batch = 0; batch < 3; batch++)
        {
            Assert.True((await service.CreateCartonBatchAsync(Lily, Batch(100))).Success);
        }

        var blank = (await service.ListCartonsAsync(Lily, null, "blank", null, false)).Data!;

        Assert.Equal(300, blank.Items.Count);
        Assert.Equal(300, blank.BlankCount);
        Assert.Equal("BX2611130001", blank.Items[0].CartonNo);
        Assert.Equal("BX2611130300", blank.Items[^1].CartonNo);
    }

    [Fact]
    public async Task 箱详情_带上空箱标字段_批内总张数一次批量查询()
    {
        var service = CreateService();
        var batch = (await service.CreateCartonBatchAsync(Lily, Batch(3))).Data!;

        var detail = await service.GetCartonAsync(Lily, batch.Cartons![1].CartonGuid);

        Assert.True(detail.Success, detail.Message);
        Assert.True(detail.Data!.IsBlank);
        Assert.Equal(batch.BatchGuid, detail.Data.BatchGuid);
        Assert.Equal(2, detail.Data.BatchSeq);
        Assert.Equal(3, detail.Data.BatchCount);
        Assert.Null(detail.Data.ActivatedAtUtc);
        Assert.Equal(0, detail.Data.LabelPrintCount);
        Assert.Null(detail.Data.LabelPrintedAtUtc);
    }

    // ---- 夹具 ----

    private static PackingCreateBatchRequestDto Batch(
        int count,
        Guid? clientRequestId = null,
        string? ownerCode = null,
        int destinationType = PackDestinationTypes.Unassigned,
        string? destinationCode = null,
        string? carrierGuid = null
    ) => new()
    {
        Count = count,
        ClientRequestId = clientRequestId ?? Guid.NewGuid(),
        OwnerCode = ownerCode,
        DestinationType = destinationType,
        DestinationCode = destinationCode,
        CarrierGuid = carrierGuid,
    };

    private async Task<PackingCartonDetailDto> SealedNormalCartonAsync(IPackingService service, PackingOperator op)
    {
        var carton = (await service.CreateCartonAsync(op, new PackingCreateCartonRequestDto())).Data!;
        var scan = await service.ScanIntoCartonAsync(op, carton.CartonGuid, new PackingScanRequestDto { Code = "HB23817", Quantity = 2 });
        Assert.True(scan.Success, scan.Message);
        var sealedResult = await service.SealCartonAsync(op, carton.CartonGuid, new PackingSealRequestDto());
        Assert.True(sealedResult.Success, sealedResult.Message);
        return sealedResult.Data!;
    }

    private void SeedMasterData()
    {
        _db.Insertable(new List<Store>
        {
            new() { StoreGUID = "store-1013", StoreCode = "1013", StoreName = "Sunnybank", TimeZoneId = "Australia/Brisbane" },
            new() { StoreGUID = "store-1020", StoreCode = "1020", StoreName = "Inala", TimeZoneId = "Australia/Brisbane" },
        }).ExecuteCommand();
        _db.Insertable(new Product
        {
            ProductCode = "P-MUG",
            ItemNumber = "HB23817",
            Barcode = "9345678123457",
            ProductName = "Ceramic Mug 350ml",
            MiddlePackageQuantity = 12,
        }).ExecuteCommand();
        _db.Insertable(new WareHouseOrder { OrderGUID = "order-1", OrderNo = "2026-0412", StoreCode = "1013", FlowStatus = 3, OrderDate = new DateTime(2026, 11, 12, 9, 0, 0) }).ExecuteCommand();
    }

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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public void Advance(TimeSpan delta) => Now = Now.Add(delta);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
