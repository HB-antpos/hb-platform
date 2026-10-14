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
using SetChildPurchasePriceMutationLock = BlazorApp.Api.Services.ProductCosts.ProductCostMutationLock;
using SetChildPurchasePriceService = BlazorApp.Api.Services.ProductCosts.ProductCostRecalculationService;

namespace BlazorApp.Api.Features.ProductWarehouse;

internal sealed class WarehouseProductSingleCreationCommandWriter : ProductWarehouseSliceBase
{
    internal WarehouseProductSingleCreationCommandWriter(ProductWarehouseSliceContext context)
        : base(context) { }

    /// <summary>
    /// 单商品命令写入器拥有唯一事务；锁顺序、多表写入、成本重算和历史顺序保持不变。
    /// </summary>
    internal async Task<CreateSingleProductResponseDto> ExecuteAsync(
        CreateSingleProductRequestDto request,
        WarehouseProductSingleCreationPlan plan
    )
    {
        var response = WarehouseProductSingleCreationResultAssembler.CreatePending();
        var warnings = new List<string>();
        var effectiveUpdatedBy = plan.UpdatedBy;

        try
        {
            // 1. 货号：为空时按供应商编码自动生成
            string itemNumber;
            if (string.IsNullOrWhiteSpace(request.ItemNumber))
            {
                var supplierCode = request.SupplierCode?.Trim();
                if (string.IsNullOrWhiteSpace(supplierCode))
                    supplierCode = request.SupplierId?.ToString();
                if (string.IsNullOrWhiteSpace(supplierCode))
                {
                    WarehouseProductSingleCreationResultAssembler.Reject(
                        response,
                        "货号为空时需提供供应商编码以自动生成"
                    );
                    return response;
                }
                (itemNumber, _) = await _itemBarcodeService.GenerateItemNumberAndBarcodeAsync(
                    supplierCode!,
                    request.ProductType
                );
            }
            else
            {
                itemNumber = request.ItemNumber;
            }

            var pricingValidationError = WarehouseProductSingleCreationValidator.ValidatePricing(
                request
            );
            if (pricingValidationError != null)
            {
                WarehouseProductSingleCreationResultAssembler.Reject(
                    response,
                    pricingValidationError
                );
                return response;
            }

            // 分店价格必须带有效且不重复的分店代码，否则在写库前直接拒绝。
            var storePriceValidationError =
                WarehouseProductSingleCreationValidator.ValidateStorePrices(request);
            if (storePriceValidationError != null)
            {
                WarehouseProductSingleCreationResultAssembler.Reject(
                    response,
                    storePriceValidationError
                );
                return response;
            }

            // 装箱数、中包数量传了就必须 ≥ 1，否则在写库前直接拒绝。
            var packagingValidationError =
                WarehouseProductSingleCreationValidator.ValidatePackaging(request);
            if (packagingValidationError != null)
            {
                WarehouseProductSingleCreationResultAssembler.Reject(
                    response,
                    packagingValidationError
                );
                return response;
            }

            // 1.1 规范化图片地址：如果为空或不是 http(s)，按货号规则生成
            var finalImageUrl = ProductImageUrlHelper.EnsureImageUrl(
                request.ImageUrl,
                itemNumber
            );

            // 2. 商品编码：为空时自动生成 UUID7
            var productCode = request.ProductCode;
            if (string.IsNullOrWhiteSpace(productCode))
            {
                productCode = UuidHelper.GenerateUuid7();
            }

            // 3. 货号/条码校验：并发查询，减少往返
            Product? existingProductByItemNumber = null;
            Product? existingBarcodeProduct = null;
            var barcodeExists = false;

            var queryByItemNumber = async () =>
            {
                using var conn = SqlSugarContext.CreateConcurrentConnection(_configuration);
                return await conn.Queryable<Product>()
                    .Where(p => p.ItemNumber == itemNumber && !p.IsDeleted)
                    .FirstAsync();
            };
            var queryByBarcode = async () =>
            {
                if (string.IsNullOrWhiteSpace(request.Barcode))
                    return (Product?)null;
                using var conn = SqlSugarContext.CreateConcurrentConnection(_configuration);
                return await conn.Queryable<Product>()
                    .Where(p => p.Barcode == request.Barcode && !p.IsDeleted)
                    .FirstAsync();
            };

            var taskByItemNumber = queryByItemNumber();
            var taskByBarcode = queryByBarcode();
            await Task.WhenAll(taskByItemNumber, taskByBarcode);
            existingProductByItemNumber = await taskByItemNumber;
            existingBarcodeProduct = await taskByBarcode;

            if (existingProductByItemNumber != null)
            {
                WarehouseProductSingleCreationResultAssembler.Reject(
                    response,
                    "货号已存在"
                );
                return response;
            }
            if (existingBarcodeProduct != null)
            {
                barcodeExists = true;
                warnings.Add($"条码 {request.Barcode} 已存在于系统中");
            }

            // 4. 条码：为空时按供应商编码自动生成 EAN-13
            var supplierCodeForBarcode =
                request.SupplierCode?.Trim() ?? request.SupplierId?.ToString();
            string? barcodeToUse = request.Barcode;
            if (
                string.IsNullOrWhiteSpace(barcodeToUse)
                && !string.IsNullOrWhiteSpace(supplierCodeForBarcode)
            )
            {
                (_, barcodeToUse) = await _itemBarcodeService.GenerateItemNumberAndBarcodeAsync(
                    supplierCodeForBarcode!,
                    request.ProductType
                );
            }

            _context.Db.Ado.BeginTran();
            // 新建商品会占用商品编码身份；Update 总闸不会阻塞无关商品的普通成本 Shared 锁，
            // 但会让相同身份的新建或主档变更串行。
            var setChildPurchasePriceLock =
                await SetChildPurchasePriceMutationLock.AcquireProductIdentitiesWithinBudgetAsync(
                    _context.Db,
                    new[] { productCode }
                );

            // 锁前的独立连接只用于快速失败；等待身份锁期间可能已有请求创建相同记录，
            // 因此必须在当前事务连接内复查关键身份，不能沿用锁前结果。
            var existingProductByCodeWithinLock = await _context.Db.Queryable<Product>()
                .Where(p => p.ProductCode == productCode && !p.IsDeleted)
                .FirstAsync();
            if (existingProductByCodeWithinLock != null)
            {
                _context.Db.Ado.RollbackTran();
                WarehouseProductSingleCreationResultAssembler.Reject(response, "商品编码已存在");
                return response;
            }

            var existingProductByItemNumberWithinLock = await _context.Db.Queryable<Product>()
                .Where(p => p.ItemNumber == itemNumber && !p.IsDeleted)
                .FirstAsync();
            if (existingProductByItemNumberWithinLock != null)
            {
                _context.Db.Ado.RollbackTran();
                WarehouseProductSingleCreationResultAssembler.Reject(response, "货号已存在");
                return response;
            }

            if (!string.IsNullOrWhiteSpace(barcodeToUse) && !barcodeExists)
            {
                var existingBarcodeProductWithinLock = await _context.Db.Queryable<Product>()
                    .Where(p => p.Barcode == barcodeToUse && !p.IsDeleted)
                    .FirstAsync();
                if (existingBarcodeProductWithinLock != null)
                {
                    barcodeExists = true;
                    warnings.Add($"条码 {barcodeToUse} 已存在于系统中");
                }
            }
            var now = DateTime.Now;
            var auditProductCode = productCode!;
            var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                new[] { auditProductCode }
            );

            // 5. 并发查询 DomesticProduct、WarehouseProduct 和活跃门店
            // 使用独立连接避免连接冲突
            var domesticProductTask = async () =>
            {
                using var conn = _context.CreateConcurrentQueryConnection();
                return await conn.Queryable<DomesticProduct>()
                    .Where(dp => dp.ProductCode == productCode)
                    .FirstAsync();
            };
            var warehouseProductTask = async () =>
            {
                using var conn = _context.CreateConcurrentQueryConnection();
                return await conn.Queryable<WarehouseProduct>()
                    .Where(wp => wp.ProductCode == productCode)
                    .FirstAsync();
            };
            var activeStoresTask = async () =>
            {
                using var conn = _context.CreateConcurrentQueryConnection();
                return await conn.Queryable<Store>()
                    .Where(s => s.IsActive == true && s.IsDeleted == false)
                    .Select(s => s.StoreCode)
                    .ToListAsync();
            };

            var taskDomestic = domesticProductTask();
            var taskWarehouse = warehouseProductTask();
            var taskStores = activeStoresTask();
            await Task.WhenAll(taskDomestic, taskWarehouse, taskStores);

            var domesticProduct = await taskDomestic;
            var warehouseProduct = await taskWarehouse;
            var activeStores = await taskStores;

            // 6. 插入商品主表 Product
            var product = WarehouseProductSingleCreationEntityMapper.CreateProduct(
                productCode!,
                itemNumber,
                barcodeToUse,
                finalImageUrl,
                request,
                effectiveUpdatedBy,
                now
            );
            await _context.Db.Insertable(product).ExecuteCommandAsync();

            // 7. 国内商品表 DomesticProduct：无则新增，有则更新
            if (domesticProduct == null)
            {
                domesticProduct = WarehouseProductSingleCreationEntityMapper.CreateDomesticProduct(
                    productCode!,
                    itemNumber,
                    barcodeToUse,
                    finalImageUrl,
                    request,
                    effectiveUpdatedBy,
                    now
                );
                await _context.Db.Insertable(domesticProduct).ExecuteCommandAsync();
            }
            else
            {
                WarehouseProductSingleCreationEntityMapper.ApplyDomesticProductUpdate(
                    domesticProduct,
                    barcodeToUse,
                    request,
                    effectiveUpdatedBy,
                    now
                );
                await _context
                    .Db.Updateable(domesticProduct)
                    .UpdateColumns(dp => new
                    {
                        dp.ProductName,
                        dp.EnglishProductName,
                        dp.Barcode,
                        dp.DomesticPrice,
                        dp.OEMPrice,
                        dp.ImportPrice,
                        dp.UnitVolume,
                        dp.ProductSpecification,
                        dp.PackingQuantity,
                        dp.MiddlePackQuantity,
                        dp.ProductType,
                        dp.UpdatedAt,
                        dp.UpdatedBy,
                    })
                    .ExecuteCommandAsync();
            }

            // 8. 仓库商品表 WarehouseProduct：无则新增，有则更新
            if (warehouseProduct == null)
            {
                warehouseProduct = WarehouseProductSingleCreationEntityMapper.CreateWarehouseProduct(
                    productCode!,
                    request,
                    effectiveUpdatedBy,
                    now
                );
                await _context.Db.Insertable(warehouseProduct).ExecuteCommandAsync();
            }
            else
            {
                WarehouseProductSingleCreationEntityMapper.ApplyWarehouseProductUpdate(
                    warehouseProduct,
                    request,
                    effectiveUpdatedBy,
                    now
                );
                await _context
                    .Db.Updateable(warehouseProduct)
                    .UpdateColumns(wp => new
                    {
                        wp.DomesticPrice,
                        wp.OEMPrice,
                        wp.ImportPrice,
                        wp.Volume,
                        wp.PackingQuantity,
                        wp.IsActive,
                        wp.UpdatedAt,
                        wp.UpdatedBy,
                    })
                    .ExecuteCommandAsync();
            }

            var domesticSetProducts = new List<DomesticSetProduct>();
            var productSetCodes = new List<ProductSetCode>();

            // 9. 套装商品：先删旧再批量插入 DomesticSetProduct + ProductSetCode，SetProductCode 自动生成
            if (request.ProductType == ProductTypeEnum.Set && request.SetItems?.Any() == true)
            {
                var existingSetProducts = await _context
                    .Db.Queryable<DomesticSetProduct>()
                    .Where(sp => sp.ProductCode == productCode && !sp.IsDeleted)
                    .ToListAsync();

                await _context
                    .Db.Deleteable<DomesticSetProduct>()
                    .Where(sp => sp.ProductCode == productCode)
                    .ExecuteCommandAsync();

                await _context
                    .Db.Deleteable<ProductSetCode>()
                    .Where(psc => psc.ProductCode == productCode)
                    .ExecuteCommandAsync();

                foreach (var setItem in request.SetItems)
                {
                    var setType = request.SetType.HasValue
                        ? (int)request.SetType.Value
                        : 1;
                    var setRows = WarehouseProductSingleCreationEntityMapper.CreateSetRows(
                        productCode!,
                        setItem,
                        setType,
                        request.ImportPrice,
                        request.OEMPrice,
                        now
                    );
                    domesticSetProducts.Add(setRows.Domestic);
                    productSetCodes.Add(setRows.Global);
                }
                if (domesticSetProducts.Any())
                {
                    await _context.Db.Insertable(domesticSetProducts).ExecuteCommandAsync();
                    await _context.Db.Insertable(productSetCodes).ExecuteCommandAsync();

                    var projectedSetRows = productSetCodes
                        .SelectMany(setCode => activeStores
                            .Where(storeCode => !string.IsNullOrWhiteSpace(storeCode))
                            .Select(storeCode =>
                                WarehouseProductSingleCreationEntityMapper.CreateProjectedSetRow(
                                    productCode!,
                                    storeCode,
                                    setCode,
                                    effectiveUpdatedBy,
                                    now
                                )
                            ))
                        .ToList();
                    if (projectedSetRows.Count > 0)
                    {
                        await _context.Db.Insertable(projectedSetRows).ExecuteCommandAsync();
                    }
                }
            }

            // 10. 一品多码：多码条码为空时用 ItemNumberHelper 生成；按门店批量插入 StoreMultiCodeProduct
            if (
                request.ProductType == ProductTypeEnum.MultiCode
                && request.MultiCodeItems?.Any() == true
            )
            {
                var existingMultiBarcodes = await _context
                    .Db.Queryable<StoreMultiCodeProduct>()
                    .Where(mcp => mcp.ProductCode == productCode && mcp.MultiBarcode != null)
                    .Select(mcp => mcp.MultiBarcode!)
                    .ToListAsync();

                var resolvedBarcodes = new List<string>();
                foreach (var multiCodeItem in request.MultiCodeItems)
                {
                    var barcode = multiCodeItem.Barcode;
                    if (string.IsNullOrWhiteSpace(barcode))
                    {
                        barcode = ItemNumberHelper.GenerateSetItemNumber(
                            itemNumber,
                            existingMultiBarcodes
                        );
                        existingMultiBarcodes.Add(barcode);
                    }
                    resolvedBarcodes.Add(barcode);
                }

                await _context
                    .Db.Deleteable<StoreMultiCodeProduct>()
                    .Where(mcp => mcp.ProductCode == productCode)
                    .ExecuteCommandAsync();

                var activeStoresForMultiCode = activeStores;

                var multiCodeProducts = new List<StoreMultiCodeProduct>();
                for (var i = 0; i < request.MultiCodeItems.Count; i++)
                {
                    var multiCodeItem = request.MultiCodeItems[i];
                    var barcode = resolvedBarcodes[i];
                    var matchedSetCode = productSetCodes.First(psc => psc.SetBarcode == barcode);
                    foreach (var storeCode in activeStoresForMultiCode)
                    {
                        multiCodeProducts.Add(
                            WarehouseProductSingleCreationEntityMapper.CreateMultiCodeRow(
                                productCode!,
                                storeCode,
                                barcode,
                                matchedSetCode,
                                multiCodeItem,
                                now
                            )
                        );
                    }
                }
                if (multiCodeProducts.Any())
                    await _context.Db.Insertable(multiCodeProducts).ExecuteCommandAsync();
            }

            // 10. 分店零售价：有传则按传入覆盖并设 StoreProductCode；未传则按活跃门店用默认价（OEMPrice）补充
            if (request.StorePrices?.Any() == true)
            {
                await _context
                    .Db.Deleteable<StoreRetailPrice>()
                    .Where(srp => srp.ProductCode == productCode)
                    .ExecuteCommandAsync();

                var storeRetailPrices = request
                    .StorePrices.Select(storePrice =>
                        WarehouseProductSingleCreationEntityMapper.CreateRequestedStorePrice(
                            productCode!,
                            storePrice,
                            now
                        )
                    )
                    .ToList();
                await _context.Db.Insertable(storeRetailPrices).ExecuteCommandAsync();
            }
            else
            {
                // 未传分店价：仅对尚未有分店价的门店补充默认价（StoreProductCode = storeCode + productCode）
                var existingStoreCodes = await _context
                    .Db.Queryable<StoreRetailPrice>()
                    .Where(srp => srp.ProductCode == productCode && !srp.IsDeleted)
                    .Select(srp => srp.StoreCode)
                    .ToListAsync();
                var existingSet = new HashSet<string?>(
                    existingStoreCodes.Where(c => !string.IsNullOrWhiteSpace(c))
                );
                var toInsert = new List<StoreRetailPrice>();
                foreach (var storeCode in activeStores)
                {
                    if (string.IsNullOrWhiteSpace(storeCode) || existingSet.Contains(storeCode))
                        continue;
                    toInsert.Add(
                        WarehouseProductSingleCreationEntityMapper.CreateDefaultStorePrice(
                            productCode!,
                            storeCode,
                            request.ImportPrice,
                            request.OEMPrice,
                            now
                        )
                    );
                }
                if (toInsert.Any())
                    await _context.Db.Insertable(toInsert).ExecuteCommandAsync();
            }

            if (productSetCodes.Any(x => IsCostDerivedSetType(x.SetType)))
            {
                var recalculation = await new SetChildPurchasePriceService(
                    _context.Db
                ).RecalculateLockedAsync(
                    setChildPurchasePriceLock,
                    new[] { productCode },
                    storeCodes: activeStores,
                    updatedBy: effectiveUpdatedBy
                );
                EnsureSetChildPurchasePriceRecalculated(
                    recalculation,
                    "创建商品后的套装子项成本重算不完整"
                );
            }

            await RecordProductChangeHistoryAsync(
                beforeSnapshots,
                new[] { auditProductCode },
                action: "Create",
                source: "WarehouseProducts",
                actorName: effectiveUpdatedBy
            );

            _context.Db.Ado.CommitTran();

            WarehouseProductSingleCreationResultAssembler.Complete(
                response,
                productCode!,
                itemNumber,
                barcodeToUse,
                barcodeExists,
                warnings
            );
        }
        catch (Exception ex)
        {
            _context.Db.Ado.RollbackTran();
            if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
            {
                throw;
            }
            _logger.LogError(ex, "创建单个商品失败");
            WarehouseProductSingleCreationResultAssembler.RejectExecution(
                response,
                ex.Message
            );
        }

        return response;
    }
}
