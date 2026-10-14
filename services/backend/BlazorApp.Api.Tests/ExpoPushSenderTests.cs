using System.Net;
using System.Text.Json;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 发送器：用假的 Expo 服务器（HttpMessageHandler）+ SQLite 发件箱，覆盖批量切分、ticket 状态流转、退避重试、回执、
/// 跳过规则、租约领取、清理，以及「日志里没有令牌 / 正文 / AccessToken / 异常消息」。
/// </summary>
public sealed class ExpoPushSenderTests : IDisposable
{
    private const string AccessToken = "super-secret-expo-access-token";

    private readonly PushTestDatabase _database = new();
    private readonly PushTestClock _clock = new(PushTestDatabase.Start);
    private readonly AuditCapturingLoggerProvider _logs = new();
    private readonly PushOptionsMonitor _options = new(new ExpoPushOptions { Enabled = true });

    private ExpoPushSender CreateSender(PushStubHandler handler, PushTestDatabase? database = null) =>
        new((database ?? _database).Context, new PushStubHttpClientFactory(handler), _options, _clock, _logs.CreateLogger<ExpoPushSender>())
        {
            JitterSource = () => 0.5,   // 不抖动，退避时间精确可断言
        };

    private sealed record Seeded(AppNotification Notification, AppPushDevice Device, AppPushDelivery Delivery);

    private Seeded SeedPending(
        string seed,
        string type = "store.price_updates",
        string title = "Price updates for your store",
        string body = "12 products need price updates.",
        string? dataJson = "{\"count\":\"12\"}",
        int priority = AppNotificationPriorities.Normal,
        DateTime? createdAt = null,
        DateTime? expiresAt = null,
        DateTime? notificationRevokedAt = null,
        DateTime? deviceRevokedAt = null,
        string user = "user-a")
    {
        var notification = _database.SeedNotification(type, title, body, priority, dataJson, createdAt, expiresAt, notificationRevokedAt);
        var device = _database.SeedDevice(user, PushTestTokens.Make(seed), revokedAt: deviceRevokedAt, revokeReason: deviceRevokedAt == null ? null : "logout");
        var delivery = _database.SeedDelivery(notification.NotificationId, device, createdAt: createdAt, nextAttemptAt: createdAt ?? _clock.Now);
        return new Seeded(notification, device, delivery);
    }

    private static string Ok(string id) => $"{{\"status\":\"ok\",\"id\":\"{id}\"}}";

    private static string Err(string error) =>
        $"{{\"status\":\"error\",\"message\":\"SECRET-BODY-TEXT for device\",\"details\":{{\"error\":\"{error}\"}}}}";

    private static Task<HttpResponseMessage> Tickets(params string[] tickets) =>
        Task.FromResult(PushStubHandler.Json(HttpStatusCode.OK, $"{{\"data\":[{string.Join(",", tickets)}]}}"));

    private static PushStubHandler AlwaysOk() => new(PushStubHandler.OkTicketsFor);

    private AppPushDelivery Row(Seeded seeded) => _database.Delivery(seeded.Delivery.DeliveryId);

    // ------------------------------------------------------------------ 开关

    [Fact]
    public async Task 开关关闭_不发任何请求_不动发件箱()
    {
        var seeded = SeedPending("off1");
        _options.Value = new ExpoPushOptions { Enabled = false, AccessToken = AccessToken };
        var handler = AlwaysOk();

        var stats = await CreateSender(handler).RunOnceAsync(default);

        Assert.Empty(handler.Requests);
        Assert.False(stats.HadActivity);
        var row = Row(seeded);
        Assert.Equal(AppPushDeliveryStates.Pending, row.State);
        Assert.Equal(0, row.Attempts);
        Assert.Null(row.LockedUntilUtc);
        Assert.Equal((0, 0), await CreateSender(handler).CleanupAsync(default));   // 清理同样不动
        Assert.Empty(_logs.Entries);
    }

    [Fact]
    public async Task 开关在运行中打开_下一轮立即生效()
    {
        var seeded = SeedPending("hot1");
        _options.Value = new ExpoPushOptions { Enabled = false };
        var handler = AlwaysOk();
        var sender = CreateSender(handler);

        await sender.RunOnceAsync(default);
        Assert.Empty(handler.Requests);

        _options.Value = new ExpoPushOptions { Enabled = true };
        await sender.RunOnceAsync(default);

        Assert.Single(handler.SendRequests);
        Assert.Equal(AppPushDeliveryStates.Sent, Row(seeded).State);
    }

    // ------------------------------------------------------------------ 请求形状

    [Fact]
    public async Task 请求形状_端点_请求头_消息字段符合Expo协议()
    {
        _options.Value = new ExpoPushOptions { Enabled = true, AccessToken = AccessToken };
        var seeded = SeedPending("shape1", priority: AppNotificationPriorities.High, expiresAt: _clock.Now.AddHours(2));
        var handler = AlwaysOk();

        await CreateSender(handler).RunOnceAsync(default);

        var request = Assert.Single(handler.SendRequests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://exp.host/--/api/v2/push/send", request.Uri.ToString());
        Assert.Equal($"Bearer {AccessToken}", request.Authorization);
        Assert.Equal("application/json", request.Accept);
        Assert.Equal("application/json", request.ContentType);   // 不带 charset 参数

        using var body = JsonDocument.Parse(request.Body);
        var message = Assert.Single(body.RootElement.EnumerateArray());
        Assert.Equal(seeded.Device.ExpoPushToken, message.GetProperty("to").GetString());
        Assert.Equal("Price updates for your store", message.GetProperty("title").GetString());
        Assert.Equal("12 products need price updates.", message.GetProperty("body").GetString());
        Assert.Equal("default", message.GetProperty("sound").GetString());
        Assert.Equal("default", message.GetProperty("channelId").GetString());
        Assert.Equal("high", message.GetProperty("priority").GetString());
        Assert.Equal(7200, message.GetProperty("ttl").GetInt32());
        var data = message.GetProperty("data");
        Assert.Equal(seeded.Notification.NotificationId, data.GetProperty("notificationId").GetString());
        Assert.Equal("store.price_updates", data.GetProperty("type").GetString());
        Assert.Equal("12", data.GetProperty("count").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 没有AccessToken_不带Authorization头(string? token)
    {
        _options.Value = new ExpoPushOptions { Enabled = true, AccessToken = token };
        SeedPending("noauth1");
        var handler = AlwaysOk();

        await CreateSender(handler).RunOnceAsync(default);

        Assert.Null(Assert.Single(handler.SendRequests).Authorization);
    }

    [Theory]
    [InlineData("https://exp.host/--/api/v2/push", "https://exp.host/--/api/v2/push/send", "https://exp.host/--/api/v2/push/getReceipts")]
    [InlineData("https://exp.host/--/api/v2/push/", "https://exp.host/--/api/v2/push/send", "https://exp.host/--/api/v2/push/getReceipts")]
    [InlineData("http://127.0.0.1:9100/push", "http://127.0.0.1:9100/push/send", "http://127.0.0.1:9100/push/getReceipts")]
    public async Task 端点_拼出send与getReceipts_尾斜杠无影响_本机回环允许http(string endpoint, string sendUrl, string receiptsUrl)
    {
        _options.Value = new ExpoPushOptions { Enabled = true, Endpoint = endpoint };
        var seeded = SeedPending("ep1");
        _database.SeedDelivery(seeded.Notification.NotificationId, _database.SeedDevice("user-a", PushTestTokens.Make("ep2")),
            state: AppPushDeliveryStates.Sent, ticketId: "t-due", sentAt: _clock.Now.AddMinutes(-20), nextAttemptAt: _clock.Now.AddMinutes(-1));
        var handler = new PushStubHandler(request => request.Uri.AbsolutePath.EndsWith("getReceipts", StringComparison.Ordinal)
            ? Task.FromResult(PushStubHandler.Json(HttpStatusCode.OK, "{\"data\":{}}"))
            : PushStubHandler.OkTicketsFor(request));

        await CreateSender(handler).RunOnceAsync(default);

        Assert.Equal(sendUrl, Assert.Single(handler.SendRequests).Uri.ToString());
        Assert.Equal(receiptsUrl, Assert.Single(handler.ReceiptRequests).Uri.ToString());
    }

    [Theory]
    [InlineData("http://exp.host/--/api/v2/push")]     // 明文 http（AccessToken 会放进头里）
    [InlineData("ftp://exp.host/push")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData(null)]
    public async Task 端点不合法_抛配置异常_不发请求(string? endpoint)
    {
        _options.Value = new ExpoPushOptions { Enabled = true, Endpoint = endpoint!, AccessToken = AccessToken };
        var seeded = SeedPending("badep1");
        var handler = AlwaysOk();

        var ex = await Assert.ThrowsAsync<ExpoPushConfigurationException>(() => CreateSender(handler).RunOnceAsync(default));

        Assert.Empty(handler.Requests);
        Assert.DoesNotContain(AccessToken, ex.Message, StringComparison.Ordinal);
        Assert.Equal(AppPushDeliveryStates.Pending, Row(seeded).State);
    }

    // ------------------------------------------------------------------ 批量切分与 ticket 顺序映射

    [Fact]
    public async Task 批量切分_250条按100_100_50发三个请求_ticket按顺序对应各自设备()
    {
        var seeded = Enumerable.Range(0, 250)
            .Select(i => SeedPending($"{i:D5}", createdAt: _clock.Now.AddSeconds(-1000 + i)))
            .ToList();
        var handler = AlwaysOk();

        var stats = await CreateSender(handler).RunOnceAsync(default);

        Assert.Equal([100, 100, 50], handler.SendRequests.Select(request => JsonDocument.Parse(request.Body).RootElement.GetArrayLength()).ToArray());
        Assert.Equal(250, stats.Accepted);
        foreach (var item in seeded)
        {
            var row = Row(item);
            Assert.Equal(AppPushDeliveryStates.Sent, row.State);
            Assert.Equal($"t-{PushStubHandler.TicketSuffix(item.Device.ExpoPushToken)}", row.TicketId);   // 顺序映射没有错位
            Assert.Equal(1, row.Attempts);
            Assert.Equal(_clock.Now, row.SentAtUtc);
            Assert.Equal(_clock.Now.AddMinutes(15), row.NextAttemptAtUtc);   // 回执到期时间
            Assert.Null(row.LockedUntilUtc);
            Assert.Null(row.LastError);
        }
    }

    [Fact]
    public async Task 批大小配置_小于100按配置_大于100封顶100()
    {
        _options.Value = new ExpoPushOptions { Enabled = true, BatchSize = 30 };
        for (var i = 0; i < 70; i++)
        {
            SeedPending($"s{i:D5}", createdAt: _clock.Now.AddSeconds(-500 + i));
        }

        var small = AlwaysOk();
        await CreateSender(small).RunOnceAsync(default);
        Assert.Equal([30, 30, 10], small.SendRequests.Select(request => JsonDocument.Parse(request.Body).RootElement.GetArrayLength()).ToArray());

        _options.Value = new ExpoPushOptions { Enabled = true, BatchSize = 500 };
        for (var i = 0; i < 130; i++)
        {
            SeedPending($"x{i:D5}", createdAt: _clock.Now.AddSeconds(-500 + i));
        }

        var large = AlwaysOk();
        await CreateSender(large).RunOnceAsync(default);
        Assert.Equal([100, 30], large.SendRequests.Select(request => JsonDocument.Parse(request.Body).RootElement.GetArrayLength()).ToArray());
    }

    [Fact]
    public async Task 一批里ok与各类error混合_每行按自己的ticket落状态()
    {
        var ok = SeedPending("mix01", createdAt: _clock.Now.AddSeconds(-50));
        var gone = SeedPending("mix02", createdAt: _clock.Now.AddSeconds(-40));
        var rate = SeedPending("mix03", createdAt: _clock.Now.AddSeconds(-30));
        var big = SeedPending("mix04", createdAt: _clock.Now.AddSeconds(-20));
        var handler = new PushStubHandler(_ => Tickets(Ok("T-OK"), Err("DeviceNotRegistered"), Err("MessageRateExceeded"), Err("MessageTooBig")));

        var stats = await CreateSender(handler).RunOnceAsync(default);

        Assert.Equal(AppPushDeliveryStates.Sent, Row(ok).State);
        Assert.Equal("T-OK", Row(ok).TicketId);
        Assert.Equal(AppPushDeliveryStates.Failed, Row(gone).State);
        Assert.Equal("DeviceNotRegistered", Row(gone).LastError);
        Assert.Equal(AppPushDeliveryStates.Pending, Row(rate).State);
        Assert.Equal(1, Row(rate).Attempts);
        Assert.Equal(AppPushDeliveryStates.Failed, Row(big).State);
        Assert.Equal("MessageTooBig", Row(big).LastError);
        Assert.Equal(1, stats.Accepted);
        Assert.Equal(2, stats.Failed);
        Assert.Equal(1, stats.Retried);
        Assert.Equal(1, stats.DevicesRevoked);
    }

    // ------------------------------------------------------------------ ticket 错误

    [Fact]
    public async Task DeviceNotRegistered_撤销该设备_记录失败_同设备其他排队记录发送时跳过()
    {
        var first = SeedPending("gone1");
        var secondNotification = _database.SeedNotification();
        var second = _database.SeedDelivery(secondNotification.NotificationId, first.Device, nextAttemptAt: _clock.Now.AddMinutes(1));
        var handler = new PushStubHandler(_ => Tickets(Err("DeviceNotRegistered")));

        await CreateSender(handler).RunOnceAsync(default);

        var device = _database.Device(first.Device.PushDeviceId);
        Assert.Equal(_clock.Now, device.RevokedAtUtc);
        Assert.Equal("device-not-registered", device.RevokeReason);
        Assert.Equal(AppPushDeliveryStates.Failed, Row(first).State);
        Assert.Equal(1, Row(first).Attempts);

        // 稍后轮到第二条：设备已撤销 → 跳过，不再向失效令牌发请求。
        _clock.Advance(TimeSpan.FromMinutes(2));
        var again = AlwaysOk();
        await CreateSender(again).RunOnceAsync(default);

        Assert.Empty(again.SendRequests);
        var skipped = _database.Delivery(second.DeliveryId);
        Assert.Equal(AppPushDeliveryStates.Skipped, skipped.State);
        Assert.Equal("device-revoked", skipped.LastError);
    }

    [Theory]
    [InlineData("MessageTooBig")]
    [InlineData("InvalidCredentials")]
    [InlineData("MismatchSenderId")]
    [InlineData("SomethingNew")]
    public async Task 其他ticket错误_不重试_直接失败_只记错误码(string error)
    {
        var seeded = SeedPending("fail1");
        var handler = new PushStubHandler(_ => Tickets(Err(error)));

        await CreateSender(handler).RunOnceAsync(default);

        var row = Row(seeded);
        Assert.Equal(AppPushDeliveryStates.Failed, row.State);
        Assert.Equal(error, row.LastError);
        Assert.Equal(1, row.Attempts);
        Assert.Null(row.LockedUntilUtc);
        Assert.Single(handler.SendRequests);
        Assert.False(_database.Device(seeded.Device.PushDeviceId).RevokedAtUtc.HasValue);   // 只有 DeviceNotRegistered 才撤销设备
    }

    [Fact]
    public async Task InvalidCredentials_记一条Error日志提示凭据问题_不含令牌()
    {
        SeedPending("cred1");
        SeedPending("cred2");
        var handler = new PushStubHandler(_ => Tickets(Err("InvalidCredentials"), Err("InvalidCredentials")));

        await CreateSender(handler).RunOnceAsync(default);

        var error = Assert.Single(_logs.Entries, entry => entry.Level == LogLevel.Error);   // 一批只记一条
        Assert.Contains("InvalidCredentials", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ExponentPushToken", _logs.Dump(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bad code with spaces and https://evil.example/leak")]
    [InlineData("")]
    public async Task ticket错误码含非法字符或缺失_落库为unknown_不把回包文本写进库(string error)
    {
        var seeded = SeedPending("weird1");
        var handler = new PushStubHandler(_ => Tickets(Err(error)));

        await CreateSender(handler).RunOnceAsync(default);

        var row = Row(seeded);
        Assert.Equal(AppPushDeliveryStates.Failed, row.State);
        Assert.Equal("unknown", row.LastError);
    }

    // ------------------------------------------------------------------ 重试与退避

    [Fact]
    public async Task MessageRateExceeded_按5秒30秒2分钟10分钟退避_第5次失败标Failed()
    {
        var seeded = SeedPending("rate1");
        var handler = new PushStubHandler(_ => Tickets(Err("MessageRateExceeded")));
        var sender = CreateSender(handler);
        var expectedDelays = new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10) };

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await sender.RunOnceAsync(default);
            var row = Row(seeded);
            Assert.Equal(AppPushDeliveryStates.Pending, row.State);
            Assert.Equal(attempt, row.Attempts);
            Assert.Equal("MessageRateExceeded", row.LastError);
            Assert.Null(row.LockedUntilUtc);
            Assert.Equal(_clock.Now + expectedDelays[attempt - 1], row.NextAttemptAtUtc);

            // 没到时间不重发。
            var before = handler.SendRequests.Count;
            await sender.RunOnceAsync(default);
            Assert.Equal(before, handler.SendRequests.Count);

            _clock.Advance(expectedDelays[attempt - 1]);
        }

        await sender.RunOnceAsync(default);   // 第 5 次失败 → 达到 MaxAttempts
        var final = Row(seeded);
        Assert.Equal(AppPushDeliveryStates.Failed, final.State);
        Assert.Equal(5, final.Attempts);
        Assert.Equal("MessageRateExceeded", final.LastError);
        Assert.Equal(5, handler.SendRequests.Count);

        await sender.RunOnceAsync(default);   // 终态不再发送
        Assert.Equal(5, handler.SendRequests.Count);
    }

    [Fact]
    public async Task MaxAttempts可配置_设为2则第2次失败即Failed()
    {
        _options.Value = new ExpoPushOptions { Enabled = true, MaxAttempts = 2 };
        var seeded = SeedPending("max1");
        var handler = new PushStubHandler(_ => Tickets(Err("MessageRateExceeded")));
        var sender = CreateSender(handler);

        await sender.RunOnceAsync(default);
        Assert.Equal(AppPushDeliveryStates.Pending, Row(seeded).State);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await sender.RunOnceAsync(default);

        Assert.Equal(AppPushDeliveryStates.Failed, Row(seeded).State);
        Assert.Equal(2, Row(seeded).Attempts);
    }

    [Fact]
    public async Task 退避抖动来自注入的随机源_落在基础时间正负10百分比内()
    {
        var seeded = SeedPending("jit1");
        var handler = new PushStubHandler(_ => Tickets(Err("MessageRateExceeded")));
        var sender = CreateSender(handler);
        sender.JitterSource = () => 0.0;

        await sender.RunOnceAsync(default);

        Assert.Equal(_clock.Now + TimeSpan.FromSeconds(4.5), Row(seeded).NextAttemptAtUtc);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(408)]
    public async Task 整批HTTP错误429_5xx_408_所有记录按退避重试(int status)
    {
        var rows = Enumerable.Range(0, 3).Select(i => SeedPending($"h{status}{i}", createdAt: _clock.Now.AddSeconds(-30 + i))).ToList();
        var handler = new PushStubHandler(_ => Task.FromResult(PushStubHandler.Json((HttpStatusCode)status, "{\"errors\":[{\"code\":\"SERVER\",\"message\":\"SECRET-BODY-TEXT\"}]}")));

        var stats = await CreateSender(handler).RunOnceAsync(default);

        Assert.Equal(3, stats.Retried);
        foreach (var item in rows)
        {
            var row = Row(item);
            Assert.Equal(AppPushDeliveryStates.Pending, row.State);
            Assert.Equal(1, row.Attempts);
            Assert.Equal($"http-{status}", row.LastError);
            Assert.Equal(_clock.Now.AddSeconds(5), row.NextAttemptAtUtc);
        }

        Assert.Single(handler.SendRequests);   // 整批失败不立即重发
    }

    [Fact]
    public async Task 网络异常与超时_按重试处理_错误码network_error与timeout_异常消息不入库不入日志()
    {
        var net = SeedPending("net01", createdAt: _clock.Now.AddSeconds(-20));
        var calls = 0;
        var handler = new PushStubHandler(_ =>
        {
            if (++calls == 1)
            {
                throw new HttpRequestException($"connect failed to exp.host with {net.Device.ExpoPushToken} and {AccessToken}");
            }

            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");
        });
        var sender = CreateSender(handler);

        await sender.RunOnceAsync(default);
        Assert.Equal("network-error", Row(net).LastError);
        Assert.Equal(AppPushDeliveryStates.Pending, Row(net).State);

        _clock.Advance(TimeSpan.FromSeconds(5));
        await sender.RunOnceAsync(default);
        Assert.Equal("timeout", Row(net).LastError);
        Assert.Equal(2, Row(net).Attempts);

        var dump = _logs.Dump();
        Assert.DoesNotContain(net.Device.ExpoPushToken, dump, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessToken, dump, StringComparison.Ordinal);
        Assert.DoesNotContain("connect failed", dump, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 调用方取消_直接抛OperationCanceledException_不当成超时重试()
    {
        var seeded = SeedPending("cancel1");
        using var cts = new CancellationTokenSource();
        var handler = new PushStubHandler(async _ =>
        {
            await cts.CancelAsync();
            cts.Token.ThrowIfCancellationRequested();
            return PushStubHandler.Json(HttpStatusCode.OK, "{}");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateSender(handler).RunOnceAsync(cts.Token));

        Assert.Equal(0, Row(seeded).Attempts);   // 没有被记成一次失败
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(413)]
    public async Task 整批HTTP4xx_不可重试_直接Failed_记http状态码(int status)
    {
        var seeded = SeedPending($"c{status}");
        var handler = new PushStubHandler(_ => Task.FromResult(PushStubHandler.Json((HttpStatusCode)status, "{\"errors\":[{\"code\":\"X\",\"message\":\"SECRET-BODY-TEXT\"}]}")));

        await CreateSender(handler).RunOnceAsync(default);

        var row = Row(seeded);
        Assert.Equal(AppPushDeliveryStates.Failed, row.State);
        Assert.Equal($"http-{status}", row.LastError);
        Assert.DoesNotContain(_logs.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task 凭据被拒绝401与403_整批Failed_记一条Error日志(int status)
    {
        _options.Value = new ExpoPushOptions { Enabled = true, AccessToken = AccessToken };
        var seeded = SeedPending($"auth{status}");
        var handler = new PushStubHandler(_ => Task.FromResult(PushStubHandler.Json((HttpStatusCode)status, "{\"errors\":[{\"code\":\"UNAUTHORIZED\"}]}")));

        await CreateSender(handler).RunOnceAsync(default);

        Assert.Equal(AppPushDeliveryStates.Failed, Row(seeded).State);
        Assert.Equal($"http-{status}", Row(seeded).LastError);
        var error = Assert.Single(_logs.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains(status.ToString(), _logs.Dump(), StringComparison.Ordinal);
        Assert.DoesNotContain(AccessToken, error.Message + _logs.Dump(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"data\":[]}")]                                   // 数量对不上
    [InlineData("{\"data\":[{\"status\":\"ok\",\"id\":\"a\"},{\"status\":\"ok\",\"id\":\"b\"}]}")]   // 比请求多
    [InlineData("{\"data\":{\"status\":\"ok\"}}")]                  // data 不是数组
    [InlineData("[1,2,3]")]
    public async Task 成功状态码但回包结构不对_保守重试_不丢消息(string body)
    {
        var seeded = SeedPending("garb1");
        var handler = new PushStubHandler(_ => Task.FromResult(PushStubHandler.Json(HttpStatusCode.OK, body)));

        await CreateSender(handler).RunOnceAsync(default);

        var row = Row(seeded);
        Assert.Equal(AppPushDeliveryStates.Pending, row.State);
        Assert.Equal("bad-response", row.LastError);
        Assert.Equal(1, row.Attempts);
    }

    [Fact]
    public async Task 同批混了不同Expo项目的令牌_退化成逐条发送_不丢消息()
    {
        var rows = Enumerable.Range(0, 3).Select(i => SeedPending($"proj{i}", createdAt: _clock.Now.AddSeconds(-30 + i))).ToList();
        var handler = new PushStubHandler(request =>
        {
            var count = JsonDocument.Parse(request.Body).RootElement.GetArrayLength();
            return count > 1
                ? Task.FromResult(PushStubHandler.Json(HttpStatusCode.BadRequest,
                    "{\"errors\":[{\"code\":\"PUSH_TOO_MANY_EXPERIENCE_IDS\",\"message\":\"x\",\"details\":{\"@a/one\":[],\"@a/two\":[]}}]}"))
                : PushStubHandler.OkTicketsFor(request);
        });

        var stats = await CreateSender(handler).RunOnceAsync(default);

        Assert.Equal([3, 1, 1, 1], handler.SendRequests.Select(request => JsonDocument.Parse(request.Body).RootElement.GetArrayLength()).ToArray());
        Assert.Equal(3, stats.Accepted);
        Assert.All(rows, item => Assert.Equal(AppPushDeliveryStates.Sent, Row(item).State));
    }

    // ------------------------------------------------------------------ 发送前跳过

    [Fact]
    public async Task 通知已撤回_已过期_设备已撤销_记录过于陈旧_通知或设备缺失_都标Skipped_不发请求()
    {
        var revoked = SeedPending("skp01", notificationRevokedAt: _clock.Now.AddMinutes(-5));
        var expired = SeedPending("skp02", expiresAt: _clock.Now);   // 到期时刻即视为已过期
        var deviceRevoked = SeedPending("skp03", deviceRevokedAt: _clock.Now.AddMinutes(-5));
        var stale = SeedPending("skp04", createdAt: _clock.Now.AddHours(-25));
        var noNotification = SeedPending("skp05");
        _database.Db.Ado.ExecuteCommand("DELETE FROM AppNotification WHERE NotificationId = @id",
            new SqlSugar.SugarParameter("@id", noNotification.Notification.NotificationId));
        var noDevice = SeedPending("skp06");
        _database.Db.Ado.ExecuteCommand("DELETE FROM AppPushDevice WHERE PushDeviceId = @id",
            new SqlSugar.SugarParameter("@id", noDevice.Device.PushDeviceId));
        var handler = AlwaysOk();

        var stats = await CreateSender(handler).RunOnceAsync(default);

        Assert.Empty(handler.Requests);
        Assert.Equal(6, stats.Skipped);
        foreach (var (item, reason) in new[]
        {
            (revoked, "revoked"), (expired, "expired"), (deviceRevoked, "device-revoked"),
            (stale, "stale"), (noNotification, "notification-missing"), (noDevice, "device-missing"),
        })
        {
            var row = Row(item);
            Assert.Equal(AppPushDeliveryStates.Skipped, row.State);
            Assert.Equal(reason, row.LastError);
            Assert.Equal(0, row.Attempts);
            Assert.Null(row.LockedUntilUtc);
        }
    }

    [Fact]
    public async Task 陈旧阈值可配置_刚好不到阈值的照常发_超过的跳过()
    {
        _options.Value = new ExpoPushOptions { Enabled = true, StaleAfterHours = 2 };
        var fresh = SeedPending("stale1", createdAt: _clock.Now.AddMinutes(-119));
        var old = SeedPending("stale2", createdAt: _clock.Now.AddMinutes(-121));
        var handler = AlwaysOk();

        await CreateSender(handler).RunOnceAsync(default);

        Assert.Equal(AppPushDeliveryStates.Sent, Row(fresh).State);
        Assert.Equal(AppPushDeliveryStates.Skipped, Row(old).State);
        Assert.Equal("stale", Row(old).LastError);
        Assert.Equal(1, JsonDocument.Parse(Assert.Single(handler.SendRequests).Body).RootElement.GetArrayLength());   // 只发了 fresh 一条
    }

    [Fact]
    public async Task 关了又开推送_积压的旧记录全部跳过_不补发陈旧通知()
    {
        var oldOnes = Enumerable.Range(0, 5).Select(i => SeedPending($"back{i}", createdAt: _clock.Now.AddDays(-3).AddSeconds(i))).ToList();
        var handler = AlwaysOk();

        await CreateSender(handler).RunOnceAsync(default);

        Assert.Empty(handler.Requests);
        Assert.All(oldOnes, item => Assert.Equal("stale", Row(item).LastError));
    }

    [Fact]
    public async Task 通知在入箱之后被撤回_发送前再检查一次_不发()
    {
        var seeded = SeedPending("rv1");
        var revokedAt = _clock.Now;   // 表达式里只放局部变量：SqlSugar 对 _clock.Now.AddMinutes(n) 这类方法调用的翻译不可靠
        _database.Db.Updateable<AppNotification>()
            .SetColumns(notification => notification.RevokedAtUtc == revokedAt)
            .Where(notification => notification.NotificationId == seeded.Notification.NotificationId)
            .ExecuteCommand();
        var handler = AlwaysOk();

        await CreateSender(handler).RunOnceAsync(default);

        Assert.Empty(handler.Requests);
        Assert.Equal("revoked", Row(seeded).LastError);
    }

    // ------------------------------------------------------------------ 隐私（端到端）

    [Theory]
    [InlineData(AppNotificationTypes.FaceUnwelcomeHit, "Possible match (similarity 93%) at Wallsend", "{\"taskId\":\"task-777\",\"storeCode\":\"ST-1042-X\"}")]
    [InlineData(AppNotificationTypes.FaceUnwelcomeExpiring, "名单条目即将到期", "{\"entryId\":\"entry-888\"}")]
    [InlineData(AppNotificationTypes.ApprovalSubmitted, "Leave request from Zhang San, 2026-10-20 to 2026-10-22", "{\"approvalId\":\"ap-999\"}")]
    [InlineData(AppNotificationTypes.ApprovalDecided, "Your leave request was approved", "{\"approvalId\":\"ap-999\"}")]
    public async Task 敏感类型端到端_请求体只有通用文案与notificationId_type_不含标题正文和DataJson(string type, string title, string dataJson)
    {
        var seeded = SeedPending("priv1", type, title, "Body with PII Zhang San 2026-10-20", dataJson);
        var handler = AlwaysOk();

        await CreateSender(handler).RunOnceAsync(default);

        var request = Assert.Single(handler.SendRequests);
        using var body = JsonDocument.Parse(request.Body);
        var message = Assert.Single(body.RootElement.EnumerateArray());
        Assert.Equal("HB", message.GetProperty("title").GetString());
        Assert.Equal("You have a new notification. Open the app to view it.", message.GetProperty("body").GetString());
        var data = message.GetProperty("data");
        Assert.Equal(2, data.EnumerateObject().Count());
        Assert.Equal(seeded.Notification.NotificationId, data.GetProperty("notificationId").GetString());
        Assert.Equal(type, data.GetProperty("type").GetString());
        foreach (var forbidden in new[] { "Wallsend", "task-777", "ST-1042-X", "entry-888", "ap-999", "Zhang San", "2026-10-20", "PII", "名单条目" })
        {
            Assert.DoesNotContain(forbidden, request.Body, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------ 回执

    private AppPushDelivery SeedSent(string ticketId, DateTime sentAt, DateTime nextAttemptAt, string deviceSeed = "rc")
    {
        var device = _database.SeedDevice("user-a", PushTestTokens.Make(deviceSeed + Guid.NewGuid().ToString("N")[..4]));
        return _database.SeedDelivery(_database.SeedNotification().NotificationId, device,
            state: AppPushDeliveryStates.Sent, ticketId: ticketId, sentAt: sentAt, nextAttemptAt: nextAttemptAt, attempts: 1);
    }

    [Fact]
    public async Task 回执_未到期不请求_到期后ok标Delivered_error标Failed并按需撤销设备()
    {
        var ok = SeedSent("t-ok", _clock.Now.AddMinutes(-20), _clock.Now.AddMinutes(-1));
        var gone = SeedSent("t-gone", _clock.Now.AddMinutes(-20), _clock.Now.AddMinutes(-1));
        var other = SeedSent("t-other", _clock.Now.AddMinutes(-20), _clock.Now.AddMinutes(-1));
        var notYet = SeedSent("t-later", _clock.Now.AddMinutes(-5), _clock.Now.AddMinutes(10));
        var handler = new PushStubHandler(_ => Task.FromResult(PushStubHandler.Json(HttpStatusCode.OK,
            "{\"data\":{\"t-ok\":{\"status\":\"ok\"},"
            + "\"t-gone\":{\"status\":\"error\",\"message\":\"SECRET-BODY-TEXT\",\"details\":{\"error\":\"DeviceNotRegistered\"}},"
            + "\"t-other\":{\"status\":\"error\",\"message\":\"x\",\"details\":{\"error\":\"MessageTooBig\"}},"
            + "\"t-later\":{\"status\":\"ok\"}}}")));

        var stats = await CreateSender(handler).RunOnceAsync(default);

        var request = Assert.Single(handler.ReceiptRequests);
        Assert.Equal("https://exp.host/--/api/v2/push/getReceipts", request.Uri.ToString());
        Assert.Equal(["t-gone", "t-ok", "t-other"], JsonDocument.Parse(request.Body).RootElement.GetProperty("ids").EnumerateArray().Select(id => id.GetString()!).Order(StringComparer.Ordinal).ToArray());

        var okRow = _database.Delivery(ok.DeliveryId);
        Assert.Equal(AppPushDeliveryStates.Delivered, okRow.State);
        Assert.Equal(_clock.Now, okRow.ReceiptCheckedAtUtc);
        var goneRow = _database.Delivery(gone.DeliveryId);
        Assert.Equal(AppPushDeliveryStates.Failed, goneRow.State);
        Assert.Equal("DeviceNotRegistered", goneRow.LastError);
        Assert.Equal("device-not-registered", _database.Device(goneRow.PushDeviceId).RevokeReason);
        var otherRow = _database.Delivery(other.DeliveryId);
        Assert.Equal(AppPushDeliveryStates.Failed, otherRow.State);
        Assert.Equal("MessageTooBig", otherRow.LastError);
        Assert.Null(_database.Device(otherRow.PushDeviceId).RevokedAtUtc);   // 只有 DeviceNotRegistered 才撤销设备
        Assert.Equal(AppPushDeliveryStates.Sent, _database.Delivery(notYet.DeliveryId).State);   // 未到期的不动
        Assert.Equal(1, stats.Delivered);
        Assert.Equal(2, stats.ReceiptFailed);
        Assert.Equal(1, stats.DevicesRevoked);
    }

    [Fact]
    public async Task 回执里没有该id_发送不足24小时过一阵再查_超过24小时按已送达收尾不再追()
    {
        var recent = SeedSent("t-recent", _clock.Now.AddHours(-23), _clock.Now.AddMinutes(-1));
        var expired = SeedSent("t-expired", _clock.Now.AddHours(-24), _clock.Now.AddMinutes(-1));
        var handler = new PushStubHandler(_ => Task.FromResult(PushStubHandler.Json(HttpStatusCode.OK, "{\"data\":{}}")));

        await CreateSender(handler).RunOnceAsync(default);

        var recentRow = _database.Delivery(recent.DeliveryId);
        Assert.Equal(AppPushDeliveryStates.Sent, recentRow.State);
        Assert.Equal(_clock.Now.AddMinutes(15), recentRow.NextAttemptAtUtc);   // 15 分钟后再查，而不是下一个 5 秒节拍
        var expiredRow = _database.Delivery(expired.DeliveryId);
        Assert.Equal(AppPushDeliveryStates.Delivered, expiredRow.State);
        Assert.Equal(_clock.Now, expiredRow.ReceiptCheckedAtUtc);

        // 再跑一轮：不到期的不请求，已收尾的不再查。
        await CreateSender(handler).RunOnceAsync(default);
        Assert.Single(handler.ReceiptRequests);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(429)]
    public async Task 取回执的请求失败_状态不变_不计重试_推迟5分钟再试(int status)
    {
        var row = SeedSent("t-retry", _clock.Now.AddMinutes(-20), _clock.Now.AddMinutes(-1));
        var handler = new PushStubHandler(_ => Task.FromResult(PushStubHandler.Json((HttpStatusCode)status, "{}")));

        await CreateSender(handler).RunOnceAsync(default);

        var updated = _database.Delivery(row.DeliveryId);
        Assert.Equal(AppPushDeliveryStates.Sent, updated.State);
        Assert.Equal(1, updated.Attempts);
        Assert.Equal(_clock.Now.AddMinutes(5), updated.NextAttemptAtUtc);
        Assert.Null(updated.ReceiptCheckedAtUtc);
    }

    [Fact]
    public async Task 回执单次最多1000个id_1500条到期分两个请求()
    {
        var device = _database.SeedDevice("user-a", PushTestTokens.Make("bulk"));
        _database.Db.Insertable(Enumerable.Range(0, 1500).Select(i => new AppPushDelivery
        {
            DeliveryId = Guid.NewGuid().ToString("N"),
            NotificationId = $"n{i:D6}",
            PushDeviceId = device.PushDeviceId,
            UserGUID = "user-a",
            State = AppPushDeliveryStates.Sent,
            Attempts = 1,
            TicketId = $"t-{i:D5}",
            CreatedAtUtc = _clock.Now.AddMinutes(-30),
            SentAtUtc = _clock.Now.AddMinutes(-20),
            NextAttemptAtUtc = _clock.Now.AddSeconds(-1500 + i),
        }).ToList()).ExecuteCommand();
        var handler = new PushStubHandler(request =>
        {
            var ids = JsonDocument.Parse(request.Body).RootElement.GetProperty("ids").EnumerateArray().Select(id => id.GetString()!);
            return Task.FromResult(PushStubHandler.Json(HttpStatusCode.OK,
                "{\"data\":{" + string.Join(",", ids.Select(id => $"\"{id}\":{{\"status\":\"ok\"}}")) + "}}"));
        });

        var stats = await CreateSender(handler).RunOnceAsync(default);

        Assert.Equal([1000, 500], handler.ReceiptRequests.Select(request => JsonDocument.Parse(request.Body).RootElement.GetProperty("ids").GetArrayLength()).ToArray());
        Assert.Equal(1500, stats.Delivered);
        Assert.Equal(1500, _database.Db.Queryable<AppPushDelivery>().Count(row => row.State == AppPushDeliveryStates.Delivered));
    }

    [Fact]
    public async Task 发送成功后经回执延迟到期_整条链路走到Delivered()
    {
        var seeded = SeedPending("chain1");
        var handler = new PushStubHandler(request => request.Uri.AbsolutePath.EndsWith("getReceipts", StringComparison.Ordinal)
            ? Task.FromResult(PushStubHandler.Json(HttpStatusCode.OK, $"{{\"data\":{{\"t-{PushStubHandler.TicketSuffix(seeded.Device.ExpoPushToken)}\":{{\"status\":\"ok\"}}}}}}"))
            : PushStubHandler.OkTicketsFor(request));
        var sender = CreateSender(handler);

        await sender.RunOnceAsync(default);
        Assert.Equal(AppPushDeliveryStates.Sent, Row(seeded).State);
        Assert.Empty(handler.ReceiptRequests);   // 回执延迟 15 分钟，当轮不会立刻去取

        _clock.Advance(TimeSpan.FromMinutes(15));
        await sender.RunOnceAsync(default);

        Assert.Equal(AppPushDeliveryStates.Delivered, Row(seeded).State);
        Assert.Single(handler.SendRequests);
        Assert.Single(handler.ReceiptRequests);
    }

    // ------------------------------------------------------------------ 租约领取

    [Fact]
    public async Task 领取_条件UPDATE抢租约_已被领走的不会再被领到_租约到期后可重新领取()
    {
        for (var i = 0; i < 5; i++)
        {
            SeedPending($"claim{i}", createdAt: _clock.Now.AddSeconds(-10 + i));
        }

        var sender = CreateSender(AlwaysOk());
        var first = await sender.ClaimBatchAsync(100, _clock.Now, default);
        var second = await sender.ClaimBatchAsync(100, _clock.Now, default);

        Assert.Equal(5, first.Claimed.Count);
        Assert.Equal(5, first.CandidateCount);
        Assert.Empty(second.Claimed);      // 租约有效期内候选查询就查不到
        Assert.Equal(0, second.CandidateCount);
        Assert.All(first.Claimed, row => Assert.Equal(_clock.Now + ExpoPushSender.LeaseDuration, row.LockedUntilUtc));

        // 崩溃的实例没写回结果：租约到期后别的实例可以重新领取。
        _clock.Advance(ExpoPushSender.LeaseDuration + TimeSpan.FromSeconds(1));
        var third = await sender.ClaimBatchAsync(100, _clock.Now, default);
        Assert.Equal(5, third.Claimed.Count);
    }

    [Fact]
    public async Task 领取_只领待发送且已到期的_未到期_非Pending_都不领()
    {
        var due = SeedPending("cl1", createdAt: _clock.Now.AddSeconds(-5));
        var future = SeedPending("cl2");
        var later = _clock.Now.AddMinutes(1);
        _database.Db.Updateable<AppPushDelivery>()
            .SetColumns(row => row.NextAttemptAtUtc == later)
            .Where(row => row.DeliveryId == future.Delivery.DeliveryId)
            .ExecuteCommand();
        var sentRow = SeedSent("t-x", _clock.Now.AddMinutes(-20), _clock.Now.AddMinutes(-1));

        var claimed = (await CreateSender(AlwaysOk()).ClaimBatchAsync(100, _clock.Now, default)).Claimed;

        Assert.Equal([due.Delivery.DeliveryId], claimed.Select(row => row.DeliveryId).ToArray());
        Assert.NotEqual(sentRow.DeliveryId, claimed.Single().DeliveryId);
    }

    [Fact]
    public async Task 领取_两个实例用各自连接同时领_已领的行互不重叠_合起来恰好是全部()
    {
        for (var i = 0; i < 60; i++)
        {
            SeedPending($"cc{i:D4}", createdAt: _clock.Now.AddSeconds(-100 + i));
        }

        var senders = Enumerable.Range(0, 4)
            .Select(_ => new ExpoPushSender(_database.NewConnectionContext(), new PushStubHttpClientFactory(AlwaysOk()), _options, _clock, _logs.CreateLogger<ExpoPushSender>()))
            .ToList();

        var results = await Task.WhenAll(senders.Select(sender => Task.Run(() => sender.ClaimBatchAsync(100, _clock.Now, default))));

        var ids = results.SelectMany(result => result.Claimed).Select(row => row.DeliveryId).ToList();
        Assert.Equal(60, ids.Count);                    // 合起来恰好是全部
        Assert.Equal(60, ids.Distinct().Count());       // 没有任何一行被两个实例同时领到
    }

    [Fact]
    public async Task 重复运行不重复发送_已Sent的与租约内的都不再发()
    {
        var done = SeedPending("dup1");
        var leased = SeedPending("dup2");
        var leaseUntil = _clock.Now.AddMinutes(3);
        _database.Db.Updateable<AppPushDelivery>()
            .SetColumns(row => row.LockedUntilUtc == leaseUntil)
            .Where(row => row.DeliveryId == leased.Delivery.DeliveryId)
            .ExecuteCommand();
        var handler = AlwaysOk();
        var sender = CreateSender(handler);

        await sender.RunOnceAsync(default);
        await sender.RunOnceAsync(default);

        var request = Assert.Single(handler.SendRequests);
        Assert.Contains(done.Device.ExpoPushToken, request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(leased.Device.ExpoPushToken, request.Body, StringComparison.Ordinal);
        Assert.Equal(AppPushDeliveryStates.Pending, Row(leased).State);
    }

    // ------------------------------------------------------------------ 清理

    [Fact]
    public async Task 清理_已结束且超过保留期的发件箱记录被删_进行中与未过期的保留()
    {
        var old = _clock.Now.AddDays(-15);
        var recent = _clock.Now.AddDays(-13);
        var device = _database.SeedDevice("user-a", PushTestTokens.Make("clean1"));
        AppPushDelivery Seed(int state, DateTime createdAt) =>
            _database.SeedDelivery(_database.SeedNotification().NotificationId, device, state: state, createdAt: createdAt);
        var oldDelivered = Seed(AppPushDeliveryStates.Delivered, old);
        var oldFailed = Seed(AppPushDeliveryStates.Failed, old);
        var oldSkipped = Seed(AppPushDeliveryStates.Skipped, old);
        var oldPending = Seed(AppPushDeliveryStates.Pending, old);
        var oldSent = Seed(AppPushDeliveryStates.Sent, old);
        var recentDelivered = Seed(AppPushDeliveryStates.Delivered, recent);

        var (deliveries, devices) = await CreateSender(AlwaysOk()).CleanupAsync(default);

        Assert.Equal(3, deliveries);
        Assert.Equal(0, devices);
        var remaining = _database.Db.Queryable<AppPushDelivery>().Select(row => row.DeliveryId).ToList();
        Assert.DoesNotContain(oldDelivered.DeliveryId, remaining);
        Assert.DoesNotContain(oldFailed.DeliveryId, remaining);
        Assert.DoesNotContain(oldSkipped.DeliveryId, remaining);
        Assert.Contains(oldPending.DeliveryId, remaining);
        Assert.Contains(oldSent.DeliveryId, remaining);
        Assert.Contains(recentDelivered.DeliveryId, remaining);
    }

    [Fact]
    public async Task 清理_保留天数可配置_分批删除超过一批也能删完()
    {
        _options.Value = new ExpoPushOptions { Enabled = true, DeliveryRetentionDays = 1 };
        var device = _database.SeedDevice("user-a", PushTestTokens.Make("clean2"));
        _database.Db.Insertable(Enumerable.Range(0, 1300).Select(i => new AppPushDelivery
        {
            DeliveryId = Guid.NewGuid().ToString("N"),
            NotificationId = $"n{i:D6}",
            PushDeviceId = device.PushDeviceId,
            UserGUID = "user-a",
            State = AppPushDeliveryStates.Delivered,
            NextAttemptAtUtc = _clock.Now,
            CreatedAtUtc = _clock.Now.AddDays(-2),
        }).ToList()).ExecuteCommand();
        var keep = _database.SeedDelivery(_database.SeedNotification().NotificationId, device, state: AppPushDeliveryStates.Delivered, createdAt: _clock.Now.AddHours(-23));

        var (deliveries, _) = await CreateSender(AlwaysOk()).CleanupAsync(default);

        Assert.Equal(1300, deliveries);
        Assert.Equal(keep.DeliveryId, Assert.Single(_database.Db.Queryable<AppPushDelivery>().ToList()).DeliveryId);
    }

    [Fact]
    public async Task 清理_撤销超过90天且无发件箱引用的设备才删_有引用_不足90天_活动设备都保留()
    {
        var longAgo = _clock.Now.AddDays(-91);
        var deletable = _database.SeedDevice("user-a", PushTestTokens.Make("dv001"), revokedAt: longAgo, revokeReason: "logout");
        var referenced = _database.SeedDevice("user-a", PushTestTokens.Make("dv002"), revokedAt: longAgo, revokeReason: "logout");
        _database.SeedDelivery(_database.SeedNotification().NotificationId, referenced, state: AppPushDeliveryStates.Pending);   // 未结束的发件箱记录仍引用它
        var tooRecent = _database.SeedDevice("user-a", PushTestTokens.Make("dv003"), revokedAt: _clock.Now.AddDays(-89), revokeReason: "logout");
        var active = _database.SeedDevice("user-a", PushTestTokens.Make("dv004"), lastSeen: longAgo);

        var (_, devices) = await CreateSender(AlwaysOk()).CleanupAsync(default);

        Assert.Equal(1, devices);
        var remaining = _database.Db.Queryable<AppPushDevice>().Select(row => row.PushDeviceId).ToList();
        Assert.DoesNotContain(deletable.PushDeviceId, remaining);
        Assert.Contains(referenced.PushDeviceId, remaining);
        Assert.Contains(tooRecent.PushDeviceId, remaining);
        Assert.Contains(active.PushDeviceId, remaining);
    }

    [Fact]
    public async Task 清理_设备被已结束但未过保留期的发件箱引用时保留_过保留期清掉发件箱后下一轮再清设备()
    {
        var longAgo = _clock.Now.AddDays(-91);
        var device = _database.SeedDevice("user-a", PushTestTokens.Make("dv010"), revokedAt: longAgo, revokeReason: "device-not-registered");
        var delivery = _database.SeedDelivery(_database.SeedNotification().NotificationId, device, state: AppPushDeliveryStates.Failed, createdAt: _clock.Now.AddDays(-5));
        var sender = CreateSender(AlwaysOk());

        var first = await sender.CleanupAsync(default);
        Assert.Equal((0, 0), first);

        _clock.Advance(TimeSpan.FromDays(10));
        var second = await sender.CleanupAsync(default);
        Assert.Equal(1, second.Deliveries);
        Assert.Equal(1, second.Devices);   // 同一轮里先清发件箱、再清已无引用的设备
        Assert.Empty(_database.Db.Queryable<AppPushDelivery>().Where(row => row.DeliveryId == delivery.DeliveryId).ToList());
    }

    // ------------------------------------------------------------------ 日志纪律

    [Fact]
    public async Task 日志只有汇总计数与错误码_没有令牌_标题正文_AccessToken_回包文本_异常消息_也没有异常对象()
    {
        _options.Value = new ExpoPushOptions { Enabled = true, AccessToken = AccessToken };
        var title = "TITLE-LEAK-CHECK";
        var body = "BODY-LEAK-CHECK";
        var a = SeedPending("lk001", title: title, body: body, createdAt: _clock.Now.AddSeconds(-60));
        var b = SeedPending("lk002", title: title, body: body, createdAt: _clock.Now.AddSeconds(-50));
        var c = SeedPending("lk003", title: title, body: body, createdAt: _clock.Now.AddSeconds(-40));
        SeedPending("lk004", title: title, body: body, createdAt: _clock.Now.AddDays(-2));   // 陈旧 → 跳过
        var calls = 0;
        var handler = new PushStubHandler(request =>
        {
            calls++;
            if (request.Uri.AbsolutePath.EndsWith("getReceipts", StringComparison.Ordinal))
            {
                return Task.FromResult(PushStubHandler.Json(HttpStatusCode.OK, "{\"data\":{\"t-ok\":{\"status\":\"error\",\"message\":\"SECRET-BODY-TEXT\",\"details\":{\"error\":\"DeviceNotRegistered\"}}}}"));
            }

            return calls switch
            {
                1 => Tickets(Ok("t-ok"), Err("DeviceNotRegistered"), Err("InvalidCredentials")),
                _ => throw new HttpRequestException($"boom {a.Device.ExpoPushToken} {AccessToken}"),
            };
        });
        var sender = CreateSender(handler);

        await sender.RunOnceAsync(default);                 // ok / 撤销 / 凭据 / 跳过
        SeedPending("lk005", title: title, body: body);
        await sender.RunOnceAsync(default);                 // 网络异常
        _clock.Advance(TimeSpan.FromMinutes(16));
        await sender.RunOnceAsync(default);                 // 回执（含 error）
        await sender.CleanupAsync(default);

        var dump = _logs.Dump();
        foreach (var forbidden in new[]
                 {
                     a.Device.ExpoPushToken, b.Device.ExpoPushToken, c.Device.ExpoPushToken, "ExponentPushToken", "PushToken[",
                     title, body, AccessToken, "SECRET-BODY-TEXT", "boom", "Authorization", "Bearer",
                 })
        {
            Assert.DoesNotContain(forbidden, dump, StringComparison.Ordinal);
        }

        Assert.NotEmpty(_logs.Entries);
        Assert.All(_logs.Entries, entry => Assert.Null(entry.Exception));
        Assert.Contains(_logs.Entries, entry => entry.Message.Contains("requests=", StringComparison.Ordinal));
    }

    public void Dispose() => _database.Dispose();
}
