using System.Diagnostics;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;

namespace BlazorApp.Api.Services;

/// <summary>商品分店日统计写入边界：只在此处定义主统计替换事务。</summary>
internal sealed class SalesStatisticsProductStoreDailyCommandWriter
{
    private const int BatchSize = 5000;

    /// <summary>
    /// 成本锁等待超时后的退避间隔。另一笔商品日统计持有整日商品锁通常 1–2.5 分钟
    /// （2026-10-03 生产实测 ≥41 秒、≥136 秒），7 次尝试（每次最多等 10 秒）合计覆盖约 4.5 分钟；
    /// 每次重试前整笔事务已回滚，间隔期间不持有总闸或任何商品锁，不额外阻塞前台成本编辑。
    /// </summary>
    internal static readonly TimeSpan[] LockRetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(90),
    ];

    internal sealed record PersistResult(
        SalesStatisticsProductStoreDailyStateSlice.ProductStatisticStatusResult Status,
        ProductStoreDailyBatchFence? BatchFence);

    internal Task<PersistResult> PersistAsync(
        SqlSugarContext context,
        POSMSqlSugarContext posmContext,
        ILogger logger,
        ProductStoreDailyRefreshInput input,
        ProductStoreDailyRefreshBuildResult build,
        IReadOnlyList<StoreSalesStatistic>? atomicStoreStatistics,
        DateTime? sourceWatermarkOverride,
        Func<Task>? validateSourceWatermarkBeforeCommitAsync,
        string? atomicSuccessStatusOverride,
        Guid? expectedJobId = null,
        Func<Task>? validateExecutionOwnershipBeforeCommitAsync = null)
    {
        var rollbackCompleted = false;
        return PersistWithLockRetryAsync(
            persistOnceAsync: () =>
            {
                rollbackCompleted = false;
                return PersistOnceAsync(
                    context, posmContext, logger, input, build, atomicStoreStatistics,
                    sourceWatermarkOverride, validateSourceWatermarkBeforeCommitAsync,
                    atomicSuccessStatusOverride, expectedJobId,
                    validateExecutionOwnershipBeforeCommitAsync,
                    () => rollbackCompleted = true);
            },
            // 当日与历史日期都重试：历史补算（HBSales 窗口的全分店原子刷新）与当日半点统计、
            // 持久队列会争同一批商品成本锁，一次 10 秒等不到就整日失败。来源签名回调重入是安全的：
            // 2025 原子入口第二次起改走"重新查询两来源"的 post 复核，只会更严格；队列 owner 校验幂等。
            canRetryAfterRollback: () => rollbackCompleted && context.Db.Ado.Transaction == null,
            targetDate: input.TargetDate,
            logger: logger);
    }

    internal static async Task<PersistResult> PersistWithLockRetryAsync(
        Func<Task<PersistResult>> persistOnceAsync,
        Func<bool> canRetryAfterRollback,
        DateTime targetDate,
        ILogger logger,
        Func<TimeSpan, Task>? delayAsync = null)
    {
        delayAsync ??= delay => Task.Delay(delay);
        var maxAttempts = LockRetryDelays.Length + 1;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await persistOnceAsync();
            }
            catch (SetChildPurchasePriceLockException ex) when (
                ex.ResultCode == -1 && attempt < maxAttempts && canRetryAfterRollback())
            {
                // 部分商品锁可能已获取，必须确认整笔事务回滚后才重试；不能在原事务内继续。
                // 只重试等待超时，取消、死锁及调用错误保留原有失败语义。
                var delay = LockRetryDelays[attempt - 1];
                logger.LogWarning(ex,
                    "商品分店每日统计成本锁等待超时，事务已回滚后重试 Date={Date:yyyy-MM-dd} Attempt={Attempt} MaxAttempts={MaxAttempts} ResultCode={ResultCode} Resource={Resource} RetryDelayMs={RetryDelayMs}",
                    targetDate, attempt, maxAttempts, ex.ResultCode, ex.Resource, delay.TotalMilliseconds);
                await delayAsync(delay);
            }
        }
    }

    private async Task<PersistResult> PersistOnceAsync(
        SqlSugarContext context,
        POSMSqlSugarContext posmContext,
        ILogger logger,
        ProductStoreDailyRefreshInput input,
        ProductStoreDailyRefreshBuildResult build,
        IReadOnlyList<StoreSalesStatistic>? atomicStoreStatistics,
        DateTime? sourceWatermarkOverride,
        Func<Task>? validateSourceWatermarkBeforeCommitAsync,
        string? atomicSuccessStatusOverride,
        Guid? expectedJobId,
        Func<Task>? validateExecutionOwnershipBeforeCommitAsync,
        Action onRollbackCompleted)
    {
        var effectiveSourceWatermark = sourceWatermarkOverride ?? input.LastSourceUploadTime;
        SupplierStoreStatisticBuildResult? supplierBuild = null;
        string? productVersion = null;
        ProductStoreDailyBatchFence? capturedBatchFence = null;
        SalesStatisticsProductStoreDailyStateSlice.ProductStatisticStatusResult status = null!;

        if (validateSourceWatermarkBeforeCommitAsync != null)
            await validateSourceWatermarkBeforeCommitAsync();

        // 双表入口和普通入口共用这一处主事务，保证删除、批量写入及状态切换不可拆分。
        await SalesStatisticsTransactionExecutor.ExecuteAsync(
            beginAsync: () => context.Db.Ado.BeginTranAsync(),
            workAsync: async () =>
            {
                // 日期锁必须是事务中的第一个业务动作；Backfill 也调用同一 helper，避免绕过写入互斥。
                await SalesStatisticsCostWriteLock.AcquireAsync(context.Db, input.TargetDate);
                await SalesStatisticsProductStoreDailyStateSlice.FenceProductStatisticExecutionOwnerAsync(
                    context,
                    input.TargetDate,
                    expectedJobId);

                // 日期锁保护整日替换及历史成本快照；仅依赖当前成本的商品需要额外的成本业务锁。
                var previousRows = await context.Db.Queryable<ProductStoreDailySalesStatistic>()
                    .Where(row => row.Date >= input.TargetDate.Date && row.Date < input.TargetDate.Date.AddDays(1))
                    .With(SqlWith.UpdLock)
                    .ToListAsync();
                var allProductCodes = SetChildPurchasePriceMutationLock.NormalizeProductCodes(
                    input.RawRows.Select(row => row.ProductCode)
                        .Concat(previousRows.Select(row => row.ProductCode)));
                var productCodes = ResolveCurrentCostProductCodes(
                    input.TargetDate, allProductCodes, build.Statistics, previousRows, input.ChinaSupplierCodes);
                logger.LogInformation(
                    "商品分店每日统计成本锁范围 Date={Date:yyyy-MM-dd} ReferencedProductCount={ReferencedProductCount} CurrentCostProductCount={CurrentCostProductCount}",
                    input.TargetDate, allProductCodes.Count, productCodes.Count);
                if (productCodes.Count > 0)
                {
                    // 一次 SQL 批量按规范顺序取锁，兼顾上万商品的往返开销和商品级隔离。
                    var lockStopwatch = Stopwatch.StartNew();
                    try
                    {
                        await SetChildPurchasePriceMutationLock.AcquireProductsInBatchWithinBudgetAsync(context.Db, productCodes);
                    }
                    catch (SetChildPurchasePriceLockException ex)
                    {
                        logger.LogWarning(ex,
                            "商品分店每日统计获取成本锁失败 Date={Date:yyyy-MM-dd} ResultCode={ResultCode} Resource={Resource} ProductCount={ProductCount} ElapsedMs={ElapsedMs}",
                            input.TargetDate, ex.ResultCode, ex.Resource, productCodes.Count, lockStopwatch.ElapsedMilliseconds);
                        throw;
                    }

                    // 成本表必须在业务锁内重读；POSM/HBSales 原明细和销售金额仍沿用锁外快照，
                    // 仅替换会受商品/分店成本写入影响的三张成本来源表。
                    var branchCodes = input.RawRows
                        .Select(row => SalesStatisticsCodeRules.ResolveBranchCode(
                            row.BranchCode,
                            row.DeviceCode,
                            input.DeviceBranchMap))
                        .Concat(previousRows.Select(row => row.BranchCode))
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Select(code => code!.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var storeCosts = await SalesStatisticsProductStoreDailySourceReader
                        .LoadStoreCostsInBatchesAsync(
                            context,
                            productCodes,
                            branchCodes,
                            includeInactive: input.TargetDate.Date < SalesStatisticsBusinessDate.Today());
                    var productCosts = await context.Db.Queryable<Product>()
                        .Where(product => product.ProductCode != null
                            && productCodes.Contains(product.ProductCode)
                            && product.IsDeleted == false)
                        .Select(product => new ProductCostRow
                        {
                            ProductCode = product.ProductCode,
                            PurchasePrice = product.PurchasePrice,
                        })
                        .ToListAsync();
                    var warehouseCosts = await context.Db.Queryable<WarehouseProduct>()
                        .Where(product => productCodes.Contains(product.ProductCode)
                            && product.IsDeleted == false)
                        .Select(product => new WarehouseCostRow
                        {
                            ProductCode = product.ProductCode,
                            ImportPrice = product.ImportPrice,
                        })
                        .ToListAsync();
                    input = input with
                    {
                        StoreCosts = storeCosts,
                        ProductCosts = productCosts,
                        WarehouseCosts = warehouseCosts,
                    };
                    build = new SalesStatisticsProductStoreDailyBuilder().Build(input);
                }
                if (input.TargetDate.Date < SalesStatisticsBusinessDate.Today())
                {
                    // 历史日期的队列恢复只修复销售事实；沿用旧快照的成本，不能按恢复当天的进价重算毛利。
                    PreserveHistoricalCostSnapshots(build.Statistics, previousRows, input.ChinaSupplierCodes);
                }

                status = await SalesStatisticsProductStoreDailyStateSlice.BuildProductStatisticStatusAsync(
                    context,
                    input.TargetDate,
                    build.Statistics,
                    build.Diagnostics,
                    input.LastSourceUploadTime,
                    build.SupplementalReturnAdjustments,
                    atomicStoreStatistics);
                if (build.Statistics.Count == 0 && input.RawRows.Count > 0)
                {
                    status = new SalesStatisticsProductStoreDailyStateSlice.ProductStatisticStatusResult(
                        SalesStatisticRefreshStatus.Failed,
                        $"商品分店每日统计存在 {input.RawRows.Count} 条来源记录，但没有可写入的有效分店商品: {input.TargetDate:yyyy-MM-dd}");
                }
                if (atomicStoreStatistics != null && status.Status == SalesStatisticRefreshStatus.Failed)
                    throw new InvalidOperationException(status.ErrorMessage ?? "2025 商品分店每日统计业务校验失败");
                if (atomicStoreStatistics != null && status.Status == SalesStatisticRefreshStatus.Fresh
                    && !string.IsNullOrWhiteSpace(atomicSuccessStatusOverride))
                {
                    status = new SalesStatisticsProductStoreDailyStateSlice.ProductStatisticStatusResult(
                        atomicSuccessStatusOverride,
                        status.ErrorMessage,
                        status.SourceProductVersion
                    );
                }
                if (atomicStoreStatistics != null)
                {
                    await context.Db.Deleteable<StoreSalesStatistic>().Where(row => row.Date == input.TargetDate).ExecuteCommandAsync();
                    if (atomicStoreStatistics.Any())
                        context.Db.Fastest<StoreSalesStatistic>().PageSize(BatchSize).BulkCopy(atomicStoreStatistics.ToList());
                }
                var deletedCount = await context.Db.Deleteable<ProductStoreDailySalesStatistic>()
                    .Where(row => row.Date == input.TargetDate).ExecuteCommandAsync();
                logger.LogInformation("删除 {Count} 条商品分店每日统计旧记录", deletedCount);
                if (build.Statistics.Any())
                    context.Db.Fastest<ProductStoreDailySalesStatistic>().PageSize(BatchSize).BulkCopy(build.Statistics);
                if (status.Status == SalesStatisticRefreshStatus.Fresh
                    || status.Status == SalesStatisticRefreshStatus.ProvisionalFresh)
                {
                    // 版本与供应商汇总必须基于数据库已持久化的 decimal 表示，避免内存中的无限精度
                    // 在 decimal(18,4) 落库后改变 hash 或供应商金额对账结果。
                    var persistedProductStatistics = await context.Db.Queryable<ProductStoreDailySalesStatistic>()
                        .Where(row => row.Date >= input.TargetDate.Date && row.Date < input.TargetDate.Date.AddDays(1))
                        .ToListAsync();
                    productVersion = SupplierStatisticVersion.ComputeProductVersion(persistedProductStatistics);
                    status = status with { SourceProductVersion = productVersion };
                    supplierBuild = await new SalesStatisticsSupplierStoreSummaryService()
                        .BuildFromProductStatisticsAsync(context, posmContext, persistedProductStatistics, DateTime.Now);
                    await new SalesStatisticsSupplierStoreSummaryService().PersistWithinTransactionAsync(
                        context,
                        input.TargetDate,
                        supplierBuild,
                        status.Status,
                        productVersion,
                        sourceWatermark: effectiveSourceWatermark);
                }
                // 所有商品与供应商派生数据写入后、四类状态发布前再次确认来源水位；
                // 回调失败会由统一事务执行器回滚整批写入。
                if (validateSourceWatermarkBeforeCommitAsync != null)
                    await validateSourceWatermarkBeforeCommitAsync();
                if (validateExecutionOwnershipBeforeCommitAsync != null)
                    await validateExecutionOwnershipBeforeCommitAsync();
                await SalesStatisticsProductStoreDailyStateSlice.UpsertProductStatisticStateAsync(
                    context,
                    input.TargetDate,
                    status,
                    effectiveSourceWatermark,
                    overwriteLastSourceUploadTime: atomicStoreStatistics != null
                );
                if (productVersion == null)
                {
                    // 诊断用失败结果已经替换商品行时，撤销旧发布版本。下一次排队不能把
                    // 这些未通过对账的商品行误认成仍与两张供应商表一致的旧完整快照。
                    await context.Db.Updateable<SalesStatisticRefreshState>()
                        .SetColumns(row => row.SourceProductVersion == null)
                        .Where(row => row.StatisticType == SalesStatisticType.ProductStoreDaily
                            && row.Date >= input.TargetDate.Date && row.Date < input.TargetDate.Date.AddDays(1))
                        .ExecuteCommandAsync();
                }
                // 投影只消费最终发布状态，并与主统计替换共用事务；缺表或非 SQL Server 直连时保持旧查询流程。
                await SalesDetailQueryProjection.RefreshDayIfSupportedAsync(
                    context,
                    posmContext,
                    input.TargetDate);
                if (atomicStoreStatistics != null)
                {
                    await SalesStatisticsProductStoreDailyStateSlice.UpsertStatisticStateAsync(
                        context,
                        SalesStatisticType.StoreSales,
                        input.TargetDate,
                        status.Status,
                        effectiveSourceWatermark,
                        status.ErrorMessage,
                        overwriteLastSourceUploadTime: true
                    );

                    // fence 在同一原子事务中、四类状态均已写入后捕获，避免提交后按日期重读时
                    // 被下一批替换，从而把 B 批误认为 A 批返回给批末 finalize/fail。
                    var capturedStates = await context.Db.Queryable<SalesStatisticRefreshState>()
                        .Where(row => row.Date >= input.TargetDate.Date
                            && row.Date < input.TargetDate.Date.AddDays(1)
                            && (row.StatisticType == SalesStatisticType.ProductStoreDaily
                                || row.StatisticType == SalesStatisticType.StoreSales
                                || row.StatisticType == SalesStatisticType.AustralianSupplierStoreSales
                                || row.StatisticType == SalesStatisticType.ChinaSupplierStoreSales))
                        .With(SqlWith.UpdLock)
                        .ToListAsync();
                    if (capturedStates.Count != 4)
                        throw new InvalidOperationException($"2025 批次四类状态写入不完整，拒绝返回 fence: {input.TargetDate:yyyy-MM-dd}");
                    capturedBatchFence = new ProductStoreDailyBatchFence(
                        input.TargetDate.Date,
                        capturedStates.Select(state => new ProductStoreDailyStateFence(
                            state.StatisticType,
                            state.Status,
                            state.SourceProductVersion,
                            state.JobId,
                            state.LastAggregatedAtUtc,
                            state.CompletedAtUtc,
                            state.LastSourceUploadTime)).ToList());
                }
            },
            commitAsync: () => context.Db.Ado.CommitTranAsync(),
            rollbackAsync: async () =>
            {
                await context.Db.Ado.RollbackTranAsync();
                // 事务执行器保留原始异常，即使回滚失败也会抛出锁异常；必须单独确认回滚成功。
                onRollbackCompleted();
            },
            logger: logger,
            operationName: "商品分店每日统计更新");
        return new PersistResult(status, capturedBatchFence);
    }

    internal static List<string> ResolveCurrentCostProductCodes(
        DateTime targetDate,
        IReadOnlyList<string> allProductCodes,
        IReadOnlyList<ProductStoreDailySalesStatistic> rebuilt,
        IReadOnlyList<ProductStoreDailySalesStatistic> previous,
        IReadOnlySet<string>? chinaSupplierCodes = null)
    {
        if (targetDate.Date >= SalesStatisticsBusinessDate.Today())
            return SetChildPurchasePriceMutationLock.NormalizeProductCodes(allProductCodes);

        var previousRows = new PreviousRowIndex(previous, chinaSupplierCodes);
        return SetChildPurchasePriceMutationLock.NormalizeProductCodes(rebuilt
            .Where(row =>
            {
                // OpenItem/身份冲突可能依赖目录条码推断，保留原有商品身份互斥，不能与目录编辑交错提交。
                if (IsOpenItemCostSource(row.CostSource) || IsOpenItemIdentityConflict(row.CostSource))
                    return true;
                var old = previousRows.Find(row);
                if (old == null)
                    return true;

                // 与 PreserveHistoricalCostSnapshots 的两条保留规则保持一致：日期锁已保护旧快照，
                // 完整旧金额或旧正数单价不依赖当前进价；缺失成本、新商品/分店组仍必须锁内重读。
                return !(old.TotalCost.HasValue && SameSalesFacts(row, old))
                    && old.UnitCostSnapshot is not > 0;
            })
            .Select(row => row.ProductCode));
    }

    internal static void PreserveHistoricalCostSnapshots(
        IReadOnlyList<ProductStoreDailySalesStatistic> rebuilt,
        IReadOnlyList<ProductStoreDailySalesStatistic> previous,
        IReadOnlySet<string>? chinaSupplierCodes = null)
    {
        var previousRows = new PreviousRowIndex(previous, chinaSupplierCodes);
        foreach (var row in rebuilt)
        {
            var old = previousRows.Find(row);
            var rebuiltIsOpenItem = IsOpenItemCostSource(row.CostSource);
            var rebuiltHasCredibleCost = row.CostSource != "Missing"
                && (row.TotalCost.HasValue || row.UnitCostSnapshot.HasValue);

            if (IsOpenItemIdentityConflict(row.CostSource))
            {
                // 混合身份已无法证明是同一开放商品，不能落入普通商品分支沿用旧单价。
                ClearCost(row);
                continue;
            }

            // OpenItem 的成本来自本次读取到的原明细价格。历史事实变化时必须重新按明细
            // 逐行求和，不能沿用旧的“单价 × 净数量”近似。
            if (rebuiltIsOpenItem)
            {
                if (!rebuiltHasCredibleCost && old != null
                    && IsTrustedOpenItemCostSource(old.CostSource)
                    && old.TotalCost.HasValue
                    && old.GrossProfit.HasValue
                    && IsMissingOriginalSaleEvidence(row.CostSource)
                    && SameOpenItemFacts(row, old))
                {
                    // 仅“原始销售明细缺失”允许保留已审计旧 OpenItem 成本；原价冲突即使
                    // 销售数量和金额未变，也必须留下缺口，不能掩盖新的原价证据问题。
                    RestoreHistoricalDerived(row, old);
                    continue;
                }
                if (!rebuiltHasCredibleCost)
                    ClearCost(row);
                continue;
            }

            if (old == null)
            {
                // 新行只有来源明确时才能补齐成本；测试/旧快照中的裸 UnitCost 不构成证据。
                if (!rebuiltHasCredibleCost)
                    ClearCost(row);
                continue;
            }

            if (old.TotalCost.HasValue
                && SameSalesFacts(row, old))
            {
                // 事实未变化时，完整旧派生值就是历史快照；即使旧快照没有单价，也不能
                // 让当天成本表的当前值覆盖已经审计过的历史金额。
                RestoreHistoricalDerived(row, old);
                continue;
            }

            if (old.UnitCostSnapshot is > 0)
            {
                // 普通商品历史单价优先；事实数量变化时只按历史单价派生新总成本，
                // 不复制已过期的 TotalCost/GrossProfit。
                row.UnitCostSnapshot = old.UnitCostSnapshot;
                row.CostSource = old.CostSource;
                // 普通商品事实变化时沿用历史快照单价，避免按当前进价重算历史毛利。
                row.TotalCost = old.UnitCostSnapshot * row.TotalQuantity;
                row.GrossProfit = row.TotalCost.HasValue ? row.TotalAmount - row.TotalCost.Value : null;
                row.GrossMarginRate = row.TotalAmount > 0 && row.GrossProfit.HasValue
                    ? row.GrossProfit.Value / row.TotalAmount : null;
                continue;
            }

            if (!rebuiltHasCredibleCost)
                ClearCost(row);
        }
    }

    /// <summary>
    /// 按行键找旧快照行。SupplierCode 是行键的一部分，而同一商品的国内货在不同时间可能被写成
    /// 国内编码族内不同的编码：旧写法的 200、直写的国内供应商编码、归属变更后的新编码，
    /// 开关关闭后又会写回 200。精确键找不到时，族内按「日期 + 分店 + 商品」再找一次；
    /// 否则历史日期一重算，国内货的旧成本快照就会因为键对不上，被当成新行按当前进价重算，且无法恢复。
    /// </summary>
    private sealed class PreviousRowIndex
    {
        private readonly Dictionary<(DateTime, string, string, string), ProductStoreDailySalesStatistic> _byKey;
        private readonly Dictionary<(DateTime, string, string), List<ProductStoreDailySalesStatistic>> _familyRows = new();
        private readonly IReadOnlySet<string>? _chinaSupplierCodes;

        internal PreviousRowIndex(
            IReadOnlyList<ProductStoreDailySalesStatistic> previous,
            IReadOnlySet<string>? chinaSupplierCodes)
        {
            _chinaSupplierCodes = chinaSupplierCodes;
            _byKey = previous.ToDictionary(
                row => (row.Date.Date, row.BranchCode, row.SupplierCode, row.ProductCode));
            if (chinaSupplierCodes == null)
                return;

            foreach (var row in previous.Where(row =>
                ChinaSupplierCodeFamily.IsFamilyCode(row.SupplierCode, chinaSupplierCodes)))
            {
                var key = (row.Date.Date, row.BranchCode, row.ProductCode);
                if (!_familyRows.TryGetValue(key, out var rows))
                    _familyRows[key] = rows = new List<ProductStoreDailySalesStatistic>();
                rows.Add(row);
            }
        }

        internal ProductStoreDailySalesStatistic? Find(ProductStoreDailySalesStatistic rebuilt)
        {
            if (_byKey.TryGetValue(
                (rebuilt.Date.Date, rebuilt.BranchCode, rebuilt.SupplierCode, rebuilt.ProductCode), out var exact))
                return exact;
            if (_chinaSupplierCodes == null
                || !ChinaSupplierCodeFamily.IsFamilyCode(rebuilt.SupplierCode, _chinaSupplierCodes)
                || !_familyRows.TryGetValue((rebuilt.Date.Date, rebuilt.BranchCode, rebuilt.ProductCode), out var candidates))
                return null;

            // 正常情况下族内只有一行。真有多行时取 200 那行，再按编码排序，保证结果稳定；
            // 同一商品的成本与归属无关，取哪一行都不会引入当前进价。
            return candidates
                .OrderByDescending(row => ChinaSupplierCodeFamily.IsLocalSupplierCode(row.SupplierCode))
                .ThenBy(row => row.SupplierCode, StringComparer.Ordinal)
                .First();
        }
    }

    private static bool IsOpenItemCostSource(string? costSource) =>
        costSource?.StartsWith("OpenItem", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsTrustedOpenItemCostSource(string? costSource) =>
        string.Equals(costSource, "OpenItem", StringComparison.OrdinalIgnoreCase)
        || string.Equals(costSource, "OpenItemOriginalPrice", StringComparison.OrdinalIgnoreCase);

    private static bool IsMissingOriginalSaleEvidence(string? costSource) =>
        costSource?.Contains("MissingOriginalSale", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsOpenItemIdentityConflict(string? costSource) =>
        string.Equals(costSource, "IdentityConflict", StringComparison.OrdinalIgnoreCase)
        || string.Equals(costSource, "OpenItemIdentityConflict", StringComparison.OrdinalIgnoreCase);

    private static bool SameSalesFacts(
        ProductStoreDailySalesStatistic rebuilt,
        ProductStoreDailySalesStatistic previous) =>
        rebuilt.TotalQuantity == previous.TotalQuantity
        && rebuilt.OrderCount == previous.OrderCount
        && Math.Round(rebuilt.TotalAmount, 4, MidpointRounding.AwayFromZero)
            == Math.Round(previous.TotalAmount, 4, MidpointRounding.AwayFromZero);

    private static bool SameOpenItemFacts(
        ProductStoreDailySalesStatistic rebuilt,
        ProductStoreDailySalesStatistic previous) =>
        SameSalesFacts(rebuilt, previous)
        && rebuilt.LastSourceUploadTime == previous.LastSourceUploadTime;

    private static void RestoreHistoricalDerived(
        ProductStoreDailySalesStatistic row,
        ProductStoreDailySalesStatistic old)
    {
        row.UnitCostSnapshot = old.UnitCostSnapshot;
        row.CostSource = old.CostSource;
        row.TotalCost = old.TotalCost;
        // 旧行可能只保存 TotalCost；补齐缺失 derived，但保留合法的 0 值和已有值。
        row.GrossProfit = old.GrossProfit ?? row.TotalAmount - old.TotalCost!.Value;
        row.GrossMarginRate = old.GrossMarginRate
            ?? (row.TotalAmount > 0m && row.GrossProfit.HasValue
                ? row.GrossProfit.Value / row.TotalAmount
                : null);
    }

    private static void ClearCost(ProductStoreDailySalesStatistic row, string? costSource = null)
    {
        row.UnitCostSnapshot = null;
        row.TotalCost = null;
        row.GrossProfit = null;
        row.GrossMarginRate = null;
        // 清理成本时保留领域层给出的异常原因，避免把身份/原价冲突伪装成普通 Missing。
        row.CostSource = string.IsNullOrWhiteSpace(costSource)
            ? string.IsNullOrWhiteSpace(row.CostSource) ? "Missing" : row.CostSource
            : costSource;
    }

}
