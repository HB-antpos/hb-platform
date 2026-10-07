namespace Hbpos.Contracts.DailyClose;

/// <summary>
/// 日结记录上传契约的共享常量。收银端（WPF / 手持 / iPad）与 Hbpos.Api 校验必须使用同一份口径，
/// 避免面额、支付方式或客户端类型在两侧各写一遍后产生漂移。
/// </summary>
public static class DailyCloseContractConstants
{
    /// <summary>当前唯一受支持的 schemaVersion。</summary>
    public const int SchemaVersion = 1;

    public const string ClientKindWpf = "Wpf";
    public const string ClientKindHandheld = "Handheld";
    public const string ClientKindIpad = "Ipad";

    public const string TenderCash = "Cash";
    public const string TenderCard = "Card";
    public const string TenderVoucher = "Voucher";

    /// <summary>纸币与硬币的分界：面额 &gt;= 500 分（$5）归纸币，&lt;= 200 分（$2）归硬币。</summary>
    public const int NoteMinimumDenominationCents = 500;

    /// <summary>AUD 11 档现金面额（分），按面额降序；每次上传每档必须恰好出现一次。</summary>
    public static IReadOnlyList<int> DenominationCents { get; } =
        [10000, 5000, 2000, 1000, 500, 200, 100, 50, 20, 10, 5];

    /// <summary>三种允许的客户端类型。</summary>
    public static IReadOnlyList<string> ClientKinds { get; } =
        [ClientKindWpf, ClientKindHandheld, ClientKindIpad];

    /// <summary>三种必须各出现一次的支付方式。</summary>
    public static IReadOnlyList<string> TenderMethods { get; } =
        [TenderCash, TenderCard, TenderVoucher];
}

/// <summary>单个支付方式的日结汇总；Net 应等于 Sales 减 Refund（允许 0.01 的舍入误差）。</summary>
public sealed record DailyCloseTenderSync(
    string Method,
    decimal SalesAmount,
    decimal RefundAmount,
    decimal NetAmount);

/// <summary>单个面额的现金盘点数量；DenominationCents 以分为单位，例如 $100 为 10000。</summary>
public sealed record DailyCloseCashCountSync(
    int DenominationCents,
    int Quantity);

/// <summary>
/// 收银端上传的一次日结快照。StoreCode / DeviceCode 必须与设备认证 claims 一致；
/// DailyCloseGuid 由客户端生成，是跨重试的幂等键。
/// </summary>
public sealed record DailyCloseSyncRequest(
    int SchemaVersion,
    Guid DailyCloseGuid,
    string StoreCode,
    string DeviceCode,
    string ClientKind,
    DateOnly BusinessDate,
    DateTimeOffset PeriodFrom,
    DateTimeOffset PeriodTo,
    DateTimeOffset SavedAt,
    string CashierId,
    string CashierName,
    string? AppVersion,
    int OrderCount,
    decimal ReturnQuantity,
    decimal RefundAmount,
    IReadOnlyList<DailyCloseTenderSync> Tenders,
    IReadOnlyList<DailyCloseCashCountSync> CashCounts,
    decimal NoteSubtotal,
    decimal CoinSubtotal,
    decimal CountedCashAmount,
    decimal CashDifference);

/// <summary>
/// Accepted 恒为 true（被拒绝时走 400 / 409）；AlreadySynced 表示同一份内容此前已入库；
/// ReplacedPlaceholder 表示这次上传覆盖了此前由审计事件回填的占位记录。
/// </summary>
public sealed record DailyCloseSyncResponse(
    bool Accepted,
    bool AlreadySynced,
    bool ReplacedPlaceholder);
