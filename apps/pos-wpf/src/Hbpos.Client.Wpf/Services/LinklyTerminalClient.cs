using System.Globalization;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;
using PCEFTPOS.EFTClient.IPInterface;

namespace Hbpos.Client.Wpf.Services;

public interface ILinklyTerminalClient
{
    Task<LinklyConnectionTestResult> TestConnectionAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task<LinklyLogonResult> LogonAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new LinklyLogonResult(
            false,
            "Linkly bank network logon is not supported by this terminal client."));

    Task<LinklySettlementResult> SettlementAsync(
        PosSessionState session,
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new LinklySettlementResult(
            false,
            "Linkly settlement is not supported by this terminal client."));

    Task<PaymentAuthorizationResult> PurchaseAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default);

    Task<PaymentAuthorizationResult> PurchaseWithReferenceAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string txnRef,
        CancellationToken cancellationToken = default);

    Task<PaymentAuthorizationResult> RecoverLastTransactionAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string txnRef,
        CancellationToken cancellationToken = default);

    Task<PaymentAuthorizationResult> RefundAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        CancellationToken cancellationToken = default);

    // 中文注释：Linkly LocalIp 的 SDK 只能用 TxnRef 关联提交和 GetLast；原交易引用仍由上层保留用于审计和退款业务语义。
    Task<PaymentAuthorizationResult> RefundWithReferenceAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        string refundTxnRef,
        CancellationToken cancellationToken = default) =>
        RefundAsync(amount, session, settings, originalReference, cancellationToken);

    Task<PaymentAuthorizationResult> VoidAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        CancellationToken cancellationToken = default);
}

public sealed record LinklyStatusTestDetails(
    string? TransactionReference,
    DateTimeOffset? Timestamp,
    string? ResponseCode,
    string? ResponseText,
    string? TxnRef);

public sealed record LinklyConnectionTestResult(
    bool Succeeded,
    string? Message = null,
    LinklyStatusTestDetails? StatusTest = null,
    bool? PinPadLoggedOn = null,
    // EFT-Client 明确回报刷卡机离线（PF）；与连不上 EFT-Client、状态查询超时区分开，调用方据此给不同提示。
    bool PinPadOffline = false);

public sealed record LinklyLogonResult(
    bool Succeeded,
    string? Message = null,
    string? ResponseCode = null,
    string? ResponseText = null,
    IReadOnlyList<string>? ReceiptTexts = null,
    bool ResultUnknown = false);

public sealed record LinklySettlementResult(
    bool Succeeded,
    string Message,
    string? SessionId = null,
    string? ResponseCode = null,
    string? ResponseText = null,
    string? SettlementData = null,
    IReadOnlyList<string>? ReceiptTexts = null,
    bool ResultUnknown = false,
    ProviderSubmissionState ProviderSubmissionState = ProviderSubmissionState.Unknown);

public interface ILinklyEftClientFactory
{
    ILinklyEftClient Create();
}

public interface ILinklyEftClient : IDisposable
{
    Task<bool> ConnectAsync(
        string hostName,
        int hostPort,
        bool useSsl,
        bool useKeepAlive);

    Task<bool> WriteRequestAsync(EFTRequest request);

    Task<bool> SendCancelRequestAsync();

    Task<EFTResponse?> ReadResponseAsync(CancellationToken cancellationToken);

    bool Disconnect();
}

public sealed class LinklyEftClientFactory : ILinklyEftClientFactory
{
    public ILinklyEftClient Create()
    {
        return new LinklyEftClientAdapter(new EFTClientIPAsync());
    }
}

public sealed class LinklyEftClientAdapter(EFTClientIPAsync client) : ILinklyEftClient
{
    public Task<bool> ConnectAsync(
        string hostName,
        int hostPort,
        bool useSsl,
        bool useKeepAlive)
    {
        return client.ConnectAsync(hostName, hostPort, useSsl, useKeepAlive);
    }

    public Task<bool> WriteRequestAsync(EFTRequest request)
    {
        return client.WriteRequestAsync(request);
    }

    public Task<bool> SendCancelRequestAsync()
    {
        return client.WriteRequestAsync(new EFTSendKeyRequest { Key = EFTPOSKey.OkCancel });
    }

    public async Task<EFTResponse?> ReadResponseAsync(CancellationToken cancellationToken)
    {
        return await client.ReadResponseAsync(cancellationToken);
    }

    public bool Disconnect()
    {
        return client.Disconnect();
    }

    public void Dispose()
    {
        client.Dispose();
    }
}

public sealed class LinklyTerminalClient(
    ILinklyEftClientFactory clientFactory,
    ILocalizationService? localization = null,
    ILinklyPaymentAttemptContextAccessor? attemptContextAccessor = null) : ILinklyTerminalClient
{
    private const string LogCategory = "LinklyLocal";
    private const string LogSource = "local-ip";
    private const string ProcessorName = "ANZ";
    private const string Merchant = "00";
    private const string CancelledMessage = "ANZ Linkly transaction was cancelled.";
    // EFT-Client 无法与 PIN pad 通信时的响应码（生产实测 ResponseText 为 "PINpad Offline"）。
    private const string PinpadOfflineResponseCode = "PF";

    public async Task<LinklyConnectionTestResult> TestConnectionAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CreateConfiguredTimeoutToken(timeout, cancellationToken);
        using var client = clientFactory.Create();
        var operation = "connect";
        try
        {
            var connectRequest = CreateConnectRequest(host, port);
            LogJson(
                "connect",
                "start",
                "request",
                request: connectRequest);
            var connected = await client.ConnectAsync(host, port, useSsl: false, useKeepAlive: false)
                .WaitAsync(timeoutCts.Token);
            LogJson(
                "connect",
                connected ? "succeeded" : "failed",
                "response",
                success: connected,
                request: connectRequest,
                response: new
                {
                    connected
                });
            if (!connected)
            {
                return new LinklyConnectionTestResult(false, T("linkly.local.test.failed", "Linkly EFT-Client connection failed."));
            }

            operation = "status";
            var statusRequest = new EFTStatusRequest
            {
                Application = TerminalApplication.EFTPOS,
                Merchant = Merchant,
                StatusType = StatusType.Standard
            };
            var statusRequestLog = new
            {
                statusRequest.Application,
                statusRequest.Merchant,
                statusRequest.StatusType
            };
            LogJson(
                operation,
                "sent",
                "request",
                request: statusRequestLog);
            if (!await client.WriteRequestAsync(statusRequest).WaitAsync(timeoutCts.Token))
            {
                LogJson(
                    operation,
                    "failed",
                    "response",
                    success: false,
                    reason: "write-request-failed",
                    request: statusRequestLog);
                return new LinklyConnectionTestResult(
                    false,
                    T("linkly.local.test.statusRequestFailed", "Linkly PINpad status request could not be sent."));
            }

            while (true)
            {
                var response = await client.ReadResponseAsync(timeoutCts.Token);
                if (response is null)
                {
                    LogJson(
                        operation,
                        "failed",
                        "response",
                        success: false,
                        reason: "empty-response",
                        request: statusRequestLog);
                    return new LinklyConnectionTestResult(
                        false,
                        T("linkly.local.test.emptyStatusResponse", "Linkly PINpad returned no status response."));
                }

                if (response is not EFTStatusResponse status)
                {
                    LogJson(
                        operation,
                        "ignored",
                        "response",
                        reason: "unexpected-response",
                        details: new
                        {
                            responseType = response.GetType().Name
                        });
                    continue;
                }

                var pinPadOffline = string.Equals(status.ResponseCode, "PF", StringComparison.OrdinalIgnoreCase) ||
                    status.ResponseText?.Contains("PINpad Offline", StringComparison.OrdinalIgnoreCase) == true;
                var ready = status.Success && status.LoggedOn && !pinPadOffline;
                LogJson(
                    operation,
                    "received",
                    "response",
                    success: status.Success && !pinPadOffline,
                    request: statusRequestLog,
                    response: new
                    {
                        status.Success,
                        status.ResponseCode,
                        status.ResponseText,
                        status.LoggedOn,
                        status.PinPadVersion,
                        status.StatusType,
                        status.TerminalCommsType
                    });
                if (pinPadOffline)
                {
                    return new LinklyConnectionTestResult(
                        false,
                        T("linkly.local.test.pinpadOffline", "Linkly PINpad is offline (PF). Check the terminal connection in Linkly Client."),
                        PinPadOffline: true);
                }

                if (ready)
                {
                    return new LinklyConnectionTestResult(
                        true,
                        T("linkly.local.test.success", "Linkly PINpad is online and logged on."),
                        PinPadLoggedOn: true);
                }

                if (status.Success && !status.LoggedOn)
                {
                    return new LinklyConnectionTestResult(
                        true,
                        T("linkly.local.test.notLoggedOn", "Linkly PINpad is online but not logged on to the bank network."),
                        PinPadLoggedOn: false);
                }

                return new LinklyConnectionTestResult(
                    false,
                    string.Format(
                        CultureInfo.CurrentCulture,
                        T("linkly.local.test.pinpadNotReady", "Linkly PINpad is not ready: {0} ({1})."),
                        NormalizeOptional(status.ResponseText) ?? T("linkly.local.test.unknownResponse", "Unknown response"),
                        NormalizeOptional(status.ResponseCode) ?? "--"));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogJson(
                operation,
                "failed",
                "response",
                success: false,
                reason: "timeout");
            return operation == "connect"
                ? new LinklyConnectionTestResult(false, T("linkly.local.test.timeout", "Linkly EFT-Client connection timed out."))
                : new LinklyConnectionTestResult(false, T("linkly.local.test.statusTimeout", "Linkly PINpad readiness check timed out."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogJson(
                operation,
                "failed",
                "response",
                success: false,
                reason: ex.GetType().Name,
                details: new
                {
                    ex.Message
                },
                exception: ex);
            return new LinklyConnectionTestResult(
                false,
                string.Format(
                    CultureInfo.CurrentCulture,
                    T("linkly.local.test.exception", "Linkly connection failed: {0}"),
                    ex.Message));
        }
        finally
        {
            SafeDisconnect(client, txnRef: null);
        }
    }

    public async Task<LinklyLogonResult> LogonAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new LinklyLogonResult(
                false,
                T("linkly.local.logon.cancelled", "Linkly bank network logon was cancelled."));
        }

        using var timeoutCts = CreateConfiguredTimeoutToken(timeout, cancellationToken);
        using var client = clientFactory.Create();
        var receipts = new List<string>();
        var requestMayHaveBeenSent = false;
        var operation = "connect";
        try
        {
            var connectRequest = CreateConnectRequest(host, port);
            LogJson("connect", "start", "request", request: connectRequest, details: new { purpose = "logon" });
            var connected = await client.ConnectAsync(host, port, useSsl: false, useKeepAlive: false)
                .WaitAsync(timeoutCts.Token);
            LogJson(
                "connect",
                connected ? "succeeded" : "failed",
                "response",
                success: connected,
                request: connectRequest,
                response: new { connected },
                details: new { purpose = "logon" });
            if (!connected)
            {
                return new LinklyLogonResult(
                    false,
                    T("linkly.local.logon.connectionFailed", "Linkly EFT-Client connection failed."));
            }

            operation = "logon";
            var request = new EFTLogonRequest
            {
                Merchant = Merchant,
                LogonType = LogonType.Standard,
                Application = TerminalApplication.EFTPOS,
                ReceiptAutoPrint = ReceiptPrintModeType.POSPrinter
            };
            LogJson(operation, "sent", "request", request: request);
            requestMayHaveBeenSent = true;
            if (!await client.WriteRequestAsync(request).WaitAsync(timeoutCts.Token))
            {
                requestMayHaveBeenSent = false;
                LogJson(operation, "failed", "response", success: false, reason: "write-request-failed", request: request);
                return new LinklyLogonResult(
                    false,
                    T("linkly.local.logon.requestFailed", "Linkly bank network logon request could not be sent."));
            }

            // 登录小票仅随结果返回供诊断，不打印、不持久化，也绝不把正文写入日志。
            while (true)
            {
                var response = await client.ReadResponseAsync(timeoutCts.Token);
                switch (response)
                {
                    case EFTReceiptResponse receipt when receipt.Type == ReceiptType.Logon:
                        CaptureReceipt(receipts, receipt);
                        LogJson(
                            "logon-receipt",
                            "received",
                            "response",
                            details: new
                            {
                                receipt.Type,
                                lineCount = receipt.ReceiptText?.Length ?? 0
                            });
                        break;
                    case EFTReceiptResponse receipt:
                        LogJson(
                            "logon-receipt",
                            "ignored",
                            "response",
                            reason: "non-logon-receipt",
                            details: new
                            {
                                receipt.Type,
                                lineCount = receipt.ReceiptText?.Length ?? 0
                            });
                        break;
                    case EFTDisplayResponse display:
                        LogJson(
                            "logon-display",
                            "ignored",
                            "response",
                            details: new
                            {
                                display.NumberOfLines,
                                display.LineLength,
                                display.CancelKeyFlag,
                                display.AcceptYesKeyFlag,
                                display.DeclineNoKeyFlag,
                                display.AuthoriseKeyFlag,
                                display.OKKeyFlag,
                                display.InputType,
                                display.GraphicCode
                            });
                        break;
                    case EFTLogonResponse logon:
                    {
                        var succeeded = logon.Success &&
                            string.Equals(logon.ResponseCode, "00", StringComparison.Ordinal);
                        LogJson(
                            operation,
                            "received",
                            "response",
                            success: succeeded,
                            request: request,
                            response: logon);
                        var responseText = NormalizeOptional(logon.ResponseText);
                        var responseCode = NormalizeOptional(logon.ResponseCode);
                        var message = succeeded
                            ? T("linkly.local.logon.success", "Linkly PINpad logged on to the bank network.")
                            : string.Format(
                                CultureInfo.CurrentCulture,
                                T("linkly.local.logon.failed", "Linkly bank network logon failed: {0} ({1})."),
                                responseText ?? T("linkly.local.test.unknownResponse", "Unknown response"),
                                responseCode ?? "--");
                        return new LinklyLogonResult(
                            succeeded,
                            message,
                            responseCode,
                            responseText,
                            receipts);
                    }
                    case null:
                        LogJson(operation, "unknown", "response", success: false, reason: "empty-response", request: request);
                        return CreateUnknownLogonResult(receipts);
                    default:
                        LogJson(
                            operation,
                            "ignored",
                            "response",
                            reason: "unexpected-response",
                            details: new { responseType = response.GetType().Name });
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogJson(
                operation,
                requestMayHaveBeenSent ? "unknown" : "failed",
                "response",
                success: false,
                reason: "caller-cancelled");
            if (requestMayHaveBeenSent)
            {
                await TryCancelLogonAsync(client, timeout);
                return new LinklyLogonResult(
                    false,
                    T("linkly.local.logon.cancelledUnknown", "Linkly bank network logon was cancelled; the result is unknown."),
                    ReceiptTexts: receipts,
                    ResultUnknown: true);
            }

            return new LinklyLogonResult(
                false,
                T("linkly.local.logon.cancelled", "Linkly bank network logon was cancelled."));
        }
        catch (OperationCanceledException)
        {
            LogJson(
                operation,
                requestMayHaveBeenSent ? "unknown" : "failed",
                "response",
                success: false,
                reason: "timeout");
            return requestMayHaveBeenSent
                ? CreateUnknownLogonResult(receipts)
                : new LinklyLogonResult(
                    false,
                    T("linkly.local.logon.timeout", "Linkly bank network logon timed out."));
        }
        catch (Exception ex)
        {
            LogJson(
                operation,
                requestMayHaveBeenSent ? "unknown" : "failed",
                "response",
                success: false,
                reason: ex.GetType().Name,
                details: new { ex.Message },
                exception: ex);
            if (requestMayHaveBeenSent)
            {
                return CreateUnknownLogonResult(receipts);
            }

            return new LinklyLogonResult(
                false,
                string.Format(
                    CultureInfo.CurrentCulture,
                    T("linkly.local.logon.exception", "Linkly bank network logon failed: {0}"),
                    ex.Message));
        }
        finally
        {
            SafeDisconnect(client, txnRef: null);
        }
    }

    public Task<PaymentAuthorizationResult> PurchaseAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        CancellationToken cancellationToken = default)
    {
        return RunTransactionAsync(
            TransactionType.PurchaseCash,
            amount,
            session,
            settings,
            originalReference: null,
            cancellationToken);
    }

    public Task<PaymentAuthorizationResult> PurchaseWithReferenceAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string txnRef,
        CancellationToken cancellationToken = default)
    {
        return RunTransactionAsync(
            TransactionType.PurchaseCash,
            amount,
            session,
            settings,
            txnRef,
            cancellationToken,
            requireTxnRefMatch: true);
    }

    public Task<PaymentAuthorizationResult> RecoverLastTransactionAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string txnRef,
        CancellationToken cancellationToken = default)
    {
        _ = session;
        return !LinklyLocalTxnRef.TryNormalizeHistoricalReference(txnRef, out var normalizedTxnRef)
            ? Task.FromResult(new PaymentAuthorizationResult(false, null, T("linkly.local.missingTxnRef", "ANZ Linkly transaction reference is missing."), TxnType: "P"))
            : TryRecoverLastTransactionAsync(
                 settings,
                 amount,
                 normalizedTxnRef,
                 [],
                 T("linkly.local.recoveryFailed", "ANZ Linkly recovery could not confirm the previous transaction."),
                 TransactionType.PurchaseCash,
                 cancellationToken);
    }

    public Task<PaymentAuthorizationResult> RefundAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        CancellationToken cancellationToken = default)
    {
        return RunTransactionAsync(
            TransactionType.Refund,
            amount,
            session,
            settings,
            originalReference,
            cancellationToken);
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
                T("linkly.local.cancelled", CancelledMessage),
                ProviderSubmissionState: ProviderSubmissionState.NotSubmitted);
        }

        using var timeoutCts = CreateBusinessWaitTimeoutToken(cancellationToken);
        var operationId = BuildTxnRef(session);
        var request = new EFTSettlementRequest
        {
            Merchant = Merchant,
            Application = TerminalApplication.EFTPOS,
            SettlementType = SettlementType.Settlement,
            ReceiptAutoPrint = ReceiptPrintModeType.POSPrinter
        };
        var receipts = new List<string>();
        using var client = clientFactory.Create();
        var requestSent = false;

        try
        {
            var connectRequest = CreateConnectRequest(settings.LinklyHost, settings.LinklyPort);
            LogJson("settlement", "connect-start", "request", settings.Environment, operationId, request: connectRequest);
            var connected = await client.ConnectAsync(settings.LinklyHost, settings.LinklyPort, useSsl: false, useKeepAlive: false)
                .WaitAsync(timeoutCts.Token);
            if (!connected)
            {
                LogJson("settlement", "failed", "response", settings.Environment, operationId, success: false, reason: "connection-failed");
                // 结算专用文案：告诉收银员结算没发出去、下一步该检查什么（收款流程仍用通用的连接失败提示）。
                return new LinklySettlementResult(
                    false,
                    T(
                        "linkly.local.settlementConnectionFailed",
                        "Could not connect to the Linkly EFT-Client on this POS, so the settlement was not sent. Make sure the Linkly program is open, then try again."),
                    ProviderSubmissionState: ProviderSubmissionState.NotSubmitted);
            }

            LogJson("settlement", "sent", "request", settings.Environment, operationId, request: request);
            if (!await client.WriteRequestAsync(request).WaitAsync(timeoutCts.Token))
            {
                LogJson("settlement", "failed", "response", settings.Environment, operationId, success: false, reason: "write-request-failed", request: request);
                return new LinklySettlementResult(
                    false,
                    T("linkly.local.requestSendFailed", "ANZ Linkly settlement request could not be sent."),
                    ProviderSubmissionState: ProviderSubmissionState.NotSubmitted);
            }

            requestSent = true;
            var response = await ReadSettlementResponseAsync(client, receipts, timeoutCts.Token, settings.Environment, operationId);
            var succeeded = response.Success && string.Equals(response.ResponseCode, "00", StringComparison.OrdinalIgnoreCase);
            var message = succeeded
                ? NormalizeOptional(response.ResponseText) ?? "ANZ Linkly settlement completed."
                : FormatResponseMessage(response.ResponseText, response.ResponseCode);
            return new LinklySettlementResult(
                succeeded,
                message,
                operationId,
                NormalizeOptional(response.ResponseCode),
                NormalizeOptional(response.ResponseText),
                NormalizeOptional(response.SettlementData),
                receipts,
                ProviderSubmissionState: ProviderSubmissionState.Submitted);
        }
        catch (OperationCanceledException) when (requestSent)
        {
            LogJson("settlement", "unknown", "response", settings.Environment, operationId, success: false, reason: cancellationToken.IsCancellationRequested ? "caller-cancelled-after-submit" : "timeout-after-submit", request: request);
            return new LinklySettlementResult(
                false,
                T("linkly.local.settlementOutcomeUnknown", "ANZ Linkly settlement outcome could not be confirmed."),
                operationId,
                ResultUnknown: true,
                ProviderSubmissionState: ProviderSubmissionState.Unknown);
        }
        catch (Exception ex) when (requestSent)
        {
            LogJson("settlement", "unknown", "response", settings.Environment, operationId, success: false, reason: ex.GetType().Name, request: request, details: new { ex.Message }, exception: ex);
            return new LinklySettlementResult(
                false,
                T("linkly.local.settlementOutcomeUnknown", "ANZ Linkly settlement outcome could not be confirmed."),
                operationId,
                ResultUnknown: true,
                ProviderSubmissionState: ProviderSubmissionState.Unknown);
        }
        catch (OperationCanceledException)
        {
            return new LinklySettlementResult(false, cancellationToken.IsCancellationRequested
                ? T("linkly.local.cancelled", CancelledMessage)
                : T("linkly.local.timeout", "ANZ Linkly settlement timed out."),
                ProviderSubmissionState: ProviderSubmissionState.NotSubmitted);
        }
        catch (Exception ex)
        {
            LogJson("settlement", "failed", "response", settings.Environment, operationId, success: false, reason: ex.GetType().Name, request: request, details: new { ex.Message }, exception: ex);
            return new LinklySettlementResult(false, string.Format(
                CultureInfo.CurrentCulture,
                T("linkly.local.transactionFailed", "ANZ Linkly settlement failed: {0}"),
                ex.Message),
                ProviderSubmissionState: ProviderSubmissionState.NotSubmitted);
        }
        finally
        {
            SafeDisconnect(client, operationId);
        }
    }

    public Task<PaymentAuthorizationResult> RefundWithReferenceAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        string refundTxnRef,
        CancellationToken cancellationToken = default)
    {
        // Linkly 1.7.3 的 EFTTransactionRequest 没有原交易引用字段；TxnRef 是 GetLast 的唯一检索键。
        // 原交易引用由调用方继续持久化并传给后端，退款 attempt 则必须以新的 TxnRef 写入终端请求。
        _ = originalReference;
        return !LinklyLocalTxnRef.TryNormalizeHistoricalReference(refundTxnRef, out var normalizedRefundTxnRef)
            ? Task.FromResult(new PaymentAuthorizationResult(false, null, T("linkly.local.missingTxnRef", "ANZ Linkly transaction reference is missing."), TxnType: "R"))
            : RunTransactionAsync(
                TransactionType.Refund,
                amount,
                session,
                settings,
                normalizedRefundTxnRef,
                cancellationToken,
                requireTxnRefMatch: true);
    }

    public Task<PaymentAuthorizationResult> VoidAsync(
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        CancellationToken cancellationToken = default)
    {
        return RunTransactionAsync(
            TransactionType.Void,
            amount,
            session,
            settings,
            originalReference,
            cancellationToken);
    }

    private async Task<PaymentAuthorizationResult> RunTransactionAsync(
        TransactionType transactionType,
        decimal amount,
        PosSessionState session,
        CardTerminalSettings settings,
        string? originalReference,
        CancellationToken cancellationToken,
        bool requireTxnRefMatch = false)
    {
        if (amount <= 0m)
        {
            return new PaymentAuthorizationResult(false, null, T("linkly.local.amountMustBePositive", "Card amount must be greater than zero."), TxnType: ToResultTxnType(transactionType));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new PaymentAuthorizationResult(false, null, T("linkly.local.cancelled", CancelledMessage), TxnType: ToResultTxnType(transactionType));
        }

        using var timeoutCts = CreateBusinessWaitTimeoutToken(cancellationToken);
        var requestedAmount = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
        string txnRef;
        if (requireTxnRefMatch)
        {
            if (!LinklyLocalTxnRef.TryNormalizeHistoricalReference(originalReference, out txnRef))
            {
                return new PaymentAuthorizationResult(false, null, T("linkly.local.missingTxnRef", "ANZ Linkly transaction reference is missing."), TxnType: ToResultTxnType(transactionType));
            }
        }
        else if (transactionType == TransactionType.PurchaseCash)
        {
            txnRef = LinklyLocalTxnRef.Create('P', Guid.NewGuid().ToString("D"));
        }
        else if (transactionType == TransactionType.Refund)
        {
            // 中文注释：退款的原交易引用只保留给业务审计，Local IP 线路必须为本次退款生成新的 R 引用。
            txnRef = LinklyLocalTxnRef.Create('R', Guid.NewGuid().ToString("D"));
        }
        else if (originalReference is not null)
        {
            if (!LinklyLocalTxnRef.TryNormalizeHistoricalReference(originalReference, out txnRef))
            {
                return new PaymentAuthorizationResult(false, null, T("linkly.local.missingTxnRef", "ANZ Linkly transaction reference is missing."), TxnType: ToResultTxnType(transactionType));
            }
        }
        else
        {
            txnRef = BuildVoidTxnRef();
        }

        var request = CreateTransactionRequest(transactionType, requestedAmount, txnRef);
        var receipts = new List<string>();
        using var client = clientFactory.Create();
        var transactionRequestSent = false;
        Task<bool>? activeCancelKeyTask = null;

        try
        {
            var connectRequest = CreateConnectRequest(settings.LinklyHost, settings.LinklyPort);
            LogJson(
                "connect",
                "start",
                "request",
                settings.Environment,
                txnRef,
                request: connectRequest);
            var connected = await client.ConnectAsync(settings.LinklyHost, settings.LinklyPort, useSsl: false, useKeepAlive: false)
                .WaitAsync(timeoutCts.Token);
            LogJson(
                "connect",
                connected ? "succeeded" : "failed",
                "response",
                settings.Environment,
                txnRef,
                success: connected,
                request: connectRequest,
                response: new
                {
                    connected
                });
            if (!connected)
            {
                return new PaymentAuthorizationResult(
                    false,
                    null,
                    T("linkly.local.connectionFailed", "ANZ Linkly EFT-Client connection failed."),
                    StatusKey: "linkly.local.connectionFailed",
                    TxnType: ToResultTxnType(transactionType),
                    FallbackAllowed: true);
            }

            // 保留终端原始交易报文，方便把 POS 请求与终端回包按 TxnRef 串起来排查。
            LogJson(
                "transaction",
                "sent",
                "request",
                settings.Environment,
                txnRef,
                request: request);
            if (!await client.WriteRequestAsync(request).WaitAsync(timeoutCts.Token))
            {
                LogJson(
                    "transaction",
                    "failed",
                    "response",
                    settings.Environment,
                    txnRef,
                    success: false,
                    reason: "write-request-failed",
                    request: request);
                return new PaymentAuthorizationResult(
                    false,
                    null,
                    T("linkly.local.requestSendFailed", "ANZ Linkly transaction request could not be sent."),
                    StatusKey: "linkly.local.requestSendFailed",
                    TxnType: ToResultTxnType(transactionType),
                    FallbackAllowed: true);
            }

            transactionRequestSent = true;
            // 中文注释：取消必须在仍然存活的连接上发取消键。直接取消 SDK 的读取会释放底层 TcpClient，
            // 之后取消键发不出去（生产日志 "Cannot access a disposed object ... TcpClient"），
            // 终端会一直停在等待刷卡。因此读取只受业务超时约束；调用方取消只负责发取消键，终端随后回「已取消」结果。
            using var readTimeoutCts = CreateBusinessWaitTimeoutToken(CancellationToken.None);
            using var cancelKeyRegistration = cancellationToken.Register(() =>
                activeCancelKeyTask = SendCancelKeyDuringTransactionAsync(client, settings, txnRef));
            var response = await ReadTransactionResponseAsync(
                client,
                receipts,
                readTimeoutCts.Token,
                settings.Environment,
                txnRef,
                requestedAmount,
                transactionType);
            return ToAuthorizationResult(response, requestedAmount, txnRef, receipts, transactionType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogJson(
                "transaction",
                "failed",
                "response",
                settings.Environment,
                txnRef,
                success: false,
                reason: "caller-cancelled",
                request: request);
            if (transactionRequestSent)
            {
                return await TryCancelActiveTransactionAsync(
                    client,
                    settings,
                    requestedAmount,
                    txnRef,
                    receipts,
                    transactionType,
                    Volatile.Read(ref activeCancelKeyTask));
            }

            return new PaymentAuthorizationResult(false, null, T("linkly.local.cancelled", CancelledMessage), TxnType: ToResultTxnType(transactionType));
        }
        catch (Exception ex) when (ex is OperationCanceledException or ConnectionException)
        {
            var fallbackMessage = ex is OperationCanceledException
                ? T("linkly.local.timeout", "ANZ Linkly transaction timed out.")
                : T("linkly.local.connectionClosed", "ANZ Linkly connection was closed.");
            LogJson(
                "transaction",
                "failed",
                "response",
                settings.Environment,
                txnRef,
                success: false,
                reason: ex is OperationCanceledException ? "timeout" : "connection-closed",
                request: request,
                details: new
                {
                    exception = ex.GetType().Name,
                    ex.Message
                },
                exception: ex);

            if (!transactionRequestSent)
            {
                return new PaymentAuthorizationResult(
                    false,
                    null,
                    fallbackMessage,
                    StatusKey: ex is OperationCanceledException ? "linkly.local.timeout" : "linkly.local.connectionFailed",
                    TxnType: ToResultTxnType(transactionType),
                    FallbackAllowed: true);
            }

            return await TryRecoverLastTransactionAsync(
                settings,
                requestedAmount,
                txnRef,
                receipts,
                fallbackMessage,
                transactionType,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var fallbackMessage = string.Format(
                CultureInfo.CurrentCulture,
                T("linkly.local.transactionFailed", "ANZ Linkly transaction failed: {0}"),
                ex.Message);
            LogJson(
                "transaction",
                "failed",
                "response",
                settings.Environment,
                txnRef,
                success: false,
                reason: ex.GetType().Name,
                request: request,
                details: new
                {
                    ex.Message
                },
                exception: ex);
            if (transactionRequestSent)
            {
                return await TryRecoverLastTransactionAsync(
                    settings,
                    requestedAmount,
                    txnRef,
                    receipts,
                    fallbackMessage,
                    transactionType,
                    cancellationToken);
            }

            return new PaymentAuthorizationResult(false, null, fallbackMessage, TxnType: ToResultTxnType(transactionType));
        }
        finally
        {
            SafeDisconnect(client, txnRef);
        }
    }

    private async Task<bool> SendCancelKeyDuringTransactionAsync(
        ILinklyEftClient client,
        CardTerminalSettings settings,
        string txnRef)
    {
        var cancelRequest = CreateCancelRequest();
        LogJson(
            "cancel",
            "sent",
            "request",
            settings.Environment,
            txnRef,
            request: cancelRequest);
        try
        {
            return await client.SendCancelRequestAsync();
        }
        catch (Exception ex) when (ex is ConnectionException or ObjectDisposedException or InvalidOperationException)
        {
            // 发送失败按“取消键未送达”处理：交易结果仍未知，由调用方走原有的最近交易恢复。
            LogJson(
                "cancel",
                "failed",
                "response",
                settings.Environment,
                txnRef,
                success: false,
                reason: "connection-closed",
                request: cancelRequest,
                details: new { exception = ex.GetType().Name, ex.Message },
                exception: ex);
            return false;
        }
    }

    private async Task<PaymentAuthorizationResult> TryCancelActiveTransactionAsync(
        ILinklyEftClient client,
        CardTerminalSettings settings,
        decimal amount,
        string txnRef,
        IReadOnlyList<string> capturedReceipts,
        TransactionType transactionType,
        Task<bool>? cancelKeyAlreadySent = null)
    {
        var receipts = new List<string>(capturedReceipts);
        var fallbackMessage = T("linkly.local.cancelOutcomeUnknown", "ANZ Linkly cancellation outcome could not be confirmed.");
        using var cancelCts = CreateConfiguredTimeoutToken(settings.TerminalTimeout, CancellationToken.None);
        var cancelRequest = CreateCancelRequest();

        try
        {
            if (cancelKeyAlreadySent is null)
            {
                LogJson(
                    "cancel",
                    "sent",
                    "request",
                    settings.Environment,
                    txnRef,
                    request: cancelRequest);
            }

            // 取消键可能已在读取期间由取消回调发出，这里等它的结果，不能再发第二次。
            if (!await (cancelKeyAlreadySent ?? client.SendCancelRequestAsync()).WaitAsync(cancelCts.Token))
            {
                LogJson(
                    "cancel",
                    "failed",
                    "response",
                    settings.Environment,
                    txnRef,
                    success: false,
                    reason: "send-cancel-failed",
                    request: cancelRequest);
                return await TryRecoverLastTransactionAsync(
                    settings,
                    amount,
                    txnRef,
                    receipts,
                    fallbackMessage,
                    transactionType,
                    CancellationToken.None);
            }

            var response = await ReadTransactionResponseAsync(
                client,
                receipts,
                cancelCts.Token,
                settings.Environment,
                txnRef,
                amount,
                transactionType);
            return ToAuthorizationResult(response, amount, txnRef, receipts, transactionType);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ConnectionException)
        {
            LogJson(
                "cancel",
                "failed",
                "response",
                settings.Environment,
                txnRef,
                success: false,
                reason: ex is OperationCanceledException ? "timeout" : "connection-closed",
                request: cancelRequest,
                details: new
                {
                    exception = ex.GetType().Name,
                    ex.Message
                },
                exception: ex);
            return await TryRecoverLastTransactionAsync(
                settings,
                amount,
                txnRef,
                receipts,
                fallbackMessage,
                transactionType,
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogJson(
                "cancel",
                "failed",
                "response",
                settings.Environment,
                txnRef,
                success: false,
                reason: ex.GetType().Name,
                request: cancelRequest,
                details: new
                {
                    ex.Message
                },
                exception: ex);
            return await TryRecoverLastTransactionAsync(
                settings,
                amount,
                txnRef,
                receipts,
                fallbackMessage,
                transactionType,
                CancellationToken.None);
        }
    }

    private async Task<PaymentAuthorizationResult> TryRecoverLastTransactionAsync(
        CardTerminalSettings settings,
        decimal amount,
        string txnRef,
        IReadOnlyList<string> capturedReceipts,
        string fallbackMessage,
        TransactionType transactionType,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new PaymentAuthorizationResult(false, null, T("linkly.local.cancelled", CancelledMessage), TxnType: ToResultTxnType(transactionType));
        }

        using var timeoutCts = CreateConfiguredTimeoutToken(settings.TerminalTimeout, cancellationToken);
        using var client = clientFactory.Create();
        var receipts = new List<string>(capturedReceipts);
        var connectRequest = CreateConnectRequest(settings.LinklyHost, settings.LinklyPort);
        try
        {
            LogJson(
                "connect",
                "start",
                "request",
                settings.Environment,
                txnRef,
                request: connectRequest,
                details: new
                {
                    recovery = true
                });
            var connected = await client.ConnectAsync(settings.LinklyHost, settings.LinklyPort, useSsl: false, useKeepAlive: false)
                .WaitAsync(timeoutCts.Token);
            LogJson(
                "connect",
                connected ? "succeeded" : "failed",
                "response",
                settings.Environment,
                txnRef,
                success: connected,
                request: connectRequest,
                response: new
                {
                    connected
                },
                details: new
                {
                    recovery = true
                });
            if (!connected)
            {
                return new PaymentAuthorizationResult(false, null, fallbackMessage, TxnType: ToResultTxnType(transactionType), ResultUnknown: true);
            }

            var request = new EFTGetLastTransactionRequest(txnRef)
            {
                Application = TerminalApplication.EFTPOS,
                Merchant = Merchant
            };
            LogJson(
                "get-last-transaction",
                "sent",
                "request",
                settings.Environment,
                txnRef,
                request: request);
            if (!await client.WriteRequestAsync(request).WaitAsync(timeoutCts.Token))
            {
                LogJson(
                    "get-last-transaction",
                    "failed",
                    "response",
                    settings.Environment,
                    txnRef,
                    success: false,
                    reason: "write-request-failed",
                    request: request);
                return new PaymentAuthorizationResult(false, null, fallbackMessage, TxnType: ToResultTxnType(transactionType), ResultUnknown: true);
            }

            while (true)
            {
                var response = await client.ReadResponseAsync(timeoutCts.Token);
                switch (response)
                {
                    case EFTReceiptResponse receipt:
                        CaptureReceipt(receipts, receipt);
                        LogJson(
                            "receipt",
                            "received",
                            "response",
                            settings.Environment,
                            txnRef,
                            response: receipt,
                            details: new
                            {
                                recovery = true
                            });
                        break;
                    case EFTGetLastTransactionResponse last:
                        LogJson(
                            "get-last-transaction",
                            "received",
                            "response",
                            settings.Environment,
                            txnRef,
                            success: last.Success && last.LastTransactionSuccess,
                            request: request,
                            response: last);
                        return ToAuthorizationResult(last, amount, txnRef, receipts, transactionType);
                    case EFTTransactionResponse transaction:
                        LogJson(
                            "transaction",
                            "received",
                            "response",
                            settings.Environment,
                            txnRef,
                            success: transaction.Success,
                            request: request,
                            response: transaction,
                            details: new
                            {
                                recovery = true
                            });
                        return ToAuthorizationResult(transaction, amount, txnRef, receipts, transactionType);
                    case null:
                        LogJson(
                            "get-last-transaction",
                            "failed",
                            "response",
                            settings.Environment,
                            txnRef,
                            success: false,
                            reason: "empty-response",
                            request: request);
                        return new PaymentAuthorizationResult(false, null, fallbackMessage, TxnType: ToResultTxnType(transactionType), ResultUnknown: true);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogJson(
                "get-last-transaction",
                "failed",
                "response",
                settings.Environment,
                txnRef,
                success: false,
                reason: "caller-cancelled");
            return new PaymentAuthorizationResult(false, null, T("linkly.local.cancelled", CancelledMessage), TxnType: ToResultTxnType(transactionType), ResultUnknown: true);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ConnectionException)
        {
            LogJson(
                "get-last-transaction",
                "failed",
                "response",
                settings.Environment,
                txnRef,
                success: false,
                reason: ex is OperationCanceledException ? "timeout" : "connection-closed",
                details: new
                {
                    exception = ex.GetType().Name,
                    ex.Message
                },
                exception: ex);
            return new PaymentAuthorizationResult(false, null, fallbackMessage, TxnType: ToResultTxnType(transactionType), ResultUnknown: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogJson(
                "get-last-transaction",
                "failed",
                "response",
                settings.Environment,
                txnRef,
                success: false,
                reason: ex.GetType().Name,
                details: new
                {
                    ex.Message
                },
                exception: ex);
            return new PaymentAuthorizationResult(false, null, fallbackMessage, TxnType: ToResultTxnType(transactionType), ResultUnknown: true);
        }
        finally
        {
            SafeDisconnect(client, txnRef);
        }
    }

    private async Task<EFTTransactionResponse> ReadTransactionResponseAsync(
        ILinklyEftClient client,
        List<string> receipts,
        CancellationToken cancellationToken,
        CardTerminalEnvironment environment,
        string txnRef,
        decimal requestedAmount,
        TransactionType transactionType)
    {
        var approvalEvidenceRecorded = false;
        while (true)
        {
            var response = await client.ReadResponseAsync(cancellationToken);
            switch (response)
            {
                case EFTReceiptResponse receipt:
                    CaptureReceipt(receipts, receipt);
                    LogJson(
                        "receipt",
                        "received",
                        "response",
                        environment,
                        txnRef,
                        response: receipt);
                    // 中文注释：小票一到就把“已批准”证据落到本地 attempt；最终响应可能回 PF/空 TxnRef，
                    // POS 也可能在这之后断电，重启恢复时只能凭这份证据建单。
                    if (!approvalEvidenceRecorded)
                    {
                        approvalEvidenceRecorded = await TryRecordReceiptApprovalEvidenceAsync(
                            receipts,
                            environment,
                            txnRef,
                            requestedAmount,
                            transactionType);
                    }

                    break;
                case EFTTransactionResponse transaction:
                    LogJson(
                        "transaction",
                        "received",
                        "response",
                        environment,
                        txnRef,
                        success: transaction.Success,
                        response: transaction);
                    return transaction;
                case null:
                    LogJson(
                        "transaction",
                        "failed",
                        "response",
                        environment,
                        txnRef,
                        success: false,
                        reason: "empty-response");
                    throw new InvalidOperationException(T("linkly.local.emptyResponse", "ANZ Linkly returned an empty response."));
            }
        }
    }

    private static EFTTransactionRequest CreateTransactionRequest(
        TransactionType transactionType,
        decimal amount,
        string txnRef)
    {
        return new EFTTransactionRequest
        {
            TxnType = transactionType,
            AmtPurchase = decimal.Round(amount, 2, MidpointRounding.AwayFromZero),
            AmtCash = 0m,
            TxnRef = txnRef,
            Application = TerminalApplication.EFTPOS,
            Merchant = Merchant,
            ReceiptAutoPrint = ReceiptPrintModeType.POSPrinter
        };
    }

    private PaymentAuthorizationResult ToAuthorizationResult(
        EFTTransactionResponse response,
        decimal requestedAmount,
        string requestedTxnRef,
        IReadOnlyList<string> receipts,
        TransactionType transactionType)
    {
        var amount = response.AmtPurchase == 0m ? (decimal?)null : response.AmtPurchase;
        var returnedTxnRef = LinklyLocalTxnRef.TrimProtocolPadding(response.TxnRef);
        if (IsPinpadOfflineRejection(response.Success, response.ResponseCode, returnedTxnRef, response.AmtPurchase, receipts))
        {
            // 中文注释：EFT-Client 连不上 PIN pad 时立即回 PF、空 TxnRef、金额 0，交易从未进入刷卡机，
            // 属于确定未提交；不能因 TxnRef 对不上而误判为结果未知，否则付款页会要求“恢复上一笔”。
            return new PaymentAuthorizationResult(
                false,
                null,
                T("linkly.local.pinpadOffline", "The card terminal (PINpad) is offline. No payment was taken."),
                TxnType: ToResultTxnType(transactionType),
                ResponseCode: NormalizeOptional(response.ResponseCode),
                ResponseText: NormalizeOptional(response.ResponseText),
                StatusKey: "linkly.local.pinpadOffline");
        }

        var referenceMatches = string.Equals(returnedTxnRef, requestedTxnRef, StringComparison.Ordinal);
        var amountMatches = response.Success
            ? response.AmtPurchase == requestedAmount
            : response.AmtPurchase == 0m || response.AmtPurchase == requestedAmount;
        var transactionTypeMatches = transactionType is not (TransactionType.PurchaseCash or TransactionType.Refund) ||
            response.TxnType == transactionType;
        if (!referenceMatches || !amountMatches || !transactionTypeMatches)
        {
            // 中文注释：生产实测（10-22 1002 店）：顾客小票已打出 APPROVED 00，最终响应却回 PF、空 TxnRef、金额 0。
            // 只在“未回显任何引用”且小票证据与本笔 TxnRef/金额一致时按已批准处理；回显了别的引用仍按身份冲突走未知。
            if (!response.Success &&
                string.IsNullOrEmpty(returnedTxnRef) &&
                TryCreateReceiptApprovedResult(
                    receipts,
                    requestedTxnRef,
                    requestedAmount,
                    transactionType,
                    "transaction",
                    response.ResponseCode,
                    response.ResponseText) is { } receiptApproved)
            {
                return receiptApproved;
            }

            return new PaymentAuthorizationResult(
                false,
                null,
                T("linkly.local.recoveryFailed", "ANZ Linkly recovery could not confirm the previous transaction."),
                TxnType: ToResultTxnType(transactionType),
                ResultUnknown: true);
        }

        var txnRef = returnedTxnRef ?? requestedTxnRef;
        var transaction = ToCardTransaction(response, amount, txnRef, receipts);
        return response.Success
            ? new PaymentAuthorizationResult(true, $"ANZ:{txnRef}", "ANZ Linkly", amount, [transaction], ProcessorName, TxnType: ToResultTxnType(transactionType), TxnRef: txnRef, ResponseCode: transaction.ResponseCode, ResponseText: transaction.ResponseText)
            : new PaymentAuthorizationResult(false, $"ANZ:{txnRef}", FormatResponseMessage(response.ResponseText, response.ResponseCode), amount, [transaction], ProcessorName, TxnType: ToResultTxnType(transactionType), TxnRef: txnRef, ResponseCode: transaction.ResponseCode, ResponseText: transaction.ResponseText);
    }

    private PaymentAuthorizationResult ToAuthorizationResult(
        EFTGetLastTransactionResponse response,
        decimal requestedAmount,
        string requestedTxnRef,
        IReadOnlyList<string> receipts,
        TransactionType transactionType)
    {
        var amount = response.AmtPurchase == 0m ? (decimal?)null : response.AmtPurchase;
        var returnedTxnRef = LinklyLocalTxnRef.TrimProtocolPadding(response.TxnRef);
        var referenceMatches = string.Equals(returnedTxnRef, requestedTxnRef, StringComparison.Ordinal);
        var amountMatches = response.LastTransactionSuccess
            ? response.AmtPurchase == requestedAmount
            : response.AmtPurchase == 0m || response.AmtPurchase == requestedAmount;
        var transactionTypeMatches = transactionType is not (TransactionType.PurchaseCash or TransactionType.Refund) ||
            response.TxnType == transactionType;
        if (!response.Success || !referenceMatches || !amountMatches || !transactionTypeMatches)
        {
            // 中文注释：本次交易过程中已收到与本笔 TxnRef/金额一致的批准小票，而 GetLast 没有回答这笔
            // （生产实测 JH / NOT FOUND、空 TxnRef，或刷卡机离线）：小票就是批准证据，按已批准处理。
            if (string.IsNullOrEmpty(returnedTxnRef) &&
                TryCreateReceiptApprovedResult(
                    receipts,
                    requestedTxnRef,
                    requestedAmount,
                    transactionType,
                    "get-last-transaction",
                    response.ResponseCode,
                    response.ResponseText) is { } receiptApproved)
            {
                return receiptApproved;
            }

            // 中文注释：查询上一笔时刷卡机离线，上一笔仍无法核对，必须保持结果未知；只把原因说清楚。
            var pinpadOffline = IsPinpadOfflineRejection(
                response.Success,
                response.ResponseCode,
                returnedTxnRef,
                response.AmtPurchase,
                receipts);
            // 中文注释：EFT-Client 正常回答了（Success=true），只是回包不是被查的这笔（引用对不上，如 JH / NOT FOUND）。
            // 仍保持 ResultUnknown，但打上专用标记，让主管结案的实时核验能与“刷卡机没回答”区分；
            // 引用对得上而金额/类型冲突的回包不打标记，仍按身份冲突失败关闭。
            var answeredButNotThisTransaction = response.Success && !referenceMatches;
            return new PaymentAuthorizationResult(
                false,
                null,
                pinpadOffline
                    ? T("linkly.local.recoveryPinpadOffline", "The card terminal (PINpad) is offline, so the previous transaction could not be checked. Reconnect the card terminal and try again.")
                    : T("linkly.local.recoveryFailed", "ANZ Linkly recovery could not confirm the previous transaction."),
                TxnType: ToResultTxnType(transactionType),
                StatusKey: answeredButNotThisTransaction ? LinklyLocalTxnRef.LastTransactionNotMatchedStatusKey : null,
                ResultUnknown: true);
        }

        var txnRef = returnedTxnRef ?? requestedTxnRef;
        var transaction = ToCardTransaction(response, amount, txnRef, receipts);
        if (response.LastTransactionSuccess)
        {
            return new PaymentAuthorizationResult(true, $"ANZ:{txnRef}", "ANZ Linkly", amount, [transaction], ProcessorName, TxnType: ToResultTxnType(transactionType), TxnRef: txnRef, ResponseCode: transaction.ResponseCode, ResponseText: transaction.ResponseText);
        }

        return HasExplicitLinklyRejection(response.ResponseCode, response.ResponseText)
            ? new PaymentAuthorizationResult(false, $"ANZ:{txnRef}", FormatResponseMessage(response.ResponseText, response.ResponseCode), amount, [transaction], ProcessorName, TxnType: ToResultTxnType(transactionType), TxnRef: txnRef, ResponseCode: transaction.ResponseCode, ResponseText: transaction.ResponseText)
            : new PaymentAuthorizationResult(false, null, T("linkly.local.recoveryFailed", "ANZ Linkly recovery could not confirm the previous transaction."), TxnType: ToResultTxnType(transactionType), ResultUnknown: true);
    }

    private static CardTransactionDto ToCardTransaction(
        EFTTransactionResponse response,
        decimal? amount,
        string txnRef,
        IReadOnlyList<string> receipts)
    {
        return new CardTransactionDto(
            ProcessorName,
            txnRef,
            FormatPositiveInt(response.AuthCode),
            NormalizeOptional(response.CardType),
            PositiveIntOrNull(response.CardName),
            MaskCardNumber(response.Pan),
            NormalizeOptional(response.Caid),
            NormalizeOptional(response.ResponseCode),
            NormalizeOptional(response.ResponseText),
            FormatPositiveInt(response.Stan),
            ToDateTimeOffsetOrNull(response.DateSettlement),
            amount is decimal value ? decimal.Round(value, 2, MidpointRounding.AwayFromZero) : 0m,
            JoinReceipts(receipts));
    }

    private async Task<EFTSettlementResponse> ReadSettlementResponseAsync(
        ILinklyEftClient client,
        ICollection<string> receipts,
        CancellationToken cancellationToken,
        CardTerminalEnvironment environment,
        string operationId)
    {
        while (true)
        {
            var response = await client.ReadResponseAsync(cancellationToken);
            switch (response)
            {
                case EFTReceiptResponse receipt when receipt.Type == ReceiptType.Settlement:
                    CaptureReceipt(receipts, receipt);
                    LogJson(
                        "settlement-receipt",
                        "received",
                        "response",
                        environment,
                        operationId,
                        response: receipt,
                        details: BuildSettlementReceiptLogDetails(receipt));
                    break;
                case EFTReceiptResponse receipt:
                    // 仅日志：非 Settlement 类型的回单仍不进入结算记录，但类型、行数和脱敏全文都要留痕，
                    // 用于事后判断 ANZ 小票里的 GRAND TOTALS 段是 Linkly 没发，还是被当作其他类型回单丢弃。
                    LogJson(
                        "settlement-receipt",
                        "ignored",
                        "response",
                        environment,
                        operationId,
                        reason: "non-settlement-receipt",
                        response: receipt,
                        details: BuildSettlementReceiptLogDetails(receipt));
                    break;
                case EFTSettlementResponse settlement:
                    LogJson("settlement", "received", "response", environment, operationId, success: settlement.Success, response: settlement);
                    return settlement;
                case null:
                    throw new InvalidOperationException(T("linkly.local.emptyResponse", "ANZ Linkly returned an empty response."));
            }
        }
    }

    /// <summary>
    /// 结算回单日志的补充字段：回单类型、行数，以及经 <see cref="LinklyReceiptTextSanitizer"/> 脱敏后的整张回单文本。
    /// 原始回单可能含卡号，绝不能直接写日志；这里只输出脱敏结果，且不影响回单是否入库的判断。
    /// </summary>
    private static object BuildSettlementReceiptLogDetails(EFTReceiptResponse receipt)
    {
        var lines = receipt.ReceiptText ?? [];
        return new
        {
            receiptType = receipt.Type.ToString(),
            lineCount = lines.Length,
            sanitizedText = LinklyReceiptTextSanitizer.Sanitize(string.Join(Environment.NewLine, lines))
        };
    }

    private async Task<bool> TryRecordReceiptApprovalEvidenceAsync(
        IReadOnlyList<string> receipts,
        CardTerminalEnvironment environment,
        string txnRef,
        decimal requestedAmount,
        TransactionType transactionType)
    {
        if (transactionType != TransactionType.PurchaseCash ||
            attemptContextAccessor?.Current is not { RecordReceiptApprovalEvidenceAsync: { } record } context ||
            !string.Equals(LinklyLocalTxnRef.TrimProtocolPadding(context.TxnRef), txnRef, StringComparison.Ordinal) ||
            !LinklyLocalReceiptApprovalEvidence.TryParse(receipts, txnRef, requestedAmount, out var evidence) ||
            evidence is null)
        {
            return false;
        }

        try
        {
            // 落库失败不能影响读取终端最终响应；证据仍在内存小票里，最终映射照样可用。
            await record(evidence, CancellationToken.None);
            LogJson(
                "receipt-approval-evidence",
                "recorded",
                "response",
                environment,
                txnRef,
                success: true,
                details: BuildReceiptApprovalEvidenceLogDetails(evidence));
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            LogJson(
                "receipt-approval-evidence",
                "failed",
                "response",
                environment,
                txnRef,
                success: false,
                reason: ex.GetType().Name,
                details: new { ex.Message },
                exception: ex);
            return false;
        }
    }

    private PaymentAuthorizationResult? TryCreateReceiptApprovedResult(
        IReadOnlyList<string> receipts,
        string requestedTxnRef,
        decimal requestedAmount,
        TransactionType transactionType,
        string source,
        string? terminalResponseCode,
        string? terminalResponseText)
    {
        // 只覆盖普通消费：小票格式按 SALE AUD $金额 解析；退款/撤销/提现仍走原有身份核对。
        if (transactionType != TransactionType.PurchaseCash ||
            !LinklyLocalReceiptApprovalEvidence.TryParse(receipts, requestedTxnRef, requestedAmount, out var evidence) ||
            evidence is null)
        {
            return null;
        }

        LogJson(
            "receipt-approval-evidence",
            "applied",
            "response",
            sessionId: requestedTxnRef,
            success: true,
            reason: source,
            details: new
            {
                evidence = BuildReceiptApprovalEvidenceLogDetails(evidence),
                terminalResponseCode = NormalizeOptional(terminalResponseCode),
                terminalResponseText = NormalizeOptional(terminalResponseText)
            });
        return CreateReceiptApprovedResult(evidence, receipts, transactionType);
    }

    // 中文注释：凭小票证据构造的批准结果，与正常批准同形（Reference=ANZ:TxnRef、响应码 00），
    // 工作流与恢复流程无需区分来源即可正常建单；恢复服务也复用这里构造落库证据的批准结果。
    internal static PaymentAuthorizationResult CreateReceiptApprovedResult(
        LinklyLocalReceiptApprovalEvidence evidence,
        IReadOnlyList<string>? receipts,
        TransactionType transactionType)
    {
        var transaction = new CardTransactionDto(
            ProcessorName,
            evidence.TxnRef,
            evidence.ApprovalCode,
            null,
            null,
            null,
            null,
            evidence.ResponseCode,
            "APPROVED",
            evidence.Stan,
            null,
            evidence.Amount,
            receipts is { Count: > 0 } ? JoinReceipts(receipts) : null);
        return new PaymentAuthorizationResult(
            true,
            $"ANZ:{evidence.TxnRef}",
            "ANZ Linkly",
            evidence.Amount,
            [transaction],
            ProcessorName,
            TxnType: ToResultTxnType(transactionType),
            TxnRef: evidence.TxnRef,
            ResponseCode: transaction.ResponseCode,
            ResponseText: transaction.ResponseText);
    }

    internal static PaymentAuthorizationResult CreateReceiptApprovedSaleResult(LinklyLocalReceiptApprovalEvidence evidence) =>
        CreateReceiptApprovedResult(evidence, null, TransactionType.PurchaseCash);

    private static object BuildReceiptApprovalEvidenceLogDetails(LinklyLocalReceiptApprovalEvidence evidence) => new
    {
        evidence.Rrn,
        evidence.ApprovalCode,
        evidence.Stan,
        evidence.Invoice,
        evidence.Amount,
        evidence.ResponseCode
    };

    private static bool IsPinpadOfflineRejection(
        bool success,
        string? responseCode,
        string? returnedTxnRef,
        decimal amountPurchase,
        IReadOnlyCollection<string> receipts)
    {
        // 中文注释：只认 EFT-Client 的“PINpad Offline”即时拒绝形态（PF、未回显 TxnRef、金额 0、无小票）；
        // 任何带回显引用、金额或小票的回包都可能是刷卡中途断线，仍按身份核对规则走结果未知。
        return !success &&
            string.Equals(responseCode?.Trim(), PinpadOfflineResponseCode, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrEmpty(returnedTxnRef) &&
            amountPurchase == 0m &&
            receipts.Count == 0;
    }

    private static bool HasExplicitLinklyRejection(string? responseCode, string? responseText)
    {
        // 中文注释：TI = OPERATOR TIMEOUT（顾客/收银员没在时限内操作刷卡机，交易未授权、未扣款）。
        // 能走到这里说明 GetLast 的 TxnRef、金额都对得上这一笔且 LastTransactionSuccess=false，
        // 终端已经明确回答“这笔没成功”；若仍当结果未知，主管结案的实时核验会永久失败关闭（1014 店 10-24 实测）。
        if (string.Equals(responseCode?.Trim(), "05", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(responseCode?.Trim(), "C0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(responseCode?.Trim(), "CN", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(responseCode?.Trim(), "TI", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(responseText) &&
            (responseText.Contains("DECLINED", StringComparison.OrdinalIgnoreCase) ||
             responseText.Contains("REJECTED", StringComparison.OrdinalIgnoreCase) ||
             responseText.Contains("CANCELLED", StringComparison.OrdinalIgnoreCase) ||
             responseText.Contains("CANCELED", StringComparison.OrdinalIgnoreCase) ||
             responseText.Contains("OPERATOR TIMEOUT", StringComparison.OrdinalIgnoreCase));
    }

    private static CardTransactionDto ToCardTransaction(
        EFTGetLastTransactionResponse response,
        decimal? amount,
        string txnRef,
        IReadOnlyList<string> receipts)
    {
        return new CardTransactionDto(
            ProcessorName,
            txnRef,
            FormatPositiveInt(response.AuthCode),
            NormalizeOptional(response.CardType),
            PositiveIntOrNull(response.CardName),
            MaskCardNumber(response.Pan),
            NormalizeOptional(response.Caid),
            NormalizeOptional(response.ResponseCode),
            NormalizeOptional(response.ResponseText),
            FormatPositiveInt(response.Stan),
            ToDateTimeOffsetOrNull(response.DateSettlement),
            amount is decimal value ? decimal.Round(value, 2, MidpointRounding.AwayFromZero) : 0m,
            JoinReceipts(receipts));
    }

    private static void CaptureReceipt(ICollection<string> receipts, EFTReceiptResponse receipt)
    {
        if (receipt.ReceiptText is not { Length: > 0 } receiptText)
        {
            return;
        }

        receipts.Add(string.Join(Environment.NewLine, receiptText));
    }

    private LinklyLogonResult CreateUnknownLogonResult(IReadOnlyList<string> receipts)
    {
        return new LinklyLogonResult(
            false,
            T(
                "linkly.local.logon.outcomeUnknown",
                "Linkly bank network logon result is unknown. Check the PINpad before retrying."),
            ReceiptTexts: receipts,
            ResultUnknown: true);
    }

    private async Task TryCancelLogonAsync(ILinklyEftClient client, TimeSpan timeout)
    {
        using var cancelCts = CreateConfiguredTimeoutToken(timeout, CancellationToken.None);
        var cancelRequest = CreateCancelRequest();
        try
        {
            LogJson("logon-cancel", "sent", "request", request: cancelRequest);
            var sent = await client.SendCancelRequestAsync().WaitAsync(cancelCts.Token);
            LogJson(
                "logon-cancel",
                sent ? "succeeded" : "failed",
                "response",
                success: sent,
                reason: sent ? null : "send-cancel-failed",
                request: cancelRequest);
        }
        catch (Exception ex)
        {
            LogJson(
                "logon-cancel",
                "failed",
                "response",
                success: false,
                reason: ex.GetType().Name,
                request: cancelRequest,
                details: new { ex.Message },
                exception: ex);
        }
    }

    private static CancellationTokenSource CreateBusinessWaitTimeoutToken(CancellationToken cancellationToken)
    {
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(LinklyTimeoutPolicy.BusinessWait);
        return timeoutCts;
    }

    private static CancellationTokenSource CreateConfiguredTimeoutToken(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout <= TimeSpan.Zero ? LinklyTimeoutPolicy.BusinessWait : timeout);
        return timeoutCts;
    }

    internal static string BuildTxnRef(PosSessionState session)
    {
        var device = new string(session.DeviceCode.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(device))
        {
            device = "POS";
        }

        // 中文注释：Settlement 日志使用独立的设备加 UTC 时间 operation ID，不承担线路 TxnRef 的 16 位协议语义。
        return $"{device}{DateTimeOffset.UtcNow:yyMMddHHmmssfff}";
    }

    private static string BuildVoidTxnRef()
    {
        // 中文注释：Void 只需要一条可传输的引用，不借用 P/R 的身份哈希语义。
        return $"V{Guid.NewGuid():N}"[..16].ToUpperInvariant();
    }

    private static string? NormalizeReference(string? reference)
    {
        return LinklyLocalTxnRef.TryNormalizeHistoricalReference(reference, out var normalized)
            ? normalized
            : null;
    }

    private static string? ToResultTxnType(TransactionType transactionType)
    {
        return transactionType switch
        {
            TransactionType.PurchaseCash => "P",
            TransactionType.Refund => "R",
            _ => null
        };
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? FormatPositiveInt(int value)
    {
        return value > 0 ? value.ToString(CultureInfo.InvariantCulture) : null;
    }

    private static int? PositiveIntOrNull(int value)
    {
        return value > 0 ? value : null;
    }

    private static DateTimeOffset? ToDateTimeOffsetOrNull(DateTime value)
    {
        return value == default
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private static string? MaskCardNumber(string? pan)
    {
        return LinklyReceiptTextSanitizer.SanitizeCardNumber(pan);
    }

    private static string? JoinReceipts(IReadOnlyList<string> receipts)
    {
        return receipts.Count == 0 ? null : string.Join(Environment.NewLine + Environment.NewLine, receipts);
    }

    private string FormatResponseMessage(string? responseText, string? responseCode)
    {
        var text = NormalizeOptional(responseText);
        var code = NormalizeOptional(responseCode);
        if (text is null && code is null)
        {
            return T("linkly.local.declined", "ANZ Linkly transaction was declined.");
        }

        return code is null ? text! : $"{text ?? T("linkly.local.declined", "ANZ Linkly transaction was declined.")} ({code})";
    }

    private string T(string key, string fallback)
    {
        var value = localization?.T(key);
        return string.IsNullOrWhiteSpace(value) || value == $"[[{key}]]" ? fallback : value;
    }

    private void LogJson(
        string operation,
        string phase,
        string? direction = null,
        CardTerminalEnvironment? environment = null,
        string? sessionId = null,
        bool? success = null,
        string? reason = null,
        object? request = null,
        object? response = null,
        object? details = null,
        Exception? exception = null)
    {
        try
        {
            LinklyJsonLog.Write(
                LogCategory,
                LogSource,
                operation,
                phase,
                direction,
                environment,
                sessionId,
                success: success,
                reason: reason,
                request: NormalizeLogPayload(request),
                response: NormalizeLogPayload(response),
                details: details,
                exception: exception);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConsoleLog.WriteError(
                "LinklyLocal",
                $"linkly json log failed error={ex.GetType().Name} message={ex.Message}",
                exception: ex);
            // 日志仅用于诊断，不能影响刷卡主流程。
        }
    }

    private static object? NormalizeLogPayload(object? payload)
    {
        return payload switch
        {
            null => null,
            EFTTransactionRequest request => new
            {
                request.TxnType,
                request.AmtPurchase,
                request.AmtCash,
                request.TxnRef,
                request.Application,
                request.Merchant,
                request.ReceiptAutoPrint
            },
            EFTLogonRequest request => new
            {
                request.Merchant,
                request.LogonType,
                request.Application,
                request.ReceiptAutoPrint,
                request.CutReceipt
            },
            EFTSettlementRequest request => new
            {
                request.SettlementType,
                request.Application,
                request.Merchant,
                request.ReceiptAutoPrint,
                request.CutReceipt
            },
            EFTGetLastTransactionRequest request => new
            {
                request.TxnRef,
                request.Application,
                request.Merchant
            },
            EFTSendKeyRequest request => new
            {
                request.Key
            },
            EFTReceiptResponse response => new
            {
                // 补充回单类型与行数；ReceiptText 在 LinklyJsonLog 中逐行脱敏，不会泄露完整卡号。
                Type = response.Type.ToString(),
                LineCount = response.ReceiptText?.Length ?? 0,
                response.ReceiptText
            },
            EFTTransactionResponse response => new
            {
                response.Success,
                response.TxnRef,
                response.AmtPurchase,
                response.ResponseCode,
                response.ResponseText,
                response.AuthCode,
                response.CardType,
                response.Pan,
                response.Caid,
                response.Stan,
                response.CardName,
                response.DateSettlement
            },
            EFTLogonResponse response => new
            {
                response.Success,
                response.ResponseCode,
                response.ResponseText,
                response.PinPadVersion,
                response.Catid,
                response.Caid,
                response.Stan,
                response.Date
            },
            EFTSettlementResponse response => new
            {
                response.Success,
                response.ResponseCode,
                response.ResponseText,
                response.Merchant,
                response.SettlementData
            },
            EFTGetLastTransactionResponse response => new
            {
                response.Success,
                response.LastTransactionSuccess,
                response.TxnRef,
                response.AmtPurchase,
                response.ResponseCode,
                response.ResponseText,
                response.AuthCode,
                response.CardType,
                response.Pan,
                response.Caid,
                response.Stan,
                response.CardName,
                response.DateSettlement
            },
            _ => payload
        };
    }

    private static object CreateConnectRequest(string host, int port)
    {
        return new
        {
            host,
            port,
            useSsl = false,
            useKeepAlive = false
        };
    }

    private static EFTSendKeyRequest CreateCancelRequest()
    {
        return new EFTSendKeyRequest
        {
            Key = EFTPOSKey.OkCancel
        };
    }

    private void SafeDisconnect(ILinklyEftClient client, string? txnRef)
    {
        try
        {
            var disconnected = client.Disconnect();
            LogJson(
                "disconnect",
                disconnected ? "succeeded" : "failed",
                "response",
                sessionId: txnRef,
                success: disconnected,
                response: new
                {
                    disconnected
                });
        }
        catch (Exception ex)
        {
            LogJson(
                "disconnect",
                "failed",
                "response",
                sessionId: txnRef,
                success: false,
                reason: ex.GetType().Name,
                details: new
                {
                    ex.Message
                },
                exception: ex);
        }
    }
}
