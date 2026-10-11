using BlazorApp.Api.Data;
using BlazorApp.Api.Services.Background;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;

namespace BlazorApp.Api.Services
{
    /// <summary>
    /// 销售统计日级对齐查询与补算服务。
    /// </summary>
    public class SalesStatisticsAlignmentService
    {
        public const string DailyFullRefreshLeaseTaskType = "DailyStatisticsAlignmentFullRefresh";

        private const decimal AmountTolerance = 0.01m;
        private readonly SqlSugarContext _context;
        private readonly POSMSqlSugarContext _posmContext;
        private readonly ScheduledTaskLeaseService _leaseService;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly ILogger<SalesStatisticsAlignmentService> _logger;

        public SalesStatisticsAlignmentService(
            SqlSugarContext context,
            POSMSqlSugarContext posmContext,
            ScheduledTaskLeaseService leaseService,
            IServiceScopeFactory serviceScopeFactory,
            ILogger<SalesStatisticsAlignmentService> logger
        )
        {
            _context = context;
            _posmContext = posmContext;
            _leaseService = leaseService;
            _serviceScopeFactory = serviceScopeFactory;
            _logger = logger;
        }

        public async Task<DailyStatisticsAlignmentResponseDto> GetDailyAlignmentAsync(
            DateTime startDate,
            DateTime endDate
        )
        {
            var start = startDate.Date;
            var end = endDate.Date;
            var dates = EnumerateDates(start, end);
            var baselineMetrics = await QueryStoreMetricsAsync(start, end);
            var metricMaps = new Dictionary<string, Dictionary<DateTime, StatisticMetric>>
            {
                [SalesStatisticType.DailySales] = await QueryDailyMetricsAsync(start, end),
                [SalesStatisticType.HourlySales] = await QueryHourlyMetricsAsync(start, end),
                [SalesStatisticType.StoreSales] = baselineMetrics,
                [SalesStatisticType.SupplierSales] = await QuerySupplierMetricsAsync(start, end),
                [SalesStatisticType.StoreSupplierSales] = await QueryStoreSupplierMetricsAsync(start, end),
                [SalesStatisticType.ProductStoreDaily] = await QueryProductStoreDailyMetricsAsync(start, end),
                [SalesStatisticType.AustralianSupplierStoreSales] = await QueryAustralianSupplierMetricsAsync(start, end),
                [SalesStatisticType.ChinaSupplierStoreSales] = await QueryChinaSupplierMetricsAsync(start, end),
            };
            var states = await QueryRefreshStatesAsync(start, end);
            var sourceWatermarks = await QuerySourceWatermarksAsync(start, end);
            var runningScopeKeys = (await _leaseService.GetRunningLeasesAsync(DailyFullRefreshLeaseTaskType, start, end))
                .Select(lease => lease.ScopeKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var rows = dates
                .OrderByDescending(date => date)
                .Select(date =>
                {
                    var baseline = GetMetric(baselineMetrics, date);
                    var sourceWatermark = sourceWatermarks.GetValueOrDefault(date);
                    var details = BuildDetails(date, baseline, sourceWatermark, metricMaps, states);
                    var isRunning = runningScopeKeys.Contains(FormatScopeKey(date));
                    var overallStatus = ResolveOverallStatus(details, isRunning);
                    var (reason, remediation) = BuildRowReasonAndRemediation(overallStatus, details);

                    return new DailyStatisticsAlignmentRowDto
                    {
                        Date = date,
                        OverallStatus = overallStatus,
                        Reason = reason,
                        Remediation = remediation,
                        BaselineAmount = baseline.TotalAmount,
                        BaselineQuantity = baseline.TotalQuantity,
                        BaselineOrderCount = baseline.OrderCount,
                        AbnormalTables = details
                            .Where(detail =>
                                !detail.DiagnosticOnly
                                && detail.Status != StatisticsAlignmentStatus.Aligned
                            )
                            .Select(detail => detail.DisplayName)
                            .ToList(),
                        LatestSourceWatermark = sourceWatermark,
                        LastCheckedAtUtc = details
                            .Select(detail => detail.LastCheckedAtUtc)
                            .Where(value => value.HasValue)
                            .DefaultIfEmpty()
                            .Max(),
                        Details = details,
                    };
                })
                .ToList();

            return new DailyStatisticsAlignmentResponseDto
            {
                StartDate = start,
                EndDate = end,
                GeneratedAtUtc = DateTime.UtcNow,
                Overview = BuildOverview(rows),
                Rows = rows,
            };
        }

        public virtual async Task<DailyStatisticsAlignmentRecalculateResponseDto> RecalculateAsync(
            IEnumerable<DateTime> dates,
            int maxConcurrency
        )
        {
            var targetDates = dates
                .Select(date => date.Date)
                .Distinct()
                .OrderBy(date => date)
                .ToList();
            var concurrency = Math.Clamp(maxConcurrency, 1, 10);
            var jobId = Guid.NewGuid();
            var processedDates = new List<DateTime>();
            var skippedDates = new List<DateTime>();
            var failedDates = new List<DateTime>();
            var syncLock = new object();

            await Parallel.ForEachAsync(
                targetDates,
                new ParallelOptions { MaxDegreeOfParallelism = concurrency },
                async (date, cancellationToken) =>
                {
                    using var scope = _serviceScopeFactory.CreateScope();
                    var statisticsJobService = scope.ServiceProvider.GetRequiredService<SalesStatisticsJobService>();

                    try
                    {
                        var result = await statisticsJobService.BatchFullRefreshConcurrent(date, date, 1);
                        if (result.SkippedDates.Contains(FormatScopeKey(date)))
                        {
                            lock (syncLock)
                            {
                                skippedDates.Add(date);
                            }
                            return;
                        }

                        if (result.Success)
                        {
                            lock (syncLock)
                            {
                                processedDates.Add(date);
                            }
                            return;
                        }

                        lock (syncLock)
                        {
                            failedDates.Add(date);
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (syncLock)
                        {
                            failedDates.Add(date);
                        }
                        _logger.LogError(ex, "统计对齐补算失败: {Date}", date);
                    }
                }
            );

            return new DailyStatisticsAlignmentRecalculateResponseDto
            {
                JobId = jobId,
                // 有日期被 guard/租约跳过时，补算并未完整完成，不能向后台日志或调用方
                // 宣称 Success；这与实际失败区分在 SkippedDates 中。
                Success = failedDates.Count == 0
                    && skippedDates.Count == 0
                    && processedDates.Count == targetDates.Count,
                Message = BuildRecalculateMessage(processedDates.Count, skippedDates.Count, failedDates.Count),
                ProcessedDates = processedDates.OrderBy(date => date).ToList(),
                SkippedDates = skippedDates.OrderBy(date => date).ToList(),
                FailedDates = failedDates.OrderBy(date => date).ToList(),
            };
        }

        private List<DailyStatisticsAlignmentTableDetailDto> BuildDetails(
            DateTime date,
            StatisticMetric baseline,
            DateTime? sourceWatermark,
            Dictionary<string, Dictionary<DateTime, StatisticMetric>> metricMaps,
            Dictionary<string, SalesStatisticRefreshState> states
        )
        {
            return new List<DailyStatisticsAlignmentTableDetailDto>
            {
                BuildDetail(date, "DailySalesStatistic", "日统计", SalesStatisticType.DailySales, baseline, sourceWatermark, metricMaps, states, true, true),
                BuildDetail(date, "HourlySalesStatistic", "小时统计", SalesStatisticType.HourlySales, baseline, sourceWatermark, metricMaps, states, true, true),
                BuildDetail(date, "StoreSalesStatistic", "分店统计", SalesStatisticType.StoreSales, baseline, sourceWatermark, metricMaps, states, true, true),
                BuildDetail(date, "SupplierSalesStatistic", "供应商统计", SalesStatisticType.SupplierSales, baseline, sourceWatermark, metricMaps, states, true, false),
                BuildDetail(date, "StoreSupplierSalesDetail", "门店供应商统计", SalesStatisticType.StoreSupplierSales, baseline, sourceWatermark, metricMaps, states, true, false),
                BuildDetail(date, "ProductStoreDailySalesStatistic", "商品/分店每日统计", SalesStatisticType.ProductStoreDaily, baseline, sourceWatermark, metricMaps, states, true, false),
                BuildDetail(date, "AustralianSupplierStoreSalesDetail", "澳洲供应商拆分", SalesStatisticType.AustralianSupplierStoreSales, baseline, sourceWatermark, metricMaps, states, false, false),
                BuildDetail(date, "ChinaSupplierStoreSalesDetail", "中国供应商拆分", SalesStatisticType.ChinaSupplierStoreSales, baseline, sourceWatermark, metricMaps, states, false, false),
            };
        }

        private static DailyStatisticsAlignmentTableDetailDto BuildDetail(
            DateTime date,
            string tableName,
            string displayName,
            string statisticType,
            StatisticMetric baseline,
            DateTime? sourceWatermark,
            Dictionary<string, Dictionary<DateTime, StatisticMetric>> metricMaps,
            Dictionary<string, SalesStatisticRefreshState> states,
            bool compareWithBaseline,
            bool compareOrderCount
        )
        {
            var metric = GetMetric(metricMaps[statisticType], date);
            var state = states.GetValueOrDefault(BuildStateKey(statisticType, date));
            var amountDifference = metric.TotalAmount - baseline.TotalAmount;
            var quantityDifference = metric.TotalQuantity - baseline.TotalQuantity;
            var orderCountDifference = metric.OrderCount - baseline.OrderCount;
            var status = ResolveDetailStatus(
                metric,
                baseline,
                state,
                sourceWatermark,
                compareWithBaseline,
                compareOrderCount,
                amountDifference,
                quantityDifference,
                orderCountDifference
            );
            var (reason, remediation) = BuildReasonAndRemediation(
                status,
                displayName,
                amountDifference,
                quantityDifference,
                orderCountDifference,
                state?.ErrorMessage,
                !compareWithBaseline
            );

            return new DailyStatisticsAlignmentTableDetailDto
            {
                StatisticType = statisticType,
                TableName = tableName,
                DisplayName = displayName,
                Status = status,
                Reason = reason,
                Remediation = remediation,
                DiagnosticOnly = !compareWithBaseline,
                RowCount = metric.RowCount,
                TotalAmount = metric.TotalAmount,
                TotalQuantity = metric.TotalQuantity,
                OrderCount = metric.OrderCount,
                SourceWatermark = sourceWatermark,
                LastAggregatedAtUtc = state?.LastAggregatedAtUtc ?? metric.LastAggregatedAt,
                LastCheckedAtUtc = state?.LastCheckedAtUtc,
                AmountDifference = amountDifference,
                QuantityDifference = quantityDifference,
                OrderCountDifference = orderCountDifference,
                ErrorMessage = state?.ErrorMessage,
            };
        }

        private static string ResolveDetailStatus(
            StatisticMetric metric,
            StatisticMetric baseline,
            SalesStatisticRefreshState? state,
            DateTime? sourceWatermark,
            bool compareWithBaseline,
            bool compareOrderCount,
            decimal amountDifference,
            decimal quantityDifference,
            int orderCountDifference
        )
        {
            if (state?.Status == SalesStatisticRefreshStatus.Failed)
            {
                return StatisticsAlignmentStatus.Failed;
            }

            var hasExpectedData =
                baseline.RowCount > 0
                || baseline.TotalAmount != 0m
                || baseline.TotalQuantity != 0m
                || baseline.OrderCount != 0
                || sourceWatermark.HasValue;
            if (compareWithBaseline && hasExpectedData && metric.RowCount == 0)
            {
                return StatisticsAlignmentStatus.Missing;
            }

            var lastStatisticWatermark = state?.LastSourceUploadTime ?? metric.LastSourceUploadTime ?? metric.LastAggregatedAt;
            if (
                sourceWatermark.HasValue
                && lastStatisticWatermark.HasValue
                && sourceWatermark.Value > lastStatisticWatermark.Value.AddMinutes(1)
            )
            {
                return StatisticsAlignmentStatus.Stale;
            }

            if (
                compareWithBaseline
                && (
                    Math.Abs(amountDifference) > AmountTolerance
                    || Math.Abs(quantityDifference) > 0m
                    || (compareOrderCount && orderCountDifference != 0)
                )
            )
            {
                return StatisticsAlignmentStatus.Mismatch;
            }

            return StatisticsAlignmentStatus.Aligned;
        }

        private static (string Reason, string Remediation) BuildRowReasonAndRemediation(
            string overallStatus,
            List<DailyStatisticsAlignmentTableDetailDto> details
        )
        {
            if (overallStatus == StatisticsAlignmentStatus.Running)
            {
                return ("当天已有统计任务运行中", "等待任务完成；租约过期后可重新补算");
            }

            var firstAbnormal = details.FirstOrDefault(detail =>
                !detail.DiagnosticOnly && detail.Status != StatisticsAlignmentStatus.Aligned
            );
            return firstAbnormal == null
                ? ("已对齐", "无需处理")
                : (firstAbnormal.Reason, firstAbnormal.Remediation);
        }

        private static (string Reason, string Remediation) BuildReasonAndRemediation(
            string status,
            string displayName,
            decimal amountDifference,
            decimal quantityDifference,
            int orderCountDifference,
            string? errorMessage,
            bool diagnosticOnly
        )
        {
            return status switch
            {
                StatisticsAlignmentStatus.Missing => (
                    $"{displayName} 当天没有统计记录",
                    "点击一键补算异常日期；仍缺失时检查对应统计任务日志和 POSM 原始数据"
                ),
                StatisticsAlignmentStatus.Stale => (
                    "POSM 上传水位晚于该统计表最后处理水位",
                    "补算当天统计；门店延迟上传时等待 POSM 水位稳定后再补算"
                ),
                StatisticsAlignmentStatus.Mismatch => (
                    $"核心指标不一致：金额差 {amountDifference:N2}，数量差 {quantityDifference:N2}，订单差 {orderCountDifference}",
                    "先补算当天；仍不一致时检查 POSM 订单、商品供应商映射和空分店回填"
                ),
                StatisticsAlignmentStatus.Failed => (
                    string.IsNullOrWhiteSpace(errorMessage) ? "刷新状态记录为失败" : errorMessage,
                    "查看任务日志失败原因，修复后补算当天"
                ),
                StatisticsAlignmentStatus.Running => (
                    "当天已有统计任务运行中",
                    "等待任务完成；租约过期后可重新补算"
                ),
                _ when diagnosticOnly => (
                    "拆分诊断项，不参与总账强制对齐",
                    "如拆分异常，检查供应商映射和 LocalSupplierCode=200 国内拆分"
                ),
                _ => ("已对齐", "无需处理"),
            };
        }

        private static string ResolveOverallStatus(
            List<DailyStatisticsAlignmentTableDetailDto> details,
            bool isRunning
        )
        {
            if (isRunning)
            {
                return StatisticsAlignmentStatus.Running;
            }

            var coreDetails = details.Where(detail => !detail.DiagnosticOnly).ToList();
            foreach (var status in new[]
            {
                StatisticsAlignmentStatus.Failed,
                StatisticsAlignmentStatus.Missing,
                StatisticsAlignmentStatus.Stale,
                StatisticsAlignmentStatus.Mismatch,
            })
            {
                if (coreDetails.Any(detail => detail.Status == status))
                {
                    return status;
                }
            }

            return StatisticsAlignmentStatus.Aligned;
        }

        private static DailyStatisticsAlignmentOverviewDto BuildOverview(
            List<DailyStatisticsAlignmentRowDto> rows
        )
        {
            return new DailyStatisticsAlignmentOverviewDto
            {
                AlignedDays = rows.Count(row => row.OverallStatus == StatisticsAlignmentStatus.Aligned),
                AbnormalDays = rows.Count(row => row.OverallStatus != StatisticsAlignmentStatus.Aligned),
                MissingTableCount = rows.Sum(row => row.Details.Count(detail => detail.Status == StatisticsAlignmentStatus.Missing)),
                MaxAmountDifference = rows
                    .SelectMany(row => row.Details.Where(detail => !detail.DiagnosticOnly))
                    .Select(detail => Math.Abs(detail.AmountDifference))
                    .DefaultIfEmpty(0m)
                    .Max(),
                LatestSourceWatermark = rows
                    .Select(row => row.LatestSourceWatermark)
                    .Where(value => value.HasValue)
                    .DefaultIfEmpty()
                    .Max(),
            };
        }

        private async Task<Dictionary<string, SalesStatisticRefreshState>> QueryRefreshStatesAsync(
            DateTime start,
            DateTime end
        )
        {
            var rows = await _context.Db.Queryable<SalesStatisticRefreshState>()
                .Where(state => state.Date >= start && state.Date <= end)
                .ToListAsync();
            return rows
                .Where(state => SalesStatisticType.DailyAlignmentTypes.Contains(state.StatisticType))
                .GroupBy(state => BuildStateKey(state.StatisticType, state.Date))
                .ToDictionary(group => group.Key, group => group.First());
        }

        private async Task<Dictionary<DateTime, DateTime?>> QuerySourceWatermarksAsync(
            DateTime start,
            DateTime end
        )
        {
            var endExclusive = end.AddDays(1);
            var rows = new List<SourceWatermarkRow>();

            rows.AddRange(await _posmContext.Db.Queryable<SalesOrder>()
                .Where(order =>
                    order.Status != null
                    && SalesOrderStatusRules.NetSalesStatuses.Contains(order.Status)
                    && order.OrderTime != null
                    && order.OrderTime >= start
                    && order.OrderTime < endExclusive
                )
                .GroupBy(order => new { Date = order.OrderTime!.Value.Date })
                .Select(order => new SourceWatermarkRow
                {
                    Date = order.OrderTime!.Value.Date,
                    Watermark = SqlFunc.AggregateMax(order.LastUploadTime),
                })
                .ToListAsync());

            rows.AddRange(await _posmContext.Db.Queryable<PaymentDetail, SalesOrder>(
                    (payment, order) => payment.OrderGuid == order.OrderGuid
                )
                .Where((payment, order) =>
                    order.Status != null
                    && SalesOrderStatusRules.NetSalesStatuses.Contains(order.Status)
                    && order.OrderTime != null
                    && order.OrderTime >= start
                    && order.OrderTime < endExclusive
                )
                .GroupBy((payment, order) => new { Date = order.OrderTime!.Value.Date })
                .Select((payment, order) => new SourceWatermarkRow
                {
                    Date = order.OrderTime!.Value.Date,
                    Watermark = SqlFunc.AggregateMax(payment.LastUploadTime),
                })
                .ToListAsync());

            rows.AddRange(await _posmContext.Db.Queryable<SalesOrderDetail, SalesOrder>(
                    (detail, order) => detail.OrderGuid == order.OrderGuid
                )
                .Where((detail, order) =>
                    order.Status != null
                    && SalesOrderStatusRules.NetSalesStatuses.Contains(order.Status)
                    && order.OrderTime != null
                    && order.OrderTime >= start
                    && order.OrderTime < endExclusive
                )
                .GroupBy((detail, order) => new { Date = order.OrderTime!.Value.Date })
                .Select((detail, order) => new SourceWatermarkRow
                {
                    Date = order.OrderTime!.Value.Date,
                    Watermark = SqlFunc.AggregateMax(detail.LastUploadTime),
                })
                .ToListAsync());

            return rows
                .Where(row => row.Watermark.HasValue)
                .GroupBy(row => row.Date.Date)
                .ToDictionary(
                    group => group.Key,
                    group => (DateTime?)group.Max(row => row.Watermark!.Value)
                );
        }

        private async Task<Dictionary<DateTime, StatisticMetric>> QueryDailyMetricsAsync(DateTime start, DateTime end)
        {
            var rows = await _context.Db.Queryable<DailySalesStatistic>()
                .Where(row => row.Date >= start && row.Date <= end)
                .GroupBy(row => row.Date)
                .Select(row => new StatisticMetric
                {
                    Date = row.Date,
                    RowCount = SqlFunc.AggregateCount(row.Date),
                    TotalAmount = SqlFunc.AggregateSum(row.TotalAmount),
                    TotalQuantity = SqlFunc.AggregateSum(row.TotalQuantity),
                    OrderCount = SqlFunc.AggregateSum(row.OrderCount),
                    LastAggregatedAt = SqlFunc.AggregateMax(row.UpdateTime),
                })
                .ToListAsync();
            return ToMetricMap(rows);
        }

        private async Task<Dictionary<DateTime, StatisticMetric>> QueryHourlyMetricsAsync(DateTime start, DateTime end)
        {
            var rows = await _context.Db.Queryable<HourlySalesStatistic>()
                .Where(row => row.Date >= start && row.Date <= end && row.BranchCode == "ALL")
                .GroupBy(row => row.Date)
                .Select(row => new StatisticMetric
                {
                    Date = row.Date,
                    RowCount = SqlFunc.AggregateCount(row.Date),
                    TotalAmount = SqlFunc.AggregateSum(row.TotalAmount),
                    TotalQuantity = SqlFunc.AggregateSum(row.TotalQuantity),
                    OrderCount = SqlFunc.AggregateSum(row.OrderCount) ?? 0,
                    LastAggregatedAt = SqlFunc.AggregateMax(row.UpdateTime),
                })
                .ToListAsync();
            return ToMetricMap(rows);
        }

        private async Task<Dictionary<DateTime, StatisticMetric>> QueryStoreMetricsAsync(DateTime start, DateTime end)
        {
            var rows = await _context.Db.Queryable<StoreSalesStatistic>()
                .Where(row => row.Date >= start && row.Date <= end)
                .GroupBy(row => row.Date)
                .Select(row => new StatisticMetric
                {
                    Date = row.Date,
                    RowCount = SqlFunc.AggregateCount(row.Date),
                    TotalAmount = SqlFunc.AggregateSum(row.TotalAmount),
                    TotalQuantity = SqlFunc.AggregateSum(row.TotalQuantity),
                    OrderCount = SqlFunc.AggregateSum(row.OrderCount),
                    LastAggregatedAt = SqlFunc.AggregateMax(row.UpdateTime),
                })
                .ToListAsync();
            return ToMetricMap(rows);
        }

        private async Task<Dictionary<DateTime, StatisticMetric>> QuerySupplierMetricsAsync(DateTime start, DateTime end)
        {
            var rows = await _context.Db.Queryable<SupplierSalesStatistic>()
                .Where(row => row.Date >= start && row.Date <= end && row.IsDomestic == false)
                .GroupBy(row => row.Date)
                .Select(row => new StatisticMetric
                {
                    Date = row.Date,
                    RowCount = SqlFunc.AggregateCount(row.Date),
                    TotalAmount = SqlFunc.AggregateSum(row.TotalAmount),
                    TotalQuantity = SqlFunc.AggregateSum(row.TotalQuantity),
                    OrderCount = SqlFunc.AggregateSum(row.OrderCount) ?? 0,
                    LastAggregatedAt = SqlFunc.AggregateMax(row.UpdateTime),
                })
                .ToListAsync();
            return ToMetricMap(rows);
        }

        private async Task<Dictionary<DateTime, StatisticMetric>> QueryStoreSupplierMetricsAsync(DateTime start, DateTime end)
        {
            var rows = await _context.Db.Queryable<StoreSupplierSalesDetail>()
                // 当前门店供应商统计会把 LocalSupplierCode=200 的销售归到具体中国供应商行，
                // 不再额外保留本地 200 行；这里按全量汇总才能与分店总账对齐。
                .Where(row => row.Date >= start && row.Date <= end)
                .GroupBy(row => row.Date)
                .Select(row => new StatisticMetric
                {
                    Date = row.Date,
                    RowCount = SqlFunc.AggregateCount(row.Date),
                    TotalAmount = SqlFunc.AggregateSum(row.TotalAmount),
                    TotalQuantity = SqlFunc.AggregateSum(row.TotalQuantity),
                    OrderCount = SqlFunc.AggregateSum(row.OrderCount) ?? 0,
                    LastAggregatedAt = SqlFunc.AggregateMax(row.UpdateTime),
                })
                .ToListAsync();
            return ToMetricMap(rows);
        }

        private async Task<Dictionary<DateTime, StatisticMetric>> QueryProductStoreDailyMetricsAsync(DateTime start, DateTime end)
        {
            var rows = await _context.Db.Queryable<ProductStoreDailySalesStatistic>()
                .Where(row => row.Date >= start && row.Date <= end)
                .GroupBy(row => row.Date)
                .Select(row => new StatisticMetric
                {
                    Date = row.Date,
                    RowCount = SqlFunc.AggregateCount(row.Date),
                    TotalAmount = SqlFunc.AggregateSum(row.TotalAmount),
                    TotalQuantity = SqlFunc.AggregateSum(row.TotalQuantity),
                    OrderCount = SqlFunc.AggregateSum(row.OrderCount),
                    LastAggregatedAt = SqlFunc.AggregateMax(row.UpdateTime),
                    LastSourceUploadTime = SqlFunc.AggregateMax(row.LastSourceUploadTime),
                })
                .ToListAsync();
            return ToMetricMap(rows);
        }

        private async Task<Dictionary<DateTime, StatisticMetric>> QueryAustralianSupplierMetricsAsync(DateTime start, DateTime end)
        {
            var rows = await _context.Db.Queryable<AustralianSupplierStoreSalesDetail>()
                .Where(row => row.Date >= start && row.Date <= end)
                .GroupBy(row => row.Date)
                .Select(row => new StatisticMetric
                {
                    Date = row.Date,
                    RowCount = SqlFunc.AggregateCount(row.Date),
                    TotalAmount = SqlFunc.AggregateSum(row.TotalAmount),
                    TotalQuantity = SqlFunc.AggregateSum(row.TotalQuantity),
                    OrderCount = SqlFunc.AggregateSum(row.OrderCount) ?? 0,
                    LastAggregatedAt = SqlFunc.AggregateMax(row.UpdateTime),
                })
                .ToListAsync();
            return ToMetricMap(rows);
        }

        private async Task<Dictionary<DateTime, StatisticMetric>> QueryChinaSupplierMetricsAsync(DateTime start, DateTime end)
        {
            var rows = await _context.Db.Queryable<ChinaSupplierStoreSalesDetail>()
                .Where(row => row.Date >= start && row.Date <= end)
                .GroupBy(row => row.Date)
                .Select(row => new StatisticMetric
                {
                    Date = row.Date,
                    RowCount = SqlFunc.AggregateCount(row.Date),
                    TotalAmount = SqlFunc.AggregateSum(row.TotalAmount),
                    TotalQuantity = SqlFunc.AggregateSum(row.TotalQuantity),
                    OrderCount = SqlFunc.AggregateSum(row.OrderCount) ?? 0,
                    LastAggregatedAt = SqlFunc.AggregateMax(row.UpdateTime),
                })
                .ToListAsync();
            return ToMetricMap(rows);
        }

        private static Dictionary<DateTime, StatisticMetric> ToMetricMap(List<StatisticMetric> rows)
        {
            return rows
                .GroupBy(row => row.Date.Date)
                .ToDictionary(group => group.Key, group => group.First());
        }

        private static StatisticMetric GetMetric(Dictionary<DateTime, StatisticMetric> metrics, DateTime date)
        {
            return metrics.GetValueOrDefault(date.Date) ?? new StatisticMetric { Date = date.Date };
        }

        private static List<DateTime> EnumerateDates(DateTime startDate, DateTime endDate)
        {
            var dates = new List<DateTime>();
            for (var date = startDate.Date; date <= endDate.Date; date = date.AddDays(1))
            {
                dates.Add(date);
            }
            return dates;
        }

        private static string FormatScopeKey(DateTime date)
        {
            return date.Date.ToString("yyyy-MM-dd");
        }

        private static string BuildStateKey(string statisticType, DateTime date)
        {
            return $"{statisticType}|{date:yyyy-MM-dd}";
        }

        private static string BuildRecalculateMessage(int processedCount, int skippedCount, int failedCount)
        {
            if (failedCount > 0)
            {
                return $"已补算 {processedCount} 天，跳过 {skippedCount} 天，失败 {failedCount} 天";
            }

            if (skippedCount > 0)
            {
                return $"已补算 {processedCount} 天，跳过 {skippedCount} 天运行中的统计";
            }

            return $"已补算 {processedCount} 天统计";
        }

        private sealed class StatisticMetric
        {
            public DateTime Date { get; set; }
            public int RowCount { get; set; }
            public decimal TotalAmount { get; set; }
            public decimal TotalQuantity { get; set; }
            public int OrderCount { get; set; }
            public DateTime? LastAggregatedAt { get; set; }
            public DateTime? LastSourceUploadTime { get; set; }
        }

        private sealed class SourceWatermarkRow
        {
            public DateTime Date { get; set; }
            public DateTime? Watermark { get; set; }
        }
    }

    public static class StatisticsAlignmentStatus
    {
        public const string Aligned = "Aligned";
        public const string Missing = "Missing";
        public const string Stale = "Stale";
        public const string Mismatch = "Mismatch";
        public const string Failed = "Failed";
        public const string Running = "Running";
    }

    public class DailyStatisticsAlignmentResponseDto
    {
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public DateTime GeneratedAtUtc { get; set; }
        public DailyStatisticsAlignmentOverviewDto Overview { get; set; } = new();
        public List<DailyStatisticsAlignmentRowDto> Rows { get; set; } = new();
    }

    public class DailyStatisticsAlignmentOverviewDto
    {
        public int AlignedDays { get; set; }
        public int AbnormalDays { get; set; }
        public int MissingTableCount { get; set; }
        public decimal MaxAmountDifference { get; set; }
        public DateTime? LatestSourceWatermark { get; set; }
    }

    public class DailyStatisticsAlignmentRowDto
    {
        public DateTime Date { get; set; }
        public string OverallStatus { get; set; } = StatisticsAlignmentStatus.Aligned;
        public string Reason { get; set; } = string.Empty;
        public string Remediation { get; set; } = string.Empty;
        public decimal BaselineAmount { get; set; }
        public decimal BaselineQuantity { get; set; }
        public int BaselineOrderCount { get; set; }
        public List<string> AbnormalTables { get; set; } = new();
        public DateTime? LatestSourceWatermark { get; set; }
        public DateTime? LastCheckedAtUtc { get; set; }
        public List<DailyStatisticsAlignmentTableDetailDto> Details { get; set; } = new();
    }

    public class DailyStatisticsAlignmentTableDetailDto
    {
        public string StatisticType { get; set; } = string.Empty;
        public string TableName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Status { get; set; } = StatisticsAlignmentStatus.Aligned;
        public string Reason { get; set; } = string.Empty;
        public string Remediation { get; set; } = string.Empty;
        public bool DiagnosticOnly { get; set; }
        public int RowCount { get; set; }
        public decimal TotalAmount { get; set; }
        public decimal TotalQuantity { get; set; }
        public int OrderCount { get; set; }
        public DateTime? SourceWatermark { get; set; }
        public DateTime? LastAggregatedAtUtc { get; set; }
        public DateTime? LastCheckedAtUtc { get; set; }
        public decimal AmountDifference { get; set; }
        public decimal QuantityDifference { get; set; }
        public int OrderCountDifference { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class DailyStatisticsAlignmentRecalculateResponseDto
    {
        public Guid JobId { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<DateTime> ProcessedDates { get; set; } = new();
        public List<DateTime> SkippedDates { get; set; } = new();
        public List<DateTime> FailedDates { get; set; } = new();
    }
}
