using System.Collections.Concurrent;
using System.Globalization;
using System.Diagnostics;
using static Hbpos.Contracts.Linkly.LinklyCloudBackendStatusConstants;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Hbpos.Api.Data;
using Hbpos.Contracts.Linkly;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace Hbpos.Api.Services;

public interface ILinklyCloudBackendAsyncService
{
    Task<LinklyCloudBackendSessionResponse> StartTransactionAsync(
        string storeCode,
        string deviceCode,
        LinklyCloudBackendTransactionRequest request,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendSessionResponse> StartSettlementAsync(
        string storeCode,
        string deviceCode,
        LinklyCloudBackendSettlementRequest request,
        CancellationToken cancellationToken) => throw new NotSupportedException("Linkly Cloud backend settlement is not supported by this implementation.");

    Task<LinklyCloudBackendSessionResponse?> GetStatusAsync(
        string storeCode,
        string deviceCode,
        string environment,
        string sessionId,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendSessionResponse?> GetSettlementStatusAsync(
        string storeCode,
        string deviceCode,
        string environment,
        string sessionId,
        CancellationToken cancellationToken) => Task.FromResult<LinklyCloudBackendSessionResponse?>(null);

    Task<LinklyCloudBackendSessionResponse?> GetActiveSessionAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendSessionResponse?> GetResumableSessionAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendSessionResponse?> GetResumableSettlementSessionAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken) => Task.FromResult<LinklyCloudBackendSessionResponse?>(null);

    Task<LinklyCloudBackendHealthResponse> GetHealthAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendHealthResponse> GetHealthAsync(
        string storeCode,
        string deviceCode,
        string environment,
        Guid? terminalId,
        long? selectionRevision,
        CancellationToken cancellationToken) =>
        GetHealthAsync(storeCode, deviceCode, environment, cancellationToken);

    Task<LinklyCloudBackendLogonTestResponse> RunLogonTestAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendLogonTestResponse> RunLogonTestAsync(
        string storeCode,
        string deviceCode,
        string environment,
        Guid? terminalId,
        long? selectionRevision,
        CancellationToken cancellationToken) =>
        RunLogonTestAsync(storeCode, deviceCode, environment, cancellationToken);

    Task<LinklyCloudBackendStatusTestResponse> RunStatusTestAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendStatusTestResponse> RunStatusTestAsync(
        string storeCode,
        string deviceCode,
        string environment,
        Guid? terminalId,
        long? selectionRevision,
        CancellationToken cancellationToken) =>
        RunStatusTestAsync(storeCode, deviceCode, environment, cancellationToken);

    Task<LinklyCloudBackendTerminalCredentialResponse> UpsertTerminalCredentialAsync(
        string storeCode,
        string deviceCode,
        LinklyCloudBackendTerminalCredentialUpsertRequest request,
        string? updatedBy,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendSessionResponse> RecoverAsync(
        string storeCode,
        string deviceCode,
        string sessionId,
        LinklyCloudBackendRecoverRequest request,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendSessionResponse> SendKeyAsync(
        string storeCode,
        string deviceCode,
        string sessionId,
        LinklyCloudBackendSendKeyRequest request,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendSessionResponse> MarkReceiptPrintedAsync(
        string storeCode,
        string deviceCode,
        string sessionId,
        LinklyCloudBackendMarkReceiptPrintedRequest request,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendSessionResponse> MarkSettlementReceiptPrintedAsync(
        string storeCode,
        string deviceCode,
        string sessionId,
        LinklyCloudBackendMarkReceiptPrintedRequest request,
        CancellationToken cancellationToken) => throw new NotSupportedException("Linkly Cloud backend settlement is not supported by this implementation.");

    Task<LinklyCloudBackendSessionResponse> AcknowledgeSessionAsync(
        string storeCode,
        string deviceCode,
        string environment,
        string sessionId,
        bool supervisorResolved,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendSessionResponse> AcknowledgeSettlementSessionAsync(
        string storeCode,
        string deviceCode,
        string environment,
        string sessionId,
        bool supervisorResolved,
        CancellationToken cancellationToken) => throw new NotSupportedException("Linkly Cloud backend settlement is not supported by this implementation.");

    Task ReceiveNotificationAsync(
        string environment,
        string sessionId,
        string type,
        string? authorizationHeader,
        JsonElement payload,
        CancellationToken cancellationToken);
}

public sealed class LinklyCloudBackendAsyncOptions
{
    public string? ProductionNotificationBearer { get; set; }

    public string? SandboxNotificationBearer { get; set; }

    public string? PublicNotificationBaseUrl { get; set; }

    public string ProductionAuthBaseUrl { get; set; } = "https://auth.cloud.pceftpos.com/v1/";

    public string SandboxAuthBaseUrl { get; set; } = "https://auth.sandbox.cloud.pceftpos.com/v1/";

    public string ProductionRestBaseUrl { get; set; } = "https://rest.pos.cloud.pceftpos.com/v1/";

    public string SandboxRestBaseUrl { get; set; } = "https://rest.pos.sandbox.cloud.pceftpos.com/v1/";

    public string PosName { get; set; } = "HotBargainPOS";

    public string PosVersion { get; set; } = "2026.5.1";

    public string? SandboxPosVendorId { get; set; } = "11111111-1111-4111-8111-111111111111";
}

public class LinklyCloudBackendAsyncService(
    ILinklyCloudBackendAsyncRepository repository,
    ILinklyCloudBackendAsyncTransport transport,
    ILinklyCloudBackendTokenProvider tokenProvider,
    ILinklyCloudCredentialRepository credentialRepository,
    ILinklyCloudBackendTerminalCredentialRepository terminalCredentialRepository,
    IOptions<LinklyCloudBackendAsyncOptions> options,
    ILogger<LinklyCloudBackendAsyncService>? logger = null,
    ILinklyCloudTerminalService? terminalService = null) : ILinklyCloudBackendAsyncService
{
    private static readonly JsonSerializerOptions ServiceJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const string RecoveryRetryNewSession = "RetryNewSession";
    private const int MaxTxnRefCreateAttempts = 5;
    private const string OperationTypeTransaction = "Transaction";
    private const string OperationTypeSettlement = "Settlement";
    private const string TerminalHealthHealthy = "Healthy";
    private const string TerminalHealthUnhealthy = "Unhealthy";

    public async Task<LinklyCloudBackendSessionResponse> StartTransactionAsync(
        string storeCode,
        string deviceCode,
        LinklyCloudBackendTransactionRequest request,
        CancellationToken cancellationToken)
    {
        var environment = NormalizeEnvironment(request.Environment);
        var normalizedStoreCode = NormalizeRequired(storeCode, "storeCode");
        var normalizedDeviceCode = NormalizeRequired(deviceCode, "deviceCode");
        var txnType = NormalizeRequired(request.TxnType, "txnType");
        // 不合规的 attempt 身份必须在创建 Pending 会话和请求终端之前拒绝，避免留下占用终端的假会话。
        var attemptTxnRef = DeriveAttemptTxnRef(txnType, request.AttemptGuid);
        var purchaseAnalysisDataSnapshot = request.PurchaseAnalysisData is null
            ? null
            : new Dictionary<string, string>(request.PurchaseAnalysisData, StringComparer.OrdinalIgnoreCase);
        Log(
            $"transaction start environment={LogValue(environment)} " +
            $"store={LogValue(normalizedStoreCode)} device={LogValue(normalizedDeviceCode)} " +
            $"attemptTxnRef={LogValue(attemptTxnRef)} " +
            $"componentVersion={GetComponentVersion()}");
        var notificationBaseUri = GetPublicNotificationBaseUri();
        // 配置缺失必须在创建本地 Pending 前失败，避免产生未提交但占用终端的假 active session。
        _ = GetRequiredNotificationBearer(environment);
        var terminalContext = terminalService is null
            ? null
            : await terminalService.ResolvePaymentTerminalAsync(
                environment,
                normalizedStoreCode,
                normalizedDeviceCode,
                request.TerminalId,
                request.SelectionRevision,
                cancellationToken);
        var activeSession = await GetActiveSessionUnlessSettlementTimedOutAsync(
            environment,
            normalizedStoreCode,
            normalizedDeviceCode,
            cancellationToken);
        if (activeSession is not null)
        {
            throw new LinklyCloudBackendActiveTransactionException(activeSession.SessionId);
        }

        if (terminalContext is not null)
        {
            var activeTerminalSession = await GetActiveSessionByTerminalUnlessSettlementTimedOutAsync(
                environment,
                normalizedStoreCode,
                terminalContext.Terminal.TerminalId,
                cancellationToken);
            if (activeTerminalSession is not null)
            {
                throw new LinklyCloudBackendActiveTransactionException(activeTerminalSession.SessionId);
            }
        }

        var token = await tokenProvider.GetTokenAsync(
            environment,
            normalizedStoreCode,
            normalizedDeviceCode,
            terminalContext?.Terminal.TerminalId,
            cancellationToken);

        var session = await CreatePendingSessionWithUniqueTxnRefAsync(
            environment,
            normalizedStoreCode,
            normalizedDeviceCode,
            terminalContext,
            cancellationToken,
            requestTxnType: txnType,
            requestAmountCents: request.AmtPurchase,
            requestPurchaseAnalysisData: purchaseAnalysisDataSnapshot,
            attemptTxnRef: attemptTxnRef);

        var notification = BuildNotificationRequest(environment, session.SessionId, notificationBaseUri);
        var purchaseAnalysisData = EnsurePurchaseAnalysisData(
            txnType,
            request.AmtPurchase,
            session.TxnRef!,
            purchaseAnalysisDataSnapshot);
        var transportRequest = new LinklyCloudBackendTransportTransactionRequest(
            environment,
            token.RestBaseUrl,
            token.AccessToken,
            session.SessionId,
            txnType,
            request.AmtPurchase,
            session.TxnRef!,
            purchaseAnalysisData,
            notification,
            normalizedStoreCode,
            normalizedDeviceCode,
            session.TerminalId);

        var response = await SendWithRecoverableFailureAsync(
            () => transport.StartTransactionAsync(transportRequest, cancellationToken));
        ApplyTransportResponse(session, response);
        if (IsTransportRecoveryFailure(response.StatusCode))
        {
            session.RecoveryCount++;
        }

        var savedSession = await UpsertSessionAndReadLatestAsync(session, cancellationToken);
        return await BuildResponseAsync(savedSession, cancellationToken);
    }

    public async Task<LinklyCloudBackendSessionResponse> StartSettlementAsync(
        string storeCode,
        string deviceCode,
        LinklyCloudBackendSettlementRequest request,
        CancellationToken cancellationToken)
    {
        var environment = NormalizeEnvironment(request.Environment);
        var normalizedStoreCode = NormalizeRequired(storeCode, "storeCode");
        var normalizedDeviceCode = NormalizeRequired(deviceCode, "deviceCode");
        var notificationBaseUri = GetPublicNotificationBaseUri();
        _ = GetRequiredNotificationBearer(environment);
        var terminalContext = terminalService is null
            ? null
            : await terminalService.ResolvePaymentTerminalAsync(
                environment,
                normalizedStoreCode,
                normalizedDeviceCode,
                request.TerminalId,
                request.SelectionRevision,
                cancellationToken);
        var activeSession = await GetActiveSessionUnlessSettlementTimedOutAsync(
            environment,
            normalizedStoreCode,
            normalizedDeviceCode,
            cancellationToken);
        if (activeSession is not null)
        {
            throw new LinklyCloudBackendActiveTransactionException(activeSession.SessionId);
        }

        if (terminalContext is not null)
        {
            var activeTerminalSession = await GetActiveSessionByTerminalUnlessSettlementTimedOutAsync(
                environment,
                normalizedStoreCode,
                terminalContext.Terminal.TerminalId,
                cancellationToken);
            if (activeTerminalSession is not null)
            {
                throw new LinklyCloudBackendActiveTransactionException(activeTerminalSession.SessionId);
            }
        }

        var token = await tokenProvider.GetTokenAsync(
            environment,
            normalizedStoreCode,
            normalizedDeviceCode,
            terminalContext?.Terminal.TerminalId,
            cancellationToken);
        var session = await CreatePendingSessionWithUniqueTxnRefAsync(
            environment,
            normalizedStoreCode,
            normalizedDeviceCode,
            terminalContext,
            cancellationToken,
            OperationTypeSettlement);
        var transportRequest = new LinklyCloudBackendTransportSettlementRequest(
            environment,
            token.RestBaseUrl,
            token.AccessToken,
            session.SessionId,
            BuildNotificationRequest(environment, session.SessionId, notificationBaseUri),
            normalizedStoreCode,
            normalizedDeviceCode,
            session.TerminalId);
        var response = await SendWithRecoverableFailureAsync(
            () => transport.StartSettlementAsync(transportRequest, cancellationToken));
        ApplySettlementTransportResponse(session, response);
        if (session.IsActive && IsTransportRecoveryFailure(response.StatusCode))
        {
            session.RecoveryCount++;
        }

        var savedSession = await UpsertSessionAndReadLatestAsync(session, cancellationToken);
        return await BuildResponseAsync(savedSession, cancellationToken);
    }

    public async Task<LinklyCloudBackendSessionResponse?> GetStatusAsync(
        string storeCode,
        string deviceCode,
        string environment,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = NormalizeEnvironment(environment);
        var normalizedStoreCode = NormalizeRequired(storeCode, "storeCode");
        var normalizedDeviceCode = NormalizeRequired(deviceCode, "deviceCode");
        var normalizedSessionId = NormalizeRequired(sessionId, "sessionId");
        var evidenceUrl = $"api/v1/linkly/cloud-backend/transactions/{normalizedSessionId}/status?environment={normalizedEnvironment}";
        LogRecoveryServiceEvidence(
            "status",
            "request",
            "request",
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            normalizedSessionId,
            "GET",
            evidenceUrl,
            requestJson: null,
            responseJson: null,
            success: null,
            reason: null,
            response: null,
            certCase: "3.1.3/4.1.2");
        var session = await repository.GetSessionAsync(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            normalizedSessionId,
            cancellationToken);
        if (!IsOperation(session, OperationTypeTransaction))
        {
            return null;
        }
        if (ShouldRefreshOfficialTransaction(session))
        {
            var refreshesCompletedMissingSuccess = ShouldRefreshCompletedMissingSuccess(session);
            var token = await tokenProvider.GetTokenAsync(
                normalizedEnvironment,
                normalizedStoreCode,
                normalizedDeviceCode,
                session!.TerminalId,
                cancellationToken);
            var transportRequest = new LinklyCloudBackendTransportSessionRequest(
                normalizedEnvironment,
                token.RestBaseUrl,
                token.AccessToken,
                normalizedSessionId,
                normalizedStoreCode,
                normalizedDeviceCode,
                session.TerminalId);
            var transportResponse = await SendWithRecoverableFailureAsync(
                () => transport.GetTransactionAsync(transportRequest, cancellationToken));
            if (refreshesCompletedMissingSuccess)
            {
                ApplyCompletedMissingSuccessRefresh(session!, transportResponse);
                session!.UpdatedAt = DateTimeOffset.UtcNow;
                session.LastHttpStatus = (int)transportResponse.StatusCode;
                session.RecoveryAction = null;
            }
            else
            {
                ApplyTransportResponse(session!, transportResponse);
            }

            await AddOfficialTransactionNotificationAsync(session!, transportResponse, cancellationToken);
            session = await UpsertSessionAndReadLatestAsync(session!, cancellationToken);
        }

        var result = session is null ? null : await BuildResponseAsync(session, cancellationToken);
        LogRecoveryServiceEvidence(
            "status",
            "response",
            "response",
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            normalizedSessionId,
            "GET",
            evidenceUrl,
            requestJson: null,
            responseJson: SerializeEvidenceJson(result),
            success: result is not null,
            reason: result is null ? "not-found" : null,
            response: result,
            certCase: "3.1.3/4.1.2");
        return result;
    }

    public async Task<LinklyCloudBackendSessionResponse?> GetSettlementStatusAsync(
        string storeCode,
        string deviceCode,
        string environment,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var session = await repository.GetSessionAsync(
            NormalizeEnvironment(environment),
            NormalizeRequired(storeCode, "storeCode"),
            NormalizeRequired(deviceCode, "deviceCode"),
            NormalizeRequired(sessionId, "sessionId"),
            cancellationToken);
        if (IsOperation(session, OperationTypeSettlement))
        {
            // 轮询到超时仍无回调的结算在这里收口，客户端立刻拿到“结果未知”，不用再空等。
            session = await CloseTimedOutSettlementAsync(session!, cancellationToken);
        }

        // Settlement 没有可安全复用的 transaction 查询端点，只以回调持久化结果为准。
        return IsOperation(session, OperationTypeSettlement)
            ? await BuildResponseAsync(session!, cancellationToken)
            : null;
    }

    public async Task<LinklyCloudBackendSessionResponse?> GetActiveSessionAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken)
    {
        var session = await GetActiveSessionUnlessSettlementTimedOutAsync(
            NormalizeEnvironment(environment),
            NormalizeRequired(storeCode, "storeCode"),
            NormalizeRequired(deviceCode, "deviceCode"),
            cancellationToken);

        return session is null
            ? null
            : await BuildResponseAsync(session, cancellationToken);
    }

    public async Task<LinklyCloudBackendSessionResponse?> GetResumableSessionAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = NormalizeEnvironment(environment);
        var normalizedStoreCode = NormalizeRequired(storeCode, "storeCode");
        var normalizedDeviceCode = NormalizeRequired(deviceCode, "deviceCode");
        var evidenceUrl = $"api/v1/linkly/cloud-backend/transactions/resumable?environment={normalizedEnvironment}";
        LogRecoveryServiceEvidence(
            "resumable session",
            "request",
            "request",
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            null,
            "GET",
            evidenceUrl,
            requestJson: null,
            responseJson: null,
            success: null,
            reason: null,
            response: null,
            certCase: "4.1.2");
        var session = await repository.GetResumableSessionAsync(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            cancellationToken);

        var result = session is null ? null : await BuildResponseAsync(session, cancellationToken);
        LogRecoveryServiceEvidence(
            "resumable session",
            "response",
            "response",
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            result?.SessionId,
            "GET",
            evidenceUrl,
            requestJson: null,
            responseJson: SerializeEvidenceJson(result),
            success: result is not null,
            reason: result is null ? "not-found" : null,
            response: result,
            certCase: "4.1.2");
        return result;
    }

    public async Task<LinklyCloudBackendSessionResponse?> GetResumableSettlementSessionAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken)
    {
        var session = await repository.GetResumableSettlementSessionAsync(
            NormalizeEnvironment(environment),
            NormalizeRequired(storeCode, "storeCode"),
            NormalizeRequired(deviceCode, "deviceCode"),
            cancellationToken);
        if (IsOperation(session, OperationTypeSettlement))
        {
            session = await CloseTimedOutSettlementAsync(session!, cancellationToken);
        }

        return IsOperation(session, OperationTypeSettlement)
            ? await BuildResponseAsync(session!, cancellationToken)
            : null;
    }

    public async Task<LinklyCloudBackendSessionResponse> RecoverAsync(
        string storeCode,
        string deviceCode,
        string sessionId,
        LinklyCloudBackendRecoverRequest request,
        CancellationToken cancellationToken)
    {
        var session = await GetRequiredSessionAsync(storeCode, deviceCode, request.Environment, sessionId, cancellationToken);
        var evidenceUrl = $"api/v1/linkly/cloud-backend/transactions/{session.SessionId}/recover";
        LogRecoveryServiceEvidence(
            "recover",
            "request",
            "request",
            session.Environment,
            session.StoreCode,
            session.DeviceCode,
            session.SessionId,
            "POST",
            evidenceUrl,
            SerializeEvidenceJson(request),
            responseJson: null,
            success: null,
            reason: null,
            response: null,
            certCase: "3.1.3/4.1.2");
        var token = await tokenProvider.GetTokenAsync(
            session.Environment,
            session.StoreCode,
            session.DeviceCode,
            session.TerminalId,
            cancellationToken);
        var transportRequest = new LinklyCloudBackendTransportSessionRequest(
            session.Environment,
            token.RestBaseUrl,
            token.AccessToken,
            session.SessionId,
            session.StoreCode,
            session.DeviceCode,
            session.TerminalId);

        var response = await SendWithRecoverableFailureAsync(
            () => transport.RecoverTransactionAsync(transportRequest, cancellationToken));

        // 闁诡厹鍨归ˇ鏌ュ传瀹ュ懐瀹夐柛娆樹簻缁ㄦ煡鎮介妸銈囶伇婵炲棌妲勭�?08/5xx 濞ｅ洦绻冪€?Pending�?01 閻熸洑鐒﹂惇浼村礆闁垮鐓€ token�?04 闂佹彃锕ラ弬浣该虹拠鎻捫楅梺澶搁檷閳?
        ApplyTransportResponse(session, response);
        if (ShouldCountRecoveryResponse(response.StatusCode))
        {
            session.RecoveryCount++;
        }

        var savedSession = await UpsertSessionAndReadLatestAsync(session, cancellationToken);
        var result = await BuildResponseAsync(savedSession, cancellationToken);
        LogRecoveryServiceEvidence(
            "recover",
            "response",
            "response",
            result.Environment,
            result.StoreCode,
            result.DeviceCode,
            result.SessionId,
            "POST",
            evidenceUrl,
            requestJson: null,
            responseJson: SerializeEvidenceJson(result),
            success: true,
            reason: null,
            response: result,
            certCase: "3.1.3/4.1.2");
        return result;
    }

    public async Task<LinklyCloudBackendSessionResponse> SendKeyAsync(
        string storeCode,
        string deviceCode,
        string sessionId,
        LinklyCloudBackendSendKeyRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedKey = NormalizeSendKey(request.Key);
        var session = await GetRequiredSessionAsync(storeCode, deviceCode, request.Environment, sessionId, cancellationToken);
        var token = await tokenProvider.GetTokenAsync(
            session.Environment,
            session.StoreCode,
            session.DeviceCode,
            session.TerminalId,
            cancellationToken);
        var transportRequest = new LinklyCloudBackendTransportSendKeyRequest(
            session.Environment,
            token.RestBaseUrl,
            token.AccessToken,
            session.SessionId,
            normalizedKey,
            NormalizeOptional(request.Data),
            session.StoreCode,
            session.DeviceCode,
            session.TerminalId);

        var response = await SendWithRecoverableFailureAsync(
            () => transport.SendKeyAsync(transportRequest, cancellationToken));
        ApplySendKeyTransportResponse(session, response);
        if (IsTransportRecoveryFailure(response.StatusCode))
        {
            session.RecoveryCount++;
        }

        var savedSession = await UpsertSessionAndReadLatestAsync(session, cancellationToken);
        return await BuildResponseAsync(savedSession, cancellationToken);
    }

    public async Task<LinklyCloudBackendSessionResponse> MarkReceiptPrintedAsync(
        string storeCode,
        string deviceCode,
        string sessionId,
        LinklyCloudBackendMarkReceiptPrintedRequest request,
        CancellationToken cancellationToken)
    {
        var session = await GetRequiredSessionAsync(storeCode, deviceCode, request.Environment, sessionId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var evidenceUrl = $"api/v1/linkly/cloud-backend/transactions/{session.SessionId}/receipt/printed";
        LogRecoveryServiceEvidence(
            "receipt/printed",
            "request",
            "request",
            session.Environment,
            session.StoreCode,
            session.DeviceCode,
            session.SessionId,
            "POST",
            evidenceUrl,
            SerializeEvidenceJson(request),
            responseJson: null,
            success: null,
            reason: null,
            response: null,
            certCase: "4.1.3");

        // 闁衡偓閼搁潧绁﹂梺顐ｆ皑閻擄繝宕ｉ鍥跺殯闁哄嫬瀛╅弸鍐嫉椤掆偓閸戯繝宕氶幏灞惧涧闁挎稒绋戦褰掑箣妞嬪寒浼傜痪顓у枦椤撶粯娼诲☉妯哄汲闁瑰灚鎸稿畵鍐╃閵堝嫮甯涢梺鐐礃閻箖宕ユ惔顖滅闁归潧绉村﹢顏呮交濞嗘挸娅￠柡宥呮穿椤斿洤顔忛崣澶娾叺闁告鍩囬埀?
        session.ReceiptPrintedAt ??= now;
        session.UpdatedAt = now;
        var savedSession = await UpsertSessionAndReadLatestAsync(session, cancellationToken);
        var result = await BuildResponseAsync(savedSession, cancellationToken);
        LogRecoveryServiceEvidence(
            "receipt/printed",
            "response",
            "response",
            result.Environment,
            result.StoreCode,
            result.DeviceCode,
            result.SessionId,
            "POST",
            evidenceUrl,
            requestJson: null,
            responseJson: SerializeEvidenceJson(result),
            success: true,
            reason: null,
            response: result,
            certCase: "4.1.3");
        return result;
    }

    public async Task<LinklyCloudBackendSessionResponse> MarkSettlementReceiptPrintedAsync(
        string storeCode,
        string deviceCode,
        string sessionId,
        LinklyCloudBackendMarkReceiptPrintedRequest request,
        CancellationToken cancellationToken)
    {
        var session = await GetRequiredSessionAsync(
            storeCode,
            deviceCode,
            request.Environment,
            sessionId,
            cancellationToken,
            OperationTypeSettlement);
        session.ReceiptPrintedAt ??= DateTimeOffset.UtcNow;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        var savedSession = await UpsertSessionAndReadLatestAsync(session, cancellationToken);
        return await BuildResponseAsync(savedSession, cancellationToken);
    }

    public async Task<LinklyCloudBackendSessionResponse> AcknowledgeSessionAsync(
        string storeCode,
        string deviceCode,
        string environment,
        string sessionId,
        bool supervisorResolved,
        CancellationToken cancellationToken)
    {
        var acknowledgedAt = DateTimeOffset.UtcNow;
        var normalizedEnvironment = NormalizeEnvironment(environment);
        var normalizedStoreCode = NormalizeRequired(storeCode, "storeCode");
        var normalizedDeviceCode = NormalizeRequired(deviceCode, "deviceCode");
        var normalizedSessionId = NormalizeRequired(sessionId, "sessionId");
        var evidenceUrl = $"api/v1/linkly/cloud-backend/transactions/{normalizedSessionId}/acknowledge";
        LogRecoveryServiceEvidence(
            "acknowledge",
            "request",
            "request",
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            normalizedSessionId,
            "POST",
            evidenceUrl,
            requestJson: supervisorResolved ? SerializeEvidenceJson(new { supervisorResolved }) : null,
            responseJson: null,
            success: null,
            reason: null,
            response: null,
            certCase: "4.1.2");
        var session = await repository.AcknowledgeSessionAsync(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            normalizedSessionId,
            acknowledgedAt,
            supervisorResolved,
            cancellationToken);

        var result = session is null
            ? throw new LinklyCloudBackendSessionNotFoundException()
            : await BuildResponseAsync(session, cancellationToken);
        LogRecoveryServiceEvidence(
            "acknowledge",
            "response",
            "response",
            result.Environment,
            result.StoreCode,
            result.DeviceCode,
            result.SessionId,
            "POST",
            evidenceUrl,
            requestJson: null,
            responseJson: SerializeEvidenceJson(result),
            success: true,
            reason: null,
            response: result,
            certCase: "4.1.2");
        return result;
    }

    public async Task<LinklyCloudBackendSessionResponse> AcknowledgeSettlementSessionAsync(
        string storeCode,
        string deviceCode,
        string environment,
        string sessionId,
        bool supervisorResolved,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = NormalizeEnvironment(environment);
        var normalizedStoreCode = NormalizeRequired(storeCode, "storeCode");
        var normalizedDeviceCode = NormalizeRequired(deviceCode, "deviceCode");
        var normalizedSessionId = NormalizeRequired(sessionId, "sessionId");
        var session = await repository.GetSessionAsync(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            normalizedSessionId,
            cancellationToken);
        if (!IsOperation(session, OperationTypeSettlement))
        {
            throw new LinklyCloudBackendSessionNotFoundException();
        }

        var acknowledged = await repository.AcknowledgeSessionAsync(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            normalizedSessionId,
            DateTimeOffset.UtcNow,
            // 主管结案才把非终态（含回调超时后的“结果未知”）写成 SupervisorResolved；普通 ack 不改状态。
            supervisorResolved,
            cancellationToken) ?? throw new LinklyCloudBackendSessionNotFoundException();
        return await BuildResponseAsync(acknowledged, cancellationToken);
    }

    public async Task ReceiveNotificationAsync(
        string environment,
        string sessionId,
        string type,
        string? authorizationHeader,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = NormalizeEnvironment(environment);
        var expectedBearer = GetNotificationBearer(normalizedEnvironment);
        var authorizationMatches = !string.IsNullOrWhiteSpace(expectedBearer) &&
            string.Equals(authorizationHeader?.Trim(), $"Bearer {expectedBearer}", StringComparison.Ordinal);
        Log(
            $"notification received environment={LogValue(normalizedEnvironment)} " +
            $"sessionId={LogValue(sessionId)} type={LogValue(type)} " +
            $"authorizationPresent={!string.IsNullOrWhiteSpace(authorizationHeader)} authorizationMatches={authorizationMatches}");
        if (string.IsNullOrWhiteSpace(expectedBearer) ||
            !authorizationMatches)
        {
            Log(
                $"notification rejected environment={LogValue(normalizedEnvironment)} " +
                $"sessionId={LogValue(sessionId)} type={LogValue(type)} reason=invalid-authorization");
            throw new LinklyCloudBackendNotificationUnauthorizedException();
        }

        var normalizedSessionId = NormalizeRequired(sessionId, "sessionId");
        var normalizedType = NormalizeRequired(type, "type");
        // 回调原文仅在当前请求内解析；进入日志、通知事实表和后续响应前必须完成脱敏。
        var rawPayloadJson = payload.GetRawText();
        var payloadJson = LinklyReceiptTextSanitizer.SanitizeSettlementData(rawPayloadJson) ?? "{}";
        var now = DateTimeOffset.UtcNow;

        var session = await repository.GetSessionByEnvironmentSessionIdAsync(
            normalizedEnvironment,
            normalizedSessionId,
            cancellationToken);
        if (session is null)
        {
            Log(
                $"notification ignored environment={LogValue(normalizedEnvironment)} " +
                $"sessionId={LogValue(normalizedSessionId)} type={LogValue(normalizedType)} reason=session-not-found");
            return;
        }

        if (string.Equals(normalizedType, "transaction", StringComparison.OrdinalIgnoreCase) &&
            HasPersistedRequestEvidence(session))
        {
            // 新证据记录必须保留供应商返回的完整身份字段，才能在恢复时做精确匹配；其余敏感字段仍保持脱敏。
            payloadJson = PreserveTransactionIdentityFields(rawPayloadJson, payloadJson);
        }

        var existingNotifications = await repository.GetNotificationsAsync(
            normalizedEnvironment,
            session.StoreCode,
            session.DeviceCode,
            normalizedSessionId,
            cancellationToken);
        var isSettlementReceipt =
            string.Equals(normalizedType, "receipt", StringComparison.OrdinalIgnoreCase) &&
            IsOperation(session, OperationTypeSettlement);
        var notificationAlreadyRecorded = !isSettlementReceipt && existingNotifications.Any(notification =>
            string.Equals(notification.Type, normalizedType, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(notification.PayloadJson, payloadJson, StringComparison.Ordinal));
        if (!notificationAlreadyRecorded)
        {
            await repository.AddNotificationAsync(new LinklyCloudBackendNotificationRecord
            {
                Environment = normalizedEnvironment,
                StoreCode = session.StoreCode,
                DeviceCode = session.DeviceCode,
                SessionId = normalizedSessionId,
                Type = normalizedType,
                PayloadJson = payloadJson,
                ReceivedAt = now
            }, cancellationToken);
        }

        if (string.Equals(normalizedType, "settlement", StringComparison.OrdinalIgnoreCase) &&
            IsOperation(session, OperationTypeSettlement))
        {
            ApplySettlementCompletedPayload(session, payloadJson, now);
        }
        else if (string.Equals(normalizedType, "transaction", StringComparison.OrdinalIgnoreCase))
        {
            LogTransactionNotificationSnapshot(normalizedSessionId, payloadJson);
            var updateOutcomeEvidence = !IsCompleted(session) ||
                string.IsNullOrWhiteSpace(session.ResponseCode);
            var fillMissingSuccess = session.TransactionSuccess is null &&
                (updateOutcomeEvidence || CanFillMissingTransactionSuccess(session, payloadJson));
            if (updateOutcomeEvidence || fillMissingSuccess)
            {
                ApplyCompletedPayload(
                    session,
                    payloadJson,
                    updateTxnRef: false,
                    updateOutcomeEvidence: updateOutcomeEvidence);
            }
            else
            {
                session.UpdatedAt = now;
            }
        }
        else if (string.Equals(normalizedType, "display", StringComparison.OrdinalIgnoreCase))
        {
            ApplyDisplayPayload(session, payloadJson, now);
        }
        else if (string.Equals(normalizedType, "receipt", StringComparison.OrdinalIgnoreCase))
        {
            if (IsOperation(session, OperationTypeSettlement))
            {
                if (!notificationAlreadyRecorded)
                {
                    ApplySettlementReceiptPayload(session, payloadJson, now);
                }
                else
                {
                    session.UpdatedAt = now;
                }
            }
            else
            {
                ApplyReceiptPayload(session, payloadJson, now);
            }
        }
        else
        {
            session.UpdatedAt = now;
        }

        await repository.UpsertSessionAsync(session, cancellationToken);
    }

    public Task<LinklyCloudBackendHealthResponse> GetHealthAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken)
    {
        return GetHealthAsync(storeCode, deviceCode, environment, null, null, cancellationToken);
    }

    public async Task<LinklyCloudBackendHealthResponse> GetHealthAsync(
        string storeCode,
        string deviceCode,
        string environment,
        Guid? terminalId,
        long? selectionRevision,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = NormalizeEnvironment(environment);
        var normalizedStoreCode = NormalizeRequired(storeCode, "storeCode");
        var normalizedDeviceCode = NormalizeRequired(deviceCode, "deviceCode");
        var terminalContext = terminalService is null
            ? null
            : await terminalService.ResolvePaymentTerminalAsync(
                normalizedEnvironment,
                normalizedStoreCode,
                normalizedDeviceCode,
                terminalId,
                selectionRevision,
                cancellationToken);
        var checks = new List<LinklyCloudBackendHealthCheckDto>();
        LogServiceJson(
            operation: "backend-health",
            phase: "request",
            direction: "request",
            environment: normalizedEnvironment,
            storeCode: normalizedStoreCode,
            deviceCode: normalizedDeviceCode,
            success: null,
            reason: null,
            request: new
            {
                environment = normalizedEnvironment,
                storeCode = normalizedStoreCode,
                deviceCode = normalizedDeviceCode
            },
            response: null,
            details: null);

        var credential = terminalContext is null
            ? await credentialRepository.GetByStoreCodeAsync(
                normalizedStoreCode,
                normalizedEnvironment,
                cancellationToken)
            : null;
        var storeCredentialReady = terminalContext is not null
            ? !string.IsNullOrWhiteSpace(terminalContext.Terminal.Username) &&
                !string.IsNullOrWhiteSpace(terminalContext.Terminal.Password)
            : credential is not null &&
                !string.IsNullOrWhiteSpace(credential.Username) &&
                !string.IsNullOrWhiteSpace(credential.Password);
        checks.Add(CreateHealthCheck(
            "STORE_CREDENTIAL",
            storeCredentialReady,
            "Linkly Cloud store credential is configured.",
            "Linkly Cloud store credential is missing for this store and environment."));

        var terminalCredential = terminalContext is null
            ? await terminalCredentialRepository.GetByDeviceAsync(
                normalizedEnvironment,
                normalizedStoreCode,
                normalizedDeviceCode,
                cancellationToken)
            : null;
        var terminalSecretReady = terminalContext is not null
            ? !string.IsNullOrWhiteSpace(terminalContext.Terminal.Secret)
            : terminalCredential is not null && !string.IsNullOrWhiteSpace(terminalCredential.Secret);
        checks.Add(CreateHealthCheck(
            "TERMINAL_SECRET",
            terminalSecretReady,
            "Linkly Cloud terminal secret is configured.",
            "Linkly Cloud terminal secret is missing for this terminal."));

        var terminalPosIdReady = terminalContext is not null
            ? !string.IsNullOrWhiteSpace(terminalContext.Terminal.PosId)
            : terminalCredential is not null && !string.IsNullOrWhiteSpace(terminalCredential.PosId);
        checks.Add(CreateHealthCheck(
            "TERMINAL_POS_ID",
            terminalPosIdReady,
            "Linkly Cloud POS ID is configured.",
            "Linkly Cloud POS ID is missing for this terminal."));

        var notificationBearerReady = !string.IsNullOrWhiteSpace(GetNotificationBearer(normalizedEnvironment));
        checks.Add(CreateHealthCheck(
            "NOTIFICATION_BEARER",
            notificationBearerReady,
            "Linkly Cloud notification bearer is configured.",
            "Linkly Cloud notification bearer is missing for this environment."));

        var publicCallbackReady = TryGetPublicNotificationBaseUri(out var publicCallbackUri);
        checks.Add(CreateHealthCheck(
            "PUBLIC_CALLBACK_URL",
            publicCallbackReady,
            "Linkly Cloud notification callback URL is public HTTPS.",
            "Linkly Cloud notification callback URL must be public HTTPS."));

        // 闂傚倸鍊搁崐鐑芥嚄閸洍鈧箓宕奸妷顔芥櫈闂佸憡鍔﹂崰鏍閸ф鈷戞い鎺嗗亾缂佸顕划濠氬冀椤愮喎浜炬鐐茬仢閸旀岸鏌熼崣澹濐亪鍩ユ径鎰潊闁绘﹢娼ф慨锔戒繆閻愵亜鈧牜鏁幒鏂哄亾濮樼厧澧撮柟閿嬪灴閹垽宕楅懖鈺佸妇濠电姰鍨煎▔娑㈡偋閸℃稑绀夋慨姗嗗幘缁犻箖鏌熼崘鎻掓Щ闁告碍锕㈠顕€宕奸悢铚傛睏闂備浇鍋愰埛鍫ュ礈濞戙垹绀夋い鏇楀亾婵﹦鍎ょ€电厧鈻庨幋鐘虫婵＄偑鍊х粻鎾愁焽瑜旈、姘舵晲婢跺浠洪梺鍛婃尭瀵爼寮查姀銈嗏拺闂侇偆鍋涢懟顖涙櫠椤曗偓閺屻劑寮村Ο铏逛紙濡ょ姷鍋為敃銏ゃ€佸▎鎾村仼閻忕偠妫勭粭宀勬⒑鐠囨煡顎楃紒鐘茬Ч瀹曟洟宕￠悘缁樻そ婵℃悂鍩℃担绋挎闂備胶顭堥惉濂稿磻閻愮儤鍋傞柕澶涘缁犻箖鏌熺€甸晲绱虫い蹇撶墑閳ь剙鍟埢搴ㄥ箣閻樼绱叉繝寰锋澘鈧劙宕戦幘娣簻闁挎梻鍋撻弳顒勬�?active session �?404 闂傚倷娴囧畷鐢稿窗閹邦喖鍨濋幖娣灪濞呯姵淇婇妶鍛櫣缂佺姵婢橀—鍐偓锝庝簼閹癸絿绱掗埀顒佺節閸ャ劎鍘搁梺鍛婂姂閸斿孩鏅跺☉銏＄厽闁圭虎鍨版禍楣冩⒑鐠囨煡顎楃紒鐘茬Ч瀹曟洟宕￠悘缁樻そ婵℃悂鍩℃担绋挎闂備胶顭堥惉濂稿磻閻愮儤鍋傞柡鍥╁枔缁犻箖鏌涢埄鍐ｆ（妞ゅ繐鐗婇崐鍫曟煥濠靛棭妲归柣鎾寸☉闇夐柨婵嗩槹濞懷囨煃瑜滈崜姘辨崲閸愨晝�?
        var response = new LinklyCloudBackendHealthResponse(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            checks.All(check => check.IsReady),
            publicCallbackUri?.AbsoluteUri,
            checks);
        LogServiceJson(
            operation: "backend-health",
            phase: "response",
            direction: "response",
            environment: normalizedEnvironment,
            storeCode: normalizedStoreCode,
            deviceCode: normalizedDeviceCode,
            success: response.IsReady,
            reason: response.IsReady ? null : "failed-health-checks",
            request: null,
            response: new
            {
                response.IsReady,
                response.PublicNotificationBaseUrl,
                FailedChecks = response.Checks
                    .Where(check => !check.IsReady)
                    .Select(check => check.Code)
                    .ToArray()
            },
            details: new
            {
                Checks = response.Checks.Select(check => new
                {
                    check.Code,
                    check.IsReady
                }).ToArray()
            });
        return response;
    }

    public Task<LinklyCloudBackendStatusTestResponse> RunStatusTestAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken)
    {
        return RunStatusTestAsync(storeCode, deviceCode, environment, null, null, cancellationToken);
    }

    public async Task<LinklyCloudBackendStatusTestResponse> RunStatusTestAsync(
        string storeCode,
        string deviceCode,
        string environment,
        Guid? terminalId,
        long? selectionRevision,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = NormalizeEnvironment(environment);
        var normalizedStoreCode = NormalizeRequired(storeCode, "storeCode");
        var normalizedDeviceCode = NormalizeRequired(deviceCode, "deviceCode");
        var terminalContext = terminalService is null
            ? null
            : await terminalService.ResolvePaymentTerminalAsync(
                normalizedEnvironment,
                normalizedStoreCode,
                normalizedDeviceCode,
                terminalId,
                selectionRevision,
                cancellationToken);
        var requestedAt = DateTimeOffset.UtcNow;
        var sessionId = Guid.NewGuid().ToString("D");
        var transportResponse = await ExecuteTerminalTestRequestAsync(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            terminalContext,
            (token, physicalTerminalId) => transport.SendStatusAsync(
                new LinklyCloudBackendTransportStatusRequest(
                    normalizedEnvironment,
                    token.RestBaseUrl,
                    token.AccessToken,
                    sessionId,
                    normalizedStoreCode,
                    normalizedDeviceCode,
                    physicalTerminalId),
                cancellationToken),
            cancellationToken);
        var status = ParseStatusTestResponse(
            transportResponse.Body,
            out var responseCode,
            out var responseText,
            out var responseTxnRef,
            out var responseDate,
            out var responseTime,
            out var loggedOn);
        var resolvedResponseTxnRef = await ResolveStatusTestTxnRefAsync(
            responseTxnRef,
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            cancellationToken);
        var succeeded = transportResponse.StatusCode == HttpStatusCode.OK &&
            status &&
            IsSuccessfulStatusTestResponse(responseCode);
        var message = BuildStatusTestMessage(transportResponse.StatusCode, succeeded, responseCode, responseText);
        var result = new LinklyCloudBackendStatusTestResponse(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            sessionId,
            requestedAt,
            (int)transportResponse.StatusCode,
            succeeded,
            responseCode,
            responseText,
            resolvedResponseTxnRef,
            responseDate,
            responseTime,
            message)
        {
            LoggedOn = transportResponse.StatusCode == HttpStatusCode.OK && status ? loggedOn : null
        };

        await TryRecordDefinitiveTerminalHealthAsync(
            terminalContext,
            transportResponse.StatusCode,
            succeeded,
            CancellationToken.None);

        LogServiceJson(
            operation: "transaction-status-test",
            phase: "response",
            direction: "response",
            environment: normalizedEnvironment,
            storeCode: normalizedStoreCode,
            deviceCode: normalizedDeviceCode,
            success: succeeded,
            reason: succeeded ? null : "status-test-failed",
            request: null,
            response: new
            {
                result.TransactionReference,
                result.HttpStatus,
                result.ResponseCode,
                result.ResponseText,
                result.TxnRef,
                result.ResponseTxnRef,
                result.ResponseDate,
                result.ResponseTime
            },
            details: new
            {
                result.RequestedAt
            });
        return result;
    }

    private async Task<string?> ResolveStatusTestTxnRefAsync(
        string? responseTxnRef,
        string normalizedEnvironment,
        string normalizedStoreCode,
        string normalizedDeviceCode,
        CancellationToken cancellationToken)
    {
        var normalizedResponseTxnRef = NormalizeOptional(responseTxnRef);
        if (normalizedResponseTxnRef is not null)
        {
            return normalizedResponseTxnRef;
        }

        // 3.1.1/3.1.2 的 Status 请求不能带 TxnRef；终端回包省略时，用本机最近未确认交易补齐响应证据。
        var latestSession = await repository.GetResumableSessionAsync(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            cancellationToken);
        return NormalizeOptional(latestSession?.TxnRef);
    }

    public Task<LinklyCloudBackendLogonTestResponse> RunLogonTestAsync(
        string storeCode,
        string deviceCode,
        string environment,
        CancellationToken cancellationToken)
    {
        return RunLogonTestAsync(storeCode, deviceCode, environment, null, null, cancellationToken);
    }

    public async Task<LinklyCloudBackendLogonTestResponse> RunLogonTestAsync(
        string storeCode,
        string deviceCode,
        string environment,
        Guid? terminalId,
        long? selectionRevision,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = NormalizeEnvironment(environment);
        var normalizedStoreCode = NormalizeRequired(storeCode, "storeCode");
        var normalizedDeviceCode = NormalizeRequired(deviceCode, "deviceCode");
        var terminalContext = terminalService is null
            ? null
            : await terminalService.ResolvePaymentTerminalAsync(
                normalizedEnvironment,
                normalizedStoreCode,
                normalizedDeviceCode,
                terminalId,
                selectionRevision,
                cancellationToken);
        var requestedAt = DateTimeOffset.UtcNow;
        var sessionId = Guid.NewGuid().ToString("D");
        var transportResponse = await ExecuteTerminalTestRequestAsync(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            terminalContext,
            (token, physicalTerminalId) => transport.SendLogonAsync(
                new LinklyCloudBackendTransportSessionRequest(
                    normalizedEnvironment,
                    token.RestBaseUrl,
                    token.AccessToken,
                    sessionId,
                    normalizedStoreCode,
                    normalizedDeviceCode,
                    physicalTerminalId),
                cancellationToken),
            cancellationToken);
        var logon = ParseLogonTestResponse(
            transportResponse.Body,
            out var responseCode,
            out var responseText,
            out var catid,
            out var caid,
            out var pinPadVersion);
        var succeeded = transportResponse.StatusCode == HttpStatusCode.OK &&
            logon &&
            string.Equals(responseCode, "00", StringComparison.OrdinalIgnoreCase);
        var message = BuildLogonTestMessage(transportResponse.StatusCode, succeeded, responseCode, responseText);
        var result = new LinklyCloudBackendLogonTestResponse(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            sessionId,
            requestedAt,
            (int)transportResponse.StatusCode,
            succeeded,
            responseCode,
            responseText,
            catid,
            caid,
            pinPadVersion,
            message);

        await TryRecordDefinitiveTerminalHealthAsync(
            terminalContext,
            transportResponse.StatusCode,
            succeeded,
            CancellationToken.None);

        LogServiceJson(
            operation: "logon-test",
            phase: "response",
            direction: "response",
            environment: normalizedEnvironment,
            storeCode: normalizedStoreCode,
            deviceCode: normalizedDeviceCode,
            success: succeeded,
            reason: succeeded ? null : "logon-test-failed",
            request: null,
            response: new
            {
                result.TransactionReference,
                result.HttpStatus,
                result.ResponseCode,
                result.ResponseText,
                result.Catid,
                result.Caid,
                result.PinPadVersion
            },
            details: new
            {
                result.RequestedAt
            });
        return result;
    }

    public async Task<LinklyCloudBackendTerminalCredentialResponse> UpsertTerminalCredentialAsync(
        string storeCode,
        string deviceCode,
        LinklyCloudBackendTerminalCredentialUpsertRequest request,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedEnvironment = NormalizeEnvironment(request.Environment);
        var normalizedStoreCode = NormalizeRequired(storeCode, "storeCode");
        var normalizedDeviceCode = NormalizeRequired(deviceCode, "deviceCode");
        var secret = NormalizeRequired(request.Secret, "secret");
        var posId = NormalizeUuidV4(request.PosId);
        var now = DateTime.UtcNow;
        LogServiceJson(
            operation: "terminal-credential",
            phase: "request",
            direction: "request",
            environment: normalizedEnvironment,
            storeCode: normalizedStoreCode,
            deviceCode: normalizedDeviceCode,
            success: null,
            reason: null,
            request: new
            {
                environment = normalizedEnvironment,
                storeCode = normalizedStoreCode,
                deviceCode = normalizedDeviceCode,
                posId,
                secret = DescribeSecret(secret)
            },
            response: null,
            details: new
            {
                updatedBy = NormalizeOptional(updatedBy)
            });

        var credential = await terminalCredentialRepository.UpsertAsync(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            secret,
            posId,
            now,
            NormalizeOptional(updatedBy),
            cancellationToken);

        var response = new LinklyCloudBackendTerminalCredentialResponse(
            credential.Environment ?? normalizedEnvironment,
            credential.StoreCode ?? normalizedStoreCode,
            credential.DeviceCode ?? normalizedDeviceCode,
            !string.IsNullOrWhiteSpace(credential.Secret),
            credential.PosId ?? posId,
            new DateTimeOffset(DateTime.SpecifyKind(credential.UpdatedAt ?? now, DateTimeKind.Utc)));
        LogServiceJson(
            operation: "terminal-credential",
            phase: "succeeded",
            direction: "response",
            environment: response.Environment,
            storeCode: response.StoreCode,
            deviceCode: response.DeviceCode,
            success: true,
            reason: null,
            request: null,
            response: new
            {
                response.Environment,
                response.StoreCode,
                response.DeviceCode,
                response.HasSecret,
                response.PosId,
                response.UpdatedAt
            },
            details: null);
        return response;
    }

    private async Task<LinklyCloudBackendSessionRecord> GetRequiredSessionAsync(
        string storeCode,
        string deviceCode,
        string environment,
        string sessionId,
        CancellationToken cancellationToken,
        string? expectedOperationType = null)
    {
        var session = await repository.GetSessionAsync(
            NormalizeEnvironment(environment),
            NormalizeRequired(storeCode, "storeCode"),
            NormalizeRequired(deviceCode, "deviceCode"),
            NormalizeRequired(sessionId, "sessionId"),
            cancellationToken);
        return session is null ||
            expectedOperationType is not null && !IsOperation(session, expectedOperationType)
            ? throw new LinklyCloudBackendSessionNotFoundException()
            : session;
    }

    private async Task<LinklyCloudBackendSessionRecord> CreatePendingSessionWithUniqueTxnRefAsync(
        string environment,
        string storeCode,
        string deviceCode,
        LinklyCloudTerminalPaymentContext? terminalContext,
        CancellationToken cancellationToken,
        string operationType = OperationTypeTransaction,
        string? requestTxnType = null,
        long? requestAmountCents = null,
        IReadOnlyDictionary<string, string>? requestPurchaseAnalysisData = null,
        string? attemptTxnRef = null)
    {
        if (terminalContext is not null && terminalContext.Terminal.UpdatedAt is null)
        {
            throw new LinklyCloudTerminalSelectionConflictException();
        }

        // 由 attempt 身份派生的引用已落在 POS 本地 attempt 上，碰撞时换号会让本地记录再也对不上这笔会话，所以只尝试一次。
        var maxAttempts = attemptTxnRef is null ? MaxTxnRefCreateAttempts : 1;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var now = DateTimeOffset.UtcNow;
            var txnRef = string.Equals(operationType, OperationTypeTransaction, StringComparison.OrdinalIgnoreCase)
                ? attemptTxnRef ?? CreateTxnRef()
                : null;
            var effectivePurchaseAnalysisData = txnRef is null || requestTxnType is null || requestAmountCents is null
                ? null
                : EnsurePurchaseAnalysisData(
                    requestTxnType,
                    requestAmountCents.Value,
                    txnRef,
                    requestPurchaseAnalysisData);
            var session = new LinklyCloudBackendSessionRecord
            {
                Environment = environment,
                StoreCode = storeCode,
                DeviceCode = deviceCode,
                TerminalId = terminalContext?.Terminal.TerminalId,
                SelectionRevision = terminalContext?.Selection.Revision,
                TerminalUpdatedAt = terminalContext?.Terminal.UpdatedAt,
                SessionId = Guid.NewGuid().ToString("D"),
                Status = StatusPending,
                OperationType = operationType,
                TxnRef = txnRef,
                // 请求证据必须和 Pending 会话一起落库，确保上游已扣款但响应丢失时仍可安全恢复。
                RequestTxnType = NormalizeOptional(requestTxnType),
                RequestAmountCents = requestAmountCents,
                RequestRfn = ReadAnalysisField(effectivePurchaseAnalysisData, "RFN"),
                IsActive = true,
                UpdatedAt = now
            };

            // 数据库原子闸门拒绝时，先区分在途会话和配置漂移；仅 TxnRef 碰撞允许生成新引用重试。
            if (await repository.TryCreateSessionAsync(session, cancellationToken))
            {
                return session;
            }

            var resumable = await repository.GetResumableSessionAsync(
                environment,
                storeCode,
                deviceCode,
                cancellationToken);
            if (resumable is not null)
            {
                throw new LinklyCloudBackendActiveTransactionException(resumable.SessionId);
            }

            if (terminalContext is not null)
            {
                var blockingTerminal = await repository.GetBlockingSessionByTerminalAsync(
                    environment,
                    storeCode,
                    terminalContext.Terminal.TerminalId,
                    cancellationToken);
                if (blockingTerminal is not null)
                {
                    throw new LinklyCloudBackendActiveTransactionException(blockingTerminal.SessionId);
                }
            }

            await EnsurePaymentConfigurationSnapshotStillCurrentAsync(
                environment,
                storeCode,
                deviceCode,
                terminalContext,
                cancellationToken);
            if (terminalContext is not null && terminalService is not null)
            {
                var currentTerminal = await terminalService.GetTerminalAsync(
                    environment,
                    storeCode,
                    terminalContext.Terminal.TerminalId,
                    cancellationToken);
                if (currentTerminal?.PairingAttemptId is not null &&
                    currentTerminal.PairingLeaseExpiresAt is not null &&
                    currentTerminal.PairingLeaseExpiresAt > DateTime.UtcNow)
                {
                    throw new LinklyCloudBackendActiveTransactionException(null);
                }
            }
        }

        if (attemptTxnRef is not null)
        {
            // 在途会话、终端占用、配置漂移都已排除，剩下的只能是同一个 attempt 已经提交过一次。
            throw new LinklyCloudBackendValidationException(
                "This payment attempt has already been submitted to Linkly Cloud. Start a new payment.");
        }

        throw new LinklyCloudBackendValidationException("Failed to allocate a unique Linkly Cloud transaction reference.");
    }

    private async Task EnsurePaymentConfigurationSnapshotStillCurrentAsync(
        string environment,
        string storeCode,
        string deviceCode,
        LinklyCloudTerminalPaymentContext? expected,
        CancellationToken cancellationToken)
    {
        if (terminalService is null)
        {
            return;
        }

        try
        {
            var current = await terminalService.ResolvePaymentTerminalAsync(
                environment,
                storeCode,
                deviceCode,
                expected?.Terminal.TerminalId,
                expected?.Selection.Revision,
                cancellationToken);
            if (expected is null)
            {
                if (current is not null)
                {
                    throw new LinklyCloudTerminalSelectionConflictException();
                }

                return;
            }

            if (current is null ||
                current.Terminal.TerminalId != expected.Terminal.TerminalId ||
                current.Selection.Revision != expected.Selection.Revision ||
                current.Terminal.UpdatedAt != expected.Terminal.UpdatedAt)
            {
                throw new LinklyCloudTerminalSelectionConflictException();
            }
        }
        catch (LinklyCloudTerminalSelectionConflictException)
        {
            throw;
        }
        catch (LinklyCloudTerminalNotFoundException)
        {
            throw new LinklyCloudTerminalSelectionConflictException();
        }
        catch (LinklyCloudTerminalNotReadyException)
        {
            throw new LinklyCloudTerminalSelectionConflictException();
        }
    }

    private LinklyCloudBackendNotificationRequest BuildNotificationRequest(
        string environment,
        string sessionId,
        Uri notificationBaseUri)
    {
        var bearer = GetRequiredNotificationBearer(environment);

        var relativePath = string.Join(
            '/',
            "api/v1/linkly/cloud-notifications",
            Uri.EscapeDataString(environment),
            Uri.EscapeDataString(sessionId),
            "{{type}}");
        return new LinklyCloudBackendNotificationRequest(
            notificationBaseUri.AbsoluteUri + relativePath,
            $"Bearer {bearer}");
    }

    private string? GetNotificationBearer(string environment)
    {
        return string.Equals(environment, "Sandbox", StringComparison.Ordinal)
            ? NormalizeOptional(options.Value.SandboxNotificationBearer)
            : NormalizeOptional(options.Value.ProductionNotificationBearer);
    }

    private string GetRequiredNotificationBearer(string environment)
    {
        var bearer = GetNotificationBearer(environment);
        if (string.IsNullOrWhiteSpace(bearer))
        {
            throw new LinklyCloudBackendValidationException("Linkly Cloud notification bearer is not configured.");
        }

        return bearer;
    }

    private Uri GetPublicNotificationBaseUri()
    {
        if (!TryGetPublicNotificationBaseUri(out var uri))
        {
            throw new LinklyCloudBackendValidationException(
                "Linkly Cloud public notification base URL must be configured as an HTTPS URL.");
        }

        return uri!;
    }

    private bool TryGetPublicNotificationBaseUri(out Uri? uri)
    {
        var configured = NormalizeOptional(options.Value.PublicNotificationBaseUrl);
        if (configured is null ||
            !Uri.TryCreate(configured, UriKind.Absolute, out var parsedUri) ||
            !string.Equals(parsedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !IsPublicHost(parsedUri.Host))
        {
            uri = null;
            return false;
        }

        uri = parsedUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? parsedUri
            : new Uri(parsedUri.AbsoluteUri + "/", UriKind.Absolute);
        return true;
    }

    private static bool IsPublicHost(string host)
    {
        var normalizedHost = host.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(normalizedHost) || IsClearlyNonPublicHostName(normalizedHost))
        {
            return false;
        }

        if (!IPAddress.TryParse(normalizedHost, out var address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !IsPrivateOrSpecialIpv4(bytes);
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 &&
            !address.Equals(IPAddress.IPv6Any) &&
            !address.IsIPv6LinkLocal &&
            !address.IsIPv6SiteLocal &&
            !address.IsIPv6Multicast &&
            !IsDocumentationIpv6(bytes) &&
            (bytes[0] & 0xfe) != 0xfc;
    }

    private static bool IsClearlyNonPublicHostName(string host)
    {
        // �?IP 濠电偞鍨堕幐鍓у垝椤栨埃鏀﹂柍褜鍓熼弻娑橆潩椤掑倐锝嗕繆椤愮姴鈧洟鍩㈤弮鍫濆嵆闁绘柨鎲＄€氭娊姊洪棃鈺呮婵犮垺锕㈠畷褰掓偩鐏炵浜?DNS闂備焦瀵х粙鎴︽嚐椤栨縿浜圭憸鏃堢嵁韫囨稒鏅滈柦妯侯槺閸旂敻鏌ｉ姀鈺佺仭闁烩剝娲熼敐鐐哄幢濞戞瑥鍓梺鍛婃处閸ㄨ鲸鎱ㄥ鍫熺厱闊浄绲芥晶鎵磽瀹ュ拋妯€鐎规洏鍎查幆鏃堟晲閸モ晪绱遍梻?
        return IsHostOrDomain(host, "localhost") ||
            IsHostOrDomain(host, "local") ||
            IsHostOrDomain(host, "internal") ||
            IsHostOrDomain(host, "lan");
    }

    private static bool IsHostOrDomain(string host, string domain)
    {
        return string.Equals(host, domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrivateOrSpecialIpv4(byte[] bytes)
    {
        return bytes[0] == 0 ||
            bytes[0] == 10 ||
            bytes[0] == 127 ||
            bytes[0] == 169 && bytes[1] == 254 ||
            bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
            bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0 ||
            bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2 ||
            bytes[0] == 192 && bytes[1] == 168 ||
            bytes[0] == 100 && bytes[1] is >= 64 and <= 127 ||
            bytes[0] == 198 && bytes[1] is 18 or 19 ||
            bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100 ||
            bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113 ||
            bytes[0] >= 224;
    }

    private static bool IsDocumentationIpv6(byte[] bytes)
    {
        return bytes[0] == 0x20 &&
            bytes[1] == 0x01 &&
            bytes[2] == 0x0d &&
            bytes[3] == 0xb8;
    }

    /// <summary>
    /// 结算启动后超过回调等待上限仍无结果（408/5xx/网络异常后 Linkly 没有回调，或回调丢失）时，
    /// 把会话收口为“结果未知”：IsActive=0 释放 POS 与终端，状态保持 Pending 且不写 ClientAcknowledgedAt，
    /// 因此终端管理闸门仍视为未决，直到客户端确认结果或主管结案（ack 带 supervisorResolved）。
    /// 若随后又收到迟到的回调，会话仍会被正常写成 Completed。
    /// </summary>
    private async Task<LinklyCloudBackendSessionRecord> CloseTimedOutSettlementAsync(
        LinklyCloudBackendSessionRecord session,
        CancellationToken cancellationToken)
    {
        if (!IsTimedOutActiveSettlement(session))
        {
            return session;
        }

        Log(
            $"settlement callback timed out sessionId={LogValue(session.SessionId)} " +
            $"lastHttp={session.LastHttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "<null>"} -> result-unknown");
        session.IsActive = false;
        session.RecoveryAction = LinklyCloudBackendStatusConstants.RecoveryResultUnknown;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        return await UpsertSessionAndReadLatestAsync(session, cancellationToken);
    }

    private static bool IsTimedOutActiveSettlement(LinklyCloudBackendSessionRecord session)
    {
        return IsOperation(session, OperationTypeSettlement) &&
            session.IsActive &&
            session.ClientAcknowledgedAt is null &&
            session.OperationSuccess is null &&
            string.Equals(session.Status, StatusPending, StringComparison.OrdinalIgnoreCase) &&
            DateTimeOffset.UtcNow - session.UpdatedAt >= LinklyTimeoutConstants.SettlementCallbackTimeout;
    }

    // 以下两个查询给“新付款/新结算前的占用检查”用：已超时的结算先收口再判断，避免它永久占住 POS 和终端。
    private async Task<LinklyCloudBackendSessionRecord?> GetActiveSessionUnlessSettlementTimedOutAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        var session = await repository.GetActiveSessionAsync(environment, storeCode, deviceCode, cancellationToken);
        return await StillBlockingAfterSettlementTimeoutAsync(session, cancellationToken);
    }

    private async Task<LinklyCloudBackendSessionRecord?> GetActiveSessionByTerminalUnlessSettlementTimedOutAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        CancellationToken cancellationToken)
    {
        var session = await repository.GetActiveSessionByTerminalAsync(environment, storeCode, terminalId, cancellationToken);
        return await StillBlockingAfterSettlementTimeoutAsync(session, cancellationToken);
    }

    private async Task<LinklyCloudBackendSessionRecord?> StillBlockingAfterSettlementTimeoutAsync(
        LinklyCloudBackendSessionRecord? session,
        CancellationToken cancellationToken)
    {
        if (session is null || !IsTimedOutActiveSettlement(session))
        {
            return session;
        }

        var closed = await CloseTimedOutSettlementAsync(session, cancellationToken);
        return closed.IsActive ? closed : null;
    }

    private async Task<LinklyCloudBackendSessionRecord> UpsertSessionAndReadLatestAsync(
        LinklyCloudBackendSessionRecord session,
        CancellationToken cancellationToken)
    {
        var latestBeforeWrite = await repository.GetSessionAsync(
            session.Environment,
            session.StoreCode,
            session.DeviceCode,
            session.SessionId,
            cancellationToken);
        if (ShouldKeepFinalResultSession(latestBeforeWrite, session))
        {
            return latestBeforeWrite!;
        }

        await repository.UpsertSessionAsync(session, cancellationToken);

        var latestAfterWrite = await repository.GetSessionAsync(
            session.Environment,
            session.StoreCode,
            session.DeviceCode,
            session.SessionId,
            cancellationToken);
        return ShouldKeepFinalResultSession(latestAfterWrite, session)
            ? latestAfterWrite!
            : latestAfterWrite ?? session;
    }

    private static bool ShouldKeepFinalResultSession(
        LinklyCloudBackendSessionRecord? latest,
        LinklyCloudBackendSessionRecord incoming)
    {
        if (latest is null || !IsProtectedFinalResult(latest))
        {
            return false;
        }

        if (!IsProtectedFinalResult(incoming))
        {
            return true;
        }

        // 已完成 session 只保护交易结果证据；允许旧资料补写缺失的 TransactionSuccess。
        var transactionSuccessConflict =
            latest.TransactionSuccess is not null &&
            incoming.TransactionSuccess is not null &&
            latest.TransactionSuccess != incoming.TransactionSuccess;
        var operationSuccessConflict =
            latest.OperationSuccess is not null &&
            incoming.OperationSuccess is not null &&
            latest.OperationSuccess != incoming.OperationSuccess;
        return !SameOptional(latest.OperationType, incoming.OperationType) ||
            !SameOptional(latest.TxnRef, incoming.TxnRef) ||
            transactionSuccessConflict ||
            operationSuccessConflict ||
            !SameOptional(latest.ResponseCode, incoming.ResponseCode) ||
            !SameOptional(latest.ResponseText, incoming.ResponseText) ||
            !SameOptional(latest.SettlementData, incoming.SettlementData);
    }

    private static bool ShouldRefreshOfficialTransaction(LinklyCloudBackendSessionRecord? session)
    {
        // 未确认的活动 session 和缺失 Success 的 completed session 都要向官方刷新结果。
        return session is { ClientAcknowledgedAt: null } &&
            (session.IsActive || ShouldRefreshCompletedMissingSuccess(session));
    }

    private static bool ShouldRefreshCompletedMissingSuccess(LinklyCloudBackendSessionRecord? session)
    {
        return session is { ClientAcknowledgedAt: null, TransactionSuccess: null } &&
            IsCompleted(session);
    }

    private async Task<LinklyCloudBackendSessionResponse> BuildResponseAsync(
        LinklyCloudBackendSessionRecord session,
        CancellationToken cancellationToken)
    {
        var notifications = await repository.GetNotificationsAsync(
            session.Environment,
            session.StoreCode,
            session.DeviceCode,
            session.SessionId,
            cancellationToken);
        // Settlement 回单以通知表为事实来源，避免并发 receipt/final 回调整行更新 session 时互相覆盖。
        var settlementReceiptTexts = IsOperation(session, OperationTypeSettlement)
            ? BuildSettlementReceiptTexts(session.SettlementReceiptTexts, notifications)
            : LinklyReceiptTextSanitizer.SanitizeReceipts(
                ReadSettlementReceiptTexts(session.SettlementReceiptTexts));
        var terminalDisplayName = session.TerminalId.HasValue && terminalService is not null
            ? await terminalService.GetTerminalDisplayNameAsync(
                session.Environment,
                session.StoreCode,
                session.TerminalId.Value,
                cancellationToken)
            : null;
        var persistedCardTransaction = BuildPersistedCardTransactionEvidence(session, notifications, out var verifiedNotification);
        var responseNotifications = HasPersistedRequestEvidence(session)
            ? notifications
                .Where(notification =>
                    !string.Equals(notification.Type, "transaction", StringComparison.OrdinalIgnoreCase) ||
                    verifiedNotification is not null && ReferenceEquals(notification, verifiedNotification))
                .ToArray()
            : notifications;
        var response = new LinklyCloudBackendSessionResponse(
            session.Environment,
            session.StoreCode,
            session.DeviceCode,
            session.SessionId,
            session.Status,
            session.TxnRef,
            session.ResponseCode,
            SanitizeOptionalText(session.ResponseText),
            session.RecoveryAction,
            SanitizeOptionalText(session.DisplayText),
            session.CancelKeyFlag,
            session.OKKeyFlag,
            session.AcceptYesKeyFlag,
            session.DeclineNoKeyFlag,
            session.AuthoriseKeyFlag,
            session.InputType,
            session.GraphicCode,
            SplitDisplayLines(session.DisplayLines)
                .Select(LinklyReceiptTextSanitizer.Sanitize)
                .ToArray(),
            SanitizeOptionalText(session.ReceiptText),
            session.RecoveryCount,
            session.ReceiptPrintedAt,
            session.ClientAcknowledgedAt,
            session.LastHttpStatus,
            responseNotifications.Select(notification => new LinklyCloudBackendNotificationDto(
                notification.Type,
                LinklyReceiptTextSanitizer.SanitizeSettlementData(notification.PayloadJson) ?? "{}",
                notification.ReceivedAt)).ToArray(),
            session.TransactionSuccess,
            NormalizeOperationType(session.OperationType),
            session.OperationSuccess,
            LinklyReceiptTextSanitizer.SanitizeSettlementData(session.SettlementData),
            settlementReceiptTexts,
            session.TerminalId,
            terminalDisplayName,
            session.CreatedAt);
        return response with
        {
            CardTransaction = persistedCardTransaction
        };
    }

    private static LinklyCloudBackendCardTransactionDto? BuildPersistedCardTransactionEvidence(
        LinklyCloudBackendSessionRecord session,
        IReadOnlyList<LinklyCloudBackendNotificationRecord> notifications,
        out LinklyCloudBackendNotificationRecord? verifiedNotification)
    {
        verifiedNotification = null;
        if (!IsOperation(session, OperationTypeTransaction) ||
            !IsCompleted(session) ||
            session.TransactionSuccess != true ||
            NormalizeOptional(session.TxnRef) is not { } txnRef ||
            NormalizeOptional(session.ResponseCode) is not { } responseCode ||
            !IsApprovedTransactionResponseCode(responseCode))
        {
            return null;
        }
        var responseText = NormalizeOptional(session.ResponseText);

        var hasAnyRequestEvidence = session.RequestTxnType is not null ||
            session.RequestAmountCents is not null ||
            session.RequestRfn is not null;
        if (!hasAnyRequestEvidence)
        {
            return BuildLegacyCardTransactionEvidence(
                notifications,
                txnRef,
                responseCode,
                responseText);
        }

        if (
            NormalizeOptional(session.RequestTxnType) is not { } requestTxnType ||
            session.RequestAmountCents is not { } requestAmountCents ||
            requestAmountCents == long.MinValue ||
            Math.Abs(requestAmountCents) > 999_999_999 ||
            !IsSafeRefundReference(session.RequestRfn))
        {
            return null;
        }

        var requestRfn = NormalizeOptional(session.RequestRfn);
        var requiresRfn = string.Equals(requestTxnType, "R", StringComparison.OrdinalIgnoreCase);
        if (requiresRfn && requestRfn is null)
        {
            return null;
        }

        foreach (var notification in notifications.Reverse())
        {
            if (!string.Equals(notification.Type, "transaction", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(notification.PayloadJson))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(notification.PayloadJson);
                var providerResponse = ReadResponse(document.RootElement);
                if (ReadBool(providerResponse, "Success") != true ||
                    !SameOptional(responseCode, ReadString(providerResponse, "ResponseCode")) ||
                    responseText is not null &&
                        !SameOptional(responseText, ReadString(providerResponse, "ResponseText")) ||
                    !string.Equals(requestTxnType, ReadString(providerResponse, "TxnType"), StringComparison.OrdinalIgnoreCase) ||
                    !MatchesPersistedAmount(requestAmountCents, ReadInt64(providerResponse, "AmtPurchase")) ||
                    !MatchesPersistedTransactionReference(txnRef, ReadString(providerResponse, "TxnRef")) ||
                    !MatchesPersistedRefundReference(requestTxnType, requestRfn, document.RootElement, providerResponse))
                {
                    continue;
                }

                // 只返回发送前持久化的三项核心证据；卡号、授权码等仍由控制器的白名单清洗器补充。
                verifiedNotification = notification;
                return new LinklyCloudBackendCardTransactionDto(
                    txnRef,
                    requestRfn,
                    null,
                    null,
                    null,
                    null,
                    responseCode,
                    SanitizeOptionalText(responseText),
                    null,
                    null,
                    AbsoluteAmountCents(requestAmountCents));
            }
            catch (JsonException)
            {
                // 损坏通知不是可恢复证据，继续查找同一会话的其他官方 transaction 通知。
            }
        }

        return null;
    }

    private static LinklyCloudBackendCardTransactionDto? BuildLegacyCardTransactionEvidence(
        IReadOnlyList<LinklyCloudBackendNotificationRecord> notifications,
        string txnRef,
        string responseCode,
        string? responseText)
    {
        foreach (var notification in notifications.Reverse())
        {
            if (!string.Equals(notification.Type, "transaction", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(notification.PayloadJson))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(notification.PayloadJson);
                var providerResponse = ReadResponse(document.RootElement);
                var providerTxnRef = NormalizeOptional(ReadString(providerResponse, "TxnRef"));
                var txnType = NormalizeOptional(ReadString(providerResponse, "TxnType"));
                var amount = ReadInt64(providerResponse, "AmtPurchase");
                var rfn = NormalizeOptional(TryReadRefundReference(document.RootElement, out _));
                if (ReadBool(providerResponse, "Success") != true ||
                    providerTxnRef is null ||
                    !IsCompleteGeneratedTxnRef(providerTxnRef) ||
                    !string.Equals(txnRef, providerTxnRef, StringComparison.OrdinalIgnoreCase) ||
                    txnType is not ("P" or "p" or "R" or "r") ||
                    amount is null or long.MinValue ||
                    Math.Abs(amount.Value) > 999_999_999 ||
                    !IsSafeRefundReference(rfn) ||
                    !SameOptional(responseCode, ReadString(providerResponse, "ResponseCode")) ||
                    responseText is not null &&
                        !SameOptional(responseText, ReadString(providerResponse, "ResponseText")))
                {
                    continue;
                }

                // 历史记录仅在单条官方通知自身携带完整且一致的全部证据时保持兼容。
                return new LinklyCloudBackendCardTransactionDto(
                    txnRef,
                    rfn,
                    null,
                    null,
                    null,
                    null,
                    responseCode,
                    SanitizeOptionalText(responseText),
                    null,
                    null,
                    Math.Abs(amount.Value));
            }
            catch (JsonException)
            {
                // 损坏的历史通知不能作为成功凭据。
            }
        }

        return null;
    }

    private static bool IsCompleteGeneratedTxnRef(string value)
    {
        return value.Length == 16 &&
            value[..12].All(char.IsAsciiDigit) &&
            value[12..].All(char.IsAsciiHexDigit);
    }

    private static bool IsApprovedTransactionResponseCode(string value)
    {
        return value is "00" or "08" or "11";
    }

    private static bool IsSafeRefundReference(string? value)
    {
        var normalized = NormalizeOptional(value);
        return normalized is { Length: <= 128 } &&
            normalized.All(character =>
                char.IsAsciiLetterOrDigit(character) || "-_./:".Contains(character));
    }

    private static bool MatchesPersistedTransactionReference(string expected, string? candidate)
    {
        var normalizedCandidate = NormalizeOptional(candidate);
        if (normalizedCandidate is null)
        {
            return false;
        }

        return string.Equals(expected, normalizedCandidate, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesPersistedAmount(long expected, long? candidate)
    {
        return candidate is { } value &&
            value != long.MinValue &&
            expected != long.MinValue &&
            AbsoluteAmountCents(value) == AbsoluteAmountCents(expected);
    }

    private static long AbsoluteAmountCents(long value)
    {
        return value == long.MinValue ? long.MaxValue : Math.Abs(value);
    }

    private static bool MatchesPersistedRefundReference(
        string requestTxnType,
        string? requestRfn,
        JsonElement root,
        JsonElement providerResponse)
    {
        var providerRfn = NormalizeOptional(TryReadRefundReference(root, out _));
        if (string.Equals(requestTxnType, "R", StringComparison.OrdinalIgnoreCase))
        {
            return requestRfn is not null &&
                string.Equals(requestRfn, providerRfn, StringComparison.OrdinalIgnoreCase);
        }

        return providerRfn is null ||
            requestRfn is not null &&
            string.Equals(requestRfn, providerRfn, StringComparison.OrdinalIgnoreCase);
    }

    private static long? ReadInt64(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numeric))
        {
            return numeric;
        }

        return value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out numeric)
                ? numeric
                : null;
    }

    private static async Task<LinklyCloudBackendTransportResponse> SendWithRecoverableFailureAsync(
        Func<Task<LinklyCloudBackendTransportResponse>> sendAsync)
    {
        try
        {
            return await sendAsync();
        }
        catch (HttpRequestException)
        {
            return new LinklyCloudBackendTransportResponse(HttpStatusCode.RequestTimeout, null);
        }
        catch (TaskCanceledException)
        {
            return new LinklyCloudBackendTransportResponse(HttpStatusCode.RequestTimeout, null);
        }
    }

    private async Task<LinklyCloudBackendTransportResponse> ExecuteTerminalTestRequestAsync(
        string environment,
        string storeCode,
        string deviceCode,
        LinklyCloudTerminalPaymentContext? terminalContext,
        Func<LinklyCloudBackendToken, Guid?, Task<LinklyCloudBackendTransportResponse>> sendAsync,
        CancellationToken cancellationToken)
    {
        var operationLease = terminalContext is null
            ? null
            : await terminalService!.AcquireOperationLeaseAsync(
                environment,
                storeCode,
                deviceCode,
                terminalContext,
                cancellationToken);
        var preserveLeaseUntilExpiry = false;
        try
        {
            // Token 获取失败发生在终端请求之前，可以安全释放；只有终端请求结果歧义才保留租约。
            var token = await tokenProvider.GetTokenAsync(
                environment,
                storeCode,
                deviceCode,
                terminalContext?.Terminal.TerminalId,
                cancellationToken);
            LinklyCloudBackendTransportResponse response;
            try
            {
                response = await sendAsync(token, terminalContext?.Terminal.TerminalId);
            }
            catch (HttpRequestException)
            {
                preserveLeaseUntilExpiry = operationLease is not null;
                return new LinklyCloudBackendTransportResponse(HttpStatusCode.RequestTimeout, null);
            }
            catch (OperationCanceledException)
            {
                preserveLeaseUntilExpiry = operationLease is not null;
                return new LinklyCloudBackendTransportResponse(HttpStatusCode.RequestTimeout, null);
            }
            catch
            {
                preserveLeaseUntilExpiry = operationLease is not null;
                throw;
            }

            preserveLeaseUntilExpiry = operationLease is not null &&
                IsAmbiguousTerminalTestResponse(response.StatusCode);
            return response;
        }
        finally
        {
            // 2xx/明确 4xx 可释放；网络、取消、408 或 5xx 可能已被终端接收，保留到自然过期。
            if (!preserveLeaseUntilExpiry && operationLease is not null && terminalService is not null)
            {
                try
                {
                    await terminalService.ReleaseOperationLeaseAsync(
                        environment,
                        storeCode,
                        operationLease.TerminalId,
                        operationLease.LeaseId,
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(
                        "Linkly Cloud terminal operation lease release failed environment={Environment} store={StoreCode} terminalId={TerminalId} error={ErrorType}",
                        environment,
                        storeCode,
                        operationLease.TerminalId,
                        ex.GetType().Name);
                }
            }
        }
    }

    private static bool IsAmbiguousTerminalTestResponse(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return statusCode == HttpStatusCode.RequestTimeout || code is >= 500 and <= 599;
    }

    private async Task TryRecordDefinitiveTerminalHealthAsync(
        LinklyCloudTerminalPaymentContext? terminalContext,
        HttpStatusCode statusCode,
        bool succeeded,
        CancellationToken cancellationToken)
    {
        // 超时、取消与 5xx 都可能已到达实体终端，沿用租约的未知结果语义，绝不伪造健康状态。
        if (terminalContext is null || !IsDefinitiveTerminalTestResponse(statusCode) || terminalService is null)
        {
            return;
        }

        try
        {
            var recorded = await terminalService.RecordHealthAsync(
                terminalContext,
                succeeded ? TerminalHealthHealthy : TerminalHealthUnhealthy,
                DateTime.UtcNow,
                cancellationToken);
            if (!recorded)
            {
                logger?.LogWarning(
                    "Linkly Cloud terminal health was not recorded because terminal configuration changed environment={Environment} store={StoreCode} terminalId={TerminalId}",
                    terminalContext.Terminal.Environment,
                    terminalContext.Terminal.StoreCode,
                    terminalContext.Terminal.TerminalId);
            }
        }
        catch (Exception ex)
        {
            // 健康快照是诊断信息；实体终端已确认的测试结果不能因快照写入失败变成 500。
            logger?.LogWarning(
                ex,
                "Linkly Cloud terminal health persistence failed environment={Environment} store={StoreCode} terminalId={TerminalId}",
                terminalContext.Terminal.Environment,
                terminalContext.Terminal.StoreCode,
                terminalContext.Terminal.TerminalId);
        }
    }

    private static bool IsDefinitiveTerminalTestResponse(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return statusCode == HttpStatusCode.OK ||
            code is >= 400 and < 500 && statusCode != HttpStatusCode.RequestTimeout;
    }

    private static void ApplyTransportResponse(
        LinklyCloudBackendSessionRecord session,
        LinklyCloudBackendTransportResponse response)
    {
        session.UpdatedAt = DateTimeOffset.UtcNow;
        session.LastHttpStatus = (int)response.StatusCode;
        session.RecoveryAction = null;
        if (response.StatusCode == HttpStatusCode.OK)
        {
            ApplyCompletedPayload(session, response.Body);
            return;
        }

        var code = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            session.Status = StatusPending;
            session.IsActive = true;
            return;
        }

        if (IsTransportRecoveryFailure(response.StatusCode))
        {
            session.Status = StatusPending;
            session.RecoveryAction = RecoveryRetry;
            session.IsActive = true;
            return;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            session.Status = StatusTokenRefreshRequired;
            session.RecoveryAction = RecoveryRefreshToken;
            session.IsActive = true;
            return;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            session.Status = StatusNotSubmitted;
            session.RecoveryAction = RecoveryRetryNewSession;
            session.IsActive = false;
            return;
        }

        session.Status = StatusFailed;
        session.ResponseText = $"Linkly Cloud returned HTTP {code}.";
        session.IsActive = false;
    }

    private static void ApplySettlementTransportResponse(
        LinklyCloudBackendSessionRecord session,
        LinklyCloudBackendTransportResponse response)
    {
        if (response.StatusCode == HttpStatusCode.OK)
        {
            session.LastHttpStatus = (int)response.StatusCode;
            ApplySettlementCompletedPayload(session, response.Body, DateTimeOffset.UtcNow);
            return;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.NotFound
            or HttpStatusCode.TooManyRequests)
        {
            // Settlement 没有 transaction GET 恢复路径：鉴权拒绝、404（原请求未成功提交）、429（限流，请求未被处理）
            // 都表示 Linkly 没有执行结算，必须立即释放终端锁。若沿用交易的“Pending+重试”语义，
            // 没有任何后续动作会收口这条会话，它会永久占用该 POS 和同一终端，刷卡、换线、配对全被挡住。
            var code = (int)response.StatusCode;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            session.LastHttpStatus = code;
            session.Status = StatusNotSubmitted;
            session.OperationSuccess = false;
            session.ResponseText = $"Linkly Cloud settlement was not submitted (HTTP {code}).";
            session.RecoveryAction = null;
            session.IsActive = false;
            return;
        }

        ApplyTransportResponse(session, response);
    }

    private static void ApplySendKeyTransportResponse(
        LinklyCloudBackendSessionRecord session,
        LinklyCloudBackendTransportResponse response)
    {
        session.UpdatedAt = DateTimeOffset.UtcNow;
        session.LastHttpStatus = (int)response.StatusCode;
        session.RecoveryAction = null;
        Log(
            $"sendkey response applied sessionId={LogValue(session.SessionId)} " +
            $"http={(int)response.StatusCode}");
        if (response.StatusCode == HttpStatusCode.OK)
        {
            ApplyCompletedPayload(session, response.Body);
            return;
        }

        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            session.Status = StatusPending;
            session.IsActive = true;
            return;
        }

        if (IsTransportRecoveryFailure(response.StatusCode))
        {
            session.Status = StatusPending;
            session.RecoveryAction = RecoveryRetry;
            session.IsActive = true;
            return;
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            session.Status = StatusTokenRefreshRequired;
            session.RecoveryAction = RecoveryRefreshToken;
            session.IsActive = true;
            return;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            session.Status = StatusNotSubmitted;
            session.RecoveryAction = RecoveryRetryNewSession;
            session.IsActive = false;
            return;
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            // Linkly 拒绝 sendkey 只说明终端动作未被接受；交易仍可能通过通知或状态查询完成。
            session.Status = StatusPending;
            session.IsActive = true;
            return;
        }

        var code = (int)response.StatusCode;
        session.Status = StatusFailed;
        session.ResponseText = $"Linkly Cloud returned HTTP {code}.";
        session.IsActive = false;
    }

    private static void ApplyCompletedPayload(
        LinklyCloudBackendSessionRecord session,
        string? payloadJson,
        bool updateTxnRef = true,
        bool updateOutcomeEvidence = true)
    {
        session.Status = StatusCompleted;
        session.IsActive = false;
        session.RecoveryAction = null;
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return;
        }

        using var document = JsonDocument.Parse(payloadJson);
        var response = ReadResponse(document.RootElement);
        var transactionSuccess = ReadBool(response, "Success");
        var responseCode = ReadString(response, "ResponseCode");
        var responseText = ReadString(response, "ResponseText");
        session.TransactionSuccess = transactionSuccess;
        if (IsCancelledResponse(transactionSuccess, responseCode, responseText))
        {
            // Cancelled 是 POS 内部最终状态，只能由 Linkly 200 的明确取消结果派生。
            session.Status = StatusCancelled;
        }
        if (updateTxnRef && !HasPersistedRequestEvidence(session))
        {
            // 仅旧记录沿用官方 TxnRef 回填；新记录的发送前引用不可被恢复响应覆盖。
            session.TxnRef = ReadString(response, "TxnRef") ?? session.TxnRef;
        }
        if (updateOutcomeEvidence)
        {
            session.ResponseCode = responseCode;
            session.ResponseText = SanitizeOptionalText(responseText);
        }
    }

    private static bool HasPersistedRequestEvidence(LinklyCloudBackendSessionRecord session)
    {
        return NormalizeOptional(session.RequestTxnType) is not null &&
            session.RequestAmountCents is not null &&
            NormalizeOptional(session.TxnRef) is not null;
    }

    private static string PreserveTransactionIdentityFields(string rawPayloadJson, string sanitizedPayloadJson)
    {
        try
        {
            using var rawDocument = JsonDocument.Parse(rawPayloadJson);
            var rawResponse = ReadResponse(rawDocument.RootElement);
            var sanitizedNode = JsonNode.Parse(sanitizedPayloadJson);
            var sanitizedResponse = sanitizedNode?[
                "Response"] as JsonObject ?? sanitizedNode as JsonObject;
            if (sanitizedResponse is null)
            {
                return sanitizedPayloadJson;
            }

            if (ReadString(rawResponse, "TxnRef") is { } txnRef)
            {
                sanitizedResponse["TxnRef"] = txnRef;
            }

            var rawPurchaseAnalysisData = ReadValue(rawResponse, "PurchaseAnalysisData");
            if (ReadString(rawPurchaseAnalysisData, "RFN") is { } rfn)
            {
                var sanitizedPurchaseAnalysisData = sanitizedResponse["PurchaseAnalysisData"] as JsonObject;
                if (sanitizedPurchaseAnalysisData is null)
                {
                    sanitizedPurchaseAnalysisData = new JsonObject();
                    sanitizedResponse["PurchaseAnalysisData"] = sanitizedPurchaseAnalysisData;
                }

                sanitizedPurchaseAnalysisData["RFN"] = rfn;
            }

            return sanitizedNode?.ToJsonString() ?? sanitizedPayloadJson;
        }
        catch (JsonException)
        {
            return sanitizedPayloadJson;
        }
    }

    private static void ApplySettlementCompletedPayload(
        LinklyCloudBackendSessionRecord session,
        string? payloadJson,
        DateTimeOffset receivedAt)
    {
        session.Status = StatusCompleted;
        session.IsActive = false;
        session.RecoveryAction = null;
        session.UpdatedAt = receivedAt;
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return;
        }

        using var document = JsonDocument.Parse(payloadJson);
        var response = ReadResponse(document.RootElement);
        session.OperationSuccess = ReadBool(response, "Success");
        session.ResponseCode = ReadString(response, "ResponseCode");
        session.ResponseText = SanitizeOptionalText(ReadString(response, "ResponseText"));
        session.SettlementData = LinklyReceiptTextSanitizer.SanitizeSettlementData(ReadString(response, "SettlementData"));
    }

    private static bool IsCancelledResponse(bool? transactionSuccess, string? responseCode, string? responseText)
    {
        // 必须先有明确失败结果，再按官方取消码或精确文案归类，避免把 declined/timeout 误判为取消。
        return transactionSuccess == false &&
            (IsCancelledResponseCode(responseCode) || IsCancelledResponseText(responseText));
    }

    private static bool IsCancelledResponseCode(string? responseCode)
    {
        var normalized = NormalizeOptional(responseCode);
        return string.Equals(normalized, "TM", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "HD", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCancelledResponseText(string? responseText)
    {
        var normalized = NormalizeOptional(responseText);
        return string.Equals(normalized, "OPERATOR CANCELLED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "TXN CANCELLED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "TRANSACTION CANCELLED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "CANCELLED", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyMissingTransactionSuccessPayload(LinklyCloudBackendSessionRecord session, string? payloadJson)
    {
        if (!CanFillMissingTransactionSuccess(session, payloadJson))
        {
            return;
        }

        using var document = JsonDocument.Parse(payloadJson!);
        var response = ReadResponse(document.RootElement);
        session.TransactionSuccess = ReadBool(response, "Success");
    }

    private static void ApplyCompletedMissingSuccessRefresh(
        LinklyCloudBackendSessionRecord session,
        LinklyCloudBackendTransportResponse response)
    {
        // 旧 completed 记录只补缺失的 Success；官方未返回最终结果时不能回滚本地 completed 证据。
        if (response.StatusCode == HttpStatusCode.OK)
        {
            ApplyMissingTransactionSuccessPayload(session, response.Body);
        }
    }

    private static bool CanFillMissingTransactionSuccess(LinklyCloudBackendSessionRecord session, string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return false;
        }

        using var document = JsonDocument.Parse(payloadJson);
        var response = ReadResponse(document.RootElement);
        var responseCode = ReadString(response, "ResponseCode");
        // 已完成交易只允许从同一结果证据补写 Success，避免迟到的失败通知覆盖已完成结果。
        return string.IsNullOrWhiteSpace(session.ResponseCode) ||
            (!string.IsNullOrWhiteSpace(responseCode) && SameOptional(session.ResponseCode, responseCode));
    }

    private async Task AddOfficialTransactionNotificationAsync(
        LinklyCloudBackendSessionRecord session,
        LinklyCloudBackendTransportResponse response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.OK ||
            string.IsNullOrWhiteSpace(response.Body))
        {
            return;
        }

        // 官方 GET transaction 的完整 payload 要作为恢复证据返回给 POS，用于 RFN、金额和收据证据解析。
        var payloadJson = LinklyReceiptTextSanitizer.SanitizeSettlementData(response.Body) ?? "{}";
        if (HasPersistedRequestEvidence(session))
        {
            // 与 webhook 通知同一口径：脱敏器会把交易引用、RFN 里的长数字串当成卡号掩掉，证据记录必须保留这两项
            // 身份字段才能和会话精确匹配。webhook 未送达时 GET 是唯一的官方证据，缺了它就核验不出卡交易明细。
            payloadJson = PreserveTransactionIdentityFields(response.Body, payloadJson);
        }

        await repository.AddNotificationAsync(new LinklyCloudBackendNotificationRecord
        {
            Environment = session.Environment,
            StoreCode = session.StoreCode,
            DeviceCode = session.DeviceCode,
            SessionId = session.SessionId,
            Type = "transaction",
            PayloadJson = payloadJson,
            ReceivedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    private void LogTransactionNotificationSnapshot(string sessionId, string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            var response = ReadResponse(root);
            var purchaseAnalysisData = ReadValue(response, "PurchaseAnalysisData");
            var refundReference = TryReadRefundReference(root, out var source);
            Log(
                $"transaction notification snapshot sessionId={LogValue(sessionId)} bytes={payloadJson.Length} " +
                $"rootKeys={DescribeKeys(root)} responseKeys={DescribeKeys(response)} " +
                $"responseCode={LogValue(ReadString(response, "ResponseCode"))} responseText={LogValue(ReadString(response, "ResponseText"))} " +
                $"padKind={DescribeKind(purchaseAnalysisData)} padKeys={DescribeKeys(purchaseAnalysisData)} " +
                $"rfnSource={LogValue(source)} rfn={LogValue(MaskReference(refundReference))}");
        }
        catch (JsonException ex)
        {
            Log($"transaction notification snapshot failed sessionId={LogValue(sessionId)} error={ex.GetType().Name}");
        }
    }

    private static void ApplyDisplayPayload(
        LinklyCloudBackendSessionRecord session,
        string payloadJson,
        DateTimeOffset receivedAt)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        var response = ReadResponse(root);
        var displayLines = ReadTextLines(root, "DisplayLines") ??
            ReadTextLines(response, "DisplayLines") ??
            ReadTextLines(root, "DisplayText") ??
            ReadTextLines(response, "DisplayText");
        var displayText = ReadText(root, "DisplayText") ??
            ReadText(response, "DisplayText") ??
            (displayLines is null ? null : string.Join(Environment.NewLine, displayLines));

        // display 閫氱煡浠ｈ〃褰撳墠缁堢鎻愮ず蹇収锛涚己澶卞瓧娈佃娓呯┖锛岄伩鍏嶆部鐢ㄤ笂涓€灞忔寜閿姸鎬併�?
        session.DisplayText = displayText;
        session.DisplayLines = displayLines is null ? null : string.Join(Environment.NewLine, displayLines);
        session.CancelKeyFlag = ReadBool(root, "CancelKeyFlag") ?? ReadBool(response, "CancelKeyFlag") ?? false;
        session.OKKeyFlag = ReadBool(root, "OKKeyFlag") ?? ReadBool(response, "OKKeyFlag") ?? false;
        session.AcceptYesKeyFlag = ReadBool(root, "AcceptYesKeyFlag") ?? ReadBool(response, "AcceptYesKeyFlag") ?? false;
        session.DeclineNoKeyFlag = ReadBool(root, "DeclineNoKeyFlag") ?? ReadBool(response, "DeclineNoKeyFlag") ?? false;
        session.AuthoriseKeyFlag = ReadBool(root, "AuthoriseKeyFlag") ?? ReadBool(response, "AuthoriseKeyFlag") ?? false;
        session.InputType = ReadString(root, "InputType") ?? ReadString(response, "InputType");
        session.GraphicCode = ReadString(root, "GraphicCode") ?? ReadString(response, "GraphicCode");
        Log(
            $"display notification applied sessionId={LogValue(session.SessionId)} " +
            $"display=\"{LogValue(TruncateForLog(displayText, 80))}\" " +
            $"cancel={session.CancelKeyFlag} ok={session.OKKeyFlag} " +
            $"yes={session.AcceptYesKeyFlag} no={session.DeclineNoKeyFlag} auth={session.AuthoriseKeyFlag}");

        // 鏄剧ず鍜屽皬绁ㄩ€氱煡鍙洿鏂拌緟鍔╁瓧娈碉紝涓嶆敼鍙樺凡瀹屾垚浜ゆ槗鐘舵€併�?
        session.UpdatedAt = receivedAt;
    }

    private static void ApplyReceiptPayload(
        LinklyCloudBackendSessionRecord session,
        string payloadJson,
        DateTimeOffset receivedAt)
    {
        var receiptText = ReadPayloadText(payloadJson, "ReceiptText");
        if (!string.IsNullOrWhiteSpace(receiptText))
        {
            session.ReceiptText = LinklyReceiptTextSanitizer.Sanitize(receiptText);
        }

        session.UpdatedAt = receivedAt;
    }

    private static void ApplySettlementReceiptPayload(
        LinklyCloudBackendSessionRecord session,
        string payloadJson,
        DateTimeOffset receivedAt)
    {
        var receiptText = ReadSettlementReceiptText(payloadJson);
        if (!string.IsNullOrWhiteSpace(receiptText))
        {
            receiptText = LinklyReceiptTextSanitizer.Sanitize(receiptText);
            var savedTexts = ReadSettlementReceiptTexts(session.SettlementReceiptTexts).ToList();
            savedTexts.Add(receiptText);
            session.SettlementReceiptTexts = JsonSerializer.Serialize(savedTexts, ServiceJsonOptions);
        }

        session.UpdatedAt = receivedAt;
    }

    private static IReadOnlyList<string> BuildSettlementReceiptTexts(
        string? serializedTexts,
        IReadOnlyList<LinklyCloudBackendNotificationRecord> notifications)
    {
        var receiptTexts = new List<string>();
        foreach (var notification in notifications)
        {
            if (!string.Equals(notification.Type, "receipt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var receiptText = ReadSettlementReceiptText(notification.PayloadJson);
            if (!string.IsNullOrWhiteSpace(receiptText))
            {
                receiptTexts.Add(receiptText);
            }
        }

        // 兼容通知事实表启用前已经保存到 session 的历史结算回单，避免与通知事实重复计数。
        if (receiptTexts.Count == 0)
        {
            receiptTexts.AddRange(ReadSettlementReceiptTexts(serializedTexts));
        }

        return LinklyReceiptTextSanitizer.SanitizeReceipts(receiptTexts);
    }

    private static string? ReadSettlementReceiptText(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            var response = ReadResponse(root);
            var receiptType = ReadString(root, "Type") ?? ReadString(response, "Type");
            return string.Equals(receiptType, "S", StringComparison.OrdinalIgnoreCase)
                ? ReadText(root, "ReceiptText") ?? ReadText(response, "ReceiptText")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ReadSettlementReceiptTexts(string? serializedTexts)
    {
        if (string.IsNullOrWhiteSpace(serializedTexts))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(serializedTexts, ServiceJsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [serializedTexts];
        }
    }

    private static bool IsTransportRecoveryFailure(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return statusCode == HttpStatusCode.RequestTimeout ||
            // 关键逻辑：429 是限流，HTTP 语义本身就是“稍后重试”，绝不能当作交易失败。
            // 它原先落到 ApplyTransportResponse 的兜底分支被写成 Status=Failed +
            // IsActive=false，此后 ShouldRefreshOfficialTransaction 恒为 false，
            // 该会话再也不向 Linkly 核对真实结果。终端若已批准，收银员却收到失败提示
            // 并重刷，就是重复扣款；而那一行会永久停在 Failed 无法自愈。
            statusCode == HttpStatusCode.TooManyRequests ||
            code is >= 500 and <= 599;
    }

    private static bool ShouldCountRecoveryResponse(HttpStatusCode statusCode)
    {
        // 202 闂備浇宕甸崑鐐电矙韫囨稑绀夐幖娣妼妗?Linkly 闂佽楠稿﹢閬嶁€﹂崼婵愬殨闁告挷鐒﹂弳婊堟煙缂併垹鏋涢�?recover 闂備浇宕垫慨鏉懨洪銏犵哗闂侇剙绉甸崕鎴澝归悡搴ｆ憼闁哄拋鍓熼幃姗€鎮欑捄杞版睏闂佽崵鍠愮换鍫ュ箖鐟欏嫭濯存繛鏉戭儏娴滈箖鏌ょ喊鍗炲闁崇粯娲樼换婵嬪閳ュ啿濮哥紓渚囧枛婢т粙骞夐幘顔芥櫇闁稿本姘ㄩ悾鎶芥⒑闂堟稓绠冲┑顖ｅ幖鍗卞┑鐘崇閸婂爼鐓崶銊︹拻缂佺姵宀搁弻锝夋晜鐠囪尙浠告�?
        return statusCode != HttpStatusCode.Accepted;
    }

    private static string? ReadPayloadText(string payloadJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        using var document = JsonDocument.Parse(payloadJson);
        return ReadText(document.RootElement, propertyName) ??
            ReadText(ReadResponse(document.RootElement), propertyName);
    }

    private static string? SanitizeOptionalText(string? value)
    {
        return value is null ? null : LinklyReceiptTextSanitizer.Sanitize(value);
    }

    private static bool IsOperation(LinklyCloudBackendSessionRecord? session, string operationType)
    {
        return session is not null &&
            string.Equals(NormalizeOperationType(session.OperationType), operationType, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeOperationType(string? operationType)
    {
        return string.Equals(operationType, OperationTypeSettlement, StringComparison.OrdinalIgnoreCase)
            ? OperationTypeSettlement
            : OperationTypeTransaction;
    }

    private static bool IsCompleted(LinklyCloudBackendSessionRecord session)
    {
        return string.Equals(session.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProtectedFinalResult(LinklyCloudBackendSessionRecord session)
    {
        return string.Equals(session.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(session.Status, StatusCancelled, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string>? EnsurePurchaseAnalysisData(
        string txnType,
        long amount,
        string txnRef,
        IReadOnlyDictionary<string, string>? purchaseAnalysisData)
    {
        var fields = purchaseAnalysisData is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(purchaseAnalysisData, StringComparer.OrdinalIgnoreCase);

        if (string.Equals(txnType, "P", StringComparison.OrdinalIgnoreCase) &&
            !fields.ContainsKey("RFN"))
        {
            // 后端异步链路�?TxnRef �?API 生成；写入原交易 RFN，后续退卡才能引用同一个原�?RFN�?
            fields["RFN"] = txnRef;
        }

        if (fields.Count == 0)
        {
            return null;
        }

        fields.TryAdd("AMT", amount.ToString("D9", CultureInfo.InvariantCulture));
        fields.TryAdd("PCM", "0000");
        return fields;
    }

    private static string? ReadAnalysisField(
        IReadOnlyDictionary<string, string>? fields,
        string fieldName)
    {
        if (fields is null)
        {
            return null;
        }

        foreach (var field in fields)
        {
            if (string.Equals(field.Key, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeOptional(field.Value);
            }
        }

        return null;
    }

    private static JsonElement ReadResponse(JsonElement root)
    {
        return TryGetProperty(root, "Response", out var response) && response.ValueKind == JsonValueKind.Object
            ? response
            : root;
    }

    private static bool ParseStatusTestResponse(
        string? bodyJson,
        out string? responseCode,
        out string? responseText,
        out string? responseTxnRef,
        out string? responseDate,
        out string? responseTime,
        out bool? loggedOn)
    {
        responseCode = null;
        responseText = null;
        responseTxnRef = null;
        responseDate = null;
        responseTime = null;
        loggedOn = null;
        if (string.IsNullOrWhiteSpace(bodyJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(bodyJson);
            var response = ReadResponse(document.RootElement);
            responseCode = ReadString(response, "ResponseCode");
            responseText = ReadString(response, "ResponseText");
            responseTxnRef = ReadString(response, "TxnRef");
            responseDate = ReadString(response, "Date");
            responseTime = ReadString(response, "Time");
            if (TryGetProperty(response, "LoggedOn", out var value) &&
                value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                loggedOn = value.GetBoolean();
            }
            return ReadBool(response, "Success") == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ParseLogonTestResponse(
        string? bodyJson,
        out string? responseCode,
        out string? responseText,
        out string? catid,
        out string? caid,
        out string? pinPadVersion)
    {
        responseCode = null;
        responseText = null;
        catid = null;
        caid = null;
        pinPadVersion = null;
        if (string.IsNullOrWhiteSpace(bodyJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(bodyJson);
            var response = ReadResponse(document.RootElement);
            responseCode = ReadString(response, "ResponseCode");
            responseText = ReadString(response, "ResponseText");
            catid = ReadString(response, "Catid");
            caid = ReadString(response, "Caid");
            pinPadVersion = ReadString(response, "PinPadVersion");
            return ReadBool(response, "Success") == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsSuccessfulStatusTestResponse(string? responseCode)
    {
        // Linkly 状态测试的 T0 表示终端状态正常，和交易批准码 00 都应算成功。
        return string.Equals(responseCode, "00", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(responseCode, "T0", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildStatusTestMessage(
        HttpStatusCode httpStatus,
        bool succeeded,
        string? responseCode,
        string? responseText)
    {
        if (succeeded)
        {
            return "ANZ Linkly Cloud transaction status test succeeded.";
        }

        if (!string.IsNullOrWhiteSpace(responseText))
        {
            return responseText.Trim();
        }

        if (!string.IsNullOrWhiteSpace(responseCode))
        {
            return $"ANZ Linkly Cloud transaction status test failed with response code {responseCode.Trim()}.";
        }

        return $"ANZ Linkly Cloud transaction status test failed with HTTP {(int)httpStatus}.";
    }

    private static string BuildLogonTestMessage(
        HttpStatusCode httpStatus,
        bool succeeded,
        string? responseCode,
        string? responseText)
    {
        if (succeeded)
        {
            return "ANZ Linkly Cloud logon succeeded.";
        }

        if (!string.IsNullOrWhiteSpace(responseText))
        {
            return responseText.Trim();
        }

        if (!string.IsNullOrWhiteSpace(responseCode))
        {
            return $"ANZ Linkly Cloud logon failed with response code {responseCode.Trim()}.";
        }

        return $"ANZ Linkly Cloud logon failed with HTTP {(int)httpStatus}.";
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

    private static string? ReadText(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Undefined ||
            !TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return NormalizeOptional(value.GetString());
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var lines = value
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => NormalizeOptional(item.GetString()))
            .Where(line => line is not null)
            .Select(line => line!)
            .ToArray();
        return lines.Length == 0 ? null : string.Join(Environment.NewLine, lines);
    }

    private static IReadOnlyList<string>? ReadTextLines(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Undefined ||
            !TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = NormalizeOptional(value.GetString());
            return text is null ? null : [text];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var lines = value
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => NormalizeOptional(item.GetString()))
            .Where(line => line is not null)
            .Select(line => line!)
            .ToArray();
        return lines.Length == 0 ? null : lines;
    }

    private static bool? ReadBool(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Undefined ||
            !TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed != 0,
            JsonValueKind.Number when value.TryGetInt32(out var parsed) => parsed != 0,
            _ => null
        };
    }

    private static IReadOnlyList<string> SplitDisplayLines(string? displayLines)
    {
        return string.IsNullOrWhiteSpace(displayLines)
            ? []
            : displayLines
                .Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeOptional)
                .Where(line => line is not null)
                .Select(line => line!)
                .ToArray();
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

    private static JsonElement ReadValue(JsonElement root, string propertyName)
    {
        return TryGetProperty(root, propertyName, out var value) ? value : default;
    }

    private static string? TryReadRefundReference(JsonElement root, out string? source)
    {
        var response = ReadResponse(root);
        var purchaseAnalysisData = ReadValue(response, "PurchaseAnalysisData");
        // 闂傚倷绀侀幖顐︽偋閸℃蛋鍥敊閹规劕小?Linkly payload �?FN/PAD 闂傚倸鍊风欢锟犲磻閳ь剟鏌涚€ｎ偅灏扮紒缁樼洴瀹曞崬螣閸濆嫬袘缂傚倷鐒︽晶搴ㄥ疾閻樿绠栧ù鐘差儏鎯熼梺鎸庢婵倕顭块幒妤佲�?PurchaseAnalysisData 闂傚倷绀侀幉锟犲礉閺囥垹绠犵€光偓閸曨偆鍔?
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

    private static string? MaskReference(string? value)
    {
        var normalized = NormalizeOptional(value);
        if (normalized is null || normalized.Length <= 8)
        {
            return normalized;
        }

        return $"{normalized[..4]}...{normalized[^4..]}";
    }

    private static string NormalizeEnvironment(string? environment)
    {
        return LinklyCloudCredentialService.NormalizeEnvironment(environment)
            ?? throw new LinklyCloudBackendValidationException("environment must be Production or Sandbox");
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        return NormalizeOptional(value)
            ?? throw new LinklyCloudBackendValidationException($"{fieldName} is required.");
    }

    private static string NormalizeSendKey(string? key)
    {
        // Linkly sendkey 闂備礁鎲￠悷顖涚濠靛牊鏆滈柟缁㈠枛閻淇婇妶鍌氫壕闂佷紮绲介妶绋款嚕椤掑倹鍠嗛柛鏇ㄥ墯椤斿秹鏌ｆ惔銏⑩姇妞ゃ劌锕顐︻敇閵忕姷锛欑紒缁㈠弮椤ユ挾绮堟径鎰厸闁稿本纰嶉幖鎰版煟閳瑥娲﹂悡銉︺亜閺冨洤鍚归柣顓炶嫰闇夐柨婵嗘閻棝鎮归幇顔兼灈鐎殿噮鍠涢ˇ鍙夈亜閹惧磭绉烘鐐差儐椤︾増鎯旈鑽ょ＝闂備胶顭堢换鎺楀储瑜旈、娆撳箛閺夎法顦ч梺闈涱槶閸庤京绱為幘缁樼�?
        var normalized = NormalizeOptional(key)?.ToUpperInvariant();
        return normalized switch
        {
            "0" or "1" or "2" or "3" => normalized,
            "OK" or "CANCEL" => "0",
            "YES" => "1",
            "NO" => "2",
            "AUTH" => "3",
            _ => throw new LinklyCloudBackendValidationException("key must be one of 0, 1, 2, or 3.")
        };
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<none>" : value.Trim();
    }

    private static string? TruncateForLog(string? value, int maxLength)
    {
        var normalized = NormalizeOptional(value);
        return normalized is null || normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }

    private static void Log(string message)
    {
        Console.WriteLine($"[HBPOS][Api][LinklyCloudBackend] {DateTimeOffset.Now:O} {message}");
    }

    private void LogRecoveryServiceEvidence(
        string operation,
        string phase,
        string direction,
        string environment,
        string storeCode,
        string deviceCode,
        string? transactionReference,
        string method,
        string url,
        string? requestJson,
        string? responseJson,
        bool? success,
        string? reason,
        object? response,
        string certCase)
    {
        var redactedRequestJson = RedactedEvidenceJsonText(requestJson);
        var redactedResponseJson = RedactedEvidenceJsonText(responseJson);
        LogServiceJson(
            operation,
            phase,
            direction,
            environment,
            storeCode,
            deviceCode,
            success,
            reason,
            requestJson is null
                ? null
                : new
                {
                    method,
                    url,
                    body = RedactedEvidenceJsonBody(requestJson)
                },
            responseJson is null ? response : RedactedEvidenceJsonBody(responseJson),
            new
            {
                certCase,
                method,
                url,
                transactionReference,
                requestJson = redactedRequestJson,
                responseJson = redactedResponseJson
            });
    }

    private static string? SerializeEvidenceJson<T>(T? value)
    {
        return value is null ? null : JsonSerializer.Serialize(value, ServiceJsonOptions);
    }

    private static string? RedactedEvidenceJsonText(string? bodyJson)
    {
        var redacted = RedactedEvidenceJsonBody(bodyJson);
        if (redacted is null)
        {
            return null;
        }

        return redacted is string text
            ? text
            : JsonSerializer.Serialize(redacted, ServiceJsonOptions);
    }

    private static object? RedactedEvidenceJsonBody(string? bodyJson)
    {
        if (string.IsNullOrWhiteSpace(bodyJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(bodyJson);
            return RedactEvidenceJsonElement(document.RootElement, propertyName: null);
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

    private static object? RedactEvidenceJsonElement(JsonElement element, string? propertyName)
    {
        if (IsSensitiveEvidenceLogProperty(propertyName))
        {
            return DescribeSensitiveEvidenceLogValue(element);
        }

        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => RedactEvidenceJsonElement(property.Value, property.Name),
                    StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(item => RedactEvidenceJsonElement(item, propertyName: null))
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

    private static bool IsSensitiveEvidenceLogProperty(string? propertyName)
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
            string.Equals(propertyName, "purchaseAnalysisData", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "pan", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "cardNumber", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "accountNumber", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "track2", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "secret", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "password", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "token", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "accessToken", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "authorization", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "authorizationHeader", StringComparison.OrdinalIgnoreCase);
    }

    private static object DescribeSensitiveEvidenceLogValue(JsonElement element)
    {
        var value = element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : element.GetRawText();
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        // 恢复证据日志只保留排障元数据，避免收据、通知 payload 或认证材料进入集中日志。
        return new
        {
            hasValue = normalized is not null,
            length = normalized?.Length ?? 0,
            lineCount = normalized is null ? 0 : normalized.Split('\n').Length
        };
    }

    private void LogServiceJson(
        string operation,
        string phase,
        string direction,
        string environment,
        string storeCode,
        string deviceCode,
        bool? success,
        string? reason,
        object? request,
        object? response,
        object? details)
    {
        logger?.LogInformation(
            "{LinklyJson}",
            BuildServiceJsonLog(
                operation,
                phase,
                direction,
                environment,
                storeCode,
                deviceCode,
                success,
                reason,
                request,
                response,
                details));
    }

    private static string BuildServiceJsonLog(
        string operation,
        string phase,
        string direction,
        string environment,
        string storeCode,
        string deviceCode,
        bool? success,
        string? reason,
        object? request,
        object? response,
        object? details)
    {
        return JsonSerializer.Serialize(
            new
            {
                source = "api-backend-service",
                operation,
                phase,
                direction,
                environment,
                sessionId = (string?)null,
                httpStatus = (int?)null,
                success,
                reason,
                elapsedMs = (long?)null,
                request,
                response,
                details = new
                {
                    timestamp = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                    storeCode,
                    deviceCode,
                    data = details
                }
            },
            ServiceJsonOptions);
    }

    private static object DescribeSecret(string secret)
    {
        var trimmed = secret.Trim();
        return new
        {
            hasSecret = trimmed.Length > 0,
            secretLength = trimmed.Length
        };
    }

    private static string GetComponentVersion()
    {
        var assembly = typeof(LinklyCloudBackendAsyncService).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
            assembly.GetName().Version?.ToString() ??
            "unknown";
    }

    private static string NormalizeUuidV4(string? value)
    {
        var normalized = NormalizeRequired(value, "posId");
        if (!Guid.TryParseExact(normalized, "D", out var parsed))
        {
            throw new LinklyCloudBackendValidationException("posId must be UUID v4.");
        }

        normalized = parsed.ToString("D");
        var version = normalized[14];
        var variant = normalized[19];
        if (version != '4' || variant is not ('8' or '9' or 'a' or 'b'))
        {
            throw new LinklyCloudBackendValidationException("posId must be UUID v4.");
        }

        return normalized;
    }

    private static bool SameOptional(string? left, string? right)
    {
        return string.Equals(NormalizeOptional(left), NormalizeOptional(right), StringComparison.OrdinalIgnoreCase);
    }

    private static LinklyCloudBackendHealthCheckDto CreateHealthCheck(
        string code,
        bool isReady,
        string readyMessage,
        string notReadyMessage)
    {
        return new LinklyCloudBackendHealthCheckDto(
            code,
            isReady,
            isReady ? readyMessage : notReadyMessage);
    }

    private static string CreateTxnRef()
    {
        Span<byte> bytes = stackalloc byte[2];
        Random.Shared.NextBytes(bytes);
        return $"{DateTimeOffset.UtcNow:yyMMddHHmmss}{Convert.ToHexString(bytes)}";
    }

    // 交易引用始终由 API 生成：客户端只提供本地 attempt 身份，由与 POS 共用的算法派生出 16 位引用。
    // POS 因此能在发请求前算出并落库同一个值，却无法指定任意引用去碰撞别的交易。
    private static string? DeriveAttemptTxnRef(string txnType, Guid? attemptGuid)
    {
        if (attemptGuid is null)
        {
            return null;
        }

        if (attemptGuid.Value == Guid.Empty || txnType is not ("P" or "R"))
        {
            throw new LinklyCloudBackendValidationException(
                "attemptGuid must be a non-empty GUID and is only supported for purchase (P) and refund (R) transactions.");
        }

        return LinklyAttemptTxnRef.Create(txnType[0], attemptGuid.Value);
    }
}

public sealed class LinklyCloudBackendValidationException(string message) : Exception(message);

public sealed class LinklyCloudBackendActiveTransactionException(string? activeSessionId)
    : Exception("An active Linkly Cloud transaction already exists for this terminal.")
{
    public string? ActiveSessionId { get; } = activeSessionId;
}

public sealed class LinklyCloudBackendSessionNotFoundException()
    : Exception("Linkly Cloud backend session was not found.");

public sealed class LinklyCloudBackendNotificationUnauthorizedException()
    : Exception("Linkly Cloud notification authorization is invalid.");

public interface ILinklyCloudBackendTokenProvider
{
    Task<LinklyCloudBackendToken> GetTokenAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendToken> GetTokenAsync(
        string environment,
        string storeCode,
        string deviceCode,
        Guid? terminalId,
        CancellationToken cancellationToken) =>
        GetTokenAsync(environment, storeCode, deviceCode, cancellationToken);
}

public sealed record LinklyCloudBackendToken(
    string RestBaseUrl,
    string AccessToken);

internal static class LinklyCloudTerminalConcurrencyGate
{
    private const int MaximumConcurrency = 2;
    // ponytail: 字典大小以上线后访问过的终端数为上限；仅在终端长期动态增长时再增加淘汰。
    private static readonly ConcurrentDictionary<
        (string Environment, string StoreCode, string DeviceCode),
        SemaphoreSlim> Gates = new();

    internal static async Task<T> RunAsync<T>(
        string environment,
        string storeCode,
        string deviceCode,
        Guid? terminalId,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        // 闸门按实体刷卡机计：有 TerminalId（线路管理）就用它，否则一台 POS 对应一台刷卡机，用设备码。
        // 由闸门统一取键，调用方只传原始身份，避免 Token 与各 REST 操作各自拼键而落进不同信号量。
        var terminalKey = terminalId is { } id && id != Guid.Empty ? id.ToString("D") : deviceCode;
        var key = (
            environment.Trim().ToUpperInvariant(),
            storeCode.Trim().ToUpperInvariant(),
            terminalKey.Trim().ToUpperInvariant());
        var gate = Gates.GetOrAdd(
            key,
            static _ => new SemaphoreSlim(MaximumConcurrency, MaximumConcurrency));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // 关键逻辑：Token 与 REST 共用当前终端的两个槽位，不阻塞其他刷卡机。
            return await action(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }
}

public sealed class HttpLinklyCloudBackendTokenProvider(
    ILinklyCloudCredentialRepository credentialRepository,
    ILinklyCloudBackendTerminalCredentialRepository terminalCredentialRepository,
    HttpClient httpClient,
    IOptions<LinklyCloudBackendAsyncOptions> options,
    ILogger<HttpLinklyCloudBackendTokenProvider>? logger = null,
    ILinklyCloudTerminalRepository? cloudTerminalRepository = null) : ILinklyCloudBackendTokenProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<LinklyCloudBackendToken> GetTokenAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        return await GetTokenAsync(environment, storeCode, deviceCode, terminalId: null, cancellationToken);
    }

    public async Task<LinklyCloudBackendToken> GetTokenAsync(
        string environment,
        string storeCode,
        string deviceCode,
        Guid? terminalId,
        CancellationToken cancellationToken)
    {
        var normalizedEnvironment = NormalizeEnvironment(environment);
        var normalizedStoreCode = NormalizeRequired(storeCode, "storeCode");
        var normalizedDeviceCode = NormalizeRequired(deviceCode, "deviceCode");
        var hasPhysicalTerminal = terminalId is { } value && value != Guid.Empty;

        if (!hasPhysicalTerminal && cloudTerminalRepository is not null)
        {
            var mode = await cloudTerminalRepository.GetConfigurationModeAsync(
                normalizedEnvironment,
                normalizedStoreCode,
                cancellationToken);
            if (string.Equals(mode?.Trim(), "Active", StringComparison.OrdinalIgnoreCase))
            {
                throw new LinklyCloudTerminalSelectionConflictException(
                    "Active Linkly Cloud multi-terminal mode requires terminalId.");
            }
        }

        string? terminalSecret;
        string? terminalPosId;
        if (hasPhysicalTerminal)
        {
            var terminal = cloudTerminalRepository is null
                ? null
                : await cloudTerminalRepository.GetAsync(
                    normalizedEnvironment,
                    normalizedStoreCode,
                    terminalId!.Value,
                    cancellationToken);
            terminalSecret = terminal?.Secret;
            terminalPosId = terminal?.PosId;
        }
        else
        {
            var credential = await credentialRepository.GetByStoreCodeAsync(
                normalizedStoreCode,
                normalizedEnvironment,
                cancellationToken);
            if (credential is null ||
                string.IsNullOrWhiteSpace(credential.Username) ||
                string.IsNullOrWhiteSpace(credential.Password))
            {
                throw new LinklyCloudBackendValidationException(
                    "Linkly Cloud credential is not configured for this store and environment.");
            }

            var terminalCredential = await terminalCredentialRepository.GetByDeviceAsync(
                normalizedEnvironment,
                normalizedStoreCode,
                normalizedDeviceCode,
                cancellationToken);
            terminalSecret = terminalCredential?.Secret;
            terminalPosId = terminalCredential?.PosId;
        }

        if (string.IsNullOrWhiteSpace(terminalSecret))
        {
            throw new LinklyCloudBackendValidationException(
                "Linkly Cloud terminal secret is not configured for this terminal.");
        }

        if (string.IsNullOrWhiteSpace(terminalPosId))
        {
            throw new LinklyCloudBackendValidationException(
                "Linkly Cloud POS ID is not configured for this terminal.");
        }

        var authBaseUrl = NormalizeRequired(GetAuthBaseUrl(normalizedEnvironment), "authBaseUrl");
        var restBaseUrl = NormalizeRequired(GetRestBaseUrl(normalizedEnvironment), "restBaseUrl");
        var posVendorId = NormalizeRequired(GetPosVendorId(normalizedEnvironment), "posVendorId");
        // 闂備礁鎲￠懝鎯归悜绛嬫�?token 闂佽崵濮村ú顓㈠绩闁秵鍎戦柣妤€鐗婃刊鎾偣閹帒濡介柡鍡楃箳缁辨挻鎷呴崫銉モ叡�?secret闂備線娼уΛ鏃傛導婵犵爤 identity 闂備礁鎲＄划宀勬儔婵傚摜宓侀柛鈩兩戝▍鐘绘煕閹扮數鍘涢柛銈囧█楠?endpoint�?
        var requestUri = new Uri(GetBaseUri(authBaseUrl), "tokens/cloudpos");
        var tokenRequest = new LinklyCloudBackendTokenRequest(
            terminalSecret.Trim(),
            NormalizeRequired(options.Value.PosName, "posName"),
            NormalizeRequired(options.Value.PosVersion, "posVersion"),
            terminalPosId.Trim(),
            posVendorId);
        LogTokenHttpRequest(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            requestUri,
            SerializeDebugJson(tokenRequest));

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = JsonContent.Create(tokenRequest, options: JsonOptions)
        };
        return await LinklyCloudTerminalConcurrencyGate.RunAsync(
            normalizedEnvironment,
            normalizedStoreCode,
            normalizedDeviceCode,
            terminalId,
            async gateCancellationToken =>
            {
                var stopwatch = Stopwatch.StartNew();
                using var response = await httpClient.SendAsync(request, gateCancellationToken);
                var body = await response.Content.ReadAsStringAsync(gateCancellationToken);
                stopwatch.Stop();
                LogTokenHttpResponse(
                    normalizedEnvironment,
                    normalizedStoreCode,
                    normalizedDeviceCode,
                    requestUri,
                    response.StatusCode,
                    stopwatch.ElapsedMilliseconds,
                    body);
                if (!response.IsSuccessStatusCode)
                {
                    throw new LinklyCloudBackendValidationException(
                        $"Linkly Cloud token endpoint returned HTTP {(int)response.StatusCode}.");
                }

                using var document = JsonDocument.Parse(body);
                var token = ReadString(document.RootElement, "token");
                if (string.IsNullOrWhiteSpace(token))
                {
                    throw new LinklyCloudBackendValidationException("Linkly Cloud token response was missing a token.");
                }

                return new LinklyCloudBackendToken(NormalizeBaseUrl(restBaseUrl), token);
            },
            cancellationToken);
    }

    private string GetAuthBaseUrl(string environment)
    {
        return string.Equals(environment, "Sandbox", StringComparison.Ordinal)
            ? options.Value.SandboxAuthBaseUrl
            : options.Value.ProductionAuthBaseUrl;
    }

    private string GetRestBaseUrl(string environment)
    {
        return string.Equals(environment, "Sandbox", StringComparison.Ordinal)
            ? options.Value.SandboxRestBaseUrl
            : options.Value.ProductionRestBaseUrl;
    }

    private string? GetPosVendorId(string environment)
    {
        return string.Equals(environment, "Sandbox", StringComparison.Ordinal)
            ? options.Value.SandboxPosVendorId
            : LinklyCloudIdentityConstants.ProductionPosVendorId;
    }

    private static string NormalizeEnvironment(string? environment)
    {
        return LinklyCloudCredentialService.NormalizeEnvironment(environment)
            ?? throw new LinklyCloudBackendValidationException("environment must be Production or Sandbox");
    }

    private static string NormalizeRequired(string? value, string fieldName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new LinklyCloudBackendValidationException($"{fieldName} is required.")
            : value.Trim();
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim();
        return trimmed.EndsWith("/", StringComparison.Ordinal) ? trimmed : trimmed + "/";
    }

    private static Uri GetBaseUri(string baseUrl)
    {
        return new Uri(NormalizeBaseUrl(baseUrl), UriKind.Absolute);
    }

    private static string SerializeDebugJson<T>(T value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private void LogTokenHttpRequest(
        string environment,
        string storeCode,
        string deviceCode,
        Uri url,
        string bodyJson)
    {
        logger?.LogInformation(
            "{LinklyJson}",
            BuildTokenJsonLog(
                "token",
                "request",
                "request",
                environment,
                storeCode,
                deviceCode,
                url,
                httpStatus: null,
                elapsedMs: null,
                request: RedactedJsonBody(bodyJson),
                response: null));
    }

    private void LogTokenHttpResponse(
        string environment,
        string storeCode,
        string deviceCode,
        Uri url,
        HttpStatusCode statusCode,
        long elapsedMs,
        string? bodyJson)
    {
        logger?.LogInformation(
            "{LinklyJson}",
            BuildTokenJsonLog(
                "token",
                "response",
                "response",
                environment,
                storeCode,
                deviceCode,
                url,
                statusCode,
                elapsedMs,
                request: null,
                response: RedactedJsonBody(bodyJson)));
    }

    private static string LogJsonBody(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<none>" : value.Trim();
    }

    private static string BuildTokenJsonLog(
        string operation,
        string phase,
        string direction,
        string environment,
        string storeCode,
        string deviceCode,
        Uri url,
        HttpStatusCode? httpStatus,
        long? elapsedMs,
        object? request,
        object? response)
    {
        return JsonSerializer.Serialize(
            new
            {
                source = "api-backend-token-provider",
                operation,
                phase,
                direction,
                environment,
                sessionId = (string?)null,
                httpStatus = httpStatus.HasValue ? (int)httpStatus.Value : (int?)null,
                success = httpStatus.HasValue ? ((int)httpStatus.Value is >= 200 and < 300) : (bool?)null,
                reason = (string?)null,
                elapsedMs,
                request,
                response,
                details = new
                {
                    timestamp = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                    method = "POST",
                    url = url.ToString(),
                    storeCode,
                    deviceCode
                }
            },
            JsonOptions);
    }

    private static object? RedactedJsonBody(string? bodyJson)
    {
        if (string.IsNullOrWhiteSpace(bodyJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(bodyJson);
            return RedactTokenJsonElement(document.RootElement, propertyName: null);
        }
        catch (JsonException)
        {
            return bodyJson;
        }
    }

    private static object? RedactTokenJsonElement(JsonElement element, string? propertyName)
    {
        if (string.Equals(propertyName, "secret", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "token", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "accessToken", StringComparison.OrdinalIgnoreCase))
        {
            var value = element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : element.GetRawText();
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            // Token provider 日志只保留排障元数据，不能记录 secret/token 原文。
            return new
            {
                hasValue = normalized is not null,
                length = normalized?.Length ?? 0
            };
        }

        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => RedactTokenJsonElement(property.Value, property.Name),
                    StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(item => RedactTokenJsonElement(item, propertyName: null))
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

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? NormalizeRequired(value.GetString(), propertyName)
            : null;
    }

    private sealed record LinklyCloudBackendTokenRequest(
        [property: JsonPropertyName("secret")] string Secret,
        [property: JsonPropertyName("posName")] string PosName,
        [property: JsonPropertyName("posVersion")] string PosVersion,
        [property: JsonPropertyName("posId")] string PosId,
        [property: JsonPropertyName("posVendorId")] string PosVendorId);
}

public interface ILinklyCloudBackendTerminalCredentialRepository
{
    Task<LinklyCloudBackendTerminalCredentialRecord?> GetByDeviceAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendTerminalCredentialRecord> UpsertAsync(
        string environment,
        string storeCode,
        string deviceCode,
        string secret,
        string posId,
        DateTime updatedAt,
        string? updatedBy,
        CancellationToken cancellationToken);

    // 旧版配对需要数据库级租约；所有实现都必须明确处理，不能回退到无保护写入。
    Task AcquireLegacyPairingLeaseAsync(
        string environment,
        string storeCode,
        Guid attemptId,
        DateTime leaseExpiresAt,
        DateTime now,
        CancellationToken cancellationToken);

    Task ReleaseLegacyPairingLeaseAsync(
        string environment,
        string storeCode,
        Guid attemptId,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendTerminalCredentialRecord> CompleteLegacyPairingAsync(
        string environment,
        string storeCode,
        string deviceCode,
        Guid attemptId,
        DateTime now,
        string secret,
        string posId,
        string? updatedBy,
        CancellationToken cancellationToken);
}

public sealed class LinklyCloudLegacyModeDisabledException()
    : Exception("Legacy Linkly Cloud credential endpoints are disabled while multi-terminal mode is Active.");

public sealed class LinklyCloudLegacyPairingLeaseConflictException()
    : Exception("Linkly Cloud pairing lease is no longer owned by this request.");

public sealed class SqlSugarLinklyCloudBackendTerminalCredentialRepository(
    HbposSqlSugarContext dbContext) : ILinklyCloudBackendTerminalCredentialRepository
{
    internal const string UpsertSql = """
        SET XACT_ABORT ON;
        SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
        BEGIN TRANSACTION;

        DECLARE @Mode NVARCHAR(16);
        SELECT @Mode = [Mode]
        FROM [dbo].[POSM_LinklyCloudConfigurationMode] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode;

        IF @Mode = N'Active'
            THROW 51005, 'Legacy Linkly Cloud endpoints are disabled while multi-terminal mode is Active.', 1;

        MERGE [dbo].[POSM_LinklyCloudBackendTerminal] WITH (HOLDLOCK) AS target
        USING (
            SELECT @Environment AS [Environment],
                   @StoreCode AS [StoreCode],
                   @DeviceCode AS [DeviceCode]) AS source
        ON target.[Environment] = source.[Environment]
           AND target.[StoreCode] = source.[StoreCode]
           AND target.[DeviceCode] = source.[DeviceCode]
        WHEN MATCHED THEN
            UPDATE SET
                [Secret] = @Secret,
                [PosId] = @PosId,
                [UpdatedAt] = @UpdatedAt,
                [UpdatedBy] = @UpdatedBy
        WHEN NOT MATCHED THEN
            INSERT ([Environment], [StoreCode], [DeviceCode], [Secret], [PosId], [UpdatedAt], [UpdatedBy])
            VALUES (@Environment, @StoreCode, @DeviceCode, @Secret, @PosId, @UpdatedAt, @UpdatedBy);

        COMMIT TRANSACTION;
        """;

    internal const string AcquireLegacyPairingLeaseSql = """
        SET XACT_ABORT ON;
        SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
        BEGIN TRANSACTION;

        DECLARE @Mode NVARCHAR(16);
        DECLARE @ExistingAttemptId UNIQUEIDENTIFIER;
        DECLARE @ExistingLeaseExpiresAt DATETIME2(7);
        SELECT @Mode = [Mode],
               @ExistingAttemptId = [LegacyPairingAttemptId],
               @ExistingLeaseExpiresAt = [LegacyPairingLeaseExpiresAt]
        FROM [dbo].[POSM_LinklyCloudConfigurationMode] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode;

        IF @Mode = N'Active'
            THROW 51005, 'Legacy Linkly Cloud endpoints are disabled while multi-terminal mode is Active.', 1;

        IF @Mode IS NULL
        BEGIN
            INSERT INTO [dbo].[POSM_LinklyCloudConfigurationMode] (
                [Environment], [StoreCode], [Mode], [LegacyPairingAttemptId], [LegacyPairingLeaseExpiresAt], [UpdatedAt], [UpdatedBy])
            VALUES (
                @Environment, @StoreCode, N'Legacy', @AttemptId, @LeaseExpiresAt, @Now, NULL);
        END
        ELSE
        BEGIN
            IF @ExistingAttemptId IS NOT NULL
               AND @ExistingLeaseExpiresAt IS NOT NULL
               AND @ExistingLeaseExpiresAt > @Now
                THROW 51007, 'Linkly Cloud legacy pairing is already in progress.', 1;

            UPDATE [dbo].[POSM_LinklyCloudConfigurationMode]
            SET [LegacyPairingAttemptId] = @AttemptId,
                [LegacyPairingLeaseExpiresAt] = @LeaseExpiresAt,
                [UpdatedAt] = @Now
            WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode;
        END;

        COMMIT TRANSACTION;
        """;

    internal const string ReleaseLegacyPairingLeaseSql = """
        UPDATE [dbo].[POSM_LinklyCloudConfigurationMode]
        SET [LegacyPairingAttemptId] = NULL,
            [LegacyPairingLeaseExpiresAt] = NULL
        WHERE [Environment] = @Environment
          AND [StoreCode] = @StoreCode
          AND [LegacyPairingAttemptId] = @AttemptId;
        """;

    internal const string CompleteLegacyPairingSql = """
        SET XACT_ABORT ON;
        SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
        BEGIN TRANSACTION;

        DECLARE @Mode NVARCHAR(16);
        DECLARE @ExistingAttemptId UNIQUEIDENTIFIER;
        DECLARE @ExistingLeaseExpiresAt DATETIME2(7);
        SELECT @Mode = [Mode],
               @ExistingAttemptId = [LegacyPairingAttemptId],
               @ExistingLeaseExpiresAt = [LegacyPairingLeaseExpiresAt]
        FROM [dbo].[POSM_LinklyCloudConfigurationMode] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode;

        IF @Mode = N'Active'
            THROW 51005, 'Legacy Linkly Cloud endpoints are disabled while multi-terminal mode is Active.', 1;

        IF @ExistingAttemptId IS NULL
           OR @ExistingAttemptId <> @AttemptId
           OR @ExistingLeaseExpiresAt IS NULL
           OR @ExistingLeaseExpiresAt <= @Now
            THROW 51006, 'Linkly Cloud pairing lease is no longer owned by this request.', 1;

        MERGE [dbo].[POSM_LinklyCloudBackendTerminal] WITH (HOLDLOCK) AS target
        USING (
            SELECT @Environment AS [Environment],
                   @StoreCode AS [StoreCode],
                   @DeviceCode AS [DeviceCode]) AS source
        ON target.[Environment] = source.[Environment]
           AND target.[StoreCode] = source.[StoreCode]
           AND target.[DeviceCode] = source.[DeviceCode]
        WHEN MATCHED THEN
            UPDATE SET
                [Secret] = @Secret,
                [PosId] = @PosId,
                [UpdatedAt] = @Now,
                [UpdatedBy] = @UpdatedBy
        WHEN NOT MATCHED THEN
            INSERT ([Environment], [StoreCode], [DeviceCode], [Secret], [PosId], [UpdatedAt], [UpdatedBy])
            VALUES (@Environment, @StoreCode, @DeviceCode, @Secret, @PosId, @Now, @UpdatedBy);

        UPDATE [dbo].[POSM_LinklyCloudConfigurationMode]
        SET [LegacyPairingAttemptId] = NULL,
            [LegacyPairingLeaseExpiresAt] = NULL,
            [UpdatedAt] = @Now
        WHERE [Environment] = @Environment
          AND [StoreCode] = @StoreCode
          AND [LegacyPairingAttemptId] = @AttemptId;

        IF @@ROWCOUNT <> 1
            THROW 51006, 'Linkly Cloud pairing lease is no longer owned by this request.', 1;

        SELECT TOP 1
            [Id], [Environment], [StoreCode], [DeviceCode], [Secret], [PosId], [UpdatedAt], [UpdatedBy]
        FROM [dbo].[POSM_LinklyCloudBackendTerminal] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Environment] = @Environment
          AND [StoreCode] = @StoreCode
          AND [DeviceCode] = @DeviceCode
          AND NULLIF(LTRIM(RTRIM([Secret])), '') IS NOT NULL
          AND NULLIF(LTRIM(RTRIM([PosId])), '') IS NOT NULL
        ORDER BY [UpdatedAt] DESC, [Id] DESC;

        COMMIT TRANSACTION;
        """;

    public async Task<LinklyCloudBackendTerminalCredentialRecord?> GetByDeviceAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                [Id],
                [Environment],
                [StoreCode],
                [DeviceCode],
                [Secret],
                [PosId],
                [UpdatedAt],
                [UpdatedBy]
            FROM [dbo].[POSM_LinklyCloudBackendTerminal]
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [DeviceCode] = @DeviceCode
              AND NULLIF(LTRIM(RTRIM([Secret])), '') IS NOT NULL
              AND NULLIF(LTRIM(RTRIM([PosId])), '') IS NOT NULL
            ORDER BY [UpdatedAt] DESC, [Id] DESC;
            """;

        var ado = dbContext.PosmDb.Ado;
        if (cancellationToken.CanBeCanceled)
        {
            ado.CancellationToken = cancellationToken;
        }

        try
        {
            return await ado.SqlQuerySingleAsync<LinklyCloudBackendTerminalCredentialRecord>(
                sql,
                new SugarParameter("@Environment", environment),
                new SugarParameter("@StoreCode", storeCode),
                new SugarParameter("@DeviceCode", deviceCode));
        }
        finally
        {
            if (cancellationToken.CanBeCanceled)
            {
                ado.RemoveCancellationToken();
            }
        }
    }

    public async Task<LinklyCloudBackendTerminalCredentialRecord> UpsertAsync(
        string environment,
        string storeCode,
        string deviceCode,
        string secret,
        string posId,
        DateTime updatedAt,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        try
        {
            // 旧端点写入前必须锁定配置模式，避免 controller 预检查与实际写入之间切换到 Active。
            await dbContext.PosmDb.Ado.ExecuteCommandAsync(
                UpsertSql,
                new
                {
                    Environment = environment,
                    StoreCode = storeCode,
                    DeviceCode = deviceCode,
                    Secret = secret,
                    PosId = posId,
                    UpdatedAt = updatedAt,
                    UpdatedBy = updatedBy
                },
                cancellationToken);
        }
        catch (Exception ex) when (IsSqlError(ex, "51005"))
        {
            throw new LinklyCloudLegacyModeDisabledException();
        }

        return await GetByDeviceAsync(environment, storeCode, deviceCode, cancellationToken)
            ?? new LinklyCloudBackendTerminalCredentialRecord
            {
                Environment = environment,
                StoreCode = storeCode,
                DeviceCode = deviceCode,
                Secret = secret,
                PosId = posId,
                UpdatedAt = updatedAt,
                UpdatedBy = updatedBy
            };
    }

    public async Task AcquireLegacyPairingLeaseAsync(
        string environment,
        string storeCode,
        Guid attemptId,
        DateTime leaseExpiresAt,
        DateTime now,
        CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.PosmDb.Ado.ExecuteCommandAsync(
                AcquireLegacyPairingLeaseSql,
                new
                {
                    Environment = environment,
                    StoreCode = storeCode,
                    AttemptId = attemptId,
                    LeaseExpiresAt = leaseExpiresAt,
                    Now = now
                },
                cancellationToken);
        }
        catch (Exception ex) when (IsSqlError(ex, "51005"))
        {
            throw new LinklyCloudLegacyModeDisabledException();
        }
        catch (Exception ex) when (IsSqlError(ex, "51007"))
        {
            throw new LinklyCloudPairingInProgressException();
        }
    }

    public Task ReleaseLegacyPairingLeaseAsync(
        string environment,
        string storeCode,
        Guid attemptId,
        CancellationToken cancellationToken) =>
        dbContext.PosmDb.Ado.ExecuteCommandAsync(
            ReleaseLegacyPairingLeaseSql,
            new
            {
                Environment = environment,
                StoreCode = storeCode,
                AttemptId = attemptId
            },
            cancellationToken);

    public async Task<LinklyCloudBackendTerminalCredentialRecord> CompleteLegacyPairingAsync(
        string environment,
        string storeCode,
        string deviceCode,
        Guid attemptId,
        DateTime now,
        string secret,
        string posId,
        string? updatedBy,
        CancellationToken cancellationToken)
    {
        try
        {
            var completed = await dbContext.PosmDb.Ado.SqlQuerySingleAsync<LinklyCloudBackendTerminalCredentialRecord>(
                CompleteLegacyPairingSql,
                new SugarParameter("@Environment", environment),
                new SugarParameter("@StoreCode", storeCode),
                new SugarParameter("@DeviceCode", deviceCode),
                new SugarParameter("@AttemptId", attemptId),
                new SugarParameter("@Now", now),
                new SugarParameter("@Secret", secret),
                new SugarParameter("@PosId", posId),
                new SugarParameter("@UpdatedBy", updatedBy));
            return completed ?? throw new LinklyCloudLegacyPairingLeaseConflictException();
        }
        catch (Exception ex) when (IsSqlError(ex, "51005"))
        {
            throw new LinklyCloudLegacyModeDisabledException();
        }
        catch (Exception ex) when (IsSqlError(ex, "51006"))
        {
            throw new LinklyCloudLegacyPairingLeaseConflictException();
        }
    }

    private static bool IsSqlError(Exception exception, string code) =>
        exception.ToString().Contains(code, StringComparison.OrdinalIgnoreCase);
}

public sealed class LinklyCloudBackendTerminalCredentialRecord
{
    public long Id { get; set; }

    public string? Environment { get; set; }

    public string? StoreCode { get; set; }

    public string? DeviceCode { get; set; }

    public string? Secret { get; set; }

    public string? PosId { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public string? UpdatedBy { get; set; }
}

public interface ILinklyCloudBackendAsyncTransport
{
    Task<LinklyCloudBackendTransportResponse> StartTransactionAsync(
        LinklyCloudBackendTransportTransactionRequest request,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendTransportResponse> StartSettlementAsync(
        LinklyCloudBackendTransportSettlementRequest request,
        CancellationToken cancellationToken) => throw new NotSupportedException("Linkly Cloud backend settlement is not supported by this transport.");

    Task<LinklyCloudBackendTransportResponse> RecoverTransactionAsync(
        LinklyCloudBackendTransportSessionRequest request,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendTransportResponse> GetTransactionAsync(
        LinklyCloudBackendTransportSessionRequest request,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendTransportResponse> SendLogonAsync(
        LinklyCloudBackendTransportSessionRequest request,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendTransportResponse> SendStatusAsync(
        LinklyCloudBackendTransportStatusRequest request,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendTransportResponse> SendKeyAsync(
        LinklyCloudBackendTransportSendKeyRequest request,
        CancellationToken cancellationToken);
}

public sealed record LinklyCloudBackendTransportResponse(
    HttpStatusCode StatusCode,
    string? Body);

public sealed record LinklyCloudBackendNotificationRequest(
    string Uri,
    string AuthorizationHeader);

public sealed record LinklyCloudBackendTransportTransactionRequest(
    string Environment,
    string RestBaseUrl,
    string AccessToken,
    string SessionId,
    string TxnType,
    long AmtPurchase,
    string TxnRef,
    IReadOnlyDictionary<string, string>? PurchaseAnalysisData,
    LinklyCloudBackendNotificationRequest Notification,
    string StoreCode,
    string DeviceCode,
    Guid? TerminalId = null);

public sealed record LinklyCloudBackendTransportSettlementRequest(
    string Environment,
    string RestBaseUrl,
    string AccessToken,
    string SessionId,
    LinklyCloudBackendNotificationRequest Notification,
    string StoreCode,
    string DeviceCode,
    Guid? TerminalId = null);

public sealed record LinklyCloudBackendTransportSessionRequest(
    string Environment,
    string RestBaseUrl,
    string AccessToken,
    string SessionId,
    string StoreCode,
    string DeviceCode,
    Guid? TerminalId = null);

public sealed record LinklyCloudBackendTransportStatusRequest(
    string Environment,
    string RestBaseUrl,
    string AccessToken,
    string SessionId,
    string StoreCode,
    string DeviceCode,
    Guid? TerminalId = null);

public sealed record LinklyCloudBackendTransportSendKeyRequest(
    string Environment,
    string RestBaseUrl,
    string AccessToken,
    string SessionId,
    string Key,
    string? Data,
    string StoreCode,
    string DeviceCode,
    Guid? TerminalId = null);

public sealed class HttpLinklyCloudBackendAsyncTransport(
    HttpClient httpClient,
    ILogger<HttpLinklyCloudBackendAsyncTransport>? logger = null) : ILinklyCloudBackendAsyncTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly JsonSerializerOptions LogJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public Task<LinklyCloudBackendTransportResponse> StartTransactionAsync(
        LinklyCloudBackendTransportTransactionRequest request,
        CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, object?>
        {
            ["Merchant"] = "00",
            ["Application"] = "00",
            ["TxnType"] = request.TxnType,
            ["AmtPurchase"] = request.AmtPurchase,
            ["TxnRef"] = request.TxnRef,
            ["CurrencyCode"] = "AUD",
            ["CutReceipt"] = "0",
            ["ReceiptAutoPrint"] = "0"
        };
        if (request.PurchaseAnalysisData is { Count: > 0 })
        {
            fields["PurchaseAnalysisData"] = request.PurchaseAnalysisData;
        }

        return SendAsync(
            request.Environment,
            request.StoreCode,
            request.DeviceCode,
            request.TerminalId,
            request.RestBaseUrl,
            request.AccessToken,
            request.SessionId,
            request.TxnType,
            request.TxnRef,
            operation: "transaction",
            endpointSegment: "transaction",
            asyncMode: true,
            method: HttpMethod.Post,
            body: new LinklyCloudBackendApiRequest(
                fields,
                new LinklyCloudBackendApiNotification(
                    request.Notification.Uri,
                    request.Notification.AuthorizationHeader)),
            cancellationToken);
    }

    public Task<LinklyCloudBackendTransportResponse> StartSettlementAsync(
        LinklyCloudBackendTransportSettlementRequest request,
        CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, object?>
        {
            ["Merchant"] = "00",
            ["Application"] = "00",
            ["SettlementType"] = "S",
            ["ReceiptAutoPrint"] = "0",
            ["CutReceipt"] = "0"
        };
        return SendAsync(
            request.Environment,
            request.StoreCode,
            request.DeviceCode,
            request.TerminalId,
            request.RestBaseUrl,
            request.AccessToken,
            request.SessionId,
            txnType: null,
            txnRef: null,
            operation: "settlement",
            endpointSegment: "settlement",
            asyncMode: true,
            method: HttpMethod.Post,
            body: new LinklyCloudBackendApiRequest(
                fields,
                new LinklyCloudBackendApiNotification(
                    request.Notification.Uri,
                    request.Notification.AuthorizationHeader)),
            cancellationToken);
    }

    public Task<LinklyCloudBackendTransportResponse> RecoverTransactionAsync(
        LinklyCloudBackendTransportSessionRequest request,
        CancellationToken cancellationToken)
    {
        return SendAsync(
            request.Environment,
            request.StoreCode,
            request.DeviceCode,
            request.TerminalId,
            request.RestBaseUrl,
            request.AccessToken,
            request.SessionId,
            txnType: null,
            txnRef: null,
            operation: "transaction",
            endpointSegment: "transaction",
            asyncMode: true,
            method: HttpMethod.Get,
            body: null,
            cancellationToken);
    }

    public Task<LinklyCloudBackendTransportResponse> GetTransactionAsync(
        LinklyCloudBackendTransportSessionRequest request,
        CancellationToken cancellationToken)
    {
        return SendAsync(
            request.Environment,
            request.StoreCode,
            request.DeviceCode,
            request.TerminalId,
            request.RestBaseUrl,
            request.AccessToken,
            request.SessionId,
            txnType: null,
            txnRef: null,
            operation: "transaction-status",
            endpointSegment: "transaction",
            // 重启恢复必须拿官方同步交易结果，不能只依赖本地通知缓存。
            asyncMode: false,
            method: HttpMethod.Get,
            body: null,
            cancellationToken);
    }

    public Task<LinklyCloudBackendTransportResponse> SendLogonAsync(
        LinklyCloudBackendTransportSessionRequest request,
        CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, object?>
        {
            ["Merchant"] = "00",
            ["LogonType"] = " ",
            ["Application"] = "00",
            ["ReceiptAutoPrint"] = "0",
            ["CutReceipt"] = "0"
        };

        return SendAsync(
            request.Environment,
            request.StoreCode,
            request.DeviceCode,
            request.TerminalId,
            request.RestBaseUrl,
            request.AccessToken,
            request.SessionId,
            txnType: null,
            txnRef: null,
            operation: "logon-test",
            endpointSegment: "logon",
            asyncMode: false,
            method: HttpMethod.Post,
            body: new LinklyCloudBackendApiRequest(fields, Notification: null),
            cancellationToken);
    }

    public Task<LinklyCloudBackendTransportResponse> SendStatusAsync(
        LinklyCloudBackendTransportStatusRequest request,
        CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, object?>
        {
            ["Merchant"] = "00",
            ["Application"] = "00",
            ["StatusType"] = "0"
        };

        return SendAsync(
            request.Environment,
            request.StoreCode,
            request.DeviceCode,
            request.TerminalId,
            request.RestBaseUrl,
            request.AccessToken,
            request.SessionId,
            txnType: null,
            txnRef: null,
            operation: "transaction-status-test",
            endpointSegment: "status",
            asyncMode: false,
            method: HttpMethod.Post,
            body: new LinklyCloudBackendApiRequest(fields, Notification: null),
            cancellationToken);
    }

    public Task<LinklyCloudBackendTransportResponse> SendKeyAsync(
        LinklyCloudBackendTransportSendKeyRequest request,
        CancellationToken cancellationToken)
    {
        var fields = new Dictionary<string, object?>
        {
            ["Key"] = request.Key
        };
        if (!string.IsNullOrWhiteSpace(request.Data))
        {
            fields["Data"] = request.Data;
        }

        return SendAsync(
            request.Environment,
            request.StoreCode,
            request.DeviceCode,
            request.TerminalId,
            request.RestBaseUrl,
            request.AccessToken,
            request.SessionId,
            txnType: null,
            txnRef: null,
            operation: "sendkey",
            endpointSegment: "sendkey",
            // Linkly 官方 SendKey 示例使用同步模式；取消键必须拿到明确接收结果，不能只拿 202 已开始。
            asyncMode: false,
            method: HttpMethod.Post,
            body: new LinklyCloudBackendApiRequest(fields, Notification: null),
            cancellationToken);
    }

    private async Task<LinklyCloudBackendTransportResponse> SendAsync(
        string environment,
        string storeCode,
        string deviceCode,
        Guid? terminalId,
        string restBaseUrl,
        string accessToken,
        string sessionId,
        string? txnType,
        string? txnRef,
        string operation,
        string endpointSegment,
        bool asyncMode,
        HttpMethod method,
        LinklyCloudBackendApiRequest? body,
        CancellationToken cancellationToken)
    {
        var requestBodyJson = body is null ? null : SerializeDebugJson(body);
        using var request = new HttpRequestMessage(
            method,
            new Uri(GetBaseUri(restBaseUrl), $"sessions/{Uri.EscapeDataString(sessionId)}/{endpointSegment}?async={asyncMode.ToString().ToLowerInvariant()}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        LogLinklyHttpRequest(
            environment,
            sessionId,
            operation,
            method,
            request.RequestUri!,
            txnType,
            txnRef,
            requestBodyJson);
        return await LinklyCloudTerminalConcurrencyGate.RunAsync(
            environment,
            storeCode,
            deviceCode,
            terminalId,
            async gateCancellationToken =>
            {
                var stopwatch = Stopwatch.StartNew();
                using var response = await httpClient.SendAsync(request, gateCancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(gateCancellationToken);
                stopwatch.Stop();
                LogLinklyHttpResponse(
                    environment,
                    sessionId,
                    operation,
                    method,
                    request.RequestUri!,
                    response.StatusCode,
                    stopwatch.ElapsedMilliseconds,
                    txnType,
                    txnRef,
                    responseBody);
                return new LinklyCloudBackendTransportResponse(response.StatusCode, responseBody);
            },
            cancellationToken);
    }

    private static Uri GetBaseUri(string baseUrl)
    {
        var trimmed = baseUrl.Trim();
        return new Uri(trimmed.EndsWith("/", StringComparison.Ordinal) ? trimmed : trimmed + "/", UriKind.Absolute);
    }

    private static string SerializeDebugJson<T>(T value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private void LogLinklyHttpRequest(
        string environment,
        string sessionId,
        string operation,
        HttpMethod method,
        Uri url,
        string? txnType,
        string? txnRef,
        string? bodyJson)
    {
        logger?.LogInformation(
            "{LinklyJson}",
            BuildTransportJsonLog(
                operation,
                "request",
                "request",
                environment,
                sessionId,
                method,
                url,
                httpStatus: null,
                elapsedMs: null,
                txnType,
                txnRef,
                transactionReference: sessionId,
                txnRefSpecified: !string.IsNullOrWhiteSpace(txnRef),
                requestJson: bodyJson,
                responseJson: null,
                request: RedactedJsonBody(bodyJson),
                response: null));
    }

    private void LogLinklyHttpResponse(
        string environment,
        string sessionId,
        string operation,
        HttpMethod method,
        Uri url,
        HttpStatusCode statusCode,
        long elapsedMs,
        string? txnType,
        string? txnRef,
        string? bodyJson)
    {
        logger?.LogInformation(
            "{LinklyJson}",
            BuildTransportJsonLog(
                operation,
                "response",
                "response",
                environment,
                sessionId,
                method,
                url,
                statusCode,
                elapsedMs,
                txnType,
                txnRef,
                transactionReference: sessionId,
                txnRefSpecified: !string.IsNullOrWhiteSpace(txnRef),
                requestJson: null,
                responseJson: bodyJson,
                request: null,
                response: RedactedJsonBody(bodyJson)));
    }

    private static string LogValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<null>" : value.Trim();
    }

    private static string LogJsonBody(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "<none>" : value.Trim();
    }

    private static string BuildTransportJsonLog(
        string operation,
        string phase,
        string direction,
        string environment,
        string sessionId,
        HttpMethod method,
        Uri url,
        HttpStatusCode? httpStatus,
        long? elapsedMs,
        string? txnType,
        string? txnRef,
        string transactionReference,
        bool txnRefSpecified,
        string? requestJson,
        string? responseJson,
        object? request,
        object? response)
    {
        var responseDetails = ReadTransportResponseDetails(responseJson);
        return JsonSerializer.Serialize(
            new
            {
                source = "api-backend-transport",
                operation,
                phase,
                direction,
                environment,
                sessionId,
                httpStatus = httpStatus.HasValue ? (int)httpStatus.Value : (int?)null,
                success = httpStatus.HasValue ? ((int)httpStatus.Value is >= 200 and < 300) : (bool?)null,
                reason = (string?)null,
                elapsedMs,
                request,
                response,
                details = new
                {
                    timestamp = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                    method = method.Method,
                    url = url.ToString(),
                    transactionReference,
                    txnType,
                    txnRef,
                    txnRefSpecified,
                    requestJson = RedactedJsonText(requestJson),
                    responseJson = RedactedJsonText(responseJson),
                    responseTxnRef = responseDetails.TxnRef,
                    responseDate = responseDetails.Date,
                    responseTime = responseDetails.Time,
                    responseCode = responseDetails.ResponseCode,
                    responseText = string.IsNullOrWhiteSpace(responseDetails.ResponseText)
                        ? null
                        : LinklyReceiptTextSanitizer.Sanitize(responseDetails.ResponseText)
                }
            },
            LogJsonOptions);
    }

    private static LinklyTransportResponseDetails ReadTransportResponseDetails(string? responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return LinklyTransportResponseDetails.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(responseJson);
            var response = TryGetProperty(document.RootElement, "Response", out var nested) &&
                nested.ValueKind == JsonValueKind.Object
                ? nested
                : document.RootElement;
            return new LinklyTransportResponseDetails(
                ReadString(response, "TxnRef"),
                ReadString(response, "Date"),
                ReadString(response, "Time"),
                ReadString(response, "ResponseCode"),
                ReadString(response, "ResponseText"));
        }
        catch (JsonException)
        {
            return LinklyTransportResponseDetails.Empty;
        }
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }

    private static string? RedactedJsonText(string? bodyJson)
    {
        var redacted = RedactedJsonBody(bodyJson);
        if (redacted is null)
        {
            return null;
        }

        return redacted is string text
            ? text
            : JsonSerializer.Serialize(redacted, LogJsonOptions);
    }

    private static object? RedactedJsonBody(string? bodyJson)
    {
        if (string.IsNullOrWhiteSpace(bodyJson))
        {
            return null;
        }

        var sanitizedBody = LinklyReceiptTextSanitizer.SanitizeSettlementData(bodyJson) ?? string.Empty;
        try
        {
            using var document = JsonDocument.Parse(sanitizedBody);
            return RedactJsonElement(document.RootElement, propertyName: null);
        }
        catch (JsonException)
        {
            return LinklyReceiptTextSanitizer.Sanitize(sanitizedBody);
        }
    }

    private static object? RedactJsonElement(JsonElement element, string? propertyName)
    {
        if (IsSensitiveLogProperty(propertyName))
        {
            return DescribeSensitiveLogValue(propertyName!, element);
        }

        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => RedactJsonElement(property.Value, property.Name),
                    StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(item => RedactJsonElement(item, propertyName: null))
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
        return string.Equals(propertyName, "secret", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "password", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "token", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "accessToken", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "authorization", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "authorizationHeader", StringComparison.OrdinalIgnoreCase);
    }

    private static object DescribeSensitiveLogValue(string propertyName, JsonElement element)
    {
        var value = element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : element.GetRawText();

        if (propertyName.Contains("authorization", StringComparison.OrdinalIgnoreCase))
        {
            return DescribeAuthorizationHeader(value);
        }

        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        // 日志只保留排障元数据，避免 secret/token 原文进入本地或集中日志。
        return new
        {
            hasValue = normalized is not null,
            length = normalized?.Length ?? 0
        };
    }

    private static object DescribeAuthorizationHeader(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var separator = normalized?.IndexOf(' ', StringComparison.Ordinal) ?? -1;
        return new
        {
            hasValue = normalized is not null,
            scheme = separator > 0 ? normalized![..separator] : null,
            length = normalized?.Length ?? 0
        };
    }

    private sealed record LinklyCloudBackendApiRequest(
        [property: JsonPropertyName("Request")] IReadOnlyDictionary<string, object?> Request,
        [property: JsonPropertyName("Notification")] LinklyCloudBackendApiNotification? Notification);

    private sealed record LinklyCloudBackendApiNotification(
        [property: JsonPropertyName("Uri")] string Uri,
        [property: JsonPropertyName("AuthorizationHeader")] string AuthorizationHeader);

    private sealed record LinklyTransportResponseDetails(
        string? TxnRef,
        string? Date,
        string? Time,
        string? ResponseCode,
        string? ResponseText)
    {
        public static LinklyTransportResponseDetails Empty { get; } = new(null, null, null, null, null);
    }
}

public interface ILinklyCloudBackendAsyncRepository
{
    Task<bool> TryCreateSessionAsync(
        LinklyCloudBackendSessionRecord session,
        CancellationToken cancellationToken);

    Task UpsertSessionAsync(
        LinklyCloudBackendSessionRecord session,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendSessionRecord?> GetSessionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        string sessionId,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendSessionRecord?> GetSessionByEnvironmentSessionIdAsync(
        string environment,
        string sessionId,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendSessionRecord?> GetActiveSessionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendSessionRecord?> GetActiveSessionByTerminalAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        CancellationToken cancellationToken) => Task.FromResult<LinklyCloudBackendSessionRecord?>(null);

    Task<LinklyCloudBackendSessionRecord?> GetBlockingSessionByTerminalAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        CancellationToken cancellationToken) =>
        GetActiveSessionByTerminalAsync(environment, storeCode, terminalId, cancellationToken);

    Task<LinklyCloudBackendSessionRecord?> GetResumableSessionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken);

    Task<LinklyCloudBackendSessionRecord?> GetResumableSettlementSessionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken) => Task.FromResult<LinklyCloudBackendSessionRecord?>(null);

    Task<LinklyCloudBackendSessionRecord?> AcknowledgeSessionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        string sessionId,
        DateTimeOffset acknowledgedAt,
        bool supervisorResolved,
        CancellationToken cancellationToken);

    Task AddNotificationAsync(
        LinklyCloudBackendNotificationRecord notification,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<LinklyCloudBackendNotificationRecord>> GetNotificationsAsync(
        string environment,
        string storeCode,
        string deviceCode,
        string sessionId,
        CancellationToken cancellationToken);
}

public sealed class InMemoryLinklyCloudBackendAsyncRepository : ILinklyCloudBackendAsyncRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LinklyCloudBackendSessionRecord> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LinklyCloudBackendNotificationRecord> _notifications = [];

    public Task<bool> TryCreateSessionAsync(
        LinklyCloudBackendSessionRecord session,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_sessions.Values.Any(existing =>
                existing.IsActive &&
                SameTerminal(existing, session.Environment, session.StoreCode, session.DeviceCode)))
            {
                return Task.FromResult(false);
            }

            if (session.TerminalId.HasValue && _sessions.Values.Any(existing =>
                existing.IsActive &&
                existing.TerminalId == session.TerminalId &&
                string.Equals(existing.Environment, session.Environment, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.StoreCode, session.StoreCode, StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult(false);
            }

            // 与 SQL 的 UX_..._TxnRef 过滤唯一索引一致：Settlement 会话没有 TxnRef，空值之间不算冲突。
            if (session.TxnRef is not null && _sessions.Values.Any(existing =>
                string.Equals(existing.Environment, session.Environment, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.StoreCode, session.StoreCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.TxnRef, session.TxnRef, StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult(false);
            }

            var created = Clone(session);
            created.CreatedAt ??= DateTimeOffset.UtcNow;
            _sessions[SessionKey(session.Environment, session.StoreCode, session.DeviceCode, session.SessionId)] = created;
            return Task.FromResult(true);
        }
    }

    public Task UpsertSessionAsync(
        LinklyCloudBackendSessionRecord session,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var key = SessionKey(session.Environment, session.StoreCode, session.DeviceCode, session.SessionId);
            if (_sessions.TryGetValue(key, out var existing) &&
                IsCompleted(existing) &&
                !IsCompleted(session))
            {
                return Task.CompletedTask;
            }

            var next = Clone(session);
            // 与 SQL 的 MERGE 一致：创建时间只在首次写入时确定，之后的更新不改它。
            next.CreatedAt = existing?.CreatedAt ?? next.CreatedAt ?? DateTimeOffset.UtcNow;
            if (_sessions.TryGetValue(key, out existing))
            {
                // 并发恢复写回不得覆盖已落库的发送前证据或对应完整 TxnRef。
                if (!string.IsNullOrWhiteSpace(existing.RequestTxnType) &&
                    existing.RequestAmountCents is not null &&
                    !string.IsNullOrWhiteSpace(existing.RequestRfn))
                {
                    next.TxnRef = existing.TxnRef;
                }

                next.RequestTxnType ??= existing.RequestTxnType;
                next.RequestAmountCents ??= existing.RequestAmountCents;
                next.RequestRfn ??= existing.RequestRfn;
            }

            if (_sessions.TryGetValue(key, out existing) &&
                existing.ClientAcknowledgedAt is not null &&
                next.ClientAcknowledgedAt is null)
            {
                next.ClientAcknowledgedAt = existing.ClientAcknowledgedAt;
            }

            _sessions[key] = next;
            return Task.CompletedTask;
        }
    }

    public Task<LinklyCloudBackendSessionRecord?> GetSessionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        string sessionId,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<LinklyCloudBackendSessionRecord?>(_sessions.TryGetValue(SessionKey(environment, storeCode, deviceCode, sessionId), out var session)
                ? Clone(session)
                : null);
        }
    }

    public Task<LinklyCloudBackendSessionRecord?> GetSessionByEnvironmentSessionIdAsync(
        string environment,
        string sessionId,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var session = _sessions.Values.FirstOrDefault(existing =>
                string.Equals(existing.Environment, environment, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(session is null ? null : Clone(session));
        }
    }

    public Task<LinklyCloudBackendSessionRecord?> GetActiveSessionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var session = _sessions.Values
                .Where(existing =>
                    SameTerminal(existing, environment, storeCode, deviceCode) &&
                    existing.IsActive)
                // 中文注释：active 只用于新付款前的即时阻塞；历史终态恢复继续走 resumable，避免清完一条又弹下一条旧记录。
                .OrderByDescending(existing => existing.UpdatedAt)
                .ThenByDescending(existing => existing.Id)
                .FirstOrDefault();
            return Task.FromResult(session is null ? null : Clone(session));
        }
    }

    public Task<LinklyCloudBackendSessionRecord?> GetActiveSessionByTerminalAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var session = _sessions.Values
                .Where(existing =>
                    existing.IsActive &&
                    existing.TerminalId == terminalId &&
                    string.Equals(existing.Environment, environment, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(existing => existing.UpdatedAt)
                .ThenByDescending(existing => existing.Id)
                .FirstOrDefault();
            return Task.FromResult(session is null ? null : Clone(session));
        }
    }

    public Task<LinklyCloudBackendSessionRecord?> GetBlockingSessionByTerminalAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var session = _sessions.Values
                .Where(existing =>
                    existing.TerminalId == terminalId &&
                    string.Equals(existing.Environment, environment, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase) &&
                    (existing.IsActive ||
                        IsFinalForClientRecovery(existing) && existing.ClientAcknowledgedAt is null))
                .OrderBy(existing => existing.IsActive ? 0 : 1)
                .ThenByDescending(existing => existing.UpdatedAt)
                .ThenByDescending(existing => existing.Id)
                .FirstOrDefault();
            return Task.FromResult(session is null ? null : Clone(session));
        }
    }

    public Task<LinklyCloudBackendSessionRecord?> GetResumableSessionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var session = _sessions.Values
                .Where(existing =>
                    SameTerminal(existing, environment, storeCode, deviceCode) &&
                    !string.Equals(existing.OperationType, "Settlement", StringComparison.OrdinalIgnoreCase) &&
                    (existing.IsActive ||
                        IsFinalForClientRecovery(existing) && existing.ClientAcknowledgedAt is null))
                .OrderBy(existing => existing.IsActive ? 0 : 1)
                .ThenByDescending(existing => existing.UpdatedAt)
                .ThenByDescending(existing => existing.Id)
                .FirstOrDefault();
            return Task.FromResult(session is null ? null : Clone(session));
        }
    }

    public Task<LinklyCloudBackendSessionRecord?> GetResumableSettlementSessionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var session = _sessions.Values
                .Where(existing =>
                    SameTerminal(existing, environment, storeCode, deviceCode) &&
                    string.Equals(existing.OperationType, "Settlement", StringComparison.OrdinalIgnoreCase) &&
                    // 回调超时收口的“结果未知”会话（Pending、IsActive=0、未 ack）也要能被找回并结案。
                    (existing.IsActive || existing.ClientAcknowledgedAt is null))
                .OrderBy(existing => existing.IsActive ? 0 : 1)
                .ThenByDescending(existing => existing.UpdatedAt)
                .ThenByDescending(existing => existing.Id)
                .FirstOrDefault();
            return Task.FromResult(session is null ? null : Clone(session));
        }
    }

    public Task<LinklyCloudBackendSessionRecord?> AcknowledgeSessionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        string sessionId,
        DateTimeOffset acknowledgedAt,
        bool supervisorResolved,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var key = SessionKey(environment, storeCode, deviceCode, sessionId);
            if (!_sessions.TryGetValue(key, out var session))
            {
                return Task.FromResult<LinklyCloudBackendSessionRecord?>(null);
            }

            var next = Clone(session);
            // 客户端确认后同步关闭 active 标记，人工确认 pending session 时也不能继续阻塞下一笔付款。
            next.ClientAcknowledgedAt = acknowledgedAt;
            if (supervisorResolved && !IsFinalForClientRecovery(next))
            {
                // 与 SQL 实现一致：只有主管结案才把非终态改为可审计的结案终态，已有终态保持原样。
                next.Status = StatusSupervisorResolved;
            }

            next.IsActive = false;
            next.UpdatedAt = acknowledgedAt;
            _sessions[key] = Clone(next);
            return Task.FromResult<LinklyCloudBackendSessionRecord?>(next);
        }
    }

    public Task AddNotificationAsync(
        LinklyCloudBackendNotificationRecord notification,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (string.Equals(notification.Type, "receipt", StringComparison.OrdinalIgnoreCase) ||
                !_notifications.Any(existing =>
                string.Equals(existing.Environment, notification.Environment, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.StoreCode, notification.StoreCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.DeviceCode, notification.DeviceCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.SessionId, notification.SessionId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.Type, notification.Type, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.PayloadJson, notification.PayloadJson, StringComparison.Ordinal)))
            {
                _notifications.Add(Clone(notification));
            }

            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<LinklyCloudBackendNotificationRecord>> GetNotificationsAsync(
        string environment,
        string storeCode,
        string deviceCode,
        string sessionId,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<LinklyCloudBackendNotificationRecord>>(
                _notifications
                    .Where(notification =>
                        string.Equals(notification.Environment, environment, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(notification.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(notification.DeviceCode, deviceCode, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(notification.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(notification => notification.ReceivedAt)
                    .Select(Clone)
                    .ToArray());
        }
    }

    private static bool SameTerminal(
        LinklyCloudBackendSessionRecord session,
        string environment,
        string storeCode,
        string deviceCode)
    {
        return string.Equals(session.Environment, environment, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(session.StoreCode, storeCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(session.DeviceCode, deviceCode, StringComparison.OrdinalIgnoreCase);
    }

    private static string SessionKey(string environment, string storeCode, string deviceCode, string sessionId)
    {
        return string.Join('|', environment, storeCode, deviceCode, sessionId);
    }

    private static bool IsCompleted(LinklyCloudBackendSessionRecord session)
    {
        return string.Equals(session.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFinalForClientRecovery(LinklyCloudBackendSessionRecord session)
    {
        return string.Equals(session.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(session.Status, StatusCancelled, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(session.Status, StatusFailed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(session.Status, StatusNotSubmitted, StringComparison.OrdinalIgnoreCase);
    }

    private static LinklyCloudBackendSessionRecord Clone(LinklyCloudBackendSessionRecord session)
    {
        return new LinklyCloudBackendSessionRecord
        {
            Id = session.Id,
            Environment = session.Environment,
            StoreCode = session.StoreCode,
            DeviceCode = session.DeviceCode,
            TerminalId = session.TerminalId,
            SessionId = session.SessionId,
            Status = session.Status,
            TxnRef = session.TxnRef,
            RequestTxnType = session.RequestTxnType,
            RequestAmountCents = session.RequestAmountCents,
            RequestRfn = session.RequestRfn,
            ResponseCode = session.ResponseCode,
            ResponseText = session.ResponseText,
            TransactionSuccess = session.TransactionSuccess,
            OperationType = session.OperationType,
            OperationSuccess = session.OperationSuccess,
            SettlementData = session.SettlementData,
            SettlementReceiptTexts = session.SettlementReceiptTexts,
            RecoveryAction = session.RecoveryAction,
            DisplayText = session.DisplayText,
            DisplayLines = session.DisplayLines,
            CancelKeyFlag = session.CancelKeyFlag,
            OKKeyFlag = session.OKKeyFlag,
            AcceptYesKeyFlag = session.AcceptYesKeyFlag,
            DeclineNoKeyFlag = session.DeclineNoKeyFlag,
            AuthoriseKeyFlag = session.AuthoriseKeyFlag,
            InputType = session.InputType,
            GraphicCode = session.GraphicCode,
            ReceiptText = session.ReceiptText,
            RecoveryCount = session.RecoveryCount,
            ReceiptPrintedAt = session.ReceiptPrintedAt,
            ClientAcknowledgedAt = session.ClientAcknowledgedAt,
            LastHttpStatus = session.LastHttpStatus,
            IsActive = session.IsActive,
            CreatedAt = session.CreatedAt,
            UpdatedAt = session.UpdatedAt
        };
    }

    private static LinklyCloudBackendNotificationRecord Clone(LinklyCloudBackendNotificationRecord notification)
    {
        return new LinklyCloudBackendNotificationRecord
        {
            Id = notification.Id,
            Environment = notification.Environment,
            StoreCode = notification.StoreCode,
            DeviceCode = notification.DeviceCode,
            SessionId = notification.SessionId,
            Type = notification.Type,
            PayloadJson = notification.PayloadJson,
            ReceivedAt = notification.ReceivedAt
        };
    }
}

public sealed class SqlSugarLinklyCloudBackendAsyncRepository(
    HbposSqlSugarContext dbContext) : ILinklyCloudBackendAsyncRepository
{
    internal const string TryCreateSessionSql = """
        SET XACT_ABORT ON;
        SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
        BEGIN TRANSACTION;
        DECLARE @CanCreate BIT = 1;
        DECLARE @Mode NVARCHAR(16) = N'Legacy';
        DECLARE @StoredTerminalId UNIQUEIDENTIFIER = NULL;
        DECLARE @StoredSelectionRevision BIGINT = NULL;

        -- 固定锁序：会话（设备、实体终端、TxnRef）-> 终端 -> 选择 -> 模式。
        IF EXISTS (
            SELECT TOP (1) 1
            FROM [dbo].[POSM_LinklyCloudBackendSession] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [DeviceCode] = @DeviceCode
              AND (
                  [IsActive] = 1
                  OR (
                      [Status] IN (N'Completed', N'Cancelled', N'Failed', N'NotSubmitted')
                      AND [ClientAcknowledgedAt] IS NULL
                  )
              ))
            SET @CanCreate = 0;

        IF @CanCreate = 1 AND @TerminalId IS NOT NULL AND EXISTS (
            SELECT TOP (1) 1
            FROM [dbo].[POSM_LinklyCloudBackendSession] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [TerminalId] = @TerminalId
              AND (
                  [IsActive] = 1
                  OR (
                      [Status] IN (N'Completed', N'Cancelled', N'Failed', N'NotSubmitted')
                      AND [ClientAcknowledgedAt] IS NULL
                  )
              ))
            SET @CanCreate = 0;

        IF @CanCreate = 1 AND @TxnRef IS NOT NULL AND EXISTS (
            SELECT TOP (1) 1
            FROM [dbo].[POSM_LinklyCloudBackendSession] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [TxnRef] = @TxnRef)
            SET @CanCreate = 0;

        IF @CanCreate = 1 AND @TerminalId IS NOT NULL
        BEGIN
            SELECT @StoredTerminalId = [TerminalId]
            FROM [dbo].[POSM_LinklyCloudTerminal] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [TerminalId] = @TerminalId
              AND [CredentialProtectionVersion] = 1
              AND [UpdatedAt] = @TerminalUpdatedAt
              AND [PairingState] = N'Ready'
              AND NULLIF(LTRIM(RTRIM([Secret])), N'') IS NOT NULL
              AND NULLIF(LTRIM(RTRIM([PosId])), N'') IS NOT NULL
              AND (
                  [PairingAttemptId] IS NULL
                  OR [PairingLeaseExpiresAt] IS NULL
                  OR [PairingLeaseExpiresAt] <= @UpdatedAt
              );

            IF @StoredTerminalId IS NULL
                SET @CanCreate = 0;
        END;

        IF @CanCreate = 1 AND @TerminalId IS NOT NULL
        BEGIN
            SELECT @StoredSelectionRevision = [Revision]
            FROM [dbo].[POSM_LinklyCloudDeviceSelection] WITH (UPDLOCK, HOLDLOCK)
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [DeviceCode] = @DeviceCode
              AND [TerminalId] = @TerminalId
              AND [Revision] = @SelectionRevision;

            IF @StoredSelectionRevision IS NULL
                SET @CanCreate = 0;
        END;

        SELECT @Mode = [Mode]
        FROM [dbo].[POSM_LinklyCloudConfigurationMode] WITH (UPDLOCK, HOLDLOCK)
        WHERE [Environment] = @Environment AND [StoreCode] = @StoreCode;

        IF @CanCreate = 1 AND (
            (@TerminalId IS NULL AND ISNULL(@Mode, N'Legacy') = N'Active')
            OR (@TerminalId IS NOT NULL AND ISNULL(@Mode, N'Legacy') <> N'Active'))
            SET @CanCreate = 0;

        IF @CanCreate = 1
        BEGIN
            INSERT INTO [dbo].[POSM_LinklyCloudBackendSession] (
                [Environment], [StoreCode], [DeviceCode], [TerminalId], [SessionId], [Status], [TxnRef], [RequestTxnType], [RequestAmountCents], [RequestRfn], [OperationType],
                [TransactionSuccess], [OperationSuccess], [SettlementData], [SettlementReceiptTexts], [ResponseCode], [ResponseText], [RecoveryAction], [DisplayText], [DisplayLines],
                [CancelKeyFlag], [OKKeyFlag], [AcceptYesKeyFlag], [DeclineNoKeyFlag], [AuthoriseKeyFlag],
                [InputType], [GraphicCode], [ReceiptText],
                [RecoveryCount], [ReceiptPrintedAt], [ClientAcknowledgedAt], [LastHttpStatus], [IsActive], [UpdatedAt])
            VALUES (
                @Environment, @StoreCode, @DeviceCode, @TerminalId, @SessionId, @Status, @TxnRef, @RequestTxnType, @RequestAmountCents, @RequestRfn, @OperationType,
                @TransactionSuccess, @OperationSuccess, @SettlementData, @SettlementReceiptTexts, @ResponseCode, @ResponseText, @RecoveryAction, @DisplayText, @DisplayLines,
                @CancelKeyFlag, @OKKeyFlag, @AcceptYesKeyFlag, @DeclineNoKeyFlag, @AuthoriseKeyFlag,
                @InputType, @GraphicCode, @ReceiptText,
                @RecoveryCount, @ReceiptPrintedAt, @ClientAcknowledgedAt, @LastHttpStatus, @IsActive, @UpdatedAt);
        END;

        COMMIT TRANSACTION;
        """;

    internal const string UpsertSessionSql = """
        MERGE [dbo].[POSM_LinklyCloudBackendSession] AS target
        USING (SELECT @Environment AS [Environment], @StoreCode AS [StoreCode], @DeviceCode AS [DeviceCode], @SessionId AS [SessionId]) AS source
        ON target.[Environment] = source.[Environment]
           AND target.[StoreCode] = source.[StoreCode]
           AND target.[DeviceCode] = source.[DeviceCode]
           AND target.[SessionId] = source.[SessionId]
        WHEN MATCHED
             AND NOT (
                target.[Status] IN ('Completed', 'Cancelled')
                AND (
                    @Status NOT IN ('Completed', 'Cancelled')
                    OR ISNULL(target.[OperationType], N'Transaction') <> ISNULL(@OperationType, N'Transaction')
                    OR ISNULL(target.[TxnRef], N'') <> ISNULL(@TxnRef, N'')
                    OR (
                        target.[TransactionSuccess] IS NOT NULL
                        AND ISNULL(CONVERT(INT, target.[TransactionSuccess]), -1) <> ISNULL(CONVERT(INT, @TransactionSuccess), -1)
                    )
                    OR (
                        target.[OperationSuccess] IS NOT NULL
                        AND ISNULL(CONVERT(INT, target.[OperationSuccess]), -1) <> ISNULL(CONVERT(INT, @OperationSuccess), -1)
                    )
                    OR (
                        target.[SettlementData] IS NOT NULL
                        AND ISNULL(target.[SettlementData], N'') <> ISNULL(@SettlementData, N'')
                    )
                    OR ISNULL(target.[ResponseCode], N'') <> ISNULL(@ResponseCode, N'')
                    OR ISNULL(target.[ResponseText], N'') <> ISNULL(@ResponseText, N'')
                )) THEN
            UPDATE SET
                [TerminalId] = COALESCE(target.[TerminalId], @TerminalId),
                [Status] = @Status,
                [TxnRef] = CASE
                    WHEN target.[RequestTxnType] IS NOT NULL
                     AND target.[RequestAmountCents] IS NOT NULL
                     AND target.[TxnRef] IS NOT NULL
                    THEN target.[TxnRef]
                    ELSE @TxnRef
                END,
                [RequestTxnType] = COALESCE(target.[RequestTxnType], @RequestTxnType),
                [RequestAmountCents] = COALESCE(target.[RequestAmountCents], @RequestAmountCents),
                [RequestRfn] = COALESCE(target.[RequestRfn], @RequestRfn),
                [OperationType] = @OperationType,
                [TransactionSuccess] = @TransactionSuccess,
                [OperationSuccess] = @OperationSuccess,
                [SettlementData] = @SettlementData,
                [SettlementReceiptTexts] = @SettlementReceiptTexts,
                [ResponseCode] = @ResponseCode,
                [ResponseText] = @ResponseText,
                [RecoveryAction] = @RecoveryAction,
                [DisplayText] = @DisplayText,
                [DisplayLines] = @DisplayLines,
                [CancelKeyFlag] = @CancelKeyFlag,
                [OKKeyFlag] = @OKKeyFlag,
                [AcceptYesKeyFlag] = @AcceptYesKeyFlag,
                [DeclineNoKeyFlag] = @DeclineNoKeyFlag,
                [AuthoriseKeyFlag] = @AuthoriseKeyFlag,
                [InputType] = @InputType,
                [GraphicCode] = @GraphicCode,
                [ReceiptText] = @ReceiptText,
                [RecoveryCount] = @RecoveryCount,
                [ReceiptPrintedAt] = @ReceiptPrintedAt,
                [ClientAcknowledgedAt] = COALESCE(@ClientAcknowledgedAt, target.[ClientAcknowledgedAt]),
                [LastHttpStatus] = @LastHttpStatus,
                [IsActive] = @IsActive,
                [UpdatedAt] = @UpdatedAt
        WHEN NOT MATCHED THEN
            INSERT (
                [Environment], [StoreCode], [DeviceCode], [TerminalId], [SessionId], [Status], [TxnRef], [RequestTxnType], [RequestAmountCents], [RequestRfn], [OperationType],
                [TransactionSuccess], [OperationSuccess], [SettlementData], [SettlementReceiptTexts], [ResponseCode], [ResponseText], [RecoveryAction], [DisplayText], [DisplayLines],
                [CancelKeyFlag], [OKKeyFlag], [AcceptYesKeyFlag], [DeclineNoKeyFlag], [AuthoriseKeyFlag],
                [InputType], [GraphicCode], [ReceiptText],
                [RecoveryCount], [ReceiptPrintedAt], [ClientAcknowledgedAt], [LastHttpStatus], [IsActive], [UpdatedAt])
            VALUES (
                @Environment, @StoreCode, @DeviceCode, @TerminalId, @SessionId, @Status, @TxnRef, @RequestTxnType, @RequestAmountCents, @RequestRfn, @OperationType,
                @TransactionSuccess, @OperationSuccess, @SettlementData, @SettlementReceiptTexts, @ResponseCode, @ResponseText, @RecoveryAction, @DisplayText, @DisplayLines,
                @CancelKeyFlag, @OKKeyFlag, @AcceptYesKeyFlag, @DeclineNoKeyFlag, @AuthoriseKeyFlag,
                @InputType, @GraphicCode, @ReceiptText,
                @RecoveryCount, @ReceiptPrintedAt, @ClientAcknowledgedAt, @LastHttpStatus, @IsActive, @UpdatedAt);
        """;

    public async Task<bool> TryCreateSessionAsync(
        LinklyCloudBackendSessionRecord session,
        CancellationToken cancellationToken)
    {
        try
        {
            var affected = await dbContext.PosmDb.Ado.ExecuteCommandAsync(
                TryCreateSessionSql,
                ToSessionParameters(session));
            return affected > 0;
        }
        catch (Exception ex) when (IsUniqueConstraintViolation(ex))
        {
            // 唯一键冲突可能来自 TxnRef 或在途会话；由服务层重读权威状态后决定是否重试。
            return false;
        }
    }

    private static bool IsUniqueConstraintViolation(Exception ex)
    {
        var text = ex.ToString();
        return text.Contains("2601", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("2627", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("UX_POSM_LinklyCloudBackendSession", StringComparison.OrdinalIgnoreCase);
    }

    public Task UpsertSessionAsync(
        LinklyCloudBackendSessionRecord session,
        CancellationToken cancellationToken)
    {
        return dbContext.PosmDb.Ado.ExecuteCommandAsync(UpsertSessionSql, ToSessionParameters(session));
    }

    public async Task<LinklyCloudBackendSessionRecord?> GetSessionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        string sessionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                [Id], [Environment], [StoreCode], [DeviceCode], [TerminalId], [SessionId], [Status], [TxnRef], [RequestTxnType], [RequestAmountCents], [RequestRfn],
                [TransactionSuccess], [OperationType], [OperationSuccess], [SettlementData], [SettlementReceiptTexts], [ResponseCode], [ResponseText], [RecoveryAction], [DisplayText], [DisplayLines],
                [CancelKeyFlag], [OKKeyFlag], [AcceptYesKeyFlag], [DeclineNoKeyFlag], [AuthoriseKeyFlag],
                [InputType], [GraphicCode], [ReceiptText],
                [RecoveryCount], [ReceiptPrintedAt], [ClientAcknowledgedAt], [LastHttpStatus], [IsActive], [CreatedAt], [UpdatedAt]
            FROM [dbo].[POSM_LinklyCloudBackendSession]
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [DeviceCode] = @DeviceCode
              AND [SessionId] = @SessionId;
            """;

        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<LinklyCloudBackendSessionRecord>(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@DeviceCode", deviceCode),
            new SugarParameter("@SessionId", sessionId));
    }

    public async Task<LinklyCloudBackendSessionRecord?> GetSessionByEnvironmentSessionIdAsync(
        string environment,
        string sessionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                [Id], [Environment], [StoreCode], [DeviceCode], [TerminalId], [SessionId], [Status], [TxnRef], [RequestTxnType], [RequestAmountCents], [RequestRfn],
                [TransactionSuccess], [OperationType], [OperationSuccess], [SettlementData], [SettlementReceiptTexts], [ResponseCode], [ResponseText], [RecoveryAction], [DisplayText], [DisplayLines],
                [CancelKeyFlag], [OKKeyFlag], [AcceptYesKeyFlag], [DeclineNoKeyFlag], [AuthoriseKeyFlag],
                [InputType], [GraphicCode], [ReceiptText],
                [RecoveryCount], [ReceiptPrintedAt], [ClientAcknowledgedAt], [LastHttpStatus], [IsActive], [UpdatedAt]
            FROM [dbo].[POSM_LinklyCloudBackendSession]
            WHERE [Environment] = @Environment
              AND [SessionId] = @SessionId
            ORDER BY [UpdatedAt] DESC, [Id] DESC;
            """;

        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<LinklyCloudBackendSessionRecord>(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@SessionId", sessionId));
    }

    public async Task<LinklyCloudBackendSessionRecord?> GetActiveSessionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                [Id], [Environment], [StoreCode], [DeviceCode], [TerminalId], [SessionId], [Status], [TxnRef], [RequestTxnType], [RequestAmountCents], [RequestRfn],
                [TransactionSuccess], [OperationType], [OperationSuccess], [SettlementData], [SettlementReceiptTexts], [ResponseCode], [ResponseText], [RecoveryAction], [DisplayText], [DisplayLines],
                [CancelKeyFlag], [OKKeyFlag], [AcceptYesKeyFlag], [DeclineNoKeyFlag], [AuthoriseKeyFlag],
                [InputType], [GraphicCode], [ReceiptText],
                [RecoveryCount], [ReceiptPrintedAt], [ClientAcknowledgedAt], [LastHttpStatus], [IsActive], [CreatedAt], [UpdatedAt]
            FROM [dbo].[POSM_LinklyCloudBackendSession]
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [DeviceCode] = @DeviceCode
              AND [IsActive] = 1
            ORDER BY
                [UpdatedAt] DESC,
                [Id] DESC;
            """;

        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<LinklyCloudBackendSessionRecord>(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@DeviceCode", deviceCode));
    }

    public async Task<LinklyCloudBackendSessionRecord?> GetActiveSessionByTerminalAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                [Id], [Environment], [StoreCode], [DeviceCode], [TerminalId], [SessionId], [Status], [TxnRef], [RequestTxnType], [RequestAmountCents], [RequestRfn],
                [TransactionSuccess], [OperationType], [OperationSuccess], [SettlementData], [SettlementReceiptTexts], [ResponseCode], [ResponseText], [RecoveryAction], [DisplayText], [DisplayLines],
                [CancelKeyFlag], [OKKeyFlag], [AcceptYesKeyFlag], [DeclineNoKeyFlag], [AuthoriseKeyFlag],
                [InputType], [GraphicCode], [ReceiptText],
                [RecoveryCount], [ReceiptPrintedAt], [ClientAcknowledgedAt], [LastHttpStatus], [IsActive], [UpdatedAt]
            FROM [dbo].[POSM_LinklyCloudBackendSession]
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [TerminalId] = @TerminalId
              AND [IsActive] = 1
            ORDER BY [UpdatedAt] DESC, [Id] DESC;
            """;

        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<LinklyCloudBackendSessionRecord>(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@TerminalId", terminalId));
    }

    public async Task<LinklyCloudBackendSessionRecord?> GetBlockingSessionByTerminalAsync(
        string environment,
        string storeCode,
        Guid terminalId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                [Id], [Environment], [StoreCode], [DeviceCode], [TerminalId], [SessionId], [Status], [TxnRef], [RequestTxnType], [RequestAmountCents], [RequestRfn],
                [TransactionSuccess], [OperationType], [OperationSuccess], [SettlementData], [SettlementReceiptTexts], [ResponseCode], [ResponseText], [RecoveryAction], [DisplayText], [DisplayLines],
                [CancelKeyFlag], [OKKeyFlag], [AcceptYesKeyFlag], [DeclineNoKeyFlag], [AuthoriseKeyFlag],
                [InputType], [GraphicCode], [ReceiptText],
                [RecoveryCount], [ReceiptPrintedAt], [ClientAcknowledgedAt], [LastHttpStatus], [IsActive], [UpdatedAt]
            FROM [dbo].[POSM_LinklyCloudBackendSession]
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [TerminalId] = @TerminalId
              AND (
                    [IsActive] = 1
                    OR ([Status] IN (N'Completed', N'Cancelled', N'Failed', N'NotSubmitted') AND [ClientAcknowledgedAt] IS NULL)
                  )
            ORDER BY
                CASE WHEN [IsActive] = 1 THEN 0 ELSE 1 END,
                [UpdatedAt] DESC,
                [Id] DESC;
            """;

        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<LinklyCloudBackendSessionRecord>(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@TerminalId", terminalId));
    }

    public async Task<LinklyCloudBackendSessionRecord?> GetResumableSessionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                [Id], [Environment], [StoreCode], [DeviceCode], [TerminalId], [SessionId], [Status], [TxnRef], [RequestTxnType], [RequestAmountCents], [RequestRfn],
                [TransactionSuccess], [OperationType], [OperationSuccess], [SettlementData], [SettlementReceiptTexts], [ResponseCode], [ResponseText], [RecoveryAction], [DisplayText], [DisplayLines],
                [CancelKeyFlag], [OKKeyFlag], [AcceptYesKeyFlag], [DeclineNoKeyFlag], [AuthoriseKeyFlag],
                [InputType], [GraphicCode], [ReceiptText],
                [RecoveryCount], [ReceiptPrintedAt], [ClientAcknowledgedAt], [LastHttpStatus], [IsActive], [UpdatedAt]
            FROM [dbo].[POSM_LinklyCloudBackendSession]
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [DeviceCode] = @DeviceCode
              AND ISNULL([OperationType], N'Transaction') = N'Transaction'
              AND (
                    [IsActive] = 1
                    OR ([Status] IN (N'Completed', N'Cancelled', N'Failed', N'NotSubmitted') AND [ClientAcknowledgedAt] IS NULL)
                  )
            ORDER BY
                CASE WHEN [IsActive] = 1 THEN 0 ELSE 1 END,
                [UpdatedAt] DESC,
                [Id] DESC;
            """;

        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<LinklyCloudBackendSessionRecord>(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@DeviceCode", deviceCode));
    }

    public async Task<LinklyCloudBackendSessionRecord?> GetResumableSettlementSessionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                [Id], [Environment], [StoreCode], [DeviceCode], [TerminalId], [SessionId], [Status], [TxnRef], [RequestTxnType], [RequestAmountCents], [RequestRfn],
                [TransactionSuccess], [OperationType], [OperationSuccess], [SettlementData], [SettlementReceiptTexts], [ResponseCode], [ResponseText], [RecoveryAction], [DisplayText], [DisplayLines],
                [CancelKeyFlag], [OKKeyFlag], [AcceptYesKeyFlag], [DeclineNoKeyFlag], [AuthoriseKeyFlag],
                [InputType], [GraphicCode], [ReceiptText],
                [RecoveryCount], [ReceiptPrintedAt], [ClientAcknowledgedAt], [LastHttpStatus], [IsActive], [CreatedAt], [UpdatedAt]
            FROM [dbo].[POSM_LinklyCloudBackendSession]
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [DeviceCode] = @DeviceCode
              AND [OperationType] = N'Settlement'
              AND (
                    [IsActive] = 1
                    -- 未 ack 的结算一律可恢复：含终态，以及回调超时收口为“结果未知”的 Pending 会话。
                    OR [ClientAcknowledgedAt] IS NULL
                  )
            ORDER BY
                CASE WHEN [IsActive] = 1 THEN 0 ELSE 1 END,
                [UpdatedAt] DESC,
                [Id] DESC;
            """;

        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<LinklyCloudBackendSessionRecord>(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@DeviceCode", deviceCode));
    }

    public async Task<LinklyCloudBackendSessionRecord?> AcknowledgeSessionAsync(
        string environment,
        string storeCode,
        string deviceCode,
        string sessionId,
        DateTimeOffset acknowledgedAt,
        bool supervisorResolved,
        CancellationToken cancellationToken)
    {
        // 普通 ack 只释放付款闸门、不改 Status：非终态会话仍被终端管理闸门视为结果未知。
        // 主管结案才把非终态写成 SupervisorResolved，已有的 Linkly 终态一律保留。
        const string sql = """
            UPDATE [dbo].[POSM_LinklyCloudBackendSession]
            SET [ClientAcknowledgedAt] = @ClientAcknowledgedAt,
                [Status] = CASE
                    WHEN @SupervisorResolved = 1
                     AND ([Status] IS NULL OR [Status] NOT IN (N'Completed', N'Cancelled', N'Failed', N'NotSubmitted'))
                    THEN N'SupervisorResolved'
                    ELSE [Status]
                END,
                [IsActive] = 0,
                [UpdatedAt] = @ClientAcknowledgedAt
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [DeviceCode] = @DeviceCode
              AND [SessionId] = @SessionId;
            """;

        var acknowledgedAtUtc = acknowledgedAt.UtcDateTime;
        var affected = await dbContext.PosmDb.Ado.ExecuteCommandAsync(
            sql,
            new SugarParameter("@ClientAcknowledgedAt", acknowledgedAtUtc),
            new SugarParameter("@SupervisorResolved", supervisorResolved),
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@DeviceCode", deviceCode),
            new SugarParameter("@SessionId", sessionId));
        return affected <= 0
            ? null
            : await GetSessionAsync(environment, storeCode, deviceCode, sessionId, cancellationToken);
    }

    public Task AddNotificationAsync(
        LinklyCloudBackendNotificationRecord notification,
        CancellationToken cancellationToken)
    {
        const string sql = """
            IF LOWER(@Type) = N'receipt' OR NOT EXISTS (
                SELECT 1
                FROM [dbo].[POSM_LinklyCloudBackendNotification] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Environment] = @Environment
                  AND [StoreCode] = @StoreCode
                  AND [DeviceCode] = @DeviceCode
                  AND [SessionId] = @SessionId
                  AND [Type] = @Type
                  AND [PayloadJson] = @PayloadJson)
            BEGIN
                INSERT INTO [dbo].[POSM_LinklyCloudBackendNotification] (
                    [Environment], [StoreCode], [DeviceCode], [SessionId], [Type], [PayloadJson], [ReceivedAt])
                VALUES (
                    @Environment, @StoreCode, @DeviceCode, @SessionId, @Type, @PayloadJson, @ReceivedAt);
            END;
            """;

        return dbContext.PosmDb.Ado.ExecuteCommandAsync(
            sql,
            new SugarParameter("@Environment", notification.Environment),
            new SugarParameter("@StoreCode", notification.StoreCode),
            new SugarParameter("@DeviceCode", notification.DeviceCode),
            new SugarParameter("@SessionId", notification.SessionId),
            new SugarParameter("@Type", notification.Type),
            new SugarParameter("@PayloadJson", notification.PayloadJson),
            new SugarParameter("@ReceivedAt", notification.ReceivedAt.UtcDateTime));
    }

    public async Task<IReadOnlyList<LinklyCloudBackendNotificationRecord>> GetNotificationsAsync(
        string environment,
        string storeCode,
        string deviceCode,
        string sessionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                [Id], [Environment], [StoreCode], [DeviceCode], [SessionId], [Type], [PayloadJson], [ReceivedAt]
            FROM [dbo].[POSM_LinklyCloudBackendNotification]
            WHERE [Environment] = @Environment
              AND [StoreCode] = @StoreCode
              AND [DeviceCode] = @DeviceCode
              AND [SessionId] = @SessionId
            ORDER BY [ReceivedAt] ASC, [Id] ASC;
            """;

        return await dbContext.PosmDb.Ado.SqlQueryAsync<LinklyCloudBackendNotificationRecord>(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@DeviceCode", deviceCode),
            new SugarParameter("@SessionId", sessionId));
    }

    private static SugarParameter[] ToSessionParameters(LinklyCloudBackendSessionRecord session)
    {
        return
        [
            new SugarParameter("@Environment", session.Environment),
            new SugarParameter("@StoreCode", session.StoreCode),
            new SugarParameter("@DeviceCode", session.DeviceCode),
            new SugarParameter("@TerminalId", session.TerminalId),
            new SugarParameter("@SelectionRevision", session.SelectionRevision),
            // Active 模式用该时间做 DATETIME2(7) 终端 CAS；显式类型避免 SqlSugar 将高精度值降为 datetime。
            new SugarParameter("@TerminalUpdatedAt", session.TerminalUpdatedAt)
            {
                DbType = System.Data.DbType.DateTime2
            },
            new SugarParameter("@SessionId", session.SessionId),
            new SugarParameter("@Status", session.Status),
            new SugarParameter("@TxnRef", session.TxnRef),
            new SugarParameter("@RequestTxnType", session.RequestTxnType),
            new SugarParameter("@RequestAmountCents", session.RequestAmountCents),
            new SugarParameter("@RequestRfn", session.RequestRfn),
            new SugarParameter("@OperationType", session.OperationType),
            new SugarParameter("@TransactionSuccess", session.TransactionSuccess),
            new SugarParameter("@OperationSuccess", session.OperationSuccess),
            new SugarParameter("@SettlementData", session.SettlementData),
            new SugarParameter("@SettlementReceiptTexts", session.SettlementReceiptTexts),
            new SugarParameter("@ResponseCode", session.ResponseCode),
            new SugarParameter("@ResponseText", session.ResponseText),
            new SugarParameter("@RecoveryAction", session.RecoveryAction),
            new SugarParameter("@DisplayText", session.DisplayText),
            new SugarParameter("@DisplayLines", session.DisplayLines),
            new SugarParameter("@CancelKeyFlag", session.CancelKeyFlag),
            new SugarParameter("@OKKeyFlag", session.OKKeyFlag),
            new SugarParameter("@AcceptYesKeyFlag", session.AcceptYesKeyFlag),
            new SugarParameter("@DeclineNoKeyFlag", session.DeclineNoKeyFlag),
            new SugarParameter("@AuthoriseKeyFlag", session.AuthoriseKeyFlag),
            new SugarParameter("@InputType", session.InputType),
            new SugarParameter("@GraphicCode", session.GraphicCode),
            new SugarParameter("@ReceiptText", session.ReceiptText),
            new SugarParameter("@RecoveryCount", session.RecoveryCount),
            new SugarParameter("@ReceiptPrintedAt", session.ReceiptPrintedAt?.UtcDateTime),
            new SugarParameter("@ClientAcknowledgedAt", session.ClientAcknowledgedAt?.UtcDateTime),
            new SugarParameter("@LastHttpStatus", session.LastHttpStatus),
            new SugarParameter("@IsActive", session.IsActive),
            new SugarParameter("@UpdatedAt", session.UpdatedAt.UtcDateTime)
        ];
    }

    private static string CreateTxnRef()
    {
        Span<byte> bytes = stackalloc byte[2];
        Random.Shared.NextBytes(bytes);
        return $"{DateTimeOffset.UtcNow:yyMMddHHmmss}{Convert.ToHexString(bytes)}";
    }
}

public sealed class LinklyCloudBackendSessionRecord
{
    public long Id { get; set; }

    public string Environment { get; set; } = string.Empty;

    public string StoreCode { get; set; } = string.Empty;

    public string DeviceCode { get; set; } = string.Empty;

    public Guid? TerminalId { get; set; }

    [SugarColumn(IsIgnore = true)]
    public long? SelectionRevision { get; set; }

    [SugarColumn(IsIgnore = true)]
    public DateTime? TerminalUpdatedAt { get; set; }

    public string SessionId { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? TxnRef { get; set; }

    [JsonIgnore]
    public string? RequestTxnType { get; set; }

    [JsonIgnore]
    public long? RequestAmountCents { get; set; }

    [JsonIgnore]
    public string? RequestRfn { get; set; }

    public bool? TransactionSuccess { get; set; }

    public string OperationType { get; set; } = "Transaction";

    public bool? OperationSuccess { get; set; }

    public string? SettlementData { get; set; }

    public string? SettlementReceiptTexts { get; set; }

    public string? ResponseCode { get; set; }

    public string? ResponseText { get; set; }

    public string? RecoveryAction { get; set; }

    public string? DisplayText { get; set; }

    public string? DisplayLines { get; set; }

    public bool CancelKeyFlag { get; set; }

    public bool OKKeyFlag { get; set; }

    public bool AcceptYesKeyFlag { get; set; }

    public bool DeclineNoKeyFlag { get; set; }

    public bool AuthoriseKeyFlag { get; set; }

    public string? InputType { get; set; }

    public string? GraphicCode { get; set; }

    public string? ReceiptText { get; set; }

    public int RecoveryCount { get; set; }

    public DateTimeOffset? ReceiptPrintedAt { get; set; }

    public DateTimeOffset? ClientAcknowledgedAt { get; set; }

    public int? LastHttpStatus { get; set; }

    public bool IsActive { get; set; }

    // 会话创建时间；SQL 由列默认值写入，升级前的旧行以 UpdatedAt 回填。仅按需 SELECT，未读取时为空。
    public DateTimeOffset? CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class LinklyCloudBackendNotificationRecord
{
    public long Id { get; set; }

    public string Environment { get; set; } = string.Empty;

    public string StoreCode { get; set; } = string.Empty;

    public string DeviceCode { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; set; }
}
