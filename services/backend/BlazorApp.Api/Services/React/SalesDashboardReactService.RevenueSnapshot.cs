using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Caching.Memory;

namespace BlazorApp.Api.Services.React;

public partial class SalesDashboardReactService
{
    private sealed class RevenueSnapshotStoreRow
    {
        public DateTime Date { get; set; }
        public string BranchCode { get; set; } = string.Empty;
        public string BranchName { get; set; } = string.Empty;
        public decimal TotalAmount { get; set; }
        public int OrderCount { get; set; }
    }

    private sealed class RevenueSnapshotHourlyRow
    {
        public DateTime Date { get; set; }
        // SQL Server 批次可直接返回当前期(0)/同期(1)的预聚合行；SQLite/raw fallback 保持 null。
        public int? Period { get; set; }
        public int Hour { get; set; }
        public string? BranchCode { get; set; }
        public string? BranchName { get; set; }
        public decimal TotalAmount { get; set; }
        public int? OrderCount { get; set; }
    }

    private sealed class RevenueSnapshotHourlyCoverageRow
    {
        public DateTime Date { get; set; }
        public string BranchCode { get; set; } = string.Empty;
    }

    private sealed class RevenueSnapshotRefreshRow
    {
        public string StatisticType { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime? LastAggregatedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
    }

    private sealed class RevenueSnapshotStatus
    {
        public bool Complete { get; init; }
        public bool CurrentComplete { get; init; }
        public bool CompareComplete { get; init; }
        public bool CurrentHourlyComplete { get; init; }
        public bool CompareHourlyComplete { get; init; }
        public bool CompareStoreComplete { get; init; }
        public bool Refreshing { get; init; }
        public DateTime? UpdatedAt { get; init; }
        public string Version { get; init; } = string.Empty;
        /// <summary>快照完整但个别日期对账未通过时的提示；页面照常显示数据。</summary>
        public string? Warning { get; init; }
    }

    private sealed class RevenueSnapshotMetric
    {
        public decimal Revenue { get; init; }
        public int Orders { get; init; }
        public decimal Aov => Orders > 0 ? Revenue / Orders : 0m;
    }

    /// <summary>
    /// 一次读取营业额排行、分时和周层级数据。
    /// 这里刻意不调用旧的 Ensure/Refresh 路径：报表读取只消费已发布的统计表，
    /// 刷新中的不完整行由上一版完整 bundle 兜底。
    /// </summary>
    public async Task<RevenueReportSnapshotDto> GetRevenueReportSnapshotAsync(
        DateRangeDto dateRange,
        List<string>? branchCodes = null,
        List<string>? focusBranchCodes = null,
        int? topN = null,
        CancellationToken cancellationToken = default
    )
    {
        ValidateDateRange(dateRange);
        var normalizedBranches = NormalizeBranchCodes(branchCodes);
        var normalizedFocusBranches = NormalizeBranchCodes(focusBranchCodes);
        if (branchCodes != null && normalizedBranches.Count == 0)
            return new RevenueReportSnapshotDto { StatisticStatus = SalesStatisticRefreshStatus.Fresh };
        if (branchCodes != null && focusBranchCodes != null)
        {
            var authorized = normalizedBranches.ToHashSet(StringComparer.OrdinalIgnoreCase);
            normalizedFocusBranches = normalizedFocusBranches.Where(authorized.Contains).ToList();
        }
        // null 表示未聚焦；空集合表示聚焦范围已无授权门店，只清空分时与周层级，保留授权排行。
        var focusScope = focusBranchCodes == null ? null : normalizedFocusBranches;

        var startDate = dateRange.StartDate.Date;
        var endDate = dateRange.EndDate.Date;
        var compareStartDate = dateRange.CompareStartDate?.Date;
        var compareEndDate = dateRange.CompareEndDate?.Date;
        var cacheKey = BuildRevenueSnapshotCacheKey(
            dateRange,
            normalizedBranches,
            focusScope,
            topN
        );
        var useSqlServerBatch = _context.Db.CurrentConnectionConfig.DbType == SqlSugar.DbType.SqlServer;
        // 本周、本月等多日区间含今天时，今天只能和去年对应日的同一时刻比较，需要单独返回最后一天的数据。
        var includeLastDay = startDate < endDate && endDate == SalesStatisticsBusinessDate.Today();

        async Task<RevenueReportSnapshotDto> ReadAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<RevenueSnapshotRefreshRow> refreshRows;
            List<RevenueSnapshotStoreRow> storeRows;
            List<RevenueSnapshotHourlyRow> hourlyRows;
            List<RevenueSnapshotHourlyCoverageRow> hourlyCoverageRows;
            Dictionary<string, string> activeStoreNames;
            if (useSqlServerBatch)
            {
                var batch = await ReadRevenueSnapshotBatchAsync(
                    dateRange,
                    normalizedBranches,
                    focusScope,
                    branchCodes == null,
                    includeLastDay,
                    cancellationToken
                );
                refreshRows = batch.RefreshRows;
                storeRows = batch.StoreRows;
                hourlyRows = batch.HourlyRows;
                hourlyCoverageRows = batch.HourlyCoverageRows;
                activeStoreNames = batch.ActiveStoreNames;
            }
            else
            {
                refreshRows = await ReadRevenueSnapshotRefreshRowsAsync(
                    startDate,
                    endDate,
                    compareStartDate,
                    compareEndDate,
                    cancellationToken
                );
                storeRows = new List<RevenueSnapshotStoreRow>();
                hourlyRows = new List<RevenueSnapshotHourlyRow>();
                hourlyCoverageRows = new List<RevenueSnapshotHourlyCoverageRow>();
                activeStoreNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            var status = BuildRevenueSnapshotStatus(
                refreshRows,
                startDate,
                endDate,
                compareStartDate,
                compareEndDate,
                focusScope,
                storeRows,
                hourlyCoverageRows
            );

            if (_cache.TryGetValue<RevenueReportSnapshotDto>(cacheKey, out var cached)
                && cached != null
                && (!status.Complete || cached.CacheVersion == status.Version))
            {
                // 刷新中的旧 bundle 是已提交且完整的快照，不能把它降级为 Pending；
                // 只有当前没有旧 bundle 时才允许返回标记为 Pending 的当前行。
                if (!status.Complete)
                    return CloneRevenueSnapshot(cached, status);
                return cached;
            }

            if (branchCodes != null)
                activeStoreNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var displayBranchCodes = normalizedBranches.Count > 0
                ? normalizedBranches.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : activeStoreNames.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            // 全店角色未指定范围时，排行以启用门店目录为准；周层级与分时也必须同口径，
            // 否则已停用门店去年的营业额只进周层级和分时的同期，与 KPI、排行对不上。
            // 目录为空（SQLite 路径不读目录）时排行本身按数据中的门店展示，这里同样不收窄。
            var defaultDetailScope = branchCodes == null && focusBranchCodes == null && displayBranchCodes.Count > 0
                ? displayBranchCodes
                : null;

            if (!useSqlServerBatch)
            {
                var hourlyScope = focusScope ?? normalizedBranches;
                storeRows = await ReadRevenueStoreRowsAsync(
                    startDate,
                    endDate,
                    compareStartDate,
                    compareEndDate,
                    normalizedBranches,
                    cancellationToken
                );
                hourlyRows = hourlyScope.Count == 0 && focusBranchCodes != null
                    ? new List<RevenueSnapshotHourlyRow>()
                    : await ReadRevenueHourlyRowsAsync(
                        startDate,
                        endDate,
                        compareStartDate,
                        compareEndDate,
                        hourlyScope,
                        cancellationToken
                    );
                hourlyCoverageRows = hourlyRows
                    .Where(row => !string.IsNullOrWhiteSpace(row.BranchCode))
                    .Select(row => new RevenueSnapshotHourlyCoverageRow
                    {
                        Date = row.Date,
                        BranchCode = row.BranchCode!.Trim(),
                    })
                    .GroupBy(row => row.Date.Date)
                    .SelectMany(dateGroup => dateGroup
                        .GroupBy(row => row.BranchCode, StringComparer.OrdinalIgnoreCase)
                        .Select(branchGroup => branchGroup.First()))
                    .ToList();
            }

            status = BuildRevenueSnapshotStatus(
                refreshRows,
                startDate,
                endDate,
                compareStartDate,
                compareEndDate,
                focusScope,
                storeRows,
                hourlyCoverageRows
            );

            if (_cache.TryGetValue<RevenueReportSnapshotDto>(cacheKey, out cached)
                && cached != null
                && (!status.Complete || cached.CacheVersion == status.Version))
                return !status.Complete ? CloneRevenueSnapshot(cached, status) : cached;

            var branches = BuildRevenueBranches(
                storeRows,
                displayBranchCodes,
                activeStoreNames,
                startDate,
                endDate,
                compareStartDate,
                compareEndDate,
                topN
            );
            if (defaultDetailScope != null)
            {
                // 完整性状态已按原始覆盖行算完，这里只收窄展示用的分时行（含最后一天）。
                hourlyRows = hourlyRows
                    .Where(row => !string.IsNullOrWhiteSpace(row.BranchCode) && defaultDetailScope.Contains(row.BranchCode.Trim()))
                    .ToList();
            }
            var hourly = BuildRevenueHourly(hourlyRows, startDate, endDate, compareStartDate, compareEndDate);
            var lastDay = includeLastDay
                ? BuildRevenueLastDay(storeRows, hourlyRows, displayBranchCodes, activeStoreNames, endDate, compareEndDate)
                : null;
            var weeklyScope = focusBranchCodes != null
                ? normalizedFocusBranches.ToHashSet(StringComparer.OrdinalIgnoreCase)
                : branchCodes != null
                    ? normalizedBranches.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : defaultDetailScope;
            var weekly = BuildRevenueWeekly(
                storeRows
                    .Where(row => weeklyScope == null || weeklyScope.Contains(row.BranchCode))
                    .Select(row => new WeeklyPerformanceStatisticRow
                    {
                        Date = row.Date,
                        BranchCode = row.BranchCode,
                        BranchName = row.BranchName,
                        TotalAmount = row.TotalAmount,
                        OrderCount = row.OrderCount,
                        AverageOrderValue = row.OrderCount > 0 ? row.TotalAmount / row.OrderCount : 0m,
                    })
                    .ToList(),
                startDate,
                endDate,
                compareStartDate,
                compareEndDate
            );

            var result = new RevenueReportSnapshotDto
            {
                Branches = branches,
                LastDay = lastDay,
                Hourly = hourly,
                Weekly = weekly,
                StatisticsPending = !status.Complete,
                CurrentPeriodPending = !status.CurrentComplete,
                ComparePeriodPending = !status.CompareComplete,
                HourlyCurrentPending = !status.CurrentHourlyComplete,
                HourlyComparePending = !status.CompareHourlyComplete,
                WeeklyComparePending = !status.CompareStoreComplete,
                RefreshInProgress = status.Refreshing,
                StatisticStatus = status.Complete
                    ? SalesStatisticRefreshStatus.Fresh
                    : SalesStatisticRefreshStatus.Pending,
                StatisticMessage = status.Complete ? status.Warning : "统计快照尚未完成。",
                StatisticUpdatedAt = status.UpdatedAt,
                CacheVersion = status.Version,
                StatisticsExpectedBranchCount = displayBranchCodes.Count,
                StatisticsSnapshotBranchCount = branches.Count,
            };

            if (status.Complete)
            {
                _cache.Set(
                    cacheKey,
                    result,
                    new MemoryCacheEntryOptions()
                        .SetAbsoluteExpiration(RANKING_CACHE_DURATION)
                        .SetSlidingExpiration(TimeSpan.FromMinutes(5))
                );
            }

            return result;
        }

        // SQL Server reader 自己在批次内完成能力选择和事务边界，避免再做一次
        // snapshot capability probe/BEGIN/COMMIT；SQLite 继续复用通用只读快照包装。
        return useSqlServerBatch
            ? await ReadAsync()
            : await ReadReportSnapshotAsync(ReadAsync);
    }

    private static string BuildRevenueSnapshotCacheKey(
        DateRangeDto dateRange,
        IReadOnlyCollection<string> branchCodes,
        IReadOnlyCollection<string>? focusBranchCodes,
        int? topN
    )
    {
        static string ScopeKey(IReadOnlyCollection<string> codes) => codes.Count == 0
            ? "all"
            : string.Join(",", codes.OrderBy(code => code, StringComparer.OrdinalIgnoreCase));

        return $"RevenueReportSnapshot_{dateRange.StartDate:yyyyMMdd}_{dateRange.EndDate:yyyyMMdd}_"
            + $"{dateRange.CompareStartDate:yyyyMMdd}_{dateRange.CompareEndDate:yyyyMMdd}_"
            + $"{dateRange.CompareMode}_{ScopeKey(branchCodes)}_"
            + $"{(focusBranchCodes == null ? "all" : focusBranchCodes.Count == 0 ? "none" : ScopeKey(focusBranchCodes))}_"
            + $"{topN?.ToString() ?? "all"}";
    }

    private async Task<List<RevenueSnapshotRefreshRow>> ReadRevenueSnapshotRefreshRowsAsync(
        DateTime startDate,
        DateTime endDate,
        DateTime? compareStartDate,
        DateTime? compareEndDate,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hasCompare = compareStartDate.HasValue && compareEndDate.HasValue;
        return await _context.Db.Queryable<SalesStatisticRefreshState>()
            .Where(state =>
                (state.StatisticType == SalesStatisticType.StoreSales
                 || state.StatisticType == SalesStatisticType.HourlySales)
                && ((state.Date >= startDate && state.Date <= endDate)
                    || (hasCompare
                        && state.Date >= compareStartDate!.Value
                        && state.Date <= compareEndDate!.Value)))
            .Select(state => new RevenueSnapshotRefreshRow
            {
                StatisticType = state.StatisticType,
                Date = state.Date,
                Status = state.Status,
                LastAggregatedAtUtc = state.LastAggregatedAtUtc,
                CompletedAtUtc = state.CompletedAtUtc,
            })
            .ToListAsync();
    }

    private static RevenueSnapshotStatus BuildRevenueSnapshotStatus(
        IReadOnlyCollection<RevenueSnapshotRefreshRow> rows,
        DateTime startDate,
        DateTime endDate,
        DateTime? compareStartDate,
        DateTime? compareEndDate,
        IReadOnlyCollection<string>? hourlyBranchCodes,
        IReadOnlyCollection<RevenueSnapshotStoreRow>? storeRows,
        IReadOnlyCollection<RevenueSnapshotHourlyCoverageRow>? hourlyCoverageRows
    )
    {
        var currentDates = EnumerateRevenueSnapshotDates(startDate, endDate);
        var compareDates = compareStartDate.HasValue && compareEndDate.HasValue
            ? EnumerateRevenueSnapshotDates(compareStartDate.Value, compareEndDate.Value)
            : new List<DateTime>();
        var lookup = rows
            .GroupBy(row => (row.StatisticType, Date: row.Date.Date))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(row => row.LastAggregatedAtUtc).First());
        // 状态表晚于历史统计引入：2025 年仍有百余天只有 StoreSalesStatistic 行而没有状态行。
        // 早于最新状态日期的缺口视为历史已发布；只有排在最新状态之后的日期（如尚未排队的今天）才算未发布。
        var latestTrackedStoreDate = rows
            .Where(row => string.Equals(row.StatisticType, SalesStatisticType.StoreSales, StringComparison.OrdinalIgnoreCase))
            .Select(row => (DateTime?)row.Date.Date)
            .DefaultIfEmpty()
            .Max();
        var storeDatesWithRows = storeRows?
            .Select(row => row.Date.Date)
            .ToHashSet()
            ?? new HashSet<DateTime>();
        var reconciliationFailedDates = new List<DateTime>();
        bool IsStoreComplete(DateTime date)
        {
            if (!lookup.TryGetValue((SalesStatisticType.StoreSales, date.Date), out var storeState))
                return storeDatesWithRows.Contains(date.Date)
                    || (latestTrackedStoreDate.HasValue && date.Date < latestTrackedStoreDate.Value);
            if (!storeState.LastAggregatedAtUtc.HasValue)
                return false;
            if (string.Equals(storeState.Status, SalesStatisticRefreshStatus.Fresh, StringComparison.OrdinalIgnoreCase))
                return storeState.CompletedAtUtc.HasValue;
            // 对账失败但已完成聚合的日期仍是整天的分店营业额；只标注日期，不让整段不可读。
            if (string.Equals(storeState.Status, SalesStatisticRefreshStatus.Failed, StringComparison.OrdinalIgnoreCase))
            {
                reconciliationFailedDates.Add(date.Date);
                return true;
            }
            // 排队或运行中的日期在 SNAPSHOT 事务里读到的是上一版完整发布；版本哈希含状态，发布后自动失效缓存。
            return string.Equals(storeState.Status, SalesStatisticRefreshStatus.Queued, StringComparison.OrdinalIgnoreCase)
                || string.Equals(storeState.Status, SalesStatisticRefreshStatus.Running, StringComparison.OrdinalIgnoreCase)
                || string.Equals(storeState.Status, SalesStatisticRefreshStatus.ProvisionalFresh, StringComparison.OrdinalIgnoreCase);
        }

        bool IsHourlyComplete(DateTime date)
        {
            // 无可见聚焦门店时不需要分时统计，不能让无关门店的刷新状态挡住授权排行。
            if (hourlyBranchCodes is { Count: 0 })
                return true;
            var hourlyStateComplete = lookup.TryGetValue((SalesStatisticType.HourlySales, date.Date), out var hourlyState)
                && string.Equals(hourlyState.Status, SalesStatisticRefreshStatus.Fresh, StringComparison.OrdinalIgnoreCase)
                && hourlyState.LastAggregatedAtUtc.HasValue
                && hourlyState.CompletedAtUtc.HasValue;

            // Hourly 状态表是在历史 hourly 快照之后才引入的。已有分时行代表旧批次
            // 已提交；只有状态存在且明确处于 Running/Queued 时才必须等待新一版。
            var hourlyBusy = hourlyState != null && (
                string.Equals(hourlyState.Status, SalesStatisticRefreshStatus.Running, StringComparison.OrdinalIgnoreCase)
                || string.Equals(hourlyState.Status, SalesStatisticRefreshStatus.Queued, StringComparison.OrdinalIgnoreCase)
                || string.Equals(hourlyState.Status, SalesStatisticRefreshStatus.Pending, StringComparison.OrdinalIgnoreCase)
                || string.Equals(hourlyState.Status, SalesStatisticRefreshStatus.Failed, StringComparison.OrdinalIgnoreCase));
            // 分时表只有产生过销售的小时才有行：没有销售的门店日（含节假日全店休业）不应被要求有分时覆盖。
            var expectedHourlyBranches = storeRows?
                .Where(row => row.Date.Date == date.Date
                    && (row.TotalAmount != 0m || row.OrderCount > 0)
                    && (hourlyBranchCodes == null || hourlyBranchCodes.Contains(row.BranchCode)))
                .Select(row => row.BranchCode)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var actualHourlyBranches = hourlyCoverageRows?
                .Where(row => row.Date.Date == date.Date && !string.IsNullOrWhiteSpace(row.BranchCode))
                .Select(row => row.BranchCode.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hourlyCoverageComplete = expectedHourlyBranches.SetEquals(actualHourlyBranches);
            if (hourlyBusy)
                return false;
            if (hourlyStateComplete)
                return hourlyCoverageComplete;
            // 历史 hourly 状态表缺失时，仅接受与 StoreSales 同日有销售分店集合完全一致的旧行；
            // 当天完全没有销售时两边同为空集，同样视为已覆盖。
            return hourlyCoverageComplete;
        }

        var currentStoreComplete = currentDates.All(IsStoreComplete);
        var currentHourlyComplete = currentDates.All(IsHourlyComplete);
        var compareStoreComplete = compareDates.Count == 0 || compareDates.All(IsStoreComplete);
        var compareHourlyComplete = compareDates.Count == 0 || compareDates.All(IsHourlyComplete);
        // 分时缺口（新 POS 测试店某些日子没有分时行、分时刷新进行中）只影响时段面板，
        // 由 HourlyCurrentPending/HourlyComparePending 提示，不再把整页营业额降级为 Pending。
        var complete = currentStoreComplete && compareStoreComplete;
        var refreshing = rows.Any(row =>
            string.Equals(row.Status, SalesStatisticRefreshStatus.Queued, StringComparison.OrdinalIgnoreCase)
            || string.Equals(row.Status, SalesStatisticRefreshStatus.Running, StringComparison.OrdinalIgnoreCase));
        var updatedAt = rows
            .Select(row => row.CompletedAtUtc ?? row.LastAggregatedAtUtc)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty()
            .Min();
        var versionSource = string.Join(
            "|",
            rows.OrderBy(row => row.Date).ThenBy(row => row.StatisticType).Select(row =>
                $"{row.StatisticType}:{row.Date:yyyyMMdd}:{row.Status}:{row.LastAggregatedAtUtc?.Ticks ?? 0}:{row.CompletedAtUtc?.Ticks ?? 0}")
        );

        return new RevenueSnapshotStatus
        {
            Complete = complete,
            CurrentComplete = currentStoreComplete && currentHourlyComplete,
            CompareComplete = compareStoreComplete && compareHourlyComplete,
            CurrentHourlyComplete = currentHourlyComplete,
            CompareHourlyComplete = compareHourlyComplete,
            CompareStoreComplete = compareStoreComplete,
            Refreshing = refreshing || !complete || !currentHourlyComplete || !compareHourlyComplete,
            UpdatedAt = rows.Any(row => row.CompletedAtUtc.HasValue || row.LastAggregatedAtUtc.HasValue)
                ? DateTime.SpecifyKind(updatedAt, DateTimeKind.Utc)
                : null,
            Version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(versionSource))),
            Warning = DescribeReconciliationFailedDates(reconciliationFailedDates) is { } failed
                ? failed.Replace("商品统计与分店营业额对账未通过", "分店营业额统计对账未通过", StringComparison.Ordinal)
                : null,
        };
    }

    private static List<DateTime> EnumerateRevenueSnapshotDates(DateTime startDate, DateTime endDate)
    {
        var start = startDate.Date;
        var end = endDate.Date;
        if (start > end)
            return new List<DateTime>();
        return Enumerable.Range(0, (end - start).Days + 1)
            .Select(offset => start.AddDays(offset))
            .ToList();
    }

    private async Task<List<RevenueSnapshotStoreRow>> ReadRevenueStoreRowsAsync(
        DateTime startDate,
        DateTime endDate,
        DateTime? compareStartDate,
        DateTime? compareEndDate,
        IReadOnlyCollection<string> branchCodes,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = _context.Db.Queryable<StoreSalesStatistic>();
        query = compareStartDate.HasValue && compareEndDate.HasValue
            ? query.Where(row =>
                (row.Date >= startDate && row.Date <= endDate)
                || (row.Date >= compareStartDate.Value && row.Date <= compareEndDate.Value))
            : query.Where(row => row.Date >= startDate && row.Date <= endDate);
        if (branchCodes.Count > 0)
            query = query.Where(row => branchCodes.Contains(row.BranchCode));

        return await query.Select(row => new RevenueSnapshotStoreRow
        {
            Date = row.Date,
            BranchCode = row.BranchCode,
            BranchName = row.BranchName,
            TotalAmount = row.TotalAmount,
            OrderCount = row.OrderCount,
        }).ToListAsync();
    }

    private async Task<List<RevenueSnapshotHourlyRow>> ReadRevenueHourlyRowsAsync(
        DateTime startDate,
        DateTime endDate,
        DateTime? compareStartDate,
        DateTime? compareEndDate,
        IReadOnlyCollection<string> branchCodes,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = _context.Db.Queryable<HourlySalesStatistic>()
            .Where(row => row.BranchCode != null && row.BranchCode != "ALL");
        query = compareStartDate.HasValue && compareEndDate.HasValue
            ? query.Where(row =>
                (row.Date >= startDate && row.Date <= endDate)
                || (row.Date >= compareStartDate.Value && row.Date <= compareEndDate.Value))
            : query.Where(row => row.Date >= startDate && row.Date <= endDate);
        if (branchCodes.Count > 0)
            query = query.Where(row => branchCodes.Contains(row.BranchCode!));

        return await query.Select(row => new RevenueSnapshotHourlyRow
        {
            Date = row.Date,
            Period = null,
            Hour = row.Hour,
            BranchCode = row.BranchCode,
            BranchName = row.BranchName,
            TotalAmount = row.TotalAmount,
            OrderCount = row.OrderCount ?? 0,
        }).ToListAsync();
    }

    private static List<ExecutiveBranchPerformanceDto> BuildRevenueBranches(
        IReadOnlyCollection<RevenueSnapshotStoreRow> rows,
        HashSet<string> displayBranchCodes,
        Dictionary<string, string> activeStoreNames,
        DateTime startDate,
        DateTime endDate,
        DateTime? compareStartDate,
        DateTime? compareEndDate,
        int? topN
    )
    {
        var current = rows.Where(row => row.Date.Date >= startDate && row.Date.Date <= endDate)
            .GroupBy(row => row.BranchCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => new RevenueSnapshotMetric
            {
                Revenue = group.Sum(row => row.TotalAmount),
                Orders = group.Sum(row => row.OrderCount),
            }, StringComparer.OrdinalIgnoreCase);
        var compare = new Dictionary<string, RevenueSnapshotMetric>(StringComparer.OrdinalIgnoreCase);
        if (compareStartDate.HasValue && compareEndDate.HasValue)
        {
            compare = rows.Where(row => row.Date.Date >= compareStartDate.Value && row.Date.Date <= compareEndDate.Value)
                .GroupBy(row => row.BranchCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => new RevenueSnapshotMetric
                {
                    Revenue = group.Sum(row => row.TotalAmount),
                    Orders = group.Sum(row => row.OrderCount),
                }, StringComparer.OrdinalIgnoreCase);
        }

        var codes = displayBranchCodes.Count > 0
            ? displayBranchCodes
            : current.Keys.Union(compare.Keys, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(activeStoreNames, StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.BranchName) && !names.ContainsKey(row.BranchCode))
                names[row.BranchCode] = row.BranchName;
        }
        var result = codes.Select(code =>
        {
            current.TryGetValue(code, out var currentMetric);
            compare.TryGetValue(code, out var compareMetric);
            var name = names.GetValueOrDefault(code, code);
            return new ExecutiveBranchPerformanceDto
            {
                BranchCode = code,
                BranchName = string.IsNullOrWhiteSpace(name) ? code : name,
                Revenue = currentMetric?.Revenue ?? 0,
                RevenueLY = compareMetric?.Revenue ?? 0,
                OrderCount = currentMetric?.Orders ?? 0,
                OrderCountLY = compareMetric?.Orders ?? 0,
                Aov = currentMetric?.Aov ?? 0,
                AovLY = compareMetric?.Aov ?? 0,
            };
        }).OrderByDescending(item => item.Revenue)
            .ThenBy(item => item.BranchCode, StringComparer.OrdinalIgnoreCase)
            .Take(topN is > 0 ? topN.Value : int.MaxValue)
            .Select((item, index) =>
            {
                item.Rank = index + 1;
                return item;
            }).ToList();
        return result;
    }

    /// <summary>
    /// 区间最后一天（今天）与同期对应日：分店全天日统计 + 分店×小时统计。
    /// SQL Server 批次里这两天是单独的期间 2/3；SQLite 路径是逐日原始行，按日期筛选。
    /// </summary>
    private static RevenueLastDaySnapshotDto BuildRevenueLastDay(
        IReadOnlyCollection<RevenueSnapshotStoreRow> storeRows,
        IReadOnlyCollection<RevenueSnapshotHourlyRow> hourlyRows,
        HashSet<string> displayBranchCodes,
        Dictionary<string, string> activeStoreNames,
        DateTime lastDate,
        DateTime? compareLastDate
    )
    {
        var dayStoreRows = storeRows
            .Where(row => row.Date.Date == lastDate || (compareLastDate.HasValue && row.Date.Date == compareLastDate.Value))
            .ToList();
        var dayHourlyRows = hourlyRows
            .Where(row => row.Period.HasValue
                ? row.Period.Value >= 2
                : row.Date.Date == lastDate || (compareLastDate.HasValue && row.Date.Date == compareLastDate.Value))
            .Select(row => row.Period.HasValue
                // 期间 2/3 映射回 0/1，复用同一套本期/同期聚合。
                ? new RevenueSnapshotHourlyRow
                {
                    Date = row.Date,
                    Period = row.Period.Value - 2,
                    Hour = row.Hour,
                    BranchCode = row.BranchCode,
                    BranchName = row.BranchName,
                    TotalAmount = row.TotalAmount,
                    OrderCount = row.OrderCount,
                }
                : row)
            .ToList();
        return new RevenueLastDaySnapshotDto
        {
            Date = lastDate,
            CompareDate = compareLastDate,
            Branches = BuildRevenueBranches(dayStoreRows, displayBranchCodes, activeStoreNames, lastDate, lastDate,
                compareLastDate, compareLastDate, null),
            Hourly = BuildRevenueHourly(dayHourlyRows, lastDate, lastDate, compareLastDate, compareLastDate),
        };
    }

    private static List<ExecutiveHourlyTrafficDto> BuildRevenueHourly(
        IReadOnlyCollection<RevenueSnapshotHourlyRow> rows,
        DateTime startDate,
        DateTime endDate,
        DateTime? compareStartDate,
        DateTime? compareEndDate
    )
    {
        static bool IsInPeriod(
            RevenueSnapshotHourlyRow row,
            int period,
            DateTime periodStart,
            DateTime periodEnd,
            DateTime? compareStart,
            DateTime? compareEnd
        )
        {
            // 预聚合 SQL 行已经被 reader 标记为所属期；不要再用其代表日期做二次过滤。
            if (row.Period.HasValue)
                return row.Period.Value == period;
            var start = period == 0 ? periodStart : compareStart;
            var end = period == 0 ? periodEnd : compareEnd;
            return start.HasValue && end.HasValue
                && row.Date.Date >= start.Value.Date
                && row.Date.Date <= end.Value.Date;
        }

        var current = rows.Where(row => IsInPeriod(row, 0, startDate, endDate, compareStartDate, compareEndDate))
            .GroupBy(row => (BranchCode: row.BranchCode!.Trim().ToUpperInvariant(), row.Hour))
            .ToDictionary(group => group.Key, group => new RevenueSnapshotMetric
            {
                Revenue = group.Sum(row => row.TotalAmount),
                Orders = group.Sum(row => row.OrderCount ?? 0),
            });
        var compare = new Dictionary<(string BranchCode, int Hour), RevenueSnapshotMetric>();
        if (compareStartDate.HasValue && compareEndDate.HasValue)
        {
            compare = rows.Where(row => IsInPeriod(row, 1, startDate, endDate, compareStartDate, compareEndDate))
                .GroupBy(row => (BranchCode: row.BranchCode!.Trim().ToUpperInvariant(), row.Hour))
                .ToDictionary(group => group.Key, group => new RevenueSnapshotMetric
                {
                    Revenue = group.Sum(row => row.TotalAmount),
                    Orders = group.Sum(row => row.OrderCount ?? 0),
                });
        }
        var names = rows.Where(row => !string.IsNullOrWhiteSpace(row.BranchCode))
            .GroupBy(row => row.BranchCode!.Trim().ToUpperInvariant())
            .ToDictionary(group => group.Key, group => group.Select(row => row.BranchName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? group.Key);
        var keys = current.Keys.Union(compare.Keys).OrderBy(key => key.BranchCode).ThenBy(key => key.Hour).ToList();
        return keys.GroupBy(key => key.BranchCode).SelectMany(group =>
        {
            var max = group.Max(key => current.GetValueOrDefault(key)?.Revenue ?? 0m);
            return group.Select(key =>
            {
                current.TryGetValue(key, out var currentMetric);
                compare.TryGetValue(key, out var compareMetric);
                var revenue = currentMetric?.Revenue ?? 0;
                return new ExecutiveHourlyTrafficDto
                {
                    Hour = $"{key.Hour:D2}:00",
                    BranchCode = key.BranchCode,
                    BranchName = names.GetValueOrDefault(key.BranchCode, key.BranchCode),
                    Revenue = revenue,
                    RevenueLY = compareMetric?.Revenue ?? 0,
                    OrderCount = currentMetric?.Orders ?? 0,
                    OrderCountLY = compareMetric?.Orders ?? 0,
                    Percentage = max > 0 ? (int)(revenue * 100 / max) : 0,
                    IsPeak = max > 0 && revenue >= max * 0.8m,
                };
            });
        }).ToList();
    }

    private static List<WeeklyPerformanceHierarchyDto> BuildRevenueWeekly(
        IReadOnlyCollection<WeeklyPerformanceStatisticRow> rows,
        DateTime startDate,
        DateTime endDate,
        DateTime? compareStartDate,
        DateTime? compareEndDate
    )
    {
        var currentRows = rows.Where(row => row.Date.Date >= startDate && row.Date.Date <= endDate)
            .ToDictionary(row => (row.Date.Date, row.BranchCode.Trim().ToUpperInvariant()), row => row);
        var compareRows = rows.Where(row => compareStartDate.HasValue && compareEndDate.HasValue
                && row.Date.Date >= compareStartDate.Value && row.Date.Date <= compareEndDate.Value)
            .Select(row => new
            {
                Key = (startDate.AddDays((row.Date.Date - compareStartDate!.Value).Days), row.BranchCode.Trim().ToUpperInvariant()),
                Row = row,
            })
            .Where(item => item.Key.Item1 >= startDate && item.Key.Item1 <= endDate)
            .ToDictionary(item => item.Key, item => item.Row);

        var aligned = currentRows.Keys.Union(compareRows.Keys).Select(key =>
        {
            currentRows.TryGetValue(key, out var current);
            compareRows.TryGetValue(key, out var compare);
            return new WeeklyPerformanceAlignedRow
            {
                Date = key.Item1,
                BranchCode = current?.BranchCode ?? compare?.BranchCode ?? key.Item2,
                BranchName = current?.BranchName ?? compare?.BranchName ?? key.Item2,
                TotalAmount = current?.TotalAmount ?? 0,
                OrderCount = current?.OrderCount ?? 0,
                AverageOrderValue = current?.AverageOrderValue ?? 0,
                CompareTotalAmount = compare?.TotalAmount ?? 0,
                CompareOrderCount = compare?.OrderCount ?? 0,
                CompareAverageOrderValue = compare?.AverageOrderValue ?? 0,
            };
        }).ToList();

        var result = new List<WeeklyPerformanceHierarchyDto>();
        foreach (var weekGroup in aligned.GroupBy(row => new { Year = ISOWeek.GetYear(row.Date), Week = ISOWeek.GetWeekOfYear(row.Date) })
                     .OrderByDescending(group => group.Key.Year)
                     .ThenByDescending(group => group.Key.Week)
                     .ThenByDescending(group => group.Sum(row => row.TotalAmount)))
        {
            var weekKey = $"w{weekGroup.Key.Year}-{weekGroup.Key.Week:D2}";
            var week = new WeeklyPerformanceHierarchyDto
            {
                Key = weekKey,
                Level = "week",
                Hierarchy = $"{weekGroup.Key.Year}-W{weekGroup.Key.Week:D2}",
                Children = new List<WeeklyPerformanceHierarchyDto>(),
            };
            foreach (var branchGroup in weekGroup.GroupBy(row => row.BranchCode, StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(group => group.Sum(row => row.TotalAmount)))
            {
                var branch = new WeeklyPerformanceHierarchyDto
                {
                    Key = $"{weekKey}-{branchGroup.Key}",
                    Level = "branch",
                    Hierarchy = branchGroup.First().BranchName,
                    Children = branchGroup.OrderByDescending(row => row.Date).Select(row => new WeeklyPerformanceHierarchyDto
                    {
                        Key = $"{weekKey}-{branchGroup.Key}-{row.Date:yyyyMMdd}",
                        Level = "date",
                        Hierarchy = row.Date.ToString("yyyy-MM-dd"),
                        Revenue = row.TotalAmount,
                        RevenueLY = row.CompareTotalAmount,
                        Orders = row.OrderCount,
                        OrdersLY = row.CompareOrderCount,
                        Aov = row.AverageOrderValue,
                        AovLY = row.CompareAverageOrderValue,
                        YoYChange = row.CompareTotalAmount > 0
                            ? ((row.TotalAmount - row.CompareTotalAmount) / row.CompareTotalAmount) * 100
                            : null,
                    }).ToList(),
                };
                branch.Revenue = branch.Children!.Sum(child => child.Revenue);
                branch.RevenueLY = branch.Children.Sum(child => child.RevenueLY);
                branch.Orders = branch.Children.Sum(child => child.Orders);
                branch.OrdersLY = branch.Children.Sum(child => child.OrdersLY);
                branch.Aov = branch.Orders > 0 ? branch.Revenue / branch.Orders : 0;
                branch.AovLY = branch.OrdersLY > 0 ? branch.RevenueLY / branch.OrdersLY : 0;
                branch.YoYChange = branch.RevenueLY > 0
                    ? ((branch.Revenue - branch.RevenueLY) / branch.RevenueLY) * 100
                    : null;
                week.Children.Add(branch);
            }
            week.Revenue = week.Children.Sum(child => child.Revenue);
            week.RevenueLY = week.Children.Sum(child => child.RevenueLY);
            week.Orders = week.Children.Sum(child => child.Orders);
            week.OrdersLY = week.Children.Sum(child => child.OrdersLY);
            week.Aov = week.Orders > 0 ? week.Revenue / week.Orders : 0;
            week.AovLY = week.OrdersLY > 0 ? week.RevenueLY / week.OrdersLY : 0;
            week.YoYChange = week.RevenueLY > 0
                ? ((week.Revenue - week.RevenueLY) / week.RevenueLY) * 100
                : null;
            result.Add(week);
        }
        return result;
    }

    private static RevenueReportSnapshotDto CloneRevenueSnapshot(
        RevenueReportSnapshotDto source,
        RevenueSnapshotStatus refreshStatus
    ) => new()
    {
        Branches = source.Branches,
        LastDay = source.LastDay,
        Hourly = source.Hourly,
        Weekly = source.Weekly,
        StatisticsPending = false,
        CurrentPeriodPending = false,
        ComparePeriodPending = source.ComparePeriodPending,
        HourlyCurrentPending = false,
        HourlyComparePending = source.HourlyComparePending,
        WeeklyComparePending = source.WeeklyComparePending,
        RefreshInProgress = true,
        StatisticStatus = SalesStatisticRefreshStatus.Fresh,
        StatisticMessage = "后台正在发布下一版统计，当前显示最近完整快照。",
        StatisticUpdatedAt = source.StatisticUpdatedAt,
        CacheVersion = source.CacheVersion,
        StatisticsExpectedBranchCount = source.StatisticsExpectedBranchCount,
        StatisticsSnapshotBranchCount = source.StatisticsSnapshotBranchCount,
    };
}
