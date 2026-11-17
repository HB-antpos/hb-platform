using System.Text.Json;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 从 MYOB 同步转账时薪（docs/plans/2026-11-16-myob-hourly-rate-sync.md）：
/// 规则层（纯函数：判定 / 取整 / 倍数推算 / 默认勾选 / 哈希）与服务层（预览、写入、哈希拦截、失败不连坐、隐私）。
/// </summary>
public sealed class MyobPayRateSyncTests : IDisposable
{
    private const string Conn = "conn-1";
    private const string Biz = "11111111-2222-4333-8444-555555555555";
    private const string Store = "BRI";

    private static readonly DateTime Today = new(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hb-myob-ratesync-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;

    public MyobPayRateSyncTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_dbPath}",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(typeof(MyobConnection), typeof(EmployeeMyobLink), typeof(AttendancePayPeriod));
        _db.Insertable(new MyobConnection
        {
            ConnectionGuid = Conn, BusinessId = Biz, BusinessName = "Investment",
            Status = MyobConnectionStatuses.Active, AuthorizedAt = Today, CreatedAt = Today,
        }).ExecuteCommand();
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    // ───────────────────────── 规则层（纯函数） ─────────────────────────

    private static PayRateSyncMultipleInput Mult(string uid, string? payRate, decimal? multiplier, bool onCard = true) =>
        new(uid, new MyobWageCategoryRecord(uid, "cat", "Hourly", payRate, multiplier), onCard);

    private static readonly PayRateSyncMultipleInput NoMap = new(null, null, false);
    private static readonly PayRateSyncOur NoRecord = new(false, null, null, null, null, null);

    private static PayRateSyncResult Compute(
        PayRateSyncOur our,
        string? payBasis = "Hourly",
        decimal? hourly = 28.4m,
        DateTime? terminated = null,
        PayRateSyncMultipleInput? saturday = null,
        PayRateSyncMultipleInput? sunday = null,
        PayRateSyncMultipleInput? holiday = null) =>
        MyobPayRateSyncRules.Compute(new PayRateSyncInput(
            our, new PayRateSyncMyob(payBasis, hourly, terminated),
            saturday ?? NoMap, sunday ?? NoMap, holiday ?? NoMap, Today));

    [Fact]
    public void Rules_NoRecord_IsCreate_WeekdayAndHolidayDefaultSelected_WeekendNot()
    {
        var result = Compute(
            NoRecord,
            saturday: Mult("sat", "RegularRate", 1.25m),
            sunday: Mult("sun", "RegularRate", 1.5m),
            holiday: Mult("ph", "RegularRate", 2.25m));

        Assert.Equal(MyobPayRateSyncKinds.Create, result.Kind);
        Assert.Null(result.SkipReason);
        Assert.Equal(28.4m, result.Weekday.Suggested);
        Assert.True(result.Weekday.CanApply);
        Assert.True(result.Weekday.DefaultSelected);

        // 周末建议值默认不勾（用户决定）；公假价默认随平日价勾上（总方案 3.4）。
        Assert.Equal(35.5m, result.Saturday.Suggested);
        Assert.True(result.Saturday.CanApply);
        Assert.False(result.Saturday.DefaultSelected);
        Assert.Equal(42.6m, result.Sunday.Suggested);
        Assert.False(result.Sunday.DefaultSelected);
        Assert.Equal(63.9m, result.PublicHoliday.Suggested);
        Assert.Equal(2.25m, result.PublicHoliday.Multiplier);
        Assert.True(result.PublicHoliday.DefaultSelected);
    }

    [Fact]
    public void Rules_FourDecimalRate_IsRoundedFirst_ThenMultiplied()
    {
        // 28.4567 → 28.46；公假 = 28.46 × 2.25 = 64.035 → 64.04（先取整基础价再乘倍数，与我方 2 位小数口径一致）。
        var result = Compute(NoRecord, hourly: 28.4567m, holiday: Mult("ph", "RegularRate", 2.25m));

        Assert.True(result.Rounded);
        Assert.Equal(28.4567m, result.MyobHourlyRate);
        Assert.Equal(28.46m, result.Weekday.Suggested);
        Assert.Equal(64.04m, result.PublicHoliday.Suggested);
        Assert.False(Compute(NoRecord, hourly: 28.4m).Rounded);
    }

    [Fact]
    public void Rules_Fill_WhenOurWeekdayIsEmpty()
    {
        var result = Compute(new PayRateSyncOur(true, "Transfer", null, null, null, null));

        Assert.Equal(MyobPayRateSyncKinds.Fill, result.Kind);
        Assert.True(result.Weekday.DefaultSelected);
    }

    [Fact]
    public void Rules_Differ_IsNotDefaultSelected_AndHolidayFollowsIt()
    {
        // 平日价不同 → 覆盖默认不勾；此时公假价（即使等于旧平日价）也不默认勾，避免只改公假、平日价还是旧值。
        var result = Compute(
            new PayRateSyncOur(true, "Transfer", 25m, null, null, 25m),
            holiday: Mult("ph", "RegularRate", 2.25m));

        Assert.Equal(MyobPayRateSyncKinds.Differ, result.Kind);
        Assert.True(result.Weekday.CanApply);
        Assert.False(result.Weekday.DefaultSelected);
        Assert.True(result.PublicHoliday.CanApply);
        Assert.False(result.PublicHoliday.DefaultSelected);
    }

    [Fact]
    public void Rules_Same_WeekdayHasNothingToWrite_HolidayStillSuggested()
    {
        // 旧规则下存量记录的公假价 = 平日价：升级后 MYOB 说该是 ×2.25，默认勾上替换。
        var oldRuleRow = Compute(
            new PayRateSyncOur(true, "Transfer", 28.4m, null, null, 28.4m),
            holiday: Mult("ph", "RegularRate", 2.25m));
        Assert.Equal(MyobPayRateSyncKinds.Same, oldRuleRow.Kind);
        Assert.False(oldRuleRow.Weekday.CanApply);
        Assert.True(oldRuleRow.PublicHoliday.CanApply);
        Assert.True(oldRuleRow.PublicHoliday.DefaultSelected);

        // 财务手动设过的公假价（与平日价不同、也不是建议值）：有建议值但不默认勾，不覆盖手动值。
        var manual = Compute(
            new PayRateSyncOur(true, "Transfer", 28.4m, null, null, 70m),
            holiday: Mult("ph", "RegularRate", 2.25m));
        Assert.True(manual.PublicHoliday.CanApply);
        Assert.False(manual.PublicHoliday.DefaultSelected);

        // 已经等于建议值：没有可写内容。
        var done = Compute(
            new PayRateSyncOur(true, "Transfer", 28.4m, null, null, 63.9m),
            holiday: Mult("ph", "RegularRate", 2.25m));
        Assert.False(done.PublicHoliday.CanApply);
    }

    [Fact]
    public void Rules_Skip_SalaryBasis_EvenWithNonZeroConvertedRate_AndDoesNotCarryTheRate()
    {
        // 年薪制卡可能带非零折算时薪（Australian 实测）：必须看 PayBasis，且不把折算值带出去。
        var result = Compute(NoRecord, payBasis: "Salary", hourly: 79.6105m);

        Assert.Equal(MyobPayRateSyncKinds.Skip, result.Kind);
        Assert.Equal(MyobPayRateSyncReasons.NotHourly, result.SkipReason);
        Assert.Null(result.MyobHourlyRate);
        Assert.False(result.Weekday.CanApply);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rules_Skip_HourlyBasisWithoutPositiveRate(int rate)
    {
        var result = Compute(NoRecord, hourly: rate);
        Assert.Equal(MyobPayRateSyncReasons.NotHourly, result.SkipReason);
        Assert.Equal(MyobPayRateSyncReasons.NotHourly, Compute(NoRecord, hourly: null).SkipReason);
    }

    [Fact]
    public void Rules_Skip_TerminatedOnlyWhenDateReached_AndBeatsOtherReasons()
    {
        Assert.Equal(MyobPayRateSyncReasons.Terminated, Compute(NoRecord, terminated: Today.AddDays(-30)).SkipReason);
        Assert.Equal(MyobPayRateSyncReasons.Terminated, Compute(NoRecord, terminated: Today).SkipReason);
        Assert.Equal(MyobPayRateSyncKinds.Create, Compute(NoRecord, terminated: Today.AddDays(1)).Kind);

        // 离职优先于「非时薪制」。
        Assert.Equal(MyobPayRateSyncReasons.Terminated, Compute(NoRecord, payBasis: "Salary", terminated: Today.AddDays(-1)).SkipReason);
    }

    [Fact]
    public void Rules_Skip_CashOnlyEmployee_ButNotMixed()
    {
        var cash = Compute(new PayRateSyncOur(true, "Cash", null, null, null, null));
        Assert.Equal(MyobPayRateSyncReasons.PayModeCash, cash.SkipReason);

        Assert.Equal(MyobPayRateSyncKinds.Fill, Compute(new PayRateSyncOur(true, "Mixed", null, null, null, null)).Kind);
    }

    [Fact]
    public void Rules_Multiplier_UnavailableReasons()
    {
        Assert.Equal(MyobPayRateSyncReasons.CategoryNotMapped, MyobPayRateSyncRules.ResolveMultiplier(NoMap).Reason);
        Assert.Equal(MyobPayRateSyncReasons.CategoryNotMapped,
            MyobPayRateSyncRules.ResolveMultiplier(new PayRateSyncMultipleInput("  ", null, false)).Reason);
        Assert.Equal(MyobPayRateSyncReasons.CategoryNotFound,
            MyobPayRateSyncRules.ResolveMultiplier(new PayRateSyncMultipleInput("gone", null, true)).Reason);
        Assert.Equal(MyobPayRateSyncReasons.CategoryNotOnCard,
            MyobPayRateSyncRules.ResolveMultiplier(Mult("sat", "RegularRate", 1.25m, onCard: false)).Reason);
        Assert.Equal(MyobPayRateSyncReasons.CategoryNotRateMultiple,
            MyobPayRateSyncRules.ResolveMultiplier(Mult("sat", "FixedHourly", null)).Reason);
        Assert.Equal(MyobPayRateSyncReasons.CategoryNotRateMultiple,
            MyobPayRateSyncRules.ResolveMultiplier(Mult("sat", "RegularRate", null)).Reason);
        Assert.Equal(MyobPayRateSyncReasons.CategoryNotRateMultiple,
            MyobPayRateSyncRules.ResolveMultiplier(Mult("sat", "RegularRate", 0m)).Reason);

        var ok = MyobPayRateSyncRules.ResolveMultiplier(Mult("sat", "regularrate", 1.2m));
        Assert.Equal(1.2m, ok.Multiplier);
        Assert.Null(ok.Reason);
    }

    [Fact]
    public void Rules_UnavailableCell_HasReasonAndCannotApply()
    {
        var result = Compute(NoRecord, saturday: Mult("sat", "FixedHourly", null), sunday: NoMap);

        Assert.Null(result.Saturday.Suggested);
        Assert.False(result.Saturday.CanApply);
        Assert.False(result.Saturday.DefaultSelected);
        Assert.Equal(MyobPayRateSyncReasons.CategoryNotRateMultiple, result.Saturday.UnavailableReason);
        Assert.Equal(MyobPayRateSyncReasons.CategoryNotMapped, result.Sunday.UnavailableReason);
        // 平日价不受影响。
        Assert.True(result.Weekday.CanApply);
    }

    [Fact]
    public void Rules_Hash_ChangesWithAnyInput_AndIsStable()
    {
        var our = new PayRateSyncOur(true, "Transfer", 25m, 30m, 35m, 50m);
        var baseHash = MyobPayRateSyncRules.Hash(our, 28.4m, 35.5m, 42.6m, 63.9m);

        Assert.Equal(baseHash, MyobPayRateSyncRules.Hash(our, 28.4m, 35.5m, 42.6m, 63.9m));
        Assert.NotEqual(baseHash, MyobPayRateSyncRules.Hash(our with { Weekday = 26m }, 28.4m, 35.5m, 42.6m, 63.9m));
        Assert.NotEqual(baseHash, MyobPayRateSyncRules.Hash(our with { PublicHoliday = null }, 28.4m, 35.5m, 42.6m, 63.9m));
        Assert.NotEqual(baseHash, MyobPayRateSyncRules.Hash(our, 28.5m, 35.5m, 42.6m, 63.9m));
        Assert.NotEqual(baseHash, MyobPayRateSyncRules.Hash(our, 28.4m, 35.6m, 42.6m, 63.9m));
        Assert.NotEqual(baseHash, MyobPayRateSyncRules.Hash(our, 28.4m, 35.5m, null, 63.9m));
        Assert.NotEqual(baseHash, MyobPayRateSyncRules.Hash(our, 28.4m, 35.5m, 42.6m, 64m));
        Assert.Equal(16, baseHash.Length);
    }

    // ───────────────────────── 服务层 ─────────────────────────

    private const string Mapped = "cat-";

    private static AttendancePayRateDto OurRow(
        string userGuid, string name, string? employmentType = "FullTime", string? payMode = "Transfer",
        decimal? weekday = 25m, decimal? saturday = null, decimal? sunday = null, decimal? holiday = null,
        bool hasRecord = true) => new()
    {
        UserGuid = userGuid,
        UserName = name,
        EmploymentType = employmentType,
        RateGuid = hasRecord ? $"rate-{userGuid}" : null,
        PayMode = hasRecord ? payMode : null,
        FixedTransferHoursPerWeek = payMode == "Mixed" ? 12m : 0m,
        TransferWeekdayRate = hasRecord ? weekday : null,
        TransferSaturdayRate = hasRecord ? saturday : null,
        TransferSundayRate = hasRecord ? sunday : null,
        TransferPublicHolidayRate = hasRecord ? holiday : null,
        CashRate = hasRecord && payMode != "Transfer" ? 22m : null,
        CashAllowancePerPeriod = hasRecord ? 5m : 0m,
        MinimumRate = hasRecord ? 24m : null,
        EffectiveFrom = hasRecord ? new DateTime(2026, 7, 1) : null,
    };

    private static MyobEmployeePayrollRecord Card(
        string uid, decimal? hourly, string payBasis = "Hourly", string[]? categories = null, DateTime? terminated = null) =>
        new(uid, null, "Employee", [], PayBasis: payBasis, HourlyRate: hourly,
            WageCategoryUids: categories ?? [$"{Mapped}sat", $"{Mapped}sun", $"{Mapped}ph", $"{Mapped}satc", $"{Mapped}sunc", $"{Mapped}phc"],
            TerminationDate: terminated);

    private static List<MyobWageCategoryRecord> Categories() =>
    [
        new($"{Mapped}sat", "SATURDAY (x1.25)", "Hourly", "RegularRate", 1.25m),
        new($"{Mapped}sun", "SUNDAY (x1.5)", "Hourly", "RegularRate", 1.5m),
        new($"{Mapped}ph", "PUBLIC HOLIDAY (x2.25)", "Hourly", "RegularRate", 2.25m),
        new($"{Mapped}satc", "SATUDAY (x 1.2)", "Hourly", "RegularRate", 1.2m),
        new($"{Mapped}sunc", "SUNDAY (x1.4)", "Hourly", "RegularRate", 1.4m),
        new($"{Mapped}phc", "PUBLIC HOLIDAY (x2)", "Hourly", "RegularRate", 2m),
    ];

    private static MyobPayCategoryMappingDto Mapping() => new()
    {
        SaturdayUid = $"{Mapped}sat", SundayUid = $"{Mapped}sun", PublicHolidayUid = $"{Mapped}ph",
        SaturdayCasualUid = $"{Mapped}satc", SundayCasualUid = $"{Mapped}sunc", PublicHolidayCasualUid = $"{Mapped}phc",
    };

    private sealed class Harness
    {
        public Mock<IAttendanceReactService> Attendance { get; } = new(MockBehavior.Strict);
        public Mock<IMyobPayCategoryService> Categories { get; } = new(MockBehavior.Strict);
        public Mock<IMyobApiClient> Client { get; } = new(MockBehavior.Strict);
        public FakeWages Wages { get; } = new();
        public List<CreateEmployeePayRateDto> Created { get; } = [];
        public HashSet<string> RejectUsers { get; } = [];
        public MyobPayRateSyncService Service { get; set; } = null!;
    }

    private Harness NewHarness(
        IEnumerable<AttendancePayRateDto> ours,
        Dictionary<string, string> links,
        Dictionary<string, MyobEmployeePayrollRecord> cards,
        MyobPayCategoryMappingDto? mapping = null)
    {
        var h = new Harness();
        h.Attendance
            .Setup(x => x.GetPayRatesAsync(It.Is<AttendancePayRateQueryDto>(q => q.StoreCode == Store)))
            .ReturnsAsync(ApiResponse<List<AttendancePayRateDto>>.OK(ours.ToList()));
        h.Attendance
            .Setup(x => x.CreatePayRateAsync(It.IsAny<CreateEmployeePayRateDto>()))
            .ReturnsAsync((CreateEmployeePayRateDto request) =>
            {
                h.Created.Add(request);
                return h.RejectUsers.Contains(request.UserGuid)
                    ? ApiResponse<AttendancePayRateDto>.Error("该周期已锁定", "PERIOD_LOCKED")
                    : ApiResponse<AttendancePayRateDto>.OK(new AttendancePayRateDto { RateGuid = $"new-{request.UserGuid}" });
            });
        h.Categories
            .Setup(x => x.GetSavedMappingAsync(Conn))
            .ReturnsAsync(mapping ?? Mapping());
        h.Client
            .Setup(x => x.GetWageCategoriesAsync(Biz, "access-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Categories());
        foreach (var (uid, record) in cards)
        {
            h.Wages.Cards[uid] = record;
        }

        foreach (var (user, uid) in links)
        {
            _db.Insertable(new EmployeeMyobLink
            {
                UserGuid = user, ConnectionGuid = Conn, MyobEmployeeUid = uid, BoundAt = Today,
            }).ExecuteCommand();
        }

        h.Service = new MyobPayRateSyncService(
            _db, h.Attendance.Object, h.Wages, h.Categories.Object, h.Client.Object, new FakeTokens(), new FixedTime(Today.AddHours(3)));
        return h;
    }

    private static MyobPayRateSyncPreviewRequest PreviewRequest(DateTime? effectiveFrom = null) =>
        new() { ConnectionGuid = Conn, StoreCode = Store, EffectiveFrom = effectiveFrom };

    [Fact]
    public async Task Preview_RowsOnlyForBoundEmployees_WithKindsAndCasualMapping()
    {
        var h = NewHarness(
            [
                OurRow("u-ft", "Aidan", weekday: 28.4m, holiday: 28.4m),
                OurRow("u-cas", "Cara", employmentType: "Casual", hasRecord: false),
                OurRow("u-sal", "Bella"),
                OurRow("u-none", "Nobody"),
            ],
            new() { ["u-ft"] = "m-ft", ["u-cas"] = "m-cas", ["u-sal"] = "m-sal" },
            new()
            {
                ["m-ft"] = Card("m-ft", 28.4m),
                ["m-cas"] = Card("m-cas", 20m),
                ["m-sal"] = Card("m-sal", 79.6105m, payBasis: "Salary"),
            });

        var result = await h.Service.PreviewAsync(PreviewRequest(new DateTime(2026, 11, 23)), CancellationToken.None);

        Assert.True(result.Success, result.Message);
        var dto = result.Data!;
        Assert.Equal(1, dto.NotBoundCount);
        Assert.Equal(new DateTime(2026, 11, 23), dto.EffectiveFrom);
        Assert.Equal("Investment", dto.ConnectionName);
        Assert.Equal(["u-ft", "u-cas", "u-sal"], dto.Rows.Select(row => row.UserGuid).ToArray());

        var ft = dto.Rows[0];
        Assert.Equal(MyobPayRateSyncKinds.Same, ft.Kind);
        Assert.Equal(35.5m, ft.Saturday.Suggested);
        Assert.Equal(63.9m, ft.PublicHoliday.Suggested);
        Assert.True(ft.PublicHoliday.DefaultSelected);

        // 临时工用临时工那一对类别：周六 ×1.2、周日 ×1.4、公假 ×2.0。
        var casual = dto.Rows[1];
        Assert.Equal(MyobPayRateSyncKinds.Create, casual.Kind);
        Assert.Equal(24m, casual.Saturday.Suggested);
        Assert.Equal(28m, casual.Sunday.Suggested);
        Assert.Equal(40m, casual.PublicHoliday.Suggested);

        var salary = dto.Rows[2];
        Assert.Equal(MyobPayRateSyncKinds.Skip, salary.Kind);
        Assert.Equal(MyobPayRateSyncReasons.NotHourly, salary.SkipReason);
        Assert.Null(salary.MyobHourlyRate);
    }

    [Fact]
    public async Task Preview_CasualWithoutCasualMapping_CannotSuggestWeekendOrHoliday()
    {
        var mapping = Mapping();
        mapping.SaturdayCasualUid = null;
        mapping.PublicHolidayCasualUid = null;
        var h = NewHarness(
            [OurRow("u-cas", "Cara", employmentType: "Casual", hasRecord: false)],
            new() { ["u-cas"] = "m-cas" },
            new() { ["m-cas"] = Card("m-cas", 20m) },
            mapping);

        var row = (await h.Service.PreviewAsync(PreviewRequest(Today), CancellationToken.None)).Data!.Rows.Single();

        Assert.Equal(MyobPayRateSyncReasons.CategoryNotMapped, row.Saturday.UnavailableReason);
        Assert.Equal(MyobPayRateSyncReasons.CategoryNotMapped, row.PublicHoliday.UnavailableReason);
        Assert.Equal(28m, row.Sunday.Suggested); // 周日临时工映射配了，不受影响
        Assert.True(row.Weekday.CanApply);
    }

    [Fact]
    public async Task Preview_CategoryNotOnCard_IsUnavailableForThatEmployee()
    {
        var h = NewHarness(
            [OurRow("u-ft", "Aidan")],
            new() { ["u-ft"] = "m-ft" },
            new() { ["m-ft"] = Card("m-ft", 28.4m, categories: [$"{Mapped}sat"]) });

        var row = (await h.Service.PreviewAsync(PreviewRequest(Today), CancellationToken.None)).Data!.Rows.Single();

        Assert.Equal(35.5m, row.Saturday.Suggested);
        Assert.Equal(MyobPayRateSyncReasons.CategoryNotOnCard, row.Sunday.UnavailableReason);
        Assert.Equal(MyobPayRateSyncReasons.CategoryNotOnCard, row.PublicHoliday.UnavailableReason);
    }

    [Fact]
    public async Task Preview_BoundButCardMissing_IsSkippedWithReason()
    {
        var h = NewHarness([OurRow("u-ft", "Aidan")], new() { ["u-ft"] = "m-gone" }, new());

        var row = (await h.Service.PreviewAsync(PreviewRequest(Today), CancellationToken.None)).Data!.Rows.Single();

        Assert.Equal(MyobPayRateSyncKinds.Skip, row.Kind);
        Assert.Equal(MyobPayRateSyncReasons.CardMissing, row.SkipReason);
    }

    [Fact]
    public async Task Preview_DefaultEffectiveFrom_IsDayAfterLastSubmittedPeriod_OrThisMonday()
    {
        var h = NewHarness([OurRow("u-ft", "Aidan")], new() { ["u-ft"] = "m-ft" }, new() { ["m-ft"] = Card("m-ft", 28.4m) });

        // 没有任何已提交周期：取本周周一（2026-11-17 是周二 → 11-16）。
        var none = await h.Service.PreviewAsync(PreviewRequest(), CancellationToken.None);
        Assert.Equal(new DateTime(2026, 11, 16), none.Data!.EffectiveFrom);

        // 最后一个已提交周期 11-02 起 2 周 → 11-16 起未锁定；未提交（Open）的周期不算。
        _db.Insertable(new AttendancePayPeriod { StoreCode = Store, PeriodStart = new DateTime(2026, 11, 2), Weeks = 2, Status = "Submitted" }).ExecuteCommand();
        _db.Insertable(new AttendancePayPeriod { StoreCode = Store, PeriodStart = new DateTime(2026, 11, 16), Weeks = 2, Status = "Open" }).ExecuteCommand();
        _db.Insertable(new AttendancePayPeriod { StoreCode = "OTHER", PeriodStart = new DateTime(2026, 12, 14), Weeks = 2, Status = "Submitted" }).ExecuteCommand();
        var locked = await h.Service.PreviewAsync(PreviewRequest(), CancellationToken.None);
        Assert.Equal(new DateTime(2026, 11, 16), locked.Data!.EffectiveFrom);
    }

    [Fact]
    public async Task Preview_PropagatesStoreAccessError_AndRejectsMissingArguments()
    {
        var h = NewHarness([], new(), new());
        h.Attendance
            .Setup(x => x.GetPayRatesAsync(It.Is<AttendancePayRateQueryDto>(q => q.StoreCode == "NOPE")))
            .ReturnsAsync(ApiResponse<List<AttendancePayRateDto>>.Error("没有权限", "FORBIDDEN_STORE"));

        var denied = await h.Service.PreviewAsync(new MyobPayRateSyncPreviewRequest { ConnectionGuid = Conn, StoreCode = "NOPE" }, CancellationToken.None);
        Assert.Equal("FORBIDDEN_STORE", denied.ErrorCode);

        Assert.Equal(MyobPayRateSyncErrorCodes.InvalidRequest,
            (await h.Service.PreviewAsync(new MyobPayRateSyncPreviewRequest { StoreCode = Store }, CancellationToken.None)).ErrorCode);
        Assert.Equal(MyobErrorCodes.ConnectionNotActive,
            (await h.Service.PreviewAsync(new MyobPayRateSyncPreviewRequest { ConnectionGuid = "nope", StoreCode = Store }, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task Preview_NeverContainsMyobUidsOrSalary()
    {
        var h = NewHarness(
            [OurRow("u-ft", "Aidan")],
            new() { ["u-ft"] = "myob-secret-uid" },
            new() { ["myob-secret-uid"] = Card("myob-secret-uid", 28.4m) });

        var json = JsonSerializer.Serialize((await h.Service.PreviewAsync(PreviewRequest(Today), CancellationToken.None)).Data);

        Assert.DoesNotContain("myob-secret-uid", json);
        Assert.DoesNotContain(Mapped, json); // 类别 UID 也不出现在响应里
        Assert.DoesNotContain("AnnualSalary", json, StringComparison.OrdinalIgnoreCase);
    }

    private static MyobPayRateSyncApplyRequest ApplyRequest(
        MyobPayRateSyncPreviewDto preview, params (string User, string[] Cells)[] items) => new()
    {
        ConnectionGuid = Conn,
        StoreCode = Store,
        EffectiveFrom = preview.EffectiveFrom,
        Items = items.Select(item => new MyobPayRateSyncApplyItem
        {
            UserGuid = item.User,
            Hash = preview.Rows.Single(row => row.UserGuid == item.User).Hash,
            Cells = item.Cells.ToList(),
        }).ToList(),
    };

    [Fact]
    public async Task Apply_CopiesExistingRecord_AndOnlyChangesSelectedCells()
    {
        // 转账 + 现金的员工：发放方式、固定转账工时、现金价、补贴、最低时薪原样带过来，只改勾选的平日价和公假价。
        var h = NewHarness(
            [OurRow("u-mix", "Mia", payMode: "Mixed", weekday: 25m, saturday: 31m, sunday: 37m, holiday: 25m)],
            new() { ["u-mix"] = "m-mix" },
            new() { ["m-mix"] = Card("m-mix", 28.4m) });
        var preview = (await h.Service.PreviewAsync(PreviewRequest(new DateTime(2026, 11, 23)), CancellationToken.None)).Data!;

        var result = await h.Service.ApplyAsync(
            ApplyRequest(preview, ("u-mix", [MyobPayRateSyncCells.Weekday, MyobPayRateSyncCells.PublicHoliday])), CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Empty(result.Data!.Failed);
        var written = Assert.Single(result.Data.Written);
        Assert.Equal("new-u-mix", written.RateGuid);

        var dto = Assert.Single(h.Created);
        Assert.Equal(Store, dto.StoreCode);
        Assert.Equal("Mixed", dto.PayMode);
        Assert.Equal(12m, dto.FixedTransferHoursPerWeek);
        Assert.Equal(28.4m, dto.TransferWeekdayRate);
        Assert.Equal(63.9m, dto.TransferPublicHolidayRate);
        Assert.Equal(31m, dto.TransferSaturdayRate); // 没勾：沿用旧值
        Assert.Equal(37m, dto.TransferSundayRate);
        Assert.Equal(22m, dto.CashRate);
        Assert.Equal(5m, dto.CashAllowancePerPeriod);
        Assert.Equal(24m, dto.MinimumRate);
        Assert.Equal(new DateTime(2026, 11, 23), dto.EffectiveFrom);
        Assert.Equal("来自 MYOB 同步 2026-11-17", dto.Remark);
    }

    [Fact]
    public async Task Apply_NewEmployee_IsCreatedAsTransfer_WithSelectedWeekendCells()
    {
        var h = NewHarness(
            [OurRow("u-new", "Nina", hasRecord: false)],
            new() { ["u-new"] = "m-new" },
            new() { ["m-new"] = Card("m-new", 28.4m) });
        var preview = (await h.Service.PreviewAsync(PreviewRequest(Today), CancellationToken.None)).Data!;

        var result = await h.Service.ApplyAsync(
            ApplyRequest(preview, ("u-new", [MyobPayRateSyncCells.Weekday, MyobPayRateSyncCells.Saturday])), CancellationToken.None);

        Assert.Empty(result.Data!.Failed);
        var dto = Assert.Single(h.Created);
        Assert.Equal("Transfer", dto.PayMode);
        Assert.Equal(0m, dto.FixedTransferHoursPerWeek);
        Assert.Equal(28.4m, dto.TransferWeekdayRate);
        Assert.Equal(35.5m, dto.TransferSaturdayRate);
        Assert.Null(dto.TransferSundayRate); // 没勾：留空
        Assert.Null(dto.TransferPublicHolidayRate); // 没勾：交给 CreatePayRateAsync 按「没带则等于平日价」
        Assert.Null(dto.CashRate);
        Assert.Equal(0m, dto.CashAllowancePerPeriod);
    }

    [Fact]
    public async Task Apply_RejectsEmployeeWhosePreviewHashNoLongerMatches_WithoutWriting()
    {
        var h = NewHarness(
            [OurRow("u-ft", "Aidan")],
            new() { ["u-ft"] = "m-ft" },
            new() { ["m-ft"] = Card("m-ft", 28.4m) });
        var preview = (await h.Service.PreviewAsync(PreviewRequest(Today), CancellationToken.None)).Data!;

        // 预览之后 MYOB 里时薪被改了：apply 一律绕过缓存重读，哈希对不上就不写。
        h.Wages.Cards["m-ft"] = Card("m-ft", 29m);
        var result = await h.Service.ApplyAsync(ApplyRequest(preview, ("u-ft", [MyobPayRateSyncCells.Weekday])), CancellationToken.None);

        var failure = Assert.Single(result.Data!.Failed);
        Assert.Equal(MyobPayRateSyncErrorCodes.ValueChanged, failure.Code);
        Assert.Empty(h.Created);
        Assert.True(h.Wages.LastRefresh, "写入前必须绕过缓存重新读 MYOB");
    }

    [Fact]
    public async Task Apply_RejectsCellsWithNothingToWrite_NoCellsAndMissingWeekdayForNewEmployee()
    {
        var h = NewHarness(
            [OurRow("u-same", "Sam", weekday: 28.4m), OurRow("u-new", "Nina", hasRecord: false)],
            new() { ["u-same"] = "m-same", ["u-new"] = "m-new" },
            new() { ["m-same"] = Card("m-same", 28.4m), ["m-new"] = Card("m-new", 28.4m) });
        var preview = (await h.Service.PreviewAsync(PreviewRequest(Today), CancellationToken.None)).Data!;

        var result = await h.Service.ApplyAsync(
            ApplyRequest(
                preview,
                ("u-same", [MyobPayRateSyncCells.Weekday]), // 平日价已一致：没有可写内容
                ("u-new", [MyobPayRateSyncCells.PublicHoliday])), // 新员工只勾公假、没勾平日价：写不出一条有效记录
            CancellationToken.None);

        Assert.Equal(2, result.Data!.Failed.Count);
        Assert.All(result.Data.Failed, failure => Assert.Equal(MyobPayRateSyncErrorCodes.NotApplicable, failure.Code));
        Assert.Empty(h.Created);

        var noCells = await h.Service.ApplyAsync(ApplyRequest(preview, ("u-new", [])), CancellationToken.None);
        Assert.Equal(MyobPayRateSyncErrorCodes.NotApplicable, Assert.Single(noCells.Data!.Failed).Code);
    }

    [Fact]
    public async Task Apply_OneFailureDoesNotBlockOthers_AndKeepsCreatePayRateErrorCode()
    {
        var h = NewHarness(
            [OurRow("u-a", "Aidan"), OurRow("u-b", "Bella"), OurRow("u-c", "Cara")],
            new() { ["u-a"] = "m-a", ["u-b"] = "m-b", ["u-c"] = "m-c" },
            new() { ["m-a"] = Card("m-a", 28.4m), ["m-b"] = Card("m-b", 28.4m), ["m-c"] = Card("m-c", 28.4m) });
        h.RejectUsers.Add("u-b");
        var preview = (await h.Service.PreviewAsync(PreviewRequest(Today), CancellationToken.None)).Data!;

        var result = await h.Service.ApplyAsync(
            ApplyRequest(
                preview,
                ("u-a", [MyobPayRateSyncCells.Weekday]),
                ("u-b", [MyobPayRateSyncCells.Weekday]),
                ("u-c", [MyobPayRateSyncCells.Weekday])),
            CancellationToken.None);

        Assert.Equal(["u-a", "u-c"], result.Data!.Written.Select(item => item.UserGuid).ToArray());
        var failure = Assert.Single(result.Data.Failed);
        Assert.Equal("u-b", failure.UserGuid);
        Assert.Equal("PERIOD_LOCKED", failure.Code);
        Assert.Equal(3, h.Created.Count);
    }

    [Fact]
    public async Task Apply_RejectsUnknownEmployeeAndUnboundEmployee()
    {
        var h = NewHarness(
            [OurRow("u-ft", "Aidan"), OurRow("u-unbound", "Una")],
            new() { ["u-ft"] = "m-ft" },
            new() { ["m-ft"] = Card("m-ft", 28.4m) });
        var preview = (await h.Service.PreviewAsync(PreviewRequest(Today), CancellationToken.None)).Data!;

        var request = ApplyRequest(preview, ("u-ft", [MyobPayRateSyncCells.Weekday]));
        request.Items.Add(new MyobPayRateSyncApplyItem { UserGuid = "u-other-store", Hash = "x", Cells = [MyobPayRateSyncCells.Weekday] });
        request.Items.Add(new MyobPayRateSyncApplyItem { UserGuid = "u-unbound", Hash = "x", Cells = [MyobPayRateSyncCells.Weekday] });
        var result = await h.Service.ApplyAsync(request, CancellationToken.None);

        Assert.Equal(["u-ft"], result.Data!.Written.Select(item => item.UserGuid).ToArray());
        Assert.Equal(MyobPayRateSyncErrorCodes.NotInPreview, result.Data.Failed.Single(item => item.UserGuid == "u-other-store").Code);
        Assert.Equal(MyobPayRateSyncErrorCodes.NotBound, result.Data.Failed.Single(item => item.UserGuid == "u-unbound").Code);
    }

    [Fact]
    public async Task Apply_RejectsInvalidRequests()
    {
        var h = NewHarness([], new(), new());

        Assert.Equal(MyobPayRateSyncErrorCodes.InvalidRequest,
            (await h.Service.ApplyAsync(new MyobPayRateSyncApplyRequest { ConnectionGuid = Conn, StoreCode = Store }, CancellationToken.None)).ErrorCode);
        Assert.Equal(MyobPayRateSyncErrorCodes.NoItems,
            (await h.Service.ApplyAsync(
                new MyobPayRateSyncApplyRequest { ConnectionGuid = Conn, StoreCode = Store, EffectiveFrom = Today }, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public void Controller_RequiresBothPayrollEditAndEmployeeBind()
    {
        var policies = Attribute.GetCustomAttributes(typeof(MyobPayRateSyncController), typeof(AuthorizeAttribute))
            .Cast<AuthorizeAttribute>()
            .Select(attribute => attribute.Policy)
            .ToList();

        Assert.Contains(Permissions.Attendance.Payroll.Edit, policies);
        Assert.Contains(Permissions.Myob.Employee.Bind, policies);
    }

    private sealed class FakeWages : IMyobEmployeeWageSource
    {
        public Dictionary<string, MyobEmployeePayrollRecord> Cards { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool LastRefresh { get; private set; }

        public Task<IReadOnlyDictionary<string, MyobEmployeePayrollRecord>> GetAsync(
            string connectionGuid, bool refresh, CancellationToken cancellationToken)
        {
            LastRefresh = refresh;
            return Task.FromResult<IReadOnlyDictionary<string, MyobEmployeePayrollRecord>>(
                new Dictionary<string, MyobEmployeePayrollRecord>(Cards, StringComparer.OrdinalIgnoreCase));
        }
    }

    private sealed class FakeTokens : IMyobTokenProvider
    {
        public Task<string> GetAccessTokenAsync(string connectionGuid, CancellationToken cancellationToken) => Task.FromResult("access-token");
        public void Invalidate(string connectionGuid) { }
    }

    private sealed class FixedTime(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
