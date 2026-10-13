using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HqEntities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class ProductMaintenanceHqProjectionWriterTests : IDisposable
{
    private static readonly DateTime UtcNow = new(2026, 9, 3, 2, 3, 4, DateTimeKind.Utc);
    private readonly string _localDbPath = Path.Combine(
        Path.GetTempPath(),
        $"product-maintenance-local-{Guid.NewGuid():N}.db"
    );
    private readonly string _hqDbPath = Path.Combine(
        Path.GetTempPath(),
        $"product-maintenance-hq-{Guid.NewGuid():N}.db"
    );
    private readonly SqlSugarClient _localDb;
    private readonly SqlSugarClient _hqDb;

    public ProductMaintenanceHqProjectionWriterTests()
    {
        _localDb = CreateDb(_localDbPath);
        _hqDb = CreateDb(_hqDbPath);
        _localDb.CodeFirst.InitTables(
            typeof(Product),
            typeof(StoreRetailPrice),
            typeof(ProductSetCode),
            typeof(StoreMultiCodeProduct),
            typeof(StoreClearancePrice)
        );
        _hqDb.CodeFirst.InitTables(
            typeof(HqBranch),
            typeof(DIC_商品信息字典表),
            typeof(DIC_商品零售价表),
            typeof(DIC_一品多码表),
            typeof(DIC_分店一品多码表),
            typeof(DIC_商品清货价表)
        );
    }

    [Fact]
    public async Task EnqueueAsync_门店价格使用事务内最终完整投影并返回公开操作状态()
    {
        await SeedLocalProductGraphAsync();
        ProductHqSyncOutboxEnqueueRequest? captured = null;
        var queue = new Mock<IProductHqSyncOutboxQueue>();
        queue.Setup(item => item.EnqueueAsync(
                _localDb,
                It.IsAny<ProductHqSyncOutboxEnqueueRequest>(),
                It.IsAny<CancellationToken>()
            ))
            .Callback<ISqlSugarClient, ProductHqSyncOutboxEnqueueRequest, CancellationToken>(
                (_, request, _) => captured = request
            )
            .ReturnsAsync(new ProductHqSyncOutboxEnqueueResultDto
            {
                OutboxId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                Operation = new ProductHqSyncOperationStatusDto
                {
                    OperationId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    Status = ProductHqSyncOutboxStatuses.Pending,
                    ProductCode = "P001",
                    StoreCode = "S01",
                    AttemptCount = 0,
                    Retryable = true,
                },
            });
        var writer = CreateWriter(queue.Object);

        await _localDb.Ado.BeginTranAsync();
        await _localDb.Updateable<StoreRetailPrice>()
            .SetColumns(item => new StoreRetailPrice
            {
                PurchasePrice = 8.25m,
                StoreRetailPriceValue = 16.5m,
                DiscountRate = 0.85m,
                IsAutoPricing = true,
                IsSpecialProduct = true,
                IsActive = false,
            })
            .Where(item => item.UUID == "price-1")
            .ExecuteCommandAsync();

        var status = await writer.EnqueueAsync(
            _localDb,
            new ProductMaintenanceHqMutationRequest
            {
                OperationKind = ProductMaintenanceHqOperationKinds.StorePriceUpdated,
                ProductCode = "P001",
                TargetStoreCodes = new List<string> { "S01" },
                AuthorizedStoreCodes = new List<string> { "S01" },
                FieldMask = ProductMaintenanceHqFieldMasks.StorePriceAndMultiCode.ToList(),
                RequestedByUserGuid = "user-1",
                Source = "tests",
                OccurredAtUtc = UtcNow,
            }
        );
        await _localDb.Ado.RollbackTranAsync();

        Assert.NotNull(captured);
        Assert.StartsWith("tests:", captured!.OperationKey, StringComparison.Ordinal);
        Assert.Equal(new[] { "S01" }, captured.TargetStoreCodes);
        Assert.Equal(new[] { "S01" }, captured.AuthorizedStoreCodes);
        Assert.Equal("user-1", captured.RequestedByUserGuid);
        Assert.Null(captured.RequestedByDeviceId);
        Assert.Equal(ProductMaintenanceHqFieldMasks.StorePriceAndMultiCode, captured.FieldMask);
        var payload = JsonSerializer.Deserialize<ProductMaintenanceHqProjectionPayloadDto>(
            captured.PayloadJson
        );
        Assert.True(payload!.StorePrices.Count > 0, captured.PayloadJson);
        var price = Assert.Single(payload.StorePrices);
        Assert.Equal(8.25m, price.PurchasePrice);
        Assert.Equal(16.5m, price.RetailPrice);
        Assert.Equal(0.85m, price.DiscountRate);
        Assert.True(price.IsAutoPricing);
        Assert.True(price.IsSpecialProduct);
        Assert.False(price.IsActive);
        Assert.Single(payload.StoreMultiCodes);
        Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", status.OperationId);
        Assert.Equal(ProductHqSyncOutboxStatuses.Pending, status.Status);
        Assert.Equal("P001", status.ProductCode);
        Assert.Equal("S01", status.StoreCode);
    }

    [Fact]
    public async Task EnqueueAsync_队列数据库异常只记录服务端并向上抛稳定安全文案()
    {
        await SeedLocalProductGraphAsync();
        const string rawDatabaseError = "SqlException: password=secret; table=ProductHqSyncOutbox";
        var databaseException = new InvalidOperationException(rawDatabaseError);
        var queue = new Mock<IProductHqSyncOutboxQueue>();
        queue.Setup(item => item.EnqueueAsync(
                _localDb,
                It.IsAny<ProductHqSyncOutboxEnqueueRequest>(),
                It.IsAny<CancellationToken>()
            ))
            .ThrowsAsync(databaseException);
        var logger = new Mock<ILogger<ProductMaintenanceHqProjectionWriter>>();
        var writer = CreateWriter(queue.Object, logger: logger.Object);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => writer.EnqueueAsync(
            _localDb,
            new ProductMaintenanceHqMutationRequest
            {
                OperationKind = ProductMaintenanceHqOperationKinds.StorePriceUpdated,
                ProductCode = "P001",
                TargetStoreCodes = new[] { "S01" },
                AuthorizedStoreCodes = new[] { "S01" },
                FieldMask = ProductMaintenanceHqFieldMasks.StorePriceAndMultiCode,
                RequestedByUserGuid = "user-1",
                Source = "tests.safe-error",
                OccurredAtUtc = UtcNow,
            }
        ));

        Assert.Equal("HQ 同步任务创建失败，请稍后重试", exception.Message);
        Assert.DoesNotContain(rawDatabaseError, exception.ToString(), StringComparison.Ordinal);
        logger.Verify(
            item => item.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) =>
                    state.ToString()!.Contains("P001", StringComparison.Ordinal)
                ),
                databaseException,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()
            ),
            Times.Once
        );
    }

    [Fact]
    public async Task ApplyAsync_门店价格与当前店多码在同一窄事务内使用最终投影()
    {
        await SeedLocalProductGraphAsync();
        await _localDb.Updateable<Product>()
            .SetColumns(item => item.LocalSupplierCode == null)
            .Where(item => item.ProductCode == "P001")
            .ExecuteCommandAsync();
        await _localDb.Updateable<StoreRetailPrice>()
            .SetColumns(item => item.SupplierCode == null)
            .Where(item => item.UUID == "price-1")
            .ExecuteCommandAsync();
        await _localDb.Updateable<StoreMultiCodeProduct>()
            .SetColumns(item => new StoreMultiCodeProduct
            {
                PurchasePrice = 33m,
                MultiCodeRetailPrice = 44m,
                DiscountRate = 0.2m,
                IsAutoPricing = false,
                IsSpecialProduct = false,
                IsActive = true,
            })
            .Where(item => item.UUID == "multi-1")
            .ExecuteCommandAsync();
        await SeedHqProductAndBranchAsync();
        await _hqDb.Insertable(new DIC_商品零售价表
        {
            HGUID = "hq-price",
            H分店代码 = "S01",
            H商品编码 = "P001",
            H分店商品编码 = "old",
            H供应商编码 = "old",
            H分店供应商编码 = "old",
            H进货价 = 1m,
            H分店零售价 = 2m,
            H折扣率 = 1m,
            H是否自动定价 = false,
            H是否特殊商品 = false,
            H使用状态 = true,
        }).ExecuteCommandAsync();
        await _hqDb.Insertable(new[]
        {
            new DIC_分店一品多码表
            {
                HGUID = "multi-1",
                H分店代码 = "S01",
                H商品编码 = "P001",
                H分店商品编码 = "legacy-store-product",
                H多码商品编码 = "M001",
                H分店多码商品编码 = "S01M001",
                H供应商编码 = "legacy-supplier",
                H进货价 = 1m,
                H一品多码零售价 = 2m,
                H使用状态 = true,
            },
            new DIC_分店一品多码表
            {
                HGUID = "multi-s02",
                H分店代码 = "S02",
                H商品编码 = "P001",
                H多码商品编码 = "M001",
                H进货价 = 99m,
                H一品多码零售价 = 199m,
                H使用状态 = true,
            },
        }).ExecuteCommandAsync();

        PushProductsToHqRequest? delegated = null;
        var hqSync = SuccessfulHqSyncMock();
        hqSync.Setup(item => item.PushToHqAsync(It.IsAny<PushProductsToHqRequest>()))
            .Callback<PushProductsToHqRequest>(request => delegated = request)
            .ReturnsAsync(ApiResponse<PushProductsToHqResult>.OK(new PushProductsToHqResult
            {
                TotalCount = 1,
                SuccessCount = 1,
            }));
        var writer = CreateWriter(Mock.Of<IProductHqSyncOutboxQueue>(), hqSync.Object);

        var result = await writer.ApplyAsync(WorkItem(
            ProductMaintenanceHqOperationKinds.StorePriceUpdated,
            ProductMaintenanceHqFieldMasks.StorePriceAndMultiCode,
            new[] { "S01" }
        ));

        Assert.Equal(ProductHqSyncOutboxExecutionDisposition.Success, result.Disposition);
        Assert.Null(delegated);
        var hqPrice = await _hqDb.Queryable<DIC_商品零售价表>().SingleAsync();
        Assert.Equal("old", hqPrice.H分店商品编码);
        Assert.Equal("old", hqPrice.H供应商编码);
        Assert.Equal("old", hqPrice.H分店供应商编码);
        Assert.Equal(5m, hqPrice.H进货价);
        Assert.Equal(10m, hqPrice.H分店零售价);
        Assert.Equal(0.9m, hqPrice.H折扣率);
        Assert.True(hqPrice.H是否自动定价);
        Assert.True(hqPrice.H是否特殊商品);
        Assert.False(hqPrice.H使用状态);
        var hqMultiCode = await _hqDb.Queryable<DIC_分店一品多码表>()
            .SingleAsync(item => item.H分店代码 == "S01");
        Assert.Equal("S01", hqMultiCode.H分店代码);
        Assert.Equal("P001", hqMultiCode.H商品编码);
        Assert.Equal("M001", hqMultiCode.H多码商品编码);
        Assert.Equal("legacy-store-product", hqMultiCode.H分店商品编码);
        Assert.Equal("legacy-supplier", hqMultiCode.H供应商编码);
        Assert.Equal(5m, hqMultiCode.H进货价);
        Assert.Equal(10m, hqMultiCode.H一品多码零售价);
        Assert.Equal(0.9m, hqMultiCode.H折扣率);
        Assert.True(hqMultiCode.H是否自动定价);
        Assert.True(hqMultiCode.H是否特殊商品);
        Assert.False(hqMultiCode.H使用状态);
        var untouchedOtherStore = await _hqDb.Queryable<DIC_分店一品多码表>()
            .SingleAsync(item => item.H分店代码 == "S02");
        Assert.Equal(99m, untouchedOtherStore.H进货价);
        Assert.Equal(199m, untouchedOtherStore.H一品多码零售价);
        Assert.True(untouchedOtherStore.H使用状态);
    }

    [Fact]
    public async Task ApplyAsync_纯门店多码编辑沿用多码行价格策略且供应商最终回退200()
    {
        await SeedLocalProductGraphAsync();
        await _localDb.Updateable<Product>()
            .SetColumns(item => item.LocalSupplierCode == null)
            .Where(item => item.ProductCode == "P001")
            .ExecuteCommandAsync();
        await _localDb.Updateable<StoreRetailPrice>()
            .SetColumns(item => item.SupplierCode == null)
            .Where(item => item.UUID == "price-1")
            .ExecuteCommandAsync();
        await _localDb.Updateable<StoreMultiCodeProduct>()
            .SetColumns(item => new StoreMultiCodeProduct
            {
                PurchasePrice = 33m,
                MultiCodeRetailPrice = 44m,
                DiscountRate = 0.2m,
                IsAutoPricing = false,
                IsSpecialProduct = false,
                IsActive = true,
            })
            .Where(item => item.UUID == "multi-1")
            .ExecuteCommandAsync();
        await SeedHqProductAndBranchAsync();
        var writer = CreateWriter(
            Mock.Of<IProductHqSyncOutboxQueue>(),
            SuccessfulHqSyncMock().Object
        );

        var result = await writer.ApplyAsync(WorkItem(
            ProductMaintenanceHqOperationKinds.ProductCodesUpdated,
            new[] { ProductMaintenanceHqFieldMasks.StoreMultiCodes },
            new[] { "S01" }
        ));

        Assert.Equal(ProductHqSyncOutboxExecutionDisposition.Success, result.Disposition);
        var hqMultiCode = await _hqDb.Queryable<DIC_分店一品多码表>().SingleAsync();
        Assert.Equal("200", hqMultiCode.H供应商编码);
        Assert.Equal(33m, hqMultiCode.H进货价);
        Assert.Equal(44m, hqMultiCode.H一品多码零售价);
        Assert.Equal(0.2m, hqMultiCode.H折扣率);
        Assert.False(hqMultiCode.H是否自动定价);
        Assert.False(hqMultiCode.H是否特殊商品);
        Assert.True(hqMultiCode.H使用状态);
    }

    [Fact]
    public async Task ApplyAsync_门店多码按Hguid命中历史业务编码且不重复插入()
    {
        await SeedLocalProductGraphAsync();
        await SeedHqProductAndBranchAsync();
        await _hqDb.Insertable(new DIC_分店一品多码表
        {
            HGUID = "multi-1",
            H分店代码 = "S01",
            H商品编码 = "P001",
            H分店商品编码 = "S01P001",
            H多码商品编码 = "LEGACY-M001",
            H分店多码商品编码 = "S01LEGACY-M001",
            H多条形码 = "legacy-barcode",
            H进货价 = 1m,
            H一品多码零售价 = 2m,
            H使用状态 = true,
        }).ExecuteCommandAsync();
        var writer = CreateWriter(
            Mock.Of<IProductHqSyncOutboxQueue>(),
            SuccessfulHqSyncMock().Object
        );

        var result = await writer.ApplyAsync(WorkItem(
            ProductMaintenanceHqOperationKinds.ProductCodesUpdated,
            new[] { ProductMaintenanceHqFieldMasks.StoreMultiCodes },
            new[] { "S01" }
        ));

        Assert.Equal(ProductHqSyncOutboxExecutionDisposition.Success, result.Disposition);
        var hqMultiCode = await _hqDb.Queryable<DIC_分店一品多码表>().SingleAsync();
        Assert.Equal("multi-1", hqMultiCode.HGUID);
        Assert.Equal("LEGACY-M001", hqMultiCode.H多码商品编码);
        Assert.Equal("S01LEGACY-M001", hqMultiCode.H分店多码商品编码);
        Assert.Equal(5m, hqMultiCode.H进货价);
        Assert.Equal(10m, hqMultiCode.H一品多码零售价);
        Assert.Equal("930000000001", hqMultiCode.H多条形码);
    }

    [Fact]
    public async Task ApplyAsync_门店多码不同身份命中不同物理记录时Blocked且不写入()
    {
        await SeedLocalProductGraphAsync();
        await SeedHqProductAndBranchAsync();
        await _hqDb.Insertable(new[]
        {
            new DIC_分店一品多码表
            {
                HGUID = "exact-row",
                H分店代码 = "S01",
                H商品编码 = "P001",
                H多码商品编码 = "M001",
                H分店多码商品编码 = "S01M001",
                H进货价 = 1m,
                H一品多码零售价 = 2m,
                H使用状态 = true,
            },
            new DIC_分店一品多码表
            {
                HGUID = "multi-1",
                H分店代码 = "S01",
                H商品编码 = "P001",
                H多码商品编码 = "LEGACY-M001",
                H分店多码商品编码 = "S01LEGACY-M001",
                H进货价 = 3m,
                H一品多码零售价 = 4m,
                H使用状态 = true,
            },
        }).ExecuteCommandAsync();
        var writer = CreateWriter(
            Mock.Of<IProductHqSyncOutboxQueue>(),
            SuccessfulHqSyncMock().Object
        );

        var result = await writer.ApplyAsync(WorkItem(
            ProductMaintenanceHqOperationKinds.ProductCodesUpdated,
            new[] { ProductMaintenanceHqFieldMasks.StoreMultiCodes },
            new[] { "S01" }
        ));

        Assert.Equal(ProductHqSyncOutboxExecutionDisposition.Blocked, result.Disposition);
        Assert.Equal("PRODUCT_HQ_MUTATION_AMBIGUOUS_MULTI_CODE", result.ErrorCode);
        var rows = await _hqDb.Queryable<DIC_分店一品多码表>()
            .OrderBy(item => item.ID)
            .ToListAsync();
        Assert.Equal(new decimal?[] { 1m, 3m }, rows.Select(item => item.H进货价));
        Assert.Equal(new decimal?[] { 2m, 4m }, rows.Select(item => item.H一品多码零售价));
    }

    [Fact]
    public async Task ApplyAsync_清货价按当前门店商品精确Upsert并在本地删除后精确删除HQ()
    {
        await SeedLocalProductGraphAsync();
        await SeedHqProductAndBranchAsync();
        var writer = CreateWriter(
            Mock.Of<IProductHqSyncOutboxQueue>(),
            SuccessfulHqSyncMock().Object
        );

        var upsert = await writer.ApplyAsync(WorkItem(
            ProductMaintenanceHqOperationKinds.ClearancePriceUpdated,
            new[] { ProductMaintenanceHqFieldMasks.StoreClearancePrice },
            new[] { "S01" }
        ));
        var inserted = await _hqDb.Queryable<DIC_商品清货价表>().SingleAsync();
        Assert.Equal(ProductHqSyncOutboxExecutionDisposition.Success, upsert.Disposition);
        Assert.Equal("S01", inserted.分店代码);
        Assert.Equal("P001", inserted.商品编码);
        Assert.Equal("CLR001", inserted.清货条形码);
        Assert.Equal(7.5m, inserted.清货价);

        await _localDb.Deleteable<StoreClearancePrice>()
            .Where(item => item.UUID == "clearance-1")
            .ExecuteCommandAsync();
        var deleteItem = WorkItem(
            ProductMaintenanceHqOperationKinds.ClearancePriceDeleted,
            new[] { ProductMaintenanceHqFieldMasks.StoreClearancePrice },
            new[] { "S01" },
            new[]
            {
                new ProductHqSyncOutboxTombstoneDto(
                    ProductMaintenanceHqResourceKinds.StoreClearancePrice,
                    "S01",
                    "CLR001"
                ),
            }
        );

        var deleted = await writer.ApplyAsync(deleteItem);

        Assert.Equal(ProductHqSyncOutboxExecutionDisposition.Success, deleted.Disposition);
        Assert.Equal(0, await _hqDb.Queryable<DIC_商品清货价表>().CountAsync());
    }

    [Fact]
    public async Task ApplyAsync_清货价新增时HQ主档缺失则Blocked且不写孤儿记录()
    {
        await SeedLocalProductGraphAsync();
        await _hqDb.Insertable(new HqBranch { BranchCode = "S01", BranchName = "一店" })
            .ExecuteCommandAsync();
        var writer = CreateWriter(
            Mock.Of<IProductHqSyncOutboxQueue>(),
            SuccessfulHqSyncMock().Object
        );

        var result = await writer.ApplyAsync(WorkItem(
            ProductMaintenanceHqOperationKinds.ClearancePriceUpdated,
            new[] { ProductMaintenanceHqFieldMasks.StoreClearancePrice },
            new[] { "S01" }
        ));

        Assert.Equal(ProductHqSyncOutboxExecutionDisposition.Blocked, result.Disposition);
        Assert.Equal("PRODUCT_HQ_MUTATION_PRODUCT_NOT_READY", result.ErrorCode);
        Assert.Equal(0, await _hqDb.Queryable<DIC_商品清货价表>().CountAsync());
    }

    [Fact]
    public async Task ApplyAsync_清货价新增时本地商品已删除则Blocked且不写孤儿记录()
    {
        await SeedLocalProductGraphAsync();
        await SeedHqProductAndBranchAsync();
        await _localDb.Updateable<Product>()
            .SetColumns(item => item.IsDeleted == true)
            .Where(item => item.ProductCode == "P001")
            .ExecuteCommandAsync();
        var writer = CreateWriter(
            Mock.Of<IProductHqSyncOutboxQueue>(),
            SuccessfulHqSyncMock().Object
        );

        var result = await writer.ApplyAsync(WorkItem(
            ProductMaintenanceHqOperationKinds.ClearancePriceUpdated,
            new[] { ProductMaintenanceHqFieldMasks.StoreClearancePrice },
            new[] { "S01" }
        ));

        Assert.Equal(ProductHqSyncOutboxExecutionDisposition.Blocked, result.Disposition);
        Assert.Equal("PRODUCT_HQ_MUTATION_LOCAL_PRODUCT_NOT_FOUND", result.ErrorCode);
        Assert.Equal(0, await _hqDb.Queryable<DIC_商品清货价表>().CountAsync());
    }

    [Fact]
    public async Task ApplyAsync_删除套装条码时停用HQ全局与各店多码而不物理删除()
    {
        await _hqDb.Insertable(new[]
        {
            new DIC_一品多码表
            {
                HGUID = "set-1",
                H商品编码 = "P001",
                H多码商品编号 = "M001",
                H使用状态 = true,
            },
        }).ExecuteCommandAsync();
        await _hqDb.Insertable(new[]
        {
            new DIC_分店一品多码表
            {
                HGUID = "multi-s01",
                H分店代码 = "S01",
                H商品编码 = "P001",
                H多码商品编码 = "M001",
                H使用状态 = true,
            },
            new DIC_分店一品多码表
            {
                HGUID = "multi-s02",
                H分店代码 = "S02",
                H商品编码 = "P001",
                H多码商品编码 = "M001",
                H使用状态 = true,
            },
        }).ExecuteCommandAsync();
        var writer = CreateWriter(
            Mock.Of<IProductHqSyncOutboxQueue>(),
            SuccessfulHqSyncMock().Object
        );
        var tombstones = new[]
        {
            new ProductHqSyncOutboxTombstoneDto(
                ProductMaintenanceHqResourceKinds.ProductSetCode,
                null,
                "M001"
            ),
            new ProductHqSyncOutboxTombstoneDto(
                ProductMaintenanceHqResourceKinds.StoreMultiCode,
                null,
                "M001"
            ),
        };

        var result = await writer.ApplyAsync(WorkItem(
            ProductMaintenanceHqOperationKinds.ProductCodesDeleted,
            Array.Empty<string>(),
            null,
            tombstones
        ));

        Assert.Equal(ProductHqSyncOutboxExecutionDisposition.Success, result.Disposition);
        Assert.False((await _hqDb.Queryable<DIC_一品多码表>().SingleAsync()).H使用状态);
        Assert.All(
            await _hqDb.Queryable<DIC_分店一品多码表>().ToListAsync(),
            item => Assert.False(item.H使用状态)
        );
    }

    [Fact]
    public async Task ApplyAsync_旧删除墓碑遇到同业务键已恢复时不覆盖新状态且空店码不扩大范围()
    {
        await SeedLocalProductGraphAsync();
        await _hqDb.Insertable(new DIC_一品多码表
        {
            HGUID = "set-1",
            H商品编码 = "P001",
            H多码商品编号 = "M001",
            H使用状态 = true,
        }).ExecuteCommandAsync();
        await _hqDb.Insertable(new DIC_分店一品多码表
        {
            HGUID = "multi-s01",
            H分店代码 = "S01",
            H商品编码 = "P001",
            H多码商品编码 = "M001",
            H使用状态 = true,
        }).ExecuteCommandAsync();
        var writer = CreateWriter(
            Mock.Of<IProductHqSyncOutboxQueue>(),
            SuccessfulHqSyncMock().Object
        );

        var result = await writer.ApplyAsync(WorkItem(
            ProductMaintenanceHqOperationKinds.ProductCodesDeleted,
            Array.Empty<string>(),
            null,
            new[]
            {
                new ProductHqSyncOutboxTombstoneDto(
                    ProductMaintenanceHqResourceKinds.ProductSetCode,
                    null,
                    "M001"
                ),
                new ProductHqSyncOutboxTombstoneDto(
                    ProductMaintenanceHqResourceKinds.StoreMultiCode,
                    null,
                    "M001"
                ),
            }
        ));

        Assert.Equal(ProductHqSyncOutboxExecutionDisposition.Success, result.Disposition);
        Assert.True((await _hqDb.Queryable<DIC_一品多码表>().SingleAsync()).H使用状态);
        Assert.True((await _hqDb.Queryable<DIC_分店一品多码表>().SingleAsync()).H使用状态);
    }

    [Fact]
    public async Task ApplyAsync_旧套装删除墓碑遇到同业务键重建时不因历史软删行误判歧义()
    {
        await SeedLocalProductGraphAsync();
        await _localDb.Updateable<ProductSetCode>()
            .SetColumns(item => new ProductSetCode { IsDeleted = true, IsActive = false })
            .Where(item => item.SetCodeId == "set-1")
            .ExecuteCommandAsync();
        await _localDb.Insertable(new ProductSetCode
        {
            SetCodeId = "set-2",
            ProductCode = "P001",
            SetProductCode = "M001",
            SetItemNumber = "M001-NEW",
            SetBarcode = "930000000009",
            SetType = 2,
            SetQuantity = 1,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _hqDb.Insertable(new DIC_一品多码表
        {
            HGUID = "set-2",
            H商品编码 = "P001",
            H多码商品编号 = "M001",
            H多条形码 = "930000000009",
            H使用状态 = true,
        }).ExecuteCommandAsync();
        var writer = CreateWriter(
            Mock.Of<IProductHqSyncOutboxQueue>(),
            SuccessfulHqSyncMock().Object
        );

        var result = await writer.ApplyAsync(WorkItem(
            ProductMaintenanceHqOperationKinds.ProductCodesDeleted,
            Array.Empty<string>(),
            null,
            new[]
            {
                new ProductHqSyncOutboxTombstoneDto(
                    ProductMaintenanceHqResourceKinds.ProductSetCode,
                    null,
                    "M001"
                ),
            }
        ));

        Assert.Equal(ProductHqSyncOutboxExecutionDisposition.Success, result.Disposition);
        Assert.True((await _hqDb.Queryable<DIC_一品多码表>().SingleAsync()).H使用状态);
    }

    [Fact]
    public async Task ApplyAsync_套装删除按历史Hguid与条码停用原HQ物理记录()
    {
        await SeedLocalProductGraphAsync();
        await _localDb.Updateable<ProductSetCode>()
            .SetColumns(item => new ProductSetCode { IsDeleted = true, IsActive = false })
            .Where(item => item.SetCodeId == "set-1")
            .ExecuteCommandAsync();
        await _hqDb.Insertable(new DIC_一品多码表
        {
            HGUID = "set-1",
            H商品编码 = "P001",
            H多码商品编号 = "LEGACY-M001",
            H多条形码 = "legacy-barcode",
            H使用状态 = true,
        }).ExecuteCommandAsync();
        await _hqDb.Insertable(new DIC_分店一品多码表
        {
            HGUID = "legacy-store-row",
            H分店代码 = "S01",
            H商品编码 = "P001",
            H多码商品编码 = "LEGACY-M001",
            H分店多码商品编码 = "S01LEGACY-M001",
            H多条形码 = "930000000001",
            H使用状态 = true,
        }).ExecuteCommandAsync();
        var writer = CreateWriter(
            Mock.Of<IProductHqSyncOutboxQueue>(),
            SuccessfulHqSyncMock().Object
        );

        var result = await writer.ApplyAsync(WorkItem(
            ProductMaintenanceHqOperationKinds.ProductCodesDeleted,
            Array.Empty<string>(),
            null,
            new[]
            {
                new ProductHqSyncOutboxTombstoneDto(
                    ProductMaintenanceHqResourceKinds.ProductSetCode,
                    null,
                    "M001"
                ),
            }
        ));

        Assert.Equal(ProductHqSyncOutboxExecutionDisposition.Success, result.Disposition);
        Assert.False((await _hqDb.Queryable<DIC_一品多码表>().SingleAsync()).H使用状态);
        Assert.False((await _hqDb.Queryable<DIC_分店一品多码表>().SingleAsync()).H使用状态);
    }

    [Fact]
    public async Task ApplyAsync_套装删除身份歧义时Blocked且不误停用()
    {
        await SeedLocalProductGraphAsync();
        await SeedHqProductAndBranchAsync();
        await _localDb.Updateable<ProductSetCode>()
            .SetColumns(item => new ProductSetCode { IsDeleted = true, IsActive = false })
            .Where(item => item.SetCodeId == "set-1")
            .ExecuteCommandAsync();
        await _hqDb.Insertable(new[]
        {
            new DIC_一品多码表
            {
                HGUID = "exact-row",
                H商品编码 = "P001",
                H多码商品编号 = "M001",
                H使用状态 = true,
            },
            new DIC_一品多码表
            {
                HGUID = "set-1",
                H商品编码 = "P001",
                H多码商品编号 = "LEGACY-M001",
                H使用状态 = true,
            },
        }).ExecuteCommandAsync();
        var hqSync = new Mock<IProductHqSyncService>(MockBehavior.Strict);
        var writer = CreateWriter(Mock.Of<IProductHqSyncOutboxQueue>(), hqSync.Object);

        var result = await writer.ApplyAsync(WorkItem(
            ProductMaintenanceHqOperationKinds.SetCodeSnapshot,
            new[] { ProductMaintenanceHqFieldMasks.ProductSetCodes },
            null,
            new[]
            {
                new ProductHqSyncOutboxTombstoneDto(
                    ProductMaintenanceHqResourceKinds.ProductSetCode,
                    null,
                    "M001"
                ),
            }
        ));

        Assert.Equal(ProductHqSyncOutboxExecutionDisposition.Blocked, result.Disposition);
        Assert.Equal("PRODUCT_HQ_MUTATION_AMBIGUOUS_MULTI_CODE", result.ErrorCode);
        Assert.All(
            await _hqDb.Queryable<DIC_一品多码表>().ToListAsync(),
            item => Assert.True(item.H使用状态)
        );
        hqSync.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Executor_完整创建复用既有PushToHq并保留全HQ分店语义()
    {
        var hqSync = SuccessfulHqSyncMock();
        PushProductsToHqRequest? delegated = null;
        hqSync.Setup(item => item.PushToHqAsync(It.IsAny<PushProductsToHqRequest>()))
            .Callback<PushProductsToHqRequest>(request => delegated = request)
            .ReturnsAsync(ApiResponse<PushProductsToHqResult>.OK(new PushProductsToHqResult
            {
                TotalCount = 1,
                SuccessCount = 1,
            }));
        var writer = CreateWriter(Mock.Of<IProductHqSyncOutboxQueue>(), hqSync.Object);
        var executor = new ProductMaintenanceHqSyncOutboxExecutor(writer);

        var result = await executor.ExecuteAsync(WorkItem(
            ProductMaintenanceHqOperationKinds.ProductCreated,
            new[] { ProductMaintenanceHqFieldMasks.All },
            null
        ));

        Assert.Equal(ProductHqSyncOutboxExecutionDisposition.Success, result.Disposition);
        Assert.NotNull(delegated);
        Assert.Equal(new[] { "P001" }, delegated!.ProductCodes);
        Assert.Null(delegated.TargetStoreCodes);
        Assert.Null(delegated.UpdateFields);
    }

    [Theory]
    [InlineData("PRODUCT_HQ_PUSH_UNKNOWN_STORE_CODES")]
    [InlineData("PRODUCT_HQ_PUSH_EMPTY_TARGET_STORES")]
    [InlineData("PRODUCT_HQ_PUSH_ITEM_ERRORS")]
    public async Task ApplyAsync_既有推送返回无效业务映射时进入Blocked(string errorCode)
    {
        await SeedHqProductAndBranchAsync();
        var hqSync = new Mock<IProductHqSyncService>(MockBehavior.Strict);
        hqSync.Setup(item => item.PushToHqAsync(It.IsAny<PushProductsToHqRequest>()))
            .ReturnsAsync(ApiResponse<PushProductsToHqResult>.Error(
                "请求包含无效业务映射",
                errorCode,
                new PushProductsToHqResult { FailedCount = 1 }
            ));
        var writer = CreateWriter(Mock.Of<IProductHqSyncOutboxQueue>(), hqSync.Object);

        var result = await writer.ApplyAsync(WorkItem(
            ProductMaintenanceHqOperationKinds.ProductTypeUpdated,
            new[] { ProductMaintenanceHqFieldMasks.ProductType },
            Array.Empty<string>()
        ));

        Assert.Equal(ProductHqSyncOutboxExecutionDisposition.Blocked, result.Disposition);
        Assert.Equal(errorCode, result.ErrorCode);
        hqSync.VerifyAll();
    }

    [Fact]
    public async Task ApplyAsync_名称字段只委托productName给既有推送_不带其它列()
    {
        await SeedHqProductAndBranchAsync();
        PushProductsToHqRequest? delegated = null;
        var hqSync = new Mock<IProductHqSyncService>(MockBehavior.Strict);
        hqSync.Setup(item => item.PushToHqAsync(It.IsAny<PushProductsToHqRequest>()))
            .Callback<PushProductsToHqRequest>(request => delegated = request)
            .ReturnsAsync(ApiResponse<PushProductsToHqResult>.OK(new PushProductsToHqResult
            {
                TotalCount = 1,
                SuccessCount = 1,
            }));
        var writer = CreateWriter(Mock.Of<IProductHqSyncOutboxQueue>(), hqSync.Object);

        var result = await writer.ApplyAsync(WorkItem(
            ProductMaintenanceHqOperationKinds.ProductNameUpdated,
            new[] { ProductMaintenanceHqFieldMasks.ProductName },
            Array.Empty<string>()
        ));

        Assert.Equal(ProductHqSyncOutboxExecutionDisposition.Success, result.Disposition);
        Assert.NotNull(delegated);
        Assert.Equal(new[] { "P001" }, delegated!.ProductCodes);
        // 只推中文名：HQ 侧据此只更新「大写名称」，英文显示名、价格、类型都不动。
        Assert.Equal(new[] { "productName" }, delegated.UpdateFields);
        hqSync.VerifyAll();
    }

    [Fact]
    public async Task ApplyAsync_HQ商品主档不存在时进入Blocked而不是永久重试()
    {
        await SeedLocalProductGraphAsync();
        await _hqDb.Insertable(new HqBranch { BranchCode = "S01", BranchName = "一店" })
            .ExecuteCommandAsync();
        var writer = CreateWriter(
            Mock.Of<IProductHqSyncOutboxQueue>(),
            SuccessfulHqSyncMock().Object
        );

        var result = await writer.ApplyAsync(WorkItem(
            ProductMaintenanceHqOperationKinds.StorePriceUpdated,
            new[] { ProductMaintenanceHqFieldMasks.StorePurchasePrice },
            new[] { "S01" }
        ));

        Assert.Equal(ProductHqSyncOutboxExecutionDisposition.Blocked, result.Disposition);
        Assert.Equal("PRODUCT_HQ_MUTATION_PRODUCT_NOT_READY", result.ErrorCode);
    }

    [Theory]
    [InlineData(ProductMaintenanceHqOperationKinds.ProductTypeUpdated, ProductMaintenanceHqFieldMasks.ProductType)]
    [InlineData(ProductMaintenanceHqOperationKinds.ProductNameUpdated, ProductMaintenanceHqFieldMasks.ProductName)]
    [InlineData(ProductMaintenanceHqOperationKinds.ProductCodesUpdated, ProductMaintenanceHqFieldMasks.ProductSetCodes)]
    public async Task ApplyAsync_非创建委托字段在HQ主档缺失时Blocked且不调用既有Push(
        string operationKind,
        string fieldMask
    )
    {
        var hqSync = new Mock<IProductHqSyncService>(MockBehavior.Strict);
        var writer = CreateWriter(Mock.Of<IProductHqSyncOutboxQueue>(), hqSync.Object);

        var result = await writer.ApplyAsync(WorkItem(
            operationKind,
            new[] { fieldMask },
            new[] { "S01" }
        ));

        Assert.Equal(ProductHqSyncOutboxExecutionDisposition.Blocked, result.Disposition);
        Assert.Equal("PRODUCT_HQ_MUTATION_PRODUCT_NOT_READY", result.ErrorCode);
        Assert.Equal(0, await _hqDb.Queryable<DIC_一品多码表>().CountAsync());
        Assert.Equal(0, await _hqDb.Queryable<DIC_分店一品多码表>().CountAsync());
        hqSync.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unknown-field")]
    public async Task ApplyAsync_空或未知字段且无墓碑时Blocked避免静默丢任务(string? field)
    {
        var hqSync = new Mock<IProductHqSyncService>(MockBehavior.Strict);
        var writer = CreateWriter(Mock.Of<IProductHqSyncOutboxQueue>(), hqSync.Object);

        var result = await writer.ApplyAsync(WorkItem(
            ProductMaintenanceHqOperationKinds.ProductCodesUpdated,
            field == null ? Array.Empty<string>() : new[] { field },
            null
        ));

        Assert.Equal(ProductHqSyncOutboxExecutionDisposition.Blocked, result.Disposition);
        Assert.Equal("PRODUCT_HQ_MUTATION_INVALID_FIELD_MASK", result.ErrorCode);
        hqSync.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("unknown-resource", null, "M001")]
    [InlineData(ProductMaintenanceHqResourceKinds.StoreMultiCode, null, "M001")]
    [InlineData(ProductMaintenanceHqResourceKinds.StoreClearancePrice, " ", "P001")]
    [InlineData(ProductMaintenanceHqResourceKinds.ProductSetCode, null, " ")]
    public async Task ApplyAsync_未知或缺少范围的墓碑Blocked避免删除任务静默成功(
        string resourceKind,
        string? storeCode,
        string businessKey
    )
    {
        var hqSync = new Mock<IProductHqSyncService>(MockBehavior.Strict);
        var writer = CreateWriter(Mock.Of<IProductHqSyncOutboxQueue>(), hqSync.Object);

        var result = await writer.ApplyAsync(WorkItem(
            ProductMaintenanceHqOperationKinds.ProductCodesDeleted,
            Array.Empty<string>(),
            null,
            new[]
            {
                new ProductHqSyncOutboxTombstoneDto(resourceKind, storeCode, businessKey),
            }
        ));

        Assert.Equal(ProductHqSyncOutboxExecutionDisposition.Blocked, result.Disposition);
        Assert.Equal("PRODUCT_HQ_MUTATION_INVALID_TOMBSTONE", result.ErrorCode);
        hqSync.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ApplyAsync_执行期间取消时向上抛出而不是转换为Retryable()
    {
        await SeedHqProductAndBranchAsync();
        using var cancellation = new CancellationTokenSource();
        var hqSync = new Mock<IProductHqSyncService>(MockBehavior.Strict);
        hqSync.Setup(item => item.PushToHqAsync(It.IsAny<PushProductsToHqRequest>()))
            .Returns(() =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            });
        var writer = CreateWriter(Mock.Of<IProductHqSyncOutboxQueue>(), hqSync.Object);

        await Assert.ThrowsAsync<OperationCanceledException>(() => writer.ApplyAsync(
            WorkItem(
                ProductMaintenanceHqOperationKinds.ProductTypeUpdated,
                new[] { ProductMaintenanceHqFieldMasks.ProductType },
                null
            ),
            cancellation.Token
        ));
        hqSync.VerifyAll();
    }

    private ProductMaintenanceHqProjectionWriter CreateWriter(
        IProductHqSyncOutboxQueue queue,
        IProductHqSyncService? hqSync = null,
        ILogger<ProductMaintenanceHqProjectionWriter>? logger = null
    ) => new(
        CreateSqlSugarContext(_localDb),
        CreateHqSqlSugarContext(_hqDb),
        queue,
        hqSync ?? SuccessfulHqSyncMock().Object,
        logger ?? NullLogger<ProductMaintenanceHqProjectionWriter>.Instance
    );

    private static Mock<IProductHqSyncService> SuccessfulHqSyncMock() => new();

    private static ProductHqSyncOutboxWorkItemDto WorkItem(
        string operationKind,
        IReadOnlyCollection<string> fieldMask,
        IReadOnlyCollection<string>? storeCodes,
        IReadOnlyCollection<ProductHqSyncOutboxTombstoneDto>? tombstones = null
    ) => new()
    {
        OutboxId = Guid.NewGuid(),
        OperationKey = Guid.NewGuid().ToString("N"),
        OperationKind = operationKind,
        ProductCode = "P001",
        ScopeKey = storeCodes == null
            ? "all"
            : storeCodes.Count == 0
                ? "global"
                : $"stores:{string.Join(',', storeCodes)}",
        TargetStoreCodes = storeCodes?.ToList(),
        FieldMask = fieldMask.ToList(),
        PayloadJson = "{}",
        Tombstones = tombstones?.ToList() ?? new List<ProductHqSyncOutboxTombstoneDto>(),
        Source = "tests",
        OccurredAtUtc = UtcNow,
        AttemptCount = 1,
    };

    private async Task SeedLocalProductGraphAsync()
    {
        await _localDb.Insertable(new Product
        {
            UUID = "product-1",
            ProductCode = "P001",
            ProductName = "测试商品",
            LocalSupplierCode = "SUP01",
            ProductType = 2,
            PurchasePrice = 4m,
            RetailPrice = 8m,
            IsAutoPricing = false,
            IsSpecialProduct = false,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new StoreRetailPrice
        {
            UUID = "price-1",
            StoreCode = "S01",
            ProductCode = "P001",
            StoreProductCode = "S01P001",
            SupplierCode = "SUP01",
            PurchasePrice = 5m,
            StoreRetailPriceValue = 10m,
            DiscountRate = 0.9m,
            IsAutoPricing = true,
            IsSpecialProduct = true,
            IsActive = false,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new ProductSetCode
        {
            SetCodeId = "set-1",
            ProductCode = "P001",
            SetProductCode = "M001",
            SetItemNumber = "M001",
            SetBarcode = "930000000001",
            SetType = 2,
            SetQuantity = 1,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new StoreMultiCodeProduct
        {
            UUID = "multi-1",
            StoreCode = "S01",
            ProductCode = "P001",
            MultiCodeProductCode = "M001",
            StoreMultiCodeProductCode = "S01M001",
            MultiBarcode = "930000000001",
            PurchasePrice = 5m,
            MultiCodeRetailPrice = 10m,
            DiscountRate = 0.9m,
            IsAutoPricing = true,
            IsSpecialProduct = true,
            IsActive = false,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new StoreClearancePrice
        {
            UUID = "clearance-1",
            StoreCode = "S01",
            ProductCode = "P001",
            ClearanceBarcode = "CLR001",
            ClearancePrice = 7.5m,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task SeedHqProductAndBranchAsync()
    {
        await _hqDb.Insertable(new HqBranch { BranchCode = "S01", BranchName = "一店" })
            .ExecuteCommandAsync();
        await _hqDb.Insertable(new DIC_商品信息字典表
        {
            HGUID = "hq-product",
            H商品标签GUID = string.Empty,
            H商品分类码GUID = string.Empty,
            H供货商编码 = "SUP01",
            H商品编码 = "P001",
            H货号 = string.Empty,
            H主条形码 = string.Empty,
            H商品名称 = "测试商品",
            H大写名称 = string.Empty,
            H规格 = string.Empty,
            H单位 = string.Empty,
            H商品图片 = string.Empty,
            H腾讯云图地址 = string.Empty,
            H进货单主表GUID = string.Empty,
            H进货单详情GUID = string.Empty,
            CBP商品中文名称 = string.Empty,
            CBP供应商编码 = string.Empty,
            CBP商品分类码GUID = string.Empty,
            FGC_Creator = "tests",
            FGC_CreateDate = UtcNow,
            FGC_LastModifier = "tests",
            FGC_LastModifyDate = UtcNow,
            FGC_UpdateHelp = string.Empty,
            H使用状态 = true,
        }).ExecuteCommandAsync();
    }

    private static SqlSugarClient CreateDb(string path) =>
        new(
            new ConnectionConfig
            {
                ConnectionString = $"Data Source={path}",
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            }
        );

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
        var context = (HqSqlSugarContext)RuntimeHelpers.GetUninitializedObject(
            typeof(HqSqlSugarContext)
        );
        typeof(HqSqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    public void Dispose()
    {
        _localDb.Dispose();
        _hqDb.Dispose();
        if (File.Exists(_localDbPath)) File.Delete(_localDbPath);
        if (File.Exists(_hqDbPath)) File.Delete(_hqDbPath);
    }
}
