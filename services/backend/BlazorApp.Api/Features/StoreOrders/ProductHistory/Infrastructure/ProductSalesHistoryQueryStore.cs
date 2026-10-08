using System.Diagnostics;
using System.Text.RegularExpressions;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.StoreOrders.ProductHistory.Domain;
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
            salesContext.EndDate
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

    internal async Task<ProductHistorySalesQuantityMapResult> GetSalesQuantitySinceLastArrivalMapAsync(
        string storeCode,
        List<string> productCodes,
        DateTime endDate
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
            foreach (var queryGroups in PackSalesStatisticCutoffGroups(cutoffGroups))
            {
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
            var mapResult = new ProductHistorySalesQuantityMapResult
            {
                ArrivalRows = arrivalRowCount,
                CutoffGroupCount = cutoffGroupCount,
                StatsQueryCount = statsQueryCount,
            };
            foreach (var pair in arrivalDateMap)
            {
                mapResult.ArrivalDateMap[pair.Key] = pair.Value;
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
