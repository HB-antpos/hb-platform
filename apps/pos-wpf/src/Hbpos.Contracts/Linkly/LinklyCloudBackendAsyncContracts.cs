using System.Text.Json.Serialization;

namespace Hbpos.Contracts.Linkly;

public static class LinklyBackendPaymentReference
{
    public const string Prefix = "ANZBACKEND";

    private const string SessionPart = "session=";
    private const string EnvironmentPart = "environment=";

    public static string Format(
        string txnRef,
        string sessionId,
        string environment,
        string? refundReference)
    {
        var reference = $"{Prefix}:{Encode(txnRef)}";
        if (!string.IsNullOrWhiteSpace(refundReference))
        {
            reference += $":{Encode(refundReference.Trim())}";
        }

        return $"{reference}:{SessionPart}{Encode(sessionId)}:{EnvironmentPart}{Encode(environment)}";
    }

    public static bool TryGetPrintMarker(
        string? reference,
        out string environment,
        out string sessionId)
    {
        environment = string.Empty;
        sessionId = string.Empty;
        if (!TryReadParts(reference, out var parts))
        {
            return false;
        }

        foreach (var part in parts.Skip(2))
        {
            if (part.StartsWith(SessionPart, StringComparison.OrdinalIgnoreCase))
            {
                sessionId = Decode(part[SessionPart.Length..]);
            }
            else if (part.StartsWith(EnvironmentPart, StringComparison.OrdinalIgnoreCase))
            {
                environment = Decode(part[EnvironmentPart.Length..]);
            }
        }

        return !string.IsNullOrWhiteSpace(environment) &&
            !string.IsNullOrWhiteSpace(sessionId);
    }

    public static string? TryGetRefundReference(string? reference)
    {
        if (!TryReadParts(reference, out var parts) || parts.Length < 3)
        {
            return null;
        }

        return parts
            .Skip(2)
            .Where(part =>
                !part.StartsWith(SessionPart, StringComparison.OrdinalIgnoreCase) &&
                !part.StartsWith(EnvironmentPart, StringComparison.OrdinalIgnoreCase))
            .Select(Decode)
            .FirstOrDefault(part => !string.IsNullOrWhiteSpace(part));
    }

    public static string? GetDisplayReference(string? reference)
    {
        if (!TryReadParts(reference, out var parts))
        {
            return reference;
        }

        var display = $"{Prefix}:{Decode(parts[1])}";
        var refundReference = TryGetRefundReference(reference);
        return string.IsNullOrWhiteSpace(refundReference)
            ? display
            : $"{display}:{refundReference}";
    }

    private static bool TryReadParts(
        string? reference,
        out string[] parts)
    {
        parts = [];
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        parts = reference.Trim().Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 &&
            string.Equals(parts[0], Prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string Encode(string value)
    {
        return Uri.EscapeDataString(value.Trim());
    }

    private static string Decode(string value)
    {
        return Uri.UnescapeDataString(value);
    }
}

/// <param name="AttemptGuid">
/// 可选。POS 本地 attempt 的身份。传入后 API 用 <see cref="LinklyAttemptTxnRef"/> 由它派生交易引用，
/// 不再随机生成；POS 用同一算法在发请求前就把该引用落到本地 attempt，请求发出后即使断电或响应丢失，
/// 本地记录仍能按 TxnRef 找回这笔会话。交易引用始终由 API 生成，客户端不能直接指定（见
/// CloudBackendAsyncRequests_DoNotExposeClientCredentialsOrDeviceScopeFields）。不传时行为与旧客户端一致。
/// </param>
public sealed record LinklyCloudBackendTransactionRequest(
    string Environment,
    string TxnType,
    long AmtPurchase,
    IReadOnlyDictionary<string, string>? PurchaseAnalysisData,
    Guid? TerminalId = null,
    long? SelectionRevision = null,
    Guid? AttemptGuid = null);

public sealed record LinklyCloudBackendSettlementRequest(
    string Environment,
    Guid? TerminalId = null,
    long? SelectionRevision = null);

public sealed record LinklyCloudBackendRecoverRequest(
    string Environment);

public sealed record LinklyCloudBackendSendKeyRequest(
    string Environment,
    string Key,
    string? Data);

public sealed record LinklyCloudBackendMarkReceiptPrintedRequest(
    string Environment);

/// <param name="SupervisorResolved">
/// 主管已在 POS 上对该会话作出结案决定。服务端仅在会话仍非终态时据此写入 SupervisorResolved，
/// 使终端管理闸门可以放行；普通 ack 不带此标记，非终态会话仍按未知处理。
/// </param>
public sealed record LinklyCloudBackendAcknowledgeRequest(
    string? Environment,
    bool SupervisorResolved = false);

public sealed record LinklyCloudBackendTerminalCredentialUpsertRequest(
    string Environment,
    string Secret,
    string PosId);

public sealed record LinklyCloudBackendSessionResponse(
    string Environment,
    string StoreCode,
    string DeviceCode,
    string SessionId,
    string Status,
    string? TxnRef,
    string? ResponseCode,
    string? ResponseText,
    string? RecoveryAction,
    string? DisplayText,
    bool CancelKeyFlag,
    bool OKKeyFlag,
    bool AcceptYesKeyFlag,
    bool DeclineNoKeyFlag,
    bool AuthoriseKeyFlag,
    string? InputType,
    string? GraphicCode,
    IReadOnlyList<string>? DisplayLines,
    string? ReceiptText,
    int RecoveryCount,
    DateTimeOffset? ReceiptPrintedAt,
    DateTimeOffset? ClientAcknowledgedAt,
    int? LastHttpStatus,
    IReadOnlyList<LinklyCloudBackendNotificationDto> Notifications,
    bool? TransactionSuccess = null,
    string OperationType = "Transaction",
    bool? OperationSuccess = null,
    string? SettlementData = null,
    IReadOnlyList<string>? SettlementReceiptTexts = null,
    Guid? TerminalId = null,
    string? TerminalDisplayName = null,
    // 会话创建时间（UTC）。客户端据此判断未决结算属于哪个营业日，避免次日结算接管前一天的会话；
    // 旧数据由 schema 升级时以 UpdatedAt 回填，仍为空时按“无法确认营业日”处理。
    DateTimeOffset? CreatedAt = null)
{
    public LinklyCloudBackendCardTransactionDto? CardTransaction { get; init; }
}

public sealed record LinklyCloudBackendCardTransactionDto(
    string? TxnRef,
    string? Rfn,
    string? AuthCode,
    string? CardType,
    string? MaskedCardNumber,
    string? MerchantId,
    string? ResponseCode,
    string? ResponseText,
    string? Stan,
    DateTimeOffset? BankDateTime,
    // Linkly 金额使用整数分币并始终返回绝对值；购买/退款方向由调用方已有支付操作决定。
    long? AmountCents);

public sealed record LinklyCloudBackendNotificationDto(
    string Type,
    string PayloadJson,
    DateTimeOffset ReceivedAt);

public sealed record LinklyCloudBackendHealthResponse(
    string Environment,
    string StoreCode,
    string DeviceCode,
    bool IsReady,
    string? PublicNotificationBaseUrl,
    IReadOnlyList<LinklyCloudBackendHealthCheckDto> Checks);

public sealed record LinklyCloudBackendHealthCheckDto(
    string Code,
    bool IsReady,
    string Message);

public sealed record LinklyCloudBackendLogonTestResponse(
    string Environment,
    string StoreCode,
    string DeviceCode,
    string TransactionReference,
    DateTimeOffset RequestedAt,
    int HttpStatus,
    bool Succeeded,
    string? ResponseCode,
    string? ResponseText,
    string? Catid,
    string? Caid,
    string? PinPadVersion,
    string Message);

public sealed record LinklyCloudBackendStatusTestResponse(
    string Environment,
    string StoreCode,
    string DeviceCode,
    string TransactionReference,
    DateTimeOffset RequestedAt,
    int HttpStatus,
    bool Succeeded,
    string? ResponseCode,
    string? ResponseText,
    string? ResponseTxnRef,
    string? ResponseDate,
    string? ResponseTime,
    string Message)
{
    [JsonPropertyName("txnRef")]
    public string? TxnRef => ResponseTxnRef;

    // 官方 Status 的实时签到状态；缺失或无有效回包时保持未知。
    [JsonPropertyName("loggedOn")]
    public bool? LoggedOn { get; init; }
}

public sealed record LinklyCloudBackendTerminalCredentialResponse(
    string Environment,
    string StoreCode,
    string DeviceCode,
    bool HasSecret,
    string PosId,
    DateTimeOffset UpdatedAt);
