using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public interface ILinklyCloudTerminalClient
{
    Task<LinklyConnectionTestResult> TestConnectionAsync(
        CardTerminalSettings settings,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken = default);

    Task<LinklySettlementResult> SettlementAsync(
        PosSessionState session,
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new LinklySettlementResult(
            false,
            "Linkly settlement is not supported by this cloud terminal client."));

    Task<PaymentAuthorizationResult> PurchaseAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default);

    Task<PaymentAuthorizationResult> RecoverTransactionAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string sessionId,
        string txnRef,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new PaymentAuthorizationResult(
            false,
            null,
            "Linkly Cloud transaction recovery is unavailable.",
            TxnType: "P",
            SessionId: sessionId,
            TxnRef: txnRef,
            ResultUnknown: true));

    Task<PaymentAuthorizationResult> RefundAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        CancellationToken cancellationToken = default);
}

public sealed class LinklyCloudTerminalClient(
    ILinklyCloudApiClient apiClient,
    ILinklyCloudSecretStore secretStore,
    TimeSpan? pollInterval = null,
    ILocalizationService? localization = null,
    ILinklyTerminalDialogService? dialogService = null,
    ILinklyPaymentAttemptContextAccessor? linklyPaymentAttemptContextAccessor = null,
    TimeProvider? timeProvider = null) : ILinklyCloudTerminalClient
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);
    // 取消后仍需等待原交易结果时的让出间隔；此时不能再用已取消的令牌等待，否则会同步返回而空转。
    private static readonly TimeSpan CancelledResultWaitInterval = TimeSpan.FromMilliseconds(50);
    private const string ProcessorName = "ANZ";
    private readonly TimeSpan _pollInterval = pollInterval.GetValueOrDefault(DefaultPollInterval);
    // 业务等待计时器走 TimeProvider，测试可注入 FakeTimeProvider 直接推进虚拟时间，不靠墙钟窗口判断是否超时。
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<LinklyConnectionTestResult> TestConnectionAsync(
        CardTerminalSettings settings,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken = default)
    {
        var posVendorId = CardTerminalSettings.ResolveLinklyPosVendorId(
            settings.Environment,
            settings.LinklyPosVendorId);
        Log($"test start environment={settings.Environment} store={LogValue(storeCode)} device={LogValue(deviceCode)} hasSecret={!string.IsNullOrWhiteSpace(settings.LinklyCloudSecret)} hasVendorId={!string.IsNullOrWhiteSpace(posVendorId)}");
        try
        {
            var endpointValidationMessage = ValidateEndpointSettings(settings);
            if (!string.IsNullOrWhiteSpace(endpointValidationMessage))
            {
                Log($"test blocked environment={settings.Environment} reason=invalid-endpoint");
                return new LinklyConnectionTestResult(false, endpointValidationMessage);
            }

            var token = await GetTokenAsync(settings, storeCode, deviceCode, cancellationToken);
            var result = await apiClient.SendLogonAsync(settings, token.Token, cancellationToken);
            var message = FormatResponseMessage(result.ResponseText, result.ResponseCode);
            Log($"test logon completed environment={settings.Environment} store={LogValue(storeCode)} device={LogValue(deviceCode)} success={result.Succeeded} responseCode={LogValue(result.ResponseCode)}");
            return result.Succeeded
                ? new LinklyConnectionTestResult(true, string.IsNullOrWhiteSpace(message) ? T("linkly.cloud.test.success", "Linkly Cloud logon succeeded.") : message)
                : new LinklyConnectionTestResult(false, string.IsNullOrWhiteSpace(message) ? T("linkly.cloud.test.failed", "Linkly Cloud logon failed.") : message);
        }
        catch (LinklyCloudApiException ex)
        {
            Log($"test failed environment={settings.Environment} store={LogValue(storeCode)} device={LogValue(deviceCode)} authFailure={ex.IsAuthenticationFailure} error={ex.GetType().Name}");
            return new LinklyConnectionTestResult(false, ex.IsAuthenticationFailure
                ? T("linkly.cloud.pairingInvalid", "Linkly Cloud pairing is invalid. Pair the terminal again.")
                : ex.Message);
        }
    }

    public Task<PaymentAuthorizationResult> PurchaseAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default)
    {
        return RunTransactionAsync(
            "P",
            amount,
            session,
            settings,
            refundReference: null,
            cancellationToken);
    }

    public Task<PaymentAuthorizationResult> RefundAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        CancellationToken cancellationToken = default)
    {
        var refundReference = TryParseRefundReference(originalReference);
        Log(
            $"refund reference resolved originalReference={LogValue(originalReference)} refundReference={LogValue(refundReference)} " +
            $"hasRefundReference={!string.IsNullOrWhiteSpace(refundReference)}");
        return string.IsNullOrWhiteSpace(refundReference)
            ? Task.FromResult(new PaymentAuthorizationResult(false, null, T("linkly.cloud.refundMissingReference", "Linkly Cloud refund requires an original RFN reference.")))
            : RunTransactionAsync(
                "R",
                amount,
                session,
                settings,
                refundReference,
                cancellationToken);
    }

    public async Task<PaymentAuthorizationResult> RecoverTransactionAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string sessionId,
        string txnRef,
        CancellationToken cancellationToken = default)
    {
        var requestedSessionId = NormalizeOptional(sessionId);
        var requestedTxnRef = NormalizeOptional(txnRef);
        var unknownMessage = T(
            "linkly.cloud.resultUnknown",
            "Linkly Cloud transaction result is unknown. Confirm the Linkly transaction status before retrying.");

        if (amount <= 0m || requestedSessionId is null || requestedTxnRef is null)
        {
            return RecoveryUnknown(
                requestedSessionId,
                requestedTxnRef,
                T("linkly.cloud.resultUnknown", unknownMessage));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return RecoveryUnknown(requestedSessionId, requestedTxnRef, unknownMessage);
        }

        if (CardTerminalSettings.NormalizeLinklyConnectionMode(settings.LinklyConnectionMode) !=
            LinklyConnectionMode.CloudDirectSync)
        {
            return RecoveryUnknown(requestedSessionId, requestedTxnRef, unknownMessage);
        }

        if (string.IsNullOrWhiteSpace(settings.LinklyCloudSecret) ||
            string.IsNullOrWhiteSpace(CardTerminalSettings.ResolveLinklyPosVendorId(
                settings.Environment,
                settings.LinklyPosVendorId)))
        {
            return RecoveryUnknown(requestedSessionId, requestedTxnRef, unknownMessage);
        }

        var endpointValidationMessage = ValidateEndpointSettings(settings);
        if (!string.IsNullOrWhiteSpace(endpointValidationMessage))
        {
            return RecoveryUnknown(requestedSessionId, requestedTxnRef, endpointValidationMessage);
        }

        // CancelAfter 不接受 TimeProvider，改为独立的业务等待计时 CTS 再与外部令牌链接，语义不变。
        using var businessWaitCts = new CancellationTokenSource(LinklyTimeoutPolicy.BusinessWait, _timeProvider);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, businessWaitCts.Token);
        try
        {
            Log($"transaction recovery start sessionId={requestedSessionId} txnRef={LogValue(requestedTxnRef)} amountMinor={ToMinorUnits(amount)}");
            var token = await GetTokenAsync(settings, session.StoreCode, session.DeviceCode, timeoutCts.Token);
            LinklyCloudTransactionResult result;
            try
            {
                // 中文注释：恢复只查询已持久化的原 session，严禁把查询失败转成新的金融 POST。
                result = await apiClient.GetTransactionAsync(
                    settings,
                    token.Token,
                    requestedSessionId,
                    timeoutCts.Token);
            }
            catch (LinklyCloudApiException ex) when (ex.IsAuthenticationFailure)
            {
                Log($"transaction recovery status auth-failure refreshing-token sessionId={requestedSessionId}");
                token = await GetTokenAsync(settings, session.StoreCode, session.DeviceCode, timeoutCts.Token);
                result = await apiClient.GetTransactionAsync(
                    settings,
                    token.Token,
                    requestedSessionId,
                    timeoutCts.Token);
            }

            if (result.Outcome != LinklyCloudTransactionOutcome.Completed ||
                IsPending(result) ||
                !MatchesRecoveryIdentity(result, amount, requestedSessionId, requestedTxnRef))
            {
                return RecoveryUnknown(requestedSessionId, requestedTxnRef, unknownMessage);
            }

            return ToAuthorizationResult(result, amount, requestedTxnRef, "P");
        }
        catch (OperationCanceledException)
        {
            return RecoveryUnknown(requestedSessionId, requestedTxnRef, unknownMessage);
        }
        catch (LinklyCloudApiException ex)
        {
            var detail = ex.IsAuthenticationFailure
                ? T("linkly.cloud.pairingInvalid", "Linkly Cloud pairing is invalid. Pair the terminal again.")
                : unknownMessage;
            return RecoveryUnknown(requestedSessionId, requestedTxnRef, detail);
        }
        catch (JsonException)
        {
            return RecoveryUnknown(
                requestedSessionId,
                requestedTxnRef,
                T("linkly.cloud.invalidResponse", "Linkly Cloud returned an invalid response."));
        }
        catch (HttpRequestException)
        {
            return RecoveryUnknown(
                requestedSessionId,
                requestedTxnRef,
                T("linkly.cloud.communicationFailed", "Linkly Cloud communication failed."));
        }
    }

    public async Task<LinklySettlementResult> SettlementAsync(
        PosSessionState session,
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new LinklySettlementResult(
                false,
                "ANZ Linkly settlement was cancelled.",
                ProviderSubmissionState: ProviderSubmissionState.NotSubmitted);
        }

        if (string.IsNullOrWhiteSpace(settings.LinklyCloudSecret))
        {
            return new LinklySettlementResult(
                false,
                T("linkly.cloud.notPaired", "Linkly Cloud terminal is not paired."),
                ProviderSubmissionState: ProviderSubmissionState.NotSubmitted);
        }

        var posVendorId = CardTerminalSettings.ResolveLinklyPosVendorId(settings.Environment, settings.LinklyPosVendorId);
        if (string.IsNullOrWhiteSpace(posVendorId))
        {
            return new LinklySettlementResult(
                false,
                T("linkly.cloud.vendorIdMissing", "Linkly POS vendor id is not configured."),
                ProviderSubmissionState: ProviderSubmissionState.NotSubmitted);
        }

        var endpointValidationMessage = ValidateEndpointSettings(settings);
        if (!string.IsNullOrWhiteSpace(endpointValidationMessage))
        {
            return new LinklySettlementResult(
                false,
                endpointValidationMessage,
                ProviderSubmissionState: ProviderSubmissionState.NotSubmitted);
        }

        // CancelAfter 不接受 TimeProvider，改为独立的业务等待计时 CTS 再与外部令牌链接，语义不变。
        using var businessWaitCts = new CancellationTokenSource(LinklyTimeoutPolicy.BusinessWait, _timeProvider);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, businessWaitCts.Token);
        var settlementRequestSent = false;
        var sessionId = Guid.NewGuid().ToString("D");
        try
        {
            Log($"settlement start environment={settings.Environment} sessionId={sessionId} store={LogValue(session.StoreCode)} device={LogValue(session.DeviceCode)}");
            var token = await GetTokenAsync(settings, session.StoreCode, session.DeviceCode, timeoutCts.Token);
            settlementRequestSent = true;
            var result = await apiClient.SendSettlementAsync(settings, token.Token, sessionId, timeoutCts.Token);
            if (result.Outcome == LinklyCloudSettlementOutcome.Unknown)
            {
                // 官方 Settlement 协议只定义同步 POST，不支持 Transaction 的 GET error recovery。
                // 不确定结果必须立即阻止重发，由收银员在终端侧人工确认。
                return SettlementUnknown(result.SessionId);
            }

            var succeeded = result.Succeeded && string.Equals(result.ResponseCode, "00", StringComparison.OrdinalIgnoreCase);
            return new LinklySettlementResult(
                succeeded,
                FormatSettlementMessage(result, succeeded),
                result.SessionId,
                NormalizeOptional(result.ResponseCode),
                NormalizeOptional(result.ResponseText),
                NormalizeOptional(result.SettlementData),
                result.ReceiptTexts,
                ProviderSubmissionState: ProviderSubmissionState.Submitted);
        }
        catch (OperationCanceledException) when (settlementRequestSent)
        {
            Log($"settlement unknown sessionId={sessionId} reason={(cancellationToken.IsCancellationRequested ? "caller-cancelled" : "timeout")}");
            return SettlementUnknown(sessionId);
        }
        catch (LinklyCloudApiException ex) when (
            settlementRequestSent &&
            ex.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError)
        {
            return new LinklySettlementResult(
                false,
                ex.IsAuthenticationFailure
                    ? T("linkly.cloud.pairingInvalid", "Linkly Cloud pairing is invalid. Pair the terminal again.")
                    : ex.Message,
                sessionId,
                ProviderSubmissionState: ProviderSubmissionState.NotSubmitted);
        }
        catch (Exception ex) when (settlementRequestSent && (ex is LinklyCloudApiException or JsonException or HttpRequestException))
        {
            Log($"settlement unknown sessionId={sessionId} error={ex.GetType().Name}");
            return SettlementUnknown(sessionId);
        }
        catch (OperationCanceledException)
        {
            return new LinklySettlementResult(
                false,
                cancellationToken.IsCancellationRequested ? "ANZ Linkly settlement was cancelled." : T("linkly.cloud.timeout", "Linkly Cloud settlement timed out."),
                sessionId,
                ProviderSubmissionState: ProviderSubmissionState.NotSubmitted);
        }
        catch (LinklyCloudApiException ex)
        {
            return new LinklySettlementResult(false, ex.IsAuthenticationFailure
                ? T("linkly.cloud.pairingInvalid", "Linkly Cloud pairing is invalid. Pair the terminal again.")
                : ex.Message,
                sessionId,
                ProviderSubmissionState: ProviderSubmissionState.NotSubmitted);
        }
        catch (JsonException)
        {
            return new LinklySettlementResult(
                false,
                T("linkly.cloud.invalidResponse", "Linkly Cloud returned an invalid response."),
                sessionId,
                ProviderSubmissionState: ProviderSubmissionState.NotSubmitted);
        }
        catch (HttpRequestException)
        {
            return new LinklySettlementResult(
                false,
                T("linkly.cloud.communicationFailed", "Linkly Cloud communication failed."),
                sessionId,
                ProviderSubmissionState: ProviderSubmissionState.NotSubmitted);
        }
    }

    private async Task<PaymentAuthorizationResult> RunTransactionAsync(
        string txnType,
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? refundReference,
        CancellationToken cancellationToken)
    {
        if (amount <= 0m)
        {
            Log($"transaction blocked txnType={txnType} store={LogValue(session.StoreCode)} device={LogValue(session.DeviceCode)} reason=invalid-amount");
            return new PaymentAuthorizationResult(false, null, T("linkly.cloud.amountMustBePositive", "Card amount must be greater than zero."));
        }

        if (string.IsNullOrWhiteSpace(settings.LinklyCloudSecret))
        {
            Log($"transaction blocked txnType={txnType} store={LogValue(session.StoreCode)} device={LogValue(session.DeviceCode)} reason=missing-secret");
            return FallbackAllowed("linkly.cloud.notPaired", T("linkly.cloud.notPaired", "Linkly Cloud terminal is not paired."));
        }

        var posVendorId = CardTerminalSettings.ResolveLinklyPosVendorId(
            settings.Environment,
            settings.LinklyPosVendorId);
        if (string.IsNullOrWhiteSpace(posVendorId))
        {
            Log($"transaction blocked txnType={txnType} store={LogValue(session.StoreCode)} device={LogValue(session.DeviceCode)} reason=missing-pos-vendor-id");
            return FallbackAllowed("linkly.cloud.vendorIdMissing", T("linkly.cloud.vendorIdMissing", "Linkly POS vendor id is not configured."));
        }

        var endpointValidationMessage = ValidateEndpointSettings(settings);
        if (!string.IsNullOrWhiteSpace(endpointValidationMessage))
        {
            Log($"transaction blocked txnType={txnType} environment={settings.Environment} store={LogValue(session.StoreCode)} device={LogValue(session.DeviceCode)} reason=invalid-endpoint");
            return FallbackAllowed("linkly.cloud.configIncomplete", endpointValidationMessage);
        }

        // CancelAfter 不接受 TimeProvider，改为独立的业务等待计时 CTS 再与外部令牌链接，语义不变。
        using var businessWaitCts = new CancellationTokenSource(LinklyTimeoutPolicy.BusinessWait, _timeProvider);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, businessWaitCts.Token);
        var keepDialogOpen = false;
        var transactionSubmitted = false;
        var attemptContext = linklyPaymentAttemptContextAccessor?.Current;
        var txnRef = string.Empty;
        var sessionId = string.Empty;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Log($"transaction start environment={settings.Environment} txnType={txnType} store={LogValue(session.StoreCode)} device={LogValue(session.DeviceCode)} amountMinor={ToMinorUnits(amount)} hasRefundReference={!string.IsNullOrWhiteSpace(refundReference)}");
            txnRef = ResolveTxnRef(session, txnType, attemptContext);
            sessionId = Guid.NewGuid().ToString("D");
            var token = await GetTokenAsync(settings, session.StoreCode, session.DeviceCode, timeoutCts.Token);
            var request = new LinklyCloudTransactionRequest(
                txnType,
                ToMinorUnits(amount),
                txnRef,
                string.IsNullOrWhiteSpace(refundReference)
                    ? null
                    : new Dictionary<string, string>
                    {
                        ["RFN"] = refundReference,
                        ["OPR"] = $"{session.CashierId}|{session.CashierName}",
                        ["AMT"] = ToMinorUnits(amount).ToString("D9", CultureInfo.InvariantCulture),
                        ["PCM"] = "0000"
                    });

            timeoutCts.Token.ThrowIfCancellationRequested();
            await BindAttemptSessionAsync(attemptContext, sessionId, txnRef);
            // 中文注释：身份落库不等于金融提交；落库期间取消也必须停在 POST 之前。
            timeoutCts.Token.ThrowIfCancellationRequested();
            transactionSubmitted = true;
            var result = await SendTransactionWithDialogAsync(settings, token, request, sessionId, timeoutCts.Token);
            if (!MatchesSubmittedResponseIdentity(result, amount, sessionId, txnRef))
                return UnknownResponseIdentity(sessionId, txnRef, txnType);
            if (IsPending(result))
            {
                Log($"transaction pending sessionId={result.SessionId} txnType={txnType} txnRef={txnRef}");
                var polled = await PollTransactionAsync(settings, session, token, sessionId, txnRef, amount, timeoutCts.Token);
                result = polled.Result;
                token = polled.Token;
                if (!MatchesSubmittedResponseIdentity(result, amount, sessionId, txnRef))
                    return UnknownResponseIdentity(sessionId, txnRef, txnType);
            }

            if (result.Outcome == LinklyCloudTransactionOutcome.NotSubmitted)
            {
                // 中文注释：已核验原请求明确未提交，重试前取消仍应释放未扣款订单。
                transactionSubmitted = false;
                timeoutCts.Token.ThrowIfCancellationRequested();
                Log($"transaction not-submitted retrying txnType={txnType} previousSessionId={result.SessionId} txnRef={txnRef}");
                var retrySessionId = Guid.NewGuid().ToString("D");
                await BindAttemptSessionAsync(attemptContext, retrySessionId, txnRef);
                sessionId = retrySessionId;
                timeoutCts.Token.ThrowIfCancellationRequested();
                transactionSubmitted = true;
                result = await SendTransactionWithDialogAsync(settings, token, request, retrySessionId, timeoutCts.Token);
                if (!MatchesSubmittedResponseIdentity(result, amount, sessionId, txnRef))
                    return UnknownResponseIdentity(sessionId, txnRef, txnType);
                if (IsPending(result))
                {
                    Log($"transaction retry pending sessionId={result.SessionId} txnType={txnType} txnRef={txnRef}");
                    result = (await PollTransactionAsync(settings, session, token, sessionId, txnRef, amount, timeoutCts.Token)).Result;
                    if (!MatchesSubmittedResponseIdentity(result, amount, sessionId, txnRef))
                        return UnknownResponseIdentity(sessionId, txnRef, txnType);
                }
            }

            if (result.Outcome == LinklyCloudTransactionOutcome.NotSubmitted)
            {
                Log($"transaction not-submitted final txnType={txnType} txnRef={txnRef}");
                var message = T("linkly.cloud.notSubmitted", "Linkly Cloud transaction was not submitted. Retry the payment.");
                await PresentDirectStatusAsync(
                    result.SessionId ?? txnRef,
                    result.Outcome.ToString(),
                    message,
                    receiptText: null,
                    responseText: message,
                    isInteractive: false,
                    isFinal: true,
                    CancellationToken.None);
                keepDialogOpen = true;
                return new PaymentAuthorizationResult(false, null, message);
            }

            Log($"transaction completed txnType={txnType} sessionId={result.SessionId} txnRef={LogValue(result.TxnRef ?? txnRef)} approved={result.Succeeded} responseCode={LogValue(result.ResponseCode)} outcome={result.Outcome}");
            await PresentDirectStatusAsync(
                result.SessionId ?? txnRef,
                result.Outcome.ToString(),
                null,
                receiptText: null,
                responseText: FormatDirectStatusMessage(result),
                isInteractive: false,
                isFinal: true,
                CancellationToken.None,
                responseCode: result.ResponseCode);
            var authorizationResult = ToAuthorizationResult(result, amount, txnRef, txnType);
            keepDialogOpen = !authorizationResult.Approved && !IsCancelledResult(result);
            return authorizationResult;
        }
        catch (OperationCanceledException ex) when (!transactionSubmitted && cancellationToken.IsCancellationRequested)
        {
            throw new CardTerminalNotSubmittedException(ex, cancellationToken);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Log($"transaction timed-out txnType={txnType} store={LogValue(session.StoreCode)} device={LogValue(session.DeviceCode)}");
            var message = T("linkly.cloud.timeout", "Linkly Cloud transaction timed out.");
            await PresentDirectStatusAsync(
                "direct-sync-timeout",
                "Timeout",
                message,
                receiptText: null,
                responseText: message,
                isInteractive: false,
                isFinal: true,
                CancellationToken.None);
            keepDialogOpen = true;
            return transactionSubmitted
                ? ResultUnknown("linkly.cloud.resultUnknown", BuildResultUnknownMessage(message), sessionId, txnRef, txnType)
                : FallbackAllowed("linkly.cloud.timeout", message);
        }
        catch (OperationCanceledException) when (transactionSubmitted && cancellationToken.IsCancellationRequested)
        {
            var message = T(
                "linkly.cloud.resultUnknown",
                "Linkly Cloud transaction result is unknown. Confirm the Linkly transaction status before retrying.");
            Log($"transaction caller-cancelled-after-submit txnType={txnType} sessionId={sessionId} txnRef={LogValue(txnRef)}");
            await PresentDirectStatusAsync(
                sessionId,
                "Unknown",
                message,
                receiptText: null,
                responseText: message,
                isInteractive: false,
                isFinal: true,
                CancellationToken.None);
            keepDialogOpen = true;
            return ResultUnknown("linkly.cloud.resultUnknown", message, sessionId, txnRef, txnType);
        }
        catch (LinklyCloudApiException ex)
        {
            Log($"transaction failed txnType={txnType} store={LogValue(session.StoreCode)} device={LogValue(session.DeviceCode)} authFailure={ex.IsAuthenticationFailure} error={ex.GetType().Name}");
            var message = ex.IsAuthenticationFailure
                ? T("linkly.cloud.pairingInvalid", "Linkly Cloud pairing is invalid. Pair the terminal again.")
                : ex.Message;
            await PresentDirectStatusAsync(
                "direct-sync-error",
                "Failed",
                message,
                receiptText: null,
                responseText: message,
                isInteractive: false,
                isFinal: true,
                CancellationToken.None);
            keepDialogOpen = true;
            return transactionSubmitted
                ? ResultUnknown("linkly.cloud.resultUnknown", BuildResultUnknownMessage(message), sessionId, txnRef, txnType)
                : FallbackAllowed("linkly.cloud.communicationFailed", message);
        }
        catch (JsonException)
        {
            Log($"transaction failed txnType={txnType} store={LogValue(session.StoreCode)} device={LogValue(session.DeviceCode)} reason=invalid-json");
            var message = T("linkly.cloud.invalidResponse", "Linkly Cloud returned an invalid response.");
            await PresentDirectStatusAsync(
                "direct-sync-error",
                "Failed",
                message,
                receiptText: null,
                responseText: message,
                isInteractive: false,
                isFinal: true,
                CancellationToken.None);
            keepDialogOpen = true;
            return transactionSubmitted
                ? ResultUnknown("linkly.cloud.resultUnknown", BuildResultUnknownMessage(message), sessionId, txnRef, txnType)
                : FallbackAllowed("linkly.cloud.invalidResponse", message);
        }
        catch (HttpRequestException)
        {
            Log($"transaction failed txnType={txnType} store={LogValue(session.StoreCode)} device={LogValue(session.DeviceCode)} reason=http-request-exception");
            var message = T("linkly.cloud.communicationFailed", "Linkly Cloud communication failed.");
            await PresentDirectStatusAsync(
                "direct-sync-error",
                "Failed",
                message,
                receiptText: null,
                responseText: message,
                isInteractive: false,
                isFinal: true,
                CancellationToken.None);
            keepDialogOpen = true;
            return transactionSubmitted
                ? ResultUnknown("linkly.cloud.resultUnknown", BuildResultUnknownMessage(message), sessionId, txnRef, txnType)
                : FallbackAllowed("linkly.cloud.communicationFailed", message);
        }
        catch (Exception ex) when (transactionSubmitted && ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 中文注释：提交后未分类异常同样不能证明未扣款，必须携带原身份进入恢复。
            Log($"transaction unknown txnType={txnType} sessionId={sessionId} error={ex.GetType().Name}");
            return UnknownResponseIdentity(sessionId, txnRef, txnType);
        }
        finally
        {
            if (dialogService is not null && !keepDialogOpen)
            {
                // 成功交易自动关闭页面弹窗；失败最终状态由收银员手动关闭。
                await dialogService.CloseAsync(CancellationToken.None);
            }
        }
    }

    private async Task<LinklyTerminalDialogAction?> PresentDirectStatusAsync(
        string sessionId,
        string status,
        string? message,
        string? receiptText,
        string? responseText,
        bool isInteractive,
        bool isFinal,
        CancellationToken cancellationToken,
        string? responseCode = null)
    {
        if (dialogService is null)
        {
            return null;
        }

        return await dialogService.UpdateAsync(
            new LinklyTerminalDialogState(
                sessionId,
                status,
                message,
                receiptText,
                responseText,
                RecoveryCount: 0,
                LastHttpStatus: null,
                Message: isFinal ? null : T("linkly.cloud.directStatusHelp", "You can cancel on the terminal or send a POS cancel request. The final result is determined by Linkly."),
                Mode: LinklyTerminalDialogMode.CloudDirectStatus,
                IsInteractive: isInteractive,
                IsFinal: isFinal,
                DisplayButtons: [],
                ResponseCode: responseCode),
            cancellationToken);
    }

    private async Task<LinklyCloudTransactionResult> SendTransactionWithDialogAsync(
        CardTerminalSettings settings,
        LinklyCloudToken token,
        LinklyCloudTransactionRequest request,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var dialogMessage = T("linkly.cloud.directPendingMessage", "Waiting for the card terminal result. Complete the operation on the terminal.");
        var transactionTask = apiClient.SendTransactionAsync(
            settings,
            token.Token,
            request,
            sessionId,
            cancellationToken);

        while (true)
        {
            var action = await PresentDirectStatusAsync(
                sessionId,
                "Pending",
                dialogMessage,
                receiptText: null,
                responseText: null,
                isInteractive: true,
                isFinal: false,
                cancellationToken);
            if (action is not null)
            {
                // 同步交易 HTTP 请求未返回时也允许 POS 发送 Linkly 官方 OK/CANCEL sendkey。
                dialogMessage = await SendDirectDialogActionAsync(settings, token, sessionId, action, cancellationToken);
            }

            if (transactionTask.IsCompleted)
            {
                return await transactionTask;
            }

            var delay = _pollInterval > TimeSpan.Zero ? _pollInterval : TimeSpan.FromMilliseconds(50);
            // 关键：令牌取消后 Task.Delay(delay, cancellationToken) 与 Task.WhenAny 都会同步返回；未注入对话框服务时
            // PresentDirectStatusAsync 也同步返回 null，循环便成了不让出线程的空转，还会把排在同一同步上下文里的
            // POST 续体饿死而活锁。取消后改用不可取消的短等待让出线程；有对话框时下一轮仍由它立即抛出取消。
            var wait = cancellationToken.IsCancellationRequested
                ? Task.Delay(CancelledResultWaitInterval)
                : Task.Delay(delay, cancellationToken);
            var completed = await Task.WhenAny(transactionTask, wait);
            if (completed == transactionTask)
            {
                return await transactionTask;
            }
        }
    }

    private async Task<PolledLinklyCloudTransaction> PollTransactionAsync(
        CardTerminalSettings settings,
        PosSessionState session,
        LinklyCloudToken token,
        string sessionId,
        string txnRef,
        decimal amount,
        CancellationToken cancellationToken)
    {
        var dialogMessage = T("linkly.cloud.directPendingMessage", "Waiting for the card terminal result. Complete the operation on the terminal.");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = await PresentDirectStatusAsync(
                sessionId,
                "Pending",
                dialogMessage,
                receiptText: null,
                responseText: null,
                isInteractive: true,
                isFinal: false,
                cancellationToken);
            if (action is not null)
            {
                dialogMessage = await SendDirectDialogActionAsync(settings, token, sessionId, action, cancellationToken);
            }

            if (_pollInterval > TimeSpan.Zero)
            {
                await Task.Delay(_pollInterval, cancellationToken);
            }

            LinklyCloudTransactionResult result;
            try
            {
                result = await apiClient.GetTransactionAsync(settings, token.Token, sessionId, cancellationToken);
            }
            catch (LinklyCloudApiException ex) when (ex.IsAuthenticationFailure)
            {
                Log($"transaction status auth-failure refreshing-token sessionId={sessionId} store={LogValue(session.StoreCode)} device={LogValue(session.DeviceCode)}");
                token = await GetTokenAsync(settings, session.StoreCode, session.DeviceCode, cancellationToken);
                continue;
            }

            if (!MatchesSubmittedResponseIdentity(result, amount, sessionId, txnRef) || !IsPending(result))
            {
                Log($"transaction status resolved sessionId={sessionId} outcome={result.Outcome} success={result.Succeeded} responseCode={LogValue(result.ResponseCode)}");
                return new PolledLinklyCloudTransaction(result, token);
            }

            Log($"transaction status still-pending sessionId={sessionId}");
        }
    }

    private async Task<string> SendDirectDialogActionAsync(
        CardTerminalSettings settings,
        LinklyCloudToken token,
        string sessionId,
        LinklyTerminalDialogAction action,
        CancellationToken cancellationToken)
    {
        try
        {
            // Linkly Cloud 同步直连取消不是最终状态，只是按官方 sendkey 发送 OK/CANCEL 键，随后仍等待交易结果。
            await apiClient.SendKeyAsync(
                settings,
                token.Token,
                sessionId,
                action.Key,
                action.Data,
                cancellationToken);
            return T("linkly.cloud.directCancelRequested", "Cancel request sent to the card terminal. Waiting for the final result.");
        }
        catch (Exception ex) when (ex is HttpRequestException or LinklyCloudApiException)
        {
            Log($"transaction direct sendkey failed sessionId={sessionId} error={ex.GetType().Name}");
            return IsCancelDialogAction(action)
                // 虚拟刷卡取消以 Linkly 最终状态为准；sendkey 短暂拒绝时继续等待，避免把已取消交易误报为取消失败。
                ? T("linkly.cloud.directPendingMessage", "Waiting for the card terminal result. Complete the operation on the terminal.")
                : T("linkly.cloud.directCancelFailed", "Cancel request could not be sent. Try again or use the terminal.");
        }
    }

    private static bool IsCancelDialogAction(LinklyTerminalDialogAction action)
    {
        return string.Equals(
            LinklyTerminalDialogKeys.Normalize(action.Key),
            LinklyTerminalDialogKeys.OkCancel,
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task<LinklyCloudToken> GetTokenAsync(
        CardTerminalSettings settings,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        var posId = await secretStore.GetOrCreateLinklyCloudPosIdAsync(
            settings.Environment,
            storeCode,
            deviceCode,
            cancellationToken);
        Log($"token resolve start environment={settings.Environment} store={LogValue(storeCode)} device={LogValue(deviceCode)} posId={ShortId(posId)}");
        return await apiClient.GetTokenAsync(settings, posId, cancellationToken);
    }

    private static string? ValidateEndpointSettings(CardTerminalSettings settings)
    {
        return CardTerminalSettings.ValidateLinklyCloudAuthBaseUrl(
                settings.Environment,
                settings.LinklyCloudAuthBaseUrl)
            ?? CardTerminalSettings.ValidateLinklyCloudRestBaseUrl(
                settings.Environment,
                settings.LinklyCloudRestBaseUrl);
    }

    private PaymentAuthorizationResult ToAuthorizationResult(
        LinklyCloudTransactionResult response,
        decimal requestedAmount,
        string requestedTxnRef,
        string txnType)
    {
        var amount = response.Amount;
        var returnedTxnRef = NormalizeOptional(response.TxnRef);
        // 中文注释：Cloud 直连提交前已绑定本地 TxnRef；成功结果仍须回传终端引用，不能用缺失引用放行金融结果。
        if (response.Succeeded && (amount is null || string.IsNullOrWhiteSpace(returnedTxnRef)))
        {
            return new PaymentAuthorizationResult(
                false,
                null,
                "ANZ Linkly Cloud result could not be verified.",
                TxnType: txnType,
                SessionId: response.SessionId,
                TxnRef: returnedTxnRef ?? requestedTxnRef,
                ResultUnknown: true);
        }

        var txnRef = returnedTxnRef ?? requestedTxnRef;
        var transaction = new CardTransactionDto(
            ProcessorName,
            txnRef,
            NormalizeOptional(response.AuthCode),
            NormalizeOptional(response.CardType),
            int.TryParse(response.CardName, out var cardName) && cardName > 0 ? cardName : null,
            MaskCardNumber(response.Pan),
            NormalizeOptional(response.Caid),
            NormalizeOptional(response.ResponseCode),
            NormalizeOptional(response.ResponseText),
            NormalizeOptional(response.Stan),
            null,
            amount is decimal value ? decimal.Round(value, 2, MidpointRounding.AwayFromZero) : 0m,
            null,
            NormalizeOptional(response.RefundReference));
        var approved = response.Succeeded;
        var reference = string.IsNullOrWhiteSpace(response.RefundReference)
            ? $"ANZCLOUD:{txnRef}"
            : $"ANZCLOUD:{txnRef}:{response.RefundReference.Trim()}";

        return approved
            ? new PaymentAuthorizationResult(
                true,
                reference,
                "ANZ Linkly Cloud",
                amount,
                [transaction],
                ProcessorName,
                ConnectionMode: LinklyConnectionMode.CloudDirectSync.ToString(),
                TxnType: txnType,
                SessionId: response.SessionId,
                TxnRef: txnRef,
                ResponseCode: transaction.ResponseCode,
                ResponseText: transaction.ResponseText)
            : new PaymentAuthorizationResult(
                false,
                reference,
                FormatResponseMessage(response.ResponseText, response.ResponseCode),
                amount,
                [transaction],
                ProcessorName,
                ConnectionMode: LinklyConnectionMode.CloudDirectSync.ToString(),
                TxnType: txnType,
                SessionId: response.SessionId,
                TxnRef: txnRef,
                ResponseCode: transaction.ResponseCode,
                ResponseText: transaction.ResponseText);
    }

    private PaymentAuthorizationResult RecoveryUnknown(
        string? sessionId,
        string? txnRef,
        string detail)
    {
        var guidance = T(
            "linkly.cloud.resultUnknown",
            "Linkly Cloud transaction result is unknown. Confirm the Linkly transaction status before retrying.");
        return ResultUnknown(
            "linkly.cloud.resultUnknown",
            string.Equals(detail, guidance, StringComparison.Ordinal)
                ? detail
                : BuildResultUnknownMessage(detail),
            sessionId,
            txnRef,
            "P");
    }

    private static bool MatchesRecoveryIdentity(
        LinklyCloudTransactionResult result,
        decimal requestedAmount,
        string requestedSessionId,
        string requestedTxnRef)
    {
        var responseSessionId = NormalizeOptional(result.SessionId);
        var responseTxnRef = NormalizeOptional(result.TxnRef);
        return string.Equals(responseSessionId, requestedSessionId, StringComparison.Ordinal) &&
            string.Equals(responseTxnRef, requestedTxnRef, StringComparison.Ordinal) &&
            result.Amount is decimal responseAmount &&
            decimal.Round(responseAmount, 2, MidpointRounding.AwayFromZero) ==
            decimal.Round(requestedAmount, 2, MidpointRounding.AwayFromZero);
    }

    private PaymentAuthorizationResult UnknownResponseIdentity(string sessionId, string txnRef, string txnType) =>
        ResultUnknown("linkly.cloud.resultUnknown",
            T("linkly.cloud.resultUnknown", "Linkly Cloud transaction result is unknown. Confirm the Linkly transaction status before retrying."),
            sessionId, txnRef, txnType);

    private static bool MatchesSubmittedResponseIdentity(
        LinklyCloudTransactionResult result, decimal amount, string sessionId, string txnRef)
    {
        // 中文注释：先核对冻结身份，再决定轮询、重试或接受金融结果；外来会话不得触发任何下一步交易。
        if (!string.Equals(NormalizeOptional(result.SessionId), sessionId, StringComparison.Ordinal))
            return false;
        if (!IsPending(result) && result.Outcome == LinklyCloudTransactionOutcome.Completed && result.Succeeded)
            return MatchesRecoveryIdentity(result, amount, sessionId, txnRef);
        // 未提交、处理中及终端明确取消/拒绝可能没有引用；一旦提供，也必须属于本次请求。
        return (NormalizeOptional(result.TxnRef) is not { } responseTxnRef ||
                string.Equals(responseTxnRef, txnRef, StringComparison.Ordinal)) &&
            (result.Amount is not { } responseAmount || responseAmount == amount);
    }

    private static async Task BindAttemptSessionAsync(
        LinklyPaymentAttemptContext? attemptContext,
        string sessionId,
        string txnRef)
    {
        if (attemptContext is null)
        {
            return;
        }

        // 中文注释：提交证据必须脱离调用方取消；调用方取消只能改变等待结果，不能阻止身份落库。
        await attemptContext.BindSessionAsync(
            sessionId,
            txnRef,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
    }

    private static string ResolveTxnRef(
        PosSessionState session,
        string txnType,
        LinklyPaymentAttemptContext? attemptContext)
    {
        if (attemptContext is not null &&
            LinklyLocalTxnRef.TryNormalizeHistoricalReference(attemptContext.TxnRef, out var persistedTxnRef))
        {
            return persistedTxnRef;
        }

        var transactionType = string.Equals(txnType, "R", StringComparison.OrdinalIgnoreCase) ? 'R' : 'P';
        return attemptContext is not null
            ? BuildTxnRef(transactionType, attemptContext.AttemptGuid.ToString("D"))
            : BuildTxnRef(session, transactionType);
    }

    private static bool IsPending(LinklyCloudTransactionResult result)
    {
        return result.Outcome == LinklyCloudTransactionOutcome.Pending ||
            (result.Outcome == LinklyCloudTransactionOutcome.Completed &&
            string.IsNullOrWhiteSpace(result.TxnRef) &&
            string.IsNullOrWhiteSpace(result.ResponseCode) &&
            string.IsNullOrWhiteSpace(result.ResponseText));
    }

    private static bool IsCancelledResult(LinklyCloudTransactionResult result)
    {
        var responseCode = NormalizeOptional(result.ResponseCode);
        if (string.Equals(responseCode, "C0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(responseCode, "CN", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var responseText = NormalizeOptional(result.ResponseText);
        return responseText is not null &&
            (responseText.Contains("CANCELLED", StringComparison.OrdinalIgnoreCase) ||
                responseText.Contains("CANCELED", StringComparison.OrdinalIgnoreCase) ||
                responseText.Contains("CANCEL", StringComparison.OrdinalIgnoreCase));
    }

    private static long ToMinorUnits(decimal amount)
    {
        return decimal.ToInt64(decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));
    }

    private static string BuildTxnRef(PosSessionState session)
    {
        var device = new string(session.DeviceCode.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(device))
        {
            device = "POS";
        }

        return BuildTxnRef('P', $"{device}|{session.StoreCode}|{Guid.NewGuid():D}");
    }

    private static string BuildTxnRef(PosSessionState session, char transactionType)
    {
        var device = new string(session.DeviceCode.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(device))
        {
            device = "POS";
        }

        return BuildTxnRef(transactionType, $"{device}|{session.StoreCode}|{Guid.NewGuid():D}");
    }

    private static string BuildTxnRef(char transactionType, string stableIdentity)
    {
        // 中文注释：Linkly TxnRef 必须同时满足 16 字符上限和同一 PIN pad 唯一；哈希编码避免长设备码截断时间造成碰撞。
        return LinklyLocalTxnRef.Create(transactionType, stableIdentity);
    }

    private static string? TryParseRefundReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var parts = reference.Trim().Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 &&
            string.Equals(parts[0], "ANZCLOUD", StringComparison.OrdinalIgnoreCase)
                ? parts[2]
                : null;
    }

    private static string? MaskCardNumber(string? pan)
    {
        return LinklyReceiptTextSanitizer.SanitizeCardNumber(pan);
    }

    private string FormatResponseMessage(string? responseText, string? responseCode)
    {
        var text = NormalizeOptional(responseText);
        var code = NormalizeOptional(responseCode);
        if (text is null && code is null)
        {
            return T("linkly.cloud.declined", "ANZ Linkly Cloud transaction was declined.");
        }

        return code is null ? text! : $"{text ?? T("linkly.cloud.declined", "ANZ Linkly Cloud transaction was declined.")} ({code})";
    }

    private string FormatDirectStatusMessage(LinklyCloudTransactionResult result)
    {
        var text = NormalizeOptional(result.ResponseText);
        var code = NormalizeOptional(result.ResponseCode);
        // 中文注释：Linkly 的 Success 字段才是金融结果；ResponseCode 仅用于展示，不能把批准收窄为 code 00。
        var approved = result.Succeeded;
        var fallback = approved
            ? T("linkly.cloud.approved", "ANZ Linkly Cloud transaction approved.")
            : T("linkly.cloud.declined", "ANZ Linkly Cloud transaction was declined.");

        return code is null ? text ?? fallback : $"{text ?? fallback} ({code})";
    }

    private string FormatSettlementMessage(LinklyCloudSettlementResult result, bool succeeded)
    {
        var text = NormalizeOptional(result.ResponseText);
        var code = NormalizeOptional(result.ResponseCode);
        var fallback = succeeded
            ? "ANZ Linkly settlement completed."
            : "ANZ Linkly settlement failed.";
        return code is null ? text ?? fallback : $"{text ?? fallback} ({code})";
    }

    private LinklySettlementResult SettlementUnknown(string sessionId)
    {
        return new LinklySettlementResult(
            false,
            "Linkly Cloud settlement result is unknown. Confirm the Linkly settlement status before trying again.",
            sessionId,
            ResultUnknown: true,
            ProviderSubmissionState: ProviderSubmissionState.Unknown);
    }

    private string T(string key, string fallback)
    {
        var value = localization?.T(key);
        return string.IsNullOrWhiteSpace(value) || value == $"[[{key}]]" ? fallback : value;
    }

    private PaymentAuthorizationResult FallbackAllowed(string statusKey, string message)
    {
        return new PaymentAuthorizationResult(false, null, message, StatusKey: statusKey, FallbackAllowed: true);
    }

    private PaymentAuthorizationResult ResultUnknown(
        string statusKey,
        string message,
        string? sessionId = null,
        string? txnRef = null,
        string? txnType = null)
    {
        return new PaymentAuthorizationResult(
            false,
            null,
            message,
            StatusKey: statusKey,
            TxnType: txnType,
            SessionId: sessionId,
            TxnRef: txnRef,
            ResultUnknown: true);
    }

    private string BuildResultUnknownMessage(string detail)
    {
        var guidance = T(
            "linkly.cloud.resultUnknown",
            "Linkly Cloud transaction result is unknown. Confirm the Linkly transaction status before retrying.");
        return string.IsNullOrWhiteSpace(detail)
            ? guidance
            : $"{detail} {guidance}";
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string Limit(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static void Log(string message)
    {
        LinklyJsonLog.WriteMessage("LinklyCloud", "cloud-terminal", message);
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value.Trim();
    }

    private static string ShortId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<null>";
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 8 ? trimmed : $"{trimmed[..8]}...";
    }

    private sealed record PolledLinklyCloudTransaction(
        LinklyCloudTransactionResult Result,
        LinklyCloudToken Token);

}

public sealed class ConfiguredLinklyTerminalClient(
    LinklyTerminalClient localClient,
    ILinklyCloudTerminalClient cloudClient,
    ILinklyBackendTerminalClient? backendClient = null) : ILinklyTerminalClient
{
    public Task<LinklyConnectionTestResult> TestConnectionAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        return localClient.TestConnectionAsync(host, port, timeout, cancellationToken);
    }

    public Task<LinklyLogonResult> LogonAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        return localClient.LogonAsync(host, port, timeout, cancellationToken);
    }

    public Task<PaymentAuthorizationResult> PurchaseAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default)
    {
        return RunWithPriorityAsync(
            settings,
            (mode, modeSettings) => PurchaseOneModeAsync(mode, amount, session, modeSettings, cancellationToken));
    }

    public Task<PaymentAuthorizationResult> PurchaseWithReferenceAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string txnRef,
        CancellationToken cancellationToken = default)
    {
        return RunWithPriorityAsync(
            settings,
            (mode, modeSettings) => PurchaseOneModeAsync(mode, amount, session, modeSettings, cancellationToken, txnRef));
    }

    public Task<PaymentAuthorizationResult> RecoverLastTransactionAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string txnRef,
        CancellationToken cancellationToken = default)
    {
        return localClient.RecoverLastTransactionAsync(amount, session, settings, txnRef, cancellationToken);
    }

    public Task<PaymentAuthorizationResult> RefundAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        CancellationToken cancellationToken = default)
    {
        return RunWithPriorityAsync(
            settings,
            (mode, modeSettings) => RefundOneModeAsync(mode, amount, session, modeSettings, originalReference, cancellationToken));
    }

    public Task<PaymentAuthorizationResult> RefundWithReferenceAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        string refundTxnRef,
        CancellationToken cancellationToken = default)
    {
        return RunWithPriorityAsync(
            settings,
            (mode, modeSettings) => RefundOneModeAsync(mode, amount, session, modeSettings, originalReference, cancellationToken, refundTxnRef));
    }

    public Task<PaymentAuthorizationResult> VoidAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        CancellationToken cancellationToken = default)
    {
        return localClient.VoidAsync(amount, session, settings, originalReference, cancellationToken);
    }

    public Task<LinklySettlementResult> SettlementAsync(
        PosSessionState session,
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default)
    {
        var mode = CardTerminalSettings.NormalizeLinklyConnectionMode(settings.LinklyConnectionMode);
        var modeSettings = settings with { LinklyConnectionMode = mode };
        return mode switch
        {
            LinklyConnectionMode.CloudDirectSync => cloudClient.SettlementAsync(session, modeSettings, cancellationToken),
            LinklyConnectionMode.CloudBackendAsync => backendClient is null
                ? Task.FromResult(new LinklySettlementResult(
                    false,
                    "ANZ Linkly backend terminal adapter is unavailable.",
                    ProviderSubmissionState: ProviderSubmissionState.NotSubmitted))
                : backendClient.SettlementAsync(session, modeSettings, cancellationToken),
            _ => localClient.SettlementAsync(session, modeSettings, cancellationToken)
        };
    }

    private Task<PaymentAuthorizationResult> PurchaseOneModeAsync(
        LinklyConnectionMode mode,
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        CancellationToken cancellationToken,
        string? txnRef = null)
    {
        // 运行时路由集中在终端适配层，付款流程只依赖统一的 Linkly client。
        return mode switch
        {
            LinklyConnectionMode.CloudDirectSync => cloudClient.PurchaseAsync(amount, session, settings, cancellationToken),
            LinklyConnectionMode.CloudBackendAsync => backendClient is null
                ? Task.FromResult(BackendUnavailable())
                : backendClient.PurchaseAsync(amount, session, settings, cancellationToken),
            _ => string.IsNullOrWhiteSpace(txnRef)
                ? localClient.PurchaseAsync(amount, session, settings, cancellationToken)
                : localClient.PurchaseWithReferenceAsync(amount, session, settings, txnRef, cancellationToken)
        };
    }

    private Task<PaymentAuthorizationResult> RefundOneModeAsync(
        LinklyConnectionMode mode,
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        CancellationToken cancellationToken,
        string? refundTxnRef = null)
    {
        // 退款沿用同一分派规则，避免上层 UI 直接理解 Linkly 模式。
        return mode switch
        {
            LinklyConnectionMode.CloudDirectSync => cloudClient.RefundAsync(amount, session, settings, originalReference, cancellationToken),
            LinklyConnectionMode.CloudBackendAsync => backendClient is null
                ? Task.FromResult(BackendUnavailable())
                : backendClient.RefundAsync(amount, session, settings, originalReference, cancellationToken),
            _ => string.IsNullOrWhiteSpace(refundTxnRef)
                ? localClient.RefundAsync(amount, session, settings, originalReference, cancellationToken)
                : localClient.RefundWithReferenceAsync(amount, session, settings, originalReference, refundTxnRef, cancellationToken)
        };
    }

    private async Task<PaymentAuthorizationResult> RunWithPriorityAsync(
        CardTerminalSettings settings,
        Func<LinklyConnectionMode, CardTerminalSettings, Task<PaymentAuthorizationResult>> runModeAsync)
    {
        var mode = CardTerminalSettings.NormalizeLinklyConnectionMode(settings.LinklyConnectionMode);
        var formattedMode = CardTerminalSettings.FormatLinklyConnectionMode(mode);
        // 关键逻辑：每笔交易只执行配置的连接模式，失败后不再切换到其他 Linkly 链路。
        var result = await runModeAsync(mode, settings with { LinklyConnectionMode = mode });
        return result with
        {
            RequestedConnectionMode = formattedMode,
            ActualConnectionMode = formattedMode,
            FallbackAttemptedModes = [formattedMode],
            FallbackSucceeded = false,
            FallbackAllowed = false
        };
    }

    private static PaymentAuthorizationResult BackendUnavailable()
    {
        return new PaymentAuthorizationResult(
            false,
            null,
            "ANZ Linkly backend terminal adapter is unavailable.",
            StatusKey: "linkly.backend.configIncomplete",
            FallbackAllowed: true);
    }
}
