using BlazorApp.Api.Controllers.React.StoreOrders;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.StoreOrders.Common;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace BlazorApp.Api.Controllers.React;

/// <summary>
/// 旧控制器构造与直接调用测试的兼容入口；HTTP 路由由 StoreOrders 子控制器承载。
/// </summary>
[NonController]
[ApiController]
[Route("api/react/v1/store-order")]
[Authorize]
public class ReactStoreOrderController : ControllerBase
{
    private readonly HttpContextAccessor _httpContextAccessor = new();
    private readonly StoreOrderProductController _productController;
    private readonly StoreOrderCartController _cartController;
    private readonly StoreOrderHistoryController _historyController;
    private readonly StoreOrderQueryController _queryController;
    private readonly StoreOrderImportPriceVarianceController _importController;
    private readonly StoreOrderInvoiceController _invoiceController;
    private readonly StoreOrderManagementController _managementController;
    private readonly StoreOrderSyncController _syncController;
    private readonly StoreOrderLifecycleController _lifecycleController;

    public ReactStoreOrderController(
        IStoreOrderReactService service,
        ILogger<ReactStoreOrderController> logger,
        IMemoryCache cache,
        SqlSugarContext dbContext,
        IUserService userService,
        IStoreService storeService,
        IAuthorizationService authorizationService,
        ICurrentUserManageableStoreScopeService storeScopeService,
        IStoreOrderSyncJobService storeOrderSyncJobService,
        IStoreOrderInvoiceEmailJobService invoiceEmailJobService,
        IStoreOrderPasteReplaceJobService pasteReplaceJobService,
        IStoreOrderInvoiceEmailTextTranslationService invoiceEmailTextTranslationService,
        IPreorderGateService preorderGateService
    )
    {
        _ = logger;
        _ = storeService;
        _ = preorderGateService;

        var adapter = new LegacyStoreOrderSliceAdapter(service);
        var accessPolicy = new StoreOrderAccessPolicy(
            new StoreOrderActorContext(_httpContextAccessor),
            authorizationService,
            storeScopeService,
            userService,
            new SqlSugarStoreOrderAccessOrderReader(dbContext),
            _httpContextAccessor,
            cache,
            NullLogger<StoreOrderAccessPolicy>.Instance
        );

        _productController = new StoreOrderProductController(
            adapter,
            adapter,
            accessPolicy,
            NullLogger<StoreOrderProductController>.Instance
        );
        _cartController = new StoreOrderCartController(
            adapter,
            adapter,
            accessPolicy,
            NullLogger<StoreOrderCartController>.Instance
        );
        _historyController = new StoreOrderHistoryController(
            adapter,
            accessPolicy,
            NullLogger<StoreOrderHistoryController>.Instance
        );
        _queryController = new StoreOrderQueryController(
            adapter,
            accessPolicy,
            NullLogger<StoreOrderQueryController>.Instance
        );
        _importController = new StoreOrderImportPriceVarianceController(
            adapter,
            accessPolicy,
            NullLogger<StoreOrderImportPriceVarianceController>.Instance
        );
        _invoiceController = new StoreOrderInvoiceController(
            invoiceEmailJobService,
            invoiceEmailTextTranslationService,
            accessPolicy,
            NullLogger<StoreOrderInvoiceController>.Instance
        );
        _managementController = new StoreOrderManagementController(
            adapter,
            adapter,
            adapter,
            pasteReplaceJobService,
            accessPolicy,
            NullLogger<StoreOrderManagementController>.Instance
        );
        _syncController = new StoreOrderSyncController(
            adapter,
            storeOrderSyncJobService,
            accessPolicy,
            NullLogger<StoreOrderSyncController>.Instance
        );
        _lifecycleController = new StoreOrderLifecycleController(
            adapter,
            accessPolicy,
            NullLogger<StoreOrderLifecycleController>.Instance
        );
    }

    [HttpPost("products")]
    public Task<IActionResult> GetProducts([FromBody] StoreOrderFilterDto filter) =>
        Prepare(_productController).GetProducts(filter);

    [HttpPost("products/batch-lookup")]
    public Task<IActionResult> BatchLookupProducts(
        [FromBody] StoreOrderBatchLookupRequestDto request
    ) => Prepare(_productController).BatchLookupProducts(request);

    [HttpPost("products/scan-lookup")]
    public Task<IActionResult> ScanLookupProducts(
        [FromBody] StoreOrderScanLookupRequestDto request
    ) => Prepare(_productController).ScanLookupProducts(request);

    [HttpPost("cart/scan-lookup-add")]
    public Task<IActionResult> ScanLookupAndAddToCart(
        [FromBody] StoreOrderScanLookupAddRequestDto request
    ) => Prepare(_cartController).ScanLookupAndAddToCart(request);

    [HttpGet("cart/{storeCode}")]
    public Task<IActionResult> GetActiveCart(string storeCode) =>
        Prepare(_cartController).GetActiveCart(storeCode);

    [HttpGet("cart/{storeCode}/summary")]
    public Task<IActionResult> GetActiveCartSummary(string storeCode) =>
        Prepare(_cartController).GetActiveCartSummary(storeCode);

    [HttpPost("cart/add")]
    [HttpPost("cart/scan-add")]
    public Task<IActionResult> AddToCart([FromBody] AddToCartRequestDto request) =>
        Prepare(_cartController).AddToCart(request);

    [HttpPost("cart/update")]
    [HttpPost("cart/scan-update")]
    public Task<IActionResult> UpdateCartItem([FromBody] AddToCartRequestDto request) =>
        Prepare(_cartController).UpdateCartItem(request);

    [HttpPost("cart/remove")]
    public Task<IActionResult> RemoveFromCart(
        [FromBody] RemoveFromCartRequestDto request
    ) => Prepare(_cartController).RemoveFromCart(request);

    [HttpPost("cart/clear")]
    public Task<IActionResult> ClearCart([FromBody] ClearCartRequestDto request) =>
        Prepare(_cartController).ClearCart(request);

    [HttpPost("submit")]
    public Task<IActionResult> SubmitOrder(
        [FromBody] SubmitStoreOrderRequestDto request
    ) => Prepare(_cartController).SubmitOrder(request);

    [HttpPost("dynamic-data")]
    public Task<IActionResult> GetDynamicData(
        [FromBody] StoreOrderDynamicDataRequestDto request
    ) => Prepare(_historyController).GetDynamicData(request);

    [HttpPost("product-order-history")]
    public Task<IActionResult> GetProductOrderHistory(
        [FromBody] StoreOrderProductOrderHistoryRequestDto request
    ) => Prepare(_historyController).GetProductOrderHistory(request);

    [HttpPost("product-activity-history")]
    public Task<IActionResult> GetProductActivityHistory(
        [FromBody] StoreOrderProductActivityHistoryRequestDto request
    ) => Prepare(_historyController).GetProductActivityHistory(request);

    [HttpPost("sales-since-last-arrival")]
    public Task<IActionResult> GetSalesSinceLastArrival(
        [FromBody] StoreOrderSalesSinceLastArrivalRequestDto request
    ) => Prepare(_historyController).GetSalesSinceLastArrival(request);

    [HttpPost("sales-since-last-arrival/summary")]
    public Task<IActionResult> GetSalesSinceLastArrivalSummary(
        [FromBody] StoreOrderSalesSinceLastArrivalSummaryRequestDto request
    ) => Prepare(_historyController).GetSalesSinceLastArrivalSummary(request);

    [HttpPost("list")]
    public Task<IActionResult> GetOrderList([FromBody] StoreOrderListFilterDto filter) =>
        Prepare(_queryController).GetOrderList(filter);

    [HttpPost("import-price-variance")]
    public Task<IActionResult> GetImportPriceVariance(
        [FromBody] StoreOrderImportPriceVarianceQueryDto query
    ) => Prepare(_importController).GetImportPriceVariance(query);

    [HttpPost("import-price-variance/details")]
    public Task<IActionResult> GetImportPriceVarianceDetails(
        [FromBody] StoreOrderImportPriceVarianceDetailQueryDto query
    ) => Prepare(_importController).GetImportPriceVarianceDetails(query);

    [HttpPost("import-price-variance/domestic-price")]
    public Task<IActionResult> UpdateImportPriceVarianceDomesticPrice(
        [FromBody] StoreOrderImportPriceVarianceDomesticPriceUpdateDto request
    ) => Prepare(_importController).UpdateImportPriceVarianceDomesticPrice(request);

    [HttpPost("import-price-variance/warehouse-import-price")]
    public Task<IActionResult> UpdateImportPriceVarianceWarehouseImportPrice(
        [FromBody] StoreOrderImportPriceVarianceWarehouseImportPriceUpdateDto request
    ) => Prepare(_importController).UpdateImportPriceVarianceWarehouseImportPrice(
        request
    );

    [HttpPost("import-price-variance/warehouse-import-price/batch")]
    public Task<IActionResult> UpdateImportPriceVarianceWarehouseImportPriceBatch(
        [FromBody] StoreOrderImportPriceVarianceWarehouseImportPriceBatchUpdateDto request
    ) => Prepare(_importController).UpdateImportPriceVarianceWarehouseImportPriceBatch(
        request
    );

    [HttpGet("detail/{orderGuid}")]
    public Task<IActionResult> GetOrderDetail(
        string orderGuid,
        [FromQuery] StoreOrderDetailQueryDto query
    ) => Prepare(_queryController).GetOrderDetail(orderGuid, query);

    [HttpGet("detail/{orderGuid}/full")]
    public Task<IActionResult> GetOrderDetailFull(string orderGuid) =>
        Prepare(_queryController).GetOrderDetailFull(orderGuid);

    [HttpPost("store-contact/update")]
    public Task<IActionResult> UpdateStoreContact(
        [FromBody] UpdateStoreOrderStoreContactDto request
    ) => Prepare(_queryController).UpdateStoreContact(request);

    [HttpPost("invoice/email")]
    public Task<IActionResult> SendInvoiceEmail(
        [FromBody] SendStoreOrderInvoiceEmailDto request
    ) => Prepare(_invoiceController).SendInvoiceEmail(request);

    [HttpPost("invoice/email/translate-text")]
    public Task<IActionResult> TranslateInvoiceEmailText(
        [FromBody] StoreOrderInvoiceEmailTextTranslationRequestDto request
    ) => Prepare(_invoiceController).TranslateInvoiceEmailText(request);

    [HttpGet("invoice/email/jobs/{jobId}")]
    public Task<IActionResult> GetInvoiceEmailJob(string jobId) =>
        Prepare(_invoiceController).GetInvoiceEmailJob(jobId);

    [HttpGet("detail/{orderGuid}/product-codes")]
    public Task<IActionResult> GetOrderDetailProductCodes(string orderGuid) =>
        Prepare(_queryController).GetOrderDetailProductCodes(orderGuid);

    [HttpPost("create")]
    public Task<IActionResult> CreateOrder([FromBody] CreateStoreOrderDto request) =>
        Prepare(_managementController).CreateOrder(request);

    [HttpPost("line/add")]
    public Task<IActionResult> AddOrderLine([FromBody] AddOrderLineDto request) =>
        Prepare(_managementController).AddOrderLine(request);

    [HttpPost("line/batch-add")]
    public Task<IActionResult> BatchAddOrderLine(
        [FromBody] BatchAddOrderLineDto request
    ) => Prepare(_managementController).BatchAddOrderLine(request);

    [HttpPost("line/paste-replace")]
    public Task<IActionResult> PasteReplaceOrderLines(
        [FromBody] PasteReplaceOrderLinesDto request
    ) => Prepare(_managementController).PasteReplaceOrderLines(request);

    [HttpPost("line/paste-replace/jobs")]
    public Task<IActionResult> CreatePasteReplaceOrderLinesJob(
        [FromBody] PasteReplaceOrderLinesDto request
    ) => Prepare(_managementController).CreatePasteReplaceOrderLinesJob(request);

    [HttpGet("line/paste-replace/jobs/{jobId}")]
    public Task<IActionResult> GetPasteReplaceOrderLinesJob(string jobId) =>
        Prepare(_managementController).GetPasteReplaceOrderLinesJob(jobId);

    [HttpPost("line/update")]
    public Task<IActionResult> UpdateOrderLine(
        [FromBody] UpdateOrderLineDto request
    ) => Prepare(_managementController).UpdateOrderLine(request);

    [HttpPost("line/remove")]
    public Task<IActionResult> RemoveOrderLine(
        [FromBody] RemoveOrderLineDto request
    ) => Prepare(_managementController).RemoveOrderLine(request);

    [HttpPost("line/batch-update")]
    public Task<IActionResult> BatchUpdateOrderLine(
        [FromBody] BatchUpdateOrderLineDto request
    ) => Prepare(_managementController).BatchUpdateOrderLine(request);

    [HttpPost("line/refresh-import-prices")]
    public Task<IActionResult> RefreshOrderLineImportPrices(
        [FromBody] RefreshStoreOrderImportPricesDto request
    ) => Prepare(_managementController).RefreshOrderLineImportPrices(request);

    [HttpPost("product/status")]
    public Task<IActionResult> UpdateProductStatus(
        [FromBody] UpdateProductStatusDto request
    ) => Prepare(_managementController).UpdateProductStatus(request);

    [HttpPost("product/batch-status")]
    public Task<IActionResult> BatchUpdateProductStatus(
        [FromBody] BatchUpdateProductStatusDto request
    ) => Prepare(_managementController).BatchUpdateProductStatus(request);

    [HttpPost("header/update")]
    public Task<IActionResult> UpdateOrderHeader(
        [FromBody] UpdateOrderHeaderDto request
    ) => Prepare(_managementController).UpdateOrderHeader(request);

    [HttpPost("outbound-date")]
    public Task<IActionResult> UpdateOrderOutboundDate(
        [FromBody] UpdateOrderOutboundDateDto request
    ) => Prepare(_managementController).UpdateOrderOutboundDate(request);

    [HttpGet("used-branches")]
    public Task<IActionResult> GetUsedBranches() =>
        Prepare(_queryController).GetUsedBranches();

    [HttpGet("unmatched-store-groups")]
    public Task<IActionResult> GetUnmatchedStoreGroups() =>
        Prepare(_queryController).GetUnmatchedStoreGroups();

    [HttpPost("batch-map-store-code")]
    public Task<IActionResult> BatchMapStoreCode(
        [FromBody] BatchMapStoreOrderStoreCodeDto request
    ) => Prepare(_queryController).BatchMapStoreCode(request);

    [HttpGet("accessible-branches")]
    public Task<IActionResult> GetAccessibleBranches() =>
        Prepare(_queryController).GetAccessibleBranches();

    [HttpDelete("{orderGuid}")]
    public Task<IActionResult> DeleteOrder(string orderGuid) =>
        Prepare(_managementController).DeleteOrder(orderGuid);

    [HttpPost("copy")]
    public Task<IActionResult> CopyOrder([FromBody] CopyOrderDto request) =>
        Prepare(_managementController).CopyOrder(request);

    [HttpPost("sync-missing-orders")]
    public Task<IActionResult> SyncMissingOrders(
        [FromBody] SyncMissingOrdersRequestDto? request
    ) => Prepare(_syncController).SyncMissingOrders(request);

    [HttpPost("sync-missing-orders/jobs")]
    public Task<IActionResult> CreateSyncMissingOrdersJob(
        [FromBody] SyncMissingOrdersRequestDto? request
    ) => Prepare(_syncController).CreateSyncMissingOrdersJob(request);

    [HttpGet("sync-missing-orders/jobs/{jobId}")]
    public Task<IActionResult> GetSyncMissingOrdersJob(string jobId) =>
        Prepare(_syncController).GetSyncMissingOrdersJob(jobId);

    [HttpPost("hq-sync/full/jobs")]
    public Task<IActionResult> CreateStoreOrderHqFullSyncJob(
        [FromBody] StoreOrderHqSyncRequestDto? request
    ) => Prepare(_syncController).CreateStoreOrderHqFullSyncJob(request);

    [HttpPost("hq-sync/incremental/jobs")]
    public Task<IActionResult> CreateStoreOrderHqIncrementalSyncJob(
        [FromBody] StoreOrderHqSyncRequestDto? request
    ) => Prepare(_syncController).CreateStoreOrderHqIncrementalSyncJob(request);

    [HttpGet("hq-sync/jobs/{jobId}")]
    public Task<IActionResult> GetStoreOrderHqSyncJob(string jobId) =>
        Prepare(_syncController).GetStoreOrderHqSyncJob(jobId);

    [HttpPost("complete/{orderGuid}")]
    public Task<IActionResult> CompleteOrder(string orderGuid) =>
        Prepare(_lifecycleController).CompleteOrder(orderGuid);

    [HttpPost("start-picking/{orderGuid}")]
    public Task<IActionResult> StartPicking(string orderGuid) =>
        Prepare(_lifecycleController).StartPicking(orderGuid);

    [HttpPost("status")]
    public Task<IActionResult> UpdateOrderStatus(
        [FromBody] UpdateOrderStatusDto request
    ) => Prepare(_lifecycleController).UpdateOrderStatus(request);

    [HttpPost("batch-status")]
    public Task<IActionResult> BatchUpdateOrderStatus(
        [FromBody] BatchUpdateOrderStatusDto request
    ) => Prepare(_lifecycleController).BatchUpdateOrderStatus(request);

    private TController Prepare<TController>(TController controller)
        where TController : ControllerBase
    {
        _httpContextAccessor.HttpContext = ControllerContext.HttpContext;
        controller.ControllerContext = ControllerContext;
        return controller;
    }
}
