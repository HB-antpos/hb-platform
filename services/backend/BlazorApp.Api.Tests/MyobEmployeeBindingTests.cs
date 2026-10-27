using System.Net;
using System.Reflection;
using System.Text;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Data.SchemaMigrations;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// MYOB 对接阶段 1：匹配打分（纯函数）、MYOB 员工接口分页、员工绑定服务（快照 / 确认 / 解绑 / 忽略 / 候选）、
/// 发薪公司（解析规则 / 列表 / 详情 / 保存）与控制器权限。SQLite 临时库 + 假 MYOB 名册，不访问真实 MYOB。
/// 注意：MYOB 员工接口的真实字段细节（EmployeePayrollDetails 的 DateOfBirth / Entitlements 命名等）按官方文档实现，
/// 需要用已授权的公司文件在线上只读核对。
/// </summary>
public sealed class MyobEmployeeBindingTests : IDisposable
{
    // 固定一个周三：当前周期（1 周一期）起始日 = 2026-10-26（周一）。
    private static readonly DateTime Now = new(2026, 10, 28, 3, 0, 0, DateTimeKind.Utc);
    private const string Inv = "conn-inv";
    private const string Aus = "conn-aus";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hb-myob-emp-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;
    private readonly FakeDirectory _directory = new();
    private readonly FakeScope _scope = new();
    private readonly FakeTime _time = new(Now);

    public MyobEmployeeBindingTests()
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
            typeof(MyobEmployeeIgnore), typeof(Store), typeof(User), typeof(UserStore), typeof(EmployeeProfile),
            typeof(EmployeeMinorCompliance), typeof(AttendanceSettings));
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private MyobEmployeeBindingService Binding() => new(_db, _directory, _time);

    private MyobPayrollEmployerService Employers() => new(_db, _scope, _time);

    // ───────────────────────── 种子数据 ─────────────────────────

    private async Task SeedBaseAsync()
    {
        foreach (var (guid, name) in new[] { (Inv, "Hot Bargain Investment Pty Ltd"), (Aus, "Hot Bargain Australian Pty Ltd") })
        {
            await _db.Insertable(new MyobConnection
            {
                ConnectionGuid = guid,
                BusinessId = $"biz-{guid}",
                BusinessName = name,
                Status = MyobConnectionStatuses.Active,
                AuthorizedAt = Now,
                CreatedAt = Now,
            }).ExecuteCommandAsync();
        }

        await SeedStoreAsync("1014", "Kawana", Inv);
        await SeedStoreAsync("1013", "Orion", Aus);
        await SeedStoreAsync("1042", "TestStore", null);
    }

    private async Task SeedStoreAsync(string code, string name, string? connectionGuid)
    {
        await _db.Insertable(new Store { StoreGUID = $"g-{code}", StoreCode = code, StoreName = name, IsActive = true }).ExecuteCommandAsync();
        if (connectionGuid is not null)
        {
            await _db.Insertable(new MyobStoreMapping { StoreCode = code, ConnectionGuid = connectionGuid, MappedAt = Now }).ExecuteCommandAsync();
        }
    }

    private async Task SeedEmployeeAsync(
        string guid,
        string given,
        string family,
        string? email,
        string? phone,
        DateTime? birthday,
        EmployeeType? type,
        params string[] storeCodes)
    {
        await _db.Insertable(new User
        {
            UserGUID = guid,
            Username = guid,
            Email = email ?? string.Empty,
            FullName = $"{given} {family}",
            GivenName = given,
            FamilyName = family,
            IsActive = true,
            CreatedAt = Now.AddDays(-30),
        }).ExecuteCommandAsync();
        await _db.Insertable(new EmployeeProfile { UserGUID = guid, Phone = phone, Birthday = birthday, EmployeeType = type }).ExecuteCommandAsync();
        foreach (var code in storeCodes)
        {
            await _db.Insertable(new UserStore { UserStoreGUID = Guid.NewGuid().ToString(), UserGUID = guid, StoreGUID = $"g-{code}" }).ExecuteCommandAsync();
        }
    }

    private static MyobEmployeeRecord Card(
        string uid, string first, string last, string? email = null, string? phone = null, bool active = true) =>
        new(uid, null, first, last, null, true, active,
            email is null ? [] : [email], phone is null ? [] : [phone]);

    private static MyobEmployeePayrollRecord Payroll(
        string uid, DateTime? dob, string? status = "FullTime", bool annual = true, bool sick = true) =>
        new(uid, dob, status,
        [
            new MyobEntitlementRecord("Holiday Leave Accrual", annual),
            new MyobEntitlementRecord("Sick Leave Accrual", sick),
        ]);

    private async Task SetDirectoryAsync(
        string connectionGuid, IReadOnlyList<MyobEmployeeRecord> cards, IReadOnlyList<MyobEmployeePayrollRecord> payroll)
    {
        var connection = await _db.Queryable<MyobConnection>().FirstAsync(item => item.ConnectionGuid == connectionGuid);
        _directory.Set(connectionGuid, MyobEmployeeDirectory.Build(connection, cards, payroll, Now));
    }

    private static readonly DateTime D1996 = new(1996, 3, 12);

    /// <summary>一套覆盖各种状态的 Investment（1014 Kawana）名单。</summary>
    private async Task SeedScenarioAsync()
    {
        await SeedBaseAsync();
        await SeedEmployeeAsync("u-mei", "Mei Lin", "Zhang", "mei.zhang@example.com", "0412 345 678", D1996, EmployeeType.FullTime, "1014");
        await SeedEmployeeAsync("u-tom", "Tom", "Anderson", "tom.a@example.com", "0411 000 111", new DateTime(2001, 1, 19), EmployeeType.PartTime, "1014");
        await SeedEmployeeAsync("u-yuki", "Yuki", "Tanaka", "yuki.t@example.com", "0411 222 333", null, EmployeeType.Temporary, "1014");
        await SeedEmployeeAsync("u-liam", "Liam", "O'Brien", "liam.ob@example.com", "0400 999 888", null, EmployeeType.PartTime, "1014");
        await SeedEmployeeAsync("u-sarah", "Sarah", "Kim", "sarah.k@example.com", "0433 100 200", new DateTime(1997, 6, 30), EmployeeType.FullTime, "1014");
        await SeedEmployeeAsync("u-chloe", "Chloe", "Wang", "sarah.k@example.com", null, null, EmployeeType.PartTime, "1014");

        await SetDirectoryAsync(Inv,
        [
            Card("c-mei", "Mei Lin", "Zhang", "mei.zhang@example.com", "+61412345678"),
            Card("c-tom", "Thomas", "Anderson", "tom.a@example.com", "0411000111"),
            Card("c-yuki", "Yuki", "Tanaka", "yuki.tanaka@old-mail.com", "0411 222 333"),
            Card("c-sarah", "Sarah", "Kim", "sarah.k@example.com", "0433100200"),
            Card("c-daniel", "Daniel", "Moore", "daniel.m@example.com"),
            Card("c-kevin", "Kevin", "Lam", active: false),
        ],
        [
            Payroll("c-mei", D1996),
            Payroll("c-tom", new DateTime(2001, 1, 19), "PartTime"),
            Payroll("c-yuki", null, "Casual", annual: true, sick: false),
            // Sarah 是全职，但 MYOB 没挂任何假期累计 → 用工类型检查应报「假期余额拉不到」。
            Payroll("c-sarah", new DateTime(1997, 6, 30), "FullTime", annual: false, sick: false),
            Payroll("c-daniel", new DateTime(1988, 9, 2)),
            Payroll("c-kevin", null),
        ]);

        // Sarah 已绑定 c-sarah。
        await _db.Insertable(new EmployeeMyobLink
        {
            LinkGuid = "link-sarah", UserGuid = "u-sarah", ConnectionGuid = Inv, MyobEmployeeUid = "c-sarah",
            MyobName = "Sarah Kim", MatchMethod = MyobMatchMethods.Manual, MatchScore = 100, BoundAt = Now.AddDays(-1),
        }).ExecuteCommandAsync();
    }

    // ───────────────────────── 匹配打分 ─────────────────────────

    private static MyobMatchSubject Subject(
        string? given = "Mei Lin", string? family = "Zhang", string? full = null, string? email = "mei.zhang@example.com",
        string? phone = "0412 345 678", DateTime? birthday = null) =>
        new("u", given, family, full ?? $"{given} {family}", email, phone, birthday ?? D1996);

    private static MyobMatchCard MatchCard(
        string first = "Mei Lin", string last = "Zhang", string[]? emails = null, string[]? phones = null,
        DateTime? dob = null, bool active = true, bool individual = true, string? company = null) =>
        new("c", first, last, company, individual, emails ?? ["mei.zhang@example.com"], phones ?? ["+61 412 345 678"], dob ?? D1996, active);

    [Fact]
    public void 打分_四项都一致_满分且是高分()
    {
        var result = MyobEmployeeMatcher.Evaluate(Subject(), MatchCard());
        Assert.Equal(100, result.Score);
        Assert.True(result.IsHigh);
        Assert.Equal(MyobSignalResults.Match, result.Email);
        Assert.Equal(MyobSignalResults.Match, result.Birthday);
        Assert.Equal(MyobSignalResults.Match, result.Name);
        Assert.Equal(MyobSignalResults.Match, result.Phone);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void 打分_手机写法不同只比末8位_邮箱不分大小写()
    {
        var result = MyobEmployeeMatcher.Evaluate(
            Subject(email: "  Mei.Zhang@Example.com "), MatchCard(phones: ["61412345678"]));
        Assert.Equal(MyobSignalResults.Match, result.Email);
        Assert.Equal(MyobSignalResults.Match, result.Phone);
    }

    [Fact]
    public void 打分_手机不一致不扣分_但会在说明里提示_其余都对上()
    {
        var result = MyobEmployeeMatcher.Evaluate(Subject(), MatchCard(phones: ["0499 888 777"]));
        Assert.Equal(MyobSignalResults.Mismatch, result.Phone);
        Assert.Equal(90, result.Score);
        Assert.True(result.IsHigh);
        Assert.Contains("手机不一致，其余都对上", result.Notes);
    }

    [Fact]
    public void 打分_邮箱和生日必须都一致才算高分_缺生日就是需确认()
    {
        // 「我方没有生日」：生日信号未知，即使邮箱、姓名、手机都对上也不能批量确认。
        var subject = new MyobMatchSubject("u", "Mei Lin", "Zhang", "Mei Lin Zhang", "mei.zhang@example.com", "0412 345 678", null);
        var result = MyobEmployeeMatcher.Evaluate(subject, MatchCard());
        Assert.Equal(MyobSignalResults.Unknown, result.Birthday);
        Assert.Equal(70, result.Score); // 45 + 15 + 10
        Assert.True(result.HasPositiveEvidence);
        Assert.False(result.IsHigh);
    }

    [Fact]
    public void 打分_生日不一致扣分且不能是高分_说明里写明()
    {
        var result = MyobEmployeeMatcher.Evaluate(Subject(), MatchCard(dob: new DateTime(1990, 5, 8)));
        Assert.Equal(MyobSignalResults.Mismatch, result.Birthday);
        Assert.False(result.IsHigh);
        Assert.Contains("生日与我方不一致", result.Notes);
        Assert.Equal(60, result.Score); // 45 + 15 + 10 − 10
    }

    [Fact]
    public void 打分_姓名写法相近_Tom与Thomas_只给少量分并说明()
    {
        var subject = Subject(given: "Tom", family: "Anderson", email: "tom.a@example.com", phone: "0411 000 111", birthday: new DateTime(2001, 1, 19));
        var result = MyobEmployeeMatcher.Evaluate(subject, MatchCard("Thomas", "Anderson", ["tom.a@example.com"], ["0411000111"], new DateTime(2001, 1, 19)));
        Assert.True(result.NamePartial);
        Assert.Equal(MyobSignalResults.Mismatch, result.Name);
        Assert.Equal(91, result.Score); // 45 + 30 + 6 + 10
        Assert.True(result.IsHigh);
        Assert.Contains("姓名写法不同（Tom / Thomas）", result.Notes);
    }

    [Theory]
    [InlineData("Tom", "Thomas", true)]
    [InlineData("Ben", "Benjamin", true)]
    [InlineData("Jo", "Jonathan", true)]
    [InlineData("Bob", "Robert", false)]   // 首字母不同
    [InlineData("Tim", "Thomas", false)]   // 不是子序列
    [InlineData("A", "Alex", false)]       // 太短
    public void 名字简称关系(string a, string b, bool expected)
    {
        Assert.Equal(expected, MyobEmployeeMatcher.IsNicknameOf(a, b));
        Assert.Equal(expected, MyobEmployeeMatcher.IsNicknameOf(b, a));
    }

    [Theory]
    [InlineData("Mei Lin", "Zhang", "Zhang", "Mei Lin")]   // 名姓顺序颠倒
    [InlineData("José", "García", "Jose", "Garcia")]       // 变音符号
    [InlineData("ANNA", "lee", "Anna", "Lee")]             // 大小写
    public void 姓名_顺序_变音符号_大小写都不影响一致(string ourGiven, string ourFamily, string first, string last)
    {
        var (signal, partial) = MyobEmployeeMatcher.NameSignal(
            Subject(given: ourGiven, family: ourFamily), MatchCard(first, last));
        Assert.Equal(MyobSignalResults.Match, signal);
        Assert.False(partial);
    }

    [Fact]
    public void 姓名_只有全名时按全名切词比对_非个人卡用公司名()
    {
        var subject = new MyobMatchSubject("u", null, null, "Wei Chen", null, null, null);
        Assert.Equal(MyobSignalResults.Match, MyobEmployeeMatcher.NameSignal(subject, MatchCard("Wei", "Chen")).Signal);
        Assert.Equal(MyobSignalResults.Match,
            MyobEmployeeMatcher.NameSignal(subject, MatchCard("x", "y", individual: false, company: "Chen Wei")).Signal);
        Assert.Equal(MyobSignalResults.Mismatch, MyobEmployeeMatcher.NameSignal(subject, MatchCard("Wei", "Zhang")).Signal);
        Assert.Equal(MyobSignalResults.Unknown,
            MyobEmployeeMatcher.NameSignal(new MyobMatchSubject("u", null, null, null, null, null, null), MatchCard()).Signal);
    }

    [Fact]
    public void 打分_没有任何正向证据不会成为候选_停用的卡永远不是高分()
    {
        var nothing = MyobEmployeeMatcher.Evaluate(
            Subject(), MatchCard("Other", "Person", ["x@y.com"], ["0300 111 222"], new DateTime(1980, 1, 1)));
        Assert.False(nothing.HasPositiveEvidence);
        Assert.False(nothing.IsHigh);

        var inactive = MyobEmployeeMatcher.Evaluate(Subject(), MatchCard(active: false));
        Assert.Equal(100, inactive.Score);
        Assert.False(inactive.IsHigh);
        Assert.Contains("MYOB 里这张卡已停用", inactive.Notes);
    }

    [Theory]
    [InlineData("0412 345 678", "•••• 5678")]
    [InlineData("+61 (0)412-345-678", "•••• 5678")]
    [InlineData("123", null)]
    [InlineData(null, null)]
    public void 手机掩码只留末4位(string? input, string? expected) =>
        Assert.Equal(expected, MyobEmployeeMatcher.MaskPhone(input));

    // ───────────────────────── MYOB 员工接口（分页 / 解析） ─────────────────────────

    private static MyobApiClient ClientFor(StubHandler handler) =>
        new(new HttpClient(handler), Options.Create(new MyobOptions
        {
            ClientId = "client-key",
            ClientSecret = "client-secret",
            RedirectUri = "https://hotbargain.vip/api/myob/oauth/callback",
            MinRequestIntervalMs = 0,
        }));

    [Fact]
    public async Task ApiClient_员工列表_沿NextPageLink翻页_解析邮箱手机_请求头齐全()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsoluteUri.Contains("skip=1", StringComparison.Ordinal)
            ? Json("""{"Items":[{"UID":"u2","FirstName":"B","LastName":"Two","IsActive":false,"Addresses":[]}],"NextPageLink":null}""")
            : Json("""
                {"Items":[{"UID":"u1","DisplayID":"EMP001","FirstName":"Mei","LastName":"Zhang","IsIndividual":true,"IsActive":true,
                  "Addresses":[{"Location":1,"Email":"mei@example.com","Phone1":"0412 345 678","Phone2":"","Phone3":"0400 111 222"},
                               {"Location":2,"Email":"MEI@example.com","Phone1":"0412 345 678"}]}],
                 "NextPageLink":"https://api.myob.com/accountright/biz-1/Contact/Employee?$top=1000&$skip=1"}
                """));
        var employees = await ClientFor(handler).GetEmployeesAsync("biz-1", "token-1", CancellationToken.None);

        Assert.Equal(2, employees.Count);
        var first = employees[0];
        Assert.Equal("u1", first.Uid);
        Assert.Equal("EMP001", first.DisplayId);
        Assert.Equal(["mei@example.com"], first.Emails); // 去重且不分大小写
        Assert.Equal(["0412 345 678", "0400 111 222"], first.Phones);
        Assert.False(employees[1].IsActive);

        Assert.Equal(2, handler.Requests.Count);
        Assert.StartsWith("https://api.myob.com/accountright/biz-1/Contact/Employee?$top=1000", handler.Requests[0].Url, StringComparison.Ordinal);
        Assert.Equal("Bearer token-1", handler.Requests[0].Headers["Authorization"]);
        Assert.Equal("client-key", handler.Requests[0].Headers["x-myobapi-key"]);
        Assert.Equal("v2", handler.Requests[0].Headers["x-myobapi-version"]);
    }

    [Fact]
    public async Task ApiClient_NextPageLink指向别的主机不跟随_防止被带到别处()
    {
        var handler = new StubHandler(_ => Json(
            """{"Items":[{"UID":"u1","FirstName":"A","LastName":"B"}],"NextPageLink":"https://evil.example.com/steal?x=1"}"""));
        var employees = await ClientFor(handler).GetEmployeesAsync("biz-1", "t", CancellationToken.None);
        Assert.Single(employees);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ApiClient_员工工资详情_解析生日雇佣状态和假期累计类别()
    {
        var handler = new StubHandler(_ => Json("""
            {"Items":[{"UID":"d1","Employee":{"UID":"u1","Name":"Mei Zhang","DisplayID":"EMP001"},
              "DateOfBirth":"1996-03-12T00:00:00","EmploymentStatus":"PartTime",
              "Entitlements":[{"EntitlementCategory":{"UID":"c1","Name":"Holiday Leave Accrual"},"IsAssigned":true,"CarryOver":0,"YearToDate":10,"Total":10},
                              {"EntitlementCategory":{"UID":"c2","Name":"Sick Leave Accrual"},"IsAssigned":false}]},
             {"UID":"d2","Employee":null}],"NextPageLink":null}
            """));
        var details = await ClientFor(handler).GetEmployeePayrollDetailsAsync("biz-1", "t", CancellationToken.None);

        var detail = Assert.Single(details); // 没有 Employee.UID 的行被丢弃
        Assert.Equal("u1", detail.EmployeeUid);
        Assert.Equal(new DateTime(1996, 3, 12), detail.DateOfBirth);
        Assert.Equal("PartTime", detail.EmploymentStatus);
        Assert.Equal(2, detail.Entitlements.Count);
        Assert.True(detail.Entitlements[0].IsAssigned);
        Assert.StartsWith("https://api.myob.com/accountright/biz-1/Contact/EmployeePayrollDetails?$top=1000", handler.Requests[0].Url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task ApiClient_非2xx统一报ApiCallFailed_消息只含状态码(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent("secret-body-should-not-leak") });
        var error = await Assert.ThrowsAsync<MyobApiException>(() =>
            ClientFor(handler).GetEmployeesAsync("biz-1", "t", CancellationToken.None));
        Assert.Equal(MyobErrorCodes.ApiCallFailed, error.Code);
        Assert.Contains(((int)status).ToString(), error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-body", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 假期累计类别按默认名称识别_年假与病假()
    {
        Assert.True(MyobEmployeeDirectory.IsAnnualCategory("Holiday Leave Accrual"));
        Assert.True(MyobEmployeeDirectory.IsAnnualCategory("Annual Leave"));
        Assert.True(MyobEmployeeDirectory.IsSickCategory("Sick Leave Accrual"));
        Assert.True(MyobEmployeeDirectory.IsSickCategory("Personal Leave"));
        Assert.False(MyobEmployeeDirectory.IsAnnualCategory("Sick Leave Accrual"));
        Assert.False(MyobEmployeeDirectory.IsSickCategory(null));
    }

    // ───────────────────────── 发薪公司解析规则 ─────────────────────────

    private static EmployeePayrollEmployer Row(string? connection, string from) => new()
    {
        UserGuid = "u", StoreCode = "1013", ConnectionGuid = connection, EffectiveFrom = DateTime.Parse(from),
    };

    [Fact]
    public void 解析规则_没有覆盖用分店默认_覆盖按生效日取最新_空值表示改回按分店()
    {
        var date = new DateTime(2026, 10, 28);
        Assert.Equal((Aus, false, (DateTime?)null), MyobPayrollEmployerRules.Resolve(Aus, [], date));
        Assert.Equal((null, false, (DateTime?)null), MyobPayrollEmployerRules.Resolve(null, [], date));

        var rows = new[] { Row(Inv, "2026-10-05"), Row(null, "2026-10-19"), Row(Inv, "2026-11-02") };
        // 10-28：最新已生效的是 10-19 那行（改回按分店）；11-02 还没到。
        Assert.Equal((Aus, false, (DateTime?)new DateTime(2026, 10, 19)), MyobPayrollEmployerRules.Resolve(Aus, rows, date));
        Assert.Equal((Inv, true, (DateTime?)new DateTime(2026, 11, 2)), MyobPayrollEmployerRules.Resolve(Aus, rows, new DateTime(2026, 11, 2)));
        Assert.Equal((Inv, true, (DateTime?)new DateTime(2026, 10, 5)), MyobPayrollEmployerRules.Resolve(Aus, rows, new DateTime(2026, 10, 10)));
    }

    // ───────────────────────── 绑定快照 ─────────────────────────

    [Fact]
    public async Task 快照_各状态分类_排序_计数()
    {
        await SeedScenarioAsync();
        var snapshot = (await Binding().GetSnapshotAsync(Inv, false, CancellationToken.None)).Data!;

        string StatusOf(string user) => snapshot.Rows.Single(row => row.UserGuid == user).Status;
        Assert.Equal(MyobBindingStatuses.Suggested, StatusOf("u-mei"));
        Assert.Equal(MyobBindingStatuses.Suggested, StatusOf("u-tom"));      // Tom / Thomas 仍是高分
        Assert.Equal(MyobBindingStatuses.NeedsReview, StatusOf("u-yuki"));   // 邮箱不同、无生日
        Assert.Equal(MyobBindingStatuses.Bound, StatusOf("u-sarah"));
        Assert.Equal(MyobBindingStatuses.Conflict, StatusOf("u-chloe"));     // 共用邮箱，最像的卡已被 Sarah 绑定
        Assert.Equal(MyobBindingStatuses.Unmatched, StatusOf("u-liam"));

        var chloe = snapshot.Rows.Single(row => row.UserGuid == "u-chloe");
        Assert.Equal("Sarah Kim", chloe.Candidate!.BoundToUserName);
        Assert.Equal("c-sarah", chloe.Candidate.Card.Uid);

        var mei = snapshot.Rows.Single(row => row.UserGuid == "u-mei");
        Assert.Equal("c-mei", mei.Candidate!.Card.Uid);
        Assert.Equal(100, mei.Candidate.Score);
        Assert.Equal("•••• 5678", mei.Candidate.Card.PhoneMasked);

        // 排序：高分建议 → 需确认/冲突 → 未匹配 → 已绑定
        Assert.Equal(
            // 同一档内按候选得分降序：冲突（共用邮箱 45 分）排在需确认（Yuki 20 分）前面。
            [MyobBindingStatuses.Suggested, MyobBindingStatuses.Suggested, MyobBindingStatuses.Conflict,
             MyobBindingStatuses.NeedsReview, MyobBindingStatuses.Unmatched, MyobBindingStatuses.Bound],
            snapshot.Rows.Select(row => row.Status).ToArray());

        Assert.Equal(6, snapshot.Counts.All);
        Assert.Equal(1, snapshot.Counts.Bound);
        Assert.Equal(2, snapshot.Counts.Suggested);
        Assert.Equal(2, snapshot.Counts.NeedsReview); // NeedsReview + Conflict
        Assert.Equal(1, snapshot.Counts.Unmatched);
        Assert.Equal(2, snapshot.Counts.BatchConfirmable);
        Assert.Equal("Hot Bargain Investment Pty Ltd", snapshot.ConnectionName);
    }

    [Fact]
    public async Task 快照_我方有MYOB没有_和_MYOB有我方没有_含停用卡和复制资料()
    {
        await SeedScenarioAsync();
        var snapshot = (await Binding().GetSnapshotAsync(Inv, false, CancellationToken.None)).Data!;

        var liam = Assert.Single(snapshot.OnlyInOurs);
        Assert.Equal("u-liam", liam.UserGuid);
        Assert.Contains("First name: Liam", liam.CopyText, StringComparison.Ordinal);
        Assert.Contains("Last name: O'Brien", liam.CopyText, StringComparison.Ordinal);
        Assert.Contains("Email: liam.ob@example.com", liam.CopyText, StringComparison.Ordinal);
        Assert.Contains("Phone: 0400 999 888", liam.CopyText, StringComparison.Ordinal);
        Assert.DoesNotContain("Date of birth", liam.CopyText, StringComparison.Ordinal); // 没填生日就不写

        // c-mei / c-tom / c-yuki 被建议给员工，c-sarah 已绑定 → 只剩 daniel 与已停用的 kevin；启用的排前面。
        Assert.Equal(["c-daniel", "c-kevin"], snapshot.OnlyInMyob.Select(item => item.Card.Uid).ToArray());
        Assert.False(snapshot.OnlyInMyob[1].Card.IsActive);
    }

    [Fact]
    public async Task 快照_用工类型检查_全职没挂累计_临时工挂了累计()
    {
        await SeedScenarioAsync();
        // Yuki（临时工）绑定到挂了年假累计的 c-yuki。
        await _db.Insertable(new EmployeeMyobLink
        {
            LinkGuid = "link-yuki", UserGuid = "u-yuki", ConnectionGuid = Inv, MyobEmployeeUid = "c-yuki",
            MatchMethod = MyobMatchMethods.Manual, BoundAt = Now,
        }).ExecuteCommandAsync();

        var snapshot = (await Binding().GetSnapshotAsync(Inv, false, CancellationToken.None)).Data!;
        Assert.Equal(2, snapshot.Counts.TypeIssues);
        var sarah = snapshot.TypeIssues.Single(item => item.UserGuid == "u-sarah");
        Assert.Equal(MyobTypeIssueCodes.MissingLeaveEntitlement, sarah.IssueCode);
        Assert.False(sarah.HasAnnualEntitlement);
        var yuki = snapshot.TypeIssues.Single(item => item.UserGuid == "u-yuki");
        Assert.Equal(MyobTypeIssueCodes.TemporaryHasLeaveEntitlement, yuki.IssueCode);
        Assert.True(yuki.HasAnnualEntitlement);
    }

    [Fact]
    public async Task 快照_两个员工争同一张卡_只有一个拿到建议()
    {
        await SeedBaseAsync();
        await SeedEmployeeAsync("u-wei1", "Wei", "Chen", null, null, null, EmployeeType.PartTime, "1014");
        await SeedEmployeeAsync("u-wei2", "Wei", "Chen", null, null, null, EmployeeType.PartTime, "1014");
        await SetDirectoryAsync(Inv, [Card("c-wei", "Wei", "Chen")], [Payroll("c-wei", null)]);

        var snapshot = (await Binding().GetSnapshotAsync(Inv, false, CancellationToken.None)).Data!;
        var withCandidate = snapshot.Rows.Where(row => row.Candidate is not null).ToList();
        Assert.Single(withCandidate);
        Assert.Equal(MyobBindingStatuses.NeedsReview, withCandidate[0].Status); // 只有姓名一致，不是高分
        Assert.Equal(1, snapshot.Rows.Count(row => row.Status == MyobBindingStatuses.Unmatched));
        Assert.Empty(snapshot.OnlyInMyob); // 卡已被建议给其中一人，不算「MYOB 有、我方没有」
    }

    [Fact]
    public async Task 快照_跨公司员工_在Orion上班但由Investment发薪_出现在Investment名单并带标记()
    {
        await SeedScenarioAsync();
        await SeedEmployeeAsync("u-jason", "Jason", "Nguyen", "jason.n@example.com", null, null, EmployeeType.PartTime, "1013");
        await _db.Insertable(new EmployeePayrollEmployer
        {
            EmployerGuid = "emp-1", UserGuid = "u-jason", StoreCode = "1013", ConnectionGuid = Inv,
            EffectiveFrom = new DateTime(2026, 10, 26), CreatedAt = Now,
        }).ExecuteCommandAsync();

        var inv = (await Binding().GetSnapshotAsync(Inv, false, CancellationToken.None)).Data!;
        var jason = inv.Rows.Single(row => row.UserGuid == "u-jason");
        Assert.True(jason.IsCrossCompany);
        Assert.Equal(["1013 Orion"], jason.StoreLabels);

        // 对 Australian：Jason 不再出现（他的唯一分店今天由 Investment 发薪）。
        await SetDirectoryAsync(Aus, [], []);
        var aus = (await Binding().GetSnapshotAsync(Aus, false, CancellationToken.None)).Data!;
        Assert.DoesNotContain(aus.Rows, row => row.UserGuid == "u-jason");
    }

    [Fact]
    public async Task 快照_MYOB拉取失败_原样返回错误码_连接未启用返回错误()
    {
        await SeedScenarioAsync();
        _directory.Error = new MyobApiException(MyobErrorCodes.NeedsReauthorization, "MYOB 授权已失效");
        var failed = await Binding().GetSnapshotAsync(Inv, false, CancellationToken.None);
        Assert.False(failed.Success);
        Assert.Equal(MyobErrorCodes.NeedsReauthorization, failed.ErrorCode);

        _directory.Error = null;
        await _db.Updateable<MyobConnection>().SetColumns(item => item.Status == MyobConnectionStatuses.NeedsReauth)
            .Where(item => item.ConnectionGuid == Inv).ExecuteCommandAsync();
        var inactive = await Binding().GetSnapshotAsync(Inv, false, CancellationToken.None);
        Assert.Equal(MyobErrorCodes.ConnectionNotActive, inactive.ErrorCode);
    }

    // ───────────────────────── 确认 / 解绑 / 忽略 ─────────────────────────

    private static MyobConfirmBindingsRequest Confirm(params (string User, string Card, bool Manual)[] items) => new()
    {
        ConnectionGuid = Inv,
        Items = items.Select(item => new MyobConfirmBindingItem { UserGuid = item.User, MyobEmployeeUid = item.Card, Manual = item.Manual }).ToList(),
    };

    [Fact]
    public async Task 确认_高分建议一键绑定_记录方式分数和绑定人()
    {
        await SeedScenarioAsync();
        var result = (await Binding().ConfirmAsync(Confirm(("u-mei", "c-mei", false), ("u-tom", "c-tom", false)), "admin-1", CancellationToken.None)).Data!;
        Assert.Equal(2, result.Confirmed);
        Assert.Empty(result.Failed);

        var link = await _db.Queryable<EmployeeMyobLink>().FirstAsync(item => item.UserGuid == "u-mei" && item.UnboundAt == null);
        Assert.Equal("c-mei", link.MyobEmployeeUid);
        Assert.Equal(MyobMatchMethods.Suggested, link.MatchMethod);
        Assert.Equal(100, link.MatchScore);
        Assert.Equal("admin-1", link.BoundByUserGuid);
        Assert.Equal("Mei Lin Zhang", link.MyobName);

        var snapshot = (await Binding().GetSnapshotAsync(Inv, false, CancellationToken.None)).Data!;
        Assert.Equal(3, snapshot.Counts.Bound);
        Assert.Equal(0, snapshot.Counts.Suggested);
    }

    [Fact]
    public async Task 确认_非高分的建议不能按建议确认_人工选卡可以()
    {
        await SeedScenarioAsync();
        var auto = (await Binding().ConfirmAsync(Confirm(("u-yuki", "c-yuki", false)), "admin-1", CancellationToken.None)).Data!;
        Assert.Equal(0, auto.Confirmed);
        Assert.Equal(MyobBindErrorCodes.NotHighScore, Assert.Single(auto.Failed).Code);

        var manual = (await Binding().ConfirmAsync(Confirm(("u-yuki", "c-yuki", true)), "admin-1", CancellationToken.None)).Data!;
        Assert.Equal(1, manual.Confirmed);
        Assert.Equal(MyobMatchMethods.Manual, (await _db.Queryable<EmployeeMyobLink>().FirstAsync(item => item.UserGuid == "u-yuki")).MatchMethod);
    }

    [Fact]
    public async Task 确认_部分失败逐条返回原因_已绑定的卡_已绑定的员工_不存在的卡和员工()
    {
        await SeedScenarioAsync();
        var result = (await Binding().ConfirmAsync(
            Confirm(
                ("u-chloe", "c-sarah", true),      // 卡已被 Sarah 绑定
                ("u-sarah", "c-mei", true),        // Sarah 已有绑定
                ("u-mei", "c-nope", true),         // MYOB 里没有这张卡
                ("u-ghost", "c-mei", true),        // 员工不存在
                ("u-mei", "c-mei", false)),        // 唯一成功的一条
            "admin-1", CancellationToken.None)).Data!;

        Assert.Equal(1, result.Confirmed);
        Assert.Equal(
            [MyobBindErrorCodes.CardAlreadyBound, MyobBindErrorCodes.UserAlreadyBound, MyobBindErrorCodes.CardNotFound, MyobBindErrorCodes.UserNotFound],
            result.Failed.Select(item => item.Code).ToArray());
        Assert.Contains("Sarah Kim", result.Failed[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 解绑_软解绑保留历史_同一张卡可以重新绑给别人()
    {
        await SeedScenarioAsync();
        var unbound = await Binding().UnbindAsync("link-sarah", "admin-2", CancellationToken.None);
        Assert.True(unbound.Success);
        var history = await _db.Queryable<EmployeeMyobLink>().FirstAsync(item => item.LinkGuid == "link-sarah");
        Assert.NotNull(history.UnboundAt);
        Assert.Equal("admin-2", history.UnboundByUserGuid);

        // 再解一次：已经没有生效的绑定。
        Assert.Equal(MyobBindErrorCodes.LinkNotFound, (await Binding().UnbindAsync("link-sarah", "admin-2", CancellationToken.None)).ErrorCode);

        var rebind = (await Binding().ConfirmAsync(Confirm(("u-chloe", "c-sarah", true)), "admin-2", CancellationToken.None)).Data!;
        Assert.Equal(1, rebind.Confirmed);
        Assert.Equal(2, await _db.Queryable<EmployeeMyobLink>().CountAsync(item => item.MyobEmployeeUid == "c-sarah")); // 历史 + 新绑定
    }

    [Fact]
    public async Task 忽略_忽略后不再出现在MYOB有我方没有_取消忽略后恢复_重复忽略幂等()
    {
        await SeedScenarioAsync();
        var request = new MyobIgnoreEmployeeRequest { ConnectionGuid = Inv, MyobEmployeeUid = "c-daniel", Ignored = true };
        Assert.True((await Binding().IgnoreAsync(request, "admin-1", CancellationToken.None)).Success);
        Assert.True((await Binding().IgnoreAsync(request, "admin-1", CancellationToken.None)).Success);
        Assert.Equal(1, await _db.Queryable<MyobEmployeeIgnore>().CountAsync());

        var after = (await Binding().GetSnapshotAsync(Inv, false, CancellationToken.None)).Data!;
        Assert.Equal(["c-kevin"], after.OnlyInMyob.Select(item => item.Card.Uid).ToArray());
        Assert.Equal(1, after.Counts.OnlyInMyob);

        request.Ignored = false;
        await Binding().IgnoreAsync(request, "admin-1", CancellationToken.None);
        Assert.Equal(0, await _db.Queryable<MyobEmployeeIgnore>().CountAsync());
        var restored = (await Binding().GetSnapshotAsync(Inv, false, CancellationToken.None)).Data!;
        Assert.Equal(2, restored.Counts.OnlyInMyob);
    }

    // ───────────────────────── 候选 ─────────────────────────

    [Fact]
    public async Task 候选_默认只给有正向证据的_按得分降序_被别人绑定的标出归属()
    {
        await SeedScenarioAsync();
        var result = (await Binding().GetCandidatesAsync(Inv, "u-chloe", null, CancellationToken.None)).Data!;
        Assert.Equal("Chloe Wang", result.UserName);
        Assert.Equal("c-sarah", Assert.Single(result.Candidates).Card.Uid); // 只有共用邮箱这一张有证据
        Assert.Equal("Sarah Kim", result.Candidates[0].BoundToUserName);
        Assert.Equal("u-sarah", result.Candidates[0].BoundToUserGuid);
    }

    [Fact]
    public async Task 候选_关键字搜索_姓名邮箱手机末几位_并带出我方资料()
    {
        await SeedScenarioAsync();
        var byName = (await Binding().GetCandidatesAsync(Inv, "u-liam", "dan moore", CancellationToken.None)).Data!;
        Assert.Equal(["c-daniel"], byName.Candidates.Select(item => item.Card.Uid).ToArray());

        var byEmail = (await Binding().GetCandidatesAsync(Inv, "u-liam", "kevin", CancellationToken.None)).Data!;
        Assert.Empty(byEmail.Candidates.Where(item => item.Card.Uid == "c-daniel"));

        var byPhone = (await Binding().GetCandidatesAsync(Inv, "u-liam", "5678", CancellationToken.None)).Data!;
        Assert.Equal(["c-mei"], byPhone.Candidates.Select(item => item.Card.Uid).ToArray());

        Assert.Equal("•••• 9888", byName.PhoneMasked);
        Assert.Equal(["1014 Kawana"], byName.StoreLabels);
        Assert.Equal("PartTime", byName.EmployeeType);

        var unknown = await Binding().GetCandidatesAsync(Inv, "u-ghost", null, CancellationToken.None);
        Assert.Equal(MyobBindErrorCodes.UserNotFound, unknown.ErrorCode);
    }

    // ───────────────────────── 发薪公司 ─────────────────────────

    private async Task SeedJasonAsync()
    {
        await SeedBaseAsync();
        // Jason 在 Orion（Australian）与 Kawana（Investment）两个分店上班。
        await SeedEmployeeAsync("u-jason", "Jason", "Nguyen", "jason.n@example.com", null, null, EmployeeType.PartTime, "1013", "1014");
    }

    [Fact]
    public async Task 发薪公司_默认按分店_列表标出跨公司()
    {
        await SeedJasonAsync();
        var list = (await Employers().ListAsync(null, null, false, CancellationToken.None)).Data!;
        Assert.Equal(2, list.Rows.Count);
        var orion = list.Rows.Single(row => row.StoreCode == "1013");
        Assert.Equal(Aus, orion.EffectiveConnectionGuid);
        Assert.False(orion.IsOverride);
        Assert.Equal("Hot Bargain Australian Pty Ltd", orion.EffectiveConnectionName);
        Assert.All(list.Rows, row => Assert.True(row.IsCrossCompany)); // 两个分店分属两家公司
        Assert.Equal(2, list.Companies.Count);

        Assert.Empty((await Employers().ListAsync("nobody", null, false, CancellationToken.None)).Data!.Rows);
        Assert.Single((await Employers().ListAsync(null, "1014", false, CancellationToken.None)).Data!.Rows);
    }

    [Fact]
    public async Task 发薪公司_详情给出可选生效日_当前期起4个周一_以及绑定状态()
    {
        await SeedJasonAsync();
        await _db.Insertable(new EmployeeMyobLink
        {
            LinkGuid = "l1", UserGuid = "u-jason", ConnectionGuid = Aus, MyobEmployeeUid = "c", MatchMethod = "Manual", BoundAt = Now,
        }).ExecuteCommandAsync();

        var detail = (await Employers().GetDetailAsync("u-jason", CancellationToken.None)).Data!;
        Assert.Equal(["2026-10-26", "2026-11-02", "2026-11-09", "2026-11-16"], detail.AllowedEffectiveFrom.ToArray());
        Assert.True(detail.BindingStates.Single(item => item.ConnectionGuid == Aus).Bound);
        Assert.False(detail.BindingStates.Single(item => item.ConnectionGuid == Inv).Bound);
    }

    [Fact]
    public async Task 发薪公司_双周周期时生效日期按双周锚点推算()
    {
        await SeedJasonAsync();
        await _db.Insertable(new AttendanceSettings { DefaultPeriodWeeks = 2, FortnightAnchorDate = new DateTime(2026, 10, 19) }).ExecuteCommandAsync();
        var detail = (await Employers().GetDetailAsync("u-jason", CancellationToken.None)).Data!;
        // 锚点周一 10-19，每 14 天一期：10-28 所在期从 10-19 开始。
        Assert.Equal(["2026-10-19", "2026-11-02", "2026-11-16", "2026-11-30"], detail.AllowedEffectiveFrom.ToArray());
    }

    [Fact]
    public async Task 发薪公司_保存覆盖_立即生效_写历史_返回最新详情()
    {
        await SeedJasonAsync();
        var result = await Employers().SaveAsync("u-jason", new MyobSavePayrollEmployersRequest
        {
            EffectiveFrom = "2026-10-26",
            Items = [new() { StoreCode = "1013", ConnectionGuid = Inv }],
        }, "admin-1", CancellationToken.None);

        Assert.True(result.Success);
        var orion = result.Data!.Rows.Single(row => row.StoreCode == "1013");
        Assert.Equal(Inv, orion.EffectiveConnectionGuid);
        Assert.True(orion.IsOverride);
        Assert.Equal("2026-10-26", orion.EffectiveFrom);
        var history = Assert.Single(result.Data.History);
        Assert.Equal(Inv, history.ConnectionGuid);
        Assert.Equal("1013", history.StoreCode);

        // 此后两个分店都由 Investment 发薪：不再算「跨公司」之外的差异，但覆盖那一行仍是例外。
        var list = (await Employers().ListAsync(null, null, false, CancellationToken.None)).Data!;
        Assert.All(list.Rows, row => Assert.Equal(Inv, row.EffectiveConnectionGuid));
    }

    [Fact]
    public async Task 发薪公司_选默认公司等于改回按分店_无变化不写行_同日再改只更新那一行()
    {
        await SeedJasonAsync();
        // 与当前一致（Orion 本来就是 Australian）：不写任何行。
        await Employers().SaveAsync("u-jason", new MyobSavePayrollEmployersRequest
        {
            EffectiveFrom = "2026-10-26", Items = [new() { StoreCode = "1013", ConnectionGuid = Aus }],
        }, "admin-1", CancellationToken.None);
        Assert.Equal(0, await _db.Queryable<EmployeePayrollEmployer>().CountAsync());

        var save = (string? company, string from) => Employers().SaveAsync("u-jason", new MyobSavePayrollEmployersRequest
        {
            EffectiveFrom = from, Items = [new() { StoreCode = "1013", ConnectionGuid = company }],
        }, "admin-1", CancellationToken.None);

        await save(Inv, "2026-11-02");            // 下一期起改由 Investment 发
        await save(Inv, "2026-11-02");            // 重复保存：不新增
        Assert.Equal(1, await _db.Queryable<EmployeePayrollEmployer>().CountAsync());

        await save(Aus, "2026-11-02");            // 同一天改回默认公司：改那一行为 null（按分店）
        var row = await _db.Queryable<EmployeePayrollEmployer>().FirstAsync();
        Assert.Null(row.ConnectionGuid);
        Assert.Equal(1, await _db.Queryable<EmployeePayrollEmployer>().CountAsync());
    }

    [Theory]
    [InlineData("2026-10-27")]   // 不是周期起始日（周二）
    [InlineData("2026-10-19")]   // 早于当前期
    [InlineData("2026-12-14")]   // 超出可选范围
    [InlineData("not-a-date")]
    public async Task 发薪公司_生效日期必须是当前或之后的周期起始日(string effectiveFrom)
    {
        await SeedJasonAsync();
        var result = await Employers().SaveAsync("u-jason", new MyobSavePayrollEmployersRequest
        {
            EffectiveFrom = effectiveFrom, Items = [new() { StoreCode = "1013", ConnectionGuid = Inv }],
        }, "admin-1", CancellationToken.None);
        Assert.Equal(MyobPayrollErrorCodes.InvalidEffectiveFrom, result.ErrorCode);
        Assert.Equal(0, await _db.Queryable<EmployeePayrollEmployer>().CountAsync());
    }

    [Fact]
    public async Task 发薪公司_校验失败整次不生效_分店不属于员工_公司未启用_无权限的分店()
    {
        await SeedJasonAsync();
        Task<BlazorApp.Shared.DTOs.ApiResponse<MyobPayrollEmployerDetailDto>> Save(params MyobSavePayrollEmployerItem[] items) =>
            Employers().SaveAsync("u-jason", new MyobSavePayrollEmployersRequest { EffectiveFrom = "2026-10-26", Items = items.ToList() }, "a", CancellationToken.None);

        Assert.Equal(MyobPayrollErrorCodes.StoreNotWorked,
            (await Save(new MyobSavePayrollEmployerItem { StoreCode = "1013", ConnectionGuid = Inv }, new MyobSavePayrollEmployerItem { StoreCode = "1042", ConnectionGuid = Inv })).ErrorCode);
        Assert.Equal(MyobPayrollErrorCodes.ConnectionNotActive,
            (await Save(new MyobSavePayrollEmployerItem { StoreCode = "1013", ConnectionGuid = "conn-ghost" })).ErrorCode);

        _scope.Scope = new CurrentUserManageableStoreScope { IsAllowed = true, IsAdmin = false, StoreCodes = ["1014"] };
        Assert.Equal(MyobPayrollErrorCodes.Forbidden, (await Save(new MyobSavePayrollEmployerItem { StoreCode = "1013", ConnectionGuid = Inv })).ErrorCode);
        Assert.Equal(0, await _db.Queryable<EmployeePayrollEmployer>().CountAsync());
    }

    [Fact]
    public async Task 发薪公司_店长只能看到并改自己可管理分店的行()
    {
        await SeedJasonAsync();
        _scope.Scope = new CurrentUserManageableStoreScope { IsAllowed = true, IsAdmin = false, StoreCodes = ["1014"] };

        var list = (await Employers().ListAsync(null, null, false, CancellationToken.None)).Data!;
        Assert.Equal(["1014"], list.Rows.Select(row => row.StoreCode).ToArray());
        var detail = (await Employers().GetDetailAsync("u-jason", CancellationToken.None)).Data!;
        Assert.Equal(["1014"], detail.Rows.Select(row => row.StoreCode).ToArray());

        _scope.Scope = new CurrentUserManageableStoreScope { IsAllowed = true, IsAdmin = false, StoreCodes = ["1042"] };
        Assert.Equal(MyobPayrollErrorCodes.Forbidden, (await Employers().GetDetailAsync("u-jason", CancellationToken.None)).ErrorCode);

        _scope.Scope = new CurrentUserManageableStoreScope { IsAllowed = false, Message = "无权限" };
        Assert.Equal(MyobPayrollErrorCodes.Forbidden, (await Employers().ListAsync(null, null, false, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task 发薪公司_只看跨公司筛选()
    {
        await SeedJasonAsync();
        await SeedEmployeeAsync("u-solo", "Solo", "Worker", null, null, null, EmployeeType.FullTime, "1014");
        var all = (await Employers().ListAsync(null, null, false, CancellationToken.None)).Data!;
        Assert.Equal(3, all.Rows.Count);
        var cross = (await Employers().ListAsync(null, null, true, CancellationToken.None)).Data!;
        Assert.Equal(["u-jason", "u-jason"], cross.Rows.Select(row => row.UserGuid).ToArray());
    }

    // ───────────────────────── 控制器与权限 ─────────────────────────

    [Fact]
    public void 控制器权限_绑定接口要求MyobEmployeeBind_发薪公司要求PayrollEdit()
    {
        AssertPolicy(typeof(MyobEmployeeBindingController), Permissions.Myob.Employee.Bind);
        AssertPolicy(typeof(MyobPayrollEmployerController), Permissions.Attendance.Payroll.Edit);
        Assert.Equal("Myob.Employee.Bind", Permissions.Myob.Employee.Bind);

        Assert.Equal("api/react/v1/myob/employee-bindings",
            typeof(MyobEmployeeBindingController).GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal("api/react/v1/myob/payroll-employers",
            typeof(MyobPayrollEmployerController).GetCustomAttribute<RouteAttribute>()!.Template);
        // 新增的接口不能有匿名放行。
        Assert.Empty(typeof(MyobEmployeeBindingController).GetMethods().Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null));
        Assert.Empty(typeof(MyobPayrollEmployerController).GetMethods().Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null));
    }

    private static void AssertPolicy(Type controller, string policy) =>
        Assert.Equal(policy, controller.GetCustomAttribute<AuthorizeAttribute>()!.Policy);

    [Fact]
    public void 权限码已登记_种子_目录_导航访问集合()
    {
        var seed = Assert.Single(PermissionSeedData.AllPermissions, item => item.Code == Permissions.Myob.Employee.Bind);
        Assert.Equal("系统管理", seed.Category);
        Assert.True(PermissionSurfaceCatalog.IsRegistered(Permissions.Myob.Employee.Bind));
        Assert.Equal(PermissionSurface.Web, PermissionSurfaceCatalog.Resolve(Permissions.Myob.Employee.Bind));
    }

    [Theory]
    [InlineData("Myob.Connection.Manage", true)]
    [InlineData("Myob.Employee.Bind", true)]
    [InlineData("Attendance.Payroll.Edit", true)]
    [InlineData("Orders.View", false)]
    public async Task 公司选择接口_三个权限任一放行_其余拒绝_只返回Active连接(string granted, bool allowed)
    {
        await SeedBaseAsync();
        await _db.Insertable(new MyobConnection
        {
            ConnectionGuid = "conn-old", BusinessId = "biz-old", BusinessName = "Needs reauth Pty Ltd",
            Status = MyobConnectionStatuses.NeedsReauth, AuthorizedAt = Now, CreatedAt = Now,
        }).ExecuteCommandAsync();
        await _db.Updateable<MyobConnection>().SetColumns(item => item.Abn == "51824753556")
            .Where(item => item.ConnectionGuid == Inv).ExecuteCommandAsync();

        var controller = new MyobCompanyOptionsController(Employers(), new FakeAuthorization(granted))
        {
            ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() },
        };
        var result = await controller.List();

        if (!allowed)
        {
            Assert.IsType<ForbidResult>(result);
            return;
        }

        var body = Assert.IsType<BlazorApp.Shared.DTOs.ApiResponse<List<MyobPayrollCompanyOptionDto>>>(
            Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal([Aus, Inv], body.Data!.Select(item => item.ConnectionGuid).OrderBy(item => item).ToArray());
        Assert.Equal("51824753556", body.Data!.Single(item => item.ConnectionGuid == Inv).Abn);
        Assert.DoesNotContain(body.Data!, item => item.ConnectionGuid == "conn-old");
        Assert.Null(typeof(MyobCompanyOptionsController).GetCustomAttribute<AuthorizeAttribute>()!.Policy); // 类上只要求已登录，权限在动作里按「任一」判断
    }

    // ───────────────────────── 迁移 SQL ─────────────────────────

    [Fact]
    public void 迁移_错误号自成一段_建表幂等_过滤唯一索引_权限码入库()
    {
        foreach (var code in new[] { "54340", "54341", "54342", "54343", "54344", "54345" })
        {
            Assert.Contains($"THROW {code}", MyobEmployeeBindingSchema.VerifySql, StringComparison.Ordinal);
            Assert.DoesNotContain($"THROW {code}", MyobEmployeeBindingSchema.ApplySql, StringComparison.Ordinal);
        }

        foreach (var table in new[] { "EmployeeMyobLink", "EmployeePayrollEmployer", "MyobEmployeeIgnore" })
        {
            Assert.Contains($"IF OBJECT_ID(N'dbo.{table}', N'U') IS NULL", MyobEmployeeBindingSchema.ApplySql, StringComparison.Ordinal);
        }

        // 软解绑要靠过滤唯一索引：缺 WHERE UnboundAt IS NULL，解绑后就无法重新绑定。
        Assert.Equal(2, CountOccurrences(MyobEmployeeBindingSchema.ApplySql, "WHERE [UnboundAt] IS NULL"));
        Assert.Contains("[has_filter] = 1", MyobEmployeeBindingSchema.VerifySql, StringComparison.Ordinal);
        Assert.Contains("Myob.Employee.Bind", MyobEmployeeBindingSchema.ApplySql, StringComparison.Ordinal);
        Assert.Contains("WHERE NOT EXISTS", MyobEmployeeBindingSchema.ApplySql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP ", MyobEmployeeBindingSchema.ApplySql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE ", MyobEmployeeBindingSchema.ApplySql, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    // ───────────────────────── 测试替身 ─────────────────────────

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record Recorded(string Url, Dictionary<string, string> Headers);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Recorded> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(item => item.Key, item => string.Join(",", item.Value), StringComparer.OrdinalIgnoreCase);
            Requests.Add(new Recorded(request.RequestUri!.AbsoluteUri, headers));
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FakeDirectory : IMyobEmployeeDirectory
    {
        private readonly Dictionary<string, MyobEmployeeDirectoryData> _data = new(StringComparer.OrdinalIgnoreCase);

        public MyobApiException? Error { get; set; }

        public void Set(string connectionGuid, MyobEmployeeDirectoryData data) => _data[connectionGuid] = data;

        public Task<MyobEmployeeDirectoryData> GetAsync(string connectionGuid, bool refresh, CancellationToken cancellationToken) =>
            Error is not null
                ? Task.FromException<MyobEmployeeDirectoryData>(Error)
                : Task.FromResult(_data[connectionGuid]);
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

    private sealed class FakeAuthorization(string grantedPolicy) : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(System.Security.Claims.ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Failed());

        public Task<AuthorizationResult> AuthorizeAsync(System.Security.Claims.ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(policyName == grantedPolicy ? AuthorizationResult.Success() : AuthorizationResult.Failed());
    }

    private sealed class FakeTime(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
