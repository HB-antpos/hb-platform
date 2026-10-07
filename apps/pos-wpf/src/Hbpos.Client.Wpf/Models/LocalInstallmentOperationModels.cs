using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Wpf.Models;

/// <summary>
/// 本地分期金融操作的类型。每次操作都有稳定 GUID，重启后不得重新生成。
/// </summary>
public enum LocalInstallmentOperationKind
{
    Create = 1,
    Repayment = 2,
    Cancel = 3,
    Refund = 4,
    Pickup = 5
}

/// <summary>
/// 只在明确没有金融副作用时才能进入 Failed；未知结果必须保持锁定。
/// </summary>
public enum LocalInstallmentOperationState
{
    Prepared = 1,
    TerminalSubmitting = 2,
    ResultUnknown = 3,
    TerminalApproved = 4,
    ApiSubmitting = 5,
    Completed = 6,
    Failed = 7
}

public enum LocalInstallmentRefundStepState
{
    Prepared = 1,
    TerminalSubmitting = 2,
    ResultUnknown = 3,
    Approved = 4,
    Completed = 5,
    Failed = 6,
    SupervisorConfirmedRefunded = 7
}

public enum InstallmentRefundSupervisorDecision
{
    ConfirmRefunded = 1,
    ConfirmNotRefunded = 2,
    ContinueWaiting = 3
}

public sealed record LocalInstallmentOperation(
    Guid OperationGuid,
    LocalInstallmentOperationKind Kind,
    Guid InstallmentGuid,
    Guid? PaymentGuid,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string IdempotencyKey,
    string RequestJson,
    LocalInstallmentOperationState State,
    string? TerminalAttemptGuid,
    string? TerminalProcessor,
    string? ResponseJson,
    string? FailureMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record LocalInstallmentRefundStep(
    Guid RefundStepGuid,
    Guid OperationGuid,
    Guid OriginalPaymentGuid,
    PaymentMethodKind Method,
    decimal Amount,
    string? OriginalReference,
    string IdempotencyKey,
    LocalInstallmentRefundStepState State,
    string? RefundReference,
    string? CardTransactionsJson,
    string? FailureMessage,
    InstallmentRefundSupervisorDecision? SupervisorDecision,
    string? SupervisorUserId,
    string? SupervisorReason,
    string? SupervisorEvidence,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ProviderEnvironment = null,
    // 原付款方式；退代金券时 Method 为 Voucher、此列保留原方式供退款计划指纹使用。旧数据为空即等于 Method。
    PaymentMethodKind? OriginalMethod = null);

public sealed record InstallmentRefundSupervisorResolution(
    InstallmentRefundSupervisorDecision Decision,
    string OperatorId,
    string Reason,
    string? Evidence = null,
    string? RefundReference = null,
    string? OperatorUserGuid = null,
    string? OperatorName = null);

public sealed record InstallmentOperationRecoveryResult(
    Guid OperationGuid,
    LocalInstallmentOperationKind Kind,
    LocalInstallmentOperationState State,
    bool ReplayedApi,
    string? Message = null);
