using System.Data;
using BlazorApp.Api.Data;
using Microsoft.Data.SqlClient;

namespace BlazorApp.Api.Services.Background;

/// <summary>
/// 维护独立销售看板月投影：按月从日事实重建身份或编码族签名变化的月份（单月约 1–2 秒）。
/// 当月每小时重算后身份变化，下一轮即重建；查询端逐月核对身份，未追上的月份自动退回日事实。
/// 表未部署（迁移 20260924.001）时跳过。
/// 配置节 CompactBoardMonthlyProjection：Enabled（默认 true）、CheckIntervalMinutes（默认 2）、MaxMonthsPerPass（默认 6）。
/// </summary>
public sealed class CompactBoardMonthlyProjectionWorker(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<CompactBoardMonthlyProjectionWorker> logger) : BackgroundService
{
    private const string LeaseTaskType = nameof(CompactBoardMonthlyProjectionWorker);
    private const string LeaseScope = "monthly-cells";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RunBudget = TimeSpan.FromMinutes(6);
    // 单月扫约 37 万行日事实、写约 6.5 万行；给回填夜的锁等待留余量。
    private const int CommandTimeoutSeconds = 300;

    private bool _schemaMissingLogged;

    private bool Enabled => configuration.GetValue("CompactBoardMonthlyProjection:Enabled", true);
    private TimeSpan CheckInterval => TimeSpan.FromMinutes(
        Math.Max(1, configuration.GetValue("CompactBoardMonthlyProjection:CheckIntervalMinutes", 2)));
    private int MaxMonthsPerPass => Math.Max(1, configuration.GetValue("CompactBoardMonthlyProjection:MaxMonthsPerPass", 6));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "独立销售看板月投影后台任务暂不可用"); }

            try { await Task.Delay(CheckInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
            return;
        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;
        if (!await services.GetRequiredService<ScheduledTaskRuntimeControlService>().IsLeaseManagedWorkerEnabledAsync())
            return;

        var context = services.GetRequiredService<SqlSugarContext>();
        if (context.Db.CurrentConnectionConfig.DbType != SqlSugar.DbType.SqlServer)
            return;

        // 独立非 MARS 连接：与看板查询同一种连接串改法。
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(context.Db.CurrentConnectionConfig.ConnectionString)
        {
            MultipleActiveResultSets = false,
        }.ConnectionString);
        await connection.OpenAsync(stoppingToken);
        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = CompactBoardMonthlyProjection.TablesExistSql;
            if (Convert.ToInt32(await probe.ExecuteScalarAsync(stoppingToken)) != 1)
            {
                if (!_schemaMissingLogged)
                {
                    logger.LogInformation("独立销售看板月投影表尚未部署，后台任务跳过；请执行 --schema=migrate");
                    _schemaMissingLogged = true;
                }
                return;
            }
        }
        _schemaMissingLogged = false;

        var staleMonths = await ReadStaleMonthsAsync(connection, MaxMonthsPerPass, stoppingToken);
        if (staleMonths.Count == 0)
            return;

        var leases = services.GetRequiredService<ScheduledTaskLeaseService>();
        var lease = await leases.TryAcquireAsync(LeaseTaskType, LeaseScope, LeaseDuration);
        if (!lease.Acquired || string.IsNullOrWhiteSpace(lease.Lease?.LeaseToken))
            return;

        var leaseToken = lease.Lease.LeaseToken;
        var success = false;
        var deadline = DateTime.UtcNow.Add(RunBudget);
        var refreshed = 0;
        try
        {
            foreach (var month in staleMonths)
            {
                stoppingToken.ThrowIfCancellationRequested();
                if (DateTime.UtcNow >= deadline)
                    break;
                await leases.EnsureActiveAsync(LeaseTaskType, LeaseScope, leaseToken, LeaseDuration, "独立销售看板月投影");
                await RefreshMonthAsync(connection, month, stoppingToken);
                refreshed++;
            }
            success = true;
        }
        finally
        {
            await leases.CompleteAsync(LeaseTaskType, LeaseScope, leaseToken, success);
        }
        if (refreshed > 0)
            logger.LogInformation("独立销售看板月投影本轮重建 {Months} 个月，待处理 {Remaining} 个月",
                refreshed, staleMonths.Count - refreshed);
    }

    internal static async Task<List<DateTime>> ReadStaleMonthsAsync(
        SqlConnection connection, int maxMonths, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = CompactBoardMonthlyProjection.BuildStaleMonthsSql();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.Parameters.Add("@cbMaxMonths", SqlDbType.Int).Value = Math.Max(1, maxMonths);
        var months = new List<DateTime>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        do
        {
            while (await reader.ReadAsync(cancellationToken))
                months.Add(reader.GetDateTime(0).Date);
        } while (await reader.NextResultAsync(cancellationToken));
        return months;
    }

    /// <summary>一个月在一个 SNAPSHOT 事务里重建：身份、编码族、事实与状态取自同一快照，不会把两版数据拼在一起。</summary>
    internal static async Task RefreshMonthAsync(SqlConnection connection, DateTime month, CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Snapshot, cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = CompactBoardMonthlyProjection.BuildRefreshMonthSql();
            command.CommandTimeout = CommandTimeoutSeconds;
            command.Parameters.Add("@cbMonth", SqlDbType.Date).Value = month.Date;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            try { await transaction.RollbackAsync(cancellationToken); } catch { /* 连接已断时无需再回滚 */ }
            throw;
        }
    }
}
