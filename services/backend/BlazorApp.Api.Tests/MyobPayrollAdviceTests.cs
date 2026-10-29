using System.Net;
using System.Reflection;
using System.Text.Json;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// MYOB 历史工资单第一批：Report/Payroll/EmployeePayrollAdvice 只读客户端 + 结构探针。
/// 全部用假 HTTP / 假客户端，不访问真实 MYOB；样例里的姓名与金额都是虚构的，
/// 同时断言它们不会出现在探针结果里（探针只允许输出结构与计数）。
/// 注意：该报表的真实响应与筛选写法尚未用已授权公司文件核对，这里的样例只是官方文档字段的复刻。
/// </summary>
public sealed class MyobPayrollAdviceTests : IDisposable
{
    private const string Uid1 = "11111111-1111-1111-1111-111111111111";
    private const string Uid2 = "22222222-2222-2222-2222-222222222222";
    private const string Conn = "conn-inv";

    // 悉尼本地 2026-10-29（UTC+10）：UTC 2026-10-28 20:00。
    private static readonly DateTime Now = new(2026, 10, 28, 20, 0, 0, DateTimeKind.Utc);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hb-myob-advice-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;

    public MyobPayrollAdviceTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_dbPath}",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(typeof(MyobConnection));
        _db.Insertable(new MyobConnection
        {
            ConnectionGuid = Conn, BusinessId = "biz-1", BusinessName = "Hot Bargain Investment Pty Ltd",
            Status = MyobConnectionStatuses.Active, AuthorizedAt = Now, CreatedAt = Now,
        }).ExecuteCommand();
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    // ───────────────────────── 查询串 ─────────────────────────

    [Fact]
    public void 查询串_日期区间与员工_用datetime和guid字面量_空格编码为百分号20()
    {
        var query = MyobAdviceQueries.Build(new MyobPayrollAdviceQuery(new DateTime(2026, 8, 1), new DateTime(2026, 11, 1), Uid1));
        Assert.Equal(
            "$top=1000&$filter=PaymentDate%20ge%20datetime'2026-08-01'%20and%20PaymentDate%20lt%20datetime'2026-11-01'"
            + $"%20and%20Employee/UID%20eq%20guid'{Uid1}'",
            query);
    }

    [Fact]
    public void 查询串_没有任何条件_只有top()
    {
        Assert.Equal("$top=1000", MyobAdviceQueries.Build(new MyobPayrollAdviceQuery()));
        Assert.Equal("$top=2&$skip=7&$orderby=PaymentDate%20desc", MyobAdviceQueries.Compose(2, 7, null, "PaymentDate desc"));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("x' or 1 eq 1 or 'a' eq 'a")]
    public void 查询串_员工UID必须是合法Guid_防止把任意字符串拼进筛选(string uid)
    {
        Assert.Throws<ArgumentException>(() => MyobAdviceQueries.Build(new MyobPayrollAdviceQuery(EmployeeUid: uid)));
    }

    // ───────────────────────── 客户端：工资单 ─────────────────────────

    private const string Page1 = """
        {"Items":[{
          "Employer":{"CompanyName":"Hot Bargain Investment Pty Ltd","ABNOrTFN":"12345678901","URI":"x"},
          "Employee":{"UID":"11111111-1111-1111-1111-111111111111","Name":"Jane Sample","DisplayID":"E000123","URI":"x"},
          "PayPeriodStartDate":"2026-10-06T00:00:00","PayPeriodEndDate":"2026-10-19T00:00:00","PaymentDate":"2026-10-23T00:00:00",
          "PayFrequency":"Fortnightly","AnnualSalary":98765.432,"GrossPay":1812.4,"NetPay":1566.4,"HourlyRate":28.4,
          "ChequeNumber":"CHQ12345","DateOfBirth":"1990-02-03T00:00:00",
          "SuperannuationFund":{"UID":"s","Name":"Sample Super Fund","URI":"x"},
          "EmploymentClassification":{"UID":"c","Name":"Casual","URI":"x"},
          "Lines":[
            {"PayrollCategory":{"UID":"a","Name":"Base Hourly","Type":"Wage","URI":"x"},"Hours":56.5,"CalculationRate":28.4,"Amount":1604.6,"YearToDate":19820.1},
            {"PayrollCategory":{"UID":"b","Name":"PAYG Withholding","Type":"Tax","URI":"x"},"Hours":null,"CalculationRate":null,"Amount":-246.0,"YearToDate":-3012.0}
          ]},
          {"Employee":{"Name":"No Uid"},"GrossPay":1}],
         "NextPageLink":"https://api.myob.com/accountright/biz-1/Report/Payroll/EmployeePayrollAdvice?$top=1000&$skip=2"}
        """;

    private const string Page2 = """
        {"Items":[{
          "Employee":{"UID":"22222222-2222-2222-2222-222222222222","Name":"Tom Sample","DisplayID":"E000124"},
          "PaymentDate":"2026-10-09T00:00:00","GrossPay":"1200.50","NetPay":1100,
          "Lines":[{"PayrollCategory":{"Name":"Base Hourly","Type":"Wage"},"Hours":12.308,"Amount":1200.5}]}],
         "NextPageLink":null}
        """;

    private sealed class Recorder
    {
        public List<string> Urls { get; } = [];
        public List<Dictionary<string, string>> Headers { get; } = [];
    }

    private static MyobApiClient Client(Recorder recorder, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(
            new HttpClient(new StubHandler(request =>
            {
                recorder.Urls.Add(request.RequestUri!.AbsoluteUri);
                recorder.Headers.Add(request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase));
                return respond(request);
            })),
            Options.Create(new MyobOptions { ClientId = "client-key", ClientSecret = "s", RedirectUri = "https://x/cb", MinRequestIntervalMs = 0 }));

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task 客户端_工资单_跟随下一页_解析展示字段_丢弃没有员工UID的记录()
    {
        var recorder = new Recorder();
        var client = Client(recorder, request => Json(request.RequestUri!.Query.Contains("skip", StringComparison.Ordinal) ? Page2 : Page1));

        var records = await client.GetPayrollAdvicesAsync(
            "biz-1", "token", new MyobPayrollAdviceQuery(new DateTime(2026, 10, 1), new DateTime(2026, 11, 1)), CancellationToken.None);

        Assert.Equal(2, recorder.Urls.Count);
        Assert.Equal(
            "https://api.myob.com/accountright/biz-1/Report/Payroll/EmployeePayrollAdvice"
            + "?$top=1000&$filter=PaymentDate%20ge%20datetime'2026-10-01'%20and%20PaymentDate%20lt%20datetime'2026-11-01'",
            recorder.Urls[0]);
        Assert.Equal("v2", recorder.Headers[0]["x-myobapi-version"]);
        Assert.Equal("client-key", recorder.Headers[0]["x-myobapi-key"]);

        Assert.Equal([Uid1, Uid2], records.Select(r => r.EmployeeUid).ToArray());
        var first = records[0];
        Assert.Equal("Hot Bargain Investment Pty Ltd", first.EmployerName);
        Assert.Equal("E000123", first.EmployeeDisplayId);
        Assert.Equal(new DateTime(2026, 10, 23), first.PaymentDate);
        Assert.Equal("Fortnightly", first.PayFrequency);
        Assert.Equal(1812.4m, first.GrossPay);
        Assert.Equal(1566.4m, first.NetPay);
        Assert.Equal("Sample Super Fund", first.SuperFundName);
        Assert.Equal(2, first.Lines.Count);
        Assert.Equal("Tax", first.Lines[1].CategoryType);
        Assert.Null(first.Lines[1].Hours);
        Assert.Equal(-246.0m, first.Lines[1].Amount);

        // 第二页的 GrossPay 是带引号的字符串、Hours 是小数：都要能读。
        Assert.Equal(1200.50m, records[1].GrossPay);
        Assert.Equal(12.308m, records[1].Lines[0].Hours);
    }

    [Fact]
    public void 工资单记录_不携带年薪时薪生日支票号和雇佣分类()
    {
        // 隐私边界：这些字段在载荷里就不声明，记录类型上也没有对应属性，缓存 / DTO / 日志不可能带出去。
        var recordProps = typeof(MyobPayrollAdviceRecord).GetProperties().Select(p => p.Name).ToHashSet();
        foreach (var banned in new[] { "AnnualSalary", "HourlyRate", "DateOfBirth", "ChequeNumber", "EmploymentClassification" })
        {
            Assert.DoesNotContain(banned, recordProps);
        }

        var payloadProps = typeof(PayrollAdvicePayload).GetProperties().Select(p => p.Name).ToHashSet();
        foreach (var banned in new[] { "AnnualSalary", "HourlyRate", "DateOfBirth", "ChequeNumber", "EmploymentClassification" })
        {
            Assert.DoesNotContain(banned, payloadProps);
        }
    }

    [Fact]
    public async Task 客户端_工资单_非2xx只报状态码_不回显响应体()
    {
        var client = Client(new Recorder(), _ => Json("""{"Errors":[{"Name":"Secret","Message":"Jane Sample 1812.40"}]}""", HttpStatusCode.Forbidden));
        var error = await Assert.ThrowsAsync<MyobApiException>(() =>
            client.GetPayrollAdvicesAsync("biz-1", "token", new MyobPayrollAdviceQuery(), CancellationToken.None));
        Assert.Equal(MyobErrorCodes.ApiCallFailed, error.Code);
        Assert.Contains("403", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Jane", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("1812", error.Message, StringComparison.Ordinal);
    }

    // ───────────────────────── 客户端：探针 ─────────────────────────

    [Fact]
    public async Task 探针_累计字段路径和计数_结果里没有姓名金额和员工UID()
    {
        var recorder = new Recorder();
        var client = Client(recorder, request => Json(request.RequestUri!.Query.Contains("skip", StringComparison.Ordinal) ? Page2 : Page1));

        var result = await client.ProbePayrollAdviceAsync("biz-1", "token", "$top=1000", 12, CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(2, result.Pages);
        Assert.False(result.Truncated);
        Assert.Equal("Object{Items}", result.RootShape);
        Assert.Contains("NextPageLink", result.RootFields);
        Assert.Equal(3, result.ItemCount); // 含缺 UID 的那条：探针看原始结构，不丢记录
        Assert.Equal(2, result.DistinctEmployees);
        Assert.Equal("2026-10-09", result.MinPaymentDate);
        Assert.Equal("2026-10-23", result.MaxPaymentDate);
        Assert.Equal(["Tax", "Wage"], result.LineTypes.ToArray());
        Assert.Contains("Base Hourly", result.CategoryNames);

        var fields = result.Fields.ToDictionary(f => f.Path, f => f.Kinds);
        Assert.Contains("Lines[].PayrollCategory.Type", fields.Keys);
        Assert.Contains("AnnualSalary", fields.Keys); // 探针要看到真实响应里有哪些字段，包括我们不会读取的
        Assert.Equal(["Number", "String"], fields["GrossPay"].ToArray());
        Assert.Equal(["Null", "Number"], fields["Lines[].Hours"].ToArray());

        // 结果整体序列化后，不得出现任何取值：姓名、金额、UID、DisplayID、公司 ABN、基金名、支票号。
        var json = JsonSerializer.Serialize(result);
        foreach (var secret in new[] { "Jane", "Tom", "1812", "1566", "98765", Uid1, Uid2, "E000123", "12345678901", "Sample Super", "CHQ12345", "1990-02-03" })
        {
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        }

        Assert.Equal(Uid1, result.FirstEmployeeUid); // 仅内部使用；上面的序列化断言证明它不对外输出
    }

    [Fact]
    public async Task 探针_达到页数上限且还有下一页_标记截断()
    {
        var client = Client(new Recorder(), _ => Json(Page1));
        var result = await client.ProbePayrollAdviceAsync("biz-1", "token", "$top=1000", 2, CancellationToken.None);
        Assert.Equal(2, result.Pages);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task 探针_非2xx不抛异常_只记状态码和错误类型名_不记Message()
    {
        var client = Client(new Recorder(), _ => Json(
            """{"Errors":[{"Severity":"Error","Name":"InvalidFilter","Message":"bad filter for Jane Sample 1812.40"}]}""",
            HttpStatusCode.BadRequest));
        var result = await client.ProbePayrollAdviceAsync("biz-1", "token", "$top=1&$filter=x", 12, CancellationToken.None);

        Assert.Equal(400, result.StatusCode);
        Assert.Equal(1, result.Pages);
        Assert.Equal(["InvalidFilter"], result.ErrorNames.ToArray());
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("Jane", json, StringComparison.Ordinal);
        Assert.DoesNotContain("1812", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[{\"PaymentDate\":\"2026-10-01\",\"Employee\":{\"UID\":\"u\"}}]", "Array", 1)]
    [InlineData("{\"PaymentDate\":\"2026-10-01\",\"Employee\":{\"UID\":\"u\"}}", "Object", 1)]
    [InlineData("{\"Items\":[]}", "Object{Items}", 0)]
    [InlineData("", "None", 0)]
    public void 探针累计器_兼容数组_单对象_空列表_空体(string body, string shape, int count)
    {
        var accumulator = new MyobAdviceProbeAccumulator();
        accumulator.AddPage(body);
        var result = accumulator.Build(200, 0, 1, false);
        Assert.Equal(shape, result.RootShape);
        Assert.Equal(count, result.ItemCount);
        Assert.False(result.BodyUnparseable);
    }

    [Fact]
    public void 探针累计器_坏JSON_标记无法解析_不抛异常()
    {
        var accumulator = new MyobAdviceProbeAccumulator();
        Assert.Null(accumulator.AddPage("<html>gateway error</html>"));
        Assert.True(accumulator.Build(200, 0, 1, false).BodyUnparseable);
    }

    // ───────────────────────── 探针服务 ─────────────────────────

    private MyobPayslipProbeService Service(FakeApi api, FakeTokens? tokens = null) =>
        new(_db, api, tokens ?? new FakeTokens(), new FakeTime(Now));

    [Fact]
    public async Task 探针服务_按固定顺序发只读请求_日期窗口以悉尼今天为准_员工UID不出现在结果里()
    {
        var api = new FakeApi();
        var response = await Service(api).RunAsync(Conn, CancellationToken.None);

        Assert.True(response.Success);
        var dto = response.Data!;
        Assert.Equal("2026-10-29", dto.RunDate);
        Assert.Equal(
            ["noFilterTop5", "fullScan", "dateGe", "dateRange", "employeeGuid", "skip", "orderByDesc"],
            dto.Steps.Select(s => s.Key).ToArray());
        Assert.Equal("biz-1", api.BusinessIds.Distinct().Single());
        Assert.All(api.AccessTokens, token => Assert.Equal("access-token", token));

        Assert.Equal("$top=5", api.Queries[0]);
        Assert.Contains("PaymentDate%20ge%20datetime'2026-07-31'", api.Queries[2], StringComparison.Ordinal); // 今天 − 90 天
        Assert.Contains("PaymentDate%20ge%20datetime'2026-09-14'%20and%20PaymentDate%20lt%20datetime'2026-10-30'", api.Queries[3], StringComparison.Ordinal);
        // 假数据文件里的首条记录属于 Uid2，探针取首条记录的员工 UID 来构造筛选。
        Assert.Contains($"Employee/UID%20eq%20guid'{Uid2}'", api.Queries[4], StringComparison.Ordinal);
        Assert.Contains("$skip=", api.Queries[5], StringComparison.Ordinal);
        Assert.Contains("$orderby=PaymentDate%20desc", api.Queries[6], StringComparison.Ordinal);

        // 对外结果：查询串里的员工 UID 用占位符，序列化后不含真实 UID。
        Assert.Contains("<首条记录的员工 UID>", dto.Steps[4].Query, StringComparison.Ordinal);
        Assert.DoesNotContain(Uid1, JsonSerializer.Serialize(dto), StringComparison.Ordinal);
        Assert.DoesNotContain(Uid2, JsonSerializer.Serialize(dto), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 探针服务_结论_数据足够时给出true_数据不足给null()
    {
        var api = new FakeApi();
        var verdicts = (await Service(api).RunAsync(Conn, CancellationToken.None)).Data!.Verdicts;
        Assert.True(verdicts.DateFilterWorks);
        Assert.True(verdicts.EmployeeFilterWorks);
        Assert.True(verdicts.TopWorks);
        Assert.True(verdicts.SkipWorks);
        Assert.True(verdicts.OrderByWorks);

        // 公司文件里一张工资单都没有：什么都证明不了，全部 null，不能误报成「不支持」。
        var empty = new FakeApi { Empty = true };
        var emptyVerdicts = (await Service(empty).RunAsync(Conn, CancellationToken.None)).Data!;
        Assert.Null(emptyVerdicts.Verdicts.DateFilterWorks);
        Assert.Null(emptyVerdicts.Verdicts.SkipWorks);
        Assert.Null(emptyVerdicts.Verdicts.OrderByWorks);
        var employeeStep = emptyVerdicts.Steps.Single(s => s.Key == "employeeGuid");
        Assert.True(employeeStep.Skipped);
        Assert.Null(emptyVerdicts.Verdicts.EmployeeFilterWorks);
    }

    [Fact]
    public async Task 探针服务_服务端忽略筛选_日期筛选结论为false()
    {
        // 区间筛选请求却返回了区间外的老记录：筛选被忽略。
        var api = new FakeApi { IgnoreDateFilter = true };
        var verdicts = (await Service(api).RunAsync(Conn, CancellationToken.None)).Data!.Verdicts;
        Assert.False(verdicts.DateFilterWorks);
    }

    [Fact]
    public async Task 探针服务_MYOB返回403_状态码进结果_不抛异常_各结论为false()
    {
        var api = new FakeApi { Status = 403 };
        var response = await Service(api).RunAsync(Conn, CancellationToken.None);
        Assert.True(response.Success);
        Assert.All(response.Data!.Steps.Where(s => !s.Skipped), s => Assert.Equal(403, s.Result!.StatusCode));
        Assert.False(response.Data.Verdicts.DateFilterWorks);
    }

    [Fact]
    public async Task 探针服务_连接未启用或令牌失效_返回业务错误()
    {
        var missing = await Service(new FakeApi()).RunAsync("nope", CancellationToken.None);
        Assert.False(missing.Success);
        Assert.Equal(MyobErrorCodes.ConnectionNotActive, missing.ErrorCode);

        var tokens = new FakeTokens { Error = new MyobApiException(MyobErrorCodes.NeedsReauthorization, "需要重新授权", isAuthorizationRevoked: true) };
        var revoked = await Service(new FakeApi(), tokens).RunAsync(Conn, CancellationToken.None);
        Assert.False(revoked.Success);
        Assert.Equal(MyobErrorCodes.NeedsReauthorization, revoked.ErrorCode);
    }

    [Fact]
    public async Task 探针服务_单步网络失败_记录到该步_其余步骤继续()
    {
        var api = new FakeApi { FailOnQueryContaining = "$orderby" };
        var dto = (await Service(api).RunAsync(Conn, CancellationToken.None)).Data!;
        var order = dto.Steps.Single(s => s.Key == "orderByDesc");
        Assert.Equal(MyobErrorCodes.ApiCallFailed, order.ErrorCode);
        Assert.Null(order.Result);
        Assert.Null(dto.Verdicts.OrderByWorks);
        Assert.NotNull(dto.Steps.Single(s => s.Key == "fullScan").Result);
    }

    // ───────────────────────── 控制器权限 ─────────────────────────

    [Fact]
    public void 探针控制器_沿用连接管理权限_且路由固定()
    {
        Assert.Equal(Permissions.Myob.Connection.Manage, typeof(MyobPayslipProbeController).GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal("api/react/v1/myob/payslip-probe", typeof(MyobPayslipProbeController).GetCustomAttribute<Microsoft.AspNetCore.Mvc.RouteAttribute>()!.Template);
        var run = typeof(MyobPayslipProbeController).GetMethod(nameof(MyobPayslipProbeController.Run))!;
        Assert.NotNull(run.GetCustomAttribute<Microsoft.AspNetCore.Mvc.HttpGetAttribute>());
        // 只读：控制器上不得有任何 POST / PUT / DELETE 动作。
        Assert.DoesNotContain(
            typeof(MyobPayslipProbeController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => method.GetCustomAttributes().Any(a => a is Microsoft.AspNetCore.Mvc.HttpPostAttribute
                or Microsoft.AspNetCore.Mvc.HttpPutAttribute or Microsoft.AspNetCore.Mvc.HttpDeleteAttribute));
    }

    // ───────────────────────── 假对象 ─────────────────────────

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class FakeTime(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class FakeTokens : IMyobTokenProvider
    {
        public MyobApiException? Error { get; set; }

        public Task<string> GetAccessTokenAsync(string connectionGuid, CancellationToken cancellationToken) =>
            Error is null ? Task.FromResult("access-token") : Task.FromException<string>(Error);

        public void Invalidate(string connectionGuid) { }
    }

    /// <summary>
    /// 只实现探针的假客户端：按查询串里的关键字模拟一个「支持全部筛选」的 MYOB（共 6 条工资单：4 条在近 45 天内，2 条更早）。
    /// 其余接口沿用接口默认实现（NotSupportedException）以外的成员都抛 NotSupported。
    /// </summary>
    private sealed class FakeApi : IMyobApiClient
    {
        public List<string> Queries { get; } = [];
        public List<string> BusinessIds { get; } = [];
        public List<string> AccessTokens { get; } = [];
        public bool Empty { get; set; }
        public bool IgnoreDateFilter { get; set; }
        public int Status { get; set; } = 200;
        public string? FailOnQueryContaining { get; set; }

        // (支付日期, 员工 UID)，默认按文件顺序（升序）。
        private static readonly (string Date, string Uid)[] Data =
        [
            ("2026-07-10", Uid2), ("2026-08-07", Uid1), ("2026-10-02", Uid2),
            ("2026-10-09", Uid1), ("2026-10-16", Uid2), ("2026-10-23", Uid1),
        ];

        public Task<MyobAdviceProbeResult> ProbePayrollAdviceAsync(
            string businessId, string accessToken, string query, int maxPages, CancellationToken cancellationToken)
        {
            Queries.Add(query);
            BusinessIds.Add(businessId);
            AccessTokens.Add(accessToken);
            if (FailOnQueryContaining is not null && query.Contains(FailOnQueryContaining, StringComparison.Ordinal))
            {
                return Task.FromException<MyobAdviceProbeResult>(new MyobApiException(MyobErrorCodes.ApiCallFailed, "无法连接 MYOB 服务"));
            }

            var rows = Status != 200 || Empty ? [] : Select(query);
            var accumulator = new MyobAdviceProbeAccumulator();
            if (Status == 200)
            {
                accumulator.AddPage(ToJson(rows));
            }

            return Task.FromResult(accumulator.Build(Status, 1, 1, false));
        }

        private (string Date, string Uid)[] Select(string query)
        {
            IEnumerable<(string Date, string Uid)> rows = Data;
            var decoded = query.Replace("%20", " ", StringComparison.Ordinal);
            if (decoded.Contains("PaymentDate ge datetime'", StringComparison.Ordinal) && !IgnoreDateFilter)
            {
                var from = Between(decoded, "PaymentDate ge datetime'");
                rows = rows.Where(row => string.CompareOrdinal(row.Date, from) >= 0);
                if (decoded.Contains("PaymentDate lt datetime'", StringComparison.Ordinal))
                {
                    var to = Between(decoded, "PaymentDate lt datetime'");
                    rows = rows.Where(row => string.CompareOrdinal(row.Date, to) < 0);
                }
            }

            if (decoded.Contains("Employee/UID eq guid'", StringComparison.Ordinal))
            {
                var uid = Between(decoded, "Employee/UID eq guid'");
                rows = rows.Where(row => row.Uid == uid);
            }

            if (decoded.Contains("$orderby=PaymentDate desc", StringComparison.Ordinal))
            {
                rows = rows.OrderByDescending(row => row.Date);
            }

            var list = rows.ToList();
            var skip = Number(decoded, "$skip=");
            if (skip is not null)
            {
                list = list.Skip(skip.Value).ToList();
            }

            return list.Take(Number(decoded, "$top=") ?? 1000).ToArray();
        }

        private static string Between(string text, string marker)
        {
            var start = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            return text[start..text.IndexOf('\'', start)];
        }

        private static int? Number(string text, string marker)
        {
            var at = text.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                return null;
            }

            var digits = new string(text[(at + marker.Length)..].TakeWhile(char.IsDigit).ToArray());
            return int.Parse(digits);
        }

        private static string ToJson((string Date, string Uid)[] rows) =>
            "{\"Items\":[" + string.Join(",", rows.Select(row =>
                $"{{\"Employee\":{{\"UID\":\"{row.Uid}\",\"Name\":\"Someone\"}},\"PaymentDate\":\"{row.Date}T00:00:00\",\"GrossPay\":100.5}}")) + "]}";

        public Task<MyobTokenResponse> ExchangeCodeAsync(string code, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MyobTokenResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MyobCompanyInfo> GetCompanyAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MyobEmployeeRecord>> GetEmployeesAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MyobEmployeePayrollRecord>> GetEmployeePayrollDetailsAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MyobEmployeePaymentRecord>> GetEmployeePaymentDetailsAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MyobWageCategoryRecord>> GetWageCategoriesAsync(string businessId, string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<MyobTimesheetStatus> GetTimesheetStatusAsync(string businessId, string accessToken, string employeeUid, DateTime startDate, DateTime endDate, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
