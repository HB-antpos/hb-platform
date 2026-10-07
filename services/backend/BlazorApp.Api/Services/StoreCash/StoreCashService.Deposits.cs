using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.StoreCash;

// 存银行：一次登记 = 若干张存单（金额 + 照片）。多日现金可以混存，覆盖营业日只是说明，不参与金额计算。
public sealed partial class StoreCashService
{
    private const int MaxSlipNoLength = 100;
    private const int MaxListLimit = 200;
    private const int DefaultListLimit = 50;

    /// <summary>事务内发现附件被并发占用等冲突时抛出，统一回滚后转成 409。</summary>
    private sealed class CashConflictException(string message) : Exception(message);

    public async Task<ApiResponse<CashAttachmentUploadSignatureDto>> CreateAttachmentUploadAsync(
        CashAccess access,
        CashAttachmentUploadRequest request,
        CancellationToken cancellationToken
    )
    {
        if (!access.CanCreateDeposit && !access.CanCreateExpense)
        {
            return Fail<CashAttachmentUploadSignatureDto>("无权上传现金凭证", StoreCashConstants.ErrorCodes.StoreForbidden);
        }

        var lookup = await LookupStoreAsync(access, request.StoreCode, cancellationToken);
        if (lookup.Store is null)
        {
            return Fail<CashAttachmentUploadSignatureDto>(lookup);
        }

        return await _attachments.CreateUploadSignatureAsync(
            lookup.Store.StoreCode,
            access.UserGuid,
            request.ContentType,
            request.FileSize,
            cancellationToken
        );
    }

    public async Task<ApiResponse<CashDepositDetailDto>> CreateDepositAsync(
        CashAccess access,
        CreateCashDepositRequest request,
        CancellationToken cancellationToken
    )
    {
        if (!access.CanCreateDeposit)
        {
            return Fail<CashDepositDetailDto>("无权登记存款", StoreCashConstants.ErrorCodes.StoreForbidden);
        }

        var clientRequestId = request.ClientRequestId?.Trim();
        if (string.IsNullOrEmpty(clientRequestId) || clientRequestId.Length > MaxClientRequestIdLength)
        {
            return Fail<CashDepositDetailDto>("请求号无效", StoreCashConstants.ErrorCodes.InvalidRequest);
        }

        var lookup = await LookupStoreAsync(access, request.StoreCode, cancellationToken);
        if (lookup.Store is null)
        {
            return Fail<CashDepositDetailDto>(lookup);
        }

        var store = lookup.Store;
        var code = store.StoreCode;

        // 幂等：弱网重试同一次提交，直接返回已落库的那条，不再校验日期窗口（窗口可能已随时间滑动）。
        var existing = await FindDepositByRequestAsync(clientRequestId, cancellationToken);
        if (existing is not null)
        {
            return await ReplayDepositAsync(existing, access, code, cancellationToken);
        }

        var today = StoreToday(store);
        var validation = ValidateDepositRequest(access, request, today);
        if (validation is not null)
        {
            return validation;
        }

        var slipInputs = request.Slips;
        var total = slipInputs.Sum(slip => slip.Amount);
        if (total > StoreCashConstants.MaxAmount)
        {
            return Fail<CashDepositDetailDto>("存款合计金额过大", StoreCashConstants.ErrorCodes.InvalidRequest);
        }

        var overrideReason = NormalizeText(request.OverrideReason, MaxReasonLength);
        var snapshot = await BuildPoolAsync(store, today, cancellationToken);
        if (snapshot.PoolBalance is { } balance)
        {
            // 备用金已在点钱前取出，所以正常情况下存款 = 现金池余额；差异过大必须说明原因。
            var expected = Math.Max(balance, 0m);
            if (Math.Abs(total - expected) > StoreCashConstants.DepositDifferenceReasonThreshold
                && (overrideReason is null || overrideReason.Length < MinReasonLength))
            {
                return Fail<CashDepositDetailDto>(
                    $"存款合计与现金池余额相差超过 {StoreCashConstants.DepositDifferenceReasonThreshold:0.##}，请填写差异原因",
                    StoreCashConstants.ErrorCodes.OverrideReasonRequired
                );
            }
        }

        var attachmentGuids = slipInputs.SelectMany(slip => slip.AttachmentGuids).ToList();
        var prepared = await _attachments.PrepareAsync(code, access.UserGuid, attachmentGuids, cancellationToken);
        if (!prepared.Success)
        {
            return Fail<CashDepositDetailDto>(prepared.Message, prepared.ErrorCode ?? StoreCashConstants.ErrorCodes.AttachmentInvalid);
        }

        var now = Now.UtcDateTime;
        var deposit = new StoreCashDeposit
        {
            StoreCode = code,
            DepositDate = StoreCashClock.ToColumn(request.DepositDate),
            CoveredFromDate = request.CoveredFromDate.HasValue ? StoreCashClock.ToColumn(request.CoveredFromDate.Value) : null,
            CoveredToDate = request.CoveredToDate.HasValue ? StoreCashClock.ToColumn(request.CoveredToDate.Value) : null,
            TotalAmount = decimal.Round(total, 2),
            Note = NormalizeText(request.Note, MaxNoteLength),
            OverrideReason = overrideReason,
            Status = StoreCashConstants.RecordStatus.Active,
            ClientRequestId = clientRequestId,
            CreatedByUserGuid = access.UserGuid,
            CreatedByName = access.UserName,
            CreatedAtUtc = now,
        };
        var slips = slipInputs
            .Select((input, index) => new StoreCashDepositSlip
            {
                DepositGuid = deposit.DepositGuid,
                StoreCode = code,
                Amount = input.Amount,
                SlipNo = NormalizeText(input.SlipNo, MaxSlipNoLength),
                SortOrder = index,
            })
            .ToList();

        await _db.Ado.BeginTranAsync();
        try
        {
            await _db.Insertable(deposit).ExecuteCommandAsync(cancellationToken);
            await _db.Insertable(slips).ExecuteCommandAsync(cancellationToken);
            for (var index = 0; index < slips.Count; index++)
            {
                await LinkAttachmentsAsync(
                    StoreCashConstants.AttachmentOwner.Slip,
                    slips[index].SlipGuid,
                    slipInputs[index].AttachmentGuids,
                    now,
                    cancellationToken
                );
            }

            await _db.Ado.CommitTranAsync();
        }
        catch (Exception ex)
        {
            await _db.Ado.RollbackTranAsync();
            if (ex is CashConflictException)
            {
                return Fail<CashDepositDetailDto>("图片状态已变更，请重新提交", StoreCashConstants.ErrorCodes.Conflict);
            }

            if (IsUniqueViolation(ex))
            {
                // 同一请求号的另一次提交刚好先落库了：按幂等返回已有记录。
                var concurrent = await FindDepositByRequestAsync(clientRequestId, cancellationToken);
                if (concurrent is not null)
                {
                    return await ReplayDepositAsync(concurrent, access, code, cancellationToken);
                }
            }

            throw;
        }

        return ApiResponse<CashDepositDetailDto>.OK(await BuildDepositDetailAsync(deposit, access, cancellationToken));
    }

    private async Task<StoreCashDeposit?> FindDepositByRequestAsync(
        string clientRequestId,
        CancellationToken cancellationToken
    ) =>
        await _db.Queryable<StoreCashDeposit>()
            .Where(item => item.ClientRequestId == clientRequestId)
            .FirstAsync(cancellationToken);

    private async Task<ApiResponse<CashDepositDetailDto>> ReplayDepositAsync(
        StoreCashDeposit existing,
        CashAccess access,
        string storeCode,
        CancellationToken cancellationToken
    )
    {
        if (!string.Equals(existing.CreatedByUserGuid, access.UserGuid, StringComparison.Ordinal)
            || !string.Equals(existing.StoreCode, storeCode, StringComparison.Ordinal))
        {
            return Fail<CashDepositDetailDto>("请求号已被其他记录使用", StoreCashConstants.ErrorCodes.Conflict);
        }

        return ApiResponse<CashDepositDetailDto>.OK(await BuildDepositDetailAsync(existing, access, cancellationToken));
    }

    private ApiResponse<CashDepositDetailDto>? ValidateDepositRequest(
        CashAccess access,
        CreateCashDepositRequest request,
        DateOnly today
    )
    {
        if (!CashVisibilityRules.IsEntryDateAllowed(access, request.DepositDate, today))
        {
            return Fail<CashDepositDetailDto>(
                $"存款日期不能晚于今天，且最多回溯 {StoreCashConstants.ManagerMaxBackfillDays} 天",
                StoreCashConstants.ErrorCodes.DateOutOfRange
            );
        }

        if (request.CoveredFromDate.HasValue != request.CoveredToDate.HasValue)
        {
            return Fail<CashDepositDetailDto>("覆盖营业日需同时填写起止日期", StoreCashConstants.ErrorCodes.InvalidRequest);
        }

        if (request.CoveredFromDate.HasValue
            && (request.CoveredFromDate > request.CoveredToDate
                || request.CoveredToDate > request.DepositDate
                || request.DepositDate.DayNumber - request.CoveredFromDate.Value.DayNumber > 366))
        {
            return Fail<CashDepositDetailDto>("覆盖营业日范围无效", StoreCashConstants.ErrorCodes.DateOutOfRange);
        }

        var slips = request.Slips ?? new List<CashDepositSlipInput>();
        if (slips.Count == 0 || slips.Count > StoreCashConstants.MaxSlipsPerDeposit)
        {
            return Fail<CashDepositDetailDto>(
                $"存单数量须在 1 到 {StoreCashConstants.MaxSlipsPerDeposit} 张之间",
                StoreCashConstants.ErrorCodes.InvalidRequest
            );
        }

        foreach (var slip in slips)
        {
            if (!IsTwoDecimalPositiveAmount(slip.Amount))
            {
                return Fail<CashDepositDetailDto>("存单金额须大于 0，且最多两位小数", StoreCashConstants.ErrorCodes.InvalidRequest);
            }

            var guids = slip.AttachmentGuids ?? new List<string>();
            if (guids.Count == 0)
            {
                return Fail<CashDepositDetailDto>("每张存单至少要有一张照片", StoreCashConstants.ErrorCodes.AttachmentRequired);
            }

            if (guids.Count > StoreCashConstants.MaxImagesPerSlip || guids.Any(string.IsNullOrWhiteSpace))
            {
                return Fail<CashDepositDetailDto>(
                    $"每张存单最多 {StoreCashConstants.MaxImagesPerSlip} 张照片",
                    StoreCashConstants.ErrorCodes.InvalidRequest
                );
            }
        }

        request.Slips = slips;
        return null;
    }

    /// <summary>把已转正的附件挂到存单或支出上：CAS 更新，任何一张被并发占用就整笔回滚。</summary>
    private async Task LinkAttachmentsAsync(
        string ownerType,
        string ownerGuid,
        IReadOnlyList<string> attachmentGuids,
        DateTime now,
        CancellationToken cancellationToken
    )
    {
        for (var order = 0; order < attachmentGuids.Count; order++)
        {
            var attachmentGuid = attachmentGuids[order];
            var sortOrder = order;
            var linked = await _db.Updateable<StoreCashAttachment>()
                .SetColumns(item => new StoreCashAttachment
                {
                    Status = StoreCashConstants.AttachmentStatus.Linked,
                    OwnerType = ownerType,
                    OwnerGuid = ownerGuid,
                    SortOrder = sortOrder,
                    LinkedAtUtc = now,
                })
                .Where(item => item.AttachmentGuid == attachmentGuid
                    && item.Status == StoreCashConstants.AttachmentStatus.Promoted
                    && item.OwnerGuid == null)
                .ExecuteCommandAsync(cancellationToken);
            if (linked != 1)
            {
                throw new CashConflictException($"附件 {attachmentGuid} 无法挂到记录上");
            }
        }
    }

    // ───────────────────────── 查询 ─────────────────────────

    private static (int Limit, int Offset) NormalizePaging(int limit, int offset) =>
        (limit <= 0 ? DefaultListLimit : Math.Min(limit, MaxListLimit), Math.Max(offset, 0));

    public async Task<ApiResponse<CashPagedDto<CashDepositListItemDto>>> ListDepositsAsync(
        CashAccess access,
        string? storeCode,
        DateOnly? from,
        DateOnly? to,
        bool includeVoided,
        int limit,
        int offset,
        CancellationToken cancellationToken
    )
    {
        if (!access.CanViewOverview)
        {
            return Fail<CashPagedDto<CashDepositListItemDto>>("无权查看现金管理", StoreCashConstants.ErrorCodes.StoreForbidden);
        }

        var lookup = await LookupStoreAsync(access, storeCode, cancellationToken);
        if (lookup.Store is null)
        {
            return Fail<CashPagedDto<CashDepositListItemDto>>(lookup);
        }

        var code = lookup.Store.StoreCode;
        var fromColumn = from.HasValue ? StoreCashClock.ToColumn(from.Value) : DateTime.MinValue;
        var toColumn = to.HasValue ? StoreCashClock.ToColumn(to.Value) : DateTime.MaxValue;
        var (take, skip) = NormalizePaging(limit, offset);

        var query = _db.Queryable<StoreCashDeposit>()
            .Where(item => item.StoreCode == code && item.DepositDate >= fromColumn && item.DepositDate <= toColumn)
            .WhereIF(!includeVoided, item => item.Status == StoreCashConstants.RecordStatus.Active);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderBy(item => item.DepositDate, OrderByType.Desc)
            .OrderBy(item => item.CreatedAtUtc, OrderByType.Desc)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        var depositGuids = rows.Select(item => item.DepositGuid).ToList();
        var slips = depositGuids.Count == 0
            ? new List<StoreCashDepositSlip>()
            : await _db.Queryable<StoreCashDepositSlip>()
                .Where(item => depositGuids.Contains(item.DepositGuid))
                .ToListAsync(cancellationToken);
        var imageCounts = await _attachments.CountLinkedAsync(
            slips.Select(item => item.SlipGuid).ToList(),
            cancellationToken
        );
        var slipsByDeposit = slips.ToLookup(item => item.DepositGuid);

        return ApiResponse<CashPagedDto<CashDepositListItemDto>>.OK(
            new CashPagedDto<CashDepositListItemDto>
            {
                Total = total,
                Items = rows
                    .Select(row =>
                    {
                        var depositSlips = slipsByDeposit[row.DepositGuid].OrderBy(slip => slip.SortOrder).ToList();
                        var item = MapDepositListItem<CashDepositListItemDto>(row, access);
                        item.SlipCount = depositSlips.Count;
                        item.ImageCount = depositSlips.Sum(slip => imageCounts.GetValueOrDefault(slip.SlipGuid));
                        item.SlipSummaries = depositSlips
                            .Select(slip => SummarizeSlip(slip, imageCounts.GetValueOrDefault(slip.SlipGuid)))
                            .ToList();
                        return item;
                    })
                    .ToList(),
            }
        );
    }

    public async Task<ApiResponse<CashDepositDetailDto>> GetDepositAsync(
        CashAccess access,
        string depositGuid,
        CancellationToken cancellationToken
    )
    {
        if (!access.CanViewOverview)
        {
            return Fail<CashDepositDetailDto>("无权查看现金管理", StoreCashConstants.ErrorCodes.StoreForbidden);
        }

        var deposit = await LoadDepositAsync(access, depositGuid, cancellationToken);
        return deposit is null
            ? Fail<CashDepositDetailDto>("存款记录不存在", StoreCashConstants.ErrorCodes.RecordNotFound)
            : ApiResponse<CashDepositDetailDto>.OK(await BuildDepositDetailAsync(deposit, access, cancellationToken));
    }

    /// <summary>读取存款并校验分店范围；越权与不存在一律返回 null，不暴露记录是否存在。</summary>
    private async Task<StoreCashDeposit?> LoadDepositAsync(
        CashAccess access,
        string depositGuid,
        CancellationToken cancellationToken
    )
    {
        var deposit = await _db.Queryable<StoreCashDeposit>()
            .Where(item => item.DepositGuid == depositGuid)
            .FirstAsync(cancellationToken);
        return deposit is not null && access.CanAccessStore(deposit.StoreCode) ? deposit : null;
    }

    private T MapDepositListItem<T>(StoreCashDeposit row, CashAccess access)
        where T : CashDepositListItemDto, new() =>
        new()
        {
            DepositGuid = row.DepositGuid,
            StoreCode = row.StoreCode,
            DepositDate = StoreCashClock.FromColumn(row.DepositDate),
            CoveredFromDate = row.CoveredFromDate.HasValue ? StoreCashClock.FromColumn(row.CoveredFromDate.Value) : null,
            CoveredToDate = row.CoveredToDate.HasValue ? StoreCashClock.FromColumn(row.CoveredToDate.Value) : null,
            TotalAmount = row.TotalAmount,
            Status = row.Status,
            Note = row.Note,
            CreatedByName = row.CreatedByName,
            CreatedAtUtc = StoreCashClock.AsUtc(row.CreatedAtUtc),
            CanVoid = row.Status == StoreCashConstants.RecordStatus.Active
                && CashVisibilityRules.CanVoid(access, access.CanCreateDeposit, row.CreatedByUserGuid, row.CreatedAtUtc, Now),
        };

    private async Task<CashDepositDetailDto> BuildDepositDetailAsync(
        StoreCashDeposit deposit,
        CashAccess access,
        CancellationToken cancellationToken
    )
    {
        var slips = await _db.Queryable<StoreCashDepositSlip>()
            .Where(item => item.DepositGuid == deposit.DepositGuid)
            .OrderBy(item => item.SortOrder)
            .ToListAsync(cancellationToken);
        var attachments = await _attachments.GetLinkedAsync(
            slips.Select(item => item.SlipGuid).ToList(),
            cancellationToken
        );

        var detail = MapDepositListItem<CashDepositDetailDto>(deposit, access);
        detail.SlipCount = slips.Count;
        detail.ImageCount = slips.Sum(slip => attachments.GetValueOrDefault(slip.SlipGuid)?.Count ?? 0);
        detail.SlipSummaries = slips
            .Select(slip => SummarizeSlip(slip, attachments.GetValueOrDefault(slip.SlipGuid)?.Count ?? 0))
            .ToList();
        detail.OverrideReason = deposit.OverrideReason;
        detail.VoidReason = deposit.VoidReason;
        detail.VoidedByName = deposit.VoidedByName;
        detail.VoidedAtUtc = StoreCashClock.AsUtc(deposit.VoidedAtUtc);
        detail.Slips = slips
            .Select(slip => new CashDepositSlipDto
            {
                SlipGuid = slip.SlipGuid,
                Amount = slip.Amount,
                SlipNo = slip.SlipNo,
                Attachments = attachments.GetValueOrDefault(slip.SlipGuid) ?? new List<CashAttachmentDto>(),
            })
            .ToList();
        return detail;
    }

    private static CashDepositSlipSummaryDto SummarizeSlip(StoreCashDepositSlip slip, int imageCount) =>
        new()
        {
            SlipGuid = slip.SlipGuid,
            Amount = slip.Amount,
            SlipNo = slip.SlipNo,
            ImageCount = imageCount,
        };

    // ───────────────────────── 作废 ─────────────────────────

    public async Task<ApiResponse<CashDepositDetailDto>> VoidDepositAsync(
        CashAccess access,
        string depositGuid,
        CashVoidRequest request,
        CancellationToken cancellationToken
    )
    {
        var deposit = await LoadDepositAsync(access, depositGuid, cancellationToken);
        if (deposit is null)
        {
            return Fail<CashDepositDetailDto>("存款记录不存在", StoreCashConstants.ErrorCodes.RecordNotFound);
        }

        // 作废是幂等的：已作废再点（弱网重试）直接返回当前状态，不报错。
        if (deposit.Status == StoreCashConstants.RecordStatus.Voided)
        {
            return ApiResponse<CashDepositDetailDto>.OK(await BuildDepositDetailAsync(deposit, access, cancellationToken));
        }

        if (!CashVisibilityRules.CanVoid(access, access.CanCreateDeposit, deposit.CreatedByUserGuid, deposit.CreatedAtUtc, Now))
        {
            return Fail<CashDepositDetailDto>(
                $"无权作废该存款（本人录入 {StoreCashConstants.ManagerSelfVoidHours} 小时内可作废，其余需要作废权限）",
                StoreCashConstants.ErrorCodes.VoidNotAllowed
            );
        }

        var reason = NormalizeText(request.Reason, MaxReasonLength);
        if (reason is null || reason.Length < MinReasonLength)
        {
            return Fail<CashDepositDetailDto>("请填写作废原因", StoreCashConstants.ErrorCodes.InvalidRequest);
        }

        var now = Now.UtcDateTime;
        var guid = deposit.DepositGuid;
        var updated = await _db.Updateable<StoreCashDeposit>()
            .SetColumns(item => new StoreCashDeposit
            {
                Status = StoreCashConstants.RecordStatus.Voided,
                VoidReason = reason,
                VoidedAtUtc = now,
                VoidedByUserGuid = access.UserGuid,
                VoidedByName = access.UserName,
            })
            .Where(item => item.DepositGuid == guid && item.Status == StoreCashConstants.RecordStatus.Active)
            .ExecuteCommandAsync(cancellationToken);
        if (updated != 1)
        {
            // 并发的另一次作废先到了：重读后按幂等处理。
            deposit = await LoadDepositAsync(access, depositGuid, cancellationToken) ?? deposit;
        }
        else
        {
            deposit.Status = StoreCashConstants.RecordStatus.Voided;
            deposit.VoidReason = reason;
            deposit.VoidedAtUtc = now;
            deposit.VoidedByUserGuid = access.UserGuid;
            deposit.VoidedByName = access.UserName;
        }

        return ApiResponse<CashDepositDetailDto>.OK(await BuildDepositDetailAsync(deposit, access, cancellationToken));
    }
}
