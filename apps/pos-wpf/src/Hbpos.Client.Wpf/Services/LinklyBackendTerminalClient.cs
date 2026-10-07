using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Linkly;
using static Hbpos.Contracts.Linkly.LinklyCloudBackendStatusConstants;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Services;

public interface ILinklyBackendTerminalClient
{
    Task<LinklyCloudTerminalListResponse> GetTerminalsAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new LinklyCloudTerminalListResponse(
            environment.ToString(),
            SelectedTerminalId: null,
            SelectionRevision: null,
            Terminals: []));

    Task<LinklyCloudTerminalSelectionResponse> SelectTerminalAsync(
        CardTerminalEnvironment environment,
        Guid terminalId,
        long? expectedRevision,
        CancellationToken cancellationToken = default) =>
        Task.FromException<LinklyCloudTerminalSelectionResponse>(
            new NotSupportedException("Linkly Cloud terminal selection is not supported by this client."));

    Task<LinklyCloudTerminalPairResponse> PairTerminalAsync(
        CardTerminalEnvironment environment,
        Guid terminalId,
        string pairCode,
        CancellationToken cancellationToken = default) =>
        Task.FromException<LinklyCloudTerminalPairResponse>(
            new NotSupportedException("Linkly Cloud terminal pairing is not supported by this client."));

    Task<LinklyCloudTerminalConnectionTestResponse> TestTerminalConnectionAsync(
        Guid terminalId,
        LinklyCloudTerminalConnectionTestRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<LinklyCloudTerminalConnectionTestResponse>(
            new NotSupportedException("Linkly Cloud terminal connection testing is not supported by this client."));

    Task<LinklyCloudTerminalListResponse> AssignTerminalAsync(
        Guid terminalId,
        LinklyCloudTerminalAssignmentRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<LinklyCloudTerminalListResponse>(
            new NotSupportedException("Linkly Cloud terminal assignment is not supported by this client."));

    Task<LinklyConnectionTestResult> TestConnectionAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);

    Task<LinklyConnectionTestResult> TestTransactionStatusAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default);

    Task<PaymentAuthorizationResult> PurchaseAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default);

    Task<PaymentAuthorizationResult> RefundAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        CancellationToken cancellationToken = default);

    Task<LinklySettlementResult> SettlementAsync(
        PosSessionState session,
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new LinklySettlementResult(
            false,
            "Linkly settlement is not supported by this backend terminal client."));

    Task<LinklyCloudBackendSessionResponse?> GetResumableSettlementAsync(
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<LinklyCloudBackendSessionResponse?>(null);

    Task AcknowledgeSettlementAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    Task MarkSettlementReceiptPrintedAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    Task<LinklyCloudBackendSessionResponse?> GetResumableSessionAsync(
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default);

    Task<LinklyCloudBackendSessionResponse> RecoverSessionAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<LinklyCloudBackendSessionResponse> ResumeSessionUntilFinalAsync(
        CardTerminalSettings settings,
        LinklyCloudBackendSessionResponse activeStatus,
        CancellationToken cancellationToken = default);

    Task<LinklyCloudBackendSessionResponse> GetSessionStatusAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken = default);

    Task AcknowledgeSessionAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken = default);

    // 主管已在 POS 上对该会话作出结案决定时使用：服务端会把仍非终态的会话记为 SupervisorResolved，
    // 让终端管理（换线、配对、连接测试）不再被这笔结果未知的会话永久挡住。
    Task AcknowledgeSupervisorResolvedSessionAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken = default);
}

public sealed class LinklyBackendTerminalClient(
    HttpClient httpClient,
    ILinklyTerminalDialogService dialogService,
    TimeSpan? pollInterval = null,
    Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
    ILocalizationService? localization = null,
    ILinklyPaymentAttemptContextAccessor? paymentAttemptContextAccessor = null,
    TimeSpan? businessWait = null,
    ILinklyBankReceiptPrinter? bankReceiptPrinter = null,
    TimeProvider? timeProvider = null) : ILinklyBackendTerminalClient
{
    private const string ProcessorName = "ANZ";
    private const string CloudBackendInvalidRequestErrorCode = "LINKLY_CLOUD_BACKEND_REQUEST_INVALID";
    private const string CloudBackendActiveOperationErrorCode = "LINKLY_CLOUD_BACKEND_ACTIVE_TRANSACTION";
    private const string CloudBackendTerminalSelectionConflictErrorCode = "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT";
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly TimeSpan _pollInterval = pollInterval.GetValueOrDefault(DefaultPollInterval);
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync = delayAsync ?? Task.Delay;
    private readonly TimeSpan _businessWait = businessWait.GetValueOrDefault(LinklyTimeoutPolicy.BusinessWait);
    // 业务等待计时器走 TimeProvider，测试可注入 FakeTimeProvider 直接推进虚拟时间，不靠墙钟窗口判断是否超时。
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly object _terminalDirectorySync = new();
    private readonly Dictionary<CardTerminalEnvironment, LinklyCloudTerminalListResponse> _terminalDirectories = [];

    public async Task<LinklyCloudTerminalListResponse> GetTerminalsAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var relativeUrl = $"api/v1/linkly/cloud-backend/terminals?environment={Uri.EscapeDataString(environment.ToString())}";
            var stopwatch = Stopwatch.StartNew();
            using var response = await SendWithFailureLogAsync(
                "terminal directory",
                HttpMethod.Get,
                FormatRequestUrl(relativeUrl),
                txnType: null,
                stopwatch,
                () => httpClient.GetAsync(relativeUrl, cancellationToken),
                cancellationToken);
            var directory = await ReadTerminalApiResultAsync<LinklyCloudTerminalListResponse>(
                response,
                "terminal directory",
                HttpMethod.Get,
                FormatRequestUrl(relativeUrl),
                stopwatch,
                cancellationToken);
            if (!string.Equals(directory.Environment, environment.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Linkly terminal directory response environment did not match the request.");
            }
            lock (_terminalDirectorySync)
            {
                _terminalDirectories[environment] = directory;
            }

            Log(
                $"terminal directory loaded environment={environment} count={directory.Terminals.Count} " +
                $"selectedTerminalId={directory.SelectedTerminalId?.ToString("D") ?? "<null>"} revision={directory.SelectionRevision?.ToString(CultureInfo.InvariantCulture) ?? "<null>"}");
            return directory;
        }
        catch
        {
            InvalidateTerminalDirectory(environment);
            throw;
        }
    }

    public async Task<LinklyCloudTerminalSelectionResponse> SelectTerminalAsync(
        CardTerminalEnvironment environment,
        Guid terminalId,
        long? expectedRevision,
        CancellationToken cancellationToken = default)
    {
        try
        {
            const string relativeUrl = "api/v1/linkly/cloud-backend/terminal-selection";
            var request = new LinklyCloudTerminalSelectionRequest(environment.ToString(), terminalId, expectedRevision);
            var stopwatch = Stopwatch.StartNew();
            using var response = await SendWithFailureLogAsync(
                "terminal selection",
                HttpMethod.Put,
                FormatRequestUrl(relativeUrl),
                txnType: null,
                stopwatch,
                () => httpClient.PutAsJsonAsync(relativeUrl, request, JsonOptions, cancellationToken),
                cancellationToken);
            var selection = await ReadTerminalApiResultAsync<LinklyCloudTerminalSelectionResponse>(
                response,
                "terminal selection",
                HttpMethod.Put,
                FormatRequestUrl(relativeUrl),
                stopwatch,
                cancellationToken);
            if (selection.TerminalId != terminalId ||
                !string.Equals(selection.Environment, environment.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Linkly terminal selection response identity did not match the request.");
            }
            lock (_terminalDirectorySync)
            {
                var hasCurrent = _terminalDirectories.TryGetValue(environment, out var current);
                _terminalDirectories[environment] = new LinklyCloudTerminalListResponse(
                    selection.Environment,
                    selection.TerminalId,
                    selection.Revision,
                    hasCurrent ? current!.Terminals : [],
                    hasCurrent ? current!.Mode : "Legacy");
            }

            Log(
                $"terminal selection saved environment={environment} terminalId={terminalId:D} " +
                $"revision={selection.Revision.ToString(CultureInfo.InvariantCulture)}");
            return selection;
        }
        catch
        {
            // PUT 可能已经到达服务器；未知或失配响应必须清除缓存，避免下一笔交易沿用错误线路。
            InvalidateTerminalDirectory(environment);
            throw;
        }
    }

    private void InvalidateTerminalDirectory(CardTerminalEnvironment environment)
    {
        lock (_terminalDirectorySync)
        {
            _terminalDirectories.Remove(environment);
        }
    }

    public async Task<LinklyCloudTerminalPairResponse> PairTerminalAsync(
        CardTerminalEnvironment environment,
        Guid terminalId,
        string pairCode,
        CancellationToken cancellationToken = default)
    {
        var relativeUrl = $"api/v1/linkly/cloud-backend/terminals/{terminalId:D}/pair";
        // 配对码属于一次性敏感输入，只进入请求体，不写日志或客户端持久化。
        var request = new LinklyCloudBackendPairRequest(environment.ToString(), pairCode);
        var stopwatch = Stopwatch.StartNew();
        // 只记录操作名与 URL，配对码所在的请求体绝不进入日志。
        using var response = await SendWithFailureLogAsync(
            "terminal pair",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            stopwatch,
            () => httpClient.PostAsJsonAsync(relativeUrl, request, JsonOptions, cancellationToken),
            cancellationToken);
        var result = await ReadTerminalApiResultAsync<LinklyCloudTerminalPairResponse>(
            response,
            "terminal pair",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            stopwatch,
            cancellationToken);
        lock (_terminalDirectorySync)
        {
            if (_terminalDirectories.TryGetValue(environment, out var current))
            {
                var terminals = current.Terminals
                    .Select(terminal => terminal.TerminalId == result.TerminalId
                        ? terminal with
                        {
                            PairingState = result.PairingState,
                            IsReady = result.IsReady
                        }
                        : terminal)
                    .ToArray();
                _terminalDirectories[environment] = current with { Terminals = terminals };
            }
        }

        Log(
            $"terminal pairing completed environment={environment} terminalId={terminalId:D} " +
            $"pairingState={result.PairingState} isReady={result.IsReady}");
        return result;
    }

    public async Task<LinklyCloudTerminalConnectionTestResponse> TestTerminalConnectionAsync(
        Guid terminalId,
        LinklyCloudTerminalConnectionTestRequest request,
        CancellationToken cancellationToken = default)
    {
        var relativeUrl = $"api/v1/linkly/cloud-backend/terminals/{terminalId:D}/connection-test";
        var stopwatch = Stopwatch.StartNew();
        using var response = await SendWithFailureLogAsync(
            "terminal connection test",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            stopwatch,
            () => httpClient.PostAsJsonAsync(relativeUrl, request, JsonOptions, cancellationToken),
            cancellationToken);
        return await ReadTerminalApiResultAsync<LinklyCloudTerminalConnectionTestResponse>(
            response,
            "terminal connection test",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            stopwatch,
            cancellationToken);
    }

    public async Task<LinklyCloudTerminalListResponse> AssignTerminalAsync(
        Guid terminalId,
        LinklyCloudTerminalAssignmentRequest request,
        CancellationToken cancellationToken = default)
    {
        var relativeUrl = $"api/v1/linkly/cloud-backend/terminals/{terminalId:D}/assignment";
        var stopwatch = Stopwatch.StartNew();
        using var response = await SendWithFailureLogAsync(
            "terminal assignment",
            HttpMethod.Put,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            stopwatch,
            () => httpClient.PutAsJsonAsync(relativeUrl, request, JsonOptions, cancellationToken),
            cancellationToken);
        var directory = await ReadTerminalApiResultAsync<LinklyCloudTerminalListResponse>(
            response,
            "terminal assignment",
            HttpMethod.Put,
            FormatRequestUrl(relativeUrl),
            stopwatch,
            cancellationToken);
        if (Enum.TryParse<CardTerminalEnvironment>(directory.Environment, ignoreCase: true, out var environment))
        {
            lock (_terminalDirectorySync)
            {
                _terminalDirectories[environment] = directory;
            }
        }

        return directory;
    }

    public async Task<LinklyConnectionTestResult> TestConnectionAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Log($"logon test start environment={environment} componentVersion={GetComponentVersion()}");
            var result = await SendTerminalScopedRequestAsync(
                "logon-test",
                HttpMethod.Post,
                "api/v1/linkly/cloud-backend/logon-test",
                environment,
                txnRefSelector: null,
                cancellationToken);
            if (result.IsSuccessStatusCode)
            {
                var logon = ReadLogonTestResult(result.Content);
                Log($"logon test completed environment={environment} success={logon.Succeeded} responseCode={LogValue(logon.ResponseCode)}");
                return new LinklyConnectionTestResult(logon.Succeeded, logon.Message);
            }

            var message = ReadSetupFailureMessage(result.StatusCode, result.Content) ?? TryReadLogonTestMessage(result.Content) ??
                string.Format(
                    CultureInfo.InvariantCulture,
                    T("linkly.backend.logonTestHttpFailed", "ANZ Linkly Cloud logon test failed with HTTP {0}."),
                    (int)result.StatusCode);
            Log($"logon test failed environment={environment} http={(int)result.StatusCode}");
            return new LinklyConnectionTestResult(false, message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or LinklyBackendHttpException or JsonException)
        {
            Log($"logon test failed environment={environment} error={ex.GetType().Name}");
            return new LinklyConnectionTestResult(false, T("settings.linkly.cloudBackend.testCommunicationFailed", "Test incomplete. Check the network and terminal screen before retrying."));
        }
    }

    public async Task<LinklyConnectionTestResult> TestTransactionStatusAsync(
        CardTerminalEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await SendTerminalScopedRequestAsync(
                "transaction-status-test",
                HttpMethod.Post,
                "api/v1/linkly/cloud-backend/status-test",
                environment,
                ReadStatusTestTxnRef,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var message = ReadSetupFailureMessage(response.StatusCode, response.Content) ?? TryReadStatusTestMessage(response.Content) ??
                    string.Format(
                        CultureInfo.InvariantCulture,
                        T("linkly.backend.statusTestHttpFailed", "ANZ Linkly Cloud transaction status test failed with HTTP {0}."),
                        (int)response.StatusCode);
                return new LinklyConnectionTestResult(false, message);
            }

            var result = ReadStatusTestResult(response.Content);
            return new LinklyConnectionTestResult(
                result.Succeeded,
                result.Message,
                new LinklyStatusTestDetails(
                    result.TransactionReference,
                    result.RequestedAt,
                    result.ResponseCode,
                    result.ResponseText,
                    result.ResponseTxnRef));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or LinklyBackendHttpException or JsonException)
        {
            Log($"transaction status test failed environment={environment} error={ex.GetType().Name}");
            return new LinklyConnectionTestResult(false, T("settings.linkly.cloudBackend.testCommunicationFailed", "Test incomplete. Check the network and terminal screen before retrying."));
        }
    }

    public Task<PaymentAuthorizationResult> PurchaseAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default)
    {
        return RunAsync("P", amount, session, settings, refundReference: null, cancellationToken);
    }

    public async Task<PaymentAuthorizationResult> RefundAsync(
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
        if (string.IsNullOrWhiteSpace(refundReference))
        {
            refundReference = await TryResolveOriginalBackendRefundReferenceAsync(settings, originalReference, cancellationToken);
        }

        return string.IsNullOrWhiteSpace(refundReference)
            ? new PaymentAuthorizationResult(false, null, T("linkly.backend.refundMissingReference", "Linkly Cloud refund requires an original RFN reference."))
            : await RunAsync("R", amount, session, settings, refundReference, cancellationToken);
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

        // CancelAfter 不接受 TimeProvider，改为独立的业务等待计时 CTS 再与外部令牌链接，语义不变。
        using var businessWaitCts = new CancellationTokenSource(_businessWait, _timeProvider);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, businessWaitCts.Token);
        LinklyCloudBackendSessionResponse? status = null;
        var settlementStartRequested = false;
        var submitted = false;
        try
        {
            status = await GetResumableSettlementAsync(settings, timeoutCts.Token);
            if (status is null)
            {
                settlementStartRequested = true;
                var terminalSelection = GetCachedTerminalSelection(settings.Environment);
                try
                {
                    status = await StartSettlementAsync(
                        new LinklyCloudBackendSettlementRequest(
                            settings.Environment.ToString(),
                            terminalSelection.TerminalId,
                            terminalSelection.SelectionRevision),
                        timeoutCts.Token);
                }
                catch (LinklyBackendHttpException ex) when (
                    ex.HttpStatus == HttpStatusCode.Conflict &&
                    string.Equals(
                        ex.ErrorCode,
                        CloudBackendTerminalSelectionConflictErrorCode,
                        StringComparison.Ordinal))
                {
                    // Fresh Daily Close 尚未访问设置/付款页时，首个 409 明确未创建 session；刷新目录后只重试一次。
                    LogSettlementEvent(
                        "retry",
                        "terminal-selection-conflict",
                        sessionId: null,
                        ex,
                        LinklyLogLevel.Information);
                    settlementStartRequested = false;
                    await GetTerminalsAsync(settings.Environment, timeoutCts.Token);
                    terminalSelection = GetCachedTerminalSelection(settings.Environment);
                    settlementStartRequested = true;
                    status = await StartSettlementAsync(
                        new LinklyCloudBackendSettlementRequest(
                            settings.Environment.ToString(),
                            terminalSelection.TerminalId,
                            terminalSelection.SelectionRevision),
                        timeoutCts.Token);
                }
                submitted = true;
            }
            else
            {
                // 已存在的未完成结算必须继续查询同一 session，禁止新建或重发结算请求。
                submitted = true;
            }

            while (!IsSettlementFinal(status))
            {
                await DelayAsync(_pollInterval, timeoutCts.Token);
                status = await GetSettlementStatusAsync(settings, status.SessionId, timeoutCts.Token);
            }

            var succeeded = !IsSettlementFailureStatus(status.Status) &&
                IsSuccessfulSettlement(status.OperationSuccess, status.ResponseCode);
            return new LinklySettlementResult(
                succeeded,
                FormatSettlementMessage(status, succeeded),
                status.SessionId,
                NormalizeOptional(status.ResponseCode),
                NormalizeOptional(status.ResponseText),
                NormalizeOptional(status.SettlementData),
                status.SettlementReceiptTexts,
                ProviderSubmissionState: ProviderSubmissionState.Submitted);
        }
        catch (OperationCanceledException ex) when (settlementStartRequested || submitted)
        {
            // 结算请求可能已到终端：无论调用方取消还是业务超时，结果都未知，需人工确认。
            LogSettlementEvent(
                "unknown",
                cancellationToken.IsCancellationRequested ? "caller-cancelled-after-submit" : "timeout",
                status?.SessionId,
                ex);
            return SettlementUnknown(status?.SessionId);
        }
        catch (LinklyBackendHttpException ex) when (
            status is null &&
            IsDefinitiveSettlementStartRejection(ex))
        {
            // 仅后端的显式错误码可证明尚未创建 settlement session；任意 4xx 都不能推断为未提交。
            LogSettlementEvent(
                "rejected",
                $"settlement rejected before submit http={(int)ex.HttpStatus} code={LogValue(ex.ErrorCode)}",
                sessionId: null,
                ex);
            if (string.Equals(ex.ErrorCode, CloudBackendActiveOperationErrorCode, StringComparison.Ordinal))
            {
                LinklyCloudBackendSessionResponse? activeStatus = null;
                try
                {
                    activeStatus = await GetActiveSessionAsync(settings, timeoutCts.Token);
                }
                catch (Exception activeLookupException) when (activeLookupException is HttpRequestException or JsonException)
                {
                    LogSettlementEvent(
                        "failed",
                        "active session lookup after settlement conflict failed",
                        sessionId: null,
                        activeLookupException);
                }

                return RejectActiveSessionForNewSettlement(activeStatus, ex.Message);
            }

            return new LinklySettlementResult(
                false,
                ex.Message,
                ProviderSubmissionState: ProviderSubmissionState.NotSubmitted);
        }
        catch (Exception ex) when ((settlementStartRequested || submitted) && (ex is LinklyBackendHttpException or HttpRequestException or JsonException))
        {
            LogSettlementEvent(
                "unknown",
                $"settlement unknown error={ex.GetType().Name}",
                status?.SessionId,
                ex);
            return SettlementUnknown(status?.SessionId);
        }
        catch (OperationCanceledException ex)
        {
            // 提交前：调用方取消是正常操作只记 Information；业务等待超时记 Warning。
            LogSettlementEvent(
                cancellationToken.IsCancellationRequested ? "cancelled" : "failed",
                cancellationToken.IsCancellationRequested ? "caller-cancelled-before-submit" : "timeout",
                status?.SessionId,
                ex,
                cancellationToken.IsCancellationRequested ? LinklyLogLevel.Information : null);
            return new LinklySettlementResult(
                false,
                cancellationToken.IsCancellationRequested ? "ANZ Linkly settlement was cancelled." : "ANZ Linkly settlement timed out.",
                status?.SessionId,
                ProviderSubmissionState: ProviderSubmissionState.NotSubmitted);
        }
        catch (Exception ex) when (ex is LinklyBackendHttpException or HttpRequestException or JsonException)
        {
            LogSettlementEvent(
                "failed",
                $"settlement failed before submit error={ex.GetType().Name}",
                status?.SessionId,
                ex);
            return new LinklySettlementResult(
                false,
                ex.Message,
                status?.SessionId,
                ProviderSubmissionState: ProviderSubmissionState.NotSubmitted);
        }
    }

    public Task<LinklyCloudBackendSessionResponse?> GetResumableSessionAsync(
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default)
    {
        return GetResumableSessionCoreAsync(settings, cancellationToken);
    }

    public Task<LinklyCloudBackendSessionResponse?> GetResumableSettlementAsync(
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default)
    {
        return GetResumableSettlementCoreAsync(settings, cancellationToken);
    }

    public Task<LinklyCloudBackendSessionResponse> RecoverSessionAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        return RecoverAsync(settings, sessionId, cancellationToken);
    }

    public Task<LinklyCloudBackendSessionResponse> GetSessionStatusAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        return GetStatusAsync(settings, sessionId, cancellationToken);
    }

    public Task AcknowledgeSessionAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        return AcknowledgeSessionCoreAsync(settings, sessionId, supervisorResolved: false, cancellationToken);
    }

    public Task AcknowledgeSupervisorResolvedSessionAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        return AcknowledgeSessionCoreAsync(settings, sessionId, supervisorResolved: true, cancellationToken);
    }

    private async Task AcknowledgeSessionCoreAsync(
        CardTerminalSettings settings,
        string sessionId,
        bool supervisorResolved,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var relativeUrl = $"api/v1/linkly/cloud-backend/transactions/{Uri.EscapeDataString(sessionId)}/acknowledge";
        var request = new LinklyCloudBackendAcknowledgeRequest(settings.Environment.ToString(), supervisorResolved);
        LogHttpRequest(
            "acknowledge",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            txnRef: null,
            bodyJson: SerializeDebugJson(request));
        using var response = await SendWithFailureLogAsync(
            "acknowledge",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            stopwatch,
            () => httpClient.PostAsJsonAsync(relativeUrl, request, JsonOptions, cancellationToken),
            cancellationToken);
        _ = await ReadApiResultAsync(
            response,
            "acknowledge",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            stopwatch,
            cancellationToken);
    }

    private async Task<PaymentAuthorizationResult> RunAsync(
        string txnType,
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? refundReference,
        CancellationToken cancellationToken)
    {
        if (amount <= 0m)
        {
            return new PaymentAuthorizationResult(false, null, T("linkly.backend.amountMustBePositive", "Card amount must be greater than zero."));
        }

        var keepDialogOpen = false;
        var transactionSubmitted = false;
        var activeSessionConflictDetected = false;
        var activeSessionTakeoverAttempted = false;
        string? lastTakenOverSessionId = null;
        // 仅用于失败日志串联：POST 成功拿到 session 后记下，异常分支以它作 TraceId。
        string? submittedSessionId = null;
        CancellationTokenSource? transactionBusinessWaitCts = null;
        CancellationTokenSource? transactionTimeoutCts = null;
        Log($"transaction request start txnType={txnType} environment={settings.Environment} componentVersion={GetComponentVersion()}");

        try
        {
            // 付款/退款开始即冻结已由本机 UI 确认的选择。后续仅允许无副作用的健康检查刷新目录，
            // 绝不能让后台刷新把本次扣款静默改投到另一台终端。
            var paymentTerminalSelection = GetCachedTerminalSelection(settings.Environment);
            var fallbackTxnRef = BuildTxnRef(session);
            BackendReadinessResult readiness;
            using (var readinessCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                // 每个提交前 HTTP 操作单独计时，旧会话接管耗时不能吃掉后续查询窗口。
                readinessCts.CancelAfter(LinklyTimeoutPolicy.HttpTimeout);
                readiness = await CheckBackendReadinessAsync(settings, readinessCts.Token);
            }

            if (!readiness.IsReady)
            {
                return FallbackAllowed("linkly.backend.unavailable", readiness.Message);
            }

            var preflightActive = await GetActiveSessionWithHttpTimeoutAsync(settings, cancellationToken);
            if (preflightActive is not null)
            {
                // 发现活动中的 Linkly session 时先接管（查询/落库/ack），成功后才允许新扣款。
                var (preflightBlock, preflightAttempted) = await TakeOverActiveSessionOrBlockAsync(
                    settings,
                    preflightActive,
                    cancellationToken,
                    activeSessionTakeoverAttempted);
                activeSessionTakeoverAttempted |= preflightAttempted;
                if (preflightBlock is not null)
                {
                    return preflightBlock;
                }

                if (preflightAttempted)
                {
                    // 接管旧会话本身会改变交易边界；即使接管成功，也必须由收银员重新发起本次付款，
                    // 禁止在同一动作中继续创建新 session。
                    var message = T(
                        "linkly.backend.activeSessionRequiresRecovery",
                        "Current terminal still has an unfinished card transaction. Recover it before starting a new payment.");
                    await PresentFinalFailureAsync("backend-active-recovered", message, cancellationToken);
                    return ActiveSessionRecoveryRequired(message);
                }
            }

            var request = new LinklyCloudBackendTransactionRequest(
                settings.Environment.ToString(),
                txnType,
                ToMinorUnits(amount),
                BuildPurchaseAnalysisData(amount, session, refundReference),
                paymentTerminalSelection.TerminalId,
                paymentTerminalSelection.SelectionRevision,
                ResolveAttemptGuidForBackendTxnRef(txnType));

            LinklyCloudBackendSessionResponse status;
            try
            {
                // 交易提交后使用完整业务等待窗口，避免过早中断已提交交易。
                // 计时 CTS 走 TimeProvider 后再与外部令牌链接，语义与 CancelAfter 相同。
                transactionBusinessWaitCts = new CancellationTokenSource(_businessWait, _timeProvider);
                transactionTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    transactionBusinessWaitCts.Token);

                while (true)
                {
                    try
                    {
                        // 已取消的调用不能先跨入“可能已提交”边界；通过检查后再标记 POST 风险窗口。
                        cancellationToken.ThrowIfCancellationRequested();
                        transactionTimeoutCts.Token.ThrowIfCancellationRequested();
                        // 每次 POST 都重新进入可能已提交区间；若服务端明确返回 409，catch 会立刻撤销该标记。
                        activeSessionConflictDetected = false;
                        transactionSubmitted = true;
                        status = await StartTransactionAsync(request, transactionTimeoutCts.Token);
                        submittedSessionId = NormalizeOptional(status.SessionId);
                        await NotifyPaymentAttemptSessionStartedAsync(status, cancellationToken);
                        break;
                    }
                    catch (LinklyBackendHttpException startEx) when (startEx.HttpStatus == HttpStatusCode.Conflict)
                    {
                        // 409 是服务端明确拒绝创建新 session；此后的异常只属于旧会话查询/接管，
                        // 不能把当前新 attempt 误记为未知，也不能允许切换 Linkly 通道继续扣款。
                        transactionSubmitted = false;
                        if (!string.Equals(
                                startEx.ErrorCode,
                                CloudBackendActiveOperationErrorCode,
                                StringComparison.Ordinal))
                        {
                            activeSessionConflictDetected = false;
                            if (string.Equals(
                                    startEx.ErrorCode,
                                    CloudBackendTerminalSelectionConflictErrorCode,
                                    StringComparison.Ordinal))
                            {
                                try
                                {
                                    // 只刷新 UI/业务缓存，不自动重放可能扣款的原始 POST；用户确认终端后手动重试。
                                    await GetTerminalsAsync(settings.Environment, cancellationToken);
                                }
                                catch (Exception refreshException) when (
                                    refreshException is HttpRequestException or JsonException or LinklyBackendHttpException)
                                {
                                    Log($"terminal directory refresh after selection conflict failed error={refreshException.GetType().Name}");
                                }
                            }

                            var conflictMessage = string.Equals(
                                startEx.ErrorCode,
                                CloudBackendTerminalSelectionConflictErrorCode,
                                StringComparison.Ordinal)
                                ? T(
                                    "linkly.backend.terminalSelectionChanged",
                                    "The selected Linkly terminal changed. Confirm the terminal shown and retry the payment.")
                                : startEx.Message;
                            await PresentFinalFailureAsync(
                                "backend-terminal-selection-conflict",
                                conflictMessage,
                                cancellationToken);
                            return new PaymentAuthorizationResult(
                                false,
                                null,
                                conflictMessage,
                                StatusKey: "linkly.backend.terminalSelectionChanged");
                        }

                        activeSessionConflictDetected = true;
                        // 409 表示后端发现活动 session；重新读取它并走与 preflight 相同的接管函数。
                        // 后端的建会话闸门还会被"已到终态但 POS 从未确认"的会话挡住（例如上一笔在绑定会话前断电，
                        // 顾客随后在终端上完成了刷卡）。active 接口只返回仍在途的会话，查不到时必须再问 resumable，
                        // 否则这台设备的 Cloud 刷卡会一直被拒，而收银台只看到"稍后再试"。
                        var conflictActive = await GetActiveSessionWithHttpTimeoutAsync(settings, cancellationToken)
                            ?? await GetResumableSessionWithHttpTimeoutAsync(settings, cancellationToken);
                        if (conflictActive is null)
                        {
                            var message = T("linkly.backend.activeSessionUnavailable", "Current terminal has an unfinished card transaction, but no recoverable active session was returned. Try again later.");
                            await PresentFinalFailureAsync("backend-active-unavailable", message, cancellationToken);
                            keepDialogOpen = true;
                            return new PaymentAuthorizationResult(false, null, message);
                        }

                        if (activeSessionTakeoverAttempted)
                        {
                            // 第二次 409 仍要读取权威活动会话。若终端已经切换到另一笔旧会话，
                            // 先定点接管并落库该会话，但本次新 attempt 不再执行第三次 POST。
                            var conflictSessionId = NormalizeOptional(conflictActive.SessionId);
                            if (!string.Equals(
                                    conflictSessionId,
                                    lastTakenOverSessionId,
                                    StringComparison.Ordinal))
                            {
                                var (secondConflictBlock, _) = await TakeOverActiveSessionOrBlockAsync(
                                    settings,
                                    conflictActive,
                                    cancellationToken,
                                    activeSessionTakeoverAttempted: false);
                                if (secondConflictBlock is not null)
                                {
                                    return secondConflictBlock;
                                }

                                lastTakenOverSessionId = conflictSessionId;
                            }

                            var repeatedMessage = T(
                                "linkly.backend.activeSessionRequiresRecovery",
                                "Current terminal still has an unfinished card transaction. Recover it before starting a new payment.");
                            await PresentFinalFailureAsync("backend-active-unavailable", repeatedMessage, cancellationToken);
                            return ActiveSessionRecoveryRequired(repeatedMessage);
                        }

                        var (conflictBlock, conflictAttempted) = await TakeOverActiveSessionOrBlockAsync(
                            settings,
                            conflictActive,
                            cancellationToken,
                            activeSessionTakeoverAttempted);
                        activeSessionTakeoverAttempted |= conflictAttempted;
                        if (conflictBlock is not null)
                        {
                            return conflictBlock;
                        }

                        if (conflictAttempted)
                        {
                            lastTakenOverSessionId = NormalizeOptional(conflictActive.SessionId);
                        }

                        // 409 虽能证明本次新 session 未创建，但旧会话接管已发生。为了避免同一
                        // 收银动作在终端状态变化后自动再次扣款，必须停止并由人工确认后重试。
                        var recoveredMessage = T(
                            "linkly.backend.activeSessionRequiresRecovery",
                            "Current terminal still has an unfinished card transaction. Recover it before starting a new payment.");
                        await PresentFinalFailureAsync("backend-active-recovered", recoveredMessage, cancellationToken);
                        return ActiveSessionRecoveryRequired(recoveredMessage);
                    }
                }
            }
            catch (LinklyBackendHttpException ex) when (IsBackendStartRejectedBeforeSession(ex))
            {
                // 请求在本地 session 创建前被后端拒绝，直接提示失败；此时没有未知交易需要恢复。
                var message = string.IsNullOrWhiteSpace(ex.Message)
                    ? T("linkly.backend.configIncomplete", "ANZ Linkly Cloud backend configuration is incomplete.")
                    : ex.Message;
                await PresentFinalFailureAsync("backend-start-rejected", message, cancellationToken);
                return FallbackAllowed("linkly.backend.configIncomplete", message);
            }

            using var localCancelCts = CancellationTokenSource.CreateLinkedTokenSource(
                transactionTimeoutCts.Token,
                dialogService.LocalCancelToken);
            var pollResult = await PollUntilFinalAsync(settings, status, localCancelCts.Token, cancellationToken);
            status = pollResult.Status;
            var result = ToAuthorizationResult(
                status,
                amount,
                fallbackTxnRef,
                suppressPrintedReceipt: false,
                pollResult.ManualCancelRequested);
            var declinedReceiptPrintResult = await TryPrintSignatureDeclinedReceiptAsync(
                settings,
                status,
                result,
                pollResult.SignatureDeclineRequested,
                cancellationToken);
            if (declinedReceiptPrintResult is { Succeeded: false })
            {
                result = result with
                {
                    Message = AppendDeclinedReceiptPrintFailure(result.Message, declinedReceiptPrintResult.Message)
                };
            }

            // 收银员已发起取消时，最终失败状态只是取消结果说明；应关闭 Linkly 弹窗，让支付页恢复操作。
            keepDialogOpen = !result.Approved && !pollResult.ManualCancelRequested;
            return result;
        }
        catch (LinklyBackendLocalCancelException ex)
        {
            LogTransactionAborted(
                txnType,
                "unknown",
                "local-cancel",
                submittedSessionId,
                transactionSubmitted,
                activeSessionConflictDetected,
                cancellationToken.IsCancellationRequested,
                ex);
            var message = T(
                "linkly.backend.cancelledUnknown",
                "Stopped waiting for the ANZ Linkly Cloud backend card result. The transaction may have reached the terminal; recover the previous transaction or confirm the result in Linkly before retrying.");
            return ResultUnknown("linkly.backend.cancelledUnknown", message);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested && !transactionSubmitted)
        {
            // 调用方在 POST 前主动取消，属于正常操作，只记 Information。
            LogTransactionAborted(
                txnType,
                "cancelled",
                "caller-cancelled-before-submit",
                submittedSessionId,
                transactionSubmitted,
                activeSessionConflictDetected,
                callerCancelled: true,
                ex,
                LinklyLogLevel.Information);
            // 明确告诉 workflow：取消发生在本次新交易 POST 之前。退款 claim 只是本地 fencing，
            // 不能因此把本次新付款/退款误记为已提交或 ResultUnknown。
            throw new CardTerminalNotSubmittedException(ex, cancellationToken);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested && transactionSubmitted)
        {
            // 虽是调用方取消，但交易可能已到终端、结果未知，按 Warning 记录以便异常中心对账。
            LogTransactionAborted(
                txnType,
                "unknown",
                "caller-cancelled-after-submit",
                submittedSessionId,
                transactionSubmitted,
                activeSessionConflictDetected,
                callerCancelled: true,
                ex);
            // POST 已进入 HTTP 管线后，即使调用方同时取消，也无法证明后端没有创建会话。
            // 必须保守落入异常中心，禁止把它当作普通取消后再次扣款。
            var message = T(
                "linkly.backend.cancelledUnknown",
                "Stopped waiting for the ANZ Linkly Cloud backend card result. The transaction may have reached the terminal; recover the previous transaction or confirm the result in Linkly before retrying.");
            return ResultUnknown("linkly.backend.cancelledUnknown", message);
        }
        catch (OperationCanceledException ex) when (dialogService.LocalCancelToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            LogTransactionAborted(
                txnType,
                "unknown",
                "local-cancel",
                submittedSessionId,
                transactionSubmitted,
                activeSessionConflictDetected,
                callerCancelled: false,
                ex);
            var message = T(
                "linkly.backend.cancelledUnknown",
                "Stopped waiting for the ANZ Linkly Cloud backend card result. The transaction may have reached the terminal; recover the previous transaction or confirm the result in Linkly before retrying.");
            return ResultUnknown("linkly.backend.cancelledUnknown", message);
        }
        catch (OperationCanceledException ex) when (transactionTimeoutCts?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            LogTransactionAborted(
                txnType,
                transactionSubmitted ? "unknown" : "failed",
                "timeout",
                submittedSessionId,
                transactionSubmitted,
                activeSessionConflictDetected,
                callerCancelled: false,
                ex);
            var message = T("linkly.backend.timeout", "ANZ Linkly Cloud transaction timed out.");
            await PresentFinalFailureAsync("backend-timeout", message, cancellationToken);
            keepDialogOpen = true;
            return activeSessionConflictDetected
                ? ActiveSessionRecoveryRequired(message)
                : transactionSubmitted
                ? ResultUnknown("linkly.backend.resultUnknown", BuildResultUnknownMessage(message))
                : FallbackAllowed("linkly.backend.timeout", message);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // 非调用方、非业务超时的取消（如 HttpClient 内部超时）：结果可能未知，按失败记 Warning。
            LinklyJsonLog.Write(
                "LinklyBackend",
                "backend-terminal",
                "operation-cancelled",
                transactionSubmitted ? "unknown" : "failed",
                sessionId: submittedSessionId,
                success: false,
                reason: "wait-cancelled",
                details: new
                {
                    message =
                        $"operation-cancelled source={ex.GetType().Name} transactionSubmitted={transactionSubmitted} " +
                        $"businessTimeoutCancelled={transactionTimeoutCts?.IsCancellationRequested == true} " +
                        $"localCancelRequested={dialogService.LocalCancelToken.IsCancellationRequested} " +
                        $"callerCancelled={cancellationToken.IsCancellationRequested}"
                },
                exception: ex);
            var message = T(
                "linkly.backend.waitCancelled",
                "Waiting for the ANZ Linkly Cloud backend response was cancelled before the transaction result could be confirmed.");
            await PresentFinalFailureAsync("backend-wait-cancelled", message, cancellationToken);
            keepDialogOpen = true;
            // 只有新交易可能已提交时才进入未知结果；409 后仍处于旧会话接管阶段。
            return activeSessionConflictDetected
                ? ActiveSessionRecoveryRequired(message)
                : transactionSubmitted
                ? ResultUnknown("linkly.backend.resultUnknown", BuildResultUnknownMessage(message))
                : FallbackAllowed("linkly.backend.waitCancelled", message);
        }
        catch (HttpRequestException ex)
        {
            LogTransactionAborted(
                txnType,
                transactionSubmitted ? "unknown" : "failed",
                "http-error",
                submittedSessionId,
                transactionSubmitted,
                activeSessionConflictDetected,
                cancellationToken.IsCancellationRequested,
                ex);
            var message = T("linkly.backend.communicationFailed", "ANZ Linkly Cloud backend communication failed.");
            await PresentFinalFailureAsync("backend-http-error", message, cancellationToken);
            keepDialogOpen = true;
            return activeSessionConflictDetected
                ? ActiveSessionRecoveryRequired(message)
                : transactionSubmitted
                ? ResultUnknown("linkly.backend.resultUnknown", BuildResultUnknownMessage(message))
                : FallbackAllowed("linkly.backend.communicationFailed", message);
        }
        catch (JsonException ex)
        {
            LogTransactionAborted(
                txnType,
                transactionSubmitted ? "unknown" : "failed",
                "invalid-response",
                submittedSessionId,
                transactionSubmitted,
                activeSessionConflictDetected,
                cancellationToken.IsCancellationRequested,
                ex);
            var message = T("linkly.backend.invalidResponse", "ANZ Linkly Cloud backend returned an invalid response.");
            await PresentFinalFailureAsync("backend-json-error", message, cancellationToken);
            keepDialogOpen = true;
            return activeSessionConflictDetected
                ? ActiveSessionRecoveryRequired(message)
                : transactionSubmitted
                ? ResultUnknown("linkly.backend.resultUnknown", BuildResultUnknownMessage(message))
                : FallbackAllowed("linkly.backend.invalidResponse", message);
        }
        finally
        {
            transactionTimeoutCts?.Dispose();
            transactionBusinessWaitCts?.Dispose();
            // 闂傚倷鑳堕幊鎾绘偤閵娾晛绀夐柡鍥╁枑閸欏繑绻涢幋鐐垫噮妞も晜鐓￠弻鏇㈠醇濠靛浂妫″銈庡亝缁捇寮婚妶鍡欓檮濠㈣泛顦遍惄搴㈢節濞堝灝鏋撻柡鍛Т椤曪綁濡搁埡浣虹暰闂佺粯顨呴悧鍡涙⒒椤栨稐绻嗛柣鎰典簻閳ь剚顨婂顐ゆ嫚瀹割喚鍔烽梺鍝勵槹椤戞瑩宕甸弴銏＄厱闁挎棁顕ч獮妯尖偓瑙勬礀閻栧ジ寮婚妸銉㈡婵炲棙鍨熷Σ鍫ユ⒑闂堟稒澶勯柛銊ョ秺楠炲繗銇愰幒鎳炽劑鏌ㄩ弮鈧崹婵堝垝椤栨粎纾介柛灞剧懅椤︼箓鏌ｅΔ鈧换鎴﹀箞閵娾晛绠瑰ù锝呮憸閻ｈ鲸绻涙潏鍓хМ妞ゃ儲鎸剧划缁樼鐎ｎ偆鍘介梺鎸庣箓濡盯骞婇崨顖滅＜妞ゆ棁顕у畵鍡欌偓娈垮枔閸旀垵鐣锋總绋垮嵆闁绘梻顭堝▓蹇涙⒒娴ｅ憡鍟炵紒瀣浮閳ワ箓宕堕鈧悘铏繆椤栨艾鎮戝┑顖氥偢閺屾洟宕煎┑鍡樻疁闂?
            if (!keepDialogOpen)
            {
                // 关闭页面弹窗不能复用交易 token；收银员取消交易时该 token 可能已经取消。
                await dialogService.CloseAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// 交易被取消/超时/通信失败时写一条结构化日志：已提交的记 unknown（结果未知需对账），未提交的记 failed。
    /// </summary>
    private static void LogTransactionAborted(
        string txnType,
        string phase,
        string reason,
        string? sessionId,
        bool transactionSubmitted,
        bool activeSessionConflictDetected,
        bool callerCancelled,
        Exception exception,
        LinklyLogLevel? level = null)
    {
        LinklyJsonLog.Write(
            "LinklyBackend",
            "backend-terminal",
            "transaction",
            phase,
            sessionId: sessionId,
            httpStatus: (exception as LinklyBackendHttpException)?.HttpStatus,
            success: false,
            reason: reason,
            details: new
            {
                txnType,
                transactionSubmitted,
                activeSessionConflictDetected,
                callerCancelled,
                errorCode = (exception as LinklyBackendHttpException)?.ErrorCode
            },
            exception: exception,
            level: level);
    }

    private static void LogSettlementEvent(
        string phase,
        string reason,
        string? sessionId,
        Exception exception,
        LinklyLogLevel? level = null)
    {
        LinklyJsonLog.Write(
            "LinklyBackend",
            "backend-terminal",
            "settlement",
            phase,
            sessionId: sessionId,
            httpStatus: (exception as LinklyBackendHttpException)?.HttpStatus,
            success: false,
            reason: reason,
            details: new
            {
                errorCode = (exception as LinklyBackendHttpException)?.ErrorCode
            },
            exception: exception,
            level: level);
    }

    private static PaymentAuthorizationResult ActiveSessionRecoveryRequired(string message) =>
        new(
            false,
            null,
            message,
            StatusKey: "linkly.backend.activeSessionRequiresRecovery");

    public Task<LinklyCloudBackendSessionResponse> ResumeSessionUntilFinalAsync(
        CardTerminalSettings settings,
        LinklyCloudBackendSessionResponse activeStatus,
        CancellationToken cancellationToken = default)
    {
        return ResumeActiveSessionAsync(settings, activeStatus, cancellationToken);
    }

    private async Task<LinklyCloudBackendSessionResponse> ResumeActiveSessionAsync(
        CardTerminalSettings settings,
        LinklyCloudBackendSessionResponse activeStatus,
        CancellationToken cancellationToken)
    {
        // 恢复上一笔交易也只等待一个业务窗口，超时后必须保留未知结果。
        using var businessWaitCts = new CancellationTokenSource(_businessWait, _timeProvider);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, businessWaitCts.Token);
        var lastStatus = activeStatus;
        try
        {
            var status = await PresentStatusAsync(
                settings,
                activeStatus,
                T("linkly.backend.activeSessionResume", "Current terminal has an unfinished card transaction. Continuing to poll/recover that session."),
                timeoutCts.Token);
            lastStatus = status;
            if (!IsFinal(status))
            {
                status = await RecoverAsync(settings, status.SessionId, timeoutCts.Token);
                lastStatus = status;
            }

            var pollResult = await PollUntilFinalAsync(settings, status, timeoutCts.Token, cancellationToken);
            return pollResult.Status;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var detail = string.Format(
                CultureInfo.InvariantCulture,
                "{0} SessionId={1}; TxnRef={2}; Status={3}.",
                T("linkly.backend.recoveryTimeout", "ANZ Linkly Cloud recovery timed out."),
                LogValue(lastStatus.SessionId),
                LogValue(lastStatus.TxnRef),
                LogValue(lastStatus.Status));
            throw new LinklyBackendResultUnknownException(BuildResultUnknownMessage(detail));
        }
    }

    private async Task<(PaymentAuthorizationResult? Block, bool TakeoverAttempted)> TakeOverActiveSessionOrBlockAsync(
        CardTerminalSettings settings,
        LinklyCloudBackendSessionResponse activeStatus,
        CancellationToken cancellationToken,
        bool activeSessionTakeoverAttempted)
    {
        if (IsSettlementOperation(activeStatus))
        {
            // 结算不是卡片/退款活动会话，直接返回可编辑的未提交结果，不接管也不发起新扣款。
            var settlementMessage = T(
                "linkly.backend.activeSettlementBlocksPayment",
                "Current terminal already has an unfinished Linkly settlement. Resolve that settlement before starting a new card payment.");
            await PresentFinalFailureAsync(activeStatus.SessionId, settlementMessage, cancellationToken);
            return (
                new PaymentAuthorizationResult(
                    false,
                    null,
                    settlementMessage,
                    StatusKey: "linkly.backend.activeSettlementBlocksPayment",
                    FallbackAllowed: true),
                false);
        }

        if (activeSessionTakeoverAttempted)
        {
            // 同一个新 attempt 只允许接管一次；第二次冲突直接拒绝，避免无限重试。
            return (await RejectActiveSessionForNewPaymentAsync(activeStatus, cancellationToken), true);
        }

        var takeOver = paymentAttemptContextAccessor?.Current?.TakeOverActiveSessionAsync;
        if (takeOver is null)
        {
            // 未接入 workflow 接管回调时保持旧行为：拒绝并提示恢复。
            return (await RejectActiveSessionForNewPaymentAsync(activeStatus, cancellationToken), true);
        }

        // 回调既可能抛出取消，也可能在返回前只标记 caller token；两种情况都必须在发起新交易前停止。
        var takeover = await takeOver(settings, activeStatus, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (takeover.Succeeded)
        {
            return (null, true);
        }

        var message = string.IsNullOrWhiteSpace(takeover.Message)
            ? T("linkly.backend.activeSessionRequiresRecovery", "Current terminal already has an unfinished card transaction. Recover the previous transaction before starting a new payment.")
            : takeover.Message;
        await PresentFinalFailureAsync(activeStatus.SessionId, message, cancellationToken);
        // 旧 session 未能释放时，本次新交易尚未提交；不能制造新的 ResultUnknown 锁。
        return (
            new PaymentAuthorizationResult(
                false,
                null,
                message,
                StatusKey: "linkly.backend.activeSessionRequiresRecovery"),
            true);
    }

    private async Task<PaymentAuthorizationResult> RejectActiveSessionForNewPaymentAsync(
        LinklyCloudBackendSessionResponse activeStatus,
        CancellationToken cancellationToken)
    {
        var activeOperation = IsSettlementOperation(activeStatus) ? "settlement" : "card transaction";
        var message = IsSettlementOperation(activeStatus)
            ? T(
                "linkly.backend.activeSettlementRequiresRecovery",
                "Current terminal already has an unfinished Linkly settlement. Resolve that settlement before starting a new card payment.")
            : T(
                "linkly.backend.activeSessionRequiresRecovery",
                "Current terminal already has an unfinished card transaction. Recover the previous transaction or ask a supervisor to confirm Linkly before starting a new payment.");
        Log(
            $"active session rejected for new payment sessionId={activeStatus.SessionId} " +
            $"operation={activeOperation} txnRef={LogValue(activeStatus.TxnRef)} status={activeStatus.Status}");
        await PresentFinalFailureAsync(activeStatus.SessionId, message, cancellationToken);
        return new PaymentAuthorizationResult(
            false,
            null,
            message,
            StatusKey: "linkly.backend.activeSessionRequiresRecovery");
    }

    private PaymentAuthorizationResult FallbackAllowed(string statusKey, string message)
    {
        return new PaymentAuthorizationResult(false, null, message, StatusKey: statusKey, FallbackAllowed: true);
    }

    private PaymentAuthorizationResult ResultUnknown(string statusKey, string message)
    {
        return new PaymentAuthorizationResult(false, null, message, StatusKey: statusKey, ResultUnknown: true);
    }

    // 销售 attempt 的引用在发请求前已落库。交易引用仍由后端生成，这里只把 attempt 身份交给它，
    // 后端用同一算法（LinklyAttemptTxnRef）派生出与本地完全相同的引用：请求发出后即使断电或响应丢失，
    // 恢复与接管仍能按 TxnRef 认领这笔会话。只有本地引用确实由该 attempt 身份派生时才发送，否则后端
    // 派生出的值会与本地记录不一致。Cloud 退款的本地引用是另一套规则，继续由后端随机生成；
    // 旧后端会忽略该字段，绑定会话时再以后端返回的引用为准。
    private Guid? ResolveAttemptGuidForBackendTxnRef(string txnType)
    {
        var context = paymentAttemptContextAccessor?.Current;
        if (context is null ||
            context.AttemptGuid == Guid.Empty ||
            !string.Equals(txnType, "P", StringComparison.Ordinal))
        {
            return null;
        }

        return string.Equals(
            NormalizeOptional(context.TxnRef),
            LinklyLocalTxnRef.Create('P', context.AttemptGuid.ToString("D")),
            StringComparison.Ordinal)
            ? context.AttemptGuid
            : null;
    }

    private static bool IsBackendStartRejectedBeforeSession(LinklyBackendHttpException ex)
    {
        return ex.HttpStatus is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity;
    }

    private static bool IsDefinitiveSettlementStartRejection(LinklyBackendHttpException ex)
    {
        return ex.HttpStatus is HttpStatusCode.BadRequest or HttpStatusCode.Conflict &&
            (string.Equals(ex.ErrorCode, CloudBackendInvalidRequestErrorCode, StringComparison.Ordinal) ||
             string.Equals(ex.ErrorCode, CloudBackendActiveOperationErrorCode, StringComparison.Ordinal) ||
             string.Equals(ex.ErrorCode, CloudBackendTerminalSelectionConflictErrorCode, StringComparison.Ordinal));
    }

    private LinklySettlementResult RejectActiveSessionForNewSettlement(
        LinklyCloudBackendSessionResponse? activeStatus,
        string fallbackMessage)
    {
        var message = activeStatus is not null && !IsSettlementOperation(activeStatus)
            ? T(
                "linkly.backend.activeTransactionBlocksSettlement",
                "Current terminal already has an unfinished card transaction. Recover it before starting a Linkly settlement.")
            : activeStatus is not null
                ? T(
                    "linkly.backend.activeSettlementRequiresRecovery",
                    "Current terminal already has an unfinished Linkly settlement. Resolve it before starting another settlement.")
                : string.IsNullOrWhiteSpace(fallbackMessage)
                    ? T(
                        "linkly.backend.activeSessionUnavailable",
                        "Current terminal has an unfinished Linkly operation. Try again later.")
                    : fallbackMessage;
        Log($"active session rejected for settlement operation={activeStatus?.OperationType ?? "unknown"} sessionId={activeStatus?.SessionId ?? "<null>"}");
        return new LinklySettlementResult(
            false,
            message,
            ProviderSubmissionState: ProviderSubmissionState.NotSubmitted);
    }

    private static bool IsSettlementOperation(LinklyCloudBackendSessionResponse status)
    {
        return string.Equals(status.OperationType, "Settlement", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<BackendReadinessResult> CheckBackendReadinessAsync(
        CardTerminalSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await SendTerminalScopedRequestAsync(
                "backend health preflight",
                HttpMethod.Get,
                "api/v1/linkly/cloud-backend/health",
                settings.Environment,
                txnRefSelector: null,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return BackendReadinessResult.NotReady(BuildBackendUnavailableMessage());
            }

            var health = ReadHealthResult(response.Content);
            if (!health.IsReady)
            {
                var details = FormatHealthFailure(health);
                return BackendReadinessResult.NotReady(string.IsNullOrWhiteSpace(details)
                    ? BuildBackendUnavailableMessage()
                    : details);
            }

            return BackendReadinessResult.Ready;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or LinklyBackendHttpException)
        {
            Log($"backend health preflight failed environment={settings.Environment} error={ex.GetType().Name}");
            return BackendReadinessResult.NotReady(BuildBackendUnavailableMessage());
        }
    }

    private string BuildBackendUnavailableMessage()
    {
        return T(
            "linkly.backend.unavailable",
            "ANZ Linkly Cloud backend API is offline. Cloud backend card payment was not started. Check the network or use another payment method.");
    }

    private string BuildResultUnknownMessage(string detail)
    {
        var guidance = T(
            "linkly.backend.resultUnknown",
            "ANZ Linkly Cloud backend transaction result is unknown. Confirm the Linkly transaction status before retrying.");
        return string.IsNullOrWhiteSpace(detail)
            ? guidance
            : $"{detail} {guidance}";
    }

    private static bool IsSettlementFinal(LinklyCloudBackendSessionResponse status)
    {
        if (string.Equals(status.Status, StatusFailed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status.Status, StatusNotSubmitted, StringComparison.OrdinalIgnoreCase) ||
            IsCancelledStatus(status.Status))
        {
            return true;
        }

        // Settlement 完成通知可能先于 Type=S 回单到达；回单可用前不能确认 session，
        // 否则持久化/ack 后将永久失去需要打印的结算单。
        return status.OperationSuccess.HasValue &&
            (status.SettlementReceiptTexts ?? []).Any(receipt => !string.IsNullOrWhiteSpace(receipt));
    }

    private static string FormatSettlementMessage(
        LinklyCloudBackendSessionResponse status,
        bool succeeded)
    {
        var text = NormalizeOptional(status.ResponseText);
        var code = NormalizeOptional(status.ResponseCode);
        var fallback = succeeded
            ? "ANZ Linkly settlement completed."
            : "ANZ Linkly settlement failed.";
        return code is null ? text ?? fallback : $"{text ?? fallback} ({code})";
    }

    private static LinklySettlementResult SettlementUnknown(string? sessionId)
    {
        return new LinklySettlementResult(
            false,
            "ANZ Linkly settlement result is unknown. Confirm the Linkly settlement status before trying again.",
            sessionId,
            ResultUnknown: true);
    }

    private async Task<LinklyBackendPollResult> PollUntilFinalAsync(
        CardTerminalSettings settings,
        LinklyCloudBackendSessionResponse status,
        CancellationToken cancellationToken,
        CancellationToken callerCancellationToken)
    {
        var manualCancelRequested = false;
        var signatureDeclineRequested = false;
        void MarkManualCancelRequested() => manualCancelRequested = true;
        void MarkSignatureDeclineRequested() => signatureDeclineRequested = true;

        var signatureSlipPrintState = new LinklySignatureSlipPrintState();
        status = await PresentStatusAsync(settings, status, message: null, cancellationToken, MarkManualCancelRequested, MarkSignatureDeclineRequested, signatureSlipPrintState);
        var shouldRefreshImmediately = !IsFinal(status) && !RequiresRecovery(status);
        while (!IsFinal(status) || IsUnresolvedSignatureReceiptStatus(status))
        {
            if (shouldRefreshImmediately)
            {
                LogStatusSnapshot("poll loop immediate refresh", status);
                shouldRefreshImmediately = false;
            }
            else
            {
                LogStatusSnapshot("poll loop before delay", status);
                await DelayBeforeNextPollAsync(status, cancellationToken);
            }

            status = RequiresRecovery(status)
                ? await RecoverAsync(settings, status.SessionId, cancellationToken)
                : await GetStatusAsync(settings, status.SessionId, cancellationToken);
            status = await PresentStatusAsync(settings, status, message: null, cancellationToken, MarkManualCancelRequested, MarkSignatureDeclineRequested, signatureSlipPrintState);
        }

        if (IsCompletedOrPendingSuccess(status) &&
            !HasReceipt(status))
        {
            // 批准或拒付已经确定，补取小票只是尽力而为：任何一次刷新失败都保留已拿到的终态，
            // 否则已扣款的批准会被改写成结果未知并锁进恢复流程，确定的拒付也会变成未知。
            for (var attempt = 0; attempt < 3 && !HasReceipt(status); attempt++)
            {
                try
                {
                    await DelayAsync(_pollInterval, cancellationToken);

                    var refreshed = await GetStatusAsync(settings, status.SessionId, cancellationToken);
                    if (!IsConsistentReceiptRefresh(status, refreshed))
                    {
                        LogStatusSnapshot("receipt refresh ignored inconsistent status", refreshed);
                        break;
                    }

                    status = refreshed;
                    status = await PresentStatusAsync(settings, status, message: null, cancellationToken, MarkManualCancelRequested, MarkSignatureDeclineRequested, signatureSlipPrintState);
                }
                catch (Exception ex) when (IsReceiptRefreshFailure(ex, callerCancellationToken))
                {
                    Log($"receipt refresh stopped sessionId={status.SessionId} attempt={attempt + 1} error={ex.GetType().Name}");
                    break;
                }
            }
        }

        return new LinklyBackendPollResult(status, manualCancelRequested, signatureDeclineRequested);
    }

    private static bool IsConsistentReceiptRefresh(
        LinklyCloudBackendSessionResponse current,
        LinklyCloudBackendSessionResponse refreshed)
    {
        // 这次刷新只为补小票：必须仍是同一会话、仍为终态，且批准/拒付判定不变，才能替换已确定的终态。
        return string.Equals(current.SessionId, refreshed.SessionId, StringComparison.Ordinal) &&
            IsFinal(refreshed) &&
            IsCompletedOrPendingSuccess(refreshed) &&
            IsApprovedFinalTransaction(current) == IsApprovedFinalTransaction(refreshed);
    }

    private static bool IsReceiptRefreshFailure(Exception exception, CancellationToken callerCancellationToken)
    {
        // 后端 5xx、网络异常、响应无效、业务窗口到期、收银员停止等待都只结束补取；
        // 调用方自己的取消（关闭支付页、退出程序）照常上抛，由调用方决定如何收尾。
        return exception is HttpRequestException or JsonException ||
            (exception is OperationCanceledException && !callerCancellationToken.IsCancellationRequested);
    }

    private async Task NotifyPaymentAttemptSessionStartedAsync(
        LinklyCloudBackendSessionResponse status,
        CancellationToken cancellationToken)
    {
        var context = paymentAttemptContextAccessor?.Current;
        if (context is null)
        {
            return;
        }

        try
        {
            await context.BindSessionAsync(
                status.SessionId,
                status.TxnRef,
                DateTimeOffset.UtcNow,
                cancellationToken);
            Log(
                $"payment attempt session bound attemptGuid={context.AttemptGuid} " +
                $"sessionId={status.SessionId} txnRef={LogValue(status.TxnRef)} status={status.Status}");
            LinklyJsonLog.Write(
                "CardRecovery",
                "card-recovery",
                "payment-attempt",
                "session-bound",
                sessionId: status.SessionId,
                success: true,
                details: new
                {
                    timestamp = DateTimeOffset.Now,
                    attemptGuid = context.AttemptGuid,
                    sessionId = status.SessionId,
                    txnRef = NormalizeOptional(status.TxnRef),
                    remoteStatus = status.Status,
                    responseCode = status.ResponseCode,
                    responseText = status.ResponseText,
                    environment = status.Environment,
                    storeCode = status.StoreCode,
                    deviceCode = status.DeviceCode
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // session 缂傚倸鍊搁崐鐑芥倿閿曞倸鍨傞柣銏犳啞閸嬧晠鏌ｉ幇闈涘缂佸墎鍋ゅ鍫曞醇椤愵澀鍑介悶姘箖缁绘盯骞嬮悙鏉戠缂備礁顦遍崗妯虹暦濡も偓铻ｉ柤濮愬€愰弸鏍ь渻閵堝棙灏甸柛鐘冲姍閹偤鎳為妷锕€寮垮┑鐘茬仛閹搁箖宕氶悧鍫㈢瘈闁逞屽墴椤㈡棃宕ㄩ缁樺攭婵犵數鍋為崹鍓佸垝閻樿缁╁ù鐘差儐閻撶喖鏌曟繛鍨姎妞ゅ繆鏅犻弻娑㈠煛閸屾粍鍒涢悗瑙勬穿缁绘繈宕洪妷鈺佸窛妞ゆ牗绮犲Σ宄扳攽閻愯埖褰х紒鑼舵铻炴俊銈呭暞瀹曟煡鏌嶈閸撴瑩鈥旈崘顔嘉ч柛顐ｇ箓閹偤姊洪崫鍕棏闁稿鎸荤换娑㈠箣閻愭潙纰嶅銈嗗灥濡瑧绮嬮幒妤婃晩闁芥ê顦辩粣鐐烘倵楠炲灝鍔氶柣妤€绻愬嵄婵鍩栭悡娑㈡煕鐏炶鈧洟鐛鈧弻娑㈠Χ閸涱喗宕冲┑鈥冲级閸旀瑩鏁愰悙渚晞闁芥ê顦竟?
            Log(
                $"payment attempt session bind failed attemptGuid={context.AttemptGuid} " +
                $"sessionId={status.SessionId} txnRef={LogValue(status.TxnRef)} error={ex.GetType().Name}");
            LinklyJsonLog.Write(
                "CardRecovery",
                "card-recovery",
                "payment-attempt",
                "session-bind-failed",
                sessionId: status.SessionId,
                success: false,
                reason: ex.GetType().Name,
                details: new
                {
                    timestamp = DateTimeOffset.Now,
                    attemptGuid = context.AttemptGuid,
                    sessionId = status.SessionId,
                    txnRef = NormalizeOptional(status.TxnRef),
                    remoteStatus = status.Status,
                    responseCode = status.ResponseCode,
                    responseText = status.ResponseText,
                    environment = status.Environment,
                    storeCode = status.StoreCode,
                    deviceCode = status.DeviceCode
                });
        }
    }

    private async Task<LinklyCloudBackendSessionResponse> PresentStatusAsync(
        CardTerminalSettings settings,
        LinklyCloudBackendSessionResponse status,
        string? message,
        CancellationToken cancellationToken,
        Action? onCancelSendKey = null,
        Action? onSignatureDeclineSendKey = null,
        LinklySignatureSlipPrintState? signatureSlipPrintState = null)
    {
        while (true)
        {
            var signatureSlipPrinted = await TryPrintSignatureSlipAsync(
                settings,
                status,
                signatureSlipPrintState,
                cancellationToken);
            if (signatureSlipPrinted is { Succeeded: false })
            {
                message = signatureSlipPrinted.Message;
                if (signatureSlipPrintState is not null)
                {
                    signatureSlipPrintState.LastFailureMessage = signatureSlipPrinted.Message;
                }
            }
            else if (signatureSlipPrinted is { Succeeded: true } &&
                signatureSlipPrintState?.LastFailureMessage is { } lastPrintFailure &&
                string.Equals(message, lastPrintFailure, StringComparison.Ordinal))
            {
                message = null;
                signatureSlipPrintState.LastFailureMessage = null;
            }

            var dialogState = ToDialogState(status, message);
            var updateStopwatch = Stopwatch.StartNew();
            var action = await dialogService.UpdateAsync(dialogState, cancellationToken);
            updateStopwatch.Stop();
            Log(
                $"dialog update completed sessionId={status.SessionId} elapsedMs={updateStopwatch.ElapsedMilliseconds} " +
                $"status={status.Status} display=\"{LogValue(TruncateForLog(dialogState.DisplayText, 80))}\" " +
                $"buttons={(dialogState.DisplayButtons?.Count ?? 0)} message=\"{LogValue(TruncateForLog(message, 80))}\"");
            if (IsFinal(status) || action is null || string.IsNullOrWhiteSpace(action.Key))
            {
                return status;
            }

            if (IsLocalCancelAction(action))
            {
                throw new LinklyBackendLocalCancelException();
            }

            if (IsSignatureApprovalAction(action) &&
                IsSignatureReceiptPrompt(status) &&
                signatureSlipPrinted?.Succeeded == false)
            {
                // 签名小票没有打出来前，不能让店员确认签名并完成订单。
                message = signatureSlipPrinted.Message;
                continue;
            }

            var isCancelSendKey = IsCancelSendKeyAction(status, action);
            var isSignatureDeclineSendKey = IsSignatureDeclineAction(status, action);
            if (isCancelSendKey)
            {
                // 只有当前终端明确显示取消键时，才记录收银员发起了取消动作。
                onCancelSendKey?.Invoke();
            }

            try
            {
                status = await SendKeyAsync(settings, status.SessionId, action, cancellationToken);
                if (isSignatureDeclineSendKey)
                {
                    // 只有签名确认阶段明确发送 No/Declined，最终失败后才补打 Declined 银行小票。
                    onSignatureDeclineSendKey?.Invoke();
                }

                LogStatusSnapshot("manual sendkey completed", status);
                message = null;
            }
            catch (LinklyBackendHttpException ex) when (ex.HttpStatus == HttpStatusCode.BadRequest)
            {
                // Linkly 拒绝 sendkey 只代表这次终端动作未被接受，不能据此结束交易等待。
                message = isCancelSendKey
                    ? null
                    : T("linkly.backend.sendKeyRejected", "Card terminal action was not accepted. Continue waiting for the transaction result.");
                try
                {
                    status = await GetStatusAsync(settings, status.SessionId, cancellationToken);
                }
                catch (HttpRequestException)
                {
                    // 状态刷新失败时保留当前 session，进入下一轮轮询或恢复。
                }

                continue;
            }
            catch (HttpRequestException)
            {
                // sendkey 可能已到达终端；继续查当前 session，避免本地提前结束未知交易。
                if (isSignatureDeclineSendKey)
                {
                    // 网络异常不能证明 No/Declined 未送达；保留拒签意图，最终 SIGNATURE ERROR 后仍补打 Declined 银行小票。
                    onSignatureDeclineSendKey?.Invoke();
                }

                message = isCancelSendKey
                    ? null
                    : T("linkly.backend.sendKeyFailed", "Card terminal action failed. Try again or recover the transaction.");
                try
                {
                    status = await GetStatusAsync(settings, status.SessionId, cancellationToken);
                }
                catch (HttpRequestException)
                {
                    // 状态刷新失败时保留当前 session，进入下一轮轮询或恢复。
                }

                continue;
            }
        }
    }
    private static bool IsLocalCancelAction(LinklyTerminalDialogAction action)
    {
        return string.Equals(action.Key, LinklyTerminalDialogKeys.LocalCancel, StringComparison.Ordinal);
    }

    private static bool IsCancelSendKeyAction(
        LinklyCloudBackendSessionResponse status,
        LinklyTerminalDialogAction action)
    {
        return status.CancelKeyFlag &&
            !status.OKKeyFlag &&
            string.Equals(LinklyTerminalDialogKeys.Normalize(action.Key), LinklyTerminalDialogKeys.OkCancel, StringComparison.Ordinal);
    }

    private static bool IsSignatureApprovalAction(LinklyTerminalDialogAction action)
    {
        var normalizedKey = LinklyTerminalDialogKeys.Normalize(action.Key);
        return string.Equals(normalizedKey, LinklyTerminalDialogKeys.Yes, StringComparison.Ordinal) ||
            string.Equals(normalizedKey, LinklyTerminalDialogKeys.Auth, StringComparison.Ordinal);
    }

    private static bool IsSignatureDeclineAction(
        LinklyCloudBackendSessionResponse status,
        LinklyTerminalDialogAction action)
    {
        return IsSignatureReceiptPrompt(status) &&
            string.Equals(LinklyTerminalDialogKeys.Normalize(action.Key), LinklyTerminalDialogKeys.No, StringComparison.Ordinal);
    }

    private async Task<ReceiptPrintResult?> TryPrintSignatureSlipAsync(
        CardTerminalSettings settings,
        LinklyCloudBackendSessionResponse status,
        LinklySignatureSlipPrintState? printState,
        CancellationToken cancellationToken)
    {
        if (bankReceiptPrinter is null ||
            printState is null ||
            !IsSignatureReceiptPrompt(status))
        {
            return null;
        }

        var printKey = status.SessionId;
        if (status.ReceiptPrintedAt is not null)
        {
            // 后端已确认签名小票打印过时，本轮只标记本地去重，避免重复补打。
            printState.PrintedKeys.Add(printKey);
            return new ReceiptPrintResult(true, T("receipt.print.success", "Receipt printed."));
        }

        var receiptText = ReadReceiptText(status);
        if (printState.PrintedKeys.Contains(printKey))
        {
            return new ReceiptPrintResult(true, T("receipt.print.success", "Receipt printed."));
        }

        var result = await bankReceiptPrinter.PrintAsync(
            settings.Environment.ToString(),
            status.SessionId,
            receiptText!,
            LinklyBankReceiptKind.SignatureRequired,
            cancellationToken: cancellationToken);
        if (result.Succeeded)
        {
            printState.PrintedKeys.Add(printKey);
            Log($"signature slip printed sessionId={status.SessionId}");
        }
        else
        {
            Log($"signature slip print failed sessionId={status.SessionId} message={LogValue(result.Message)}");
        }

        return result;
    }

    private async Task<ReceiptPrintResult?> TryPrintSignatureDeclinedReceiptAsync(
        CardTerminalSettings settings,
        LinklyCloudBackendSessionResponse status,
        PaymentAuthorizationResult result,
        bool signatureDeclineRequested,
        CancellationToken cancellationToken)
    {
        if (bankReceiptPrinter is null ||
            !signatureDeclineRequested ||
            result.Approved)
        {
            return null;
        }

        var receiptText = ReadReceiptText(status);
        if (!IsDeclinedBankReceiptText(receiptText))
        {
            return null;
        }

        var printResult = await bankReceiptPrinter.PrintAsync(
            settings.Environment.ToString(),
            status.SessionId,
            receiptText!,
            LinklyBankReceiptKind.Declined,
            cardType: result.CardTransactions?.FirstOrDefault()?.CardType,
            maskedCardNumber: result.CardTransactions?.FirstOrDefault()?.MaskedCardNumber,
            responseCode: result.ResponseCode,
            responseText: result.ResponseText,
            cancellationToken: cancellationToken);
        if (printResult.Succeeded)
        {
            Log($"signature declined receipt printed sessionId={status.SessionId}");
        }
        else
        {
            Log($"signature declined receipt print failed sessionId={status.SessionId} message={LogValue(printResult.Message)}");
        }

        return printResult;
    }

    private string AppendDeclinedReceiptPrintFailure(
        string? originalMessage,
        string? printMessage)
    {
        var original = NormalizeOptional(originalMessage) ??
            T("linkly.backend.declined", "ANZ Linkly Cloud transaction was declined.");
        var printFailure = NormalizeOptional(printMessage) ??
            T("receipt.print.failed", "Receipt print failed.");
        return string.Concat(
            original,
            " ",
            T("linkly.backend.declinedReceiptPrintFailed", "Declined bank receipt could not be printed:"),
            " ",
            printFailure);
    }

    private static bool IsDeclinedBankReceiptText(string? receiptText)
    {
        var text = NormalizeOptional(receiptText);
        return text is not null &&
            (text.Contains("DECLINED", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("SIGNATURE ERROR", StringComparison.OrdinalIgnoreCase));
    }

    private Task PresentFinalFailureAsync(
        string sessionId,
        string message,
        CancellationToken cancellationToken)
    {
        return dialogService.UpdateAsync(
            new LinklyTerminalDialogState(
                sessionId,
                StatusFailed,
                message,
                ReceiptText: null,
                ResponseText: message,
                RecoveryCount: 0,
                LastHttpStatus: null,
                Message: null,
                ResponseCode: null,
                IsInteractive: false,
                IsFinal: true,
                DisplayButtons: []),
            cancellationToken);
    }

    private LinklyTerminalDialogState ToDialogState(
        LinklyCloudBackendSessionResponse status,
        string? message)
    {
        var isFinal = IsFinal(status);
        var responseText = NormalizeOptional(status.ResponseText);
        // 显示文本优先级：最终状态用 ResponseText 或 Status；自动继续提示用等待文案；其余用终端 DisplayText。
        var displayText = isFinal
            ? responseText ?? NormalizeOptional(status.Status)
            : IsAutoContinueDisplay(status)
                ? T("payment.status.cardProcessing", "Waiting for card terminal result...")
            : NormalizeOptional(status.DisplayText);

        return new LinklyTerminalDialogState(
            status.SessionId,
            status.Status,
            displayText,
            ReadReceiptText(status),
            responseText,
            status.RecoveryCount,
            status.LastHttpStatus,
            NormalizeOptional(message),
            LinklyTerminalDialogMode.CloudBackendInteractive,
            IsInteractive: !isFinal,
            IsFinal: isFinal,
            DisplayButtons: BuildDisplayButtons(status),
            InputType: NormalizeOptional(status.InputType),
            GraphicCode: NormalizeOptional(status.GraphicCode),
            SupportsCancelPayment: SupportsCancelPayment(status),
            ResponseCode: NormalizeOptional(status.ResponseCode));
    }

    private static IReadOnlyList<LinklyTerminalDialogButton> BuildDisplayButtons(
        LinklyCloudBackendSessionResponse status)
    {
        if (IsFinal(status))
        {
            return [];
        }

        var buttons = new List<LinklyTerminalDialogButton>();
        if (!HasDisplayNotification(status))
        {
            return buttons;
        }

        var signatureDisplayFlags = TryReadLatestSignatureDisplayFlags(status);
        if (IsCardTerminalWaitDisplay(status) && signatureDisplayFlags is null)
        {
            return buttons;
        }

        var okKeyFlag = status.OKKeyFlag;
        var cancelKeyFlag = status.CancelKeyFlag;
        var acceptYesKeyFlag = status.AcceptYesKeyFlag;
        var declineNoKeyFlag = status.DeclineNoKeyFlag;
        var authoriseKeyFlag = status.AuthoriseKeyFlag;
        if (signatureDisplayFlags is not null &&
            !acceptYesKeyFlag &&
            !declineNoKeyFlag &&
            !authoriseKeyFlag)
        {
            // 顶层 session 有时会回退成旧 PROCESSING，签名确认按钮以最新 SIGNATURE OK? display 通知为准。
            okKeyFlag = signatureDisplayFlags.Value.OKKeyFlag;
            cancelKeyFlag = signatureDisplayFlags.Value.CancelKeyFlag;
            acceptYesKeyFlag = signatureDisplayFlags.Value.AcceptYesKeyFlag;
            declineNoKeyFlag = signatureDisplayFlags.Value.DeclineNoKeyFlag;
            authoriseKeyFlag = signatureDisplayFlags.Value.AuthoriseKeyFlag;
        }

        // Linkly REST sendkey 对 OK 和 CANCEL 都使用 Key=0，按终端旗标决定按钮文案。
        if (okKeyFlag && cancelKeyFlag)
        {
            buttons.Add(new LinklyTerminalDialogButton("linkly.backend.dialog.button.okCancel", LinklyTerminalDialogKeys.OkCancel));
        }
        else if (okKeyFlag)
        {
            buttons.Add(new LinklyTerminalDialogButton("linkly.backend.dialog.button.ok", LinklyTerminalDialogKeys.OkCancel));
        }

        if (acceptYesKeyFlag)
        {
            buttons.Add(new LinklyTerminalDialogButton("linkly.backend.dialog.button.yesApproved", LinklyTerminalDialogKeys.Yes));
        }

        if (declineNoKeyFlag)
        {
            buttons.Add(new LinklyTerminalDialogButton("linkly.backend.dialog.button.noDeclined", LinklyTerminalDialogKeys.No, IsDestructive: true));
        }

        if (authoriseKeyFlag)
        {
            // 签名授权发送 AUTH=3，不能复用 OK/CANCEL 的 Key=0。
            buttons.Add(new LinklyTerminalDialogButton("linkly.backend.dialog.button.authoriseSignature", LinklyTerminalDialogKeys.Auth));
        }

        if (!okKeyFlag && cancelKeyFlag)
        {
            buttons.Add(CreateCancelButton());
        }

        return buttons;
    }

    private static (
        bool OKKeyFlag,
        bool CancelKeyFlag,
        bool AcceptYesKeyFlag,
        bool DeclineNoKeyFlag,
        bool AuthoriseKeyFlag)? TryReadLatestSignatureDisplayFlags(LinklyCloudBackendSessionResponse status)
    {
        if (!IsSignatureReceiptPrompt(status))
        {
            return null;
        }

        foreach (var notification in (status.Notifications ?? []).Reverse())
        {
            if (!string.Equals(notification.Type, "display", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(notification.PayloadJson) ||
                !notification.PayloadJson.Contains("SIGNATURE OK?", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(notification.PayloadJson);
                var response = ReadResponse(document.RootElement);
                return (
                    ReadFlag(response, "OKKeyFlag"),
                    ReadFlag(response, "CancelKeyFlag"),
                    ReadFlag(response, "AcceptYesKeyFlag"),
                    ReadFlag(response, "DeclineNoKeyFlag"),
                    ReadFlag(response, "AuthoriseKeyFlag"));
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private static LinklyTerminalDialogButton CreateCancelButton()
    {
        return new LinklyTerminalDialogButton(
            "linkly.backend.dialog.button.cancel",
            LinklyTerminalDialogKeys.OkCancel,
            IsDestructive: true);
    }

    private static bool HasDisplayNotification(LinklyCloudBackendSessionResponse status)
    {
        return (status.Notifications ?? [])
            .Any(notification => string.Equals(notification.Type, "display", StringComparison.OrdinalIgnoreCase));
    }

    private static bool SupportsCancelPayment(LinklyCloudBackendSessionResponse status)
    {
        var signatureDisplayFlags = TryReadLatestSignatureDisplayFlags(status);
        if (signatureDisplayFlags is not null)
        {
            // 签名确认阶段以最新 SIGNATURE OK? 通知为准，避免旧 PROCESSING 顶层标志重新打开全局取消。
            return signatureDisplayFlags.Value.CancelKeyFlag;
        }

        if (IsSignatureReceiptPrompt(status) && !status.CancelKeyFlag)
        {
            return false;
        }

        if (status.CancelKeyFlag)
        {
            return true;
        }

        return (status.Notifications ?? []).Any(NotificationHasCancelKeyFlag);
    }

    private static bool NotificationHasCancelKeyFlag(LinklyCloudBackendNotificationDto notification)
    {
        if (!string.Equals(notification.Type, "display", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(notification.PayloadJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(notification.PayloadJson);
            var response = ReadResponse(document.RootElement);
            return ReadFlag(response, "CancelKeyFlag");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsCardTerminalWaitDisplay(LinklyCloudBackendSessionResponse status)
    {
        var display = NormalizeDisplayPrompt(status.DisplayText);
        if (IsCardTerminalWaitPrompt(display))
        {
            return true;
        }

        return (status.DisplayLines ?? [])
            .Select(NormalizeDisplayPrompt)
            .Any(IsCardTerminalWaitPrompt);
    }

    private static bool IsAutoContinueDisplay(LinklyCloudBackendSessionResponse status)
    {
        var display = NormalizeDisplayPrompt(status.DisplayText);
        if (IsAutoContinuePrompt(display))
        {
            return true;
        }

        return (status.DisplayLines ?? [])
            .Select(NormalizeDisplayPrompt)
            .Any(IsAutoContinuePrompt);
    }

    private static bool IsAutoContinuePrompt(string? value)
    {
        return string.Equals(value, "TAP OK TO CONTINUE", StringComparison.Ordinal);
    }

    private static bool IsCardTerminalWaitPrompt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains("SWIPE CARD", StringComparison.Ordinal) ||
            value.Contains("PRESENT CARD", StringComparison.Ordinal) ||
            value.Contains("INSERT CARD", StringComparison.Ordinal) ||
            value.Contains("TAP CARD", StringComparison.Ordinal) ||
            value.Contains("TAP OK TO CONTINUE", StringComparison.Ordinal) ||
            value.Contains("WAITING FOR CARD", StringComparison.Ordinal);
    }

    private static string? NormalizeDisplayPrompt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Join(' ', value.Trim().ToUpperInvariant().Split(
            [' ', '\r', '\n', '\t'],
            StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task<LinklyCloudBackendSessionResponse> StartTransactionAsync(
        LinklyCloudBackendTransactionRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        const string relativeUrl = "api/v1/linkly/cloud-backend/transactions";
        LogHttpRequest(
            "start transaction",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            request.TxnType,
            txnRef: null,
            bodyJson: SerializeDebugJson(request));
        using var response = await SendWithFailureLogAsync(
            "start transaction",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            txnType: request.TxnType,
            stopwatch,
            () => httpClient.PostAsJsonAsync(relativeUrl, request, JsonOptions, cancellationToken),
            cancellationToken);
        var status = await ReadApiResultAsync(
            response,
            "start transaction",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            request.TxnType,
            stopwatch,
            cancellationToken);
        return status;
    }

    private async Task<LinklyCloudBackendSessionResponse> StartSettlementAsync(
        LinklyCloudBackendSettlementRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        const string relativeUrl = "api/v1/linkly/cloud-backend/settlements";
        LogHttpRequest(
            "start settlement",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            txnType: "S",
            txnRef: null,
            bodyJson: SerializeDebugJson(request));
        using var response = await SendWithFailureLogAsync(
            "start settlement",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            txnType: "S",
            stopwatch,
            () => httpClient.PostAsJsonAsync(relativeUrl, request, JsonOptions, cancellationToken),
            cancellationToken);
        return await ReadApiResultAsync(
            response,
            "start settlement",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            txnType: "S",
            stopwatch,
            cancellationToken);
    }

    private async Task<LinklyCloudBackendSessionResponse?> GetActiveSessionAsync(
        CardTerminalSettings settings,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var relativeUrl = $"api/v1/linkly/cloud-backend/transactions/active?environment={Uri.EscapeDataString(settings.Environment.ToString())}";
        LogHttpRequest(
            "active session",
            HttpMethod.Get,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            txnRef: null,
            bodyJson: null);
        using var response = await SendWithFailureLogAsync(
            "active session",
            HttpMethod.Get,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            stopwatch,
            () => httpClient.GetAsync(relativeUrl, cancellationToken),
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();
            LogHttpResponse(
                "active session",
                HttpMethod.Get,
                FormatRequestUrl(relativeUrl),
                response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                txnType: null,
                txnRef: null,
                body,
                // 404 表示当前没有活动/可恢复会话，属于正常结果，不能按失败告警。
                level: LinklyLogLevel.Information);
            return null;
        }

        var status = await ReadApiResultAsync(
            response,
            "active session",
            HttpMethod.Get,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            stopwatch,
            cancellationToken);
        return status;
    }

    private async Task<LinklyCloudBackendSessionResponse?> GetActiveSessionWithHttpTimeoutAsync(
        CardTerminalSettings settings,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(LinklyTimeoutPolicy.HttpTimeout);
        return await GetActiveSessionAsync(settings, timeoutCts.Token);
    }

    private async Task<LinklyCloudBackendSessionResponse?> GetResumableSessionWithHttpTimeoutAsync(
        CardTerminalSettings settings,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(LinklyTimeoutPolicy.HttpTimeout);
        return await GetResumableSessionCoreAsync(settings, timeoutCts.Token);
    }

    private async Task<LinklyCloudBackendSessionResponse?> GetResumableSessionCoreAsync(
        CardTerminalSettings settings,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var relativeUrl = $"api/v1/linkly/cloud-backend/transactions/resumable?environment={Uri.EscapeDataString(settings.Environment.ToString())}";
        LogHttpRequest(
            "resumable session",
            HttpMethod.Get,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            txnRef: null,
            bodyJson: null);
        using var response = await SendWithFailureLogAsync(
            "resumable session",
            HttpMethod.Get,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            stopwatch,
            () => httpClient.GetAsync(relativeUrl, cancellationToken),
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();
            LogHttpResponse(
                "resumable session",
                HttpMethod.Get,
                FormatRequestUrl(relativeUrl),
                response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                txnType: null,
                txnRef: null,
                body,
                // 404 表示当前没有活动/可恢复会话，属于正常结果，不能按失败告警。
                level: LinklyLogLevel.Information);
            return null;
        }

        var status = await ReadApiResultAsync(
            response,
            "resumable session",
            HttpMethod.Get,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            stopwatch,
            cancellationToken);
        return status;
    }

    private async Task<LinklyCloudBackendSessionResponse?> GetResumableSettlementCoreAsync(
        CardTerminalSettings settings,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var relativeUrl = $"api/v1/linkly/cloud-backend/settlements/resumable?environment={Uri.EscapeDataString(settings.Environment.ToString())}";
        LogHttpRequest(
            "resumable settlement",
            HttpMethod.Get,
            FormatRequestUrl(relativeUrl),
            txnType: "S",
            txnRef: null,
            bodyJson: null);
        using var response = await SendWithFailureLogAsync(
            "resumable settlement",
            HttpMethod.Get,
            FormatRequestUrl(relativeUrl),
            txnType: "S",
            stopwatch,
            () => httpClient.GetAsync(relativeUrl, cancellationToken),
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();
            LogHttpResponse(
                "resumable settlement",
                HttpMethod.Get,
                FormatRequestUrl(relativeUrl),
                response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                txnType: "S",
                txnRef: null,
                body,
                // 404 表示当前没有活动/可恢复会话，属于正常结果，不能按失败告警。
                level: LinklyLogLevel.Information);
            return null;
        }

        return await ReadApiResultAsync(
            response,
            "resumable settlement",
            HttpMethod.Get,
            FormatRequestUrl(relativeUrl),
            txnType: "S",
            stopwatch,
            cancellationToken);
    }

    private async Task<LinklyCloudBackendSessionResponse> GetStatusAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var relativeUrl = $"api/v1/linkly/cloud-backend/transactions/{Uri.EscapeDataString(sessionId)}/status?environment={Uri.EscapeDataString(settings.Environment.ToString())}";
        LogHttpRequest(
            "status",
            HttpMethod.Get,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            txnRef: null,
            bodyJson: null);
        using var response = await SendWithFailureLogAsync(
            "status",
            HttpMethod.Get,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            stopwatch,
            () => httpClient.GetAsync(relativeUrl, cancellationToken),
            cancellationToken);
        var status = await ReadApiResultAsync(
            response,
            "status",
            HttpMethod.Get,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            stopwatch,
            cancellationToken);
        return status;
    }

    private async Task<LinklyCloudBackendSessionResponse> GetSettlementStatusAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var relativeUrl = $"api/v1/linkly/cloud-backend/settlements/{Uri.EscapeDataString(sessionId)}/status?environment={Uri.EscapeDataString(settings.Environment.ToString())}";
        LogHttpRequest(
            "settlement status",
            HttpMethod.Get,
            FormatRequestUrl(relativeUrl),
            txnType: "S",
            txnRef: null,
            bodyJson: null);
        using var response = await SendWithFailureLogAsync(
            "settlement status",
            HttpMethod.Get,
            FormatRequestUrl(relativeUrl),
            txnType: "S",
            stopwatch,
            () => httpClient.GetAsync(relativeUrl, cancellationToken),
            cancellationToken);
        return await ReadApiResultAsync(
            response,
            "settlement status",
            HttpMethod.Get,
            FormatRequestUrl(relativeUrl),
            txnType: "S",
            stopwatch,
            cancellationToken);
    }

    private async Task PostSettlementSessionMarkerAsync(
        CardTerminalSettings settings,
        string sessionId,
        string operation,
        object request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var relativeUrl = $"api/v1/linkly/cloud-backend/settlements/{Uri.EscapeDataString(sessionId)}/{operation}";
        LogHttpRequest(
            $"settlement {operation}",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            txnType: "S",
            txnRef: null,
            bodyJson: SerializeDebugJson(request));
        using var response = await SendWithFailureLogAsync(
            $"settlement {operation}",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            txnType: "S",
            stopwatch,
            () => httpClient.PostAsJsonAsync(relativeUrl, request, JsonOptions, cancellationToken),
            cancellationToken);
        _ = await ReadApiResultAsync(
            response,
            $"settlement {operation}",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            txnType: "S",
            stopwatch,
            cancellationToken);
    }

    private async Task<LinklyCloudBackendSessionResponse> RecoverAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var relativeUrl = $"api/v1/linkly/cloud-backend/transactions/{Uri.EscapeDataString(sessionId)}/recover";
        var request = new LinklyCloudBackendRecoverRequest(settings.Environment.ToString());
        LogHttpRequest(
            "recover",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            txnRef: null,
            bodyJson: SerializeDebugJson(request));
        using var response = await SendWithFailureLogAsync(
            "recover",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            stopwatch,
            () => httpClient.PostAsJsonAsync(relativeUrl, request, JsonOptions, cancellationToken),
            cancellationToken);
        var status = await ReadApiResultAsync(
            response,
            "recover",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            stopwatch,
            cancellationToken);
        return status;
    }

    private async Task<LinklyCloudBackendSessionResponse> SendKeyAsync(
        CardTerminalSettings settings,
        string sessionId,
        LinklyTerminalDialogAction action,
        CancellationToken cancellationToken)
    {
        var normalizedKey = LinklyTerminalDialogKeys.Normalize(action.Key);
        var stopwatch = Stopwatch.StartNew();
        var relativeUrl = $"api/v1/linkly/cloud-backend/transactions/{Uri.EscapeDataString(sessionId)}/sendkey";
        var request = new LinklyCloudBackendSendKeyRequest(
            settings.Environment.ToString(),
            normalizedKey,
            NormalizeOptional(action.Data));
        LogHttpRequest(
            "sendkey",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            txnRef: null,
            bodyJson: SerializeDebugJson(request));
        using var response = await SendWithFailureLogAsync(
            "sendkey",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            stopwatch,
            () => httpClient.PostAsJsonAsync(relativeUrl, request, JsonOptions, cancellationToken),
            cancellationToken);
        var status = await ReadApiResultAsync(
            response,
            "sendkey",
            HttpMethod.Post,
            FormatRequestUrl(relativeUrl),
            txnType: null,
            stopwatch,
            cancellationToken);
        return status;
    }

    private async Task<string?> TryResolveOriginalBackendRefundReferenceAsync(
        CardTerminalSettings settings,
        string? originalReference,
        CancellationToken cancellationToken)
    {
        if (!LinklyBackendPaymentReference.TryGetPrintMarker(originalReference, out var environment, out var sessionId) ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            Log($"refund reference recovery skipped reason=no-backend-session originalReference={LogValue(originalReference)}");
            return null;
        }

        if (!string.Equals(environment, settings.Environment.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            Log(
                $"refund reference recovery skipped reason=environment-mismatch referenceEnvironment={LogValue(environment)} " +
                $"settingsEnvironment={settings.Environment}");
            return null;
        }

        try
        {
            var status = await GetStatusAsync(settings, sessionId, cancellationToken);
            var refundReference = TryReadRefundReference(status, originalReference) ??
                TryReadOriginalTxnRef(originalReference);
            Log(
                $"refund reference recovery completed sessionId={sessionId} status={status.Status} " +
                $"notifications={status.Notifications?.Count ?? 0} refundReference={LogValue(refundReference)} " +
                $"transactionPayloads={BuildRefundReferenceRecoverySnapshot(status)}");
            return refundReference;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"refund reference recovery failed sessionId={sessionId} error={ex.GetType().Name}");
            return null;
        }
    }

    private (Guid? TerminalId, long? SelectionRevision) GetCachedTerminalSelection(
        CardTerminalEnvironment environment)
    {
        lock (_terminalDirectorySync)
        {
            if (_terminalDirectories.TryGetValue(environment, out var directory) &&
                string.Equals(directory.Mode, "Active", StringComparison.OrdinalIgnoreCase) &&
                directory.SelectedTerminalId is Guid terminalId &&
                directory.SelectionRevision is long selectionRevision)
            {
                return (terminalId, selectionRevision);
            }
        }

        return (null, null);
    }

    private async Task<TerminalScopedHttpResult> SendTerminalScopedRequestAsync(
        string operation,
        HttpMethod method,
        string relativePath,
        CardTerminalEnvironment environment,
        Func<string, string?>? txnRefSelector,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var relativeUrl = BuildTerminalScopedRelativeUrl(relativePath, environment);
            var url = FormatRequestUrl(relativeUrl);
            var stopwatch = Stopwatch.StartNew();
            LogHttpRequest(
                operation,
                method,
                url,
                txnType: null,
                txnRef: null,
                bodyJson: null);
            using var request = new HttpRequestMessage(method, relativeUrl);
            using var response = await SendWithFailureLogAsync(
                operation,
                method,
                url,
                txnType: null,
                stopwatch,
                () => httpClient.SendAsync(request, cancellationToken),
                cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();
            LogHttpResponse(
                operation,
                method,
                url,
                response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                txnType: null,
                txnRef: txnRefSelector?.Invoke(content),
                bodyJson: content);

            if (attempt == 0 && IsTerminalSelectionConflict(response.StatusCode, content))
            {
                // Fresh app 或后台改选后，先刷新权威目录再用新 terminal/revision 重试一次；首个 409 未触达 Linkly。
                await GetTerminalsAsync(environment, cancellationToken);
                continue;
            }

            return new TerminalScopedHttpResult(response.StatusCode, content);
        }

        throw new InvalidOperationException("Linkly terminal-scoped request retry did not return a response.");
    }

    private string BuildTerminalScopedRelativeUrl(
        string relativePath,
        CardTerminalEnvironment environment)
    {
        var relativeUrl = $"{relativePath}?environment={Uri.EscapeDataString(environment.ToString())}";
        var selection = GetCachedTerminalSelection(environment);
        return selection.TerminalId is Guid terminalId && selection.SelectionRevision is long revision
            ? $"{relativeUrl}&terminalId={terminalId:D}&selectionRevision={revision.ToString(CultureInfo.InvariantCulture)}"
            : relativeUrl;
    }

    private static bool IsTerminalSelectionConflict(HttpStatusCode statusCode, string content)
    {
        if (statusCode != HttpStatusCode.Conflict || string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.TryGetProperty("errorCode", out var errorCode) &&
                string.Equals(
                    errorCode.GetString(),
                    CloudBackendTerminalSelectionConflictErrorCode,
                    StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record TerminalScopedHttpResult(HttpStatusCode StatusCode, string Content)
    {
        public bool IsSuccessStatusCode => (int)StatusCode is >= 200 and <= 299;
    }

    private async Task<T> ReadTerminalApiResultAsync<T>(
        HttpResponseMessage response,
        string operation,
        HttpMethod method,
        string url,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        ApiResult<T>? result = null;
        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                result = JsonSerializer.Deserialize<ApiResult<T>>(content, JsonOptions);
            }
            catch (JsonException) when (!response.IsSuccessStatusCode)
            {
                result = null;
            }
            catch (JsonException ex)
            {
                stopwatch.Stop();
                LogHttpFailure(operation, method, url, txnType: null, stopwatch.ElapsedMilliseconds, ex, cancellationToken, response.StatusCode);
                throw;
            }
        }

        stopwatch.Stop();
        if (!response.IsSuccessStatusCode || result?.Success != true || result.Data is null)
        {
            // 终端管理（目录/选择/配对/测试/分配）失败只记状态码与服务端 errorCode；不记正文，避免带出配对等敏感字段。
            LinklyJsonLog.Write(
                "LinklyBackend",
                "backend-terminal",
                operation,
                "failed",
                direction: "response",
                httpStatus: response.StatusCode,
                success: false,
                reason: result?.ErrorCode ?? (response.IsSuccessStatusCode ? "failure-response" : "http-error"),
                elapsedMs: stopwatch.ElapsedMilliseconds,
                details: new
                {
                    method = method.Method,
                    url,
                    errorCode = result?.ErrorCode
                });
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new LinklyBackendHttpException(
                ReadSetupFailureMessage(response.StatusCode, content) ?? result?.Message ?? $"Linkly backend request failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode,
                result?.ErrorCode);
        }

        if (result?.Success != true || result.Data is null)
        {
            throw new LinklyBackendHttpException(
                ReadSetupFailureMessage(response.StatusCode, content) ?? result?.Message ?? "Linkly backend returned a failure response.",
                response.StatusCode,
                result?.ErrorCode);
        }

        return result.Data;
    }

    private string? ReadSetupFailureMessage(HttpStatusCode statusCode, string content)
    {
        string? errorCode = null;
        try
        {
            errorCode = JsonSerializer.Deserialize<ApiResult<JsonElement>>(content, JsonOptions)?.ErrorCode;
        }
        catch (JsonException)
        {
            // 非 JSON 网关响应仍按 HTTP 状态提供操作建议，不能误判为配对码错误。
        }

        // 仅解释设置操作的明确错误码；不改变交易重试、选择版本及终端占用保护。
        var key = errorCode switch
        {
            "LINKLY_CLOUD_BACKEND_ACTIVE_TRANSACTION" or "LINKLY_CLOUD_TERMINAL_SESSION_ACTIVE" => "operationBlocked",
            "LINKLY_CLOUD_BACKEND_PAIR_IN_PROGRESS" or "LINKLY_CLOUD_TERMINAL_PAIRING_CONFLICT" => "pairInProgress",
            "LINKLY_CLOUD_BACKEND_PAIR_REJECTED" => "pairRejected",
            "LINKLY_CLOUD_BACKEND_PAIR_REQUEST_INVALID" => "pairRequestInvalid",
            "LINKLY_CLOUD_BACKEND_PAIR_CREDENTIAL_MISSING" or "LINKLY_CLOUD_CREDENTIAL_NOT_CONFIGURED" or
                "LINKLY_CLOUD_TERMINAL_CREDENTIAL_REENTRY_REQUIRED" or "LINKLY_CLOUD_TERMINAL_CREDENTIAL_UNAVAILABLE" => "credentialsRequired",
            "LINKLY_CLOUD_BACKEND_PAIR_TIMEOUT" or "LINKLY_CLOUD_BACKEND_PAIR_UPSTREAM_FAILED" or
                "LINKLY_CLOUD_BACKEND_PAIR_PERSISTENCE_FAILED" => "pairUnconfirmed",
            "LINKLY_CLOUD_TERMINAL_NOT_READY" => "pairBeforeSelect",
            "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT" => "selectionChanged",
            "LINKLY_CLOUD_TERMINAL_ASSIGNED" => "assignedElsewhere",
            "LINKLY_CLOUD_TERMINAL_NOT_FOUND" => "terminalUnavailable",
            _ => statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "permissionRequired" : null
        };
        return key is null ? null : T($"settings.linkly.cloudBackend.{key}", string.Empty);
    }

    private static async Task<LinklyCloudBackendSessionResponse> ReadApiResultAsync(
        HttpResponseMessage response,
        string operation,
        HttpMethod method,
        string url,
        string? txnType,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        ApiResult<LinklyCloudBackendSessionResponse>? result = null;
        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                result = JsonSerializer.Deserialize<ApiResult<LinklyCloudBackendSessionResponse>>(content, JsonOptions);
            }
            catch (JsonException) when (!response.IsSuccessStatusCode)
            {
                result = null;
            }
            catch (JsonException ex)
            {
                // 2xx 但正文不是合法 JSON：先记下状态码与耗时再抛出，否则这次响应在日志里完全消失。
                stopwatch.Stop();
                LogHttpFailure(
                    operation,
                    method,
                    url,
                    txnType,
                    stopwatch.ElapsedMilliseconds,
                    ex,
                    cancellationToken,
                    response.StatusCode);
                throw;
            }
        }
        stopwatch.Stop();
        LogHttpResponse(
            operation,
            method,
            url,
            response.StatusCode,
            stopwatch.ElapsedMilliseconds,
            txnType,
            result?.Data?.TxnRef,
            content);

        if (!response.IsSuccessStatusCode)
        {
            throw new LinklyBackendHttpException(
                result?.Message ?? $"Linkly backend request failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode,
                result?.ErrorCode);
        }

        if (result?.Success != true || result.Data is null)
        {
            throw new LinklyBackendHttpException(
                result?.Message ?? "Linkly backend returned a failure response.",
                response.StatusCode,
                result?.ErrorCode);
        }

        if (string.IsNullOrWhiteSpace(result.Data.SessionId))
        {
            throw new JsonException("Linkly backend response is missing session id.");
        }

        LogStatusSnapshot($"{operation} http response elapsedMs={stopwatch.ElapsedMilliseconds} http={(int)response.StatusCode}", result.Data);
        return result.Data;
    }

    private static string? TryReadApiMessage(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            var result = JsonSerializer.Deserialize<ApiResult<LinklyCloudBackendSessionResponse>>(content, JsonOptions);
            return NormalizeOptional(result?.Message);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static LinklyCloudBackendStatusTestResponse ReadStatusTestResult(string content)
    {
        var result = JsonSerializer.Deserialize<ApiResult<LinklyCloudBackendStatusTestResponse>>(content, JsonOptions);
        if (result?.Success != true || result.Data is null)
        {
            throw new LinklyBackendHttpException(
                result?.Message ?? "Linkly backend returned a failure response.",
                HttpStatusCode.OK);
        }

        return result.Data;
    }

    private static LinklyCloudBackendLogonTestResponse ReadLogonTestResult(string content)
    {
        var result = JsonSerializer.Deserialize<ApiResult<LinklyCloudBackendLogonTestResponse>>(content, JsonOptions);
        if (result?.Success != true || result.Data is null)
        {
            throw new LinklyBackendHttpException(
                result?.Message ?? "Linkly backend returned a failure response.",
                HttpStatusCode.OK);
        }

        return result.Data;
    }

    private static string? TryReadLogonTestMessage(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            var result = JsonSerializer.Deserialize<ApiResult<LinklyCloudBackendLogonTestResponse>>(content, JsonOptions);
            return NormalizeOptional(result?.Message ?? result?.Data?.Message);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryReadStatusTestMessage(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            var result = JsonSerializer.Deserialize<ApiResult<LinklyCloudBackendStatusTestResponse>>(content, JsonOptions);
            return NormalizeOptional(result?.Message ?? result?.Data?.Message);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadStatusTestTxnRef(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            var result = JsonSerializer.Deserialize<ApiResult<LinklyCloudBackendStatusTestResponse>>(content, JsonOptions);
            return NormalizeOptional(result?.Data?.ResponseTxnRef);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static LinklyCloudBackendHealthResponse ReadHealthResult(string content)
    {
        var result = JsonSerializer.Deserialize<ApiResult<LinklyCloudBackendHealthResponse>>(content, JsonOptions);
        if (result?.Success != true || result.Data is null)
        {
            throw new JsonException("Linkly backend health response is invalid.");
        }

        return result.Data;
    }

    private string FormatHealthFailure(LinklyCloudBackendHealthResponse health)
    {
        var failedMessages = GetFailedHealthChecks(health)
            .Select(check => NormalizeOptional(check.Message))
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return failedMessages.Length == 0
            ? T("linkly.backend.configIncomplete", "ANZ Linkly Cloud backend configuration is incomplete.")
            : string.Join(Environment.NewLine, failedMessages!);
    }

    private static IReadOnlyList<LinklyCloudBackendHealthCheckDto> GetFailedHealthChecks(
        LinklyCloudBackendHealthResponse health)
    {
        return (health.Checks ?? [])
            .Where(check => !check.IsReady)
            .ToArray();
    }

    private PaymentAuthorizationResult ToAuthorizationResult(
        LinklyCloudBackendSessionResponse status,
        decimal requestedAmount,
        string requestedTxnRef,
        bool suppressPrintedReceipt,
        bool manualCancelRequested = false)
    {
        if (string.Equals(status.Status, StatusNotSubmitted, StringComparison.OrdinalIgnoreCase))
        {
            return new PaymentAuthorizationResult(false, null, T("linkly.backend.notSubmitted", "Linkly Cloud transaction was not submitted. Retry the payment."));
        }

        var transactionResult = ReadTransactionResult(status, requestedAmount, requestedTxnRef);
        var amount = transactionResult.Amount;
        var receiptText = ReadReceiptText(status, suppressPrintedReceipt);
        var transaction = ToCardTransaction(transactionResult, amount, receiptText);
        var approved = IsCompletedOrPendingSuccess(status) &&
            transactionResult.Succeeded;
        if (approved && !IsTransactionResultVerified(status, transactionResult, requestedAmount))
        {
            return new PaymentAuthorizationResult(false, null, "ANZ Linkly Cloud result could not be verified.", ResultUnknown: true);
        }
        var reference = LinklyBackendPaymentReference.Format(
            transaction.TxnRef ?? transactionResult.SessionId,
            transactionResult.SessionId,
            status.Environment,
            transactionResult.RefundReference);

        var failureMessage = manualCancelRequested
            ? T("linkly.backend.cancelled", "ANZ Linkly Cloud transaction was cancelled.")
            : FormatResponseMessage(transactionResult.ResponseText, transactionResult.ResponseCode);

        return approved
            ? new PaymentAuthorizationResult(
                true,
                reference,
                "ANZ Linkly Cloud",
                amount,
                [transaction],
                ProcessorName,
                status.Environment,
                LinklyConnectionMode.CloudBackendAsync.ToString(),
                null,
                status.SessionId,
                transaction.TxnRef,
                transaction.ResponseCode,
                transaction.ResponseText)
            : new PaymentAuthorizationResult(
                false,
                reference,
                failureMessage,
                amount,
                [transaction],
                ProcessorName,
                status.Environment,
                LinklyConnectionMode.CloudBackendAsync.ToString(),
                null,
                status.SessionId,
                transaction.TxnRef,
                transaction.ResponseCode,
                transaction.ResponseText,
                StatusKey: manualCancelRequested ? "linkly.backend.cancelled" : null);
    }

    private sealed record LinklyBackendPollResult(
        LinklyCloudBackendSessionResponse Status,
        bool ManualCancelRequested,
        bool SignatureDeclineRequested);

    private sealed class LinklySignatureSlipPrintState
    {
        public HashSet<string> PrintedKeys { get; } = new(StringComparer.Ordinal);

        public string? LastFailureMessage { get; set; }
    }

    internal static bool IsTransactionResultVerified(
        LinklyCloudBackendSessionResponse status,
        LinklyCloudTransactionResult result,
        decimal requestedAmount)
    {
        // 实时收款与自动恢复共用证据校验，绝不能用请求/草稿金额补造终端批准金额。
        var persistedTxnRef = NormalizeOptional(status.TxnRef);
        var terminalTxnRef = NormalizeOptional(result.TxnRef);
        if (string.IsNullOrWhiteSpace(terminalTxnRef) ||
            (persistedTxnRef is not null && !string.Equals(persistedTxnRef, terminalTxnRef, StringComparison.Ordinal)) ||
            result.Amount is not decimal amount || amount < 0m ||
            decimal.Round(amount, 2, MidpointRounding.AwayFromZero) !=
            decimal.Round(requestedAmount, 2, MidpointRounding.AwayFromZero))
        {
            return false;
        }

        // 安全明细也不能覆盖会话已保护的结果，避免混用不同终态的金额与批准码。
        var card = status.CardTransaction;
        return card is null ||
            ((NormalizeOptional(result.ResponseCode) is not string resultCode || LinklyApprovalResponseCodes.IsApproved(resultCode)) &&
             (NormalizeOptional(status.ResponseCode) is not string code ||
              string.Equals(code, NormalizeOptional(card.ResponseCode), StringComparison.OrdinalIgnoreCase)) &&
             (NormalizeOptional(status.ResponseText) is not string text ||
              string.Equals(text, NormalizeOptional(card.ResponseText), StringComparison.OrdinalIgnoreCase)));
    }

    internal static CardTransactionDto ToCardTransaction(
        LinklyCloudTransactionResult response,
        decimal? amount,
        string? receiptText)
    {
        return new CardTransactionDto(
            ProcessorName,
            NormalizeOptional(response.TxnRef) ?? response.SessionId,
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
            NormalizeOptional(receiptText),
            NormalizeOptional(response.RefundReference));
    }

    internal static LinklyCloudTransactionResult ReadTransactionResult(
        LinklyCloudBackendSessionResponse status,
        decimal requestedAmount,
        string requestedTxnRef)
    {
        var protectedResponseCode = NormalizeOptional(status.ResponseCode);
        var protectedResponseText = NormalizeOptional(status.ResponseText);
        if (status.CardTransaction is { } card)
        {
            // 状态接口已提供白名单化的权威明细；不再依赖原始通知的嵌套格式，也不回填请求金额。
            var responseCode = protectedResponseCode ?? NormalizeOptional(card.ResponseCode);
            return new LinklyCloudTransactionResult(
                status.SessionId,
                IsSuccessfulTransaction(status.TransactionSuccess, responseCode, notificationSuccess: null),
                NormalizeOptional(card.TxnRef) ?? NormalizeOptional(status.TxnRef),
                NormalizeOptional(card.AuthCode),
                NormalizeOptional(card.CardType),
                null,
                NormalizeOptional(card.MaskedCardNumber),
                NormalizeOptional(card.MerchantId),
                responseCode,
                protectedResponseText ?? NormalizeOptional(card.ResponseText),
                NormalizeOptional(card.Stan),
                card.AmountCents is long cents ? cents / 100m : null,
                NormalizeOptional(card.Rfn) ?? TryReadRefundReference(status, null));
        }

        var notifications = status.Notifications ?? [];
        var fallbackRefundReference = TryReadRefundReference(status, null);
        var transactionNotification = string.IsNullOrWhiteSpace(protectedResponseCode)
            ? notifications.LastOrDefault(IsTransactionNotification)
            : notifications.LastOrDefault(notification =>
                IsTransactionNotification(notification) &&
                TransactionNotificationMatchesProtectedResult(notification, protectedResponseCode, protectedResponseText));
        if (transactionNotification is null || string.IsNullOrWhiteSpace(transactionNotification.PayloadJson))
        {
            return new LinklyCloudTransactionResult(
                status.SessionId,
                IsSuccessfulTransaction(status.TransactionSuccess, protectedResponseCode, notificationSuccess: null),
                NormalizeOptional(status.TxnRef),
                null,
                null,
                null,
                null,
                null,
                protectedResponseCode,
                protectedResponseText,
                null,
                null,
                fallbackRefundReference);
        }

        using var document = JsonDocument.Parse(transactionNotification.PayloadJson);
        var response = ReadResponse(document.RootElement);
        var purchaseAnalysisData = ReadObject(response, "PurchaseAnalysisData");
        var notificationRefundReference = TryReadRefundReference(document.RootElement, out _);
        var responseCodeFromNotification = ReadString(response, "ResponseCode");
        var responseTextFromNotification = ReadString(response, "ResponseText");
        var finalResponseCode = protectedResponseCode ?? responseCodeFromNotification;
        var finalResponseText = protectedResponseText ?? responseTextFromNotification;
        var notificationSuccess = ReadBool(response, "Success");
        return new LinklyCloudTransactionResult(
            status.SessionId,
            IsSuccessfulTransaction(status.TransactionSuccess, finalResponseCode, notificationSuccess),
            ReadString(response, "TxnRef") ?? NormalizeOptional(status.TxnRef),
            ReadString(response, "AuthCode"),
            ReadString(response, "CardType"),
            ReadString(response, "CardName"),
            ReadString(response, "Pan"),
            ReadString(response, "Caid"),
            finalResponseCode,
            finalResponseText,
            ReadString(response, "Stan"),
            ReadDecimal(response, "AmtPurchase"),
            ReadString(purchaseAnalysisData, "RFN") ?? notificationRefundReference ?? fallbackRefundReference);
    }

    private static bool IsSuccessfulTransaction(
        bool? transactionSuccess,
        string? responseCode,
        bool? notificationSuccess)
    {
        if (transactionSuccess == false)
        {
            return false;
        }

        if (LinklyApprovalResponseCodes.IsApproved(responseCode))
        {
            return true;
        }

        // 老数据或短暂不完整的 backend 状态可能没有 TransactionSuccess；保留 notification Success 兼容路径。
        return transactionSuccess == true || notificationSuccess == true;
    }

    internal static bool IsApprovedFinalTransaction(LinklyCloudBackendSessionResponse status)
    {
        if (!IsCompletedOrPendingSuccess(status))
        {
            return false;
        }

        // 统一沿用 backend 的批准证据分类：受保护响应码优先，并兼容已验证的交易通知。
        return IsSuccessfulTransaction(status.TransactionSuccess, status.ResponseCode, notificationSuccess: null) ||
            HasVerifiedApprovedTransactionNotification(status);
    }

    private static LinklyReceiptApproval? TryReadReceiptApproval(LinklyCloudBackendSessionResponse status)
    {
        if (!string.Equals(status.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var receiptText = ReadReceiptText(status);
        if (string.IsNullOrWhiteSpace(receiptText))
        {
            return null;
        }

        foreach (var rawLine in receiptText.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            var separatorIndex = rawLine.LastIndexOf('-');
            if (separatorIndex <= 0 || separatorIndex == rawLine.Length - 1)
            {
                continue;
            }

            var responseText = NormalizeOptional(rawLine[..separatorIndex]);
            var responseCode = NormalizeOptional(rawLine[(separatorIndex + 1)..]);
            // 只有 receipt 明确给出 Linkly 批准码时才作为 fallback，避免单纯 Completed 被误判成功。
            if (string.IsNullOrWhiteSpace(responseText) ||
                !LinklyApprovalResponseCodes.IsApproved(responseCode))
            {
                continue;
            }

            return new LinklyReceiptApproval(responseCode!, responseText);
        }

        return null;
    }

    private sealed record LinklyReceiptApproval(string ResponseCode, string ResponseText);

    private static bool IsUnresolvedSignatureReceiptStatus(LinklyCloudBackendSessionResponse status)
    {
        if (!string.Equals(status.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase) ||
            !IsSignatureReceiptPrompt(status))
        {
            return false;
        }

        if (status.TransactionSuccess.HasValue ||
            !string.IsNullOrWhiteSpace(NormalizeOptional(status.ResponseCode)))
        {
            return false;
        }

        return !(status.Notifications ?? []).Any(IsTransactionNotification);
    }

    private static bool IsSignatureReceiptPrompt(LinklyCloudBackendSessionResponse status)
    {
        var displayText = NormalizeOptional(status.DisplayText);
        var receiptText = ReadReceiptText(status);
        return !string.IsNullOrWhiteSpace(receiptText) &&
            receiptText.Contains("PLEASE SIGN", StringComparison.OrdinalIgnoreCase) &&
            receiptText.Contains("APPROVE WITH SIG", StringComparison.OrdinalIgnoreCase) &&
            (
                string.Equals(displayText, "SIGNATURE OK?", StringComparison.OrdinalIgnoreCase) ||
                status.AcceptYesKeyFlag ||
                status.AuthoriseKeyFlag ||
                (status.Notifications ?? []).Any(NotificationHasSignaturePromptFlag));
    }

    private static bool NotificationHasSignaturePromptFlag(LinklyCloudBackendNotificationDto notification)
    {
        if (!string.Equals(notification.Type, "display", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(notification.PayloadJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(notification.PayloadJson);
            var response = ReadResponse(document.RootElement);
            return notification.PayloadJson.Contains("SIGNATURE OK?", StringComparison.OrdinalIgnoreCase) ||
                ReadFlag(response, "AcceptYesKeyFlag") ||
                ReadFlag(response, "AuthoriseKeyFlag");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? TryReadRefundReference(
        LinklyCloudBackendSessionResponse status,
        string? fallbackReference)
    {
        var backendReference = LinklyBackendPaymentReference.TryGetRefundReference(fallbackReference);
        if (!string.IsNullOrWhiteSpace(backendReference))
        {
            return backendReference;
        }

        foreach (var notification in (status.Notifications ?? []).Where(IsTransactionNotification).Reverse())
        {
            if (string.IsNullOrWhiteSpace(notification.PayloadJson))
            {
                continue;
            }

            using var document = JsonDocument.Parse(notification.PayloadJson);
            var refundReference = TryReadRefundReference(document.RootElement, out _);
            if (!string.IsNullOrWhiteSpace(refundReference))
            {
                return refundReference;
            }
        }

        return null;
    }

    private static string BuildRefundReferenceRecoverySnapshot(LinklyCloudBackendSessionResponse status)
    {
        var transactionNotifications = (status.Notifications ?? [])
            .Where(IsTransactionNotification)
            .TakeLast(5)
            .ToArray();
        if (transactionNotifications.Length == 0)
        {
            return "<none>";
        }

        var parts = transactionNotifications.Select((notification, index) =>
        {
            if (string.IsNullOrWhiteSpace(notification.PayloadJson))
            {
                return $"#{index + 1}:empty";
            }

            try
            {
                using var document = JsonDocument.Parse(notification.PayloadJson);
                var root = document.RootElement;
                var response = ReadResponse(root);
                var purchaseAnalysisData = ReadValue(response, "PurchaseAnalysisData");
                var refundReference = TryReadRefundReference(root, out var source);
                return $"#{index + 1}:bytes={notification.PayloadJson.Length},rootKeys={DescribeKeys(root)},responseKeys={DescribeKeys(response)}," +
                    $"padKind={DescribeKind(purchaseAnalysisData)},padKeys={DescribeKeys(purchaseAnalysisData)},rfnSource={LogValue(source)},rfn={LogValue(refundReference)}";
            }
            catch (JsonException ex)
            {
                return $"#{index + 1}:invalid-json:{ex.GetType().Name}";
            }
        });

        return string.Join(" | ", parts);
    }

    private static string? TryReadRefundReference(JsonElement root, out string? source)
    {
        var response = ReadResponse(root);
        var purchaseAnalysisData = ReadValue(response, "PurchaseAnalysisData");
        // Linkly backend 闂?PAD 闂傚倷绶氬鑽ゆ嫻閻旂厧绀夐悗锝庡墰缁犳柨顭块懜闈涘閻熸瑱濡囬埀顒€鍘滈崑鎾绘煕閺囨ê濡煎ù婊呭亾閹便劌螣婢剁鎯堥梺鍛婄憿閸嬫捇姊婚崒娆戝妽鐟滄澘鍟撮幊鐔碱敍濠靛嫪姹楅梺鍝勮閸庢煡宕甸崟顖涚厾闁诡厽甯掗崝婊呯箔閹达附鐓熼幖杈剧稻閺嗏晜銇勯姀鐙呰含闁圭鎳樺畷鐔碱敍濮ｇ鍎遍妴鎺戭潩閿濆懍澹曢梻浣芥〃缁讹繝宕ｉ崘銊ф殾闁圭増婢樼粈鍌炴煕韫囨挸鎮戞い鏂跨Ф缁辨捇宕掑▎鎴М闁诲孩鍑归崣鍐ㄧ暦瑜版帒惟鐟滃宕戦幘鏂ユ婵炲棙蓱閻ｇ厧顪冮妶鍡楃仯闁绘帪濡囩划娆愬緞鐏炴儳鐝伴悷婊冪箳缁牊寰勯幇顓涙嫼濡炪倖鍔楅崰搴㈢閹€鏀介柣鎰级椤ョ娀鏌涚€ｎ偅灏扮紒鍌涘浮椤㈡瑩鎸婃径宀€鐛柣搴＄畭閸庨亶骞婇幇顔句笉闁瑰瓨绻嶅〒濠氭煏閸繄澧遍柛銈嗙懄閵囧嫰寮埀顒€顫忔繝姘疅闁圭虎鍠楅弲鏌ユ煕閳╁啰鎲块柛?RFN闂?
        var refundReference = TryReadRefundReferenceValue(purchaseAnalysisData, "Response.PurchaseAnalysisData", out source);
        if (!string.IsNullOrWhiteSpace(refundReference))
        {
            return refundReference;
        }

        refundReference = TryReadRefundReferenceValue(response, "Response", out source);
        if (!string.IsNullOrWhiteSpace(refundReference))
        {
            return refundReference;
        }

        refundReference = TryReadRefundReferenceValue(root, "Root", out source);
        if (!string.IsNullOrWhiteSpace(refundReference))
        {
            return refundReference;
        }

        source = null;
        return null;
    }

    private static string? TryReadRefundReferenceValue(JsonElement element, string path, out string? source)
    {
        source = null;
        return element.ValueKind switch
        {
            JsonValueKind.Object => TryReadRefundReferenceObject(element, path, out source),
            JsonValueKind.Array => TryReadRefundReferenceArray(element, path, out source),
            JsonValueKind.String => TryReadRefundReferenceFromText(element.GetString(), path, out source),
            JsonValueKind.Number => null,
            JsonValueKind.True => null,
            JsonValueKind.False => null,
            _ => null
        };
    }

    private static string? TryReadRefundReferenceObject(JsonElement element, string path, out string? source)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, "RFN", StringComparison.OrdinalIgnoreCase))
            {
                var value = ReadScalar(property.Value);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    source = $"{path}.{property.Name}";
                    return value;
                }
            }
        }

        var key = ReadString(element, "Key") ??
            ReadString(element, "Name") ??
            ReadString(element, "Tag") ??
            ReadString(element, "Code");
        if (string.Equals(key, "RFN", StringComparison.OrdinalIgnoreCase))
        {
            var value = ReadString(element, "Value") ??
                ReadString(element, "Data") ??
                ReadString(element, "Text");
            if (!string.IsNullOrWhiteSpace(value))
            {
                source = $"{path}[{key}]";
                return value;
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            var value = TryReadRefundReferenceValue(property.Value, $"{path}.{property.Name}", out source);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        source = null;
        return null;
    }

    private static string? TryReadRefundReferenceArray(JsonElement element, string path, out string? source)
    {
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var value = TryReadRefundReferenceValue(item, $"{path}[{index}]", out source);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            index++;
        }

        source = null;
        return null;
    }

    private static string? TryReadRefundReferenceFromText(string? text, string path, out string? source)
    {
        var value = NormalizeOptional(text);
        if (value is null)
        {
            source = null;
            return null;
        }

        var marker = value.IndexOf("RFN", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            source = null;
            return null;
        }

        var start = marker + 3;
        while (start < value.Length && (char.IsWhiteSpace(value[start]) || value[start] is ':' or '=' or '-'))
        {
            start++;
        }

        var end = start;
        while (end < value.Length && !char.IsWhiteSpace(value[end]) && value[end] is not ',' and not ';' and not '|')
        {
            end++;
        }

        var refundReference = NormalizeOptional(value[start..end]);
        source = refundReference is null ? null : path;
        return refundReference;
    }

    private static JsonElement ReadValue(JsonElement root, string propertyName)
    {
        return TryGetProperty(root, propertyName, out var value) ? value : default;
    }

    private static string? ReadScalar(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => NormalizeOptional(value.GetString()),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static string DescribeKind(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Undefined ? "<missing>" : element.ValueKind.ToString();
    }

    private static string DescribeKeys(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return DescribeKind(element);
        }

        var keys = element.EnumerateObject()
            .Select(property => property.Name)
            .Take(12)
            .ToArray();
        return keys.Length == 0 ? "<empty>" : string.Join(",", keys);
    }

    private static bool IsTransactionNotification(LinklyCloudBackendNotificationDto notification)
    {
        return string.Equals(notification.Type, "transaction", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TransactionNotificationMatchesProtectedResult(
        LinklyCloudBackendNotificationDto notification,
        string protectedResponseCode,
        string? protectedResponseText)
    {
        if (string.IsNullOrWhiteSpace(notification.PayloadJson))
        {
            return false;
        }

        using var document = JsonDocument.Parse(notification.PayloadJson);
        var response = ReadResponse(document.RootElement);
        if (!string.Equals(ReadString(response, "ResponseCode"), protectedResponseCode, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var responseText = ReadString(response, "ResponseText");
        return string.IsNullOrWhiteSpace(protectedResponseText) ||
            string.IsNullOrWhiteSpace(responseText) ||
            string.Equals(responseText, protectedResponseText, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadReceiptText(LinklyCloudBackendSessionResponse status)
    {
        return ReadReceiptText(status, suppressPrintedReceipt: false);
    }

    private static string? ReadReceiptText(
        LinklyCloudBackendSessionResponse status,
        bool suppressPrintedReceipt)
    {
        if (suppressPrintedReceipt && status.ReceiptPrintedAt is not null)
        {
            return null;
        }

        return NormalizeOptional(status.ReceiptText) ?? ReadReceiptText(status.Notifications ?? []);
    }

    private static string? ReadReceiptText(IReadOnlyList<LinklyCloudBackendNotificationDto> notifications)
    {
        var receipts = notifications
            .Where(notification => string.Equals(notification.Type, "receipt", StringComparison.OrdinalIgnoreCase))
            .Select(notification => ReadReceiptNotification(notification.PayloadJson))
            .Where(receipt => !string.IsNullOrWhiteSpace(receipt))
            .Select(receipt => receipt!)
            .ToArray();
        return receipts.Length == 0 ? null : string.Join(Environment.NewLine + Environment.NewLine, receipts);
    }

    private static bool HasReceipt(LinklyCloudBackendSessionResponse status)
    {
        return !string.IsNullOrWhiteSpace(status.ReceiptText) ||
            (status.Notifications ?? []).Any(notification =>
                string.Equals(notification.Type, "receipt", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(ReadReceiptNotification(notification.PayloadJson)));
    }

    private static string? ReadReceiptNotification(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(payloadJson);
        return ReadReceiptText(document.RootElement) ?? ReadReceiptText(ReadResponse(document.RootElement));
    }

    private static string? ReadReceiptText(JsonElement element)
    {
        if (!TryGetProperty(element, "ReceiptText", out var receipt))
        {
            return null;
        }

        if (receipt.ValueKind == JsonValueKind.String)
        {
            return NormalizeOptional(receipt.GetString());
        }

        if (receipt.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var lines = receipt
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => NormalizeOptional(item.GetString()))
            .Where(line => line is not null)
            .Select(line => line!)
            .ToArray();
        return lines.Length == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    private static bool IsFinal(LinklyCloudBackendSessionResponse status)
    {
        return HasResolvedCompletedOutcome(status) ||
            string.Equals(status.Status, StatusFailed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status.Status, StatusNotSubmitted, StringComparison.OrdinalIgnoreCase) ||
            IsCancelledStatus(status.Status) ||
            HasOfficialPendingSuccess(status);
    }

    public async Task AcknowledgeSettlementAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await PostSettlementSessionMarkerAsync(
            settings,
            sessionId,
            "acknowledge",
            new LinklyCloudBackendAcknowledgeRequest(settings.Environment.ToString()),
            cancellationToken);
    }

    public async Task MarkSettlementReceiptPrintedAsync(
        CardTerminalSettings settings,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        await PostSettlementSessionMarkerAsync(
            settings,
            sessionId,
            "receipt/printed",
            new LinklyCloudBackendMarkReceiptPrintedRequest(settings.Environment.ToString()),
            cancellationToken);
    }

    private static bool HasResolvedCompletedOutcome(LinklyCloudBackendSessionResponse status)
    {
        if (!string.Equals(status.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Linkly OK/sendkey 后可能先返回 Completed，但交易通知和结果字段稍后才到；结果未解析前继续等待同一 session。
        return status.TransactionSuccess.HasValue ||
            !string.IsNullOrWhiteSpace(NormalizeOptional(status.ResponseCode)) ||
            HasVerifiedApprovedTransactionNotification(status);
    }

    private static bool IsCompletedOrPendingSuccess(LinklyCloudBackendSessionResponse status)
    {
        return string.Equals(status.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase) ||
            HasOfficialPendingSuccess(status);
    }

    private static bool HasOfficialPendingSuccess(LinklyCloudBackendSessionResponse status)
    {
        // Pending 但已有官方交易成功证据时，必须收尾成功交易，避免终端已批准而 WPF 继续等待。
        return string.Equals(status.Status, StatusPending, StringComparison.OrdinalIgnoreCase) &&
            HasOfficialSuccessEvidence(status);
    }

    private static bool HasOfficialSuccessEvidence(LinklyCloudBackendSessionResponse status)
    {
        if (status.TransactionSuccess == false)
        {
            return false;
        }

        return HasVerifiedApprovedTransactionNotification(status);
    }

    private static bool HasVerifiedApprovedTransactionNotification(LinklyCloudBackendSessionResponse status)
    {
        return TryReadVerifiedApprovedTransactionNotification(status, out _);
    }

    // 已到终态的批准与实时收款、自动恢复共用同一套金额核验；仍在 Pending 的批准由
    // HasPendingApprovalEvidenceMatchingAttempt 负责，两者按会话状态分工。
    internal static bool HasFinalApprovalEvidenceMatchingAttempt(
        LinklyCloudBackendSessionResponse status,
        string? expectedTxnRef,
        decimal expectedAmount)
    {
        if (string.Equals(status.Status, StatusPending, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var requestedAmount = Math.Abs(expectedAmount);
        var result = ReadTransactionResult(status, requestedAmount, NormalizeOptional(expectedTxnRef) ?? string.Empty);
        return result.Succeeded && IsTransactionResultVerified(status, result, requestedAmount);
    }

    internal static bool HasPendingApprovalEvidenceMatchingAttempt(
        LinklyCloudBackendSessionResponse status,
        string? expectedTxnRef,
        decimal expectedAmount,
        string? expectedTxnType)
    {
        if (!string.Equals(status.Status, StatusPending, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!TryReadVerifiedApprovedTransactionNotification(status, out var evidence))
        {
            return false;
        }

        var normalizedExpectedTxnRef = NormalizeOptional(expectedTxnRef);
        if (normalizedExpectedTxnRef is null ||
            !string.Equals(evidence.TxnRef, normalizedExpectedTxnRef, StringComparison.Ordinal) ||
            decimal.Round(evidence.Amount, 2, MidpointRounding.AwayFromZero) !=
            decimal.Round(Math.Abs(expectedAmount), 2, MidpointRounding.AwayFromZero))
        {
            return false;
        }

        var notificationTxnType = NormalizeOptional(evidence.TxnType);
        var normalizedExpectedTxnType = NormalizeOptional(expectedTxnType);
        return notificationTxnType is null ||
            normalizedExpectedTxnType is null ||
            string.Equals(notificationTxnType, normalizedExpectedTxnType, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadVerifiedApprovedTransactionNotification(
        LinklyCloudBackendSessionResponse status,
        out VerifiedTransactionNotificationEvidence evidence)
    {
        evidence = default;
        var protectedResponseCode = NormalizeOptional(status.ResponseCode);
        var protectedResponseText = NormalizeOptional(status.ResponseText);
        var notification = string.IsNullOrWhiteSpace(protectedResponseCode)
            ? (status.Notifications ?? []).LastOrDefault(IsTransactionNotification)
            : (status.Notifications ?? []).LastOrDefault(candidate =>
                IsTransactionNotification(candidate) &&
                TransactionNotificationMatchesProtectedResult(candidate, protectedResponseCode, protectedResponseText));
        if (notification is null || string.IsNullOrWhiteSpace(notification.PayloadJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(notification.PayloadJson);
            var response = ReadResponse(document.RootElement);
            var responseCode = ReadString(response, "ResponseCode");
            var notificationTxnRef = NormalizeOptional(ReadString(response, "TxnRef"));
            var persistedTxnRef = NormalizeOptional(status.TxnRef);
            if (!string.IsNullOrWhiteSpace(notificationTxnRef) &&
                !string.IsNullOrWhiteSpace(persistedTxnRef) &&
                !string.Equals(notificationTxnRef, persistedTxnRef, StringComparison.Ordinal))
            {
                return false;
            }

            var amount = ReadDecimal(response, "AmtPurchase");
            var resolvedTxnRef = notificationTxnRef ?? persistedTxnRef;
            if (!IsSuccessfulTransaction(
                status.TransactionSuccess,
                protectedResponseCode ?? responseCode,
                ReadBool(response, "Success")) ||
                amount is null ||
                string.IsNullOrWhiteSpace(resolvedTxnRef))
            {
                return false;
            }

            evidence = new VerifiedTransactionNotificationEvidence(
                resolvedTxnRef,
                amount.Value,
                ReadString(response, "TxnType"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private readonly record struct VerifiedTransactionNotificationEvidence(
        string TxnRef,
        decimal Amount,
        string? TxnType);

    private static bool IsCancelledStatus(string? status)
    {
        // Linkly 后端取消成功可能返回两种拼写，轮询必须在这里停止，避免误报上一笔未完成。
        return string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "Canceled", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresRecovery(LinklyCloudBackendSessionResponse status)
    {
        return string.Equals(status.Status, StatusTokenRefreshRequired, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status.RecoveryAction, RecoveryRefreshToken, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(status.RecoveryAction, RecoveryRetry, StringComparison.OrdinalIgnoreCase) &&
                IsRecoveryHttpStatus(status.LastHttpStatus));
    }

    private async Task DelayBeforeNextPollAsync(
        LinklyCloudBackendSessionResponse status,
        CancellationToken cancellationToken)
    {
        var delay = GetNextPollDelay(status);
        Log($"poll delay start sessionId={status.SessionId} delayMs={delay.TotalMilliseconds:0} lastHttp={status.LastHttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "<null>"}");
        var stopwatch = Stopwatch.StartNew();
        await DelayAsync(delay, cancellationToken);
        stopwatch.Stop();
        Log($"poll delay completed sessionId={status.SessionId} elapsedMs={stopwatch.ElapsedMilliseconds}");
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay > TimeSpan.Zero)
        {
            await _delayAsync(delay, cancellationToken);
        }
    }

    private TimeSpan GetNextPollDelay(LinklyCloudBackendSessionResponse status)
    {
        if (!IsRecoveryHttpStatus(status.LastHttpStatus))
        {
            return _pollInterval;
        }

        var exponent = Math.Clamp(status.RecoveryCount, 0, 6);
        var multiplier = 1 << exponent;
        var baseMilliseconds = Math.Min(
            _pollInterval.TotalMilliseconds * multiplier,
            TimeSpan.FromSeconds(30).TotalMilliseconds);
        var jitterWindow = Math.Min(500d, baseMilliseconds / 2d);
        var jitter = CalculateStableJitterMilliseconds(status, jitterWindow);
        var milliseconds = Math.Clamp(
            baseMilliseconds + jitter,
            0d,
            TimeSpan.FromSeconds(30).TotalMilliseconds);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static double CalculateStableJitterMilliseconds(
        LinklyCloudBackendSessionResponse status,
        double jitterWindow)
    {
        if (jitterWindow <= 0d)
        {
            return 0d;
        }

        // 使用 session 状态生成稳定抖动，避免多台收银机在恢复阶段同时重试。
        var seed = $"{status.SessionId}|{status.RecoveryCount}|{status.LastHttpStatus}";
        uint hash = 2166136261u;
        unchecked
        {
            foreach (var ch in seed)
            {
                hash ^= ch;
                hash *= 16777619u;
            }
        }

        var range = Math.Max(1, (int)Math.Round(jitterWindow * 2d));
        var offset = (hash % (uint)(range + 1)) - jitterWindow;
        if (Math.Abs(offset) < 0.001d)
        {
            offset = (hash & 1u) == 0u
                ? Math.Min(1d, jitterWindow)
                : -Math.Min(1d, jitterWindow);
        }

        return offset;
    }

    private static bool IsRecoveryHttpStatus(int? httpStatus)
    {
        return httpStatus == (int)HttpStatusCode.RequestTimeout ||
            // 关键逻辑：必须与服务端 IsTransportRecoveryFailure 覆盖同一组状态码。
            // 服务端把 429 标成可恢复之后，RequiresRecovery 仍要本判定为真才会真正走恢复；
            // 同时 GetNextPollDelay 也以此决定是否启用指数退避——被限流时正该退避，
            // 而不是继续按固定间隔猛打。
            httpStatus == (int)HttpStatusCode.TooManyRequests ||
            httpStatus is >= 500 and <= 599;
    }

    private static IReadOnlyDictionary<string, string>? BuildPurchaseAnalysisData(
        decimal amount,
        PosSessionState session,
        string? refundReference)
    {
        if (string.IsNullOrWhiteSpace(refundReference))
        {
            return null;
        }

        return new Dictionary<string, string>
        {
            ["RFN"] = refundReference.Trim(),
            ["OPR"] = $"{session.CashierId}|{session.CashierName}",
            ["AMT"] = ToMinorUnits(amount).ToString("D9", CultureInfo.InvariantCulture),
            ["PCM"] = "0000"
        };
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

        return Limit($"{device}{DateTimeOffset.UtcNow:yyMMddHHmmss}", 16);
    }

    private static string? TryParseRefundReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var backendRefundReference = LinklyBackendPaymentReference.TryGetRefundReference(reference);
        if (!string.IsNullOrWhiteSpace(backendRefundReference))
        {
            return backendRefundReference;
        }

        var parts = reference.Trim().Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 &&
            string.Equals(parts[0], "ANZCLOUD", StringComparison.OrdinalIgnoreCase)
                ? parts[2]
                : null;
    }

    private static string? TryReadOriginalTxnRef(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var parts = reference.Trim().Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 &&
            string.Equals(parts[0], LinklyBackendPaymentReference.Prefix, StringComparison.OrdinalIgnoreCase)
                ? Uri.UnescapeDataString(parts[1])
                : null;
    }

    private string FormatResponseMessage(string? responseText, string? responseCode)
    {
        var text = NormalizeOptional(responseText);
        var code = NormalizeOptional(responseCode);
        if (text is null && code is null)
        {
            return T("linkly.backend.declined", "ANZ Linkly Cloud transaction was declined.");
        }

        return code is null ? text! : $"{text ?? T("linkly.backend.declined", "ANZ Linkly Cloud transaction was declined.")} ({code})";
    }

    private static JsonElement ReadResponse(JsonElement root)
    {
        return TryGetProperty(root, "Response", out var response) && response.ValueKind == JsonValueKind.Object
            ? response
            : root;
    }

    private static JsonElement ReadObject(JsonElement root, string propertyName)
    {
        return TryGetProperty(root, propertyName, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : default;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Undefined ||
            !TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => NormalizeOptional(value.GetString()),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool? ReadBool(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static bool ReadFlag(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
            JsonValueKind.String => IsTruthyFlag(value.GetString()),
            _ => false
        };
    }

    private static bool IsTruthyFlag(string? value)
    {
        var normalized = NormalizeOptional(value);
        return string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static decimal? ReadDecimal(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var decimalValue))
        {
            return decimalValue / 100m;
        }

        return value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), out var parsed)
            ? parsed / 100m
            : null;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? MaskCardNumber(string? pan)
    {
        return LinklyReceiptTextSanitizer.SanitizeCardNumber(pan);
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
        LinklyJsonLog.WriteMessage("LinklyBackend", "backend-terminal", message);
    }

    private string FormatRequestUrl(string relativeUrl)
    {
        return httpClient.BaseAddress is null
            ? relativeUrl
            : new Uri(httpClient.BaseAddress, relativeUrl).ToString();
    }

    private static string SerializeDebugJson<T>(T value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static void LogHttpRequest(
        string operation,
        HttpMethod method,
        string url,
        string? txnType,
        string? txnRef,
        string? bodyJson)
    {
        var responseDetails = LinklyHttpEvidenceDetails.Empty;
        LinklyJsonLog.Write(
            "LinklyBackend",
            "backend-terminal",
            operation,
            "request",
            direction: "request",
            request: new
            {
                method = method.Method,
                url,
                body = RawJsonBody(bodyJson)
            },
            details: new
            {
                timestamp = DateTimeOffset.Now,
                certCase = GetCertificationCase(operation),
                transactionReference = ReadTransactionReference(url, bodyJson, responseDetails),
                txnType,
                txnRef,
                requestJson = RedactedJsonText(bodyJson),
                responseJson = (string?)null
            });
    }

    private static void LogHttpResponse(
        string operation,
        HttpMethod method,
        string url,
        HttpStatusCode statusCode,
        long elapsedMs,
        string? txnType,
        string? txnRef,
        string? bodyJson,
        LinklyLogLevel? level = null)
    {
        var responseDetails = ReadLinklyHttpEvidenceDetails(bodyJson);
        LinklyJsonLog.Write(
            "LinklyBackend",
            "backend-terminal",
            operation,
            "response",
            direction: "response",
            httpStatus: statusCode,
            success: (int)statusCode is >= 200 and < 300,
            elapsedMs: elapsedMs,
            response: new
            {
                method = method.Method,
                url,
                body = RawJsonBody(bodyJson)
            },
            details: new
            {
                timestamp = DateTimeOffset.Now,
                certCase = GetCertificationCase(operation),
                transactionReference = ReadTransactionReference(url, null, responseDetails),
                txnType,
                txnRef = NormalizeOptional(txnRef) ?? responseDetails.TxnRef,
                requestJson = (string?)null,
                responseJson = RedactedJsonText(bodyJson),
                responseTxnRef = responseDetails.TxnRef,
                responseDate = responseDetails.Date,
                responseTime = responseDetails.Time,
                responseCode = responseDetails.ResponseCode,
                responseText = responseDetails.ResponseText
            },
            level: level);
    }

    /// <summary>
    /// 发送阶段（尚未拿到响应）的网络异常/超时原先只有 request 日志；这里补一条带耗时的失败记录后原样抛出，不改变异常类型。
    /// </summary>
    private static async Task<HttpResponseMessage> SendWithFailureLogAsync(
        string operation,
        HttpMethod method,
        string url,
        string? txnType,
        Stopwatch stopwatch,
        Func<Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        try
        {
            return await send();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            LogHttpFailure(operation, method, url, txnType, stopwatch.ElapsedMilliseconds, ex, cancellationToken);
            throw;
        }
    }

    private static void LogHttpFailure(
        string operation,
        HttpMethod method,
        string url,
        string? txnType,
        long elapsedMs,
        Exception exception,
        CancellationToken cancellationToken,
        HttpStatusCode? httpStatus = null)
    {
        // 令牌已取消 = 调用方取消或上层业务超时，由上层 catch 区分并记录，这里只记 Information 避免重复告警；
        // 令牌未取消的 OperationCanceledException 是 HttpClient 自身超时。
        var cancelled = exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
        var reason = exception switch
        {
            OperationCanceledException when cancelled => "cancelled",
            OperationCanceledException => "timeout",
            HttpRequestException => "network-error",
            JsonException => "invalid-response",
            _ => exception.GetType().Name
        };
        LinklyJsonLog.Write(
            "LinklyBackend",
            "backend-terminal",
            operation,
            cancelled ? "cancelled" : "failed",
            direction: "response",
            httpStatus: httpStatus,
            success: false,
            reason: reason,
            elapsedMs: elapsedMs,
            details: new
            {
                method = method.Method,
                url,
                txnType
            },
            exception: exception,
            level: cancelled ? LinklyLogLevel.Information : null);
    }

    private static string? GetCertificationCase(string operation)
    {
        return operation switch
        {
            "transaction-status-test" => "3.1.1/3.1.2",
            "resumable session" => "4.1.2",
            "status" => "3.1.3/4.1.2",
            "recover" => "3.1.3/4.1.2",
            _ => null
        };
    }

    private static string? ReadTransactionReference(
        string url,
        string? requestJson,
        LinklyHttpEvidenceDetails responseDetails)
    {
        return NormalizeOptional(responseDetails.TxnRef) ??
            ReadSessionIdFromUrl(url) ??
            ReadSessionIdFromJson(requestJson);
    }

    private static string? ReadSessionIdFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], "transactions", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segments[i], "sessions", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(segments[i + 1]);
            }
        }

        return null;
    }

    private static string? ReadSessionIdFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return ReadString(document.RootElement, "SessionId");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static LinklyHttpEvidenceDetails ReadLinklyHttpEvidenceDetails(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return LinklyHttpEvidenceDetails.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var response = TryGetProperty(root, "result", out var result) && result.ValueKind == JsonValueKind.Object
                ? result
                : root;
            if (TryGetProperty(response, "data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                response = data;
            }

            return new LinklyHttpEvidenceDetails(
                ReadString(response, "txnRef") ?? ReadString(response, "TxnRef") ?? ReadString(response, "responseTxnRef"),
                ReadString(response, "responseDate") ?? ReadString(response, "Date"),
                ReadString(response, "responseTime") ?? ReadString(response, "Time"),
                ReadString(response, "responseCode") ?? ReadString(response, "ResponseCode"),
                ReadString(response, "responseText") ?? ReadString(response, "ResponseText"));
        }
        catch (JsonException)
        {
            return LinklyHttpEvidenceDetails.Empty;
        }
    }

    private sealed record LinklyHttpEvidenceDetails(
        string? TxnRef,
        string? Date,
        string? Time,
        string? ResponseCode,
        string? ResponseText)
    {
        public static LinklyHttpEvidenceDetails Empty { get; } = new(null, null, null, null, null);
    }

    private static void LogStatusSnapshot(string prefix, LinklyCloudBackendSessionResponse status)
    {
        // 状态快照只是过程记录（轮询每秒一次），显示文本里出现 "FAILED" 等字样也不能被自动升为 Warning 刷屏；
        // 真正的失败由响应/异常分支单独记录。
        var spaceIndex = prefix.IndexOf(' ', StringComparison.Ordinal);
        LinklyJsonLog.Write(
            "LinklyBackend",
            "backend-terminal",
            spaceIndex <= 0 ? prefix : prefix[..spaceIndex],
            "snapshot",
            sessionId: status.SessionId,
            details: new
            {
                message = BuildStatusSnapshotMessage(prefix, status)
            },
            level: LinklyLogLevel.Information);
    }

    private static string BuildStatusSnapshotMessage(string prefix, LinklyCloudBackendSessionResponse status)
    {
        return
            $"{prefix} sessionId={status.SessionId} status={status.Status} lastHttp={status.LastHttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "<null>"} " +
            $"txnRef={LogValue(status.TxnRef)} " +
            $"display=\"{LogValue(TruncateForLog(status.DisplayText, 80))}\" " +
            $"flags=cancel:{status.CancelKeyFlag},ok:{status.OKKeyFlag},yes:{status.AcceptYesKeyFlag},no:{status.DeclineNoKeyFlag},auth:{status.AuthoriseKeyFlag} " +
            $"notifications={status.Notifications?.Count ?? 0}";
    }

    private static string LogJsonBody(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<none>" : value.Trim();
    }

    private static string? RedactedJsonText(string? bodyJson)
    {
        var redacted = RawJsonBody(bodyJson);
        if (redacted is null)
        {
            return null;
        }

        return redacted is string text
            ? text
            : JsonSerializer.Serialize(redacted, JsonOptions);
    }

    private static object? RawJsonBody(string? bodyJson)
    {
        if (string.IsNullOrWhiteSpace(bodyJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(bodyJson);
            return RedactLogJsonElement(document.RootElement, propertyName: null);
        }
        catch (JsonException)
        {
            var normalized = bodyJson.Trim();
            return new
            {
                hasValue = normalized.Length > 0,
                length = normalized.Length
            };
        }
    }

    private static object? RedactLogJsonElement(JsonElement element, string? propertyName)
    {
        if (IsMaskedCardLogProperty(propertyName))
        {
            var cardNumber = element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : element.GetRawText();
            return LinklyJsonLog.MaskCardNumberForLog(cardNumber);
        }

        if (IsSensitiveLogProperty(propertyName))
        {
            return DescribeSensitiveLogValue(element);
        }

        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => RedactLogJsonElement(property.Value, property.Name),
                    StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(item => RedactLogJsonElement(item, propertyName: null))
                .ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var number) => number,
            JsonValueKind.Number when element.TryGetDecimal(out var number) => number,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }

    private static bool IsSensitiveLogProperty(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        return string.Equals(propertyName, "receiptText", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "payloadJson", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "displayText", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "displayLines", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "graphicCode", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "inputType", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "analysisData", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "token", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "accessToken", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "authorization", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "secret", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "track2", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMaskedCardLogProperty(string? propertyName)
    {
        return string.Equals(propertyName, "cardNumber", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "maskedCardNumber", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "accountNumber", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "pan", StringComparison.OrdinalIgnoreCase);
    }

    private static object DescribeSensitiveLogValue(JsonElement element)
    {
        var value = element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : element.GetRawText();
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        // 支付收据和卡片分析字段只记录排障元数据，避免本地日志保存完整交易凭据。
        return new
        {
            hasValue = normalized is not null,
            length = normalized?.Length ?? 0,
            lineCount = normalized is null ? 0 : normalized.Split('\n').Length
        };
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value.Trim();
    }

    private static string? TruncateForLog(string? value, int maxLength)
    {
        var normalized = NormalizeOptional(value);
        if (normalized is null || normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..maxLength];
    }

    private static string GetComponentVersion()
    {
        var assembly = typeof(LinklyBackendTerminalClient).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
            assembly.GetName().Version?.ToString() ??
            "unknown";
    }

    private string T(string key, string fallback)
    {
        var value = localization?.T(key) ?? LocalizationResourceProvider.Instance[key];
        return string.IsNullOrWhiteSpace(value) || value.StartsWith("[[", StringComparison.Ordinal)
            ? fallback
            : value;
    }

    private sealed record BackendReadinessResult(bool IsReady, string Message)
    {
        public static BackendReadinessResult Ready { get; } = new(true, string.Empty);

        public static BackendReadinessResult NotReady(string message)
        {
            return new BackendReadinessResult(false, message);
        }
    }

}
