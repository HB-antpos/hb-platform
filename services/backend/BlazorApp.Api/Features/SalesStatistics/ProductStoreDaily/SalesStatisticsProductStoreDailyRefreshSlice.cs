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
    /// <summary>销售统计垂直切片：SalesStatisticsProductStoreDailyRefreshSlice。</summary>
    internal sealed class SalesStatisticsProductStoreDailyRefreshSlice : SalesStatisticsSliceBase
    {
        private readonly SalesStatisticsProductStoreDailySupportSlice _productSupport;

        /// <summary>失败状态写入的 CAS 基线，防止旧刷新覆盖后来发布的状态。</summary>
        internal sealed record ProductStatisticFailureFence(
            string Status,
            string? SourceProductVersion,
            Guid? JobId,
            DateTime? LastAggregatedAtUtc,
            DateTime? CompletedAtUtc,
            DateTime? LastSourceUploadTime);

        public SalesStatisticsProductStoreDailyRefreshSlice(
            SalesStatisticsSliceContext shared,
            SalesStatisticsProductStoreDailySupportSlice productSupport)
            : base(shared)
        {
            _productSupport = productSupport;
        }

    internal async Task<ProductStoreDailyBatchFence?> UpdateProductStoreDailyStatisticsWithContext(
        SqlSugarContext context,
        POSMSqlSugarContext posmContext,
        HBSalesRecordSqlSugarContext? hbSalesContext,
        ILogger logger,
        DateTime date,
        IReadOnlyList<StoreSalesStatistic>? atomicStoreStatistics = null,
        DateTime? sourceWatermarkOverride = null,
        Func<Task>? validateSourceWatermarkBeforeCommitAsync = null,
        IReadOnlyList<ProductStoreDailySourceRow>? preloadedHBSalesRows = null,
        string? atomicSuccessStatusOverride = null,
        Posm2025DailySnapshot? preloadedPosmSnapshot = null,
        Guid? expectedJobId = null,
        Func<Task>? validateExecutionOwnershipBeforeCommitAsync = null,
        ProductStatisticFailureFence? initialProductStateFence = null
    )
    {
        var targetDate = date.Date;
        initialProductStateFence ??= await CaptureProductStatisticFailureFenceAsync(context, targetDate);
        var useCurrentDaySnapshot = SalesStatisticsBusinessDate.IsToday(targetDate) && targetDate.Year != 2025
            && atomicStoreStatistics == null && preloadedPosmSnapshot == null
            && validateSourceWatermarkBeforeCommitAsync == null;
        DateTime? currentDaySourceWatermark = null;
        var originalCommandTimeout = context.Db.Ado.CommandTimeOut;
        context.Db.Ado.CommandTimeOut = Math.Max(originalCommandTimeout, CommandTimeoutSeconds);
        try
        {
            logger.LogInformation("开始更新商品分店每日统计: {Date}", targetDate);
            // 开关只影响本次写出的 SupplierCode；库里新旧两种写法的行可以混存，读取侧都认。
            var writeDirectChinaSupplierCode =
                _configuration?.GetValue<bool>(ChinaSupplierCodeFamily.WriteDirectCodeConfigKey) ?? false;
            ProductStoreDailyRefreshInput input = null!;
            if (useCurrentDaySnapshot)
            {
                (input, atomicStoreStatistics) = await new SalesStatisticsProductStoreDailySourceReader()
                    .LoadCurrentDaySnapshotAsync(context, posmContext, logger, targetDate,
                        () => _productSupport.BuildStoreStatisticsAsync(
                            context, posmContext, hbSalesContext, targetDate, null),
                        writeDirectChinaSupplierCode);
                currentDaySourceWatermark = input.LastSourceUploadTime;
            }
            else
            {
                input = await new SalesStatisticsProductStoreDailySourceReader().LoadAsync(
                    context, posmContext, hbSalesContext, logger, targetDate,
                    preloadedHBSalesRows, preloadedPosmSnapshot,
                    writeDirectChinaSupplierCode: writeDirectChinaSupplierCode);
            }
            if (writeDirectChinaSupplierCode)
            {
                logger.LogInformation(
                    "商品分店每日统计直写国内供应商编码: {Date}, 解析出国内供应商的商品 {ResolvedProductCount} 个",
                    targetDate, input.ChinaSupplierByProduct.Count);
            }
            var build = new SalesStatisticsProductStoreDailyBuilder().Build(input);
            var sourceWatermarkValidator = validateSourceWatermarkBeforeCommitAsync;
            if (sourceWatermarkValidator == null && targetDate.Year != 2025 && !useCurrentDaySnapshot)
            {
                // 普通日入口没有外层协调器回调时，仍须在事务前后复核同一日 POSM 水位。
                // 2025 入口由原子协调器提供同时覆盖 POSM/HBSales 的签名校验。
                sourceWatermarkValidator = async () =>
                {
                    var currentWatermark = await SalesStatisticsProductStoreDailyStateSlice
                        .QueryDailyPosmSourceWatermarkAsync(posmContext, targetDate);
                    if (currentWatermark != input.LastSourceUploadTime)
                    {
                        throw new InvalidOperationException(
                            $"商品分店每日统计构建期间来源水位发生变化，拒绝提交: {targetDate:yyyy-MM-dd}");
                    }
                };
            }
            var persistResult = await new SalesStatisticsProductStoreDailyCommandWriter().PersistAsync(
                context, posmContext, logger, input, build, atomicStoreStatistics, sourceWatermarkOverride,
                sourceWatermarkValidator,
                atomicSuccessStatusOverride,
                expectedJobId,
                validateExecutionOwnershipBeforeCommitAsync);
            var status = persistResult.Status;
            logger.LogInformation(
                "商品分店每日统计更新完成: {Date}, 总记录: {Total}, 状态: {Status}",
                targetDate, build.Statistics.Count, status.Status);
            return persistResult.BatchFence;
        }
        catch (Exception ex)
        {
            if (useCurrentDaySnapshot)
            {
                await Persist2025AtomicFailureStatesAsync(
                    context, logger, targetDate, currentDaySourceWatermark, ex,
                    expectedJobId, validateExecutionOwnershipBeforeCommitAsync,
                    expectedProductStateFence: initialProductStateFence);
            }
            else if (atomicStoreStatistics == null)
            {
                try
                {
                    await PersistProductStatisticFailureAsync(
                        context,
                        logger,
                        targetDate,
                        ex,
                        expectedJobId,
                        validateExecutionOwnershipBeforeCommitAsync,
                        initialProductStateFence);
                }
                catch (Exception stateException)
                {
                    // 失败状态无法持久化时仅记录附加错误，不能覆盖原始统计异常。
                    logger.LogError(stateException, "写入商品分店每日统计失败状态失败: {Date}", targetDate);
                }
            }
            else
            {
                // 2025 原子入口统一由外层成对写入失败状态，避免内外层重复或单边落状态。
                logger.LogDebug(ex, "2025 双表原子统计失败，交由外层持久化成对失败状态: {Date}", targetDate);
            }
            logger.LogError(ex, "更新商品分店每日统计失败: {Date}", targetDate);
            throw;
        }
        finally
        {
            context.Db.Ado.CommandTimeOut = originalCommandTimeout;
        }
    }

    // 拆分迁移期间保留旧实现作为行为对照；下一步删除其重复逻辑。
    internal async Task<ProductStoreDailyBatchFence> Update2025StoreAndProductStatisticsAtomically(
        SqlSugarContext context,
        POSMSqlSugarContext posmContext,
        HBSalesRecordSqlSugarContext? hbSalesContext,
        ILogger logger,
        DateTime date,
        DateTime? sourceWatermarkOverride = null,
        IReadOnlyList<ProductStoreDailySourceRow>? preloadedHBSalesRows = null,
        HBSales2025DailySnapshotSignature? expectedHBSalesSignature = null,
        bool deferHBSalesStabilityToBatchEnd = false,
        Posm2025DailySnapshot? preloadedPosmSnapshot = null,
        Guid? expectedJobId = null,
        Func<Task>? validateExecutionOwnershipAsync = null
    )
    {
        var targetDate = date.Date;
        var initialProductStateFence = await CaptureProductStatisticFailureFenceAsync(context, targetDate);
        DateTime? effectiveSourceWatermark = null;
        try
        {
            var requiredHBSalesContext = hbSalesContext
                ?? throw new InvalidOperationException("2025 年统计缺少 HBSalesRecord 上下文");
            // 先读取明细快照：分店/商品统计和 pre 水位必须基于同一批 HBSales 行，避免额外聚合扫描。
            var hbSalesRows = preloadedHBSalesRows?.ToList()
                ?? await SalesStatisticsProductStoreDailySourceReader
                    .LoadHBSalesProductStoreDailyRowsAsync(
                    requiredHBSalesContext,
                    targetDate,
                    targetDate.AddDays(1)
                );
            if (expectedHBSalesSignature != null)
            {
                var actualSignature = SalesStatisticsProductStoreDailyDomainRules
                    .CreateHBSales2025DailySnapshotSignature(
                    targetDate,
                    hbSalesRows
                );
                if (actualSignature != expectedHBSalesSignature)
                {
                    throw new InvalidOperationException(
                        $"2025 批量预载 HBSales 签名不匹配，拒绝提交: {targetDate:yyyy-MM-dd}"
                    );
                }
            }
            // 构建前先固定 POSM 与 HBSales 水位。批量路径只在日内复核 POSM，
            // HBSales 由批末的同范围签名一次复核，避免每天再扫描千万级详情表。
            var prePosmSourceWatermark = preloadedPosmSnapshot == null
                ? await SalesStatisticsProductStoreDailyStateSlice
                    .QueryDailyPosmSourceWatermarkAsync(posmContext, targetDate)
                : SalesStatisticsProductStoreDailyDomainRules.GetPosmSnapshotWatermark(
                    preloadedPosmSnapshot
                );
            var preSourceWatermark = SalesStatisticsProductStoreDailyDomainRules.GetLatestSourceTime(
                prePosmSourceWatermark,
                SalesStatisticsProductStoreDailyDomainRules.GetHBSalesSourceWatermark(hbSalesRows)
            );
            effectiveSourceWatermark = preSourceWatermark;
            if (sourceWatermarkOverride.HasValue
                && preSourceWatermark != sourceWatermarkOverride)
            {
                throw new InvalidOperationException(
                    $"2025 双表统计来源水位在调用后已变化，拒绝提交: {targetDate:yyyy-MM-dd}"
                );
            }

            // 事务开始前完成两套来源读取和两张表计算，缩短主库锁定窗口。
            var storeBuildStopwatch = Stopwatch.StartNew();
            var storeStatistics = await _productSupport.BuildStoreStatisticsAsync(
                context,
                posmContext,
                requiredHBSalesContext,
                targetDate,
                null,
                hbSalesRows,
                preloadedPosmSnapshot
            );
            logger.LogInformation("2025 Runner store build 完成: {Date}, {ElapsedMilliseconds}ms", targetDate, storeBuildStopwatch.ElapsedMilliseconds);
            var productBuildStopwatch = Stopwatch.StartNew();
            var sourceValidationInvocation = 0;
            var persistResult = await UpdateProductStoreDailyStatisticsWithContext(
                context,
                posmContext,
                requiredHBSalesContext,
                logger,
                targetDate,
                storeStatistics,
                effectiveSourceWatermark,
                async () =>
                {
                    var postSignatureStopwatch = Stopwatch.StartNew();
                    var isPreCommitValidation = Interlocked.Exchange(ref sourceValidationInvocation, 1) == 0;
                    if (preloadedPosmSnapshot != null)
                    {
                        // 提交前只复核同日来源；四张表逐表比较，任一张漂移都拒绝写入 HBweb。
                        var postPosmSnapshot = await SalesStatisticsProductStoreDailySourceQueries
                            .Load2025PosmDailySnapshotAsync(posmContext, targetDate);
                        if (postPosmSnapshot.Signature != preloadedPosmSnapshot.Signature)
                        {
                            throw new InvalidOperationException(
                                $"2025 Runner POSM 日快照签名发生变化，拒绝提交: {targetDate:yyyy-MM-dd}"
                            );
                        }
                    }
                    var postSourceWatermark = deferHBSalesStabilityToBatchEnd
                        // 批量路径把 HBSales 放到批末一次复核；日内仍必须独立复核 POSM。
                        ? await SalesStatisticsProductStoreDailyStateSlice
                            .QueryDailyPosmSourceWatermarkAsync(posmContext, targetDate)
                        : isPreCommitValidation
                            // pre 阶段复用已装载 HBSales 行，避免在事务前重复扫描同一批明细；
                            // post 阶段重新查两来源，检测构建窗口内的漂移。
                            ? SalesStatisticsProductStoreDailyDomainRules.GetLatestSourceTime(
                                await SalesStatisticsProductStoreDailyStateSlice
                                    .QueryDailyPosmSourceWatermarkAsync(posmContext, targetDate),
                                SalesStatisticsProductStoreDailyDomainRules.GetHBSalesSourceWatermark(hbSalesRows))
                            : await SalesStatisticsProductStoreDailyStateSlice.QueryDailySourceWatermarkAsync(
                                posmContext,
                                requiredHBSalesContext,
                                targetDate
                            );
                    var sourceWatermarkToCompare = deferHBSalesStabilityToBatchEnd
                        ? prePosmSourceWatermark
                        : preSourceWatermark;
                    if (postSourceWatermark != sourceWatermarkToCompare)
                    {
                        throw new InvalidOperationException(
                            $"2025 双表统计构建期间来源水位发生变化，拒绝提交: {targetDate:yyyy-MM-dd}"
                        );
                    }
                    logger.LogInformation("2025 Runner post signature 完成: {Date}, {ElapsedMilliseconds}ms", targetDate, postSignatureStopwatch.ElapsedMilliseconds);
                },
                hbSalesRows,
                deferHBSalesStabilityToBatchEnd ? ProvisionalFreshStatus : null,
                preloadedPosmSnapshot,
                expectedJobId,
                validateExecutionOwnershipAsync,
                initialProductStateFence
            );
            logger.LogInformation("2025 Runner product build 完成: {Date}, {ElapsedMilliseconds}ms", targetDate, productBuildStopwatch.ElapsedMilliseconds);
            return persistResult ?? throw new InvalidOperationException(
                $"2025 原子统计未返回批次 fence: {targetDate:yyyy-MM-dd}");
        }
        catch (Exception ex)
        {
            await Persist2025AtomicFailureStatesAsync(
                context,
                logger,
                targetDate,
                effectiveSourceWatermark,
                ex,
                expectedJobId,
                validateExecutionOwnershipAsync,
                expectedProductStateFence: initialProductStateFence
            );
            throw;
        }
    }

    internal async Task Persist2025AtomicFailureStatesAsync(
        SqlSugarContext context,
        ILogger logger,
        DateTime targetDate,
        DateTime? effectiveSourceWatermark,
        Exception originalException,
        Guid? expectedJobId = null,
        Func<Task>? validateExecutionOwnershipAsync = null,
        ProductStoreDailyBatchFence? expectedBatchFence = null,
        ProductStatisticFailureFence? expectedProductStateFence = null
    )
    {
        if (SalesStatisticsCostWriteLock.IsBusy(originalException))
        {
            logger.LogWarning(originalException, "2025 双表统计失败由成本日期锁忙导致，仅记录不覆盖失败状态: {Date}", targetDate);
            return;
        }
        expectedProductStateFence ??= ToProductStatisticFailureFence(expectedBatchFence);
        try
        {
            // 主统计事务已回滚或尚未开始；此处独立事务保证商品、分店及两类供应商 Failed 状态成对提交。
            await SalesStatisticsTransactionExecutor.ExecuteAsync(
                beginAsync: () => context.Db.Ado.BeginTranAsync(),
                workAsync: async () =>
                {
                    await SalesStatisticsCostWriteLock.AcquireAsync(context.Db, targetDate);
                    if (!await IsProductStatisticFailureFenceCurrentAsync(
                            context, targetDate, expectedProductStateFence))
                    {
                        logger.LogWarning(
                            "2025 统计失败状态基线已变化，仅记录不覆盖新状态: {Date}",
                            targetDate);
                        return;
                    }
                    await SalesStatisticsProductStoreDailyStateSlice.FenceProductStatisticExecutionOwnerAsync(
                        context,
                        targetDate,
                        expectedJobId);
                    if (validateExecutionOwnershipAsync != null)
                        await validateExecutionOwnershipAsync();
                    if (expectedBatchFence != null)
                    {
                        var states = await context.Db.Queryable<SalesStatisticRefreshState>()
                            .Where(state =>
                                state.Date >= targetDate.Date
                                && state.Date < targetDate.Date.AddDays(1)
                                && (state.StatisticType == SalesStatisticType.ProductStoreDaily
                                    || state.StatisticType == SalesStatisticType.StoreSales
                                    || state.StatisticType == SalesStatisticType.AustralianSupplierStoreSales
                                    || state.StatisticType == SalesStatisticType.ChinaSupplierStoreSales))
                            .With(SqlWith.UpdLock)
                            .ToListAsync();
                        SalesStatisticsProductStoreDailyBatchFenceOperations.Validate(
                            states,
                            expectedBatchFence,
                            ProvisionalFreshStatus);
                    }
                    // 商品编码无法解析是商品主档漂移，被拒绝时旧快照原封不动；
                    // 此时整天标 Failed 只会让含该日（含同期对比）的报表全部打不开，改为保留已发布状态并记告警。
                    if (HBSalesProductCodeUnresolvedMarker.IsMarked(originalException)
                        && await TryKeepPublishedSnapshotAsync(context, targetDate))
                    {
                        logger.LogWarning(
                            originalException,
                            "2025 重算被拒绝（商品编码无法解析），保留上一次已发布统计，不标记 Failed: {Date}",
                            targetDate);
                        return;
                    }
                    await SalesStatisticsProductStoreDailyStateSlice.UpsertProductStatisticStateAsync(
                        context,
                        targetDate,
                        new SalesStatisticsProductStoreDailyStateSlice.ProductStatisticStatusResult(
                            SalesStatisticRefreshStatus.Failed,
                            originalException.Message
                        ),
                        effectiveSourceWatermark,
                        overwriteLastSourceUploadTime: true
                    );
                    await SalesStatisticsProductStoreDailyStateSlice.UpsertStatisticStateAsync(
                        context,
                        SalesStatisticType.StoreSales,
                        targetDate,
                        SalesStatisticRefreshStatus.Failed,
                        effectiveSourceWatermark,
                        originalException.Message,
                        overwriteLastSourceUploadTime: true
                    );
                    await SalesStatisticsProductStoreDailyStateSlice.UpsertStatisticStateAsync(
                        context,
                        SalesStatisticType.AustralianSupplierStoreSales,
                        targetDate,
                        SalesStatisticRefreshStatus.Failed,
                        effectiveSourceWatermark,
                        originalException.Message,
                        overwriteLastSourceUploadTime: true
                    );
                    await SalesStatisticsProductStoreDailyStateSlice.UpsertStatisticStateAsync(
                        context,
                        SalesStatisticType.ChinaSupplierStoreSales,
                        targetDate,
                        SalesStatisticRefreshStatus.Failed,
                        effectiveSourceWatermark,
                        originalException.Message,
                        overwriteLastSourceUploadTime: true
                    );
                },
                commitAsync: () => context.Db.Ado.CommitTranAsync(),
                rollbackAsync: () => context.Db.Ado.RollbackTranAsync(),
                logger: logger,
                operationName: "2025 四类统计失败状态写入"
            );
        }
        catch (Exception stateException)
        {
            if (SalesStatisticsCostWriteLock.IsBusy(stateException))
            {
                logger.LogWarning(
                    stateException,
                    "2025 双表统计失败状态写入遇到成本日期锁忙，仅记录不覆盖失败状态: {Date}",
                    targetDate);
                return;
            }
            // 状态持久化失败只补充日志；调用方必须仍收到最初的业务或事务异常。
            logger.LogError(
                stateException,
                "2025 双表统计失败状态写入失败，保留原始异常: {Date}, OriginalError={OriginalError}",
                targetDate,
                originalException.Message
            );
        }
    }

    /// <summary>
    /// 被拒绝的重算没有写入任何事实，商品/分店/供应商统计仍是上一次的发布版本；
    /// 但排队与认领已清空商品日统计的完成时间。仅当同日其余三类状态仍证明上次发布完整
    /// （均为 Fresh、两类供应商与商品版本一致）时，才把商品状态还原为 Fresh；
    /// 否则返回 false，由调用方走原有的成对 Failed 写入。必须在失败状态事务内、已持有日期锁时调用。
    /// </summary>
    internal static async Task<bool> TryKeepPublishedSnapshotAsync(SqlSugarContext context, DateTime targetDate)
    {
        var date = targetDate.Date;
        var nextDate = date.AddDays(1);
        var states = await context.Db.Queryable<SalesStatisticRefreshState>()
            .Where(state => state.Date >= date && state.Date < nextDate
                && (state.StatisticType == SalesStatisticType.ProductStoreDaily
                    || state.StatisticType == SalesStatisticType.StoreSales
                    || state.StatisticType == SalesStatisticType.AustralianSupplierStoreSales
                    || state.StatisticType == SalesStatisticType.ChinaSupplierStoreSales))
            .With(SqlWith.UpdLock)
            .ToListAsync();
        var product = states.SingleOrDefault(state => state.StatisticType == SalesStatisticType.ProductStoreDaily);
        if (product == null
            || (product.Status != SalesStatisticRefreshStatus.Running && product.Status != SalesStatisticRefreshStatus.Queued)
            || !product.LastAggregatedAtUtc.HasValue
            || string.IsNullOrWhiteSpace(product.SourceProductVersion))
            return false;

        bool IsPublished(string statisticType, bool requireSameVersion)
        {
            var row = states.SingleOrDefault(state => state.StatisticType == statisticType);
            return row != null
                && row.Status == SalesStatisticRefreshStatus.Fresh
                && row.CompletedAtUtc.HasValue
                && (!requireSameVersion || row.SourceProductVersion == product.SourceProductVersion);
        }

        if (!IsPublished(SalesStatisticType.StoreSales, requireSameVersion: false)
            || !IsPublished(SalesStatisticType.AustralianSupplierStoreSales, requireSameVersion: true)
            || !IsPublished(SalesStatisticType.ChinaSupplierStoreSales, requireSameVersion: true))
            return false;

        product.Status = SalesStatisticRefreshStatus.Fresh;
        // 完成时间沿用上次发布的聚合时间，保持报表缓存版本不因这次被拒绝的重算而变化。
        product.CompletedAtUtc = product.LastAggregatedAtUtc;
        product.ErrorMessage = null;
        product.LastCheckedAtUtc = DateTime.UtcNow;
        await context.Db.Updateable(product).ExecuteCommandAsync();
        return true;
    }

    internal static async Task PersistProductStatisticFailureAsync(
        SqlSugarContext context,
        ILogger logger,
        DateTime targetDate,
        Exception originalException,
        Guid? expectedJobId,
        Func<Task>? validateExecutionOwnershipAsync,
        ProductStatisticFailureFence? expectedProductStateFence)
    {
        if (SalesStatisticsCostWriteLock.IsBusy(originalException))
        {
            logger.LogWarning(originalException, "商品统计刷新失败由成本日期锁忙导致，仅记录不覆盖失败状态: {Date}", targetDate);
            return;
        }

        try
        {
            await SalesStatisticsTransactionExecutor.ExecuteAsync(
                beginAsync: () => context.Db.Ado.BeginTranAsync(),
                workAsync: async () =>
                {
                    await SalesStatisticsCostWriteLock.AcquireAsync(context.Db, targetDate);
                    if (!await IsProductStatisticFailureFenceCurrentAsync(
                            context, targetDate, expectedProductStateFence))
                    {
                        logger.LogWarning(
                            "商品统计失败状态基线已变化，仅记录不覆盖新状态: {Date}",
                            targetDate);
                        return;
                    }
                    await SalesStatisticsProductStoreDailyStateSlice.FenceProductStatisticExecutionOwnerAsync(
                        context, targetDate, expectedJobId);
                    if (validateExecutionOwnershipAsync != null)
                        await validateExecutionOwnershipAsync();
                    await SalesStatisticsProductStoreDailyStateSlice.UpsertProductStatisticStateAsync(
                        context,
                        targetDate,
                        new SalesStatisticsProductStoreDailyStateSlice.ProductStatisticStatusResult(
                            SalesStatisticRefreshStatus.Failed,
                            originalException.Message),
                        null);
                },
                commitAsync: () => context.Db.Ado.CommitTranAsync(),
                rollbackAsync: () => context.Db.Ado.RollbackTranAsync(),
                logger: logger,
                operationName: "商品分店每日统计失败状态写入");
        }
        catch (Exception stateException) when (SalesStatisticsCostWriteLock.IsBusy(stateException))
        {
            logger.LogWarning(
                stateException,
                "商品统计失败状态写入遇到成本日期锁忙，仅记录不覆盖失败状态: {Date}",
                targetDate);
        }
    }

    internal static async Task<ProductStatisticFailureFence?> CaptureProductStatisticFailureFenceAsync(
        SqlSugarContext context,
        DateTime targetDate)
    {
        var normalizedDate = targetDate.Date;
        var state = await context.Db.Queryable<SalesStatisticRefreshState>()
            .Where(item => item.StatisticType == SalesStatisticType.ProductStoreDaily
                && item.Date >= normalizedDate
                && item.Date < normalizedDate.AddDays(1))
            .FirstAsync();
        return state == null
            ? null
            : new ProductStatisticFailureFence(
                state.Status,
                state.SourceProductVersion,
                state.JobId,
                state.LastAggregatedAtUtc,
                state.CompletedAtUtc,
                state.LastSourceUploadTime);
    }

    private static async Task<bool> IsProductStatisticFailureFenceCurrentAsync(
        SqlSugarContext context,
        DateTime targetDate,
        ProductStatisticFailureFence? expected)
    {
        var normalizedDate = targetDate.Date;
        var state = await context.Db.Queryable<SalesStatisticRefreshState>()
            .Where(item => item.StatisticType == SalesStatisticType.ProductStoreDaily
                && item.Date >= normalizedDate
                && item.Date < normalizedDate.AddDays(1))
            .With(SqlWith.UpdLock)
            .FirstAsync();
        if (expected == null)
            return state == null;
        return state != null
            && state.Status == expected.Status
            && state.SourceProductVersion == expected.SourceProductVersion
            && state.JobId == expected.JobId
            && state.LastAggregatedAtUtc == expected.LastAggregatedAtUtc
            && state.CompletedAtUtc == expected.CompletedAtUtc
            && state.LastSourceUploadTime == expected.LastSourceUploadTime;
    }

    private static ProductStatisticFailureFence? ToProductStatisticFailureFence(
        ProductStoreDailyBatchFence? expectedBatchFence)
    {
        var productFence = expectedBatchFence?.States
            .SingleOrDefault(item => item.StatisticType == SalesStatisticType.ProductStoreDaily);
        return productFence == null
            ? null
            : new ProductStatisticFailureFence(
                ProvisionalFreshStatus,
                productFence.SourceProductVersion,
                productFence.JobId,
                productFence.LastAggregatedAtUtc,
                productFence.CompletedAtUtc,
                productFence.LastSourceUploadTime);
    }

    internal static string ResolveBranchCode(
        string? branchCode,
        string? deviceCode,
        Dictionary<string, string> deviceBranchMap
    ) => SalesStatisticsCodeRules.ResolveBranchCode(branchCode, deviceCode, deviceBranchMap);

    internal static DateTime? GetPosmSnapshotWatermark(Posm2025DailySnapshot snapshot) =>
        SalesStatisticsProductStoreDailyDomainRules.GetPosmSnapshotWatermark(snapshot);

    internal static Task<List<StoreCostRow>> LoadStoreCostsInBatchesAsync(
        SqlSugarContext context,
        IReadOnlyCollection<string> productCodes,
        IReadOnlyCollection<string> branchCodes
    ) => SalesStatisticsProductStoreDailySourceReader.LoadStoreCostsInBatchesAsync(
        context, productCodes, branchCodes
    );
    internal static Task<List<ProductStoreDailySourceRow>> LoadHBSalesProductStoreDailyRowsAsync(
        HBSalesRecordSqlSugarContext hbSalesContext,
        DateTime targetDate,
        DateTime nextDate,
        int? maxRows = null
    ) => SalesStatisticsProductStoreDailySourceReader.LoadHBSalesProductStoreDailyRowsAsync(
        hbSalesContext, targetDate, nextDate, maxRows
    );
    internal static HBSales2025DailySnapshotSignature CreateHBSales2025DailySnapshotSignature(
        DateTime date,
        IEnumerable<ProductStoreDailySourceRow> rows
    ) => SalesStatisticsProductStoreDailyDomainRules
        .CreateHBSales2025DailySnapshotSignature(date, rows);

    internal static void AppendHBSalesSignatureValue(IncrementalHash checksum, object? value) =>
        SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(checksum, value);

    internal static Posm2025DailySnapshotSignature CreatePosm2025DailySnapshotSignature(
        DateTime date,
        IEnumerable<StoreStatisticOrderRow> orders,
        IEnumerable<ProductStoreDailySourceRow> details,
        IEnumerable<StoreStatisticPaymentRow> payments,
        IEnumerable<ProductStoreDailySourceRow> salesReturns
    ) => SalesStatisticsProductStoreDailyDomainRules.CreatePosm2025DailySnapshotSignature(
        date,
        orders,
        details,
        payments,
        salesReturns
    );

    internal static Posm2025DailyTableSignature CreatePosmTableSignature<T>(
        IReadOnlyList<T> rows,
        Func<T, DateTime?> lastModifiedSelector,
        Func<T, DateTime?> createdSelector,
        Func<T, object?[]> valuesSelector
    ) => SalesStatisticsProductStoreDailyDomainRules.CreatePosmTableSignature(
        rows,
        lastModifiedSelector,
        createdSelector,
        valuesSelector
    );

    internal static Task ResolveMissingHBSalesProductCodesAsync(
        SqlSugarContext context,
        IReadOnlyList<ProductStoreDailySourceRow> hbSalesRows
    ) => SalesStatisticsProductStoreDailySourceReader.ResolveMissingHBSalesProductCodesAsync(
        context, hbSalesRows
    );
    internal static string SelectDeterministicProductCode(IEnumerable<string> candidates) =>
        SalesStatisticsProductStoreDailyDomainRules.SelectDeterministicProductCode(candidates);

    internal static DateTime? GetLatestSourceTime(params DateTime?[] timestamps) =>
        SalesStatisticsProductStoreDailyDomainRules.GetLatestSourceTime(timestamps);

    internal static DateTime? GetLatestSourceTime(IEnumerable<ProductStoreDailySourceRow> rows) =>
        SalesStatisticsProductStoreDailyDomainRules.GetLatestSourceTime(rows);

    internal static DateTime? GetHBSalesSourceWatermark(
        IEnumerable<ProductStoreDailySourceRow> hbSalesRows
    ) => SalesStatisticsProductStoreDailyDomainRules.GetHBSalesSourceWatermark(hbSalesRows);

    internal static async Task<List<HBSalesStoreAggregateRow>> LoadHBSalesStoreAggregatesAsync(
        HBSalesRecordSqlSugarContext hbSalesContext,
        DateTime targetDate,
        DateTime nextDate
    ) => await SalesStatisticsProductStoreDailySourceQueries.LoadHBSalesStoreAggregatesAsync(
        hbSalesContext,
        targetDate,
        nextDate
    );

    internal static List<HBSalesStoreAggregateRow> BuildHBSalesStoreAggregates(
        IReadOnlyList<ProductStoreDailySourceRow> hbSalesRows
    ) => SalesStatisticsProductStoreDailyDomainRules.BuildHBSalesStoreAggregates(hbSalesRows);

    }
}
