using BlazorApp.Api.Data;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBSalesRecord;
using BlazorApp.Shared.Models.POSM;

namespace BlazorApp.Api.Services;

/// <summary>商品分店日统计共享的来源查询；不依赖任何业务切片。</summary>
internal static class SalesStatisticsProductStoreDailySourceQueries
{
    private const int CommandTimeoutSeconds = 1800;
    private const int HBSalesMainCheckoutDateWindowDays = 7;

    internal static async Task<List<ProductStoreDailySourceRow>> LoadSupplementalReturnRowsAsync(
        POSMSqlSugarContext posmContext,
        DateTime targetDate,
        DateTime nextDate,
        HashSet<string> detailGuidSet)
    {
        var returnTableName = posmContext.Db.EntityMaintenance.GetTableName(typeof(SalesReturnRecord));
        var hasReturnTable = posmContext.Db.DbMaintenance.GetTableInfoList(false)
            .Any(table => string.Equals(
                table.Name,
                returnTableName,
                StringComparison.OrdinalIgnoreCase
            ));
        if (!hasReturnTable)
            return [];

        var returnRows = await posmContext.Db.Queryable<SalesReturnRecord>()
            .LeftJoin<SalesOrder>((returnRow, order) =>
                returnRow.ReturnOrderGuid == order.OrderGuid)
            .LeftJoin<SalesOrderDetail>((returnRow, order, detail) =>
                returnRow.OriginalOrderDetailGuid == detail.OrderDetailGuid)
            .Where((returnRow, order, detail) =>
                order.Status != null
                && (order.Status == 1 || order.Status == 4)
                && order.OrderTime != null
                && order.OrderTime >= targetDate
                && order.OrderTime < nextDate
            )
            .Select((returnRow, order, detail) => new
            {
                returnRow.ReturnDetailGuid,
                ReturnProductCode = returnRow.ProductCode,
                ReturnQuantity = returnRow.ReturnQuantity,
                ReturnAmount = returnRow.ReturnAmount,
                ReturnCreatedTime = returnRow.CreatedTime,
                ReturnUpdatedTime = returnRow.UpdatedTime,
                OriginalOrderGuid = returnRow.OriginalOrderGuid,
                order.OrderGuid,
                order.BranchCode,
                order.DeviceCode,
                order.OrderTime,
                OrderLastUploadTime = order.LastUploadTime,
                DetailProductCode = detail.ProductCode,
                OriginalDetailOrderGuid = detail.OrderGuid,
                detail.SupplierCode,
                detail.ProductName,
                detail.Barcode,
                DetailPrice = detail.Price,
                DetailSubtotal = detail.Subtotal,
                OriginalDetailQuantity = detail.Quantity,
            })
            .ToListAsync();

        return returnRows
            .Where(row =>
                string.IsNullOrWhiteSpace(row.ReturnDetailGuid)
                || !detailGuidSet.Contains(row.ReturnDetailGuid)
            )
            .Select(row =>
            {
                var originalIdentityMatches = string.Equals(
                        row.OriginalOrderGuid?.Trim(),
                        row.OriginalDetailOrderGuid?.Trim(),
                        StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(row.ReturnProductCode)
                        || string.IsNullOrWhiteSpace(row.DetailProductCode)
                        || string.Equals(
                            row.ReturnProductCode.Trim(),
                            row.DetailProductCode.Trim(),
                            StringComparison.OrdinalIgnoreCase));
                return new ProductStoreDailySourceRow
                {
                Date = row.OrderTime!.Value.Date,
                OrderGuid = row.OrderGuid,
                DetailGuid = row.ReturnDetailGuid,
                BranchCode = row.BranchCode,
                DeviceCode = row.DeviceCode,
                OrderLastUploadTime = row.OrderLastUploadTime,
                ProductCode = string.IsNullOrWhiteSpace(row.ReturnProductCode)
                    ? row.DetailProductCode
                    : row.ReturnProductCode,
                SupplierCode = row.SupplierCode,
                ProductName = row.ProductName,
                Barcode = row.Barcode,
                Price = originalIdentityMatches ? row.DetailPrice : null,
                Subtotal = originalIdentityMatches ? row.DetailSubtotal : null,
                OriginalUnitPrice = originalIdentityMatches ? row.DetailPrice : null,
                OriginalSubtotal = originalIdentityMatches ? row.DetailSubtotal : null,
                PriceLookupCode = row.Barcode,
                OriginalSaleQuantity = row.OriginalDetailQuantity,
                OriginalSaleCostEvidence = originalIdentityMatches,
                Quantity = -Math.Abs(row.ReturnQuantity ?? 0m),
                ActualAmount = -Math.Abs(row.ReturnAmount ?? 0m),
                DetailLastUploadTime = row.ReturnUpdatedTime ?? row.ReturnCreatedTime,
                SourceCreatedAt = row.ReturnCreatedTime,
                SourceUpdatedAt = row.ReturnUpdatedTime,
                };
            })
            .ToList();
    }

    internal static async Task<Dictionary<string, string>> LoadDeviceBranchMapAsync(
        POSMSqlSugarContext posmContext,
        IEnumerable<string?> deviceCodes)
    {
        var targetDeviceCodes = deviceCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targetDeviceCodes.Count == 0)
            return [];

        return (await posmContext.Db.Queryable<POSM_设备注册信息表>()
                .Where(device => device.系统设备编号 != null
                    && targetDeviceCodes.Contains(SqlFunc.ToUpper(device.系统设备编号.Trim())))
                .Select(device => new { device.系统设备编号, device.分店代码 })
                .ToListAsync())
            .Where(device => !string.IsNullOrWhiteSpace(device.系统设备编号))
            .GroupBy(device => device.系统设备编号.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(device => device.分店代码)
                    .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code))?.Trim()
                    ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
    }

    internal static async Task<(
        Dictionary<string, decimal> PaymentAmounts,
        Dictionary<string, decimal> DetailAmounts
    )> LoadOrderAmountMapsAsync<T>(
        POSMSqlSugarContext posmContext,
        DateTime startDate,
        DateTime endExclusive,
        IEnumerable<T> detailRows,
        Func<T, string?> orderGuidSelector,
        Func<T, decimal> detailAmountSelector)
    {
        // 支付金额映射与明细来源同口径（含退款单），否则退款行会被分摊成 0 金额、负数量。
        var paymentRows = await posmContext.Db.Queryable<PaymentDetail, SalesOrder>(
                (payment, order) => payment.OrderGuid == order.OrderGuid
            )
            .Where((payment, order) =>
                order.Status != null
                && SalesOrderStatusRules.NetSalesStatuses.Contains(order.Status)
                && order.OrderTime != null
                && order.OrderTime >= startDate
                && order.OrderTime < endExclusive
            )
            .GroupBy((payment, order) => payment.OrderGuid)
            .Select((payment, order) => new OrderAmountRow
            {
                OrderGuid = payment.OrderGuid,
                Amount = SqlFunc.AggregateSum(payment.Amount) ?? 0m,
            })
            .ToListAsync();

        var detailAmountRows = detailRows.Select(row => new OrderAmountRow
        {
            OrderGuid = orderGuidSelector(row),
            Amount = detailAmountSelector(row),
        });

        return (
            SalesStatisticsProductStoreDailyDomainRules.BuildOrderAmountMap(paymentRows),
            SalesStatisticsProductStoreDailyDomainRules.BuildOrderAmountMap(detailAmountRows)
        );
    }

    /// <summary>
    /// 主表与明细表的结账日期都是 date 列。SqlSugar 表达式默认把 DateTime 变量作为 datetime 参数下发，
    /// SQL Server 会对整列做隐式转换而放弃索引查找：生产实测同一天查询 13 秒，改为 date 参数后 0.7 秒。
    /// 因此结账日期范围条件改用显式 <see cref="System.Data.DbType.Date"/> 参数；别名 main/detail 与表达式中的 lambda 参数名一致。
    /// </summary>
    private const string HBSalesCheckoutDateRangeWhere =
        "[detail].[B结账日期] IS NOT NULL"
        + " AND [detail].[B结账日期] >= @hbDetailStart AND [detail].[B结账日期] < @hbDetailEnd"
        + " AND [main].[B结账日期] IS NOT NULL"
        + " AND [main].[B结账日期] >= @hbMainStart AND [main].[B结账日期] < @hbMainEnd";

    private static SugarParameter[] BuildHBSalesCheckoutDateParameters(DateTime targetDate, DateTime nextDate)
    {
        var mainCheckoutDateWindowStart = targetDate.Date.AddDays(-HBSalesMainCheckoutDateWindowDays);
        var mainCheckoutDateWindowEnd = nextDate.Date.AddDays(HBSalesMainCheckoutDateWindowDays);
        return new[]
        {
            new SugarParameter("@hbDetailStart", targetDate.Date, System.Data.DbType.Date),
            new SugarParameter("@hbDetailEnd", nextDate.Date, System.Data.DbType.Date),
            new SugarParameter("@hbMainStart", mainCheckoutDateWindowStart, System.Data.DbType.Date),
            new SugarParameter("@hbMainEnd", mainCheckoutDateWindowEnd, System.Data.DbType.Date),
        };
    }

    internal static async Task<List<HBSalesStoreAggregateRow>> LoadHBSalesStoreAggregatesAsync(
        HBSalesRecordSqlSugarContext hbSalesContext,
        DateTime targetDate,
        DateTime nextDate)
    {
        var originalCommandTimeout = hbSalesContext.Db.Ado.CommandTimeOut;
        hbSalesContext.Db.Ado.CommandTimeOut = Math.Max(
            originalCommandTimeout,
            CommandTimeoutSeconds
        );
        try
        {
            return await hbSalesContext.Db.Queryable<SalesOrderMain>()
                .LeftJoin<SalesOrderDetailRecord>((main, detail) =>
                    main.B销售单号 == detail.B销售单号)
                .Where(
                    HBSalesCheckoutDateRangeWhere,
                    BuildHBSalesCheckoutDateParameters(targetDate, nextDate)
                )
                .Where((main, detail) =>
                    (main.B单据类型 == null || main.B单据类型.Trim() != "2")
                    && detail.B分店代码 != null
                    && detail.B分店代码.Trim() != ""
                )
                .GroupBy((main, detail) => detail.B分店代码!.Trim())
                .Select((main, detail) => new HBSalesStoreAggregateRow
                {
                    BranchCode = detail.B分店代码!.Trim(),
                    TotalAmount = SqlFunc.AggregateSum(
                        (detail.B合计金额 ?? 0m) * SqlFunc.IIF(
                            main.B单据类型 != null
                                && (main.B单据类型.Trim() == "3"
                                    || main.B单据类型.Trim() == "4"),
                            -1m,
                            1m
                        )
                    ),
                    TotalQuantity = SqlFunc.AggregateSum(
                        (detail.B数量 ?? 0m) * SqlFunc.IIF(
                            main.B单据类型 != null
                                && (main.B单据类型.Trim() == "3"
                                    || main.B单据类型.Trim() == "4"),
                            -1m,
                            1m
                        )
                    ),
                    OrderCount = SqlFunc.AggregateDistinctCount(main.B销售单号),
                })
                .ToListAsync();
        }
        finally
        {
            hbSalesContext.Db.Ado.CommandTimeOut = originalCommandTimeout;
        }
    }

    /// <summary>
    /// HBSales 历史窗口内的分时来源：按分店与结账小时聚合金额、销量与去重单数。
    /// 过滤规则与 <see cref="LoadHBSalesStoreAggregatesAsync"/> 完全一致，保证分时之和等于分店日统计。
    /// 结账时间是 time 类型，跨数据库无法在 SQL 中稳定取小时，因此先按销售单拉取，再在内存中分桶。
    /// </summary>
    internal static async Task<List<HBSalesHourlyAggregateRow>> LoadHBSalesHourlyAggregatesAsync(
        HBSalesRecordSqlSugarContext hbSalesContext,
        DateTime targetDate,
        DateTime nextDate)
    {
        var originalCommandTimeout = hbSalesContext.Db.Ado.CommandTimeOut;
        hbSalesContext.Db.Ado.CommandTimeOut = Math.Max(
            originalCommandTimeout,
            CommandTimeoutSeconds
        );
        List<HBSalesHourlySourceRow> orderRows;
        try
        {
            orderRows = await hbSalesContext.Db.Queryable<SalesOrderMain>()
                .LeftJoin<SalesOrderDetailRecord>((main, detail) =>
                    main.B销售单号 == detail.B销售单号)
                .Where(
                    HBSalesCheckoutDateRangeWhere,
                    BuildHBSalesCheckoutDateParameters(targetDate, nextDate)
                )
                .Where((main, detail) =>
                    (main.B单据类型 == null || main.B单据类型.Trim() != "2")
                    && detail.B分店代码 != null
                    && detail.B分店代码.Trim() != ""
                )
                .GroupBy((main, detail) => new
                {
                    BranchCode = detail.B分店代码!.Trim(),
                    main.B销售单号,
                    main.B单据类型,
                    DetailCheckoutTime = detail.B结账时间,
                    MainCheckoutTime = main.B结账时间,
                })
                .Select((main, detail) => new HBSalesHourlySourceRow
                {
                    BranchCode = detail.B分店代码!.Trim(),
                    SalesOrderNo = main.B销售单号,
                    DocumentType = main.B单据类型,
                    DetailCheckoutTime = detail.B结账时间,
                    MainCheckoutTime = main.B结账时间,
                    TotalAmount = SqlFunc.AggregateSum(detail.B合计金额 ?? 0m),
                    TotalQuantity = SqlFunc.AggregateSum(detail.B数量 ?? 0m),
                })
                .ToListAsync();
        }
        finally
        {
            hbSalesContext.Db.Ado.CommandTimeOut = originalCommandTimeout;
        }

        return BuildHBSalesHourlyAggregates(orderRows);
    }

    /// <summary>
    /// 把按销售单拉取的 HBSales 行折算为分店小时聚合：退货/退款单（单据类型 3、4）取反，
    /// 小时取该单最早的结账时间，缺少结账时间的旧单沿用历史导入口径落到 0 点。
    /// </summary>
    internal static List<HBSalesHourlyAggregateRow> BuildHBSalesHourlyAggregates(
        IEnumerable<HBSalesHourlySourceRow> rows)
    {
        var orders = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.BranchCode))
            .GroupBy(row => (
                BranchCode: row.BranchCode!.Trim(),
                SalesOrderNo: row.SalesOrderNo?.Trim() ?? string.Empty
            ))
            .Select(group =>
            {
                // 同一销售单的明细可能落在不同秒，先按销售单归并，才能保证单数按小时去重。
                var checkoutTime = group
                    .Select(row => row.DetailCheckoutTime ?? row.MainCheckoutTime)
                    .Where(time => time.HasValue)
                    .OrderBy(time => time!.Value)
                    .FirstOrDefault();
                return new
                {
                    group.Key.BranchCode,
                    HasOrderNo = group.Key.SalesOrderNo.Length > 0,
                    Hour = checkoutTime.HasValue ? checkoutTime.Value.Hours : 0,
                    TotalAmount = group.Sum(row =>
                        ApplyHBSalesDocumentTypeSign(row.DocumentType, row.TotalAmount)),
                    TotalQuantity = group.Sum(row =>
                        ApplyHBSalesDocumentTypeSign(row.DocumentType, row.TotalQuantity)),
                };
            })
            .ToList();

        return orders
            .GroupBy(order => (order.BranchCode, order.Hour))
            .Select(group => new HBSalesHourlyAggregateRow
            {
                BranchCode = group.Key.BranchCode,
                Hour = group.Key.Hour,
                TotalAmount = group.Sum(order => order.TotalAmount),
                TotalQuantity = group.Sum(order => order.TotalQuantity),
                // 与分店日统计的 COUNT(DISTINCT 销售单号) 口径一致：空单号不计单数，但金额仍计入。
                OrderCount = group.Count(order => order.HasOrderNo),
            })
            .OrderBy(row => row.BranchCode, StringComparer.Ordinal)
            .ThenBy(row => row.Hour)
            .ToList();
    }

    private static decimal ApplyHBSalesDocumentTypeSign(string? documentType, decimal value)
    {
        var normalized = documentType?.Trim();
        return normalized == "3" || normalized == "4" ? -value : value;
    }

    internal static async Task<Posm2025DailySnapshot> Load2025PosmDailySnapshotAsync(
        POSMSqlSugarContext posmContext,
        DateTime date)
    {
        var targetDate = date.Date;
        if (targetDate.Year != 2025)
            throw new ArgumentException("POSM 预载入口只接受 2025 日期", nameof(date));

        var nextDate = targetDate.AddDays(1);
        var orderRows = await posmContext.Db.Queryable<SalesOrder>()
            .Where(order =>
                order.Status != null
                && (order.Status == 1 || order.Status == 4)
                && order.OrderTime != null
                && order.OrderTime >= targetDate
                && order.OrderTime < nextDate)
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
            })
            .ToListAsync();
        // 订单行（订单数）只含 1/4；明细与支付按净额口径并入退款单（Status=3）。
        var detailRows = await posmContext.Db.Queryable<SalesOrder>()
            .LeftJoin<SalesOrderDetail>((order, detail) => order.OrderGuid == detail.OrderGuid)
            .Where(order =>
                order.Status != null
                && SalesOrderStatusRules.NetSalesStatuses.Contains(order.Status)
                && order.OrderTime != null
                && order.OrderTime >= targetDate
                && order.OrderTime < nextDate)
            .Select((order, detail) => new ProductStoreDailySourceRow
            {
                Date = order.OrderTime!.Value.Date,
                OrderGuid = order.OrderGuid,
                OrderStatus = order.Status,
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
                SourceCreatedAt = detail.CreatedTime,
                SourceUpdatedAt = detail.UpdatedTime,
            })
            .ToListAsync();
        var paymentRows = await posmContext.Db.Queryable<PaymentDetail, SalesOrder>(
                (payment, order) => payment.OrderGuid == order.OrderGuid)
            .Where((payment, order) =>
                order.Status != null
                && SalesOrderStatusRules.NetSalesStatuses.Contains(order.Status)
                && order.OrderTime != null
                && order.OrderTime >= targetDate
                && order.OrderTime < nextDate)
            .Select((payment, order) => new StoreStatisticPaymentRow
            {
                PaymentGuid = payment.PaymentGuid,
                OrderGuid = payment.OrderGuid,
                BranchCode = order.BranchCode,
                DeviceCode = order.DeviceCode,
                Amount = payment.Amount ?? 0m,
                CreatedAt = payment.CreatedTime,
                UpdatedAt = payment.UpdatedTime,
                LastUploadTime = payment.LastUploadTime,
            })
            .ToListAsync();
        var detailGuidSet = detailRows
            .Select(row => row.DetailGuid)
            .Where(guid => !string.IsNullOrWhiteSpace(guid))
            .Select(guid => guid!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var supplementalReturnRows = await LoadSupplementalReturnRowsAsync(
            posmContext,
            targetDate,
            nextDate,
            detailGuidSet
        );
        var deviceBranchMap = await LoadDeviceBranchMapAsync(
            posmContext,
            detailRows.Select(row => row.DeviceCode)
                .Concat(paymentRows.Select(row => row.DeviceCode))
                .Concat(orderRows.Select(row => row.DeviceCode))
                .Concat(supplementalReturnRows.Select(row => row.DeviceCode))
        );
        var signature = SalesStatisticsProductStoreDailyDomainRules
            .CreatePosm2025DailySnapshotSignature(
                targetDate,
                orderRows,
                detailRows,
                paymentRows,
                supplementalReturnRows
            );
        return new Posm2025DailySnapshot(
            detailRows,
            supplementalReturnRows,
            paymentRows,
            orderRows,
            deviceBranchMap,
            signature
        );
    }
}
