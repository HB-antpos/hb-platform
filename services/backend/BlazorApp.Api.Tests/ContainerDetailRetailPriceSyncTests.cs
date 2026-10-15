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
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 货柜明细改仓库零售价（贴牌价格）接入「同步分店零售价 / 生成价格任务」两个开关。
/// 语义与仓库商品页完全一致（请求不传 = 都勾选）：
/// 同步 + 生成 = 直接改分店价并发「待换标签」任务（HQ 随任务执行同步）；不同步 = 不动分店价；
/// 同步 + 不生成 = 直接改分店价、无任务、改价当下同事务入队 HQ；不同步 + 不生成 = 只改仓库价。
/// </summary>
public sealed class ContainerDetailRetailPriceSyncTests : IDisposable
{
    private const string ContainerCode = "C-RETAIL-SYNC";

    private readonly string _localDbPath;
    private readonly string _hbSalesDbPath;
    private readonly SqliteConnection _localConnection;
    private readonly SqliteConnection _hbSalesConnection;
    private readonly SqlSugarClient _localDb;
    private readonly SqlSugarScope _hbSalesDb;

    public ContainerDetailRetailPriceSyncTests()
    {
        _localDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _hbSalesDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _localConnection = new SqliteConnection($"Data Source={_localDbPath}");
        _hbSalesConnection = new SqliteConnection($"Data Source={_hbSalesDbPath}");
        _localConnection.Open();
        _hbSalesConnection.Open();
        _localDb = new SqlSugarClient(CreateConnectionConfig(_localConnection.ConnectionString));
        _hbSalesDb = new SqlSugarScope(CreateConnectionConfig(_hbSalesConnection.ConnectionString));

        _localDb.CodeFirst.InitTables(
            typeof(Container),
            typeof(ContainerDetail),
            typeof(DomesticProduct),
            typeof(DomesticSetProduct),
            typeof(ProductGrade),
            typeof(DomesticProductCreationLog),
            typeof(WarehouseProduct),
            typeof(Product),
            typeof(Store),
            typeof(StoreRetailPrice),
            typeof(ProductSetCode),
            typeof(StoreMultiCodeProduct),
            typeof(WarehouseCategory),
            typeof(ContainerDetailFieldOverrideAudit)
        );
    }

    [Fact]
    public async Task 默认两个开关都勾选_直接覆盖启用分店价并发待换标签任务_HQ不在改价时推()
    {
        await SeedScenarioAsync("P-DEFAULT");
        var fixtures = new ServiceFixtures();

        var result = await fixtures.CreateService(this).BatchUpdateDetailsDetailedAsync(
            ContainerCode,
            new List<UpdateContainerDetailDto> { new() { HGUID = "D-P-DEFAULT", 贴牌价格 = 6.66m } }
        );

        Assert.Empty(result.ValidationErrors);
        Assert.Equal(1, result.TotalUpdated);
        // 仓库价与货柜明细照常回写。
        Assert.Equal(6.66m, (await WarehouseProductAsync("P-DEFAULT")).OEMPrice);
        Assert.Equal(6.66m, (await _localDb.Queryable<ContainerDetail>().SingleAsync(d => d.DetailCode == "D-P-DEFAULT")).OEMPrice);
        // 启用分店被覆盖；特殊商品 / 自动定价 / 停用分店 / 已是新价的行原样不动。
        Assert.Equal(6.66m, await StoreRetailAsync("P-DEFAULT", "S01"));
        Assert.Equal(6.66m, await StoreRetailAsync("P-DEFAULT", "S02"));
        Assert.Equal(2.2m, await StoreRetailAsync("P-DEFAULT", "S-SPECIAL"));
        Assert.Equal(2.2m, await StoreRetailAsync("P-DEFAULT", "S-AUTO"));
        Assert.Equal(2.2m, await StoreRetailAsync("P-DEFAULT", "S-OFF"));
        Assert.Equal(6.66m, await StoreRetailAsync("P-DEFAULT", "S-SAME"));
        // 待换标签任务只登记真正被改价的两家店，并带上改价前的旧价（货架标签上的价格）。
        var recorded = Assert.Single(fixtures.OverwriteBatches);
        Assert.Equal(new[] { "S01", "S02" }, recorded.OrderBy(item => item.StoreCode).Select(item => item.StoreCode));
        Assert.All(recorded, item => Assert.Equal(2.2m, item.OldRetailPrice));
        Assert.Empty(fixtures.Enqueuer.Calls);
        Assert.All(fixtures.HistoryContexts, context => Assert.False(context.SkipPriceTasks));
        Assert.All(fixtures.HistoryContexts, context => Assert.Equal("ContainerDetail", context.Source));
    }

    [Fact]
    public async Task 不同步分店零售价_分店价原样保留_仍由价格漏斗给分店发改价格任务()
    {
        await SeedScenarioAsync("P-NOSYNC");
        var fixtures = new ServiceFixtures();

        await fixtures.CreateService(this).BatchUpdateDetailsDetailedAsync(
            ContainerCode,
            new List<UpdateContainerDetailDto> { new() { HGUID = "D-P-NOSYNC", 贴牌价格 = 6.66m } },
            new WarehousePriceSyncPolicy(SyncStoreRetailPrices: false, GenerateTasks: true)
        );

        Assert.Equal(6.66m, (await WarehouseProductAsync("P-NOSYNC")).OEMPrice);
        Assert.Equal(2.2m, await StoreRetailAsync("P-NOSYNC", "S01"));
        Assert.Equal(2.2m, await StoreRetailAsync("P-NOSYNC", "S02"));
        Assert.Empty(fixtures.OverwriteBatches);
        Assert.Empty(fixtures.Enqueuer.Calls);
        // 生成任务 = 不跳过漏斗，分店据此收到「改价格」。
        Assert.All(fixtures.HistoryContexts, context => Assert.False(context.SkipPriceTasks));
    }

    [Fact]
    public async Task 同步且不生成任务_直接改分店价_无任务_改价当下同事务入队HQ()
    {
        await SeedScenarioAsync("P-NOTASK");
        var fixtures = new ServiceFixtures();

        await fixtures.CreateService(this).BatchUpdateDetailsDetailedAsync(
            ContainerCode,
            new List<UpdateContainerDetailDto> { new() { HGUID = "D-P-NOTASK", 贴牌价格 = 6.66m } },
            new WarehousePriceSyncPolicy(SyncStoreRetailPrices: true, GenerateTasks: false)
        );

        Assert.Equal(6.66m, await StoreRetailAsync("P-NOTASK", "S01"));
        Assert.Equal(6.66m, await StoreRetailAsync("P-NOTASK", "S02"));
        // 不生成任务：不登记待换标签；变更历史带 SkipPriceTasks，价格漏斗也不建任务。
        Assert.Empty(fixtures.OverwriteBatches);
        Assert.All(fixtures.HistoryContexts, context => Assert.True(context.SkipPriceTasks));
        // 没有任务执行可等，改价当下入队 HQ：只带真正被改价的启用分店，只推零售价字段，且在事务内。
        var call = Assert.Single(fixtures.Enqueuer.Calls);
        Assert.Equal("P-NOTASK", call.ProductCode);
        Assert.Equal(new[] { "S01", "S02" }, call.StoreCodes);
        Assert.Equal(new[] { ProductMaintenanceHqFieldMasks.StoreRetailPrice }, call.FieldMask);
        Assert.Equal("container-detail-price", call.Source);
        Assert.True(call.InTransaction, "入队必须发生在货柜明细保存事务内");
    }

    [Fact]
    public async Task 不同步且不生成任务_只改仓库价_分店价无任务无HQ且漏斗被跳过()
    {
        await SeedScenarioAsync("P-ONLYWH");
        var fixtures = new ServiceFixtures();

        await fixtures.CreateService(this).BatchUpdateDetailsDetailedAsync(
            ContainerCode,
            new List<UpdateContainerDetailDto> { new() { HGUID = "D-P-ONLYWH", 贴牌价格 = 6.66m } },
            new WarehousePriceSyncPolicy(SyncStoreRetailPrices: false, GenerateTasks: false)
        );

        Assert.Equal(6.66m, (await WarehouseProductAsync("P-ONLYWH")).OEMPrice);
        Assert.Equal(2.2m, await StoreRetailAsync("P-ONLYWH", "S01"));
        Assert.Empty(fixtures.OverwriteBatches);
        Assert.Empty(fixtures.Enqueuer.Calls);
        Assert.All(fixtures.HistoryContexts, context => Assert.True(context.SkipPriceTasks));
    }

    [Fact]
    public async Task 只改进口价不改零售价_不覆盖分店零售价()
    {
        await SeedScenarioAsync("P-IMPORT");
        var fixtures = new ServiceFixtures();

        await fixtures.CreateService(this).BatchUpdateDetailsDetailedAsync(
            ContainerCode,
            new List<UpdateContainerDetailDto> { new() { HGUID = "D-P-IMPORT", 进口价格 = 4.4m } }
        );

        Assert.Equal(2.2m, await StoreRetailAsync("P-IMPORT", "S01"));
        Assert.Empty(fixtures.OverwriteBatches);
        Assert.Empty(fixtures.Enqueuer.Calls);
    }

    [Fact]
    public async Task 入队HQ失败_整笔货柜明细保存一起回滚_仓库价分店价明细都不变()
    {
        await SeedScenarioAsync("P-ROLLBACK");
        var fixtures = new ServiceFixtures();
        fixtures.Enqueuer.ThrowOnEnqueue = new InvalidOperationException("HQ outbox 不可用");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixtures.CreateService(this).BatchUpdateDetailsDetailedAsync(
                ContainerCode,
                new List<UpdateContainerDetailDto> { new() { HGUID = "D-P-ROLLBACK", 贴牌价格 = 6.66m } },
                new WarehousePriceSyncPolicy(SyncStoreRetailPrices: true, GenerateTasks: false)
            )
        );

        Assert.Equal(2.2m, (await WarehouseProductAsync("P-ROLLBACK")).OEMPrice);
        Assert.Equal(2.2m, await StoreRetailAsync("P-ROLLBACK", "S01"));
        Assert.Equal(2.2m, await StoreRetailAsync("P-ROLLBACK", "S02"));
        Assert.Equal(
            2.2m,
            (await _localDb.Queryable<ContainerDetail>().SingleAsync(d => d.DetailCode == "D-P-ROLLBACK")).OEMPrice
        );
        Assert.Empty(fixtures.HistoryContexts);
    }

    [Fact]
    public async Task 批量设价apply_prices_请求里的两个开关同样生效()
    {
        await SeedScenarioAsync("P-APPLY");
        var fixtures = new ServiceFixtures();

        var updated = await fixtures.CreateService(this).ApplyPricesByScopeAsync(
            ContainerCode,
            new ContainerDetailApplyPricesRequestDto
            {
                SelectedHguids = new List<string> { "D-P-APPLY" },
                OemPrice = 7.77m,
                SyncStoreRetailPrices = true,
                GenerateTasks = false,
            }
        );

        Assert.Equal(1, updated);
        Assert.Equal(7.77m, (await WarehouseProductAsync("P-APPLY")).OEMPrice);
        Assert.Equal(7.77m, await StoreRetailAsync("P-APPLY", "S01"));
        Assert.Equal(7.77m, await StoreRetailAsync("P-APPLY", "S02"));
        Assert.Empty(fixtures.OverwriteBatches);
        // 新价 7.77 与 S-SAME 的现价 6.66 不同，所以这次 S-SAME 也被改价并入队（其余跳过规则不变）。
        Assert.Equal(new[] { "S-SAME", "S01", "S02" }, Assert.Single(fixtures.Enqueuer.Calls).StoreCodes);
        Assert.All(fixtures.HistoryContexts, context => Assert.True(context.SkipPriceTasks));
    }

    [Fact]
    public async Task 批量设价apply_prices_不传开关_按两个都勾选处理()
    {
        await SeedScenarioAsync("P-APPLY-DEFAULT");
        var fixtures = new ServiceFixtures();

        await fixtures.CreateService(this).ApplyPricesByScopeAsync(
            ContainerCode,
            new ContainerDetailApplyPricesRequestDto
            {
                SelectedHguids = new List<string> { "D-P-APPLY-DEFAULT" },
                OemPrice = 7.77m,
            }
        );

        Assert.Equal(7.77m, await StoreRetailAsync("P-APPLY-DEFAULT", "S01"));
        Assert.Single(fixtures.OverwriteBatches);
        Assert.Empty(fixtures.Enqueuer.Calls);
        Assert.All(fixtures.HistoryContexts, context => Assert.False(context.SkipPriceTasks));
    }

    // ---------- 场景与断言辅助 ----------

    /// <summary>
    /// 一个已建档商品：仓库零售价 / 货柜明细 / 主档零售价都是 2.2；
    /// 分店价行覆盖各种需要被跳过或不动的情况。
    /// </summary>
    private async Task SeedScenarioAsync(string productCode)
    {
        if (!await _localDb.Queryable<Container>().AnyAsync(item => item.ContainerCode == ContainerCode))
        {
            await _localDb.Insertable(
                new Container
                {
                    ContainerCode = ContainerCode,
                    ContainerNumber = "RETAIL-SYNC-1",
                    IsDeleted = false,
                }
            ).ExecuteCommandAsync();
        }

        await _localDb.Insertable(
            new ContainerDetail
            {
                DetailCode = $"D-{productCode}",
                ContainerCode = ContainerCode,
                ProductCode = productCode,
                ImportPrice = 3.3m,
                OEMPrice = 2.2m,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new WarehouseProduct
            {
                ProductCode = productCode,
                ImportPrice = 3.3m,
                OEMPrice = 2.2m,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new Product
            {
                ProductCode = productCode,
                ProductName = productCode,
                PurchasePrice = 3.3m,
                RetailPrice = 2.2m,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();

        var stores = new[]
        {
            new Store { StoreCode = "S01", IsActive = true, IsDeleted = false },
            new Store { StoreCode = "S02", IsActive = true, IsDeleted = false },
            new Store { StoreCode = "S-SPECIAL", IsActive = true, IsDeleted = false },
            new Store { StoreCode = "S-AUTO", IsActive = true, IsDeleted = false },
            new Store { StoreCode = "S-SAME", IsActive = true, IsDeleted = false },
            new Store { StoreCode = "S-OFF", IsActive = false, IsDeleted = false },
        };
        foreach (var store in stores)
        {
            if (await _localDb.Queryable<Store>().AnyAsync(item => item.StoreCode == store.StoreCode))
            {
                continue;
            }
            await _localDb.Insertable(store).ExecuteCommandAsync();
        }

        await _localDb.Insertable(
            new List<StoreRetailPrice>
            {
                Price(productCode, "S01", 2.2m),
                Price(productCode, "S02", 2.2m),
                Price(productCode, "S-SPECIAL", 2.2m, special: true),
                Price(productCode, "S-AUTO", 2.2m, auto: true),
                Price(productCode, "S-OFF", 2.2m),
                // 已是新价：不动，也就不会产生无意义的任务。
                Price(productCode, "S-SAME", 6.66m),
            }
        ).ExecuteCommandAsync();
    }

    private static StoreRetailPrice Price(
        string productCode,
        string storeCode,
        decimal retail,
        bool special = false,
        bool auto = false
    ) =>
        new()
        {
            UUID = Guid.NewGuid().ToString("N"),
            StoreCode = storeCode,
            ProductCode = productCode,
            PurchasePrice = 3.3m,
            StoreRetailPriceValue = retail,
            IsActive = true,
            IsDeleted = false,
            IsSpecialProduct = special,
            IsAutoPricing = auto,
        };

    private Task<WarehouseProduct> WarehouseProductAsync(string productCode) =>
        _localDb.Queryable<WarehouseProduct>().SingleAsync(item => item.ProductCode == productCode);

    private async Task<decimal?> StoreRetailAsync(string productCode, string storeCode) =>
        (
            await _localDb.Queryable<StoreRetailPrice>()
                .SingleAsync(item => item.ProductCode == productCode && item.StoreCode == storeCode && !item.IsDeleted)
        ).StoreRetailPriceValue;

    /// <summary>每个用例一套：记录价格任务 / HQ 入队 / 变更历史上下文三类副作用。</summary>
    private sealed class ServiceFixtures
    {
        public List<IReadOnlyCollection<StorePriceOverwrite>> OverwriteBatches { get; } = new();
        public RecordingHqEnqueuer Enqueuer { get; } = new();
        public List<WarehouseProductChangeHistoryContextDto> HistoryContexts { get; } = new();

        public ContainerReactService CreateService(ContainerDetailRetailPriceSyncTests owner)
        {
            var taskService = new Mock<IStorePriceUpdateTaskService>();
            taskService
                .Setup(item => item.RecordStoreOverwritesAsync(
                    It.IsAny<IReadOnlyCollection<StorePriceOverwrite>>(),
                    It.IsAny<PriceTaskInitiator>(),
                    It.IsAny<CancellationToken>()
                ))
                .Callback<IReadOnlyCollection<StorePriceOverwrite>, PriceTaskInitiator, CancellationToken>(
                    (overwrites, _, _) => OverwriteBatches.Add(overwrites.ToList())
                )
                .Returns(Task.CompletedTask);

            var history = new Mock<IWarehouseProductChangeHistoryService>();
            history
                .Setup(item => item.RecordChangesAsync(
                    It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                    It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                    It.IsAny<WarehouseProductChangeHistoryContextDto>(),
                    It.IsAny<CancellationToken>()
                ))
                .Callback<
                    IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>,
                    IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>,
                    WarehouseProductChangeHistoryContextDto,
                    CancellationToken
                >((_, _, context, _) => HistoryContexts.Add(context))
                .ReturnsAsync(0);

            return new ContainerReactService(
                CreateSqlSugarContext(owner._localDb),
                CreateHqSqlSugarContext(),
                CreateHBSalesSqlSugarContext(owner._hbSalesDb),
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ContainerDetailConcurrency:Enabled"] = "False",
                    })
                    .Build(),
                Mock.Of<IMapper>(),
                NullLogger<ContainerReactService>.Instance,
                Mock.Of<IContainerHqSyncService>(),
                CreateTranslationServiceMock(),
                history.Object,
                Mock.Of<ICurrentUserService>(),
                new EphemeralDataProtectionProvider(),
                taskService.Object,
                Enqueuer
            );
        }
    }

    /// <summary>记录入队调用：同时记录调用时是否仍在调用方事务内，验证「改价与入队同事务」。</summary>
    private sealed class RecordingHqEnqueuer : IWarehouseStorePriceHqSyncEnqueuer
    {
        public List<HqEnqueueCall> Calls { get; } = new();
        public Exception? ThrowOnEnqueue { get; set; }

        public Task<ProductHqSyncOperationStatusDto?> EnqueueAsync(
            ISqlSugarClient transactionDb,
            string productCode,
            IEnumerable<string?> storeCodes,
            IReadOnlyCollection<string> fieldMask,
            string updatedBy,
            string source,
            CancellationToken cancellationToken = default
        )
        {
            Calls.Add(
                new HqEnqueueCall(
                    productCode,
                    storeCodes.Where(code => code != null).Select(code => code!).OrderBy(code => code).ToList(),
                    fieldMask.ToList(),
                    source,
                    transactionDb.Ado.Transaction != null
                )
            );
            if (ThrowOnEnqueue != null)
            {
                throw ThrowOnEnqueue;
            }
            return Task.FromResult<ProductHqSyncOperationStatusDto?>(
                new ProductHqSyncOperationStatusDto { OperationId = $"test-op-{Calls.Count}" }
            );
        }
    }

    private sealed record HqEnqueueCall(
        string ProductCode,
        List<string> StoreCodes,
        List<string> FieldMask,
        string Source,
        bool InTransaction
    );

    public void Dispose()
    {
        _localDb.Dispose();
        _hbSalesDb.Dispose();
        _localConnection.Dispose();
        _hbSalesConnection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_localDbPath);
        SqliteTempFileCleanup.DeleteIfExists(_hbSalesDbPath);
    }

    private static ITranslationService CreateTranslationServiceMock()
    {
        var translationService = new Mock<ITranslationService>();
        translationService
            .Setup(x => x.ContainsChinese(It.IsAny<string>()))
            .Returns<string>(value => value.Any(c => c >= '一' && c <= '鿿'));
        return translationService.Object;
    }

    private static ConnectionConfig CreateConnectionConfig(string connectionString) =>
        new()
        {
            ConnectionString = connectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
            MoreSettings = new ConnMoreSettings { IsNoReadXmlDescription = true },
        };

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        var dbField = typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic);
        dbField!.SetValue(context, db);
        return context;
    }

    private static HqSqlSugarContext CreateHqSqlSugarContext()
    {
        var context = (HqSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HqSqlSugarContext));
        var dbField = typeof(HqSqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic);
        dbField!.SetValue(context, new Mock<ISqlSugarClient>().Object);
        return context;
    }

    private static HBSalesSqlSugarContext CreateHBSalesSqlSugarContext(SqlSugarScope db)
    {
        var context = (HBSalesSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HBSalesSqlSugarContext));
        var dbField = typeof(HBSalesSqlSugarContext).GetField(
            "_db",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        dbField!.SetValue(context, db);
        return context;
    }
}
