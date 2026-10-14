using System.Net;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Api.Services.Background;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 后台任务：双层开关（ScheduledTasks:Enabled 与 Push:Expo:Enabled）、失败限频日志、每小时清理。
/// 注意：既有的人脸 / 改价 / 新品后台任务并不检查 ScheduledTasks:Enabled，本任务自己检查，这里用测试固定下来。
/// </summary>
public sealed class ExpoPushBackgroundServiceTests : IDisposable
{
    private readonly PushTestDatabase _database = new();
    private readonly PushTestClock _clock = new(PushTestDatabase.Start);
    private readonly AuditCapturingLoggerProvider _logs = new();
    private readonly PushOptionsMonitor _options = new(new ExpoPushOptions { Enabled = true, AccessToken = "tok-secret-123" });
    private readonly PushStubHandler _handler = new(PushStubHandler.OkTicketsFor);

    private sealed class CountingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {
        public int Created { get; private set; }

        public IServiceScope CreateScope()
        {
            Created++;
            return inner.CreateScope();
        }
    }

    private (ExpoPushBackgroundService Service, CountingScopeFactory Scopes) Create(bool scheduledTasksEnabled = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton<IOptionsMonitor<ExpoPushOptions>>(_options);
        services.AddSingleton<IHttpClientFactory>(new PushStubHttpClientFactory(_handler));
        services.AddScoped(_ => _database.Context);
        services.AddSingleton(_logs.CreateLogger<ExpoPushSender>());
        services.AddScoped<ExpoPushSender>();
        var provider = services.BuildServiceProvider();
        var scopes = new CountingScopeFactory(provider.GetRequiredService<IServiceScopeFactory>());
        var service = new ExpoPushBackgroundService(
            scopes,
            _options,
            Options.Create(new ScheduledTaskOptions { Enabled = scheduledTasksEnabled }),
            _clock,
            _logs.CreateLogger<ExpoPushBackgroundService>())
        {
            StartupDelay = TimeSpan.Zero,
        };
        return (service, scopes);
    }

    private AppPushDelivery SeedPending(string seed)
    {
        var notification = _database.SeedNotification();
        var device = _database.SeedDevice("user-a", PushTestTokens.Make(seed));
        return _database.SeedDelivery(notification.NotificationId, device);
    }

    [Fact]
    public async Task 总开关ScheduledTasks关闭_任务启动即退出_不进循环_不创建作用域_不发请求()
    {
        var delivery = SeedPending("bg001");
        var (service, scopes) = Create(scheduledTasksEnabled: false);

        await service.StartAsync(default);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));   // 立即完成，而不是挂起在循环里
        await service.StopAsync(default);

        Assert.Equal(0, scopes.Created);
        Assert.Empty(_handler.Requests);
        Assert.Equal(AppPushDeliveryStates.Pending, _database.Delivery(delivery.DeliveryId).State);
        Assert.Contains(_logs.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains("ScheduledTasks:Enabled=false", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 推送开关关闭_节拍直接返回_不创建作用域_不查库_不发请求()
    {
        SeedPending("bg002");
        _options.Value = new ExpoPushOptions { Enabled = false };
        // 连表都删掉：真正查库就会抛异常。
        _database.Db.Ado.ExecuteCommand("DROP TABLE AppPushDelivery");
        var (service, scopes) = Create();

        var wait = await service.TickAsync(default);

        Assert.Equal(TimeSpan.FromSeconds(5), wait);
        Assert.Equal(0, scopes.Created);
        Assert.Empty(_handler.Requests);
        Assert.Empty(_logs.Entries);
    }

    [Fact]
    public async Task 两个开关都开_一个节拍发出待发送记录_等待一个轮询间隔()
    {
        var delivery = SeedPending("bg003");
        _options.Value = new ExpoPushOptions { Enabled = true, PollIntervalSeconds = 7 };
        var (service, scopes) = Create();

        var wait = await service.TickAsync(default);

        Assert.Equal(TimeSpan.FromSeconds(7), wait);
        Assert.Equal(1, scopes.Created);
        Assert.Single(_handler.SendRequests);
        Assert.Equal(AppPushDeliveryStates.Sent, _database.Delivery(delivery.DeliveryId).State);
    }

    [Fact]
    public async Task 迁移未执行表不存在_节拍不抛异常_至少等30秒_同类失败10分钟内只记一条日志且不含异常消息()
    {
        _database.Db.Ado.ExecuteCommand("DROP TABLE AppPushDelivery");
        var (service, _) = Create();

        var first = await service.TickAsync(default);
        var second = await service.TickAsync(default);
        _clock.Advance(TimeSpan.FromMinutes(9));
        var third = await service.TickAsync(default);

        Assert.All([first, second, third], wait => Assert.True(wait >= TimeSpan.FromSeconds(30)));
        var errors = _logs.Entries.Where(entry => entry.Level == LogLevel.Error).ToList();
        var error = Assert.Single(errors);
        Assert.Contains("schema-missing", _logs.Dump(), StringComparison.Ordinal);
        Assert.Contains("20261017.001-app-push", _logs.Dump(), StringComparison.Ordinal);
        Assert.Null(error.Exception);
        Assert.DoesNotContain("no such table", _logs.Dump(), StringComparison.OrdinalIgnoreCase);

        _clock.Advance(TimeSpan.FromMinutes(2));   // 超过 10 分钟
        await service.TickAsync(default);
        Assert.Equal(2, _logs.Entries.Count(entry => entry.Level == LogLevel.Error));
    }

    [Fact]
    public async Task 配置错误_节拍记一条配置类Error_不含配置值_不发请求()
    {
        SeedPending("bg004");
        _options.Value = new ExpoPushOptions { Enabled = true, Endpoint = "http://exp.host/plain", AccessToken = "tok-secret-123" };
        var (service, _) = Create();

        var wait = await service.TickAsync(default);
        await service.TickAsync(default);

        Assert.True(wait >= TimeSpan.FromSeconds(30));
        Assert.Empty(_handler.Requests);
        var error = Assert.Single(_logs.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("configuration", _logs.Dump(), StringComparison.Ordinal);
        Assert.DoesNotContain("tok-secret-123", _logs.Dump(), StringComparison.Ordinal);
        Assert.Null(error.Exception);
    }

    [Fact]
    public async Task 节拍中的调用方取消_不记错误日志()
    {
        SeedPending("bg005");
        var (service, _) = Create();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await service.TickAsync(cts.Token);

        Assert.DoesNotContain(_logs.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task 清理每小时一次_首个节拍就跑_一小时内不重复_满一小时再跑()
    {
        var device = _database.SeedDevice("user-a", PushTestTokens.Make("bg006"));
        AppPushDelivery SeedOld() => _database.SeedDelivery(
            _database.SeedNotification().NotificationId, device, state: AppPushDeliveryStates.Delivered, createdAt: _clock.Now.AddDays(-20));
        var first = SeedOld();
        var (service, _) = Create();

        await service.TickAsync(default);
        Assert.Empty(_database.Db.Queryable<AppPushDelivery>().Where(row => row.DeliveryId == first.DeliveryId).ToList());

        var second = SeedOld();
        _clock.Advance(TimeSpan.FromMinutes(59));
        await service.TickAsync(default);
        Assert.NotEmpty(_database.Db.Queryable<AppPushDelivery>().Where(row => row.DeliveryId == second.DeliveryId).ToList());

        _clock.Advance(TimeSpan.FromMinutes(2));
        await service.TickAsync(default);
        Assert.Empty(_database.Db.Queryable<AppPushDelivery>().Where(row => row.DeliveryId == second.DeliveryId).ToList());
    }

    [Fact]
    public async Task 启动后循环真的在跑_关停时干净退出()
    {
        var delivery = SeedPending("bg007");
        _options.Value = new ExpoPushOptions { Enabled = true, PollIntervalSeconds = 1 };
        var (service, _) = Create();

        // 循环在后台线程里用主连接写库；SqlSugar 客户端不是线程安全的，所以轮询等待用另一个独立连接读。
        var reader = _database.NewConnectionContext().Db;
        await service.StartAsync(default);
        await ExpoPushWait.UntilAsync(() =>
            reader.Queryable<AppPushDelivery>().Where(row => row.DeliveryId == delivery.DeliveryId).First().State == AppPushDeliveryStates.Sent);
        await service.StopAsync(default);

        Assert.Equal(AppPushDeliveryStates.Sent, _database.Delivery(delivery.DeliveryId).State);
        Assert.True(service.ExecuteTask!.IsCompleted);
    }

    [Fact]
    public void 部署契约_Program注册了推送配置_设备服务_命名HttpClient_发送器与后台任务_超时30秒_不跟随重定向()
    {
        var program = File.ReadAllText(Path.Combine(ApiRoot(), "Program.cs"));

        Assert.Contains("Configure<BlazorApp.Api.Features.Notifications.ExpoPushOptions>", program, StringComparison.Ordinal);
        Assert.Contains("ExpoPushOptions.SectionName", program, StringComparison.Ordinal);
        Assert.Contains("Notifications.IAppPushDeviceService,", program, StringComparison.Ordinal);
        Assert.Contains("BlazorApp.Api.Features.Notifications.ExpoPushSender.HttpClientName", program, StringComparison.Ordinal);
        Assert.Contains("client.Timeout = TimeSpan.FromSeconds(30)", program, StringComparison.Ordinal);
        Assert.Contains("AllowAutoRedirect = false", program, StringComparison.Ordinal);
        Assert.Contains("AddScoped<BlazorApp.Api.Features.Notifications.ExpoPushSender>()", program, StringComparison.Ordinal);
        Assert.Contains("AddHostedService<BlazorApp.Api.Features.Notifications.ExpoPushBackgroundService>()", program, StringComparison.Ordinal);
        // 命名客户端的名字是常量，发送器与注册处共用。
        Assert.Equal("expo-push", ExpoPushSender.HttpClientName);
    }

    private static string ApiRoot([System.Runtime.CompilerServices.CallerFilePath] string testFilePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", "BlazorApp.Api"));

    public void Dispose() => _database.Dispose();
}

internal static class ExpoPushWait
{
    /// <summary>轮询等待条件成立（真实时间，最多 10 秒）。假时钟下 Task.Delay 不会推进，所以启动循环的用例让启动延迟为 0。</summary>
    public static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("条件在 10 秒内未成立");
            }

            await Task.Delay(20);
        }
    }
}
