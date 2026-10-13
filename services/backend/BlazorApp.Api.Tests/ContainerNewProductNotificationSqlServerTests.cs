using BlazorApp.Api.Data;
using BlazorApp.Api.Data.SchemaMigrations;
using BlazorApp.Api.Features.Notifications;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.Background;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class ContainerNewProductSqlServerFactAttribute : FactAttribute
{
    public const string ConnectionEnvironmentVariable = "HB_TEST_SQLSERVER_CONNECTION";

    public ContainerNewProductSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)))
        {
            Skip = $"未配置 {ConnectionEnvironmentVariable}，跳过新品到店每日推送的真实 SQL Server 集成测试。";
        }
    }
}

/// <summary>
/// 新品到店每日推送在真实 SQL Server 上的回归：表用迁移 20261015.002 与通知平台迁移的真实建表脚本创建，
/// 数据库访问走生产同款 <see cref="SqlSugarContext"/> 配置（WITH(NOLOCK)、自动关连接、MARS），每个门店一个 DI 作用域。
/// 覆盖 SQLite 测不出的部分：date 列的等值往返、Seen 超过单条语句参数上限时的大批量写入、
/// 驱动层唯一冲突号（2627）的分类与事务整体回滚、大小写不敏感排序规则下的主键。每个用例创建 GUID 命名的独立数据库并在结束时删除。
/// </summary>
[Trait("Category", "SQL")]
public sealed class ContainerNewProductNotificationSqlServerTests
{
    private const string Sydney = "Australia/Sydney";
    private const string StoreCode = "S-NSW";
    private const string ManagerGuid = "user-manager";
    private const string CashierGuid = "user-cashier";

    [ContainerNewProductSqlServerFact]
    public async Task SQLServer_基线分批写入_次日推送_同日重跑与崩溃重试幂等_大小写不敏感的商品编码不重复推()
    {
        await using var server = await Fixture.CreateAsync();

        // 首日基线：450 个商品一次写入（分成 3 个 INSERT 批次、同一个事务），Seen 总参数量远超 SQL Server 单条语句 2100 的上限。
        var firstDay = Enumerable.Range(0, 450).Select(index => $"P{index:D4}").ToArray();
        server.AddContainer("C-1", new DateTime(2026, 7, 15), firstDay);
        await server.RunAsync(Local(2026, 7, 15, 9, 0));

        Assert.Equal("450", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.ContainerNewProductSeen"));
        Assert.Equal("2026-07-15|1|0", await server.ScalarAsync(
            "SELECT CONVERT(varchar(10), LocalDate, 23) + '|' + CONVERT(varchar(1), IsBaseline) + '|' + CONVERT(varchar(10), NewCount) FROM dbo.ContainerNewProductRun"));
        Assert.Equal("0", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.AppNotification"));

        // 同日第二轮：date 列等值命中已有 Run，不再处理。
        server.AddContainer("C-1B", new DateTime(2026, 7, 15), "P-LATE");
        await server.RunAsync(Local(2026, 7, 15, 9, 5));
        Assert.Equal("450", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.ContainerNewProductSeen"));
        Assert.Equal("1", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.ContainerNewProductRun"));

        // 次日：P-LATE 是相对累计已见集合的新增，只推它一个。
        await server.RunAsync(Local(2026, 7, 16, 9, 0));
        Assert.Equal("1", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.AppNotification"));
        var notificationId = await server.ScalarAsync("SELECT NotificationId FROM dbo.AppNotification");
        Assert.Equal($"container-new-products:{StoreCode}:2026-07-16", await server.ScalarAsync("SELECT DedupeKey FROM dbo.AppNotification"));
        Assert.Equal("1", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.AppNotificationRecipient"));
        Assert.Equal(ManagerGuid, await server.ScalarAsync("SELECT UserGUID FROM dbo.AppNotificationRecipient"));
        Assert.Equal(notificationId, await server.ScalarAsync("SELECT NotificationId FROM dbo.ContainerNewProductSeen WHERE ProductCode = N'P-LATE'"));
        Assert.Equal("1", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.ContainerNewProductRun WHERE NewCount = 1 AND IsBaseline = 0"));

        // 崩溃重试：发布之后、落记录之前崩溃 → 当天 Run 与新品 Seen 缺失；重跑发布被幂等键去重（唯一索引），随后补写。
        await server.ExecuteAsync("DELETE dbo.ContainerNewProductRun WHERE LocalDate = '2026-07-16'; DELETE dbo.ContainerNewProductSeen WHERE ProductCode = N'P-LATE';");
        await server.RunAsync(Local(2026, 7, 16, 9, 5));
        Assert.Equal("1", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.AppNotification"));
        Assert.Equal(notificationId, await server.ScalarAsync("SELECT NotificationId FROM dbo.ContainerNewProductRun WHERE LocalDate = '2026-07-16'"));
        Assert.Equal("1", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.ContainerNewProductSeen WHERE ProductCode = N'P-LATE'"));

        // 数据库排序规则不区分大小写：只是大小写不同的商品编码不是新增，也不会撞主键。
        server.AddContainer("C-3", new DateTime(2026, 7, 17), "p-late");
        await server.RunAsync(Local(2026, 7, 17, 9, 0));
        Assert.Equal("1", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.AppNotification"));
        Assert.Equal("0", await server.ScalarAsync("SELECT NewCount FROM dbo.ContainerNewProductRun WHERE LocalDate = '2026-07-17'"));
        Assert.Equal("451", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.ContainerNewProductSeen"));
    }

    [ContainerNewProductSqlServerFact]
    public async Task SQLServer_并发实例抢先写入Run_主键冲突2627被分类为并发_事务整体回滚_不抛异常()
    {
        await using var server = await Fixture.CreateAsync();
        server.AddContainer("C-1", new DateTime(2026, 7, 15), "P1", "P2");

        // 另一实例在我们查询之后、落记录之前抢先写了同店同日的 Run：我们的 Seen 批量写入成功，Run 主键冲突，整个事务回滚。
        server.QueryDecorator = inner => new RacingQuery(inner, store =>
            server.ExecuteSync(
                $"INSERT dbo.ContainerNewProductRun (StoreCode, LocalDate, ProcessedAtUtc, NewCount, IsBaseline) VALUES (N'{store.StoreCode}', '2026-07-15', SYSUTCDATETIME(), 0, 1)"));

        await server.RunAsync(Local(2026, 7, 15, 9, 0));

        Assert.Equal("0", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.ContainerNewProductSeen"));
        Assert.Equal("1", await server.ScalarAsync("SELECT COUNT(*) FROM dbo.ContainerNewProductRun"));
        Assert.Contains(server.WorkerLog.Entries, entry => entry.Contains("抢先写入", StringComparison.Ordinal));
        Assert.DoesNotContain(server.WorkerLog.Entries, entry => entry.Contains("处理失败", StringComparison.Ordinal));
    }

    private static DateTime Local(int year, int month, int day, int hour, int minute) =>
        TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified),
            TimeZoneInfo.FindSystemTimeZoneById(Sydney));

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _serverConnectionString;
        private readonly string _databaseName;
        private readonly string _connectionString;
        private readonly ContainerNewProductNotificationOptions _options = new();
        private readonly TimeProviderStub _time = new();
        private readonly SwitchableScopeFactory _scopeFactory = new();
        private readonly ContainerNewProductNotificationWorker _worker;
        private readonly SqlSugarContext _seedContext;

        private Fixture(string serverConnectionString, string databaseName, string connectionString)
        {
            _serverConnectionString = serverConnectionString;
            _databaseName = databaseName;
            _connectionString = connectionString;
            _seedContext = NewContext();
            var optionsMonitor = new Mock<IOptionsMonitor<ContainerNewProductNotificationOptions>>();
            optionsMonitor.SetupGet(monitor => monitor.CurrentValue).Returns(_options);
            _worker = new ContainerNewProductNotificationWorker(_scopeFactory, optionsMonitor.Object, _time, WorkerLog);
        }

        public CapturingLogger<ContainerNewProductNotificationWorker> WorkerLog { get; } = new();

        public Func<IContainerNewProductsQuery, IContainerNewProductsQuery>? QueryDecorator { get; set; }

        public static async Task<Fixture> CreateAsync()
        {
            var server = Environment.GetEnvironmentVariable(ContainerNewProductSqlServerFactAttribute.ConnectionEnvironmentVariable)!;
            var name = $"hb_cnp_{Guid.NewGuid():N}";
            await ExecuteOnAsync(server, $"CREATE DATABASE [{name}] COLLATE Chinese_PRC_90_CI_AS;");
            var fixture = new Fixture(server, name, new SqlConnectionStringBuilder(server) { InitialCatalog = name }.ConnectionString);
            fixture._seedContext.Db.CodeFirst.InitTables(
                typeof(Store), typeof(User), typeof(UserStore), typeof(Container), typeof(ContainerDetail),
                typeof(DomesticProduct), typeof(WarehouseProduct), typeof(Product), typeof(StoreRetailPrice),
                typeof(WarehouseProductChangeHistory));

            // 真实迁移建表脚本：列类型、主键与生产一致，并跑一遍只读门禁。
            foreach (var sql in new[]
                     {
                         AppNotificationSchema.ApplySql,
                         ContainerNewProductNoticeSchema.ApplySql,
                         ContainerNewProductNoticeSchema.VerifySql,
                     })
            {
                await fixture.ExecuteAsync(sql);
            }

            var db = fixture._seedContext.Db;
            db.Insertable(new Store { StoreGUID = "g-nsw", StoreCode = StoreCode, StoreName = "NSW", Address = "Sydney NSW 2000" }).ExecuteCommand();
            db.Insertable(new List<User>
            {
                new() { UserGUID = ManagerGuid, Username = "manager", IsActive = true },
                new() { UserGUID = CashierGuid, Username = "cashier", IsActive = true },
            }).ExecuteCommand();
            db.Insertable(new List<UserStore>
            {
                new() { UserGUID = ManagerGuid, StoreGUID = "g-nsw" },
                new() { UserGUID = CashierGuid, StoreGUID = "g-nsw" },
            }).ExecuteCommand();
            return fixture;
        }

        public void AddContainer(string containerCode, DateTime arrivalDate, params string[] productCodes)
        {
            var db = _seedContext.Db;
            db.Insertable(new Container { ContainerCode = containerCode, ContainerNumber = containerCode, ActualArrivalDate = arrivalDate }).ExecuteCommand();
            db.Insertable(productCodes
                .Select(productCode => new ContainerDetail
                {
                    DetailCode = $"{containerCode}-{productCode}",
                    ContainerCode = containerCode,
                    ProductCode = productCode,
                })
                .ToList()).ExecuteCommand();
        }

        public async Task RunAsync(DateTime nowUtc)
        {
            _time.Now = nowUtc;
            var roleService = new Mock<IRoleService>(MockBehavior.Strict);
            roleService
                .Setup(service => service.UserHasPermissionAsync(ManagerGuid, Permissions.Container.MobileNewProductsView))
                .ReturnsAsync(ApiResponse<bool>.OK(true));
            roleService
                .Setup(service => service.UserHasPermissionAsync(CashierGuid, Permissions.Container.MobileNewProductsView))
                .ReturnsAsync(ApiResponse<bool>.OK(false));

            var services = new ServiceCollection();
            // 每个作用域一个全新的生产配置 SqlSugarContext（独立客户端与连接），与线上每店一个作用域一致。
            services.AddScoped(_ => NewContext());
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
            services.AddSingleton(roleService.Object);
            services.AddSingleton<INotificationRealtimeDispatcher>(new NoopDispatcher());
            services.AddSingleton<TimeProvider>(_time);
            services.AddScoped<INotificationRecipientResolver, NotificationRecipientResolver>();
            // 发布时连接上有未结束的事务就抛异常（发布器自己开事务，嵌套会破坏外层回滚）：worker 的所有发布都必须在事务之外。
            services.AddScoped<INotificationPublisher>(provider => new OutsideTransactionPublisher(
                ActivatorUtilities.CreateInstance<NotificationPublisher>(provider),
                provider.GetRequiredService<SqlSugarContext>().Db));
            services.AddScoped<IContainerNewProductsQuery>(provider =>
            {
                IContainerNewProductsQuery query = new ContainerNewProductsQuery(provider.GetRequiredService<SqlSugarContext>());
                return QueryDecorator?.Invoke(query) ?? query;
            });
            await using var provider = services.BuildServiceProvider(validateScopes: true);
            _scopeFactory.Inner = provider.GetRequiredService<IServiceScopeFactory>();

            await _worker.RunOnceAsync(nowUtc, CancellationToken.None);
        }

        public Task ExecuteAsync(string sql) => ExecuteOnAsync(_connectionString, sql);

        public void ExecuteSync(string sql) => ExecuteOnAsync(_connectionString, sql).GetAwaiter().GetResult();

        public async Task<string> ScalarAsync(string sql)
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
        }

        public async ValueTask DisposeAsync()
        {
            SqlConnection.ClearAllPools();
            await ExecuteOnAsync(_serverConnectionString,
                $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}];");
        }

        private SqlSugarContext NewContext()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = _connectionString,
                    ["Database:CommandTimeoutSeconds"] = "60",
                    ["Database:EnableSqlLogging"] = "false",
                })
                .Build();
            return new SqlSugarContext(configuration, NullLogger<SqlSugarContext>.Instance, new TestCurrentUser());
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

    private sealed class TestCurrentUser : ICurrentUserService
    {
        public string GetCurrentUsername() => "ContainerNewProductSqlServerTest";

        public string GetCurrentUserGuid() => string.Empty;
    }

    private sealed class TimeProviderStub : TimeProvider
    {
        public DateTime Now { get; set; } = new(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);

        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc), TimeSpan.Zero);
    }

    private sealed class NoopDispatcher : INotificationRealtimeDispatcher
    {
        public Task SendToUsersAsync(
            IReadOnlyCollection<string> userGuids,
            NotificationDto notification,
            CancellationToken cancellationToken = default
        ) => Task.CompletedTask;
    }

    private sealed class OutsideTransactionPublisher(INotificationPublisher inner, ISqlSugarClient db) : INotificationPublisher
    {
        public Task<NotificationPublishResult> PublishAsync(
            NotificationPublishRequest request,
            CancellationToken cancellationToken = default
        ) => db.Ado.IsAnyTran()
            ? throw new InvalidOperationException("通知必须在业务事务之外发布")
            : inner.PublishAsync(request, cancellationToken);

        public Task<bool> RevokeAsync(string dedupeKey, CancellationToken cancellationToken = default) =>
            inner.RevokeAsync(dedupeKey, cancellationToken);
    }

    /// <summary>真实内核查询之后立刻执行一段代码，模拟「查询之后、落记录之前」被别的实例抢先写入。</summary>
    private sealed class RacingQuery(IContainerNewProductsQuery inner, Action<Store> afterQuery) : IContainerNewProductsQuery
    {
        public async Task<ContainerNewProductsResponseDto> GetForStoreAsync(
            Store store,
            bool includeExisting,
            DateTime nowUtc,
            CancellationToken cancellationToken = default
        )
        {
            var result = await inner.GetForStoreAsync(store, includeExisting, nowUtc, cancellationToken);
            afterQuery(store);
            return result;
        }
    }

    private sealed class SwitchableScopeFactory : IServiceScopeFactory
    {
        public IServiceScopeFactory Inner { get; set; } = null!;

        public IServiceScope CreateScope() => Inner.CreateScope();
    }

    public sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Add(formatter(state, exception));
    }
}
