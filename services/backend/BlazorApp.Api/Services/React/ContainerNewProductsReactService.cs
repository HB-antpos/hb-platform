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
        bool includeExisting = false,
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
        // 窗口按「预计到店区间」算；区间 = 货柜日期 + [起, 止] 个工作日，所以先按放宽后的货柜日期粗筛，再逐柜精确过滤
        var (from, toExclusive) = BuildWindow(localToday);
        var (containerFrom, containerToExclusive) = BuildContainerQueryWindow(from, toExclusive);
        var (startWeekdays, endWeekdays) = GetStoreArrivalWeekdayRange(stateCode);
        var candidateContainers = await _db.Queryable<Container>()
            .Where(x => !x.IsDeleted)
            .Where(x =>
                (x.ActualArrivalDate != null && x.ActualArrivalDate >= containerFrom && x.ActualArrivalDate < containerToExclusive)
                || (x.ActualArrivalDate == null && x.EstimatedArrivalDate != null && x.EstimatedArrivalDate >= containerFrom && x.EstimatedArrivalDate < containerToExclusive))
            .Select(x => new ContainerDateRow
            {
                ContainerCode = x.ContainerCode,
                ContainerNumber = x.ContainerNumber,
                ActualArrivalDate = x.ActualArrivalDate,
                EstimatedArrivalDate = x.EstimatedArrivalDate,
            })
            .ToListAsync(cancellationToken);
        var storeArrivalByContainer = new Dictionary<string, (DateTime Start, DateTime End)>(StringComparer.OrdinalIgnoreCase);
        var containers = new List<ContainerDateRow>();
        foreach (var candidate in candidateContainers)
        {
            var containerDate = (candidate.ActualArrivalDate ?? candidate.EstimatedArrivalDate)!.Value.Date;
            var storeArrival = (Start: AddWeekdays(containerDate, startWeekdays), End: AddWeekdays(containerDate, endWeekdays));
            // 到店区间与窗口有交集就显示：区间跨过窗口边界的货柜也可能在窗口内到店
            if (!OverlapsWindow(storeArrival.Start, storeArrival.End, from, toExclusive)) continue;
            storeArrivalByContainer[candidate.ContainerCode] = storeArrival;
            containers.Add(candidate);
        }

        if (containers.Count == 0)
        {
            return new ContainerNewProductsResponseDto { StoreCode = normalizedStoreCode, StateCode = stateCode, LocalToday = DateOnly.FromDateTime(localToday) };
        }

        var containerCodes = containers.Select(x => x.ContainerCode).ToList();
        var details = await _db.Queryable<ContainerDetail>()
            .Where(x => !x.IsDeleted && x.ProductCode != null && containerCodes.Contains(x.ContainerCode))
            .LeftJoin<DomesticProduct>((detail, product) => detail.ProductCode == product.ProductCode)
            .Select((detail, product) => new DetailRow
            {
                ContainerCode = detail.ContainerCode,
                ProductCode = detail.ProductCode!,
                HbProductNo = product.HBProductNo,
                LoadingQuantity = detail.LoadingQuantity,
                ImageUrl = product.ProductImage,
                Barcode = product.Barcode,
                DetailRetailPrice = detail.OEMPrice,
            })
            .ToListAsync(cancellationToken);
        details = details.Where(x => HasUsableProductCode(x.ProductCode)).ToList();

        var productCodes = details.Select(x => x.ProductCode).Distinct().ToList();
        if (productCodes.Count == 0)
        {
            return new ContainerNewProductsResponseDto { StoreCode = normalizedStoreCode, StateCode = stateCode, LocalToday = DateOnly.FromDateTime(localToday) };
        }
        var existingProducts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var historyKeys = new HashSet<HistoryKey>(HistoryKey.Comparer);
        // 已建档商品的主档条码/零售价，以及本门店的分店零售价；同一商品多行时取第一条有效值
        var localProducts = new Dictionary<string, LocalProductRow>(StringComparer.OrdinalIgnoreCase);
        var storeRetailPrices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
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

            foreach (var row in await _db.Queryable<Product>()
                .Where(x => !x.IsDeleted && x.ProductCode != null && productCodeBatch.Contains(x.ProductCode))
                .Select(x => new LocalProductRow { ProductCode = x.ProductCode!, Barcode = x.Barcode, RetailPrice = x.RetailPrice })
                .ToListAsync(cancellationToken))
            {
                if (!localProducts.TryGetValue(row.ProductCode, out var existing)
                    || (string.IsNullOrWhiteSpace(existing.Barcode) && !string.IsNullOrWhiteSpace(row.Barcode))
                    || (PositiveOrNull(existing.RetailPrice) == null && PositiveOrNull(row.RetailPrice) != null))
                {
                    localProducts[row.ProductCode] = row;
                }
            }

            // 分店价被停用时 POS 按总部价卖，所以只认启用中的分店价
            foreach (var row in await _db.Queryable<StoreRetailPrice>()
                .Where(x => !x.IsDeleted && x.IsActive && x.StoreCode == normalizedStoreCode
                    && x.ProductCode != null && productCodeBatch.Contains(x.ProductCode))
                .Select(x => new StoreRetailPriceRow { ProductCode = x.ProductCode!, Price = x.StoreRetailPriceValue })
                .ToListAsync(cancellationToken))
            {
                if (PositiveOrNull(row.Price) is { } price) storeRetailPrices.TryAdd(row.ProductCode, price);
            }
        }

        var containerByCode = containers.ToDictionary(x => x.ContainerCode, StringComparer.OrdinalIgnoreCase);
        var items = new List<ContainerNewProductItemDto>();
        foreach (var group in details.GroupBy(x => (x.ContainerCode, x.ProductCode)))
        {
            var detail = group.First();
            // 新商品 = 仓库里还没有，或正是由本柜提交时新建的；其余是补货的已有商品，仅在调用方要求时返回
            var isNewProduct = ShouldIncludeProduct(
                existingProducts.Contains(detail.ProductCode),
                historyKeys.Contains(new HistoryKey(detail.ProductCode, detail.ContainerCode)));
            if (!isNewProduct && !includeExisting)
            {
                continue;
            }

            var container = containerByCode[detail.ContainerCode];
            localProducts.TryGetValue(detail.ProductCode, out var localProduct);
            items.Add(new ContainerNewProductItemDto
            {
                ProductCode = detail.ProductCode,
                HbProductNo = string.IsNullOrWhiteSpace(detail.HbProductNo) ? null : detail.HbProductNo.Trim(),
                // 同一货柜同一商品可能拆成多行明细，数量要合计；全部为空时保持 null，前端不显示
                Quantity = group.Any(x => x.LoadingQuantity.HasValue) ? group.Sum(x => x.LoadingQuantity ?? 0) : null,
                ImageUrl = ProductImageUrlHelper.EnsureImageUrl(detail.ImageUrl, detail.ProductCode),
                Barcode = FirstNonBlank(detail.Barcode, localProduct?.Barcode),
                RetailPrice = ResolveRetailPrice(
                    storeRetailPrices.TryGetValue(detail.ProductCode, out var storePrice) ? storePrice : null,
                    localProduct?.RetailPrice,
                    group.Select(x => x.DetailRetailPrice).FirstOrDefault(x => PositiveOrNull(x) != null)),
                ContainerCode = detail.ContainerCode,
                ContainerNumber = container.ContainerNumber,
                EstimatedStoreArrivalDate = DateOnly.FromDateTime(storeArrivalByContainer[container.ContainerCode].Start),
                EstimatedStoreArrivalDateEnd = DateOnly.FromDateTime(storeArrivalByContainer[container.ContainerCode].End),
                Basis = container.ActualArrivalDate.HasValue ? "actual" : "estimated",
                IsNewProduct = isNewProduct,
            });
        }

        return new ContainerNewProductsResponseDto
        {
            StoreCode = normalizedStoreCode,
            StateCode = stateCode,
            LocalToday = DateOnly.FromDateTime(localToday),
            // 按到店区间起始日排（同一门店各柜区间长度相同，起始日相同则结束日也相同），同一到店日按 HB 货号排；没有货号的排在该日末尾，最后用 ProductCode 保证顺序稳定
            Items = items
                .OrderBy(x => x.EstimatedStoreArrivalDate)
                .ThenBy(x => x.HbProductNo == null)
                .ThenBy(x => x.HbProductNo, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.ProductCode, StringComparer.Ordinal)
                .ToList(),
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

    // 预计到店区间（货柜到仓库日期之后的工作日数，含两端）：NSW 当天至 3 个工作日，QLD 3 至 7 个工作日
    internal static (int Start, int End) GetStoreArrivalWeekdayRange(string stateCode) =>
        stateCode == "NSW" ? (0, 3) : (3, 7);

    // 到店区间 [start, end] 与窗口 [from, toExclusive) 是否有交集
    internal static bool OverlapsWindow(DateTime start, DateTime end, DateTime from, DateTime toExclusive) =>
        end >= from && start < toExclusive;

    // 预计到店日窗口：过去 1 周至未来 2 周，含今天前 7 天与后 14 天，上界为开区间
    internal static (DateTime From, DateTime ToExclusive) BuildWindow(DateTime localToday) =>
        (localToday.Date.AddDays(-7), localToday.Date.AddDays(15));

    // 货柜日期粗筛窗口：到店区间结束日比货柜日期最多晚 7 个工作日（最多跨 11 个自然日），下界多放 14 天保证不漏；
    // 到店区间起始日不早于货柜日期，所以上界沿用到店窗口上界即可
    internal static (DateTime From, DateTime ToExclusive) BuildContainerQueryWindow(DateTime storeFrom, DateTime storeToExclusive) =>
        (storeFrom.AddDays(-14), storeToExclusive);

    internal static bool ShouldIncludeProduct(bool warehouseProductExists, bool matchingContainerCreateAudit) =>
        !warehouseProductExists || matchingContainerCreateAudit;

    // 零售价优先级：门店分店价 → 商品主档零售价 → 货柜明细零售价（未建档新品的计划价）；0 或负数视为没有
    internal static decimal? ResolveRetailPrice(decimal? storeRetailPrice, decimal? productRetailPrice, decimal? detailRetailPrice) =>
        PositiveOrNull(storeRetailPrice) ?? PositiveOrNull(productRetailPrice) ?? PositiveOrNull(detailRetailPrice);

    private static decimal? PositiveOrNull(decimal? value) => value > 0 ? value : null;

    private static string? FirstNonBlank(params string?[] values) =>
        values.Select(x => x?.Trim()).FirstOrDefault(x => !string.IsNullOrEmpty(x));

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
    private sealed class DetailRow { public string ContainerCode { get; init; } = string.Empty; public string ProductCode { get; init; } = string.Empty; public string? HbProductNo { get; init; } public decimal? LoadingQuantity { get; init; } public string? ImageUrl { get; init; } public string? Barcode { get; init; } public decimal? DetailRetailPrice { get; init; } }
    private sealed class LocalProductRow { public string ProductCode { get; init; } = string.Empty; public string? Barcode { get; init; } public decimal? RetailPrice { get; init; } }
    private sealed class StoreRetailPriceRow { public string ProductCode { get; init; } = string.Empty; public decimal? Price { get; init; } }
    private sealed class HistoryRow { public string ProductCode { get; init; } = string.Empty; public string ContainerCode { get; init; } = string.Empty; }
    private sealed record HistoryKey(string ProductCode, string ContainerCode) { public static IEqualityComparer<HistoryKey> Comparer { get; } = new KeyComparer(); private sealed class KeyComparer : IEqualityComparer<HistoryKey> { public bool Equals(HistoryKey? x, HistoryKey? y) => x != null && y != null && string.Equals(x.ProductCode, y.ProductCode, StringComparison.OrdinalIgnoreCase) && string.Equals(x.ContainerCode, y.ContainerCode, StringComparison.OrdinalIgnoreCase); public int GetHashCode(HistoryKey obj) => HashCode.Combine(obj.ProductCode.ToUpperInvariant(), obj.ContainerCode.ToUpperInvariant()); } }
}

public sealed class ContainerNewProductsForbiddenException : Exception;
public sealed class ContainerNewProductsStoreNotFoundException : Exception;
public sealed class ContainerNewProductsStateUnknownException : Exception;
