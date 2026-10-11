using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services.Background;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBSalesRecord;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;

namespace BlazorApp.Api.Services
{
    /// <summary>销售统计垂直切片：SalesStatisticsDailyHourlySlice。</summary>
    internal sealed class SalesStatisticsDailyHourlySlice : SalesStatisticsSliceBase
    {
        private readonly SalesStatisticsStoreDailySlice _storeDaily;
        private readonly SalesStatisticsProductStoreDailyEntrySlice _productEntry;

        public SalesStatisticsDailyHourlySlice(
            SalesStatisticsSliceContext shared,
            SalesStatisticsStoreDailySlice storeDaily,
            SalesStatisticsProductStoreDailyEntrySlice productEntry)
            : base(shared)
        {
            _storeDaily = storeDaily;
            _productEntry = productEntry;
        }

    internal static async Task ExecuteTransactionSafelyAsync(
        Func<Task> beginAsync,
        Func<Task> workAsync,
        Func<Task> commitAsync,
        Func<Task> rollbackAsync,
        ILogger logger,
        string operationName
    ) => await SalesStatisticsTransactionExecutor.ExecuteAsync(
        beginAsync,
        workAsync,
        commitAsync,
        rollbackAsync,
        logger,
        operationName
    );

    /// <summary>
    /// 更新当前小时统计数据
    /// 包括分时统计、每日统计、分店统计、澳洲供应商门店统计、中国供应商门店统计
    /// </summary>
    public async Task UpdateCurrentHourStatistics()
    {
        try
        {
            // 获取当前时间
            var now = SalesStatisticsBusinessDate.Now();
            var currentHour = now.Hour;
            var currentDate = now.Date;

            _logger.LogInformation(
                "开始更新当前小时统计数据: {Date} {Hour}",
                currentDate,
                currentHour
            );

            // 更新分时统计数据
            await UpdateHourlyStatistics(currentDate, currentHour);
            // 更新每日统计数据
            await UpdateDailyStatistics(currentDate.ToString("yyyy-MM-dd"));
            // 当天分店、商品和两类供应商由同一来源快照原子发布，避免先切换分店表。
            await _productEntry.UpdateProductStoreDailyStatistics(currentDate);

            _logger.LogInformation(
                "当前小时统计数据更新完成: {Date} {Hour}",
                currentDate,
                currentHour
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新当前小时统计数据失败");
            throw;
        }
    }

    /// <summary>
    /// 更新每日统计数据
    /// 从POSM系统聚合当日销售订单的汇总数据
    /// </summary>
    /// <param name="dateStr">日期字符串（格式yyyy-MM-dd），为空则更新当天</param>
    public async Task UpdateDailyStatistics(string? dateStr = null)
    {
        try
        {
            // 确定目标日期
            var date = string.IsNullOrEmpty(dateStr)
                ? SalesStatisticsBusinessDate.Today()
                : DateTime.Parse(dateStr).Date;

            _logger.LogInformation("开始更新每日统计数据: {Date}", date);

            var statistic = await BuildDailySalesStatisticAsync(_posmContext, date, DateTime.Now);

            await ReplaceDailySalesStatisticAsync(_context, _logger, date, statistic);

            _logger.LogInformation("每日统计数据更新完成: {Date}", date);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新每日统计数据失败: {Date}", dateStr);
            throw;
        }
    }

    internal static async Task<DailySalesStatistic?> BuildDailySalesStatisticAsync(
        POSMSqlSugarContext posmContext,
        DateTime date,
        DateTime updateTime
    )
    {
        var targetDate = date.Date;
        var nextDate = targetDate.AddDays(1);

        // 金额与销量按净额统计：并入退款单（Status=3，负数），订单数与 SKU 数仍只数 1/4。
        var paymentSummary = await posmContext.Db.Queryable<PaymentDetail, SalesOrder>(
                (pd, so) => pd.OrderGuid == so.OrderGuid
            )
            .Where((pd, so) =>
                so.Status != null
                && SalesOrderStatusRules.NetSalesStatuses.Contains(so.Status)
                && so.OrderTime != null
                && so.OrderTime >= targetDate
                && so.OrderTime < nextDate
            )
            .GroupBy((pd, so) => so.OrderTime!.Value.Date)
            .Select((pd, so) => new
            {
                TotalAmount = SqlFunc.AggregateSum(pd.Amount) ?? 0m,
            })
            .FirstAsync();

        var quantitySummary = await posmContext.Db.Queryable<SalesOrderDetail, SalesOrder>(
                (d, so) => d.OrderGuid == so.OrderGuid
            )
            .Where((d, so) =>
                so.Status != null
                && SalesOrderStatusRules.NetSalesStatuses.Contains(so.Status)
                && so.OrderTime != null
                && so.OrderTime >= targetDate
                && so.OrderTime < nextDate
            )
            .GroupBy((d, so) => so.OrderTime!.Value.Date)
            .Select((d, so) => new
            {
                TotalQuantity = SqlFunc.AggregateSum(d.Quantity ?? 0),
            })
            .FirstAsync();

        var skuCodes = await posmContext.Db.Queryable<SalesOrderDetail, SalesOrder>(
                (detail, order) => detail.OrderGuid == order.OrderGuid)
            .Where((detail, order) =>
                order.Status != null && (order.Status == 1 || order.Status == 4)
                && order.OrderTime != null && order.OrderTime >= targetDate && order.OrderTime < nextDate
                && detail.ProductCode != null && detail.ProductCode != string.Empty)
            .Select((detail, order) => detail.ProductCode)
            .Distinct()
            .ToListAsync();
        var orderRows = await posmContext.Db.Queryable<SalesOrder>()
            .Where(so =>
                so.Status != null
                && (so.Status == 1 || so.Status == 4)
                && so.OrderTime != null
                && so.OrderTime >= targetDate
                && so.OrderTime < nextDate
            )
            .GroupBy(so => so.OrderGuid)
            .Select(so => new StoreStatisticOrderRow
            {
                OrderGuid = so.OrderGuid,
            })
            .ToListAsync();

        var totalAmount = paymentSummary?.TotalAmount ?? 0m;
        var totalQuantity = quantitySummary?.TotalQuantity ?? 0;
        // 日统计金额、数量、订单数拆开在 SQL 端聚合，避免拆分支付或明细行把非金额指标放大。
        var orderCount = orderRows
            .Select(row => row.OrderGuid)
            .Where(orderGuid => !string.IsNullOrWhiteSpace(orderGuid))
            .Count();

        if (totalAmount == 0m && totalQuantity == 0 && orderCount == 0)
        {
            return null;
        }

        return new DailySalesStatistic
        {
            Date = targetDate,
            TotalAmount = totalAmount,
            TotalQuantity = totalQuantity,
            OrderCount = orderCount,
            SkuCount = skuCodes.Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            CustomerCount = orderCount,
            AverageOrderValue = orderCount > 0 ? totalAmount / orderCount : 0m,
            UpdateTime = updateTime,
        };
    }

    private static Task ReplaceDailySalesStatisticAsync(
        SqlSugarContext context,
        ILogger logger,
        DateTime date,
        DailySalesStatistic? statistic) =>
        ExecuteTransactionSafelyAsync(
            beginAsync: () => context.Db.Ado.BeginTranAsync(),
            workAsync: async () =>
            {
                await context.Db.Deleteable<DailySalesStatistic>()
                    .Where(existing => existing.Date >= date.Date && existing.Date < date.Date.AddDays(1))
                    .ExecuteCommandAsync();
                if (statistic != null)
                    await context.Db.Insertable(statistic).ExecuteCommandAsync();
            },
            commitAsync: () => context.Db.Ado.CommitTranAsync(),
            rollbackAsync: () => context.Db.Ado.RollbackTranAsync(),
            logger: logger,
            operationName: "每日统计数据更新");

    /// <summary>
    /// 更新分时统计数据
    /// 按小时和分店维度聚合销售数据，包含全店汇总记录，按支付明细统计营业额
    /// </summary>
    /// <param name="date">目标日期</param>
    /// <param name="hour">指定小时，为空则更新全天24小时</param>
    public async Task UpdateHourlyStatistics(DateTime date, int? hour = null)
    {
        // 唯一实现在 SalesStatisticsHourlyRefresher；HBSales 历史窗口外传 null，避免误读旧系统。
        await SalesStatisticsHourlyRefresher.RefreshAsync(
            _context,
            _posmContext,
            GetHBSalesContextForVerifiedHistory(date),
            _logger,
            date,
            hour
        );
    }

    }
}
