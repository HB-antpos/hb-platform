using System.Text.Json;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services.Background;
using BlazorApp.Api.Services.ProductCosts;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services;

/// <summary>仅主库成本缺口维护。预览、执行与回滚都按日期隔离，不重建销售事实。</summary>
public sealed class SalesCostBackfillService(
    SqlSugarContext context, POSMSqlSugarContext posm, HBSalesRecordSqlSugarContext history,
    ScheduledTaskLeaseService leases, ILogger<SalesCostBackfillService> logger)
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(30);
    private readonly List<(string Type, string Scope, string Token)> heldLeases = [];
    private static readonly string[] SnapshotTypes = [SalesStatisticType.ProductStoreDaily,
        SalesStatisticType.AustralianSupplierStoreSales, SalesStatisticType.ChinaSupplierStoreSales];

    public bool SchemaReady() => context.Db.DbMaintenance.IsAnyTable("SalesCostBackfillBatch", false)
        && context.Db.DbMaintenance.IsAnyTable("SalesCostBackfillDay", false)
        && context.Db.DbMaintenance.IsAnyTable("SalesCostBackfillItem", false);

    public async Task<Guid> PreviewAsync(DateTime start, DateTime end, string actor, bool automatic = false)
    {
        if (!SchemaReady()) throw new InvalidOperationException("成本回填审计表尚未完成受控迁移");
        if (start.Date < SalesStatisticsHBSalesHistoryWindow.StartDate
            || end.Date < start.Date
            || end.Date > SalesStatisticsBusinessDate.Today())
            throw new ArgumentException($"日期必须在 {SalesStatisticsHBSalesHistoryWindow.StartDate:yyyy-MM-dd} 至当前业务日之间");
        if (start.Date < SalesStatisticsHBSalesHistoryWindow.EndExclusive && !LegacyDatabaseGate.HBSalesEnabled)
            throw new ArgumentException($"HBSales 已断开，只能回填 {SalesStatisticsHBSalesHistoryWindow.EndExclusive:yyyy-MM-dd} 及之后的日期");
        var now = DateTime.UtcNow;
        var batch = new SalesCostBackfillBatch { Id = Guid.NewGuid(), StartDate = start.Date,
            EndDate = end.Date, RequestedBy = actor, Automatic = automatic,
            CreatedAtUtc = now, UpdatedAtUtc = now, RuleVersion = SalesCostBackfillRules.Version };
        await context.Db.Ado.BeginTranAsync();
        try
        {
            await context.Db.Insertable(batch).ExecuteCommandAsync();
            var days = new List<SalesCostBackfillDay>();
            for (var date = start.Date; date <= end.Date; date = date.AddDays(1))
                days.Add(new() { BatchId = batch.Id, Date = date, UpdatedAtUtc = now });
            foreach (var chunk in days.Chunk(100))
                await context.Db.Insertable(chunk.ToList()).ExecuteCommandAsync();
            await context.Db.Ado.CommitTranAsync();
            return batch.Id;
        }
        catch { await context.Db.Ado.RollbackTranAsync(); throw; }
    }

    public async Task<object?> GetAsync(Guid id, int page = 1, int pageSize = 100)
    {
        var batch = await context.Db.Queryable<SalesCostBackfillBatch>().InSingleAsync(id);
        if (batch == null) return null;
        // 全范围含数百日期，发布前后影像保留在审计库；进度响应不重复传输整年供应商明细。
        var days = await context.Db.Queryable<SalesCostBackfillDay>().Where(x => x.BatchId == id)
            .OrderBy(x => x.Date).Select(x => new { x.BatchId, x.Date, x.Status,
                x.SourceHash, x.SnapshotVersion, x.FailedOperation, x.CandidateCount,
                x.UnresolvedCount, x.AppliedCount, x.UpdatedAtUtc, x.Error }).ToListAsync();
        var query = context.Db.Queryable<SalesCostBackfillItem>().Where(x => x.BatchId == id);
        var total = await query.CountAsync();
        var items = await query.OrderBy(x => x.Date).OrderBy(x => x.Id)
            .ToPageListAsync(Math.Max(1, page), Math.Clamp(pageSize, 1, 100));
        return new { batch, days, candidateCount = days.Sum(x => x.CandidateCount),
            unresolvedCount = days.Sum(x => x.UnresolvedCount), appliedCount = days.Sum(x => x.AppliedCount),
            total, pageIndex = Math.Max(1, page), pageSize = Math.Clamp(pageSize, 1, 100), items };
    }

    public async Task<bool> RequestAsync(Guid id, bool rollback, string actor)
    {
        // 冻结批次只能由其当前状态推进；重复请求不生成第二份执行任务。
        var allowed = rollback ? new[] { "Applied", "AppliedWithExceptions", "RollbackWithConflicts" }
            : new[] { "Previewed", "AppliedWithExceptions" };
        var state = rollback ? "RollingBack" : "Applying";
        await context.Db.Ado.BeginTranAsync(System.Data.IsolationLevel.Serializable);
        try
        {
            var batch = await context.Db.Queryable<SalesCostBackfillBatch>().Where(x => x.Id == id)
                .With(SqlWith.UpdLock).SingleAsync();
            if (batch == null || batch.RuleVersion != SalesCostBackfillRules.Version) return false;
            if (batch.Status == state || batch.Status == (rollback ? "RolledBack" : "Applied")) return true;
            if (!allowed.Contains(batch.Status)) return false;
            batch.Status = state; batch.Error = null; batch.UpdatedAtUtc = DateTime.UtcNow;
            if (rollback) batch.RolledBackBy = actor; else batch.AppliedBy = actor;
            await context.Db.Updateable(batch).ExecuteCommandAsync();
            // 只重试事务失败的日期；证据冲突保持冻结，必须重新预览。
            await context.Db.Updateable<SalesCostBackfillDay>()
                .SetColumns(x => new SalesCostBackfillDay { Status = rollback ? "Applied" : "Previewed", Error = null, FailedOperation = null })
                .Where(x => x.BatchId == id && x.Status == "Failed" && x.FailedOperation == state)
                .ExecuteCommandAsync();
            await context.Db.Ado.CommitTranAsync();
            return true;
        }
        finally { if (context.Db.Ado.Transaction != null) await context.Db.Ado.RollbackTranAsync(); }
    }

    public async Task<bool> RetryPreviewAsync(Guid id, string actor)
    {
        await context.Db.Ado.BeginTranAsync(System.Data.IsolationLevel.Serializable);
        try
        {
            var batch = await context.Db.Queryable<SalesCostBackfillBatch>().Where(x => x.Id == id)
                .With(SqlWith.UpdLock).SingleAsync();
            if (batch == null || batch.RuleVersion != SalesCostBackfillRules.Version) return false;
            if (batch.Status == "Previewing") return true;
            if (batch.Status is not ("Previewed" or "AppliedWithExceptions")) return false;
            var days = await context.Db.Queryable<SalesCostBackfillDay>()
                .Where(x => x.BatchId == id && (x.Status == "Deferred"
                    || (x.Status == "Failed" && x.FailedOperation == "Previewing")))
                .With(SqlWith.UpdLock).ToListAsync();
            if (days.Count == 0) return false;
            foreach (var day in days)
            {
                // 已冻结或已写入过的日期禁止重新预览；只恢复尚未产生任何审计明细的日期。
                if (await context.Db.Queryable<SalesCostBackfillItem>()
                    .AnyAsync(x => x.BatchId == id && x.Date == day.Date)) return false;
                day.Status = "Pending"; day.FailedOperation = null; day.Error = null;
                day.SourceHash = day.SnapshotVersion = day.BeforePublicationJson = day.AfterPublicationJson = null;
                day.CandidateCount = day.UnresolvedCount = day.AppliedCount = 0; day.UpdatedAtUtc = DateTime.UtcNow;
                await context.Db.Updateable(day).ExecuteCommandAsync();
            }
            batch.Status = "Previewing"; batch.PreviewRetriedBy = actor; batch.UpdatedAtUtc = DateTime.UtcNow;
            await context.Db.Updateable(batch).ExecuteCommandAsync();
            await context.Db.Ado.CommitTranAsync(); return true;
        }
        finally { if (context.Db.Ado.Transaction != null) await context.Db.Ado.RollbackTranAsync(); }
    }

    internal async Task<bool> RunOneAsync(CancellationToken token)
    {
        if (!SchemaReady()) return false;
        var batch = await context.Db.Queryable<SalesCostBackfillBatch>()
            .Where(x => x.Status == "Previewing" || x.Status == "Applying" || x.Status == "RollingBack")
            .OrderBy(x => x.CreatedAtUtc).FirstAsync();
        if (batch == null) return false;
        if (batch.RuleVersion != SalesCostBackfillRules.Version)
        {
            batch.Status = "Failed"; batch.Error = "回填批次规则版本不一致，必须重新预览";
            await context.Db.Updateable(batch).ExecuteCommandAsync();
            return true;
        }
        var nextStatus = batch.Status == "Previewing" ? "Pending" : batch.Status == "Applying" ? "Previewed" : "Applied";
        var day = await context.Db.Queryable<SalesCostBackfillDay>()
            .Where(x => x.BatchId == batch.Id && x.Status == nextStatus).OrderBy(x => x.Date).FirstAsync();
        if (day == null)
        {
            var days = await context.Db.Queryable<SalesCostBackfillDay>().Where(x => x.BatchId == batch.Id).ToListAsync();
            var exceptions = batch.Status == "RollingBack"
                ? days.Any(x => x.Status == "RollbackConflict"
                    || (x.FailedOperation == "RollingBack" && (x.Status == "Conflict" || x.Status == "Failed")))
                : days.Any(x => x.Status is "Conflict" or "Failed" or "Deferred" || x.UnresolvedCount > 0);
            batch.Status = batch.Status switch
            {
                "Previewing" => "Previewed",
                "Applying" => exceptions ? "AppliedWithExceptions" : "Applied",
                _ => exceptions ? "RollbackWithConflicts" : "RolledBack",
            };
            batch.UpdatedAtUtc = DateTime.UtcNow;
            await context.Db.Updateable(batch).ExecuteCommandAsync();
            return true;
        }
        return await WithDateLeaseAsync(day.Date, async () =>
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (batch.Status == "Previewing") await PreviewDayAsync(day, token);
                else await ChangeDayAsync(day, batch.Status == "RollingBack", token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // 对外只给出分类；原始数据库错误可能含连接信息，保留在内部日志。
                logger.LogError(ex, "成本回填日期失败: {Batch} {Date}", day.BatchId, day.Date);
                var failureStatus = ex is CostBackfillConflictException
                    ? (batch.Status == "RollingBack" ? "RollbackConflict" : "Conflict") : "Failed";
                var error = ex is CostBackfillConflictException ? ex.Message : "日期处理失败，请查看受保护的服务日志";
                try
                {
                    await context.Db.Ado.BeginTranAsync();
                    await SalesStatisticsCostWriteLock.AcquireAsync(context.Db, day.Date);
                    await EnsureLeasesAsync();
                    // 主事务已回滚，只补记失败字段；绝不把内存中未提交的冻结/after值写回。
                    // 同时核验租约与阶段状态，旧执行器不能覆盖接管者已经推进的日期。
                    await context.Db.Updateable<SalesCostBackfillDay>()
                        .SetColumns(x => new SalesCostBackfillDay { Status = failureStatus,
                            FailedOperation = batch.Status, Error = error, UpdatedAtUtc = DateTime.UtcNow })
                        .Where(x => x.BatchId == day.BatchId && x.Date == day.Date && x.Status == nextStatus)
                        .ExecuteCommandAsync();
                    await context.Db.Ado.CommitTranAsync();
                }
                catch (Exception failureError)
                {
                    if (context.Db.Ado.Transaction != null) await context.Db.Ado.RollbackTranAsync();
                    logger.LogWarning(failureError, "成本回填失败补记未获得当前执行权: {Batch} {Date}", day.BatchId, day.Date);
                }
            }
        });
    }

    private async Task PreviewDayAsync(SalesCostBackfillDay day, CancellationToken token)
    {
        var rows = await ReadRowsAsync(day.Date);
        var states = await ReadStatesAsync(day.Date);
        if (!Published(states, rows))
        {
            day.Status = "Deferred"; day.Error = "销售事实或供应商快照尚未完整发布";
            await context.Db.Updateable(day).ExecuteCommandAsync();
            return;
        }
        var gaps = rows.Where(SalesCostBackfillRules.NeedsRepair).ToList();
        var input = gaps.Count > 0 ? await LoadAsync(day.Date, gaps.Select(x => x.ProductCode).Distinct().ToArray()) : null;
        token.ThrowIfCancellationRequested();
        var rebuilt = input == null ? new List<ProductStoreDailySalesStatistic>()
            : new SalesStatisticsProductStoreDailyBuilder().Build(input).Statistics;
        day.SnapshotVersion = SupplierStatisticVersion.ComputeProductVersion(rows);
        day.SourceHash = input == null ? "" : SourceHash(input);
        // 证据按商品预分组，避免每个缺口反复扫描数十万条门店价格。
        var storeEvidence = input?.StoreCosts.ToLookup(x => x.ProductCode, StringComparer.OrdinalIgnoreCase);
        var productEvidence = input?.ProductCosts.ToLookup(x => x.ProductCode, StringComparer.OrdinalIgnoreCase);
        var warehouseEvidence = input?.WarehouseCosts.ToLookup(x => x.ProductCode, StringComparer.OrdinalIgnoreCase);
        var rawEvidence = input?.RawRows.ToLookup(x => x.ProductCode, StringComparer.OrdinalIgnoreCase);
        var findRebuilt = SalesCostBackfillRules.BuildRebuiltLookup(rebuilt, input?.ChinaSupplierCodes);
        var items = gaps.Select(row =>
        {
            var calculated = findRebuilt(row);
            var proposal = SalesCostBackfillRules.Propose(row, calculated);
            return new SalesCostBackfillItem { Id = Guid.NewGuid(), BatchId = day.BatchId, Date = row.Date,
                BranchCode = row.BranchCode, SupplierCode = row.SupplierCode, ProductCode = row.ProductCode,
                BeforeJson = SalesCostBackfillRules.Json(row), ProposedJson = proposal.Cost == null ? null : JsonSerializer.Serialize(proposal.Cost),
                Status = proposal.Cost == null ? "Unresolved" : "Candidate", Reason = proposal.Reason,
                EvidenceJson = JsonSerializer.Serialize(new { rule = SalesCostBackfillRules.Version,
                    checkedAtUtc = DateTime.UtcNow, sourceHash = day.SourceHash,
                    source = proposal.Cost?.CostSource,
                    currentCostFallback = !row.TotalCost.HasValue && row.UnitCostSnapshot is not > 0,
                    storeCosts = storeEvidence?[row.ProductCode], productCosts = productEvidence?[row.ProductCode],
                    warehouseCosts = warehouseEvidence?[row.ProductCode],
                    lines = rawEvidence?[row.ProductCode].Where(x =>
                        SalesStatisticsCodeRules.ResolveBranchCode(x.BranchCode, x.DeviceCode, input!.DeviceBranchMap) == row.BranchCode)
                        .Select(x => new { x.IsHBSalesSource, x.OrderGuid, x.DetailGuid, x.SupplierCode,
                            x.ProductCode, x.Barcode, x.PriceLookupCode, x.DocumentType, x.PricingUnit,
                            x.OriginalUnitPrice, x.OriginalSubtotal, x.OriginalSaleQuantity, x.OriginalSaleCostEvidence, x.Quantity,
                            originalUnitPriceVerified = SalesStatisticsProductStoreDailyDomainRules.ResolveOriginalUnitPrice(x) }) }),
                UpdatedAtUtc = DateTime.UtcNow };
        }).ToList();
        await context.Db.Ado.BeginTranAsync(System.Data.IsolationLevel.Serializable);
        try
        {
            await SalesStatisticsCostWriteLock.AcquireAsync(context.Db, day.Date);
            var productCodes = gaps.Select(x => x.ProductCode).Distinct().ToArray();
            if (productCodes.Length > 0)
                await ProductCostMutationLock.AcquireProductsAsync(context.Db, productCodes);
            var current = await ReadRowsAsync(day.Date, true);
            if (SupplierStatisticVersion.ComputeProductVersion(current) != day.SnapshotVersion
                || !Published(await ReadStatesAsync(day.Date), current))
                throw new CostBackfillConflictException("预览期间统计版本发生变化");
            day.BeforePublicationJson = await ReadPublicationAsync(day.Date);
            foreach (var chunk in items.Chunk(100))
                await context.Db.Insertable(chunk.ToList()).ExecuteCommandAsync();
            day.CandidateCount = items.Count(x => x.Status == "Candidate");
            day.UnresolvedCount = items.Count - day.CandidateCount;
            day.Status = "Previewed"; day.UpdatedAtUtc = DateTime.UtcNow;
            await context.Db.Updateable(day).ExecuteCommandAsync();
            if (input != null) await VerifySourcesAsync(day.Date, productCodes, day.SourceHash!);
            await EnsureLeasesAsync();
            await context.Db.Ado.CommitTranAsync();
        }
        catch { await context.Db.Ado.RollbackTranAsync(); throw; }
    }

    private async Task ChangeDayAsync(SalesCostBackfillDay day, bool rollback, CancellationToken token)
    {
        var items = await context.Db.Queryable<SalesCostBackfillItem>()
            .Where(x => x.BatchId == day.BatchId && x.Date == day.Date
                && x.Status == (rollback ? "Applied" : "Candidate")).ToListAsync();
        await context.Db.Ado.BeginTranAsync(System.Data.IsolationLevel.Serializable);
        try
        {
            await SalesStatisticsCostWriteLock.AcquireAsync(context.Db, day.Date);
            if (items.Count > 0)
                await ProductCostMutationLock.AcquireProductsAsync(context.Db, items.Select(x => x.ProductCode));
            var rows = await ReadRowsAsync(day.Date, true);
            var states = await ReadStatesAsync(day.Date);
            if (!Published(states, rows)) throw new CostBackfillConflictException("统计版本未完整发布或已变化");
            var publication = await ReadPublicationAsync(day.Date);
            if (publication != (rollback ? day.AfterPublicationJson : day.BeforePublicationJson))
                throw new CostBackfillConflictException("供应商成本或发布版本已有后续修改，未覆盖");
            if (!rollback && SupplierStatisticVersion.ComputeProductVersion(rows) != day.SnapshotVersion)
                throw new CostBackfillConflictException("统计已变化，需要重新预览此日期");
            if (!rollback && items.Count > 0)
            {
                var input = await LoadAsync(day.Date, rows.Where(SalesCostBackfillRules.NeedsRepair)
                    .Select(x => x.ProductCode).Distinct().ToArray());
                if (SourceHash(input) != day.SourceHash)
                    throw new CostBackfillConflictException("成本来源或原始明细已变化，需要重新预览此日期");
            }
            var byKey = rows.ToDictionary(SalesCostBackfillRules.Key);
            var changed = 0; var conflicts = 0;
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                var key = $"{item.Date:yyyyMMdd}|{item.BranchCode}|{item.SupplierCode}|{item.ProductCode}";
                if (!byKey.TryGetValue(key, out var row)
                    || !SalesCostBackfillRules.MatchesAfter(row, rollback ? item.AfterJson! : item.BeforeJson))
                {
                    if (!rollback) throw new CostBackfillConflictException("目标记录与预览不一致");
                    item.Status = "RollbackConflict"; item.ConflictReason = "回填后已有其他修改，未覆盖"; conflicts++;
                }
                else
                {
                    var cost = rollback ? JsonSerializer.Deserialize<SalesCostBackfillRules.RowImage>(item.BeforeJson)!.Cost
                        : JsonSerializer.Deserialize<SalesCostBackfillRules.CostImage>(item.ProposedJson!)!;
                    SalesCostBackfillRules.SetCost(row, cost);
                    row.UpdateTime = DateTime.Now;
                    await UpdateCostAsync(row);
                    var persisted = await context.Db.Queryable<ProductStoreDailySalesStatistic>()
                        .Where(x => x.Date == row.Date && x.BranchCode == row.BranchCode
                            && x.SupplierCode == row.SupplierCode && x.ProductCode == row.ProductCode).SingleAsync();
                    if (!rollback) item.AfterJson = SalesCostBackfillRules.Json(persisted);
                    item.Status = rollback ? "RolledBack" : "Applied"; changed++;
                }
                item.UpdatedAtUtc = DateTime.UtcNow;
                await context.Db.Updateable(item).ExecuteCommandAsync();
            }
            if (changed > 0) await PublishCostsAsync(day.Date, states, token);
            if (!rollback) day.AfterPublicationJson = await ReadPublicationAsync(day.Date);
            day.Status = rollback ? (conflicts > 0 ? "RollbackConflict" : "RolledBack") : "Applied";
            if (!rollback) day.AppliedCount = changed;
            day.UpdatedAtUtc = DateTime.UtcNow;
            await context.Db.Updateable(day).ExecuteCommandAsync();
            if (!rollback && items.Count > 0)
            {
                // 成本更新只改统计表，因此可以在提交前再次完整核验外部原始明细。
                // 完整内容哈希同时发现删除、价格更正和不推进 MAX 水位的旧行修改。
                var productCodes = await context.Db.Queryable<SalesCostBackfillItem>()
                    .Where(x => x.BatchId == day.BatchId && x.Date == day.Date)
                    .Select(x => x.ProductCode).ToListAsync();
                await VerifySourcesAsync(day.Date, productCodes.Distinct().ToArray(), day.SourceHash!);
            }
            await EnsureLeasesAsync();
            await context.Db.Ado.CommitTranAsync();
        }
        catch { await context.Db.Ado.RollbackTranAsync(); throw; }
    }

    private async Task UpdateCostAsync(ProductStoreDailySalesStatistic row)
    {
        var affected = await context.Db.Updateable(row).UpdateColumns(x => new {
            x.UnitCostSnapshot, x.TotalCost, x.GrossProfit, x.GrossMarginRate, x.CostSource, x.UpdateTime })
            .ExecuteCommandAsync();
        if (affected != 1) throw new CostBackfillConflictException("成本写入目标行数不一致");
    }

    private async Task PublishCostsAsync(DateTime date, List<SalesStatisticRefreshState> states, CancellationToken token)
    {
        var rows = await ReadRowsAsync(date);
        var version = SupplierStatisticVersion.ComputeProductVersion(rows);
        var build = await new SalesStatisticsSupplierStoreSummaryService()
            .BuildFromProductStatisticsAsync(context, posm, rows, DateTime.Now);
        var australian = await context.Db.Queryable<AustralianSupplierStoreSalesDetail>()
            .Where(x => x.Date >= date && x.Date < date.AddDays(1)).ToListAsync();
        var china = await context.Db.Queryable<ChinaSupplierStoreSalesDetail>()
            .Where(x => x.Date >= date && x.Date < date.AddDays(1)).ToListAsync();
        // 派生表只更新成本列；映射改变或销售对账失败不能趁回填重写销售事实。
        if (!SupplierFactsEqual(australian.Select(x => (x.BranchCode, x.SupplierCode, x.TotalAmount, x.TotalQuantity, x.OrderCount)),
                build.Australian.Select(x => (x.BranchCode, x.SupplierCode, x.TotalAmount, x.TotalQuantity, x.OrderCount)))
            || !SupplierFactsEqual(china.Select(x => (x.BranchCode, x.SupplierCode, x.TotalAmount, x.TotalQuantity, x.OrderCount)),
                build.China.Select(x => (x.BranchCode, x.SupplierCode, x.TotalAmount, x.TotalQuantity, x.OrderCount))))
            throw new CostBackfillConflictException("供应商销售事实对账不一致，整批回滚");
        foreach (var row in build.Australian)
        {
            token.ThrowIfCancellationRequested();
            if (await context.Db.Updateable(row).UpdateColumns(x => new { x.TotalCost, x.GrossProfit,
                    x.StatisticRowCount, x.CostedRowCount, x.GrossProfitRowCount, x.UpdateTime }).ExecuteCommandAsync() != 1)
                throw new CostBackfillConflictException("澳洲供应商更新行数不一致");
        }
        foreach (var row in build.China)
            if (await context.Db.Updateable(row).UpdateColumns(x => new { x.TotalCost, x.GrossProfit,
                    x.StatisticRowCount, x.CostedRowCount, x.GrossProfitRowCount, x.UpdateTime }).ExecuteCommandAsync() != 1)
                throw new CostBackfillConflictException("中国供应商更新行数不一致");
        foreach (var state in states)
        {
            state.SourceProductVersion = version;
            state.LastAggregatedAtUtc = state.CompletedAtUtc = DateTime.UtcNow;
            await context.Db.Updateable(state).UpdateColumns(x => new {
                x.SourceProductVersion, x.LastAggregatedAtUtc, x.CompletedAtUtc }).ExecuteCommandAsync();
        }
    }

    internal static bool SupplierFactsEqual(IEnumerable<(string Branch, string Supplier, decimal Amount, int Quantity, int? Orders)> a,
        IEnumerable<(string Branch, string Supplier, decimal Amount, int Quantity, int? Orders)> b) =>
        a.OrderBy(x => x.Branch, StringComparer.Ordinal).ThenBy(x => x.Supplier, StringComparer.Ordinal)
            .SequenceEqual(b.OrderBy(x => x.Branch, StringComparer.Ordinal).ThenBy(x => x.Supplier, StringComparer.Ordinal));

    private async Task<string> ReadPublicationAsync(DateTime date)
    {
        // 保存派生表的事实、成本与持久化版本；不把名称、检查时间等无关字段当作冲突。
        var australian = await context.Db.Queryable<AustralianSupplierStoreSalesDetail>()
            .Where(x => x.Date >= date && x.Date < date.AddDays(1))
            .OrderBy(x => x.BranchCode).OrderBy(x => x.SupplierCode)
            .Select(x => new { x.BranchCode, x.SupplierCode, x.TotalAmount, x.TotalQuantity, x.OrderCount,
                x.TotalCost, x.GrossProfit, x.StatisticRowCount, x.CostedRowCount, x.GrossProfitRowCount }).ToListAsync();
        var china = await context.Db.Queryable<ChinaSupplierStoreSalesDetail>()
            .Where(x => x.Date >= date && x.Date < date.AddDays(1))
            .OrderBy(x => x.BranchCode).OrderBy(x => x.SupplierCode)
            .Select(x => new { x.BranchCode, x.SupplierCode, x.TotalAmount, x.TotalQuantity, x.OrderCount,
                x.TotalCost, x.GrossProfit, x.StatisticRowCount, x.CostedRowCount, x.GrossProfitRowCount }).ToListAsync();
        var versions = (await ReadStatesAsync(date)).OrderBy(x => x.StatisticType, StringComparer.Ordinal)
            .Select(x => new { x.StatisticType, x.SourceProductVersion });
        return JsonSerializer.Serialize(new { australian, china, versions });
    }

    private Task<ProductStoreDailyRefreshInput> LoadAsync(DateTime date, IReadOnlyCollection<string> productCodes) =>
        new SalesStatisticsProductStoreDailySourceReader().LoadAsync(context, posm,
            SalesStatisticsHBSalesHistoryWindow.Includes(date) ? history : null,
            logger, date, null, null, productCodes);

    private async Task VerifySourcesAsync(DateTime date, IReadOnlyCollection<string> productCodes, string expectedHash)
    {
        if (SourceHash(await LoadAsync(date, productCodes)) != expectedHash)
            throw new CostBackfillConflictException("原始销售明细或成本来源在处理期间发生变化，整批未提交");
    }

    private Task<List<ProductStoreDailySalesStatistic>> ReadRowsAsync(DateTime date, bool forUpdate = false)
    {
        var query = context.Db.Queryable<ProductStoreDailySalesStatistic>()
            .Where(x => x.Date >= date.Date && x.Date < date.Date.AddDays(1));
        if (forUpdate) query = query.With(SqlWith.UpdLock);
        return query.ToListAsync();
    }
    private Task<List<SalesStatisticRefreshState>> ReadStatesAsync(DateTime date)
    {
        // SqlSugar 表达式解析器不能读取私有静态字段，先捕获为查询局部参数。
        var types = SnapshotTypes;
        return context.Db.Queryable<SalesStatisticRefreshState>().Where(x => types.Contains(x.StatisticType)
            && x.Date >= date.Date && x.Date < date.Date.AddDays(1)).ToListAsync();
    }
    private static bool Published(List<SalesStatisticRefreshState> states, List<ProductStoreDailySalesStatistic> rows)
    {
        var version = SupplierStatisticVersion.ComputeProductVersion(rows);
        return states.Count == 3 && states.All(x =>
            (x.Status == SalesStatisticRefreshStatus.Fresh || x.Status == SalesStatisticRefreshStatus.ProvisionalFresh)
            && x.CompletedAtUtc.HasValue && x.LastAggregatedAtUtc.HasValue && x.SourceProductVersion == version);
    }
    internal static string SourceHash(ProductStoreDailyRefreshInput input) =>
        SalesCostBackfillRules.Hash(Canonical(JsonSerializer.SerializeToElement(input)));
    private static string Canonical(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => JsonSerializer.Serialize(x.Name) + ":" + Canonical(x.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Canonical).OrderBy(x => x, StringComparer.Ordinal)) + "]",
        _ => element.GetRawText(),
    };

    private async Task<bool> WithDateLeaseAsync(DateTime date, Func<Task> work)
    {
        var acquired = new List<(string Type, string Scope, string Token)>();
        try
        {
            var requests = new List<(string Type, string Scope)>();
            if (date.Year == 2025) requests.Add((ProductStoreDailyStatisticQueueService.ProductStoreDaily2025SerialLeaseTaskType,
                ProductStoreDailyStatisticQueueService.ProductStoreDaily2025SerialLeaseScope));
            requests.Add((SalesStatisticsAlignmentService.DailyFullRefreshLeaseTaskType, date.ToString("yyyy-MM-dd")));
            foreach (var request in requests)
            {
                var lease = await leases.TryAcquireAsync(request.Type, request.Scope, LeaseDuration);
                if (!lease.Acquired || string.IsNullOrWhiteSpace(lease.Lease?.LeaseToken)) return false;
                acquired.Add((request.Type, request.Scope, lease.Lease.LeaseToken));
            }
            heldLeases.AddRange(acquired);
            await work();
            return true;
        }
        finally
        {
            heldLeases.Clear();
            foreach (var lease in acquired.AsEnumerable().Reverse())
                await leases.CompleteAsync(lease.Type, lease.Scope, lease.Token, true);
        }
    }

    private async Task EnsureLeasesAsync()
    {
        // 租约过期后即使旧进程仍活着，也不能提交；防止两个实例交错发布。
        foreach (var lease in heldLeases)
            if (!await leases.RenewAsync(lease.Type, lease.Scope, lease.Token, LeaseDuration))
                throw new CostBackfillConflictException("执行租约已失效，拒绝提交成本变更");
    }
}

internal sealed class CostBackfillConflictException(string message) : Exception(message);
