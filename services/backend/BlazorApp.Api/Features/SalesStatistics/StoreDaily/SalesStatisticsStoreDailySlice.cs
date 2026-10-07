using System.Runtime.ExceptionServices;
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
    /// <summary>销售统计垂直切片：SalesStatisticsStoreDailySlice。</summary>
    internal sealed class SalesStatisticsStoreDailySlice : SalesStatisticsSliceBase
    {
        private readonly SalesStatisticsProductStoreDailyRefreshSlice _productRefresh;
        private readonly SalesStatisticsProductStoreDailySupportSlice _productSupport;
        private readonly SalesStatisticsOrchestrationSlice _orchestration;

        public SalesStatisticsStoreDailySlice(
            SalesStatisticsSliceContext shared,
            SalesStatisticsProductStoreDailyRefreshSlice productRefresh,
            SalesStatisticsProductStoreDailySupportSlice productSupport,
            SalesStatisticsOrchestrationSlice orchestration)
            : base(shared)
        {
            _productRefresh = productRefresh;
            _productSupport = productSupport;
            _orchestration = orchestration;
        }

    public async Task UpdateStoreStatistics(DateTime? date = null)
    {
        var targetDate = (date ?? SalesStatisticsBusinessDate.Today()).Date;
        try
        {
            _logger.LogInformation("开始更新分店统计数据: {Date}", targetDate);

            if (SalesStatisticsHBSalesHistoryWindow.Includes(targetDate))
            {
                // HBSales 历史窗口内的分店与商品统计来自双来源，必须在同一事务内同时替换。
                await _productRefresh.Update2025StoreAndProductStatisticsAtomically(
                    _context,
                    _posmContext,
                    _hbSalesContext,
                    _logger,
                    targetDate
                );
                return;
            }

            var statisticsList = await _productSupport.BuildStoreStatisticsAsync(
                _context,
                _posmContext,
                GetHBSalesContextForVerifiedHistory(targetDate),
                targetDate,
                null
            );

            await SalesStatisticsTransactionExecutor.ExecuteAsync(
                beginAsync: () => _context.Db.Ado.BeginTranAsync(),
                workAsync: async () =>
                {
                    // 删除该日期的所有旧记录
                    var deletedCount = await _context
                        .Db.Deleteable<StoreSalesStatistic>()
                        .Where(s => s.Date == targetDate)
                        .ExecuteCommandAsync();
                    _logger.LogInformation("删除 {Count} 条分店统计旧记录", deletedCount);

                    // 批量插入新记录
                    if (statisticsList.Any())
                    {
                        _context
                            .Db.Fastest<StoreSalesStatistic>()
                            .PageSize(BatchSize)
                            .BulkCopy(statisticsList);
                    }
                },
                commitAsync: () => _context.Db.Ado.CommitTranAsync(),
                rollbackAsync: () => _context.Db.Ado.RollbackTranAsync(),
                logger: _logger,
                operationName: "分店统计数据更新"
            );

            _logger.LogInformation(
                "分店统计数据更新完成: {Date}, 总记录: {Total}",
                targetDate,
                statisticsList.Count
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新分店统计数据失败: {Date}", targetDate);
            throw;
        }
    }

    /// <summary>
    /// 全量刷新前一天数据
    /// 刷新前一天的每日统计、分时统计、分店统计和供应商统计
    /// </summary>
    public async Task<SalesStatisticsRefreshExecutionResult> FullRefreshPreviousDay()
    {
        try
        {
            var previousDay = SalesStatisticsBusinessDate.Today().AddDays(-1);

            _logger.LogInformation("开始全量刷新前一天数据: {Date}", previousDay);

            // 全量刷新统一走带数据库租约的入口，保证 8 张日级统计表口径一致且跨实例不重复跑。
            var refreshed = await RunLeasedFullRefreshForSingleDateAsync(previousDay, "前一天");

            _logger.LogInformation("前一天数据全量刷新完成: {Date}", previousDay);
            return refreshed
                ? SalesStatisticsRefreshExecutionResult.Completed()
                : SalesStatisticsRefreshExecutionResult.Skipped("前一天统计已有运行中的日期租约");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "全量刷新前一天数据失败");
            throw;
        }
    }

    /// <summary>
    /// 全量刷新当天数据
    /// 刷新当天的每日统计、分时统计、分店统计和供应商统计
    /// </summary>
    public async Task<SalesStatisticsRefreshExecutionResult> FullRefreshCurrentDay(bool automatic = false, bool includeHistorical = true, int firstHistoricalDayOffset = 1)
    {
        try
        {
            var currentDay = SalesStatisticsBusinessDate.Today();

            _logger.LogInformation("开始全量刷新当天数据: {Date}", currentDay);

            // 当天主刷新也复用带数据库租约的完整路径，避免和手动补算抢同一天。
            var refreshed = await RunLeasedFullRefreshForSingleDateAsync(currentDay, "当天");
            if (!refreshed || !includeHistorical)
            {
                return refreshed
                    ? SalesStatisticsRefreshExecutionResult.Completed()
                    : SalesStatisticsRefreshExecutionResult.Skipped("当天统计已有运行中的日期租约");
            }

            // POSM 可能延迟上传，最近 7 天逐日滚动补算：商品统计无条件重算，总账类统计只在水位落后时整天全量刷新；
            // 历史日只在夜间窗口启动。每个日期开始前重新检查，窗口边界到达后保留剩余日期到下一轮；当天主刷新和显式入口不受影响。
            for (var offset = Math.Clamp(firstHistoricalDayOffset, 1, 7); offset < 7; offset++)
            {
                if (automatic && !SalesStatisticsHistoricalRefreshWindow.IsOpen(_timeProvider))
                {
                    _logger.LogInformation(
                        "当前时间不在历史商品统计夜间窗口，保留后续历史日待下一窗口: {Date}, RemainingOffset={Offset}",
                        currentDay,
                        offset
                    );
                    return SalesStatisticsRefreshExecutionResult.Completed();
                }

                await RunRollingHistoricalRefreshAsync(currentDay.AddDays(-offset));
            }

            _logger.LogInformation("当天数据全量刷新完成: {Date}", currentDay);
            return SalesStatisticsRefreshExecutionResult.Completed();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "全量刷新当天数据失败");
            throw;
        }
    }

    internal async Task<bool> RunLeasedFullRefreshForSingleDateAsync(
        DateTime date,
        string label
    )
    {
        var result = await _orchestration.BatchFullRefreshConcurrent(date, date, 1);
        if (result.HasSkippedDates && !result.HasFailedDates)
        {
            _logger.LogInformation(
                "{Label}数据全量刷新跳过，日期 {Date} 已有运行中统计租约",
                label,
                date.ToString("yyyy-MM-dd")
            );
            return false;
        }
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Message);
        }

        return result.ProcessedDays == 1;
    }

    /// <summary>
    /// 滚动补算要核对水位的「总账」类统计。商品分店每日及其澳洲/国内供应商拆分由滚动补算本身无条件重算，不在此列。
    /// </summary>
    private static readonly string[] RollingLedgerStatisticTypes =
    {
        SalesStatisticType.DailySales,
        SalesStatisticType.HourlySales,
        SalesStatisticType.StoreSales,
        SalesStatisticType.SupplierSales,
        SalesStatisticType.StoreSupplierSales,
    };

    /// <summary>
    /// 近 7 天滚动补算的单日入口。
    /// 总账类统计只在「当天」和「次日夜间」各刷新一次，之后 POSM 才上传的迟到订单不会再进入这些表
    /// （2026-09-26 一笔订单 10-01 才上传，分店营业额因此比支付明细少 13.98，而商品统计被滚动补算追平了）。
    /// 这里先比较 POSM 当日真实上传水位与总账状态里记录的水位：落后就整天全量刷新（已包含商品统计，不再重复跑商品补算），
    /// 否则维持原行为，只重算商品统计。
    /// </summary>
    internal async Task RunRollingHistoricalRefreshAsync(DateTime date)
    {
        var targetDate = date.Date;
        if (await HasLedgerStatisticsFallenBehindSourceAsync(targetDate))
        {
            _logger.LogInformation(
                "日期 {Date} 的总账统计水位落后于 POSM 上传水位，滚动补算改为整天全量刷新",
                targetDate.ToString("yyyy-MM-dd")
            );
            // 返回 false 表示日期租约被其他执行者占用，这次跳过即可：水位仍然落后，下一晚会再次比较。
            // 刷新失败会抛异常，与商品滚动补算失败时的处理一致。
            await RunLeasedFullRefreshForSingleDateAsync(targetDate, "迟到上传滚动补算");
            return;
        }

        await RunLeasedProductStoreDailyRefreshAsync(targetDate);
    }

    /// <summary>
    /// 总账类统计里是否有任一张的状态水位落后于 POSM 当日真实上传水位（或根本没有状态）。
    /// 状态水位在刷新开始时就写入，所以一次尝试（含失败）之后不会再触发，不会出现永久失败的日期每晚重试；
    /// 失败日期仍由数据对齐页的 Failed 状态负责。
    /// </summary>
    internal async Task<bool> HasLedgerStatisticsFallenBehindSourceAsync(DateTime date)
    {
        var targetDate = date.Date;
        // HBSales 历史窗口内的分店与商品统计由原子入口同时刷新，不存在「只补商品」的情况。
        if (SalesStatisticsHBSalesHistoryWindow.Includes(targetDate))
            return false;

        using var scope = _serviceScopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SqlSugarContext>();
        var posmContext = scope.ServiceProvider.GetRequiredService<POSMSqlSugarContext>();
        var hbSalesContext = scope.ServiceProvider.GetService<HBSalesRecordSqlSugarContext>();

        // 与刷新时写入状态用的是同一个水位查询，两边口径一致，已对齐的日期差值恒为 0。
        var sourceWatermark = await SalesStatisticsProductStoreDailyStateSlice
            .QueryDailySourceWatermarkAsync(posmContext, hbSalesContext, targetDate);
        if (!sourceWatermark.HasValue)
            return false; // 当天没有任何来源数据（休业日），没有可落后的内容。

        var nextDate = targetDate.AddDays(1);
        // SqlSugar 表达式不能直接引用私有静态字段，先拷到局部变量再放进查询条件。
        var ledgerTypes = RollingLedgerStatisticTypes;
        // SQLite/SQL Server 的 DateTime 精度不同，按规范化日期范围读取状态。
        var states = await context.Db.Queryable<SalesStatisticRefreshState>()
            .Where(s =>
                ledgerTypes.Contains(s.StatisticType)
                && s.Date >= targetDate
                && s.Date < nextDate
            )
            .ToListAsync();

        return ledgerTypes.Any(type =>
        {
            var recorded = states.FirstOrDefault(s => s.StatisticType == type)?.LastSourceUploadTime;
            return !recorded.HasValue || recorded.Value < sourceWatermark.Value;
        });
    }

    internal async Task<bool> RunLeasedProductStoreDailyRefreshAsync(DateTime date)
    {
        var targetDate = date.Date;
        using var scope = _serviceScopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<SqlSugarContext>();
        var posmContext = scope.ServiceProvider.GetRequiredService<POSMSqlSugarContext>();
        var hbSalesContext = scope.ServiceProvider.GetService<HBSalesRecordSqlSugarContext>();
        var logger = _logger;
        var leaseService = scope.ServiceProvider.GetRequiredService<ScheduledTaskLeaseService>();
        var dateStr = targetDate.ToString("yyyy-MM-dd");
        var leaseTaskType = SalesStatisticsAlignmentService.DailyFullRefreshLeaseTaskType;
        var leaseDuration = TimeSpan.FromHours(2);
        string? leaseToken = null;
        DateTime? sourceWatermark = null;

        try
        {
            var lease = await leaseService.TryAcquireAsync(leaseTaskType, dateStr, leaseDuration);
            if (!lease.Acquired)
            {
                _logger.LogInformation("日期 {Date} 已有统计租约运行中，商品滚动补算跳过", dateStr);
                return false;
            }

            leaseToken = lease.Lease?.LeaseToken;
            if (string.IsNullOrWhiteSpace(leaseToken))
            {
                throw new InvalidOperationException($"统计租约缺少 fencing token: {dateStr}");
            }

            await leaseService.EnsureActiveAsync(
                leaseTaskType,
                dateStr,
                leaseToken,
                leaseDuration,
                "商品分店每日滚动补算"
            );
            if (SalesStatisticsHBSalesHistoryWindow.Includes(targetDate))
            {
                // 滚动补算同样不能让商品表单独 Running/Failed，原子入口会成对维护状态。
                await _productRefresh.Update2025StoreAndProductStatisticsAtomically(
                    context,
                    posmContext,
                    hbSalesContext,
                    logger,
                    targetDate
                );
            }
            else
            {
                sourceWatermark = await SalesStatisticsProductStoreDailyStateSlice
                    .QueryDailySourceWatermarkAsync(
                    posmContext,
                    hbSalesContext,
                    targetDate
                );
                await SalesStatisticsProductStoreDailyStateSlice.UpsertStatisticStateAsync(
                    context,
                    SalesStatisticType.ProductStoreDaily,
                    targetDate,
                    SalesStatisticRefreshStatus.Running,
                    sourceWatermark,
                    null
                );
                await _productRefresh.UpdateProductStoreDailyStatisticsWithContext(
                    context,
                    posmContext,
                    hbSalesContext,
                    logger,
                    targetDate
                );
            }
            await leaseService.EnsureActiveAsync(
                leaseTaskType,
                dateStr,
                leaseToken,
                leaseDuration,
                "商品分店每日滚动补算完成确认"
            );
            if (!await leaseService.CompleteAsync(leaseTaskType, dateStr, leaseToken, true))
            {
                throw new InvalidOperationException($"统计租约完成失败，token 已失效: {dateStr}");
            }
            return true;
        }
        catch (Exception ex)
        {
            if (!SalesStatisticsHBSalesHistoryWindow.Includes(targetDate))
            {
                await SalesStatisticsProductStoreDailyStateSlice.UpsertStatisticStateAsync(
                    context,
                    SalesStatisticType.ProductStoreDaily,
                    targetDate,
                    SalesStatisticRefreshStatus.Failed,
                    sourceWatermark,
                    ex.Message
                );
            }
            if (!string.IsNullOrWhiteSpace(leaseToken))
            {
                await leaseService.CompleteAsync(leaseTaskType, dateStr, leaseToken, false, ex.Message);
            }
            throw;
        }
    }

    /// <summary>
    /// 检查是否为国内供应商
    /// </summary>
    /// <param name="supplierCode">供应商代码</param>
    /// <returns>是否为国内供应商</returns>
    internal async Task<bool> CheckIsDomesticSupplierAsync(string supplierCode)
    {
        try
        {
            // 查询中国供应商表
            var chinaSupplier = await _context.ChinaSupplierDb.GetFirstAsync(s =>
                s.SupplierCode == supplierCode && !s.IsDeleted
            );

            if (chinaSupplier != null)
            {
                return true;
            }

            // 查询国内产品表
            var domesticProduct = await _context
                .Db.Queryable<DomesticProduct>()
                .Where(dp => dp.SupplierCode == supplierCode && !dp.IsDeleted)
                .FirstAsync();

            return domesticProduct != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 更新指定分店统计数据
    /// 可以指定分店代码列表，只更新这些分店的统计数据
    /// </summary>
    /// <param name="date">目标日期</param>
    /// <param name="branchCodes">分店代码列表，为空则更新所有分店</param>
    public async Task UpdateStoreStatistics(DateTime date, List<string>? branchCodes = null)
    {
        var targetDate = date.Date;
        var targetBranchCodes = SalesStatisticsCodeRules.NormalizeBranchCodes(branchCodes);
        try
        {
            _logger.LogInformation(
                "开始更新指定分店统计数据: {Date}, Branches: {Branches}",
                targetDate,
                targetBranchCodes.Any() ? string.Join(", ", targetBranchCodes) : "All"
            );

            if (SalesStatisticsHBSalesHistoryWindow.Includes(targetDate))
            {
                if (targetBranchCodes.Any())
                {
                    throw new InvalidOperationException(
                        "HBSales 历史窗口内不能仅刷新指定分店：该操作会破坏 ProductStoreDaily 与 StoreSales 的双表一致性，请执行全分店刷新"
                    );
                }

                // null、空集合和空白分店代码均等同全分店，统一进入双表原子刷新。
                await _productRefresh.Update2025StoreAndProductStatisticsAtomically(
                    _context,
                    _posmContext,
                    _hbSalesContext,
                    _logger,
                    targetDate
                );
                return;
            }

            var statisticsList = await _productSupport.BuildStoreStatisticsAsync(
                _context,
                _posmContext,
                GetHBSalesContextForVerifiedHistory(targetDate),
                targetDate,
                branchCodes
            );

            await SalesStatisticsTransactionExecutor.ExecuteAsync(
                beginAsync: () => _context.Db.Ado.BeginTranAsync(),
                workAsync: async () =>
                {
                    // 指定分店重算只替换对应分店，避免清掉同日其它分店统计。
                    var deleteable = _context.Db.Deleteable<StoreSalesStatistic>()
                        .Where(s => s.Date == targetDate);
                    if (targetBranchCodes.Any())
                    {
                        deleteable = deleteable.Where(s => targetBranchCodes.Contains(s.BranchCode));
                    }

                    var deletedCount = await deleteable.ExecuteCommandAsync();
                    _logger.LogInformation("删除 {Count} 条分店统计旧记录", deletedCount);

                    // 批量插入新记录
                    if (statisticsList.Any())
                    {
                        _context
                            .Db.Fastest<StoreSalesStatistic>()
                            .PageSize(BatchSize)
                            .BulkCopy(statisticsList);
                    }
                },
                commitAsync: () => _context.Db.Ado.CommitTranAsync(),
                rollbackAsync: () => _context.Db.Ado.RollbackTranAsync(),
                logger: _logger,
                operationName: "指定分店统计数据更新"
            );

            _logger.LogInformation(
                "指定分店统计数据更新完成: {Date}, 总记录: {Total}",
                targetDate,
                statisticsList.Count
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新指定分店统计数据失败: {Date}", targetDate);
            throw;
        }
    }

    }
}
