using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;

namespace BlazorApp.Api.Interfaces.React
{
    public interface ILocalSupplierInvoicesReactService
    {
        Task<GridResponseDto<LocalSupplierInvoiceListDto>> GetGridDataAsync(GridRequestDto request);
        Task<GridResponseDto<LocalSupplierInvoiceListDto>> GetGridDataAsync(
            GridRequestDto request,
            List<string>? allowedStoreCodes
        );
        Task<ApiResponse<List<LocalSupplierInvoiceStoreCountDto>>> GetStoreCountsAsync(
            GridRequestDto request,
            List<string>? allowedStoreCodes
        );
        Task<ApiResponse<LocalSupplierInvoiceFilterOptionsDto>> GetFilterOptionsAsync(
            List<string>? allowedStoreCodes,
            string? storeCode
        );
        Task<ApiResponse<LocalSupplierInvoiceDetailDto>> GetInvoiceAsync(string invoiceGuid);
        Task<ApiResponse<List<LocalSupplierInvoiceItemDto>>> GetDetailsAsync(string invoiceGuid);
        Task<GridResponseDto<LocalSupplierInvoiceItemDto>> GetDetailsGridAsync(
            string invoiceGuid,
            GridRequestDto request
        );
        Task<ApiResponse<string>> CreateAsync(CreateInvoiceRequest dto);
        Task<ApiResponse<bool>> DeleteAsync(string invoiceGuid, string updatedBy);
        Task<ApiResponse<List<SupplierItemDetectResult>>> DetectSupplierItemAsync(
            DetectSupplierItemRequest dto
        );
        Task<ApiResponse<List<BarcodeDetectResult>>> DetectBarcodeAsync(DetectBarcodeRequest dto);
        Task<ApiResponse<bool>> UpdateAsync(string invoiceGuid, UpdateInvoiceRequest dto);
        Task<ApiResponse<BatchResultDto>> BatchUpsertDetailsAsync(
            string invoiceGuid,
            List<InvoiceDetailUpsertItemDto> items,
            string updatedBy
        );
        Task<ApiResponse<BatchResultDto>> BatchUpdateDetailsAsync(
            string invoiceGuid,
            BatchUpdateInvoiceDetailsRequest request,
            string updatedBy
        );
        Task<ApiResponse<UpdateToStorePricesResultDto>> UpdateDetailsToStorePricesAsync(
            UpdateToStorePricesRequest dto,
            string updatedBy
        );
        Task<ApiResponse<UpdateToStorePricesResultDto>> UpdateDetailsToStorePricesAsync(
            UpdateToStorePricesRequest dto,
            string updatedBy,
            int lockWaitMilliseconds
        );
        Task<ApiResponse<UpdateLastPurchasePricesResultDto>> UpdateLastPurchasePricesAsync(
            string invoiceGuid,
            UpdateLastPurchasePricesRequest request,
            string updatedBy
        );
        Task<ApiResponse<CheckProductsResponseDto>> CheckProductsAsync(CheckProductsRequest dto);
        Task<ApiResponse<BatchResultDto>> PasteDetailsAsync(
            PasteDetailsRequest dto,
            string updatedBy
        );
        Task<ApiResponse<bool>> UpdateDetailActionAsync(
            string invoiceGuid,
            string detailGuid,
            int action
        );
        Task<ApiResponse<BatchResultDto>> BatchUpdateDetailActionAsync(
            string invoiceGuid,
            BatchUpdateDetailActionRequest dto
        );
        Task<ApiResponse<bool>> DeleteDetailsAsync(
            string invoiceGuid,
            List<string> detailGuids,
            string updatedBy
        );
        Task<ApiResponse<GetBarcodeAbnormalDetailsResponse>> GetBarcodeAbnormalDetailsAsync(
            string invoiceGuid
        );
        Task<ApiResponse<GetProductsByBarcodeResponse>> GetProductsByBarcodeAsync(
            string invoiceGuid,
            string barcode
        );
        Task<ApiResponse<GetProductsByProductCodeResponse>> GetProductsByProductCodeAsync(
            string invoiceGuid,
            string productCode
        );
        Task<ApiResponse<InvoiceNoCheckResult>> CheckInvoiceNoExistsAsync(
            string storeCode,
            string supplierCode,
            string invoiceNo
        );
        Task<ApiResponse<BatchExecuteActionsResultDto>> BatchExecuteActionsAsync(
            string invoiceGuid,
            List<string> detailGuids,
            string userName,
            List<BatchExecuteNewProductProductTypeSelectionDto>? newProductProductTypeSelections = null,
            List<BatchExecuteExpectedActionDto>? expectedActions = null,
            IReadOnlyCollection<StoreLocalSupplierInvoiceDetails>? confirmedDetails = null,
            bool confirmedLargePriceChange = false
        );
        Task<SyncResult> PushInvoicesToHqAsync(List<string> invoiceGuids);
    }
}
