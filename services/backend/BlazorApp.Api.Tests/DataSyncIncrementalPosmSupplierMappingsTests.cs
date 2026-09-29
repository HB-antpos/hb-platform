using System.Reflection;
using System.Runtime.CompilerServices;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.Background;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;
using HbTaskStatus = BlazorApp.Shared.Models.HBweb.TaskStatus;

namespace BlazorApp.Api.Tests;

public sealed class DataSyncIncrementalPosmSupplierMappingsTests : IDisposable
{
    private readonly string _localDbPath;
    private readonly string _posmDbPath;
    private readonly SqliteConnection _localConnection;
    private readonly SqliteConnection _posmConnection;
    private readonly SqlSugarClient _localDb;
    private readonly SqlSugarClient _posmDb;

    public DataSyncIncrementalPosmSupplierMappingsTests()
    {
        _localDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _posmDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _localConnection = new SqliteConnection($"Data Source={_localDbPath}");
        _posmConnection = new SqliteConnection($"Data Source={_posmDbPath}");
        _localConnection.Open();
        _posmConnection.Open();

        _localDb = new SqlSugarClient(CreateConnectionConfig(_localConnection.ConnectionString));
        _posmDb = new SqlSugarClient(CreateConnectionConfig(_posmConnection.ConnectionString));

        _localDb.CodeFirst.InitTables(typeof(Product), typeof(WarehouseProduct), typeof(DomesticProduct));
        _posmDb.CodeFirst.InitTables(typeof(PosmProductSupplierMapping));
        CreateScheduledTaskLogTable(_localDb);
    }

    [Fact]
    public async Task SyncPosmProductSupplierMappingsIncrementalAsync_外部已有运行中定时任务时复用任务日志并完成同步()
    {
        var now = DateTime.UtcNow;
        var externalTaskLog = await SeedRunningScheduledTaskLogAsync(now);
        await SeedProductAsync("P-UPDATE", "100", now);
        await SeedProductAsync("P-INSERT", "200", now);
        await SeedWarehouseProductWithDomesticSupplierAsync("P-INSERT", "CN-200");
        await _posmDb.Insertable(
            new PosmProductSupplierMapping
            {
                ProductCode = "P-UPDATE",
                LocalSupplierCode = "OLD",
                ChinaSupplierCode = "OLD-CN",
                LastUpdateTime = now.AddDays(-1),
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();

        var result = await CreateService().SyncPosmProductSupplierMappingsIncrementalAsync(
            now.AddDays(-1)
        );

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, result.AddedCount);
        Assert.Equal(1, result.UpdatedCount);

        var mappings = await _posmDb.Queryable<PosmProductSupplierMapping>()
            .OrderBy(x => x.ProductCode)
            .ToListAsync();
        Assert.Equal(2, mappings.Count);

        var updated = Assert.Single(mappings, x => x.ProductCode == "P-UPDATE");
        Assert.Equal("100", updated.LocalSupplierCode);
        Assert.Null(updated.ChinaSupplierCode);

        var inserted = Assert.Single(mappings, x => x.ProductCode == "P-INSERT");
        Assert.Equal("200", inserted.LocalSupplierCode);
        Assert.Equal("CN-200", inserted.ChinaSupplierCode);

        var taskLogs = await _localDb.Queryable<ScheduledTaskLog>()
            .Where(x => x.TaskType == TaskType.SyncPosmProductSupplierMappingsIncremental)
            .ToListAsync();
        var taskLog = Assert.Single(taskLogs);
        Assert.Equal(externalTaskLog.Id, taskLog.Id);
        Assert.Equal(HbTaskStatus.Success, taskLog.Status);
        Assert.NotNull(taskLog.CompletedAt);
    }

    [Fact]
    public async Task SyncPosmProductSupplierMappingsIncrementalAsync_复用外部任务日志时失败会回写失败状态()
    {
        var externalTaskLog = await SeedRunningScheduledTaskLogAsync(DateTime.UtcNow);
        var service = CreateService(posmContext: CreateContext<POSMSqlSugarContext>());

        var result = await service.SyncPosmProductSupplierMappingsIncrementalAsync(DateTime.UtcNow.AddDays(-1));

        Assert.False(result.IsSuccess);

        var taskLogs = await _localDb.Queryable<ScheduledTaskLog>()
            .Where(x => x.TaskType == TaskType.SyncPosmProductSupplierMappingsIncremental)
            .ToListAsync();
        var taskLog = Assert.Single(taskLogs);
        Assert.Equal(externalTaskLog.Id, taskLog.Id);
        Assert.Equal(HbTaskStatus.Failed, taskLog.Status);
        Assert.False(string.IsNullOrWhiteSpace(taskLog.ErrorMessage));
    }

    [Fact]
    public async Task SyncPosmProductSupplierMappingsIncrementalAsync_国内供应商关系更新会刷新既有映射()
    {
        var start = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedProductAsync("P-RELATION", "200", start.AddDays(-1));
        await SeedWarehouseProductWithDomesticSupplierAsync("P-RELATION", "CN-NEW", start.AddHours(1));
        await SeedPosmMappingAsync("P-RELATION", "200", "CN-OLD", start.AddDays(-1));

        var result = await CreateService().SyncPosmProductSupplierMappingsIncrementalAsync(start);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, result.UpdatedCount);
        var mapping = await _posmDb.Queryable<PosmProductSupplierMapping>()
            .SingleAsync(row => row.ProductCode == "P-RELATION");
        Assert.Equal("CN-NEW", mapping.ChinaSupplierCode);
        Assert.False(mapping.IsDeleted);
    }

    [Fact]
    public async Task SyncPosmProductSupplierMappingsIncrementalAsync_仓库关系软删除会清空中国供应商编码()
    {
        var start = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedProductAsync("P-WAREHOUSE-DELETED", "200", start.AddDays(-1));
        await SeedWarehouseProductWithDomesticSupplierAsync(
            "P-WAREHOUSE-DELETED", "CN-OLD", start.AddHours(1), warehouseIsDeleted: true);
        await SeedPosmMappingAsync("P-WAREHOUSE-DELETED", "200", "CN-OLD", start.AddDays(-1));

        var result = await CreateService().SyncPosmProductSupplierMappingsIncrementalAsync(start);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, result.UpdatedCount);
        var mapping = await _posmDb.Queryable<PosmProductSupplierMapping>()
            .SingleAsync(row => row.ProductCode == "P-WAREHOUSE-DELETED");
        Assert.Null(mapping.ChinaSupplierCode);
        Assert.False(mapping.IsDeleted);
    }

    [Fact]
    public async Task SyncPosmProductSupplierMappingsIncrementalAsync_商品软删除保留历史映射()
    {
        var start = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedProductAsync("P-PRODUCT-DELETED", "200", start.AddHours(1), isDeleted: true);
        await SeedPosmMappingAsync("P-PRODUCT-DELETED", "200", "CN-OLD", start.AddDays(-1));

        var result = await CreateService().SyncPosmProductSupplierMappingsIncrementalAsync(start);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(0, result.DeletedCount);
        var mapping = await _posmDb.Queryable<PosmProductSupplierMapping>()
            .SingleAsync(row => row.ProductCode == "P-PRODUCT-DELETED");
        Assert.Equal("CN-OLD", mapping.ChinaSupplierCode);
        Assert.False(mapping.IsDeleted);
    }

    [Fact]
    public async Task SyncPosmProductSupplierMappingsIncrementalAsync_定时运行会捕获上次成功之后的关系变更()
    {
        var now = DateTime.UtcNow;
        // 上一次定时运行 20 分钟前成功；本次由调度外层先写好运行中日志，服务内复用它。
        await SeedScheduledTaskLogAsync(now.AddMinutes(-20), HbTaskStatus.Success);
        var currentTaskLog = await SeedScheduledTaskLogAsync(now, HbTaskStatus.Running);

        // 复刻 G093096：商品本身 UpdatedAt 很旧，仓库/国内商品关系在上次成功之后才建立，POSM 映射仍为空。
        await SeedProductAsync("P-RELATION-AFTER-LAST-RUN", "200", now.AddYears(-3));
        await SeedWarehouseProductWithDomesticSupplierAsync(
            "P-RELATION-AFTER-LAST-RUN", "HB038", now.AddMinutes(-5));
        await SeedPosmMappingAsync("P-RELATION-AFTER-LAST-RUN", "200", null, now.AddMonths(-5));

        // 早于上次成功且超出重叠区的变更应已由上一次运行处理，本次不会回扫到它。
        await SeedProductAsync("P-RELATION-BEFORE-LAST-RUN", "200", now.AddYears(-3));
        await SeedWarehouseProductWithDomesticSupplierAsync(
            "P-RELATION-BEFORE-LAST-RUN", "CN-NEW", now.AddHours(-3));
        await SeedPosmMappingAsync("P-RELATION-BEFORE-LAST-RUN", "200", "CN-OLD", now.AddMonths(-5));

        var result = await CreateService().SyncPosmProductSupplierMappingsIncrementalAsync();

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(0, result.AddedCount);
        Assert.Equal(1, result.UpdatedCount);

        var mappings = await _posmDb.Queryable<PosmProductSupplierMapping>().ToListAsync();
        var changedAfterLastRun = Assert.Single(mappings, x => x.ProductCode == "P-RELATION-AFTER-LAST-RUN");
        Assert.Equal("HB038", changedAfterLastRun.ChinaSupplierCode);
        var changedBeforeLastRun = Assert.Single(mappings, x => x.ProductCode == "P-RELATION-BEFORE-LAST-RUN");
        Assert.Equal("CN-OLD", changedBeforeLastRun.ChinaSupplierCode);

        var taskLog = await _localDb.Queryable<ScheduledTaskLog>()
            .SingleAsync(x => x.Id == currentTaskLog.Id);
        Assert.Equal(HbTaskStatus.Success, taskLog.Status);
    }

    [Fact]
    public void ResolvePosmMappingIncrementalStart_上次成功在几分钟前时从上次开始时间向前重叠而不按整天截断()
    {
        var utcNow = new DateTime(2026, 9, 21, 6, 6, 50, DateTimeKind.Utc);
        // 从 SQL Server 读回的任务日志时间值是 UTC，但 Kind 为 Unspecified。
        var lastSuccessStartedAt = DateTime.SpecifyKind(utcNow.AddMinutes(-20), DateTimeKind.Unspecified);

        var start = DataSyncIncrementalService.ResolvePosmMappingIncrementalStart(lastSuccessStartedAt, utcNow);

        Assert.Equal(utcNow.AddMinutes(-30), start);
        Assert.Equal(DateTimeKind.Utc, start.Kind);
    }

    [Fact]
    public void ResolvePosmMappingIncrementalStart_上次成功在几小时前时覆盖整段间隔()
    {
        // 悉尼 20 点停调度、次日 7 点恢复，间隔约 11 小时，整天截断同样会得到 0 天。
        var utcNow = new DateTime(2026, 9, 21, 21, 6, 50, DateTimeKind.Utc);

        var start = DataSyncIncrementalService.ResolvePosmMappingIncrementalStart(utcNow.AddHours(-11), utcNow);

        Assert.Equal(utcNow.AddHours(-11).AddMinutes(-10), start);
    }

    [Fact]
    public void ResolvePosmMappingIncrementalStart_上次成功超过30天时最多回溯30天()
    {
        var utcNow = new DateTime(2026, 9, 21, 6, 6, 50, DateTimeKind.Utc);

        var start = DataSyncIncrementalService.ResolvePosmMappingIncrementalStart(utcNow.AddDays(-45), utcNow);

        Assert.Equal(utcNow.AddDays(-30), start);
    }

    [Fact]
    public void ResolvePosmMappingIncrementalStart_没有成功记录时回溯最近30天()
    {
        var utcNow = new DateTime(2026, 9, 21, 6, 6, 50, DateTimeKind.Utc);

        var start = DataSyncIncrementalService.ResolvePosmMappingIncrementalStart(null, utcNow);

        Assert.Equal(utcNow.AddDays(-30), start);
    }

    [Fact]
    public void SyncPosmProductSupplierMappingsAsync_全量同步契约保留历史并可激活软删除映射()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourcePath = Path.Combine(
            repositoryRoot,
            "services/backend/BlazorApp.Api/Services/React/DataSyncFullService.cs"
        );
        var source = File.ReadAllText(sourcePath);
        var methodStart = source.IndexOf("public async Task<SyncResult> SyncPosmProductSupplierMappingsAsync()", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("public async Task<SyncResult> SyncProductCategoriesFromHqAsync", methodStart, StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "未找到全量 POSM 映射同步方法。");
        Assert.True(methodEnd > methodStart, "无法确定全量 POSM 映射同步方法边界。");
        var method = source[methodStart..methodEnd];

        Assert.DoesNotContain("Deleteable<PosmProductSupplierMapping>", method, StringComparison.Ordinal);
        Assert.Contains("|| existing.IsDeleted", method, StringComparison.Ordinal);
        Assert.Contains(".ToListAsync();", method, StringComparison.Ordinal);
    }

    [Fact]
    public void POSM映射全量与增量同步必须在同一执行租约内读取快照并提交()
    {
        var repositoryRoot = FindRepositoryRoot();
        var fullMethod = ReadMethod(
            Path.Combine(
                repositoryRoot,
                "services/backend/BlazorApp.Api/Services/React/DataSyncFullService.cs"
            ),
            "public async Task<SyncResult> SyncPosmProductSupplierMappingsAsync()",
            "public async Task<SyncResult> SyncProductCategoriesFromHqAsync"
        );
        var incrementalMethod = ReadMethod(
            Path.Combine(
                repositoryRoot,
                "services/backend/BlazorApp.Api/Services/React/DataSyncIncrementalService.cs"
            ),
            "public async Task<SyncResult> SyncPosmProductSupplierMappingsIncrementalAsync(",
            "private async Task<ScheduledTaskLog> StartPosmIncrementalTaskLogAsync"
        );

        AssertLeaseCoversSnapshotAndCommit(fullMethod, "productsTask");
        AssertLeaseCoversSnapshotAndCommit(incrementalMethod, "changedProducts");
    }

    [Fact]
    public void POSM映射执行租约必须使用统一资源和SQLServer事务级排他锁()
    {
        var source = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "services/backend/BlazorApp.Api/Services/React/PosmProductSupplierMappingSyncLock.cs"
            )
        );

        Assert.Contains("SemaphoreSlim", source, StringComparison.Ordinal);
        Assert.Contains("HB:PosmProductSupplierMappingSync", source, StringComparison.Ordinal);
        Assert.Contains("sys.sp_getapplock", source, StringComparison.Ordinal);
        Assert.Contains("@LockMode = 'Exclusive'", source, StringComparison.Ordinal);
        Assert.Contains("@LockOwner = 'Transaction'", source, StringComparison.Ordinal);
        Assert.Contains("LockTimeoutMilliseconds = 10_000", source, StringComparison.Ordinal);
        Assert.Contains(
            "WaitAsync(TimeSpan.FromMilliseconds(LockTimeoutMilliseconds))",
            source,
            StringComparison.Ordinal
        );
        Assert.Contains("PosmProductSupplierMappingSyncLockException", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task POSM映射增量同步并发运行时第二次不会进入源快照且最终只有一条映射()
    {
        var now = DateTime.UtcNow;
        await SeedProductAsync("P-CONCURRENT", "200", now);
        await SeedWarehouseProductWithDomesticSupplierAsync("P-CONCURRENT", "CN-CONCURRENT", now);

        var testProbeProperty = typeof(PosmProductSupplierMappingSyncLock).GetProperty(
            "TestProbeAsync",
            BindingFlags.Static | BindingFlags.NonPublic
        );
        Assert.NotNull(testProbeProperty);
        var originalProbe = (Func<int, Task>?)testProbeProperty.GetValue(null);
        var firstSnapshotEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWaitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstToContinue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitStartedCount = 0;
        var snapshotEnteredCount = 0;

        testProbeProperty.SetValue(
            null,
            (Func<int, Task>)(async phase =>
            {
                if (phase == 1 && Interlocked.Increment(ref waitStartedCount) == 2)
                {
                    secondWaitStarted.TrySetResult();
                }

                if (phase == 2 && Interlocked.Increment(ref snapshotEnteredCount) == 1)
                {
                    firstSnapshotEntered.TrySetResult();
                    await allowFirstToContinue.Task;
                }
            })
        );

        try
        {
            var firstRun = CreateService().SyncPosmProductSupplierMappingsIncrementalAsync(
                now.AddMinutes(-1)
            );
            await firstSnapshotEntered.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

            var secondRun = CreateService().SyncPosmProductSupplierMappingsIncrementalAsync(
                now.AddMinutes(-1)
            );
            await secondWaitStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

            // 第二个运行已经请求租约，但第一个尚未放行，绝不能读取源快照或基于旧 POSM 快照插入。
            Assert.Equal(1, Volatile.Read(ref snapshotEnteredCount));
            allowFirstToContinue.TrySetResult();

            var results = await Task.WhenAll(firstRun, secondRun);
            Assert.All(results, result => Assert.True(result.IsSuccess, result.Message));

            var mappings = await _posmDb.Queryable<PosmProductSupplierMapping>()
                .Where(mapping => mapping.ProductCode == "P-CONCURRENT")
                .ToListAsync();
            var mapping = Assert.Single(mappings);
            Assert.Equal("200", mapping.LocalSupplierCode);
            Assert.Equal("CN-CONCURRENT", mapping.ChinaSupplierCode);
        }
        finally
        {
            allowFirstToContinue.TrySetResult();
            testProbeProperty.SetValue(null, originalProbe);
        }
    }

    public void Dispose()
    {
        _localDb.Dispose();
        _posmDb.Dispose();
        _localConnection.Dispose();
        _posmConnection.Dispose();
        DeleteIfExists(_localDbPath);
        DeleteIfExists(_posmDbPath);
    }

    private DataSyncIncrementalService CreateService(POSMSqlSugarContext? posmContext = null)
    {
        var localContext = CreateSqlSugarContext(_localDb);

        return new DataSyncIncrementalService(
            localContext,
            CreateContext<HqSqlSugarContext>(),
            CreateContext<HBSalesSqlSugarContext>(),
            posmContext ?? CreatePosmSqlSugarContext(_posmDb),
            new ConfigurationBuilder().Build(),
            Mock.Of<IMapper>(),
            NullLogger<DataSyncIncrementalService>.Instance,
            new ScheduledTaskLogService(
                localContext,
                NullLogger<ScheduledTaskLogService>.Instance
            ),
            Mock.Of<IStoreRetailPriceHqSyncService>(),
            new MemoryCache(new MemoryCacheOptions()),
            Mock.Of<IWarehouseProductChangeHistoryService>(),
            Mock.Of<ICurrentUserService>()
        );
    }

    private Task<ScheduledTaskLog> SeedRunningScheduledTaskLogAsync(DateTime startedAt) =>
        SeedScheduledTaskLogAsync(startedAt, HbTaskStatus.Running);

    private async Task<ScheduledTaskLog> SeedScheduledTaskLogAsync(DateTime startedAt, string status)
    {
        var taskLog = new ScheduledTaskLog
        {
            Id = Guid.NewGuid(),
            TaskType = TaskType.SyncPosmProductSupplierMappingsIncremental,
            Status = status,
            StartedAt = startedAt,
            CompletedAt = status == HbTaskStatus.Running ? null : startedAt.AddSeconds(2),
            ScheduledTime = startedAt,
            TriggeredBy = TaskTrigger.Scheduled,
            RetryCount = 0,
            CanRetry = true,
            CreatedAt = startedAt,
            UpdatedAt = startedAt,
            IsDeleted = false,
        };
        await _localDb.Insertable(taskLog).ExecuteCommandAsync();
        return taskLog;
    }

    private async Task SeedProductAsync(
        string productCode,
        string localSupplierCode,
        DateTime updatedAt,
        bool isDeleted = false
    )
    {
        await _localDb.Insertable(
            new Product
            {
                UUID = Guid.NewGuid().ToString("N"),
                ProductCode = productCode,
                LocalSupplierCode = localSupplierCode,
                ProductName = productCode,
                UpdatedAt = updatedAt,
                CreatedAt = updatedAt.AddDays(-1),
                IsDeleted = isDeleted,
            }
        ).ExecuteCommandAsync();
    }

    private async Task SeedWarehouseProductWithDomesticSupplierAsync(
        string productCode,
        string supplierCode,
        DateTime? updatedAt = null,
        bool warehouseIsDeleted = false
    )
    {
        var timestamp = updatedAt ?? DateTime.UtcNow;
        await _localDb.Insertable(
            new DomesticProduct
            {
                ProductCode = productCode,
                SupplierCode = supplierCode,
                CreatedAt = timestamp.AddDays(-1),
                UpdatedAt = timestamp,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();

        await _localDb.Insertable(
            new WarehouseProduct
            {
                ProductCode = productCode,
                CreatedAt = timestamp.AddDays(-1),
                UpdatedAt = timestamp,
                IsDeleted = warehouseIsDeleted,
            }
        ).ExecuteCommandAsync();
    }

    private async Task SeedPosmMappingAsync(
        string productCode,
        string localSupplierCode,
        string? chinaSupplierCode,
        DateTime updatedAt
    )
    {
        await _posmDb.Insertable(new PosmProductSupplierMapping
        {
            ProductCode = productCode,
            LocalSupplierCode = localSupplierCode,
            ChinaSupplierCode = chinaSupplierCode,
            LastUpdateTime = updatedAt,
            CreatedAt = updatedAt.AddDays(-1),
            UpdatedAt = updatedAt,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private static ConnectionConfig CreateConnectionConfig(string connectionString) =>
        new()
        {
            ConnectionString = connectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        };

    private static void CreateScheduledTaskLogTable(ISqlSugarClient db)
    {
        db.Ado.ExecuteCommand(
            """
            CREATE TABLE IF NOT EXISTS ScheduledTaskLog (
                Id TEXT PRIMARY KEY,
                TaskType TEXT NOT NULL,
                TaskParameters TEXT NULL,
                Status TEXT NOT NULL,
                StartedAt TEXT NOT NULL,
                CompletedAt TEXT NULL,
                DurationMs INTEGER NULL,
                ErrorMessage TEXT NULL,
                RetryCount INTEGER NOT NULL,
                CanRetry INTEGER NOT NULL,
                ScheduledTime TEXT NOT NULL,
                TriggeredBy TEXT NULL,
                CreatedAt TEXT NOT NULL,
                CreatedBy TEXT NULL,
                UpdatedAt TEXT NULL,
                UpdatedBy TEXT NULL,
                IsDeleted INTEGER NULL
            );
            """
        );
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            SqliteTempFileCleanup.DeleteIfExists(path);
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "services/backend/BlazorApp.Api/Services/React/DataSyncFullService.cs")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("未找到 hb-platform 仓库根目录。");
    }

    private static void AssertLeaseCoversSnapshotAndCommit(string method, string snapshotMarker)
    {
        var acquireIndex = method.IndexOf(
            "PosmProductSupplierMappingSyncLock.AcquireAsync",
            StringComparison.Ordinal
        );
        var snapshotIndex = method.IndexOf(snapshotMarker, StringComparison.Ordinal);
        var existingMappingsIndex = method.IndexOf("existingMappings", StringComparison.Ordinal);
        var commitIndex = method.IndexOf("CommitAsync", StringComparison.Ordinal);

        Assert.True(acquireIndex >= 0, "同步方法必须获取 POSM 映射执行租约。");
        Assert.True(snapshotIndex > acquireIndex, "源快照必须在获取执行租约之后读取。");
        Assert.True(existingMappingsIndex > acquireIndex, "POSM 既有映射必须在执行租约内重新读取。");
        Assert.True(commitIndex > existingMappingsIndex, "执行租约必须覆盖 POSM 写入直到提交。");
    }

    private static string ReadMethod(string path, string startMarker, string endMarker)
    {
        var source = File.ReadAllText(path);
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"无法提取方法: {startMarker}");
        return source[start..end];
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    private static POSMSqlSugarContext CreatePosmSqlSugarContext(ISqlSugarClient db)
    {
        var context = (POSMSqlSugarContext)RuntimeHelpers.GetUninitializedObject(
            typeof(POSMSqlSugarContext)
        );
        typeof(POSMSqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    private static TContext CreateContext<TContext>()
        where TContext : class
    {
        return (TContext)RuntimeHelpers.GetUninitializedObject(typeof(TContext));
    }
}
