using System.Collections.Concurrent;
using System.Data;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Features.DataSync.Common;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HqEntities;
using SqlSugar;

namespace BlazorApp.Api.Features.DataSync.Incremental;

/// <summary>
/// DataSyncIncrementalStore 保留旧同步步骤的顺序，仅负责本切片的持久化与读取。
/// </summary>
internal sealed class DataSyncIncrementalStore : DataSyncSliceBase
{
    public DataSyncIncrementalStore(DataSyncSliceContext context)
        : base(context)
    {
    }

        public async Task<SyncResult> SyncProductsIncrementalFromHqAsync(DateTime lastUpdateDate)
        {
            var result = new SyncResult
            {
                StartTime = DateTime.Now,
                IsSuccess = false,
                Message = "",
                AddedCount = 0,
                UpdatedCount = 0,
                ErrorCount = 0,
            };
            var batchGuid = Guid.NewGuid();

            try
            {
                Logger.LogInformation(
                    $"🚀 开始从HQ数据库增量同步商品信息数据（包括商品字典和一品多码表到ProductSetCode）（上次更新时间: {lastUpdateDate:yyyy-MM-dd HH:mm:ss}）..."
                );

                // 1. 检查HQ数据库连接
                HqContext.CheckConnection();

                // 🔄 开启事务，确保数据一致性
                await LocalContext.Db.Ado.BeginTranAsync();

                try
                {
                    var childCostLockScope = await SetChildPurchasePriceMutationLock.AcquireAllAsync(
                        LocalContext.Db
                    );
                    // 2. 从HQ数据库获取指定日期后更新的商品信息数据
                    const int batchSize = 5000; // 每批处理5000条记录，增量同步不需要太大批次
                    var totalProcessed = 0;
                    var totalProductAdded = 0;
                    var totalProductUpdated = 0;
                    var totalErrors = 0;
                    var pageNumber = 1;
                    var existingProductsByCode = (await LocalContext.Db.Queryable<Product>().ToListAsync())
                        .Where(product => !string.IsNullOrWhiteSpace(product.ProductCode))
                        .GroupBy(product => product.ProductCode!.Trim(), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                    // 有效、停用和软删除的本地 Type1 都受 GUID 与规范化父子业务键保护。
                    var protectedType1 = await DataSyncProductProtectionRules.GetAllType1ProtectionAsync(LocalContext.Db);
                    var auditProductCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var beforeSnapshots =
                        new Dictionary<string, WarehouseProductChangeSnapshotDto>(
                            StringComparer.OrdinalIgnoreCase
                        );

                    Logger.LogInformation("开始增量同步商品字典表数据...");

                    while (true)
                    {
                        var hqProductsBatch = await HqContext
                            .DIC_商品信息字典表Db.AsQueryable()
                            .Where(x => x.FGC_LastModifyDate >= lastUpdateDate) // 只获取指定日期后更新的商品
                            .Skip((pageNumber - 1) * batchSize)
                            .Take(batchSize)
                            .ToListAsync();

                        if (!hqProductsBatch.Any())
                            break; // 没有更多数据

                        Logger.LogInformation(
                            $"从HQ数据库获取到第 {pageNumber} 批更新的商品信息，共 {hqProductsBatch.Count} 条"
                        );

                        try
                        {
                            // 🚀 处理Product数据的增量同步
                            // 1. 转换为Product实体
                            var localProducts = hqProductsBatch
                                .Select(hqProduct => Mapper.Map<Product>(hqProduct))
                                .ToList();
                            foreach (var product in localProducts)
                            {
                                if (string.IsNullOrWhiteSpace(product.ProductCode))
                                {
                                    continue;
                                }

                                var productCode = product.ProductCode.Trim();
                                if (!existingProductsByCode.TryGetValue(productCode, out var existing))
                                {
                                    continue;
                                }

                                // 旧增量入口直接 Storageable 写 Product，沿用普通商品成本保护。
                                product.PurchasePrice = HqPurchasePriceSyncGuard.PreservePositiveForOrdinaryProduct(
                                    existing.PurchasePrice,
                                    product.PurchasePrice,
                                    existing.ProductType,
                                    product.ProductType
                                );
                            }
                            var pageAuditProductCodes = new HashSet<string>(
                                localProducts
                                    .Where(product => !string.IsNullOrWhiteSpace(product.ProductCode))
                                    .Select(product => product.ProductCode!.Trim()),
                                StringComparer.OrdinalIgnoreCase
                            );
                            var firstSeenProductCodes = pageAuditProductCodes
                                .Where(code => !auditProductCodes.Contains(code))
                                .ToList();
                            if (firstSeenProductCodes.Count > 0)
                            {
                                // 跨页重复商品只能使用本任务首次遇到时的 before，避免中间状态污染审计。
                                var pageBeforeSnapshots =
                                    await ChangeHistoryService.CaptureSnapshotsAsync(
                                        firstSeenProductCodes
                                    );
                                foreach (var snapshot in pageBeforeSnapshots)
                                {
                                    beforeSnapshots.TryAdd(snapshot.Key, snapshot.Value);
                                }

                                auditProductCodes.UnionWith(firstSeenProductCodes);
                            }

                            // 2. 处理Product增量同步
                            var productStorageResult = await LocalContext
                                .Db.Storageable(localProducts)
                                .WhereColumns(x => x.ProductCode) // 基于商品编码进行判断
                                .ToStorageAsync();

                            var productInsertResult =
                                productStorageResult.AsInsertable.ExecuteCommand();
                            var productUpdateResult =
                                productStorageResult.AsUpdateable.ExecuteCommand();

                            totalProductAdded += productInsertResult;
                            totalProductUpdated += productUpdateResult;
                            foreach (var product in localProducts)
                            {
                                if (!string.IsNullOrWhiteSpace(product.ProductCode))
                                {
                                    existingProductsByCode[product.ProductCode.Trim()] = product;
                                }
                            }

                            Logger.LogInformation(
                                $"第 {pageNumber} 批商品字典增量同步完成 - Product新增: {productInsertResult}, 更新: {productUpdateResult}"
                            );

                            // 🚀 输出前3个处理结果的示例（用于调试）
                            foreach (var product in localProducts.Take(3))
                            {
                                Logger.LogDebug(
                                    $"   示例商品: {product.ProductCode} (更新时间: {product.UpdatedAt:yyyy-MM-dd HH:mm:ss})"
                                );
                            }

                            // 每处理几批后稍微延迟，避免数据库压力过大
                            if (pageNumber % 10 == 0)
                            {
                                await Task.Delay(500); // 每10批延迟0.5秒
                                Logger.LogInformation(
                                    $"增量同步进度: 已处理 {pageNumber} 批，总计新增/更新 {totalProductAdded + totalProductUpdated} 条Product记录"
                                );
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError(ex, $"第 {pageNumber} 批商品信息增量同步失败");
                            totalErrors += hqProductsBatch.Count;
                            // 历史或业务写入失败时回滚整个增量调用，不能继续提交后续页。
                            throw;
                        }

                        totalProcessed += hqProductsBatch.Count;
                        pageNumber++;
                    }

                    Logger.LogInformation(
                        $"商品字典表增量同步完成 - Product新增: {totalProductAdded}, 更新: {totalProductUpdated}"
                    );

                    // 🚀 5. 使用JOIN连接查询增量同步一品多码表数据到ProductSetCode
                    Logger.LogInformation(
                        "开始使用JOIN连接查询增量同步一品多码表数据到ProductSetCode..."
                    );

                    var totalMultiCodeAdded = 0;
                    var totalMultiCodeUpdated = 0;
                    var syncedMultiCodeParentCodes = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase
                    );
                    pageNumber = 1;

                    while (true)
                    {
                        // 🔧 优化策略：使用JOIN连接查询，直接关联商品信息表和一品多码表
                        // 同时添加增量同步的时间条件
                        var hqMultiCodesBatch = await HqContext
                            .Db.Queryable<
                                BlazorApp.Shared.Models.HqEntities.DIC_一品多码表,
                                BlazorApp.Shared.Models.HqEntities.DIC_商品信息字典表
                            >(
                                (multiCode, product) =>
                                    new JoinQueryInfos(
                                        JoinType.Inner,
                                        multiCode.H商品编码 == product.H商品编码
                                    )
                            )
                            .Where(
                                (multiCode, product) =>
                                    multiCode.FGC_LastModifyDate >= lastUpdateDate
                                    && !string.IsNullOrEmpty(multiCode.H多码商品编号)
                                    && !string.IsNullOrEmpty(multiCode.H商品编码)
                            )
                            .Select((multiCode, product) => multiCode)
                            .Skip((pageNumber - 1) * batchSize)
                            .Take(batchSize)
                            .ToListAsync();

                        if (!hqMultiCodesBatch.Any())
                            break; // 没有更多数据

                        Logger.LogInformation(
                            $"从HQ数据库获取到第 {pageNumber} 批更新的一品多码数据，共 {hqMultiCodesBatch.Count} 条"
                        );

                        try
                        {
                            var multiCodeSetCodes = new List<ProductSetCode>();
                            foreach (var hqMultiCode in hqMultiCodesBatch)
                            {
                                if (
                                    DataSyncProductProtectionRules.TryGetProtectedType1Conflict(
                                        hqMultiCode,
                                        protectedType1,
                                        out var conflictReason
                                    )
                                )
                                {
                                    Logger.LogWarning(
                                        "跳过与本地 Type1 冲突的 HQ 增量多码。ProductCode={ProductCode}, ChildCode={ChildCode}, HGUID={Hguid}, Reason={Reason}",
                                        hqMultiCode.H商品编码,
                                        hqMultiCode.H多码商品编号,
                                        hqMultiCode.HGUID,
                                        conflictReason
                                    );
                                    continue;
                                }

                                var mapped = Mapper.Map<ProductSetCode>(hqMultiCode);
                                mapped.SetPurchasePrice = null;
                                multiCodeSetCodes.Add(mapped);
                                DataSyncProductProtectionRules.AddNormalizedCode(
                                    syncedMultiCodeParentCodes,
                                    mapped.ProductCode
                                );
                            }

                            // 父子业务键是关系身份，不能用 SetItemNumber 命中另一条本地关系。
                            var multiCodeStorageResult = await LocalContext
                                .Db.Storageable(multiCodeSetCodes)
                                .WhereColumns(x => new { x.ProductCode, x.SetProductCode })
                                .ToStorageAsync();

                            var multiCodeInsertResult =
                                multiCodeStorageResult.AsInsertable.ExecuteCommand();
                            var multiCodeUpdateResult =
                                multiCodeStorageResult.AsUpdateable.ExecuteCommand();

                            totalMultiCodeAdded += multiCodeInsertResult;
                            totalMultiCodeUpdated += multiCodeUpdateResult;

                            Logger.LogInformation(
                                $"第 {pageNumber} 批一品多码增量同步完成 - ProductSetCode新增: {multiCodeInsertResult}, 更新: {multiCodeUpdateResult}"
                            );

                            // 每处理几批后稍微延迟，避免数据库压力过大
                            if (pageNumber % 8 == 0)
                            {
                                await Task.Delay(800); // 每8批延迟0.8秒
                                Logger.LogInformation(
                                    $"一品多码增量同步进度: 已处理 {pageNumber} 批，总计新增/更新 {totalMultiCodeAdded + totalMultiCodeUpdated} 条ProductSetCode记录"
                                );
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError(ex, $"第 {pageNumber} 批一品多码数据增量同步失败");
                            totalErrors += hqMultiCodesBatch.Count;
                            // 历史或业务写入失败时回滚整个增量调用，不能继续提交后续页。
                            throw;
                        }

                        pageNumber++;
                    }

                    Logger.LogInformation(
                        $"一品多码表增量同步完成 - ProductSetCode新增: {totalMultiCodeAdded}, 更新: {totalMultiCodeUpdated}"
                    );

                    var affectedSetParentCodes = new HashSet<string>(
                        auditProductCodes,
                        StringComparer.OrdinalIgnoreCase
                    );
                    affectedSetParentCodes.UnionWith(syncedMultiCodeParentCodes);
                    if (affectedSetParentCodes.Count > 0)
                    {
                        var recalculation = await new SetChildPurchasePriceService(LocalContext.Db)
                            .RecalculateLockedAsync(
                                childCostLockScope,
                                affectedSetParentCodes,
                                null,
                                HistoryContextFactory.ResolveSetChildPurchasePriceActor()
                            );
                        DataSyncProductProtectionRules.EnsureSetChildPurchasePriceRecalculated(
                            recalculation,
                            affectedSetParentCodes
                        );
                    }

                    if (auditProductCodes.Count > 0)
                    {
                        // 所有 Product 与 ProductSetCode 业务写入完成后才采集最终 after，并只记录一次批次历史。
                        var afterSnapshots = await ChangeHistoryService.CaptureSnapshotsAsync(
                            auditProductCodes
                        );
                        await ChangeHistoryService.RecordChangesAsync(
                            beforeSnapshots,
                            afterSnapshots,
                            HistoryContextFactory.Create(
                                "DataSyncLegacyIncremental",
                                batchGuid,
                                DateTime.UtcNow
                            )
                        );
                    }

                    // 🎉 提交事务
                    await LocalContext.Db.Ado.CommitTranAsync();

                    result.AddedCount = totalProductAdded + totalMultiCodeAdded;
                    result.UpdatedCount = totalProductUpdated + totalMultiCodeUpdated;
                    result.ErrorCount = totalErrors;
                    result.IsSuccess = totalErrors == 0;
                    result.Message =
                        $"🎉 商品信息增量同步完成！总共处理: {totalProcessed}, Product表新增: {totalProductAdded}, 更新: {totalProductUpdated}; ProductSetCode表新增: {totalMultiCodeAdded}, 更新: {totalMultiCodeUpdated}，错误: {totalErrors}";
                    Logger.LogInformation(result.Message);
                }
                catch (Exception)
                {
                    // 🔙 回滚事务
                    await LocalContext.Db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "增量同步商品信息数据时发生错误");
                result.IsSuccess = false;
                // 增量路径与全量路径一致：业务锁冲突必须保留为可重试失败。
                if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out var conflict))
                {
                    result.ErrorCode = SetChildPurchasePriceMutationLock.BusyErrorCode;
                    result.Message = conflict!.Message;
                }
                else
                {
                    result.Message = $"增量同步失败: {ex.Message}";
                }
            }
            finally
            {
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }

            return result;
        }

        public async Task<SyncResult> SyncProductStocksIncrementalFromHqAsync(
            DateTime lastUpdateDate
        )
        {
            var result = new SyncResult
            {
                StartTime = DateTime.Now,
                IsSuccess = false,
                Message = "",
                AddedCount = 0,
                UpdatedCount = 0,
                ErrorCount = 0,
            };
            var batchGuid = Guid.NewGuid();

            try
            {
                Logger.LogInformation(
                    $"🚀 开始从HQ数据库增量同步商品库存数据（上次更新时间: {lastUpdateDate:yyyy-MM-dd HH:mm:ss}）..."
                );

                // 1. 检查HQ数据库连接
                HqContext.CheckConnection();

                // 增量调用的所有分页共用一个事务，历史失败时整体回滚。
                await LocalContext.Db.Ado.BeginTranAsync();
                try
                {
                    // 从HQ数据库获取指定日期后更新的库存信息数据
                    const int batchSize = 10000; // 每批处理10000条记录
                    var totalProcessed = 0;
                    var totalAdded = 0;
                    var totalUpdated = 0;
                    var totalErrors = 0;
                    var pageNumber = 1;
                    var auditProductCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var beforeSnapshots =
                        new Dictionary<string, WarehouseProductChangeSnapshotDto>(
                            StringComparer.OrdinalIgnoreCase
                        );
                    // Storageable 命中即整行更新：先记下本地中包数，写入前回填，HQ 只补本地空缺。
                    var localMinOrderQuantities =
                        await WarehouseMinOrderQuantitySyncGuard.LoadLocalValuesAsync(LocalContext.Db);

                    while (true)
                    {
                        var hqStocksBatch = await HqContext
                            .CBP_DIC_商品库存表Db.AsQueryable()
                            .Includes(x => x.商品信息) // 使用导航查询，同时获取商品信息
                            .Where(x =>
                                !string.IsNullOrEmpty(x.H商品编码)
                                && x.FGC_LastModifyDate >= lastUpdateDate
                            )
                            .Skip((pageNumber - 1) * batchSize)
                            .Take(batchSize)
                            .ToListAsync();

                        if (!hqStocksBatch.Any())
                            break; // 没有更多数据

                        Logger.LogInformation(
                            $"从HQ数据库获取到第 {pageNumber} 批更新的库存信息，共 {hqStocksBatch.Count} 条"
                        );

                        var warehouseProducts = Mapper.Map<List<WarehouseProduct>>(hqStocksBatch);
                        WarehouseMinOrderQuantitySyncGuard.Apply(
                            warehouseProducts,
                            localMinOrderQuantities
                        );

                        try
                        {
                            var pageAuditProductCodes = new HashSet<string>(
                                warehouseProducts
                                    .Where(product => !string.IsNullOrWhiteSpace(product.ProductCode))
                                    .Select(product => product.ProductCode.Trim()),
                                StringComparer.OrdinalIgnoreCase
                            );
                            var firstSeenProductCodes = pageAuditProductCodes
                                .Where(code => !auditProductCodes.Contains(code))
                                .ToList();
                            if (firstSeenProductCodes.Count > 0)
                            {
                                // 同一编码跨页出现时保留最初快照，最终只和全部写入后的 after 对比。
                                var pageBeforeSnapshots =
                                    await ChangeHistoryService.CaptureSnapshotsAsync(
                                        firstSeenProductCodes
                                    );
                                foreach (var snapshot in pageBeforeSnapshots)
                                {
                                    beforeSnapshots.TryAdd(snapshot.Key, snapshot.Value);
                                }

                                auditProductCodes.UnionWith(firstSeenProductCodes);
                            }

                            var storageResult = await LocalContext
                                .Db.Storageable(warehouseProducts)
                                .WhereColumns(x => x.ProductCode) // 基于商品编码进行判断
                                .ToStorageAsync();

                            var insertResult = storageResult.AsInsertable.ExecuteCommand();
                            var updateResult = storageResult.AsUpdateable.ExecuteCommand();

                            totalAdded += insertResult;
                            totalUpdated += updateResult;

                            Logger.LogInformation(
                                $"第 {pageNumber} 批库存信息增量同步完成 - 新增: {insertResult}, 更新: {updateResult}"
                            );

                            foreach (var stock in warehouseProducts.Take(3))
                            {
                                Logger.LogDebug(
                                    $"   示例库存: {stock.ProductCode} (库存量: {stock.StockQuantity})"
                                );
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError(ex, $"第 {pageNumber} 批库存信息增量同步失败");
                            totalErrors += hqStocksBatch.Count;
                            // 历史或业务写入失败时回滚整个增量调用，不能继续提交后续页。
                            throw;
                        }

                        totalProcessed += hqStocksBatch.Count;
                        pageNumber++;
                    }

                    if (auditProductCodes.Count > 0)
                    {
                        // 全部库存分页写入结束后统一采集 after，确保一个 BatchGuid 只产生一次审计写入。
                        var afterSnapshots = await ChangeHistoryService.CaptureSnapshotsAsync(
                            auditProductCodes
                        );
                        await ChangeHistoryService.RecordChangesAsync(
                            beforeSnapshots,
                            afterSnapshots,
                            HistoryContextFactory.Create(
                                "DataSyncLegacyIncremental",
                                batchGuid,
                                DateTime.UtcNow
                            )
                        );
                    }

                    await LocalContext.Db.Ado.CommitTranAsync();

                    result.AddedCount = totalAdded;
                    result.UpdatedCount = totalUpdated;
                    result.ErrorCount = totalErrors;
                    result.IsSuccess = totalErrors == 0;
                    result.Message =
                        $"库存信息增量同步完成！总共处理: {totalProcessed}, 新增: {totalAdded}, 更新: {totalUpdated}, 错误: {totalErrors}";
                    Logger.LogInformation(result.Message);
                }
                catch (Exception)
                {
                    await LocalContext.Db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "增量同步商品库存数据时发生错误");
                result.Message = $"库存增量同步失败: {ex.Message}";
                result.IsSuccess = false;
            }
            finally
            {
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }

            return result;
        }
}
