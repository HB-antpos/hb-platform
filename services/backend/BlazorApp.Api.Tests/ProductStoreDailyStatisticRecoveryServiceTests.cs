using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services.Background;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class ProductStoreDailyStatisticRecoveryServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public ProductStoreDailyStatisticRecoveryServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables<ScheduledTaskRuntimeControl, ScheduledTaskInstanceState, ScheduledTaskLease>();
    }

    [Fact]
    public async Task 旧通用调度实例仍活动时_当前实例仍排空持久队列()
    {
        await _db.Insertable(new ScheduledTaskRuntimeControl
        {
            Id = ScheduledTaskRuntimeControl.DefaultId,
            SchedulerEnabled = true,
            ActiveInstanceId = "legacy-scheduler",
            UpdatedAtUtc = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = CreateQueue(recovered);
        await using var host = CreateHost("canonical-worker-new", true, queue.Object);

        await host.Worker.StartAsync(CancellationToken.None);
        await recovered.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        await host.Worker.StopAsync(CancellationToken.None);

        queue.Verify(x => x.RecoverExpiredRunningClaimsAsync(It.IsAny<CancellationToken>()), Times.Once);
        queue.Verify(x => x.DrainOnceAsync(It.IsAny<CancellationToken>()), Times.Once);
        queue.Verify(x => x.FinalizeJobsAsync(It.IsAny<CancellationToken>()), Times.Once);
        var control = await _db.Queryable<ScheduledTaskRuntimeControl>()
            .SingleAsync(x => x.Id == ScheduledTaskRuntimeControl.DefaultId);
        Assert.Equal("legacy-scheduler", control.ActiveInstanceId);
    }

    [Fact]
    public async Task 持久化总开关关闭时_不调用队列()
    {
        await _db.Insertable(new ScheduledTaskRuntimeControl
        {
            Id = ScheduledTaskRuntimeControl.DefaultId,
            SchedulerEnabled = false,
            ActiveInstanceId = "legacy-scheduler",
            UpdatedAtUtc = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        var queue = CreateQueue();
        await using var host = CreateHost("canonical-worker-new", true, queue.Object);

        await host.Worker.StartAsync(CancellationToken.None);
        await WaitForHeartbeatAsync("canonical-worker-new");
        await host.Worker.StopAsync(CancellationToken.None);

        queue.Verify(x => x.RecoverExpiredRunningClaimsAsync(It.IsAny<CancellationToken>()), Times.Never);
        queue.Verify(x => x.DrainOnceAsync(It.IsAny<CancellationToken>()), Times.Never);
        queue.Verify(x => x.FinalizeJobsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task 配置开关关闭时_不调用队列()
    {
        var queue = CreateQueue();
        await using var host = CreateHost("canonical-worker-config-disabled", false, queue.Object);

        await host.Worker.StartAsync(CancellationToken.None);
        await WaitForHeartbeatAsync("canonical-worker-config-disabled");
        await host.Worker.StopAsync(CancellationToken.None);

        queue.Verify(x => x.RecoverExpiredRunningClaimsAsync(It.IsAny<CancellationToken>()), Times.Never);
        queue.Verify(x => x.DrainOnceAsync(It.IsAny<CancellationToken>()), Times.Never);
        queue.Verify(x => x.FinalizeJobsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private Mock<IProductStoreDailyStatisticQueueService> CreateQueue(TaskCompletionSource? recovered = null)
    {
        var queue = new Mock<IProductStoreDailyStatisticQueueService>(MockBehavior.Strict);
        queue.Setup(x => x.RecoverExpiredRunningClaimsAsync(It.IsAny<CancellationToken>()))
            .Callback(() => recovered?.TrySetResult())
            .ReturnsAsync(0);
        queue.Setup(x => x.DrainOnceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        queue.Setup(x => x.FinalizeJobsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        return queue;
    }

    private RecoveryHost CreateHost(
        string instanceId,
        bool schedulerEnabled,
        IProductStoreDailyStatisticQueueService queue
    )
    {
        var runtimeControl = new ScheduledTaskRuntimeControlService(
            CreateContext(_db),
            Options.Create(new ScheduledTaskOptions { Enabled = schedulerEnabled, InstanceId = instanceId }),
            NullLogger<ScheduledTaskRuntimeControlService>.Instance
        );
        var services = new ServiceCollection();
        services.AddSingleton(runtimeControl);
        services.AddSingleton(queue);
        var provider = services.BuildServiceProvider();
        return new RecoveryHost(
            provider,
            new ProductStoreDailyStatisticRecoveryService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ProductStoreDailyStatisticRecoveryService>.Instance
            )
        );
    }

    private static SqlSugarContext CreateContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    private Task WaitForHeartbeatAsync(string instanceId) =>
        WaitUntilAsync(
            () => _db.Queryable<ScheduledTaskInstanceState>()
                .Where(instance => instance.InstanceId == instanceId)
                .AnyAsync(),
            diagnostics: () => $"恢复服务未触达运行时开关: {instanceId}"
        );

    private sealed class RecoveryHost(ServiceProvider provider, ProductStoreDailyStatisticRecoveryService worker)
        : IAsyncDisposable
    {
        public ProductStoreDailyStatisticRecoveryService Worker { get; } = worker;

        public ValueTask DisposeAsync()
        {
            provider.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
