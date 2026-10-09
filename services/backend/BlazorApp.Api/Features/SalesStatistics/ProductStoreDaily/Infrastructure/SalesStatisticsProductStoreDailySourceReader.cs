using BlazorApp.Api.Data;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBSalesRecord;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;

namespace BlazorApp.Api.Services;

/// <summary>
/// 标记「HBSales 历史来源行缺商品编码且无法唯一解析商品」这类拒绝：由商品主档漂移所致，
/// 校验发生在任何写入之前，旧的已发布统计原封不动。失败状态写入据此保留旧快照，
/// 不把整天标成 Failed 去阻断报表。用 Exception.Data 打标而不改异常类型，既有精确类型断言与捕获不受影响。
/// </summary>
internal static class HBSalesProductCodeUnresolvedMarker
{
    private const string Key = "HBSalesProductCodeUnresolved";

    internal static T Mark<T>(T exception) where T : Exception
    {
        exception.Data[Key] = true;
        return exception;
    }

    internal static bool IsMarked(Exception? exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current.Data.Contains(Key))
                return true;
        }
        return false;
    }
}

/// <summary>商品分店日统计的来源读取边界：只装载输入，不承担聚合或写入。</summary>
internal sealed class SalesStatisticsProductStoreDailySourceReader
{
    private const int CommandTimeoutSeconds = 1800;
    private const int HBSalesMainCheckoutDateWindowDays = 7;
    private const int StoreCostProductQueryBatchSize = 500;
    private const int PosmSupplierMappingQueryBatchSize = 500;

    internal async Task<(ProductStoreDailyRefreshInput Input, List<StoreSalesStatistic> Stores)> LoadCurrentDaySnapshotAsync(
        SqlSugarContext context,
        POSMSqlSugarContext posmContext,
        ILogger logger,
        DateTime date,
        Func<Task<List<StoreSalesStatistic>>> loadStoreStatisticsAsync,
        bool writeDirectChinaSupplierCode = false)
    {
        if (!SalesStatisticsBusinessDate.IsToday(date) || date.Year == 2025)
            throw new ArgumentException("营业中快照只接受当天非 2025 日期", nameof(date));
        if (posmContext.Db.Ado.Transaction != null)
            throw new InvalidOperationException("当天统计快照必须独立持有 POSM 只读事务");
        ProductStoreDailyRefreshInput input = null!;
        List<StoreSalesStatistic> stores = null!;
        // 营业期间允许新订单持续上传。支付汇总、商品明细及来源水位固定在同一快照，
        // 新上传留给下一轮；主库写入开始前释放只读快照，避免占用来源事务过久。
        await SalesStatisticsTransactionExecutor.ExecuteAsync(
            beginAsync: () => posmContext.Db.Ado.BeginTranAsync(
                posmContext.Db.CurrentConnectionConfig.DbType == DbType.SqlServer
                    ? System.Data.IsolationLevel.Snapshot
                    : System.Data.IsolationLevel.Serializable),
            workAsync: async () =>
            {
                stores = await loadStoreStatisticsAsync();
                input = await LoadAsync(
                    context, posmContext, null, logger, date, null, null,
                    writeDirectChinaSupplierCode: writeDirectChinaSupplierCode);
            },
            commitAsync: () => posmContext.Db.Ado.CommitTranAsync(),
            rollbackAsync: () => posmContext.Db.Ado.RollbackTranAsync(),
            logger: logger, operationName: "读取当天分店与商品一致快照");
        return (input, stores);
    }

    internal async Task<ProductStoreDailyRefreshInput> LoadAsync(
        SqlSugarContext context,
        POSMSqlSugarContext posmContext,
        HBSalesRecordSqlSugarContext? hbSalesContext,
        ILogger logger,
        DateTime date,
        IReadOnlyList<ProductStoreDailySourceRow>? preloadedHBSalesRows,
        Posm2025DailySnapshot? preloadedPosmSnapshot,
        IReadOnlyCollection<string>? costProductCodes = null,
        bool writeDirectChinaSupplierCode = false)
    {
        var targetDate = date.Date;
        var nextDate = targetDate.AddDays(1);
        var posmWatermark = preloadedPosmSnapshot == null
            ? await SalesStatisticsProductStoreDailyStateSlice.QueryDailyPosmSourceWatermarkAsync(posmContext, targetDate)
            : SalesStatisticsProductStoreDailyDomainRules.GetPosmSnapshotWatermark(
                preloadedPosmSnapshot
            );

        var hbSalesRows = SalesStatisticsHBSalesHistoryWindow.Includes(targetDate)
            ? preloadedHBSalesRows?.ToList()
                ?? await LoadHBSalesProductStoreDailyRowsAsync(
                    hbSalesContext ?? throw new InvalidOperationException("HBSales 历史窗口内的商品统计缺少 HBSalesRecord 上下文"),
                    targetDate,
                    nextDate)
            : new List<ProductStoreDailySourceRow>();
        if (SalesStatisticsHBSalesHistoryWindow.Includes(targetDate))
            await ValidateAndResolveHBSalesRowsAsync(context, hbSalesContext, hbSalesRows, targetDate);

        var lastSourceUploadTime = SalesStatisticsProductStoreDailyDomainRules.GetLatestSourceTime(
            posmWatermark,
            SalesStatisticsProductStoreDailyDomainRules.GetLatestSourceTime(hbSalesRows)
        );
        var missingHBSalesBranchCount = hbSalesRows.Count(row => string.IsNullOrWhiteSpace(row.BranchCode));
        if (missingHBSalesBranchCount > 0)
        {
            // 缺分店来源仅记录诊断，不能在装载阶段篡改既有口径。
            logger.LogWarning(
                "HBSales 历史窗口内有 {Count} 条明细缺少分店编码，未写入商品分店统计: {Date}",
                missingHBSalesBranchCount,
                targetDate.ToString("yyyy-MM-dd"));
        }

        var detailRows = preloadedPosmSnapshot?.DetailRows.ToList() ?? await posmContext.Db.Queryable<SalesOrder>()
            .LeftJoin<SalesOrderDetail>((order, detail) => order.OrderGuid == detail.OrderGuid)
            .Where(order => order.Status != null
                && (order.Status == 1 || order.Status == 4)
                && order.OrderTime != null
                && order.OrderTime >= targetDate
                && order.OrderTime < nextDate)
            .Select((order, detail) => new ProductStoreDailySourceRow
            {
                Date = order.OrderTime!.Value.Date,
                OrderGuid = order.OrderGuid,
                DetailGuid = detail.OrderDetailGuid,
                BranchCode = order.BranchCode,
                DeviceCode = order.DeviceCode,
                OrderLastUploadTime = order.LastUploadTime,
                ProductCode = detail.ProductCode,
                SupplierCode = detail.SupplierCode,
                ProductName = detail.ProductName,
                Barcode = detail.Barcode,
                Price = detail.Price,
                Subtotal = detail.Subtotal,
                OriginalUnitPrice = detail.Price,
                OriginalSubtotal = detail.Subtotal,
                PriceLookupCode = detail.Barcode,
                OriginalSaleQuantity = detail.Quantity,
                Quantity = detail.Quantity ?? 0m,
                ActualAmount = detail.ActualAmount ?? 0m,
                DetailLastUploadTime = detail.LastUploadTime,
            })
            .ToListAsync();
        var detailGuidSet = detailRows.Select(row => row.DetailGuid)
            .Where(guid => !string.IsNullOrWhiteSpace(guid))
            .Select(guid => guid!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var supplementalReturnRows = preloadedPosmSnapshot?.SupplementalReturnRows.ToList()
            ?? await SalesStatisticsProductStoreDailySourceQueries.LoadSupplementalReturnRowsAsync(
                posmContext,
                targetDate,
                nextDate,
                detailGuidSet
            );
        var rawRows = detailRows.Concat(supplementalReturnRows).Concat(hbSalesRows).ToList();
        await ResolveAuthoritativeOpenItemCatalogAsync(
            context,
            rawRows,
            includeInactive: targetDate.Date < SalesStatisticsBusinessDate.Today());
        var orderAmountMaps = preloadedPosmSnapshot == null
            ? await SalesStatisticsProductStoreDailySourceQueries.LoadOrderAmountMapsAsync(
                posmContext,
                targetDate,
                nextDate,
                detailRows,
                row => row.OrderGuid,
                row => row.ActualAmount
            )
            : (
                PaymentAmounts: SalesStatisticsProductStoreDailyDomainRules.BuildOrderAmountMap(
                    preloadedPosmSnapshot.PaymentRows.Select(row => new OrderAmountRow
                    {
                        OrderGuid = row.OrderGuid,
                        Amount = row.Amount,
                    })
                ),
                DetailAmounts: SalesStatisticsProductStoreDailyDomainRules.BuildOrderAmountMap(
                    detailRows.Select(row => new OrderAmountRow
                    {
                        OrderGuid = row.OrderGuid,
                        Amount = row.ActualAmount,
                    })
                )
            );
        var deviceBranchMap = preloadedPosmSnapshot?.DeviceBranchMap.ToDictionary(
                entry => entry.Key,
                entry => entry.Value,
                StringComparer.OrdinalIgnoreCase
            )
            ?? await SalesStatisticsProductStoreDailySourceQueries.LoadDeviceBranchMapAsync(
                posmContext,
                rawRows.Where(row => string.IsNullOrWhiteSpace(row.BranchCode))
                    .Select(row => row.DeviceCode)
            );
        var productCodes = rawRows.Select(row => row.ProductCode)
            .Where(code => !string.IsNullOrWhiteSpace(code)).Select(code => code!).Distinct().ToList();
        var costProductCodeSet = costProductCodes?
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var costLookupProductCodes = costProductCodeSet == null
            ? productCodes
            : productCodes
                .Where(code => costProductCodeSet.Contains(code.Trim()))
                .ToList();
        var posmSupplierMapping = await LoadPosmSupplierMappingInBatchesAsync(
            posmContext,
            rawRows.Where(row => !row.IsHBSalesSource && string.IsNullOrWhiteSpace(row.SupplierCode))
                .Select(row => row.ProductCode));
        foreach (var row in rawRows.Where(row =>
            !row.IsHBSalesSource
            && string.IsNullOrWhiteSpace(row.SupplierCode)
            && !string.IsNullOrWhiteSpace(row.ProductCode)
            && posmSupplierMapping.ContainsKey(row.ProductCode.Trim())))
        {
            // HBSales 供应商是权威事实；仅 POSM 明细缺值时使用商品映射补齐。
            row.SupplierCode = posmSupplierMapping[row.ProductCode!.Trim()];
        }
        var branchCodes = rawRows.Select(row => SalesStatisticsCodeRules.ResolveBranchCode(
                row.BranchCode,
                row.DeviceCode,
                deviceBranchMap
            ))
            .Where(code => !string.IsNullOrWhiteSpace(code)).Distinct().ToList();
        // 成本回填可以只传入当前缺口商品，避免每天为 1 万个销售商品加载跨店价格与发票单位；
        // rawRows、供应商/分店身份和金额分摊仍始终使用完整商品集合，保持销售事实不变。
        var storeCosts = await LoadStoreCostsInBatchesAsync(
            context,
            costLookupProductCodes,
            branchCodes,
            includeInactive: targetDate.Date < SalesStatisticsBusinessDate.Today());
        var productCosts = costLookupProductCodes.Count == 0 ? new List<ProductCostRow>() : await context.Db.Queryable<Product>()
            .Where(product => product.ProductCode != null && costLookupProductCodes.Contains(product.ProductCode) && product.IsDeleted == false)
            .Select(product => new ProductCostRow { ProductCode = product.ProductCode, PurchasePrice = product.PurchasePrice })
            .ToListAsync();
        var warehouseCosts = costLookupProductCodes.Count == 0 ? new List<WarehouseCostRow>() : await context.Db.Queryable<WarehouseProduct>()
            .Where(product => costLookupProductCodes.Contains(product.ProductCode) && product.IsDeleted == false)
            .Select(product => new WarehouseCostRow { ProductCode = product.ProductCode, ImportPrice = product.ImportPrice })
            .ToListAsync();

        // 国内供应商编码目录与开关无关，总要加载：库里可能已有直写行，写入端按行键找旧行时
        // 要把国内编码族视为同一个供应商，否则历史成本快照会因为键对不上而被当前进价覆盖。
        var chinaSupplierCodes = await ChinaSupplierCodeFamily.LoadChinaSupplierCodesAsync(context.Db);
        // 只有开关打开才解析。原始明细行保持不动（成本回填的来源哈希和证据都基于它），改写发生在聚合器里。
        var chinaSupplierByProduct = writeDirectChinaSupplierCode
            ? await LoadChinaSupplierByProductAsync(context, chinaSupplierCodes)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return new ProductStoreDailyRefreshInput(
            targetDate, rawRows, supplementalReturnRows.ToHashSet(), orderAmountMaps.PaymentAmounts,
            orderAmountMaps.DetailAmounts, deviceBranchMap, storeCosts, productCosts, warehouseCosts,
            lastSourceUploadTime)
        {
            ChinaSupplierByProduct = chinaSupplierByProduct,
            ChinaSupplierCodes = chinaSupplierCodes,
        };
    }

    /// <summary>
    /// 按主数据解析国内货的国内供应商：未删除的仓库商品关联到未删除的国内商品，取它的供应商编码。
    /// 规则与 POSM 商品映射同步一致，但直接读主数据，不依赖映射是否同步及时。
    /// 编码必须存在于国内供应商目录：读取侧靠「编码是否属于目录」识别直写行，
    /// 目录外的编码写进去会被澳洲侧报表当成普通供应商，这种商品保持 200。
    /// 两张主数据表各约两万行，一条固定文本的查询整表读出；按当天商品编码分批 IN 查询的话，
    /// 每批 SQL 文本不同都要重新编译，批量重算多天时反而更慢。
    /// </summary>
    internal static async Task<Dictionary<string, string>> LoadChinaSupplierByProductAsync(
        SqlSugarContext context,
        HashSet<string> chinaSupplierCodes)
    {
        var rows = await context.Db.Queryable<WarehouseProduct>()
            .InnerJoin<DomesticProduct>((warehouse, domestic) => warehouse.ProductCode == domestic.ProductCode)
            .Where((warehouse, domestic) =>
                warehouse.IsDeleted == false
                && domestic.IsDeleted == false
                && domestic.SupplierCode != null
                && domestic.SupplierCode != "")
            .Select((warehouse, domestic) => new ChinaSupplierProductRow
            {
                ProductCode = warehouse.ProductCode,
                SupplierCode = domestic.SupplierCode,
            })
            .ToListAsync();

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var productCode = row.ProductCode?.Trim();
            var supplierCode = row.SupplierCode?.Trim();
            if (string.IsNullOrWhiteSpace(productCode) || string.IsNullOrWhiteSpace(supplierCode))
                continue;
            // 取目录里的规范写法，避免主数据与目录大小写不一致时写出两种编码。
            if (chinaSupplierCodes.TryGetValue(supplierCode, out var catalogCode))
                result[productCode] = catalogCode;
        }
        return result;
    }

    internal sealed class ChinaSupplierProductRow
    {
        public string? ProductCode { get; set; }
        public string? SupplierCode { get; set; }
    }

    private static async Task ValidateAndResolveHBSalesRowsAsync(
        SqlSugarContext context,
        HBSalesRecordSqlSugarContext? hbSalesContext,
        List<ProductStoreDailySourceRow> hbSalesRows,
        DateTime targetDate)
    {
        hbSalesRows.RemoveAll(row => row.Quantity == 0m && row.ActualAmount == 0m);
        await ResolveMissingHBSalesProductCodesAsync(
            context,
            hbSalesRows,
            includeInactive: targetDate.Date < SalesStatisticsBusinessDate.Today());
        await ResolveHBSalesReturnOriginalRowsAsync(hbSalesContext, hbSalesRows);
        var invalidRows = hbSalesRows.Where(row => string.IsNullOrWhiteSpace(row.BranchCode)
                || string.IsNullOrWhiteSpace(row.ProductCode))
            .ToList();
        if (invalidRows.Count == 0)
            return;

        var missingFields = new List<string>();
        if (invalidRows.Any(row => string.IsNullOrWhiteSpace(row.BranchCode)))
            missingFields.Add("分店编码");
        if (invalidRows.Any(row => string.IsNullOrWhiteSpace(row.ProductCode)))
            missingFields.Add("商品编码且无法获得唯一商品候选");
        var message =
            $"2025 HBSales 存在 {invalidRows.Count} 条非零来源行缺少{string.Join("或", missingFields)}，不能替换双表统计: {targetDate:yyyy-MM-dd}";
        // 只有「仅缺商品编码」才属于可保留旧快照的漂移；缺分店编码仍是来源数据缺陷，维持 Failed 契约。
        var exception = new InvalidOperationException(message);
        if (invalidRows.All(row => !string.IsNullOrWhiteSpace(row.BranchCode)))
            HBSalesProductCodeUnresolvedMarker.Mark(exception);
        throw exception;
    }

    private static bool IsOpenItemLookupCode(params string?[] values) => values.Any(value =>
        string.Equals(value?.Trim(), "OPENITEM", StringComparison.OrdinalIgnoreCase));

    private static async Task ResolveHBSalesReturnOriginalRowsAsync(
        HBSalesRecordSqlSugarContext? context,
        IReadOnlyList<ProductStoreDailySourceRow> rows)
    {
        var returnRows = rows.Where(row => row.DocumentType?.Trim() is "3" or "4").ToList();
        foreach (var row in returnRows)
            row.OriginalSaleCostEvidence = false;
        // 预加载快照可能没有 HBSales 上下文；此时保留缺口，不能把退货行当前的
        // B单价误当成原销售原价。后续带上下文的完整入口才能补做唯一原单关联。
        if (context == null)
            return;
        var originalOrderNumbers = returnRows
            .Select(row => row.OriginalHBSalesOrderNumber?.Trim())
            .Where(number => !string.IsNullOrWhiteSpace(number))
            .Select(number => number!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (originalOrderNumbers.Count == 0)
            return;

        var originalRows = await context.Db.Queryable<SalesOrderDetailRecord>()
            .Where(detail => detail.B销售单号 != null && originalOrderNumbers.Contains(detail.B销售单号))
            .Select(detail => new
            {
                detail.B销售单号,
                detail.ID,
                detail.B退货码,
                detail.B产品编号,
                detail.B条形码,
                detail.B单价,
                detail.B原价合计金额,
                detail.B数量,
                detail.B单位,
            })
            .ToListAsync();

        foreach (var row in returnRows)
        {
            var orderNumber = row.OriginalHBSalesOrderNumber?.Trim();
            if (string.IsNullOrWhiteSpace(orderNumber))
                continue;
            var candidates = originalRows.Where(original =>
                string.Equals(original.B销售单号?.Trim(), orderNumber, StringComparison.OrdinalIgnoreCase));
            var returnCode = row.HBSalesReturnCode?.Trim();
            var openItemLookup = IsOpenItemLookupCode(row.ProductCode, row.Barcode, row.PriceLookupCode);
            if (!string.IsNullOrWhiteSpace(returnCode))
            {
                candidates = candidates.Where(original =>
                    string.Equals(original.B退货码?.Trim(), returnCode, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                // 没有退货码时必须通过产品编号/条码同时唯一匹配，不能只按名称或价格猜原单。
                candidates = candidates.Where(original =>
                    (openItemLookup
                        || string.IsNullOrWhiteSpace(row.ProductCode)
                        || string.Equals(original.B产品编号?.Trim(), row.ProductCode.Trim(), StringComparison.OrdinalIgnoreCase))
                    && (string.IsNullOrWhiteSpace(row.Barcode)
                        || string.Equals(original.B条形码?.Trim(), row.Barcode.Trim(), StringComparison.OrdinalIgnoreCase)));
            }
            var matches = candidates.ToList();
            if (matches.Count != 1)
                continue;
            var original = matches[0];
            if (!openItemLookup
                && !string.IsNullOrWhiteSpace(row.ProductCode)
                && !string.IsNullOrWhiteSpace(original.B产品编号)
                && !string.Equals(original.B产品编号.Trim(), row.ProductCode.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;
            row.HBSalesUnitPrice = original.B单价;
            row.HBSalesOriginalAmount = original.B原价合计金额;
            row.OriginalUnitPrice = original.B单价;
            row.OriginalSubtotal = original.B原价合计金额;
            row.OriginalSaleQuantity = original.B数量;
            row.PricingUnit = original.B单位;
            row.OriginalSaleCostEvidence = true;
            if (string.IsNullOrWhiteSpace(row.PriceLookupCode)
                && IsOpenItemLookupCode(original.B条形码))
                row.PriceLookupCode = "OPENITEM";
        }
    }

    private static async Task ResolveAuthoritativeOpenItemCatalogAsync(
        SqlSugarContext context,
        IReadOnlyList<ProductStoreDailySourceRow> rows,
        bool includeInactive = false)
    {
        var productCodes = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.ProductCode))
            .Select(row => row.ProductCode!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var lookupCodes = rows
            .Where(row => string.IsNullOrWhiteSpace(row.ProductCode))
            .SelectMany(row => new[] { row.Barcode, row.PriceLookupCode })
            .Where(value => IsOpenItemLookupCode(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (productCodes.Count == 0 && lookupCodes.Count == 0)
            return;

        var catalogRows = new List<(string ProductCode, string Barcode)>();
        foreach (var batch in productCodes.Chunk(StoreCostProductQueryBatchSize))
        {
            var productQuery = context.Db.Queryable<Product>()
                .Where(product => product.IsDeleted == false
                    && product.ProductCode != null
                    && product.Barcode != null
                    && batch.Contains(product.ProductCode))
                .WhereIF(!includeInactive, product => product.IsActive);
            var candidates = await productQuery
                .Select(product => new
                {
                    product.ProductCode,
                    product.Barcode,
                })
                .ToListAsync();
            catalogRows.AddRange(candidates
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.ProductCode)
                    && !string.IsNullOrWhiteSpace(candidate.Barcode))
                .Select(candidate => (candidate.ProductCode!.Trim(), candidate.Barcode!.Trim())));
        }

        var lookupCatalogRows = new List<(string ProductCode, string Barcode)>();
        foreach (var batch in lookupCodes.Chunk(StoreCostProductQueryBatchSize))
        {
            var batchValues = batch.ToList();
            var productQuery = context.Db.Queryable<Product>()
                .Where(product => product.IsDeleted == false
                    && product.ProductCode != null
                    && product.Barcode != null
                    && batchValues.Contains(product.Barcode))
                .WhereIF(!includeInactive, product => product.IsActive);
            var candidates = await productQuery
                .Select(product => new
                {
                    product.ProductCode,
                    product.Barcode,
                })
                .ToListAsync();
            lookupCatalogRows.AddRange(candidates
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.ProductCode)
                    && !string.IsNullOrWhiteSpace(candidate.Barcode))
                .Select(candidate => (candidate.ProductCode!.Trim(), candidate.Barcode!.Trim())));
        }

        var openItemCatalogCodes = catalogRows
            .Where(candidate => IsOpenItemLookupCode(candidate.Barcode))
            .Select(candidate => candidate.ProductCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Where(row => !string.IsNullOrWhiteSpace(row.ProductCode)
            && string.IsNullOrWhiteSpace(row.Barcode)
            && string.IsNullOrWhiteSpace(row.PriceLookupCode)
            && openItemCatalogCodes.Contains(row.ProductCode!.Trim())))
        {
            // 仅以当前 Product 目录的精确 ProductCode -> Barcode=OPENITEM 关系确认，
            // 解决 G006243 一类历史明细缺 Barcode 的情况，不根据名称或货号推断。
            row.PriceLookupCode = "OPENITEM";
        }

        var lookupCandidates = lookupCatalogRows
            .GroupBy(candidate => candidate.Barcode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(candidate => candidate.ProductCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Where(row => string.IsNullOrWhiteSpace(row.ProductCode)))
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var lookup in new[] { row.Barcode, row.PriceLookupCode }
                .Where(value => IsOpenItemLookupCode(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (lookupCandidates.TryGetValue(lookup, out var codes))
                    candidates.UnionWith(codes);
            }

            // 只能把唯一权威目录商品编码写回来源；0 个或多个候选都保留缺口，
            // 绝不创造 OPENITEM synthetic 分组键。
            var canonicalCode = SalesStatisticsProductStoreDailyDomainRules
                .ResolveUniqueCanonicalProductCode(candidates);
            if (!string.IsNullOrWhiteSpace(canonicalCode))
                row.ProductCode = canonicalCode;
        }
    }

    internal static async Task<List<StoreCostRow>> LoadStoreCostsInBatchesAsync(
    SqlSugarContext context,
    IReadOnlyCollection<string> productCodes,
    IReadOnlyCollection<string> branchCodes,
    bool includeInactive = false
)
{
    if (productCodes.Count == 0 || branchCodes.Count == 0)
        return new List<StoreCostRow>();

    // branchCodes 仅用于保证调用方仍可传入目标分店；回退需要同时看到其它有效分店。
    // 查询仍按商品编码分批，避免把全表加载进内存。
    var rows = new List<StoreCostRow>();
    var invoiceDetailTableName = context.Db.EntityMaintenance
        .GetTableName(typeof(StoreLocalSupplierInvoiceDetails));
    var hasInvoiceDetailTable = context.Db.DbMaintenance.GetTableInfoList(false)
        .Any(table => string.Equals(table.Name, invoiceDetailTableName, StringComparison.OrdinalIgnoreCase));
    foreach (var productCodeBatch in productCodes
        .Distinct(StringComparer.Ordinal)
        .Chunk(StoreCostProductQueryBatchSize))
    {
        // 超大 IN 条件在 460 万行分店价格表上会导致 SQL Server 优化和并发超时；
        // 小批量查询继续命中现有 ProductCode + StoreCode 索引，统计口径保持不变。
        var batch = productCodeBatch.ToList();
        var storePriceQuery = context.Db.Queryable<StoreRetailPrice>()
            .Where(p =>
                p.ProductCode != null
                && p.StoreCode != null
                && batch.Contains(p.ProductCode)
                && p.SupplierCode != null
                && p.IsDeleted == false)
            .WhereIF(!includeInactive, p => p.IsActive);
        var batchRows = await storePriceQuery
            .Select(p => new StoreCostRow
            {
                StoreCode = p.StoreCode,
                SupplierCode = p.SupplierCode,
                ProductCode = p.ProductCode,
                IsActive = p.IsActive,
                PurchasePrice = p.PurchasePrice,
            })
            .ToListAsync();

        // StoreRetailPrice 本身没有单位列，使用同一分店/供应商/商品的进货明细单位
        // 作为实际计价单位来源；多个不同单位时保持未知，禁止跨店猜测。
        List<StoreCostRow> unitRows;
        if (hasInvoiceDetailTable)
        {
            unitRows = await context.Db.Queryable<StoreLocalSupplierInvoiceDetails>()
                .Where(detail => detail.ProductCode != null
                    && detail.StoreCode != null
                    && detail.SupplierCode != null
                    && detail.Unit != null
                    && detail.Unit.Trim() != ""
                    && batch.Contains(detail.ProductCode)
                    && detail.IsDeleted == false)
                .Select(detail => new StoreCostRow
                {
                    StoreCode = detail.StoreCode,
                    SupplierCode = detail.SupplierCode,
                    ProductCode = detail.ProductCode,
                    PricingUnit = detail.Unit,
                    PricingUnitKnown = true,
                })
                .ToListAsync();
        }
        else
        {
            unitRows = [];
        }
        var unitsByKey = unitRows
            .GroupBy(row => $"{row.StoreCode?.Trim()}|{row.SupplierCode?.Trim()}|{row.ProductCode?.Trim()}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => row.PricingUnit!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var row in batchRows)
        {
            var key = $"{row.StoreCode?.Trim()}|{row.SupplierCode?.Trim()}|{row.ProductCode?.Trim()}";
            if (unitsByKey.TryGetValue(key, out var units) && units.Count == 1)
            {
                row.PricingUnit = units[0];
                row.PricingUnitKnown = true;
            }
        }
        rows.AddRange(batchRows);
    }

    return rows;
}

internal static async Task<Dictionary<string, string>> LoadPosmSupplierMappingInBatchesAsync(
    POSMSqlSugarContext posmContext,
    IEnumerable<string?> productCodes)
{
    var targetProductCodes = productCodes
        .Where(code => !string.IsNullOrWhiteSpace(code))
        .Select(code => code!.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    var mappings = new List<PosmProductSupplierMapping>();
    foreach (var batch in targetProductCodes.Chunk(PosmSupplierMappingQueryBatchSize))
    {
        var batchCodes = batch.ToList();
        mappings.AddRange(await posmContext.Db.Queryable<PosmProductSupplierMapping>()
            .Where(mapping => batchCodes.Contains(mapping.ProductCode))
            .ToListAsync());
    }
    return mappings
        .Where(mapping => !string.IsNullOrWhiteSpace(mapping.ProductCode)
            && !string.IsNullOrWhiteSpace(mapping.LocalSupplierCode))
        .GroupBy(mapping => mapping.ProductCode.Trim(), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            group => group.Key,
            group => group.Select(mapping => mapping.LocalSupplierCode.Trim()).First(),
            StringComparer.OrdinalIgnoreCase);
}


internal static async Task<List<ProductStoreDailySourceRow>> LoadHBSalesProductStoreDailyRowsAsync(
    HBSalesRecordSqlSugarContext hbSalesContext,
    DateTime targetDate,
    DateTime nextDate,
    int? maxRows = null
)
{
    var originalCommandTimeout = hbSalesContext.Db.Ado.CommandTimeOut;
    hbSalesContext.Db.Ado.CommandTimeOut = Math.Max(
        originalCommandTimeout,
        CommandTimeoutSeconds
    );
    List<ProductStoreDailySourceRow> rows;
    try
    {
        if (hbSalesContext.Db.CurrentConnectionConfig.DbType == DbType.SqlServer)
        {
            var sql = BuildHBSalesProductStoreDailyRowsSql(hbSalesContext.Db, targetDate, nextDate, maxRows);
            rows = await hbSalesContext.Db.Ado.SqlQueryAsync<ProductStoreDailySourceRow>(sql.Key, sql.Value.ToArray());
        }
        else
        {
            var query = BuildHBSalesProductStoreDailyRowsQuery(hbSalesContext.Db, targetDate, nextDate);
            rows = maxRows.HasValue
                ? await query.Take(maxRows.Value + 1).ToListAsync()
                : await query.ToListAsync();
        }
    }
    finally
    {
        // 共享上下文可能被后续查询复用，必须还原调用方原有超时。
        hbSalesContext.Db.Ado.CommandTimeOut = originalCommandTimeout;
    }

    if (maxRows.HasValue && rows.Count > maxRows.Value)
    {
        throw new InvalidOperationException(
            $"2025 HBSales 批量快照超过 {maxRows.Value:N0} 行内存保护上限，请缩小日期范围"
        );
    }

    foreach (var row in rows.Where(row =>
        SalesStatisticsCodeRules.Normalize(row.DocumentType) == "3"
        || SalesStatisticsCodeRules.Normalize(row.DocumentType) == "4"
    ))
    {
        // HBSales 年度统计口径：类型 3/4 为退货/退款，数量和金额统一取反。
        row.Quantity = -row.Quantity;
        row.ActualAmount = -row.ActualAmount;
    }

    return rows;
}

/// <summary>
/// HBSales 明细在 SQL Server 上按实际日期重编译。结账日期是 date 列，而 SqlSugar 把日期变量下发为 datetime 参数，
/// 带参缓存的计划用不上 IX_B销售清单详情表副本_折扣日日期单号 覆盖索引（BatchProductSalesDiscountSourceIndexes.sql），
/// 每次聚集扫描明细表约 40 万页：2026-09-21 生产原句 sp_executesql 复现 17.6 秒，
/// 加 OPTION (RECOMPILE) 后 CPU 15 毫秒、明细表逻辑读 60 次，结果逐行一致。
/// </summary>
internal static KeyValuePair<string, List<SugarParameter>> BuildHBSalesProductStoreDailyRowsSql(
    ISqlSugarClient db,
    DateTime targetDate,
    DateTime nextDate,
    int? maxRows = null
)
{
    var query = BuildHBSalesProductStoreDailyRowsQuery(db, targetDate, nextDate);
    var sql = (maxRows.HasValue ? query.Take(maxRows.Value + 1) : query).ToSql();
    return new KeyValuePair<string, List<SugarParameter>>(sql.Key + " OPTION (RECOMPILE)", sql.Value);
}

internal static ISugarQueryable<ProductStoreDailySourceRow> BuildHBSalesProductStoreDailyRowsQuery(
    ISqlSugarClient db,
    DateTime targetDate,
    DateTime nextDate
)
{
    var mainCheckoutDateWindowStart = targetDate.AddDays(
        -HBSalesMainCheckoutDateWindowDays
    );
    var mainCheckoutDateWindowEnd = nextDate.AddDays(HBSalesMainCheckoutDateWindowDays);
    return db.Queryable<SalesOrderMain>()
            .LeftJoin<SalesOrderDetailRecord>((main, detail) =>
                main.B销售单号 == detail.B销售单号
            )
            .Where((main, detail) =>
                detail.B结账日期.HasValue
                && detail.B结账日期.Value >= targetDate
                && detail.B结账日期.Value < nextDate
                && main.B结账日期.HasValue
                && main.B结账日期.Value >= mainCheckoutDateWindowStart
                && main.B结账日期.Value < mainCheckoutDateWindowEnd
                &&
                // SQL 的 != 会丢掉 NULL；年度口径是只排除类型 2。
                (main.B单据类型 == null || main.B单据类型.Trim() != "2")
            )
            .Select((main, detail) => new ProductStoreDailySourceRow
            {
                IsHBSalesSource = true,
                Date = detail.B结账日期!.Value.Date,
                // 前缀避免与 POSM 的订单号偶然相同而误合并订单数。
                OrderGuid = "HBSALES:" + main.B销售单号,
                // 分店统计的订单数必须保留源单号的 null 语义，不能从带前缀的显示键反推。
                HBSalesOrderNumber = main.B销售单号,
                DetailGuid = $"HBSALES:{detail.ID}",
                // 与既有 2025 年度统计保持一致：分店以明细记录为准，不从主表补写。
                BranchCode = detail.B分店代码,
                ProductCode = detail.B产品编号,
                ItemNumber = detail.B货号,
                SupplierCode = detail.B供应商ID,
                ProductName = detail.B商品名,
                Barcode = detail.B条形码,
                HBSalesUnitPrice = detail.B单价,
                HBSalesOriginalAmount = detail.B原价合计金额,
                OriginalUnitPrice = detail.B单价,
                OriginalSubtotal = detail.B原价合计金额,
                PricingUnit = detail.B单位,
                PriceLookupCode = detail.B条形码,
                OriginalSaleQuantity = detail.B数量,
                OriginalHBSalesOrderNumber = main.B原销售单号,
                HBSalesReturnCode = detail.B退货码,
                Quantity = detail.B数量 ?? 0m,
                ActualAmount = detail.B合计金额 ?? 0m,
                // HBSales 使用明细/主表的最后修改时间；创建时间仅作为旧记录的可靠回退。
                // 同时保留四个原始时间，供 pre 水位按字段独立 MAX，不能由回退值反推。
                HBSalesMainLastModifiedAt = main.FGC_LastModifyDate,
                HBSalesMainCreatedAt = main.FGC_CreateDate,
                HBSalesDetailLastModifiedAt = detail.FGC_LastModifyDate,
                HBSalesDetailCreatedAt = detail.FGC_CreateDate,
                OrderLastUploadTime = main.FGC_LastModifyDate ?? main.FGC_CreateDate,
                DetailLastUploadTime = detail.FGC_LastModifyDate ?? detail.FGC_CreateDate,
                DocumentType = main.B单据类型,
            });
}


internal static async Task ResolveMissingHBSalesProductCodesAsync(
    SqlSugarContext context,
    IReadOnlyList<ProductStoreDailySourceRow> hbSalesRows,
    bool includeInactive = false
)
{
    var rowsToResolve = hbSalesRows
        .Where(row => string.IsNullOrWhiteSpace(row.ProductCode))
        .Where(row =>
            !string.IsNullOrWhiteSpace(SalesStatisticsCodeRules.Normalize(row.ItemNumber))
            || !string.IsNullOrWhiteSpace(SalesStatisticsCodeRules.Normalize(row.Barcode))
        )
        .ToList();
    if (!rowsToResolve.Any())
    {
        return;
    }

    var globalLookupCodes = rowsToResolve
        .SelectMany(row => new[]
        {
            SalesStatisticsCodeRules.Normalize(row.ItemNumber),
            SalesStatisticsCodeRules.Normalize(row.Barcode),
        })
        .Where(code => !string.IsNullOrWhiteSpace(code))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    var barcodeLookupCodes = rowsToResolve
        .Select(row => SalesStatisticsCodeRules.Normalize(row.Barcode))
        .Where(code => !string.IsNullOrWhiteSpace(code))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    var globalCandidates = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
    var branchCandidates = new Dictionary<(string StoreCode, string Barcode), HashSet<string>>();
    var crossStoreCandidates = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

    void AddGlobalCandidate(string? lookupCode, string? productCode)
    {
        var normalizedLookupCode = SalesStatisticsCodeRules.Normalize(lookupCode);
        var normalizedProductCode = SalesStatisticsCodeRules.Normalize(productCode);
        if (string.IsNullOrWhiteSpace(normalizedLookupCode)
            || string.IsNullOrWhiteSpace(normalizedProductCode))
        {
            return;
        }

        if (!globalCandidates.TryGetValue(normalizedLookupCode, out var candidates))
        {
            candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            globalCandidates[normalizedLookupCode] = candidates;
        }
        candidates.Add(normalizedProductCode);
    }

    void AddStoreCandidate(string? storeCode, string? lookupCode, string? productCode)
    {
        var normalizedStoreCode = SalesStatisticsCodeRules.Normalize(storeCode);
        var normalizedLookupCode = SalesStatisticsCodeRules.Normalize(lookupCode);
        var normalizedProductCode = SalesStatisticsCodeRules.Normalize(productCode);
        if (string.IsNullOrWhiteSpace(normalizedStoreCode)
            || string.IsNullOrWhiteSpace(normalizedLookupCode)
            || string.IsNullOrWhiteSpace(normalizedProductCode))
        {
            return;
        }

        var branchKey = (
            normalizedStoreCode.ToUpperInvariant(),
            normalizedLookupCode.ToUpperInvariant()
        );
        if (!branchCandidates.TryGetValue(branchKey, out var candidates))
        {
            candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            branchCandidates[branchKey] = candidates;
        }
        candidates.Add(normalizedProductCode);

        if (!crossStoreCandidates.TryGetValue(normalizedLookupCode, out var crossStore))
        {
            crossStore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            crossStoreCandidates[normalizedLookupCode] = crossStore;
        }
        crossStore.Add(normalizedProductCode);
    }

    foreach (var lookupCodeBatch in globalLookupCodes.Chunk(StoreCostProductQueryBatchSize))
    {
        // 只查询当前缺码行携带的货号/条码，分批限制 IN 条件大小，避免不受控参数量。
        var batch = lookupCodeBatch.ToList();
        var productQuery = context.Db.Queryable<Product>()
            .Where(product =>
                product.IsDeleted == false
                && product.ProductCode != null
                && (
                    (product.ItemNumber != null && batch.Contains(product.ItemNumber))
                    || (product.Barcode != null && batch.Contains(product.Barcode))
                )
            )
            .WhereIF(!includeInactive, product => product.IsActive);
        var productRows = await productQuery
            .Select(product => new { product.ItemNumber, product.Barcode, product.ProductCode })
            .ToListAsync();
        foreach (var product in productRows)
        {
            AddGlobalCandidate(product.ItemNumber, product.ProductCode);
            AddGlobalCandidate(product.Barcode, product.ProductCode);
        }
    }

    foreach (var barcodeBatch in barcodeLookupCodes.Chunk(StoreCostProductQueryBatchSize))
    {
        // ProductSetCode 和 StoreMultiCodeProduct 都以条码为强键，后者还必须匹配分店。
        var batch = barcodeBatch.ToList();
        var productSetCodeQuery = context.Db.Queryable<ProductSetCode>()
            .Where(setCode =>
                setCode.IsDeleted == false
                && setCode.ProductCode != null
                && setCode.SetBarcode != null
                && batch.Contains(setCode.SetBarcode)
            )
            .WhereIF(!includeInactive, setCode => setCode.IsActive);
        var productSetCodeRows = await productSetCodeQuery
            .Select(setCode => new { setCode.SetBarcode, setCode.ProductCode })
            .ToListAsync();
        foreach (var setCode in productSetCodeRows)
        {
            AddGlobalCandidate(setCode.SetBarcode, setCode.ProductCode);
        }

        var storeMultiCodeQuery = context.Db.Queryable<StoreMultiCodeProduct>()
            .Where(multiCode =>
                multiCode.IsDeleted == false
                && multiCode.StoreCode != null
                && multiCode.ProductCode != null
                && multiCode.MultiBarcode != null
                && batch.Contains(multiCode.MultiBarcode)
            )
            .WhereIF(!includeInactive, multiCode => multiCode.IsActive);
        var storeMultiCodeRows = await storeMultiCodeQuery
            .Select(multiCode => new
            {
                multiCode.StoreCode,
                multiCode.MultiBarcode,
                multiCode.ProductCode,
            })
            .ToListAsync();
        foreach (var multiCode in storeMultiCodeRows)
        {
            AddStoreCandidate(multiCode.StoreCode, multiCode.MultiBarcode, multiCode.ProductCode);
        }
    }

    foreach (var row in rowsToResolve)
    {
        var barcode = SalesStatisticsCodeRules.Normalize(row.Barcode);
        var branchKey = (
            SalesStatisticsCodeRules.Normalize(row.BranchCode).ToUpperInvariant(),
            barcode.ToUpperInvariant()
        );
        if (!string.IsNullOrWhiteSpace(barcode)
            && branchCandidates.TryGetValue(branchKey, out var branch))
        {
            // 第一层命中后不可再被全局候选推翻；多候选直接保留为空，不能降级。
            if (branch.Count == 1)
            {
                row.ProductCode = SalesStatisticsProductStoreDailyDomainRules
                    .SelectDeterministicProductCode(branch);
            }
            continue;
        }

        var global = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lookupCode in new[]
            {
                SalesStatisticsCodeRules.Normalize(row.ItemNumber),
                SalesStatisticsCodeRules.Normalize(row.Barcode),
            }
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (globalCandidates.TryGetValue(lookupCode, out var candidates))
            {
                global.UnionWith(candidates);
            }
        }

        // 第二层将 Product 的货号/条码候选与 ProductSetCode 条码候选合并；冲突同样不能降级。
        if (global.Count == 1)
        {
            row.ProductCode = SalesStatisticsProductStoreDailyDomainRules
                .SelectDeterministicProductCode(global);
            continue;
        }
        if (global.Count > 1 || string.IsNullOrWhiteSpace(barcode))
        {
            continue;
        }

        // 仅前两层均无候选时，才允许跨分店多码条码回退；多候选保持空并由原子路径失败。
        if (crossStoreCandidates.TryGetValue(barcode, out var crossStore)
            && crossStore.Count == 1)
        {
            row.ProductCode = SalesStatisticsProductStoreDailyDomainRules
                .SelectDeterministicProductCode(crossStore);
        }
    }
}

}
