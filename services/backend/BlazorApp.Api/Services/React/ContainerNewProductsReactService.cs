using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.Attendance;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using SqlSugar;
using System.Text.RegularExpressions;

namespace BlazorApp.Api.Services.React;

public sealed class ContainerNewProductsReactService(
    SqlSugarContext context,
    ICurrentUserManageableStoreScopeService storeScope,
    ICurrentUserService currentUser
) : IContainerNewProductsReactService
{
    private readonly ISqlSugarClient _db = context.Db;

    public async Task<ContainerNewProductsResponseDto> GetAsync(
        string storeCode,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedStoreCode = storeCode.Trim();
        if (!await CanAccessStoreCodeAsync(normalizedStoreCode))
        {
            throw new ContainerNewProductsForbiddenException();
        }

        var store = await _db.Queryable<Store>()
            .Where(x => !x.IsDeleted && x.StoreCode == normalizedStoreCode)
            .FirstAsync(cancellationToken);
        if (store == null)
        {
            throw new ContainerNewProductsStoreNotFoundException();
        }

        var stateCode = ResolveState(store);
        if (stateCode == null)
        {
            throw new ContainerNewProductsStateUnknownException();
        }

        var localToday = GetLocalToday(stateCode);
        var (from, toExclusive) = BuildWindow(localToday);
        var containers = await _db.Queryable<Container>()
            .Where(x => !x.IsDeleted)
            .Where(x =>
                (x.ActualArrivalDate != null && x.ActualArrivalDate >= from && x.ActualArrivalDate < toExclusive)
                || (x.ActualArrivalDate == null && x.EstimatedArrivalDate != null && x.EstimatedArrivalDate >= from && x.EstimatedArrivalDate < toExclusive))
            .Select(x => new ContainerDateRow
            {
                ContainerCode = x.ContainerCode,
                ContainerNumber = x.ContainerNumber,
                ActualArrivalDate = x.ActualArrivalDate,
                EstimatedArrivalDate = x.EstimatedArrivalDate,
            })
            .ToListAsync(cancellationToken);

        if (containers.Count == 0)
        {
            return new ContainerNewProductsResponseDto { StoreCode = normalizedStoreCode, StateCode = stateCode };
        }

        var containerCodes = containers.Select(x => x.ContainerCode).ToList();
        var details = await _db.Queryable<ContainerDetail>()
            .Where(x => !x.IsDeleted && x.ProductCode != null && containerCodes.Contains(x.ContainerCode))
            .LeftJoin<DomesticProduct>((detail, product) => detail.ProductCode == product.ProductCode)
            .Select((detail, product) => new DetailRow
            {
                ContainerCode = detail.ContainerCode,
                ProductCode = detail.ProductCode!,
                ImageUrl = product.ProductImage,
            })
            .ToListAsync(cancellationToken);
        details = details.Where(x => HasUsableProductCode(x.ProductCode)).ToList();

        var productCodes = details.Select(x => x.ProductCode).Distinct().ToList();
        if (productCodes.Count == 0)
        {
            return new ContainerNewProductsResponseDto { StoreCode = normalizedStoreCode, StateCode = stateCode };
        }
        var existingProducts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var historyKeys = new HashSet<HistoryKey>(HistoryKey.Comparer);
        foreach (var productCodeBatch in productCodes.Chunk(500))
        {
            foreach (var code in await _db.Queryable<WarehouseProduct>()
                .Where(x => productCodeBatch.Contains(x.ProductCode))
                .Select(x => x.ProductCode)
                .ToListAsync(cancellationToken)) existingProducts.Add(code);

            foreach (var row in await _db.Queryable<WarehouseProductChangeHistory>()
                .Where(x => productCodeBatch.Contains(x.ProductCode) && x.Action == "Create" && x.Source == "ContainerSubmit" && x.SourceReference != null && containerCodes.Contains(x.SourceReference))
                .Select(x => new HistoryRow { ProductCode = x.ProductCode, ContainerCode = x.SourceReference! })
                .ToListAsync(cancellationToken)) historyKeys.Add(new HistoryKey(row.ProductCode, row.ContainerCode));
        }

        var containerByCode = containers.ToDictionary(x => x.ContainerCode, StringComparer.OrdinalIgnoreCase);
        var items = new List<ContainerNewProductItemDto>();
        foreach (var detail in details.GroupBy(x => (x.ContainerCode, x.ProductCode)).Select(x => x.First()))
        {
            if (!ShouldIncludeProduct(
                    existingProducts.Contains(detail.ProductCode),
                    historyKeys.Contains(new HistoryKey(detail.ProductCode, detail.ContainerCode))))
            {
                continue;
            }

            var container = containerByCode[detail.ContainerCode];
            var baseDate = (container.ActualArrivalDate ?? container.EstimatedArrivalDate)!.Value.Date;
            items.Add(new ContainerNewProductItemDto
            {
                ProductCode = detail.ProductCode,
                ImageUrl = ProductImageUrlHelper.EnsureImageUrl(detail.ImageUrl, detail.ProductCode),
                ContainerCode = detail.ContainerCode,
                ContainerNumber = container.ContainerNumber,
                EstimatedStoreArrivalDate = DateOnly.FromDateTime(AddWeekdays(baseDate, stateCode == "NSW" ? 3 : 7)),
                Basis = container.ActualArrivalDate.HasValue ? "actual" : "estimated",
            });
        }

        return new ContainerNewProductsResponseDto
        {
            StoreCode = normalizedStoreCode,
            StateCode = stateCode,
            Items = items.OrderBy(x => x.EstimatedStoreArrivalDate).ThenBy(x => x.ProductCode, StringComparer.Ordinal).ToList(),
        };
    }

    private async Task<bool> CanAccessStoreCodeAsync(string storeCode)
    {
        var scope = await storeScope.GetScopeAsync();
        if (scope.IsAdmin || scope.CanAccessStoreCode(storeCode))
        {
            return true;
        }

        // 独立新品权限允许普通授权用户读取其 UserStore 关联门店，关联门店不要求 IsPrimary。
        var userGuid = currentUser.GetCurrentUserGuid();
        return !string.IsNullOrWhiteSpace(userGuid)
            && await _db.Queryable<UserStore>()
                .InnerJoin<Store>((assignment, store) => assignment.StoreGUID == store.StoreGUID)
                .Where((assignment, store) =>
                    assignment.UserGUID == userGuid
                    && !assignment.IsDeleted
                    && !store.IsDeleted
                    && store.StoreCode == storeCode)
                .AnyAsync();
    }

    internal static DateTime AddWeekdays(DateTime date, int weekdays)
    {
        while (weekdays > 0)
        {
            date = date.AddDays(1);
            if (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) weekdays--;
        }
        return date;
    }

    internal static (DateTime From, DateTime ToExclusive) BuildWindow(DateTime localToday) =>
        (localToday.Date.AddDays(-14), localToday.Date.AddDays(29));

    internal static bool ShouldIncludeProduct(bool warehouseProductExists, bool matchingContainerCreateAudit) =>
        !warehouseProductExists || matchingContainerCreateAudit;

    internal static bool HasUsableProductCode(string? productCode) =>
        !string.IsNullOrWhiteSpace(productCode);

    internal static string? ResolveState(Store store)
    {
        var postcode = PublicHolidaySyncHelper.ExtractPostcodeFromAddress(store.Address);
        var text = (store.Address ?? string.Empty).ToUpperInvariant();
        return PublicHolidaySyncHelper.ResolveJurisdictionFromPostcode(postcode)
            ?? (Regex.IsMatch(text, @"(?:^|[\s,])(?:QLD|QUEENSLAND)(?:[\s,]|$)") ? "QLD" : null)
            ?? (Regex.IsMatch(text, @"(?:^|[\s,])(?:NSW|NEW SOUTH WALES)(?:[\s,]|$)") ? "NSW" : null);
    }

    private static DateTime GetLocalToday(string state) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(state == "QLD" ? "Australia/Brisbane" : "Australia/Sydney")).Date;

    private sealed class ContainerDateRow { public string ContainerCode { get; init; } = string.Empty; public string? ContainerNumber { get; init; } public DateTime? ActualArrivalDate { get; init; } public DateTime? EstimatedArrivalDate { get; init; } }
    private sealed class DetailRow { public string ContainerCode { get; init; } = string.Empty; public string ProductCode { get; init; } = string.Empty; public string? ImageUrl { get; init; } }
    private sealed class HistoryRow { public string ProductCode { get; init; } = string.Empty; public string ContainerCode { get; init; } = string.Empty; }
    private sealed record HistoryKey(string ProductCode, string ContainerCode) { public static IEqualityComparer<HistoryKey> Comparer { get; } = new KeyComparer(); private sealed class KeyComparer : IEqualityComparer<HistoryKey> { public bool Equals(HistoryKey? x, HistoryKey? y) => x != null && y != null && string.Equals(x.ProductCode, y.ProductCode, StringComparison.OrdinalIgnoreCase) && string.Equals(x.ContainerCode, y.ContainerCode, StringComparison.OrdinalIgnoreCase); public int GetHashCode(HistoryKey obj) => HashCode.Combine(obj.ProductCode.ToUpperInvariant(), obj.ContainerCode.ToUpperInvariant()); } }
}

public sealed class ContainerNewProductsForbiddenException : Exception;
public sealed class ContainerNewProductsStoreNotFoundException : Exception;
public sealed class ContainerNewProductsStateUnknownException : Exception;
