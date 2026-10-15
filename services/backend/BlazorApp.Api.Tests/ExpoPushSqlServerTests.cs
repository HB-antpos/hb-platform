using System.Net;
using System.Text.Json;
using BlazorApp.Api.Data;
using BlazorApp.Api.Data.SchemaMigrations;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class PushSqlServerFactAttribute : FactAttribute
{
    public const string PrimaryVariable = "HB_TEST_SQLSERVER_CONNECTION";
    public const string FallbackVariable = "HBWEB_SCHEMA_SQLSERVER_TEST_CONNECTION";

    public PushSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Connection()))
        {
            Skip = $"未配置 {PrimaryVariable} / {FallbackVariable}，跳过 App 推送的真实 SQL Server 集成测试。";
        }
    }

    internal static string? Connection() =>
        FirstNonEmpty(Environment.GetEnvironmentVariable(PrimaryVariable), Environment.GetEnvironmentVariable(FallbackVariable));

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}

/// <summary>
/// App 推送在真实 SQL Server 上的回归（表用迁移 20261017.001 的真实建表脚本创建，含过滤唯一索引）：
/// 覆盖 SQLite 测不出的部分——条件 UPDATE / SetColumns 空值的 SQL 翻译、驱动层唯一冲突号（2601 / 2627）分类、
/// 并发领取的原子性、令牌并发注册，以及设备表不存在时的真实 208 错误。每个用例创建 GUID 命名的独立数据库，结束时删除。
/// </summary>
[Trait("Category", "SQL")]
public sealed class ExpoPushSqlServerTests
{
    private static readonly DateTime Start = new DateTime(2026, 10, 17, 1, 0, 0, 123, DateTimeKind.Utc).AddTicks(4567);

    private static string TokenOf(string seed) => PushTestTokens.Make(seed);

    [PushSqlServerFact]
    public async Task SQLServer_令牌注册_转移_空值列翻译_过滤唯一索引冲突被识别()
    {
        await using var server = await PushServerDatabase.CreateAsync();
        var clock = new PushTestClock(Start);
        var service = new AppPushDeviceService(server.NewContext(), clock, NullLogger<AppPushDeviceService>.Instance);
        var token = TokenOf("sql001");

        var first = await service.RegisterAsync("user-a", new PushDeviceRegisterRequestDto
        {
            ExpoPushToken = token, Platform = "ios", AppVersion = "1.0.7", RuntimeVersion = "1.0.7",
        });
        // 再次注册不带版本：SetColumns 的空值赋值必须翻译成 SET ... = NULL（而不是被忽略或报错）。
        clock.Advance(TimeSpan.FromSeconds(1));
        var refreshed = await service.RegisterAsync("user-a", new PushDeviceRegisterRequestDto { ExpoPushToken = token, Platform = "ios" });
        Assert.Equal(first.PushDeviceId, refreshed.PushDeviceId);
        Assert.Equal("1", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.AppPushDevice WHERE AppVersion IS NULL AND RuntimeVersion IS NULL"));

        // 换人：旧行 reassigned、新账号另起一行；过滤唯一索引保证活动行只有一条。
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = await service.RegisterAsync("user-b", new PushDeviceRegisterRequestDto { ExpoPushToken = token, Platform = "android" });
        Assert.NotEqual(first.PushDeviceId, second.PushDeviceId);
        Assert.Equal("2", await server.ScalarAsync($"SELECT COUNT(*) FROM dbo.AppPushDevice WHERE ExpoPushToken = N'{token}'"));
        Assert.Equal("1", await server.ScalarAsync($"SELECT COUNT(*) FROM dbo.AppPushDevice WHERE ExpoPushToken = N'{token}' AND RevokedAtUtc IS NULL"));
        Assert.Equal("reassigned", await server.ScalarAsync($"SELECT RevokeReason FROM dbo.AppPushDevice WHERE PushDeviceId = N'{first.PushDeviceId}'"));

        // 直接塞第二个活动行：驱动层唯一冲突号被分类为「唯一冲突」（SqlSugar 包装后仍可识别）。
        var duplicate = new AppPushDevice
        {
            PushDeviceId = Guid.NewGuid().ToString("N"), UserGUID = "user-c", Platform = "ios", ExpoPushToken = token,
            CreatedAtUtc = clock.Now, LastSeenAtUtc = clock.Now,
        };
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => server.NewClient().Insertable(duplicate).ExecuteCommandAsync());
        Assert.True(SqlErrorClassifier.IsUniqueViolation(ex));
        Assert.False(SqlErrorClassifier.IsMissingObject(ex));

        // 退出登录撤销（logout）；撤销别人的令牌不动。
        Assert.Equal(0, (await service.RevokeAsync("user-a", new PushDeviceRevokeRequestDto { ExpoPushToken = token })).Revoked);
        Assert.Equal(1, (await service.RevokeAsync("user-b", new PushDeviceRevokeRequestDto { ExpoPushToken = token })).Revoked);
        Assert.Equal("logout", await server.ScalarAsync($"SELECT RevokeReason FROM dbo.AppPushDevice WHERE PushDeviceId = N'{second.PushDeviceId}'"));
    }

    [PushSqlServerFact]
    public async Task SQLServer_同一令牌被三个账号同时注册_无异常_活动行恰好一条()
    {
        await using var server = await PushServerDatabase.CreateAsync();
        var clock = new PushTestClock(Start);

        for (var round = 0; round < 3; round++)
        {
            var token = TokenOf($"race{round:D2}");
            var tasks = new[] { "user-a", "user-b", "user-c" }.Select(user => Task.Run(() =>
                new AppPushDeviceService(server.NewContext(), clock, NullLogger<AppPushDeviceService>.Instance)
                    .RegisterAsync(user, new PushDeviceRegisterRequestDto { ExpoPushToken = token, Platform = "ios" })));

            await Task.WhenAll(tasks);   // 任何一个抛异常都会让用例失败

            Assert.Equal("1", await server.ScalarAsync($"SELECT COUNT(*) FROM dbo.AppPushDevice WHERE ExpoPushToken = N'{token}' AND RevokedAtUtc IS NULL"));
        }
    }

    [PushSqlServerFact]
    public async Task SQLServer_领取的原子性_多个实例并发领取同一批记录_每行只会被一个实例领到()
    {
        await using var server = await PushServerDatabase.CreateAsync();
        var clock = new PushTestClock(Start.AddMinutes(1));
        const int total = 200;
        var device = TokenOf("claim0");
        await server.ExecuteAsync($"""
            INSERT dbo.AppPushDevice (PushDeviceId, UserGUID, Platform, ExpoPushToken, CreatedAtUtc, LastSeenAtUtc)
            VALUES (N'dev0', N'user-a', N'ios', N'{device}', SYSUTCDATETIME(), SYSUTCDATETIME());
            """);
        await server.ExecuteAsync($"""
            INSERT dbo.AppPushDelivery (DeliveryId, NotificationId, PushDeviceId, UserGUID, State, Attempts, NextAttemptAtUtc, CreatedAtUtc)
            SELECT CONVERT(nvarchar(32), REPLACE(CONVERT(nvarchar(36), NEWID()), N'-', N'')), N'n' + CONVERT(nvarchar(10), v.number), N'dev0', N'user-a', 0, 0,
                   DATEADD(SECOND, -100, CONVERT(datetime2, '{clock.Now:yyyy-MM-dd HH:mm:ss.fffffff}', 121)),
                   CONVERT(datetime2, '{clock.Now.AddHours(-1):yyyy-MM-dd HH:mm:ss.fffffff}', 121)
            FROM (SELECT TOP ({total}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS number FROM sys.all_objects) v;
            """);
        Assert.Equal(total.ToString(), await server.ScalarAsync("SELECT COUNT(*) FROM dbo.AppPushDelivery"));

        var senders = Enumerable.Range(0, 8)
            .Select(_ => new ExpoPushSender(
                server.NewContext(),
                new PushStubHttpClientFactory(new PushStubHandler(PushStubHandler.OkTicketsFor)),
                new PushOptionsMonitor(new ExpoPushOptions { Enabled = true }),
                clock,
                NullLogger<ExpoPushSender>.Instance))
            .ToList();

        // 起跑线：8 个实例同时开始领取，尽量制造真实的争用。
        var gate = new TaskCompletionSource();
        var running = senders.Select(sender => Task.Run(async () =>
        {
            await gate.Task;
            var mine = new List<string>();
            while (true)
            {
                var batch = await sender.ClaimBatchAsync(15, clock.Now, default);
                if (batch.CandidateCount == 0)
                {
                    return mine;
                }

                mine.AddRange(batch.Claimed.Select(row => row.DeliveryId));
            }
        })).ToList();
        gate.SetResult();
        var claimedLists = await Task.WhenAll(running);

        var all = claimedLists.SelectMany(list => list).ToList();
        Assert.Equal(total, all.Count);              // 合起来恰好是全部（没有漏领）
        Assert.Equal(total, all.Distinct().Count()); // 没有任何一行被两个实例同时领到（没有重复领取）
        Assert.Equal(total.ToString(), await server.ScalarAsync("SELECT COUNT(*) FROM dbo.AppPushDelivery WHERE State = 0 AND LockedUntilUtc IS NOT NULL"));
    }

    [PushSqlServerFact]
    public async Task SQLServer_端到端_发布器同事务写发件箱_发送_回执_重试_设备失效_清理()
    {
        await using var server = await PushServerDatabase.CreateAsync();
        var clock = new PushTestClock(Start);
        var logs = new AuditCapturingLoggerProvider();
        var options = new PushOptionsMonitor(new ExpoPushOptions { Enabled = true, AccessToken = "sql-secret-token" });
        var seedDb = server.NewClient();
        seedDb.Insertable(new List<User>
        {
            new() { UserGUID = "user-a", Username = "a", IsActive = true },
            new() { UserGUID = "user-b", Username = "b", IsActive = true },
            new() { UserGUID = "user-c", Username = "c", IsActive = true },
        }).ExecuteCommand();
        var deviceService = new AppPushDeviceService(server.NewContext(), clock, NullLogger<AppPushDeviceService>.Instance);
        var tokenA = TokenOf("e2eaa1");
        var tokenB = TokenOf("e2ebb1");
        await deviceService.RegisterAsync("user-a", new PushDeviceRegisterRequestDto { ExpoPushToken = tokenA, Platform = "ios" });
        await deviceService.RegisterAsync("user-b", new PushDeviceRegisterRequestDto { ExpoPushToken = tokenB, Platform = "android" });

        var publisherContext = server.NewContext();
        var publisher = new NotificationPublisher(
            publisherContext,
            new NotificationRecipientResolver(publisherContext, Mock.Of<IRoleService>(), NullLogger<NotificationRecipientResolver>.Instance),
            new NoopRealtimeDispatcher(),
            clock,
            logs.CreateLogger<NotificationPublisher>(),
            options);

        // user-c 没有设备：站内消息有、推送没有。
        var published = await publisher.PublishAsync(new NotificationPublishRequest
        {
            Type = "store.price_updates",
            Title = "Price updates for your store",
            Body = "12 products need price updates.",
            Audience = NotificationAudience.ForUsers("user-a", "user-b", "user-c"),
            Data = new Dictionary<string, string> { ["count"] = "12" },
        });
        Assert.Equal(3, published.RecipientCount);
        Assert.Equal("3", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.AppNotificationRecipient"));
        Assert.Equal("2", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.AppPushDelivery WHERE State = 0 AND Attempts = 0"));

        // (通知, 设备) 唯一：重复入箱被唯一索引拒绝。
        var dup = new AppPushDelivery
        {
            DeliveryId = Guid.NewGuid().ToString("N"), NotificationId = published.NotificationId!,
            PushDeviceId = (await seedDb.Queryable<AppPushDevice>().Where(device => device.ExpoPushToken == tokenA).FirstAsync()).PushDeviceId,
            UserGUID = "user-a", NextAttemptAtUtc = clock.Now, CreatedAtUtc = clock.Now,
        };
        var dupEx = await Assert.ThrowsAnyAsync<Exception>(() => seedDb.Insertable(dup).ExecuteCommandAsync());
        Assert.True(SqlErrorClassifier.IsUniqueViolation(dupEx));

        // 发送：两条都被受理；Sent 之后租约与错误列必须是 NULL（SetColumns 空值翻译）。
        var phase = 0;
        var handler = new PushStubHandler(request =>
        {
            if (request.Uri.AbsolutePath.EndsWith("getReceipts", StringComparison.Ordinal))
            {
                var ids = JsonDocument.Parse(request.Body).RootElement.GetProperty("ids").EnumerateArray().Select(id => id.GetString()!);
                return Task.FromResult(PushStubHandler.Json(HttpStatusCode.OK,
                    "{\"data\":{" + string.Join(",", ids.Select(id => $"\"{id}\":{{\"status\":\"ok\"}}")) + "}}"));
            }

            return phase == 0
                ? PushStubHandler.OkTicketsFor(request)
                : Task.FromResult(PushStubHandler.Json(HttpStatusCode.OK,
                    "{\"data\":[{\"status\":\"error\",\"details\":{\"error\":\"MessageRateExceeded\"}},{\"status\":\"error\",\"details\":{\"error\":\"DeviceNotRegistered\"}}]}"));
        });
        var sender = new ExpoPushSender(server.NewContext(), new PushStubHttpClientFactory(handler), options, clock, logs.CreateLogger<ExpoPushSender>());

        clock.Advance(TimeSpan.FromSeconds(1));
        var stats = await sender.RunOnceAsync(default);

        Assert.Equal(2, stats.Accepted);
        Assert.Equal("2", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.AppPushDelivery WHERE State = 1 AND TicketId IS NOT NULL AND LockedUntilUtc IS NULL AND LastError IS NULL AND Attempts = 1 AND SentAtUtc IS NOT NULL"));
        Assert.Equal("Bearer sql-secret-token", handler.SendRequests.Single().Authorization);

        // 回执（15 分钟后）：两条都 Delivered。
        clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        await sender.RunOnceAsync(default);
        Assert.Equal("2", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.AppPushDelivery WHERE State = 2 AND ReceiptCheckedAtUtc IS NOT NULL"));

        // 第二条通知：一个限流重试、一个设备失效。
        phase = 1;
        var second = await publisher.PublishAsync(new NotificationPublishRequest
        {
            Type = "store.price_updates",
            Title = "Price updates for your store",
            Body = "3 products need price updates.",
            Audience = NotificationAudience.ForUsers("user-a", "user-b"),
        });
        clock.Advance(TimeSpan.FromSeconds(1));
        await sender.RunOnceAsync(default);

        var secondId = second.NotificationId!;
        // 限流的那条：回到 Pending，累计 1 次，租约清空，下次尝试约 5 秒后（±10% 抖动）。
        var retry = await seedDb.Queryable<AppPushDelivery>().Where(row => row.NotificationId == secondId && row.State == 0).FirstAsync();
        Assert.Equal(1, retry.Attempts);
        Assert.Null(retry.LockedUntilUtc);
        Assert.Equal("MessageRateExceeded", retry.LastError);
        Assert.InRange((retry.NextAttemptAtUtc - clock.Now).TotalSeconds, 4, 6);
        // 设备失效的那条：Failed，设备被撤销（两条记录的先后顺序不固定，所以只数个数）。
        Assert.Equal("1", await server.ScalarAsync(
            $"SELECT COUNT(*) FROM dbo.AppPushDelivery WHERE NotificationId = N'{secondId}' AND State = 3 AND LastError = N'DeviceNotRegistered'"));
        Assert.Equal("1", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.AppPushDevice WHERE RevokeReason = N'device-not-registered' AND RevokedAtUtc IS NOT NULL"));

        // 清理：20 天后，已结束的记录被删，仍在重试的 Pending 保留；设备撤销不足 90 天不删。
        clock.Advance(TimeSpan.FromDays(20));
        var (deliveries, devices) = await sender.CleanupAsync(default);
        Assert.Equal(3, deliveries);   // 第一条通知的两条 Delivered + 第二条通知失效的那条 Failed
        Assert.Equal(0, devices);
        Assert.Equal("1", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.AppPushDelivery"));
        Assert.Equal("0", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.AppPushDelivery WHERE State IN (2, 3, 4)"));

        // 日志里没有令牌 / AccessToken / 通知文案。
        var dump = logs.Dump();
        foreach (var forbidden in new[] { tokenA, tokenB, "sql-secret-token", "Price updates for your store", "products need price updates" })
        {
            Assert.DoesNotContain(forbidden, dump, StringComparison.Ordinal);
        }
    }

    [PushSqlServerFact]
    public async Task SQLServer_设备表不存在_发布器真实208错误被兜住_通知照常发布_只记一条Warning()
    {
        await using var server = await PushServerDatabase.CreateAsync();
        var clock = new PushTestClock(Start);
        var logs = new AuditCapturingLoggerProvider();
        server.NewClient().Insertable(new User { UserGUID = "user-a", Username = "a", IsActive = true }).ExecuteCommand();
        await server.ExecuteAsync("DROP TABLE dbo.AppPushDelivery; DROP TABLE dbo.AppPushDevice;");   // 模拟开关打开但迁移没跑
        var context = server.NewContext();
        var publisher = new NotificationPublisher(
            context,
            new NotificationRecipientResolver(context, Mock.Of<IRoleService>(), NullLogger<NotificationRecipientResolver>.Instance),
            new NoopRealtimeDispatcher(),
            clock,
            logs.CreateLogger<NotificationPublisher>(),
            new PushOptionsMonitor(new ExpoPushOptions { Enabled = true }));

        var result = await publisher.PublishAsync(new NotificationPublishRequest
        {
            Type = "system.test",
            Title = "t",
            Body = "b",
            Audience = NotificationAudience.ForUsers("user-a"),
        });

        Assert.NotNull(result.NotificationId);
        Assert.Equal("1", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.AppNotification"));
        var warning = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(true, warning.State["SchemaMissing"]);
        Assert.Null(warning.Exception);

        // 后台任务同样不抛、记「迁移未执行」。
        var service = new ExpoPushSender(server.NewContext(), new PushStubHttpClientFactory(new PushStubHandler(PushStubHandler.OkTicketsFor)),
            new PushOptionsMonitor(new ExpoPushOptions { Enabled = true }), clock, logs.CreateLogger<ExpoPushSender>());
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => service.RunOnceAsync(default));
        Assert.True(SqlErrorClassifier.IsMissingObject(ex));
    }

    [PushSqlServerFact]
    public async Task SQLServer_迁移后的表结构_与实体列一一对应_实体可直接读写()
    {
        await using var server = await PushServerDatabase.CreateAsync();
        var db = server.NewClient();
        var device = new AppPushDevice
        {
            PushDeviceId = Guid.NewGuid().ToString("N"), UserGUID = "user-a", Platform = "ios", ExpoPushToken = TokenOf("shape1"),
            AppVersion = "1.0.7", RuntimeVersion = "1.0.7", CreatedAtUtc = Start, LastSeenAtUtc = Start, RevokedAtUtc = Start.AddDays(1), RevokeReason = "logout",
        };
        var delivery = new AppPushDelivery
        {
            DeliveryId = Guid.NewGuid().ToString("N"), NotificationId = "n1", PushDeviceId = device.PushDeviceId, UserGUID = "user-a",
            State = AppPushDeliveryStates.Sent, Attempts = 2, NextAttemptAtUtc = Start, LockedUntilUtc = Start, TicketId = "ticket-1",
            LastError = "x", CreatedAtUtc = Start, SentAtUtc = Start, ReceiptCheckedAtUtc = Start,
        };

        await db.Insertable(device).ExecuteCommandAsync();
        await db.Insertable(delivery).ExecuteCommandAsync();

        var readDevice = await db.Queryable<AppPushDevice>().Where(row => row.PushDeviceId == device.PushDeviceId).FirstAsync();
        var readDelivery = await db.Queryable<AppPushDelivery>().Where(row => row.DeliveryId == delivery.DeliveryId).FirstAsync();
        Assert.Equal("logout", readDevice.RevokeReason);
        Assert.Equal("1.0.7", readDevice.RuntimeVersion);
        Assert.Equal(2, readDelivery.Attempts);
        Assert.Equal("ticket-1", readDelivery.TicketId);
        Assert.NotNull(readDelivery.ReceiptCheckedAtUtc);
    }

    private sealed class NoopRealtimeDispatcher : INotificationRealtimeDispatcher
    {
        public Task SendToUsersAsync(IReadOnlyCollection<string> userGuids, NotificationDto notification, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}

/// <summary>GUID 命名的独立测试库，表由迁移的真实脚本创建，连接配置与生产一致（WITH(NOLOCK) 读、事务内不加）。</summary>
internal sealed class PushServerDatabase : IAsyncDisposable
{
    private readonly string _serverConnectionString;
    private readonly string _databaseName;

    private PushServerDatabase(string serverConnectionString, string databaseName, string connectionString)
    {
        _serverConnectionString = serverConnectionString;
        _databaseName = databaseName;
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public static async Task<PushServerDatabase> CreateAsync()
    {
        var server = PushSqlServerFactAttribute.Connection()!;
        var name = $"hb_push_{Guid.NewGuid():N}";
        await ExecuteOnAsync(server, $"CREATE DATABASE [{name}] COLLATE Chinese_PRC_90_CI_AS;");
        var database = new PushServerDatabase(server, name, new SqlConnectionStringBuilder(server) { InitialCatalog = name, MaxPoolSize = 200 }.ConnectionString);
        using (var db = database.NewClient())
        {
            db.CodeFirst.InitTables(typeof(Store), typeof(User), typeof(UserStore));
        }

        // 真实迁移建表脚本 + 只读门禁：列类型、主键、过滤唯一索引与生产一致。
        foreach (var sql in new[] { AppNotificationSchema.ApplySql, AppPushSchema.ApplySql, AppPushSchema.VerifySql })
        {
            await database.ExecuteAsync(sql);
        }

        return database;
    }

    /// <summary>与 SqlSugarContext 同样的读写配置：自动关连接、SQL Server 查询带 NOLOCK（事务内不加）。</summary>
    public SqlSugarClient NewClient() =>
        new(new ConnectionConfig
        {
            ConnectionString = ConnectionString,
            DbType = DbType.SqlServer,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            MoreSettings = new ConnMoreSettings
            {
                IsWithNoLockQuery = true,
                DisableWithNoLockWithTran = true,
                SqlServerCodeFirstNvarchar = true,
            },
        });

    public SqlSugarContext NewContext() => PushTestDatabase.CreateSqlSugarContext(NewClient());

    public Task ExecuteAsync(string sql) => ExecuteOnAsync(ConnectionString, sql);

    public async Task<string> ScalarAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task ExecuteOnAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        try
        {
            await ExecuteOnAsync(_serverConnectionString, $"""
                IF DB_ID(N'{_databaseName}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{_databaseName}];
                END;
                """);
        }
        finally
        {
            SqlConnection.ClearAllPools();
        }
    }
}
