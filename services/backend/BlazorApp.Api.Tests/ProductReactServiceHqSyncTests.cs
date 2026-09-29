using System.Reflection;
using System.Runtime.CompilerServices;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Mappings;
using BlazorApp.Api.Mappings.Profiles.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HqEntities;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class ProductReactServiceHqSyncTests : IDisposable
{
    private readonly string _localDbPath;
    private readonly string _hqDbPath;
    private readonly SqliteConnection _localConnection;
    private readonly SqliteConnection _hqConnection;
    private readonly SqlSugarClient _localDb;
    private readonly SqlSugarScope _hqDb;
    private readonly IMapper _mapper;

    public ProductReactServiceHqSyncTests()
    {
        _localDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _hqDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _localConnection = new SqliteConnection($"Data Source={_localDbPath}");
        _hqConnection = new SqliteConnection($"Data Source={_hqDbPath}");
        _localConnection.Open();
        _hqConnection.Open();

        _localDb = new SqlSugarClient(CreateConnectionConfig(_localConnection.ConnectionString));
        _hqDb = new SqlSugarScope(CreateConnectionConfig(_hqConnection.ConnectionString));
        _mapper = CreateMapper();

        // 只初始化本次同步链路依赖的最小表集合，避免测试基建过重。
        _localDb.CodeFirst.InitTables(
            typeof(Product),
            typeof(WarehouseProduct),
            typeof(Store),
            typeof(StoreRetailPrice),
            typeof(StoreMultiCodeProduct),
            typeof(ProductSetCode),
            typeof(WarehouseCategory),
            typeof(LocalSupplierCategory),
            typeof(LocalSupplierCategoryCapture),
            typeof(LocalSupplierCategoryProductAssignment)
        );
        _hqDb.CodeFirst.InitTables(
            typeof(DIC_商品信息字典表),
            typeof(DIC_商品零售价表),
            typeof(DIC_分店一品多码表)
        );
    }

    [Fact]
    public async Task SyncProductsFromHqAsync_本地存在同编码软删商品时_应该恢复原记录且不新增重复商品和关联表()
    {
        const string productCode = "P-RESTORE-001";
        var localUpdatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var hqUpdatedAt = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc);

        await SeedActiveStoreAsync("S01");
        await _localDb.Insertable(
            new Product
            {
                UUID = "local-product-restore",
                ProductCode = productCode,
                LocalSupplierCode = "SUP-OLD",
                ItemNumber = "ITEM-OLD",
                Barcode = "BAR-OLD",
                ProductName = "旧商品",
                ProductType = 1,
                PurchasePrice = 1.2m,
                RetailPrice = 2.4m,
                IsActive = false,
                IsAutoPricing = false,
                IsSpecialProduct = false,
                CreatedAt = localUpdatedAt,
                UpdatedAt = localUpdatedAt,
                IsDeleted = true,
            }
        ).ExecuteCommandAsync();

        await _localDb.Insertable(
            new StoreRetailPrice
            {
                UUID = "retail-restore-1",
                StoreCode = "S01",
                ProductCode = productCode,
                SupplierCode = "SUP-OLD",
                PurchasePrice = 1.2m,
                StoreRetailPriceValue = 2.4m,
                IsActive = false,
                IsAutoPricing = false,
                IsSpecialProduct = false,
                CreatedAt = localUpdatedAt,
                UpdatedAt = localUpdatedAt,
                IsDeleted = true,
            }
        ).ExecuteCommandAsync();

        await _localDb.Insertable(
            new StoreMultiCodeProduct
            {
                UUID = "multi-restore-1",
                StoreCode = "S01",
                ProductCode = productCode,
                MultiCodeProductCode = "MULTI-OLD",
                StoreMultiCodeProductCode = "S01-MULTI-OLD",
                MultiBarcode = "MULTI-BAR-OLD",
                PurchasePrice = 1.1m,
                MultiCodeRetailPrice = 2.3m,
                IsActive = false,
                IsAutoPricing = false,
                IsSpecialProduct = false,
                CreatedAt = localUpdatedAt,
                UpdatedAt = localUpdatedAt,
                IsDeleted = true,
            }
        ).ExecuteCommandAsync();

        await _localDb.Insertable(
            new ProductSetCode
            {
                SetCodeId = "set-restore-1",
                ProductCode = productCode,
                SetProductCode = "MULTI-OLD",
                SetItemNumber = "MULTI-OLD",
                SetBarcode = "MULTI-BAR-OLD",
                SetPurchasePrice = 1.1m,
                SetRetailPrice = 2.3m,
                SetType = 2,
                SetQuantity = 1,
                IsActive = false,
                CreatedAt = localUpdatedAt,
                UpdatedAt = localUpdatedAt,
                IsDeleted = true,
            }
        ).ExecuteCommandAsync();

        await SeedHqProductAsync(productCode, hqUpdatedAt, "SUP-NEW", "ITEM-NEW", "商品新名称", "BAR-NEW");
        await _hqDb.Insertable(
            new DIC_商品零售价表
            {
                HGUID = "hq-retail-restore-1",
                H分店代码 = "S01",
                H商品编码 = productCode,
                H分店商品编码 = "S01-P-RESTORE-001",
                H供应商编码 = "SUP-NEW",
                H分店供应商编码 = "S01-SUP-NEW",
                H进货价 = 8.8m,
                H分店零售价 = 12.8m,
                H折扣率 = 0.9m,
                H使用状态 = true,
                H是否自动定价 = true,
                H是否特殊商品 = true,
                FGC_CreateDate = hqUpdatedAt,
                FGC_LastModifyDate = hqUpdatedAt,
            }
        ).ExecuteCommandAsync();
        await _hqDb.Insertable(
            new DIC_分店一品多码表
            {
                HGUID = "hq-multi-restore-1",
                H分店代码 = "S01",
                H商品编码 = productCode,
                H分店商品编码 = "S01-P-RESTORE-001",
                H多码商品编码 = "MULTI-NEW",
                H分店多码商品编码 = "S01-MULTI-NEW",
                H供应商编码 = "SUP-NEW",
                H多条形码 = "MULTI-BAR-NEW",
                H进货价 = 8.3m,
                H折扣率 = 0.88m,
                H一品多码零售价 = 12.6m,
                H使用状态 = true,
                H是否自动定价 = true,
                H是否特殊商品 = true,
                FGC_CreateDate = hqUpdatedAt,
                FGC_LastModifyDate = hqUpdatedAt,
            }
        ).ExecuteCommandAsync();

        var service = CreateService();

        var response = await service.SyncProductsFromHqAsync();

        Assert.True(response.Success, response.Message);
        Assert.Empty(response.Data?.Errors ?? new List<string>());

        var products = await _localDb.Queryable<Product>()
            .Where(x => x.ProductCode == productCode)
            .ToListAsync();
        var retailPrices = await _localDb.Queryable<StoreRetailPrice>()
            .Where(x => x.ProductCode == productCode)
            .ToListAsync();
        var multiCodes = await _localDb.Queryable<StoreMultiCodeProduct>()
            .Where(x => x.ProductCode == productCode)
            .ToListAsync();
        var setCodes = await _localDb.Queryable<ProductSetCode>()
            .Where(x => x.ProductCode == productCode)
            .ToListAsync();

        var product = Assert.Single(products);
        Assert.Equal("local-product-restore", product.UUID);
        Assert.False(product.IsDeleted);
        Assert.Single(retailPrices);
        Assert.Equal("hq-retail-restore-1", retailPrices[0].UUID);
        Assert.False(retailPrices[0].IsDeleted);
        Assert.Single(multiCodes);
        Assert.Equal("hq-multi-restore-1", multiCodes[0].UUID);
        Assert.False(multiCodes[0].IsDeleted);
        Assert.Single(setCodes);
        Assert.Equal("hq-multi-restore-1", setCodes[0].SetCodeId);
        Assert.False(setCodes[0].IsDeleted);
    }

    [Fact]
    public async Task SyncProductsFromHqAsync_HQ同键门店多码不得覆盖本地套装派生成本()
    {
        const string productCode = "P-SET-PROTECTED";
        const string childCode = "M-PROTECTED";
        var now = new DateTime(2026, 6, 4, 0, 0, 0, DateTimeKind.Utc);

        await SeedActiveStoreAsync("S01");
        await _localDb.Insertable(
            new Product
            {
                UUID = "local-product-protected",
                ProductCode = productCode,
                ProductName = "本地套装",
                PurchasePrice = 8.8m,
                RetailPrice = 12m,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new StoreRetailPrice
            {
                UUID = "local-retail-protected",
                StoreCode = "S01",
                ProductCode = productCode,
                SupplierCode = "SUP-HQ",
                PurchasePrice = 1.11m,
                StoreRetailPriceValue = 10m,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new ProductSetCode
            {
                SetCodeId = "local-set-protected",
                ProductCode = productCode,
                SetProductCode = childCode,
                SetItemNumber = childCode,
                SetBarcode = "LOCAL-SET-BARCODE",
                SetPurchasePrice = 8.8m,
                SetRetailPrice = 4.56m,
                SetType = 1,
                SetQuantity = 1,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new StoreMultiCodeProduct
            {
                UUID = "local-store-multi-protected",
                StoreCode = "S01",
                ProductCode = productCode,
                MultiCodeProductCode = childCode,
                StoreMultiCodeProductCode = "S01-M-PROTECTED",
                MultiBarcode = "LOCAL-STORE-BARCODE",
                PurchasePrice = 1.11m,
                MultiCodeRetailPrice = 4.44m,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();

        await SeedHqProductAsync(productCode, now, "SUP-HQ", "ITEM-HQ", "HQ商品", "BAR-HQ");
        await _hqDb.Insertable(
            new DIC_分店一品多码表
            {
                HGUID = "hq-store-multi-protected",
                H分店代码 = "S01",
                H商品编码 = productCode,
                H分店商品编码 = "S01-P-SET-PROTECTED",
                H多码商品编码 = childCode,
                H分店多码商品编码 = "S01-M-PROTECTED",
                H供应商编码 = "SUP-HQ",
                H多条形码 = "HQ-SHOULD-NOT-APPLY",
                H进货价 = 9.99m,
                H一品多码零售价 = 19.99m,
                H使用状态 = true,
                FGC_CreateDate = now,
                FGC_LastModifyDate = now,
            }
        ).ExecuteCommandAsync();

        var response = await CreateService().SyncProductsFromHqAsync();

        Assert.True(response.Success, response.Message);
        var protectedChild = await _localDb.Queryable<ProductSetCode>()
            .SingleAsync(row => row.SetCodeId == "local-set-protected");
        Assert.Equal(1, protectedChild.SetType);
        Assert.Equal(8.8m, protectedChild.SetPurchasePrice);
        Assert.Equal(4.56m, protectedChild.SetRetailPrice);
        Assert.Equal("LOCAL-SET-BARCODE", protectedChild.SetBarcode);
        Assert.False(protectedChild.IsDeleted);
        var protectedStoreChild = await _localDb.Queryable<StoreMultiCodeProduct>()
            .SingleAsync(row => row.UUID == "local-store-multi-protected");
        Assert.Equal(1.11m, protectedStoreChild.PurchasePrice);
        Assert.Equal(4.44m, protectedStoreChild.MultiCodeRetailPrice);
        Assert.Equal("LOCAL-STORE-BARCODE", protectedStoreChild.MultiBarcode);
        Assert.False(protectedStoreChild.IsDeleted);
    }

    [Fact]
    public async Task UpsertProductSetCodesAsync_GUID与业务键交叉命中时保留两行()
    {
        const string productCode = "P-LEGACY-CROSS";
        const string targetChildCode = "CHILD-TARGET";
        var now = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc);
        await _localDb.Insertable(new[]
        {
            new ProductSetCode
            {
                SetCodeId = "hq-legacy-cross-guid",
                ProductCode = productCode,
                SetProductCode = "CHILD-GUID-OWNER",
                SetItemNumber = "CHILD-GUID-OWNER",
                SetBarcode = "LOCAL-GUID-OWNER",
                SetType = 2,
                SetQuantity = 1,
                IsActive = true,
                IsDeleted = false,
            },
            new ProductSetCode
            {
                SetCodeId = "local-legacy-key-owner",
                ProductCode = productCode,
                SetProductCode = targetChildCode,
                SetItemNumber = targetChildCode,
                SetBarcode = "HQ-TARGET-BARCODE",
                SetType = 2,
                SetQuantity = 1,
                IsActive = true,
                IsDeleted = false,
            },
        }).ExecuteCommandAsync();
        var hqRow = new DIC_分店一品多码表
        {
            ID = 1,
            HGUID = "hq-legacy-cross-guid",
            H分店代码 = "S01",
            H商品编码 = productCode,
            H分店商品编码 = $"S01-{productCode}",
            H多码商品编码 = targetChildCode,
            H分店多码商品编码 = "S01-CHILD-TARGET",
            H供应商编码 = "SUP-HQ",
            H多条形码 = "HQ-TARGET-BARCODE",
            H进货价 = 9.99m,
            H一品多码零售价 = 19.99m,
            H使用状态 = true,
            FGC_CreateDate = now,
            FGC_LastModifyDate = now,
        };
        var result = await InvokeProductSetCodeUpsertAsync(
            new List<string> { productCode },
            new List<DIC_分店一品多码表> { hqRow },
            now
        );

        Assert.Contains(result.Errors, error =>
            error.Contains("旧 ProductSetCode Upsert 身份冲突", StringComparison.Ordinal)
            && error.Contains("hq-legacy-cross-guid", StringComparison.Ordinal)
            && error.Contains("P-LEGACY-CROSS/CHILD-TARGET", StringComparison.Ordinal)
            && error.Contains("本地记录=", StringComparison.Ordinal)
        );
        var guidOwner = await _localDb.Queryable<ProductSetCode>()
            .SingleAsync(row => row.SetCodeId == "hq-legacy-cross-guid");
        var keyOwner = await _localDb.Queryable<ProductSetCode>()
            .SingleAsync(row => row.SetCodeId == "local-legacy-key-owner");
        Assert.Equal("CHILD-GUID-OWNER", guidOwner.SetProductCode);
        Assert.Equal("LOCAL-GUID-OWNER", guidOwner.SetBarcode);
        Assert.False(guidOwner.IsDeleted);
        Assert.Equal(targetChildCode, keyOwner.SetProductCode);
        Assert.Equal("HQ-TARGET-BARCODE", keyOwner.SetBarcode);
        Assert.False(keyOwner.IsDeleted);
    }

    [Fact]
    public async Task UpsertProductSetCodesAsync_仅GUID命中其他父商品且目标键空闲时安全迁移()
    {
        const string sourceProductCode = "P-LEGACY-GUID-SOURCE";
        const string targetProductCode = "P-LEGACY-GUID-TARGET";
        const string targetChildCode = "CHILD-TARGET";
        const string sharedGuid = "hq-legacy-guid-migration";
        var now = new DateTime(2026, 6, 5, 1, 0, 0, DateTimeKind.Utc);
        await _localDb.Insertable(
            new ProductSetCode
            {
                SetCodeId = sharedGuid,
                ProductCode = sourceProductCode,
                SetProductCode = "CHILD-SOURCE",
                SetItemNumber = "CHILD-SOURCE",
                SetBarcode = "LOCAL-SOURCE-BARCODE",
                SetType = 2,
                SetQuantity = 1,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        var hqRow = new DIC_分店一品多码表
        {
            ID = 1,
            HGUID = sharedGuid,
            H分店代码 = "S01",
            H商品编码 = targetProductCode,
            H多码商品编码 = targetChildCode,
            H多条形码 = "HQ-TARGET-BARCODE",
            H使用状态 = true,
            FGC_LastModifyDate = now,
        };

        var result = await InvokeProductSetCodeUpsertAsync(
            new List<string> { targetProductCode },
            new List<DIC_分店一品多码表> { hqRow },
            now
        );

        Assert.Equal(0, result.ProductSetCodesCreated);
        var migrated = await _localDb.Queryable<ProductSetCode>()
            .SingleAsync(row => row.SetCodeId == sharedGuid);
        Assert.Equal(targetProductCode, migrated.ProductCode);
        Assert.Equal(targetChildCode, migrated.SetProductCode);
        Assert.Equal("HQ-TARGET-BARCODE", migrated.SetBarcode);
        Assert.Equal(1, await _localDb.Queryable<ProductSetCode>().CountAsync());
    }

    [Fact]
    public async Task UpsertProductSetCodesAsync_源同业务键多非空GUID时拒绝并保留本地行()
    {
        const string productCode = "P-LEGACY-SOURCE-CONFLICT";
        const string childCode = "CHILD-CONFLICT";
        var now = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc);
        await _localDb.Insertable(
            new ProductSetCode
            {
                SetCodeId = "local-source-conflict",
                ProductCode = productCode,
                SetProductCode = childCode,
                SetItemNumber = childCode,
                SetBarcode = "LOCAL-MUST-STAY",
                SetType = 2,
                SetQuantity = 1,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        var hqRows = new[]
        {
            new DIC_分店一品多码表
            {
                ID = 1,
                HGUID = "hq-key-guid-a",
                H商品编码 = productCode,
                H多码商品编码 = childCode,
                H多条形码 = "HQ-A",
                H使用状态 = true,
                FGC_LastModifyDate = now,
            },
            new DIC_分店一品多码表
            {
                ID = 2,
                HGUID = "hq-key-guid-b",
                H商品编码 = productCode,
                H多码商品编码 = childCode,
                H多条形码 = "HQ-B",
                H使用状态 = true,
                FGC_LastModifyDate = now.AddMinutes(1),
            },
        };

        var result = await InvokeProductSetCodeUpsertAsync(
            new List<string> { productCode },
            hqRows.ToList(),
            now
        );

        Assert.Equal(0, result.ProductSetCodesCreated);
        var local = await _localDb.Queryable<ProductSetCode>()
            .SingleAsync(row => row.SetCodeId == "local-source-conflict");
        Assert.Equal(childCode, local.SetProductCode);
        Assert.Equal("LOCAL-MUST-STAY", local.SetBarcode);
        Assert.False(local.IsDeleted);
        Assert.Equal(1, await _localDb.Queryable<ProductSetCode>().CountAsync());
    }

    [Fact]
    public async Task UpsertProductSetCodesAsync_完全相同源身份只应用最新确定行()
    {
        const string productCode = "P-LEGACY-DUPLICATE";
        const string childCode = "CHILD-DUPLICATE";
        var now = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc);
        var hqRows = new[]
        {
            new DIC_分店一品多码表
            {
                ID = 1,
                HGUID = "hq-same-identity",
                H商品编码 = productCode,
                H多码商品编码 = childCode,
                H多条形码 = "HQ-NEWEST",
                H使用状态 = true,
                FGC_LastModifyDate = now.AddMinutes(2),
            },
            new DIC_分店一品多码表
            {
                ID = 2,
                HGUID = "hq-same-identity",
                H商品编码 = productCode,
                H多码商品编码 = childCode,
                H多条形码 = "HQ-OLDER-LAST",
                H使用状态 = true,
                FGC_LastModifyDate = now,
            },
        };

        var result = await InvokeProductSetCodeUpsertAsync(
            new List<string> { productCode },
            hqRows.ToList(),
            now
        );

        Assert.Equal(1, result.ProductSetCodesCreated);
        var local = await _localDb.Queryable<ProductSetCode>()
            .SingleAsync(row => row.ProductCode == productCode);
        Assert.Equal("hq-same-identity", local.SetCodeId);
        Assert.Equal("HQ-NEWEST", local.SetBarcode);
    }

    [Fact]
    public async Task SyncProductsFromHqAsync_历史失败时_所有写入回滚且结果计数归零()
    {
        var hqProducts = Enumerable.Range(1, 201)
            .Select(index => new DIC_商品信息字典表
            {
                ID = index,
                HGUID = $"hq-bulk-{index}",
                H商品标签GUID = $"tag-bulk-{index}",
                H商品分类码GUID = "CATEGORY-BULK",
                H商品编码 = $"P-BULK-{index:D3}",
                H供货商编码 = "SUP-BULK",
                H货号 = $"ITEM-{index:D3}",
                H主条形码 = $"BAR-{index:D3}",
                H商品名称 = $"批量商品{index}",
                H商品类型 = 1,
                H大写名称 = $"BULK PRODUCT {index}",
                H规格 = "默认规格",
                H单位 = "EA",
                H进货价 = 1m,
                H零售价 = 2m,
                H是否自动定价 = false,
                H商品图片 = "bulk-image.png",
                中包数量 = 1,
                H腾讯云图地址 = "https://example.invalid/bulk.png",
                H使用状态 = true,
                H是否特殊商品 = false,
                H进货单主表GUID = $"bulk-order-{index}",
                H进货单详情GUID = $"bulk-order-detail-{index}",
                CBP商品中文名称 = $"批量商品{index}",
                CBP供应商编码 = "SUP-BULK",
                CBP商品分类码GUID = "CATEGORY-BULK",
                FGC_Creator = "HQ",
                FGC_LastModifier = "HQ",
                FGC_CreateDate = DateTime.UtcNow.AddMinutes(-5),
                FGC_LastModifyDate = DateTime.UtcNow,
                FGC_UpdateHelp = "test",
            })
            .ToList();
        await _hqDb.Insertable(hqProducts).ExecuteCommandAsync();

        var historyService = new Mock<IWarehouseProductChangeHistoryService>(MockBehavior.Strict);
        WarehouseProductChangeHistoryContextDto? capturedContext = null;
        historyService
            .Setup(service => service.CaptureSnapshotsAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(new Dictionary<string, WarehouseProductChangeSnapshotDto>(
                StringComparer.OrdinalIgnoreCase
            ));
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
            .ThrowsAsync(new InvalidOperationException("历史写入失败"));

        var currentUserService = new Mock<ICurrentUserService>(MockBehavior.Strict);
        currentUserService.Setup(service => service.GetCurrentUserGuid()).Returns("user-guid-react");
        currentUserService.Setup(service => service.GetCurrentUsername()).Returns("React操作员");

        var response = await CreateServiceWithHistory(
            historyService.Object,
            currentUserService.Object
        )
            .SyncProductsFromHqAsync();

        Assert.False(response.Success);
        var failedResult = Assert.IsType<HqProductSyncResult>(response.Details);
        Assert.Equal(0, failedResult.ProductsAdded);
        Assert.Equal(0, failedResult.ProductsUpdated);
        Assert.Equal(0, failedResult.ProductsDeleted);
        Assert.Contains("历史写入失败", failedResult.Errors);
        Assert.NotNull(capturedContext);
        Assert.Equal("user-guid-react", capturedContext!.ActorUserGuid);
        Assert.Equal("React操作员", capturedContext.ActorName);
        Assert.Equal("User", capturedContext.ActorType);
        Assert.Equal(0, await _localDb.Queryable<Product>().CountAsync());
        historyService.Verify(service => service.RecordChangesAsync(
            It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
            It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
            It.IsAny<WarehouseProductChangeHistoryContextDto>(),
            It.IsAny<CancellationToken>()
        ), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_历史写入失败时_商品和门店镜像应一起回滚()
    {
        await SeedActiveStoreAsync("S01");
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
            .ThrowsAsync(new InvalidOperationException("history insert failed"));

        var service = CreateServiceWithHistory(historyService.Object);
        var response = await service.CreateAsync(
            new CreateProductDto
            {
                ProductCode = "P-AUDIT-ROLLBACK",
                ProductName = "审计回滚商品",
                LocalSupplierCode = "SUP",
                ItemNumber = "ITEM-AUDIT-ROLLBACK",
                IsActive = true,
            }
        );

        Assert.False(response.Success);
        // UseTranAsync 失败必须原样进入失败分支，不能在回滚后查询不到商品而掩盖根因。
        Assert.Contains("history insert failed", response.Message);
        Assert.Null(
            await _localDb.Queryable<Product>()
                .FirstAsync(item => item.ProductCode == "P-AUDIT-ROLLBACK")
        );
        Assert.Equal(
            0,
            await _localDb.Queryable<StoreRetailPrice>()
                .Where(item => item.ProductCode == "P-AUDIT-ROLLBACK")
                .CountAsync()
        );
        historyService.VerifyAll();
    }

    [Fact]
    public async Task BatchDeleteAsync_共享子项键时不删除其他父商品关系()
    {
        await SeedDeletableProductAsync("P-DELETE-A", "SHARED-CHILD");
        await SeedDeletableProductAsync("P-DELETE-B", "SHARED-CHILD");

        var response = await CreateService().BatchDeleteAsync(
            new List<string> { "P-DELETE-A" },
            isSoftDelete: true
        );

        Assert.True(response.Success, response.Message);
        Assert.Equal(1, response.Data!.SuccessCount);
        var otherParentRelation = await _localDb.Queryable<ProductSetCode>()
            .SingleAsync(row => row.SetCodeId == "P-DELETE-B-SET");
        Assert.False(otherParentRelation.IsDeleted);
    }

    [Fact]
    public async Task BatchDeleteAsync_单商品级联失败时回滚该商品并继续其他商品()
    {
        await SeedDeletableProductAsync("P-DELETE-FAIL", "FAIL-CHILD");
        await SeedDeletableProductAsync("P-DELETE-OK", "OK-CHILD");
        await _localDb.Ado.ExecuteCommandAsync(
            """
            CREATE TRIGGER RejectFailingProductStoreMultiDelete
            BEFORE UPDATE OF IsDeleted ON StoreMultiCodeProduct
            WHEN OLD.ProductCode = 'P-DELETE-FAIL' AND NEW.IsDeleted = 1
            BEGIN
                SELECT RAISE(ABORT, 'injected store multi failure');
            END;
            """
        );

        var response = await CreateService().BatchDeleteAsync(
            new List<string> { "P-DELETE-FAIL", "P-DELETE-OK" },
            isSoftDelete: true
        );

        Assert.True(response.Success, response.Message);
        Assert.Equal(1, response.Data!.SuccessCount);
        Assert.Equal(1, response.Data.FailedCount);
        Assert.False((await _localDb.Queryable<Product>()
            .SingleAsync(row => row.ProductCode == "P-DELETE-FAIL")).IsDeleted);
        Assert.False((await _localDb.Queryable<ProductSetCode>()
            .SingleAsync(row => row.SetCodeId == "P-DELETE-FAIL-SET")).IsDeleted);
        Assert.False((await _localDb.Queryable<StoreMultiCodeProduct>()
            .SingleAsync(row => row.UUID == "P-DELETE-FAIL-MULTI")).IsDeleted);
        Assert.True((await _localDb.Queryable<Product>()
            .SingleAsync(row => row.ProductCode == "P-DELETE-OK")).IsDeleted);
    }

    [Fact]
    public async Task BatchUpdateAsync_整体事务失败时清零成功数并返回结构化失败详情()
    {
        var response = await CreateService().BatchUpdateAsync(null!);

        Assert.False(response.Success);
        var data = Assert.IsType<BatchOperationReactResult>(response.Data);
        Assert.Equal(0, data.SuccessCount);
        Assert.Equal(1, data.FailedCount);
        var failure = Assert.Single(data.FailureDetails);
        Assert.Equal(response.Message, failure.Message);
        Assert.Single(data.Errors);
    }

    public void Dispose()
    {
        _localDb.Dispose();
        _hqDb.Dispose();
        _localConnection.Dispose();
        _hqConnection.Dispose();

        if (File.Exists(_localDbPath))
        {
            SqliteTempFileCleanup.DeleteIfExists(_localDbPath);
        }

        if (File.Exists(_hqDbPath))
        {
            SqliteTempFileCleanup.DeleteIfExists(_hqDbPath);
        }
    }

    private ProductReactService CreateService()
    {
        return new ProductReactService(
            CreateSqlSugarContext(_localDb),
            CreateHqSqlSugarContext(_hqDb, CreateHqConfiguration(_hqConnection.ConnectionString)),
            _mapper,
            NullLogger<ProductReactService>.Instance,
            new HttpContextAccessor(),
            new ProductAuditNoopHistoryService(),
            new ProductAuditSystemCurrentUserService()
        );
    }

    private async Task<HqProductSyncResult> InvokeProductSetCodeUpsertAsync(
        List<string> productCodes,
        List<DIC_分店一品多码表> hqRows,
        DateTime now
    )
    {
        var method = typeof(ProductReactService).GetMethod(
            "UpsertProductSetCodesAsync",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.NotNull(method);
        var result = new HqProductSyncResult();
        var invocation = method!.Invoke(
            CreateService(),
            new object[] { productCodes, hqRows, now, result }
        );
        await Assert.IsAssignableFrom<Task>(invocation);
        return result;
    }

    private async Task SeedDeletableProductAsync(string productCode, string childCode)
    {
        await _localDb.Insertable(new Product
        {
            UUID = $"{productCode}-UUID",
            ProductCode = productCode,
            ProductName = productCode,
            PurchasePrice = 10m,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new ProductSetCode
        {
            SetCodeId = $"{productCode}-SET",
            ProductCode = productCode,
            SetProductCode = childCode,
            SetItemNumber = childCode,
            SetBarcode = childCode,
            SetPurchasePrice = 10m,
            SetRetailPrice = 20m,
            SetType = 1,
            SetQuantity = 1,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new StoreMultiCodeProduct
        {
            UUID = $"{productCode}-MULTI",
            StoreCode = "S01",
            ProductCode = productCode,
            MultiCodeProductCode = childCode,
            StoreMultiCodeProductCode = $"S01-{productCode}-{childCode}",
            MultiBarcode = childCode,
            PurchasePrice = 10m,
            MultiCodeRetailPrice = 20m,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new StoreRetailPrice
        {
            UUID = $"{productCode}-PRICE",
            StoreCode = "S01",
            ProductCode = productCode,
            PurchasePrice = 10m,
            StoreRetailPriceValue = 20m,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private ProductReactService CreateServiceWithHistory(
        IWarehouseProductChangeHistoryService historyService,
        ICurrentUserService? currentUserService = null
    )
    {
        return new ProductReactService(
            CreateSqlSugarContext(_localDb),
            CreateHqSqlSugarContext(_hqDb, CreateHqConfiguration(_hqConnection.ConnectionString)),
            _mapper,
            NullLogger<ProductReactService>.Instance,
            new HttpContextAccessor(),
            historyService,
            currentUserService ?? new ProductAuditSystemCurrentUserService()
        );
    }

    private async Task SeedActiveStoreAsync(string storeCode)
    {
        await _localDb.Insertable(
            new Store
            {
                StoreGUID = $"store-{storeCode}",
                StoreCode = storeCode,
                StoreName = $"门店{storeCode}",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
    }

    private async Task SeedHqProductAsync(
        string productCode,
        DateTime lastModifyDate,
        string supplierCode,
        string itemNumber,
        string productName,
        string barcode
    )
    {
        await _hqDb.Insertable(
            new DIC_商品信息字典表
            {
                HGUID = $"hq-product-{productCode}",
                H商品标签GUID = $"tag-{productCode}",
                H商品分类码GUID = "CATEGORY-NEW",
                H供货商编码 = supplierCode,
                H商品编码 = productCode,
                H货号 = itemNumber,
                H主条形码 = barcode,
                H商品名称 = productName,
                H商品类型 = 2,
                H大写名称 = productName.ToUpperInvariant(),
                H规格 = "默认规格",
                H单位 = "EA",
                H进货价 = 8.8m,
                H零售价 = 12.8m,
                H是否自动定价 = true,
                H商品图片 = "hq-image.png",
                中包数量 = 6,
                H腾讯云图地址 = "https://example.invalid/image.png",
                H使用状态 = true,
                H是否特殊商品 = true,
                H进货单主表GUID = $"order-{productCode}",
                H进货单详情GUID = $"order-detail-{productCode}",
                CBP商品中文名称 = productName,
                CBP供应商编码 = supplierCode,
                CBP商品分类码GUID = "WAREHOUSE-NEW",
                FGC_Creator = "HQ",
                FGC_LastModifier = "HQ",
                FGC_CreateDate = lastModifyDate.AddMinutes(-10),
                FGC_LastModifyDate = lastModifyDate,
                FGC_UpdateHelp = "test",
            }
        ).ExecuteCommandAsync();
    }

    private static ConnectionConfig CreateConnectionConfig(string connectionString) =>
        new()
        {
            ConnectionString = connectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        };

    private static IMapper CreateMapper()
    {
        var configuration = new MapperConfiguration(
            cfg => cfg.AddProfile<ReactProductMappingProfile>(),
            NullLoggerFactory.Instance
        );
        return configuration.CreateMapper();
    }

    private static IConfiguration CreateHqConfiguration(string connectionString)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:StoreHzgHQConnection"] = connectionString,
                }
            )
            .Build();
    }

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        var dbField = typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic);
        dbField!.SetValue(context, db);
        return context;
    }

    private static HqSqlSugarContext CreateHqSqlSugarContext(
        ISqlSugarClient db,
        IConfiguration configuration
    )
    {
        var context = (HqSqlSugarContext)RuntimeHelpers.GetUninitializedObject(
            typeof(HqSqlSugarContext)
        );

        // 这里显式注入 SqlSugar 与配置，确保测试能稳定命中真实同步逻辑。
        var dbField = typeof(HqSqlSugarContext).GetField(
            "_db",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        dbField!.SetValue(context, db);

        var configurationField = typeof(HqSqlSugarContext).GetField(
            "<Configuration>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        configurationField!.SetValue(context, configuration);

        return context;
    }
}

internal sealed class ProductAuditNoopHistoryService : IWarehouseProductChangeHistoryService
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
    ) => Task.FromResult(0);

    public Task<WarehouseProductChangeHistoryPageDto> GetChangeHistoryAsync(
        string productCode,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default
    ) => Task.FromResult(new WarehouseProductChangeHistoryPageDto());
}

internal sealed class ProductAuditSystemCurrentUserService : ICurrentUserService
{
    public string GetCurrentUsername() => "System";

    public string GetCurrentUserGuid() => string.Empty;
}
