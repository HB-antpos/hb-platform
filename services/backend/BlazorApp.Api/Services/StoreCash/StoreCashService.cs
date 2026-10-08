using BlazorApp.Api.Data;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.StoreCash;

/// <summary>
/// 分店现金管理核心：现金池 = 期初 + 纳入的日结实点现金 − 存款 − 支出。
/// 备用金在点钱前已取出，不计入；日结存档由 <see cref="ICashDailyCloseSource"/> 提供，未接入时余额不可算。
/// 本文件放公共校验、现金池快照、总览、按日明细与日结存档手选；存款、支出、期初盘点见各自的分部文件。
/// </summary>
public sealed partial class StoreCashService : IStoreCashService
{
    private const int MaxDailyRangeDays = 93;
    private const int MaxReasonLength = 500;
    private const int MinReasonLength = 2;
    private const int MaxNoteLength = 500;
    private const int MaxClientRequestIdLength = 64;
    private const int MissingCloseLookbackDays = 14;
    private const int NoOpeningScanDays = 60;

    private readonly ISqlSugarClient _db;
    private readonly ICashDailyCloseSource _closeSource;
    private readonly IStoreCashAttachmentService _attachments;
    private readonly ILogger<StoreCashService> _logger;
    private readonly TimeProvider _timeProvider;

    public StoreCashService(
        SqlSugarContext context,
        ICashDailyCloseSource closeSource,
        IStoreCashAttachmentService attachments,
        ILogger<StoreCashService> logger,
        TimeProvider timeProvider
    )
        : this(context.Db, closeSource, attachments, logger, timeProvider) { }

    internal StoreCashService(
        ISqlSugarClient db,
        ICashDailyCloseSource closeSource,
        IStoreCashAttachmentService attachments,
        ILogger<StoreCashService> logger,
        TimeProvider timeProvider
    )
    {
        _db = db;
        _closeSource = closeSource;
        _attachments = attachments;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    // ───────────────────────── 公共校验与小工具 ─────────────────────────

    private sealed record StoreLookup(Store? Store, string? ErrorCode, string? ErrorMessage);

    private async Task<StoreLookup> LookupStoreAsync(
        CashAccess access,
        string? storeCode,
        CancellationToken cancellationToken
    )
    {
        var code = storeCode?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            return new StoreLookup(null, StoreCashConstants.ErrorCodes.InvalidRequest, "请选择分店");
        }

        // 先判范围再查库：无权分店与不存在的分店对调用方表现一致，避免探测分店代码。
        if (!access.CanAccessStore(code))
        {
            return new StoreLookup(null, StoreCashConstants.ErrorCodes.StoreForbidden, "无权操作该分店的现金数据");
        }

        var store = await _db.Queryable<Store>()
            .Where(item => item.StoreCode == code && !item.IsDeleted)
            .FirstAsync(cancellationToken);
        return store is null
            ? new StoreLookup(null, StoreCashConstants.ErrorCodes.StoreNotFound, "分店不存在")
            : new StoreLookup(store, null, null);
    }

    private DateTimeOffset Now => _timeProvider.GetUtcNow();

    private DateOnly StoreToday(Store store) => StoreCashClock.GetStoreToday(store, Now);

    private static ApiResponse<T> Fail<T>(string message, string errorCode) => ApiResponse<T>.Error(message, errorCode);

    private static ApiResponse<T> Fail<T>(StoreLookup lookup) =>
        ApiResponse<T>.Error(lookup.ErrorMessage ?? "分店无效", lookup.ErrorCode);

    private static bool IsTwoDecimalPositiveAmount(decimal amount) =>
        amount > 0 && amount <= StoreCashConstants.MaxAmount && decimal.Round(amount, 2) == amount;

    private static string? NormalizeText(string? value, int maxLength)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return text.Length > maxLength ? text[..maxLength] : text;
    }

    private static bool IsUniqueViolation(Exception exception) =>
        EmployeeCashierBarcodeService.IsUniqueConstraintViolation(exception);

    // ───────────────────────── 范围与阈值 ─────────────────────────

    public async Task<ApiResponse<CashContextDto>> GetContextAsync(
        CashAccess access,
        CancellationToken cancellationToken
    )
    {
        if (!access.CanViewOverview)
        {
            return Fail<CashContextDto>("无权查看现金管理", StoreCashConstants.ErrorCodes.StoreForbidden);
        }

        var storesQuery = _db.Queryable<Store>().Where(item => !item.IsDeleted);
        List<Store> stores;
        if (access.AllStores)
        {
            stores = await storesQuery.OrderBy(item => item.StoreCode).ToListAsync(cancellationToken);
        }
        else
        {
            var codes = access.StoreCodes.ToList();
            stores = codes.Count == 0
                ? new List<Store>()
                : await storesQuery
                    .Where(item => codes.Contains(item.StoreCode))
                    .OrderBy(item => item.StoreCode)
                    .ToListAsync(cancellationToken);
        }

        var now = Now;
        return ApiResponse<CashContextDto>.OK(
            new CashContextDto
            {
                Stores = stores
                    .Select(store => new CashStoreOptionDto
                    {
                        StoreCode = store.StoreCode,
                        StoreName = store.StoreName,
                        TimeZoneId = StoreCashClock.ResolveTimeZoneId(store),
                        StoreToday = StoreCashClock.GetStoreToday(store, now),
                        CashRegisterEnabled = store.IsActive,
                    })
                    .ToList(),
                Capabilities = new CashCapabilitiesDto
                {
                    CanCreateDeposit = access.CanCreateDeposit,
                    CanCreateExpense = access.CanCreateExpense,
                    CanViewAllStores = access.AllStores,
                    CanVoid = access.CanVoid,
                },
                DailyCloseConnected = _closeSource.IsConnected,
                T2VisibleDays = StoreCashConstants.ManagerT2VisibleDays,
                MaxBackfillDays = StoreCashConstants.ManagerMaxBackfillDays,
                SelfVoidHours = StoreCashConstants.ManagerSelfVoidHours,
                DepositOverdueDays = StoreCashConstants.DepositOverdueDays,
                DepositDifferenceReasonThreshold = StoreCashConstants.DepositDifferenceReasonThreshold,
                MaxSlipsPerDeposit = StoreCashConstants.MaxSlipsPerDeposit,
                MaxImagesPerSlip = StoreCashConstants.MaxImagesPerSlip,
                MaxImagesPerExpense = StoreCashConstants.MaxImagesPerExpense,
            }
        );
    }

    // ───────────────────────── 现金池快照（计算见 StoreCashService.Pool.cs） ─────────────────────────

    private async Task<StoreCashBalanceEntry?> GetActiveOpeningAsync(
        string storeCode,
        CancellationToken cancellationToken
    ) =>
        await _db.Queryable<StoreCashBalanceEntry>()
            .Where(item => item.StoreCode == storeCode
                && item.EntryType == StoreCashConstants.BalanceEntryType.Opening
                && item.Status == StoreCashConstants.RecordStatus.Active)
            .FirstAsync(cancellationToken);

    /// <summary>被某张有效存款的覆盖范围包含；有多张时取最后录入的一张作为「由哪张存款覆盖」。</summary>
    private static StoreCashDeposit? FindCoveringDeposit(IEnumerable<StoreCashDeposit> deposits, DateOnly date) =>
        deposits
            .Where(item => item.CoveredFromDate.HasValue
                && item.CoveredToDate.HasValue
                && StoreCashClock.FromColumn(item.CoveredFromDate.Value) <= date
                && date <= StoreCashClock.FromColumn(item.CoveredToDate.Value))
            .OrderByDescending(item => item.CreatedAtUtc)
            .FirstOrDefault();

    // ───────────────────────── 总览 ─────────────────────────

    public async Task<ApiResponse<CashStoreSummaryDto>> GetSummaryAsync(
        CashAccess access,
        string? storeCode,
        CancellationToken cancellationToken
    )
    {
        if (!access.CanViewOverview)
        {
            return Fail<CashStoreSummaryDto>("无权查看现金管理", StoreCashConstants.ErrorCodes.StoreForbidden);
        }

        var lookup = await LookupStoreAsync(access, storeCode, cancellationToken);
        if (lookup.Store is null)
        {
            return Fail<CashStoreSummaryDto>(lookup);
        }

        var store = lookup.Store;
        var today = StoreToday(store);
        var pool = await BuildPoolAsync(store, today, cancellationToken);

        // 分类合计：无全部分店权限者的 T2 只含窗口内，所以分类之和可能小于 ExpenseTotal（展示层限制，余额仍是真实值）。
        var categories = CategoryTotals(pool.Expenses, access, today);
        var uncovered = UncoveredDays(pool);
        DateOnly? oldestUncovered = uncovered.Count > 0 ? uncovered[0].Key : null;

        var missingCloseDates = new List<DateOnly>();
        if (pool.Connected)
        {
            var openingDate = pool.Opening is null ? (DateOnly?)null : StoreCashClock.FromColumn(pool.Opening.EntryDate);
            var datesWithClose = pool.Archives.Select(archive => archive.BusinessDate).ToHashSet();
            for (var offset = MissingCloseLookbackDays; offset >= 1; offset--)
            {
                var date = today.AddDays(-offset);
                if (date < pool.ScanFrom || (openingDate.HasValue && date < openingDate.Value))
                {
                    continue;
                }

                if (!datesWithClose.Contains(date))
                {
                    missingCloseDates.Add(date);
                }
            }
        }

        var lastDeposit = await _db.Queryable<StoreCashDeposit>()
            .Where(item => item.StoreCode == store.StoreCode && item.Status == StoreCashConstants.RecordStatus.Active)
            .OrderBy(item => item.DepositDate, OrderByType.Desc)
            .FirstAsync(cancellationToken);
        var lastCount = await _db.Queryable<StoreCashBalanceEntry>()
            .Where(item => item.StoreCode == store.StoreCode
                && item.EntryType == StoreCashConstants.BalanceEntryType.Count
                && item.Status == StoreCashConstants.RecordStatus.Active)
            .OrderBy(item => item.EntryDate, OrderByType.Desc)
            .OrderBy(item => item.CreatedAtUtc, OrderByType.Desc)
            .FirstAsync(cancellationToken);

        return ApiResponse<CashStoreSummaryDto>.OK(
            new CashStoreSummaryDto
            {
                StoreCode = store.StoreCode,
                StoreName = store.StoreName,
                AsOfDate = today,
                DailyCloseConnected = pool.Connected,
                LatestCloseDate = pool.Archives.Count > 0 ? pool.Archives.Max(archive => archive.BusinessDate) : null,
                OpeningMissing = pool.Opening is null,
                Opening = pool.Opening is null ? null : MapEntry(pool.Opening, access),
                PoolBalance = pool.PoolBalance,
                InflowTotal = pool.InflowTotal,
                DepositTotal = pool.DepositTotal,
                ExpenseTotal = pool.ExpenseTotal,
                ExpenseByCategory = categories,
                T2Restricted = !access.AllStores,
                UncoveredDayCount = uncovered.Count,
                OldestUncoveredDate = oldestUncovered,
                UncoveredCash = pool.Connected ? decimal.Round(uncovered.Sum(item => item.Value), 2) : null,
                DepositOverdue = IsDepositOverdue(today, oldestUncovered),
                SuggestedDepositAmount = pool.PoolBalance is > 0 ? pool.PoolBalance : null,
                LastDepositDate = lastDeposit is null ? null : StoreCashClock.FromColumn(lastDeposit.DepositDate),
                LastCount = lastCount is null ? null : MapEntry(lastCount, access),
                MissingCloseDates = missingCloseDates,
            }
        );
    }

    // ───────────────────────── 按日明细 ─────────────────────────

    public async Task<ApiResponse<CashDailyDto>> GetDailyAsync(
        CashAccess access,
        string? storeCode,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken
    )
    {
        if (!access.CanViewOverview)
        {
            return Fail<CashDailyDto>("无权查看现金管理", StoreCashConstants.ErrorCodes.StoreForbidden);
        }

        var lookup = await LookupStoreAsync(access, storeCode, cancellationToken);
        if (lookup.Store is null)
        {
            return Fail<CashDailyDto>(lookup);
        }

        if (to < from || to.DayNumber - from.DayNumber >= MaxDailyRangeDays)
        {
            return Fail<CashDailyDto>(
                $"日期范围无效，最多查询 {MaxDailyRangeDays} 天",
                StoreCashConstants.ErrorCodes.InvalidRequest
            );
        }

        var store = lookup.Store;
        var today = StoreToday(store);
        var code = store.StoreCode;
        var fromColumn = StoreCashClock.ToColumn(from);
        var toColumn = StoreCashClock.ToColumn(to);

        var connected = _closeSource.IsConnected;
        var archives = connected
            ? (await _closeSource.GetArchivesAsync(new[] { code }, from, to, cancellationToken))
                .Where(archive => string.Equals(archive.StoreCode, code, StringComparison.OrdinalIgnoreCase))
                .ToList()
            : new List<CashCloseArchive>();
        var selections = await _db.Queryable<StoreCashCloseSelection>()
            .Where(item => item.StoreCode == code
                && item.IsCurrent
                && item.BusinessDate >= fromColumn
                && item.BusinessDate <= toColumn)
            .ToListAsync(cancellationToken);
        var selectionByKey = selections.ToDictionary(
            item => (StoreCashClock.FromColumn(item.BusinessDate), item.DeviceCode)
        );
        var deposits = await _db.Queryable<StoreCashDeposit>()
            .Where(item => item.StoreCode == code
                && item.Status == StoreCashConstants.RecordStatus.Active
                && item.CoveredFromDate != null
                && item.CoveredToDate != null
                && item.CoveredFromDate <= toColumn
                && item.CoveredToDate >= fromColumn)
            .ToListAsync(cancellationToken);
        var expenses = await _db.Queryable<StoreCashExpense>()
            .Where(item => item.StoreCode == code
                && item.Status == StoreCashConstants.RecordStatus.Active
                && item.ExpenseDate >= fromColumn
                && item.ExpenseDate <= toColumn)
            .Select(item => new ExpenseLite
            {
                ExpenseGuid = item.ExpenseGuid,
                Category = item.Category,
                ExpenseDate = item.ExpenseDate,
                Amount = item.Amount,
            })
            .ToListAsync(cancellationToken);
        var expenseByDate = expenses
            .Where(item => CashVisibilityRules.CanSeeExpense(
                access,
                item.Category,
                StoreCashClock.FromColumn(item.ExpenseDate),
                today))
            .GroupBy(item => StoreCashClock.FromColumn(item.ExpenseDate))
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Amount));

        var archivesByDate = archives.ToLookup(archive => archive.BusinessDate);
        var rows = new List<CashDailyRowDto>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var devices = archivesByDate[date]
                .GroupBy(archive => archive.DeviceCode)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group =>
                {
                    selectionByKey.TryGetValue((date, group.Key), out var current);
                    var resolved = CashCloseSelectionResolver.Resolve(group.ToList(), current);
                    return BuildDeviceDto(group.Key, group.ToList(), resolved);
                })
                .ToList();
            var covering = FindCoveringDeposit(deposits, date);
            rows.Add(
                new CashDailyRowDto
                {
                    BusinessDate = date,
                    InflowCash = decimal.Round(devices.Sum(device => device.IncludedCash), 2),
                    HasClose = devices.Count > 0,
                    Covered = covering is not null,
                    CoveredByDepositGuid = covering?.DepositGuid,
                    ExpenseTotal = decimal.Round(expenseByDate.GetValueOrDefault(date), 2),
                    Devices = devices,
                }
            );
        }

        return ApiResponse<CashDailyDto>.OK(
            new CashDailyDto
            {
                StoreCode = code,
                DailyCloseConnected = connected,
                Rows = rows,
            }
        );
    }

    private static CashDailyDeviceDto BuildDeviceDto(
        string deviceCode,
        IReadOnlyList<CashCloseArchive> archives,
        ResolvedCloseSelection resolved
    )
    {
        var includedIds = resolved.Included.Select(archive => archive.CloseId).ToHashSet(StringComparer.Ordinal);
        return new CashDailyDeviceDto
        {
            DeviceCode = deviceCode,
            SelectionMode = resolved.Mode,
            SelectionStale = resolved.Stale,
            SelectionOverlapWarning = resolved.OverlapWarning,
            SelectionReason = resolved.Reason,
            SelectedByName = resolved.SelectedByName,
            SelectedAtUtc = StoreCashClock.AsUtc(resolved.SelectedAtUtc),
            IncludedCash = decimal.Round(resolved.IncludedCash, 2),
            Archives = archives
                .OrderByDescending(archive => archive.SavedAtUtc)
                .Select(archive => new CashCloseArchiveDto
                {
                    CloseId = archive.CloseId,
                    SavedAtUtc = StoreCashClock.AsUtc(archive.SavedAtUtc),
                    PeriodFromUtc = StoreCashClock.AsUtc(archive.PeriodFromUtc),
                    PeriodToUtc = StoreCashClock.AsUtc(archive.PeriodToUtc),
                    CountedCash = archive.CountedCash,
                    ExpectedCash = archive.ExpectedCash,
                    Variance = archive.Variance,
                    Included = includedIds.Contains(archive.CloseId),
                })
                .ToList(),
        };
    }

    // ───────────────────────── 日结存档手选 ─────────────────────────

    public async Task<ApiResponse<CashDailyDeviceDto>> SetCloseSelectionAsync(
        CashAccess access,
        CashCloseSelectionRequest request,
        CancellationToken cancellationToken
    )
    {
        if (!access.CanCreateDeposit)
        {
            return Fail<CashDailyDeviceDto>("无权调整纳入现金池的日结", StoreCashConstants.ErrorCodes.StoreForbidden);
        }

        var lookup = await LookupStoreAsync(access, request.StoreCode, cancellationToken);
        if (lookup.Store is null)
        {
            return Fail<CashDailyDeviceDto>(lookup);
        }

        var store = lookup.Store;
        var today = StoreToday(store);
        if (!CashVisibilityRules.IsEntryDateAllowed(access, request.BusinessDate, today))
        {
            return Fail<CashDailyDeviceDto>(
                $"营业日超出可调整范围（最多回溯 {StoreCashConstants.ManagerMaxBackfillDays} 天）",
                StoreCashConstants.ErrorCodes.DateOutOfRange
            );
        }

        var mode = request.Mode?.Trim();
        if (mode != StoreCashConstants.SelectionMode.Default && mode != StoreCashConstants.SelectionMode.Manual)
        {
            return Fail<CashDailyDeviceDto>("选择方式无效", StoreCashConstants.ErrorCodes.InvalidRequest);
        }

        var deviceCode = request.DeviceCode?.Trim();
        if (string.IsNullOrEmpty(deviceCode))
        {
            return Fail<CashDailyDeviceDto>("请选择设备", StoreCashConstants.ErrorCodes.InvalidRequest);
        }

        if (!_closeSource.IsConnected)
        {
            return Fail<CashDailyDeviceDto>(
                "日结数据暂未接入，暂不能调整",
                StoreCashConstants.ErrorCodes.CloseSourceUnavailable
            );
        }

        var code = store.StoreCode;
        var archives = (await _closeSource.GetArchivesAsync(
                new[] { code },
                request.BusinessDate,
                request.BusinessDate,
                cancellationToken
            ))
            .Where(archive => string.Equals(archive.StoreCode, code, StringComparison.OrdinalIgnoreCase)
                && archive.BusinessDate == request.BusinessDate
                && string.Equals(archive.DeviceCode, deviceCode, StringComparison.Ordinal))
            .ToList();
        if (archives.Count == 0)
        {
            return Fail<CashDailyDeviceDto>("该设备当天没有日结存档", StoreCashConstants.ErrorCodes.CloseNotFound);
        }

        var businessColumn = StoreCashClock.ToColumn(request.BusinessDate);
        var current = await _db.Queryable<StoreCashCloseSelection>()
            .Where(item => item.StoreCode == code
                && item.BusinessDate == businessColumn
                && item.DeviceCode == deviceCode
                && item.IsCurrent)
            .FirstAsync(cancellationToken);

        var reason = NormalizeText(request.Reason, MaxReasonLength);
        StoreCashCloseSelection? next = null;
        if (mode == StoreCashConstants.SelectionMode.Manual)
        {
            var closeIds = (request.CloseIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (closeIds.Count == 0)
            {
                return Fail<CashDailyDeviceDto>("请至少选择一份日结存档", StoreCashConstants.ErrorCodes.InvalidRequest);
            }

            var selected = archives.Where(archive => closeIds.Contains(archive.CloseId, StringComparer.Ordinal)).ToList();
            if (selected.Count != closeIds.Count)
            {
                return Fail<CashDailyDeviceDto>("所选日结存档不存在或已变更", StoreCashConstants.ErrorCodes.CloseNotFound);
            }

            if (reason is null || reason.Length < MinReasonLength)
            {
                return Fail<CashDailyDeviceDto>("手动选择日结存档必须填写原因", StoreCashConstants.ErrorCodes.InvalidRequest);
            }

            next = new StoreCashCloseSelection
            {
                StoreCode = code,
                BusinessDate = businessColumn,
                DeviceCode = deviceCode,
                Mode = StoreCashConstants.SelectionMode.Manual,
                CloseIdsJson = CashCloseSelectionResolver.SerializeCloseIds(closeIds),
                LatestSavedAtUtcAtSelection = archives.Max(archive => archive.SavedAtUtc),
                OverlapWarning = CashCloseSelectionResolver.HasOverlap(selected),
                Reason = reason,
                IsCurrent = true,
                SelectedByUserGuid = access.UserGuid,
                SelectedByName = access.UserName,
                SelectedAtUtc = Now.UtcDateTime,
            };
        }
        else if (current is not null && current.Mode == StoreCashConstants.SelectionMode.Manual)
        {
            // 恢复默认：写一条 Default 记录留痕（谁、何时、为什么改回），而不是直接删掉手选记录。
            next = new StoreCashCloseSelection
            {
                StoreCode = code,
                BusinessDate = businessColumn,
                DeviceCode = deviceCode,
                Mode = StoreCashConstants.SelectionMode.Default,
                CloseIdsJson = "[]",
                LatestSavedAtUtcAtSelection = archives.Max(archive => archive.SavedAtUtc),
                OverlapWarning = false,
                Reason = reason ?? "恢复默认",
                IsCurrent = true,
                SelectedByUserGuid = access.UserGuid,
                SelectedByName = access.UserName,
                SelectedAtUtc = Now.UtcDateTime,
            };
        }

        if (next is not null)
        {
            await _db.Ado.BeginTranAsync();
            try
            {
                await _db.Updateable<StoreCashCloseSelection>()
                    .SetColumns(item => new StoreCashCloseSelection { IsCurrent = false })
                    .Where(item => item.StoreCode == code
                        && item.BusinessDate == businessColumn
                        && item.DeviceCode == deviceCode
                        && item.IsCurrent)
                    .ExecuteCommandAsync(cancellationToken);
                await _db.Insertable(next).ExecuteCommandAsync(cancellationToken);
                await _db.Ado.CommitTranAsync();
            }
            catch (Exception ex)
            {
                await _db.Ado.RollbackTranAsync();
                if (IsUniqueViolation(ex))
                {
                    // 并发的另一次手选抢先写入了当前记录：让调用方刷新后重试，而不是悄悄覆盖对方。
                    return Fail<CashDailyDeviceDto>("日结选择刚被其他人修改，请刷新后重试", StoreCashConstants.ErrorCodes.Conflict);
                }

                throw;
            }
        }

        var resolved = CashCloseSelectionResolver.Resolve(archives, next ?? current);
        return ApiResponse<CashDailyDeviceDto>.OK(BuildDeviceDto(deviceCode, archives, resolved));
    }
}
