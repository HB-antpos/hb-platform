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
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class StoreProductMaintenanceWarehousePriceSyncTests : IDisposable
{
    private const string StorePriceUuid = "store-price-1";
    private const string ProductCode = "product-1";
    private const string StoreCode = "store-1";

    private readonly string _dbPath;
    private readonly SqliteConnection _sqliteConnection;
    private readonly SqlSugarClient _db;
    private readonly MemoryCache _cache;
    private readonly Mock<IAutoPricingService> _autoPricingService = new();

    public StoreProductMaintenanceWarehousePriceSyncTests()
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
            typeof(Product),
            typeof(DomesticProduct),
            typeof(WarehouseProduct),
            typeof(StoreRetailPrice),
            typeof(ProductSetCode),
            typeof(StoreMultiCodeProduct),
            typeof(StoreClearancePrice),
            typeof(Store)
        );
        _db.Ado.ExecuteCommand(
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

        _autoPricingService
            .Setup(service => service.FindStrategyForPriceAsync(
                It.IsAny<decimal>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()
            ))
            .ReturnsAsync((PricingStrategy?)null);
        _autoPricingService
            .Setup(service => service.CalculateRate(It.IsAny<decimal>(), It.IsAny<PricingStrategy?>()))
            .Returns(0m);
        _autoPricingService
            .Setup(service => service.GetAllActiveStrategiesAsync())
            .ReturnsAsync(new List<PricingStrategy>());
        _cache = new MemoryCache(new MemoryCacheOptions());
    }

    [Fact]
    public void Contract_定义仓库价格对账请求与响应字段()
    {
        var requestType = typeof(SyncStoreProductWarehousePriceRequestDto);
        var responseType = typeof(SyncStoreProductWarehousePriceResultDto);

        Assert.NotNull(requestType.GetProperty("ConfirmRetailPrice"));
        Assert.NotNull(requestType.GetProperty("ExpectedWarehousePurchasePrice"));
        Assert.NotNull(requestType.GetProperty("ExpectedWarehouseRetailPrice"));
        Assert.NotNull(requestType.GetProperty("ExpectedStorePurchasePrice"));
        Assert.NotNull(requestType.GetProperty("ExpectedStoreRetailPrice"));
        Assert.NotNull(requestType.GetProperty("ExpectedDiscountRate"));
        Assert.NotNull(responseType.GetProperty("Status"));
        Assert.NotNull(responseType.GetProperty("PurchaseUpdated"));
        Assert.NotNull(responseType.GetProperty("RetailUpdated"));
        Assert.NotNull(responseType.GetProperty("RetailConfirmationRequired"));
        Assert.NotNull(responseType.GetProperty("StorePrice"));
        Assert.NotNull(responseType.GetProperty("WarehousePurchasePrice"));
        Assert.NotNull(responseType.GetProperty("WarehouseRetailPrice"));
        Assert.NotNull(responseType.GetProperty("PreviousStorePurchasePrice"));
        Assert.NotNull(responseType.GetProperty("PreviousStoreRetailPrice"));
        Assert.NotNull(responseType.GetProperty("DiscountRate"));
        Assert.NotNull(responseType.GetProperty("PreviousDiscountedRetailPrice"));
        Assert.NotNull(responseType.GetProperty("NewDiscountedRetailPrice"));
    }

    [Fact]
    public void Controller_暴露当前分店仓库价格对账路由()
    {
        var method = typeof(ReactStoreProductMaintenanceController).GetMethod(
            "SyncWarehousePrice",
            BindingFlags.Instance | BindingFlags.Public
        );
        var route = method?.GetCustomAttribute<HttpPostAttribute>();
        var serviceMethod = typeof(IStoreProductMaintenanceReactService).GetMethod(
            "SyncWarehousePriceAsync"
        );

        Assert.NotNull(method);
        Assert.Equal("store-prices/{uuid}/sync-warehouse", route?.Template);
        Assert.NotNull(serviceMethod);
    }

    [Fact]
    public void Controller_暴露仅超级管理员可保存的商品条码快照路由()
    {
        var method = typeof(ReactStoreProductMaintenanceController).GetMethod(
            "SaveSetCodeSnapshot",
            BindingFlags.Instance | BindingFlags.Public
        );
        var route = method?.GetCustomAttribute<HttpPostAttribute>();

        Assert.NotNull(method);
        Assert.Equal("set-codes/save-snapshot", route?.Template);
        Assert.NotNull(typeof(IStoreProductMaintenanceReactService).GetMethod("SaveSetCodeSnapshotAsync"));
    }

    [Fact]
    public async Task Controller_非超级管理员拒绝保存全局条码快照()
    {
        var service = new Mock<IStoreProductMaintenanceReactService>(MockBehavior.Strict);
        var controller = new ReactStoreProductMaintenanceController(
            service.Object, Mock.Of<IDeviceRegistrationService>(), Mock.Of<IMapper>(),
            CreateSqlSugarContext(_db), NullLogger<ReactStoreProductMaintenanceController>.Instance,
            Mock.Of<IAuthorizationService>()
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.Name, "warehouse-manager"),
                        new Claim(ClaimTypes.Role, "WarehouseManager"),
                    }, "test")),
                },
            },
        };

        Assert.IsType<ForbidResult>(await controller.SaveSetCodeSnapshot(new()));
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Controller_SuperAdmin以不受限作用域调用快照服务()
    {
        var service = new Mock<IStoreProductMaintenanceReactService>(MockBehavior.Strict);
        service.Setup(value => value.SaveSetCodeSnapshotAsync(
                It.IsAny<SaveStoreProductSetCodeSnapshotDto>(), "super-admin", null
            ))
            .ReturnsAsync(ApiResponse<SaveStoreProductSetCodeSnapshotResultDto>.OK(new()));
        var controller = new ReactStoreProductMaintenanceController(
            service.Object, Mock.Of<IDeviceRegistrationService>(), Mock.Of<IMapper>(),
            CreateSqlSugarContext(_db), NullLogger<ReactStoreProductMaintenanceController>.Instance,
            CreateSuccessfulAuthorizationService()
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.Name, "super-admin"),
                        new Claim(ClaimTypes.Role, "SuperAdmin"),
                    }, "test")),
                },
            },
        };

        Assert.IsType<OkObjectResult>(await controller.SaveSetCodeSnapshot(new()));
        service.VerifyAll();
    }

    [Fact]
    public async Task Controller_更新商品类型遇成本锁繁忙返回409而非200失败()
    {
        // 移动端「商品查询」切换类型：与多码/条码等写入口一致，BUSY 必须走 409，客户端才能给出可重试提示。
        var service = new Mock<IStoreProductMaintenanceReactService>(MockBehavior.Strict);
        service.Setup(value => value.UpdateProductTypeAsync(
                "P-1",
                It.IsAny<UpdateStoreProductTypeDto>(),
                It.IsAny<string>(),
                It.IsAny<List<string>?>()
            ))
            .ReturnsAsync(ApiResponse<StoreProductTypeUpdateResultDto>.Error(
                "套装商品正在被其他操作修改，请稍后重试",
                SetChildPurchasePriceMutationLock.BusyErrorCode
            ));
        var controller = new ReactStoreProductMaintenanceController(
            service.Object, Mock.Of<IDeviceRegistrationService>(), Mock.Of<IMapper>(),
            CreateSqlSugarContext(_db), NullLogger<ReactStoreProductMaintenanceController>.Instance,
            CreateSuccessfulAuthorizationService()
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.Name, "super-admin"),
                        new Claim(ClaimTypes.Role, "SuperAdmin"),
                    }, "test")),
                },
            },
        };

        var result = await controller.UpdateProductType("P-1", new UpdateStoreProductTypeDto());

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var body = Assert.IsType<ApiResponse<StoreProductTypeUpdateResultDto>>(conflict.Value);
        Assert.Equal(SetChildPurchasePriceMutationLock.BusyErrorCode, body.ErrorCode);
        service.VerifyAll();
    }

    [Fact]
    public async Task Controller_登录编辑缺少StoreProductsEdit权限时拒绝且不调用服务()
    {
        var service = new Mock<IStoreProductMaintenanceReactService>(MockBehavior.Strict);
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(item => item.AuthorizeAsync(
                It.IsAny<ClaimsPrincipal>(),
                null,
                Permissions.StoreProducts.Edit
            ))
            .ReturnsAsync(AuthorizationResult.Failed());
        var controller = new ReactStoreProductMaintenanceController(
            service.Object,
            Mock.Of<IDeviceRegistrationService>(),
            Mock.Of<IMapper>(),
            CreateSqlSugarContext(_db),
            NullLogger<ReactStoreProductMaintenanceController>.Instance,
            authorization.Object
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.Name, "warehouse-manager"),
                        new Claim(ClaimTypes.Role, "WarehouseManager"),
                    }, "test")),
                },
            },
        };

        Assert.IsType<ForbidResult>(await controller.UpdateStorePrice("price-1", new()));
        authorization.VerifyAll();
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateProductTypeAsync_成功提交时入队全局类型事件并保留设备授权分店()
    {
        const string productCode = "P-TYPE-HQ-EVENT";
        await SeedMultiCodeParentAsync(productCode, 20m, 20m);
        var calls = new List<(ISqlSugarClient Db, ProductMaintenanceHqMutationRequest Request)>();
        var (writer, status) = CreateCapturingProjectionWriter(calls);

        var result = await CreateService(hqProjectionWriter: writer.Object)
            .UpdateProductTypeAsync(
                productCode,
                new UpdateStoreProductTypeDto { ProductType = 1, StoreCode = StoreCode },
                "device:test-device",
                new List<string> { StoreCode }
            );

        Assert.True(result.Success, result.Message);
        Assert.Same(status, result.Data!.HqSync);
        var request = AssertMutationRequest(
            calls,
            ProductMaintenanceHqOperationKinds.ProductTypeUpdated,
            Array.Empty<string>(),
            new[] { StoreCode },
            new[] { ProductMaintenanceHqFieldMasks.ProductType }
        );
        Assert.Empty(request.Tombstones);
        Assert.Equal("test-device", request.RequestedByDeviceId);
        Assert.Null(request.RequestedByUserGuid);
    }

    [Fact]
    public async Task UpdateMultiCodeAsync_成功提交时仅入队当前店多码事件()
    {
        const string productCode = "P-MULTI-HQ-EVENT";
        await SeedMultiCodeParentAsync(productCode, 20m, 20m);
        var set = BuildMultiCodeSetCode(productCode, "A", 10m, setType: 2);
        var row = BuildMultiCodeStoreRow(productCode, "A", 10m, 20m);
        await _db.Insertable(set).ExecuteCommandAsync();
        await _db.Insertable(row).ExecuteCommandAsync();
        var calls = new List<(ISqlSugarClient Db, ProductMaintenanceHqMutationRequest Request)>();
        var (writer, status) = CreateCapturingProjectionWriter(calls);

        var result = await CreateService(hqProjectionWriter: writer.Object)
            .UpdateMultiCodeAsync(
                row.UUID,
                new UpdateStoreProductMultiCodeDto
                {
                    RetailPrice = 25m,
                    IsAutoPricing = false,
                    IsSpecialProduct = true,
                    IsActive = true,
                },
                "device:test-device",
                new List<string> { StoreCode }
            );

        Assert.True(result.Success, result.Message);
        Assert.Same(status, result.Data!.HqSync);
        var request = AssertMutationRequest(
            calls,
            ProductMaintenanceHqOperationKinds.ProductCodesUpdated,
            new[] { StoreCode },
            new[] { StoreCode },
            new[] { ProductMaintenanceHqFieldMasks.StoreMultiCodes }
        );
        Assert.Empty(request.Tombstones);
        Assert.Equal("test-device", request.RequestedByDeviceId);
    }

    [Fact]
    public async Task UpdateMultiCodeAsync_旧版无SetCode关联记录可原子更新条码并入队当前店事件()
    {
        const string productCode = "P-LEGACY-MULTI-BARCODE";
        await SeedMultiCodeParentAsync(productCode, 20m, 20m);
        var row = BuildMultiCodeStoreRow(productCode, "LEGACY", 20m, 10m);
        await _db.Insertable(row).ExecuteCommandAsync();
        var calls = new List<(ISqlSugarClient Db, ProductMaintenanceHqMutationRequest Request)>();
        var (writer, status) = CreateCapturingProjectionWriter(calls);

        var result = await CreateService(hqProjectionWriter: writer.Object)
            .UpdateMultiCodeAsync(
                row.UUID,
                new UpdateStoreProductMultiCodeDto { Barcode = "  LEGACY-BARCODE-UPDATED  " },
                "device:test-device",
                new List<string> { StoreCode }
            );

        Assert.True(result.Success, result.Message);
        Assert.Equal("LEGACY-BARCODE-UPDATED", result.Data!.Barcode);
        Assert.Same(status, result.Data.HqSync);
        var persisted = await _db.Queryable<StoreMultiCodeProduct>()
            .SingleAsync(x => x.UUID == row.UUID);
        Assert.Equal("LEGACY-BARCODE-UPDATED", persisted.MultiBarcode);
        AssertMutationRequest(
            calls,
            ProductMaintenanceHqOperationKinds.ProductCodesUpdated,
            new[] { StoreCode },
            new[] { StoreCode },
            new[] { ProductMaintenanceHqFieldMasks.StoreMultiCodes }
        );
    }

    [Fact]
    public async Task UpdateMultiCodeAsync_拒绝空白条码且不修改旧版记录()
    {
        const string productCode = "P-LEGACY-MULTI-BARCODE-INVALID";
        await SeedMultiCodeParentAsync(productCode, 20m, 20m);
        var row = BuildMultiCodeStoreRow(productCode, "LEGACY", 20m, 10m);
        await _db.Insertable(row).ExecuteCommandAsync();

        var result = await CreateService().UpdateMultiCodeAsync(
            row.UUID,
            new UpdateStoreProductMultiCodeDto { Barcode = "   " },
            "device:test-device",
            new List<string> { StoreCode }
        );

        Assert.False(result.Success);
        Assert.Contains("条码不能为空", result.Message);
        var persisted = await _db.Queryable<StoreMultiCodeProduct>()
            .SingleAsync(x => x.UUID == row.UUID);
        Assert.Equal(row.MultiBarcode, persisted.MultiBarcode);
    }

    [Fact]
    public async Task CreateSetCodeAsync_成功提交时按实际投影店入队全局与门店多码事件()
    {
        const string productCode = "P-CREATE-SET-HQ-EVENT";
        await SeedMultiCodeParentAsync(productCode, 20m, 20m);
        await EnsureActiveStoreAsync();
        var calls = new List<(ISqlSugarClient Db, ProductMaintenanceHqMutationRequest Request)>();
        var (writer, status) = CreateCapturingProjectionWriter(calls);

        var result = await CreateService(hqProjectionWriter: writer.Object)
            .CreateSetCodeAsync(
                new CreateStoreProductSetCodeDto
                {
                    ProductCode = productCode,
                    StoreCode = StoreCode,
                    ProductType = 1,
                    Barcode = "BAR-NEW",
                    RetailPrice = 10m,
                    IsActive = true,
                },
                "device:test-device",
                new List<string> { StoreCode }
            );

        Assert.True(result.Success, result.Message);
        Assert.Same(status, result.Data!.HqSync);
        var request = AssertMutationRequest(
            calls,
            ProductMaintenanceHqOperationKinds.ProductCodesUpdated,
            new[] { StoreCode },
            new[] { StoreCode },
            new[]
            {
                ProductMaintenanceHqFieldMasks.ProductSetCodes,
                ProductMaintenanceHqFieldMasks.StoreMultiCodes,
            }
        );
        Assert.Empty(request.Tombstones);
    }

    [Fact]
    public async Task CreateSetCodeAsync_目标商品不属于授权分店时拒绝全局条码创建()
    {
        const string productCode = "P-CREATE-SET-SCOPE";
        await SeedMultiCodeParentAsync(productCode, 20m, 20m);
        var calls = new List<(ISqlSugarClient Db, ProductMaintenanceHqMutationRequest Request)>();
        var (writer, _) = CreateCapturingProjectionWriter(calls);

        var result = await CreateService(hqProjectionWriter: writer.Object)
            .CreateSetCodeAsync(
                new CreateStoreProductSetCodeDto
                {
                    ProductCode = productCode,
                    StoreCode = "S02",
                    ProductType = 1,
                    Barcode = "BAR-UNAUTHORIZED",
                    RetailPrice = 10m,
                    IsActive = true,
                },
                "device:other-store-device",
                new List<string> { "S02" }
            );

        Assert.False(result.Success);
        Assert.Contains("无权", result.Message);
        Assert.Empty(calls);
        Assert.Equal(0, await _db.Queryable<ProductSetCode>()
            .Where(item => item.ProductCode == productCode && !item.IsDeleted)
            .CountAsync());
    }

    [Fact]
    public async Task UpdateSetCodeAsync_成功提交时按实际投影店入队全局与门店多码事件()
    {
        const string productCode = "P-UPDATE-SET-HQ-EVENT";
        await SeedMultiCodeParentAsync(productCode, 20m, 20m);
        await EnsureActiveStoreAsync();
        var set = BuildMultiCodeSetCode(productCode, "A", 10m, setType: 1);
        await _db.Insertable(set).ExecuteCommandAsync();
        await _db.Insertable(BuildMultiCodeStoreRow(productCode, "A", 10m, 20m))
            .ExecuteCommandAsync();
        var calls = new List<(ISqlSugarClient Db, ProductMaintenanceHqMutationRequest Request)>();
        var (writer, status) = CreateCapturingProjectionWriter(calls);

        var result = await CreateService(hqProjectionWriter: writer.Object)
            .UpdateSetCodeAsync(
                set.SetCodeId,
                new UpdateStoreProductSetCodeDto
                {
                    StoreCode = StoreCode,
                    Barcode = "BAR-UPDATED",
                    RetailPrice = 12m,
                    IsActive = true,
                },
                "device:test-device",
                new List<string> { StoreCode }
            );

        Assert.True(result.Success, result.Message);
        Assert.Same(status, result.Data!.HqSync);
        var request = AssertMutationRequest(
            calls,
            ProductMaintenanceHqOperationKinds.ProductCodesUpdated,
            new[] { StoreCode },
            new[] { StoreCode },
            new[]
            {
                ProductMaintenanceHqFieldMasks.ProductSetCodes,
                ProductMaintenanceHqFieldMasks.StoreMultiCodes,
            }
        );
        Assert.Empty(request.Tombstones);
    }

    [Fact]
    public async Task UpdateSetCodeAsync_目标商品不属于授权分店时拒绝全局条码修改()
    {
        const string productCode = "P-UPDATE-SET-SCOPE";
        await SeedMultiCodeParentAsync(productCode, 20m, 20m);
        var set = BuildMultiCodeSetCode(productCode, "A", 10m, setType: 1);
        await _db.Insertable(set).ExecuteCommandAsync();
        var calls = new List<(ISqlSugarClient Db, ProductMaintenanceHqMutationRequest Request)>();
        var (writer, _) = CreateCapturingProjectionWriter(calls);

        var result = await CreateService(hqProjectionWriter: writer.Object)
            .UpdateSetCodeAsync(
                set.SetCodeId,
                new UpdateStoreProductSetCodeDto
                {
                    StoreCode = "S02",
                    Barcode = "BAR-UNAUTHORIZED",
                    RetailPrice = 12m,
                    IsActive = true,
                },
                "device:other-store-device",
                new List<string> { "S02" }
            );

        Assert.False(result.Success);
        Assert.Contains("无权", result.Message);
        Assert.Empty(calls);
        var persisted = await _db.Queryable<ProductSetCode>()
            .SingleAsync(item => item.SetCodeId == set.SetCodeId);
        Assert.Equal(set.SetBarcode, persisted.SetBarcode);
        Assert.Equal(set.SetRetailPrice, persisted.SetRetailPrice);
    }

    [Fact]
    public async Task DeleteSetCodeAsync_成功提交时入队全局停用墓碑并保留设备授权分店()
    {
        const string productCode = "P-DELETE-SET-HQ-EVENT";
        await SeedMultiCodeParentAsync(productCode, 20m, 20m);
        var set = BuildMultiCodeSetCode(productCode, "A", 10m, setType: 1);
        var storeRow = BuildMultiCodeStoreRow(productCode, "A", 10m, 20m);
        await _db.Insertable(set).ExecuteCommandAsync();
        await _db.Insertable(storeRow).ExecuteCommandAsync();
        var calls = new List<(ISqlSugarClient Db, ProductMaintenanceHqMutationRequest Request)>();
        var (writer, status) = CreateCapturingProjectionWriter(calls);

        var result = await CreateService(hqProjectionWriter: writer.Object)
            .DeleteSetCodeAsync(
                set.SetCodeId,
                "device:test-device",
                new List<string> { StoreCode }
            );

        Assert.True(result.Success, result.Message);
        Assert.True(result.Data!.Deleted);
        Assert.Same(status, result.Data.HqSync);
        var request = AssertMutationRequest(
            calls,
            ProductMaintenanceHqOperationKinds.ProductCodesDeleted,
            Array.Empty<string>(),
            new[] { StoreCode },
            Array.Empty<string>()
        );
        var tombstone = Assert.Single(request.Tombstones);
        Assert.Equal(ProductMaintenanceHqResourceKinds.ProductSetCode, tombstone.ResourceKind);
        Assert.Null(tombstone.StoreCode);
        Assert.Equal(set.SetProductCode, tombstone.BusinessKey);
        Assert.True((await _db.Queryable<ProductSetCode>()
            .SingleAsync(item => item.SetCodeId == set.SetCodeId)).IsDeleted);
        Assert.Equal(0, await _db.Queryable<StoreMultiCodeProduct>()
            .Where(item => item.UUID == storeRow.UUID)
            .CountAsync());
    }

    [Fact]
    public async Task DeleteSetCodeAsync_仅有其他分店权限时拒绝且不删除全局条码()
    {
        const string productCode = "P-DELETE-SET-SCOPE";
        await SeedMultiCodeParentAsync(productCode, 20m, 20m);
        var set = BuildMultiCodeSetCode(productCode, "A", 10m, setType: 1);
        var storeRow = BuildMultiCodeStoreRow(productCode, "A", 10m, 20m);
        await _db.Insertable(set).ExecuteCommandAsync();
        await _db.Insertable(storeRow).ExecuteCommandAsync();
        var calls = new List<(ISqlSugarClient Db, ProductMaintenanceHqMutationRequest Request)>();
        var (writer, _) = CreateCapturingProjectionWriter(calls);

        var result = await CreateService(hqProjectionWriter: writer.Object)
            .DeleteSetCodeAsync(
                set.SetCodeId,
                "device:other-store-device",
                new List<string> { "S02" }
            );

        Assert.False(result.Success);
        Assert.Contains("无权", result.Message);
        Assert.Empty(calls);
        Assert.False((await _db.Queryable<ProductSetCode>()
            .SingleAsync(item => item.SetCodeId == set.SetCodeId)).IsDeleted);
        Assert.Equal(1, await _db.Queryable<StoreMultiCodeProduct>()
            .Where(item => item.UUID == storeRow.UUID && !item.IsDeleted)
            .CountAsync());
    }

    [Fact]
    public async Task UpsertClearancePriceAsync_非空价格成功提交时入队当前店精确事件()
    {
        await SeedAsync("200", 5m, 10m, 0.9m, DateTime.UtcNow.AddDays(-2));
        var calls = new List<(ISqlSugarClient Db, ProductMaintenanceHqMutationRequest Request)>();
        var (writer, status) = CreateCapturingProjectionWriter(calls);

        var result = await CreateService(hqProjectionWriter: writer.Object)
            .UpsertClearancePriceAsync(
                ProductCode,
                new UpsertStoreProductClearancePriceDto
                {
                    StoreCode = StoreCode,
                    ClearancePrice = 7.5m,
                },
                "device:test-device",
                new List<string> { StoreCode }
            );

        Assert.True(result.Success, result.Message);
        Assert.Same(status, result.Data!.HqSync);
        var request = AssertMutationRequest(
            calls,
            ProductMaintenanceHqOperationKinds.ClearancePriceUpdated,
            new[] { StoreCode },
            new[] { StoreCode },
            new[] { ProductMaintenanceHqFieldMasks.StoreClearancePrice }
        );
        Assert.Empty(request.Tombstones);
        var persisted = await _db.Queryable<StoreClearancePrice>().SingleAsync();
        Assert.Equal(7.5m, persisted.ClearancePrice);
    }

    [Fact]
    public async Task UpsertClearancePriceAsync_目标商品不属于授权分店时拒绝写入()
    {
        await SeedAsync("200", 5m, 10m, 0.9m, DateTime.UtcNow.AddDays(-2));
        var calls = new List<(ISqlSugarClient Db, ProductMaintenanceHqMutationRequest Request)>();
        var (writer, _) = CreateCapturingProjectionWriter(calls);

        var result = await CreateService(hqProjectionWriter: writer.Object)
            .UpsertClearancePriceAsync(
                ProductCode,
                new UpsertStoreProductClearancePriceDto
                {
                    StoreCode = "S02",
                    ClearancePrice = 7.5m,
                },
                "device:other-store-device",
                new List<string> { "S02" }
            );

        Assert.False(result.Success);
        Assert.Contains("无权", result.Message);
        Assert.Empty(calls);
        Assert.Equal(0, await _db.Queryable<StoreClearancePrice>()
            .Where(item => item.ProductCode == ProductCode && !item.IsDeleted)
            .CountAsync());
    }

    [Fact]
    public async Task UpdateStorePriceAsync_成功提交时入队当前店完整价格与多码事件()
    {
        await SeedAsync("200", 5m, 10m, 0.9m, DateTime.UtcNow.AddDays(-2));
        var calls = new List<(ISqlSugarClient Db, ProductMaintenanceHqMutationRequest Request)>();
        var (writer, status) = CreateCapturingProjectionWriter(calls);

        var result = await CreateService(hqProjectionWriter: writer.Object)
            .UpdateStorePriceAsync(
                StorePriceUuid,
                new UpdateStoreProductPriceDto
                {
                    PurchasePrice = 6m,
                    RetailPrice = 12m,
                    DiscountRate = 0.8m,
                    IsAutoPricing = false,
                    IsSpecialProduct = true,
                    IsActive = true,
                },
                "device:test-device",
                new List<string> { StoreCode }
            );

        Assert.True(result.Success, result.Message);
        Assert.Same(status, result.Data!.HqSync);
        var request = AssertMutationRequest(
            calls,
            ProductMaintenanceHqOperationKinds.StorePriceUpdated,
            new[] { StoreCode },
            new[] { StoreCode },
            ProductMaintenanceHqFieldMasks.StorePriceAndMultiCode
        );
        Assert.Empty(request.Tombstones);
        Assert.Equal("test-device", request.RequestedByDeviceId);
    }

    [Fact]
    public async Task UpdateStorePriceAsync_HQ入队失败时回滚门店价格且公开错误不含数据库详情()
    {
        await SeedAsync("200", 5m, 10m, 0.9m, DateTime.UtcNow.AddDays(-2));
        var writer = new Mock<IProductMaintenanceHqProjectionWriter>(MockBehavior.Strict);
        writer.Setup(item => item.EnqueueAsync(
                _db,
                It.IsAny<ProductMaintenanceHqMutationRequest>(),
                It.IsAny<CancellationToken>()
            ))
            .ThrowsAsync(new ProductMaintenanceHqEnqueueException("HQ 同步任务创建失败，请稍后重试"));

        var result = await CreateService(hqProjectionWriter: writer.Object)
            .UpdateStorePriceAsync(
                StorePriceUuid,
                new UpdateStoreProductPriceDto
                {
                    PurchasePrice = 99m,
                    RetailPrice = 88m,
                    DiscountRate = 0.1m,
                    IsAutoPricing = false,
                    IsSpecialProduct = true,
                    IsActive = false,
                },
                "device:test-device",
                new List<string> { StoreCode }
            );

        Assert.False(result.Success);
        Assert.Contains("HQ 同步任务创建失败，请稍后重试", result.Message);
        Assert.DoesNotContain("SqlException", result.Message, StringComparison.OrdinalIgnoreCase);
        var persisted = await GetStorePriceAsync();
        Assert.Equal(5m, persisted.PurchasePrice);
        Assert.Equal(10m, persisted.StoreRetailPriceValue);
        Assert.Equal(0.9m, persisted.DiscountRate);
        writer.VerifyAll();
    }

    [Fact]
    public async Task UpdateStorePriceAsync_未知数据库异常仅返回安全通用文案()
    {
        await SeedAsync("200", 5m, 10m, 0.9m, DateTime.UtcNow.AddDays(-2));
        var writer = new Mock<IProductMaintenanceHqProjectionWriter>(MockBehavior.Strict);
        writer.Setup(item => item.EnqueueAsync(
                _db,
                It.IsAny<ProductMaintenanceHqMutationRequest>(),
                It.IsAny<CancellationToken>()
            ))
            .ThrowsAsync(new InvalidOperationException("SqlException: invalid object dbo.ProductHqSyncOutbox"));

        var result = await CreateService(hqProjectionWriter: writer.Object)
            .UpdateStorePriceAsync(
                StorePriceUuid,
                new UpdateStoreProductPriceDto
                {
                    PurchasePrice = 99m,
                    RetailPrice = 88m,
                    DiscountRate = 0.1m,
                    IsAutoPricing = false,
                    IsSpecialProduct = true,
                    IsActive = false,
                },
                "device:test-device",
                new List<string> { StoreCode }
            );

        Assert.False(result.Success);
        Assert.Equal("更新分店商品失败，请稍后重试", result.Message);
        Assert.DoesNotContain("SqlException", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ProductHqSyncOutbox", result.Message, StringComparison.OrdinalIgnoreCase);
        var persisted = await GetStorePriceAsync();
        Assert.Equal(5m, persisted.PurchasePrice);
        Assert.Equal(10m, persisted.StoreRetailPriceValue);
        Assert.Equal(0.9m, persisted.DiscountRate);
        writer.VerifyAll();
    }

    [Fact]
    public async Task UpdateStorePriceAsync_响应投影失败时回滚本地事务且不误报成功()
    {
        await SeedAsync("200", 5m, 10m, 0.9m, DateTime.UtcNow.AddDays(-2));
        var calls = new List<(ISqlSugarClient Db, ProductMaintenanceHqMutationRequest Request)>();
        var (writer, _) = CreateCapturingProjectionWriter(calls);
        _autoPricingService
            .Setup(service => service.FindStrategyForPriceAsync(
                It.IsAny<decimal>(),
                It.IsAny<string?>(),
                It.IsAny<string?>()
            ))
            .ThrowsAsync(new InvalidOperationException("response projection failed"));

        var result = await CreateService(hqProjectionWriter: writer.Object)
            .UpdateStorePriceAsync(
                StorePriceUuid,
                new UpdateStoreProductPriceDto
                {
                    PurchasePrice = 99m,
                    RetailPrice = 88m,
                    DiscountRate = 0.1m,
                    IsAutoPricing = false,
                    IsSpecialProduct = true,
                    IsActive = false,
                },
                "device:test-device",
                new List<string> { StoreCode }
            );

        Assert.False(result.Success);
        Assert.Equal("更新分店商品失败，请稍后重试", result.Message);
        Assert.Single(calls);
        var persisted = await GetStorePriceAsync();
        Assert.Equal(5m, persisted.PurchasePrice);
        Assert.Equal(10m, persisted.StoreRetailPriceValue);
        Assert.Equal(0.9m, persisted.DiscountRate);
        writer.VerifyAll();
    }

    [Fact]
    public async Task UpsertClearancePriceAsync_删除入队失败时回滚本地清货价()
    {
        await SeedAsync("200", 5m, 10m, 0.9m, DateTime.UtcNow.AddDays(-2));
        await _db.Insertable(new StoreClearancePrice
        {
            UUID = "clearance-rollback",
            StoreCode = StoreCode,
            ProductCode = ProductCode,
            ClearanceBarcode = "CLR-ROLLBACK",
            ClearancePrice = 7.5m,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        var writer = new Mock<IProductMaintenanceHqProjectionWriter>(MockBehavior.Strict);
        writer.Setup(item => item.EnqueueAsync(
                _db,
                It.IsAny<ProductMaintenanceHqMutationRequest>(),
                It.IsAny<CancellationToken>()
            ))
            .ThrowsAsync(new ProductMaintenanceHqEnqueueException("HQ 同步任务创建失败，请稍后重试"));

        var result = await CreateService(hqProjectionWriter: writer.Object)
            .UpsertClearancePriceAsync(
                ProductCode,
                new UpsertStoreProductClearancePriceDto
                {
                    StoreCode = StoreCode,
                    ClearancePrice = null,
                },
                "device:test-device",
                new List<string> { StoreCode }
            );

        Assert.False(result.Success);
        var persisted = await _db.Queryable<StoreClearancePrice>()
            .SingleAsync(item => item.UUID == "clearance-rollback");
        Assert.Equal(7.5m, persisted.ClearancePrice);
        Assert.False(persisted.IsDeleted);
        writer.VerifyAll();
    }

    [Fact]
    public async Task SaveSetCodeSnapshotAsync_提交增删改时原子更新并同步投影()
    {
        const string productCode = "P-SNAPSHOT-SAVE";
        await SeedMultiCodeParentAsync(productCode, storePurchasePrice: 30m, productPurchasePrice: 30m);
        await EnsureActiveStoreAsync();
        await _db.Updateable<Product>()
            .SetColumns(x => x.ProductType == 1)
            .Where(x => x.ProductCode == productCode)
            .ExecuteCommandAsync();
        var retained = BuildMultiCodeSetCode(productCode, "RETAIN", 10m, setType: 1);
        var removed = BuildMultiCodeSetCode(productCode, "REMOVE", 20m, setType: 1);
        await _db.Insertable(new[] { retained, removed }).ExecuteCommandAsync();
        var calls = new List<(ISqlSugarClient Db, ProductMaintenanceHqMutationRequest Request)>();
        var (writer, status) = CreateCapturingProjectionWriter(calls);
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(item => item.GetCurrentUsername()).Returns("super-admin");
        currentUser.Setup(item => item.GetCurrentUserGuid()).Returns("super-admin-guid");

        var result = await CreateService(
                hqProjectionWriter: writer.Object,
                currentUserService: currentUser.Object
            )
            .SaveSetCodeSnapshotAsync(
            new SaveStoreProductSetCodeSnapshotDto
            {
                ProductCode = productCode,
                StoreCode = StoreCode,
                ExpectedProductType = 1,
                ProductType = 1,
                ExpectedItems = new() { SnapshotItem(retained), SnapshotItem(removed) },
                Items = new()
                {
                    new()
                    {
                        SetCodeId = retained.SetCodeId,
                        Barcode = "BAR-UPDATED",
                        RetailPrice = 11m,
                        SetType = 1,
                        IsActive = true,
                    },
                    new()
                    {
                        Barcode = "BAR-NEW",
                        RetailPrice = 30m,
                        SetType = 1,
                        IsActive = true,
                    },
                },
            },
            "测试用户",
            accessibleStoreCodes: null
        );

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, result.Data!.Items.Count);
        Assert.Contains(result.Data.Items, item => item.SetCodeId == retained.SetCodeId && item.SetBarcode == "BAR-UPDATED");
        Assert.DoesNotContain(
            await _db.Queryable<ProductSetCode>().Where(item => item.ProductCode == productCode && !item.IsDeleted).ToListAsync(),
            item => item.SetCodeId == removed.SetCodeId
        );
        Assert.Equal(2, await _db.Queryable<StoreMultiCodeProduct>()
            .Where(item => item.ProductCode == productCode && !item.IsDeleted)
            .CountAsync());
        Assert.Same(status, result.Data.HqSync);
        var request = AssertMutationRequest(
            calls,
            ProductMaintenanceHqOperationKinds.SetCodeSnapshot,
            new[] { StoreCode },
            null,
            new[]
            {
                ProductMaintenanceHqFieldMasks.ProductType,
                ProductMaintenanceHqFieldMasks.ProductSetCodes,
                ProductMaintenanceHqFieldMasks.StoreMultiCodes,
            }
        );
        var tombstone = Assert.Single(request.Tombstones);
        Assert.Equal(ProductMaintenanceHqResourceKinds.ProductSetCode, tombstone.ResourceKind);
        Assert.Null(tombstone.StoreCode);
        Assert.Equal(removed.SetProductCode, tombstone.BusinessKey);
        Assert.Equal("super-admin-guid", request.RequestedByUserGuid);
        Assert.Null(request.RequestedByDeviceId);
    }

    [Fact]
    public async Task SaveSetCodeSnapshotAsync_期望快照过期时返回冲突且不写入()
    {
        const string productCode = "P-SNAPSHOT-CONFLICT";
        await SeedMultiCodeParentAsync(productCode, storePurchasePrice: 30m, productPurchasePrice: 30m);
        await EnsureActiveStoreAsync();
        await _db.Updateable<Product>()
            .SetColumns(x => x.ProductType == 1)
            .Where(x => x.ProductCode == productCode)
            .ExecuteCommandAsync();
        var existing = BuildMultiCodeSetCode(productCode, "STALE", 10m, setType: 1);
        await _db.Insertable(existing).ExecuteCommandAsync();

        var result = await CreateService().SaveSetCodeSnapshotAsync(
            new SaveStoreProductSetCodeSnapshotDto
            {
                ProductCode = productCode,
                StoreCode = StoreCode,
                ExpectedProductType = 1,
                ProductType = 1,
                ExpectedItems = new()
                {
                    new()
                    {
                        SetCodeId = existing.SetCodeId,
                        Barcode = existing.SetBarcode!,
                        RetailPrice = 9m,
                        SetType = 1,
                        IsActive = true,
                    },
                },
                Items = new()
                {
                    new()
                    {
                        SetCodeId = existing.SetCodeId,
                        Barcode = "MUST-NOT-SAVE",
                        RetailPrice = 12m,
                        SetType = 1,
                        IsActive = true,
                    },
                },
            },
            "测试用户",
            accessibleStoreCodes: null
        );

        Assert.False(result.Success);
        Assert.Equal(StoreProductMaintenanceReactService.SetCodeSnapshotConflictErrorCode, result.ErrorCode);
        var persisted = await _db.Queryable<ProductSetCode>()
            .SingleAsync(item => item.SetCodeId == existing.SetCodeId);
        Assert.Equal(existing.SetBarcode, persisted.SetBarcode);
        Assert.Equal(10m, persisted.SetRetailPrice);
    }

    [Fact]
    public async Task SaveSetCodeSnapshotAsync_审计失败时回滚父类型和新增条码()
    {
        const string productCode = "P-SNAPSHOT-ROLLBACK";
        await SeedMultiCodeParentAsync(productCode, storePurchasePrice: 30m, productPurchasePrice: 30m);
        await EnsureActiveStoreAsync();
        var history = new Mock<IWarehouseProductChangeHistoryService>();
        history
            .Setup(value => value.CaptureSnapshotsAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(new Dictionary<string, WarehouseProductChangeSnapshotDto>());
        history
            .Setup(value => value.RecordChangesAsync(
                It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                It.IsAny<WarehouseProductChangeHistoryContextDto>(),
                It.IsAny<CancellationToken>()
            ))
            .ThrowsAsync(new InvalidOperationException("历史写入失败"));

        var result = await CreateService(history.Object).SaveSetCodeSnapshotAsync(
            new SaveStoreProductSetCodeSnapshotDto
            {
                ProductCode = productCode,
                StoreCode = StoreCode,
                ExpectedProductType = null,
                ProductType = 1,
                Items = new()
                {
                    new()
                    {
                        Barcode = "ROLLBACK-BARCODE",
                        RetailPrice = 10m,
                        SetType = 1,
                        IsActive = true,
                    },
                },
            },
            "测试用户",
            accessibleStoreCodes: null
        );

        Assert.False(result.Success);
        Assert.Empty(await _db.Queryable<ProductSetCode>()
            .Where(item => item.ProductCode == productCode && !item.IsDeleted)
            .ToListAsync());
        Assert.Null(await _db.Queryable<Product>()
            .Where(item => item.ProductCode == productCode)
            .Select(item => item.ProductType)
            .FirstAsync());
    }

    [Fact]
    public async Task GetCodes和套装编辑_严格区分类型并拒绝零售价()
    {
        const string productCode = "P-SET-CODE-GUARD";
        await SeedMultiCodeParentAsync(productCode, storePurchasePrice: 20m, productPurchasePrice: 20m);
        await EnsureActiveStoreAsync();
        var set = BuildMultiCodeSetCode(productCode, "SET", 10m, setType: 1);
        var multi = BuildMultiCodeSetCode(productCode, "MULTI", 20m, setType: 2);
        var firstByBarcode = BuildMultiCodeSetCode(productCode, "Z-ID", 11m, setType: 1);
        firstByBarcode.SetBarcode = "A-STABLE";
        await _db.Insertable(new[] { set, multi, firstByBarcode }).ExecuteCommandAsync();
        var service = CreateService();

        var setPage = await service.GetSetCodesAsync(productCode, StoreCode, 1, 50, null, null);
        var multiPage = await service.GetMultiCodesAsync(productCode, StoreCode, 1, 50, null, null);
        var create = await service.CreateSetCodeAsync(new()
        {
            ProductCode = productCode, StoreCode = StoreCode, ProductType = 1, Barcode = "ZERO", RetailPrice = 0m,
        }, "测试用户", null);
        var update = await service.UpdateSetCodeAsync(set.SetCodeId, new()
        {
            StoreCode = StoreCode, Barcode = set.SetBarcode!, RetailPrice = 0m,
        }, "测试用户", null);

        Assert.True(setPage.Success);
        Assert.All(setPage.Data!.Items, item => Assert.Equal(1, item.SetType));
        Assert.Equal(new[] { "A-STABLE", set.SetBarcode }, setPage.Data.Items.Select(item => item.SetBarcode).ToArray());
        Assert.True(multiPage.Success);
        Assert.Single(multiPage.Data!.Items);
        Assert.Equal(multi.SetCodeId, multiPage.Data.Items[0].SetCodeId);
        Assert.False(create.Success);
        Assert.False(update.Success);
    }

    [Fact]
    public async Task UpdateProductTypeAsync_锁内发现已有子码时拒绝类型切换()
    {
        const string productCode = "P-TYPE-SWITCH-GUARD";
        await SeedMultiCodeParentAsync(productCode, storePurchasePrice: 20m, productPurchasePrice: 20m);
        await _db.Insertable(BuildMultiCodeSetCode(productCode, "CHILD", 10m, setType: 1)).ExecuteCommandAsync();

        var result = await CreateService().UpdateProductTypeAsync(
            productCode, new() { ProductType = 2, StoreCode = StoreCode }, "测试用户", null
        );

        Assert.False(result.Success);
        Assert.Contains("不能直接切换", result.Message);
    }

    [Fact]
    public async Task SaveSetCodeSnapshotAsync_历史多码子项可统一修复为套装()
    {
        const string productCode = "P-SNAPSHOT-REPAIR-TYPE";
        await SeedMultiCodeParentAsync(productCode, 30m, 30m);
        await EnsureActiveStoreAsync();
        await _db.Updateable<Product>().SetColumns(x => x.ProductType == 1)
            .Where(x => x.ProductCode == productCode).ExecuteCommandAsync();
        var legacy = BuildMultiCodeSetCode(productCode, "LEGACY", 50m, 2);
        await _db.Insertable(legacy).ExecuteCommandAsync();

        var result = await CreateService().SaveSetCodeSnapshotAsync(new()
        {
            ProductCode = productCode, StoreCode = StoreCode, ExpectedProductType = 1, ProductType = 1,
            ExpectedItems = new() { SnapshotItem(legacy) },
            Items = new() { new() { SetCodeId = legacy.SetCodeId, Barcode = legacy.SetBarcode!, RetailPrice = 15m, SetType = 1, IsActive = true } },
        }, "测试用户", null);

        Assert.True(result.Success, result.Message);
        var persisted = await _db.Queryable<ProductSetCode>().SingleAsync(x => x.SetCodeId == legacy.SetCodeId);
        Assert.Equal(1, persisted.SetType);
        Assert.Equal(15m, persisted.SetRetailPrice);
    }

    [Fact]
    public async Task SaveSetCodeSnapshotAsync_历史空条码可修复为有效条码()
    {
        const string productCode = "P-SNAPSHOT-REPAIR-BARCODE";
        await SeedMultiCodeParentAsync(productCode, 30m, 30m);
        await EnsureActiveStoreAsync();
        await _db.Updateable<Product>().SetColumns(x => x.ProductType == 1)
            .Where(x => x.ProductCode == productCode).ExecuteCommandAsync();
        var legacy = BuildMultiCodeSetCode(productCode, "EMPTY", 10m, 1);
        legacy.SetBarcode = null;
        await _db.Insertable(legacy).ExecuteCommandAsync();

        var result = await CreateService().SaveSetCodeSnapshotAsync(new()
        {
            ProductCode = productCode, StoreCode = StoreCode, ExpectedProductType = 1, ProductType = 1,
            ExpectedItems = new() { SnapshotItem(legacy) },
            Items = new() { new() { SetCodeId = legacy.SetCodeId, Barcode = "REPAIRED", RetailPrice = 10m, SetType = 1, IsActive = true } },
        }, "测试用户", null);

        Assert.True(result.Success, result.Message);
        Assert.Equal("REPAIRED", await _db.Queryable<ProductSetCode>()
            .Where(x => x.SetCodeId == legacy.SetCodeId).Select(x => x.SetBarcode).SingleAsync());
    }

    [Fact]
    public async Task SaveSetCodeSnapshotAsync_跨店同步保留分店定价策略字段()
    {
        const string productCode = "P-SNAPSHOT-METADATA";
        await SeedMultiCodeParentAsync(productCode, 30m, 30m);
        await EnsureActiveStoreAsync();
        await _db.Updateable<Product>().SetColumns(x => x.ProductType == 1)
            .Where(x => x.ProductCode == productCode).ExecuteCommandAsync();
        var existing = BuildMultiCodeSetCode(productCode, "KEEP", 10m, 1);
        await _db.Insertable(existing).ExecuteCommandAsync();
        await _db.Insertable(new StoreMultiCodeProduct
        {
            UUID = "metadata-projection", StoreCode = StoreCode, ProductCode = productCode,
            MultiCodeProductCode = existing.SetProductCode, StoreMultiCodeProductCode = StoreCode + existing.SetProductCode,
            MultiBarcode = existing.SetBarcode, MultiCodeRetailPrice = 10m, DiscountRate = .2m,
            IsAutoPricing = true, IsSpecialProduct = true, IsActive = true, IsDeleted = false,
        }).ExecuteCommandAsync();

        var result = await CreateService().SaveSetCodeSnapshotAsync(new()
        {
            ProductCode = productCode, StoreCode = StoreCode, ExpectedProductType = 1, ProductType = 1,
            ExpectedItems = new() { SnapshotItem(existing) },
            Items = new() { new() { SetCodeId = existing.SetCodeId, Barcode = "UPDATED", RetailPrice = 12m, SetType = 1, IsActive = true } },
        }, "测试用户", null);

        Assert.True(result.Success, result.Message);
        var projection = await _db.Queryable<StoreMultiCodeProduct>().SingleAsync(x => x.UUID == "metadata-projection");
        Assert.Equal(.2m, projection.DiscountRate);
        Assert.True(projection.IsAutoPricing);
        Assert.True(projection.IsSpecialProduct);
        Assert.Equal("UPDATED", projection.MultiBarcode);
    }

    [Fact]
    public async Task SaveSetCodeSnapshotAsync_父类型不变仍写专用子码审计()
    {
        const string productCode = "P-SNAPSHOT-AUDIT";
        await SeedMultiCodeParentAsync(productCode, 30m, 30m);
        await EnsureActiveStoreAsync();
        await _db.Updateable<Product>().SetColumns(x => x.ProductType == 1)
            .Where(x => x.ProductCode == productCode).ExecuteCommandAsync();
        var existing = BuildMultiCodeSetCode(productCode, "BEFORE", 10m, 1);
        await _db.Insertable(existing).ExecuteCommandAsync();

        var result = await CreateService().SaveSetCodeSnapshotAsync(new()
        {
            ProductCode = productCode, StoreCode = StoreCode, ExpectedProductType = 1, ProductType = 1,
            ExpectedItems = new() { SnapshotItem(existing) },
            Items = new() { new() { SetCodeId = existing.SetCodeId, Barcode = "AFTER", RetailPrice = 12m, SetType = 1, IsActive = true } },
        }, "审计用户", null);

        Assert.True(result.Success, result.Message);
        var audit = await _db.Queryable<WarehouseProductChangeHistory>().SingleAsync(x =>
            x.ProductCode == productCode && x.Source == "StoreProductMaintenanceSetCodeSnapshot");
        Assert.Equal("审计用户", audit.ActorName);
        Assert.Contains("productSetCodes", audit.ChangesJson);
        Assert.Contains("BEFORE", audit.ChangesJson);
        Assert.Contains("AFTER", audit.ChangesJson);
    }

    [Fact]
    public async Task SaveSetCodeSnapshotAsync_大快照以批量查询和写入同步多门店投影()
    {
        const string productCode = "P-SNAPSHOT-BATCH";
        await SeedMultiCodeParentAsync(productCode, 30m, 30m);
        await EnsureActiveStoreAsync();
        await _db.Insertable(new Store { StoreGUID = "store-2-guid", StoreCode = "store-2", StoreName = "第二分店", IsActive = true, IsDeleted = false }).ExecuteCommandAsync();
        await _db.Insertable(new StoreRetailPrice { UUID = "batch-store-2", StoreCode = "store-2", ProductCode = productCode, StoreProductCode = "store-2-" + productCode, PurchasePrice = 30m, StoreRetailPriceValue = 50m, IsActive = true, IsDeleted = false }).ExecuteCommandAsync();
        var commands = 0;
        _db.Aop.OnLogExecuting = (_, _) => commands++;
        try
        {
            var result = await CreateService().SaveSetCodeSnapshotAsync(new()
            {
                ProductCode = productCode, StoreCode = StoreCode, ExpectedProductType = null, ProductType = 1,
                Items = Enumerable.Range(1, 520).Select(i => new SaveStoreProductSetCodeSnapshotItemDto
                {
                    Barcode = $"BATCH-{i:D3}", RetailPrice = 10m + i, SetType = 1, IsActive = true,
                }).ToList(),
            }, "测试用户", null);

            Assert.True(result.Success, result.Message);
            Assert.Equal(1040, await _db.Queryable<StoreMultiCodeProduct>().Where(x => x.ProductCode == productCode && !x.IsDeleted).CountAsync());
            Assert.True(commands < 50, $"批量快照执行了 {commands} 条 SQL，疑似逐条投影同步");
        }
        finally
        {
            _db.Aop.OnLogExecuting = null;
        }
    }

    [Fact]
    public async Task SyncWarehousePriceAsync_非供应商200不写入()
    {
        var updatedAt = DateTime.UtcNow.AddDays(-2);
        await SeedAsync("225", 5m, 10m, 0.9m, updatedAt, 6m, 12m);

        var result = await CreateService().SyncWarehousePriceAsync(
            StorePriceUuid,
            new SyncStoreProductWarehousePriceRequestDto(),
            "tester",
            new List<string> { StoreCode }
        );

        Assert.True(result.Success);
        Assert.Equal("not_applicable", result.Data!.Status);
        Assert.False(result.Data.PurchaseUpdated);
        Assert.False(result.Data.RetailUpdated);
        var entity = await GetStorePriceAsync();
        Assert.Equal(5m, entity.PurchasePrice);
        Assert.Equal(10m, entity.StoreRetailPriceValue);
        Assert.Equal(updatedAt, entity.UpdatedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncWarehousePriceAsync_仓库缺失或两个价格均无效时不覆盖(bool seedInvalidSource)
    {
        var updatedAt = DateTime.UtcNow.AddDays(-2);
        await SeedAsync("200", 5m, 10m, 0.9m, updatedAt);
        if (seedInvalidSource)
        {
            await InsertWarehousePriceAsync(0m, -1m);
        }

        var result = await CreateService().SyncWarehousePriceAsync(
            StorePriceUuid,
            new SyncStoreProductWarehousePriceRequestDto(),
            "tester",
            new List<string> { StoreCode }
        );

        Assert.True(result.Success);
        Assert.Equal("missing_source", result.Data!.Status);
        var entity = await GetStorePriceAsync();
        Assert.Equal(5m, entity.PurchasePrice);
        Assert.Equal(10m, entity.StoreRetailPriceValue);
        Assert.Equal(updatedAt, entity.UpdatedAt);
    }

    [Fact]
    public async Task SyncWarehousePriceAsync_仓库价格舍入为零时不覆盖分店价格()
    {
        var updatedAt = DateTime.UtcNow.AddDays(-2);
        await SeedAsync("200", 5m, 10m, 0.2m, updatedAt, 0.004m, 10m);

        var result = await CreateService().SyncWarehousePriceAsync(
            StorePriceUuid,
            new SyncStoreProductWarehousePriceRequestDto(),
            "tester",
            new List<string> { StoreCode }
        );

        Assert.True(result.Success);
        Assert.Equal("synced", result.Data!.Status);
        Assert.False(result.Data.PurchaseUpdated);
        var entity = await GetStorePriceAsync();
        Assert.Equal(5m, entity.PurchasePrice);
        Assert.Equal(updatedAt, entity.UpdatedAt);
    }

    [Fact]
    public async Task SyncWarehousePriceAsync_自动同步进货价且不触发自动定价并要求确认零售价()
    {
        await SeedAsync(" 200 ", 5m, 10m, 0.9m, DateTime.UtcNow.AddDays(-2), 6.5m, 12m);
        var original = await GetStorePriceAsync();
        original.IsAutoPricing = true;
        original.IsSpecialProduct = true;
        original.IsActive = false;
        await _db.Updateable(original).ExecuteCommandAsync();

        var result = await CreateService().SyncWarehousePriceAsync(
            StorePriceUuid,
            new SyncStoreProductWarehousePriceRequestDto(),
            "tester",
            new List<string> { StoreCode }
        );

        Assert.True(result.Success);
        Assert.Equal("confirmation_required", result.Data!.Status);
        Assert.True(result.Data.PurchaseUpdated);
        Assert.False(result.Data.RetailUpdated);
        Assert.True(result.Data.RetailConfirmationRequired);
        Assert.Equal(5m, result.Data.PreviousStorePurchasePrice);
        Assert.Equal(10m, result.Data.PreviousStoreRetailPrice);
        var entity = await GetStorePriceAsync();
        Assert.Equal(6.5m, entity.PurchasePrice);
        Assert.Equal(10m, entity.StoreRetailPriceValue);
        Assert.Equal(0.9m, entity.DiscountRate);
        Assert.True(entity.IsAutoPricing);
        Assert.True(entity.IsSpecialProduct);
        Assert.False(entity.IsActive);
        _autoPricingService.Verify(
            service => service.CalculateRetailPrice(It.IsAny<decimal>(), It.IsAny<PricingStrategy?>()),
            Times.Never
        );
    }

    [Fact]
    public async Task SyncWarehousePriceAsync_真实写入分支同事务入队并返回HqSync()
    {
        await SeedAsync("200", 5m, 10m, 0.9m, DateTime.UtcNow.AddDays(-2), 6.5m, 12m);
        var status = new ProductHqSyncOperationStatusDto
        {
            OperationId = "warehouse-operation",
            Status = ProductHqSyncOutboxStatuses.Pending,
            ProductCode = ProductCode,
            StoreCode = StoreCode,
            Retryable = true,
        };
        var writer = new Mock<IProductMaintenanceHqProjectionWriter>(MockBehavior.Strict);
        writer.Setup(item => item.EnqueueAsync(
                _db,
                It.Is<ProductMaintenanceHqMutationRequest>(request =>
                    request.OperationKind == ProductMaintenanceHqOperationKinds.WarehousePriceSynced
                    && request.ProductCode == ProductCode
                    && request.TargetStoreCodes!.SequenceEqual(new[] { StoreCode })
                    && request.AuthorizedStoreCodes!.SequenceEqual(new[] { StoreCode })
                    && request.FieldMask.SequenceEqual(
                        ProductMaintenanceHqFieldMasks.StorePriceAndMultiCode
                    )
                    && request.Tombstones.Count == 0
                    && request.RequestedByDeviceId == "test-device"
                    && request.RequestedByUserGuid == null
                ),
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(status);

        var result = await CreateService(hqProjectionWriter: writer.Object)
            .SyncWarehousePriceAsync(
                StorePriceUuid,
                new SyncStoreProductWarehousePriceRequestDto(),
                "device:test-device",
                new List<string> { StoreCode }
            );

        Assert.True(result.Success, result.Message);
        Assert.Same(status, result.Data!.HqSync);
        writer.VerifyAll();
    }

    [Fact]
    public async Task SyncWarehousePriceAsync_无变化成功分支不入队且HqSync为空()
    {
        await SeedAsync("225", 5m, 10m, 0.9m, DateTime.UtcNow.AddDays(-2), 6m, 12m);
        var writer = new Mock<IProductMaintenanceHqProjectionWriter>(MockBehavior.Strict);

        var result = await CreateService(hqProjectionWriter: writer.Object)
            .SyncWarehousePriceAsync(
                StorePriceUuid,
                new SyncStoreProductWarehousePriceRequestDto(),
                "device:test-device",
                new List<string> { StoreCode }
            );

        Assert.True(result.Success, result.Message);
        Assert.Null(result.Data!.HqSync);
        writer.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpsertClearancePriceAsync_空价格物理删除本地记录并同事务入队精确墓碑()
    {
        await SeedAsync("200", 5m, 10m, 0.9m, DateTime.UtcNow.AddDays(-2));
        await _db.Insertable(new StoreClearancePrice
        {
            UUID = "clearance-1",
            StoreCode = StoreCode,
            ProductCode = ProductCode,
            ClearanceBarcode = "CLR-1",
            ClearancePrice = 7.5m,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        var status = new ProductHqSyncOperationStatusDto
        {
            OperationId = "clearance-operation",
            Status = ProductHqSyncOutboxStatuses.Pending,
            ProductCode = ProductCode,
            StoreCode = StoreCode,
            Retryable = true,
        };
        var writer = new Mock<IProductMaintenanceHqProjectionWriter>(MockBehavior.Strict);
        writer.Setup(item => item.EnqueueAsync(
                _db,
                It.Is<ProductMaintenanceHqMutationRequest>(request =>
                    request.OperationKind == ProductMaintenanceHqOperationKinds.ClearancePriceDeleted
                    && request.TargetStoreCodes!.SequenceEqual(new[] { StoreCode })
                    && request.AuthorizedStoreCodes!.SequenceEqual(new[] { StoreCode })
                    && request.FieldMask.SequenceEqual(
                        new[] { ProductMaintenanceHqFieldMasks.StoreClearancePrice }
                    )
                    && request.Tombstones.Single().ResourceKind
                        == ProductMaintenanceHqResourceKinds.StoreClearancePrice
                    && request.Tombstones.Single().StoreCode == StoreCode
                    && request.Tombstones.Single().BusinessKey == "CLR-1"
                    && request.RequestedByDeviceId == "test-device"
                ),
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(status);

        var result = await CreateService(hqProjectionWriter: writer.Object)
            .UpsertClearancePriceAsync(
                ProductCode,
                new UpsertStoreProductClearancePriceDto
                {
                    StoreCode = StoreCode,
                    ClearancePrice = null,
                },
                "device:test-device",
                new List<string> { StoreCode }
            );

        Assert.True(result.Success, result.Message);
        Assert.Same(status, result.Data!.HqSync);
        Assert.Equal(0, await _db.Queryable<StoreClearancePrice>()
            .Where(item => item.ProductCode == ProductCode && item.StoreCode == StoreCode)
            .CountAsync());
        writer.VerifyAll();
    }

    [Fact]
    public async Task UpsertClearancePriceAsync_已有重复记录清空时删除全部业务键记录()
    {
        await SeedAsync("200", 5m, 10m, 0.9m, DateTime.UtcNow.AddDays(-2));
        await _db.Insertable(new[]
        {
            new StoreClearancePrice
            {
                UUID = "clearance-duplicate-a",
                StoreCode = StoreCode,
                ProductCode = ProductCode,
                ClearanceBarcode = "CLR-A",
                ClearancePrice = 7.5m,
                IsDeleted = false,
            },
            new StoreClearancePrice
            {
                UUID = "clearance-duplicate-b",
                StoreCode = StoreCode,
                ProductCode = ProductCode,
                ClearanceBarcode = "CLR-B",
                ClearancePrice = 8.5m,
                IsDeleted = false,
            },
        }).ExecuteCommandAsync();

        var result = await CreateService().UpsertClearancePriceAsync(
            ProductCode,
            new UpsertStoreProductClearancePriceDto
            {
                StoreCode = StoreCode,
                ClearancePrice = null,
            },
            "device:test-device",
            new List<string> { StoreCode }
        );

        Assert.True(result.Success, result.Message);
        Assert.Equal(0, await _db.Queryable<StoreClearancePrice>()
            .Where(item => item.ProductCode == ProductCode && item.StoreCode == StoreCode)
            .CountAsync());
    }

    [Fact]
    public async Task UpsertClearancePriceAsync_已有重复记录更新时收敛为单条最新记录()
    {
        await SeedAsync("200", 5m, 10m, 0.9m, DateTime.UtcNow.AddDays(-2));
        await _db.Insertable(new[]
        {
            new StoreClearancePrice
            {
                UUID = "clearance-duplicate-old",
                StoreCode = StoreCode,
                ProductCode = ProductCode,
                ClearanceBarcode = "CLR-OLD",
                ClearancePrice = 7.5m,
                CreatedAt = DateTime.UtcNow.AddDays(-2),
                IsDeleted = false,
            },
            new StoreClearancePrice
            {
                UUID = "clearance-duplicate-new",
                StoreCode = StoreCode,
                ProductCode = ProductCode,
                ClearanceBarcode = "CLR-NEW",
                ClearancePrice = 8.5m,
                CreatedAt = DateTime.UtcNow.AddDays(-1),
                IsDeleted = false,
            },
        }).ExecuteCommandAsync();

        var result = await CreateService().UpsertClearancePriceAsync(
            ProductCode,
            new UpsertStoreProductClearancePriceDto
            {
                StoreCode = StoreCode,
                ClearancePrice = 6.25m,
            },
            "device:test-device",
            new List<string> { StoreCode }
        );

        Assert.True(result.Success, result.Message);
        var rows = await _db.Queryable<StoreClearancePrice>()
            .Where(item => item.ProductCode == ProductCode && item.StoreCode == StoreCode)
            .ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(6.25m, row.ClearancePrice);
    }

    [Fact]
    public async Task SyncWarehousePriceAsync_主记录只写价格与更新审计列()
    {
        await SeedAsync("200", 5m, 10m, 0.25m, DateTime.UtcNow.AddDays(-2), 6.5m, 12m);
        var original = await GetStorePriceAsync();
        original.IsAutoPricing = true;
        original.IsSpecialProduct = true;
        original.IsActive = false;
        await _db.Updateable(original).ExecuteCommandAsync();
        string? storePriceUpdateSql = null;
        _db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (
                sql.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("StoreRetailPrice", StringComparison.OrdinalIgnoreCase)
            )
            {
                storePriceUpdateSql = sql;
            }
        };

        var result = await CreateService().SyncWarehousePriceAsync(
            StorePriceUuid,
            new SyncStoreProductWarehousePriceRequestDto(),
            "tester",
            new List<string> { StoreCode }
        );

        Assert.True(result.Success);
        Assert.NotNull(storePriceUpdateSql);
        var setClause = storePriceUpdateSql![..storePriceUpdateSql.IndexOf(
            "WHERE",
            StringComparison.OrdinalIgnoreCase
        )];
        Assert.Contains("PurchasePrice", setClause, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UpdatedAt", setClause, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UpdatedBy", setClause, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DiscountRate", setClause, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IsAutoPricing", setClause, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IsSpecialProduct", setClause, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IsActive", setClause, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CreatedAt", setClause, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CreatedBy", setClause, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SyncWarehousePriceAsync_权威更新锁固定按商品仓库分店价顺序获取()
    {
        await SeedAsync("200", 6.5m, 12m, 0.2m, DateTime.UtcNow.AddDays(-2), 6.5m, 12m);
        var authorityReadOrder = new List<string>();
        _db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (!sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (sql.Contains("StoreRetailPrice", StringComparison.OrdinalIgnoreCase))
            {
                authorityReadOrder.Add("StoreRetailPrice");
            }
            else if (sql.Contains("WarehouseProduct", StringComparison.OrdinalIgnoreCase))
            {
                authorityReadOrder.Add("WarehouseProduct");
            }
            else if (sql.Contains("Product", StringComparison.OrdinalIgnoreCase))
            {
                authorityReadOrder.Add("Product");
            }
        };

        var result = await CreateService().SyncWarehousePriceAsync(
            StorePriceUuid,
            new SyncStoreProductWarehousePriceRequestDto(),
            "tester",
            new List<string> { StoreCode }
        );

        Assert.True(result.Success);
        Assert.Equal(
            new[] { "StoreRetailPrice", "Product", "WarehouseProduct", "StoreRetailPrice" },
            authorityReadOrder.Take(4)
        );
    }

    [Fact]
    public async Task SyncWarehousePriceAsync_锁定重读变成无权分店时不返回价格数据()
    {
        await SeedAsync("200", 6.5m, 12m, 0.2m, DateTime.UtcNow.AddDays(-2), 6.5m, 12m);
        var storeChangedAfterLocatorRead = false;
        _db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (
                storeChangedAfterLocatorRead
                || !sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                || !(
                    sql.Contains("FROM `Product`", StringComparison.OrdinalIgnoreCase)
                    || sql.Contains("FROM \"Product\"", StringComparison.OrdinalIgnoreCase)
                    || sql.Contains("FROM [Product]", StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                return;
            }

            storeChangedAfterLocatorRead = true;
            _db.Ado.ExecuteCommand(
                "UPDATE StoreRetailPrice SET StoreCode = @storeCode WHERE UUID = @uuid",
                new { storeCode = "other-store", uuid = StorePriceUuid }
            );
        };

        var result = await CreateService().SyncWarehousePriceAsync(
            StorePriceUuid,
            new SyncStoreProductWarehousePriceRequestDto(),
            "tester",
            new List<string> { StoreCode }
        );

        Assert.True(storeChangedAfterLocatorRead);
        Assert.False(result.Success);
        Assert.Equal("当前账号或设备无权修改该分店商品", result.Message);
        Assert.Null(result.ErrorCode);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task SyncWarehousePriceAsync_条件更新未命中时返回价格冲突()
    {
        var updatedAt = DateTime.UtcNow.AddDays(-2);
        await SeedAsync("200", 5m, 10m, 0.25m, updatedAt, 6.5m, 12m);
        await _db.Ado.ExecuteCommandAsync(
            """
            CREATE TRIGGER ignore_store_price_update
            BEFORE UPDATE ON StoreRetailPrice
            BEGIN
                SELECT RAISE(IGNORE);
            END;
            """
        );

        var result = await CreateService().SyncWarehousePriceAsync(
            StorePriceUuid,
            new SyncStoreProductWarehousePriceRequestDto(),
            "tester",
            new List<string> { StoreCode }
        );

        Assert.False(result.Success);
        Assert.Equal("PRICE_VERSION_CONFLICT", result.ErrorCode);
        Assert.Equal(5m, result.Data!.StorePrice!.PurchasePrice);
        Assert.Equal(updatedAt, (await GetStorePriceAsync()).UpdatedAt);
    }

    [Fact]
    public async Task SyncWarehousePriceAsync_确认零售价保留折扣并同步派生记录()
    {
        await SeedAsync("200", 5m, 10m, 0.2m, DateTime.UtcNow.AddDays(-2), 6.5m, 12m);
        await SeedProjectedSetCodeAsync();
        var service = CreateService();

        var preview = await service.SyncWarehousePriceAsync(
            StorePriceUuid,
            new SyncStoreProductWarehousePriceRequestDto(),
            "tester",
            new List<string> { StoreCode }
        );
        Assert.Equal("confirmation_required", preview.Data!.Status);

        var result = await service.SyncWarehousePriceAsync(
            StorePriceUuid,
            CreateConfirmRequest(6.5m, 12m, 6.5m, 10m, 0.2m),
            "tester",
            new List<string> { StoreCode }
        );

        Assert.True(result.Success);
        Assert.Equal("synced", result.Data!.Status);
        Assert.False(result.Data.PurchaseUpdated);
        Assert.True(result.Data.RetailUpdated);
        Assert.False(result.Data.RetailConfirmationRequired);
        Assert.Equal(0.2m, result.Data.DiscountRate);
        Assert.Equal(8m, result.Data.PreviousDiscountedRetailPrice);
        Assert.Equal(9.6m, result.Data.NewDiscountedRetailPrice);
        var entity = await GetStorePriceAsync();
        Assert.Equal(6.5m, entity.PurchasePrice);
        Assert.Equal(12m, entity.StoreRetailPriceValue);
        Assert.Equal(0.2m, entity.DiscountRate);
        var projection = await _db.Queryable<StoreMultiCodeProduct>()
            .Where(x => x.UUID == "projection-1")
            .FirstAsync();
        Assert.Equal(6.5m, projection.PurchasePrice);
        Assert.Equal(12m, projection.MultiCodeRetailPrice);
    }

    [Fact]
    public async Task SyncWarehousePriceAsync_停用多码关系与投影不更新并保留创建审计()
    {
        await SeedAsync("200", 5m, 10m, 0.2m, DateTime.UtcNow.AddDays(-2), 6.5m, 12m);
        await SeedProjectedSetCodeAsync();
        var createdAt = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var projection = await _db.Queryable<StoreMultiCodeProduct>()
            .Where(x => x.UUID == "projection-1")
            .FirstAsync();
        projection.DiscountRate = 0.25m;
        projection.IsAutoPricing = true;
        projection.IsSpecialProduct = true;
        projection.IsActive = false;
        projection.CreatedAt = createdAt;
        projection.CreatedBy = "original-creator";
        await _db.Updateable(projection).ExecuteCommandAsync();
        await _db.Updateable<ProductSetCode>()
            .SetColumns(x => x.IsActive == false)
            .Where(x => x.SetCodeId == "set-1")
            .ExecuteCommandAsync();
        var service = CreateService();

        var preview = await service.SyncWarehousePriceAsync(
            StorePriceUuid,
            new SyncStoreProductWarehousePriceRequestDto(),
            "tester",
            new List<string> { StoreCode }
        );
        Assert.Equal("confirmation_required", preview.Data!.Status);
        var result = await service.SyncWarehousePriceAsync(
            StorePriceUuid,
            CreateConfirmRequest(6.5m, 12m, 6.5m, 10m, 0.2m),
            "tester",
            new List<string> { StoreCode }
        );

        Assert.True(result.Success);
        var latest = await _db.Queryable<StoreMultiCodeProduct>()
            .Where(x => x.UUID == "projection-1")
            .FirstAsync();
        Assert.Equal(5m, latest.PurchasePrice);
        Assert.Equal(10m, latest.MultiCodeRetailPrice);
        Assert.Equal(0.25m, latest.DiscountRate);
        Assert.True(latest.IsAutoPricing);
        Assert.True(latest.IsSpecialProduct);
        Assert.False(latest.IsActive);
        Assert.Equal(createdAt, latest.CreatedAt);
        Assert.Equal("original-creator", latest.CreatedBy);
    }

    [Fact]
    public async Task SyncWarehousePriceAsync_价格已一致时幂等且不刷新UpdatedAt()
    {
        var updatedAt = DateTime.UtcNow.AddDays(-2);
        await SeedAsync("200", 6.5m, 12m, 0.8m, updatedAt, 6.504m, 12.004m);

        var result = await CreateService().SyncWarehousePriceAsync(
            StorePriceUuid,
            new SyncStoreProductWarehousePriceRequestDto(),
            "tester",
            new List<string> { StoreCode }
        );

        Assert.True(result.Success);
        Assert.Equal("synced", result.Data!.Status);
        Assert.False(result.Data.PurchaseUpdated);
        Assert.False(result.Data.RetailUpdated);
        Assert.False(result.Data.RetailConfirmationRequired);
        Assert.Equal(updatedAt, (await GetStorePriceAsync()).UpdatedAt);
    }

    [Fact]
    public async Task SyncWarehousePriceAsync_确认时分店快照变化返回冲突且零写入()
    {
        var updatedAt = DateTime.UtcNow.AddDays(-2);
        await SeedAsync("200", 5m, 10m, 0.8m, updatedAt, 6.5m, 12m);

        var result = await CreateService().SyncWarehousePriceAsync(
            StorePriceUuid,
            CreateConfirmRequest(6.5m, 12m, 5m, 9m, 0.8m),
            "tester",
            new List<string> { StoreCode }
        );

        Assert.False(result.Success);
        Assert.Equal("PRICE_VERSION_CONFLICT", result.ErrorCode);
        Assert.Equal(10m, result.Data!.StorePrice!.RetailPrice);
        var entity = await GetStorePriceAsync();
        Assert.Equal(5m, entity.PurchasePrice);
        Assert.Equal(10m, entity.StoreRetailPriceValue);
        Assert.Equal(updatedAt, entity.UpdatedAt);
    }

    [Fact]
    public async Task SyncWarehousePriceAsync_确认时仓库快照变化返回冲突且零写入()
    {
        var updatedAt = DateTime.UtcNow.AddDays(-2);
        await SeedAsync("200", 5m, 10m, 0.8m, updatedAt, 6.5m, 12m);

        var result = await CreateService().SyncWarehousePriceAsync(
            StorePriceUuid,
            CreateConfirmRequest(6.5m, 11m, 5m, 10m, 0.8m),
            "tester",
            new List<string> { StoreCode }
        );

        Assert.False(result.Success);
        Assert.Equal("PRICE_VERSION_CONFLICT", result.ErrorCode);
        Assert.Equal(12m, result.Data!.WarehouseRetailPrice);
        Assert.Equal(updatedAt, (await GetStorePriceAsync()).UpdatedAt);
    }

    [Fact]
    public async Task SyncWarehousePriceAsync_拒绝无权分店且不写入()
    {
        var updatedAt = DateTime.UtcNow.AddDays(-2);
        await SeedAsync("200", 5m, 10m, 0.8m, updatedAt, 6.5m, 12m);

        var result = await CreateService().SyncWarehousePriceAsync(
            StorePriceUuid,
            new SyncStoreProductWarehousePriceRequestDto(),
            "tester",
            new List<string> { "other-store" }
        );

        Assert.False(result.Success);
        Assert.Contains("无权", result.Message);
        Assert.Equal(updatedAt, (await GetStorePriceAsync()).UpdatedAt);
    }

    [Fact]
    public async Task SyncWarehousePriceAsync_派生记录更新失败时回滚主价格()
    {
        await SeedAsync("200", 5m, 10m, 0.8m, DateTime.UtcNow.AddDays(-2), 5m, 12m);
        await SeedProjectedSetCodeAsync();
        await _db.Ado.ExecuteCommandAsync(
            """
            CREATE TRIGGER fail_projection_update
            BEFORE UPDATE ON StoreMultiCodeProduct
            BEGIN
                SELECT RAISE(ABORT, 'projection failure');
            END;
            """
        );

        var result = await CreateService().SyncWarehousePriceAsync(
            StorePriceUuid,
            CreateConfirmRequest(5m, 12m, 5m, 10m, 0.8m),
            "tester",
            new List<string> { StoreCode }
        );

        Assert.False(result.Success);
        Assert.Equal("仓库价格对账失败，请稍后重试", result.Message);
        Assert.DoesNotContain("projection failure", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10m, (await GetStorePriceAsync()).StoreRetailPriceValue);
        var projection = await _db.Queryable<StoreMultiCodeProduct>()
            .Where(x => x.UUID == "projection-1")
            .FirstAsync();
        Assert.Equal(10m, projection.MultiCodeRetailPrice);
    }

    [Fact]
    public async Task Controller_快照冲突返回409并携带最新数据()
    {
        var latest = new SyncStoreProductWarehousePriceResultDto
        {
            Status = "confirmation_required",
            WarehouseRetailPrice = 12m,
        };
        var conflict = new ApiResponse<SyncStoreProductWarehousePriceResultDto>
        {
            Success = false,
            ErrorCode = "PRICE_VERSION_CONFLICT",
            Message = "价格已变化",
            Data = latest,
        };
        var service = new Mock<IStoreProductMaintenanceReactService>();
        service
            .Setup(value => value.SyncWarehousePriceAsync(
                StorePriceUuid,
                It.IsAny<SyncStoreProductWarehousePriceRequestDto>(),
                It.IsAny<string>(),
                null
            ))
            .ReturnsAsync(conflict);
        var controller = new ReactStoreProductMaintenanceController(
            service.Object,
            Mock.Of<IDeviceRegistrationService>(),
            Mock.Of<IMapper>(),
            CreateSqlSugarContext(_db),
            NullLogger<ReactStoreProductMaintenanceController>.Instance,
            CreateSuccessfulAuthorizationService()
        )
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[]
                        {
                            new Claim(ClaimTypes.Name, "admin"),
                            new Claim(ClaimTypes.Role, "Admin"),
                        },
                        "test"
                    )),
                },
            },
        };

        var response = await controller.SyncWarehousePrice(
            StorePriceUuid,
            new SyncStoreProductWarehousePriceRequestDto { ConfirmRetailPrice = true }
        );

        var objectResult = Assert.IsType<ConflictObjectResult>(response);
        var body = Assert.IsType<ApiResponse<SyncStoreProductWarehousePriceResultDto>>(
            objectResult.Value
        );
        Assert.Equal("PRICE_VERSION_CONFLICT", body.ErrorCode);
        Assert.Same(latest, body.Data);
    }

    [Fact]
    public async Task UpdateMultiCodeAsync_Type1忽略提交成本并按当前门店整组比例重算()
    {
        const string productCode = "P-TYPE1";
        await SeedMultiCodeParentAsync(productCode, storePurchasePrice: 30m, productPurchasePrice: 90m);
        await _db.Insertable(new[]
        {
            BuildMultiCodeSetCode(productCode, "A", 10m, setType: 1),
            BuildMultiCodeSetCode(productCode, "B", 20m, setType: 1),
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            BuildMultiCodeStoreRow(productCode, "A", 10m, 1m),
            BuildMultiCodeStoreRow(productCode, "B", 20m, 1m),
        }).ExecuteCommandAsync();

        var response = await CreateService().UpdateMultiCodeAsync(
            MultiCodeRowUuid(productCode, "A"),
            new UpdateStoreProductMultiCodeDto { PurchasePrice = 999m, RetailPrice = 20m },
            "测试用户",
            new List<string> { StoreCode }
        );

        Assert.True(response.Success, response.Message);
        var rows = await _db.Queryable<StoreMultiCodeProduct>()
            .Where(x => x.ProductCode == productCode && !x.IsDeleted)
            .OrderBy(x => x.MultiCodeProductCode)
            .ToListAsync();
        Assert.Equal(new[] { 15m, 15m }, rows.Select(x => x.PurchasePrice!.Value).ToArray());
        Assert.Equal(20m, rows[0].MultiCodeRetailPrice);
        Assert.Equal(15m, response.Data!.PurchasePrice);
    }

    [Fact]
    public async Task UpdateMultiCodeAsync_Type2忽略提交成本并最终等于门店父成本()
    {
        const string productCode = "P-TYPE2";
        await SeedMultiCodeParentAsync(productCode, storePurchasePrice: 42m, productPurchasePrice: 30m);
        await _db.Insertable(BuildMultiCodeSetCode(productCode, "A", 18m, setType: 2))
            .ExecuteCommandAsync();
        await _db.Insertable(BuildMultiCodeStoreRow(productCode, "A", 18m, 1m))
            .ExecuteCommandAsync();

        var response = await CreateService().UpdateMultiCodeAsync(
            MultiCodeRowUuid(productCode, "A"),
            new UpdateStoreProductMultiCodeDto { PurchasePrice = -999m, RetailPrice = 18m },
            "测试用户",
            new List<string> { StoreCode }
        );

        Assert.True(response.Success, response.Message);
        var persisted = await _db.Queryable<StoreMultiCodeProduct>()
            .SingleAsync(x => x.UUID == MultiCodeRowUuid(productCode, "A"));
        Assert.Equal(42m, persisted.PurchasePrice);
        Assert.Equal(42m, response.Data!.PurchasePrice);
    }

    [Fact]
    public async Task UpdateMultiCodeAsync_停用Type2忽略提交成本且不触发成本更新()
    {
        const string productCode = "P-TYPE2-INACTIVE";
        await SeedMultiCodeParentAsync(productCode, storePurchasePrice: 42m, productPurchasePrice: 30m);
        var setCode = BuildMultiCodeSetCode(productCode, "A", 18m, setType: 2);
        setCode.IsActive = false;
        await _db.Insertable(setCode).ExecuteCommandAsync();
        await _db.Insertable(BuildMultiCodeStoreRow(productCode, "A", 18m, 7m))
            .ExecuteCommandAsync();

        var response = await CreateService().UpdateMultiCodeAsync(
            MultiCodeRowUuid(productCode, "A"),
            new UpdateStoreProductMultiCodeDto { PurchasePrice = 999m, RetailPrice = 19m },
            "测试用户",
            new List<string> { StoreCode }
        );

        Assert.True(response.Success, response.Message);
        var persisted = await _db.Queryable<StoreMultiCodeProduct>()
            .SingleAsync(x => x.UUID == MultiCodeRowUuid(productCode, "A"));
        Assert.Equal(7m, persisted.PurchasePrice);
        Assert.Equal(19m, persisted.MultiCodeRetailPrice);
    }

    [Fact]
    public async Task UpdateMultiCodeAsync_普通多码遇到同组无法重算时回滚且不复活软删除子项()
    {
        const string productCode = "P-ROLLBACK";
        await SeedMultiCodeParentAsync(productCode, storePurchasePrice: 30m, productPurchasePrice: 30m);
        await _db.Insertable(new[]
        {
            BuildMultiCodeSetCode(productCode, "A", 10m, setType: 1),
            BuildMultiCodeSetCode(productCode, "B", 20m, setType: 1),
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            BuildMultiCodeStoreRow(productCode, "A", 10m, 1m),
            BuildMultiCodeStoreRow(productCode, "B", 20m, 2m, isDeleted: true),
            BuildMultiCodeStoreRow(productCode, "NORMAL", 8m, 5m),
        }).ExecuteCommandAsync();

        var response = await CreateService().UpdateMultiCodeAsync(
            MultiCodeRowUuid(productCode, "NORMAL"),
            new UpdateStoreProductMultiCodeDto { PurchasePrice = 12m, RetailPrice = 15m },
            "测试用户",
            new List<string> { StoreCode }
        );

        Assert.False(response.Success);
        Assert.Contains("无法完整重算", response.Message);
        var ordinary = await _db.Queryable<StoreMultiCodeProduct>()
            .SingleAsync(x => x.UUID == MultiCodeRowUuid(productCode, "NORMAL"));
        var deleted = await _db.Queryable<StoreMultiCodeProduct>()
            .SingleAsync(x => x.UUID == MultiCodeRowUuid(productCode, "B"));
        Assert.Equal(5m, ordinary.PurchasePrice);
        Assert.Equal(8m, ordinary.MultiCodeRetailPrice);
        Assert.True(deleted.IsDeleted);
        Assert.Equal(2m, deleted.PurchasePrice);
    }

    [Fact]
    public async Task UpdateMultiCodeAsync_软删除目标保持删除且不接受更新()
    {
        const string productCode = "P-DELETED";
        await SeedMultiCodeParentAsync(productCode, storePurchasePrice: 30m, productPurchasePrice: 30m);
        await _db.Insertable(
                BuildMultiCodeStoreRow(productCode, "A", 10m, 1m, isDeleted: true)
            )
            .ExecuteCommandAsync();

        var response = await CreateService().UpdateMultiCodeAsync(
            MultiCodeRowUuid(productCode, "A"),
            new UpdateStoreProductMultiCodeDto { PurchasePrice = 999m, RetailPrice = 999m },
            "测试用户",
            new List<string> { StoreCode }
        );

        Assert.False(response.Success);
        var persisted = await _db.Queryable<StoreMultiCodeProduct>()
            .SingleAsync(x => x.UUID == MultiCodeRowUuid(productCode, "A"));
        Assert.True(persisted.IsDeleted);
        Assert.Equal(1m, persisted.PurchasePrice);
        Assert.Equal(10m, persisted.MultiCodeRetailPrice);
    }

    [Fact]
    public void UpdateMultiCodeAsync_锁内复读完整关系身份并按精确门店商品组重算()
    {
        var sourcePath = ResolveStoreProductMaintenanceServiceSourcePath();
        var source = File.ReadAllText(sourcePath);
        var methodStart = source.IndexOf("UpdateMultiCodeAsync(", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("CreateSetCodeAsync(", methodStart, StringComparison.Ordinal);
        var methodSource = source[methodStart..methodEnd];

        Assert.Contains("expectedStoreCode", methodSource);
        Assert.Contains("expectedProductCode", methodSource);
        Assert.Contains("expectedMultiCodeProductCode", methodSource);
        Assert.Contains("x.SetType == 1 || x.SetType == 2", methodSource);
        Assert.Contains("RecalculateStoreGroupsLockedAsync", methodSource);
    }

    private static string ResolveStoreProductMaintenanceServiceSourcePath(
        [CallerFilePath] string testSourcePath = ""
    )
    {
        // 以测试源码位置为锚点，避免 linked worktree 或 --artifacts-path 改变运行目录后误判仓库根。
        var testProjectDirectory = Path.GetDirectoryName(testSourcePath)
            ?? throw new InvalidOperationException("无法定位商品维护测试源码目录。");

        return Path.GetFullPath(
            Path.Combine(
                testProjectDirectory,
                "..",
                "BlazorApp.Api",
                "Services",
                "React",
                "StoreProductMaintenanceReactService.cs"
            )
        );
    }

    public void Dispose()
    {
        _cache.Dispose();
        _db.Dispose();
        _sqliteConnection.Dispose();
        if (File.Exists(_dbPath))
        {
            SqliteTempFileCleanup.DeleteIfExists(_dbPath);
        }
    }

    private StoreProductMaintenanceReactService CreateService(
        IWarehouseProductChangeHistoryService? historyService = null,
        IProductMaintenanceHqProjectionWriter? hqProjectionWriter = null,
        ICurrentUserService? currentUserService = null
    )
    {
        return new StoreProductMaintenanceReactService(
            CreateSqlSugarContext(_db),
            NullLogger<StoreProductMaintenanceReactService>.Instance,
            _autoPricingService.Object,
            _cache,
            historyService ?? WarehouseProductChangeHistoryTestDouble.CreateNoop(),
            currentUserService ?? Mock.Of<ICurrentUserService>(),
            hqProjectionWriter ?? CreateSuccessfulProjectionWriter()
        );
    }

    private static (
        Mock<IProductMaintenanceHqProjectionWriter> Writer,
        ProductHqSyncOperationStatusDto Status
    ) CreateCapturingProjectionWriter(
        List<(ISqlSugarClient Db, ProductMaintenanceHqMutationRequest Request)> calls
    )
    {
        var status = new ProductHqSyncOperationStatusDto
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Status = ProductHqSyncOutboxStatuses.Pending,
            Retryable = true,
        };
        var writer = new Mock<IProductMaintenanceHqProjectionWriter>(MockBehavior.Strict);
        writer.Setup(item => item.EnqueueAsync(
                It.IsAny<ISqlSugarClient>(),
                It.IsAny<ProductMaintenanceHqMutationRequest>(),
                It.IsAny<CancellationToken>()
            ))
            .Callback<ISqlSugarClient, ProductMaintenanceHqMutationRequest, CancellationToken>(
                (db, request, _) => calls.Add((db, request))
            )
            .ReturnsAsync(status);
        return (writer, status);
    }

    private ProductMaintenanceHqMutationRequest AssertMutationRequest(
        List<(ISqlSugarClient Db, ProductMaintenanceHqMutationRequest Request)> calls,
        string operationKind,
        IReadOnlyCollection<string>? targetStoreCodes,
        IReadOnlyCollection<string>? authorizedStoreCodes,
        IReadOnlyCollection<string> fieldMask
    )
    {
        var call = Assert.Single(calls);
        Assert.Same(_db, call.Db);
        Assert.Equal(operationKind, call.Request.OperationKind);
        Assert.Equal(targetStoreCodes, call.Request.TargetStoreCodes);
        Assert.Equal(authorizedStoreCodes, call.Request.AuthorizedStoreCodes);
        Assert.Equal(fieldMask, call.Request.FieldMask);
        return call.Request;
    }

    private static IProductMaintenanceHqProjectionWriter CreateSuccessfulProjectionWriter()
    {
        var writer = new Mock<IProductMaintenanceHqProjectionWriter>();
        writer.Setup(item => item.EnqueueAsync(
                It.IsAny<ISqlSugarClient>(),
                It.IsAny<ProductMaintenanceHqMutationRequest>(),
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(new ProductHqSyncOperationStatusDto
            {
                OperationId = Guid.NewGuid().ToString("N"),
                Status = ProductHqSyncOutboxStatuses.Pending,
                Retryable = true,
            });
        return writer.Object;
    }

    private static IAuthorizationService CreateSuccessfulAuthorizationService()
    {
        var authorization = new Mock<IAuthorizationService>();
        authorization.Setup(item => item.AuthorizeAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<object?>(),
                It.IsAny<string>()
            ))
            .ReturnsAsync(AuthorizationResult.Success());
        return authorization.Object;
    }

    private async Task EnsureActiveStoreAsync()
    {
        if (!await _db.Queryable<Store>().Where(item => item.StoreCode == StoreCode).AnyAsync())
        {
            await _db.Insertable(new Store
            {
                StoreGUID = $"{StoreCode}-GUID",
                StoreCode = StoreCode,
                StoreName = "测试分店",
                IsActive = true,
                IsDeleted = false,
            }).ExecuteCommandAsync();
        }
    }

    private async Task SeedMultiCodeParentAsync(
        string productCode,
        decimal storePurchasePrice,
        decimal productPurchasePrice
    )
    {
        await _db.Insertable(new Product
        {
            UUID = $"{productCode}-PRODUCT",
            ProductCode = productCode,
            ProductName = productCode,
            LocalSupplierCode = "200",
            PurchasePrice = productPurchasePrice,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _db.Insertable(new StoreRetailPrice
        {
            UUID = $"{productCode}-PRICE",
            StoreCode = StoreCode,
            ProductCode = productCode,
            StoreProductCode = $"{StoreCode}-{productCode}",
            SupplierCode = "200",
            PurchasePrice = storePurchasePrice,
            StoreRetailPriceValue = 50m,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private static ProductSetCode BuildMultiCodeSetCode(
        string productCode,
        string childCode,
        decimal retailPrice,
        int setType
    ) => new()
    {
        SetCodeId = $"{productCode}-{childCode}-SET",
        ProductCode = productCode,
        SetProductCode = MultiCodeChildProductCode(productCode, childCode),
        SetItemNumber = $"ITEM-{productCode}-{childCode}",
        SetBarcode = $"BAR-{productCode}-{childCode}",
        SetPurchasePrice = 1m,
        SetRetailPrice = retailPrice,
        SetQuantity = 1,
        SetType = setType,
        IsActive = true,
        IsDeleted = false,
    };

    private static SaveStoreProductSetCodeSnapshotItemDto SnapshotItem(ProductSetCode item) => new()
    {
        SetCodeId = item.SetCodeId,
        Barcode = item.SetBarcode ?? string.Empty,
        RetailPrice = item.SetRetailPrice,
        SetType = item.SetType,
        IsActive = item.IsActive,
    };

    private static StoreMultiCodeProduct BuildMultiCodeStoreRow(
        string productCode,
        string childCode,
        decimal retailPrice,
        decimal purchasePrice,
        bool isDeleted = false
    ) => new()
    {
        UUID = MultiCodeRowUuid(productCode, childCode),
        StoreCode = StoreCode,
        ProductCode = productCode,
        MultiCodeProductCode = MultiCodeChildProductCode(productCode, childCode),
        StoreMultiCodeProductCode = $"{StoreCode}-{productCode}-{childCode}",
        MultiBarcode = $"BAR-{productCode}-{childCode}",
        PurchasePrice = purchasePrice,
        MultiCodeRetailPrice = retailPrice,
        IsActive = true,
        IsDeleted = isDeleted,
    };

    private static string MultiCodeRowUuid(string productCode, string childCode) =>
        $"{productCode}-{childCode}-ROW";

    private static string MultiCodeChildProductCode(string productCode, string childCode) =>
        $"{productCode}-{childCode}-CHILD";

    private async Task SeedAsync(
        string localSupplierCode,
        decimal? storePurchasePrice,
        decimal? storeRetailPrice,
        decimal? discountRate,
        DateTime updatedAt,
        decimal? warehousePurchasePrice = null,
        decimal? warehouseRetailPrice = null
    )
    {
        await _db.Insertable(new Store
        {
            StoreGUID = "store-guid-1",
            StoreCode = StoreCode,
            StoreName = "测试分店",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _db.Insertable(new Product
        {
            UUID = "product-uuid-1",
            ProductCode = ProductCode,
            ProductName = "测试商品",
            LocalSupplierCode = localSupplierCode,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _db.Insertable(new StoreRetailPrice
        {
            UUID = StorePriceUuid,
            StoreCode = StoreCode,
            ProductCode = ProductCode,
            StoreProductCode = $"{StoreCode}-{ProductCode}",
            SupplierCode = localSupplierCode,
            PurchasePrice = storePurchasePrice,
            StoreRetailPriceValue = storeRetailPrice,
            DiscountRate = discountRate,
            IsAutoPricing = false,
            IsSpecialProduct = false,
            IsActive = true,
            IsDeleted = false,
            CreatedAt = updatedAt.AddDays(-1),
            UpdatedAt = updatedAt,
            UpdatedBy = "seed",
        }).ExecuteCommandAsync();

        if (warehousePurchasePrice.HasValue || warehouseRetailPrice.HasValue)
        {
            await InsertWarehousePriceAsync(warehousePurchasePrice, warehouseRetailPrice);
        }
    }

    private Task<int> InsertWarehousePriceAsync(
        decimal? warehousePurchasePrice,
        decimal? warehouseRetailPrice
    )
    {
        return _db.Insertable(new WarehouseProduct
        {
            ProductCode = ProductCode,
            ImportPrice = warehousePurchasePrice,
            OEMPrice = warehouseRetailPrice,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task SeedProjectedSetCodeAsync()
    {
        await _db.Insertable(new ProductSetCode
        {
            SetCodeId = "set-1",
            ProductCode = ProductCode,
            SetProductCode = "set-product-1",
            SetItemNumber = "set-item-1",
            SetBarcode = "set-barcode-1",
            SetType = 2,
            SetQuantity = 1,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _db.Insertable(new StoreMultiCodeProduct
        {
            UUID = "projection-1",
            StoreCode = StoreCode,
            ProductCode = ProductCode,
            MultiCodeProductCode = "set-product-1",
            StoreMultiCodeProductCode = $"{StoreCode}set-product-1",
            MultiBarcode = "set-barcode-1",
            PurchasePrice = 5m,
            MultiCodeRetailPrice = 10m,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private Task<StoreRetailPrice> GetStorePriceAsync()
    {
        return _db.Queryable<StoreRetailPrice>()
            .Where(x => x.UUID == StorePriceUuid)
            .FirstAsync();
    }

    private static SyncStoreProductWarehousePriceRequestDto CreateConfirmRequest(
        decimal? warehousePurchasePrice,
        decimal? warehouseRetailPrice,
        decimal? storePurchasePrice,
        decimal? storeRetailPrice,
        decimal? discountRate
    ) => new()
    {
        ConfirmRetailPrice = true,
        ExpectedWarehousePurchasePrice = warehousePurchasePrice,
        ExpectedWarehouseRetailPrice = warehouseRetailPrice,
        ExpectedStorePurchasePrice = storePurchasePrice,
        ExpectedStoreRetailPrice = storeRetailPrice,
        ExpectedDiscountRate = discountRate,
    };

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }
}
