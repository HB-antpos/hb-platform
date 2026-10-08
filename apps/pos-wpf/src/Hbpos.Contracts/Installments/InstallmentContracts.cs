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
    bool AmendLinesSupported = false);

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
    bool AlreadyExists = false);

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
