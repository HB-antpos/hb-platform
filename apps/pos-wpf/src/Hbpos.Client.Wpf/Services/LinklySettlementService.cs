using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Linkly;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Wpf.Services;

public sealed record LinklySettlementExecutionResult(
    LocalLinklySettlementRecord Settlement,
    ReceiptPrintResult? PrintResult,
    bool ResultUnknown = false,
    bool ReusedFinalEvidence = false);

public sealed record LinklySettlementManualResolutionResult(
    bool Resolved,
    LocalLinklySettlementRecord Settlement,
    string Message);

public interface ILinklySettlementService
{
    Task<LinklySettlementExecutionResult> SettleAndPrintAsync(
        PosSessionState session,
        DateTime businessDate,
        CancellationToken cancellationToken = default);

    Task<ReceiptPrintResult> ReprintAsync(
        LocalLinklySettlementRecord settlement,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalLinklySettlementRecord>> GetHistoryAsync(
        PosSessionState session,
        DateTime businessDate,
        CancellationToken cancellationToken = default);

    Task<LinklySettlementManualResolutionResult> ResolveUncertainAsync(
        PosSessionState session,
        LocalLinklySettlementRecord settlement,
        LocalLinklySettlementManualResolution resolution,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new LinklySettlementManualResolutionResult(
            false,
            settlement,
            "Manual Linkly settlement resolution is not supported by this service."));

    // 日结保存后是否自动发送 Linkly 结算；默认不自动，未实现的服务不能意外触发银行操作。
    Task<bool> ShouldAutoSettleAfterDailyCloseAsync(
        PosSessionState session,
        DateTime businessDate,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}

public sealed class LinklySettlementService(
    ILinklyTerminalClient terminalClient,
    ICardTerminalSettingsProvider settingsProvider,
    ILocalLinklySettlementRepository settlementRepository,
    ILinklyBankReceiptPrinter receiptPrinter,
    ILinklyBackendTerminalClient? backendTerminalClient = null,
    ILinklySettlementUploadScheduler? settlementUploadScheduler = null,
    ILinklyTerminalSelectionTransitionGate? linklyTerminalSelectionTransitionGate = null,
    IPaymentMethodSettingsService? paymentMethodSettingsService = null) : ILinklySettlementService
{
    public async Task<bool> ShouldAutoSettleAfterDailyCloseAsync(
        PosSessionState session,
        DateTime businessDate,
        CancellationToken cancellationToken = default)
    {
        // 结算只能针对今天；补做昨天的日结不能把今天的终端批次结掉。
        if (businessDate.Date != DateTime.Today || paymentMethodSettingsService is null)
        {
            return false;
        }

        // 重新从本地设置读取，避免内存里还是默认值（UseManualCard=false）时误判为集成刷卡。
        var paymentMethods = await paymentMethodSettingsService.LoadAsync(cancellationToken);
        if (paymentMethods.UseManualCard)
        {
            // 手动刷卡（独立刷卡机）模式下 POS 不连终端，结算由刷卡机自己完成。
            return false;
        }

        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        if (settings.Processor != CardProcessorKind.Linkly)
        {
            return false;
        }

        // 一天可以保存多次日结：今天已有成功结算就不再重复结算；
        // 有待定/结果未知的记录须由收银员在结算页处理，自动流程不碰。失败的记录允许自动重试。
        var existingSettlements = await settlementRepository.GetByBusinessDateAsync(
            session.StoreCode,
            session.DeviceCode,
            businessDate,
            cancellationToken);
        return !existingSettlements.Any(settlement =>
            settlement.Status is LocalLinklySettlementStatus.Succeeded
                or LocalLinklySettlementStatus.Pending
                or LocalLinklySettlementStatus.Unknown);
    }

    public async Task<LinklySettlementExecutionResult> SettleAndPrintAsync(
        PosSessionState session,
        DateTime businessDate,
        CancellationToken cancellationToken = default)
    {
        if (businessDate.Date != DateTime.Today)
        {
            throw new InvalidOperationException("Linkly settlement is only available for the current business date.");
        }

        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        if (settings.Processor != CardProcessorKind.Linkly)
        {
            // 非 Linkly 模式绝不能创建结算记录或触发终端调用。
            throw new InvalidOperationException("Linkly settlement is unavailable because Linkly is not the active card processor.");
        }

        // 结算会消费当前 POS 的线路选择；从本地未决检查到终端结果落库期间禁止换线。
        await using var transitionLease = linklyTerminalSelectionTransitionGate is null
            ? null
            : await linklyTerminalSelectionTransitionGate.EnterFinancialOperationAsync(cancellationToken);

        var existingSettlements = await settlementRepository.GetByBusinessDateAsync(
            session.StoreCode,
            session.DeviceCode,
            businessDate,
            cancellationToken);
        var unresolvedSettlement = existingSettlements.FirstOrDefault(settlement =>
            settlement.Status is LocalLinklySettlementStatus.Pending or LocalLinklySettlementStatus.Unknown);
        if (unresolvedSettlement is not null)
        {
            return await RecoverResumableSettlementAsync(settings, unresolvedSettlement, cancellationToken);
        }

        var requestedAt = DateTimeOffset.UtcNow;
        var settlement = new LocalLinklySettlementRecord(
            Guid.NewGuid(),
            session.StoreCode,
            session.DeviceCode,
            businessDate.Date,
            settings.LinklyConnectionMode.ToString(),
            settings.Environment.ToString(),
            ProviderSessionId: null,
            LocalLinklySettlementStatus.Pending,
            ResponseCode: null,
            ResponseText: null,
            SettlementData: null,
            ReceiptTexts: [],
            requestedAt,
            CompletedAt: null,
            FirstPrintedAt: null,
            LastPrintedAt: null,
            PrintCount: 0,
            LastPrintError: null)
        {
            ProviderSubmissionState = ProviderSubmissionState.Unknown
        };

        // 银行操作开始前先持久化，发生断线时保留可审计的未知记录，绝不自动重发。
        if (!await settlementRepository.TryCreatePendingAsync(settlement, cancellationToken))
        {
            var unresolved = (await settlementRepository.GetByBusinessDateAsync(
                    session.StoreCode,
                    session.DeviceCode,
                    businessDate,
                    cancellationToken))
                .FirstOrDefault(existing => existing.Status is LocalLinklySettlementStatus.Pending or LocalLinklySettlementStatus.Unknown);
            if (unresolved is not null)
            {
                return BlockUnresolvedSettlement(unresolved);
            }

            throw new InvalidOperationException("The unresolved Linkly settlement could not be read after a concurrent create.");
        }
        RequestSettlementUpload();

        LinklySettlementResult terminalResult;
        try
        {
            terminalResult = await terminalClient.SettlementAsync(session, settings, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // 终端调用已经开始时，非调用方超时按结果未知落库；调用方主动取消则保留 Pending 锁并继续传播。
            var unknown = new LocalLinklySettlementCompletion(
                LocalLinklySettlementStatus.Unknown,
                ResponseCode: null,
                ResponseText: ex.Message,
                SettlementData: null,
                ReceiptTexts: [],
                DateTimeOffset.UtcNow,
                ProviderSubmissionState.Unknown);
            await settlementRepository.CompleteAsync(settlement.SettlementGuid, unknown, CancellationToken.None);
            RequestSettlementUpload();
            return new LinklySettlementExecutionResult(
                settlement with
                {
                    Status = LocalLinklySettlementStatus.Unknown,
                    ResponseText = ex.Message,
                    CompletedAt = unknown.CompletedAt,
                    ProviderSubmissionState = ProviderSubmissionState.Unknown
                },
                PrintResult: null,
                ResultUnknown: true);
        }

        if (!string.IsNullOrWhiteSpace(terminalResult.SessionId))
        {
            settlement = await BindOrReuseSettlementAsync(
                settlement,
                terminalResult.SessionId,
                cancellationToken);
        }

        var submissionState = terminalResult.ResultUnknown
            ? ProviderSubmissionState.Unknown
            : terminalResult.ProviderSubmissionState;
        var status = terminalResult.ResultUnknown || submissionState == ProviderSubmissionState.Unknown
            ? LocalLinklySettlementStatus.Unknown
            : terminalResult.Succeeded
                ? LocalLinklySettlementStatus.Succeeded
                : LocalLinklySettlementStatus.Failed;
        var completion = new LocalLinklySettlementCompletion(
            status,
            terminalResult.ResponseCode,
            terminalResult.ResponseText ?? terminalResult.Message,
            terminalResult.SettlementData,
            terminalResult.ReceiptTexts,
            DateTimeOffset.UtcNow,
            submissionState);

        if (IsDefinitive(settlement.Status))
        {
            // 同一 provider session 已有最终证据时，后续未知或空响应不得覆盖既有结果、回单或打印审计。
            if (status != LocalLinklySettlementStatus.Unknown)
            {
                await AcknowledgeCloudBackendSettlementAsync(settings, settlement, cancellationToken);
            }

            return new LinklySettlementExecutionResult(
                settlement,
                PrintResult: null,
                ResultUnknown: status == LocalLinklySettlementStatus.Unknown,
                ReusedFinalEvidence: true);
        }

        await settlementRepository.CompleteAsync(settlement.SettlementGuid, completion, CancellationToken.None);
        RequestSettlementUpload();
        settlement = settlement with
        {
            Status = completion.Status,
            ResponseCode = completion.ResponseCode,
            ResponseText = completion.ResponseText,
            SettlementData = completion.SettlementData,
            ReceiptTexts = completion.ReceiptTexts ?? [],
            CompletedAt = completion.CompletedAt,
            ProviderSubmissionState = completion.ProviderSubmissionState
        };

        if (settlement.Status == LocalLinklySettlementStatus.Unknown)
        {
            return new LinklySettlementExecutionResult(settlement, PrintResult: null, ResultUnknown: true);
        }

        await AcknowledgeCloudBackendSettlementAsync(settings, settlement, cancellationToken);

        var printResult = await ReprintAsync(settlement, cancellationToken);
        return new LinklySettlementExecutionResult(settlement, printResult);
    }

    public async Task<ReceiptPrintResult> ReprintAsync(
        LocalLinklySettlementRecord settlement,
        CancellationToken cancellationToken = default)
    {
        if (settlement.ReceiptTexts.Count == 0)
        {
            return new ReceiptPrintResult(false, "No Linkly settlement receipt is available to print.");
        }

        ReceiptPrintResult result = new(true, "Receipt printed.");
        try
        {
            foreach (var receiptText in settlement.ReceiptTexts)
            {
                result = await receiptPrinter.PrintAsync(
                    settlement.Environment,
                    settlement.ProviderSessionId ?? settlement.SettlementGuid.ToString("D"),
                    receiptText,
                    LinklyBankReceiptKind.Settlement,
                    responseCode: settlement.ResponseCode,
                    responseText: settlement.ResponseText,
                    cancellationToken: cancellationToken);
                if (!result.Succeeded)
                {
                    await settlementRepository.MarkPrintFailedAsync(
                        settlement.SettlementGuid,
                        result.Message,
                        DateTimeOffset.UtcNow,
                        CancellationToken.None);
                    RequestSettlementUpload();
                    return result;
                }

                // 每次成功返回都代表一张回单已经物理输出；后续回单失败时也必须保留这次审计。
                await settlementRepository.MarkPrintedAsync(
                    settlement.SettlementGuid,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None);
                RequestSettlementUpload();
            }
        }
        catch (Exception ex)
        {
            await settlementRepository.MarkPrintFailedAsync(
                settlement.SettlementGuid,
                ex.Message,
                DateTimeOffset.UtcNow,
                CancellationToken.None);
            RequestSettlementUpload();
            return new ReceiptPrintResult(false, ex.Message);
        }

        await MarkCloudBackendReceiptPrintedAsync(settlement, cancellationToken);
        return result;
    }

    public Task<IReadOnlyList<LocalLinklySettlementRecord>> GetHistoryAsync(
        PosSessionState session,
        DateTime businessDate,
        CancellationToken cancellationToken = default)
    {
        return settlementRepository.GetByBusinessDateAsync(
            session.StoreCode,
            session.DeviceCode,
            businessDate,
            cancellationToken);
    }

    public async Task<LinklySettlementManualResolutionResult> ResolveUncertainAsync(
        PosSessionState session,
        LocalLinklySettlementRecord settlement,
        LocalLinklySettlementManualResolution resolution,
        CancellationToken cancellationToken = default)
    {
        if (!IsManualResolutionEligible(session, settlement))
        {
            return new LinklySettlementManualResolutionResult(
                false,
                settlement,
                "Only unresolved Local IP or Cloud Direct Linkly settlements for this POS can be manually resolved.");
        }

        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        if (settings.Processor != CardProcessorKind.Linkly)
        {
            return new LinklySettlementManualResolutionResult(
                false,
                settlement,
                "Linkly settlement resolution is unavailable because Linkly is not the active card processor.");
        }

        var current = (await settlementRepository.GetByBusinessDateAsync(
                session.StoreCode,
                session.DeviceCode,
                settlement.BusinessDate,
                cancellationToken))
            .FirstOrDefault(item => item.SettlementGuid == settlement.SettlementGuid);
        if (current is null || !IsManualResolutionEligible(session, current))
        {
            return new LinklySettlementManualResolutionResult(
                false,
                settlement,
                "The selected Linkly settlement is no longer eligible for manual resolution. Refresh the history and try again.");
        }

        // CAS 只更新同一 revision 的未决记录；这里不会调用终端，也不会创建新的 settlement。
        var resolved = await settlementRepository.TryResolveUncertainAsync(
            current.SettlementGuid,
            settlement.PayloadRevision,
            resolution,
            DateTimeOffset.UtcNow,
            cancellationToken);
        if (!resolved)
        {
            return new LinklySettlementManualResolutionResult(
                false,
                current,
                "The Linkly settlement changed before the decision was saved. Refresh the history and verify it again.");
        }

        RequestSettlementUpload();
        var refreshed = (await settlementRepository.GetByBusinessDateAsync(
                session.StoreCode,
                session.DeviceCode,
                current.BusinessDate,
                cancellationToken))
            .FirstOrDefault(item => item.SettlementGuid == current.SettlementGuid) ?? current;
        return new LinklySettlementManualResolutionResult(
            true,
            refreshed,
            "The Linkly settlement was manually resolved and queued for upload.");
    }

    private async Task AcknowledgeCloudBackendSettlementAsync(
        CardTerminalSettings settings,
        LocalLinklySettlementRecord settlement,
        CancellationToken cancellationToken)
    {
        if (backendTerminalClient is null ||
            settlement.Status is not (LocalLinklySettlementStatus.Succeeded or LocalLinklySettlementStatus.Failed) ||
            string.IsNullOrWhiteSpace(settlement.ProviderSessionId) ||
            !TryGetStoredCloudBackendSettings(settings, settlement, out var backendSettings))
        {
            return;
        }

        try
        {
            await backendTerminalClient.AcknowledgeSettlementAsync(backendSettings, settlement.ProviderSessionId, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HBPOS][Client][Settlement] acknowledge failed session={settlement.ProviderSessionId} error={ex.GetType().Name}");
        }
    }

    private async Task<LinklySettlementExecutionResult> RecoverResumableSettlementAsync(
        CardTerminalSettings settings,
        LocalLinklySettlementRecord unresolvedSettlement,
        CancellationToken cancellationToken)
    {
        if (backendTerminalClient is null ||
            !string.Equals(unresolvedSettlement.Environment, settings.Environment.ToString(), StringComparison.Ordinal))
        {
            return BlockUnresolvedSettlement(unresolvedSettlement);
        }

        if (!TryGetStoredCloudBackendSettings(settings, unresolvedSettlement, out var backendSettings))
        {
            return BlockUnresolvedSettlement(unresolvedSettlement);
        }

        LinklyCloudBackendSessionResponse? resumable;
        try
        {
            resumable = await backendTerminalClient.GetResumableSettlementAsync(backendSettings, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // 只传播调用方主动取消；内部超时等非调用方取消按未决结算阻塞处理，不重发、不覆盖状态。
            Console.WriteLine($"[HBPOS][Client][Settlement] resumable lookup failed error={ex.GetType().Name}");
            return BlockUnresolvedSettlement(unresolvedSettlement);
        }

        if (resumable is null ||
            string.IsNullOrWhiteSpace(resumable.SessionId) ||
            (!string.IsNullOrWhiteSpace(unresolvedSettlement.ProviderSessionId) &&
             !string.Equals(unresolvedSettlement.ProviderSessionId, resumable.SessionId, StringComparison.Ordinal)) ||
            !IsDeliverableResumableSettlement(resumable))
        {
            return BlockUnresolvedSettlement(unresolvedSettlement);
        }

        var settlement = unresolvedSettlement;
        if (string.IsNullOrWhiteSpace(settlement.ProviderSessionId))
        {
            try
            {
                await settlementRepository.BindProviderSessionAsync(
                    settlement.SettlementGuid,
                    resumable.SessionId,
                    CancellationToken.None);
                RequestSettlementUpload();
                settlement = settlement with { ProviderSessionId = resumable.SessionId };
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return BlockUnresolvedSettlement(unresolvedSettlement);
            }
        }

        var receiptTexts = (resumable.SettlementReceiptTexts ?? [])
            .Where(receipt => !string.IsNullOrWhiteSpace(receipt))
            .ToArray();
        var failed = LinklyCloudBackendStatusConstants.IsSettlementFailureStatus(resumable.Status) ||
            !LinklyCloudBackendStatusConstants.IsSuccessfulSettlement(
                resumable.OperationSuccess,
                resumable.ResponseCode);
        var completion = new LocalLinklySettlementCompletion(
            failed ? LocalLinklySettlementStatus.Failed : LocalLinklySettlementStatus.Succeeded,
            resumable.ResponseCode ?? settlement.ResponseCode,
            resumable.ResponseText ?? resumable.DisplayText ?? settlement.ResponseText,
            resumable.SettlementData ?? settlement.SettlementData,
            receiptTexts.Length > 0 ? receiptTexts : settlement.ReceiptTexts,
            DateTimeOffset.UtcNow,
            ProviderSubmissionState.Submitted);
        await settlementRepository.CompleteAsync(settlement.SettlementGuid, completion, CancellationToken.None);
        RequestSettlementUpload();
        settlement = settlement with
        {
            Status = completion.Status,
            ResponseCode = completion.ResponseCode,
            ResponseText = completion.ResponseText,
            SettlementData = completion.SettlementData,
            ReceiptTexts = completion.ReceiptTexts ?? [],
            CompletedAt = completion.CompletedAt,
            ProviderSubmissionState = completion.ProviderSubmissionState
        };

        await AcknowledgeCloudBackendSettlementAsync(settings, settlement, cancellationToken);
        var printResult = await ReprintAsync(settlement, cancellationToken);
        return new LinklySettlementExecutionResult(settlement, printResult);
    }

    private static LinklySettlementExecutionResult BlockUnresolvedSettlement(LocalLinklySettlementRecord settlement)
    {
        return new LinklySettlementExecutionResult(
            settlement,
            PrintResult: null,
            ResultUnknown: settlement.Status == LocalLinklySettlementStatus.Unknown);
    }

    private static bool IsDeliverableResumableSettlement(LinklyCloudBackendSessionResponse resumable)
    {
        return LinklyCloudBackendStatusConstants.IsSettlementFailureStatus(resumable.Status) ||
            (resumable.OperationSuccess is not null &&
             (resumable.SettlementReceiptTexts ?? []).Any(receipt => !string.IsNullOrWhiteSpace(receipt)));
    }

    private static bool TryGetStoredCloudBackendSettings(
        CardTerminalSettings currentSettings,
        LocalLinklySettlementRecord settlement,
        out CardTerminalSettings backendSettings)
    {
        backendSettings = currentSettings;
        if (!string.Equals(settlement.ConnectionMode, LinklyConnectionMode.CloudBackendAsync.ToString(), StringComparison.Ordinal) ||
            !Enum.TryParse<CardTerminalEnvironment>(settlement.Environment, ignoreCase: true, out var environment))
        {
            return false;
        }

        backendSettings = currentSettings with
        {
            LinklyConnectionMode = LinklyConnectionMode.CloudBackendAsync,
            Environment = environment
        };
        return true;
    }

    private async Task<LocalLinklySettlementRecord> BindOrReuseSettlementAsync(
        LocalLinklySettlementRecord pendingSettlement,
        string providerSessionId,
        CancellationToken cancellationToken)
    {
        var existing = await settlementRepository.GetByProviderSessionIdAsync(providerSessionId, cancellationToken);
        if (existing is not null)
        {
            if (!await settlementRepository.DeleteUnboundPendingAsync(pendingSettlement.SettlementGuid, CancellationToken.None))
            {
                throw new InvalidOperationException("The pending Linkly settlement could not be safely replaced.");
            }

            return existing;
        }

        try
        {
            await settlementRepository.BindProviderSessionAsync(
                pendingSettlement.SettlementGuid,
                providerSessionId,
                CancellationToken.None);
            RequestSettlementUpload();
            return pendingSettlement with
            {
                ProviderSessionId = providerSessionId,
                ProviderSubmissionState = ProviderSubmissionState.Submitted
            };
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // 同一云端 session 的并发恢复只复用既有记录，绝不放宽本地唯一索引。
            existing = await settlementRepository.GetByProviderSessionIdAsync(providerSessionId, CancellationToken.None);
            if (existing is null ||
                !await settlementRepository.DeleteUnboundPendingAsync(pendingSettlement.SettlementGuid, CancellationToken.None))
            {
                throw;
            }

            return existing;
        }
    }

    private static bool IsDefinitive(LocalLinklySettlementStatus status)
    {
        return status is LocalLinklySettlementStatus.Succeeded or LocalLinklySettlementStatus.Failed;
    }

    private static bool IsManualResolutionEligible(
        PosSessionState session,
        LocalLinklySettlementRecord settlement)
    {
        return string.Equals(settlement.StoreCode, session.StoreCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(settlement.DeviceCode, session.DeviceCode, StringComparison.OrdinalIgnoreCase) &&
            settlement.Status is LocalLinklySettlementStatus.Pending or LocalLinklySettlementStatus.Unknown &&
            (string.Equals(settlement.ConnectionMode, LinklyConnectionMode.LocalIp.ToString(), StringComparison.Ordinal) ||
             string.Equals(settlement.ConnectionMode, LinklyConnectionMode.CloudDirectSync.ToString(), StringComparison.Ordinal));
    }

    private void RequestSettlementUpload()
    {
        try
        {
            settlementUploadScheduler?.RequestUpload();
        }
        catch (Exception ex)
        {
            // 上传唤醒失败不能影响银行结算或本地 POS 小票打印。
            Console.WriteLine($"[HBPOS][Client][Settlement] upload wake-up failed error={ex.GetType().Name}");
        }
    }

    private async Task MarkCloudBackendReceiptPrintedAsync(
        LocalLinklySettlementRecord settlement,
        CancellationToken cancellationToken)
    {
        if (backendTerminalClient is null ||
            settlement.Status is not (LocalLinklySettlementStatus.Succeeded or LocalLinklySettlementStatus.Failed) ||
            string.IsNullOrWhiteSpace(settlement.ProviderSessionId))
        {
            return;
        }

        try
        {
            var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
            if (!TryGetStoredCloudBackendSettings(settings, settlement, out var backendSettings))
            {
                return;
            }

            await backendTerminalClient.MarkSettlementReceiptPrintedAsync(backendSettings, settlement.ProviderSessionId, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HBPOS][Client][Settlement] receipt printed marker failed session={settlement.ProviderSessionId} error={ex.GetType().Name}");
        }
    }
}
