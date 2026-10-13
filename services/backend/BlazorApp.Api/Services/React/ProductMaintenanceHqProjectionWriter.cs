using System.Text.Json;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HqEntities;
using SqlSugar;

namespace BlazorApp.Api.Services.React;

/// <summary>
/// 将商品维护事务的最终本地状态写入可靠 outbox，并在后台把窄范围变更精确投影到 HQ。
/// </summary>
public sealed class ProductMaintenanceHqProjectionWriter : IProductMaintenanceHqProjectionWriter
{
    private const string SafeEnqueueFailureMessage = "HQ 同步任务创建失败，请稍后重试";
    private const string AmbiguousMultiCodeErrorCode =
        "PRODUCT_HQ_MUTATION_AMBIGUOUS_MULTI_CODE";
    private static readonly JsonSerializerOptions JsonOptions = new();
    private static readonly HashSet<string> ExactStorePriceFields = new(
        new[]
        {
            ProductMaintenanceHqFieldMasks.StorePurchasePrice,
            ProductMaintenanceHqFieldMasks.StoreRetailPrice,
            ProductMaintenanceHqFieldMasks.StoreDiscountRate,
            ProductMaintenanceHqFieldMasks.StoreAutoPricing,
            ProductMaintenanceHqFieldMasks.StoreSpecialProduct,
            ProductMaintenanceHqFieldMasks.StoreActive,
        },
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly HashSet<string> KnownFieldMasks = new(
        new[]
        {
            ProductMaintenanceHqFieldMasks.All,
            ProductMaintenanceHqFieldMasks.ProductType,
            ProductMaintenanceHqFieldMasks.ProductName,
            ProductMaintenanceHqFieldMasks.StorePurchasePrice,
            ProductMaintenanceHqFieldMasks.StoreRetailPrice,
            ProductMaintenanceHqFieldMasks.StoreDiscountRate,
            ProductMaintenanceHqFieldMasks.StoreAutoPricing,
            ProductMaintenanceHqFieldMasks.StoreSpecialProduct,
            ProductMaintenanceHqFieldMasks.StoreActive,
            ProductMaintenanceHqFieldMasks.ProductSetCodes,
            ProductMaintenanceHqFieldMasks.StoreMultiCodes,
            ProductMaintenanceHqFieldMasks.StoreClearancePrice,
        },
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly HashSet<string> KnownOperationKinds = new(
        new[]
        {
            ProductMaintenanceHqOperationKinds.ProductCreated,
            ProductMaintenanceHqOperationKinds.ProductTypeUpdated,
            ProductMaintenanceHqOperationKinds.ProductNameUpdated,
            ProductMaintenanceHqOperationKinds.StorePriceUpdated,
            ProductMaintenanceHqOperationKinds.WarehousePriceSynced,
            ProductMaintenanceHqOperationKinds.ProductCodesUpdated,
            ProductMaintenanceHqOperationKinds.ProductCodesDeleted,
            ProductMaintenanceHqOperationKinds.SetCodeSnapshot,
            ProductMaintenanceHqOperationKinds.ClearancePriceUpdated,
            ProductMaintenanceHqOperationKinds.ClearancePriceDeleted,
        },
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly HashSet<string> KnownResourceKinds = new(
        new[]
        {
            ProductMaintenanceHqResourceKinds.ProductSetCode,
            ProductMaintenanceHqResourceKinds.StoreMultiCode,
            ProductMaintenanceHqResourceKinds.StoreClearancePrice,
        },
        StringComparer.OrdinalIgnoreCase
    );

    private readonly SqlSugarContext _localContext;
    private readonly HqSqlSugarContext _hqContext;
    private readonly IProductHqSyncOutboxQueue _queue;
    private readonly IProductHqSyncService _hqSync;
    private readonly ILogger<ProductMaintenanceHqProjectionWriter> _logger;

    public ProductMaintenanceHqProjectionWriter(
        SqlSugarContext localContext,
        HqSqlSugarContext hqContext,
        IProductHqSyncOutboxQueue queue,
        IProductHqSyncService hqSync,
        ILogger<ProductMaintenanceHqProjectionWriter> logger
    )
    {
        _localContext = localContext;
        _hqContext = hqContext;
        _queue = queue;
        _hqSync = hqSync;
        _logger = logger;
    }

    public async Task<ProductHqSyncOperationStatusDto> EnqueueAsync(
        ISqlSugarClient transactionDb,
        ProductMaintenanceHqMutationRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(transactionDb);
        ArgumentNullException.ThrowIfNull(request);

        var productCode = NormalizeRequired(request.ProductCode, nameof(request.ProductCode));
        var source = NormalizeRequired(request.Source, nameof(request.Source));
        var operationKind = NormalizeRequired(request.OperationKind, nameof(request.OperationKind));
        var storeCodes = NormalizeStoreCodes(request.TargetStoreCodes);
        try
        {
            var payload = await ReadProjectionAsync(transactionDb, productCode, storeCodes);
            var enqueueResult = await _queue.EnqueueAsync(
                transactionDb,
                new ProductHqSyncOutboxEnqueueRequest
                {
                    OperationKey = $"{source}:{Guid.NewGuid():N}",
                    OperationKind = operationKind,
                    ProductCode = productCode,
                    TargetStoreCodes = storeCodes,
                    AuthorizedStoreCodes = NormalizeStoreCodes(request.AuthorizedStoreCodes),
                    FieldMask = request.FieldMask
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Select(item => item.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
                    Tombstones = request.Tombstones
                        .Where(item =>
                            !string.IsNullOrWhiteSpace(item.ResourceKind)
                            && !string.IsNullOrWhiteSpace(item.BusinessKey)
                        )
                        .Distinct()
                        .ToList(),
                    RequestedByUserGuid = request.RequestedByUserGuid,
                    RequestedByDeviceId = request.RequestedByDeviceId,
                    Source = source,
                    OccurredAtUtc = request.OccurredAtUtc.Kind == DateTimeKind.Utc
                        ? request.OccurredAtUtc
                        : request.OccurredAtUtc.ToUniversalTime(),
                },
                cancellationToken
            );
            return enqueueResult.Operation;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 数据库原始异常只进入服务端日志，禁止被上层 ApiResponse 拼接后泄露给客户端。
            _logger.LogError(
                ex,
                "商品 HQ 同步任务入队失败: {OperationKind}/{ProductCode}",
                operationKind,
                productCode
            );
            throw new ProductMaintenanceHqEnqueueException(SafeEnqueueFailureMessage);
        }
    }

    public async Task<ProductHqSyncOutboxExecutionResult> ApplyAsync(
        ProductHqSyncOutboxWorkItemDto workItem,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(workItem);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(workItem.ProductCode))
        {
            return ProductHqSyncOutboxExecutionResult.Blocked(
                "PRODUCT_HQ_MUTATION_INVALID_PRODUCT",
                "商品编码无效，无法同步 HQ"
            );
        }
        if (!KnownOperationKinds.Contains(workItem.OperationKind))
        {
            return ProductHqSyncOutboxExecutionResult.Blocked(
                "PRODUCT_HQ_MUTATION_UNSUPPORTED_KIND",
                "HQ 同步操作类型无效"
            );
        }

        var productCode = workItem.ProductCode.Trim();
        var fields = workItem.FieldMask.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (
            fields.Any(item => !KnownFieldMasks.Contains(item))
            || (fields.Count == 0 && workItem.Tombstones.Count == 0)
        )
        {
            return ProductHqSyncOutboxExecutionResult.Blocked(
                "PRODUCT_HQ_MUTATION_INVALID_FIELD_MASK",
                "HQ 同步字段映射无效"
            );
        }
        if (workItem.Tombstones.Any(item => IsInvalidTombstone(item, workItem.Tombstones)))
        {
            // 删除任务缺少业务键或精确门店时不能猜测范围，更不能静默标记成功。
            return ProductHqSyncOutboxExecutionResult.Blocked(
                "PRODUCT_HQ_MUTATION_INVALID_TOMBSTONE",
                "HQ 同步删除范围无效"
            );
        }
        var hasExactStorePriceFields = fields.Any(ExactStorePriceFields.Contains);

        try
        {
            if (fields.Contains(ProductMaintenanceHqFieldMasks.All))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await DelegateExistingPushAsync(productCode, null, null);
            }

            var delegatedFields = new List<string>();
            if (fields.Contains(ProductMaintenanceHqFieldMasks.ProductType))
            {
                delegatedFields.Add(ProductMaintenanceHqFieldMasks.ProductType);
            }
            if (fields.Contains(ProductMaintenanceHqFieldMasks.ProductName))
            {
                // 中文名走既有的 productName 字段推送：只更新 HQ「大写名称」，不碰英文显示名与其他列。
                delegatedFields.Add(ProductMaintenanceHqFieldMasks.ProductName);
            }
            if (fields.Contains(ProductMaintenanceHqFieldMasks.ProductSetCodes))
            {
                delegatedFields.Add(ProductMaintenanceHqFieldMasks.ProductSetCodes);
            }
            if (delegatedFields.Count > 0)
            {
                if (
                    !string.Equals(
                        workItem.OperationKind,
                        ProductMaintenanceHqOperationKinds.ProductCreated,
                        StringComparison.OrdinalIgnoreCase
                    )
                    && !await _hqContext.Db.Queryable<DIC_商品信息字典表>()
                        .AnyAsync(item => item.H商品编码 == productCode)
                )
                {
                    // 编辑任务只能投影到既有 HQ 主档，禁止借窄字段更新隐式创建残缺商品图。
                    return ProductHqSyncOutboxExecutionResult.Blocked(
                        "PRODUCT_HQ_MUTATION_PRODUCT_NOT_READY",
                        "HQ 未找到对应商品"
                    );
                }

                if (fields.Contains(ProductMaintenanceHqFieldMasks.ProductSetCodes))
                {
                    var identityValidation = await ValidateProductSetCodeIdentitiesAsync(
                        productCode,
                        _hqContext.Db
                    );
                    if (identityValidation != null)
                    {
                        return identityValidation;
                    }
                }
                if (fields.Contains(ProductMaintenanceHqFieldMasks.StoreMultiCodes))
                {
                    var identityValidation = await ValidateStoreMultiCodeIdentitiesAsync(
                        productCode,
                        workItem.TargetStoreCodes,
                        _hqContext.Db
                    );
                    if (identityValidation != null)
                    {
                        return identityValidation;
                    }
                }
                if (workItem.Tombstones.Count > 0)
                {
                    // 委托既有推送前先只读解析墓碑，避免身份歧义已知时产生任何 HQ 写入。
                    var identityValidation = await ApplyTombstonesAsync(
                        productCode,
                        workItem.Tombstones,
                        _hqContext.Db,
                        writeChanges: false
                    );
                    if (identityValidation != null)
                    {
                        return identityValidation;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                var delegatedResult = await DelegateExistingPushAsync(
                    productCode,
                    delegatedFields,
                    workItem.TargetStoreCodes?.ToList()
                );
                if (delegatedResult.Disposition != ProductHqSyncOutboxExecutionDisposition.Success)
                {
                    return delegatedResult;
                }
            }

            var requiresDirectWrite =
                hasExactStorePriceFields
                || fields.Contains(ProductMaintenanceHqFieldMasks.StoreMultiCodes)
                || fields.Contains(ProductMaintenanceHqFieldMasks.StoreClearancePrice)
                || workItem.Tombstones.Count > 0;
            if (!requiresDirectWrite)
            {
                return ProductHqSyncOutboxExecutionResult.Succeeded();
            }

            var hqDb = _hqContext.Db;
            cancellationToken.ThrowIfCancellationRequested();
            await hqDb.Ado.BeginTranAsync();
            ProductHqMutationExecutionLock? hqMutationLock = null;
            try
            {
                hqMutationLock = await ProductHqMutationExecutionLock.AcquireAsync(
                    hqDb,
                    new[] { productCode },
                    cancellationToken
                );
                if (hqMutationLock == null)
                {
                    await hqDb.Ado.RollbackTranAsync();
                    return ProductHqSyncOutboxExecutionResult.Retryable(
                        SetChildPurchasePriceMutationLock.BusyErrorCode,
                        "HQ 商品同步正忙，系统将自动重试"
                    );
                }

                if (hasExactStorePriceFields)
                {
                    var storeValidation = ValidateStoreScope(workItem.TargetStoreCodes);
                    if (storeValidation != null)
                    {
                        await hqDb.Ado.RollbackTranAsync();
                        return storeValidation;
                    }

                    foreach (var storeCode in NormalizeStoreCodes(workItem.TargetStoreCodes)!)
                    {
                        var exactResult = await ApplyExactStorePriceAsync(
                            productCode,
                            storeCode,
                            hqDb
                        );
                        if (exactResult != null)
                        {
                            await hqDb.Ado.RollbackTranAsync();
                            return exactResult;
                        }
                    }
                }

                if (fields.Contains(ProductMaintenanceHqFieldMasks.StoreMultiCodes))
                {
                    var storeValidation = ValidateStoreScope(workItem.TargetStoreCodes);
                    if (storeValidation != null)
                    {
                        await hqDb.Ado.RollbackTranAsync();
                        return storeValidation;
                    }

                    foreach (var storeCode in NormalizeStoreCodes(workItem.TargetStoreCodes)!)
                    {
                        var exactResult = await ApplyExactStoreMultiCodesAsync(
                            productCode,
                            storeCode,
                            hqDb,
                            hasExactStorePriceFields
                        );
                        if (exactResult != null)
                        {
                            await hqDb.Ado.RollbackTranAsync();
                            return exactResult;
                        }
                    }
                }

                if (fields.Contains(ProductMaintenanceHqFieldMasks.StoreClearancePrice))
                {
                    var storeValidation = ValidateStoreScope(workItem.TargetStoreCodes);
                    if (storeValidation != null)
                    {
                        await hqDb.Ado.RollbackTranAsync();
                        return storeValidation;
                    }

                    foreach (var storeCode in NormalizeStoreCodes(workItem.TargetStoreCodes)!)
                    {
                        var exactResult = await ApplyExactClearancePriceAsync(
                            productCode,
                            storeCode,
                            hqDb
                        );
                        if (exactResult != null)
                        {
                            await hqDb.Ado.RollbackTranAsync();
                            return exactResult;
                        }
                    }
                }

                var tombstoneResult = await ApplyTombstonesAsync(
                    productCode,
                    workItem.Tombstones,
                    hqDb
                );
                if (tombstoneResult != null)
                {
                    await hqDb.Ado.RollbackTranAsync();
                    return tombstoneResult;
                }
                cancellationToken.ThrowIfCancellationRequested();
                await hqDb.Ado.CommitTranAsync();
                return ProductHqSyncOutboxExecutionResult.Succeeded();
            }
            catch
            {
                await hqDb.Ado.RollbackTranAsync();
                throw;
            }
            finally
            {
                if (hqMutationLock != null)
                {
                    await hqMutationLock.DisposeAsync();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "商品维护 HQ 投影失败: OperationId={OperationId}, ProductCode={ProductCode}, OperationKind={OperationKind}",
                workItem.OperationKey,
                productCode,
                workItem.OperationKind
            );
            return ProductHqSyncOutboxExecutionResult.Retryable(
                "PRODUCT_HQ_MUTATION_TRANSIENT_ERROR",
                "HQ 暂时无法完成同步，系统将自动重试"
            );
        }
    }

    private async Task<ProductMaintenanceHqProjectionPayloadDto> ReadProjectionAsync(
        ISqlSugarClient db,
        string productCode,
        List<string>? storeCodes
    )
    {
        var product = await db.Queryable<Product>()
            .Where(item => item.ProductCode == productCode && !item.IsDeleted)
            .FirstAsync();
        var storePrices = await db.Queryable<StoreRetailPrice>()
            .Where(item => item.ProductCode == productCode && !item.IsDeleted)
            .ToListAsync();
        var setCodes = await db.Queryable<ProductSetCode>()
            .Where(item => item.ProductCode == productCode && !item.IsDeleted)
            .ToListAsync();
        var multiCodes = await db.Queryable<StoreMultiCodeProduct>()
            .Where(item => item.ProductCode == productCode && !item.IsDeleted)
            .ToListAsync();
        var clearancePrices = await db.Queryable<StoreClearancePrice>()
            .Where(item => item.ProductCode == productCode && !item.IsDeleted)
            .ToListAsync();
        if (storeCodes != null)
        {
            var scope = storeCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            storePrices = storePrices
                .Where(item => item.StoreCode != null && scope.Contains(item.StoreCode))
                .ToList();
            multiCodes = multiCodes
                .Where(item => item.StoreCode != null && scope.Contains(item.StoreCode))
                .ToList();
            clearancePrices = clearancePrices
                .Where(item => item.StoreCode != null && scope.Contains(item.StoreCode))
                .ToList();
        }

        return new ProductMaintenanceHqProjectionPayloadDto
        {
            Product = product == null
                ? null
                : new ProductMaintenanceHqProductProjectionDto
                {
                    ProductCode = productCode,
                    SupplierCode = product.LocalSupplierCode,
                    ItemNumber = product.ItemNumber,
                    Barcode = product.Barcode,
                    ProductName = product.ProductName,
                    EnglishName = product.EnglishName,
                    ProductType = product.ProductType,
                    PurchasePrice = product.PurchasePrice,
                    RetailPrice = product.RetailPrice,
                    IsAutoPricing = product.IsAutoPricing,
                    IsSpecialProduct = product.IsSpecialProduct,
                    IsActive = product.IsActive,
                },
            StorePrices = storePrices.Select(item => new ProductMaintenanceHqStorePriceProjectionDto
            {
                StoreCode = item.StoreCode ?? string.Empty,
                ProductCode = productCode,
                StoreProductCode = item.StoreProductCode,
                SupplierCode = item.SupplierCode,
                PurchasePrice = item.PurchasePrice,
                RetailPrice = item.StoreRetailPriceValue,
                DiscountRate = item.DiscountRate,
                IsAutoPricing = item.IsAutoPricing,
                IsSpecialProduct = item.IsSpecialProduct,
                IsActive = item.IsActive,
            }).ToList(),
            ProductSetCodes = setCodes.Select(item => new ProductMaintenanceHqSetCodeProjectionDto
            {
                SetCodeId = item.SetCodeId,
                ProductCode = productCode,
                SetProductCode = item.SetProductCode,
                SetItemNumber = item.SetItemNumber,
                SetBarcode = item.SetBarcode,
                PurchasePrice = item.SetPurchasePrice,
                RetailPrice = item.SetRetailPrice,
                Quantity = item.SetQuantity,
                Type = item.SetType,
                IsActive = item.IsActive,
            }).ToList(),
            StoreMultiCodes = multiCodes.Select(item => new ProductMaintenanceHqStoreMultiCodeProjectionDto
            {
                StoreCode = item.StoreCode ?? string.Empty,
                ProductCode = productCode,
                MultiCodeProductCode = item.MultiCodeProductCode ?? string.Empty,
                StoreMultiCodeProductCode = item.StoreMultiCodeProductCode,
                Barcode = item.MultiBarcode,
                PurchasePrice = item.PurchasePrice,
                RetailPrice = item.MultiCodeRetailPrice,
                DiscountRate = item.DiscountRate,
                IsAutoPricing = item.IsAutoPricing,
                IsSpecialProduct = item.IsSpecialProduct,
                IsActive = item.IsActive,
            }).ToList(),
            ClearancePrices = clearancePrices.Select(item => new ProductMaintenanceHqClearancePriceProjectionDto
            {
                StoreCode = item.StoreCode ?? string.Empty,
                ProductCode = productCode,
                Barcode = item.ClearanceBarcode,
                Price = item.ClearancePrice,
            }).ToList(),
        };
    }

    private async Task<ProductHqSyncOutboxExecutionResult> DelegateExistingPushAsync(
        string productCode,
        List<string>? updateFields,
        List<string>? targetStoreCodes
    )
    {
        var response = await _hqSync.PushToHqAsync(new PushProductsToHqRequest
        {
            ProductCodes = new List<string> { productCode },
            UpdateFields = updateFields,
            TargetStoreCodes = targetStoreCodes,
        });
        if (response?.Success == true)
        {
            return ProductHqSyncOutboxExecutionResult.Succeeded();
        }

        var errorCode = response?.ErrorCode ?? "PRODUCT_HQ_MUTATION_PUSH_FAILED";
        if (
            string.Equals(
                errorCode,
                SetChildPurchasePriceMutationLock.BusyErrorCode,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return ProductHqSyncOutboxExecutionResult.Retryable(
                errorCode,
                "HQ 商品同步正忙，系统将自动重试"
            );
        }

        if (
            errorCode is "PRODUCT_HQ_PUSH_EMPTY_CODES"
                or "PRODUCT_HQ_PUSH_NO_PRODUCTS"
                or "PRODUCT_HQ_PUSH_UNKNOWN_STORE_CODES"
                or "PRODUCT_HQ_PUSH_EMPTY_TARGET_STORES"
                or "PRODUCT_HQ_PUSH_ITEM_ERRORS"
        )
        {
            return ProductHqSyncOutboxExecutionResult.Blocked(
                errorCode,
                "商品当前无法推送到 HQ"
            );
        }

        return ProductHqSyncOutboxExecutionResult.Retryable(
            errorCode,
            "HQ 暂时无法完成同步，系统将自动重试"
        );
    }

    private async Task<ProductHqSyncOutboxExecutionResult?> ApplyExactStorePriceAsync(
        string productCode,
        string storeCode,
        ISqlSugarClient hqDb
    )
    {
        var localDb = _localContext.Db;
        var localPrice = await localDb.Queryable<StoreRetailPrice>()
            .Where(item =>
                item.ProductCode == productCode
                && item.StoreCode == storeCode
                && !item.IsDeleted
            )
            .FirstAsync();
        var product = await localDb.Queryable<Product>()
            .Where(item => item.ProductCode == productCode && !item.IsDeleted)
            .FirstAsync();
        if (localPrice == null || product == null)
        {
            return ProductHqSyncOutboxExecutionResult.Blocked(
                "PRODUCT_HQ_MUTATION_LOCAL_PRICE_NOT_FOUND",
                "未找到当前门店商品价格"
            );
        }

        if (!await hqDb.Queryable<HqBranch>().AnyAsync(item => item.BranchCode == storeCode))
        {
            return ProductHqSyncOutboxExecutionResult.Blocked(
                "PRODUCT_HQ_MUTATION_STORE_NOT_FOUND",
                "HQ 未配置当前分店"
            );
        }
        if (
            !await hqDb.Queryable<DIC_商品信息字典表>()
                .AnyAsync(item => item.H商品编码 == productCode)
        )
        {
            return ProductHqSyncOutboxExecutionResult.Blocked(
                "PRODUCT_HQ_MUTATION_PRODUCT_NOT_READY",
                "HQ 未找到对应商品"
            );
        }

        var supplierCode = NormalizeOptional(localPrice.SupplierCode)
            ?? NormalizeOptional(product.LocalSupplierCode)
            ?? "200";
        var now = DateTime.Now;
        var affected = await hqDb.Updateable<DIC_商品零售价表>()
            .SetColumns(item => new DIC_商品零售价表
            {
                H进货价 = localPrice.PurchasePrice ?? 0,
                H分店零售价 = localPrice.StoreRetailPriceValue ?? 0,
                H折扣率 = localPrice.DiscountRate ?? 0,
                H是否自动定价 = localPrice.IsAutoPricing,
                H是否特殊商品 = localPrice.IsSpecialProduct,
                H使用状态 = localPrice.IsActive,
                FGC_LastModifier = "HBweb",
                FGC_LastModifyDate = now,
            })
            .Where(item => item.H分店代码 == storeCode && item.H商品编码 == productCode)
            .ExecuteCommandAsync();
        if (affected > 0)
        {
            return null;
        }

        var defaultDate = new DateTime(1900, 1, 1);
        var row = new DIC_商品零售价表
        {
            HGUID = Guid.NewGuid().ToString("N"),
            H分店代码 = storeCode,
            H商品编码 = productCode,
            H分店商品编码 = storeCode + productCode,
            H供应商编码 = supplierCode,
            H分店供应商编码 = storeCode + supplierCode,
            H进货价 = localPrice.PurchasePrice ?? 0,
            H分店零售价 = localPrice.StoreRetailPriceValue ?? 0,
            H商品缺货日期 = defaultDate,
            H活动开始日期 = defaultDate,
            H活动结束日期 = defaultDate,
            H折扣率 = localPrice.DiscountRate ?? 0,
            H是否自动定价 = localPrice.IsAutoPricing,
            H是否特殊商品 = localPrice.IsSpecialProduct,
            H使用状态 = localPrice.IsActive,
            FGC_Creator = "HBweb",
            FGC_CreateDate = now,
            FGC_LastModifier = "HBweb",
            FGC_LastModifyDate = now,
        };
        await hqDb.Insertable(row).IgnoreColumns(item => item.ID).ExecuteCommandAsync();
        return null;
    }

    private async Task<ProductHqSyncOutboxExecutionResult?> ApplyExactClearancePriceAsync(
        string productCode,
        string storeCode,
        ISqlSugarClient hqDb
    )
    {
        if (!await hqDb.Queryable<HqBranch>().AnyAsync(item => item.BranchCode == storeCode))
        {
            return ProductHqSyncOutboxExecutionResult.Blocked(
                "PRODUCT_HQ_MUTATION_STORE_NOT_FOUND",
                "HQ 未配置当前分店"
            );
        }

        var local = await _localContext.Db.Queryable<StoreClearancePrice>()
            .Where(item =>
                item.ProductCode == productCode
                && item.StoreCode == storeCode
                && !item.IsDeleted
            )
            .FirstAsync();
        if (local?.ClearancePrice == null)
        {
            await hqDb.Deleteable<DIC_商品清货价表>()
                .Where(item => item.分店代码 == storeCode && item.商品编码 == productCode)
                .ExecuteCommandAsync();
            return null;
        }

        var localProductExists = await _localContext.Db.Queryable<Product>()
            .AnyAsync(item => item.ProductCode == productCode && !item.IsDeleted);
        if (!localProductExists)
        {
            return ProductHqSyncOutboxExecutionResult.Blocked(
                "PRODUCT_HQ_MUTATION_LOCAL_PRODUCT_NOT_FOUND",
                "未找到本地商品"
            );
        }
        if (
            !await hqDb.Queryable<DIC_商品信息字典表>()
                .AnyAsync(item => item.H商品编码 == productCode)
        )
        {
            return ProductHqSyncOutboxExecutionResult.Blocked(
                "PRODUCT_HQ_MUTATION_PRODUCT_NOT_READY",
                "HQ 未找到对应商品"
            );
        }

        var now = DateTime.Now;
        var affected = await hqDb.Updateable<DIC_商品清货价表>()
            .SetColumns(item => new DIC_商品清货价表
            {
                清货条形码 = local.ClearanceBarcode ?? string.Empty,
                清货价 = local.ClearancePrice.Value,
                FGC_LastModifier = "HBweb",
                FGC_LastModifyDate = now,
            })
            .Where(item => item.分店代码 == storeCode && item.商品编码 == productCode)
            .ExecuteCommandAsync();
        if (affected > 0)
        {
            return null;
        }

        var row = new DIC_商品清货价表
        {
            HGUID = Guid.NewGuid().ToString("N"),
            分店代码 = storeCode,
            商品编码 = productCode,
            清货条形码 = local.ClearanceBarcode ?? string.Empty,
            清货价 = local.ClearancePrice.Value,
            FGC_Creator = "HBweb",
            FGC_CreateDate = now,
            FGC_LastModifier = "HBweb",
            FGC_LastModifyDate = now,
        };
        await hqDb.Insertable(row).IgnoreColumns(item => item.ID).ExecuteCommandAsync();
        return null;
    }

    private async Task<ProductHqSyncOutboxExecutionResult?> ApplyExactStoreMultiCodesAsync(
        string productCode,
        string storeCode,
        ISqlSugarClient hqDb,
        bool useStorePriceProjection
    )
    {
        var localDb = _localContext.Db;
        var product = await localDb.Queryable<Product>()
            .Where(item => item.ProductCode == productCode && !item.IsDeleted)
            .FirstAsync();
        if (product == null)
        {
            return ProductHqSyncOutboxExecutionResult.Blocked(
                "PRODUCT_HQ_MUTATION_LOCAL_PRODUCT_NOT_FOUND",
                "未找到本地商品"
            );
        }
        if (!await hqDb.Queryable<HqBranch>().AnyAsync(item => item.BranchCode == storeCode))
        {
            return ProductHqSyncOutboxExecutionResult.Blocked(
                "PRODUCT_HQ_MUTATION_STORE_NOT_FOUND",
                "HQ 未配置当前分店"
            );
        }
        if (
            !await hqDb.Queryable<DIC_商品信息字典表>()
                .AnyAsync(item => item.H商品编码 == productCode)
        )
        {
            return ProductHqSyncOutboxExecutionResult.Blocked(
                "PRODUCT_HQ_MUTATION_PRODUCT_NOT_READY",
                "HQ 未找到对应商品"
            );
        }

        var storePrice = await localDb.Queryable<StoreRetailPrice>()
            .Where(item =>
                item.ProductCode == productCode
                && item.StoreCode == storeCode
                && !item.IsDeleted
            )
            .FirstAsync();
        if (useStorePriceProjection && storePrice == null)
        {
            return ProductHqSyncOutboxExecutionResult.Blocked(
                "PRODUCT_HQ_MUTATION_LOCAL_PRICE_NOT_FOUND",
                "未找到当前门店商品价格"
            );
        }
        var supplierCode = NormalizeOptional(storePrice?.SupplierCode)
            ?? NormalizeOptional(product.LocalSupplierCode)
            ?? "200";
        var rows = await localDb.Queryable<StoreMultiCodeProduct>()
            .Where(item =>
                item.ProductCode == productCode
                && item.StoreCode == storeCode
                && !item.IsDeleted
            )
            .ToListAsync();
        var existingHqRows = await hqDb.Queryable<DIC_分店一品多码表>()
            .Where(item => item.H分店代码 == storeCode && item.H商品编码 == productCode)
            .ToListAsync();
        foreach (var local in rows.Where(item => !string.IsNullOrWhiteSpace(item.MultiCodeProductCode)))
        {
            var multiCode = local.MultiCodeProductCode!.Trim();
            // 价格类 mutation 以最终门店主价格为权威；纯多码编辑才使用多码行自身字段。
            var purchasePrice = useStorePriceProjection
                ? storePrice!.PurchasePrice
                : local.PurchasePrice;
            var retailPrice = useStorePriceProjection
                ? storePrice!.StoreRetailPriceValue
                : local.MultiCodeRetailPrice;
            var discountRate = useStorePriceProjection
                ? storePrice!.DiscountRate
                : local.DiscountRate;
            var isAutoPricing = useStorePriceProjection
                ? storePrice!.IsAutoPricing
                : local.IsAutoPricing;
            var isSpecialProduct = useStorePriceProjection
                ? storePrice!.IsSpecialProduct
                : local.IsSpecialProduct;
            var isActive = useStorePriceProjection ? storePrice!.IsActive : local.IsActive;
            var now = DateTime.Now;
            var resolution = ResolveStoreMultiCodeRows(
                existingHqRows,
                multiCode,
                local.UUID,
                local.MultiBarcode,
                NormalizeOptional(local.StoreMultiCodeProductCode) ?? storeCode + multiCode
            );
            if (resolution.IsAmbiguous)
            {
                return AmbiguousMultiCodeResult();
            }
            if (resolution.Rows.Count > 0)
            {
                foreach (var existing in resolution.Rows)
                {
                    await hqDb.Updateable<DIC_分店一品多码表>()
                        .SetColumns(item => new DIC_分店一品多码表
                        {
                            H主条形码 = NormalizeOptional(product.Barcode) ?? string.Empty,
                            H多条形码 = NormalizeOptional(local.MultiBarcode) ?? string.Empty,
                            H进货价 = purchasePrice ?? 0,
                            H折扣率 = discountRate ?? 0,
                            H一品多码零售价 = retailPrice ?? 0,
                            H是否自动定价 = isAutoPricing,
                            H是否特殊商品 = isSpecialProduct,
                            H使用状态 = isActive,
                            FGC_LastModifier = "HBweb",
                            FGC_LastModifyDate = now,
                        })
                        // 兼容历史错码命中时保留 HQ 既有业务编码，仅按物理主键更新投影字段。
                        .Where(item => item.ID == existing.ID)
                        .ExecuteCommandAsync();
                }
                continue;
            }

            var row = new DIC_分店一品多码表
            {
                HGUID = NormalizeOptional(local.UUID) ?? Guid.NewGuid().ToString("N"),
                H分店代码 = storeCode,
                H商品编码 = productCode,
                H分店商品编码 = storeCode + productCode,
                H多码商品编码 = multiCode,
                H分店多码商品编码 = NormalizeOptional(local.StoreMultiCodeProductCode)
                    ?? storeCode + multiCode,
                H供应商编码 = supplierCode,
                H主条形码 = NormalizeOptional(product.Barcode) ?? string.Empty,
                H多条形码 = NormalizeOptional(local.MultiBarcode) ?? string.Empty,
                H进货价 = purchasePrice ?? 0,
                H折扣率 = discountRate ?? 0,
                H一品多码零售价 = retailPrice ?? 0,
                H库存 = 0,
                H库存金额 = 0,
                H自动新价格 = 0,
                H库存预警数 = 0,
                H是否缺货状态 = false,
                H最小订货量 = 0,
                H最小订货量合计金额 = 0,
                H活动类型 = string.Empty,
                H满减活动代码 = string.Empty,
                H满减数量 = 0,
                H满减金额 = 0,
                H是否自动定价 = isAutoPricing,
                H是否特殊商品 = isSpecialProduct,
                H商品柜组号 = string.Empty,
                H使用状态 = isActive,
                H动态销售数量 = 0,
                H动态销售额 = 0,
                H动态成本 = 0,
                H动态毛利 = 0,
                H动态毛利率 = 0,
                H动态销售占比 = 0,
                FGC_Creator = "HBweb",
                FGC_CreateDate = now,
                FGC_LastModifier = "HBweb",
                FGC_LastModifyDate = now,
            };
            row.ID = await hqDb.Insertable(row)
                .IgnoreColumns(item => item.ID)
                .ExecuteReturnIdentityAsync();
            existingHqRows.Add(row);
        }
        return null;
    }

    private async Task<ProductHqSyncOutboxExecutionResult?> ApplyTombstonesAsync(
        string productCode,
        IReadOnlyList<ProductHqSyncOutboxTombstoneDto> tombstones,
        ISqlSugarClient hqDb,
        bool writeChanges = true
    )
    {
        foreach (var tombstone in tombstones)
        {
            var businessKey = tombstone.BusinessKey.Trim();
            if (
                string.Equals(
                    tombstone.ResourceKind,
                    ProductMaintenanceHqResourceKinds.ProductSetCode,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                var activeLocalSetCodes = await _localContext.Db.Queryable<ProductSetCode>()
                    .Where(item =>
                        item.ProductCode == productCode && !item.IsDeleted && item.IsActive
                    )
                    .ToListAsync();
                var restoredSetCodes = activeLocalSetCodes
                    .Where(item =>
                        CodeEquals(item.SetProductCode, businessKey)
                        || CodeEquals(item.SetCodeId, businessKey)
                    )
                    .ToList();
                if (restoredSetCodes.Count > 1)
                {
                    return AmbiguousMultiCodeResult();
                }
                if (restoredSetCodes.Count == 1)
                {
                    // 旧任务重试时以当前已恢复的业务键为准，不能让历史软删行覆盖新记录。
                    continue;
                }
                var localSetResolution = await ResolveLocalSetCodeAsync(
                    productCode,
                    businessKey
                );
                if (localSetResolution.IsAmbiguous)
                {
                    return AmbiguousMultiCodeResult();
                }
                var localSetCode = localSetResolution.Rows.SingleOrDefault();
                if (localSetCode is { IsDeleted: false, IsActive: true })
                {
                    continue;
                }

                var globalRows = await hqDb.Queryable<DIC_一品多码表>()
                    .Where(item => item.H商品编码 == productCode)
                    .ToListAsync();
                var globalResolution = ResolveProductSetCodeRows(
                    globalRows,
                    businessKey,
                    localSetCode
                );
                if (globalResolution.IsAmbiguous)
                {
                    return AmbiguousMultiCodeResult();
                }

                var localStoreRows = await _localContext.Db.Queryable<StoreMultiCodeProduct>()
                    .Where(item => item.ProductCode == productCode)
                    .ToListAsync();
                localStoreRows = localStoreRows
                    .Where(item =>
                        CodeEquals(item.MultiCodeProductCode, businessKey)
                        || CodeEquals(item.MultiCodeProductCode, localSetCode?.SetProductCode)
                        || CodeEquals(item.MultiBarcode, localSetCode?.SetBarcode)
                    )
                    .ToList();
                var storeRows = await hqDb.Queryable<DIC_分店一品多码表>()
                    .Where(item => item.H商品编码 == productCode)
                    .ToListAsync();
                var storeRowsToDisable = new List<DIC_分店一品多码表>();
                foreach (
                    var storeGroup in storeRows.GroupBy(
                        item => NormalizeOptional(item.H分店代码) ?? string.Empty,
                        StringComparer.OrdinalIgnoreCase
                    )
                )
                {
                    var storeCode = storeGroup.Key;
                    var resolution = ResolveProductSetStoreMultiCodeRows(
                        storeGroup.ToList(),
                        storeCode,
                        businessKey,
                        localSetCode,
                        localStoreRows.Where(item => CodeEquals(item.StoreCode, storeCode)).ToList()
                    );
                    if (resolution.IsAmbiguous)
                    {
                        return AmbiguousMultiCodeResult();
                    }
                    storeRowsToDisable.AddRange(resolution.Rows);
                }

                if (writeChanges)
                {
                    await DisableProductSetRowsAsync(hqDb, globalResolution.Rows);
                    await DisableStoreMultiCodeRowsAsync(hqDb, storeRowsToDisable);
                }
                continue;
            }

            if (
                string.Equals(
                    tombstone.ResourceKind,
                    ProductMaintenanceHqResourceKinds.StoreMultiCode,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                var storeCode = NormalizeOptional(tombstone.StoreCode);
                if (storeCode == null)
                {
                    // 分店资源必须带精确门店；空值不能被解释成全店范围。
                    continue;
                }
                var localStoreRows = await _localContext.Db.Queryable<StoreMultiCodeProduct>()
                    .Where(item => item.ProductCode == productCode && item.StoreCode == storeCode)
                    .ToListAsync();
                var restoredStoreRows = localStoreRows
                    .Where(item =>
                        !item.IsDeleted
                        && item.IsActive
                        && (
                            CodeEquals(item.MultiCodeProductCode, businessKey)
                            || CodeEquals(item.UUID, businessKey)
                            || CodeEquals(item.StoreMultiCodeProductCode, businessKey)
                        )
                    )
                    .ToList();
                if (restoredStoreRows.Count > 1)
                {
                    return AmbiguousMultiCodeResult();
                }
                if (restoredStoreRows.Count == 1)
                {
                    continue;
                }
                var localMatches = localStoreRows
                    .Where(item =>
                        CodeEquals(item.MultiCodeProductCode, businessKey)
                        || CodeEquals(item.UUID, businessKey)
                        || CodeEquals(item.MultiBarcode, businessKey)
                        || CodeEquals(item.StoreMultiCodeProductCode, businessKey)
                    )
                    .ToList();
                if (localMatches.Select(item => item.UUID).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                {
                    return AmbiguousMultiCodeResult();
                }
                var localStoreRow = localMatches.SingleOrDefault();
                var localSetResolution = await ResolveLocalSetCodeAsync(
                    productCode,
                    businessKey
                );
                if (localSetResolution.IsAmbiguous)
                {
                    return AmbiguousMultiCodeResult();
                }
                var localSetCode = localSetResolution.Rows.SingleOrDefault();
                var hqRows = await hqDb.Queryable<DIC_分店一品多码表>()
                    .Where(item =>
                        item.H商品编码 == productCode && item.H分店代码 == storeCode
                    )
                    .ToListAsync();
                var resolution = ResolveProductSetStoreMultiCodeRows(
                    hqRows,
                    storeCode,
                    businessKey,
                    localSetCode,
                    localStoreRow == null
                        ? Array.Empty<StoreMultiCodeProduct>()
                        : new[] { localStoreRow }
                );
                if (resolution.IsAmbiguous)
                {
                    return AmbiguousMultiCodeResult();
                }
                if (writeChanges)
                {
                    await DisableStoreMultiCodeRowsAsync(hqDb, resolution.Rows);
                }
                continue;
            }

            if (
                string.Equals(
                    tombstone.ResourceKind,
                    ProductMaintenanceHqResourceKinds.StoreClearancePrice,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                var storeCode = NormalizeOptional(tombstone.StoreCode);
                if (storeCode == null)
                {
                    continue;
                }
                var restored = await _localContext.Db.Queryable<StoreClearancePrice>()
                    .AnyAsync(item =>
                        item.ProductCode == productCode
                        && item.StoreCode == storeCode
                        && !item.IsDeleted
                        && item.ClearancePrice != null
                    );
                if (restored)
                {
                    continue;
                }
                if (writeChanges)
                {
                    var delete = hqDb.Deleteable<DIC_商品清货价表>()
                        .Where(item => item.商品编码 == productCode);
                    delete = delete.Where(item => item.分店代码 == storeCode);
                    await delete.ExecuteCommandAsync();
                }
            }
        }

        return null;
    }

    private async Task<ProductHqSyncOutboxExecutionResult?> ValidateProductSetCodeIdentitiesAsync(
        string productCode,
        ISqlSugarClient hqDb
    )
    {
        var localRows = await _localContext.Db.Queryable<ProductSetCode>()
            .Where(item => item.ProductCode == productCode && !item.IsDeleted)
            .ToListAsync();
        var hqRows = await hqDb.Queryable<DIC_一品多码表>()
            .Where(item => item.H商品编码 == productCode)
            .ToListAsync();
        var ownerByHqId = new Dictionary<int, string>();
        foreach (var local in localRows)
        {
            var resolution = ResolveProductSetCodeRows(
                hqRows,
                local.SetProductCode,
                local
            );
            if (resolution.IsAmbiguous)
            {
                return AmbiguousMultiCodeResult();
            }
            foreach (var row in resolution.Rows)
            {
                if (
                    ownerByHqId.TryGetValue(row.ID, out var owner)
                    && !CodeEquals(owner, local.SetProductCode)
                )
                {
                    return AmbiguousMultiCodeResult();
                }
                ownerByHqId[row.ID] = local.SetProductCode;
            }
        }
        return null;
    }

    private async Task<ProductHqSyncOutboxExecutionResult?> ValidateStoreMultiCodeIdentitiesAsync(
        string productCode,
        IReadOnlyList<string>? storeCodes,
        ISqlSugarClient hqDb
    )
    {
        var storeValidation = ValidateStoreScope(storeCodes);
        if (storeValidation != null)
        {
            return storeValidation;
        }
        var ownerByHqId = new Dictionary<int, string>();
        foreach (var storeCode in NormalizeStoreCodes(storeCodes)!)
        {
            var localRows = await _localContext.Db.Queryable<StoreMultiCodeProduct>()
                .Where(item =>
                    item.ProductCode == productCode
                    && item.StoreCode == storeCode
                    && !item.IsDeleted
                )
                .ToListAsync();
            var hqRows = await hqDb.Queryable<DIC_分店一品多码表>()
                .Where(item => item.H商品编码 == productCode && item.H分店代码 == storeCode)
                .ToListAsync();
            foreach (var local in localRows.Where(item =>
                !string.IsNullOrWhiteSpace(item.MultiCodeProductCode)
            ))
            {
                var businessKey = local.MultiCodeProductCode!.Trim();
                var resolution = ResolveStoreMultiCodeRows(
                    hqRows,
                    businessKey,
                    local.UUID,
                    local.MultiBarcode,
                    NormalizeOptional(local.StoreMultiCodeProductCode) ?? storeCode + businessKey
                );
                if (resolution.IsAmbiguous)
                {
                    return AmbiguousMultiCodeResult();
                }
                foreach (var row in resolution.Rows)
                {
                    var ownerKey = $"{storeCode}|{businessKey}";
                    if (
                        ownerByHqId.TryGetValue(row.ID, out var owner)
                        && !string.Equals(owner, ownerKey, StringComparison.OrdinalIgnoreCase)
                    )
                    {
                        return AmbiguousMultiCodeResult();
                    }
                    ownerByHqId[row.ID] = ownerKey;
                }
            }
        }
        return null;
    }

    private async Task<IdentityResolution<ProductSetCode>> ResolveLocalSetCodeAsync(
        string productCode,
        string businessKey
    )
    {
        var rows = await _localContext.Db.Queryable<ProductSetCode>()
            .Where(item => item.ProductCode == productCode)
            .ToListAsync();
        var matches = rows
            .Where(item =>
                CodeEquals(item.SetProductCode, businessKey)
                || CodeEquals(item.SetCodeId, businessKey)
                || CodeEquals(item.SetItemNumber, businessKey)
                || CodeEquals(item.SetBarcode, businessKey)
            )
            .ToList();
        return matches.Select(item => item.SetCodeId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count()
            > 1
            ? IdentityResolution<ProductSetCode>.Ambiguous()
            : IdentityResolution<ProductSetCode>.Matched(matches);
    }

    private static IdentityResolution<DIC_分店一品多码表> ResolveStoreMultiCodeRows(
        IReadOnlyList<DIC_分店一品多码表> rows,
        string businessKey,
        string? guid,
        string? barcode,
        string? storeMultiProductKey
    ) => ResolveIdentityRows(
        rows,
        item => item.ID,
        new IdentitySignal<DIC_分店一品多码表>(item => item.H多码商品编码, businessKey),
        new IdentitySignal<DIC_分店一品多码表>(item => item.HGUID, guid),
        new IdentitySignal<DIC_分店一品多码表>(item => item.H多条形码, barcode),
        new IdentitySignal<DIC_分店一品多码表>(
            item => item.H分店多码商品编码,
            storeMultiProductKey
        )
    );

    private static IdentityResolution<DIC_一品多码表> ResolveProductSetCodeRows(
        IReadOnlyList<DIC_一品多码表> rows,
        string businessKey,
        ProductSetCode? local
    ) => ResolveIdentityRows(
        rows,
        item => item.ID,
        new IdentitySignal<DIC_一品多码表>(item => item.H多码商品编号, businessKey),
        new IdentitySignal<DIC_一品多码表>(item => item.H多码商品编号, local?.SetProductCode),
        new IdentitySignal<DIC_一品多码表>(item => item.HGUID, local?.SetCodeId),
        new IdentitySignal<DIC_一品多码表>(item => item.H多条形码, local?.SetBarcode),
        new IdentitySignal<DIC_一品多码表>(item => item.H多码商品编号, local?.SetItemNumber)
    );

    private static IdentityResolution<DIC_分店一品多码表> ResolveProductSetStoreMultiCodeRows(
        IReadOnlyList<DIC_分店一品多码表> rows,
        string storeCode,
        string businessKey,
        ProductSetCode? localSetCode,
        IReadOnlyList<StoreMultiCodeProduct> localStoreRows
    )
    {
        var signals = new List<IdentitySignal<DIC_分店一品多码表>>
        {
            new(item => item.H多码商品编码, businessKey),
            new(item => item.H多码商品编码, localSetCode?.SetProductCode),
        };
        signals.AddRange(localStoreRows.Select(item =>
            new IdentitySignal<DIC_分店一品多码表>(row => row.HGUID, item.UUID)
        ));
        signals.Add(new IdentitySignal<DIC_分店一品多码表>(
            item => item.HGUID,
            localSetCode?.SetCodeId
        ));
        signals.AddRange(localStoreRows.Select(item =>
            new IdentitySignal<DIC_分店一品多码表>(row => row.H多条形码, item.MultiBarcode)
        ));
        signals.Add(new IdentitySignal<DIC_分店一品多码表>(
            item => item.H多条形码,
            localSetCode?.SetBarcode
        ));
        signals.AddRange(localStoreRows.Select(item =>
            new IdentitySignal<DIC_分店一品多码表>(
                row => row.H分店多码商品编码,
                item.StoreMultiCodeProductCode
            )
        ));
        signals.Add(new IdentitySignal<DIC_分店一品多码表>(
            item => item.H分店多码商品编码,
            storeCode + businessKey
        ));
        signals.Add(new IdentitySignal<DIC_分店一品多码表>(
            item => item.H分店多码商品编码,
            localSetCode == null ? null : storeCode + localSetCode.SetProductCode
        ));
        signals.Add(new IdentitySignal<DIC_分店一品多码表>(
            item => item.H分店多码商品编码,
            localSetCode == null ? null : storeCode + localSetCode.SetItemNumber
        ));
        signals.Add(new IdentitySignal<DIC_分店一品多码表>(
            item => item.H多码商品编码,
            localSetCode?.SetItemNumber
        ));
        return ResolveIdentityRows(rows, item => item.ID, signals.ToArray());
    }

    private static IdentityResolution<T> ResolveIdentityRows<T>(
        IReadOnlyList<T> rows,
        Func<T, int> idSelector,
        params IdentitySignal<T>[] signals
    )
    {
        List<T>? selected = null;
        HashSet<int>? selectedIds = null;
        foreach (var signal in signals)
        {
            var identity = NormalizeOptional(signal.Identity);
            if (identity == null)
            {
                continue;
            }
            var matches = rows
                .Where(item => CodeEquals(signal.ValueSelector(item), identity))
                .ToList();
            if (matches.Count == 0)
            {
                continue;
            }
            if (selected == null)
            {
                selected = matches;
                selectedIds = matches.Select(idSelector).ToHashSet();
                continue;
            }
            if (matches.Any(item => !selectedIds!.Contains(idSelector(item))))
            {
                return IdentityResolution<T>.Ambiguous();
            }
        }
        return IdentityResolution<T>.Matched(selected ?? new List<T>());
    }

    private static async Task DisableProductSetRowsAsync(
        ISqlSugarClient hqDb,
        IReadOnlyList<DIC_一品多码表> rows
    )
    {
        var ids = rows.Select(item => item.ID).Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }
        await hqDb.Updateable<DIC_一品多码表>()
            .SetColumns(item => item.H使用状态 == false)
            .Where(item => ids.Contains(item.ID))
            .ExecuteCommandAsync();
    }

    private static async Task DisableStoreMultiCodeRowsAsync(
        ISqlSugarClient hqDb,
        IReadOnlyList<DIC_分店一品多码表> rows
    )
    {
        var ids = rows.Select(item => item.ID).Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }
        await hqDb.Updateable<DIC_分店一品多码表>()
            .SetColumns(item => item.H使用状态 == false)
            .Where(item => ids.Contains(item.ID))
            .ExecuteCommandAsync();
    }

    private static ProductHqSyncOutboxExecutionResult AmbiguousMultiCodeResult() =>
        ProductHqSyncOutboxExecutionResult.Blocked(
            AmbiguousMultiCodeErrorCode,
            "HQ 多码身份存在歧义，请先修正数据"
        );

    private static bool CodeEquals(string? left, string? right)
    {
        var normalizedLeft = NormalizeOptional(left);
        var normalizedRight = NormalizeOptional(right);
        return normalizedLeft != null
            && normalizedRight != null
            && string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInvalidTombstone(
        ProductHqSyncOutboxTombstoneDto? tombstone,
        IReadOnlyList<ProductHqSyncOutboxTombstoneDto> allTombstones
    )
    {
        if (
            tombstone == null
            || string.IsNullOrWhiteSpace(tombstone.ResourceKind)
            || string.IsNullOrWhiteSpace(tombstone.BusinessKey)
            || !KnownResourceKinds.Contains(tombstone.ResourceKind)
        )
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(tombstone.StoreCode))
        {
            return false;
        }

        if (
            string.Equals(
                tombstone.ResourceKind,
                ProductMaintenanceHqResourceKinds.StoreClearancePrice,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return true;
        }

        if (
            string.Equals(
                tombstone.ResourceKind,
                ProductMaintenanceHqResourceKinds.StoreMultiCode,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            // 兼容旧任务中冗余的空门店墓碑：只有同一业务键的全局套装墓碑可安全覆盖它。
            return !allTombstones.Any(item =>
                item != null
                && string.Equals(
                    item.ResourceKind,
                    ProductMaintenanceHqResourceKinds.ProductSetCode,
                    StringComparison.OrdinalIgnoreCase
                )
                && CodeEquals(item.BusinessKey, tombstone.BusinessKey)
            );
        }

        return false;
    }

    private sealed record IdentitySignal<T>(Func<T, string?> ValueSelector, string? Identity);

    private sealed record IdentityResolution<T>(bool IsAmbiguous, IReadOnlyList<T> Rows)
    {
        public static IdentityResolution<T> Ambiguous() => new(true, Array.Empty<T>());

        public static IdentityResolution<T> Matched(IReadOnlyList<T> rows) => new(false, rows);
    }

    private static ProductHqSyncOutboxExecutionResult? ValidateStoreScope(
        IReadOnlyList<string>? storeCodes
    ) => storeCodes == null || storeCodes.Count == 0
        ? ProductHqSyncOutboxExecutionResult.Blocked(
            "PRODUCT_HQ_MUTATION_STORE_SCOPE_REQUIRED",
            "门店商品变更缺少目标分店"
        )
        : null;

    private static List<string>? NormalizeStoreCodes(IEnumerable<string>? storeCodes)
    {
        if (storeCodes == null)
        {
            return null;
        }

        return storeCodes
            .Select(NormalizeOptional)
            .Where(item => item != null)
            .Select(item => item!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeRequired(string? value, string parameterName) =>
        NormalizeOptional(value)
        ?? throw new ArgumentException($"{parameterName} 不能为空", parameterName);

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class ProductMaintenanceHqEnqueueException : Exception
{
    public ProductMaintenanceHqEnqueueException(string message)
        : base(message) { }
}
