using Hbpos.Client.Wpf.Models;

namespace Hbpos.Client.Wpf.Services;

// 恢复中心定点身份：provider + AttemptGuid 唯一定位一条未结 attempt。
public readonly record struct CardRecoveryAttemptKey(
    CardProcessorKind Processor,
    Guid AttemptGuid);

// 恢复队列条目：只读快照，供列表展示与定点恢复/结案使用。
public sealed record CardRecoveryQueueItem(
    CardProcessorKind Processor,
    Guid AttemptGuid,
    string OperationKind,
    decimal Amount,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    string Environment,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? OrderDraftJson = null,
    string? SessionId = null,
    string? TxnRef = null,
    string? CheckoutId = null,
    string? ResponseCode = null,
    string? ResponseText = null,
    string? PaymentReference = null,
    string? PaymentId = null,
    Guid? OperationGuid = null,
    string? PaymentStatus = null)
{
    public bool IsOpen { get; init; } = true;

    public CardRecoveryAttemptKey Key => new(Processor, AttemptGuid);
}

// 恢复中心需要区分“某 provider 没有未结记录”和“某 provider 本次加载失败”。
// Items 只包含本次成功读取的 provider；界面必须保留失败 provider 的最后已知快照。
public sealed record CardRecoveryQueueLoadResult(
    IReadOnlyList<CardRecoveryQueueItem> Items,
    IReadOnlyList<CardProcessorKind> FailedProviders)
{
    public bool IsComplete => FailedProviders.Count == 0;
}

public interface ICardRecoveryQueueLoader
{
    Task<CardRecoveryQueueLoadResult> LoadHistoryQueueAsync(
        PosSessionState session, CancellationToken cancellationToken = default) =>
        LoadOpenQueueAsync(session, cancellationToken);

    Task<CardRecoveryQueueLoadResult> LoadOpenQueueAsync(
        PosSessionState session,
        CancellationToken cancellationToken = default);
}

// 恢复中心统一的三态主管决定；仅作为定点结案命令，不落库、不是持久化状态枚举。
public enum CardRecoverySupervisorDecision
{
    ConfirmProcessed,
    ConfirmNotProcessed,
    ContinueWaiting
}

// 收银员登录/启动后对“确定性”未结记录做自动定点恢复的汇总。
// 只涉及不需要再向刷卡机发起任何交易、也不需要触碰购物车的记录：已批准待建单、待收尾、已完成未 ack。
public sealed record CardAutoRecoverySummary(
    int Examined,
    int RecoveredOrders,
    PosSessionState? UpdatedSession = null)
{
    public static CardAutoRecoverySummary None { get; } = new(0, 0);
}

// 定点结案的统一结果。
public sealed record CardRecoveryResolutionResult(
    bool Succeeded,
    string Message,
    CardPaymentRecoveryResult? RecoveryResult = null,
    bool RetryAllowed = false,
    bool LockRetained = false,
    bool ResolutionPersisted = false,
    bool ResolutionApplied = false);

public static class CardRecoveryPhases
{
    public const string None = "None";
    public const string FinalizePending = "FinalizePending";
}
