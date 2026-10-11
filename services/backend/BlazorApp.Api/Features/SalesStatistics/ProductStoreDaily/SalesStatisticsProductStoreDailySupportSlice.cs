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
    /// <summary>销售统计垂直切片：SalesStatisticsProductStoreDailySupportSlice。</summary>
    internal sealed class SalesStatisticsProductStoreDailySupportSlice : SalesStatisticsSliceBase
    {
        public SalesStatisticsProductStoreDailySupportSlice(SalesStatisticsSliceContext shared)
            : base(shared) { }

    internal async Task<IReadOnlyList<ProductStoreDailyBranchRollup>> GetProductStoreDailyReturnAdjustmentsAsync(
        DateTime date
    )
    {
        var targetDate = date.Date;
        var nextDate = targetDate.AddDays(1);
        var detailRows = await _posmContext.Db.Queryable<SalesOrder>()
            .LeftJoin<SalesOrderDetail>((o, d) => o.OrderGuid == d.OrderGuid)
            // 与商品店日统计的明细来源完全一致（含退款单），补充退货才能按同一批明细 GUID 去重。
            .Where((o, d) =>
                o.Status != null
                && SalesOrderStatusRules.NetSalesStatuses.Contains(o.Status)
                && o.OrderTime != null
                && o.OrderTime >= targetDate
                && o.OrderTime < nextDate
            )
            .Select((o, d) => new { d.OrderDetailGuid })
            .ToListAsync();
        var detailGuidSet = detailRows
            .Select(x => x.OrderDetailGuid)
            .Where(guid => !string.IsNullOrWhiteSpace(guid))
            .Select(guid => guid!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var supplementalReturnRows = await SalesStatisticsProductStoreDailySourceQueries
            .LoadSupplementalReturnRowsAsync(
            _posmContext,
            targetDate,
            nextDate,
            detailGuidSet
        );
        var deviceBranchMap = await SalesStatisticsProductStoreDailySourceQueries
            .LoadDeviceBranchMapAsync(
            _posmContext,
            supplementalReturnRows.Select(row => row.DeviceCode)
        );

        return supplementalReturnRows
            .Select(row => new
            {
                Row = row,
                BranchCode = SalesStatisticsCodeRules.ResolveBranchCode(
                    row.BranchCode,
                    row.DeviceCode,
                    deviceBranchMap
                ),
            })
            .Where(x =>
                !string.IsNullOrWhiteSpace(x.BranchCode)
                && !string.IsNullOrWhiteSpace(x.Row.ProductCode)
            )
            .GroupBy(x => x.BranchCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ProductStoreDailyBranchRollup(
                group.Key,
                group.Sum(x => x.Row.ActualAmount),
                (int)group.Sum(x => x.Row.Quantity)
            ))
            .ToList();
    }

    internal static async Task<List<ProductStoreDailySourceRow>> LoadSupplementalReturnRowsAsync(
        POSMSqlSugarContext posmContext,
        DateTime targetDate,
        DateTime nextDate,
        HashSet<string> detailGuidSet
    ) => await SalesStatisticsProductStoreDailySourceQueries.LoadSupplementalReturnRowsAsync(
        posmContext,
        targetDate,
        nextDate,
        detailGuidSet
    );

    internal static string NormalizeCode(string? code) => SalesStatisticsCodeRules.Normalize(code);

    internal static string ResolveStatisticSupplierCode(
        string? mappedSupplierCode,
        string? detailSupplierCode) =>
        SalesStatisticsProductStoreDailyDomainRules.ResolveStatisticSupplierCode(
            mappedSupplierCode,
            detailSupplierCode
        );

    internal static async Task<Dictionary<string, string>> LoadDeviceBranchMapAsync(
        POSMSqlSugarContext posmContext,
        IEnumerable<string?> deviceCodes
    ) => await SalesStatisticsProductStoreDailySourceQueries.LoadDeviceBranchMapAsync(
        posmContext,
        deviceCodes
    );

    internal static async Task<(
        Dictionary<string, decimal> PaymentAmounts,
        Dictionary<string, decimal> DetailAmounts
    )> LoadOrderAmountMapsAsync<T>(
        POSMSqlSugarContext posmContext,
        DateTime startDate,
        DateTime endExclusive,
        IEnumerable<T> detailRows,
        Func<T, string?> orderGuidSelector,
        Func<T, decimal> detailAmountSelector
    ) => await SalesStatisticsProductStoreDailySourceQueries.LoadOrderAmountMapsAsync(
        posmContext,
        startDate,
        endExclusive,
        detailRows,
        orderGuidSelector,
        detailAmountSelector
    );

    internal static Dictionary<string, decimal> BuildOrderAmountMap(
        IEnumerable<OrderAmountRow> rows) =>
        SalesStatisticsProductStoreDailyDomainRules.BuildOrderAmountMap(rows);

    internal static decimal ResolveStatisticAmount(
        string? orderGuid,
        decimal detailAmount,
        Dictionary<string, decimal> paymentAmounts,
        Dictionary<string, decimal> detailAmounts
    ) => SalesStatisticsProductStoreDailyDomainRules.ResolveStatisticAmount(
        orderGuid,
        detailAmount,
        paymentAmounts,
        detailAmounts
    );

    internal async Task<List<StoreSalesStatistic>> BuildStoreStatisticsAsync(
        SqlSugarContext context,
        POSMSqlSugarContext posmContext,
        HBSalesRecordSqlSugarContext? hbSalesContext,
        DateTime date,
        List<string>? branchCodes,
        IReadOnlyList<ProductStoreDailySourceRow>? preloadedHBSalesRows = null,
        Posm2025DailySnapshot? preloadedPosmSnapshot = null
    )
    {
        var targetDate = date.Date;
        var nextDate = targetDate.AddDays(1);
        var targetBranchCodes = SalesStatisticsCodeRules.NormalizeBranchCodes(branchCodes);

        // 金额、销量按净额：并入退款单（Status=3，负数）；订单数仍只数 1/4（见 orderRows）。
        var paymentRows = preloadedPosmSnapshot?.PaymentRows.ToList() ?? await posmContext.Db.Queryable<PaymentDetail, SalesOrder>(
                (pd, so) => pd.OrderGuid == so.OrderGuid
            )
            .Where((pd, so) =>
                so.Status != null
                && SalesOrderStatusRules.NetSalesStatuses.Contains(so.Status)
                && so.OrderTime != null
                && so.OrderTime >= targetDate
                && so.OrderTime < nextDate
            )
            .Select((pd, so) => new StoreStatisticPaymentRow
            {
                OrderGuid = so.OrderGuid,
                BranchCode = so.BranchCode,
                DeviceCode = so.DeviceCode,
                Amount = pd.Amount ?? 0m,
            })
            .ToListAsync();

        var quantityRows = preloadedPosmSnapshot?.DetailRows
            .Select(row => new StoreStatisticQuantityRow
            {
                OrderGuid = row.OrderGuid,
                BranchCode = row.BranchCode,
                DeviceCode = row.DeviceCode,
                Quantity = (int)row.Quantity,
            })
            .ToList()
            ?? await posmContext.Db.Queryable<SalesOrderDetail, SalesOrder>(
                (d, so) => d.OrderGuid == so.OrderGuid
            )
            .Where((d, so) =>
                so.Status != null
                && SalesOrderStatusRules.NetSalesStatuses.Contains(so.Status)
                && so.OrderTime != null
                && so.OrderTime >= targetDate
                && so.OrderTime < nextDate
            )
            .Select((d, so) => new StoreStatisticQuantityRow
            {
                OrderGuid = so.OrderGuid,
                BranchCode = so.BranchCode,
                DeviceCode = so.DeviceCode,
                Quantity = d.Quantity ?? 0,
            })
            .ToListAsync();

        var orderRows = preloadedPosmSnapshot?.OrderRows.ToList() ?? await posmContext.Db.Queryable<SalesOrder>()
            .Where(so =>
                so.Status != null
                && (so.Status == 1 || so.Status == 4)
                && so.OrderTime != null
                && so.OrderTime >= targetDate
                && so.OrderTime < nextDate
            )
            .Select(so => new StoreStatisticOrderRow
            {
                OrderGuid = so.OrderGuid,
                BranchCode = so.BranchCode,
                DeviceCode = so.DeviceCode,
            })
            .ToListAsync();

        var deviceCodes = paymentRows
            .Select(row => string.IsNullOrWhiteSpace(row.BranchCode) ? row.DeviceCode : null)
            .Concat(quantityRows.Select(row => string.IsNullOrWhiteSpace(row.BranchCode) ? row.DeviceCode : null))
            .Concat(orderRows.Select(row => string.IsNullOrWhiteSpace(row.BranchCode) ? row.DeviceCode : null))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!)
            .Distinct()
            .ToList();
        var deviceBranchMap = preloadedPosmSnapshot?.DeviceBranchMap.ToDictionary(
                entry => entry.Key,
                entry => entry.Value,
                StringComparer.OrdinalIgnoreCase
            ) ?? await SalesStatisticsProductStoreDailySourceQueries.LoadDeviceBranchMapAsync(
                posmContext,
                deviceCodes);

        bool IsTargetBranch(string branchCode) =>
            !string.IsNullOrWhiteSpace(branchCode)
            && (!targetBranchCodes.Any() || targetBranchCodes.Contains(branchCode));

        var resolvedPaymentRows = paymentRows
            .Select(row => new
            {
                BranchCode = SalesStatisticsCodeRules.ResolveBranchCode(
                    row.BranchCode,
                    row.DeviceCode,
                    deviceBranchMap
                ),
                row.Amount,
            })
            .ToList();
        LogSkippedBranchCodeRows(
            "分店每日销售统计金额",
            resolvedPaymentRows,
            row => row.BranchCode,
            row => row.Amount,
            _ => 0m
        );

        var resolvedQuantityRows = quantityRows
            .Select(row => new
            {
                BranchCode = SalesStatisticsCodeRules.ResolveBranchCode(
                    row.BranchCode,
                    row.DeviceCode,
                    deviceBranchMap
                ),
                row.Quantity,
            })
            .ToList();
        LogSkippedBranchCodeRows(
            "分店每日销售统计销量",
            resolvedQuantityRows,
            row => row.BranchCode,
            _ => 0m,
            row => row.Quantity
        );

        var resolvedOrderRows = orderRows
            .Select(row => new
            {
                row.OrderGuid,
                BranchCode = SalesStatisticsCodeRules.ResolveBranchCode(
                    row.BranchCode,
                    row.DeviceCode,
                    deviceBranchMap
                ),
            })
            .ToList();
        LogSkippedBranchCodeRows(
            "分店每日销售统计订单数",
            resolvedOrderRows,
            row => row.BranchCode,
            _ => 0m,
            _ => 0m
        );

        // 金额仍以支付明细为准；只在内存中解析分店，避免订单分店为空时漏入统计。
        var amountByBranch = resolvedPaymentRows
            .Where(row => IsTargetBranch(row.BranchCode))
            .GroupBy(row => row.BranchCode)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Amount));

        // 销量使用销售明细 Quantity，不能使用订单头 ItemCount。
        var quantityByBranch = resolvedQuantityRows
            .Where(row => IsTargetBranch(row.BranchCode))
            .GroupBy(row => row.BranchCode)
            .ToDictionary(group => group.Key, group => (decimal)group.Sum(row => row.Quantity));

        var orderCountByBranch = resolvedOrderRows
            .Where(row => IsTargetBranch(row.BranchCode) && !string.IsNullOrWhiteSpace(row.OrderGuid))
            .GroupBy(row => row.BranchCode)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => row.OrderGuid).Distinct().Count()
            );

        if (SalesStatisticsHBSalesHistoryWindow.Includes(targetDate))
        {
            var hbSalesAggregates = preloadedHBSalesRows != null
                ? SalesStatisticsProductStoreDailyDomainRules.BuildHBSalesStoreAggregates(
                    preloadedHBSalesRows
                )
                : await SalesStatisticsProductStoreDailySourceQueries
                    .LoadHBSalesStoreAggregatesAsync(
                    hbSalesContext
                        ?? throw new InvalidOperationException("HBSales 历史窗口内的分店统计缺少 HBSalesRecord 上下文"),
                    targetDate,
                    nextDate
                );
            var resolvedHBSalesRows = hbSalesAggregates
                .Select(row => new
                {
                    BranchCode = SalesStatisticsCodeRules.Normalize(row.BranchCode),
                    row.TotalQuantity,
                    row.TotalAmount,
                    row.OrderCount,
                })
                .Where(row => IsTargetBranch(row.BranchCode))
                .ToList();

            foreach (var group in resolvedHBSalesRows.GroupBy(row => row.BranchCode))
            {
                amountByBranch[group.Key] = amountByBranch.GetValueOrDefault(group.Key)
                    + group.Sum(row => row.TotalAmount);
                quantityByBranch[group.Key] = quantityByBranch.GetValueOrDefault(group.Key)
                    + group.Sum(row => row.TotalQuantity);
                orderCountByBranch[group.Key] = orderCountByBranch.GetValueOrDefault(group.Key)
                    + group.Sum(row => row.OrderCount);
            }
        }

        var statisticBranchCodes = amountByBranch.Keys
            .Union(quantityByBranch.Keys)
            .Union(orderCountByBranch.Keys)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();

        var stores = statisticBranchCodes.Any()
            ? await context.Db.Queryable<Store>()
                .Where(s => statisticBranchCodes.Contains(s.StoreCode))
                .ToListAsync()
            : new List<Store>();
        var storeDict = stores.ToDictionary(s => s.StoreCode, s => s);

        return statisticBranchCodes
            .Select(branchCode =>
            {
                var totalAmount = amountByBranch.GetValueOrDefault(branchCode);
                var orderCount = orderCountByBranch.GetValueOrDefault(branchCode);
                var store = storeDict.GetValueOrDefault(branchCode);

                return new StoreSalesStatistic
                {
                    Date = targetDate,
                    BranchCode = branchCode,
                    BranchName = store?.StoreName ?? branchCode,
                    TotalAmount = totalAmount,
                    TotalQuantity = (int)quantityByBranch.GetValueOrDefault(branchCode),
                    OrderCount = orderCount,
                    CustomerCount = orderCount,
                    AverageOrderValue = orderCount > 0 ? totalAmount / orderCount : 0m,
                    UpdateTime = DateTime.Now,
                };
            })
            .ToList();
    }

    internal static List<string> NormalizeBranchCodes(List<string>? branchCodes) =>
        SalesStatisticsCodeRules.NormalizeBranchCodes(branchCodes);

    internal static List<string> NormalizeSupplierCodes(List<string>? supplierCodes) =>
        SalesStatisticsCodeRules.NormalizeSupplierCodes(supplierCodes);

    }
}
