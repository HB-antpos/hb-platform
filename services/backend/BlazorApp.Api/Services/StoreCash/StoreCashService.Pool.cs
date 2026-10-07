using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;

namespace BlazorApp.Api.Services.StoreCash;

// 现金池计算拆成两步：先按分店批量取数（每类数据一条查询，多店总览不逐店查库），再逐店纯计算。
// 单店总览、存款差异校验、盘点应有余额与 Web 多店总览共用同一套计算，口径不会分叉。
public sealed partial class StoreCashService
{
    private sealed class ExpenseLite
    {
        public string ExpenseGuid { get; set; } = string.Empty;
        public string StoreCode { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public DateTime ExpenseDate { get; set; }
        public decimal Amount { get; set; }
        public string ReviewStatus { get; set; } = StoreCashConstants.ReviewStatus.None;
    }

    /// <summary>一批分店的现金池原始数据：期初、日结存档、当前手选、有效存款与有效支出。</summary>
    private sealed class PoolInputs
    {
        public required bool Connected { get; init; }
        public required Dictionary<string, StoreCashBalanceEntry> Openings { get; init; }
        public required ILookup<string, CashCloseArchive> Archives { get; init; }
        public required Dictionary<(string Store, DateOnly Date, string Device), StoreCashCloseSelection> Selections { get; init; }
        public required ILookup<string, StoreCashDeposit> Deposits { get; init; }
        public required ILookup<string, ExpenseLite> Expenses { get; init; }
    }

    private sealed class PoolSnapshot
    {
        public required Store Store { get; init; }
        public required DateOnly AsOf { get; init; }
        public required bool Connected { get; init; }
        public StoreCashBalanceEntry? Opening { get; init; }
        public required DateOnly ScanFrom { get; init; }
        public required List<CashCloseArchive> Archives { get; init; }
        public required Dictionary<DateOnly, decimal> InflowByDate { get; init; }
        public required List<StoreCashDeposit> Deposits { get; init; }
        public required List<ExpenseLite> Expenses { get; init; }
        public decimal? InflowTotal { get; init; }
        public decimal DepositTotal { get; init; }
        public decimal ExpenseTotal { get; init; }
        public decimal? PoolBalance { get; init; }
    }

    /// <summary>分店代码在各数据源里大小写可能不一致，统一按大写比较。</summary>
    private static string StoreKey(string storeCode) => storeCode.Trim().ToUpperInvariant();

    private static DateOnly? OpeningDate(StoreCashBalanceEntry? opening) =>
        opening is null ? null : StoreCashClock.FromColumn(opening.EntryDate);

    /// <summary>
    /// 批量取数。日结与手选从「最早期初日（没有期初的店取 asOfMax 往前 60 天）」扫到 asOfMax；
    /// 存款与支出从最早期初日起（任一店没有期初则不设下限）；rangeFrom 更早时一并覆盖，供总览统计区间。
    /// </summary>
    private async Task<PoolInputs> LoadPoolInputsAsync(
        IReadOnlyList<Store> stores,
        DateOnly asOfMax,
        DateOnly? rangeFrom,
        CancellationToken cancellationToken
    )
    {
        var codes = stores.Select(store => store.StoreCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var openingRows = codes.Count == 0
            ? new List<StoreCashBalanceEntry>()
            : await _db.Queryable<StoreCashBalanceEntry>()
                .Where(item => codes.Contains(item.StoreCode)
                    && item.EntryType == StoreCashConstants.BalanceEntryType.Opening
                    && item.Status == StoreCashConstants.RecordStatus.Active)
                .ToListAsync(cancellationToken);
        // 唯一索引保证每店至多一条有效期初；防御性地取最后录入的一条。
        var openings = openingRows
            .GroupBy(item => StoreKey(item.StoreCode))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.CreatedAtUtc).First());

        var storeOpeningDates = stores
            .Select(store => OpeningDate(openings.GetValueOrDefault(StoreKey(store.StoreCode))))
            .ToList();
        var scanFrom = storeOpeningDates
            .Select(date => date ?? asOfMax.AddDays(-NoOpeningScanDays))
            .DefaultIfEmpty(asOfMax)
            .Min();
        var totalsFrom = storeOpeningDates.Any(date => date is null)
            ? DateOnly.MinValue
            : storeOpeningDates.Select(date => date!.Value).DefaultIfEmpty(asOfMax).Min();
        if (rangeFrom.HasValue)
        {
            scanFrom = rangeFrom.Value < scanFrom ? rangeFrom.Value : scanFrom;
            totalsFrom = rangeFrom.Value < totalsFrom ? rangeFrom.Value : totalsFrom;
        }

        var connected = _closeSource.IsConnected;
        IReadOnlyList<CashCloseArchive> archives = Array.Empty<CashCloseArchive>();
        var selections = new List<StoreCashCloseSelection>();
        if (connected && codes.Count > 0 && scanFrom <= asOfMax)
        {
            archives = await _closeSource.GetArchivesAsync(codes, scanFrom, asOfMax, cancellationToken);
            var scanFromColumn = StoreCashClock.ToColumn(scanFrom);
            var asOfMaxColumn = StoreCashClock.ToColumn(asOfMax);
            selections = await _db.Queryable<StoreCashCloseSelection>()
                .Where(item => codes.Contains(item.StoreCode)
                    && item.IsCurrent
                    && item.BusinessDate >= scanFromColumn
                    && item.BusinessDate <= asOfMaxColumn)
                .ToListAsync(cancellationToken);
        }

        var totalsFromColumn = StoreCashClock.ToColumn(totalsFrom);
        var asOfColumn = StoreCashClock.ToColumn(asOfMax);
        var deposits = codes.Count == 0
            ? new List<StoreCashDeposit>()
            : await _db.Queryable<StoreCashDeposit>()
                .Where(item => codes.Contains(item.StoreCode)
                    && item.Status == StoreCashConstants.RecordStatus.Active
                    && item.DepositDate >= totalsFromColumn
                    && item.DepositDate <= asOfColumn)
                .ToListAsync(cancellationToken);
        var expenses = codes.Count == 0
            ? new List<ExpenseLite>()
            : await _db.Queryable<StoreCashExpense>()
                .Where(item => codes.Contains(item.StoreCode)
                    && item.Status == StoreCashConstants.RecordStatus.Active
                    && item.ExpenseDate >= totalsFromColumn
                    && item.ExpenseDate <= asOfColumn)
                .Select(item => new ExpenseLite
                {
                    ExpenseGuid = item.ExpenseGuid,
                    StoreCode = item.StoreCode,
                    Category = item.Category,
                    ExpenseDate = item.ExpenseDate,
                    Amount = item.Amount,
                    ReviewStatus = item.ReviewStatus,
                })
                .ToListAsync(cancellationToken);

        return new PoolInputs
        {
            Connected = connected,
            Openings = openings,
            Archives = archives.ToLookup(archive => StoreKey(archive.StoreCode)),
            Selections = selections
                .GroupBy(item => (StoreKey(item.StoreCode), StoreCashClock.FromColumn(item.BusinessDate), item.DeviceCode))
                .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.SelectedAtUtc).First()),
            Deposits = deposits.ToLookup(item => StoreKey(item.StoreCode)),
            Expenses = expenses.ToLookup(item => StoreKey(item.StoreCode)),
        };
    }

    /// <summary>按「分店、营业日、设备」分组，用当前手选记录决定每组纳入哪几份存档。</summary>
    private static Dictionary<(DateOnly Date, string Device), ResolvedCloseSelection> ResolveCloses(
        string storeKey,
        IEnumerable<CashCloseArchive> archives,
        PoolInputs inputs
    )
    {
        var resolved = new Dictionary<(DateOnly Date, string Device), ResolvedCloseSelection>();
        foreach (var group in archives.GroupBy(archive => (archive.BusinessDate, archive.DeviceCode)))
        {
            inputs.Selections.TryGetValue((storeKey, group.Key.BusinessDate, group.Key.DeviceCode), out var current);
            resolved[group.Key] = CashCloseSelectionResolver.Resolve(group.ToList(), current);
        }

        return resolved;
    }

    /// <summary>单店截止 asOf（含当天）的现金池：期初日起的日结流入、存款、支出；没有期初时余额不可算。</summary>
    private static PoolSnapshot ComputePool(Store store, PoolInputs inputs, DateOnly asOf)
    {
        var key = StoreKey(store.StoreCode);
        var opening = inputs.Openings.GetValueOrDefault(key);
        var openingDate = OpeningDate(opening);
        // 没有期初时仍扫描最近一段日结，让「未存天数」等提示可用；存款、支出合计按全部有效记录统计。
        var scanFrom = openingDate ?? asOf.AddDays(-NoOpeningScanDays);
        var totalsFrom = openingDate ?? DateOnly.MinValue;

        var archives = inputs.Connected
            ? inputs.Archives[key].Where(archive => archive.BusinessDate >= scanFrom && archive.BusinessDate <= asOf).ToList()
            : new List<CashCloseArchive>();
        var inflowByDate = new Dictionary<DateOnly, decimal>();
        foreach (var (dayKey, selection) in ResolveCloses(key, archives, inputs))
        {
            inflowByDate[dayKey.Date] = inflowByDate.GetValueOrDefault(dayKey.Date) + selection.IncludedCash;
        }

        var deposits = inputs.Deposits[key]
            .Where(item => StoreCashClock.FromColumn(item.DepositDate) >= totalsFrom
                && StoreCashClock.FromColumn(item.DepositDate) <= asOf)
            .ToList();
        var expenses = inputs.Expenses[key]
            .Where(item => StoreCashClock.FromColumn(item.ExpenseDate) >= totalsFrom
                && StoreCashClock.FromColumn(item.ExpenseDate) <= asOf)
            .ToList();

        var depositTotal = deposits.Sum(item => item.TotalAmount);
        var expenseTotal = expenses.Sum(item => item.Amount);
        decimal? inflowTotal = inputs.Connected && opening is not null ? inflowByDate.Values.Sum() : null;
        decimal? pool = inputs.Connected && opening is not null
            ? decimal.Round(opening.Amount + inflowTotal!.Value - depositTotal - expenseTotal, 2)
            : null;

        return new PoolSnapshot
        {
            Store = store,
            AsOf = asOf,
            Connected = inputs.Connected,
            Opening = opening,
            ScanFrom = scanFrom,
            Archives = archives,
            InflowByDate = inflowByDate,
            Deposits = deposits,
            Expenses = expenses,
            InflowTotal = inflowTotal.HasValue ? decimal.Round(inflowTotal.Value, 2) : null,
            DepositTotal = decimal.Round(depositTotal, 2),
            ExpenseTotal = decimal.Round(expenseTotal, 2),
            PoolBalance = pool,
        };
    }

    private async Task<PoolSnapshot> BuildPoolAsync(Store store, DateOnly asOf, CancellationToken cancellationToken)
    {
        var inputs = await LoadPoolInputsAsync(new[] { store }, asOf, null, cancellationToken);
        return ComputePool(store, inputs, asOf);
    }

    /// <summary>未覆盖营业日：有日结现金，却不在任何有效存款的覆盖范围内（按日期升序）。</summary>
    private static List<KeyValuePair<DateOnly, decimal>> UncoveredDays(PoolSnapshot pool) =>
        pool.InflowByDate
            .Where(item => item.Value > 0 && FindCoveringDeposit(pool.Deposits, item.Key) is null)
            .OrderBy(item => item.Key)
            .ToList();

    private static bool IsDepositOverdue(DateOnly today, DateOnly? oldestUncovered) =>
        oldestUncovered.HasValue
        && today.DayNumber - oldestUncovered.Value.DayNumber > StoreCashConstants.DepositOverdueDays;

    /// <summary>
    /// 支出分类合计：四类固定顺序；T2 受当前账号可见窗口限制（展示层限制，支出真实合计另算）。
    /// </summary>
    private static List<CashExpenseCategoryTotalDto> CategoryTotals(
        IEnumerable<ExpenseLite> expenses,
        CashAccess access,
        DateOnly storeToday
    )
    {
        var visible = expenses
            .Where(item => CashVisibilityRules.CanSeeExpense(
                access,
                item.Category,
                StoreCashClock.FromColumn(item.ExpenseDate),
                storeToday))
            .ToList();
        return StoreCashConstants.ExpenseCategory.All
            .Select(category => new CashExpenseCategoryTotalDto
            {
                Category = category,
                Amount = decimal.Round(visible.Where(item => item.Category == category).Sum(item => item.Amount), 2),
            })
            .ToList();
    }

    // ───────────────────────── 多店总览 ─────────────────────────

    public async Task<ApiResponse<CashOverviewDto>> GetOverviewAsync(
        CashAccess access,
        DateOnly? from,
        DateOnly? to,
        IReadOnlyList<string>? storeCodes,
        CancellationToken cancellationToken
    )
    {
        if (!access.CanViewOverview)
        {
            return Fail<CashOverviewDto>("无权查看现金管理", StoreCashConstants.ErrorCodes.StoreForbidden);
        }

        if (from.HasValue != to.HasValue)
        {
            return Fail<CashOverviewDto>("请同时提供起止日期", StoreCashConstants.ErrorCodes.InvalidRequest);
        }

        var stores = await LoadVisibleStoresAsync(access, storeCodes, cancellationToken);
        var now = Now;
        var todays = stores.ToDictionary(store => store.StoreCode, store => StoreCashClock.GetStoreToday(store, now));
        // 默认区间：本月 1 日到今天（以悉尼日期为准；各店时区差不会超过一天）。
        var referenceToday = todays.Count > 0 ? todays.Values.Max() : StoreCashClock.GetStoreToday(null, now);
        var rangeTo = to ?? referenceToday;
        var rangeFrom = from ?? new DateOnly(rangeTo.Year, rangeTo.Month, 1);
        if (rangeTo < rangeFrom || rangeTo.DayNumber - rangeFrom.DayNumber >= MaxDailyRangeDays)
        {
            return Fail<CashOverviewDto>(
                $"日期范围无效，最多查询 {MaxDailyRangeDays} 天",
                StoreCashConstants.ErrorCodes.InvalidRequest
            );
        }

        var result = new CashOverviewDto
        {
            From = rangeFrom,
            To = rangeTo,
            DailyCloseConnected = _closeSource.IsConnected,
            T2Restricted = !access.AllStores,
        };
        if (stores.Count == 0)
        {
            result.Totals = BuildOverviewTotals(result.Rows);
            return ApiResponse<CashOverviewDto>.OK(result);
        }

        var asOfMax = todays.Values.Max();
        var inputs = await LoadPoolInputsAsync(stores, asOfMax > rangeTo ? asOfMax : rangeTo, rangeFrom, cancellationToken);
        foreach (var store in stores)
        {
            result.Rows.Add(BuildOverviewRow(store, todays[store.StoreCode], inputs, access, rangeFrom, rangeTo));
        }

        result.Totals = BuildOverviewTotals(result.Rows);
        return ApiResponse<CashOverviewDto>.OK(result);
    }

    /// <summary>当前账号可见的分店与请求分店取交集；请求为空表示全部可见分店。越权分店直接忽略，不报错。</summary>
    private async Task<List<Store>> LoadVisibleStoresAsync(
        CashAccess access,
        IReadOnlyList<string>? requestedCodes,
        CancellationToken cancellationToken
    )
    {
        var requested = (requestedCodes ?? Array.Empty<string>())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var allowed = requested.Count > 0
            ? requested.Where(access.CanAccessStore).ToList()
            : access.AllStores ? null : access.StoreCodes.ToList();
        if (allowed is { Count: 0 })
        {
            return new List<Store>();
        }

        var query = _db.Queryable<Store>().Where(item => !item.IsDeleted);
        if (allowed is not null)
        {
            query = query.Where(item => allowed.Contains(item.StoreCode));
        }

        return await query.OrderBy(item => item.StoreCode).ToListAsync(cancellationToken);
    }

    private static CashOverviewRowDto BuildOverviewRow(
        Store store,
        DateOnly storeToday,
        PoolInputs inputs,
        CashAccess access,
        DateOnly rangeFrom,
        DateOnly rangeTo
    )
    {
        var key = StoreKey(store.StoreCode);
        var pool = ComputePool(store, inputs, storeToday);
        var uncovered = UncoveredDays(pool);
        DateOnly? oldestUncovered = uncovered.Count > 0 ? uncovered[0].Key : null;

        // 区间统计：日结按营业日、存款按存款日、支出按发生日落在 [rangeFrom, rangeTo] 内。
        var rangeArchives = inputs.Connected
            ? inputs.Archives[key].Where(archive => archive.BusinessDate >= rangeFrom && archive.BusinessDate <= rangeTo).ToList()
            : new List<CashCloseArchive>();
        var resolved = ResolveCloses(key, rangeArchives, inputs);
        var closeDays = rangeArchives.Select(archive => archive.BusinessDate).ToHashSet();
        var openingDate = OpeningDate(pool.Opening);
        var missingCloseDays = 0;
        if (inputs.Connected)
        {
            for (var date = rangeFrom; date <= rangeTo && date < storeToday; date = date.AddDays(1))
            {
                // 期初之前的日子不算缺日结：上线前本来就没有要对的现金。
                if ((!openingDate.HasValue || date >= openingDate.Value) && !closeDays.Contains(date))
                {
                    missingCloseDays++;
                }
            }
        }

        var rangeDeposits = inputs.Deposits[key]
            .Where(item => StoreCashClock.FromColumn(item.DepositDate) >= rangeFrom
                && StoreCashClock.FromColumn(item.DepositDate) <= rangeTo)
            .ToList();
        var rangeExpenses = inputs.Expenses[key]
            .Where(item => StoreCashClock.FromColumn(item.ExpenseDate) >= rangeFrom
                && StoreCashClock.FromColumn(item.ExpenseDate) <= rangeTo)
            .ToList();
        var lastDeposit = inputs.Deposits[key].MaxBy(item => item.DepositDate);

        return new CashOverviewRowDto
        {
            StoreCode = store.StoreCode,
            StoreName = store.StoreName,
            StoreToday = storeToday,
            OpeningMissing = pool.Opening is null,
            PoolBalance = pool.PoolBalance,
            UncoveredDayCount = uncovered.Count,
            OldestUncoveredDate = oldestUncovered,
            DepositOverdue = IsDepositOverdue(storeToday, oldestUncovered),
            LastDepositDate = lastDeposit is null ? null : StoreCashClock.FromColumn(lastDeposit.DepositDate),
            InflowCash = inputs.Connected ? decimal.Round(resolved.Values.Sum(item => item.IncludedCash), 2) : null,
            CloseVariance = inputs.Connected
                ? decimal.Round(resolved.Values.Sum(item => item.Included.Sum(archive => archive.Variance)), 2)
                : null,
            CloseDayCount = closeDays.Count,
            MissingCloseDayCount = missingCloseDays,
            DepositTotal = decimal.Round(rangeDeposits.Sum(item => item.TotalAmount), 2),
            DepositCount = rangeDeposits.Count,
            ExpenseTotal = decimal.Round(rangeExpenses.Sum(item => item.Amount), 2),
            ExpenseByCategory = CategoryTotals(rangeExpenses, access, storeToday),
            // 只数当前账号看得到的存疑支出，不通过计数暴露窗口外的 T2。
            FlaggedExpenseCount = rangeExpenses.Count(item =>
                item.ReviewStatus == StoreCashConstants.ReviewStatus.Flagged
                && CashVisibilityRules.CanSeeExpense(
                    access,
                    item.Category,
                    StoreCashClock.FromColumn(item.ExpenseDate),
                    storeToday)),
        };
    }

    private static CashOverviewTotalsDto BuildOverviewTotals(IReadOnlyList<CashOverviewRowDto> rows)
    {
        // 任一店不可算（null）时合计也不可算，避免把不可算的店当 0 加进去。
        static decimal? SumNullable(IEnumerable<decimal?> values)
        {
            var list = values.ToList();
            return list.Count == 0 || list.Any(value => value is null)
                ? null
                : decimal.Round(list.Sum(value => value!.Value), 2);
        }

        return new CashOverviewTotalsDto
        {
            PoolBalance = SumNullable(rows.Select(row => row.PoolBalance)),
            InflowCash = SumNullable(rows.Select(row => row.InflowCash)),
            CloseVariance = SumNullable(rows.Select(row => row.CloseVariance)),
            DepositTotal = decimal.Round(rows.Sum(row => row.DepositTotal), 2),
            DepositCount = rows.Sum(row => row.DepositCount),
            ExpenseTotal = decimal.Round(rows.Sum(row => row.ExpenseTotal), 2),
            ExpenseByCategory = StoreCashConstants.ExpenseCategory.All
                .Select(category => new CashExpenseCategoryTotalDto
                {
                    Category = category,
                    Amount = decimal.Round(
                        rows.Sum(row => row.ExpenseByCategory.Where(item => item.Category == category).Sum(item => item.Amount)),
                        2
                    ),
                })
                .ToList(),
            UncoveredDayCount = rows.Sum(row => row.UncoveredDayCount),
            OverdueStoreCount = rows.Count(row => row.DepositOverdue),
            FlaggedExpenseCount = rows.Sum(row => row.FlaggedExpenseCount),
        };
    }
}
