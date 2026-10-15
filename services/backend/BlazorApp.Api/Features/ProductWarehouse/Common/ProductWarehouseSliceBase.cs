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

internal sealed record ProductWarehouseSliceContext(
    SqlSugarContext Context,
    HqSqlSugarContext HqContext,
    ILogger Logger,
    IConfiguration Configuration,
    ItemBarcodeService ItemBarcodeService,
    IMapper Mapper,
    IDataSyncFullService DataSyncFullService,
    IWarehouseProductChangeHistoryService ChangeHistoryService,
    ITranslationService? TranslationService,
    IStorePriceUpdateTaskService? PriceTaskService = null,
    IWarehouseStorePriceHqSyncEnqueuer? HqPriceSyncEnqueuer = null
);

internal abstract class ProductWarehouseSliceBase
{
    protected const int PickingLocationType = 1;
    protected const string SystemUpdatedBy = "System";
    protected const string MobileWarehousePricePatchUpdatedBy = "MobileWarehousePricePatch";
    // HQ 同步 outbox 的来源标识：区分「Web 完整编辑」与「单字段改价（Web PATCH / 移动端仓库维护）」。
    protected const string WarehouseFullUpdateHqSource = "warehouse-product-full-update";
    protected const string WarehousePricePatchHqSource = "warehouse-product-price-patch";

    protected readonly SqlSugarContext _context;
    // Feature 仅依赖抽象日志接口，禁止反向依赖 React 兼容门面类型。
    protected readonly ILogger _logger;
    protected readonly IConfiguration _configuration;
    protected readonly ItemBarcodeService _itemBarcodeService;
    protected readonly IDataSyncFullService _dataSyncFullService;
    protected readonly IWarehouseProductChangeHistoryService _changeHistoryService;
    protected readonly ITranslationService? _translationService;
    protected readonly IStorePriceUpdateTaskService? _priceTaskService;
    protected readonly IWarehouseStorePriceHqSyncEnqueuer? _hqPriceSyncEnqueuer;

    protected ProductWarehouseSliceBase(ProductWarehouseSliceContext context)
    {
        _context = context.Context;
        _logger = context.Logger;
        _configuration = context.Configuration;
        _itemBarcodeService = context.ItemBarcodeService;
        _dataSyncFullService = context.DataSyncFullService;
        _changeHistoryService = context.ChangeHistoryService;
        _translationService = context.TranslationService;
        _priceTaskService = context.PriceTaskService;
        _hqPriceSyncEnqueuer = context.HqPriceSyncEnqueuer;
    }

    protected sealed record ImportProductNameResolution(
        string DisplayName,
        string? EnglishName,
        bool WasTranslated
    );

    protected async Task<Dictionary<string, ImportProductNameResolution>> ResolveImportProductNamesAsync(
        IEnumerable<DomesticProduct> domesticProducts
    )
    {
        var products = domesticProducts
            .Where(p => !string.IsNullOrWhiteSpace(p.ProductCode))
            .ToList();
        var resolutions = new Dictionary<string, ImportProductNameResolution>();
        var needTranslation = new List<DomesticProduct>();

        foreach (var product in products)
        {
            var englishName = NormalizeValidEnglishName(product.EnglishProductName);
            if (!string.IsNullOrWhiteSpace(englishName))
            {
                resolutions[product.ProductCode] = new ImportProductNameResolution(
                    englishName,
                    englishName,
                    false
                );
                continue;
            }

            if (!string.IsNullOrWhiteSpace(product.ProductName))
            {
                needTranslation.Add(product);
            }
            else
            {
                resolutions[product.ProductCode] = new ImportProductNameResolution(
                    product.HBProductNo ?? product.ProductCode,
                    null,
                    false
                );
            }
        }

        var translations = new Dictionary<string, string>();
        if (_translationService != null && needTranslation.Count > 0)
        {
            translations = await _translationService.BatchTranslateToEnglishAsync(
                needTranslation.Select(p => p.ProductName!).Distinct().ToList()
            );
        }

        foreach (var product in needTranslation)
        {
            var translatedName = translations.TryGetValue(product.ProductName!, out var value)
                ? NormalizeValidEnglishName(value)
                : null;

            if (!string.IsNullOrWhiteSpace(translatedName))
            {
                resolutions[product.ProductCode] = new ImportProductNameResolution(
                    translatedName,
                    translatedName,
                    true
                );
                continue;
            }

            if (translations.TryGetValue(product.ProductName!, out var invalidTranslation))
            {
                // 翻译服务失败时会返回原文；仍含中文的结果不能写入英文字段。
                _logger.LogWarning(
                    "跳过仍包含中文的国内导入英文名称写回: ProductCode={ProductCode}, ProductName={ProductName}, Translation={Translation}",
                    product.ProductCode,
                    product.ProductName,
                    invalidTranslation
                );
            }

            resolutions[product.ProductCode] = new ImportProductNameResolution(
                product.ProductName!.Trim(),
                null,
                false
            );
        }

        return resolutions;
    }

    protected string? NormalizeValidEnglishName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim();
        return ContainsChinese(normalized) ? null : normalized;
    }

    protected bool ContainsChinese(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return _translationService?.ContainsChinese(value)
            ?? value.Any(c => c >= '\u4e00' && c <= '\u9fff');
    }

    protected static bool ShouldSmartFillExistingProductName(
        Product existingProduct,
        DomesticProduct domesticProduct,
        ImportProductNameResolution nameResolution
    )
    {
        if (string.IsNullOrWhiteSpace(nameResolution.EnglishName))
            return false;

        if (string.IsNullOrWhiteSpace(existingProduct.ProductName))
            return true;

        return !string.IsNullOrWhiteSpace(domesticProduct.ProductName)
            && string.Equals(
                existingProduct.ProductName.Trim(),
                domesticProduct.ProductName.Trim(),
                StringComparison.Ordinal
            );
    }

    protected ISugarQueryable<T> WithWarehouseProductUpdateLock<T>(
        ISugarQueryable<T> queryable
    )
    {
        return _context.Db.CurrentConnectionConfig.DbType == DbType.SqlServer
            ? queryable.With(SqlWith.UpdLock)
            : queryable;
    }

    protected internal static string ResolveUpdatedBy(string? updatedBy)
    {
        // 服务也会被后台任务等非 HTTP 入口调用，空操作人必须可审计地回退为 System。
        return string.IsNullOrWhiteSpace(updatedBy) ? SystemUpdatedBy : updatedBy;
    }

    protected static bool IsCostDerivedSetType(int setType)
    {
        // Type1 按子项零售价分摊，Type2 直接继承主成本；两者都禁止入口直接覆盖子项成本。
        return setType == 1 || setType == 2;
    }

    protected static void EnsureSetChildPurchasePriceRecalculated(
        SetChildPurchasePriceWritebackResultDto recalculation,
        string fallbackMessage
    )
    {
        if (
            recalculation.ProductSetCode.SkippedGroupCount == 0
            && recalculation.StoreMultiCodeProduct.SkippedGroupCount == 0
        )
        {
            return;
        }

        var reason = recalculation.Errors.FirstOrDefault()?.Reason ?? fallbackMessage;
        throw new InvalidOperationException(reason);
    }

    protected async Task RecordProductChangeHistoryAsync(
        IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto> beforeSnapshots,
        IEnumerable<string> productCodes,
        string action,
        string source,
        string actorName,
        Guid? batchGuid = null,
        string? sourceReference = null,
        string? actorUserGuid = null
    )
    {
        var normalizedCodes = productCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (normalizedCodes.Count == 0)
        {
            return;
        }

        var afterSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(normalizedCodes);
        await _changeHistoryService.RecordChangesAsync(
            beforeSnapshots,
            afterSnapshots,
            new WarehouseProductChangeHistoryContextDto
            {
                Action = action,
                Source = source,
                SourceReference = sourceReference,
                BatchGuid = batchGuid,
                ActorUserGuid = actorUserGuid,
                ActorName = ResolveUpdatedBy(actorName),
            }
        );
    }

    /// <summary>
    /// 仓库自动下发覆盖分店零售价后，为价格确实发生变化的分店登记「待换标签」通知。
    /// 通知失败不能影响改价本身，因此只记录日志；移动端列表与定时对账会兜底。
    /// </summary>
    protected async Task RecordStorePriceOverwritesAsync(
        IReadOnlyCollection<StorePriceOverwrite> overwrites,
        string updatedBy
    )
    {
        if (_priceTaskService == null || overwrites.Count == 0)
        {
            return;
        }

        try
        {
            await _priceTaskService.RecordStoreOverwritesAsync(
                overwrites,
                new PriceTaskInitiator(ResolveUpdatedBy(updatedBy), "WarehouseAutoSync")
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "登记分店待换标签通知失败: Count={Count}", overwrites.Count);
        }
    }

    protected async Task UpsertActiveStoreRetailPricesAsync(
        Product product,
        decimal? purchasePrice,
        decimal? retailPrice,
        DateTime now,
        string updatedBy
    )
    {
        if (string.IsNullOrWhiteSpace(product.ProductCode))
        {
            return;
        }

        var stores = await _context
            .Db.Queryable<Store>()
            .Where(s => s.IsActive && !s.IsDeleted)
            .Select(s => new { s.StoreCode })
            .ToListAsync();
        var activeStoreCodes = stores
            .Select(s => s.StoreCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct()
            .ToList();
        if (!activeStoreCodes.Any())
        {
            return;
        }

        var existingPrices = await _context
            .Db.Queryable<StoreRetailPrice>()
            .Where(srp =>
                srp.ProductCode == product.ProductCode
                && srp.StoreCode != null
                && activeStoreCodes.Contains(srp.StoreCode)
                && !srp.IsDeleted
            )
            .ToListAsync();
        var existingStoreCodes = existingPrices
            .Select(price => price.StoreCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToHashSet();

        // 覆盖前的分店零售价就是货架标签上的价格，覆盖之后无从推断，必须在这里先留存。
        // 下面的循环会就地改写 price 对象，所以要先拷贝出来。
        var overwrittenPrices = retailPrice.HasValue
            ? existingPrices
                .Where(price => !string.IsNullOrWhiteSpace(price.StoreCode))
                .Select(price => new StorePriceOverwrite(
                    price.StoreCode!,
                    product.ProductCode!,
                    price.StoreRetailPriceValue,
                    price.DiscountRate
                ))
                .ToList()
            : new List<StorePriceOverwrite>();

        foreach (var price in existingPrices)
        {
            if (purchasePrice.HasValue)
            {
                price.PurchasePrice = purchasePrice;
            }
            if (retailPrice.HasValue)
            {
                price.StoreRetailPriceValue = retailPrice;
            }
            price.UpdatedAt = now;
            price.UpdatedBy = updatedBy;
        }

        if (existingPrices.Any())
        {
            var existingUuids = existingPrices.Select(price => price.UUID).ToList();
            var update = _context.Db.Updateable<StoreRetailPrice>()
                .SetColumns(price => price.UpdatedAt == now)
                .SetColumns(price => price.UpdatedBy == updatedBy)
                .Where(price => existingUuids.Contains(price.UUID));
            if (purchasePrice.HasValue)
            {
                update = update.SetColumns(price => price.PurchasePrice == purchasePrice);
            }
            if (retailPrice.HasValue)
            {
                update = update.SetColumns(price => price.StoreRetailPriceValue == retailPrice);
            }
            await update.ExecuteCommandAsync();
            await RecordStorePriceOverwritesAsync(overwrittenPrices, updatedBy);
        }

        var insertPrices = activeStoreCodes
            .Where(storeCode => !existingStoreCodes.Contains(storeCode))
            .Select(storeCode => new StoreRetailPrice
            {
                UUID = UuidHelper.GenerateUuid7(),
                StoreCode = storeCode,
                ProductCode = product.ProductCode,
                StoreProductCode = storeCode + product.ProductCode,
                SupplierCode = product.LocalSupplierCode,
                PurchasePrice = purchasePrice ?? product.PurchasePrice,
                StoreRetailPriceValue = retailPrice ?? product.RetailPrice,
                DiscountRate = null,
                IsActive = product.IsActive,
                IsAutoPricing = product.IsAutoPricing,
                IsSpecialProduct = product.IsSpecialProduct,
                CreatedAt = now,
                CreatedBy = updatedBy,
                UpdatedAt = now,
                UpdatedBy = updatedBy,
                IsDeleted = false,
            })
            .ToList();

        if (insertPrices.Any())
        {
            await _context.Db.Insertable(insertPrices).ExecuteCommandAsync();
        }

        // 本方法覆盖/补建了全部启用分店的价格，HQ 里这些分店的价格也要跟着变（同事务入队，由 outbox 异步写 HQ）。
        var hqFieldMask = new List<string>();
        if (purchasePrice.HasValue)
        {
            hqFieldMask.Add(ProductMaintenanceHqFieldMasks.StorePurchasePrice);
        }
        if (retailPrice.HasValue)
        {
            hqFieldMask.Add(ProductMaintenanceHqFieldMasks.StoreRetailPrice);
        }
        if (hqFieldMask.Count > 0 && (existingPrices.Any() || insertPrices.Any()))
        {
            await EnqueueStorePriceHqSyncAsync(
                product.ProductCode,
                activeStoreCodes,
                hqFieldMask,
                updatedBy,
                WarehousePricePatchHqSource
            );
        }
    }

    /// <summary>
    /// 仓库改写分店价后，在同一事务内登记「分店价 → HQ」同步任务。
    /// 与「待换标签」通知不同，这里不吞异常：入队失败必须让整笔改价一起回滚，否则 HBweb 与 HQ 会悄悄分叉。
    /// </summary>
    protected Task EnqueueStorePriceHqSyncAsync(
        string productCode,
        IEnumerable<string?> storeCodes,
        IReadOnlyCollection<string> fieldMask,
        string updatedBy,
        string source
    ) =>
        _hqPriceSyncEnqueuer == null
            ? Task.CompletedTask
            : _hqPriceSyncEnqueuer.EnqueueAsync(
                _context.Db,
                productCode,
                storeCodes,
                fieldMask,
                updatedBy,
                source
            );
}
