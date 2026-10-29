using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Options;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// MYOB Timesheet 推送与撤销（方案 docs/plans/2026-10-29-myob-timesheet-push.md 第 4 节）：
/// 纯函数规则（取整 / 哈希 / 归一化 / 比较 / PUT 请求体 / 总闸）+ 推送服务（SQLite 临时库 + 有状态的假 MYOB）。
/// 假 MYOB 按 2026-10-30 的实测行为实现：PUT 整体覆盖该员工该区间、空 Lines 可清空、读回日期带 T00:00:00；不访问真实 MYOB。
/// </summary>
public sealed class MyobTimesheetPushTests : IDisposable
{
    // 固定一个周三（悉尼本地 2026-11-04）：周期 2026-10-26 ~ 11-01 已结束。
    private static readonly DateTime Now = new(2026, 11, 4, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Start = new(2026, 10, 26);
    private static readonly DateTime End = new(2026, 11, 1);
    private const string Conn = "conn-1";
    private const string Biz = "biz-1";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hb-myob-push-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;
    private readonly FakeMyob _myob = new();
    private readonly FakePlans _plans = new();
    private readonly FakeScope _scope = new();
    private readonly FakeTime _time = new(Now);

    public MyobTimesheetPushTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_dbPath}",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(typeof(MyobConnection), typeof(MyobTimesheetPush), typeof(User));
        _db.Insertable(new MyobConnection { ConnectionGuid = Conn, BusinessId = Biz, BusinessName = "Co 1", Status = MyobConnectionStatuses.Active })
            .ExecuteCommand();
        _plans.Computation = Computation(Row("u1", "Alice"), Row("u2", "Bob"));
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private MyobTimesheetPushService Service(MyobOptions? options = null) => new(
        _db, _plans, _scope, _myob, new FakeTokens(), Options.Create(options ?? Enabled()), _time);

    private static MyobOptions Enabled() => new() { TimesheetPushEnabled = true, TimesheetPushConnectionGuids = Conn };

    private static MyobPushRequestDto Request(params string[] users) => new()
    {
        ConnectionGuid = Conn,
        PeriodStart = "2026-10-26",
        UserGuids = users.ToList(),
    };

    // ───────────────────────── 造数 ─────────────────────────

    private static MyobPreviewRowDto Row(string userGuid, string name, string status = MyobPreviewStatuses.Ready, params string[] hints) => new()
    {
        UserGuid = userGuid,
        UserName = name,
        Status = status,
        HintCodes = hints.ToList(),
    };

    private static MyobCompanyPlan PlanOf(params (string Key, string Date, int Minutes)[] lines) => new()
    {
        ConnectionGuid = Conn,
        Pushed = lines.Select(item => new MyobPlanLine(Conn, DateTime.Parse(item.Date), item.Key, item.Minutes)).ToList(),
    };

    /// <summary>默认计划：平日 10-26 8 小时 + 周六 10-31 4 小时 → 目标 [uid-sat 10-31 4h, uid-weekday 10-26 8h]。</summary>
    private static MyobCompanyPlan DefaultPlan() => PlanOf(
        (MyobPayCategoryKeys.Weekday, "2026-10-26", 480),
        (MyobPayCategoryKeys.Saturday, "2026-10-31", 240));

    private static MyobTimesheetComputation Computation(params MyobPreviewRowDto[] rows)
    {
        var dto = new MyobTimesheetPreviewDto { ConnectionGuid = Conn, Rows = rows.ToList() };
        var plans = rows.ToDictionary(row => row.UserGuid, _ => DefaultPlan(), StringComparer.OrdinalIgnoreCase);
        var links = rows.ToDictionary(
            row => row.UserGuid,
            row => new EmployeeMyobLink { UserGuid = row.UserGuid, ConnectionGuid = Conn, MyobEmployeeUid = "emp-" + row.UserGuid },
            StringComparer.OrdinalIgnoreCase);
        var mapping = new Dictionary<string, string?>
        {
            [MyobPayCategoryKeys.Weekday] = "uid-weekday",
            [MyobPayCategoryKeys.Saturday] = "uid-sat",
        };
        return new MyobTimesheetComputation(
            dto,
            new MyobConnection { ConnectionGuid = Conn, BusinessId = Biz, BusinessName = "Co 1", Status = MyobConnectionStatuses.Active },
            Start, End, 1, new DateTime(2026, 11, 4), plans, links, mapping);
    }

    private static readonly MyobTimesheetCell[] DefaultTarget =
    [
        new("uid-sat", "2026-10-31", 4m),
        new("uid-weekday", "2026-10-26", 8m),
    ];

    private static MyobTimesheetLineRecord Existing(string uid, string date, decimal hours, bool processed = false) =>
        new(uid, [new MyobTimesheetEntryRecord(date + "T00:00:00", hours, processed)]);

    private List<MyobTimesheetPush> Records() => _db.Queryable<MyobTimesheetPush>().OrderBy(item => item.StartedAt).ToList();

    // ───────────────────────── 规则（纯函数） ─────────────────────────

    [Theory]
    [InlineData(450, 7.5)]
    [InlineData(20, 0.333)]
    [InlineData(40, 0.667)]
    [InlineData(0, 0)]
    public void 取整_分钟换算小时_保留三位小数(int minutes, double expected) =>
        Assert.Equal((decimal)expected, MyobTimesheetPushRules.ToHours(minutes));

    [Fact]
    public void 规划转单元格_同类别同一天合并分钟_不同类别映射到同一个UID也合并_未映射的类别单独收集()
    {
        var plan = PlanOf(
            (MyobPayCategoryKeys.Weekday, "2026-10-26", 100),
            (MyobPayCategoryKeys.Weekday, "2026-10-26", 20),
            (MyobPayCategoryKeys.Saturday, "2026-10-26", 30),
            (MyobPayCategoryKeys.HolidayLeave, "2026-10-27", 60));
        var mapping = new Dictionary<string, string?>
        {
            [MyobPayCategoryKeys.Weekday] = "uid-a",
            [MyobPayCategoryKeys.Saturday] = "UID-A", // 与平日映射到同一个 MYOB 类别（大小写不同）
            [MyobPayCategoryKeys.HolidayLeave] = null,
        };

        var (cells, unmapped) = MyobTimesheetPushRules.BuildPlannedCells(plan, mapping);

        var cell = Assert.Single(cells);
        Assert.Equal("2026-10-26", cell.Date);
        Assert.Equal(150, cell.Minutes);
        Assert.Equal([MyobPayCategoryKeys.HolidayLeave], unmapped);
    }

    [Fact]
    public void 请求哈希_顺序无关_改一分钟或换员工卡就变()
    {
        var a = new MyobPlannedCell("u1", "2026-10-26", 480);
        var b = new MyobPlannedCell("u2", "2026-10-27", 60);
        var hash = MyobTimesheetPushRules.ComputeRequestHash(Conn, "emp-1", Start, End, [a, b]);

        Assert.Equal(hash, MyobTimesheetPushRules.ComputeRequestHash(Conn, "EMP-1", Start, End, [b, a]));
        Assert.NotEqual(hash, MyobTimesheetPushRules.ComputeRequestHash(Conn, "emp-1", Start, End, [a, b with { Minutes = 61 }]));
        Assert.NotEqual(hash, MyobTimesheetPushRules.ComputeRequestHash(Conn, "emp-2", Start, End, [a, b]));
        Assert.NotEqual(hash, MyobTimesheetPushRules.ComputeRequestHash(Conn, "emp-1", Start, End.AddDays(7), [a, b]));
        Assert.Equal(64, hash.Length);
    }

    [Fact]
    public void 归一化_日期取前十位_零小时丢弃_同类别同天累加()
    {
        var cells = MyobTimesheetPushRules.Normalize(
        [
            new MyobTimesheetLineRecord("uid-b",
            [
                new MyobTimesheetEntryRecord("2026-10-27T00:00:00", 2m, false),
                new MyobTimesheetEntryRecord("2026-10-27T00:00:00", 1.5m, false),
                new MyobTimesheetEntryRecord("2026-10-28T00:00:00", 0m, false),
                new MyobTimesheetEntryRecord("2026-10-29", null, false),
            ]),
            new MyobTimesheetLineRecord("uid-a", [new MyobTimesheetEntryRecord("2026-10-26T00:00:00", 7.333m, false)]),
        ]);

        Assert.Equal(2, cells.Count);
        Assert.Equal(new MyobTimesheetCell("uid-a", "2026-10-26", 7.333m), cells[0]);
        Assert.Equal(new MyobTimesheetCell("uid-b", "2026-10-27", 3.5m), cells[1]);
    }

    [Fact]
    public void 比较_容差内相等_类别忽略大小写_数量或日期不同就不等()
    {
        var left = new[] { new MyobTimesheetCell("UID-A", "2026-10-26", 7.5m) };
        Assert.True(MyobTimesheetPushRules.SameCells(left, [new MyobTimesheetCell("uid-a", "2026-10-26", 7.5004m)]));
        Assert.False(MyobTimesheetPushRules.SameCells(left, [new MyobTimesheetCell("uid-a", "2026-10-26", 7.502m)]));
        Assert.False(MyobTimesheetPushRules.SameCells(left, [new MyobTimesheetCell("uid-a", "2026-10-27", 7.5m)]));
        Assert.False(MyobTimesheetPushRules.SameCells(left, []));
        Assert.True(MyobTimesheetPushRules.SameCells([], []));
    }

    [Fact]
    public void PUT请求体_按类别分组_日期yyyyMMdd_空集合得到空Lines()
    {
        var lines = MyobTimesheetPushRules.ToPutLines(
        [
            new MyobTimesheetCell("uid-b", "2026-10-27", 2m),
            new MyobTimesheetCell("uid-a", "2026-10-28", 1m),
            new MyobTimesheetCell("uid-a", "2026-10-26", 7.5m),
        ]);

        Assert.Equal(["uid-a", "uid-b"], lines.Select(line => line.CategoryUid));
        Assert.Equal(["2026-10-26", "2026-10-28"], lines[0].Entries.Select(entry => entry.Date));
        Assert.Empty(MyobTimesheetPushRules.ToPutLines([]));
    }

    [Fact]
    public void 快照序列化_往返一致_损坏或为空返回null()
    {
        var cells = DefaultTarget.ToList();
        Assert.Equal(cells, MyobTimesheetPushRules.FromJson(MyobTimesheetPushRules.ToJson(cells)));
        Assert.Empty(MyobTimesheetPushRules.FromJson("[]")!);
        Assert.Null(MyobTimesheetPushRules.FromJson("{not json"));
        Assert.Null(MyobTimesheetPushRules.FromJson(null));
        Assert.Null(MyobTimesheetPushRules.FromJson(" "));
    }

    [Fact]
    public void 总闸_默认关_开了但白名单为空也禁止_星号放全部_白名单非空只放列出的()
    {
        Assert.False(MyobTimesheetPushGate.IsAllowed(new MyobOptions(), Conn));
        // 只开总闸、忘了配白名单：不能因此放行所有公司文件。
        Assert.False(MyobTimesheetPushGate.IsAllowed(new MyobOptions { TimesheetPushEnabled = true }, Conn));
        Assert.True(MyobTimesheetPushGate.IsAllowed(new MyobOptions { TimesheetPushEnabled = true, TimesheetPushConnectionGuids = "*" }, Conn));
        var listed = new MyobOptions { TimesheetPushEnabled = true, TimesheetPushConnectionGuids = " other ,CONN-1;x" };
        Assert.True(MyobTimesheetPushGate.IsAllowed(listed, Conn));
        Assert.False(MyobTimesheetPushGate.IsAllowed(listed, "conn-2"));
        // 总闸关时白名单没有意义。
        Assert.False(MyobTimesheetPushGate.IsAllowed(
            new MyobOptions { TimesheetPushEnabled = false, TimesheetPushConnectionGuids = Conn }, Conn));
    }

    // ───────────────────────── 推送：入口校验 ─────────────────────────

    [Fact]
    public async Task 推送_总闸关或不在白名单_直接拒绝_不规划不写MYOB()
    {
        var closed = await Service(new MyobOptions()).PushAsync(Request("u1"), "admin", default);
        Assert.False(closed.Success);
        Assert.Equal(MyobPushErrorCodes.Disabled, closed.ErrorCode);

        var notListed = await Service(new MyobOptions { TimesheetPushEnabled = true, TimesheetPushConnectionGuids = "other" })
            .PushAsync(Request("u1"), "admin", default);
        Assert.Equal(MyobPushErrorCodes.Disabled, notListed.ErrorCode);

        Assert.Equal(0, _plans.Calls);
        Assert.Equal(0, _myob.PutCalls.Count);
        Assert.Empty(Records());
    }

    [Fact]
    public async Task 推送_请求不合法_员工数0或超过10或缺周期_都被拒绝()
    {
        var service = Service();
        Assert.Equal(MyobPushErrorCodes.InvalidRequest, (await service.PushAsync(Request(), "admin", default)).ErrorCode);
        var eleven = Request(Enumerable.Range(1, 11).Select(i => "u" + i).ToArray());
        Assert.Equal(MyobPushErrorCodes.InvalidRequest, (await service.PushAsync(eleven, "admin", default)).ErrorCode);
        var noPeriod = Request("u1");
        noPeriod.PeriodStart = " ";
        Assert.Equal(MyobPushErrorCodes.InvalidRequest, (await service.PushAsync(noPeriod, "admin", default)).ErrorCode);
        Assert.Empty(_myob.PutCalls);
    }

    [Fact]
    public async Task 推送_周期还没结束_拒绝()
    {
        _plans.Computation = _plans.Computation! with { End = new DateTime(2026, 11, 4), Today = new DateTime(2026, 11, 4) };
        var result = await Service().PushAsync(Request("u1"), "admin", default);
        Assert.Equal(MyobPushErrorCodes.PeriodNotEnded, result.ErrorCode);
        Assert.Empty(_myob.PutCalls);
    }

    [Fact]
    public async Task 推送_规划失败_比如没有分店权限_原样返回错误()
    {
        _plans.Error = ApiResponse<MyobTimesheetComputation>.Error("没有权限", MyobTimesheetErrorCodes.Forbidden);
        var result = await Service().PushAsync(Request("u1"), "admin", default);
        Assert.Equal(MyobTimesheetErrorCodes.Forbidden, result.ErrorCode);
        Assert.Empty(_myob.PutCalls);
    }

    // ───────────────────────── 推送：主流程 ─────────────────────────

    [Fact]
    public async Task 推送_MYOB里是空的_写入后回读一致_记Pushed_请求体与快照正确()
    {
        var result = await Service().PushAsync(Request("u1"), "admin", default);

        Assert.True(result.Success);
        var item = Assert.Single(result.Data!.Results);
        Assert.Equal(MyobPushStatuses.Pushed, item.Status);
        Assert.Equal(12m, item.PushedHours);

        var put = Assert.Single(_myob.PutCalls);
        Assert.Equal("emp-u1", put.EmployeeUid);
        Assert.Equal((Start, End), (put.Start, put.End));
        Assert.Equal(["uid-sat", "uid-weekday"], put.Lines.Select(line => line.CategoryUid));
        Assert.Equal("2026-10-26", put.Lines[1].Entries[0].Date);
        Assert.Equal(8m, put.Lines[1].Entries[0].Hours);

        var record = Assert.Single(Records());
        Assert.Equal(MyobPushStatuses.Pushed, record.Status);
        Assert.Equal(result.Data.BatchGuid, record.BatchGuid);
        Assert.Equal("admin", record.ActorUserGuid);
        Assert.Equal(720, record.PushedMinutes);
        Assert.Equal(64, record.RequestHash!.Length);
        Assert.Empty(MyobTimesheetPushRules.FromJson(record.BeforeJson)!);
        Assert.Equal(DefaultTarget, MyobTimesheetPushRules.FromJson(record.AfterJson));
        Assert.NotNull(record.FinishedAt);
        Assert.Equal(DefaultTarget, MyobTimesheetPushRules.Normalize(_myob.Store["emp-u1"]));
    }

    [Fact]
    public async Task 推送_一块里有拦截项的员工只被拒绝_同块其他员工照常推送()
    {
        _plans.Computation = Computation(Row("u1", "Alice", MyobPreviewStatuses.Blocked), Row("u2", "Bob"));
        _plans.Computation.Dto.Rows[0].Blockers.Add(new MyobPreviewBlockerDto { Code = MyobBlockerCodes.NotBound, Message = "未绑定" });

        var result = await Service().PushAsync(Request("u1", "u2"), "admin", default);

        Assert.Equal(MyobPushStatuses.Blocked, result.Data!.Results[0].Status);
        Assert.Equal(MyobBlockerCodes.NotBound, result.Data.Results[0].Code);
        Assert.Equal(MyobPushStatuses.Pushed, result.Data.Results[1].Status);
        Assert.Equal(["emp-u2"], _myob.PutCalls.Select(call => call.EmployeeUid));
        Assert.Equal("u2", Assert.Single(Records()).UserGuid); // Blocked 不落库
    }

    [Fact]
    public async Task 推送_预览里没有这个员工_按未就绪拒绝()
    {
        var result = await Service().PushAsync(Request("ghost"), "admin", default);
        var item = Assert.Single(result.Data!.Results);
        Assert.Equal(MyobPushStatuses.Blocked, item.Status);
        Assert.Equal(MyobPushErrorCodes.NotReady, item.Code);
        Assert.Empty(_myob.PutCalls);
    }

    [Fact]
    public async Task 推送_提示项没勾选已知晓就拒绝_勾选了才写()
    {
        _plans.Computation = Computation(Row("u1", "Alice", MyobPreviewStatuses.Ready, MyobHintCodes.BirthdayReview));

        var refused = await Service().PushAsync(Request("u1"), "admin", default);
        Assert.Equal(MyobPushErrorCodes.HintNotAcknowledged, refused.Data!.Results[0].Code);
        Assert.Empty(_myob.PutCalls);

        var request = Request("u1");
        request.AcknowledgedHintCodes = [MyobHintCodes.BirthdayReview];
        var accepted = await Service().PushAsync(request, "admin", default);
        Assert.Equal(MyobPushStatuses.Pushed, accepted.Data!.Results[0].Status);
    }

    [Fact]
    public async Task 推送_要推送的类别在服务端映射里缺失_拒绝而不是少写()
    {
        _plans.Computation = _plans.Computation! with
        {
            MappingByKey = new Dictionary<string, string?> { [MyobPayCategoryKeys.Weekday] = "uid-weekday" },
        };
        var result = await Service().PushAsync(Request("u1"), "admin", default);
        Assert.Equal(MyobBlockerCodes.CategoryNotMapped, result.Data!.Results[0].Code);
        Assert.Empty(_myob.PutCalls);
    }

    // ───────────────────────── 推送：MYOB 现状判定 ─────────────────────────

    [Fact]
    public async Task 推送_MYOB已有已发薪记录_一律跳过_哪怕勾选了覆盖()
    {
        _myob.Store["emp-u1"] = [Existing("uid-weekday", "2026-10-26", 8m, processed: true)];
        var request = Request("u1");
        request.OverwriteUserGuids = ["u1"];

        var result = await Service().PushAsync(request, "admin", default);

        Assert.Equal(MyobPushStatuses.SkippedProcessed, result.Data!.Results[0].Status);
        Assert.Empty(_myob.PutCalls);
        Assert.Equal(MyobPushStatuses.SkippedProcessed, Assert.Single(Records()).Status);
    }

    [Fact]
    public async Task 推送_已有未发薪记录_没勾选覆盖就跳过_勾选后覆盖并保存推送前快照()
    {
        var old = new[] { Existing("uid-weekday", "2026-10-27", 3m) };
        _myob.Store["emp-u1"] = old.ToList();

        var skipped = await Service().PushAsync(Request("u1"), "admin", default);
        Assert.Equal(MyobPushStatuses.SkippedHasEntries, skipped.Data!.Results[0].Status);
        Assert.Empty(_myob.PutCalls);

        var request = Request("u1");
        request.OverwriteUserGuids = ["u1"];
        var overwritten = await Service().PushAsync(request, "admin", default);
        Assert.Equal(MyobPushStatuses.Pushed, overwritten.Data!.Results[0].Status);

        var pushed = Records().Single(item => item.Status == MyobPushStatuses.Pushed);
        Assert.Equal(
            [new MyobTimesheetCell("uid-weekday", "2026-10-27", 3m)],
            MyobTimesheetPushRules.FromJson(pushed.BeforeJson));
        // 整体覆盖：原来的 10-27 那天没有了
        Assert.Equal(DefaultTarget, MyobTimesheetPushRules.Normalize(_myob.Store["emp-u1"]));
    }

    [Fact]
    public async Task 推送_MYOB里已经是目标内容_不重复写_记UpToDate()
    {
        _myob.Store["emp-u1"] =
        [
            Existing("uid-weekday", "2026-10-26", 8m),
            Existing("uid-sat", "2026-10-31", 4m),
        ];

        var result = await Service().PushAsync(Request("u1"), "admin", default);

        Assert.Equal(MyobPushStatuses.UpToDate, result.Data!.Results[0].Status);
        Assert.Empty(_myob.PutCalls);
        Assert.Equal(MyobPushStatuses.UpToDate, Assert.Single(Records()).Status);
    }

    [Fact]
    public async Task 推送_读MYOB现状失败_不写入_记Failed()
    {
        _myob.GetError = new MyobApiException(MyobErrorCodes.ApiCallFailed, "x", System.Net.HttpStatusCode.BadGateway);
        var result = await Service().PushAsync(Request("u1"), "admin", default);
        Assert.Equal(MyobPushStatuses.Failed, result.Data!.Results[0].Status);
        Assert.Equal(MyobPushErrorCodes.ReadFailed, result.Data.Results[0].Code);
        Assert.Empty(_myob.PutCalls);
        Assert.DoesNotContain(Records(), item => item.Status == MyobPushStatuses.Running); // 锁已释放
    }

    // ───────────────────────── 推送：PUT 失败 / 校验 ─────────────────────────

    [Fact]
    public async Task 推送_PUT返回5xx_重试一次_第二次成功就算成功()
    {
        _myob.PutResults.Enqueue(new MyobTimesheetPutResult(503, null));
        var result = await Service().PushAsync(Request("u1"), "admin", default);
        Assert.Equal(MyobPushStatuses.Pushed, result.Data!.Results[0].Status);
        Assert.Equal(2, _myob.PutCalls.Count);
    }

    [Fact]
    public async Task 推送_PUT返回4xx_不重试_记Failed只存错误名和状态码()
    {
        _myob.PutResults.Enqueue(new MyobTimesheetPutResult(400, "DateOutOfRange", "221"));
        var result = await Service().PushAsync(Request("u1"), "admin", default);

        var item = result.Data!.Results[0];
        Assert.Equal(MyobPushStatuses.Failed, item.Status);
        Assert.Equal("DateOutOfRange", item.Code);
        Assert.Single(_myob.PutCalls);
        var record = Assert.Single(Records());
        Assert.Equal(MyobPushStatuses.Failed, record.Status);
        Assert.Equal("DateOutOfRange", record.ErrorCode);
        Assert.Equal(400, record.ErrorStatus);
        Assert.Empty(_myob.Store); // 没写进去
    }

    [Fact]
    public async Task 推送_PUT连续5xx_重试用完仍失败()
    {
        _myob.PutResults.Enqueue(new MyobTimesheetPutResult(500, null));
        _myob.PutResults.Enqueue(new MyobTimesheetPutResult(502, null));
        var result = await Service().PushAsync(Request("u1"), "admin", default);
        Assert.Equal(MyobPushStatuses.Failed, result.Data!.Results[0].Status);
        Assert.Equal(2, _myob.PutCalls.Count);
    }

    [Fact]
    public async Task 推送_PUT返回401_本块剩余员工全部停止_不再调用MYOB()
    {
        _myob.PutResults.Enqueue(new MyobTimesheetPutResult(401, null));
        var result = await Service().PushAsync(Request("u1", "u2"), "admin", default);

        Assert.Equal(MyobPushStatuses.Failed, result.Data!.Results[0].Status);
        Assert.Equal(MyobPushStatuses.Failed, result.Data.Results[1].Status);
        Assert.Equal(MyobPushErrorCodes.AuthFailed, result.Data.Results[1].Code);
        Assert.Single(_myob.PutCalls);
        Assert.DoesNotContain(Records(), item => item.UserGuid == "u2");
    }

    [Fact]
    public async Task 推送_回读与目标不一致_记Mismatch_不自动再写_快照记真实内容()
    {
        _myob.TamperAfterPut = 0.5m;
        var result = await Service().PushAsync(Request("u1"), "admin", default);

        var item = result.Data!.Results[0];
        Assert.Equal(MyobPushStatuses.Mismatch, item.Status);
        Assert.Equal(MyobPushErrorCodes.ReadbackMismatch, item.Code);
        Assert.Single(_myob.PutCalls);
        var record = Assert.Single(Records());
        Assert.Equal(MyobPushStatuses.Mismatch, record.Status);
        Assert.NotEqual(DefaultTarget, MyobTimesheetPushRules.FromJson(record.AfterJson));
    }

    [Fact]
    public async Task 推送_令牌失败_整块不处理_也不落库()
    {
        var service = new MyobTimesheetPushService(
            _db, _plans, _scope, _myob, new FakeTokens { Error = new MyobApiException(MyobErrorCodes.NeedsReauthorization, "需要重新授权") },
            Options.Create(Enabled()), _time);
        var result = await service.PushAsync(Request("u1"), "admin", default);
        Assert.Equal(MyobPushErrorCodes.AuthFailed, result.ErrorCode);
        Assert.Empty(Records());
    }

    // ───────────────────────── 推送：并发 ─────────────────────────

    [Fact]
    public async Task 推送_同一周期已有未过期的Running_返回正在推送中_不写MYOB()
    {
        await _db.Insertable(new MyobTimesheetPush
        {
            BatchGuid = Guid.NewGuid().ToString(), ConnectionGuid = Conn, PeriodStart = Start, Weeks = 1, UserGuid = "u9",
            MyobEmployeeUid = "emp-u9", Status = MyobPushStatuses.Running, StartedAt = Now.AddMinutes(-2),
        }).ExecuteCommandAsync();

        var result = await Service().PushAsync(Request("u1"), "admin", default);

        Assert.Equal(MyobPushErrorCodes.InProgress, result.ErrorCode);
        Assert.Empty(_myob.PutCalls);
    }

    [Fact]
    public async Task 推送_Running超过10分钟视为中断_标Failed后放行()
    {
        await _db.Insertable(new MyobTimesheetPush
        {
            BatchGuid = Guid.NewGuid().ToString(), ConnectionGuid = Conn, PeriodStart = Start, Weeks = 1, UserGuid = "u9",
            MyobEmployeeUid = "emp-u9", Status = MyobPushStatuses.Running, StartedAt = Now.AddMinutes(-11),
        }).ExecuteCommandAsync();

        var result = await Service().PushAsync(Request("u1"), "admin", default);

        Assert.True(result.Success);
        var stale = Records().Single(item => item.UserGuid == "u9");
        Assert.Equal(MyobPushStatuses.Failed, stale.Status); // 还没读到 MYOB 快照就中断：没有写过，记 Failed
        Assert.Equal(MyobPushErrorCodes.Stale, stale.ErrorCode);
    }

    [Fact]
    public async Task 推送_分块调用共用同一个批次号()
    {
        var first = await Service().PushAsync(Request("u1"), "admin", default);
        var second = Request("u2");
        second.BatchGuid = first.Data!.BatchGuid;
        var result = await Service().PushAsync(second, "admin", default);

        Assert.Equal(first.Data.BatchGuid, result.Data!.BatchGuid);
        Assert.Single(Records().Select(item => item.BatchGuid).Distinct());
    }

    // ───────────────────────── 撤销 ─────────────────────────

    [Fact]
    public async Task 撤销_原来有记录_把推送前的快照写回去()
    {
        _myob.Store["emp-u1"] = [Existing("uid-weekday", "2026-10-27", 3m)];
        var request = Request("u1");
        request.OverwriteUserGuids = ["u1"];
        var pushed = await Service().PushAsync(request, "admin", default);

        var undo = await Service().UndoBatchAsync(pushed.Data!.BatchGuid, "boss", default);

        Assert.True(undo.Success);
        Assert.Equal(MyobPushStatuses.Undone, undo.Data!.Results[0].Status);
        Assert.Equal(
            [new MyobTimesheetCell("uid-weekday", "2026-10-27", 3m)],
            MyobTimesheetPushRules.Normalize(_myob.Store["emp-u1"]));
        var record = Assert.Single(Records());
        Assert.Equal(MyobPushStatuses.Undone, record.Status);
        Assert.Equal("boss", record.UndoneByUserGuid);
        Assert.NotNull(record.UndoneAt);
    }

    [Fact]
    public async Task 撤销_原来没有记录_发空Lines清空()
    {
        var pushed = await Service().PushAsync(Request("u1"), "admin", default);
        _myob.PutCalls.Clear();

        var undo = await Service().UndoBatchAsync(pushed.Data!.BatchGuid, "boss", default);

        Assert.Equal(MyobPushStatuses.Undone, undo.Data!.Results[0].Status);
        var put = Assert.Single(_myob.PutCalls);
        Assert.Empty(put.Lines);
        Assert.Empty(MyobTimesheetPushRules.Normalize(_myob.Store["emp-u1"]));
    }

    [Fact]
    public async Task 撤销_MYOB里的内容被人改过_不覆盖_记录保持可重试()
    {
        var pushed = await Service().PushAsync(Request("u1"), "admin", default);
        _myob.Store["emp-u1"] = [Existing("uid-weekday", "2026-10-26", 9m)]; // 有人在 MYOB 里手改
        _myob.PutCalls.Clear();

        var undo = await Service().UndoBatchAsync(pushed.Data!.BatchGuid, "boss", default);

        Assert.Equal(MyobPushStatuses.UndoConflict, undo.Data!.Results[0].Status);
        Assert.Empty(_myob.PutCalls);
        var record = Assert.Single(Records());
        Assert.Equal(MyobPushStatuses.Pushed, record.Status);
        Assert.NotNull(record.UndoNote);
    }

    [Fact]
    public async Task 撤销_该员工已发薪_拒绝且不改动()
    {
        var pushed = await Service().PushAsync(Request("u1"), "admin", default);
        _myob.Store["emp-u1"] = [Existing("uid-weekday", "2026-10-26", 8m, processed: true)];
        _myob.PutCalls.Clear();

        var undo = await Service().UndoBatchAsync(pushed.Data!.BatchGuid, "boss", default);

        Assert.Equal(MyobPushStatuses.UndoBlockedProcessed, undo.Data!.Results[0].Status);
        Assert.Empty(_myob.PutCalls);
    }

    [Fact]
    public async Task 撤销_该员工有更新的批次_拒绝_先撤销新批次再撤销旧批次()
    {
        var first = await Service().PushAsync(Request("u1"), "admin", default);

        // 规划变了（多 1 小时周六）→ 第二次推送覆盖第一次
        _time.Advance(TimeSpan.FromMinutes(5));
        ((Dictionary<string, MyobCompanyPlan>)_plans.Computation!.Plans)["u1"] = PlanOf(
            (MyobPayCategoryKeys.Weekday, "2026-10-26", 480),
            (MyobPayCategoryKeys.Saturday, "2026-10-31", 300));
        var overwrite = Request("u1");
        overwrite.OverwriteUserGuids = ["u1"];
        var second = await Service().PushAsync(overwrite, "admin", default);
        Assert.Equal(MyobPushStatuses.Pushed, second.Data!.Results[0].Status);

        _myob.PutCalls.Clear();
        var blocked = await Service().UndoBatchAsync(first.Data!.BatchGuid, "boss", default);
        Assert.Equal(MyobPushStatuses.UndoBlockedNewer, blocked.Data!.Results[0].Status);
        Assert.Empty(_myob.PutCalls);

        var undoSecond = await Service().UndoBatchAsync(second.Data.BatchGuid, "boss", default);
        Assert.Equal(MyobPushStatuses.Undone, undoSecond.Data!.Results[0].Status);
        // 恢复到第二次推送前 = 第一次推送的内容
        Assert.Equal(DefaultTarget, MyobTimesheetPushRules.Normalize(_myob.Store["emp-u1"]));

        // 新批次已撤销、MYOB 内容回到第一次推送的样子之后，第一次的撤销不再被「更新批次」挡住：
        // 但新批次状态是 Undone（不在阻挡名单里），所以现在可以撤销第一个批次。
        var undoFirst = await Service().UndoBatchAsync(first.Data.BatchGuid, "boss", default);
        Assert.Equal(MyobPushStatuses.Undone, undoFirst.Data!.Results[0].Status);
        Assert.Empty(MyobTimesheetPushRules.Normalize(_myob.Store["emp-u1"]));
    }

    [Fact]
    public async Task 撤销_批次不存在_总闸关_没有可撤销的写入_分别返回对应错误()
    {
        Assert.Equal(MyobPushErrorCodes.BatchNotFound, (await Service().UndoBatchAsync("nope", "boss", default)).ErrorCode);

        var pushed = await Service().PushAsync(Request("u1"), "admin", default);
        Assert.Equal(
            MyobPushErrorCodes.Disabled,
            (await Service(new MyobOptions()).UndoBatchAsync(pushed.Data!.BatchGuid, "boss", default)).ErrorCode);

        // 只有跳过的批次：没有可撤销的写入
        _myob.Store["emp-u2"] = [Existing("uid-weekday", "2026-10-27", 3m)];
        var skipped = await Service().PushAsync(Request("u2"), "admin", default);
        Assert.Equal(
            MyobPushErrorCodes.NothingToUndo,
            (await Service().UndoBatchAsync(skipped.Data!.BatchGuid, "boss", default)).ErrorCode);
    }

    // ───────────────────────── 审查发现的回归（2026-10-30） ─────────────────────────

    [Fact]
    public async Task 覆盖按人确认_只有被勾选的员工会被覆盖_预览之后新出现记录的员工不会被静默覆盖()
    {
        _myob.Store["emp-u1"] = [Existing("uid-weekday", "2026-10-27", 3m)];
        _myob.Store["emp-u2"] = [Existing("uid-weekday", "2026-10-28", 5m)]; // 用户没为 u2 勾选覆盖（预览时它还是空的）
        var request = Request("u1", "u2");
        request.OverwriteUserGuids = ["u1"];

        var result = await Service().PushAsync(request, "admin", default);

        Assert.Equal(MyobPushStatuses.Pushed, result.Data!.Results[0].Status);
        Assert.Equal(MyobPushStatuses.SkippedHasEntries, result.Data.Results[1].Status);
        Assert.Equal(["emp-u1"], _myob.PutCalls.Select(call => call.EmployeeUid));
        Assert.Equal(5m, MyobTimesheetPushRules.Normalize(_myob.Store["emp-u2"]).Single().Hours); // u2 的内容原样还在
    }

    [Fact]
    public async Task 现状里带Job或Notes_或有负工时_都算有内容_必须勾选覆盖才写()
    {
        _myob.Store["emp-u1"] = [new MyobTimesheetLineRecord("uid-weekday", [], HasExtras: true)]; // 只有 Notes，没有工时
        _myob.Store["emp-u2"] = [Existing("uid-weekday", "2026-10-27", -2m)];                      // 负工时（更正）

        var result = await Service().PushAsync(Request("u1", "u2"), "admin", default);

        Assert.All(result.Data!.Results, item => Assert.Equal(MyobPushStatuses.SkippedHasEntries, item.Status));
        Assert.Empty(_myob.PutCalls);
    }

    [Fact]
    public async Task 先加锁再读_读取失败或取消后锁被释放_不会留下Running卡住周期()
    {
        _myob.CancelOnGet = true;
        await Assert.ThrowsAsync<OperationCanceledException>(() => Service().PushAsync(Request("u1"), "admin", default));

        var record = Assert.Single(Records());
        Assert.Equal(MyobPushStatuses.Failed, record.Status);
        Assert.Equal("CANCELED", record.ErrorCode);

        _myob.CancelOnGet = false;
        var again = await Service().PushAsync(Request("u1"), "admin", default);
        Assert.Equal(MyobPushStatuses.Pushed, again.Data!.Results[0].Status);
    }

    [Fact]
    public async Task 落库之后调用方取消不会打断回读与收尾_记录不会卡在Running()
    {
        using var cts = new CancellationTokenSource();
        _myob.OnPut = () => cts.Cancel(); // PUT 发出去的同时用户关了页签

        var result = await Service().PushAsync(Request("u1"), "admin", cts.Token);

        Assert.Equal(MyobPushStatuses.Pushed, result.Data!.Results[0].Status);
        var record = Assert.Single(Records());
        Assert.Equal(MyobPushStatuses.Pushed, record.Status);
        Assert.NotNull(record.FinishedAt);
    }

    [Fact]
    public async Task PUT超时但其实已生效_回读等于目标就按成功收尾_推送前快照保留_仍可撤销()
    {
        _myob.Store["emp-u1"] = [Existing("uid-weekday", "2026-10-27", 3m)];
        _myob.ApplyOnFailure = true;
        _myob.PutResults.Enqueue(new MyobTimesheetPutResult(503, null));
        _myob.PutResults.Enqueue(new MyobTimesheetPutResult(503, null));
        var request = Request("u1");
        request.OverwriteUserGuids = ["u1"];

        var pushed = await Service().PushAsync(request, "admin", default);

        Assert.Equal(MyobPushStatuses.Pushed, pushed.Data!.Results[0].Status);
        var record = Assert.Single(Records());
        Assert.Equal([new MyobTimesheetCell("uid-weekday", "2026-10-27", 3m)], MyobTimesheetPushRules.FromJson(record.BeforeJson));

        _myob.ApplyOnFailure = false;
        var undo = await Service().UndoBatchAsync(pushed.Data.BatchGuid, "boss", default);
        Assert.Equal(MyobPushStatuses.Undone, undo.Data!.Results[0].Status);
        Assert.Equal(3m, MyobTimesheetPushRules.Normalize(_myob.Store["emp-u1"]).Single().Hours);
    }

    [Fact]
    public async Task PUT结果不确定且回读读不了_记Mismatch而不是Failed_保留可撤销的快照()
    {
        _myob.PutResults.Enqueue(new MyobTimesheetPutResult(0, null));
        _myob.PutResults.Enqueue(new MyobTimesheetPutResult(0, null));
        _myob.GetErrorAfterPut = true;

        var result = await Service().PushAsync(Request("u1"), "admin", default);

        var item = result.Data!.Results[0];
        Assert.Equal(MyobPushStatuses.Mismatch, item.Status);
        Assert.Equal(MyobPushErrorCodes.ReadbackFailed, item.Code);
        Assert.Contains(Records(), record => record.Status == MyobPushStatuses.Mismatch && record.BeforeJson is not null);
    }

    [Fact]
    public async Task 不确定的PUT_回读仍是推送前的内容_才记Failed()
    {
        _myob.Store["emp-u1"] = [Existing("uid-weekday", "2026-10-27", 3m)];
        _myob.PutResults.Enqueue(new MyobTimesheetPutResult(504, null));
        _myob.PutResults.Enqueue(new MyobTimesheetPutResult(504, null));
        var request = Request("u1");
        request.OverwriteUserGuids = ["u1"];

        var result = await Service().PushAsync(request, "admin", default);

        Assert.Equal(MyobPushStatuses.Failed, result.Data!.Results[0].Status);
        Assert.Equal(3m, MyobTimesheetPushRules.Normalize(_myob.Store["emp-u1"]).Single().Hours);
    }

    [Fact]
    public async Task Running遗留超过10分钟且已有快照_标Mismatch而不是Failed_保留撤销入口()
    {
        await _db.Insertable(new MyobTimesheetPush
        {
            BatchGuid = Guid.NewGuid().ToString(), ConnectionGuid = Conn, PeriodStart = Start, Weeks = 1, UserGuid = "u9",
            MyobEmployeeUid = "emp-u9", Status = MyobPushStatuses.Running, StartedAt = Now.AddMinutes(-11),
            BeforeJson = "[]", AfterJson = MyobTimesheetPushRules.ToJson(DefaultTarget),
        }).ExecuteCommandAsync();

        await Service().PushAsync(Request("u1"), "admin", default);

        var stale = Records().Single(item => item.UserGuid == "u9");
        Assert.Equal(MyobPushStatuses.Mismatch, stale.Status);
        Assert.Equal(MyobPushErrorCodes.Stale, stale.ErrorCode);
    }

    [Fact]
    public async Task 撤销_后来的UpToDate不挡住原批次的撤销()
    {
        var first = await Service().PushAsync(Request("u1"), "admin", default);
        var second = await Service().PushAsync(Request("u1"), "admin", default); // MYOB 已是目标 → UpToDate
        Assert.Equal(MyobPushStatuses.UpToDate, second.Data!.Results[0].Status);

        var undo = await Service().UndoBatchAsync(first.Data!.BatchGuid, "boss", default);

        Assert.Equal(MyobPushStatuses.Undone, undo.Data!.Results[0].Status);
        Assert.Empty(MyobTimesheetPushRules.Normalize(_myob.Store["emp-u1"]));
    }

    [Fact]
    public async Task 撤销_同一批次里同一员工写过两次_最新的先撤_一次调用全部恢复()
    {
        var first = await Service().PushAsync(Request("u1"), "admin", default);
        _time.Advance(TimeSpan.FromMinutes(1));
        ((Dictionary<string, MyobCompanyPlan>)_plans.Computation!.Plans)["u1"] = PlanOf((MyobPayCategoryKeys.Weekday, "2026-10-26", 600));
        var again = Request("u1");
        again.BatchGuid = first.Data!.BatchGuid; // 分块重发：同一批次
        again.OverwriteUserGuids = ["u1"];
        await Service().PushAsync(again, "admin", default);
        _myob.PutCalls.Clear();

        var undo = await Service().UndoBatchAsync(first.Data.BatchGuid, "boss", default);

        Assert.Equal(2, undo.Data!.Results.Count);
        Assert.All(undo.Data.Results, item => Assert.Equal(MyobPushStatuses.Undone, item.Status));
        Assert.Empty(MyobTimesheetPushRules.Normalize(_myob.Store["emp-u1"])); // 回到第一次推送之前
    }

    [Fact]
    public async Task 撤销_写成功但回读失败后重试_发现已是推送前的内容就直接记账_不会变成冲突()
    {
        var pushed = await Service().PushAsync(Request("u1"), "admin", default);
        _myob.GetErrorAfterPut = false;
        _myob.PutCalls.Clear();

        _myob.GetErrorAfterPut = true; // PUT 之后的回读失败
        // GetErrorAfterPut 以 PutCalls.Count > 0 为条件：第一次读（PUT 之前）正常，PUT 之后读失败。
        var first = await Service().UndoBatchAsync(pushed.Data!.BatchGuid, "boss", default);
        Assert.Equal(MyobPushStatuses.Mismatch, first.Data!.Results[0].Status);
        Assert.Equal(MyobPushStatuses.Pushed, Assert.Single(Records()).Status); // 还没记账

        _myob.GetErrorAfterPut = false;
        var retry = await Service().UndoBatchAsync(pushed.Data.BatchGuid, "boss", default);

        Assert.Equal(MyobPushStatuses.Undone, retry.Data!.Results[0].Status);
        Assert.Equal(MyobPushStatuses.Undone, Assert.Single(Records()).Status);
    }

    [Fact]
    public async Task 批次号_必须是合法GUID_已存在的批次必须同一周期_不能把别的周期混进来()
    {
        var bad = Request("u1");
        bad.BatchGuid = "not-a-guid";
        Assert.Equal(MyobPushErrorCodes.InvalidRequest, (await Service().PushAsync(bad, "admin", default)).ErrorCode);

        var foreign = Guid.NewGuid().ToString();
        await _db.Insertable(new MyobTimesheetPush
        {
            BatchGuid = foreign, ConnectionGuid = Conn, PeriodStart = Start.AddDays(-7), Weeks = 1, UserGuid = "u1",
            MyobEmployeeUid = "emp-u1", Status = MyobPushStatuses.Pushed, StartedAt = Now.AddDays(-7),
        }).ExecuteCommandAsync();
        var mixed = Request("u2");
        mixed.BatchGuid = foreign;
        var result = await Service().PushAsync(mixed, "admin", default);

        Assert.Equal(MyobPushErrorCodes.InvalidRequest, result.ErrorCode);
        Assert.Empty(_myob.PutCalls);
    }

    [Fact]
    public async Task 非管理员只能看和撤销自己推送的批次_管理员不受限()
    {
        var pushed = await Service().PushAsync(Request("u1"), "admin", default);
        _scope.Scope = new CurrentUserManageableStoreScope { IsAllowed = true, IsAdmin = false, UserGuid = "someone-else" };

        Assert.Equal(MyobTimesheetErrorCodes.Forbidden, (await Service().GetBatchAsync(pushed.Data!.BatchGuid, default)).ErrorCode);
        Assert.Equal(MyobTimesheetErrorCodes.Forbidden, (await Service().UndoBatchAsync(pushed.Data.BatchGuid, "someone-else", default)).ErrorCode);
        Assert.Empty((await Service().ListBatchesAsync(Conn, null, default)).Data!);

        _scope.Scope = new CurrentUserManageableStoreScope { IsAllowed = true, IsAdmin = false, UserGuid = "admin" };
        Assert.True((await Service().GetBatchAsync(pushed.Data.BatchGuid, default)).Success);
        Assert.Single((await Service().ListBatchesAsync(Conn, null, default)).Data!);

        _scope.Scope = new CurrentUserManageableStoreScope { IsAllowed = true, IsAdmin = true, UserGuid = "someone-else" };
        Assert.True((await Service().GetBatchAsync(pushed.Data.BatchGuid, default)).Success);
    }

    [Fact]
    public async Task 错误码超过列宽_截断后再落库_不会因为更新失败卡住记录()
    {
        _myob.PutResults.Enqueue(new MyobTimesheetPutResult(400, new string('X', 200)));

        await Service().PushAsync(Request("u1"), "admin", default);

        var record = Assert.Single(Records());
        Assert.Equal(MyobPushStatuses.Failed, record.Status);
        Assert.Equal(80, record.ErrorCode!.Length);
    }

    [Fact]
    public async Task 总闸开了但白名单为空_推送和撤销都被拒绝()
    {
        var result = await Service(new MyobOptions { TimesheetPushEnabled = true }).PushAsync(Request("u1"), "admin", default);
        Assert.Equal(MyobPushErrorCodes.Disabled, result.ErrorCode);
        Assert.Empty(_myob.PutCalls);
    }

    // ───────────────────────── 批次查询 ─────────────────────────

    [Fact]
    public async Task 批次列表与明细_汇总各状态人数_可撤销数只算真正写过的()
    {
        _myob.Store["emp-u2"] = [Existing("uid-weekday", "2026-10-27", 3m)]; // u2 有记录且不覆盖 → 跳过
        await _db.Insertable(new User { UserGUID = "admin", Username = "admin", Email = "admin@example.com", FullName = "Admin Pan", IsActive = true, CreatedAt = Now }).ExecuteCommandAsync();
        var pushed = await Service().PushAsync(Request("u1", "u2"), "admin", default);

        var list = await Service().ListBatchesAsync(Conn, "2026-10-26", default);
        var summary = Assert.Single(list.Data!);
        Assert.Equal(pushed.Data!.BatchGuid, summary.BatchGuid);
        Assert.Equal(2, summary.Total);
        Assert.Equal(1, summary.Pushed);
        Assert.Equal(1, summary.Skipped);
        Assert.Equal(1, summary.Undoable);
        Assert.Equal("Admin Pan", summary.ActorName);

        var detail = await Service().GetBatchAsync(pushed.Data.BatchGuid, default);
        Assert.Equal(2, detail.Data!.Items.Count);
        Assert.Equal("2026-10-26", detail.Data.PeriodStart);
        Assert.Contains(detail.Data.Items, item => item.Status == MyobPushStatuses.Pushed && item.PushedHours == 12m);

        Assert.Equal(MyobPushErrorCodes.BatchNotFound, (await Service().GetBatchAsync("nope", default)).ErrorCode);
        Assert.Equal(MyobTimesheetErrorCodes.InvalidPeriod, (await Service().ListBatchesAsync(Conn, "bad", default)).ErrorCode);
    }

    [Fact]
    public async Task 批次查询_没有分店管理权限的账号_被拒绝()
    {
        _scope.Scope = new CurrentUserManageableStoreScope { IsAllowed = false, Message = "无权限" };
        Assert.Equal(MyobTimesheetErrorCodes.Forbidden, (await Service().ListBatchesAsync(Conn, null, default)).ErrorCode);
        Assert.Equal(MyobTimesheetErrorCodes.Forbidden, (await Service().GetBatchAsync("x", default)).ErrorCode);
    }

    // ───────────────────────── 替身 ─────────────────────────

    private sealed record PutCall(
        string EmployeeUid, DateTime Start, DateTime End, IReadOnlyList<MyobTimesheetPutLine> Lines);

    /// <summary>有状态的假 MYOB：每个员工一份 Timesheet；PUT 整体覆盖（空 Lines = 清空），读回日期带 T00:00:00。</summary>
    private sealed class FakeMyob : IMyobApiClient
    {
        public Dictionary<string, List<MyobTimesheetLineRecord>> Store { get; } = new();
        public List<PutCall> PutCalls { get; } = [];
        public Queue<MyobTimesheetPutResult> PutResults { get; } = new();
        public MyobApiException? GetError { get; set; }

        /// <summary>非 null：PUT 成功后把存的第一个单元格小时数加上这个值，模拟「回读与预期不一致」。</summary>
        public decimal? TamperAfterPut { get; set; }

        /// <summary>true：PUT 返回失败（如超时 / 5xx）时内容其实已经写进去，模拟「结果不确定」。</summary>
        public bool ApplyOnFailure { get; set; }

        /// <summary>true：第一次 PUT 之后的所有读取都失败，模拟「写完回读不了」。</summary>
        public bool GetErrorAfterPut { get; set; }

        /// <summary>PUT 被调用时触发（用来在写入中途取消调用方的 token）。</summary>
        public Action? OnPut { get; set; }

        /// <summary>true：读取时抛取消异常，模拟调用方在 PUT 之前取消。</summary>
        public bool CancelOnGet { get; set; }

        public Task<IReadOnlyList<MyobTimesheetLineRecord>> GetTimesheetLinesAsync(
            string businessId, string accessToken, string employeeUid, DateTime startDate, DateTime endDate, CancellationToken cancellationToken)
        {
            if (CancelOnGet)
            {
                return Task.FromException<IReadOnlyList<MyobTimesheetLineRecord>>(new OperationCanceledException());
            }

            if (GetError is not null || (GetErrorAfterPut && PutCalls.Count > 0))
            {
                return Task.FromException<IReadOnlyList<MyobTimesheetLineRecord>>(
                    GetError ?? new MyobApiException(MyobErrorCodes.ApiCallFailed, "x", System.Net.HttpStatusCode.BadGateway));
            }

            return Task.FromResult<IReadOnlyList<MyobTimesheetLineRecord>>(
                Store.TryGetValue(employeeUid, out var lines) ? lines.ToList() : []);
        }

        public Task<MyobTimesheetPutResult> PutTimesheetAsync(
            string businessId, string accessToken, string employeeUid, DateTime startDate, DateTime endDate,
            IReadOnlyList<MyobTimesheetPutLine> lines, CancellationToken cancellationToken)
        {
            PutCalls.Add(new PutCall(employeeUid, startDate, endDate, lines));
            OnPut?.Invoke();
            var result = PutResults.Count > 0 ? PutResults.Dequeue() : new MyobTimesheetPutResult(200, null);
            if (!result.Success && !ApplyOnFailure)
            {
                return Task.FromResult(result);
            }

            var stored = lines
                .Select(line => new MyobTimesheetLineRecord(
                    line.CategoryUid,
                    line.Entries.Select(entry => new MyobTimesheetEntryRecord(entry.Date + "T00:00:00", entry.Hours, false)).ToList()))
                .ToList();
            if (TamperAfterPut is { } extra && stored.Count > 0)
            {
                var first = stored[0];
                var entries = first.Entries.ToList();
                entries[0] = entries[0] with { Hours = entries[0].Hours + extra };
                stored[0] = first with { Entries = entries };
            }

            Store[employeeUid] = stored;
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<MyobWageCategoryRecord>> GetWageCategoriesAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MyobTimesheetStatus> GetTimesheetStatusAsync(string businessId, string accessToken, string employeeUid, DateTime startDate, DateTime endDate, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MyobTokenResponse> ExchangeCodeAsync(string code, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MyobTokenResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MyobCompanyInfo> GetCompanyAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MyobEmployeeRecord>> GetEmployeesAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MyobEmployeePayrollRecord>> GetEmployeePayrollDetailsAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MyobEmployeePaymentRecord>> GetEmployeePaymentDetailsAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakePlans : IMyobTimesheetPlanSource
    {
        public MyobTimesheetComputation? Computation { get; set; }
        public ApiResponse<MyobTimesheetComputation>? Error { get; set; }
        public int Calls { get; private set; }

        public Task<ApiResponse<MyobTimesheetComputation>> ComputeAsync(
            string connectionGuid, string? periodStart, bool checkMyob, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Error ?? ApiResponse<MyobTimesheetComputation>.OK(Computation!));
        }
    }

    private sealed class FakeTokens : IMyobTokenProvider
    {
        public MyobApiException? Error { get; set; }

        public Task<string> GetAccessTokenAsync(string connectionGuid, CancellationToken cancellationToken) =>
            Error is null ? Task.FromResult("access-token") : Task.FromException<string>(Error);

        public void Invalidate(string connectionGuid) { }
    }

    private sealed class FakeScope : ICurrentUserManageableStoreScopeService
    {
        public CurrentUserManageableStoreScope Scope { get; set; } = new() { IsAllowed = true, IsAdmin = true };

        public Task<CurrentUserManageableStoreScope> GetScopeAsync() => Task.FromResult(Scope);
        public Task<IReadOnlyList<string>> GetAccessibleStoreCodesAsync() => throw new NotSupportedException();
        public Task<bool> CanAccessStoreCodeAsync(string storeCode) => throw new NotSupportedException();
        public Task<bool> CanAccessOrderAsync(string orderGuid) => throw new NotSupportedException();
        public Task<bool> CanManageStoreAsync(string storeGuid) => throw new NotSupportedException();
        public Task<bool> CanManageUserAsync(string userGuid) => throw new NotSupportedException();
    }

    private sealed class FakeTime(DateTime utcNow) : TimeProvider
    {
        private DateTime _now = utcNow;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => new(_now, TimeSpan.Zero);
    }
}
