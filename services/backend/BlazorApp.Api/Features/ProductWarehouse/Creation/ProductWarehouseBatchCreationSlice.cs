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

internal sealed class ProductWarehouseBatchCreationSlice
    : ProductWarehouseSliceBase,
      IProductWarehouseBatchCreationSlice
{
    internal ProductWarehouseBatchCreationSlice(ProductWarehouseSliceContext context)
        : base(context) { }

    /// <summary>
    /// 批量创建仓库商品
    /// 支持普通商品和套装商品，自动跳过已存在的商品
    /// </summary>
    /// <param name="items">待创建的商品列表</param>
    /// <param name="useTransaction">是否由本方法自行开启事务；整柜提交会关闭它，交给外层事务统一控制</param>
    /// <returns>批量操作结果</returns>
    public Task<BatchOperationResultDto> BatchCreateAsync(
        List<CreateItemDto> items,
        bool useTransaction = true
    )
    {
        return BatchCreateAsync(items, useTransaction, SystemUpdatedBy);
    }

    public Task<BatchOperationResultDto> BatchCreateAsync(
        List<CreateItemDto> items,
        bool useTransaction,
        string? updatedBy
    )
    {
        return BatchCreateAsync(
            items,
            useTransaction,
            updatedBy,
            auditSource: "WarehouseProducts",
            sourceReference: null,
            batchGuid: null
        );
    }

    public async Task<BatchOperationResultDto> BatchCreateAsync(
        List<CreateItemDto> items,
        bool useTransaction,
        string? updatedBy,
        string auditSource,
        string? sourceReference,
        Guid? batchGuid
    )
    {
        return await BatchCreateAsync(
            items,
            useTransaction,
            updatedBy,
            auditSource,
            sourceReference,
            batchGuid,
            actorUserGuid: null
        );
    }

    public async Task<BatchOperationResultDto> BatchCreateAsync(
        List<CreateItemDto> items,
        bool useTransaction,
        string? updatedBy,
        string auditSource,
        string? sourceReference,
        Guid? batchGuid,
        string? actorUserGuid
    )
    {
        var result = new BatchOperationResultDto { Success = true, Message = "创建完成" };
        if (items == null || items.Count == 0)
            return result;

        var effectiveUpdatedBy = ResolveUpdatedBy(updatedBy);
        var effectiveBatchGuid = batchGuid ?? Guid.NewGuid();
        var ownsTransaction = useTransaction || _context.Db.Ado.Transaction == null;

        try
        {
            if (ownsTransaction)
            {
                // 默认入口自行开事务；没有外层事务的显式调用也必须保证套装写入原子性。
                _context.Db.Ado.BeginTran();
            }
            var now = DateTime.Now;

            // 收集所有需要查询的 ProductCode 和 ItemNumber
            var codes = items
                .Select(i => i.ProductCode)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct()
                .ToList();
            // 编码完整时只占用本批商品身份：Update 总闸与无关普通成本 Shared 锁兼容，
            // 但会让同一商品的并发新建和主档身份变更互斥；缺少编码仍需全局锁兜底。
            var setChildPurchasePriceLock = items.All(item =>
                !string.IsNullOrWhiteSpace(item.ProductCode)
            )
                ? await SetChildPurchasePriceMutationLock.AcquireProductIdentitiesWithinBudgetAsync(
                    _context.Db,
                    codes
                )
                : await SetChildPurchasePriceMutationLock.AcquireAllAsync(_context.Db);
            var itemNumbers = items
                .Select(i => i.ItemNumber)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct()
                .ToList();
            var queryProducts = _context.Db.Queryable<Product>();
            HashSet<string> existingCodes;
            HashSet<string> existingItems;
            Dictionary<string, string> itemToCode = new Dictionary<string, string>();
            HashSet<string> existingWpCodes = new HashSet<string>();

            // 批量查询已存在的商品（避免 N+1 问题）
            if (codes.Any() && itemNumbers.Any())
            {
                queryProducts = queryProducts.Where(p =>
                    codes.Contains(p.ProductCode)
                    || (p.ItemNumber != null && itemNumbers.Contains(p.ItemNumber))
                );
                var existing = await queryProducts
                    .Select(p => new { p.ProductCode, p.ItemNumber })
                    .ToListAsync();
                existingCodes = existing
                    .Select(p => p.ProductCode)
                    .Where(x => x != null)
                    .Select(x => x!)
                    .ToHashSet();
                existingItems = existing
                    .Select(p => p.ItemNumber)
                    .Where(x => x != null)
                    .Select(x => x!)
                    .ToHashSet();
                foreach (var e in existing)
                {
                    if (
                        e.ItemNumber != null
                        && e.ProductCode != null
                        && !itemToCode.ContainsKey(e.ItemNumber)
                    )
                    {
                        itemToCode[e.ItemNumber] = e.ProductCode;
                    }
                }
                var mappedCodes = existing
                    .Select(p => p.ProductCode)
                    .Where(x => x != null)
                    .Select(x => x!)
                    .Distinct()
                    .ToList();
                var wpExisting = await _context
                    .Db.Queryable<WarehouseProduct>()
                    .Where(w => mappedCodes.Contains(w.ProductCode))
                    .Select(w => w.ProductCode)
                    .ToListAsync();
                existingWpCodes = wpExisting.ToHashSet();
            }
            else if (codes.Any())
            {
                queryProducts = queryProducts.Where(p => codes.Contains(p.ProductCode));
                var existing = await queryProducts
                    .Select(p => new { p.ProductCode, p.ItemNumber })
                    .ToListAsync();
                existingCodes = existing
                    .Select(p => p.ProductCode)
                    .Where(x => x != null)
                    .Select(x => x!)
                    .ToHashSet();
                existingItems = existing
                    .Select(p => p.ItemNumber)
                    .Where(x => x != null)
                    .Select(x => x!)
                    .ToHashSet();
                foreach (var e in existing)
                {
                    if (
                        e.ItemNumber != null
                        && e.ProductCode != null
                        && !itemToCode.ContainsKey(e.ItemNumber)
                    )
                    {
                        itemToCode[e.ItemNumber] = e.ProductCode;
                    }
                }
                var wpExisting = await _context
                    .Db.Queryable<WarehouseProduct>()
                    .Where(w => codes.Contains(w.ProductCode))
                    .Select(w => w.ProductCode)
                    .ToListAsync();
                existingWpCodes = wpExisting.ToHashSet();
            }
            else if (itemNumbers.Any())
            {
                queryProducts = queryProducts.Where(p =>
                    p.ItemNumber != null && itemNumbers.Contains(p.ItemNumber)
                );
                var existing = await queryProducts
                    .Select(p => new { p.ProductCode, p.ItemNumber })
                    .ToListAsync();
                existingCodes = existing
                    .Select(p => p.ProductCode)
                    .Where(x => x != null)
                    .Select(x => x!)
                    .ToHashSet();
                existingItems = existing
                    .Select(p => p.ItemNumber)
                    .Where(x => x != null)
                    .Select(x => x!)
                    .ToHashSet();
                foreach (var e in existing)
                {
                    if (
                        e.ItemNumber != null
                        && e.ProductCode != null
                        && !itemToCode.ContainsKey(e.ItemNumber)
                    )
                    {
                        itemToCode[e.ItemNumber] = e.ProductCode;
                    }
                }
                var mappedCodes = existing
                    .Select(p => p.ProductCode)
                    .Where(x => x != null)
                    .Select(x => x!)
                    .Distinct()
                    .ToList();
                var wpExisting = await _context
                    .Db.Queryable<WarehouseProduct>()
                    .Where(w => mappedCodes.Contains(w.ProductCode))
                    .Select(w => w.ProductCode)
                    .ToListAsync();
                existingWpCodes = wpExisting.ToHashSet();
            }
            else
            {
                existingCodes = new HashSet<string>();
                existingItems = new HashSet<string>();
                existingWpCodes = new HashSet<string>();
            }

            // 待创建的商品、仓库商品、套装编码列表
            var toCreateProducts = new List<Product>();
            var toCreateWps = new List<WarehouseProduct>();
            var toCreateSetCodes = new List<ProductSetCode>();

            // 收集所有套装商品的 ProductCode（用于批量查询，避免 N+1 问题）
            var setProductCodesToQuery = new HashSet<string>();
            foreach (var item in items)
            {
                if (!item.IsSetProduct)
                    continue;
                var code = item.ProductCode;
                if (string.IsNullOrWhiteSpace(code))
                {
                    if (
                        !string.IsNullOrWhiteSpace(item.ItemNumber)
                        && itemToCode.TryGetValue(item.ItemNumber!, out var mapped)
                    )
                    {
                        code = mapped;
                    }
                }
                if (!string.IsNullOrWhiteSpace(code))
                    setProductCodesToQuery.Add(code!);
            }

            // 批量查询套装商品关联数据（一次性查询，避免 N+1）
            var setProductsByCode = setProductCodesToQuery.Any()
                ? (
                    await _context
                        .Db.Queryable<DomesticSetProduct>()
                        .Where(sp =>
                            setProductCodesToQuery.Contains(sp.ProductCode) && !sp.IsDeleted
                        )
                        .ToListAsync()
                )
                    .GroupBy(sp => sp.ProductCode)
                    .ToDictionary(g => g.Key, g => g.ToList())
                : new Dictionary<string, List<DomesticSetProduct>>();

            // 遍历处理每个商品
            foreach (var item in items)
            {
                // 验证必填字段
                if (string.IsNullOrWhiteSpace(item.ItemNumber))
                {
                    result.Errors.Add("ItemNumber cannot be empty");
                    result.FailedCount++;
                    continue;
                }
                if (item.OEMPrice <= 0)
                {
                    result.Errors.Add($"RRP must be greater than 0: {item.ItemNumber}");
                    result.FailedCount++;
                    continue;
                }
                if (item.ImportPrice <= 0)
                {
                    result.Errors.Add(
                        $"Import price must be greater than 0: {item.ItemNumber}"
                    );
                    result.FailedCount++;
                    continue;
                }

                // 确定 ProductCode（如果未提供则自动生成）
                var code = item.ProductCode;
                if (string.IsNullOrWhiteSpace(code))
                {
                    if (
                        !string.IsNullOrWhiteSpace(item.ItemNumber)
                        && itemToCode.TryGetValue(item.ItemNumber!, out var mapped)
                    )
                    {
                        code = mapped;
                    }
                    else
                    {
                        code = UuidHelper.GenerateUuid7();
                    }
                }

                // 检查是否已存在
                var wpExists =
                    !string.IsNullOrWhiteSpace(code) && existingWpCodes.Contains(code!);
                var productExists =
                    existingCodes.Contains(code)
                    || (
                        !string.IsNullOrWhiteSpace(item.ItemNumber)
                        && existingItems.Contains(item.ItemNumber!)
                    );

                // 跳过已存在的仓库商品
                if (wpExists)
                {
                    result.SkippedItems.Add(item.ItemNumber);
                    result.SkippedCount++;
                    continue;
                }

                // 创建商品记录（如果不存在）
                if (!productExists)
                {
                    var product = new Product
                    {
                        ProductCode = code,
                        ItemNumber = item.ItemNumber,
                        Barcode = item.Barcode,
                        LocalSupplierCode = "200",
                        ProductName =
                            !string.IsNullOrWhiteSpace(item.EnglishName) ? item.EnglishName
                            : !string.IsNullOrWhiteSpace(item.ChineseName) ? item.ChineseName
                            : item.ItemNumber,
                        EnglishName = item.EnglishName,
                        PurchasePrice = item.ImportPrice,
                        RetailPrice = item.OEMPrice,
                        ProductImage = ProductImageUrlHelper.EnsureImageUrl(
                            item.ImageUrl,
                            item.ItemNumber ?? code
                        ),
                        WarehouseCategoryGUID = string.IsNullOrWhiteSpace(item.WarehouseCategoryGUID)
                            ? null
                            : item.WarehouseCategoryGUID.Trim(),
                        // 货柜创建套装时必须同步写 POS 商品类型，否则 POS 商品管理会把空值显示为单品。
                        ProductType = item.ProductType ?? (item.IsSetProduct ? 1 : 0),
                        IsAutoPricing = false,
                        IsActive = true,
                        IsDeleted = false,
                        CreatedAt = now,
                        UpdatedAt = now,
                        CreatedBy = effectiveUpdatedBy,
                        UpdatedBy = effectiveUpdatedBy,
                    };
                    toCreateProducts.Add(product);
                }

                // 创建仓库商品记录
                var wp = new WarehouseProduct
                {
                    ProductCode = code,
                    DomesticPrice = item.DomesticPrice,
                    OEMPrice = item.OEMPrice,
                    ImportPrice = item.ImportPrice,
                    Volume = item.Volume,
                    StockQuantity = 0,
                    IsActive = true,
                    IsDeleted = false,
                    CreatedAt = now,
                    UpdatedAt = now,
                    CreatedBy = effectiveUpdatedBy,
                    UpdatedBy = effectiveUpdatedBy,
                };
                toCreateWps.Add(wp);

                // 处理套装商品（使用内存查找，避免 N+1）
                if (item.IsSetProduct && !string.IsNullOrWhiteSpace(code))
                {
                    if (setProductsByCode.TryGetValue(code!, out var setProducts))
                    {
                        foreach (var sp in setProducts)
                        {
                            var setCode = new ProductSetCode
                            {
                                SetCodeId = sp.SetProductCode!,
                                ProductCode = code,
                                SetProductCode = sp.SetProductCode!,
                                SetItemNumber = sp.SetProductNo,
                                SetBarcode = sp.SetBarcode,
                                // Type1/Type2 子项成本都是派生值，待全部关系和门店投影落库后统一重算。
                                SetPurchasePrice = null,
                                SetRetailPrice = sp.OEMPrice ?? item.OEMPrice,
                                SetQuantity = 1,
                                SetType = 1,
                                IsActive = true,
                                IsDeleted = false,
                                CreatedAt = now,
                                UpdatedAt = now,
                            };
                            toCreateSetCodes.Add(setCode);
                        }
                    }
                }

                result.SuccessCount++;
            }

            var auditProductCodes = toCreateWps
                .Select(item => item.ProductCode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                auditProductCodes
            );

            // 批量插入商品
            if (toCreateProducts.Any())
            {
                await _context.Db.Insertable(toCreateProducts).ExecuteCommandAsync();
            }
            // 批量插入仓库商品
            if (toCreateWps.Any())
            {
                await _context.Db.Insertable(toCreateWps).ExecuteCommandAsync();
            }
            // 批量插入套装编码
            if (toCreateSetCodes.Any())
            {
                await _context.Db.Insertable(toCreateSetCodes).ExecuteCommandAsync();
            }

            // 本批刚新建的主商品在 Product 表里原本不存在，门店多码表里挂在这些编码下的活跃行
            // 必然是历史残留（如门店开业批量导入后主商品被删，且子项编码是文本货号而非关系 UUID）。
            // 不清掉的话会与本次按关系主键写入的子项并存，套装成本重算校验会判「门店子项不完整」而整体回滚。
            // 软删除并保留行，可追溯；校验与查询都只看未删除行。
            var newProductCodes = toCreateProducts
                .Select(p => p.ProductCode)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c!)
                .Distinct()
                .ToList();
            if (newProductCodes.Count > 0)
            {
                var cleaned = await _context
                    .Db.Updateable<StoreMultiCodeProduct>()
                    .SetColumns(x => new StoreMultiCodeProduct
                    {
                        IsDeleted = true,
                        IsActive = false,
                        UpdatedAt = now,
                    })
                    .Where(x => newProductCodes.Contains(x.ProductCode!) && !x.IsDeleted)
                    .ExecuteCommandAsync();
                if (cleaned > 0)
                {
                    _logger.LogWarning(
                        "新建商品前清理了 {Count} 条无主商品的门店多码残留行，商品编码: {Codes}",
                        cleaned,
                        string.Join(",", newProductCodes)
                    );
                }
            }

            // 同步到门店零售价和多码商品表
            var activeStores = (
                await _context
                    .Db.Queryable<Store>()
                    .Where(s => s.IsActive == true && s.IsDeleted == false)
                    .Select(s => s.StoreCode)
                    .ToListAsync()
            )
                .Where(storeCode => !string.IsNullOrWhiteSpace(storeCode))
                .Select(storeCode => storeCode!)
                .Distinct()
                .ToList();

            if (activeStores.Any() && toCreateProducts.Any())
            {
                var toCreateStoreRetailPrices = new List<StoreRetailPrice>();
                var toCreateStoreMultiCodeProducts = new List<StoreMultiCodeProduct>();

                var createdProductsDict = toCreateProducts
                    .Where(p => !string.IsNullOrWhiteSpace(p.ProductCode))
                    .GroupBy(p => p.ProductCode!)
                    .ToDictionary(g => g.Key, g => g.First());

                foreach (var product in toCreateProducts)
                {
                    foreach (var storeCode in activeStores)
                    {
                        toCreateStoreRetailPrices.Add(
                            new StoreRetailPrice
                            {
                                UUID = UuidHelper.GenerateUuid7(),
                                StoreCode = storeCode,
                                ProductCode = product.ProductCode,
                                StoreProductCode = storeCode + product.ProductCode,
                                SupplierCode = product.LocalSupplierCode,
                                PurchasePrice = product.PurchasePrice,
                                StoreRetailPriceValue = product.RetailPrice,
                                DiscountRate = null,
                                IsActive = true,
                                IsAutoPricing = false,
                                IsSpecialProduct = false,
                                CreatedAt = now,
                                UpdatedAt = now,
                            }
                        );
                    }
                }

                foreach (var setCode in toCreateSetCodes)
                {
                    foreach (var storeCode in activeStores)
                    {
                        toCreateStoreMultiCodeProducts.Add(
                            new StoreMultiCodeProduct
                            {
                                UUID = UuidHelper.GenerateUuid7(),
                                StoreCode = storeCode,
                                ProductCode = setCode.ProductCode,
                                MultiCodeProductCode = setCode.SetProductCode,
                                StoreMultiCodeProductCode = storeCode + setCode.SetProductCode,
                                MultiBarcode = setCode.SetBarcode,
                                PurchasePrice = setCode.SetPurchasePrice,
                                MultiCodeRetailPrice = setCode.SetRetailPrice,
                                DiscountRate = null,
                                IsActive = true,
                                IsAutoPricing = false,
                                IsSpecialProduct = false,
                                CreatedAt = now,
                                UpdatedAt = now,
                            }
                        );
                    }
                }

                if (toCreateStoreRetailPrices.Any())
                {
                    await _context
                        .Db.Insertable(toCreateStoreRetailPrices)
                        .PageSize(1000)
                        .ExecuteCommandAsync();
                    _logger.LogInformation(
                        "创建了 {Count} 条分店价格记录",
                        toCreateStoreRetailPrices.Count
                    );
                }

                if (toCreateStoreMultiCodeProducts.Any())
                {
                    await _context
                        .Db.Insertable(toCreateStoreMultiCodeProducts)
                        .PageSize(1000)
                        .ExecuteCommandAsync();
                    _logger.LogInformation(
                        "创建了 {Count} 条分店多码记录",
                        toCreateStoreMultiCodeProducts.Count
                    );
                }
            }

            var createdSetProductCodes = toCreateSetCodes
                .Where(setCode => IsCostDerivedSetType(setCode.SetType))
                .Select(setCode => setCode.ProductCode)
                .ToList();
            if (createdSetProductCodes.Count > 0)
            {
                var recalculation = await new SetChildPurchasePriceService(
                    _context.Db
                ).RecalculateLockedAsync(
                    setChildPurchasePriceLock,
                    createdSetProductCodes,
                    storeCodes: activeStores,
                    updatedBy: effectiveUpdatedBy
                );
                EnsureSetChildPurchasePriceRecalculated(
                    recalculation,
                    "批量创建后的套装子项成本重算不完整"
                );
            }

            await RecordProductChangeHistoryAsync(
                beforeSnapshots,
                auditProductCodes,
                action: "Create",
                source: auditSource,
                actorName: effectiveUpdatedBy,
                batchGuid: effectiveBatchGuid,
                sourceReference: sourceReference,
                actorUserGuid: actorUserGuid
            );

            if (ownsTransaction)
            {
                _context.Db.Ado.CommitTran();
            }
        }
        catch (Exception ex)
        {
            if (ownsTransaction)
            {
                _context.Db.Ado.RollbackTran();
            }
            if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _))
            {
                throw;
            }
            _logger.LogError(ex, "批量创建失败");
            return new BatchOperationResultDto
            {
                Success = false,
                Message = "批量创建失败: " + ex.Message,
                Errors = new List<string> { ex.Message },
            };
        }

        return result;
    }
}
