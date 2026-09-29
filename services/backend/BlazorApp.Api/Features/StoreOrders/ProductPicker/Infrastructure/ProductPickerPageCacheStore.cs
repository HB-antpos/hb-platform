using BlazorApp.Api.Cache;
using BlazorApp.Api.Features.StoreOrders.ProductPicker.Domain;
using BlazorApp.Shared.DTOs;
using Microsoft.Extensions.Caching.Memory;

namespace BlazorApp.Api.Features.StoreOrders.ProductPicker.Infrastructure;

internal sealed class ProductPickerPageCacheStore(
    IMemoryCache cache,
    IProductPickerLocationLookup locationLookup,
    ILogger<ProductPickerPageCacheStore> logger
)
{
    internal bool TryGet(
        StoreOrderFilterDto filter,
        out PagedListReactDto<StoreOrderProductDto>? result
    )
    {
        result = null;
        if (!ShouldCache(filter))
        {
            return false;
        }

        var cacheKey = CreateCacheKey(filter);
        if (!cache.TryGetValue(cacheKey, out result) || result is null)
        {
            logger.LogDebug("缓存未命中，从服务获取商品列表: {CacheKey}", cacheKey);
            return false;
        }

        logger.LogDebug("从缓存获取商品列表: {CacheKey}", cacheKey);
        return true;
    }

    internal void Set(
        StoreOrderFilterDto filter,
        PagedListReactDto<StoreOrderProductDto> result
    )
    {
        if (!ShouldCache(filter))
        {
            return;
        }

        var cacheKey = CreateCacheKey(filter);
        var options = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(ProductPickerRules.HomePageCacheDuration)
            .SetPriority(CacheItemPriority.Normal);
        cache.Set(cacheKey, result, options);
        logger.LogDebug(
            "商品列表已缓存: {CacheKey}, 过期时间: {Expiration}",
            cacheKey,
            DateTime.Now.Add(ProductPickerRules.HomePageCacheDuration)
        );
    }

    private string CreateCacheKey(StoreOrderFilterDto filter)
    {
        return StoreOrderCacheKeys.Products(filter, locationLookup.IsEnabled);
    }

    private static bool ShouldCache(StoreOrderFilterDto filter)
    {
        return !filter.ExcludeExistingWarehouseProducts
            && string.IsNullOrWhiteSpace(filter.ExcludeOrderGUID)
            && string.IsNullOrWhiteSpace(filter.SupplierCode)
            && !HasTextSearch(filter);
    }

    private static bool HasTextSearch(StoreOrderFilterDto filter)
    {
        // 关键字检索既不读也不写缓存：跨用户几乎不复用，且上下架、改价后用户常立即用同一关键字复查，
        // 缓存（尤其是空结果）会让刚上架的商品在过期前一直搜不到；写入口众多，无法可靠地按写入失效。
        var columnFilters = filter.ColumnFilters;
        return !string.IsNullOrWhiteSpace(filter.ItemNumber)
            || !string.IsNullOrWhiteSpace(filter.ProductName)
            || !string.IsNullOrWhiteSpace(columnFilters?.ItemNumber)
            || !string.IsNullOrWhiteSpace(columnFilters?.ProductName)
            || !string.IsNullOrWhiteSpace(columnFilters?.Barcode)
            || !string.IsNullOrWhiteSpace(columnFilters?.SupplierKeyword);
    }
}
