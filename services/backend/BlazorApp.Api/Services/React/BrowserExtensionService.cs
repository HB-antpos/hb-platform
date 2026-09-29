using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Models;
using BlazorApp.Api.Services.LocalSupplierCategories;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace BlazorApp.Api.Services.React;

public sealed class BrowserExtensionService : IBrowserExtensionService
{
    private readonly ISqlSugarClient _db;
    private readonly ISqlSugarClient _posmDb;
    private readonly IOptionsSnapshot<BrowserExtensionOptions> _options;
    private readonly ILogger<BrowserExtensionService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly BrowserExtensionRankingSnapshotCache _rankingCache;

    public BrowserExtensionService(
        SqlSugarContext context,
        POSMSqlSugarContext posmContext,
        IOptionsSnapshot<BrowserExtensionOptions> options,
        ILogger<BrowserExtensionService> logger,
        IMemoryCache memoryCache,
        TimeProvider? timeProvider = null
    )
    {
        _db = context.Db;
        _posmDb = posmContext.Db;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _rankingCache = new BrowserExtensionRankingSnapshotCache(memoryCache);
    }

    public BrowserExtensionReleaseDto GetRelease() =>
        BrowserExtensionProfileCatalog.BuildRelease(_options.Value);

    public BrowserExtensionSupplierProfilesDto GetSupplierProfiles() =>
        BrowserExtensionProfileCatalog.BuildProfiles(_options.Value);

    public async Task<BrowserExtensionProductSummaryBatchDto> GetProductSummariesAsync(
        BrowserExtensionProductSummaryBatchRequestDto request
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        var storeCode = NormalizeCode(request.StoreCode, "分店代码");
        var supplierCode = NormalizeCode(request.SupplierCode, "供应商代码");
        var salesRankingDays = BrowserExtensionRankingLogic.NormalizeSummaryDays(
            request.SalesRankingDays
        );
        EnsureSupplierEnabled(supplierCode);
        var itemNumbers = BrowserExtensionPurchaseCycleSqlBuilder.NormalizeItemNumbers(
            request.ItemNumbers
        );
        var today = await ResolveStoreTodayAsync(storeCode);
        var items = await QuerySummariesAsync(storeCode, supplierCode, itemNumbers, today);
        var rankingToday = ResolveRankingToday();
        var response = new BrowserExtensionProductSummaryBatchDto
        {
            StoreCode = storeCode,
            SupplierCode = supplierCode,
            EndDate = today,
            SalesRankingDays = salesRankingDays,
            SalesRankingStartDate = BrowserExtensionRankingLogic.ResolveStartDate(
                rankingToday,
                salesRankingDays
            ),
            SalesRankingEndDate = rankingToday,
            Items = items,
        };

        try
        {
            var ranking = await GetSupplierSalesRankingSnapshotAsync(
                supplierCode,
                salesRankingDays,
                rankingToday
            );
            response.SalesRankingAvailable = true;
            response.SalesRankingStartDate = ranking.StartDate;
            response.SalesRankingEndDate = ranking.EndDate;
            response.SalesRankingEnabledStoreCount = ranking.EnabledStoreCount;
            response.SalesRankingTotalProductCount = ranking.TotalProductCount;
            response.SalesRankingStatisticLastUpdate = ranking.SalesStatisticLastUpdate;
            BrowserExtensionRankingLogic.ApplySalesRankBands(
                response.Items,
                ranking.RankedTopThirty
            );
        }
        catch (Exception ex)
        {
            // 排名是摘要上的增强信息；失败时保留原有采购记录，避免整页按钮不可用。
            _logger.LogWarning(
                ex,
                "浏览器订货助手批量摘要销量排名降级 SupplierCode={SupplierCode} Days={Days}",
                supplierCode,
                salesRankingDays
            );
        }

        return response;
    }

    public async Task<BrowserExtensionPurchaseCyclesDto> GetPurchaseCyclesAsync(
        BrowserExtensionPurchaseCyclesRequestDto request
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        var storeCode = NormalizeCode(request.StoreCode, "分店代码");
        var supplierCode = NormalizeCode(request.SupplierCode, "供应商代码");
        EnsureSupplierEnabled(supplierCode);
        var itemNumber = BrowserExtensionPurchaseCycleSqlBuilder.NormalizeItemNumbers(
            new[] { request.ItemNumber }
        )[0];
        var today = await ResolveStoreTodayAsync(storeCode);
        var summary = (
            await QuerySummariesAsync(storeCode, supplierCode, new[] { itemNumber }, today)
        ).Single();

        var response = new BrowserExtensionPurchaseCyclesDto
        {
            StoreCode = storeCode,
            SupplierCode = supplierCode,
            ItemNumber = itemNumber,
            MatchStatus = summary.MatchStatus,
            ProductCode = summary.ProductCode,
            ProductName = summary.ProductName,
            EndDate = today,
            SalesStatisticLastUpdate = summary.SalesStatisticLastUpdate,
            LatestPurchaseDate = summary.LatestPurchaseDate,
            LatestPurchaseQuantity = summary.LatestPurchaseQuantity,
            SalesSinceLatestPurchase = summary.SalesSinceLatestPurchase,
        };

        if (summary.MatchStatus != BrowserExtensionMatchStatuses.Matched)
        {
            return response;
        }

        var purchaseQuery = BrowserExtensionPurchaseCycleSqlBuilder.BuildPurchaseLines(
            storeCode,
            supplierCode,
            itemNumber,
            today.AddMonths(-12),
            today
        );
        var purchaseRows = await _db.Ado.SqlQueryAsync<PurchaseSqlRow>(
            purchaseQuery.Sql,
            purchaseQuery.Parameters.ToArray()
        );
        var purchases = purchaseRows
            .Where(row => row.PurchaseDate.HasValue)
            .Select(row =>
                new BrowserExtensionPurchaseLine
                {
                    PurchaseDate = DateOnly.FromDateTime(row.PurchaseDate!.Value),
                    InvoiceNumber = row.InvoiceNumber,
                    Quantity = row.Quantity,
                    PurchasePrice = row.PurchasePrice,
                    Amount = row.Amount,
                    ProductCode = row.ProductCode,
                    ProductName = row.ProductName,
                }
            )
            .ToList();

        if (purchases.Count == 0)
        {
            return response;
        }

        var productCodes = purchases
            .Select(row => row.ProductCode)
            .Append(summary.ProductCode)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sales = new List<BrowserExtensionSalesLine>();
        if (productCodes.Count > 0)
        {
            var selectedPurchaseDates = purchases
                .Select(row => row.PurchaseDate)
                .Distinct()
                .OrderByDescending(value => value)
                .Take(BrowserExtensionPurchaseCycleCalculator.MaximumCycles)
                .ToList();
            var salesStartDate = selectedPurchaseDates.Min();
            var salesQuery = BrowserExtensionPurchaseCycleSqlBuilder.BuildSales(
                storeCode,
                supplierCode,
                productCodes,
                salesStartDate,
                today
            );
            var salesRows = await _db.Ado.SqlQueryAsync<SalesSqlRow>(
                salesQuery.Sql,
                salesQuery.Parameters.ToArray()
            );
            sales = salesRows
                .Where(row => row.Date.HasValue)
                .Select(row =>
                    new BrowserExtensionSalesLine
                    {
                        Date = DateOnly.FromDateTime(row.Date!.Value),
                        ProductCode = row.ProductCode,
                        Quantity = row.Quantity,
                        Amount = row.Amount,
                        StatisticLastUpdate = row.StatisticLastUpdate,
                    }
                )
                .ToList();
            response.SalesStatisticLastUpdate = sales
                .Select(row => row.StatisticLastUpdate)
                .Where(value => value.HasValue)
                .DefaultIfEmpty(summary.SalesStatisticLastUpdate)
                .Max();
        }

        response.Cycles = BrowserExtensionPurchaseCycleCalculator.Build(purchases, sales, today);
        return response;
    }

    public async Task<BrowserExtensionStoreOptionsDto> GetEnabledStoresAsync(
        IReadOnlyCollection<string> relatedStoreCodes
    )
    {
        var relatedCodes = (relatedStoreCodes ?? Array.Empty<string>())
            .Select(code => code?.Trim().ToUpperInvariant())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (relatedCodes.Count == 0)
        {
            return new BrowserExtensionStoreOptionsDto();
        }

        var enabledPosStoreCodes = await QueryEnabledPosStoreCodesAsync();
        var allowedStoreCodes = enabledPosStoreCodes
            .Where(relatedCodes.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowedStoreCodes.Count == 0)
        {
            return new BrowserExtensionStoreOptionsDto();
        }

        var stores = await _db.Queryable<Store>()
            .Where(store => store.IsActive && !store.IsDeleted)
            .ToListAsync();

        return new BrowserExtensionStoreOptionsDto
        {
            Stores = stores
                .Where(store => allowedStoreCodes.Contains(store.StoreCode?.Trim() ?? string.Empty))
                .OrderBy(store => store.StoreName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(store => store.StoreCode, StringComparer.OrdinalIgnoreCase)
                .Select(store => new BrowserExtensionStoreOptionDto
                {
                    StoreCode = store.StoreCode.Trim(),
                    StoreName = string.IsNullOrWhiteSpace(store.StoreName)
                        ? store.StoreCode.Trim()
                        : store.StoreName.Trim(),
                })
                .ToList(),
        };
    }

    public async Task<BrowserExtensionSupplierTopSalesDto> GetSupplierTopSalesAsync(
        BrowserExtensionSupplierTopSalesRequestDto request
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        var supplierCode = NormalizeCode(request.SupplierCode, "供应商代码");
        var days = BrowserExtensionRankingLogic.NormalizeDays(request.Days);
        var paging = BrowserExtensionRankingLogic.ResolveTopSalesPaging(
            request.TopPercent,
            request.Page,
            request.PageSize
        );
        EnsureSupplierEnabled(supplierCode);

        var today = ResolveRankingToday();
        var ranking = await GetSupplierSalesRankingSnapshotAsync(supplierCode, days, today);
        var response = new BrowserExtensionSupplierTopSalesDto
        {
            SupplierCode = supplierCode,
            SnapshotVersion = ranking.SnapshotVersion,
            Days = days,
            StartDate = ranking.StartDate,
            EndDate = ranking.EndDate,
            EnabledStoreCount = ranking.EnabledStoreCount,
            TotalProductCount = ranking.TotalProductCount,
            SalesStatisticLastUpdate = ranking.SalesStatisticLastUpdate,
            TopPercent = paging.TopPercent,
        };
        var topCount = BrowserExtensionRankingLogic.CalculateTopItemCount(
            ranking.TotalProductCount,
            paging.TopPercent
        );
        var ranked = ranking.RankedTopThirty.Take(topCount).ToList();
        response.TotalRankedCount = ranked.Count;
        if (!paging.IsLegacy)
        {
            var pageWindow = BrowserExtensionRankingLogic.ResolvePageWindow(
                paging.Page!.Value,
                paging.PageSize!.Value,
                response.TotalRankedCount
            );
            response.Page = pageWindow.Page;
            response.PageSize = pageWindow.PageSize;
            response.TotalPages = pageWindow.TotalPages;
            ranked = ranked.Skip(pageWindow.Skip).Take(pageWindow.PageSize).ToList();
        }

        if (ranked.Count == 0)
        {
            return response;
        }

        var productCodes = ranked.Select(row => row.ProductCode).ToList();
        var products = await _db.Queryable<Product>()
            .Where(product =>
                !product.IsDeleted
                && product.ProductCode != null
                && productCodes.Contains(product.ProductCode)
            )
            .ToListAsync();
        var productByCode = products
            .Where(product => !string.IsNullOrWhiteSpace(product.ProductCode))
            .GroupBy(product => product.ProductCode!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(product => product.UpdatedAt ?? product.CreatedAt)
                    .First(),
                StringComparer.OrdinalIgnoreCase
            );

        response.Items = ranked
            .Select(row =>
            {
                productByCode.TryGetValue(row.ProductCode, out var product);
                var itemNumber = product?.ItemNumber?.Trim();
                var productName = product?.ProductName?.Trim();
                return new BrowserExtensionSupplierTopSalesItemDto
                {
                    Rank = row.Rank,
                    ItemNumber = string.IsNullOrWhiteSpace(itemNumber)
                        ? row.ProductCode
                        : itemNumber,
                    ProductCode = row.ProductCode,
                    ProductName = !string.IsNullOrWhiteSpace(productName)
                        ? productName
                        : row.ProductName?.Trim() ?? row.ProductCode,
                    ImageUrl = product?.ProductImage?.Trim(),
                    SalesQuantity = row.SalesQuantity,
                    AverageSellingPrice = row.AverageSellingPrice,
                    SalesRankBand = row.SalesRankBand,
                };
            })
            .ToList();

        _logger.LogInformation(
            "浏览器订货助手供应商热销排行查询完成 SupplierCode={SupplierCode} Days={Days} StoreCount={StoreCount} ProductCount={ProductCount} TopPercent={TopPercent} TotalRankedCount={TotalRankedCount} Page={Page} PageSize={PageSize} TotalPages={TotalPages} ItemCount={ItemCount}",
            supplierCode,
            days,
            ranking.EnabledStoreCount,
            response.TotalProductCount,
            response.TopPercent,
            response.TotalRankedCount,
            response.Page,
            response.PageSize,
            response.TotalPages,
            response.Items.Count
        );
        return response;
    }

    public async Task<BrowserExtensionSupplierProductStoreSalesDto>
        GetSupplierProductStoreSalesAsync(
            BrowserExtensionSupplierProductStoreSalesRequestDto request
        )
    {
        ArgumentNullException.ThrowIfNull(request);
        var supplierCode = NormalizeCode(request.SupplierCode, "供应商代码");
        var productCode = NormalizeCode(request.ProductCode, "商品代码");
        var days = BrowserExtensionRankingLogic.NormalizeDays(request.Days);
        EnsureSupplierEnabled(supplierCode);

        var endDate = request.EndDate;
        var startDate = request.StartDate;
        var expectedStartDate = BrowserExtensionRankingLogic.ResolveStartDate(endDate, days);
        // 明细必须绑定用户点击时看到的排行榜快照；跨日或周期变化时让客户端刷新排行榜。
        if (
            endDate != ResolveRankingToday()
            || startDate == default
            || startDate != expectedStartDate
        )
        {
            throw new BrowserExtensionRankingSnapshotChangedException();
        }

        var snapshotContext = await GetRankingSnapshotContextAsync(
            supplierCode,
            days,
            startDate,
            endDate
        );
        EnsureRankingSnapshotVersion(snapshotContext.SnapshotVersion, request.SnapshotVersion);
        var enabledStoreCodes = snapshotContext.EnabledStoreCodes;
        var response = new BrowserExtensionSupplierProductStoreSalesDto
        {
            SupplierCode = supplierCode,
            ProductCode = productCode,
            SnapshotVersion = snapshotContext.SnapshotVersion,
            Days = days,
            StartDate = startDate,
            EndDate = endDate,
            EnabledStoreCount = enabledStoreCodes.Count,
        };
        if (enabledStoreCodes.Count == 0)
        {
            EnsureRankingSnapshotTotal(
                response.TotalSalesQuantity,
                request.ExpectedTotalSalesQuantity
            );
            await EnsureRankingSnapshotVersionCurrentAsync(
                supplierCode,
                days,
                startDate,
                endDate,
                request.SnapshotVersion
            );
            return response;
        }

        var startDateTime = startDate.ToDateTime(TimeOnly.MinValue);
        var endDateExclusive = endDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var salesRows = await _db.Queryable<ProductStoreDailySalesStatistic>()
            .Where(row =>
                enabledStoreCodes.Contains(row.BranchCode)
                && row.SupplierCode == supplierCode
                && row.ProductCode == productCode
                && row.Date >= startDateTime
                && row.Date < endDateExclusive
            )
            .GroupBy(row => row.BranchCode)
            .Select(row => new BrowserExtensionProductStoreSalesAggregate
            {
                StoreCode = row.BranchCode,
                SalesQuantity = SqlFunc.AggregateSum(row.TotalQuantity),
                SalesStatisticLastUpdate = SqlFunc.AggregateMax(row.UpdateTime),
            })
            .ToListAsync();
        var salesByStore = salesRows
            .Where(row => !string.IsNullOrWhiteSpace(row.StoreCode))
            .GroupBy(row => row.StoreCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(row => row.SalesQuantity),
                StringComparer.OrdinalIgnoreCase
            );
        var stores = await _db.Queryable<Store>()
            .Where(store =>
                store.IsActive
                && !store.IsDeleted
                && enabledStoreCodes.Contains(store.StoreCode)
            )
            .ToListAsync();
        var storeNames = stores
            .Where(store => !string.IsNullOrWhiteSpace(store.StoreCode))
            .GroupBy(store => store.StoreCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(store => store.StoreName?.Trim())
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? group.Key,
                StringComparer.OrdinalIgnoreCase
            );

        response.Stores = enabledStoreCodes
            .Select(storeCode => new BrowserExtensionSupplierProductStoreSalesItemDto
            {
                StoreCode = storeCode,
                StoreName = storeNames.GetValueOrDefault(storeCode, storeCode),
                SalesQuantity = salesByStore.GetValueOrDefault(storeCode, 0m),
            })
            .OrderByDescending(store => store.SalesQuantity)
            .ThenBy(store => store.StoreName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(store => store.StoreCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
        response.TotalSalesQuantity = response.Stores.Sum(store => store.SalesQuantity);
        EnsureRankingSnapshotTotal(
            response.TotalSalesQuantity,
            request.ExpectedTotalSalesQuantity
        );
        response.SalesStatisticLastUpdate = salesRows
            .Select(row => row.SalesStatisticLastUpdate)
            .Where(value => value.HasValue)
            .DefaultIfEmpty()
            .Max();
        // 查询期间刷新任务或启用门店发生变化时，拒绝返回混合快照。
        await EnsureRankingSnapshotVersionCurrentAsync(
            supplierCode,
            days,
            startDate,
            endDate,
            request.SnapshotVersion
        );

        _logger.LogInformation(
            "浏览器订货助手商品分店销量查询完成 SupplierCode={SupplierCode} ProductCode={ProductCode} Days={Days} StoreCount={StoreCount} TotalSalesQuantity={TotalSalesQuantity}",
            supplierCode,
            productCode,
            days,
            response.EnabledStoreCount,
            response.TotalSalesQuantity
        );
        return response;
    }

    private static void EnsureRankingSnapshotTotal(decimal actual, decimal expected)
    {
        if (actual != expected)
        {
            throw new BrowserExtensionRankingSnapshotChangedException();
        }
    }

    private static void EnsureRankingSnapshotVersion(string actual, string expected)
    {
        if (
            string.IsNullOrWhiteSpace(expected)
            || !string.Equals(actual, expected.Trim(), StringComparison.Ordinal)
        )
        {
            throw new BrowserExtensionRankingSnapshotChangedException();
        }
    }

    private async Task EnsureRankingSnapshotVersionCurrentAsync(
        string supplierCode,
        int days,
        DateOnly startDate,
        DateOnly endDate,
        string expectedSnapshotVersion
    )
    {
        var current = await GetRankingSnapshotContextAsync(
            supplierCode,
            days,
            startDate,
            endDate
        );
        EnsureRankingSnapshotVersion(current.SnapshotVersion, expectedSnapshotVersion);
    }

    private async Task<(
        List<string> EnabledStoreCodes,
        string SnapshotVersion
    )> GetRankingSnapshotContextAsync(
        string supplierCode,
        int days,
        DateOnly startDate,
        DateOnly endDate
    )
    {
        var enabledStoreCodes = await QueryEnabledPosStoreCodesAsync();
        var refreshStates = await QueryProductStoreDailyRefreshStatesAsync(startDate, endDate);
        var snapshotVersion = BuildSupplierSalesRankingCacheKey(
            supplierCode,
            days,
            startDate,
            endDate,
            enabledStoreCodes,
            refreshStates
        );
        return (enabledStoreCodes, snapshotVersion);
    }

    private async Task<BrowserExtensionSupplierSalesRankingSnapshot>
        GetSupplierSalesRankingSnapshotAsync(string supplierCode, int days, DateOnly today)
    {
        var startDate = BrowserExtensionRankingLogic.ResolveStartDate(today, days);
        var snapshotContext = await GetRankingSnapshotContextAsync(
            supplierCode,
            days,
            startDate,
            today
        );
        var enabledStoreCodes = snapshotContext.EnabledStoreCodes;
        if (enabledStoreCodes.Count == 0)
        {
            return new BrowserExtensionSupplierSalesRankingSnapshot
            {
                SupplierCode = supplierCode,
                SnapshotVersion = snapshotContext.SnapshotVersion,
                Days = days,
                StartDate = startDate,
                EndDate = today,
            };
        }

        var cacheKey = snapshotContext.SnapshotVersion;
        return await _rankingCache.GetOrCreateAsync(
            cacheKey,
            () =>
                QuerySupplierSalesRankingSnapshotAsync(
                    supplierCode,
                    days,
                    startDate,
                    today,
                    enabledStoreCodes,
                    snapshotContext.SnapshotVersion
                )
        );
    }

    private async Task<BrowserExtensionSupplierSalesRankingSnapshot>
        QuerySupplierSalesRankingSnapshotAsync(
            string supplierCode,
            int days,
            DateOnly startDate,
            DateOnly endDate,
            IReadOnlyList<string> enabledStoreCodes,
            string snapshotVersion
        )
    {
        var startDateTime = startDate.ToDateTime(TimeOnly.MinValue);
        var endDateExclusive = endDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var salesRows = await _db.Queryable<ProductStoreDailySalesStatistic>()
            .Where(row =>
                enabledStoreCodes.Contains(row.BranchCode)
                && row.SupplierCode == supplierCode
                && row.Date >= startDateTime
                && row.Date < endDateExclusive
            )
            .GroupBy(row => row.ProductCode)
            .Select(row => new BrowserExtensionSupplierSalesAggregate
            {
                ProductCode = row.ProductCode,
                ProductName = SqlFunc.AggregateMax(row.ProductName),
                SalesQuantity = SqlFunc.AggregateSum(row.TotalQuantity),
                SalesAmount = SqlFunc.AggregateSum(row.TotalAmount),
                SalesStatisticLastUpdate = SqlFunc.AggregateMax(row.UpdateTime),
            })
            .ToListAsync();
        var totalProductCount = salesRows.Count(row =>
            !string.IsNullOrWhiteSpace(row.ProductCode) && row.SalesQuantity > 0m
        );
        var salesStatisticLastUpdate = salesRows
            .Select(row => row.SalesStatisticLastUpdate)
            .Where(value => value.HasValue)
            .DefaultIfEmpty()
            .Max();

        return new BrowserExtensionSupplierSalesRankingSnapshot
        {
            SupplierCode = supplierCode,
            SnapshotVersion = snapshotVersion,
            Days = days,
            StartDate = startDate,
            EndDate = endDate,
            EnabledStoreCount = enabledStoreCodes.Count,
            TotalProductCount = totalProductCount,
            SalesStatisticLastUpdate = salesStatisticLastUpdate,
            RankedTopThirty = BrowserExtensionRankingLogic.RankTopPercent(
                salesRows,
                BrowserExtensionRankingLogic.MaximumTopPercent
            ),
        };
    }

    private async Task<List<BrowserExtensionRankingRefreshStateRow>>
        QueryProductStoreDailyRefreshStatesAsync(DateOnly startDate, DateOnly endDate)
    {
        var startDateTime = startDate.ToDateTime(TimeOnly.MinValue);
        var endDateExclusive = endDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return await _db.Queryable<SalesStatisticRefreshState>()
            .Where(state =>
                state.StatisticType == SalesStatisticType.ProductStoreDaily
                && state.Date >= startDateTime
                && state.Date < endDateExclusive
            )
            .OrderBy(state => state.Date)
            .Select(state => new BrowserExtensionRankingRefreshStateRow
            {
                Date = state.Date,
                Status = state.Status,
                LastSourceUploadTime = state.LastSourceUploadTime,
                LastAggregatedAtUtc = state.LastAggregatedAtUtc,
                CompletedAtUtc = state.CompletedAtUtc,
            })
            .ToListAsync();
    }

    private static string BuildSupplierSalesRankingCacheKey(
        string supplierCode,
        int days,
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyList<string> enabledStoreCodes,
        IReadOnlyList<BrowserExtensionRankingRefreshStateRow> refreshStates
    )
    {
        var source = new StringBuilder();
        AppendCacheKeyPart(source, supplierCode);
        AppendCacheKeyPart(source, days.ToString());
        AppendCacheKeyPart(source, startDate.DayNumber.ToString());
        AppendCacheKeyPart(source, endDate.DayNumber.ToString());
        AppendCacheKeyPart(source, enabledStoreCodes.Count.ToString());
        foreach (var storeCode in enabledStoreCodes)
        {
            AppendCacheKeyPart(source, storeCode);
        }

        AppendCacheKeyPart(source, refreshStates.Count.ToString());
        foreach (var state in refreshStates)
        {
            AppendCacheKeyPart(source, state.Date.Ticks.ToString());
            AppendCacheKeyPart(source, state.Status);
            AppendCacheKeyPart(source, state.LastSourceUploadTime?.Ticks.ToString());
            AppendCacheKeyPart(source, state.LastAggregatedAtUtc?.Ticks.ToString());
            AppendCacheKeyPart(source, state.CompletedAtUtc?.Ticks.ToString());
        }

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.ToString())));
        return $"browser-extension:supplier-sales-ranking:v1:{digest}";
    }

    private static void AppendCacheKeyPart(StringBuilder destination, string? value)
    {
        var normalized = value ?? string.Empty;
        destination.Append(normalized.Length).Append(':').Append(normalized).Append('|');
    }

    private async Task<List<BrowserExtensionProductSummaryDto>> QuerySummariesAsync(
        string storeCode,
        string supplierCode,
        IReadOnlyList<string> itemNumbers,
        DateOnly today
    )
    {
        var query = BrowserExtensionPurchaseCycleSqlBuilder.BuildSummary(
            storeCode,
            supplierCode,
            itemNumbers,
            today
        );
        var rows = await _db.Ado.SqlQueryAsync<SummarySqlRow>(
            query.Sql,
            query.Parameters.ToArray()
        );
        var byItemNumber = rows.ToDictionary(
            row => row.ItemNumber,
            StringComparer.OrdinalIgnoreCase
        );

        _logger.LogInformation(
            "浏览器订货助手批量摘要查询完成 StoreCode={StoreCode} SupplierCode={SupplierCode} ItemCount={ItemCount}",
            storeCode,
            supplierCode,
            itemNumbers.Count
        );

        return itemNumbers
            .Select(itemNumber =>
            {
                if (!byItemNumber.TryGetValue(itemNumber, out var row))
                {
                    return new BrowserExtensionProductSummaryDto { ItemNumber = itemNumber };
                }

                return new BrowserExtensionProductSummaryDto
                {
                    ItemNumber = row.ItemNumber,
                    MatchStatus = row.MatchStatus,
                    ProductCode = row.ProductCode,
                    ProductName = row.ProductName,
                    LatestPurchaseDate = row.LatestPurchaseDate.HasValue
                        ? DateOnly.FromDateTime(row.LatestPurchaseDate.Value)
                        : null,
                    LatestPurchaseQuantity = row.LatestPurchaseQuantity,
                    SalesSinceLatestPurchase = row.SalesSinceLatestPurchase,
                    SalesStatisticLastUpdate = row.SalesStatisticLastUpdate,
                };
            })
            .ToList();
    }

    private async Task<DateOnly> ResolveStoreTodayAsync(string storeCode)
    {
        var store = await _db.Queryable<Store>()
            .Where(item => item.StoreCode == storeCode && item.IsActive && !item.IsDeleted)
            .FirstAsync();
        if (store == null)
        {
            throw new KeyNotFoundException("分店不存在或已停用。");
        }

        var timeZoneId =
            StoreTimeZonePolicy.TryNormalize(store.TimeZoneId, out var configuredTimeZone)
            && !string.IsNullOrWhiteSpace(configuredTimeZone)
                ? configuredTimeZone
                : InstallmentOrderStoreTimeZoneResolver.Resolve(store);
        var timeZone = FindTimeZone(timeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), timeZone);
        return DateOnly.FromDateTime(localNow.DateTime);
    }

    private async Task<List<string>> QueryEnabledPosStoreCodesAsync()
    {
        var storeCodes = await _posmDb.Queryable<POSM_设备注册信息表>()
            .Where(device =>
                device.设备状态 == 1
                && device.设备类型 == "POS"
                && device.分店代码 != null
                && device.分店代码 != ""
            )
            .Select(device => device.分店代码)
            .ToListAsync();

        var enabledPosStoreCodes = storeCodes
            .Select(code => code?.Trim().ToUpperInvariant())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (enabledPosStoreCodes.Count == 0)
        {
            return new List<string>();
        }

        var activeStores = await _db.Queryable<Store>()
            .Where(store => store.IsActive && !store.IsDeleted)
            .Select(store => store.StoreCode)
            .ToListAsync();
        return activeStores
            .Select(code => code?.Trim().ToUpperInvariant())
            .Where(code => !string.IsNullOrWhiteSpace(code) && enabledPosStoreCodes.Contains(code))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private DateOnly ResolveRankingToday()
    {
        var timeZone = FindTimeZone(StoreTimeZonePolicy.Brisbane);
        var localNow = TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), timeZone);
        return DateOnly.FromDateTime(localNow.DateTime);
    }

    private void EnsureSupplierEnabled(string supplierCode)
    {
        if (!BrowserExtensionProfileCatalog.IsSupplierEnabled(_options.Value, supplierCode))
        {
            throw new KeyNotFoundException("供应商配置不存在或已停用。");
        }
    }

    private static string NormalizeCode(string value, string fieldName)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException($"{fieldName}不能为空。", nameof(value));
        }

        if (normalized.Length > 50)
        {
            throw new ArgumentException($"{fieldName}最长为 50 个字符。", nameof(value));
        }

        return normalized;
    }

    private static TimeZoneInfo FindTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(StoreTimeZonePolicy.Brisbane);
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(StoreTimeZonePolicy.Brisbane);
        }
    }

    private sealed class SummarySqlRow
    {
        public string ItemNumber { get; set; } = string.Empty;
        public string MatchStatus { get; set; } = BrowserExtensionMatchStatuses.Unmatched;
        public string? ProductCode { get; set; }
        public string? ProductName { get; set; }
        public DateTime? LatestPurchaseDate { get; set; }
        public decimal? LatestPurchaseQuantity { get; set; }
        public decimal SalesSinceLatestPurchase { get; set; }
        public DateTime? SalesStatisticLastUpdate { get; set; }
    }

    private sealed class BrowserExtensionRankingRefreshStateRow
    {
        public DateTime Date { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime? LastSourceUploadTime { get; set; }
        public DateTime? LastAggregatedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
    }

    private sealed class BrowserExtensionProductStoreSalesAggregate
    {
        public string StoreCode { get; set; } = string.Empty;
        public decimal SalesQuantity { get; set; }
        public DateTime? SalesStatisticLastUpdate { get; set; }
    }

    private sealed class PurchaseSqlRow
    {
        public DateTime? PurchaseDate { get; set; }
        public string? InvoiceNumber { get; set; }
        public decimal Quantity { get; set; }
        public decimal? PurchasePrice { get; set; }
        public decimal? Amount { get; set; }
        public string? ProductCode { get; set; }
        public string? ProductName { get; set; }
    }

    private sealed class SalesSqlRow
    {
        public DateTime? Date { get; set; }
        public string? ProductCode { get; set; }
        public decimal Quantity { get; set; }
        public decimal Amount { get; set; }
        public DateTime? StatisticLastUpdate { get; set; }
    }
}

public sealed class BrowserExtensionRankingSnapshotChangedException : Exception
{
    public BrowserExtensionRankingSnapshotChangedException()
        : base("排行榜数据已更新，请刷新排行榜后重试。") { }
}

public static partial class BrowserExtensionProfileCatalog
{
    public const string ExtensionVersionHeader = "X-HB-Extension-Version";
    public const string SupplierProfilesMinimumClientVersion = "1.1.0";
    public const string ExtendedProfilesMinimumClientVersion = "1.2.0";
    public const string CategoryProfilesMinimumClientVersion = "1.5.0";

    private static readonly HashSet<string> AllowedCategoryKeySources = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "pathname",
        "hash",
    };

    private static readonly HashSet<string> AllowedSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "attribute",
        "text",
    };

    private static readonly HashSet<string> AllowedTransforms = new(StringComparer.OrdinalIgnoreCase)
    {
        "trim",
        "uppercase",
        "lowercase",
        "after-colon",
        "underscore-to-slash",
        "after-sku",
    };

    private static readonly HashSet<string> AllowedMountPositions = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "beforebegin",
        "afterbegin",
        "beforeend",
        "afterend",
    };

    public static BrowserExtensionReleaseDto BuildRelease(BrowserExtensionOptions options) =>
        new()
        {
            LatestVersion = NormalizeVersion(options.LatestVersion, "1.0.0"),
            MinimumVersion = NormalizeVersion(options.MinimumVersion, "1.0.0"),
            ChromeStoreUrl = NormalizeHttpsUrl(options.ChromeStoreUrl),
            EdgeStoreUrl = NormalizeHttpsUrl(options.EdgeStoreUrl),
            SafariStoreUrl = NormalizeHttpsUrl(options.SafariStoreUrl),
            ReleaseNotes = new BrowserExtensionReleaseNotesDto
            {
                Zh = options.ReleaseNotesZh?.Trim() ?? string.Empty,
                En = options.ReleaseNotesEn?.Trim() ?? string.Empty,
            },
        };

    public static BrowserExtensionSupplierProfilesDto BuildProfiles(BrowserExtensionOptions options)
    {
        var configuredProfiles = options.SupplierProfiles
            ?? new List<BrowserExtensionSupplierProfileOptions>();
        var configuredCodes = configuredProfiles
            .Select(profile => profile.SupplierCode?.Trim())
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var builtInProfiles = new List<BrowserExtensionSupplierProfileOptions>();
        if (options.UseBuiltInDatsProfile)
        {
            builtInProfiles.Add(BrowserExtensionSupplierProfileOptions.CreateDatsDefault());
        }
        if (options.UseBuiltInSupplierProfiles)
        {
            builtInProfiles.AddRange(BrowserExtensionSupplierProfileOptions.CreateSupplierDefaults());
        }

        // 同业务供应商代码的后台配置优先；配置为 disabled 可立即停用有问题的内置规则。
        var candidates = builtInProfiles
            .Where(profile => !configuredCodes.Contains(profile.SupplierCode))
            .Concat(configuredProfiles);

        var profiles = candidates
            .Where(profile => profile.Enabled)
            .Select(TryBuildProfile)
            .Where(profile => profile != null)
            .Cast<BrowserExtensionSupplierProfileDto>()
            .GroupBy(profile => profile.SupplierCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (!options.CategoryCaptureEnabled)
        {
            // 总开关关闭时不下发任何分类配置，扩展随下一次配置同步停止采集。
            foreach (var profile in profiles)
            {
                profile.Category = null;
            }
        }

        return new BrowserExtensionSupplierProfilesDto
        {
            ConfigVersion = string.IsNullOrWhiteSpace(options.ConfigVersion)
                ? "1"
                : options.ConfigVersion.Trim(),
            Profiles = profiles,
        };
    }

    /// <summary>
    /// 按业务供应商代码查找当前生效的 profile（已合并内置与后台配置、已校验）。
    /// </summary>
    public static BrowserExtensionSupplierProfileDto? FindProfile(
        BrowserExtensionOptions options,
        string? supplierCode
    )
    {
        if (string.IsNullOrWhiteSpace(supplierCode))
        {
            return null;
        }

        var normalized = supplierCode.Trim();
        return BuildProfiles(options)
            .Profiles.FirstOrDefault(profile =>
                profile.SupplierCode.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            );
    }

    public static bool IsSupplierEnabled(BrowserExtensionOptions options, string? supplierCode) =>
        FindProfile(options, supplierCode) != null;

    /// <summary>
    /// 判断绝对 URL 的来源是否落在 profile 声明的供应商 origin 内（与扩展 host 授权同一口径）。
    /// </summary>
    public static bool UrlMatchesProfileOrigins(
        BrowserExtensionSupplierProfileDto profile,
        string? url
    )
    {
        if (
            !Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        )
        {
            return false;
        }

        var origin = $"{uri.Scheme}://{uri.Authority}/";
        return profile.Origins.Any(pattern =>
        {
            var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return Regex.IsMatch(
                origin,
                regex,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(50)
            );
        });
    }

    public static BrowserExtensionSupplierProfilesDto FilterProfilesForClient(
        BrowserExtensionSupplierProfilesDto profiles,
        string? extensionVersion
    )
    {
        if (IsVersionAtLeast(extensionVersion, CategoryProfilesMinimumClientVersion))
        {
            return profiles;
        }

        profiles = StripCategoryProfiles(profiles);
        if (IsVersionAtLeast(extensionVersion, ExtendedProfilesMinimumClientVersion))
        {
            return profiles;
        }

        if (IsVersionAtLeast(extensionVersion, SupplierProfilesMinimumClientVersion))
        {
            // 1.1.x 不认识新增转换，也没有 TXK 的精确 HTTP host permission。
            return new BrowserExtensionSupplierProfilesDto
            {
                ConfigVersion = profiles.ConfigVersion,
                Profiles = profiles.Profiles
                    .Where(profile =>
                        profile.Origins.All(origin => origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        && profile.ItemNumber.Transforms.All(transform =>
                            !transform.Equals("underscore-to-slash", StringComparison.OrdinalIgnoreCase)
                            && !transform.Equals("after-sku", StringComparison.OrdinalIgnoreCase)
                        )
                    )
                    .ToList(),
            };
        }

        // 1.0.x 不认识 after-colon；仅返回旧客户端可安全校验的 DATS，避免整份目录失效。
        return new BrowserExtensionSupplierProfilesDto
        {
            ConfigVersion = profiles.ConfigVersion,
            Profiles = profiles.Profiles
                .Where(profile => profile.SupplierCode == "240")
                .ToList(),
        };
    }

    /// <summary>
    /// 1.5.0 之前的扩展不认识分类配置；虽然旧版校验会忽略未知字段，仍剥离以保持载荷最小。
    /// </summary>
    private static BrowserExtensionSupplierProfilesDto StripCategoryProfiles(
        BrowserExtensionSupplierProfilesDto profiles
    ) =>
        new()
        {
            ConfigVersion = profiles.ConfigVersion,
            Profiles = profiles.Profiles
                .Select(profile => new BrowserExtensionSupplierProfileDto
                {
                    SupplierCode = profile.SupplierCode,
                    DisplayName = profile.DisplayName,
                    Enabled = profile.Enabled,
                    Origins = profile.Origins,
                    ListPagePatterns = profile.ListPagePatterns,
                    CardSelector = profile.CardSelector,
                    ItemNumber = profile.ItemNumber,
                    MountSelector = profile.MountSelector,
                    MountPosition = profile.MountPosition,
                    Category = null,
                })
                .ToList(),
        };

    private static bool IsVersionAtLeast(string? value, string minimum)
    {
        return Version.TryParse(value?.Trim(), out var version)
            && Version.TryParse(minimum, out var minimumVersion)
            && version >= minimumVersion;
    }

    private static BrowserExtensionSupplierProfileDto? TryBuildProfile(
        BrowserExtensionSupplierProfileOptions options
    )
    {
        var supplierCode = options.SupplierCode?.Trim().ToUpperInvariant();
        var source = options.ItemNumberSource?.Trim().ToLowerInvariant();
        var origins = (options.Origins ?? new List<string>())
            .Select(value => value?.Trim())
            .Where(value => IsSafeMatchPattern(value, originOnly: true))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var pagePatterns = (options.ListPagePatterns ?? new List<string>())
            .Select(value => value?.Trim())
            .Where(value => IsSafeMatchPattern(value, originOnly: false))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var transforms = (options.ItemNumberTransforms ?? new List<string>())
            .Select(value => value?.Trim().ToLowerInvariant())
            .Where(value => value != null && AllowedTransforms.Contains(value))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var mountPosition = options.MountPosition?.Trim().ToLowerInvariant();

        if (
            string.IsNullOrWhiteSpace(supplierCode)
            || supplierCode.Length > 50
            || string.IsNullOrWhiteSpace(source)
            || !AllowedSources.Contains(source)
            || origins.Count == 0
            || pagePatterns.Count == 0
            || !IsSafeSelector(options.CardSelector)
            || !IsSafeSelector(options.MountSelector)
            || string.IsNullOrWhiteSpace(mountPosition)
            || !AllowedMountPositions.Contains(mountPosition)
            || (
                source.Equals("attribute", StringComparison.OrdinalIgnoreCase)
                && !IsSafeAttributeName(options.ItemNumberAttribute)
            )
            || !IsSafeSelector(options.ItemNumberSelector, allowEmpty: true)
        )
        {
            return null;
        }

        return new BrowserExtensionSupplierProfileDto
        {
            SupplierCode = supplierCode,
            DisplayName = string.IsNullOrWhiteSpace(options.DisplayName)
                ? supplierCode
                : options.DisplayName.Trim(),
            Enabled = true,
            Origins = origins,
            ListPagePatterns = pagePatterns,
            CardSelector = options.CardSelector.Trim(),
            ItemNumber = new BrowserExtensionItemNumberRuleDto
            {
                Source = source,
                Selector = string.IsNullOrWhiteSpace(options.ItemNumberSelector)
                    ? null
                    : options.ItemNumberSelector.Trim(),
                Attribute = string.IsNullOrWhiteSpace(options.ItemNumberAttribute)
                    ? null
                    : options.ItemNumberAttribute.Trim(),
                Transforms = transforms,
            },
            MountSelector = options.MountSelector.Trim(),
            MountPosition = mountPosition,
            Category = TryBuildCategory(options.Category, supplierCode, origins),
        };
    }

    /// <summary>
    /// 校验并构建分类采集配置。任何字段非法只关闭该供应商的分类采集（返回 null），不影响按钮注入 profile。
    /// 供应商 200（Hot Bargain）的分类即仓库分类，永不下发采集配置。
    /// </summary>
    internal static BrowserExtensionSupplierCategoryProfileDto? TryBuildCategory(
        BrowserExtensionSupplierCategoryOptions? options,
        string supplierCode,
        IReadOnlyList<string> origins
    )
    {
        if (
            options == null
            || !options.Enabled
            || LocalSupplierCategoryConstants.IsHotBargain(supplierCode)
        )
        {
            return null;
        }

        var pagePatterns = NormalizePagePatterns(options.CategoryPagePatterns);
        var excludePatterns = NormalizePagePatterns(options.CategoryExcludePatterns);
        var keyQueryParams = (options.KeyQueryParams ?? new List<string>())
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var promotionalPatterns = (options.PromotionalPatterns ?? new List<string>())
            .Select(value => value?.Trim().ToLowerInvariant())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var keySource = options.KeySource?.Trim().ToLowerInvariant() ?? "pathname";
        var navRootUrl = options.NavRootUrl?.Trim();

        if (
            pagePatterns == null
            || excludePatterns == null
            || pagePatterns.Count > 20
            || excludePatterns.Count > 20
            || !AllowedCategoryKeySources.Contains(keySource)
            || keyQueryParams.Count > 5
            || keyQueryParams.Any(value => !CategoryQueryParamRegex().IsMatch(value))
            || promotionalPatterns.Count > LocalSupplierCategoryPromotionRule.MaxPatternCount
            || promotionalPatterns.Any(value => !LocalSupplierCategoryPromotionRule.IsSafePattern(value))
            || !IsSafeSelector(options.BreadcrumbSelector, allowEmpty: true)
            || !IsSafeSelector(options.TitleSelector, allowEmpty: true)
            || !IsSafeSelector(options.NavSelector, allowEmpty: true)
            || !IsSafeSelector(options.SubcategoryLinkSelector, allowEmpty: true)
            || !IsSafeSelector(options.PaginationNextSelector, allowEmpty: true)
            || options.BreadcrumbSkip is < 0 or > 5
            || options.MaxPages is < 1 or > 50
            || options.MaxDepth is < 1 or > 6
            || options.MaxCategories is < 10 or > 2000
            || options.CrawlDelayMs is < 500 or > 15000
            || !IsSafeNavRootUrl(navRootUrl, origins)
        )
        {
            return null;
        }

        return new BrowserExtensionSupplierCategoryProfileDto
        {
            Enabled = true,
            PassiveEnabled = options.PassiveEnabled,
            CrawlEnabled = options.CrawlEnabled,
            CategoryPagePatterns = pagePatterns,
            CategoryExcludePatterns = excludePatterns,
            BreadcrumbSelector = NullIfBlank(options.BreadcrumbSelector),
            BreadcrumbSkip = options.BreadcrumbSkip,
            TitleSelector = NullIfBlank(options.TitleSelector),
            KeySource = keySource,
            KeyQueryParams = keyQueryParams,
            NavRootUrl = string.IsNullOrWhiteSpace(navRootUrl) ? null : navRootUrl,
            NavSelector = NullIfBlank(options.NavSelector),
            SubcategoryLinkSelector = NullIfBlank(options.SubcategoryLinkSelector),
            PaginationNextSelector = NullIfBlank(options.PaginationNextSelector),
            MaxPages = options.MaxPages,
            MaxDepth = options.MaxDepth,
            MaxCategories = options.MaxCategories,
            CrawlDelayMs = options.CrawlDelayMs,
            PromotionalPatterns = promotionalPatterns,
        };
    }

    private static List<string>? NormalizePagePatterns(IEnumerable<string>? values)
    {
        var result = new List<string>();
        foreach (var raw in values ?? Array.Empty<string>())
        {
            var value = raw?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            // 分类页模式允许 "/" 开头的路径通配，或与 listPagePatterns 相同的完整 match pattern。
            var isPathGlob = value.StartsWith('/') && value.Length <= 300 && !value.Any(char.IsWhiteSpace);
            if (!isPathGlob && !IsSafeMatchPattern(value, originOnly: false))
            {
                return null;
            }

            if (!result.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static bool IsSafeNavRootUrl(string? value, IReadOnlyList<string> origins)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (value.Length > 300 || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        // 绝对地址必须落在该供应商已声明的 origin 内，且不得携带账号信息。
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        return UrlMatchesProfileOrigins(
            new BrowserExtensionSupplierProfileDto { Origins = origins.ToList() },
            uri.AbsoluteUri
        );
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsSafeMatchPattern(string? value, bool originOnly)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 300)
        {
            return false;
        }

        var match = HttpsMatchPatternRegex().Match(value);
        if (!match.Success)
        {
            // 供应商目前只提供 HTTP；仅允许这个经过核验的精确主机，不开放 HTTP 通配。
            match = TxkHttpMatchPatternRegex().Match(value);
        }
        if (!match.Success)
        {
            return false;
        }

        return !originOnly || match.Groups["path"].Value == "/*";
    }

    private static bool IsSafeSelector(string? value, bool allowEmpty = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return allowEmpty;
        }

        return value.Length <= 500 && !value.Contains('\0') && !value.Contains('\n');
    }

    private static bool IsSafeAttributeName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 100
        && AttributeNameRegex().IsMatch(value);

    private static string NormalizeVersion(string? value, string fallback)
    {
        var normalized = value?.Trim();
        return normalized != null && VersionRegex().IsMatch(normalized) ? normalized : fallback;
    }

    private static string NormalizeHttpsUrl(string? value)
    {
        var normalized = value?.Trim();
        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : string.Empty;
    }

    [GeneratedRegex(@"^https://(?<host>(?:\*\.)?[A-Za-z0-9.-]+)(?::\d+)?(?<path>/[^\s]*)$")]
    private static partial Regex HttpsMatchPatternRegex();

    [GeneratedRegex(@"^http://txkorders\.inzantsales\.com(?<path>/[^\s]*)$", RegexOptions.IgnoreCase)]
    private static partial Regex TxkHttpMatchPatternRegex();

    [GeneratedRegex(@"^[A-Za-z_:][A-Za-z0-9_:.-]*$")]
    private static partial Regex AttributeNameRegex();

    [GeneratedRegex(@"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_\-\[\]]{1,50}$")]
    private static partial Regex CategoryQueryParamRegex();
}
