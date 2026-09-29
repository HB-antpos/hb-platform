using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces.React
{
    /// <summary>
    /// 澳洲本地商品销量分析服务。
    /// </summary>
    public interface ILocalSupplierProductSalesAnalysisService
    {
        Task<ApiResponse<LocalSupplierProductSalesOptionsDto>> GetOptionsAsync(
            IReadOnlyList<string>? scopedStoreCodes
        );

        Task<ApiResponse<List<LocalSupplierProductSalesSupplierCategoryOptionDto>>> GetSupplierCategoryOptionsAsync(
            IReadOnlyList<string>? supplierCodes,
            IReadOnlyList<string>? scopedStoreCodes,
            bool tree = false
        );

        Task<
            ApiResponse<
                LocalSupplierProductSalesPagedDto<LocalSupplierProductSalesCandidateDto>
            >
        > GetCandidatesAsync(
            LocalSupplierProductSalesAnalysisRequest request,
            IReadOnlyList<string>? scopedStoreCodes
        );

        Task<ApiResponse<LocalSupplierProductSalesSummaryResponseDto>> GetSummaryAsync(
            LocalSupplierProductSalesAnalysisRequest request,
            IReadOnlyList<string>? scopedStoreCodes
        );

        Task<ApiResponse<List<LocalSupplierProductSalesDailyDto>>> GetProductDailyAsync(
            LocalSupplierProductSalesAnalysisRequest request,
            IReadOnlyList<string>? scopedStoreCodes
        );

        Task<ApiResponse<LocalSupplierProductSalesInvoiceDetailPageDto>> GetInvoiceDetailsAsync(
            LocalSupplierProductSalesAnalysisRequest request,
            IReadOnlyList<string>? scopedStoreCodes
        );

        Task<ApiResponse<List<LocalSupplierProductSalesBranchDto>>> GetBranchesAsync(
            LocalSupplierProductSalesAnalysisRequest request,
            IReadOnlyList<string>? scopedStoreCodes
        );

        Task<ApiResponse<List<LocalSupplierProductSalesBranchDailyDto>>> GetBranchDailyAsync(
            LocalSupplierProductSalesAnalysisRequest request,
            IReadOnlyList<string>? scopedStoreCodes
        );

        Task<ApiResponse<LocalSupplierProductSalesBootstrapResponseDto>> BootstrapAsync(
            LocalSupplierProductSalesAnalysisRequest request,
            IReadOnlyList<string>? scopedStoreCodes
        );
    }
}
