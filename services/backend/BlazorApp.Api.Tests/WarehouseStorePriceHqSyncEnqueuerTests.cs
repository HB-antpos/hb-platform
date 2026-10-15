using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 「分店价 → HQ」入队器：重点验证它只带 HQ 里存在的启用分店、
/// 兜底请求人、遵守开关、把 outbox 操作号返回给调用方，并且入队失败不被吞掉（调用方依赖异常来回滚）。
/// </summary>
public sealed class WarehouseStorePriceHqSyncEnqueuerTests : IDisposable
{
    private const string ProductCode = "P-HQ-ENQ";
    private static readonly string[] RetailMask = { ProductMaintenanceHqFieldMasks.StoreRetailPrice };

    private readonly string _dbPath;
    private readonly SqliteConnection _sqliteConnection;
    private readonly SqlSugarClient _db;

    public WarehouseStorePriceHqSyncEnqueuerTests()
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
        _db.CodeFirst.InitTables(typeof(Store));
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

    [Fact]
    public async Task 只带启用且未删除的候选分店_停用_已删除_不在候选内的都剔除()
    {
        await SeedStoreAsync("1005", isActive: true, isDeleted: false);
        await SeedStoreAsync("1006", isActive: true, isDeleted: false);
        await SeedStoreAsync("2001", isActive: false, isDeleted: false); // 停用分店：HQ 没有
        await SeedStoreAsync("2002", isActive: true, isDeleted: true);
        await SeedStoreAsync("1007", isActive: true, isDeleted: false); // 启用但不在候选里
        var (enqueuer, captured) = CreateEnqueuer(userGuid: "user-1");

        await enqueuer.EnqueueAsync(
            _db,
            ProductCode,
            new string?[] { "1005", " 1006 ", "1005", null, "", "2001", "2002" },
            RetailMask,
            "admin",
            "unit-test"
        );

        var request = Assert.Single(captured);
        Assert.Equal(new[] { "1005", "1006" }, request.TargetStoreCodes!.OrderBy(code => code));
        // 授权范围冻结为目标分店：只有能管理这些分店的人才能查询/重试该任务。
        Assert.Equal(
            request.TargetStoreCodes!.OrderBy(code => code),
            request.AuthorizedStoreCodes!.OrderBy(code => code)
        );
    }

    [Fact]
    public async Task 候选分店全是停用分店_不入队()
    {
        await SeedStoreAsync("2001", isActive: false, isDeleted: false);
        var (enqueuer, captured) = CreateEnqueuer(userGuid: "user-1");

        await enqueuer.EnqueueAsync(_db, ProductCode, new[] { "2001" }, RetailMask, "admin", "unit-test");

        Assert.Empty(captured);
    }

    [Fact]
    public async Task 没有任何候选分店_不入队()
    {
        var (enqueuer, captured) = CreateEnqueuer(userGuid: "user-1");

        await enqueuer.EnqueueAsync(_db, ProductCode, Array.Empty<string?>(), RetailMask, "admin", "unit-test");

        Assert.Empty(captured);
    }

    [Fact]
    public async Task 请求内容_操作类型字段掩码来源与商品编码原样带入()
    {
        await SeedStoreAsync("1005", isActive: true, isDeleted: false);
        var (enqueuer, captured) = CreateEnqueuer(userGuid: "user-1");
        var mask = new[]
        {
            ProductMaintenanceHqFieldMasks.StorePurchasePrice,
            ProductMaintenanceHqFieldMasks.StoreRetailPrice,
        };

        await enqueuer.EnqueueAsync(_db, $" {ProductCode} ", new[] { "1005" }, mask, "admin", "unit-test-source");

        var request = Assert.Single(captured);
        Assert.Equal(ProductMaintenanceHqOperationKinds.StorePriceUpdated, request.OperationKind);
        Assert.Equal(ProductCode, request.ProductCode);
        Assert.Equal(mask, request.FieldMask);
        Assert.Equal("unit-test-source", request.Source);
    }

    [Fact]
    public async Task 有登录用户_用用户标识且不带设备标识()
    {
        await SeedStoreAsync("1005", isActive: true, isDeleted: false);
        var (enqueuer, captured) = CreateEnqueuer(userGuid: "user-guid-9");

        await enqueuer.EnqueueAsync(_db, ProductCode, new[] { "1005" }, RetailMask, "admin", "unit-test");

        var request = Assert.Single(captured);
        Assert.Equal("user-guid-9", request.RequestedByUserGuid);
        Assert.Null(request.RequestedByDeviceId);
    }

    [Fact]
    public async Task 没有登录用户_用系统标识兜底_满足用户与设备二选一()
    {
        await SeedStoreAsync("1005", isActive: true, isDeleted: false);
        var (enqueuer, captured) = CreateEnqueuer(userGuid: "");

        await enqueuer.EnqueueAsync(
            _db,
            ProductCode,
            new[] { "1005" },
            RetailMask,
            "MobileWarehousePricePatch",
            "unit-test"
        );

        var request = Assert.Single(captured);
        Assert.Null(request.RequestedByUserGuid);
        Assert.Equal("system:MobileWarehousePricePatch", request.RequestedByDeviceId);
    }

    [Fact]
    public async Task 开关关闭_不入队()
    {
        await SeedStoreAsync("1005", isActive: true, isDeleted: false);
        var (enqueuer, captured) = CreateEnqueuer(userGuid: "user-1", syncToHq: false);

        await enqueuer.EnqueueAsync(_db, ProductCode, new[] { "1005" }, RetailMask, "admin", "unit-test");

        Assert.Empty(captured);
    }

    [Fact]
    public async Task 入队成功_返回outbox操作号_供调用方记到价格任务上()
    {
        await SeedStoreAsync("1005", isActive: true, isDeleted: false);
        var (enqueuer, _) = CreateEnqueuer(userGuid: "user-1");

        var operation = await enqueuer.EnqueueAsync(_db, ProductCode, new[] { "1005" }, RetailMask, "admin", "unit-test");

        Assert.Equal("op-from-writer", operation?.OperationId);
    }

    [Fact]
    public async Task 没有可同步的分店或开关关闭_返回null()
    {
        await SeedStoreAsync("2001", isActive: false, isDeleted: false);
        var (enqueuer, _) = CreateEnqueuer(userGuid: "user-1");
        var (disabled, _) = CreateEnqueuer(userGuid: "user-1", syncToHq: false);
        await SeedStoreAsync("1005", isActive: true, isDeleted: false);

        Assert.Null(await enqueuer.EnqueueAsync(_db, ProductCode, new[] { "2001" }, RetailMask, "admin", "unit-test"));
        Assert.Null(await disabled.EnqueueAsync(_db, ProductCode, new[] { "1005" }, RetailMask, "admin", "unit-test"));
    }

    [Fact]
    public async Task 入队失败_异常原样向上抛_由调用方回滚改价()
    {
        await SeedStoreAsync("1005", isActive: true, isDeleted: false);
        var writer = new Mock<IProductMaintenanceHqProjectionWriter>();
        writer
            .Setup(item =>
                item.EnqueueAsync(
                    It.IsAny<ISqlSugarClient>(),
                    It.IsAny<ProductMaintenanceHqMutationRequest>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(new ProductMaintenanceHqEnqueueException("HQ 同步任务创建失败，请稍后重试"));
        var enqueuer = CreateEnqueuer(writer.Object, userGuid: "user-1", syncToHq: true);

        await Assert.ThrowsAsync<ProductMaintenanceHqEnqueueException>(() =>
            enqueuer.EnqueueAsync(_db, ProductCode, new[] { "1005" }, RetailMask, "admin", "unit-test")
        );
    }

    private (WarehouseStorePriceHqSyncEnqueuer Enqueuer, List<ProductMaintenanceHqMutationRequest> Captured) CreateEnqueuer(
        string userGuid,
        bool syncToHq = true
    )
    {
        var captured = new List<ProductMaintenanceHqMutationRequest>();
        var writer = new Mock<IProductMaintenanceHqProjectionWriter>();
        writer
            .Setup(item =>
                item.EnqueueAsync(
                    It.IsAny<ISqlSugarClient>(),
                    It.IsAny<ProductMaintenanceHqMutationRequest>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<ISqlSugarClient, ProductMaintenanceHqMutationRequest, CancellationToken>(
                (_, request, _) => captured.Add(request)
            )
            .ReturnsAsync(new ProductHqSyncOperationStatusDto { OperationId = "op-from-writer" });
        return (CreateEnqueuer(writer.Object, userGuid, syncToHq), captured);
    }

    private static WarehouseStorePriceHqSyncEnqueuer CreateEnqueuer(
        IProductMaintenanceHqProjectionWriter writer,
        string userGuid,
        bool syncToHq
    )
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(item => item.GetCurrentUserGuid()).Returns(userGuid);
        currentUser.Setup(item => item.GetCurrentUsername()).Returns("admin");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["StorePriceUpdateTasks:SyncToHq"] = syncToHq ? "true" : "false",
                }
            )
            .Build();
        return new WarehouseStorePriceHqSyncEnqueuer(writer, currentUser.Object, configuration);
    }

    private async Task SeedStoreAsync(string storeCode, bool isActive, bool isDeleted)
    {
        await _db.Insertable(
                new Store
                {
                    StoreGUID = $"store-guid-{storeCode}",
                    StoreCode = storeCode,
                    StoreName = $"Store {storeCode}",
                    IsActive = isActive,
                    IsDeleted = isDeleted,
                }
            )
            .ExecuteCommandAsync();
    }
}
