namespace Hbpos.Contracts.CashMovements;

/// <summary>
/// 收银端现金进出（Cash In / Cash Out）上传契约的共享常量。WPF / 手持 / iPad 与 Hbpos.Api 必须使用同一份口径。
/// </summary>
public static class CashMovementContractConstants
{
    /// <summary>当前唯一受支持的 schemaVersion。</summary>
    public const int SchemaVersion = 1;

    public const string DirectionIn = "In";
    public const string DirectionOut = "Out";

    /// <summary>补零钱：从保险柜拿零钱放进钱箱（存入）。</summary>
    public const string ReasonChangeTopUp = "ChangeTopUp";

    /// <summary>其他存入，备注必填（存入）。</summary>
    public const string ReasonOtherIn = "OtherIn";

    /// <summary>交店长 / 入保险柜：钱离开钱箱但仍在店里（取出）。</summary>
    public const string ReasonSafeDrop = "SafeDrop";

    /// <summary>小额现金支出，备注必填；后台据此自动生成「现金支出」（取出）。</summary>
    public const string ReasonPaidOut = "PaidOut";

    /// <summary>备注最短字数（与后台现金支出原因口径一致）与最长字数。</summary>
    public const int MinimumRequiredNoteLength = 2;
    public const int MaximumNoteLength = 200;

    /// <summary>单次批量上传的最大条数。</summary>
    public const int MaximumBatchSize = 100;

    /// <summary>
    /// 每个原因码的「自然方向」。普通记录的方向必须等于自然方向；
    /// 冲正记录（VoidsMovementGuid 非空）的方向必须与自然方向相反，金额与原记录相同。
    /// </summary>
    public static IReadOnlyDictionary<string, string> NaturalDirections { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ReasonChangeTopUp] = DirectionIn,
            [ReasonOtherIn] = DirectionIn,
            [ReasonSafeDrop] = DirectionOut,
            [ReasonPaidOut] = DirectionOut
        };

    /// <summary>备注必填的原因码。</summary>
    public static IReadOnlyList<string> NoteRequiredReasons { get; } = [ReasonOtherIn, ReasonPaidOut];
}

/// <summary>
/// 一笔收银端现金进出。MovementGuid 由客户端生成，是跨重试的幂等键；记录写入后不可修改，
/// 输错只能再上传一条 VoidsMovementGuid 指向原记录的冲正记录。
/// </summary>
public sealed record CashMovementSync(
    Guid MovementGuid,
    string Direction,
    string ReasonCode,
    decimal Amount,
    string? Note,
    Guid? VoidsMovementGuid,
    DateOnly BusinessDate,
    DateTimeOffset OccurredAt,
    string CashierId,
    string CashierName,
    // 收银员没有对应权限、由他人扫码授权时记录授权人；本人有权限时为空。
    string? AuthorizedById,
    string? AuthorizedByName,
    // 本机流水号（如 CM-0009），只用于凭条和列表展示，不参与幂等。
    string? DisplayNumber);

/// <summary>批量上传；StoreCode / DeviceCode 必须与设备认证 claims 一致。</summary>
public sealed record CashMovementSyncRequest(
    int SchemaVersion,
    string StoreCode,
    string DeviceCode,
    string ClientKind,
    string? AppVersion,
    IReadOnlyList<CashMovementSync> Movements);

/// <summary>单条处理结果：Accepted / AlreadySynced 表示服务端已持有；Rejected 时带错误码，客户端不再自动重试。</summary>
public sealed record CashMovementSyncResult(
    Guid MovementGuid,
    string Status,
    string? Code,
    string? Message);

public sealed record CashMovementSyncResponse(IReadOnlyList<CashMovementSyncResult> Results);

public static class CashMovementSyncStatuses
{
    public const string Accepted = "Accepted";
    public const string AlreadySynced = "AlreadySynced";
    public const string Rejected = "Rejected";
}
