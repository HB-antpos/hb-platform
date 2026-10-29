using System.Net;
using System.Reflection;
using System.Text;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 从 MYOB 同步资料到我方：差异规则（纯函数）、字段统计 / 预览 / 写入 / 同步记录 / 撤销（SQLite 临时库 + 假名册 + 假员工资料服务）。
/// 注意：MYOB 的住址、性别、退休金、付款详情字段按官方文档实现，真实响应字段名须用已授权公司文件在线上只读核对。
/// </summary>
public sealed class MyobEmployeeSyncTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 29, 3, 0, 0, DateTimeKind.Utc);
    private const string Inv = "conn-inv";
    private const string Actor = "u-admin";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hb-myob-sync-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;
    private readonly FakeDirectory _directory = new();
    private readonly FakePayments _payments = new();
    private readonly FakeProfiles _profiles;
    private readonly FakeTime _time = new(Now);
    private bool _canEdit = true;

    public MyobEmployeeSyncTests()
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
            typeof(MyobEmployeeIgnore), typeof(MyobEmployeeSyncBatch), typeof(Store), typeof(User), typeof(UserStore),
            typeof(EmployeeProfile), typeof(EmployeeMinorCompliance), typeof(EmployeeProfileSensitiveChangeRequest),
            typeof(AttendanceSettings));
        _profiles = new FakeProfiles(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private MyobEmployeeSyncService Service() =>
        new(_db, _directory, _payments, _profiles, _ => Task.FromResult(_canEdit), _time);

    // ───────────────────────── 规则（纯函数） ─────────────────────────

    private static SyncOurValues Our(
        string? full = null, string? email = null, string? phone = null, DateTime? birthday = null,
        EmployeeGender? gender = null, string? address = null, EmployeeType? type = null,
        string? bsb = null, string? account = null, string? superName = null, string? superAccount = null) =>
        new("u", full ?? "Someone", full, null, null, null, email, phone, birthday, gender, address, type,
            bsb, account, superName, superAccount, 0, false);

    private static SyncMyobValues Myob(
        string? first = null, string? last = null, string[]? emails = null, string[]? phones = null,
        DateTime? birthday = null, string? gender = null, MyobAddressRecord? address = null,
        string? status = null, bool entitlement = false, string? bsb = null, string? account = null,
        string? fund = null, string? member = null, bool individual = true) =>
        new(individual, first, last, emails ?? [], phones ?? [], birthday, gender, address, status, entitlement,
            bsb, account, fund, member);

    private static SyncDiff? Field(string field, SyncOurValues our, SyncMyobValues myob) =>
        MyobEmployeeSyncRules.ComputeField(field, our, myob);

    [Fact]
    public void 规则_姓名只差大小写和空格_无差异()
    {
        Assert.Null(Field(MyobSyncFields.Name, Our(full: "Aidan  Forde"), Myob("AIDAN", "FORDE")));
    }

    [Fact]
    public void 规则_我方姓名带中间名_算覆盖且MYOB全大写会改成首字母大写()
    {
        var diff = Field(MyobSyncFields.Name, Our(full: "Kathryn L Townson"), Myob("KATHRYN", "TOWNSON"));
        Assert.NotNull(diff);
        Assert.Equal(MyobSyncChangeKinds.Overwrite, diff!.Kind);
        Assert.Equal("Kathryn Townson", diff.MyobDisplay);
        Assert.Equal("Kathryn", diff.After["given"]);
        Assert.Equal("Townson", diff.After["family"]);
        Assert.Null(diff.After["middle"]);
    }

    [Fact]
    public void 规则_我方姓名为空_算补空_公司型卡片不同步姓名()
    {
        var fill = Field(MyobSyncFields.Name, Our(full: ""), Myob("Mia", "Chen"));
        Assert.Equal(MyobSyncChangeKinds.Fill, fill!.Kind);
        Assert.Null(Field(MyobSyncFields.Name, Our(full: ""), Myob("Hot", "Bargain", individual: false)));
    }

    [Fact]
    public void 规则_手机_国际区号与空格写法一致_无差异_座机和手机并存优先手机()
    {
        Assert.Null(Field(MyobSyncFields.Phone, Our(phone: "0412 345 678"), Myob(phones: ["+61412345678"])));
        // 我方号码和 MYOB 任一号码一致就算一致。
        Assert.Null(Field(MyobSyncFields.Phone, Our(phone: "0412345678"), Myob(phones: ["0755551234", "0412345678"])));

        var diff = Field(MyobSyncFields.Phone, Our(), Myob(phones: ["07 5555 1234", "0412345678"]));
        Assert.Equal(MyobSyncChangeKinds.Fill, diff!.Kind);
        Assert.Equal("0412 345 678", diff.MyobDisplay);
    }

    [Fact]
    public void 规则_MYOB没有值_一律不产生差异_不会拿空值清掉我方资料()
    {
        var our = Our(full: "A B", email: "a@b.com", phone: "0412345678", birthday: new DateTime(2000, 1, 1),
            gender: EmployeeGender.Male, address: "1 St", type: EmployeeType.FullTime,
            bsb: "063-010", account: "12344821", superName: "Fund", superAccount: "1");
        Assert.Empty(MyobEmployeeSyncRules.Compute(our, Myob()));
    }

    [Fact]
    public void 规则_生日_同一天无差异_我方为空补空_不同覆盖()
    {
        var day = new DateTime(2009, 7, 5);
        Assert.Null(Field(MyobSyncFields.Birthday, Our(birthday: day), Myob(birthday: day)));
        Assert.Equal(MyobSyncChangeKinds.Fill, Field(MyobSyncFields.Birthday, Our(), Myob(birthday: day))!.Kind);
        var diff = Field(MyobSyncFields.Birthday, Our(birthday: new DateTime(2009, 7, 6)), Myob(birthday: day));
        Assert.Equal(MyobSyncChangeKinds.Overwrite, diff!.Kind);
        Assert.Equal("05 Jul 2009", diff.MyobDisplay);
    }

    [Fact]
    public void 规则_性别_我方未知等同没填_MYOB的Other映射为其他()
    {
        var fill = Field(MyobSyncFields.Gender, Our(gender: EmployeeGender.Unknown), Myob(gender: "Female"));
        Assert.Equal(MyobSyncChangeKinds.Fill, fill!.Kind);
        Assert.Equal("female", fill.After["gender"]);
        Assert.Null(Field(MyobSyncFields.Gender, Our(gender: EmployeeGender.Female), Myob(gender: "Female")));
        Assert.Null(Field(MyobSyncFields.Gender, Our(), Myob(gender: "Prefer not to say")));
    }

    [Fact]
    public void 规则_住址_拼成一行_换行变逗号_空地址不比()
    {
        var address = new MyobAddressRecord("14 Bay St\r\nUnit 2", "Robina", "QLD", "4226", "Australia");
        var diff = Field(MyobSyncFields.Address, Our(), Myob(address: address));
        Assert.Equal("14 Bay St, Unit 2, Robina QLD 4226", diff!.MyobDisplay);
        Assert.Null(Field(MyobSyncFields.Address, Our(address: "14 bay st unit 2 robina qld 4226"), Myob(address: address)));
        Assert.Null(Field(MyobSyncFields.Address, Our(), Myob(address: new MyobAddressRecord(null, null, "QLD", null, "Australia"))));
    }

    [Fact]
    public void 规则_用工类型_Casual对应临时工_Other不同步_临时工遇到年假累计给提示()
    {
        Assert.Null(Field(MyobSyncFields.EmploymentType, Our(type: EmployeeType.Temporary), Myob(status: "Casual")));
        Assert.Null(Field(MyobSyncFields.EmploymentType, Our(type: EmployeeType.Temporary), Myob(status: "Other")));

        var diff = Field(MyobSyncFields.EmploymentType, Our(type: EmployeeType.Temporary), Myob(status: "PartTime", entitlement: true));
        Assert.Equal(MyobSyncChangeKinds.Overwrite, diff!.Kind);
        Assert.Equal("兼职", diff.MyobDisplay);
        Assert.Equal("partTime", diff.After["employmentType"]);
        Assert.Contains("年假", diff.Note);
    }

    [Fact]
    public void 规则_银行_只比数字_展示只留账号末4位_我方为空补空()
    {
        Assert.Null(Field(MyobSyncFields.Bank, Our(bsb: "063-010", account: "1234 4821"), Myob(bsb: "063010", account: "12344821")));

        var fill = Field(MyobSyncFields.Bank, Our(), Myob(bsb: "063010", account: "12344821"));
        Assert.Equal(MyobSyncChangeKinds.Fill, fill!.Kind);
        Assert.Equal("063-010 · •••• 4821", fill.MyobDisplay);
        Assert.Null(fill.OurDisplay);

        var overwrite = Field(MyobSyncFields.Bank, Our(bsb: "063-010", account: "99999999"), Myob(bsb: "063010", account: "12344821"));
        Assert.Equal(MyobSyncChangeKinds.Overwrite, overwrite!.Kind);
        Assert.Equal("063-010 · •••• 9999", overwrite.OurDisplay);
    }

    [Fact]
    public void 规则_退休金_基金名忽略大小写_会员号只留末4位()
    {
        Assert.Null(Field(MyobSyncFields.Superannuation, Our(superName: "australiansuper", superAccount: "123-489034"),
            Myob(fund: "AustralianSuper", member: "123489034")));
        var diff = Field(MyobSyncFields.Superannuation, Our(), Myob(fund: "AustralianSuper", member: "123489034"));
        Assert.Equal("AustralianSuper · •••• 9034", diff!.MyobDisplay);
    }

    [Fact]
    public void 规则_哈希_稳定且随值变化()
    {
        var one = MyobEmployeeSyncRules.Hash("phone", "myob", "0412345678");
        Assert.Equal(one, MyobEmployeeSyncRules.Hash("phone", "myob", "0412345678"));
        Assert.NotEqual(one, MyobEmployeeSyncRules.Hash("phone", "myob", "0412345679"));
        Assert.NotEqual(one, MyobEmployeeSyncRules.Hash("phone", "our", "0412345678"));
    }

    // ───────────────────────── 服务：种子 ─────────────────────────

    private async Task SeedAsync()
    {
        await _db.Insertable(new MyobConnection
        {
            ConnectionGuid = Inv, BusinessId = "biz-inv", BusinessName = "Hot Bargain Robinson Road Pty Ltd",
            Status = MyobConnectionStatuses.Active, AuthorizedAt = Now, CreatedAt = Now,
        }).ExecuteCommandAsync();
        await _db.Insertable(new Store { StoreGUID = "g-1002", StoreCode = "1002", StoreName = "Robinson Road", IsActive = true }).ExecuteCommandAsync();
        await _db.Insertable(new MyobStoreMapping { StoreCode = "1002", ConnectionGuid = Inv, MappedAt = Now }).ExecuteCommandAsync();

        // Aidan：资料几乎全空；姓名带中间名。
        await SeedUserAsync("u-aidan", "Aidan", "Ryan", "Forde", "aidan@example.com", EmployeeType.Temporary);
        // Ella：手机和 MYOB 不一样；没有住址。
        await SeedUserAsync("u-ella", "Ella", null, "Bradley", "ella@example.com", EmployeeType.Temporary, phone: "0433 120 774");
        // Xanthe：全部一致 → 无差异。
        await SeedUserAsync("u-xanthe", "Xanthe", null, "Pereira", "xanthe@example.com", EmployeeType.Temporary, phone: "0421 998 306");
        // Ghost：已绑定，但 MYOB 里找不到卡。
        await SeedUserAsync("u-ghost", "Ghost", null, "Person", "ghost@example.com", EmployeeType.PartTime);

        foreach (var (user, uid) in new[] { ("u-aidan", "c-aidan"), ("u-ella", "c-ella"), ("u-xanthe", "c-xanthe"), ("u-ghost", "c-ghost") })
        {
            await _db.Insertable(new EmployeeMyobLink
            {
                LinkGuid = $"link-{user}", UserGuid = user, ConnectionGuid = Inv, MyobEmployeeUid = uid,
                MatchMethod = MyobMatchMethods.Manual, BoundAt = Now.AddDays(-1),
            }).ExecuteCommandAsync();
        }

        await SetDirectoryAsync(
        [
            new MyobEmployeeRecord("c-aidan", null, "AIDAN", "FORDE", null, true, true, ["aidan@example.com"], ["+61412345881"],
                [new MyobAddressRecord("1 Test St", "Robina", "QLD", "4226", "Australia")]),
            new MyobEmployeeRecord("c-ella", null, "ELLA", "BRADLEY", null, true, true, ["ella@example.com"], ["0433 120 477"],
                [new MyobAddressRecord("14 Bay St", "Robina", "QLD", "4226", "Australia")]),
            new MyobEmployeeRecord("c-xanthe", null, "Xanthe", "Pereira", null, true, true, ["xanthe@example.com"], ["0421998306"]),
        ],
        [
            new MyobEmployeePayrollRecord("c-aidan", new DateTime(2009, 7, 5), "Casual", [], "Male", "AustralianSuper", "123489034"),
            new MyobEmployeePayrollRecord("c-ella", null, "Casual", [], "Female"),
            new MyobEmployeePayrollRecord("c-xanthe", null, "Casual", []),
        ]);
        _payments.Accounts["c-aidan"] = new MyobBankAccountRecord("063010", "12344821");
    }

    private async Task SeedUserAsync(
        string guid, string given, string? middle, string family, string email, EmployeeType? type, string? phone = null)
    {
        await _db.Insertable(new User
        {
            UserGUID = guid, Username = guid, Email = email, IsActive = true, CreatedAt = Now.AddDays(-30),
            GivenName = given, MiddleName = middle, FamilyName = family,
            FullName = string.Join(' ', new[] { given, middle, family }.Where(part => !string.IsNullOrEmpty(part))),
        }).ExecuteCommandAsync();
        await _db.Insertable(new EmployeeProfile { UserGUID = guid, Phone = phone, EmployeeType = type }).ExecuteCommandAsync();
        await _db.Insertable(new UserStore { UserStoreGUID = Guid.NewGuid().ToString(), UserGUID = guid, StoreGUID = "g-1002" }).ExecuteCommandAsync();
    }

    private async Task SetDirectoryAsync(IReadOnlyList<MyobEmployeeRecord> cards, IReadOnlyList<MyobEmployeePayrollRecord> payroll)
    {
        var connection = await _db.Queryable<MyobConnection>().FirstAsync(item => item.ConnectionGuid == Inv);
        _directory.Data = MyobEmployeeDirectory.Build(connection, cards, payroll, Now);
    }

    private static readonly string[] AllFields =
        [.. MyobSyncFields.All];

    private async Task<MyobSyncPreviewDto> PreviewAsync(
        string[]? fields = null, string mode = MyobSyncModes.FillEmpty, params string[] users)
    {
        var response = await Service().PreviewAsync(
            new MyobSyncPreviewRequest
            {
                ConnectionGuid = Inv, Fields = (fields ?? AllFields).ToList(), Mode = mode,
                UserGuids = users.Length == 0 ? null : users.ToList(),
            },
            Actor, CancellationToken.None);
        Assert.True(response.Success, response.Message);
        return response.Data!;
    }

    /// <summary>按预览里「默认勾选」的项构造 apply 请求。</summary>
    private static MyobSyncApplyRequest ApplyFrom(MyobSyncPreviewDto preview, bool confirm = false, Func<MyobSyncChangeDto, bool>? pick = null) => new()
    {
        ConnectionGuid = Inv,
        ConfirmSupersedePendingSensitiveRequests = confirm,
        Items = preview.Employees
            .Select(employee => new MyobSyncApplyEmployeeItem
            {
                UserGuid = employee.UserGuid,
                ExpectedSensitiveRevision = employee.SensitiveRevision,
                Changes = employee.Changes
                    .Where(change => pick?.Invoke(change) ?? change.DefaultChecked)
                    .Select(change => new MyobSyncApplyChangeItem { Field = change.Field, OurHash = change.OurHash, MyobHash = change.MyobHash })
                    .ToList(),
            })
            .Where(item => item.Changes.Count > 0)
            .ToList(),
    };

    // ───────────────────────── 服务：统计与预览 ─────────────────────────

    [Fact]
    public async Task 字段统计_按字段给出MYOB有值_补空_不同_并跳过找不到卡的人()
    {
        await SeedAsync();
        var response = await Service().GetFieldStatsAsync(new MyobSyncScopeRequest { ConnectionGuid = Inv }, Actor, CancellationToken.None);
        Assert.True(response.Success);
        var stats = response.Data!;

        Assert.Equal(4, stats.BoundCount);
        Assert.Equal(1, stats.MissingCardCount);
        Assert.True(stats.CanWrite);
        Assert.Equal(MyobSyncFields.All, stats.Fields.Select(item => item.Field).ToArray());

        var phone = stats.Fields.Single(item => item.Field == MyobSyncFields.Phone);
        Assert.Equal(3, phone.MyobHasValue); // Aidan / Ella / Xanthe
        Assert.Equal(1, phone.Fillable);     // Aidan 我方为空
        Assert.Equal(1, phone.Differs);      // Ella 号码不同

        var birthday = stats.Fields.Single(item => item.Field == MyobSyncFields.Birthday);
        Assert.True(birthday.Sensitive);
        Assert.False(birthday.DefaultSelected);
        Assert.Equal(1, birthday.Fillable);

        var bank = stats.Fields.Single(item => item.Field == MyobSyncFields.Bank);
        Assert.Equal(1, bank.Fillable);
        Assert.Empty(stats.UnavailableFields);
    }

    [Fact]
    public async Task 字段统计_付款详情读不到_银行字段标为不可用_其它字段照常()
    {
        await SeedAsync();
        _payments.Error = new MyobApiException(MyobErrorCodes.ApiCallFailed, "MYOB Contact/EmployeePaymentDetails 接口返回 403");
        var stats = (await Service().GetFieldStatsAsync(new MyobSyncScopeRequest { ConnectionGuid = Inv }, Actor, CancellationToken.None)).Data!;

        Assert.Equal([MyobSyncFields.Bank], stats.UnavailableFields);
        Assert.Equal(0, stats.Fields.Single(item => item.Field == MyobSyncFields.Bank).MyobHasValue);
        Assert.Equal(3, stats.Fields.Single(item => item.Field == MyobSyncFields.Phone).MyobHasValue);
    }

    [Fact]
    public async Task 预览_只补空模式_补空项默认勾上_覆盖项默认不勾_统计与跳过人数正确()
    {
        await SeedAsync();
        var preview = await PreviewAsync(fields: [MyobSyncFields.Name, MyobSyncFields.Phone, MyobSyncFields.Address, MyobSyncFields.Birthday]);

        // Aidan（姓名覆盖 + 手机补空 + 住址补空 + 生日补空）、Ella（手机覆盖 + 住址补空）；Xanthe 无差异；Ghost 卡缺失。
        Assert.Equal(["Aidan Ryan Forde", "Ella Bradley"], preview.Employees.Select(item => item.UserName).ToArray());
        Assert.Equal(2, preview.Summary.EmployeeCount);
        Assert.Equal(6, preview.Summary.ChangeCount);
        Assert.Equal(2, preview.Summary.OverwriteCount);
        Assert.Equal(1, preview.Summary.SensitiveCount);
        Assert.Equal(4, preview.Summary.DefaultCheckedCount);
        Assert.Equal(1, preview.Summary.SkippedNoDiff);
        Assert.Equal(1, preview.Summary.SkippedMissingCard);

        var aidan = preview.Employees[0];
        Assert.All(aidan.Changes.Where(item => item.Kind == MyobSyncChangeKinds.Fill), item => Assert.True(item.DefaultChecked));
        Assert.All(aidan.Changes.Where(item => item.Kind == MyobSyncChangeKinds.Overwrite), item => Assert.False(item.DefaultChecked));
        Assert.Equal(["1002 Robinson Road"], aidan.StoreLabels);
    }

    [Fact]
    public async Task 预览_以MYOB为准模式_覆盖项也默认勾上()
    {
        await SeedAsync();
        var preview = await PreviewAsync(fields: [MyobSyncFields.Phone], mode: MyobSyncModes.Overwrite);
        Assert.All(preview.Employees.SelectMany(item => item.Changes), item => Assert.True(item.DefaultChecked));
    }

    [Fact]
    public async Task 预览_没有写员工资料权限_或没选字段_都拒绝()
    {
        await SeedAsync();
        _canEdit = false;
        var denied = await Service().PreviewAsync(
            new MyobSyncPreviewRequest { ConnectionGuid = Inv, Fields = [MyobSyncFields.Phone] }, Actor, CancellationToken.None);
        Assert.Equal(MyobSyncErrorCodes.NoPermission, denied.ErrorCode);

        _canEdit = true;
        var empty = await Service().PreviewAsync(
            new MyobSyncPreviewRequest { ConnectionGuid = Inv, Fields = ["bogus"] }, Actor, CancellationToken.None);
        Assert.Equal(MyobSyncErrorCodes.NoFields, empty.ErrorCode);
    }

    [Fact]
    public async Task 预览_范围限定单人时只算这个人()
    {
        await SeedAsync();
        var preview = await PreviewAsync(users: ["u-ella"]);
        Assert.Equal(["u-ella"], preview.Employees.Select(item => item.UserGuid).ToArray());
        Assert.Equal(0, preview.Summary.SkippedMissingCard);
    }

    // ───────────────────────── 服务：写入 ─────────────────────────

    [Fact]
    public async Task 写入_资料类字段整份保存_没选的字段原样带回_并留下批次快照()
    {
        await SeedAsync();
        var preview = await PreviewAsync(fields: [MyobSyncFields.Address, MyobSyncFields.Phone], users: ["u-ella"]);
        // 只勾住址（补空），手机（覆盖）默认不勾。
        var request = ApplyFrom(preview);
        Assert.Single(request.Items.Single().Changes);

        var result = (await Service().ApplyAsync(request, Actor, CancellationToken.None)).Data!;

        Assert.Equal(1, result.WrittenEmployeeCount);
        Assert.Equal(1, result.WrittenChangeCount);
        Assert.Empty(result.Failed);
        Assert.NotNull(result.BatchGuid);
        Assert.Equal(Now.AddDays(7), result.UndoDeadline);

        var profile = await _db.Queryable<EmployeeProfile>().FirstAsync(item => item.UserGUID == "u-ella");
        Assert.Equal("14 Bay St, Robina QLD 4226", profile.Address);
        Assert.Equal("0433 120 774", profile.Phone); // 手机没勾，保持原值，没有被「未传即清空」冲掉

        var upsert = Assert.Single(_profiles.Upserts);
        Assert.Equal("0433 120 774", upsert.Phone);
        Assert.Equal(0, upsert.ExpectedSensitiveRevision);

        var batch = await _db.Queryable<MyobEmployeeSyncBatch>().FirstAsync(item => item.BatchGuid == result.BatchGuid);
        Assert.Equal(1, batch.ChangeCount);
        Assert.Equal(Actor, batch.ActorUserGuid);
        Assert.Contains("14 Bay St", batch.ChangesJson);
    }

    [Fact]
    public async Task 写入_姓名走用户表_名中间名姓与全名一起更新_生日敏感字段进资料()
    {
        await SeedAsync();
        var preview = await PreviewAsync(fields: [MyobSyncFields.Name, MyobSyncFields.Birthday, MyobSyncFields.Gender], users: ["u-aidan"]);
        var result = (await Service().ApplyAsync(
            ApplyFrom(preview, pick: _ => true), Actor, CancellationToken.None)).Data!;

        Assert.Equal(3, result.WrittenChangeCount);
        Assert.Equal(1, result.WrittenSensitiveCount);

        var user = await _db.Queryable<User>().FirstAsync(item => item.UserGUID == "u-aidan");
        Assert.Equal("Aidan Forde", user.FullName);
        Assert.Equal("Aidan", user.GivenName);
        Assert.Null(user.MiddleName);
        Assert.Equal("Forde", user.FamilyName);

        var profile = await _db.Queryable<EmployeeProfile>().FirstAsync(item => item.UserGUID == "u-aidan");
        Assert.Equal(new DateTime(2009, 7, 5), profile.Birthday);
        Assert.Equal(EmployeeGender.Male, profile.Gender);
        Assert.Equal(1, profile.SensitiveRevision);
    }

    [Fact]
    public async Task 写入_预览之后我方值变了_该项不写_报值已变化()
    {
        await SeedAsync();
        var preview = await PreviewAsync(fields: [MyobSyncFields.Phone], users: ["u-aidan"]);
        // 预览之后有人把 Aidan 的手机填了。
        await _db.Updateable<EmployeeProfile>().SetColumns(item => item.Phone == "0400 000 000").Where(item => item.UserGUID == "u-aidan").ExecuteCommandAsync();

        var result = (await Service().ApplyAsync(ApplyFrom(preview), Actor, CancellationToken.None)).Data!;

        Assert.Equal(0, result.WrittenChangeCount);
        Assert.Null(result.BatchGuid);
        var failure = Assert.Single(result.Failed);
        Assert.Equal(MyobSyncErrorCodes.ValueChanged, failure.Code);
        Assert.Equal([MyobSyncFields.Phone], failure.Fields);
        Assert.Empty(_profiles.Upserts);
    }

    [Fact]
    public async Task 写入_预览之后MYOB的值变了_该项不写()
    {
        await SeedAsync();
        var preview = await PreviewAsync(fields: [MyobSyncFields.Phone], users: ["u-aidan"]);
        await SetDirectoryAsync(
            [new MyobEmployeeRecord("c-aidan", null, "AIDAN", "FORDE", null, true, true, ["aidan@example.com"], ["0499 111 222"])], []);

        var result = (await Service().ApplyAsync(ApplyFrom(preview), Actor, CancellationToken.None)).Data!;
        Assert.Equal(MyobSyncErrorCodes.ValueChanged, Assert.Single(result.Failed).Code);
    }

    [Fact]
    public async Task 写入_敏感字段有待审申请_没确认不写_确认后写入并作废申请()
    {
        await SeedAsync();
        await _db.Insertable(new EmployeeProfileSensitiveChangeRequest
        {
            UserGUID = "u-aidan", Status = EmployeeProfileSensitiveChangeStatus.Pending, SubmittedAt = Now.AddDays(-1),
        }).ExecuteCommandAsync();

        var preview = await PreviewAsync(fields: [MyobSyncFields.Birthday, MyobSyncFields.Phone], users: ["u-aidan"]);
        Assert.True(preview.Employees.Single().HasPendingSensitiveRequest);
        Assert.Equal(1, preview.Summary.PendingSensitiveRequestCount);

        // 没确认：生日被拒，手机（非敏感）照常写入。
        var denied = (await Service().ApplyAsync(ApplyFrom(preview), Actor, CancellationToken.None)).Data!;
        Assert.Equal(1, denied.WrittenChangeCount);
        var failure = Assert.Single(denied.Failed);
        Assert.Equal(MyobSyncErrorCodes.PendingRequestNeedsConfirm, failure.Code);
        Assert.Equal([MyobSyncFields.Birthday], failure.Fields);
        Assert.Null((await _db.Queryable<EmployeeProfile>().FirstAsync(item => item.UserGUID == "u-aidan")).Birthday);

        // 确认作废后再写。
        var preview2 = await PreviewAsync(fields: [MyobSyncFields.Birthday], users: ["u-aidan"]);
        var confirmed = (await Service().ApplyAsync(ApplyFrom(preview2, confirm: true), Actor, CancellationToken.None)).Data!;
        Assert.Equal(1, confirmed.WrittenSensitiveCount);
        Assert.Empty(confirmed.Failed);
        var request = await _db.Queryable<EmployeeProfileSensitiveChangeRequest>().FirstAsync(item => item.UserGUID == "u-aidan");
        Assert.Equal(EmployeeProfileSensitiveChangeStatus.Superseded, request.Status);
    }

    [Fact]
    public async Task 写入_预览之后敏感资料版本变了_敏感项报版本冲突()
    {
        await SeedAsync();
        var preview = await PreviewAsync(fields: [MyobSyncFields.Birthday], users: ["u-aidan"]);
        await _db.Updateable<EmployeeProfile>().SetColumns(item => item.SensitiveRevision == 5).Where(item => item.UserGUID == "u-aidan").ExecuteCommandAsync();

        var result = (await Service().ApplyAsync(ApplyFrom(preview), Actor, CancellationToken.None)).Data!;
        Assert.Equal(MyobSyncErrorCodes.VersionConflict, Assert.Single(result.Failed).Code);
        Assert.Equal(0, result.WrittenChangeCount);
    }

    [Fact]
    public async Task 写入_员工已解绑或卡已不存在_整员工失败()
    {
        await SeedAsync();
        var preview = await PreviewAsync(fields: [MyobSyncFields.Phone], users: ["u-aidan"]);
        var unboundAt = Now; // SqlSugar 表达式里不能直接引用私有静态字段
        await _db.Updateable<EmployeeMyobLink>().SetColumns(item => item.UnboundAt == unboundAt).Where(item => item.UserGuid == "u-aidan").ExecuteCommandAsync();
        var unbound = (await Service().ApplyAsync(ApplyFrom(preview), Actor, CancellationToken.None)).Data!;
        Assert.Equal(MyobSyncErrorCodes.NotBound, Assert.Single(unbound.Failed).Code);

        var request = new MyobSyncApplyRequest
        {
            ConnectionGuid = Inv,
            Items = [new MyobSyncApplyEmployeeItem { UserGuid = "u-ghost", Changes = [new MyobSyncApplyChangeItem { Field = MyobSyncFields.Phone, OurHash = "x", MyobHash = "y" }] }],
        };
        var missing = (await Service().ApplyAsync(request, Actor, CancellationToken.None)).Data!;
        Assert.Equal(MyobSyncErrorCodes.CardMissing, Assert.Single(missing.Failed).Code);
    }

    [Fact]
    public async Task 写入_资料服务返回失败_如实记入失败_不留批次()
    {
        await SeedAsync();
        var preview = await PreviewAsync(fields: [MyobSyncFields.Phone], users: ["u-aidan"]);
        _profiles.NextError = ("UPSERT_EMPLOYEE_PROFILE_FAILED", "保存员工个人信息失败");

        var result = (await Service().ApplyAsync(ApplyFrom(preview), Actor, CancellationToken.None)).Data!;
        var failure = Assert.Single(result.Failed);
        Assert.Equal(MyobSyncErrorCodes.WriteFailed, failure.Code);
        Assert.Equal("保存员工个人信息失败", failure.Message);
        Assert.Null(result.BatchGuid);
    }

    [Fact]
    public async Task 写入_没有写员工资料权限_拒绝()
    {
        await SeedAsync();
        _canEdit = false;
        var response = await Service().ApplyAsync(
            new MyobSyncApplyRequest { ConnectionGuid = Inv, Items = [new MyobSyncApplyEmployeeItem { UserGuid = "u-aidan", Changes = [new MyobSyncApplyChangeItem { Field = "phone" }] }] },
            Actor, CancellationToken.None);
        Assert.Equal(MyobSyncErrorCodes.NoPermission, response.ErrorCode);
    }

    // ───────────────────────── 服务：记录与撤销 ─────────────────────────

    private async Task<string> ApplyAidanAsync()
    {
        var preview = await PreviewAsync(fields: [MyobSyncFields.Name, MyobSyncFields.Phone, MyobSyncFields.Bank], users: ["u-aidan"]);
        var result = (await Service().ApplyAsync(ApplyFrom(preview, pick: _ => true), Actor, CancellationToken.None)).Data!;
        Assert.True(result.WrittenChangeCount == 3, string.Join("; ", result.Failed.Select(f => $"{f.Code}:{string.Join(',', f.Fields)}:{f.Message}")));
        return result.BatchGuid!;
    }

    [Fact]
    public async Task 同步记录_列出批次_可撤销_撤销后状态变为已撤销()
    {
        await SeedAsync();
        var batchGuid = await ApplyAidanAsync();

        var list = (await Service().ListBatchesAsync(Inv, CancellationToken.None)).Data!;
        var batch = Assert.Single(list);
        Assert.Equal(batchGuid, batch.BatchGuid);
        Assert.Equal(MyobSyncBatchStatuses.Active, batch.Status);
        Assert.True(batch.CanUndo);
        Assert.Equal(3, batch.ChangeCount);
        Assert.Equal(1, batch.SensitiveCount);
        Assert.Equal("u-admin", Actor); // 操作人没有用户行时姓名为空，不报错
        Assert.Null(batch.ActorName);

        await Service().UndoAsync(batchGuid, Actor, CancellationToken.None);
        var after = Assert.Single((await Service().ListBatchesAsync(Inv, CancellationToken.None)).Data!);
        Assert.Equal(MyobSyncBatchStatuses.Undone, after.Status);
        Assert.False(after.CanUndo);
    }

    [Fact]
    public async Task 撤销_还原本次写入的字段_姓名各段一起还原()
    {
        await SeedAsync();
        var batchGuid = await ApplyAidanAsync();

        var undo = (await Service().UndoAsync(batchGuid, Actor, CancellationToken.None)).Data!;
        Assert.Equal(1, undo.RestoredEmployeeCount);
        Assert.Equal(3, undo.RestoredChangeCount);
        Assert.Empty(undo.SkippedChanged);

        var user = await _db.Queryable<User>().FirstAsync(item => item.UserGUID == "u-aidan");
        Assert.Equal("Aidan Ryan Forde", user.FullName);
        Assert.Equal("Ryan", user.MiddleName);
        var profile = await _db.Queryable<EmployeeProfile>().FirstAsync(item => item.UserGUID == "u-aidan");
        Assert.Null(profile.Phone);
        Assert.Null(profile.BankBSB);
        Assert.Null(profile.BankACC);
    }

    [Fact]
    public async Task 撤销_写入之后又被人改过的字段不还原_只还原没动过的()
    {
        await SeedAsync();
        var batchGuid = await ApplyAidanAsync();
        // 写入之后有人把手机改成了别的号码。
        await _db.Updateable<EmployeeProfile>().SetColumns(item => item.Phone == "0400 999 999").Where(item => item.UserGUID == "u-aidan").ExecuteCommandAsync();

        var undo = (await Service().UndoAsync(batchGuid, Actor, CancellationToken.None)).Data!;
        Assert.Equal(2, undo.RestoredChangeCount);
        var skipped = Assert.Single(undo.SkippedChanged);
        Assert.Equal([MyobSyncFields.Phone], skipped.Fields);
        Assert.Equal("0400 999 999", (await _db.Queryable<EmployeeProfile>().FirstAsync(item => item.UserGUID == "u-aidan")).Phone);
    }

    [Fact]
    public async Task 撤销_不能重复撤销_过期不能撤销_批次不存在报错()
    {
        await SeedAsync();
        var batchGuid = await ApplyAidanAsync();
        Assert.True((await Service().UndoAsync(batchGuid, Actor, CancellationToken.None)).Success);
        Assert.Equal(MyobSyncErrorCodes.BatchAlreadyUndone, (await Service().UndoAsync(batchGuid, Actor, CancellationToken.None)).ErrorCode);
        Assert.Equal(MyobSyncErrorCodes.BatchNotFound, (await Service().UndoAsync("nope", Actor, CancellationToken.None)).ErrorCode);

        var second = await ApplyAidanAsync();
        _time.Now = Now.AddDays(8);
        Assert.Equal(MyobSyncErrorCodes.BatchExpired, (await Service().UndoAsync(second, Actor, CancellationToken.None)).ErrorCode);
        var listed = (await Service().ListBatchesAsync(Inv, CancellationToken.None)).Data!.Single(item => item.BatchGuid == second);
        Assert.Equal(MyobSyncBatchStatuses.Expired, listed.Status);
    }

    // ───────────────────────── MYOB 客户端与银行数据源 ─────────────────────────

    private static MyobApiClient ClientFor(Func<HttpRequestMessage, HttpResponseMessage> respond, List<string>? urls = null) =>
        new(new HttpClient(new StubHandler(respond, urls)), Options.Create(new MyobOptions
        {
            ClientId = "client-key", ClientSecret = "s", RedirectUri = "https://x/cb", MinRequestIntervalMs = 0,
        }));

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task 客户端_员工卡保留地址行_薪资详情解析性别与退休金()
    {
        var employees = await ClientFor(_ => Json("""
            {"Items":[{"UID":"e1","FirstName":"A","LastName":"B","Addresses":[
              {"Location":1,"Street":" 14 Bay St ","City":"Robina","State":"QLD","PostCode":"4226","Country":"Australia","Phone1":"0412345678","Email":"a@b.com"},
              {"Location":2,"Phone1":"0755551234"}]}],"NextPageLink":null}
            """)).GetEmployeesAsync("biz", "token", CancellationToken.None);
        var address = Assert.Single(employees.Single().Addresses!);
        Assert.Equal("14 Bay St", address.Street);
        Assert.Equal("4226", address.PostCode);

        var payroll = await ClientFor(_ => Json("""
            {"Items":[{"Employee":{"UID":"e1"},"DateOfBirth":"2009-07-05T00:00:00","Gender":"Male","EmploymentStatus":"Casual",
              "Superannuation":{"SuperannuationFund":{"UID":"f1","Name":"AustralianSuper"},"EmployeeMembershipNumber":" 123489034 "}}],"NextPageLink":null}
            """)).GetEmployeePayrollDetailsAsync("biz", "token", CancellationToken.None);
        var record = payroll.Single();
        Assert.Equal("Male", record.Gender);
        Assert.Equal("AustralianSuper", record.SuperFundName);
        Assert.Equal("123489034", record.SuperMembershipNumber);
    }

    [Fact]
    public async Task 客户端_付款详情_解析BSB和账号_两项都没有的账户丢弃_请求路径正确()
    {
        var urls = new List<string>();
        var records = await ClientFor(_ => Json("""
            {"Items":[{"Employee":{"UID":"e1"},"BankAccounts":[
                {"BSBNumber":"063010","BankAccountNumber":"12344821","BankAccountName":"A B"},{"BankAccountName":"empty"}]},
              {"Employee":null,"BankAccounts":[{"BSBNumber":"1"}]}],"NextPageLink":null}
            """), urls).GetEmployeePaymentDetailsAsync("biz", "token", CancellationToken.None);

        var account = Assert.Single(records.Single().BankAccounts);
        Assert.Equal("063010", account.Bsb);
        Assert.Equal("12344821", account.AccountNumber);
        Assert.Contains("/Contact/EmployeePaymentDetails", urls.Single());
    }

    [Fact]
    public async Task 银行数据源_缓存10分钟_刷新才重拉_失败原样抛出()
    {
        await SeedAsync();
        var api = new PaymentApi();
        var source = new MyobEmployeePaymentSource(_db, api, new FakeTokens(), new MemoryCache(new MemoryCacheOptions()));

        var first = await source.GetAsync(Inv, false, CancellationToken.None);
        Assert.Equal("12344821", first["c-aidan"].AccountNumber);
        Assert.False(first.ContainsKey("c-nobody")); // 没有银行账户的员工不进字典
        await source.GetAsync(Inv, false, CancellationToken.None);
        Assert.Equal(1, api.Calls);
        await source.GetAsync(Inv, true, CancellationToken.None);
        Assert.Equal(2, api.Calls);

        api.Error = new MyobApiException(MyobErrorCodes.ApiCallFailed, "MYOB Contact/EmployeePaymentDetails 接口返回 403");
        await Assert.ThrowsAsync<MyobApiException>(() => source.GetAsync(Inv, true, CancellationToken.None));
    }

    // ───────────────────────── 控制器 ─────────────────────────

    [Fact]
    public void 控制器_类级要求绑定权限_路由在employee_sync下()
    {
        var type = typeof(MyobEmployeeSyncController);
        Assert.Equal(Permissions.Myob.Employee.Bind, type.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal("api/react/v1/myob/employee-sync", type.GetCustomAttribute<RouteAttribute>()!.Template);
        var routes = type.GetMethods()
            .Select(method => method.GetCustomAttributes<HttpMethodAttribute>().SingleOrDefault()?.Template)
            .Where(template => template is not null)
            .ToHashSet();
        Assert.Superset(new HashSet<string?> { "field-stats", "preview", "apply", "batches", "batches/{batchGuid}/undo" }, routes);
    }

    [Fact]
    public async Task 控制器_没有写资料权限返回403_批次不存在返回404_成功200()
    {
        var service = new StubSyncService();
        var controller = new MyobEmployeeSyncController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                        [new System.Security.Claims.Claim("userId", Actor)], "test")),
                },
            },
        };

        service.Preview = ApiResponse<MyobSyncPreviewDto>.Error("no", MyobSyncErrorCodes.NoPermission);
        Assert.Equal(403, ((ObjectResult)await controller.Preview(new MyobSyncPreviewRequest(), CancellationToken.None)).StatusCode);

        service.Undo = ApiResponse<MyobSyncUndoResultDto>.Error("none", MyobSyncErrorCodes.BatchNotFound);
        Assert.IsType<NotFoundObjectResult>(await controller.Undo("x", CancellationToken.None));

        service.Preview = ApiResponse<MyobSyncPreviewDto>.OK(new MyobSyncPreviewDto());
        Assert.IsType<OkObjectResult>(await controller.Preview(new MyobSyncPreviewRequest(), CancellationToken.None));

        service.Preview = ApiResponse<MyobSyncPreviewDto>.Error("bad", MyobSyncErrorCodes.NoFields);
        Assert.IsType<BadRequestObjectResult>(await controller.Preview(new MyobSyncPreviewRequest(), CancellationToken.None));
    }

    // ───────────────────────── 假依赖 ─────────────────────────

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond, List<string>? urls) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            urls?.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FakeTokens : IMyobTokenProvider
    {
        public Task<string> GetAccessTokenAsync(string connectionGuid, CancellationToken cancellationToken) => Task.FromResult("access-token");
        public void Invalidate(string connectionGuid) { }
    }

    /// <summary>只实现付款详情的假 MYOB 客户端；其余接口本组用例用不到。</summary>
    private sealed class PaymentApi : IMyobApiClient
    {
        public int Calls { get; private set; }
        public MyobApiException? Error { get; set; }

        public Task<IReadOnlyList<MyobEmployeePaymentRecord>> GetEmployeePaymentDetailsAsync(string businessId, string accessToken, CancellationToken cancellationToken)
        {
            Calls++;
            if (Error is not null)
            {
                return Task.FromException<IReadOnlyList<MyobEmployeePaymentRecord>>(Error);
            }

            return Task.FromResult<IReadOnlyList<MyobEmployeePaymentRecord>>(
            [
                new MyobEmployeePaymentRecord("c-aidan", [new MyobBankAccountRecord("063010", "12344821")]),
                new MyobEmployeePaymentRecord("c-nobody", []),
            ]);
        }

        public Task<MyobTokenResponse> ExchangeCodeAsync(string code, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MyobTokenResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MyobCompanyInfo> GetCompanyAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MyobEmployeeRecord>> GetEmployeesAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MyobEmployeePayrollRecord>> GetEmployeePayrollDetailsAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MyobWageCategoryRecord>> GetWageCategoriesAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MyobTimesheetStatus> GetTimesheetStatusAsync(string businessId, string accessToken, string employeeUid, DateTime startDate, DateTime endDate, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubSyncService : IMyobEmployeeSyncService
    {
        public ApiResponse<MyobSyncPreviewDto> Preview { get; set; } = ApiResponse<MyobSyncPreviewDto>.OK(new MyobSyncPreviewDto());
        public ApiResponse<MyobSyncUndoResultDto> Undo { get; set; } = ApiResponse<MyobSyncUndoResultDto>.OK(new MyobSyncUndoResultDto());

        public Task<ApiResponse<MyobSyncPreviewDto>> PreviewAsync(MyobSyncPreviewRequest request, string actorUserGuid, CancellationToken cancellationToken) => Task.FromResult(Preview);
        public Task<ApiResponse<MyobSyncUndoResultDto>> UndoAsync(string batchGuid, string actorUserGuid, CancellationToken cancellationToken) => Task.FromResult(Undo);
        public Task<ApiResponse<MyobSyncFieldStatsDto>> GetFieldStatsAsync(MyobSyncScopeRequest request, string actorUserGuid, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiResponse<MyobSyncApplyResultDto>> ApplyAsync(MyobSyncApplyRequest request, string actorUserGuid, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApiResponse<List<MyobSyncBatchDto>>> ListBatchesAsync(string connectionGuid, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeTime(DateTime utcNow) : TimeProvider
    {
        public DateTime Now { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    private sealed class FakeDirectory : IMyobEmployeeDirectory
    {
        public MyobEmployeeDirectoryData Data { get; set; } = null!;

        public Task<MyobEmployeeDirectoryData> GetAsync(string connectionGuid, bool refresh, CancellationToken cancellationToken) =>
            Task.FromResult(Data);
    }

    private sealed class FakePayments : IMyobEmployeePaymentSource
    {
        public Dictionary<string, MyobBankAccountRecord> Accounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public MyobApiException? Error { get; set; }

        public Task<IReadOnlyDictionary<string, MyobBankAccountRecord>> GetAsync(string connectionGuid, bool refresh, CancellationToken cancellationToken) =>
            Error is not null
                ? Task.FromException<IReadOnlyDictionary<string, MyobBankAccountRecord>>(Error)
                : Task.FromResult<IReadOnlyDictionary<string, MyobBankAccountRecord>>(Accounts);
    }

    /// <summary>
    /// 贴近真实 EmployeeProfileService.UpsertAdminAsync 的语义：整份覆盖（DTO 里没带的字段会被清空）、
    /// 敏感字段变化时校验版本号、有待审申请时要求确认并将其作废、变更后版本号 +1。
    /// </summary>
    private sealed class FakeProfiles(ISqlSugarClient db) : IEmployeeProfileService
    {
        public List<EmployeeProfileUpsertDto> Upserts { get; } = [];
        public (string Code, string Message)? NextError { get; set; }

        public async Task<ApiResponse<EmployeeProfileDetailDto>> GetAdminDetailAsync(string userGuid)
        {
            var user = await db.Queryable<User>().FirstAsync(item => item.UserGUID == userGuid);
            var profile = await db.Queryable<EmployeeProfile>().FirstAsync(item => item.UserGUID == userGuid);
            return ApiResponse<EmployeeProfileDetailDto>.OK(new EmployeeProfileDetailDto
            {
                UserGUID = userGuid,
                Email = user.Email,
                FullName = user.FullName,
                Phone = profile?.Phone,
                BankBsb = profile?.BankBSB,
                BankAccountNumber = profile?.BankACC,
                SuperannuationCompanyName = profile?.SuperannuationCompanyName,
                SuperannuationCompanyCode = profile?.SuperannuationCompanyCode,
                SuperannuationAccountNumber = profile?.SuperannuationAccount,
                Birthday = profile?.Birthday,
                Gender = profile?.Gender?.ToString().ToLowerInvariant(),
                EmploymentType = profile?.EmployeeType is { } type ? MyobEmployeeSyncRules.EmployeeTypeKey(type) : null,
                Address = profile?.Address,
                SensitiveRevision = profile?.SensitiveRevision ?? 0,
            });
        }

        public async Task<ApiResponse<EmployeeProfileDetailDto>> UpsertAdminAsync(string userGuid, EmployeeProfileUpsertDto dto)
        {
            Upserts.Add(dto);
            if (NextError is { } error)
            {
                return ApiResponse<EmployeeProfileDetailDto>.Error(error.Message, error.Code);
            }

            var profile = await db.Queryable<EmployeeProfile>().FirstAsync(item => item.UserGUID == userGuid);
            var sensitiveChanged = profile.Birthday?.Date != dto.Birthday?.Date
                || (profile.BankBSB ?? "") != (dto.BankBsb ?? "")
                || (profile.BankACC ?? "") != (dto.BankAccountNumber ?? "")
                || (profile.SuperannuationCompanyName ?? "") != (dto.SuperannuationCompanyName ?? "")
                || (profile.SuperannuationAccount ?? "") != (dto.SuperannuationAccountNumber ?? "");
            if (sensitiveChanged)
            {
                if (dto.ExpectedSensitiveRevision.HasValue && dto.ExpectedSensitiveRevision.Value != profile.SensitiveRevision)
                {
                    return ApiResponse<EmployeeProfileDetailDto>.Error("正式敏感资料已变化", EmployeeProfileSensitiveChangeService.VersionConflictCode);
                }

                var pending = await db.Queryable<EmployeeProfileSensitiveChangeRequest>()
                    .Where(item => item.UserGUID == userGuid && item.Status == EmployeeProfileSensitiveChangeStatus.Pending)
                    .ToListAsync();
                if (pending.Count > 0)
                {
                    if (dto.ConfirmSupersedePendingSensitiveChangeRequest != true)
                    {
                        return ApiResponse<EmployeeProfileDetailDto>.Error("存在待审核敏感资料变更", EmployeeProfileService.PendingChangeConfirmationRequiredCode);
                    }

                    await db.Updateable<EmployeeProfileSensitiveChangeRequest>()
                        .SetColumns(item => item.Status == EmployeeProfileSensitiveChangeStatus.Superseded)
                        .Where(item => item.UserGUID == userGuid && item.Status == EmployeeProfileSensitiveChangeStatus.Pending)
                        .ExecuteCommandAsync();
                }
            }

            profile.Phone = dto.Phone;
            profile.BankBSB = dto.BankBsb;
            profile.BankACC = dto.BankAccountNumber;
            profile.SuperannuationCompanyName = dto.SuperannuationCompanyName;
            profile.SuperannuationCompanyCode = dto.SuperannuationCompanyCode;
            profile.SuperannuationAccount = dto.SuperannuationAccountNumber;
            profile.Birthday = dto.Birthday?.Date;
            profile.Gender = Enum.TryParse<EmployeeGender>(dto.Gender, true, out var gender) ? gender : null;
            profile.EmployeeType = dto.EmploymentType?.ToLowerInvariant() switch
            {
                "fulltime" => EmployeeType.FullTime,
                "parttime" => EmployeeType.PartTime,
                "casual" => EmployeeType.Temporary,
                _ => null,
            };
            profile.Address = dto.Address;
            if (sensitiveChanged)
            {
                profile.SensitiveRevision++;
            }

            await db.Updateable(profile).ExecuteCommandAsync();
            if (dto.HasEmail)
            {
                await db.Updateable<User>().SetColumns(item => item.Email == dto.Email!).Where(item => item.UserGUID == userGuid).ExecuteCommandAsync();
            }

            return await GetAdminDetailAsync(userGuid);
        }

        public Task<ApiResponse<PagedResult<EmployeeProfileListItemDto>>> GetAdminListAsync(EmployeeProfileQueryDto query) => throw new NotSupportedException();
        public Task<ApiResponse<EmployeeProfileDetailDto>> GetSelfAsync() => throw new NotSupportedException();
        public Task<ApiResponse<EmployeeProfileDetailDto>> UpsertSelfAsync(EmployeeProfileUpsertDto dto) => throw new NotSupportedException();
    }
}
