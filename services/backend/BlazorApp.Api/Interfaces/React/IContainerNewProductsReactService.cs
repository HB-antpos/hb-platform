using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces.React;

public interface IContainerNewProductsReactService
{
    /// <param name="includeExisting">为 true 时同时返回仓库已建档的「已有商品」（带 IsNewProduct=false）；默认只返回新商品，工作台角标与旧版 App 口径不变。</param>
    Task<ContainerNewProductsResponseDto> GetAsync(string storeCode, bool includeExisting = false, CancellationToken cancellationToken = default);
}
