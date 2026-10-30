using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BlazorApp.Api.Data;
using BlazorApp.Api.Models;
using BlazorApp.Api.Services.Sms;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class AustralianPhoneNumberTests
{
    [Theory]
    [InlineData("0412 345 678", "+61412345678")]
    [InlineData("0412-345-678", "+61412345678")]
    [InlineData("+61 412 345 678", "+61412345678")]
    [InlineData("+61412345678", "+61412345678")]
    [InlineData("61412345678", "+61412345678")]
    [InlineData("0061 412 345 678", "+61412345678")]
    [InlineData("+61 (0)412 345 678", "+61412345678")]
    [InlineData("  0412345678  ", "+61412345678")]
    public void 同一号码的不同写法_统一成同一个E164(string raw, string expected)
    {
        Assert.True(AustralianPhoneNumber.TryNormalize(raw, out var e164));
        Assert.Equal(expected, e164);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("02 9876 5432")] // 座机收不到短信
    [InlineData("+61 2 9876 5432")]
    [InlineData("0412 345")] // 太短
    [InlineData("0412 345 6789")] // 太长
    [InlineData("0412 345 67a")] // 夹字母
    [InlineData("0412 345 678 ext 5")]
    [InlineData("++61412345678")]
    [InlineData("0412+345678")]
    [InlineData("+44 7911 123456")] // 非澳洲号码
    [InlineData("412345678")] // 没有国家码也没有前导 0：不猜
    public void 非澳洲手机号_一律拒绝(string? raw)
    {
        Assert.False(AustralianPhoneNumber.TryNormalize(raw, out var e164));
        Assert.Equal(string.Empty, e164);
    }

    [Fact]
    public void 打码号码_只留国家码前缀和最后三位()
    {
        Assert.Equal("+614*****678", AustralianPhoneNumber.Mask("+61412345678"));
        Assert.Equal("***", AustralianPhoneNumber.Mask(null));
        Assert.Equal("***", AustralianPhoneNumber.Mask("+61"));
    }
}

public sealed class SmsMessagesTests
{
    [Fact]
    public void 验证码短信_只有英文且单条不超过160字符()
    {
        var plain = SmsMessages.VerificationCode("123456", 10);
        var guardian = SmsMessages.GuardianVerificationCode("123456", 10);

        foreach (var body in new[] { plain, guardian })
        {
            Assert.True(SmsMessages.IsGsm7Safe(body), body);
            Assert.InRange(body.Length, 1, SmsMessages.SingleSegmentLength);
            Assert.Equal(1, SmsMessages.SegmentCount(body));
            Assert.Contains("123456", body);
            Assert.Contains("10 minutes", body);
        }

        // 监护人验证码额外强调不能告诉员工本人（防代签）。
        Assert.Contains("including the employee", guardian);
    }

    [Fact]
    public void 监护人链接短信_含链接和有效期_只有GSM7字符()
    {
        const string url = "https://hotbargain.vip/minor-employment/sign#token=AbC-dEf_123456789012345678901234567890123";
        var body = SmsMessages.GuardianSigningLink("Sam Lee", url, "05/11 14:00 Sydney time");

        Assert.True(SmsMessages.IsGsm7Safe(body), body);
        Assert.Contains(url, body);
        Assert.Contains("Sam Lee", body);
        Assert.Contains("Valid until 05/11 14:00 Sydney time.", body);
        // 带链接和姓名，允许拼成 2 条，但不应更长。
        Assert.InRange(SmsMessages.SegmentCount(body), 1, 2);
    }

    [Theory]
    [InlineData("José Núñez", "Jose Nunez")] // 变音符号去掉
    [InlineData("  Sam  ", "Sam")]
    [InlineData("张三", "your child")] // 中文名无法用 GSM-7 表达：宁可不写名字
    [InlineData("Sam 张", "your child")]
    [InlineData("", "your child")]
    [InlineData(null, "your child")]
    [InlineData("Sam’s", "your child")] // 弯引号
    public void 姓名进短信前先净化_过不了就用兜底词(string? name, string expected)
    {
        Assert.Equal(expected, SmsMessages.SafeName(name, "your child"));
    }

    [Fact]
    public void 过长姓名用兜底词()
    {
        Assert.Equal("your child", SmsMessages.SafeName(new string('a', 41), "your child"));
    }

    [Theory]
    [InlineData("Hot Bargain: code 123456.", true)]
    [InlineData("Line one\nLine two", true)]
    [InlineData("Price $5 (ok) 50% off; yes? #1 <a>=b_c @d", true)]
    [InlineData("Dash – here", false)] // 长破折号会让整条改用 UCS-2
    [InlineData("Curly ’ quote", false)]
    [InlineData("中文", false)]
    [InlineData("Emoji \U0001F600", false)]
    [InlineData("Euro €", false)] // GSM-7 扩展字符占两位，保守起见不收
    [InlineData("Brace {x}", false)]
    [InlineData("Bracket [x]", false)]
    [InlineData("Pipe | tilde ~ caret ^ backslash \\", false)]
    public void GSM7安全子集(string text, bool expected)
    {
        Assert.Equal(expected, SmsMessages.IsGsm7Safe(text));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(160, 1)]
    [InlineData(161, 2)]
    [InlineData(306, 2)]
    [InlineData(307, 3)]
    public void 计费条数按160和153估算(int length, int segments)
    {
        Assert.Equal(segments, SmsMessages.SegmentCount(new string('a', length)));
    }
}

public sealed class SmsSenderTests : IDisposable
{
    private const string HashKey = "test-hash-key";
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;
    private readonly FakeGateway _gateway = new();

    public SmsSenderTests()
    {
        _connection = new SqliteConnection($"Data Source={Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db")}");
        _connection.Open();
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(typeof(SmsSendLog));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private SmsSender CreateSender(Action<SmsOptions>? configure = null)
    {
        var options = new SmsOptions
        {
            Enabled = true,
            AccountSid = "ACtest",
            AuthToken = "token",
            From = "HotBargain",
            LogHashKey = HashKey,
            PerPhoneHourLimit = 2,
            PerPhoneDayLimit = 3,
            GlobalDayLimit = 5,
        };
        configure?.Invoke(options);
        return new SmsSender(CreateContext(_db), _gateway, Options.Create(options), NullLogger<SmsSender>.Instance);
    }

    private static SmsRequest Request(string phone, string? body = null) =>
        new(SmsPurposes.GuardianVerificationCode, phone, body ?? SmsMessages.VerificationCode("123456", 10), "42");

    private static string Hash(string e164)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(HashKey));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(e164))).ToLowerInvariant();
    }

    [Fact]
    public async Task 总闸默认关闭_拒发且不调服务商不写记录()
    {
        var sender = CreateSender(o => o.Enabled = false);

        var result = await sender.SendAsync(Request("0412 345 678"));

        Assert.False(sender.IsAvailable);
        Assert.False(result.Success);
        Assert.Equal(SmsErrorCodes.Disabled, result.ErrorCode);
        Assert.Empty(_gateway.Calls);
        Assert.False(await _db.Queryable<SmsSendLog>().AnyAsync());
    }

    [Theory]
    [InlineData("AccountSid")]
    [InlineData("AuthToken")]
    [InlineData("From")]
    [InlineData("LogHashKey")]
    public async Task 总闸打开但缺任何一项必填配置_仍然拒发(string missing)
    {
        var sender = CreateSender(o =>
        {
            switch (missing)
            {
                case "AccountSid": o.AccountSid = ""; break;
                case "AuthToken": o.AuthToken = " "; break;
                case "From": o.From = ""; break;
                case "LogHashKey": o.LogHashKey = ""; break;
            }
        });

        var result = await sender.SendAsync(Request("0412 345 678"));

        Assert.False(sender.IsAvailable);
        Assert.Equal(SmsErrorCodes.Disabled, result.ErrorCode);
        Assert.Empty(_gateway.Calls);
    }

    [Theory]
    [InlineData("02 9876 5432")]
    [InlineData("not a number")]
    [InlineData("+44 7911 123456")]
    public async Task 不是澳洲手机号_拒发(string phone)
    {
        var result = await CreateSender().SendAsync(Request(phone));

        Assert.False(result.Success);
        Assert.Equal(SmsErrorCodes.InvalidPhone, result.ErrorCode);
        Assert.Empty(_gateway.Calls);
        Assert.False(await _db.Queryable<SmsSendLog>().AnyAsync());
    }

    [Fact]
    public async Task 正文含非GSM7字符_拒发()
    {
        var result = await CreateSender().SendAsync(Request("0412 345 678", "你的验证码是 123456"));

        Assert.False(result.Success);
        Assert.Equal(SmsErrorCodes.BodyNotGsm7, result.ErrorCode);
        Assert.Empty(_gateway.Calls);
    }

    [Fact]
    public async Task 发送成功_网关收到E164_记录只存哈希和打码号码()
    {
        var body = SmsMessages.VerificationCode("654321", 10);

        var result = await CreateSender().SendAsync(Request("0412 345 678", body));

        Assert.True(result.Success, result.Message);
        var call = Assert.Single(_gateway.Calls);
        Assert.Equal("+61412345678", call.To);
        Assert.Equal(body, call.Body);

        var row = await _db.Queryable<SmsSendLog>().SingleAsync();
        Assert.Equal(SmsSendStatuses.Sent, row.Status);
        Assert.Equal(SmsPurposes.GuardianVerificationCode, row.Purpose);
        Assert.Equal(Hash("+61412345678"), row.PhoneHash);
        Assert.Equal("+614*****678", row.PhoneMask);
        Assert.Equal("SM-fake-1", row.ProviderMessageId);
        Assert.Equal("42", row.RelatedKey);
        Assert.Equal(1, row.Segments);
        Assert.NotNull(row.FinishedAtUtc);
        // 记录里任何一列都不含号码明文、验证码或短信正文。
        var everything = string.Join('|', row.PhoneHash, row.PhoneMask, row.Purpose, row.RelatedKey, row.ProviderMessageId, row.ErrorCode);
        Assert.DoesNotContain("412345678", everything);
        Assert.DoesNotContain("654321", everything);
    }

    [Fact]
    public async Task 服务商失败_记录为Failed且返回统一错误_不泄露服务商细节()
    {
        _gateway.Next = new SmsGatewayResult(false, null, "TWILIO_21211");

        var result = await CreateSender().SendAsync(Request("0412 345 678"));

        Assert.False(result.Success);
        Assert.Equal(SmsErrorCodes.SendFailed, result.ErrorCode);
        Assert.DoesNotContain("21211", result.Message);
        var row = await _db.Queryable<SmsSendLog>().SingleAsync();
        Assert.Equal(SmsSendStatuses.Failed, row.Status);
        Assert.Equal("TWILIO_21211", row.ErrorCode);
        Assert.Null(row.ProviderMessageId);
    }

    [Fact]
    public async Task 同一号码不同写法共用限流_失败也计数()
    {
        var sender = CreateSender(); // 每小时 2 条
        _gateway.Next = new SmsGatewayResult(false, null, "HTTP_500");

        var first = await sender.SendAsync(Request("0412 345 678"));
        var second = await sender.SendAsync(Request("+61 412 345 678"));
        var third = await sender.SendAsync(Request("61412345678"));

        Assert.Equal(SmsErrorCodes.SendFailed, first.ErrorCode);
        Assert.Equal(SmsErrorCodes.SendFailed, second.ErrorCode);
        Assert.Equal(SmsErrorCodes.PhoneRateLimited, third.ErrorCode);
        Assert.Equal(2, _gateway.Calls.Count);
        Assert.Equal(2, await _db.Queryable<SmsSendLog>().CountAsync());
    }

    [Fact]
    public async Task 每天上限按24小时算_超过一小时但未满24小时的记录仍计入()
    {
        var sender = CreateSender(o => { o.PerPhoneHourLimit = 100; o.PerPhoneDayLimit = 2; });
        var hash = Hash("+61412345678");
        await _db.Insertable(new SmsSendLog
        {
            Purpose = SmsPurposes.GuardianVerificationCode, PhoneHash = hash, PhoneMask = "x", Segments = 1,
            Status = SmsSendStatuses.Sent, CreatedAtUtc = DateTime.UtcNow.AddHours(-5),
        }).ExecuteCommandAsync();
        await _db.Insertable(new SmsSendLog
        {
            Purpose = SmsPurposes.GuardianVerificationCode, PhoneHash = hash, PhoneMask = "x", Segments = 1,
            Status = SmsSendStatuses.Sent, CreatedAtUtc = DateTime.UtcNow.AddHours(-20),
        }).ExecuteCommandAsync();

        var result = await sender.SendAsync(Request("0412 345 678"));

        Assert.Equal(SmsErrorCodes.PhoneRateLimited, result.ErrorCode);
        Assert.Empty(_gateway.Calls);
    }

    [Fact]
    public async Task 超过24小时的旧记录不计入限流()
    {
        var sender = CreateSender();
        var hash = Hash("+61412345678");
        for (var i = 0; i < 5; i++)
        {
            await _db.Insertable(new SmsSendLog
            {
                Purpose = SmsPurposes.GuardianVerificationCode, PhoneHash = hash, PhoneMask = "x", Segments = 1,
                Status = SmsSendStatuses.Sent, CreatedAtUtc = DateTime.UtcNow.AddHours(-25),
            }).ExecuteCommandAsync();
        }

        var result = await sender.SendAsync(Request("0412 345 678"));

        Assert.True(result.Success, result.Message);
        Assert.Single(_gateway.Calls);
    }

    [Fact]
    public async Task 全站日预算用完后_任何号码都拒发()
    {
        var sender = CreateSender(o => o.GlobalDayLimit = 2);

        var a = await sender.SendAsync(Request("0412 000 001"));
        var b = await sender.SendAsync(Request("0412 000 002"));
        var c = await sender.SendAsync(Request("0412 000 003"));

        Assert.True(a.Success);
        Assert.True(b.Success);
        Assert.Equal(SmsErrorCodes.DailyBudgetExceeded, c.ErrorCode);
        Assert.Equal(2, _gateway.Calls.Count);
    }

    private static SqlSugarContext CreateContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    private sealed class FakeGateway : ISmsGateway
    {
        public List<(string To, string Body)> Calls { get; } = new();
        public SmsGatewayResult? Next { get; set; }

        public Task<SmsGatewayResult> SendAsync(string toE164, string body, CancellationToken cancellationToken)
        {
            Calls.Add((toE164, body));
            return Task.FromResult(Next ?? new SmsGatewayResult(true, $"SM-fake-{Calls.Count}", null));
        }
    }
}

public sealed class TwilioSmsGatewayTests
{
    private static SmsOptions Options() => new()
    {
        Enabled = true,
        AccountSid = "AC123",
        AuthToken = "secret-token",
        From = "HotBargain",
        LogHashKey = "k",
        ApiBaseUrl = "https://sms.example.test/",
    };

    private static (TwilioSmsGateway Gateway, StubHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var gateway = new TwilioSmsGateway(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(Options()), NullLogger<TwilioSmsGateway>.Instance);
        return (gateway, handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task 成功_请求地址认证头和表单字段正确_返回消息ID()
    {
        var (gateway, handler) = Create(_ => Json(HttpStatusCode.Created, """{"sid":"SM999","status":"queued"}"""));

        var result = await gateway.SendAsync("+61412345678", "Hot Bargain: hello", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("SM999", result.ProviderMessageId);
        Assert.Null(result.ErrorCode);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://sms.example.test/2010-04-01/Accounts/AC123/Messages.json", request.Uri);
        Assert.Equal("Basic", request.AuthScheme);
        Assert.Equal(Convert.ToBase64String(Encoding.ASCII.GetBytes("AC123:secret-token")), request.AuthParameter);
        var form = System.Web.HttpUtility.ParseQueryString(request.Body);
        Assert.Equal("+61412345678", form["To"]);
        Assert.Equal("HotBargain", form["From"]);
        Assert.Equal("Hot Bargain: hello", form["Body"]);
    }

    [Fact]
    public async Task 服务商拒绝_返回服务商错误码_不带号码和正文()
    {
        var (gateway, _) = Create(_ => Json(HttpStatusCode.BadRequest,
            """{"code":21211,"message":"The 'To' number +61412345678 is not a valid phone number.","status":400}"""));

        var result = await gateway.SendAsync("+61412345678", "body", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TWILIO_21211", result.ErrorCode);
        Assert.DoesNotContain("412345678", result.ErrorCode);
    }

    [Fact]
    public async Task 响应不是JSON_按HTTP状态处理()
    {
        var (gateway, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>bad gateway</html>") });

        var result = await gateway.SendAsync("+61412345678", "body", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("HTTP_502", result.ErrorCode);
    }

    [Fact]
    public async Task 网络异常_返回统一错误码_不抛出()
    {
        var (gateway, _) = Create(_ => throw new HttpRequestException("connection refused to https://sms.example.test/ AC123"));

        var result = await gateway.SendAsync("+61412345678", "body", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("GATEWAY_EXCEPTION", result.ErrorCode);
    }

    private sealed record CapturedRequest(HttpMethod Method, string Uri, string? AuthScheme, string? AuthParameter, string Body);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method, request.RequestUri!.ToString(),
                request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter, body));
            return _respond(request);
        }
    }
}
