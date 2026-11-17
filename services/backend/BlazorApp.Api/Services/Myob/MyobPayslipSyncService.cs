using System.Diagnostics;
using BlazorApp.Api.Data;
using BlazorApp.Api.Models.Myob;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;

namespace BlazorApp.Api.Services.Myob;

internal enum MyobPayslipSyncTrigger
{
    /// <summary>后台定时任务（悉尼 0 / 8 / 12 / 15 / 18 / 21 点整点，以及首次回填 / 错过整点后的补跑）。</summary>
    Scheduled,

    /// <summary>页面上的「同步」按钮。</summary>
    Manual,
}

/// <summary>某家公司文件这次同步的结果。Status 取值见 <see cref="MyobPayslipSyncStatuses"/>。消息只含状态码级别的描述，不含 MYOB 响应体与薪资。</summary>
internal sealed record MyobPayslipSyncOutcome(
    string ConnectionGuid,
    string ConnectionName,
    string Status,
    string? ErrorCode,
    string? ErrorMessage,
    bool Backfilled,
    int Fetched,
    int Inserted,
    int Updated,
    int Removed);

internal static class MyobPayslipSyncStatuses
{
    /// <summary>同步成功（本地库已与 MYOB 对齐）。</summary>
    public const string Synced = "Synced";

    /// <summary>手动同步时发现刚同步过（60 秒冷却内），没有再打 MYOB。</summary>
    public const string Skipped = "Skipped";

    /// <summary>别的进程 / 请求正在同步这家公司（租约有效），本次没有动它。</summary>
    public const string Busy = "Busy";

    public const string Failed = "Failed";
}

internal interface IMyobPayslipSyncService
{
    /// <summary>
    /// 同步给定公司文件的工资单：整段取回成功后在本地库对账（新增 / 更正 / 标记找不到），失败则本地数据原样保留。
    /// 还没回填完的公司会整段回填 24 个月，否则只对账最近 8 周。各公司先拿租约，再并发取 MYOB，最后顺序落库。
    /// </summary>
    Task<IReadOnlyList<MyobPayslipSyncOutcome>> SyncAsync(
        IReadOnlyList<MyobConnection> connections, MyobPayslipSyncTrigger trigger, CancellationToken cancellationToken);

    /// <summary>后台任务此刻该同步的公司文件（状态 Active，且按 <see cref="MyobPayslipSyncSchedule.IsDue"/> 到期）。</summary>
    Task<IReadOnlyList<MyobConnection>> FindDueConnectionsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// MYOB 工资单本地同步（只读 MYOB，写我方库）。方案 docs/plans/2026-10-29-myob-payslip-history.md 第 12 节。
/// 关键约束：
/// - 只有「整段完整取回」才会对账，取数失败不会动本地数据（否则没取到的单会被误标成找不到）；
/// - 每家公司同一时刻只有一个同步（库里的租约），后台任务、手动按钮、新旧容器重叠都不会并发写；
/// - 日志与错误消息不含姓名 / 金额 / MYOB 响应体；数据库异常只记类型（SQL Server 的截断错误会带出被截断的值）。
/// </summary>
internal sealed class MyobPayslipSyncService : IMyobPayslipSyncService
{
    // 页面上最晚能查到「今天」；同步多取 7 天，让提前录入的、支付日在未来几天内的工资单也进本地库。
    private const int LookaheadDays = 7;

    // SQL Server 单条命令最多 2100 个参数；快照表约 22 列，每批 50 行留足余量。
    private const int InsertChunk = 50;
    private const int IdChunk = 500;

    private readonly ISqlSugarClient _db;
    private readonly IMyobApiClient _client;
    private readonly IMyobTokenProvider _tokens;
    private readonly TimeProvider _time;
    private readonly ILogger<MyobPayslipSyncService> _logger;
    private readonly TimeZoneInfo _zone = MyobPayslipSyncSchedule.ResolveZone();

    public MyobPayslipSyncService(
        SqlSugarContext context,
        IMyobApiClient client,
        IMyobTokenProvider tokens,
        TimeProvider? timeProvider = null,
        ILogger<MyobPayslipSyncService>? logger = null)
        : this(context.Db, client, tokens, timeProvider, logger)
    {
    }

    internal MyobPayslipSyncService(
        ISqlSugarClient db,
        IMyobApiClient client,
        IMyobTokenProvider tokens,
        TimeProvider? timeProvider = null,
        ILogger<MyobPayslipSyncService>? logger = null)
    {
        _db = db;
        _client = client;
        _tokens = tokens;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<MyobPayslipSyncService>.Instance;
    }

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;

    // ───────────────────────── 谁该同步 ─────────────────────────

    public async Task<IReadOnlyList<MyobConnection>> FindDueConnectionsAsync(CancellationToken cancellationToken)
    {
        var now = Now();
        var today = MyobPayslipSyncSchedule.LocalToday(now, _zone);
        var slot = MyobPayslipSyncSchedule.LatestSlotUtc(now, _zone);
        var connections = await _db.Queryable<MyobConnection>()
            .Where(item => item.Status == MyobConnectionStatuses.Active)
            .ToListAsync();
        var states = (await _db.Queryable<MyobPayslipSyncState>().ToListAsync())
            .ToDictionary(state => state.ConnectionGuid, StringComparer.OrdinalIgnoreCase);

        return connections
            .Where(connection => MyobPayslipSyncSchedule.IsDue(states.GetValueOrDefault(connection.ConnectionGuid), slot, now, today))
            .OrderBy(connection => connection.BusinessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ───────────────────────── 同步 ─────────────────────────

    private sealed record SyncJob(MyobConnection Connection, bool Backfill, DateTime From, DateTime ToExclusive, string Token);

    private sealed record FetchResult(IReadOnlyList<MyobPayrollAdviceRecord>? Records, string? ErrorCode, string? ErrorMessage)
    {
        internal bool Ok => Records is not null;
    }

    public async Task<IReadOnlyList<MyobPayslipSyncOutcome>> SyncAsync(
        IReadOnlyList<MyobConnection> connections, MyobPayslipSyncTrigger trigger, CancellationToken cancellationToken)
    {
        var owner = Guid.NewGuid().ToString("N");
        var now = Now();
        var today = MyobPayslipSyncSchedule.LocalToday(now, _zone);
        var outcomes = new Dictionary<string, MyobPayslipSyncOutcome>(StringComparer.OrdinalIgnoreCase);
        var jobs = new List<SyncJob>();
        var unfinished = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // 阶段一（顺序）：拿租约、取令牌。令牌提供者会读写库（刷新令牌），不能和别的库操作并发。
            foreach (var connection in connections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = await EnsureStateAsync(connection.ConnectionGuid);
                var backfill = MyobPayslipSyncSchedule.NeedsBackfill(state, today);
                if (trigger == MyobPayslipSyncTrigger.Manual
                    && state.LastAttemptAt is { } attempt
                    && now - attempt < MyobPayslipSyncSchedule.ManualCooldown)
                {
                    outcomes[connection.ConnectionGuid] = Outcome(connection, MyobPayslipSyncStatuses.Skipped, backfill);
                    continue;
                }

                if (!await TryAcquireLeaseAsync(connection.ConnectionGuid, owner, now))
                {
                    outcomes[connection.ConnectionGuid] = Outcome(connection, MyobPayslipSyncStatuses.Busy, backfill);
                    continue;
                }

                unfinished.Add(connection.ConnectionGuid);
                string token;
                try
                {
                    token = await _tokens.GetAccessTokenAsync(connection.ConnectionGuid, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var (code, message) = Describe(ex, connection.ConnectionGuid);
                    outcomes[connection.ConnectionGuid] = await FinishAsync(connection, owner, backfill, null, code, message);
                    unfinished.Remove(connection.ConnectionGuid);
                    continue;
                }

                var from = backfill
                    ? today.AddDays(-MyobPayslipSyncSchedule.BackfillDays)
                    : today.AddDays(-MyobPayslipSyncSchedule.RecentDays);
                jobs.Add(new SyncJob(connection, backfill, from, today.AddDays(LookaheadDays + 1), token));
            }

            // 阶段二（并发）：只有 HTTP，不碰数据库。各公司同时取；全局限流闸保证相邻请求至少隔 130ms。
            var fetched = await Task.WhenAll(jobs.Select(job => FetchAsync(job, cancellationToken)));

            // 阶段三（顺序）：对账落库、释放租约。
            for (var index = 0; index < jobs.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var job = jobs[index];
                outcomes[job.Connection.ConnectionGuid] = await ApplyAsync(job, fetched[index], owner);
                unfinished.Remove(job.Connection.ConnectionGuid);
            }
        }
        finally
        {
            // 中途取消 / 异常：把还没处理完的租约还回去，别让别人白等 10 分钟。
            foreach (var guid in unfinished)
            {
                await ReleaseLeaseQuietlyAsync(guid, owner);
            }
        }

        return connections.Select(connection => outcomes[connection.ConnectionGuid]).ToList();
    }

    private async Task<FetchResult> FetchAsync(SyncJob job, CancellationToken cancellationToken)
    {
        try
        {
            var records = await _client.GetPayrollAdvicesAsync(
                job.Connection.BusinessId, job.Token, new MyobPayrollAdviceQuery(job.From, job.ToExclusive), cancellationToken);
            return new FetchResult(records, null, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var (code, message) = Describe(ex, job.Connection.ConnectionGuid);
            return new FetchResult(null, code, message);
        }
    }

    private async Task<MyobPayslipSyncOutcome> ApplyAsync(SyncJob job, FetchResult fetch, string owner)
    {
        var connection = job.Connection;
        if (!fetch.Ok)
        {
            return await FinishAsync(connection, owner, job.Backfill, null, fetch.ErrorCode, fetch.ErrorMessage);
        }

        var timer = Stopwatch.StartNew();
        try
        {
            var now = Now();
            var incoming = fetch.Records!
                .Select(record => MyobPayslipSnapshotRules.ToSnapshot(connection.ConnectionGuid, record, now))
                .OfType<MyobPayslipSnapshot>()
                .ToList();
            var existing = await _db.Queryable<MyobPayslipSnapshot>()
                .Where(row => row.ConnectionGuid == connection.ConnectionGuid && row.PaymentDate >= job.From && row.PaymentDate < job.ToExclusive)
                .ToListAsync();
            var plan = MyobPayslipSnapshotRules.Reconcile(existing, incoming, now);
            await SaveAsync(plan);

            _logger.LogInformation(
                "MYOB 工资单同步完成 Connection={Connection} Backfill={Backfill} Fetched={Fetched} Inserted={Inserted} Updated={Updated} Removed={Removed} ElapsedMs={ElapsedMs}",
                connection.ConnectionGuid, job.Backfill, incoming.Count, plan.Inserts.Count, plan.Updates.Count, plan.Removals.Count, timer.ElapsedMilliseconds);
            return await FinishAsync(
                connection, owner, job.Backfill, job.From, null, null,
                incoming.Count, plan.Inserts.Count, plan.Updates.Count, plan.Removals.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 数据库异常可能带出被截断的值（含薪资），只记类型。
            _logger.LogError("MYOB 工资单本地保存失败 Connection={Connection} Error={ErrorType}", connection.ConnectionGuid, ex.GetType().Name);
            return await FinishAsync(connection, owner, job.Backfill, null, MyobPayslipErrorCodes.SyncSaveFailed, "工资单保存到本地失败，已保留原有数据，请稍后重试");
        }
    }

    /// <summary>一个事务里完成新增 / 改写 / 标记找不到：要么全成，要么本地数据原样不动。</summary>
    private async Task SaveAsync(MyobPayslipReconcilePlan plan)
    {
        if (!plan.HasChanges)
        {
            return;
        }

        await _db.Ado.BeginTranAsync();
        try
        {
            foreach (var chunk in plan.Inserts.Chunk(InsertChunk))
            {
                await _db.Insertable(chunk.ToList()).ExecuteCommandAsync();
            }

            foreach (var row in plan.Updates)
            {
                await _db.Updateable(row).ExecuteCommandAsync();
            }

            foreach (var chunk in plan.Removals.Chunk(IdChunk))
            {
                var guids = chunk.Select(row => row.SnapshotGuid).ToList();
                var removedAt = chunk[0].RemovedAt;
                await _db.Updateable<MyobPayslipSnapshot>()
                    .SetColumns(row => new MyobPayslipSnapshot { RemovedAt = removedAt })
                    .Where(row => guids.Contains(row.SnapshotGuid))
                    .ExecuteCommandAsync();
            }

            await _db.Ado.CommitTranAsync();
        }
        catch
        {
            await _db.Ado.RollbackTranAsync();
            throw;
        }
    }

    // ───────────────────────── 状态与租约 ─────────────────────────

    private async Task<MyobPayslipSyncState> EnsureStateAsync(string connectionGuid)
    {
        var state = await _db.Queryable<MyobPayslipSyncState>().Where(item => item.ConnectionGuid == connectionGuid).FirstAsync();
        if (state is not null)
        {
            return state;
        }

        try
        {
            state = new MyobPayslipSyncState { ConnectionGuid = connectionGuid };
            await _db.Insertable(state).ExecuteCommandAsync();
            return state;
        }
        catch
        {
            // 与另一个进程同时首次建行：以先建成的那一行为准。
            return await _db.Queryable<MyobPayslipSyncState>().Where(item => item.ConnectionGuid == connectionGuid).FirstAsync()
                ?? throw new InvalidOperationException("无法创建 MYOB 工资单同步状态");
        }
    }

    /// <summary>原子抢租约：只有「没人持有，或租约已过期」才更新成功（影响行数为 1）。同时记下本次尝试时间。</summary>
    private async Task<bool> TryAcquireLeaseAsync(string connectionGuid, string owner, DateTime now)
    {
        var until = now + MyobPayslipSyncSchedule.LeaseDuration;
        var affected = await _db.Updateable<MyobPayslipSyncState>()
            .SetColumns(item => new MyobPayslipSyncState { LeaseUntil = until, LeaseOwner = owner, LastAttemptAt = now })
            .Where(item => item.ConnectionGuid == connectionGuid && (item.LeaseUntil == null || item.LeaseUntil < now))
            .ExecuteCommandAsync();
        return affected == 1;
    }

    /// <summary>
    /// 收尾：写结果并释放租约。成功 → 记 LastSuccessAt、清错误，回填成功时 CoveredFrom 推到本次起点；失败 → 只记错误，本地数据与进度不动。
    /// 只有仍持有租约的一方才能收尾（租约被别人接手后，迟到的收尾不得覆盖对方的状态）。
    /// </summary>
    private async Task<MyobPayslipSyncOutcome> FinishAsync(
        MyobConnection connection,
        string owner,
        bool backfill,
        DateTime? coveredFrom,
        string? errorCode,
        string? errorMessage,
        int fetched = 0,
        int inserted = 0,
        int updated = 0,
        int removed = 0)
    {
        var state = await _db.Queryable<MyobPayslipSyncState>().Where(item => item.ConnectionGuid == connection.ConnectionGuid).FirstAsync();
        if (state is not null && string.Equals(state.LeaseOwner, owner, StringComparison.Ordinal))
        {
            if (errorCode is null)
            {
                state.LastSuccessAt = Now();
                state.LastError = null;
                state.LastErrorCode = null;
                if (backfill && coveredFrom is { } from && (state.CoveredFrom is null || from < state.CoveredFrom))
                {
                    state.CoveredFrom = from;
                }
            }
            else
            {
                state.LastError = errorMessage;
                state.LastErrorCode = errorCode;
            }

            state.LeaseUntil = null;
            state.LeaseOwner = null;
            await _db.Updateable(state).ExecuteCommandAsync();
        }

        return new MyobPayslipSyncOutcome(
            connection.ConnectionGuid,
            connection.BusinessName,
            errorCode is null ? MyobPayslipSyncStatuses.Synced : MyobPayslipSyncStatuses.Failed,
            errorCode,
            errorMessage,
            backfill,
            fetched,
            inserted,
            updated,
            removed);
    }

    private async Task ReleaseLeaseQuietlyAsync(string connectionGuid, string owner)
    {
        try
        {
            await _db.Updateable<MyobPayslipSyncState>()
                .SetColumns(item => new MyobPayslipSyncState { LeaseUntil = null, LeaseOwner = null })
                .Where(item => item.ConnectionGuid == connectionGuid && item.LeaseOwner == owner)
                .ExecuteCommandAsync();
        }
        catch (Exception ex)
        {
            // 租约本来就会到期；这里只是尽快归还，失败不影响正确性。
            _logger.LogWarning("归还 MYOB 工资单同步租约失败 Connection={Connection} Error={ErrorType}", connectionGuid, ex.GetType().Name);
        }
    }

    private static MyobPayslipSyncOutcome Outcome(MyobConnection connection, string status, bool backfill) =>
        new(connection.ConnectionGuid, connection.BusinessName, status, null, null, backfill, 0, 0, 0, 0);

    /// <summary>把异常翻译成可展示的错误码 + 消息。MYOB 异常的消息按约定只含状态码；其它异常只记类型，消息统一成「暂时不可用」。</summary>
    private (string Code, string Message) Describe(Exception ex, string connectionGuid)
    {
        if (ex is MyobApiException myob)
        {
            return (myob.Code, myob.Message);
        }

        _logger.LogWarning("MYOB 工资单拉取失败 Connection={Connection} Error={ErrorType}", connectionGuid, ex.GetType().Name);
        return (MyobErrorCodes.ApiCallFailed, "MYOB 暂时不可用，请稍后重试");
    }
}
