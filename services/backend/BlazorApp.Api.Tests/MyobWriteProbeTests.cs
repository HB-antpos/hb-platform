using System.Reflection;
using System.Text.Json;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// MYOB Timesheet 写入探针（临时，实测后删除）：安全约束（未配置 / 连接不符 / 过去的周期 / 区间非空都不写）、
/// 在「整体覆盖」与「按行合并」两种假 MYOB 上的结论与清理、日期格式回退、客户端 GET / PUT 的请求形状。
/// 假 MYOB 在内存里模拟 Timesheet，不访问真实 MYOB。
/// </summary>
public sealed class MyobWriteProbeTests : IDisposable
{
    // 悉尼本地 2026-11-04。
    private static readonly DateTime Now = new(2026, 11, 4, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Start = new(2026, 11, 9);
    private const string Conn = "conn-rr";
    private const string Biz = "11111111-2222-4333-8444-555555555555"; // 形如 MYOB 的 BusinessId（cf_uri）；测试用，与我方连接 GUID 刻意不同
    private const string Card = "card-test";
    private const string DisplayId = "0086";
    private const string Weekday = "cat-weekday";
    private const string Saturday = "cat-saturday";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hb-myob-wp-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;
    private readonly ProbeApi _api = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly FakeTime _time = new(Now);

    public MyobWriteProbeTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_dbPath}",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(typeof(MyobConnection), typeof(MyobPayCategoryMapping));
        _db.Insertable(new MyobConnection
        {
            ConnectionGuid = Conn, BusinessId = Biz, BusinessName = "Robinson Road",
            Status = MyobConnectionStatuses.Active, AuthorizedAt = Now, CreatedAt = Now,
        }).ExecuteCommand();
    }

    public void Dispose()
    {
        _cache.Dispose();
        _db.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private MyobWriteProbeService Service(MyobOptions? options = null) => new(
        _db,
        _api,
        new FakeTokens(),
        new MyobPayCategoryService(_db, _api, new FakeTokens(), _cache, _time),
        Options.Create(options ?? Enabled()),
        _time);

    private static MyobOptions Enabled() => new() { WriteProbeBusinessId = Biz, WriteProbeEmployeeDisplayId = DisplayId };

    private static MyobWriteProbeRequest Request(string? periodStart = null, string? business = null, string? unassigned = null) => new()
    {
        BusinessId = business ?? Biz,
        PeriodStart = periodStart ?? "2026-11-09",
        WeekdayCategoryUid = Weekday,
        SaturdayCategoryUid = Saturday,
        UnassignedCategoryUid = unassigned,
    };

    // ───────────────────────── 安全约束：一次写入都不发 ─────────────────────────

    [Theory]
    [InlineData("", DisplayId, Biz)]
    [InlineData(Biz, "", Biz)]
    [InlineData(Biz, DisplayId, "some-other-business-id")]
    [InlineData(Biz, DisplayId, Conn)] // 误把我方连接 GUID 当成 BusinessId 也不行
    public async Task 未配置或公司文件不符_按未开启处理_不读不写(string configuredBusinessId, string configuredDisplayId, string requested)
    {
        var options = new MyobOptions { WriteProbeBusinessId = configuredBusinessId, WriteProbeEmployeeDisplayId = configuredDisplayId };
        var result = await Service(options).RunAsync(Request(business: requested), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(MyobWriteProbeErrorCodes.NotEnabled, result.ErrorCode);
        Assert.Equal(0, _api.Puts);
        Assert.Equal(0, _api.Gets);
        Assert.Equal(0, _api.EmployeeLookups);
    }

    [Fact]
    public async Task 员工卡不是启用中的test_test专用卡_拒绝_不读不写()
    {
        foreach (var card in new[]
        {
            new MyobEmployeeRecord(Card, DisplayId, "Jane", "Smith", null, true, true, [], []),   // 真人卡
            new MyobEmployeeRecord(Card, DisplayId, "test", "Smith", null, true, true, [], []),   // 只有名叫 test
            new MyobEmployeeRecord(Card, DisplayId, "test", "test", null, true, false, [], []),   // 已停用
        })
        {
            _api.Employees = [card];
            var result = await Service().RunAsync(Request(), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(MyobWriteProbeErrorCodes.TestCardNotAllowed, result.ErrorCode);
        }

        Assert.Equal(0, _api.Puts);
        Assert.Equal(0, _api.Gets);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Employee_ID匹配不到唯一一张卡_拒绝_不读不写(int count)
    {
        _api.Employees = Enumerable.Range(0, count)
            .Select(index => new MyobEmployeeRecord($"card-{index}", DisplayId, "test", "test", null, true, true, [], []))
            .ToList();

        var result = await Service().RunAsync(Request(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(MyobWriteProbeErrorCodes.TestCardNotAllowed, result.ErrorCode);
        Assert.Equal(0, _api.Puts);
        Assert.Equal(0, _api.Gets);
    }

    [Fact]
    public async Task Employee_ID不区分前后空格和大小写_仍只匹配该卡()
    {
        _api.Employees =
        [
            new MyobEmployeeRecord("card-other", "0087", "test", "test", null, true, true, [], []),
            new MyobEmployeeRecord(Card, " 0086 ", " TEST ", "Test", null, true, true, [], []),
        ];

        var result = await Service().RunAsync(Request(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.All(_api.PutEmployees, uid => Assert.Equal(Card, uid));
    }

    [Fact]
    public async Task 过去的周期被拒绝_不读不写()
    {
        var result = await Service().RunAsync(Request("2026-11-03"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(MyobWriteProbeErrorCodes.InvalidRequest, result.ErrorCode);
        Assert.Equal(0, _api.Puts);
        Assert.Equal(0, _api.Gets);
    }

    [Fact]
    public async Task 日期格式不对被拒绝()
    {
        var result = await Service().RunAsync(Request("11/09/2026"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(MyobWriteProbeErrorCodes.InvalidRequest, result.ErrorCode);
        Assert.Equal(0, _api.Puts);
    }

    [Fact]
    public async Task 没有类别又没有保存映射_拒绝()
    {
        var request = Request();
        request.WeekdayCategoryUid = null;
        var result = await Service().RunAsync(request, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(MyobWriteProbeErrorCodes.CategoryMissing, result.ErrorCode);
        Assert.Equal(0, _api.Puts);
    }

    [Fact]
    public async Task 区间内测试卡已有记录_中止_一次写入都不发_也不改动原有记录()
    {
        _api.Seed(Weekday, "2026-11-12T00:00:00", 8m);

        var result = await Service().RunAsync(Request(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Data!.Aborted);
        Assert.Equal(0, _api.Puts);
        Assert.Single(_api.Store);
        Assert.Equal(8m, _api.Store[0].Hours);
    }

    [Fact]
    public async Task 写入前读取失败_中止_不写()
    {
        _api.GetError = new MyobApiException(MyobErrorCodes.ApiCallFailed, "MYOB Timesheet 接口返回 500", System.Net.HttpStatusCode.InternalServerError);

        var result = await Service().RunAsync(Request(), CancellationToken.None);

        Assert.True(result.Data!.Aborted);
        Assert.Equal(0, _api.Puts);
    }

    [Fact]
    public async Task 只写配置里的那张员工卡()
    {
        await Service().RunAsync(Request(), CancellationToken.None);

        Assert.True(_api.Puts > 0);
        Assert.All(_api.PutEmployees, uid => Assert.Equal(Card, uid));
        Assert.All(_api.GetEmployees, uid => Assert.Equal(Card, uid));
    }

    // ───────────────────────── 结论与清理 ─────────────────────────

    [Fact]
    public async Task 整体覆盖的MYOB_结论正确_最后清空()
    {
        var result = await Service().RunAsync(Request(), CancellationToken.None);

        var v = result.Data!.Verdicts;
        Assert.False(result.Data.Aborted);
        Assert.Equal("date", v.DateFormatAccepted);
        Assert.True(v.MinimalBodyAccepted);
        Assert.Equal(7.333m, v.HoursReadBack);
        Assert.True(v.PutReplacesAll);
        Assert.False(v.OutOfRangeEntryStored);
        Assert.True(v.ClearWithEmptyLines);
        Assert.Null(v.ClearWithEmptyEntries); // 第一种办法就有效，不再往下试
        Assert.True(v.FinalEmpty);
        Assert.True(v.FinalClean);
        Assert.Empty(_api.Store);
    }

    [Fact]
    public async Task 按行合并且空Lines清不掉的MYOB_逐个降级找到有效办法_最后清空()
    {
        _api.Merge = true;

        var result = await Service().RunAsync(Request(), CancellationToken.None);

        var v = result.Data!.Verdicts;
        Assert.False(v.PutReplacesAll);
        Assert.False(v.ClearWithEmptyLines);
        Assert.True(v.ClearWithEmptyEntries);
        Assert.True(v.FinalEmpty);
        Assert.Empty(_api.Store);
    }

    [Fact]
    public async Task 空Lines和空Entries都清不掉_改成0小时_最后只剩0工时的记录_告知干净但不为空()
    {
        _api.Merge = true;
        _api.EmptyEntriesClears = false;

        var result = await Service().RunAsync(Request(), CancellationToken.None);

        var v = result.Data!.Verdicts;
        Assert.False(v.ClearWithEmptyLines);
        Assert.False(v.ClearWithEmptyEntries);
        Assert.True(v.ClearWithZeroHours);
        Assert.False(v.FinalEmpty);
        Assert.True(v.FinalClean);
        Assert.All(_api.Store, entry => Assert.Equal(0m, entry.Hours));
    }

    [Fact]
    public async Task 越界的一天被MYOB存下来_记录下来_清理也能清掉()
    {
        _api.StoreOutOfRange = true;

        var result = await Service().RunAsync(Request(), CancellationToken.None);

        Assert.True(result.Data!.Verdicts.OutOfRangeEntryStored);
        Assert.True(result.Data.Verdicts.FinalEmpty);
        Assert.Empty(_api.Store);
    }

    [Fact]
    public async Task 日期只收带时间的格式_回退后成功_后续步骤都用带时间的写法()
    {
        _api.RejectPlainDate = true;

        var result = await Service().RunAsync(Request(), CancellationToken.None);

        var v = result.Data!.Verdicts;
        Assert.Equal("datetime", v.DateFormatAccepted);
        Assert.True(v.PutReplacesAll);
        Assert.Equal(400, result.Data.Steps.First(step => step.Key == "put-date-format").HttpStatus);
        // 第一次（被拒绝的）请求写了 2 天纯日期；其后的每一次写入都必须是带时间的写法。
        Assert.All(_api.PutDates.Skip(2), date => Assert.Contains("T00:00:00", date, StringComparison.Ordinal));
        Assert.Empty(_api.Store);
    }

    [Fact]
    public async Task 两种日期格式都被拒绝_停止后续写入_只剩清理读取()
    {
        _api.RejectAllPuts = true;

        var result = await Service().RunAsync(Request(), CancellationToken.None);

        var v = result.Data!.Verdicts;
        Assert.Null(v.DateFormatAccepted);
        Assert.False(v.MinimalBodyAccepted);
        Assert.Equal(2, _api.Puts);
        Assert.True(v.FinalEmpty);
    }

    [Fact]
    public async Task 给测试卡没有的类别写入_被拒绝_记录下来_并清理()
    {
        _api.RejectCategory = "cat-unassigned";

        var result = await Service().RunAsync(Request(unassigned: "cat-unassigned"), CancellationToken.None);

        Assert.True(result.Data!.Verdicts.UnassignedCategoryRejected);
        Assert.Empty(_api.Store);
    }

    [Fact]
    public async Task 请求被取消_仍然清理()
    {
        using var cts = new CancellationTokenSource();
        _api.OnPut = () =>
        {
            if (_api.Puts == 3)
            {
                cts.Cancel();
            }
        };
        _api.ThrowOnCancelledPut = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().RunAsync(Request(), cts.Token));

        Assert.Empty(_api.Store);
    }

    [Fact]
    public async Task 响应里没有员工UID_也不含MYOB响应体()
    {
        var result = await Service().RunAsync(Request(), CancellationToken.None);

        var json = JsonSerializer.Serialize(result.Data);
        Assert.DoesNotContain(Card, json, StringComparison.Ordinal);
        Assert.DoesNotContain(Weekday, json, StringComparison.Ordinal);
    }

    // ───────────────────────── 客户端：GET / PUT 的请求形状 ─────────────────────────

    private static MyobApiClient ClientFor(Func<HttpRequestMessage, HttpResponseMessage> respond, out RecordingHandler handler)
    {
        handler = new RecordingHandler(respond);
        return new MyobApiClient(new HttpClient(handler), Options.Create(new MyobOptions
        {
            ClientId = "client-key", ClientSecret = "s", RedirectUri = "https://x/cb", MinRequestIntervalMs = 0,
        }));
    }

    private static HttpResponseMessage Json(string body, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task 客户端PUT_方法_地址_头_请求体按MYOB的PascalCase属性名()
    {
        var client = ClientFor(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK), out var handler);

        var result = await client.PutTimesheetAsync(
            "biz", "token", "emp-1", new DateTime(2026, 11, 9), new DateTime(2026, 11, 15),
            [new MyobTimesheetPutLine("cat-1", [new MyobTimesheetPutEntry("2026-11-09", 7.5m), new MyobTimesheetPutEntry("2026-11-10", 4m)])],
            CancellationToken.None);

        Assert.True(result.Success);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Contains("/biz/Payroll/Timesheet/emp-1?StartDate=2026-11-09&EndDate=2026-11-15", request.Url, StringComparison.Ordinal);
        Assert.Equal("Bearer token", request.Authorization);
        Assert.Equal("v2", request.ApiVersion);
        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal("emp-1", root.GetProperty("Employee").GetProperty("UID").GetString());
        Assert.Equal("2026-11-09", root.GetProperty("StartDate").GetString());
        Assert.Equal("2026-11-15", root.GetProperty("EndDate").GetString());
        var line = Assert.Single(root.GetProperty("Lines").EnumerateArray());
        Assert.Equal("cat-1", line.GetProperty("PayrollCategory").GetProperty("UID").GetString());
        var entries = line.GetProperty("Entries").EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal("2026-11-09", entries[0].GetProperty("Date").GetString());
        Assert.Equal(7.5m, entries[0].GetProperty("Hours").GetDecimal());
        Assert.False(entries[0].TryGetProperty("Processed", out _)); // 最小请求体：不带 Processed / Job / Notes
    }

    [Fact]
    public async Task 客户端PUT_空Lines_请求体里是空数组()
    {
        var client = ClientFor(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK), out var handler);

        await client.PutTimesheetAsync("biz", "token", "emp-1", new DateTime(2026, 11, 9), new DateTime(2026, 11, 29), [], CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        Assert.Equal(0, body.RootElement.GetProperty("Lines").GetArrayLength());
    }

    [Fact]
    public async Task 客户端PUT_非2xx不抛异常_只给状态码和错误类型名_不回显响应体()
    {
        var client = ClientFor(_ => Json("""{"Errors":[{"ErrorCode":"1","Name":"InvalidDate","Message":"secret-message"}]}""", System.Net.HttpStatusCode.BadRequest), out _);

        var result = await client.PutTimesheetAsync("biz", "t", "e", Start, Start.AddDays(6), [], CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(400, result.StatusCode);
        Assert.Equal("InvalidDate", result.ErrorName);
        Assert.DoesNotContain("secret-message", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 客户端PUT_错误体不是JSON_只给状态码()
    {
        var client = ClientFor(_ => Json("<html>gateway</html>", System.Net.HttpStatusCode.BadGateway), out _);

        var result = await client.PutTimesheetAsync("biz", "t", "e", Start, Start.AddDays(6), [], CancellationToken.None);

        Assert.Equal(502, result.StatusCode);
        Assert.Null(result.ErrorName);
    }

    [Fact]
    public async Task 客户端GET明细_解析类别_日期原文_小时小数位_Processed_兼容列表()
    {
        var client = ClientFor(_ => Json("""
            {"Employee":{"UID":"e"},"Lines":[
              {"PayrollCategory":{"UID":"cat-1","Name":"Base Hourly"},"Entries":[
                {"Date":"2026-11-09T00:00:00","Hours":7.333,"Processed":false},
                {"Date":"2026-11-10T00:00:00","Hours":4,"Processed":true}]},
              {"PayrollCategory":{"UID":"cat-2"},"Entries":[]}]}
            """), out var handler);

        var lines = await client.GetTimesheetLinesAsync("biz", "token", "emp-1", Start, Start.AddDays(6), CancellationToken.None);

        Assert.Equal(2, lines.Count);
        Assert.Equal("cat-1", lines[0].CategoryUid);
        Assert.Equal("2026-11-09T00:00:00", lines[0].Entries[0].Date);
        Assert.Equal(7.333m, lines[0].Entries[0].Hours);
        Assert.False(lines[0].Entries[0].Processed);
        Assert.True(lines[0].Entries[1].Processed);
        Assert.Empty(lines[1].Entries);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
    }

    [Theory]
    [InlineData("")]
    [InlineData("""{"Lines":[]}""")]
    public async Task 客户端GET明细_空体或没有Lines_按没有记录(string body)
    {
        var client = ClientFor(_ => Json(body), out _);
        Assert.Empty(await client.GetTimesheetLinesAsync("b", "t", "e", Start, Start.AddDays(6), CancellationToken.None));
    }

    [Fact]
    public async Task 客户端GET明细_404按没有记录_其他非2xx报错且不回显响应体()
    {
        var notFound = ClientFor(_ => Json("{}", System.Net.HttpStatusCode.NotFound), out _);
        Assert.Empty(await notFound.GetTimesheetLinesAsync("b", "t", "e", Start, Start.AddDays(6), CancellationToken.None));

        var forbidden = ClientFor(_ => Json("secret-body", System.Net.HttpStatusCode.Forbidden), out _);
        var error = await Assert.ThrowsAsync<MyobApiException>(() =>
            forbidden.GetTimesheetLinesAsync("b", "t", "e", Start, Start.AddDays(6), CancellationToken.None));
        Assert.Contains("403", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-body", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 控制器_权限沿用MYOB连接管理()
    {
        Assert.Equal(Permissions.Myob.Connection.Manage, typeof(MyobWriteProbeController).GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }

    // ───────────────────────── 测试替身 ─────────────────────────

    private sealed record RecordedRequest(HttpMethod Method, string Url, string? Authorization, string? ApiVersion, string? Body);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("x-myobapi-version", out var versions) ? versions.FirstOrDefault() : null,
                body));
            return respond(request);
        }
    }

    /// <summary>内存里的假 Timesheet：Merge=false 为整体覆盖（区间内全部替换），Merge=true 为按行合并（只替换请求里出现的类别）。</summary>
    private sealed class ProbeApi : IMyobApiClient
    {
        public sealed record Stored(string Category, string Date, decimal Hours);

        public List<Stored> Store { get; } = [];
        public bool Merge { get; set; }
        public bool EmptyEntriesClears { get; set; } = true;
        public bool StoreOutOfRange { get; set; }
        public bool RejectPlainDate { get; set; }
        public bool RejectAllPuts { get; set; }
        public string? RejectCategory { get; set; }
        public MyobApiException? GetError { get; set; }
        public bool ThrowOnCancelledPut { get; set; }
        public Action? OnPut { get; set; }
        public IReadOnlyList<MyobEmployeeRecord> Employees { get; set; } =
            [new MyobEmployeeRecord(Card, DisplayId, "test", "test", null, true, true, [], [])];

        public int EmployeeLookups { get; private set; }
        public int Puts { get; private set; }
        public int Gets { get; private set; }
        public List<string> PutEmployees { get; } = [];
        public List<string> GetEmployees { get; } = [];
        public List<string> PutDates { get; } = [];

        public void Seed(string category, string date, decimal hours) => Store.Add(new Stored(category, date, hours));

        public Task<IReadOnlyList<MyobTimesheetLineRecord>> GetTimesheetLinesAsync(
            string businessId, string accessToken, string employeeUid, DateTime startDate, DateTime endDate, CancellationToken cancellationToken)
        {
            Gets++;
            GetEmployees.Add(employeeUid);
            if (GetError is not null)
            {
                return Task.FromException<IReadOnlyList<MyobTimesheetLineRecord>>(GetError);
            }

            IReadOnlyList<MyobTimesheetLineRecord> lines = Store
                .Where(entry => InRange(entry.Date, startDate, endDate))
                .GroupBy(entry => entry.Category)
                .Select(group => new MyobTimesheetLineRecord(
                    group.Key, group.Select(entry => new MyobTimesheetEntryRecord(entry.Date, entry.Hours, false)).ToList()))
                .ToList();
            return Task.FromResult(lines);
        }

        public Task<MyobTimesheetPutResult> PutTimesheetAsync(
            string businessId,
            string accessToken,
            string employeeUid,
            DateTime startDate,
            DateTime endDate,
            IReadOnlyList<MyobTimesheetPutLine> lines,
            CancellationToken cancellationToken)
        {
            Puts++;
            PutEmployees.Add(employeeUid);
            PutDates.AddRange(lines.SelectMany(line => line.Entries.Select(entry => entry.Date)));
            OnPut?.Invoke();
            if (ThrowOnCancelledPut && cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (RejectAllPuts
                || (RejectPlainDate && lines.Any(line => line.Entries.Any(entry => !entry.Date.Contains('T', StringComparison.Ordinal))))
                || (RejectCategory is not null && lines.Any(line => line.CategoryUid == RejectCategory)))
            {
                return Task.FromResult(new MyobTimesheetPutResult(400, "InvalidRequest"));
            }

            var incoming = lines
                .SelectMany(line => line.Entries.Select(entry => new Stored(line.CategoryUid, Normalize(entry.Date), entry.Hours)))
                .Where(entry => StoreOutOfRange || InRange(entry.Date, startDate, endDate))
                .ToList();
            if (!Merge)
            {
                Store.RemoveAll(entry => InRange(entry.Date, startDate, endDate));
            }
            else
            {
                // 合并：只替换请求里出现的类别；带空 Entries 的行（且允许）= 清掉该类别在区间内的记录。
                foreach (var line in lines)
                {
                    if (line.Entries.Count == 0 && !EmptyEntriesClears)
                    {
                        continue;
                    }

                    Store.RemoveAll(entry => entry.Category == line.CategoryUid && InRange(entry.Date, startDate, endDate));
                }
            }

            Store.AddRange(incoming);
            return Task.FromResult(new MyobTimesheetPutResult(200, null));
        }

        private static string Normalize(string date) => date.Contains('T', StringComparison.Ordinal) ? date : date + "T00:00:00";

        private static bool InRange(string date, DateTime start, DateTime end)
        {
            var day = DateTime.Parse(date.Length >= 10 ? date[..10] : date);
            return day >= start.Date && day <= end.Date;
        }

        public Task<MyobTokenResponse> ExchangeCodeAsync(string code, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MyobTokenResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MyobCompanyInfo> GetCompanyAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MyobEmployeeRecord>> GetEmployeesAsync(string businessId, string accessToken, CancellationToken cancellationToken)
        {
            EmployeeLookups++;
            return Task.FromResult(Employees);
        }

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

    private sealed class FakeTime(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
