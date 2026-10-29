using System.Reflection;
using System.Text.Json;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Api.Services.Attendance;
using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// MYOB 历史工资单：工资单 ↔ 员工 ↔ 分店 的归类规则、权限范围、缓存、查看记录与控制器。
/// SQLite 临时库 + 假 MYOB + 假考勤源；姓名与金额都是虚构的。
/// </summary>
public sealed class MyobPayslipTests : IDisposable
{
    // 悉尼本地 2026-10-29（UTC+10）：UTC 2026-10-28 20:00。
    private static readonly DateTime Now = new(2026, 10, 28, 20, 0, 0, DateTimeKind.Utc);
    private const string Inv = "conn-inv";
    private const string Aus = "conn-aus";

    // 两个公司文件里各自的员工卡 UID（必须是合法 Guid：详情接口会把它拼进 OData 表达式）。
    private const string CardJane = "aaaaaaaa-0000-0000-0000-000000000001"; // 绑定 u-jane
    private const string CardTom = "aaaaaaaa-0000-0000-0000-000000000002"; // 绑定 u-tom
    private const string CardStranger = "aaaaaaaa-0000-0000-0000-000000000003"; // 没绑定
    private const string CardIgnored = "aaaaaaaa-0000-0000-0000-000000000004"; // 在忽略名单
    private const string CardJaneAus = "bbbbbbbb-0000-0000-0000-000000000001"; // Jane 在 Australian 的卡

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hb-myob-payslip-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;
    private readonly FakeApi _api = new();
    private readonly FakeScope _scope = new();
    private readonly FakePermissions _permissions = new();
    private readonly FakeSource _source = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public MyobPayslipTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_dbPath}",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(
            typeof(MyobConnection), typeof(MyobStoreMapping), typeof(EmployeeMyobLink), typeof(EmployeePayrollEmployer),
            typeof(MyobEmployeeIgnore), typeof(Store), typeof(UserStore), typeof(MyobPayslipViewLog));
        Seed();
    }

    public void Dispose()
    {
        _cache.Dispose();
        _db.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    /// <summary>
    /// 数据：1002 Wallsend → Investment；1003 Bankstown → Australian；1005 Newcastle → Investment。
    /// Jane：Wallsend + Bankstown 两店，两个公司各有一张卡（跨公司）；Tom：只在 Newcastle（Investment）；
    /// Stranger：Investment 里有卡但没绑定；Ignored：在忽略名单。
    /// </summary>
    private void Seed()
    {
        foreach (var (guid, name) in new[] { (Inv, "Hot Bargain Investment Pty Ltd"), (Aus, "Hot Bargain Australian Pty Ltd") })
        {
            _db.Insertable(new MyobConnection
            {
                ConnectionGuid = guid, BusinessId = $"biz-{guid}", BusinessName = name,
                Status = MyobConnectionStatuses.Active, AuthorizedAt = Now, CreatedAt = Now,
            }).ExecuteCommand();
        }

        _db.Insertable(new MyobConnection
        {
            ConnectionGuid = "conn-off", BusinessId = "biz-off", BusinessName = "Disconnected Pty Ltd",
            Status = "Disconnected", AuthorizedAt = Now, CreatedAt = Now,
        }).ExecuteCommand();

        foreach (var (code, name, conn) in new[] { ("1002", "Wallsend", Inv), ("1003", "Bankstown", Aus), ("1005", "Newcastle", Inv) })
        {
            _db.Insertable(new Store { StoreGUID = $"g-{code}", StoreCode = code, StoreName = name, IsActive = true }).ExecuteCommand();
            _db.Insertable(new MyobStoreMapping { StoreCode = code, ConnectionGuid = conn, MappedAt = Now }).ExecuteCommand();
        }

        foreach (var (user, store) in new[] { ("u-jane", "1002"), ("u-jane", "1003"), ("u-tom", "1005") })
        {
            _db.Insertable(new UserStore { UserStoreGUID = Guid.NewGuid().ToString(), UserGUID = user, StoreGUID = $"g-{store}" }).ExecuteCommand();
        }

        Bind("u-jane", Inv, CardJane);
        Bind("u-jane", Aus, CardJaneAus);
        Bind("u-tom", Inv, CardTom);
        _db.Insertable(new MyobEmployeeIgnore { ConnectionGuid = Inv, MyobEmployeeUid = CardIgnored, IgnoredAt = Now }).ExecuteCommand();
    }

    private void Bind(string user, string connection, string card, DateTime? unboundAt = null) =>
        _db.Insertable(new EmployeeMyobLink
        {
            UserGuid = user, ConnectionGuid = connection, MyobEmployeeUid = card, BoundAt = Now.AddDays(-100), UnboundAt = unboundAt,
        }).ExecuteCommand();

    private static MyobPayrollAdviceRecord Slip(
        string card, string name, string paymentDate, decimal gross = 1000m, decimal net = 800m, string? periodStart = null,
        params MyobPayrollAdviceLine[] lines) =>
        new("Hot Bargain Pty Ltd", "12345678901", card, name, "E-" + name[..2],
            DateTime.Parse(periodStart ?? DateTime.Parse(paymentDate).AddDays(-13).ToString("yyyy-MM-dd")),
            DateTime.Parse(paymentDate).AddDays(-4), DateTime.Parse(paymentDate), "Fortnightly", gross, net, "Sample Super",
            lines.Length > 0
                ? lines
                :
                [
                    new MyobPayrollAdviceLine("Base Hourly", "Wage", 40m, 25m, gross, gross * 10),
                    new MyobPayrollAdviceLine("PAYG Withholding", "Tax", null, null, -(gross - net), -(gross - net) * 10),
                    new MyobPayrollAdviceLine("Superannuation Guarantee", "Superannuation", null, null, 115m, 1150m),
                ]);

    private MyobPayslipService Service(TimeProvider? time = null) =>
        new(_db, _api, new FakeTokens(), _scope, _permissions, _source, _cache, time ?? new FakeTime(Now));

    private static MyobPayslipQuery Q(string? storeCode = null, string? connection = null, string? keyword = null,
        bool unassigned = false, bool refresh = false, string? from = "2026-08-01", string? to = "2026-10-29") =>
        new() { StoreCode = storeCode, ConnectionGuid = connection, Keyword = keyword, OnlyUnassigned = unassigned, Refresh = refresh, From = from, To = to };

    private void GivenSlips()
    {
        _api.Slips[Inv] =
        [
            Slip(CardJane, "Jane Sample", "2026-10-23"),
            Slip(CardTom, "Tom Sample", "2026-10-23"),
            Slip(CardStranger, "Stan Stranger", "2026-10-23"),
            Slip(CardIgnored, "Ivy Ignored", "2026-10-23"),
        ];
        _api.Slips[Aus] = [Slip(CardJaneAus, "Jane Sample", "2026-10-23", 500m, 450m)];
        // Jane 本期在 Wallsend、Bankstown 都有考勤；Tom 在 Newcastle。
        _source.Pairs.AddRange([("u-jane", "1002"), ("u-jane", "1003"), ("u-tom", "1005")]);
    }

    private void AsAdmin() => _permissions.Granted.Add(Permissions.Myob.Payslip.ViewAllStores);

    private void AsManager(params string[] stores)
    {
        _permissions.Granted.Add(Permissions.Myob.Payslip.ViewManagedStore);
        _scope.Scope = new CurrentUserManageableStoreScope
        {
            IsAllowed = true, IsAuthenticated = true, IsStoreManager = true, UserGuid = "u-mgr", StoreCodes = stores,
        };
    }

    // ───────────────────────── 归类规则（纯函数） ─────────────────────────

    private static EmployeePayrollEmployer Override(string store, string? connection, string from) =>
        new() { UserGuid = "u", StoreCode = store, ConnectionGuid = connection, EffectiveFrom = DateTime.Parse(from) };

    private static readonly IReadOnlyDictionary<string, string?> Mappings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
    {
        ["1002"] = Inv, ["1003"] = Aus, ["1005"] = Inv,
    };

    [Fact]
    public void 归类_考勤分店且支付日由本公司发薪_归为Attendance_同公司多店一张单同时出现在多个分店下()
    {
        var placement = MyobPayslipMatcher.Place(Inv, new DateTime(2026, 10, 23), ["1005", "1002"], ["1002"], Mappings, []);
        Assert.Equal(MyobPayslipPlacements.Attendance, placement.Kind);
        Assert.Equal(["1002", "1005"], placement.StoreCodes.ToArray()); // 排序、不拆分金额
    }

    [Fact]
    public void 归类_跨公司_每家公司的工资单只归到由该公司发薪的分店()
    {
        // Jane 在 Wallsend（Investment）和 Bankstown（Australian）都有考勤。
        var inv = MyobPayslipMatcher.Place(Inv, new DateTime(2026, 10, 23), ["1002", "1003"], ["1002", "1003"], Mappings, []);
        var aus = MyobPayslipMatcher.Place(Aus, new DateTime(2026, 10, 23), ["1002", "1003"], ["1002", "1003"], Mappings, []);
        Assert.Equal(["1002"], inv.StoreCodes.ToArray());
        Assert.Equal(["1003"], aus.StoreCodes.ToArray());
    }

    [Fact]
    public void 归类_员工覆盖按支付日当天生效_老工资单仍归到当时的分店()
    {
        // 1003 Bankstown 默认 Australian；员工自 2026-10-01 起改由 Investment 发薪。
        var rows = new[] { Override("1003", Inv, "2026-10-01") };
        var before = MyobPayslipMatcher.Place(Inv, new DateTime(2026, 9, 24), ["1003"], [], Mappings, rows);
        var after = MyobPayslipMatcher.Place(Inv, new DateTime(2026, 10, 23), ["1003"], [], Mappings, rows);
        Assert.Equal(MyobPayslipPlacements.Unknown, before.Kind); // 9 月还是 Australian 发薪：Investment 的单不归它
        Assert.Equal(["1003"], after.StoreCodes.ToArray());
        Assert.Equal(MyobPayslipPlacements.Attendance, after.Kind);
    }

    [Fact]
    public void 归类_覆盖改回按分店_回到分店映射的公司()
    {
        var rows = new[] { Override("1003", Inv, "2026-10-01"), Override("1003", null, "2026-10-15") };
        var placement = MyobPayslipMatcher.Place(Aus, new DateTime(2026, 10, 23), ["1003"], [], Mappings, rows);
        Assert.Equal(["1003"], placement.StoreCodes.ToArray());
    }

    [Fact]
    public void 归类_周期内没有考勤数据_回退到员工当前分店_标为Inferred()
    {
        var placement = MyobPayslipMatcher.Place(Inv, new DateTime(2026, 10, 23), [], ["1002", "1003"], Mappings, []);
        Assert.Equal(MyobPayslipPlacements.Inferred, placement.Kind);
        Assert.Equal(["1002"], placement.StoreCodes.ToArray()); // 1003 由 Australian 发薪，不属于这张 Investment 的单
    }

    [Fact]
    public void 归类_考勤分店不由本公司发薪_不会因此进入分店集合_仍可回退当前分店()
    {
        var placement = MyobPayslipMatcher.Place(Inv, new DateTime(2026, 10, 23), ["1003"], ["1005"], Mappings, []);
        Assert.Equal(MyobPayslipPlacements.Inferred, placement.Kind);
        Assert.Equal(["1005"], placement.StoreCodes.ToArray());
    }

    [Fact]
    public void 归类_考勤和当前分店都对不上_Unknown()
    {
        var placement = MyobPayslipMatcher.Place(Inv, new DateTime(2026, 10, 23), [], ["1003"], Mappings, []);
        Assert.Equal(MyobPayslipPlacements.Unknown, placement.Kind);
        Assert.Empty(placement.StoreCodes);
    }

    [Fact]
    public void 归类_分店还没映射公司_不会被当作任何公司发薪()
    {
        var mappings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["9999"] = null };
        var placement = MyobPayslipMatcher.Place(Inv, new DateTime(2026, 10, 23), ["9999"], ["9999"], mappings, []);
        Assert.Equal(MyobPayslipPlacements.Unknown, placement.Kind);
    }

    [Fact]
    public void 找绑定_优先有效绑定_否则取最近解除的_解绑后历史工资单不消失_找不到为空()
    {
        var old = new EmployeeMyobLink { UserGuid = "u-old", ConnectionGuid = Inv, MyobEmployeeUid = CardJane, BoundAt = Now.AddDays(-300), UnboundAt = Now.AddDays(-200) };
        var older = new EmployeeMyobLink { UserGuid = "u-older", ConnectionGuid = Inv, MyobEmployeeUid = CardJane, BoundAt = Now.AddDays(-500), UnboundAt = Now.AddDays(-400) };
        var active = new EmployeeMyobLink { UserGuid = "u-now", ConnectionGuid = Inv, MyobEmployeeUid = CardJane, BoundAt = Now.AddDays(-100) };
        var otherCompany = new EmployeeMyobLink { UserGuid = "u-x", ConnectionGuid = Aus, MyobEmployeeUid = CardJane, BoundAt = Now };

        Assert.Equal("u-now", MyobPayslipMatcher.ResolveLink([old, older, active, otherCompany], Inv, CardJane)!.UserGuid);
        Assert.Equal("u-old", MyobPayslipMatcher.ResolveLink([older, old, otherCompany], Inv, CardJane)!.UserGuid);
        Assert.Null(MyobPayslipMatcher.ResolveLink([otherCompany], Inv, CardJane));
        Assert.Equal("u-now", MyobPayslipMatcher.ResolveLink([active], Inv, CardJane.ToUpperInvariant())!.UserGuid); // UID 大小写不敏感
    }

    [Fact]
    public void 可见性_全部分店什么都看得到_管辖分店只看已绑定且有交集的()
    {
        var attendance = new MyobPayslipPlacement(MyobPayslipPlacements.Attendance, ["1002", "1005"]);
        var unknown = new MyobPayslipPlacement(MyobPayslipPlacements.Unknown, []);
        var unbound = new MyobPayslipPlacement(MyobPayslipPlacements.Unbound, []);

        Assert.True(MyobPayslipMatcher.IsVisible(MyobPayslipAccess.Everything, false, unbound));
        Assert.True(MyobPayslipMatcher.IsVisible(MyobPayslipAccess.Everything, true, unknown));

        var manager = MyobPayslipAccess.Stores(["1005"]);
        Assert.True(MyobPayslipMatcher.IsVisible(manager, true, attendance)); // 有交集：整张都能看
        Assert.False(MyobPayslipMatcher.IsVisible(manager, false, attendance)); // 未绑定：不可见
        Assert.False(MyobPayslipMatcher.IsVisible(manager, true, unknown));
        Assert.False(MyobPayslipMatcher.IsVisible(MyobPayslipAccess.Stores(["1003"]), true, attendance));
        Assert.True(MyobPayslipMatcher.IsVisible(MyobPayslipAccess.Stores(["1002"]), true, attendance));
        Assert.True(MyobPayslipMatcher.IsUnassigned(MyobPayslipPlacements.Unbound));
        Assert.True(MyobPayslipMatcher.IsUnassigned(MyobPayslipPlacements.Unknown));
        Assert.False(MyobPayslipMatcher.IsUnassigned(MyobPayslipPlacements.Inferred));
    }

    // ───────────────────────── 列表 ─────────────────────────

    [Fact]
    public async Task 列表_全部分店权限_看到所有已绑定行加未绑定行_忽略名单不展示_合计与分店标签正确()
    {
        GivenSlips();
        AsAdmin();

        var response = await Service().ListAsync(Q(), CancellationToken.None);

        Assert.True(response.Success);
        var list = response.Data!;
        Assert.True(list.CanViewAllStores);
        Assert.All(list.Companies, status => Assert.True(status.Ok));
        // Inv：Jane、Tom、Stranger（Ignored 被排除）；Aus：Jane → 共 4 张。
        Assert.Equal(4, list.TotalCount);
        Assert.DoesNotContain(list.Rows, row => row.EmployeeUid == CardIgnored);
        Assert.Equal(1, list.UnassignedCount);
        Assert.Equal(1000m * 3 + 500m, list.GrossTotal);
        Assert.Equal(800m * 3 + 450m, list.NetTotal);
        Assert.Equal(4, list.EmployeeCount); // 以（公司，员工卡）计：Jane 在两家公司各算一张卡

        var janeInv = list.Rows.Single(row => row.ConnectionGuid == Inv && row.EmployeeUid == CardJane);
        Assert.Equal("u-jane", janeInv.UserGuid);
        Assert.Equal(["1002"], janeInv.Stores.Select(s => s.StoreCode).ToArray());
        Assert.Equal("1002 Wallsend", janeInv.Stores[0].StoreLabel);
        Assert.Equal(MyobPayslipPlacements.Attendance, janeInv.Placement);
        Assert.Equal(-200m, janeInv.TaxTotal); // 税行合计（负数）
        var janeAus = list.Rows.Single(row => row.ConnectionGuid == Aus);
        Assert.Equal(["1003"], janeAus.Stores.Select(s => s.StoreCode).ToArray());

        var stranger = list.Rows.Single(row => row.EmployeeUid == CardStranger);
        Assert.Equal(MyobPayslipPlacements.Unbound, stranger.Placement);
        Assert.Null(stranger.UserGuid);
        Assert.Empty(stranger.Stores);
    }

    [Fact]
    public async Task 列表_按分店过滤_跨公司员工在各自公司的分店下出现_未归属行不出现()
    {
        GivenSlips();
        AsAdmin();

        var wallsend = (await Service().ListAsync(Q("1002"), CancellationToken.None)).Data!;
        Assert.Equal([(Inv, CardJane)], wallsend.Rows.Select(row => (row.ConnectionGuid, row.EmployeeUid)).ToArray());

        var bankstown = (await Service().ListAsync(Q("1003"), CancellationToken.None)).Data!;
        Assert.Equal([(Aus, CardJaneAus)], bankstown.Rows.Select(row => (row.ConnectionGuid, row.EmployeeUid)).ToArray());

        var newcastle = (await Service().ListAsync(Q("1005"), CancellationToken.None)).Data!;
        Assert.Equal([CardTom], newcastle.Rows.Select(row => row.EmployeeUid).ToArray());
        Assert.Equal(1, newcastle.UnassignedCount); // 未归属数量不受分店过滤影响
    }

    [Fact]
    public async Task 列表_同公司多店员工_一张单同时出现在两个分店下_金额不拆分()
    {
        _api.Slips[Inv] = [Slip(CardTom, "Tom Sample", "2026-10-23")];
        _source.Pairs.AddRange([("u-tom", "1005"), ("u-tom", "1002")]);
        AsAdmin();

        var atNewcastle = (await Service().ListAsync(Q("1005"), CancellationToken.None)).Data!;
        var atWallsend = (await Service().ListAsync(Q("1002"), CancellationToken.None)).Data!;
        Assert.Single(atNewcastle.Rows);
        Assert.Single(atWallsend.Rows);
        Assert.Equal(1000m, atNewcastle.Rows[0].GrossPay);
        Assert.Equal(1000m, atWallsend.Rows[0].GrossPay);
    }

    [Fact]
    public async Task 列表_没有考勤数据_按当前分店推断并标Inferred()
    {
        _api.Slips[Inv] = [Slip(CardTom, "Tom Sample", "2026-10-23")]; // 不加 Pairs
        AsAdmin();

        var row = Assert.Single((await Service().ListAsync(Q(), CancellationToken.None)).Data!.Rows);
        Assert.Equal(MyobPayslipPlacements.Inferred, row.Placement);
        Assert.Equal(["1005"], row.Stores.Select(s => s.StoreCode).ToArray());
    }

    [Fact]
    public async Task 列表_已绑定但分店对不上_归为无法确定_计入未归属并可单独筛选()
    {
        // Tom 没有考勤数据，当前分店 Newcastle 由 Investment 发薪；换成一张 Australian 的单则对不上。
        _db.Insertable(new EmployeeMyobLink { UserGuid = "u-tom", ConnectionGuid = Aus, MyobEmployeeUid = "bbbbbbbb-0000-0000-0000-000000000009", BoundAt = Now }).ExecuteCommand();
        _api.Slips[Aus] = [Slip("bbbbbbbb-0000-0000-0000-000000000009", "Tom Sample", "2026-10-23")];
        AsAdmin();

        var all = (await Service().ListAsync(Q(), CancellationToken.None)).Data!;
        var row = Assert.Single(all.Rows);
        Assert.Equal(MyobPayslipPlacements.Unknown, row.Placement);
        Assert.Equal(1, all.UnassignedCount);

        var only = (await Service().ListAsync(Q(unassigned: true), CancellationToken.None)).Data!;
        Assert.Single(only.Rows);
    }

    [Fact]
    public async Task 列表_只看未归属_只包含未绑定与无法确定的行()
    {
        GivenSlips();
        AsAdmin();
        var only = (await Service().ListAsync(Q(unassigned: true), CancellationToken.None)).Data!;
        Assert.Equal([CardStranger], only.Rows.Select(row => row.EmployeeUid).ToArray());
    }

    [Fact]
    public async Task 列表_关键字匹配员工姓名或MYOB编号_不区分大小写()
    {
        GivenSlips();
        AsAdmin();
        var byName = (await Service().ListAsync(Q(keyword: "tom"), CancellationToken.None)).Data!;
        Assert.Equal([CardTom], byName.Rows.Select(row => row.EmployeeUid).ToArray());
        var byId = (await Service().ListAsync(Q(keyword: "E-Ja"), CancellationToken.None)).Data!;
        Assert.Equal(2, byId.Rows.Count); // Jane 在两家公司
    }

    [Fact]
    public async Task 列表_一家公司读取失败_状态单独标出_另一家的数据照常返回()
    {
        GivenSlips();
        _api.Errors[Aus] = new MyobApiException(MyobErrorCodes.ApiCallFailed, "MYOB 工资单报表接口返回 401", System.Net.HttpStatusCode.Unauthorized);
        AsAdmin();

        var list = (await Service().ListAsync(Q(), CancellationToken.None)).Data!;
        var failed = list.Companies.Single(status => status.ConnectionGuid == Aus);
        Assert.False(failed.Ok);
        Assert.Equal(MyobErrorCodes.ApiCallFailed, failed.ErrorCode);
        Assert.Contains("401", failed.ErrorMessage, StringComparison.Ordinal);
        Assert.True(list.Companies.Single(status => status.ConnectionGuid == Inv).Ok);
        Assert.Equal(3, list.TotalCount); // 只有 Investment 的 Jane、Tom、Stranger
    }

    [Fact]
    public async Task 列表_只列状态Active的连接_可按公司过滤()
    {
        GivenSlips();
        AsAdmin();
        var all = (await Service().ListAsync(Q(), CancellationToken.None)).Data!;
        Assert.DoesNotContain(all.Companies, status => status.ConnectionGuid == "conn-off");

        var onlyAus = (await Service().ListAsync(Q(connection: Aus), CancellationToken.None)).Data!;
        Assert.Single(onlyAus.Companies);
        Assert.Equal(1, onlyAus.TotalCount);
    }

    // ───────────────────────── 缓存与区间 ─────────────────────────

    [Fact]
    public async Task 列表_命中缓存不重复请求MYOB_refresh绕过缓存()
    {
        GivenSlips();
        AsAdmin();
        var service = Service();
        await service.ListAsync(Q(), CancellationToken.None);
        var callsAfterFirst = _api.Calls;
        Assert.Equal(2, callsAfterFirst); // 两家公司各一次

        await service.ListAsync(Q(storeCode: "1002", keyword: "jane"), CancellationToken.None); // 过滤条件不影响缓存键
        Assert.Equal(callsAfterFirst, _api.Calls);

        await service.ListAsync(Q(refresh: true), CancellationToken.None);
        Assert.Equal(callsAfterFirst + 2, _api.Calls);

        await service.ListAsync(Q(from: "2026-09-01"), CancellationToken.None); // 区间不同 = 不同缓存键
        Assert.Equal(callsAfterFirst + 4, _api.Calls);
    }

    [Fact]
    public async Task 列表_默认最近90天_区间含首尾_传给MYOB的是左闭右开()
    {
        GivenSlips();
        AsAdmin();
        var list = (await Service().ListAsync(new MyobPayslipQuery { ConnectionGuid = Inv }, CancellationToken.None)).Data!;
        Assert.Equal("2026-07-31", list.From);
        Assert.Equal("2026-10-29", list.To);
        var query = Assert.Single(_api.Queries);
        Assert.Equal(new DateTime(2026, 7, 31), query.PaymentFrom);
        Assert.Equal(new DateTime(2026, 10, 30), query.PaymentToExclusive); // to + 1 天
    }

    [Theory]
    [InlineData("2026-10-30", "2026-10-01")]   // 开始晚于结束
    [InlineData("2024-01-01", "2026-10-29")]   // 超过 24 个月
    [InlineData("10/01/2026", "2026-10-29")]   // 格式不对
    [InlineData("2026-10-01", "not-a-date")]
    public async Task 列表_区间不合法_返回InvalidRange_不请求MYOB(string from, string to)
    {
        GivenSlips();
        AsAdmin();
        var response = await Service().ListAsync(Q(from: from, to: to), CancellationToken.None);
        Assert.False(response.Success);
        Assert.Equal(MyobPayslipErrorCodes.InvalidRange, response.ErrorCode);
        Assert.Equal(0, _api.Calls);
    }

    [Fact]
    public void 区间_恰好24个月通过_多一天不通过()
    {
        var today = new DateTime(2026, 10, 29);
        Assert.True(MyobPayslipService.TryResolveRange("2024-10-29", "2026-10-29", today, out _, out _, out _)); // 731 天
        Assert.False(MyobPayslipService.TryResolveRange("2024-10-28", "2026-10-29", today, out _, out _, out _)); // 732 天
        Assert.True(MyobPayslipService.TryResolveRange(null, null, today, out var from, out var to, out _));
        Assert.Equal(today.AddDays(-90), from);
        Assert.Equal(today, to);
    }

    // ───────────────────────── 权限范围 ─────────────────────────

    [Fact]
    public async Task 权限_没有任何新权限码_拒绝_不请求MYOB()
    {
        GivenSlips();
        var response = await Service().ListAsync(Q(), CancellationToken.None);
        Assert.False(response.Success);
        Assert.Equal(MyobPayslipErrorCodes.Forbidden, response.ErrorCode);
        Assert.Equal(0, _api.Calls);

        var companies = await Service().GetCompaniesAsync(CancellationToken.None);
        Assert.Equal(MyobPayslipErrorCodes.Forbidden, companies.ErrorCode);
        var detail = await Service().GetDetailAsync(new MyobPayslipDetailRequest { ConnectionGuid = Inv, EmployeeUid = CardJane, PaymentDate = "2026-10-23" }, CancellationToken.None);
        Assert.Equal(MyobPayslipErrorCodes.Forbidden, detail.ErrorCode);
    }

    [Fact]
    public async Task 权限_未登录_拒绝()
    {
        GivenSlips();
        AsAdmin();
        _scope.Scope = new CurrentUserManageableStoreScope { IsAuthenticated = false, Message = "当前账号未登录" };
        var response = await Service().ListAsync(Q(), CancellationToken.None);
        Assert.Equal(MyobPayslipErrorCodes.Forbidden, response.ErrorCode);
        Assert.Equal(0, _api.Calls);
    }

    [Fact]
    public async Task 权限_店长有管辖分店权限但管理范围不允许_拒绝并带范围服务的提示()
    {
        _permissions.Granted.Add(Permissions.Myob.Payslip.ViewManagedStore);
        _scope.Scope = new CurrentUserManageableStoreScope { IsAuthenticated = true, IsAllowed = false, UserGuid = "u-mgr", Message = "当前账号没有店员管理权限" };
        var response = await Service().ListAsync(Q(), CancellationToken.None);
        Assert.Equal(MyobPayslipErrorCodes.Forbidden, response.ErrorCode);
        Assert.Equal("当前账号没有店员管理权限", response.Message);
    }

    [Fact]
    public async Task 权限_店长只看到与管辖分店有交集的已绑定员工_看不到未绑定和无法确定_未归属数量恒为0()
    {
        GivenSlips();
        AsManager("1005"); // 只管 Newcastle

        var list = (await Service().ListAsync(Q(), CancellationToken.None)).Data!;
        Assert.False(list.CanViewAllStores);
        Assert.Equal([CardTom], list.Rows.Select(row => row.EmployeeUid).ToArray());
        Assert.Equal(0, list.UnassignedCount);
        Assert.DoesNotContain(list.Rows, row => row.Placement is MyobPayslipPlacements.Unbound or MyobPayslipPlacements.Unknown);
        // 店长不是所有公司都看得到：Newcastle 由 Investment 发薪，Australian 不相关，不读取。
        Assert.Equal([Inv], list.Companies.Select(status => status.ConnectionGuid).ToArray());
        Assert.DoesNotContain(Aus, _api.Slips.Keys.Where(key => _api.Queried.Contains(key)));
    }

    [Fact]
    public async Task 权限_店长看到员工的整张工资单_包括别的分店标签_这是约定的取舍()
    {
        _api.Slips[Inv] = [Slip(CardTom, "Tom Sample", "2026-10-23")];
        _source.Pairs.AddRange([("u-tom", "1005"), ("u-tom", "1002")]);
        AsManager("1005");

        var row = Assert.Single((await Service().ListAsync(Q(), CancellationToken.None)).Data!.Rows);
        Assert.Equal(["1002", "1005"], row.Stores.Select(s => s.StoreCode).ToArray());
        Assert.Equal(1000m, row.GrossPay);
    }

    [Fact]
    public async Task 权限_店长按管辖范围以外的分店过滤_或只看未归属_拒绝()
    {
        GivenSlips();
        AsManager("1005");

        var otherStore = await Service().ListAsync(Q("1002"), CancellationToken.None);
        Assert.Equal(MyobPayslipErrorCodes.Forbidden, otherStore.ErrorCode);
        var unassigned = await Service().ListAsync(Q(unassigned: true), CancellationToken.None);
        Assert.Equal(MyobPayslipErrorCodes.Forbidden, unassigned.ErrorCode);
        Assert.Equal(0, _api.Calls);

        var own = await Service().ListAsync(Q("1005"), CancellationToken.None);
        Assert.True(own.Success);
    }

    [Fact]
    public async Task 权限_全部分店权限优先于管辖分店权限_管理员口径不受店长范围限制()
    {
        GivenSlips();
        _permissions.Granted.Add(Permissions.Myob.Payslip.ViewManagedStore);
        AsAdmin();
        _scope.Scope = new CurrentUserManageableStoreScope { IsAuthenticated = true, IsAllowed = true, UserGuid = "u-boss", StoreCodes = ["1005"] };
        var list = (await Service().ListAsync(Q(), CancellationToken.None)).Data!;
        Assert.True(list.CanViewAllStores);
        Assert.Equal(4, list.TotalCount);
    }

    [Fact]
    public async Task 公司下拉_管理员看到全部Active公司_店长只看到与管辖分店有关的公司()
    {
        AsAdmin();
        var all = (await Service().GetCompaniesAsync(CancellationToken.None)).Data!;
        Assert.Equal([Aus, Inv], all.Select(c => c.ConnectionGuid).OrderBy(x => x).ToArray());

        _permissions.Granted.Clear();
        AsManager("1003"); // Bankstown → Australian
        var scoped = (await Service().GetCompaniesAsync(CancellationToken.None)).Data!;
        Assert.Equal([Aus], scoped.Select(c => c.ConnectionGuid).ToArray());
    }

    [Fact]
    public async Task 公司下拉_店长管辖分店的员工被覆盖到别的公司_该公司也算相关()
    {
        // 1005 Newcastle 默认 Investment；其员工 u-tom 自 2026-10-01 起改由 Australian 发薪。
        _db.Insertable(new EmployeePayrollEmployer { UserGuid = "u-tom", StoreCode = "1005", ConnectionGuid = Aus, EffectiveFrom = new DateTime(2026, 10, 1) }).ExecuteCommand();
        AsManager("1005");
        var scoped = (await Service().GetCompaniesAsync(CancellationToken.None)).Data!;
        Assert.Equal([Aus, Inv], scoped.Select(c => c.ConnectionGuid).OrderBy(x => x).ToArray());
    }

    // ───────────────────────── 明细 + 查看记录 ─────────────────────────

    private static MyobPayslipDetailRequest D(string connection, string card, string date = "2026-10-23", string? periodStart = null) =>
        new() { ConnectionGuid = connection, EmployeeUid = card, PaymentDate = date, PeriodStart = periodStart };

    [Fact]
    public async Task 明细_成功_分区块展示_写一条查看记录_记录里没有金额和姓名()
    {
        GivenSlips();
        AsAdmin();
        _scope.Scope = new CurrentUserManageableStoreScope { IsAuthenticated = true, IsAllowed = true, IsAdmin = true, UserGuid = "u-finance" };

        var response = await Service().GetDetailAsync(D(Inv, CardJane), CancellationToken.None);

        Assert.True(response.Success);
        var detail = response.Data!;
        Assert.Equal("Jane Sample", detail.EmployeeName);
        Assert.Equal("Fortnightly", detail.PayFrequency);
        Assert.Equal(1000m, detail.GrossPay);
        Assert.Equal(800m, detail.NetPay);
        Assert.Equal("Base Hourly", Assert.Single(detail.Wages).Description);
        Assert.Equal(-200m, Assert.Single(detail.Taxes).Amount);
        Assert.Equal(115m, Assert.Single(detail.Superannuation).Amount);
        Assert.Equal("Sample Super", detail.SuperFundName);
        Assert.Empty(detail.Entitlements);
        Assert.Equal(["1002"], detail.Stores.Select(s => s.StoreCode).ToArray());

        // 只查这一名员工、这一天：MYOB 请求带员工筛选与单日窗口。
        var query = Assert.Single(_api.Queries);
        Assert.Equal(CardJane, query.EmployeeUid);
        Assert.Equal(new DateTime(2026, 10, 23), query.PaymentFrom);
        Assert.Equal(new DateTime(2026, 10, 24), query.PaymentToExclusive);

        var log = Assert.Single(await _db.Queryable<MyobPayslipViewLog>().ToListAsync());
        Assert.Equal("u-finance", log.ActorUserGuid);
        Assert.Equal(Inv, log.ConnectionGuid);
        Assert.Equal("u-jane", log.TargetUserGuid);
        Assert.Equal(CardJane, log.MyobEmployeeUid);
        Assert.Equal(new DateTime(2026, 10, 23), log.PaymentDate);
        Assert.Equal(Now, log.ViewedAt);
        // 查看记录实体只有这些列：没有金额、姓名等工资内容。
        Assert.Equal(
            ["ActorUserGuid", "ConnectionGuid", "LogGuid", "MyobEmployeeUid", "PaymentDate", "TargetUserGuid", "ViewedAt"],
            typeof(MyobPayslipViewLog).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task 明细_未知类型和Expense的行放进其它_Entitlement单独成块()
    {
        _api.Slips[Inv] =
        [
            Slip(CardJane, "Jane Sample", "2026-10-23", 1000m, 800m, null,
                new MyobPayrollAdviceLine("Annual Leave Accrual", "Entitlement", 4.35m, null, 4.35m, 51.7m),
                new MyobPayrollAdviceLine("Phone allowance", "Expense", null, null, 20m, 200m),
                new MyobPayrollAdviceLine("Mystery", "SomethingNew", null, null, 1m, 1m),
                new MyobPayrollAdviceLine("Union fee", "Deduction", null, null, -5m, -50m),
                new MyobPayrollAdviceLine("No type", null, null, null, 1m, 1m)),
        ];
        AsAdmin();
        var detail = (await Service().GetDetailAsync(D(Inv, CardJane), CancellationToken.None)).Data!;
        Assert.Equal("Annual Leave Accrual", Assert.Single(detail.Entitlements).Description);
        Assert.Equal("Union fee", Assert.Single(detail.Deductions).Description);
        Assert.Equal(["Phone allowance", "Mystery", "No type"], detail.Others.Select(l => l.Description).ToArray());
    }

    [Fact]
    public async Task 明细_店长看管辖外员工_找不到_不暴露存在_不写查看记录()
    {
        GivenSlips();
        AsManager("1005");

        var other = await Service().GetDetailAsync(D(Inv, CardJane), CancellationToken.None);
        Assert.Equal(MyobPayslipErrorCodes.NotFound, other.ErrorCode);
        var stranger = await Service().GetDetailAsync(D(Inv, CardStranger), CancellationToken.None);
        Assert.Equal(MyobPayslipErrorCodes.NotFound, stranger.ErrorCode);
        Assert.Empty(await _db.Queryable<MyobPayslipViewLog>().ToListAsync());

        // 管辖内的员工可以看，并留下记录。
        var own = await Service().GetDetailAsync(D(Inv, CardTom), CancellationToken.None);
        Assert.True(own.Success);
        Assert.Equal("u-mgr", Assert.Single(await _db.Queryable<MyobPayslipViewLog>().ToListAsync()).ActorUserGuid);
    }

    [Fact]
    public async Task 明细_未绑定员工_只有全部分店权限能看_记录里目标员工为空但留MYOB员工卡UID()
    {
        GivenSlips();
        AsAdmin();
        var response = await Service().GetDetailAsync(D(Inv, CardStranger), CancellationToken.None);
        Assert.True(response.Success);
        Assert.Equal(MyobPayslipPlacements.Unbound, response.Data!.Placement);
        Assert.Null(response.Data.UserGuid);
        var log = Assert.Single(await _db.Queryable<MyobPayslipViewLog>().ToListAsync());
        Assert.Null(log.TargetUserGuid);
        Assert.Equal(CardStranger, log.MyobEmployeeUid);
    }

    [Theory]
    [InlineData("not-a-guid", "2026-10-23")]
    [InlineData("aaaaaaaa-0000-0000-0000-000000000001", "23/10/2026")]
    [InlineData("x' or 1 eq 1 or 'a' eq 'a", "2026-10-23")]
    public async Task 明细_参数不合法_找不到_不请求MYOB(string card, string date)
    {
        GivenSlips();
        AsAdmin();
        var response = await Service().GetDetailAsync(D(Inv, card, date), CancellationToken.None);
        Assert.Equal(MyobPayslipErrorCodes.NotFound, response.ErrorCode);
        Assert.Equal(0, _api.Calls);
    }

    [Fact]
    public async Task 明细_公司未启用或不存在_找不到()
    {
        GivenSlips();
        AsAdmin();
        Assert.Equal(MyobPayslipErrorCodes.NotFound, (await Service().GetDetailAsync(D("conn-off", CardJane), CancellationToken.None)).ErrorCode);
        Assert.Equal(MyobPayslipErrorCodes.NotFound, (await Service().GetDetailAsync(D("nope", CardJane), CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task 明细_同一支付日有多张_用周期起始日区分_并提示张数()
    {
        _api.Slips[Inv] =
        [
            Slip(CardJane, "Jane Sample", "2026-10-23", 1000m, 800m, "2026-10-06"),
            Slip(CardJane, "Jane Sample", "2026-10-23", 300m, 250m, "2026-09-29"),
        ];
        AsAdmin();
        var first = (await Service().GetDetailAsync(D(Inv, CardJane), CancellationToken.None)).Data!;
        Assert.Equal(2, first.SamePaymentDateCount);
        var second = (await Service().GetDetailAsync(D(Inv, CardJane, periodStart: "2026-09-29"), CancellationToken.None)).Data!;
        Assert.Equal(300m, second.GrossPay);
        var none = await Service().GetDetailAsync(D(Inv, CardJane, periodStart: "2026-01-01"), CancellationToken.None);
        Assert.Equal(MyobPayslipErrorCodes.NotFound, none.ErrorCode);
    }

    [Fact]
    public async Task 明细_MYOB读取失败_返回MYOB错误码_不写查看记录()
    {
        GivenSlips();
        AsAdmin();
        _api.Errors[Inv] = new MyobApiException(MyobErrorCodes.ApiCallFailed, "MYOB 工资单报表接口返回 503");
        var response = await Service().GetDetailAsync(D(Inv, CardJane), CancellationToken.None);
        Assert.Equal(MyobErrorCodes.ApiCallFailed, response.ErrorCode);
        Assert.Empty(await _db.Queryable<MyobPayslipViewLog>().ToListAsync());
    }

    [Fact]
    public async Task 明细_记不下查看记录_拒绝显示_宁可不给看也不无痕地看()
    {
        GivenSlips();
        AsAdmin();
        _db.Ado.ExecuteCommand("DROP TABLE MyobPayslipViewLog");
        var response = await Service().GetDetailAsync(D(Inv, CardJane), CancellationToken.None);
        Assert.False(response.Success);
        Assert.Equal(MyobPayslipErrorCodes.ViewLogFailed, response.ErrorCode);
        Assert.Null(response.Data);
    }

    // ───────────────────────── 我方考勤工时（按需加载） ─────────────────────────

    [Fact]
    public async Task 工时_按分店汇总计薪工时和带薪假_周期取自工资单_只取这一名员工_不写查看记录()
    {
        _api.Slips[Inv] = [Slip(CardTom, "Tom Sample", "2026-10-23")];
        _source.Pairs.AddRange([("u-tom", "1005"), ("u-tom", "1002")]);
        _source.Hours[("1005", "u-tom")] = (WorkMinutes: 38 * 60 + 30, Sick: 2m, Annual: 4m, Holiday: 1.5m);
        _source.Hours[("1002", "u-tom")] = (WorkMinutes: 24 * 60, Sick: 0m, Annual: 0m, Holiday: 0m);
        AsAdmin();

        var response = await Service().GetHoursAsync(D(Inv, CardTom), CancellationToken.None);

        Assert.True(response.Success);
        var hours = response.Data!;
        Assert.Equal(["1002", "1005"], hours.Stores.Select(s => s.StoreCode).ToArray());
        var newcastle = hours.Stores.Single(s => s.StoreCode == "1005");
        Assert.Equal(38.5m, newcastle.WorkHours);
        Assert.Equal(7.5m, newcastle.LeaveHours); // 病假 2 + 年假 4 + 公假 1.5
        Assert.Equal("1005 Newcastle", newcastle.StoreLabel);
        Assert.Equal(24m, hours.Stores.Single(s => s.StoreCode == "1002").WorkHours);
        Assert.Equal(62.5m, hours.TotalWorkHours);
        Assert.Equal(7.5m, hours.TotalLeaveHours);

        // 周期取自 MYOB 工资单（10-10 ~ 10-19 共 10 天 → 2 周），不是客户端传的；只查这一名员工。
        Assert.All(_source.StoreCalls, call => Assert.Equal(["u-tom"], call.Users));
        Assert.All(_source.StoreCalls, call => Assert.Equal(new DateTime(2026, 10, 10), call.Start));
        Assert.All(_source.StoreCalls, call => Assert.Equal(2, call.Weeks));
        Assert.Empty(await _db.Queryable<MyobPayslipViewLog>().ToListAsync()); // 与明细同一次查看，不重复记
    }

    [Fact]
    public async Task 工时_单个分店读取失败_只标这一行_其它分店照常_不回显异常内容()
    {
        _api.Slips[Inv] = [Slip(CardTom, "Tom Sample", "2026-10-23")];
        _source.Pairs.AddRange([("u-tom", "1005"), ("u-tom", "1002")]);
        _source.Hours[("1005", "u-tom")] = (600, 0m, 0m, 0m);
        _source.FailStores.Add("1002");
        AsAdmin();

        var response = await Service().GetHoursAsync(D(Inv, CardTom), CancellationToken.None);

        Assert.True(response.Success);
        Assert.True(response.Data!.Stores.Single(s => s.StoreCode == "1002").Failed);
        Assert.False(response.Data.Stores.Single(s => s.StoreCode == "1005").Failed);
        Assert.Equal(10m, response.Data.TotalWorkHours);
        var json = JsonSerializer.Serialize(response);
        Assert.DoesNotContain("secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("1812", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 工时_未绑定或无法确定分店_返回空列表_不是错误_不调用考勤()
    {
        GivenSlips();
        AsAdmin();
        var unbound = await Service().GetHoursAsync(D(Inv, CardStranger), CancellationToken.None);
        Assert.True(unbound.Success);
        Assert.Empty(unbound.Data!.Stores);
        Assert.Empty(_source.StoreCalls);
    }

    [Fact]
    public async Task 工时_权限与明细一致_无权限403_店长看管辖外员工404_管辖内可看()
    {
        GivenSlips();
        _source.Hours[("1005", "u-tom")] = (480, 0m, 0m, 0m);

        var denied = await Service().GetHoursAsync(D(Inv, CardTom), CancellationToken.None);
        Assert.Equal(MyobPayslipErrorCodes.Forbidden, denied.ErrorCode);

        AsManager("1002"); // 只管 Wallsend：Tom 在 Newcastle，不在管辖内
        var outOfScope = await Service().GetHoursAsync(D(Inv, CardTom), CancellationToken.None);
        Assert.Equal(MyobPayslipErrorCodes.NotFound, outOfScope.ErrorCode);
        Assert.Empty(_source.StoreCalls); // 越权时一次考勤都不查

        _permissions.Granted.Clear();
        AsManager("1005");
        var own = await Service().GetHoursAsync(D(Inv, CardTom), CancellationToken.None);
        Assert.True(own.Success);
        Assert.Equal(8m, own.Data!.TotalWorkHours);
    }

    [Theory]
    [InlineData("not-a-guid", "2026-10-23")]
    [InlineData("aaaaaaaa-0000-0000-0000-000000000002", "bad-date")]
    public async Task 工时_参数不合法_找不到_不请求MYOB(string card, string date)
    {
        GivenSlips();
        AsAdmin();
        var response = await Service().GetHoursAsync(D(Inv, card, date), CancellationToken.None);
        Assert.Equal(MyobPayslipErrorCodes.NotFound, response.ErrorCode);
        Assert.Equal(0, _api.Calls);
    }

    [Fact]
    public async Task 工时_MYOB读取失败_返回MYOB错误码()
    {
        GivenSlips();
        AsAdmin();
        _api.Errors[Inv] = new MyobApiException(MyobErrorCodes.ApiCallFailed, "MYOB 工资单报表接口返回 503");
        var response = await Service().GetHoursAsync(D(Inv, CardTom), CancellationToken.None);
        Assert.Equal(MyobErrorCodes.ApiCallFailed, response.ErrorCode);
    }

    [Fact]
    public async Task 展示对象_序列化后不含年薪时薪生日支票号银行等字段()
    {
        GivenSlips();
        AsAdmin();
        var list = JsonSerializer.Serialize((await Service().ListAsync(Q(), CancellationToken.None)).Data);
        var detail = JsonSerializer.Serialize((await Service().GetDetailAsync(D(Inv, CardJane), CancellationToken.None)).Data);
        foreach (var json in new[] { list, detail })
        {
            foreach (var banned in new[] { "annualSalary", "hourlyRate", "dateOfBirth", "chequeNumber", "bsb", "accountNumber", "tfn" })
            {
                Assert.DoesNotContain(banned, json, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // ───────────────────────── 控制器 ─────────────────────────

    [Fact]
    public void 控制器_只要求已登录_权限在服务里按范围判断_三个动作都是只读GET且都不缓存()
    {
        var type = typeof(MyobPayslipController);
        var authorize = type.GetCustomAttribute<AuthorizeAttribute>()!;
        Assert.Null(authorize.Policy); // 不能写死单个权限码：两个权限码范围不同、任一满足即可
        Assert.Equal("api/react/v1/myob/payslips", type.GetCustomAttribute<RouteAttribute>()!.Template);

        var actions = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).ToList();
        Assert.Equal(4, actions.Count);
        Assert.All(actions, method => Assert.NotNull(method.GetCustomAttribute<HttpGetAttribute>()));
        Assert.DoesNotContain(actions, method => method.GetCustomAttributes().Any(a =>
            a is HttpPostAttribute or HttpPutAttribute or HttpDeleteAttribute or HttpPatchAttribute));
        Assert.Equal(
            ["", "companies", "detail", "hours"],
            actions.Select(method => method.GetCustomAttribute<HttpGetAttribute>()!.Template ?? string.Empty).OrderBy(t => t, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task 控制器_错误码映射_无权限403_找不到404_其余400_成功200_响应不缓存()
    {
        async Task<(int Status, string? CacheControl)> Call(ApiResponse<MyobPayslipListDto> result)
        {
            var controller = new MyobPayslipController(new StubPayslipService(result))
            {
                ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() },
            };
            var action = await controller.List(new MyobPayslipQuery(), CancellationToken.None);
            var status = action switch
            {
                OkObjectResult => 200,
                NotFoundObjectResult => 404,
                BadRequestObjectResult => 400,
                ObjectResult obj => obj.StatusCode ?? 0,
                _ => 0,
            };
            return (status, controller.Response.Headers.CacheControl.ToString());
        }

        Assert.Equal((200, "no-store"), await Call(ApiResponse<MyobPayslipListDto>.OK(new MyobPayslipListDto())));
        Assert.Equal((403, "no-store"), await Call(ApiResponse<MyobPayslipListDto>.Error("x", MyobPayslipErrorCodes.Forbidden)));
        Assert.Equal((404, "no-store"), await Call(ApiResponse<MyobPayslipListDto>.Error("x", MyobPayslipErrorCodes.NotFound)));
        Assert.Equal((400, "no-store"), await Call(ApiResponse<MyobPayslipListDto>.Error("x", MyobPayslipErrorCodes.InvalidRange)));
    }

    // ───────────────────────── 假对象 ─────────────────────────

    private sealed class StubPayslipService(ApiResponse<MyobPayslipListDto> list) : IMyobPayslipService
    {
        public Task<ApiResponse<List<MyobPayslipCompanyDto>>> GetCompaniesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiResponse<MyobPayslipListDto>> ListAsync(MyobPayslipQuery query, CancellationToken cancellationToken) => Task.FromResult(list);
        public Task<ApiResponse<MyobPayslipDetailDto>> GetDetailAsync(MyobPayslipDetailRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiResponse<MyobPayslipHoursDto>> GetHoursAsync(MyobPayslipDetailRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeApi : IMyobApiClient
    {
        public Dictionary<string, IReadOnlyList<MyobPayrollAdviceRecord>> Slips { get; } = new();
        public Dictionary<string, MyobApiException> Errors { get; } = new();
        public List<MyobPayrollAdviceQuery> Queries { get; } = [];
        public HashSet<string> Queried { get; } = [];
        public int Calls => Queries.Count;

        public Task<IReadOnlyList<MyobPayrollAdviceRecord>> GetPayrollAdvicesAsync(
            string businessId, string accessToken, MyobPayrollAdviceQuery query, CancellationToken cancellationToken)
        {
            Queries.Add(query);
            var connection = businessId["biz-".Length..];
            Queried.Add(connection);
            if (Errors.TryGetValue(connection, out var error))
            {
                return Task.FromException<IReadOnlyList<MyobPayrollAdviceRecord>>(error);
            }

            // 像真实 MYOB 一样在服务端按区间与员工筛选。
            IEnumerable<MyobPayrollAdviceRecord> rows = Slips.GetValueOrDefault(connection) ?? [];
            rows = rows.Where(row => row.PaymentDate >= query.PaymentFrom && row.PaymentDate < query.PaymentToExclusive);
            if (!string.IsNullOrWhiteSpace(query.EmployeeUid))
            {
                rows = rows.Where(row => row.EmployeeUid.Equals(query.EmployeeUid, StringComparison.OrdinalIgnoreCase));
            }

            return Task.FromResult<IReadOnlyList<MyobPayrollAdviceRecord>>(rows.OrderByDescending(row => row.PaymentDate).ToList());
        }

        public Task<MyobTokenResponse> ExchangeCodeAsync(string code, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MyobTokenResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MyobCompanyInfo> GetCompanyAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MyobEmployeeRecord>> GetEmployeesAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MyobEmployeePayrollRecord>> GetEmployeePayrollDetailsAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MyobEmployeePaymentRecord>> GetEmployeePaymentDetailsAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MyobWageCategoryRecord>> GetWageCategoriesAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MyobTimesheetStatus> GetTimesheetStatusAsync(string businessId, string accessToken, string employeeUid, DateTime startDate, DateTime endDate, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeTokens : IMyobTokenProvider
    {
        public Task<string> GetAccessTokenAsync(string connectionGuid, CancellationToken cancellationToken) => Task.FromResult("access-token");
        public void Invalidate(string connectionGuid) { }
    }

    private sealed class FakePermissions : IMyobPayslipPermissionChecker
    {
        public HashSet<string> Granted { get; } = new(StringComparer.Ordinal);
        public Task<bool> HasAsync(string userGuid, string permission) => Task.FromResult(Granted.Contains(permission));
    }

    private sealed class FakeSource : IAttendancePayrollPushSource
    {
        public List<(string UserGuid, string StoreCode)> Pairs { get; } = [];

        public Task<PayrollPushSettings> GetSettingsAsync() => Task.FromResult(new PayrollPushSettings(1, null));

        public Task<IReadOnlyList<(string UserGuid, string StoreCode)>> FindDataPairsAsync(
            IReadOnlyCollection<string> userGuids, DateTime periodStart, int weeks) =>
            Task.FromResult<IReadOnlyList<(string, string)>>(
                Pairs.Where(pair => userGuids.Contains(pair.UserGuid, StringComparer.OrdinalIgnoreCase)).ToList());

        // 每个 (店, 员工) 的工时分钟与带薪假小时；Fail 里的店模拟读取失败。
        public Dictionary<(string Store, string User), (int WorkMinutes, decimal Sick, decimal Annual, decimal Holiday)> Hours { get; } = new();
        public HashSet<string> FailStores { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Store, DateTime Start, int Weeks, string[] Users)> StoreCalls { get; } = [];

        public Task<PayrollPushStoreData> GetStoreDataAsync(string storeCode, DateTime periodStart, int weeks, IReadOnlyCollection<string> onlyUsers)
        {
            StoreCalls.Add((storeCode, periodStart, weeks, onlyUsers.ToArray()));
            if (FailStores.Contains(storeCode))
            {
                throw new InvalidOperationException("secret failure detail 1812.40");
            }

            var employees = onlyUsers
                .Where(user => Hours.ContainsKey((storeCode, user)))
                .Select(user =>
                {
                    var h = Hours[(storeCode, user)];
                    return new PayrollPushEmployee(
                        user, user, null, null, [],
                        [new PayrollWorkItem(periodStart, PayrollBucket.Weekday, h.WorkMinutes)],
                        [new PayrollLeaveItem(periodStart, h.Sick)],
                        [new PayrollLeaveItem(periodStart, h.Holiday)],
                        [new PayrollLeaveItem(periodStart, h.Annual)],
                        []);
                })
                .ToList();
            return Task.FromResult(new PayrollPushStoreData(storeCode, storeCode, false, employees));
        }
    }

    private sealed class FakeScope : ICurrentUserManageableStoreScopeService
    {
        // 默认已登录；管理员用 AsAdmin() 授权，店长用 AsManager() 设置范围。
        public CurrentUserManageableStoreScope Scope { get; set; } = new() { IsAuthenticated = true, IsAllowed = true, IsAdmin = true, UserGuid = "u-admin" };

        public Task<CurrentUserManageableStoreScope> GetScopeAsync() => Task.FromResult(Scope);
        public Task<IReadOnlyList<string>> GetAccessibleStoreCodesAsync() => throw new NotSupportedException();
        public Task<bool> CanAccessStoreCodeAsync(string storeCode) => throw new NotSupportedException();
        public Task<bool> CanAccessOrderAsync(string orderGuid) => throw new NotSupportedException();
        public Task<bool> CanManageStoreAsync(string storeGuid) => throw new NotSupportedException();
        public Task<bool> CanManageUserAsync(string userGuid) => throw new NotSupportedException();
    }

    private sealed class FakeTime(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
