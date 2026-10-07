using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.StoreCash;

// 现金支出：现金工资、现金购物、T2、其他。店长录入即生效，不走审核。
// T2 对无全部分店权限者只开放自己分店最近 14 天，所有读取与作废路径都经 CashVisibilityRules.CanSeeExpense。
public sealed partial class StoreCashService
{
    private const int MaxPayeeNameLength = 100;

    public async Task<ApiResponse<CashExpenseDetailDto>> CreateExpenseAsync(
        CashAccess access,
        CreateCashExpenseRequest request,
        CancellationToken cancellationToken
    )
    {
        if (!access.CanCreateExpense)
        {
            return Fail<CashExpenseDetailDto>("无权录入现金支出", StoreCashConstants.ErrorCodes.StoreForbidden);
        }

        var clientRequestId = request.ClientRequestId?.Trim();
        if (string.IsNullOrEmpty(clientRequestId) || clientRequestId.Length > MaxClientRequestIdLength)
        {
            return Fail<CashExpenseDetailDto>("请求号无效", StoreCashConstants.ErrorCodes.InvalidRequest);
        }

        var lookup = await LookupStoreAsync(access, request.StoreCode, cancellationToken);
        if (lookup.Store is null)
        {
            return Fail<CashExpenseDetailDto>(lookup);
        }

        var store = lookup.Store;
        var code = store.StoreCode;

        // 幂等：弱网重试同一次提交，直接返回已落库的那条。
        var existing = await FindExpenseByRequestAsync(clientRequestId, cancellationToken);
        if (existing is not null)
        {
            return await ReplayExpenseAsync(existing, access, code, cancellationToken);
        }

        var today = StoreToday(store);
        var category = request.Category?.Trim();
        if (!StoreCashConstants.ExpenseCategory.IsValid(category))
        {
            return Fail<CashExpenseDetailDto>("支出类别无效", StoreCashConstants.ErrorCodes.InvalidRequest);
        }

        if (!IsTwoDecimalPositiveAmount(request.Amount))
        {
            return Fail<CashExpenseDetailDto>("金额须大于 0，且最多两位小数", StoreCashConstants.ErrorCodes.InvalidRequest);
        }

        if (!CashVisibilityRules.IsEntryDateAllowed(access, request.ExpenseDate, today))
        {
            return Fail<CashExpenseDetailDto>(
                $"支出日期不能晚于今天，且最多回溯 {StoreCashConstants.ManagerMaxBackfillDays} 天",
                StoreCashConstants.ErrorCodes.DateOutOfRange
            );
        }

        var attachmentGuids = request.AttachmentGuids ?? new List<string>();
        if (attachmentGuids.Count > StoreCashConstants.MaxImagesPerExpense || attachmentGuids.Any(string.IsNullOrWhiteSpace))
        {
            return Fail<CashExpenseDetailDto>(
                $"最多上传 {StoreCashConstants.MaxImagesPerExpense} 张图片",
                StoreCashConstants.ErrorCodes.InvalidRequest
            );
        }

        if (category == StoreCashConstants.ExpenseCategory.Purchase && attachmentGuids.Count == 0)
        {
            return Fail<CashExpenseDetailDto>("现金购物必须上传收据照片", StoreCashConstants.ErrorCodes.AttachmentRequired);
        }

        var payeeName = NormalizeText(request.PayeeName, MaxPayeeNameLength);
        var payeeUserGuid = request.PayeeUserGuid?.Trim();
        if (!string.IsNullOrEmpty(payeeUserGuid))
        {
            var payee = await _db.Queryable<User>()
                .Where(item => item.UserGUID == payeeUserGuid && !item.IsDeleted)
                .FirstAsync(cancellationToken);
            var linked = payee is not null
                && await _db.Queryable<UserStore>()
                    .AnyAsync(item => item.UserGUID == payeeUserGuid && item.StoreGUID == store.StoreGUID && !item.IsDeleted);
            if (!linked)
            {
                return Fail<CashExpenseDetailDto>("所选员工不属于该分店", StoreCashConstants.ErrorCodes.InvalidRequest);
            }

            // 关联员工时收款人姓名以员工资料为准，避免同一个人在记录里出现多种写法。
            payeeName = NormalizeText(payee!.FullName, MaxPayeeNameLength)
                ?? NormalizeText(payee.Username, MaxPayeeNameLength)
                ?? payeeName;
        }
        else
        {
            payeeUserGuid = null;
        }

        var prepared = await _attachments.PrepareAsync(code, access.UserGuid, attachmentGuids, cancellationToken);
        if (!prepared.Success)
        {
            return Fail<CashExpenseDetailDto>(prepared.Message, prepared.ErrorCode ?? StoreCashConstants.ErrorCodes.AttachmentInvalid);
        }

        var now = Now.UtcDateTime;
        var expense = new StoreCashExpense
        {
            StoreCode = code,
            ExpenseDate = StoreCashClock.ToColumn(request.ExpenseDate),
            Category = category!,
            Amount = request.Amount,
            PayeeUserGuid = payeeUserGuid,
            PayeeName = payeeName,
            Note = NormalizeText(request.Note, MaxNoteLength),
            ReviewStatus = "None",
            Status = StoreCashConstants.RecordStatus.Active,
            ClientRequestId = clientRequestId,
            CreatedByUserGuid = access.UserGuid,
            CreatedByName = access.UserName,
            CreatedAtUtc = now,
        };

        await _db.Ado.BeginTranAsync();
        try
        {
            await _db.Insertable(expense).ExecuteCommandAsync(cancellationToken);
            await LinkAttachmentsAsync(
                StoreCashConstants.AttachmentOwner.Expense,
                expense.ExpenseGuid,
                attachmentGuids,
                now,
                cancellationToken
            );
            await _db.Ado.CommitTranAsync();
        }
        catch (Exception ex)
        {
            await _db.Ado.RollbackTranAsync();
            if (ex is CashConflictException)
            {
                return Fail<CashExpenseDetailDto>("图片状态已变更，请重新提交", StoreCashConstants.ErrorCodes.Conflict);
            }

            if (IsUniqueViolation(ex))
            {
                var concurrent = await FindExpenseByRequestAsync(clientRequestId, cancellationToken);
                if (concurrent is not null)
                {
                    return await ReplayExpenseAsync(concurrent, access, code, cancellationToken);
                }
            }

            throw;
        }

        return ApiResponse<CashExpenseDetailDto>.OK(await BuildExpenseDetailAsync(expense, access, cancellationToken));
    }

    private async Task<StoreCashExpense?> FindExpenseByRequestAsync(
        string clientRequestId,
        CancellationToken cancellationToken
    ) =>
        await _db.Queryable<StoreCashExpense>()
            .Where(item => item.ClientRequestId == clientRequestId)
            .FirstAsync(cancellationToken);

    private async Task<ApiResponse<CashExpenseDetailDto>> ReplayExpenseAsync(
        StoreCashExpense existing,
        CashAccess access,
        string storeCode,
        CancellationToken cancellationToken
    )
    {
        if (!string.Equals(existing.CreatedByUserGuid, access.UserGuid, StringComparison.Ordinal)
            || !string.Equals(existing.StoreCode, storeCode, StringComparison.Ordinal))
        {
            return Fail<CashExpenseDetailDto>("请求号已被其他记录使用", StoreCashConstants.ErrorCodes.Conflict);
        }

        return ApiResponse<CashExpenseDetailDto>.OK(await BuildExpenseDetailAsync(existing, access, cancellationToken));
    }

    // ───────────────────────── 查询 ─────────────────────────

    public async Task<ApiResponse<CashPagedDto<CashExpenseListItemDto>>> ListExpensesAsync(
        CashAccess access,
        string? storeCode,
        DateOnly? from,
        DateOnly? to,
        string? category,
        string? reviewStatus,
        bool includeVoided,
        int limit,
        int offset,
        CancellationToken cancellationToken
    )
    {
        if (!access.CanViewOverview)
        {
            return Fail<CashPagedDto<CashExpenseListItemDto>>("无权查看现金管理", StoreCashConstants.ErrorCodes.StoreForbidden);
        }

        var reviewFilter = reviewStatus?.Trim();
        if (!string.IsNullOrEmpty(reviewFilter) && !StoreCashConstants.ReviewStatus.IsValid(reviewFilter))
        {
            return Fail<CashPagedDto<CashExpenseListItemDto>>("核对状态无效", StoreCashConstants.ErrorCodes.InvalidRequest);
        }

        var lookup = await LookupStoreAsync(access, storeCode, cancellationToken);
        if (lookup.Store is null)
        {
            return Fail<CashPagedDto<CashExpenseListItemDto>>(lookup);
        }

        var categoryFilter = category?.Trim();
        if (!string.IsNullOrEmpty(categoryFilter) && !StoreCashConstants.ExpenseCategory.IsValid(categoryFilter))
        {
            return Fail<CashPagedDto<CashExpenseListItemDto>>("支出类别无效", StoreCashConstants.ErrorCodes.InvalidRequest);
        }

        var store = lookup.Store;
        var code = store.StoreCode;
        var today = StoreToday(store);
        var fromColumn = from.HasValue ? StoreCashClock.ToColumn(from.Value) : DateTime.MinValue;
        var toColumn = to.HasValue ? StoreCashClock.ToColumn(to.Value) : DateTime.MaxValue;
        var t2FromColumn = StoreCashClock.ToColumn(CashVisibilityRules.T2VisibleFrom(today));
        var hasCategory = !string.IsNullOrEmpty(categoryFilter);
        var hasReview = !string.IsNullOrEmpty(reviewFilter);
        var restrictT2 = !access.AllStores;
        var (take, skip) = NormalizePaging(limit, offset);

        // 无全部分店权限者看不到窗口外的 T2：过滤下推到 SQL，分页与总数才一致，也不会把被隐藏的行读出内存。
        var query = _db.Queryable<StoreCashExpense>()
            .Where(item => item.StoreCode == code && item.ExpenseDate >= fromColumn && item.ExpenseDate <= toColumn)
            .WhereIF(!includeVoided, item => item.Status == StoreCashConstants.RecordStatus.Active)
            .WhereIF(hasCategory, item => item.Category == categoryFilter)
            .WhereIF(hasReview, item => item.ReviewStatus == reviewFilter)
            .WhereIF(
                restrictT2,
                item => item.Category != StoreCashConstants.ExpenseCategory.T2 || item.ExpenseDate >= t2FromColumn
            );
        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderBy(item => item.ExpenseDate, OrderByType.Desc)
            .OrderBy(item => item.CreatedAtUtc, OrderByType.Desc)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
        var imageCounts = await _attachments.CountLinkedAsync(
            rows.Select(item => item.ExpenseGuid).ToList(),
            cancellationToken
        );
        var reviewerNames = await LoadUserNamesAsync(rows.Select(item => item.ReviewedByUserGuid), cancellationToken);

        return ApiResponse<CashPagedDto<CashExpenseListItemDto>>.OK(
            new CashPagedDto<CashExpenseListItemDto>
            {
                Total = total,
                Items = rows
                    .Select(row =>
                    {
                        var item = MapExpenseListItem<CashExpenseListItemDto>(row, access, reviewerNames);
                        item.ImageCount = imageCounts.GetValueOrDefault(row.ExpenseGuid);
                        return item;
                    })
                    .ToList(),
            }
        );
    }

    public async Task<ApiResponse<CashExpenseDetailDto>> GetExpenseAsync(
        CashAccess access,
        string expenseGuid,
        CancellationToken cancellationToken
    )
    {
        if (!access.CanViewOverview)
        {
            return Fail<CashExpenseDetailDto>("无权查看现金管理", StoreCashConstants.ErrorCodes.StoreForbidden);
        }

        var expense = await LoadVisibleExpenseAsync(access, expenseGuid, cancellationToken);
        return expense is null
            ? Fail<CashExpenseDetailDto>("支出记录不存在", StoreCashConstants.ErrorCodes.RecordNotFound)
            : ApiResponse<CashExpenseDetailDto>.OK(await BuildExpenseDetailAsync(expense, access, cancellationToken));
    }

    /// <summary>
    /// 读取支出并同时校验分店范围与 T2 可见性；越权、窗口外的 T2 与不存在一律返回 null，
    /// 不暴露「这条记录存在但你看不到」。
    /// </summary>
    private async Task<StoreCashExpense?> LoadVisibleExpenseAsync(
        CashAccess access,
        string expenseGuid,
        CancellationToken cancellationToken
    )
    {
        var expense = await _db.Queryable<StoreCashExpense>()
            .Where(item => item.ExpenseGuid == expenseGuid)
            .FirstAsync(cancellationToken);
        if (expense is null || !access.CanAccessStore(expense.StoreCode))
        {
            return null;
        }

        if (access.AllStores)
        {
            return expense;
        }

        var store = await _db.Queryable<Store>()
            .Where(item => item.StoreCode == expense.StoreCode && !item.IsDeleted)
            .FirstAsync(cancellationToken);
        return store is not null
            && CashVisibilityRules.CanSeeExpense(
                access,
                expense.Category,
                StoreCashClock.FromColumn(expense.ExpenseDate),
                StoreToday(store))
            ? expense
            : null;
    }

    private T MapExpenseListItem<T>(
        StoreCashExpense row,
        CashAccess access,
        IReadOnlyDictionary<string, string> reviewerNames
    )
        where T : CashExpenseListItemDto, new() =>
        new()
        {
            ExpenseGuid = row.ExpenseGuid,
            StoreCode = row.StoreCode,
            ExpenseDate = StoreCashClock.FromColumn(row.ExpenseDate),
            Category = row.Category,
            Amount = row.Amount,
            PayeeName = row.PayeeName,
            Note = row.Note,
            ReviewStatus = row.ReviewStatus,
            ReviewNote = row.ReviewNote,
            ReviewedAtUtc = StoreCashClock.AsUtc(row.ReviewedAtUtc),
            ReviewedByName = row.ReviewedByUserGuid is null ? null : reviewerNames.GetValueOrDefault(row.ReviewedByUserGuid),
            CanReview = access.CanVoid && row.Status == StoreCashConstants.RecordStatus.Active,
            Status = row.Status,
            CreatedByName = row.CreatedByName,
            CreatedAtUtc = StoreCashClock.AsUtc(row.CreatedAtUtc),
            CanVoid = row.Status == StoreCashConstants.RecordStatus.Active
                && CashVisibilityRules.CanVoid(access, access.CanCreateExpense, row.CreatedByUserGuid, row.CreatedAtUtc, Now),
        };

    private async Task<CashExpenseDetailDto> BuildExpenseDetailAsync(
        StoreCashExpense expense,
        CashAccess access,
        CancellationToken cancellationToken
    )
    {
        var attachments = await _attachments.GetLinkedAsync(new[] { expense.ExpenseGuid }, cancellationToken);
        var list = attachments.GetValueOrDefault(expense.ExpenseGuid) ?? new List<CashAttachmentDto>();
        var reviewerNames = await LoadUserNamesAsync(new[] { expense.ReviewedByUserGuid }, cancellationToken);
        var detail = MapExpenseListItem<CashExpenseDetailDto>(expense, access, reviewerNames);
        detail.ImageCount = list.Count;
        detail.PayeeUserGuid = expense.PayeeUserGuid;
        detail.VoidReason = expense.VoidReason;
        detail.VoidedByName = expense.VoidedByName;
        detail.VoidedAtUtc = StoreCashClock.AsUtc(expense.VoidedAtUtc);
        detail.Attachments = list;
        return detail;
    }

    /// <summary>核对人姓名：取员工姓名，没有就用登录名；一次查完本页涉及的人。</summary>
    private async Task<IReadOnlyDictionary<string, string>> LoadUserNamesAsync(
        IEnumerable<string?> userGuids,
        CancellationToken cancellationToken
    )
    {
        var guids = userGuids
            .Where(guid => !string.IsNullOrWhiteSpace(guid))
            .Select(guid => guid!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (guids.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var users = await _db.Queryable<User>()
            .Where(item => guids.Contains(item.UserGUID))
            .Select(item => new { item.UserGUID, item.FullName, item.Username })
            .ToListAsync(cancellationToken);
        return users.ToDictionary(
            item => item.UserGUID,
            item => string.IsNullOrWhiteSpace(item.FullName) ? item.Username : item.FullName!,
            StringComparer.Ordinal
        );
    }

    // ───────────────────────── 核对标记 ─────────────────────────

    /// <summary>
    /// 财务事后核对：已核 / 存疑 / 清除。需要 Cash.Void（与作废同属财务级权限），只对有效记录；
    /// 存疑必须写说明。标记不影响支出生效与现金池，只用于筛选与提示。
    /// </summary>
    public async Task<ApiResponse<CashExpenseDetailDto>> ReviewExpenseAsync(
        CashAccess access,
        string expenseGuid,
        CashExpenseReviewRequest request,
        CancellationToken cancellationToken
    )
    {
        if (!access.CanVoid)
        {
            return Fail<CashExpenseDetailDto>("无权核对现金支出", StoreCashConstants.ErrorCodes.StoreForbidden);
        }

        var expense = await LoadVisibleExpenseAsync(access, expenseGuid, cancellationToken);
        if (expense is null)
        {
            return Fail<CashExpenseDetailDto>("支出记录不存在", StoreCashConstants.ErrorCodes.RecordNotFound);
        }

        if (expense.Status != StoreCashConstants.RecordStatus.Active)
        {
            return Fail<CashExpenseDetailDto>("已作废的支出不能核对", StoreCashConstants.ErrorCodes.InvalidRequest);
        }

        var status = request.ReviewStatus?.Trim();
        if (!StoreCashConstants.ReviewStatus.IsValid(status))
        {
            return Fail<CashExpenseDetailDto>("核对状态无效", StoreCashConstants.ErrorCodes.InvalidRequest);
        }

        var note = NormalizeText(request.Note, MaxReasonLength);
        if (status == StoreCashConstants.ReviewStatus.Flagged && (note is null || note.Length < MinReasonLength))
        {
            return Fail<CashExpenseDetailDto>("标记存疑必须写明原因", StoreCashConstants.ErrorCodes.InvalidRequest);
        }

        var cleared = status == StoreCashConstants.ReviewStatus.None;
        var now = Now.UtcDateTime;
        var guid = expense.ExpenseGuid;
        string? reviewer = cleared ? null : access.UserGuid;
        DateTime? reviewedAt = cleared ? null : now;
        string? reviewNote = cleared ? null : note;
        await _db.Updateable<StoreCashExpense>()
            .SetColumns(item => new StoreCashExpense
            {
                ReviewStatus = status!,
                ReviewNote = reviewNote,
                ReviewedByUserGuid = reviewer,
                ReviewedAtUtc = reviewedAt,
            })
            .Where(item => item.ExpenseGuid == guid && item.Status == StoreCashConstants.RecordStatus.Active)
            .ExecuteCommandAsync(cancellationToken);

        var reloaded = await LoadVisibleExpenseAsync(access, expenseGuid, cancellationToken) ?? expense;
        return ApiResponse<CashExpenseDetailDto>.OK(await BuildExpenseDetailAsync(reloaded, access, cancellationToken));
    }

    // ───────────────────────── 作废 ─────────────────────────

    public async Task<ApiResponse<CashExpenseDetailDto>> VoidExpenseAsync(
        CashAccess access,
        string expenseGuid,
        CashVoidRequest request,
        CancellationToken cancellationToken
    )
    {
        var expense = await LoadVisibleExpenseAsync(access, expenseGuid, cancellationToken);
        if (expense is null)
        {
            return Fail<CashExpenseDetailDto>("支出记录不存在", StoreCashConstants.ErrorCodes.RecordNotFound);
        }

        if (expense.Status == StoreCashConstants.RecordStatus.Voided)
        {
            return ApiResponse<CashExpenseDetailDto>.OK(await BuildExpenseDetailAsync(expense, access, cancellationToken));
        }

        if (!CashVisibilityRules.CanVoid(access, access.CanCreateExpense, expense.CreatedByUserGuid, expense.CreatedAtUtc, Now))
        {
            return Fail<CashExpenseDetailDto>(
                $"无权作废该支出（本人录入 {StoreCashConstants.ManagerSelfVoidHours} 小时内可作废，其余需要作废权限）",
                StoreCashConstants.ErrorCodes.VoidNotAllowed
            );
        }

        var reason = NormalizeText(request.Reason, MaxReasonLength);
        if (reason is null || reason.Length < MinReasonLength)
        {
            return Fail<CashExpenseDetailDto>("请填写作废原因", StoreCashConstants.ErrorCodes.InvalidRequest);
        }

        var now = Now.UtcDateTime;
        var guid = expense.ExpenseGuid;
        var updated = await _db.Updateable<StoreCashExpense>()
            .SetColumns(item => new StoreCashExpense
            {
                Status = StoreCashConstants.RecordStatus.Voided,
                VoidReason = reason,
                VoidedAtUtc = now,
                VoidedByUserGuid = access.UserGuid,
                VoidedByName = access.UserName,
            })
            .Where(item => item.ExpenseGuid == guid && item.Status == StoreCashConstants.RecordStatus.Active)
            .ExecuteCommandAsync(cancellationToken);
        if (updated != 1)
        {
            expense = await LoadVisibleExpenseAsync(access, expenseGuid, cancellationToken) ?? expense;
        }
        else
        {
            expense.Status = StoreCashConstants.RecordStatus.Voided;
            expense.VoidReason = reason;
            expense.VoidedAtUtc = now;
            expense.VoidedByUserGuid = access.UserGuid;
            expense.VoidedByName = access.UserName;
        }

        return ApiResponse<CashExpenseDetailDto>.OK(await BuildExpenseDetailAsync(expense, access, cancellationToken));
    }
}
