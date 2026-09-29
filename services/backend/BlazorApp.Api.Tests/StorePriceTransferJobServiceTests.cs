using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HqEntities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class StorePriceTransferJobServiceTests
{
    private const string SqlServerTestConnectionEnvVar = "STORE_PRICE_TRANSFER_SQLSERVER_TEST_CONNECTION";

    [Fact]
    public async Task TransferAsync_HqToLocal_同步价格和多码并按目标分店新增缺失行()
    {
        await using var localConnection = new SqliteConnection($"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await localConnection.OpenAsync();
        await using var hqConnection = new SqliteConnection($"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await hqConnection.OpenAsync();
        using var localDb = new SqlSugarClient(CreateConnectionConfig(localConnection.ConnectionString));
        using var hqDb = new SqlSugarClient(CreateConnectionConfig(hqConnection.ConnectionString));
        InitLocalTables(localDb);
        InitHqTables(hqDb);
        await localDb.Insertable(new[]
        {
            new Product
            {
                UUID = "product-p01",
                ProductCode = "P01",
                ProductName = "套装 P01",
                PurchasePrice = 1m,
                IsActive = true,
                IsDeleted = false,
            },
            new Product
            {
                UUID = "product-p02",
                ProductCode = "P02",
                ProductName = "固定套装 P02",
                PurchasePrice = 1m,
                IsActive = true,
                IsDeleted = false,
            },
        }).ExecuteCommandAsync();
        await localDb.Insertable(new[]
        {
            new ProductSetCode
            {
                SetCodeId = "set-p01-m01",
                ProductCode = "P01",
                SetProductCode = "M01",
                SetItemNumber = "ITEM-M01",
                SetBarcode = "BAR-M01",
                SetPurchasePrice = 99m,
                SetRetailPrice = 7m,
                SetQuantity = 1,
                SetType = 1,
                IsActive = true,
                IsDeleted = false,
            },
            new ProductSetCode
            {
                SetCodeId = "set-p02-m02",
                ProductCode = "P02",
                SetProductCode = "M02",
                SetItemNumber = "ITEM-M02",
                SetBarcode = "BAR-M02",
                SetPurchasePrice = 99m,
                SetRetailPrice = 0m,
                SetQuantity = 1,
                SetType = 2,
                IsActive = true,
                IsDeleted = false,
            },
        }).ExecuteCommandAsync();
        await localDb.Insertable(new StoreRetailPrice
        {
            UUID = "local-retail-target",
            StoreCode = "T01",
            ProductCode = "P01",
            StoreProductCode = "T01P01",
            SupplierCode = "OLD",
            PurchasePrice = 1m,
            StoreRetailPriceValue = 2m,
            DiscountRate = 0.1m,
            IsAutoPricing = false,
            IsSpecialProduct = false,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await localDb.Insertable(BuildLocalRetail(
            "local-retail-rollback",
            "T01",
            "P-ROLLBACK",
            5m,
            9m,
            0m,
            false,
            false
        )).ExecuteCommandAsync();
        await localDb.Insertable(BuildLocalMulti(
            "local-multi-target",
            "T01",
            "P01",
            "M01",
            1m,
            2m,
            0.1m,
            false,
            false
        )).ExecuteCommandAsync();
        await hqDb.Insertable(new[]
        {
            BuildHqRetail(1, "S01", "P01", 5m, 9m, 0.25m, true, true),
            BuildHqRetail(2, "S01", "P02", 6m, 10m, 0.35m, true, false),
        }).ExecuteCommandAsync();
        await hqDb.Insertable(new[]
        {
            BuildHqMulti(1, "S01", "P01", "M01", 3m, 7m, 0.15m, true, false),
            BuildHqMulti(2, "S01", "P02", "M02", 4m, 8m, 0.20m, false, true),
        }).ExecuteCommandAsync();
        var service = CreateTransferService(localDb, hqDb);
        var progressSnapshots = new List<StorePriceTransferResult>();

        var result = await service.TransferAsync(new StorePriceTransferRequest
        {
            Direction = StorePriceTransferDirectionConstants.HqToLocal,
            SourceStoreCode = "S01",
            TargetStoreCode = "T01",
            SyncRetailPrices = true,
            SyncMultiCodePrices = true,
            SyncPurchasePrice = true,
            SyncRetailPrice = true,
            SyncDiscountRate = true,
            SyncIsAutoPricing = true,
            SyncIsSpecialProduct = true,
        }, "tester", progressSnapshots.Add);

        Assert.True(result.Success, result.Message);
        Assert.Equal(4, result.Data!.TotalCount);
        Assert.Equal(2, result.Data.RetailPriceTotal);
        Assert.Equal(2, result.Data.MultiCodeTotal);
        Assert.Contains(progressSnapshots, snapshot => snapshot.TotalCount == 4);
        Assert.Contains(progressSnapshots, snapshot => snapshot.TotalProcessed == 4);
        Assert.Equal(4, result.Data.TotalProcessed);
        Assert.Equal(2, result.Data.InsertedCount);
        Assert.Equal(2, result.Data.UpdatedCount);
        Assert.Equal(1, result.Data.RetailPriceInserted);
        Assert.Equal(1, result.Data.RetailPriceUpdated);
        Assert.Equal(1, result.Data.MultiCodeInserted);
        Assert.Equal(1, result.Data.MultiCodeUpdated);

        var updatedRetail = await localDb.Queryable<StoreRetailPrice>()
            .SingleAsync(x => x.StoreCode == "T01" && x.ProductCode == "P01");
        Assert.Equal(5m, updatedRetail.PurchasePrice);
        Assert.Equal(9m, updatedRetail.StoreRetailPriceValue);
        Assert.Equal(0.25m, updatedRetail.DiscountRate);
        Assert.True(updatedRetail.IsAutoPricing);
        Assert.True(updatedRetail.IsSpecialProduct);

        var updatedMulti = await localDb.Queryable<StoreMultiCodeProduct>()
            .SingleAsync(x => x.StoreCode == "T01" && x.ProductCode == "P01");
        Assert.Equal(5m, updatedMulti.PurchasePrice);
        Assert.Equal(7m, updatedMulti.MultiCodeRetailPrice);
        var unchangedGlobalSet = await localDb.Queryable<ProductSetCode>()
            .SingleAsync(x => x.SetCodeId == "set-p01-m01");
        Assert.Equal(99m, unchangedGlobalSet.SetPurchasePrice);

        var insertedRetail = await localDb.Queryable<StoreRetailPrice>()
            .SingleAsync(x => x.StoreCode == "T01" && x.ProductCode == "P02");
        Assert.Equal("T01P02", insertedRetail.StoreProductCode);
        Assert.Equal(6m, insertedRetail.PurchasePrice);
        Assert.Equal(10m, insertedRetail.StoreRetailPriceValue);

        var insertedMulti = await localDb.Queryable<StoreMultiCodeProduct>()
            .SingleAsync(x => x.StoreCode == "T01" && x.ProductCode == "P02" && x.MultiCodeProductCode == "M02");
        Assert.Equal("T01M02", insertedMulti.StoreMultiCodeProductCode);
        Assert.Equal(6m, insertedMulti.PurchasePrice);
        Assert.Equal(8m, insertedMulti.MultiCodeRetailPrice);
        Assert.True(insertedMulti.IsSpecialProduct);
    }

    [Fact]
    public void 总部同步到门店_两类套装均按完整父子键忽略源成本()
    {
        var source = File.ReadAllText(ResolveStorePriceTransferServicePath());

        Assert.True(
            source.Split("SetType == 1 ||").Length - 1 >= 2,
            "HQ→Local 的独立多码与父价+多码组合路径都必须覆盖 Type1/Type2"
        );
        Assert.Contains("batchChildCodes.Contains(x.SetProductCode)", source);
        Assert.Contains("setChildRequest = WithoutPurchasePrice(request)", source);
    }

    [Fact]
    public async Task TransferAsync_HqToLocal_套装目标组无法重算时应回滚当前多码写批次()
    {
        await using var localConnection = new SqliteConnection($"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await localConnection.OpenAsync();
        await using var hqConnection = new SqliteConnection($"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await hqConnection.OpenAsync();
        using var localDb = new SqlSugarClient(CreateConnectionConfig(localConnection.ConnectionString));
        using var hqDb = new SqlSugarClient(CreateConnectionConfig(hqConnection.ConnectionString));
        InitLocalTables(localDb);
        InitHqTables(hqDb);

        await localDb.Insertable(new Product
        {
            UUID = "product-rollback",
            ProductCode = "P-ROLLBACK",
            PurchasePrice = 5m,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await localDb.Insertable(new ProductSetCode
        {
            SetCodeId = "set-rollback",
            ProductCode = "P-ROLLBACK",
            SetProductCode = "M-ROLLBACK",
            SetItemNumber = "ITEM-ROLLBACK",
            SetBarcode = "BAR-ROLLBACK",
            SetRetailPrice = 0m,
            SetQuantity = 1,
            SetType = 1,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await localDb.Insertable(BuildLocalMulti(
            "local-multi-rollback",
            "T01",
            "P-ROLLBACK",
            "M-ROLLBACK",
            1m,
            2m,
            0m,
            false,
            false
        )).ExecuteCommandAsync();
        await hqDb.Insertable(BuildHqMulti(
            1,
            "S01",
            "P-ROLLBACK",
            "M-ROLLBACK",
            99m,
            0m,
            0m,
            false,
            false
        )).ExecuteCommandAsync();

        var result = await CreateTransferService(localDb, hqDb).TransferAsync(new StorePriceTransferRequest
        {
            Direction = StorePriceTransferDirectionConstants.HqToLocal,
            SourceStoreCode = "S01",
            TargetStoreCode = "T01",
            SyncRetailPrices = false,
            SyncMultiCodePrices = true,
            SyncPurchasePrice = true,
            SyncRetailPrice = true,
        }, "tester");

        Assert.False(result.Success);
        var localMulti = await localDb.Queryable<StoreMultiCodeProduct>()
            .SingleAsync(x => x.UUID == "local-multi-rollback");
        Assert.Equal(1m, localMulti.PurchasePrice);
        Assert.Equal(2m, localMulti.MultiCodeRetailPrice);
    }

    [Fact]
    public async Task TransferAsync_HqToLocal_同时同步时HQ同键多码不得绕过软删除墓碑()
    {
        await using var localConnection = new SqliteConnection($"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await localConnection.OpenAsync();
        await using var hqConnection = new SqliteConnection($"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await hqConnection.OpenAsync();
        using var localDb = new SqlSugarClient(CreateConnectionConfig(localConnection.ConnectionString));
        using var hqDb = new SqlSugarClient(CreateConnectionConfig(hqConnection.ConnectionString));
        InitLocalTables(localDb);
        InitHqTables(hqDb);

        await localDb.Insertable(new Product
        {
            UUID = "product-atomic",
            ProductCode = "P-ATOMIC",
            PurchasePrice = 5m,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await localDb.Insertable(new ProductSetCode
        {
            SetCodeId = "set-atomic",
            ProductCode = "P-ATOMIC",
            SetProductCode = "M-MISSING",
            SetItemNumber = "ITEM-MISSING",
            SetBarcode = "BAR-MISSING",
            SetRetailPrice = 0m,
            SetQuantity = 1,
            SetType = 2,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await localDb.Insertable(BuildLocalRetail(
            "local-retail-atomic",
            "T01",
            "P-ATOMIC",
            5m,
            9m,
            0m,
            false,
            false
        )).ExecuteCommandAsync();
        var deletedProjection = BuildLocalMulti(
            "local-multi-deleted",
            "T01",
            "P-ATOMIC",
            "M-MISSING",
            1m,
            2m,
            0m,
            false,
            false
        );
        deletedProjection.IsActive = false;
        deletedProjection.IsDeleted = true;
        await localDb.Insertable(deletedProjection).ExecuteCommandAsync();
        await hqDb.Insertable(BuildHqRetail(
            1,
            "S01",
            "P-ATOMIC",
            20m,
            30m,
            0m,
            false,
            false
        )).ExecuteCommandAsync();
        await hqDb.Insertable(BuildHqMulti(
            1,
            "S01",
            "P-ATOMIC",
            "M-MISSING",
            99m,
            40m,
            0m,
            false,
            false
        )).ExecuteCommandAsync();

        var result = await CreateTransferService(localDb, hqDb).TransferAsync(new StorePriceTransferRequest
        {
            Direction = StorePriceTransferDirectionConstants.HqToLocal,
            SourceStoreCode = "S01",
            TargetStoreCode = "T01",
            SyncRetailPrices = true,
            SyncMultiCodePrices = true,
            SyncPurchasePrice = true,
            SyncRetailPrice = true,
        }, "tester");

        Assert.False(result.Success);
        var localRetail = await localDb.Queryable<StoreRetailPrice>()
            .SingleAsync(x => x.UUID == "local-retail-atomic");
        Assert.Equal(5m, localRetail.PurchasePrice);
        Assert.Equal(9m, localRetail.StoreRetailPriceValue);
        var unchangedDeletedProjection = await localDb.Queryable<StoreMultiCodeProduct>()
            .SingleAsync(x => x.UUID == "local-multi-deleted");
        Assert.True(unchangedDeletedProjection.IsDeleted);
        Assert.False(unchangedDeletedProjection.IsActive);
        Assert.Equal(
            0,
            await localDb.Queryable<StoreMultiCodeProduct>()
                .Where(x =>
                    x.StoreCode == "T01"
                    && x.ProductCode == "P-ATOMIC"
                    && x.MultiCodeProductCode == "M-MISSING"
                    && !x.IsDeleted
                )
                .CountAsync()
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferAsync_HqToLocal_父价墓碑不得旁路新建有效行(
        bool syncMultiCodePrices
    )
    {
        await using var localConnection = new SqliteConnection($"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await localConnection.OpenAsync();
        await using var hqConnection = new SqliteConnection($"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await hqConnection.OpenAsync();
        using var localDb = new SqlSugarClient(CreateConnectionConfig(localConnection.ConnectionString));
        using var hqDb = new SqlSugarClient(CreateConnectionConfig(hqConnection.ConnectionString));
        InitLocalTables(localDb);
        InitHqTables(hqDb);
        await localDb.Insertable(new Product
        {
            UUID = "product-retail-tombstone",
            ProductCode = "P-RETAIL-TOMBSTONE",
            PurchasePrice = 5m,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        var tombstone = BuildLocalRetail(
            "retail-tombstone",
            "T01",
            "P-RETAIL-TOMBSTONE",
            5m,
            9m,
            0m,
            false,
            false
        );
        tombstone.IsDeleted = true;
        tombstone.IsActive = false;
        await localDb.Insertable(tombstone).ExecuteCommandAsync();
        await hqDb.Insertable(BuildHqRetail(
            1,
            "S01",
            "P-RETAIL-TOMBSTONE",
            20m,
            30m,
            0m,
            false,
            false
        )).ExecuteCommandAsync();

        var result = await CreateTransferService(localDb, hqDb).TransferAsync(
            new StorePriceTransferRequest
            {
                Direction = StorePriceTransferDirectionConstants.HqToLocal,
                SourceStoreCode = "S01",
                TargetStoreCode = "T01",
                SyncRetailPrices = true,
                SyncMultiCodePrices = syncMultiCodePrices,
                SyncPurchasePrice = true,
                SyncRetailPrice = true,
            },
            "tester"
        );

        Assert.False(result.Success);
        Assert.Equal(
            0,
            await localDb.Queryable<StoreRetailPrice>()
                .Where(x =>
                    x.StoreCode == "T01"
                    && x.ProductCode == "P-RETAIL-TOMBSTONE"
                    && !x.IsDeleted
                )
                .CountAsync()
        );
        var persisted = await localDb.Queryable<StoreRetailPrice>()
            .SingleAsync(x => x.UUID == "retail-tombstone");
        Assert.True(persisted.IsDeleted);
        Assert.Equal(5m, persisted.PurchasePrice);
        Assert.Equal(9m, persisted.StoreRetailPriceValue);
    }

    [Fact]
    public async Task TransferAsync_HqToLocal_独立多码同步不得绕过软删除墓碑()
    {
        await using var localConnection = new SqliteConnection($"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await localConnection.OpenAsync();
        await using var hqConnection = new SqliteConnection($"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await hqConnection.OpenAsync();
        using var localDb = new SqlSugarClient(CreateConnectionConfig(localConnection.ConnectionString));
        using var hqDb = new SqlSugarClient(CreateConnectionConfig(hqConnection.ConnectionString));
        InitLocalTables(localDb);
        InitHqTables(hqDb);
        var tombstone = BuildLocalMulti(
            "multi-tombstone",
            "T01",
            "P-MULTI-TOMBSTONE",
            "M-TOMBSTONE",
            5m,
            9m,
            0m,
            false,
            false
        );
        tombstone.IsDeleted = true;
        tombstone.IsActive = false;
        await localDb.Insertable(tombstone).ExecuteCommandAsync();
        await hqDb.Insertable(BuildHqMulti(
            1,
            "S01",
            "P-MULTI-TOMBSTONE",
            "M-TOMBSTONE",
            20m,
            30m,
            0m,
            false,
            false
        )).ExecuteCommandAsync();

        var result = await CreateTransferService(localDb, hqDb).TransferAsync(
            new StorePriceTransferRequest
            {
                Direction = StorePriceTransferDirectionConstants.HqToLocal,
                SourceStoreCode = "S01",
                TargetStoreCode = "T01",
                SyncRetailPrices = false,
                SyncMultiCodePrices = true,
                SyncPurchasePrice = true,
                SyncRetailPrice = true,
            },
            "tester"
        );

        Assert.False(result.Success);
        Assert.Equal(
            0,
            await localDb.Queryable<StoreMultiCodeProduct>()
                .Where(x =>
                    x.StoreCode == "T01"
                    && x.ProductCode == "P-MULTI-TOMBSTONE"
                    && x.MultiCodeProductCode == "M-TOMBSTONE"
                    && !x.IsDeleted
                )
                .CountAsync()
        );
        Assert.True(
            (await localDb.Queryable<StoreMultiCodeProduct>()
                .SingleAsync(x => x.UUID == "multi-tombstone"))
                .IsDeleted
        );
    }

    [Fact]
    public async Task TransferAsync_LocalToHq_同步价格和多码并只覆盖勾选字段()
    {
        await using var localConnection = new SqliteConnection($"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await localConnection.OpenAsync();
        await using var hqConnection = new SqliteConnection($"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await hqConnection.OpenAsync();
        using var localDb = new SqlSugarClient(CreateConnectionConfig(localConnection.ConnectionString));
        using var hqDb = new SqlSugarClient(CreateConnectionConfig(hqConnection.ConnectionString));
        InitLocalTables(localDb);
        InitHqTables(hqDb);
        await localDb.Insertable(new[]
        {
            BuildLocalRetail("local-retail-1", "S01", "P01", 11m, 21m, 0.44m, true, true),
            BuildLocalRetail("local-retail-2", "S01", "P02", 12m, 22m, 0.55m, false, false),
        }).ExecuteCommandAsync();
        await localDb.Insertable(new[]
        {
            BuildLocalMulti("local-multi-1", "S01", "P01", "M01", 31m, 41m, 0.66m, true, false),
            BuildLocalMulti("local-multi-2", "S01", "P02", "M02", 32m, 42m, 0.77m, false, true),
        }).ExecuteCommandAsync();
        await hqDb.Insertable(BuildHqRetail(1, "T01", "P01", 1m, 2m, 0.1m, false, false))
            .ExecuteCommandAsync();
        await hqDb.Insertable(BuildHqMulti(1, "T01", "P01", "M01", 3m, 4m, 0.2m, false, false))
            .ExecuteCommandAsync();
        var service = CreateTransferService(localDb, hqDb);

        var result = await service.TransferAsync(new StorePriceTransferRequest
        {
            Direction = StorePriceTransferDirectionConstants.LocalToHq,
            SourceStoreCode = "S01",
            TargetStoreCode = "T01",
            SyncRetailPrices = true,
            SyncMultiCodePrices = true,
            SyncPurchasePrice = true,
            SyncRetailPrice = false,
            SyncDiscountRate = false,
            SyncIsAutoPricing = true,
            SyncIsSpecialProduct = false,
        }, "tester");

        Assert.True(result.Success, result.Message);
        Assert.Equal(4, result.Data!.TotalProcessed);
        Assert.Equal(2, result.Data.InsertedCount);
        Assert.Equal(2, result.Data.UpdatedCount);

        var updatedRetail = await hqDb.Queryable<DIC_商品零售价表>()
            .SingleAsync(x => x.H分店代码 == "T01" && x.H商品编码 == "P01");
        Assert.Equal(11m, updatedRetail.H进货价);
        Assert.Equal(2m, updatedRetail.H分店零售价);
        Assert.Equal(0.1m, updatedRetail.H折扣率);
        Assert.True(updatedRetail.H是否自动定价);
        Assert.False(updatedRetail.H是否特殊商品);

        var insertedRetail = await hqDb.Queryable<DIC_商品零售价表>()
            .SingleAsync(x => x.H分店代码 == "T01" && x.H商品编码 == "P02");
        Assert.Equal("T01P02", insertedRetail.H分店商品编码);
        Assert.Equal(12m, insertedRetail.H进货价);
        Assert.Equal(0m, insertedRetail.H分店零售价);
        Assert.False(insertedRetail.H是否特殊商品);

        var updatedMulti = await hqDb.Queryable<DIC_分店一品多码表>()
            .SingleAsync(x => x.H分店代码 == "T01" && x.H商品编码 == "P01" && x.H多码商品编码 == "M01");
        Assert.Equal(31m, updatedMulti.H进货价);
        Assert.Equal(4m, updatedMulti.H一品多码零售价);
        Assert.True(updatedMulti.H是否自动定价);

        var insertedMulti = await hqDb.Queryable<DIC_分店一品多码表>()
            .SingleAsync(x => x.H分店代码 == "T01" && x.H商品编码 == "P02" && x.H多码商品编码 == "M02");
        Assert.Equal("T01M02", insertedMulti.H分店多码商品编码);
        Assert.Equal(32m, insertedMulti.H进货价);
        Assert.Equal(0m, insertedMulti.H一品多码零售价);
    }

    [Fact]
    public async Task TransferAsync_LocalToHq_Hq更新只写勾选字段和审计字段()
    {
        await using var localConnection = new SqliteConnection($"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await localConnection.OpenAsync();
        await using var hqConnection = new SqliteConnection($"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await hqConnection.OpenAsync();
        using var localDb = new SqlSugarClient(CreateConnectionConfig(localConnection.ConnectionString));
        using var hqDb = new SqlSugarClient(CreateConnectionConfig(hqConnection.ConnectionString));
        InitLocalTables(localDb);
        InitHqTables(hqDb);
        await localDb.Insertable(BuildLocalRetail("local-retail-1", "S01", "P01", 11m, 21m, 0.44m, true, true))
            .ExecuteCommandAsync();
        await localDb.Insertable(BuildLocalMulti("local-multi-1", "S01", "P01", "M01", 31m, 41m, 0.66m, true, false))
            .ExecuteCommandAsync();
        var hqRetail = BuildHqRetail(1, "T01", "P01", 1m, 2m, 0.1m, false, false);
        hqRetail.H库存 = 123m;
        hqRetail.H活动类型 = "PROMO";
        hqRetail.H动态销售额 = 456m;
        await hqDb.Insertable(hqRetail).ExecuteCommandAsync();
        var hqMulti = BuildHqMulti(1, "T01", "P01", "M01", 3m, 4m, 0.2m, false, false);
        hqMulti.H库存 = 789m;
        hqMulti.H活动类型 = "MULTI";
        hqMulti.H动态销售额 = 987m;
        await hqDb.Insertable(hqMulti).ExecuteCommandAsync();
        var executedSql = new List<string>();
        hqDb.Aop.OnLogExecuting = (sql, _) => executedSql.Add(sql);
        var service = CreateTransferService(localDb, hqDb);

        var result = await service.TransferAsync(new StorePriceTransferRequest
        {
            Direction = StorePriceTransferDirectionConstants.LocalToHq,
            SourceStoreCode = "S01",
            TargetStoreCode = "T01",
            SyncRetailPrices = true,
            SyncMultiCodePrices = true,
            SyncPurchasePrice = true,
            SyncRetailPrice = true,
        }, "tester");

        Assert.True(result.Success, result.Message);
        var updateSql = executedSql
            .Where(sql => sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Contains(updateSql, sql => sql.Contains("DIC_商品零售价表", StringComparison.Ordinal));
        Assert.Contains(updateSql, sql => sql.Contains("DIC_分店一品多码表", StringComparison.Ordinal));
        Assert.DoesNotContain(updateSql, sql => sql.Contains("H库存", StringComparison.Ordinal));
        Assert.DoesNotContain(updateSql, sql => sql.Contains("H活动类型", StringComparison.Ordinal));
        Assert.DoesNotContain(updateSql, sql => sql.Contains("H动态销售额", StringComparison.Ordinal));

        var updatedRetail = await hqDb.Queryable<DIC_商品零售价表>()
            .SingleAsync(x => x.H分店代码 == "T01" && x.H商品编码 == "P01");
        Assert.Equal(123m, updatedRetail.H库存);
        Assert.Equal("PROMO", updatedRetail.H活动类型);
        Assert.Equal(456m, updatedRetail.H动态销售额);
        var updatedMulti = await hqDb.Queryable<DIC_分店一品多码表>()
            .SingleAsync(x => x.H分店代码 == "T01" && x.H商品编码 == "P01" && x.H多码商品编码 == "M01");
        Assert.Equal(789m, updatedMulti.H库存);
        Assert.Equal("MULTI", updatedMulti.H活动类型);
        Assert.Equal(987m, updatedMulti.H动态销售额);
    }

    [Fact]
    public void StorePriceTransferService_HqInsert路径应忽略SqlServer自增ID()
    {
        var source = File.ReadAllText(ResolveStorePriceTransferServicePath());

        Assert.Contains("IgnoreColumns(row => row.ID)", source);
        Assert.DoesNotContain("INSERT ([ID]", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StorePriceTransferService_SqlServer快速路径应使用BulkCopy和Merge()
    {
        var source = File.ReadAllText(ResolveStorePriceTransferServicePath());

        Assert.Contains("SqlBulkCopy", source);
        Assert.Contains("MERGE", source);
        Assert.Contains("#StorePriceTransferRetail", source);
        Assert.Contains("#StorePriceTransferMulti", source);
        Assert.Contains("OUTPUT $action", source);
        Assert.DoesNotContain("WITH (NOLOCK)", source);
    }

    [Fact]
    public void 跨店复制_锁前仅缓存候选键且不使用源成本写入目标()
    {
        var source = File.ReadAllText(ResolveStoreProductPriceReactServicePath());

        Assert.Contains("Channel.CreateBounded<List<string>>", source);
        Assert.Contains("Channel.CreateBounded<List<StoreMultiCodeCopyCandidate>>", source);
        Assert.Contains("锁内重读源、目标与关系", source);
        Assert.Contains("RecalculateCopiedStoreCostsAsync", source);
        Assert.DoesNotContain(
            "PurchasePrice = dto.SyncPurchasePrice ? source.PurchasePrice : null",
            source
        );
        Assert.DoesNotContain(
            "source.PurchasePrice.HasValue && (dto.Mode == \"Overwrite\"",
            source
        );
    }

    [Fact]
    public void StorePriceTransferService_SqlServerStageSql字符串列应使用目标库排序规则()
    {
        var stageSqls = new[]
        {
            InvokeStageSql("BuildLocalRetailStageSql"),
            InvokeStageSql("BuildLocalMultiStageSql"),
            InvokeStageSql("BuildHqRetailStageSql"),
            InvokeStageSql("BuildHqMultiStageSql"),
        };

        foreach (var stageSql in stageSqls)
        {
            Assert.Contains("COLLATE DATABASE_DEFAULT", stageSql);
            Assert.DoesNotContain("nvarchar(50) NULL", stageSql);
            Assert.DoesNotContain("nvarchar(50) NOT NULL", stageSql);
            Assert.DoesNotContain("nvarchar(100) NULL", stageSql);
            Assert.DoesNotContain("nvarchar(100) NOT NULL", stageSql);
        }
    }

    [Fact]
    public void StorePriceTransferService_BulkMerge应先释放Reader再提交并保护Rollback异常()
    {
        var source = File.ReadAllText(ResolveStorePriceTransferServicePath());
        var readerIndex = source.IndexOf("using (var reader = await mergeCommand.ExecuteReaderAsync())", StringComparison.Ordinal);
        var commitIndex = source.IndexOf("transaction.Commit();", StringComparison.Ordinal);
        var returnIndex = source.IndexOf("return new StorePriceTransferWriteCounts", commitIndex, StringComparison.Ordinal);

        Assert.True(readerIndex >= 0, "BulkMergeAsync 应显式释放 reader");
        Assert.True(commitIndex > readerIndex, "BulkMergeAsync 应在 reader 释放后再提交事务");
        Assert.True(returnIndex > commitIndex, "BulkMergeAsync 应先提交事务再返回写入统计");
        Assert.Contains("rollback 失败不能盖住真正的 bulk merge 异常", source);
        Assert.Contains("catch (Exception rollbackEx)", source);
        Assert.DoesNotContain("using var reader = await mergeCommand.ExecuteReaderAsync();", source);
    }

    [Fact]
    public void StorePriceTransferService_SqlServerMergeSql应按勾选字段生成()
    {
        var request = new StorePriceTransferRequest
        {
            TargetStoreCode = "T01",
            SyncPurchasePrice = true,
            SyncRetailPrice = false,
            SyncDiscountRate = false,
            SyncIsAutoPricing = true,
            SyncIsSpecialProduct = false,
        };

        var localRetailSql = InvokeMergeSql("BuildLocalRetailMergeSql", request);
        Assert.Contains("MERGE [dbo].[StoreRetailPrice] WITH (HOLDLOCK)", localRetailSql);
        Assert.Contains("target.[StoreCode] = @TargetStoreCode AND target.[ProductCode] = source.[ProductCode]", localRetailSql);
        Assert.Contains("target.[PurchasePrice] = source.[PurchasePrice]", localRetailSql);
        Assert.Contains("target.[IsAutoPricing] = source.[IsAutoPricing]", localRetailSql);
        Assert.DoesNotContain("target.[StoreRetailPriceValue] = source.[StoreRetailPriceValue]", localRetailSql);
        Assert.Contains("OUTPUT $action INTO @actions", localRetailSql);

        var localMultiSql = InvokeMergeSql("BuildLocalMultiMergeSql", request);
        Assert.Contains("MERGE [dbo].[StoreMultiCodeProduct] WITH (HOLDLOCK)", localMultiSql);
        Assert.Contains("target.[MultiCodeProductCode] = source.[MultiCodeProductCode]", localMultiSql);
        Assert.Contains("target.[PurchasePrice] = source.[PurchasePrice]", localMultiSql);
        Assert.DoesNotContain("target.[MultiCodeRetailPrice] = source.[MultiCodeRetailPrice]", localMultiSql);

        var hqRetailSql = InvokeMergeSql("BuildHqRetailMergeSql", request);
        Assert.Contains("MERGE [dbo].[DIC_商品零售价表] WITH (HOLDLOCK)", hqRetailSql);
        Assert.Contains("target.[H分店代码] = @TargetStoreCode AND target.[H商品编码] = source.[H商品编码]", hqRetailSql);
        Assert.Contains("target.[H进货价] = source.[H进货价]", hqRetailSql);
        Assert.Contains("target.[H是否自动定价] = source.[H是否自动定价]", hqRetailSql);
        Assert.DoesNotContain("target.[H分店零售价] = source.[H分店零售价]", hqRetailSql);
        Assert.DoesNotContain("[ID]", hqRetailSql);

        var hqMultiSql = InvokeMergeSql("BuildHqMultiMergeSql", request);
        Assert.Contains("MERGE [dbo].[DIC_分店一品多码表] WITH (HOLDLOCK)", hqMultiSql);
        Assert.Contains("target.[H多码商品编码] = source.[H多码商品编码]", hqMultiSql);
        Assert.Contains("target.[H进货价] = source.[H进货价]", hqMultiSql);
        Assert.DoesNotContain("target.[H一品多码零售价] = source.[H一品多码零售价]", hqMultiSql);
        Assert.DoesNotContain("[ID]", hqMultiSql);
    }

    [Fact]
    public void StorePriceTransferService_SqlServer索引脚本应包含源分页和目标匹配索引()
    {
        var source = File.ReadAllText(ResolveStorePriceTransferServicePath());
        var indexScript = File.ReadAllText(ResolveStorePriceTransferIndexScriptPath());
        var rollbackScript = File.ReadAllText(ResolveStorePriceTransferIndexRollbackScriptPath());

        Assert.DoesNotContain("EnsureSqlServerIndexesAsync", source);
        Assert.DoesNotContain("CREATE INDEX [IX_SPT_", source);

        Assert.Contains("IX_SPT_HqRetail_SourceStoreStatusId", indexScript);
        Assert.Contains("IX_SPT_HqRetail_TargetStoreProduct", indexScript);
        Assert.Contains("IX_SPT_HqMulti_SourceStoreStatusId", indexScript);
        Assert.Contains("IX_SPT_HqMulti_TargetStoreProductMulti", indexScript);
        Assert.Contains("IX_SPT_LocalRetail_SourceCursor", indexScript);
        Assert.Contains("IX_SPT_LocalMulti_SourceCursor", indexScript);
        Assert.Contains("DROP INDEX [IX_SPT_HqRetail_SourceStoreStatusId]", rollbackScript);
        Assert.Contains("DROP INDEX [IX_SPT_LocalRetail_SourceCursor]", rollbackScript);
    }

    [Fact]
    [Trait("Category", "SQL")]
    public async Task TransferAsync_SqlServerBulkMerge_真实执行插入更新和跳过()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(SqlServerTestConnectionEnvVar);
        // SQL 分类必须由 weekly 专用数据库执行；缺少配置时失败，禁止产生“未运行但绿色”的结果。
        Assert.False(string.IsNullOrWhiteSpace(baseConnectionString));

        var databaseName = $"HbStorePriceTransfer_{Guid.NewGuid():N}";
        var masterConnectionString = BuildSqlServerConnectionString(baseConnectionString!, "master");
        var databaseConnectionString = BuildSqlServerConnectionString(baseConnectionString!, databaseName);
        await ExecuteSqlServerAsync(masterConnectionString, $"CREATE DATABASE {QuoteSqlServerName(databaseName)};");

        try
        {
            using var localDb = new SqlSugarClient(CreateSqlServerConnectionConfig(databaseConnectionString));
            using var hqDb = new SqlSugarClient(CreateSqlServerConnectionConfig(databaseConnectionString));
            InitLocalTables(localDb);
            await InitSqlServerHqTablesAsync(hqDb);
            var service = CreateTransferService(localDb, hqDb, readBatchSize: 10, writeBatchSize: 10);

            await SeedSqlServerHqToLocalAsync(localDb, hqDb);
            var hqToLocal = await service.TransferAsync(new StorePriceTransferRequest
            {
                Direction = StorePriceTransferDirectionConstants.HqToLocal,
                SourceStoreCode = "SQL_HQ_SRC",
                TargetStoreCode = "SQL_LOCAL_TGT",
                SyncRetailPrices = true,
                SyncMultiCodePrices = true,
                SyncPurchasePrice = true,
                SyncRetailPrice = true,
                SyncDiscountRate = true,
                SyncIsAutoPricing = true,
                SyncIsSpecialProduct = true,
            }, "sql-tester");

            Assert.True(hqToLocal.Success, hqToLocal.Message);
            Assert.Equal(6, hqToLocal.Data!.TotalCount);
            Assert.Equal(2, hqToLocal.Data.InsertedCount);
            Assert.Equal(2, hqToLocal.Data.UpdatedCount);
            Assert.Equal(2, hqToLocal.Data.SkippedCount);
            var insertedLocalRetail = await localDb.Queryable<StoreRetailPrice>()
                .SingleAsync(row => row.StoreCode == "SQL_LOCAL_TGT" && row.ProductCode == "HQ_R_INSERT");
            Assert.Equal("SQL_LOCAL_TGTHQ_R_INSERT", insertedLocalRetail.StoreProductCode);
            Assert.Equal(13m, insertedLocalRetail.PurchasePrice);
            var updatedLocalMulti = await localDb.Queryable<StoreMultiCodeProduct>()
                .SingleAsync(row => row.StoreCode == "SQL_LOCAL_TGT" && row.ProductCode == "HQ_M_UPDATE" && row.MultiCodeProductCode == "M_UPDATE");
            Assert.Equal(23m, updatedLocalMulti.PurchasePrice);
            Assert.Equal(33m, updatedLocalMulti.MultiCodeRetailPrice);

            await SeedSqlServerLocalToHqAsync(localDb, hqDb);
            var localToHq = await service.TransferAsync(new StorePriceTransferRequest
            {
                Direction = StorePriceTransferDirectionConstants.LocalToHq,
                SourceStoreCode = "SQL_LOCAL_SRC",
                TargetStoreCode = "SQL_HQ_TGT",
                SyncRetailPrices = true,
                SyncMultiCodePrices = true,
                SyncPurchasePrice = true,
                SyncRetailPrice = true,
                SyncDiscountRate = true,
                SyncIsAutoPricing = true,
                SyncIsSpecialProduct = true,
            }, "sql-tester");

            Assert.True(localToHq.Success, localToHq.Message);
            Assert.Equal(6, localToHq.Data!.TotalCount);
            Assert.Equal(2, localToHq.Data.InsertedCount);
            Assert.Equal(2, localToHq.Data.UpdatedCount);
            Assert.Equal(2, localToHq.Data.SkippedCount);
            var insertedHqRetail = await hqDb.Queryable<DIC_商品零售价表>()
                .SingleAsync(row => row.H分店代码 == "SQL_HQ_TGT" && row.H商品编码 == "LOCAL_R_INSERT");
            Assert.True(insertedHqRetail.ID > 0);
            Assert.Equal(43m, insertedHqRetail.H进货价);
            var updatedHqMulti = await hqDb.Queryable<DIC_分店一品多码表>()
                .SingleAsync(row => row.H分店代码 == "SQL_HQ_TGT" && row.H商品编码 == "LOCAL_M_UPDATE" && row.H多码商品编码 == "LM_UPDATE");
            Assert.Equal(53m, updatedHqMulti.H进货价);
            Assert.Equal(63m, updatedHqMulti.H一品多码零售价);
        }
        finally
        {
            await DropSqlServerDatabaseAsync(masterConnectionString, databaseName);
        }
    }

    [Theory]
    [InlineData("", "T01", true, true, true)]
    [InlineData("S01", "", true, true, true)]
    [InlineData("S01", "T01", false, false, true)]
    [InlineData("S01", "T01", true, true, false)]
    public async Task TransferAsync_非法请求返回校验错误(
        string sourceStoreCode,
        string targetStoreCode,
        bool syncRetailPrices,
        bool syncMultiCodePrices,
        bool syncAnyField
    )
    {
        await using var localConnection = new SqliteConnection($"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await localConnection.OpenAsync();
        await using var hqConnection = new SqliteConnection($"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await hqConnection.OpenAsync();
        using var localDb = new SqlSugarClient(CreateConnectionConfig(localConnection.ConnectionString));
        using var hqDb = new SqlSugarClient(CreateConnectionConfig(hqConnection.ConnectionString));
        InitLocalTables(localDb);
        InitHqTables(hqDb);
        var service = CreateTransferService(localDb, hqDb);

        var result = await service.TransferAsync(new StorePriceTransferRequest
        {
            Direction = StorePriceTransferDirectionConstants.HqToLocal,
            SourceStoreCode = sourceStoreCode,
            TargetStoreCode = targetStoreCode,
            SyncRetailPrices = syncRetailPrices,
            SyncMultiCodePrices = syncMultiCodePrices,
            SyncPurchasePrice = syncAnyField,
        }, "tester");

        Assert.False(result.Success);
        Assert.Equal("VALIDATION_ERROR", result.ErrorCode);
    }

    [Fact]
    public async Task TransferAsync_HqToLocal_同名源目标分店允许同步()
    {
        await using var localConnection = new SqliteConnection($"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await localConnection.OpenAsync();
        await using var hqConnection = new SqliteConnection($"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await hqConnection.OpenAsync();
        using var localDb = new SqlSugarClient(CreateConnectionConfig(localConnection.ConnectionString));
        using var hqDb = new SqlSugarClient(CreateConnectionConfig(hqConnection.ConnectionString));
        InitLocalTables(localDb);
        InitHqTables(hqDb);
        await hqDb.Insertable(BuildHqRetail(1, "S01", "P01", 5m, 9m, 0.25m, true, false))
            .ExecuteCommandAsync();
        var service = CreateTransferService(localDb, hqDb);

        var result = await service.TransferAsync(new StorePriceTransferRequest
        {
            Direction = StorePriceTransferDirectionConstants.HqToLocal,
            SourceStoreCode = "S01",
            TargetStoreCode = "S01",
            SyncRetailPrices = true,
            SyncMultiCodePrices = false,
            SyncPurchasePrice = true,
            SyncRetailPrice = true,
        }, "tester");

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, result.Data!.TotalProcessed);
        Assert.Equal(1, result.Data.RetailPriceInserted);
        var insertedRetail = await localDb.Queryable<StoreRetailPrice>()
            .SingleAsync(x => x.StoreCode == "S01" && x.ProductCode == "P01");
        Assert.Equal("S01P01", insertedRetail.StoreProductCode);
        Assert.Equal(5m, insertedRetail.PurchasePrice);
        Assert.Equal(9m, insertedRetail.StoreRetailPriceValue);
    }

    [Fact]
    public async Task StartJobAsync_相同方向目标运行中时复用同一个任务()
    {
        var release = new TaskCompletionSource<ApiResponse<StorePriceTransferResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var transferService = new Mock<IStorePriceTransferService>();
        transferService
            .Setup(service => service.TransferAsync(
                It.IsAny<StorePriceTransferRequest>(),
                It.IsAny<string>(),
                It.IsAny<Action<StorePriceTransferResult>?>()
            ))
            .Returns(release.Task);
        var service = CreateJobService(transferService);

        var first = await service.StartJobAsync(BuildRequest(sourceStoreCode: "S01", targetStoreCode: "T01"), "admin");
        var duplicate = await service.StartJobAsync(
            BuildRequest(sourceStoreCode: "S02", targetStoreCode: "T01", syncRetailPrice: false),
            "admin"
        );

        Assert.Equal(first.JobId, duplicate.JobId);
        Assert.True(duplicate.IsDuplicateRequest);

        release.SetResult(ApiResponse<StorePriceTransferResult>.OK(new StorePriceTransferResult { TotalProcessed = 1 }));
        var completed = await WaitForJobAsync(service, first.JobId);
        Assert.Equal(StorePriceTransferJobStatusConstants.Succeeded, completed.Status);
        transferService.Verify(service => service.TransferAsync(
            It.IsAny<StorePriceTransferRequest>(),
            "admin",
            It.IsAny<Action<StorePriceTransferResult>?>()
        ), Times.Once);
    }

    [Fact]
    public async Task StartJobAsync_不同目标分店允许并行创建任务()
    {
        var release = new TaskCompletionSource<ApiResponse<StorePriceTransferResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var transferService = new Mock<IStorePriceTransferService>();
        transferService
            .Setup(service => service.TransferAsync(
                It.IsAny<StorePriceTransferRequest>(),
                It.IsAny<string>(),
                It.IsAny<Action<StorePriceTransferResult>?>()
            ))
            .Returns(release.Task);
        var service = CreateJobService(transferService);

        var first = await service.StartJobAsync(BuildRequest(targetStoreCode: "T01"), "admin");
        var second = await service.StartJobAsync(BuildRequest(targetStoreCode: "T02"), "admin");

        Assert.NotEqual(first.JobId, second.JobId);
        Assert.False(second.IsDuplicateRequest);

        release.SetResult(ApiResponse<StorePriceTransferResult>.OK(new StorePriceTransferResult { TotalProcessed = 1 }));
        Assert.Equal(StorePriceTransferJobStatusConstants.Succeeded, (await WaitForJobAsync(service, first.JobId)).Status);
        Assert.Equal(StorePriceTransferJobStatusConstants.Succeeded, (await WaitForJobAsync(service, second.JobId)).Status);
        transferService.Verify(service => service.TransferAsync(
            It.IsAny<StorePriceTransferRequest>(),
            "admin",
            It.IsAny<Action<StorePriceTransferResult>?>()
        ), Times.Exactly(2));
    }

    [Fact]
    public async Task StartJobAsync_运行中查询返回中间进度快照()
    {
        var release = new TaskCompletionSource<ApiResponse<StorePriceTransferResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var transferService = new Mock<IStorePriceTransferService>();
        transferService
            .Setup(service => service.TransferAsync(
                It.IsAny<StorePriceTransferRequest>(),
                It.IsAny<string>(),
                It.IsAny<Action<StorePriceTransferResult>?>()
            ))
            .Returns<StorePriceTransferRequest, string, Action<StorePriceTransferResult>?>((_, _, progress) =>
            {
                progress?.Invoke(new StorePriceTransferResult
                {
                    TotalCount = 10,
                    RetailPriceTotal = 6,
                    MultiCodeTotal = 4,
                    TotalProcessed = 4,
                    SkippedCount = 1,
                    InsertedCount = 3,
                    UpdatedCount = 1,
                });
                return release.Task;
            });
        var service = CreateJobService(transferService);

        var started = await service.StartJobAsync(BuildRequest(), "admin");
        var running = await WaitForRunningProgressAsync(service, started.JobId);

        Assert.Equal(StorePriceTransferJobStatusConstants.Running, running.Status);
        Assert.Equal(10, running.Result!.TotalCount);
        Assert.Equal(5, running.Result.TotalProcessed + running.Result.SkippedCount);
        Assert.Contains("5/10", running.Message);

        release.SetResult(ApiResponse<StorePriceTransferResult>.OK(new StorePriceTransferResult
        {
            TotalCount = 10,
            TotalProcessed = 9,
            SkippedCount = 1,
        }));
        Assert.Equal(StorePriceTransferJobStatusConstants.Succeeded, (await WaitForJobAsync(service, started.JobId)).Status);
    }

    [Fact]
    public async Task TransferAsync_失败且已有提交时返回部分提交提示()
    {
        await using var localConnection = new SqliteConnection($"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await localConnection.OpenAsync();
        await using var hqConnection = new SqliteConnection($"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await hqConnection.OpenAsync();
        using var localDb = new SqlSugarClient(CreateConnectionConfig(localConnection.ConnectionString));
        using var hqDb = new SqlSugarClient(CreateConnectionConfig(hqConnection.ConnectionString));
        InitLocalTables(localDb);
        InitHqTables(hqDb);
        localDb.Ado.ExecuteCommand(
            """
            CREATE TRIGGER StoreRetailPrice_Fail_P02
            BEFORE INSERT ON StoreRetailPrice
            WHEN NEW.ProductCode = 'P02'
            BEGIN
                SELECT RAISE(ABORT, 'P02 insert failed');
            END;
            """
        );
        await hqDb.Insertable(new[]
        {
            BuildHqRetail(1, "S01", "P01", 5m, 9m, 0.25m, true, true),
            BuildHqRetail(2, "S01", "P02", 6m, 10m, 0.35m, true, false),
        }).ExecuteCommandAsync();
        var service = CreateTransferService(localDb, hqDb);

        var result = await service.TransferAsync(new StorePriceTransferRequest
        {
            Direction = StorePriceTransferDirectionConstants.HqToLocal,
            SourceStoreCode = "S01",
            TargetStoreCode = "T01",
            SyncRetailPrices = true,
            SyncMultiCodePrices = false,
            SyncPurchasePrice = true,
            SyncRetailPrice = true,
        }, "tester");

        Assert.False(result.Success);
        var details = Assert.IsType<StorePriceTransferResult>(result.Details);
        Assert.Equal(1, details.TotalProcessed);
        Assert.Contains("已提交 1 条，已提交批次不会自动回滚", result.Message);
        Assert.Contains("已提交 1 条，已提交批次不会自动回滚", details.Errors.Single());
    }

    [Fact]
    public async Task ReactStoreProductPricesController_价格同步Job接口委托Job服务并要求管理员角色()
    {
        var startMethod = typeof(ReactStoreProductPricesController)
            .GetMethod(nameof(ReactStoreProductPricesController.StartStorePriceTransferJob))!;
        var startAuthorize = startMethod.GetCustomAttribute<AuthorizeAttribute>();
        Assert.Equal("Admin,管理员", startAuthorize?.Roles);

        var getMethod = typeof(ReactStoreProductPricesController)
            .GetMethod(nameof(ReactStoreProductPricesController.GetStorePriceTransferJob))!;
        var getAuthorize = getMethod.GetCustomAttribute<AuthorizeAttribute>();
        Assert.Equal("Admin,管理员", getAuthorize?.Roles);

        var jobService = new Mock<IStorePriceTransferJobService>(MockBehavior.Strict);
        jobService
            .Setup(service => service.StartJobAsync(
                It.Is<StorePriceTransferRequest>(request => request.SourceStoreCode == "S01"),
                "admin",
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(new StorePriceTransferJobDto
            {
                JobId = "job-1",
                Status = StorePriceTransferJobStatusConstants.Running,
            });
        jobService
            .Setup(service => service.GetJobAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorePriceTransferJobDto
            {
                JobId = "job-1",
                Status = StorePriceTransferJobStatusConstants.Succeeded,
                Result = new StorePriceTransferResult { TotalProcessed = 1 },
            });
        var controller = CreateController(jobService.Object);
        controller.ControllerContext.HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "admin") },
                    "test"
                )
            ),
        };

        var startResponse = await controller.StartStorePriceTransferJob(BuildRequest());
        var getResponse = await controller.GetStorePriceTransferJob("job-1");

        Assert.IsType<OkObjectResult>(startResponse);
        Assert.IsType<OkObjectResult>(getResponse);
        jobService.VerifyAll();
    }

    [Fact]
    public async Task ReactStoreProductPricesController_空请求直接返回BadRequest且不启动Job()
    {
        var jobService = new Mock<IStorePriceTransferJobService>(MockBehavior.Strict);
        var controller = CreateController(jobService.Object);

        var response = await controller.StartStorePriceTransferJob(null);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response);
        var body = Assert.IsType<ApiResponse<StorePriceTransferJobDto>>(badRequest.Value);
        Assert.False(body.Success);
        Assert.Equal("INVALID_REQUEST", body.ErrorCode);
        jobService.Verify(
            service => service.StartJobAsync(
                It.IsAny<StorePriceTransferRequest>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()
            ),
            Times.Never
        );
    }

    private static StorePriceTransferRequest BuildRequest(
        string sourceStoreCode = "S01",
        string targetStoreCode = "T01",
        bool syncRetailPrice = true
    ) => new()
    {
        Direction = StorePriceTransferDirectionConstants.HqToLocal,
        SourceStoreCode = sourceStoreCode,
        TargetStoreCode = targetStoreCode,
        SyncRetailPrices = true,
        SyncMultiCodePrices = true,
        SyncPurchasePrice = true,
        SyncRetailPrice = syncRetailPrice,
    };

    private static StorePriceTransferService CreateTransferService(
        ISqlSugarClient localDb,
        ISqlSugarClient hqDb,
        int readBatchSize = 1,
        int writeBatchSize = 1
    )
    {
        return new StorePriceTransferService(
            CreateSqlSugarContext(localDb),
            CreateHqSqlSugarContext(hqDb),
            Options.Create(new StoreRetailPriceHqSyncOptions
            {
                HqReadBatchSize = readBatchSize,
                WriteBatchSize = writeBatchSize,
            }),
            NullLogger<StorePriceTransferService>.Instance
        );
    }

    private static StorePriceTransferJobService CreateJobService(Mock<IStorePriceTransferService> transferService)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => transferService.Object);
        var provider = services.BuildServiceProvider();
        return new StorePriceTransferJobService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<StorePriceTransferJobService>.Instance
        );
    }

    private static ReactStoreProductPricesController CreateController(IStorePriceTransferJobService jobService)
    {
        return new ReactStoreProductPricesController(
            Mock.Of<IStoreProductPriceReactService>(),
            Mock.Of<IStoreRetailPriceReactService>(),
            Mock.Of<IUserService>(),
            Mock.Of<ILogger<ReactStoreProductPricesController>>(),
            jobService
        );
    }

    private static async Task<StorePriceTransferJobDto> WaitForJobAsync(
        StorePriceTransferJobService service,
        string jobId
    )
    {
        var job = await WaitForValueAsync(
            () => service.GetJobAsync(jobId),
            current => current is { Status: not StorePriceTransferJobStatusConstants.Running },
            describeLast: current => $"job 当前状态：{current?.Status ?? "未找到"}"
        );
        return job!;
    }

    private static async Task<StorePriceTransferJobDto> WaitForRunningProgressAsync(
        StorePriceTransferJobService service,
        string jobId
    )
    {
        var job = await WaitForValueAsync(
            () => service.GetJobAsync(jobId),
            current => current is { Status: StorePriceTransferJobStatusConstants.Running, Result: not null },
            describeLast: current =>
                $"job 当前状态：{current?.Status ?? "未找到"}，是否已发布进度：{current?.Result is not null}"
        );
        return job!;
    }

    private static void InitLocalTables(ISqlSugarClient db)
    {
        db.CodeFirst.InitTables(
            typeof(Product),
            typeof(WarehouseProduct),
            typeof(ProductSetCode),
            typeof(StoreRetailPrice),
            typeof(StoreMultiCodeProduct)
        );
    }

    private static void InitHqTables(ISqlSugarClient db)
    {
        db.CodeFirst.InitTables(typeof(DIC_商品零售价表), typeof(DIC_分店一品多码表));
    }

    private static StoreRetailPrice BuildLocalRetail(
        string uuid,
        string storeCode,
        string productCode,
        decimal purchasePrice,
        decimal retailPrice,
        decimal discountRate,
        bool isAutoPricing,
        bool isSpecialProduct
    ) => new()
    {
        UUID = uuid,
        StoreCode = storeCode,
        ProductCode = productCode,
        StoreProductCode = storeCode + productCode,
        SupplierCode = "200",
        PurchasePrice = purchasePrice,
        StoreRetailPriceValue = retailPrice,
        DiscountRate = discountRate,
        IsAutoPricing = isAutoPricing,
        IsSpecialProduct = isSpecialProduct,
        IsActive = true,
        IsDeleted = false,
    };

    private static StoreMultiCodeProduct BuildLocalMulti(
        string uuid,
        string storeCode,
        string productCode,
        string multiCode,
        decimal purchasePrice,
        decimal retailPrice,
        decimal discountRate,
        bool isAutoPricing,
        bool isSpecialProduct
    ) => new()
    {
        UUID = uuid,
        StoreCode = storeCode,
        ProductCode = productCode,
        MultiCodeProductCode = multiCode,
        StoreMultiCodeProductCode = storeCode + multiCode,
        MultiBarcode = "B" + multiCode,
        PurchasePrice = purchasePrice,
        MultiCodeRetailPrice = retailPrice,
        DiscountRate = discountRate,
        IsAutoPricing = isAutoPricing,
        IsSpecialProduct = isSpecialProduct,
        IsActive = true,
        IsDeleted = false,
    };

    private static DIC_商品零售价表 BuildHqRetail(
        int id,
        string storeCode,
        string productCode,
        decimal purchasePrice,
        decimal retailPrice,
        decimal discountRate,
        bool isAutoPricing,
        bool isSpecialProduct
    ) => new()
    {
        ID = id,
        HGUID = $"hq-retail-{storeCode}-{productCode}",
        H分店代码 = storeCode,
        H商品编码 = productCode,
        H分店商品编码 = storeCode + productCode,
        H供应商编码 = "200",
        H分店供应商编码 = storeCode + "200",
        H进货价 = purchasePrice,
        H分店零售价 = retailPrice,
        H折扣率 = discountRate,
        H使用状态 = true,
        H是否自动定价 = isAutoPricing,
        H是否特殊商品 = isSpecialProduct,
        FGC_Creator = "test",
        FGC_CreateDate = new DateTime(2026, 1, 1),
        FGC_LastModifier = "test",
        FGC_LastModifyDate = new DateTime(2026, 1, 2),
    };

    private static DIC_分店一品多码表 BuildHqMulti(
        int id,
        string storeCode,
        string productCode,
        string multiCode,
        decimal purchasePrice,
        decimal retailPrice,
        decimal discountRate,
        bool isAutoPricing,
        bool isSpecialProduct
    ) => new()
    {
        ID = id,
        HGUID = $"hq-multi-{storeCode}-{productCode}-{multiCode}",
        H分店代码 = storeCode,
        H商品编码 = productCode,
        H分店商品编码 = storeCode + productCode,
        H多码商品编码 = multiCode,
        H分店多码商品编码 = storeCode + multiCode,
        H供应商编码 = "200",
        H主条形码 = "B" + productCode,
        H多条形码 = "B" + multiCode,
        H进货价 = purchasePrice,
        H一品多码零售价 = retailPrice,
        H折扣率 = discountRate,
        H使用状态 = true,
        H是否自动定价 = isAutoPricing,
        H是否特殊商品 = isSpecialProduct,
        FGC_Creator = "test",
        FGC_CreateDate = new DateTime(2026, 1, 1),
        FGC_LastModifier = "test",
        FGC_LastModifyDate = new DateTime(2026, 1, 2),
    };

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

    private static ConnectionConfig CreateConnectionConfig(string connectionString)
    {
        return new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        };
    }

    private static ConnectionConfig CreateSqlServerConnectionConfig(string connectionString)
    {
        return new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = DbType.SqlServer,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        };
    }

    private static async Task SeedSqlServerHqToLocalAsync(ISqlSugarClient localDb, ISqlSugarClient hqDb)
    {
        await localDb.Insertable(new[]
        {
            BuildLocalRetail("sql-hq-local-retail-skip", "SQL_LOCAL_TGT", "HQ_R_SKIP", 11m, 21m, 0.11m, true, false),
            BuildLocalRetail("sql-hq-local-retail-update", "SQL_LOCAL_TGT", "HQ_R_UPDATE", 1m, 2m, 0.01m, false, false),
        }).ExecuteCommandAsync();
        await localDb.Insertable(new[]
        {
            BuildLocalMulti("sql-hq-local-multi-skip", "SQL_LOCAL_TGT", "HQ_M_SKIP", "M_SKIP", 21m, 31m, 0.21m, true, false),
            BuildLocalMulti("sql-hq-local-multi-update", "SQL_LOCAL_TGT", "HQ_M_UPDATE", "M_UPDATE", 1m, 2m, 0.01m, false, false),
        }).ExecuteCommandAsync();

        await InsertSqlServerHqRetailAsync(
            hqDb,
            BuildHqRetail(0, "SQL_HQ_SRC", "HQ_R_SKIP", 11m, 21m, 0.11m, true, false),
            BuildHqRetail(0, "SQL_HQ_SRC", "HQ_R_UPDATE", 12m, 22m, 0.12m, true, true),
            BuildHqRetail(0, "SQL_HQ_SRC", "HQ_R_INSERT", 13m, 23m, 0.13m, false, true)
        );
        await InsertSqlServerHqMultiAsync(
            hqDb,
            BuildHqMulti(0, "SQL_HQ_SRC", "HQ_M_SKIP", "M_SKIP", 21m, 31m, 0.21m, true, false),
            BuildHqMulti(0, "SQL_HQ_SRC", "HQ_M_UPDATE", "M_UPDATE", 23m, 33m, 0.23m, true, true),
            BuildHqMulti(0, "SQL_HQ_SRC", "HQ_M_INSERT", "M_INSERT", 24m, 34m, 0.24m, false, true)
        );
    }

    private static async Task SeedSqlServerLocalToHqAsync(ISqlSugarClient localDb, ISqlSugarClient hqDb)
    {
        await localDb.Insertable(new[]
        {
            BuildLocalRetail("sql-local-hq-retail-skip", "SQL_LOCAL_SRC", "LOCAL_R_SKIP", 41m, 51m, 0.41m, true, false),
            BuildLocalRetail("sql-local-hq-retail-update", "SQL_LOCAL_SRC", "LOCAL_R_UPDATE", 42m, 52m, 0.42m, true, true),
            BuildLocalRetail("sql-local-hq-retail-insert", "SQL_LOCAL_SRC", "LOCAL_R_INSERT", 43m, 53m, 0.43m, false, true),
        }).ExecuteCommandAsync();
        await localDb.Insertable(new[]
        {
            BuildLocalMulti("sql-local-hq-multi-skip", "SQL_LOCAL_SRC", "LOCAL_M_SKIP", "LM_SKIP", 51m, 61m, 0.51m, true, false),
            BuildLocalMulti("sql-local-hq-multi-update", "SQL_LOCAL_SRC", "LOCAL_M_UPDATE", "LM_UPDATE", 53m, 63m, 0.53m, true, true),
            BuildLocalMulti("sql-local-hq-multi-insert", "SQL_LOCAL_SRC", "LOCAL_M_INSERT", "LM_INSERT", 54m, 64m, 0.54m, false, true),
        }).ExecuteCommandAsync();

        await InsertSqlServerHqRetailAsync(
            hqDb,
            BuildHqRetail(0, "SQL_HQ_TGT", "LOCAL_R_SKIP", 41m, 51m, 0.41m, true, false),
            BuildHqRetail(0, "SQL_HQ_TGT", "LOCAL_R_UPDATE", 1m, 2m, 0.01m, false, false)
        );
        await InsertSqlServerHqMultiAsync(
            hqDb,
            BuildHqMulti(0, "SQL_HQ_TGT", "LOCAL_M_SKIP", "LM_SKIP", 51m, 61m, 0.51m, true, false),
            BuildHqMulti(0, "SQL_HQ_TGT", "LOCAL_M_UPDATE", "LM_UPDATE", 1m, 2m, 0.01m, false, false)
        );
    }

    private static Task<int> InsertSqlServerHqRetailAsync(ISqlSugarClient db, params DIC_商品零售价表[] rows)
    {
        return db.Insertable(rows).IgnoreColumns(row => row.ID).ExecuteCommandAsync();
    }

    private static Task<int> InsertSqlServerHqMultiAsync(ISqlSugarClient db, params DIC_分店一品多码表[] rows)
    {
        return db.Insertable(rows).IgnoreColumns(row => row.ID).ExecuteCommandAsync();
    }

    private static async Task InitSqlServerHqTablesAsync(ISqlSugarClient db)
    {
        await db.Ado.ExecuteCommandAsync(
            """
            CREATE TABLE [dbo].[DIC_商品零售价表] (
                [ID] int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                [HGUID] nvarchar(50) NULL,
                [H分店代码] nvarchar(50) NULL,
                [H商品编码] nvarchar(50) NULL,
                [H分店商品编码] nvarchar(50) NULL,
                [H供应商编码] nvarchar(50) NULL,
                [H分店供应商编码] nvarchar(50) NULL,
                [H进货价] decimal(18, 4) NOT NULL,
                [H分店零售价] decimal(18, 4) NOT NULL,
                [H库存] decimal(18, 4) NOT NULL,
                [H库存金额] decimal(18, 4) NOT NULL,
                [H库存预警数] decimal(18, 4) NOT NULL,
                [H商品缺货日期] datetime2 NOT NULL,
                [H是否缺货状态] bit NOT NULL,
                [H最小订货量] decimal(18, 4) NOT NULL,
                [H最小订货量合计金额] decimal(18, 4) NOT NULL,
                [H活动类型] nvarchar(100) NULL,
                [H满减活动代码] nvarchar(100) NULL,
                [H活动开始日期] datetime2 NOT NULL,
                [H活动结束日期] datetime2 NOT NULL,
                [H折扣率] decimal(18, 6) NOT NULL,
                [H满减数量] decimal(18, 4) NOT NULL,
                [H满减金额] decimal(18, 4) NOT NULL,
                [H多码数量] decimal(18, 4) NOT NULL,
                [H使用状态] bit NOT NULL,
                [H是否自动定价] bit NOT NULL,
                [H自动新价格] decimal(18, 4) NOT NULL,
                [H盘点入库记录数] decimal(18, 4) NOT NULL,
                [H是否特殊商品] bit NOT NULL,
                [H动态销售数量] decimal(18, 4) NOT NULL,
                [H动态销售额] decimal(18, 4) NOT NULL,
                [H动态成本] decimal(18, 4) NOT NULL,
                [H动态毛利] decimal(18, 4) NOT NULL,
                [H动态毛利率] decimal(18, 6) NOT NULL,
                [H动态销售占比] decimal(18, 6) NOT NULL,
                [FGC_Creator] nvarchar(100) NULL,
                [FGC_CreateDate] datetime2 NOT NULL,
                [FGC_LastModifier] nvarchar(100) NULL,
                [FGC_LastModifyDate] datetime2 NOT NULL
            );

            CREATE TABLE [dbo].[DIC_分店一品多码表] (
                [ID] int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                [HGUID] nvarchar(50) NULL,
                [H分店代码] nvarchar(50) NULL,
                [H商品编码] nvarchar(50) NULL,
                [H分店商品编码] nvarchar(50) NULL,
                [H多码商品编码] nvarchar(50) NULL,
                [H分店多码商品编码] nvarchar(50) NULL,
                [H供应商编码] nvarchar(50) NULL,
                [H主条形码] nvarchar(50) NULL,
                [H多条形码] nvarchar(50) NULL,
                [H进货价] decimal(18, 4) NULL,
                [H折扣率] decimal(18, 6) NULL,
                [H一品多码零售价] decimal(18, 4) NULL,
                [H库存] decimal(18, 4) NULL,
                [H库存金额] decimal(18, 4) NULL,
                [H自动新价格] decimal(18, 4) NULL,
                [H库存预警数] decimal(18, 4) NULL,
                [H商品缺货日期] datetime2 NULL,
                [H是否缺货状态] bit NULL,
                [H最小订货量] decimal(18, 4) NULL,
                [H最小订货量合计金额] decimal(18, 4) NULL,
                [H活动类型] nvarchar(100) NULL,
                [H满减活动代码] nvarchar(100) NULL,
                [H活动开始日期] datetime2 NULL,
                [H活动结束日期] datetime2 NULL,
                [H满减数量] decimal(18, 4) NULL,
                [H满减金额] decimal(18, 4) NULL,
                [H是否自动定价] bit NULL,
                [H是否特殊商品] bit NULL,
                [H商品柜组号] nvarchar(50) NULL,
                [H使用状态] bit NULL,
                [H动态销售数量] decimal(18, 4) NULL,
                [H动态销售额] decimal(18, 4) NULL,
                [H动态成本] decimal(18, 4) NULL,
                [H动态毛利] decimal(18, 4) NULL,
                [H动态毛利率] decimal(18, 6) NULL,
                [H动态销售占比] decimal(18, 6) NULL,
                [FGC_Creator] nvarchar(100) NULL,
                [FGC_CreateDate] datetime2 NULL,
                [FGC_LastModifier] nvarchar(100) NULL,
                [FGC_LastModifyDate] datetime2 NULL
            );
            """
        );
    }

    private static string BuildSqlServerConnectionString(string connectionString, string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = databaseName,
        };
        return builder.ConnectionString;
    }

    private static async Task ExecuteSqlServerAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropSqlServerDatabaseAsync(string masterConnectionString, string databaseName)
    {
        var quotedName = QuoteSqlServerName(databaseName);
        await ExecuteSqlServerAsync(
            masterConnectionString,
            $"""
            IF DB_ID(N'{databaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE {quotedName} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE {quotedName};
            END;
            """
        );
    }

    private static string QuoteSqlServerName(string name)
    {
        return $"[{name.Replace("]", "]]")}]";
    }

    private static string ResolveStorePriceTransferServicePath([CallerFilePath] string testFilePath = "")
    {
        var testDirectory = Path.GetDirectoryName(testFilePath)
            ?? throw new InvalidOperationException("无法解析测试文件目录");
        return Path.GetFullPath(
            Path.Combine(testDirectory, "..", "BlazorApp.Api", "Services", "React", "StorePriceTransferService.cs")
        );
    }

    private static string ResolveStoreProductPriceReactServicePath([CallerFilePath] string testFilePath = "")
    {
        var testDirectory = Path.GetDirectoryName(testFilePath)
            ?? throw new InvalidOperationException("无法解析测试文件目录");
        return Path.GetFullPath(
            Path.Combine(
                testDirectory,
                "..",
                "BlazorApp.Api",
                "Services",
                "React",
                "StoreProductPriceReactService.cs"
            )
        );
    }

    private static string ResolveStorePriceTransferIndexScriptPath([CallerFilePath] string testFilePath = "")
    {
        var testDirectory = Path.GetDirectoryName(testFilePath)
            ?? throw new InvalidOperationException("无法解析测试文件目录");
        return Path.GetFullPath(
            Path.Combine(testDirectory, "..", "SqlScripts", "StorePriceTransferPerformanceIndexes.sql")
        );
    }

    private static string ResolveStorePriceTransferIndexRollbackScriptPath([CallerFilePath] string testFilePath = "")
    {
        var testDirectory = Path.GetDirectoryName(testFilePath)
            ?? throw new InvalidOperationException("无法解析测试文件目录");
        return Path.GetFullPath(
            Path.Combine(testDirectory, "..", "SqlScripts", "StorePriceTransferPerformanceIndexes.Rollback.sql")
        );
    }

    private static string InvokeMergeSql(string methodName, StorePriceTransferRequest request)
    {
        var method = typeof(StorePriceTransferService).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"未找到 {methodName}");
        return (string)method.Invoke(null, new object[] { request })!;
    }

    private static string InvokeStageSql(string methodName)
    {
        var method = typeof(StorePriceTransferService).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"未找到 {methodName}");
        return (string)method.Invoke(null, null)!;
    }
}
