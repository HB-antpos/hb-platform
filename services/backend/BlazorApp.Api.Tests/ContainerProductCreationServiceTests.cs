using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using AutoMapper;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HqEntities;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class ContainerProductCreationServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _sqliteConnection;
    private readonly SqlSugarClient _db;
    private readonly string _hbSalesDbPath;
    private readonly SqliteConnection _hbSalesSqliteConnection;
    private readonly SqlSugarScope _hbSalesDb;

    public ContainerProductCreationServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _sqliteConnection = new SqliteConnection($"Data Source={_dbPath}");
        _sqliteConnection.Open();

        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _sqliteConnection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });

        _db.CodeFirst.InitTables(
            typeof(Container),
            typeof(ContainerDetail),
            typeof(DomesticProduct),
            typeof(DomesticSetProduct),
            typeof(Product),
            typeof(ProductSetCode),
            typeof(WarehouseProduct),
            typeof(Store),
            typeof(StoreRetailPrice),
            typeof(StoreMultiCodeProduct),
            typeof(ChinaSupplier),
            typeof(ProductLocation),
            typeof(Location),
            typeof(ProductGrade),
            typeof(WarehouseCategory)
        );

        _hbSalesDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _hbSalesSqliteConnection = new SqliteConnection($"Data Source={_hbSalesDbPath}");
        _hbSalesSqliteConnection.Open();
        _hbSalesDb = new SqlSugarScope(new ConnectionConfig
        {
            ConnectionString = _hbSalesSqliteConnection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        _hbSalesDb.CodeFirst.InitTables(typeof(CPT_DIC_商品套装信息表));
    }

    [Fact]
    public async Task ExecuteAsync_CreatesNormalProductAndStoreRetailPrices()
    {
        await InsertActiveStoreAsync("S001");
        await InsertContainerDetailAsync("D001", "C001", "P001", "普通商品", 1.2m, 3.4m);
        await InsertDomesticProductAsync("P001", "HB001", "商品一", "Product One", 0);

        var service = CreateService();

        var result = await service.ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-1",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D001" },
            }
        );

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Contains(result.Created, item => item.ProductCode == "P001" && item.DetailHguid == "D001");
        var product = await _db.Queryable<Product>().SingleAsync(p => p.ProductCode == "P001");
        var warehouseProduct = await _db.Queryable<WarehouseProduct>().SingleAsync(p => p.ProductCode == "P001");
        var storeRetailPrice = await _db.Queryable<StoreRetailPrice>().SingleAsync(p => p.ProductCode == "P001");

        Assert.Equal(0, product.ProductType);
        Assert.Equal(1.2m, product.PurchasePrice);
        Assert.Equal(3.4m, product.RetailPrice);
        Assert.Equal(1.2m, warehouseProduct.ImportPrice);
        Assert.Equal(3.4m, warehouseProduct.OEMPrice);
        Assert.Equal(1.2m, storeRetailPrice.PurchasePrice);
        Assert.Equal(3.4m, storeRetailPrice.StoreRetailPriceValue);
    }

    [Fact]
    public async Task ExecuteAsync_商品锁冲突回滚后重试耗尽时逐条返回繁忙且不创建()
    {
        await InsertContainerDetailAsync("D-BUSY-1", "C-BUSY", "P-BUSY-1", "普通商品", 1.2m, 3.4m);
        await InsertContainerDetailAsync("D-BUSY-2", "C-BUSY", "P-BUSY-2", "普通商品", 1.2m, 3.4m);
        await InsertDomesticProductAsync("P-BUSY-1", "HB-BUSY-1", "繁忙商品一", "Busy Product One", 0);
        await InsertDomesticProductAsync("P-BUSY-2", "HB-BUSY-2", "繁忙商品二", "Busy Product Two", 0);
        var warehouseService = new Mock<IProductWarehouseReactService>();
        warehouseService
            .Setup(service => service.BatchCreateAsync(
                It.IsAny<List<CreateItemDto>>(),
                false,
                It.IsAny<string?>(),
                "ContainerSubmit",
                "C-BUSY",
                It.IsAny<Guid?>(),
                It.IsAny<string?>()
            ))
            .ThrowsAsync(new SetChildPurchasePriceLockException("test", -1));

        var result = await CreateService(warehouseService: warehouseService.Object).ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-busy",
                ContainerGuid = "C-BUSY",
                DetailHguids = new List<string> { "D-BUSY-1", "D-BUSY-2" },
            }
        );

        Assert.Equal(2, result.FailedCount);
        Assert.All(result.Errors, error =>
        {
            Assert.Equal(SetChildPurchasePriceMutationLock.BusyErrorCode, error.ReasonCode);
            Assert.Equal("商品创建繁忙，等待其他商品操作超时，本次未创建新商品", error.Message);
        });
        Assert.Contains(result.Errors, error => error.DetailHguid == "D-BUSY-1");
        Assert.Contains(result.Errors, error => error.DetailHguid == "D-BUSY-2");
        Assert.Equal(0, await _db.Queryable<Product>().CountAsync());
        warehouseService.Verify(
            service => service.BatchCreateAsync(
                It.IsAny<List<CreateItemDto>>(),
                false,
                It.IsAny<string?>(),
                "ContainerSubmit",
                "C-BUSY",
                It.IsAny<Guid?>(),
                It.IsAny<string?>()
            ),
            Times.Exactly(6)
        );
    }

    [Fact]
    public async Task ExecuteAsync_商品锁取消时不重试并逐条返回取消()
    {
        await InsertContainerDetailAsync("D-CANCELLED", "C-CANCELLED", "P-CANCELLED", "普通商品", 1.2m, 3.4m);
        await InsertDomesticProductAsync("P-CANCELLED", "HB-CANCELLED", "取消商品", "Cancelled Product", 0);
        var warehouseService = new Mock<IProductWarehouseReactService>();
        warehouseService
            .Setup(service => service.BatchCreateAsync(
                It.IsAny<List<CreateItemDto>>(),
                false,
                It.IsAny<string?>(),
                "ContainerSubmit",
                "C-CANCELLED",
                It.IsAny<Guid?>(),
                It.IsAny<string?>()
            ))
            .ThrowsAsync(new SetChildPurchasePriceLockException("test", -2));

        var result = await CreateService(warehouseService: warehouseService.Object).ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-cancelled",
                ContainerGuid = "C-CANCELLED",
                DetailHguids = new List<string> { "D-CANCELLED" },
            }
        );

        var error = Assert.Single(result.Errors);
        Assert.Equal("PRODUCT_CREATION_CANCELLED", error.ReasonCode);
        Assert.Equal("商品创建已取消，未重试", error.Message);
        Assert.Equal(1, result.FailedCount);
        warehouseService.Verify(
            service => service.BatchCreateAsync(
                It.IsAny<List<CreateItemDto>>(),
                false,
                It.IsAny<string?>(),
                "ContainerSubmit",
                "C-CANCELLED",
                It.IsAny<Guid?>(),
                It.IsAny<string?>()
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task ExecuteAsync_商品锁冲突后复读其他创建者完成的商品并跳过()
    {
        await InsertContainerDetailAsync("D-REREAD", "C-REREAD", "P-REREAD", "普通商品", 1.2m, 3.4m);
        await InsertDomesticProductAsync("P-REREAD", "HB-REREAD", "复读商品", "Reread Product", 0);
        var insertedByOtherCreator = false;
        var retryLogger = new CallbackLogger<ContainerProductCreationExecutorService>(() =>
        {
            if (insertedByOtherCreator)
            {
                return;
            }

            insertedByOtherCreator = true;
            _db.Insertable(new Product
            {
                UUID = "LP-P-REREAD",
                ProductCode = "P-REREAD",
                ItemNumber = "HB-REREAD",
                ProductName = "复读商品",
                EnglishName = "Reread Product",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommand();
            _db.Insertable(new WarehouseProduct
            {
                ProductCode = "P-REREAD",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommand();
        });
        var warehouseService = new Mock<IProductWarehouseReactService>();
        warehouseService
            .Setup(service => service.BatchCreateAsync(
                It.IsAny<List<CreateItemDto>>(),
                false,
                It.IsAny<string?>(),
                "ContainerSubmit",
                "C-REREAD",
                It.IsAny<Guid?>(),
                It.IsAny<string?>()
            ))
            .ThrowsAsync(new SetChildPurchasePriceLockException("test", -1));

        var result = await CreateService(
            warehouseService: warehouseService.Object,
            logger: retryLogger
        ).ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-reread",
                ContainerGuid = "C-REREAD",
                DetailHguids = new List<string> { "D-REREAD" },
            }
        );

        Assert.True(insertedByOtherCreator);
        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(0, result.FailedCount);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal("DUPLICATE_PRODUCT_CODE", skipped.ReasonCode);
        Assert.Equal("P-REREAD", skipped.ProductCode);
        Assert.Equal(1, await _db.Queryable<Product>().Where(item => item.ProductCode == "P-REREAD").CountAsync());
        warehouseService.Verify(
            service => service.BatchCreateAsync(
                It.IsAny<List<CreateItemDto>>(),
                false,
                It.IsAny<string?>(),
                "ContainerSubmit",
                "C-REREAD",
                It.IsAny<Guid?>(),
                It.IsAny<string?>()
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task ExecuteAsync_UsesProvidedUpdatedByForCreatedWarehouseProduct()
    {
        await InsertActiveStoreAsync("S001");
        await InsertContainerDetailAsync("D-UPDATER", "C001", "P-UPDATER", "普通商品", 1.2m, 3.4m);
        await InsertDomesticProductAsync("P-UPDATER", "HB-UPDATER", "更新人商品", "Updater Product", 0);

        await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-updater",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-UPDATER" },
            },
            "操作员 A"
        );

        var warehouseProduct = await _db.Queryable<WarehouseProduct>()
            .SingleAsync(item => item.ProductCode == "P-UPDATER");
        Assert.Equal("操作员 A", warehouseProduct.CreatedBy);
        Assert.Equal("操作员 A", warehouseProduct.UpdatedBy);
    }

    [Fact]
    public async Task ExecuteAsync_FallsBackToSystemWhenUpdatedByIsBlank()
    {
        await InsertActiveStoreAsync("S001");
        await InsertContainerDetailAsync("D-UPDATER-EMPTY", "C001", "P-UPDATER-EMPTY", "普通商品", 1.2m, 3.4m);
        await InsertDomesticProductAsync("P-UPDATER-EMPTY", "HB-UPDATER-EMPTY", "空更新人商品", "Empty Updater Product", 0);

        await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-updater-empty",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-UPDATER-EMPTY" },
            },
            "  "
        );

        var warehouseProduct = await _db.Queryable<WarehouseProduct>()
            .SingleAsync(item => item.ProductCode == "P-UPDATER-EMPTY");
        Assert.Equal("System", warehouseProduct.UpdatedBy);
    }

    [Fact]
    public async Task Controller_UsesIdentityNameAsJobUpdatedBy()
    {
        var jobService = new Mock<IContainerProductCreationJobService>(MockBehavior.Strict);
        jobService
            .Setup(service => service.StartJobAsync(
                "claim-user-id",
                "操作员 A",
                It.Is<ContainerProductCreationJobRequestDto>(request =>
                    request.OperationId == "op-controller" && request.ContainerGuid == "C001"),
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(new ContainerProductCreationJobDto { JobId = "job-1" });
        var controller = new ReactContainerProductsController(
            jobService.Object,
            NullLogger<ReactContainerProductsController>.Instance
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[]
                        {
                            new Claim(ClaimTypes.NameIdentifier, "claim-user-id"),
                            new Claim(ClaimTypes.Name, "操作员 A"),
                        },
                        "test"
                    )),
                },
            },
        };

        var result = await controller.StartCreateNewProductsJob(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-controller",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D001" },
            },
            CancellationToken.None
        );

        Assert.IsType<OkObjectResult>(result);
        jobService.VerifyAll();
    }

    [Fact]
    public async Task Controller_StartSubmitContainerJob_稳定用户与身份名称透传并提交全柜()
    {
        var request = new ContainerProductCreationJobRequestDto
        {
            OperationId = "op-submit-container",
            ContainerGuid = "C001",
            DetailHguids = new List<string> { "D001", "D002" },
        };
        var jobService = new Mock<IContainerProductCreationJobService>(MockBehavior.Strict);
        jobService
            .Setup(service => service.StartJobAsync(
                "stable-user-id",
                "货柜提交人",
                It.Is<ContainerProductCreationJobRequestDto>(submitted =>
                    ReferenceEquals(submitted, request)
                    && submitted.SubmitContainer
                    && submitted.DetailHguids != null
                    && submitted.DetailHguids.Count == 0),
                CancellationToken.None
            ))
            .ReturnsAsync(new ContainerProductCreationJobDto { JobId = "submit-container-job-1" });
        var controller = new ReactContainerProductsController(
            jobService.Object,
            NullLogger<ReactContainerProductsController>.Instance
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[]
                        {
                            new Claim(ClaimTypes.NameIdentifier, "stable-user-id"),
                            new Claim(ClaimTypes.Name, "货柜提交人"),
                        },
                        "test"
                    )),
                },
            },
        };

        var result = await controller.StartSubmitContainerJob(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        jobService.Verify(
            service => service.StartJobAsync(
                "stable-user-id",
                "货柜提交人",
                It.Is<ContainerProductCreationJobRequestDto>(submitted =>
                    ReferenceEquals(submitted, request)
                    && submitted.SubmitContainer
                    && submitted.DetailHguids != null
                    && submitted.DetailHguids.Count == 0),
                CancellationToken.None
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task JobService_PassesActorGuidAndUpdatedByToScopedExecutor()
    {
        var executor = new BlockingContainerProductCreationExecutor();
        var services = new ServiceCollection();
        services.AddSingleton<IContainerProductCreationExecutorService>(executor);
        var provider = services.BuildServiceProvider();
        var jobService = new ContainerProductCreationJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ContainerProductCreationJobService>.Instance
        );

        await jobService.StartJobAsync(
            "claim-user-id",
            "操作员 A",
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-job-updater",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D001" },
            }
        );

        Assert.Equal("claim-user-id", await executor.WaitForActorUserGuidAsync());
        Assert.Equal("操作员 A", await executor.WaitForUpdatedByAsync());
        executor.Release();
    }

    [Fact]
    public async Task ExecuteAsync_CreatesSetProductWhenRelationIsComplete()
    {
        await InsertActiveStoreAsync("S001");
        await InsertContainerDetailAsync("D-SET-OK", "C001", "P-SET-OK", "套装商品", 2.2m, 5.5m);
        await InsertDomesticProductAsync("P-SET-OK", "HB-SET-OK", "完整套装", "Complete Set", 1);
        await InsertDomesticSetProductAsync("P-SET-OK", "SET-CODE-1", "HB-SET-OK-A");

        var service = CreateService();

        var result = await service.ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-set-ok",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-SET-OK" },
            }
        );

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(0, result.FailedCount);

        var setCode = await _db.Queryable<ProductSetCode>()
            .FirstAsync(item => item.ProductCode == "P-SET-OK");
        Assert.NotNull(setCode);
        Assert.Equal("SET-CODE-1", setCode.SetProductCode);

        var product = await _db.Queryable<Product>().SingleAsync(p => p.ProductCode == "P-SET-OK");
        Assert.Equal(1, product.ProductType);

        var storeMultiCode = await _db.Queryable<StoreMultiCodeProduct>()
            .FirstAsync(item => item.ProductCode == "P-SET-OK");
        Assert.NotNull(storeMultiCode);
        Assert.Equal("SET-CODE-1", storeMultiCode.MultiCodeProductCode);
    }

    [Fact]
    public async Task ExecuteAsync_CreatesSetRelationsFromSameMixedGroupChildrenWhenMainOnlySelected()
    {
        await InsertActiveStoreAsync("S001");
        await InsertContainerDetailAsync(
            "D-SET-MAIN",
            "C001",
            "P-SET-MAIN",
            "套装商品",
            10m,
            18.8m,
            mixedGroupCode: "MG-001"
        );
        await InsertDomesticProductAsync("P-SET-MAIN", "HB-SET-MAIN", "主套装", "Main Set", 1);
        await InsertContainerDetailAsync(
            "D-SET-CHILD-A",
            "C001",
            "P-SET-CHILD-A",
            "套装子商品",
            2.2m,
            4m,
            domesticPrice: 12.5m,
            mixedGroupCode: "MG-001",
            setQuantity: 2m
        );
        await InsertDomesticProductAsync("P-SET-CHILD-A", "HB-SET-CHILD-A", "套装子项", "Set Child A", 0);
        await InsertContainerDetailAsync(
            "D-SET-CHILD-B",
            "C001",
            "P-SET-CHILD-B",
            "套装子商品",
            9.9m,
            6m,
            domesticPrice: 13.5m,
            mixedGroupCode: "MG-001",
            setQuantity: 1m
        );
        await InsertDomesticProductAsync("P-SET-CHILD-B", "HB-SET-CHILD-B", "套装子项B", "Set Child B", 0);

        var service = CreateService();

        var result = await service.ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-set-main-only",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-SET-MAIN" },
            }
        );

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(0, result.FailedCount);

        var product = await _db.Queryable<Product>().SingleAsync(p => p.ProductCode == "P-SET-MAIN");
        var warehouseProduct = await _db.Queryable<WarehouseProduct>().SingleAsync(p => p.ProductCode == "P-SET-MAIN");
        var domesticSets = await _db.Queryable<DomesticSetProduct>()
            .Where(item => item.ProductCode == "P-SET-MAIN")
            .OrderBy(item => item.SetProductCode)
            .ToListAsync();
        var productSetCodes = await _db.Queryable<ProductSetCode>()
            .Where(item => item.ProductCode == "P-SET-MAIN")
            .OrderBy(item => item.SetProductCode)
            .ToListAsync();
        var storeMultiCodes = await _db.Queryable<StoreMultiCodeProduct>()
            .Where(item => item.ProductCode == "P-SET-MAIN")
            .OrderBy(item => item.MultiCodeProductCode)
            .ToListAsync();

        Assert.Equal("HB-SET-MAIN", product.ItemNumber);
        Assert.Equal("P-SET-MAIN", warehouseProduct.ProductCode);
        Assert.Equal(2, domesticSets.Count);
        Assert.Equal(2, productSetCodes.Count);
        Assert.Equal(2, storeMultiCodes.Count);
        Assert.Contains(domesticSets, item =>
            item.SetProductCode == "P-SET-CHILD-A"
            && item.ProductNo == "HB-SET-MAIN"
            && item.SetProductNo == "HB-SET-CHILD-A"
            && item.SetBarcode == "BAR-HB-SET-CHILD-A"
            && item.DomesticPrice == 12.5m
            && item.ImportPrice == 2.2m
            && item.OEMPrice == 4m
        );
        Assert.Contains(productSetCodes, item =>
            item.SetProductCode == "P-SET-CHILD-A"
            && item.SetItemNumber == "HB-SET-CHILD-A"
            && item.SetRetailPrice == 4m
            && item.SetPurchasePrice == 4m
        );
        Assert.Contains(productSetCodes, item =>
            item.SetProductCode == "P-SET-CHILD-B"
            && item.SetItemNumber == "HB-SET-CHILD-B"
            && item.SetRetailPrice == 6m
            && item.SetPurchasePrice == 6m
        );
        Assert.Contains(storeMultiCodes, item =>
            item.StoreCode == "S001"
            && item.MultiCodeProductCode == "P-SET-CHILD-A"
            && item.MultiCodeRetailPrice == 4m
            && item.PurchasePrice == 4m
        );
        Assert.Contains(storeMultiCodes, item =>
            item.StoreCode == "S001"
            && item.MultiCodeProductCode == "P-SET-CHILD-B"
            && item.MultiCodeRetailPrice == 6m
            && item.PurchasePrice == 6m
        );
    }

    [Fact]
    public async Task ExecuteAsync_同组原始子项编码为空时应拒绝整组套装创建()
    {
        await InsertContainerDetailAsync(
            "D-SET-MAIN-EMPTY-CHILD",
            "C001",
            "P-SET-MAIN-EMPTY-CHILD",
            "套装商品",
            10m,
            18.8m,
            mixedGroupCode: "MG-EMPTY-CHILD"
        );
        await InsertDomesticProductAsync(
            "P-SET-MAIN-EMPTY-CHILD",
            "HB-SET-MAIN-EMPTY-CHILD",
            "主套装",
            "Main Set",
            1
        );
        await InsertContainerDetailAsync(
            "D-SET-CHILD-VALID",
            "C001",
            "P-SET-CHILD-VALID",
            "套装子商品",
            2m,
            4m,
            mixedGroupCode: "MG-EMPTY-CHILD"
        );
        await InsertDomesticProductAsync(
            "P-SET-CHILD-VALID",
            "HB-SET-CHILD-VALID",
            "有效子项",
            "Valid Child",
            0
        );
        await InsertContainerDetailAsync(
            "D-SET-CHILD-EMPTY",
            "C001",
            string.Empty,
            "套装子商品",
            2m,
            4m,
            mixedGroupCode: "MG-EMPTY-CHILD"
        );

        var result = await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-set-empty-child",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-SET-MAIN-EMPTY-CHILD" },
            }
        );

        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(
            0,
            await _db.Queryable<Product>()
                .Where(item => item.ProductCode == "P-SET-MAIN-EMPTY-CHILD")
                .CountAsync()
        );
        Assert.Equal(
            0,
            await _db.Queryable<DomesticSetProduct>()
                .Where(item => item.ProductCode == "P-SET-MAIN-EMPTY-CHILD")
                .CountAsync()
        );
    }

    [Fact]
    public async Task ExecuteAsync_同组原始子项编码Trim加Upper后重复时应拒绝整组套装创建()
    {
        await InsertContainerDetailAsync(
            "D-SET-MAIN-DUP-CHILD",
            "C001",
            "P-SET-MAIN-DUP-CHILD",
            "套装商品",
            10m,
            18.8m,
            mixedGroupCode: "MG-DUP-CHILD"
        );
        await InsertDomesticProductAsync(
            "P-SET-MAIN-DUP-CHILD",
            "HB-SET-MAIN-DUP-CHILD",
            "主套装",
            "Main Set",
            1
        );
        await InsertContainerDetailAsync(
            "D-SET-CHILD-DUP-A",
            "C001",
            "P-SET-CHILD-DUP",
            "套装子商品",
            2m,
            4m,
            mixedGroupCode: "MG-DUP-CHILD"
        );
        await InsertDomesticProductAsync(
            "P-SET-CHILD-DUP",
            "HB-SET-CHILD-DUP-A",
            "子项 A",
            "Child A",
            0
        );
        await InsertContainerDetailAsync(
            "D-SET-CHILD-DUP-B",
            "C001",
            " p-set-child-dup ",
            "套装子商品",
            2m,
            4m,
            mixedGroupCode: "MG-DUP-CHILD"
        );
        await InsertDomesticProductAsync(
            " p-set-child-dup ",
            "HB-SET-CHILD-DUP-B",
            "子项 B",
            "Child B",
            0
        );

        var result = await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-set-duplicate-child",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-SET-MAIN-DUP-CHILD" },
            }
        );

        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(
            0,
            await _db.Queryable<Product>()
                .Where(item => item.ProductCode == "P-SET-MAIN-DUP-CHILD")
                .CountAsync()
        );
        Assert.Equal(
            0,
            await _db.Queryable<DomesticSetProduct>()
                .Where(item => item.ProductCode == "P-SET-MAIN-DUP-CHILD")
                .CountAsync()
        );
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotCountSelectedSetChildWhenSameBatchCreatesMainSet()
    {
        await InsertActiveStoreAsync("S001");
        await InsertContainerDetailAsync(
            "D-SET-MAIN-BATCH",
            "C001",
            "P-SET-MAIN-BATCH",
            "套装商品",
            8.8m,
            18.8m,
            mixedGroupCode: "MG-002"
        );
        await InsertDomesticProductAsync("P-SET-MAIN-BATCH", "HB-SET-MAIN-BATCH", "批量套装", "Batch Set", 1);
        await InsertContainerDetailAsync(
            "D-SET-CHILD-BATCH",
            "C001",
            "P-SET-CHILD-BATCH",
            "套装子商品",
            2.2m,
            6.6m,
            mixedGroupCode: "MG-002"
        );
        await InsertDomesticProductAsync("P-SET-CHILD-BATCH", "HB-SET-CHILD-BATCH", "批量子项", "Batch Child", 0);

        var service = CreateService();

        var result = await service.ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-set-main-child",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-SET-MAIN-BATCH", "D-SET-CHILD-BATCH" },
            }
        );

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Contains(result.Created, item => item.ProductCode == "P-SET-MAIN-BATCH");
        Assert.Equal(0, await _db.Queryable<Product>().Where(p => p.ProductCode == "P-SET-CHILD-BATCH").CountAsync());
        Assert.Equal(1, await _db.Queryable<ProductSetCode>().Where(p => p.ProductCode == "P-SET-MAIN-BATCH").CountAsync());
        Assert.Equal(1, await _db.Queryable<StoreMultiCodeProduct>().Where(p => p.ProductCode == "P-SET-MAIN-BATCH").CountAsync());
    }

    [Fact]
    public async Task ExecuteAsync_CompletesSetCodesForExistingSetProductFromLocalSetChildTable()
    {
        await InsertActiveStoreAsync("S001");
        await InsertExistingProductAsync("P-SET-EXISTS", "HB-SET-EXISTS", 8.8m, 18.8m);
        await InsertExistingWarehouseProductAsync("P-SET-EXISTS", 1.1m, 2.2m, 3.3m);
        await InsertContainerDetailAsync(
            "D-SET-EXISTS",
            "C001",
            "P-SET-EXISTS",
            "套装商品",
            8.8m,
            18.8m,
            mixedGroupCode: "MG-EXISTS"
        );
        await InsertDomesticProductAsync("P-SET-EXISTS", "HB-SET-EXISTS", "已存在套装", "Existing Set", 1);
        await InsertDomesticSetProductAsync("P-SET-EXISTS", "P-SET-EXISTS-CHILD", "HB-SET-EXISTS-CHILD");

        var service = CreateService();

        var result = await service.ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-existing-set-complete",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-SET-EXISTS" },
            }
        );

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Contains(result.Created, item =>
            item.ProductCode == "P-SET-EXISTS" && item.Message == "套装子码已补齐"
        );
        Assert.Equal(1, await _db.Queryable<Product>().Where(p => p.ProductCode == "P-SET-EXISTS").CountAsync());
        Assert.Equal(1, await _db.Queryable<WarehouseProduct>().Where(p => p.ProductCode == "P-SET-EXISTS").CountAsync());

        var domesticSet = await _db.Queryable<DomesticSetProduct>()
            .SingleAsync(item => item.ProductCode == "P-SET-EXISTS");
        var productSetCode = await _db.Queryable<ProductSetCode>()
            .SingleAsync(item => item.ProductCode == "P-SET-EXISTS");
        var storeMultiCode = await _db.Queryable<StoreMultiCodeProduct>()
            .SingleAsync(item => item.ProductCode == "P-SET-EXISTS");

        Assert.Equal("P-SET-EXISTS-CHILD", domesticSet.SetProductCode);
        Assert.Equal("HB-SET-EXISTS-CHILD", domesticSet.SetProductNo);
        Assert.Equal("P-SET-EXISTS-CHILD", productSetCode.SetProductCode);
        Assert.Equal("HB-SET-EXISTS-CHILD", productSetCode.SetItemNumber);
        Assert.Equal("P-SET-EXISTS-CHILD", storeMultiCode.MultiCodeProductCode);
        Assert.Equal("S001", storeMultiCode.StoreCode);

        var product = await _db.Queryable<Product>().SingleAsync(p => p.ProductCode == "P-SET-EXISTS");
        var warehouseProduct = await _db.Queryable<WarehouseProduct>().SingleAsync(p => p.ProductCode == "P-SET-EXISTS");
        Assert.Equal(1, product.ProductType);
        Assert.Equal(8.8m, product.PurchasePrice);
        Assert.Equal(18.8m, product.RetailPrice);
        Assert.Equal(1.1m, warehouseProduct.DomesticPrice);
        Assert.Equal(2.2m, warehouseProduct.ImportPrice);
        Assert.Equal(3.3m, warehouseProduct.OEMPrice);
    }

    [Fact]
    public async Task ExecuteAsync_FixesExistingSetProductTypeWithoutChangingMainProductFields()
    {
        await InsertActiveStoreAsync("S001");
        await InsertExistingProductAsync("P-SET-TYPE-FIX", "HB-SET-TYPE-FIX", 8.8m, 18.8m, productType: 0);
        await InsertExistingWarehouseProductAsync("P-SET-TYPE-FIX", 1.1m, 2.2m, 3.3m);
        await InsertContainerDetailAsync(
            "D-SET-TYPE-FIX",
            "C001",
            "P-SET-TYPE-FIX",
            "套装商品",
            9.9m,
            19.9m
        );
        await InsertDomesticProductAsync("P-SET-TYPE-FIX", "HB-SET-TYPE-FIX", "套装类型修复", "Type Fix Set", 1);
        await InsertDomesticSetProductAsync("P-SET-TYPE-FIX", "P-SET-TYPE-FIX-CHILD", "HB-SET-TYPE-FIX-CHILD");

        var service = CreateService();

        var result = await service.ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-existing-set-type-fix",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-SET-TYPE-FIX" },
            }
        );

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Contains(result.Created, item => item.ProductCode == "P-SET-TYPE-FIX");
        Assert.Equal(1, await _db.Queryable<Product>().Where(p => p.ProductCode == "P-SET-TYPE-FIX").CountAsync());
        Assert.Equal(1, await _db.Queryable<WarehouseProduct>().Where(p => p.ProductCode == "P-SET-TYPE-FIX").CountAsync());

        var product = await _db.Queryable<Product>().SingleAsync(p => p.ProductCode == "P-SET-TYPE-FIX");
        var warehouseProduct = await _db.Queryable<WarehouseProduct>().SingleAsync(p => p.ProductCode == "P-SET-TYPE-FIX");
        Assert.Equal(1, product.ProductType);
        Assert.Equal("HB-SET-TYPE-FIX", product.ProductName);
        Assert.Equal("HB-SET-TYPE-FIX", product.EnglishName);
        Assert.Equal(8.8m, product.PurchasePrice);
        Assert.Equal(18.8m, product.RetailPrice);
        Assert.Equal(1.1m, warehouseProduct.DomesticPrice);
        Assert.Equal(2.2m, warehouseProduct.ImportPrice);
        Assert.Equal(3.3m, warehouseProduct.OEMPrice);
    }

    [Fact]
    public async Task ExecuteAsync_LoadsExistingSetChildrenFromHqWhenLocalSetChildTableIsEmpty()
    {
        await InsertActiveStoreAsync("S001");
        await InsertExistingProductAsync("P-SET-HQ", "HB-SET-HQ", 8.8m, 18.8m);
        await InsertExistingWarehouseProductAsync("P-SET-HQ", 1.1m, 2.2m, 3.3m);
        await InsertContainerDetailAsync("D-SET-HQ", "C001", "P-SET-HQ", "套装商品", 8.8m, 18.8m);
        await InsertDomesticProductAsync("P-SET-HQ", "HB-SET-HQ", "HQ套装", "HQ Set", 1);
        await InsertHqDomesticSetProductAsync(
            "HQ-SET-CODE-1",
            "P-SET-HQ",
            "HB-SET-HQ-01",
            "BAR-HQ-SET-01",
            domesticPrice: 12.5m,
            importPrice: 2.5m,
            oemPrice: 6.5m
        );

        var service = CreateService();

        var result = await service.ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-existing-set-hq",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-SET-HQ" },
            }
        );

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Contains(result.Created, item =>
            item.ProductCode == "P-SET-HQ" && item.Message == "套装子码已补齐"
        );

        var domesticSet = await _db.Queryable<DomesticSetProduct>().SingleAsync(x => x.ProductCode == "P-SET-HQ");
        var productSetCode = await _db.Queryable<ProductSetCode>().SingleAsync(x => x.ProductCode == "P-SET-HQ");
        var storeMultiCode = await _db.Queryable<StoreMultiCodeProduct>().SingleAsync(x => x.ProductCode == "P-SET-HQ");

        Assert.Equal("HB-SET-HQ-01", domesticSet.SetProductCode);
        Assert.Equal("HB-SET-HQ", domesticSet.ProductNo);
        Assert.Equal("HB-SET-HQ-01", domesticSet.SetProductNo);
        Assert.Equal("BAR-HQ-SET-01", domesticSet.SetBarcode);
        Assert.Equal(12.5m, domesticSet.DomesticPrice);
        Assert.Equal("HB-SET-HQ-01", productSetCode.SetProductCode);
        Assert.Equal(8.8m, productSetCode.SetPurchasePrice);
        Assert.Equal("HB-SET-HQ-01", storeMultiCode.MultiCodeProductCode);
        Assert.Equal(6.5m, storeMultiCode.MultiCodeRetailPrice);
    }

    [Fact]
    public async Task ExecuteAsync_Hq套装子项规范化后重复时应拒绝整组且不落本地关系()
    {
        await InsertExistingProductAsync(
            "P-SET-HQ-DUP",
            "HB-SET-HQ-DUP",
            8.8m,
            18.8m,
            productType: 0
        );
        await InsertExistingWarehouseProductAsync("P-SET-HQ-DUP", 1.1m, 2.2m, 3.3m);
        await InsertContainerDetailAsync(
            "D-SET-HQ-DUP",
            "C001",
            "P-SET-HQ-DUP",
            "套装商品",
            8.8m,
            18.8m
        );
        await InsertDomesticProductAsync(
            "P-SET-HQ-DUP",
            "HB-SET-HQ-DUP",
            "HQ重复套装",
            "HQ Duplicate Set",
            1
        );
        await InsertHqDomesticSetProductAsync(
            "HQ-DUP-1",
            "P-SET-HQ-DUP",
            "HQ-CHILD-DUP",
            "BAR-HQ-DUP-1",
            12.5m,
            2.5m,
            6.5m
        );
        await InsertHqDomesticSetProductAsync(
            "HQ-DUP-2",
            "P-SET-HQ-DUP",
            " hq-child-dup ",
            "BAR-HQ-DUP-2",
            13.5m,
            3.5m,
            7.5m
        );
        var beforeProduct = await _db.Queryable<Product>()
            .SingleAsync(item => item.ProductCode == "P-SET-HQ-DUP");

        var result = await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-existing-set-hq-duplicate",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-SET-HQ-DUP" },
            }
        );

        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Contains(result.Errors, error => error.Message?.Contains("套装子项编码重复") == true);
        Assert.Equal(
            0,
            await _db.Queryable<DomesticSetProduct>()
                .Where(item => item.ProductCode == "P-SET-HQ-DUP")
                .CountAsync()
        );
        var afterProduct = await _db.Queryable<Product>()
            .SingleAsync(item => item.ProductCode == "P-SET-HQ-DUP");
        Assert.Equal(beforeProduct.ProductType, afterProduct.ProductType);
        Assert.Equal(beforeProduct.UpdatedAt, afterProduct.UpdatedAt);
        Assert.Equal(beforeProduct.UpdatedBy, afterProduct.UpdatedBy);
        Assert.Equal(
            0,
            await _db.Queryable<ProductSetCode>()
                .Where(item => item.ProductCode == "P-SET-HQ-DUP")
                .CountAsync()
        );
        Assert.Equal(
            0,
            await _db.Queryable<StoreMultiCodeProduct>()
                .Where(item => item.ProductCode == "P-SET-HQ-DUP")
                .CountAsync()
        );
    }

    [Fact]
    public async Task ExecuteAsync_Hq套装子项所有业务键为空时应拒绝整组且不生成随机键()
    {
        await InsertExistingProductAsync(
            "P-SET-HQ-EMPTY",
            "HB-SET-HQ-EMPTY",
            8.8m,
            18.8m,
            productType: 0
        );
        await InsertExistingWarehouseProductAsync("P-SET-HQ-EMPTY", 1.1m, 2.2m, 3.3m);
        await InsertContainerDetailAsync(
            "D-SET-HQ-EMPTY",
            "C001",
            "P-SET-HQ-EMPTY",
            "套装商品",
            8.8m,
            18.8m
        );
        await InsertDomesticProductAsync(
            "P-SET-HQ-EMPTY",
            "HB-SET-HQ-EMPTY",
            "HQ空键套装",
            "HQ Empty Set",
            1
        );
        await InsertHqDomesticSetProductAsync(
            "   ",
            "P-SET-HQ-EMPTY",
            "   ",
            "   ",
            12.5m,
            2.5m,
            6.5m
        );
        var beforeProduct = await _db.Queryable<Product>()
            .SingleAsync(item => item.ProductCode == "P-SET-HQ-EMPTY");

        var result = await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-existing-set-hq-empty",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-SET-HQ-EMPTY" },
            }
        );

        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Contains(
            result.Errors,
            error => error.Message?.Contains("存在空的套装子项编码") == true
        );
        Assert.Equal(
            0,
            await _db.Queryable<DomesticSetProduct>()
                .Where(item => item.ProductCode == "P-SET-HQ-EMPTY")
                .CountAsync()
        );
        var afterProduct = await _db.Queryable<Product>()
            .SingleAsync(item => item.ProductCode == "P-SET-HQ-EMPTY");
        Assert.Equal(beforeProduct.ProductType, afterProduct.ProductType);
        Assert.Equal(beforeProduct.UpdatedAt, afterProduct.UpdatedAt);
        Assert.Equal(beforeProduct.UpdatedBy, afterProduct.UpdatedBy);
        Assert.Equal(
            0,
            await _db.Queryable<ProductSetCode>()
                .Where(item => item.ProductCode == "P-SET-HQ-EMPTY")
                .CountAsync()
        );
    }

    [Fact]
    public async Task ExecuteAsync_SkipsExistingSetProductWithSetChildNotFoundInsteadOfDuplicate()
    {
        await InsertExistingProductAsync("P-SET-NO-CHILD", "HB-SET-NO-CHILD", 8.8m, 18.8m);
        await InsertExistingWarehouseProductAsync("P-SET-NO-CHILD", 1.1m, 2.2m, 3.3m);
        await InsertContainerDetailAsync(
            "D-SET-NO-CHILD",
            "C001",
            "P-SET-NO-CHILD",
            "套装商品",
            8.8m,
            18.8m,
            mixedGroupCode: "MG-NO-CHILD"
        );
        await InsertDomesticProductAsync("P-SET-NO-CHILD", "HB-SET-NO-CHILD", "无子项套装", "No Child Set", 1);
        await InsertContainerDetailAsync(
            "D-SET-NO-CHILD-GROUP-CHILD",
            "C001",
            "P-SET-NO-CHILD-GROUP-CHILD",
            "套装子商品",
            2.2m,
            6.6m,
            mixedGroupCode: "MG-NO-CHILD"
        );
        await InsertDomesticProductAsync(
            "P-SET-NO-CHILD-GROUP-CHILD",
            "HB-SET-NO-CHILD-GROUP-CHILD",
            "同组子项",
            "Same Group Child",
            0
        );

        var service = CreateService();

        var result = await service.ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-existing-set-no-child",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-SET-NO-CHILD" },
            }
        );

        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Contains(result.Skipped, item =>
            item.ProductCode == "P-SET-NO-CHILD"
            && item.DetailHguid == "D-SET-NO-CHILD"
            && item.ReasonCode == "SET_CHILD_NOT_FOUND"
        );
        Assert.DoesNotContain(result.Skipped, item => item.ReasonCode == "DUPLICATE_PRODUCT_CODE");
    }

    [Fact]
    public async Task ExecuteAsync_CompletesMissingProductSetAndStoreMultiCodesForExistingSetRelation()
    {
        await InsertActiveStoreAsync("S001");
        await InsertActiveStoreAsync("S002");
        await InsertExistingProductAsync("P-SET-PARTIAL", "HB-SET-PARTIAL", 10m, 18.8m);
        await InsertExistingWarehouseProductAsync("P-SET-PARTIAL", 1.1m, 2.2m, 3.3m);
        await InsertContainerDetailAsync(
            "D-SET-PARTIAL",
            "C001",
            "P-SET-PARTIAL",
            "套装商品",
            8.8m,
            18.8m,
            mixedGroupCode: "MG-PARTIAL"
        );
        await InsertDomesticProductAsync("P-SET-PARTIAL", "HB-SET-PARTIAL", "部分套装", "Partial Set", 1);
        await InsertDomesticSetProductAsync("P-SET-PARTIAL", "P-SET-PARTIAL-CHILD", "HB-SET-PARTIAL-CHILD", importPrice: 9.9m, oemPrice: 4m);
        await InsertDomesticSetProductAsync("P-SET-PARTIAL", "P-SET-PARTIAL-CHILD-B", "HB-SET-PARTIAL-CHILD-B", importPrice: 8.8m, oemPrice: 6m);
        await InsertProductSetCodeAsync("P-SET-PARTIAL", "P-SET-PARTIAL-CHILD", "HB-SET-PARTIAL-CHILD", purchasePrice: 9.9m, retailPrice: 4m);
        await InsertStoreMultiCodeAsync("S001", "P-SET-PARTIAL", "P-SET-PARTIAL-CHILD", "BAR-HB-SET-PARTIAL-CHILD");

        var service = CreateService();

        var result = await service.ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-existing-set-partial",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-SET-PARTIAL" },
            }
        );

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Contains(result.Created, item =>
            item.ProductCode == "P-SET-PARTIAL" && item.Message == "套装子码已补齐"
        );
        Assert.Equal(2, await _db.Queryable<ProductSetCode>().Where(p => p.ProductCode == "P-SET-PARTIAL").CountAsync());
        Assert.Equal(4, await _db.Queryable<StoreMultiCodeProduct>().Where(p => p.ProductCode == "P-SET-PARTIAL").CountAsync());
        var updatedSetCode = await _db.Queryable<ProductSetCode>()
            .SingleAsync(p => p.ProductCode == "P-SET-PARTIAL" && p.SetProductCode == "P-SET-PARTIAL-CHILD");
        var insertedSetCode = await _db.Queryable<ProductSetCode>()
            .SingleAsync(p => p.ProductCode == "P-SET-PARTIAL" && p.SetProductCode == "P-SET-PARTIAL-CHILD-B");
        Assert.Equal(4m, updatedSetCode.SetPurchasePrice);
        Assert.Equal(6m, insertedSetCode.SetPurchasePrice);
        Assert.NotNull(await _db.Queryable<StoreMultiCodeProduct>()
            .FirstAsync(p => p.ProductCode == "P-SET-PARTIAL" && p.StoreCode == "S002"));
    }

    [Fact]
    public async Task ExecuteAsync_Type1活跃行与历史墓碑并存时只窄更新活跃行()
    {
        const string productCode = "P-SET-ACTIVE-HISTORY";
        const string childCode = "P-SET-ACTIVE-HISTORY-CHILD";
        const string updatedBy = "容器建品回归测试";
        var createdAt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var oldUpdatedAt = createdAt.AddDays(1);
        await PrepareExistingSetCompletionAsync(
            productCode,
            childCode,
            "D-SET-ACTIVE-HISTORY",
            productType: 1
        );
        await _db.Insertable(new[]
        {
            new ProductSetCode
            {
                SetCodeId = "SET-ACTIVE",
                ProductCode = productCode,
                SetProductCode = childCode,
                SetItemNumber = "旧套装货号",
                SetBarcode = "OLD-SET-BARCODE",
                SetPurchasePrice = 10m,
                SetRetailPrice = 1m,
                SetQuantity = 7,
                SetType = 1,
                IsActive = true,
                IsDeleted = false,
                CreatedAt = createdAt,
                CreatedBy = "总部活跃创建人",
                UpdatedAt = oldUpdatedAt,
                UpdatedBy = "总部活跃旧更新人",
            },
            new ProductSetCode
            {
                SetCodeId = "SET-DELETED",
                ProductCode = productCode,
                SetProductCode = childCode,
                SetItemNumber = "软删除历史货号",
                SetBarcode = "DELETED-SET-BARCODE",
                SetPurchasePrice = 91m,
                SetRetailPrice = 92m,
                SetQuantity = 9,
                SetType = 2,
                IsActive = true,
                IsDeleted = true,
                CreatedAt = createdAt.AddYears(-2),
                CreatedBy = "总部删除创建人",
                UpdatedAt = oldUpdatedAt.AddYears(-2),
                UpdatedBy = "总部删除更新人",
            },
            new ProductSetCode
            {
                SetCodeId = "SET-INACTIVE",
                ProductCode = productCode,
                SetProductCode = childCode,
                SetItemNumber = "停用历史货号",
                SetBarcode = "INACTIVE-SET-BARCODE",
                SetPurchasePrice = 81m,
                SetRetailPrice = 82m,
                SetQuantity = 8,
                SetType = 3,
                IsActive = false,
                IsDeleted = false,
                CreatedAt = createdAt.AddYears(-1),
                CreatedBy = "总部停用创建人",
                UpdatedAt = oldUpdatedAt.AddYears(-1),
                UpdatedBy = "总部停用更新人",
            },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new StoreMultiCodeProduct
            {
                UUID = "STORE-ACTIVE",
                StoreCode = "S001",
                ProductCode = productCode,
                MultiCodeProductCode = childCode,
                StoreMultiCodeProductCode = "LEGACY-STORE-MULTI-CODE",
                MultiBarcode = "OLD-STORE-BARCODE",
                PurchasePrice = 10m,
                MultiCodeRetailPrice = 1m,
                DiscountRate = 0.25m,
                IsAutoPricing = true,
                IsSpecialProduct = true,
                IsActive = true,
                IsDeleted = false,
                CreatedAt = createdAt,
                CreatedBy = "门店活跃创建人",
                UpdatedAt = oldUpdatedAt,
                UpdatedBy = "门店活跃旧更新人",
            },
            new StoreMultiCodeProduct
            {
                UUID = "STORE-DELETED",
                StoreCode = "S001",
                ProductCode = productCode,
                MultiCodeProductCode = childCode,
                StoreMultiCodeProductCode = "DELETED-STORE-MULTI-CODE",
                MultiBarcode = "DELETED-STORE-BARCODE",
                PurchasePrice = 71m,
                MultiCodeRetailPrice = 72m,
                DiscountRate = 0.71m,
                IsAutoPricing = true,
                IsSpecialProduct = true,
                IsActive = true,
                IsDeleted = true,
                CreatedAt = createdAt.AddYears(-2),
                CreatedBy = "门店删除创建人",
                UpdatedAt = oldUpdatedAt.AddYears(-2),
                UpdatedBy = "门店删除更新人",
            },
            new StoreMultiCodeProduct
            {
                UUID = "STORE-INACTIVE",
                StoreCode = "S001",
                ProductCode = productCode,
                MultiCodeProductCode = childCode,
                StoreMultiCodeProductCode = "INACTIVE-STORE-MULTI-CODE",
                MultiBarcode = "INACTIVE-STORE-BARCODE",
                PurchasePrice = 61m,
                MultiCodeRetailPrice = 62m,
                DiscountRate = 0.61m,
                IsAutoPricing = true,
                IsSpecialProduct = true,
                IsActive = false,
                IsDeleted = false,
                CreatedAt = createdAt.AddYears(-1),
                CreatedBy = "门店停用创建人",
                UpdatedAt = oldUpdatedAt.AddYears(-1),
                UpdatedBy = "门店停用更新人",
            },
        }).ExecuteCommandAsync();

        var result = await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-active-with-history",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-SET-ACTIVE-HISTORY" },
            },
            updatedBy
        );

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(0, result.FailedCount);

        var setRows = await _db.Queryable<ProductSetCode>()
            .Where(row => row.ProductCode == productCode && row.SetProductCode == childCode)
            .ToListAsync();
        Assert.Equal(3, setRows.Count);
        var activeSet = Assert.Single(setRows, row => row.IsActive && !row.IsDeleted);
        Assert.Equal("SET-ACTIVE", activeSet.SetCodeId);
        Assert.Equal(productCode, activeSet.ProductCode);
        Assert.Equal(childCode, activeSet.SetProductCode);
        Assert.Equal($"ITEM-{childCode}", activeSet.SetItemNumber);
        Assert.Equal($"BAR-ITEM-{childCode}", activeSet.SetBarcode);
        Assert.Equal(10m, activeSet.SetPurchasePrice);
        Assert.Equal(6.5m, activeSet.SetRetailPrice);
        Assert.Equal(1, activeSet.SetQuantity);
        Assert.Equal(1, activeSet.SetType);
        Assert.Equal(createdAt, activeSet.CreatedAt);
        Assert.Equal("总部活跃创建人", activeSet.CreatedBy);
        Assert.Equal(updatedBy, activeSet.UpdatedBy);
        Assert.NotEqual(oldUpdatedAt, activeSet.UpdatedAt);

        var deletedSet = Assert.Single(setRows, row => row.SetCodeId == "SET-DELETED");
        Assert.True(deletedSet.IsDeleted);
        Assert.True(deletedSet.IsActive);
        Assert.Equal("软删除历史货号", deletedSet.SetItemNumber);
        Assert.Equal("DELETED-SET-BARCODE", deletedSet.SetBarcode);
        Assert.Equal(91m, deletedSet.SetPurchasePrice);
        Assert.Equal(92m, deletedSet.SetRetailPrice);
        Assert.Equal(9, deletedSet.SetQuantity);
        Assert.Equal(2, deletedSet.SetType);
        Assert.Equal("总部删除创建人", deletedSet.CreatedBy);
        Assert.Equal("总部删除更新人", deletedSet.UpdatedBy);

        var inactiveSet = Assert.Single(setRows, row => row.SetCodeId == "SET-INACTIVE");
        Assert.False(inactiveSet.IsActive);
        Assert.False(inactiveSet.IsDeleted);
        Assert.Equal("停用历史货号", inactiveSet.SetItemNumber);
        Assert.Equal("INACTIVE-SET-BARCODE", inactiveSet.SetBarcode);
        Assert.Equal(81m, inactiveSet.SetPurchasePrice);
        Assert.Equal(82m, inactiveSet.SetRetailPrice);
        Assert.Equal(8, inactiveSet.SetQuantity);
        Assert.Equal(3, inactiveSet.SetType);
        Assert.Equal("总部停用创建人", inactiveSet.CreatedBy);
        Assert.Equal("总部停用更新人", inactiveSet.UpdatedBy);

        var storeRows = await _db.Queryable<StoreMultiCodeProduct>()
            .Where(row =>
                row.StoreCode == "S001"
                && row.ProductCode == productCode
                && row.MultiCodeProductCode == childCode
            )
            .ToListAsync();
        Assert.Equal(3, storeRows.Count);
        var activeStore = Assert.Single(storeRows, row => row.IsActive && !row.IsDeleted);
        Assert.Equal("STORE-ACTIVE", activeStore.UUID);
        Assert.Equal("S001", activeStore.StoreCode);
        Assert.Equal(productCode, activeStore.ProductCode);
        Assert.Equal(childCode, activeStore.MultiCodeProductCode);
        Assert.Equal($"S001{childCode}", activeStore.StoreMultiCodeProductCode);
        Assert.Equal($"BAR-ITEM-{childCode}", activeStore.MultiBarcode);
        Assert.Equal(10m, activeStore.PurchasePrice);
        Assert.Equal(6.5m, activeStore.MultiCodeRetailPrice);
        Assert.Equal(0.25m, activeStore.DiscountRate);
        Assert.True(activeStore.IsAutoPricing);
        Assert.True(activeStore.IsSpecialProduct);
        Assert.Equal(createdAt, activeStore.CreatedAt);
        Assert.Equal("门店活跃创建人", activeStore.CreatedBy);
        Assert.Equal(updatedBy, activeStore.UpdatedBy);
        Assert.NotEqual(oldUpdatedAt, activeStore.UpdatedAt);

        var deletedStore = Assert.Single(storeRows, row => row.UUID == "STORE-DELETED");
        Assert.True(deletedStore.IsDeleted);
        Assert.True(deletedStore.IsActive);
        Assert.Equal("DELETED-STORE-MULTI-CODE", deletedStore.StoreMultiCodeProductCode);
        Assert.Equal("DELETED-STORE-BARCODE", deletedStore.MultiBarcode);
        Assert.Equal(71m, deletedStore.PurchasePrice);
        Assert.Equal(72m, deletedStore.MultiCodeRetailPrice);
        Assert.Equal(0.71m, deletedStore.DiscountRate);
        Assert.Equal("门店删除创建人", deletedStore.CreatedBy);
        Assert.Equal("门店删除更新人", deletedStore.UpdatedBy);

        var inactiveStore = Assert.Single(storeRows, row => row.UUID == "STORE-INACTIVE");
        Assert.False(inactiveStore.IsActive);
        Assert.False(inactiveStore.IsDeleted);
        Assert.Equal("INACTIVE-STORE-MULTI-CODE", inactiveStore.StoreMultiCodeProductCode);
        Assert.Equal("INACTIVE-STORE-BARCODE", inactiveStore.MultiBarcode);
        Assert.Equal(61m, inactiveStore.PurchasePrice);
        Assert.Equal(62m, inactiveStore.MultiCodeRetailPrice);
        Assert.Equal(0.61m, inactiveStore.DiscountRate);
        Assert.Equal("门店停用创建人", inactiveStore.CreatedBy);
        Assert.Equal("门店停用更新人", inactiveStore.UpdatedBy);
    }

    [Fact]
    public async Task ExecuteAsync_Type1仅有历史总部行时应报数据质量并回滚父商品()
    {
        const string productCode = "P-SET-HISTORY-ONLY-HQ";
        const string childCode = "P-SET-HISTORY-ONLY-HQ-CHILD";
        await PrepareExistingSetCompletionAsync(
            productCode,
            childCode,
            "D-SET-HISTORY-ONLY-HQ",
            productType: 0
        );
        await _db.Insertable(new[]
        {
            CreateProductSetProjection(
                "SET-HISTORY-DELETED",
                productCode,
                childCode,
                isActive: true,
                isDeleted: true
            ),
            CreateProductSetProjection(
                "SET-HISTORY-INACTIVE",
                productCode,
                childCode,
                isActive: false,
                isDeleted: false
            ),
        }).ExecuteCommandAsync();
        var beforeProduct = await _db.Queryable<Product>()
            .SingleAsync(row => row.ProductCode == productCode);

        var result = await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-history-only-hq",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-SET-HISTORY-ONLY-HQ" },
            },
            "历史总部测试"
        );

        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Contains(result.Errors, error =>
            error.ReasonCode == "SET_GROUP_DATA_QUALITY_ERROR"
            && error.Message?.Contains("仅存在停用或软删除的总部关系") == true
        );
        var afterProduct = await _db.Queryable<Product>()
            .SingleAsync(row => row.ProductCode == productCode);
        Assert.Equal(beforeProduct.ProductType, afterProduct.ProductType);
        Assert.Equal(beforeProduct.UpdatedAt, afterProduct.UpdatedAt);
        Assert.Equal(beforeProduct.UpdatedBy, afterProduct.UpdatedBy);
        var historyRows = await _db.Queryable<ProductSetCode>()
            .Where(row => row.ProductCode == productCode && row.SetProductCode == childCode)
            .ToListAsync();
        Assert.Equal(2, historyRows.Count);
        Assert.DoesNotContain(historyRows, row => row.IsActive && !row.IsDeleted);
    }

    [Fact]
    public async Task ExecuteAsync_Type1多条活跃总部行时应报数据质量并回滚父商品()
    {
        const string productCode = "P-SET-MULTI-ACTIVE-HQ";
        const string childCode = "P-SET-MULTI-ACTIVE-HQ-CHILD";
        await PrepareExistingSetCompletionAsync(
            productCode,
            childCode,
            "D-SET-MULTI-ACTIVE-HQ",
            productType: 0
        );
        await _db.Insertable(new[]
        {
            CreateProductSetProjection("SET-ACTIVE-A", productCode, childCode),
            CreateProductSetProjection("SET-ACTIVE-B", productCode, childCode),
        }).ExecuteCommandAsync();
        var beforeProduct = await _db.Queryable<Product>()
            .SingleAsync(row => row.ProductCode == productCode);

        var result = await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-multi-active-hq",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-SET-MULTI-ACTIVE-HQ" },
            },
            "多总部活跃测试"
        );

        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Contains(result.Errors, error =>
            error.ReasonCode == "SET_GROUP_DATA_QUALITY_ERROR"
            && error.Message?.Contains("存在多条活跃总部关系") == true
        );
        var afterProduct = await _db.Queryable<Product>()
            .SingleAsync(row => row.ProductCode == productCode);
        Assert.Equal(beforeProduct.ProductType, afterProduct.ProductType);
        Assert.Equal(beforeProduct.UpdatedAt, afterProduct.UpdatedAt);
        Assert.Equal(beforeProduct.UpdatedBy, afterProduct.UpdatedBy);
    }

    [Fact]
    public async Task ExecuteAsync_Type1仅有历史门店行时应报数据质量并回滚已写变更()
    {
        const string productCode = "P-SET-HISTORY-ONLY-STORE";
        const string childCode = "P-SET-HISTORY-ONLY-STORE-CHILD";
        await PrepareExistingSetCompletionAsync(
            productCode,
            childCode,
            "D-SET-HISTORY-ONLY-STORE",
            productType: 0
        );
        var activeSet = CreateProductSetProjection("SET-ACTIVE", productCode, childCode);
        activeSet.SetItemNumber = "回滚前总部货号";
        activeSet.SetBarcode = "ROLLBACK-BEFORE-BARCODE";
        activeSet.SetRetailPrice = 1m;
        activeSet.UpdatedBy = "回滚前总部更新人";
        await _db.Insertable(activeSet).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            CreateStoreMultiCodeProjection(
                "STORE-HISTORY-DELETED",
                productCode,
                childCode,
                isActive: true,
                isDeleted: true
            ),
            CreateStoreMultiCodeProjection(
                "STORE-HISTORY-INACTIVE",
                productCode,
                childCode,
                isActive: false,
                isDeleted: false
            ),
        }).ExecuteCommandAsync();
        var beforeProduct = await _db.Queryable<Product>()
            .SingleAsync(row => row.ProductCode == productCode);

        var result = await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-history-only-store",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-SET-HISTORY-ONLY-STORE" },
            },
            "历史门店测试"
        );

        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Contains(result.Errors, error =>
            error.ReasonCode == "SET_GROUP_DATA_QUALITY_ERROR"
            && error.Message?.Contains("仅存在停用或软删除的门店关系") == true
        );
        var afterProduct = await _db.Queryable<Product>()
            .SingleAsync(row => row.ProductCode == productCode);
        Assert.Equal(beforeProduct.ProductType, afterProduct.ProductType);
        Assert.Equal(beforeProduct.UpdatedAt, afterProduct.UpdatedAt);
        Assert.Equal(beforeProduct.UpdatedBy, afterProduct.UpdatedBy);
        var rolledBackSet = await _db.Queryable<ProductSetCode>()
            .SingleAsync(row => row.SetCodeId == "SET-ACTIVE");
        Assert.Equal("回滚前总部货号", rolledBackSet.SetItemNumber);
        Assert.Equal("ROLLBACK-BEFORE-BARCODE", rolledBackSet.SetBarcode);
        Assert.Equal(1m, rolledBackSet.SetRetailPrice);
        Assert.Equal("回滚前总部更新人", rolledBackSet.UpdatedBy);
    }

    [Fact]
    public async Task ExecuteAsync_Type1多条活跃门店行时应报数据质量并回滚整柜事务()
    {
        const string containerCode = "C-SET-MULTI-ACTIVE-STORE";
        const string productCode = "P-SET-MULTI-ACTIVE-STORE";
        const string childCode = "P-SET-MULTI-ACTIVE-STORE-CHILD";
        await PrepareExistingSetCompletionAsync(
            productCode,
            childCode,
            "D-SET-MULTI-ACTIVE-STORE",
            containerCode,
            productType: 0,
            insertContainer: true
        );
        var activeSet = CreateProductSetProjection("SET-ACTIVE", productCode, childCode);
        activeSet.SetItemNumber = "整柜回滚前总部货号";
        activeSet.SetRetailPrice = 1m;
        await _db.Insertable(activeSet).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            CreateStoreMultiCodeProjection("STORE-ACTIVE-A", productCode, childCode),
            CreateStoreMultiCodeProjection("STORE-ACTIVE-B", productCode, childCode),
        }).ExecuteCommandAsync();
        var beforeProduct = await _db.Queryable<Product>()
            .SingleAsync(row => row.ProductCode == productCode);

        var result = await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = $"submit-container:{containerCode}",
                ContainerGuid = containerCode,
                SubmitContainer = true,
            },
            "多门店活跃测试"
        );

        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.False(result.ContainerCompleted);
        Assert.Contains(result.Errors, error =>
            error.ReasonCode == "SET_GROUP_DATA_QUALITY_ERROR"
            && error.Message?.Contains("存在多条活跃门店关系") == true
        );
        var container = await _db.Queryable<Container>()
            .SingleAsync(row => row.ContainerCode == containerCode);
        Assert.Equal(1, container.Status);
        var afterProduct = await _db.Queryable<Product>()
            .SingleAsync(row => row.ProductCode == productCode);
        Assert.Equal(beforeProduct.ProductType, afterProduct.ProductType);
        Assert.Equal(beforeProduct.UpdatedAt, afterProduct.UpdatedAt);
        Assert.Equal(beforeProduct.UpdatedBy, afterProduct.UpdatedBy);
        var rolledBackSet = await _db.Queryable<ProductSetCode>()
            .SingleAsync(row => row.SetCodeId == "SET-ACTIVE");
        Assert.Equal("整柜回滚前总部货号", rolledBackSet.SetItemNumber);
        Assert.Equal(1m, rolledBackSet.SetRetailPrice);
    }

    [Fact]
    public async Task ExecuteAsync_Type1缺失投影时应按新建语义初始化审计与生命周期()
    {
        const string productCode = "P-SET-NEW-PROJECTION";
        const string childCode = "P-SET-NEW-PROJECTION-CHILD";
        const string updatedBy = "新建投影测试";
        await PrepareExistingSetCompletionAsync(
            productCode,
            childCode,
            "D-SET-NEW-PROJECTION",
            productType: 1
        );

        var result = await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-new-projection-audit",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-SET-NEW-PROJECTION" },
            },
            updatedBy
        );

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(0, result.FailedCount);
        var setCode = await _db.Queryable<ProductSetCode>()
            .SingleAsync(row => row.ProductCode == productCode && row.SetProductCode == childCode);
        Assert.Equal(childCode, setCode.SetCodeId);
        Assert.Equal(1, setCode.SetType);
        Assert.True(setCode.IsActive);
        Assert.False(setCode.IsDeleted);
        Assert.Equal(updatedBy, setCode.CreatedBy);
        Assert.Equal(updatedBy, setCode.UpdatedBy);

        var storeMultiCode = await _db.Queryable<StoreMultiCodeProduct>()
            .SingleAsync(row =>
                row.StoreCode == "S001"
                && row.ProductCode == productCode
                && row.MultiCodeProductCode == childCode
            );
        Assert.False(string.IsNullOrWhiteSpace(storeMultiCode.UUID));
        Assert.Equal($"S001{childCode}", storeMultiCode.StoreMultiCodeProductCode);
        Assert.True(storeMultiCode.IsActive);
        Assert.False(storeMultiCode.IsDeleted);
        Assert.Null(storeMultiCode.DiscountRate);
        Assert.False(storeMultiCode.IsAutoPricing);
        Assert.False(storeMultiCode.IsSpecialProduct);
        Assert.Equal(updatedBy, storeMultiCode.CreatedBy);
        Assert.Equal(updatedBy, storeMultiCode.UpdatedBy);
    }

    [Fact]
    public async Task ExecuteAsync_同父同子键已有Type2时不得改写为套装关系()
    {
        const string productCode = "P-SET-TYPE2-SAME-KEY";
        const string childCode = "P-SET-TYPE2-CHILD";
        await InsertActiveStoreAsync("S001");
        await InsertExistingProductAsync(productCode, "HB-SET-TYPE2", 10m, 18.8m);
        await InsertExistingWarehouseProductAsync(productCode, 1.1m, 2.2m, 3.3m);
        await InsertContainerDetailAsync(
            "D-SET-TYPE2-SAME-KEY",
            "C001",
            productCode,
            "套装商品",
            10m,
            18.8m
        );
        await InsertDomesticProductAsync(productCode, "HB-SET-TYPE2", "套装", "Set", 1);
        await InsertDomesticSetProductAsync(
            productCode,
            childCode,
            "HB-SET-TYPE2-CHILD",
            importPrice: 2m,
            oemPrice: 5m
        );
        await _db.Insertable(new ProductSetCode
        {
            SetCodeId = "TYPE2-GLOBAL",
            ProductCode = productCode,
            SetProductCode = childCode,
            SetItemNumber = "TYPE2-ITEM",
            SetBarcode = "TYPE2-BARCODE",
            SetPurchasePrice = 77m,
            SetRetailPrice = 88m,
            SetQuantity = 9,
            SetType = 2,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _db.Insertable(new StoreMultiCodeProduct
        {
            UUID = "TYPE2-STORE",
            StoreCode = "S001",
            ProductCode = productCode,
            MultiCodeProductCode = childCode,
            StoreMultiCodeProductCode = "S001-TYPE2",
            MultiBarcode = "TYPE2-STORE-BARCODE",
            PurchasePrice = 66m,
            MultiCodeRetailPrice = 77m,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();

        var result = await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-existing-type2-same-key",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-SET-TYPE2-SAME-KEY" },
            }
        );

        Assert.Equal(1, result.FailedCount);
        Assert.Contains(result.Errors, x =>
            x.ProductCode == productCode && x.Message?.Contains("普通多码关系占用") == true
        );
        var globalRows = await _db.Queryable<ProductSetCode>()
            .Where(x => x.ProductCode == productCode && x.SetProductCode == childCode)
            .ToListAsync();
        var globalType2 = Assert.Single(globalRows);
        Assert.Equal(2, globalType2.SetType);
        Assert.Equal("TYPE2-BARCODE", globalType2.SetBarcode);
        Assert.Equal(77m, globalType2.SetPurchasePrice);
        Assert.Equal(88m, globalType2.SetRetailPrice);
        Assert.Equal(9, globalType2.SetQuantity);

        var storeRows = await _db.Queryable<StoreMultiCodeProduct>()
            .Where(x =>
                x.StoreCode == "S001"
                && x.ProductCode == productCode
                && x.MultiCodeProductCode == childCode
            )
            .ToListAsync();
        var storeType2 = Assert.Single(storeRows);
        Assert.Equal("TYPE2-STORE-BARCODE", storeType2.MultiBarcode);
        Assert.Equal(66m, storeType2.PurchasePrice);
        Assert.Equal(77m, storeType2.MultiCodeRetailPrice);
    }

    [Fact]
    public async Task ExecuteAsync_DistributesRoundingDifferenceToLastSetChild()
    {
        await InsertActiveStoreAsync("S001");
        await InsertExistingProductAsync("P-SET-ROUND", "HB-SET-ROUND", 10m, 18.8m);
        await InsertExistingWarehouseProductAsync("P-SET-ROUND", 1.1m, 2.2m, 3.3m);
        await InsertContainerDetailAsync("D-SET-ROUND", "C001", "P-SET-ROUND", "套装商品", 10m, 18.8m);
        await InsertDomesticProductAsync("P-SET-ROUND", "HB-SET-ROUND", "舍入套装", "Round Set", 1);
        await InsertDomesticSetProductAsync("P-SET-ROUND", "P-SET-ROUND-A", "HB-SET-ROUND-A", importPrice: 1m, oemPrice: 1m);
        await InsertDomesticSetProductAsync("P-SET-ROUND", "P-SET-ROUND-B", "HB-SET-ROUND-B", importPrice: 1m, oemPrice: 1m);
        await InsertDomesticSetProductAsync("P-SET-ROUND", "P-SET-ROUND-C", "HB-SET-ROUND-C", importPrice: 1m, oemPrice: 1m);

        var result = await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-existing-set-round",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-SET-ROUND" },
            }
        );

        Assert.Equal(1, result.CreatedCount);
        var setCodes = await _db.Queryable<ProductSetCode>()
            .Where(p => p.ProductCode == "P-SET-ROUND")
            .OrderBy(p => p.SetProductCode)
            .ToListAsync();
        Assert.Equal(new[] { 3.33m, 3.33m, 3.34m }, setCodes.Select(item => item.SetPurchasePrice.GetValueOrDefault()).ToArray());
        Assert.Equal(10m, setCodes.Sum(item => item.SetPurchasePrice));
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsCompleteWhenExistingSetCodesAreAlreadyComplete()
    {
        await InsertActiveStoreAsync("S001");
        await InsertExistingProductAsync("P-SET-COMPLETE", "HB-SET-COMPLETE", 8.8m, 18.8m, productType: 1);
        await InsertExistingWarehouseProductAsync("P-SET-COMPLETE", 1.1m, 2.2m, 3.3m);
        await InsertContainerDetailAsync(
            "D-SET-COMPLETE",
            "C001",
            "P-SET-COMPLETE",
            "套装商品",
            8.8m,
            18.8m,
            mixedGroupCode: "MG-COMPLETE"
        );
        await InsertDomesticProductAsync("P-SET-COMPLETE", "HB-SET-COMPLETE", "完整已存在套装", "Complete Existing Set", 1);
        await InsertDomesticSetProductAsync("P-SET-COMPLETE", "P-SET-COMPLETE-CHILD", "HB-SET-COMPLETE-CHILD");
        await InsertProductSetCodeAsync(
            "P-SET-COMPLETE",
            "P-SET-COMPLETE-CHILD",
            "HB-SET-COMPLETE-CHILD",
            purchasePrice: 8.8m,
            retailPrice: 5.5m
        );
        await InsertStoreMultiCodeAsync(
            "S001",
            "P-SET-COMPLETE",
            "P-SET-COMPLETE-CHILD",
            "BAR-HB-SET-COMPLETE-CHILD",
            purchasePrice: 8.8m
        );

        var service = CreateService();

        var result = await service.ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-existing-set-complete-noop",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-SET-COMPLETE" },
            }
        );

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Contains(result.Created, item =>
            item.ProductCode == "P-SET-COMPLETE" && item.Message == "套装子码已完整"
        );
        Assert.Equal(1, await _db.Queryable<DomesticSetProduct>().Where(p => p.ProductCode == "P-SET-COMPLETE").CountAsync());
        Assert.Equal(1, await _db.Queryable<ProductSetCode>().Where(p => p.ProductCode == "P-SET-COMPLETE").CountAsync());
        Assert.Equal(1, await _db.Queryable<StoreMultiCodeProduct>().Where(p => p.ProductCode == "P-SET-COMPLETE").CountAsync());
    }

    [Fact]
    public async Task ExecuteAsync_已存在套装关系价为空时重跑保留已有零售价且重算通过()
    {
        // 复现线上现象：套装首次创建成功后，页面没刷新又点一次「创建新商品」。
        // 套装里有一行垃圾关系（DomesticSetProduct.OEMPrice 为空），已有行的零售价来自首次创建时的父商品兜底价；
        // 旧逻辑把已有行价格覆盖成空，导致「子项零售价为空或0」重算失败并回滚。
        const string productCode = "P-SET-KEEP-PRICE";
        await SeedExistingSetWithTwoChildrenAsync(
            productCode,
            "C-SET-KEEP-PRICE",
            "D-SET-KEEP-PRICE",
            relationPriceA: 20m,
            relationPriceB: null,
            activeStoreCodes: new[] { "S001", "S002" }
        );

        var result = await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-set-keep-price",
                ContainerGuid = "C-SET-KEEP-PRICE",
                DetailHguids = new List<string> { "D-SET-KEEP-PRICE" },
            }
        );

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(0, result.FailedCount);
        // 关系价为空 + 其余字段都没变：没有任何需要刷新的内容，应判定为“已完整”。
        Assert.Contains(result.Created, item =>
            item.ProductCode == productCode && item.Message == "套装子码已完整"
        );
        var setCodes = await _db.Queryable<ProductSetCode>().Where(p => p.ProductCode == productCode).ToListAsync();
        Assert.Equal(20m, setCodes.Single(p => p.SetProductCode == $"{productCode}-A").SetRetailPrice);
        Assert.Equal(50m, setCodes.Single(p => p.SetProductCode == $"{productCode}-B").SetRetailPrice);
        Assert.Equal(10m, setCodes.Sum(p => p.SetPurchasePrice));
        var storeRows = await _db.Queryable<StoreMultiCodeProduct>().Where(p => p.ProductCode == productCode).ToListAsync();
        Assert.Equal(4, storeRows.Count);
        Assert.All(storeRows.Where(p => p.MultiCodeProductCode == $"{productCode}-B"), row => Assert.Equal(50m, row.MultiCodeRetailPrice));
        Assert.All(storeRows.Where(p => p.MultiCodeProductCode == $"{productCode}-A"), row => Assert.Equal(20m, row.MultiCodeRetailPrice));
        foreach (var storeCode in new[] { "S001", "S002" })
        {
            Assert.Equal(10m, storeRows.Where(p => p.StoreCode == storeCode).Sum(p => p.PurchasePrice));
        }
    }

    [Fact]
    public async Task ExecuteAsync_已存在套装关系价为0时同样保留已有零售价()
    {
        // 关系价为 0 与为空同样不可用，不能把已有的有效零售价覆盖成 0。
        const string productCode = "P-SET-KEEP-ZERO";
        await SeedExistingSetWithTwoChildrenAsync(
            productCode,
            "C-SET-KEEP-ZERO",
            "D-SET-KEEP-ZERO",
            relationPriceA: 0m,
            relationPriceB: 0m,
            activeStoreCodes: new[] { "S001" }
        );

        var result = await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-set-keep-zero",
                ContainerGuid = "C-SET-KEEP-ZERO",
                DetailHguids = new List<string> { "D-SET-KEEP-ZERO" },
            }
        );

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Contains(result.Created, item => item.Message == "套装子码已完整");
        var setCodes = await _db.Queryable<ProductSetCode>().Where(p => p.ProductCode == productCode).ToListAsync();
        Assert.Equal(20m, setCodes.Single(p => p.SetProductCode == $"{productCode}-A").SetRetailPrice);
        Assert.Equal(50m, setCodes.Single(p => p.SetProductCode == $"{productCode}-B").SetRetailPrice);
    }

    [Fact]
    public async Task ExecuteAsync_已存在套装关系价大于0且不同时仍刷新为新价()
    {
        // 关系价可用（> 0）时保持原行为：已有行价格跟随关系价刷新，总部与所有启用门店一致。
        const string productCode = "P-SET-REFRESH-PRICE";
        await SeedExistingSetWithTwoChildrenAsync(
            productCode,
            "C-SET-REFRESH-PRICE",
            "D-SET-REFRESH-PRICE",
            relationPriceA: 25m,
            relationPriceB: 50m,
            activeStoreCodes: new[] { "S001", "S002" }
        );

        var result = await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-set-refresh-price",
                ContainerGuid = "C-SET-REFRESH-PRICE",
                DetailHguids = new List<string> { "D-SET-REFRESH-PRICE" },
            }
        );

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Contains(result.Created, item => item.Message == "套装子码已补齐");
        var setCodes = await _db.Queryable<ProductSetCode>().Where(p => p.ProductCode == productCode).ToListAsync();
        Assert.Equal(25m, setCodes.Single(p => p.SetProductCode == $"{productCode}-A").SetRetailPrice);
        Assert.Equal(50m, setCodes.Single(p => p.SetProductCode == $"{productCode}-B").SetRetailPrice);
        Assert.Equal(10m, setCodes.Sum(p => p.SetPurchasePrice));
        var storeRows = await _db.Queryable<StoreMultiCodeProduct>().Where(p => p.ProductCode == productCode).ToListAsync();
        Assert.All(storeRows.Where(p => p.MultiCodeProductCode == $"{productCode}-A"), row => Assert.Equal(25m, row.MultiCodeRetailPrice));
        Assert.All(storeRows.Where(p => p.MultiCodeProductCode == $"{productCode}-B"), row => Assert.Equal(50m, row.MultiCodeRetailPrice));
    }

    [Fact]
    public async Task ExecuteAsync_缺少总部与门店行的套装带空价关系时仍被校验拒绝且整体回滚()
    {
        // 防止放过真正的坏数据：保留已有价只针对“已有行”；新建分支（已有商品缺 ProductSetCode/门店多码行）
        // 带着空价关系去新建时，零售价仍为空，重算校验必须拒绝并回滚，不能落下半成品。
        const string productCode = "P-SET-NEW-NULL-PRICE";
        const string childCode = "P-SET-NEW-NULL-PRICE-CHILD";
        await PrepareExistingSetCompletionAsync(productCode, childCode, "D-SET-NEW-NULL-PRICE", productType: 1);
        await _db.Updateable<DomesticSetProduct>()
            .SetColumns(p => p.OEMPrice == null)
            .Where(p => p.ProductCode == productCode)
            .ExecuteCommandAsync();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-set-new-null-price",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-SET-NEW-NULL-PRICE" },
            }
        ));

        Assert.Contains("子项零售价为空或0", exception.Message);
        Assert.Equal(0, await _db.Queryable<ProductSetCode>().Where(p => p.ProductCode == productCode).CountAsync());
        Assert.Equal(0, await _db.Queryable<StoreMultiCodeProduct>().Where(p => p.ProductCode == productCode).CountAsync());
    }

    [Fact]
    public async Task ExecuteAsync_未启用门店存在空价或0价多码行时不影响套装重跑且不被改动()
    {
        // 校验与重算只覆盖启用门店（Store.IsActive && !IsDeleted）；未启用门店上的脏多码行既不参与校验，也不被刷新。
        const string productCode = "P-SET-INACTIVE-STORE";
        await SeedExistingSetWithTwoChildrenAsync(
            productCode,
            "C-SET-INACTIVE-STORE",
            "D-SET-INACTIVE-STORE",
            relationPriceA: 25m,
            relationPriceB: null,
            activeStoreCodes: new[] { "S001" }
        );
        await InsertStoreAsync("S-OFF", isActive: false, isDeleted: false);
        await InsertStoreAsync("S-DEL", isActive: true, isDeleted: true);
        foreach (var storeCode in new[] { "S-OFF", "S-DEL" })
        {
            await InsertStoreMultiCodeAsync(storeCode, productCode, $"{productCode}-A", "BAR-OLD-A", purchasePrice: null, retailPrice: null);
            await InsertStoreMultiCodeAsync(storeCode, productCode, $"{productCode}-B", "BAR-OLD-B", purchasePrice: 99m, retailPrice: 0m);
        }

        var result = await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-set-inactive-store",
                ContainerGuid = "C-SET-INACTIVE-STORE",
                DetailHguids = new List<string> { "D-SET-INACTIVE-STORE" },
            }
        );

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(0, result.FailedCount);
        // 启用门店正常刷新：A 跟随关系价 25，B 关系价为空保留 50。
        var activeRows = await _db.Queryable<StoreMultiCodeProduct>()
            .Where(p => p.ProductCode == productCode && p.StoreCode == "S001")
            .ToListAsync();
        Assert.Equal(25m, activeRows.Single(p => p.MultiCodeProductCode == $"{productCode}-A").MultiCodeRetailPrice);
        Assert.Equal(50m, activeRows.Single(p => p.MultiCodeProductCode == $"{productCode}-B").MultiCodeRetailPrice);
        Assert.Equal(10m, activeRows.Sum(p => p.PurchasePrice));
        // 未启用/已删除门店的行原样保留：不新增、不刷新、不重算成本。
        foreach (var storeCode in new[] { "S-OFF", "S-DEL" })
        {
            var rows = await _db.Queryable<StoreMultiCodeProduct>()
                .Where(p => p.ProductCode == productCode && p.StoreCode == storeCode)
                .ToListAsync();
            Assert.Equal(2, rows.Count);
            var rowA = rows.Single(p => p.MultiCodeProductCode == $"{productCode}-A");
            Assert.Null(rowA.MultiCodeRetailPrice);
            Assert.Null(rowA.PurchasePrice);
            Assert.Equal("BAR-OLD-A", rowA.MultiBarcode);
            var rowB = rows.Single(p => p.MultiCodeProductCode == $"{productCode}-B");
            Assert.Equal(0m, rowB.MultiCodeRetailPrice);
            Assert.Equal(99m, rowB.PurchasePrice);
        }
    }

    [Fact]
    public async Task ExecuteAsync_SubmitContainer_已存在套装关系价大于0且不同时仍刷新并完成货柜()
    {
        // 整柜提交口径不变：关系价可用时照旧刷新已有行，其余校验与货柜完成逻辑不受影响。
        const string productCode = "P-SET-SUBMIT-REFRESH";
        const string containerCode = "C-SET-SUBMIT-REFRESH";
        await SeedExistingSetWithTwoChildrenAsync(
            productCode,
            containerCode,
            "D-SET-SUBMIT-REFRESH",
            relationPriceA: 25m,
            relationPriceB: 50m,
            activeStoreCodes: new[] { "S001" },
            insertContainer: true
        );

        var result = await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = $"submit-container:{containerCode}",
                ContainerGuid = containerCode,
                SubmitContainer = true,
            }
        );

        Assert.Equal(0, result.FailedCount);
        Assert.True(result.ContainerCompleted);
        var setCodes = await _db.Queryable<ProductSetCode>().Where(p => p.ProductCode == productCode).ToListAsync();
        Assert.Equal(25m, setCodes.Single(p => p.SetProductCode == $"{productCode}-A").SetRetailPrice);
        Assert.Equal(50m, setCodes.Single(p => p.SetProductCode == $"{productCode}-B").SetRetailPrice);
        var container = await _db.Queryable<Container>().SingleAsync(item => item.ContainerCode == containerCode);
        Assert.Equal(2, container.Status);
    }

    [Fact]
    public async Task ExecuteAsync_SubmitContainer_已存在套装关系价为空时保留已有价并完成货柜()
    {
        // 补码刷新逻辑被整柜提交与创建新商品共用，同一个“关系价为空不覆盖已有价”的修正对两者都生效。
        const string productCode = "P-SET-SUBMIT-KEEP";
        const string containerCode = "C-SET-SUBMIT-KEEP";
        await SeedExistingSetWithTwoChildrenAsync(
            productCode,
            containerCode,
            "D-SET-SUBMIT-KEEP",
            relationPriceA: 20m,
            relationPriceB: null,
            activeStoreCodes: new[] { "S001" },
            insertContainer: true
        );

        var result = await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = $"submit-container:{containerCode}",
                ContainerGuid = containerCode,
                SubmitContainer = true,
            }
        );

        Assert.Equal(0, result.FailedCount);
        Assert.True(result.ContainerCompleted);
        var setCodes = await _db.Queryable<ProductSetCode>().Where(p => p.ProductCode == productCode).ToListAsync();
        Assert.Equal(50m, setCodes.Single(p => p.SetProductCode == $"{productCode}-B").SetRetailPrice);
        var container = await _db.Queryable<Container>().SingleAsync(item => item.ContainerCode == containerCode);
        Assert.Equal(2, container.Status);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsRowsWithMissingEnglishNameAndSetChild()
    {
        await InsertContainerDetailAsync("D-MISSING-EN", "C001", "P-MISSING-EN", "普通商品", 1.2m, 3.4m);
        await InsertDomesticProductAsync("P-MISSING-EN", "HB-MISSING-EN", "缺英文", null, 0);
        await InsertContainerDetailAsync("D-SET-CHILD", "C001", "P-SET-CHILD", "套装子商品", 1.2m, 3.4m);
        await InsertDomesticProductAsync("P-SET-CHILD", "HB-SET-CHILD", "套装子", "Set Child", 0);

        var service = CreateService();

        var result = await service.ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-2",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-MISSING-EN", "D-SET-CHILD" },
            }
        );

        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(2, result.SkippedCount);
        Assert.Contains(result.Skipped, item => item.DetailHguid == "D-MISSING-EN" && item.ReasonCode == "MISSING_ENGLISH_NAME");
        Assert.Contains(result.Skipped, item => item.DetailHguid == "D-SET-CHILD" && item.ReasonCode == "MISSING_SET_RELATION");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_中文名称为空或纯空白且英文名称有值时用英文名称兜底创建(string? chineseName)
    {
        await InsertContainerDetailAsync("D-NAME-FB", "C-NAME-FB", "P-NAME-FB", "普通商品", 1.2m, 3.4m);
        await InsertDomesticProductAsync("P-NAME-FB", "HB-NAME-FB", chineseName, "Fallback Belt", 0);
        List<CreateItemDto>? capturedItems = null;
        var warehouseService = new Mock<IProductWarehouseReactService>();
        warehouseService
            .Setup(service => service.BatchCreateAsync(
                It.IsAny<List<CreateItemDto>>(),
                false,
                It.IsAny<string?>(),
                "ContainerSubmit",
                "C-NAME-FB",
                It.IsAny<Guid?>(),
                It.IsAny<string?>()
            ))
            .Callback<List<CreateItemDto>, bool, string?, string, string?, Guid?, string?>(
                (items, _, _, _, _, _, _) => capturedItems = items.ToList()
            )
            .ReturnsAsync(new BatchOperationResultDto { Success = true });

        var result = await CreateService(warehouseService: warehouseService.Object).ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-name-fb",
                ContainerGuid = "C-NAME-FB",
                DetailHguids = new List<string> { "D-NAME-FB" },
            }
        );

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Contains(result.Created, item => item.ProductCode == "P-NAME-FB" && item.DetailHguid == "D-NAME-FB");
        // 传给仓库建档的商品名称必须是英文名称兜底值，不能是货号或空串。
        var createItem = Assert.Single(Assert.IsType<List<CreateItemDto>>(capturedItems));
        Assert.Equal("Fallback Belt", createItem.ChineseName);
        Assert.Equal("Fallback Belt", createItem.EnglishName);
        Assert.NotEqual("HB-NAME-FB", createItem.ChineseName);
    }

    [Fact]
    public async Task ExecuteAsync_中文有值时仍以中文名称建档不被英文名称覆盖()
    {
        await InsertContainerDetailAsync("D-NAME-CN", "C-NAME-CN", "P-NAME-CN", "普通商品", 1.2m, 3.4m);
        await InsertDomesticProductAsync("P-NAME-CN", "HB-NAME-CN", "中文皮带", "Chinese Belt", 0);
        List<CreateItemDto>? capturedItems = null;
        var warehouseService = new Mock<IProductWarehouseReactService>();
        warehouseService
            .Setup(service => service.BatchCreateAsync(
                It.IsAny<List<CreateItemDto>>(),
                false,
                It.IsAny<string?>(),
                "ContainerSubmit",
                "C-NAME-CN",
                It.IsAny<Guid?>(),
                It.IsAny<string?>()
            ))
            .Callback<List<CreateItemDto>, bool, string?, string, string?, Guid?, string?>(
                (items, _, _, _, _, _, _) => capturedItems = items.ToList()
            )
            .ReturnsAsync(new BatchOperationResultDto { Success = true });

        var result = await CreateService(warehouseService: warehouseService.Object).ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-name-cn",
                ContainerGuid = "C-NAME-CN",
                DetailHguids = new List<string> { "D-NAME-CN" },
            }
        );

        Assert.Equal(1, result.CreatedCount);
        var createItem = Assert.Single(Assert.IsType<List<CreateItemDto>>(capturedItems));
        Assert.Equal("中文皮带", createItem.ChineseName);
        Assert.Equal("Chinese Belt", createItem.EnglishName);
    }

    [Fact]
    public async Task ExecuteAsync_中文与英文名称均为空时只跳过该行且其他行照常创建()
    {
        await InsertActiveStoreAsync("S001");
        // 正常行、中文空英文有、中文纯空白英文有：都应创建成功。
        await InsertContainerDetailAsync("D-MIX-OK", "C-MIX", "P-MIX-OK", "普通商品", 1.2m, 3.4m);
        await InsertDomesticProductAsync("P-MIX-OK", "HB-MIX-OK", "正常商品", "Normal Product", 0);
        await InsertContainerDetailAsync("D-MIX-FB", "C-MIX", "P-MIX-FB", "普通商品", 1.2m, 3.4m);
        await InsertDomesticProductAsync("P-MIX-FB", "HB-MIX-FB", null, "English Only", 0);
        await InsertContainerDetailAsync("D-MIX-BLANK-CN", "C-MIX", "P-MIX-BLANK-CN", "普通商品", 1.2m, 3.4m);
        await InsertDomesticProductAsync("P-MIX-BLANK-CN", "HB-MIX-BLANK-CN", "   ", "Blank Chinese", 0);
        // 中文、英文都为空（含纯空白）：只跳过这两行，不得拦住整批。
        await InsertContainerDetailAsync("D-MIX-NONE", "C-MIX", "P-MIX-NONE", "普通商品", 1.2m, 3.4m);
        await InsertDomesticProductAsync("P-MIX-NONE", "HB-MIX-NONE", null, null, 0);
        await InsertContainerDetailAsync("D-MIX-BLANK-ALL", "C-MIX", "P-MIX-BLANK-ALL", "普通商品", 1.2m, 3.4m);
        await InsertDomesticProductAsync("P-MIX-BLANK-ALL", "HB-MIX-BLANK-ALL", "  ", "  ", 0);
        // 中文有英文空：沿用原口径，仍按 MISSING_ENGLISH_NAME 跳过。
        await InsertContainerDetailAsync("D-MIX-NO-EN", "C-MIX", "P-MIX-NO-EN", "普通商品", 1.2m, 3.4m);
        await InsertDomesticProductAsync("P-MIX-NO-EN", "HB-MIX-NO-EN", "只有中文", null, 0);

        var result = await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-mix",
                ContainerGuid = "C-MIX",
                DetailHguids = new List<string>
                {
                    "D-MIX-OK",
                    "D-MIX-FB",
                    "D-MIX-BLANK-CN",
                    "D-MIX-NONE",
                    "D-MIX-BLANK-ALL",
                    "D-MIX-NO-EN",
                },
            }
        );

        Assert.Equal(3, result.CreatedCount);
        Assert.Equal(3, result.SkippedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Empty(result.Errors);
        Assert.Contains(result.Created, item => item.DetailHguid == "D-MIX-OK");
        Assert.Contains(result.Created, item => item.DetailHguid == "D-MIX-FB");
        Assert.Contains(result.Created, item => item.DetailHguid == "D-MIX-BLANK-CN");

        // 两个名称都空的行：结果里要带货号与商品编码，原因码沿用 MISSING_CHINESE_NAME，文案说明两个名称均为空。
        const string bothEmptyMessage = "商品名称与英文名称均为空，已跳过";
        Assert.Contains(result.Skipped, item =>
            item.DetailHguid == "D-MIX-NONE"
            && item.ProductCode == "P-MIX-NONE"
            && item.ItemNumber == "HB-MIX-NONE"
            && item.ReasonCode == "MISSING_CHINESE_NAME"
            && item.Message == bothEmptyMessage
        );
        Assert.Contains(result.Skipped, item =>
            item.DetailHguid == "D-MIX-BLANK-ALL"
            && item.ProductCode == "P-MIX-BLANK-ALL"
            && item.ItemNumber == "HB-MIX-BLANK-ALL"
            && item.ReasonCode == "MISSING_CHINESE_NAME"
            && item.Message == bothEmptyMessage
        );
        Assert.Contains(result.Skipped, item =>
            item.DetailHguid == "D-MIX-NO-EN"
            && item.ReasonCode == "MISSING_ENGLISH_NAME"
            && item.Message == "英文名称不能为空"
        );

        // 被跳过的行不得往商品主档、仓库商品、分店价写任何记录（尤其不能拿货号/编码当临时名称）。
        var createdCodes = new[] { "P-MIX-OK", "P-MIX-FB", "P-MIX-BLANK-CN" };
        var products = await _db.Queryable<Product>().ToListAsync();
        Assert.Equal(createdCodes.OrderBy(code => code), products.Select(p => p.ProductCode!).OrderBy(code => code));
        Assert.DoesNotContain(products, p => p.ProductName == "HB-MIX-NONE" || p.ProductName == "HB-MIX-BLANK-ALL");
        Assert.Equal(3, await _db.Queryable<WarehouseProduct>().CountAsync());
        Assert.Equal(3, await _db.Queryable<StoreRetailPrice>().CountAsync());
        // 国内商品源数据不被改写：中文名称仍为空。
        var domesticNone = await _db.Queryable<DomesticProduct>().SingleAsync(p => p.ProductCode == "P-MIX-NONE");
        Assert.Null(domesticNone.ProductName);
        var domesticFallback = await _db.Queryable<DomesticProduct>().SingleAsync(p => p.ProductCode == "P-MIX-FB");
        Assert.Null(domesticFallback.ProductName);
        // 兜底建档的商品主档名称就是英文名称。
        var fallbackProduct = products.Single(p => p.ProductCode == "P-MIX-FB");
        Assert.Equal("English Only", fallbackProduct.ProductName);
        Assert.Equal("English Only", fallbackProduct.EnglishName);
    }

    [Fact]
    public async Task ExecuteAsync_SubmitContainer_中文名称为空时仍按原口径阻止提交不启用英文兜底()
    {
        // 英文兜底只属于「创建新商品」任务；整柜提交的口径不变，名称缺失仍作为阻断错误、货柜不推进完成。
        await InsertContainerAsync("C-SUBMIT-NAME", status: 1);
        await InsertActiveStoreAsync("S001");
        await InsertContainerDetailAsync("D-SUBMIT-NAME", "C-SUBMIT-NAME", "P-SUBMIT-NAME", "普通商品", 1.2m, 3.4m);
        await InsertDomesticProductAsync("P-SUBMIT-NAME", "HB-SUBMIT-NAME", null, "English Only", 0);

        var result = await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "submit-container:C-SUBMIT-NAME",
                ContainerGuid = "C-SUBMIT-NAME",
                SubmitContainer = true,
            }
        );

        Assert.False(result.ContainerCompleted);
        Assert.Contains(result.Errors, item =>
            item.DetailHguid == "D-SUBMIT-NAME"
            && item.ReasonCode == "MISSING_CHINESE_NAME"
            && item.Message == "商品名称不能为空"
        );
        Assert.Null(await _db.Queryable<Product>().FirstAsync(item => item.ProductCode == "P-SUBMIT-NAME"));
        var container = await _db.Queryable<Container>().SingleAsync(item => item.ContainerCode == "C-SUBMIT-NAME");
        Assert.Equal(1, container.Status);
    }

    [Fact]
    public async Task ExecuteAsync_UsesChineseNameWrittenByBatchUpdateDetails()
    {
        await InsertActiveStoreAsync("S001");
        await InsertContainerDetailAsync("D-NAME-SAVED", "C001", "P-NAME-SAVED", "普通商品", 1.2m, 3.4m);
        await InsertDomesticProductAsync("P-NAME-SAVED", "HB-NAME-SAVED", null, "Saved Belt", 0);
        var containerReactService = CreateContainerReactService();
        await containerReactService.BatchUpdateDetailsAsync(
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "D-NAME-SAVED", 商品名称 = "保存后的皮带" },
            }
        );
        var service = CreateService();

        var result = await service.ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-name-saved",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-NAME-SAVED" },
            }
        );

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.DoesNotContain(result.Skipped, item => item.ReasonCode == "MISSING_CHINESE_NAME");
        var domesticProduct = await _db.Queryable<DomesticProduct>().SingleAsync(p => p.ProductCode == "P-NAME-SAVED");
        Assert.Equal("保存后的皮带", domesticProduct.ProductName);
    }

    [Fact]
    public async Task ExecuteAsync_UsesTargetCategoryWrittenByBatchUpdateDetails()
    {
        await InsertActiveStoreAsync("S001");
        await InsertContainerDetailAsync("D-CATEGORY-SAVED", "C001", "P-CATEGORY-SAVED", "普通商品", 1.2m, 3.4m);
        await InsertDomesticProductAsync("P-CATEGORY-SAVED", "HB-CATEGORY-SAVED", "分类商品", "Category Product", 0);
        await InsertWarehouseCategoryAsync("CAT-CREATED");
        var containerReactService = CreateContainerReactService();
        await containerReactService.BatchUpdateDetailsAsync(
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "D-CATEGORY-SAVED", ProductCategoryGUID = "CAT-CREATED" },
            }
        );
        var service = CreateService();

        var result = await service.ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-category-saved",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-CATEGORY-SAVED" },
            }
        );

        Assert.Equal(1, result.CreatedCount);
        var product = await _db.Queryable<Product>().SingleAsync(p => p.ProductCode == "P-CATEGORY-SAVED");
        Assert.Equal("CAT-CREATED", product.WarehouseCategoryGUID);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsRowsWithInvalidRetailPrice()
    {
        await InsertActiveStoreAsync("S001");
        await InsertContainerDetailAsync("D-INVALID-RETAIL", "C001", "P-INVALID-RETAIL", "普通商品", 1.2m, 0m);
        await InsertDomesticProductAsync("P-INVALID-RETAIL", "HB-INVALID-RETAIL", "无零售价商品", "No Retail Price", 0);

        var service = CreateService();

        var result = await service.ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-invalid-retail",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-INVALID-RETAIL" },
            }
        );

        Assert.Equal(0, result.CreatedCount);
        Assert.Contains(result.Skipped, item =>
            item.DetailHguid == "D-INVALID-RETAIL"
            && item.ReasonCode == "INVALID_OEM_PRICE"
            && item.Message == "零售价必须大于 0"
        );
    }

    [Fact]
    public async Task ExecuteAsync_SkipsDuplicateProductCodeAndSetWithoutRelation()
    {
        await _db.Insertable(new Product
        {
            UUID = "LP001",
            ProductCode = "P-EXISTS",
            ItemNumber = "HB-EXISTS",
            ProductName = "已存在",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await InsertContainerDetailAsync("D-EXISTS", "C001", "P-EXISTS", "普通商品", 1.2m, 3.4m);
        await InsertDomesticProductAsync("P-EXISTS", "HB-EXISTS", "已存在", "Exists", 0);
        await InsertContainerDetailAsync("D-SET", "C001", "P-SET", "套装商品", 1.2m, 3.4m);
        await InsertDomesticProductAsync("P-SET", "HB-SET", "套装", "Set Product", 1);

        var service = CreateService();

        var result = await service.ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-3",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-EXISTS", "D-SET" },
            }
        );

        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(2, result.SkippedCount);
        Assert.Contains(result.Skipped, item => item.DetailHguid == "D-EXISTS" && item.ReasonCode == "DUPLICATE_PRODUCT_CODE");
        Assert.Contains(result.Skipped, item => item.DetailHguid == "D-SET" && item.ReasonCode == "MISSING_SET_RELATION");
    }

    [Fact]
    public async Task ExecuteAsync_SkipsRowsWithDuplicateLocalItemNumber()
    {
        await _db.Insertable(new Product
        {
            UUID = "LP-DUP-ITEM",
            ProductCode = "P-OTHER",
            ItemNumber = "HB-DUP",
            ProductName = "同货号商品",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await InsertContainerDetailAsync("D-DUP-ITEM", "C001", "P-DUP-ITEM", "普通商品", 1.2m, 3.4m);
        await InsertDomesticProductAsync("P-DUP-ITEM", "HB-DUP", "重复货号", "Duplicate Item", 0);

        var service = CreateService();

        var result = await service.ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-dup-item",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D-DUP-ITEM" },
            }
        );

        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Contains(result.Skipped, item => item.DetailHguid == "D-DUP-ITEM" && item.ReasonCode == "DUPLICATE_ITEM_NUMBER");
        Assert.Equal(0, await _db.Queryable<WarehouseProduct>().Where(p => p.ProductCode == "P-DUP-ITEM").CountAsync());
    }

    [Fact]
    public async Task ExecuteAsync_SubmitContainer_CreatesNewProductsUpdatesExistingPricesAndCompletesContainer()
    {
        await InsertContainerAsync("C-SUBMIT", status: 1);
        await InsertActiveStoreAsync("S001");
        await InsertContainerDetailAsync("D-NEW", "C-SUBMIT", "P-NEW", "普通商品", 1.2m, 3.4m, domesticPrice: 9.9m);
        await InsertDomesticProductAsync("P-NEW", "HB-NEW", "新商品", "New Product", 0);
        await InsertContainerDetailAsync("D-EXISTING", "C-SUBMIT", "P-EXISTING", "普通商品", 5.6m, 7.8m, domesticPrice: 11.2m);
        await InsertDomesticProductAsync("P-EXISTING", "HB-EXISTING", "已有商品来源", "Existing Source", 0);
        await InsertExistingProductAsync("P-EXISTING", "HB-EXISTING", 1.1m, 2.2m);
        await InsertExistingWarehouseProductAsync("P-EXISTING", domesticPrice: 3.3m, importPrice: 4.4m, oemPrice: 5.5m);

        var result = await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "submit-container:C-SUBMIT",
                ContainerGuid = "C-SUBMIT",
                SubmitContainer = true,
            }
        );

        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.True(result.ContainerCompleted);
        Assert.Contains(result.Created, item => item.ProductCode == "P-NEW" && item.DetailHguid == "D-NEW");
        Assert.Contains(result.Updated, item => item.ProductCode == "P-EXISTING" && item.DetailHguid == "D-EXISTING");

        var container = await _db.Queryable<Container>().SingleAsync(item => item.ContainerCode == "C-SUBMIT");
        Assert.Equal(2, container.Status);

        var existingProduct = await _db.Queryable<Product>().SingleAsync(item => item.ProductCode == "P-EXISTING");
        var existingWarehouseProduct = await _db.Queryable<WarehouseProduct>().SingleAsync(item => item.ProductCode == "P-EXISTING");
        var storeRetailPrice = await _db.Queryable<StoreRetailPrice>().SingleAsync(item => item.ProductCode == "P-EXISTING");

        Assert.Equal(5.6m, existingProduct.PurchasePrice);
        Assert.Equal(11.2m, existingWarehouseProduct.DomesticPrice);
        Assert.Equal(5.6m, existingWarehouseProduct.ImportPrice);
        Assert.Equal(7.8m, existingWarehouseProduct.OEMPrice);
        Assert.Equal(0.12m, existingWarehouseProduct.Volume);
        Assert.Equal(5.6m, storeRetailPrice.PurchasePrice);
        Assert.Equal(7.8m, storeRetailPrice.StoreRetailPriceValue);
        // 普通商品没有完整子项关系时，只更新主价投影，不能生成无子项编码的 StoreMulti 占位行。
        Assert.Empty(await _db.Queryable<StoreMultiCodeProduct>()
            .Where(item => item.ProductCode == "P-EXISTING")
            .ToListAsync());
    }

    [Fact]
    public async Task ExecuteAsync_SubmitContainer_普通商品不得随机改写既有StoreMulti子项()
    {
        await InsertContainerAsync("C-NO-RANDOM-STORE-MULTI", status: 1);
        await InsertActiveStoreAsync("S001");
        await InsertContainerDetailAsync(
            "D-NO-RANDOM-STORE-MULTI",
            "C-NO-RANDOM-STORE-MULTI",
            "P-NO-RANDOM-STORE-MULTI",
            "普通商品",
            8.8m,
            9.9m
        );
        await InsertDomesticProductAsync(
            "P-NO-RANDOM-STORE-MULTI",
            "HB-NO-RANDOM-STORE-MULTI",
            "普通商品",
            "Normal Product",
            0
        );
        await InsertExistingProductAsync(
            "P-NO-RANDOM-STORE-MULTI",
            "HB-NO-RANDOM-STORE-MULTI",
            1.1m,
            2.2m
        );
        await InsertExistingWarehouseProductAsync(
            "P-NO-RANDOM-STORE-MULTI",
            domesticPrice: 3.3m,
            importPrice: 4.4m,
            oemPrice: 5.5m
        );
        await InsertStoreMultiCodeAsync(
            "S001",
            "P-NO-RANDOM-STORE-MULTI",
            "CHILD-A",
            "BAR-A",
            purchasePrice: 1.2m,
            retailPrice: 2.3m
        );
        await InsertStoreMultiCodeAsync(
            "S001",
            "P-NO-RANDOM-STORE-MULTI",
            "CHILD-B",
            "BAR-B",
            purchasePrice: 3.4m,
            retailPrice: 4.5m
        );

        var result = await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "submit-container:C-NO-RANDOM-STORE-MULTI",
                ContainerGuid = "C-NO-RANDOM-STORE-MULTI",
                SubmitContainer = true,
            }
        );

        var storeMultiCodes = await _db.Queryable<StoreMultiCodeProduct>()
            .Where(item => item.ProductCode == "P-NO-RANDOM-STORE-MULTI")
            .OrderBy(item => item.MultiCodeProductCode)
            .ToListAsync();

        Assert.Equal(0, result.FailedCount);
        Assert.Equal(2, storeMultiCodes.Count);
        Assert.Equal(new decimal?[] { 1.2m, 3.4m }, storeMultiCodes.Select(item => item.PurchasePrice));
        Assert.Equal(new decimal?[] { 2.3m, 4.5m }, storeMultiCodes.Select(item => item.MultiCodeRetailPrice));
    }

    [Fact]
    public async Task ExecuteAsync_SubmitContainer_UpdatesOnlyPricesForExistingProducts()
    {
        await InsertContainerAsync("C-PRICE-ONLY", status: 1);
        await InsertActiveStoreAsync("S001");
        await InsertContainerDetailAsync("D-PRICE-ONLY", "C-PRICE-ONLY", "P-PRICE-ONLY", "普通商品", 8.8m, 9.9m, domesticPrice: 10.1m);
        await InsertDomesticProductAsync("P-PRICE-ONLY", "HB-PRICE-ONLY", "国内名称", "Domestic Name", 0);
        await InsertExistingProductAsync("P-PRICE-ONLY", "HB-PRICE-ONLY", 1.1m, 2.2m);
        await InsertExistingWarehouseProductAsync("P-PRICE-ONLY", domesticPrice: 3.3m, importPrice: 4.4m, oemPrice: 5.5m);
        await _db.Updateable<Product>()
            .SetColumns(item => new Product
            {
                ProductName = "保留名称",
                EnglishName = "Keep Name",
                ProductType = 0,
                WarehouseCategoryGUID = "KEEP-CAT",
                IsActive = false,
            })
            .Where(item => item.ProductCode == "P-PRICE-ONLY")
            .ExecuteCommandAsync();
        await _db.Updateable<WarehouseProduct>()
            .SetColumns(item => new WarehouseProduct
            {
                IsActive = false,
            })
            .Where(item => item.ProductCode == "P-PRICE-ONLY")
            .ExecuteCommandAsync();

        var result = await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "submit-container:C-PRICE-ONLY",
                ContainerGuid = "C-PRICE-ONLY",
                SubmitContainer = true,
            },
            "整柜提交人"
        );

        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.True(result.ContainerCompleted);

        var product = await _db.Queryable<Product>().SingleAsync(item => item.ProductCode == "P-PRICE-ONLY");
        var warehouseProduct = await _db.Queryable<WarehouseProduct>().SingleAsync(item => item.ProductCode == "P-PRICE-ONLY");

        Assert.Equal(8.8m, product.PurchasePrice);
        Assert.Equal("保留名称", product.ProductName);
        Assert.Equal("Keep Name", product.EnglishName);
        Assert.Equal("KEEP-CAT", product.WarehouseCategoryGUID);
        Assert.False(product.IsActive);
        Assert.Equal(10.1m, warehouseProduct.DomesticPrice);
        Assert.Equal(8.8m, warehouseProduct.ImportPrice);
        Assert.Equal(9.9m, warehouseProduct.OEMPrice);
        Assert.False(warehouseProduct.IsActive);
        Assert.Equal("整柜提交人", warehouseProduct.UpdatedBy);
    }

    [Fact]
    public async Task ExecuteAsync_SubmitContainer_名称缺失但有入队Guid时保留用户型审计身份()
    {
        await InsertContainerAsync("C-ACTOR-GUID", status: 1);
        await InsertActiveStoreAsync("S001");
        await InsertContainerDetailAsync(
            "D-ACTOR-GUID",
            "C-ACTOR-GUID",
            "P-ACTOR-GUID",
            "普通商品",
            8.8m,
            9.9m,
            domesticPrice: 10.1m
        );
        await InsertDomesticProductAsync("P-ACTOR-GUID", "HB-ACTOR-GUID", "国内名称", "Domestic Name", 0);
        await InsertExistingProductAsync("P-ACTOR-GUID", "HB-ACTOR-GUID", 1.1m, 2.2m);
        await InsertExistingWarehouseProductAsync(
            "P-ACTOR-GUID",
            domesticPrice: 3.3m,
            importPrice: 4.4m,
            oemPrice: 5.5m
        );

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
            .ReturnsAsync(1);

        var result = await CreateService(historyService.Object).ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "submit-container:C-ACTOR-GUID",
                ContainerGuid = "C-ACTOR-GUID",
                SubmitContainer = true,
            },
            "queued-user-guid",
            null,
            CancellationToken.None
        );

        Assert.Equal(0, result.FailedCount);
        Assert.NotNull(capturedContext);
        Assert.Equal("queued-user-guid", capturedContext!.ActorUserGuid);
        Assert.Equal("User", capturedContext.ActorType);
    }

    [Fact]
    public async Task ExecuteAsync_SubmitContainer_DoesNotCompleteContainerWhenFailureExists()
    {
        await InsertContainerAsync("C-FAIL", status: 1);
        await InsertContainerDetailAsync("D-FAIL", "C-FAIL", "P-FAIL", "普通商品", 1.2m, 3.4m);

        var result = await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "submit-container:C-FAIL",
                ContainerGuid = "C-FAIL",
                SubmitContainer = true,
            }
        );

        Assert.False(result.ContainerCompleted);
        Assert.True(result.FailedCount > 0);
        var container = await _db.Queryable<Container>().SingleAsync(item => item.ContainerCode == "C-FAIL");
        Assert.Equal(1, container.Status);
    }

    [Fact]
    public async Task ExecuteAsync_SubmitContainer_RollsBackCreatedProductsWhenLaterUpdateFails()
    {
        await InsertContainerAsync("C-ROLLBACK", status: 1);
        await InsertActiveStoreAsync("S001");
        await InsertContainerDetailAsync("D-ROLLBACK-NEW", "C-ROLLBACK", "P-ROLLBACK-NEW", "普通商品", 1.2m, 3.4m);
        await InsertDomesticProductAsync("P-ROLLBACK-NEW", "HB-ROLLBACK-NEW", "回滚新商品", "Rollback Product", 0);
        await InsertContainerDetailAsync("D-ROLLBACK-EXISTING", "C-ROLLBACK", "P-ROLLBACK-EXISTING", "普通商品", 5.6m, 7.8m);
        await InsertDomesticProductAsync("P-ROLLBACK-EXISTING", "HB-ROLLBACK-EXISTING", "已有商品来源", "Existing Source", 0);
        await InsertExistingProductAsync("P-ROLLBACK-EXISTING", "HB-ROLLBACK-EXISTING", 1.1m, 2.2m);

        var result = await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "submit-container:C-ROLLBACK",
                ContainerGuid = "C-ROLLBACK",
                SubmitContainer = true,
            }
        );

        Assert.False(result.ContainerCompleted);
        Assert.Contains(result.Errors, item => item.ReasonCode == "WAREHOUSE_PRODUCT_NOT_FOUND");
        // 整柜提交是原子动作：已有商品更新失败时，前面创建的新商品和分店价格也必须回滚。
        Assert.Null(await _db.Queryable<Product>().FirstAsync(item => item.ProductCode == "P-ROLLBACK-NEW"));
        Assert.Null(await _db.Queryable<WarehouseProduct>().FirstAsync(item => item.ProductCode == "P-ROLLBACK-NEW"));
        Assert.Equal(0, await _db.Queryable<StoreRetailPrice>().Where(item => item.ProductCode == "P-ROLLBACK-NEW").CountAsync());
        // 结果计数必须与回滚后的数据一致，不能仍显示「创建 1」让用户误以为部分成功。
        Assert.Equal(0, result.CreatedCount);
        Assert.Empty(result.Created);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Empty(result.Updated);
        var container = await _db.Queryable<Container>().SingleAsync(item => item.ContainerCode == "C-ROLLBACK");
        Assert.Equal(1, container.Status);
    }

    [Fact]
    public async Task ExecuteAsync_SubmitContainer_LoadsOnlyCurrentContainerDetails()
    {
        await InsertContainerAsync("C-SCOPE", status: 1);
        await InsertContainerAsync("C-OTHER", status: 1);
        await InsertActiveStoreAsync("S001");
        await InsertContainerDetailAsync("D-SCOPE", "C-SCOPE", "P-SCOPE", "普通商品", 1.2m, 3.4m);
        await InsertDomesticProductAsync("P-SCOPE", "HB-SCOPE", "范围商品", "Scope Product", 0);
        await InsertContainerDetailAsync("D-OTHER", "C-OTHER", "P-OTHER", "普通商品", 2.2m, 4.4m);
        await InsertDomesticProductAsync("P-OTHER", "HB-OTHER", "其他商品", "Other Product", 0);

        var result = await CreateService().ExecuteAsync(
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "submit-container:C-SCOPE",
                ContainerGuid = "C-SCOPE",
                SubmitContainer = true,
            }
        );

        Assert.Equal(1, result.CreatedCount);
        Assert.Contains(result.Created, item => item.ProductCode == "P-SCOPE");
        Assert.DoesNotContain(result.Created, item => item.ProductCode == "P-OTHER");
        Assert.NotNull(await _db.Queryable<Product>().FirstAsync(item => item.ProductCode == "P-SCOPE"));
        Assert.Null(await _db.Queryable<Product>().FirstAsync(item => item.ProductCode == "P-OTHER"));
        var currentContainer = await _db.Queryable<Container>().SingleAsync(item => item.ContainerCode == "C-SCOPE");
        var otherContainer = await _db.Queryable<Container>().SingleAsync(item => item.ContainerCode == "C-OTHER");
        Assert.Equal(2, currentContainer.Status);
        Assert.Equal(1, otherContainer.Status);
    }

    [Fact]
    public async Task JobService_ReusesSameOperationContainerAndDetails()
    {
        var executor = new BlockingContainerProductCreationExecutor();
        var services = new ServiceCollection();
        services.AddSingleton<IContainerProductCreationExecutorService>(executor);
        var provider = services.BuildServiceProvider();
        var jobService = new ContainerProductCreationJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ContainerProductCreationJobService>.Instance
        );
        var request = new ContainerProductCreationJobRequestDto
        {
            OperationId = "op-reuse",
            ContainerGuid = "C001",
            DetailHguids = new List<string> { "D002", "D001" },
        };

        var first = await jobService.StartJobAsync("user-1", request);
        var second = await jobService.StartJobAsync(
            "user-1",
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-reuse",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D001", "D002" },
            }
        );
        executor.Release();
        await Task.Delay(50);

        Assert.Equal(first.JobId, second.JobId);
        Assert.True(second.IsDuplicateRequest);
    }

    [Fact]
    public async Task JobService_ReusesSameContainerAndDetailsWhenOperationIdChanges()
    {
        var executor = new BlockingContainerProductCreationExecutor();
        var services = new ServiceCollection();
        services.AddSingleton<IContainerProductCreationExecutorService>(executor);
        var provider = services.BuildServiceProvider();
        var jobService = new ContainerProductCreationJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ContainerProductCreationJobService>.Instance
        );

        var first = await jobService.StartJobAsync(
            "user-1",
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-reuse-a",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D002", "D001" },
            }
        );
        var second = await jobService.StartJobAsync(
            "user-1",
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-reuse-b",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D001", "D002" },
            }
        );
        executor.Release();
        await Task.Delay(50);

        Assert.Equal(first.JobId, second.JobId);
        Assert.True(second.IsDuplicateRequest);
    }

    [Fact]
    public async Task JobService_BlocksCrossUserJobResultAccess()
    {
        var executor = new BlockingContainerProductCreationExecutor();
        var services = new ServiceCollection();
        services.AddSingleton<IContainerProductCreationExecutorService>(executor);
        var provider = services.BuildServiceProvider();
        var jobService = new ContainerProductCreationJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ContainerProductCreationJobService>.Instance
        );

        var job = await jobService.StartJobAsync(
            "user-1",
            new ContainerProductCreationJobRequestDto
            {
                OperationId = "op-user-1",
                ContainerGuid = "C001",
                DetailHguids = new List<string> { "D001" },
            }
        );

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => jobService.GetJobAsync("user-2", job.JobId)
        );
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => jobService.StartJobAsync(
                "user-2",
                new ContainerProductCreationJobRequestDto
                {
                    OperationId = "op-user-2",
                    ContainerGuid = "C001",
                    DetailHguids = new List<string> { "D001" },
                }
            )
        );

        executor.Release();
        await Task.Delay(50);
    }

    [Fact]
    public async Task JobService_失败任务完成后同一操作再次提交应重新执行()
    {
        // 第一次执行失败（如缺英文名），用户修正数据后再次提交必须真的重跑，不能返回缓存的失败结果。
        var executor = new SequencedContainerProductCreationExecutor(failedCountByCall: new[] { 1, 0 });
        var jobService = CreateJobService(executor);
        var request = new ContainerProductCreationJobRequestDto { OperationId = "op-retry", ContainerGuid = "C001", SubmitContainer = true };

        var first = await jobService.StartJobAsync("user-1", request);
        var firstDone = await WaitForJobAsync(jobService, "user-1", first.JobId);
        Assert.Equal(ContainerProductCreationJobStatusConstants.Failed, firstDone.Status);

        var second = await jobService.StartJobAsync("user-1", request);
        var secondDone = await WaitForJobAsync(jobService, "user-1", second.JobId);

        Assert.NotEqual(first.JobId, second.JobId);
        Assert.False(second.IsDuplicateRequest);
        Assert.Equal(ContainerProductCreationJobStatusConstants.Succeeded, secondDone.Status);
        Assert.Equal(2, executor.CallCount);
    }

    [Fact]
    public async Task JobService_成功任务在保留期内再次提交仍复用结果()
    {
        var executor = new SequencedContainerProductCreationExecutor(failedCountByCall: new[] { 0, 0 });
        var jobService = CreateJobService(executor);
        var request = new ContainerProductCreationJobRequestDto { OperationId = "op-done", ContainerGuid = "C001", SubmitContainer = true };

        var first = await jobService.StartJobAsync("user-1", request);
        await WaitForJobAsync(jobService, "user-1", first.JobId);
        var second = await jobService.StartJobAsync("user-1", request);

        Assert.Equal(first.JobId, second.JobId);
        Assert.True(second.IsDuplicateRequest);
        Assert.Equal(1, executor.CallCount);
    }

    [Fact]
    public async Task JobService_创建新商品任务成功但有跳过明细时再次提交应重新执行()
    {
        // 第一次执行成功但有跳过明细（如商品名称与英文名称均为空）：用户补全数据后再点「创建新商品」，
        // 同一货柜 + 同一组明细在保留期内必须真的重跑，不能拿到旧的“已跳过”结果。
        var executor = new SequencedContainerProductCreationExecutor(
            failedCountByCall: new[] { 0, 0, 0 },
            skippedCountByCall: new[] { 1, 0, 0 }
        );
        var jobService = CreateJobService(executor);
        var request = new ContainerProductCreationJobRequestDto
        {
            OperationId = "op-skipped",
            ContainerGuid = "C001",
            DetailHguids = new List<string> { "D-1", "D-2" },
            SubmitContainer = false,
        };

        var first = await jobService.StartJobAsync("user-1", request);
        var firstDone = await WaitForJobAsync(jobService, "user-1", first.JobId);
        Assert.Equal(ContainerProductCreationJobStatusConstants.Succeeded, firstDone.Status);
        Assert.Equal(1, firstDone.Result.SkippedCount);

        var second = await jobService.StartJobAsync("user-1", request);
        var secondDone = await WaitForJobAsync(jobService, "user-1", second.JobId);

        Assert.NotEqual(first.JobId, second.JobId);
        Assert.False(second.IsDuplicateRequest);
        Assert.Equal(ContainerProductCreationJobStatusConstants.Succeeded, secondDone.Status);
        Assert.Equal(0, secondDone.Result.SkippedCount);
        Assert.Equal(2, executor.CallCount);

        // 第二次已无跳过明细：之后再次提交回到“成功任务复用”的原口径，不再重复执行。
        var third = await jobService.StartJobAsync("user-1", request);
        Assert.Equal(second.JobId, third.JobId);
        Assert.True(third.IsDuplicateRequest);
        Assert.Equal(2, executor.CallCount);
    }

    [Fact]
    public async Task JobService_创建新商品任务成功且无跳过明细时再次提交仍复用结果()
    {
        var executor = new SequencedContainerProductCreationExecutor(
            failedCountByCall: new[] { 0, 0 },
            skippedCountByCall: new[] { 0, 0 }
        );
        var jobService = CreateJobService(executor);
        var request = new ContainerProductCreationJobRequestDto
        {
            OperationId = "op-no-skip",
            ContainerGuid = "C001",
            DetailHguids = new List<string> { "D-1" },
            SubmitContainer = false,
        };

        var first = await jobService.StartJobAsync("user-1", request);
        await WaitForJobAsync(jobService, "user-1", first.JobId);
        var second = await jobService.StartJobAsync("user-1", request);

        Assert.Equal(first.JobId, second.JobId);
        Assert.True(second.IsDuplicateRequest);
        Assert.Equal(1, executor.CallCount);
    }

    [Fact]
    public async Task JobService_整柜提交成功即使有跳过明细再次提交仍复用结果()
    {
        // 整柜提交可能已推进货柜状态，重跑有风险，所以“有跳过明细不复用”的规则只适用于创建新商品任务。
        var executor = new SequencedContainerProductCreationExecutor(
            failedCountByCall: new[] { 0, 0 },
            skippedCountByCall: new[] { 1, 0 }
        );
        var jobService = CreateJobService(executor);
        var request = new ContainerProductCreationJobRequestDto
        {
            OperationId = "op-submit-skipped",
            ContainerGuid = "C001",
            SubmitContainer = true,
        };

        var first = await jobService.StartJobAsync("user-1", request);
        var firstDone = await WaitForJobAsync(jobService, "user-1", first.JobId);
        Assert.Equal(ContainerProductCreationJobStatusConstants.Succeeded, firstDone.Status);
        Assert.Equal(1, firstDone.Result.SkippedCount);

        var second = await jobService.StartJobAsync("user-1", request);

        Assert.Equal(first.JobId, second.JobId);
        Assert.True(second.IsDuplicateRequest);
        Assert.Equal(1, executor.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_补全名称后重跑同一组明细时已建档行按已存在跳过且不重复建档()
    {
        // 重跑安全性证据：第一次有一行名称全空被跳过；用户补全名称后用同一组明细再执行一次，
        // 已建档的行必须按 DUPLICATE_PRODUCT_CODE 跳过，只补建刚修正的行，不产生重复数据。
        await InsertActiveStoreAsync("S001");
        await InsertContainerDetailAsync("D-RERUN-OK", "C-RERUN", "P-RERUN-OK", "普通商品", 1.2m, 3.4m);
        await InsertDomesticProductAsync("P-RERUN-OK", "HB-RERUN-OK", "正常商品", "Normal Product", 0);
        await InsertContainerDetailAsync("D-RERUN-FIX", "C-RERUN", "P-RERUN-FIX", "普通商品", 1.2m, 3.4m);
        await InsertDomesticProductAsync("P-RERUN-FIX", "HB-RERUN-FIX", null, null, 0);
        var request = new ContainerProductCreationJobRequestDto
        {
            OperationId = "op-rerun",
            ContainerGuid = "C-RERUN",
            DetailHguids = new List<string> { "D-RERUN-OK", "D-RERUN-FIX" },
        };

        var first = await CreateService().ExecuteAsync(request);

        Assert.Equal(1, first.CreatedCount);
        Assert.Equal(1, first.SkippedCount);
        Assert.Contains(first.Skipped, item =>
            item.DetailHguid == "D-RERUN-FIX" && item.ReasonCode == "MISSING_CHINESE_NAME"
        );
        Assert.Equal(1, await _db.Queryable<Product>().CountAsync());

        // 用户补全了英文名称（中文名称仍为空，创建时用英文名称兜底）。
        await _db.Updateable<DomesticProduct>()
            .SetColumns(p => p.EnglishProductName == "Fixed English Name")
            .Where(p => p.ProductCode == "P-RERUN-FIX")
            .ExecuteCommandAsync();

        var second = await CreateService().ExecuteAsync(request);

        Assert.Equal(1, second.CreatedCount);
        Assert.Equal(1, second.SkippedCount);
        Assert.Equal(0, second.FailedCount);
        Assert.Contains(second.Created, item => item.DetailHguid == "D-RERUN-FIX");
        Assert.Contains(second.Skipped, item =>
            item.DetailHguid == "D-RERUN-OK" && item.ReasonCode == "DUPLICATE_PRODUCT_CODE"
        );
        // 每个商品编码在三张表里各只有一行，没有重复建档或写坏。
        Assert.Equal(2, await _db.Queryable<Product>().CountAsync());
        Assert.Equal(1, await _db.Queryable<Product>().Where(p => p.ProductCode == "P-RERUN-OK").CountAsync());
        Assert.Equal(2, await _db.Queryable<WarehouseProduct>().CountAsync());
        Assert.Equal(2, await _db.Queryable<StoreRetailPrice>().CountAsync());
    }

    private static ContainerProductCreationJobService CreateJobService(IContainerProductCreationExecutorService executor)
    {
        var services = new ServiceCollection();
        services.AddSingleton(executor);
        var provider = services.BuildServiceProvider();
        return new ContainerProductCreationJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ContainerProductCreationJobService>.Instance
        );
    }

    private static async Task<ContainerProductCreationJobDto> WaitForJobAsync(
        ContainerProductCreationJobService jobService,
        string userId,
        string jobId
    )
    {
        for (var i = 0; i < 200; i++)
        {
            var job = await jobService.GetJobAsync(userId, jobId);
            if (job != null
                && job.Status != ContainerProductCreationJobStatusConstants.Queued
                && job.Status != ContainerProductCreationJobStatusConstants.Running)
            {
                return job;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"任务 {jobId} 未在预期时间内完成");
    }

    private sealed class SequencedContainerProductCreationExecutor : IContainerProductCreationExecutorService
    {
        private readonly int[] _failedCountByCall;
        private readonly int[] _skippedCountByCall;
        private int _callCount;

        // skippedCountByCall 为空时每次调用都不带跳过明细（沿用原有用例的行为）。
        public SequencedContainerProductCreationExecutor(int[] failedCountByCall, int[]? skippedCountByCall = null)
        {
            _failedCountByCall = failedCountByCall;
            _skippedCountByCall = skippedCountByCall ?? new[] { 0 };
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<ContainerProductCreationResultDto> ExecuteAsync(
            ContainerProductCreationJobRequestDto request,
            CancellationToken cancellationToken = default
        ) => ExecuteAsync(request, null, null, cancellationToken);

        public Task<ContainerProductCreationResultDto> ExecuteAsync(
            ContainerProductCreationJobRequestDto request,
            string? updatedBy,
            CancellationToken cancellationToken = default
        ) => ExecuteAsync(request, null, updatedBy, cancellationToken);

        public Task<ContainerProductCreationResultDto> ExecuteAsync(
            ContainerProductCreationJobRequestDto request,
            string? actorUserGuid,
            string? updatedBy,
            CancellationToken cancellationToken = default
        )
        {
            var call = Interlocked.Increment(ref _callCount) - 1;
            var failed = _failedCountByCall[Math.Min(call, _failedCountByCall.Length - 1)];
            var skipped = _skippedCountByCall[Math.Min(call, _skippedCountByCall.Length - 1)];
            var result = new ContainerProductCreationResultDto
            {
                FailedCount = failed,
                SkippedCount = skipped,
            };
            for (var i = 0; i < skipped; i++)
            {
                result.Skipped.Add(new ContainerProductCreationResultItemDto
                {
                    DetailHguid = $"D-SKIPPED-{i + 1}",
                    ReasonCode = "MISSING_CHINESE_NAME",
                    Message = "商品名称与英文名称均为空，已跳过",
                });
            }

            return Task.FromResult(result);
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        _sqliteConnection.Dispose();
        _hbSalesDb.Dispose();
        _hbSalesSqliteConnection.Dispose();
        if (File.Exists(_dbPath))
        {
            SqliteTempFileCleanup.DeleteIfExists(_dbPath);
        }
        if (File.Exists(_hbSalesDbPath))
        {
            SqliteTempFileCleanup.DeleteIfExists(_hbSalesDbPath);
        }
    }

    private async Task InsertActiveStoreAsync(string storeCode)
    {
        await _db.Insertable(new Store
        {
            StoreCode = storeCode,
            StoreName = storeCode,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task InsertStoreAsync(string storeCode, bool isActive, bool isDeleted)
    {
        await _db.Insertable(new Store
        {
            StoreCode = storeCode,
            StoreName = storeCode,
            IsActive = isActive,
            IsDeleted = isDeleted,
        }).ExecuteCommandAsync();
    }

    /// <summary>
    /// 造一个“已经创建成功”的两子项套装：A 为正常子项，B 模拟线上的垃圾关系（关系价可为空）。
    /// 已有 ProductSetCode / 各启用门店多码行的零售价为：A=20、B=50（B 的 50 即首次创建时的父商品兜底价），
    /// 成本已按零售价占比分摊（A=4、B=6，合计 10），与父商品进货价 10 一致。
    /// </summary>
    private async Task SeedExistingSetWithTwoChildrenAsync(
        string productCode,
        string containerCode,
        string detailCode,
        decimal? relationPriceA,
        decimal? relationPriceB,
        string[] activeStoreCodes,
        bool insertContainer = false
    )
    {
        if (insertContainer)
        {
            await InsertContainerAsync(containerCode, status: 1);
        }
        foreach (var storeCode in activeStoreCodes)
        {
            await InsertActiveStoreAsync(storeCode);
        }
        await InsertExistingProductAsync(productCode, $"HB-{productCode}", 10m, 50m, productType: 1);
        await InsertExistingWarehouseProductAsync(productCode, 1.1m, 10m, 50m);
        await InsertContainerDetailAsync(detailCode, containerCode, productCode, "套装商品", 10m, 50m);
        await InsertDomesticProductAsync(productCode, $"HB-{productCode}", "套装商品", "Set Product", 1);

        var children = new[]
        {
            (Code: $"{productCode}-A", Retail: 20m, Purchase: 4m, RelationPrice: relationPriceA),
            (Code: $"{productCode}-B", Retail: 50m, Purchase: 6m, RelationPrice: relationPriceB),
        };
        foreach (var child in children)
        {
            var itemNumber = $"ITEM-{child.Code}";
            await InsertDomesticSetProductAsync(productCode, child.Code, itemNumber, oemPrice: child.RelationPrice);
            await InsertProductSetCodeAsync(
                productCode,
                child.Code,
                itemNumber,
                purchasePrice: child.Purchase,
                retailPrice: child.Retail
            );
            foreach (var storeCode in activeStoreCodes)
            {
                await InsertStoreMultiCodeAsync(
                    storeCode,
                    productCode,
                    child.Code,
                    $"BAR-{itemNumber}",
                    purchasePrice: child.Purchase,
                    retailPrice: child.Retail
                );
            }
        }
    }

    private async Task InsertContainerAsync(string containerCode, int status)
    {
        await _db.Insertable(new Container
        {
            ContainerCode = containerCode,
            ContainerNumber = containerCode,
            Status = status,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task InsertContainerDetailAsync(
        string detailCode,
        string containerCode,
        string productCode,
        string productType,
        decimal importPrice,
        decimal oemPrice,
        decimal domesticPrice = 10m,
        string? mixedGroupCode = null,
        decimal? setQuantity = null
    )
    {
        await _db.Insertable(new ContainerDetail
        {
            DetailCode = detailCode,
            ContainerCode = containerCode,
            ProductCode = productCode,
            ProductType = productType,
            DomesticPrice = domesticPrice,
            ImportPrice = importPrice,
            OEMPrice = oemPrice,
            MixedGroupCode = mixedGroupCode,
            SetQuantity = setQuantity,
            UnitVolume = 0.12m,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task InsertDomesticProductAsync(
        string productCode,
        string itemNumber,
        string? productName,
        string? englishName,
        int productType
    )
    {
        await _db.Insertable(new DomesticProduct
        {
            ProductCode = productCode,
            HBProductNo = itemNumber,
            ProductName = productName,
            EnglishProductName = englishName,
            Barcode = $"BAR-{itemNumber}",
            ProductType = productType,
            ProductImage = $"/{itemNumber}.jpg",
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task InsertDomesticSetProductAsync(
        string productCode,
        string setProductCode,
        string setProductNo,
        decimal importPrice = 2.2m,
        decimal? oemPrice = 5.5m
    )
    {
        await _db.Insertable(new DomesticSetProduct
        {
            SetProductCode = setProductCode,
            ProductCode = productCode,
            ProductNo = productCode,
            SetProductNo = setProductNo,
            SetBarcode = $"BAR-{setProductNo}",
            ImportPrice = importPrice,
            OEMPrice = oemPrice,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task InsertHqDomesticSetProductAsync(
        string hguid,
        string productCode,
        string setProductNo,
        string setBarcode,
        decimal domesticPrice,
        decimal importPrice,
        decimal oemPrice
    )
    {
        await _hbSalesDb.Insertable(new CPT_DIC_商品套装信息表
        {
            HGUID = hguid,
            商品编码 = productCode,
            商品小货号 = setProductNo,
            条形码 = setBarcode,
            国内价格 = domesticPrice,
            进口价格 = importPrice,
            贴牌价格 = oemPrice,
            使用状态 = 1,
        }).ExecuteCommandAsync();
    }

    private async Task InsertExistingProductAsync(
        string productCode,
        string itemNumber,
        decimal purchasePrice,
        decimal retailPrice,
        int? productType = null
    )
    {
        await _db.Insertable(new Product
        {
            UUID = $"LP-{productCode}",
            ProductCode = productCode,
            ItemNumber = itemNumber,
            ProductName = itemNumber,
            EnglishName = itemNumber,
            ProductType = productType,
            PurchasePrice = purchasePrice,
            RetailPrice = retailPrice,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task InsertExistingWarehouseProductAsync(
        string productCode,
        decimal domesticPrice,
        decimal importPrice,
        decimal oemPrice
    )
    {
        await _db.Insertable(new WarehouseProduct
        {
            ProductCode = productCode,
            DomesticPrice = domesticPrice,
            ImportPrice = importPrice,
            OEMPrice = oemPrice,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task InsertProductSetCodeAsync(
        string productCode,
        string setProductCode,
        string setItemNumber,
        decimal purchasePrice = 2.2m,
        decimal? retailPrice = 5.5m
    )
    {
        await _db.Insertable(new ProductSetCode
        {
            SetCodeId = setProductCode,
            ProductCode = productCode,
            SetProductCode = setProductCode,
            SetItemNumber = setItemNumber,
            SetBarcode = $"BAR-{setItemNumber}",
            SetPurchasePrice = purchasePrice,
            SetRetailPrice = retailPrice,
            SetQuantity = 1,
            SetType = 1,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task InsertStoreMultiCodeAsync(
        string storeCode,
        string productCode,
        string multiCodeProductCode,
        string multiBarcode,
        decimal? purchasePrice = 2.2m,
        decimal? retailPrice = 5.5m
    )
    {
        await _db.Insertable(new StoreMultiCodeProduct
        {
            UUID = $"{storeCode}-{multiCodeProductCode}",
            StoreCode = storeCode,
            ProductCode = productCode,
            MultiCodeProductCode = multiCodeProductCode,
            StoreMultiCodeProductCode = storeCode + multiCodeProductCode,
            MultiBarcode = multiBarcode,
            PurchasePrice = purchasePrice,
            MultiCodeRetailPrice = retailPrice,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task PrepareExistingSetCompletionAsync(
        string productCode,
        string childCode,
        string detailCode,
        string containerCode = "C001",
        int productType = 0,
        bool insertContainer = false
    )
    {
        if (insertContainer)
        {
            await InsertContainerAsync(containerCode, status: 1);
        }
        await InsertActiveStoreAsync("S001");
        await InsertExistingProductAsync(
            productCode,
            $"HB-{productCode}",
            purchasePrice: 10m,
            retailPrice: 18.8m,
            productType
        );
        await InsertExistingWarehouseProductAsync(
            productCode,
            domesticPrice: 1.1m,
            importPrice: 2.2m,
            oemPrice: 3.3m
        );
        await InsertContainerDetailAsync(
            detailCode,
            containerCode,
            productCode,
            "套装商品",
            importPrice: 10m,
            oemPrice: 18.8m
        );
        await InsertDomesticProductAsync(
            productCode,
            $"HB-{productCode}",
            "套装商品",
            "Set Product",
            productType: 1
        );
        await InsertDomesticSetProductAsync(
            productCode,
            childCode,
            $"ITEM-{childCode}",
            importPrice: 2m,
            oemPrice: 6.5m
        );
    }

    private static ProductSetCode CreateProductSetProjection(
        string setCodeId,
        string productCode,
        string childCode,
        bool isActive = true,
        bool isDeleted = false
    )
    {
        return new ProductSetCode
        {
            SetCodeId = setCodeId,
            ProductCode = productCode,
            SetProductCode = childCode,
            SetItemNumber = $"OLD-{setCodeId}",
            SetBarcode = $"BAR-{setCodeId}",
            SetPurchasePrice = 10m,
            SetRetailPrice = 1m,
            SetQuantity = 7,
            SetType = 1,
            IsActive = isActive,
            IsDeleted = isDeleted,
            CreatedAt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            CreatedBy = $"CREATED-{setCodeId}",
            UpdatedAt = new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc),
            UpdatedBy = $"UPDATED-{setCodeId}",
        };
    }

    private static StoreMultiCodeProduct CreateStoreMultiCodeProjection(
        string uuid,
        string productCode,
        string childCode,
        bool isActive = true,
        bool isDeleted = false
    )
    {
        return new StoreMultiCodeProduct
        {
            UUID = uuid,
            StoreCode = "S001",
            ProductCode = productCode,
            MultiCodeProductCode = childCode,
            StoreMultiCodeProductCode = $"OLD-{uuid}",
            MultiBarcode = $"BAR-{uuid}",
            PurchasePrice = 10m,
            MultiCodeRetailPrice = 1m,
            DiscountRate = 0.25m,
            IsAutoPricing = true,
            IsSpecialProduct = true,
            IsActive = isActive,
            IsDeleted = isDeleted,
            CreatedAt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            CreatedBy = $"CREATED-{uuid}",
            UpdatedAt = new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc),
            UpdatedBy = $"UPDATED-{uuid}",
        };
    }

    private ContainerProductCreationExecutorService CreateService(
        IWarehouseProductChangeHistoryService? historyService = null,
        IProductWarehouseReactService? warehouseService = null,
        ILogger<ContainerProductCreationExecutorService>? logger = null
    )
    {
        var configuration = new ConfigurationBuilder().Build();
        var context = CreateSqlSugarContext(_db);
        var itemBarcodeService = new ItemBarcodeService(
            context,
            NullLogger<ItemBarcodeService>.Instance,
            configuration
        );
        warehouseService ??= new ProductWarehouseReactService(
            context,
            CreateHqSqlSugarContext(),
            NullLogger<ProductWarehouseReactService>.Instance,
            configuration,
            itemBarcodeService,
            Mock.Of<IMapper>(),
            Mock.Of<IDataSyncFullService>(),
            Mock.Of<IWarehouseProductChangeHistoryService>()
        );

        return new ContainerProductCreationExecutorService(
            context,
            CreateHBSalesSqlSugarContext(_hbSalesDb),
            warehouseService,
            logger ?? NullLogger<ContainerProductCreationExecutorService>.Instance,
            historyService ?? Mock.Of<IWarehouseProductChangeHistoryService>()
        );
    }

    private async Task InsertWarehouseCategoryAsync(string categoryGuid)
    {
        await _db.Insertable(new WarehouseCategory
        {
            CategoryGUID = categoryGuid,
            CategoryName = $"分类 {categoryGuid}",
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private ContainerReactService CreateContainerReactService()
    {
        return new ContainerReactService(
            CreateSqlSugarContext(_db),
            CreateHqSqlSugarContext(),
            CreateHBSalesSqlSugarContext(_hbSalesDb),
            new ConfigurationBuilder().Build(),
            Mock.Of<IMapper>(),
            NullLogger<ContainerReactService>.Instance,
            Mock.Of<IContainerHqSyncService>(),
            CreateTranslationServiceMock(),
            Mock.Of<IWarehouseProductChangeHistoryService>(),
            Mock.Of<ICurrentUserService>()
        );
    }

    private static ITranslationService CreateTranslationServiceMock()
    {
        var translationService = new Mock<ITranslationService>();
        translationService
            .Setup(service => service.ContainsChinese(It.IsAny<string>()))
            .Returns((string value) => value.Any(ch => ch >= '\u4e00' && ch <= '\u9fff'));
        translationService
            .Setup(service => service.BatchTranslateToEnglishAsync(It.IsAny<List<string>>()))
            .ReturnsAsync((List<string> values) => values.ToDictionary(value => value, value => value));
        return translationService.Object;
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(
            typeof(SqlSugarContext)
        );
        var dbField = typeof(SqlSugarContext).GetField(
            "_db",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        dbField!.SetValue(context, db);
        return context;
    }

    private static HqSqlSugarContext CreateHqSqlSugarContext()
    {
        var context = (HqSqlSugarContext)RuntimeHelpers.GetUninitializedObject(
            typeof(HqSqlSugarContext)
        );
        var dbField = typeof(HqSqlSugarContext).GetField(
            "_db",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        dbField!.SetValue(context, new Mock<ISqlSugarClient>().Object);
        return context;
    }

    private static HBSalesSqlSugarContext CreateHBSalesSqlSugarContext(SqlSugarScope db)
    {
        var context = (HBSalesSqlSugarContext)RuntimeHelpers.GetUninitializedObject(
            typeof(HBSalesSqlSugarContext)
        );
        var dbField = typeof(HBSalesSqlSugarContext).GetField(
            "_db",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        dbField!.SetValue(context, db);
        return context;
    }

    private sealed class BlockingContainerProductCreationExecutor
        : IContainerProductCreationExecutorService
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string?> _updatedByReceived = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource<string?> _actorUserGuidReceived = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public Task<ContainerProductCreationResultDto> ExecuteAsync(
            ContainerProductCreationJobRequestDto request,
            CancellationToken cancellationToken = default
        )
        {
            return ExecuteAsync(request, null, null, cancellationToken);
        }

        public Task<ContainerProductCreationResultDto> ExecuteAsync(
            ContainerProductCreationJobRequestDto request,
            string? updatedBy,
            CancellationToken cancellationToken = default
        )
        {
            return ExecuteAsync(request, null, updatedBy, cancellationToken);
        }

        public async Task<ContainerProductCreationResultDto> ExecuteAsync(
            ContainerProductCreationJobRequestDto request,
            string? actorUserGuid,
            string? updatedBy,
            CancellationToken cancellationToken = default
        )
        {
            _actorUserGuidReceived.TrySetResult(actorUserGuid);
            _updatedByReceived.TrySetResult(updatedBy);
            await _release.Task.WaitAsync(cancellationToken);
            return new ContainerProductCreationResultDto();
        }

        public Task<string?> WaitForUpdatedByAsync() => _updatedByReceived.Task;

        public Task<string?> WaitForActorUserGuidAsync() => _actorUserGuidReceived.Task;

        public void Release() => _release.TrySetResult();
    }

    private sealed class CallbackLogger<T>(Action onInformation) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
            where TState : notnull
        {
            if (logLevel == LogLevel.Information)
            {
                onInformation();
            }
        }
    }
}
