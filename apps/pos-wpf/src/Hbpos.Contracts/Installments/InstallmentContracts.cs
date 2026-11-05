using System.Text.Json.Serialization;
using Hbpos.Contracts.Orders;

namespace Hbpos.Contracts.Installments;

public enum InstallmentStatus
{
    Active = 1,
    PaidOff = 2,
    PickedUp = 3,
    Cancelled = 4
}

/// <summary>
/// 分期生命周期的共享判定，API 各写入闸门与客户端按钮必须用同一口径。
/// </summary>
public static class InstallmentLifecycleRules
{
    /// <summary>
    /// 可"取消并退款"：未付清的进行中单，或已付清但尚未提货的单。
    /// 已提货、已取消不可走取消退款；作废（不退款）仍只允许进行中单，不受此规则影响。
    /// </summary>
    public static bool CanCancelWithRefund(InstallmentStatus status, decimal balanceAmount) =>
        (status == InstallmentStatus.Active && balanceAmount > 0m) ||
        (status == InstallmentStatus.PaidOff && balanceAmount == 0m);

    public static bool CanCancelWithRefund(int status, decimal balanceAmount) =>
        CanCancelWithRefund((InstallmentStatus)status, balanceAmount);

    /// <summary>未传退款方式的旧请求/旧记录一律按原路退处理；未定义的枚举值拒绝。</summary>
    public static InstallmentCancelRefundMode NormalizeRefundMode(InstallmentCancelRefundMode? mode) =>
        mode switch
        {
            null => InstallmentCancelRefundMode.OriginalRoute,
            InstallmentCancelRefundMode.OriginalRoute or InstallmentCancelRefundMode.Voucher => mode.Value,
            _ => throw new InvalidOperationException("Installment cancellation refund mode is invalid.")
        };

    /// <summary>
    /// 退款合计是否覆盖全部原付款：原路退按方式逐项相等；退代金券则只允许代金券且合计等于原付款总额。
    /// API 旧取消接口、claim 原子提交、提交快照校验三处共用，避免口径漂移。
    /// </summary>
    public static bool RefundTotalsMatch(
        IReadOnlyDictionary<PaymentMethodKind, decimal> paidByMethod,
        IReadOnlyDictionary<PaymentMethodKind, decimal> refundByMethod,
        InstallmentCancelRefundMode mode)
    {
        if (mode == InstallmentCancelRefundMode.Voucher)
        {
            return refundByMethod.Count == 1 &&
                refundByMethod.TryGetValue(PaymentMethodKind.Voucher, out var voucherRefund) &&
                voucherRefund == decimal.Round(paidByMethod.Values.Sum(), 2, MidpointRounding.AwayFromZero);
        }

        return paidByMethod.Count == refundByMethod.Count &&
            paidByMethod.All(pair => refundByMethod.TryGetValue(pair.Key, out var refundAmount) && refundAmount == pair.Value);
    }

    /// <summary>某笔原付款在给定退款方式下应以什么方式退回。</summary>
    public static PaymentMethodKind ResolveRefundMethod(
        PaymentMethodKind originalMethod,
        InstallmentCancelRefundMode mode) =>
        mode == InstallmentCancelRefundMode.Voucher ? PaymentMethodKind.Voucher : originalMethod;
}

/// <summary>取消分期的退款方式：原路退回，或全部改发退款代金券（含刷卡原付款）。</summary>
public enum InstallmentCancelRefundMode
{
    OriginalRoute = 1,
    Voucher = 2
}

public enum InstallmentPaymentStatus
{
    Recorded = 1,
    Voided = 2
}

public enum InstallmentCancellationKind
{
    RefundCancel = 1,
    VoidCancel = 2
}

public enum InstallmentRepaymentClaimStatus
{
    Prepared = 1,
    ProviderPending = 2,
    Committed = 3,
    Released = 4,
    Declined = 5,
    Unknown = 6
}

public enum InstallmentRepaymentClaimResolveOutcome
{
    Released = 1,
    Declined = 2,
    Unknown = 3
}

public enum InstallmentCancelClaimStatus
{
    Prepared = 1,
    RefundPending = 2,
    Committed = 3,
    Released = 4,
    Declined = 5,
    Unknown = 6
}

public enum InstallmentCancelClaimResolveOutcome
{
    Released = 1,
    Declined = 2,
    Unknown = 3
}

public sealed record InstallmentRepaymentCapabilitiesResponse(
    bool RepaymentClaimsSupported,
    bool RepaymentClaimsRequired,
    bool CrossDeviceRepaymentEnabled,
    int PreparedClaimTtlSeconds,
    bool CancelClaimsSupported = true,
    bool CancelClaimsRequired = false,
    int CancelPreparedClaimTtlSeconds = 120,
    bool CrossDeviceCancelRefundEnabled = false,
    bool CrossDeviceVoidEnabled = false,
    bool CrossDevicePickupEnabled = false,
    bool CardRepaymentSupported = false,
    [property: JsonPropertyName("repaymentClaimPrepareProviderV1")]
    bool RepaymentClaimPrepareProviderV1 = false,
    bool AmendLinesSupported = false,
    // 允许刷卡续付的通道族（square / linkly / zeller / manual）。旧服务端不返回（null）时，客户端只能看 CardRepaymentSupported。
    IReadOnlyList<string>? CardRepaymentProviders = null,
    // 服务端支持主管结案卡状态不明的刷卡续付（supervisor-resolve 与 blocking 查询）。
    bool CardRepaymentSupervisorResolveSupported = false);

/// <summary>
/// 分期续付刷卡通道族的统一口径：claim 上登记的 provider、卡证据的 Processor、能力位与服务端开关都按它归一。
/// 只比较字母和数字、忽略大小写，按前缀识别：Linkly 卡证据的 Processor 是 "ANZ"，移动端登记的是 "linkly-cloud"。
/// </summary>
public static class InstallmentCardRepaymentProviders
{
    public const string Square = "square";
    public const string Linkly = "linkly";
    public const string Zeller = "zeller";

    /// <summary>手动刷卡：在独立刷卡机上收款，由收银员在 POS 上确认，没有刷卡机证据也无法查询。</summary>
    public const string Manual = "manual";

    public static IReadOnlyList<string> All { get; } = [Square, Linkly, Zeller, Manual];

    /// <summary>归一到通道族；不认识的返回 null。</summary>
    public static string? NormalizeFamily(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        if (normalized.StartsWith(Square, StringComparison.Ordinal))
        {
            return Square;
        }

        if (normalized.StartsWith(Linkly, StringComparison.Ordinal) ||
            normalized.StartsWith("anz", StringComparison.Ordinal))
        {
            return Linkly;
        }

        if (normalized.StartsWith(Manual, StringComparison.Ordinal))
        {
            return Manual;
        }

        return normalized.StartsWith(Zeller, StringComparison.Ordinal) ? Zeller : null;
    }

    /// <summary>解析逗号分隔的配置（如 "square,linkly,zeller"），去重、忽略不认识的项，按固定顺序返回。</summary>
    public static IReadOnlyList<string> ParseList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var families = value
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeFamily)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        return All.Where(families.Contains).ToArray();
    }
}

public sealed record InstallmentRepaymentClaimCreateRequest(
    Guid OperationGuid,
    Guid PaymentGuid,
    decimal Amount,
    PaymentMethodKind Method,
    string IdempotencyKey);

public sealed record InstallmentRepaymentClaimBeginProviderRequest(
    string Provider,
    string ProviderAttemptId);

public sealed record InstallmentRepaymentClaimPrepareProviderRequest(
    Guid PaymentGuid,
    decimal Amount,
    PaymentMethodKind Method,
    string IdempotencyKey,
    string Provider,
    string ProviderAttemptId);

public sealed record InstallmentRepaymentClaimResolveRequest(
    InstallmentRepaymentClaimResolveOutcome Outcome,
    bool CashNotCollectedConfirmed = false,
    string? ProviderAttemptId = null);

public sealed record InstallmentRepaymentClaimCommitRequest(
    string? Reference = null,
    string? ReservationToken = null,
    IReadOnlyList<CardTransactionDto>? CardTransactions = null);

public sealed record InstallmentRepaymentClaimDto(
    Guid InstallmentGuid,
    Guid OperationGuid,
    Guid PaymentGuid,
    decimal Amount,
    PaymentMethodKind Method,
    string IdempotencyKey,
    InstallmentRepaymentClaimStatus Status,
    string? Provider,
    string? ProviderAttemptId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    InstallmentAppendPaymentResponse? Commit = null,
    bool AlreadyExists = false,
    // 发起这笔续付的收银机；主管在别的收银机上结案时据此提示。旧服务端不返回。
    string? ClaimantDeviceCode = null);

/// <summary>主管对卡状态不明的刷卡续付做的人工结论。</summary>
public enum InstallmentRepaymentSupervisorDecision
{
    /// <summary>已向银行 / 刷卡机后台核实扣款成功：按 claim 金额入账。</summary>
    ConfirmedCharged = 1,

    /// <summary>已核实没有扣款：结案为 Declined，解除对该分期的占用。</summary>
    ConfirmedNotCharged = 2
}

/// <summary>占用某个分期的续付 claim；没有占用时 Claim 为 null。</summary>
public sealed record InstallmentRepaymentBlockingClaimResponse(InstallmentRepaymentClaimDto? Claim);

/// <summary>主管结案请求。Reason 必填；Reference 可填银行批准码、RRN 等核实依据。</summary>
public sealed record InstallmentRepaymentClaimSupervisorResolveRequest(
    InstallmentRepaymentSupervisorDecision Decision,
    string Reason,
    string? Reference = null);

public sealed record InstallmentCancelClaimCreateRequest(
    Guid OperationGuid,
    string IdempotencyKey,
    string? Reason,
    string RefundPlanFingerprint,
    InstallmentCancelRefundMode? RefundMode = null);

public sealed record InstallmentCancelClaimResolveRequest(
    InstallmentCancelClaimResolveOutcome Outcome,
    IReadOnlyList<InstallmentRefundPaymentCommandDto>? ApprovedRefunds = null);

public sealed record InstallmentCancelClaimCommitRequest(
    IReadOnlyList<InstallmentRefundPaymentCommandDto> Refunds);

public sealed record InstallmentCancelClaimCommitResponse(
    InstallmentDetailsDto Details,
    bool AlreadyCancelled);

public sealed record InstallmentCancelClaimDto(
    Guid InstallmentGuid,
    Guid OperationGuid,
    string IdempotencyKey,
    string RefundPlanFingerprint,
    InstallmentCancelClaimStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    InstallmentCancelClaimCommitResponse? Commit = null,
    bool AlreadyExists = false,
    string? OriginalDeviceCode = null,
    string? ExecutingDeviceCode = null,
    InstallmentCancelRefundMode RefundMode = InstallmentCancelRefundMode.OriginalRoute);

public sealed record InstallmentLineDto(
    Guid InstallmentLineGuid,
    string ProductCode,
    string? ReferenceCode,
    string DisplayName,
    string LookupCode,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal ActualAmount,
    string? ItemNumber = null);

public sealed record InstallmentPaymentCommandDto(
    Guid PaymentGuid,
    PaymentMethodKind Method,
    decimal Amount,
    string? Reference,
    string? ReservationToken = null,
    IReadOnlyList<CardTransactionDto>? CardTransactions = null,
    string? IdempotencyKey = null);

public sealed record InstallmentCreateRequest(
    Guid InstallmentGuid,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string CashierName,
    DateTimeOffset CreatedAt,
    decimal TotalAmount,
    decimal DownPaymentAmount,
    IReadOnlyList<InstallmentLineDto> Lines,
    InstallmentPaymentCommandDto DownPayment,
    string CustomerName,
    string CustomerPhone,
    string? Note = null);

public sealed record InstallmentCreateResponse(
    Guid InstallmentGuid,
    string InstallmentNumber,
    InstallmentStatus Status,
    decimal PaidAmount,
    decimal BalanceAmount,
    InstallmentDetailsDto Details,
    bool AlreadyExists = false,
    string? Message = null);

public sealed record InstallmentAppendPaymentRequest(
    Guid InstallmentGuid,
    Guid PaymentGuid,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string CashierName,
    decimal Amount,
    PaymentMethodKind Method,
    string? Reference,
    string? ReservationToken = null,
    IReadOnlyList<CardTransactionDto>? CardTransactions = null,
    string? IdempotencyKey = null);

public sealed record InstallmentAppendPaymentResponse(
    Guid InstallmentGuid,
    Guid PaymentGuid,
    decimal PaidAmount,
    decimal BalanceAmount,
    InstallmentStatus Status,
    InstallmentDetailsDto Details,
    bool AlreadyRecorded = false,
    string? Message = null);

public sealed record InstallmentConfirmPickupRequest(
    Guid InstallmentGuid,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string CashierName,
    DateTimeOffset ConfirmedAt,
    string? Note = null,
    Guid OperationGuid = default,
    string? IdempotencyKey = null);

public sealed record InstallmentConfirmPickupResponse(
    Guid InstallmentGuid,
    InstallmentStatus Status,
    DateTimeOffset PickedUpAt,
    InstallmentDetailsDto Details,
    bool AlreadyConfirmed = false);

/// <summary>
/// 修改分期单商品列表：用 <see cref="Lines"/> 整体替换原有商品行（可增行、改数量 / 单价、删行）。
/// 仅在线可用；<see cref="ExpectedUpdatedAt"/> 是乐观并发令牌，必须等于客户端看到的订单 UpdatedAt，
/// 否则服务端返回 409，避免两台设备基于旧快照互相覆盖。门店 / 设备 / 收银员字段由服务端按票据覆盖。
/// </summary>
public sealed record InstallmentAmendLinesRequest(
    Guid InstallmentGuid,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string CashierName,
    IReadOnlyList<InstallmentLineDto> Lines,
    DateTimeOffset ExpectedUpdatedAt,
    string? Reason = null);

public sealed record InstallmentAmendLinesResponse(
    Guid InstallmentGuid,
    InstallmentStatus Status,
    decimal TotalAmount,
    decimal PaidAmount,
    decimal BalanceAmount,
    InstallmentDetailsDto Details);

/// <summary>修改商品列表失败时 API 返回的业务错误码，客户端据此给出明确提示。</summary>
public static class InstallmentAmendLinesErrorCodes
{
    public const string InvalidLines = "INSTALLMENT_AMEND_INVALID_LINES";
    public const string TotalBelowPaid = "INSTALLMENT_AMEND_TOTAL_BELOW_PAID";
    public const string TotalBelowMinimum = "INSTALLMENT_AMEND_TOTAL_BELOW_MINIMUM";
    public const string StatusNotAllowed = "INSTALLMENT_AMEND_STATUS_NOT_ALLOWED";
    public const string Stale = "INSTALLMENT_AMEND_STALE";
}

public enum InstallmentAmendLinesValidation
{
    Valid = 0,
    NoLines,
    InvalidQuantity,
    InvalidUnitPrice,
    InvalidDiscount,
    InvalidActualAmount,
    DuplicateLine,
    MissingText,
    TotalBelowPaid,
    TotalBelowMinimum
}

/// <summary>
/// 修改分期商品列表的共享规则。服务端落库校验与客户端编辑界面必须用同一份，避免口径漂移。
/// 金额一律按 2 位小数、AwayFromZero 舍入，与购物车行金额算法一致。
/// </summary>
public static class InstallmentAmendRules
{
    /// <summary>分期订单总额下限，与创建分期时的下限相同。</summary>
    public const decimal MinimumTotalAmount = 50m;

    public static bool CanAmend(InstallmentStatus status) =>
        status is InstallmentStatus.Active or InstallmentStatus.PaidOff;

    /// <summary>行实收 = 数量 × 单价 − 折扣。</summary>
    public static decimal CalculateActualAmount(decimal quantity, decimal unitPrice, decimal discountAmount) =>
        Round(quantity * unitPrice - discountAmount);

    public static decimal CalculateTotal(IEnumerable<InstallmentLineDto> lines) =>
        Round(lines.Sum(line => line.ActualAmount));

    /// <summary>改后余额 = max(0, 新总额 − 已付)；余额为 0 即视为已付清待提货。</summary>
    public static decimal CalculateBalance(decimal newTotal, decimal paidAmount) =>
        Math.Max(0m, Round(newTotal - paidAmount));

    public static InstallmentStatus ResolveStatus(decimal newTotal, decimal paidAmount) =>
        CalculateBalance(newTotal, paidAmount) <= 0m ? InstallmentStatus.PaidOff : InstallmentStatus.Active;

    public static InstallmentAmendLinesValidation ValidateLines(IReadOnlyList<InstallmentLineDto>? lines)
    {
        if (lines is null || lines.Count == 0)
        {
            return InstallmentAmendLinesValidation.NoLines;
        }

        var seen = new HashSet<Guid>();
        foreach (var line in lines)
        {
            if (line.InstallmentLineGuid == Guid.Empty || !seen.Add(line.InstallmentLineGuid))
            {
                return InstallmentAmendLinesValidation.DuplicateLine;
            }

            if (string.IsNullOrWhiteSpace(line.ProductCode) ||
                string.IsNullOrWhiteSpace(line.DisplayName) ||
                string.IsNullOrWhiteSpace(line.LookupCode))
            {
                return InstallmentAmendLinesValidation.MissingText;
            }

            if (line.Quantity <= 0m)
            {
                return InstallmentAmendLinesValidation.InvalidQuantity;
            }

            if (line.UnitPrice <= 0m)
            {
                return InstallmentAmendLinesValidation.InvalidUnitPrice;
            }

            var gross = Round(line.Quantity * line.UnitPrice);
            if (line.DiscountAmount < 0m || line.DiscountAmount >= gross)
            {
                return InstallmentAmendLinesValidation.InvalidDiscount;
            }

            if (line.ActualAmount <= 0m ||
                line.ActualAmount != CalculateActualAmount(line.Quantity, line.UnitPrice, line.DiscountAmount))
            {
                return InstallmentAmendLinesValidation.InvalidActualAmount;
            }
        }

        return InstallmentAmendLinesValidation.Valid;
    }

    /// <summary>新总额不得低于已付款，也不得低于分期订单总额下限。</summary>
    public static InstallmentAmendLinesValidation ValidateTotal(decimal newTotal, decimal paidAmount)
    {
        if (newTotal < Round(paidAmount))
        {
            return InstallmentAmendLinesValidation.TotalBelowPaid;
        }

        return newTotal < MinimumTotalAmount
            ? InstallmentAmendLinesValidation.TotalBelowMinimum
            : InstallmentAmendLinesValidation.Valid;
    }

    private static decimal Round(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
}

public sealed record InstallmentRefundPaymentCommandDto(
    Guid PaymentGuid,
    PaymentMethodKind Method,
    decimal Amount,
    string? Reference,
    IReadOnlyList<CardTransactionDto>? CardTransactions = null,
    string? IdempotencyKey = null,
    Guid OriginalPaymentGuid = default);

public sealed record InstallmentCancelRequest(
    Guid InstallmentGuid,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string CashierName,
    DateTimeOffset CancelledAt,
    IReadOnlyList<InstallmentRefundPaymentCommandDto> Refunds,
    string? Reason = null,
    string? IdempotencyKey = null,
    InstallmentCancelRefundMode? RefundMode = null);

public sealed record InstallmentCancelResponse(
    Guid InstallmentGuid,
    InstallmentStatus Status,
    InstallmentDetailsDto Details,
    bool AlreadyCancelled = false,
    string? Message = null);

public sealed record InstallmentVoidRequest(
    Guid InstallmentGuid,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string CashierName,
    DateTimeOffset VoidedAt,
    string? Reason = null,
    string? IdempotencyKey = null,
    Guid OperationGuid = default);

public sealed record InstallmentVoidResponse(
    Guid InstallmentGuid,
    InstallmentStatus Status,
    InstallmentDetailsDto Details,
    bool AlreadyVoided = false,
    string? Message = null);

public sealed record InstallmentHistoryQueryRequest(
    string StoreCode,
    string? DeviceCode = null,
    DateTimeOffset? CreatedFrom = null,
    DateTimeOffset? CreatedTo = null,
    string? Keyword = null,
    InstallmentStatus? Status = null,
    int Take = 100,
    int Skip = 0,
    DateTimeOffset? UpdatedFrom = null,
    DateTimeOffset? UpdatedTo = null,
    bool OrderByUpdatedAt = false);

public sealed record InstallmentHistoryQueryResponse(
    IReadOnlyList<InstallmentSummaryDto> Orders);

public sealed record InstallmentSummaryDto(
    Guid InstallmentGuid,
    string InstallmentNumber,
    string StoreCode,
    string DeviceCode,
    string CashierName,
    string CustomerName,
    string CustomerPhone,
    DateTimeOffset CreatedAt,
    decimal TotalAmount,
    decimal DownPaymentAmount,
    decimal PaidAmount,
    decimal BalanceAmount,
    InstallmentStatus Status,
    DateTimeOffset UpdatedAt,
    InstallmentCancellationKind? CancellationKind = null);

public sealed record InstallmentDetailsDto(
    Guid InstallmentGuid,
    string InstallmentNumber,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string CashierName,
    string CustomerName,
    string CustomerPhone,
    DateTimeOffset CreatedAt,
    decimal TotalAmount,
    decimal MinimumDownPayment,
    decimal DownPaymentAmount,
    decimal PaidAmount,
    decimal BalanceAmount,
    InstallmentStatus Status,
    IReadOnlyList<InstallmentLineDto> Lines,
    IReadOnlyList<InstallmentPaymentDto> Payments,
    InstallmentPickupInfoDto? PickupInfo,
    InstallmentCancellationInfoDto? CancellationInfo = null,
    string? Note = null,
    DateTimeOffset? UpdatedAt = null);

public sealed record InstallmentPaymentDto(
    Guid PaymentGuid,
    PaymentMethodKind Method,
    decimal Amount,
    string? Reference,
    InstallmentPaymentStatus Status,
    DateTimeOffset RecordedAt,
    string CashierId,
    string DeviceCode,
    IReadOnlyList<CardTransactionDto>? CardTransactions = null,
    string? IdempotencyKey = null,
    [property: JsonIgnore] string? ReservationToken = null,
    string? CashierName = null);

public sealed record InstallmentPickupInfoDto(
    DateTimeOffset PickedUpAt,
    string PickedUpBy,
    string? Note = null);

public sealed record InstallmentCancellationInfoDto(
    InstallmentCancellationKind Kind,
    DateTimeOffset CancelledAt,
    string CancelledBy,
    string? Reason = null,
    string? IdempotencyKey = null);
