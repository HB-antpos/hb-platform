using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class ProductChangeHistoryLegacyEntryPointTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public ProductChangeHistoryLegacyEntryPointTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(
            typeof(Product),
            typeof(Store),
            typeof(StoreRetailPrice),
            typeof(ProductSetCode),
            typeof(DomesticProduct),
            typeof(WarehouseProduct)
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
    }

    [Fact]
    public async Task CreateWithPrices_使用服务端身份记录创建历史上下文()
    {
        var currentUser = CreateCurrentUserService("server-user-guid", "服务端创建者");
        var history = new WarehouseProductChangeHistoryService(
            CreateSqlSugarContext(_db),
            NullLogger<WarehouseProductChangeHistoryService>.Instance,
            currentUser
        );
        var controller = CreateController(history, currentUser);

        var result = await controller.CreateWithPrices(new CreateProductWithPricesDto
        {
            ProductName = "历史创建商品",
            PurchasePrice = 1.2m,
            RetailPrice = 2.3m,
            IsAutoPricing = false,
        });

        Assert.IsType<OkObjectResult>(result);
        var historyEvent = Assert.Single(
            await _db.Queryable<WarehouseProductChangeHistory>().ToListAsync()
        );
        Assert.Equal("Create", historyEvent.Action);
        Assert.Equal("ProductLegacyCreateWithPrices", historyEvent.Source);
        Assert.Equal("server-user-guid", historyEvent.ActorUserGuid);
        Assert.Equal("服务端创建者", historyEvent.ActorName);
        Assert.Equal("User", historyEvent.ActorType);
    }

    [Fact]
    public async Task CreateWithPrices_历史失败时回滚商品和门店价格()
    {
        await SeedStoreAsync();
        var history = CreateHistoryService(throwWhenRecording: true);
        var controller = CreateController(history.Object, "server-user-guid", "服务端创建者");

        var result = await controller.CreateWithPrices(new CreateProductWithPricesDto
        {
            ProductName = "应回滚的创建商品",
            PurchasePrice = 1.2m,
            RetailPrice = 2.3m,
            IsAutoPricing = false,
        });

        Assert.IsType<ObjectResult>(result);
        Assert.Equal(0, await _db.Queryable<Product>().CountAsync());
        Assert.Equal(0, await _db.Queryable<StoreRetailPrice>().CountAsync());
    }

    [Fact]
    public async Task UpdateProductTypeAsync_使用服务端身份记录更新历史上下文()
    {
        await SeedProductAndDomesticProductAsync(0);
        var contexts = new List<WarehouseProductChangeHistoryContextDto>();
        var service = CreateStoreMaintenanceService(
            CreateHistoryService(contexts).Object,
            "server-user-guid",
            "服务端更新者"
        );

        var result = await service.UpdateProductTypeAsync(
            "product-1",
            new UpdateStoreProductTypeDto { ProductType = 1 },
            "请求传入的名称",
            null
        );

        Assert.True(result.Success, result.Message);
        var context = Assert.Single(contexts);
        Assert.Equal("Update", context.Action);
        Assert.Equal("StoreProductMaintenance", context.Source);
        Assert.Equal("product-1", context.SourceReference);
        Assert.Equal("server-user-guid", context.ActorUserGuid);
        Assert.Equal("服务端更新者", context.ActorName);
        Assert.Equal("User", context.ActorType);
        var product = await _db.Queryable<Product>()
            .SingleAsync(item => item.ProductCode == "product-1");
        var domesticProduct = await _db.Queryable<DomesticProduct>()
            .SingleAsync(item => item.ProductCode == "product-1");
        Assert.Equal("服务端更新者", product.UpdatedBy);
        Assert.Equal("服务端更新者", domesticProduct.UpdatedBy);
    }

    [Fact]
    public async Task UpdateProductTypeAsync_历史失败时回滚Product和DomesticProduct()
    {
        await SeedProductAndDomesticProductAsync(0);
        var service = CreateStoreMaintenanceService(
            CreateHistoryService(throwWhenRecording: true).Object,
            "server-user-guid",
            "服务端更新者"
        );

        var result = await service.UpdateProductTypeAsync(
            "product-1",
            new UpdateStoreProductTypeDto { ProductType = 1 },
            "请求传入的名称",
            null
        );

        Assert.False(result.Success);
        Assert.Equal(0, (await _db.Queryable<Product>().SingleAsync(x => x.ProductCode == "product-1")).ProductType);
        Assert.Equal(0, (await _db.Queryable<DomesticProduct>().SingleAsync(x => x.ProductCode == "product-1")).ProductType);
    }

    [Fact]
    public async Task UpdateProductNameAsync_成功时同步改主档与国内商品镜像_记录历史并只入队名称字段()
    {
        await SeedProductAndDomesticProductAsync(0);
        var contexts = new List<WarehouseProductChangeHistoryContextDto>();
        var enqueued = new List<ProductMaintenanceHqMutationRequest>();
        var service = CreateStoreMaintenanceService(
            CreateHistoryService(contexts).Object,
            "server-user-guid",
            "服务端更新者",
            enqueued
        );

        var result = await service.UpdateProductNameAsync(
            "product-1",
            new UpdateStoreProductNameDto { ProductName = "  新的商品名称  " },
            "请求传入的名称",
            null
        );

        Assert.True(result.Success, result.Message);
        Assert.True(result.Data!.Changed);
        // 首尾空白被去掉后写入。
        Assert.Equal("新的商品名称", result.Data.ProductName);
        var product = await _db.Queryable<Product>().SingleAsync(item => item.ProductCode == "product-1");
        var domestic = await _db.Queryable<DomesticProduct>().SingleAsync(item => item.ProductCode == "product-1");
        Assert.Equal("新的商品名称", product.ProductName);
        Assert.Equal("新的商品名称", domestic.ProductName);
        // 操作人取服务端身份，不取请求里传来的名字。
        Assert.Equal("服务端更新者", product.UpdatedBy);
        Assert.Equal("服务端更新者", domestic.UpdatedBy);

        var context = Assert.Single(contexts);
        Assert.Equal("Update", context.Action);
        Assert.Equal("StoreProductMaintenance", context.Source);
        Assert.Equal("product-1", context.SourceReference);
        Assert.Equal("server-user-guid", context.ActorUserGuid);
        Assert.Equal("服务端更新者", context.ActorName);

        // HQ 同步只带名称字段：不能顺带推价格、类型等其它列。
        var request = Assert.Single(enqueued);
        Assert.Equal(ProductMaintenanceHqOperationKinds.ProductNameUpdated, request.OperationKind);
        Assert.Equal("product-1", request.ProductCode);
        Assert.Equal(new[] { ProductMaintenanceHqFieldMasks.ProductName }, request.FieldMask.ToArray());
    }

    [Fact]
    public async Task UpdateProductNameAsync_名称没变时不写库不记历史不入队()
    {
        await SeedProductAndDomesticProductAsync(0);
        var contexts = new List<WarehouseProductChangeHistoryContextDto>();
        var enqueued = new List<ProductMaintenanceHqMutationRequest>();
        var service = CreateStoreMaintenanceService(
            CreateHistoryService(contexts).Object,
            "server-user-guid",
            "服务端更新者",
            enqueued
        );

        var result = await service.UpdateProductNameAsync(
            "product-1",
            new UpdateStoreProductNameDto { ProductName = " 测试商品 " },
            "x",
            null
        );

        Assert.True(result.Success, result.Message);
        Assert.False(result.Data!.Changed);
        Assert.Empty(contexts);
        Assert.Empty(enqueued);
        Assert.Null((await _db.Queryable<Product>().SingleAsync(item => item.ProductCode == "product-1")).UpdatedBy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("第一行\n第二行")]
    [InlineData("带\t制表符")]
    public async Task UpdateProductNameAsync_空名称或含控制字符时拒绝且不改动(string name)
    {
        await SeedProductAndDomesticProductAsync(0);
        var contexts = new List<WarehouseProductChangeHistoryContextDto>();
        var service = CreateStoreMaintenanceService(
            CreateHistoryService(contexts).Object,
            "server-user-guid",
            "服务端更新者"
        );

        var result = await service.UpdateProductNameAsync(
            "product-1",
            new UpdateStoreProductNameDto { ProductName = name },
            "x",
            null
        );

        Assert.False(result.Success);
        Assert.Empty(contexts);
        Assert.Equal("测试商品", (await _db.Queryable<Product>().SingleAsync(item => item.ProductCode == "product-1")).ProductName);
    }

    [Fact]
    public async Task UpdateProductNameAsync_超过200字符时拒绝_恰好200字符时通过()
    {
        await SeedProductAndDomesticProductAsync(0);
        var service = CreateStoreMaintenanceService(
            CreateHistoryService().Object,
            "server-user-guid",
            "服务端更新者"
        );

        var tooLong = await service.UpdateProductNameAsync(
            "product-1",
            new UpdateStoreProductNameDto { ProductName = new string('名', 201) },
            "x",
            null
        );
        var exact = await service.UpdateProductNameAsync(
            "product-1",
            new UpdateStoreProductNameDto { ProductName = new string('名', 200) },
            "x",
            null
        );

        Assert.False(tooLong.Success);
        Assert.True(exact.Success, exact.Message);
        Assert.Equal(200, (await _db.Queryable<Product>().SingleAsync(item => item.ProductCode == "product-1")).ProductName.Length);
    }

    [Fact]
    public async Task UpdateProductNameAsync_历史失败时回滚Product和DomesticProduct()
    {
        await SeedProductAndDomesticProductAsync(0);
        var service = CreateStoreMaintenanceService(
            CreateHistoryService(throwWhenRecording: true).Object,
            "server-user-guid",
            "服务端更新者"
        );

        var result = await service.UpdateProductNameAsync(
            "product-1",
            new UpdateStoreProductNameDto { ProductName = "新名称" },
            "x",
            null
        );

        Assert.False(result.Success);
        Assert.Equal("测试商品", (await _db.Queryable<Product>().SingleAsync(x => x.ProductCode == "product-1")).ProductName);
        Assert.Equal("测试国内商品", (await _db.Queryable<DomesticProduct>().SingleAsync(x => x.ProductCode == "product-1")).ProductName);
    }

    [Fact]
    public async Task UpdateProductNameAsync_商品不存在时报错()
    {
        var service = CreateStoreMaintenanceService(
            CreateHistoryService().Object,
            "server-user-guid",
            "服务端更新者"
        );

        var result = await service.UpdateProductNameAsync(
            "missing",
            new UpdateStoreProductNameDto { ProductName = "新名称" },
            "x",
            null
        );

        Assert.False(result.Success);
        Assert.Equal("商品不存在", result.Message);
    }

    [Fact]
    public async Task GetProductNameHistoryAsync_只返回名称变更_保持新到旧并按条数截取()
    {
        await SeedProductAndDomesticProductAsync(0);
        var history = CreateHistoryService();
        history.Setup(service => service.GetChangeHistoryAsync(
                "product-1",
                1,
                100,
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(new WarehouseProductChangeHistoryPageDto
            {
                PageNumber = 1,
                PageSize = 100,
                TotalCount = 4,
                TotalPages = 1,
                Events =
                [
                    HistoryEvent("店长甲", "productName", "旧名B", "新名C", new DateTime(2026, 10, 13, 3, 0, 0, DateTimeKind.Utc)),
                    HistoryEvent("店长乙", "retailPrice", "1.00", "2.00", new DateTime(2026, 10, 13, 2, 0, 0, DateTimeKind.Utc)),
                    HistoryEvent("店长丙", "productName", "旧名A", "旧名B", new DateTime(2026, 10, 12, 3, 0, 0, DateTimeKind.Utc)),
                    HistoryEvent("店长丁", "productName", "最初", "旧名A", new DateTime(2026, 10, 11, 3, 0, 0, DateTimeKind.Utc)),
                ],
            });
        var service = CreateStoreMaintenanceService(history.Object, "server-user-guid", "服务端更新者");

        var result = await service.GetProductNameHistoryAsync("product-1", null, 2, null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, result.Data!.Items.Count);
        Assert.Equal("新名C", result.Data.Items[0].AfterName);
        Assert.Equal("旧名B", result.Data.Items[0].BeforeName);
        Assert.Equal("店长甲", result.Data.Items[0].ActorName);
        // 价格变更那一条被过滤掉，第二条是上一次改名。
        Assert.Equal("店长丙", result.Data.Items[1].ActorName);
        Assert.Equal("旧名B", result.Data.Items[1].AfterName);
    }

    private static WarehouseProductChangeHistoryEventDto HistoryEvent(
        string actor,
        string fieldKey,
        string before,
        string after,
        DateTime occurredAtUtc
    ) => new()
    {
        EventGuid = Guid.NewGuid(),
        Action = "Update",
        Source = "StoreProductMaintenance",
        ActorName = actor,
        ActorType = "User",
        OccurredAtUtc = occurredAtUtc,
        Changes = [new WarehouseProductChangeItemDto { FieldKey = fieldKey, BeforeValue = before, AfterValue = after }],
    };

    public void Dispose()
    {
        _cache.Dispose();
        _db.Dispose();
        _connection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private ReactProductsController CreateController(
        IWarehouseProductChangeHistoryService historyService,
        string userGuid,
        string username
    ) => CreateController(historyService, CreateCurrentUserService(userGuid, username));

    private ReactProductsController CreateController(
        IWarehouseProductChangeHistoryService historyService,
        ICurrentUserService currentUserService
    ) => new(
        CreateSqlSugarContext(_db),
        NullLogger<ReactProductsController>.Instance,
        historyService,
        currentUserService,
        CreateSuccessfulProjectionWriter()
    )
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
    };

    private StoreProductMaintenanceReactService CreateStoreMaintenanceService(
        IWarehouseProductChangeHistoryService historyService,
        string userGuid,
        string username,
        List<ProductMaintenanceHqMutationRequest>? enqueued = null
    ) => new(
        CreateSqlSugarContext(_db),
        NullLogger<StoreProductMaintenanceReactService>.Instance,
        Mock.Of<IAutoPricingService>(),
        _cache,
        historyService,
        CreateCurrentUserService(userGuid, username),
        CreateSuccessfulProjectionWriter(enqueued)
    );

    private static IProductMaintenanceHqProjectionWriter CreateSuccessfulProjectionWriter(
        List<ProductMaintenanceHqMutationRequest>? enqueued = null
    )
    {
        var writer = new Mock<IProductMaintenanceHqProjectionWriter>();
        writer.Setup(item => item.EnqueueAsync(
                It.IsAny<ISqlSugarClient>(),
                It.IsAny<ProductMaintenanceHqMutationRequest>(),
                It.IsAny<CancellationToken>()
            ))
            .Callback((ISqlSugarClient _, ProductMaintenanceHqMutationRequest request, CancellationToken _) =>
                enqueued?.Add(request))
            .ReturnsAsync(new ProductHqSyncOperationStatusDto
            {
                OperationId = Guid.NewGuid().ToString("N"),
                Status = ProductHqSyncOutboxStatuses.Pending,
                Retryable = true,
            });
        return writer.Object;
    }

    private static Mock<IWarehouseProductChangeHistoryService> CreateHistoryService(
        List<WarehouseProductChangeHistoryContextDto>? contexts = null,
        bool throwWhenRecording = false
    )
    {
        var history = new Mock<IWarehouseProductChangeHistoryService>();
        history
            .Setup(service => service.CaptureSnapshotsAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(new Dictionary<string, WarehouseProductChangeSnapshotDto>());
        if (throwWhenRecording)
        {
            history
                .Setup(service => service.RecordChangesAsync(
                    It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                    It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                    It.IsAny<WarehouseProductChangeHistoryContextDto>(),
                    It.IsAny<CancellationToken>()
                ))
                .ThrowsAsync(new InvalidOperationException("历史写入失败"));
        }
        else
        {
            history
                .Setup(service => service.RecordChangesAsync(
                    It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                    It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                    It.IsAny<WarehouseProductChangeHistoryContextDto>(),
                    It.IsAny<CancellationToken>()
                ))
                .Callback((
                    IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto> _,
                    IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto> _,
                    WarehouseProductChangeHistoryContextDto context,
                    CancellationToken _
                ) => contexts?.Add(context))
                .ReturnsAsync(1);
        }

        return history;
    }

    private static ICurrentUserService CreateCurrentUserService(string userGuid, string username)
    {
        var currentUser = new Mock<ICurrentUserService>(MockBehavior.Strict);
        currentUser.Setup(service => service.GetCurrentUserGuid()).Returns(userGuid);
        currentUser.Setup(service => service.GetCurrentUsername()).Returns(username);
        return currentUser.Object;
    }

    private Task SeedStoreAsync() => _db.Insertable(new Store
    {
        StoreGUID = "store-guid-1",
        StoreCode = "store-1",
        StoreName = "测试门店",
        IsActive = true,
        IsDeleted = false,
    }).ExecuteCommandAsync();

    private async Task SeedProductAndDomesticProductAsync(int productType)
    {
        await _db.Insertable(new Product
        {
            UUID = "product-uuid-1",
            ProductCode = "product-1",
            ProductName = "测试商品",
            ProductType = productType,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _db.Insertable(new DomesticProduct
        {
            ProductCode = "product-1",
            ProductName = "测试国内商品",
            ProductType = productType,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }
}
