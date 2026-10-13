using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces.React;

/// <summary>
/// 新品列表的时间窗口（过去 1 周至未来 3 周）按哪个日期算。
/// </summary>
public enum ContainerNewProductsWindowBasis
{
    /// <summary>默认（旧版 App 与后台每日推送的口径）：按「预计到店区间」与窗口有交集；条目按预计到店起始日排序。</summary>
    StoreArrival = 0,

    /// <summary>按货柜到仓库日期（实际到货日期优先，没有时用预计到岸日期）落在窗口内；条目按到仓库日期排序。</summary>
    WarehouseArrival = 1,
}

public interface IContainerNewProductsReactService
{
    /// <param name="includeExisting">为 true 时同时返回仓库已建档的「已有商品」（带 IsNewProduct=false）；默认只返回新商品，工作台角标与旧版 App 口径不变。</param>
    /// <param name="windowBasis">窗口依据；默认 <see cref="ContainerNewProductsWindowBasis.StoreArrival"/>，与改动前完全一致。放在 CancellationToken 之后，避免破坏按位置传 ct 的现有调用方。</param>
    Task<ContainerNewProductsResponseDto> GetAsync(
        string storeCode,
        bool includeExisting = false,
        CancellationToken cancellationToken = default,
        ContainerNewProductsWindowBasis windowBasis = ContainerNewProductsWindowBasis.StoreArrival);
}
