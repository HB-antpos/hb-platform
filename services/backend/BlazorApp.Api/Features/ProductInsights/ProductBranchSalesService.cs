using BlazorApp.Api.Data;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.POSM;
using SqlSugar;

namespace BlazorApp.Api.Features.ProductInsights;

public interface IProductBranchSalesService
{
    Task<ProductInsightBranchSalesDto> GetAsync(
        string productCode,
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyCollection<string>? authorizedStoreCodes,
        CancellationToken cancellationToken = default
    );
}

/// <summary>以已启用 POS 设备对应的活跃门店为全集，读取商品日销售统计。</summary>
public sealed class ProductBranchSalesService(
    SqlSugarContext context,
    POSMSqlSugarContext posmContext,
    StoreProductInsightQueryService storeInsightQuery,
    TimeProvider timeProvider
) : IProductBranchSalesService
{
    private readonly ISqlSugarClient _db = context.Db;
    private readonly ISqlSugarClient _posmDb = posmContext.Db;

    public async Task<ProductInsightBranchSalesDto> GetAsync(
        string productCode,
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyCollection<string>? authorizedStoreCodes,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedProductCode = NormalizeRequired(productCode, "商品代码");
        if (endDate < startDate)
            throw new ArgumentException("结束日期不能早于开始日期。", nameof(endDate));

        var allPosStores = await QueryEnabledPosStoresAsync(cancellationToken);
        var fullScope = authorizedStoreCodes == null;
        var includedStores = fullScope
            ? allPosStores
            : FilterAuthorizedStores(allPosStores, authorizedStoreCodes!);

        var result = new ProductInsightBranchSalesDto
        {
            ProductCode = normalizedProductCode,
            Range = new ProductInsightBranchSalesRangeDto
            {
                StartDate = startDate.ToString("yyyy-MM-dd"),
                EndDate = endDate.ToString("yyyy-MM-dd"),
            },
            // 此字段表示响应生成时间；统计实际更新时间另行如实返回，不能冒充为同一时间。
            GeneratedAt = timeProvider.GetUtcNow().UtcDateTime,
            Scope = fullScope ? "all-pos" : "authorized-pos",
            TotalPosStoreCount = allPosStores.Count,
            IncludedStoreCount = includedStores.Count,
        };
        if (includedStores.Count == 0)
            return result;

        var start = startDate.ToDateTime(TimeOnly.MinValue);
        var endExclusive = endDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var storeCodes = includedStores.Select(store => store.StoreCode).ToList();
        var aggregates = await _db.Queryable<ProductStoreDailySalesStatistic>()
            .Where(row =>
                storeCodes.Contains(row.BranchCode)
                && row.ProductCode == normalizedProductCode
                && row.Date >= start
                && row.Date < endExclusive
            )
            .GroupBy(row => row.BranchCode)
            .Select(row => new ProductBranchSalesAggregate
            {
                StoreCode = row.BranchCode,
                Quantity = SqlFunc.AggregateSum(row.TotalQuantity),
                Amount = SqlFunc.AggregateSum(row.TotalAmount),
                SalesStatisticLastUpdatedAt = SqlFunc.AggregateMax(row.UpdateTime),
            })
            .ToListAsync();
        // 进货量复用单店进销页的查询口径，保证同一分店两处看到的进货数一致。
        var purchases = await storeInsightQuery.GetBranchPurchasesAsync(
            normalizedProductCode,
            storeCodes,
            start,
            endDate.ToDateTime(TimeOnly.MinValue),
            cancellationToken
        );
        result.PurchaseSource = purchases.SourceType;

        var byStore = aggregates
            .Where(row => !string.IsNullOrWhiteSpace(row.StoreCode))
            .GroupBy(row => row.StoreCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new ProductBranchSalesAggregate
                {
                    StoreCode = group.Key,
                    Quantity = group.Sum(row => row.Quantity),
                    Amount = group.Sum(row => row.Amount),
                    SalesStatisticLastUpdatedAt = group
                        .Where(row => row.SalesStatisticLastUpdatedAt.HasValue)
                        .Select(row => row.SalesStatisticLastUpdatedAt)
                        .Max(),
                },
                StringComparer.OrdinalIgnoreCase
            );

        result.Rows = includedStores
            .Select(store =>
            {
                byStore.TryGetValue(store.StoreCode, out var sale);
                return new ProductInsightBranchSalesRowDto
                {
                    StoreCode = store.StoreCode,
                    StoreName = store.StoreName,
                    Quantity = sale?.Quantity ?? 0,
                    Amount = sale?.Amount ?? 0m,
                    PurchaseQuantity = purchases.QuantityByStore.GetValueOrDefault(store.StoreCode),
                };
            })
            .OrderByDescending(row => row.Quantity)
            .ThenByDescending(row => row.Amount)
            .ThenBy(row => row.StoreName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.StoreCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
        result.Quantity = result.Rows.Sum(row => row.Quantity);
        result.Amount = result.Rows.Sum(row => row.Amount);
        result.PurchaseQuantity = result.Rows.Sum(row => row.PurchaseQuantity);
        result.SalesStatisticLastUpdatedAt = aggregates
            .Where(row => row.SalesStatisticLastUpdatedAt.HasValue)
            .Select(row => row.SalesStatisticLastUpdatedAt)
            .Max();
        return result;
    }

    private async Task<List<ProductBranchSalesStore>> QueryEnabledPosStoresAsync(
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var posStoreCodes = await _posmDb.Queryable<POSM_设备注册信息表>()
            .Where(device =>
                device.设备状态 == 1
                && device.设备类型 == "POS"
                && device.分店代码 != null
                && device.分店代码 != ""
            )
            .Select(device => device.分店代码)
            .ToListAsync();
        var canonicalPosCodes = posStoreCodes
            .Select(NormalizeOptional)
            .Where(code => code != null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (canonicalPosCodes.Count == 0)
            return new();

        var activeStores = await _db.Queryable<Store>()
            .Where(store => store.IsActive && !store.IsDeleted)
            .Select(store => new { store.StoreCode, store.StoreName })
            .ToListAsync();
        return activeStores
            .Select(store => new ProductBranchSalesStore
            {
                StoreCode = NormalizeOptional(store.StoreCode) ?? string.Empty,
                StoreName = string.IsNullOrWhiteSpace(store.StoreName) ? store.StoreCode : store.StoreName.Trim(),
            })
            .Where(store => store.StoreCode.Length > 0 && canonicalPosCodes.Contains(store.StoreCode))
            .GroupBy(store => store.StoreCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(store => store.StoreName, StringComparer.OrdinalIgnoreCase).First())
            .OrderBy(store => store.StoreCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<ProductBranchSalesStore> FilterAuthorizedStores(
        IEnumerable<ProductBranchSalesStore> allPosStores,
        IReadOnlyCollection<string> authorizedStoreCodes
    )
    {
        var authorized = authorizedStoreCodes
            .Select(NormalizeOptional)
            .Where(code => code != null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return allPosStores.Where(store => authorized.Contains(store.StoreCode)).ToList();
    }

    private static string NormalizeRequired(string value, string label) =>
        NormalizeOptional(value) ?? throw new ArgumentException($"{label}不能为空。", nameof(value));

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private sealed class ProductBranchSalesAggregate
    {
        public string StoreCode { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal Amount { get; set; }
        public DateTime? SalesStatisticLastUpdatedAt { get; set; }
    }

    private sealed class ProductBranchSalesStore
    {
        public string StoreCode { get; set; } = string.Empty;
        public string StoreName { get; set; } = string.Empty;
    }
}
