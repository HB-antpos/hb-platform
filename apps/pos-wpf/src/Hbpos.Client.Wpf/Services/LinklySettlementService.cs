using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Linkly;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Wpf.Services;

public sealed record LinklySettlementExecutionResult(
    LocalLinklySettlementRecord Settlement,
    ReceiptPrintResult? PrintResult,
    bool ResultUnknown = false,
    bool ReusedFinalEvidence = false,
    // 前一营业日还有未决的 CloudBackendAsync 结算，今天的结算因此没有发送；Settlement 就是那条旧记录。
    bool BlockedByEarlierBusinessDay = false);

public sealed record LinklySettlementManualResolutionResult(
    bool Resolved,
    LocalLinklySettlementRecord Settlement,
    string Message);

// 结算前的刷卡机预检结果。Ready=false 时结算请求不会发出，也不会产生任何本地或服务器记录。
public sealed record LinklySettlementTerminalCheck(
    bool Ready,
    bool Offline = false,
    string? Message = null)
{
    public static LinklySettlementTerminalCheck Passed { get; } = new(true);
}

// 刷卡机未就绪、结算被拦下：此时尚未创建结算记录，没有需要上传或处理的数据，收银员排除故障后可直接重试。
public sealed class LinklySettlementTerminalUnavailableException(LinklySettlementTerminalCheck check)
    : InvalidOperationException(check.Message ?? "The card terminal is not ready, so the Linkly settlement was not sent.")
{
    public LinklySettlementTerminalCheck Check { get; } = check;
}

public interface ILinklySettlementService
{
    // 发送结算前先确认刷卡机在线：离线时直接说明、不发送，避免留下没有金额的失败结算记录。
    // 默认视为通过，不支持预检的实现保持原有行为。
    Task<LinklySettlementTerminalCheck> CheckTerminalReadyAsync(
        PosSessionState session,
        DateTime businessDate,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(LinklySettlementTerminalCheck.Passed);

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

    // 只查询、补录：向 Linkly 服务端查询一条未决 CloudBackendAsync 结算的结果并写回本地，绝不发送新结算。
    // 前一营业日遗留的未决结算靠它补完（营业日页切到那一天选中记录即可）。
    Task<LinklySettlementManualResolutionResult> QueryUnresolvedAsync(
        PosSessionState session,
        LocalLinklySettlementRecord settlement,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new LinklySettlementManualResolutionResult(
            false,
            settlement,
            "Querying an unresolved Linkly settlement is not supported by this service."));

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
    IPaymentMethodSettingsService? paymentMethodSettingsService = null,
    TimeProvider? timeProvider = null) : ILinklySettlementService
{
    // 一次结算前最多处理多少条前一营业日遗留的服务端会话，防止异常数据造成无限循环。
    private const int MaxEarlierSessionsPerRun = 5;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    // 预检只是问一次刷卡机状态，用短超时：刷卡机真离线时不能让收银员干等业务级超时。
    private static readonly TimeSpan TerminalCheckTimeout = TimeSpan.FromSeconds(10);

    public async Task<LinklySettlementTerminalCheck> CheckTerminalReadyAsync(
        PosSessionState session,
        DateTime businessDate,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);

        // 已有未决（Pending/Unknown）记录时，结算本来就会被阻塞并给出专门提示，不能在这里误报成“刷卡机离线”。
        var existingSettlements = await settlementRepository.GetByBusinessDateAsync(
            session.StoreCode,
            session.DeviceCode,
            businessDate,
            cancellationToken);
        if (existingSettlements.Any(settlement =>
                settlement.Status is LocalLinklySettlementStatus.Pending or LocalLinklySettlementStatus.Unknown))
        {
            return LinklySettlementTerminalCheck.Passed;
        }

        return await CheckTerminalAsync(settings, cancellationToken);
    }

    private async Task<LinklySettlementTerminalCheck> CheckTerminalAsync(
        CardTerminalSettings settings,
        CancellationToken cancellationToken)
    {
        // 只有本地 IP 模式能直接向 EFT-Client 查刷卡机状态；云端模式没有等价的轻量查询，保持原有行为。
        if (settings.Processor != CardProcessorKind.Linkly ||
            CardTerminalSettings.NormalizeLinklyConnectionMode(settings.LinklyConnectionMode) != LinklyConnectionMode.LocalIp)
        {
            return LinklySettlementTerminalCheck.Passed;
        }

        var result = await terminalClient.TestConnectionAsync(
            settings.LinklyHost,
            settings.LinklyPort,
            TerminalCheckTimeout,
            cancellationToken);

        // 在线但尚未登录银行网络（Succeeded 且 PinPadLoggedOn=false）仍放行：终端会自己回银行响应，不属于离线。
        return result.Succeeded
            ? LinklySettlementTerminalCheck.Passed
            : new LinklySettlementTerminalCheck(false, result.PinPadOffline, result.Message);
    }

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

        // 前一营业日遗留的结算（服务端未 ack 的会话、本地未决记录）必须先单独补完或结案，再发今天的结算：
        // 否则 resumable 会把旧会话当成今天的结算接管，今天的批次没发给银行却显示成功，还会打印旧回单。
        var earlierBlock = await ReconcileEarlierBusinessDaysAsync(settings, session, businessDate, cancellationToken);
        if (earlierBlock is not null)
        {
            return earlierBlock;
        }

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

        // 刷卡机离线或连不上时在这里拦下：此时还没创建任何记录，本地和服务器都不会留下没有金额的失败结算。
        // 必须放在未决记录分支之后，已有未决记录时的阻塞提示不受影响；VM 在确认框之前也会预检一次，这里兜住确认期间掉线。
        var terminalCheck = await CheckTerminalAsync(settings, cancellationToken);
        if (!terminalCheck.Ready)
        {
            ConsoleLog.WriteWarning(
                "LinklySettlement",
                $"settlement blocked before send: terminal not ready offline={terminalCheck.Offline} message={terminalCheck.Message}",
                new ApplicationLogContext(
                    Properties: new Dictionary<string, object?>
                    {
                        ["storeCode"] = session.StoreCode,
                        ["deviceCode"] = session.DeviceCode,
                        ["mode"] = settings.LinklyConnectionMode.ToString(),
                        ["result"] = terminalCheck.Offline ? "pinpad-offline" : "terminal-not-ready"
                    }));
            throw new LinklySettlementTerminalUnavailableException(terminalCheck);
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
            // 结算结果未知需要人工到 Linkly 核对，记 Error 并带异常。
            ConsoleLog.WriteError(
                "LinklySettlement",
                $"settlement terminal call failed settlementGuid={settlement.SettlementGuid:D} " +
                $"mode={settlement.ConnectionMode} environment={settlement.Environment} error={ex.GetType().Name} -> Unknown",
                BuildSettlementContext(settlement, status: LocalLinklySettlementStatus.Unknown),
                ex);
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
        LogTerminalResult(settlement, terminalResult, status, submissionState);
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
                "Only unresolved Linkly settlements for this POS can be manually resolved.");
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

        // CloudBackendAsync：本地还没绑定服务端会话时，尽力把同一营业日的未 ack 会话绑上，
        // 这样结案后 ack 才能关闭服务端会话，换线、配对不再被它挡住。
        var expectedRevision = settlement.PayloadRevision;
        if (IsCloudBackend(current))
        {
            var bound = await TryBindSameDayResumableSessionAsync(settings, current, cancellationToken);
            if (!ReferenceEquals(bound, current) && current.PayloadRevision == settlement.PayloadRevision)
            {
                // 绑定本身会让修订号 +1；界面载入的记录没有过期时，期望修订号跟着走，过期的仍然会被 CAS 拒绝。
                expectedRevision = bound.PayloadRevision;
            }

            current = bound;
        }

        // CAS 只更新同一 revision 的未决记录；这里不会调用终端，也不会创建新的 settlement。
        var resolved = await settlementRepository.TryResolveUncertainAsync(
            current.SettlementGuid,
            expectedRevision,
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
        if (IsCloudBackend(current) && !string.IsNullOrWhiteSpace(current.ProviderSessionId))
        {
            // 主管结案要让服务端把非终态会话记为 SupervisorResolved；ack 失败不影响本地结案，
            // 下次结算前的补发（ReplayAcknowledge）会按服务端仍未结案的状态再次带上主管标记。
            await AcknowledgeCloudBackendSettlementAsync(
                settings,
                current with { Status = LocalLinklySettlementStatus.Failed },
                cancellationToken,
                supervisorResolved: true);
        }

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
        CancellationToken cancellationToken,
        bool supervisorResolved = false)
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
            if (supervisorResolved)
            {
                await backendTerminalClient.AcknowledgeSupervisorResolvedSettlementAsync(backendSettings, settlement.ProviderSessionId, cancellationToken);
            }
            else
            {
                await backendTerminalClient.AcknowledgeSettlementAsync(backendSettings, settlement.ProviderSessionId, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // ack 失败不影响本地结果，后端会保留可恢复会话，下次结算前会按服务端未 ack 的会话补发。
            ConsoleLog.WriteWarning(
                "LinklySettlement",
                $"settlement acknowledge failed settlementGuid={settlement.SettlementGuid:D} sessionId={settlement.ProviderSessionId} error={ex.GetType().Name}",
                BuildSettlementContext(settlement),
                ex);
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
            ConsoleLog.WriteWarning(
                "LinklySettlement",
                $"settlement resumable lookup failed settlementGuid={unresolvedSettlement.SettlementGuid:D} error={ex.GetType().Name} -> blocked",
                BuildSettlementContext(unresolvedSettlement),
                ex);
            return BlockUnresolvedSettlement(unresolvedSettlement);
        }

        if (resumable is null &&
            string.IsNullOrWhiteSpace(unresolvedSettlement.ProviderSessionId) &&
            IsNotInFlight(unresolvedSettlement))
        {
            // 本地没有 sessionId，服务端也没有任何未 ack 的结算会话：请求没有创建出会话
            // （例如服务端在建会话前就拒绝了），可以确认未提交。否则这条记录会一直是未决，当天再也无法结算。
            return await ConfirmNotSubmittedAsync(unresolvedSettlement, cancellationToken);
        }

        if (resumable is null ||
            string.IsNullOrWhiteSpace(resumable.SessionId) ||
            (!string.IsNullOrWhiteSpace(unresolvedSettlement.ProviderSessionId) &&
             !string.Equals(unresolvedSettlement.ProviderSessionId, resumable.SessionId, StringComparison.Ordinal)))
        {
            return BlockUnresolvedSettlement(unresolvedSettlement);
        }

        if (string.IsNullOrWhiteSpace(unresolvedSettlement.ProviderSessionId) &&
            !BelongsToBusinessDayOf(unresolvedSettlement, resumable))
        {
            // 绑定一个没有 sessionId 的记录前必须确认营业日一致：resumable 不按营业日过滤，
            // 否则别的营业日的会话会被绑到当天的记录上，结果记错日期。
            ConsoleLog.WriteWarning(
                "LinklySettlement",
                $"settlement resumable session belongs to another business day settlementGuid={unresolvedSettlement.SettlementGuid:D} " +
                $"businessDate={unresolvedSettlement.BusinessDate:yyyy-MM-dd} sessionId={resumable.SessionId} -> blocked",
                BuildSettlementContext(unresolvedSettlement));
            return BlockUnresolvedSettlement(unresolvedSettlement);
        }

        return await ApplyBackendSessionAsync(settings, unresolvedSettlement, resumable, cancellationToken)
            ?? BlockUnresolvedSettlement(unresolvedSettlement);
    }

    /// <summary>
    /// 把服务端已交付的结算会话结果写回本地未决记录：绑定 sessionId、落库、ack、打印回单。
    /// 会话还不能交付（仍在进行或结果未知）时返回 null，本地记录保持未决。
    /// </summary>
    private async Task<LinklySettlementExecutionResult?> ApplyBackendSessionAsync(
        CardTerminalSettings settings,
        LocalLinklySettlementRecord unresolvedSettlement,
        LinklyCloudBackendSessionResponse resumable,
        CancellationToken cancellationToken)
    {
        if (!IsDeliverableResumableSettlement(resumable))
        {
            return null;
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
                return null;
            }
        }

        var receiptTexts = (resumable.SettlementReceiptTexts ?? [])
            .Where(receipt => !string.IsNullOrWhiteSpace(receipt))
            .ToArray();
        var failed = LinklyCloudBackendStatusConstants.IsSettlementFailureStatus(resumable.Status) ||
            resumable.OperationSuccess == false ||
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

    /// <summary>
    /// 发今天的结算之前，先把前一营业日遗留的 CloudBackendAsync 结算补完或结案：
    /// 1) 服务端还没 ack 的前一营业日会话：对应本地记录已定论就补发 ack，未决就尝试补录结果，本地没有记录就补建一条；
    /// 2) 其余本地未决的前一营业日记录单独向服务端核对（没有 sessionId 且服务端没有会话的，确认为未提交）。
    /// 仍无法补完时返回阻塞结果，今天的结算不发送，由主管在对应营业日查询或结案。
    /// </summary>
    private async Task<LinklySettlementExecutionResult?> ReconcileEarlierBusinessDaysAsync(
        CardTerminalSettings settings,
        PosSessionState session,
        DateTime businessDate,
        CancellationToken cancellationToken)
    {
        if (backendTerminalClient is null)
        {
            return null;
        }

        var today = businessDate.Date;
        var blocked = await DrainEarlierServerSessionsAsync(settings, session, today, cancellationToken);
        if (blocked is not null)
        {
            return blocked;
        }

        var unresolved = await settlementRepository.GetUnresolvedAsync(
            session.StoreCode,
            session.DeviceCode,
            settings.Environment.ToString(),
            cancellationToken);
        foreach (var earlier in unresolved.Where(record => record.BusinessDate.Date < today && IsCloudBackend(record)))
        {
            var recovered = await RecoverResumableSettlementAsync(settings, earlier, cancellationToken);
            if (recovered.Settlement.Status is LocalLinklySettlementStatus.Pending or LocalLinklySettlementStatus.Unknown)
            {
                return BlockByEarlierBusinessDay(recovered.Settlement);
            }
        }

        return null;
    }

    private static LinklySettlementExecutionResult BlockByEarlierBusinessDay(LocalLinklySettlementRecord settlement)
    {
        return new LinklySettlementExecutionResult(
            settlement,
            PrintResult: null,
            ResultUnknown: settlement.Status == LocalLinklySettlementStatus.Unknown,
            BlockedByEarlierBusinessDay: true);
    }

    private async Task<LinklySettlementExecutionResult?> DrainEarlierServerSessionsAsync(
        CardTerminalSettings settings,
        PosSessionState session,
        DateTime today,
        CancellationToken cancellationToken)
    {
        if (settings.LinklyConnectionMode != LinklyConnectionMode.CloudBackendAsync)
        {
            return null;
        }

        string? lastSessionId = null;
        for (var i = 0; i < MaxEarlierSessionsPerRun; i++)
        {
            LinklyCloudBackendSessionResponse? resumable;
            try
            {
                resumable = await backendTerminalClient!.GetResumableSettlementAsync(settings, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // 查不到旧会话时不阻塞今天的结算：后端客户端自己也不会接管前一营业日的会话。
                ConsoleLog.WriteWarning(
                    "LinklySettlement",
                    $"earlier settlement session lookup failed error={ex.GetType().Name}",
                    exception: ex);
                return null;
            }

            if (resumable is null ||
                string.IsNullOrWhiteSpace(resumable.SessionId) ||
                LinklySettlementBusinessDay.Of(resumable.CreatedAt, _timeProvider) is not { } sessionDay ||
                sessionDay >= today)
            {
                // 没有、或属于今天（无 CreatedAt 的旧服务端也走这里）：交给原有的同日恢复流程。
                return null;
            }

            if (string.Equals(lastSessionId, resumable.SessionId, StringComparison.Ordinal))
            {
                // 上一轮处理后同一会话还在（ack 失败）：不再循环，留给下次。
                return null;
            }

            lastSessionId = resumable.SessionId;
            var blocked = await HandleEarlierServerSessionAsync(settings, session, resumable, sessionDay, cancellationToken);
            if (blocked is not null)
            {
                return blocked;
            }
        }

        return null;
    }

    private async Task<LinklySettlementExecutionResult?> HandleEarlierServerSessionAsync(
        CardTerminalSettings settings,
        PosSessionState session,
        LinklyCloudBackendSessionResponse resumable,
        DateTime sessionDay,
        CancellationToken cancellationToken)
    {
        var local = await settlementRepository.GetByProviderSessionIdAsync(resumable.SessionId, cancellationToken);
        if (local is null)
        {
            // 本地没有记录绑定这个会话：优先接到同一营业日、还没拿到 sessionId 的未决记录上
            // （启动请求超时或响应丢失留下的）；确实没有再补建一条，保证每个服务端会话都有本地记录可审计、可结案。
            var candidates = (await settlementRepository.GetUnresolvedAsync(
                    session.StoreCode,
                    session.DeviceCode,
                    settings.Environment.ToString(),
                    cancellationToken))
                .Where(record => IsCloudBackend(record) &&
                    string.IsNullOrWhiteSpace(record.ProviderSessionId) &&
                    record.BusinessDate.Date == sessionDay)
                .ToList();
            if (candidates.Count > 1)
            {
                return BlockByEarlierBusinessDay(candidates[0]);
            }

            local = candidates.Count == 1
                ? candidates[0]
                : await AdoptOrphanSessionAsync(settings, session, resumable, sessionDay, cancellationToken);
            if (local is null)
            {
                return null;
            }
        }

        if (local.Status is LocalLinklySettlementStatus.Pending or LocalLinklySettlementStatus.Unknown)
        {
            var applied = await ApplyBackendSessionAsync(settings, local, resumable, cancellationToken);
            return applied is null
                ? BlockByEarlierBusinessDay(local)
                : null;
        }

        // 本地早已定论，只是当时 ack 没成功：补发。服务端会话若仍是非终态（主管结案的情形），要带上主管标记才能关闭。
        await AcknowledgeCloudBackendSettlementAsync(
            settings,
            local,
            cancellationToken,
            supervisorResolved: !IsServerFinalStatus(resumable.Status));
        return null;
    }

    private static bool IsServerFinalStatus(string? status)
    {
        return string.Equals(status, LinklyCloudBackendStatusConstants.StatusCompleted, StringComparison.OrdinalIgnoreCase) ||
            LinklyCloudBackendStatusConstants.IsSettlementFailureStatus(status);
    }

    private async Task<LocalLinklySettlementRecord?> AdoptOrphanSessionAsync(
        CardTerminalSettings settings,
        PosSessionState session,
        LinklyCloudBackendSessionResponse resumable,
        DateTime sessionDay,
        CancellationToken cancellationToken)
    {
        var requestedAt = resumable.CreatedAt ?? _timeProvider.GetUtcNow();
        var orphan = new LocalLinklySettlementRecord(
            Guid.NewGuid(),
            session.StoreCode,
            session.DeviceCode,
            sessionDay,
            LinklyConnectionMode.CloudBackendAsync.ToString(),
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
        if (!await settlementRepository.TryCreatePendingAsync(orphan, cancellationToken))
        {
            // 那个营业日已有别的未决记录占位：不再强建，由调用方按阻塞处理。
            ConsoleLog.WriteWarning(
                "LinklySettlement",
                $"earlier settlement session could not be adopted: business date already has an unresolved record sessionId={resumable.SessionId}",
                BuildSettlementContext(orphan));
            return null;
        }

        RequestSettlementUpload();
        ConsoleLog.WriteWarning(
            "LinklySettlement",
            $"earlier settlement session had no local record; created settlementGuid={orphan.SettlementGuid:D} " +
            $"businessDate={sessionDay:yyyy-MM-dd} sessionId={resumable.SessionId} status={resumable.Status}",
            BuildSettlementContext(orphan));
        return orphan;
    }

    // 主管结案前尽力把同一营业日的未 ack 会话绑到没有 sessionId 的记录上；任何失败都当作没有可绑的会话。
    private async Task<LocalLinklySettlementRecord> TryBindSameDayResumableSessionAsync(
        CardTerminalSettings settings,
        LocalLinklySettlementRecord settlement,
        CancellationToken cancellationToken)
    {
        if (backendTerminalClient is null ||
            !string.IsNullOrWhiteSpace(settlement.ProviderSessionId) ||
            !TryGetStoredCloudBackendSettings(settings, settlement, out var backendSettings))
        {
            return settlement;
        }

        try
        {
            var resumable = await backendTerminalClient.GetResumableSettlementAsync(backendSettings, cancellationToken);
            if (resumable is null ||
                string.IsNullOrWhiteSpace(resumable.SessionId) ||
                LinklySettlementBusinessDay.Of(resumable.CreatedAt, _timeProvider) != settlement.BusinessDate.Date ||
                await settlementRepository.GetByProviderSessionIdAsync(resumable.SessionId, cancellationToken) is not null)
            {
                return settlement;
            }

            await settlementRepository.BindProviderSessionAsync(settlement.SettlementGuid, resumable.SessionId, CancellationToken.None);
            // 绑定会让 payload 修订号 +1；重新读取，后面的 CAS 才对得上。
            return (await settlementRepository.GetByBusinessDateAsync(
                    settlement.StoreCode,
                    settlement.DeviceCode,
                    settlement.BusinessDate,
                    cancellationToken))
                .FirstOrDefault(item => item.SettlementGuid == settlement.SettlementGuid) ?? settlement;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            ConsoleLog.WriteWarning(
                "LinklySettlement",
                $"settlement session lookup before supervisor resolution failed settlementGuid={settlement.SettlementGuid:D} error={ex.GetType().Name}",
                BuildSettlementContext(settlement),
                ex);
            return settlement;
        }
    }

    public async Task<LinklySettlementManualResolutionResult> QueryUnresolvedAsync(
        PosSessionState session,
        LocalLinklySettlementRecord settlement,
        CancellationToken cancellationToken = default)
    {
        if (backendTerminalClient is null ||
            !string.Equals(settlement.StoreCode, session.StoreCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(settlement.DeviceCode, session.DeviceCode, StringComparison.OrdinalIgnoreCase) ||
            settlement.Status is not (LocalLinklySettlementStatus.Pending or LocalLinklySettlementStatus.Unknown) ||
            !IsCloudBackend(settlement))
        {
            return new LinklySettlementManualResolutionResult(
                false,
                settlement,
                "Only unresolved Cloud Backend Linkly settlements for this POS can be queried.");
        }

        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        if (settings.Processor != CardProcessorKind.Linkly)
        {
            return new LinklySettlementManualResolutionResult(
                false,
                settlement,
                "Linkly settlement query is unavailable because Linkly is not the active card processor.");
        }

        LocalLinklySettlementRecord? Reload(IEnumerable<LocalLinklySettlementRecord> records) =>
            records.FirstOrDefault(item => item.SettlementGuid == settlement.SettlementGuid);

        // 查询、补录要和结算一样占用线路切换闸门，期间不能换线。
        await using var transitionLease = linklyTerminalSelectionTransitionGate is null
            ? null
            : await linklyTerminalSelectionTransitionGate.EnterFinancialOperationAsync(cancellationToken);

        // 先按服务端未 ack 的前一营业日会话逐个对账（返回的阻塞结果无需处理，下面会重新读取本条记录的最新状态）。
        await DrainEarlierServerSessionsAsync(settings, session, _timeProvider.GetLocalNow().Date, cancellationToken);
        var current = Reload(await settlementRepository.GetByBusinessDateAsync(
            settlement.StoreCode,
            settlement.DeviceCode,
            settlement.BusinessDate,
            cancellationToken));
        if (current is null)
        {
            return new LinklySettlementManualResolutionResult(
                false,
                settlement,
                "The selected Linkly settlement no longer exists. Refresh the history and try again.");
        }

        if (current.Status is LocalLinklySettlementStatus.Pending or LocalLinklySettlementStatus.Unknown)
        {
            var recovered = await RecoverResumableSettlementAsync(settings, current, cancellationToken);
            current = Reload(await settlementRepository.GetByBusinessDateAsync(
                settlement.StoreCode,
                settlement.DeviceCode,
                settlement.BusinessDate,
                cancellationToken)) ?? recovered.Settlement;
        }

        var resolved = current.Status is not (LocalLinklySettlementStatus.Pending or LocalLinklySettlementStatus.Unknown);
        return new LinklySettlementManualResolutionResult(
            resolved,
            current,
            resolved
                ? "The Linkly settlement result was found and recorded. No new settlement was sent."
                : "The Linkly settlement result is still unavailable. Record the supervisor decision if the terminal shows the result.");
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
        // 与服务端 ResolveCloudBackendFinalStatus 同口径：OperationSuccess=false 即失败终态（银行拒绝、刷卡机离线
        // 都可能没有 Type=S 回单）；成功必须带回单，否则确认后会永久丢失需要打印的结算单。
        return LinklyCloudBackendStatusConstants.IsSettlementFailureStatus(resumable.Status) ||
            resumable.OperationSuccess == false ||
            (resumable.OperationSuccess is not null &&
             (resumable.SettlementReceiptTexts ?? []).Any(receipt => !string.IsNullOrWhiteSpace(receipt)));
    }

    private static bool IsCloudBackend(LocalLinklySettlementRecord settlement)
    {
        return string.Equals(settlement.ConnectionMode, LinklyConnectionMode.CloudBackendAsync.ToString(), StringComparison.Ordinal);
    }

    // Pending 可能是另一次结算正在进行（尚未建出服务端会话），不能据此断定“未提交”；
    // Unknown 表示那次尝试已经结束，或 Pending 已久（崩溃遗留）才可以。
    private bool IsNotInFlight(LocalLinklySettlementRecord settlement)
    {
        return settlement.Status == LocalLinklySettlementStatus.Unknown ||
            _timeProvider.GetUtcNow() - settlement.RequestedAt >= LinklyTimeoutConstants.SettlementCallbackTimeout;
    }

    // 会话创建时间所在的本地日期必须等于本地记录的营业日；服务端还没有 CreatedAt（旧版本）时无法确认，
    // 只放行今天的记录，沿用旧行为，前一天的记录一律不绑定。
    private bool BelongsToBusinessDayOf(LocalLinklySettlementRecord settlement, LinklyCloudBackendSessionResponse resumable)
    {
        var sessionDay = LinklySettlementBusinessDay.Of(resumable.CreatedAt, _timeProvider);
        return sessionDay is { } day
            ? day == settlement.BusinessDate.Date
            : settlement.BusinessDate.Date == _timeProvider.GetLocalNow().Date;
    }

    private async Task<LinklySettlementExecutionResult> ConfirmNotSubmittedAsync(
        LocalLinklySettlementRecord settlement,
        CancellationToken cancellationToken)
    {
        ConsoleLog.WriteWarning(
            "LinklySettlement",
            $"settlement confirmed not submitted: no provider session and no resumable session settlementGuid={settlement.SettlementGuid:D} " +
            $"businessDate={settlement.BusinessDate:yyyy-MM-dd}",
            BuildSettlementContext(settlement, LocalLinklySettlementStatus.Failed));
        var completion = new LocalLinklySettlementCompletion(
            LocalLinklySettlementStatus.Failed,
            ResponseCode: null,
            ResponseText: "Settlement was confirmed as not submitted: the server has no settlement session for this POS.",
            SettlementData: null,
            ReceiptTexts: [],
            _timeProvider.GetUtcNow(),
            ProviderSubmissionState.NotSubmitted);
        await settlementRepository.CompleteAsync(settlement.SettlementGuid, completion, CancellationToken.None);
        RequestSettlementUpload();
        return new LinklySettlementExecutionResult(
            settlement with
            {
                Status = completion.Status,
                ResponseText = completion.ResponseText,
                ReceiptTexts = [],
                CompletedAt = completion.CompletedAt,
                ProviderSubmissionState = completion.ProviderSubmissionState
            },
            PrintResult: null);
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
             string.Equals(settlement.ConnectionMode, LinklyConnectionMode.CloudDirectSync.ToString(), StringComparison.Ordinal) ||
             // CloudBackendAsync 以前没有人工结案通道：结果未知的结算只能改库。现在结案后会带主管标记 ack 服务端会话。
             string.Equals(settlement.ConnectionMode, LinklyConnectionMode.CloudBackendAsync.ToString(), StringComparison.Ordinal));
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
            ConsoleLog.WriteWarning(
                "LinklySettlement",
                $"settlement upload wake-up failed error={ex.GetType().Name}",
                exception: ex);
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
            ConsoleLog.WriteWarning(
                "LinklySettlement",
                $"settlement receipt printed marker failed settlementGuid={settlement.SettlementGuid:D} sessionId={settlement.ProviderSessionId} error={ex.GetType().Name}",
                BuildSettlementContext(settlement),
                ex);
        }
    }

    /// <summary>
    /// 终端返回后记录结算结论：成功 Information；失败或结果未知 Warning（未知需人工到 Linkly 核对）。
    /// </summary>
    private static void LogTerminalResult(
        LocalLinklySettlementRecord settlement,
        LinklySettlementResult terminalResult,
        LocalLinklySettlementStatus status,
        ProviderSubmissionState submissionState)
    {
        var message =
            $"settlement terminal result settlementGuid={settlement.SettlementGuid:D} mode={settlement.ConnectionMode} " +
            $"status={status} sessionId={terminalResult.SessionId ?? "<null>"} submission={submissionState} " +
            $"responseCode={terminalResult.ResponseCode ?? "<null>"} resultUnknown={terminalResult.ResultUnknown}";
        var context = BuildSettlementContext(settlement, status, terminalResult.ResponseCode);
        if (status == LocalLinklySettlementStatus.Succeeded)
        {
            ConsoleLog.WriteInformation("LinklySettlement", message, context);
        }
        else
        {
            ConsoleLog.WriteWarning("LinklySettlement", $"{message} message={terminalResult.Message}", context);
        }
    }

    private static ApplicationLogContext BuildSettlementContext(
        LocalLinklySettlementRecord settlement,
        LocalLinklySettlementStatus? status = null,
        string? responseCode = null)
    {
        return new ApplicationLogContext(
            TraceId: settlement.SettlementGuid.ToString("D"),
            Properties: new Dictionary<string, object?>
            {
                ["storeCode"] = settlement.StoreCode,
                ["deviceCode"] = settlement.DeviceCode,
                ["mode"] = settlement.ConnectionMode,
                ["status"] = (status ?? settlement.Status).ToString(),
                ["result"] = responseCode
            });
    }
}
