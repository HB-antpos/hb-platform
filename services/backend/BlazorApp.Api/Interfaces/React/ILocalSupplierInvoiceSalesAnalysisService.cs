using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces.React
{
    public interface ILocalSupplierInvoiceSalesAnalysisService
    {
        Task<ApiResponse<LocalSupplierInvoiceSalesAnalysisResponseDto>> GetAnalysisAsync(
            string invoiceGuid
        );

        Task<ApiResponse<LocalSupplierPurchaseSalesAnalysisResponseDto>> GetPurchaseSalesAnalysisAsync(
            LocalSupplierPurchaseSalesAnalysisQueryDto query,
            IReadOnlyList<string>? scopedStoreCodes
        );

        Task<List<LocalSupplierPurchaseSalesAnalysisStoreOptionDto>> GetStoreOptionsAsync(
            IReadOnlyList<string>? scopedStoreCodes
        );

        Task<List<LocalSupplierPurchaseSalesAnalysisSupplierOptionDto>> GetSupplierOptionsAsync(
            IReadOnlyList<string>? scopedStoreCodes,
            string? storeCode
        );

        Task<List<LocalSupplierCategoryNodeDto>> GetPurchaseSalesAnalysisCategoryTreeAsync(
            string supplierCode
        );
    }
}
