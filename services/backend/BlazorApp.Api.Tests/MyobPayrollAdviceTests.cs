using System.Net;
using System.Text;
using BlazorApp.Api.Services.Myob;
using Microsoft.Extensions.Options;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// MYOB 历史工资单：Report/Payroll/EmployeePayrollAdvice 只读客户端。
/// 全部用假 HTTP，不访问真实 MYOB；样例里的姓名与金额都是虚构的。
/// 重点：2026-10-29 探针在真实公司文件上发现 $top=1000 满页时 NextPageLink 为空而数据还有更多，
/// 所以客户端必须按「≤90 天窗口 + $skip」取数，不能跟 NextPageLink，否则会静默漏数据。
/// </summary>
public sealed class MyobPayrollAdviceTests
{
    private const string Uid1 = "11111111-1111-1111-1111-111111111111";
    private const string Uid2 = "22222222-2222-2222-2222-222222222222";

    // ───────────────────────── 查询串与窗口 ─────────────────────────

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
    public void 查询串_skip为0不带skip_大于0才带()
    {
        var range = new MyobPayrollAdviceQuery(new DateTime(2026, 8, 1), new DateTime(2026, 9, 1));
        Assert.DoesNotContain("$skip", MyobAdviceQueries.Build(range), StringComparison.Ordinal);
        Assert.Contains("$top=1000&$skip=2000&$filter=", MyobAdviceQueries.Build(range, 2000), StringComparison.Ordinal);
        Assert.Equal("$top=1000", MyobAdviceQueries.Build(new MyobPayrollAdviceQuery()));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("x' or 1 eq 1 or 'a' eq 'a")]
    public void 查询串_员工UID必须是合法Guid_防止把任意字符串拼进筛选(string uid)
    {
        Assert.Throws<ArgumentException>(() => MyobAdviceQueries.Build(new MyobPayrollAdviceQuery(EmployeeUid: uid)));
    }

    [Fact]
    public void 窗口切分_按最长90天切_首尾相接不重叠_最后一个窗口取剩余()
    {
        var windows = MyobAdviceWindows.Split(new DateTime(2026, 1, 1), new DateTime(2026, 7, 20));
        Assert.Equal(
            [
                (new DateTime(2026, 1, 1), new DateTime(2026, 4, 1)),
                (new DateTime(2026, 4, 1), new DateTime(2026, 6, 30)),
                (new DateTime(2026, 6, 30), new DateTime(2026, 7, 20)),
            ],
            windows.ToArray());
    }

    [Theory]
    [InlineData("2026-10-01", "2026-10-01")]
    [InlineData("2026-10-02", "2026-10-01")]
    public void 窗口切分_区间为空或倒置_没有窗口(string from, string to)
    {
        Assert.Empty(MyobAdviceWindows.Split(DateTime.Parse(from), DateTime.Parse(to)));
    }

    [Fact]
    public void 窗口切分_恰好90天_只有一个窗口()
    {
        var windows = MyobAdviceWindows.Split(new DateTime(2026, 1, 1), new DateTime(2026, 4, 1));
        Assert.Single(windows);
    }

    // ───────────────────────── 客户端：翻页 ─────────────────────────

    private sealed class Recorder
    {
        public List<string> Urls { get; } = [];
        public List<Dictionary<string, string>> Headers { get; } = [];
    }

    private static MyobApiClient Client(Recorder recorder, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(
            new HttpClient(new StubHandler(request =>
            {
                lock (recorder)
                {
                    recorder.Urls.Add(request.RequestUri!.AbsoluteUri);
                    recorder.Headers.Add(request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase));
                }

                return respond(request);
            })),
            Options.Create(new MyobOptions { ClientId = "client-key", ClientSecret = "s", RedirectUri = "https://x/cb", MinRequestIntervalMs = 0 }));

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Item(string uid, string paymentDate, string name = "Jane Sample", string gross = "100.5") =>
        $$"""
        {"Employer":{"CompanyName":"Hot Bargain Investment Pty Ltd","ABNOrTFN":"12345678901"},
         "Employee":{"UID":"{{uid}}","Name":"{{name}}","DisplayID":"E1"},
         "PayPeriodStartDate":"{{paymentDate}}T00:00:00","PayPeriodEndDate":"{{paymentDate}}T00:00:00","PaymentDate":"{{paymentDate}}T00:00:00",
         "PayFrequency":"Weekly","AnnualSalary":98765.432,"GrossPay":{{gross}},"NetPay":80,"HourlyRate":28.4,
         "ChequeNumber":"CHQ12345","DateOfBirth":"1990-02-03T00:00:00",
         "Lines":[{"PayrollCategory":{"Name":"Base Hourly","Type":"Wage"},"Hours":10,"CalculationRate":28.4,"Amount":284,"YearToDate":1000}]}
        """;

    private static string Page(IEnumerable<string> items, string nextLink = "null") =>
        $$"""{"Count":0,"Items":[{{string.Join(",", items)}}],"NextPageLink":{{nextLink}}}""";

    private static string Skip(HttpRequestMessage request)
    {
        var query = request.RequestUri!.Query;
        var at = query.IndexOf("$skip=", StringComparison.Ordinal);
        return at < 0 ? "0" : new string(query[(at + 6)..].TakeWhile(char.IsDigit).ToArray());
    }

    [Fact]
    public async Task 客户端_工资单_一个窗口不满页_一次请求_带筛选与v2头_解析展示字段并丢弃没有员工UID的记录()
    {
        var recorder = new Recorder();
        var noUid = """{"Employee":{"Name":"No Uid"},"GrossPay":1}""";
        var client = Client(recorder, _ => Json(Page([Item(Uid1, "2026-10-23"), Item(Uid2, "2026-10-09", "Tom Sample", "\"1200.50\""), noUid])));

        var records = await client.GetPayrollAdvicesAsync(
            "biz-1", "token", new MyobPayrollAdviceQuery(new DateTime(2026, 10, 1), new DateTime(2026, 11, 1)), CancellationToken.None);

        var url = Assert.Single(recorder.Urls);
        Assert.Equal(
            "https://api.myob.com/accountright/biz-1/Report/Payroll/EmployeePayrollAdvice"
            + "?$top=1000&$filter=PaymentDate%20ge%20datetime'2026-10-01'%20and%20PaymentDate%20lt%20datetime'2026-11-01'",
            url);
        Assert.Equal("v2", recorder.Headers[0]["x-myobapi-version"]);
        Assert.Equal("client-key", recorder.Headers[0]["x-myobapi-key"]);

        // 支付日期倒序。
        Assert.Equal([Uid1, Uid2], records.Select(r => r.EmployeeUid).ToArray());
        var first = records[0];
        Assert.Equal("Hot Bargain Investment Pty Ltd", first.EmployerName);
        Assert.Equal("E1", first.EmployeeDisplayId);
        Assert.Equal(new DateTime(2026, 10, 23), first.PaymentDate);
        Assert.Equal("Weekly", first.PayFrequency);
        Assert.Equal(100.5m, first.GrossPay);
        Assert.Equal(80m, first.NetPay);
        var line = Assert.Single(first.Lines);
        Assert.Equal("Wage", line.CategoryType);
        Assert.Equal(284m, line.Amount);
        Assert.Equal(1200.50m, records[1].GrossPay); // 带引号的数字也能读
    }

    [Fact]
    public async Task 客户端_工资单_窗口满页且NextPageLink为空_必须继续skip翻页_不能漏数据()
    {
        // 真实探针现象：满 1000 条时 NextPageLink 为空，但还有更多。
        var recorder = new Recorder();
        var full = Enumerable.Range(0, 1000).Select(i => Item(Guid.NewGuid().ToString(), "2026-10-23", $"Emp {i}", (100 + i).ToString())).ToList();
        var tail = Enumerable.Range(0, 3).Select(i => Item(Guid.NewGuid().ToString(), "2026-10-02", $"Tail {i}", (5000 + i).ToString())).ToList();
        var client = Client(recorder, request => Json(Page(Skip(request) == "0" ? full : tail)));

        var records = await client.GetPayrollAdvicesAsync(
            "biz-1", "token", new MyobPayrollAdviceQuery(new DateTime(2026, 10, 1), new DateTime(2026, 11, 1)), CancellationToken.None);

        Assert.Equal(1003, records.Count);
        Assert.Equal(2, recorder.Urls.Count);
        Assert.DoesNotContain("$skip", recorder.Urls[0], StringComparison.Ordinal);
        Assert.Contains("$top=1000&$skip=1000&$filter=", recorder.Urls[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task 客户端_工资单_刚好满页后下一页为空_也只多一次请求_不会死循环()
    {
        var recorder = new Recorder();
        var full = Enumerable.Range(0, 1000).Select(i => Item(Guid.NewGuid().ToString(), "2026-10-23", $"Emp {i}")).ToList();
        var client = Client(recorder, request => Json(Page(Skip(request) == "0" ? full : [])));

        var records = await client.GetPayrollAdvicesAsync(
            "biz-1", "token", new MyobPayrollAdviceQuery(new DateTime(2026, 10, 1), new DateTime(2026, 11, 1)), CancellationToken.None);

        Assert.Equal(1000, records.Count);
        Assert.Equal(2, recorder.Urls.Count);
    }

    [Fact]
    public async Task 客户端_工资单_一个窗口翻页超过上限_报错而不是静默截断()
    {
        var recorder = new Recorder();
        var full = Enumerable.Range(0, 1000).Select(i => Item(Guid.NewGuid().ToString(), "2026-10-23", $"Emp {i}")).ToList();
        var client = Client(recorder, _ => Json(Page(full)));

        var error = await Assert.ThrowsAsync<MyobApiException>(() => client.GetPayrollAdvicesAsync(
            "biz-1", "token", new MyobPayrollAdviceQuery(new DateTime(2026, 10, 1), new DateTime(2026, 11, 1)), CancellationToken.None));

        Assert.Equal(MyobErrorCodes.ApiCallFailed, error.Code);
        Assert.Contains("缩小日期范围", error.Message, StringComparison.Ordinal);
        Assert.Equal(20, recorder.Urls.Count);
    }

    [Fact]
    public async Task 客户端_工资单_长区间按90天窗口各取一次_重复记录去重_按支付日期倒序()
    {
        var recorder = new Recorder();
        var client = Client(recorder, request =>
        {
            var filter = Uri.UnescapeDataString(request.RequestUri!.Query);
            // 同一张工资单在相邻两个窗口各出现一次（模拟翻页期间边界漂移）；另有每个窗口自己的一张。
            var items = new List<string> { Item(Uid1, "2026-04-01", "Dup Person", "300") };
            items.Add(filter.Contains("datetime'2026-01-01'", StringComparison.Ordinal)
                ? Item(Uid2, "2026-01-15", "Early", "100")
                : Item(Uid2, "2026-06-15", "Late", "200"));
            return Json(Page(items));
        });

        var records = await client.GetPayrollAdvicesAsync(
            "biz-1", "token", new MyobPayrollAdviceQuery(new DateTime(2026, 1, 1), new DateTime(2026, 7, 20)), CancellationToken.None);

        Assert.Equal(3, recorder.Urls.Count); // 1/1~4/1、4/1~6/30、6/30~7/20
        Assert.Contains("datetime'2026-04-01'%20and%20PaymentDate%20lt%20datetime'2026-06-30'", recorder.Urls.Single(u => u.Contains("datetime'2026-04-01'%20and", StringComparison.Ordinal)), StringComparison.Ordinal);
        // Dup Person 在三个窗口都出现，只保留一条；Early 与 Late 各一条。
        Assert.Equal(3, records.Count);
        Assert.Equal(
            [new DateTime(2026, 6, 15), new DateTime(2026, 4, 1), new DateTime(2026, 1, 15)],
            records.Select(r => r.PaymentDate!.Value).ToArray());
    }

    [Fact]
    public async Task 客户端_工资单_金额不同的同日同周期记录不会被当成重复吞掉()
    {
        var client = Client(new Recorder(), _ => Json(Page([Item(Uid1, "2026-10-23", "Jane", "100"), Item(Uid1, "2026-10-23", "Jane", "120")])));
        var records = await client.GetPayrollAdvicesAsync(
            "biz-1", "token", new MyobPayrollAdviceQuery(new DateTime(2026, 10, 1), new DateTime(2026, 11, 1)), CancellationToken.None);
        Assert.Equal(2, records.Count);
    }

    [Fact]
    public async Task 客户端_工资单_5xx重试一次后成功()
    {
        var calls = 0;
        var client = Client(new Recorder(), _ => ++calls == 1
            ? Json("""{"Errors":[{"Name":"InternalError"}]}""", HttpStatusCode.InternalServerError)
            : Json(Page([Item(Uid1, "2026-10-23")])));

        var records = await client.GetPayrollAdvicesAsync(
            "biz-1", "token", new MyobPayrollAdviceQuery(new DateTime(2026, 10, 1), new DateTime(2026, 11, 1)), CancellationToken.None);

        Assert.Single(records);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task 客户端_工资单_5xx重试后仍失败_报状态码_不回显响应体()
    {
        var calls = 0;
        var client = Client(new Recorder(), _ =>
        {
            calls++;
            return Json("""{"Errors":[{"Name":"Secret","Message":"Jane Sample 1812.40"}]}""", HttpStatusCode.BadGateway);
        });

        var error = await Assert.ThrowsAsync<MyobApiException>(() => client.GetPayrollAdvicesAsync(
            "biz-1", "token", new MyobPayrollAdviceQuery(new DateTime(2026, 10, 1), new DateTime(2026, 11, 1)), CancellationToken.None));

        Assert.Equal(2, calls); // 只重试 1 次
        Assert.Equal(MyobErrorCodes.ApiCallFailed, error.Code);
        Assert.Contains("502", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Jane", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("1812", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task 客户端_工资单_4xx不重试_报状态码(HttpStatusCode status)
    {
        var calls = 0;
        var client = Client(new Recorder(), _ =>
        {
            calls++;
            return Json("""{"Errors":[]}""", status);
        });

        var error = await Assert.ThrowsAsync<MyobApiException>(() => client.GetPayrollAdvicesAsync(
            "biz-1", "token", new MyobPayrollAdviceQuery(new DateTime(2026, 10, 1), new DateTime(2026, 11, 1)), CancellationToken.None));

        Assert.Equal(1, calls);
        Assert.Contains(((int)status).ToString(), error.Message, StringComparison.Ordinal);
        Assert.Equal(status, error.StatusCode);
    }

    [Fact]
    public async Task 客户端_工资单_无法解析的响应_报错且不回显内容()
    {
        var client = Client(new Recorder(), _ => Json("<html>secret gateway page</html>"));
        var error = await Assert.ThrowsAsync<MyobApiException>(() => client.GetPayrollAdvicesAsync(
            "biz-1", "token", new MyobPayrollAdviceQuery(new DateTime(2026, 10, 1), new DateTime(2026, 11, 1)), CancellationToken.None));
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 客户端_工资单_缺少日期区间_拒绝_不允许整个历史一次拉取()
    {
        var client = Client(new Recorder(), _ => Json(Page([])));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetPayrollAdvicesAsync("biz-1", "token", new MyobPayrollAdviceQuery(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetPayrollAdvicesAsync("biz-1", "token", new MyobPayrollAdviceQuery(PaymentFrom: new DateTime(2026, 1, 1)), CancellationToken.None));
    }

    [Fact]
    public async Task 客户端_工资单_按员工筛选_窗口内带guid字面量()
    {
        var recorder = new Recorder();
        var client = Client(recorder, _ => Json(Page([Item(Uid1, "2026-10-23")])));
        await client.GetPayrollAdvicesAsync(
            "biz-1", "token", new MyobPayrollAdviceQuery(new DateTime(2026, 10, 23), new DateTime(2026, 10, 24), Uid1), CancellationToken.None);
        Assert.Contains($"Employee/UID%20eq%20guid'{Uid1}'", Assert.Single(recorder.Urls), StringComparison.Ordinal);
    }

    // ───────────────────────── 隐私边界 ─────────────────────────

    [Fact]
    public void 工资单记录_不携带年薪生日和雇佣分类_时薪与支票号仅为还原MYOB页头而保留()
    {
        // 这些字段在真实响应里确实存在（探针已确认），所以载荷里就不声明、记录类型上也没有，缓存 / DTO / 日志不可能带出去。
        // 2026-10-29 起 HourlyRate / ChequeNumber 放开（MYOB 默认 Pay Slip 页头的 Hourly Rate / Reference Number），只进明细与 PDF。
        var banned = new[] { "AnnualSalary", "DateOfBirth", "EmploymentClassification" };
        var recordProps = typeof(MyobPayrollAdviceRecord).GetProperties().Select(p => p.Name).ToHashSet();
        var payloadProps = typeof(PayrollAdvicePayload).GetProperties().Select(p => p.Name).ToHashSet();
        foreach (var name in banned)
        {
            Assert.DoesNotContain(name, recordProps);
            Assert.DoesNotContain(name, payloadProps);
        }

        Assert.Contains("HourlyRate", recordProps);
        Assert.Contains("ChequeNumber", recordProps);
    }

    [Fact]
    public void 工资单展示对象_序列化后没有敏感字段名()
    {
        var detail = System.Text.Json.JsonSerializer.Serialize(new MyobPayslipDetailDto());
        var row = System.Text.Json.JsonSerializer.Serialize(new MyobPayslipRowDto());
        // 明细只含还原页头所需的 hourlyRate / referenceNumber；列表行一律不含。
        foreach (var name in new[] { "annualSalary", "dateOfBirth", "chequeNumber", "bank", "tfn" })
        {
            Assert.DoesNotContain(name, detail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(name, row, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var name in new[] { "hourlyRate", "referenceNumber" })
        {
            Assert.DoesNotContain(name, row, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
