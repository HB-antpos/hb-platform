using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.StoreCash;

// 期初与盘点：期初是现金池的起点（每店一条有效）；盘点是某天结束时店里实际现金，与现金池余额比对。
public sealed partial class StoreCashService
{
    private CashBalanceEntryDto MapEntry(StoreCashBalanceEntry entry, CashAccess access) =>
        new()
        {
            EntryGuid = entry.EntryGuid,
            StoreCode = entry.StoreCode,
            EntryType = entry.EntryType,
            EntryDate = StoreCashClock.FromColumn(entry.EntryDate),
            Amount = entry.Amount,
            ExpectedAmount = entry.ExpectedAmount,
            Difference = entry.ExpectedAmount.HasValue ? decimal.Round(entry.Amount - entry.ExpectedAmount.Value, 2) : null,
            Note = entry.Note,
            Status = entry.Status,
            CreatedByName = entry.CreatedByName,
            CreatedAtUtc = StoreCashClock.AsUtc(entry.CreatedAtUtc),
            CanVoid = entry.Status == StoreCashConstants.RecordStatus.Active
                && CashVisibilityRules.CanVoid(access, access.CanCreateDeposit, entry.CreatedByUserGuid, entry.CreatedAtUtc, Now),
        };

    public async Task<ApiResponse<List<CashBalanceEntryDto>>> ListEntriesAsync(
        CashAccess access,
        string? storeCode,
        bool includeVoided,
        CancellationToken cancellationToken
    )
    {
        if (!access.CanViewOverview)
        {
            return Fail<List<CashBalanceEntryDto>>("无权查看现金管理", StoreCashConstants.ErrorCodes.StoreForbidden);
        }

        var lookup = await LookupStoreAsync(access, storeCode, cancellationToken);
        if (lookup.Store is null)
        {
            return Fail<List<CashBalanceEntryDto>>(lookup);
        }

        var code = lookup.Store.StoreCode;
        var rows = await _db.Queryable<StoreCashBalanceEntry>()
            .Where(item => item.StoreCode == code)
            .WhereIF(!includeVoided, item => item.Status == StoreCashConstants.RecordStatus.Active)
            .OrderBy(item => item.EntryDate, OrderByType.Desc)
            .OrderBy(item => item.CreatedAtUtc, OrderByType.Desc)
            .Take(200)
            .ToListAsync(cancellationToken);
        return ApiResponse<List<CashBalanceEntryDto>>.OK(rows.Select(row => MapEntry(row, access)).ToList());
    }

    public async Task<ApiResponse<CashBalanceEntryDto>> SetOpeningAsync(
        CashAccess access,
        SetCashOpeningRequest request,
        CancellationToken cancellationToken
    )
    {
        var prepared = await PrepareEntryAsync(
            access,
            request.ClientRequestId,
            request.StoreCode,
            request.EntryDate,
            request.Amount,
            allowZero: true,
            cancellationToken
        );
        if (prepared.Error is not null)
        {
            return prepared.Error;
        }

        if (prepared.Replay is not null)
        {
            return ApiResponse<CashBalanceEntryDto>.OK(MapEntry(prepared.Replay, access));
        }

        var store = prepared.Store!;
        var code = store.StoreCode;
        var opening = await GetActiveOpeningAsync(code, cancellationToken);
        if (opening is not null)
        {
            return Fail<CashBalanceEntryDto>(
                "该分店已有期初现金，如需修改请先作废原记录",
                StoreCashConstants.ErrorCodes.OpeningExists
            );
        }

        var entry = NewEntry(
            access,
            code,
            StoreCashConstants.BalanceEntryType.Opening,
            request.EntryDate,
            request.Amount,
            null,
            request.Note,
            prepared.ClientRequestId!
        );
        try
        {
            await _db.Insertable(entry).ExecuteCommandAsync(cancellationToken);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            // 唯一索引兜底：并发的另一次期初录入先到了，或同一请求号已落库。
            var concurrent = await FindEntryByRequestAsync(prepared.ClientRequestId!, cancellationToken);
            if (concurrent is not null
                && concurrent.CreatedByUserGuid == access.UserGuid
                && concurrent.StoreCode == code)
            {
                return ApiResponse<CashBalanceEntryDto>.OK(MapEntry(concurrent, access));
            }

            return Fail<CashBalanceEntryDto>(
                "该分店已有期初现金，如需修改请先作废原记录",
                StoreCashConstants.ErrorCodes.OpeningExists
            );
        }

        return ApiResponse<CashBalanceEntryDto>.OK(MapEntry(entry, access));
    }

    public async Task<ApiResponse<CashBalanceEntryDto>> CreateCountAsync(
        CashAccess access,
        CreateCashCountRequest request,
        CancellationToken cancellationToken
    )
    {
        var prepared = await PrepareEntryAsync(
            access,
            request.ClientRequestId,
            request.StoreCode,
            request.EntryDate,
            request.Amount,
            allowZero: true,
            cancellationToken
        );
        if (prepared.Error is not null)
        {
            return prepared.Error;
        }

        if (prepared.Replay is not null)
        {
            return ApiResponse<CashBalanceEntryDto>.OK(MapEntry(prepared.Replay, access));
        }

        var store = prepared.Store!;
        var code = store.StoreCode;

        // 盘点留存当时按现金池算出的应有余额：之后日结、存款、支出再变动，这次盘点的差异依据也不会漂移。
        decimal? expected = null;
        var opening = await GetActiveOpeningAsync(code, cancellationToken);
        if (opening is not null && request.EntryDate >= StoreCashClock.FromColumn(opening.EntryDate))
        {
            var snapshot = await BuildPoolAsync(store, request.EntryDate, cancellationToken);
            expected = snapshot.PoolBalance;
        }

        var entry = NewEntry(
            access,
            code,
            StoreCashConstants.BalanceEntryType.Count,
            request.EntryDate,
            request.Amount,
            expected,
            request.Note,
            prepared.ClientRequestId!
        );
        try
        {
            await _db.Insertable(entry).ExecuteCommandAsync(cancellationToken);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            var concurrent = await FindEntryByRequestAsync(prepared.ClientRequestId!, cancellationToken);
            if (concurrent is not null
                && concurrent.CreatedByUserGuid == access.UserGuid
                && concurrent.StoreCode == code)
            {
                return ApiResponse<CashBalanceEntryDto>.OK(MapEntry(concurrent, access));
            }

            return Fail<CashBalanceEntryDto>("请求号已被其他记录使用", StoreCashConstants.ErrorCodes.Conflict);
        }

        return ApiResponse<CashBalanceEntryDto>.OK(MapEntry(entry, access));
    }

    public async Task<ApiResponse<CashBalanceEntryDto>> VoidEntryAsync(
        CashAccess access,
        string entryGuid,
        CashVoidRequest request,
        CancellationToken cancellationToken
    )
    {
        var entry = await _db.Queryable<StoreCashBalanceEntry>()
            .Where(item => item.EntryGuid == entryGuid)
            .FirstAsync(cancellationToken);
        if (entry is null || !access.CanAccessStore(entry.StoreCode))
        {
            return Fail<CashBalanceEntryDto>("记录不存在", StoreCashConstants.ErrorCodes.RecordNotFound);
        }

        if (entry.Status == StoreCashConstants.RecordStatus.Voided)
        {
            return ApiResponse<CashBalanceEntryDto>.OK(MapEntry(entry, access));
        }

        if (!CashVisibilityRules.CanVoid(access, access.CanCreateDeposit, entry.CreatedByUserGuid, entry.CreatedAtUtc, Now))
        {
            return Fail<CashBalanceEntryDto>(
                $"无权作废该记录（本人录入 {StoreCashConstants.ManagerSelfVoidHours} 小时内可作废，其余需要作废权限）",
                StoreCashConstants.ErrorCodes.VoidNotAllowed
            );
        }

        var reason = NormalizeText(request.Reason, MaxReasonLength);
        if (reason is null || reason.Length < MinReasonLength)
        {
            return Fail<CashBalanceEntryDto>("请填写作废原因", StoreCashConstants.ErrorCodes.InvalidRequest);
        }

        var now = Now.UtcDateTime;
        var guid = entry.EntryGuid;
        var updated = await _db.Updateable<StoreCashBalanceEntry>()
            .SetColumns(item => new StoreCashBalanceEntry
            {
                Status = StoreCashConstants.RecordStatus.Voided,
                VoidReason = reason,
                VoidedAtUtc = now,
                VoidedByUserGuid = access.UserGuid,
                VoidedByName = access.UserName,
            })
            .Where(item => item.EntryGuid == guid && item.Status == StoreCashConstants.RecordStatus.Active)
            .ExecuteCommandAsync(cancellationToken);
        if (updated == 1)
        {
            entry.Status = StoreCashConstants.RecordStatus.Voided;
            entry.VoidReason = reason;
            entry.VoidedAtUtc = now;
            entry.VoidedByUserGuid = access.UserGuid;
            entry.VoidedByName = access.UserName;
        }
        else
        {
            entry = await _db.Queryable<StoreCashBalanceEntry>()
                .Where(item => item.EntryGuid == guid)
                .FirstAsync(cancellationToken) ?? entry;
        }

        return ApiResponse<CashBalanceEntryDto>.OK(MapEntry(entry, access));
    }

    // ───────────────────────── 公共部分 ─────────────────────────

    private sealed record PreparedEntry(
        Store? Store,
        string? ClientRequestId,
        StoreCashBalanceEntry? Replay,
        ApiResponse<CashBalanceEntryDto>? Error
    );

    /// <summary>期初与盘点共用的前置校验：权限、请求号、分店范围、幂等重放、日期与金额。</summary>
    private async Task<PreparedEntry> PrepareEntryAsync(
        CashAccess access,
        string? clientRequestIdRaw,
        string? storeCode,
        DateOnly entryDate,
        decimal amount,
        bool allowZero,
        CancellationToken cancellationToken
    )
    {
        if (!access.CanCreateDeposit)
        {
            return new PreparedEntry(
                null,
                null,
                null,
                Fail<CashBalanceEntryDto>("无权录入期初与盘点", StoreCashConstants.ErrorCodes.StoreForbidden)
            );
        }

        var clientRequestId = clientRequestIdRaw?.Trim();
        if (string.IsNullOrEmpty(clientRequestId) || clientRequestId.Length > MaxClientRequestIdLength)
        {
            return new PreparedEntry(
                null,
                null,
                null,
                Fail<CashBalanceEntryDto>("请求号无效", StoreCashConstants.ErrorCodes.InvalidRequest)
            );
        }

        var lookup = await LookupStoreAsync(access, storeCode, cancellationToken);
        if (lookup.Store is null)
        {
            return new PreparedEntry(null, null, null, Fail<CashBalanceEntryDto>(lookup));
        }

        var existing = await FindEntryByRequestAsync(clientRequestId, cancellationToken);
        if (existing is not null)
        {
            return string.Equals(existing.CreatedByUserGuid, access.UserGuid, StringComparison.Ordinal)
                && string.Equals(existing.StoreCode, lookup.Store.StoreCode, StringComparison.Ordinal)
                ? new PreparedEntry(lookup.Store, clientRequestId, existing, null)
                : new PreparedEntry(
                    null,
                    null,
                    null,
                    Fail<CashBalanceEntryDto>("请求号已被其他记录使用", StoreCashConstants.ErrorCodes.Conflict)
                );
        }

        var amountValid = amount <= StoreCashConstants.MaxAmount
            && decimal.Round(amount, 2) == amount
            && (allowZero ? amount >= 0 : amount > 0);
        if (!amountValid)
        {
            return new PreparedEntry(
                null,
                null,
                null,
                Fail<CashBalanceEntryDto>("金额不能为负，且最多两位小数", StoreCashConstants.ErrorCodes.InvalidRequest)
            );
        }

        var today = StoreToday(lookup.Store);
        if (!CashVisibilityRules.IsEntryDateAllowed(access, entryDate, today))
        {
            return new PreparedEntry(
                null,
                null,
                null,
                Fail<CashBalanceEntryDto>(
                    $"日期不能晚于今天，且最多回溯 {StoreCashConstants.ManagerMaxBackfillDays} 天",
                    StoreCashConstants.ErrorCodes.DateOutOfRange
                )
            );
        }

        return new PreparedEntry(lookup.Store, clientRequestId, null, null);
    }

    private async Task<StoreCashBalanceEntry?> FindEntryByRequestAsync(
        string clientRequestId,
        CancellationToken cancellationToken
    ) =>
        await _db.Queryable<StoreCashBalanceEntry>()
            .Where(item => item.ClientRequestId == clientRequestId)
            .FirstAsync(cancellationToken);

    private StoreCashBalanceEntry NewEntry(
        CashAccess access,
        string storeCode,
        string entryType,
        DateOnly entryDate,
        decimal amount,
        decimal? expected,
        string? note,
        string clientRequestId
    ) =>
        new()
        {
            StoreCode = storeCode,
            EntryType = entryType,
            EntryDate = StoreCashClock.ToColumn(entryDate),
            Amount = amount,
            ExpectedAmount = expected,
            Note = NormalizeText(note, MaxNoteLength),
            Status = StoreCashConstants.RecordStatus.Active,
            ClientRequestId = clientRequestId,
            CreatedByUserGuid = access.UserGuid,
            CreatedByName = access.UserName,
            CreatedAtUtc = Now.UtcDateTime,
        };
}
