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
/// 仓库订单拣货：拣货记录只追加、行合计 = 记录之和，提交时写入配货数并锁定会话。
/// </summary>
public sealed class WarehousePickingServiceTests : IDisposable
{
    private const string OrderGuid = "order-1";
    private const string StoreCode = "1013";

    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;
    private readonly Mock<IProductWarehouseReactService> _productWarehouseService = new(MockBehavior.Strict);

    private static readonly WarehousePickerContext Alex = new("user-alex", "Alex Chen", false, "user-alex", null);
    private static readonly WarehousePickerContext Mia = new("user-mia", "Mia Wong", false, null, "PDA-01");
    private static readonly WarehousePickerContext Manager = new("user-mgr", "Morgan Lee", true, "user-mgr", null);

    public WarehousePickingServiceTests()
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
            typeof(WarehouseOrderPickStockout)
        );
    }

    [Fact]
    public async Task Join_已提交订单转为配货中并登记会话与参与人()
    {
        await SeedStandardOrderAsync(flowStatus: 1);

        var result = await CreateService().JoinAsync(OrderGuid, Alex);

        Assert.True(result.Success, result.Message);
        var order = await _db.Queryable<WareHouseOrder>().SingleAsync(item => item.OrderGUID == OrderGuid);
        Assert.Equal(3, order.FlowStatus);
        Assert.Equal("Alex Chen", order.UpdatedBy);
        var session = await _db.Queryable<WarehouseOrderPickSession>().SingleAsync();
        Assert.Equal(WarehouseOrderPickSessionStatuses.Picking, session.Status);
        Assert.Equal("Alex Chen", session.StartedByName);
        var participant = await _db.Queryable<WarehouseOrderPickParticipant>().SingleAsync();
        Assert.Equal("user-alex", participant.PickerUserGuid);
        Assert.Equal(3, result.Data!.FlowStatus);
        Assert.Equal("Morayfield", result.Data.StoreName);
        Assert.Equal(4, result.Data.Lines.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Join_购物车与已完成订单不能拣货(int flowStatus)
    {
        await SeedStandardOrderAsync(flowStatus);

        var result = await CreateService().JoinAsync(OrderGuid, Alex);

        Assert.False(result.Success);
        Assert.Equal(409, result.StatusCode);
        Assert.Equal(WarehousePickingErrorCodes.OrderNotPickable, result.ErrorCode);
        Assert.Equal(0, await _db.Queryable<WarehouseOrderPickSession>().CountAsync());
    }

    [Fact]
    public async Task Sheet_码表包含主码货号多码套装子码与货位_套装子码带子项名称()
    {
        await SeedStandardOrderAsync(flowStatus: 3);

        var sheet = (await CreateService().JoinAsync(OrderGuid, Alex)).Data!;

        var cup = sheet.Lines.Single(line => line.DetailGuid == "d-cup");
        Assert.Equal(12, cup.MinOrderQuantity);
        Assert.Equal("A-03-12-02", cup.LocationCode);
        Assert.False(cup.IsSet);
        var set = sheet.Lines.Single(line => line.DetailGuid == "d-set");
        Assert.True(set.IsSet);
        Assert.Equal(3, set.SetChildren.Count);
        Assert.Contains(set.SetChildren, child => child.ProductName == "Small box" && child.Barcode == "6901234567892");

        AssertCode(sheet, "9312345678905", "line", "d-cup", WarehouseOrderPickMatchKinds.Barcode);
        AssertCode(sheet, "HB20931", "line", "d-cup", WarehouseOrderPickMatchKinds.ItemNumber);
        AssertCode(sheet, "MULTI-CUP", "line", "d-cup", WarehouseOrderPickMatchKinds.MultiCode);
        AssertCode(sheet, "STORE-MULTI-CUP", "line", "d-cup", WarehouseOrderPickMatchKinds.MultiCode);
        var child = AssertCode(sheet, "6901234567892", "line", "d-set", WarehouseOrderPickMatchKinds.SetChild);
        Assert.Equal("Small box", child.Label);
        AssertCode(sheet, "A-03-12-02", "location", "d-cup", null);
        AssertCode(sheet, "2000000000017", "location", "d-cup", null);
        // 别的分店的一品多码不属于本单分店，不能进码表。
        Assert.DoesNotContain(sheet.Codes, code => code.Code == "OTHER-STORE-MULTI");
    }

    [Fact]
    public async Task AppendRecord_扫码按服务端当前中包数累加并记录拣货人()
    {
        await SeedStandardOrderAsync(flowStatus: 3);
        var service = CreateService();
        await service.JoinAsync(OrderGuid, Alex);

        var first = await service.AppendRecordAsync(OrderGuid, Scan("d-cup", "9312345678905"), Alex);
        var second = await service.AppendRecordAsync(OrderGuid, Scan("d-cup", "9312345678905"), Alex);

        Assert.True(second.Success, second.Message);
        Assert.Equal(12, first.Data!.AppliedDelta);
        Assert.Equal(24, second.Data!.Line.PickedTotal);
        var pickedBy = Assert.Single(second.Data.Line.PickedBy);
        Assert.Equal("Alex Chen", pickedBy.PickerName);
        var record = await _db.Queryable<WarehouseOrderPickRecord>().FirstAsync();
        Assert.Equal(12, record.MinOrderQuantityAtPick);
        Assert.Equal("9312345678905", record.ScannedCode);
        Assert.Equal(WarehouseOrderPickMatchKinds.Barcode, record.MatchedBy);
        var participant = await _db.Queryable<WarehouseOrderPickParticipant>().SingleAsync();
        Assert.Equal("d-cup", participant.LastDetailGUID);
    }

    [Fact]
    public async Task AppendRecord_同一请求编号重放不重复计数()
    {
        await SeedStandardOrderAsync(flowStatus: 3);
        var service = CreateService();
        await service.JoinAsync(OrderGuid, Alex);
        var request = Scan("d-cup", "HB20931");

        await service.AppendRecordAsync(OrderGuid, request, Alex);
        var replay = await service.AppendRecordAsync(OrderGuid, request, Alex);

        Assert.True(replay.Success);
        Assert.True(replay.Data!.Duplicate);
        Assert.Equal(12, replay.Data.Line.PickedTotal);
        Assert.Equal(1, await _db.Queryable<WarehouseOrderPickRecord>().CountAsync());
    }

    [Fact]
    public async Task AppendRecord_中包未设置时拒绝按中包计_显式件数可计入()
    {
        await SeedStandardOrderAsync(flowStatus: 3);
        var service = CreateService();
        await service.JoinAsync(OrderGuid, Alex);

        var rejected = await service.AppendRecordAsync(OrderGuid, Scan("d-sponge", "9312345612343"), Alex);
        var single = Scan("d-sponge", "9312345612343");
        single.Pieces = 1;
        var counted = await service.AppendRecordAsync(OrderGuid, single, Alex);

        Assert.False(rejected.Success);
        Assert.Equal(WarehousePickingErrorCodes.MinOrderQuantityMissing, rejected.ErrorCode);
        Assert.True(counted.Success, counted.Message);
        Assert.Equal(1, counted.Data!.Line.PickedTotal);
    }

    [Fact]
    public async Task AppendRecord_减一个中包最多减到零_为零时拒绝()
    {
        await SeedStandardOrderAsync(flowStatus: 3);
        var service = CreateService();
        await service.JoinAsync(OrderGuid, Alex);
        await service.SetLineTotalAsync(OrderGuid, "d-cup", SetTotal(5, 0), Alex);

        var decrement = await service.AppendRecordAsync(OrderGuid, Button("d-cup", WarehouseOrderPickSources.Decrement), Alex);
        var belowZero = await service.AppendRecordAsync(OrderGuid, Button("d-cup", WarehouseOrderPickSources.Decrement), Alex);

        Assert.True(decrement.Success, decrement.Message);
        Assert.Equal(-5, decrement.Data!.AppliedDelta);
        Assert.Equal(0, decrement.Data.Line.PickedTotal);
        Assert.False(belowZero.Success);
        Assert.Equal(WarehousePickingErrorCodes.PickedBelowZero, belowZero.ErrorCode);
    }

    [Fact]
    public async Task 一起拣_行合计为各拣货人记录之和且分别归属()
    {
        await SeedStandardOrderAsync(flowStatus: 3);
        var service = CreateService();
        await service.JoinAsync(OrderGuid, Alex);
        await service.JoinAsync(OrderGuid, Mia);

        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", "9312345678905"), Alex);
        var result = await service.AppendRecordAsync(OrderGuid, Scan("d-cup", "9312345678905"), Mia);
        var progress = await service.GetProgressAsync(OrderGuid);

        Assert.Equal(24, result.Data!.Line.PickedTotal);
        Assert.Equal(2, result.Data.Line.PickedBy.Count);
        var line = progress.Data!.Lines.Single(item => item.DetailGuid == "d-cup");
        Assert.Contains(line.PickedBy, entry => entry.PickerName == "Mia Wong" && entry.Quantity == 12);
        Assert.Equal(2, progress.Data.Participants.Count);
        var miaRecord = await _db.Queryable<WarehouseOrderPickRecord>().SingleAsync(record => record.PickerUserGuid == "user-mia");
        Assert.Equal("PDA-01", miaRecord.DeviceCode);
    }

    [Fact]
    public async Task SetLineTotal_合计已被他人改变时返回最新合计_一致时按差额记账()
    {
        await SeedStandardOrderAsync(flowStatus: 3);
        var service = CreateService();
        await service.JoinAsync(OrderGuid, Alex);
        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", "9312345678905"), Mia);

        var stale = await service.SetLineTotalAsync(OrderGuid, "d-cup", SetTotal(30, expectedTotal: 0), Alex);
        var applied = await service.SetLineTotalAsync(OrderGuid, "d-cup", SetTotal(30, expectedTotal: 12), Alex);

        Assert.False(stale.Success);
        Assert.Equal(409, stale.StatusCode);
        Assert.Equal(WarehousePickingErrorCodes.PickedTotalChanged, stale.ErrorCode);
        Assert.Equal(12, stale.Data!.Line.PickedTotal);
        Assert.True(applied.Success, applied.Message);
        Assert.Equal(18, applied.Data!.AppliedDelta);
        Assert.Equal(30, applied.Data.Line.PickedTotal);
        var adjustment = await _db.Queryable<WarehouseOrderPickRecord>()
            .SingleAsync(record => record.Source == WarehouseOrderPickSources.SetTotal);
        Assert.Equal("user-alex", adjustment.PickerUserGuid);
    }

    [Fact]
    public async Task Submit_写入配货数与金额_双零行软删除_会话锁定后拒绝再写()
    {
        await SeedStandardOrderAsync(flowStatus: 3);
        var service = CreateService();
        await service.JoinAsync(OrderGuid, Alex);
        await service.SetLineTotalAsync(OrderGuid, "d-cup", SetTotal(24, 0), Alex);
        await service.SetLineTotalAsync(OrderGuid, "d-set", SetTotal(14, 0), Mia);

        var submitted = await service.SubmitAsync(OrderGuid, Alex);
        var afterSubmit = await service.AppendRecordAsync(OrderGuid, Scan("d-cup", "9312345678905"), Alex);
        var again = await service.SubmitAsync(OrderGuid, Mia);

        Assert.True(submitted.Success, submitted.Message);
        Assert.Equal(4, submitted.Data!.LineCount);
        Assert.Equal(2, submitted.Data.ShortLineCount);
        Assert.Equal(1, submitted.Data.OverLineCount);
        var details = await _db.Queryable<WareHouseOrderDetails>().ToListAsync();
        var cup = details.Single(detail => detail.DetailGUID == "d-cup");
        Assert.Equal(24m, cup.AllocQuantity);
        Assert.Equal(24m * 12.99m, cup.OEMAmount);
        Assert.Equal("Alex Chen", cup.UpdatedBy);
        Assert.Equal(0m, details.Single(detail => detail.DetailGUID == "d-sponge").AllocQuantity);
        Assert.False(details.Single(detail => detail.DetailGUID == "d-sponge").IsDeleted);
        // 仓库主动加的行（订货 0）没拣到：与批量改配货数同口径软删除。
        Assert.True(details.Single(detail => detail.DetailGUID == "d-extra").IsDeleted);
        var order = await _db.Queryable<WareHouseOrder>().SingleAsync(item => item.OrderGUID == OrderGuid);
        Assert.Equal(3, order.FlowStatus);
        Assert.Equal(24m * 12.99m + 14m * 15.99m, order.OEMTotalAmount);
        var session = await _db.Queryable<WarehouseOrderPickSession>().SingleAsync();
        Assert.Equal(WarehouseOrderPickSessionStatuses.Submitted, session.Status);
        Assert.Equal("Alex Chen", session.SubmittedByName);
        Assert.Equal(WarehousePickingErrorCodes.SessionSubmitted, afterSubmit.ErrorCode);
        Assert.Equal(WarehousePickingErrorCodes.SessionSubmitted, again.ErrorCode);
    }

    [Fact]
    public async Task MarkStockout_保留已拣合计_记录原因与货位快照_拣货单与进度都带标记_撤销后消失()
    {
        await SeedStandardOrderAsync(flowStatus: 3);
        var service = CreateService();
        await service.JoinAsync(OrderGuid, Alex);
        await service.AppendRecordAsync(OrderGuid, Scan("d-cup", "9312345678905"), Alex);

        var marked = await service.MarkStockoutAsync(OrderGuid, "d-cup", WarehouseOrderPickStockoutReasons.LocationEmpty, Mia);

        Assert.True(marked.Success, marked.Message);
        Assert.Equal(12, marked.Data!.Line.PickedTotal);
        Assert.Equal(0, marked.Data.AppliedDelta);
        Assert.Equal(WarehouseOrderPickStockoutReasons.LocationEmpty, marked.Data.Line.Stockout!.Reason);
        Assert.Equal("Mia Wong", marked.Data.Line.Stockout.MarkedByName);
        Assert.Equal(12, marked.Data.Line.Stockout.PickedAtMark);
        var row = await _db.Queryable<WarehouseOrderPickStockout>().SingleAsync();
        Assert.Equal("A-03-12-02", row.LocationCode);
        Assert.Equal("P-CUP", row.ProductCode);
        // 标记不写拣货记录。
        Assert.Equal(1, await _db.Queryable<WarehouseOrderPickRecord>().CountAsync());

        var sheet = await service.GetSheetAsync(OrderGuid);
        Assert.NotNull(sheet.Data!.Lines.Single(line => line.DetailGuid == "d-cup").Stockout);
        Assert.Null(sheet.Data.Lines.Single(line => line.DetailGuid == "d-sponge").Stockout);
        var progress = await service.GetProgressAsync(OrderGuid);
        Assert.NotNull(progress.Data!.Lines.Single(line => line.DetailGuid == "d-cup").Stockout);

        // 同一行再次标记覆盖原因，不新增行。
        var remarked = await service.MarkStockoutAsync(OrderGuid, "d-cup", WarehouseOrderPickStockoutReasons.Damaged, Alex);
        Assert.Equal(WarehouseOrderPickStockoutReasons.Damaged, remarked.Data!.Line.Stockout!.Reason);
        Assert.Equal(1, await _db.Queryable<WarehouseOrderPickStockout>().CountAsync());

        var cleared = await service.ClearStockoutAsync(OrderGuid, "d-cup", Alex);
        var clearedAgain = await service.ClearStockoutAsync(OrderGuid, "d-cup", Alex);

        Assert.True(cleared.Success, cleared.Message);
        Assert.Null(cleared.Data!.Line.Stockout);
        Assert.True(clearedAgain.Success, clearedAgain.Message);
        var clearedRow = await _db.Queryable<WarehouseOrderPickStockout>().SingleAsync();
        Assert.NotNull(clearedRow.ClearedAtUtc);
        Assert.Equal("Alex Chen", clearedRow.ClearedByName);
        Assert.Null((await service.GetProgressAsync(OrderGuid)).Data!.Lines.Single(line => line.DetailGuid == "d-cup").Stockout);
    }

    [Fact]
    public async Task MarkStockout_已拣齐的行与无效原因都拒绝_已提交后不能再标()
    {
        await SeedStandardOrderAsync(flowStatus: 3);
        var service = CreateService();
        await service.JoinAsync(OrderGuid, Alex);
        await service.SetLineTotalAsync(OrderGuid, "d-cup", SetTotal(36, 0), Alex);

        var complete = await service.MarkStockoutAsync(OrderGuid, "d-cup", WarehouseOrderPickStockoutReasons.LocationEmpty, Alex);
        var invalidReason = await service.MarkStockoutAsync(OrderGuid, "d-sponge", 9, Alex);
        var missingLine = await service.MarkStockoutAsync(OrderGuid, "d-none", WarehouseOrderPickStockoutReasons.LocationEmpty, Alex);
        await service.SubmitAsync(OrderGuid, Alex);
        var afterSubmit = await service.MarkStockoutAsync(OrderGuid, "d-sponge", WarehouseOrderPickStockoutReasons.LocationEmpty, Alex);

        Assert.Equal(WarehousePickingErrorCodes.LineAlreadyComplete, complete.ErrorCode);
        Assert.Equal(36, complete.Data!.Line.PickedTotal);
        Assert.Equal(WarehousePickingErrorCodes.InvalidRequest, invalidReason.ErrorCode);
        Assert.Equal(WarehousePickingErrorCodes.LineNotFound, missingLine.ErrorCode);
        Assert.Equal(WarehousePickingErrorCodes.SessionSubmitted, afterSubmit.ErrorCode);
        Assert.Equal(0, await _db.Queryable<WarehouseOrderPickStockout>().CountAsync());
    }

    [Fact]
    public async Task 又拣到货时没货标记自动失效_减少数量不影响标记()
    {
        await SeedStandardOrderAsync(flowStatus: 3);
        var service = CreateService();
        await service.JoinAsync(OrderGuid, Alex);
        await service.SetLineTotalAsync(OrderGuid, "d-cup", SetTotal(24, 0), Alex);
        await service.MarkStockoutAsync(OrderGuid, "d-cup", WarehouseOrderPickStockoutReasons.WrongProduct, Alex);

        var decrement = await service.AppendRecordAsync(OrderGuid, Button("d-cup", WarehouseOrderPickSources.Decrement), Alex);
        Assert.NotNull(decrement.Data!.Line.Stockout);

        var scanned = await service.AppendRecordAsync(OrderGuid, Scan("d-cup", "9312345678905"), Mia);

        Assert.True(scanned.Success, scanned.Message);
        Assert.Null(scanned.Data!.Line.Stockout);
        var row = await _db.Queryable<WarehouseOrderPickStockout>().SingleAsync();
        Assert.Equal("Mia Wong", row.ClearedByName);
    }

    [Fact]
    public async Task ListOrders_只列已提交与配货中且未提交拣货的订单_支持筛选计数与分店名搜索()
    {
        await SeedStoreAsync();
        await SeedOrderAsync("o-cart", "2026-0001", 0, new DateTime(2026, 9, 20));
        await SeedOrderAsync("o-new", "2026-0002", 1, new DateTime(2026, 9, 28));
        await SeedOrderAsync("o-picking", "2026-0003", 3, new DateTime(2026, 9, 27));
        await SeedOrderAsync("o-done", "2026-0004", 2, new DateTime(2026, 9, 26));
        await SeedOrderAsync("o-submitted-pick", "2026-0005", 3, new DateTime(2026, 9, 25));
        await _db.Insertable(new WarehouseOrderPickSession
        {
            OrderGUID = "o-submitted-pick",
            Status = WarehouseOrderPickSessionStatuses.Submitted,
            StartedByName = "Alex Chen",
        }).ExecuteCommandAsync();
        var service = CreateService();

        var all = await service.ListOrdersAsync("all", null);
        var picking = await service.ListOrdersAsync("picking", null);
        var byStoreName = await service.ListOrdersAsync("all", "Moray");

        Assert.Equal(new[] { "o-new", "o-picking" }, all.Data!.Items.Select(item => item.OrderGuid));
        Assert.Equal(2, all.Data.Counts.All);
        Assert.Equal(1, all.Data.Counts.ToPick);
        Assert.Equal(1, all.Data.Counts.Picking);
        Assert.Equal("Morayfield", all.Data.Items[0].StoreName);
        Assert.Equal(new[] { "o-picking" }, picking.Data!.Items.Select(item => item.OrderGuid));
        Assert.Equal(2, byStoreName.Data!.Items.Count);
    }

    [Fact]
    public async Task ResolveOrder_支持二维码前缀与手输订单号_不存在或不可拣时报错()
    {
        await SeedStandardOrderAsync(flowStatus: 1);
        await SeedOrderAsync("o-done", "2026-0999", 2, new DateTime(2026, 9, 1));
        var service = CreateService();

        var byQr = await service.ResolveOrderAsync("hbso:2026-0418");
        var byNumber = await service.ResolveOrderAsync(" 2026-0418 ");
        var missing = await service.ResolveOrderAsync("HBSO:2026-9999");
        var done = await service.ResolveOrderAsync("HBSO:2026-0999");

        Assert.Equal(OrderGuid, byQr.Data!.OrderGuid);
        Assert.Equal(OrderGuid, byNumber.Data!.OrderGuid);
        Assert.Equal(404, missing.StatusCode);
        Assert.Equal(WarehousePickingErrorCodes.OrderNotPickable, done.ErrorCode);
    }

    [Fact]
    public async Task SetMinOrderQuantity_员工只能补录缺失值_已有值须经理才能改()
    {
        await SeedStandardOrderAsync(flowStatus: 3);
        _productWarehouseService
            .Setup(service => service.PatchAsync(
                It.IsAny<string>(),
                It.IsAny<WarehouseProductPatchDto>(),
                It.IsAny<string?>()
            ))
            .ReturnsAsync(new WarehouseProductPatchResultDto { Success = true });
        var service = CreateService();

        var staffOverwrite = await service.SetMinOrderQuantityAsync(OrderGuid, "d-cup", 6, Alex);
        var staffFill = await service.SetMinOrderQuantityAsync(OrderGuid, "d-sponge", 12, Alex);
        var managerOverwrite = await service.SetMinOrderQuantityAsync(OrderGuid, "d-cup", 6, Manager);

        Assert.Equal(WarehousePickingErrorCodes.MinOrderQuantityAlreadySet, staffOverwrite.ErrorCode);
        Assert.Equal(12, staffOverwrite.Data!.MinOrderQuantity);
        Assert.True(staffFill.Success, staffFill.Message);
        Assert.True(managerOverwrite.Success, managerOverwrite.Message);
        _productWarehouseService.Verify(service => service.PatchAsync(
            "P-SPONGE",
            It.Is<WarehouseProductPatchDto>(dto => dto.MinOrderQuantity == 12),
            "Alex Chen"
        ), Times.Once);
        _productWarehouseService.Verify(service => service.PatchAsync(
            "P-CUP",
            It.Is<WarehouseProductPatchDto>(dto => dto.MinOrderQuantity == 6),
            "Morgan Lee"
        ), Times.Once);
    }

    [Fact]
    public async Task 中包数为0与为空同样按缺失处理_扫码拒绝按中包计_员工可以补录()
    {
        await SeedStandardOrderAsync(flowStatus: 3);
        await _db.Updateable<WarehouseProduct>()
            .SetColumns(product => new WarehouseProduct { MinOrderQuantity = 0 })
            .Where(product => product.ProductCode == "P-SPONGE")
            .ExecuteCommandAsync();
        _productWarehouseService
            .Setup(service => service.PatchAsync("P-SPONGE", It.IsAny<WarehouseProductPatchDto>(), It.IsAny<string?>()))
            .ReturnsAsync(new WarehouseProductPatchResultDto { Success = true });
        var service = CreateService();
        await service.JoinAsync(OrderGuid, Alex);

        var scan = await service.AppendRecordAsync(OrderGuid, Scan("d-sponge", "9312345612343"), Alex);
        var fill = await service.SetMinOrderQuantityAsync(OrderGuid, "d-sponge", 12, Alex);

        Assert.Equal(WarehousePickingErrorCodes.MinOrderQuantityMissing, scan.ErrorCode);
        Assert.True(fill.Success, fill.Message);
        _productWarehouseService.Verify(service => service.PatchAsync(
            "P-SPONGE",
            It.Is<WarehouseProductPatchDto>(dto => dto.MinOrderQuantity == 12),
            "Alex Chen"
        ), Times.Once);
    }

    [Fact]
    public async Task LookupCode_扫到非本单商品时返回商品与配货位_套装子码回到主商品()
    {
        await SeedStandardOrderAsync(flowStatus: 3);
        var service = CreateService();

        var direct = await service.LookupCodeAsync("9312345678905");
        // 大号子项没有独立商品档案：按套装子码回到主商品。
        var child = await service.LookupCodeAsync("6901234567878");
        var missing = await service.LookupCodeAsync("0000000000000");

        Assert.Equal("P-CUP", direct.Data!.ProductCode);
        Assert.Equal("A-03-12-02", direct.Data.LocationCode);
        Assert.Equal("P-SET", child.Data!.ProductCode);
        Assert.Equal(WarehousePickingErrorCodes.CodeNotFound, missing.ErrorCode);
    }

    private static WarehousePickingCodeDto AssertCode(
        WarehousePickingSheetDto sheet,
        string code,
        string target,
        string detailGuid,
        int? matchedBy
    )
    {
        var entry = Assert.Single(sheet.Codes, item => item.Code == code && item.Target == target);
        Assert.Contains(detailGuid, entry.DetailGuids);
        Assert.Equal(matchedBy, entry.MatchedBy);
        return entry;
    }

    private static WarehousePickingRecordRequestDto Scan(string detailGuid, string code) => new()
    {
        DetailGuid = detailGuid,
        Source = WarehouseOrderPickSources.Scan,
        ScannedCode = code,
        MatchedBy = WarehouseOrderPickMatchKinds.Barcode,
        ClientRequestId = Guid.NewGuid(),
    };

    private static WarehousePickingRecordRequestDto Button(string detailGuid, int source) => new()
    {
        DetailGuid = detailGuid,
        Source = source,
        ClientRequestId = Guid.NewGuid(),
    };

    private static WarehousePickingSetTotalRequestDto SetTotal(int total, int expectedTotal) => new()
    {
        Total = total,
        ExpectedTotal = expectedTotal,
        ClientRequestId = Guid.NewGuid(),
    };

    private async Task SeedStandardOrderAsync(int flowStatus)
    {
        await SeedStoreAsync();
        await SeedOrderAsync(OrderGuid, "2026-0418", flowStatus, new DateTime(2026, 9, 29, 9, 12, 0));
        await _db.Insertable(new List<WareHouseOrderDetails>
        {
            Detail("d-cup", "P-CUP", 36, 12.99m),
            Detail("d-sponge", "P-SPONGE", 24, 3.99m),
            Detail("d-set", "P-SET", 12, 15.99m),
            Detail("d-extra", "P-EXTRA", 0, 2.99m),
        }).ExecuteCommandAsync();
        await _db.Insertable(new List<Product>
        {
            ProductRow("P-CUP", "HB20931", "9312345678905", "Stainless Steel Tumbler", productType: 0),
            ProductRow("P-SPONGE", "HB18806", "9312345612343", "Dish Sponge 6 Pack", productType: 0),
            ProductRow("P-SET", "HB31207", "9312345312076", "Storage Box Set of 3", productType: 1),
            ProductRow("P-EXTRA", "HB99999", "9312345999999", "Extra", productType: 0),
            ProductRow("P-SET-S", "HB31207-S", "6901234567892", "Small box", productType: 0),
        }).ExecuteCommandAsync();
        await _db.Insertable(new List<WarehouseProduct>
        {
            new() { ProductCode = "P-CUP", MinOrderQuantity = 12, IsActive = true },
            new() { ProductCode = "P-SPONGE", MinOrderQuantity = null, IsActive = true },
            new() { ProductCode = "P-SET", MinOrderQuantity = 4, IsActive = true },
            new() { ProductCode = "P-EXTRA", MinOrderQuantity = 1, IsActive = true },
        }).ExecuteCommandAsync();
        await _db.Insertable(new List<ProductSetCode>
        {
            SetCode("P-SET", "P-SET-L", "HB31207-L", "6901234567878", setType: 1),
            SetCode("P-SET", "P-SET-M", "HB31207-M", "6901234567885", setType: 1),
            SetCode("P-SET", "P-SET-S", "HB31207-S", "6901234567892", setType: 1),
            SetCode("P-CUP", "P-CUP-ALT", "HB20931A", "MULTI-CUP", setType: 2),
        }).ExecuteCommandAsync();
        await _db.Insertable(new List<StoreMultiCodeProduct>
        {
            new() { StoreCode = StoreCode, ProductCode = "P-CUP", MultiBarcode = "STORE-MULTI-CUP" },
            new() { StoreCode = "9999", ProductCode = "P-CUP", MultiBarcode = "OTHER-STORE-MULTI" },
        }).ExecuteCommandAsync();
        await _db.Insertable(new Location
        {
            LocationGuid = "loc-1",
            LocationType = 1,
            LocationCode = "A-03-12-02",
            LocationBarcode = "2000000000017",
            Status = 1,
        }).ExecuteCommandAsync();
        await _db.Insertable(new ProductLocation { ProductCode = "P-CUP", LocationGuid = "loc-1" }).ExecuteCommandAsync();
    }

    private Task SeedStoreAsync() =>
        _db.Insertable(new Store { StoreGUID = "store-guid", StoreCode = StoreCode, StoreName = "Morayfield" })
            .ExecuteCommandAsync();

    private Task SeedOrderAsync(string orderGuid, string orderNo, int flowStatus, DateTime orderDate) =>
        _db.Insertable(new WareHouseOrder
        {
            OrderGUID = orderGuid,
            OrderNo = orderNo,
            StoreCode = StoreCode,
            FlowStatus = flowStatus,
            OrderDate = orderDate,
        }).ExecuteCommandAsync();

    private static WareHouseOrderDetails Detail(string detailGuid, string productCode, decimal quantity, decimal oemPrice) => new()
    {
        DetailGUID = detailGuid,
        OrderGUID = OrderGuid,
        StoreCode = StoreCode,
        ProductCode = productCode,
        Quantity = quantity,
        OEMPrice = oemPrice,
        ImportPrice = 1m,
    };

    private static Product ProductRow(string productCode, string itemNumber, string barcode, string name, int productType) => new()
    {
        ProductCode = productCode,
        ItemNumber = itemNumber,
        Barcode = barcode,
        ProductName = name,
        ProductType = productType,
    };

    private static ProductSetCode SetCode(string productCode, string childCode, string childItem, string childBarcode, int setType) => new()
    {
        ProductCode = productCode,
        SetProductCode = childCode,
        SetItemNumber = childItem,
        SetBarcode = childBarcode,
        SetType = setType,
    };

    private WarehousePickingService CreateService() =>
        new(CreateSqlSugarContext(_db), _productWarehouseService.Object, NullLogger<WarehousePickingService>.Instance);

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
