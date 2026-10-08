using BlazorApp.Api.Data;
using BlazorApp.Api.Features.StoreOrders.ProductHistory.Domain;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.StoreOrders.ProductHistory.Infrastructure;

internal sealed class ProductOrderHistoryQueryStore(
    SqlSugarContext context,
    ProductSalesHistoryQueryStore salesHistoryQueryStore
)
{
    private readonly ISqlSugarClient _db = context.Db;

    internal async Task<
        PagedListReactDto<StoreOrderProductOrderHistoryItemDto>
    > GetProductOrderHistoryAsync(ProductOrderHistoryQueryInput input)
    {
        var result = ProductHistoryRules.CreateOrderHistoryResult(input);
        RefAsync<int> totalCount = 0;
        // 同订单同商品重复明细在数据库侧聚合，避免分页后再在内存求和。
        var rows = await _db.Queryable<WareHouseOrderDetails>()
            .InnerJoin<WareHouseOrder>((detail, order) => detail.OrderGUID == order.OrderGUID)
            .Where((detail, order) =>
                order.StoreCode == input.StoreCode
                && detail.ProductCode == input.ProductCode
                && order.FlowStatus > 0
                && !order.IsDeleted
                && !detail.IsDeleted
            )
            .GroupBy((detail, order) => new
            {
                detail.OrderGUID,
                order.OrderNo,
                order.OrderDate,
                order.OutboundDate,
                order.FlowStatus,
                order.CreatedAt,
            })
            .Select((detail, order) => new ProductHistoryOrderHistoryRow
            {
                OrderGUID = detail.OrderGUID,
                OrderNo = order.OrderNo,
                OrderDate = order.OrderDate,
                OutboundDate = order.OutboundDate,
                FlowStatus = order.FlowStatus,
                CreatedAt = order.CreatedAt,
                Quantity = SqlFunc.AggregateSum(detail.Quantity),
                AllocQuantity = SqlFunc.AggregateSum(detail.AllocQuantity),
            })
            .MergeTable()
            .OrderBy(item => item.OrderDate, OrderByType.Desc)
            .OrderBy(item => item.CreatedAt, OrderByType.Desc)
            .OrderBy(item => item.OrderGUID, OrderByType.Desc)
            .ToPageListAsync(input.PageNumber, input.PageSize, totalCount);

        result.Total = totalCount.Value;
        result.Items = rows
            .Select(row => new StoreOrderProductOrderHistoryItemDto
            {
                OrderGUID = row.OrderGUID ?? string.Empty,
                OrderNo = row.OrderNo,
                OrderDate = row.OrderDate,
                OutboundDate = row.OutboundDate,
                FlowStatus = row.FlowStatus,
                Quantity = row.Quantity,
                AllocQuantity = row.AllocQuantity,
            })
            .ToList();
        return result;
    }

    internal async Task<StoreOrderProductActivityHistoryResultDto> GetProductActivityHistoryAsync(
        ProductActivityHistoryQueryInput input
    )
    {
        var result = ProductHistoryRules.CreateActivityHistoryResult(input);
        var salesContext = await salesHistoryQueryStore.GetActiveStoreSalesContextAsync(
            input.StoreCode
        );
        var endDate = salesContext?.EndDate.Date ?? salesHistoryQueryStore.UtcToday;
        if (salesContext != null)
        {
            result.EndDate = endDate;
            result.LastArrivalDate = await salesHistoryQueryStore.GetLatestArrivalDateAsync(
                salesContext.StoreCode,
                input.ProductCode,
                endDate
            );
        }

        // 仅将最近 24 个月内的 6 个完成订单边界读入内存；历史行与销量仍在数据库分页。
        // 24 个月（原 12 个月）：一年多没再订的商品，弹窗也要能看到上次来货那张单及其后的销量，与商品卡片一致。
        var historyStartDate = endDate.AddMonths(-24);
        var historyEndExclusive = endDate.AddDays(1);
        var selectedOrders = await BuildAggregatedProductOrderHistoryQuery(
                input.StoreCode,
                input.ProductCode
            )
            .Where(item =>
                item.FlowStatus == 2
                && item.OutboundDate != null
                && item.OutboundDate >= historyStartDate
                && item.OutboundDate < historyEndExclusive
            )
            .OrderBy(item => item.OutboundDate, OrderByType.Desc)
            .OrderBy(item => item.CreatedAt, OrderByType.Desc)
            .OrderBy(item => item.OrderGUID, OrderByType.Desc)
            .Take(6)
            .ToListAsync();

        if (selectedOrders.Count > 0)
        {
            result.LatestOrderQuantity = selectedOrders[0].Quantity;
            result.LatestAllocQuantity = selectedOrders[0].AllocQuantity;
        }

        if (salesContext != null && result.LastArrivalDate.HasValue)
        {
            result.TotalSalesQuantity = await salesHistoryQueryStore.GetTotalSalesQuantityAsync(
                salesContext.StoreCode,
                input.ProductCode,
                result.LastArrivalDate.Value.Date,
                endDate
            );
        }

        var validArrivalGroups = selectedOrders
            .Where(item => (item.AllocQuantity ?? 0) > 0 && item.OutboundDate.HasValue)
            .GroupBy(item => item.OutboundDate!.Value.Date)
            .OrderBy(group => group.Key)
            .ToList();
        var intervals = new List<ProductHistorySalesInterval>(validArrivalGroups.Count);
        for (var index = 0; index < validArrivalGroups.Count; index++)
        {
            var group = validArrivalGroups[index];
            intervals.Add(new ProductHistorySalesInterval
            {
                AnchorOrderGuid = group.First().OrderGUID ?? string.Empty,
                StartDate = group.Key,
                EndDate = index + 1 < validArrivalGroups.Count
                    ? validArrivalGroups[index + 1].Key.AddDays(-1)
                    : endDate,
            });
        }

        var selectedOrderGuids = selectedOrders
            .Select(item => item.OrderGUID ?? string.Empty)
            .Where(guid => !string.IsNullOrWhiteSpace(guid))
            .ToList();

        if (input.RecordType == "order")
        {
            var orderPage = await LoadOrderActivityRowsAsync(
                input.StoreCode,
                input.ProductCode,
                selectedOrderGuids,
                historyStartDate,
                historyEndExclusive,
                input.PageNumber,
                input.PageSize
            );
            result.Total = orderPage.Total;
            result.Items = orderPage.Rows.Select(ToActivityItem).ToList();
            return result;
        }

        if (input.RecordType == "sales")
        {
            if (salesContext != null && intervals.Count > 0)
            {
                var salesPage = await LoadSalesActivityRowsAsync(
                    salesContext.StoreCode,
                    input.ProductCode,
                    intervals,
                    input.PageNumber,
                    input.PageSize
                );
                result.Total = salesPage.Total;
                result.Items = salesPage.Rows.Select(ToActivityItem).ToList();
            }

            return result;
        }

        var activityQueries = new List<ISugarQueryable<ProductHistoryActivityHistoryRow>>();
        if (selectedOrderGuids.Count > 0)
        {
            var orderActivityQuery = BuildAggregatedProductOrderHistoryQuery(
                    input.StoreCode,
                    input.ProductCode
                )
                .Where(item =>
                    selectedOrderGuids.Contains(item.OrderGUID!)
                    && item.FlowStatus == 2
                    && item.OutboundDate != null
                    && item.OutboundDate >= historyStartDate
                    && item.OutboundDate < historyEndExclusive
                )
                .Select(item => new ProductHistoryActivityHistoryRow
                {
                    RecordType = "order",
                    RecordDate = item.OrderDate,
                    SortDate = item.OutboundDate,
                    SortType = 1,
                    CreatedAt = item.CreatedAt,
                    OrderGUID = item.OrderGUID ?? string.Empty,
                    OrderNo = item.OrderNo,
                    OrderDate = item.OrderDate,
                    OutboundDate = item.OutboundDate,
                    FlowStatus = item.FlowStatus,
                    Quantity = item.Quantity,
                    AllocQuantity = item.AllocQuantity,
                    SalesQuantity = null,
                    AveragePrice = null,
                    PeriodStartDate = null,
                    PeriodEndDate = null,
                });
            activityQueries.Add(orderActivityQuery);
        }

        if (salesContext != null && intervals.Count > 0)
        {
            foreach (var interval in intervals)
            {
                var intervalStart = interval.StartDate;
                var intervalEnd = interval.EndDate;
                var anchorOrderGuid = interval.AnchorOrderGuid;
                var dailySalesQuery = salesHistoryQueryStore
                    .BuildDailySalesQuery(
                        salesContext.StoreCode,
                        input.ProductCode,
                        intervalStart,
                        intervalEnd
                    )
                    .Select(item => new ProductHistoryActivityHistoryRow
                    {
                        RecordType = "sales",
                        RecordDate = item.Date,
                        SortDate = item.Date,
                        SortType = 0,
                        CreatedAt = null,
                        OrderGUID = string.Empty,
                        OrderNo = null,
                        OrderDate = null,
                        OutboundDate = null,
                        FlowStatus = null,
                        Quantity = null,
                        AllocQuantity = null,
                        SalesQuantity = item.TotalQuantity,
                        AveragePrice = item.TotalQuantity == 0
                            ? (decimal?)null
                            : item.TotalAmount / item.TotalQuantity,
                        PeriodStartDate = intervalStart,
                        PeriodEndDate = intervalEnd,
                    });
                activityQueries.Add(dailySalesQuery);

                var subtotalQuery = _db.Queryable<WareHouseOrder>()
                    .LeftJoin<ProductStoreDailySalesStatistic>((order, statistic) =>
                        statistic.BranchCode == salesContext.StoreCode
                        && statistic.ProductCode == input.ProductCode
                        && statistic.Date >= intervalStart
                        && statistic.Date <= intervalEnd
                    )
                    .Where((order, statistic) => order.OrderGUID == anchorOrderGuid)
                    .GroupBy((order, statistic) => order.OrderGUID)
                    .Select((order, statistic) => new ProductHistoryActivityHistoryRow
                    {
                        RecordType = "salesSubtotal",
                        RecordDate = intervalEnd,
                        SortDate = intervalEnd.AddDays(1).AddTicks(-1),
                        SortType = 2,
                        CreatedAt = null,
                        OrderGUID = string.Empty,
                        OrderNo = null,
                        OrderDate = null,
                        OutboundDate = null,
                        FlowStatus = null,
                        Quantity = null,
                        AllocQuantity = null,
                        SalesQuantity = SqlFunc.IsNull(
                            SqlFunc.AggregateSum(statistic.TotalQuantity),
                            0
                        ),
                        AveragePrice = SqlFunc.IsNull(
                                SqlFunc.AggregateSum(statistic.TotalQuantity),
                                0
                            ) == 0
                            ? (decimal?)null
                            : SqlFunc.IsNull(
                                SqlFunc.AggregateSum(statistic.TotalAmount),
                                0m
                            )
                                / SqlFunc.IsNull(
                                    SqlFunc.AggregateSum(statistic.TotalQuantity),
                                    0
                                ),
                        PeriodStartDate = intervalStart,
                        PeriodEndDate = intervalEnd,
                    });
                activityQueries.Add(subtotalQuery);
            }
        }

        List<ProductHistoryActivityHistoryRow> rows;
        if (activityQueries.Count == 0)
        {
            rows = new List<ProductHistoryActivityHistoryRow>();
            result.Total = 0;
        }
        else
        {
            var mergedQuery = activityQueries.Count == 1
                ? activityQueries[0].MergeTable()
                : _db.UnionAll(activityQueries.ToArray()).MergeTable();
            result.Total = await mergedQuery.CountAsync();
            var requestedSkip = (long)(input.PageNumber - 1) * input.PageSize;
            if (requestedSkip >= result.Total || requestedSkip >= int.MaxValue)
            {
                rows = new List<ProductHistoryActivityHistoryRow>();
            }
            else
            {
                rows = await mergedQuery
                    .OrderBy(item => item.SortDate, OrderByType.Desc)
                    .OrderBy(item => item.SortType, OrderByType.Desc)
                    .OrderBy(item => item.OrderDate, OrderByType.Desc)
                    .OrderBy(item => item.CreatedAt, OrderByType.Desc)
                    .OrderBy(item => item.OrderGUID, OrderByType.Desc)
                    .Skip((int)requestedSkip)
                    .Take(input.PageSize)
                    .ToListAsync();
            }
        }

        result.Items = rows.Select(ToActivityItem).ToList();
        return result;
    }

    private async Task<(
        int Total,
        List<ProductHistoryActivityHistoryRow> Rows
    )> LoadOrderActivityRowsAsync(
        string storeCode,
        string productCode,
        List<string> selectedOrderGuids,
        DateTime historyStartDate,
        DateTime historyEndExclusive,
        int pageNumber,
        int pageSize
    )
    {
        if (selectedOrderGuids.Count == 0)
        {
            return (0, new List<ProductHistoryActivityHistoryRow>());
        }

        var query = BuildAggregatedProductOrderHistoryQuery(storeCode, productCode)
            .Where(item =>
                selectedOrderGuids.Contains(item.OrderGUID!)
                && item.FlowStatus == 2
                && item.OutboundDate != null
                && item.OutboundDate >= historyStartDate
                && item.OutboundDate < historyEndExclusive
            );
        var total = await query.CountAsync();
        var requestedSkip = (long)(pageNumber - 1) * pageSize;
        if (requestedSkip >= total || requestedSkip >= int.MaxValue)
        {
            return (total, new List<ProductHistoryActivityHistoryRow>());
        }

        var rows = await query
            .OrderBy(item => item.OutboundDate, OrderByType.Desc)
            .OrderBy(item => item.OrderDate, OrderByType.Desc)
            .OrderBy(item => item.CreatedAt, OrderByType.Desc)
            .OrderBy(item => item.OrderGUID, OrderByType.Desc)
            .Skip((int)requestedSkip)
            .Take(pageSize)
            .ToListAsync();
        return (total, rows.Select(ToOrderActivityRow).ToList());
    }

    private async Task<(
        int Total,
        List<ProductHistoryActivityHistoryRow> Rows
    )> LoadSalesActivityRowsAsync(
        string storeCode,
        string productCode,
        List<ProductHistorySalesInterval> intervals,
        int pageNumber,
        int pageSize
    )
    {
        var activityQueries = new List<
            ISugarQueryable<ProductHistorySalesActivityHistoryRow>
        >();
        foreach (var interval in intervals)
        {
            var intervalStart = interval.StartDate;
            var intervalEnd = interval.EndDate;
            var anchorOrderGuid = interval.AnchorOrderGuid;
            var dailySalesQuery = salesHistoryQueryStore
                .BuildDailySalesQuery(storeCode, productCode, intervalStart, intervalEnd)
                .Select(item => new ProductHistorySalesActivityHistoryRow
                {
                    RecordType = "sales",
                    RecordDate = item.Date,
                    SortDate = item.Date,
                    SortType = 0,
                    SalesQuantity = item.TotalQuantity,
                    AveragePrice = item.TotalQuantity == 0
                        ? (decimal?)null
                        : item.TotalAmount / item.TotalQuantity,
                    PeriodStartDate = intervalStart,
                    PeriodEndDate = intervalEnd,
                });
            activityQueries.Add(dailySalesQuery);

            var subtotalQuery = _db.Queryable<WareHouseOrder>()
                .LeftJoin<ProductStoreDailySalesStatistic>((order, statistic) =>
                    statistic.BranchCode == storeCode
                    && statistic.ProductCode == productCode
                    && statistic.Date >= intervalStart
                    && statistic.Date <= intervalEnd
                )
                .Where((order, statistic) => order.OrderGUID == anchorOrderGuid)
                .GroupBy((order, statistic) => order.OrderGUID)
                .Select((order, statistic) => new ProductHistorySalesActivityHistoryRow
                {
                    RecordType = "salesSubtotal",
                    RecordDate = intervalEnd,
                    SortDate = intervalEnd.AddDays(1).AddTicks(-1),
                    SortType = 2,
                    SalesQuantity = SqlFunc.IsNull(
                        SqlFunc.AggregateSum(statistic.TotalQuantity),
                        0
                    ),
                    AveragePrice = SqlFunc.IsNull(
                            SqlFunc.AggregateSum(statistic.TotalQuantity),
                            0
                        ) == 0
                        ? (decimal?)null
                        : SqlFunc.IsNull(
                            SqlFunc.AggregateSum(statistic.TotalAmount),
                            0m
                        )
                            / SqlFunc.IsNull(
                                SqlFunc.AggregateSum(statistic.TotalQuantity),
                                0
                            ),
                    PeriodStartDate = intervalStart,
                    PeriodEndDate = intervalEnd,
                });
            activityQueries.Add(subtotalQuery);
        }

        var mergedQuery = _db.UnionAll(activityQueries.ToArray()).MergeTable();
        var total = await mergedQuery.CountAsync();
        var requestedSkip = (long)(pageNumber - 1) * pageSize;
        if (requestedSkip >= total || requestedSkip >= int.MaxValue)
        {
            return (total, new List<ProductHistoryActivityHistoryRow>());
        }

        var rows = await mergedQuery
            .OrderBy(item => item.SortDate, OrderByType.Desc)
            .OrderBy(item => item.SortType, OrderByType.Desc)
            .Skip((int)requestedSkip)
            .Take(pageSize)
            .ToListAsync();
        return (total, rows.Select(ToSalesActivityRow).ToList());
    }

    private ISugarQueryable<ProductHistoryOrderHistoryRow> BuildAggregatedProductOrderHistoryQuery(
        string storeCode,
        string productCode
    )
    {
        return _db.Queryable<WareHouseOrderDetails>()
            .InnerJoin<WareHouseOrder>((detail, order) => detail.OrderGUID == order.OrderGUID)
            .Where((detail, order) =>
                order.StoreCode == storeCode
                && detail.ProductCode == productCode
                && order.FlowStatus > 0
                && !order.IsDeleted
                && !detail.IsDeleted
            )
            .GroupBy((detail, order) => new
            {
                detail.OrderGUID,
                order.OrderNo,
                order.OrderDate,
                order.OutboundDate,
                order.FlowStatus,
                order.CreatedAt,
            })
            .Select((detail, order) => new ProductHistoryOrderHistoryRow
            {
                OrderGUID = detail.OrderGUID,
                OrderNo = order.OrderNo,
                OrderDate = order.OrderDate,
                OutboundDate = order.OutboundDate,
                FlowStatus = order.FlowStatus,
                CreatedAt = order.CreatedAt,
                Quantity = SqlFunc.AggregateSum(detail.Quantity),
                AllocQuantity = SqlFunc.AggregateSum(detail.AllocQuantity),
            })
            .MergeTable();
    }

    private static ProductHistoryActivityHistoryRow ToOrderActivityRow(
        ProductHistoryOrderHistoryRow row
    )
    {
        return new ProductHistoryActivityHistoryRow
        {
            RecordType = "order",
            RecordDate = row.OrderDate?.Date,
            SortDate = row.OutboundDate,
            SortType = 1,
            CreatedAt = row.CreatedAt,
            OrderGUID = row.OrderGUID ?? string.Empty,
            OrderNo = row.OrderNo,
            OrderDate = row.OrderDate,
            OutboundDate = row.OutboundDate,
            FlowStatus = row.FlowStatus,
            Quantity = row.Quantity,
            AllocQuantity = row.AllocQuantity,
        };
    }

    private static ProductHistoryActivityHistoryRow ToSalesActivityRow(
        ProductHistorySalesActivityHistoryRow row
    )
    {
        return new ProductHistoryActivityHistoryRow
        {
            RecordType = row.RecordType,
            RecordDate = row.RecordDate?.Date,
            SortDate = row.SortDate,
            SortType = row.SortType,
            CreatedAt = null,
            OrderGUID = string.Empty,
            SalesQuantity = row.SalesQuantity,
            AveragePrice = row.AveragePrice,
            PeriodStartDate = row.PeriodStartDate,
            PeriodEndDate = row.PeriodEndDate,
        };
    }

    private static StoreOrderProductActivityHistoryItemDto ToActivityItem(
        ProductHistoryActivityHistoryRow row
    )
    {
        return new StoreOrderProductActivityHistoryItemDto
        {
            RecordType = row.RecordType,
            RecordDate = row.RecordDate?.Date,
            OrderGUID = row.RecordType == "order" ? row.OrderGUID : null,
            OrderNo = row.RecordType == "order" ? row.OrderNo : null,
            OrderDate = row.RecordType == "order" ? row.OrderDate : null,
            OutboundDate = row.RecordType == "order" ? row.OutboundDate : null,
            FlowStatus = row.RecordType == "order" ? row.FlowStatus : null,
            Quantity = row.RecordType == "order" ? row.Quantity : null,
            AllocQuantity = row.RecordType == "order" ? row.AllocQuantity : null,
            SalesQuantity = row.RecordType != "order" ? row.SalesQuantity : null,
            AveragePrice = row.RecordType != "order" ? row.AveragePrice : null,
            PeriodStartDate = row.RecordType != "order" ? row.PeriodStartDate : null,
            PeriodEndDate = row.RecordType != "order" ? row.PeriodEndDate : null,
        };
    }
}
