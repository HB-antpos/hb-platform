using Microsoft.Extensions.Options;

namespace Hbpos.Api.Services;

public static class CardTenderReconciliationIssueTypes
{
    /// <summary>已批准的后端会话（银行已扣款）超过宽限期仍没有任何订单承载。</summary>
    public const string ApprovedSessionWithoutOrder = "ApprovedSessionWithoutOrder";
}

public sealed class CardTenderReconciliationOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>两次对账之间的间隔（分钟）。</summary>
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>首次对账前的等待（分钟），避开服务启动期的迁移与目录预热。</summary>
    public int InitialDelayMinutes { get; set; } = 2;

    /// <summary>
    /// 批准后多久仍无订单才算孤儿（分钟）。离线收银端会把订单暂存本地，网络恢复后才补传，
    /// 宽限期太短会把「订单还在路上」误报成孤儿。
    /// </summary>
    public int GracePeriodMinutes { get; set; } = 120;

    /// <summary>只对账最近多少天内批准的会话。</summary>
    public int LookbackDays { get; set; } = 14;

    /// <summary>单次对账最多处理的候选会话数，防止异常堆积时一次跑太久。</summary>
    public int MaxCandidatesPerRun { get; set; } = 200;
}

public sealed record CardTenderReconciliationResult(int Scanned, int Healed, int Reported, int Resolved);

public interface ICardTenderReconciliationService
{
    Task<CardTenderReconciliationResult> RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 「已批准会话无订单」对账。订单同步核对（<see cref="ICardTenderOrderVerifier"/>）负责
/// 「有 tender 但会话不对」的方向；这里负责反方向：银行已批准的会话，在宽限期后仍没有订单。
/// 孤儿扣款只能靠它主动发现，否则要等客诉或银行对账。
/// </summary>
public sealed class CardTenderReconciliationService(
    ICardTenderReconciliationRepository repository,
    IOptions<CardTenderReconciliationOptions> options,
    TimeProvider timeProvider,
    ILogger<CardTenderReconciliationService> logger) : ICardTenderReconciliationService
{
    public async Task<CardTenderReconciliationResult> RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var candidates = await repository.FindApprovedSessionsWithoutOrderAsync(
            now.AddDays(-Math.Max(1, settings.LookbackDays)),
            now.AddMinutes(-Math.Max(1, settings.GracePeriodMinutes)),
            Math.Max(1, settings.MaxCandidatesPerRun),
            cancellationToken);

        var healed = 0;
        var issues = new List<CardTenderIssue>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 订单其实已入库，只是会话没回链（回链上线前的订单、回链写入失败）：补回链，不报异常。
            var orderGuid = await repository.FindOrderGuidByBackendPaymentAsync(
                candidate.Environment,
                candidate.SessionId,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(orderGuid))
            {
                await repository.TryLinkSessionToOrderAsync(candidate.Id, orderGuid, cancellationToken);
                healed++;
                continue;
            }

            var amountText = candidate.RequestAmountCents is { } cents ? $"{cents / 100m:0.00}" : "<unknown>";
            issues.Add(new CardTenderIssue(
                CardTenderReconciliationIssueTypes.ApprovedSessionWithoutOrder,
                CardTenderIssueSeverities.Error,
                CardTenderIssueSources.ReconciliationJob,
                candidate.StoreCode,
                candidate.DeviceCode,
                candidate.Environment,
                candidate.SessionId,
                candidate.TxnRef,
                null,
                null,
                candidate.RequestAmountCents is { } requestCents ? requestCents / 100m : null,
                CardTenderEvidenceChecker.Truncate(
                    $"Approved Linkly session txnType={candidate.RequestTxnType ?? "<null>"} amount={amountText} " +
                    $"completedAtUtc={candidate.CompletedAtUtc:O} still has no order after {settings.GracePeriodMinutes} minutes.")));
        }

        if (issues.Count > 0)
        {
            await repository.UpsertIssuesAsync(issues, cancellationToken);
            foreach (var issue in issues)
            {
                logger.LogWarning(
                    "CardTenderReconciliation approved session without order store={StoreCode} device={DeviceCode} environment={Environment} sessionId={SessionId} txnRef={TxnRef} amount={Amount} detail={Detail}",
                    issue.StoreCode,
                    issue.DeviceCode,
                    issue.Environment,
                    issue.SessionId,
                    issue.TxnRef,
                    issue.Amount,
                    issue.Detail);
            }
        }

        var resolved = await repository.ResolveIssuesForLinkedSessionsAsync(cancellationToken);
        logger.LogInformation(
            "CardTenderReconciliation run completed scanned={Scanned} healed={Healed} reported={Reported} resolved={Resolved}",
            candidates.Count,
            healed,
            issues.Count,
            resolved);
        return new CardTenderReconciliationResult(candidates.Count, healed, issues.Count, resolved);
    }
}

/// <summary>定时触发对账。每轮失败只记日志，不影响下一轮；多实例同时跑是幂等的。</summary>
public sealed class CardTenderReconciliationBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<CardTenderReconciliationOptions> options,
    ILogger<CardTenderReconciliationBackgroundService> logger) : BackgroundService
{
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<ICardTenderReconciliationService>();
            await service.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CardTenderReconciliation run failed");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("CardTenderReconciliation is disabled");
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(Math.Max(0, settings.InitialDelayMinutes)), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, settings.IntervalMinutes)));
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 宿主关闭。
        }
    }
}
