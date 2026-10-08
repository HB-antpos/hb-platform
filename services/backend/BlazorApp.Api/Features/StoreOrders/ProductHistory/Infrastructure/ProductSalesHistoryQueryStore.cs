using System.Diagnostics;
using System.Text.RegularExpressions;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.StoreOrders.ProductHistory.Domain;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.Attendance;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.StoreOrders.ProductHistory.Infrastructure;

internal sealed class ProductSalesHistoryQueryStore(
    SqlSugarContext context,
    ILogger<ProductSalesHistoryQueryStore> logger,
    TimeProvider? timeProvider = null
)
{
    private const int SalesStatisticsMaxCutoffGroupsPerQuery = 100;
    private const int SalesStatisticsParameterBudget = 800;
    private const int SalesStatisticsFixedParameterCount = 2;
    private readonly ISqlSugarClient _db = context.Db;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    internal DateTime UtcToday => _timeProvider.GetUtcNow().UtcDateTime.Date;

    internal async Task<StoreOrderSalesSinceLastArrivalResultDto> GetSalesSinceLastArrivalAsync(
        SalesSinceLastArrivalQueryInput input
    )
    {
        var result = ProductHistoryRules.CreateSalesResult(input);
        var salesContext = await GetActiveStoreSalesContextAsync(input.StoreCode);
        if (salesContext == null)
        {
            result.IsAvailable = false;
            return result;
        }

        result.EndDate = salesContext.EndDate;
        var lastArrivalDate = await GetLatestArrivalDateAsync(
            salesContext.StoreCode,
            input.ProductCode,
            salesContext.EndDate
        );
        result.LastArrivalDate = lastArrivalDate;
        if (!lastArrivalDate.HasValue)
        {
            result.IsAvailable = false;
            return result;
        }

        result.IsAvailable = true;
        var startDate = lastArrivalDate.Value.Date;
        result.TotalSalesQuantity = await _db
            .Queryable<ProductStoreDailySalesStatistic>()
            .Where(item =>
                item.BranchCode == salesContext.StoreCode
                && item.ProductCode == input.ProductCode
                && item.Date >= startDate
                && item.Date <= salesContext.EndDate
            )
            .SumAsync(item => item.TotalQuantity);

        RefAsync<int> totalCount = 0;
        var dailyRows = await _db
            .Queryable<ProductStoreDailySalesStatistic>()
            .Where(item =>
                item.BranchCode == salesContext.StoreCode
                && item.ProductCode == input.ProductCode
                && item.Date >= startDate
                && item.Date <= salesContext.EndDate
            )
            .GroupBy(item => item.Date)
            .Select(item => new ProductHistoryDailySalesStatisticRow
            {
                Date = item.Date,
                TotalQuantity = SqlFunc.AggregateSum(item.TotalQuantity),
                TotalAmount = SqlFunc.AggregateSum(item.TotalAmount),
            })
            .OrderBy(item => item.Date, OrderByType.Desc)
            .ToPageListAsync(input.PageNumber, input.PageSize, totalCount);

        result.TotalCount = totalCount.Value;
        result.Items = dailyRows
            .Select(item => new StoreOrderSalesSinceLastArrivalItemDto
            {
                Date = item.Date,
                SalesQuantity = item.TotalQuantity,
                AveragePrice = item.TotalQuantity == 0
                    ? (decimal?)null
                    : item.TotalAmount / item.TotalQuantity,
            })
            .ToList();
        return result;
    }

    internal async Task<
        List<StoreOrderSalesSinceLastArrivalSummaryItemDto>
    > GetSalesSinceLastArrivalSummaryAsync(SalesSinceLastArrivalSummaryQueryInput input)
    {
        var result = ProductHistoryRules.CreateSalesSummaryResult(input.ProductCodes);
        var salesContext = await GetActiveStoreSalesContextAsync(input.StoreCode);
        if (salesContext == null)
        {
            return result;
        }

        var salesQuantityResult = await GetSalesQuantitySinceLastArrivalMapAsync(
            salesContext.StoreCode,
            input.ProductCodes,
            salesContext.EndDate,
            includeArrivalOrder: true
        );
        foreach (var item in result)
        {
            item.SalesQuantitySinceLastArrival = salesQuantityResult.SalesQuantityMap.TryGetValue(
                item.ProductCode,
                out var salesQuantity
            )
                ? salesQuantity
                : null;
            // 起点日期与销量同源，前端用它提示「自某日来货起」；没有来货记录时两者都为 null。
            item.LastArrivalDate = salesQuantityResult.ArrivalDateMap.TryGetValue(
                item.ProductCode,
                out var lastArrivalDate
            )
                ? lastArrivalDate
                : null;
            // 销量实际统计起点：日统计只有 2024-09-14 起的数据，来货早于它时从数据起点算，避免把缺数据误读成没卖出。
            item.SalesStartDate = item.LastArrivalDate is { } arrivalDay
                ? (arrivalDay.Date > SalesStatisticsHBSalesHistoryWindow.StartDate
                    ? arrivalDay.Date
                    : SalesStatisticsHBSalesHistoryWindow.StartDate)
                : null;
            item.LastArrivalQuantity = salesQuantityResult.ArrivalQuantityMap.TryGetValue(
                item.ProductCode,
                out var lastArrivalQuantity
            )
                ? lastArrivalQuantity
                : null;
            item.LastArrivalOrderQuantity = salesQuantityResult.ArrivalOrderQuantityMap.TryGetValue(
                item.ProductCode,
                out var lastArrivalOrderQuantity
            )
                ? lastArrivalOrderQuantity
                : null;
            item.LastArrivalOrderDate = salesQuantityResult.ArrivalOrderDateMap.TryGetValue(
                item.ProductCode,
                out var lastArrivalOrderDate
            )
                ? lastArrivalOrderDate
                : null;
        }

        return result;
    }

    internal async Task<ProductHistorySalesContext?> GetActiveStoreSalesContextAsync(
        string? storeCode
    )
    {
        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return null;
        }

        var normalizedStoreCode = storeCode.Trim();
        // 门店启用状态与时区资料一次读取；停用门店不再触发来货或销售查询。
        var store = await _db.Queryable<Store>()
            .Where(item => item.StoreCode == normalizedStoreCode && !item.IsDeleted)
            .Select(item => new ProductHistorySalesStoreRow
            {
                IsActive = item.IsActive,
                StoreCode = item.StoreCode,
                StoreName = item.StoreName,
                Address = item.Address,
                TimeZoneId = item.TimeZoneId,
            })
            .FirstAsync();
        if (store?.IsActive != true)
        {
            return null;
        }

        var timeZoneId = ResolveStoreTimeZoneForSales(store);
        var timeZone = ResolveTimeZoneInfo(timeZoneId);
        return new ProductHistorySalesContext
        {
            StoreCode = store.StoreCode?.Trim() ?? normalizedStoreCode,
            EndDate = TimeZoneInfo.ConvertTimeFromUtc(
                    _timeProvider.GetUtcNow().UtcDateTime,
                    timeZone
                )
                .Date,
        };
    }

    internal async Task<DateTime?> GetLatestArrivalDateAsync(
        string storeCode,
        string productCode,
        DateTime endDate
    )
    {
        var exclusiveEndDate = endDate.Date.AddDays(1);
        var row = await _db.Queryable<WareHouseOrderDetails>()
            .InnerJoin<WareHouseOrder>((detail, order) => detail.OrderGUID == order.OrderGUID)
            .Where((detail, order) =>
                order.StoreCode == storeCode
                && order.FlowStatus > 0
                && !order.IsDeleted
                && !detail.IsDeleted
                && order.OutboundDate != null
                && order.OutboundDate < exclusiveEndDate
                && detail.AllocQuantity > 0
                && detail.ProductCode == productCode
            )
            .OrderBy((detail, order) => order.OutboundDate, OrderByType.Desc)
            .Select((detail, order) => new ProductHistoryLastArrivalRow
            {
                ProductCode = detail.ProductCode,
                OutboundDate = order.OutboundDate,
            })
            .FirstAsync();
        return row?.OutboundDate;
    }

    /// <param name="includeArrivalOrder">
    /// 是否同时查出「最近一次送货的订单」（订货日期、订货数量、送货数量）。只有商品卡片用的 summary 接口需要；
    /// dynamic-data 接口不需要，关掉以保持它固定的查询次数。
    /// </param>
    internal async Task<ProductHistorySalesQuantityMapResult> GetSalesQuantitySinceLastArrivalMapAsync(
        string storeCode,
        List<string> productCodes,
        DateTime endDate,
        bool includeArrivalOrder = false
    )
    {
        var salesSw = Stopwatch.StartNew();
        var arrivalRowCount = 0;
        var cutoffGroupCount = 0;
        var statsQueryCount = 0;
        var salesRows = 0;
        try
        {
            var normalizedProductCodes = productCodes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (normalizedProductCodes.Count == 0)
            {
                return new ProductHistorySalesQuantityMapResult();
            }

            var exclusiveEndDate = endDate.Date.AddDays(1);
            var arrivalRows = await _db.Queryable<WareHouseOrderDetails>()
                .InnerJoin<WareHouseOrder>((detail, order) =>
                    detail.OrderGUID == order.OrderGUID
                )
                .Where((detail, order) =>
                    order.StoreCode == storeCode
                    && order.FlowStatus > 0
                    && !order.IsDeleted
                    && !detail.IsDeleted
                    && order.OutboundDate != null
                    && order.OutboundDate < exclusiveEndDate
                    && detail.AllocQuantity > 0
                    && detail.ProductCode != null
                    && normalizedProductCodes.Contains(detail.ProductCode)
                )
                .GroupBy((detail, order) => detail.ProductCode)
                .Select((detail, order) => new ProductHistoryLastArrivalRow
                {
                    ProductCode = detail.ProductCode,
                    OutboundDate = SqlFunc.AggregateMax(order.OutboundDate),
                })
                .ToListAsync();
            arrivalRowCount = arrivalRows.Count;

            var arrivalDateMap = arrivalRows
                .Where(item =>
                    !string.IsNullOrWhiteSpace(item.ProductCode) && item.OutboundDate.HasValue
                )
                .GroupBy(item => item.ProductCode!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().OutboundDate!.Value.Date,
                    StringComparer.OrdinalIgnoreCase
                );
            if (arrivalDateMap.Count == 0)
            {
                return new ProductHistorySalesQuantityMapResult
                {
                    ArrivalRows = arrivalRowCount,
                };
            }

            var cutoffGroups = arrivalDateMap
                .GroupBy(item => item.Value)
                .OrderBy(group => group.Key)
                .SelectMany(group =>
                    group
                        .Select(item => item.Key)
                        .Chunk(ProductHistoryRules.SalesStatisticsMaxProductCodesPerCutoffGroup)
                        .Select(codes => new ProductHistorySalesCutoffGroup
                        {
                            ArrivalDate = group.Key,
                            ProductCodes = codes.ToList(),
                        })
                )
                .ToList();
            cutoffGroupCount = cutoffGroups.Count;
            var statisticRows = new List<ProductHistoryProductSalesStatisticRow>();
            var arrivalQuantityRows = new List<ProductHistoryArrivalQuantityRow>();
            foreach (var queryGroups in PackSalesStatisticCutoffGroups(cutoffGroups))
            {
                // 来货单与销量共用同一批「商品 + 来货日」分组：来货日是该商品出库日期最大值截到天，
                // 所以「出库日期 >= 来货日」只会命中最近来货当天的出库单；按订单分组后在内存里挑出库最晚的一张。
                if (includeArrivalOrder)
                {
                    var arrivalExpressionable = Expressionable.Create<WareHouseOrderDetails, WareHouseOrder>();
                    foreach (var queryGroup in queryGroups)
                    {
                        var groupCodes = queryGroup.ProductCodes;
                        var arrivalDate = queryGroup.ArrivalDate;
                        arrivalExpressionable = arrivalExpressionable.Or((detail, order) =>
                            groupCodes.Contains(detail.ProductCode) && order.OutboundDate >= arrivalDate
                        );
                    }

                    var queryArrivalQuantityRows = await _db.Queryable<WareHouseOrderDetails>()
                        .InnerJoin<WareHouseOrder>((detail, order) =>
                            detail.OrderGUID == order.OrderGUID
                        )
                        .Where((detail, order) =>
                            order.StoreCode == storeCode
                            && order.FlowStatus > 0
                            && !order.IsDeleted
                            && !detail.IsDeleted
                            && order.OutboundDate != null
                            && order.OutboundDate < exclusiveEndDate
                            && detail.AllocQuantity > 0
                        )
                        .Where(arrivalExpressionable.ToExpression())
                        .GroupBy((detail, order) => new
                        {
                            detail.ProductCode,
                            order.OrderGUID,
                            order.OrderDate,
                            order.OutboundDate,
                        })
                        .Select((detail, order) => new ProductHistoryArrivalQuantityRow
                        {
                            ProductCode = detail.ProductCode,
                            OrderGUID = order.OrderGUID,
                            OrderDate = order.OrderDate,
                            OutboundDate = order.OutboundDate,
                            AllocQuantity = SqlFunc.AggregateSum(detail.AllocQuantity),
                            OrderQuantity = SqlFunc.AggregateSum(detail.Quantity),
                        })
                        .ToListAsync();
                    arrivalQuantityRows.AddRange(queryArrivalQuantityRows);
                }

                var expressionable = Expressionable.Create<ProductStoreDailySalesStatistic>();
                foreach (var queryGroup in queryGroups)
                {
                    var groupCodes = queryGroup.ProductCodes;
                    var arrivalDate = queryGroup.ArrivalDate;
                    expressionable = expressionable.Or(item =>
                        groupCodes.Contains(item.ProductCode) && item.Date >= arrivalDate
                    );
                }

                statsQueryCount++;
                var queryRows = await _db.Queryable<ProductStoreDailySalesStatistic>()
                    .Where(item => item.BranchCode == storeCode && item.Date <= endDate)
                    .Where(expressionable.ToExpression())
                    .GroupBy(item => item.ProductCode)
                    .Select(item => new ProductHistoryProductSalesStatisticRow
                    {
                        ProductCode = item.ProductCode,
                        TotalQuantity = SqlFunc.AggregateSum(item.TotalQuantity),
                    })
                    .ToListAsync();
                statisticRows.AddRange(queryRows);
            }

            var statisticQuantityMap = statisticRows
                .Where(item => !string.IsNullOrWhiteSpace(item.ProductCode))
                .GroupBy(item => item.ProductCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Sum(item => item.TotalQuantity),
                    StringComparer.OrdinalIgnoreCase
                );
            // 每个商品取「最近一次送货的订单」：出库时间最晚，同一时刻再按订货日期、订单号取最新，结果稳定。
            var arrivalOrderMap = arrivalQuantityRows
                .Where(item => !string.IsNullOrWhiteSpace(item.ProductCode))
                .GroupBy(item => item.ProductCode!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderByDescending(item => item.OutboundDate)
                        .ThenByDescending(item => item.OrderDate)
                        .ThenByDescending(item => item.OrderGUID, StringComparer.Ordinal)
                        .First(),
                    StringComparer.OrdinalIgnoreCase
                );
            var mapResult = new ProductHistorySalesQuantityMapResult
            {
                ArrivalRows = arrivalRowCount,
                CutoffGroupCount = cutoffGroupCount,
                StatsQueryCount = statsQueryCount,
            };
            foreach (var pair in arrivalDateMap)
            {
                mapResult.ArrivalDateMap[pair.Key] = pair.Value;
                arrivalOrderMap.TryGetValue(pair.Key, out var arrivalOrder);
                mapResult.ArrivalQuantityMap[pair.Key] = arrivalOrder?.AllocQuantity ?? 0m;
                mapResult.ArrivalOrderQuantityMap[pair.Key] = arrivalOrder?.OrderQuantity ?? 0m;
                if (arrivalOrder?.OrderDate is { } arrivalOrderDate)
                {
                    mapResult.ArrivalOrderDateMap[pair.Key] = arrivalOrderDate;
                }
                mapResult.SalesQuantityMap[pair.Key] = statisticQuantityMap.TryGetValue(
                    pair.Key,
                    out var salesQuantity
                )
                    ? salesQuantity
                    : 0;
            }

            salesRows = mapResult.SalesQuantityMap.Count;
            return mapResult;
        }
        finally
        {
            salesSw.Stop();
            logger.LogInformation(
                "[shop-home-perf] stage=sales-since-last-arrival.map requestCount={RequestCount} arrivalRows={ArrivalRows} cutoffGroupCount={CutoffGroupCount} statsQueryCount={StatsQueryCount} salesRows={SalesRows} salesMs={SalesMs}",
                productCodes.Count,
                arrivalRowCount,
                cutoffGroupCount,
                statsQueryCount,
                salesRows,
                salesSw.ElapsedMilliseconds
            );
        }
    }

    internal ISugarQueryable<ProductHistoryDailySalesStatisticRow> BuildDailySalesQuery(
        string storeCode,
        string productCode,
        DateTime startDate,
        DateTime endDate
    )
    {
        return _db.Queryable<ProductStoreDailySalesStatistic>()
            .Where(item =>
                item.BranchCode == storeCode
                && item.ProductCode == productCode
                && item.Date >= startDate
                && item.Date <= endDate
            )
            .GroupBy(item => item.Date)
            .Select(item => new ProductHistoryDailySalesStatisticRow
            {
                Date = item.Date,
                TotalQuantity = SqlFunc.AggregateSum(item.TotalQuantity),
                TotalAmount = SqlFunc.AggregateSum(item.TotalAmount),
            })
            .MergeTable();
    }

    internal async Task<int> GetTotalSalesQuantityAsync(
        string storeCode,
        string productCode,
        DateTime startDate,
        DateTime endDate
    )
    {
        return await _db.Queryable<ProductStoreDailySalesStatistic>()
            .Where(item =>
                item.BranchCode == storeCode
                && item.ProductCode == productCode
                && item.Date >= startDate
                && item.Date <= endDate
            )
            .SumAsync(item => item.TotalQuantity);
    }

    private string ResolveStoreTimeZoneForSales(ProductHistorySalesStoreRow store)
    {
        if (!string.IsNullOrWhiteSpace(store.TimeZoneId))
        {
            if (
                StoreTimeZonePolicy.TryNormalize(
                    store.TimeZoneId,
                    out var configuredTimeZone
                )
                && !string.IsNullOrWhiteSpace(configuredTimeZone)
            )
            {
                return configuredTimeZone;
            }

            logger.LogWarning(
                "门店 {StoreCode} 配置了不支持的销售统计时区 {TimeZoneId}，将按门店资料回退推导",
                store.StoreCode,
                store.TimeZoneId
            );
        }

        var postcode = PublicHolidaySyncHelper.ExtractPostcodeFromAddress(store.Address);
        var jurisdiction = PublicHolidaySyncHelper.ResolveJurisdictionFromPostcode(postcode);
        if (jurisdiction == "QLD")
        {
            return StoreTimeZonePolicy.Brisbane;
        }

        if (jurisdiction == "NSW")
        {
            return StoreTimeZonePolicy.Sydney;
        }

        if (
            int.TryParse(postcode, out var postcodeValue)
            && (
                (postcodeValue >= 3000 && postcodeValue <= 3999)
                || (postcodeValue >= 8000 && postcodeValue <= 8999)
            )
        )
        {
            return StoreTimeZonePolicy.Melbourne;
        }

        if (
            ContainsWholeToken(store.StoreCode, "BRI", "BRISBANE", "QLD", "QUEENSLAND")
            || ContainsWholeToken(
                $"{store.StoreName} {store.Address}",
                "BRISBANE",
                "QLD",
                "QUEENSLAND"
            )
        )
        {
            return StoreTimeZonePolicy.Brisbane;
        }

        var storeDetails = $"{store.StoreName} {store.Address}";
        if (
            ContainsWholeToken(store.StoreCode, "MEL", "MELBOURNE", "VIC", "VICTORIA")
            || ContainsWholeToken(storeDetails, "MELBOURNE", "VIC")
            || (
                ContainsWholeToken(storeDetails, "VICTORIA")
                && !ContainsWholeToken(
                    storeDetails,
                    "NSW",
                    "NEW SOUTH WALES",
                    "QLD",
                    "QUEENSLAND",
                    "SA",
                    "SOUTH AUSTRALIA",
                    "WA",
                    "WESTERN AUSTRALIA",
                    "TAS",
                    "TASMANIA",
                    "NT",
                    "NORTHERN TERRITORY",
                    "ACT"
                )
            )
        )
        {
            return StoreTimeZonePolicy.Melbourne;
        }

        return StoreTimeZonePolicy.Sydney;
    }

    private static bool ContainsWholeToken(string? text, params string[] candidates)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return candidates.Any(candidate =>
            Regex.IsMatch(
                text,
                $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(candidate)}(?![\p{{L}}\p{{N}}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            )
        );
    }

    private static TimeZoneInfo ResolveTimeZoneInfo(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(StoreTimeZonePolicy.Sydney);
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(StoreTimeZonePolicy.Sydney);
        }
    }

    private static List<List<ProductHistorySalesCutoffGroup>> PackSalesStatisticCutoffGroups(
        List<ProductHistorySalesCutoffGroup> cutoffGroups
    )
    {
        var batches = new List<List<ProductHistorySalesCutoffGroup>>();
        var currentBatch = new List<ProductHistorySalesCutoffGroup>();
        var currentProductCount = 0;
        foreach (var cutoffGroup in cutoffGroups)
        {
            var nextProductCount = currentProductCount + cutoffGroup.ProductCodes.Count;
            var nextGroupCount = currentBatch.Count + 1;
            var exceedsParameterBudget =
                nextProductCount + nextGroupCount + SalesStatisticsFixedParameterCount
                > SalesStatisticsParameterBudget;
            if (
                currentBatch.Count > 0
                && (
                    nextGroupCount > SalesStatisticsMaxCutoffGroupsPerQuery
                    || exceedsParameterBudget
                )
            )
            {
                batches.Add(currentBatch);
                currentBatch = new List<ProductHistorySalesCutoffGroup>();
                currentProductCount = 0;
            }

            currentBatch.Add(cutoffGroup);
            currentProductCount += cutoffGroup.ProductCodes.Count;
        }

        if (currentBatch.Count > 0)
        {
            batches.Add(currentBatch);
        }

        return batches;
    }
}
