namespace Hbpos.Client.Wpf.Services.Zeller;

// 中文注释：这个文件里的类型都是我们自己的，不引用 Zeller.PaymentsSdk。
// 只有 SDK 网关实现需要引用 SDK，其余逻辑（分类、恢复、测试）都只依赖这些类型。

/// <summary>Zeller 交易状态。Unknown 表示 SDK 返回了本程序不认识的状态，一律按结果未知处理。</summary>
public enum ZellerTransactionStatus
{
    Unknown = 0,
    Approved,
    Declined,
    Failed
}

public enum ZellerTransactionType
{
    Unknown = 0,
    Purchase,
    Refund
}

/// <summary>
/// SDK 0.1.0 的全部 26 种错误类型。数值与 SDK 的 ErrorType 一致，网关按数值转换；
/// SDK 以后新增的类型落到 Unrecognised，按结果未知处理。
/// </summary>
public enum ZellerErrorType
{
    Unrecognised = -1,
    NetworkFailure = 0,
    SetupRequired = 1,
    OperationFailure = 2,
    OperationBusy = 3,
    OperationInterrupted = 4,
    OperationRequestInvalid = 5,
    OperationResponseInvalid = 6,
    ConnectionFailure = 7,
    FeatureNotAvailable = 8,
    PermissionsMissing = 9,
    PairedDeviceBusy = 10,
    PairedDeviceNotAvailable = 11,
    Cancelled = 12,
    InactivityTimeout = 13,
    CardDeclined = 14,
    CardBlocked = 15,
    CardNotSupported = 16,
    CardReadFailure = 17,
    CardInsertRequired = 18,
    TransactionNotFound = 19,
    TransactionResponseTimeout = 20,
    TransactionResponseInvalid = 21,
    TransactionNetworkFailure = 22,
    TransactionSystemFailure = 23,
    TransactionDeclined = 24,
    TransactionFailure = 25
}

/// <summary>扣款、退款过程中 SDK 推来的事件。只区分状态机关心的两种，其余归为 Other。</summary>
public enum ZellerTerminalEventKind
{
    Other = 0,

    /// <summary>刷卡机已提示刷卡：请求已经到达刷卡机。</summary>
    AwaitingPaymentMethod,

    /// <summary>刷卡机已读到卡，正在处理：从这一刻起钱可能已经扣了。</summary>
    Processing
}

public sealed record ZellerTerminalEvent(ZellerTerminalEventKind Kind, string Name, string? Message = null);

/// <summary>一笔 Zeller 交易。金额单位是分。</summary>
/// <remarks>
/// 中文注释：扣款、退款的返回带 ReceiptText 和 ApprovalCode；按 reference 查回来的交易这两项为空，只有 ReceiptLink。
/// Timestamp 来自刷卡机时钟，实测比电脑快十几到三十秒，不能用来匹配交易。
/// </remarks>
public sealed record ZellerTransaction(
    ZellerTransactionStatus Status,
    ZellerTransactionType Type,
    int AmountCents,
    string? TransactionUuid,
    string? ExternalReference,
    string? ResponseCode = null,
    string? ResponseText = null,
    string? ApprovalCode = null,
    string? Rrn = null,
    string? PanMasked = null,
    string? Scheme = null,
    string? Bin = null,
    string? MerchantId = null,
    string? TerminalId = null,
    string? ReceiptText = null,
    string? ReceiptLink = null,
    DateTimeOffset? Timestamp = null);

/// <summary>一次扣款或退款调用是怎么结束的。</summary>
public enum ZellerCallOutcome
{
    /// <summary>SDK 返回了一笔交易。注意：被拒的交易也在这里，必须再看 Status。</summary>
    Transaction,

    /// <summary>SDK 返回了错误类型。</summary>
    Error,

    /// <summary>看门狗放弃了这次调用：SDK 一直没有返回，调用已随 Terminal 重建而作废。</summary>
    Abandoned,

    /// <summary>SDK 抛出了异常。</summary>
    Faulted
}

/// <summary>扣款或退款调用的结果，加上调用期间收到的事件证据。</summary>
public sealed record ZellerCallResult(
    ZellerCallOutcome Outcome,
    ZellerTransaction? Transaction = null,
    ZellerErrorType? ErrorType = null,
    string? Message = null,
    bool ReachedTerminal = false,
    bool ProcessingStarted = false)
{
    public static ZellerCallResult FromTransaction(ZellerTransaction transaction, bool reachedTerminal = true, bool processingStarted = true) =>
        new(ZellerCallOutcome.Transaction, transaction, ReachedTerminal: reachedTerminal, ProcessingStarted: processingStarted);

    public static ZellerCallResult FromError(ZellerErrorType errorType, string? message = null, bool reachedTerminal = false, bool processingStarted = false) =>
        new(ZellerCallOutcome.Error, ErrorType: errorType, Message: message, ReachedTerminal: reachedTerminal, ProcessingStarted: processingStarted);

    public static ZellerCallResult Abandoned(bool reachedTerminal = false, bool processingStarted = false) =>
        new(ZellerCallOutcome.Abandoned, ReachedTerminal: reachedTerminal, ProcessingStarted: processingStarted);

    public static ZellerCallResult Faulted(string message, bool reachedTerminal = false, bool processingStarted = false) =>
        new(ZellerCallOutcome.Faulted, Message: message, ReachedTerminal: reachedTerminal, ProcessingStarted: processingStarted);
}

/// <summary>按 reference 查询的结果。Succeeded 为 false 时不能把「没有交易」当成结论。</summary>
public sealed record ZellerQueryResult(
    bool Succeeded,
    IReadOnlyList<ZellerTransaction> Transactions,
    ZellerErrorType? ErrorType = null,
    string? Message = null)
{
    public static ZellerQueryResult Found(params ZellerTransaction[] transactions) => new(true, transactions);

    public static ZellerQueryResult Failed(ZellerErrorType errorType, string? message = null) =>
        new(false, [], errorType, message);
}

/// <summary>初始化或配对的结果。SDK 0.1.0 只告诉我们成功与否，拿不到刷卡机 ID 和 Site。</summary>
public sealed record ZellerPairingResult(bool IsPaired, ZellerErrorType? ErrorType = null, string? Message = null);

public sealed record ZellerPurchaseRequest(int AmountCents, string Reference);

/// <summary>按原交易退款。OriginalTransactionUuid 是原扣款的 transactionUuid，不用 reference（一个 reference 可能对应多笔）。</summary>
public sealed record ZellerRefundRequest(int AmountCents, string Reference, string OriginalTransactionUuid);
