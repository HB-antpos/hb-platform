using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces.React;

public interface IContainerNewProductsReactService
{
    Task<ContainerNewProductsResponseDto> GetAsync(string storeCode, CancellationToken cancellationToken = default);
}
