using System.Reflection;
using System.Runtime.CompilerServices;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 批量修改（同步 batch-update 与后台任务 batch-update/jobs）把「是否上架」设为下架时的供货说明登记。
/// 两条路径最终都进入 WarehouseProductBatchUpdateCommandWriter，这里用真实服务 + SQLite 验证落库结果。
/// </summary>
public sealed class WarehouseProductBatchUpdateSupplyNoticeTests : IDisposable
{
    private const string Actor = "仓库经理B";
    private readonly string _dbPath;
    private readonly SqliteConnection _sqliteConnection;
    private readonly SqlSugarClient _db;

    public WarehouseProductBatchUpdateSupplyNoticeTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _sqliteConnection = new SqliteConnection($"Data Source={_dbPath}");
        _sqliteConnection.Open();
        _db = new SqlSugarClient(
            new ConnectionConfig
            {
                ConnectionString = _sqliteConnection.ConnectionString,
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = false,
                InitKeyType = InitKeyType.Attribute,
                MoreSettings = new ConnMoreSettings(),
            }
        );
        _db.CodeFirst.InitTables(
            typeof(Product),
            typeof(WarehouseProduct),
            typeof(DomesticProduct),
            typeof(StoreRetailPrice),
            typeof(ProductSetCode)
        );
        WarehouseProductSupplyNoticeSchemaMigrator
            .EnsureAsync(_db, NullLogger.Instance)
            .GetAwaiter()
            .GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        _sqliteConnection.Dispose();
        if (File.Exists(_dbPath))
        {
            SqliteTempFileCleanup.DeleteIfExists(_dbPath);
        }
    }

    // ---------- 同步路径：POST batch-update → service.BatchUpdateAsync(items, user, options) ----------

    [Fact]
    public async Task 同步路径_下架带说明_登记说明且未设状态的行不动()
    {
        await SeedWarehouseProductAsync("P-SYNC-DELIST", isActive: true);
        await SeedWarehouseProductAsync("P-SYNC-UNTOUCHED", isActive: false);
        await SeedOpenNoticeAsync("P-SYNC-UNTOUCHED", WarehouseProductSupplyPlans.Seasonal, "原登记人");

        var result = await CreateService().BatchUpdateAsync(
            new List<UpdateItemDto>
            {
                new() { ProductCode = "P-SYNC-DELIST", IsActive = false },
                // 留空不改状态：只改价格，已有说明必须保持原样。
                new() { ProductCode = "P-SYNC-UNTOUCHED", DomesticPrice = 8.8m },
            },
            Actor,
            new WarehouseProductBatchUpdateOptionsDto { SupplyNotice = CreateValidNotice() }
        );

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, result.SuccessCount);
        Assert.False(await IsActiveAsync("P-SYNC-DELIST"));
        var notice = await GetOpenNoticeAsync("P-SYNC-DELIST");
        Assert.NotNull(notice);
        Assert.Equal(WarehouseProductSupplyPlans.WillRestock, notice!.SupplyPlan);
        Assert.Equal(WarehouseProductSupplyExpectedPrecisions.Range, notice.ExpectedPrecision);
        Assert.Equal(new DateTime(2099, 10, 10), notice.ExpectedTo);
        Assert.Equal("运输途中，入库后开放订货", notice.StoreFacingNote);
        Assert.Equal(Actor, notice.CreatedBy);
        Assert.Equal("WarehouseProducts", notice.Source);

        var untouched = await GetOpenNoticeAsync("P-SYNC-UNTOUCHED");
        Assert.NotNull(untouched);
        Assert.Equal(WarehouseProductSupplyPlans.Seasonal, untouched!.SupplyPlan);
        Assert.Equal("原登记人", untouched.UpdatedBy);
    }

    [Fact]
    public async Task 同步路径_上架关闭说明()
    {
        await SeedWarehouseProductAsync("P-SYNC-RELIST", isActive: false);
        await SeedOpenNoticeAsync("P-SYNC-RELIST", WarehouseProductSupplyPlans.WillRestock, "原登记人");

        var result = await CreateService().BatchUpdateAsync(
            new List<UpdateItemDto> { new() { ProductCode = "P-SYNC-RELIST", IsActive = true } },
            Actor,
            new WarehouseProductBatchUpdateOptionsDto()
        );

        Assert.True(result.Success, result.Message);
        Assert.True(await IsActiveAsync("P-SYNC-RELIST"));
        Assert.Null(await GetOpenNoticeAsync("P-SYNC-RELIST"));
        var closed = await _db.Queryable<WarehouseProductSupplyNotice>()
            .SingleAsync(item => item.ProductCode == "P-SYNC-RELIST");
        Assert.NotNull(closed.ClosedAtUtc);
        Assert.Equal(Actor, closed.ClosedBy);
    }

    [Fact]
    public async Task 同步路径_下架不带说明_兼容旧客户端且不登记()
    {
        await SeedWarehouseProductAsync("P-SYNC-LEGACY", isActive: true);

        var result = await CreateService().BatchUpdateAsync(
            new List<UpdateItemDto> { new() { ProductCode = "P-SYNC-LEGACY", IsActive = false } },
            Actor,
            new WarehouseProductBatchUpdateOptionsDto()
        );

        Assert.True(result.Success, result.Message);
        Assert.False(await IsActiveAsync("P-SYNC-LEGACY"));
        Assert.Empty(await _db.Queryable<WarehouseProductSupplyNotice>().ToListAsync());
    }

    [Fact]
    public async Task 同步路径_说明非法_整批拒绝且不写库()
    {
        await SeedWarehouseProductAsync("P-SYNC-BAD", isActive: true, domesticPrice: 2.5m);

        var result = await CreateService().BatchUpdateAsync(
            new List<UpdateItemDto>
            {
                new() { ProductCode = "P-SYNC-BAD", IsActive = false, DomesticPrice = 9.9m },
            },
            Actor,
            new WarehouseProductBatchUpdateOptionsDto
            {
                SupplyNotice = new WarehouseProductSupplyNoticeInputDto { SupplyPlan = "" },
            }
        );

        Assert.False(result.Success);
        Assert.Equal(1, result.FailedCount);
        Assert.Contains("后续计划", result.Message);
        // 不能出现“已下架但说明没记上”的半截状态，其他字段也不能被部分写入。
        var warehouseProduct = await _db.Queryable<WarehouseProduct>()
            .SingleAsync(item => item.ProductCode == "P-SYNC-BAD");
        Assert.True(warehouseProduct.IsActive);
        Assert.Equal(2.5m, warehouseProduct.DomesticPrice);
        Assert.Empty(await _db.Queryable<WarehouseProductSupplyNotice>().ToListAsync());
    }

    // ---------- 后台任务路径：POST batch-update/jobs → WarehouseProductBatchUpdateJobService ----------

    [Fact]
    public async Task 后台任务_下架带说明登记_上架关闭_未设状态的行不动()
    {
        await SeedWarehouseProductAsync("P-JOB-DELIST", isActive: true);
        await SeedWarehouseProductAsync("P-JOB-RELIST", isActive: false);
        await SeedOpenNoticeAsync("P-JOB-RELIST", WarehouseProductSupplyPlans.Undecided, "原登记人");
        await SeedWarehouseProductAsync("P-JOB-UNTOUCHED", isActive: false);
        await SeedOpenNoticeAsync("P-JOB-UNTOUCHED", WarehouseProductSupplyPlans.Seasonal, "原登记人");
        var jobService = CreateJobService();

        var started = await jobService.StartJobAsync(
            new WarehouseProductBatchUpdateJobRequestDto
            {
                Items =
                [
                    new UpdateItemDto { ProductCode = "P-JOB-DELIST", IsActive = false },
                    new UpdateItemDto { ProductCode = "P-JOB-RELIST", IsActive = true },
                    new UpdateItemDto { ProductCode = "P-JOB-UNTOUCHED", MinOrderQuantity = 6 },
                ],
                SupplyNotice = CreateValidNotice(),
            },
            Actor
        );
        var completed = await WaitForJobAsync(jobService, started.JobId);

        Assert.Equal(WarehouseProductBatchUpdateJobStatusConstants.Succeeded, completed.Status);
        Assert.False(await IsActiveAsync("P-JOB-DELIST"));
        var registered = await GetOpenNoticeAsync("P-JOB-DELIST");
        Assert.NotNull(registered);
        Assert.Equal(WarehouseProductSupplyPlans.WillRestock, registered!.SupplyPlan);
        Assert.Equal(Actor, registered.CreatedBy);

        Assert.True(await IsActiveAsync("P-JOB-RELIST"));
        Assert.Null(await GetOpenNoticeAsync("P-JOB-RELIST"));

        var untouched = await GetOpenNoticeAsync("P-JOB-UNTOUCHED");
        Assert.NotNull(untouched);
        Assert.Equal(WarehouseProductSupplyPlans.Seasonal, untouched!.SupplyPlan);
        Assert.Equal("原登记人", untouched.UpdatedBy);
        Assert.False(await IsActiveAsync("P-JOB-UNTOUCHED"));
    }

    [Fact]
    public async Task 后台任务_说明非法_任务失败并返回明确错误且不写库()
    {
        await SeedWarehouseProductAsync("P-JOB-BAD", isActive: true, domesticPrice: 3.5m);
        var jobService = CreateJobService();

        var started = await jobService.StartJobAsync(
            new WarehouseProductBatchUpdateJobRequestDto
            {
                Items =
                [
                    new UpdateItemDto { ProductCode = "P-JOB-BAD", IsActive = false, DomesticPrice = 7.7m },
                ],
                SupplyNotice = new WarehouseProductSupplyNoticeInputDto
                {
                    SupplyPlan = WarehouseProductSupplyPlans.WillRestock,
                    ExpectedPrecision = WarehouseProductSupplyExpectedPrecisions.Range,
                    ExpectedFrom = new DateOnly(2099, 10, 10),
                    ExpectedTo = new DateOnly(2099, 10, 5),
                },
            },
            Actor
        );
        var completed = await WaitForJobAsync(jobService, started.JobId);

        Assert.Equal(WarehouseProductBatchUpdateJobStatusConstants.Failed, completed.Status);
        Assert.Contains("开始日期不能晚于结束日期", completed.Message);
        Assert.Equal(1, completed.Result?.FailedCount);
        var warehouseProduct = await _db.Queryable<WarehouseProduct>()
            .SingleAsync(item => item.ProductCode == "P-JOB-BAD");
        Assert.True(warehouseProduct.IsActive);
        Assert.Equal(3.5m, warehouseProduct.DomesticPrice);
        Assert.Empty(await _db.Queryable<WarehouseProductSupplyNotice>().ToListAsync());
    }

    private static WarehouseProductSupplyNoticeInputDto CreateValidNotice() =>
        new()
        {
            SupplyPlan = WarehouseProductSupplyPlans.WillRestock,
            ExpectedPrecision = WarehouseProductSupplyExpectedPrecisions.Range,
            ExpectedFrom = new DateOnly(2099, 10, 5),
            ExpectedTo = new DateOnly(2099, 10, 10),
            StoreFacingNote = "运输途中，入库后开放订货",
        };

    private async Task SeedWarehouseProductAsync(
        string productCode,
        bool isActive,
        decimal? domesticPrice = null
    )
    {
        await _db.Insertable(
                new Product
                {
                    UUID = $"product-uuid-{productCode}",
                    ProductCode = productCode,
                    ProductName = productCode,
                    ItemNumber = $"ITEM-{productCode}",
                    Barcode = $"BAR-{productCode}",
                    IsActive = true,
                    IsDeleted = false,
                }
            )
            .ExecuteCommandAsync();
        await _db.Insertable(
                new WarehouseProduct
                {
                    ProductCode = productCode,
                    DomesticPrice = domesticPrice,
                    IsActive = isActive,
                    IsDeleted = false,
                }
            )
            .ExecuteCommandAsync();
    }

    private async Task SeedOpenNoticeAsync(string productCode, string supplyPlan, string actor)
    {
        var createdAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        await _db.Insertable(
                new WarehouseProductSupplyNotice
                {
                    ProductCode = productCode,
                    SupplyPlan = supplyPlan,
                    ExpectedPrecision = WarehouseProductSupplyExpectedPrecisions.Unknown,
                    Source = "WarehouseProducts",
                    CreatedBy = actor,
                    CreatedAtUtc = createdAt,
                    UpdatedBy = actor,
                    UpdatedAtUtc = createdAt,
                }
            )
            .ExecuteCommandAsync();
    }

    private async Task<bool> IsActiveAsync(string productCode) =>
        (await _db.Queryable<WarehouseProduct>().SingleAsync(item => item.ProductCode == productCode))
            .IsActive;

    private async Task<WarehouseProductSupplyNotice?> GetOpenNoticeAsync(string productCode) =>
        await _db.Queryable<WarehouseProductSupplyNotice>()
            .Where(item => item.ProductCode == productCode && item.ClosedAtUtc == null)
            .FirstAsync();

    private WarehouseProductBatchUpdateJobService CreateJobService()
    {
        // 后台任务在自己的 DI 作用域里解析服务；这里注册基于同一 SQLite 库的真实服务，验证说明经任务快照一路传到写入器。
        var services = new ServiceCollection();
        services.AddScoped<IProductWarehouseReactService>(_ => CreateService());
        services.AddScoped(_ => Mock.Of<IProductHqSyncService>());
        var provider = services.BuildServiceProvider();
        return new WarehouseProductBatchUpdateJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<WarehouseProductBatchUpdateJobService>.Instance
        );
    }

    private static async Task<WarehouseProductBatchUpdateJobDto> WaitForJobAsync(
        IWarehouseProductBatchUpdateJobService service,
        string jobId
    )
    {
        var job = await WaitForValueAsync(
            () => service.GetJobAsync(jobId),
            current =>
                current?.Status == WarehouseProductBatchUpdateJobStatusConstants.Succeeded
                || current?.Status == WarehouseProductBatchUpdateJobStatusConstants.PartiallySucceeded
                || current?.Status == WarehouseProductBatchUpdateJobStatusConstants.Failed,
            describeLast: current => $"仓库商品批量修改 job 当前状态：{current?.Status ?? "未找到"}"
        );
        return job!;
    }

    private ProductWarehouseReactService CreateService()
    {
        var configuration = new ConfigurationBuilder().Build();
        var context = CreateSqlSugarContext(_db);
        return new ProductWarehouseReactService(
            context,
            CreateHqSqlSugarContext(),
            NullLogger<ProductWarehouseReactService>.Instance,
            configuration,
            new ItemBarcodeService(context, NullLogger<ItemBarcodeService>.Instance, configuration),
            Mock.Of<IMapper>(),
            Mock.Of<IDataSyncFullService>(),
            CreateNoopChangeHistoryService(),
            Mock.Of<ITranslationService>()
        );
    }

    private static IWarehouseProductChangeHistoryService CreateNoopChangeHistoryService()
    {
        var service = new Mock<IWarehouseProductChangeHistoryService>();
        service
            .Setup(item =>
                item.CaptureSnapshotsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(
                new Dictionary<string, WarehouseProductChangeSnapshotDto>(StringComparer.OrdinalIgnoreCase)
            );
        service
            .Setup(item =>
                item.RecordChangesAsync(
                    It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                    It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                    It.IsAny<WarehouseProductChangeHistoryContextDto>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(0);
        return service.Object;
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    private static HqSqlSugarContext CreateHqSqlSugarContext()
    {
        var context = (HqSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HqSqlSugarContext));
        typeof(HqSqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, new Mock<ISqlSugarClient>().Object);
        return context;
    }
}
