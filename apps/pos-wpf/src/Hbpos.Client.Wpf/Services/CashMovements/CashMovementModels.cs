using Hbpos.Contracts.CashMovements;

namespace Hbpos.Client.Wpf.Services;

public enum CashMovementDirection
{
    In = 1,
    Out = 2
}

/// <summary>
/// 收银端现金存入 / 取出的原因码，与服务端契约（<see cref="CashMovementContractConstants"/>）同一份取值。
/// 每个原因码有固定的「自然方向」：普通记录方向等于自然方向，冲正记录方向相反、金额相同。
/// </summary>
public static class CashMovementReasons
{
    public const string ChangeTopUp = CashMovementContractConstants.ReasonChangeTopUp;
    public const string OtherIn = CashMovementContractConstants.ReasonOtherIn;
    public const string SafeDrop = CashMovementContractConstants.ReasonSafeDrop;
    public const string PaidOut = CashMovementContractConstants.ReasonPaidOut;

    public static IReadOnlyList<string> InReasons { get; } = [ChangeTopUp, OtherIn];

    public static IReadOnlyList<string> OutReasons { get; } = [SafeDrop, PaidOut];

    public static bool IsKnown(string? reason) => reason is ChangeTopUp or OtherIn or SafeDrop or PaidOut;

    public static CashMovementDirection NaturalDirection(string reason) => reason switch
    {
        ChangeTopUp or OtherIn => CashMovementDirection.In,
        SafeDrop or PaidOut => CashMovementDirection.Out,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown cash movement reason.")
    };

    /// <summary>「其他存入」「小额支出」必须写备注，与服务端及后台现金支出的原因口径一致（至少 2 个字）。</summary>
    public static bool IsNoteRequired(string reason) => reason is OtherIn or PaidOut;

    public static string ToContractDirection(CashMovementDirection direction) =>
        direction == CashMovementDirection.In
            ? CashMovementContractConstants.DirectionIn
            : CashMovementContractConstants.DirectionOut;
}

/// <summary>本机一笔现金进出记录（只增不改；输错由一条 VoidsMovementGuid 指向它的冲正记录抵消）。</summary>
public sealed record LocalCashMovement(
    Guid MovementGuid,
    string StoreCode,
    string DeviceCode,
    CashMovementDirection Direction,
    string ReasonCode,
    decimal Amount,
    string? Note,
    Guid? VoidsMovementGuid,
    DateTime BusinessDate,
    DateTimeOffset OccurredAt,
    string CashierId,
    string CashierName,
    string? AuthorizedById,
    string? AuthorizedByName,
    string DisplayNumber,
    string UploadStatus = LocalCashMovementUploadStatus.Pending)
{
    public bool IsVoid => VoidsMovementGuid is not null;

    /// <summary>对钱箱现金的影响：存入为正、取出为负。</summary>
    public decimal SignedAmount => Direction == CashMovementDirection.In ? Amount : -Amount;
}

public static class LocalCashMovementUploadStatus
{
    public const string Pending = "Pending";
    public const string Synced = "Synced";
    public const string Rejected = "Rejected";
}

/// <summary>
/// 一个日结区间内按原因码汇总的现金进出（冲正已抵消）。存入 = 补零钱 + 其他存入；取出 = 交店长 + 小额支出。
/// 应有现金 = 现金净额 + 进位 + 存入 − 取出。
/// </summary>
public sealed record DailyCloseCashMovementTotals(
    decimal ChangeTopUp,
    decimal OtherIn,
    decimal SafeDrop,
    decimal PaidOut)
{
    public static DailyCloseCashMovementTotals Empty { get; } = new(0m, 0m, 0m, 0m);

    public decimal CashIn => Round(ChangeTopUp + OtherIn);

    public decimal CashOut => Round(SafeDrop + PaidOut);

    public bool HasAny => ChangeTopUp != 0m || OtherIn != 0m || SafeDrop != 0m || PaidOut != 0m;

    /// <summary>
    /// 按原因码汇总：方向等于自然方向的记录加、方向相反的冲正记录减，所以一对「原记录 + 冲正」正好抵消为 0。
    /// </summary>
    public static DailyCloseCashMovementTotals FromMovements(IEnumerable<LocalCashMovement> movements)
    {
        var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var movement in movements)
        {
            if (!CashMovementReasons.IsKnown(movement.ReasonCode))
            {
                continue;
            }

            var natural = CashMovementReasons.NaturalDirection(movement.ReasonCode);
            var signed = movement.Direction == natural ? movement.Amount : -movement.Amount;
            totals[movement.ReasonCode] = totals.GetValueOrDefault(movement.ReasonCode) + signed;
        }

        return new DailyCloseCashMovementTotals(
            Round(totals.GetValueOrDefault(CashMovementReasons.ChangeTopUp)),
            Round(totals.GetValueOrDefault(CashMovementReasons.OtherIn)),
            Round(totals.GetValueOrDefault(CashMovementReasons.SafeDrop)),
            Round(totals.GetValueOrDefault(CashMovementReasons.PaidOut)));
    }

    private static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}

/// <summary>某营业日本机的现金进出：明细（按发生时间）+ 汇总 + 已被冲正的原记录。</summary>
public sealed record CashMovementPeriodSnapshot(
    DateTime BusinessDate,
    IReadOnlyList<LocalCashMovement> Movements,
    DailyCloseCashMovementTotals Totals,
    IReadOnlySet<Guid> VoidedMovementGuids)
{
    public bool IsVoided(LocalCashMovement movement) => VoidedMovementGuids.Contains(movement.MovementGuid);
}

/// <summary>新建一笔现金进出的输入（方向由原因码决定）。</summary>
public sealed record CashMovementDraft(string ReasonCode, decimal Amount, string? Note);

/// <summary>操作授权人：收银员本人有权限时为 null；由他人扫码授权时记授权人。</summary>
public sealed record CashMovementAuthorizer(string AuthorizedById, string AuthorizedByName);

/// <summary>现金进出业务校验失败；Code 对应界面文案键 cashMovement.error.{Code}。</summary>
public sealed class CashMovementValidationException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
