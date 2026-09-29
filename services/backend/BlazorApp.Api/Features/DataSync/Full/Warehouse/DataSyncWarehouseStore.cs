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

namespace BlazorApp.Api.Features.DataSync.Full.Warehouse;

/// <summary>
/// DataSyncWarehouseStore 保留旧同步步骤的顺序，仅负责本切片的持久化与读取。
/// </summary>
internal sealed class DataSyncWarehouseStore : DataSyncSliceBase
{
    public DataSyncWarehouseStore(DataSyncSliceContext context)
        : base(context)
    {
    }

        public async Task<SyncResult> SyncProductStocksFromHqAsync()
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
                    "🚀 开始从HQ数据库同步商品库存数据（AutoMapper + 导航查询模式）..."
                );

                // 1. 检查HQ数据库连接
                HqContext.CheckConnection();

                // 全量清空、写入、快照和历史必须由同一个事务覆盖。
                await LocalContext.Db.Ado.BeginTranAsync();
                try
                {
                    var existingRows = await LocalContext
                        .Db.Queryable<WarehouseProduct>()
                        .Select(item => new WarehouseMinOrderQuantitySyncGuard.LocalMinOrderQuantityRow
                        {
                            ProductCode = item.ProductCode,
                            MinOrderQuantity = item.MinOrderQuantity,
                        })
                        .ToListAsync();
                    var auditProductCodes = new HashSet<string>(
                        existingRows
                            .Select(row => row.ProductCode)
                            .Where(code => !string.IsNullOrWhiteSpace(code))
                            .Select(code => code!.Trim()),
                        StringComparer.OrdinalIgnoreCase
                    );
                    // 清表前记下本地中包数，重建时回填：HQ 的最小订货量只补本地空缺，不覆盖本地维护值。
                    var localMinOrderQuantities =
                        WarehouseMinOrderQuantitySyncGuard.BuildLocalValues(existingRows);
                    var beforeSnapshots = await ChangeHistoryService.CaptureSnapshotsAsync(
                        auditProductCodes
                    );

                    // 先清空本地WarehouseProduct表，失败时由外层回滚恢复原数据。
                    Logger.LogInformation("清空本地WarehouseProduct表...");
                    var deletedCount = await LocalContext
                        .Db.Deleteable<WarehouseProduct>()
                        .ExecuteCommandAsync();
                    Logger.LogInformation($"已清空 {deletedCount} 条WarehouseProduct记录");

                    // 从HQ数据库获取所有商品库存数据。
                    var totalInserted = 0;
                    const int batchSize = 5000; // 每批处理5000条记录
                    var pageNumber = 1;

                    while (true)
                    {
                        var hqStocksBatch = await HqContext
                            .CBP_DIC_商品库存表Db.AsQueryable()
                            .Includes(x => x.商品信息) // 使用导航属性加载关联的商品信息
                            .Skip((pageNumber - 1) * batchSize)
                            .Take(batchSize)
                            .ToListAsync();

                        if (!hqStocksBatch.Any())
                            break; // 没有更多数据

                        var withProductInfoCount = hqStocksBatch.Count(x => x.商品信息 != null);
                        Logger.LogInformation(
                            $"从HQ数据库获取到第 {pageNumber} 批商品库存，共 {hqStocksBatch.Count} 条，其中 {withProductInfoCount} 条有关联的商品信息"
                        );

                        var warehouseProducts = Mapper.Map<List<WarehouseProduct>>(hqStocksBatch);
                        WarehouseMinOrderQuantitySyncGuard.Apply(
                            warehouseProducts,
                            localMinOrderQuantities
                        );
                        foreach (var product in warehouseProducts)
                        {
                            if (!string.IsNullOrWhiteSpace(product.ProductCode))
                            {
                                auditProductCodes.Add(product.ProductCode.Trim());
                            }
                        }

                        Logger.LogInformation(
                            $"AutoMapper转换完成，生成 {warehouseProducts.Count} 个WarehouseProduct对象"
                        );

                        await LocalContext.Db.Insertable(warehouseProducts).ExecuteCommandAsync();

                        totalInserted += warehouseProducts.Count;
                        Logger.LogInformation(
                            $"第 {pageNumber} 批商品库存AutoMapper批量插入完成，已插入 {warehouseProducts.Count} 条"
                        );

                        // 🚀 输出前3个转换结果的示例（用于调试）
                        foreach (var product in warehouseProducts.Take(3))
                        {
                            Logger.LogDebug(
                                $"   示例商品: {product.ProductCode} (库存: {product.StockQuantity}, 价格: {product.OEMPrice:C2})"
                            );
                        }

                        pageNumber++;
                    }

                    var afterSnapshots = await ChangeHistoryService.CaptureSnapshotsAsync(
                        auditProductCodes
                    );
                    await ChangeHistoryService.RecordChangesAsync(
                        beforeSnapshots,
                        afterSnapshots,
                        HistoryContextFactory.Create(
                            "DataSyncLegacyFull",
                            batchGuid,
                            DateTime.UtcNow
                        )
                    );

                    await LocalContext.Db.Ado.CommitTranAsync();

                    result.AddedCount = totalInserted;
                    result.UpdatedCount = 0; // 清空重建模式下没有更新操作
                    result.ErrorCount = 0;
                    result.IsSuccess = true;
                    result.Message =
                        $"商品库存同步完成（使用AutoMapper转换）！清空: {deletedCount} 条，新增: {totalInserted} 条，错误: 0 条";
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
                Logger.LogError(ex, "同步商品库存数据时发生错误");
                result.Message = $"同步失败: {ex.Message}";
                result.IsSuccess = false;
            }
            finally
            {
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }

            return result;
        }

        public async Task<List<WarehouseProduct>> ConvertHqStocksToWarehouseProductsAsync(
            List<string> productCodes
        )
        {
            try
            {
                Logger.LogInformation(
                    $"🔄 开始使用AutoMapper转换 {productCodes.Count} 个商品的库存数据..."
                );

                // 🚀 使用导航查询获取HQ数据
                var hqStocks = await HqContext
                    .CBP_DIC_商品库存表Db.AsQueryable()
                    .Includes(x => x.商品信息) // 使用导航属性
                    .Where(x =>
                        !string.IsNullOrEmpty(x.H商品编码) && productCodes.Contains(x.H商品编码)
                    )
                    .ToListAsync();

                Logger.LogInformation($"📊 从HQ获取到 {hqStocks.Count} 条库存记录");

                // 🚀 使用AutoMapper进行批量转换
                var warehouseProducts = Mapper.Map<List<WarehouseProduct>>(hqStocks);

                Logger.LogInformation(
                    $"✅ AutoMapper转换完成，生成 {warehouseProducts.Count} 个WarehouseProduct对象"
                );

                // 🚀 输出转换统计信息
                var withProductInfo = warehouseProducts.Count(x =>
                    !string.IsNullOrEmpty(x.ProductCode)
                );
                var totalValue = warehouseProducts.Sum(x => x.StockValue ?? 0);
                var totalStock = warehouseProducts.Sum(x => x.StockQuantity ?? 0);

                Logger.LogInformation(
                    $"📈 转换统计: 有详细信息: {withProductInfo}个, 总库存值: {totalValue:C2}, 总库存量: {totalStock}"
                );

                return warehouseProducts;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "❌ AutoMapper转换HQ库存数据时发生错误");
                return new List<WarehouseProduct>();
            }
        }
}
