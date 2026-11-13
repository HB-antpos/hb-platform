using Hbpos.Contracts.CashMovements;
using Hbpos.Contracts.DailyClose;

namespace Hbpos.Api.Services;

public interface ICashMovementSyncService
{
    Task<CashMovementSyncResponse> SyncAsync(
        CashMovementSyncRequest request,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken);
}

/// <summary>
/// 收银端现金进出批量上传：请求级问题（版本、客户端类型、批量大小）整体 400；
/// 单条记录问题只拒这一条（Rejected，客户端不再重试），其余照常入库。按批内顺序处理，
/// 同批里先出现的原记录可被后面的冲正记录引用。
/// </summary>
internal sealed class CashMovementSyncService(
    ICashMovementRepository repository,
    TimeProvider? timeProvider = null) : ICashMovementSyncService
{
    private const int MaximumCashierIdLength = 64;
    private const int MaximumCashierNameLength = 128;
    private const int MaximumAppVersionLength = 64;
    private const int MaximumDisplayNumberLength = 32;
    private const decimal MaximumAmount = 1_000_000m;

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<CashMovementSyncResponse> SyncAsync(
        CashMovementSyncRequest request,
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SchemaVersion != CashMovementContractConstants.SchemaVersion)
        {
            throw new DailyCloseValidationException("UNSUPPORTED_SCHEMA_VERSION", "schemaVersion must be 1.");
        }

        var clientKind = DailyCloseContractConstants.ClientKinds.FirstOrDefault(kind =>
                string.Equals(kind, request.ClientKind?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new DailyCloseValidationException("INVALID_CLIENT_KIND", "clientKind must be Wpf, Handheld or Ipad.");

        if (request.Movements is null || request.Movements.Count == 0 ||
            request.Movements.Count > CashMovementContractConstants.MaximumBatchSize)
        {
            throw new DailyCloseValidationException(
                "INVALID_BATCH_SIZE",
                $"movements must contain 1 to {CashMovementContractConstants.MaximumBatchSize} entries.");
        }

        var appVersion = Trimmed(request.AppVersion, MaximumAppVersionLength);
        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var results = new List<CashMovementSyncResult>(request.Movements.Count);
        foreach (var movement in request.Movements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await SyncOneAsync(movement, storeCode, deviceCode, clientKind, appVersion, nowUtc, cancellationToken));
        }

        return new CashMovementSyncResponse(results);
    }

    private async Task<CashMovementSyncResult> SyncOneAsync(
        CashMovementSync? movement,
        string storeCode,
        string deviceCode,
        string clientKind,
        string? appVersion,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (movement is null || movement.MovementGuid == Guid.Empty)
        {
            return Rejected(Guid.Empty, "MOVEMENT_GUID_REQUIRED", "movementGuid is required.");
        }

        PosmCashMovementRecord record;
        try
        {
            record = ValidateAndNormalize(movement, storeCode, deviceCode, clientKind, appVersion, nowUtc);
        }
        catch (DailyCloseValidationException ex)
        {
            return Rejected(movement.MovementGuid, ex.Code, ex.Message);
        }

        // 幂等：同一 Guid 已存在时只看内容是否一致，不再重复校验冲正关系。
        var existing = await repository.GetByGuidAsync(record.MovementGuid, cancellationToken);
        if (existing is not null)
        {
            return CompareExisting(existing, record);
        }

        if (record.VoidsMovementGuid is { } voidsGuid)
        {
            var original = await repository.GetByGuidAsync(voidsGuid, cancellationToken);
            if (original is null)
            {
                // 原记录必须先到服务端；客户端按发生顺序上传，正常情况下原记录总在冲正之前。
                return Rejected(record.MovementGuid, "VOIDED_MOVEMENT_NOT_FOUND", "The voided movement has not been uploaded.");
            }

            if (VoidMismatch(original, record) is { } mismatch)
            {
                return Rejected(record.MovementGuid, "INVALID_VOID", mismatch);
            }
        }

        var outcome = await repository.TryInsertAsync(record, cancellationToken);
        switch (outcome)
        {
            case CashMovementInsertOutcome.Inserted:
                return new CashMovementSyncResult(record.MovementGuid, CashMovementSyncStatuses.Accepted, null, null);
            case CashMovementInsertOutcome.AlreadyVoided:
                return Rejected(record.MovementGuid, "ALREADY_VOIDED", "The movement has already been voided by another movement.");
            default:
                // 并发重复上传：别的请求刚插入同一个 Guid，重读后按已存在处理。
                var raced = await repository.GetByGuidAsync(record.MovementGuid, cancellationToken);
                return raced is null
                    ? Rejected(record.MovementGuid, "CASH_MOVEMENT_CONCURRENT_UPDATE", "Retry the upload.")
                    : CompareExisting(raced, record);
        }
    }

    private static PosmCashMovementRecord ValidateAndNormalize(
        CashMovementSync movement,
        string storeCode,
        string deviceCode,
        string clientKind,
        string? appVersion,
        DateTime nowUtc)
    {
        var reasonCode = Canonical(
            movement.ReasonCode,
            CashMovementContractConstants.NaturalDirections.Keys,
            "INVALID_REASON_CODE",
            "reasonCode must be ChangeTopUp, OtherIn, SafeDrop or PaidOut.");
        var direction = Canonical(
            movement.Direction,
            [CashMovementContractConstants.DirectionIn, CashMovementContractConstants.DirectionOut],
            "INVALID_DIRECTION",
            "direction must be In or Out.");

        // 普通记录方向 = 原因码的自然方向；冲正记录方向相反（存入的冲正是取出，反之亦然）。
        var natural = CashMovementContractConstants.NaturalDirections[reasonCode];
        var isVoid = movement.VoidsMovementGuid is not null;
        if (isVoid && movement.VoidsMovementGuid == Guid.Empty)
        {
            throw new DailyCloseValidationException("INVALID_VOID", "voidsMovementGuid cannot be empty.");
        }

        if (isVoid && movement.VoidsMovementGuid == movement.MovementGuid)
        {
            throw new DailyCloseValidationException("INVALID_VOID", "A movement cannot void itself.");
        }

        if (string.Equals(direction, natural, StringComparison.Ordinal) == isVoid)
        {
            throw new DailyCloseValidationException(
                "DIRECTION_REASON_MISMATCH",
                "direction must match the reason's natural direction, or be opposite for a void.");
        }

        var amount = decimal.Round(movement.Amount, 2, MidpointRounding.AwayFromZero);
        if (amount <= 0 || amount > MaximumAmount || amount != movement.Amount)
        {
            throw new DailyCloseValidationException("INVALID_AMOUNT", "amount must be a positive value with at most 2 decimals.");
        }

        var note = Trimmed(movement.Note, CashMovementContractConstants.MaximumNoteLength, "NOTE_TOO_LONG");
        if (!isVoid &&
            CashMovementContractConstants.NoteRequiredReasons.Contains(reasonCode) &&
            (note is null || note.Length < CashMovementContractConstants.MinimumRequiredNoteLength))
        {
            throw new DailyCloseValidationException(
                "NOTE_REQUIRED",
                $"note must be at least {CashMovementContractConstants.MinimumRequiredNoteLength} characters for {reasonCode}.");
        }

        if (movement.BusinessDate == default || movement.OccurredAt == default)
        {
            throw new DailyCloseValidationException("TIME_REQUIRED", "businessDate and occurredAt are required.");
        }

        // 与日结同口径：营业日最多比发生时刻的 UTC 日期晚一天（澳洲东部早间）；发生时刻不能明显晚于服务器时间。
        var occurredUtc = movement.OccurredAt.UtcDateTime;
        var occurredUtcDate = DateOnly.FromDateTime(occurredUtc);
        if (movement.BusinessDate > occurredUtcDate.AddDays(1) || movement.BusinessDate < occurredUtcDate.AddDays(-1))
        {
            throw new DailyCloseValidationException("INVALID_BUSINESS_DATE", "businessDate does not match occurredAt.");
        }

        if (occurredUtc > nowUtc.AddDays(1))
        {
            throw new DailyCloseValidationException("INVALID_OCCURRED_AT", "occurredAt is in the future.");
        }

        return new PosmCashMovementRecord
        {
            MovementGuid = movement.MovementGuid,
            StoreCode = storeCode.Trim(),
            DeviceCode = deviceCode.Trim(),
            ClientKind = clientKind,
            Direction = direction,
            ReasonCode = reasonCode,
            Amount = amount,
            Note = note,
            VoidsMovementGuid = movement.VoidsMovementGuid,
            BusinessDate = movement.BusinessDate.ToDateTime(TimeOnly.MinValue),
            OccurredAtUtc = occurredUtc,
            CashierId = Trimmed(movement.CashierId, MaximumCashierIdLength, "CASHIER_ID_TOO_LONG") ?? string.Empty,
            CashierName = Trimmed(movement.CashierName, MaximumCashierNameLength, "CASHIER_NAME_TOO_LONG") ?? string.Empty,
            AuthorizedById = Trimmed(movement.AuthorizedById, MaximumCashierIdLength, "CASHIER_ID_TOO_LONG"),
            AuthorizedByName = Trimmed(movement.AuthorizedByName, MaximumCashierNameLength, "CASHIER_NAME_TOO_LONG"),
            DisplayNumber = Trimmed(movement.DisplayNumber, MaximumDisplayNumberLength, "DISPLAY_NUMBER_TOO_LONG"),
            AppVersion = appVersion,
            ReceivedAtUtc = nowUtc
        };
    }

    /// <summary>冲正必须：同一设备、同一原因码、金额相同、方向相反，且原记录本身不是冲正。</summary>
    private static string? VoidMismatch(PosmCashMovementRecord original, PosmCashMovementRecord reversal)
    {
        if (!SameCode(original.StoreCode, reversal.StoreCode) || !SameCode(original.DeviceCode, reversal.DeviceCode))
        {
            return "The voided movement belongs to another store or device.";
        }

        if (original.VoidsMovementGuid is not null)
        {
            return "A void movement cannot be voided.";
        }

        if (!string.Equals(original.ReasonCode, reversal.ReasonCode, StringComparison.Ordinal) ||
            original.Amount != reversal.Amount ||
            string.Equals(original.Direction, reversal.Direction, StringComparison.Ordinal))
        {
            return "A void must have the same reason and amount and the opposite direction.";
        }

        return null;
    }

    /// <summary>同 Guid 再次上传：同设备且业务内容一致视为幂等重放；ReceivedAt、AppVersion 不参与比较。</summary>
    private static CashMovementSyncResult CompareExisting(PosmCashMovementRecord existing, PosmCashMovementRecord incoming)
    {
        if (!SameCode(existing.StoreCode, incoming.StoreCode) || !SameCode(existing.DeviceCode, incoming.DeviceCode))
        {
            return Rejected(incoming.MovementGuid, "CASH_MOVEMENT_SCOPE_CONFLICT", "The movement already belongs to another store or device.");
        }

        var same = string.Equals(existing.Direction, incoming.Direction, StringComparison.Ordinal) &&
                   string.Equals(existing.ReasonCode, incoming.ReasonCode, StringComparison.Ordinal) &&
                   existing.Amount == incoming.Amount &&
                   string.Equals(existing.Note, incoming.Note, StringComparison.Ordinal) &&
                   existing.VoidsMovementGuid == incoming.VoidsMovementGuid &&
                   existing.BusinessDate.Date == incoming.BusinessDate.Date &&
                   existing.OccurredAtUtc.Ticks == incoming.OccurredAtUtc.Ticks &&
                   string.Equals(existing.CashierId, incoming.CashierId, StringComparison.Ordinal);
        return same
            ? new CashMovementSyncResult(incoming.MovementGuid, CashMovementSyncStatuses.AlreadySynced, null, null)
            : Rejected(incoming.MovementGuid, "CASH_MOVEMENT_CONTENT_CONFLICT", "The same movement was uploaded earlier with different content.");
    }

    private static CashMovementSyncResult Rejected(Guid guid, string code, string message) =>
        new(guid, CashMovementSyncStatuses.Rejected, code, message);

    private static bool SameCode(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string Canonical(string? value, IEnumerable<string> allowed, string code, string message)
    {
        var trimmed = value?.Trim();
        return allowed.FirstOrDefault(candidate => string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? throw new DailyCloseValidationException(code, message);
    }

    private static string? Trimmed(string? value, int maxLength, string code = "VALUE_TOO_LONG")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : throw new DailyCloseValidationException(code, $"Value exceeds {maxLength} characters.");
    }
}
