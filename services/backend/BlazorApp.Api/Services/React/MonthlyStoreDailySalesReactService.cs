using System.Globalization;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Extensions.Caching.Memory;
using SqlSugar;

namespace BlazorApp.Api.Services.React;

/// <summary>
/// 月度日销售下载页的数据：某个月内每家分店逐日的营业额、刷卡、现金。
///
/// 口径（与营业额日报一致，不另起炉灶）：
/// - 营业额取 StoreSalesStatistic.TotalAmount。统计任务里这个值 = 对 Status 为 1/4 订单的 payment_detail.Amount 求和，
///   所以按支付方式拆分后，刷卡 + 现金 + 其他（代金券等）恒等于营业额；退款支付是负数，自然按净额计入。
/// - 刷卡/现金读 POSM：payment_detail 关联 sales_order，订单状态、OrderTime 墙钟日、分店解析（订单无分店时按设备注册表回填）
///   与统计任务逐项相同。
/// - 2024-09-14 至 2026-04-30 的营业额还叠加了 HBSales 旧系统（没有支付方式），这些日子 POSM 合计小于营业额，
///   没法给出可靠的刷卡/现金，只返回营业额，拆分留空（null），不做估算。
/// - 空值语义见 <see cref="MonthlyStoreDailySalesDayDto"/>：缺数是 null，休业日是 0。
/// </summary>
public sealed class MonthlyStoreDailySalesReactService : IMonthlyStoreDailySalesReactService
{
    /// <summary>刷卡/现金按月聚合要扫一个月的支付明细，结果只有几百行，缓存几分钟即可。</summary>
    private static readonly TimeSpan PaymentSummaryCacheDuration = TimeSpan.FromMinutes(5);

    /// <summary>营业额与 POSM 支付合计的容差：两边都是同一批支付金额求和，新 POS 的分币误差远小于它。</summary>
    private const decimal ReconcileTolerance = 0.01m;

    private readonly SqlSugarContext _context;
    private readonly POSMSqlSugarContext _posmContext;
    private readonly IMemoryCache _cache;

    public MonthlyStoreDailySalesReactService(
        SqlSugarContext context,
        POSMSqlSugarContext posmContext,
        IMemoryCache cache
    )
    {
        _context = context;
        _posmContext = posmContext;
        _cache = cache;
    }

    /// <summary>测试钩子：悉尼业务日期的"今天"。今天还没统计完，不计入月度数据。</summary>
    internal Func<DateTime> BusinessTodayProvider { get; set; } = SalesStatisticsBusinessDate.Today;

    public async Task<MonthlyStoreDailySalesDto> GetMonthlyStoreDailySalesAsync(
        string month,
        IReadOnlyCollection<string>? branchScope,
        bool forceRefresh,
        CancellationToken cancellationToken
    )
    {
        var monthStart = ParseMonth(month);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var result = new MonthlyStoreDailySalesDto
        {
            Month = monthStart.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            DaysInMonth = monthEnd.Day,
        };

        // 已计入到昨天为止：今天的统计还在滚动，当月进行中时不能把半天数据当整天交出去。
        var yesterday = BusinessTodayProvider().Date.AddDays(-1);
        var countedThrough = yesterday < monthEnd ? yesterday : monthEnd;
        if (countedThrough < monthStart)
            return result; // 整个月都在未来（或本月 1 日就是今天）：没有任何已计入的日子

        result.CountedThroughDate = FormatDate(countedThrough);
        result.CountedDays = (countedThrough - monthStart).Days + 1;
        if (branchScope is { Count: 0 })
            return result; // 没有可见分店

        cancellationToken.ThrowIfCancellationRequested();
        var endExclusive = countedThrough.AddDays(1);
        var scope = branchScope == null
            ? null
            : new HashSet<string>(branchScope.Select(code => code.Trim()), StringComparer.OrdinalIgnoreCase);

        // 营业额统计行：一个月只有「分店数 × 天数」行，整月读出再在内存里按授权范围过滤，
        // 这样"某天是否整体已发布"的判断不会因用户只看一两家店而改变。
        var statRows = await _context.Db.Queryable<StoreSalesStatistic>()
            .Where(row => row.Date >= monthStart && row.Date < endExclusive)
            .Select(row => new MonthlyStatRow
            {
                Date = row.Date,
                BranchCode = row.BranchCode,
                BranchName = row.BranchName,
                TotalAmount = row.TotalAmount,
            })
            .ToListAsync();
        var stateRows = await _context.Db.Queryable<SalesStatisticRefreshState>()
            .Where(state =>
                state.StatisticType == SalesStatisticType.StoreSales
                && state.Date >= monthStart
                && state.Date < endExclusive)
            .Select(state => new MonthlyStateRow
            {
                Date = state.Date,
                Status = state.Status,
                LastAggregatedAtUtc = state.LastAggregatedAtUtc,
                CompletedAtUtc = state.CompletedAtUtc,
            })
            .ToListAsync();

        var payments = await GetPaymentSummaryAsync(monthStart, endExclusive, forceRefresh);
        var published = new PublishedDays(stateRows, statRows);

        var revenueByStoreDay = statRows
            .GroupBy(row => (Branch: row.BranchCode.Trim().ToUpperInvariant(), Day: row.Date.Date))
            .ToDictionary(group => group.Key, group => group.Sum(row => row.TotalAmount));

        var stores = statRows
            .Where(row => !string.IsNullOrWhiteSpace(row.BranchCode)
                && (scope == null || scope.Contains(row.BranchCode.Trim())))
            .GroupBy(row => row.BranchCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var storeRows in stores)
        {
            var branchCode = storeRows.Key;
            var branchKey = branchCode.ToUpperInvariant();
            var store = new MonthlyStoreDailySalesStoreDto
            {
                BranchCode = branchCode,
                // 分店名取最近一天统计行里的名字（改名后以新名为准）
                BranchName = storeRows
                    .OrderByDescending(row => row.Date)
                    .Select(row => row.BranchName?.Trim())
                    .FirstOrDefault(name => !string.IsNullOrEmpty(name)) ?? branchCode,
            };

            for (var day = monthStart; day <= countedThrough; day = day.AddDays(1))
            {
                store.Days.Add(BuildDay(
                    day,
                    branchKey,
                    published,
                    revenueByStoreDay,
                    payments));
            }

            result.Stores.Add(store);
        }

        return result;
    }

    /// <summary>单店单日：先判断营业额有没有发布，再判断刷卡/现金能否与营业额对上。</summary>
    private static MonthlyStoreDailySalesDayDto BuildDay(
        DateTime day,
        string branchKey,
        PublishedDays published,
        IReadOnlyDictionary<(string Branch, DateTime Day), decimal> revenueByStoreDay,
        IReadOnlyDictionary<(string Branch, DateTime Day), PaymentTotals> payments
    )
    {
        var dto = new MonthlyStoreDailySalesDayDto { Date = FormatDate(day) };
        // 这一天还没有已发布的统计：营业额缺数，不能写 0（休业日才是 0）。
        if (!published.IsPublished(day))
            return dto;

        var revenue = revenueByStoreDay.TryGetValue((branchKey, day), out var amount) ? amount : 0m;
        dto.Revenue = Round2(revenue);

        payments.TryGetValue((branchKey, day), out var paid);
        // POSM 支付合计与营业额对不上，说明营业额里含 HBSales 旧系统来源，或统计还没追上 POSM 的迟到上传；
        // 这时刷卡/现金只是一部分，宁可留空也不给一个看似完整的数字。
        if (Math.Abs(paid.Total - revenue) > ReconcileTolerance)
            return dto;

        dto.Card = Round2(paid.Card);
        dto.Cash = Round2(paid.Cash);
        dto.Other = Round2(revenue - paid.Card - paid.Cash);
        return dto;
    }

    /// <summary>
    /// 读取（并缓存）整月 POSM 支付汇总：按 分店 × 日期 汇总 总额 / 刷卡 / 现金。
    /// 缓存的是不含授权范围的整月结果，不同用户共用；缓存键含截止日期，跨天自动换键。
    /// </summary>
    private async Task<IReadOnlyDictionary<(string Branch, DateTime Day), PaymentTotals>> GetPaymentSummaryAsync(
        DateTime monthStart,
        DateTime endExclusive,
        bool forceRefresh
    )
    {
        var cacheKey = $"MonthlyStoreDailySales.Payments:{monthStart:yyyyMMdd}:{endExclusive:yyyyMMdd}";
        if (!forceRefresh
            && _cache.TryGetValue<IReadOnlyDictionary<(string Branch, DateTime Day), PaymentTotals>>(cacheKey, out var cached)
            && cached != null)
        {
            return cached;
        }

        // 与 UpdateDailyStatistics / 分店日统计同一过滤：已支付(1)与分期付(4)的订单，OrderTime 是门店墙钟时间，直接按日比较。
        var rows = await _posmContext.Db.Queryable<PaymentDetail, SalesOrder>(
                (pd, so) => pd.OrderGuid == so.OrderGuid)
            .Where((pd, so) =>
                so.Status != null
                && (so.Status == 1 || so.Status == 4)
                && so.OrderTime != null
                && so.OrderTime >= monthStart
                && so.OrderTime < endExclusive)
            .GroupBy((pd, so) => new
            {
                Date = so.OrderTime!.Value.Date,
                so.BranchCode,
                so.DeviceCode,
                pd.PaymentMethod,
            })
            .Select((pd, so) => new MonthlyPaymentRow
            {
                Date = so.OrderTime!.Value.Date,
                BranchCode = so.BranchCode,
                DeviceCode = so.DeviceCode,
                PaymentMethod = pd.PaymentMethod,
                Amount = SqlFunc.AggregateSum(pd.Amount) ?? 0m,
            })
            .ToListAsync();

        // 订单头没有分店编码时，统计任务按设备注册表回填分店；这里必须同样回填，否则这部分金额会对不上营业额。
        var deviceBranchMap = await SalesStatisticsProductStoreDailySourceQueries.LoadDeviceBranchMapAsync(
            _posmContext,
            rows.Where(row => string.IsNullOrWhiteSpace(row.BranchCode)).Select(row => row.DeviceCode));

        var totals = new Dictionary<(string Branch, DateTime Day), PaymentTotals>();
        foreach (var row in rows)
        {
            var branch = SalesStatisticsCodeRules.ResolveBranchCode(row.BranchCode, row.DeviceCode, deviceBranchMap);
            if (string.IsNullOrWhiteSpace(branch))
                continue; // 统计任务同样丢弃解析不出分店的行

            var key = (branch.ToUpperInvariant(), row.Date.Date);
            totals.TryGetValue(key, out var current);
            totals[key] = new PaymentTotals(
                current.Total + row.Amount,
                current.Card + (row.PaymentMethod == (int)PaymentMethod.Card ? row.Amount : 0m),
                current.Cash + (row.PaymentMethod == (int)PaymentMethod.Cash ? row.Amount : 0m));
        }

        _cache.Set(cacheKey, (IReadOnlyDictionary<(string Branch, DateTime Day), PaymentTotals>)totals, PaymentSummaryCacheDuration);
        return totals;
    }

    private static DateTime ParseMonth(string? month)
    {
        if (!DateTime.TryParseExact(
                month?.Trim(),
                "yyyy-MM",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed)
            || parsed.Year < 2020)
        {
            throw new ArgumentException("月份格式应为 yyyy-MM");
        }

        return new DateTime(parsed.Year, parsed.Month, 1);
    }

    private static string FormatDate(DateTime date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static decimal Round2(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// 哪些日期的分店营业额已经发布。规则与营业额报表的 IsStoreComplete 一致：
    /// 有状态行的按状态判定；没有状态行的（状态表晚于历史统计引入）只要当天已有统计行，
    /// 或早于最新有状态的日期，就视为历史已发布。
    /// </summary>
    private sealed class PublishedDays
    {
        private readonly Dictionary<DateTime, MonthlyStateRow> _states;
        private readonly HashSet<DateTime> _datesWithRows;
        private readonly DateTime? _latestTracked;

        public PublishedDays(IEnumerable<MonthlyStateRow> stateRows, IEnumerable<MonthlyStatRow> statRows)
        {
            _states = stateRows
                .GroupBy(row => row.Date.Date)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(row => row.LastAggregatedAtUtc).First());
            // 全部分店的统计行日期，不受授权范围影响
            _datesWithRows = statRows.Select(row => row.Date.Date).ToHashSet();
            _latestTracked = _states.Count == 0 ? null : _states.Keys.Max();
        }

        public bool IsPublished(DateTime day)
        {
            if (!_states.TryGetValue(day, out var state))
                return _datesWithRows.Contains(day) || (_latestTracked.HasValue && day < _latestTracked.Value);
            if (!state.LastAggregatedAtUtc.HasValue)
                return false;

            return state.Status switch
            {
                SalesStatisticRefreshStatus.Fresh => state.CompletedAtUtc.HasValue,
                // 对账失败但已完成聚合的日期仍是整天的分店营业额，报表也照常展示
                SalesStatisticRefreshStatus.Failed => true,
                // 正在重算：数据库里是上一版已发布数据，但前提是这天确实已有统计行
                SalesStatisticRefreshStatus.Queued
                    or SalesStatisticRefreshStatus.Running
                    or SalesStatisticRefreshStatus.ProvisionalFresh => _datesWithRows.Contains(day),
                _ => false,
            };
        }
    }

    internal sealed class MonthlyStatRow
    {
        public DateTime Date { get; set; }
        public string BranchCode { get; set; } = string.Empty;
        public string? BranchName { get; set; }
        public decimal TotalAmount { get; set; }
    }

    internal sealed class MonthlyStateRow
    {
        public DateTime Date { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime? LastAggregatedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
    }

    internal sealed class MonthlyPaymentRow
    {
        public DateTime Date { get; set; }
        public string? BranchCode { get; set; }
        public string? DeviceCode { get; set; }
        public int PaymentMethod { get; set; }
        public decimal Amount { get; set; }
    }

    internal readonly record struct PaymentTotals(decimal Total, decimal Card, decimal Cash);
}
