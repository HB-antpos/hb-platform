using System.Security.Cryptography;
using System.Diagnostics;
using System.Globalization;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBSalesRecord;
using BlazorApp.Shared.Models.POSM;
using SqlSugar;

namespace BlazorApp.Api.Services.React;

/// <summary>
/// 定时快照的单日来源边界。先固定整个营业日的来源签名，再由既有 SQL 聚合器一次计算全商品、全分店；
/// 不允许按页面商品或门店重新扫描成交明细。
/// </summary>
internal sealed class BatchProductSalesDiscountSnapshotSourceReader(
    ISqlSugarClient catalogDb,
    ISqlSugarClient posmDb,
    ISqlSugarClient hbSalesDb,
    ILogger<BatchProductSalesDiscountSnapshotSourceReader>? logger = null)
{
    private const string DiscountClassificationRuleVersion = "batch-product-sales-discount-v3";
    private readonly ISqlSugarClient _catalogDb = catalogDb;
    private readonly ISqlSugarClient _posmDb = posmDb;
    private readonly ISqlSugarClient _hbSalesDb = hbSalesDb;

    internal async Task<List<BatchProductSalesAggregateRow>> ReadDayAsync(
        DateTime day,
        CancellationToken token)
    {
        var prepared = await CapturePreparedAsync(day, token);
        var result = await ReadPreparedDayAsync(prepared, token);
        return result.Rows;
    }

    /// <summary>
    /// 固定单日来源范围与版本。调度器可以复用同一个准备结果做 Fresh 检查和事实读取，
    /// 避免在同一次工作项中重复扫描来源；仍必须在读取后重新 capture 作为发布围栏。
    /// </summary>
    internal async Task<PreparedDay> CapturePreparedAsync(DateTime day, CancellationToken token)
    {
        return await WithSourceCancellationAsync(token, async () =>
        {
            var elapsed = Stopwatch.StartNew();
            DaySourceSnapshot snapshot;
            try { snapshot = await CaptureAsync(day.Date, token); }
            finally
            {
                // 保留取消样本的耗时，避免将整段来源预算误认成最后一条 SQL 的执行时间。
                logger?.LogInformation("折扣来源阶段耗时: {Date}, Stage=来源签名, ElapsedMs={ElapsedMs}, Canceled={Canceled}",
                    day.Date, elapsed.ElapsedMilliseconds, token.IsCancellationRequested);
            }
            return new PreparedDay(day.Date, snapshot,
                BuildSourceVersion(snapshot.PosmSignature, snapshot.HBSalesSignature, snapshot.AliasVersion,
                    snapshot.DiscountSemanticVersion, snapshot.SupplierMappingVersion));
        });
    }

    /// <summary>使用已经固定的范围执行事实聚合，并以前后完整来源版本相等作为结果可发布的前提。</summary>
    internal async Task<PreparedDayReadResult> ReadPreparedDayAsync(PreparedDay prepared, CancellationToken token)
    {
        return await WithSourceCancellationAsync(token, async () =>
        {
            ArgumentNullException.ThrowIfNull(prepared);
            var before = prepared.Snapshot;
            if (before.ProductCodes.Count == 0 || before.StoreCodes.Count == 0)
            {
                var emptyAfter = await CapturePreparedAsync(prepared.Day, token);
                if (!string.Equals(prepared.SourceVersion, emptyAfter.SourceVersion, StringComparison.Ordinal))
                    throw SourceChangedDuringRead(prepared.Day);
                return new PreparedDayReadResult([], emptyAfter.SourceVersion);
            }

            // 聚合器的 products/stores 是单日来源一次性收集的全集，只作为 SQL OPENJSON 范围；
            // 不会形成“商品数 × 日期”或“商品数 × 分店”的查询循环。
            var elapsed = Stopwatch.StartNew();
            List<BatchProductSalesAggregateRow> facts;
            try
            {
                facts = await new BatchProductSalesAnalysisFactReader(_catalogDb, _posmDb, _hbSalesDb)
                    .ReadAsync(before.ProductCodes, prepared.Day, prepared.Day, before.StoreCodes, token, before.HBSalesAliases);
            }
            finally
            {
                logger?.LogInformation("折扣来源阶段耗时: {Date}, Stage=事实聚合, ElapsedMs={ElapsedMs}, Canceled={Canceled}",
                    prepared.Day, elapsed.ElapsedMilliseconds, token.IsCancellationRequested);
            }

            var after = await CapturePreparedAsync(prepared.Day, token);
            if (!string.Equals(prepared.SourceVersion, after.SourceVersion, StringComparison.Ordinal))
                throw SourceChangedDuringRead(prepared.Day);

            return new PreparedDayReadResult(facts, after.SourceVersion);
        });
    }

    internal async Task<string> GetSourceVersionAsync(DateTime day, CancellationToken token)
    {
        return (await CapturePreparedAsync(day, token)).SourceVersion;
    }

    /// <summary>
    /// SqlSugar 的带 token 查询会把 token 留在 client 级别的 ADO 上，后续无 token 的写入仍会继承它。
    /// 三个来源都可能在嵌套读取中复用，所以必须恢复调用前状态，而非无条件清空。
    /// </summary>
    private async Task<T> WithSourceCancellationAsync<T>(CancellationToken token, Func<Task<T>> action)
    {
        var catalogToken = _catalogDb.Ado.CancellationToken;
        var posmToken = _posmDb.Ado.CancellationToken;
        var hbSalesToken = _hbSalesDb.Ado.CancellationToken;
        _catalogDb.Ado.CancellationToken = token;
        _posmDb.Ado.CancellationToken = token;
        _hbSalesDb.Ado.CancellationToken = token;
        try
        {
            return await action();
        }
        finally
        {
            RestoreAdoCancellationToken(_hbSalesDb, hbSalesToken);
            RestoreAdoCancellationToken(_posmDb, posmToken);
            RestoreAdoCancellationToken(_catalogDb, catalogToken);
        }
    }

    private static void RestoreAdoCancellationToken(ISqlSugarClient db, CancellationToken? token)
    {
        if (token.HasValue)
            db.Ado.CancellationToken = token.Value;
        else
            db.Ado.RemoveCancellationToken();
    }

    private static InvalidOperationException SourceChangedDuringRead(DateTime day) => new(
        $"批量折扣日快照来源在读取期间发生变化，拒绝发布并等待重试: {day:yyyy-MM-dd}");

    /// <summary>
    /// 组合 canonical POSM/HBSales 签名、实际参与解析的别名候选和规则版本。
    /// 不能以 MAX(上传时间) 代替：旧时间戳的支付、退货或别名修复也必须使日快照失效。
    /// </summary>
    internal static string BuildSourceVersion(
        Posm2025DailySnapshotSignature posmSignature,
        HBSales2025DailySnapshotSignature? hbSalesSignature,
        string aliasVersion,
        string discountSemanticVersion = "",
        string supplierMappingVersion = "")
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, DiscountClassificationRuleVersion);
        AppendPosmSignature(hash, posmSignature);
        if (hbSalesSignature == null)
        {
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, "no-hbsales");
        }
        else
        {
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, hbSalesSignature.Date);
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, hbSalesSignature.RowCount);
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, hbSalesSignature.MainLastModifiedAt);
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, hbSalesSignature.MainCreatedAt);
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, hbSalesSignature.DetailLastModifiedAt);
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, hbSalesSignature.DetailCreatedAt);
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, hbSalesSignature.Checksum);
        }
        SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, aliasVersion);
        // canonical 日销量签名并不含所有折扣判定字段；这些字段即使没有更新时间也必须失效快照。
        SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, discountSemanticVersion);
        // SupplierCode 为空时事实读取会回退到 POSM 商品供应商映射；该表的值修正没有可靠更新时间。
        SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, supplierMappingVersion);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendPosmSignature(IncrementalHash hash, Posm2025DailySnapshotSignature signature)
    {
        SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, signature.Date);
        foreach (var part in new[] { signature.Orders, signature.Details, signature.Payments, signature.SalesReturns })
        {
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, part.RowCount);
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, part.LastModifiedAt);
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, part.CreatedAt);
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, part.Checksum);
        }
    }

    private async Task<DaySourceSnapshot> CaptureAsync(DateTime day, CancellationToken token)
    {
        EnsureSqlServer(_catalogDb, "HBweb");
        EnsureSqlServer(_posmDb, "POSM");
        EnsureSqlServer(_hbSalesDb, "HBSales");
        var nextDay = day.AddDays(1);

        // 同一 SqlSugar client 的连接未必启用 MARS，来源读取按连接顺序执行以保持可用性。
        // 明细/折扣明细/支付与商品日统计同口径并入退款单（Status=3）；订单行与补充退货保持 1/4。
        var orders = await _posmDb.Queryable<SalesOrder>()
            .Where(order => order.Status != null && (order.Status == 1 || order.Status == 4)
                && order.OrderTime != null && order.OrderTime >= day && order.OrderTime < nextDay)
            .Select(order => new StoreStatisticOrderRow
            {
                OrderGuid = order.OrderGuid,
                BranchCode = order.BranchCode,
                DeviceCode = order.DeviceCode,
                OrderTime = order.OrderTime,
                Status = order.Status,
                LastUploadTime = order.LastUploadTime,
                CreatedAt = order.CreatedTime,
                UpdatedAt = order.UpdatedTime,
            }).ToListAsync(token);
        var details = await _posmDb.Queryable<SalesOrder>()
            .LeftJoin<SalesOrderDetail>((order, detail) => order.OrderGuid == detail.OrderGuid)
            .Where((order, detail) => order.Status != null
                && SalesOrderStatusRules.NetSalesStatuses.Contains(order.Status)
                && order.OrderTime != null && order.OrderTime >= day && order.OrderTime < nextDay)
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
            }).ToListAsync(token);
        var discountDetails = await _posmDb.Queryable<SalesOrder>()
            .InnerJoin<SalesOrderDetail>((order, detail) => order.OrderGuid == detail.OrderGuid)
            .Where((order, detail) => order.Status != null
                && SalesOrderStatusRules.NetSalesStatuses.Contains(order.Status)
                && order.OrderTime != null && order.OrderTime >= day && order.OrderTime < nextDay)
            .Select((order, detail) => new DiscountSemanticRow
            {
                Scope = "posm-detail", Key = detail.OrderDetailGuid, RelatedKey = order.OrderGuid,
                Value1 = detail.ProductCode, Value2 = detail.Quantity, Value3 = detail.ActualAmount,
                Value4 = detail.Price, Value5 = detail.Subtotal, Value6 = detail.DiscountAmount,
                Value7 = detail.DiscountRate,
            }).ToListAsync(token);
        token.ThrowIfCancellationRequested();
        var payments = await _posmDb.Queryable<PaymentDetail, SalesOrder>(
                (payment, order) => payment.OrderGuid == order.OrderGuid)
            .Where((payment, order) => order.Status != null
                && SalesOrderStatusRules.NetSalesStatuses.Contains(order.Status)
                && order.OrderTime != null && order.OrderTime >= day && order.OrderTime < nextDay)
            .Select((payment, order) => new StoreStatisticPaymentRow
            {
                PaymentGuid = payment.PaymentGuid,
                OrderGuid = payment.OrderGuid,
                Amount = payment.Amount ?? 0m,
                CreatedAt = payment.CreatedTime,
                UpdatedAt = payment.UpdatedTime,
                LastUploadTime = payment.LastUploadTime,
            }).ToListAsync(token);
        var detailGuids = details.Select(row => row.DetailGuid).Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var supplementalReturns = await LoadSupplementalReturnsAsync(day, nextDay, detailGuids, token);
        // 与 canonical 共用经过核验的 HBSales 历史窗口，不能把 2024 的真实来源错误视为零。
        var hbRows = SalesStatisticsHBSalesHistoryWindow.Includes(day) ? await LoadHBSalesRowsAsync(day, nextDay, token) : [];
        var returnDiscountRows = await LoadReturnDiscountSemanticRowsAsync(day, nextDay, detailGuids, token);
        var hbDiscountRows = SalesStatisticsHBSalesHistoryWindow.Includes(day)
            ? await LoadHBSalesDiscountSemanticRowsAsync(hbRows, token)
            : [];
        var aliasResult = await BuildScopeAndAliasVersionAsync(details, supplementalReturns, hbRows, orders, token);
        var supplierMappingVersion = await CreatePosmSupplierMappingVersionAsync(details, supplementalReturns, token);
        var posmSignature = SalesStatisticsProductStoreDailyDomainRules.CreatePosm2025DailySnapshotSignature(
            day, orders, details, payments, supplementalReturns);
        var hbSignature = SalesStatisticsHBSalesHistoryWindow.Includes(day)
            ? SalesStatisticsProductStoreDailyDomainRules.CreateHBSales2025DailySnapshotSignature(day, hbRows)
            : null;
        var discountSemanticVersion = CreateDiscountSemanticVersion(discountDetails, returnDiscountRows, hbDiscountRows);
        return new DaySourceSnapshot(posmSignature, hbSignature, aliasResult.AliasVersion, discountSemanticVersion,
            supplierMappingVersion,
            aliasResult.ProductCodes, aliasResult.StoreCodes, aliasResult.HBSalesAliases);
    }

    private async Task<List<ProductStoreDailySourceRow>> LoadSupplementalReturnsAsync(
        DateTime day, DateTime nextDay, HashSet<string> detailGuids, CancellationToken token)
    {
        var returnTable = _posmDb.EntityMaintenance.GetTableName(typeof(SalesReturnRecord));
        if (!_posmDb.DbMaintenance.GetTableInfoList(false)
            .Any(table => string.Equals(table.Name, returnTable, StringComparison.OrdinalIgnoreCase)))
            return [];
        var rows = await _posmDb.Queryable<SalesReturnRecord>()
            .LeftJoin<SalesOrder>((returnRow, order) => returnRow.ReturnOrderGuid == order.OrderGuid)
            .LeftJoin<SalesOrderDetail>((returnRow, order, detail) => returnRow.OriginalOrderDetailGuid == detail.OrderDetailGuid)
            .Where((returnRow, order, detail) => order.Status != null && (order.Status == 1 || order.Status == 4)
                && order.OrderTime != null && order.OrderTime >= day && order.OrderTime < nextDay)
            .Select((returnRow, order, detail) => new ReturnSourceRow
            {
                ReturnDetailGuid = returnRow.ReturnDetailGuid,
                ReturnProductCode = returnRow.ProductCode,
                ReturnQuantity = returnRow.ReturnQuantity,
                ReturnAmount = returnRow.ReturnAmount,
                ReturnCreatedTime = returnRow.CreatedTime,
                ReturnUpdatedTime = returnRow.UpdatedTime,
                OriginalOrderGuid = returnRow.OriginalOrderGuid,
                OrderGuid = order.OrderGuid,
                BranchCode = order.BranchCode,
                DeviceCode = order.DeviceCode,
                OrderTime = order.OrderTime,
                OrderLastUploadTime = order.LastUploadTime,
                DetailProductCode = detail.ProductCode,
                OriginalDetailOrderGuid = detail.OrderGuid,
                SupplierCode = detail.SupplierCode,
                ProductName = detail.ProductName,
                Barcode = detail.Barcode,
                DetailPrice = detail.Price,
                DetailSubtotal = detail.Subtotal,
                OriginalDetailQuantity = detail.Quantity,
            }).ToListAsync(token);
        token.ThrowIfCancellationRequested();
        return rows.Where(row => string.IsNullOrWhiteSpace(row.ReturnDetailGuid) || !detailGuids.Contains(row.ReturnDetailGuid))
            .Select(row =>
            {
                var originalMatches = string.Equals(row.OriginalOrderGuid?.Trim(), row.OriginalDetailOrderGuid?.Trim(), StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(row.ReturnProductCode) || string.IsNullOrWhiteSpace(row.DetailProductCode)
                        || string.Equals(row.ReturnProductCode.Trim(), row.DetailProductCode.Trim(), StringComparison.OrdinalIgnoreCase));
                return new ProductStoreDailySourceRow
                {
                    Date = row.OrderTime!.Value.Date,
                    OrderGuid = row.OrderGuid,
                    DetailGuid = row.ReturnDetailGuid,
                    BranchCode = row.BranchCode,
                    DeviceCode = row.DeviceCode,
                    OrderLastUploadTime = row.OrderLastUploadTime,
                    ProductCode = string.IsNullOrWhiteSpace(row.ReturnProductCode) ? row.DetailProductCode : row.ReturnProductCode,
                    SupplierCode = row.SupplierCode,
                    ProductName = row.ProductName,
                    Barcode = row.Barcode,
                    Price = originalMatches ? row.DetailPrice : null,
                    Subtotal = originalMatches ? row.DetailSubtotal : null,
                    OriginalUnitPrice = originalMatches ? row.DetailPrice : null,
                    OriginalSubtotal = originalMatches ? row.DetailSubtotal : null,
                    PriceLookupCode = row.Barcode,
                    OriginalSaleQuantity = row.OriginalDetailQuantity,
                    OriginalSaleCostEvidence = originalMatches,
                    Quantity = -Math.Abs(row.ReturnQuantity ?? 0m),
                    ActualAmount = -Math.Abs(row.ReturnAmount ?? 0m),
                    DetailLastUploadTime = row.ReturnUpdatedTime ?? row.ReturnCreatedTime,
                    SourceCreatedAt = row.ReturnCreatedTime,
                    SourceUpdatedAt = row.ReturnUpdatedTime,
                };
            }).ToList();
    }

    private async Task<List<DiscountSemanticRow>> LoadReturnDiscountSemanticRowsAsync(
        DateTime day, DateTime nextDay, HashSet<string> detailGuids, CancellationToken token)
    {
        var returnTable = _posmDb.EntityMaintenance.GetTableName(typeof(SalesReturnRecord));
        if (!_posmDb.DbMaintenance.GetTableInfoList(false)
            .Any(table => string.Equals(table.Name, returnTable, StringComparison.OrdinalIgnoreCase)))
            return [];
        var rows = await _posmDb.Queryable<SalesReturnRecord>()
            .LeftJoin<SalesOrder>((returnRow, order) => returnRow.ReturnOrderGuid == order.OrderGuid)
            .LeftJoin<SalesOrderDetail>((returnRow, order, detail) => returnRow.OriginalOrderDetailGuid == detail.OrderDetailGuid)
            .Where((returnRow, order, detail) => order.Status != null && (order.Status == 1 || order.Status == 4)
                && order.OrderTime != null && order.OrderTime >= day && order.OrderTime < nextDay)
            .Select((returnRow, order, detail) => new DiscountSemanticRow
            {
                Scope = "posm-return", Key = returnRow.ReturnDetailGuid, RelatedKey = returnRow.OriginalOrderDetailGuid,
                Value1 = returnRow.OriginalOrderGuid, Value2 = returnRow.ProductCode,
                Value3 = returnRow.ReturnQuantity, Value4 = returnRow.ReturnAmount,
                Value5 = detail.OrderGuid, Value6 = detail.ProductCode, Value7 = detail.Quantity,
                Value8 = detail.ActualAmount, Value9 = detail.Price, Value10 = detail.Subtotal,
                Value11 = detail.DiscountAmount, Value12 = detail.DiscountRate,
                // 补充退货按原单 SupplierCode 分组；它会影响 canonical 的供应商截断和金额 round，
                // 即使原单不在目标日且没有更新时间也必须进入日版本。
                SupplierCode = detail.SupplierCode,
            }).ToListAsync(token);
        token.ThrowIfCancellationRequested();
        return rows.Where(row => string.IsNullOrWhiteSpace(row.Key) || !detailGuids.Contains(row.Key)).ToList();
    }

    private async Task<List<ProductStoreDailySourceRow>> LoadHBSalesRowsAsync(DateTime day, DateTime nextDay, CancellationToken token)
    {
        // CaptureAsync 已要求 HBSales 为 SQL Server，这里总是按实际日期重编译，原因见 BuildHBSalesRowsSql。
        var sql = BuildHBSalesRowsSql(_hbSalesDb, day, nextDay);
        var rows = await _hbSalesDb.Ado.SqlQueryAsync<ProductStoreDailySourceRow>(sql.Key, sql.Value.ToArray(), token);
        token.ThrowIfCancellationRequested();
        foreach (var row in rows.Where(row => row.DocumentType?.Trim() is "3" or "4"))
        {
            row.Quantity = -row.Quantity;
            row.ActualAmount = -row.ActualAmount;
        }
        foreach (var row in rows)
            row.DetailGuid = row.HBSalesDetailId.ToString(CultureInfo.InvariantCulture);
        return rows;
    }

    /// <summary>
    /// 来源签名每天要巡检 HBSales 窗口内的全部历史日期（生产约 220 次/天）。结账日期是 date 列，
    /// SqlSugar 却把日期变量下发为 datetime 参数，带参缓存的计划用不上 IX_B销售清单详情表副本_折扣日日期单号
    /// 覆盖索引，每次聚集扫描明细表约 40 万页、平均 12.9 秒：2026-09-21 生产原句 sp_executesql 复现 13.8 秒，
    /// 加 OPTION (RECOMPILE) 后 CPU 约 0.15 秒、明细表逻辑读 418 次，结果逐行一致。
    /// </summary>
    internal static KeyValuePair<string, List<SugarParameter>> BuildHBSalesRowsSql(
        ISqlSugarClient db, DateTime day, DateTime nextDay)
    {
        var sql = db.Queryable<SalesOrderMain>()
            .LeftJoin<SalesOrderDetailRecord>((main, detail) => main.B销售单号 == detail.B销售单号)
            .Where((main, detail) => detail.B结账日期.HasValue && detail.B结账日期.Value >= day && detail.B结账日期.Value < nextDay
                && main.B结账日期.HasValue && main.B结账日期.Value >= day.AddDays(-7) && main.B结账日期.Value < nextDay.AddDays(7)
                && (main.B单据类型 == null || main.B单据类型.Trim() != "2"))
            .Select((main, detail) => new ProductStoreDailySourceRow
            {
                IsHBSalesSource = true,
                Date = detail.B结账日期!.Value.Date,
                // 此读取器不以 HBSales 的显示键参与聚合；直接保留源单号，避免 SQL Server 的 + 把字符串前缀强制转为整数。
                OrderGuid = main.B销售单号,
                HBSalesOrderNumber = main.B销售单号,
                // 不能在 SQL 投影中调用 ToString：SqlSugar 会生成 CAST(... AS nvarchar(max))，
                // 大量行流式传输会显著拖慢来源围栏。保留数值 ID，物化后再生成相同的十进制签名键。
                HBSalesDetailId = detail.ID,
                BranchCode = detail.B分店代码,
                ProductCode = detail.B产品编号,
                ItemNumber = detail.B货号,
                SupplierCode = detail.B供应商ID,
                ProductName = detail.B商品名,
                Barcode = detail.B条形码,
                HBSalesUnitPrice = detail.B单价,
                HBSalesOriginalAmount = detail.B原价合计金额,
                HBSalesSaleAmount = detail.B合计金额,
                HBSalesDiscountRate = detail.B折扣率,
                OriginalUnitPrice = detail.B单价,
                OriginalSubtotal = detail.B原价合计金额,
                PricingUnit = detail.B单位,
                PriceLookupCode = detail.B条形码,
                OriginalSaleQuantity = detail.B数量,
                OriginalHBSalesOrderNumber = main.B原销售单号,
                HBSalesReturnCode = detail.B退货码,
                Quantity = detail.B数量 ?? 0m,
                ActualAmount = detail.B合计金额 ?? 0m,
                HBSalesMainLastModifiedAt = main.FGC_LastModifyDate,
                HBSalesMainCreatedAt = main.FGC_CreateDate,
                HBSalesDetailLastModifiedAt = detail.FGC_LastModifyDate,
                HBSalesDetailCreatedAt = detail.FGC_CreateDate,
                OrderLastUploadTime = main.FGC_LastModifyDate ?? main.FGC_CreateDate,
                DetailLastUploadTime = detail.FGC_LastModifyDate ?? detail.FGC_CreateDate,
                DocumentType = main.B单据类型,
            }).ToSql();
        return new KeyValuePair<string, List<SugarParameter>>(sql.Key + " OPTION (RECOMPILE)", sql.Value);
    }

    private async Task<List<DiscountSemanticRow>> LoadHBSalesDiscountSemanticRowsAsync(
        IReadOnlyList<ProductStoreDailySourceRow> dayRows, CancellationToken token)
    {
        // LoadHBSalesRowsAsync 已按相同 join/filter 固定当天行；直接复用，避免每个 Capture 重扫一次 HBSales 日明细。
        // 退货行的 Quantity/ActualAmount 已归一化，因此签名必须使用保留的原始字段，与原查询逐字段等价。
        var rows = dayRows.Select(row => new DiscountSemanticRow
            {
                Scope = "hbs-detail", Key = row.DetailGuid, RelatedKey = row.HBSalesOrderNumber,
                Value1 = row.DocumentType, Value2 = row.OriginalHBSalesOrderNumber, Value3 = row.ProductCode,
                Value4 = row.HBSalesReturnCode, Value5 = row.Barcode, Value6 = row.OriginalSaleQuantity,
                Value7 = row.HBSalesSaleAmount, Value8 = row.HBSalesUnitPrice, Value9 = row.HBSalesOriginalAmount,
                Value10 = row.HBSalesDiscountRate,
            }).ToList();
        var originalOrderNumbers = dayRows.Where(row => row.DocumentType?.Trim() is "3" or "4")
            .Select(row => row.OriginalHBSalesOrderNumber).Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (originalOrderNumbers.Count == 0)
            return rows;
        foreach (var batch in originalOrderNumbers.Chunk(500))
        {
            var values = batch.ToList();
            var originals = await _hbSalesDb.Queryable<SalesOrderDetailRecord>()
                // FactReader 的原单证据以 LTRIM/RTRIM 关联；签名查询也必须规范化，不能漏掉源值带空格的原单。
                .Where(detail => detail.B销售单号 != null && values.Contains(detail.B销售单号.Trim()))
                .Select(detail => new DiscountSemanticRow
                {
                    Scope = "hbs-return-evidence", Key = detail.ID.ToString(), RelatedKey = detail.B销售单号,
                    Value1 = detail.B产品编号, Value2 = detail.B退货码, Value3 = detail.B条形码,
                    Value4 = detail.B数量, Value5 = detail.B合计金额, Value6 = detail.B单价,
                    Value7 = detail.B原价合计金额, Value8 = detail.B折扣率,
                }).ToListAsync(token);
            rows.AddRange(originals);
        }
        token.ThrowIfCancellationRequested();
        return rows;
    }

    private static string CreateDiscountSemanticVersion(params IReadOnlyList<DiscountSemanticRow>[] collections)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, "batch-product-sales-discount-semantic-v1");
        foreach (var row in collections.SelectMany(rows => rows)
                     .OrderBy(row => row.Scope, StringComparer.Ordinal).ThenBy(row => row.Key, StringComparer.Ordinal)
                     .ThenBy(row => row.RelatedKey, StringComparer.Ordinal))
        {
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, row.Scope);
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, row.Key);
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, row.RelatedKey);
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, row.Value1);
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, row.Value2);
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, row.Value3);
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, row.Value4);
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, row.Value5);
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, row.Value6);
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, row.Value7);
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, row.Value8);
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, row.Value9);
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, row.Value10);
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, row.Value11);
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, row.Value12);
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, row.SupplierCode);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>
    /// 事实 SQL 对缺失 SupplierCode 的 POSM 行回退到本表。映射没有可用于水位的更新时间，
    /// 因而签名必须同时保留当天实际关联的产品集合和每个映射值：无映射产品后来插入映射时也会失效。
    /// </summary>
    private async Task<string> CreatePosmSupplierMappingVersionAsync(
        IReadOnlyList<ProductStoreDailySourceRow> details,
        IReadOnlyList<ProductStoreDailySourceRow> supplementalReturns,
        CancellationToken token)
    {
        var productCodes = details.Concat(supplementalReturns)
            .Select(row => row.ProductCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var mappings = new List<SupplierMappingSignatureRow>();
        foreach (var batch in productCodes.Chunk(500))
        {
            var values = batch.ToList();
            var rows = await _posmDb.Queryable<PosmProductSupplierMapping>()
                // FactReader 用已规范化的明细编码与映射表原键关联。不能在映射键上套 TRIM：
                // 这会使 SQL Server 放弃 ProductCode 主键，并把前导空格的无效旧映射误纳入来源版本。
                .Where(mapping => mapping.ProductCode != null && values.Contains(mapping.ProductCode))
                .Select(mapping => new SupplierMappingSignatureRow
                {
                    ProductCode = mapping.ProductCode,
                    LocalSupplierCode = mapping.LocalSupplierCode,
                })
                .ToListAsync(token);
            mappings.AddRange(rows);
        }
        token.ThrowIfCancellationRequested();

        // ProductCode 是主键，正常每个规范化编码最多一个映射。仍按 SQL Server 的尾部空格比较语义
        // 分组，既保留异常数据时的确定性签名，也让下面每个商品 O(1) 取候选而非 O(N×M) 扫描。
        var mappingsByProductCode = mappings
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.ProductCode))
            .GroupBy(mapping => mapping.ProductCode!.TrimEnd(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(mapping => mapping.ProductCode, StringComparer.Ordinal)
                    .ThenBy(mapping => mapping.LocalSupplierCode, StringComparer.Ordinal)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, "posm-local-supplier-mapping-v2");
        foreach (var productCode in productCodes)
        {
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, productCode);
            var candidates = mappingsByProductCode.TryGetValue(productCode, out var matchedMappings)
                ? matchedMappings
                : [];
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, candidates.Count);
            foreach (var mapping in candidates)
            {
                SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, mapping.ProductCode);
                SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(hash, mapping.LocalSupplierCode);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private async Task<ScopeResult> BuildScopeAndAliasVersionAsync(
        IReadOnlyList<ProductStoreDailySourceRow> details,
        IReadOnlyList<ProductStoreDailySourceRow> supplementalReturns,
        IReadOnlyList<ProductStoreDailySourceRow> hbRows,
        IReadOnlyList<StoreStatisticOrderRow> orders,
        CancellationToken token)
    {
        var products = details.Concat(supplementalReturns).Concat(hbRows)
            .Select(row => row.ProductCode).Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var aliases = hbRows.Where(row => string.IsNullOrWhiteSpace(row.ProductCode))
            .SelectMany(row => new[] { row.ItemNumber, row.Barcode }).Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var aliasPairs = new List<string>();
        var productAliasCandidates = new List<CatalogAliasProduct>();
        var setAliasCandidates = new List<CatalogAlias>();
        var multiAliasCandidates = new List<CatalogStoreAlias>();
        foreach (var batch in aliases.Chunk(500))
        {
            var values = batch.ToList();
            var productRows = await _catalogDb.Queryable<Product>()
                .Where(product => product.IsDeleted == false && product.ProductCode != null
                    && ((product.ItemNumber != null && values.Contains(product.ItemNumber))
                        || (product.Barcode != null && values.Contains(product.Barcode))))
                .Select(product => new { product.ProductCode, product.ItemNumber, product.Barcode }).ToListAsync(token);
            foreach (var row in productRows)
            {
                products.Add(row.ProductCode!.Trim());
                aliasPairs.Add($"product|{row.ItemNumber}|{row.Barcode}|{row.ProductCode}");
                productAliasCandidates.Add(new CatalogAliasProduct(row.ProductCode, row.ItemNumber, row.Barcode));
            }
            var setRows = await _catalogDb.Queryable<ProductSetCode>()
                .Where(row => row.IsDeleted == false && row.ProductCode != null && row.SetBarcode != null && values.Contains(row.SetBarcode))
                .Select(row => new { row.SetBarcode, row.ProductCode }).ToListAsync(token);
            foreach (var row in setRows)
            {
                products.Add(row.ProductCode!.Trim());
                aliasPairs.Add($"set|{row.SetBarcode}|{row.ProductCode}");
                setAliasCandidates.Add(new CatalogAlias(row.SetBarcode, row.ProductCode));
            }
            var multiRows = await _catalogDb.Queryable<StoreMultiCodeProduct>()
                .Where(row => row.IsDeleted == false && row.ProductCode != null && row.MultiBarcode != null && values.Contains(row.MultiBarcode))
                .Select(row => new { row.StoreCode, row.MultiBarcode, row.ProductCode }).ToListAsync(token);
            foreach (var row in multiRows)
            {
                products.Add(row.ProductCode!.Trim());
                aliasPairs.Add($"multi|{row.StoreCode}|{row.MultiBarcode}|{row.ProductCode}");
                multiAliasCandidates.Add(new CatalogStoreAlias(row.MultiBarcode, row.StoreCode, row.ProductCode));
            }
        }
        var stores = orders.Concat(details.Select(row => new StoreStatisticOrderRow { BranchCode = row.BranchCode, DeviceCode = row.DeviceCode }))
            .Select(row => row.BranchCode).Where(code => !string.IsNullOrWhiteSpace(code)).Select(code => code!.Trim())
            .Concat(hbRows.Select(row => row.BranchCode).Where(code => !string.IsNullOrWhiteSpace(code)).Select(code => code!.Trim()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var devices = orders.Where(row => string.IsNullOrWhiteSpace(row.BranchCode)).Select(row => row.DeviceCode)
            .Concat(details.Where(row => string.IsNullOrWhiteSpace(row.BranchCode)).Select(row => row.DeviceCode))
            .Where(code => !string.IsNullOrWhiteSpace(code)).Select(code => code!.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (devices.Count > 0)
        {
            var deviceRows = await _posmDb.Queryable<POSM_设备注册信息表>()
                .Where(row => row.系统设备编号 != null && devices.Contains(SqlFunc.ToUpper(row.系统设备编号.Trim())))
                .OrderBy(row => row.ID)
                .Select(row => new { row.系统设备编号, row.分店代码 }).ToListAsync(token);
            // 与 ProductStoreDaily 的 first non-empty 设备映射保持同一 C# 分组语义。
            foreach (var mapped in deviceRows.Where(row => !string.IsNullOrWhiteSpace(row.系统设备编号))
                         .GroupBy(row => row.系统设备编号.Trim(), StringComparer.OrdinalIgnoreCase)
                         .Select(group => new
                         {
                             DeviceCode = group.Key,
                             BranchCode = group.Select(row => row.分店代码)
                                 .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code))?.Trim()
                         })
                         .Where(row => !string.IsNullOrWhiteSpace(row.BranchCode)))
            {
                stores.Add(mapped.BranchCode!);
                // 设备归属是空 BranchCode 的真实分店维度，和别名一样属于解析规则输入。
                aliasPairs.Add($"device|{mapped.DeviceCode}|{mapped.BranchCode}");
            }
        }
        token.ThrowIfCancellationRequested();
        // Capture 已只读取“当天实际缺产品码”的 alias 及其全量歧义候选。先建字典而不是让
        // 每个 alias 反复扫描所有候选；2025 大销售日的别名数量和候选量都可能很大。
        // 结果仍按旧解析器相同的 global → branch → cross 优先级固定为 SQL 可消费的行。
        var targetProducts = products.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var globalCandidatesByAlias = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var crossCandidatesByAlias = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var branchCandidatesByAlias = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
        static void AddCandidate(Dictionary<string, HashSet<string>> candidates, string? alias, string? product)
        {
            if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(product)) return;
            var key = alias.Trim();
            if (!candidates.TryGetValue(key, out var values)) candidates[key] = values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            values.Add(product.Trim());
        }
        foreach (var row in productAliasCandidates)
        {
            AddCandidate(globalCandidatesByAlias, row.ItemNumber, row.ProductCode);
            AddCandidate(globalCandidatesByAlias, row.Barcode, row.ProductCode);
        }
        foreach (var row in setAliasCandidates)
            AddCandidate(globalCandidatesByAlias, row.Alias, row.ProductCode);
        foreach (var row in multiAliasCandidates)
        {
            AddCandidate(crossCandidatesByAlias, row.Alias, row.ProductCode);
            if (string.IsNullOrWhiteSpace(row.Alias) || string.IsNullOrWhiteSpace(row.BranchCode) || string.IsNullOrWhiteSpace(row.ProductCode))
                continue;
            var alias = row.Alias.Trim();
            if (!branchCandidatesByAlias.TryGetValue(alias, out var branchCandidates))
                branchCandidatesByAlias[alias] = branchCandidates = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            AddCandidate(branchCandidates, row.BranchCode, row.ProductCode);
        }
        var hbsAliases = new List<BatchProductSalesHBSalesAlias>();
        foreach (var value in aliases)
        {
            globalCandidatesByAlias.TryGetValue(value, out var globalCandidates);
            globalCandidates ??= [];
            if (globalCandidates.Count == 1 && targetProducts.Contains(globalCandidates.Single()))
                hbsAliases.Add(new BatchProductSalesHBSalesAlias(value, null, globalCandidates.Single(), "global"));
            else if (globalCandidates.Count > 0)
                hbsAliases.Add(new BatchProductSalesHBSalesAlias(value, null, string.Empty, "global"));

            foreach (var branch in stores)
            {
                var branchCandidates = branchCandidatesByAlias.TryGetValue(value, out var candidatesByBranch)
                    && candidatesByBranch.TryGetValue(branch, out var candidates)
                    ? candidates
                    : [];
                if (branchCandidates.Count == 1 && targetProducts.Contains(branchCandidates.Single()))
                    hbsAliases.Add(new BatchProductSalesHBSalesAlias(value, branch, branchCandidates.Single(), "branch"));
                else if (branchCandidates.Count > 0)
                    hbsAliases.Add(new BatchProductSalesHBSalesAlias(value, branch, string.Empty, "branch"));
            }
            crossCandidatesByAlias.TryGetValue(value, out var crossCandidates);
            crossCandidates ??= [];
            if (globalCandidates.Count == 0 && crossCandidates.Count == 1 && targetProducts.Contains(crossCandidates.Single()))
                hbsAliases.Add(new BatchProductSalesHBSalesAlias(value, null, crossCandidates.Single(), "cross"));
        }
        using var aliasHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(aliasHash, "hbsales-alias-resolution-v1");
        foreach (var pair in aliasPairs.OrderBy(value => value, StringComparer.Ordinal))
            SalesStatisticsProductStoreDailyDomainRules.AppendSignatureValue(aliasHash, pair);
        return new ScopeResult(products.OrderBy(code => code, StringComparer.OrdinalIgnoreCase).ToList(),
            stores.OrderBy(code => code, StringComparer.OrdinalIgnoreCase).ToList(),
            Convert.ToHexString(aliasHash.GetHashAndReset()), hbsAliases);
    }

    private static void EnsureSqlServer(ISqlSugarClient db, string name)
    {
        if (db.CurrentConnectionConfig.DbType != DbType.SqlServer)
            throw new NotSupportedException($"批量折扣日快照要求 {name} 使用 SQL Server 来源库。");
    }

    internal sealed record DaySourceSnapshot(
        Posm2025DailySnapshotSignature PosmSignature,
        HBSales2025DailySnapshotSignature? HBSalesSignature,
        string AliasVersion,
        string DiscountSemanticVersion,
        string SupplierMappingVersion,
        List<string> ProductCodes,
        List<string> StoreCodes,
        List<BatchProductSalesHBSalesAlias> HBSalesAliases);

    internal sealed class PreparedDay
    {
        internal DateTime Day { get; }
        internal string SourceVersion { get; }
        internal DaySourceSnapshot Snapshot { get; }

        internal PreparedDay(DateTime day, DaySourceSnapshot snapshot, string sourceVersion)
        {
            Day = day.Date;
            Snapshot = snapshot;
            SourceVersion = sourceVersion;
        }
    }

    internal sealed record PreparedDayReadResult(
        List<BatchProductSalesAggregateRow> Rows,
        string SourceVersion);

    private sealed record ScopeResult(List<string> ProductCodes, List<string> StoreCodes, string AliasVersion,
        List<BatchProductSalesHBSalesAlias> HBSalesAliases);

    private sealed record CatalogAliasProduct(string? ProductCode, string? ItemNumber, string? Barcode);
    private sealed record CatalogAlias(string? Alias, string? ProductCode);
    private sealed record CatalogStoreAlias(string? Alias, string? BranchCode, string? ProductCode);
    private sealed class SupplierMappingSignatureRow
    {
        public string? ProductCode { get; init; }
        public string? LocalSupplierCode { get; init; }
    }

    private sealed class ReturnSourceRow
    {
        public string? ReturnDetailGuid { get; init; }
        public string? ReturnProductCode { get; init; }
        public decimal? ReturnQuantity { get; init; }
        public decimal? ReturnAmount { get; init; }
        public DateTime? ReturnCreatedTime { get; init; }
        public DateTime? ReturnUpdatedTime { get; init; }
        public string? OriginalOrderGuid { get; init; }
        public string? OrderGuid { get; init; }
        public string? BranchCode { get; init; }
        public string? DeviceCode { get; init; }
        public DateTime? OrderTime { get; init; }
        public DateTime? OrderLastUploadTime { get; init; }
        public string? DetailProductCode { get; init; }
        public string? OriginalDetailOrderGuid { get; init; }
        public string? SupplierCode { get; init; }
        public string? ProductName { get; init; }
        public string? Barcode { get; init; }
        public decimal? DetailPrice { get; init; }
        public decimal? DetailSubtotal { get; init; }
        public decimal? OriginalDetailQuantity { get; init; }
    }

    private sealed class DiscountSemanticRow
    {
        public string Scope { get; init; } = string.Empty;
        public string? Key { get; init; }
        public string? RelatedKey { get; init; }
        public object? Value1 { get; init; }
        public object? Value2 { get; init; }
        public object? Value3 { get; init; }
        public object? Value4 { get; init; }
        public object? Value5 { get; init; }
        public object? Value6 { get; init; }
        public object? Value7 { get; init; }
        public object? Value8 { get; init; }
        public object? Value9 { get; init; }
        public object? Value10 { get; init; }
        public object? Value11 { get; init; }
        public object? Value12 { get; init; }
        public string? SupplierCode { get; init; }
    }
}
