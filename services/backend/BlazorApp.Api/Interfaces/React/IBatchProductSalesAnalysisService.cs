using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces.React;

public interface IBatchProductSalesAnalysisService
{
    Task<ApiResponse<BatchProductSalesOptionsDto>> GetOptionsAsync(IReadOnlyList<string>? scopedStoreCodes, CancellationToken cancellationToken = default);
    Task<ApiResponse<BatchProductSalesQueryResultDto>> QueryAsync(BatchProductSalesQueryRequestDto request, IReadOnlyList<string>? scopedStoreCodes, CancellationToken cancellationToken = default);
    Task<ApiResponse<BatchProductSalesDetailDto>> GetDetailAsync(BatchProductSalesDetailRequestDto request, IReadOnlyList<string>? scopedStoreCodes, CancellationToken cancellationToken = default);
    Task<ApiResponse<BatchProductSalesBranchOverviewDto>> GetBranchOverviewAsync(BatchProductSalesBranchOverviewRequestDto request, IReadOnlyList<string>? scopedStoreCodes, CancellationToken cancellationToken = default);
    Task<ApiResponse<BatchProductSalesDiscountOverviewDto>> GetDiscountOverviewAsync(BatchProductSalesBranchOverviewRequestDto request, IReadOnlyList<string>? scopedStoreCodes, CancellationToken cancellationToken = default);
    Task<ApiResponse<BatchProductSalesPurchaseResultDto>> GetPurchaseQuantitiesAsync(BatchProductSalesPurchaseRequestDto request, IReadOnlyList<string>? scopedStoreCodes, CancellationToken cancellationToken = default);
    Task<string> ExportDetailCsvAsync(BatchProductSalesFollowupRequestDto request, IReadOnlyList<string>? scopedStoreCodes, CancellationToken cancellationToken = default);
}
