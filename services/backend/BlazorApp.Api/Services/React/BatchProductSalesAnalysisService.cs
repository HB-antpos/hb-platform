using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.Background;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Services.React;

/// <summary>
/// 前台只读取商品分店日统计和已发布折扣快照；成交明细由独立后台任务读取。
/// </summary>
public sealed class BatchProductSalesAnalysisService : IBatchProductSalesAnalysisService
{
    internal const int MaxItemNumbers = 3000;
    // 折扣快照按商品读取；超过 500 个商品时只提供净额查询和导出。
    private const int MaxDiscountOverviewProductCodes = 500;
    // SQL Server 的参数上限为 2100；给日期、门店等过滤条件留出空间。
    private const int ProductCodeQueryBatchSize = 500;
    internal const int MaxDays = 366;
    private readonly ISqlSugarClient _db;
    private readonly BatchProductSalesStatisticReader _statisticReader;
    private readonly BatchProductSalesDiscountSnapshotReader _discountSnapshotReader;
    private readonly IProductStoreDailyStatisticQueueService _productStoreDailyQueue;
    private readonly ILogger<BatchProductSalesAnalysisService> _logger;

    public BatchProductSalesAnalysisService(
        SqlSugarContext context,
        IProductStoreDailyStatisticQueueService productStoreDailyQueue,
        ILogger<BatchProductSalesAnalysisService> logger)
        : this(context.Db, productStoreDailyQueue, logger) { }

    internal BatchProductSalesAnalysisService(
        ISqlSugarClient db,
        IProductStoreDailyStatisticQueueService productStoreDailyQueue,
        ILogger<BatchProductSalesAnalysisService> logger)
    {
        _db = db;
        _statisticReader = new BatchProductSalesStatisticReader(db);
        _discountSnapshotReader = new BatchProductSalesDiscountSnapshotReader(db);
        _productStoreDailyQueue = productStoreDailyQueue;
        _logger = logger;
    }

    public async Task<ApiResponse<BatchProductSalesOptionsDto>> GetOptionsAsync(
        IReadOnlyList<string>? scopedStoreCodes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scope = NormalizeScope(scopedStoreCodes);
        if (scope is { Count: 0 })
            throw new BatchProductSalesAnalysisForbiddenException();

        var query = _db.Queryable<Store>().Where(s => s.IsDeleted == false);
        if (scope != null)
            query = query.Where(s => scope.Contains(s.StoreCode));
        var stores = await query.Select(s => new BatchProductSalesStoreDto
        {
            Code = s.StoreCode,
            Name = s.StoreName,
        }).ToListAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return ApiResponse<BatchProductSalesOptionsDto>.OK(new BatchProductSalesOptionsDto
        {
            Stores = stores.Where(s => !string.IsNullOrWhiteSpace(s.Code))
                .OrderBy(s => s.Code, StringComparer.OrdinalIgnoreCase).ToList(),
        });
    }

    public async Task<ApiResponse<BatchProductSalesQueryResultDto>> QueryAsync(
        BatchProductSalesQueryRequestDto request,
        IReadOnlyList<string>? scopedStoreCodes,
        CancellationToken cancellationToken = default)
    {
        var range = ValidateRange(request);
        var itemNumbers = NormalizeItemNumbers(request.ItemNumbers);
        var storeScope = await ResolveEffectiveStoreScopeAsync(request.StoreCodes, scopedStoreCodes, cancellationToken);
        var storeCodes = storeScope.Codes;
        cancellationToken.ThrowIfCancellationRequested();

        var matches = await ResolveItemMatchesAsync(itemNumbers, cancellationToken);
        var productCodes = matches.Where(m => m.Status == "matched")
            .SelectMany(m => m.ProductCodes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var products = await LoadProductsAsync(productCodes, cancellationToken);
        var coverageBefore = await _statisticReader.CoverageAsync(range.StartDate, range.EndDate, cancellationToken);
        var queueResult = coverageBefore.PendingReasons.Count > 0 && productCodes.Count > 0
            ? await QueuePendingStatisticDatesAsync(coverageBefore.PendingReasons.Keys.ToList(), cancellationToken)
            : BatchProductSalesStatisticQueueResult.NotNeeded;
        // 摘要只需要净销量，直接从稳定的商品-分店-日事实表在 SQL 端聚合；
        // 不能为 3000 个货号把全年逐笔成交明细加载到 API 进程。
        // 分别按商品和日期聚合，避免物化日期×商品矩阵；未完成日期不会进入查询范围。
        var quantitySummary = await ReadQuantitySummaryAsync(productCodes, coverageBefore.ReadyDates, storeCodes, cancellationToken);
        // 分店总览只聚合日×分店，不展开商品×分店×日期矩阵。
        var branchRows = new List<BatchProductSalesAggregateRow>();
        foreach (var productCodeBatch in productCodes.Chunk(ProductCodeQueryBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var branchRowsQuery = _db.Queryable<ProductStoreDailySalesStatistic>().With(SqlWith.Null)
                .Where(BatchProductSalesStatisticReader.BuildDatePredicate(coverageBefore.ReadyDates).ToExpression())
                .Where(s => productCodeBatch.Contains(s.ProductCode));
            if (storeCodes != null) branchRowsQuery = branchRowsQuery.Where(s => storeCodes.Contains(s.BranchCode));
            branchRows.AddRange(await branchRowsQuery.GroupBy(s => new { s.BranchCode, s.ProductCode }).Select(s => new BatchProductSalesAggregateRow
            {
                BranchCode = s.BranchCode, ProductCode = s.ProductCode,
                Quantity = SqlFunc.AggregateSum(s.TotalQuantity), UnknownQuantity = SqlFunc.AggregateSum(s.TotalQuantity), UnknownRowCount = 1,
                SalesAmount = SqlFunc.AggregateSum(s.TotalAmount),
            }).ToListAsync(cancellationToken));
        }
        var coverageAfter = await _statisticReader.CoverageAsync(range.StartDate, range.EndDate, cancellationToken);
        var stableReadyDates = coverageBefore.ReadyDates.Where(date =>
            coverageAfter.DateVersions.TryGetValue(date, out var afterVersion)
            && coverageBefore.DateVersions.TryGetValue(date, out var beforeVersion)
            && string.Equals(beforeVersion, afterVersion, StringComparison.Ordinal)).ToList();
        // BranchProduct 没有日期键，C1 发现变化日时必须用稳定日期重读，不能把变化日的分店排名混进摘要。
        // C2 只核验锁定的稳定集合；原 pending 后来 Fresh 不影响本次摘要。
        if (stableReadyDates.Count != coverageBefore.ReadyDates.Count)
        {
            quantitySummary = await ReadQuantitySummaryAsync(productCodes, stableReadyDates, storeCodes, cancellationToken);
            branchRows = new List<BatchProductSalesAggregateRow>();
            foreach (var productCodeBatch in productCodes.Chunk(ProductCodeQueryBatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stableBranchQuery = _db.Queryable<ProductStoreDailySalesStatistic>().With(SqlWith.Null)
                    .Where(BatchProductSalesStatisticReader.BuildDatePredicate(stableReadyDates).ToExpression())
                    .Where(s => productCodeBatch.Contains(s.ProductCode));
                if (storeCodes != null) stableBranchQuery = stableBranchQuery.Where(s => storeCodes.Contains(s.BranchCode));
                branchRows.AddRange(await stableBranchQuery.GroupBy(s => new { s.BranchCode, s.ProductCode }).Select(s => new BatchProductSalesAggregateRow
                {
                    BranchCode = s.BranchCode, ProductCode = s.ProductCode,
                    Quantity = SqlFunc.AggregateSum(s.TotalQuantity), UnknownQuantity = SqlFunc.AggregateSum(s.TotalQuantity), UnknownRowCount = 1,
                    SalesAmount = SqlFunc.AggregateSum(s.TotalAmount),
                }).ToListAsync(cancellationToken));
            }
            var coverageFinal = await _statisticReader.CoverageAsync(range.StartDate, range.EndDate, cancellationToken);
            if (stableReadyDates.Any(date => !coverageFinal.DateVersions.TryGetValue(date, out var version)
                || !string.Equals(version, coverageAfter.DateVersions.GetValueOrDefault(date), StringComparison.Ordinal)))
                throw new BatchProductSalesCoverageVersionConflictException();
            coverageAfter = coverageFinal;
        }
        var totalsByProduct = quantitySummary.ByProduct.ToDictionary(row => row.ProductCode,
            row => (Quantity: row.Quantity, SalesAmount: row.SalesAmount), StringComparer.OrdinalIgnoreCase);
        var totalsByDate = quantitySummary.ByDate.ToDictionary(row => row.Date.Date,
            row => (Quantity: row.Quantity, SalesAmount: row.SalesAmount));
        var branchSummaries = branchRows
            .GroupBy(row => row.BranchCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group =>
            {
                var rows = group.ToList();
                return (Metrics: BuildNetMetrics(rows),
                    ContributingProductCount: rows.GroupBy(row => row.ProductCode, StringComparer.OrdinalIgnoreCase)
                        .Count(productRows => productRows.Sum(row => row.Quantity) != 0m || productRows.Sum(row => row.SalesAmount) != 0m));
            }, StringComparer.OrdinalIgnoreCase);
        var coverage = BuildCoverage(coverageAfter, stableReadyDates, queueResult);
        var result = new BatchProductSalesQueryResultDto
        {
            StartDate = range.StartDate,
            EndDate = range.EndDate,
            StoreCodes = storeCodes,
            Matches = matches,
            StatisticStatus = coverage.Status == "complete" ? "Fresh" : "Pending",
            StatisticUpdatedAt = coverageAfter.UpdatedAt,
            Coverage = coverage,
            Overview = new BatchProductSalesOverviewDto
            {
                Metrics = stableReadyDates.Count == 0 ? null : new BatchProductSalesMetricsDto { Quantity = quantitySummary.ByProduct.Sum(row => row.Quantity), SalesAmount = quantitySummary.ByProduct.Sum(row => row.SalesAmount), UnknownQuantity = quantitySummary.ByProduct.Sum(row => row.Quantity), DiscountStatus = "unknown" },
                Daily = stableReadyDates.Select(date => { var totals = totalsByDate.GetValueOrDefault(date.Date); return new BatchProductSalesDailyDto { Date = date, Metrics = new BatchProductSalesMetricsDto { Quantity = totals.Quantity, SalesAmount = totals.SalesAmount, UnknownQuantity = totals.Quantity, DiscountStatus = "unknown" } }; }).ToList(),
                Branches = (storeCodes ?? branchRows.Select(row => row.BranchCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList()).Select(store =>
                {
                    var summary = branchSummaries.GetValueOrDefault(store);
                    return new BatchProductSalesOverviewBranchDto { BranchCode = store, BranchName = storeScope.Names.GetValueOrDefault(store, store), Metrics = summary.Metrics ?? new BatchProductSalesMetricsDto(), Daily = [], ContributingProductCount = summary.ContributingProductCount, SelectedProductCount = products.Count };
                }).ToList(),
            },
            Products = products.Select(product => new BatchProductSalesProductSummaryDto
            {
                ProductCode = product.ProductCode, ItemNumber = product.ItemNumber,
                ProductName = product.ProductName, EnglishName = product.EnglishName,
                Barcode = product.Barcode, ImageUrl = product.ImageUrl,
                Quantity = stableReadyDates.Count == 0 ? null : totalsByProduct.GetValueOrDefault(product.ProductCode).Quantity,
                SalesAmount = stableReadyDates.Count == 0 ? null : totalsByProduct.GetValueOrDefault(product.ProductCode).SalesAmount,
            }).OrderBy(p => p.ItemNumber, StringComparer.OrdinalIgnoreCase).ToList(),
        };
        if (coverage.Status == "complete")
        {
            result.Warnings.Add("摘要净销量来自商品分店日统计；折扣拆分由后台成交聚合快照提供。");
        }
        else
        {
            result.Warnings.Add(stableReadyDates.Count == 0
                ? "所选日期尚无已完成统计，销量暂不可用。"
                : "仅展示已统计日期的销量，其余日期暂未计入。");
            queueResult.AppendWarnings(result.Warnings);
        }
        return ApiResponse<BatchProductSalesQueryResultDto>.OK(result);
    }

    public async Task<ApiResponse<BatchProductSalesDetailDto>> GetDetailAsync(
        BatchProductSalesDetailRequestDto request,
        IReadOnlyList<string>? scopedStoreCodes,
        CancellationToken cancellationToken = default)
    {
        var range = ValidateRange(request);
        var productCode = NormalizeRequired(request.ProductCode, "productCode");
        var storeScope = await ResolveEffectiveStoreScopeAsync(request.StoreCodes, scopedStoreCodes, cancellationToken);
        var storeCodes = storeScope.Codes;
        var product = (await LoadProductsAsync([productCode], cancellationToken)).SingleOrDefault();
        if (product == null)
            throw new BatchProductSalesAnalysisValidationException("商品不存在。");

        var coverageBefore = await _statisticReader.CoverageAsync(range.StartDate, range.EndDate, cancellationToken);
        var hasCoverageVersion = !string.IsNullOrWhiteSpace(request.CoverageVersion);
        var hasReadyDates = request.ReadyDates is { Count: > 0 };
        if (hasCoverageVersion != hasReadyDates)
            throw new BatchProductSalesAnalysisValidationException("coverageVersion 与 readyDates 必须同时传入。");
        var requestedReadyDates = hasReadyDates
            ? ParseReadyDates(request.ReadyDates!, range.StartDate, range.EndDate)
            : coverageBefore.ReadyDates.ToList();
        if (hasCoverageVersion && (!requestedReadyDates.All(coverageBefore.DateVersions.ContainsKey)
            || !string.Equals(request.CoverageVersion, BuildCoverageVersion(coverageBefore.DateVersions, requestedReadyDates), StringComparison.Ordinal)))
            throw new BatchProductSalesCoverageVersionConflictException();

        var quantities = await _statisticReader.ReadAsync(productCode, requestedReadyDates, storeCodes, cancellationToken);
        BatchProductSalesDiscountSnapshotReadResult discount;
        if (!request.IncludeDiscounts)
        {
            // 快速详情仅返回严格日统计销量；分类与价格没有快照证据时必须保持未知。
            discount = new(BatchProductSalesDiscountSnapshotReader.MarkUnknown(
                quantities, productCode, requestedReadyDates, storeCodes), "Pending", null);
        }
        else
        {
            try
            {
                // 请求只读取已发布的日快照；调度器负责两年预计算和后续回填。
                discount = await _discountSnapshotReader.ReadAsync(productCode, requestedReadyDates,
                    storeCodes, quantities, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "折扣日快照不可用，保留商品日统计销量 {ProductCode}", productCode);
                // 异常回退也必须显式标记未知，不能让净额刚好为零的销量被聚合为 complete。
                discount = new(BatchProductSalesDiscountSnapshotReader.MarkUnknown(
                    quantities, productCode, requestedReadyDates, storeCodes), "Unavailable", null);
            }
        }
        var discountState = discount.Status;
        var facts = discount.Rows;
        var coverageAfter = await _statisticReader.CoverageAsync(range.StartDate, range.EndDate, cancellationToken);
        var stableReadyDates = requestedReadyDates.Where(date => coverageAfter.DateVersions.TryGetValue(date, out var afterVersion)
            && coverageBefore.DateVersions.TryGetValue(date, out var beforeVersion)
            && string.Equals(beforeVersion, afterVersion, StringComparison.Ordinal)).ToList();
        if (hasCoverageVersion && stableReadyDates.Count != requestedReadyDates.Count)
            throw new BatchProductSalesCoverageVersionConflictException();
        if (!hasCoverageVersion && stableReadyDates.Count != requestedReadyDates.Count)
        {
            quantities = quantities.Where(row => stableReadyDates.Contains(row.Date.Date)).ToList();
            facts = facts.Where(row => stableReadyDates.Contains(row.Date.Date)).ToList();
        }
        var coverage = BuildCoverage(coverageAfter, stableReadyDates, BatchProductSalesStatisticQueueResult.NotNeeded);
        var storeNames = storeScope.Names;
        var result = new BatchProductSalesDetailDto
        {
            StartDate = range.StartDate,
            EndDate = range.EndDate,
            StoreCodes = storeCodes,
            ProductCodes = [productCode],
            Product = product,
            StatisticStatus = coverage.Status == "complete" ? "Fresh" : "Pending", StatisticUpdatedAt = coverageAfter.UpdatedAt,
            DiscountStatisticStatus = discountState, DiscountUpdatedAt = discount.UpdatedAt,
            Coverage = coverage,
            Metrics = stableReadyDates.Count == 0 ? new() { DiscountStatus = "pending" } : BuildAggregateMetrics(facts),
            Daily = BuildAggregateDaily(facts, stableReadyDates),
            Branches = facts.GroupBy(f => f.BranchCode, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => new BatchProductSalesBranchDto
                {
                    BranchCode = group.Key,
                    BranchName = storeNames.TryGetValue(group.Key, out var name) ? name : group.Key,
                    Metrics = BuildAggregateMetrics(group),
                    // 未选中分店只传有统计的日期，前端在选中和导出时补齐已知零日，减少全年响应体。
                    Daily = group.GroupBy(row => row.Date).OrderBy(days => days.Key)
                        .Select(days => new BatchProductSalesDailyDto { Date = days.Key, Metrics = BuildAggregateMetrics(days) }).ToList(),
                }).ToList(),
        };
        // Backfilling/Refreshing 由范围状态驱动前端轮询；逐日指标保留已核验或未知证据，不能整体改写为 pending。
        return ApiResponse<BatchProductSalesDetailDto>.OK(result);
    }

    public async Task<ApiResponse<BatchProductSalesBranchOverviewDto>> GetBranchOverviewAsync(BatchProductSalesBranchOverviewRequestDto request, IReadOnlyList<string>? scopedStoreCodes, CancellationToken cancellationToken = default)
    {
        var context = await ResolveFollowupAsync(request, scopedStoreCodes, cancellationToken);
        var branchCode = NormalizeRequired(request.BranchCode, "branchCode");
        if (!context.StoreCodes.Contains(branchCode, StringComparer.OrdinalIgnoreCase)) throw new BatchProductSalesAnalysisForbiddenException();
        // 分店页只需要商品总计和每日总计，在 SQL 端分别聚合，避免展开商品×日期矩阵。
        // 分店总览沿用统计读取器的整日范围，兼容历史数据中非午夜的 Date 值。
        var summary = await ReadQuantitySummaryAsync(context.ProductCodes, context.ReadyDates, [branchCode], cancellationToken);
        var branchRows = summary.ByDate.Select(row => new BatchProductSalesAggregateRow
        {
            Date = row.Date, BranchCode = branchCode, Quantity = row.Quantity,
            UnknownQuantity = row.Quantity, UnknownRowCount = 1, SalesAmount = row.SalesAmount,
        }).ToList();
        var productMetrics = summary.ByProduct.ToDictionary(row => row.ProductCode,
            row => BuildNetMetrics([new BatchProductSalesAggregateRow
            {
                Quantity = row.Quantity, UnknownQuantity = row.Quantity, SalesAmount = row.SalesAmount,
            }]), StringComparer.OrdinalIgnoreCase);
        var after = await _statisticReader.CoverageAsync(context.Range.StartDate, context.Range.EndDate, cancellationToken);
        ValidateLockedCoverage(context, after);
        var products = context.Products.Select(product => new BatchProductSalesBranchProductDto {
            ProductCode=product.ProductCode, ItemNumber=product.ItemNumber, ProductName=product.ProductName, EnglishName=product.EnglishName, Barcode=product.Barcode, ImageUrl=product.ImageUrl,
            Metrics=productMetrics.GetValueOrDefault(product.ProductCode) ?? BuildNetMetrics([]) }).ToList();
        return ApiResponse<BatchProductSalesBranchOverviewDto>.OK(new BatchProductSalesBranchOverviewDto { StartDate=context.Range.StartDate, EndDate=context.Range.EndDate, StoreCodes=context.StoreCodes, ProductCodes=context.ProductCodes, Coverage=BuildCoverage(after, context.ReadyDates, BatchProductSalesStatisticQueueResult.NotNeeded), Branch=BuildBranch(branchCode, context.StoreNames, branchRows, context.ReadyDates), Products=products });
    }

    public async Task<ApiResponse<BatchProductSalesDiscountOverviewDto>> GetDiscountOverviewAsync(BatchProductSalesBranchOverviewRequestDto request, IReadOnlyList<string>? scopedStoreCodes, CancellationToken cancellationToken = default)
    {
        var context = await ResolveFollowupAsync(request, scopedStoreCodes, cancellationToken);
        if (context.ProductCodes.Count > MaxDiscountOverviewProductCodes)
            throw new BatchProductSalesAnalysisValidationException("折扣分类总览最多支持 500 个商品；超过 500 个商品请查看净销量和金额。");
        var selectedStores = string.IsNullOrWhiteSpace(request.BranchCode) ? context.StoreCodes : [NormalizeRequired(request.BranchCode, "branchCode")];
        if (!selectedStores.All(store => context.StoreCodes.Contains(store, StringComparer.OrdinalIgnoreCase))) throw new BatchProductSalesAnalysisForbiddenException();
        var discountStateBefore = await _discountSnapshotReader.CaptureStateAsync(context.ReadyDates, cancellationToken);
        var readyDates = context.ReadyDates.Select(date => date.Date).ToHashSet();
        var facts = new List<BatchProductSalesAggregateRow>();
        var contributingByBranch = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var productMetrics = new Dictionary<string, BatchProductSalesMetricsDto>(StringComparer.OrdinalIgnoreCase);
        var discountStatuses = new List<string>();
        DateTime? discountUpdatedAt = null;
        foreach (var productBatch in context.ProductCodes.Chunk(20))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var statistics = await _statisticReader.ReadAsync(productBatch, context.ReadyDates, selectedStores, cancellationToken);
            var discounts = await _discountSnapshotReader.ReadManyAsync(productBatch, context.ReadyDates, selectedStores, statistics, cancellationToken, discountStateBefore);
            foreach (var value in discounts.Values)
            {
                discountStatuses.Add(value.Status);
                if (value.UpdatedAt.HasValue && (!discountUpdatedAt.HasValue || value.UpdatedAt > discountUpdatedAt))
                    discountUpdatedAt = value.UpdatedAt;
            }
            var batchFacts = discounts.Values.SelectMany(value => value.Rows)
                .Where(row => readyDates.Contains(row.Date.Date)).ToList();
            foreach (var branchGroup in batchFacts.GroupBy(row => row.BranchCode, StringComparer.OrdinalIgnoreCase))
            {
                var count = branchGroup.GroupBy(row => row.ProductCode, StringComparer.OrdinalIgnoreCase)
                    .Count(group => group.Sum(row => row.Quantity) != 0m || group.Sum(row => row.SalesAmount) != 0m);
                contributingByBranch[branchGroup.Key] = contributingByBranch.GetValueOrDefault(branchGroup.Key) + count;
            }
            if (!string.IsNullOrWhiteSpace(request.BranchCode))
                foreach (var group in batchFacts.GroupBy(row => row.ProductCode, StringComparer.OrdinalIgnoreCase))
                    if (!string.IsNullOrWhiteSpace(group.Key)) productMetrics[group.Key] = BuildAggregateMetrics(group);
            // 折扣快照按小批读取，只累积 Date×Branch 合计与商品指标，避免持有 3000 商品全年明细。
            facts = MergeDateBranchRows(facts.Concat(batchFacts));
        }
        var after = await _statisticReader.CoverageAsync(context.Range.StartDate, context.Range.EndDate, cancellationToken);
        ValidateLockedCoverage(context, after);
        var discountStateAfter = await _discountSnapshotReader.CaptureStateAsync(context.ReadyDates, cancellationToken);
        if (discountStateBefore.SchemaReady != discountStateAfter.SchemaReady
            || !string.Equals(discountStateBefore.Fingerprint, discountStateAfter.Fingerprint, StringComparison.Ordinal))
            throw new BatchProductSalesCoverageVersionConflictException();
        var coverage = BuildCoverage(after, context.ReadyDates, BatchProductSalesStatisticQueueResult.NotNeeded);
        var overview = BuildOverview(facts, context.ReadyDates, selectedStores, context.StoreNames, context.ProductCodes.Count, coverage.Status == "pending", contributingByBranch);
        var branch = string.IsNullOrWhiteSpace(request.BranchCode) ? null : BuildBranch(selectedStores[0], context.StoreNames, facts, context.ReadyDates);
        var products = branch == null ? [] : context.Products.Select(product => new BatchProductSalesBranchProductDto { ProductCode=product.ProductCode, ItemNumber=product.ItemNumber, ProductName=product.ProductName, EnglishName=product.EnglishName, Barcode=product.Barcode, ImageUrl=product.ImageUrl, Metrics=productMetrics.GetValueOrDefault(product.ProductCode) ?? BuildAggregateMetrics([]) }).ToList();
        return ApiResponse<BatchProductSalesDiscountOverviewDto>.OK(new BatchProductSalesDiscountOverviewDto { StartDate=context.Range.StartDate, EndDate=context.Range.EndDate, StoreCodes=context.StoreCodes, ProductCodes=context.ProductCodes, Coverage=coverage, Overview=overview, Branch=branch, Products=products, DiscountStatisticStatus=CombineDiscountStatus(discountStatuses), DiscountUpdatedAt=discountUpdatedAt, Warnings=coverage.Status == "pending" ? ["所选日期尚无已完成统计。"] : [] });
    }

    public async Task<string> ExportDetailCsvAsync(BatchProductSalesFollowupRequestDto request, IReadOnlyList<string>? scopedStoreCodes, CancellationToken cancellationToken = default)
    {
        var context = await ResolveFollowupAsync(request, scopedStoreCodes, cancellationToken);
        var netOnly = context.ProductCodes.Count > ProductCodeQueryBatchSize;
        var facts = netOnly
            // 超过 500 个货号时，折扣快照会产生大量商品×日期读取；导出改用日统计表的
            // SQL 汇总，保留净销量和金额，并把折扣分类明确标为未知。
            ? await ReadNetExportFactsAsync(context.ProductCodes, context.ReadyDates, context.StoreCodes, cancellationToken)
            : [];
        if (!netOnly)
        {
            foreach (var batch in context.ProductCodes.Chunk(20))
            {
                var statistics = await _statisticReader.ReadAsync(batch, context.ReadyDates, context.StoreCodes, cancellationToken);
                var discounts = await ReadExportDiscountsWithRetryAsync(batch, context.ReadyDates, context.StoreCodes, statistics, cancellationToken);
                // 每批立即折叠为 Date×Branch；导出不保留商品明细矩阵，下一批可释放。
                facts = MergeDateBranchRows(facts.Concat(discounts.Values.SelectMany(value => value.Rows)));
            }
        }
        var beforeWrite = await _statisticReader.CoverageAsync(context.Range.StartDate, context.Range.EndDate, cancellationToken);
        ValidateLockedCoverage(context, beforeWrite);
        var path = Path.Combine(Path.GetTempPath(), $"batch-product-sales-{Guid.NewGuid():N}.csv");
        try
        {
            var csv = BuildCsv(context, BuildCoverage(beforeWrite, context.ReadyDates, BatchProductSalesStatisticQueueResult.NotNeeded), facts, netOnly);
            // 文件已完整落盘后重新核验锁；冲突或取消只能走 finally，绝不下发半文件。
            await File.WriteAllTextAsync(path, csv, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
            var finalCoverage = await _statisticReader.CoverageAsync(context.Range.StartDate, context.Range.EndDate, cancellationToken);
            ValidateLockedCoverage(context, finalCoverage);
            cancellationToken.ThrowIfCancellationRequested();
            return await File.ReadAllTextAsync(path, cancellationToken);
        }
        finally { try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { _logger.LogWarning(ex, "删除批量销量导出临时文件失败"); } }
    }

    private async Task<Dictionary<string, BatchProductSalesDiscountSnapshotReadResult>> ReadExportDiscountsWithRetryAsync(
        IReadOnlyList<string> productCodes,
        IReadOnlyList<DateTime> readyDates,
        IReadOnlyList<string> storeCodes,
        IReadOnlyList<BatchProductSalesAggregateRow> statistics,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _discountSnapshotReader.ReadManyAsync(productCodes, readyDates, storeCodes, statistics, cancellationToken);
            }
            catch (SqlException exception) when (exception.Number == 1205 && attempt < maxAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(200 * attempt);
                _logger.LogWarning(exception,
                    "批量销量导出折扣快照读取发生死锁，第 {Attempt}/{MaxAttempts} 次重试将在 {DelayMs}ms 后执行",
                    attempt, maxAttempts, delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private async Task<List<BatchProductSalesAggregateRow>> ReadNetExportFactsAsync(
        IReadOnlyList<string> productCodes, IReadOnlyList<DateTime> readyDates,
        IReadOnlyList<string> storeCodes, CancellationToken cancellationToken)
    {
        var facts = new List<BatchProductSalesAggregateRow>();
        foreach (var productCodeBatch in productCodes.Chunk(ProductCodeQueryBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var datePredicate = BatchProductSalesStatisticReader.BuildDatePredicate(readyDates);
            var query = _db.Queryable<ProductStoreDailySalesStatistic>().With(SqlWith.Null)
                .Where(s => productCodeBatch.Contains(s.ProductCode))
                .Where(datePredicate.ToExpression())
                .Where(s => storeCodes.Contains(s.BranchCode));
            facts.AddRange(await query.GroupBy(s => new { s.Date, s.BranchCode })
                .Select(s => new BatchProductSalesAggregateRow
                {
                    Date = s.Date,
                    BranchCode = s.BranchCode,
                    Quantity = SqlFunc.AggregateSum(s.TotalQuantity),
                    UnknownQuantity = SqlFunc.AggregateSum(s.TotalQuantity),
                    UnknownRowCount = 1,
                    SalesAmount = SqlFunc.AggregateSum(s.TotalAmount),
                }).ToListAsync(cancellationToken));
        }
        return MergeDateBranchRows(facts);
    }

    private async Task<FollowupContext> ResolveFollowupAsync(BatchProductSalesFollowupRequestDto request, IReadOnlyList<string>? granted, CancellationToken token)
    {
        var range = ValidateRange(request);
        var products = NormalizeProductCodes(request.ProductCodes);
        if (string.IsNullOrWhiteSpace(request.CoverageVersion) || request.ReadyDates is not { Count: > 0 }) throw new BatchProductSalesAnalysisValidationException("coverageVersion 与 readyDates 必须同时传入。");
        var scope = await ResolveEffectiveStoreScopeAsync(request.StoreCodes, granted, token);
        var requested = NormalizeStoreCodes(request.StoreCodes);
        if (requested.Count > 0 && !requested.OrderBy(x=>x).SequenceEqual(scope.Codes.OrderBy(x=>x), StringComparer.OrdinalIgnoreCase)) throw new BatchProductSalesAnalysisForbiddenException();
        var coverage = await _statisticReader.CoverageAsync(range.StartDate, range.EndDate, token);
        var ready = ParseReadyDates(request.ReadyDates, range.StartDate, range.EndDate);
        if (!ready.All(coverage.DateVersions.ContainsKey) || !string.Equals(request.CoverageVersion, BuildCoverageVersion(coverage.DateVersions, ready), StringComparison.Ordinal)) throw new BatchProductSalesCoverageVersionConflictException();
        var productDtos = await LoadProductsAsync(products, token);
        if (productDtos.Count != products.Count) throw new BatchProductSalesAnalysisValidationException("商品不存在。");
        return new(range, products, productDtos, scope.Codes, scope.Names, ready, coverage);
    }

    private static void ValidateLockedCoverage(FollowupContext context, BatchProductSalesDateCoverage after)
    {
        if (context.ReadyDates.Any(date => !after.DateVersions.TryGetValue(date, out var version) || !string.Equals(version, context.Before.DateVersions.GetValueOrDefault(date), StringComparison.Ordinal))) throw new BatchProductSalesCoverageVersionConflictException();
    }
    private static List<string> NormalizeProductCodes(IEnumerable<string>? values) { var rows=values?.Where(v=>!string.IsNullOrWhiteSpace(v)).Select(v=>v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()??[]; if(rows.Count==0||rows.Count>MaxItemNumbers) throw new BatchProductSalesAnalysisValidationException($"商品数量必须在 1 到 {MaxItemNumbers} 之间。"); return rows; }
    private static BatchProductSalesMetricsDto BuildNetMetrics(IEnumerable<BatchProductSalesAggregateRow> rows) { var list=rows.ToList(); return new BatchProductSalesMetricsDto { Quantity=list.Sum(r=>r.Quantity), SalesAmount=list.Sum(r=>r.SalesAmount), UnknownQuantity=list.Sum(r=>r.Quantity), DiscountStatus="unknown" }; }
    private static BatchProductSalesBranchDto BuildBranch(string code, IReadOnlyDictionary<string,string> names, IEnumerable<BatchProductSalesAggregateRow> rows, IReadOnlyList<DateTime> dates) => new() { BranchCode=code, BranchName=names.GetValueOrDefault(code, code), Metrics=BuildAggregateMetrics(rows), Daily=BuildAggregateDaily(rows,dates) };
    private static BatchProductSalesOverviewDto BuildOverview(List<BatchProductSalesAggregateRow> facts, IReadOnlyList<DateTime> dates, IReadOnlyList<string> stores, IReadOnlyDictionary<string,string> names, int selected, bool pending, IReadOnlyDictionary<string,int> contributingByBranch) => new() { Metrics=pending?null:BuildAggregateMetrics(facts), Daily=pending?[]:BuildAggregateDaily(facts,dates), Branches=pending?[]:stores.Select(store=>new BatchProductSalesOverviewBranchDto { BranchCode=store, BranchName=names.GetValueOrDefault(store,store), Metrics=BuildAggregateMetrics(facts.Where(f=>string.Equals(f.BranchCode,store,StringComparison.OrdinalIgnoreCase))), Daily=BuildAggregateDaily(facts.Where(f=>string.Equals(f.BranchCode,store,StringComparison.OrdinalIgnoreCase)),dates), ContributingProductCount=contributingByBranch.GetValueOrDefault(store), SelectedProductCount=selected }).ToList() };
    private static string CombineDiscountStatus(IEnumerable<string> statuses)
    {
        var rows = statuses.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (rows.Count == 0) return "Pending";
        if (rows.Count == 1) return rows[0];
        // 混合时保留最能说明后台进度的状态，不能把 Backfilling/Refreshing 压成 Unavailable。
        if (rows.Contains("Fresh", StringComparer.OrdinalIgnoreCase) || rows.Contains("Refreshing", StringComparer.OrdinalIgnoreCase)) return "Partial";
        if (rows.Contains("Backfilling", StringComparer.OrdinalIgnoreCase)) return "Backfilling";
        if (rows.Contains("OutOfSync", StringComparer.OrdinalIgnoreCase)) return "OutOfSync";
        return "Unavailable";
    }
    private static List<BatchProductSalesAggregateRow> MergeDateBranchRows(IEnumerable<BatchProductSalesAggregateRow> rows) => rows
        .GroupBy(row => (row.Date.Date, row.BranchCode), new DateBranchComparer())
        .Select(group => new BatchProductSalesAggregateRow
        {
            Date = group.Key.Item1, BranchCode = group.Key.Item2,
            Quantity = group.Sum(row => row.Quantity), RegularQuantity = group.Sum(row => row.RegularQuantity), DiscountQuantity = group.Sum(row => row.DiscountQuantity),
            UnknownQuantity = group.Sum(row => row.UnknownQuantity), ReturnQuantity = group.Sum(row => row.ReturnQuantity), UnknownRowCount = group.Sum(row => row.UnknownRowCount),
            SalesAmount = group.Sum(row => row.SalesAmount), OriginalPriceMin = MinPrice(group.Select(row => row.OriginalPriceMin)), OriginalPriceMax = MaxPrice(group.Select(row => row.OriginalPriceMax)),
            DiscountPriceMin = MinPrice(group.Select(row => row.DiscountPriceMin)), DiscountPriceMax = MaxPrice(group.Select(row => row.DiscountPriceMax)),
        }).ToList();
    private sealed class DateBranchComparer : IEqualityComparer<(DateTime, string)>
    { public bool Equals((DateTime, string) x, (DateTime, string) y) => x.Item1 == y.Item1 && string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase); public int GetHashCode((DateTime, string) value) => HashCode.Combine(value.Item1, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Item2)); }

    private static string BuildCsv(FollowupContext context, BatchProductSalesCoverageDto coverage, List<BatchProductSalesAggregateRow> facts, bool netOnly = false)
    {
        static string E(string? value)
        {
            var cell = value ?? string.Empty;
            // 与前端导出保持一致：阻止 Excel 将门店名等外部文本解释成公式。
            if (System.Text.RegularExpressions.Regex.IsMatch(cell, @"^\s*[=+\-@]")) cell = "'" + cell;
            return "\"" + cell.Replace("\"", "\"\"") + "\"";
        }
        static string N(decimal value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var readyDates = context.ReadyDates.Select(date => date.Date).ToHashSet();
        var allDates = Enumerable.Range(0, (context.Range.EndDate.Date - context.Range.StartDate.Date).Days + 1)
            .Select(offset => context.Range.StartDate.Date.AddDays(offset)).ToList();
        var daily = BuildAggregateDaily(facts, context.ReadyDates).ToDictionary(row => row.Date.Date);
        string DailyValues(DateTime date, IReadOnlyDictionary<DateTime, BatchProductSalesDailyDto> rows)
        {
            // 未完成日期不能补零；用空数值保留日期轴，并明确这些日期不计入合计。
            // 5 个销量数值为空，加上 Status 共 6 个字段；与每日两种区段的表头列数一致。
            if (!readyDates.Contains(date.Date)) return ",,,,,,pending";
            var metrics = rows[date.Date].Metrics;
            return $",{N(metrics.Quantity)},{N(metrics.RegularQuantity)},{N(metrics.DiscountQuantity)},{N(metrics.UnknownQuantity)},{N(metrics.SalesAmount)},{metrics.DiscountStatus}";
        }
        var lines = new List<string>
        {
            "查询范围," + context.Range.StartDate.ToString("yyyy-MM-dd") + "," + context.Range.EndDate.ToString("yyyy-MM-dd"),
            // 门店范围是一个 CSV 单元格；先合并再转义，避免每个门店代码各自带引号而破坏列边界。
            "门店范围," + E(string.Join("|", context.StoreCodes)),
            "商品范围," + E(string.Join("|", context.ProductCodes)),
            "销量口径," + E(netOnly
                ? "仅含已完成日期的净销量；未统计日期数值为空且未计入合计；折扣分类未知"
                : "仅含已完成日期；未统计日期数值为空且未计入合计"),
            "Coverage," + coverage.Status,
            "CoverageVersion," + coverage.Version,
            "ReadyDates," + string.Join("|", coverage.ReadyDates),
            "PendingDates," + string.Join("|", coverage.PendingDates.Select(x => x.Date + ":" + x.Reason)),
            "合计每日", "Date,Quantity,Regular,Discount,Unknown,Amount,Status"
        };
        lines.AddRange(allDates.Select(date => $"{date:yyyy-MM-dd}{DailyValues(date, daily)}"));
        lines.Add("分店汇总"); lines.Add("Branch,Quantity,Regular,Discount,Unknown,Amount");
        lines.AddRange(context.StoreCodes.Select(store => { var m = BuildAggregateMetrics(facts.Where(f => string.Equals(f.BranchCode, store, StringComparison.OrdinalIgnoreCase))); return $"{E(context.StoreNames.GetValueOrDefault(store, store))},{N(m.Quantity)},{N(m.RegularQuantity)},{N(m.DiscountQuantity)},{N(m.UnknownQuantity)},{N(m.SalesAmount)}"; }));
        lines.Add("分店每日"); lines.Add("Branch,Date,Quantity,Regular,Discount,Unknown,Amount,Status");
        lines.AddRange(context.StoreCodes.SelectMany(store =>
        {
            var branchDaily = BuildAggregateDaily(facts.Where(f => string.Equals(f.BranchCode, store, StringComparison.OrdinalIgnoreCase)), context.ReadyDates)
                .ToDictionary(row => row.Date.Date);
            return allDates.Select(date => $"{E(context.StoreNames.GetValueOrDefault(store, store))},{date:yyyy-MM-dd}{DailyValues(date, branchDaily)}");
        }));
        return string.Join(Environment.NewLine, lines);
    }
    private sealed record FollowupContext((DateTime StartDate, DateTime EndDate) Range, List<string> ProductCodes, List<BatchProductSalesProductDto> Products, List<string> StoreCodes, Dictionary<string,string> StoreNames, List<DateTime> ReadyDates, BatchProductSalesDateCoverage Before);

    private async Task<List<BatchProductSalesMatchDto>> ResolveItemMatchesAsync(
        IReadOnlyList<string> itemNumbers, CancellationToken cancellationToken)
    {
        var rows = new List<(string? ItemNumber, string? ProductCode)>();
        foreach (var itemNumberBatch in itemNumbers.Chunk(ProductCodeQueryBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchRows = await _db.Queryable<Product>()
                .Where(p => p.ItemNumber != null && itemNumberBatch.Contains(p.ItemNumber))
                .Select(p => new { p.ItemNumber, p.ProductCode }).ToListAsync(cancellationToken);
            rows.AddRange(batchRows.Select(row => (row.ItemNumber, row.ProductCode)));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var byItem = rows.Where(r => !string.IsNullOrWhiteSpace(r.ItemNumber) && !string.IsNullOrWhiteSpace(r.ProductCode))
            .GroupBy(r => r.ItemNumber!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(r => r.ProductCode!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
        return itemNumbers.Select(item =>
        {
            var codes = byItem.TryGetValue(item, out var candidates) ? candidates : [];
            return new BatchProductSalesMatchDto
            {
                ItemNumber = item, ProductCodes = codes,
                Status = codes.Count switch { 0 => "notFound", 1 => "matched", _ => "ambiguous" },
            };
        }).ToList();
    }

    private async Task<List<BatchProductSalesProductDto>> LoadProductsAsync(
        IReadOnlyList<string> productCodes, CancellationToken cancellationToken)
    {
        if (productCodes.Count == 0) return [];
        var products = new List<BatchProductSalesProductDto>();
        foreach (var productCodeBatch in productCodes.Chunk(ProductCodeQueryBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            products.AddRange(await _db.Queryable<Product>().Where(p => productCodeBatch.Contains(p.ProductCode))
                .Select(p => new BatchProductSalesProductDto
                {
                    ProductCode = p.ProductCode ?? string.Empty, ItemNumber = p.ItemNumber ?? string.Empty, ProductName = p.ProductName ?? string.Empty,
                    EnglishName = p.EnglishName, Barcode = p.Barcode, ImageUrl = p.ProductImage,
                }).ToListAsync(cancellationToken));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return products.Where(p => !string.IsNullOrWhiteSpace(p.ProductCode))
            .GroupBy(p => p.ProductCode, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    private async Task<QuantitySummary> ReadQuantitySummaryAsync(
        IReadOnlyList<string> productCodes, IReadOnlyList<DateTime> dates,
        IReadOnlyList<string>? storeCodes, CancellationToken cancellationToken)
    {
        if (productCodes.Count == 0 || dates.Count == 0) return new([], []);
        var byProduct = new List<ProductQuantitySummary>();
        var byDate = new List<DailyQuantitySummary>();
        foreach (var productCodeBatch in productCodes.Chunk(ProductCodeQueryBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var productQuery = _db.Queryable<ProductStoreDailySalesStatistic>().With(SqlWith.Null)
                .Where(s => productCodeBatch.Contains(s.ProductCode));
            var dateQuery = _db.Queryable<ProductStoreDailySalesStatistic>().With(SqlWith.Null)
                .Where(s => productCodeBatch.Contains(s.ProductCode));
            // Date 字段历史上可能带有时间，统一按所选日期生成半开整日区间。
            var datePredicate = BatchProductSalesStatisticReader.BuildDatePredicate(dates);
            productQuery = productQuery.Where(datePredicate.ToExpression());
            dateQuery = dateQuery.Where(datePredicate.ToExpression());
            if (storeCodes != null)
            {
                productQuery = productQuery.Where(s => storeCodes.Contains(s.BranchCode));
                dateQuery = dateQuery.Where(s => storeCodes.Contains(s.BranchCode));
            }
            byProduct.AddRange(await productQuery.GroupBy(s => s.ProductCode).Select(s => new ProductQuantitySummary
            {
                ProductCode = s.ProductCode,
                Quantity = SqlFunc.AggregateSum(s.TotalQuantity),
                SalesAmount = SqlFunc.AggregateSum(s.TotalAmount),
            }).ToListAsync(cancellationToken));
            byDate.AddRange(await dateQuery.GroupBy(s => s.Date).Select(s => new DailyQuantitySummary
            {
                Date = s.Date,
                Quantity = SqlFunc.AggregateSum(s.TotalQuantity),
                SalesAmount = SqlFunc.AggregateSum(s.TotalAmount),
            }).ToListAsync(cancellationToken));
        }
        return new QuantitySummary(
            byProduct.GroupBy(row => row.ProductCode, StringComparer.OrdinalIgnoreCase)
                .Select(group => new ProductQuantitySummary { ProductCode = group.Key, Quantity = group.Sum(row => row.Quantity), SalesAmount = group.Sum(row => row.SalesAmount) }).ToList(),
            byDate.GroupBy(row => row.Date.Date)
                .Select(group => new DailyQuantitySummary { Date = group.Key, Quantity = group.Sum(row => row.Quantity), SalesAmount = group.Sum(row => row.SalesAmount) }).ToList());
    }

    internal static BatchProductSalesMetricsDto BuildAggregateMetrics(IEnumerable<BatchProductSalesAggregateRow> facts)
    {
        var rows = facts.ToList();
        var unknownRows = rows.Sum(row => row.UnknownRowCount);
        var regular = rows.Sum(row => row.RegularQuantity);
        var discount = rows.Sum(row => row.DiscountQuantity);
        var unknown = rows.Sum(row => row.UnknownQuantity);
        return new BatchProductSalesMetricsDto
        {
            Quantity = regular + discount + unknown, RegularQuantity = regular, DiscountQuantity = discount,
            UnknownQuantity = unknown, ReturnQuantity = rows.Sum(row => row.ReturnQuantity), SalesAmount = rows.Sum(row => row.SalesAmount),
            // 未知销售与退货可能刚好抵消；必须以未知行数而不是净数量判断状态。
            DiscountStatus = unknownRows > 0 ? (regular != 0m || discount != 0m ? "partial" : "unknown") : "complete",
            OriginalPriceMin = MinPrice(rows.Select(row => row.OriginalPriceMin)), OriginalPriceMax = MaxPrice(rows.Select(row => row.OriginalPriceMax)),
            DiscountPriceMin = MinPrice(rows.Select(row => row.DiscountPriceMin)), DiscountPriceMax = MaxPrice(rows.Select(row => row.DiscountPriceMax)),
        };
    }

    private static List<BatchProductSalesDailyDto> BuildAggregateDaily(IEnumerable<BatchProductSalesAggregateRow> facts, IEnumerable<DateTime> readyDates)
    {
        var map = facts.GroupBy(f => f.Date).ToDictionary(g => g.Key, g => BuildAggregateMetrics(g));
        return readyDates.Select(date => date.Date).Distinct().OrderBy(date => date)
            .Select(date => new BatchProductSalesDailyDto
            {
                Date = date,
                Metrics = map.TryGetValue(date, out var metrics) ? metrics : new BatchProductSalesMetricsDto { DiscountStatus = "complete" },
            }).ToList();
    }

    private static decimal? MinPrice(IEnumerable<decimal?> values) { var rows = values.Where(value => value.HasValue).Select(value => value!.Value).ToList(); return rows.Count == 0 ? null : rows.Min(); }
    private static decimal? MaxPrice(IEnumerable<decimal?> values) { var rows = values.Where(value => value.HasValue).Select(value => value!.Value).ToList(); return rows.Count == 0 ? null : rows.Max(); }

    private async Task<(List<string> Codes, Dictionary<string, string> Names)> ResolveEffectiveStoreScopeAsync(
        IEnumerable<string>? requested, IReadOnlyList<string>? granted, CancellationToken cancellationToken)
    {
        var requestedScope = ResolveStoreScope(requested, granted);
        var activeStores = await _db.Queryable<Store>().Where(s => s.IsDeleted == false)
            .Select(s => new BatchProductSalesStoreDto { Code = s.StoreCode, Name = s.StoreName }).ToListAsync();
        cancellationToken.ThrowIfCancellationRequested();
        // 权限与活跃分店每次请求重新解析，同时携带名称，避免单品详情再查一次门店。
        var permitted = activeStores.Where(s => !string.IsNullOrWhiteSpace(s.Code))
            .Select(s => new BatchProductSalesStoreDto { Code = s.Code.Trim(), Name = s.Name })
            .Where(s => requestedScope == null || requestedScope.Contains(s.Code, StringComparer.OrdinalIgnoreCase))
            .GroupBy(s => s.Code, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        return (permitted.Select(s => s.Code).ToList(), permitted.ToDictionary(s => s.Code,
            s => string.IsNullOrWhiteSpace(s.Name) ? s.Code : s.Name, StringComparer.OrdinalIgnoreCase));
    }

    private Task<BatchProductSalesStatisticStatus> GetStatisticStatusAsync(
        DateTime startDate, DateTime endDate, CancellationToken cancellationToken) =>
        _statisticReader.StatusAsync(startDate, endDate, cancellationToken);

    private static BatchProductSalesCoverageDto BuildCoverage(BatchProductSalesDateCoverage current,
        IReadOnlyList<DateTime> stableReadyDates, BatchProductSalesStatisticQueueResult queueResult)
    {
        var ready = stableReadyDates.Select(date => date.Date).Distinct().OrderBy(date => date).ToList();
        var pending = current.PendingReasons.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var date in current.DateVersions.Keys.Where(date => !ready.Contains(date))) pending[date] = "changed";
        return new BatchProductSalesCoverageDto
        {
            Status = pending.Count == 0 ? "complete" : ready.Count == 0 ? "pending" : "partial",
            ReadyDates = ready.Select(date => date.ToString("yyyy-MM-dd")).ToList(),
            PendingDates = pending.OrderBy(pair => pair.Key).Select(pair => new BatchProductSalesPendingDateDto
            {
                Date = pair.Key.ToString("yyyy-MM-dd"),
                // 查询期间刚完成的日期未参与本次事实读取，必须保持 changed，不能被先前的入队结果覆盖。
                Reason = current.PendingReasons.ContainsKey(pair.Key)
                    ? queueResult.GetPendingReason(pair.Key, pair.Value)
                    : pair.Value,
            }).ToList(),
            Version = BuildCoverageVersion(current.DateVersions, ready),
        };
    }

    private static string BuildCoverageVersion(IReadOnlyDictionary<DateTime, string> dateVersions, IEnumerable<DateTime> readyDates) =>
        BatchProductSalesStatisticReader.Hash(System.Text.Json.JsonSerializer.Serialize(readyDates.Select(date => date.Date)
            .Distinct().OrderBy(date => date).Select(date => new { Date = date.ToString("yyyy-MM-dd"), Version = dateVersions.GetValueOrDefault(date) })));

    private static List<DateTime> ParseReadyDates(IEnumerable<string> values, DateTime start, DateTime end)
    {
        var dates = values.Select(value => DateTime.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date) ? date.Date
                : throw new BatchProductSalesAnalysisValidationException("readyDates 必须为 yyyy-MM-dd。"))
            .Distinct().OrderBy(date => date).ToList();
        if (dates.Count == 0 || dates.Any(date => date < start || date > end))
            throw new BatchProductSalesAnalysisValidationException("readyDates 必须位于请求日期范围内。");
        return dates;
    }

    private async Task<BatchProductSalesStatisticQueueResult> QueuePendingStatisticDatesAsync(
        IReadOnlyList<DateTime> pendingDates,
        CancellationToken cancellationToken)
    {
        var submitted = new List<DateTime>();
        var alreadyActive = new List<DateTime>();
        var failures = new List<DateTime>();
        foreach (var segment in SplitStatisticQueueSegments(pendingDates))
        {
            try
            {
                var result = segment.IsYearBackfill
                    ? await _productStoreDailyQueue.EnqueueYearBackfillAsync(
                        segment.Dates, "batch-product-sales-analysis", cancellationToken: cancellationToken)
                    : await _productStoreDailyQueue.EnqueueAsync(
                        segment.Dates, "batch-product-sales-analysis", cancellationToken: cancellationToken);
                submitted.AddRange(result.SubmittedDates.Select(date => date.Date));
                alreadyActive.AddRange(result.SkippedDates.Select(date => date.Date));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 队列事务失败时不能把 Pending 说成已经提交；其余独立分段仍可尝试恢复可用性。
                _logger.LogError(ex, "批量货号销量查询提交商品日统计失败，日期 {StartDate:yyyy-MM-dd} 至 {EndDate:yyyy-MM-dd}",
                    segment.Dates[0], segment.Dates[^1]);
                failures.AddRange(segment.Dates);
            }
        }
        return new BatchProductSalesStatisticQueueResult(submitted, alreadyActive, failures);
    }

    /// <summary>
    /// 2025 年读取 HBSales 配对年度事实，沿用年度回填队列与其全局串行租约；其他年份遵守常规 31 天任务上限。
    /// Fresh 日期不会传入，因此重查冷门历史不会清空或重建已完成区间。
    /// </summary>
    internal static List<BatchProductSalesStatisticQueueSegment> SplitStatisticQueueSegments(IEnumerable<DateTime> pendingDates)
    {
        var dates = pendingDates.Select(date => date.Date).Distinct().OrderBy(date => date).ToList();
        var segments = new List<BatchProductSalesStatisticQueueSegment>();
        foreach (var group in dates.GroupBy(date => date.Year))
        {
            var groupDates = group.ToList();
            if (group.Key == 2025)
            {
                // 输入范围最多 366 天，单年度最多 365 天，符合年度回填队列上限。
                segments.Add(new BatchProductSalesStatisticQueueSegment(groupDates, true));
                continue;
            }
            foreach (var chunk in groupDates.Chunk(31))
                segments.Add(new BatchProductSalesStatisticQueueSegment(chunk.ToList(), false));
        }
        return segments;
    }

    internal static (DateTime StartDate, DateTime EndDate) ValidateRange(BatchProductSalesScopeDto request)
    {
        var start = request.StartDate.Date; var end = request.EndDate.Date;
        if (start == DateTime.MinValue || end == DateTime.MinValue || start > end || end > GetBrisbaneToday() || (end - start).TotalDays + 1 > MaxDays)
            throw new BatchProductSalesAnalysisValidationException("日期范围无效，最多 366 天。");
        return (start, end);
    }
    internal static List<string> NormalizeItemNumbers(IEnumerable<string>? values)
    {
        var items = values?.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        if (items.Count == 0 || items.Count > MaxItemNumbers) throw new BatchProductSalesAnalysisValidationException($"货号数量必须在 1 到 {MaxItemNumbers} 之间。");
        return items;
    }
    internal static List<string>? ResolveStoreScope(IEnumerable<string>? requested, IReadOnlyList<string>? granted)
    {
        var normalizedRequested = NormalizeStoreCodes(requested);
        var normalizedGranted = NormalizeScope(granted);
        if (normalizedGranted is { Count: 0 }) throw new BatchProductSalesAnalysisForbiddenException();
        if (normalizedGranted == null) return normalizedRequested.Count == 0 ? null : normalizedRequested;
        if (normalizedRequested.Count == 0) return normalizedGranted;
        if (normalizedRequested.Any(code => !normalizedGranted.Contains(code, StringComparer.OrdinalIgnoreCase))) throw new BatchProductSalesAnalysisForbiddenException();
        return normalizedRequested;
    }
    private static List<string>? NormalizeScope(IReadOnlyList<string>? values) => values == null ? null : NormalizeStoreCodes(values);
    private static List<string> NormalizeStoreCodes(IEnumerable<string>? values) => values?.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
    private static string NormalizeRequired(string? value, string name) => string.IsNullOrWhiteSpace(value) ? throw new BatchProductSalesAnalysisValidationException($"{name} 不能为空。") : value.Trim();
    private static DateTime GetBrisbaneToday()
    {
        try { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Australia/Brisbane")).Date; }
        catch (TimeZoneNotFoundException) { return DateTime.UtcNow.Date; }
    }
    internal sealed record BatchProductSalesStatisticQueueSegment(
        IReadOnlyList<DateTime> Dates,
        bool IsYearBackfill);

    private sealed class BatchProductSalesStatisticQueueResult
    {
        public static readonly BatchProductSalesStatisticQueueResult NotNeeded = new([], [], []);

        public BatchProductSalesStatisticQueueResult(
            IReadOnlyCollection<DateTime> submittedDates,
            IReadOnlyCollection<DateTime> alreadyActiveDates,
            IReadOnlyCollection<DateTime> failedDates)
        {
            SubmittedDates = submittedDates.Distinct().OrderBy(date => date).ToList();
            AlreadyActiveDates = alreadyActiveDates.Distinct().OrderBy(date => date).ToList();
            FailedDates = failedDates.Distinct().OrderBy(date => date).ToList();
        }

        private IReadOnlyList<DateTime> SubmittedDates { get; }
        private IReadOnlyList<DateTime> AlreadyActiveDates { get; }
        private IReadOnlyList<DateTime> FailedDates { get; }

        public void AppendWarnings(List<string> warnings)
        {
            if (SubmittedDates.Count > 0)
                warnings.Add($"已将 {SubmittedDates.Count} 个缺失或未完成统计日期提交到持久队列，等待后台生成后可重试查询。");
            if (AlreadyActiveDates.Count > 0)
                warnings.Add($"{AlreadyActiveDates.Count} 个统计日期已有活动队列任务，未重复创建任务。");
            if (FailedDates.Count > 0)
                warnings.Add($"{FailedDates.Count} 个统计日期提交失败，尚未排队，请人工重试。");
        }

        public string GetPendingReason(DateTime date, string currentReason)
        {
            date = date.Date;
            if (FailedDates.Contains(date)) return "queueFailed";
            if (SubmittedDates.Contains(date)) return "queued";
            if (AlreadyActiveDates.Contains(date)) return "active";
            return currentReason;
        }
    }

    private sealed class ProductQuantitySummary { public string ProductCode { get; set; } = string.Empty; public decimal Quantity { get; set; } public decimal SalesAmount { get; set; } }
    private sealed class DailyQuantitySummary { public DateTime Date { get; set; } public decimal Quantity { get; set; } public decimal SalesAmount { get; set; } }
    private sealed record QuantitySummary(List<ProductQuantitySummary> ByProduct, List<DailyQuantitySummary> ByDate);
}

public sealed class BatchProductSalesAnalysisValidationException(string message) : ArgumentException(message);
public sealed class BatchProductSalesAnalysisForbiddenException : UnauthorizedAccessException;
public sealed class BatchProductSalesCoverageVersionConflictException : InvalidOperationException;
