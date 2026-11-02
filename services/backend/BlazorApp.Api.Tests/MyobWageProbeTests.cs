using System.Text.Json;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// MYOB Wage 节只读探针（临时，核对完字段后删除）：字段路径 / 类型 / 计数、时薪汇总统计、
/// 响应里不含姓名 / UID / 年薪、指定员工卡只返回时薪三项、连接不存在 / 未启用的拒绝。
/// </summary>
public sealed class MyobWageProbeServiceTests : IDisposable
{
    private const string Conn = "conn-1";
    private const string Biz = "11111111-2222-4333-8444-555555555555";

    // 一张时薪制（带年薪字段为 null）、一张年薪制（时薪 0）、一张没有 Wage 的卡。姓名 / UID / 年薪值用来断言「不外泄」。
    private const string Cards = """
        [
          {"Employee":{"UID":"uid-aidan","Name":"Aidan Secret","DisplayID":"E001"},
           "Wage":{"PayBasis":"Hourly","PayFrequency":"Weekly","HourlyRate":28.4,"AnnualSalary":null,
                   "WageCategories":[{"Name":"Base Hourly"},{"Name":"SATURDAY (x1.25)"}]}},
          {"Employee":{"UID":"uid-bella","Name":"Bella Secret","DisplayID":"E002"},
           "Wage":{"PayBasis":"Salary","PayFrequency":"Fortnightly","HourlyRate":0,"AnnualSalary":104000,
                   "WageCategories":[{"Name":"Base Salary"}]}},
          {"Employee":{"UID":"uid-cara","Name":"Cara Secret","DisplayID":"E003"}}
        ]
        """;

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hb-myob-wage-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;

    public MyobWageProbeServiceTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_dbPath}",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(typeof(MyobConnection));
        var now = new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);
        _db.Insertable(new MyobConnection
        {
            ConnectionGuid = Conn, BusinessId = Biz, BusinessName = "Investment",
            Status = MyobConnectionStatuses.Active, AuthorizedAt = now, CreatedAt = now,
        }).ExecuteCommand();
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private static List<JsonElement> ParseCards() =>
        JsonSerializer.Deserialize<List<JsonElement>>(Cards)!;

    [Fact]
    public void Analyze_ListsFieldPathsKindsAndCounts()
    {
        var dto = MyobWageProbeAnalyzer.Analyze(ParseCards());

        Assert.Equal(3, dto.CardCount);
        Assert.Equal(2, dto.CardsWithWage);

        var hourly = Assert.Single(dto.Fields, item => item.Path == "Wage.HourlyRate");
        Assert.Equal(2, hourly.Present);
        Assert.Equal(2, hourly.NonNull);
        Assert.Equal(["Number"], hourly.Kinds);

        // AnnualSalary 一张是 null、一张是数字：类型两种都见到，非空只算一张。
        var salary = Assert.Single(dto.Fields, item => item.Path == "Wage.AnnualSalary");
        Assert.Equal(2, salary.Present);
        Assert.Equal(1, salary.NonNull);
        Assert.Equal(["Null", "Number"], salary.Kinds);

        // 数组元素用 [] 表示，且同一张卡里重复出现的路径只算一次。
        var category = Assert.Single(dto.Fields, item => item.Path == "Wage.WageCategories[].Name");
        Assert.Equal(2, category.Present);
    }

    [Fact]
    public void Analyze_GivesEnumCountsAndRateStatsOnly()
    {
        var dto = MyobWageProbeAnalyzer.Analyze(ParseCards());

        Assert.Equal(1, dto.EnumValues["Wage.PayBasis"]["Hourly"]);
        Assert.Equal(1, dto.EnumValues["Wage.PayBasis"]["Salary"]);
        Assert.Equal(1, dto.EnumValues["Wage.PayFrequency"]["Weekly"]);

        var stats = Assert.Single(dto.RateStats);
        Assert.Equal("Wage.HourlyRate", stats.Path);
        Assert.Equal(2, stats.Count);
        Assert.Equal(1, stats.ZeroCount);
        Assert.Equal(0m, stats.Min);
        Assert.Equal(28.4m, stats.Max);
    }

    [Fact]
    public void Analyze_NeverContainsNamesUidsOrSalaryValues()
    {
        var json = JsonSerializer.Serialize(MyobWageProbeAnalyzer.Analyze(ParseCards()));

        Assert.DoesNotContain("Secret", json);
        Assert.DoesNotContain("uid-", json);
        Assert.DoesNotContain("104000", json);
        // 工资类别名是公司级数据，但探针也不回显值，只回显路径。
        Assert.DoesNotContain("Base Hourly", json);
    }

    [Fact]
    public void PickCard_ReturnsOnlyPayBasisFrequencyAndHourlyRate()
    {
        var picked = MyobWageProbeAnalyzer.PickCard(ParseCards(), "uid-aidan", "E001");

        Assert.True(picked.Found);
        Assert.Equal("Hourly", picked.PayBasis);
        Assert.Equal("Weekly", picked.PayFrequency);
        Assert.Equal(28.4m, picked.HourlyRate);
        Assert.DoesNotContain("104000", JsonSerializer.Serialize(MyobWageProbeAnalyzer.PickCard(ParseCards(), "uid-bella", "E002")));
    }

    [Fact]
    public void PickCard_UnknownUidIsNotFound()
    {
        Assert.False(MyobWageProbeAnalyzer.PickCard(ParseCards(), "uid-nobody", "E999").Found);
        Assert.False(MyobWageProbeAnalyzer.PickCard(ParseCards(), null, "E999").Found);
    }

    [Fact]
    public async Task Run_ReturnsStructureAndTheRequestedCard()
    {
        var service = Create();

        var result = await service.RunAsync(Conn, "e001", CancellationToken.None);

        Assert.True(result.Success);
        var dto = result.Data!;
        Assert.Equal("Investment", dto.ConnectionName);
        Assert.Equal(3, dto.CardCount);
        Assert.True(dto.Card!.Found);
        Assert.Equal(28.4m, dto.Card.HourlyRate);
    }

    [Fact]
    public async Task Run_WithoutDisplayIdReturnsNoPersonalCard()
    {
        var result = await Create().RunAsync(Conn, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Data!.Card);
    }

    [Fact]
    public async Task Run_RejectsMissingAndInactiveConnections()
    {
        var service = Create();
        Assert.Equal(MyobWageProbeErrorCodes.InvalidRequest, (await service.RunAsync(" ", null, CancellationToken.None)).ErrorCode);
        Assert.Equal(MyobErrorCodes.ConnectionNotFound, (await service.RunAsync("nope", null, CancellationToken.None)).ErrorCode);

        _db.Updateable<MyobConnection>()
            .SetColumns(item => item.Status == MyobConnectionStatuses.NeedsReauth)
            .Where(item => item.ConnectionGuid == Conn)
            .ExecuteCommand();
        Assert.Equal(MyobErrorCodes.ConnectionNotActive, (await service.RunAsync(Conn, null, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public void Controller_RequiresConnectionManagePermission()
    {
        var attribute = (AuthorizeAttribute?)Attribute.GetCustomAttribute(typeof(MyobWageProbeController), typeof(AuthorizeAttribute));

        Assert.Equal(Permissions.Myob.Connection.Manage, attribute?.Policy);
    }

    private MyobWageProbeService Create()
    {
        var directory = new MyobEmployeeDirectoryData(Conn, "Investment", DateTime.UtcNow,
        [
            new MyobDirectoryCard(
                new MyobMatchCard("uid-aidan", "Aidan", "Secret", null, true, [], [], null, true),
                new MyobEmployeeCardDto { Uid = "uid-aidan", DisplayId = "E001", Name = "Aidan Secret" }),
        ]);
        return new MyobWageProbeService(_db, new RawClient(Cards), new FakeTokens(), new FakeDirectory(directory));
    }

    private sealed class RawClient(string json) : IMyobRawReadClient
    {
        public Task<IReadOnlyList<JsonElement>> GetEmployeePayrollDetailsRawAsync(
            string businessId, string accessToken, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<JsonElement>>(JsonSerializer.Deserialize<List<JsonElement>>(json)!);
    }

    private sealed class FakeTokens : IMyobTokenProvider
    {
        public Task<string> GetAccessTokenAsync(string connectionGuid, CancellationToken cancellationToken) => Task.FromResult("access-token");
        public void Invalidate(string connectionGuid) { }
    }

    private sealed class FakeDirectory(MyobEmployeeDirectoryData data) : IMyobEmployeeDirectory
    {
        public Task<MyobEmployeeDirectoryData> GetAsync(string connectionGuid, bool refresh, CancellationToken cancellationToken) =>
            Task.FromResult(data);
    }
}
