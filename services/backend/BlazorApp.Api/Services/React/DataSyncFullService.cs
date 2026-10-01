using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.Background;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.HqEntities;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using SqlSugar;
using System.Runtime.ExceptionServices;
using WarehouseMinOrderQuantitySyncGuard = BlazorApp.Api.Features.DataSync.Common.WarehouseMinOrderQuantitySyncGuard;

namespace BlazorApp.Api.Services.React
{
    public class DataSyncFullService : IDataSyncFullService
    {
        private readonly SqlSugarContext _localContext;
        private readonly HqSqlSugarContext _hqContext;
        private readonly HBSalesSqlSugarContext _hbSalesContext;
        private readonly POSMSqlSugarContext _posmContext;
        private readonly IConfiguration _configuration;
        private readonly IMapper _mapper;
        private readonly ILogger<DataSyncFullService> _logger;
        private readonly ScheduledTaskLogService _taskLogService;
        private readonly IStoreRetailPriceHqSyncService _storeRetailPriceHqSyncService;
        private readonly IMemoryCache _cache;
        private readonly IWarehouseProductChangeHistoryService _changeHistoryService;
        private readonly ICurrentUserService _currentUserService;

        public DataSyncFullService(
            SqlSugarContext localContext,
            HqSqlSugarContext hqContext,
            HBSalesSqlSugarContext hbSalesContext,
            POSMSqlSugarContext posmContext,
            IConfiguration configuration,
            IMapper mapper,
            ILogger<DataSyncFullService> logger,
            ScheduledTaskLogService taskLogService,
            IStoreRetailPriceHqSyncService storeRetailPriceHqSyncService,
            IMemoryCache cache,
            IWarehouseProductChangeHistoryService changeHistoryService,
            ICurrentUserService currentUserService
        )
        {
            _localContext = localContext;
            _hqContext = hqContext;
            _hbSalesContext = hbSalesContext;
            _posmContext = posmContext;
            _configuration = configuration;
            _mapper = mapper;
            _logger = logger;
            _taskLogService = taskLogService;
            _storeRetailPriceHqSyncService = storeRetailPriceHqSyncService;
            _cache = cache;
            _changeHistoryService = changeHistoryService;
            _currentUserService = currentUserService;
        }

        /// <summary>
        /// 全量同步商品：HQ 商品字典表 → 本地 Product 表
        ///
        /// 架构：生产者-消费者 Channel 管道模式
        /// ┌──────────────────┐     ┌──────────────────┐
        /// │  生产者（4并发）   │     │  消费者（6并发）   │
        /// │  分页查询HQ 40K   │────▶│  分块20K BulkCopy │
        /// │  SemaphoreSlim   │     │  分页10K 插入     │
        /// └──────────────────┘     └──────────────────┘
        ///
        /// 流程：
        /// 1) 检查 HQ 连接；
        /// 2) 清空本地 Product 表；
        /// 3) 生产者并发（上限4）分页（40,000）拉取 HQ 数据，通过 Channel 传递给消费者；
        /// 4) 消费者并发（6个）分块（20,000）执行 BulkCopy，分页（10,000）插入；
        /// 5) 统计新增/错误数与耗时。
        /// </summary>
        public async Task<SyncResult> SyncProductsFromHqAsync()
        {
            const int hqBatchSize = 40000;
            const int writePageSize = 10000;
            var result = new SyncResult { StartTime = DateTime.Now };
            var batchGuid = Guid.NewGuid();
            var (auditActorGuid, auditActorName) = GetCurrentAuditActor();

            try
            {
                _hqContext.CheckConnection();
                using var hqCountDb = HqSqlSugarContext.CreateConcurrentConnection(
                    _configuration
                );
                var total = await hqCountDb.Queryable<DIC_商品信息字典表>().CountAsync();
                var pages = (int)Math.Ceiling(total / (double)hqBatchSize);
                List<Product> initialProducts = new();
                var existingByCode = new Dictionary<string, Product>(StringComparer.OrdinalIgnoreCase);
                var hqCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var batchAuditCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var batchBeforeSnapshots = new Dictionary<
                    string,
                    WarehouseProductChangeSnapshotDto
                >(StringComparer.OrdinalIgnoreCase);
                var added = 0;
                var updated = 0;

                var transaction = await _localContext.Db.Ado.UseTranAsync(async () =>
                {
                    // 旧全量入口也会改写 Product；旧成本快照必须在同一总锁内读取并持续到提交。
                    _ = await SetChildPurchasePriceMutationLock.AcquireAllAsync(
                        _localContext.Db
                    );
                    initialProducts = await _localContext.Db.Queryable<Product>()
                        .Where(item => item.ProductCode != null)
                        .ToListAsync();
                    foreach (var product in initialProducts)
                    {
                        if (!string.IsNullOrWhiteSpace(product.ProductCode))
                        {
                            existingByCode[product.ProductCode.Trim()] = product;
                        }
                    }

                    for (var page = 1; page <= pages; page++)
                    {
                        using var hqDb = HqSqlSugarContext.CreateConcurrentConnection(
                            _configuration
                        );
                        var hqBatch = await hqDb.Queryable<DIC_商品信息字典表>()
                            .OrderBy(item => item.ID)
                            .Skip((page - 1) * hqBatchSize)
                            .Take(hqBatchSize)
                            .ToListAsync();
                        if (hqBatch.Count == 0)
                        {
                            continue;
                        }

                        var localBatch = _mapper.Map<List<Product>>(hqBatch)
                            .Where(item => !string.IsNullOrWhiteSpace(item.ProductCode))
                            .GroupBy(item => item.ProductCode!, StringComparer.OrdinalIgnoreCase)
                            .Select(group => group.Last())
                            .ToList();
                        var auditCodes = localBatch
                            .Select(item => item.ProductCode!.Trim())
                            .ToList();
                        var firstSeenAuditCodes = auditCodes
                            .Where(code => batchAuditCodes.Add(code))
                            .ToList();
                        if (firstSeenAuditCodes.Count > 0)
                        {
                            var pageBeforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                                firstSeenAuditCodes
                            );
                            foreach (var snapshot in pageBeforeSnapshots)
                            {
                                batchBeforeSnapshots.TryAdd(snapshot.Key, snapshot.Value);
                            }
                        }
                        var occurredAtUtc = DateTime.UtcNow;
                        var toInsert = new List<Product>();
                        var toUpdate = new List<Product>();
                        foreach (var item in localBatch)
                        {
                            var productCode = item.ProductCode!.Trim();
                            item.ProductCode = productCode;
                            hqCodes.Add(productCode);
                            item.UpdatedAt = occurredAtUtc;
                            item.UpdatedBy = auditActorName;
                            if (existingByCode.TryGetValue(productCode, out var existing))
                            {
                                item.PurchasePrice = HqPurchasePriceSyncGuard.PreservePositiveForOrdinaryProduct(
                                    existing.PurchasePrice,
                                    item.PurchasePrice,
                                    existing.ProductType,
                                    item.ProductType
                                );
                                item.UUID = existing.UUID;
                                toUpdate.Add(item);
                            }
                            else
                            {
                                item.CreatedAt = occurredAtUtc;
                                item.CreatedBy = auditActorName;
                                toInsert.Add(item);
                            }
                            existingByCode[productCode] = item;
                        }

                        using (SqlSugarAuditScope.PreserveExplicitAuditFields())
                        {
                            if (toUpdate.Count > 0)
                            {
                                await _localContext.Db.Fastest<Product>()
                                    .AS("Product")
                                    .PageSize(writePageSize)
                                    .BulkUpdateAsync(toUpdate);
                            }
                            if (toInsert.Count > 0)
                            {
                                await _localContext.Db.Fastest<Product>()
                                    .AS("Product")
                                    .PageSize(writePageSize)
                                    .BulkCopyAsync(toInsert);
                            }
                        }

                        added += toInsert.Count;
                        updated += toUpdate.Count;
                    }

                    var staleCodes = initialProducts
                        .Select(item => item.ProductCode)
                        .Where(code => !string.IsNullOrWhiteSpace(code) && !hqCodes.Contains(code!))
                        .Select(code => code!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (staleCodes.Count > 0)
                    {
                        // 本期只记录新建和修改；HQ 已移除商品的物理删除不生成字段变空事件。
                        await DeleteProductsByCodeInBatchesAsync(staleCodes);
                    }

                    if (batchAuditCodes.Count > 0)
                    {
                        var afterSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                            batchAuditCodes
                        );
                        await _changeHistoryService.RecordChangesAsync(
                            batchBeforeSnapshots,
                            afterSnapshots,
                            CreateSystemSyncContext(
                                "DataSyncFull",
                                batchGuid,
                                DateTime.UtcNow,
                                auditActorGuid,
                                auditActorName
                            )
                        );
                    }
                });
                if (!transaction.IsSuccess)
                {
                    throw transaction.ErrorException
                        ?? new InvalidOperationException("Product 全量同步事务失败");
                }

                result.AddedCount = added;
                result.UpdatedCount = updated;
                result.IsSuccess = true;
                result.Message = $"商品同步成功，新增 {added} 条，更新 {updated} 条";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 商品全量同步异常");
                result.IsSuccess = false;
                result.ErrorCount = 1;
                // 保留套装成本锁的 busy 语义，供上层返回可重试的 409。
                if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out var conflict))
                {
                    result.ErrorCode = SetChildPurchasePriceMutationLock.BusyErrorCode;
                    result.BusyErrorCount = 1;
                    result.Message = conflict!.Message;
                }
                else
                {
                    result.Message = "商品同步异常";
                }
            }
            finally
            {
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }

            return result;
        }

        // 保留旧并发实现供性能对比；生产接口统一走上面的事务化审计实现。
        private async Task<SyncResult> SyncProductsFromHqLegacyConcurrentAsync()
        {
            var result = new SyncResult { StartTime = DateTime.Now };
            Console.WriteLine("📦 [商品同步] ===== 开始全量同步商品 =====");

            try
            {
                // 步骤1：检查 HQ 数据库连接
                _logger.LogInformation("[ReactSync] 商品同步：检查HQ连接");
                _hqContext.CheckConnection();
                Console.WriteLine("✅ [商品同步] HQ连接检查通过");

                // 步骤2：清空本地 Product 表（全量同步先清后写）
                // 使用 TRUNCATE TABLE 替代 DELETE，大幅提升清空速度
                var deleteStart = DateTime.Now;
                using var syncLocalDb = SqlSugarContext.CreateConcurrentConnection(_configuration);
                _logger.LogInformation("[ReactSync] 清空本地 Product 表");
                await syncLocalDb.Ado.ExecuteCommandAsync("TRUNCATE TABLE Product");
                var deleteDuration = DateTime.Now - deleteStart;
                Console.WriteLine(
                    $"🗑️ [商品同步] 本地 Product 表已清空，耗时 {deleteDuration.TotalSeconds:F1}s"
                );

                // 并发参数配置
                const int batchSize = 40000; // 生产者每批查询 HQ 数据量
                const int producerConcurrency = 4; // 生产者最大并发数
                const int consumerConcurrency = 6; // 消费者并发数
                const int chunkSize = 20000; // 消费者每次 BulkCopy 的分块大小
                const int writePageSize = 10000; // BulkCopy 内部分页大小

                // 步骤3：统计 HQ 总数据量，计算分页数
                using var hqCountDb = HqSqlSugarContext.CreateConcurrentConnection(_configuration);
                var totalCount = await hqCountDb.Queryable<DIC_商品信息字典表>().CountAsync();
                var pageCount = (int)Math.Ceiling(totalCount / (double)batchSize);
                Console.WriteLine(
                    $"📊 [商品同步] HQ总数据量: {totalCount:N0} 条, 分页数: {pageCount}, 每批: {batchSize:N0} 条"
                );
                Console.WriteLine(
                    $"⚙️ [商品同步] 生产者并发: {producerConcurrency}, 消费者并发: {consumerConcurrency}, 分块: {chunkSize:N0}, 写入分页: {writePageSize:N0}"
                );

                // 创建有界 Channel：容量=消费者数×2，超出时生产者阻塞（背压机制）
                var channel = System.Threading.Channels.Channel.CreateBounded<List<Product>>(
                    capacity: consumerConcurrency * 2
                );

                // 线程安全的统计计数器（多消费者并发写入时使用 Interlocked 操作）
                var totalAdded = 0;
                var totalErrors = 0;
                var fetchErrors = 0;

                // ===== 启动消费者（6个并发） =====
                // 每个消费者独立数据库连接，从 Channel 读取数据后分块 BulkCopy
                var consumers = new List<Task>();
                for (int i = 0; i < consumerConcurrency; i++)
                {
                    var consumerIdx = i;
                    var c = Task.Run(async () =>
                    {
                        // 每个消费者创建独立的数据库连接
                        using var consumerDb = SqlSugarContext.CreateConcurrentConnection(
                            _configuration
                        );
                        // BulkCopy 大批量写入易超时，使用更长命令超时
                        consumerDb.Ado.CommandTimeOut = _configuration.GetValue<int>(
                            "Database:BulkCopyCommandTimeoutSeconds",
                            600
                        );

                        // 从 Channel 持续读取数据，直到 Channel 关闭
                        await foreach (var batch in channel.Reader.ReadAllAsync())
                        {
                            // 将生产者的一批数据分块处理，避免单次 BulkCopy 过大
                            for (int offset = 0; offset < batch.Count; offset += chunkSize)
                            {
                                var chunk = batch.Skip(offset).Take(chunkSize).ToList();
                                try
                                {
                                    await consumerDb
                                        .Fastest<Product>()
                                        .AS("Product")
                                        .PageSize(writePageSize)
                                        .BulkCopyAsync(chunk);
                                    System.Threading.Interlocked.Add(ref totalAdded, chunk.Count);
                                    _logger.LogInformation(
                                        "[ReactSync] 商品消费者{Idx}写入：{Count}",
                                        consumerIdx,
                                        chunk.Count
                                    );
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(
                                        ex,
                                        "[ReactSync] 商品消费者{Idx}写入失败（批大小:{Size}）",
                                        consumerIdx,
                                        chunk.Count
                                    );
                                    System.Threading.Interlocked.Add(ref totalErrors, chunk.Count);
                                    Console.WriteLine(
                                        $"❌ [商品同步] 消费者{consumerIdx}写入失败: {chunk.Count} 条, 错误: {ex.Message}"
                                    );
                                    await Task.Delay(1500);
                                }
                            }
                        }
                    });
                    consumers.Add(c);
                }
                Console.WriteLine(
                    $"🚀 [商品同步] 已启动 {consumerConcurrency} 个消费者，等待数据..."
                );

                // ===== 启动生产者（SemaphoreSlim 控制并发上限4） =====
                var semaphore = new SemaphoreSlim(producerConcurrency, producerConcurrency);
                _logger.LogInformation(
                    "[ReactSync] 商品并发读取初始化：Total={Total}, Pages={Pages}, BatchSize={Batch}, Producers={Producers}, Consumers={Consumers}",
                    totalCount,
                    pageCount,
                    batchSize,
                    producerConcurrency,
                    consumerConcurrency
                );

                var producers = new List<Task>();
                for (var page = 1; page <= pageCount; page++)
                {
                    var pageIndex = page;
                    var p = Task.Run(async () =>
                    {
                        // 等待信号量许可，控制同时执行的生产者数量
                        await semaphore.WaitAsync();
                        try
                        {
                            var skip = (pageIndex - 1) * batchSize;
                            // 为每个生产者创建独立的 HQ 连接，避免并发冲突
                            using var hqDb = HqSqlSugarContext.CreateConcurrentConnection(
                                _configuration
                            );
                            var hqBatch = await hqDb.Queryable<DIC_商品信息字典表>()
                                .Skip(skip)
                                .Take(batchSize)
                                .ToListAsync();

                            if (hqBatch.Any())
                            {
                                Console.WriteLine(
                                    $"📥 [商品同步] 生产者查询第{pageIndex}/{pageCount}批: {hqBatch.Count:N0} 条"
                                );
                                _logger.LogInformation(
                                    "[ReactSync] 商品第{Page}批：{Count}",
                                    pageIndex,
                                    hqBatch.Count
                                );
                                // 映射为本地 Product 实体后写入 Channel
                                var localBatch = _mapper.Map<List<Product>>(hqBatch);
                                await channel.Writer.WriteAsync(localBatch);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(
                                ex,
                                "[ReactSync] 商品第{Page}批查询/映射失败",
                                pageIndex
                            );
                            System.Threading.Interlocked.Increment(ref fetchErrors);
                            Console.WriteLine(
                                $"❌ [商品同步] 第{pageIndex}批查询失败: {ex.Message}"
                            );
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    });
                    producers.Add(p);
                }

                // 步骤4：等待所有生产者完成 → 关闭 Channel → 等待所有消费者完成
                await Task.WhenAll(producers);
                Console.WriteLine("📥 [商品同步] 所有生产者已完成，关闭 Channel");
                channel.Writer.Complete();
                await Task.WhenAll(consumers);
                Console.WriteLine("📤 [商品同步] 所有消费者已完成");

                // 步骤5：汇总统计结果
                result.AddedCount = totalAdded;
                result.ErrorCount = totalErrors + fetchErrors;
                result.IsSuccess = (totalErrors == 0 && fetchErrors == 0);
                result.Message = totalErrors == 0 ? "商品同步成功" : "商品同步完成，但存在错误";

                Console.WriteLine(
                    $"📊 [商品同步] 同步完成: 新增 {totalAdded:N0} 条, 写入错误 {totalErrors:N0} 条, 查询错误 {fetchErrors} 批"
                );
                Console.WriteLine(
                    $"⏱️ [商品同步] 总耗时: {DateTime.Now - result.StartTime:hh\\:mm\\:ss}"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 商品同步异常");
                result.IsSuccess = false;
                result.Message = "商品同步异常";
                Console.WriteLine($"💥 [商品同步] 同步异常: {ex.Message}");
            }
            finally
            {
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }

            return result;
        }

        /// <summary>
        /// 全量同步分店零售价旧入口。
        /// 当前实现统一委托给 HQ 零售价同步服务处理，
        /// 由统一服务负责全量同步的影子表流程，以及指定分店时的直接替换流程。
        /// </summary>
        public async Task<SyncResult> SyncStoreRetailPricesFromHqConcurrentAsync(
            List<string>? selectedStoreCodes = null
        )
        {
            if (_storeRetailPriceHqSyncService != null)
            {
                return await _storeRetailPriceHqSyncService.SyncFullAsync(selectedStoreCodes);
            }

            throw new InvalidOperationException("分店零售价 HQ 统一同步服务未注册");
        }

        /// <summary>
        /// 批量插入重试封装：动态批量大小 + 分级超时 + 指数退避
        /// 说明：
        /// - 使用 SqlSugar Fastest BulkCopy 提升吞吐；
        /// - 动态调整批量大小：超时时降级到更小的批量（10000→5000→2500→1000）；
        /// - 分级超时策略：重试时增加超时时间（600s→1200s→1800s）；
        /// - 指数退避：每次重试前等待（1s→2s→4s）；
        /// - 返回实际插入的记录数，便于上层统计。
        /// </summary>
        private async Task<int> RetryBulkInsertAsync<T>(
            ISqlSugarClient db,
            List<T> data,
            int maxRetries = 5,
            int? initialPageSize = null,
            int? timeoutSeconds = null
        )
            where T : class, new()
        {
            var defaultPageSize =
                initialPageSize
                ?? _configuration.GetValue<int>("Database:BulkCopyInitialPageSize", 10000);
            var defaultTimeout =
                timeoutSeconds
                ?? _configuration.GetValue<int>("Database:BulkCopyCommandTimeoutSeconds", 900);

            var dataCount = data.Count;
            var dynamicTimeout = Math.Max(defaultTimeout, (dataCount / 1000) * 60);

            var retries = 0;
            var pageSize = defaultPageSize;
            var currentTimeout = dynamicTimeout;
            int inserted = 0;

            while (retries < maxRetries)
            {
                try
                {
                    db.Ado.CommandTimeOut = currentTimeout;

                    for (int i = 0; i < data.Count; i += pageSize)
                    {
                        var batch = data.Skip(i).Take(pageSize).ToList();
                        await db.Fastest<T>().BulkCopyAsync(batch);
                        inserted += batch.Count;
                    }

                    return inserted;
                }
                catch (SqlException ex) when (ex.Number == -2 && retries < maxRetries - 1)
                {
                    retries++;
                    pageSize = Math.Max(
                        _configuration.GetValue<int>("Database:BulkCopyMinPageSize", 10000),
                        pageSize / 2
                    );
                    currentTimeout = Math.Min(currentTimeout * 2, 1800);

                    _logger.LogWarning(
                        ex,
                        "[ReactSync] 批量插入超时（PageSize={PageSize}, Timeout={Timeout}s），重试{Retry}/{Max}，新PageSize={NewPageSize}, 新Timeout={NewTimeout}s",
                        pageSize * 2,
                        currentTimeout / 2,
                        retries,
                        maxRetries,
                        pageSize,
                        currentTimeout
                    );

                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retries)));
                }
                catch (SqlException ex) when (ex.Number == 1205 && retries < maxRetries - 1)
                {
                    retries++;
                    pageSize = Math.Max(
                        _configuration.GetValue<int>("Database:BulkCopyMinPageSize", 1000),
                        pageSize / 2
                    );
                    currentTimeout = Math.Min(currentTimeout * 2, 1800);

                    _logger.LogWarning(
                        ex,
                        "[ReactSync] 批量插入死锁（PageSize={PageSize}, Timeout={Timeout}s），重试{Retry}/{Max}，新PageSize={NewPageSize}, 新Timeout={NewTimeout}s",
                        pageSize * 2,
                        currentTimeout / 2,
                        retries,
                        maxRetries,
                        pageSize,
                        currentTimeout
                    );

                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retries)));
                }
                catch (Exception ex)
                    when ((
                            ex.Message?.Contains("transport-level error") == true
                            || ex.Message?.Contains("Cannot access destination table") == true
                            || (
                                ex.InnerException != null
                                && (
                                    ex.InnerException.Message?.Contains("transport-level error")
                                        == true
                                    || ex.InnerException.Message?.Contains(
                                        "Cannot access destination table"
                                    ) == true
                                )
                            )
                        )
                        && retries < maxRetries - 1
                    )
                {
                    retries++;
                    currentTimeout = Math.Min(currentTimeout * 2, 1800);
                    var delaySeconds = Math.Pow(2, retries + 1);

                    _logger.LogWarning(
                        ex,
                        "[ReactSync] 批量插入传输层错误，等待{Delay}s后重试 {Retry}/{Max}：{Msg}",
                        delaySeconds,
                        retries,
                        maxRetries,
                        ex.Message
                    );

                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                }
                catch (Exception ex) when (retries < maxRetries - 1)
                {
                    retries++;
                    currentTimeout = Math.Min(currentTimeout * 2, 1800);

                    _logger.LogWarning(
                        ex,
                        "[ReactSync] 批量插入失败（Timeout={Timeout}s），重试{Retry}/{Max}，新Timeout={NewTimeout}s：{Msg}",
                        currentTimeout / 2,
                        retries,
                        maxRetries,
                        currentTimeout,
                        ex.Message
                    );

                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retries)));
                }
                finally
                {
                    db.Ado.CommandTimeOut = _configuration.GetValue<int>(
                        "Database:ConcurrentCommandTimeoutSeconds",
                        300
                    );
                }
            }

            throw new Exception(
                $"批量插入最终失败：已重试 {maxRetries} 次，成功插入 {inserted}/{data.Count} 条记录"
            );
        }

        /// <summary>
        /// 全量同步分店清货价：HQ 清货价表 → 本地 StoreClearancePrice（串行批量写入）
        /// 支持按分店过滤；事务包裹清理与写入，失败回滚。
        /// </summary>
        public async Task<SyncResult> SyncStoreClearancePricesFromHqConcurrentAsync(
            List<string>? selectedStoreCodes = null,
            int? maxConcurrency = null,
            int? batchSize = null
        )
        {
            var result = new SyncResult { StartTime = DateTime.Now };

            var effectiveMaxConcurrency =
                maxConcurrency ?? _configuration.GetValue<int>("Database:SyncMaxConcurrency", 2);
            var effectiveBatchSize =
                batchSize ?? _configuration.GetValue<int>("Database:SyncBatchSize", 50000);
            var actualBatchSize = effectiveBatchSize;
            try
            {
                _hqContext.CheckConnection();

                await _localContext.Db.Ado.BeginTranAsync();
                try
                {
                    // 本地清理：如果指定了分店，只清理这些分店；否则清理全部
                    var deleteStart = DateTime.Now;
                    int deleted;
                    if (selectedStoreCodes?.Any() == true)
                    {
                        deleted = await _localContext
                            .Db.Deleteable<StoreClearancePrice>()
                            .Where(x =>
                                x.StoreCode != null && selectedStoreCodes.Contains(x.StoreCode)
                            )
                            .ExecuteCommandAsync();
                    }
                    else
                    {
                        deleted = await _localContext
                            .Db.Deleteable<StoreClearancePrice>()
                            .ExecuteCommandAsync();
                    }
                    var deleteDuration = DateTime.Now - deleteStart;
                    _logger.LogInformation(
                        "[ReactSync] 清理清货价本地数据：删除{Deleted:N0}条，耗时{Seconds:F1}s",
                        deleted,
                        deleteDuration.TotalSeconds
                    );

                    // 全量（或按分店）拉取 HQ 清货价数据，分页串行写入
                    var hqDb = HqSqlSugarContext.CreateConcurrentConnection(_configuration);
                    var hasStores = selectedStoreCodes != null && selectedStoreCodes.Count > 0;
                    var baseQuery = hqDb.Queryable<DIC_商品清货价表>()
                        .Where(c => !string.IsNullOrEmpty(c.商品编码))
                        .WhereIF(
                            hasStores,
                            c => SqlFunc.ContainsArray(selectedStoreCodes!, c.分店代码)
                        )
                        .Where(c =>
                            SqlFunc
                                .Subqueryable<DIC_商品信息字典表>()
                                .Where(product =>
                                    product.H商品编码 == c.商品编码 && product.H使用状态 == true
                                )
                                .Any()
                        );

                    var totalCount = await baseQuery.CountAsync();
                    if (totalCount == 0)
                    {
                        await _localContext.Db.Ado.CommitTranAsync();
                        result.IsSuccess = true;
                        result.Message = "未发现需要同步的清货价数据";
                        return result;
                    }

                    var pages = (int)Math.Ceiling(totalCount / (double)actualBatchSize);
                    var totalAdded = 0;
                    var totalErrors = 0;

                    for (var page = 1; page <= pages; page++)
                    {
                        var skip = (page - 1) * actualBatchSize;
                        var pageStart = DateTime.Now;
                        var hqPage = await baseQuery
                            .OrderBy(c => c.ID)
                            .Skip(skip)
                            .Take(actualBatchSize)
                            .ToListAsync();
                        var pageQuerySec = (DateTime.Now - pageStart).TotalSeconds;
                        _logger.LogInformation(
                            "[ReactSync] 清货价批{Page}/{Pages} 拉取{Count} 用时{Sec:F1}s",
                            page,
                            pages,
                            hqPage.Count,
                            (double)pageQuerySec
                        );

                        if (!hqPage.Any())
                            continue;

                        var localBatch = _mapper.Map<List<StoreClearancePrice>>(hqPage);
                        try
                        {
                            await _localContext
                                .Db.Fastest<StoreClearancePrice>()
                                .AS("StoreClearancePrice")
                                .PageSize(50000)
                                .BulkCopyAsync(localBatch);
                            totalAdded += localBatch.Count;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(
                                ex,
                                "[ReactSync] 清货价批{Page} 插入失败 批大小{Size}",
                                page,
                                localBatch.Count
                            );
                            totalErrors += localBatch.Count;
                            await Task.Delay(1500);
                        }

                        hqPage.Clear();
                        localBatch.Clear();
                    }

                    hqDb.Dispose();
                    await _localContext.Db.Ado.CommitTranAsync();

                    result.AddedCount = totalAdded;
                    result.ErrorCount = totalErrors;
                    result.IsSuccess = totalErrors == 0;
                    result.Message =
                        totalErrors == 0 ? "分店清货价同步成功" : "分店清货价同步完成，但存在错误";

                    var finalDuration = DateTime.Now - result.StartTime;
                    result.EndTime = DateTime.Now;
                    result.Duration = finalDuration;

                    result.TotalCount = totalAdded + totalErrors;
                    result.TotalStores = 0;
                    result.SuccessStores = 0;
                    result.FailedStores = 0;

                    var successRate =
                        result.TotalCount > 0
                            ? (double)result.SuccessCount / result.TotalCount * 100
                            : 0;

                    result.Message =
                        totalErrors == 0
                            ? $"分店清货价同步成功：共处理{result.TotalCount:N0}条记录，成功率{successRate:F1}%"
                            : $"分店清货价同步完成：共处理{result.TotalCount:N0}条记录，成功{result.SuccessCount:N0}条，失败{result.ErrorCount:N0}条，成功率{successRate:F1}%";
                }
                catch (Exception exTran)
                {
                    await _localContext.Db.Ado.RollbackTranAsync();
                    throw new Exception("分店清货价同步事务失败", exTran);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 分店清货价同步异常");
                result.IsSuccess = false;
                result.Message = "分店清货价同步异常";
            }
            finally
            {
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }

            return result;
        }

        private async Task<(
            int processed,
            int added,
            int errors,
            double queryTime,
            double insertTime,
            StoreSyncError? error
        )> ProcessSingleStoreClearanceAsync(
            int storeIndex,
            string storeCode,
            int batchSize,
            SemaphoreSlim semaphore
        )
        {
            await semaphore.WaitAsync();
            ISqlSugarClient? localDb = null;
            ISqlSugarClient? hqDb = null;
            var processed = 0;
            var added = 0;
            var errors = 0;
            var storeStart = DateTime.Now;
            try
            {
                storeStart = DateTime.Now;
                localDb = SqlSugarContext.CreateConcurrentConnection(_configuration);
                hqDb = HqSqlSugarContext.CreateConcurrentConnection(_configuration);

                var countStart = DateTime.Now;
                var totalCount = await hqDb.Queryable<DIC_商品清货价表>()
                    .Where(c =>
                        !string.IsNullOrEmpty(c.商品编码)
                        && c.分店代码 == storeCode
                        && SqlFunc
                            .Subqueryable<DIC_商品信息字典表>()
                            .Where(product =>
                                product.H商品编码 == c.商品编码 && product.H使用状态 == true
                            )
                            .Any()
                    )
                    .CountAsync();
                var qDuration = DateTime.Now - countStart;
                if (totalCount == 0)
                    return (0, 0, 0, qDuration.TotalSeconds, 0, null);

                var pages = (int)Math.Ceiling(totalCount / (double)batchSize);
                var insertTotalSeconds = 0.0;

                for (var page = 1; page <= pages; page++)
                {
                    var retries = 0;
                    List<DIC_商品清货价表> hqPage = new();
                    while (true)
                    {
                        try
                        {
                            var skip = (page - 1) * batchSize;
                            var pageStart = DateTime.Now;
                            hqPage = await hqDb.Queryable<DIC_商品清货价表>()
                                .Where(c =>
                                    !string.IsNullOrEmpty(c.商品编码)
                                    && c.分店代码 == storeCode
                                    && SqlFunc
                                        .Subqueryable<DIC_商品信息字典表>()
                                        .Where(product =>
                                            product.H商品编码 == c.商品编码
                                            && product.H使用状态 == true
                                        )
                                        .Any()
                                )
                                .OrderBy(c => c.ID)
                                .Skip(skip)
                                .Take(batchSize)
                                .ToListAsync();
                            var pageQuerySec = (DateTime.Now - pageStart).TotalSeconds;
                            _logger.LogInformation(
                                "[ReactSync] 分店{Store} 清货价 页{Page}/{Pages} 拉取{Count} 用时{Sec:F1}s",
                                storeCode,
                                page,
                                pages,
                                hqPage.Count,
                                pageQuerySec
                            );
                            break;
                        }
                        catch (SqlSugarException ex) when (retries < 3)
                        {
                            retries++;
                            var delay = TimeSpan.FromSeconds(Math.Pow(2, retries));
                            _logger.LogWarning(
                                ex,
                                "[ReactSync] 分店{Store} 清货价 页{Page} 查询失败，重试{Retry}/3，等待{Delay}s",
                                storeCode,
                                page,
                                retries,
                                delay.TotalSeconds
                            );
                            await Task.Delay(delay);
                            hqDb?.Dispose();
                            hqDb = HqSqlSugarContext.CreateConcurrentConnection(_configuration);
                            continue;
                        }
                    }

                    if (!hqPage.Any())
                        continue;

                    var localBatch = _mapper.Map<List<StoreClearancePrice>>(hqPage);
                    var insertStart = DateTime.Now;
                    try
                    {
                        var inserted = await RetryBulkInsertAsync(localDb, localBatch, 3);
                        added += inserted;
                        errors += localBatch.Count - inserted;

                        var insertDuration = DateTime.Now - insertStart;
                        _logger.LogInformation(
                            "[ReactSync] 分店{Store} 清货价 页{Page}/{Pages} 插入成功：{Inserted:N0}/{Total:N0}条，耗时{Sec:F1}s",
                            storeCode,
                            page,
                            pages,
                            inserted,
                            localBatch.Count,
                            insertDuration.TotalSeconds
                        );
                    }
                    catch (Exception ex)
                    {
                        var insertDuration = DateTime.Now - insertStart;
                        _logger.LogError(
                            ex,
                            "[ReactSync] 分店{Store} 清货价 页{Page}/{Pages} 插入失败，耗时{Sec:F1}s",
                            storeCode,
                            page,
                            pages,
                            insertDuration.TotalSeconds
                        );
                        errors += localBatch.Count;
                    }
                    insertTotalSeconds += (DateTime.Now - insertStart).TotalSeconds;
                    processed += hqPage.Count;
                    hqPage.Clear();
                    localBatch.Clear();
                }

                var storeDuration = DateTime.Now - storeStart;
                _logger.LogInformation(
                    "[ReactSync] 分店{Store} 清货价完成：处理{Processed:N0} 成功{Added:N0} 失败{Errors:N0} 总耗时{Sec:F1}s",
                    storeCode,
                    processed,
                    added,
                    errors,
                    storeDuration.TotalSeconds
                );
                return (processed, added, errors, qDuration.TotalSeconds, insertTotalSeconds, null);
            }
            catch (Exception ex)
            {
                var error = new StoreSyncError
                {
                    StoreCode = storeCode,
                    ErrorMessage = ex.Message,
                    ExceptionType = ex.GetType().Name,
                    IsRetried = false,
                    RetryCount = 0,
                    ProcessedCount = processed,
                    InsertedCount = added,
                    FailedCount = errors,
                    DurationSeconds = (DateTime.Now - storeStart).TotalSeconds,
                };

                _logger.LogError(ex, "[ReactSync] 分店{Store} 清货价处理异常", storeCode);
                return (0, 0, 1, 0, 0, error);
            }
            finally
            {
                localDb?.Dispose();
                hqDb?.Dispose();
                semaphore.Release();
            }
        }

        /// <summary>
        /// 按分店并发同步“一品多码”：HQ 分店多码表 → 本地 StoreMultiCodeProduct。
        /// 先清理目标分店历史数据，再分店并发拉取与批量写入。
        /// </summary>
        public async Task<SyncResult> SyncStoreMultiCodeProductsFromHqConcurrentAsync(
            List<string>? selectedStoreCodes = null,
            int? maxConcurrency = null,
            int? batchSize = null
        )
        {
            var result = new SyncResult { StartTime = DateTime.Now };

            var effectiveMaxConcurrency =
                maxConcurrency ?? _configuration.GetValue<int>("Database:SyncMaxConcurrency", 5);
            var effectiveBatchSize =
                batchSize ?? _configuration.GetValue<int>("Database:SyncBatchSize", 50000);

            var semaphore = new SemaphoreSlim(effectiveMaxConcurrency, effectiveMaxConcurrency);
            var progressLock = new object();
            var storeErrors = new List<StoreSyncError>();

            var totalProcessed = 0;
            var totalAdded = 0;
            var totalErrors = 0;
            var totalQueryTime = 0.0;
            var totalInsertTime = 0.0;

            try
            {
                List<string> storeCodesToProcess;
                if (selectedStoreCodes?.Any() == true)
                {
                    storeCodesToProcess = selectedStoreCodes;
                }
                else
                {
                    var startQuery = DateTime.Now;
                    var storeCodesNullable = await _hqContext
                        .Db.Queryable<DIC_分店一品多码表, DIC_商品信息字典表>(
                            (mc, product) =>
                                new JoinQueryInfos(
                                    JoinType.Inner,
                                    mc.H商品编码 == product.H商品编码
                                )
                        )
                        .Where(
                            (mc, product) =>
                                !string.IsNullOrEmpty(mc.H商品编码)
                                && !string.IsNullOrEmpty(mc.H分店代码)
                                && mc.H使用状态 == true
                                && product.H使用状态 == true
                        )
                        .GroupBy((mc, product) => mc.H分店代码)
                        .Select((mc, product) => mc.H分店代码)
                        .ToListAsync();
                    storeCodesToProcess = storeCodesNullable
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x!)
                        .ToList();
                    var duration = DateTime.Now - startQuery;
                    _logger.LogInformation(
                        "[ReactSync] 一品多码分店数{Count}，查询耗时{Seconds:F1}s",
                        storeCodesToProcess.Count,
                        duration.TotalSeconds
                    );
                }

                if (!storeCodesToProcess.Any())
                {
                    result.IsSuccess = true;
                    result.Message = "未发现需要同步的分店";
                    return result;
                }

                var tasks =
                    new List<
                        Task<(
                            int processed,
                            int added,
                            int errors,
                            double queryTime,
                            double insertTime,
                            StoreSyncError? error
                        )>
                    >();
                var processStart = DateTime.Now;

                for (int i = 0; i < storeCodesToProcess.Count; i++)
                {
                    var storeCode = storeCodesToProcess[i];
                    var t = Task.Run(async () =>
                        await ProcessSingleStoreMultiCodeAsync(
                            i,
                            storeCode,
                            effectiveBatchSize,
                            semaphore
                        )
                    );
                    tasks.Add(t);

                    if (tasks.Count >= effectiveMaxConcurrency)
                    {
                        var done = await Task.WhenAny(tasks);
                        tasks.Remove(done);
                        var r = await done;
                        lock (progressLock)
                        {
                            totalProcessed += r.processed;
                            totalAdded += r.added;
                            totalErrors += r.errors;
                            totalQueryTime += r.queryTime;
                            totalInsertTime += r.insertTime;
                            if (r.error != null)
                            {
                                storeErrors.Add(r.error);
                            }
                        }
                    }
                }

                var rest = await Task.WhenAll(tasks);
                foreach (var r in rest)
                {
                    lock (progressLock)
                    {
                        totalProcessed += r.processed;
                        totalAdded += r.added;
                        totalErrors += r.errors;
                        totalQueryTime += r.queryTime;
                        totalInsertTime += r.insertTime;
                        if (r.error != null)
                        {
                            storeErrors.Add(r.error);
                        }
                    }
                }

                result.AddedCount = totalAdded;
                result.ErrorCount = totalErrors;
                result.BusyErrorCount = storeErrors.Count(error =>
                    error.ErrorMessage.StartsWith(
                        SetChildPurchasePriceMutationLock.BusyErrorCode + ":",
                        StringComparison.Ordinal
                    )
                );
                if (result.BusyErrorCount > 0 && result.BusyErrorCount == result.ErrorCount)
                {
                    result.ErrorCode = SetChildPurchasePriceMutationLock.BusyErrorCode;
                }
                result.IsSuccess = totalErrors == 0;
                result.Message =
                    totalErrors == 0 ? "分店一品多码同步成功" : "分店一品多码同步完成，但存在错误";

                var finalDuration = DateTime.Now - processStart;
                result.EndTime = DateTime.Now;
                result.Duration = finalDuration;

                result.TotalCount = totalProcessed;
                result.TotalStores = storeCodesToProcess.Count;
                result.SuccessStores = storeCodesToProcess.Count - storeErrors.Count;
                result.FailedStores = storeErrors.Count;
                result.StoreErrors = storeErrors;

                var successRate =
                    result.TotalCount > 0
                        ? (double)result.SuccessCount / result.TotalCount * 100
                        : 0;

                result.Message =
                    totalErrors == 0
                        ? $"分店一品多码同步成功：共处理{result.TotalCount:N0}条记录，{result.SuccessStores}个分店全部成功，成功率{successRate:F1}%"
                        : $"分店一品多码同步完成：共处理{result.TotalCount:N0}条记录，成功{result.SuccessCount:N0}条，失败{result.ErrorCount:N0}条，{result.SuccessStores}个分店成功，{result.FailedStores}个分店失败，成功率{successRate:F1}%";

                if (storeErrors.Any())
                {
                    const int maxErrorDetails = 50;
                    var errorDetails = storeErrors
                        .Take(maxErrorDetails)
                        .Select(e =>
                            $"分店{e.StoreCode}: {e.ErrorMessage} (处理{e.ProcessedCount:N0}条, 成功{e.InsertedCount:N0}条, 耗时{e.DurationSeconds:F1}s)"
                        )
                        .ToList();

                    var omittedCount = storeErrors.Count - errorDetails.Count;
                    result.Details =
                        "错误分店列表：\n"
                        + string.Join("\n", errorDetails)
                        + (omittedCount > 0 ? $"\n另有 {omittedCount} 个错误分店未展开" : string.Empty);
                }

                _logger.LogInformation(
                    "[ReactSync] 一品多码分店同步完成：门店{StoreCount} 记录{Processed:N0} 耗时{Seconds:F1}s 错误{Errors:N0}",
                    storeCodesToProcess.Count,
                    totalProcessed,
                    finalDuration.TotalSeconds,
                    totalErrors
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 分店一品多码同步异常");
                result.IsSuccess = false;
                result.Message = "分店一品多码同步异常";
                result.ErrorCount = Math.Max(result.ErrorCount, 1);
                if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out var conflict))
                {
                    result.ErrorCode = SetChildPurchasePriceMutationLock.BusyErrorCode;
                    result.BusyErrorCount = result.ErrorCount;
                    result.Message = conflict!.Message;
                }
            }
            finally
            {
                semaphore.Dispose();
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }

            return result;
        }

        private async Task<(
            int processed,
            int added,
            int errors,
            double queryTime,
            double insertTime,
            StoreSyncError? error
        )> ProcessSingleStoreMultiCodeAsync(
            int storeIndex,
            string storeCode,
            int batchSize,
            SemaphoreSlim semaphore
        )
        {
            await semaphore.WaitAsync();

            ISqlSugarClient? localDb = null;
            ISqlSugarClient? hqDb = null;
            var processed = 0;
            var added = 0;
            var errors = 0;
            var storeStart = DateTime.Now;
            var localTransactionStarted = false;
            try
            {
                storeStart = DateTime.Now;
                localDb = SqlSugarContext.CreateConcurrentConnection(_configuration);
                hqDb = HqSqlSugarContext.CreateConcurrentConnection(_configuration);
                await localDb.Ado.BeginTranAsync();
                localTransactionStarted = true;
                var lockScope = await SetChildPurchasePriceMutationLock.AcquireAllAsync(localDb);
                var protectedStoreMultiCodeKeys = await GetProtectedStoreMultiCodeKeysAsync(localDb);
                var existingStoreRows = await localDb.Queryable<StoreMultiCodeProduct>()
                    .Where(item => item.StoreCode == storeCode)
                    .ToListAsync();
                var deleteUuids = existingStoreRows
                    .Where(item => !protectedStoreMultiCodeKeys.Contains(GetStoreMultiCodeBusinessKey(item)))
                    .Select(item => item.UUID)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToList();
                var deleted = deleteUuids.Count == 0
                    ? 0
                    : await localDb.Deleteable<StoreMultiCodeProduct>()
                        .Where(item => deleteUuids.Contains(item.UUID))
                        .ExecuteCommandAsync();
                _logger.LogInformation(
                    "[ReactSync] 分店{Store} 清理普通一品多码{Deleted:N0}条",
                    storeCode,
                    deleted
                );

                var countStart = DateTime.Now;
                var totalCount = await hqDb.Queryable<DIC_分店一品多码表>()
                    .Where(mc =>
                        !string.IsNullOrEmpty(mc.H商品编码)
                        && mc.H分店代码 == storeCode
                        && mc.H使用状态 == true
                        && SqlFunc
                            .Subqueryable<DIC_商品信息字典表>()
                            .Where(product =>
                                product.H商品编码 == mc.H商品编码 && product.H使用状态 == true
                            )
                            .Any()
                        && SqlFunc
                            .Subqueryable<DIC_一品多码表>()
                            .Where(ypp =>
                                ypp.H商品编码 == mc.H商品编码 && (ypp.H使用状态 ?? false) == true
                            )
                            .Any()
                    )
                    .CountAsync();
                var qDuration = DateTime.Now - countStart;
                if (totalCount == 0)
                {
                    await localDb.Ado.CommitTranAsync();
                    localTransactionStarted = false;
                    return (0, 0, 0, qDuration.TotalSeconds, 0, null);
                }

                var pages = (int)Math.Ceiling(totalCount / (double)batchSize);
                var insertTotalSeconds = 0.0;

                for (var page = 1; page <= pages; page++)
                {
                    var retries = 0;
                    List<DIC_分店一品多码表> hqPage = new();
                    while (true)
                    {
                        try
                        {
                            var skip = (page - 1) * batchSize;
                            var pageStart = DateTime.Now;
                            hqPage = await hqDb.Queryable<DIC_分店一品多码表>()
                                .Where(mc =>
                                    !string.IsNullOrEmpty(mc.H商品编码)
                                    && mc.H分店代码 == storeCode
                                    && mc.H使用状态 == true
                                    && SqlFunc
                                        .Subqueryable<DIC_商品信息字典表>()
                                        .Where(product =>
                                            product.H商品编码 == mc.H商品编码
                                            && product.H使用状态 == true
                                        )
                                        .Any()
                                    && SqlFunc
                                        .Subqueryable<DIC_一品多码表>()
                                        .Where(ypp =>
                                            ypp.H商品编码 == mc.H商品编码
                                            && (ypp.H使用状态 ?? false) == true
                                        )
                                        .Any()
                                )
                                .OrderBy(mc => mc.ID)
                                .Skip(skip)
                                .Take(batchSize)
                                .ToListAsync();
                            var pageQuerySec = (DateTime.Now - pageStart).TotalSeconds;
                            _logger.LogInformation(
                                "[ReactSync] 分店{Store} 一品多码 页{Page}/{Pages} 拉取{Count} 用时{Sec:F1}s",
                                storeCode,
                                page,
                                pages,
                                hqPage.Count,
                                pageQuerySec
                            );
                            break;
                        }
                        catch (SqlSugarException ex) when (retries < 3)
                        {
                            retries++;
                            var delay = TimeSpan.FromSeconds(Math.Pow(2, retries));
                            _logger.LogWarning(
                                ex,
                                "[ReactSync] 分店{Store} 一品多码 页{Page} 查询失败，重试{Retry}/3，等待{Delay}s",
                                storeCode,
                                page,
                                retries,
                                delay.TotalSeconds
                            );
                            await Task.Delay(delay);
                            hqDb?.Dispose();
                            hqDb = HqSqlSugarContext.CreateConcurrentConnection(_configuration);
                            continue;
                        }
                    }

                    if (!hqPage.Any())
                        continue;

                    var localBatch = _mapper.Map<List<StoreMultiCodeProduct>>(hqPage)
                        .Select(item =>
                        {
                            // HQ 普通多码的成本不能直写，必须由锁内统一重算。
                            item.PurchasePrice = null;
                            return item;
                        })
                        // 受保护行不接受 HQ PurchasePrice 或其他字段写入，避免本地套装成本被覆盖。
                        .Where(item =>
                            !protectedStoreMultiCodeKeys.Contains(GetStoreMultiCodeBusinessKey(item))
                        )
                        .ToList();
                    var insertStart = DateTime.Now;
                    try
                    {
                        var inserted = await RetryBulkInsertAsync(localDb, localBatch, 3);
                        if (inserted != localBatch.Count)
                        {
                            throw new InvalidOperationException(
                                $"分店{storeCode}一品多码写入不完整: 期望{localBatch.Count}条，实际{inserted}条"
                            );
                        }
                        added += inserted;

                        var insertDuration = DateTime.Now - insertStart;
                        _logger.LogInformation(
                            "[ReactSync] 分店{Store} 一品多码 页{Page}/{Pages} 插入成功：{Inserted:N0}/{Total:N0}条，耗时{Sec:F1}s",
                            storeCode,
                            page,
                            pages,
                            inserted,
                            localBatch.Count,
                            insertDuration.TotalSeconds
                        );
                    }
                    catch (Exception ex)
                    {
                        var insertDuration = DateTime.Now - insertStart;
                        _logger.LogError(
                            ex,
                            "[ReactSync] 分店{Store} 一品多码 页{Page}/{Pages} 插入失败，耗时{Sec:F1}s",
                            storeCode,
                            page,
                            pages,
                            insertDuration.TotalSeconds
                        );
                        throw;
                    }
                    insertTotalSeconds += (DateTime.Now - insertStart).TotalSeconds;
                    processed += hqPage.Count;
                    hqPage.Clear();
                    localBatch.Clear();
                }

                var writtenGroups = await localDb.Queryable<StoreMultiCodeProduct>()
                    .Where(item => item.StoreCode == storeCode && item.ProductCode != null)
                    .Select(item => new { item.StoreCode, item.ProductCode })
                    .ToListAsync();
                await new SetChildPurchasePriceService(localDb).RecalculateStoreGroupsLockedAsync(
                    lockScope,
                    writtenGroups.Select(item => (item.StoreCode, item.ProductCode)),
                    "DataSync"
                );

                await localDb.Ado.CommitTranAsync();
                localTransactionStarted = false;

                var storeDuration = DateTime.Now - storeStart;
                _logger.LogInformation(
                    "[ReactSync] 分店{Store} 一品多码完成：处理{Processed:N0} 成功{Added:N0} 失败{Errors:N0} 总耗时{Sec:F1}s",
                    storeCode,
                    processed,
                    added,
                    errors,
                    storeDuration.TotalSeconds
                );
                return (processed, added, errors, qDuration.TotalSeconds, insertTotalSeconds, null);
            }
            catch (Exception ex)
            {
                if (localTransactionStarted && localDb != null)
                {
                    await localDb.Ado.RollbackTranAsync();
                    added = 0;
                }
                var error = new StoreSyncError
                {
                    StoreCode = storeCode,
                    // 保留 BUSY，调用方才能把锁冲突识别为可重试而非普通同步失败。
                    ErrorMessage = SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _)
                        ? $"{SetChildPurchasePriceMutationLock.BusyErrorCode}: {ex.Message}"
                        : ex.Message,
                    ExceptionType = ex.GetType().Name,
                    IsRetried = false,
                    RetryCount = 0,
                    ProcessedCount = processed,
                    InsertedCount = added,
                    FailedCount = errors,
                    DurationSeconds = (DateTime.Now - storeStart).TotalSeconds,
                };

                _logger.LogError(ex, "[ReactSync] 分店{Store} 一品多码处理异常", storeCode);
                return (0, 0, 1, 0, 0, error);
            }
            finally
            {
                localDb?.Dispose();
                hqDb?.Dispose();
                semaphore.Release();
            }
        }

        /// <summary>
        /// 全量同步“套装多码”：HQ 一品多码 → 本地 ProductSetCode。
        /// 先在 HQ 短事务内冻结完整源快照，再完成源身份和本地身份预检；
        /// 只有全部预检通过后才删除旧 Type2 并从同一份快照写入。
        /// </summary>
        public async Task<SyncResult> SyncProductSetCodesFromHqAsync(
            int hqBatchSize = 50000,
            int writePageSize = 10000,
            int maxReadConcurrency = 8
        )
        {
            var result = new SyncResult { StartTime = DateTime.Now };
            try
            {
                _hqContext.CheckConnection();

                // 兼容既有接口参数。全量同步改为单次冻结 HQ 快照后，不再并发重读源分页。
                _ = maxReadConcurrency;
                List<string?> activeProductCodes;
                List<DIC_一品多码表> activeHqRows;
                var hqSnapshotTransactionStarted = false;
                try
                {
                    // 两张 HQ 表必须来自同一个可串行化读取边界，避免商品状态和多码关系
                    // 在两次查询间变化而拼出一个从未真实存在过的源集合。
                    await _hqContext.Db.Ado.BeginTranAsync(
                        System.Data.IsolationLevel.Serializable
                    );
                    hqSnapshotTransactionStarted = true;
                    activeProductCodes = await _hqContext.Db.Queryable<DIC_商品信息字典表>()
                        // HQ 上下文默认 NOLOCK；一致性快照必须显式覆盖，避免 Serializable 被绕过。
                        .With(SqlWith.Null)
                        .Where(product => product.H使用状态 == true)
                        .Select(product => product.H商品编码)
                        .ToListAsync();
                    activeHqRows = await _hqContext.Db.Queryable<DIC_一品多码表>()
                        .With(SqlWith.Null)
                        .Where(row => (row.H使用状态 ?? false) == true)
                        .ToListAsync();
                    await _hqContext.Db.Ado.CommitTranAsync();
                    hqSnapshotTransactionStarted = false;
                }
                catch
                {
                    if (hqSnapshotTransactionStarted)
                    {
                        await _hqContext.Db.Ado.RollbackTranAsync();
                    }
                    throw;
                }

                // 先检查完整活跃原始行，不能用业务键或条码条件把坏行过滤掉。
                var invalidSourceRows = activeHqRows
                    .Where(row =>
                        string.IsNullOrWhiteSpace(row.H商品编码)
                        || string.IsNullOrWhiteSpace(row.H多码商品编号)
                    )
                    .ToList();
                if (invalidSourceRows.Count > 0)
                {
                    result.IsSuccess = false;
                    result.ErrorCode = "PRODUCT_SET_CODE_SOURCE_INVALID";
                    result.ErrorCount = invalidSourceRows.Count;
                    result.Message = "HQ 套装多码存在空业务键，未删除或写入任何本地关系";
                    var details = invalidSourceRows
                        .Take(50)
                        .Select(row =>
                            $"HQ ID={row.ID}，GUID={ProductSetCodeIdentityResolver.Normalize(row.HGUID) ?? "(空)"}，父商品={ProductSetCodeIdentityResolver.Normalize(row.H商品编码) ?? "(空)"}，子商品={ProductSetCodeIdentityResolver.Normalize(row.H多码商品编号) ?? "(空)"}"
                        )
                        .ToList();
                    var omittedCount = invalidSourceRows.Count - details.Count;
                    result.Details = string.Join("\n", details)
                        + (omittedCount > 0
                            ? $"\n另有 {omittedCount} 条空业务键记录未展开"
                            : string.Empty);
                    return result;
                }

                var activeProductCodeSet = activeProductCodes
                    .Select(ProductSetCodeIdentityResolver.Normalize)
                    .Where(code => code != null)
                    .Select(code => code!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var sourcePayloadRows = activeHqRows
                    .Where(row =>
                    {
                        var productCode = ProductSetCodeIdentityResolver.Normalize(
                            row.H商品编码
                        );
                        return productCode != null && activeProductCodeSet.Contains(productCode);
                    })
                    .ToList();

                var sourcePreflight = ProductSetCodeIdentityResolver.PreflightSource(
                    sourcePayloadRows,
                    row => row.HGUID,
                    row => row.H商品编码,
                    row => row.H多码商品编号,
                    row => row.FGC_LastModifyDate,
                    row => row.ID
                );
                if (sourcePreflight.Conflicts.Count > 0)
                {
                    var details = sourcePreflight.Conflicts
                        .Select(conflict => conflict.ToErrorMessage())
                        .Take(50)
                        .ToList();
                    result.IsSuccess = false;
                    result.ErrorCode = "PRODUCT_SET_CODE_IDENTITY_CONFLICT";
                    result.ErrorCount = sourcePreflight.Conflicts.Count;
                    result.Message = "HQ 套装多码身份预检失败，未删除或写入任何本地关系";
                    result.Details = string.Join("\n", details);
                    return result;
                }

                var localTransactionStarted = false;
                await _localContext.Db.Ado.BeginTranAsync();
                localTransactionStarted = true;
                try
                {
                    var lockScope = await SetChildPurchasePriceMutationLock.AcquireAllAsync(
                        _localContext.Db
                    );
                    // 本地身份只能在总锁内读取和判定；通过后直到提交不会被其他合规写路径改动。
                    var localIdentityRows = await _localContext.Db.Queryable<ProductSetCode>()
                        .ToListAsync();
                    var identityIndex = ProductSetCodeIdentityResolver.CreateIndex(
                        localIdentityRows
                    );
                    var protectedSourceIds = new HashSet<int>();
                    var localIdentityConflictDetails = new List<string>();
                    foreach (var sourceRow in sourcePreflight.Rows)
                    {
                        var resolution = identityIndex.Resolve(
                            sourceRow.HGUID,
                            sourceRow.H商品编码,
                            sourceRow.H多码商品编号
                        );
                        if (resolution.Kind == ProductSetCodeIdentityMatchKind.Conflict)
                        {
                            var businessKey = resolution.BusinessKey == null
                                ? "(空)"
                                : ProductSetCodeIdentityResolver.FormatBusinessKey(
                                    resolution.BusinessKey
                                );
                            localIdentityConflictDetails.Add(
                                $"本地 ProductSetCode 身份冲突：HQ ID={sourceRow.ID}，GUID={resolution.Guid ?? "(空)"}，业务键={businessKey}，本地记录={ProductSetCodeIdentityResolver.FormatLocalRecords(resolution.AllMatches)}"
                            );
                            continue;
                        }

                        if (resolution.AllMatches.Any(row => row.SetType == 1))
                        {
                            protectedSourceIds.Add(sourceRow.ID);
                        }
                    }

                    if (localIdentityConflictDetails.Count > 0)
                    {
                        await _localContext.Db.Ado.RollbackTranAsync();
                        localTransactionStarted = false;
                        result.IsSuccess = false;
                        result.ErrorCode = "PRODUCT_SET_CODE_IDENTITY_CONFLICT";
                        result.ErrorCount = localIdentityConflictDetails.Count;
                        result.Message =
                            "本地套装多码身份预检失败，未删除或写入任何本地关系";
                        result.Details = string.Join(
                            "\n",
                            localIdentityConflictDetails.Take(50)
                        );
                        return result;
                    }

                    var protectedSetCodeKeys = await GetProtectedSetCodeKeysAsync(_localContext.Db);
                    var protectedSetCodeIds = await GetProtectedSetCodeIdsAsync(_localContext.Db);
                    var acceptedSourceRows = sourcePreflight.Rows
                        .Where(row => !protectedSourceIds.Contains(row.ID))
                        .ToList();
                    var sourcePages = DataSyncIncrementalService.BuildProductSetCodeSourcePages(
                        acceptedSourceRows,
                        hqBatchSize
                    );
                    var localPages = new List<List<ProductSetCode>>(sourcePages.Count);
                    var totalAdded = 0;
                    var totalMissingProductCode = 0;
                    var totalMissingSetProductCode = 0;
                    var totalMissingSetItemNumber = 0;
                    var recalculationProductCodes = new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase
                    );

                    // 映射和必填字段校验也必须在删除前完成；这里仅消费冻结的 HQ 对象，
                    // 不得再访问 HQ 数据库。
                    for (var pageIndex = 0; pageIndex < sourcePages.Count; pageIndex++)
                    {
                        var localBatch = _mapper.Map<List<ProductSetCode>>(
                                sourcePages[pageIndex]
                            )
                            .Select(item =>
                            {
                                item.SetType = 2;
                                item.SetPurchasePrice = null;
                                return item;
                            })
                            .ToList();
                        var missingProductCode = localBatch.Count(item =>
                            string.IsNullOrWhiteSpace(item.ProductCode)
                        );
                        var missingSetProductCode = localBatch.Count(item =>
                            string.IsNullOrWhiteSpace(item.SetProductCode)
                        );
                        var missingSetItemNumber = localBatch.Count(item =>
                            string.IsNullOrWhiteSpace(item.SetItemNumber)
                        );
                        totalMissingProductCode += missingProductCode;
                        totalMissingSetProductCode += missingSetProductCode;
                        totalMissingSetItemNumber += missingSetItemNumber;

                        localBatch = localBatch
                            .Where(item =>
                                !protectedSetCodeIds.Contains(item.SetCodeId)
                                && !protectedSetCodeKeys.Contains(
                                    GetSetCodeBusinessKey(item)
                                )
                            )
                            .ToList();
                        localPages.Add(localBatch);
                        _logger.LogInformation(
                            "[ReactSync] 套装多码冻结页{Page}校验：缺失ProductCode={A} 缺失SetProductCode={B} 缺失SetItemNumber={C} 保留={Valid}",
                            pageIndex + 1,
                            missingProductCode,
                            missingSetProductCode,
                            missingSetItemNumber,
                            localBatch.Count
                        );
                    }

                    var totalMissingRequiredFields =
                        totalMissingProductCode
                        + totalMissingSetProductCode
                        + totalMissingSetItemNumber;
                    if (totalMissingRequiredFields > 0)
                    {
                        await _localContext.Db.Ado.RollbackTranAsync();
                        localTransactionStarted = false;
                        result.IsSuccess = false;
                        result.ErrorCode = "PRODUCT_SET_CODE_SOURCE_INVALID";
                        result.ErrorCount = totalMissingRequiredFields;
                        result.Message = "HQ 套装多码映射后存在空业务键，未删除或写入任何本地关系";
                        result.Details =
                            $"缺失 ProductCode={totalMissingProductCode}，缺失 SetProductCode={totalMissingSetProductCode}，缺失 SetItemNumber={totalMissingSetItemNumber}";
                        return result;
                    }

                    await _localContext
                        .Db.Deleteable<ProductSetCode>()
                        .AS("ProductSetCode")
                        // 所有状态的本地 Type1 都必须原样保留；停用和软删除关系也不能被 HQ 删除后复用。
                        .Where(x => x.SetType != 1)
                        .ExecuteCommandAsync();

                    foreach (var localBatch in localPages)
                    {
                        if (localBatch.Count == 0)
                        {
                            continue;
                        }

                        await _localContext
                            .Db.Fastest<ProductSetCode>()
                            .AS("ProductSetCode")
                            .PageSize(writePageSize)
                            .BulkCopyAsync(localBatch);
                        foreach (
                            var productCode in localBatch
                                .Select(item => item.ProductCode)
                                .Where(code => !string.IsNullOrWhiteSpace(code))
                        )
                        {
                            recalculationProductCodes.Add(productCode!.Trim());
                        }
                        totalAdded += localBatch.Count;
                    }

                    if (recalculationProductCodes.Count > 0)
                    {
                        // 关系写入和 Type1/Type2 成本回写必须同事务、同业务锁提交。
                        await new SetChildPurchasePriceService(_localContext.Db).RecalculateLockedAsync(
                            lockScope,
                            recalculationProductCodes,
                            storeCodes: null,
                            "DataSync"
                        );
                    }

                    await _localContext.Db.Ado.CommitTranAsync();
                    localTransactionStarted = false;
                    result.AddedCount = totalAdded;
                    result.ErrorCount = 0;
                    result.IsSuccess = true;
                    result.Message = "套装多码同步成功";
                    _logger.LogInformation(
                        "[ReactSync] 套装多码汇总：缺失ProductCode={A} 缺失SetProductCode={B} 缺失SetItemNumber={C} 新增={Added} 错误={Err}",
                        totalMissingProductCode,
                        totalMissingSetProductCode,
                        totalMissingSetItemNumber,
                        totalAdded,
                        0
                    );
                }
                catch (Exception exTran)
                {
                    if (localTransactionStarted)
                    {
                        await _localContext.Db.Ado.RollbackTranAsync();
                    }
                    throw new Exception("套装多码同步事务失败", exTran);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 套装多码同步异常");
                result.IsSuccess = false;
                if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out var conflict))
                {
                    result.ErrorCode = SetChildPurchasePriceMutationLock.BusyErrorCode;
                    result.BusyErrorCount = 1;
                    result.Message = conflict!.Message;
                }
                else
                {
                    result.Message = "套装多码同步异常";
                }
                result.ErrorCount = Math.Max(result.ErrorCount, 1);
            }
            finally
            {
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }

            return result;
        }

        private static string GetSetCodeBusinessKey(ProductSetCode item) =>
            $"{item.ProductCode?.Trim()}\u001F{item.SetProductCode?.Trim()}";

        private static string GetStoreMultiCodeBusinessKey(StoreMultiCodeProduct item) =>
            $"{item.ProductCode?.Trim()}\u001F{item.MultiCodeProductCode?.Trim()}";

        private static async Task<HashSet<string>> GetProtectedSetCodeIdsAsync(ISqlSugarClient db)
        {
            var protectedIds = await db.Queryable<ProductSetCode>()
                // HQ 普通多码不得复用任何状态的本地套装关系，包括停用和软删除记录。
                .Where(item => item.SetType == 1)
                .Select(item => item.SetCodeId)
                .ToListAsync();
            return new HashSet<string>(
                protectedIds.Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.OrdinalIgnoreCase
            );
        }

        private static async Task<HashSet<string>> GetProtectedSetCodeKeysAsync(
            ISqlSugarClient db
        )
        {
            var protectedRows = await db.Queryable<ProductSetCode>()
                .Where(item => item.SetType == 1)
                .Select(item => new { item.ProductCode, item.SetProductCode })
                .ToListAsync();

            return new HashSet<string>(
                protectedRows.Select(item =>
                    $"{item.ProductCode?.Trim()}\u001F{item.SetProductCode?.Trim()}"
                ),
                StringComparer.OrdinalIgnoreCase
            );
        }

        private static async Task<HashSet<string>> GetProtectedStoreMultiCodeKeysAsync(
            ISqlSugarClient db
        )
        {
            var protectedRows = await db.Queryable<ProductSetCode>()
                .Where(item => item.SetType == 1)
                .Select(item => new { item.ProductCode, item.SetProductCode })
                .ToListAsync();

            return new HashSet<string>(
                protectedRows.Select(item =>
                    $"{item.ProductCode?.Trim()}\u001F{item.SetProductCode?.Trim()}"
                ),
                StringComparer.OrdinalIgnoreCase
            );
        }

        /// <summary>
        /// 全量同步国内商品：HQ 国内商品字典 → 本地 DomesticProduct。
        /// 分页拉取、批量写入，事务保障一致性。
        /// </summary>
        public async Task<SyncResult> SyncDomesticProductsFromHqAsync(
            int hqBatchSize = 50000,
            int writePageSize = 10000
        )
        {
            var result = new SyncResult { StartTime = DateTime.Now };
            var batchGuid = Guid.NewGuid();
            var (auditActorGuid, auditActorName) = GetCurrentAuditActor();
            try
            {
                if (!(await _hbSalesContext.TestConnectionAsync()))
                {
                    throw new InvalidOperationException("HBSales 数据库连接失败");
                }

                var total = await _hbSalesContext.Db.Queryable<CPT_DIC_商品信息字典表>()
                    .Where(item => !string.IsNullOrEmpty(item.商品编码))
                    .CountAsync();
                var pages = (int)Math.Ceiling(total / (double)hqBatchSize);
                var initialCodes = await _localContext.Db.Queryable<DomesticProduct>()
                    .Select(item => item.ProductCode)
                    .ToListAsync();
                var existingCodes = new HashSet<string>(
                    initialCodes,
                    StringComparer.OrdinalIgnoreCase
                );
                var hqCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var batchAuditCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var batchBeforeSnapshots = new Dictionary<
                    string,
                    WarehouseProductChangeSnapshotDto
                >(StringComparer.OrdinalIgnoreCase);
                var added = 0;
                var updated = 0;

                var transaction = await _localContext.Db.Ado.UseTranAsync(async () =>
                {
                    for (var page = 1; page <= pages; page++)
                    {
                        var batch = await _hbSalesContext.Db.Queryable<CPT_DIC_商品信息字典表>()
                            .Where(item => !string.IsNullOrEmpty(item.商品编码))
                            .OrderBy(item => item.ID)
                            .Skip((page - 1) * hqBatchSize)
                            .Take(hqBatchSize)
                            .ToListAsync();
                        if (batch.Count == 0)
                        {
                            continue;
                        }

                        var localBatch = _mapper.Map<List<DomesticProduct>>(batch)
                            .Where(item => !string.IsNullOrWhiteSpace(item.ProductCode))
                            .GroupBy(item => item.ProductCode, StringComparer.OrdinalIgnoreCase)
                            .Select(group => group.Last())
                            .ToList();
                        var auditCodes = localBatch
                            .Select(item => item.ProductCode.Trim())
                            .ToList();
                        var firstSeenAuditCodes = auditCodes
                            .Where(code => batchAuditCodes.Add(code))
                            .ToList();
                        if (firstSeenAuditCodes.Count > 0)
                        {
                            var pageBeforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                                firstSeenAuditCodes
                            );
                            foreach (var snapshot in pageBeforeSnapshots)
                            {
                                batchBeforeSnapshots.TryAdd(snapshot.Key, snapshot.Value);
                            }
                        }
                        var occurredAtUtc = DateTime.UtcNow;
                        var toInsert = new List<DomesticProduct>();
                        var toUpdate = new List<DomesticProduct>();
                        foreach (var item in localBatch)
                        {
                            item.ProductCode = item.ProductCode.Trim();
                            hqCodes.Add(item.ProductCode);
                            item.UpdatedAt = occurredAtUtc;
                            item.UpdatedBy = auditActorName;
                            if (existingCodes.Contains(item.ProductCode))
                            {
                                toUpdate.Add(item);
                            }
                            else
                            {
                                item.CreatedAt = occurredAtUtc;
                                item.CreatedBy = auditActorName;
                                toInsert.Add(item);
                                existingCodes.Add(item.ProductCode);
                            }
                        }

                        using (SqlSugarAuditScope.PreserveExplicitAuditFields())
                        {
                            if (toUpdate.Count > 0)
                            {
                                await _localContext.Db.Fastest<DomesticProduct>()
                                    .AS("DomesticProduct")
                                    .PageSize(writePageSize)
                                    .BulkUpdateAsync(toUpdate);
                            }
                            if (toInsert.Count > 0)
                            {
                                await _localContext.Db.Fastest<DomesticProduct>()
                                    .AS("DomesticProduct")
                                    .PageSize(writePageSize)
                                    .BulkCopyAsync(toInsert);
                            }
                        }

                        added += toInsert.Count;
                        updated += toUpdate.Count;
                    }

                    var staleCodes = initialCodes
                        .Where(code => !string.IsNullOrWhiteSpace(code) && !hqCodes.Contains(code))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (staleCodes.Count > 0)
                    {
                        // 本期只记录新建和修改；HQ 已移除商品的物理删除不生成字段变空事件。
                        await DeleteDomesticProductsByCodeInBatchesAsync(staleCodes);
                    }

                    if (batchAuditCodes.Count > 0)
                    {
                        var afterSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                            batchAuditCodes
                        );
                        await _changeHistoryService.RecordChangesAsync(
                            batchBeforeSnapshots,
                            afterSnapshots,
                            CreateSystemSyncContext(
                                "DataSyncFull",
                                batchGuid,
                                DateTime.UtcNow,
                                auditActorGuid,
                                auditActorName
                            )
                        );
                    }
                });
                if (!transaction.IsSuccess)
                {
                    throw transaction.ErrorException
                        ?? new InvalidOperationException("DomesticProduct 全量同步事务失败");
                }

                result.AddedCount = added;
                result.UpdatedCount = updated;
                result.IsSuccess = true;
                result.Message = $"DomesticProduct 同步成功，新增 {added} 条，更新 {updated} 条";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] DomesticProduct 全量同步异常");
                result.IsSuccess = false;
                result.ErrorCount = 1;
                result.Message = "DomesticProduct 同步异常";
            }
            finally
            {
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }

            return result;
        }

        private async Task<SyncResult> SyncDomesticProductsFromHqLegacyAsync(
            int hqBatchSize = 50000,
            int writePageSize = 10000
        )
        {
            var result = new SyncResult { StartTime = DateTime.Now };
            try
            {
                if (!(await _hbSalesContext.TestConnectionAsync()))
                {
                    throw new Exception("HBSales 数据库连接失败");
                }
                await _localContext.Db.Ado.BeginTranAsync();
                var bulkCopyTimeout = _configuration.GetValue<int>(
                    "Database:BulkCopyCommandTimeoutSeconds",
                    600
                );
                var prevTimeout = _localContext.Db.Ado.CommandTimeOut;
                _localContext.Db.Ado.CommandTimeOut = bulkCopyTimeout;
                try
                {
                    await _localContext
                        .Db.Deleteable<DomesticProduct>()
                        .AS("DomesticProduct")
                        .ExecuteCommandAsync();
                    var total = await _hbSalesContext
                        .Db.Queryable<CPT_DIC_商品信息字典表>()
                        .Where(x => !string.IsNullOrEmpty(x.商品编码))
                        .CountAsync();
                    var pages = (int)Math.Ceiling(total / (double)hqBatchSize);
                    var added = 0;
                    var errors = 0;
                    for (var page = 1; page <= pages; page++)
                    {
                        var skip = (page - 1) * hqBatchSize;
                        var batch = await _hbSalesContext
                            .Db.Queryable<CPT_DIC_商品信息字典表>()
                            .Where(x => !string.IsNullOrEmpty(x.商品编码))
                            .OrderBy(x => x.ID)
                            .Skip(skip)
                            .Take(hqBatchSize)
                            .ToListAsync();
                        if (!batch.Any())
                            continue;
                        var localBatch = _mapper.Map<List<DomesticProduct>>(batch);
                        try
                        {
                            await _localContext
                                .Db.Fastest<DomesticProduct>()
                                .AS("DomesticProduct")
                                .PageSize(writePageSize)
                                .BulkCopyAsync(localBatch);
                            added += localBatch.Count;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(
                                ex,
                                "[ReactSync] DomesticProduct 批{Page} 插入失败 批大小{Size}",
                                page,
                                localBatch.Count
                            );
                            errors += localBatch.Count;
                            await Task.Delay(1500);
                        }
                        batch.Clear();
                        localBatch.Clear();
                    }
                    await _localContext.Db.Ado.CommitTranAsync();
                    result.AddedCount = added;
                    result.ErrorCount = errors;
                    result.IsSuccess = errors == 0;
                    result.Message =
                        errors == 0
                            ? "DomesticProduct 同步成功"
                            : "DomesticProduct 同步完成，但存在错误";
                }
                catch (Exception exTran)
                {
                    await _localContext.Db.Ado.RollbackTranAsync();
                    throw new Exception("DomesticProduct 同步事务失败", exTran);
                }
                finally
                {
                    _localContext.Db.Ado.CommandTimeOut = prevTimeout;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] DomesticProduct 同步异常");
                result.IsSuccess = false;
                result.Message = "DomesticProduct 同步异常";
            }
            finally
            {
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }
            return result;
        }

        /// <summary>
        /// 全量同步国内套装：HQ 国内套装信息 → 本地 DomesticSetProduct。
        /// 写入前过滤 ProductCode/SetProductNo 为空的数据。
        /// </summary>
        public async Task<SyncResult> SyncDomesticSetProductsFromHqAsync(
            int hqBatchSize = 50000,
            int writePageSize = 10000
        )
        {
            var result = new SyncResult { StartTime = DateTime.Now };
            try
            {
                if (!(await _hbSalesContext.TestConnectionAsync()))
                {
                    throw new Exception("HBSales 数据库连接失败");
                }
                await _localContext.Db.Ado.BeginTranAsync();
                try
                {
                    await _localContext
                        .Db.Deleteable<DomesticSetProduct>()
                        .AS("DomesticSetProduct")
                        .ExecuteCommandAsync();
                    var total = await _hbSalesContext
                        .Db.Queryable<CPT_DIC_商品套装信息表>()
                        .CountAsync();
                    var pages = (int)Math.Ceiling(total / (double)hqBatchSize);
                    var added = 0;
                    var errors = 0;
                    for (var page = 1; page <= pages; page++)
                    {
                        var skip = (page - 1) * hqBatchSize;
                        var batch = await _hbSalesContext
                            .Db.Queryable<CPT_DIC_商品套装信息表>()
                            .OrderBy(x => x.ID)
                            .Skip(skip)
                            .Take(hqBatchSize)
                            .ToListAsync();
                        if (!batch.Any())
                            continue;
                        var localBatch = _mapper
                            .Map<List<DomesticSetProduct>>(batch)
                            .Where(x =>
                                !string.IsNullOrWhiteSpace(x.ProductCode)
                                && !string.IsNullOrWhiteSpace(x.SetProductNo)
                            )
                            .ToList();
                        try
                        {
                            await _localContext
                                .Db.Fastest<DomesticSetProduct>()
                                .AS("DomesticSetProduct")
                                .PageSize(writePageSize)
                                .BulkCopyAsync(localBatch);
                            added += localBatch.Count;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(
                                ex,
                                "[ReactSync] DomesticSetProduct 批{Page} 插入失败 批大小{Size}",
                                page,
                                localBatch.Count
                            );
                            errors += localBatch.Count;
                            await Task.Delay(1500);
                        }
                        batch.Clear();
                        localBatch.Clear();
                    }
                    await _localContext.Db.Ado.CommitTranAsync();
                    result.AddedCount = added;
                    result.ErrorCount = errors;
                    result.IsSuccess = errors == 0;
                    result.Message =
                        errors == 0
                            ? "DomesticSetProduct 同步成功"
                            : "DomesticSetProduct 同步完成，但存在错误";
                }
                catch (Exception exTran)
                {
                    await _localContext.Db.Ado.RollbackTranAsync();
                    throw new Exception("DomesticSetProduct 同步事务失败", exTran);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] DomesticSetProduct 同步异常");
                result.IsSuccess = false;
                result.Message = "DomesticSetProduct 同步异常";
            }
            finally
            {
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }
            return result;
        }

        /// <summary>
        /// 全量同步货号前缀：HQ 前缀信息 → 本地 ProductPrefixCode。
        /// 写入前过滤 SupplierCode/PrefixName 为空的数据。
        /// </summary>
        public async Task<SyncResult> SyncProductPrefixCodesFromHqAsync(
            int hqBatchSize = 50000,
            int writePageSize = 10000
        )
        {
            var result = new SyncResult { StartTime = DateTime.Now };
            try
            {
                if (!(await _hbSalesContext.TestConnectionAsync()))
                {
                    throw new Exception("HBSales 数据库连接失败");
                }
                await _localContext.Db.Ado.BeginTranAsync();
                try
                {
                    await _localContext
                        .Db.Deleteable<ProductPrefixCode>()
                        .AS("ProductPrefixCode")
                        .ExecuteCommandAsync();
                    var total = await _hbSalesContext
                        .Db.Queryable<CPT_DIC_货号前缀信息表>()
                        .CountAsync();
                    var pages = (int)Math.Ceiling(total / (double)hqBatchSize);
                    var added = 0;
                    var errors = 0;
                    for (var page = 1; page <= pages; page++)
                    {
                        var skip = (page - 1) * hqBatchSize;
                        var batch = await _hbSalesContext
                            .Db.Queryable<CPT_DIC_货号前缀信息表>()
                            .OrderBy(x => x.ID)
                            .Skip(skip)
                            .Take(hqBatchSize)
                            .ToListAsync();
                        if (!batch.Any())
                            continue;
                        var localBatch = _mapper
                            .Map<List<ProductPrefixCode>>(batch)
                            .Where(x =>
                                !string.IsNullOrWhiteSpace(x.SupplierCode)
                                && !string.IsNullOrWhiteSpace(x.PrefixName)
                            )
                            .ToList();
                        try
                        {
                            await _localContext
                                .Db.Fastest<ProductPrefixCode>()
                                .AS("ProductPrefixCode")
                                .PageSize(writePageSize)
                                .BulkCopyAsync(localBatch);
                            added += localBatch.Count;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(
                                ex,
                                "[ReactSync] ProductPrefixCode 批{Page} 插入失败 批大小{Size}",
                                page,
                                localBatch.Count
                            );
                            errors += localBatch.Count;
                            await Task.Delay(1500);
                        }
                        batch.Clear();
                        localBatch.Clear();
                    }
                    await _localContext.Db.Ado.CommitTranAsync();
                    result.AddedCount = added;
                    result.ErrorCount = errors;
                    result.IsSuccess = errors == 0;
                    result.Message =
                        errors == 0
                            ? "ProductPrefixCode 同步成功"
                            : "ProductPrefixCode 同步完成，但存在错误";
                }
                catch (Exception exTran)
                {
                    await _localContext.Db.Ado.RollbackTranAsync();
                    throw new Exception("ProductPrefixCode 同步事务失败", exTran);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] ProductPrefixCode 同步异常");
                result.IsSuccess = false;
                result.Message = "ProductPrefixCode 同步异常";
            }
            finally
            {
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }
            return result;
        }

        /// <summary>
        /// 全量同步国内供应商：HQ 供应商信息 → 本地 ChinaSupplier。
        /// 分页拉取、批量写入，统一日志汇总。
        /// </summary>
        public async Task<SyncResult> SyncChinaSuppliersFromHqAsync(
            int hqBatchSize = 50000,
            int writePageSize = 10000
        )
        {
            var result = new SyncResult { StartTime = DateTime.Now };
            try
            {
                if (!(await _hbSalesContext.TestConnectionAsync()))
                {
                    throw new Exception("HBSales 数据库连接失败");
                }
                await _localContext.Db.Ado.BeginTranAsync();
                try
                {
                    await _localContext
                        .Db.Deleteable<ChinaSupplier>()
                        .AS("ChinaSupplier")
                        .ExecuteCommandAsync();
                    var total = await _hbSalesContext
                        .Db.Queryable<CBP_DIC_国内供应商信息表>()
                        .CountAsync();
                    var pages = (int)Math.Ceiling(total / (double)hqBatchSize);
                    var added = 0;
                    var errors = 0;
                    for (var page = 1; page <= pages; page++)
                    {
                        var skip = (page - 1) * hqBatchSize;
                        var batch = await _hbSalesContext
                            .Db.Queryable<CBP_DIC_国内供应商信息表>()
                            .OrderBy(x => x.ID)
                            .Skip(skip)
                            .Take(hqBatchSize)
                            .ToListAsync();
                        if (!batch.Any())
                            continue;
                        var localBatch = _mapper.Map<List<ChinaSupplier>>(batch);
                        try
                        {
                            await _localContext
                                .Db.Fastest<ChinaSupplier>()
                                .AS("ChinaSupplier")
                                .PageSize(writePageSize)
                                .BulkCopyAsync(localBatch);
                            added += localBatch.Count;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(
                                ex,
                                "[ReactSync] ChinaSupplier 批{Page} 插入失败 批大小{Size}",
                                page,
                                localBatch.Count
                            );
                            errors += localBatch.Count;
                            await Task.Delay(1500);
                        }
                        batch.Clear();
                        localBatch.Clear();
                    }
                    await _localContext.Db.Ado.CommitTranAsync();
                    result.AddedCount = added;
                    result.ErrorCount = errors;
                    result.IsSuccess = errors == 0;
                    result.Message =
                        errors == 0
                            ? "ChinaSupplier 同步成功"
                            : "ChinaSupplier 同步完成，但存在错误";
                }
                catch (Exception exTran)
                {
                    await _localContext.Db.Ado.RollbackTranAsync();
                    throw new Exception("ChinaSupplier 同步事务失败", exTran);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] ChinaSupplier 同步异常");
                result.IsSuccess = false;
                result.Message = "ChinaSupplier 同步异常";
            }
            finally
            {
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }
            return result;
        }

        /// <summary>
        /// 全量同步仓库分类码：HQ 分类码表 → 本地 WarehouseCategory。
        /// 分页拉取、批量写入。
        /// </summary>
        public async Task<SyncResult> SyncWarehouseCategoriesFromHqAsync(
            int hqBatchSize = 50000,
            int writePageSize = 10000
        )
        {
            var result = new SyncResult { StartTime = DateTime.Now };
            try
            {
                _hqContext.CheckConnection();
                await _localContext.Db.Ado.BeginTranAsync();
                try
                {
                    await _localContext
                        .Db.Deleteable<WarehouseCategory>()
                        .AS("WarehouseCategory")
                        .ExecuteCommandAsync();
                    var hqDb = HqSqlSugarContext.CreateConcurrentConnection(_configuration);
                    var total = await hqDb.Queryable<CBP_DIC_商品分类码表>().CountAsync();
                    var pages = (int)Math.Ceiling(total / (double)hqBatchSize);
                    var added = 0;
                    var errors = 0;
                    for (var page = 1; page <= pages; page++)
                    {
                        var skip = (page - 1) * hqBatchSize;
                        var batch = await hqDb.Queryable<CBP_DIC_商品分类码表>()
                            .OrderBy(x => x.ID)
                            .Skip(skip)
                            .Take(hqBatchSize)
                            .ToListAsync();
                        if (!batch.Any())
                            continue;
                        var localBatch = _mapper.Map<List<WarehouseCategory>>(batch);
                        try
                        {
                            await _localContext
                                .Db.Fastest<WarehouseCategory>()
                                .AS("WarehouseCategory")
                                .PageSize(writePageSize)
                                .BulkCopyAsync(localBatch);
                            added += localBatch.Count;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(
                                ex,
                                "[ReactSync] WarehouseCategory 批{Page} 插入失败 批大小{Size}",
                                page,
                                localBatch.Count
                            );
                            errors += localBatch.Count;
                            await Task.Delay(1500);
                        }
                        batch.Clear();
                        localBatch.Clear();
                    }
                    hqDb.Dispose();
                    await _localContext.Db.Ado.CommitTranAsync();
                    // 全量同步会先清空 WarehouseCategory，事务成功后必须清理移动端分类树缓存。
                    WarehouseCategoryReactService.InvalidateTreeCache(_cache);
                    result.AddedCount = added;
                    result.ErrorCount = errors;
                    result.IsSuccess = errors == 0;
                    result.Message =
                        errors == 0
                            ? "WarehouseCategory 同步成功"
                            : "WarehouseCategory 同步完成，但存在错误";
                }
                catch (Exception exTran)
                {
                    await _localContext.Db.Ado.RollbackTranAsync();
                    throw new Exception("WarehouseCategory 同步事务失败", exTran);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] WarehouseCategory 同步异常");
                result.IsSuccess = false;
                result.Message = "WarehouseCategory 同步异常";
            }
            finally
            {
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }
            return result;
        }

        /// <summary>
        /// 全量同步货柜详情：HQ 详情表 → 本地 ContainerDetail（不派生主表 Container）。
        /// 支持主表GUID筛选；HQ 读取并发、本地事务单 writer 写入。
        ///
        /// 架构：
        ///   生产者(N×SemaphoreSlim=4) ──→ Channel&lt;List&lt;T&gt;&gt; ──→ 单事务 writer(1)
        ///         HQ 分页查询               有界背压(8)            分块 BulkCopy
        /// </summary>
        public async Task<SyncResult> SyncContainerDetailsFromHqAsync(
            List<string>? masterGuids = null
        )
        {
            var result = new SyncResult { StartTime = DateTime.Now };
            Console.WriteLine("📦 [货柜详情同步] ===== 开始全量同步货柜详情 =====");

            var taskLog = await _taskLogService.LogTaskStartAsync(
                TaskType.SyncContainerDetails,
                new TaskParameters
                {
                    CustomParameters = new Dictionary<string, object>
                    {
                        ["MasterGuids"] = masterGuids ?? new List<string>(),
                    },
                },
                TaskTrigger.Manual
            );
            ISqlSugarClient? mutationLockDb = null;
            var mutationLockTransactionStarted = false;
            try
            {
                _hqContext.CheckConnection();
                Console.WriteLine("✅ [货柜详情同步] HQ连接检查通过");

                // 全量替换必须把总锁、TRUNCATE 和全部写入绑定到同一个客户端事务；
                // HQ 读取可以并行，但本地库只能由一个 writer 顺序写入。
                mutationLockDb = SqlSugarContext.CreateConcurrentConnection(_configuration);
                await mutationLockDb.Ado.BeginTranAsync();
                mutationLockTransactionStarted = true;
                await ContainerMutationLock.AcquireAllAsync(mutationLockDb);

                var deleteStart = DateTime.Now;
                await mutationLockDb.Ado.ExecuteCommandAsync("TRUNCATE TABLE ContainerDetail");
                Console.WriteLine(
                    $"🗑️ [货柜详情同步] 本地 ContainerDetail 表已清空，耗时 {(DateTime.Now - deleteStart).TotalSeconds:F1}s"
                );

                const int batchSize = 50000;
                const int producerConcurrency = 4;
                const int chunkSize = 20000;
                const int writePageSize = 10000;

                var hasFilter = masterGuids != null && masterGuids.Count > 0;

                using var hqCountDb = HqSqlSugarContext.CreateConcurrentConnection(_configuration);
                var totalCount = await hqCountDb
                    .Queryable<CPT_RED_货柜单详情表Store>()
                    .Where(x => !string.IsNullOrEmpty(x.主表GUID))
                    .WhereIF(hasFilter, x => SqlFunc.ContainsArray(masterGuids!, x.主表GUID))
                    .CountAsync();
                var pageCount = (int)Math.Ceiling(totalCount / (double)batchSize);
                Console.WriteLine(
                    $"📊 [货柜详情同步] HQ总数据量: {totalCount:N0} 条, 分页数: {pageCount}, 每批: {batchSize:N0} 条"
                );
                Console.WriteLine(
                    $"⚙️ [货柜详情同步] 生产者并发: {producerConcurrency}, 本地单写入器: 1, 分块: {chunkSize:N0}, 写入分页: {writePageSize:N0}"
                );

                var channel = System.Threading.Channels.Channel.CreateBounded<
                    List<ContainerDetail>
                >(capacity: producerConcurrency * 2);

                var totalAdded = 0;
                using var writerFailure = new CancellationTokenSource();
                mutationLockDb.Ado.CommandTimeOut = _configuration.GetValue<int>(
                    "Database:BulkCopyCommandTimeoutSeconds",
                    600
                );
                var writer = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (
                            var batch in channel.Reader.ReadAllAsync(writerFailure.Token)
                        )
                        {
                            for (int offset = 0; offset < batch.Count; offset += chunkSize)
                            {
                                var localBatch = batch.Skip(offset).Take(chunkSize).ToList();
                                await mutationLockDb
                                    .Fastest<ContainerDetail>()
                                    .AS("ContainerDetail")
                                    .PageSize(writePageSize)
                                    .BulkCopyAsync(localBatch);
                                totalAdded += localBatch.Count;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // writer 失败时主动关闭管道并取消仍在等待背压的生产者，避免事务卡死。
                        channel.Writer.TryComplete(ex);
                        writerFailure.Cancel();
                        throw;
                    }
                });

                var semaphore = new SemaphoreSlim(producerConcurrency, producerConcurrency);
                _logger.LogInformation(
                    "[ReactSync] 货柜详情并发读取初始化：Total={Total}, Pages={Pages}, BatchSize={Batch}, Producers={Producers}, Consumers={Consumers}",
                    totalCount,
                    pageCount,
                    batchSize,
                    producerConcurrency,
                    1
                );

                var producers = new List<Task>();
                for (var page = 1; page <= pageCount; page++)
                {
                    var pageIndex = page;
                    var p = Task.Run(async () =>
                    {
                        var semaphoreAcquired = false;
                        try
                        {
                            await semaphore.WaitAsync(writerFailure.Token);
                            semaphoreAcquired = true;
                            writerFailure.Token.ThrowIfCancellationRequested();

                            var skip = (pageIndex - 1) * batchSize;
                            using var hqDb = HqSqlSugarContext.CreateConcurrentConnection(
                                _configuration
                            );
                            var hqBatch = await hqDb.Queryable<CPT_RED_货柜单详情表Store>()
                                .Where(x => !string.IsNullOrEmpty(x.主表GUID))
                                .WhereIF(
                                    hasFilter,
                                    x => SqlFunc.ContainsArray(masterGuids!, x.主表GUID)
                                )
                                .OrderBy(x => x.ID)
                                .Skip(skip)
                                .Take(batchSize)
                                .ToListAsync();

                            if (hqBatch.Any())
                            {
                                Console.WriteLine(
                                    $"📥 [货柜详情同步] 生产者查询第{pageIndex}/{pageCount}批: {hqBatch.Count:N0} 条"
                                );
                                _logger.LogInformation(
                                    "[ReactSync] 货柜详情第{Page}批：{Count}",
                                    pageIndex,
                                    hqBatch.Count
                                );
                                var localBatch = _mapper
                                    .Map<List<ContainerDetail>>(hqBatch)
                                    .Where(x => !string.IsNullOrWhiteSpace(x.ContainerCode))
                                    .ToList();
                                await channel.Writer.WriteAsync(localBatch, writerFailure.Token);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(
                                ex,
                                "[ReactSync] 货柜详情第{Page}批查询/映射失败",
                                pageIndex
                            );
                            Console.WriteLine(
                                $"❌ [货柜详情同步] 第{pageIndex}批查询失败: {ex.Message}"
                            );
                            // 读取或映射失败不能静默跳过，否则 TRUNCATE 后会提交不完整数据。
                            throw;
                        }
                        finally
                        {
                            if (semaphoreAcquired)
                            {
                                semaphore.Release();
                            }
                        }
                    });
                    producers.Add(p);
                }

                Exception? producerFailure = null;
                Exception? writerException = null;
                try
                {
                    await Task.WhenAll(producers);
                    Console.WriteLine("📥 [货柜详情同步] 所有生产者已完成，关闭 Channel");
                }
                catch (Exception ex)
                {
                    producerFailure = ex;
                    writerFailure.Cancel();
                    channel.Writer.TryComplete(ex);
                }
                finally
                {
                    // 无论生产者成功或失败，都结束 writer；失败路径由外层统一回滚事务。
                    channel.Writer.TryComplete();
                    try
                    {
                        await writer;
                    }
                    catch (Exception ex)
                    {
                        writerException = ex;
                    }
                }

                // writer 的真实写入异常优先于其触发的生产者取消异常，确保死锁/锁超时仍可被外层识别。
                if (writerException != null
                    && (
                        producerFailure == null
                        || (
                            writerException is not OperationCanceledException
                            && writerException
                                is not System.Threading.Channels.ChannelClosedException
                        )
                    )
                )
                {
                    ExceptionDispatchInfo.Capture(writerException).Throw();
                }

                if (producerFailure != null)
                {
                    ExceptionDispatchInfo.Capture(producerFailure).Throw();
                }

                if (writerException != null)
                {
                    ExceptionDispatchInfo.Capture(writerException).Throw();
                }
                Console.WriteLine("📤 [货柜详情同步] 本地单写入器已完成");

                await mutationLockDb.Ado.CommitTranAsync();
                mutationLockTransactionStarted = false;

                result.AddedCount = totalAdded;
                result.ErrorCount = 0;
                result.IsSuccess = true;
                result.Message = "ContainerDetail 同步成功";

                Console.WriteLine(
                    $"📊 [货柜详情同步] 同步完成: 新增 {totalAdded:N0} 条"
                );
                Console.WriteLine(
                    $"⏱️ [货柜详情同步] 总耗时: {DateTime.Now - result.StartTime:hh\\:mm\\:ss}"
                );
            }
            catch (Exception ex)
            {
                if (mutationLockTransactionStarted && mutationLockDb?.Ado.Transaction != null)
                {
                    await RollbackContainerMutationTransactionSafelyAsync(mutationLockDb, ex);
                    mutationLockTransactionStarted = false;
                }
                _logger.LogError(ex, "[ReactSync] ContainerDetail 同步异常");
                result.IsSuccess = false;
                result.Message = "ContainerDetail 同步异常";
                if (ContainerMutationLock.TryResolveConflict(ex, out _))
                {
                    result.ErrorCode = ContainerMutationLock.BusyErrorCode;
                }
                Console.WriteLine($"💥 [货柜详情同步] 同步异常: {ex.Message}");
                await _taskLogService.LogTaskFailureAsync(taskLog.Id, ex.Message);
            }
            finally
            {
                if (mutationLockTransactionStarted && mutationLockDb?.Ado.Transaction != null)
                {
                    await mutationLockDb.Ado.RollbackTranAsync();
                }
                mutationLockDb?.Dispose();
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }
            if (result.IsSuccess)
            {
                await _taskLogService.LogTaskSuccessAsync(taskLog.Id);
            }
            return result;
        }

        /// <summary>
        /// 全量同步货柜主表：HQ `CPT_RED_货柜单主表` → 本地 `Container`
        /// 流程：检查连接 → 事务清理 → 分页读取HQ → AutoMapper映射 → 批量写入 → 汇总日志。
        /// 约束：仅同步 HGUID 非空的主表记录，以保证与详情的主表GUID一致性。
        /// </summary>
        public async Task<SyncResult> SyncContainersFromHqAsync(
            int hqBatchSize = 50000,
            int writePageSize = 10000
        )
        {
            var result = new SyncResult { StartTime = DateTime.Now };
            var taskLog = await _taskLogService.LogTaskStartAsync(
                TaskType.SyncContainers,
                new TaskParameters(),
                TaskTrigger.Manual
            );
            try
            {
                _hqContext.CheckConnection();
                await _localContext.Db.Ado.BeginTranAsync();
                try
                {
                    await ContainerMutationLock.AcquireAllAsync(_localContext.Db);
                    await _localContext
                        .Db.Deleteable<Container>()
                        .AS("Container")
                        .ExecuteCommandAsync();
                    var hqDb = HqSqlSugarContext.CreateConcurrentConnection(_configuration);
                    var total = await hqDb.Queryable<CPT_RED_货柜单主表Store>()
                        .Where(x => !string.IsNullOrEmpty(x.HGUID))
                        .CountAsync();
                    var pages = (int)Math.Ceiling(total / (double)hqBatchSize);
                    var added = 0;
                    var errors = 0;

                    for (var page = 1; page <= pages; page++)
                    {
                        var skip = (page - 1) * hqBatchSize;
                        var batch = await hqDb.Queryable<CPT_RED_货柜单主表Store>()
                            .Where(x => !string.IsNullOrEmpty(x.HGUID))
                            .OrderBy(x => x.ID)
                            .Skip(skip)
                            .Take(hqBatchSize)
                            .ToListAsync();
                        if (!batch.Any())
                            continue;

                        var localBatch = _mapper
                            .Map<List<Container>>(batch)
                            .Where(x => !string.IsNullOrWhiteSpace(x.ContainerCode))
                            .ToList();
                        try
                        {
                            await _localContext
                                .Db.Fastest<Container>()
                                .AS("Container")
                                .PageSize(writePageSize)
                                .BulkCopyAsync(localBatch);
                            added += localBatch.Count;
                        }
                        catch (Exception ex)
                            when (!ContainerMutationLock.TryResolveConflict(ex, out _))
                        {
                            _logger.LogError(
                                ex,
                                "[ReactSync] Container 批{Page} 插入失败 批大小{Size}",
                                page,
                                localBatch.Count
                            );
                            errors += localBatch.Count;
                            await Task.Delay(1500);
                        }

                        batch.Clear();
                        localBatch.Clear();
                    }
                    hqDb.Dispose();
                    await _localContext.Db.Ado.CommitTranAsync();
                    result.AddedCount = added;
                    result.ErrorCount = errors;
                    result.IsSuccess = errors == 0;
                    result.Message =
                        errors == 0 ? "Container 同步成功" : "Container 同步完成，但存在错误";
                }
                catch (Exception exTran)
                {
                    await RollbackContainerMutationTransactionSafelyAsync(
                        _localContext.Db,
                        exTran
                    );
                    throw new Exception("Container 同步事务失败", exTran);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] Container 同步异常");
                result.IsSuccess = false;
                result.Message = "Container 同步异常";
                if (ContainerMutationLock.TryResolveConflict(ex, out _))
                {
                    result.ErrorCode = ContainerMutationLock.BusyErrorCode;
                }
                await _taskLogService.LogTaskFailureAsync(taskLog.Id, ex.Message);
            }
            finally
            {
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }
            if (result.IsSuccess)
            {
                await _taskLogService.LogTaskSuccessAsync(taskLog.Id);
            }
            return result;
        }

        private async Task RollbackContainerMutationTransactionSafelyAsync(
            ISqlSugarClient db,
            Exception originalException
        )
        {
            if (db.Ado.Transaction == null)
            {
                return;
            }

            try
            {
                await db.Ado.RollbackTranAsync();
            }
            catch (Exception rollbackException)
            {
                // 回滚失败不能覆盖 1205 或应用锁繁忙，否则控制器无法返回 409。
                // SQL Server 终止事务后，SqlSugar 可能仍持有失效事务；复位后才能安全释放连接。
                ContainerMutationLock.ResetFailedTransaction(db);
                _logger.LogWarning(
                    rollbackException,
                    "货柜同步事务回滚失败，保留原始异常 {OriginalExceptionType}",
                    originalException.GetType().Name
                );
            }
        }

        /// <summary>
        /// 全量同步仓库商品：HQ `CBP_DIC_商品库存表` → 本地 `WarehouseProduct`
        /// 按商品编码新增/更新库存业务字段，保留本地库位、体积、装箱数等人工维护字段。
        /// </summary>
        public Task<SyncResult> SyncWarehouseProductsFromHqAsync(
            int hqBatchSize = 50000,
            int writePageSize = 10000
        ) => SyncWarehouseProductsFromHqAsync(hqBatchSize, writePageSize, null, null);

        public async Task<SyncResult> SyncWarehouseProductsFromHqAsync(
            int hqBatchSize,
            int writePageSize,
            string? actorUserGuid,
            string? actorName
        )
        {
            var result = new SyncResult { StartTime = DateTime.Now };
            var auditActorName = !string.IsNullOrWhiteSpace(actorName)
                ? actorName.Trim()
                : !string.IsNullOrWhiteSpace(actorUserGuid)
                    ? actorUserGuid.Trim()
                    : "System";
            try
            {
                _hqContext.CheckConnection();
                var total = await _hqContext
                    .Db.Queryable<CBP_DIC_商品库存表>()
                    .Where(x => SqlFunc.HasValue(x.H商品编码))
                    .CountAsync();
                var pages = (int)Math.Ceiling(total / (double)hqBatchSize);
                var existingRows = await _localContext
                    .Db.Queryable<WarehouseProduct>()
                    .Select(x => new WarehouseMinOrderQuantitySyncGuard.LocalMinOrderQuantityRow
                    {
                        ProductCode = x.ProductCode,
                        MinOrderQuantity = x.MinOrderQuantity,
                    })
                    .ToListAsync();
                // 中包数由仓库本地维护：HQ 最小订货量只补本地空缺，已有正数的行更新时回填本地值。
                var localMinOrderQuantities =
                    WarehouseMinOrderQuantitySyncGuard.BuildLocalValues(existingRows);
                var existingCodeMap = existingRows
                    .Select(row => row.ProductCode ?? string.Empty)
                    .Select(code => new
                    {
                        Original = code,
                        Normalized = NormalizeWarehouseProductCode(code),
                    })
                    .Where(x => x.Normalized != null)
                    .GroupBy(x => x.Normalized!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.First().Original,
                        StringComparer.OrdinalIgnoreCase
                    );
                var initiallyExistingCodes = new HashSet<string>(
                    existingCodeMap.Keys,
                    StringComparer.OrdinalIgnoreCase
                );
                var countedAddedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var countedUpdatedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var batchAuditCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var batchBeforeSnapshots = new Dictionary<
                    string,
                    WarehouseProductChangeSnapshotDto
                >(StringComparer.OrdinalIgnoreCase);
                var added = 0;
                var updated = 0;
                var syncBatchGuid = Guid.NewGuid();
                var transactionResult = await _localContext.Db.Ado.UseTranAsync(async () =>
                {
                    for (var page = 1; page <= pages; page++)
                    {
                        var skip = (page - 1) * hqBatchSize;
                        var batch = await _hqContext
                            .Db.Queryable<CBP_DIC_商品库存表>()
                            .Where(x => SqlFunc.HasValue(x.H商品编码))
                            .OrderBy(x => x.ID)
                            .Skip(skip)
                            .Take(hqBatchSize)
                            .ToListAsync();
                        if (!batch.Any())
                            continue;

                        var localBatch = _mapper
                            .Map<List<WarehouseProduct>>(batch)
                            .Select(item => new
                            {
                                Item = item,
                                NormalizedCode = NormalizeWarehouseProductCode(item.ProductCode),
                            })
                            .Where(x => x.NormalizedCode != null)
                            .GroupBy(x => x.NormalizedCode!, StringComparer.OrdinalIgnoreCase)
                            .Select(group => group.Last())
                            .ToList();
                        if (!localBatch.Any())
                            continue;

                        var pageProductCodes = localBatch
                            .Select(entry => entry.NormalizedCode!)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        var lockScope = await SetChildPurchasePriceMutationLock.AcquireProductsAsync(
                            _localContext.Db,
                            pageProductCodes
                        );

                        var now = DateTime.UtcNow;
                        var auditProductCodes = localBatch
                            .Select(entry => entry.NormalizedCode!)
                            .ToList();
                        var firstSeenAuditCodes = auditProductCodes
                            .Where(code => batchAuditCodes.Add(code))
                            .ToList();
                        if (firstSeenAuditCodes.Count > 0)
                        {
                            var pageBeforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                                firstSeenAuditCodes
                            );
                            foreach (var snapshot in pageBeforeSnapshots)
                            {
                                batchBeforeSnapshots.TryAdd(snapshot.Key, snapshot.Value);
                            }
                        }
                        var toInsert = new List<WarehouseProduct>();
                        var toUpdate = new List<WarehouseProduct>();
                        foreach (var entry in localBatch)
                        {
                            var item = entry.Item;
                            var normalizedCode = entry.NormalizedCode!;
                            item.UpdatedAt = now;
                            item.UpdatedBy = auditActorName;
                            if (existingCodeMap.TryGetValue(normalizedCode, out var existingCode))
                            {
                                item.ProductCode = existingCode;
                                toUpdate.Add(item);
                            }
                            else
                            {
                                item.ProductCode = normalizedCode;
                                item.CreatedAt = now;
                                item.CreatedBy = auditActorName;
                                toInsert.Add(item);
                            }
                        }
                        WarehouseMinOrderQuantitySyncGuard.Apply(toUpdate, localMinOrderQuantities);

                        using (SqlSugarAuditScope.PreserveExplicitAuditFields())
                        {
                            if (toUpdate.Any())
                            {
                                // 只更新 HQ 库存业务字段，避免覆盖本地维护的体积、装箱数、库位等信息。
                                await _localContext
                                    .Db.Updateable(toUpdate)
                                    .AS("WarehouseProduct")
                                    .UpdateColumns(w => new
                                    {
                                        w.DomesticPrice,
                                        w.OEMPrice,
                                        w.ImportPrice,
                                        w.StockQuantity,
                                        w.MinOrderQuantity,
                                        w.StockValue,
                                        w.StockAlertQuantity,
                                        w.IsActive,
                                        w.UpdatedAt,
                                        w.UpdatedBy,
                                    })
                                    .ExecuteCommandAsync();
                                foreach (var item in toUpdate)
                                {
                                    var normalizedCode = NormalizeWarehouseProductCode(
                                        item.ProductCode
                                    );
                                    if (
                                        normalizedCode != null
                                        && initiallyExistingCodes.Contains(normalizedCode)
                                        && countedUpdatedCodes.Add(normalizedCode)
                                    )
                                    {
                                        updated++;
                                    }
                                }
                            }

                            if (toInsert.Any())
                            {
                                await _localContext
                                    .Db.Insertable(toInsert)
                                    .AS("WarehouseProduct")
                                    .PageSize(writePageSize)
                                    .ExecuteCommandAsync();
                                foreach (var item in toInsert)
                                {
                                    var normalizedCode = NormalizeWarehouseProductCode(
                                        item.ProductCode
                                    );
                                    if (normalizedCode == null)
                                        continue;
                                    existingCodeMap[normalizedCode] = item.ProductCode;
                                    if (countedAddedCodes.Add(normalizedCode))
                                    {
                                        added++;
                                    }
                                }
                            }
                        }

                        // ImportPrice 写入后立即在同一产品锁与事务内回写 Type1/Type2 成本。
                        await new SetChildPurchasePriceService(_localContext.Db).RecalculateLockedAsync(
                            lockScope,
                            pageProductCodes,
                            storeCodes: null,
                            auditActorName
                        );

                        _logger.LogInformation(
                            "[ReactSync] WarehouseProduct 全量更新页{Page}: 新增{Added}, 更新{Updated}",
                            page,
                            toInsert.Count,
                            toUpdate.Count
                        );
                    }

                    if (batchAuditCodes.Count > 0)
                    {
                        var afterSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                            batchAuditCodes
                        );
                        await _changeHistoryService.RecordChangesAsync(
                            batchBeforeSnapshots,
                            afterSnapshots,
                            CreateSystemSyncContext(
                                "DataSyncFull",
                                syncBatchGuid,
                                DateTime.UtcNow,
                                actorUserGuid,
                                auditActorName
                            )
                        );
                    }
                });

                if (!transactionResult.IsSuccess)
                {
                    _logger.LogError(
                        transactionResult.ErrorException,
                        "[ReactSync] WarehouseProduct 全量同步事务失败"
                    );
                    result.IsSuccess = false;
                    if (
                        SetChildPurchasePriceMutationLock.TryResolveConflict(
                            transactionResult.ErrorException,
                            out var conflict
                        )
                    )
                    {
                        result.ErrorCode = SetChildPurchasePriceMutationLock.BusyErrorCode;
                        result.BusyErrorCount = 1;
                        result.Message = conflict!.Message;
                    }
                    else
                    {
                        result.Message = "WarehouseProduct 同步事务失败";
                    }
                    result.ErrorCount = 1;
                    return result;
                }

                result.AddedCount = added;
                result.UpdatedCount = updated;
                result.ErrorCount = 0;
                result.IsSuccess = true;
                result.Message = $"WarehouseProduct 同步成功，新增 {added} 条，更新 {updated} 条";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] WarehouseProduct 同步异常");
                result.IsSuccess = false;
                if (SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out var conflict))
                {
                    result.ErrorCode = SetChildPurchasePriceMutationLock.BusyErrorCode;
                    result.BusyErrorCount = 1;
                    result.Message = conflict!.Message;
                }
                else
                {
                    result.Message = "WarehouseProduct 同步异常";
                }
                result.ErrorCount = 1;
            }
            finally
            {
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }
            return result;
        }

        private static string? NormalizeWarehouseProductCode(string? productCode)
        {
            var normalized = productCode?.Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        public async Task<SyncResult> SyncStoreLocalSupplierInvoicesFromHqAsync(
            int hqBatchSize = 50000,
            int writePageSize = 10000
        )
        {
            var result = new SyncResult { StartTime = DateTime.Now };
            var taskLog = await _taskLogService.LogTaskStartAsync(
                TaskType.SyncStoreLocalSupplierInvoices,
                new TaskParameters(),
                TaskTrigger.Manual
            );
            try
            {
                _hqContext.CheckConnection();
                // 删除阶段使用事务，写入阶段取消全局事务，避免大事务中途失败导致后续批次“事务已完成”
                await _localContext.Db.Ado.UseTranAsync(async () =>
                {
                    await _localContext
                        .Db.Deleteable<StoreLocalSupplierInvoice>()
                        .AS("StoreLocalSupplierInvoice")
                        .ExecuteCommandAsync();
                });
                var total = await _hqContext
                    .Db.Queryable<RED_进货单主表Store>()
                    .Where(x => SqlFunc.HasValue(x.HGUID))
                    .CountAsync();
                var pages = (int)Math.Ceiling(total / (double)hqBatchSize);
                var added = 0;
                var errors = 0;
                for (var page = 1; page <= pages; page++)
                {
                    var skip = (page - 1) * hqBatchSize;
                    var batch = await _hqContext
                        .Db.Queryable<RED_进货单主表Store>()
                        .Where(x => SqlFunc.HasValue(x.HGUID))
                        .OrderBy(x => x.ID)
                        .Skip(skip)
                        .Take(hqBatchSize)
                        .ToListAsync();
                    if (!batch.Any())
                        continue;
                    var localBatch = _mapper
                        .Map<List<StoreLocalSupplierInvoice>>(batch)
                        .Where(x => !string.IsNullOrWhiteSpace(x.InvoiceGUID))
                        .ToList();
                    try
                    {
                        await _localContext
                            .Db.Fastest<StoreLocalSupplierInvoice>()
                            .AS("StoreLocalSupplierInvoice")
                            .PageSize(writePageSize)
                            .BulkCopyAsync(localBatch);
                        added += localBatch.Count;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "[ReactSync] StoreLocalSupplierInvoice 批{Page} 插入失败 批大小{Size}",
                            page,
                            localBatch.Count
                        );
                        errors += localBatch.Count;
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 2 * page)));
                    }
                    batch.Clear();
                    localBatch.Clear();
                }
                await _localContext.Db.Ado.CommitTranAsync();
                result.AddedCount = added;
                result.ErrorCount = errors;
                result.IsSuccess = errors == 0;
                result.Message =
                    errors == 0
                        ? "StoreLocalSupplierInvoice 同步成功"
                        : "StoreLocalSupplierInvoice 同步完成，但存在错误";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] StoreLocalSupplierInvoice 同步异常");
                result.IsSuccess = false;
                result.Message = "StoreLocalSupplierInvoice 同步异常";
                await _taskLogService.LogTaskFailureAsync(taskLog.Id, ex.Message);
            }
            finally
            {
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }
            if (result.IsSuccess)
            {
                await _taskLogService.LogTaskSuccessAsync(taskLog.Id);
            }
            return result;
        }

        /// <summary>
        /// 全量同步进货单详情：HQ → 本地 StoreLocalSupplierInvoiceDetails。
        /// Channel Pipeline 并发读写。
        ///
        /// 架构：
        ///   生产者(N×SemaphoreSlim=4) ──→ Channel&lt;List&lt;T&gt;&gt; ──→ 消费者(6并发)
        ///         HQ 分页查询               有界背压(12)           分块 BulkCopy
        /// </summary>
        public async Task<SyncResult> SyncStoreLocalSupplierInvoiceDetailsFromHqAsync()
        {
            var result = new SyncResult { StartTime = DateTime.Now };
            Console.WriteLine("📦 [进货详情同步] ===== 开始全量同步进货单详情 =====");

            var taskLog = await _taskLogService.LogTaskStartAsync(
                TaskType.SyncStoreLocalSupplierInvoiceDetails,
                new TaskParameters(),
                TaskTrigger.Manual
            );
            try
            {
                _hqContext.CheckConnection();
                Console.WriteLine("✅ [进货详情同步] HQ连接检查通过");

                var deleteStart = DateTime.Now;
                using var syncLocalDb = SqlSugarContext.CreateConcurrentConnection(_configuration);
                await syncLocalDb.Ado.ExecuteCommandAsync(
                    "TRUNCATE TABLE StoreLocalSupplierInvoiceDetails"
                );
                Console.WriteLine(
                    $"🗑️ [进货详情同步] 本地 StoreLocalSupplierInvoiceDetails 表已清空，耗时 {(DateTime.Now - deleteStart).TotalSeconds:F1}s"
                );

                const int batchSize = 50000;
                const int producerConcurrency = 4;
                const int consumerConcurrency = 6;
                const int chunkSize = 20000;
                const int writePageSize = 10000;

                using var hqCountDb = HqSqlSugarContext.CreateConcurrentConnection(_configuration);
                var totalCount = await hqCountDb
                    .Queryable<RED_进货单详情表Store>()
                    .Where(x => SqlFunc.HasValue(x.H主表GUID))
                    .CountAsync();
                var pageCount = (int)Math.Ceiling(totalCount / (double)batchSize);
                Console.WriteLine(
                    $"📊 [进货详情同步] HQ总数据量: {totalCount:N0} 条, 分页数: {pageCount}, 每批: {batchSize:N0} 条"
                );
                Console.WriteLine(
                    $"⚙️ [进货详情同步] 生产者并发: {producerConcurrency}, 消费者并发: {consumerConcurrency}, 分块: {chunkSize:N0}, 写入分页: {writePageSize:N0}"
                );

                var channel = System.Threading.Channels.Channel.CreateBounded<
                    List<StoreLocalSupplierInvoiceDetails>
                >(capacity: consumerConcurrency * 2);

                var totalAdded = 0;
                var totalErrors = 0;
                var fetchErrors = 0;

                var consumers = new List<Task>();
                for (int i = 0; i < consumerConcurrency; i++)
                {
                    var consumerIdx = i;
                    var c = Task.Run(async () =>
                    {
                        using var consumerDb = SqlSugarContext.CreateConcurrentConnection(
                            _configuration
                        );
                        consumerDb.Ado.CommandTimeOut = _configuration.GetValue<int>(
                            "Database:BulkCopyCommandTimeoutSeconds",
                            600
                        );

                        await foreach (var batch in channel.Reader.ReadAllAsync())
                        {
                            for (int offset = 0; offset < batch.Count; offset += chunkSize)
                            {
                                var chunk = batch.Skip(offset).Take(chunkSize).ToList();
                                try
                                {
                                    await consumerDb
                                        .Fastest<StoreLocalSupplierInvoiceDetails>()
                                        .AS("StoreLocalSupplierInvoiceDetails")
                                        .PageSize(writePageSize)
                                        .BulkCopyAsync(chunk);
                                    System.Threading.Interlocked.Add(ref totalAdded, chunk.Count);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(
                                        ex,
                                        "[ReactSync] 进货详情消费者{Idx}写入失败（批大小:{Size}）",
                                        consumerIdx,
                                        chunk.Count
                                    );
                                    System.Threading.Interlocked.Add(ref totalErrors, chunk.Count);
                                    Console.WriteLine(
                                        $"❌ [进货详情同步] 消费者{consumerIdx}写入失败: {chunk.Count} 条, 错误: {ex.Message}"
                                    );
                                    await Task.Delay(1500);
                                }
                            }
                        }
                    });
                    consumers.Add(c);
                }
                Console.WriteLine(
                    $"🚀 [进货详情同步] 已启动 {consumerConcurrency} 个消费者，等待数据..."
                );

                var semaphore = new SemaphoreSlim(producerConcurrency, producerConcurrency);
                _logger.LogInformation(
                    "[ReactSync] 进货详情并发读取初始化：Total={Total}, Pages={Pages}, BatchSize={Batch}, Producers={Producers}, Consumers={Consumers}",
                    totalCount,
                    pageCount,
                    batchSize,
                    producerConcurrency,
                    consumerConcurrency
                );

                var producers = new List<Task>();
                for (var page = 1; page <= pageCount; page++)
                {
                    var pageIndex = page;
                    var p = Task.Run(async () =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            var skip = (pageIndex - 1) * batchSize;
                            using var hqDb = HqSqlSugarContext.CreateConcurrentConnection(
                                _configuration
                            );
                            var hqBatch = await hqDb.Queryable<RED_进货单详情表Store>()
                                .Where(x => SqlFunc.HasValue(x.H主表GUID))
                                .OrderBy(x => x.ID)
                                .Skip(skip)
                                .Take(batchSize)
                                .ToListAsync();

                            if (hqBatch.Any())
                            {
                                Console.WriteLine(
                                    $"📥 [进货详情同步] 生产者查询第{pageIndex}/{pageCount}批: {hqBatch.Count:N0} 条"
                                );
                                _logger.LogInformation(
                                    "[ReactSync] 进货详情第{Page}批：{Count}",
                                    pageIndex,
                                    hqBatch.Count
                                );
                                var localBatch = _mapper
                                    .Map<List<StoreLocalSupplierInvoiceDetails>>(hqBatch)
                                    .Where(x =>
                                        !string.IsNullOrWhiteSpace(x.DetailGUID)
                                        && !string.IsNullOrWhiteSpace(x.InvoiceGUID)
                                    )
                                    .ToList();
                                await channel.Writer.WriteAsync(localBatch);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(
                                ex,
                                "[ReactSync] 进货详情第{Page}批查询/映射失败",
                                pageIndex
                            );
                            System.Threading.Interlocked.Increment(ref fetchErrors);
                            Console.WriteLine(
                                $"❌ [进货详情同步] 第{pageIndex}批查询失败: {ex.Message}"
                            );
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    });
                    producers.Add(p);
                }

                await Task.WhenAll(producers);
                Console.WriteLine("📥 [进货详情同步] 所有生产者已完成，关闭 Channel");
                channel.Writer.Complete();
                await Task.WhenAll(consumers);
                Console.WriteLine("📤 [进货详情同步] 所有消费者已完成");

                result.AddedCount = totalAdded;
                result.ErrorCount = totalErrors + fetchErrors;
                result.IsSuccess = (totalErrors == 0 && fetchErrors == 0);
                result.Message =
                    (totalErrors == 0 && fetchErrors == 0)
                        ? "StoreLocalSupplierInvoiceDetails 同步成功"
                        : "StoreLocalSupplierInvoiceDetails 同步完成，但存在错误";

                Console.WriteLine(
                    $"📊 [进货详情同步] 同步完成: 新增 {totalAdded:N0} 条, 写入错误 {totalErrors:N0} 条, 查询错误 {fetchErrors} 批"
                );
                Console.WriteLine(
                    $"⏱️ [进货详情同步] 总耗时: {DateTime.Now - result.StartTime:hh\\:mm\\:ss}"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] StoreLocalSupplierInvoiceDetails 同步异常");
                result.IsSuccess = false;
                result.Message = "StoreLocalSupplierInvoiceDetails 同步异常";
                Console.WriteLine($"💥 [进货详情同步] 同步异常: {ex.Message}");
                await _taskLogService.LogTaskFailureAsync(taskLog.Id, ex.Message);
            }
            finally
            {
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }
            if (result.IsSuccess)
            {
                await _taskLogService.LogTaskSuccessAsync(taskLog.Id);
            }
            return result;
        }

        public async Task<SyncResult> SyncStoreLocalSupplierInvoicesAndDetailsFromHqAsync(
            int hqBatchSize = 50000,
            int writePageSize = 10000
        )
        {
            var result = new SyncResult { StartTime = DateTime.Now };
            var taskLog = await _taskLogService.LogTaskStartAsync(
                TaskType.SyncStoreLocalSupplierInvoicesAll,
                new TaskParameters(),
                TaskTrigger.Manual
            );
            try
            {
                var mainResult = await SyncStoreLocalSupplierInvoicesFromHqAsync(
                    hqBatchSize,
                    writePageSize
                );
                var detailResult = await SyncStoreLocalSupplierInvoiceDetailsFromHqAsync();
                result = new SyncResult
                {
                    StartTime = mainResult.StartTime,
                    EndTime = detailResult.EndTime,
                    Duration = detailResult.EndTime - mainResult.StartTime,
                    AddedCount = (mainResult.AddedCount) + (detailResult.AddedCount),
                    ErrorCount = (mainResult.ErrorCount) + (detailResult.ErrorCount),
                    IsSuccess = (mainResult.IsSuccess && detailResult.IsSuccess),
                    Message =
                        (mainResult.IsSuccess && detailResult.IsSuccess)
                            ? "主+详情同步成功"
                            : "主+详情同步完成，但存在错误",
                };
                await _taskLogService.LogTaskSuccessAsync(taskLog.Id);
                return result;
            }
            catch (Exception ex)
            {
                result.IsSuccess = false;
                result.Message = "主+详情同步异常";
                await _taskLogService.LogTaskFailureAsync(taskLog.Id, ex.Message);
                return result;
            }
        }

        public async Task<SyncResult> SyncWareHouseOrdersFromHqAsync(
            int hqBatchSize = 50000,
            int writePageSize = 10000
        )
        {
            var result = new SyncResult { StartTime = DateTime.Now };
            var taskLog = await _taskLogService.LogTaskStartAsync(
                TaskType.SyncWareHouseOrders,
                new TaskParameters(),
                TaskTrigger.Manual
            );
            try
            {
                _hqContext.CheckConnection();
                await _localContext.Db.Ado.UseTranAsync(async () =>
                {
                    await _localContext
                        .Db.Deleteable<WareHouseOrder>()
                        .AS("WareHouseOrder")
                        .ExecuteCommandAsync();
                });
                var total = await _hqContext
                    .Db.Queryable<CBP_RED_分店订货单主表Store>()
                    .Where(x => SqlFunc.HasValue(x.HGUID))
                    .CountAsync();
                var pages = (int)Math.Ceiling(total / (double)hqBatchSize);
                var added = 0;
                var errors = 0;
                for (var page = 1; page <= pages; page++)
                {
                    var skip = (page - 1) * hqBatchSize;
                    var batch = await _hqContext
                        .Db.Queryable<CBP_RED_分店订货单主表Store>()
                        .Where(x => SqlFunc.HasValue(x.HGUID))
                        .OrderBy(x => x.ID)
                        .Skip(skip)
                        .Take(hqBatchSize)
                        .ToListAsync();
                    if (!batch.Any())
                        continue;
                    var localBatch = _mapper
                        .Map<List<WareHouseOrder>>(batch)
                        .Where(x => !string.IsNullOrWhiteSpace(x.OrderGUID))
                        .ToList();
                    try
                    {
                        await _localContext
                            .Db.Fastest<WareHouseOrder>()
                            .AS("WareHouseOrder")
                            .PageSize(writePageSize)
                            .BulkCopyAsync(localBatch);
                        added += localBatch.Count;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "[ReactSync] WareHouseOrder 批{Page} 插入失败 批大小{Size}",
                            page,
                            localBatch.Count
                        );
                        errors += localBatch.Count;
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 2 * page)));
                    }
                    batch.Clear();
                    localBatch.Clear();
                }
                result.AddedCount = added;
                result.ErrorCount = errors;
                result.IsSuccess = errors == 0;
                result.Message =
                    errors == 0 ? "WareHouseOrder 同步成功" : "WareHouseOrder 同步完成，但存在错误";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] WareHouseOrder 同步异常");
                result.IsSuccess = false;
                result.Message = "WareHouseOrder 同步异常";
                await _taskLogService.LogTaskFailureAsync(taskLog.Id, ex.Message);
            }
            finally
            {
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }
            if (result.IsSuccess)
            {
                await _taskLogService.LogTaskSuccessAsync(taskLog.Id);
            }
            return result;
        }

        /// <summary>
        /// 全量同步订货单详情：HQ → 本地 WareHouseOrderDetails。
        /// Channel Pipeline 并发读写。
        ///
        /// 架构：
        ///   生产者(N×SemaphoreSlim=4) ──→ Channel&lt;List&lt;T&gt;&gt; ──→ 消费者(6并发)
        ///         HQ 分页查询               有界背压(12)           分块 BulkCopy
        /// </summary>
        public async Task<SyncResult> SyncWareHouseOrderDetailsFromHqAsync()
        {
            var result = new SyncResult { StartTime = DateTime.Now };
            Console.WriteLine("📦 [订货详情同步] ===== 开始全量同步订货单详情 =====");

            var taskLog = await _taskLogService.LogTaskStartAsync(
                TaskType.SyncWareHouseOrderDetails,
                new TaskParameters(),
                TaskTrigger.Manual
            );
            try
            {
                _hqContext.CheckConnection();
                Console.WriteLine("✅ [订货详情同步] HQ连接检查通过");

                var deleteStart = DateTime.Now;
                using var syncLocalDb = SqlSugarContext.CreateConcurrentConnection(_configuration);
                await syncLocalDb.Ado.ExecuteCommandAsync("TRUNCATE TABLE WareHouseOrderDetails");
                Console.WriteLine(
                    $"🗑️ [订货详情同步] 本地 WareHouseOrderDetails 表已清空，耗时 {(DateTime.Now - deleteStart).TotalSeconds:F1}s"
                );

                const int batchSize = 50000;
                const int producerConcurrency = 4;
                const int consumerConcurrency = 6;
                const int chunkSize = 20000;
                const int writePageSize = 10000;

                using var hqCountDb = HqSqlSugarContext.CreateConcurrentConnection(_configuration);
                var totalCount = await hqCountDb
                    .Queryable<CBP_RED_分店订单详情表Store>()
                    .Where(x => SqlFunc.HasValue(x.主表GUID))
                    .CountAsync();
                var pageCount = (int)Math.Ceiling(totalCount / (double)batchSize);
                Console.WriteLine(
                    $"📊 [订货详情同步] HQ总数据量: {totalCount:N0} 条, 分页数: {pageCount}, 每批: {batchSize:N0} 条"
                );
                Console.WriteLine(
                    $"⚙️ [订货详情同步] 生产者并发: {producerConcurrency}, 消费者并发: {consumerConcurrency}, 分块: {chunkSize:N0}, 写入分页: {writePageSize:N0}"
                );

                var channel = System.Threading.Channels.Channel.CreateBounded<
                    List<WareHouseOrderDetails>
                >(capacity: consumerConcurrency * 2);

                var totalAdded = 0;
                var totalErrors = 0;
                var fetchErrors = 0;

                var consumers = new List<Task>();
                for (int i = 0; i < consumerConcurrency; i++)
                {
                    var consumerIdx = i;
                    var c = Task.Run(async () =>
                    {
                        using var consumerDb = SqlSugarContext.CreateConcurrentConnection(
                            _configuration
                        );
                        consumerDb.Ado.CommandTimeOut = _configuration.GetValue<int>(
                            "Database:BulkCopyCommandTimeoutSeconds",
                            600
                        );

                        await foreach (var batch in channel.Reader.ReadAllAsync())
                        {
                            for (int offset = 0; offset < batch.Count; offset += chunkSize)
                            {
                                var chunk = batch.Skip(offset).Take(chunkSize).ToList();
                                try
                                {
                                    await consumerDb
                                        .Fastest<WareHouseOrderDetails>()
                                        .AS("WareHouseOrderDetails")
                                        .PageSize(writePageSize)
                                        .BulkCopyAsync(chunk);
                                    System.Threading.Interlocked.Add(ref totalAdded, chunk.Count);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(
                                        ex,
                                        "[ReactSync] 订货详情消费者{Idx}写入失败（批大小:{Size}）",
                                        consumerIdx,
                                        chunk.Count
                                    );
                                    System.Threading.Interlocked.Add(ref totalErrors, chunk.Count);
                                    Console.WriteLine(
                                        $"❌ [订货详情同步] 消费者{consumerIdx}写入失败: {chunk.Count} 条, 错误: {ex.Message}"
                                    );
                                    await Task.Delay(1500);
                                }
                            }
                        }
                    });
                    consumers.Add(c);
                }
                Console.WriteLine(
                    $"🚀 [订货详情同步] 已启动 {consumerConcurrency} 个消费者，等待数据..."
                );

                var semaphore = new SemaphoreSlim(producerConcurrency, producerConcurrency);
                _logger.LogInformation(
                    "[ReactSync] 订货详情并发读取初始化：Total={Total}, Pages={Pages}, BatchSize={Batch}, Producers={Producers}, Consumers={Consumers}",
                    totalCount,
                    pageCount,
                    batchSize,
                    producerConcurrency,
                    consumerConcurrency
                );

                var producers = new List<Task>();
                for (var page = 1; page <= pageCount; page++)
                {
                    var pageIndex = page;
                    var p = Task.Run(async () =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            var skip = (pageIndex - 1) * batchSize;
                            using var hqDb = HqSqlSugarContext.CreateConcurrentConnection(
                                _configuration
                            );
                            var hqBatch = await hqDb.Queryable<CBP_RED_分店订单详情表Store>()
                                .Where(x => SqlFunc.HasValue(x.主表GUID))
                                .OrderBy(x => x.ID)
                                .Skip(skip)
                                .Take(batchSize)
                                .ToListAsync();

                            if (hqBatch.Any())
                            {
                                Console.WriteLine(
                                    $"📥 [订货详情同步] 生产者查询第{pageIndex}/{pageCount}批: {hqBatch.Count:N0} 条"
                                );
                                _logger.LogInformation(
                                    "[ReactSync] 订货详情第{Page}批：{Count}",
                                    pageIndex,
                                    hqBatch.Count
                                );
                                var localBatch = _mapper
                                    .Map<List<WareHouseOrderDetails>>(hqBatch)
                                    .Where(x =>
                                        !string.IsNullOrWhiteSpace(x.DetailGUID)
                                        && !string.IsNullOrWhiteSpace(x.OrderGUID)
                                    )
                                    .ToList();
                                await channel.Writer.WriteAsync(localBatch);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(
                                ex,
                                "[ReactSync] 订货详情第{Page}批查询/映射失败",
                                pageIndex
                            );
                            System.Threading.Interlocked.Increment(ref fetchErrors);
                            Console.WriteLine(
                                $"❌ [订货详情同步] 第{pageIndex}批查询失败: {ex.Message}"
                            );
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    });
                    producers.Add(p);
                }

                await Task.WhenAll(producers);
                Console.WriteLine("📥 [订货详情同步] 所有生产者已完成，关闭 Channel");
                channel.Writer.Complete();
                await Task.WhenAll(consumers);
                Console.WriteLine("📤 [订货详情同步] 所有消费者已完成");

                result.AddedCount = totalAdded;
                result.ErrorCount = totalErrors + fetchErrors;
                result.IsSuccess = (totalErrors == 0 && fetchErrors == 0);
                result.Message =
                    (totalErrors == 0 && fetchErrors == 0)
                        ? "WareHouseOrderDetails 同步成功"
                        : "WareHouseOrderDetails 同步完成，但存在错误";

                Console.WriteLine(
                    $"📊 [订货详情同步] 同步完成: 新增 {totalAdded:N0} 条, 写入错误 {totalErrors:N0} 条, 查询错误 {fetchErrors} 批"
                );
                Console.WriteLine(
                    $"⏱️ [订货详情同步] 总耗时: {DateTime.Now - result.StartTime:hh\\:mm\\:ss}"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] WareHouseOrderDetails 同步异常");
                result.IsSuccess = false;
                result.Message = "WareHouseOrderDetails 同步异常";
                Console.WriteLine($"💥 [订货详情同步] 同步异常: {ex.Message}");
                await _taskLogService.LogTaskFailureAsync(taskLog.Id, ex.Message);
            }
            finally
            {
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }
            if (result.IsSuccess)
            {
                await _taskLogService.LogTaskSuccessAsync(taskLog.Id);
            }
            return result;
        }

        public async Task<SyncResult> SyncWareHouseOrdersAllFromHqAsync(
            int hqBatchSize = 50000,
            int writePageSize = 10000
        )
        {
            var result = new SyncResult { StartTime = DateTime.Now };
            var taskLog = await _taskLogService.LogTaskStartAsync(
                TaskType.SyncWareHouseOrdersAll,
                new TaskParameters(),
                TaskTrigger.Manual
            );
            try
            {
                var main = await SyncWareHouseOrdersFromHqAsync(hqBatchSize, writePageSize);
                var det = await SyncWareHouseOrderDetailsFromHqAsync();
                result = new SyncResult
                {
                    StartTime = main.StartTime,
                    EndTime = det.EndTime,
                    Duration = det.EndTime - main.StartTime,
                    AddedCount = (main.AddedCount) + (det.AddedCount),
                    ErrorCount = (main.ErrorCount) + (det.ErrorCount),
                    IsSuccess = main.IsSuccess && det.IsSuccess,
                    Message =
                        (main.IsSuccess && det.IsSuccess)
                            ? "WareHouseOrder(主+详情)同步成功"
                            : "WareHouseOrder(主+详情)同步完成，但存在错误",
                };
                await _taskLogService.LogTaskSuccessAsync(taskLog.Id);
                return result;
            }
            catch (Exception ex)
            {
                result.IsSuccess = false;
                result.Message = "WareHouseOrder(主+详情)同步异常";
                await _taskLogService.LogTaskFailureAsync(taskLog.Id, ex.Message);
                return result;
            }
        }

        public async Task<SyncResult> SyncLocationsFromHqAsync(
            int hqBatchSize = 50000,
            int writePageSize = 10000
        )
        {
            var result = new SyncResult { StartTime = DateTime.Now };
            try
            {
                _hqContext.CheckConnection();
                await _localContext.Db.Ado.BeginTranAsync();
                try
                {
                    await _localContext
                        .Db.Deleteable<BlazorApp.Shared.Models.Location>()
                        .AS("Location")
                        .ExecuteCommandAsync();

                    var total = await _hqContext
                        .Db.Queryable<CPT_DIC_货位编码信息表>()
                        .CountAsync();

                    var pages = (int)Math.Ceiling(total / (double)hqBatchSize);
                    var added = 0;
                    var errors = 0;

                    for (var page = 1; page <= pages; page++)
                    {
                        var skip = (page - 1) * hqBatchSize;
                        var batch = await _hqContext
                            .Db.Queryable<CPT_DIC_货位编码信息表>()
                            .OrderBy(x => x.ID)
                            .Skip(skip)
                            .Take(hqBatchSize)
                            .ToListAsync();

                        if (!batch.Any())
                            continue;

                        var localBatch = _mapper.Map<List<BlazorApp.Shared.Models.Location>>(batch);

                        foreach (var item in localBatch)
                        {
                            if (string.IsNullOrEmpty(item.LocationGuid))
                                item.LocationGuid = Guid.NewGuid().ToString();
                        }

                        try
                        {
                            await _localContext
                                .Db.Fastest<BlazorApp.Shared.Models.Location>()
                                .AS("Location")
                                .PageSize(writePageSize)
                                .BulkCopyAsync(localBatch);
                            added += localBatch.Count;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(
                                ex,
                                "[ReactSync] Location 批{Page} 插入失败 批大小{Size}",
                                page,
                                localBatch.Count
                            );
                            errors += localBatch.Count;
                            await Task.Delay(1500);
                        }
                        batch.Clear();
                        localBatch.Clear();
                    }
                    await _localContext.Db.Ado.CommitTranAsync();
                    result.AddedCount = added;
                    result.ErrorCount = errors;
                    result.IsSuccess = errors == 0;
                    result.Message =
                        errors == 0 ? "Location 同步成功" : "Location 同步完成，但存在错误";
                }
                catch (Exception exTran)
                {
                    await _localContext.Db.Ado.RollbackTranAsync();
                    throw new Exception("Location 同步事务失败", exTran);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] Location 同步异常");
                result.IsSuccess = false;
                result.Message = "Location 同步异常";
            }
            finally
            {
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }
            return result;
        }

        public async Task<SyncResult> SyncProductLocationsFromHqAsync(
            int hqBatchSize = 50000,
            int writePageSize = 10000
        )
        {
            var result = new SyncResult { StartTime = DateTime.Now };
            try
            {
                _hqContext.CheckConnection();
                await _localContext.Db.Ado.BeginTranAsync();
                try
                {
                    await _localContext
                        .Db.Deleteable<ProductLocation>()
                        .AS("ProductLocation")
                        .ExecuteCommandAsync();

                    // Pre-load Location Code to GUID map
                    var locationMap = await _hqContext
                        .Db.Queryable<CPT_DIC_货位编码信息表>()
                        .Select(x => new { x.货位编码, x.HGUID })
                        .ToListAsync();

                    var codeToGuid = locationMap
                        .Where(x =>
                            !string.IsNullOrEmpty(x.货位编码) && !string.IsNullOrEmpty(x.HGUID)
                        )
                        .GroupBy(x => x.货位编码)
                        .ToDictionary(g => g.Key!, g => g.First().HGUID!);

                    var added = 0;
                    var errors = 0;

                    async Task ProcessBatch<T>(
                        List<T> sourceBatch,
                        Func<T, ProductLocation?> mapper
                    )
                    {
                        var localBatch = new List<ProductLocation>();
                        foreach (var item in sourceBatch)
                        {
                            var pl = mapper(item);
                            if (pl != null)
                                localBatch.Add(pl);
                        }

                        if (localBatch.Any())
                        {
                            try
                            {
                                await _localContext
                                    .Db.Fastest<ProductLocation>()
                                    .AS("ProductLocation")
                                    .PageSize(writePageSize)
                                    .BulkCopyAsync(localBatch);
                                added += localBatch.Count;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "[ReactSync] ProductLocation 批量插入失败");
                                errors += localBatch.Count;
                            }
                        }
                    }

                    // 1. Sync CPT_RED_货位存货信息表
                    var totalStock = await _hqContext
                        .Db.Queryable<CPT_RED_货位存货信息表>()
                        .CountAsync();
                    var pagesStock = (int)Math.Ceiling(totalStock / (double)hqBatchSize);

                    for (var page = 1; page <= pagesStock; page++)
                    {
                        var batch = await _hqContext
                            .Db.Queryable<CPT_RED_货位存货信息表>()
                            .OrderBy(x => x.ID)
                            .Skip((page - 1) * hqBatchSize)
                            .Take(hqBatchSize)
                            .ToListAsync();

                        await ProcessBatch(
                            batch,
                            item =>
                            {
                                if (
                                    string.IsNullOrEmpty(item.货位编码)
                                    || !codeToGuid.TryGetValue(item.货位编码, out var locGuid)
                                )
                                    return null;

                                return new ProductLocation
                                {
                                    Guid = item.HGUID ?? Guid.NewGuid().ToString(),
                                    ProductCode = item.商品编码 ?? "",
                                    LocationGuid = locGuid,
                                };
                            }
                        );
                    }

                    // 2. Sync CPT_RED_货位配货信息表
                    var totalPick = await _hqContext
                        .Db.Queryable<CPT_RED_货位配货信息表>()
                        .CountAsync();
                    var pagesPick = (int)Math.Ceiling(totalPick / (double)hqBatchSize);

                    for (var page = 1; page <= pagesPick; page++)
                    {
                        var batch = await _hqContext
                            .Db.Queryable<CPT_RED_货位配货信息表>()
                            .OrderBy(x => x.ID)
                            .Skip((page - 1) * hqBatchSize)
                            .Take(hqBatchSize)
                            .ToListAsync();

                        await ProcessBatch(
                            batch,
                            item =>
                            {
                                if (
                                    string.IsNullOrEmpty(item.货位编码)
                                    || !codeToGuid.TryGetValue(item.货位编码, out var locGuid)
                                )
                                    return null;

                                return new ProductLocation
                                {
                                    Guid = item.HGUID ?? Guid.NewGuid().ToString(),
                                    ProductCode = item.商品编码 ?? "",
                                    LocationGuid = locGuid,
                                };
                            }
                        );
                    }

                    await _localContext.Db.Ado.CommitTranAsync();
                    result.AddedCount = added;
                    result.ErrorCount = errors;
                    result.IsSuccess = errors == 0;
                    result.Message =
                        errors == 0
                            ? "ProductLocation 同步成功"
                            : "ProductLocation 同步完成，但存在错误";
                }
                catch (Exception exTran)
                {
                    await _localContext.Db.Ado.RollbackTranAsync();
                    throw new Exception("ProductLocation 同步事务失败", exTran);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] ProductLocation 同步异常");
                result.IsSuccess = false;
                result.Message = "ProductLocation 同步异常";
            }
            finally
            {
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }
            return result;
        }

        /// <summary>
        /// 全量同步收银用户：HQ 收银用户信息表 → 本地 CashRegisterUser 表
        /// 流程：
        /// 1) 检查 HQ 连接；
        /// 2) 开启本地事务并清空 CashRegisterUser；
        /// 3) 分页（50,000）拉取 HQ 数据，转换为 CashRegisterUser 后以 10,000 批量写入；
        /// 4) 统计新增/错误数与耗时，提交事务；异常则回滚。
        /// </summary>
        public async Task<SyncResult> SyncCashRegisterUsersFromHqAsync(
            int hqBatchSize = 50000,
            int writePageSize = 10000
        )
        {
            var result = new SyncResult { StartTime = DateTime.Now };
            try
            {
                _logger.LogInformation("[ReactSync] 收银用户同步：检查HQ连接");
                _hqContext.CheckConnection();

                var total = await _hqContext.Db.Queryable<DIC_收银用户信息表>().CountAsync();
                var pages = (int)Math.Ceiling(total / (double)hqBatchSize);

                await _localContext.Db.Ado.BeginTranAsync();
                try
                {
                    await CashierBarcodeMutationLock.AcquireAsync(_localContext.Db);
                    // 关键逻辑：必须完整扫描 HQ 并在同一事务预占全部条码，任何冲突都发生在删除旧表之前。
                    var preflightBatches = new List<List<CashRegisterUser>>(pages);
                    for (var page = 1; page <= pages; page++)
                    {
                        var preflightBatch = await _hqContext
                            .Db.Queryable<DIC_收银用户信息表>()
                            .OrderBy(item => item.ID)
                            .Skip((page - 1) * hqBatchSize)
                            .Take(hqBatchSize)
                            .ToListAsync();
                        if (preflightBatch.Count == 0)
                        {
                            continue;
                        }
                        var localPreflightBatch = _mapper.Map<List<CashRegisterUser>>(preflightBatch);
                        await CashierBarcodeSyncGuard.ValidateAndReserveHqBatchAsync(
                            _localContext.Db,
                            localPreflightBatch
                        );
                        preflightBatches.Add(localPreflightBatch);
                    }

                    _logger.LogInformation("[ReactSync] 清空本地 CashRegisterUser 表");
                    await _localContext
                        .Db.Deleteable<CashRegisterUser>()
                        .AS("CashRegisterUsers")
                        .ExecuteCommandAsync();

                    var added = 0;

                    _logger.LogInformation(
                        "[ReactSync] 收银用户总计: {Total}, 分为 {Pages} 页",
                        total,
                        pages
                    );

                    for (var pageIndex = 0; pageIndex < preflightBatches.Count; pageIndex++)
                    {
                        var localBatch = preflightBatches[pageIndex];

                        await _localContext
                            .Db.Fastest<CashRegisterUser>()
                            .AS("CashRegisterUsers")
                            .PageSize(writePageSize)
                            .BulkCopyAsync(localBatch);
                        added += localBatch.Count;
                        _logger.LogInformation(
                            "[ReactSync] 收银用户第{Page}/{TotalPages}页完成，新增{Count}条",
                            pageIndex + 1,
                            pages,
                            localBatch.Count
                        );
                    }

                    await _localContext.Db.Ado.CommitTranAsync();
                    result.AddedCount = added;
                    result.ErrorCount = 0;
                    result.IsSuccess = true;
                    result.Message = "收银用户同步成功";
                }
                catch (Exception exTran)
                {
                    await _localContext.Db.Ado.RollbackTranAsync();
                    throw new Exception("收银用户同步事务失败", exTran);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 收银用户同步异常");
                result.IsSuccess = false;
                result.Message = "收银用户同步异常";
            }
            finally
            {
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }
            return result;
        }

        /// <summary>
        /// 同步商品-供应商映射表：主数据库 → POSM 数据库
        /// 流程：
        /// 1) 并发查询 Product 和 HBLocalSupplier；
        /// 2) 查询 LocalSupplierCode == "200" 的商品对应的 ChinaSupplier；
        /// 3) 构建完整映射关系数据；
        /// 4) 查询现有映射数据，区分更新和插入操作；
        /// 5) Upsert 操作：批量更新已有记录，批量插入新记录；
        /// 6) 保留不在当前有效商品清单中的历史映射，避免统计回溯失去供应商归属。
        /// </summary>
        public async Task<SyncResult> SyncPosmProductSupplierMappingsAsync()
        {
            var result = new SyncResult { StartTime = DateTime.Now };
            try
            {
                _logger.LogInformation("[ReactSync] 商品-供应商映射同步：开始");

                await using var mappingSyncLock =
                    await PosmProductSupplierMappingSyncLock.AcquireAsync(_posmContext.Db);

                // 步骤 1：使用独立 SqlSugarClient 并发查询 products 和 localSuppliers（两个查询互不依赖，可以并发执行以提升性能）
                // 使用独立的数据库连接避免并发连接冲突
                using var productsClient = SqlSugarContext.CreateConcurrentConnection(
                    _configuration
                );
                using var suppliersClient = SqlSugarContext.CreateConcurrentConnection(
                    _configuration
                );

                var productsDb = new SimpleClient<Product>(productsClient);
                var suppliersDb = new SimpleClient<HBLocalSupplier>(suppliersClient);

                var productsTask = productsDb.GetListAsync(p => !p.IsDeleted);
                var localSuppliersTask = suppliersDb.GetListAsync(ls => !ls.IsDeleted);

                // 等待两个查询都完成
                await Task.WhenAll(productsTask, localSuppliersTask);

                var products = await productsTask;
                var localSuppliers = await localSuppliersTask;

                _logger.LogInformation("[ReactSync] 读取到 {Count} 个商品", products.Count);
                _logger.LogInformation(
                    "[ReactSync] 读取到 {Count} 个本地供应商",
                    localSuppliers.Count
                );

                // 步骤 2：筛选出 LocalSupplierCode == "200" 的商品，这些商品需要关联中国供应商
                var productsWithSupplier200 = products
                    .Where(p =>
                        p.LocalSupplierCode == "200" && !string.IsNullOrEmpty(p.ProductCode)
                    )
                    .Select(p => p.ProductCode!)
                    .Distinct()
                    .ToList();

                // 步骤 3：批量获取 WarehouseProduct 及其对应的 ChinaSupplierCode
                var productChinaSupplierDict = new Dictionary<string, string>();
                if (productsWithSupplier200.Any())
                {
                    // 全量同步也要分批查映射，避免大型商品库把 Contains 展开到 SQL Server 参数上限。
                    foreach (var batch in productsWithSupplier200.Chunk(500))
                    {
                        var batchCodes = batch.ToList();
                        var warehouseProducts = await _localContext
                            .Db.Queryable<WarehouseProduct>()
                            .Includes(wp => wp.DomesticProduct)
                            .Where(wp =>
                                wp.ProductCode != null
                                && batchCodes.Contains(wp.ProductCode)
                                && !wp.IsDeleted
                            )
                            .ToListAsync();

                        // 构建商品代码 -> 中国供应商代码的字典映射
                        foreach (var wp in warehouseProducts)
                        {
                            if (
                                !string.IsNullOrEmpty(wp.ProductCode)
                                && wp.DomesticProduct != null
                                && !wp.DomesticProduct.IsDeleted
                                && !string.IsNullOrEmpty(wp.DomesticProduct.SupplierCode)
                            )
                            {
                                productChinaSupplierDict[wp.ProductCode] =
                                    wp.DomesticProduct.SupplierCode;
                            }
                        }
                    }
                }

                _logger.LogInformation(
                    "[ReactSync] 找到 {Count} 个商品关联了中国供应商",
                    productChinaSupplierDict.Count
                );

                // 步骤 4：为所有商品构建映射关系数据
                var mappings = new List<PosmProductSupplierMapping>();
                foreach (var product in products)
                {
                    // 跳过没有商品编码的商品
                    if (string.IsNullOrEmpty(product.ProductCode))
                        continue;

                    var mapping = new PosmProductSupplierMapping
                    {
                        ProductCode = product.ProductCode,
                        LocalSupplierCode = product.LocalSupplierCode ?? string.Empty,
                        ChinaSupplierCode = null, // 默认为 null，下面根据情况设置
                        LastUpdateTime = DateTime.Now,
                        IsDeleted = false,
                    };

                    // 如果本地供应商代码是 "200"，则需要设置中国供应商代码
                    if (product.LocalSupplierCode == "200")
                    {
                        if (
                            productChinaSupplierDict.TryGetValue(
                                product.ProductCode,
                                out var chinaCode
                            )
                        )
                        {
                            mapping.ChinaSupplierCode = chinaCode;
                        }
                    }

                    mappings.Add(mapping);
                }

                // 步骤 5：执行租约已在源快照前开启；在同一事务中重读 POSM 现有映射再写入。
                try
                {
                    _logger.LogInformation("[ReactSync] 查询现有映射数据");
                    var existingMappings = await _posmContext
                        .Db.Queryable<PosmProductSupplierMapping>()
                        .ToListAsync();

                    // 将现有映射转为字典，以 ProductCode 为键，便于快速查找
                    var existingDict = existingMappings.ToDictionary(m => m.ProductCode, m => m);

                    _logger.LogInformation(
                        "[ReactSync] 现有映射数据 {Count} 条",
                        existingMappings.Count
                    );

                    // 步骤 6：区分更新和插入数据（Upsert 策略）
                    var toUpdate = new List<PosmProductSupplierMapping>();
                    var toInsert = new List<PosmProductSupplierMapping>();

                    foreach (var mapping in mappings)
                    {
                        if (existingDict.TryGetValue(mapping.ProductCode, out var existing))
                        {
                            // 商品已存在：检查是否需要更新（本地供应商或中国供应商代码变化）
                            if (
                                existing.LocalSupplierCode != mapping.LocalSupplierCode
                                || existing.ChinaSupplierCode != mapping.ChinaSupplierCode
                                || existing.IsDeleted
                            )
                            {
                                // 保留原有的创建时间
                                mapping.CreatedAt = existing.CreatedAt;
                                toUpdate.Add(mapping);
                            }
                        }
                        else
                        {
                            // 商品不存在：需要插入新记录
                            toInsert.Add(mapping);
                        }
                    }

                    _logger.LogInformation(
                        "[ReactSync] 需要更新 {UpdateCount} 条，插入 {InsertCount} 条；历史映射保持不删",
                        toUpdate.Count,
                        toInsert.Count
                    );

                    var updatedCount = 0;
                    var insertedCount = 0;
                    var deletedCount = 0;

                    // 步骤 8：批量更新已存在的记录
                    if (toUpdate.Any())
                    {
                        await _posmContext
                            .Db.Fastest<PosmProductSupplierMapping>()
                            .PageSize(10000)
                            .BulkUpdateAsync(toUpdate);
                        updatedCount = toUpdate.Count;
                        _logger.LogInformation("[ReactSync] 更新完成，共 {Count} 条", updatedCount);
                    }

                    // 步骤 9：批量插入新记录
                    if (toInsert.Any())
                    {
                        await _posmContext
                            .Db.Fastest<PosmProductSupplierMapping>()
                            .PageSize(10000)
                            .BulkCopyAsync(toInsert);
                        insertedCount = toInsert.Count;
                        _logger.LogInformation(
                            "[ReactSync] 插入完成，共 {Count} 条",
                            insertedCount
                        );
                    }

                    // 记录操作统计
                    result.UpdatedCount = updatedCount;
                    result.AddedCount = insertedCount;
                    result.DeletedCount = deletedCount;

                    // 提交事务；跨实例 SQL Server applock 会随此事务一并释放。
                    await mappingSyncLock.CommitAsync();
                    result.IsSuccess = true;
                    result.Message =
                        $"商品-供应商映射表同步成功：更新 {updatedCount} 条，插入 {insertedCount} 条，删除 {deletedCount} 条";
                }
                catch (Exception exTran)
                {
                    // await using 在异常路径回滚事务并释放进程内执行租约。
                    throw new Exception("商品-供应商映射表同步事务失败", exTran);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 商品-供应商映射表同步异常");
                result.IsSuccess = false;
                result.Message = "商品-供应商映射表同步异常";
            }
            finally
            {
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }
            return result;
        }

        public async Task<SyncResult> SyncProductCategoriesFromHqAsync(
            int hqBatchSize = 50000,
            int writePageSize = 10000
        )
        {
            var result = new SyncResult { StartTime = DateTime.Now };
            try
            {
                _hqContext.CheckConnection();
                await _localContext.Db.Ado.BeginTranAsync();
                try
                {
                    await _localContext
                        .Db.Deleteable<ProductCategory>()
                        .AS("ProductCategory")
                        .ExecuteCommandAsync();
                    var hqDb = HqSqlSugarContext.CreateConcurrentConnection(_configuration);
                    var total = await hqDb.Queryable<DIC_商品分类码表>().CountAsync();
                    var pages = (int)Math.Ceiling(total / (double)hqBatchSize);
                    var added = 0;
                    var errors = 0;
                    for (var page = 1; page <= pages; page++)
                    {
                        var skip = (page - 1) * hqBatchSize;
                        var batch = await hqDb.Queryable<DIC_商品分类码表>()
                            .OrderBy(x => x.ID)
                            .Skip(skip)
                            .Take(hqBatchSize)
                            .ToListAsync();
                        if (!batch.Any())
                            continue;
                        var localBatch = _mapper.Map<List<ProductCategory>>(batch);
                        try
                        {
                            await _localContext
                                .Db.Fastest<ProductCategory>()
                                .AS("ProductCategory")
                                .PageSize(writePageSize)
                                .BulkCopyAsync(localBatch);
                            added += localBatch.Count;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(
                                ex,
                                "[ReactSync] ProductCategory 批{Page} 插入失败 批大小{Size}",
                                page,
                                localBatch.Count
                            );
                            errors += localBatch.Count;
                            await Task.Delay(1500);
                        }
                        batch.Clear();
                        localBatch.Clear();
                    }
                    hqDb.Dispose();
                    await _localContext.Db.Ado.CommitTranAsync();
                    result.AddedCount = added;
                    result.ErrorCount = errors;
                    result.IsSuccess = errors == 0;
                    result.Message =
                        errors == 0
                            ? "ProductCategory 同步成功"
                            : "ProductCategory 同步完成，但存在错误";
                }
                catch (Exception exTran)
                {
                    await _localContext.Db.Ado.RollbackTranAsync();
                    throw new Exception("ProductCategory 同步事务失败", exTran);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] ProductCategory 同步异常");
                result.IsSuccess = false;
                result.Message = "ProductCategory 同步异常";
            }
            finally
            {
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }
            return result;
        }

        public async Task<SyncResult> SyncSpecialProductFromHqAsync(
            List<string>? selectedStoreCodes = null
        )
        {
            var result = new SyncResult { StartTime = DateTime.Now };
            var totalUpdated = 0;
            var totalErrors = 0;

            try
            {
                _logger.LogInformation("[ReactSync] 开始同步特殊商品标记 HQ → 本地");

                using var hqDb = HqSqlSugarContext.CreateConcurrentConnection(_configuration);
                using var localDb = SqlSugarContext.CreateConcurrentConnection(_configuration);

                var query = hqDb
                    .Queryable<DIC_商品零售价表>()
                    .Where(x => x.H是否特殊商品 == true && x.H使用状态 == true);

                if (selectedStoreCodes?.Any() == true)
                {
                    query = query.Where(x => selectedStoreCodes.Contains(x.H分店代码));
                }

                var hqSpecialProducts = await query
                    .Select(x => new { x.H分店代码, x.H商品编码 })
                    .ToListAsync();

                if (!hqSpecialProducts.Any())
                {
                    result.IsSuccess = true;
                    result.Message = "HQ 未发现特殊商品记录";
                    return result;
                }

                _logger.LogInformation(
                    "[ReactSync] HQ 特殊商品记录数: {Count}",
                    hqSpecialProducts.Count
                );

                var groupedByStore = hqSpecialProducts
                    .GroupBy(x => x.H分店代码)
                    .ToList();

                foreach (var storeGroup in groupedByStore)
                {
                    var storeCode = storeGroup.Key;
                    var productCodes = storeGroup.Select(x => x.H商品编码).Distinct().ToList();

                    try
                    {
                        const int batchSize = 500;
                        var batchCount = (int)Math.Ceiling(productCodes.Count / (double)batchSize);

                        for (int i = 0; i < batchCount; i++)
                        {
                            var batchCodes = productCodes
                                .Skip(i * batchSize)
                                .Take(batchSize)
                                .ToList();

                            var updated = await localDb
                                .Updateable<StoreRetailPrice>()
                                .SetColumns(x => x.IsSpecialProduct == true)
                                .Where(x =>
                                    x.StoreCode == storeCode
                                    && x.ProductCode != null
                                    && batchCodes.Contains(x.ProductCode)
                                )
                                .ExecuteCommandAsync();

                            totalUpdated += updated;
                        }
                    }
                    catch (Exception ex)
                    {
                        totalErrors++;
                        _logger.LogError(
                            ex,
                            "[ReactSync] 分店 {StoreCode} 特殊商品标记同步失败",
                            storeCode
                        );
                    }
                }

                result.UpdatedCount = totalUpdated;
                result.ErrorCount = totalErrors;
                result.IsSuccess = totalErrors == 0;
                result.TotalCount = totalUpdated + totalErrors;
                result.Message =
                    totalErrors == 0
                        ? $"特殊商品标记同步成功：更新{totalUpdated:N0}条记录"
                        : $"特殊商品标记同步完成：更新{totalUpdated:N0}条，失败{totalErrors}个分店";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReactSync] 特殊商品标记同步异常");
                result.IsSuccess = false;
                result.Message = "特殊商品标记同步异常: " + ex.Message;
            }
            finally
            {
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;
            }

            return result;
        }

        private (string? UserGuid, string Name) GetCurrentAuditActor()
        {
            // 请求作用域内使用当前用户；后台新 scope 没有 HttpContext 时由 CurrentUserService 回退 System。
            var actorName = _currentUserService.GetCurrentUsername();
            actorName = string.IsNullOrWhiteSpace(actorName) ? "System" : actorName.Trim();
            var actorGuid = _currentUserService.GetCurrentUserGuid();
            if (
                !string.IsNullOrWhiteSpace(actorGuid)
                && string.Equals(actorName, "System", StringComparison.OrdinalIgnoreCase)
            )
            {
                actorName = actorGuid.Trim();
            }
            return (
                string.IsNullOrWhiteSpace(actorGuid) ? null : actorGuid.Trim(),
                actorName
            );
        }

        private static WarehouseProductChangeHistoryContextDto CreateSystemSyncContext(
            string source,
            Guid batchGuid,
            DateTime occurredAtUtc,
            string? actorUserGuid = null,
            string? actorName = null
        )
        {
            // 后台计划任务不带身份时使用 System；用户入队任务使用已保存的服务端身份。
            var resolvedActorName = string.IsNullOrWhiteSpace(actorName) ? "System" : actorName.Trim();
            var resolvedActorGuid = string.IsNullOrWhiteSpace(actorUserGuid)
                ? null
                : actorUserGuid.Trim();
            var isSystem = resolvedActorGuid == null
                && string.Equals(
                    resolvedActorName,
                    "System",
                    StringComparison.OrdinalIgnoreCase
                );
            return new WarehouseProductChangeHistoryContextDto
            {
                Action = "Sync",
                Source = source,
                BatchGuid = batchGuid,
                ActorUserGuid = resolvedActorGuid,
                ActorName = resolvedActorName,
                ActorType = isSystem ? "System" : "User",
                OccurredAtUtc = occurredAtUtc,
            };
        }

        private async Task DeleteProductsByCodeInBatchesAsync(IReadOnlyList<string> productCodes)
        {
            foreach (var codeBatch in productCodes.Chunk(1000))
            {
                var codes = codeBatch.ToList();
                await _localContext.Db.Deleteable<Product>()
                    .Where(item => item.ProductCode != null && codes.Contains(item.ProductCode))
                    .ExecuteCommandAsync();
            }
        }

        private async Task DeleteDomesticProductsByCodeInBatchesAsync(
            IReadOnlyList<string> productCodes
        )
        {
            foreach (var codeBatch in productCodes.Chunk(1000))
            {
                var codes = codeBatch.ToList();
                await _localContext.Db.Deleteable<DomesticProduct>()
                    .Where(item => codes.Contains(item.ProductCode))
                    .ExecuteCommandAsync();
            }
        }
    }
}
