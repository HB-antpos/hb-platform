using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HqEntities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Features.ProductWarehouse;

internal sealed record ProductWarehouseSlices(
    IProductWarehouseDetectionSlice Detection,
    IProductWarehouseBatchUpdateSlice BatchUpdate,
    IProductWarehouseBatchCreationSlice BatchCreation,
    IProductWarehouseTableSlice Table,
    IProductWarehouseSingleCreationSlice SingleCreation,
    IProductWarehouseImportSlice Import,
    IProductWarehouseUpdateSlice Update,
    IProductWarehouseBarcodePriceSlice BarcodePrice,
    IProductWarehouseSyncSlice Sync,
    IProductWarehouseMobileSlice Mobile
);

internal static class ProductWarehouseLegacyFactory
{
    internal static ProductWarehouseSlices Create(
        SqlSugarContext context,
        HqSqlSugarContext hqContext,
        ILogger logger,
        IConfiguration configuration,
        ItemBarcodeService itemBarcodeService,
        IMapper mapper,
        IDataSyncFullService dataSyncFullService,
        IWarehouseProductChangeHistoryService changeHistoryService,
        ITranslationService? translationService,
        IStorePriceUpdateTaskService? priceTaskService = null,
        IWarehouseStorePriceHqSyncEnqueuer? hqPriceSyncEnqueuer = null
    )
    {
        var sliceContext = new ProductWarehouseSliceContext(
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
        );

        return new ProductWarehouseSlices(
            new ProductWarehouseDetectionSlice(sliceContext),
            new ProductWarehouseBatchUpdateSlice(sliceContext),
            new ProductWarehouseBatchCreationSlice(sliceContext),
            new ProductWarehouseTableSlice(sliceContext),
            new ProductWarehouseSingleCreationSlice(sliceContext),
            new ProductWarehouseImportSlice(sliceContext),
            new ProductWarehouseUpdateSlice(sliceContext),
            new ProductWarehouseBarcodePriceSlice(sliceContext),
            new ProductWarehouseSyncSlice(sliceContext),
            new ProductWarehouseMobileSlice(sliceContext)
        );
    }
}
