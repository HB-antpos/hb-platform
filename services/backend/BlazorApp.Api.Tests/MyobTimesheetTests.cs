using System.Reflection;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Api.Services.Attendance;
using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// MYOB 对接阶段 2（本批：工资类别映射 + 只读推送预览）：
/// 规划器（跨公司分摊、装填顺序、按日拆分）、类别映射服务（建议 / 保存校验）、预览服务（拦截项 / 提示项 / 状态 / MYOB 现状）、控制器权限。
/// SQLite 临时库 + 假 MYOB + 假工资供数，不访问真实 MYOB。
/// 注意：MYOB 的 PayrollCategory/Wage 与 Payroll/Timesheet 的真实响应需用已授权公司文件在线上只读核对；
/// Timesheet PUT 是替换还是合并，必须在测试员工 / 空周期上实测后才能开放推送，本批不写 MYOB。
/// </summary>
public sealed class MyobTimesheetTests : IDisposable
{
    // 固定一个周三（悉尼本地 2026-11-04）：当前周期（1 周一期）起始日 2026-11-02，默认预览上一期 2026-10-26 ~ 11-01。
    private static readonly DateTime Now = new(2026, 11, 4, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Start = new(2026, 10, 26);
    private const string Inv = "conn-inv";
    private const string Aus = "conn-aus";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hb-myob-ts-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;
    private readonly FakeApi _api = new();
    private readonly FakeTokens _tokens = new();
    private readonly FakeScope _scope = new();
    private readonly FakeSource _source = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly FakeTime _time = new(Now);

    public MyobTimesheetTests()
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
            typeof(MyobPayCategoryMapping), typeof(Store), typeof(User), typeof(UserStore), typeof(EmployeeProfile),
            typeof(EmployeeMinorCompliance));
    }

    public void Dispose()
    {
        _cache.Dispose();
        _db.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private MyobPayCategoryService Categories() => new(_db, _api, _tokens, _cache, _time);

    private MyobTimesheetPreviewService Preview(MyobOptions? options = null) => new(
        _db, _source, _scope, Categories(), _api, _tokens, Options.Create(options ?? new MyobOptions()), _time);

    // ───────────────────────── 规划器（纯函数） ─────────────────────────

    private static MyobWorkItem W(string company, string store, string date, MyobWorkKind kind, double hours) =>
        new(company, store, DateTime.Parse(date), kind, (int)Math.Round(hours * 60));

    private static int Minutes(MyobCompanyPlan plan, string category) =>
        plan.Pushed.Where(line => line.CategoryKey == category).Sum(line => line.Minutes);

    [Fact]
    public void 规划_全转账_全部推送_类别映射正确_公假和带薪公假归平日_折现不推送()
    {
        var items = new[]
        {
            W(Inv, "1014", "2026-10-26", MyobWorkKind.Weekday, 8),
            W(Inv, "1014", "2026-10-27", MyobWorkKind.PublicHolidayWork, 8),
            W(Inv, "1014", "2026-10-28", MyobWorkKind.PublicHolidayPay, 7.6),
            W(Inv, "1014", "2026-10-31", MyobWorkKind.Saturday, 4),
            W(Inv, "1014", "2026-11-01", MyobWorkKind.Sunday, 5),
            W(Inv, "1014", "2026-10-29", MyobWorkKind.Sick, 6),
            W(Inv, "1014", "2026-10-30", MyobWorkKind.Annual, 8),
            W(Inv, "1014", "2026-10-30", MyobWorkKind.AnnualCashOut, 2),
        };
        var plan = MyobTimesheetPlanner.Plan(PayrollCalculator.PayModeTransfer, 0m, 1, items).Companies[Inv];

        Assert.Equal(items.Sum(item => item.Minutes), plan.CapacityMinutes);
        Assert.Equal((int)Math.Round((8 + 8 + 7.6) * 60), Minutes(plan, MyobPayCategoryKeys.Weekday));
        Assert.Equal(240, Minutes(plan, MyobPayCategoryKeys.Saturday));
        Assert.Equal(300, Minutes(plan, MyobPayCategoryKeys.Sunday));
        Assert.Equal(360, Minutes(plan, MyobPayCategoryKeys.PersonalLeave));
        Assert.Equal(480, Minutes(plan, MyobPayCategoryKeys.HolidayLeave));
        Assert.Equal(120, plan.CashOutTransferMinutes); // 折现占用额度但不推送
        Assert.Equal(plan.CapacityMinutes, plan.PushedMinutes + plan.CashOutTransferMinutes);
        Assert.Empty(plan.CashByDayKind);
    }

    [Fact]
    public void 规划_临时工_周六周日走临时工专用类别_平日和假期类别不变_全职兼职不受影响()
    {
        var items = new[]
        {
            W(Inv, "1014", "2026-10-26", MyobWorkKind.Weekday, 8),
            W(Inv, "1014", "2026-10-27", MyobWorkKind.PublicHolidayWork, 4),
            W(Inv, "1014", "2026-10-31", MyobWorkKind.Saturday, 4),
            W(Inv, "1014", "2026-11-01", MyobWorkKind.Sunday, 5),
        };

        var casual = MyobTimesheetPlanner.Plan(PayrollCalculator.PayModeTransfer, 0m, 1, items, isCasual: true).Companies[Inv];
        Assert.Equal(240, Minutes(casual, MyobPayCategoryKeys.SaturdayCasual));
        Assert.Equal(300, Minutes(casual, MyobPayCategoryKeys.SundayCasual));
        Assert.Equal(0, Minutes(casual, MyobPayCategoryKeys.Saturday));
        Assert.Equal(0, Minutes(casual, MyobPayCategoryKeys.Sunday));
        Assert.Equal(720, Minutes(casual, MyobPayCategoryKeys.Weekday)); // 平日 8h + 公假上班 4h 仍是平日类别
        Assert.Equal(casual.CapacityMinutes, casual.PushedMinutes);

        // 全职 / 兼职（默认）：周末仍走原来的类别，没有任何临时工类别的行。
        foreach (var regular in new[]
                 {
                     MyobTimesheetPlanner.Plan(PayrollCalculator.PayModeTransfer, 0m, 1, items).Companies[Inv],
                     MyobTimesheetPlanner.Plan(PayrollCalculator.PayModeTransfer, 0m, 1, items, isCasual: false).Companies[Inv],
                 })
        {
            Assert.Equal(240, Minutes(regular, MyobPayCategoryKeys.Saturday));
            Assert.Equal(300, Minutes(regular, MyobPayCategoryKeys.Sunday));
            Assert.Equal(0, Minutes(regular, MyobPayCategoryKeys.SaturdayCasual));
            Assert.Equal(0, Minutes(regular, MyobPayCategoryKeys.SundayCasual));
        }
    }

    [Fact]
    public void 规划_临时工_转账加现金_周末仍先进转账_额度分配与全职一致()
    {
        var items = new[]
        {
            W(Inv, "1013", "2026-10-26", MyobWorkKind.Weekday, 8),
            W(Inv, "1013", "2026-10-31", MyobWorkKind.Saturday, 8),
        };
        var plan = MyobTimesheetPlanner.Plan(PayrollCalculator.PayModeMixed, 10m, 1, items, isCasual: true).Companies[Inv];

        Assert.Equal(600, plan.CapacityMinutes);
        Assert.Equal(480, Minutes(plan, MyobPayCategoryKeys.SaturdayCasual)); // 周六先进转账
        Assert.Equal(120, Minutes(plan, MyobPayCategoryKeys.Weekday));
        Assert.Equal(360, plan.CashByDayKind.Sum(pair => pair.Value));         // 现金里的是平日 6h，与类别无关
    }

    [Fact]
    public void 规划_全现金_不推送任何工时()
    {
        var plan = MyobTimesheetPlanner.Plan(
            PayrollCalculator.PayModeCash, 0m, 1, [W(Inv, "1014", "2026-10-26", MyobWorkKind.Weekday, 8)]).Companies[Inv];
        Assert.Equal(0, plan.CapacityMinutes);
        Assert.Empty(plan.Pushed);
        Assert.Equal(480, plan.CashByDayKind[(new DateTime(2026, 10, 26), MyobWorkKind.Weekday)]);
    }

    [Fact]
    public void 规划_转账加现金_单公司_周六先进转账_平日按日期从早到晚填满()
    {
        var items = new[]
        {
            W(Inv, "1013", "2026-10-26", MyobWorkKind.Weekday, 8),
            W(Inv, "1013", "2026-10-27", MyobWorkKind.Weekday, 8),
            W(Inv, "1013", "2026-10-28", MyobWorkKind.Weekday, 6),
            W(Inv, "1013", "2026-10-31", MyobWorkKind.Saturday, 8),
        };
        var plan = MyobTimesheetPlanner.Plan(PayrollCalculator.PayModeMixed, 15m, 1, items).Companies[Inv];

        Assert.Equal(900, plan.CapacityMinutes);
        Assert.Equal(480, Minutes(plan, MyobPayCategoryKeys.Saturday));       // 周六 8h 全进转账，按实际日期
        Assert.Equal(420, Minutes(plan, MyobPayCategoryKeys.Weekday));        // 平日只进 7h
        var weekday = plan.Pushed.Single(line => line.CategoryKey == MyobPayCategoryKeys.Weekday);
        Assert.Equal(new DateTime(2026, 10, 26), weekday.Date);               // 全部落在周一（最早的一天）
        // 现金：周一剩 1h、周二 8h、周三 6h = 15h
        Assert.Equal(900, plan.CashByDayKind.Where(pair => pair.Key.Kind == MyobWorkKind.Weekday).Sum(pair => pair.Value));
        Assert.Equal(plan.ActualMinutes, plan.PushedMinutes + plan.CashByDayKind.Values.Sum());
    }

    [Fact]
    public void 规划_跨公司按工时比例分摊固定转账工时_3比1_再各自装填()
    {
        var items = new[]
        {
            W(Inv, "1013", "2026-10-26", MyobWorkKind.Weekday, 8),
            W(Inv, "1013", "2026-10-27", MyobWorkKind.Weekday, 8),
            W(Inv, "1013", "2026-10-28", MyobWorkKind.Weekday, 6),
            W(Inv, "1013", "2026-10-31", MyobWorkKind.Saturday, 8),
            W(Aus, "1025", "2026-10-29", MyobWorkKind.Weekday, 8),
            W(Aus, "1025", "2026-10-30", MyobWorkKind.Weekday, 2),
        };
        var result = MyobTimesheetPlanner.Plan(PayrollCalculator.PayModeMixed, 20m, 1, items);

        Assert.Equal(900, result.Companies[Inv].CapacityMinutes); // 15.00 h
        Assert.Equal(300, result.Companies[Aus].CapacityMinutes); // 5.00 h
        Assert.Equal(1200, result.Companies.Values.Sum(plan => plan.CapacityMinutes)); // 总额度不多不少
        Assert.Equal(480, Minutes(result.Companies[Inv], MyobPayCategoryKeys.Saturday));
        Assert.Equal(420, Minutes(result.Companies[Inv], MyobPayCategoryKeys.Weekday));
        Assert.Equal(300, Minutes(result.Companies[Aus], MyobPayCategoryKeys.Weekday));
        Assert.Equal(new DateTime(2026, 10, 29), result.Companies[Aus].Pushed.Single().Date); // 周四 8h 里先填 5h
    }

    [Fact]
    public void 规划_分摊取整的余数逐分钟补齐_总额度不丢_结果确定()
    {
        var actual = new Dictionary<string, int> { ["a"] = 100, ["b"] = 100, ["c"] = 100 };
        var capacity = MyobTimesheetPlanner.AllocateCapacity(PayrollCalculator.PayModeMixed, 5m / 60m, 1, actual);
        Assert.Equal(5, capacity.Values.Sum()); // 5 分钟分给三家：不是 1+1+1 丢 2 分钟
        Assert.Equal([2, 2, 1], new[] { capacity["a"], capacity["b"], capacity["c"] }); // 同量按公司标识先后
        var again = MyobTimesheetPlanner.AllocateCapacity(PayrollCalculator.PayModeMixed, 5m / 60m, 1, actual);
        Assert.Equal(capacity, again);
    }

    [Fact]
    public void 规划_固定转账工时超过实际工时_按实际全部转账_双周容量乘周数()
    {
        var items = new[] { W(Inv, "1014", "2026-10-26", MyobWorkKind.Weekday, 8) };
        var tooMuch = MyobTimesheetPlanner.Plan(PayrollCalculator.PayModeMixed, 40m, 1, items).Companies[Inv];
        Assert.Equal(480, tooMuch.CapacityMinutes);
        Assert.Equal(480, tooMuch.PushedMinutes);

        var fortnight = MyobTimesheetPlanner.AllocateCapacity(
            PayrollCalculator.PayModeMixed, 10m, 2, new Dictionary<string, int> { [Inv] = 100 * 60 });
        Assert.Equal(20 * 60, fortnight[Inv]);
    }

    [Fact]
    public void 规划_带薪假装填顺序_带薪公假_病假_年假_折现_公假上班_平日()
    {
        var items = new[]
        {
            W(Inv, "1014", "2026-10-26", MyobWorkKind.Weekday, 10),
            W(Inv, "1014", "2026-10-27", MyobWorkKind.PublicHolidayWork, 10),
            W(Inv, "1014", "2026-10-28", MyobWorkKind.AnnualCashOut, 4),
            W(Inv, "1014", "2026-10-29", MyobWorkKind.Annual, 4),
            W(Inv, "1014", "2026-10-30", MyobWorkKind.Sick, 4),
            W(Inv, "1014", "2026-10-31", MyobWorkKind.PublicHolidayPay, 4),
        };
        // 额度 14h：带薪公假 4 → 病假 4 → 年假 4 → 折现 2（只进一半，占用额度不推送）
        var plan = MyobTimesheetPlanner.Plan(PayrollCalculator.PayModeMixed, 14m, 1, items).Companies[Inv];
        Assert.Equal(240, Minutes(plan, MyobPayCategoryKeys.Weekday));       // 只有带薪公假 4h；平日 / 公假上班一点都没进
        Assert.Equal(240, Minutes(plan, MyobPayCategoryKeys.PersonalLeave));
        Assert.Equal(240, Minutes(plan, MyobPayCategoryKeys.HolidayLeave));
        Assert.Equal(120, plan.CashOutTransferMinutes);
        Assert.Equal(840, plan.CapacityMinutes);
        Assert.Equal(plan.CapacityMinutes, plan.PushedMinutes + plan.CashOutTransferMinutes);
    }

    [Fact]
    public void 规划_同一种内按日期早到晚_分钟为0的忽略_没有工时返回空()
    {
        var items = new[]
        {
            W(Inv, "1014", "2026-10-28", MyobWorkKind.Weekday, 4),
            W(Inv, "1014", "2026-10-26", MyobWorkKind.Weekday, 4),
            new MyobWorkItem(Inv, "1014", new DateTime(2026, 10, 27), MyobWorkKind.Weekday, 0),
        };
        var plan = MyobTimesheetPlanner.Plan(PayrollCalculator.PayModeMixed, 5m, 1, items).Companies[Inv];
        Assert.Equal([new DateTime(2026, 10, 26), new DateTime(2026, 10, 28)], plan.Pushed.Select(line => line.Date).ToArray());
        Assert.Equal([240, 60], plan.Pushed.Select(line => line.Minutes).ToArray());

        Assert.Empty(MyobTimesheetPlanner.Plan(PayrollCalculator.PayModeTransfer, 0m, 1, []).Companies);
    }

    // ───────────────────────── 工资类别映射 ─────────────────────────

    private async Task SeedConnectionsAsync()
    {
        foreach (var (guid, name) in new[] { (Inv, "Hot Bargain Investment Pty Ltd"), (Aus, "Hot Bargain Australian Pty Ltd") })
        {
            await _db.Insertable(new MyobConnection
            {
                ConnectionGuid = guid, BusinessId = $"biz-{guid}", BusinessName = name,
                Status = MyobConnectionStatuses.Active, AuthorizedAt = Now, CreatedAt = Now,
            }).ExecuteCommandAsync();
        }
    }

    private static IReadOnlyList<MyobWageCategoryRecord> StandardCategories() =>
    [
        new("cat-base", "Base Hourly", "Hourly"),
        new("cat-sat", "Saturday Hourly", "Hourly"),
        new("cat-sun", "Sunday Hourly", "Hourly"),
        new("cat-personal", "Personal Leave Pay", "Hourly"),
        new("cat-holiday", "Holiday Leave Pay", "Hourly"),
        new("cat-ot", "Overtime", "Hourly"),
        new("cat-salary", "Base Salary", "Salary"),
    ];

    private async Task SaveCompleteMappingAsync(string connection = Inv)
    {
        _api.Categories = StandardCategories();
        var result = await Categories().SaveAsync(connection, new MyobPayCategoryMappingDto
        {
            WeekdayUid = "cat-base", SaturdayUid = "cat-sat", SundayUid = "cat-sun",
            PersonalLeaveUid = "cat-personal", HolidayLeaveUid = "cat-holiday",
        }, "admin-1", CancellationToken.None);
        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task 类别映射_按名称相近给建议_不会把同一个类别推荐给两项_平日不抢周末和假期类别()
    {
        await SeedConnectionsAsync();
        _api.Categories = StandardCategories();
        var dto = (await Categories().GetAsync(Inv, false, CancellationToken.None)).Data!;

        Assert.Equal("cat-base", dto.Suggestions.WeekdayUid);
        Assert.Equal("cat-sat", dto.Suggestions.SaturdayUid);
        Assert.Equal("cat-sun", dto.Suggestions.SundayUid);
        Assert.Equal("cat-personal", dto.Suggestions.PersonalLeaveUid);
        Assert.Equal("cat-holiday", dto.Suggestions.HolidayLeaveUid);
        Assert.Equal(0, dto.MappedCount); // 建议不等于已保存
        Assert.False(dto.Complete);
        Assert.Equal(7, dto.Categories.Count);
        Assert.Equal(dto.Categories.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Select(item => item.Uid), dto.Categories.Select(item => item.Uid));

        // 没有名称相近的类别：不乱推荐（周六 / 周日 / 年假为空），平日退到「名称里没有周末 / 假期字样的计时类别」。
        _api.Categories = [new("x1", "Casual Rate", "Hourly"), new("x2", "Bonus", "Salary")];
        _cache.Remove($"myob:wagecats:{Inv}");
        var sparse = (await Categories().GetAsync(Inv, false, CancellationToken.None)).Data!;
        Assert.Null(sparse.Suggestions.SaturdayUid);
        Assert.Null(sparse.Suggestions.HolidayLeaveUid);
        Assert.Equal("x1", sparse.Suggestions.WeekdayUid);
    }

    /// <summary>线上两家公司文件的真实类别名（2026-10-27 只读核对所得，仅名称与类型）；用 name 当 Uid 方便断言。</summary>
    private static IReadOnlyList<MyobWageCategoryRecord> RealCategories(bool australian)
    {
        var common = new (string Name, string Type)[]
        {
            ("Advance", "Salary"), ("Annual Leave Loading", "Hourly"), ("Annual Leave Pay", "Hourly"),
            ("Base Hourly", "Hourly"), ("Base Salary", "Salary"), ("Bonus", "Salary"), ("CDEP Payments", "Hourly"),
            ("Commission", "Salary"), ("FP: AL Loading - ULT", "Hourly"), ("FP: Unused AL - ULT", "Hourly"),
            ("FP: Unused LSL - ULT", "Hourly"), ("Long Service Leave", "Hourly"), ("Other Income", "Salary"),
            ("Personal Leave Pay", "Hourly"), ("PUBLIC HOLIDAY (x2)", "Hourly"), ("PUBLIC HOLIDAY (x2.25)", "Hourly"),
            ("SATURDAY (x1.25)", "Hourly"), ("SUNDAY (x1.4)", "Hourly"), ("SUNDAY (x1.5)", "Hourly"),
            ("Unused Annual Leave", "Hourly"), ("Unused Long Service Leave", "Hourly"),
        };
        var specific = australian
            ? new (string Name, string Type)[]
            {
                ("Back Pay", "Hourly"), ("Overtime (1.5x)", "Hourly"), ("Overtime (2x)", "Hourly"),
                ("Pay in lieu of notice", "Hourly"), ("SATUDAY (x 1.2)", "Hourly"), // 线上确有这个拼错的类别
            }
            : new (string Name, string Type)[]
            {
                ("Back Pay", "Salary"), ("ETP TAX FREE", "Salary"), ("ETP Taxable", "Salary"),
                ("Paid Parental Leave", "Salary"), ("SATURDAY (x1.2)", "Hourly"),
            };
        return common.Concat(specific).Select(item => new MyobWageCategoryRecord(item.Name, item.Name, item.Type)).ToList();
    }

    [Fact]
    public async Task 类别映射_建议_用线上真实类别名_年假选Annual_Leave_Pay_倍率有多档并列时不猜()
    {
        await SeedConnectionsAsync();

        // Australian：只有一个 SATURDAY（拼错的 SATUDAY 不算），周日有 x1.4 / x1.5 两档 → 周日不猜。
        _api.Categories = RealCategories(australian: true);
        var aus = (await Categories().GetAsync(Inv, false, CancellationToken.None)).Data!;
        Assert.Equal("Base Hourly", aus.Suggestions.WeekdayUid);
        Assert.Equal("SATURDAY (x1.25)", aus.Suggestions.SaturdayUid);
        Assert.Null(aus.Suggestions.SundayUid);
        Assert.Equal("Personal Leave Pay", aus.Suggestions.PersonalLeaveUid);
        // 线上曾被建议成 Annual Leave Loading（附加津贴）——年假必须是休假期间的工资，不是 Loading / Unused。
        Assert.Equal("Annual Leave Pay", aus.Suggestions.HolidayLeaveUid);

        // Investment：周六 x1.2 / x1.25、周日 x1.4 / x1.5 都并列 → 都不猜；Back Pay 是 Salary 型，不会被当成平日。
        _api.Categories = RealCategories(australian: false);
        _cache.Remove($"myob:wagecats:{Inv}");
        var invest = (await Categories().GetAsync(Inv, false, CancellationToken.None)).Data!;
        Assert.Equal("Base Hourly", invest.Suggestions.WeekdayUid);
        Assert.Null(invest.Suggestions.SaturdayUid);
        Assert.Null(invest.Suggestions.SundayUid);
        Assert.Equal("Personal Leave Pay", invest.Suggestions.PersonalLeaveUid);
        Assert.Equal("Annual Leave Pay", invest.Suggestions.HolidayLeaveUid);
    }

    [Fact]
    public async Task 类别映射_建议_临时工周末只认名字带Casual的类别_全职兼职周末不会被Casual抢走或误选()
    {
        await SeedConnectionsAsync();
        _api.Categories =
        [
            new("base", "Base Hourly", "Hourly"),
            new("sat", "SATURDAY (x1.25)", "Hourly"), new("sat-c", "SATURDAY CASUAL (x1.5)", "Hourly"),
            new("sun", "SUNDAY (x1.5)", "Hourly"), new("sun-c", "SUNDAY CASUAL (x1.75)", "Hourly"),
        ];
        var dto = (await Categories().GetAsync(Inv, false, CancellationToken.None)).Data!;
        Assert.Equal("sat", dto.Suggestions.SaturdayUid);
        Assert.Equal("sun", dto.Suggestions.SundayUid);
        Assert.Equal("sat-c", dto.Suggestions.SaturdayCasualUid);
        Assert.Equal("sun-c", dto.Suggestions.SundayCasualUid);

        // 没有名字带 Casual 的类别：临时工两项不猜（不能把全职的周末类别也推荐给临时工）。
        _api.Categories = [new("sat", "SATURDAY (x1.25)", "Hourly"), new("sun", "SUNDAY (x1.5)", "Hourly")];
        _cache.Remove($"myob:wagecats:{Inv}");
        var noCasual = (await Categories().GetAsync(Inv, false, CancellationToken.None)).Data!;
        Assert.Equal("sat", noCasual.Suggestions.SaturdayUid);
        Assert.Null(noCasual.Suggestions.SaturdayCasualUid);
        Assert.Null(noCasual.Suggestions.SundayCasualUid);
    }

    [Fact]
    public async Task 类别映射_临时工周末两项可选_保存读回_不影响完整度_不能与其他项选同一类别()
    {
        await SeedConnectionsAsync();
        _api.Categories = StandardCategories().Concat(
        [
            new MyobWageCategoryRecord("cat-sat-c", "Saturday Casual", "Hourly"),
            new MyobWageCategoryRecord("cat-sun-c", "Sunday Casual", "Hourly"),
        ]).ToList();

        // 只设必填 5 项：已完整，临时工两项为空。
        await SaveCompleteMappingAsync();
        _api.Categories = StandardCategories().Concat(
        [
            new MyobWageCategoryRecord("cat-sat-c", "Saturday Casual", "Hourly"),
            new MyobWageCategoryRecord("cat-sun-c", "Sunday Casual", "Hourly"),
        ]).ToList();
        _cache.Remove($"myob:wagecats:{Inv}");
        var basic = (await Categories().GetAsync(Inv, false, CancellationToken.None)).Data!;
        Assert.True(basic.Complete);
        Assert.Equal(5, basic.MappedCount);
        Assert.Null(basic.Mapping.SaturdayCasualUid);
        Assert.Equal("cat-sat-c", basic.Suggestions.SaturdayCasualUid); // 没设的临时工项照样给建议

        // 再设临时工两项：存取一致，必填计数仍是 5。
        var saved = await Categories().SaveAsync(Inv, new MyobPayCategoryMappingDto
        {
            WeekdayUid = "cat-base", SaturdayUid = "cat-sat", SundayUid = "cat-sun",
            SaturdayCasualUid = "cat-sat-c", SundayCasualUid = "cat-sun-c",
            PersonalLeaveUid = "cat-personal", HolidayLeaveUid = "cat-holiday",
        }, "admin-1", CancellationToken.None);
        Assert.True(saved.Success, saved.Message);
        var full = (await Categories().GetAsync(Inv, false, CancellationToken.None)).Data!;
        Assert.Equal("cat-sat-c", full.Mapping.SaturdayCasualUid);
        Assert.Equal("cat-sun-c", full.Mapping.SundayCasualUid);
        Assert.Equal(5, full.MappedCount);
        Assert.True(full.Complete);
        Assert.Null(full.Suggestions.SaturdayCasualUid); // 已设置的项不再给建议

        // 临时工周六与全职周六选同一个类别：拒绝；不存在的类别：拒绝。
        var duplicate = await Categories().SaveAsync(Inv, new MyobPayCategoryMappingDto
        {
            SaturdayUid = "cat-sat", SaturdayCasualUid = "cat-sat",
        }, "admin-1", CancellationToken.None);
        Assert.Equal(MyobTimesheetErrorCodes.CategoryDuplicate, duplicate.ErrorCode);
        var ghost = await Categories().SaveAsync(Inv, new MyobPayCategoryMappingDto { SundayCasualUid = "cat-ghost" }, "admin-1", CancellationToken.None);
        Assert.Equal(MyobTimesheetErrorCodes.CategoryNotFound, ghost.ErrorCode);

        // 临时工类别在 MYOB 里被删：标出缺失项，但必填项仍完整。
        _api.Categories = StandardCategories().ToList();
        var missing = (await Categories().GetAsync(Inv, true, CancellationToken.None)).Data!;
        Assert.Equal([MyobPayCategoryKeys.SaturdayCasual, MyobPayCategoryKeys.SundayCasual], missing.MissingKeys);
        Assert.True(missing.Complete);
    }

    [Fact]
    public async Task 类别映射_建议_Salary型类别不入选_Annual与Holiday_Leave_Pay并列也不猜()
    {
        await SeedConnectionsAsync();
        // 只有 Salary 型：不能记工时，平日 / 病假 / 年假都不建议。
        _api.Categories = [new("s1", "Base Salary", "Salary"), new("s2", "Personal Leave Pay", "Salary"), new("s3", "Annual Leave Pay", "Salary")];
        var salaryOnly = (await Categories().GetAsync(Inv, false, CancellationToken.None)).Data!;
        Assert.Null(salaryOnly.Suggestions.WeekdayUid);
        Assert.Null(salaryOnly.Suggestions.PersonalLeaveUid);
        Assert.Null(salaryOnly.Suggestions.HolidayLeaveUid);

        // 两个口径都叫得通 → 并列，不替财务选；读不到类型（null）的类别不排除。
        _api.Categories = [new("a", "Annual Leave Pay", null), new("h", "Holiday Leave Pay", "Hourly")];
        _cache.Remove($"myob:wagecats:{Inv}");
        var tie = (await Categories().GetAsync(Inv, false, CancellationToken.None)).Data!;
        Assert.Null(tie.Suggestions.HolidayLeaveUid);

        _api.Categories = [new("a", "Annual Leave Pay", null)];
        _cache.Remove($"myob:wagecats:{Inv}");
        Assert.Equal("a", (await Categories().GetAsync(Inv, false, CancellationToken.None)).Data!.Suggestions.HolidayLeaveUid);
    }

    [Fact]
    public async Task 类别映射_保存后读回_五项齐全才算完整_已设置的项不再给建议()
    {
        await SeedConnectionsAsync();
        await SaveCompleteMappingAsync();
        var dto = (await Categories().GetAsync(Inv, false, CancellationToken.None)).Data!;
        Assert.True(dto.Complete);
        Assert.Equal(5, dto.MappedCount);
        Assert.Empty(dto.MissingKeys);
        Assert.Null(dto.Suggestions.WeekdayUid);
        Assert.NotNull(dto.UpdatedAt);

        // 只保存 4 项：不完整，且未设置的那项仍有建议
        await Categories().SaveAsync(Inv, new MyobPayCategoryMappingDto
        {
            WeekdayUid = "cat-base", SaturdayUid = "cat-sat", SundayUid = "cat-sun", PersonalLeaveUid = "cat-personal",
        }, "admin-1", CancellationToken.None);
        var partial = (await Categories().GetAsync(Inv, false, CancellationToken.None)).Data!;
        Assert.False(partial.Complete);
        Assert.Equal(4, partial.MappedCount);
        Assert.Equal("cat-holiday", partial.Suggestions.HolidayLeaveUid);
        Assert.Equal(1, await _db.Queryable<MyobPayCategoryMapping>().CountAsync()); // 重复保存是更新，不是新增
    }

    [Fact]
    public async Task 类别映射_保存校验_不存在的类别_同一类别映射两项_连接未启用()
    {
        await SeedConnectionsAsync();
        _api.Categories = StandardCategories();
        var ghost = await Categories().SaveAsync(Inv, new MyobPayCategoryMappingDto { WeekdayUid = "cat-ghost" }, "a", CancellationToken.None);
        Assert.Equal(MyobTimesheetErrorCodes.CategoryNotFound, ghost.ErrorCode);

        var duplicate = await Categories().SaveAsync(Inv, new MyobPayCategoryMappingDto { WeekdayUid = "cat-base", SaturdayUid = "cat-base" }, "a", CancellationToken.None);
        Assert.Equal(MyobTimesheetErrorCodes.CategoryDuplicate, duplicate.ErrorCode);
        Assert.Equal(0, await _db.Queryable<MyobPayCategoryMapping>().CountAsync());

        await _db.Updateable<MyobConnection>().SetColumns(item => item.Status == MyobConnectionStatuses.NeedsReauth)
            .Where(item => item.ConnectionGuid == Aus).ExecuteCommandAsync();
        Assert.Equal(MyobErrorCodes.ConnectionNotActive, (await Categories().GetAsync(Aus, false, CancellationToken.None)).ErrorCode);
        Assert.Equal(MyobErrorCodes.ConnectionNotActive,
            (await Categories().SaveAsync(Aus, new MyobPayCategoryMappingDto(), "a", CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task 类别映射_MYOB里类别被删除后标出缺失项_且不算已映射_读取失败原样返回错误()
    {
        await SeedConnectionsAsync();
        await SaveCompleteMappingAsync();
        _api.Categories = StandardCategories().Where(item => item.Uid != "cat-sat").ToList();
        var dto = (await Categories().GetAsync(Inv, true, CancellationToken.None)).Data!;
        Assert.Equal([MyobPayCategoryKeys.Saturday], dto.MissingKeys);
        Assert.Equal(4, dto.MappedCount);
        Assert.False(dto.Complete);

        _api.CategoriesError = new MyobApiException(MyobErrorCodes.ApiCallFailed, "MYOB Payroll/PayrollCategory/Wage 接口返回 401");
        var failed = await Categories().GetAsync(Inv, true, CancellationToken.None);
        Assert.False(failed.Success);
        Assert.Equal(MyobErrorCodes.ApiCallFailed, failed.ErrorCode);
    }

    [Fact]
    public async Task 类别映射_缓存10分钟_刷新才重新读MYOB()
    {
        await SeedConnectionsAsync();
        _api.Categories = StandardCategories();
        await Categories().GetAsync(Inv, false, CancellationToken.None);
        await Categories().GetAsync(Inv, false, CancellationToken.None);
        Assert.Equal(1, _api.CategoryCalls);
        await Categories().GetAsync(Inv, true, CancellationToken.None);
        Assert.Equal(2, _api.CategoryCalls);
    }

    // ───────────────────────── MYOB 客户端（工资类别 / Timesheet 状态） ─────────────────────────

    private static MyobApiClient ClientFor(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)), Options.Create(new MyobOptions
        {
            ClientId = "client-key", ClientSecret = "s", RedirectUri = "https://x/cb", MinRequestIntervalMs = 0,
        }));

    private static HttpResponseMessage Json(string body, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task 客户端_工资类别_解析UID名称和计时类型_缺UID或名称的丢弃()
    {
        var client = ClientFor(_ => Json("""
            {"Items":[{"UID":"c1","Name":"Base Hourly","WageType":"Hourly"},{"UID":"c2","Name":"Base Salary","WageType":"Salary"},
                      {"UID":"c3","Name":""},{"Name":"No uid"}],"NextPageLink":null}
            """));
        var items = await client.GetWageCategoriesAsync("biz", "token", CancellationToken.None);
        Assert.Equal(["c1", "c2"], items.Select(item => item.Uid).ToArray());
        Assert.Equal("Hourly", items[0].WageType);
    }

    [Theory]
    [InlineData("""{"Employee":{"UID":"e"},"Lines":[{"Entries":[{"Date":"2026-10-26","Hours":8,"Processed":false}]}]}""", true, false)]
    [InlineData("""{"Employee":{"UID":"e"},"Lines":[{"Entries":[{"Hours":8,"Processed":true},{"Hours":2,"Processed":false}]}]}""", true, true)]
    [InlineData("""{"Items":[{"Lines":[{"Entries":[{"Hours":1,"Processed":true}]}]}]}""", true, true)]
    [InlineData("""{"Lines":[]}""", false, false)]
    [InlineData("""{"Lines":[{"Entries":[]}]}""", false, false)]
    [InlineData("", false, false)]
    public async Task 客户端_Timesheet现状_兼容单个对象和列表_空体按没有记录(string body, bool hasEntries, bool processed)
    {
        var handler = new StubHandler(_ => Json(body));
        var client = new MyobApiClient(new HttpClient(handler), Options.Create(new MyobOptions { ClientId = "k", MinRequestIntervalMs = 0 }));
        var status = await client.GetTimesheetStatusAsync("biz", "token", "emp-1", new DateTime(2026, 10, 26), new DateTime(2026, 11, 1), CancellationToken.None);
        Assert.Equal(hasEntries, status.HasEntries);
        Assert.Equal(processed, status.HasProcessedEntries);
        var url = Assert.Single(handler.Urls);
        Assert.Contains("/biz/Payroll/Timesheet/emp-1?StartDate=2026-10-26&EndDate=2026-11-01", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 客户端_Timesheet现状_404按没有记录_其他非2xx报错且不回显响应体()
    {
        var notFound = ClientFor(_ => Json("{}", System.Net.HttpStatusCode.NotFound));
        var status = await notFound.GetTimesheetStatusAsync("b", "t", "e", Start, Start.AddDays(6), CancellationToken.None);
        Assert.False(status.HasEntries);

        var forbidden = ClientFor(_ => Json("secret-body", System.Net.HttpStatusCode.Forbidden));
        var error = await Assert.ThrowsAsync<MyobApiException>(() =>
            forbidden.GetTimesheetStatusAsync("b", "t", "e", Start, Start.AddDays(6), CancellationToken.None));
        Assert.Equal(MyobErrorCodes.ApiCallFailed, error.Code);
        Assert.Contains("403", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-body", error.Message, StringComparison.Ordinal);
    }

    // ───────────────────────── 预览服务 ─────────────────────────

    private static PayrollRate Rate(string mode, decimal fixedHours = 0m, decimal weekday = 30m, decimal? holiday = null, decimal? minimum = null, string from = "2026-07-01") =>
        new("rate-" + mode, DateTime.Parse(from), mode, fixedHours, weekday, 35m, 40m, holiday ?? weekday, 25m, 0m, minimum);

    private static PayrollWorkItem Work(string date, PayrollBucket bucket, double hours) =>
        new(DateTime.Parse(date), bucket, (int)Math.Round(hours * 60));

    private static PayrollPushEmployee Emp(
        string user, string name, PayrollRate? rate, IEnumerable<PayrollWorkItem> work,
        IEnumerable<PayrollLeaveItem>? sick = null, IEnumerable<PayrollLeaveItem>? holidayPay = null,
        IEnumerable<PayrollLeaveItem>? annual = null, IEnumerable<PayrollLeaveItem>? cashOut = null,
        DateTime? birthday = null, int? age = null, string? employmentType = null) =>
        new(user, name, birthday, age, rate is null ? [] : [rate], work.ToList(), (sick ?? []).ToList(),
            (holidayPay ?? []).ToList(), (annual ?? []).ToList(), (cashOut ?? []).ToList(), employmentType);

    private static PayrollLeaveItem Leave(string date, double hours) => new(DateTime.Parse(date), (decimal)hours);

    private async Task SeedStoreAsync(string code, string name, string? connection, bool locked, params PayrollPushEmployee[] employees)
    {
        await _db.Insertable(new Store { StoreGUID = $"g-{code}", StoreCode = code, StoreName = name, IsActive = true }).ExecuteCommandAsync();
        if (connection is not null)
        {
            await _db.Insertable(new MyobStoreMapping { StoreCode = code, ConnectionGuid = connection, MappedAt = Now }).ExecuteCommandAsync();
        }

        _source.Stores[code] = new PayrollPushStoreData(code, name, locked, employees);
    }

    private async Task SeedUserAsync(string guid, string name, params string[] storeCodes)
    {
        await _db.Insertable(new User
        {
            UserGUID = guid, Username = guid, Email = $"{guid}@example.com", FullName = name, IsActive = true, CreatedAt = Now,
        }).ExecuteCommandAsync();
        await _db.Insertable(new EmployeeProfile { UserGUID = guid }).ExecuteCommandAsync();
        foreach (var code in storeCodes)
        {
            await _db.Insertable(new UserStore { UserStoreGUID = Guid.NewGuid().ToString(), UserGUID = guid, StoreGUID = $"g-{code}" }).ExecuteCommandAsync();
        }
    }

    private async Task BindAsync(string user, string connection = Inv, string? name = null) =>
        await _db.Insertable(new EmployeeMyobLink
        {
            LinkGuid = $"link-{user}-{connection}", UserGuid = user, ConnectionGuid = connection, MyobEmployeeUid = $"uid-{user}",
            MyobName = name ?? user, MatchMethod = "Manual", BoundAt = Now,
        }).ExecuteCommandAsync();

    /// <summary>主场景：Investment（1014）+ Australian（1013 / 1025），映射完整。</summary>
    private async Task SeedMainScenarioAsync()
    {
        await SeedConnectionsAsync();
        await SaveCompleteMappingAsync();

        await SeedStoreAsync("1014", "Kawana", Inv, locked: true,
            Emp("u-mei", "Mei", Rate("Transfer"),
                [Work("2026-10-26", PayrollBucket.Weekday, 8), Work("2026-10-27", PayrollBucket.Weekday, 8),
                 Work("2026-10-28", PayrollBucket.Weekday, 8), Work("2026-10-29", PayrollBucket.Weekday, 6),
                 Work("2026-10-31", PayrollBucket.Saturday, 4)]),
            Emp("u-sarah", "Sarah", Rate("Mixed", 16m),
                [Work("2026-10-26", PayrollBucket.Weekday, 8), Work("2026-10-27", PayrollBucket.Weekday, 8), Work("2026-10-28", PayrollBucket.Weekday, 8)],
                sick: [Leave("2026-10-29", 8)]),
            Emp("u-tom", "Tom", Rate("Cash"), [Work("2026-10-26", PayrollBucket.Weekday, 20)]),
            Emp("u-priya", "Priya", Rate("Transfer"), [Work("2026-10-26", PayrollBucket.Weekday, 32)]));
        await SeedStoreAsync("1013", "Orion", Aus, locked: true,
            Emp("u-jason", "Jason", Rate("Mixed", 20m),
                [Work("2026-10-26", PayrollBucket.Weekday, 8), Work("2026-10-27", PayrollBucket.Weekday, 8),
                 Work("2026-10-28", PayrollBucket.Weekday, 6), Work("2026-10-31", PayrollBucket.Saturday, 8)]));
        await SeedStoreAsync("1025", "Kenmore", Aus, locked: true,
            Emp("u-jason", "Jason", Rate("Mixed", 20m),
                [Work("2026-10-29", PayrollBucket.Weekday, 8), Work("2026-10-30", PayrollBucket.Weekday, 2)]));

        await SeedUserAsync("u-mei", "Mei", "1014");
        await SeedUserAsync("u-sarah", "Sarah", "1014");
        await SeedUserAsync("u-tom", "Tom", "1014");
        await SeedUserAsync("u-priya", "Priya", "1014");
        await SeedUserAsync("u-jason", "Jason", "1013", "1025");
        // Jason 的 Orion 工时由 Investment 发薪（覆盖），Kenmore 仍按分店归 Australian
        await _db.Insertable(new EmployeePayrollEmployer
        {
            EmployerGuid = "emp-jason", UserGuid = "u-jason", StoreCode = "1013", ConnectionGuid = Inv,
            EffectiveFrom = Start, CreatedAt = Now,
        }).ExecuteCommandAsync();
        foreach (var user in new[] { "u-mei", "u-sarah", "u-tom", "u-jason" })
        {
            await BindAsync(user);
        }

        _source.Pairs.AddRange(new[]
        {
            ("u-mei", "1014"), ("u-sarah", "1014"), ("u-tom", "1014"), ("u-priya", "1014"), ("u-jason", "1013"), ("u-jason", "1025"),
        });
    }

    private async Task<MyobTimesheetPreviewDto> PreviewInvAsync(bool checkMyob = false, string? period = null)
    {
        var result = await Preview().PreviewAsync(Inv, period, checkMyob, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        return result.Data!;
    }

    private static MyobPreviewRowDto RowOf(MyobTimesheetPreviewDto dto, string user) => dto.Rows.Single(row => row.UserGuid == user);

    [Fact]
    public async Task 预览_默认上一周期_给出最近6期_公司没有员工时返回空()
    {
        await SeedConnectionsAsync();
        var dto = await PreviewInvAsync();
        Assert.Equal("2026-10-26", dto.PeriodStart);
        Assert.Equal("2026-11-01", dto.PeriodEnd);
        Assert.Equal(1, dto.Weeks);
        Assert.Equal(["2026-11-02", "2026-10-26", "2026-10-19", "2026-10-12", "2026-10-05", "2026-09-28"], dto.Periods.Select(item => item.Start).ToArray());
        Assert.Empty(dto.Rows);
        Assert.False(dto.PushEnabled);
    }

    [Theory]
    [InlineData("2026-10-27")]
    [InlineData("nope")]
    public async Task 预览_周期起始日不是周一或格式不对_报错(string period)
    {
        await SeedConnectionsAsync();
        var result = await Preview().PreviewAsync(Inv, period, false, CancellationToken.None);
        Assert.Equal(MyobTimesheetErrorCodes.InvalidPeriod, result.ErrorCode);
    }

    [Fact]
    public async Task 预览_可推送行_转账全进_按类别汇总_校验通过()
    {
        await SeedMainScenarioAsync();
        var dto = await PreviewInvAsync();

        var mei = RowOf(dto, "u-mei");
        Assert.Equal(MyobPreviewStatuses.Ready, mei.Status);
        Assert.Empty(mei.Blockers);
        Assert.Equal(30m, mei.Pushed.Weekday);
        Assert.Equal(4m, mei.Pushed.Saturday);
        Assert.Equal(34m, mei.PushedTotal);
        Assert.Equal(34m, mei.ActualHours);
        Assert.False(mei.IsCrossCompany);
        Assert.Equal(["1014 Kawana"], mei.StoreLabels);
        Assert.True(mei.Detail.CheckOk, mei.Detail.CheckMessage);
        Assert.Equal(7, mei.Detail.Days.Count);
        Assert.Equal("2026-10-26", mei.Detail.Days[0]);
        Assert.Equal(8m, mei.Detail.Pushed.Single(item => item.Key == MyobPayCategoryKeys.Weekday).Hours[0]);
        Assert.Equal(30m, mei.Detail.Pushed.Single(item => item.Key == MyobPayCategoryKeys.Weekday).Total);
    }

    [Fact]
    public async Task 预览_临时工周末按临时工类别推送_汇总列仍按周六周日合计_没映射临时工类别时拦截_全职兼职不受影响()
    {
        await SeedConnectionsAsync();
        // 只映射必填 5 项（临时工两项没设）
        await SaveCompleteMappingAsync();
        await SeedStoreAsync("1014", "Kawana", Inv, locked: true,
            Emp("u-cas", "Cas", Rate("Transfer"),
                [Work("2026-10-26", PayrollBucket.Weekday, 8), Work("2026-10-31", PayrollBucket.Saturday, 4), Work("2026-11-01", PayrollBucket.Sunday, 5)],
                employmentType: "Casual"),
            Emp("u-ft", "Fran", Rate("Transfer"),
                [Work("2026-10-26", PayrollBucket.Weekday, 8), Work("2026-10-31", PayrollBucket.Saturday, 4), Work("2026-11-01", PayrollBucket.Sunday, 5)],
                employmentType: "FullTime"),
            Emp("u-pt", "Pat", Rate("Transfer"),
                [Work("2026-10-31", PayrollBucket.Saturday, 4)], employmentType: "PartTime"),
            Emp("u-unk", "Unk", Rate("Transfer"),
                [Work("2026-10-31", PayrollBucket.Saturday, 4)])); // 没填员工类型：按全职 / 兼职处理
        foreach (var user in new[] { "u-cas", "u-ft", "u-pt", "u-unk" })
        {
            await SeedUserAsync(user, user, "1014");
            await BindAsync(user);
            _source.Pairs.Add((user, "1014"));
        }

        var dto = await PreviewInvAsync();
        // 临时工周末工时要用临时工类别，但还没映射：拦截，Ref 指向对应类别键。
        var cas = RowOf(dto, "u-cas");
        Assert.Equal(MyobPreviewStatuses.Blocked, cas.Status);
        Assert.Contains(cas.Blockers, item => item.Code == MyobBlockerCodes.CategoryNotMapped && item.Ref == MyobPayCategoryKeys.SaturdayCasual);
        Assert.Contains(cas.Blockers, item => item.Code == MyobBlockerCodes.CategoryNotMapped && item.Ref == MyobPayCategoryKeys.SundayCasual);
        Assert.DoesNotContain(cas.Blockers, item => item.Ref == MyobPayCategoryKeys.Saturday || item.Ref == MyobPayCategoryKeys.Sunday);
        Assert.Equal(["saturdayCasual", "sundayCasual", "weekday"], cas.Detail.Pushed.Select(item => item.Key).Order().ToArray());
        Assert.Equal(4m, cas.Pushed.Saturday); // 汇总列仍是「周六」口径
        Assert.Equal(5m, cas.Pushed.Sunday);
        Assert.Equal(17m, cas.PushedTotal);

        // 全职 / 兼职 / 没填类型：周末走原类别，没有任何拦截。
        foreach (var user in new[] { "u-ft", "u-pt", "u-unk" })
        {
            var row = RowOf(dto, user);
            Assert.Equal(MyobPreviewStatuses.Ready, row.Status);
            Assert.Empty(row.Blockers);
            Assert.DoesNotContain(row.Detail.Pushed, item => item.Key is MyobPayCategoryKeys.SaturdayCasual or MyobPayCategoryKeys.SundayCasual);
        }

        // 必填项齐全 = 就绪卡完整，不因为临时工两项没设而变成「不完整」。
        Assert.Equal(5, dto.Readiness.CategoryMappedCount);
        Assert.True(dto.Readiness.CategoryComplete);

        // 补上临时工两项映射：拦截消失，网格行带 MYOB 类别名。
        _api.Categories = StandardCategories().Concat(
        [
            new MyobWageCategoryRecord("cat-sat-c", "Saturday Casual", "Hourly"),
            new MyobWageCategoryRecord("cat-sun-c", "Sunday Casual", "Hourly"),
        ]).ToList();
        _cache.Remove($"myob:wagecats:{Inv}");
        var saved = await Categories().SaveAsync(Inv, new MyobPayCategoryMappingDto
        {
            WeekdayUid = "cat-base", SaturdayUid = "cat-sat", SundayUid = "cat-sun",
            SaturdayCasualUid = "cat-sat-c", SundayCasualUid = "cat-sun-c",
            PersonalLeaveUid = "cat-personal", HolidayLeaveUid = "cat-holiday",
        }, "admin-1", CancellationToken.None);
        Assert.True(saved.Success, saved.Message);
        var ready = RowOf(await PreviewInvAsync(), "u-cas");
        Assert.Equal(MyobPreviewStatuses.Ready, ready.Status);
        Assert.Empty(ready.Blockers);
        Assert.Equal("Saturday Casual", ready.Detail.Pushed.Single(item => item.Key == MyobPayCategoryKeys.SaturdayCasual).Label);
        Assert.Equal("Sunday Casual", ready.Detail.Pushed.Single(item => item.Key == MyobPayCategoryKeys.SundayCasual).Label);
    }

    [Fact]
    public async Task 预览_将推送网格带MYOB类别名_缓存里有才有_没有为空()
    {
        await SeedMainScenarioAsync();
        // 保存映射时已把类别列表放进缓存 → 预览直接带名字，不再请求 MYOB
        var callsBefore = _api.CategoryCalls;
        var mei = RowOf(await PreviewInvAsync(), "u-mei");
        Assert.Equal("Base Hourly", mei.Detail.Pushed.Single(item => item.Key == MyobPayCategoryKeys.Weekday).Label);
        Assert.Equal("Saturday Hourly", mei.Detail.Pushed.Single(item => item.Key == MyobPayCategoryKeys.Saturday).Label);
        Assert.Equal(callsBefore, _api.CategoryCalls);
        Assert.All(mei.Detail.Actual, item => Assert.Null(item.Label)); // 实际工时行不是 MYOB 类别

        _cache.Remove($"myob:wagecats:{Inv}");
        Assert.Null(RowOf(await PreviewInvAsync(), "u-mei").Detail.Pushed.First().Label);
        Assert.Equal(callsBefore, _api.CategoryCalls); // 缓存没了也不会为取名字去请求 MYOB
    }

    [Fact]
    public async Task 预览_病假先于平日装入转账_推送到病假类别()
    {
        await SeedMainScenarioAsync();
        var sarah = RowOf(await PreviewInvAsync(), "u-sarah");
        Assert.Equal(MyobPreviewStatuses.Ready, sarah.Status);
        Assert.Equal(8m, sarah.Pushed.PersonalLeave);
        Assert.Equal(8m, sarah.Pushed.Weekday);
        Assert.Equal(16m, sarah.PushedTotal);
        Assert.Equal(16m, sarah.AllocatedTransferHours);
        Assert.Equal(32m, sarah.ActualHours);
        // 现金：平日 16h 不推送
        Assert.Equal(16m, sarah.Detail.Cash.Single(item => item.Key == MyobKindKeys.Weekday).Total);
    }

    [Fact]
    public async Task 预览_跨公司员工_本公司分到15小时_份额按3比1_明细校验通过()
    {
        await SeedMainScenarioAsync();
        var jason = RowOf(await PreviewInvAsync(), "u-jason");

        Assert.True(jason.IsCrossCompany);
        Assert.Equal(MyobPreviewStatuses.Ready, jason.Status);
        Assert.Equal(30m, jason.ActualHours);               // 本公司名下 1013 的 30h
        Assert.Equal(15m, jason.AllocatedTransferHours);    // 固定转账 20h × 30:10
        Assert.Equal(8m, jason.Pushed.Saturday);
        Assert.Equal(7m, jason.Pushed.Weekday);
        Assert.Equal(15m, jason.PushedTotal);
        Assert.Equal(["1013 Orion"], jason.StoreLabels);
        Assert.Equal(2, jason.Detail.Shares.Count);
        var mine = jason.Detail.Shares.Single(share => share.ConnectionGuid == Inv);
        Assert.Equal(15m, mine.AllocatedTransferHours);
        Assert.Equal(30m, mine.ActualHours);
        Assert.Equal(5m, jason.Detail.Shares.Single(share => share.ConnectionGuid == Aus).AllocatedTransferHours);
        Assert.True(jason.Detail.CheckOk, jason.Detail.CheckMessage);
        Assert.Equal(15m, jason.Detail.Cash.Sum(item => item.Total));
    }

    [Fact]
    public async Task 预览_全现金_不推送状态为NoTransfer_且不再提示未绑定类拦截()
    {
        await SeedMainScenarioAsync();
        var dto = await PreviewInvAsync();
        var tom = RowOf(dto, "u-tom");
        Assert.Equal(MyobPreviewStatuses.NoTransfer, tom.Status);
        Assert.Equal(0m, tom.PushedTotal);
        Assert.Equal(20m, tom.ActualHours);

        // 全现金员工即使没绑定，也不该显示成被拦截（他本来就不用推）
        await _db.Deleteable<EmployeeMyobLink>().Where(link => link.UserGuid == "u-tom").ExecuteCommandAsync();
        Assert.Equal(MyobPreviewStatuses.NoTransfer, RowOf(await PreviewInvAsync(), "u-tom").Status);
        Assert.Empty(RowOf(await PreviewInvAsync(), "u-tom").Blockers);
    }

    [Fact]
    public async Task 预览_拦截项_未绑定_分店未锁定_类别未映射_无时薪_分店未映射()
    {
        await SeedMainScenarioAsync();
        var dto = await PreviewInvAsync();

        var priya = RowOf(dto, "u-priya");
        Assert.Equal(MyobPreviewStatuses.Blocked, priya.Status);
        Assert.Equal([MyobBlockerCodes.NotBound], priya.Blockers.Select(item => item.Code).ToArray());

        // 分店未锁定 + 未映射类别 + 没时薪 + 未映射的分店
        await SeedStoreAsync("1042", "TestStore", Inv, locked: false,
            Emp("u-liam", "Liam", Rate("Transfer"), [Work("2026-10-26", PayrollBucket.Weekday, 12)]),
            Emp("u-ned", "Ned", null, [Work("2026-10-26", PayrollBucket.Weekday, 5)]));
        await SeedStoreAsync("1099", "Unmapped", null, locked: true,
            Emp("u-uma", "Uma", Rate("Transfer"), [Work("2026-10-27", PayrollBucket.Weekday, 6)]));
        await SeedUserAsync("u-liam", "Liam", "1042");
        await SeedUserAsync("u-ned", "Ned", "1042");
        await SeedUserAsync("u-uma", "Uma", "1014", "1099");
        foreach (var user in new[] { "u-liam", "u-ned", "u-uma" })
        {
            await BindAsync(user);
        }

        _source.Pairs.AddRange(new[] { ("u-liam", "1042"), ("u-ned", "1042"), ("u-uma", "1099") });
        // 让 Uma 在 1014 也有一点工时，才会进入 Investment 名单且带出 1099 的数据
        _source.Stores["1014"] = _source.Stores["1014"] with
        {
            Employees = _source.Stores["1014"].Employees
                .Append(Emp("u-uma", "Uma", Rate("Transfer"), [Work("2026-10-28", PayrollBucket.Weekday, 2)])).ToList(),
        };
        _source.Pairs.Add(("u-uma", "1014"));
        await _db.Updateable<MyobPayCategoryMapping>().SetColumns(item => item.SaturdayUid == null)
            .Where(item => item.ConnectionGuid == Inv).ExecuteCommandAsync();

        dto = await PreviewInvAsync();
        Assert.Equal([MyobBlockerCodes.StoreNotLocked], RowOf(dto, "u-liam").Blockers.Select(item => item.Code).ToArray());
        Assert.Equal("1042", RowOf(dto, "u-liam").Blockers[0].Ref);
        Assert.Contains(RowOf(dto, "u-ned").Blockers, item => item.Code == MyobBlockerCodes.NoRate);
        Assert.Equal(MyobPreviewStatuses.Blocked, RowOf(dto, "u-ned").Status);
        Assert.Contains(RowOf(dto, "u-uma").Blockers, item => item.Code == MyobBlockerCodes.StoreNotMapped && item.Ref == "1099");
        var mei = RowOf(dto, "u-mei");
        Assert.Contains(mei.Blockers, item => item.Code == MyobBlockerCodes.CategoryNotMapped && item.Ref == MyobPayCategoryKeys.Saturday);
        Assert.Equal(MyobPreviewStatuses.Blocked, mei.Status);

        // 就绪卡
        // 已锁定分店 = 本公司员工涉及的所有分店里已锁定的（含他们在其他公司的分店：跨公司分摊依赖它们）；1042 是唯一未锁定的。
        Assert.Contains("1014", dto.Readiness.LockedStores);
        Assert.DoesNotContain("1042", dto.Readiness.LockedStores);
        var unlocked = Assert.Single(dto.Readiness.UnlockedStores);
        Assert.Equal("1042", unlocked.StoreCode);
        Assert.Equal(2, unlocked.EmployeeCount);
        Assert.Equal(4, dto.Readiness.CategoryMappedCount);
        Assert.False(dto.Readiness.CategoryComplete);
    }

    [Fact]
    public async Task 预览_汇总与排序_可推送在前_被拦截其次_不推送最后()
    {
        await SeedMainScenarioAsync();
        var dto = await PreviewInvAsync();
        Assert.Equal(
            [MyobPreviewStatuses.Ready, MyobPreviewStatuses.Ready, MyobPreviewStatuses.Ready, MyobPreviewStatuses.Blocked, MyobPreviewStatuses.NoTransfer],
            dto.Rows.Select(row => row.Status).ToArray());
        Assert.Equal(3, dto.Totals.ReadyCount);
        Assert.Equal(34m + 16m + 15m, dto.Totals.ReadyHours);
        Assert.Equal(1, dto.Totals.BlockedCount);
        Assert.Equal(1, dto.Totals.NoTransferCount);
    }

    [Fact]
    public async Task 预览_店长只能看自己可管理的分店_其余分店数据不完整的员工被拦截()
    {
        await SeedMainScenarioAsync();
        // 只能管理 1013：Jason 的 1013 数据可见，但 1025（Kenmore）不可见 → 他的分摊不完整，必须拦截而不是悄悄算错
        _scope.Scope = new CurrentUserManageableStoreScope { IsAllowed = true, IsAdmin = false, StoreCodes = ["1013", "1014"] };
        var dto = await PreviewInvAsync();
        var jason = RowOf(dto, "u-jason");
        Assert.Contains(jason.Blockers, item => item.Code == MyobBlockerCodes.NoStoreAccess && item.Ref == "1025");
        Assert.Equal(MyobPreviewStatuses.Blocked, jason.Status);
        Assert.Equal(MyobPreviewStatuses.Ready, RowOf(dto, "u-mei").Status);

        _scope.Scope = new CurrentUserManageableStoreScope { IsAllowed = false, Message = "无权限" };
        Assert.Equal(MyobTimesheetErrorCodes.Forbidden,
            (await Preview().PreviewAsync(Inv, null, false, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task 预览_提示项_年假折现_公假价不一致_生日换档_低于最低时薪()
    {
        await SeedMainScenarioAsync();
        await SeedStoreAsync("1030", "Hints", Inv, locked: true,
            // 年假 7.6h + 折现 8h；全转账所以折现也「转账」但不推送
            Emp("u-yuki", "Yuki", Rate("Transfer"),
                [Work("2026-10-26", PayrollBucket.Weekday, 8)], annual: [Leave("2026-10-27", 7.6)], cashOut: [Leave("2026-10-28", 8)]),
            // 公假上班 + 公假价（60）与平日价（30）不一致
            Emp("u-hol", "Hol", Rate("Transfer", weekday: 30m, holiday: 60m),
                [Work("2026-10-27", PayrollBucket.PublicHoliday, 8)]),
            // 未成年：生日 2026-10-20 满 17 岁，时薪记录 2026-07-01 生效（早于生日）→ 提示改时薪
            Emp("u-teen", "Teen", Rate("Transfer"), [Work("2026-10-26", PayrollBucket.Weekday, 5)],
                birthday: new DateTime(2009, 10, 20), age: 17),
            // 最低时薪 35 > 平日价 30
            Emp("u-low", "Low", Rate("Transfer", minimum: 35m), [Work("2026-10-26", PayrollBucket.Weekday, 5)]));
        foreach (var (user, name) in new[] { ("u-yuki", "Yuki"), ("u-hol", "Hol"), ("u-teen", "Teen"), ("u-low", "Low") })
        {
            await SeedUserAsync(user, name, "1030");
            await BindAsync(user);
            _source.Pairs.Add((user, "1030"));
        }

        var dto = await PreviewInvAsync();
        string[] CodesOf(string user) => dto.Hints.Where(hint => hint.UserGuid == user).Select(hint => hint.Code).ToArray();

        Assert.Equal([MyobHintCodes.AnnualCashOutNotPushed], CodesOf("u-yuki"));
        Assert.Contains("8.00 h", dto.Hints.Single(hint => hint.UserGuid == "u-yuki").Message, StringComparison.Ordinal);
        Assert.Equal(8m, RowOf(dto, "u-yuki").CashOutHours);
        Assert.Equal(7.6m, RowOf(dto, "u-yuki").Pushed.HolidayLeave);
        Assert.Equal(8m, RowOf(dto, "u-yuki").Pushed.Weekday);
        Assert.Equal(8m, RowOf(dto, "u-yuki").Detail.AnnualCashOutTransferHours); // 全转账：折现占用 8h 额度但不推送
        Assert.True(RowOf(dto, "u-yuki").Detail.CheckOk, RowOf(dto, "u-yuki").Detail.CheckMessage);
        Assert.Equal([MyobHintCodes.PublicHolidayRateMismatch], CodesOf("u-hol"));
        Assert.Contains(MyobHintCodes.BirthdayReview, CodesOf("u-teen"));
        Assert.Contains("2026-10-20", dto.Hints.Single(hint => hint.Code == MyobHintCodes.BirthdayReview).Message, StringComparison.Ordinal);
        Assert.Contains(MyobHintCodes.BelowMinimumRate, CodesOf("u-low"));
        Assert.Empty(CodesOf("u-mei"));
        Assert.Contains(MyobHintCodes.AnnualCashOutNotPushed, RowOf(dto, "u-yuki").HintCodes);
    }

    [Fact]
    public async Task 预览_检查MYOB现状_已发薪拦截_已有记录标出_读取失败不拦截只标记_默认不读MYOB()
    {
        await SeedMainScenarioAsync();
        await PreviewInvAsync();
        Assert.Equal(0, _api.TimesheetCalls); // 默认不读 MYOB

        _api.Timesheets["uid-u-mei"] = new MyobTimesheetStatus(true, true);
        _api.Timesheets["uid-u-sarah"] = new MyobTimesheetStatus(true, false);
        _api.Timesheets["uid-u-jason"] = new MyobTimesheetStatus(false, false);
        var dto = await PreviewInvAsync(checkMyob: true);

        Assert.True(dto.Readiness.MyobChecked);
        Assert.Equal(1, dto.Readiness.MyobProcessedCount);
        var mei = RowOf(dto, "u-mei");
        Assert.Equal(MyobTimesheetStates.Processed, mei.MyobState);
        Assert.Contains(mei.Blockers, item => item.Code == MyobBlockerCodes.MyobProcessed);
        Assert.Equal(MyobPreviewStatuses.Blocked, mei.Status);
        Assert.Equal(MyobTimesheetStates.HasEntries, RowOf(dto, "u-sarah").MyobState);
        Assert.Equal(MyobPreviewStatuses.Ready, RowOf(dto, "u-sarah").Status);
        Assert.Equal(MyobTimesheetStates.Clear, RowOf(dto, "u-jason").MyobState);
        Assert.Equal(MyobTimesheetStates.Unknown, RowOf(dto, "u-tom").MyobState); // 没有可推送工时的不读
        Assert.Equal("u-mei", mei.MyobName); // 绑定时记下的 MYOB 姓名快照（测试里用员工标识）

        _api.TimesheetError = new MyobApiException(MyobErrorCodes.ApiCallFailed, "MYOB Timesheet 接口返回 403");
        var failed = await PreviewInvAsync(checkMyob: true);
        Assert.True(failed.Readiness.MyobCheckFailedCount >= 3);
        Assert.Equal(MyobTimesheetStates.CheckFailed, RowOf(failed, "u-sarah").MyobState);
        Assert.Equal(MyobPreviewStatuses.Ready, RowOf(failed, "u-sarah").Status); // 读失败只标记，不当成已发薪
    }

    [Fact]
    public async Task 预览_PushEnabled只跟配置_默认关闭()
    {
        await SeedMainScenarioAsync();
        Assert.False((await PreviewInvAsync()).PushEnabled);
        var on = await Preview(new MyobOptions { TimesheetPushEnabled = true }).PreviewAsync(Inv, null, false, CancellationToken.None);
        Assert.True(on.Data!.PushEnabled);
    }

    [Fact]
    public async Task 预览_另一家公司的员工不出现在本公司预览里_Australian视角只看Kenmore的份额()
    {
        await SeedMainScenarioAsync();
        await BindAsync("u-jason", Aus);
        await SaveCompleteMappingAsync(Aus);
        var result = await Preview().PreviewAsync(Aus, null, false, CancellationToken.None);
        var dto = result.Data!;
        Assert.Equal(["u-jason"], dto.Rows.Select(row => row.UserGuid).ToArray());
        var jason = dto.Rows[0];
        Assert.Equal(10m, jason.ActualHours);
        Assert.Equal(5m, jason.AllocatedTransferHours);
        Assert.Equal(5m, jason.PushedTotal);
        Assert.Equal(["1025 Kenmore"], jason.StoreLabels);
        Assert.Equal(MyobPreviewStatuses.Ready, jason.Status);
    }

    // ───────────────────────── 年假核定按天分配 ─────────────────────────

    [Fact]
    public void 年假核定按天分配_从早到晚_不超过当天申请_核定为0没有项()
    {
        var requested = new SortedDictionary<DateTime, decimal>
        {
            [new DateTime(2026, 10, 27)] = 8m,
            [new DateTime(2026, 10, 28)] = 8m,
            [new DateTime(2026, 10, 29)] = 8m,
        };
        var items = BlazorApp.Api.Services.React.AttendanceReactService.DistributeApproved(requested, 12m);
        Assert.Equal([8m, 4m], items.Select(item => item.Hours).ToArray());
        Assert.Equal([new DateTime(2026, 10, 27), new DateTime(2026, 10, 28)], items.Select(item => item.Date).ToArray());
        Assert.Empty(BlazorApp.Api.Services.React.AttendanceReactService.DistributeApproved(requested, 0m));
        Assert.Equal(24m, BlazorApp.Api.Services.React.AttendanceReactService.DistributeApproved(requested, 99m).Sum(item => item.Hours));
    }

    // ───────────────────────── 控制器权限 ─────────────────────────

    [Fact]
    public void 控制器权限_类别映射要求MyobConnectionManage_预览要求PayrollEdit_无匿名放行()
    {
        Assert.Equal(Permissions.Myob.Connection.Manage, typeof(MyobPayCategoryController).GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal(Permissions.Attendance.Payroll.Edit, typeof(MyobTimesheetController).GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal("api/react/v1/myob/pay-categories", typeof(MyobPayCategoryController).GetCustomAttribute<Microsoft.AspNetCore.Mvc.RouteAttribute>()!.Template);
        Assert.Equal("api/react/v1/myob/timesheet", typeof(MyobTimesheetController).GetCustomAttribute<Microsoft.AspNetCore.Mvc.RouteAttribute>()!.Template);
        Assert.Empty(typeof(MyobPayCategoryController).GetMethods().Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null));
        Assert.Empty(typeof(MyobTimesheetController).GetMethods().Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null));
    }

    // ───────────────────────── 测试替身 ─────────────────────────

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FakeApi : IMyobApiClient
    {
        public IReadOnlyList<MyobWageCategoryRecord> Categories { get; set; } = [];
        public MyobApiException? CategoriesError { get; set; }
        public int CategoryCalls { get; private set; }
        public Dictionary<string, MyobTimesheetStatus> Timesheets { get; } = new();
        public MyobApiException? TimesheetError { get; set; }
        public int TimesheetCalls { get; private set; }

        public Task<IReadOnlyList<MyobWageCategoryRecord>> GetWageCategoriesAsync(string businessId, string accessToken, CancellationToken cancellationToken)
        {
            CategoryCalls++;
            return CategoriesError is null ? Task.FromResult(Categories) : Task.FromException<IReadOnlyList<MyobWageCategoryRecord>>(CategoriesError);
        }

        public Task<MyobTimesheetStatus> GetTimesheetStatusAsync(
            string businessId, string accessToken, string employeeUid, DateTime startDate, DateTime endDate, CancellationToken cancellationToken)
        {
            TimesheetCalls++;
            if (TimesheetError is not null)
            {
                return Task.FromException<MyobTimesheetStatus>(TimesheetError);
            }

            return Task.FromResult(Timesheets.GetValueOrDefault(employeeUid, new MyobTimesheetStatus(false, false)));
        }

        public Task<MyobTokenResponse> ExchangeCodeAsync(string code, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MyobTokenResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MyobCompanyInfo> GetCompanyAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MyobEmployeeRecord>> GetEmployeesAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MyobEmployeePayrollRecord>> GetEmployeePayrollDetailsAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MyobEmployeePaymentRecord>> GetEmployeePaymentDetailsAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeTokens : IMyobTokenProvider
    {
        public Task<string> GetAccessTokenAsync(string connectionGuid, CancellationToken cancellationToken) => Task.FromResult("access-token");
        public void Invalidate(string connectionGuid) { }
    }

    private sealed class FakeSource : IAttendancePayrollPushSource
    {
        public Dictionary<string, PayrollPushStoreData> Stores { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string UserGuid, string StoreCode)> Pairs { get; } = [];
        public int Weeks { get; set; } = 1;

        public Task<PayrollPushSettings> GetSettingsAsync() => Task.FromResult(new PayrollPushSettings(Weeks, null));

        public Task<IReadOnlyList<(string UserGuid, string StoreCode)>> FindDataPairsAsync(
            IReadOnlyCollection<string> userGuids, DateTime periodStart, int weeks) =>
            Task.FromResult<IReadOnlyList<(string, string)>>(
                Pairs.Where(pair => userGuids.Contains(pair.UserGuid, StringComparer.OrdinalIgnoreCase)).ToList());

        public Task<PayrollPushStoreData> GetStoreDataAsync(
            string storeCode, DateTime periodStart, int weeks, IReadOnlyCollection<string> onlyUsers)
        {
            var data = Stores[storeCode];
            return Task.FromResult(data with
            {
                Employees = data.Employees.Where(item => onlyUsers.Contains(item.UserGuid, StringComparer.OrdinalIgnoreCase)).ToList(),
            });
        }
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
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
