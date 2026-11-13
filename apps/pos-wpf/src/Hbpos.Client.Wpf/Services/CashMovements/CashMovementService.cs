using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.CashMovements;

namespace Hbpos.Client.Wpf.Services;

public interface ICashMovementService
{
    /// <summary>本机某营业日的现金进出（与日结同一个本地自然日区间）。</summary>
    Task<CashMovementPeriodSnapshot> GetPeriodAsync(
        PosSessionState session,
        DateTime businessDate,
        CancellationToken cancellationToken = default);

    /// <summary>今天的营业日（门店本地日期）。</summary>
    DateTime Today { get; }

    Task<LocalCashMovement> CreateAsync(
        PosSessionState session,
        CashMovementDraft draft,
        CashMovementAuthorizer? authorizer,
        CancellationToken cancellationToken = default);

    /// <summary>冲正一条原记录：生成一条方向相反、金额相同、指向原记录的记录。</summary>
    Task<LocalCashMovement> VoidAsync(
        PosSessionState session,
        LocalCashMovement original,
        string? note,
        CashMovementAuthorizer? authorizer,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 收银端现金存入 / 取出：先写本地 SQLite 再唤醒上传（断网照常可用）。
/// 不设单笔上限与主管授权阈值（2026-11-13 已定口径）；权限由调用方按收银员权限校验。
/// </summary>
public sealed class CashMovementService(
    ILocalCashMovementRepository repository,
    IDailyCloseUploadScheduler? uploadScheduler = null,
    TimeProvider? timeProvider = null,
    TimeZoneInfo? businessTimeZone = null) : ICashMovementService
{
    // 与服务端一致的金额健全性上限（防止误输入多个 0），不是业务上的单笔限额。
    internal const decimal MaximumAmount = 1_000_000m;

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly TimeZoneInfo timeZone = businessTimeZone ?? TimeZoneInfo.Local;

    public DateTime Today => TimeZoneInfo.ConvertTime(clock.GetUtcNow(), timeZone).Date;

    public async Task<CashMovementPeriodSnapshot> GetPeriodAsync(
        PosSessionState session,
        DateTime businessDate,
        CancellationToken cancellationToken = default)
    {
        var period = DailyClosePeriodFactory.Create(businessDate.Date, timeZone);
        var movements = await repository.ListAsync(
            session.StoreCode,
            session.DeviceCode,
            period.From,
            period.To,
            cancellationToken);
        var voided = movements
            .Where(item => item.VoidsMovementGuid is not null)
            .Select(item => item.VoidsMovementGuid!.Value)
            .ToHashSet();
        return new CashMovementPeriodSnapshot(
            businessDate.Date,
            movements,
            DailyCloseCashMovementTotals.FromMovements(movements),
            voided);
    }

    public async Task<LocalCashMovement> CreateAsync(
        PosSessionState session,
        CashMovementDraft draft,
        CashMovementAuthorizer? authorizer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!CashMovementReasons.IsKnown(draft.ReasonCode))
        {
            throw new CashMovementValidationException("reasonRequired", "A cash movement reason is required.");
        }

        var amount = ValidateAmount(draft.Amount);
        var note = NormalizeNote(draft.Note);
        if (CashMovementReasons.IsNoteRequired(draft.ReasonCode) &&
            (note is null || note.Length < CashMovementContractConstants.MinimumRequiredNoteLength))
        {
            throw new CashMovementValidationException("noteRequired", "A note of at least 2 characters is required.");
        }

        RequireScope(session);
        var now = clock.GetUtcNow();
        var movement = new LocalCashMovement(
            Guid.NewGuid(),
            session.StoreCode,
            session.DeviceCode,
            CashMovementReasons.NaturalDirection(draft.ReasonCode),
            draft.ReasonCode,
            amount,
            note,
            null,
            TimeZoneInfo.ConvertTime(now, timeZone).Date,
            TimeZoneInfo.ConvertTime(now, timeZone),
            session.CashierId,
            session.CashierName,
            authorizer?.AuthorizedById,
            authorizer?.AuthorizedByName,
            await repository.NextDisplayNumberAsync(session.StoreCode, session.DeviceCode, cancellationToken));
        await repository.TryInsertAsync(movement, cancellationToken);
        RequestUpload();
        return movement;
    }

    public async Task<LocalCashMovement> VoidAsync(
        PosSessionState session,
        LocalCashMovement original,
        string? note,
        CashMovementAuthorizer? authorizer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        RequireScope(session);
        if (original.IsVoid)
        {
            throw new CashMovementValidationException("cannotVoidVoid", "A void record cannot be voided.");
        }

        if (!string.Equals(original.StoreCode, session.StoreCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(original.DeviceCode, session.DeviceCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new CashMovementValidationException("otherDevice", "Only records of this till can be voided.");
        }

        // 只能冲正今天的记录；今天保存过日结之后，之前的记录也不能再冲正（否则已打印、已上传的日结对不上）。
        var now = clock.GetUtcNow();
        var today = TimeZoneInfo.ConvertTime(now, timeZone).Date;
        if (original.BusinessDate.Date != today)
        {
            throw new CashMovementValidationException("notToday", "Only today's records can be voided.");
        }

        if (await repository.HasDailyCloseSavedAfterAsync(
                session.StoreCode,
                session.DeviceCode,
                today,
                original.OccurredAt,
                cancellationToken))
        {
            throw new CashMovementValidationException("closedAfter", "The daily close has been saved after this record.");
        }

        var reversal = new LocalCashMovement(
            Guid.NewGuid(),
            session.StoreCode,
            session.DeviceCode,
            original.Direction == CashMovementDirection.In ? CashMovementDirection.Out : CashMovementDirection.In,
            original.ReasonCode,
            original.Amount,
            NormalizeNote(note),
            original.MovementGuid,
            today,
            TimeZoneInfo.ConvertTime(now, timeZone),
            session.CashierId,
            session.CashierName,
            authorizer?.AuthorizedById,
            authorizer?.AuthorizedByName,
            await repository.NextDisplayNumberAsync(session.StoreCode, session.DeviceCode, cancellationToken));
        if (!await repository.TryInsertAsync(reversal, cancellationToken))
        {
            throw new CashMovementValidationException("alreadyVoided", "This record has already been voided.");
        }

        RequestUpload();
        return reversal;
    }

    internal static decimal ValidateAmount(decimal amount)
    {
        if (amount <= 0m || amount > MaximumAmount || decimal.Round(amount, 2) != amount)
        {
            throw new CashMovementValidationException("invalidAmount", "Amount must be positive with at most 2 decimals.");
        }

        return amount;
    }

    private static string? NormalizeNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return null;
        }

        var trimmed = note.Trim();
        return trimmed.Length <= CashMovementContractConstants.MaximumNoteLength
            ? trimmed
            : trimmed[..CashMovementContractConstants.MaximumNoteLength];
    }

    private static void RequireScope(PosSessionState session)
    {
        if (string.IsNullOrWhiteSpace(session.StoreCode) || string.IsNullOrWhiteSpace(session.DeviceCode))
        {
            throw new CashMovementValidationException("deviceNotRegistered", "The till is not registered.");
        }
    }

    private void RequestUpload()
    {
        try
        {
            uploadScheduler?.RequestUpload();
        }
        catch (Exception ex)
        {
            // 记录已落库：唤醒失败时 Worker 的 30 秒轮询仍会补传。
            ConsoleLog.WriteError(
                "CashMovementUpload",
                $"upload wake-up failed error={ex.GetType().Name} message={ex.Message}",
                exception: ex);
        }
    }
}
