using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Data.SchemaMigrations;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services.Background;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class StorePriceUpdateNoticeSqlServerFactAttribute : FactAttribute
{
    public const string ConnectionEnvironmentVariable = "HB_TEST_SQLSERVER_CONNECTION";

    public StorePriceUpdateNoticeSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)))
        {
            Skip = $"未配置 {ConnectionEnvironmentVariable}，跳过门店价格更新合并推送的真实 SQL Server 集成测试。";
        }
    }
}

/// <summary>
/// 门店价格更新合并推送在真实 SQL Server 上的回归：表用迁移 20261015.001 与任务表迁移器的真实建表脚本创建，
/// 覆盖 SQLite 测不出的部分——左连接 + IsNull 的 SQL 翻译、分块 IN 删除、事务内批量插入、datetime2 往返、
/// 基线 / 合并 / 再次变更 / 清理的完整一轮。每个用例创建 GUID 命名的独立数据库并在结束时删除。
/// </summary>
[Trait("Category", "SQL")]
public sealed class StorePriceUpdateNotificationSqlServerTests
{
    private static readonly DateTime T0 = new(2026, 10, 15, 1, 0, 0, DateTimeKind.Utc);

    [StorePriceUpdateNoticeSqlServerFact]
    public async Task SQLServer_基线_合并_再次变更_清理_完整一轮()
    {
        await using var server = await ServerDatabase.CreateAsync();
        using var db = server.NewClient();
        var roleService = new Mock<IRoleService>(MockBehavior.Strict);
        roleService
            .Setup(service => service.UserHasPermissionAsync("u-manager", Permissions.StoreProducts.PriceUpdates))
            .ReturnsAsync(ApiResponse<bool>.OK(true));
        var time = new FixedTimeProvider(T0);
        var context = CreateSqlSugarContext(db);
        var publisher = new NotificationPublisher(
            context,
            new NotificationRecipientResolver(context, roleService.Object, NullLogger<NotificationRecipientResolver>.Instance),
            new NoopDispatcher(),
            time,
            NullLogger<NotificationPublisher>.Instance);
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddSingleton<INotificationPublisher>(publisher);
        using var provider = services.BuildServiceProvider();
        var worker = new StorePriceUpdateNotificationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            time,
            NullLogger<StorePriceUpdateNotificationWorker>.Instance);

        // 1. 首次启用：积压任务静默基线，不发通知，写入基线标记。
        var a1 = AddTask(db, "1013", "A1", T0.AddHours(-10));
        var a2 = AddTask(db, "1013", "A2", T0.AddHours(-10), StorePriceUpdateTaskKinds.LabelOnly);
        await worker.RunOnceAsync(T0, CancellationToken.None);
        Assert.Equal(0, await db.Queryable<AppNotification>().CountAsync());
        Assert.Equal(3, await db.Queryable<StorePriceUpdateNotice>().CountAsync());
        Assert.Equal(1, await db.Queryable<StorePriceUpdateNotice>().CountAsync(n => n.TaskId == 0));

        // 2. 基线之后：窗口没满不发；窗口满了把全部未通知任务合并成一条。
        var b1 = AddTask(db, "1013", "B1", T0.AddMinutes(-40));
        var b2 = AddTask(db, "1013", "B2", T0.AddMinutes(-3), StorePriceUpdateTaskKinds.LabelOnly);
        var c1 = AddTask(db, "2001", "C1", T0.AddMinutes(-3));
        await worker.RunOnceAsync(T0, CancellationToken.None);
        var notification = Assert.Single(await db.Queryable<AppNotification>().ToListAsync());
        Assert.Equal("1013", notification.StoreCode);
        Assert.Equal("1 product needs a price change and 1 needs a new shelf label. Open Price Updates to review.", notification.Body);
        Assert.Equal(
            ["u-manager"],
            await db.Queryable<AppNotificationRecipient>().Select(r => r.UserGUID).ToListAsync());
        // 窗口内更新的 b2 被一并带上；另一家店窗口没满的 c1 不在其中。
        Assert.Equal(
            new[] { a1, a2, b1, b2 }.Order(),
            (await db.Queryable<StorePriceUpdateNotice>().Where(n => n.TaskId > 0).ToListAsync())
                .Select(n => n.TaskId).Order());
        Assert.DoesNotContain(await db.Queryable<StorePriceUpdateNotice>().ToListAsync(), n => n.TaskId == c1);

        // 3. 再跑不重发；任务再次变更（ChangeCount 变大）后窗口满再发一条。
        await worker.RunOnceAsync(T0.AddMinutes(5), CancellationToken.None);
        Assert.Equal(1, await db.Queryable<AppNotification>().CountAsync());
        var refreshedAt = T0.AddMinutes(10);
        db.Updateable<StorePriceUpdateTask>()
            .SetColumns(task => new StorePriceUpdateTask { ChangeCount = 2, InitiatedAtUtc = refreshedAt })
            .Where(task => task.Id == b1)
            .ExecuteCommand();
        time.Now = T0.AddMinutes(45);
        await worker.RunOnceAsync(T0.AddMinutes(45), CancellationToken.None);
        Assert.Equal(2, await db.Queryable<AppNotification>().CountAsync());
        Assert.Equal(2, (await db.Queryable<StorePriceUpdateNotice>().FirstAsync(n => n.TaskId == b1)).ChangeCount);

        // 4. 清理：非 Pending 且早于 30 天、或任务行已不存在的 Notice 被删；Pending 与基线标记保留。
        var done = AddTask(db, "1013", "D1", T0.AddDays(-80), status: StorePriceUpdateTaskStatuses.Completed);
        db.Insertable(new List<StorePriceUpdateNotice>
        {
            new() { TaskId = done, ChangeCount = 1, NotifiedAtUtc = T0.AddDays(-40) },
            new() { TaskId = 987654, ChangeCount = 1, NotifiedAtUtc = T0.AddDays(-40) },
        }).ExecuteCommand();
        var removed = await StorePriceUpdateNotificationWorker.CleanupAsync(db, T0.AddMinutes(45), CancellationToken.None);
        Assert.Equal(2, removed);
        Assert.Equal(1, await db.Queryable<StorePriceUpdateNotice>().CountAsync(n => n.TaskId == 0));
    }

    private static long AddTask(
        ISqlSugarClient db,
        string storeCode,
        string productCode,
        DateTime initiatedAtUtc,
        string kind = StorePriceUpdateTaskKinds.PriceUpdate,
        string status = StorePriceUpdateTaskStatuses.Pending)
    {
        return db.Insertable(new StorePriceUpdateTask
        {
            StoreCode = storeCode,
            ProductCode = productCode,
            Status = status,
            Kind = kind,
            InitiatorName = "tester",
            InitiatorSource = "Test",
            InitiatedAtUtc = initiatedAtUtc,
            ChangeCount = 1,
            CreatedAtUtc = initiatedAtUtc,
            UpdatedAtUtc = initiatedAtUtc,
        }).ExecuteReturnBigIdentity();
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    private sealed class NoopDispatcher : INotificationRealtimeDispatcher
    {
        public Task SendToUsersAsync(
            IReadOnlyCollection<string> userGuids,
            NotificationDto notification,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ServerDatabase : IAsyncDisposable
    {
        private readonly string _serverConnectionString;
        private readonly string _databaseName;

        private ServerDatabase(string serverConnectionString, string databaseName, string connectionString)
        {
            _serverConnectionString = serverConnectionString;
            _databaseName = databaseName;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public static async Task<ServerDatabase> CreateAsync()
        {
            var server = Environment.GetEnvironmentVariable(StorePriceUpdateNoticeSqlServerFactAttribute.ConnectionEnvironmentVariable)!;
            var name = $"hb_n3a_{Guid.NewGuid():N}";
            await ExecuteOnAsync(server, $"CREATE DATABASE [{name}] COLLATE Chinese_PRC_90_CI_AS;");
            var database = new ServerDatabase(server, name, new SqlConnectionStringBuilder(server) { InitialCatalog = name }.ConnectionString);
            using (var db = database.NewClient())
            {
                // 任务表迁移器会幂等补一行权限码，要求权限表已存在（生产启动时早已有）。
                db.CodeFirst.InitTables(typeof(Store), typeof(User), typeof(UserStore), typeof(SysPermission));
                // 真实迁移建表脚本：通知平台两张表、已通知记录表（含只读门禁）、任务表与其过滤唯一索引。
                await database.ExecuteAsync(AppNotificationSchema.ApplySql);
                await database.ExecuteAsync(StorePriceUpdateNoticeSchema.ApplySql);
                await database.ExecuteAsync(StorePriceUpdateNoticeSchema.VerifySql);
                await StorePriceUpdateTaskSchemaMigrator.EnsureAsync(db, NullLogger.Instance);

                db.Insertable(new User { UserGUID = "u-manager", Username = "manager", IsActive = true }).ExecuteCommand();
                db.Insertable(new Store { StoreGUID = "s-1013", StoreCode = "1013" }).ExecuteCommand();
                db.Insertable(new Store { StoreGUID = "s-2001", StoreCode = "2001" }).ExecuteCommand();
                db.Insertable(new UserStore { UserGUID = "u-manager", StoreGUID = "s-1013" }).ExecuteCommand();
            }

            return database;
        }

        public SqlSugarClient NewClient() =>
            new(new ConnectionConfig
            {
                ConnectionString = ConnectionString,
                DbType = DbType.SqlServer,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            });

        public Task ExecuteAsync(string sql) => ExecuteOnAsync(ConnectionString, sql);

        public async ValueTask DisposeAsync()
        {
            SqlConnection.ClearAllPools();
            await ExecuteOnAsync(_serverConnectionString,
                $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}];");
        }

        private static async Task ExecuteOnAsync(string connectionString, string sql)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }
}
