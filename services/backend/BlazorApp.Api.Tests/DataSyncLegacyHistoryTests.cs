using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Mappings.Profiles;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HqEntities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class DataSyncLegacyHistoryTests : IDisposable
{
    private readonly string _localDbPath;
    private readonly string _hqDbPath;
    private readonly string _hbSalesDbPath;
    private readonly SqliteConnection _localConnection;
    private readonly SqliteConnection _hqConnection;
    private readonly SqliteConnection _hbSalesConnection;
    private readonly SqlSugarClient _localDb;
    private readonly SqlSugarClient _hqDb;
    private readonly SqlSugarScope _hbSalesDb;
    private readonly IMapper _mapper;

    public DataSyncLegacyHistoryTests()
    {
        _localDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _hqDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _hbSalesDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _localConnection = new SqliteConnection($"Data Source={_localDbPath}");
        _hqConnection = new SqliteConnection($"Data Source={_hqDbPath}");
        _hbSalesConnection = new SqliteConnection($"Data Source={_hbSalesDbPath}");
        _localConnection.Open();
        _hqConnection.Open();
        _hbSalesConnection.Open();

        _localDb = new SqlSugarClient(CreateConnectionConfig(_localConnection.ConnectionString));
        _hqDb = new SqlSugarClient(CreateConnectionConfig(_hqConnection.ConnectionString));
        _hbSalesDb = new SqlSugarScope(CreateConnectionConfig(_hbSalesConnection.ConnectionString));
        _localDb.CodeFirst.InitTables(
            typeof(Product),
            typeof(WarehouseProduct),
            typeof(DomesticProduct),
            typeof(ProductSetCode),
            typeof(StoreRetailPrice),
            typeof(StoreMultiCodeProduct)
        );
        _localDb.Ado.ExecuteCommand(
            """
            CREATE TABLE WarehouseProductChangeHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                EventGuid TEXT NOT NULL,
                ProductCode TEXT NOT NULL,
                Action TEXT NOT NULL,
                Source TEXT NOT NULL,
                SourceReference TEXT NULL,
                BatchGuid TEXT NULL,
                ActorUserGuid TEXT NULL,
                ActorName TEXT NOT NULL,
                ActorType TEXT NOT NULL,
                OccurredAtUtc TEXT NOT NULL,
                ChangesJson TEXT NOT NULL
            )
            """
        );
        _hqDb.CodeFirst.InitTables(
            typeof(CBP_DIC_商品库存表),
            typeof(CPT_DIC_商品信息字典表),
            typeof(DIC_商品信息字典表),
            typeof(DIC_一品多码表),
            typeof(DIC_分店一品多码表)
        );
        _hbSalesDb.CodeFirst.InitTables(typeof(CPT_DIC_商品信息字典表));
        _mapper = new MapperConfiguration(
            cfg =>
            {
                cfg.AddProfile<ProductMappingProfile>();
                cfg.AddProfile<ProductSetCodeMappingProfile>();
                cfg.AddProfile<StoreMappingProfile>();
                cfg.AddProfile<WarehouseMappingProfile>();
                cfg.AddProfile<DomesticProductMappingProfile>();
            },
            NullLoggerFactory.Instance
        ).CreateMapper();
    }

    [Fact]
    public void DataSyncService_历史服务依赖为空时立即拒绝()
    {
        Assert.Throws<ArgumentNullException>(() => CreateService(null!));
    }

    [Fact]
    public void DataSyncService_当前用户服务依赖为空时立即拒绝()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DataSyncService(
                CreateSqlSugarContext(_localDb),
                CreateHqSqlSugarContext(_hqDb),
                CreateHBSalesSqlSugarContext(_hbSalesDb),
                NullLogger<DataSyncService>.Instance,
                _mapper,
                Mock.Of<ITranslationService>(),
                new ConfigurationBuilder().Build(),
                CreateRealHistoryService(),
                null!
            )
        );
    }

    [Fact]
    public void DataSyncFacade_公开入口只委派且Features不反向依赖Facade日志类别()
    {
        var facadeSource = File.ReadAllText(ResolveRepositoryFile(
            "services/backend/BlazorApp.Api/Services/DataSyncService.cs"
        ));
        var featureRoot = Directory.GetParent(Path.GetDirectoryName(ResolveRepositoryFile(
            "services/backend/BlazorApp.Api/Features/DataSync/Common/DataSyncSliceContext.cs"
        ))!)!.FullName;

        Assert.Contains(
            "public Task<SyncResult> SyncLocationsFromHqAsync() => _locations.SyncLocationsFromHqAsync();",
            facadeSource
        );
        Assert.Contains(
            "public Task<SyncResult> SyncContainersFromHqAsync() => _containers.SyncContainersFromHqAsync();",
            facadeSource
        );
        Assert.Contains(
            "public Task<SyncResult> SyncProductsFromHqAsync() => _products.SyncProductsFromHqAsync();",
            facadeSource
        );
        Assert.Contains(
            "public Task<SyncResult> SyncStoreRetailPricesFromHqConcurrentAsync",
            facadeSource
        );
        Assert.DoesNotContain("public class BatchResult", facadeSource, StringComparison.Ordinal);
        Assert.NotNull(
            typeof(DataSyncService).Assembly.GetType(
                "BlazorApp.Api.Services.BatchResult",
                throwOnError: false
            )
        );

        var productsSource = File.ReadAllText(ResolveRepositoryFile(
            "services/backend/BlazorApp.Api/Features/DataSync/Full/Products/DataSyncProductsStore.cs"
        ));
        Assert.DoesNotContain("new BatchResult", productsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Task<BatchResult>", productsSource, StringComparison.Ordinal);

        foreach (var featureFile in Directory.GetFiles(featureRoot, "*.cs", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("ILogger<DataSyncService>", File.ReadAllText(featureFile));
        }
    }

    [Fact]
    public void DataSync全量同步_替换写入必须在提交前受单一事务保护且错误不标记成功()
    {
        var containersSource = File.ReadAllText(ResolveRepositoryFile(
            "services/backend/BlazorApp.Api/Features/DataSync/Full/Stores/DataSyncContainersStore.cs"
        ));
        var pricesSource = File.ReadAllText(ResolveRepositoryFile(
            "services/backend/BlazorApp.Api/Features/DataSync/Full/Stores/DataSyncStorePricesConcurrentStore.cs"
        ));
        var productsSource = File.ReadAllText(ResolveRepositoryFile(
            "services/backend/BlazorApp.Api/Features/DataSync/Full/Products/DataSyncProductsStore.cs"
        ));
        var locationsStoreSource = File.ReadAllText(ResolveRepositoryFile(
            "services/backend/BlazorApp.Api/Features/DataSync/Locations/DataSyncLocationsStore.cs"
        ));
        var locationsComponentsSource = File.ReadAllText(ResolveRepositoryFile(
            "services/backend/BlazorApp.Api/Features/DataSync/Locations/DataSyncLocationsFullSyncComponents.cs"
        ));

        Assert.True(containersSource.IndexOf("BeginTranAsync", StringComparison.Ordinal) < containersSource.IndexOf("Deleteable<ContainerDetail>", StringComparison.Ordinal));
        Assert.Contains("CommitTranAsync", containersSource);
        Assert.Contains("RollbackTranAsync", containersSource);

        Assert.Contains("BeginTransactionAsync", pricesSource);
        Assert.Contains("CommitAsync", pricesSource);
        Assert.Contains("RollbackAsync", pricesSource);
        Assert.DoesNotContain("localDb = SqlSugarContext.CreateConcurrentConnection", pricesSource);

        Assert.Contains("result.IsSuccess = totalErrors == 0;", productsSource);
        Assert.DoesNotContain("result.IsSuccess = true;\n                    result.Message =\n                        $\"🎉 商品信息同步完成", productsSource);

        Assert.Contains("DataSyncLocationsSourceReader", locationsStoreSource);
        Assert.Contains("DataSyncLocationsEntityMapper", locationsStoreSource);
        Assert.Contains("DataSyncLocationsTransactionWriter", locationsStoreSource);
        Assert.Contains("DataSyncLocationsResultAssembler", locationsStoreSource);
        Assert.DoesNotContain("BulkMergeAsync", ExtractMethodBody(locationsStoreSource, "SyncLocationsFromHqAsync"));
        Assert.Contains("BeginTranAsync", locationsComponentsSource);
        Assert.Contains("RollbackTranAsync", locationsComponentsSource);
    }

    [Fact]
    public void DataSync货位全量同步_逐批暂存且最终事务不覆盖远端读取与映射()
    {
        var locationsStoreSource = File.ReadAllText(ResolveRepositoryFile(
            "services/backend/BlazorApp.Api/Features/DataSync/Locations/DataSyncLocationsStore.cs"
        ));
        var locationsComponentsSource = File.ReadAllText(ResolveRepositoryFile(
            "services/backend/BlazorApp.Api/Features/DataSync/Locations/DataSyncLocationsFullSyncComponents.cs"
        ));

        var syncMethod = ExtractMethodBody(locationsStoreSource, "SyncLocationsFromHqAsync");

        // 读取、映射和暂存逐批完成，绝不能先收集所有 HQ 批次再构造第二套完整 Location 图。
        Assert.Contains("ReadBatchesAsync", syncMethod);
        Assert.Contains("DataSyncLocationsSpool", syncMethod);
        Assert.Contains("MapBatch", syncMethod);
        Assert.DoesNotContain("ReadAllAsync", syncMethod);
        Assert.DoesNotContain(".Map(sourceBatches)", syncMethod);
        Assert.True(
            syncMethod.IndexOf("CompleteWritingAsync", StringComparison.Ordinal)
                < syncMethod.IndexOf("WriteAsync", StringComparison.Ordinal)
        );

        Assert.Contains("IAsyncEnumerable", locationsComponentsSource);
        Assert.Contains("yield return batch", locationsComponentsSource);
        Assert.DoesNotContain("var batches = new List<IReadOnlyList<CPT_DIC_货位编码信息表>>", locationsComponentsSource);
        Assert.Contains("ReadBatchesAsync", locationsComponentsSource);
        Assert.Contains("ReadBatchesAsync", ExtractMethodBody(locationsComponentsSource, "WriteAsync"));
        Assert.Contains("BeginTranAsync", locationsComponentsSource);
        Assert.Contains("RollbackTranAsync", locationsComponentsSource);
    }

    [Fact]
    public async Task SyncLocationsFromHqAsync_映射暂存失败时保留原货位数据()
    {
        _localDb.CodeFirst.InitTables(typeof(Location));
        _hqDb.CodeFirst.InitTables(typeof(CPT_DIC_货位编码信息表));
        await _localDb.Insertable(new Location
        {
            LocationGuid = "LOCAL-LOCATION-MUST-STAY",
            LocationCode = "OLD-A01",
            Status = 1,
        }).ExecuteCommandAsync();
        await _hqDb.Insertable(new CPT_DIC_货位编码信息表
        {
            ID = 1,
            HGUID = "HQ-LOCATION-FAIL",
            货位编码 = "A01",
            状态 = 1,
        }).ExecuteCommandAsync();

        var mapper = new Mock<IMapper>(MockBehavior.Strict);
        mapper.Setup(item => item.Map<List<Location>>(It.IsAny<object>()))
            .Throws(new InvalidOperationException("模拟货位映射暂存失败"));

        var result = await CreateService(CreateRealHistoryService(), mapper: mapper.Object)
            .SyncLocationsFromHqAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(1, result.ErrorCount);
        Assert.Equal(1, await _localDb.Queryable<Location>().CountAsync());
        Assert.Equal(
            "LOCAL-LOCATION-MUST-STAY",
            (await _localDb.Queryable<Location>().SingleAsync()).LocationGuid
        );
    }

    [Fact]
    public void DataSync门店零售价全量同步_使用有界生产消费且失败时回滚()
    {
        var pricesSource = File.ReadAllText(ResolveRepositoryFile(
            "services/backend/BlazorApp.Api/Features/DataSync/Full/Stores/DataSyncStorePricesConcurrentStore.cs"
        ));

        Assert.Contains("Channel.CreateBounded<StorePriceSourceBatch>", pricesSource);
        // 队列容量是内存预算，不能因为 Controller 放宽并发度就线性放大到百万行。
        Assert.Contains("StagingChannelCapacity = 2", pricesSource);
        Assert.Contains("new BoundedChannelOptions(StagingChannelCapacity)", pricesSource);
        Assert.DoesNotContain("new BoundedChannelOptions(maxConcurrency)", pricesSource);
        Assert.Contains("BoundedChannelFullMode.Wait", pricesSource);
        Assert.Contains("sourceBatches.ReadAllAsync(cancellationToken)", pricesSource);
        Assert.Contains("cancellationSource.Cancel()", pricesSource);
        Assert.DoesNotContain("var sourceBatches = await Task.WhenAll", pricesSource);
        Assert.Contains("await transaction.RollbackAsync()", pricesSource);
    }

    [Fact]
    public void DataSync门店零售价全量同步_大批量写入与交换必须继承配置超时()
    {
        var pricesSource = File.ReadAllText(ResolveRepositoryFile(
            "services/backend/BlazorApp.Api/Features/DataSync/Full/Stores/DataSyncStorePricesConcurrentStore.cs"
        ));

        Assert.Contains("Database:BulkCopyCommandTimeoutSeconds", pricesSource);
        Assert.Contains("BulkCopyTimeout = bulkCopyCommandTimeoutSeconds", pricesSource);
        Assert.Contains("Database:ConcurrentCommandTimeoutSeconds", pricesSource);
        Assert.Contains("command.CommandTimeout = commandTimeoutSeconds", pricesSource);
    }

    [Fact]
    public void DataSync门店零售价并发全量同步_必须先有界暂存再原子替换()
    {
        var pricesSource = File.ReadAllText(ResolveRepositoryFile(
            "services/backend/BlazorApp.Api/Features/DataSync/Full/Stores/DataSyncStorePricesConcurrentStore.cs"
        ));
        var legacyPricesSource = File.ReadAllText(ResolveRepositoryFile(
            "services/backend/BlazorApp.Api/Features/DataSync/Full/Stores/DataSyncStorePricesStore.cs"
        ));
        var legacyProductsSource = File.ReadAllText(ResolveRepositoryFile(
            "services/backend/BlazorApp.Api/Features/DataSync/Full/Stores/DataSyncStoreProductsStore.cs"
        ));
        var spoolSource = File.ReadAllText(ResolveRepositoryFile(
            "services/backend/BlazorApp.Api/Features/DataSync/Full/Stores/DataSyncStorePricesSpool.cs"
        ));
        var readStoreMethod = ExtractMethodBody(
            pricesSource,
            "private async Task ReadStorePricesFromHqAsync"
        );
        var replaceMethod = ExtractMethodBody(
            pricesSource,
            "private async Task ReplaceLocalPricesAsync"
        );
        var legacyRetailMethod = ExtractMethodBody(
            legacyPricesSource,
            "SyncStoreRetailPricesFromHqAsync"
        );
        var legacyClearanceMethod = ExtractMethodBody(
            legacyProductsSource,
            "SyncStoreClearancePricesFromHqAsync"
        );

        // 每店以 HQ 主键推进游标，避免 Offset 翻页漏行或重复行。
        Assert.Contains("price.ID > lastSeenId", readStoreMethod);
        Assert.Contains("OrderBy(price => price.ID)", readStoreMethod);
        Assert.Contains("Take(sourceBatchSize)", readStoreMethod);
        Assert.Contains("Math.Min(batchSize, MaximumSourceBatchSize)", pricesSource);
        Assert.Contains("MaximumSourceBatchSize = 50000", pricesSource);

        // 远程读取阶段不能持有本地 live 表事务；完整生产完成后才替换本地数据。
        Assert.DoesNotContain("LocalContext.Db", readStoreMethod);
        Assert.DoesNotContain("BeginTranAsync", readStoreMethod);
        Assert.True(
            pricesSource.IndexOf("StageStorePricesInDatabaseAsync", StringComparison.Ordinal)
                < pricesSource.IndexOf("ReplaceLocalPricesAsync", StringComparison.Ordinal)
        );
        Assert.Contains("DataSyncStorePricesSpool", pricesSource);
        Assert.Contains("new DataSyncStorePricesSpool(Logger)", pricesSource);
        Assert.Contains("SqlBulkCopy", pricesSource);

        var stagingMethod = ExtractMethodBody(pricesSource, "StageStorePricesInDatabaseAsync");
        // staging 装载发生在最终替换事务之外；SQL Server 通过固定连接保证 #temp 与 BulkCopy 同会话。
        Assert.DoesNotContain("BeginTransaction", stagingMethod);
        Assert.DoesNotContain("BeginTranAsync", stagingMethod);
        Assert.Contains("SqlConnection", pricesSource);
        Assert.Contains("#DataSyncStorePrices", pricesSource);

        var sqlServerReplaceMethod = ExtractMethodBody(
            pricesSource,
            "ReplaceSqlServerLocalPricesAsync"
        );
        Assert.Contains("BeginTransactionAsync", sqlServerReplaceMethod);
        Assert.Contains("RollbackAsync", sqlServerReplaceMethod);
        Assert.Contains("sp_getapplock", sqlServerReplaceMethod);
        Assert.Contains("INSERT INTO [StoreRetailPrice]", sqlServerReplaceMethod);
        Assert.DoesNotContain("ReadBatchesAsync", replaceMethod);

        // BulkCopy 失败必须上抛到同一事务边界，不能提交删除后的部分结果。
        Assert.DoesNotContain("❌ 批量插入失败", legacyRetailMethod);
        Assert.DoesNotContain("⚠️ 分店零售价数据同步部分成功", legacyRetailMethod);
        Assert.DoesNotContain("❌ 批量插入失败", legacyClearanceMethod);
        Assert.DoesNotContain("⚠️ 分店清货价数据同步部分成功", legacyClearanceMethod);

        // 本地替换提交后，临时文件清理失败只能告警，不能把已提交成功反报为失败。
        var disposeMethod = ExtractMethodBody(spoolSource, "DisposeAsync");
        Assert.Contains("catch (Exception cleanupException)", disposeMethod);
        Assert.Contains("LogWarning", disposeMethod);
        Assert.DoesNotContain("throw", disposeMethod, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncStoreRetailPricesFromHqAsync_BulkCopy失败时回滚并保留原数据()
    {
        _hqDb.CodeFirst.InitTables(typeof(DIC_商品零售价表));
        await _localDb.Insertable(new StoreRetailPrice
        {
            UUID = "LOCAL-RETAIL-MUST-STAY",
            StoreCode = "S01",
            ProductCode = "P-RETAIL-FAIL",
            StoreRetailPriceValue = 1m,
        }).ExecuteCommandAsync();
        await _hqDb.Insertable(CreateIncrementalProduct(1, "P-RETAIL-FAIL")).ExecuteCommandAsync();
        await _hqDb.Insertable(new DIC_商品零售价表
        {
            ID = 1,
            HGUID = "HQ-RETAIL-FAIL",
            H分店代码 = "S01",
            H商品编码 = "P-RETAIL-FAIL",
            H供应商编码 = "SUP-01",
            H进货价 = 2m,
            H分店零售价 = 3m,
            H使用状态 = true,
            FGC_CreateDate = new DateTime(2026, 8, 26),
            FGC_LastModifyDate = new DateTime(2026, 8, 26),
        }).ExecuteCommandAsync();
        await _localDb.Ado.ExecuteCommandAsync(
            """
            CREATE TRIGGER reject_store_retail_price_bulk_copy
            BEFORE INSERT ON "StoreRetailPrice"
            BEGIN
                SELECT RAISE(ABORT, '模拟零售价 BulkCopy 失败');
            END;
            """
        );

        var result = await CreateService(CreateRealHistoryService())
            .SyncStoreRetailPricesFromHqConcurrentAsync(new List<string> { "S01" }, 1, 1);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, result.AddedCount);
        Assert.Equal(1, await _localDb.Queryable<StoreRetailPrice>().CountAsync());
        Assert.Equal(
            "LOCAL-RETAIL-MUST-STAY",
            (await _localDb.Queryable<StoreRetailPrice>().SingleAsync()).UUID
        );
    }

    [Fact]
    public async Task SyncContainersFromHqAsync_映射失败时保留旧货柜数据()
    {
        _localDb.CodeFirst.InitTables(typeof(Container), typeof(ContainerDetail));
        _hqDb.CodeFirst.InitTables(typeof(CPT_RED_货柜单主表Store));
        await _localDb.Insertable(new Container
        {
            ContainerCode = "LOCAL-CONTAINER-MUST-STAY",
            ContainerNumber = "旧货柜",
        }).ExecuteCommandAsync();
        await _hqDb.Insertable(new CPT_RED_货柜单主表Store
        {
            ID = 1,
            HGUID = "HQ-CONTAINER-FAIL",
            货柜编号 = "HQ货柜",
        }).ExecuteCommandAsync();

        var mapper = new Mock<IMapper>(MockBehavior.Strict);
        mapper.Setup(item => item.Map<List<Container>>(It.IsAny<object>()))
            .Throws(new InvalidOperationException("模拟货柜映射失败"));

        var result = await CreateService(CreateRealHistoryService(), mapper: mapper.Object)
            .SyncContainersFromHqAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(1, result.ErrorCount);
        Assert.Equal(1, await _localDb.Queryable<Container>().CountAsync());
        Assert.Equal(
            "LOCAL-CONTAINER-MUST-STAY",
            (await _localDb.Queryable<Container>().SingleAsync()).ContainerCode
        );
    }

    [Fact]
    public async Task SyncProductStocksIncrementalFromHqAsync_只改变库存时不写历史且恢复仓库审计字段()
    {
        var originalUpdatedAt = new DateTime(2026, 8, 12, 1, 2, 3, DateTimeKind.Utc);
        await _localDb.Insertable(
                new WarehouseProduct
                {
                    ProductCode = "P-STOCK-ONLY",
                    DomesticPrice = 1m,
                    OEMPrice = 2m,
                    ImportPrice = 3m,
                    StockQuantity = 10,
                    MinOrderQuantity = 4,
                    StockValue = 30m,
                    StockAlertQuantity = 5,
                    IsActive = true,
                    Volume = 0m,
                    CreatedAt = originalUpdatedAt,
                    UpdatedAt = originalUpdatedAt,
                    CreatedBy = "旧创建人",
                    UpdatedBy = "旧操作人",
                }
            )
            .ExecuteCommandAsync();
        await _hqDb.Insertable(
                new CBP_DIC_商品库存表
                {
                    HGUID = "HQ-STOCK-ONLY",
                    H商品编码 = "P-STOCK-ONLY",
                    H国内价格 = 1m,
                    H贴牌价格 = 2m,
                    H进口价格 = 3m,
                    H库存 = 20m,
                    H最小订货量 = 4m,
                    H库存金额 = 30m,
                    H库存预警数 = 5,
                    H使用状态 = 1,
                    FGC_LastModifyDate = new DateTime(2026, 8, 12, 2, 0, 0),
                }
            )
            .ExecuteCommandAsync();

        var result = await CreateService(CreateRealHistoryService()).SyncProductStocksIncrementalFromHqAsync(
            new DateTime(2026, 8, 12, 0, 0, 0)
        );

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(0, await _localDb.Queryable<WarehouseProductChangeHistory>().CountAsync());
        var synced = await _localDb.Queryable<WarehouseProduct>().SingleAsync();
        Assert.Equal(20, synced.StockQuantity);
        Assert.Equal(originalUpdatedAt, synced.UpdatedAt);
        Assert.Equal("旧操作人", synced.UpdatedBy);
    }

    [Fact]
    public async Task SyncProductStocksIncrementalFromHqAsync_价格变化时使用服务端批次和System写历史且不记录库存数量()
    {
        await _localDb.Insertable(
                new WarehouseProduct
                {
                    ProductCode = "P-PRICE",
                    DomesticPrice = 1m,
                    OEMPrice = 2m,
                    ImportPrice = 3m,
                    StockQuantity = 10,
                    MinOrderQuantity = 4,
                    StockValue = 30m,
                    StockAlertQuantity = 5,
                    IsActive = true,
                    Volume = 0m,
                }
            )
            .ExecuteCommandAsync();
        await _hqDb.Insertable(
                new CBP_DIC_商品库存表
                {
                    HGUID = "HQ-PRICE",
                    H商品编码 = "P-PRICE",
                    H国内价格 = 1m,
                    H贴牌价格 = 2m,
                    H进口价格 = 4m,
                    H库存 = 20m,
                    H最小订货量 = 4m,
                    H库存金额 = 30m,
                    H库存预警数 = 5,
                    H使用状态 = 1,
                    FGC_LastModifyDate = new DateTime(2026, 8, 12, 2, 0, 0),
                }
            )
            .ExecuteCommandAsync();

        var result = await CreateService(CreateRealHistoryService()).SyncProductStocksIncrementalFromHqAsync(
            new DateTime(2026, 8, 12, 0, 0, 0)
        );

        Assert.True(result.IsSuccess, result.Message);
        var history = await _localDb.Queryable<WarehouseProductChangeHistory>().SingleAsync();
        Assert.Equal("BatchUpdate", history.Action);
        Assert.Equal("DataSyncLegacyIncremental", history.Source);
        Assert.Null(history.ActorUserGuid);
        Assert.Equal("System", history.ActorName);
        Assert.Equal("System", history.ActorType);
        Assert.NotNull(history.BatchGuid);
        using var changes = JsonDocument.Parse(history.ChangesJson);
        Assert.Contains(
            changes.RootElement.EnumerateArray(),
            item => item.GetProperty("fieldKey").GetString() == "importPrice"
        );
        Assert.DoesNotContain(
            changes.RootElement.EnumerateArray(),
            item => item.GetProperty("fieldKey").GetString() == "stockQuantity"
        );
    }

    [Fact]
    public async Task SyncProductStocksFromHqAsync_历史写入失败时回滚整次全量同步()
    {
        await _localDb.Insertable(
                new WarehouseProduct
                {
                    ProductCode = "P-OLD",
                    DomesticPrice = 1m,
                    StockQuantity = 10,
                }
            )
            .ExecuteCommandAsync();
        await _hqDb.Insertable(
                new CBP_DIC_商品库存表
                {
                    HGUID = "HQ-NEW",
                    H商品编码 = "P-NEW",
                    H国内价格 = 9m,
                    H库存 = 99m,
                    H使用状态 = 1,
                }
            )
            .ExecuteCommandAsync();

        var result = await CreateService(new ThrowingHistoryService()).SyncProductStocksFromHqAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(1, await _localDb.Queryable<WarehouseProduct>().CountAsync());
        var original = await _localDb.Queryable<WarehouseProduct>().SingleAsync();
        Assert.Equal("P-OLD", original.ProductCode);
        Assert.Equal(1m, original.DomesticPrice);
        Assert.Equal(10, original.StockQuantity);
    }

    [Fact]
    public async Task SyncProductStocksFromHqAsync_清表重建时保留本地正数中包数且只用HQ补空缺()
    {
        await _localDb.Insertable(
                new List<WarehouseProduct>
                {
                    new() { ProductCode = "P-KEEP", MinOrderQuantity = 6, StockQuantity = 1 },
                    new() { ProductCode = "P-FILL", MinOrderQuantity = null, StockQuantity = 1 },
                }
            )
            .ExecuteCommandAsync();
        await _hqDb.Insertable(
                new List<CBP_DIC_商品库存表>
                {
                    new()
                    {
                        HGUID = "HQ-KEEP",
                        H商品编码 = "P-KEEP",
                        H库存 = 30m,
                        H最小订货量 = 0m,
                        H使用状态 = 1,
                    },
                    new()
                    {
                        HGUID = "HQ-FILL",
                        H商品编码 = "P-FILL",
                        H库存 = 40m,
                        H最小订货量 = 8m,
                        H使用状态 = 1,
                    },
                    new()
                    {
                        HGUID = "HQ-NEW",
                        H商品编码 = "P-NEW",
                        H库存 = 50m,
                        H最小订货量 = 10m,
                        H使用状态 = 1,
                    },
                }
            )
            .ExecuteCommandAsync();

        var result = await CreateService(CreateRealHistoryService()).SyncProductStocksFromHqAsync();

        Assert.True(result.IsSuccess, result.Message);
        var rows = await _localDb.Queryable<WarehouseProduct>().ToListAsync();
        Assert.Equal(6, rows.Single(row => row.ProductCode == "P-KEEP").MinOrderQuantity);
        Assert.Equal(30, rows.Single(row => row.ProductCode == "P-KEEP").StockQuantity);
        Assert.Equal(8, rows.Single(row => row.ProductCode == "P-FILL").MinOrderQuantity);
        Assert.Equal(10, rows.Single(row => row.ProductCode == "P-NEW").MinOrderQuantity);
    }

    [Fact]
    public async Task SyncProductStocksIncrementalFromHqAsync_整行更新时保留本地正数中包数()
    {
        await _localDb.Insertable(
                new List<WarehouseProduct>
                {
                    new() { ProductCode = "P-KEEP", MinOrderQuantity = 6, StockQuantity = 1 },
                    new() { ProductCode = "P-ZERO", MinOrderQuantity = 0, StockQuantity = 1 },
                }
            )
            .ExecuteCommandAsync();
        await _hqDb.Insertable(
                new List<CBP_DIC_商品库存表>
                {
                    new()
                    {
                        HGUID = "HQ-KEEP",
                        H商品编码 = "P-KEEP",
                        H库存 = 30m,
                        H最小订货量 = 12m,
                        H使用状态 = 1,
                        FGC_LastModifyDate = new DateTime(2026, 8, 12, 2, 0, 0),
                    },
                    new()
                    {
                        HGUID = "HQ-ZERO",
                        H商品编码 = "P-ZERO",
                        H库存 = 40m,
                        H最小订货量 = 4m,
                        H使用状态 = 1,
                        FGC_LastModifyDate = new DateTime(2026, 8, 12, 2, 0, 0),
                    },
                }
            )
            .ExecuteCommandAsync();

        var result = await CreateService(CreateRealHistoryService()).SyncProductStocksIncrementalFromHqAsync(
            new DateTime(2026, 8, 12, 0, 0, 0)
        );

        Assert.True(result.IsSuccess, result.Message);
        var rows = await _localDb.Queryable<WarehouseProduct>().ToListAsync();
        Assert.Equal(6, rows.Single(row => row.ProductCode == "P-KEEP").MinOrderQuantity);
        Assert.Equal(30, rows.Single(row => row.ProductCode == "P-KEEP").StockQuantity);
        Assert.Equal(4, rows.Single(row => row.ProductCode == "P-ZERO").MinOrderQuantity);
    }

    [Fact]
    public async Task SyncProductsFromHqAsync_全量同步使用当前请求用户历史上下文()
    {
        await _hqDb.Insertable(
                new DIC_商品信息字典表
                {
                    ID = 1,
                    HGUID = "HQ-FULL",
                    H商品标签GUID = "",
                    H商品分类码GUID = "",
                    H供货商编码 = "",
                    H商品编码 = "P-FULL",
                    H货号 = "ITEM-FULL",
                    H主条形码 = "",
                    H商品名称 = "全量商品",
                    H大写名称 = "",
                    H规格 = "",
                    H单位 = "",
                    H进货价 = 1m,
                    H零售价 = 2m,
                    H使用状态 = true,
                    H商品图片 = "",
                    H腾讯云图地址 = "",
                    H进货单主表GUID = "",
                    H进货单详情GUID = "",
                    CBP商品中文名称 = "",
                    CBP供应商编码 = "",
                    CBP商品分类码GUID = "",
                    FGC_Creator = "",
                    FGC_LastModifier = "",
                    FGC_UpdateHelp = "",
                    FGC_CreateDate = new DateTime(2026, 8, 12),
                    FGC_LastModifyDate = new DateTime(2026, 8, 12),
                }
        )
            .ExecuteCommandAsync();
        var history = new RecordingHistoryService();
        var currentUser = CreateCurrentUser("user-guid-001", "同步操作员");

        var result = await CreateService(history, currentUser).SyncProductsFromHqAsync();

        Assert.True(result.IsSuccess, result.Message);
        var context = Assert.Single(history.Contexts);
        Assert.Equal("BatchUpdate", context.Action);
        Assert.Equal("DataSyncLegacyFull", context.Source);
        Assert.Equal("user-guid-001", context.ActorUserGuid);
        Assert.Equal("同步操作员", context.ActorName);
        Assert.Equal("User", context.ActorType);
        Assert.NotNull(context.BatchGuid);
        Assert.Equal(1, await _localDb.Queryable<Product>().CountAsync());
    }

    [Fact]
    public async Task SyncProductsFromHqAsync_全量同步保留同键套装子项并按HQ主成本重算()
    {
        await _localDb.Insertable(
            new Product
            {
                UUID = "LOCAL-PROTECTED-PRODUCT",
                ProductCode = "P-PROTECTED-FULL",
                ProductName = "旧套装",
                PurchasePrice = 5m,
                RetailPrice = 10m,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new ProductSetCode
            {
                SetCodeId = "LOCAL-PROTECTED-SET",
                ProductCode = "P-PROTECTED-FULL",
                SetProductCode = "M-PROTECTED",
                SetItemNumber = "LEGACY-ITEM-NUMBER",
                SetBarcode = "LOCAL-BARCODE",
                SetPurchasePrice = 5m,
                SetRetailPrice = 10m,
                SetType = 1,
                SetQuantity = 1,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _hqDb.Insertable(
            new DIC_商品信息字典表
            {
                ID = 100,
                HGUID = "HQ-PROTECTED-PRODUCT",
                H商品标签GUID = "",
                H商品分类码GUID = "",
                H供货商编码 = "",
                H商品编码 = "P-PROTECTED-FULL",
                H货号 = "ITEM-PROTECTED",
                H主条形码 = "",
                H商品名称 = "HQ套装",
                H大写名称 = "",
                H规格 = "",
                H单位 = "",
                H进货价 = 8m,
                H零售价 = 12m,
                H使用状态 = true,
                H商品图片 = "",
                H腾讯云图地址 = "",
                H进货单主表GUID = "",
                H进货单详情GUID = "",
                CBP商品中文名称 = "",
                CBP供应商编码 = "",
                CBP商品分类码GUID = "",
                FGC_Creator = "",
                FGC_LastModifier = "",
                FGC_UpdateHelp = "",
                FGC_CreateDate = new DateTime(2026, 8, 12),
                FGC_LastModifyDate = new DateTime(2026, 8, 12),
            }
        ).ExecuteCommandAsync();
        await _hqDb.Insertable(
            new DIC_一品多码表
            {
                HGUID = "HQ-CONFLICTING-MULTI",
                H商品编码 = "P-PROTECTED-FULL",
                H多码商品编号 = "M-PROTECTED",
                H多条形码 = "HQ-SHOULD-NOT-APPLY",
                H进货价 = 99m,
                H一品多码零售价 = 199m,
                H使用状态 = true,
                FGC_CreateDate = new DateTime(2026, 8, 12),
                FGC_LastModifyDate = new DateTime(2026, 8, 12),
            }
        ).ExecuteCommandAsync();

        var result = await CreateService(CreateRealHistoryService()).SyncProductsFromHqAsync();

        Assert.True(result.IsSuccess, result.Message);
        var protectedChild = await _localDb.Queryable<ProductSetCode>().SingleAsync();
        Assert.Equal("LOCAL-PROTECTED-SET", protectedChild.SetCodeId);
        Assert.Equal(1, protectedChild.SetType);
        Assert.Equal("M-PROTECTED", protectedChild.SetProductCode);
        Assert.Equal("LOCAL-BARCODE", protectedChild.SetBarcode);
        Assert.Equal(8m, protectedChild.SetPurchasePrice);
        Assert.Equal(10m, protectedChild.SetRetailPrice);
        Assert.True(protectedChild.IsActive);
        Assert.False(protectedChild.IsDeleted);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task SyncProductsFromHqAsync_停用或软删除Type1按Guid和规范化业务键保护(
        bool isActive,
        bool isDeleted
    )
    {
        const string productCode = "P-TYPE1-STATE";
        await _localDb.Insertable(
            new Product
            {
                UUID = "LOCAL-TYPE1-PRODUCT",
                ProductCode = productCode,
                ProductName = "本地套装",
                PurchasePrice = 5m,
                RetailPrice = 10m,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new ProductSetCode
            {
                SetCodeId = "LOCAL-TYPE1-ID",
                ProductCode = productCode,
                SetProductCode = "CHILD-STATE",
                SetItemNumber = "CHILD-STATE",
                SetBarcode = "LOCAL-TYPE1-BARCODE",
                SetPurchasePrice = 5m,
                SetRetailPrice = 10m,
                SetType = 1,
                SetQuantity = 1,
                IsActive = isActive,
                IsDeleted = isDeleted,
            }
        ).ExecuteCommandAsync();

        var hqProduct = CreateIncrementalProduct(201, productCode);
        hqProduct.H进货价 = 8m;
        await _hqDb.Insertable(hqProduct).ExecuteCommandAsync();
        await _hqDb.Insertable(
            new[]
            {
                new DIC_一品多码表
                {
                    HGUID = "HQ-CONFLICT-BY-KEY",
                    H商品编码 = productCode,
                    H多码商品编号 = " child-state ",
                    H多条形码 = "HQ-KEY-BARCODE",
                    H进货价 = 99m,
                    H一品多码零售价 = 199m,
                    H使用状态 = true,
                    FGC_LastModifyDate = new DateTime(2026, 8, 12, 3, 0, 0),
                },
                new DIC_一品多码表
                {
                    HGUID = " local-type1-id ",
                    H商品编码 = productCode,
                    H多码商品编号 = "OTHER-CHILD",
                    H多条形码 = "HQ-GUID-BARCODE",
                    H进货价 = 98m,
                    H一品多码零售价 = 198m,
                    H使用状态 = true,
                    FGC_LastModifyDate = new DateTime(2026, 8, 12, 3, 0, 0),
                },
            }
        ).ExecuteCommandAsync();

        var result = await CreateService(CreateRealHistoryService()).SyncProductsFromHqAsync();

        Assert.True(result.IsSuccess, result.Message);
        var protectedChild = Assert.Single(await _localDb.Queryable<ProductSetCode>().ToListAsync());
        Assert.Equal("LOCAL-TYPE1-ID", protectedChild.SetCodeId);
        Assert.Equal(1, protectedChild.SetType);
        Assert.Equal("CHILD-STATE", protectedChild.SetProductCode);
        Assert.Equal("LOCAL-TYPE1-BARCODE", protectedChild.SetBarcode);
        Assert.Equal(5m, protectedChild.SetPurchasePrice);
        Assert.Equal(isActive, protectedChild.IsActive);
        Assert.Equal(isDeleted, protectedChild.IsDeleted);
    }

    [Fact]
    public async Task SyncProductsFromHqAsync_HqType2成本最终使用本地主商品成本()
    {
        const string productCode = "P-TYPE2-FULL";
        var hqProduct = CreateIncrementalProduct(202, productCode);
        hqProduct.H进货价 = 8m;
        await _hqDb.Insertable(hqProduct).ExecuteCommandAsync();
        await _hqDb.Insertable(
            new DIC_一品多码表
            {
                HGUID = "HQ-TYPE2-FULL",
                H商品编码 = productCode,
                H多码商品编号 = "M-TYPE2-FULL",
                H多条形码 = "TYPE2-FULL-BARCODE",
                H进货价 = 99m,
                H一品多码零售价 = 20m,
                H使用状态 = true,
                FGC_LastModifyDate = new DateTime(2026, 8, 12, 3, 0, 0),
            }
        ).ExecuteCommandAsync();

        var result = await CreateService(CreateRealHistoryService()).SyncProductsFromHqAsync();

        Assert.True(result.IsSuccess, result.Message);
        var type2 = await _localDb.Queryable<ProductSetCode>().SingleAsync();
        Assert.Equal(2, type2.SetType);
        Assert.Equal(8m, type2.SetPurchasePrice);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task SyncProductsIncrementalFromHqAsync_停用或软删除Type1不会被更新为Type2(
        bool isActive,
        bool isDeleted
    )
    {
        const string productCode = "P-TYPE1-INCREMENTAL";
        await _localDb.Insertable(
            new Product
            {
                UUID = "LOCAL-INCREMENTAL-PRODUCT",
                ProductCode = productCode,
                ProductName = "本地套装",
                PurchasePrice = 5m,
                RetailPrice = 10m,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new ProductSetCode
            {
                SetCodeId = "LOCAL-INCREMENTAL-TYPE1",
                ProductCode = productCode,
                SetProductCode = "CHILD-INCREMENTAL",
                SetItemNumber = "CHILD-INCREMENTAL",
                SetBarcode = "LOCAL-INCREMENTAL-BARCODE",
                SetPurchasePrice = 5m,
                SetRetailPrice = 10m,
                SetType = 1,
                SetQuantity = 1,
                IsActive = isActive,
                IsDeleted = isDeleted,
            }
        ).ExecuteCommandAsync();
        var hqProduct = CreateIncrementalProduct(203, productCode);
        hqProduct.H进货价 = 8m;
        await _hqDb.Insertable(hqProduct).ExecuteCommandAsync();
        await _hqDb.Insertable(
            new DIC_一品多码表
            {
                HGUID = "HQ-INCREMENTAL-TYPE2",
                H商品编码 = productCode,
                H多码商品编号 = "CHILD-INCREMENTAL",
                H多条形码 = "HQ-INCREMENTAL-BARCODE",
                H进货价 = 99m,
                H一品多码零售价 = 199m,
                H使用状态 = true,
                FGC_LastModifyDate = new DateTime(2026, 8, 12, 3, 0, 0),
            }
        ).ExecuteCommandAsync();

        var result = await CreateService(CreateRealHistoryService())
            .SyncProductsIncrementalFromHqAsync(new DateTime(2026, 8, 12, 0, 0, 0));

        Assert.True(result.IsSuccess, result.Message);
        var protectedChild = Assert.Single(await _localDb.Queryable<ProductSetCode>().ToListAsync());
        Assert.Equal("LOCAL-INCREMENTAL-TYPE1", protectedChild.SetCodeId);
        Assert.Equal(1, protectedChild.SetType);
        Assert.Equal("LOCAL-INCREMENTAL-BARCODE", protectedChild.SetBarcode);
        Assert.Equal(isActive, protectedChild.IsActive);
        Assert.Equal(isDeleted, protectedChild.IsDeleted);
    }

    [Fact]
    public async Task SyncProductsIncrementalFromHqAsync_HqType2成本最终使用更新后的本地主商品成本()
    {
        const string productCode = "P-TYPE2-INCREMENTAL";
        const string childCode = "M-TYPE2-INCREMENTAL";
        await _localDb.Insertable(
            new Product
            {
                UUID = "LOCAL-TYPE2-INCREMENTAL-PRODUCT",
                ProductCode = productCode,
                ProductName = "本地多码商品",
                PurchasePrice = 5m,
                RetailPrice = 10m,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new ProductSetCode
            {
                SetCodeId = "LOCAL-TYPE2-INCREMENTAL-RELATION",
                ProductCode = productCode,
                SetProductCode = childCode,
                SetItemNumber = childCode,
                SetBarcode = "LOCAL-TYPE2-INCREMENTAL-BARCODE",
                SetPurchasePrice = 5m,
                SetRetailPrice = 10m,
                SetType = 2,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        var hqProduct = CreateIncrementalProduct(206, productCode);
        hqProduct.H进货价 = 8m;
        await _hqDb.Insertable(hqProduct).ExecuteCommandAsync();
        await _hqDb.Insertable(
            new DIC_一品多码表
            {
                HGUID = "HQ-TYPE2-INCREMENTAL",
                H商品编码 = productCode,
                H多码商品编号 = childCode,
                H多条形码 = "HQ-TYPE2-INCREMENTAL-BARCODE",
                H进货价 = 99m,
                H一品多码零售价 = 20m,
                H使用状态 = true,
                FGC_LastModifyDate = new DateTime(2026, 8, 12, 3, 0, 0),
            }
        ).ExecuteCommandAsync();

        var result = await CreateService(CreateRealHistoryService())
            .SyncProductsIncrementalFromHqAsync(new DateTime(2026, 8, 12, 0, 0, 0));

        Assert.True(result.IsSuccess, result.Message);
        var type2 = await _localDb.Queryable<ProductSetCode>().SingleAsync();
        Assert.Equal(2, type2.SetType);
        Assert.Equal(8m, type2.SetPurchasePrice);
    }

    [Fact]
    public async Task SyncStoreMultiCodeProductsFromHqAsync_Type2成本按各门店主成本分别校正()
    {
        const string productCode = "P-STORE-TYPE2";
        const string childCode = "M-STORE-TYPE2";
        await _localDb.Insertable(
            new Product
            {
                UUID = "LOCAL-STORE-TYPE2-PRODUCT",
                ProductCode = productCode,
                ProductName = "门店多码商品",
                PurchasePrice = 5m,
                RetailPrice = 20m,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new ProductSetCode
            {
                SetCodeId = "LOCAL-STORE-TYPE2-RELATION",
                ProductCode = productCode,
                SetProductCode = childCode,
                SetItemNumber = childCode,
                SetPurchasePrice = 5m,
                SetRetailPrice = 20m,
                SetType = 2,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new[]
            {
                new StoreRetailPrice
                {
                    UUID = "STORE-PRICE-S1",
                    StoreCode = "S1",
                    ProductCode = productCode,
                    PurchasePrice = 11m,
                    StoreRetailPriceValue = 21m,
                    IsActive = true,
                    IsDeleted = false,
                },
                new StoreRetailPrice
                {
                    UUID = "STORE-PRICE-S2",
                    StoreCode = "S2",
                    ProductCode = productCode,
                    PurchasePrice = 13m,
                    StoreRetailPriceValue = 23m,
                    IsActive = true,
                    IsDeleted = false,
                },
            }
        ).ExecuteCommandAsync();
        await _hqDb.Insertable(CreateIncrementalProduct(204, productCode)).ExecuteCommandAsync();
        await _hqDb.Insertable(
            new[]
            {
                new DIC_分店一品多码表
                {
                    HGUID = "HQ-STORE-TYPE2-S1",
                    H分店代码 = "S1",
                    H商品编码 = productCode,
                    H多码商品编码 = childCode,
                    H多条形码 = "STORE-TYPE2-S1",
                    H进货价 = 99m,
                    H一品多码零售价 = 21m,
                    H使用状态 = true,
                },
                new DIC_分店一品多码表
                {
                    HGUID = "HQ-STORE-TYPE2-S2",
                    H分店代码 = "S2",
                    H商品编码 = productCode,
                    H多码商品编码 = childCode,
                    H多条形码 = "STORE-TYPE2-S2",
                    H进货价 = 98m,
                    H一品多码零售价 = 23m,
                    H使用状态 = true,
                },
            }
        ).ExecuteCommandAsync();

        var result = await CreateService(CreateRealHistoryService())
            .SyncStoreMultiCodeProductsFromHqAsync(["S1", "S2"]);

        Assert.True(result.IsSuccess, result.Message);
        var storeCosts = (await _localDb.Queryable<StoreMultiCodeProduct>().ToListAsync())
            .ToDictionary(item => item.StoreCode!, item => item.PurchasePrice);
        Assert.Equal(11m, storeCosts["S1"]);
        Assert.Equal(13m, storeCosts["S2"]);
    }

    [Fact]
    public async Task SyncStoreMultiCodeProductsFromHqAsync_停用Type1的门店投影不会被HQ覆盖()
    {
        const string productCode = "P-STORE-TYPE1-INACTIVE";
        const string childCode = "M-STORE-TYPE1-INACTIVE";
        await _localDb.Insertable(
            new Product
            {
                UUID = "LOCAL-STORE-TYPE1-PRODUCT",
                ProductCode = productCode,
                ProductName = "停用套装",
                PurchasePrice = 4m,
                RetailPrice = 10m,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new ProductSetCode
            {
                SetCodeId = "LOCAL-STORE-TYPE1-RELATION",
                ProductCode = productCode,
                SetProductCode = childCode,
                SetItemNumber = childCode,
                SetPurchasePrice = 4m,
                SetRetailPrice = 10m,
                SetType = 1,
                IsActive = false,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new StoreMultiCodeProduct
            {
                UUID = "LOCAL-STORE-TYPE1-PROJECTION",
                StoreCode = "S1",
                ProductCode = productCode,
                MultiCodeProductCode = childCode,
                MultiBarcode = "LOCAL-STORE-TYPE1-BARCODE",
                PurchasePrice = 4m,
                MultiCodeRetailPrice = 10m,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _hqDb.Insertable(CreateIncrementalProduct(205, productCode)).ExecuteCommandAsync();
        await _hqDb.Insertable(
            new DIC_分店一品多码表
            {
                HGUID = "HQ-STORE-TYPE1-CONFLICT",
                H分店代码 = "S1",
                H商品编码 = productCode,
                H多码商品编码 = childCode,
                H多条形码 = "HQ-STORE-TYPE1-BARCODE",
                H进货价 = 99m,
                H一品多码零售价 = 199m,
                H使用状态 = true,
            }
        ).ExecuteCommandAsync();

        var result = await CreateService(CreateRealHistoryService())
            .SyncStoreMultiCodeProductsFromHqAsync(["S1"]);

        Assert.True(result.IsSuccess, result.Message);
        var protectedProjection = await _localDb.Queryable<StoreMultiCodeProduct>().SingleAsync();
        Assert.Equal("LOCAL-STORE-TYPE1-PROJECTION", protectedProjection.UUID);
        Assert.Equal("LOCAL-STORE-TYPE1-BARCODE", protectedProjection.MultiBarcode);
        Assert.Equal(4m, protectedProjection.PurchasePrice);
    }

    [Fact]
    public async Task SyncProductsIncrementalFromHqAsync_增量同步使用服务端批次和System历史上下文()
    {
        await _hqDb.Insertable(
                new DIC_商品信息字典表
                {
                    ID = 1,
                    HGUID = "HQ-INCREMENTAL",
                    H商品标签GUID = "",
                    H商品分类码GUID = "",
                    H供货商编码 = "",
                    H商品编码 = "P-INCREMENTAL",
                    H货号 = "ITEM-INCREMENTAL",
                    H主条形码 = "",
                    H商品名称 = "增量商品",
                    H大写名称 = "",
                    H规格 = "",
                    H单位 = "",
                    H进货价 = 1m,
                    H零售价 = 2m,
                    H使用状态 = true,
                    H商品图片 = "",
                    H腾讯云图地址 = "",
                    H进货单主表GUID = "",
                    H进货单详情GUID = "",
                    CBP商品中文名称 = "",
                    CBP供应商编码 = "",
                    CBP商品分类码GUID = "",
                    FGC_Creator = "",
                    FGC_LastModifier = "",
                    FGC_UpdateHelp = "",
                    FGC_CreateDate = new DateTime(2026, 8, 12),
                    FGC_LastModifyDate = new DateTime(2026, 8, 12, 2, 0, 0),
                }
            )
            .ExecuteCommandAsync();
        var history = new RecordingHistoryService();

        var result = await CreateService(history).SyncProductsIncrementalFromHqAsync(
            new DateTime(2026, 8, 12)
        );

        Assert.True(result.IsSuccess, result.Message);
        var context = Assert.Single(history.Contexts);
        Assert.Equal("BatchUpdate", context.Action);
        Assert.Equal("DataSyncLegacyIncremental", context.Source);
        Assert.Equal("System", context.ActorName);
        Assert.Equal("System", context.ActorType);
        Assert.NotNull(context.BatchGuid);
        Assert.Equal(1, await _localDb.Queryable<Product>().CountAsync());
    }

    [Fact]
    public async Task SyncProductsIncrementalFromHqAsync_跨页重复编码只捕获首次Before并在全部写入后记录一次历史()
    {
        const string repeatedCode = "P-PAGED-00000";
        await _hqDb.Insertable(
                Enumerable.Range(0, 5001)
                    .Select(index =>
                        CreateIncrementalProduct(
                            index + 1,
                            index == 5000 ? repeatedCode : $"P-PAGED-{index:D5}"
                        )
                    )
                    .ToList()
            )
            .ExecuteCommandAsync();
        var history = new RecordingHistoryService();

        var result = await CreateService(history).SyncProductsIncrementalFromHqAsync(
            new DateTime(2026, 8, 12)
        );

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, history.SnapshotCaptureRequests.Count);
        Assert.All(history.SnapshotCaptureRequests, request => Assert.Equal(5000, request.Count));
        Assert.Contains(repeatedCode, history.SnapshotCaptureRequests[0]);
        Assert.Contains(repeatedCode, history.SnapshotCaptureRequests[1]);
        Assert.Single(history.Recordings);
        Assert.Equal(5000, await _localDb.Queryable<Product>().CountAsync());
    }

    [Fact]
    public async Task SyncProductStocksIncrementalFromHqAsync_跨页重复编码只捕获首次Before并在全部写入后记录一次历史()
    {
        const string repeatedCode = "W-PAGED-00000";
        await _hqDb.Insertable(
                Enumerable.Range(0, 10001)
                    .Select(index =>
                        CreateIncrementalWarehouseStock(
                            index + 1,
                            index == 10000 ? repeatedCode : $"W-PAGED-{index:D5}"
                        )
                    )
                    .ToList()
            )
            .ExecuteCommandAsync();
        var history = new RecordingHistoryService();

        var result = await CreateService(history).SyncProductStocksIncrementalFromHqAsync(
            new DateTime(2026, 8, 12)
        );

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, history.SnapshotCaptureRequests.Count);
        Assert.All(history.SnapshotCaptureRequests, request => Assert.Equal(10000, request.Count));
        Assert.Contains(repeatedCode, history.SnapshotCaptureRequests[0]);
        Assert.Contains(repeatedCode, history.SnapshotCaptureRequests[1]);
        Assert.Single(history.Recordings);
        Assert.Equal(10000, await _localDb.Queryable<WarehouseProduct>().CountAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("System")]
    public async Task SyncProductsIncrementalFromHqAsync_有当前用户Guid但名称缺失或System时保留用户审计身份(
        string? actorName
    )
    {
        await _hqDb.Insertable(CreateIncrementalProduct(1, "P-ACTOR-GUID")).ExecuteCommandAsync();
        var currentUser = CreateCurrentUser("actor-guid-001", actorName);

        var result = await CreateService(CreateRealHistoryService(), currentUser)
            .SyncProductsIncrementalFromHqAsync(new DateTime(2026, 8, 12));

        Assert.True(result.IsSuccess, result.Message);
        var history = await _localDb.Queryable<WarehouseProductChangeHistory>().SingleAsync();
        Assert.Equal("actor-guid-001", history.ActorUserGuid);
        Assert.Equal("actor-guid-001", history.ActorName);
        Assert.Equal("User", history.ActorType);
    }

    [Fact]
    public async Task SyncProductsIncrementalFromHqAsync_历史写入失败时回滚全部业务写入并清零成功数()
    {
        await _hqDb.Insertable(CreateIncrementalProduct(1, "P-ROLLBACK-HISTORY")).ExecuteCommandAsync();

        var result = await CreateService(new ThrowingHistoryService())
            .SyncProductsIncrementalFromHqAsync(new DateTime(2026, 8, 12));

        Assert.False(result.IsSuccess);
        Assert.Equal(0, result.AddedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(0, await _localDb.Queryable<Product>().CountAsync());
    }

    [Fact]
    public async Task SyncDomesticProductsFromHqAsync_国内商品同步使用全量System历史上下文()
    {
        await _hbSalesDb.Insertable(
                new CPT_DIC_商品信息字典表
                {
                    商品编码 = "P-DOMESTIC",
                    供应商编码 = "SUP-01",
                    HB货号 = "HB-DOMESTIC",
                    中文名称 = "国内商品",
                    国内价格 = 1m,
                    进口价格 = 2m,
                    贴牌价格 = 3m,
                    使用状态 = 1,
                }
            )
            .ExecuteCommandAsync();
        var history = new RecordingHistoryService();

        var result = await CreateService(history).SyncDomesticProductsFromHqAsync();

        Assert.True(result.IsSuccess, result.Message);
        var context = Assert.Single(history.Contexts);
        Assert.Equal("BatchUpdate", context.Action);
        Assert.Equal("DataSyncLegacyFull", context.Source);
        Assert.Equal("System", context.ActorName);
        Assert.Equal("System", context.ActorType);
        Assert.NotNull(context.BatchGuid);
        Assert.Equal(1, await _localDb.Queryable<DomesticProduct>().CountAsync());
    }

    [Fact]
    public async Task TranslateAllProductNamesAsync_批量翻译记录当前用户和共享批次()
    {
        await _localDb.Insertable(new Product
        {
            UUID = "UUID-TRANSLATE-1",
            ProductCode = "P-TRANSLATE-1",
            ProductName = "中文商品",
            EnglishName = null,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        var translation = CreateTranslationService("中文商品", "Chinese Product");
        var history = new RecordingHistoryService();

        var result = await CreateService(
                history,
                CreateCurrentUser("translator-guid", "translator-user"),
                translation
            )
            .TranslateAllProductNamesAsync();

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, result.UpdatedCount);
        var product = await _localDb.Queryable<Product>()
            .SingleAsync(item => item.ProductCode == "P-TRANSLATE-1");
        Assert.Equal("Chinese Product", product.EnglishName);
        Assert.Equal("translator-user", product.UpdatedBy);
        var context = Assert.Single(history.Contexts);
        Assert.Equal("BatchUpdate", context.Action);
        Assert.Equal("ProductTranslation", context.Source);
        Assert.Equal("translator-guid", context.ActorUserGuid);
        Assert.Equal("translator-user", context.ActorName);
        Assert.Equal("User", context.ActorType);
        Assert.NotNull(context.BatchGuid);
    }

    [Fact]
    public async Task TranslateProductNamesAsync_历史失败回滚英文名称()
    {
        await _localDb.Insertable(new Product
        {
            UUID = "UUID-TRANSLATE-ROLLBACK",
            ProductCode = "P-TRANSLATE-ROLLBACK",
            ProductName = "回滚商品",
            EnglishName = null,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        var translation = CreateTranslationService("回滚商品", "Rollback Product");

        var result = await CreateService(
                new ThrowingHistoryService(),
                CreateCurrentUser("translator-guid", "translator-user"),
                translation
            )
            .TranslateProductNamesAsync("untranslated");

        Assert.False(result.IsSuccess);
        Assert.Equal(0, result.UpdatedCount);
        var product = await _localDb.Queryable<Product>()
            .SingleAsync(item => item.ProductCode == "P-TRANSLATE-ROLLBACK");
        Assert.Null(product.EnglishName);
    }

    public void Dispose()
    {
        _localDb.Dispose();
        _hqDb.Dispose();
        _hbSalesDb.Dispose();
        _localConnection.Dispose();
        _hqConnection.Dispose();
        _hbSalesConnection.Dispose();
        File.Delete(_localDbPath);
        File.Delete(_hqDbPath);
        File.Delete(_hbSalesDbPath);
    }

    private DataSyncService CreateService(
        IWarehouseProductChangeHistoryService historyService,
        ICurrentUserService? currentUserService = null,
        ITranslationService? translationService = null,
        IMapper? mapper = null
    )
    {
        var localContext = CreateSqlSugarContext(_localDb);
        var hqContext = CreateHqSqlSugarContext(_hqDb);
        return new DataSyncService(
            localContext,
            hqContext,
            CreateHBSalesSqlSugarContext(_hbSalesDb),
            NullLogger<DataSyncService>.Instance,
            mapper ?? _mapper,
            translationService ?? Mock.Of<ITranslationService>(),
            new ConfigurationBuilder().Build(),
            historyService,
            currentUserService ?? Mock.Of<ICurrentUserService>()
        );
    }

    private static ITranslationService CreateTranslationService(
        string source,
        string translated
    )
    {
        var translation = new Mock<ITranslationService>(MockBehavior.Strict);
        translation.Setup(service => service.ContainsChinese(source)).Returns(true);
        translation
            .Setup(service =>
                service.BatchTranslateToEnglishAsync(
                    It.Is<List<string>>(names => names.SequenceEqual(new[] { source }))
                )
            )
            .ReturnsAsync(new Dictionary<string, string> { [source] = translated });
        return translation.Object;
    }

    private static DIC_商品信息字典表 CreateIncrementalProduct(int id, string productCode) =>
        new()
        {
            ID = id,
            HGUID = $"HQ-PRODUCT-{id}",
            H商品标签GUID = string.Empty,
            H商品分类码GUID = string.Empty,
            H供货商编码 = string.Empty,
            H商品编码 = productCode,
            H货号 = $"ITEM-{id}",
            H主条形码 = string.Empty,
            H商品名称 = $"分页商品-{id}",
            H大写名称 = string.Empty,
            H规格 = string.Empty,
            H单位 = string.Empty,
            H进货价 = 1m,
            H零售价 = 2m,
            H使用状态 = true,
            H商品图片 = string.Empty,
            H腾讯云图地址 = string.Empty,
            H进货单主表GUID = string.Empty,
            H进货单详情GUID = string.Empty,
            CBP商品中文名称 = string.Empty,
            CBP供应商编码 = string.Empty,
            CBP商品分类码GUID = string.Empty,
            FGC_Creator = string.Empty,
            FGC_LastModifier = string.Empty,
            FGC_UpdateHelp = string.Empty,
            FGC_CreateDate = new DateTime(2026, 8, 12),
            FGC_LastModifyDate = new DateTime(2026, 8, 12, 2, 0, 0),
        };

    private static CBP_DIC_商品库存表 CreateIncrementalWarehouseStock(int id, string productCode) =>
        new()
        {
            ID = id,
            HGUID = $"HQ-WAREHOUSE-{id}",
            H商品编码 = productCode,
            H国内价格 = 1m,
            H贴牌价格 = 2m,
            H进口价格 = 3m,
            H库存 = 10m,
            H最小订货量 = 4m,
            H库存金额 = 30m,
            H库存预警数 = 5,
            H使用状态 = 1,
            FGC_LastModifyDate = new DateTime(2026, 8, 12, 2, 0, 0),
        };

    private static ICurrentUserService CreateCurrentUser(string userGuid, string? username)
    {
        var currentUser = new Mock<ICurrentUserService>(MockBehavior.Strict);
        currentUser.Setup(service => service.GetCurrentUserGuid()).Returns(userGuid);
        currentUser.Setup(service => service.GetCurrentUsername()).Returns(username!);
        return currentUser.Object;
    }

    private IWarehouseProductChangeHistoryService CreateRealHistoryService() =>
        new WarehouseProductChangeHistoryService(
            CreateSqlSugarContext(_localDb),
            NullLogger<WarehouseProductChangeHistoryService>.Instance,
            Mock.Of<ICurrentUserService>()
        );

    private static ConnectionConfig CreateConnectionConfig(string connectionString) =>
        new()
        {
            ConnectionString = connectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        };

    private static string ResolveRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"未找到仓库文件: {relativePath}");
    }

    private static string ExtractMethodBody(string source, string methodName)
    {
        var signatureStart = source.IndexOf(methodName, StringComparison.Ordinal);
        if (signatureStart < 0)
            throw new InvalidOperationException($"未找到方法: {methodName}");

        var bodyStart = source.IndexOf('{', signatureStart);
        if (bodyStart < 0)
            throw new InvalidOperationException($"未找到方法体: {methodName}");

        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[bodyStart..(index + 1)];
        }

        throw new InvalidOperationException($"无法读取方法体: {methodName}");
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    private static HqSqlSugarContext CreateHqSqlSugarContext(ISqlSugarClient db)
    {
        var context = (HqSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HqSqlSugarContext));
        typeof(HqSqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    private static HBSalesSqlSugarContext CreateHBSalesSqlSugarContext(SqlSugarScope db)
    {
        var context = (HBSalesSqlSugarContext)
            RuntimeHelpers.GetUninitializedObject(typeof(HBSalesSqlSugarContext));
        typeof(HBSalesSqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    private static TContext CreateContext<TContext>()
        where TContext : class =>
        (TContext)RuntimeHelpers.GetUninitializedObject(typeof(TContext));

    private sealed class ThrowingHistoryService : IWarehouseProductChangeHistoryService
    {
        public Task<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>> CaptureSnapshotsAsync(
            IEnumerable<string> productCodes,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(
            new Dictionary<string, WarehouseProductChangeSnapshotDto>(StringComparer.OrdinalIgnoreCase)
        );

        public Task<int> RecordChangesAsync(
            IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto> beforeSnapshots,
            IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto> afterSnapshots,
            WarehouseProductChangeHistoryContextDto context,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("历史写入失败");

        public Task<WarehouseProductChangeHistoryPageDto> GetChangeHistoryAsync(
            string productCode,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }

    private sealed class RecordingHistoryService : IWarehouseProductChangeHistoryService
    {
        public List<WarehouseProductChangeHistoryContextDto> Contexts { get; } = [];
        public List<IReadOnlySet<string>> SnapshotCaptureRequests { get; } = [];
        public List<WarehouseProductChangeHistoryContextDto> Recordings { get; } = [];

        public Task<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>> CaptureSnapshotsAsync(
            IEnumerable<string> productCodes,
            CancellationToken cancellationToken = default
        )
        {
            SnapshotCaptureRequests.Add(
                new HashSet<string>(
                    productCodes.Where(code => !string.IsNullOrWhiteSpace(code)),
                    StringComparer.OrdinalIgnoreCase
                )
            );
            return Task.FromResult<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(
                new Dictionary<string, WarehouseProductChangeSnapshotDto>(StringComparer.OrdinalIgnoreCase)
            );
        }

        public Task<int> RecordChangesAsync(
            IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto> beforeSnapshots,
            IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto> afterSnapshots,
            WarehouseProductChangeHistoryContextDto context,
            CancellationToken cancellationToken = default
        )
        {
            Contexts.Add(context);
            Recordings.Add(context);
            return Task.FromResult(0);
        }

        public Task<WarehouseProductChangeHistoryPageDto> GetChangeHistoryAsync(
            string productCode,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();
    }
}
