using BlazorApp.Api.Features.StoreOrders.Cart;
using BlazorApp.Api.Features.StoreOrders.ImportPriceVariance;
using BlazorApp.Api.Features.StoreOrders.Lifecycle;
using BlazorApp.Api.Features.StoreOrders.OrderManagement;
using BlazorApp.Api.Features.StoreOrders.OrderPlacement;
using BlazorApp.Api.Features.StoreOrders.Orders;
using BlazorApp.Api.Features.StoreOrders.PasteReplace;
using BlazorApp.Api.Features.StoreOrders.ProductHistory;
using BlazorApp.Api.Features.StoreOrders.ProductPicker;
using BlazorApp.Api.Features.StoreOrders.Sync;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Controllers.React.StoreOrders;

/// <summary>
/// 仅供旧 Controller 构造测试使用；生产 Controller 由 DI 直接取得窄切片。
/// </summary>
internal sealed class LegacyStoreOrderSliceAdapter(IStoreOrderReactService service) :
    IStoreOrderProductPickerSlice,
    IStoreOrderCartSlice,
    IStoreOrderPlacementSlice,
    IStoreOrderProductHistorySlice,
    IStoreOrderOrdersSlice,
    IStoreOrderImportPriceVarianceSlice,
    IStoreOrderOrderManagementSlice,
    IStoreOrderPasteReplaceExecutor,
    IStoreOrderMissingOrdersSyncExecutor,
    IStoreOrderLifecycleSlice
{
    public Task<PagedListReactDto<StoreOrderProductDto>> GetPagedListAsync(
        StoreOrderFilterDto filter
    ) => service.GetPagedListAsync(filter);

    public Task<ApiResponse<List<StoreOrderBatchLookupItemDto>>> BatchLookupProductsAsync(
        StoreOrderBatchLookupRequestDto request
    ) => service.BatchLookupProductsAsync(request);

    public Task<ApiResponse<StoreOrderScanLookupResultDto>> ScanLookupProductsAsync(
        StoreOrderScanLookupRequestDto request
    ) => service.ScanLookupProductsAsync(request);

    public Task<ApiResponse<StoreOrderScanLookupResultDto>> LookupAsync(
        StoreOrderScanLookupRequestDto request
    ) => service.ScanLookupProductsAsync(request);

    public Task<PagedListReactDto<StoreOrderProductDto>> GetHomePageWarmUpPageAsync(
        int pageSize,
        CancellationToken cancellationToken = default
    ) => service.GetPagedListAsync(CreateHomePageFilter(pageSize));

    public Task<PagedListReactDto<StoreOrderProductDto>> GetHomePageCachePageAsync(
        int pageSize,
        CancellationToken cancellationToken = default
    ) => service.GetPagedListAsync(CreateHomePageFilter(pageSize));

    public Task WarmUpHomePageAsync() => Task.CompletedTask;

    public Task<ApiResponse<StoreOrderScanLookupAddResultDto>> ScanLookupAndAddToCartMutationAsync(
        StoreOrderScanLookupAddRequestDto request
    ) => service.ScanLookupAndAddToCartMutationAsync(request);

    public Task<ApiResponse<StoreOrderCartDto?>> GetActiveCartAsync(string storeCode) =>
        service.GetActiveCartAsync(storeCode);

    public Task<ApiResponse<StoreOrderCartDto?>> GetActiveCartSummaryAsync(
        string storeCode
    ) => service.GetActiveCartSummaryAsync(storeCode);

    public Task<ApiResponse<StoreOrderCartDto?>> AddToCartAsync(
        AddToCartRequestDto request
    ) => service.AddToCartAsync(request);

    public Task<ApiResponse<StoreOrderCartMutationResultDto?>> AddToCartMutationAsync(
        AddToCartRequestDto request
    ) => service.AddToCartMutationAsync(request);

    public Task<ApiResponse<StoreOrderCartDto?>> UpdateCartItemAsync(
        AddToCartRequestDto request
    ) => service.UpdateCartItemAsync(request);

    public Task<ApiResponse<StoreOrderCartMutationResultDto?>> UpdateCartItemMutationAsync(
        AddToCartRequestDto request
    ) => service.UpdateCartItemMutationAsync(request);

    public Task<ApiResponse<bool>> RemoveFromCartAsync(
        RemoveFromCartRequestDto request
    ) => service.RemoveFromCartAsync(request);

    public Task<ApiResponse<StoreOrderCartDto?>> ClearCartAsync(string storeCode) =>
        service.ClearCartAsync(storeCode);

    public Task<ApiResponse<SubmitStoreOrderResultDto>> SubmitOrderAsync(
        SubmitStoreOrderRequestDto request
    ) => service.SubmitOrderAsync(request);

    public Task<ApiResponse<string>> CreateOrderAsync(CreateStoreOrderDto request) =>
        service.CreateOrderAsync(request);

    public Task<ApiResponse<CopyOrderResultDto>> CopyOrderAsync(CopyOrderDto request) =>
        service.CopyOrderAsync(request);

    public Task<ApiResponse<List<StoreOrderDynamicDataDto>>> GetProductsDynamicDataAsync(
        StoreOrderDynamicDataRequestDto request
    ) => service.GetProductsDynamicDataAsync(request);

    public Task<ApiResponse<PagedListReactDto<StoreOrderProductOrderHistoryItemDto>>> GetProductOrderHistoryAsync(
        StoreOrderProductOrderHistoryRequestDto request
    ) => service.GetProductOrderHistoryAsync(request);

    public Task<ApiResponse<StoreOrderProductActivityHistoryResultDto>> GetProductActivityHistoryAsync(
        StoreOrderProductActivityHistoryRequestDto request
    ) => service.GetProductActivityHistoryAsync(request);

    public Task<ApiResponse<StoreOrderSalesSinceLastArrivalResultDto>> GetSalesSinceLastArrivalAsync(
        StoreOrderSalesSinceLastArrivalRequestDto request
    ) => service.GetSalesSinceLastArrivalAsync(request);

    public Task<ApiResponse<List<StoreOrderSalesSinceLastArrivalSummaryItemDto>>> GetSalesSinceLastArrivalSummaryAsync(
        StoreOrderSalesSinceLastArrivalSummaryRequestDto request
    ) => service.GetSalesSinceLastArrivalSummaryAsync(request);

    public Task<PagedListReactDto<StoreOrderListItemDto>> GetOrderListAsync(
        StoreOrderListFilterDto filter
    ) => service.GetOrderListAsync(filter);

    public Task<ApiResponse<StoreOrderDetailDto?>> GetOrderDetailAsync(
        string orderGuid,
        StoreOrderDetailQueryDto? query = null
    ) => service.GetOrderDetailAsync(orderGuid, query);

    public Task<ApiResponse<StoreOrderCartDto?>> GetOrderDetailFullAsync(
        string orderGuid
    ) => service.GetOrderDetailFullAsync(orderGuid);

    public Task<ApiResponse<List<string>>> GetOrderDetailProductCodesAsync(
        string orderGuid
    ) => service.GetOrderDetailProductCodesAsync(orderGuid);

    public Task<ApiResponse<StoreOrderStoreContactDto>> UpdateStoreContactAsync(
        UpdateStoreOrderStoreContactDto request
    ) => service.UpdateStoreContactAsync(request);

    public Task<ApiResponse<List<BranchDto>>> GetUsedBranchesAsync() =>
        service.GetUsedBranchesAsync();

    public Task<ApiResponse<List<UnmatchedStoreOrderGroupDto>>> GetUnmatchedStoreOrderGroupsAsync() =>
        service.GetUnmatchedStoreOrderGroupsAsync();

    public Task<ApiResponse<BatchMapStoreOrderStoreCodeResultDto>> BatchMapStoreOrderStoreCodeAsync(
        BatchMapStoreOrderStoreCodeDto request
    ) => service.BatchMapStoreOrderStoreCodeAsync(request);

    public Task<ApiResponse<StoreOrderImportPriceVarianceResultDto>> GetImportPriceVarianceAsync(
        StoreOrderImportPriceVarianceQueryDto query
    ) => service.GetImportPriceVarianceAsync(query);

    public Task<ApiResponse<StoreOrderImportPriceVarianceDetailResultDto>> GetImportPriceVarianceDetailsAsync(
        StoreOrderImportPriceVarianceDetailQueryDto query
    ) => service.GetImportPriceVarianceDetailsAsync(query);

    public Task<ApiResponse<StoreOrderImportPriceVarianceDomesticPriceUpdateResultDto>> UpdateImportPriceVarianceDomesticPriceAsync(
        StoreOrderImportPriceVarianceDomesticPriceUpdateDto request
    ) => service.UpdateImportPriceVarianceDomesticPriceAsync(request);

    public Task<ApiResponse<StoreOrderImportPriceVarianceWarehouseImportPriceUpdateResultDto>> UpdateImportPriceVarianceWarehouseImportPriceAsync(
        StoreOrderImportPriceVarianceWarehouseImportPriceUpdateDto request
    ) => service.UpdateImportPriceVarianceWarehouseImportPriceAsync(request);

    public Task<ApiResponse<StoreOrderImportPriceVarianceWarehouseImportPriceBatchUpdateResultDto>> UpdateImportPriceVarianceWarehouseImportPriceBatchAsync(
        StoreOrderImportPriceVarianceWarehouseImportPriceBatchUpdateDto request
    ) => service.UpdateImportPriceVarianceWarehouseImportPriceBatchAsync(request);

    public Task<ApiResponse<bool>> AddOrderLineAsync(AddOrderLineDto request) =>
        service.AddOrderLineAsync(request);

    public Task<ApiResponse<bool>> BatchAddOrderLineAsync(
        BatchAddOrderLineDto request
    ) => service.BatchAddOrderLineAsync(request);

    public Task<ApiResponse<bool>> UpdateOrderLineAsync(UpdateOrderLineDto request) =>
        service.UpdateOrderLineAsync(request);

    public Task<ApiResponse<bool>> RemoveOrderLineAsync(RemoveOrderLineDto request) =>
        service.RemoveOrderLineAsync(request);

    public Task<ApiResponse<bool>> BatchUpdateOrderLineAsync(
        BatchUpdateOrderLineDto request
    ) => service.BatchUpdateOrderLineAsync(request);

    public Task<ApiResponse<RefreshStoreOrderImportPricesResultDto>> RefreshOrderLineImportPricesAsync(
        RefreshStoreOrderImportPricesDto request
    ) => service.RefreshOrderLineImportPricesAsync(request);

    public Task<ApiResponse<bool>> UpdateOrderHeaderAsync(
        UpdateOrderHeaderDto request
    ) => service.UpdateOrderHeaderAsync(request);

    public Task<ApiResponse<bool>> UpdateOrderOutboundDateAsync(
        UpdateOrderOutboundDateDto request
    ) => service.UpdateOrderOutboundDateAsync(request);

    public Task<ApiResponse<bool>> DeleteOrderAsync(string orderGuid) =>
        service.DeleteOrderAsync(orderGuid);

    public Task<ApiResponse<bool>> UpdateProductStatusAsync(
        UpdateProductStatusDto request
    ) => service.UpdateProductStatusAsync(request);

    public Task<ApiResponse<bool>> BatchUpdateProductStatusAsync(
        BatchUpdateProductStatusDto request
    ) => service.BatchUpdateProductStatusAsync(request);

    public Task<ApiResponse<bool>> PasteReplaceOrderLinesAsync(
        PasteReplaceOrderLinesDto request
    ) => service.PasteReplaceOrderLinesAsync(request);

    public Task<SyncMissingOrdersResultDto> SyncMissingOrdersFromHqAsync(
        SyncMissingOrdersRequestDto? request
    ) => service.SyncMissingOrdersFromHqAsync(request);

    public Task<ApiResponse<bool>> CompleteOrderAsync(string orderGuid) =>
        service.CompleteOrderAsync(orderGuid);

    public Task<ApiResponse<bool>> StartPickingAsync(string orderGuid) =>
        service.StartPickingAsync(orderGuid);

    public Task<ApiResponse<bool>> UpdateOrderStatusAsync(
        string orderGuid,
        int newStatus,
        bool bypassPreorderGate = false
    ) => service.UpdateOrderStatusAsync(orderGuid, newStatus, bypassPreorderGate);

    public Task<ApiResponse<int>> BatchUpdateOrderStatusAsync(
        List<string> orderGuids,
        int newStatus,
        bool bypassPreorderGate = false
    ) => service.BatchUpdateOrderStatusAsync(
        orderGuids,
        newStatus,
        bypassPreorderGate
    );

    private static StoreOrderFilterDto CreateHomePageFilter(int pageSize) =>
        new()
        {
            PageNumber = 1,
            PageSize = pageSize,
            StoreCode = "all",
            SortBy = "Default",
        };
}
