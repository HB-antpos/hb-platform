using Hbpos.Client.Wpf.Models;

namespace Hbpos.Client.Wpf.Services;

public sealed class CardPaymentRecoveryCoordinator(
    ICardTerminalSettingsProvider settingsProvider,
    CardPaymentRecoveryService linklyRecoveryService,
    ISquarePaymentRecoveryService squareRecoveryService,
    FinancialSupervisorAuditReplayService? supervisorAuditReplay = null) :
    ICardPaymentRecoveryService,
    ICardRecoveryQueueLoader
{
    public async Task<CardPaymentRecoveryResult> RecoverLatestAsync(
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        await ReplaySupervisorAuditAsync(cancellationToken);
        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        return settings.Processor switch
        {
            CardProcessorKind.Linkly => await linklyRecoveryService.RecoverLatestAsync(cart, session, cancellationToken),
            CardProcessorKind.Square => await squareRecoveryService.RecoverLatestAsync(cart, session, cancellationToken),
            _ => CardPaymentRecoveryResult.None
        };
    }

    public async Task<CardPaymentRecoveryResult> RecoverActiveSessionAsync(
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        await ReplaySupervisorAuditAsync(cancellationToken);
        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        return settings.Processor switch
        {
            CardProcessorKind.Linkly => await linklyRecoveryService.RecoverActiveSessionAsync(cart, session, cancellationToken),
            _ => CardPaymentRecoveryResult.None
        };
    }

    public async Task<CardPaymentRecoveryResult> ManuallyClearActiveSessionAsync(
        string sessionId,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        return settings.Processor == CardProcessorKind.Linkly
            ? await linklyRecoveryService.ManuallyClearActiveSessionAsync(sessionId, session, cancellationToken)
            : CardPaymentRecoveryResult.None;
    }

    public Task<CardRefundSupervisorResolutionResult> ResolveRefundAsync(
        CardRefundSupervisorResolution resolution,
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        return resolution.Processor switch
        {
            CardProcessorKind.Linkly => linklyRecoveryService.ResolveRefundAsync(
                resolution,
                cart,
                session,
                cancellationToken),
            CardProcessorKind.Square => squareRecoveryService.ResolveRefundAsync(
                resolution,
                cart,
                session,
                cancellationToken),
            _ => Task.FromResult(new CardRefundSupervisorResolutionResult(
                false,
                "The refund processor is not supported."))
        };
    }

    public Task<CardPaymentSupervisorResolutionResult> ResolvePaymentAsync(
        CardPaymentSupervisorResolution resolution,
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        return resolution.Processor == CardProcessorKind.Linkly
            ? linklyRecoveryService.ResolvePaymentAsync(
                resolution,
                cart,
                session,
                cancellationToken)
            : Task.FromResult(new CardPaymentSupervisorResolutionResult(
                false,
                "The payment processor is not supported.",
                LockRetained: true));
    }

    public async Task<IReadOnlyList<CardRecoveryQueueItem>> ListHistoryAsync(
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        var result = await LoadHistoryQueueAsync(session, cancellationToken);
        if (!result.IsComplete)
            throw new InvalidOperationException(
                $"Card recovery history could not refresh {string.Join(", ", result.FailedProviders)}.");
        return result.Items;
    }

    public async Task<IReadOnlyList<CardRecoveryQueueItem>> ListOpenAsync(
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        var result = await LoadOpenQueueAsync(session, cancellationToken);
        if (!result.IsComplete)
        {
            // 兼容调用方没有部分加载诊断能力；继续 fail-closed，避免把缺失 provider 误计为零。
            throw new InvalidOperationException(
                $"Card recovery queue could not refresh {string.Join(", ", result.FailedProviders)}.");
        }

        return result.Items;
    }

    public Task<CardRecoveryQueueLoadResult> LoadOpenQueueAsync(
        PosSessionState session, CancellationToken cancellationToken = default) =>
        LoadQueueAsync(session, includeHistory: false, cancellationToken);

    public Task<CardRecoveryQueueLoadResult> LoadHistoryQueueAsync(
        PosSessionState session, CancellationToken cancellationToken = default) =>
        LoadQueueAsync(session, includeHistory: true, cancellationToken);

    private async Task<CardRecoveryQueueLoadResult> LoadQueueAsync(
        PosSessionState session, bool includeHistory, CancellationToken cancellationToken)
    {
        await ReplaySupervisorAuditAsync(cancellationToken);
        // 双 provider 队列：同时列出 Linkly 与 Square 的未结 attempt，全局按更新时间排序，
        // 并隔离单一 provider 的读取故障，避免健康 provider 的恢复入口一起消失。
        var linklyLoad = LoadProviderAsync(
            CardProcessorKind.Linkly,
            () => includeHistory
                ? linklyRecoveryService.ListHistoryAsync(session, cancellationToken)
                : linklyRecoveryService.ListOpenAsync(session, cancellationToken),
            cancellationToken);
        var squareLoad = LoadProviderAsync(
            CardProcessorKind.Square,
            () => includeHistory
                ? squareRecoveryService.ListHistoryAsync(session, cancellationToken)
                : squareRecoveryService.ListOpenAsync(session, cancellationToken),
            cancellationToken);
        await Task.WhenAll(linklyLoad, squareLoad);

        var results = new[] { await linklyLoad, await squareLoad };
        var items = results
            .Where(result => result.Succeeded)
            .SelectMany(result => result.Items)
            .OrderByDescending(item => item.UpdatedAt)
            .ThenByDescending(item => item.CreatedAt)
            .ToArray();
        var failedProviders = results
            .Where(result => !result.Succeeded)
            .Select(result => result.Processor)
            .ToArray();
        return new CardRecoveryQueueLoadResult(items, failedProviders);
    }

    private static async Task<CardRecoveryProviderLoad> LoadProviderAsync(
        CardProcessorKind processor,
        Func<Task<IReadOnlyList<CardRecoveryQueueItem>>> loadAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            return new CardRecoveryProviderLoad(processor, true, await loadAsync());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // provider 自己产生的超时只影响该 provider；调用方明确取消时仍须传播取消。
            cancellationToken.ThrowIfCancellationRequested();
            // 原先静默吞掉：异常中心只显示"该 provider 加载失败"，现在记下原因便于排查。
            ConsoleLog.WriteWarning(
                "CardRecovery",
                $"recovery queue load failed processor={processor} error={ex.GetType().Name}",
                new ApplicationLogContext(
                    Properties: new Dictionary<string, object?>
                    {
                        ["source"] = processor.ToString(),
                        ["action"] = "load-queue"
                    }),
                ex);
            return new CardRecoveryProviderLoad(processor, false, []);
        }
    }

    public async Task<CardAutoRecoverySummary> AutoRecoverDeterministicAsync(
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        await ReplaySupervisorAuditAsync(cancellationToken);
        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        return settings.Processor == CardProcessorKind.Linkly
            ? await linklyRecoveryService.AutoRecoverDeterministicAsync(session, cancellationToken)
            : CardAutoRecoverySummary.None;
    }

    public async Task<CardPaymentRecoveryResult> RecoverAsync(
        CardRecoveryAttemptKey key,
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        await ReplaySupervisorAuditAsync(cancellationToken);
        return key.Processor switch
        {
            CardProcessorKind.Linkly => await linklyRecoveryService.RecoverAttemptAsync(
                key.AttemptGuid,
                cart,
                session,
                cancellationToken),
            CardProcessorKind.Square => await squareRecoveryService.RecoverAttemptAsync(
                key.AttemptGuid,
                cart,
                session,
                cancellationToken),
            _ => CardPaymentRecoveryResult.None
        };
    }

    public async Task<CardRecoveryResolutionResult> ResolveAsync(
        CardRecoveryAttemptKey key,
        CardRecoverySupervisorDecision decision,
        string reason,
        string? evidence,
        string? reference,
        PosCartService cart,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        await ReplaySupervisorAuditAsync(cancellationToken);
        return key.Processor switch
        {
            CardProcessorKind.Linkly => await linklyRecoveryService.ResolveAttemptAsync(
                key.AttemptGuid,
                decision,
                reason,
                evidence,
                reference,
                cart,
                session,
                cancellationToken),
            CardProcessorKind.Square => await squareRecoveryService.ResolveAttemptAsync(
                key.AttemptGuid,
                decision,
                reason,
                evidence,
                reference,
                cart,
                session,
                cancellationToken),
            _ => new CardRecoveryResolutionResult(
                false,
                "The provider is not supported.",
                LockRetained: true)
        };
    }

    internal Task<bool> CompleteDraftHandoffAsync(
        CardRecoveryAttemptKey key,
        PosCartService cart,
        CancellationToken cancellationToken = default)
    {
        // 必须按 provider + attempt 精确路由；同一 GUID 即使同时存在于两张 provider 表中，
        // 也不能让一次 UI handoff 终态化另一处理器的金融记录。
        return key.Processor switch
        {
            CardProcessorKind.Linkly => linklyRecoveryService.CompleteDraftHandoffAsync(
                key.AttemptGuid,
                cart,
                cancellationToken),
            CardProcessorKind.Square when squareRecoveryService is SquarePaymentRecoveryService squareRecovery =>
                squareRecovery.CompleteDraftHandoffAsync(
                    key.AttemptGuid,
                    cart,
                    cancellationToken),
            CardProcessorKind.Square => Task.FromResult(false),
            _ => Task.FromResult(false)
        };
    }

    private async Task ReplaySupervisorAuditAsync(CancellationToken cancellationToken)
    {
        if (supervisorAuditReplay is not null)
        {
            await supervisorAuditReplay.ReplayPendingAsync(cancellationToken);
        }
    }

    private sealed record CardRecoveryProviderLoad(
        CardProcessorKind Processor,
        bool Succeeded,
        IReadOnlyList<CardRecoveryQueueItem> Items);
}
