using BlazorApp.Api.Data;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 通知发布器接入推送发件箱：开关、去重、无接收人、无设备、设备表不存在时都不写发件箱；
/// 有设备时与通知、接收人在同一个事务里写入，且 (通知 × 设备) 唯一。
/// </summary>
public sealed class ExpoPushPublisherTests : IDisposable
{
    private const string UserA = "user-a";
    private const string UserB = "user-b";
    private const string UserC = "user-c";

    private readonly PushTestDatabase _database = new();
    private readonly PushTestClock _clock = new(PushTestDatabase.Start);
    private readonly AuditCapturingLoggerProvider _logs = new();
    private readonly PushOptionsMonitor _options = new(new ExpoPushOptions { Enabled = true });

    public ExpoPushPublisherTests()
    {
        _database.SeedUser(UserA);
        _database.SeedUser(UserB);
        _database.SeedUser(UserC);
    }

    private NotificationPublisher CreatePublisher(bool withOptions = true)
    {
        var context = _database.Context;
        return new NotificationPublisher(
            context,
            new NotificationRecipientResolver(context, Mock.Of<IRoleService>(), NullLogger<NotificationRecipientResolver>.Instance),
            new NoopDispatcher(),
            _clock,
            _logs.CreateLogger<NotificationPublisher>(),
            withOptions ? _options : null);
    }

    private static NotificationPublishRequest Request(NotificationAudience audience, string type = "store.price_updates") => new()
    {
        Type = type,
        Title = "Price updates for your store",
        Body = "12 products need price updates.",
        Audience = audience,
        Data = new Dictionary<string, string> { ["count"] = "12" },
    };

    private List<AppPushDelivery> Deliveries() => _database.Db.Queryable<AppPushDelivery>().ToList();

    [Fact]
    public async Task 开关关闭_有设备也不写发件箱_行为与没有推送功能时一致()
    {
        _database.SeedDevice(UserA, PushTestTokens.Make("a1"));
        _options.Value = new ExpoPushOptions { Enabled = false };

        var result = await CreatePublisher().PublishAsync(Request(NotificationAudience.ForUsers(UserA)));

        Assert.NotNull(result.NotificationId);
        Assert.Equal(1, result.RecipientCount);
        Assert.Empty(Deliveries());
    }

    [Fact]
    public async Task 构造时没有注入推送配置_等同关闭_现有六处测试构造方式不受影响()
    {
        _database.SeedDevice(UserA, PushTestTokens.Make("a2"));

        // 不传第六个可选参数（既有测试的构造方式）。
        var result = await CreatePublisher(withOptions: false).PublishAsync(Request(NotificationAudience.ForUsers(UserA)));

        Assert.NotNull(result.NotificationId);
        Assert.Empty(Deliveries());
    }

    [Fact]
    public void DI解析_注册了推送配置就注入_没注册也能解析并回退默认值()
    {
        static ServiceProvider Build(bool withOptions)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(PushTestDatabase.CreateSqlSugarContext(new SqlSugar.SqlSugarClient(new SqlSugar.ConnectionConfig
            {
                ConnectionString = "Data Source=:memory:",
                DbType = SqlSugar.DbType.Sqlite,
            })));
            services.AddSingleton(Mock.Of<INotificationRecipientResolver>());
            services.AddSingleton<INotificationRealtimeDispatcher>(new NoopDispatcher());
            services.AddSingleton(TimeProvider.System);
            if (withOptions)
            {
                services.Configure<ExpoPushOptions>(options => options.Enabled = true);
            }

            services.AddScoped<INotificationPublisher, NotificationPublisher>();
            return services.BuildServiceProvider();
        }

        using (var with = Build(withOptions: true))
        using (var scope = with.CreateScope())
        {
            Assert.IsType<NotificationPublisher>(scope.ServiceProvider.GetRequiredService<INotificationPublisher>());
        }

        using (var without = Build(withOptions: false))
        using (var scope = without.CreateScope())
        {
            Assert.IsType<NotificationPublisher>(scope.ServiceProvider.GetRequiredService<INotificationPublisher>());
        }

        // 同样验证 ActivatorUtilities 路径（部分宿主用它构造）。
        var activated = ActivatorUtilities.CreateInstance<NotificationPublisher>(
            new ServiceCollection().AddLogging().BuildServiceProvider(),
            _database.Context,
            Mock.Of<INotificationRecipientResolver>(),
            new NoopDispatcher(),
            TimeProvider.System);
        Assert.NotNull(activated);
    }

    [Fact]
    public async Task 开启且有活动设备_为每个接收人的每个活动设备写一行Pending_撤销的设备与无设备的人不写()
    {
        var a1 = _database.SeedDevice(UserA, PushTestTokens.Make("a1"));
        var a2 = _database.SeedDevice(UserA, PushTestTokens.Make("a2"), platform: "android");
        _database.SeedDevice(UserB, PushTestTokens.Make("b1"), revokedAt: PushTestDatabase.Start, revokeReason: "logout");   // 已撤销
        // UserC 没有设备。

        var result = await CreatePublisher().PublishAsync(Request(NotificationAudience.ForUsers(UserA, UserB, UserC)));

        Assert.Equal(3, result.RecipientCount);   // 站内消息三个人都有
        var rows = Deliveries();
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { a1.PushDeviceId, a2.PushDeviceId }.Order(StringComparer.Ordinal), rows.Select(row => row.PushDeviceId).Order(StringComparer.Ordinal));
        Assert.All(rows, row =>
        {
            Assert.Equal(result.NotificationId, row.NotificationId);
            Assert.Equal(UserA, row.UserGUID);
            Assert.Equal(AppPushDeliveryStates.Pending, row.State);
            Assert.Equal(0, row.Attempts);
            Assert.Equal(_clock.Now, row.NextAttemptAtUtc);
            Assert.Equal(_clock.Now, row.CreatedAtUtc);
            Assert.Null(row.LockedUntilUtc);
            Assert.Null(row.TicketId);
            Assert.Equal(32, row.DeliveryId.Length);
        });
    }

    [Fact]
    public async Task 发件箱行与通知_接收人在同一事务里_发件箱写入失败则通知一并回滚()
    {
        _database.SeedDevice(UserA, PushTestTokens.Make("a3"));
        // 让发件箱的 INSERT 必然失败：整个事务（通知 + 接收人 + 发件箱）都必须回滚，不能留下「有通知没推送」的半截数据。
        _database.Db.Ado.ExecuteCommand("CREATE TRIGGER trg_fail BEFORE INSERT ON AppPushDelivery BEGIN SELECT RAISE(ABORT, 'outbox down'); END");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            CreatePublisher().PublishAsync(Request(NotificationAudience.ForUsers(UserA))));

        Assert.Equal(0, await _database.Db.Queryable<AppNotification>().CountAsync());
        Assert.Equal(0, await _database.Db.Queryable<AppNotificationRecipient>().CountAsync());
        Assert.Empty(Deliveries());
    }

    [Fact]
    public async Task 幂等键命中_不再写发件箱()
    {
        _database.SeedDevice(UserA, PushTestTokens.Make("a4"));
        var publisher = CreatePublisher();
        var request = Request(NotificationAudience.ForUsers(UserA)) with { DedupeKey = "price:1013:20261017" };

        var first = await publisher.PublishAsync(request);
        var second = await publisher.PublishAsync(request);

        Assert.False(first.Deduplicated);
        Assert.True(second.Deduplicated);
        Assert.Single(Deliveries());
    }

    [Fact]
    public async Task 没有接收人_不产生通知也不写发件箱()
    {
        _database.SeedDevice(UserA, PushTestTokens.Make("a5"));
        _database.SeedUser("user-off", active: false);
        _database.SeedDevice("user-off", PushTestTokens.Make("off1"));

        var result = await CreatePublisher().PublishAsync(Request(NotificationAudience.ForUsers("user-off", "missing-user")));

        Assert.Null(result.NotificationId);
        Assert.Empty(Deliveries());
        Assert.Equal(0, await _database.Db.Queryable<AppNotification>().CountAsync());
    }

    [Fact]
    public async Task 设备表不存在_通知照常发布_不写发件箱_只记一条Warning且不含异常消息()
    {
        _database.Db.Ado.ExecuteCommand("DROP TABLE AppPushDevice");

        var result = await CreatePublisher().PublishAsync(Request(NotificationAudience.ForUsers(UserA)));

        Assert.NotNull(result.NotificationId);
        Assert.Equal(1, await _database.Db.Queryable<AppNotification>().CountAsync());
        Assert.Equal(1, await _database.Db.Queryable<AppNotificationRecipient>().CountAsync());
        Assert.Empty(Deliveries());

        var warnings = _logs.Entries.Where(entry => entry.Level == LogLevel.Warning).ToList();
        var warning = Assert.Single(warnings);
        Assert.Null(warning.Exception);
        Assert.Equal(true, warning.State["SchemaMissing"]);
        Assert.DoesNotContain("no such table", _logs.Dump(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task 设备表不存在但开关关闭_根本不查设备表_也没有Warning()
    {
        _database.Db.Ado.ExecuteCommand("DROP TABLE AppPushDevice");
        _options.Value = new ExpoPushOptions { Enabled = false };

        var result = await CreatePublisher().PublishAsync(Request(NotificationAudience.ForUsers(UserA)));

        Assert.NotNull(result.NotificationId);
        Assert.DoesNotContain(_logs.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task 通知发布时已过期_不写发件箱()
    {
        _database.SeedDevice(UserA, PushTestTokens.Make("a6"));

        var result = await CreatePublisher().PublishAsync(Request(NotificationAudience.ForUsers(UserA)) with
        {
            ExpiresAtUtc = _clock.Now.AddMinutes(-1),
        });

        Assert.NotNull(result.NotificationId);
        Assert.Empty(Deliveries());
    }

    [Fact]
    public async Task 同一通知对同一设备只能入箱一次_唯一索引兜底()
    {
        var device = _database.SeedDevice(UserA, PushTestTokens.Make("a7"));
        var result = await CreatePublisher().PublishAsync(Request(NotificationAudience.ForUsers(UserA)));

        var duplicate = new AppPushDelivery
        {
            DeliveryId = Guid.NewGuid().ToString("N"),
            NotificationId = result.NotificationId!,
            PushDeviceId = device.PushDeviceId,
            UserGUID = UserA,
            NextAttemptAtUtc = _clock.Now,
            CreatedAtUtc = _clock.Now,
        };

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => _database.Db.Insertable(duplicate).ExecuteCommandAsync());
        Assert.True(SqlErrorClassifier.IsUniqueViolation(ex));
        Assert.Single(Deliveries());
    }

    [Fact]
    public void 活动令牌过滤唯一索引_第二个活动行冲突_撤销后可再建()
    {
        var token = PushTestTokens.Make("u1");
        var first = _database.SeedDevice(UserA, token);

        var ex = Assert.ThrowsAny<Exception>(() => _database.SeedDevice(UserB, token));
        Assert.True(SqlErrorClassifier.IsUniqueViolation(ex));

        var revokedAt = _clock.Now;
        _database.Db.Updateable<AppPushDevice>()
            .SetColumns(device => device.RevokedAtUtc == revokedAt)
            .Where(device => device.PushDeviceId == first.PushDeviceId)
            .ExecuteCommand();
        _database.SeedDevice(UserB, token);   // 旧行已撤销，不再占用唯一性

        Assert.Equal(2, _database.Db.Queryable<AppPushDevice>().Count(device => device.ExpoPushToken == token));
    }

    [Fact]
    public async Task 接收人很多时设备查询分块_全部设备都入箱()
    {
        var users = Enumerable.Range(0, 1200).Select(i => $"bulk-{i:D4}").ToList();
        _database.Db.Insertable(users.Select(user => new User { UserGUID = user, Username = user, IsActive = true }).ToList()).ExecuteCommand();
        _database.Db.Insertable(users.Select((user, index) => new AppPushDevice
        {
            PushDeviceId = Guid.NewGuid().ToString("N"),
            UserGUID = user,
            Platform = "ios",
            ExpoPushToken = PushTestTokens.Make($"b{index:D5}"),
            CreatedAtUtc = _clock.Now,
            LastSeenAtUtc = _clock.Now,
        }).ToList()).ExecuteCommand();

        var result = await CreatePublisher().PublishAsync(Request(NotificationAudience.ForUsers(users.ToArray())));

        Assert.Equal(1200, result.RecipientCount);
        Assert.Equal(1200, Deliveries().Count);
    }

    public void Dispose() => _database.Dispose();

    private sealed class NoopDispatcher : INotificationRealtimeDispatcher
    {
        public Task SendToUsersAsync(
            IReadOnlyCollection<string> userGuids,
            NotificationDto notification,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
