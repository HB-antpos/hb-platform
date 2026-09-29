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
using Microsoft.Extensions.DependencyInjection;

namespace BlazorApp.Api.Services.React;

/// <summary>
/// 旧接口的兼容入口。业务、事务、SQL 与映射均由各垂直切片负责。
/// </summary>
public partial class StoreOrderReactService : IStoreOrderReactService
{
    private readonly IStoreOrderProductPickerSlice _productPickerSlice;
    private readonly IStoreOrderCartSlice _cartSlice;
    private readonly IStoreOrderPlacementSlice _orderPlacementSlice;
    private readonly IStoreOrderProductHistorySlice _productHistorySlice;
    private readonly IStoreOrderOrdersSlice _ordersSlice;
    private readonly IStoreOrderImportPriceVarianceSlice _importPriceVarianceSlice;
    private readonly IStoreOrderOrderManagementSlice _orderManagementSlice;
    private readonly IStoreOrderPasteReplaceExecutor _pasteReplaceExecutor;
    private readonly IStoreOrderMissingOrdersSyncExecutor _missingOrdersSyncExecutor;
    private readonly IStoreOrderLifecycleSlice _lifecycleSlice;

    [ActivatorUtilitiesConstructor]
    public StoreOrderReactService(
        IStoreOrderProductPickerSlice productPickerSlice,
        IStoreOrderCartSlice cartSlice,
        IStoreOrderPlacementSlice orderPlacementSlice,
        IStoreOrderProductHistorySlice productHistorySlice,
        IStoreOrderOrdersSlice ordersSlice,
        IStoreOrderImportPriceVarianceSlice importPriceVarianceSlice,
        IStoreOrderOrderManagementSlice orderManagementSlice,
        IStoreOrderPasteReplaceExecutor pasteReplaceExecutor,
        IStoreOrderMissingOrdersSyncExecutor missingOrdersSyncExecutor,
        IStoreOrderLifecycleSlice lifecycleSlice
    )
    {
        _productPickerSlice = productPickerSlice;
        _cartSlice = cartSlice;
        _orderPlacementSlice = orderPlacementSlice;
        _productHistorySlice = productHistorySlice;
        _ordersSlice = ordersSlice;
        _importPriceVarianceSlice = importPriceVarianceSlice;
        _orderManagementSlice = orderManagementSlice;
        _pasteReplaceExecutor = pasteReplaceExecutor;
        _missingOrdersSyncExecutor = missingOrdersSyncExecutor;
        _lifecycleSlice = lifecycleSlice;
    }

    public Task<PagedListReactDto<StoreOrderProductDto>> GetPagedListAsync(
        StoreOrderFilterDto filter
    ) => _productPickerSlice.GetPagedListAsync(filter);

    public Task<PagedListReactDto<StoreOrderProductDto>> GetHomePageWarmUpPageAsync(
        int pageSize,
        CancellationToken cancellationToken = default
    ) => _productPickerSlice.GetHomePageWarmUpPageAsync(pageSize, cancellationToken);

    public Task<PagedListReactDto<StoreOrderProductDto>> GetHomePageCachePageAsync(
        int pageSize,
        CancellationToken cancellationToken = default
    ) => _productPickerSlice.GetHomePageCachePageAsync(pageSize, cancellationToken);

    public Task<ApiResponse<List<StoreOrderBatchLookupItemDto>>> BatchLookupProductsAsync(
        StoreOrderBatchLookupRequestDto request
    ) => _productPickerSlice.BatchLookupProductsAsync(request);

    public Task<ApiResponse<StoreOrderScanLookupResultDto>> ScanLookupProductsAsync(
        StoreOrderScanLookupRequestDto request
    ) => _productPickerSlice.ScanLookupProductsAsync(request);

    public Task<ApiResponse<StoreOrderScanLookupAddResultDto>> ScanLookupAndAddToCartMutationAsync(
        StoreOrderScanLookupAddRequestDto request
    ) => _cartSlice.ScanLookupAndAddToCartMutationAsync(request);

    public Task<ApiResponse<StoreOrderCartDto?>> GetActiveCartAsync(string storeCode) =>
        _cartSlice.GetActiveCartAsync(storeCode);

    public Task<ApiResponse<StoreOrderCartDto?>> GetActiveCartSummaryAsync(
        string storeCode
    ) => _cartSlice.GetActiveCartSummaryAsync(storeCode);

    public Task<ApiResponse<StoreOrderCartDto?>> AddToCartAsync(
        AddToCartRequestDto request
    ) => _cartSlice.AddToCartAsync(request);

    public Task<ApiResponse<StoreOrderCartMutationResultDto?>> AddToCartMutationAsync(
        AddToCartRequestDto request
    ) => _cartSlice.AddToCartMutationAsync(request);

    public Task<ApiResponse<StoreOrderCartDto?>> UpdateCartItemAsync(
        AddToCartRequestDto request
    ) => _cartSlice.UpdateCartItemAsync(request);

    public Task<ApiResponse<StoreOrderCartMutationResultDto?>> UpdateCartItemMutationAsync(
        AddToCartRequestDto request
    ) => _cartSlice.UpdateCartItemMutationAsync(request);

    public Task<ApiResponse<bool>> RemoveFromCartAsync(
        RemoveFromCartRequestDto request
    ) => _cartSlice.RemoveFromCartAsync(request);

    public Task<ApiResponse<StoreOrderCartDto?>> ClearCartAsync(string storeCode) =>
        _cartSlice.ClearCartAsync(storeCode);

    public Task<ApiResponse<SubmitStoreOrderResultDto>> SubmitOrderAsync(
        SubmitStoreOrderRequestDto request
    ) => _orderPlacementSlice.SubmitOrderAsync(request);

    public Task<ApiResponse<List<StoreOrderDynamicDataDto>>> GetProductsDynamicDataAsync(
        StoreOrderDynamicDataRequestDto request
    ) => _productHistorySlice.GetProductsDynamicDataAsync(request);

    public Task<ApiResponse<PagedListReactDto<StoreOrderProductOrderHistoryItemDto>>> GetProductOrderHistoryAsync(
        StoreOrderProductOrderHistoryRequestDto request
    ) => _productHistorySlice.GetProductOrderHistoryAsync(request);

    public Task<ApiResponse<StoreOrderSalesSinceLastArrivalResultDto>> GetSalesSinceLastArrivalAsync(
        StoreOrderSalesSinceLastArrivalRequestDto request
    ) => _productHistorySlice.GetSalesSinceLastArrivalAsync(request);

    public Task<ApiResponse<StoreOrderProductActivityHistoryResultDto>> GetProductActivityHistoryAsync(
        StoreOrderProductActivityHistoryRequestDto request
    ) => _productHistorySlice.GetProductActivityHistoryAsync(request);

    public Task<ApiResponse<List<StoreOrderSalesSinceLastArrivalSummaryItemDto>>> GetSalesSinceLastArrivalSummaryAsync(
        StoreOrderSalesSinceLastArrivalSummaryRequestDto request
    ) => _productHistorySlice.GetSalesSinceLastArrivalSummaryAsync(request);

    public Task<PagedListReactDto<StoreOrderListItemDto>> GetOrderListAsync(
        StoreOrderListFilterDto filter
    ) => _ordersSlice.GetOrderListAsync(filter);

    public Task<ApiResponse<StoreOrderImportPriceVarianceResultDto>> GetImportPriceVarianceAsync(
        StoreOrderImportPriceVarianceQueryDto query
    ) => _importPriceVarianceSlice.GetImportPriceVarianceAsync(query);

    public Task<ApiResponse<StoreOrderImportPriceVarianceDetailResultDto>> GetImportPriceVarianceDetailsAsync(
        StoreOrderImportPriceVarianceDetailQueryDto query
    ) => _importPriceVarianceSlice.GetImportPriceVarianceDetailsAsync(query);

    public Task<ApiResponse<StoreOrderImportPriceVarianceDomesticPriceUpdateResultDto>> UpdateImportPriceVarianceDomesticPriceAsync(
        StoreOrderImportPriceVarianceDomesticPriceUpdateDto request
    ) => _importPriceVarianceSlice.UpdateImportPriceVarianceDomesticPriceAsync(request);

    public Task<ApiResponse<StoreOrderImportPriceVarianceWarehouseImportPriceUpdateResultDto>> UpdateImportPriceVarianceWarehouseImportPriceAsync(
        StoreOrderImportPriceVarianceWarehouseImportPriceUpdateDto request
    ) => _importPriceVarianceSlice.UpdateImportPriceVarianceWarehouseImportPriceAsync(
        request
    );

    public Task<ApiResponse<StoreOrderImportPriceVarianceWarehouseImportPriceBatchUpdateResultDto>> UpdateImportPriceVarianceWarehouseImportPriceBatchAsync(
        StoreOrderImportPriceVarianceWarehouseImportPriceBatchUpdateDto request
    ) => _importPriceVarianceSlice.UpdateImportPriceVarianceWarehouseImportPriceBatchAsync(
        request
    );

    public Task<ApiResponse<StoreOrderDetailDto?>> GetOrderDetailAsync(
        string orderGuid,
        StoreOrderDetailQueryDto? query = null
    ) => _ordersSlice.GetOrderDetailAsync(orderGuid, query);

    public Task<ApiResponse<StoreOrderCartDto?>> GetOrderDetailFullAsync(
        string orderGuid
    ) => _ordersSlice.GetOrderDetailFullAsync(orderGuid);

    public Task<ApiResponse<StoreOrderStoreContactDto>> UpdateStoreContactAsync(
        UpdateStoreOrderStoreContactDto request
    ) => _ordersSlice.UpdateStoreContactAsync(request);

    public Task<ApiResponse<List<string>>> GetOrderDetailProductCodesAsync(
        string orderGuid
    ) => _ordersSlice.GetOrderDetailProductCodesAsync(orderGuid);

    public Task<ApiResponse<List<BranchDto>>> GetUsedBranchesAsync() =>
        _ordersSlice.GetUsedBranchesAsync();

    public Task<ApiResponse<List<UnmatchedStoreOrderGroupDto>>> GetUnmatchedStoreOrderGroupsAsync() =>
        _ordersSlice.GetUnmatchedStoreOrderGroupsAsync();

    public Task<ApiResponse<BatchMapStoreOrderStoreCodeResultDto>> BatchMapStoreOrderStoreCodeAsync(
        BatchMapStoreOrderStoreCodeDto request
    ) => _ordersSlice.BatchMapStoreOrderStoreCodeAsync(request);

    public Task<ApiResponse<string>> CreateOrderAsync(CreateStoreOrderDto request) =>
        _orderPlacementSlice.CreateOrderAsync(request);

    public Task<ApiResponse<bool>> AddOrderLineAsync(AddOrderLineDto request) =>
        _orderManagementSlice.AddOrderLineAsync(request);

    public Task<ApiResponse<bool>> BatchAddOrderLineAsync(
        BatchAddOrderLineDto request
    ) => _orderManagementSlice.BatchAddOrderLineAsync(request);

    public Task<ApiResponse<bool>> PasteReplaceOrderLinesAsync(
        PasteReplaceOrderLinesDto request
    ) => _pasteReplaceExecutor.PasteReplaceOrderLinesAsync(request);

    public Task<ApiResponse<bool>> UpdateOrderLineAsync(UpdateOrderLineDto request) =>
        _orderManagementSlice.UpdateOrderLineAsync(request);

    public Task<ApiResponse<bool>> RemoveOrderLineAsync(RemoveOrderLineDto request) =>
        _orderManagementSlice.RemoveOrderLineAsync(request);

    public Task<ApiResponse<bool>> BatchUpdateOrderLineAsync(
        BatchUpdateOrderLineDto request
    ) => _orderManagementSlice.BatchUpdateOrderLineAsync(request);

    public Task<ApiResponse<RefreshStoreOrderImportPricesResultDto>> RefreshOrderLineImportPricesAsync(
        RefreshStoreOrderImportPricesDto request
    ) => _orderManagementSlice.RefreshOrderLineImportPricesAsync(request);

    public Task<ApiResponse<bool>> UpdateOrderHeaderAsync(
        UpdateOrderHeaderDto request
    ) => _orderManagementSlice.UpdateOrderHeaderAsync(request);

    public Task<ApiResponse<bool>> UpdateOrderOutboundDateAsync(
        UpdateOrderOutboundDateDto request
    ) => _orderManagementSlice.UpdateOrderOutboundDateAsync(request);

    public Task<ApiResponse<bool>> DeleteOrderAsync(string orderGuid) =>
        _orderManagementSlice.DeleteOrderAsync(orderGuid);

    public Task<ApiResponse<bool>> UpdateProductStatusAsync(
        UpdateProductStatusDto request
    ) => _orderManagementSlice.UpdateProductStatusAsync(request);

    public Task<ApiResponse<bool>> BatchUpdateProductStatusAsync(
        BatchUpdateProductStatusDto request
    ) => _orderManagementSlice.BatchUpdateProductStatusAsync(request);

    public Task<ApiResponse<CopyOrderResultDto>> CopyOrderAsync(CopyOrderDto request) =>
        _orderPlacementSlice.CopyOrderAsync(request);

    public Task<SyncMissingOrdersResultDto> SyncMissingOrdersFromHqAsync(
        SyncMissingOrdersRequestDto? request
    ) => _missingOrdersSyncExecutor.SyncMissingOrdersFromHqAsync(request);

    public Task<ApiResponse<bool>> CompleteOrderAsync(string orderGuid) =>
        _lifecycleSlice.CompleteOrderAsync(orderGuid);

    public Task<ApiResponse<bool>> StartPickingAsync(string orderGuid) =>
        _lifecycleSlice.StartPickingAsync(orderGuid);

    public Task<ApiResponse<bool>> UpdateOrderStatusAsync(
        string orderGuid,
        int newStatus,
        bool bypassPreorderGate = false
    ) => _lifecycleSlice.UpdateOrderStatusAsync(
        orderGuid,
        newStatus,
        bypassPreorderGate
    );

    public Task<ApiResponse<int>> BatchUpdateOrderStatusAsync(
        List<string> orderGuids,
        int newStatus,
        bool bypassPreorderGate = false
    ) => _lifecycleSlice.BatchUpdateOrderStatusAsync(
        orderGuids,
        newStatus,
        bypassPreorderGate
    );
}
