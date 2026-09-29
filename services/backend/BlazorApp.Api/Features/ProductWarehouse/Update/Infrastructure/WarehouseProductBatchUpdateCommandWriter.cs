using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.SupplyNotices;
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
using SetChildPurchasePriceMutationLock = BlazorApp.Api.Services.ProductCosts.ProductCostMutationLock;
using SetChildPurchasePriceService = BlazorApp.Api.Services.ProductCosts.ProductCostRecalculationService;

namespace BlazorApp.Api.Features.ProductWarehouse;

internal sealed class WarehouseProductBatchUpdateCommandWriter : ProductWarehouseSliceBase
{
    internal WarehouseProductBatchUpdateCommandWriter(ProductWarehouseSliceContext context)
        : base(context) { }

    /// <summary>
    /// 命令写入器拥有批量更新唯一事务；锁、复读、写入、重算与历史顺序保持集中。
    /// </summary>
    internal async Task<WarehouseProductBatchUpdateResultDto> ExecuteAsync(
        List<UpdateItemDto> items,
        WarehouseProductBatchUpdateOptionsDto options,
        WarehouseProductBatchUpdatePlan executionPlan,
        WarehouseProductBatchUpdateResultDto result
    )
    {
        var effectiveUpdatedBy = executionPlan.UpdatedBy;
        var batchGuid = executionPlan.BatchGuid;
        var normalizedImageBaseUrl = executionPlan.NormalizedImageBaseUrl;

        try
        {
            // 开启事务
            _context.Db.Ado.BeginTran();
            // 先锁再读取，避免主成本、门店主价与套装子项派生价交叉覆盖。
            var setChildPurchasePriceLock =
                await SetChildPurchasePriceMutationLock.AcquireAllAsync(_context.Db);

            // 收集需要查询的 ProductCode 和 ItemNumber
            var productCodes = items
                .Select(i => i.ProductCode)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct()
                .ToList();
            var itemNumbers = items
                .Select(i => i.ItemNumber)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct()
                .ToList();

            // 批量查询仓库商品（避免 N+1 问题）
            var wpList = new List<WarehouseProduct>();
            if (productCodes.Any())
            {
                var wpByCodes = await _context
                    .Db.Queryable<WarehouseProduct>()
                    .Where(w => productCodes.Contains(w.ProductCode))
                    .ToListAsync();
                wpList.AddRange(wpByCodes);
            }
            // 通过 ItemNumber 查询对应的仓库商品
            if (itemNumbers.Any())
            {
                var codesFromItems = await _context
                    .Db.Queryable<Product>()
                    .Where(p => p.ItemNumber != null && itemNumbers.Contains(p.ItemNumber))
                    .Select(p => p.ProductCode)
                    .ToListAsync();
                if (codesFromItems.Any())
                {
                    var wpByItems = await _context
                        .Db.Queryable<WarehouseProduct>()
                        .Where(w => codesFromItems.Contains(w.ProductCode))
                        .ToListAsync();
                    wpList.AddRange(wpByItems);
                }
            }
            wpList = wpList.GroupBy(w => w.ProductCode).Select(g => g.First()).ToList();

            var byCode = wpList.ToDictionary(w => w.ProductCode);
            var itemToCode = new Dictionary<string, string>();
            if (itemNumbers.Any())
            {
                var codeMap = await _context
                    .Db.Queryable<Product>()
                    .Where(p => p.ItemNumber != null && itemNumbers.Contains(p.ItemNumber))
                    .Select(p => new { p.ItemNumber, p.ProductCode })
                    .ToListAsync();
                foreach (var m in codeMap)
                {
                    if (
                        !string.IsNullOrWhiteSpace(m.ItemNumber)
                        && !itemToCode.ContainsKey(m.ItemNumber!)
                    )
                    {
                        itemToCode[m.ItemNumber!] = m.ProductCode ?? string.Empty;
                    }
                }
            }

            var imageProductsByCode = new Dictionary<string, Product>(
                StringComparer.OrdinalIgnoreCase
            );
            if (options.GenerateImageUrls)
            {
                var imageProductCodes = productCodes
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code!)
                    .Concat(itemToCode.Values)
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var codeBatch in imageProductCodes.Chunk(500))
                {
                    var codes = codeBatch.ToList();
                    var products = await _context
                        .Db.Queryable<Product>()
                        .Where(product =>
                            product.ProductCode != null
                            && codes.Contains(product.ProductCode)
                            && !product.IsDeleted
                        )
                        .ToListAsync();
                    foreach (
                        var product in products
                            .Where(product => !string.IsNullOrWhiteSpace(product.ProductCode))
                            .GroupBy(
                                product => product.ProductCode!,
                                StringComparer.OrdinalIgnoreCase
                            )
                            .Select(group => group
                                .OrderByDescending(product =>
                                    product.UpdatedAt ?? product.CreatedAt
                                )
                                .ThenByDescending(product => product.CreatedAt)
                                .First()
                            )
                    )
                    {
                        if (
                            !string.IsNullOrWhiteSpace(product.ProductCode)
                            && !imageProductsByCode.ContainsKey(product.ProductCode)
                        )
                        {
                            imageProductsByCode[product.ProductCode] = product;
                        }
                    }
                }
            }

            // 先按最终可解析的商品编码一次性读取旧快照；非法、重复或最终无变化项不会产生事件。
            var auditProductCodes = productCodes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code!)
                .Concat(itemToCode.Values)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                auditProductCodes
            );

            var supplierDomesticProductsByCode = new Dictionary<string, DomesticProduct>(
                StringComparer.OrdinalIgnoreCase
            );
            var supplierProductsByCode = new Dictionary<string, Product>(
                StringComparer.OrdinalIgnoreCase
            );
            if (
                items.Any(item => !string.IsNullOrWhiteSpace(item.SupplierCode))
                && auditProductCodes.Any()
            )
            {
                var supplierDomesticProducts = await _context
                    .Db.Queryable<DomesticProduct>()
                    .Where(product => auditProductCodes.Contains(product.ProductCode))
                    .ToListAsync();
                supplierDomesticProductsByCode = supplierDomesticProducts
                    .GroupBy(product => product.ProductCode, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.FirstOrDefault(product => !product.IsDeleted) ?? group.First(),
                        StringComparer.OrdinalIgnoreCase
                    );

                var supplierProducts = await _context
                    .Db.Queryable<Product>()
                    .Where(product =>
                        product.ProductCode != null
                        && auditProductCodes.Contains(product.ProductCode)
                        && !product.IsDeleted
                    )
                    .ToListAsync();
                supplierProductsByCode = supplierProducts
                    .Where(product => !string.IsNullOrWhiteSpace(product.ProductCode))
                    .GroupBy(product => product.ProductCode!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group
                            .OrderByDescending(product => product.UpdatedAt ?? product.CreatedAt)
                            .ThenByDescending(product => product.CreatedAt)
                            .First(),
                        StringComparer.OrdinalIgnoreCase
                    );
            }

            var toUpdateWp = new List<WarehouseProduct>();
            var toCreateWp = new List<WarehouseProduct>();
            var codesWithImportPrice = new List<string>();
            var codesWithStorePurchasePrice = new List<string>();
            var packingQuantityByCode = new Dictionary<string, int>();
            var supplierCodeByProductCode = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase
            );
            var supplierWarehouseProductsByCode = new Dictionary<string, WarehouseProduct>(
                StringComparer.OrdinalIgnoreCase
            );
            var imageUrlByCode = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase
            );
            var processedProductCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 只收集本次显式把「是否上架」设为下架的商品；未设状态（留空不改）的行不登记供货说明。
            var delistedProductCodes = new List<string>();

            foreach (var item in items)
            {
                var itemValidationError = WarehouseProductBatchUpdateValidator.Validate(item);
                if (itemValidationError != null)
                {
                    // 服务层同时做防御性校验，确保绕过 DTO 校验的调用也不会部分写入其他字段。
                    WarehouseProductBatchUpdateResultAssembler.AddFailure(
                        result,
                        itemValidationError
                    );
                    continue;
                }

                WarehouseProduct? wp = null;
                string? targetCode = null;
                if (
                    !string.IsNullOrWhiteSpace(item.ProductCode)
                    && byCode.TryGetValue(item.ProductCode!, out var wpByCode)
                )
                {
                    wp = wpByCode;
                    targetCode = item.ProductCode!;
                }
                else if (
                    !string.IsNullOrWhiteSpace(item.ItemNumber)
                    && itemToCode.TryGetValue(item.ItemNumber!, out var mappedCode)
                    && byCode.TryGetValue(mappedCode, out var wpByItem)
                )
                {
                    wp = wpByItem;
                    targetCode = mappedCode;
                }

                if (wp == null && string.IsNullOrWhiteSpace(targetCode))
                {
                    if (!string.IsNullOrWhiteSpace(item.ProductCode))
                    {
                        targetCode = item.ProductCode!;
                    }
                    else if (
                        !string.IsNullOrWhiteSpace(item.ItemNumber)
                        && itemToCode.TryGetValue(item.ItemNumber!, out var mapCode2)
                    )
                    {
                        targetCode = mapCode2;
                    }
                }

                if (string.IsNullOrWhiteSpace(targetCode))
                {
                    WarehouseProductBatchUpdateResultAssembler.AddFailure(
                        result,
                        $"无法解析商品编码: ProductCode={item.ProductCode}, ItemNumber={item.ItemNumber}"
                    );
                    continue;
                }

                if (!processedProductCodes.Add(targetCode))
                {
                    // 同一批次按最终商品编码只处理首项，避免重复新建主键或后项覆盖首项。
                    WarehouseProductBatchUpdateResultAssembler.AddFailure(
                        result,
                        $"批次内商品编码重复: {targetCode}"
                    );
                    continue;
                }

                if (options.GenerateImageUrls)
                {
                    if (!imageProductsByCode.TryGetValue(targetCode, out var imageProduct))
                    {
                        WarehouseProductBatchUpdateResultAssembler.AddFailure(
                            result,
                            $"未找到本地商品主档，无法生成图片地址: ProductCode={targetCode}"
                        );
                        continue;
                    }

                    if (
                        !WarehouseProductBatchImageUrlBuilder.TryBuild(
                            normalizedImageBaseUrl!,
                            imageProduct.ItemNumber,
                            out var imageUrl,
                            out var imageUrlError
                        )
                    )
                    {
                        WarehouseProductBatchUpdateResultAssembler.AddFailure(
                            result,
                            $"{imageUrlError}: ProductCode={targetCode}"
                        );
                        continue;
                    }

                    imageUrlByCode[targetCode] = imageUrl;
                }

                var normalizedSupplierCode = string.IsNullOrWhiteSpace(item.SupplierCode)
                    ? null
                    : item.SupplierCode.Trim();
                if (
                    normalizedSupplierCode != null
                    && (
                        !supplierDomesticProductsByCode.TryGetValue(
                            targetCode,
                            out var supplierDomesticProduct
                        )
                        || supplierDomesticProduct.IsDeleted
                    )
                    && !supplierProductsByCode.ContainsKey(targetCode)
                )
                {
                    WarehouseProductBatchUpdateResultAssembler.AddFailure(
                        result,
                        $"未找到商品主档，无法设置国内供应商: ProductCode={targetCode}"
                    );
                    continue;
                }

                if (wp == null)
                {
                    var newWp = WarehouseProductBatchUpdateEntityMapper.CreateWarehouseProduct(
                        targetCode!,
                        item,
                        effectiveUpdatedBy
                    );
                    toCreateWp.Add(newWp);
                    if (item.IsActive == false)
                    {
                        delistedProductCodes.Add(targetCode);
                    }
                    if (normalizedSupplierCode != null)
                    {
                        supplierCodeByProductCode[targetCode] = normalizedSupplierCode;
                        supplierWarehouseProductsByCode[targetCode] = newWp;
                    }
                    if (item.PackingQuantity.HasValue)
                    {
                        // 装箱数以国内商品表为展示主来源，同时保留仓库表值供缺失时回退。
                        packingQuantityByCode[targetCode!] = item.PackingQuantity.Value;
                    }
                    if (item.ImportPrice.HasValue)
                    {
                        codesWithImportPrice.Add(targetCode!);
                        if (item.SyncStorePurchasePrice ?? true)
                        {
                            // 货柜页字段可选时可关闭分店进货价联动；旧入口不传时保持同步。
                            codesWithStorePurchasePrice.Add(targetCode!);
                        }
                    }
                    continue;
                }

                if (item.PackingQuantity.HasValue)
                {
                    // 只同步未删除的国内商品，避免恢复或污染历史软删除记录。
                    packingQuantityByCode[wp.ProductCode] = item.PackingQuantity.Value;
                }
                WarehouseProductBatchUpdateEntityMapper.ApplyWarehouseProductUpdate(
                    wp,
                    item,
                    effectiveUpdatedBy
                );
                toUpdateWp.Add(wp);
                if (item.IsActive == false)
                {
                    delistedProductCodes.Add(wp.ProductCode);
                }

                if (normalizedSupplierCode != null)
                {
                    supplierCodeByProductCode[targetCode] = normalizedSupplierCode;
                    supplierWarehouseProductsByCode[targetCode] = wp;
                }

                if (item.ImportPrice.HasValue)
                {
                    codesWithImportPrice.Add(wp.ProductCode);
                    if (item.SyncStorePurchasePrice ?? true)
                    {
                        // 货柜页字段可选时可关闭分店进货价联动；旧入口不传时保持同步。
                        codesWithStorePurchasePrice.Add(wp.ProductCode);
                    }
                }
            }

            if (toUpdateWp.Any())
            {
                await _context
                    .Db.Updateable(toUpdateWp)
                    .UpdateColumns(w => new
                    {
                        w.DomesticPrice,
                        w.OEMPrice,
                        w.ImportPrice,
                        w.Volume,
                        w.PackingQuantity,
                        w.MinOrderQuantity,
                        w.IsActive,
                        w.UpdatedAt,
                        w.UpdatedBy,
                    })
                    .ExecuteCommandAsync();
                // 批量编辑把商品改回上架时，关闭其供货说明（按当前状态判断，幂等）。
                await WarehouseProductSupplyNoticeWriter.CloseNoticesForActiveProductsAsync(
                    _context.Db,
                    toUpdateWp.Where(w => w.IsActive).Select(w => w.ProductCode).ToList(),
                    toUpdateWp[0].UpdatedBy ?? "System",
                    DateTime.UtcNow
                );
                WarehouseProductBatchUpdateResultAssembler.AddSuccesses(
                    result,
                    toUpdateWp.Count
                );
            }
            if (toCreateWp.Any())
            {
                await _context.Db.Insertable(toCreateWp).ExecuteCommandAsync();
                WarehouseProductBatchUpdateResultAssembler.AddSuccesses(
                    result,
                    toCreateWp.Count
                );
            }
            if (delistedProductCodes.Count > 0)
            {
                // 与下架同一事务登记供货说明（已有未关闭说明则就地更新）；没带说明时保持原状，兼容旧客户端。
                await WarehouseProductSupplyNoticeWriter.ApplyStatusChangeAsync(
                    _context.Db,
                    delistedProductCodes,
                    isActive: false,
                    executionPlan.SupplyNotice,
                    effectiveUpdatedBy,
                    source: "WarehouseProducts",
                    DateTime.UtcNow
                );
            }

            if (supplierCodeByProductCode.Any())
            {
                var supplierUpdatedAt = DateTime.Now;
                var domesticProductsToCreate = new List<DomesticProduct>();
                var domesticProductsToUpdate = new List<DomesticProduct>();
                foreach (var (productCode, supplierCode) in supplierCodeByProductCode)
                {
                    supplierDomesticProductsByCode.TryGetValue(
                        productCode,
                        out var domesticProduct
                    );
                    var shouldCreate = domesticProduct == null;
                    var shouldRefreshFromProduct = shouldCreate || domesticProduct!.IsDeleted;
                    if (shouldCreate)
                    {
                        domesticProduct = WarehouseProductBatchUpdateEntityMapper.CreateDomesticProduct(
                            productCode,
                            effectiveUpdatedBy,
                            supplierUpdatedAt
                        );
                    }

                    if (shouldRefreshFromProduct)
                    {
                        // 选择供应商代表明确建立国内商品关系；缺失时补建，软删除时恢复并刷新主数据。
                        var product = supplierProductsByCode[productCode];
                        var warehouseProduct = supplierWarehouseProductsByCode[productCode];
                        WarehouseProductBatchUpdateEntityMapper.RefreshDomesticProduct(
                            domesticProduct!,
                            product,
                            warehouseProduct
                        );
                    }

                    WarehouseProductBatchUpdateEntityMapper.ApplyDomesticSupplier(
                        domesticProduct!,
                        supplierCode,
                        effectiveUpdatedBy,
                        supplierUpdatedAt
                    );
                    if (shouldCreate)
                        domesticProductsToCreate.Add(domesticProduct!);
                    else
                        domesticProductsToUpdate.Add(domesticProduct!);
                }

                if (domesticProductsToCreate.Any())
                {
                    await _context
                        .Db.Insertable(domesticProductsToCreate)
                        .ExecuteCommandAsync();
                }
                if (domesticProductsToUpdate.Any())
                {
                    await _context
                        .Db.Updateable(domesticProductsToUpdate)
                        .ExecuteCommandAsync();
                }
            }

            if (packingQuantityByCode.Any())
            {
                var packingProductCodes = packingQuantityByCode.Keys.ToList();
                var domesticProducts = await _context
                    .Db.Queryable<DomesticProduct>()
                    .Where(dp => packingProductCodes.Contains(dp.ProductCode) && !dp.IsDeleted)
                    .ToListAsync();
                foreach (var domesticProduct in domesticProducts)
                {
                    domesticProduct.PackingQuantity = packingQuantityByCode[
                        domesticProduct.ProductCode
                    ];
                    domesticProduct.UpdatedAt = DateTime.Now;
                    domesticProduct.UpdatedBy = effectiveUpdatedBy;
                }

                if (domesticProducts.Any())
                {
                    await _context
                        .Db.Updateable(domesticProducts)
                        .UpdateColumns(dp => new
                        {
                            dp.PackingQuantity,
                            dp.UpdatedAt,
                            dp.UpdatedBy,
                        })
                        .ExecuteCommandAsync();
                }
            }

            if (imageUrlByCode.Any())
            {
                var imageUpdatedAt = DateTime.Now;
                var imageProducts = imageUrlByCode.Keys
                    .Select(code => imageProductsByCode[code])
                    .ToList();
                foreach (var product in imageProducts)
                {
                    product.ProductImage = imageUrlByCode[product.ProductCode!];
                    product.UpdatedAt = imageUpdatedAt;
                    product.UpdatedBy = effectiveUpdatedBy;
                }
                foreach (var productBatch in imageProducts.Chunk(200))
                {
                    await _context
                        .Db.Updateable(productBatch.ToList())
                        .UpdateColumns(product => new
                        {
                            product.ProductImage,
                            product.UpdatedAt,
                            product.UpdatedBy,
                        })
                        .ExecuteCommandAsync();
                }

                var imageProductCodes = imageUrlByCode.Keys.ToList();
                var imageDomesticProducts = new List<DomesticProduct>();
                foreach (var codeBatch in imageProductCodes.Chunk(500))
                {
                    var codes = codeBatch.ToList();
                    imageDomesticProducts.AddRange(
                        await _context
                            .Db.Queryable<DomesticProduct>()
                            .Where(product =>
                                codes.Contains(product.ProductCode) && !product.IsDeleted
                            )
                            .ToListAsync()
                    );
                }
                foreach (var domesticProduct in imageDomesticProducts)
                {
                    domesticProduct.ProductImage = imageUrlByCode[
                        domesticProduct.ProductCode
                    ];
                    domesticProduct.UpdatedAt = imageUpdatedAt;
                    domesticProduct.UpdatedBy = effectiveUpdatedBy;
                }
                foreach (var domesticBatch in imageDomesticProducts.Chunk(200))
                {
                    await _context
                        .Db.Updateable(domesticBatch.ToList())
                        .UpdateColumns(product => new
                        {
                            product.ProductImage,
                            product.UpdatedAt,
                            product.UpdatedBy,
                        })
                        .ExecuteCommandAsync();
                }

                WarehouseProductBatchUpdateResultAssembler.SetImageUpdates(
                    result,
                    imageUrlByCode
                );
            }

            if (codesWithImportPrice.Any())
            {
                var products = await _context
                    .Db.Queryable<Product>()
                    .Where(p =>
                        p.ProductCode != null && codesWithImportPrice.Contains(p.ProductCode)
                    )
                    .ToListAsync();
                var importDict = toUpdateWp
                    .Where(w => w.ImportPrice.HasValue)
                    .ToDictionary(w => w.ProductCode, w => w.ImportPrice!.Value);
                foreach (var w in toCreateWp.Where(x => x.ImportPrice.HasValue))
                {
                    if (!importDict.ContainsKey(w.ProductCode))
                    {
                        importDict[w.ProductCode] = w.ImportPrice!.Value;
                    }
                }
                foreach (var p in products)
                {
                    if (
                        p.ProductCode != null
                        && importDict.TryGetValue(p.ProductCode, out var importPrice)
                    )
                    {
                        p.PurchasePrice = importPrice;
                        p.UpdatedAt = DateTime.Now;
                        p.UpdatedBy = effectiveUpdatedBy;
                    }
                }
                if (products.Any())
                {
                    await _context
                        .Db.Updateable(products)
                        .UpdateColumns(p => new
                        {
                            p.PurchasePrice,
                            p.UpdatedAt,
                            p.UpdatedBy,
                        })
                        .ExecuteCommandAsync();
                }

                var storeRetailPrices = codesWithStorePurchasePrice.Any()
                    ? await _context
                        .Db.Queryable<StoreRetailPrice>()
                        .Where(srp =>
                            srp.ProductCode != null
                            && codesWithStorePurchasePrice.Contains(srp.ProductCode)
                        )
                        .ToListAsync()
                    : new List<StoreRetailPrice>();

                foreach (var srp in storeRetailPrices)
                {
                    if (importDict.TryGetValue(srp.ProductCode!, out var importPrice))
                    {
                        srp.PurchasePrice = importPrice;
                        srp.UpdatedAt = DateTime.Now;
                    }
                }

                if (storeRetailPrices.Any())
                {
                    await _context
                        .Db.Updateable(storeRetailPrices)
                        .UpdateColumns(srp => new { srp.PurchasePrice, srp.UpdatedAt })
                        .ExecuteCommandAsync();
                    _logger.LogInformation(
                        "更新了 {Count} 条分店价格记录的进货价",
                        storeRetailPrices.Count
                    );
                }

                var recalculation = await new SetChildPurchasePriceService(
                    _context.Db
                ).RecalculateLockedAsync(
                    setChildPurchasePriceLock,
                    codesWithImportPrice,
                    storeCodes: null,
                    updatedBy: effectiveUpdatedBy
                );
                EnsureSetChildPurchasePriceRecalculated(
                    recalculation,
                    "批量更新后的套装子项成本重算不完整"
                );
            }

            await RecordProductChangeHistoryAsync(
                beforeSnapshots,
                auditProductCodes,
                action: "BatchUpdate",
                source: "WarehouseProducts",
                actorName: effectiveUpdatedBy,
                batchGuid: batchGuid
            );

            _context.Db.Ado.CommitTran();
        }
        catch (Exception ex)
        {
            _context.Db.Ado.RollbackTran();
            if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
            {
                throw;
            }
            _logger.LogError(ex, "批量更新失败");
            WarehouseProductBatchUpdateResultAssembler.RejectExecution(result, ex.Message);
        }

        return result;
    }
}
