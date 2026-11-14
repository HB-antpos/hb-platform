using BlazorApp.Api.Data;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.StoreCash;

/// <summary>
/// 把收银端「小额现金支出」（PaidOut）自动生成为后台现金支出（类别 Purchase）。
/// 现金池入池口径是「实点 + 取出 − 存入」，小额支出已随取出加回，所以必须在这里扣一次，
/// 账面上也才能看到钱花在了哪。冲正记录会把对应的自动支出作废。
/// 幂等靠 StoreCashExpense.ClientRequestId 的全局唯一：同一笔收银端记录无论跑几次、几个实例都只生成一条。
/// </summary>
public sealed class PosPaidOutExpenseSynchronizer
{
    /// <summary>自动支出请求号前缀：pos-paidout:{MovementGuid}（48 字符，不超过 64 的上限）。</summary>
    public const string ClientRequestIdPrefix = "pos-paidout:";

    /// <summary>自动支出的创建人标识（不是任何真实用户 Guid）。</summary>
    public const string SystemCreatorGuid = "pos-terminal";

    private const int BatchSize = 200;
    private const int MaxNoteLength = 500;

    private readonly ISqlSugarClient _db;
    private readonly IPosCashMovementSource _source;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    // 增量水位只存在内存：进程重启后从 0 重扫，靠请求号幂等跳过已生成的记录。
    private long _watermark;

    public PosPaidOutExpenseSynchronizer(
        ISqlSugarClient db,
        IPosCashMovementSource source,
        TimeProvider timeProvider,
        ILogger logger
    )
    {
        _db = db;
        _source = source;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public long Watermark => _watermark;

    /// <summary>后台任务每轮新建同步器时，接上上一轮的水位。</summary>
    public void RestoreWatermark(long watermark) => _watermark = Math.Max(_watermark, watermark);

    /// <summary>处理一轮；返回本轮新生成与新作废的支出条数。</summary>
    public async Task<int> SyncOnceAsync(CancellationToken cancellationToken)
    {
        var changed = 0;
        while (true)
        {
            var batch = await _source.GetPaidOutsAfterAsync(_watermark, BatchSize, cancellationToken);
            if (batch.Count == 0)
            {
                return changed;
            }

            foreach (var movement in batch)
            {
                // 逐条推进水位：中途失败时下一轮从失败那条重试，前面已处理的靠幂等不会重复。
                changed += movement.VoidsMovementGuid is { } voided
                    ? await VoidAsync(movement, voided, cancellationToken)
                    : await CreateAsync(movement, cancellationToken);
                _watermark = movement.Id;
            }

            if (batch.Count < BatchSize)
            {
                return changed;
            }
        }
    }

    public static string RequestIdFor(Guid movementGuid) => ClientRequestIdPrefix + movementGuid.ToString("D");

    private async Task<int> CreateAsync(PosCashMovement movement, CancellationToken cancellationToken)
    {
        var requestId = RequestIdFor(movement.MovementGuid);
        var exists = await _db.Queryable<StoreCashExpense>()
            .Where(item => item.ClientRequestId == requestId)
            .AnyAsync(cancellationToken);
        if (exists)
        {
            return 0;
        }

        var expense = new StoreCashExpense
        {
            ExpenseGuid = Guid.NewGuid().ToString(),
            StoreCode = movement.StoreCode,
            ExpenseDate = movement.BusinessDate.ToDateTime(TimeOnly.MinValue),
            Category = StoreCashConstants.ExpenseCategory.Purchase,
            Amount = movement.Amount,
            Note = Truncate(BuildNote(movement), MaxNoteLength),
            ReviewStatus = StoreCashConstants.ReviewStatus.None,
            Status = StoreCashConstants.RecordStatus.Active,
            ClientRequestId = requestId,
            CreatedByUserGuid = SystemCreatorGuid,
            CreatedByName = Truncate($"{movement.CashierName}（收银端）", 100),
            CreatedAtUtc = movement.ReceivedAtUtc,
        };

        try
        {
            await _db.Insertable(expense).ExecuteCommandAsync(cancellationToken);
            return 1;
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            // 另一个实例刚生成了同一条：幂等跳过。
            return 0;
        }
    }

    private async Task<int> VoidAsync(PosCashMovement reversal, Guid originalGuid, CancellationToken cancellationToken)
    {
        var requestId = RequestIdFor(originalGuid);
        var expense = await _db.Queryable<StoreCashExpense>()
            .Where(item => item.ClientRequestId == requestId)
            .FirstAsync(cancellationToken);
        if (expense is null)
        {
            // 原记录 Id 一定更小、本应先生成；没找到说明数据异常，记日志后跳过，不能卡住后续记录。
            _logger.LogWarning(
                "POS paid-out void has no generated expense movement={MovementGuid} voids={OriginalGuid} store={StoreCode}",
                reversal.MovementGuid,
                originalGuid,
                reversal.StoreCode
            );
            return 0;
        }

        if (expense.Status == StoreCashConstants.RecordStatus.Voided)
        {
            return 0;
        }

        var reason = string.IsNullOrWhiteSpace(reversal.Note) ? "收银端冲正" : $"收银端冲正：{reversal.Note.Trim()}";
        var updated = await _db.Updateable<StoreCashExpense>()
            .SetColumns(item => new StoreCashExpense
            {
                Status = StoreCashConstants.RecordStatus.Voided,
                VoidReason = Truncate(reason, MaxNoteLength),
                VoidedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
                VoidedByUserGuid = SystemCreatorGuid,
                VoidedByName = Truncate($"{reversal.CashierName}（收银端）", 100),
            })
            .Where(item => item.ExpenseGuid == expense.ExpenseGuid && item.Status == StoreCashConstants.RecordStatus.Active)
            .ExecuteCommandAsync(cancellationToken);
        return updated;
    }

    private static string BuildNote(PosCashMovement movement)
    {
        var number = string.IsNullOrWhiteSpace(movement.DisplayNumber) ? string.Empty : $" {movement.DisplayNumber}";
        return $"收银端小额支出：{movement.Note?.Trim()}（{movement.DeviceCode}{number}）";
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static bool IsUniqueViolation(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
            {
                return true;
            }

            // SQLite（测试）：UNIQUE constraint failed。
            if (current.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>每 2 分钟跑一轮小额支出同步；单轮失败只记日志，下一轮从失败处重试。</summary>
internal sealed class PosPaidOutExpenseSyncBackgroundService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PosPaidOutExpenseSyncBackgroundService> _logger;
    private long _watermark;

    public PosPaidOutExpenseSyncBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<PosPaidOutExpenseSyncBackgroundService> logger
    )
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动后稍等，避开启动期的建表、预热。
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var synchronizer = new PosPaidOutExpenseSynchronizer(
                    scope.ServiceProvider.GetRequiredService<SqlSugarContext>().Db,
                    scope.ServiceProvider.GetRequiredService<IPosCashMovementSource>(),
                    scope.ServiceProvider.GetRequiredService<TimeProvider>(),
                    _logger
                );
                synchronizer.RestoreWatermark(_watermark);
                var changed = await synchronizer.SyncOnceAsync(stoppingToken);
                _watermark = synchronizer.Watermark;
                if (changed > 0)
                {
                    _logger.LogInformation("POS paid-out expense sync changed={Changed} watermark={Watermark}", changed, _watermark);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POS paid-out expense sync failed watermark={Watermark}", _watermark);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
