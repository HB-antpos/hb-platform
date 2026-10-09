using System.Data;
using System.Linq;
using System.Text.Json;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.HqEntities;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Npgsql;
using BlazorApp.Api.Features.LocalSupplierInvoices;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    /// <summary>保持既有 DI 构造与接口契约的纯委派 façade。</summary>
    public class LocalSupplierInvoicesReactService : ILocalSupplierInvoicesReactService
    {
        private readonly LocalSupplierInvoicesQueriesHandler _queries;
        private readonly LocalSupplierInvoicesHeaderHandler _header;
        private readonly LocalSupplierInvoicesDetailsHandler _details;
        private readonly LocalSupplierInvoicesProductReviewHandler _productReview;
        private readonly LocalSupplierInvoicesPricingHandler _pricing;
        private readonly LocalSupplierInvoicesProductLookupHandler _productLookup;
        private readonly LocalSupplierInvoicesProductExecutionHandler _productExecution;
        private readonly LocalSupplierInvoicesHqSyncHandler _hqSync;

        public LocalSupplierInvoicesReactService(
            SqlSugarContext context,
            HqSqlSugarContext hqContext,
            IMapper mapper,
            ILogger<LocalSupplierInvoicesReactService> logger,
            IAutoPricingService autoPricingService,
            IWarehouseProductChangeHistoryService changeHistoryService,
            ILocalSupplierInvoiceHqProductSyncService? hqProductSyncService = null)
        {
            // 保持原构造函数与 DI 注册不变；各切片共享同一组已注入依赖。
            var dependencies = new LocalSupplierInvoicesDependencies(context, hqContext, mapper, logger, autoPricingService, changeHistoryService, hqProductSyncService);
            _queries = new LocalSupplierInvoicesQueriesHandler(dependencies);
            _header = new LocalSupplierInvoicesHeaderHandler(dependencies);
            _details = new LocalSupplierInvoicesDetailsHandler(dependencies);
            _productReview = new LocalSupplierInvoicesProductReviewHandler(dependencies);
            _pricing = new LocalSupplierInvoicesPricingHandler(dependencies);
            _productLookup = new LocalSupplierInvoicesProductLookupHandler(dependencies);
            _productExecution = new LocalSupplierInvoicesProductExecutionHandler(dependencies);
            _hqSync = new LocalSupplierInvoicesHqSyncHandler(dependencies);
        }

        public Task<GridResponseDto<LocalSupplierInvoiceListDto>> GetGridDataAsync(GridRequestDto request) => _queries.GetGridDataAsync(request);
        public Task<GridResponseDto<LocalSupplierInvoiceListDto>> GetGridDataAsync(GridRequestDto request, List<string>? allowedStoreCodes) => _queries.GetGridDataAsync(request, allowedStoreCodes);
        public Task<ApiResponse<List<LocalSupplierInvoiceStoreCountDto>>> GetStoreCountsAsync(GridRequestDto request, List<string>? allowedStoreCodes) => _queries.GetStoreCountsAsync(request, allowedStoreCodes);
        public Task<ApiResponse<LocalSupplierInvoiceFilterOptionsDto>> GetFilterOptionsAsync(List<string>? allowedStoreCodes, string? storeCode) => _queries.GetFilterOptionsAsync(allowedStoreCodes, storeCode);
        public Task<ApiResponse<LocalSupplierInvoiceDetailDto>> GetInvoiceAsync(string invoiceGuid) => _queries.GetInvoiceAsync(invoiceGuid);
        public Task<ApiResponse<List<LocalSupplierInvoiceItemDto>>> GetDetailsAsync(string invoiceGuid) => _queries.GetDetailsAsync(invoiceGuid);
        public Task<GridResponseDto<LocalSupplierInvoiceItemDto>> GetDetailsGridAsync(string invoiceGuid, GridRequestDto request) => _queries.GetDetailsGridAsync(invoiceGuid, request);
        public Task<ApiResponse<string>> CreateAsync(CreateInvoiceRequest dto) => _header.CreateAsync(dto);
        public Task<ApiResponse<bool>> DeleteAsync(string invoiceGuid, string updatedBy) => _header.DeleteAsync(invoiceGuid, updatedBy);
        public Task<ApiResponse<List<SupplierItemDetectResult>>> DetectSupplierItemAsync(DetectSupplierItemRequest dto) => _productReview.DetectSupplierItemAsync(dto);
        public Task<ApiResponse<List<BarcodeDetectResult>>> DetectBarcodeAsync(DetectBarcodeRequest dto) => _productReview.DetectBarcodeAsync(dto);
        public Task<ApiResponse<bool>> UpdateAsync(string invoiceGuid, UpdateInvoiceRequest dto) => _header.UpdateAsync(invoiceGuid, dto);
        public Task<ApiResponse<BatchResultDto>> BatchUpsertDetailsAsync(string invoiceGuid, List<InvoiceDetailUpsertItemDto> items, string updatedBy) => _details.BatchUpsertDetailsAsync(invoiceGuid, items, updatedBy);
        public Task<ApiResponse<BatchResultDto>> BatchUpdateDetailsAsync(string invoiceGuid, BatchUpdateInvoiceDetailsRequest request, string updatedBy) => _details.BatchUpdateDetailsAsync(invoiceGuid, request, updatedBy);
        public Task<ApiResponse<UpdateToStorePricesResultDto>> UpdateDetailsToStorePricesAsync(UpdateToStorePricesRequest dto, string updatedBy) => _pricing.UpdateDetailsToStorePricesAsync(dto, updatedBy);
        public Task<ApiResponse<UpdateToStorePricesResultDto>> UpdateDetailsToStorePricesAsync(UpdateToStorePricesRequest dto, string updatedBy, int lockWaitMilliseconds) => _pricing.UpdateDetailsToStorePricesAsync(dto, updatedBy, lockWaitMilliseconds);
        public Task<ApiResponse<UpdateLastPurchasePricesResultDto>> UpdateLastPurchasePricesAsync(string invoiceGuid, UpdateLastPurchasePricesRequest request, string updatedBy) => _pricing.UpdateLastPurchasePricesAsync(invoiceGuid, request, updatedBy);
        public Task<ApiResponse<CheckProductsResponseDto>> CheckProductsAsync(CheckProductsRequest dto) => _productReview.CheckProductsAsync(dto);
        public Task<ApiResponse<BatchResultDto>> PasteDetailsAsync(PasteDetailsRequest dto, string updatedBy) => _details.PasteDetailsAsync(dto, updatedBy);
        public Task<ApiResponse<bool>> UpdateDetailActionAsync(string invoiceGuid, string detailGuid, int action) => _details.UpdateDetailActionAsync(invoiceGuid, detailGuid, action);
        public Task<ApiResponse<BatchResultDto>> BatchUpdateDetailActionAsync(string invoiceGuid, BatchUpdateDetailActionRequest dto) => _details.BatchUpdateDetailActionAsync(invoiceGuid, dto);
        public Task<ApiResponse<bool>> DeleteDetailsAsync(string invoiceGuid, List<string> detailGuids, string updatedBy) => _details.DeleteDetailsAsync(invoiceGuid, detailGuids, updatedBy);
        public Task<ApiResponse<GetBarcodeAbnormalDetailsResponse>> GetBarcodeAbnormalDetailsAsync(string invoiceGuid) => _details.GetBarcodeAbnormalDetailsAsync(invoiceGuid);
        public Task<ApiResponse<GetProductsByBarcodeResponse>> GetProductsByBarcodeAsync(string invoiceGuid, string barcode) => _productLookup.GetProductsByBarcodeAsync(invoiceGuid, barcode);
        public Task<ApiResponse<GetProductsByProductCodeResponse>> GetProductsByProductCodeAsync(string invoiceGuid, string productCode) => _productLookup.GetProductsByProductCodeAsync(invoiceGuid, productCode);
        public Task<ApiResponse<InvoiceNoCheckResult>> CheckInvoiceNoExistsAsync(string storeCode, string supplierCode, string invoiceNo) => _productLookup.CheckInvoiceNoExistsAsync(storeCode, supplierCode, invoiceNo);
        public Task<ApiResponse<BatchExecuteActionsResultDto>> BatchExecuteActionsAsync(string invoiceGuid, List<string> detailGuids, string userName, List<BatchExecuteNewProductProductTypeSelectionDto>? newProductProductTypeSelections = null, List<BatchExecuteExpectedActionDto>? expectedActions = null, IReadOnlyCollection<StoreLocalSupplierInvoiceDetails>? confirmedDetails = null, bool confirmedLargePriceChange = false, IReadOnlyCollection<string>? targetStoreCodes = null) => _productExecution.BatchExecuteActionsAsync(invoiceGuid, detailGuids, userName, newProductProductTypeSelections, expectedActions, confirmedDetails, confirmedLargePriceChange, targetStoreCodes);
        public Task<SyncResult> PushInvoicesToHqAsync(List<string> invoiceGuids) => _hqSync.PushInvoicesToHqAsync(invoiceGuids);

        // 保留既有反射测试入口；连接隔离与并发实现仍由 ProductReview 切片拥有。
        private Task<List<T>> QueryInChunksParallelAsync<T, TKey>(
            IReadOnlyList<TKey> keys,
            int chunkSize,
            Func<ISqlSugarClient, List<TKey>, Task<List<T>>> fetch,
            int maxConcurrency = 5
        ) => _productReview.QueryInChunksParallelAsync(keys, chunkSize, fetch, maxConcurrency);
    }
}
