using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.ProductWarehouse;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Services.React;

/// <summary>
/// 旧接口的兼容入口。业务、事务、SQL、锁与映射均由 ProductWarehouse 垂直切片负责。
/// </summary>
public class ProductWarehouseReactService : IProductWarehouseReactService
{
    private readonly IProductWarehouseDetectionSlice _detection;
    private readonly IProductWarehouseBatchUpdateSlice _batchUpdate;
    private readonly IProductWarehouseBatchCreationSlice _batchCreation;
    private readonly IProductWarehouseTableSlice _table;
    private readonly IProductWarehouseSingleCreationSlice _singleCreation;
    private readonly IProductWarehouseImportSlice _import;
    private readonly IProductWarehouseUpdateSlice _update;
    private readonly IProductWarehouseBarcodePriceSlice _barcodePrice;
    private readonly IProductWarehouseSyncSlice _sync;
    private readonly IProductWarehouseMobileSlice _mobile;

    public ProductWarehouseReactService(
        SqlSugarContext context,
        HqSqlSugarContext hqContext,
        ILogger<ProductWarehouseReactService> logger,
        IConfiguration configuration,
        ItemBarcodeService itemBarcodeService,
        IMapper mapper,
        IDataSyncFullService dataSyncFullService,
        IWarehouseProductChangeHistoryService changeHistoryService,
        ITranslationService? translationService = null,
        // 可选：用于在仓库自动下发分店价时登记「待换标签」通知；缺省时该功能静默关闭。
        IStorePriceUpdateTaskService? priceTaskService = null,
        // 可选：仓库覆盖分店价后同事务登记「分店价 → HQ」同步任务；缺省时不同步 HQ。
        IWarehouseStorePriceHqSyncEnqueuer? hqPriceSyncEnqueuer = null
    )
        : this(
            ProductWarehouseLegacyFactory.Create(
                context,
                hqContext,
                logger,
                configuration,
                itemBarcodeService,
                mapper,
                dataSyncFullService,
                changeHistoryService,
                translationService,
                priceTaskService,
                hqPriceSyncEnqueuer
            )
        ) { }

    internal ProductWarehouseReactService(ProductWarehouseSlices slices)
    {
        _detection = slices.Detection;
        _batchUpdate = slices.BatchUpdate;
        _batchCreation = slices.BatchCreation;
        _table = slices.Table;
        _singleCreation = slices.SingleCreation;
        _import = slices.Import;
        _update = slices.Update;
        _barcodePrice = slices.BarcodePrice;
        _sync = slices.Sync;
        _mobile = slices.Mobile;
    }

    public Task<List<DetectionResultDto>> DetectAsync(List<DetectionItemDto> items) =>
        _detection.DetectAsync(items);

    public Task<BatchOperationResultDto> BatchUpdateAsync(List<UpdateItemDto> items) =>
        _batchUpdate.BatchUpdateAsync(items);

    public Task<BatchOperationResultDto> BatchUpdateAsync(
        List<UpdateItemDto> items,
        string? updatedBy
    ) => _batchUpdate.BatchUpdateAsync(items, updatedBy);

    public Task<WarehouseProductBatchUpdateResultDto> BatchUpdateAsync(
        List<UpdateItemDto> items,
        string? updatedBy,
        WarehouseProductBatchUpdateOptionsDto options
    ) => _batchUpdate.BatchUpdateAsync(items, updatedBy, options);

    public Task<BatchOperationResultDto> BatchCreateAsync(
        List<CreateItemDto> items,
        bool useTransaction = true
    ) => _batchCreation.BatchCreateAsync(items, useTransaction);

    public Task<BatchOperationResultDto> BatchCreateAsync(
        List<CreateItemDto> items,
        bool useTransaction,
        string? updatedBy
    ) => _batchCreation.BatchCreateAsync(items, useTransaction, updatedBy);

    public Task<BatchOperationResultDto> BatchCreateAsync(
        List<CreateItemDto> items,
        bool useTransaction,
        string? updatedBy,
        string auditSource,
        string? sourceReference,
        Guid? batchGuid
    ) => _batchCreation.BatchCreateAsync(
        items,
        useTransaction,
        updatedBy,
        auditSource,
        sourceReference,
        batchGuid
    );

    public Task<BatchOperationResultDto> BatchCreateAsync(
        List<CreateItemDto> items,
        bool useTransaction,
        string? updatedBy,
        string auditSource,
        string? sourceReference,
        Guid? batchGuid,
        string? actorUserGuid
    ) => _batchCreation.BatchCreateAsync(
        items,
        useTransaction,
        updatedBy,
        auditSource,
        sourceReference,
        batchGuid,
        actorUserGuid
    );

    public async Task<ReactTableResponseDto<WarehouseProductReactListDto>> GetAntdTableDataAsync(
        ReactTableRequestDto request
    )
    {
        try
        {
            return await _table.GetAntdTableDataAsync(request);
        }
        catch (ProductWarehouseTableQueryException exception)
        {
            // React 兼容层保留旧异常类型，避免反射和既有调用方感知切片内部的规范类型。
            throw ToLegacyTableQueryException(exception);
        }
    }

    public Task<CreateSingleProductResponseDto> CreateSingleProductAsync(
        CreateSingleProductRequestDto request
    ) => _singleCreation.CreateSingleProductAsync(request);

    public Task<CreateSingleProductResponseDto> CreateSingleProductAsync(
        CreateSingleProductRequestDto request,
        string? updatedBy
    ) => _singleCreation.CreateSingleProductAsync(request, updatedBy);

    public Task<ReactTableResponseDto<DomesticProductNotInWarehouseDto>> GetDomesticProductsNotInWarehouseAsync(
        GetDomesticProductsNotInWarehouseRequestDto request
    ) => _import.GetDomesticProductsNotInWarehouseAsync(request);

    public Task<ImportFromDomesticResponseDto> ImportFromDomesticAsync(
        ImportFromDomesticRequestDto request
    ) => _import.ImportFromDomesticAsync(request);

    public Task<ImportFromDomesticResponseDto> ImportFromDomesticAsync(
        ImportFromDomesticRequestDto request,
        string? updatedBy
    ) => _import.ImportFromDomesticAsync(request, updatedBy);

    public Task<WarehouseProductFullUpdateResultDto> FullUpdateAsync(
        string productCode,
        WarehouseProductFullUpdateDto dto
    ) => _update.FullUpdateAsync(productCode, dto);

    public Task<WarehouseProductFullUpdateResultDto> FullUpdateAsync(
        string productCode,
        WarehouseProductFullUpdateDto dto,
        string? updatedBy
    ) => _update.FullUpdateAsync(productCode, dto, updatedBy);

    public Task<WarehouseProductPatchResultDto?> PatchAsync(
        string productCode,
        WarehouseProductPatchDto dto
    ) => _update.PatchAsync(productCode, dto);

    public Task<WarehouseProductPatchResultDto?> PatchAsync(
        string productCode,
        WarehouseProductPatchDto dto,
        string? updatedBy
    ) => _update.PatchAsync(productCode, dto, updatedBy);

    public Task<List<BarcodePriceItemDto>> GetBarcodePricesAsync(string productCode) =>
        _barcodePrice.GetBarcodePricesAsync(productCode);

    public Task<BatchToggleWarehouseProductsActiveResultDto> BatchToggleActiveAsync(
        BatchToggleWarehouseProductsActiveRequestDto request
    ) => _update.BatchToggleActiveAsync(request);

    public Task<BatchToggleWarehouseProductsActiveResultDto> BatchToggleActiveAsync(
        BatchToggleWarehouseProductsActiveRequestDto request,
        string? updatedBy
    ) => _update.BatchToggleActiveAsync(request, updatedBy);

    public Task<ReactTableResponseDto<NonHotbargainProductNotInWarehouseDto>> GetNonHotbargainProductsNotInWarehouseAsync(
        GetNonHotbargainProductsNotInWarehouseRequestDto request
    ) => _import.GetNonHotbargainProductsNotInWarehouseAsync(request);

    public Task<ImportFromDomesticResponseDto> ImportNonHotbargainProductsAsync(
        ImportNonHotbargainRequestDto request
    ) => _import.ImportNonHotbargainProductsAsync(request);

    public Task<ImportFromDomesticResponseDto> ImportNonHotbargainProductsAsync(
        ImportNonHotbargainRequestDto request,
        string? updatedBy
    ) => _import.ImportNonHotbargainProductsAsync(request, updatedBy);

    public Task<SyncResult> SyncFromHqAsync() => _sync.SyncFromHqAsync();

    public Task<SyncResult> SyncFromHqAsync(string? actorUserGuid, string? actorName) =>
        _sync.SyncFromHqAsync(actorUserGuid, actorName);

    public Task<List<WarehouseMobileProductDto>> LookupMobileProductsAsync(string keyword) =>
        _mobile.LookupMobileProductsAsync(keyword);

    public Task<WarehouseMobileProductDto?> GetMobileProductAsync(string productCode) =>
        _mobile.GetMobileProductAsync(productCode);

    public Task<WarehouseMobileProductDto?> PatchMobileProductAsync(
        string productCode,
        WarehouseMobileProductPatchDto dto
    ) => _mobile.PatchMobileProductAsync(productCode, dto);

    public Task<WarehouseMobileProductDto?> PatchMobileProductAsync(
        string productCode,
        WarehouseMobileProductPatchDto dto,
        string? updatedBy
    ) => _mobile.PatchMobileProductAsync(productCode, dto, updatedBy);

    public Task<WarehouseMobileProductDto?> SetMobileProductLocationAsync(
        string productCode,
        string? locationGuid
    ) => _mobile.SetMobileProductLocationAsync(productCode, locationGuid);

    public Task<WarehouseProductLabelPrintDto?> GetMobileProductPrintPayloadAsync(
        string productCode
    ) => _mobile.GetMobileProductPrintPayloadAsync(productCode);

    public Task<WarehouseLocationLabelPrintDto?> GetMobileLocationPrintPayloadAsync(
        string productCode
    ) => _mobile.GetMobileLocationPrintPayloadAsync(productCode);

    // 兼容两项现有 SQL 生成测试；这里只委派，不持有查询实现。
    private ISugarQueryable<WarehouseProductCodeSearchCandidate> BuildWarehouseTextSearchCandidateQuery(
        string keyword
    ) => _table
        .BuildWarehouseTextSearchCandidateQuery(keyword)
        .Select(candidate => new WarehouseProductCodeSearchCandidate { ProductCode = candidate.ProductCode });

    private ISugarQueryable<WarehouseProductCodeSearchCandidate> BuildWarehouseCodeSearchCandidateQuery(
        string keyword
    ) => _table
        .BuildWarehouseCodeSearchCandidateQuery(keyword)
        .Select(candidate => new WarehouseProductCodeSearchCandidate { ProductCode = candidate.ProductCode });

    private static WarehouseProductTableQueryException ToLegacyTableQueryException(
        ProductWarehouseTableQueryException exception
    ) => new(
        exception.FailedStage,
        new WarehouseProductTableTimingSnapshot(
            exception.Timings.CandidateMs,
            exception.Timings.CountMs,
            exception.Timings.PageMs,
            exception.Timings.LocationMs,
            exception.Timings.RowsMs,
            exception.Timings.MapMs,
            exception.Timings.TotalMs
        ),
        exception.InnerException ?? exception,
        exception.Request is null
            ? null
            : new WarehouseProductTableRequestSnapshot(
                exception.Request.PageNumber,
                exception.Request.PageSize,
                exception.Request.CategoryCount,
                exception.Request.FilterCount,
                exception.Request.KeywordType,
                exception.Request.KeywordLength,
                exception.Request.SortBy,
                exception.Request.SortOrder
            )
    );
}
