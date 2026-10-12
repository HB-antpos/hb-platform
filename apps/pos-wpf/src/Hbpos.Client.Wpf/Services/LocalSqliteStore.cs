using System.Data;
using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Wpf.Services;

public interface ILocalSqliteCheckpointService
{
    Task CheckpointWalAsync(CancellationToken cancellationToken = default);
}

public sealed class LocalSqliteStore : ILocalSqliteCheckpointService
{
    private readonly object _transitionGate = new();
    private string _activeDatabasePath;
    private ApiEndpointDatabasePartitionResolver? _partitionResolver;
    private TaskCompletionSource? _drainedConnections;
    private long _activeConnectionCount;
    private long _transitionSequence;
    private LocalDatabaseTransition? _transition;

    public LocalSqliteStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hbpos.Client",
            "hbpos_client.db"))
    {
    }

    public LocalSqliteStore(string databasePath)
    {
        _activeDatabasePath = PreparePath(databasePath);
    }

    public LocalSqliteStore(
        ApiRuntimeEndpointState endpointState,
        ApiEndpointDatabasePartitionResolver partitionResolver)
        : this(partitionResolver.GetDatabasePath(endpointState.CurrentAddress.AbsoluteUri))
    {
        _partitionResolver = partitionResolver;
    }

    public string ActiveDatabasePath => Volatile.Read(ref _activeDatabasePath);

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        BeginConnectionLease();
        var connection = new SqliteConnection(CreateConnectionString(ActiveDatabasePath));
        var released = 0;
        void ReleaseOnce()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                ReleaseConnectionLease();
            }
        }

        connection.StateChange += (_, args) =>
        {
            if (args.OriginalState != ConnectionState.Closed && args.CurrentState == ConnectionState.Closed)
            {
                ReleaseOnce();
            }
        };

        try
        {
            // 只读计时：量化「每次新建非池化连接」的真实代价，用于决定是否值得改成连接池（见 LocalSqliteOpenTimingDiagnostics）。
            var openStartedAt = Stopwatch.GetTimestamp();
            await connection.OpenAsync(cancellationToken);
            var openedAt = Stopwatch.GetTimestamp();
            await ConfigureConnectionAsync(connection, cancellationToken);
            var configuredAt = Stopwatch.GetTimestamp();
            LocalSqliteOpenTimingDiagnostics.Shared.Record(
                Stopwatch.GetElapsedTime(openStartedAt, openedAt),
                Stopwatch.GetElapsedTime(openedAt, configuredAt));
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            ReleaseOnce();
            throw;
        }
    }

    public async Task<LocalDatabaseSwitch> PrepareSwitchAsync(
        string targetAddress,
        ApiEndpointDatabasePartitionResolver partitionResolver,
        CancellationToken cancellationToken = default)
    {
        var previousPath = ActiveDatabasePath;
        var targetPath = PreparePath(partitionResolver.GetDatabasePath(targetAddress));
        await using var connection = new SqliteConnection(CreateConnectionString(targetPath));
        await connection.OpenAsync(cancellationToken);
        await ConfigureConnectionAsync(connection, cancellationToken);
        return new LocalDatabaseSwitch(previousPath, targetPath);
    }

    public Task<LocalDatabaseSwitch> PrepareSwitchAsync(
        string targetAddress,
        CancellationToken cancellationToken = default)
    {
        var resolver = _partitionResolver ??
            throw new InvalidOperationException("当前数据库存储未配置服务器分区解析器。");
        return PrepareSwitchAsync(targetAddress, resolver, cancellationToken);
    }

    public async Task<LocalDatabaseTransition> BeginTransitionAsync(
        LocalDatabaseSwitch preparedSwitch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparedSwitch);
        LocalDatabaseTransition transition;
        Task drained;
        lock (_transitionGate)
        {
            if (_transition is not null)
            {
                throw new LocalDatabaseTransitionException("本地数据库正在切换。");
            }

            if (!string.Equals(ActiveDatabasePath, preparedSwitch.PreviousDatabasePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new LocalDatabaseTransitionException("本地数据库切换准备结果已过期。");
            }

            transition = new LocalDatabaseTransition(
                ++_transitionSequence,
                preparedSwitch.PreviousDatabasePath,
                preparedSwitch.TargetDatabasePath);
            _transition = transition;
            _drainedConnections = _activeConnectionCount == 0
                ? null
                : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            drained = _drainedConnections?.Task ?? Task.CompletedTask;
        }

        // 先阻止新连接，再等待所有旧库连接关闭，避免切换瞬间跨库写入。
        try
        {
            await drained.WaitAsync(cancellationToken);
            return transition;
        }
        catch
        {
            Abort(transition);
            throw;
        }
    }

    public void Publish(LocalDatabaseTransition transition)
    {
        lock (_transitionGate)
        {
            ValidateTransition(transition);
            Volatile.Write(ref _activeDatabasePath, transition.TargetDatabasePath);
        }
    }

    public void Complete(LocalDatabaseTransition transition)
    {
        lock (_transitionGate)
        {
            ValidateTransition(transition);
            _transition = null;
            _drainedConnections = null;
        }
    }

    public void Abort(LocalDatabaseTransition transition)
    {
        lock (_transitionGate)
        {
            ValidateTransition(transition);
            Volatile.Write(ref _activeDatabasePath, transition.PreviousDatabasePath);
            _transition = null;
            _drainedConnections = null;
        }
    }

    internal async Task<SqliteConnection> OpenTransitionConnectionAsync(
        LocalDatabaseTransition transition,
        CancellationToken cancellationToken)
    {
        lock (_transitionGate)
        {
            ValidateTransition(transition);
        }

        // 此连接只供切换持有者执行最终只读安全检查，不参与普通连接租约。
        var connection = new SqliteConnection(CreateConnectionString(transition.PreviousDatabasePath));
        try
        {
            // 只读计时：量化「每次新建非池化连接」的真实代价，用于决定是否值得改成连接池（见 LocalSqliteOpenTimingDiagnostics）。
            var openStartedAt = Stopwatch.GetTimestamp();
            await connection.OpenAsync(cancellationToken);
            var openedAt = Stopwatch.GetTimestamp();
            await ConfigureConnectionAsync(connection, cancellationToken);
            var configuredAt = Stopwatch.GetTimestamp();
            LocalSqliteOpenTimingDiagnostics.Shared.Record(
                Stopwatch.GetElapsedTime(openStartedAt, openedAt),
                Stopwatch.GetElapsedTime(openedAt, configuredAt));
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public void Switch(LocalDatabaseSwitch preparedSwitch)
    {
        var transition = BeginTransitionAsync(preparedSwitch, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        Publish(transition);
        Complete(transition);
    }

    public void Rollback(LocalDatabaseSwitch preparedSwitch)
    {
        ArgumentNullException.ThrowIfNull(preparedSwitch);
        Volatile.Write(ref _activeDatabasePath, preparedSwitch.PreviousDatabasePath);
    }

    public async Task CheckpointWalAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        // WAL checkpoint 只在低频维护点执行，避免每次写入都强制刷盘影响收银性能。
        await ExecutePragmaAsync(connection, "PRAGMA wal_checkpoint(PASSIVE);", cancellationToken);
    }

    private void BeginConnectionLease()
    {
        lock (_transitionGate)
        {
            if (_transition is not null)
            {
                throw new LocalDatabaseTransitionException("本地数据库正在切换，新连接已拒绝。");
            }

            _activeConnectionCount++;
        }
    }

    private void ReleaseConnectionLease()
    {
        TaskCompletionSource? drained = null;
        lock (_transitionGate)
        {
            _activeConnectionCount--;
            if (_activeConnectionCount == 0 && _transition is not null)
            {
                drained = _drainedConnections;
            }
        }

        drained?.TrySetResult();
    }

    private void ValidateTransition(LocalDatabaseTransition transition)
    {
        if (_transition is null || _transition.Id != transition.Id)
        {
            throw new LocalDatabaseTransitionException("本地数据库切换令牌无效或已结束。");
        }
    }

    private static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ExecutePragmaAsync(connection, "PRAGMA busy_timeout = 5000;", cancellationToken);
        await ExecutePragmaAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken);
    }

    private static async Task ExecutePragmaAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string PreparePath(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
        return fullPath;
    }

    private static string CreateConnectionString(string databasePath)
    {
        return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();
    }
}

public sealed record LocalDatabaseSwitch(
    string PreviousDatabasePath,
    string TargetDatabasePath);

public sealed record LocalDatabaseTransition(
    long Id,
    string PreviousDatabasePath,
    string TargetDatabasePath);

public sealed class LocalDatabaseTransitionException(string message) : InvalidOperationException(message);
