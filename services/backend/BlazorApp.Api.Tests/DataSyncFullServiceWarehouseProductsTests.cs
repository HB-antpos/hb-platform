using System.Reflection;
using System.Runtime.CompilerServices;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Mappings.Profiles.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.Background;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HqEntities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class DataSyncFullServiceWarehouseProductsTests : IDisposable
{
    private readonly string _localDbPath;
    private readonly string _hqDbPath;
    private readonly SqliteConnection _localConnection;
    private readonly SqliteConnection _hqConnection;
    private readonly SqlSugarClient _localDb;
    private readonly SqlSugarClient _hqDb;
    private readonly IMapper _mapper;

    public DataSyncFullServiceWarehouseProductsTests()
    {
        _localDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _hqDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _localConnection = new SqliteConnection($"Data Source={_localDbPath}");
        _hqConnection = new SqliteConnection($"Data Source={_hqDbPath}");
        _localConnection.Open();
        _hqConnection.Open();

        _localDb = new SqlSugarClient(CreateConnectionConfig(_localConnection.ConnectionString));
        _hqDb = new SqlSugarClient(CreateConnectionConfig(_hqConnection.ConnectionString));
        _localDb.CodeFirst.InitTables(
            typeof(WarehouseProduct),
            typeof(Product),
            typeof(ProductSetCode),
            typeof(StoreMultiCodeProduct),
            typeof(StoreRetailPrice)
        );
        _hqDb.CodeFirst.InitTables(
            typeof(CBP_DIC_商品库存表),
            typeof(DIC_商品信息字典表),
            typeof(DIC_一品多码表)
        );

        _mapper = new MapperConfiguration(
            cfg =>
            {
                cfg.AddProfile<ReactWarehouseProductStockProfile>();
                cfg.AddProfile<ReactProductSetCodeMappingProfile>();
            },
            NullLoggerFactory.Instance
        ).CreateMapper();
    }

    [Fact]
    public async Task SyncProductSetCodesFromHqAsync_空多码业务键在删除前失败并保留本地关系()
    {
        const string productCode = "P-INVALID-EMPTY-CHILD";
        const string localSetCodeId = "LOCAL-MUST-STAY";
        await _localDb.Insertable(new ProductSetCode
        {
            SetCodeId = localSetCodeId,
            ProductCode = productCode,
            SetProductCode = "LOCAL-CHILD",
            SetItemNumber = "LOCAL-CHILD",
            SetBarcode = "LOCAL-BARCODE",
            SetPurchasePrice = 1m,
            SetRetailPrice = 2m,
            SetType = 2,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _hqDb.Insertable(new DIC_商品信息字典表
        {
            HGUID = "HQ-PRODUCT-GUID",
            H商品标签GUID = "HQ-TAG-GUID",
            H商品分类码GUID = "HQ-CATEGORY-GUID",
            H供货商编码 = "SUP",
            H商品编码 = productCode,
            H货号 = "ITEM-INVALID",
            H主条形码 = "PRODUCT-BARCODE",
            H商品名称 = "空多码业务键测试商品",
            H商品类型 = 2,
            H大写名称 = "INVALID CHILD KEY PRODUCT",
            H规格 = "EA",
            H单位 = "EA",
            H进货价 = 1m,
            H零售价 = 2m,
            H是否自动定价 = false,
            H商品图片 = "image.png",
            中包数量 = 1,
            H腾讯云图地址 = "https://example.invalid/image.png",
            H使用状态 = true,
            H是否特殊商品 = false,
            H进货单主表GUID = "ORDER-GUID",
            H进货单详情GUID = "ORDER-DETAIL-GUID",
            CBP商品中文名称 = "空多码业务键测试商品",
            CBP供应商编码 = "SUP",
            CBP商品分类码GUID = "WAREHOUSE-CATEGORY",
            FGC_Creator = "HQ",
            FGC_CreateDate = DateTime.UtcNow.AddDays(-1),
            FGC_LastModifier = "HQ",
            FGC_LastModifyDate = DateTime.UtcNow,
            FGC_UpdateHelp = "test",
        }).ExecuteCommandAsync();
        await _hqDb.Insertable(new DIC_一品多码表
        {
            HGUID = "HQ-INVALID-SET-GUID",
            H商品编码 = productCode,
            H多码商品编号 = null,
            H主条形码 = "BARCODE-WITHOUT-BUSINESS-KEY",
            H使用状态 = true,
            FGC_LastModifyDate = DateTime.UtcNow,
        }).ExecuteCommandAsync();

        var result = await CreateService().SyncProductSetCodesFromHqAsync(10, 10, 1);

        Assert.False(result.IsSuccess);
        Assert.Equal("PRODUCT_SET_CODE_SOURCE_INVALID", result.ErrorCode);
        Assert.Equal(1, result.ErrorCount);
        var local = await _localDb.Queryable<ProductSetCode>()
            .SingleAsync(row => row.SetCodeId == localSetCodeId);
        Assert.Equal("LOCAL-CHILD", local.SetProductCode);
        Assert.False(local.IsDeleted);
    }

    [Fact]
    public async Task SyncProductSetCodesFromHqAsync_空父商品业务键在删除前失败并保留本地关系()
    {
        await AssertInvalidProductSetSourcePreservesLocalAsync(
            "EMPTY-PARENT",
            hqProductCode: null,
            hqChildCode: "HQ-CHILD",
            hqBarcode: "HQ-BARCODE",
            seedActiveHqProduct: false
        );
    }

    [Fact]
    public async Task SyncProductSetCodesFromHqAsync_空子商品且无条码时仍在删除前失败并保留本地关系()
    {
        await AssertInvalidProductSetSourcePreservesLocalAsync(
            "EMPTY-CHILD-NO-BARCODE",
            hqProductCode: "P-INVALID-EMPTY-CHILD-NO-BARCODE",
            hqChildCode: null,
            hqBarcode: null,
            seedActiveHqProduct: true
        );
    }

    [Fact]
    public async Task SyncWarehouseProductsFromHqAsync_按商品编码新增更新且保留本地字段()
    {
        var originalCreatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await SeedLocalWarehouseProductAsync(
            "P-UPDATE",
            domesticPrice: 1m,
            oemPrice: 2m,
            importPrice: 3m,
            stockQuantity: 4,
            minOrderQuantity: 5,
            stockValue: 6m,
            stockAlertQuantity: 7,
            isActive: true,
            volume: 8.8m,
            packingQuantity: 9,
            createdAt: originalCreatedAt,
            createdBy: "LocalUser",
            isDeleted: true
        );
        await SeedLocalWarehouseProductAsync(
            "P-KEEP",
            domesticPrice: 11m,
            oemPrice: 12m,
            importPrice: 13m,
            stockQuantity: 14,
            minOrderQuantity: 15,
            stockValue: 16m,
            stockAlertQuantity: 17,
            isActive: false,
            volume: 18.8m,
            packingQuantity: 19,
            createdAt: originalCreatedAt.AddDays(-1),
            createdBy: "KeepUser",
            isDeleted: false
        );
        await SeedHqStockAsync(
            "P-UPDATE",
            domesticPrice: 101m,
            oemPrice: 102m,
            importPrice: 103m,
            stockQuantity: 104m,
            minOrderQuantity: 105m,
            stockValue: 106m,
            stockAlertQuantity: 107,
            isActive: 0
        );
        await SeedHqStockAsync(
            "P-INSERT",
            domesticPrice: 201m,
            oemPrice: 202m,
            importPrice: 203m,
            stockQuantity: 204m,
            minOrderQuantity: 205m,
            stockValue: 206m,
            stockAlertQuantity: 207,
            isActive: 1
        );

        var result = await CreateService().SyncWarehouseProductsFromHqAsync(1, 1);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, result.AddedCount);
        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(0, result.ErrorCount);

        var updated = await _localDb.Queryable<WarehouseProduct>()
            .SingleAsync(x => x.ProductCode == "P-UPDATE");
        Assert.Equal(101m, updated.DomesticPrice);
        Assert.Equal(102m, updated.OEMPrice);
        Assert.Equal(103m, updated.ImportPrice);
        Assert.Equal(104, updated.StockQuantity);
        // 中包数由仓库本地维护：本地已有正数时 HQ 的最小订货量不再覆盖。
        Assert.Equal(5, updated.MinOrderQuantity);
        Assert.Equal(106m, updated.StockValue);
        Assert.Equal(107, updated.StockAlertQuantity);
        Assert.False(updated.IsActive);
        Assert.Equal(8.8m, updated.Volume);
        Assert.Equal(9, updated.PackingQuantity);
        Assert.Equal(originalCreatedAt, updated.CreatedAt);
        Assert.Equal("LocalUser", updated.CreatedBy);
        Assert.True(updated.IsDeleted);
        Assert.Equal("System", updated.UpdatedBy);

        var inserted = await _localDb.Queryable<WarehouseProduct>()
            .SingleAsync(x => x.ProductCode == "P-INSERT");
        Assert.Equal(201m, inserted.DomesticPrice);
        Assert.Equal(202m, inserted.OEMPrice);
        Assert.Equal(203m, inserted.ImportPrice);
        Assert.Equal(204, inserted.StockQuantity);
        Assert.Equal(205, inserted.MinOrderQuantity);
        Assert.Equal(206m, inserted.StockValue);
        Assert.Equal(207, inserted.StockAlertQuantity);
        Assert.True(inserted.IsActive);
        Assert.Equal("System", inserted.CreatedBy);
        Assert.Equal("System", inserted.UpdatedBy);

        var kept = await _localDb.Queryable<WarehouseProduct>()
            .SingleAsync(x => x.ProductCode == "P-KEEP");
        Assert.Equal(11m, kept.DomesticPrice);
        Assert.Equal(18.8m, kept.Volume);
        Assert.Equal(19, kept.PackingQuantity);
        Assert.Equal("KeepUser", kept.CreatedBy);
        Assert.False(kept.IsDeleted);
    }

    [Fact]
    public async Task SyncWarehouseProductsFromHqAsync_本地中包数为空或零时用HQ最小订货量补位()
    {
        await SeedLocalWarehouseProductAsync(
            "P-ZERO",
            domesticPrice: 1m,
            oemPrice: 2m,
            importPrice: 3m,
            stockQuantity: 4,
            minOrderQuantity: 0,
            stockValue: 6m,
            stockAlertQuantity: 7,
            isActive: true,
            volume: 8.8m,
            packingQuantity: 9,
            createdAt: new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            createdBy: "LocalUser",
            isDeleted: false
        );
        await _localDb.Insertable(new WarehouseProduct
        {
            ProductCode = "P-NULL",
            MinOrderQuantity = null,
            IsActive = true,
            CreatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            CreatedBy = "LocalUser",
        }).ExecuteCommandAsync();
        await SeedHqStockAsync(
            "P-ZERO",
            domesticPrice: 101m,
            oemPrice: 102m,
            importPrice: 103m,
            stockQuantity: 104m,
            minOrderQuantity: 12m,
            stockValue: 106m,
            stockAlertQuantity: 107,
            isActive: 1
        );
        await SeedHqStockAsync(
            "P-NULL",
            domesticPrice: 201m,
            oemPrice: 202m,
            importPrice: 203m,
            stockQuantity: 204m,
            minOrderQuantity: 6m,
            stockValue: 206m,
            stockAlertQuantity: 207,
            isActive: 1
        );

        var result = await CreateService().SyncWarehouseProductsFromHqAsync(1, 1);

        Assert.True(result.IsSuccess, result.Message);
        var zero = await _localDb.Queryable<WarehouseProduct>()
            .SingleAsync(x => x.ProductCode == "P-ZERO");
        Assert.Equal(12, zero.MinOrderQuantity);
        var empty = await _localDb.Queryable<WarehouseProduct>()
            .SingleAsync(x => x.ProductCode == "P-NULL");
        Assert.Equal(6, empty.MinOrderQuantity);
    }

    [Fact]
    public async Task SyncWarehouseProductsFromHqAsync_商品编码大小写不同仍更新本地记录()
    {
        await SeedLocalWarehouseProductAsync(
            "P-CASE",
            domesticPrice: 1m,
            oemPrice: 2m,
            importPrice: 3m,
            stockQuantity: 4,
            minOrderQuantity: 5,
            stockValue: 6m,
            stockAlertQuantity: 7,
            isActive: true,
            volume: 8m,
            packingQuantity: 9,
            createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            createdBy: "LocalUser",
            isDeleted: false
        );
        await SeedHqStockAsync(
            "p-case",
            domesticPrice: 301m,
            oemPrice: 302m,
            importPrice: 303m,
            stockQuantity: 304m,
            minOrderQuantity: 305m,
            stockValue: 306m,
            stockAlertQuantity: 307,
            isActive: 1
        );

        var result = await CreateService().SyncWarehouseProductsFromHqAsync(10, 10);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(0, result.AddedCount);
        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(1, await _localDb.Queryable<WarehouseProduct>().CountAsync());
        var updated = await _localDb.Queryable<WarehouseProduct>()
            .SingleAsync(x => x.ProductCode == "P-CASE");
        Assert.Equal(301m, updated.DomesticPrice);
        Assert.Equal("P-CASE", updated.ProductCode);
    }

    [Fact]
    public async Task SyncWarehouseProductsFromHqAsync_跨页重复商品编码只统计一次新增且最后一条生效()
    {
        await SeedHqStockAsync(
            "P-DUP",
            domesticPrice: 401m,
            oemPrice: 402m,
            importPrice: 403m,
            stockQuantity: 404m,
            minOrderQuantity: 405m,
            stockValue: 406m,
            stockAlertQuantity: 407,
            isActive: 1
        );
        await SeedHqStockAsync(
            "p-dup",
            domesticPrice: 501m,
            oemPrice: 502m,
            importPrice: 503m,
            stockQuantity: 504m,
            minOrderQuantity: 505m,
            stockValue: 506m,
            stockAlertQuantity: 507,
            isActive: 0
        );

        var result = await CreateService().SyncWarehouseProductsFromHqAsync(1, 1);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, result.AddedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(1, await _localDb.Queryable<WarehouseProduct>().CountAsync());
        var inserted = await _localDb.Queryable<WarehouseProduct>()
            .SingleAsync(x => x.ProductCode == "P-DUP");
        Assert.Equal(501m, inserted.DomesticPrice);
        Assert.Equal(504, inserted.StockQuantity);
        Assert.False(inserted.IsActive);
    }

    [Fact]
    public async Task SyncWarehouseProductsFromHqAsync_跨页重复商品同批次只写一条最终历史()
    {
        await SeedHqStockAsync(
            "P-AUDIT-DUP",
            domesticPrice: 601m,
            oemPrice: 602m,
            importPrice: 603m,
            stockQuantity: 604m,
            minOrderQuantity: 605m,
            stockValue: 606m,
            stockAlertQuantity: 607,
            isActive: 1
        );
        await SeedHqStockAsync(
            "p-audit-dup",
            domesticPrice: 701m,
            oemPrice: 702m,
            importPrice: 703m,
            stockQuantity: 704m,
            minOrderQuantity: 705m,
            stockValue: 706m,
            stockAlertQuantity: 707,
            isActive: 0
        );

        var historyService = new Mock<IWarehouseProductChangeHistoryService>(MockBehavior.Strict);
        historyService
            .Setup(service => service.CaptureSnapshotsAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()
            ))
            .Returns((IEnumerable<string> codes, CancellationToken _) =>
                Task.FromResult<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(
                    codes.Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            code => code,
                            code => new WarehouseProductChangeSnapshotDto { ProductCode = code },
                            StringComparer.OrdinalIgnoreCase
                        )
                )
            );
        historyService
            .Setup(service => service.RecordChangesAsync(
                It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                It.Is<WarehouseProductChangeHistoryContextDto>(context =>
                    context.BatchGuid.HasValue && context.Source == "DataSyncFull"
                ),
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(1);

        var result = await CreateService(historyService: historyService.Object)
            .SyncWarehouseProductsFromHqAsync(1, 1);

        Assert.True(result.IsSuccess, result.Message);
        historyService.Verify(service => service.RecordChangesAsync(
            It.Is<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(snapshots =>
                snapshots.Count == 1 && snapshots.ContainsKey("P-AUDIT-DUP")
            ),
            It.Is<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(snapshots =>
                snapshots.Count == 1 && snapshots.ContainsKey("P-AUDIT-DUP")
            ),
            It.IsAny<WarehouseProductChangeHistoryContextDto>(),
            It.IsAny<CancellationToken>()
        ), Times.Once);
    }

    [Fact]
    public async Task SyncWarehouseProductsFromHqAsync_异常失败时返回错误计数()
    {
        var result = await CreateService(CreateContext<HqSqlSugarContext>())
            .SyncWarehouseProductsFromHqAsync(10, 10);

        Assert.False(result.IsSuccess);
        Assert.Equal(1, result.ErrorCount);
    }

    [Fact]
    public async Task SyncWarehouseProductsFromHqAsync_使用入队身份写入历史上下文()
    {
        await SeedLocalWarehouseProductAsync(
            "P-ACTOR",
            domesticPrice: 1m,
            oemPrice: 2m,
            importPrice: 3m,
            stockQuantity: 4,
            minOrderQuantity: 5,
            stockValue: 6m,
            stockAlertQuantity: 7,
            isActive: true,
            volume: 8m,
            packingQuantity: 9,
            createdAt: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            createdBy: "原创建人",
            isDeleted: false
        );
        await SeedHqStockAsync(
            "P-ACTOR",
            domesticPrice: 101m,
            oemPrice: 102m,
            importPrice: 103m,
            stockQuantity: 104m,
            minOrderQuantity: 105m,
            stockValue: 106m,
            stockAlertQuantity: 107,
            isActive: 1
        );

        // 同构模拟生产 SqlSugarContext 的审计拦截器；未开启 Preserve scope 时会覆盖入队操作人。
        _localDb.Aop.DataExecuting = (_, entityInfo) =>
        {
            if (
                entityInfo.EntityValue is not BaseEntity
                || entityInfo.OperationType != DataFilterType.UpdateByObject
                || SqlSugarAuditScope.ShouldPreserveExplicitAuditFields
            )
            {
                return;
            }

            if (entityInfo.PropertyName == "UpdatedBy")
            {
                entityInfo.SetValue("AOP-System");
            }
            if (entityInfo.PropertyName == "UpdatedAt")
            {
                entityInfo.SetValue(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            }
        };

        WarehouseProductChangeHistoryContextDto? capturedContext = null;
        var historyService = new Mock<IWarehouseProductChangeHistoryService>(MockBehavior.Strict);
        historyService
            .Setup(service => service.CaptureSnapshotsAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(new Dictionary<string, WarehouseProductChangeSnapshotDto>());
        historyService
            .Setup(service => service.RecordChangesAsync(
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
            >((_, _, context, _) => capturedContext = context)
            .ReturnsAsync(0);

        var result = await CreateService(historyService: historyService.Object)
            .SyncWarehouseProductsFromHqAsync(10, 10, "user-guid-warehouse", "仓库操作员");

        Assert.True(result.IsSuccess, result.Message);
        Assert.NotNull(capturedContext);
        Assert.Equal("user-guid-warehouse", capturedContext!.ActorUserGuid);
        Assert.Equal("仓库操作员", capturedContext.ActorName);
        Assert.Equal("User", capturedContext.ActorType);
        var inserted = await _localDb.Queryable<WarehouseProduct>()
            .SingleAsync(item => item.ProductCode == "P-ACTOR");
        Assert.Equal("原创建人", inserted.CreatedBy);
        Assert.Equal("仓库操作员", inserted.UpdatedBy);
    }

    public void Dispose()
    {
        _localDb.Dispose();
        _hqDb.Dispose();
        _localConnection.Dispose();
        _hqConnection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_localDbPath);
        SqliteTempFileCleanup.DeleteIfExists(_hqDbPath);
    }

    private DataSyncFullService CreateService(
        HqSqlSugarContext? hqContext = null,
        IWarehouseProductChangeHistoryService? historyService = null,
        ICurrentUserService? currentUserService = null
    )
    {
        var localContext = CreateSqlSugarContext(_localDb);
        var configuration = CreateHqConfiguration(_hqConnection.ConnectionString);

        return new DataSyncFullService(
            localContext,
            hqContext ?? CreateHqSqlSugarContext(_hqDb, configuration),
            CreateContext<HBSalesSqlSugarContext>(),
            CreateContext<POSMSqlSugarContext>(),
            configuration,
            _mapper,
            NullLogger<DataSyncFullService>.Instance,
            new ScheduledTaskLogService(
                localContext,
                NullLogger<ScheduledTaskLogService>.Instance
            ),
            Mock.Of<IStoreRetailPriceHqSyncService>(),
            new MemoryCache(new MemoryCacheOptions()),
            historyService ?? CreateNoopChangeHistoryService(),
            currentUserService ?? Mock.Of<ICurrentUserService>()
        );
    }

    private static IWarehouseProductChangeHistoryService CreateNoopChangeHistoryService()
    {
        var service = new Mock<IWarehouseProductChangeHistoryService>();
        service
            .Setup(item =>
                item.CaptureSnapshotsAsync(
                    It.IsAny<IEnumerable<string>>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                new Dictionary<string, WarehouseProductChangeSnapshotDto>(
                    StringComparer.OrdinalIgnoreCase
                )
            );
        service
            .Setup(item =>
                item.RecordChangesAsync(
                    It.IsAny<
                        IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>
                    >(),
                    It.IsAny<
                        IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>
                    >(),
                    It.IsAny<WarehouseProductChangeHistoryContextDto>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(0);
        return service.Object;
    }

    private async Task AssertInvalidProductSetSourcePreservesLocalAsync(
        string scenario,
        string? hqProductCode,
        string? hqChildCode,
        string? hqBarcode,
        bool seedActiveHqProduct
    )
    {
        var localSetCodeId = $"LOCAL-MUST-STAY-{scenario}";
        await _localDb.Insertable(new ProductSetCode
        {
            SetCodeId = localSetCodeId,
            ProductCode = $"LOCAL-PARENT-{scenario}",
            SetProductCode = $"LOCAL-CHILD-{scenario}",
            SetItemNumber = $"LOCAL-CHILD-{scenario}",
            SetBarcode = $"LOCAL-BARCODE-{scenario}",
            SetPurchasePrice = 1m,
            SetRetailPrice = 2m,
            SetType = 2,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();

        if (seedActiveHqProduct)
        {
            await _hqDb.Insertable(new DIC_商品信息字典表
            {
                ID = 1,
                HGUID = $"HQ-PRODUCT-{scenario}",
                H商品标签GUID = $"HQ-TAG-{scenario}",
                H商品分类码GUID = $"HQ-CATEGORY-{scenario}",
                H供货商编码 = "SUP",
                H商品编码 = hqProductCode,
                H货号 = $"ITEM-{scenario}",
                H主条形码 = $"PRODUCT-BARCODE-{scenario}",
                H商品名称 = $"无效套装来源测试-{scenario}",
                H商品类型 = 2,
                H大写名称 = $"INVALID SET SOURCE {scenario}",
                H规格 = "EA",
                H单位 = "EA",
                H进货价 = 1m,
                H零售价 = 2m,
                H是否自动定价 = false,
                H商品图片 = "image.png",
                中包数量 = 1,
                H腾讯云图地址 = "https://example.invalid/image.png",
                H使用状态 = true,
                H是否特殊商品 = false,
                H进货单主表GUID = $"ORDER-{scenario}",
                H进货单详情GUID = $"ORDER-DETAIL-{scenario}",
                CBP商品中文名称 = $"无效套装来源测试-{scenario}",
                CBP供应商编码 = "SUP",
                CBP商品分类码GUID = $"WAREHOUSE-CATEGORY-{scenario}",
                FGC_Creator = "HQ",
                FGC_CreateDate = DateTime.UtcNow.AddDays(-1),
                FGC_LastModifier = "HQ",
                FGC_LastModifyDate = DateTime.UtcNow,
                FGC_UpdateHelp = "test",
            }).ExecuteCommandAsync();
        }

        await _hqDb.Insertable(new DIC_一品多码表
        {
            HGUID = $"HQ-INVALID-{scenario}",
            H商品编码 = hqProductCode,
            H多码商品编号 = hqChildCode,
            H主条形码 = hqBarcode,
            H使用状态 = true,
            FGC_LastModifyDate = DateTime.UtcNow,
        }).ExecuteCommandAsync();

        var result = await CreateService().SyncProductSetCodesFromHqAsync(10, 10, 1);

        Assert.False(result.IsSuccess);
        Assert.Equal("PRODUCT_SET_CODE_SOURCE_INVALID", result.ErrorCode);
        Assert.Equal(1, result.ErrorCount);
        var local = await _localDb.Queryable<ProductSetCode>()
            .SingleAsync(row => row.SetCodeId == localSetCodeId);
        Assert.False(local.IsDeleted);
    }

    private async Task SeedLocalWarehouseProductAsync(
        string productCode,
        decimal domesticPrice,
        decimal oemPrice,
        decimal importPrice,
        int stockQuantity,
        int minOrderQuantity,
        decimal stockValue,
        int stockAlertQuantity,
        bool isActive,
        decimal volume,
        int packingQuantity,
        DateTime createdAt,
        string createdBy,
        bool isDeleted
    )
    {
        await _localDb.Insertable(new WarehouseProduct
        {
            ProductCode = productCode,
            DomesticPrice = domesticPrice,
            OEMPrice = oemPrice,
            ImportPrice = importPrice,
            StockQuantity = stockQuantity,
            MinOrderQuantity = minOrderQuantity,
            StockValue = stockValue,
            StockAlertQuantity = stockAlertQuantity,
            IsActive = isActive,
            Volume = volume,
            PackingQuantity = packingQuantity,
            CreatedAt = createdAt,
            CreatedBy = createdBy,
            UpdatedAt = createdAt,
            UpdatedBy = createdBy,
            IsDeleted = isDeleted,
        }).ExecuteCommandAsync();
    }

    private async Task SeedHqStockAsync(
        string productCode,
        decimal domesticPrice,
        decimal oemPrice,
        decimal importPrice,
        decimal stockQuantity,
        decimal minOrderQuantity,
        decimal stockValue,
        int stockAlertQuantity,
        int isActive
    )
    {
        await _hqDb.Insertable(new CBP_DIC_商品库存表
        {
            HGUID = $"HQ-{productCode}",
            H商品编码 = productCode,
            H国内价格 = domesticPrice,
            H贴牌价格 = oemPrice,
            H进口价格 = importPrice,
            H库存 = stockQuantity,
            H最小订货量 = minOrderQuantity,
            H库存金额 = stockValue,
            H库存预警数 = stockAlertQuantity,
            H使用状态 = isActive,
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

    private static IConfiguration CreateHqConfiguration(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:StoreHzgHQConnection"] = connectionString,
                }
            )
            .Build();

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    private static HqSqlSugarContext CreateHqSqlSugarContext(
        ISqlSugarClient db,
        IConfiguration configuration
    )
    {
        var context = (HqSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HqSqlSugarContext));
        typeof(HqSqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        typeof(HqSqlSugarContext)
            .GetField("<Configuration>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, configuration);
        return context;
    }

    private static TContext CreateContext<TContext>()
        where TContext : class
    {
        return (TContext)RuntimeHelpers.GetUninitializedObject(typeof(TContext));
    }
}
