using System.Text;
using System.Text.Json;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 推送内容的纯函数测试：隐私规则（敏感类型只发通用文案、data 不带 DataJson）、data 大小上限、ttl / 优先级、退避表，
/// 以及配置绑定与 compose 部署契约。这些函数不碰数据库与网络。
/// </summary>
public sealed class ExpoPushContentTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 1, 0, 0, DateTimeKind.Utc);
    private const string Token = "ExponentPushToken[abcdefghijklmnopqrstuv]";

    private static AppNotification Notification(
        string type,
        string title = "原始标题 Store 1013",
        string body = "原始正文 张三 2026-10-20",
        string? dataJson = "{\"taskId\":\"T-1\",\"storeCode\":\"1013\"}",
        int priority = AppNotificationPriorities.Normal,
        DateTime? expiresAt = null) => new()
    {
        NotificationId = "0123456789abcdef0123456789abcdef",
        Type = type,
        Title = title,
        Body = body,
        DataJson = dataJson,
        Priority = priority,
        ExpiresAtUtc = expiresAt,
        CreatedAtUtc = Now,
        CreatedBy = "test",
    };

    // ---------- 隐私：敏感类型 ----------

    [Theory]
    [InlineData(AppNotificationTypes.FaceUnwelcomeHit)]
    [InlineData(AppNotificationTypes.FaceUnwelcomeExpiring)]
    [InlineData(AppNotificationTypes.ApprovalSubmitted)]
    [InlineData(AppNotificationTypes.ApprovalDecided)]
    [InlineData("FACE.unknown-future-type")]
    public void 敏感类型_推送只发通用文案_data只有notificationId与type_不合并DataJson(string type)
    {
        var message = ExpoPushContent.Build(Notification(type), Token, new ExpoPushOptions(), Now);

        Assert.Equal("HB", message.Title);
        Assert.Equal("You have a new notification. Open the app to view it.", message.Body);
        Assert.Equal(2, message.Data.Count);
        Assert.Equal("0123456789abcdef0123456789abcdef", message.Data["notificationId"]);
        Assert.Equal(type, message.Data["type"]);

        // 整条序列化后的请求体里不得出现原始文案与 DataJson 里的任何标识。
        var json = ExpoPushContent.SerializeSendBody([message]);
        foreach (var forbidden in new[] { "原始标题", "原始正文", "张三", "1013", "T-1", "taskId", "storeCode", "2026-10-20" })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(AppNotificationTypes.StorePriceUpdates)]
    [InlineData(AppNotificationTypes.ContainerNewProducts)]
    [InlineData(AppNotificationTypes.SupplierPreorderReminder)]
    [InlineData(AppNotificationTypes.Test)]
    public void 非敏感类型_推送用通知原本的标题正文_data合并DataJson(string type)
    {
        var message = ExpoPushContent.Build(
            Notification(type, "Price updates for your store", "12 products need price updates.", "{\"count\":\"12\",\"date\":\"2026-10-17\"}"),
            Token,
            new ExpoPushOptions(),
            Now);

        Assert.Equal("Price updates for your store", message.Title);
        Assert.Equal("12 products need price updates.", message.Body);
        Assert.Equal("12", message.Data["count"]);
        Assert.Equal("2026-10-17", message.Data["date"]);
        Assert.Equal("0123456789abcdef0123456789abcdef", message.Data["notificationId"]);
        Assert.Equal(type, message.Data["type"]);
    }

    [Fact]
    public void 非敏感类型_DataJson里的同名键不能覆盖notificationId与type()
    {
        var message = ExpoPushContent.Build(
            Notification("store.price_updates", dataJson: "{\"notificationId\":\"evil\",\"type\":\"face.x\",\"k\":\"v\"}"),
            Token,
            new ExpoPushOptions(),
            Now);

        Assert.Equal("0123456789abcdef0123456789abcdef", message.Data["notificationId"]);
        Assert.Equal("store.price_updates", message.Data["type"]);
        Assert.Equal("v", message.Data["k"]);
    }

    [Fact]
    public void 非敏感类型_data序列化超过4KB_只保留notificationId与type()
    {
        var big = JsonSerializer.Serialize(new Dictionary<string, string> { ["blob"] = new string('a', 4200), ["k"] = "v" });
        var message = ExpoPushContent.Build(Notification("store.price_updates", dataJson: big), Token, new ExpoPushOptions(), Now);

        Assert.Equal(2, message.Data.Count);
        Assert.False(message.Data.ContainsKey("blob"));
    }

    [Fact]
    public void 非敏感类型_data刚好不超过4KB时保留_按UTF8字节计()
    {
        // 键 + 值的 UTF-8 字节数是判断依据：中文每字 3 字节，1300 个汉字已经约 3.9KB，不能按字符数算。
        var fits = JsonSerializer.Serialize(new Dictionary<string, string> { ["note"] = new string('价', 1000) });
        var tooBig = JsonSerializer.Serialize(new Dictionary<string, string> { ["note"] = new string('价', 1400) });

        var kept = ExpoPushContent.Build(Notification("store.price_updates", dataJson: fits), Token, new ExpoPushOptions(), Now);
        var dropped = ExpoPushContent.Build(Notification("store.price_updates", dataJson: tooBig), Token, new ExpoPushOptions(), Now);

        Assert.True(kept.Data.ContainsKey("note"));
        Assert.False(dropped.Data.ContainsKey("note"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"a\":1}")]
    public void 非敏感类型_DataJson缺失或损坏_只保留notificationId与type_不抛异常(string? dataJson)
    {
        var message = ExpoPushContent.Build(Notification("store.price_updates", dataJson: dataJson), Token, new ExpoPushOptions(), Now);

        Assert.Equal(2, message.Data.Count);
    }

    // ---------- 敏感前缀配置 ----------

    [Fact]
    public void 敏感前缀_未配置用默认_配置后完全替换_空数组表示没有敏感类型_空白前缀被忽略_不区分大小写()
    {
        Assert.True(new ExpoPushOptions().IsSensitiveType("face.unwelcome-hit"));
        Assert.True(new ExpoPushOptions().IsSensitiveType("approval.decided"));
        Assert.False(new ExpoPushOptions().IsSensitiveType("store.price_updates"));
        Assert.False(new ExpoPushOptions().IsSensitiveType(""));

        var custom = new ExpoPushOptions { SensitiveTypePrefixes = ["store."] };
        Assert.True(custom.IsSensitiveType("STORE.price_updates"));
        Assert.False(custom.IsSensitiveType("face.unwelcome-hit"));   // 替换而不是追加

        var none = new ExpoPushOptions { SensitiveTypePrefixes = [] };
        Assert.False(none.IsSensitiveType("face.unwelcome-hit"));

        // 空白前缀不能变成「匹配所有类型」。
        var blank = new ExpoPushOptions { SensitiveTypePrefixes = ["", "  "] };
        Assert.False(blank.IsSensitiveType("face.unwelcome-hit"));
    }

    // ---------- 优先级与 ttl ----------

    [Fact]
    public void 优先级_High映射high_其余映射default()
    {
        Assert.Equal("high", ExpoPushContent.ResolvePriority(AppNotificationPriorities.High));
        Assert.Equal("default", ExpoPushContent.ResolvePriority(AppNotificationPriorities.Normal));
        var message = ExpoPushContent.Build(Notification("system.test", priority: AppNotificationPriorities.High), Token, new ExpoPushOptions(), Now);
        Assert.Equal("high", message.Priority);
    }

    [Theory]
    [InlineData(null, 86400)]        // 无过期时间 → 24 小时
    [InlineData(3600, 3600)]         // 剩余 1 小时
    [InlineData(172800, 86400)]      // 剩余 2 天 → 封顶 24 小时
    [InlineData(0, 1)]               // 已到期的兜底：至少 1 秒，不传 0
    [InlineData(-60, 1)]
    public void ttl_按剩余秒数_上限24小时_至少1秒(int? remainingSeconds, int expected)
    {
        DateTime? expiresAt = remainingSeconds is int seconds ? Now.AddSeconds(seconds) : null;

        Assert.Equal(expected, ExpoPushContent.ComputeTtlSeconds(expiresAt, Now));
        Assert.Equal(expected, ExpoPushContent.Build(Notification("system.test", expiresAt: expiresAt), Token, new ExpoPushOptions(), Now).Ttl);
    }

    [Fact]
    public void 消息JSON形状_字段名与固定值符合Expo协议()
    {
        var message = ExpoPushContent.Build(
            Notification("store.price_updates", "Price updates", "12 products", "{\"count\":\"12\"}"),
            Token,
            new ExpoPushOptions(),
            Now);

        using var document = JsonDocument.Parse(ExpoPushContent.SerializeSendBody([message, message]));
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Equal(2, document.RootElement.GetArrayLength());
        var first = document.RootElement[0];
        Assert.Equal(Token, first.GetProperty("to").GetString());
        Assert.Equal("Price updates", first.GetProperty("title").GetString());
        Assert.Equal("12 products", first.GetProperty("body").GetString());
        Assert.Equal("default", first.GetProperty("sound").GetString());
        Assert.Equal("default", first.GetProperty("channelId").GetString());
        Assert.Equal("default", first.GetProperty("priority").GetString());
        Assert.Equal(86400, first.GetProperty("ttl").GetInt32());
        Assert.Equal("12", first.GetProperty("data").GetProperty("count").GetString());
        Assert.Equal(8, first.EnumerateObject().Count());   // 没有多余字段

        using var receipts = JsonDocument.Parse(ExpoPushContent.SerializeReceiptsBody(["a", "b"]));
        Assert.Equal(new[] { "a", "b" }, receipts.RootElement.GetProperty("ids").EnumerateArray().Select(id => id.GetString()!).ToArray());
    }

    // ---------- 退避表 ----------

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 30)]
    [InlineData(3, 120)]
    [InlineData(4, 600)]
    [InlineData(5, 3600)]
    [InlineData(6, 3600)]     // 超过表长按最后一项
    [InlineData(99, 3600)]
    [InlineData(0, 5)]        // 防御：小于 1 按第 1 项
    public void 退避表_第n次失败等第n项_5秒30秒2分钟10分钟1小时(int failureCount, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), ExpoPushBackoff.Delay(failureCount, 0.5));
    }

    [Fact]
    public void 退避抖动_在基础时间的正负10百分比内()
    {
        var low = ExpoPushBackoff.Delay(3, 0);
        var high = ExpoPushBackoff.Delay(3, 0.999999);

        Assert.Equal(TimeSpan.FromSeconds(108), low);
        Assert.True(high > TimeSpan.FromSeconds(131.9) && high <= TimeSpan.FromSeconds(132));
        for (var i = 0; i < 100; i++)
        {
            var delay = ExpoPushBackoff.Delay(4, Random.Shared.NextDouble());
            Assert.InRange(delay.TotalSeconds, 540, 660);
        }
    }

    // ---------- 配置绑定 ----------

    [Fact]
    public void 配置_默认值符合约定_默认关闭()
    {
        var options = new ExpoPushOptions();

        Assert.False(options.Enabled);
        Assert.Equal("https://exp.host/--/api/v2/push", options.Endpoint);
        Assert.Null(options.AccessToken);
        Assert.Equal(5, options.MaxAttempts);
        Assert.Equal(100, options.BatchSize);
        Assert.Equal(15, options.ReceiptDelayMinutes);
        Assert.Equal(14, options.DeliveryRetentionDays);
        Assert.Equal(5, options.PollIntervalSeconds);
        Assert.Equal(24, options.StaleAfterHours);
        Assert.Equal(["face.", "approval."], ExpoPushOptions.DefaultSensitiveTypePrefixes);
    }

    [Fact]
    public void 配置_带下限的读取口径_批大小不超过Expo上限100_其余最小为1()
    {
        var options = new ExpoPushOptions
        {
            BatchSize = 500,
            MaxAttempts = 0,
            ReceiptDelayMinutes = 0,
            DeliveryRetentionDays = -3,
            PollIntervalSeconds = 0,
            StaleAfterHours = 0,
        };

        Assert.Equal(100, options.EffectiveBatchSize);
        Assert.Equal(1, new ExpoPushOptions { BatchSize = 0 }.EffectiveBatchSize);
        Assert.Equal(1, options.EffectiveMaxAttempts);
        Assert.Equal(1, options.EffectiveReceiptDelayMinutes);
        Assert.Equal(1, options.EffectiveRetentionDays);
        Assert.Equal(1, options.EffectivePollIntervalSeconds);
        Assert.Equal(1, options.EffectiveStaleAfterHours);
    }

    [Fact]
    public void 配置绑定_环境变量式键映射到PushExpo节_空AccessToken等同未配置_数组配置替换默认前缀()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Push:Expo:Enabled"] = "true",
            ["Push:Expo:AccessToken"] = "",
            ["Push:Expo:MaxAttempts"] = "3",
            ["Push:Expo:SensitiveTypePrefixes:0"] = "x.",
        }).Build();
        var options = new ExpoPushOptions();
        configuration.GetSection(ExpoPushOptions.SectionName).Bind(options);

        Assert.True(options.Enabled);
        Assert.Equal(3, options.MaxAttempts);
        Assert.True(string.IsNullOrWhiteSpace(options.AccessToken));
        Assert.Equal(new[] { "x." }, options.SensitiveTypePrefixes!);
        Assert.False(options.IsSensitiveType("face.unwelcome-hit"));   // 配置后是替换，不是追加
        Assert.True(options.IsSensitiveType("x.y"));
    }

    [Fact]
    public void 配置_ToString不带出AccessToken()
    {
        var options = new ExpoPushOptions { Enabled = true, AccessToken = "super-secret-access-token" };

        Assert.DoesNotContain("super-secret-access-token", options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-access-token", $"{options}", StringComparison.Ordinal);
    }

    [Fact]
    public void 示例配置文件_可绑定_默认关闭_不含AccessToken()
    {
        var path = Path.Combine(ApiRoot(), "appsettings.PushExpo.example.json");
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("AccessToken", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Development", Path.GetFileName(path), StringComparison.Ordinal);

        var configuration = new ConfigurationBuilder().AddJsonFile(path).Build();
        var options = new ExpoPushOptions();
        configuration.GetSection(ExpoPushOptions.SectionName).Bind(options);

        Assert.False(options.Enabled);
        Assert.Equal(new ExpoPushOptions().Endpoint, options.Endpoint);
        Assert.Equal(5, options.MaxAttempts);
        Assert.Equal(100, options.BatchSize);
        Assert.Equal(new[] { "face.", "approval." }, options.SensitiveTypePrefixes!);
    }

    [Fact]
    public void 部署契约_compose里Push变量都带默认值_没有必填_默认关闭_AccessToken默认为空()
    {
        var compose = File.ReadAllText(Path.Combine(ApiRoot(), "..", "docker-compose.yml"));
        var lines = compose.Split('\n').Where(line => line.Contains("Push__Expo__", StringComparison.Ordinal) && !line.TrimStart().StartsWith('#')).ToList();

        Assert.Equal(2, lines.Count);
        foreach (var line in lines)
        {
            Assert.Matches(@"=\$\{EXPO_[A-Z_]+:-[^}]*\}\s*$", line);
            Assert.DoesNotContain(":?", line, StringComparison.Ordinal);
        }

        Assert.Contains("- Push__Expo__Enabled=${EXPO_PUSH_ENABLED:-false}", compose, StringComparison.Ordinal);
        Assert.Contains("- Push__Expo__AccessToken=${EXPO_ACCESS_TOKEN:-}", compose, StringComparison.Ordinal);
        // 其他既有映射原样保留（抽查人脸比对与中心日志各一条）。
        Assert.Contains("- FaceMatch__BackgroundEnabled=${FACE_MATCH_BACKGROUND_ENABLED:-true}", compose, StringComparison.Ordinal);
        Assert.Contains("- ApplicationLogging__Enabled=true", compose, StringComparison.Ordinal);
    }

    private static string ApiRoot([System.Runtime.CompilerServices.CallerFilePath] string testFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", "BlazorApp.Api"));
}
