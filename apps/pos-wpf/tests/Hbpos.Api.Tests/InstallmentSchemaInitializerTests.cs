using System.Reflection;
using System.Runtime.CompilerServices;
using Hbpos.Api;
using Hbpos.Api.Data;
using Hbpos.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SqlSugar;

namespace Hbpos.Api.Tests;

public sealed class InstallmentSchemaInitializerTests
{
    [Fact]
    public async Task Initializer_runs_schema_once_under_a_transaction_owned_database_lock()
    {
        var executor = new CapturingExecutor();
        var initializer = new SqlSugarInstallmentSchemaInitializer(
            executor,
            new InstallmentSchemaInitializationState());

        await initializer.InitializeAsync();
        await initializer.InitializeAsync();

        Assert.Equal(1, executor.CallCount);
        Assert.Contains("sys.sp_getapplock", executor.AcquireLockSql, StringComparison.Ordinal);
        Assert.Contains("@LockOwner = N'Transaction'", executor.AcquireLockSql, StringComparison.Ordinal);
        Assert.Contains("@LockTimeout = 60000", executor.AcquireLockSql, StringComparison.Ordinal);
        Assert.Contains("ALTER COLUMN [PickedUpAt] DATETIME2 NULL", executor.RepairSql, StringComparison.Ordinal);
        Assert.Contains("ALTER COLUMN [CancellationKind] INT NULL", executor.RepairSql, StringComparison.Ordinal);
        Assert.Contains("ADD [PickupOperationGuid] NVARCHAR(36) NULL", executor.RepairSql, StringComparison.Ordinal);
        Assert.Contains("ADD [CancellationOperationGuid] NVARCHAR(36) NULL", executor.RepairSql, StringComparison.Ordinal);
        Assert.Contains("ALTER COLUMN [CardTransactionsJson] NVARCHAR(MAX) NULL", executor.RepairSql, StringComparison.Ordinal);
        Assert.Contains("OBJECT_ID(N'[dbo].[StoreVoucherReservation]'", executor.RepairSql, StringComparison.Ordinal);
        Assert.Contains("ADD [ConsumedAtUtc] DATETIME2 NULL", executor.RepairSql, StringComparison.Ordinal);
        Assert.Contains("ADD [ConsumedByReference] NVARCHAR(100) NULL", executor.RepairSql, StringComparison.Ordinal);
        Assert.Contains("ALTER COLUMN [ConsumedAtUtc] DATETIME2 NULL", executor.RepairSql, StringComparison.Ordinal);
        Assert.Contains("ALTER COLUMN [ConsumedByReference] NVARCHAR(100) NULL", executor.RepairSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initializer_adds_history_scope_and_line_lookup_indexes()
    {
        var executor = new CapturingExecutor();
        var initializer = new SqlSugarInstallmentSchemaInitializer(
            executor,
            new InstallmentSchemaInitializationState());

        await initializer.InitializeAsync();

        Assert.Contains("IX_InstallmentOrder_HistoryScope", executor.RepairSql, StringComparison.Ordinal);
        Assert.Contains("([StoreCode], [CreatedAt] DESC, [InstallmentGuid] DESC)", executor.RepairSql, StringComparison.Ordinal);
        Assert.Contains("IX_InstallmentOrder_HistoryUpdatedScope", executor.RepairSql, StringComparison.Ordinal);
        Assert.Contains("([StoreCode], [UpdatedAt] DESC, [InstallmentGuid] DESC)", executor.RepairSql, StringComparison.Ordinal);
        Assert.Contains("IX_InstallmentOrderLine_HistoryLookup", executor.RepairSql, StringComparison.Ordinal);
        Assert.Contains("([InstallmentGuid], [ItemNumber], [LookupCode])", executor.RepairSql, StringComparison.Ordinal);
        Assert.Contains("IX_InstallmentOrderLine_ItemNumberLookup", executor.RepairSql, StringComparison.Ordinal);
        Assert.Contains("([ItemNumber], [InstallmentGuid])", executor.RepairSql, StringComparison.Ordinal);
        Assert.Contains("IX_InstallmentOrderLine_BarcodeLookup", executor.RepairSql, StringComparison.Ordinal);
        Assert.Contains("([LookupCode], [InstallmentGuid])", executor.RepairSql, StringComparison.Ordinal);
        Assert.Contains("IX_InstallmentOrderLine_ProductCodeLookup", executor.RepairSql, StringComparison.Ordinal);
        Assert.Contains("([ProductCode], [InstallmentGuid])", executor.RepairSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Executor_creates_voucher_reservation_table_during_non_sql_server_startup()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"hbpos-installment-schema-{Guid.NewGuid():N}.db");
        var client = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={databasePath}",
            DbType = DbType.Sqlite,
            InitKeyType = InitKeyType.Attribute,
            IsAutoCloseConnection = true
        });
        try
        {
            var context = CreateDbContext(client);
            var services = new ServiceCollection()
                .AddSingleton(context)
                .BuildServiceProvider();
            await using (services)
            {
                var executor = new SqlSugarInstallmentSchemaSqlExecutor(
                    services.GetRequiredService<IServiceScopeFactory>());

                Assert.Equal(0, await CountTableAsync(client, "StoreVoucherReservation"));

                await executor.ExecuteAsync(string.Empty, string.Empty);

                Assert.Equal(1, await CountTableAsync(client, "StoreVoucherReservation"));
            }
        }
        finally
        {
            // Windows 上连接池仍持有文件句柄时无法删除，先释放客户端并清空连接池。
            client.Dispose();
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
            {
                try
                {
                    File.Delete(databasePath);
                }
                catch (IOException)
                {
                    // SQLite 可能短暂占用测试数据库文件，不影响建表断言。
                }
            }
        }
    }

    [Fact]
    public async Task Concurrent_initialization_is_single_flight_in_the_process()
    {
        var executor = new BlockingExecutor();
        var initializer = new SqlSugarInstallmentSchemaInitializer(
            executor,
            new InstallmentSchemaInitializationState());

        var first = initializer.InitializeAsync();
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = initializer.InitializeAsync();
        executor.Release.TrySetResult();

        await Task.WhenAll(first, second);

        Assert.Equal(1, executor.CallCount);
    }

    [Fact]
    public async Task Failed_initialization_is_not_marked_complete_and_can_retry()
    {
        var executor = new FailOnceExecutor();
        var initializer = new SqlSugarInstallmentSchemaInitializer(
            executor,
            new InstallmentSchemaInitializationState());

        await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.InitializeAsync());
        await initializer.InitializeAsync();

        Assert.Equal(2, executor.CallCount);
    }

    [Fact]
    public async Task Startup_service_propagates_schema_failure_when_POSM_is_configured()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:PosmConnection"] = "Server=unused;Database=unused"
            })
            .Build();
        var initializer = new FailingInitializer();
        var startup = new InstallmentSchemaStartupService(initializer, configuration);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            startup.StartAsync(CancellationToken.None));

        Assert.Equal("schema failed", exception.Message);
        Assert.Equal(1, initializer.CallCount);
    }

    [Fact]
    public async Task Startup_service_skips_schema_when_POSM_is_not_configured()
    {
        var initializer = new FailingInitializer();
        var startup = new InstallmentSchemaStartupService(
            initializer,
            new ConfigurationBuilder().Build());

        await startup.StartAsync(CancellationToken.None);

        Assert.Equal(0, initializer.CallCount);
    }

    [Fact]
    public void Registration_wires_singleton_initializer_executor_and_startup_gate()
    {
        var services = new ServiceCollection();

        services.AddHbposApiServices(new ConfigurationBuilder().Build());

        Assert.Equal(
            ServiceLifetime.Singleton,
            Assert.Single(services, descriptor =>
                descriptor.ServiceType == typeof(IInstallmentSchemaInitializer)).Lifetime);
        Assert.Equal(
            ServiceLifetime.Singleton,
            Assert.Single(services, descriptor =>
                descriptor.ServiceType == typeof(IInstallmentSchemaSqlExecutor)).Lifetime);
        var hosted = Assert.Single(services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(InstallmentSchemaStartupService));
        Assert.Equal(ServiceLifetime.Singleton, hosted.Lifetime);
    }

    private sealed class CapturingExecutor : IInstallmentSchemaSqlExecutor
    {
        public int CallCount { get; private set; }

        public string AcquireLockSql { get; private set; } = string.Empty;

        public string RepairSql { get; private set; } = string.Empty;

        public Task ExecuteAsync(
            string acquireLockSql,
            string repairSql,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            AcquireLockSql = acquireLockSql;
            RepairSql = repairSql;
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingExecutor : IInstallmentSchemaSqlExecutor
    {
        public TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public async Task ExecuteAsync(
            string acquireLockSql,
            string repairSql,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class FailOnceExecutor : IInstallmentSchemaSqlExecutor
    {
        public int CallCount { get; private set; }

        public Task ExecuteAsync(
            string acquireLockSql,
            string repairSql,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return CallCount == 1
                ? Task.FromException(new InvalidOperationException("schema failed"))
                : Task.CompletedTask;
        }
    }

    private sealed class FailingInitializer : IInstallmentSchemaInitializer
    {
        public int CallCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromException(new InvalidOperationException("schema failed"));
        }
    }

    private static Task<int> CountTableAsync(ISqlSugarClient client, string tableName)
    {
        return client.Ado.GetIntAsync(
            "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = @tableName",
            new SugarParameter("@tableName", tableName));
    }

    private static HbposSqlSugarContext CreateDbContext(ISqlSugarClient client)
    {
        var context = (HbposSqlSugarContext)RuntimeHelpers.GetUninitializedObject(
            typeof(HbposSqlSugarContext));
        SetAutoProperty(context, nameof(HbposSqlSugarContext.MainDb), client);
        SetAutoProperty(context, nameof(HbposSqlSugarContext.PosmDb), client);
        return context;
    }

    private static void SetAutoProperty(
        HbposSqlSugarContext context,
        string propertyName,
        ISqlSugarClient value)
    {
        var backingField = typeof(HbposSqlSugarContext).GetField(
            $"<{propertyName}>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Unable to find backing field for {propertyName}.");
        backingField.SetValue(context, value);
    }
}

internal sealed class TestNoOpInstallmentSchemaInitializer : IInstallmentSchemaInitializer
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
