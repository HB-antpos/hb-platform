using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.WarehousePicking;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 拣货分配：按 M 型走位顺序把订单行按品种数切成连续段派给员工；只做引导，保存的是预览出来的逐行归属。
/// </summary>
public sealed class WarehousePickingAssignmentServiceTests : IDisposable
{
    private const string OrderGuid = "order-a";
    private const string StoreCode = "1013";

    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;
    private readonly Mock<IWarehousePickerService> _pickers = new();
    private static readonly WarehousePickingAssigner Manager = new("u-mgr", "Morgan Lee");

    public WarehousePickingAssignmentServiceTests()
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
        foreach (var (guid, name) in new[] { ("u-chen", "Chen Wei"), ("u-li", "Li Na"), ("u-wang", "Wang Fang") })
        {
            _pickers
                .Setup(service => service.GetEligibilityAsync(guid))
                .ReturnsAsync(new WarehousePickerEligibility(guid, name, true, false, "WarehouseStaff"));
        }

        _pickers
            .Setup(service => service.GetEligibilityAsync("u-gone"))
            .ReturnsAsync(new WarehousePickerEligibility("u-gone", "Gone", false, false, null));
    }

    [Fact]
    public void 走位排序与移动端M型同口径_按区排列层_不规范编码在后_无货位最后()
    {
        var sorted = WarehousePickingRoute.SortByRoute(new[]
        {
            RouteLine("d-none", null, "HB1"),
            RouteLine("d-old", "OLD-A01"),
            RouteLine("a4-07", "A-04-07-01"),
            RouteLine("a4-18", "A-04-18-03"),
            RouteLine("a3-15", "A-03-15-01"),
            RouteLine("a3-12-l2", "A-03-12-02"),
            RouteLine("a3-12-l1", "A-03-12-01"),
            RouteLine("b1-02", "B-01-02-01"),
            RouteLine("x10", "A-10-01"),
        });

        Assert.Equal(
            new[] { "a3-12-l1", "a3-12-l2", "a3-15", "a4-07", "a4-18", "x10", "b1-02", "d-old", "d-none" },
            sorted.Select(line => line.DetailGuid).ToArray()
        );
        Assert.Equal(new WarehouseParsedLocation("A", 3, "03", 12, 2), WarehousePickingRoute.ParseLocationCode("a-03-12-02, B-01"));
        Assert.Null(WarehousePickingRoute.ParseLocationCode("A-01-02-03-04"));
        Assert.Null(WarehousePickingRoute.ParseLocationCode("LOC-PRIORITY"));
    }

    [Fact]
    public void 按品种数平均分_余数从前往后每人多一个_连续切段首尾相接()
    {
        Assert.Equal(new[] { 4, 3, 3 }, WarehousePickingRoute.SplitEvenly(10, 3));
        Assert.Equal(new[] { 1, 1, 0 }, WarehousePickingRoute.SplitEvenly(2, 3));
        var lines = Enumerable.Range(1, 5).Select(index => RouteLine($"d-{index}", $"A-01-0{index}")).ToList();

        var segments = WarehousePickingRoute.SplitContiguous(lines, new[] { 2, 0, 3 });

        Assert.Equal(new[] { "d-1", "d-2" }, segments[0].Select(line => line.DetailGuid));
        Assert.Empty(segments[1]);
        Assert.Equal(new[] { "d-3", "d-4", "d-5" }, segments[2].Select(line => line.DetailGuid));
    }

    [Fact]
    public void 分单条码_生成与解析往返_非分单或格式不对返回空_版本严格递增()
    {
        var code = WarehousePickingRules.FormatSlipCode("so260930012", 2, 23_456_789);

        Assert.Equal("HBSP:SO260930012/2/DYRDH", code);
        Assert.Equal(("SO260930012", 2, 23_456_789), WarehousePickingRules.ParseSlipCode($" {code.ToLowerInvariant()}\r"));
        foreach (var invalid in new[] { "HBSO:SO1", "HBSP:SO1/0/A", "HBSP:SO1/1", "HBSP:/1/A", "HBSP:SO1/1/A-B", "HBSP:SO1/1/ZZZZZZZ", "", null })
        {
            Assert.Null(WarehousePickingRules.ParseSlipCode(invalid));
        }

        var now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var first = WarehousePickingRules.NextAssignmentVersion(null, now);
        Assert.Equal((int)(now - new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds, first);
        Assert.Equal(first + 1, WarehousePickingRules.NextAssignmentVersion(first, now));
        Assert.Equal(first + 5, WarehousePickingRules.NextAssignmentVersion(first, now.AddSeconds(5)));
    }

    [Fact]
    public async Task Preview_默认平均分成走位上的连续段_带起止货位与无货位行数()
    {
        await SeedOrderAsync();

        var preview = await CreateService().PreviewAsync(OrderGuid, Pickers(("u-chen", null), ("u-li", null), ("u-wang", null)));

        Assert.True(preview.Success, preview.Message);
        Assert.Equal(7, preview.Data!.LineCount);
        Assert.Equal(1, preview.Data.UnlocatedLineCount);
        Assert.Equal(1, preview.Data.IrregularLineCount);
        var segments = preview.Data.Segments;
        Assert.Equal(new[] { 3, 2, 2 }, segments.Select(segment => segment.LineCount));
        Assert.Equal(new[] { "d-a3-12", "d-a3-15", "d-a4-07" }, segments[0].DetailGuids);
        Assert.Equal("A-03-12-02", segments[0].FirstLocation);
        Assert.Equal("A-04-07-01", segments[0].LastLocation);
        Assert.Equal(new[] { "d-a4-18", "d-b1-02" }, segments[1].DetailGuids);
        Assert.Equal(new[] { "d-old", "d-none" }, segments[2].DetailGuids);
        Assert.Equal(1, segments[2].UnlocatedLineCount);
        Assert.Equal(1, segments[2].IrregularLineCount);
        Assert.Equal("OLD-A01", segments[2].FirstLocation);
        Assert.Equal(0, await _db.Queryable<WarehouseOrderPickAssignment>().CountAsync());
    }

    [Fact]
    public async Task Preview_经理调整各人品种数_总数不符或员工不可拣时拒绝()
    {
        await SeedOrderAsync();
        var service = CreateService();

        var adjusted = await service.PreviewAsync(OrderGuid, Pickers(("u-chen", 5), ("u-li", 2)));
        var wrongSum = await service.PreviewAsync(OrderGuid, Pickers(("u-chen", 5), ("u-li", 1)));
        var mixed = await service.PreviewAsync(OrderGuid, Pickers(("u-chen", 5), ("u-li", null)));
        var ineligible = await service.PreviewAsync(OrderGuid, Pickers(("u-chen", null), ("u-gone", null)));
        var duplicated = await service.PreviewAsync(OrderGuid, Pickers(("u-chen", null), ("U-CHEN", null)));

        Assert.Equal(new[] { 5, 2 }, adjusted.Data!.Segments.Select(segment => segment.LineCount));
        Assert.Equal(WarehousePickingErrorCodes.AssignCountsInvalid, wrongSum.ErrorCode);
        Assert.Equal(WarehousePickingErrorCodes.AssignCountsInvalid, mixed.ErrorCode);
        Assert.Equal(WarehousePickingErrorCodes.AssignPickerInvalid, ineligible.ErrorCode);
        Assert.Equal(WarehousePickingErrorCodes.AssignPickerInvalid, duplicated.ErrorCode);
    }

    [Fact]
    public async Task Save_按预览逐行保存_拣货单与进度带负责人_派给我的列表与计数_重新分配整单替换()
    {
        await SeedOrderAsync();
        var service = CreateService();
        var preview = await service.PreviewAsync(OrderGuid, Pickers(("u-chen", null), ("u-li", null)));

        var saved = await service.SaveAsync(OrderGuid, FromPreview(preview.Data!), Manager);

        Assert.True(saved.Success, saved.Message);
        Assert.Equal(0, saved.Data!.UnassignedLineCount);
        Assert.Equal(new[] { ("u-chen", 4), ("u-li", 3) }, saved.Data.Assignees.Select(item => (item.PickerUserGuid, item.LineCount)));
        Assert.Equal("Morgan Lee", saved.Data.AssignedByName);

        var picking = CreatePickingService();
        var sheet = await picking.GetSheetAsync(OrderGuid);
        Assert.Equal("Chen Wei", sheet.Data!.Lines.Single(line => line.DetailGuid == "d-a3-12").AssigneeName);
        Assert.Equal("u-li", sheet.Data.Lines.Single(line => line.DetailGuid == "d-none").AssigneeUserGuid);
        await picking.JoinAsync(OrderGuid, new WarehousePickerContext("u-chen", "Chen Wei", false, "u-chen", null));
        var progress = await picking.GetProgressAsync(OrderGuid);
        Assert.Equal("u-chen", progress.Data!.Lines.Single(line => line.DetailGuid == "d-a3-15").AssigneeUserGuid);

        var mine = await picking.ListOrdersAsync("mine", null, "u-li");
        var others = await picking.ListOrdersAsync("mine", null, "u-wang");
        var unknown = await picking.ListOrdersAsync("all", null, null);
        Assert.Equal(OrderGuid, Assert.Single(mine.Data!.Items).OrderGuid);
        Assert.Equal(1, mine.Data.Counts.Mine);
        Assert.Empty(others.Data!.Items);
        Assert.Equal(0, others.Data.Counts.Mine);
        Assert.Null(unknown.Data!.Counts.Mine);
        Assert.Equal(new[] { "Chen Wei", "Li Na" }, unknown.Data.Items.Single().Assignees.Select(item => item.PickerName));

        // 重新分配：整单替换，旧负责人不再残留。
        var reassigned = await service.PreviewAsync(OrderGuid, Pickers(("u-wang", null)));
        await service.SaveAsync(OrderGuid, FromPreview(reassigned.Data!), Manager);
        var rows = await _db.Queryable<WarehouseOrderPickAssignment>().ToListAsync();
        Assert.Equal(7, rows.Count);
        Assert.All(rows, row => Assert.Equal("u-wang", row.PickerUserGuid));

        var cleared = await service.ClearAsync(OrderGuid);
        Assert.Empty(cleared.Data!.Assignees);
        Assert.Equal(7, cleared.Data.UnassignedLineCount);
    }

    [Fact]
    public async Task 段号按员工顺序_分单条码可解析到订单与段_重新分配或撤销后旧分单失效()
    {
        await SeedOrderAsync();
        var service = CreateService();
        var preview = await service.PreviewAsync(OrderGuid, Pickers(("u-li", null), ("u-chen", null)));
        var saved = await service.SaveAsync(OrderGuid, FromPreview(preview.Data!), Manager);

        Assert.Equal(new[] { (1, "u-li"), (2, "u-chen") }, saved.Data!.Assignees.Select(item => (item.SegmentNo, item.PickerUserGuid)));
        var chenCode = saved.Data.Assignees[1].SlipCode!;
        Assert.StartsWith("HBSP:2026-0418/2/", chenCode);
        var sheet = await CreatePickingService().GetSheetAsync(OrderGuid);
        Assert.Equal(1, sheet.Data!.Lines.Single(line => line.DetailGuid == "d-a3-12").AssignmentSegmentNo);
        Assert.Equal(2, sheet.Data.Lines.Single(line => line.DetailGuid == "d-none").AssignmentSegmentNo);

        var resolved = await service.ResolveSlipAsync(chenCode);
        Assert.True(resolved.Success, resolved.Message);
        Assert.Equal(OrderGuid, resolved.Data!.OrderGuid);
        Assert.Equal((2, 2, "u-chen", 3), (resolved.Data.SegmentNo, resolved.Data.SegmentCount, resolved.Data.PickerUserGuid, resolved.Data.LineCount));

        var wrongSegment = await service.ResolveSlipAsync(chenCode.Replace("/2/", "/3/"));
        var notSlip = await service.ResolveSlipAsync("HBSO:2026-0418");
        Assert.Equal(WarehousePickingErrorCodes.SlipStale, wrongSegment.ErrorCode);
        Assert.Equal(WarehousePickingErrorCodes.InvalidRequest, notSlip.ErrorCode);

        // 同样的人同样的分法重新保存，版本也会变：旧分单失效，新分单可用。
        var again = await service.SaveAsync(OrderGuid, FromPreview(preview.Data!), Manager);
        Assert.Equal(WarehousePickingErrorCodes.SlipStale, (await service.ResolveSlipAsync(chenCode)).ErrorCode);
        var newCode = again.Data!.Assignees[1].SlipCode!;
        Assert.NotEqual(chenCode, newCode);
        Assert.True((await service.ResolveSlipAsync(newCode)).Success);

        await service.ClearAsync(OrderGuid);
        Assert.Equal(WarehousePickingErrorCodes.SlipStale, (await service.ResolveSlipAsync(newCode)).ErrorCode);
    }

    [Fact]
    public async Task 分单打印数据_每段一页_行按走位顺序带区排_列出同单其他负责人_可只打一段()
    {
        await SeedOrderAsync();
        var service = CreateService();
        Assert.Equal(WarehousePickingErrorCodes.AssignLinesInvalid, (await service.GetSlipsAsync(OrderGuid, null)).ErrorCode);
        var preview = await service.PreviewAsync(OrderGuid, Pickers(("u-chen", 4), ("u-li", 3)));
        await service.SaveAsync(OrderGuid, FromPreview(preview.Data!), Manager);

        var slips = await service.GetSlipsAsync(OrderGuid, null);
        var single = await service.GetSlipsAsync(OrderGuid, 2);
        var missing = await service.GetSlipsAsync(OrderGuid, 5);

        Assert.True(slips.Success, slips.Message);
        Assert.Equal("Morayfield", slips.Data!.StoreName);
        Assert.Equal("Morgan Lee", slips.Data.AssignedByName);
        var first = slips.Data.Slips[0];
        Assert.Equal((1, 2, "Chen Wei", 4, 48m), (first.SegmentNo, first.SegmentCount, first.PickerName, first.LineCount, first.Pieces));
        Assert.Equal(new[] { "d-a3-12", "d-a3-15", "d-a4-07", "d-a4-18" }, first.Lines.Select(line => line.DetailGuid));
        Assert.Equal(("A", "03"), (first.Lines[0].Zone, first.Lines[0].RowLabel));
        Assert.Equal(("A", "04"), (first.Lines[2].Zone, first.Lines[2].RowLabel));
        Assert.Equal(("A-03-12-02", "A-04-18-03"), (first.FirstLocation, first.LastLocation));
        Assert.Equal(new[] { "Li Na" }, first.OtherPickerNames);
        var second = Assert.Single(single.Data!.Slips);
        Assert.Equal(new[] { "d-b1-02", "d-old", "d-none" }, second.Lines.Select(line => line.DetailGuid));
        Assert.Null(second.Lines[1].Zone);
        Assert.Equal(WarehousePickingErrorCodes.InvalidRequest, missing.ErrorCode);
    }

    [Fact]
    public async Task 分配概况带每行所属段与每人进度_列表批量查询返回各单负责人()
    {
        await SeedOrderAsync();
        var service = CreateService();
        var preview = await service.PreviewAsync(OrderGuid, Pickers(("u-chen", 4), ("u-li", 3)));
        await service.SaveAsync(OrderGuid, FromPreview(preview.Data!), Manager);
        var picking = CreatePickingService();
        var chen = new WarehousePickerContext("u-chen", "Chen Wei", false, "u-chen", null);
        await picking.JoinAsync(OrderGuid, chen);
        await picking.SetLineTotalAsync(OrderGuid, "d-a3-12", new WarehousePickingSetTotalRequestDto { Total = 12, ExpectedTotal = 0, ClientRequestId = Guid.NewGuid() }, chen);
        await picking.SetLineTotalAsync(OrderGuid, "d-a3-15", new WarehousePickingSetTotalRequestDto { Total = 5, ExpectedTotal = 0, ClientRequestId = Guid.NewGuid() }, chen);
        await picking.MarkStockoutAsync(OrderGuid, "d-a3-15", WarehouseOrderPickStockoutReasons.LocationEmpty, chen);

        var summary = (await service.GetAsync(OrderGuid)).Data!;
        var first = summary.Assignees[0];
        var second = summary.Assignees[1];

        Assert.Equal((48m, 17, 1, 1), (first.Pieces!.Value, first.PickedPieces!.Value, first.CompletedLineCount!.Value, first.StockoutLineCount!.Value));
        Assert.Equal(("A-03-12-02", "A-04-18-03"), (first.FirstLocation, first.LastLocation));
        Assert.NotNull(first.LastActiveAtUtc);
        Assert.Null(second.LastActiveAtUtc);
        Assert.Equal(("B-01-02-01", "OLD-A01"), (second.FirstLocation, second.LastLocation));
        Assert.Equal(7, summary.Lines.Count);
        Assert.Equal(2, summary.Lines.Single(line => line.DetailGuid == "d-none").SegmentNo);

        var summaries = await service.ListSummariesAsync(new[] { OrderGuid, "order-none", OrderGuid });
        Assert.Equal(new[] { ("Chen Wei", 1, 4), ("Li Na", 2, 3) }, summaries.Data![OrderGuid].Select(item => (item.PickerName, item.SegmentNo, item.LineCount)));
        Assert.False(summaries.Data.ContainsKey("order-none"));
        Assert.Equal(
            WarehousePickingErrorCodes.InvalidRequest,
            (await service.ListSummariesAsync(Enumerable.Range(0, 201).Select(index => $"o-{index}"))).ErrorCode
        );
    }

    [Fact]
    public async Task 不选员工按份数分段_分单印待领取_员工扫码领取_别人再扫不改负责人()
    {
        await SeedOrderAsync();
        var service = CreateService();
        var preview = await service.PreviewAsync(OrderGuid, Pickers((null, null), (null, null), (null, null)));
        Assert.True(preview.Success, preview.Message);
        Assert.All(preview.Data!.Segments, segment => Assert.Null(segment.PickerUserGuid));
        var saved = await service.SaveAsync(OrderGuid, FromPreview(preview.Data), Manager);

        Assert.Equal(new[] { 1, 2, 3 }, saved.Data!.Assignees.Select(item => item.SegmentNo));
        Assert.All(saved.Data.Assignees, item => Assert.Null(item.PickerUserGuid));
        var slips = (await service.GetSlipsAsync(OrderGuid, 2)).Data!.Slips.Single();
        Assert.Null(slips.PickerName);
        Assert.Equal(new[] { "第1段待领取", "第3段待领取" }, slips.OtherPickerNames);
        var code = saved.Data.Assignees[1].SlipCode!;

        var alex = new WarehousePickerContext("u-chen", "Chen Wei", false, "u-chen", null);
        var li = new WarehousePickerContext("u-li", "Li Na", false, null, "PDA-02");
        var claimed = await service.ClaimSlipAsync(code, alex);
        var again = await service.ClaimSlipAsync(code, alex);
        var other = await service.ClaimSlipAsync(code, li);

        Assert.True(claimed.Success, claimed.Message);
        Assert.Equal((true, true, "Chen Wei", 2), (claimed.Data!.ClaimedNow, claimed.Data.ClaimedByMe, claimed.Data.PickerName, claimed.Data.SegmentNo));
        Assert.Equal((false, true), (again.Data!.ClaimedNow, again.Data.ClaimedByMe));
        Assert.Equal((false, false, "u-chen"), (other.Data!.ClaimedNow, other.Data.ClaimedByMe, other.Data.PickerUserGuid));
        var picking = CreatePickingService();
        Assert.Equal(1, (await picking.ListOrdersAsync("mine", null, "u-chen")).Data!.Counts.Mine);
        Assert.Equal(0, (await picking.ListOrdersAsync("mine", null, "u-li")).Data!.Counts.Mine);
        var sheet = (await picking.GetSheetAsync(OrderGuid)).Data!;
        Assert.All(sheet.Lines.Where(line => line.AssignmentSegmentNo == 2), line => Assert.Equal("u-chen", line.AssigneeUserGuid));
        Assert.All(sheet.Lines.Where(line => line.AssignmentSegmentNo != 2), line => Assert.Null(line.AssigneeUserGuid));

        // 重新分配后旧分单领取不了。
        await service.SaveAsync(OrderGuid, FromPreview(preview.Data), Manager);
        Assert.Equal(WarehousePickingErrorCodes.SlipStale, (await service.ClaimSlipAsync(code, li)).ErrorCode);
    }

    [Fact]
    public async Task 经理改领取人或释放_不改版本分单仍有效_指定部分员工混合分段()
    {
        await SeedOrderAsync();
        var service = CreateService();
        var mixed = await service.PreviewAsync(OrderGuid, Pickers(("u-chen", null), (null, null)));
        Assert.Equal(new[] { "u-chen", null }, mixed.Data!.Segments.Select(segment => segment.PickerUserGuid));
        var saved = await service.SaveAsync(OrderGuid, FromPreview(mixed.Data), Manager);
        var code = saved.Data!.Assignees[1].SlipCode!;

        var assigned = await service.SetSegmentPickerAsync(OrderGuid, 2, "u-li");
        Assert.Equal("Li Na", assigned.Data!.Assignees[1].PickerName);
        Assert.Equal(code, assigned.Data.Assignees[1].SlipCode);
        var released = await service.SetSegmentPickerAsync(OrderGuid, 1, null);
        Assert.Null(released.Data!.Assignees[0].PickerUserGuid);
        Assert.Equal(WarehousePickingErrorCodes.AssignPickerInvalid, (await service.SetSegmentPickerAsync(OrderGuid, 1, "u-gone")).ErrorCode);
        Assert.Equal(WarehousePickingErrorCodes.InvalidRequest, (await service.SetSegmentPickerAsync(OrderGuid, 9, "u-li")).ErrorCode);

        var claim = await service.ClaimSlipAsync(code, new WarehousePickerContext("u-wang", "Wang Fang", false, null, null));
        Assert.True(claim.Success, claim.Message);
        Assert.Equal(("u-li", false), (claim.Data!.PickerUserGuid, claim.Data.ClaimedByMe));
    }

    [Fact]
    public async Task 批量按份数分_全部待领取_份数为零或超过十份时拒绝()
    {
        await SeedOrderAsync();
        var service = CreateService();

        var batch = await service.AssignEvenlyAsync(
            new WarehousePickingBatchAssignRequestDto { OrderGuids = new List<string> { OrderGuid }, SegmentCount = 3 },
            Manager
        );
        var none = await service.AssignEvenlyAsync(
            new WarehousePickingBatchAssignRequestDto { OrderGuids = new List<string> { OrderGuid }, SegmentCount = 0 },
            Manager
        );
        var tooMany = await service.AssignEvenlyAsync(
            new WarehousePickingBatchAssignRequestDto { OrderGuids = new List<string> { OrderGuid }, SegmentCount = 11 },
            Manager
        );

        Assert.True(batch.Data!.Items.Single().Success);
        var rows = await _db.Queryable<WarehouseOrderPickAssignment>().ToListAsync();
        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(row => row.SegmentNo).Distinct().OrderBy(no => no));
        Assert.All(rows, row => Assert.Null(row.PickerUserGuid));
        Assert.Equal(WarehousePickingErrorCodes.AssignPickerInvalid, none.ErrorCode);
        Assert.Equal(WarehousePickingErrorCodes.AssignPickerInvalid, tooMany.ErrorCode);
    }

    [Fact]
    public async Task 批量派单_品种比人少时跳过空段_段号保持连续()
    {
        await _db.Insertable(new Store { StoreGUID = "store-guid", StoreCode = StoreCode, StoreName = "Morayfield" }).ExecuteCommandAsync();
        await SeedOrderHeaderAsync("order-small", "2026-0421", flowStatus: 1);
        await _db.Insertable(new List<WareHouseOrderDetails> { Detail("order-small", "d-s1", "P-S1", 6), Detail("order-small", "d-s2", "P-S2", 6) })
            .ExecuteCommandAsync();

        var batch = await CreateService().AssignEvenlyAsync(
            new WarehousePickingBatchAssignRequestDto
            {
                OrderGuids = new List<string> { "order-small" },
                PickerUserGuids = new List<string> { "u-chen", "u-li", "u-wang" },
            },
            Manager
        );

        Assert.True(batch.Data!.Items.Single().Success);
        var rows = await _db.Queryable<WarehouseOrderPickAssignment>().OrderBy(row => row.SegmentNo).ToListAsync();
        Assert.Equal(new[] { (1, "u-chen"), (2, "u-li") }, rows.Select(row => (row.SegmentNo, row.PickerUserGuid)));
        Assert.Single(rows.Select(row => row.AssignmentVersion).Distinct());
    }

    [Fact]
    public async Task Save_行不属于本单或同一行派两次时拒绝且不改原分配_部分分配允许()
    {
        await SeedOrderAsync();
        var service = CreateService();
        await service.SaveAsync(OrderGuid, Assign(("u-chen", new[] { "d-a3-12" })), Manager);

        var foreign = await service.SaveAsync(OrderGuid, Assign(("u-li", new[] { "d-a3-15", "d-other" })), Manager);
        var twice = await service.SaveAsync(OrderGuid, Assign(("u-li", new[] { "d-a3-15" }), ("u-wang", new[] { "d-a3-15" })), Manager);
        var empty = await service.SaveAsync(OrderGuid, Assign(("u-li", Array.Empty<string>())), Manager);

        Assert.Equal(WarehousePickingErrorCodes.AssignLinesInvalid, foreign.ErrorCode);
        Assert.Equal(WarehousePickingErrorCodes.AssignLinesInvalid, twice.ErrorCode);
        Assert.Equal(WarehousePickingErrorCodes.AssignLinesInvalid, empty.ErrorCode);
        var row = Assert.Single(await _db.Queryable<WarehouseOrderPickAssignment>().ToListAsync());
        Assert.Equal("u-chen", row.PickerUserGuid);
        var summary = await service.GetAsync(OrderGuid);
        Assert.Equal(6, summary.Data!.UnassignedLineCount);
    }

    [Fact]
    public async Task 已提交拣货或不可拣的订单不能派单_批量派单逐张返回结果()
    {
        await SeedOrderAsync();
        await SeedOrderHeaderAsync("order-b", "2026-0419", flowStatus: 1);
        await _db.Insertable(Detail("order-b", "d-b1", "P-A3-12", 6)).ExecuteCommandAsync();
        await SeedOrderHeaderAsync("order-cart", "2026-0420", flowStatus: 0);
        await _db.Insertable(new WarehouseOrderPickSession
        {
            OrderGUID = "order-b",
            Status = WarehouseOrderPickSessionStatuses.Submitted,
            StartedAtUtc = DateTime.UtcNow,
            StartedByName = "Chen Wei",
            SubmittedByName = "Chen Wei",
            UpdatedAtUtc = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        var service = CreateService();

        var submitted = await service.PreviewAsync("order-b", Pickers(("u-chen", null)));
        var batch = await service.AssignEvenlyAsync(
            new WarehousePickingBatchAssignRequestDto
            {
                OrderGuids = new List<string> { OrderGuid, "order-b", "order-cart", "order-missing" },
                PickerUserGuids = new List<string> { "u-chen", "u-li" },
            },
            Manager
        );

        Assert.Equal(WarehousePickingErrorCodes.SessionSubmitted, submitted.ErrorCode);
        Assert.True(batch.Success, batch.Message);
        Assert.Equal(
            new[] { true, false, false, false },
            batch.Data!.Items.Select(item => item.Success)
        );
        Assert.Equal(WarehousePickingErrorCodes.SessionSubmitted, batch.Data.Items[1].ErrorCode);
        Assert.Equal(WarehousePickingErrorCodes.OrderNotPickable, batch.Data.Items[2].ErrorCode);
        Assert.Equal(WarehousePickingErrorCodes.OrderNotFound, batch.Data.Items[3].ErrorCode);
        var rows = await _db.Queryable<WarehouseOrderPickAssignment>().ToListAsync();
        Assert.Equal(7, rows.Count);
        Assert.Equal(4, rows.Count(row => row.PickerUserGuid == "u-chen"));
    }

    [Fact]
    public async Task 候选员工附带手上仍在拣的已派订单数()
    {
        await SeedOrderAsync();
        _pickers
            .Setup(service => service.ListEligibleAsync())
            .ReturnsAsync(new List<WarehousePickerEligibility>
            {
                new("u-chen", "Chen Wei", true, false, "WarehouseStaff"),
                new("u-li", "Li Na", true, false, "WarehouseStaff"),
            });
        var service = CreateService();
        await service.SaveAsync(OrderGuid, Assign(("u-chen", new[] { "d-a3-12", "d-a3-15" })), Manager);

        var candidates = await service.ListCandidatesAsync();

        Assert.Equal(new[] { ("u-chen", 1), ("u-li", 0) }, candidates.Data!.Select(item => (item.PickerUserGuid, item.ActiveOrderCount)));
    }

    private static WarehouseRouteLine RouteLine(string detailGuid, string? location, string? itemNumber = null) =>
        new(detailGuid, location, itemNumber ?? detailGuid, detailGuid.ToUpperInvariant(), 12);

    private static WarehousePickingAssignmentPreviewRequestDto Pickers(params (string? Guid, int? Count)[] pickers) => new()
    {
        Pickers = pickers
            .Select(picker => new WarehousePickingAssignmentPickerInputDto { PickerUserGuid = picker.Guid, LineCount = picker.Count })
            .ToList(),
    };

    private static WarehousePickingAssignmentSaveRequestDto FromPreview(WarehousePickingAssignmentPreviewDto preview) => new()
    {
        Assignments = preview.Segments
            .Select(segment => new WarehousePickingAssignmentInputDto
            {
                PickerUserGuid = segment.PickerUserGuid,
                DetailGuids = segment.DetailGuids,
            })
            .ToList(),
    };

    private static WarehousePickingAssignmentSaveRequestDto Assign(params (string Guid, string[] Details)[] assignments) => new()
    {
        Assignments = assignments
            .Select(item => new WarehousePickingAssignmentInputDto { PickerUserGuid = item.Guid, DetailGuids = item.Details.ToList() })
            .ToList(),
    };

    /// <summary>7 行：5 行规范货位（跨 A-03、A-04、B-01）、1 行不规范编码、1 行未绑定货位。</summary>
    private async Task SeedOrderAsync()
    {
        await _db.Insertable(new Store { StoreGUID = "store-guid", StoreCode = StoreCode, StoreName = "Morayfield" }).ExecuteCommandAsync();
        await SeedOrderHeaderAsync(OrderGuid, "2026-0418", flowStatus: 1);
        var products = new[]
        {
            ("d-a4-18", "P-A4-18", "A-04-18-03"),
            ("d-none", "P-NONE", (string?)null),
            ("d-a3-15", "P-A3-15", "A-03-15-01"),
            ("d-old", "P-OLD", "OLD-A01"),
            ("d-b1-02", "P-B1-02", "B-01-02-01"),
            ("d-a3-12", "P-A3-12", "A-03-12-02"),
            ("d-a4-07", "P-A4-07", "A-04-07-01"),
        };
        foreach (var (detailGuid, productCode, location) in products)
        {
            await _db.Insertable(Detail(OrderGuid, detailGuid, productCode, 12)).ExecuteCommandAsync();
            await _db.Insertable(new Product { ProductCode = productCode, ItemNumber = productCode.Replace("P-", "HB"), ProductName = productCode })
                .ExecuteCommandAsync();
            if (location == null)
            {
                continue;
            }

            await _db.Insertable(new Location { LocationGuid = $"loc-{productCode}", LocationType = 1, LocationCode = location, Status = 1 })
                .ExecuteCommandAsync();
            await _db.Insertable(new ProductLocation { Guid = $"pl-{productCode}", ProductCode = productCode, LocationGuid = $"loc-{productCode}" }).ExecuteCommandAsync();
        }
    }

    private Task SeedOrderHeaderAsync(string orderGuid, string orderNo, int flowStatus) =>
        _db.Insertable(new WareHouseOrder
        {
            OrderGUID = orderGuid,
            OrderNo = orderNo,
            StoreCode = StoreCode,
            FlowStatus = flowStatus,
            OrderDate = new DateTime(2026, 9, 30, 9, 0, 0),
        }).ExecuteCommandAsync();

    private static WareHouseOrderDetails Detail(string orderGuid, string detailGuid, string productCode, decimal quantity) => new()
    {
        DetailGUID = detailGuid,
        OrderGUID = orderGuid,
        StoreCode = StoreCode,
        ProductCode = productCode,
        Quantity = quantity,
        OEMPrice = 1m,
        ImportPrice = 1m,
    };

    private WarehousePickingAssignmentService CreateService() =>
        new(CreateSqlSugarContext(_db), _pickers.Object, NullLogger<WarehousePickingAssignmentService>.Instance);

    private WarehousePickingService CreatePickingService() =>
        new(
            CreateSqlSugarContext(_db),
            new Mock<IProductWarehouseReactService>(MockBehavior.Strict).Object,
            NullLogger<WarehousePickingService>.Instance
        );

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
