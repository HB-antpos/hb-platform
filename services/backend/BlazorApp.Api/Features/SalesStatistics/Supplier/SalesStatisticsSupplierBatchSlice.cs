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
    /// <summary>销售统计垂直切片：SalesStatisticsSupplierBatchSlice。</summary>
    internal sealed class SalesStatisticsSupplierBatchSlice : SalesStatisticsSliceBase
    {
        private readonly SalesStatisticsStoreDailySlice _storeDaily;
        private readonly SalesStatisticsSupplierSlice _supplier;
        private readonly SalesStatisticsDailyHourlySlice _dailyHourly;

        public SalesStatisticsSupplierBatchSlice(
            SalesStatisticsSliceContext shared,
            SalesStatisticsStoreDailySlice storeDaily,
            SalesStatisticsSupplierSlice supplier,
            SalesStatisticsDailyHourlySlice dailyHourly)
            : base(shared)
        {
            _storeDaily = storeDaily;
            _supplier = supplier;
            _dailyHourly = dailyHourly;
        }

    public async Task<BatchStatisticsUpdateResult> BatchUpdateStoreStatistics(
        DateTime startDate,
        DateTime endDate,
        List<string>? branchCodes = null
    )
    {
        var result = new BatchStatisticsUpdateResult();
        // 验证日期范围
        var validation = ValidateDateRange(startDate, endDate);
        if (!validation.Success)
        {
            return validation;
        }

        result.TotalDays = (int)(endDate - startDate).TotalDays + 1;
        _logger.LogInformation(
            "开始批量更新分店统计数据: {StartDate} 至 {EndDate}, 分店: {Branches}",
            startDate.ToString("yyyy-MM-dd"),
            endDate.ToString("yyyy-MM-dd"),
            branchCodes != null ? string.Join(", ", branchCodes) : "All"
        );

        // 逐日更新统计数据
        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            try
            {
                await _storeDaily.UpdateStoreStatistics(date, branchCodes);
                result.ProcessedDays++;
            }
            catch (Exception ex)
            {
                result.FailedDates.Add(date.ToString("yyyy-MM-dd"));
                _logger.LogError(ex, "批量更新分店统计失败: {Date}", date);
            }
        }

        result.Success = result.FailedDates.Count == 0;
        result.Message = result.Success
            ? $"批量更新分店统计完成: {result.ProcessedDays}/{result.TotalDays} 天"
            : $"批量更新分店统计部分完成: {result.ProcessedDays}/{result.TotalDays} 天, 失败 {result.FailedDates.Count} 天";

        _logger.LogInformation(result.Message);
        return result;
    }

    /// <summary>
    /// 批量更新供应商统计数据
    /// 指定日期范围内更新供应商统计数据
    /// </summary>
    /// <param name="startDate">开始日期</param>
    /// <param name="endDate">结束日期</param>
    /// <param name="supplierCodes">供应商代码列表，为空则更新所有供应商</param>
    /// <returns>批量更新结果</returns>
    public async Task<BatchStatisticsUpdateResult> BatchUpdateSupplierStatistics(
        DateTime startDate,
        DateTime endDate,
        List<string>? supplierCodes = null
    )
    {
        var result = new BatchStatisticsUpdateResult();
        // 验证日期范围
        var validation = ValidateDateRange(startDate, endDate);
        if (!validation.Success)
        {
            return validation;
        }

        result.TotalDays = (int)(endDate - startDate).TotalDays + 1;
        _logger.LogInformation(
            "开始批量更新供应商统计数据: {StartDate} 至 {EndDate}, 供应商: {Suppliers}",
            startDate.ToString("yyyy-MM-dd"),
            endDate.ToString("yyyy-MM-dd"),
            supplierCodes != null ? string.Join(", ", supplierCodes) : "All"
        );

        try
        {
            // 执行更新
            await _supplier.UpdateSupplierStatistics(startDate, endDate, supplierCodes);
            result.ProcessedDays = result.TotalDays;
            result.Success = true;
            result.Message =
                $"批量更新供应商统计完成: {result.ProcessedDays}/{result.TotalDays} 天";
            _logger.LogInformation(result.Message);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message =
                $"批量更新供应商统计失败: {result.TotalDays} 天, 错误: {ex.Message}";
            _logger.LogError(
                ex,
                "批量更新供应商统计失败: {StartDate} 至 {EndDate}",
                startDate,
                endDate
            );
        }

        return result;
    }

    /// <summary>
    /// 并发批量更新供应商统计数据
    /// 将日期范围拆分为多个块，并发处理以提高效率
    /// </summary>
    /// <param name="startDate">开始日期</param>
    /// <param name="endDate">结束日期</param>
    /// <param name="supplierCodes">供应商代码列表，为空则更新所有供应商</param>
    /// <param name="maxConcurrency">最大并发数，为空则使用配置值</param>
    /// <returns>批量更新结果</returns>
    public async Task<BatchStatisticsUpdateResult> BatchUpdateSupplierStatisticsConcurrent(
        DateTime startDate,
        DateTime endDate,
        List<string>? supplierCodes = null,
        int? maxConcurrency = null
    )
    {
        var result = new BatchStatisticsUpdateResult();

        // 验证日期范围
        if (startDate > endDate)
        {
            result.Message = "开始日期不能大于结束日期";
            _logger.LogWarning(result.Message);
            return result;
        }

        var totalDays = (int)(endDate - startDate).TotalDays + 1;

        // 检查日期范围是否超出并发更新支持的最大天数
        if (totalDays > _maxDaysForConcurrentUpdate)
        {
            result.Message =
                $"日期范围过大，并发更新最多支持 {_maxDaysForConcurrentUpdate} 天（当前: {totalDays} 天）";
            _logger.LogWarning(result.Message);
            return result;
        }

        // 确定并发度
        var concurrency = maxConcurrency ?? _maxConcurrentUpdates;

        _logger.LogInformation(
            "开始并发批量更新供应商统计数据: {StartDate} 至 {EndDate}, 供应商: {Suppliers}, 并发度: {Concurrency}",
            startDate.ToString("yyyy-MM-dd"),
            endDate.ToString("yyyy-MM-dd"),
            supplierCodes != null ? string.Join(", ", supplierCodes) : "All",
            concurrency
        );

        // 拆分日期范围为多个并发块
        var dateRanges = SplitDateRange(startDate, endDate, concurrency, _maxDaysPerChunk);
        _logger.LogInformation(
            "将 {TotalDays} 天拆分为 {ChunkCount} 个并发块",
            totalDays,
            dateRanges.Count
        );

        var processedDays = 0;
        var failedDates = new List<string>();
        var failedRanges = new List<string>();
        var skippedDates = new List<string>();
        var syncLock = new object();

        var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = concurrency };

        // 启动计时器
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // 并发处理每个日期块
            await Parallel.ForEachAsync(
                dateRanges,
                parallelOptions,
                async (dateRange, cancellationToken) =>
                {
                    try
                    {
                        _logger.LogInformation(
                            "开始处理并发块 {StartDate} 至 {EndDate} ({Days} 天)",
                            dateRange.StartDate.ToString("yyyy-MM-dd"),
                            dateRange.EndDate.ToString("yyyy-MM-dd"),
                            dateRange.DayCount
                        );

                        // 为每个并发任务创建独立的作用域和数据库上下文
                        using var scope = _serviceScopeFactory.CreateScope();
                        var context =
                            scope.ServiceProvider.GetRequiredService<SqlSugarContext>();
                        var posmContext =
                            scope.ServiceProvider.GetRequiredService<POSMSqlSugarContext>();
                        var logger = _logger;

                        // 调用带上下文的更新方法
                        await UpdateSupplierStatisticsWithContext(
                            context,
                            posmContext,
                            logger,
                            dateRange.StartDate,
                            dateRange.EndDate,
                            supplierCodes
                        );

                        // 使用锁更新进度
                        lock (syncLock)
                        {
                            processedDays += dateRange.DayCount;
                        }

                        logger.LogInformation(
                            "并发块处理完成 {StartDate} 至 {EndDate} ({Days} 天), 累计进度: {Progress}/{Total}",
                            dateRange.StartDate.ToString("yyyy-MM-dd"),
                            dateRange.EndDate.ToString("yyyy-MM-dd"),
                            dateRange.DayCount,
                            processedDays,
                            totalDays
                        );
                    }
                    catch (Exception ex)
                    {
                        var rangeKey =
                            $"{dateRange.StartDate:yyyy-MM-dd} 至 {dateRange.EndDate:yyyy-MM-dd}";
                        failedRanges.Add(rangeKey);

                        // 记录失败的日期
                        for (
                            var d = dateRange.StartDate;
                            d <= dateRange.EndDate;
                            d = d.AddDays(1)
                        )
                        {
                            failedDates.Add(d.ToString("yyyy-MM-dd"));
                        }

                        _logger.LogError(
                            ex,
                            "并发块处理失败 {StartDate} 至 {EndDate}",
                            dateRange.StartDate.ToString("yyyy-MM-dd"),
                            dateRange.EndDate.ToString("yyyy-MM-dd")
                        );
                    }
                }
            );

            stopwatch.Stop();

            result.TotalDays = totalDays;
            result.ProcessedDays = processedDays;
            result.FailedDates = failedDates;
            result.Success = failedDates.Count == 0;

            var avgTimePerDay = stopwatch.Elapsed.TotalSeconds / totalDays;
            result.Message = result.Success
                ? $"并发批量更新供应商统计完成: {processedDays}/{totalDays} 天, 总耗时: {stopwatch.Elapsed:mm\\:ss}, 平均每天: {avgTimePerDay:F2}秒"
                : $"并发批量更新供应商统计部分完成: {processedDays}/{totalDays} 天, 失败: {failedDates.Count} 天, 总耗时: {stopwatch.Elapsed:mm\\:ss}";

            _logger.LogInformation(
                "{ResultMessage}, 失败的日期块: {FailedRanges}",
                result.Message,
                failedRanges.Count > 0 ? string.Join("; ", failedRanges) : "无"
            );
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            result.Success = false;
            result.Message =
                $"并发批量更新供应商统计失败: 处理 {processedDays}/{totalDays} 天, 错误: {ex.Message}";
            _logger.LogError(
                ex,
                "并发批量更新供应商统计失败: {StartDate} 至 {EndDate}",
                startDate,
                endDate
            );
        }

        return result;
    }

    /// <summary>
    /// 带上下文更新供应商统计数据（用于并发处理）
    /// </summary>
    /// <param name="context">数据库上下文</param>
    /// <param name="posmContext">POSM数据库上下文</param>
    /// <param name="logger">日志记录器</param>
    /// <param name="startDate">开始日期</param>
    /// <param name="endDate">结束日期</param>
    /// <param name="supplierCodes">供应商代码列表</param>
    internal static async Task UpdateSupplierStatisticsWithContext(
        SqlSugarContext context,
        POSMSqlSugarContext posmContext,
        ILogger logger,
        DateTime startDate,
        DateTime endDate,
        List<string>? supplierCodes
    )
    {
        try
        {
            var endExclusive = endDate.AddDays(1);
            var targetSupplierCodes = SalesStatisticsCodeRules.NormalizeSupplierCodes(
                supplierCodes
            );

            logger.LogInformation(
                "更新供应商统计数据: {StartDate} 至 {EndDate}, Suppliers: {Suppliers}",
                startDate.ToString("yyyy-MM-dd"),
                endDate.ToString("yyyy-MM-dd"),
                supplierCodes != null ? string.Join(", ", supplierCodes) : "All"
            );

            // 构建查询
            var query = posmContext
                .Db.Queryable<SalesOrder>()
                .LeftJoin<SalesOrderDetail>((o, d) => o.OrderGuid == d.OrderGuid)
                .LeftJoin<PosmProductSupplierMapping>(
                    (o, d, m) => d.ProductCode == m.ProductCode
                )
                // 金额、数量按净额并入退款单（Status=3，负数）；门店数、订单数只数 1/4。
                .Where(o =>
                    o.Status != null
                    && SalesOrderStatusRules.NetSalesStatuses.Contains(o.Status)
                    && o.OrderTime != null
                );

            // 使用半开区间过滤，避免不同数据库对 DateTime.Date 的翻译差异。
            query = query.Where(o =>
                o.OrderTime >= startDate
                && o.OrderTime < endExclusive
            );

            // 设置供应商代码过滤条件
            if (targetSupplierCodes.Any())
            {
                var includesUnknownSupplier = targetSupplierCodes.Contains(UnknownSupplierCode);
                query = query.Where(
                    (o, d, m) =>
                        (m.LocalSupplierCode != null
                            && targetSupplierCodes.Contains(m.LocalSupplierCode.Trim()))
                        || (m.ChinaSupplierCode != null
                            && targetSupplierCodes.Contains(m.ChinaSupplierCode.Trim()))
                        || (d.SupplierCode != null
                            && targetSupplierCodes.Contains(d.SupplierCode.Trim()))
                        || (
                            includesUnknownSupplier
                            && (m.LocalSupplierCode == null || m.LocalSupplierCode.Trim() == "")
                            && (d.SupplierCode == null || d.SupplierCode.Trim() == "")
                        )
                );
            }

            // 获取原始销售数据
            var rawData = await query
                .Select(
                    (o, d, m) =>
                        new
                        {
                            Date = o.OrderTime!.Value.Date,
                            BranchCode = o.BranchCode,
                            DetailSupplierCode = d.SupplierCode,
                            LocalSupplierCode = m.LocalSupplierCode,
                            ChinaSupplierCode = m.ChinaSupplierCode,
                            TotalAmount = d.ActualAmount,
                            Quantity = d.Quantity ?? 0m,
                            OrderGuid = o.OrderGuid,
                            OrderStatus = o.Status,
                        }
                )
                .ToListAsync();
            var orderAmountMaps = await SalesStatisticsProductStoreDailySourceQueries
                .LoadOrderAmountMapsAsync(
                posmContext,
                startDate,
                endExclusive,
                rawData,
                row => row.OrderGuid,
                row => row.TotalAmount ?? 0m
            );

            var resolvedRows = rawData
                .Select(x => new
                {
                    x.Date,
                    BranchCode = SalesStatisticsCodeRules.Normalize(x.BranchCode),
                    LocalSupplierCode = SalesStatisticsProductStoreDailyDomainRules
                        .ResolveStatisticSupplierCode(
                        x.LocalSupplierCode,
                        x.DetailSupplierCode
                    ),
                    ChinaSupplierCode = SalesStatisticsCodeRules.Normalize(x.ChinaSupplierCode),
                    TotalAmount = SalesStatisticsProductStoreDailyDomainRules
                        .ResolveStatisticAmount(
                        x.OrderGuid,
                        x.TotalAmount ?? 0m,
                        orderAmountMaps.PaymentAmounts,
                        orderAmountMaps.DetailAmounts
                    ),
                    x.Quantity,
                    x.OrderGuid,
                    x.OrderStatus,
                })
                .ToList();

            var shouldRefreshAllSuppliers = !targetSupplierCodes.Any();
            var refreshesLocalMasterSupplier = targetSupplierCodes.Contains("200");
            var localRowsForStats = shouldRefreshAllSuppliers
                ? resolvedRows
                : resolvedRows.Where(x => targetSupplierCodes.Contains(x.LocalSupplierCode)).ToList();
            var chinaRowsForStats = resolvedRows
                .Where(x =>
                    x.LocalSupplierCode == "200"
                    && !string.IsNullOrEmpty(x.ChinaSupplierCode)
                    && (
                        shouldRefreshAllSuppliers
                        || refreshesLocalMasterSupplier
                        || targetSupplierCodes.Contains(x.ChinaSupplierCode)
                    )
                )
                .ToList();

            // 本地供应商聚合：局部刷新国内子供应商时不重写本地 200 总计。
            var localStats = localRowsForStats
                .GroupBy(x => new { x.Date, x.LocalSupplierCode })
                .Select(g => new SupplierSalesStatistic
                {
                    Date = g.Key.Date,
                    SupplierCode = g.Key.LocalSupplierCode,
                    IsDomestic = false,
                    TotalAmount = g.Sum(x => x.TotalAmount),
                    TotalQuantity = (int)g.Sum(x => x.Quantity),
                    // 退款单只冲减金额/数量，不计门店数与订单数。
                    StoreCount = g.Where(x => !SalesOrderStatusRules.IsRefundOrder(x.OrderStatus))
                        .Select(x => x.BranchCode)
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Distinct()
                        .Count(),
                    OrderCount = g.Where(x => !SalesOrderStatusRules.IsRefundOrder(x.OrderStatus))
                        .Select(x => x.OrderGuid)
                        .Where(orderGuid => !string.IsNullOrWhiteSpace(orderGuid))
                        .Distinct()
                        .Count(),
                    UpdateTime = DateTime.Now,
                })
                .ToList();

            // 国内供应商聚合
            var chinaStats = chinaRowsForStats
                .GroupBy(x => new { x.Date, x.ChinaSupplierCode })
                .Select(g => new SupplierSalesStatistic
                {
                    Date = g.Key.Date,
                    SupplierCode = g.Key.ChinaSupplierCode!,
                    IsDomestic = true,
                    TotalAmount = g.Sum(x => x.TotalAmount),
                    TotalQuantity = (int)g.Sum(x => x.Quantity),
                    // 退款单只冲减金额/数量，不计门店数与订单数。
                    StoreCount = g.Where(x => !SalesOrderStatusRules.IsRefundOrder(x.OrderStatus))
                        .Select(x => x.BranchCode)
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Distinct()
                        .Count(),
                    OrderCount = g.Where(x => !SalesOrderStatusRules.IsRefundOrder(x.OrderStatus))
                        .Select(x => x.OrderGuid)
                        .Where(orderGuid => !string.IsNullOrWhiteSpace(orderGuid))
                        .Distinct()
                        .Count(),
                    UpdateTime = DateTime.Now,
                })
                .ToList();

            // 合并本地供应商和国内供应商统计
            var allStats = localStats.Concat(chinaStats).ToList();

            var allLocalCodes = localStats.Select(x => x.SupplierCode).Distinct().ToList();
            var allChinaCodes = chinaStats.Select(x => x.SupplierCode).Distinct().ToList();

            var supplierNameDict = new Dictionary<string, string>();

            // 查询本地供应商名称
            if (allLocalCodes.Any())
            {
                var localSuppliers = await context.HBLocalSupplierDb.GetListAsync(s =>
                    s.LocalSupplierCode != null
                    && allLocalCodes.Contains(s.LocalSupplierCode)
                    && !s.IsDeleted
                );
                foreach (var s in localSuppliers)
                {
                    if (!string.IsNullOrEmpty(s.LocalSupplierCode))
                    {
                        supplierNameDict[s.LocalSupplierCode] = s.Name ?? s.LocalSupplierCode;
                    }
                }
            }

            // 查询国内供应商名称
            if (allChinaCodes.Any())
            {
                var chinaSuppliers = await context.ChinaSupplierDb.GetListAsync(s =>
                    s.SupplierCode != null
                    && allChinaCodes.Contains(s.SupplierCode)
                    && !s.IsDeleted
                );
                foreach (var s in chinaSuppliers)
                {
                    if (!string.IsNullOrEmpty(s.SupplierCode))
                    {
                        supplierNameDict[s.SupplierCode] = s.SupplierName ?? s.SupplierCode;
                    }
                }
            }

            // 为统计记录填充供应商名称
            foreach (var stat in allStats)
            {
                if (stat.SupplierCode == UnknownSupplierCode)
                {
                    stat.SupplierName = UnknownSupplierName;
                }
                else if (supplierNameDict.TryGetValue(stat.SupplierCode, out var name))
                {
                    stat.SupplierName = name;
                }
                else
                {
                    stat.SupplierName = stat.SupplierCode;
                }
            }

            await SalesStatisticsTransactionExecutor.ExecuteAsync(
                beginAsync: () => context.Db.Ado.BeginTranAsync(),
                workAsync: async () =>
                {
                    // 供应商统计按目标范围重建，避免旧空供应商或已无销售供应商残留。
                    var deleteable = context.Db.Deleteable<SupplierSalesStatistic>()
                        .Where(s => s.Date >= startDate && s.Date <= endDate);
                    if (targetSupplierCodes.Any())
                    {
                        var deleteSupplierCodes = new List<string>();
                        if (refreshesLocalMasterSupplier)
                        {
                            var existingDomesticSupplierCodes = await context
                                .Db.Queryable<SupplierSalesStatistic>()
                                .Where(s =>
                                    s.Date >= startDate
                                    && s.Date <= endDate
                                    && s.IsDomestic == true
                                )
                                .Select(s => s.SupplierCode)
                                .Distinct()
                                .ToListAsync();
                            deleteSupplierCodes.AddRange(existingDomesticSupplierCodes);
                        }

                        deleteSupplierCodes = deleteSupplierCodes
                            .Concat(targetSupplierCodes)
                            .Concat(allStats.Select(s => s.SupplierCode))
                            .Where(code => !string.IsNullOrWhiteSpace(code))
                            .Select(code => code.Trim())
                            .Distinct()
                            .ToList();
                        var includesUnknownSupplier = targetSupplierCodes.Contains(UnknownSupplierCode);
                        deleteable = deleteable.Where(s =>
                            deleteSupplierCodes.Contains(s.SupplierCode.Trim())
                            || (
                                includesUnknownSupplier
                                && (s.SupplierCode == null || s.SupplierCode.Trim() == "")
                            )
                        );
                    }

                    var deletedCount = await deleteable.ExecuteCommandAsync();
                    logger.LogInformation("删除 {Count} 条供应商统计旧记录", deletedCount);

                    if (!allStats.Any())
                    {
                        logger.LogInformation(
                            "没有找到供应商统计数据: {StartDate} 至 {EndDate}",
                            startDate.ToString("yyyy-MM-dd"),
                            endDate.ToString("yyyy-MM-dd")
                        );
                        return;
                    }

                    context
                        .Db.Fastest<SupplierSalesStatistic>()
                        .PageSize(BatchSize)
                        .BulkCopy(allStats);
                    logger.LogInformation("批量插入 {Count} 条供应商统计记录", allStats.Count);
                },
                commitAsync: () => context.Db.Ado.CommitTranAsync(),
                rollbackAsync: () => context.Db.Ado.RollbackTranAsync(),
                logger: logger,
                operationName: "供应商统计数据更新"
            );

            logger.LogInformation(
                "供应商统计数据更新完成: {StartDate} 至 {EndDate}, 总记录: {Total}",
                startDate.ToString("yyyy-MM-dd"),
                endDate.ToString("yyyy-MM-dd"),
                allStats.Count
            );
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "更新供应商统计数据失败: {StartDate} 至 {EndDate}",
                startDate,
                endDate
            );
            throw;
        }
    }

    internal static List<DateRange> SplitDateRange(
        DateTime startDate,
        DateTime endDate,
        int maxChunks,
        int maxDaysPerChunk)
    {
        var totalDays = (int)(endDate - startDate).TotalDays + 1;

        var chunkCount = Math.Min(
            maxChunks,
            (int)Math.Ceiling((double)totalDays / maxDaysPerChunk)
        );

        var ranges = new List<DateRange>();
        var currentStart = startDate;

        while (currentStart <= endDate)
        {
            var currentEnd = currentStart.AddDays(maxDaysPerChunk - 1);
            if (currentEnd > endDate)
            {
                currentEnd = endDate;
            }

            ranges.Add(new DateRange { StartDate = currentStart, EndDate = currentEnd });
            currentStart = currentEnd.AddDays(1);
        }

        return ranges;
    }

    /// <summary>
    /// 批量更新每日统计数据
    /// 逐日更新指定日期范围内的每日销售汇总数据
    /// </summary>
    /// <param name="startDate">开始日期</param>
    /// <param name="endDate">结束日期</param>
    /// <returns>批量更新结果</returns>
    public async Task<BatchStatisticsUpdateResult> BatchUpdateDailyStatistics(
        DateTime startDate,
        DateTime endDate
    )
    {
        var result = new BatchStatisticsUpdateResult();
        // 验证日期范围
        var validation = ValidateDateRange(startDate, endDate);
        if (!validation.Success)
        {
            return validation;
        }

        result.TotalDays = (int)(endDate - startDate).TotalDays + 1;
        _logger.LogInformation(
            "开始批量更新每日统计数据: {StartDate} 至 {EndDate}",
            startDate.ToString("yyyy-MM-dd"),
            endDate.ToString("yyyy-MM-dd")
        );

        // 逐日更新统计数据
        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            try
            {
                await _dailyHourly.UpdateDailyStatistics(date.ToString("yyyy-MM-dd"));
                result.ProcessedDays++;
            }
            catch (Exception ex)
            {
                result.FailedDates.Add(date.ToString("yyyy-MM-dd"));
                _logger.LogError(ex, "批量更新每日统计失败: {Date}", date);
            }
        }

        result.Success = result.FailedDates.Count == 0;
        result.Message = result.Success
            ? $"批量更新每日统计完成: {result.ProcessedDays}/{result.TotalDays} 天"
            : $"批量更新每日统计部分完成: {result.ProcessedDays}/{result.TotalDays} 天, 失败 {result.FailedDates.Count} 天";

        _logger.LogInformation(result.Message);
        return result;
    }

    /// <summary>
    /// 批量更新分时统计数据
    /// 逐日更新指定日期范围内的分时统计数据（可指定小时）
    /// </summary>
    /// <param name="startDate">开始日期</param>
    /// <param name="endDate">结束日期</param>
    /// <param name="hour">指定小时，为空则更新所有小时</param>
    /// <returns>批量更新结果</returns>
    public async Task<BatchStatisticsUpdateResult> BatchUpdateHourlyStatistics(
        DateTime startDate,
        DateTime endDate,
        int? hour = null
    )
    {
        var result = new BatchStatisticsUpdateResult();
        // 验证日期范围
        var validation = ValidateDateRange(startDate, endDate);
        if (!validation.Success)
        {
            return validation;
        }

        result.TotalDays = (int)(endDate - startDate).TotalDays + 1;
        var hourStr = hour.HasValue ? $" hour {hour.Value}" : " all hours";
        _logger.LogInformation(
            "开始批量更新分时统计数据: {StartDate} 至 {EndDate}{Hour}",
            startDate.ToString("yyyy-MM-dd"),
            endDate.ToString("yyyy-MM-dd"),
            hourStr
        );

        // 逐日更新统计数据
        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            try
            {
                if (hour.HasValue)
                {
                    await _dailyHourly.UpdateHourlyStatistics(date, hour.Value);
                }
                else
                {
                    await _dailyHourly.UpdateHourlyStatistics(date, null);
                }
                result.ProcessedDays++;
            }
            catch (Exception ex)
            {
                result.FailedDates.Add(date.ToString("yyyy-MM-dd"));
                _logger.LogError(ex, "批量更新分时统计失败: {Date}", date);
            }
        }

        result.Success = result.FailedDates.Count == 0;
        result.Message = result.Success
            ? $"批量更新分时统计完成: {result.ProcessedDays}/{result.TotalDays} 天"
            : $"批量更新分时统计部分完成: {result.ProcessedDays}/{result.TotalDays} 天, 失败 {result.FailedDates.Count} 天";

        _logger.LogInformation(result.Message);
        return result;
    }

    }
}
