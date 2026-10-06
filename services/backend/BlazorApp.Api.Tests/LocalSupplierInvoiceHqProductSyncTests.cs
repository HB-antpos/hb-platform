using System.Reflection;
using System.Runtime.CompilerServices;
using System.ComponentModel.DataAnnotations;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HqEntities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class LocalSupplierInvoiceHqProductSyncTests : IDisposable
{
    private readonly string _localDbPath;
    private readonly string _hqDbPath;
    private readonly SqliteConnection _localConnection;
    private readonly SqliteConnection _hqConnection;
    private readonly SqlSugarClient _localDb;
    private readonly SqlSugarClient _hqDb;

    public LocalSupplierInvoiceHqProductSyncTests()
    {
        _localDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _hqDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _localConnection = new SqliteConnection($"Data Source={_localDbPath}");
        _hqConnection = new SqliteConnection($"Data Source={_hqDbPath}");
        _localConnection.Open();
        _hqConnection.Open();

        _localDb = CreateSqlSugarClient(_localConnection.ConnectionString);
        _hqDb = CreateSqlSugarClient(_hqConnection.ConnectionString);

        _localDb.CodeFirst.InitTables(
            typeof(Store),
            typeof(Product),
            typeof(WarehouseProduct),
            typeof(DomesticProduct),
            typeof(StoreRetailPrice),
            typeof(ProductSetCode),
            typeof(StoreMultiCodeProduct),
            typeof(StoreLocalSupplierInvoice),
            typeof(StoreLocalSupplierInvoiceDetails)
        );
        _hqDb.CodeFirst.InitTables(
            typeof(DIC_商品信息字典表),
            typeof(DIC_商品零售价表),
            typeof(DIC_一品多码表),
            typeof(DIC_分店一品多码表)
        );
    }

    [Fact]
    public void EnsureHqProducts_后端契约与前端一致()
    {
        Assert.Null(typeof(UpdateToStorePricesRequest).GetProperty("UpdateHqProduct"));
        Assert.True(typeof(UpdateToStorePricesResultDto).IsSubclassOf(typeof(BatchResultDto)));
        Assert.NotNull(typeof(EnsureHqProductsRequest).GetProperty("DetailGuids"));
        Assert.NotNull(typeof(EnsureHqProductsRequest).GetProperty("TargetStoreCodes"));
        Assert.NotNull(typeof(EnsureHqProductsResult).GetProperty("HqPurchasePricesUpdated"));
        Assert.NotNull(typeof(UpdateHqProductsResult).GetProperty("HqProductSetCodesCreated"));
        Assert.NotNull(typeof(UpdateHqProductsResult).GetProperty("HqProductSetCodesUpdated"));
        Assert.NotNull(typeof(UpdateHqProductsResult).GetProperty("HqStoreMultiCodesCreated"));
        Assert.NotNull(typeof(UpdateHqProductsResult).GetProperty("HqStoreMultiCodesUpdated"));
        AssertRequiredProperty<UpdateHqProductsRequest>("DetailGuids");
        AssertRequiredProperty<UpdateHqProductsRequest>("TargetStoreCodes");
        AssertRequiredProperty<UpdateHqProductsRequest>("UpdateFields");

        var method = typeof(ReactLocalSupplierInvoicesController).GetMethod("EnsureHqProducts");
        Assert.NotNull(method);

        var route = Assert.Single(method!.GetCustomAttributes<HttpPostAttribute>());
        Assert.Equal("{invoiceGuid}/details/ensure-hq-products", route.Template);

        var auth = Assert.Single(method.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(Permissions.LocalPurchase.Edit, auth.Policy);

        var updateMethod = typeof(ReactLocalSupplierInvoicesController).GetMethod("UpdateHqProducts");
        Assert.NotNull(updateMethod);

        var updateRoute = Assert.Single(updateMethod!.GetCustomAttributes<HttpPostAttribute>());
        Assert.Equal("{invoiceGuid}/details/update-hq-products", updateRoute.Template);

        var updatePolicies = updateMethod
            .GetCustomAttributes<AuthorizeAttribute>()
            .Select(x => x.Policy)
            .ToArray();
        Assert.Contains(Permissions.LocalPurchase.Edit, updatePolicies);
        Assert.Contains(Permissions.LocalPurchase.PushToHq, updatePolicies);
    }

    [Fact]
    public async Task UpdateHqProductsAsync_空请求字段_返回稳定校验错误()
    {
        var service = CreateSyncService();

        var nullRequestResult = await service.UpdateHqProductsAsync("invoice-1", null, "tester");
        Assert.False(nullRequestResult.Success);
        Assert.Equal("VALIDATION_ERROR", nullRequestResult.Code);

        var nullDetailResult = await service.UpdateHqProductsAsync(
            "invoice-1",
            new UpdateHqProductsRequest
            {
                DetailGuids = null!,
                TargetStoreCodes = new List<string> { "S01" },
                UpdateFields = new UpdateToStorePricesFields { UpdateRetailPrice = true },
            },
            "tester"
        );
        Assert.False(nullDetailResult.Success);
        Assert.Equal("VALIDATION_ERROR", nullDetailResult.Code);

        var nullFieldsResult = await service.UpdateHqProductsAsync(
            "invoice-1",
            new UpdateHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-1" },
                TargetStoreCodes = new List<string> { "S01" },
                UpdateFields = null!,
            },
            "tester"
        );
        Assert.False(nullFieldsResult.Success);
        Assert.Equal("VALIDATION_ERROR", nullFieldsResult.Code);
    }

    [Fact]
    public async Task UpdateHqProductsAsync_只更新勾选字段_未勾选字段保持不变()
    {
        await SeedStoreAsync("S01", true);
        await SeedStoreAsync("S02", true);
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");
        await SeedExistingProductAsync("P-001", "SUP01");
        await SeedHqProductAsync("P-001", 5m, 10m);
        await SeedHqPriceAsync("S01", "P-001", 5m, 10m);
        await SeedHqPriceAsync("S02", "P-001", 6m, 12m);
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-1",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-001",
            PurchasePrice = 8m,
            RetailPrice = 18m,
            AutoPricing = true,
            IsSpecialProduct = true,
            DiscountRate = 0.25m,
            IsDeleted = false,
        });

        var result = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-1",
            new UpdateHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-1" },
                TargetStoreCodes = new List<string> { "S01" },
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdateIsAutoPricing = true,
                },
            },
            "tester"
        );

        Assert.True(result.Success, BuildFailureMessage(result));
        Assert.Equal(1, result.Data!.Updated);
        Assert.Equal(1, result.Data.HqAutoPricingUpdated);
        Assert.Equal(0, result.Data.HqPurchasePricesUpdated);
        Assert.Equal(0, result.Data.HqRetailPricesUpdated);
        Assert.Equal(0, result.Data.HqSpecialProductsUpdated);
        Assert.Equal(0, result.Data.HqDiscountRatesUpdated);

        var hqS01 = await _hqDb.Queryable<DIC_商品零售价表>()
            .FirstAsync(x => x.H分店代码 == "S01" && x.H商品编码 == "P-001");
        var hqS02 = await _hqDb.Queryable<DIC_商品零售价表>()
            .FirstAsync(x => x.H分店代码 == "S02" && x.H商品编码 == "P-001");

        Assert.True(hqS01.H是否自动定价);
        Assert.Equal(5m, hqS01.H进货价);
        Assert.Equal(10m, hqS01.H分店零售价);
        Assert.False(hqS01.H是否特殊商品);
        Assert.Equal(0m, hqS01.H折扣率);
        Assert.False(hqS02.H是否自动定价);
        Assert.Equal(6m, hqS02.H进货价);
        Assert.Equal(12m, hqS02.H分店零售价);
    }

    [Fact]
    public async Task UpdateHqProductsAsync_同批商品实际更新字段不同_分组写入且库存活动字段保持不变()
    {
        await SeedStoreAsync("S01", true);
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");
        await SeedExistingProductAsync("P-001", "SUP01", "ITEM-001", "930000030001");
        await SeedExistingProductAsync("P-002", "SUP01", "ITEM-002", "930000030002");
        await SeedHqProductAsync("P-001", 5m, 10m, "ITEM-001", "930000030001");
        await SeedHqProductAsync("P-002", 6m, 12m, "ITEM-002", "930000030002", 2);
        await SeedHqPriceAsync("S01", "P-001", 5m, 10m);
        await _hqDb.Insertable(new DIC_商品零售价表
        {
            ID = 2,
            HGUID = "hq-price-S01-P-002",
            H分店代码 = "S01",
            H商品编码 = "P-002",
            H分店商品编码 = "S01P-002",
            H供应商编码 = "SUP01",
            H分店供应商编码 = "S01SUP01",
            H进货价 = 6m,
            H分店零售价 = 12m,
            H库存 = 222m,
            H库存金额 = 2664m,
            H活动类型 = "满减",
            H满减活动代码 = "PROMO-002",
            H使用状态 = true,
            FGC_Creator = "seed",
            FGC_CreateDate = DateTime.UtcNow,
            FGC_LastModifier = "seed",
            FGC_LastModifyDate = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        await _hqDb.Updateable<DIC_商品零售价表>()
            .SetColumns(price => new DIC_商品零售价表
            {
                H库存 = 111m,
                H库存金额 = 1110m,
                H活动类型 = "折扣",
                H满减活动代码 = "PROMO-001",
            })
            .Where(price => price.H分店代码 == "S01" && price.H商品编码 == "P-001")
            .ExecuteCommandAsync();
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-field-group-1",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-001",
            ItemNumber = "ITEM-001",
            Barcode = "930000030001",
            PurchasePrice = 8m,
            RetailPrice = 18m,
            IsDeleted = false,
        });
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-field-group-2",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-002",
            ItemNumber = "ITEM-002",
            Barcode = "930000030002",
            PurchasePrice = 0m,
            RetailPrice = 19m,
            IsDeleted = false,
        });
        await _hqDb.Ado.ExecuteCommandAsync(
            """
            CREATE TRIGGER "reject_unrelated_hq_price_columns"
            BEFORE UPDATE OF "H库存", "H库存金额", "H活动类型", "H满减活动代码", "H动态销售数量"
            ON "DIC_商品零售价表"
            BEGIN
                SELECT RAISE(ABORT, 'unrelated HQ price column updated');
            END;
            """
        );

        var hqPriceWriteCommands = 0;
        _hqDb.Aop.OnLogExecuting = (sql, _) =>
        {
            if (IsTableWriteCommand(sql, "DIC_商品零售价表"))
                hqPriceWriteCommands++;
        };

        ApiResponse<UpdateHqProductsResult> response;
        try
        {
            response = await CreateSyncService().UpdateHqProductsAsync(
                "invoice-1",
                new UpdateHqProductsRequest
                {
                    DetailGuids = new List<string> { "detail-field-group-1", "detail-field-group-2" },
                    TargetStoreCodes = new List<string> { "S01" },
                    UpdateFields = new UpdateToStorePricesFields
                    {
                        UpdatePurchasePrice = true,
                        UpdateRetailPrice = true,
                    },
                },
                "tester"
            );
        }
        finally
        {
            _hqDb.Aop.OnLogExecuting = null;
        }

        Assert.True(response.Success, BuildFailureMessage(response));
        Assert.Equal(2, response.Data!.Updated);
        Assert.Equal(1, response.Data.HqPurchasePricesUpdated);
        Assert.Equal(2, response.Data.HqRetailPricesUpdated);
        Assert.Equal(2, hqPriceWriteCommands);

        var prices = await _hqDb.Queryable<DIC_商品零售价表>()
            .OrderBy(price => price.H商品编码)
            .ToListAsync();
        Assert.Collection(
            prices,
            price =>
            {
                Assert.Equal((8m, 18m), (price.H进货价, price.H分店零售价));
                Assert.Equal((111m, 1110m), (price.H库存, price.H库存金额));
                Assert.Equal(("折扣", "PROMO-001"), (price.H活动类型, price.H满减活动代码));
            },
            price =>
            {
                Assert.Equal((6m, 19m), (price.H进货价, price.H分店零售价));
                Assert.Equal((222m, 2664m), (price.H库存, price.H库存金额));
                Assert.Equal(("满减", "PROMO-002"), (price.H活动类型, price.H满减活动代码));
            }
        );
    }

    [Fact]
    public async Task UpdateHqProductsAsync_同步本地多码关系到HQ()
    {
        await SeedStoreAsync("S01", true);
        await SeedStoreAsync("S02", true);
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");
        await SeedExistingProductAsync("P-001", "SUP01");
        await SeedHqProductAsync("P-001", 5m, 10m);
        await SeedHqPriceAsync("S01", "P-001", 5m, 10m);
        await SeedHqPriceAsync("S02", "P-001", 5m, 10m);
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-hq-multicode",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-001",
            ItemNumber = "88842",
            Barcode = "191554882676",
            PurchasePrice = 8m,
            RetailPrice = 18m,
            AutoPricing = true,
            IsSpecialProduct = true,
            IsDeleted = false,
        });
        await _localDb.Insertable(new ProductSetCode
        {
            SetCodeId = "set-code-1",
            ProductCode = "P-001",
            SetProductCode = "MC-001",
            SetItemNumber = "88842",
            SetBarcode = "191554882690",
            SetPurchasePrice = 8m,
            SetRetailPrice = 18m,
            SetQuantity = 1,
            SetType = 2,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new[]
        {
            new StoreMultiCodeProduct
            {
                UUID = "store-multi-s01",
                StoreCode = "S01",
                ProductCode = "P-001",
                MultiCodeProductCode = "MC-001",
                StoreMultiCodeProductCode = "S01MC-001",
                MultiBarcode = "191554882690",
                PurchasePrice = 8m,
                MultiCodeRetailPrice = 18m,
                DiscountRate = 0.12m,
                IsAutoPricing = true,
                IsSpecialProduct = true,
                IsActive = true,
                IsDeleted = false,
            },
            new StoreMultiCodeProduct
            {
                UUID = "store-multi-s02",
                StoreCode = "S02",
                ProductCode = "P-001",
                MultiCodeProductCode = "MC-001",
                StoreMultiCodeProductCode = "S02MC-001",
                MultiBarcode = "191554882690",
                PurchasePrice = 9m,
                MultiCodeRetailPrice = 19m,
                DiscountRate = 0.22m,
                IsAutoPricing = false,
                IsSpecialProduct = false,
                IsActive = true,
                IsDeleted = false,
            },
        }).ExecuteCommandAsync();

        var firstResult = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-1",
            new UpdateHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-hq-multicode" },
                TargetStoreCodes = new List<string> { "S01" },
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdatePurchasePrice = true,
                },
            },
            "tester"
        );

        Assert.True(firstResult.Success, BuildFailureMessage(firstResult));
        Assert.Equal(1, firstResult.Data!.HqProductSetCodesCreated);
        Assert.Equal(1, firstResult.Data.HqStoreMultiCodesCreated);
        Assert.Equal(1, await _hqDb.Queryable<DIC_一品多码表>().CountAsync());
        Assert.Equal(1, await _hqDb.Queryable<DIC_分店一品多码表>().CountAsync());

        var hqSetCode = await _hqDb.Queryable<DIC_一品多码表>().SingleAsync();
        var hqStoreMulti = await _hqDb.Queryable<DIC_分店一品多码表>().SingleAsync();
        var localSetCode = await _localDb.Queryable<ProductSetCode>().SingleAsync();
        var localStoreMulti = await _localDb.Queryable<StoreMultiCodeProduct>()
            .SingleAsync(x => x.StoreCode == "S01");
        Assert.Equal("P-001", hqSetCode.H商品编码);
        Assert.Equal("MC-001", hqSetCode.H多码商品编号);
        Assert.Equal("191554882690", hqSetCode.H多条形码);
        // 推送前必须先用本地主成本回算，不能把进货单明细的 8 写到 HQ 多码关系。
        Assert.Equal(5m, localSetCode.SetPurchasePrice);
        Assert.Equal(5m, localStoreMulti.PurchasePrice);
        Assert.Equal(5m, hqSetCode.H进货价);
        Assert.Equal("S01", hqStoreMulti.H分店代码);
        Assert.Equal("S01MC-001", hqStoreMulti.H分店多码商品编码);
        Assert.Equal(5m, hqStoreMulti.H进货价);
        Assert.Equal(18m, hqStoreMulti.H一品多码零售价);

        var secondResult = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-1",
            new UpdateHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-hq-multicode" },
                TargetStoreCodes = new List<string> { "S01" },
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdatePurchasePrice = true,
                },
            },
            "tester"
        );

        Assert.True(secondResult.Success, BuildFailureMessage(secondResult));
        Assert.Equal(0, secondResult.Data!.HqProductSetCodesCreated);
        Assert.Equal(1, secondResult.Data.HqProductSetCodesUpdated);
        Assert.Equal(0, secondResult.Data.HqStoreMultiCodesCreated);
        Assert.Equal(1, secondResult.Data.HqStoreMultiCodesUpdated);
        Assert.Equal(1, await _hqDb.Queryable<DIC_一品多码表>().CountAsync());
        Assert.Equal(1, await _hqDb.Queryable<DIC_分店一品多码表>().CountAsync());
    }

    [Fact]
    public async Task EnsureHqProductsAsync_受影响多码投影不完整_本地价格和明细一起回滚()
    {
        await SeedStoreAsync("S01", true);
        await SeedInvoiceAsync("invoice-rollback", "S01", "SUP01");
        await SeedExistingProductAsync("P-ROLLBACK", "SUP01");
        await SeedLocalPriceAsync("S01", "P-ROLLBACK", 5m, 10m);
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-rollback",
            InvoiceGUID = "invoice-rollback",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-ROLLBACK",
            ItemNumber = "ITEM-OLD",
            Barcode = "930000000000",
            PurchasePrice = 8m,
            RetailPrice = 18m,
            IsDeleted = false,
        });
        await _localDb.Insertable(new ProductSetCode
        {
            SetCodeId = "set-incomplete",
            ProductCode = "P-ROLLBACK",
            SetProductCode = "MC-MISSING",
            SetItemNumber = "ITEM-MC",
            SetBarcode = "930000000001",
            SetPurchasePrice = 99m,
            SetRetailPrice = 18m,
            SetType = 2,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();

        var response = await CreateSyncService().EnsureHqProductsAsync(
            "invoice-rollback",
            new EnsureHqProductsRequest
            {
                DetailGuids = ["detail-rollback"],
                TargetStoreCodes = ["S01"],
            },
            "tester"
        );

        Assert.False(response.Success);
        var storePrice = await _localDb.Queryable<StoreRetailPrice>()
            .SingleAsync(row => row.StoreCode == "S01" && row.ProductCode == "P-ROLLBACK");
        var detail = await _localDb.Queryable<StoreLocalSupplierInvoiceDetails>()
            .SingleAsync(row => row.DetailGUID == "detail-rollback");
        var setCode = await _localDb.Queryable<ProductSetCode>()
            .SingleAsync(row => row.SetCodeId == "set-incomplete");
        Assert.Equal((5m, 10m), (storePrice.PurchasePrice, storePrice.StoreRetailPriceValue));
        Assert.Null(detail.LastPurchasePrice);
        Assert.Equal(99m, setCode.SetPurchasePrice);
        Assert.Equal(0, await _hqDb.Queryable<DIC_商品信息字典表>().CountAsync());
    }

    [Fact]
    public async Task UpdateHqProductsAsync_多个多码与多个目标分店_多码写入按表批量执行()
    {
        foreach (var storeCode in new[] { "S01", "S02", "S03" })
        {
            await SeedStoreAsync(storeCode, true);
        }
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");
        await SeedExistingProductAsync("P-001", "SUP01");
        await SeedHqProductAsync("P-001", 5m, 10m);
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-hq-multicode-batch",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-001",
            ItemNumber = "ITEM-MULTI-BATCH",
            Barcode = "930000020000",
            PurchasePrice = 8m,
            RetailPrice = 18m,
            AutoPricing = true,
            IsDeleted = false,
        });
        await _localDb.Insertable(
            Enumerable.Range(1, 41).Select(index => new ProductSetCode
            {
                SetCodeId = $"set-code-batch-{index}",
                ProductCode = "P-001",
                SetProductCode = $"MC-BATCH-{index}",
                SetItemNumber = "ITEM-MULTI-BATCH",
                SetBarcode = $"93000002{index:D5}",
                SetPurchasePrice = 8m + index,
                SetRetailPrice = 18m + index,
                SetQuantity = 1,
                SetType = 2,
                IsActive = true,
                IsDeleted = false,
            }).ToList()
        ).ExecuteCommandAsync();
        await SeedStoreMultiCodeProjectionsForAllActiveRelationsAsync("P-001");

        var productSetWriteCommands = 0;
        var storeMultiWriteCommands = 0;
        _hqDb.Aop.OnLogExecuting = (sql, _) =>
        {
            if (IsTableWriteCommand(sql, "DIC_一品多码表"))
                productSetWriteCommands++;
            if (IsTableWriteCommand(sql, "DIC_分店一品多码表"))
                storeMultiWriteCommands++;
        };

        try
        {
            var response = await CreateSyncService().UpdateHqProductsAsync(
                "invoice-1",
                new UpdateHqProductsRequest
                {
                    DetailGuids = new List<string> { "detail-hq-multicode-batch" },
                    TargetStoreCodes = new List<string> { "S01", "S02", "S03" },
                    UpdateFields = new UpdateToStorePricesFields
                    {
                        UpdatePurchasePrice = true,
                    },
                },
                "tester"
            );

            Assert.True(response.Success, BuildFailureMessage(response));
            Assert.Equal(41, response.Data!.HqProductSetCodesCreated);
            Assert.Equal(123, response.Data.HqStoreMultiCodesCreated);
        }
        finally
        {
            _hqDb.Aop.OnLogExecuting = null;
        }

        Assert.Equal(41, await _hqDb.Queryable<DIC_一品多码表>().CountAsync());
        Assert.Equal(123, await _hqDb.Queryable<DIC_分店一品多码表>().CountAsync());
        Assert.True(productSetWriteCommands <= 2, $"HQ商品多码写入命令数为 {productSetWriteCommands}");
        Assert.True(storeMultiWriteCommands <= 4, $"HQ分店多码写入命令数为 {storeMultiWriteCommands}");
    }

    [Fact]
    public async Task UpdateHqProductsAsync_多码批量失败且备用键为空_不覆盖无关HQ多码()
    {
        await SeedStoreAsync("S01", true);
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");
        await SeedExistingProductAsync("P-001", "SUP01");
        await SeedHqProductAsync("P-001", 5m, 10m);
        await SeedHqPriceAsync("S01", "P-001", 5m, 10m);
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-hq-multicode-empty-fallback-key",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-001",
            ItemNumber = "ITEM-MULTI-EMPTY-FALLBACK",
            Barcode = "930000030000",
            PurchasePrice = 8m,
            RetailPrice = 18m,
            IsDeleted = false,
        });
        await _localDb.Insertable(new[]
        {
            new ProductSetCode
            {
                SetCodeId = "set-code-new-empty-barcode",
                ProductCode = "P-001",
                SetProductCode = "MC-NEW",
                SetBarcode = string.Empty,
                SetPurchasePrice = 8m,
                SetRetailPrice = 18m,
                IsActive = true,
                IsDeleted = false,
            },
            new ProductSetCode
            {
                SetCodeId = "set-code-reject",
                ProductCode = "P-001",
                SetProductCode = "MC-REJECT",
                SetBarcode = "930000030002",
                SetPurchasePrice = 9m,
                SetRetailPrice = 19m,
                IsActive = true,
                IsDeleted = false,
            },
        }).ExecuteCommandAsync();
        await SeedStoreMultiCodeProjectionsForAllActiveRelationsAsync("P-001");
        await _hqDb.Insertable(new DIC_一品多码表
        {
            ID = 1,
            HGUID = "hq-unrelated-empty-barcode",
            H商品编码 = "P-001",
            H多码商品编号 = "MC-EXISTING",
            H供应商编码 = "SUP01",
            H主条形码 = "930000030000",
            H多条形码 = string.Empty,
            H进货价 = 1m,
            H一品多码零售价 = 2m,
            H使用状态 = true,
            FGC_Creator = "seed",
            FGC_CreateDate = DateTime.UtcNow,
            FGC_LastModifier = "seed",
            FGC_LastModifyDate = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        await _hqDb.Ado.ExecuteCommandAsync(
            """
            CREATE TRIGGER "reject_hq_product_set_code_insert"
            BEFORE INSERT ON "DIC_一品多码表"
            WHEN NEW."H多码商品编号" = 'MC-REJECT'
            BEGIN
                SELECT RAISE(ABORT, 'reject MC-REJECT');
            END;
            """
        );

        var response = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-1",
            new UpdateHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-hq-multicode-empty-fallback-key" },
                TargetStoreCodes = new List<string> { "S01" },
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdatePurchasePrice = true,
                },
            },
            "tester"
        );

        var failedResult = Assert.IsType<UpdateHqProductsResult>(response.Details);
        Assert.False(response.Success);
        Assert.Equal("HQ_UPDATE_PARTIAL_FAILED", response.ErrorCode);
        Assert.Equal(1, failedResult.HqProductSetCodesCreated);
        Assert.Equal(1, failedResult.Failed);

        var productSetCodes = await _hqDb.Queryable<DIC_一品多码表>()
            .OrderBy(row => row.ID)
            .ToListAsync();
        Assert.Equal(2, productSetCodes.Count);
        Assert.Equal("MC-EXISTING", productSetCodes[0].H多码商品编号);
        Assert.Equal(1m, productSetCodes[0].H进货价);
        Assert.Contains(productSetCodes, row => row.H多码商品编号 == "MC-NEW");
        Assert.DoesNotContain(productSetCodes, row => row.H多码商品编号 == "MC-REJECT");
    }

    [Fact]
    public async Task UpdateHqProductsAsync_多码更新时记录被并发删除_降级重建并按创建计数()
    {
        await SeedStoreAsync("S01", true);
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");
        await SeedExistingProductAsync("P-001", "SUP01");
        await SeedHqProductAsync("P-001", 5m, 10m);
        await SeedHqPriceAsync("S01", "P-001", 5m, 10m);
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-hq-multicode-concurrent-delete",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-001",
            ItemNumber = "ITEM-MULTI-CONCURRENT-DELETE",
            Barcode = "930000030100",
            PurchasePrice = 8m,
            RetailPrice = 18m,
            IsDeleted = false,
        });
        await _localDb.Insertable(new ProductSetCode
        {
            SetCodeId = "set-code-concurrent-delete",
            ProductCode = "P-001",
            SetProductCode = "MC-CONCURRENT-DELETE",
            SetBarcode = "930000030101",
            SetPurchasePrice = 8m,
            SetRetailPrice = 18m,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await SeedStoreMultiCodeProjectionsForAllActiveRelationsAsync("P-001");
        await _hqDb.Insertable(new DIC_一品多码表
        {
            ID = 1,
            HGUID = "hq-set-code-concurrent-delete",
            H商品编码 = "P-001",
            H多码商品编号 = "MC-CONCURRENT-DELETE",
            H供应商编码 = "SUP01",
            H主条形码 = "930000030100",
            H多条形码 = "930000030101",
            H进货价 = 5m,
            H一品多码零售价 = 10m,
            H使用状态 = true,
            FGC_Creator = "seed",
            FGC_CreateDate = DateTime.UtcNow,
            FGC_LastModifier = "seed",
            FGC_LastModifyDate = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        await _hqDb.Ado.ExecuteCommandAsync(
            """
            CREATE TRIGGER "delete_hq_product_set_code_before_update"
            BEFORE UPDATE ON "DIC_一品多码表"
            WHEN NEW."H多码商品编号" = 'MC-CONCURRENT-DELETE'
            BEGIN
                DELETE FROM "DIC_一品多码表" WHERE "ID" = OLD."ID";
                SELECT RAISE(IGNORE);
            END;
            """
        );

        var response = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-1",
            new UpdateHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-hq-multicode-concurrent-delete" },
                TargetStoreCodes = new List<string> { "S01" },
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdatePurchasePrice = true,
                },
            },
            "tester"
        );

        Assert.True(response.Success, BuildFailureMessage(response));
        Assert.Equal(1, response.Data!.HqProductSetCodesCreated);
        Assert.Equal(0, response.Data.HqProductSetCodesUpdated);
        var rebuilt = await _hqDb.Queryable<DIC_一品多码表>().SingleAsync();
        Assert.Equal("MC-CONCURRENT-DELETE", rebuilt.H多码商品编号);
        Assert.Equal(5m, rebuilt.H进货价);
    }

    [Fact]
    public async Task UpdateHqProductsAsync_商品多码编号与条码命中不同HQ记录_返回冲突且不覆盖()
    {
        await SeedStoreAsync("S01", true);
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");
        await SeedExistingProductAsync("P-001", "SUP01");
        await SeedHqProductAsync("P-001", 5m, 10m);
        await SeedHqPriceAsync("S01", "P-001", 5m, 10m);
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-hq-multicode-key-conflict",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-001",
            ItemNumber = "ITEM-MULTI-KEY-CONFLICT",
            Barcode = "930000030200",
            PurchasePrice = 8m,
            RetailPrice = 18m,
            IsDeleted = false,
        });
        await _localDb.Insertable(new ProductSetCode
        {
            SetCodeId = "set-code-key-conflict",
            ProductCode = "P-001",
            SetProductCode = "MC-BY-CODE",
            SetBarcode = "930000030202",
            SetPurchasePrice = 8m,
            SetRetailPrice = 18m,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await SeedStoreMultiCodeProjectionsForAllActiveRelationsAsync("P-001");
        await _hqDb.Insertable(new[]
        {
            new DIC_一品多码表
            {
                ID = 1,
                HGUID = "hq-set-code-match-by-code",
                H商品编码 = "P-001",
                H多码商品编号 = "MC-BY-CODE",
                H多条形码 = "930000030201",
                H进货价 = 5m,
                H一品多码零售价 = 10m,
            },
            new DIC_一品多码表
            {
                ID = 2,
                HGUID = "hq-set-code-match-by-barcode",
                H商品编码 = "P-001",
                H多码商品编号 = "MC-BY-BARCODE",
                H多条形码 = "930000030202",
                H进货价 = 6m,
                H一品多码零售价 = 12m,
            },
        }).ExecuteCommandAsync();

        var response = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-1",
            new UpdateHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-hq-multicode-key-conflict" },
                TargetStoreCodes = new List<string> { "S01" },
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdatePurchasePrice = true,
                },
            },
            "tester"
        );

        var failedResult = Assert.IsType<UpdateHqProductsResult>(response.Details);
        Assert.False(response.Success);
        Assert.Equal("HQ_UPDATE_PARTIAL_FAILED", response.ErrorCode);
        Assert.Equal(1, failedResult.Failed);
        Assert.Contains(
            failedResult.Errors,
            error => error.Message.Contains("HQ商品多码业务键冲突")
        );

        var productSetCodes = await _hqDb.Queryable<DIC_一品多码表>()
            .OrderBy(row => row.ID)
            .ToListAsync();
        Assert.Collection(
            productSetCodes,
            row => Assert.Equal(("MC-BY-CODE", "930000030201", 5m), (row.H多码商品编号, row.H多条形码, row.H进货价)),
            row => Assert.Equal(("MC-BY-BARCODE", "930000030202", 6m), (row.H多码商品编号, row.H多条形码, row.H进货价))
        );
        Assert.Equal(0, await _hqDb.Queryable<DIC_分店一品多码表>().CountAsync());
    }

    [Fact]
    public async Task UpdateHqProductsAsync_商品多码两个业务键均为空_本地校正失败且不推送HQ()
    {
        await SeedStoreAsync("S01", true);
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");
        await SeedExistingProductAsync("P-001", "SUP01");
        await SeedHqProductAsync("P-001", 5m, 10m);
        await SeedHqPriceAsync("S01", "P-001", 5m, 10m);
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-hq-multicode-empty-keys",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-001",
            ItemNumber = "ITEM-MULTI-EMPTY-KEYS",
            Barcode = "930000030300",
            PurchasePrice = 8m,
            RetailPrice = 18m,
            IsDeleted = false,
        });
        await _localDb.Insertable(new ProductSetCode
        {
            SetCodeId = "set-code-empty-keys",
            ProductCode = "P-001",
            SetProductCode = string.Empty,
            SetBarcode = string.Empty,
            SetPurchasePrice = 8m,
            SetRetailPrice = 18m,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();

        var response = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-1",
            new UpdateHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-hq-multicode-empty-keys" },
                TargetStoreCodes = new List<string> { "S01" },
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdatePurchasePrice = true,
                },
            },
            "tester"
        );

        Assert.False(response.Success);
        Assert.Equal("HQ_UPDATE_ERROR", response.ErrorCode);
        Assert.Contains("子项业务键为空", response.Message);
        Assert.Equal(0, await _hqDb.Queryable<DIC_一品多码表>().CountAsync());
        Assert.Equal(0, await _hqDb.Queryable<DIC_分店一品多码表>().CountAsync());
    }

    [Fact]
    public async Task UpdateHqProductsAsync_更新HQ价格但不回写本单明细上次进货价()
    {
        await SeedStoreAsync("S01", true);
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");
        await SeedExistingProductAsync("P-001", "SUP01");
        await SeedHqProductAsync("P-001", 5m, 10m);
        await SeedHqPriceAsync("S01", "P-001", 5m, 10m);
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-no-writeback",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-001",
            PurchasePrice = 8m,
            RetailPrice = 18m,
            LastPurchasePrice = 3.21m,
            IsDeleted = false,
        });

        var result = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-1",
            new UpdateHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-no-writeback" },
                TargetStoreCodes = new List<string> { "S01" },
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdatePurchasePrice = true,
                },
            },
            "tester"
        );

        Assert.True(result.Success, BuildFailureMessage(result));
        Assert.Equal(1, result.Data!.HqPurchasePricesUpdated);

        var hqPrice = await _hqDb.Queryable<DIC_商品零售价表>()
            .FirstAsync(x => x.H分店代码 == "S01" && x.H商品编码 == "P-001");
        Assert.Equal(8m, hqPrice.H进货价);

        var detail = await _localDb.Queryable<StoreLocalSupplierInvoiceDetails>()
            .FirstAsync(x => x.DetailGUID == "detail-no-writeback");
        Assert.Equal(3.21m, detail.LastPurchasePrice);
    }

    [Fact]
    public async Task UpdateHqProductsAsync_目标分店HQ价格不存在_插入时使用更新字段完整建价()
    {
        await SeedStoreAsync("S01", true);
        await SeedStoreAsync("S02", true);
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");
        await SeedExistingProductAsync("P-001", "SUP01");
        await SeedHqProductAsync("P-001", 5m, 10m);
        await SeedHqPriceAsync("S01", "P-001", 5m, 10m);
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-missing-price",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-001",
            PurchasePrice = 8m,
            RetailPrice = 18m,
            AutoPricing = true,
            IsSpecialProduct = false,
            DiscountRate = 0.10m,
            IsDeleted = false,
        });

        var result = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-1",
            new UpdateHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-missing-price" },
                TargetStoreCodes = new List<string> { "S01", "S02" },
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdatePurchasePrice = true,
                    UpdateRetailPrice = true,
                    UpdateIsAutoPricing = true,
                    UpdateIsSpecialProduct = true,
                    UpdateDiscountRate = true,
                    PurchasePrice = 9m,
                    RetailPrice = 19m,
                    IsAutoPricing = false,
                    IsSpecialProduct = true,
                    DiscountRate = 0.30m,
                },
            },
            "tester"
        );

        Assert.True(result.Success, BuildFailureMessage(result));
        Assert.Equal(2, result.Data!.Updated);

        var existingPrice = await _hqDb.Queryable<DIC_商品零售价表>()
            .FirstAsync(x => x.H分店代码 == "S01" && x.H商品编码 == "P-001");
        Assert.Equal(9m, existingPrice.H进货价);
        Assert.Equal(19m, existingPrice.H分店零售价);
        Assert.False(existingPrice.H是否自动定价);
        Assert.True(existingPrice.H是否特殊商品);
        Assert.Equal(0.30m, existingPrice.H折扣率);

        var insertedPrice = await _hqDb.Queryable<DIC_商品零售价表>()
            .FirstAsync(x => x.H分店代码 == "S02" && x.H商品编码 == "P-001");
        Assert.Equal(9m, insertedPrice.H进货价);
        Assert.Equal(19m, insertedPrice.H分店零售价);
        Assert.False(insertedPrice.H是否自动定价);
        Assert.True(insertedPrice.H是否特殊商品);
        Assert.Equal(0.30m, insertedPrice.H折扣率);
    }

    [Fact]
    public async Task UpdateHqProductsAsync_数值为0时跳过且布尔False仍然写入()
    {
        await SeedStoreAsync("S01", true);
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");
        await SeedExistingProductAsync("P-001", "SUP01");
        await SeedHqProductAsync("P-001", 5m, 10m);
        await SeedHqPriceAsync("S01", "P-001", 5m, 10m);
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-zero-skip",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-001",
            PurchasePrice = 0m,
            RetailPrice = 0m,
            AutoPricing = false,
            IsSpecialProduct = false,
            DiscountRate = 0m,
            IsDeleted = false,
        });

        var result = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-1",
            new UpdateHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-zero-skip" },
                TargetStoreCodes = new List<string> { "S01" },
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdatePurchasePrice = true,
                    UpdateRetailPrice = true,
                    UpdateIsAutoPricing = true,
                    UpdateIsSpecialProduct = true,
                    UpdateDiscountRate = true,
                },
            },
            "tester"
        );

        var hqPrice = await _hqDb.Queryable<DIC_商品零售价表>()
            .FirstAsync(x => x.H分店代码 == "S01" && x.H商品编码 == "P-001");

        Assert.True(result.Success, BuildFailureMessage(result));
        Assert.Equal(1, result.Data!.Updated);
        Assert.Equal(0, result.Data.HqPurchasePricesUpdated);
        Assert.Equal(0, result.Data.HqRetailPricesUpdated);
        Assert.Equal(1, result.Data.HqAutoPricingUpdated);
        Assert.Equal(1, result.Data.HqSpecialProductsUpdated);
        Assert.Equal(0, result.Data.HqDiscountRatesUpdated);
        Assert.Equal(5m, hqPrice.H进货价);
        Assert.Equal(10m, hqPrice.H分店零售价);
        Assert.False(hqPrice.H是否自动定价);
        Assert.False(hqPrice.H是否特殊商品);
        Assert.Equal(0m, hqPrice.H折扣率);
    }

    [Fact]
    public async Task UpdateHqProductsAsync_自动定价为空时按否写入HQ价格()
    {
        await SeedStoreAsync("S01", true);
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");
        await SeedExistingProductAsync("P-001", "SUP01");
        await SeedHqProductAsync("P-001", 5m, 10m);
        await SeedHqPriceAsync("S01", "P-001", 5m, 10m);
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-null-auto-pricing",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-001",
            PurchasePrice = 8m,
            RetailPrice = 18m,
            AutoPricing = null,
            IsDeleted = false,
        });

        await _hqDb.Updateable<DIC_商品零售价表>()
            .SetColumns(x => x.H是否自动定价 == true)
            .Where(x => x.H分店代码 == "S01" && x.H商品编码 == "P-001")
            .ExecuteCommandAsync();

        var result = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-1",
            new UpdateHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-null-auto-pricing" },
                TargetStoreCodes = new List<string> { "S01" },
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdateIsAutoPricing = true,
                },
            },
            "tester"
        );

        var hqPrice = await _hqDb.Queryable<DIC_商品零售价表>()
            .FirstAsync(x => x.H分店代码 == "S01" && x.H商品编码 == "P-001");

        Assert.True(result.Success, BuildFailureMessage(result));
        Assert.Equal(1, result.Data!.Updated);
        Assert.Equal(0, result.Data.Skipped);
        Assert.Equal(1, result.Data.HqAutoPricingUpdated);
        Assert.False(hqPrice.H是否自动定价);
        Assert.DoesNotContain(result.Data.Errors, error => error.Message.Contains("自动定价为空"));
    }

    [Fact]
    public async Task UpdateHqProductsAsync_未选择字段_拒绝写入()
    {
        await SeedStoreAsync("S01", true);

        var result = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-1",
            new UpdateHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-1" },
                TargetStoreCodes = new List<string> { "S01" },
                UpdateFields = new UpdateToStorePricesFields(),
            },
            "tester"
        );

        Assert.False(result.Success);
        Assert.Equal("VALIDATION_ERROR", result.ErrorCode);
        Assert.Equal(0, result.Data?.Updated ?? 0);
    }

    [Fact]
    public async Task UpdateHqProductsAsync_货号仅大小写不同且已存在时_绑定已有商品不重复创建()
    {
        await SeedStoreAsync("S01", true);
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");
        await SeedExistingProductAsync("P-001", "SUP01");
        await SeedLocalPriceAsync("S01", "P-001", 5m, 10m);
        await SeedHqProductAsync("P-001", 5m, 10m);
        await SeedHqPriceAsync("S01", "P-001", 5m, 10m);
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-case",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ItemNumber = "item-old",
            Barcode = "930000001111",
            ProductName = "大小写绑定测试",
            PurchasePrice = 7m,
            RetailPrice = 17m,
            IsDeleted = false,
        });

        var result = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-1",
            new UpdateHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-case" },
                TargetStoreCodes = new List<string> { "S01" },
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdatePurchasePrice = true,
                },
            },
            "tester"
        );

        Assert.True(result.Success, BuildFailureMessage(result));
        Assert.Equal(0, result.Data!.HbwebCreated);
        Assert.Equal(1, await _localDb.Queryable<Product>().CountAsync());

        var detail = await _localDb.Queryable<StoreLocalSupplierInvoiceDetails>()
            .FirstAsync(x => x.DetailGUID == "detail-case");
        Assert.True(string.IsNullOrWhiteSpace(detail.ProductCode));

        var hqPrice = await _hqDb.Queryable<DIC_商品零售价表>()
            .FirstAsync(x => x.H分店代码 == "S01" && x.H商品编码 == "P-001");
        Assert.Equal(7m, hqPrice.H进货价);
    }

    [Fact]
    public async Task UpdateHqProductsAsync_明细商品编码失效但货号已存在_绑定已有本地商品不重复创建()
    {
        await SeedStoreAsync("S01", true);
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");
        await SeedExistingProductAsync("P-001", "SUP01");
        await SeedLocalPriceAsync("S01", "P-001", 5m, 10m);
        await SeedHqProductAsync("P-001", 5m, 10m);
        await SeedHqPriceAsync("S01", "P-001", 5m, 10m);
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-stale-code",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "STALE-PRODUCT",
            ItemNumber = "item-old",
            Barcode = "930000001111",
            ProductName = "失效编码绑定测试",
            PurchasePrice = 7m,
            RetailPrice = 17m,
            IsDeleted = false,
        });

        var result = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-1",
            new UpdateHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-stale-code" },
                TargetStoreCodes = new List<string> { "S01" },
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdatePurchasePrice = true,
                },
            },
            "tester"
        );

        Assert.True(result.Success, BuildFailureMessage(result));
        Assert.Equal(0, result.Data!.HbwebCreated);
        Assert.Equal(1, await _localDb.Queryable<Product>().CountAsync());

        var detail = await _localDb.Queryable<StoreLocalSupplierInvoiceDetails>()
            .FirstAsync(x => x.DetailGUID == "detail-stale-code");
        Assert.Equal("STALE-PRODUCT", detail.ProductCode);
    }

    [Fact]
    public async Task UpdateHqProductsAsync_Hq已有大小写货号但编码不同_复用Hq商品不重复创建()
    {
        await SeedStoreAsync("S01", true);
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");
        await SeedExistingProductAsync("P-001", "SUP01");
        await SeedLocalPriceAsync("S01", "P-001", 5m, 10m);
        await SeedHqProductAsync("P-HQ-OLD", 5m, 10m, itemNumber: "ITEM-OLD", barcode: "930000000000");
        await SeedHqPriceAsync("S01", "P-HQ-OLD", 5m, 10m);
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-hq-case",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-001",
            ItemNumber = "item-old",
            Barcode = "930000001111",
            ProductName = "HQ大小写绑定测试",
            PurchasePrice = 7m,
            RetailPrice = 17m,
            IsDeleted = false,
        });

        var result = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-1",
            new UpdateHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-hq-case" },
                TargetStoreCodes = new List<string> { "S01" },
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdatePurchasePrice = true,
                },
            },
            "tester"
        );

        Assert.True(result.Success, BuildFailureMessage(result));
        Assert.Equal(0, result.Data!.HqCreated);
        Assert.Equal(1, result.Data.HqExisting);
        Assert.Equal(1, await _hqDb.Queryable<DIC_商品信息字典表>().CountAsync());

        var oldHqPrice = await _hqDb.Queryable<DIC_商品零售价表>()
            .FirstAsync(x => x.H分店代码 == "S01" && x.H商品编码 == "P-HQ-OLD");
        Assert.Equal(7m, oldHqPrice.H进货价);

        var newCodePriceCount = await _hqDb.Queryable<DIC_商品零售价表>()
            .CountAsync(x => x.H商品编码 == "P-001");
        Assert.Equal(0, newCodePriceCount);
    }

    [Fact]
    public async Task UpdateHqProductsAsync_HQ商品不存在_新建商品并为所有启用分店处理价格()
    {
        await SeedStoreAsync("S01", true);
        await SeedStoreAsync("S02", true);
        await SeedStoreAsync("S03", false);
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");
        await SeedExistingProductAsync("P-001", "SUP01");
        await SeedDomesticProductAsync("P-001", "CN-SUP01");
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-new-hq",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-001",
            ItemNumber = "ITEM-NEW-HQ",
            Barcode = "930000001234",
            ProductName = "HQ不存在测试",
            PurchasePrice = 7.50m,
            RetailPrice = 15.00m,
            AutoPricing = true,
            IsDeleted = false,
        });

        var result = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-1",
            new UpdateHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-new-hq" },
                TargetStoreCodes = new List<string> { "S01" },
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdatePurchasePrice = true,
                },
            },
            "tester"
        );

        Assert.True(result.Success, BuildFailureMessage(result));
        Assert.Equal(1, result.Data!.HqCreated);
        Assert.Equal(2, result.Data.Updated);

        var hqProduct = await _hqDb.Queryable<DIC_商品信息字典表>()
            .FirstAsync(x => x.H商品编码 == "P-001");
        Assert.Equal("P-001", hqProduct.H商品编码);
        Assert.Equal("CN-SUP01", hqProduct.CBP供应商编码);

        var hqPrices = await _hqDb.Queryable<DIC_商品零售价表>()
            .Where(x => x.H商品编码 == "P-001")
            .OrderBy(x => x.H分店代码)
            .ToListAsync();
        Assert.Equal(new[] { "S01", "S02" }, hqPrices.Select(x => x.H分店代码).ToArray());
        Assert.All(hqPrices, price => Assert.Equal(7.50m, price.H进货价));
        Assert.DoesNotContain(hqPrices, price => price.H分店代码 == "S03");
    }

    [Fact]
    public async Task UpdateHqProductsAsync_本地商品不存在_逐行报错跳过且不新建商品不写价格与历史()
    {
        await SeedStoreAsync("S01", true);
        await SeedStoreAsync("S02", true);
        await SeedStoreAsync("S03", false);
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-new-local-product",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ItemNumber = "ITEM-NEW-LOCAL",
            Barcode = "930000009999",
            ProductName = "本地不存在测试",
            PurchasePrice = 4.20m,
            RetailPrice = 9.90m,
            AutoPricing = false,
            IsSpecialProduct = true,
            DiscountRate = 0.15m,
            IsDeleted = false,
        });
        // 严格模拟且不配置任何调用：本流程一旦写变更历史，测试即失败。
        var historyService = new Mock<IWarehouseProductChangeHistoryService>(MockBehavior.Strict);

        var response = await CreateSyncService(historyService.Object).UpdateHqProductsAsync(
            "invoice-1",
            new UpdateHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-new-local-product" },
                TargetStoreCodes = new List<string> { "S01" },
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdatePurchasePrice = true,
                },
            },
            "tester"
        );

        // 「更新HQ商品」不再隐式新建本地商品：找不到主档的行报错跳过，提示先执行「新建商品」。
        Assert.False(response.Success);
        Assert.Equal("HQ_UPDATE_PARTIAL_FAILED", response.ErrorCode);
        var result = Assert.IsType<UpdateHqProductsResult>(response.Details);
        Assert.Equal(0, result.HbwebCreated);
        Assert.Equal(0, result.HqCreated);
        Assert.Equal(1, result.Failed);
        Assert.Contains(
            result.Errors,
            error => error.DetailGuid == "detail-new-local-product" && error.Message.Contains("请先执行「新建商品」")
        );

        Assert.Equal(0, await _localDb.Queryable<Product>().Where(x => x.ItemNumber == "ITEM-NEW-LOCAL").CountAsync());
        var detail = await _localDb.Queryable<StoreLocalSupplierInvoiceDetails>()
            .FirstAsync(x => x.DetailGUID == "detail-new-local-product");
        Assert.Null(detail.ProductCode);
        Assert.Null(detail.StoreProductCode);
        Assert.Equal(0, await _localDb.Queryable<StoreRetailPrice>().CountAsync());
        Assert.Equal(0, await _hqDb.Queryable<DIC_商品信息字典表>().CountAsync());
        Assert.Equal(0, await _hqDb.Queryable<DIC_商品零售价表>().CountAsync());
        historyService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateHqProductsAsync_本地商品已存在_不更新本地分店价格()
    {
        await SeedStoreAsync("S01", true);
        await SeedStoreAsync("S02", true);
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");
        await SeedExistingProductAsync("P-001", "SUP01");
        await SeedLocalPriceAsync("S01", "P-001", 5m, 10m);
        await SeedLocalPriceAsync("S02", "P-001", 6m, 12m);
        await SeedHqProductAsync("P-001", 5m, 10m);
        await SeedHqPriceAsync("S01", "P-001", 5m, 10m);
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-existing-local-product",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-001",
            ItemNumber = "ITEM-NEW",
            Barcode = "930000000002",
            ProductName = "已有本地商品",
            PurchasePrice = 8m,
            RetailPrice = 18m,
            AutoPricing = true,
            IsSpecialProduct = true,
            DiscountRate = 0.05m,
            IsDeleted = false,
        });

        var result = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-1",
            new UpdateHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-existing-local-product" },
                TargetStoreCodes = new List<string> { "S01" },
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdatePurchasePrice = true,
                    UpdateRetailPrice = true,
                    UpdateDiscountRate = true,
                    UpdateIsAutoPricing = true,
                    UpdateIsSpecialProduct = true,
                },
            },
            "tester"
        );

        Assert.True(result.Success, BuildFailureMessage(result));
        Assert.Equal(0, result.Data!.HbwebCreated);

        var localS01 = await _localDb.Queryable<StoreRetailPrice>()
            .FirstAsync(x => x.StoreCode == "S01" && x.ProductCode == "P-001");
        var localS02 = await _localDb.Queryable<StoreRetailPrice>()
            .FirstAsync(x => x.StoreCode == "S02" && x.ProductCode == "P-001");
        Assert.Equal(5m, localS01.PurchasePrice);
        Assert.Equal(10m, localS01.StoreRetailPriceValue);
        Assert.Equal(6m, localS02.PurchasePrice);
        Assert.Equal(12m, localS02.StoreRetailPriceValue);
    }

    [Theory]
    [InlineData(4, 3)]
    [InlineData(31, 20)]
    public async Task UpdateHqProductsAsync_本地已建多商品_HQ新建时为启用分店写价格_价格写入命令数应受批量上限约束(
        int productCount,
        int activeStoreCount
    )
    {
        for (var storeIndex = 1; storeIndex <= activeStoreCount; storeIndex++)
        {
            await SeedStoreAsync($"S{storeIndex:D2}", true);
        }
        await SeedStoreAsync("S99", false);
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");

        var detailGuids = new List<string>();
        for (var index = 1; index <= productCount; index++)
        {
            var detailGuid = $"detail-batch-{index}";
            detailGuids.Add(detailGuid);
            // 本地主档由「新建商品」先建好并回填到明细；「更新HQ商品」只负责补写 HQ。
            await SeedExistingProductAsync($"P-BATCH-{index}", "SUP01", $"ITEM-BATCH-{index}", $"93000001{index:D5}");
            await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
            {
                DetailGUID = detailGuid,
                InvoiceGUID = "invoice-1",
                StoreCode = "S01",
                SupplierCode = "SUP01",
                ProductCode = $"P-BATCH-{index}",
                ItemNumber = $"ITEM-BATCH-{index}",
                Barcode = $"93000001{index:D5}",
                ProductName = $"批量新商品{index}",
                PurchasePrice = 10m + index,
                RetailPrice = 20m + index,
                AutoPricing = true,
                IsDeleted = false,
            });
        }

        // 种子数据全部完成后才挂载 AOP，仅统计本次同步产生的目标表 INSERT/UPDATE 命令。
        var localPriceWriteCommands = 0;
        var hqPriceWriteCommands = 0;
        _localDb.Aop.OnLogExecuting = (sql, _) =>
        {
            if (IsTableWriteCommand(sql, "StoreRetailPrice"))
                localPriceWriteCommands++;
        };
        _hqDb.Aop.OnLogExecuting = (sql, _) =>
        {
            if (IsTableWriteCommand(sql, "DIC_商品零售价表"))
                hqPriceWriteCommands++;
        };

        UpdateHqProductsResult? result;
        try
        {
            var response = await CreateSyncService().UpdateHqProductsAsync(
                "invoice-1",
                new UpdateHqProductsRequest
                {
                    DetailGuids = detailGuids,
                    TargetStoreCodes = new List<string> { "S01" },
                    UpdateFields = new UpdateToStorePricesFields
                    {
                        UpdatePurchasePrice = true,
                        UpdateRetailPrice = true,
                    },
                },
                "tester"
            );
            Assert.True(response.Success, BuildFailureMessage(response));
            result = response.Data;
        }
        finally
        {
            _localDb.Aop.OnLogExecuting = null;
            _hqDb.Aop.OnLogExecuting = null;
        }

        Assert.NotNull(result);
        Assert.Equal(0, result!.HbwebCreated);
        Assert.Equal(productCount, result.HqCreated);

        // 本地分店价不归「更新HQ商品」写；HQ 侧商品是新建的，所以为全部启用分店写价格，停用分店不写。
        var expectedPriceCount = productCount * activeStoreCount;
        Assert.Equal(0, await _localDb.Queryable<StoreRetailPrice>().CountAsync());
        Assert.Equal(0, localPriceWriteCommands);
        Assert.Equal(expectedPriceCount, await _hqDb.Queryable<DIC_商品零售价表>().CountAsync());
        Assert.Equal(0, await _hqDb.Queryable<DIC_商品零售价表>()
            .Where(x => x.H分店代码 == "S99")
            .CountAsync());

        var hqCommandLimit = productCount * (int)Math.Ceiling(activeStoreCount / 40m);
        Assert.True(
            hqPriceWriteCommands <= hqCommandLimit,
            $"HQ 价格写入命令数超标：{hqPriceWriteCommands} 条(上限{hqCommandLimit})"
        );
    }

    [Fact]
    public async Task UpdateHqProductsAsync_批量新增HQ价格单店失败_降级后保留其他分店并准确计数()
    {
        await SeedStoreAsync("S01", true);
        await SeedStoreAsync("S02", true);
        await SeedStoreAsync("S03", true);
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-batch-fallback",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-BATCH-FALLBACK",
            ItemNumber = "ITEM-BATCH-FALLBACK",
            Barcode = "930000010099",
            ProductName = "批量失败降级测试",
            PurchasePrice = 8.80m,
            RetailPrice = 18.80m,
            AutoPricing = true,
            IsDeleted = false,
        });
        await _hqDb.Ado.ExecuteCommandAsync(
            """
            CREATE TRIGGER "reject_hq_price_s02"
            BEFORE INSERT ON "DIC_商品零售价表"
            WHEN NEW."H分店代码" = 'S02'
            BEGIN
                SELECT RAISE(ABORT, 'reject S02');
            END;
            """
        );

        await SeedExistingProductAsync("P-BATCH-FALLBACK", "SUP01", "ITEM-BATCH-FALLBACK", "930000010099");

        var response = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-1",
            new UpdateHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-batch-fallback" },
                TargetStoreCodes = new List<string> { "S01" },
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdatePurchasePrice = true,
                },
            },
            "tester"
        );

        var failedResult = Assert.IsType<UpdateHqProductsResult>(response.Details);
        Assert.False(response.Success);
        Assert.Equal("HQ_UPDATE_PARTIAL_FAILED", response.ErrorCode);
        Assert.Equal(1, failedResult.HqCreated);
        Assert.Equal(2, failedResult.Updated);
        Assert.Equal(2, failedResult.HqPurchasePricesUpdated);
        Assert.Equal(1, failedResult.Failed);
        Assert.Contains(
            failedResult.Errors,
            error => error.StoreCode == "S02" && error.Message.Contains("更新HQ分店价格失败")
        );

        var hqPrices = await _hqDb.Queryable<DIC_商品零售价表>()
            .OrderBy(price => price.H分店代码)
            .ToListAsync();
        Assert.Equal(new[] { "S01", "S03" }, hqPrices.Select(price => price.H分店代码).ToArray());
        // 本地分店价不归「更新HQ商品」写（新建商品时已由「新建商品」为全部启用分店建好）。
        Assert.Equal(0, await _localDb.Queryable<StoreRetailPrice>().CountAsync());
    }

    [Fact]
    public async Task UpdateHqProductsAsync_批量更新HQ价格单店失败_降级后只统计实际成功分店()
    {
        foreach (var storeCode in new[] { "S01", "S02", "S03" })
        {
            await SeedStoreAsync(storeCode, true);
        }
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");
        await SeedExistingProductAsync("P-001", "SUP01");
        await SeedHqProductAsync("P-001", 5m, 10m);
        await SeedHqPriceAsync("S01", "P-001", 5m, 10m);
        await SeedHqPriceAsync("S02", "P-001", 6m, 12m);
        await _hqDb.Insertable(new DIC_商品零售价表
        {
            ID = 3,
            HGUID = "hq-price-S03-P-001",
            H分店代码 = "S03",
            H商品编码 = "P-001",
            H分店商品编码 = "S03P-001",
            H供应商编码 = "SUP01",
            H分店供应商编码 = "S03SUP01",
            H进货价 = 7m,
            H分店零售价 = 14m,
            H使用状态 = true,
            FGC_Creator = "seed",
            FGC_CreateDate = DateTime.UtcNow,
            FGC_LastModifier = "seed",
            FGC_LastModifyDate = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-update-batch-fallback",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-001",
            ItemNumber = "ITEM-UPDATE-BATCH-FALLBACK",
            Barcode = "930000010299",
            ProductName = "批量更新失败降级测试",
            PurchasePrice = 8.80m,
            RetailPrice = 18.80m,
            IsDeleted = false,
        });
        await _hqDb.Ado.ExecuteCommandAsync(
            """
            CREATE TRIGGER "reject_hq_price_update_s02"
            BEFORE UPDATE ON "DIC_商品零售价表"
            WHEN NEW."H分店代码" = 'S02'
            BEGIN
                SELECT RAISE(ABORT, 'reject S02');
            END;
            """
        );

        var response = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-1",
            new UpdateHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-update-batch-fallback" },
                TargetStoreCodes = new List<string> { "S01", "S02", "S03" },
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdatePurchasePrice = true,
                },
            },
            "tester"
        );

        var failedResult = Assert.IsType<UpdateHqProductsResult>(response.Details);
        Assert.False(response.Success);
        Assert.Equal("HQ_UPDATE_PARTIAL_FAILED", response.ErrorCode);
        Assert.Equal(2, failedResult.Updated);
        Assert.Equal(2, failedResult.HqPurchasePricesUpdated);
        Assert.Equal(1, failedResult.Failed);
        Assert.Contains(
            failedResult.Errors,
            error => error.StoreCode == "S02" && error.Message.Contains("更新HQ分店价格失败")
        );

        var prices = await _hqDb.Queryable<DIC_商品零售价表>()
            .OrderBy(price => price.H分店代码)
            .ToListAsync();
        Assert.Collection(
            prices,
            price => Assert.Equal(("S01", 8.80m), (price.H分店代码, price.H进货价)),
            price => Assert.Equal(("S02", 6m), (price.H分店代码, price.H进货价)),
            price => Assert.Equal(("S03", 8.80m), (price.H分店代码, price.H进货价))
        );
    }

    [Fact]
    public async Task UpdateHqProductsAsync_批量更新仅影响部分行_未写入分店不得计为成功()
    {
        await SeedStoreAsync("S01", true);
        await SeedStoreAsync("S02", true);
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");
        await SeedExistingProductAsync("P-001", "SUP01");
        await SeedHqProductAsync("P-001", 5m, 10m);
        await SeedHqPriceAsync("S01", "P-001", 5m, 10m);
        await SeedHqPriceAsync("S02", "P-001", 6m, 12m);
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-update-affected-row-mismatch",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-001",
            ItemNumber = "ITEM-AFFECTED-ROW-MISMATCH",
            Barcode = "930000010399",
            ProductName = "更新影响行数核验",
            PurchasePrice = 8.90m,
            RetailPrice = 18.90m,
            IsDeleted = false,
        });
        await _hqDb.Ado.ExecuteCommandAsync(
            """
            CREATE TRIGGER "ignore_hq_price_update_s02"
            BEFORE UPDATE ON "DIC_商品零售价表"
            WHEN NEW."H分店代码" = 'S02'
            BEGIN
                SELECT RAISE(IGNORE);
            END;
            """
        );

        var response = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-1",
            new UpdateHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-update-affected-row-mismatch" },
                TargetStoreCodes = new List<string> { "S01", "S02" },
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdatePurchasePrice = true,
                },
            },
            "tester"
        );

        var failedResult = Assert.IsType<UpdateHqProductsResult>(response.Details);
        Assert.False(response.Success);
        Assert.Equal("HQ_UPDATE_PARTIAL_FAILED", response.ErrorCode);
        Assert.Equal(1, failedResult.Updated);
        Assert.Equal(1, failedResult.HqPurchasePricesUpdated);
        Assert.Equal(1, failedResult.Failed);
        Assert.Contains(
            failedResult.Errors,
            error => error.StoreCode == "S02" && error.Message.Contains("实际影响0行")
        );

        var prices = await _hqDb.Queryable<DIC_商品零售价表>()
            .OrderBy(price => price.H分店代码)
            .ToListAsync();
        Assert.Collection(
            prices,
            price => Assert.Equal(("S01", 8.90m), (price.H分店代码, price.H进货价)),
            price => Assert.Equal(("S02", 6m), (price.H分店代码, price.H进货价))
        );
    }

    [Fact]
    public async Task EnsureHqProductsAsync_缺本地和HQ商品_新建商品并为所有启用分店创建价格()
    {
        await SeedStoreAsync("S01", true);
        await SeedStoreAsync("S02", true);
        await SeedStoreAsync("S03", false);
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-1",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ItemNumber = "ITEM-1",
            Barcode = "930000000001",
            ProductName = "测试商品",
            PurchasePrice = 4.20m,
            RetailPrice = 9.90m,
            AutoPricing = false,
            IsSpecialProduct = true,
            DiscountRate = 0.15m,
            IsDeleted = false,
        });

        IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>? capturedBeforeSnapshots = null;
        IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>? capturedAfterSnapshots = null;
        WarehouseProductChangeHistoryContextDto? capturedContext = null;
        var historyService = new Mock<IWarehouseProductChangeHistoryService>(MockBehavior.Strict);
        historyService
            .Setup(service => service.RecordChangesAsync(
                It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                It.Is<WarehouseProductChangeHistoryContextDto>(context =>
                    context.Action == "Create"
                    && context.Source == "LocalSupplierInvoiceHqProductSync"
                    && context.SourceReference == "invoice-1"
                    && context.BatchGuid.HasValue
                    && context.ActorUserGuid == null
                    && context.ActorName == "tester"
                ),
                It.IsAny<CancellationToken>()
            ))
            .Callback<
                IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>,
                IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>,
                WarehouseProductChangeHistoryContextDto,
                CancellationToken
            >((before, after, context, _) =>
            {
                capturedBeforeSnapshots = before;
                capturedAfterSnapshots = after;
                capturedContext = context;
            })
            .ReturnsAsync(1);

        var result = await CreateSyncService(historyService.Object).EnsureHqProductsAsync(
            "invoice-1",
            new EnsureHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-1" },
                TargetStoreCodes = new List<string> { "S01" },
            },
            "tester"
        );

        Assert.True(result.Success, BuildFailureMessage(result));
        Assert.Equal(1, result.Data!.HbwebCreated);
        Assert.Equal(1, result.Data.HqCreated);
        Assert.Equal(0, result.Data.Failed);

        var detail = await _localDb.Queryable<StoreLocalSupplierInvoiceDetails>()
            .FirstAsync(x => x.DetailGUID == "detail-1");
        Assert.False(string.IsNullOrWhiteSpace(detail.ProductCode));
        var product = await _localDb.Queryable<Product>()
            .SingleAsync(x => x.ProductCode == detail.ProductCode);

        Assert.NotNull(capturedBeforeSnapshots);
        Assert.Empty(capturedBeforeSnapshots);
        Assert.NotNull(capturedAfterSnapshots);
        var createdSnapshot = Assert.Single(capturedAfterSnapshots);
        Assert.Equal(product.ProductCode, createdSnapshot.Key);
        AssertCreatedProductSnapshot(product, createdSnapshot.Value);
        Assert.NotNull(capturedContext);
        historyService.Verify(service => service.CaptureSnapshotsAsync(
            It.IsAny<IEnumerable<string>>(),
            It.IsAny<CancellationToken>()
        ), Times.Never);
        historyService.VerifyAll();

        var localPrices = await _localDb.Queryable<StoreRetailPrice>()
            .Where(x => x.ProductCode == detail.ProductCode)
            .OrderBy(x => x.StoreCode)
            .ToListAsync();
        Assert.Equal(new[] { "S01", "S02" }, localPrices.Select(x => x.StoreCode).ToArray());
        Assert.All(localPrices, price => Assert.Equal(4.20m, price.PurchasePrice));
        Assert.DoesNotContain(localPrices, price => price.StoreCode == "S03");

        var hqProduct = await _hqDb.Queryable<DIC_商品信息字典表>()
            .FirstAsync(x => x.H商品编码 == detail.ProductCode);
        Assert.Equal("ITEM-1", hqProduct.H货号);
        Assert.Equal("930000000001", hqProduct.H主条形码);
        Assert.Equal("detail-1", hqProduct.H进货单详情GUID);
        Assert.Equal(string.Empty, hqProduct.CBP供应商编码);

        var hqPrices = await _hqDb.Queryable<DIC_商品零售价表>()
            .Where(x => x.H商品编码 == detail.ProductCode)
            .OrderBy(x => x.H分店代码)
            .ToListAsync();
        Assert.Equal(new[] { "S01", "S02" }, hqPrices.Select(x => x.H分店代码).ToArray());
        Assert.All(hqPrices, price => Assert.Equal(9.90m, price.H分店零售价));
    }

    [Fact]
    public async Task EnsureHqProductsAsync_已有本地主档_不写统一历史事件()
    {
        await SeedStoreAsync("S01", true);
        await SeedInvoiceAsync("invoice-existing-history", "S01", "SUP01");
        await SeedExistingProductAsync("P-EXISTING-HISTORY", "SUP01", "ITEM-EXISTING-HISTORY");
        await SeedHqProductAsync("P-EXISTING-HISTORY", 5m, 10m, "ITEM-EXISTING-HISTORY");
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-existing-history",
            InvoiceGUID = "invoice-existing-history",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-EXISTING-HISTORY",
            ItemNumber = "ITEM-EXISTING-HISTORY",
            Barcode = "930000000000",
            PurchasePrice = 8m,
            RetailPrice = 18m,
            IsDeleted = false,
        });

        var historyService = new Mock<IWarehouseProductChangeHistoryService>(MockBehavior.Strict);

        var result = await CreateSyncService(historyService.Object).EnsureHqProductsAsync(
            "invoice-existing-history",
            new EnsureHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-existing-history" },
                TargetStoreCodes = new List<string> { "S01" },
            },
            "tester"
        );

        Assert.True(result.Success, BuildFailureMessage(result));
        historyService.Verify(service => service.CaptureSnapshotsAsync(
            It.IsAny<IEnumerable<string>>(),
            It.IsAny<CancellationToken>()
        ), Times.Never);
        historyService.Verify(service => service.RecordChangesAsync(
            It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
            It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
            It.IsAny<WarehouseProductChangeHistoryContextDto>(),
            It.IsAny<CancellationToken>()
        ), Times.Never);
    }

    [Fact]
    public async Task EnsureHqProductsAsync_历史写入失败_回滚本地主档创建()
    {
        await SeedStoreAsync("S01", true);
        await SeedInvoiceAsync("invoice-history-rollback", "S01", "SUP01");
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-history-rollback",
            InvoiceGUID = "invoice-history-rollback",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ItemNumber = "ITEM-HISTORY-ROLLBACK",
            Barcode = "930000005555",
            ProductName = "回滚历史商品",
            PurchasePrice = 4.20m,
            RetailPrice = 9.90m,
            IsDeleted = false,
        });

        var historyService = new Mock<IWarehouseProductChangeHistoryService>(MockBehavior.Strict);
        historyService
            .Setup(service => service.RecordChangesAsync(
                It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                It.IsAny<WarehouseProductChangeHistoryContextDto>(),
                It.IsAny<CancellationToken>()
            ))
            .ThrowsAsync(new InvalidOperationException("历史写入失败"));

        var result = await CreateSyncService(historyService.Object).EnsureHqProductsAsync(
            "invoice-history-rollback",
            new EnsureHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-history-rollback" },
                TargetStoreCodes = new List<string> { "S01" },
            },
            "tester"
        );

        Assert.False(result.Success);
        historyService.Verify(service => service.CaptureSnapshotsAsync(
            It.IsAny<IEnumerable<string>>(),
            It.IsAny<CancellationToken>()
        ), Times.Never);
        Assert.Equal(
            0,
            await _localDb.Queryable<Product>()
                .Where(item => item.ItemNumber == "ITEM-HISTORY-ROLLBACK")
                .CountAsync()
        );
        Assert.Equal(0, await _localDb.Queryable<StoreRetailPrice>().CountAsync());
        Assert.Equal(0, await _hqDb.Queryable<DIC_商品信息字典表>().CountAsync());
        historyService.VerifyAll();
    }

    [Fact]
    public async Task EnsureHqProductsAsync_已有商品_只更新请求目标分店()
    {
        await SeedStoreAsync("S01", true);
        await SeedStoreAsync("S02", true);
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");
        await SeedExistingProductAsync("P-001", "SUP01");
        await SeedDomesticProductAsync("P-001", "CN-SUP01");
        await SeedLocalPriceAsync("S01", "P-001", 5m, 10m);
        await SeedLocalPriceAsync("S02", "P-001", 6m, 12m);
        await SeedHqProductAsync("P-001", 5m, 10m);
        await _hqDb.Updateable<DIC_商品信息字典表>()
            .SetColumns(row => new DIC_商品信息字典表 { CBP供应商编码 = "200" })
            .Where(row => row.H商品编码 == "P-001")
            .ExecuteCommandAsync();
        await SeedHqPriceAsync("S01", "P-001", 5m, 10m);
        await SeedHqPriceAsync("S02", "P-001", 6m, 12m);
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-1",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-001",
            ItemNumber = "ITEM-NEW",
            Barcode = "930000000002",
            ProductName = "已存在商品",
            PurchasePrice = 8m,
            RetailPrice = 18m,
            AutoPricing = true,
            IsSpecialProduct = false,
            DiscountRate = 0.05m,
            IsDeleted = false,
        });

        var result = await CreateSyncService().EnsureHqProductsAsync(
            "invoice-1",
            new EnsureHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-1" },
                TargetStoreCodes = new List<string> { "S01" },
            },
            "tester"
        );

        Assert.True(result.Success, BuildFailureMessage(result));
        Assert.Equal(1, result.Data!.HqExisting);
        Assert.Equal(1, result.Data.HqPurchasePricesUpdated);

        var localS01 = await _localDb.Queryable<StoreRetailPrice>()
            .FirstAsync(x => x.StoreCode == "S01" && x.ProductCode == "P-001");
        var localS02 = await _localDb.Queryable<StoreRetailPrice>()
            .FirstAsync(x => x.StoreCode == "S02" && x.ProductCode == "P-001");
        Assert.Equal(8m, localS01.PurchasePrice);
        Assert.Equal(18m, localS01.StoreRetailPriceValue);
        Assert.Equal(6m, localS02.PurchasePrice);
        Assert.Equal(12m, localS02.StoreRetailPriceValue);

        var product = await _localDb.Queryable<Product>()
            .FirstAsync(x => x.ProductCode == "P-001");
        Assert.Equal("ITEM-OLD", product.ItemNumber);
        Assert.Equal("930000000000", product.Barcode);
        Assert.Equal(5m, product.PurchasePrice);
        Assert.Equal(10m, product.RetailPrice);

        var hqProduct = await _hqDb.Queryable<DIC_商品信息字典表>()
            .FirstAsync(x => x.H商品编码 == "P-001");
        Assert.Equal("ITEM-OLD", hqProduct.H货号);
        Assert.Equal("930000000000", hqProduct.H主条形码);
        Assert.Equal(5m, hqProduct.H进货价);
        Assert.Equal(10m, hqProduct.H零售价);

        var hqS01 = await _hqDb.Queryable<DIC_商品零售价表>()
            .FirstAsync(x => x.H分店代码 == "S01" && x.H商品编码 == "P-001");
        var hqS02 = await _hqDb.Queryable<DIC_商品零售价表>()
            .FirstAsync(x => x.H分店代码 == "S02" && x.H商品编码 == "P-001");
        Assert.Equal(8m, hqS01.H进货价);
        Assert.Equal(18m, hqS01.H分店零售价);
        Assert.Equal(6m, hqS02.H进货价);
        Assert.Equal(12m, hqS02.H分店零售价);
        Assert.Equal("CN-SUP01", hqProduct.CBP供应商编码);
    }

    [Fact]
    public async Task EnsureHqProductsAsync_ProductCode为空但货号已存在_绑定已有商品不重复创建()
    {
        await SeedStoreAsync("S01", true);
        await SeedStoreAsync("S02", true);
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");
        await SeedExistingProductAsync("P-001", "SUP01");
        await SeedLocalPriceAsync("S01", "P-001", 5m, 10m);
        await SeedLocalPriceAsync("S02", "P-001", 6m, 12m);
        await SeedHqProductAsync("P-001", 5m, 10m);
        await SeedHqPriceAsync("S01", "P-001", 5m, 10m);
        await SeedHqPriceAsync("S02", "P-001", 6m, 12m);
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-1",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ItemNumber = "ITEM-OLD",
            Barcode = "930000009999",
            ProductName = "重复货号明细",
            PurchasePrice = 7m,
            RetailPrice = 17m,
            IsDeleted = false,
        });

        var result = await CreateSyncService().EnsureHqProductsAsync(
            "invoice-1",
            new EnsureHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-1" },
                TargetStoreCodes = new List<string> { "S01" },
            },
            "tester"
        );

        Assert.True(result.Success, BuildFailureMessage(result));
        Assert.Equal(0, result.Data!.HbwebCreated);
        Assert.Equal(1, await _localDb.Queryable<Product>().CountAsync());

        var detail = await _localDb.Queryable<StoreLocalSupplierInvoiceDetails>()
            .FirstAsync(x => x.DetailGUID == "detail-1");
        Assert.Equal("P-001", detail.ProductCode);

        var localS01 = await _localDb.Queryable<StoreRetailPrice>()
            .FirstAsync(x => x.StoreCode == "S01" && x.ProductCode == "P-001");
        var localS02 = await _localDb.Queryable<StoreRetailPrice>()
            .FirstAsync(x => x.StoreCode == "S02" && x.ProductCode == "P-001");
        Assert.Equal(7m, localS01.PurchasePrice);
        Assert.Equal(17m, localS01.StoreRetailPriceValue);
        Assert.Equal(6m, localS02.PurchasePrice);
        Assert.Equal(12m, localS02.StoreRetailPriceValue);
    }

    [Fact]
    public async Task EnsureHqProductsAsync_目标分店不存在或停用_拒绝写入()
    {
        await SeedStoreAsync("S01", true);
        await SeedStoreAsync("S02", false);
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-1",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ItemNumber = "ITEM-1",
            Barcode = "930000000004",
            ProductName = "停用分店测试",
            PurchasePrice = 4m,
            RetailPrice = 8m,
            IsDeleted = false,
        });

        var result = await CreateSyncService().EnsureHqProductsAsync(
            "invoice-1",
            new EnsureHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-1" },
                TargetStoreCodes = new List<string> { "S02" },
            },
            "tester"
        );

        Assert.False(result.Success);
        Assert.Equal("INVALID_TARGET_STORE", result.ErrorCode);
        Assert.Equal(0, await _localDb.Queryable<Product>().CountAsync());
        Assert.Equal(0, await _localDb.Queryable<StoreRetailPrice>().CountAsync());
    }

    [Fact]
    public async Task EnsureHqProductsAsync_HQ失败时_本地写入保留并返回错误()
    {
        await SeedStoreAsync("S01", true);
        await SeedStoreAsync("S02", true);
        await SeedInvoiceAsync("invoice-1", "S01", "SUP01");
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-1",
            InvoiceGUID = "invoice-1",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ItemNumber = "ITEM-FAIL",
            Barcode = "930000000003",
            ProductName = "HQ失败本地保留",
            PurchasePrice = 3.30m,
            RetailPrice = 6.60m,
            IsDeleted = false,
        });
        await _hqDb.Ado.ExecuteCommandAsync("DROP TABLE \"DIC_商品信息字典表\"");

        var result = await CreateSyncService().EnsureHqProductsAsync(
            "invoice-1",
            new EnsureHqProductsRequest
            {
                DetailGuids = new List<string> { "detail-1" },
                TargetStoreCodes = new List<string> { "S01" },
            },
            "tester"
        );

        var failedResult = Assert.IsType<EnsureHqProductsResult>(result.Details);
        Assert.False(result.Success);
        Assert.Equal("HQ_SYNC_PARTIAL_FAILED", result.ErrorCode);
        Assert.Equal(1, failedResult.HbwebCreated);
        Assert.Equal(1, failedResult.Failed);
        Assert.NotEmpty(failedResult.Errors);

        var detail = await _localDb.Queryable<StoreLocalSupplierInvoiceDetails>()
            .FirstAsync(x => x.DetailGUID == "detail-1");
        var localPrices = await _localDb.Queryable<StoreRetailPrice>()
            .Where(x => x.ProductCode == detail.ProductCode)
            .OrderBy(x => x.StoreCode)
            .ToListAsync();
        Assert.Equal(new[] { "S01", "S02" }, localPrices.Select(x => x.StoreCode).ToArray());
    }

    [Fact]
    public async Task UpdateHqProductsAsync_已有商品且目标门店完全没有本地投影_自动补齐门店多码并成功()
    {
        await SeedStoreAsync("S01", true);
        await SeedInvoiceAsync("invoice-missing-projection", "S01", "SUP01");
        await SeedExistingProductAsync("P-MISSING-PROJECTION", "SUP01");
        await _localDb.Insertable(new ProductSetCode
        {
            SetCodeId = "set-missing-projection",
            ProductCode = "P-MISSING-PROJECTION",
            SetProductCode = "MC-MISSING-PROJECTION",
            SetItemNumber = "ITEM-MC-MISSING-PROJECTION",
            SetBarcode = "930000099901",
            SetPurchasePrice = 99m,
            SetRetailPrice = 10m,
            SetQuantity = 1,
            SetType = 2,
            IsActive = true,
            IsDeleted = false,
            CreatedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        await SeedHqProductAsync("P-MISSING-PROJECTION", 5m, 10m);
        await SeedHqPriceAsync("S01", "P-MISSING-PROJECTION", 5m, 10m);
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-missing-projection",
            InvoiceGUID = "invoice-missing-projection",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-MISSING-PROJECTION",
            ItemNumber = "ITEM-OLD",
            Barcode = "930000000000",
            PurchasePrice = 77m,
            RetailPrice = 10m,
            IsDeleted = false,
        });

        var response = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-missing-projection",
            new UpdateHqProductsRequest
            {
                DetailGuids = ["detail-missing-projection"],
                TargetStoreCodes = ["S01"],
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdatePurchasePrice = true,
                },
            },
            "tester"
        );

        Assert.True(response.Success, BuildFailureMessage(response));
        var localMulti = await _localDb.Queryable<StoreMultiCodeProduct>()
            .SingleAsync(x => x.StoreCode == "S01" && x.ProductCode == "P-MISSING-PROJECTION");
        Assert.Equal("MC-MISSING-PROJECTION", localMulti.MultiCodeProductCode);
        Assert.Equal(5m, localMulti.PurchasePrice);
        Assert.Equal(0, await _localDb.Queryable<StoreRetailPrice>()
            .Where(x => x.StoreCode == "S01" && x.ProductCode == "P-MISSING-PROJECTION")
            .CountAsync());
        Assert.Equal(5m, (await _localDb.Queryable<Product>()
            .SingleAsync(x => x.ProductCode == "P-MISSING-PROJECTION")).PurchasePrice);
        Assert.Equal(77m, (await _localDb.Queryable<StoreLocalSupplierInvoiceDetails>()
            .SingleAsync(x => x.DetailGUID == "detail-missing-projection")).PurchasePrice);
        Assert.Equal(1, await _hqDb.Queryable<DIC_一品多码表>()
            .Where(x => x.H商品编码 == "P-MISSING-PROJECTION")
            .CountAsync());
        Assert.Equal(1, await _hqDb.Queryable<DIC_分店一品多码表>()
            .Where(x => x.H商品编码 == "P-MISSING-PROJECTION" && x.H分店代码 == "S01")
            .CountAsync());
    }

    [Fact]
    public async Task UpdateHqProductsAsync_有效本地店与HQ独有店混选_HQ独有店数据保持不变()
    {
        await SeedStoreAsync("S01", true);
        await SeedInvoiceAsync("invoice-hq-only-mixed", "S01", "SUP01");
        await SeedExistingProductAsync("P-HQ-ONLY-MIXED", "SUP01");
        await _localDb.Insertable(new ProductSetCode
        {
            SetCodeId = "set-hq-only-mixed",
            ProductCode = "P-HQ-ONLY-MIXED",
            SetProductCode = "MC-HQ-ONLY-MIXED",
            SetItemNumber = "ITEM-HQ-ONLY-MIXED",
            SetBarcode = "930000099902",
            SetPurchasePrice = 99m,
            SetRetailPrice = 10m,
            SetQuantity = 1,
            SetType = 2,
            IsActive = true,
            IsDeleted = false,
            CreatedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        await SeedHqProductAsync("P-HQ-ONLY-MIXED", 5m, 10m);
        await SeedHqPriceAsync("S01", "P-HQ-ONLY-MIXED", 5m, 10m);
        await SeedHqPriceAsync("HQ99", "P-HQ-ONLY-MIXED", 66m, 77m);
        await _hqDb.Insertable(new DIC_分店一品多码表
        {
            HGUID = "hq-only-mixed-multi",
            H分店代码 = "HQ99",
            H商品编码 = "P-HQ-ONLY-MIXED",
            H多码商品编码 = "MC-HQ-ONLY-MIXED",
            H分店多码商品编码 = "HQ99MC-HQ-ONLY-MIXED",
            H多条形码 = "930000099902",
            H进货价 = 88m,
            H一品多码零售价 = 10m,
            H使用状态 = true,
            FGC_Creator = "seed",
            FGC_CreateDate = DateTime.UtcNow,
            FGC_LastModifier = "seed",
            FGC_LastModifyDate = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-hq-only-mixed",
            InvoiceGUID = "invoice-hq-only-mixed",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-HQ-ONLY-MIXED",
            ItemNumber = "ITEM-OLD",
            Barcode = "930000000000",
            PurchasePrice = 77m,
            RetailPrice = 18m,
            IsDeleted = false,
        });

        var hqOnlyPriceBefore = await _hqDb.Queryable<DIC_商品零售价表>()
            .SingleAsync(x => x.H分店代码 == "HQ99" && x.H商品编码 == "P-HQ-ONLY-MIXED");
        var hqOnlyMultiBefore = await _hqDb.Queryable<DIC_分店一品多码表>()
            .SingleAsync(x => x.H分店代码 == "HQ99" && x.H商品编码 == "P-HQ-ONLY-MIXED");

        var response = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-hq-only-mixed",
            new UpdateHqProductsRequest
            {
                DetailGuids = ["detail-hq-only-mixed"],
                TargetStoreCodes = ["S01", "HQ99"],
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdateRetailPrice = true,
                },
            },
            "tester"
        );

        Assert.True(response.Success, BuildFailureMessage(response));
        var hqOnlyPriceAfter = await _hqDb.Queryable<DIC_商品零售价表>()
            .SingleAsync(x => x.H分店代码 == "HQ99" && x.H商品编码 == "P-HQ-ONLY-MIXED");
        var hqOnlyMultiAfter = await _hqDb.Queryable<DIC_分店一品多码表>()
            .SingleAsync(x => x.H分店代码 == "HQ99" && x.H商品编码 == "P-HQ-ONLY-MIXED");
        var validHqPriceAfter = await _hqDb.Queryable<DIC_商品零售价表>()
            .SingleAsync(x => x.H分店代码 == "S01" && x.H商品编码 == "P-HQ-ONLY-MIXED");
        Assert.Equal((hqOnlyPriceBefore.H进货价, hqOnlyPriceBefore.H分店零售价),
            (hqOnlyPriceAfter.H进货价, hqOnlyPriceAfter.H分店零售价));
        Assert.Equal(18m, validHqPriceAfter.H分店零售价);
        Assert.Equal("tester", validHqPriceAfter.FGC_LastModifier);
        Assert.Equal("seed", hqOnlyPriceAfter.FGC_LastModifier);
        Assert.Equal(hqOnlyPriceBefore.FGC_LastModifyDate, hqOnlyPriceAfter.FGC_LastModifyDate);
        Assert.Equal((hqOnlyMultiBefore.H进货价, hqOnlyMultiBefore.H一品多码零售价),
            (hqOnlyMultiAfter.H进货价, hqOnlyMultiAfter.H一品多码零售价));
        Assert.Equal(0, await _localDb.Queryable<StoreRetailPrice>()
            .Where(x => x.ProductCode == "P-HQ-ONLY-MIXED")
            .CountAsync());
        Assert.Equal(1, await _localDb.Queryable<StoreMultiCodeProduct>()
            .Where(x => x.StoreCode == "S01" && x.ProductCode == "P-HQ-ONLY-MIXED")
            .CountAsync());
        Assert.Equal(0, await _localDb.Queryable<StoreMultiCodeProduct>()
            .Where(x => x.StoreCode == "HQ99" && x.ProductCode == "P-HQ-ONLY-MIXED")
            .CountAsync());
    }

    [Fact]
    public async Task UpdateHqProductsAsync_目标全部为HQ独有店_返回校验错误且零写入()
    {
        await SeedStoreAsync("S01", true);
        await SeedInvoiceAsync("invoice-hq-only", "S01", "SUP01");
        await SeedExistingProductAsync("P-HQ-ONLY", "SUP01");
        await _localDb.Insertable(new ProductSetCode
        {
            SetCodeId = "set-hq-only",
            ProductCode = "P-HQ-ONLY",
            SetProductCode = "MC-HQ-ONLY",
            SetItemNumber = "ITEM-HQ-ONLY",
            SetBarcode = "930000099903",
            SetPurchasePrice = 99m,
            SetRetailPrice = 10m,
            SetQuantity = 1,
            SetType = 2,
            IsActive = true,
            IsDeleted = false,
            CreatedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-hq-only",
            InvoiceGUID = "invoice-hq-only",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-HQ-ONLY",
            ItemNumber = "ITEM-OLD",
            Barcode = "930000000000",
            PurchasePrice = 77m,
            RetailPrice = 18m,
            IsDeleted = false,
        });

        var response = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-hq-only",
            new UpdateHqProductsRequest
            {
                DetailGuids = ["detail-hq-only"],
                TargetStoreCodes = ["HQ99"],
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdateRetailPrice = true,
                },
            },
            "tester"
        );

        Assert.False(response.Success);
        Assert.Equal("VALIDATION_ERROR", response.ErrorCode);
        Assert.Contains("没有可更新的 HBweb 分店", response.Message);
        Assert.Equal(99m, (await _localDb.Queryable<ProductSetCode>()
            .SingleAsync(x => x.SetCodeId == "set-hq-only")).SetPurchasePrice);
        Assert.Equal(0, await _localDb.Queryable<StoreRetailPrice>().CountAsync());
        Assert.Equal(0, await _localDb.Queryable<StoreMultiCodeProduct>().CountAsync());
        Assert.Equal(0, await _hqDb.Queryable<DIC_商品信息字典表>().CountAsync());
        Assert.Equal(0, await _hqDb.Queryable<DIC_商品零售价表>().CountAsync());
        Assert.Equal(0, await _hqDb.Queryable<DIC_分店一品多码表>().CountAsync());
    }

    [Fact]
    public async Task UpdateHqProductsAsync_本地分店停用或软删除_拒绝并零写入()
    {
        await SeedStoreAsync("S01", false);
        await SeedStoreAsync("S03", true);
        await _localDb.Insertable(new Store
        {
            StoreGUID = "store-S02-deleted",
            StoreCode = "S02",
            StoreName = "S02",
            IsActive = true,
            IsDeleted = true,
        }).ExecuteCommandAsync();
        await SeedInvoiceAsync("invoice-invalid-store", "S01", "SUP01");
        await SeedExistingProductAsync("P-INVALID-STORE", "SUP01");
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-invalid-store",
            InvoiceGUID = "invoice-invalid-store",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-INVALID-STORE",
            ItemNumber = "ITEM-OLD",
            Barcode = "930000000000",
            PurchasePrice = 77m,
            RetailPrice = 18m,
            IsDeleted = false,
        });

        foreach (var storeCode in new[] { "S01", "S02" })
        {
            var response = await CreateSyncService().UpdateHqProductsAsync(
                "invoice-invalid-store",
                new UpdateHqProductsRequest
                {
                    DetailGuids = ["detail-invalid-store"],
                    TargetStoreCodes = [storeCode],
                    UpdateFields = new UpdateToStorePricesFields
                    {
                        UpdateRetailPrice = true,
                    },
                },
                "tester"
            );

            Assert.False(response.Success);
            Assert.Equal("INVALID_TARGET_STORE", response.ErrorCode);
        }

        Assert.Equal(0, await _localDb.Queryable<StoreRetailPrice>().CountAsync());
        Assert.Equal(0, await _localDb.Queryable<StoreMultiCodeProduct>().CountAsync());
        Assert.Equal(0, await _hqDb.Queryable<DIC_商品信息字典表>().CountAsync());
    }

    [Fact]
    public async Task UpdateHqProductsAsync_整组与部分子项缺失且无门店主价格_补齐全部HBweb启用店但只推送有效目标店()
    {
        await SeedStoreAsync("S01", true);
        await SeedStoreAsync("S02", true);
        await SeedInvoiceAsync("invoice-missing-groups", "S01", "SUP01");
        await SeedExistingProductAsync("P-FULL-MISSING", "SUP01");
        await SeedExistingProductAsync("P-PARTIAL-MISSING", "SUP01", "ITEM-PARTIAL", "930000099904");
        await _localDb.Insertable(new[]
        {
            new ProductSetCode
            {
                SetCodeId = "set-full-a",
                ProductCode = "P-FULL-MISSING",
                SetProductCode = "MC-FULL-A",
                SetItemNumber = "ITEM-MC-FULL-A",
                SetBarcode = "930000099905",
                SetPurchasePrice = 99m,
                SetRetailPrice = 10m,
                SetType = 1,
                IsActive = true,
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
            },
            new ProductSetCode
            {
                SetCodeId = "set-full-b",
                ProductCode = "P-FULL-MISSING",
                SetProductCode = "MC-FULL-B",
                SetItemNumber = "ITEM-MC-FULL-B",
                SetBarcode = "930000099906",
                SetPurchasePrice = 99m,
                SetRetailPrice = 20m,
                SetType = 1,
                IsActive = true,
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
            },
            new ProductSetCode
            {
                SetCodeId = "set-partial-a",
                ProductCode = "P-PARTIAL-MISSING",
                SetProductCode = "MC-PARTIAL-A",
                SetItemNumber = "ITEM-MC-PARTIAL-A",
                SetBarcode = "930000099907",
                SetPurchasePrice = 99m,
                SetRetailPrice = 10m,
                SetType = 1,
                IsActive = true,
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
            },
            new ProductSetCode
            {
                SetCodeId = "set-partial-b",
                ProductCode = "P-PARTIAL-MISSING",
                SetProductCode = "MC-PARTIAL-B",
                SetItemNumber = "ITEM-MC-PARTIAL-B",
                SetBarcode = "930000099908",
                SetPurchasePrice = 99m,
                SetRetailPrice = 20m,
                SetType = 1,
                IsActive = true,
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
            },
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new StoreMultiCodeProduct
        {
            UUID = "partial-existing-a",
            StoreCode = "S01",
            ProductCode = "P-PARTIAL-MISSING",
            MultiCodeProductCode = "MC-PARTIAL-A",
            StoreMultiCodeProductCode = "S01MC-PARTIAL-A",
            MultiBarcode = "930000099907",
            PurchasePrice = 99m,
            MultiCodeRetailPrice = 10m,
            IsActive = true,
            IsDeleted = false,
            CreatedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-full-missing",
            InvoiceGUID = "invoice-missing-groups",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-FULL-MISSING",
            ItemNumber = "ITEM-OLD",
            Barcode = "930000000000",
            PurchasePrice = 77m,
            RetailPrice = 18m,
            IsDeleted = false,
        });
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-partial-missing",
            InvoiceGUID = "invoice-missing-groups",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-PARTIAL-MISSING",
            ItemNumber = "ITEM-PARTIAL",
            Barcode = "930000099904",
            PurchasePrice = 77m,
            RetailPrice = 18m,
            IsDeleted = false,
        });

        var response = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-missing-groups",
            new UpdateHqProductsRequest
            {
                DetailGuids = ["detail-full-missing", "detail-partial-missing"],
                TargetStoreCodes = ["S01"],
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdateRetailPrice = true,
                },
            },
            "tester"
        );

        Assert.True(response.Success, BuildFailureMessage(response));
        Assert.Equal(2, await _localDb.Queryable<StoreMultiCodeProduct>()
            .Where(x => x.StoreCode == "S01" && x.ProductCode == "P-FULL-MISSING")
            .CountAsync());
        Assert.Equal(2, await _localDb.Queryable<StoreMultiCodeProduct>()
            .Where(x => x.StoreCode == "S02" && x.ProductCode == "P-FULL-MISSING")
            .CountAsync());
        Assert.Equal(2, await _localDb.Queryable<StoreMultiCodeProduct>()
            .Where(x => x.StoreCode == "S01" && x.ProductCode == "P-PARTIAL-MISSING")
            .CountAsync());
        Assert.Equal(2, await _localDb.Queryable<StoreMultiCodeProduct>()
            .Where(x => x.StoreCode == "S02" && x.ProductCode == "P-PARTIAL-MISSING")
            .CountAsync());
        Assert.Equal(0, await _localDb.Queryable<StoreRetailPrice>()
            .Where(x => x.ProductCode == "P-FULL-MISSING" || x.ProductCode == "P-PARTIAL-MISSING")
            .CountAsync());
        Assert.Equal(2, await _hqDb.Queryable<DIC_分店一品多码表>()
            .Where(x => x.H分店代码 == "S01" && x.H商品编码 == "P-FULL-MISSING")
            .CountAsync());
        Assert.Equal(0, await _hqDb.Queryable<DIC_分店一品多码表>()
            .Where(x => x.H分店代码 == "S02" && x.H商品编码 == "P-FULL-MISSING")
            .CountAsync());
    }

    [Fact]
    public async Task UpdateHqProductsAsync_全局仓库回退且门店主成本优先_Type1Type2均不采用明细成本()
    {
        await SeedStoreAsync("S01", true);
        await SeedInvoiceAsync("invoice-cost-fallback", "S01", "SUP01");
        await SeedExistingProductAsync("P-COST-FALLBACK", "SUP01");
        await _localDb.Updateable<Product>()
            .SetColumns(x => x.PurchasePrice == 0m)
            .Where(x => x.ProductCode == "P-COST-FALLBACK")
            .ExecuteCommandAsync();
        await _localDb.Insertable(new WarehouseProduct
        {
            ProductCode = "P-COST-FALLBACK",
            ImportPrice = 12m,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new[]
        {
            new ProductSetCode
            {
                SetCodeId = "set-cost-type1",
                ProductCode = "P-COST-FALLBACK",
                SetProductCode = "MC-COST-TYPE1",
                SetItemNumber = "ITEM-COST-TYPE1",
                SetBarcode = "930000099909",
                SetPurchasePrice = 99m,
                SetRetailPrice = 10m,
                SetType = 1,
                IsActive = true,
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
            },
            new ProductSetCode
            {
                SetCodeId = "set-cost-type2",
                ProductCode = "P-COST-FALLBACK",
                SetProductCode = "MC-COST-TYPE2",
                SetItemNumber = "ITEM-COST-TYPE2",
                SetBarcode = "930000099910",
                SetPurchasePrice = 99m,
                SetRetailPrice = 0m,
                SetType = 2,
                IsActive = true,
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
            },
        }).ExecuteCommandAsync();
        await SeedLocalPriceAsync("S01", "P-COST-FALLBACK", 9m, 20m);
        await _localDb.Insertable(new[]
        {
            new StoreMultiCodeProduct
            {
                UUID = "store-cost-type1",
                StoreCode = "S01",
                ProductCode = "P-COST-FALLBACK",
                MultiCodeProductCode = "MC-COST-TYPE1",
                StoreMultiCodeProductCode = "S01MC-COST-TYPE1",
                MultiBarcode = "930000099909",
                PurchasePrice = 99m,
                MultiCodeRetailPrice = 10m,
                IsActive = true,
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
            },
            new StoreMultiCodeProduct
            {
                UUID = "store-cost-type2",
                StoreCode = "S01",
                ProductCode = "P-COST-FALLBACK",
                MultiCodeProductCode = "MC-COST-TYPE2",
                StoreMultiCodeProductCode = "S01MC-COST-TYPE2",
                MultiBarcode = "930000099910",
                PurchasePrice = 99m,
                MultiCodeRetailPrice = 0m,
                IsActive = true,
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
            },
        }).ExecuteCommandAsync();
        await SeedHqProductAsync("P-COST-FALLBACK", 0m, 10m);
        await SeedHqPriceAsync("S01", "P-COST-FALLBACK", 0m, 10m);
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-cost-fallback",
            InvoiceGUID = "invoice-cost-fallback",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-COST-FALLBACK",
            ItemNumber = "ITEM-OLD",
            Barcode = "930000000000",
            PurchasePrice = 77m,
            RetailPrice = 18m,
            IsDeleted = false,
        });

        var response = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-cost-fallback",
            new UpdateHqProductsRequest
            {
                DetailGuids = ["detail-cost-fallback"],
                TargetStoreCodes = ["S01"],
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdatePurchasePrice = true,
                },
            },
            "tester"
        );

        Assert.True(response.Success, BuildFailureMessage(response));
        var globalCosts = await _localDb.Queryable<ProductSetCode>()
            .Where(x => x.ProductCode == "P-COST-FALLBACK")
            .OrderBy(x => x.SetProductCode)
            .Select(x => x.SetPurchasePrice)
            .ToListAsync();
        var storeCosts = await _localDb.Queryable<StoreMultiCodeProduct>()
            .Where(x => x.ProductCode == "P-COST-FALLBACK")
            .OrderBy(x => x.MultiCodeProductCode)
            .Select(x => x.PurchasePrice)
            .ToListAsync();
        Assert.Equal(new decimal?[] { 12m, 12m }, globalCosts);
        Assert.Equal(new decimal?[] { 9m, 9m }, storeCosts);
        Assert.Equal(77m, (await _localDb.Queryable<StoreLocalSupplierInvoiceDetails>()
            .SingleAsync(x => x.DetailGUID == "detail-cost-fallback")).PurchasePrice);

        var hqGlobalCosts = await _hqDb.Queryable<DIC_一品多码表>()
            .Where(x => x.H商品编码 == "P-COST-FALLBACK")
            .OrderBy(x => x.H多码商品编号)
            .Select(x => x.H进货价)
            .ToListAsync();
        var hqStoreCosts = await _hqDb.Queryable<DIC_分店一品多码表>()
            .Where(x => x.H商品编码 == "P-COST-FALLBACK" && x.H分店代码 == "S01")
            .OrderBy(x => x.H多码商品编码)
            .Select(x => x.H进货价)
            .ToListAsync();
        Assert.Equal(new decimal?[] { 12m, 12m }, hqGlobalCosts);
        Assert.Equal(new decimal?[] { 9m, 9m }, hqStoreCosts);
    }

    [Fact]
    public async Task UpdateHqProductsAsync_重复提交缺失投影_不重复创建关系且成本稳定()
    {
        await SeedStoreAsync("S01", true);
        await SeedInvoiceAsync("invoice-idempotent", "S01", "SUP01");
        await SeedExistingProductAsync("P-IDEMPOTENT", "SUP01");
        await _localDb.Insertable(new ProductSetCode
        {
            SetCodeId = "set-idempotent",
            ProductCode = "P-IDEMPOTENT",
            SetProductCode = "MC-IDEMPOTENT",
            SetItemNumber = "ITEM-IDEMPOTENT",
            SetBarcode = "930000099911",
            SetPurchasePrice = 99m,
            SetRetailPrice = 10m,
            SetType = 2,
            IsActive = true,
            IsDeleted = false,
            CreatedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        await SeedHqProductAsync("P-IDEMPOTENT", 5m, 10m);
        await SeedHqPriceAsync("S01", "P-IDEMPOTENT", 5m, 10m);
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-idempotent",
            InvoiceGUID = "invoice-idempotent",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-IDEMPOTENT",
            ItemNumber = "ITEM-OLD",
            Barcode = "930000000000",
            PurchasePrice = 77m,
            RetailPrice = 18m,
            IsDeleted = false,
        });
        var request = new UpdateHqProductsRequest
        {
            DetailGuids = ["detail-idempotent"],
            TargetStoreCodes = ["S01"],
            UpdateFields = new UpdateToStorePricesFields
            {
                UpdatePurchasePrice = true,
            },
        };

        var first = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-idempotent",
            request,
            "tester"
        );
        var firstLocalMultiCount = await _localDb.Queryable<StoreMultiCodeProduct>()
            .CountAsync(x => x.ProductCode == "P-IDEMPOTENT");
        var firstHqGlobalCount = await _hqDb.Queryable<DIC_一品多码表>()
            .CountAsync(x => x.H商品编码 == "P-IDEMPOTENT");
        var firstHqStoreCount = await _hqDb.Queryable<DIC_分店一品多码表>()
            .CountAsync(x => x.H商品编码 == "P-IDEMPOTENT" && x.H分店代码 == "S01");

        var second = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-idempotent",
            request,
            "tester"
        );

        Assert.True(first.Success, BuildFailureMessage(first));
        Assert.True(second.Success, BuildFailureMessage(second));
        Assert.Equal(1, first.Data!.HqProductSetCodesCreated);
        Assert.Equal(1, first.Data.HqStoreMultiCodesCreated);
        Assert.Equal(0, second.Data!.HqProductSetCodesCreated);
        Assert.Equal(0, second.Data.HqStoreMultiCodesCreated);
        Assert.Equal(1, firstLocalMultiCount);
        Assert.Equal(1, firstHqGlobalCount);
        Assert.Equal(1, firstHqStoreCount);
        Assert.Equal(firstLocalMultiCount, await _localDb.Queryable<StoreMultiCodeProduct>()
            .CountAsync(x => x.ProductCode == "P-IDEMPOTENT"));
        Assert.Equal(firstHqGlobalCount, await _hqDb.Queryable<DIC_一品多码表>()
            .CountAsync(x => x.H商品编码 == "P-IDEMPOTENT"));
        Assert.Equal(firstHqStoreCount, await _hqDb.Queryable<DIC_分店一品多码表>()
            .CountAsync(x => x.H商品编码 == "P-IDEMPOTENT" && x.H分店代码 == "S01"));
        Assert.Equal(5m, (await _localDb.Queryable<StoreMultiCodeProduct>()
            .SingleAsync(x => x.ProductCode == "P-IDEMPOTENT")).PurchasePrice);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task UpdateHqProductsAsync_安全商品与门店历史墓碑混批_补齐和HQ写入整体回滚(
        bool tombstoneIsActive,
        bool tombstoneIsDeleted
    )
    {
        await SeedStoreAsync("S01", true);
        await SeedInvoiceAsync("invoice-mixed-rollback", "S01", "SUP01");
        await SeedExistingProductAsync("P-SAFE-ROLLBACK", "SUP01");
        await SeedExistingProductAsync("P-TOMBSTONE-ROLLBACK", "SUP01", "ITEM-TOMBSTONE", "930000099912");
        await _localDb.Insertable(new[]
        {
            new ProductSetCode
            {
                SetCodeId = "set-safe-rollback",
                ProductCode = "P-SAFE-ROLLBACK",
                SetProductCode = "MC-SAFE-ROLLBACK",
                SetItemNumber = "ITEM-SAFE-ROLLBACK",
                SetBarcode = "930000099913",
                SetPurchasePrice = 99m,
                SetRetailPrice = 10m,
                SetType = 2,
                IsActive = true,
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
            },
            new ProductSetCode
            {
                SetCodeId = "set-tombstone-rollback",
                ProductCode = "P-TOMBSTONE-ROLLBACK",
                SetProductCode = "MC-TOMBSTONE-ROLLBACK",
                SetItemNumber = "ITEM-TOMBSTONE-ROLLBACK",
                SetBarcode = "930000099914",
                SetPurchasePrice = 99m,
                SetRetailPrice = 10m,
                SetType = 2,
                IsActive = true,
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
            },
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new StoreMultiCodeProduct
        {
            UUID = "tombstone-rollback",
            StoreCode = "S01",
            ProductCode = "P-TOMBSTONE-ROLLBACK",
            MultiCodeProductCode = "MC-TOMBSTONE-ROLLBACK",
            StoreMultiCodeProductCode = "S01MC-TOMBSTONE-ROLLBACK",
            MultiBarcode = "930000099914",
            PurchasePrice = 44m,
            MultiCodeRetailPrice = 10m,
            IsActive = tombstoneIsActive,
            IsDeleted = tombstoneIsDeleted,
            CreatedAt = DateTime.UtcNow,
            UpdatedBy = "历史操作人",
        }).ExecuteCommandAsync();
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-safe-rollback",
            InvoiceGUID = "invoice-mixed-rollback",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-SAFE-ROLLBACK",
            ItemNumber = "ITEM-OLD",
            Barcode = "930000000000",
            PurchasePrice = 77m,
            RetailPrice = 18m,
            IsDeleted = false,
        });
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-tombstone-rollback",
            InvoiceGUID = "invoice-mixed-rollback",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-TOMBSTONE-ROLLBACK",
            ItemNumber = "ITEM-TOMBSTONE",
            Barcode = "930000099912",
            PurchasePrice = 77m,
            RetailPrice = 18m,
            IsDeleted = false,
        });

        var response = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-mixed-rollback",
            new UpdateHqProductsRequest
            {
                DetailGuids = ["detail-safe-rollback", "detail-tombstone-rollback"],
                TargetStoreCodes = ["S01"],
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdatePurchasePrice = true,
                },
            },
            "tester"
        );

        Assert.False(response.Success);
        Assert.Equal("HQ_UPDATE_ERROR", response.ErrorCode);
        Assert.Contains("P-TOMBSTONE-ROLLBACK", response.Message);
        Assert.Contains("停用或软删除", response.Message);
        Assert.Equal(99m, (await _localDb.Queryable<ProductSetCode>()
            .SingleAsync(x => x.ProductCode == "P-SAFE-ROLLBACK")).SetPurchasePrice);
        Assert.Equal(99m, (await _localDb.Queryable<ProductSetCode>()
            .SingleAsync(x => x.ProductCode == "P-TOMBSTONE-ROLLBACK")).SetPurchasePrice);
        Assert.Equal(0, await _localDb.Queryable<StoreRetailPrice>().CountAsync());
        Assert.Equal(0, await _localDb.Queryable<StoreMultiCodeProduct>()
            .CountAsync(x => x.ProductCode == "P-SAFE-ROLLBACK"));
        var tombstone = await _localDb.Queryable<StoreMultiCodeProduct>()
            .SingleAsync(x => x.ProductCode == "P-TOMBSTONE-ROLLBACK");
        Assert.Equal(tombstoneIsActive, tombstone.IsActive);
        Assert.Equal(tombstoneIsDeleted, tombstone.IsDeleted);
        Assert.Equal(44m, tombstone.PurchasePrice);
        Assert.Equal(0, await _hqDb.Queryable<DIC_商品信息字典表>().CountAsync());
        Assert.Equal(0, await _hqDb.Queryable<DIC_一品多码表>().CountAsync());
        Assert.Equal(0, await _hqDb.Queryable<DIC_分店一品多码表>().CountAsync());
    }

    [Fact]
    public async Task UpdateHqProductsAsync_安全商品与Type1Type2冲突混批_补齐和HQ写入整体回滚()
    {
        await SeedStoreAsync("S01", true);
        await SeedInvoiceAsync("invoice-type-conflict-rollback", "S01", "SUP01");
        await SeedExistingProductAsync("P-SAFE-TYPE-ROLLBACK", "SUP01");
        await SeedExistingProductAsync("P-TYPE-CONFLICT-ROLLBACK", "SUP01", "ITEM-TYPE-CONFLICT", "930000099916");
        await _localDb.Insertable(new[]
        {
            new ProductSetCode
            {
                SetCodeId = "set-safe-type-rollback",
                ProductCode = "P-SAFE-TYPE-ROLLBACK",
                SetProductCode = "MC-SAFE-TYPE-ROLLBACK",
                SetItemNumber = "ITEM-SAFE-TYPE-ROLLBACK",
                SetBarcode = "930000099917",
                SetPurchasePrice = 99m,
                SetRetailPrice = 10m,
                SetType = 2,
                IsActive = true,
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
            },
            new ProductSetCode
            {
                SetCodeId = "set-conflict-type1-rollback",
                ProductCode = "P-TYPE-CONFLICT-ROLLBACK",
                SetProductCode = "MC-TYPE-CONFLICT-ROLLBACK",
                SetItemNumber = "ITEM-TYPE-CONFLICT-ROLLBACK",
                SetBarcode = "930000099918",
                SetPurchasePrice = 99m,
                SetRetailPrice = 10m,
                SetType = 1,
                IsActive = true,
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
            },
            new ProductSetCode
            {
                SetCodeId = "set-conflict-type2-rollback",
                ProductCode = "P-TYPE-CONFLICT-ROLLBACK",
                SetProductCode = "MC-TYPE-CONFLICT-ROLLBACK",
                SetItemNumber = "ITEM-TYPE-CONFLICT-ROLLBACK",
                SetBarcode = "930000099918",
                SetPurchasePrice = 99m,
                SetRetailPrice = 10m,
                SetType = 2,
                IsActive = true,
                IsDeleted = false,
                CreatedAt = DateTime.UtcNow,
            },
        }).ExecuteCommandAsync();
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-safe-type-rollback",
            InvoiceGUID = "invoice-type-conflict-rollback",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-SAFE-TYPE-ROLLBACK",
            ItemNumber = "ITEM-OLD",
            Barcode = "930000000000",
            PurchasePrice = 77m,
            RetailPrice = 18m,
            IsDeleted = false,
        });
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-type-conflict-rollback",
            InvoiceGUID = "invoice-type-conflict-rollback",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-TYPE-CONFLICT-ROLLBACK",
            ItemNumber = "ITEM-TYPE-CONFLICT",
            Barcode = "930000099916",
            PurchasePrice = 77m,
            RetailPrice = 18m,
            IsDeleted = false,
        });

        var response = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-type-conflict-rollback",
            new UpdateHqProductsRequest
            {
                DetailGuids = ["detail-safe-type-rollback", "detail-type-conflict-rollback"],
                TargetStoreCodes = ["S01"],
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdatePurchasePrice = true,
                },
            },
            "tester"
        );

        Assert.False(response.Success);
        Assert.Equal("HQ_UPDATE_ERROR", response.ErrorCode);
        Assert.Contains("P-TYPE-CONFLICT-ROLLBACK", response.Message);
        Assert.Contains("Type1/Type2", response.Message);
        Assert.Equal(99m, (await _localDb.Queryable<ProductSetCode>()
            .SingleAsync(x => x.ProductCode == "P-SAFE-TYPE-ROLLBACK")).SetPurchasePrice);
        Assert.Equal(99m, (await _localDb.Queryable<ProductSetCode>()
            .Where(x => x.ProductCode == "P-TYPE-CONFLICT-ROLLBACK")
            .OrderBy(x => x.SetCodeId)
            .FirstAsync()).SetPurchasePrice);
        Assert.Equal(0, await _localDb.Queryable<StoreMultiCodeProduct>().CountAsync());
        Assert.Equal(0, await _hqDb.Queryable<DIC_商品信息字典表>().CountAsync());
        Assert.Equal(0, await _hqDb.Queryable<DIC_一品多码表>().CountAsync());
        Assert.Equal(0, await _hqDb.Queryable<DIC_分店一品多码表>().CountAsync());
    }

    [Fact]
    public async Task UpdateHqProductsAsync_补齐后门店成本重算触发器失败_本地补齐与HQ写入回滚()
    {
        await SeedStoreAsync("S01", true);
        await SeedInvoiceAsync("invoice-trigger-rollback", "S01", "SUP01");
        await SeedExistingProductAsync("P-TRIGGER-ROLLBACK", "SUP01");
        await _localDb.Insertable(new ProductSetCode
        {
            SetCodeId = "set-trigger-rollback",
            ProductCode = "P-TRIGGER-ROLLBACK",
            SetProductCode = "MC-TRIGGER-ROLLBACK",
            SetItemNumber = "ITEM-TRIGGER-ROLLBACK",
            SetBarcode = "930000099915",
            SetPurchasePrice = 99m,
            SetRetailPrice = 10m,
            SetType = 2,
            IsActive = true,
            IsDeleted = false,
            CreatedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        await SeedDetailAsync(new StoreLocalSupplierInvoiceDetails
        {
            DetailGUID = "detail-trigger-rollback",
            InvoiceGUID = "invoice-trigger-rollback",
            StoreCode = "S01",
            SupplierCode = "SUP01",
            ProductCode = "P-TRIGGER-ROLLBACK",
            ItemNumber = "ITEM-OLD",
            Barcode = "930000000000",
            PurchasePrice = 77m,
            RetailPrice = 18m,
            IsDeleted = false,
        });
        await _localDb.Ado.ExecuteCommandAsync(
            """
            CREATE TRIGGER reject_recalculated_store_multi
            BEFORE UPDATE OF PurchasePrice ON StoreMultiCodeProduct
            BEGIN
                SELECT RAISE(ABORT, 'reject recalculated store multi');
            END;
            """
        );

        var response = await CreateSyncService().UpdateHqProductsAsync(
            "invoice-trigger-rollback",
            new UpdateHqProductsRequest
            {
                DetailGuids = ["detail-trigger-rollback"],
                TargetStoreCodes = ["S01"],
                UpdateFields = new UpdateToStorePricesFields
                {
                    UpdatePurchasePrice = true,
                },
            },
            "tester"
        );

        Assert.False(response.Success);
        Assert.Equal("HQ_UPDATE_ERROR", response.ErrorCode);
        Assert.Equal(99m, (await _localDb.Queryable<ProductSetCode>()
            .SingleAsync(x => x.SetCodeId == "set-trigger-rollback")).SetPurchasePrice);
        Assert.Equal(0, await _localDb.Queryable<StoreRetailPrice>().CountAsync());
        Assert.Equal(0, await _localDb.Queryable<StoreMultiCodeProduct>().CountAsync());
        Assert.Equal(0, await _hqDb.Queryable<DIC_商品信息字典表>().CountAsync());
        Assert.Equal(0, await _hqDb.Queryable<DIC_一品多码表>().CountAsync());
        Assert.Equal(0, await _hqDb.Queryable<DIC_分店一品多码表>().CountAsync());
    }

    private LocalSupplierInvoiceHqProductSyncService CreateSyncService(
        IWarehouseProductChangeHistoryService? historyService = null
    )
    {
        return new LocalSupplierInvoiceHqProductSyncService(
            CreateSqlSugarContext(_localDb),
            CreateHqSqlSugarContext(_hqDb),
            NullLogger<LocalSupplierInvoiceHqProductSyncService>.Instance,
            historyService ?? WarehouseProductChangeHistoryTestDouble.CreateNoop()
        );
    }

    private static string BuildFailureMessage(ApiResponse<EnsureHqProductsResult> result)
    {
        var errors = result.Data?.Errors ?? (result.Details as EnsureHqProductsResult)?.Errors;
        return errors == null
            ? result.Message
            : $"{result.Message}: {string.Join("; ", errors.Select(x => $"{x.DetailGuid}/{x.StoreCode}: {x.Message}"))}";
    }

    private static string BuildFailureMessage(ApiResponse<UpdateHqProductsResult> result)
    {
        var errors = result.Data?.Errors ?? (result.Details as UpdateHqProductsResult)?.Errors;
        return errors == null
            ? result.Message
            : $"{result.Message}: {string.Join("; ", errors.Select(x => $"{x.DetailGuid}/{x.StoreCode}: {x.Message}"))}";
    }

    private static void AssertCreatedProductSnapshot(
        Product product,
        WarehouseProductChangeSnapshotDto snapshot
    )
    {
        Assert.Equal(product.ProductCode, snapshot.ProductCode);
        Assert.Equal(product.PurchasePrice, snapshot.ImportPrice);
        Assert.Equal(product.RetailPrice, snapshot.RetailPrice);
        Assert.Equal(product.LocalSupplierCode, snapshot.LocalSupplierCode);
        Assert.Equal(product.ProductName, snapshot.ProductName);
        Assert.Equal(product.EnglishName, snapshot.EnglishName);
        Assert.Equal(product.ItemNumber, snapshot.ItemNumber);
        Assert.Equal(product.Barcode, snapshot.Barcode);
        Assert.Equal(product.ProductType, snapshot.ProductType);
        Assert.Equal(product.ProductCategoryGUID, snapshot.ProductCategoryGuid);
        Assert.Equal(product.WarehouseCategoryGUID, snapshot.WarehouseCategoryGuid);
        Assert.Equal(product.MiddlePackageQuantity, snapshot.MiddlePackageQuantity);
        Assert.Equal(product.ProductImage, snapshot.ProductImage);
        Assert.Equal(product.IsAutoPricing, snapshot.IsAutoPricing);
        Assert.Equal(product.IsActive, snapshot.IsActive);
        Assert.False(snapshot.WarehouseProductExists);
        Assert.Null(snapshot.WarehouseSource);
        Assert.Null(snapshot.DomesticSource);

        var productSource = Assert.IsType<WarehouseProductChangeSourceValuesDto>(snapshot.ProductSource);
        Assert.Equal(snapshot.ImportPrice, productSource.ImportPrice);
        Assert.Equal(snapshot.RetailPrice, productSource.RetailPrice);
        Assert.Equal(snapshot.LocalSupplierCode, productSource.LocalSupplierCode);
        Assert.Equal(snapshot.ProductName, productSource.ProductName);
        Assert.Equal(snapshot.EnglishName, productSource.EnglishName);
        Assert.Equal(snapshot.ItemNumber, productSource.ItemNumber);
        Assert.Equal(snapshot.Barcode, productSource.Barcode);
        Assert.Equal(snapshot.ProductType, productSource.ProductType);
        Assert.Equal(snapshot.ProductCategoryGuid, productSource.ProductCategoryGuid);
        Assert.Equal(snapshot.WarehouseCategoryGuid, productSource.WarehouseCategoryGuid);
        Assert.Equal(snapshot.MiddlePackageQuantity, productSource.MiddlePackageQuantity);
        Assert.Equal(snapshot.ProductImage, productSource.ProductImage);
        Assert.Equal(snapshot.IsAutoPricing, productSource.IsAutoPricing);
        Assert.Equal(snapshot.IsActive, productSource.IsActive);
    }

    private async Task SeedStoreAsync(string storeCode, bool active)
    {
        await _localDb.Insertable(new Store
        {
            StoreGUID = $"store-{storeCode}",
            StoreCode = storeCode,
            StoreName = storeCode,
            IsActive = active,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task SeedInvoiceAsync(string invoiceGuid, string storeCode, string supplierCode)
    {
        await _localDb.Insertable(new StoreLocalSupplierInvoice
        {
            InvoiceGUID = invoiceGuid,
            StoreCode = storeCode,
            SupplierCode = supplierCode,
            InvoiceNo = "INV-001",
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task SeedDetailAsync(StoreLocalSupplierInvoiceDetails detail)
    {
        await _localDb.Insertable(detail).ExecuteCommandAsync();
    }

    private async Task SeedExistingProductAsync(
        string productCode,
        string supplierCode,
        string itemNumber = "ITEM-OLD",
        string barcode = "930000000000"
    )
    {
        await _localDb.Insertable(new Product
        {
            UUID = productCode,
            ProductCode = productCode,
            LocalSupplierCode = supplierCode,
            ItemNumber = itemNumber,
            Barcode = barcode,
            ProductName = "旧商品",
            PurchasePrice = 5m,
            RetailPrice = 10m,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task SeedDomesticProductAsync(string productCode, string supplierCode)
    {
        await _localDb.Insertable(new DomesticProduct
        {
            ProductCode = productCode,
            SupplierCode = supplierCode,
            ProductName = $"国内商品-{productCode}",
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task SeedLocalPriceAsync(string storeCode, string productCode, decimal purchasePrice, decimal retailPrice)
    {
        await _localDb.Insertable(new StoreRetailPrice
        {
            UUID = $"{storeCode}-{productCode}",
            StoreCode = storeCode,
            ProductCode = productCode,
            StoreProductCode = $"{storeCode}{productCode}",
            SupplierCode = "SUP01",
            PurchasePrice = purchasePrice,
            StoreRetailPriceValue = retailPrice,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
    }

    private async Task SeedStoreMultiCodeProjectionsForAllActiveRelationsAsync(
        string productCode
    )
    {
        var stores = await _localDb.Queryable<Store>()
            .Where(store => store.IsActive && !store.IsDeleted && store.StoreCode != null)
            .Select(store => store.StoreCode)
            .ToListAsync();
        var relations = await _localDb.Queryable<ProductSetCode>()
            .Where(relation =>
                relation.ProductCode == productCode
                && (relation.SetType == 1 || relation.SetType == 2)
                && relation.IsActive
                && !relation.IsDeleted
                && relation.SetProductCode != null
                && relation.SetProductCode != string.Empty
            )
            .ToListAsync();
        var rows = stores.SelectMany(storeCode => relations.Select(relation =>
            new StoreMultiCodeProduct
            {
                UUID = $"{storeCode}-{relation.SetCodeId}",
                StoreCode = storeCode,
                ProductCode = productCode,
                MultiCodeProductCode = relation.SetProductCode,
                StoreMultiCodeProductCode = $"{storeCode}{relation.SetProductCode}",
                MultiBarcode = relation.SetBarcode,
                PurchasePrice = 99m,
                MultiCodeRetailPrice = relation.SetRetailPrice,
                IsActive = true,
                IsDeleted = false,
            }
        )).ToList();
        if (rows.Count > 0)
        {
            await _localDb.Insertable(rows).ExecuteCommandAsync();
        }
    }

    private async Task SeedHqProductAsync(
        string productCode,
        decimal purchasePrice,
        decimal retailPrice,
        string itemNumber = "ITEM-OLD",
        string barcode = "930000000000",
        int id = 1
    )
    {
        await _hqDb.Insertable(new DIC_商品信息字典表
        {
            ID = id,
            HGUID = $"hq-product-{productCode}",
            H商品标签GUID = string.Empty,
            H商品分类码GUID = string.Empty,
            H供货商编码 = "SUP01",
            H商品编码 = productCode,
            H货号 = itemNumber,
            H主条形码 = barcode,
            H商品名称 = "旧商品",
            H大写名称 = "旧商品",
            H规格 = string.Empty,
            H单位 = string.Empty,
            H进货价 = purchasePrice,
            H零售价 = retailPrice,
            H商品图片 = string.Empty,
            H腾讯云图地址 = string.Empty,
            H使用状态 = true,
            H进货单主表GUID = string.Empty,
            H进货单详情GUID = string.Empty,
            CBP商品中文名称 = string.Empty,
            CBP供应商编码 = string.Empty,
            CBP商品分类码GUID = string.Empty,
            FGC_Creator = "seed",
            FGC_CreateDate = DateTime.UtcNow,
            FGC_LastModifier = "seed",
            FGC_LastModifyDate = DateTime.UtcNow,
            FGC_UpdateHelp = string.Empty,
        }).ExecuteCommandAsync();
    }

    private async Task SeedHqPriceAsync(string storeCode, string productCode, decimal purchasePrice, decimal retailPrice)
    {
        await _hqDb.Insertable(new DIC_商品零售价表
        {
            ID = storeCode == "S01" ? 1 : 2,
            HGUID = $"hq-price-{storeCode}-{productCode}",
            H分店代码 = storeCode,
            H商品编码 = productCode,
            H分店商品编码 = $"{storeCode}{productCode}",
            H供应商编码 = "SUP01",
            H分店供应商编码 = $"{storeCode}SUP01",
            H进货价 = purchasePrice,
            H分店零售价 = retailPrice,
            H使用状态 = true,
            FGC_Creator = "seed",
            FGC_CreateDate = DateTime.UtcNow,
            FGC_LastModifier = "seed",
            FGC_LastModifyDate = DateTime.UtcNow,
        }).ExecuteCommandAsync();
    }

    private static SqlSugarClient CreateSqlSugarClient(string connectionString)
    {
        return new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
    }

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
        var context = (HqSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HqSqlSugarContext));
        typeof(HqSqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    private static void AssertRequiredProperty<T>(string propertyName)
    {
        var property = typeof(T).GetProperty(propertyName);
        Assert.NotNull(property);
        Assert.NotNull(property!.GetCustomAttribute<RequiredAttribute>());
    }

    private static bool IsTableWriteCommand(string sql, string tableName)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return false;

        var trimmed = sql.AsSpan().TrimStart();
        var isInsert = trimmed.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase);
        var isUpdate = trimmed.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase);
        if (!isInsert && !isUpdate)
            return false;

        // SQLite 下 SqlSugar 用双引号包裹表名；同时兼容未加引号形式。
        return sql.Contains($"\"{tableName}\"", StringComparison.Ordinal)
            || sql.Contains(tableName, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _localDb.Dispose();
        _hqDb.Dispose();
        _localConnection.Dispose();
        _hqConnection.Dispose();
        if (File.Exists(_localDbPath)) SqliteTempFileCleanup.DeleteIfExists(_localDbPath);
        if (File.Exists(_hqDbPath)) SqliteTempFileCleanup.DeleteIfExists(_hqDbPath);
    }
}
