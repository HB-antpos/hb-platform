using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces.React;

public interface IBrowserExtensionService
{
    BrowserExtensionReleaseDto GetRelease();
    BrowserExtensionSupplierProfilesDto GetSupplierProfiles();

    /// <param name="cancellationToken">调用方请求令牌；已取消时销量排名不再降级，取消异常直接上抛</param>
    Task<BrowserExtensionProductSummaryBatchDto> GetProductSummariesAsync(
        BrowserExtensionProductSummaryBatchRequestDto request,
        CancellationToken cancellationToken = default
    );

    Task<BrowserExtensionPurchaseCyclesDto> GetPurchaseCyclesAsync(
        BrowserExtensionPurchaseCyclesRequestDto request
    );

    Task<BrowserExtensionStoreOptionsDto> GetEnabledStoresAsync(
        IReadOnlyCollection<string> relatedStoreCodes
    );

    Task<BrowserExtensionSupplierTopSalesDto> GetSupplierTopSalesAsync(
        BrowserExtensionSupplierTopSalesRequestDto request
    );

    Task<BrowserExtensionSupplierProductStoreSalesDto> GetSupplierProductStoreSalesAsync(
        BrowserExtensionSupplierProductStoreSalesRequestDto request
    );
}
