using BlazorApp.Api.Data;
using BlazorApp.Api.Features.SupplyNotices;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Logging;
using SqlSugar;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BlazorApp.Api.Services
{
    /// <summary>
    /// 仓库商品批量管理服务实现
    /// 支持10W+数据量的高性能查询和批量操作
    /// </summary>
    public class WarehouseProductBatchService : IWarehouseProductBatchService
    {
        private readonly ISqlSugarClient _db;
        private readonly ILogger<WarehouseProductBatchService> _logger;
        private readonly IWarehouseProductChangeHistoryService _changeHistoryService;
        private readonly ICurrentUserService _currentUserService;

        public WarehouseProductBatchService(
            SqlSugarContext context,
            ILogger<WarehouseProductBatchService> logger,
            IWarehouseProductChangeHistoryService changeHistoryService,
            ICurrentUserService currentUserService)
        {
            _db = context.Db;
            _logger = logger;
            _changeHistoryService = changeHistoryService;
            _currentUserService = currentUserService;
        }

        #region 查询相关

        /// <summary>
        /// 根据过滤条件获取商品列表（分页）
        /// 优化：避免N+1查询，使用Include预加载关联数据
        /// </summary>
        public async Task<PagedResultDto<WarehouseProductBatchDto>> GetByFilterAsync(WarehouseProductBatchFilterDto filter)
        {
            try
            {
                // 没有「查看国内供应商名称」权限时，供应商名称这一支换成匹配不到的占位关键词。
                var supplierNameFilter = DomesticSupplierInfoMasking.GuardNameKeyword(
                    filter.LocalSupplierCode ?? string.Empty
                );
                // 构建查询条件 - 通过DomesticProduct获取供应商信息
                var query = _db.Queryable<WarehouseProduct>()
                    .LeftJoin<Product>((wp, p) => wp.ProductCode == p.ProductCode)
                    .LeftJoin<DomesticProduct>((wp, p, dp) => wp.ProductCode == dp.ProductCode)
                    .LeftJoin<ChinaSupplier>((wp, p, dp, cs) => dp.SupplierCode == cs.SupplierCode)
                    .WhereIF(!string.IsNullOrEmpty(filter.ProductCode),
                        (wp, p, dp, cs) => wp.ProductCode.Contains(filter.ProductCode!))
                    .WhereIF(!string.IsNullOrEmpty(filter.ProductName),
                        (wp, p, dp, cs) => p.ProductName != null &&
                                   (p.ProductName.Contains(filter.ProductName!) ||
                                    (p.EnglishName != null && p.EnglishName.Contains(filter.ProductName!))))
                    .WhereIF(!string.IsNullOrEmpty(filter.ItemNumber),
                        (wp, p, dp, cs) => p.ItemNumber != null && p.ItemNumber.Contains(filter.ItemNumber!))
                    .WhereIF(!string.IsNullOrEmpty(filter.LocalSupplierCode),
                        (wp, p, dp, cs) => (dp.SupplierCode != null && dp.SupplierCode.Contains(filter.LocalSupplierCode!)) ||
                                           (cs.SupplierCode != null && cs.SupplierCode.Contains(filter.LocalSupplierCode!)) ||
                                           (cs.SupplierName != null && cs.SupplierName.Contains(supplierNameFilter)))
                    .WhereIF(filter.DomesticPriceMin.HasValue,
                        (wp, p, dp, cs) => wp.DomesticPrice >= filter.DomesticPriceMin)
                    .WhereIF(filter.DomesticPriceMax.HasValue,
                        (wp, p, dp, cs) => wp.DomesticPrice <= filter.DomesticPriceMax)
                    .WhereIF(filter.OEMPriceMin.HasValue,
                        (wp, p, dp, cs) => wp.OEMPrice >= filter.OEMPriceMin)
                    .WhereIF(filter.OEMPriceMax.HasValue,
                        (wp, p, dp, cs) => wp.OEMPrice <= filter.OEMPriceMax)
                    .WhereIF(filter.ImportPriceMin.HasValue,
                        (wp, p, dp, cs) => wp.ImportPrice >= filter.ImportPriceMin)
                    .WhereIF(filter.ImportPriceMax.HasValue,
                        (wp, p, dp, cs) => wp.ImportPrice <= filter.ImportPriceMax)
                    .WhereIF(filter.StockQuantityMin.HasValue,
                        (wp, p, dp, cs) => wp.StockQuantity >= filter.StockQuantityMin)
                    .WhereIF(filter.StockQuantityMax.HasValue,
                        (wp, p, dp, cs) => wp.StockQuantity <= filter.StockQuantityMax)
                    .WhereIF(filter.IsActive.HasValue,
                        (wp, p, dp, cs) => wp.IsActive == filter.IsActive)
                    .WhereIF(filter.ProductCodes != null && filter.ProductCodes.Any(),
                        (wp, p, dp, cs) => filter.ProductCodes!.Contains(wp.ProductCode)); // 外部传入的商品集合

                // 仓位过滤（需要Join查询）
                if (!string.IsNullOrEmpty(filter.LocationCode))
                {
                    query = query.Where((wp, p, dp, cs) => SqlFunc.Subqueryable<ProductLocation>()
                        .InnerJoin<Location>((pl, l) => pl.LocationGuid == l.LocationGuid)
                        .Where((pl, l) => pl.ProductCode == wp.ProductCode && l.LocationCode!.Contains(filter.LocationCode))
                        .Any());
                }

                // 排序 - 如果未指定排序字段，默认按货号降序
                var sortField = string.IsNullOrEmpty(filter.SortField) ? "itemnumber" : filter.SortField.ToLower();
                var sortOrder = string.IsNullOrEmpty(filter.SortOrder) ? "desc" : filter.SortOrder;
                var isAsc = string.Equals(sortOrder, "asc", StringComparison.OrdinalIgnoreCase);

                // 对于货号排序，先按字符串排序（用于数据库查询），后面在内存中重新排序
                var isItemNumberSort = sortField == "itemnumber";

                // 使用Lambda表达式进行排序
                query = sortField switch
                {
                    "itemnumber" => isAsc ? query.OrderBy((wp, p, dp, cs) => p.ItemNumber) : query.OrderByDescending((wp, p, dp, cs) => p.ItemNumber),
                    "productcode" => isAsc ? query.OrderBy((wp, p, dp, cs) => wp.ProductCode) : query.OrderByDescending((wp, p, dp, cs) => wp.ProductCode),
                    "domesticprice" => isAsc ? query.OrderBy((wp, p, dp, cs) => wp.DomesticPrice) : query.OrderByDescending((wp, p, dp, cs) => wp.DomesticPrice),
                    "stockquantity" => isAsc ? query.OrderBy((wp, p, dp, cs) => wp.StockQuantity) : query.OrderByDescending((wp, p, dp, cs) => wp.StockQuantity),
                    "updatedat" => isAsc ? query.OrderBy((wp, p, dp, cs) => wp.UpdatedAt) : query.OrderByDescending((wp, p, dp, cs) => wp.UpdatedAt),
                    // 默认按货号排序
                    _ => query.OrderByDescending((wp, p, dp, cs) => p.ItemNumber)
                };

                // 分页查询 - 先查询商品数据
                var totalCount = await query.CountAsync();
                var pageIndex = filter.PageIndex < 1 ? 1 : filter.PageIndex;
                var pageSize = filter.PageSize < 1 ? 50 : filter.PageSize;

                // 先获取分页的ProductCode列表（用于后续批量查询）
                // 由于SqlSugar多表Join后Select有问题，我们使用SQL片段
                var sqlable = query.Skip((pageIndex - 1) * pageSize).Take(pageSize);
                var sql = sqlable.ToSql();

                var productCodesInPage = await _db.Ado.SqlQueryAsync<string>(
                    $"SELECT ProductCode FROM ({sql.Key}) AS t",
                    sql.Value
                );

                // 重新查询完整数据（包括Product和DomesticProduct）
                var warehouseProducts = await _db.Queryable<WarehouseProduct>()
                    .Includes(wp => wp.Product)
                    .Includes(wp => wp.DomesticProduct)
                    .Where(wp => productCodesInPage.Contains(wp.ProductCode))
                    .ToListAsync();

                // 批量查询供应商名称（通过DomesticProduct的SupplierCode）
                var supplierCodes = warehouseProducts
                    .Where(wp => wp.DomesticProduct != null && !string.IsNullOrEmpty(wp.DomesticProduct.SupplierCode))
                    .Select(wp => wp.DomesticProduct!.SupplierCode!)
                    .Distinct()
                    .ToList();

                var suppliers = await _db.Queryable<ChinaSupplier>()
                    .Where(cs => supplierCodes.Contains(cs.SupplierCode!))
                    .ToListAsync();

                var supplierDict = suppliers.ToDictionary(
                    cs => cs.SupplierCode!,
                    cs => cs.SupplierName
                );

                // 为每个商品关联供应商名称（通过DomesticProduct）
                var supplierNames = new Dictionary<string, string?>();
                foreach (var wp in warehouseProducts)
                {
                    if (wp.DomesticProduct != null && !string.IsNullOrEmpty(wp.DomesticProduct.SupplierCode))
                    {
                        if (supplierDict.TryGetValue(wp.DomesticProduct.SupplierCode, out var supplierName))
                        {
                            supplierNames[wp.ProductCode] = supplierName;
                        }
                    }
                }

                // 预加载Locations - 使用Join查询
                var productCodes = warehouseProducts.Select(wp => wp.ProductCode).ToList();
                if (productCodes.Any())
                {
                    var locationsData = await _db.Queryable<ProductLocation>()
                        .LeftJoin<Location>((pl, l) => pl.LocationGuid == l.LocationGuid)
                        .Where((pl, l) => pl.ProductCode != null && productCodes.Contains(pl.ProductCode))
                        .Select((pl, l) => new { pl.ProductCode, Location = l })
                        .ToListAsync();

                    var locationDict = locationsData
                        .GroupBy(x => x.ProductCode)
                        .Where(g => g.Key != null)
                        .ToDictionary(g => g.Key!, g => g.Select(x => x.Location).Where(l => l != null).ToList()!);

                    foreach (var wp in warehouseProducts)
                    {
                        if (locationDict.TryGetValue(wp.ProductCode, out var locs))
                        {
                            wp.Locations = locs;
                        }
                    }
                }

                // 映射到DTO，同时添加供应商名称
                var dtos = warehouseProducts.Select(wp =>
                {
                    var dto = MapToDto(wp);
                    if (supplierNames.TryGetValue(wp.ProductCode, out var supplierName))
                    {
                        dto.SupplierName = supplierName;
                    }
                    return dto;
                }).ToList();

                // 如果是按货号排序，在内存中重新按数字部分排序
                if (isItemNumberSort)
                {
                    dtos = isAsc
                        ? dtos.OrderBy(dto => ExtractItemNumberForSort(dto.ItemNumber)).ToList()
                        : dtos.OrderByDescending(dto => ExtractItemNumberForSort(dto.ItemNumber)).ToList();
                }

                return new PagedResultDto<WarehouseProductBatchDto>
                {
                    Data = dtos,
                    TotalCount = totalCount,
                    PageIndex = pageIndex,
                    PageSize = pageSize
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查询仓库商品失败，过滤条件: {Filter}", filter);
                throw;
            }
        }

        /// <summary>
        /// 获取所有可用仓位列表
        /// </summary>
        public async Task<List<LocationOptionDto>> GetAvailableLocationsAsync()
        {
            try
            {
                var locations = await _db.Queryable<Location>()
                    .Where(l => l.Status == 1) // 只查询启用的仓位
                    .OrderBy(l => l.LocationCode)
                    .ToListAsync();

                return locations.Select(l => new LocationOptionDto
                {
                    LocationGuid = l.LocationGuid,
                    LocationCode = l.LocationCode,
                    LocationBarcode = l.LocationBarcode,
                    LocationType = l.LocationType,
                    Status = l.Status
                }).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取仓位列表失败");
                throw;
            }
        }

        #endregion

        #region 批量更新

        /// <summary>
        /// 批量更新商品信息（使用事务）
        /// 支持乐观锁并发控制
        /// </summary>
        public async Task<BatchUpdateResult> BatchUpdateAsync(BatchUpdateRequest request)
        {
            var result = new BatchUpdateResult
            {
                DetailErrors = new Dictionary<string, string>(),
                FailedProductCodes = new List<string>()
            };

            try
            {
                var productCodes = request.Products
                    .Select(product => product.ProductCode)
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var batchGuid = Guid.NewGuid();
                var occurredAtUtc = DateTime.UtcNow;
                var actorName = ResolveActorName();
                // 使用事务确保数据一致性
                var updateResult = await _db.Ado.UseTranAsync(async () =>
                {
                    SetChildPurchasePriceLockScope? lockScope = null;
                    if (productCodes.Count > 0)
                    {
                        lockScope = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                            _db,
                            productCodes
                        );
                    }
                    var beforeSnapshots = await CaptureChangeSnapshotsAsync(productCodes);
                    int successCount = 0;
                    int failedCount = 0;
                    var processedProductCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var updatedCostProductCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var dto in request.Products)
                    {
                        var normalizedProductCode = dto.ProductCode?.Trim() ?? string.Empty;
                        var errorKey = normalizedProductCode.Length == 0 ? "（空商品编码）" : normalizedProductCode;
                        if (normalizedProductCode.Length == 0)
                        {
                            result.DetailErrors![errorKey] = "商品编码不能为空";
                            result.FailedProductCodes!.Add(errorKey);
                            failedCount++;
                            continue;
                        }
                        if (!processedProductCodes.Add(normalizedProductCode))
                        {
                            result.DetailErrors![normalizedProductCode] = "批次内商品编码重复";
                            result.FailedProductCodes!.Add(normalizedProductCode);
                            failedCount++;
                            continue;
                        }

                        try
                        {
                            // 查询当前数据（包含RowVersion）
                            var existing = await _db.Queryable<WarehouseProduct>()
                                .Where(wp =>
                                    wp.ProductCode == normalizedProductCode && !wp.IsDeleted
                                )
                                .FirstAsync();

                            if (existing == null)
                            {
                                result.DetailErrors![normalizedProductCode] = "商品不存在";
                                result.FailedProductCodes!.Add(normalizedProductCode);
                                failedCount++;
                                continue;
                            }


                            // 更新字段
                            existing.DomesticPrice = dto.DomesticPrice;
                            existing.OEMPrice = dto.OEMPrice;
                            existing.ImportPrice = dto.ImportPrice;
                            existing.StockQuantity = dto.StockQuantity;
                            existing.MinOrderQuantity = dto.MinOrderQuantity;
                            existing.StockAlertQuantity = dto.StockAlertQuantity;
                            existing.Volume = dto.Volume;
                            existing.IsActive = dto.IsActive;

                            // 自动计算库存金额 = 库存数量 × 进口价
                            if (existing.StockQuantity.HasValue && existing.ImportPrice.HasValue)
                            {
                                existing.StockValue = existing.StockQuantity.Value * existing.ImportPrice.Value;
                            }
                            else
                            {
                                existing.StockValue = null;
                            }

                            existing.UpdatedAt = occurredAtUtc;
                            existing.UpdatedBy = actorName;

                            // 更新数据（SqlSugar会自动处理RowVersion）
                            var affected = await _db.Updateable(existing)
                                .UpdateColumns(wp => new
                                {
                                    wp.DomesticPrice,
                                    wp.OEMPrice,
                                    wp.ImportPrice,
                                    wp.StockQuantity,
                                    wp.MinOrderQuantity,
                                    wp.StockAlertQuantity,
                                    wp.Volume,
                                    wp.IsActive,
                                    wp.StockValue,
                                    wp.UpdatedAt,
                                    wp.UpdatedBy,
                                })
                                .ExecuteCommandAsync();

                            if (affected > 0)
                            {
                                // 更新仓位关系（如果有变更）
                                if (dto.LocationGuid != null)
                                {
                                    await UpdateProductLocationAsync(normalizedProductCode, dto.LocationGuid, throwOnError: true);
                                }

                                successCount++;
                                updatedCostProductCodes.Add(normalizedProductCode);
                            }
                            else
                            {
                                result.DetailErrors![normalizedProductCode] = "更新失败";
                                result.FailedProductCodes!.Add(normalizedProductCode);
                                failedCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "更新商品{ProductCode}发生数据库异常，回滚整个批次", normalizedProductCode);
                            throw;
                        }
                    }

                    result.UpdatedCount = successCount;
                    result.FailedCount = failedCount;
                    result.Success = failedCount == 0;

                    if (lockScope != null)
                    {
                        await RecalculateSetChildPurchasePricesLockedAsync(
                            lockScope,
                            updatedCostProductCodes,
                            actorName
                        );
                    }

                    var afterSnapshots = await CaptureChangeSnapshotsAsync(productCodes);
                    await RecordChangeHistoryAsync(
                        beforeSnapshots,
                        afterSnapshots,
                        "BatchUpdate",
                        "LegacyWarehouseProductBatch",
                        batchGuid,
                        occurredAtUtc
                    );

                    return new { successCount, failedCount };
                });

                if (!updateResult.IsSuccess)
                {
                    throw updateResult.ErrorException
                        ?? new InvalidOperationException("批量商品更新事务失败");
                }

                _logger.LogInformation("批量更新完成：成功{Success}条，失败{Failed}条",
                    updateResult.Data.successCount, updateResult.Data.failedCount);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量更新事务失败");
                result.Success = false;
                result.UpdatedCount = 0;
                result.ErrorMessage = SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _)
                    ? $"{SetChildPurchasePriceMutationLock.BusyErrorCode}: 套装商品正在被其他操作修改，请稍后重试"
                    : "批量更新失败：" + ex.Message;
                return result;
            }
        }

        /// <summary>
        /// 增量保存（保存单条或部分修改）
        /// </summary>
        public async Task<IncrementalSaveResult> IncrementalSaveAsync(IncrementalSaveRequest request)
        {
            var result = new IncrementalSaveResult
            {
                UpdatedProducts = new List<WarehouseProductBatchDto>(),
                FailedDetails = new Dictionary<string, string>()
            };

            try
            {
                var productCodes = request.Products
                    .Select(product => product.ProductCode)
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var batchGuid = Guid.NewGuid();
                var occurredAtUtc = DateTime.UtcNow;
                var actorName = ResolveActorName();
                var transactionResult = await _db.Ado.UseTranAsync(async () =>
                {
                    SetChildPurchasePriceLockScope? lockScope = null;
                    if (productCodes.Count > 0)
                    {
                        lockScope = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                            _db,
                            productCodes
                        );
                    }
                    var beforeSnapshots = await CaptureChangeSnapshotsAsync(productCodes);
                    var processedProductCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var updatedCostProductCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var dto in request.Products)
                    {
                        var normalizedProductCode = dto.ProductCode?.Trim() ?? string.Empty;
                        var errorKey = normalizedProductCode.Length == 0 ? "（空商品编码）" : normalizedProductCode;
                        if (normalizedProductCode.Length == 0)
                        {
                            result.FailedDetails![errorKey] = "商品编码不能为空";
                            result.FailedCount++;
                            continue;
                        }
                        if (!processedProductCodes.Add(normalizedProductCode))
                        {
                            result.FailedDetails![errorKey] = "批次内商品编码重复";
                            result.FailedCount++;
                            continue;
                        }

                        try
                        {
                            var existing = await _db.Queryable<WarehouseProduct>()
                                .Includes(wp => wp.Product)
                                .Includes(wp => wp.Locations)
                                .Where(wp =>
                                    wp.ProductCode == normalizedProductCode && !wp.IsDeleted
                                )
                                .FirstAsync();

                            if (existing == null)
                            {
                                result.FailedDetails![errorKey] = "商品不存在";
                                result.FailedCount++;
                                continue;
                            }


                            // 更新字段
                            existing.DomesticPrice = dto.DomesticPrice;
                            existing.OEMPrice = dto.OEMPrice;
                            existing.ImportPrice = dto.ImportPrice;
                            existing.StockQuantity = dto.StockQuantity;
                            existing.MinOrderQuantity = dto.MinOrderQuantity;
                            existing.StockAlertQuantity = dto.StockAlertQuantity;
                            existing.Volume = dto.Volume;
                            existing.IsActive = dto.IsActive;

                            // 计算库存金额
                            if (existing.StockQuantity.HasValue && existing.ImportPrice.HasValue)
                            {
                                existing.StockValue = existing.StockQuantity.Value * existing.ImportPrice.Value;
                            }

                            existing.UpdatedAt = occurredAtUtc;
                            existing.UpdatedBy = actorName;

                            await _db.Updateable(existing)
                                .UpdateColumns(wp => new
                                {
                                    wp.DomesticPrice,
                                    wp.OEMPrice,
                                    wp.ImportPrice,
                                    wp.StockQuantity,
                                    wp.MinOrderQuantity,
                                    wp.StockAlertQuantity,
                                    wp.Volume,
                                    wp.IsActive,
                                    wp.StockValue,
                                    wp.UpdatedAt,
                                    wp.UpdatedBy,
                                })
                                .ExecuteCommandAsync();

                            // 更新仓位
                            if (dto.LocationGuid != null)
                            {
                                await UpdateProductLocationAsync(normalizedProductCode, dto.LocationGuid, throwOnError: true);
                            }

                            // 重新查询获取新的RowVersion
                            var updated = await _db.Queryable<WarehouseProduct>()
                                .Includes(wp => wp.Product)
                                .Includes(wp => wp.Locations)
                                .Where(wp => wp.ProductCode == normalizedProductCode)
                                .FirstAsync();

                            result.UpdatedProducts!.Add(MapToDto(updated));
                            result.SavedCount++;
                            updatedCostProductCodes.Add(normalizedProductCode);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "增量保存商品{ProductCode}发生数据库异常，回滚整个批次", normalizedProductCode);
                            throw;
                        }
                    }

                    result.Success = result.FailedCount == 0;

                    if (lockScope != null)
                    {
                        await RecalculateSetChildPurchasePricesLockedAsync(
                            lockScope,
                            updatedCostProductCodes,
                            actorName
                        );
                    }

                    var afterSnapshots = await CaptureChangeSnapshotsAsync(productCodes);
                    await RecordChangeHistoryAsync(
                        beforeSnapshots,
                        afterSnapshots,
                        "BatchUpdate",
                        "LegacyWarehouseProductBatch",
                        batchGuid,
                        occurredAtUtc
                    );
                });

                if (!transactionResult.IsSuccess)
                {
                    throw transactionResult.ErrorException
                        ?? new InvalidOperationException("增量保存事务失败");
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "增量保存失败");
                result.Success = false;
                result.SavedCount = 0;
                result.UpdatedProducts?.Clear();
                result.ErrorMessage = SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _)
                    ? $"{SetChildPurchasePriceMutationLock.BusyErrorCode}: 套装商品正在被其他操作修改，请稍后重试"
                    : "增量保存失败：" + ex.Message;
                return result;
            }
        }

        #endregion

        #region 批量操作

        /// <summary>
        /// 批量设置价格
        /// </summary>
        public async Task<BulkOperationResult> BulkSetPriceAsync(BulkSetPriceRequest request)
        {
            var result = new BulkOperationResult();

            try
            {
                var productCodes = request.ProductCodes
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var batchGuid = Guid.NewGuid();
                var occurredAtUtc = DateTime.UtcNow;
                var actorName = ResolveActorName();
                var transactionResult = await _db.Ado.UseTranAsync(async () =>
                {
                    SetChildPurchasePriceLockScope? lockScope = null;
                    if (
                        request.PriceType.Equals("IMPORT", StringComparison.OrdinalIgnoreCase)
                        && productCodes.Count > 0
                    )
                    {
                        lockScope = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                            _db,
                            productCodes
                        );
                    }
                    var beforeSnapshots = await CaptureChangeSnapshotsAsync(productCodes);
                    // 使用UpdateColumns只更新特定字段，避免更新所有字段
                    int affected = 0;

                    switch (request.PriceType.ToUpper())
                    {
                        case "DOMESTIC":
                            affected = await _db.Updateable<WarehouseProduct>()
                                .SetColumns(wp => wp.DomesticPrice == request.Price)
                                .SetColumns(wp => wp.UpdatedAt == occurredAtUtc)
                                .SetColumns(wp => wp.UpdatedBy == actorName)
                                .Where(wp =>
                                    productCodes.Contains(wp.ProductCode) && !wp.IsDeleted
                                )
                                .ExecuteCommandAsync();
                            break;

                        case "OEM":
                            affected = await _db.Updateable<WarehouseProduct>()
                                .SetColumns(wp => wp.OEMPrice == request.Price)
                                .SetColumns(wp => wp.UpdatedAt == occurredAtUtc)
                                .SetColumns(wp => wp.UpdatedBy == actorName)
                                .Where(wp =>
                                    productCodes.Contains(wp.ProductCode) && !wp.IsDeleted
                                )
                                .ExecuteCommandAsync();
                            break;

                        case "IMPORT":
                            // 进口价变更需要重新计算库存金额
                            var products = await _db.Queryable<WarehouseProduct>()
                                .Where(wp =>
                                    productCodes.Contains(wp.ProductCode) && !wp.IsDeleted
                                )
                                .ToListAsync();

                            foreach (var product in products)
                            {
                                product.ImportPrice = request.Price;
                                if (product.StockQuantity.HasValue)
                                {
                                    product.StockValue = product.StockQuantity.Value * request.Price;
                                }
                                product.UpdatedAt = occurredAtUtc;
                                product.UpdatedBy = actorName;
                            }

                            affected = await _db.Updateable(products)
                                .UpdateColumns(wp => new
                                {
                                    wp.ImportPrice,
                                    wp.StockValue,
                                    wp.UpdatedAt,
                                    wp.UpdatedBy,
                                })
                                .ExecuteCommandAsync();
                            break;

                        default:
                            throw new ArgumentException($"不支持的价格类型: {request.PriceType}");
                    }

                    result.AffectedCount = affected;
                    result.Success = affected > 0;

                    if (lockScope != null)
                    {
                        await RecalculateSetChildPurchasePricesLockedAsync(
                            lockScope,
                            productCodes,
                            actorName
                        );
                    }

                    var afterSnapshots = await CaptureChangeSnapshotsAsync(productCodes);
                    await RecordChangeHistoryAsync(
                        beforeSnapshots,
                        afterSnapshots,
                        "BatchUpdate",
                        "LegacyWarehouseProductBatch",
                        batchGuid,
                        occurredAtUtc
                    );

                    _logger.LogInformation("批量设置{PriceType}价格为{Price}，影响{Count}条",
                        request.PriceType, request.Price, affected);
                });

                if (!transactionResult.IsSuccess)
                {
                    throw transactionResult.ErrorException
                        ?? new InvalidOperationException("批量设置价格事务失败");
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量设置价格失败");
                result.Success = false;
                result.AffectedCount = 0;
                result.ErrorMessage = SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _)
                    ? $"{SetChildPurchasePriceMutationLock.BusyErrorCode}: 套装商品正在被其他操作修改，请稍后重试"
                    : ex.Message;
                return result;
            }
        }

        /// <summary>
        /// 批量调整库存
        /// </summary>
        public async Task<BulkOperationResult> BulkAdjustStockAsync(BulkAdjustStockRequest request)
        {
            var result = new BulkOperationResult();

            try
            {
                await _db.Ado.UseTranAsync(async () =>
                {
                    var products = await _db.Queryable<WarehouseProduct>()
                        .Where(wp => request.ProductCodes.Contains(wp.ProductCode))
                        .ToListAsync();

                    foreach (var product in products)
                    {
                        switch (request.AdjustType.ToUpper())
                        {
                            case "SET":
                                product.StockQuantity = request.Quantity;
                                break;

                            case "ADD":
                                product.StockQuantity = (product.StockQuantity ?? 0) + request.Quantity;
                                break;

                            case "SUBTRACT":
                                product.StockQuantity = Math.Max(0, (product.StockQuantity ?? 0) - request.Quantity);
                                break;

                            default:
                                throw new ArgumentException($"不支持的调整类型: {request.AdjustType}");
                        }

                        // 重新计算库存金额
                        if (product.StockQuantity.HasValue && product.ImportPrice.HasValue)
                        {
                            product.StockValue = product.StockQuantity.Value * product.ImportPrice.Value;
                        }

                        product.UpdatedAt = DateTime.Now;
                    }

                    var affected = await _db.Updateable(products).ExecuteCommandAsync();
                    result.AffectedCount = affected;
                    result.Success = affected > 0;

                    _logger.LogInformation("批量调整库存{AdjustType} {Quantity}，影响{Count}条",
                        request.AdjustType, request.Quantity, affected);
                });

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量调整库存失败");
                result.Success = false;
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        /// <summary>
        /// 批量设置使用状态
        /// </summary>
        public async Task<BulkOperationResult> BulkSetStatusAsync(BulkSetStatusRequest request)
        {
            var result = new BulkOperationResult();

            try
            {
                var productCodes = request.ProductCodes
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var batchGuid = Guid.NewGuid();
                var occurredAtUtc = DateTime.UtcNow;
                var actorName = ResolveActorName();
                var transactionResult = await _db.Ado.UseTranAsync(async () =>
                {
                    var beforeSnapshots = await CaptureChangeSnapshotsAsync(productCodes);
                    var affected = await _db.Updateable<WarehouseProduct>()
                        .SetColumns(wp => wp.IsActive == request.IsActive)
                        .SetColumns(wp => wp.UpdatedAt == occurredAtUtc)
                        .SetColumns(wp => wp.UpdatedBy == actorName)
                        .Where(wp => productCodes.Contains(wp.ProductCode))
                        .ExecuteCommandAsync();

                    result.AffectedCount = affected;
                    result.Success = affected > 0;

                    if (request.IsActive)
                    {
                        // 旧入口没有录入界面：只在上架时关闭供货说明，下架不登记。
                        await WarehouseProductSupplyNoticeWriter.CloseNoticesForActiveProductsAsync(
                            _db,
                            productCodes,
                            actorName,
                            occurredAtUtc
                        );
                    }

                    var afterSnapshots = await CaptureChangeSnapshotsAsync(productCodes);
                    await RecordChangeHistoryAsync(
                        beforeSnapshots,
                        afterSnapshots,
                        "BatchUpdate",
                        "LegacyWarehouseProductBatch",
                        batchGuid,
                        occurredAtUtc
                    );

                    _logger.LogInformation("批量设置状态为{Status}，影响{Count}条", request.IsActive, affected);
                });

                if (!transactionResult.IsSuccess)
                {
                    throw transactionResult.ErrorException
                        ?? new InvalidOperationException("批量设置状态事务失败");
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量设置状态失败");
                result.Success = false;
                result.AffectedCount = 0;
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        /// <summary>
        /// 批量设置仓位
        /// </summary>
        public async Task<BulkOperationResult> BulkSetLocationAsync(BulkSetLocationRequest request)
        {
            var result = new BulkOperationResult();

            try
            {
                await _db.Ado.UseTranAsync(async () =>
                {
                    int count = 0;
                    foreach (var productCode in request.ProductCodes)
                    {
                        var success = await UpdateProductLocationAsync(productCode, request.LocationGuid);
                        if (success) count++;
                    }

                    result.AffectedCount = count;
                    result.Success = count > 0;

                    _logger.LogInformation("批量设置仓位{LocationGuid}，影响{Count}条",
                        request.LocationGuid, count);
                });

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量设置仓位失败");
                result.Success = false;
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        /// <summary>
        /// 更新单个商品的仓位信息
        /// </summary>
        public async Task<bool> UpdateLocationAsync(LocationEditDto request)
        {
            try
            {
                return await UpdateProductLocationAsync(request.ProductCode, request.LocationGuid);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新商品{ProductCode}的仓位失败", request.ProductCode);
                return false;
            }
        }

        #endregion

        #region 导出功能

        /// <summary>
        /// 导出为Excel（使用EPPlus或NPOI）
        /// </summary>
        public Task<byte[]> ExportToExcelAsync(WarehouseProductBatchFilterDto filter)
        {
            // TODO: 实现Excel导出
            // 需要安装 EPPlus 或 NPOI NuGet包
            throw new NotImplementedException("Excel导出功能待实现");
        }

        /// <summary>
        /// 导出为PDF（使用QuestPDF或iTextSharp）
        /// </summary>
        public Task<byte[]> ExportToPdfAsync(WarehouseProductBatchFilterDto filter)
        {
            // TODO: 实现PDF导出
            // 需要安装 QuestPDF 或 iTextSharp NuGet包
            throw new NotImplementedException("PDF导出功能待实现");
        }

        #endregion

        #region 私有辅助方法

        /// <summary>
        /// 映射实体到DTO
        /// </summary>
        private WarehouseProductBatchDto MapToDto(WarehouseProduct wp)
        {
            var dto = new WarehouseProductBatchDto
            {
                ProductCode = wp.ProductCode,
                DomesticPrice = wp.DomesticPrice,
                OEMPrice = wp.OEMPrice,
                ImportPrice = wp.ImportPrice,
                StockQuantity = wp.StockQuantity,
                MinOrderQuantity = wp.MinOrderQuantity,
                StockValue = wp.StockValue,
                StockAlertQuantity = wp.StockAlertQuantity,
                Volume = wp.Volume,
                IsActive = wp.IsActive,
                CreatedAt = wp.CreatedAt,
                UpdatedAt = wp.UpdatedAt
            };

            // 填充Product信息
            if (wp.Product != null)
            {
                dto.ProductName = wp.Product.ProductName;
                dto.ItemNumber = wp.Product.ItemNumber;
                dto.LocalSupplierCode = wp.Product.LocalSupplierCode;

                // 使用ProductImage字段，如果为空则使用腾讯云COS URL + 货号.jpg
                if (!string.IsNullOrWhiteSpace(wp.Product.ProductImage))
                {
                    dto.ImageUrl = wp.Product.ProductImage;
                }
                else if (!string.IsNullOrWhiteSpace(wp.Product.ItemNumber))
                {
                    // 使用腾讯云COS URL + 货号.jpg
                    dto.ImageUrl = $"http://hotbargain-yw-2023-1300114625.cos.ap-shanghai.myqcloud.com/{wp.Product.ItemNumber}.jpg";
                }
                else
                {
                    // 如果货号也为空，则设置为null（前端会显示占位符图标）
                    dto.ImageUrl = null;
                }
            }

            // 填充Location信息（UI限制只显示第一个）
            if (wp.Locations != null && wp.Locations.Any())
            {
                var firstLocation = wp.Locations.First();
                dto.LocationGuid = firstLocation.LocationGuid;
                dto.LocationCode = firstLocation.LocationCode;
                dto.LocationBarcode = firstLocation.LocationBarcode;
            }

            return dto;
        }

        /// <summary>
        /// 更新商品的仓位关系（业务规则：只能1个仓位）
        /// </summary>
        private async Task<bool> UpdateProductLocationAsync(
            string productCode,
            string? locationGuid,
            bool throwOnError = false
        )
        {
            try
            {
                // 删除现有的所有仓位关系
                await _db.Deleteable<ProductLocation>()
                    .Where(pl => pl.ProductCode == productCode)
                    .ExecuteCommandAsync();

                // 如果指定了新仓位，则创建新关系
                if (!string.IsNullOrEmpty(locationGuid))
                {
                    var newRelation = new ProductLocation
                    {
                        Guid = Guid.NewGuid().ToString(),
                        ProductCode = productCode,
                        LocationGuid = locationGuid,
                        CreatedAt = DateTime.Now,
                        UpdatedAt = DateTime.Now
                    };

                    await _db.Insertable(newRelation).ExecuteCommandAsync();
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新商品{ProductCode}的仓位关系失败", productCode);
                if (throwOnError)
                {
                    throw;
                }
                return false;
            }
        }

        private async Task<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>
            CaptureChangeSnapshotsAsync(IEnumerable<string> productCodes)
        {
            return await _changeHistoryService.CaptureSnapshotsAsync(productCodes);
        }

        private async Task RecordChangeHistoryAsync(
            IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto> beforeSnapshots,
            IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto> afterSnapshots,
            string action,
            string source,
            Guid batchGuid,
            DateTime? occurredAtUtc = null
        )
        {
            await _changeHistoryService.RecordChangesAsync(
                beforeSnapshots,
                afterSnapshots,
                new WarehouseProductChangeHistoryContextDto
                {
                    Action = action,
                    Source = source,
                    BatchGuid = batchGuid,
                    ActorName = ResolveActorName(),
                    ActorType = ResolveActorType(),
                    ActorUserGuid = ResolveActorGuid(),
                    OccurredAtUtc = occurredAtUtc ?? DateTime.UtcNow,
                }
            );
        }

        private async Task RecalculateSetChildPurchasePricesLockedAsync(
            SetChildPurchasePriceLockScope lockScope,
            IEnumerable<string> productCodes,
            string updatedBy
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

            var recalculation = await new SetChildPurchasePriceService(_db)
                .RecalculateLockedAsync(
                    lockScope,
                    normalizedCodes,
                    storeCodes: null,
                    updatedBy: updatedBy
                );
            if (
                recalculation.ProductSetCode.SkippedGroupCount > 0
                || recalculation.StoreMultiCodeProduct.SkippedGroupCount > 0
            )
            {
                throw new InvalidOperationException(
                    recalculation.Errors.FirstOrDefault()?.Reason
                        ?? "套装与多码商品成本无法完整校正"
                );
            }
        }

        private string ResolveActorName()
        {
            var actorName = _currentUserService.GetCurrentUsername();
            return string.IsNullOrWhiteSpace(actorName) ? "System" : actorName;
        }

        private string ResolveActorGuid() => _currentUserService.GetCurrentUserGuid();

        private string ResolveActorType() =>
            !string.IsNullOrWhiteSpace(ResolveActorGuid())
            || !string.Equals(ResolveActorName(), "System", StringComparison.OrdinalIgnoreCase)
                ? "User"
                : "System";

        /// <summary>
        /// 提取货号中的数字部分用于排序
        /// 例如：HB309-096 -> 96, HB309-237 -> 237
        /// </summary>
        private int ExtractItemNumberForSort(string? itemNumber)
        {
            if (string.IsNullOrEmpty(itemNumber))
                return 0;

            // 查找 "-" 的位置
            var dashIndex = itemNumber.IndexOf('-');
            if (dashIndex < 0 || dashIndex >= itemNumber.Length - 1)
                return 0;

            // 提取 "-" 后面的部分
            var numberPart = itemNumber.Substring(dashIndex + 1);

            // 尝试转换为整数
            if (int.TryParse(numberPart, out var number))
                return number;

            return 0;
        }

        #endregion
    }
}
