using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class ContainerReactServiceBatchUpdateDetailsTests : IDisposable
{
    private readonly string _localDbPath;
    private readonly string _hbSalesDbPath;
    private readonly SqliteConnection _localConnection;
    private readonly SqliteConnection _hbSalesConnection;
    private readonly SqlSugarClient _localDb;
    private readonly SqlSugarScope _hbSalesDb;

    public ContainerReactServiceBatchUpdateDetailsTests()
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
            typeof(StoreRetailPrice),
            typeof(ProductSetCode),
            typeof(StoreMultiCodeProduct),
            typeof(WarehouseCategory),
            typeof(ContainerDetailFieldOverrideAudit)
        );
    }

    [Fact]
    public async Task ContainerReactServiceUpdateContainerAsync_状态变化_应更新货柜主表状态并保留头部字段更新()
    {
        await _localDb.Insertable(
            new Container
            {
                ContainerCode = "OOCU5568972",
                ContainerNumber = "OOCU5568972",
                LoadingDate = new DateTime(2026, 5, 26),
                EstimatedArrivalDate = new DateTime(2026, 6, 16),
                ActualArrivalDate = new DateTime(2026, 6, 15),
                ExchangeRate = 4.5m,
                ShippingFee = 100m,
                Status = 0,
                Remarks = "旧备注",
            }
        ).ExecuteCommandAsync();
        var service = CreateService();

        var success = await service.UpdateContainerAsync(
            "OOCU5568972",
            new UpdateContainerDto
            {
                货柜编号 = " OOCU5568973 ",
                装柜日期 = new DateTime(2026, 5, 27),
                预计到岸日期 = new DateTime(2026, 6, 17),
                实际到货日期 = new DateTime(2026, 6, 16),
                汇率 = 4.6m,
                运费 = 1280m,
                备注 = "运输中",
                状态 = 1,
            }
        );

        var container = await _localDb.Queryable<Container>()
            .SingleAsync(x => x.ContainerCode == "OOCU5568972");
        Assert.True(success);
        Assert.Equal("OOCU5568973", container.ContainerNumber);
        Assert.Equal(new DateTime(2026, 5, 27), container.LoadingDate);
        Assert.Equal(new DateTime(2026, 6, 17), container.EstimatedArrivalDate);
        Assert.Equal(1, container.Status);
        Assert.Equal(new DateTime(2026, 6, 16), container.ActualArrivalDate);
        Assert.Equal(4.6m, container.ExchangeRate);
        Assert.Equal(1280m, container.ShippingFee);
        Assert.Equal("运输中", container.Remarks);
    }

    [Fact]
    public async Task ContainerReactServiceUpdateContainerAsync_显式清空到库日期_应置空且不影响其他字段()
    {
        await _localDb.Insertable(
            new List<Container>
            {
                new()
                {
                    ContainerCode = "C-CLEAR-DATES",
                    ContainerNumber = "CLEAR0001",
                    LoadingDate = new DateTime(2026, 9, 1),
                    EstimatedArrivalDate = new DateTime(2026, 10, 8),
                    ActualArrivalDate = new DateTime(2026, 10, 9),
                    Status = 1,
                    Remarks = "保留备注",
                },
                new()
                {
                    ContainerCode = "C-KEEP-DATES",
                    ContainerNumber = "KEEP0001",
                    LoadingDate = new DateTime(2026, 9, 2),
                    EstimatedArrivalDate = new DateTime(2026, 10, 10),
                    ActualArrivalDate = new DateTime(2026, 10, 11),
                    Status = 1,
                },
            }
        ).ExecuteCommandAsync();
        var service = CreateService();

        // 只清空实际到货：预计到岸未传值、也未要求清空，必须保持原值。
        Assert.True(
            await service.UpdateContainerAsync(
                "C-CLEAR-DATES",
                new UpdateContainerDto { ClearActualArrivalDate = true }
            )
        );
        var afterActualCleared = await _localDb.Queryable<Container>()
            .SingleAsync(x => x.ContainerCode == "C-CLEAR-DATES");
        Assert.Null(afterActualCleared.ActualArrivalDate);
        Assert.Equal(new DateTime(2026, 10, 8), afterActualCleared.EstimatedArrivalDate);
        Assert.Equal("保留备注", afterActualCleared.Remarks);
        Assert.Equal(1, afterActualCleared.Status);

        Assert.True(
            await service.UpdateContainerAsync(
                "C-CLEAR-DATES",
                new UpdateContainerDto { ClearEstimatedArrivalDate = true }
            )
        );
        var afterBothCleared = await _localDb.Queryable<Container>()
            .SingleAsync(x => x.ContainerCode == "C-CLEAR-DATES");
        Assert.Null(afterBothCleared.EstimatedArrivalDate);
        Assert.Null(afterBothCleared.ActualArrivalDate);

        // 清空只作用于目标货柜。
        var untouched = await _localDb.Queryable<Container>()
            .SingleAsync(x => x.ContainerCode == "C-KEEP-DATES");
        Assert.Equal(new DateTime(2026, 10, 10), untouched.EstimatedArrivalDate);
        Assert.Equal(new DateTime(2026, 10, 11), untouched.ActualArrivalDate);
    }

    [Fact]
    public async Task ContainerReactServiceUpdateContainerAsync_只设置日期不传清空标记_应只改日期()
    {
        await _localDb.Insertable(
            new Container
            {
                ContainerCode = "C-SET-ACTUAL",
                ContainerNumber = "SET0001",
                LoadingDate = new DateTime(2026, 9, 3),
                EstimatedArrivalDate = new DateTime(2026, 10, 8),
                Status = 1,
            }
        ).ExecuteCommandAsync();
        var service = CreateService();

        Assert.True(
            await service.UpdateContainerAsync(
                "C-SET-ACTUAL",
                new UpdateContainerDto { 实际到货日期 = new DateTime(2026, 10, 7) }
            )
        );

        var container = await _localDb.Queryable<Container>()
            .SingleAsync(x => x.ContainerCode == "C-SET-ACTUAL");
        Assert.Equal(new DateTime(2026, 10, 7), container.ActualArrivalDate);
        Assert.Equal(new DateTime(2026, 10, 8), container.EstimatedArrivalDate);
        // 登记实际到库不联动状态。
        Assert.Equal(1, container.Status);
    }

    [Fact]
    public async Task ContainerReactServiceUpdateContainerAsync_更新成同编号同装柜日期_应拒绝保存()
    {
        await _localDb.Insertable(
            new List<Container>
            {
                new()
                {
                    ContainerCode = "C-EXISTING",
                    ContainerNumber = "CSNU6209359",
                    LoadingDate = new DateTime(2026, 5, 29, 8, 30, 0),
                    Status = 1,
                },
                new()
                {
                    ContainerCode = "C-TARGET",
                    ContainerNumber = "CSNU6209360",
                    LoadingDate = new DateTime(2026, 5, 30),
                    Status = 1,
                },
            }
        ).ExecuteCommandAsync();
        var service = CreateService(concurrencyEnabled: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateContainerAsync(
                "C-TARGET",
                new UpdateContainerDto
                {
                    货柜编号 = " CSNU6209359 ",
                    装柜日期 = new DateTime(2026, 5, 29, 15, 45, 0),
                }
            )
        );

        Assert.Equal("货柜编号 CSNU6209359 在装柜日期 2026-05-29 已存在", ex.Message);
    }

    [Fact]
    public async Task ContainerReactServiceUpdateContainerAsync_历史重复数据只改状态备注_应允许保存()
    {
        await _localDb.Insertable(
            new List<Container>
            {
                new()
                {
                    ContainerCode = "C-DUPLICATE-1",
                    ContainerNumber = "CSNU6209359",
                    LoadingDate = new DateTime(2026, 5, 29, 8, 30, 0),
                    Status = 1,
                },
                new()
                {
                    ContainerCode = "C-DUPLICATE-2",
                    ContainerNumber = "CSNU6209359",
                    LoadingDate = new DateTime(2026, 5, 29, 15, 45, 0),
                    Status = 1,
                    Remarks = "旧备注",
                },
            }
        ).ExecuteCommandAsync();
        var service = CreateService();

        var success = await service.UpdateContainerAsync(
            "C-DUPLICATE-2",
            new UpdateContainerDto
            {
                货柜编号 = " CSNU6209359 ",
                装柜日期 = new DateTime(2026, 5, 29),
                状态 = 2,
                备注 = "只改状态备注",
            }
        );

        var container = await _localDb.Queryable<Container>()
            .SingleAsync(x => x.ContainerCode == "C-DUPLICATE-2");
        Assert.True(success);
        Assert.Equal(2, container.Status);
        Assert.Equal("只改状态备注", container.Remarks);
    }

    [Fact]
    public async Task AlignDomesticProductCodeAsync_确认后应反向更新国内编码并级联引用()
    {
        await SeedDetailAsync("D-ALIGN-1", "DOM-OLD");
        await SeedDetailAsync("D-ALIGN-2", "DOM-OLD");
        await _localDb.Insertable(new DomesticProduct
        {
            ProductCode = "DOM-OLD",
            HBProductNo = "ITEM-ALIGN",
            SupplierCode = "200",
            ProductName = "国内旧编码商品",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await SeedLocalProductAsync("LOCAL-NEW", "本地主档商品", null, "ITEM-ALIGN", "200");
        await _localDb.Insertable(new DomesticSetProduct
        {
            ProductCode = "DOM-OLD",
            ProductNo = "ITEM-ALIGN",
            SetProductNo = "SET-ALIGN",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new ProductGrade
        {
            Id = "GRADE-ALIGN",
            ProductCode = "DOM-OLD",
            Grade = "A",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new DomesticProductCreationLog
        {
            LogId = "LOG-ALIGN",
            ProductCode = "DOM-OLD",
            SupplierCode = "200",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>? recordedBefore = null;
        IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>? recordedAfter = null;
        WarehouseProductChangeHistoryContextDto? recordedContext = null;
        var history = CreateAlignHistoryMock(
            "DOM-OLD",
            "LOCAL-NEW",
            (before, after, context) =>
            {
                recordedBefore = before;
                recordedAfter = after;
                recordedContext = context;
            }
        );
        var currentUser = CreateCurrentUser("align-user-guid", "对齐操作员");
        var service = CreateService(history.Object, currentUser);

        var result = await service.AlignDomesticProductCodeAsync(
            new AlignDomesticProductCodeRequestDto
            {
                DetailHguid = "D-ALIGN-1",
                ExpectedDomesticProductCode = "DOM-OLD",
                TargetProductCode = "LOCAL-NEW",
                SupplierCode = "200",
            }
        );

        Assert.Equal("DOM-OLD", result.OldProductCode);
        Assert.Equal("LOCAL-NEW", result.NewProductCode);
        Assert.Equal(1, result.UpdatedDomesticProducts);
        Assert.Equal(2, result.UpdatedContainerDetails);
        Assert.Equal(1, result.UpdatedDomesticSetProducts);
        Assert.Equal(1, result.UpdatedProductGrades);
        Assert.Equal(1, result.UpdatedDomesticProductCreationLogs);
        Assert.False(await _localDb.Queryable<DomesticProduct>().AnyAsync(x => x.ProductCode == "DOM-OLD"));
        Assert.True(await _localDb.Queryable<DomesticProduct>().AnyAsync(x => x.ProductCode == "LOCAL-NEW"));
        Assert.Equal(2, await _localDb.Queryable<ContainerDetail>().CountAsync(x => x.ProductCode == "LOCAL-NEW"));
        Assert.True(await _localDb.Queryable<DomesticSetProduct>().AnyAsync(x => x.ProductCode == "LOCAL-NEW"));
        Assert.True(await _localDb.Queryable<ProductGrade>().AnyAsync(x => x.ProductCode == "LOCAL-NEW"));
        Assert.True(await _localDb.Queryable<DomesticProductCreationLog>().AnyAsync(x => x.ProductCode == "LOCAL-NEW"));
        Assert.NotNull(recordedBefore);
        Assert.NotNull(recordedAfter);
        Assert.True(recordedBefore!.TryGetValue("LOCAL-NEW", out var reboundBefore));
        Assert.True(recordedAfter!.TryGetValue("LOCAL-NEW", out var capturedAfter));
        Assert.Equal("DOM-OLD", reboundBefore!.ProductCode);
        Assert.Equal("LOCAL-NEW", capturedAfter!.ProductCode);
        Assert.Equal("Update", recordedContext?.Action);
        Assert.Equal("ContainerDetail", recordedContext?.Source);
        Assert.Equal("C-TEST", recordedContext?.SourceReference);
        Assert.Equal("align-user-guid", recordedContext?.ActorUserGuid);
        Assert.Equal("对齐操作员", recordedContext?.ActorName);
        history.VerifyAll();
    }

    [Fact]
    public async Task AlignDomesticProductCodeAsync_历史失败时回滚国内商品和引用改码()
    {
        await SeedDetailAsync("D-ALIGN-ROLLBACK", "DOM-ROLLBACK");
        await _localDb.Insertable(new DomesticProduct
        {
            ProductCode = "DOM-ROLLBACK",
            HBProductNo = "ITEM-ROLLBACK",
            SupplierCode = "200",
            ProductName = "回滚商品",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await SeedLocalProductAsync(
            "LOCAL-ROLLBACK",
            "本地主档回滚商品",
            null,
            "ITEM-ROLLBACK",
            "200"
        );
        var history = CreateAlignHistoryMock(
            "DOM-ROLLBACK",
            "LOCAL-ROLLBACK",
            (_, _, _) => { },
            throwWhenRecording: true
        );
        var service = CreateService(history.Object, CreateCurrentUser("rollback-guid", "回滚操作员"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AlignDomesticProductCodeAsync(
                new AlignDomesticProductCodeRequestDto
                {
                    DetailHguid = "D-ALIGN-ROLLBACK",
                    ExpectedDomesticProductCode = "DOM-ROLLBACK",
                    TargetProductCode = "LOCAL-ROLLBACK",
                    SupplierCode = "200",
                }
            )
        );

        Assert.True(await _localDb.Queryable<DomesticProduct>()
            .AnyAsync(item => item.ProductCode == "DOM-ROLLBACK"));
        Assert.False(await _localDb.Queryable<DomesticProduct>()
            .AnyAsync(item => item.ProductCode == "LOCAL-ROLLBACK"));
        Assert.True(await _localDb.Queryable<ContainerDetail>()
            .AnyAsync(item => item.DetailCode == "D-ALIGN-ROLLBACK" && item.ProductCode == "DOM-ROLLBACK"));
        history.VerifyAll();
    }

    [Fact]
    public async Task AlignDomesticProductCodeAsync_缺少供应商代码_应拒绝()
    {
        var service = CreateService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AlignDomesticProductCodeAsync(
                new AlignDomesticProductCodeRequestDto
                {
                    DetailHguid = "D-ALIGN-NO-SUPPLIER",
                    ExpectedDomesticProductCode = "DOM-NO-SUPPLIER",
                    TargetProductCode = "LOCAL-NO-SUPPLIER",
                }
            )
        );

        Assert.Equal("供应商代码不能为空", ex.Message);
    }

    [Fact]
    public async Task AlignDomesticProductCodeAsync_目标国内编码已存在_应拒绝()
    {
        await SeedDetailAsync("D-ALIGN-DUP", "DOM-DUP-OLD");
        await _localDb.Insertable(new DomesticProduct
        {
            ProductCode = "DOM-DUP-OLD",
            HBProductNo = "ITEM-DUP",
            SupplierCode = "200",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new DomesticProduct
        {
            ProductCode = "LOCAL-DUP",
            HBProductNo = "ITEM-DUP",
            SupplierCode = "200",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await SeedLocalProductAsync("LOCAL-DUP", "本地主档商品", null, "ITEM-DUP", "200");
        var service = CreateService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AlignDomesticProductCodeAsync(
                new AlignDomesticProductCodeRequestDto
                {
                    DetailHguid = "D-ALIGN-DUP",
                    ExpectedDomesticProductCode = "DOM-DUP-OLD",
                    TargetProductCode = "LOCAL-DUP",
                    SupplierCode = "200",
                }
            )
        );

        Assert.Equal("目标国内商品编码已存在，不能自动合并", ex.Message);
    }

    [Fact]
    public async Task AlignDomesticProductCodeAsync_货号不一致_应拒绝()
    {
        await SeedDetailAsync("D-ALIGN-ITEM", "DOM-ITEM-OLD");
        await _localDb.Insertable(new DomesticProduct
        {
            ProductCode = "DOM-ITEM-OLD",
            HBProductNo = "ITEM-OLD",
            SupplierCode = "200",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await SeedLocalProductAsync("LOCAL-ITEM-NEW", "本地主档商品", null, "ITEM-NEW", "200");
        var service = CreateService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AlignDomesticProductCodeAsync(
                new AlignDomesticProductCodeRequestDto
                {
                    DetailHguid = "D-ALIGN-ITEM",
                    ExpectedDomesticProductCode = "DOM-ITEM-OLD",
                    TargetProductCode = "LOCAL-ITEM-NEW",
                    SupplierCode = "200",
                }
            )
        );

        Assert.Equal("国内商品货号与本地主档货号不一致，不能对齐编码", ex.Message);
    }

    [Fact]
    public async Task AlignDomesticProductCodeAsync_国内商品供应商不一致_应拒绝()
    {
        await SeedDetailAsync("D-ALIGN-DOM-SUPPLIER", "DOM-DOM-SUPPLIER-OLD");
        await _localDb.Insertable(new DomesticProduct
        {
            ProductCode = "DOM-DOM-SUPPLIER-OLD",
            HBProductNo = "ITEM-DOM-SUPPLIER",
            SupplierCode = "999",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await SeedLocalProductAsync("LOCAL-DOM-SUPPLIER-NEW", "本地主档商品", null, "ITEM-DOM-SUPPLIER", "200");
        var service = CreateService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AlignDomesticProductCodeAsync(
                new AlignDomesticProductCodeRequestDto
                {
                    DetailHguid = "D-ALIGN-DOM-SUPPLIER",
                    ExpectedDomesticProductCode = "DOM-DOM-SUPPLIER-OLD",
                    TargetProductCode = "LOCAL-DOM-SUPPLIER-NEW",
                    SupplierCode = "200",
                }
            )
        );

        Assert.Equal("国内商品供应商代码与候选供应商不一致，不能对齐编码", ex.Message);
    }

    [Fact]
    public async Task AlignDomesticProductCodeAsync_本地主档供应商不一致_应拒绝()
    {
        await SeedDetailAsync("D-ALIGN-SUPPLIER", "DOM-SUPPLIER-OLD");
        await _localDb.Insertable(new DomesticProduct
        {
            ProductCode = "DOM-SUPPLIER-OLD",
            HBProductNo = "ITEM-SUPPLIER",
            SupplierCode = "200",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await SeedLocalProductAsync("LOCAL-SUPPLIER-NEW", "本地主档商品", null, "ITEM-SUPPLIER", "999");
        var service = CreateService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AlignDomesticProductCodeAsync(
                new AlignDomesticProductCodeRequestDto
                {
                    DetailHguid = "D-ALIGN-SUPPLIER",
                    ExpectedDomesticProductCode = "DOM-SUPPLIER-OLD",
                    TargetProductCode = "LOCAL-SUPPLIER-NEW",
                    SupplierCode = "200",
                }
            )
        );

        Assert.Equal("供应商代码与本地主档不一致，不能对齐编码", ex.Message);
    }

    [Fact]
    public async Task AlignDomesticProductCodeAsync_套装子商品_应拒绝()
    {
        await SeedDetailAsync("D-ALIGN-SET-CHILD", "DOM-SET-CHILD-OLD", "套装子商品");
        var service = CreateService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AlignDomesticProductCodeAsync(
                new AlignDomesticProductCodeRequestDto
                {
                    DetailHguid = "D-ALIGN-SET-CHILD",
                    ExpectedDomesticProductCode = "DOM-SET-CHILD-OLD",
                    TargetProductCode = "LOCAL-SET-CHILD-NEW",
                    SupplierCode = "200",
                }
            )
        );

        Assert.Equal("套装子商品关联套装结构，暂不支持单独对齐编码", ex.Message);
    }

    [Fact]
    public async Task AlignDomesticProductCodeAsync_明细旧编码已变化_应拒绝()
    {
        await SeedDetailAsync("D-ALIGN-STALE", "DOM-CHANGED");
        var service = CreateService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AlignDomesticProductCodeAsync(
                new AlignDomesticProductCodeRequestDto
                {
                    DetailHguid = "D-ALIGN-STALE",
                    ExpectedDomesticProductCode = "DOM-STALE",
                    TargetProductCode = "LOCAL-STALE",
                    SupplierCode = "200",
                }
            )
        );

        Assert.Equal("明细商品编码已变化，请刷新后重试", ex.Message);
    }

    [Fact]
    public async Task AlignDomesticProductCodeAsync_原国内编码已存在本地主档_应拒绝()
    {
        await SeedDetailAsync("D-ALIGN-OLD-LOCAL", "DOM-OLD-LOCAL");
        await _localDb.Insertable(new DomesticProduct
        {
            ProductCode = "DOM-OLD-LOCAL",
            HBProductNo = "ITEM-OLD-LOCAL",
            SupplierCode = "200",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await SeedLocalProductAsync("LOCAL-OLD-LOCAL", "目标本地主档商品", null, "ITEM-OLD-LOCAL", "200");
        await SeedLocalProductAsync("DOM-OLD-LOCAL", "旧码本地主档商品", null, "ITEM-OLD-LOCAL", "200");
        var service = CreateService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AlignDomesticProductCodeAsync(
                new AlignDomesticProductCodeRequestDto
                {
                    DetailHguid = "D-ALIGN-OLD-LOCAL",
                    ExpectedDomesticProductCode = "DOM-OLD-LOCAL",
                    TargetProductCode = "LOCAL-OLD-LOCAL",
                    SupplierCode = "200",
                }
            )
        );

        Assert.Equal("原国内商品编码已存在本地主档或仓库商品，不能自动改码", ex.Message);
    }

    [Fact]
    public async Task AlignDomesticProductCodeAsync_原国内编码已存在仓库商品_应拒绝()
    {
        await SeedDetailAsync("D-ALIGN-OLD-WAREHOUSE", "DOM-OLD-WAREHOUSE");
        await _localDb.Insertable(new DomesticProduct
        {
            ProductCode = "DOM-OLD-WAREHOUSE",
            HBProductNo = "ITEM-OLD-WAREHOUSE",
            SupplierCode = "200",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await SeedLocalProductAsync("LOCAL-OLD-WAREHOUSE", "目标本地主档商品", null, "ITEM-OLD-WAREHOUSE", "200");
        await _localDb.Insertable(new WarehouseProduct
        {
            ProductCode = "DOM-OLD-WAREHOUSE",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        var service = CreateService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AlignDomesticProductCodeAsync(
                new AlignDomesticProductCodeRequestDto
                {
                    DetailHguid = "D-ALIGN-OLD-WAREHOUSE",
                    ExpectedDomesticProductCode = "DOM-OLD-WAREHOUSE",
                    TargetProductCode = "LOCAL-OLD-WAREHOUSE",
                    SupplierCode = "200",
                }
            )
        );

        Assert.Equal("原国内商品编码已存在本地主档或仓库商品，不能自动改码", ex.Message);
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_普通保存不应反向更新国内商品编码()
    {
        await SeedDetailAsync("D-NO-ALIGN", "DOM-NO-ALIGN");
        await _localDb.Insertable(new DomesticProduct
        {
            ProductCode = "DOM-NO-ALIGN",
            HBProductNo = "ITEM-NO-ALIGN",
            DomesticPrice = 1m,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await SeedLocalProductAsync("LOCAL-NO-ALIGN", "本地主档商品", null, "ITEM-NO-ALIGN");
        var service = CreateService();

        var totalUpdated = await service.BatchUpdateDetailsAsync(
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "D-NO-ALIGN", 国内价格 = 2m },
            }
        );

        Assert.Equal(1, totalUpdated);
        Assert.True(await _localDb.Queryable<DomesticProduct>().AnyAsync(x => x.ProductCode == "DOM-NO-ALIGN"));
        Assert.False(await _localDb.Queryable<DomesticProduct>().AnyAsync(x => x.ProductCode == "LOCAL-NO-ALIGN"));
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_未匹配明细目标分类_应保存到货柜明细()
    {
        await SeedDetailAsync("D-TARGET-CATEGORY-NEW", "P-TARGET-CATEGORY-NEW");
        await SeedWarehouseCategoryAsync("CAT-TARGET-NEW");
        var service = CreateService();

        var totalUpdated = await service.BatchUpdateDetailsAsync(
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "D-TARGET-CATEGORY-NEW", ProductCategoryGUID = "CAT-TARGET-NEW" },
            }
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-TARGET-CATEGORY-NEW");
        Assert.Equal(1, totalUpdated);
        Assert.Equal("CAT-TARGET-NEW", detail.TargetWarehouseCategoryGUID);
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_已有商品目标分类_应同步本地商品仓库分类()
    {
        await SeedDetailAsync("D-TARGET-CATEGORY-EXISTING", "P-TARGET-CATEGORY-EXISTING");
        await SeedRelatedPriceRowsAsync("P-TARGET-CATEGORY-EXISTING");
        await SeedWarehouseCategoryAsync("CAT-TARGET-EXISTING");
        var service = CreateService();

        var totalUpdated = await service.BatchUpdateDetailsAsync(
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "D-TARGET-CATEGORY-EXISTING", ProductCategoryGUID = "CAT-TARGET-EXISTING" },
            }
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-TARGET-CATEGORY-EXISTING");
        var product = await _localDb.Queryable<Product>()
            .SingleAsync(x => x.ProductCode == "P-TARGET-CATEGORY-EXISTING");
        Assert.Equal(1, totalUpdated);
        Assert.Equal("CAT-TARGET-EXISTING", detail.TargetWarehouseCategoryGUID);
        Assert.Equal("CAT-TARGET-EXISTING", product.WarehouseCategoryGUID);
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_空白目标分类_应拒绝并不清空明细分类()
    {
        await SeedDetailAsync("D-TARGET-CATEGORY-BLANK", "P-TARGET-CATEGORY-BLANK");
        await _localDb.Updateable<ContainerDetail>()
            .SetColumns(x => x.TargetWarehouseCategoryGUID == "CAT-EXISTING")
            .Where(x => x.DetailCode == "D-TARGET-CATEGORY-BLANK")
            .ExecuteCommandAsync();
        var service = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.BatchUpdateDetailsAsync(
                new List<UpdateContainerDetailDto>
                {
                    new() { HGUID = "D-TARGET-CATEGORY-BLANK", ProductCategoryGUID = "   " },
                }
            )
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-TARGET-CATEGORY-BLANK");
        Assert.Equal("CAT-EXISTING", detail.TargetWarehouseCategoryGUID);
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_不存在或已删除目标分类_应拒绝并不写入分类()
    {
        await SeedDetailAsync("D-TARGET-CATEGORY-MISSING", "P-TARGET-CATEGORY-MISSING");
        await SeedWarehouseCategoryAsync("CAT-DELETED", isDeleted: true);
        var service = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.BatchUpdateDetailsAsync(
                new List<UpdateContainerDetailDto>
                {
                    new() { HGUID = "D-TARGET-CATEGORY-MISSING", ProductCategoryGUID = "CAT-MISSING" },
                    new() { HGUID = "D-TARGET-CATEGORY-MISSING", ProductCategoryGUID = "CAT-DELETED" },
                }
            )
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-TARGET-CATEGORY-MISSING");
        Assert.Null(detail.TargetWarehouseCategoryGUID);
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_纯英文名称_Trim后应回写DomesticProduct()
    {
        await SeedDetailAndProductAsync("D-EN-ONLY", "P-EN-ONLY", englishName: null);
        await SeedLocalProductAsync("P-EN-ONLY", productName: "旧本地商品名", englishName: "Old Local English");
        var service = CreateService();

        var totalUpdated = await service.BatchUpdateDetailsAsync(
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "D-EN-ONLY", 英文名称 = "  Large Strawberry  " },
            }
        );

        var product = await _localDb.Queryable<DomesticProduct>()
            .SingleAsync(x => x.ProductCode == "P-EN-ONLY");
        var localProduct = await _localDb.Queryable<Product>()
            .SingleAsync(x => x.ProductCode == "P-EN-ONLY");
        Assert.Equal(1, totalUpdated);
        Assert.Equal("Large Strawberry", product.EnglishProductName);
        Assert.Equal("Large Strawberry", localProduct.ProductName);
        Assert.Equal("Large Strawberry", localProduct.EnglishName);
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_空白英文名称_不覆盖DomesticProduct()
    {
        await SeedDetailAndProductAsync("D-BLANK-EN", "P-BLANK-EN", englishName: "Existing English");
        var service = CreateService();

        var totalUpdated = await service.BatchUpdateDetailsAsync(
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "D-BLANK-EN", 英文名称 = "   " },
            }
        );

        var product = await _localDb.Queryable<DomesticProduct>()
            .SingleAsync(x => x.ProductCode == "P-BLANK-EN");
        Assert.Equal(0, totalUpdated);
        Assert.Equal("Existing English", product.EnglishProductName);
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_清空英文名称_应清空DomesticProduct()
    {
        await SeedDetailAndProductAsync("D-CLEAR-EN", "P-CLEAR-EN", englishName: "Existing English");
        await SeedLocalProductAsync("P-CLEAR-EN", productName: "保留本地商品名", englishName: "Existing Local English");
        var service = CreateService();

        var totalUpdated = await service.BatchUpdateDetailsAsync(
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "D-CLEAR-EN", ClearEnglishName = true },
            }
        );

        var product = await _localDb.Queryable<DomesticProduct>()
            .SingleAsync(x => x.ProductCode == "P-CLEAR-EN");
        var localProduct = await _localDb.Queryable<Product>()
            .SingleAsync(x => x.ProductCode == "P-CLEAR-EN");
        Assert.Equal(1, totalUpdated);
        Assert.Null(product.EnglishProductName);
        Assert.Equal("保留本地商品名", localProduct.ProductName);
        Assert.Null(localProduct.EnglishName);
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_同一商品多条明细相同英文名称意图_应聚合回写并统计请求行()
    {
        await SeedDetailAndProductAsync("D-SAME-1", "P-SAME", englishName: "Old English");
        await SeedDetailAsync("D-SAME-2", "P-SAME");
        await SeedDetailAsync("D-SAME-3", "P-SAME");
        await SeedLocalProductAsync("P-SAME", "旧本地商品名", "Old Local English");
        var service = CreateService();

        var totalUpdated = await service.BatchUpdateDetailsAsync(
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "D-SAME-1", 商品名称 = "聚合中文名" },
                new() { HGUID = "D-SAME-2", 英文名称 = "Same English" },
                new() { HGUID = "D-SAME-3", 英文名称 = " Same English " },
            }
        );

        var product = await _localDb.Queryable<DomesticProduct>()
            .SingleAsync(x => x.ProductCode == "P-SAME");
        Assert.Equal(3, totalUpdated);
        Assert.Equal("聚合中文名", product.ProductName);
        Assert.Equal("Same English", product.EnglishProductName);
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_同一商品多条明细相同清空意图_应清空并统计请求行()
    {
        await SeedDetailAndProductAsync("D-SAME-CLEAR-1", "P-SAME-CLEAR", englishName: "Old English");
        await SeedDetailAsync("D-SAME-CLEAR-2", "P-SAME-CLEAR");
        await SeedDetailAsync("D-SAME-CLEAR-3", "P-SAME-CLEAR");
        await SeedLocalProductAsync(
            "P-SAME-CLEAR",
            "保留本地商品名",
            "Old Local English"
        );
        var service = CreateService();

        var totalUpdated = await service.BatchUpdateDetailsAsync(
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "D-SAME-CLEAR-1", ClearEnglishName = true },
                new() { HGUID = "D-SAME-CLEAR-2", ClearEnglishName = true },
                new() { HGUID = "D-SAME-CLEAR-3", ClearEnglishName = true },
            }
        );

        var product = await _localDb.Queryable<DomesticProduct>()
            .SingleAsync(x => x.ProductCode == "P-SAME-CLEAR");
        Assert.Equal(3, totalUpdated);
        Assert.Null(product.EnglishProductName);
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_明细不存在_返回校验错误并阻止该行全部字段()
    {
        await SeedDetailAsync("D-EXISTING-PARTIAL", "P-EXISTING-PARTIAL");
        var service = CreateService();

        var result = await service.BatchUpdateDetailsDetailedAsync(
            "C-TEST",
            new List<UpdateContainerDetailDto>
            {
                new()
                {
                    HGUID = "D-MISSING-DETAILED",
                    进口价格 = 9.99m,
                    英文名称 = "Missing Detail",
                    ProductCategoryGUID = "CAT-SHOULD-NOT-BE-VALIDATED",
                },
                new() { HGUID = "D-EXISTING-PARTIAL", 进口价格 = 2.34m },
            }
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-EXISTING-PARTIAL");
        Assert.Equal(1, result.TotalUpdated);
        Assert.Equal(2, result.TotalRequested);
        var error = Assert.Single(result.ValidationErrors);
        Assert.Equal("D-MISSING-DETAILED", error.HGUID);
        Assert.Equal("*", error.Field);
        Assert.Equal("DETAIL_NOT_FOUND", error.Code);
        Assert.Equal(2.34m, detail.ImportPrice);
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_英文名称任一关联目标存在_更新实际目标且两者都缺失才报错()
    {
        await SeedDetailAsync("D-DOMESTIC-MISSING", "P-DOMESTIC-MISSING");
        await SeedLocalProductAsync(
            "P-DOMESTIC-MISSING",
            productName: "本地主档旧名",
            englishName: "Local Old"
        );
        await SeedDetailAndProductAsync(
            "D-LOCAL-MISSING",
            "P-LOCAL-MISSING",
            englishName: "Domestic Old"
        );
        await SeedDetailAsync("D-PRODUCT-CODE-MISSING", productCode: null);
        var service = CreateService();

        var result = await service.BatchUpdateDetailsDetailedAsync(
            "C-TEST",
            new List<UpdateContainerDetailDto>
            {
                new()
                {
                    HGUID = "D-DOMESTIC-MISSING",
                    进口价格 = 3.45m,
                    英文名称 = "New English One",
                },
                new()
                {
                    HGUID = "D-LOCAL-MISSING",
                    进口价格 = 4.56m,
                    英文名称 = "New English Two",
                },
                new()
                {
                    HGUID = "D-PRODUCT-CODE-MISSING",
                    进口价格 = 5.67m,
                    英文名称 = "New English Three",
                },
            }
        );

        var domesticMissingDetail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-DOMESTIC-MISSING");
        var localMissingDetail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-LOCAL-MISSING");
        var productCodeMissingDetail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-PRODUCT-CODE-MISSING");
        var localOnlyProduct = await _localDb.Queryable<Product>()
            .SingleAsync(x => x.ProductCode == "P-DOMESTIC-MISSING");
        var domesticOnlyProduct = await _localDb.Queryable<DomesticProduct>()
            .SingleAsync(x => x.ProductCode == "P-LOCAL-MISSING");

        Assert.Equal(3, result.TotalUpdated);
        Assert.Equal(3, result.TotalRequested);
        var error = Assert.Single(result.ValidationErrors);
        Assert.Equal("D-PRODUCT-CODE-MISSING", error.HGUID);
        Assert.Equal("英文名称", error.Field);
        Assert.Equal("RELATED_PRODUCT_NOT_FOUND", error.Code);
        Assert.Equal(3.45m, domesticMissingDetail.ImportPrice);
        Assert.Equal(4.56m, localMissingDetail.ImportPrice);
        Assert.Equal(5.67m, productCodeMissingDetail.ImportPrice);
        Assert.Equal("New English One", localOnlyProduct.ProductName);
        Assert.Equal("New English One", localOnlyProduct.EnglishName);
        Assert.Equal("New English Two", domesticOnlyProduct.EnglishProductName);
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_同商品英文名称意图冲突_所有冲突项报错且不覆盖名称()
    {
        await SeedDetailAndProductAsync(
            "D-CONFLICT-1",
            "P-CONFLICT",
            englishName: "Existing English"
        );
        await SeedDetailAsync("D-CONFLICT-2", "P-CONFLICT");
        await SeedDetailAsync("D-CONFLICT-3", "P-CONFLICT");
        await SeedLocalProductAsync(
            "P-CONFLICT",
            productName: "Existing Local Name",
            englishName: "Existing Local English"
        );
        var service = CreateService();

        var result = await service.BatchUpdateDetailsDetailedAsync(
            "C-TEST",
            new List<UpdateContainerDetailDto>
            {
                new()
                {
                    HGUID = "D-CONFLICT-1",
                    进口价格 = 5.67m,
                    英文名称 = "First English",
                },
                new() { HGUID = "D-CONFLICT-2", 英文名称 = "Second English" },
                new() { HGUID = "D-CONFLICT-3", ClearEnglishName = true },
            }
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-CONFLICT-1");
        var domesticProduct = await _localDb.Queryable<DomesticProduct>()
            .SingleAsync(x => x.ProductCode == "P-CONFLICT");
        var localProduct = await _localDb.Queryable<Product>()
            .SingleAsync(x => x.ProductCode == "P-CONFLICT");

        Assert.Equal(1, result.TotalUpdated);
        Assert.Equal(3, result.TotalRequested);
        Assert.Equal(3, result.ValidationErrors.Count);
        Assert.Equal(
            new[] { "D-CONFLICT-1", "D-CONFLICT-2", "D-CONFLICT-3" },
            result.ValidationErrors.Select(error => error.HGUID).OrderBy(value => value)
        );
        Assert.All(
            result.ValidationErrors,
            error =>
            {
                Assert.Equal("英文名称", error.Field);
                Assert.Equal("CONFLICTING_PRODUCT_ENGLISH_NAME", error.Code);
            }
        );
        Assert.Equal(5.67m, detail.ImportPrice);
        Assert.Equal("Existing English", domesticProduct.EnglishProductName);
        Assert.Equal("Existing Local Name", localProduct.ProductName);
        Assert.Equal("Existing Local English", localProduct.EnglishName);
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_相同英文名称NoOp和相同清空意图_均视为成功()
    {
        await SeedDetailAndProductAsync("D-NOOP-NAME-1", "P-NOOP-NAME", "Same English");
        await SeedDetailAsync("D-NOOP-NAME-2", "P-NOOP-NAME");
        await SeedLocalProductAsync("P-NOOP-NAME", "Same English", "Same English");
        await SeedDetailAndProductAsync("D-NOOP-CLEAR-1", "P-NOOP-CLEAR", null);
        await SeedDetailAsync("D-NOOP-CLEAR-2", "P-NOOP-CLEAR");
        await SeedLocalProductAsync("P-NOOP-CLEAR", "保留本地商品名", null);
        var service = CreateService();

        var result = await service.BatchUpdateDetailsDetailedAsync(
            "C-TEST",
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "D-NOOP-NAME-1", 英文名称 = "Same English" },
                new() { HGUID = "D-NOOP-NAME-2", 英文名称 = " Same English " },
                new() { HGUID = "D-NOOP-CLEAR-1", ClearEnglishName = true },
                new() { HGUID = "D-NOOP-CLEAR-2", ClearEnglishName = true },
            }
        );

        Assert.Equal(4, result.TotalUpdated);
        Assert.Equal(4, result.TotalRequested);
        Assert.Empty(result.ValidationErrors);
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_英文名称含中文_返回错误且保留同一行价格更新()
    {
        await SeedDetailAndProductAsync(
            "D-CHINESE-DETAILED",
            "P-CHINESE-DETAILED",
            englishName: "Existing English"
        );
        await SeedLocalProductAsync(
            "P-CHINESE-DETAILED",
            productName: "Existing Local Name",
            englishName: "Existing Local English"
        );
        var service = CreateService();

        var result = await service.BatchUpdateDetailsDetailedAsync(
            "C-TEST",
            new List<UpdateContainerDetailDto>
            {
                new()
                {
                    HGUID = "D-CHINESE-DETAILED",
                    进口价格 = 6.78m,
                    英文名称 = "Large 草莓",
                },
            }
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-CHINESE-DETAILED");
        var domesticProduct = await _localDb.Queryable<DomesticProduct>()
            .SingleAsync(x => x.ProductCode == "P-CHINESE-DETAILED");
        var error = Assert.Single(result.ValidationErrors);
        Assert.Equal(1, result.TotalUpdated);
        Assert.Equal("D-CHINESE-DETAILED", error.HGUID);
        Assert.Equal("英文名称", error.Field);
        Assert.Equal("CONTAINS_CHINESE", error.Code);
        Assert.Equal(6.78m, detail.ImportPrice);
        Assert.Equal("Existing English", domesticProduct.EnglishProductName);
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_货柜范围外明细返回错误且绝不写入()
    {
        await SeedDetailAsync("D-IN-SCOPE", productCode: null);
        await _localDb.Insertable(
            new ContainerDetail
            {
                DetailCode = "D-OUT-OF-SCOPE",
                ContainerCode = "C-OTHER",
                DomesticPrice = 1m,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        var service = CreateService();

        var result = await service.BatchUpdateDetailsDetailedAsync(
            "C-TEST",
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "D-IN-SCOPE", 国内价格 = 2m },
                new() { HGUID = "D-OUT-OF-SCOPE", 国内价格 = 9m },
            }
        );

        var inScope = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(row => row.DetailCode == "D-IN-SCOPE");
        var outOfScope = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(row => row.DetailCode == "D-OUT-OF-SCOPE");
        Assert.Equal(2m, inScope.DomesticPrice);
        Assert.Equal(1m, outOfScope.DomesticPrice);
        var error = Assert.Single(result.ValidationErrors);
        Assert.Equal("D-OUT-OF-SCOPE", error.HGUID);
        Assert.Equal("*", error.Field);
        Assert.Equal("DETAIL_OUTSIDE_CONTAINER", error.Code);
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_并发改动其它明细字段_窄更新不得用旧实体覆盖()
    {
        await SeedDetailAsync("D-NARROW-WRITE", productCode: null);
        var captureCount = 0;
        var history = new Mock<IWarehouseProductChangeHistoryService>();
        history
            .Setup(service => service.CaptureSnapshotsAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()
            ))
            .Returns(async () =>
            {
                if (captureCount++ == 0)
                {
                    await _localDb.Updateable<ContainerDetail>()
                        .SetColumns(row => row.OEMPrice == 99m)
                        .Where(row => row.DetailCode == "D-NARROW-WRITE")
                        .ExecuteCommandAsync();
                }
                return new Dictionary<string, WarehouseProductChangeSnapshotDto>();
            });
        history
            .Setup(service => service.RecordChangesAsync(
                It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
                It.IsAny<WarehouseProductChangeHistoryContextDto>(),
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(0);
        var service = CreateService(history.Object);

        var result = await service.BatchUpdateDetailsDetailedAsync(
            "C-TEST",
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "D-NARROW-WRITE", 国内价格 = 8m },
            }
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(row => row.DetailCode == "D-NARROW-WRITE");
        Assert.Equal(1, result.TotalUpdated);
        Assert.Equal(8m, detail.DomesticPrice);
        Assert.Equal(99m, detail.OEMPrice);
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_同字段令牌过期应保留草稿所需冲突信息_其它字段仍可保存()
    {
        await SeedDetailAsync("D-FIELD-CONCURRENCY", productCode: null);
        await _localDb.Updateable<ContainerDetail>()
            .SetColumns(detail => detail.DomesticPrice == 2m)
            .SetColumns(detail => detail.TransportCost == 1m)
            .Where(detail => detail.DetailCode == "D-FIELD-CONCURRENCY")
            .ExecuteCommandAsync();
        var baselineDomestic = ContainerDetailFieldConcurrencyGuard.CreateToken(
            "D-FIELD-CONCURRENCY", "国内价格", 1m, null
        );
        var baselineTransport = ContainerDetailFieldConcurrencyGuard.CreateToken(
            "D-FIELD-CONCURRENCY", "运输成本", 1m, null
        );
        var service = CreateService(concurrencyEnabled: true);

        var result = await service.BatchUpdateDetailsDetailedAsync(
            "C-TEST",
            new List<UpdateContainerDetailDto>
            {
                new()
                {
                    HGUID = "D-FIELD-CONCURRENCY",
                    国内价格 = 3m,
                    运输成本 = 4m,
                    ExpectedServerFieldTokens = new Dictionary<string, string>
                    {
                        ["国内价格"] = baselineDomestic,
                        ["运输成本"] = baselineTransport,
                    },
                    SkipRelatedProductSync = true,
                },
            }
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(row => row.DetailCode == "D-FIELD-CONCURRENCY");
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("国内价格", conflict.Field);
        Assert.Equal("CONCURRENT_FIELD_UPDATE", conflict.Code);
        Assert.Equal(2m, conflict.ServerValue);
        Assert.Equal(3m, conflict.SubmittedValue);
        Assert.Equal(2m, detail.DomesticPrice);
        Assert.Equal(4m, detail.TransportCost);
        Assert.Equal(1, result.TotalUpdated);
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_确认覆盖应在同一事务追加审计()
    {
        await SeedDetailAsync("D-OVERRIDE-AUDIT", productCode: null);
        await _localDb.Updateable<ContainerDetail>()
            .SetColumns(detail => detail.DomesticPrice == 2m)
            .Where(detail => detail.DetailCode == "D-OVERRIDE-AUDIT")
            .ExecuteCommandAsync();
        var service = CreateService(
            currentUserService: CreateCurrentUser("USER-OVERRIDE", "确认覆盖用户"),
            concurrencyEnabled: true
        );
        var currentToken = ContainerDetailFieldConcurrencyGuard.CreateToken(
            "D-OVERRIDE-AUDIT", "国内价格", 2m, null
        );

        var result = await service.BatchUpdateDetailsDetailedAsync(
            "C-TEST",
            new List<UpdateContainerDetailDto>
            {
                new()
                {
                    HGUID = "D-OVERRIDE-AUDIT",
                    国内价格 = 3m,
                    ExpectedServerFieldTokens = new Dictionary<string, string>
                    {
                        ["国内价格"] = ContainerDetailFieldConcurrencyGuard.CreateToken(
                            "D-OVERRIDE-AUDIT", "国内价格", 1m, null
                        ),
                    },
                    OverrideAcknowledgements = new Dictionary<string, string>
                    {
                        ["国内价格"] = currentToken,
                    },
                    SkipRelatedProductSync = true,
                },
            }
        );

        var audit = await _localDb.Queryable<ContainerDetailFieldOverrideAudit>().SingleAsync();
        Assert.Empty(result.Conflicts);
        Assert.Equal(1, result.TotalUpdated);
        Assert.Equal("D-OVERRIDE-AUDIT", audit.DetailHguid);
        Assert.Equal("国内价格", audit.Field);
        Assert.Equal(currentToken, audit.ConfirmationToken);
        Assert.Equal("USER-OVERRIDE", audit.ActorUserGuid);
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_确认覆盖后字段校验失败_不应写入假审计()
    {
        await SeedDetailAndProductAsync("D-OVERRIDE-REJECTED", "P-OVERRIDE-REJECTED", "Old English");
        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(item => item.DetailCode == "D-OVERRIDE-REJECTED");
        var domestic = await _localDb.Queryable<DomesticProduct>()
            .SingleAsync(item => item.ProductCode == "P-OVERRIDE-REJECTED");
        var snapshot = ContainerDetailFieldConcurrencyGuard.CreateSnapshots(
            detail,
            warehouseProduct: null,
            domesticProduct: domestic,
            localProduct: null
        )["英文名称"];
        var currentToken = ContainerDetailFieldConcurrencyGuard.CreateToken(
            detail.DetailCode,
            "英文名称",
            snapshot.Value,
            snapshot.RelatedValue
        );
        var service = CreateService(
            currentUserService: CreateCurrentUser("USER-OVERRIDE-REJECTED", "确认覆盖用户"),
            concurrencyEnabled: true
        );

        var result = await service.BatchUpdateDetailsDetailedAsync(
            "C-TEST",
            new List<UpdateContainerDetailDto>
            {
                new()
                {
                    HGUID = detail.DetailCode,
                    // 备注保证事务会继续执行至审计阶段；英文名随后因中文校验失败而不应留下覆盖记录。
                    备注 = "仍应保存",
                    英文名称 = "中文名称",
                    ExpectedServerFieldTokens = new Dictionary<string, string>
                    {
                        ["英文名称"] = ContainerDetailFieldConcurrencyGuard.CreateToken(
                            detail.DetailCode,
                            "英文名称",
                            "Older English",
                            null
                        ),
                    },
                    OverrideAcknowledgements = new Dictionary<string, string>
                    {
                        ["英文名称"] = currentToken,
                    },
                },
            }
        );

        Assert.Contains(result.ValidationErrors, error => error.Field == "英文名称" && error.Code == "CONTAINS_CHINESE");
        Assert.Empty(await _localDb.Queryable<ContainerDetailFieldOverrideAudit>().ToListAsync());
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_多明细字段更新使用单条参数化CaseSql()
    {
        await SeedDetailAsync("D-CASE-1", productCode: null);
        await SeedDetailAsync("D-CASE-2", productCode: null);
        await SeedDetailAsync("D-CASE-3", productCode: null);
        var detailUpdateCount = 0;
        _localDb.Aop.OnLogExecuting = (sql, _) =>
        {
            if (
                sql.TrimStart().StartsWith("UPDATE ContainerDetail", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("DomesticPrice = CASE", StringComparison.OrdinalIgnoreCase)
            )
            {
                Interlocked.Increment(ref detailUpdateCount);
            }
        };
        var service = CreateService();

        var result = await service.BatchUpdateDetailsDetailedAsync(
            "C-TEST",
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "D-CASE-1", 国内价格 = 3m },
                new() { HGUID = "D-CASE-2", 国内价格 = 4m },
                new() { HGUID = "D-CASE-3", 国内价格 = 5m },
            }
        );

        Assert.Equal(3, result.TotalUpdated);
        Assert.Equal(1, detailUpdateCount);
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_最大参数预算时按固定分块写入明细()
    {
        const int rowCount = 81;
        for (var index = 0; index < rowCount; index++)
        {
            await SeedDetailAsync($"D-PARAMETER-BUDGET-{index:D3}", productCode: null);
        }
        var detailUpdateCount = 0;
        _localDb.Aop.OnLogExecuting = (sql, _) =>
        {
            if (
                sql.TrimStart().StartsWith("UPDATE ContainerDetail", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("DomesticPrice = CASE", StringComparison.OrdinalIgnoreCase)
            )
            {
                Interlocked.Increment(ref detailUpdateCount);
            }
        };
        var service = CreateService();

        var updated = await service.BatchUpdateDetailsAsync(
            Enumerable
                .Range(0, rowCount)
                .Select(index => new UpdateContainerDetailDto
                {
                    HGUID = $"D-PARAMETER-BUDGET-{index:D3}",
                    调整浮率 = 1.1m,
                    国内价格 = 2m,
                    进口价格 = 3m,
                    运输成本 = 4m,
                    贴牌价格 = 5m,
                    单件装箱数 = 6m,
                    单件体积 = 7m,
                    装柜数量 = 8m,
                    合计装柜体积 = 9m,
                    合计装柜金额 = 10m,
                    IsActive = true,
                })
                .ToList()
        );

        Assert.Equal(rowCount, updated);
        Assert.Equal(2, detailUpdateCount);
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_重复明细整行拒绝且不受请求顺序影响()
    {
        await SeedDetailAsync("D-DUPLICATE-REQUEST", productCode: null);
        var service = CreateService();

        var result = await service.BatchUpdateDetailsDetailedAsync(
            "C-TEST",
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "D-DUPLICATE-REQUEST", 国内价格 = 2m },
                new() { HGUID = "D-DUPLICATE-REQUEST", 国内价格 = 3m },
            }
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(row => row.DetailCode == "D-DUPLICATE-REQUEST");
        var error = Assert.Single(result.ValidationErrors);
        Assert.Equal(2, result.TotalRequested);
        Assert.Equal(0, result.TotalUpdated);
        Assert.Equal("D-DUPLICATE-REQUEST", error.HGUID);
        Assert.Equal("*", error.Field);
        Assert.Equal("DUPLICATE_DETAIL_UPDATE", error.Code);
        Assert.Null(detail.DomesticPrice);
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_重复明细严格入口整体拒绝()
    {
        await SeedDetailAsync("D-DUPLICATE-STRICT", productCode: null);
        var service = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.BatchUpdateDetailsAsync(
                new List<UpdateContainerDetailDto>
                {
                    new() { HGUID = "D-DUPLICATE-STRICT", 国内价格 = 2m },
                    new() { HGUID = "D-DUPLICATE-STRICT", 国内价格 = 3m },
                }
            )
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(row => row.DetailCode == "D-DUPLICATE-STRICT");
        Assert.Null(detail.DomesticPrice);
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_关联价格同步按商品聚合并按参数预算分块()
    {
        const int productCount = 401;
        var details = new List<ContainerDetail>();
        var warehouses = new List<WarehouseProduct>();
        var products = new List<Product>();
        var storePrices = new List<StoreRetailPrice>();
        var updates = new List<UpdateContainerDetailDto>();
        for (var index = 0; index < productCount; index++)
        {
            var productCode = $"P-SYNC-BUDGET-{index:D3}";
            details.Add(new ContainerDetail
            {
                DetailCode = $"D-SYNC-BUDGET-{index:D3}",
                ContainerCode = "C-TEST",
                ProductCode = productCode,
                ImportPrice = 1m,
                IsDeleted = false,
            });
            warehouses.Add(new WarehouseProduct
            {
                ProductCode = productCode,
                ImportPrice = 1m,
                OEMPrice = 2m,
                IsActive = true,
                IsDeleted = false,
            });
            products.Add(new Product
            {
                ProductCode = productCode,
                ProductName = productCode,
                PurchasePrice = 1m,
                RetailPrice = 2m,
                IsActive = true,
                IsDeleted = false,
            });
            storePrices.Add(new StoreRetailPrice
            {
                StoreCode = "001",
                ProductCode = productCode,
                PurchasePrice = 1m,
                StoreRetailPriceValue = 2m,
                IsActive = true,
                IsDeleted = false,
            });
            updates.Add(
                new UpdateContainerDetailDto
                {
                    HGUID = $"D-SYNC-BUDGET-{index:D3}",
                    进口价格 = 3m,
                    贴牌价格 = 4m,
                    IsActive = false,
                }
            );
        }
        await _localDb.Insertable(details).ExecuteCommandAsync();
        await _localDb.Insertable(warehouses).ExecuteCommandAsync();
        await _localDb.Insertable(products).ExecuteCommandAsync();
        await _localDb.Insertable(storePrices).ExecuteCommandAsync();
        var warehouseUpdateCount = 0;
        var productUpdateCount = 0;
        var storePriceUpdateCount = 0;
        _localDb.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.TrimStart().StartsWith("UPDATE WarehouseProduct SET", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref warehouseUpdateCount);
            if (sql.TrimStart().StartsWith("UPDATE Product SET", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref productUpdateCount);
            if (sql.TrimStart().StartsWith("UPDATE StoreRetailPrice SET", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref storePriceUpdateCount);
        };
        var service = CreateService();

        var updated = await service.BatchUpdateDetailsAsync(updates);

        Assert.Equal(productCount, updated);
        Assert.Equal(2, warehouseUpdateCount);
        Assert.Equal(2, productUpdateCount);
        Assert.Equal(2, storePriceUpdateCount);
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_门店关系大量补齐按固定分块插入()
    {
        const string productCode = "P-STORE-RELATION-BUDGET";
        await SeedDetailAndProductAsync("D-STORE-RELATION-BUDGET", productCode, "Old English");
        await SeedRelatedPriceRowsAsync(productCode);
        await _localDb.Insertable(
            new ProductSetCode
            {
                SetCodeId = "SET-STORE-RELATION-BUDGET",
                ProductCode = productCode,
                SetProductCode = "CHILD-STORE-RELATION-BUDGET",
                SetItemNumber = "CHILD-STORE-RELATION-BUDGET",
                SetBarcode = "9300000000776",
                SetRetailPrice = 5m,
                SetType = 2,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            Enumerable
                .Range(0, 81)
                .Select(index => new StoreRetailPrice
                {
                    StoreCode = $"B{index:D3}",
                    ProductCode = productCode,
                    PurchasePrice = 1m,
                    StoreRetailPriceValue = 2m,
                    IsActive = true,
                    IsDeleted = false,
                })
                .ToList()
        ).ExecuteCommandAsync();
        var insertCount = 0;
        _localDb.Aop.OnLogExecuting = (sql, _) =>
        {
            if (
                sql.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("StoreMultiCodeProduct", StringComparison.OrdinalIgnoreCase)
            )
            {
                Interlocked.Increment(ref insertCount);
            }
        };
        var service = CreateService();

        var result = await service.BatchUpdateDetailsDetailedAsync(
            "C-TEST",
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "D-STORE-RELATION-BUDGET", 进口价格 = 10m },
            }
        );

        Assert.Empty(result.ValidationErrors);
        Assert.Equal(83, result.AutoRepairedRelationCount);
        Assert.Equal(2, insertCount);
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_补关系校验失败时按固定分块删除本次插入行()
    {
        const string productCode = "P-STORE-RELATION-DELETE-BUDGET";
        await SeedDetailAndProductAsync("D-STORE-RELATION-DELETE-BUDGET", productCode, "Old English");
        await SeedRelatedPriceRowsAsync(productCode);
        await _localDb.Insertable(
            new ProductSetCode
            {
                SetCodeId = "SET-STORE-RELATION-DELETE-BUDGET",
                ProductCode = productCode,
                SetProductCode = "CHILD-STORE-RELATION-DELETE-BUDGET",
                SetItemNumber = "CHILD-STORE-RELATION-DELETE-BUDGET",
                SetBarcode = "9300000000783",
                SetRetailPrice = 5m,
                SetType = 2,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            Enumerable
                .Range(0, 1_001)
                .Select(index => new StoreRetailPrice
                {
                    StoreCode = $"C{index:D4}",
                    ProductCode = productCode,
                    PurchasePrice = 1m,
                    StoreRetailPriceValue = 2m,
                    IsActive = true,
                    IsDeleted = false,
                })
                .ToList()
        ).ExecuteCommandAsync();
        // 模拟插入后才暴露的结构异常，验证只回收本次新增 UUID，且删除不会超出参数预算。
        await _localDb.Ado.ExecuteCommandAsync(
            """
            CREATE TRIGGER store_multi_code_product_force_invalid
            AFTER INSERT ON StoreMultiCodeProduct
            BEGIN
                UPDATE StoreMultiCodeProduct
                SET MultiCodeProductCode = 'BROKEN'
                WHERE UUID = NEW.UUID;
            END;
            """
        );
        var deleteCount = 0;
        _localDb.Aop.OnLogExecuting = (sql, _) =>
        {
            if (
                sql.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("StoreMultiCodeProduct", StringComparison.OrdinalIgnoreCase)
            )
            {
                Interlocked.Increment(ref deleteCount);
            }
        };
        var service = CreateService();

        try
        {
            var result = await service.BatchUpdateDetailsDetailedAsync(
                "C-TEST",
                new List<UpdateContainerDetailDto>
                {
                    new() { HGUID = "D-STORE-RELATION-DELETE-BUDGET", 进口价格 = 10m },
                }
            );

            Assert.Contains(
                result.ValidationErrors,
                error => error.Code == "SET_CHILD_COST_RECALCULATION_INCOMPLETE"
            );
            Assert.Equal(0, result.AutoRepairedRelationCount);
            Assert.Equal(3, deleteCount);
            Assert.Empty(
                await _localDb.Queryable<StoreMultiCodeProduct>()
                    .Where(row => row.ProductCode == productCode)
                    .ToListAsync()
            );
        }
        finally
        {
            await _localDb.Ado.ExecuteCommandAsync(
                "DROP TRIGGER IF EXISTS store_multi_code_product_force_invalid;"
            );
        }
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_同商品贴牌价与上下架冲突_逐字段拒绝且保存无冲突字段()
    {
        const string productCode = "P-CONFLICT-PRICE-ACTIVE";
        await SeedDetailAndProductAsync("D-CONFLICT-A", productCode, "Old English");
        await SeedDetailAsync("D-CONFLICT-B", productCode);
        await SeedRelatedPriceRowsAsync(productCode);
        var service = CreateService();

        var result = await service.BatchUpdateDetailsDetailedAsync(
            "C-TEST",
            new List<UpdateContainerDetailDto>
            {
                new()
                {
                    HGUID = "D-CONFLICT-A",
                    国内价格 = 11m,
                    贴牌价格 = 8m,
                    IsActive = true,
                },
                new()
                {
                    HGUID = "D-CONFLICT-B",
                    国内价格 = 12m,
                    贴牌价格 = 9m,
                    IsActive = false,
                },
            }
        );

        var details = await _localDb.Queryable<ContainerDetail>()
            .Where(row => row.ProductCode == productCode)
            .OrderBy(row => row.DetailCode)
            .ToListAsync();
        var product = await _localDb.Queryable<Product>()
            .SingleAsync(row => row.ProductCode == productCode);
        var warehouse = await _localDb.Queryable<WarehouseProduct>()
            .SingleAsync(row => row.ProductCode == productCode);
        Assert.Equal(new decimal?[] { 11m, 12m }, details.Select(row => row.DomesticPrice));
        Assert.All(details, row => Assert.Null(row.OEMPrice));
        // 上下架字段意图冲突时必须保持每行原值；此测试种子没有设置该字段，原值为 null。
        Assert.All(details, row => Assert.Null(row.IsActive));
        Assert.Equal(2.22m, product.RetailPrice);
        Assert.Equal(2.22m, warehouse.OEMPrice);
        Assert.True(warehouse.IsActive);
        Assert.Equal(4, result.ValidationErrors.Count);
        Assert.Equal(
            new[] { "D-CONFLICT-A", "D-CONFLICT-B" },
            result.ValidationErrors
                .Where(error => error.Code == "CONFLICTING_PRODUCT_OEM_PRICE")
                .Select(error => error.HGUID)
                .OrderBy(value => value)
        );
        Assert.Equal(
            new[] { "D-CONFLICT-A", "D-CONFLICT-B" },
            result.ValidationErrors
                .Where(error => error.Code == "CONFLICTING_PRODUCT_ACTIVE_STATE")
                .Select(error => error.HGUID)
                .OrderBy(value => value)
        );
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_同商品相同贴牌价与上下架意图_合并并全部保存()
    {
        const string productCode = "P-MERGED-PRICE-ACTIVE";
        await SeedDetailAndProductAsync("D-MERGED-A", productCode, "Old English");
        await SeedDetailAsync("D-MERGED-B", productCode);
        await SeedRelatedPriceRowsAsync(productCode);
        var service = CreateService();

        var result = await service.BatchUpdateDetailsDetailedAsync(
            "C-TEST",
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "D-MERGED-A", 贴牌价格 = 8m, IsActive = false },
                new() { HGUID = "D-MERGED-B", 贴牌价格 = 8m, IsActive = false },
            }
        );

        var details = await _localDb.Queryable<ContainerDetail>()
            .Where(row => row.ProductCode == productCode)
            .ToListAsync();
        var product = await _localDb.Queryable<Product>()
            .SingleAsync(row => row.ProductCode == productCode);
        var warehouse = await _localDb.Queryable<WarehouseProduct>()
            .SingleAsync(row => row.ProductCode == productCode);
        Assert.Equal(2, result.TotalUpdated);
        Assert.Empty(result.ValidationErrors);
        Assert.All(details, row => Assert.Equal(8m, row.OEMPrice));
        Assert.All(details, row => Assert.False(row.IsActive));
        Assert.Equal(8m, product.RetailPrice);
        Assert.Equal(8m, warehouse.OEMPrice);
        Assert.False(warehouse.IsActive);
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_缺失Type1和Type2门店关系_自动补齐并重算成本()
    {
        const string productCode = "P-SET-AUTO-REPAIR";
        await SeedDetailAndProductAsync("D-SET-AUTO-REPAIR", productCode, "Old English");
        await SeedRelatedPriceRowsAsync(productCode);
        await _localDb.Updateable<Product>()
            .SetColumns(product => product.IsSpecialProduct == true)
            .Where(product => product.ProductCode == productCode)
            .ExecuteCommandAsync();
        await _localDb.Insertable(
            new List<ProductSetCode>
            {
                new()
                {
                    SetCodeId = "SET-AUTO-TYPE1",
                    ProductCode = productCode,
                    SetProductCode = "CHILD-TYPE1",
                    SetItemNumber = "CHILD-TYPE1",
                    SetBarcode = "9300000000011",
                    SetRetailPrice = 4m,
                    SetType = 1,
                    IsActive = true,
                    IsDeleted = false,
                },
                new()
                {
                    SetCodeId = "SET-AUTO-TYPE2",
                    ProductCode = productCode,
                    SetProductCode = " CHILD-TYPE2 ",
                    SetItemNumber = "CHILD-TYPE2",
                    SetBarcode = "9300000000028",
                    SetRetailPrice = 5m,
                    SetType = 2,
                    IsActive = true,
                    IsDeleted = false,
                },
            }
        ).ExecuteCommandAsync();
        var service = CreateService(
            currentUserService: CreateCurrentUser("user-charles", "charles")
        );

        var result = await service.BatchUpdateDetailsDetailedAsync(
            "C-TEST",
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "D-SET-AUTO-REPAIR", 进口价格 = 10m },
            }
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(row => row.DetailCode == "D-SET-AUTO-REPAIR");
        var product = await _localDb.Queryable<Product>()
            .SingleAsync(row => row.ProductCode == productCode);
        var setRows = await _localDb.Queryable<ProductSetCode>()
            .Where(row => row.ProductCode == productCode)
            .OrderBy(row => row.SetProductCode)
            .ToListAsync();
        var storeRows = await _localDb.Queryable<StoreMultiCodeProduct>()
            .Where(row => row.ProductCode == productCode && row.IsActive && !row.IsDeleted)
            .OrderBy(row => row.StoreCode)
            .OrderBy(row => row.MultiCodeProductCode)
            .ToListAsync();

        Assert.Equal(1, result.TotalUpdated);
        Assert.Empty(result.ValidationErrors);
        Assert.Equal(2, result.AutoRepairedStoreGroupCount);
        Assert.Equal(4, result.AutoRepairedRelationCount);
        Assert.Equal(10m, detail.ImportPrice);
        Assert.Equal(10m, product.PurchasePrice);
        Assert.All(setRows, row => Assert.Equal(10m, row.SetPurchasePrice));
        Assert.Equal(4, storeRows.Count);
        Assert.Equal(
            new[] { "CHILD-TYPE1", "CHILD-TYPE2" },
            storeRows
                .Select(row => row.MultiCodeProductCode!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code)
        );
        Assert.All(storeRows, row =>
        {
            Assert.Equal(10m, row.PurchasePrice);
            Assert.Equal(row.StoreCode + row.MultiCodeProductCode, row.StoreMultiCodeProductCode);
            Assert.True(row.IsSpecialProduct);
            Assert.Equal("charles", row.CreatedBy);
            Assert.Equal("charles", row.UpdatedBy);
        });
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_多个缺失关系商品_同批补齐并统一重算()
    {
        var productPrices = new Dictionary<string, decimal>
        {
            ["P-BATCH-REPAIR-A"] = 8m,
            ["P-BATCH-REPAIR-B"] = 12m,
        };
        foreach (var pair in productPrices)
        {
            var detailCode = $"D-{pair.Key}";
            await SeedDetailAndProductAsync(detailCode, pair.Key, "Old English");
            await SeedRelatedPriceRowsAsync(pair.Key);
            await _localDb.Insertable(
                new ProductSetCode
                {
                    SetCodeId = $"SET-{pair.Key}",
                    ProductCode = pair.Key,
                    SetProductCode = $"CHILD-{pair.Key}",
                    SetItemNumber = $"CHILD-{pair.Key}",
                    SetBarcode = pair.Key == "P-BATCH-REPAIR-A"
                        ? "9300000000080"
                        : "9300000000097",
                    SetRetailPrice = 5m,
                    SetType = 2,
                    IsActive = true,
                    IsDeleted = false,
                }
            ).ExecuteCommandAsync();
        }
        var productSetSelectCount = 0;
        _localDb.Aop.OnLogExecuting = (sql, _) =>
        {
            if (
                sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("ProductSetCode", StringComparison.OrdinalIgnoreCase)
            )
            {
                Interlocked.Increment(ref productSetSelectCount);
            }
        };
        var service = CreateService();

        var result = await service.BatchUpdateDetailsDetailedAsync(
            "C-TEST",
            productPrices
                .Select(pair => new UpdateContainerDetailDto
                {
                    HGUID = $"D-{pair.Key}",
                    进口价格 = pair.Value,
                })
                .ToList()
        );

        Assert.Equal(2, result.TotalUpdated);
        Assert.Empty(result.ValidationErrors);
        Assert.Equal(4, result.AutoRepairedStoreGroupCount);
        Assert.Equal(4, result.AutoRepairedRelationCount);
        Assert.True(
            // 预加载、批量结构校验和后续成本重算各有固定查询；无论商品数多少均不得随商品数增长。
            productSetSelectCount <= 7,
            $"批量补关系后 ProductSetCode 查询应保持常数级，实际 {productSetSelectCount} 次"
        );
        foreach (var pair in productPrices)
        {
            Assert.Equal(
                pair.Value,
                (
                    await _localDb.Queryable<ProductSetCode>()
                        .SingleAsync(row => row.ProductCode == pair.Key)
                ).SetPurchasePrice
            );
            var storeRows = await _localDb.Queryable<StoreMultiCodeProduct>()
                .Where(row => row.ProductCode == pair.Key && row.IsActive && !row.IsDeleted)
                .ToListAsync();
            Assert.Equal(2, storeRows.Count);
            Assert.All(storeRows, row => Assert.Equal(pair.Value, row.PurchasePrice));
        }
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_缺失关系命中软删除墓碑_仅拒绝进口价并保存其它字段和正常行()
    {
        const string unsafeProductCode = "P-SET-TOMBSTONE";
        const string normalProductCode = "P-NORMAL-SAVE";
        await SeedDetailAndProductAsync("D-SET-TOMBSTONE", unsafeProductCode, "Old English");
        await SeedRelatedPriceRowsAsync(unsafeProductCode);
        await SeedDetailAndProductAsync("D-NORMAL-SAVE", normalProductCode, "Old English");
        await SeedRelatedPriceRowsAsync(normalProductCode);
        await _localDb.Insertable(
            new ProductSetCode
            {
                SetCodeId = "SET-TOMBSTONE-TYPE2",
                ProductCode = unsafeProductCode,
                SetProductCode = "CHILD-TOMBSTONE",
                SetItemNumber = "CHILD-TOMBSTONE",
                SetBarcode = "9300000000035",
                SetRetailPrice = 6m,
                SetType = 2,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new StoreMultiCodeProduct
            {
                UUID = "STORE-TOMBSTONE",
                StoreCode = "001",
                ProductCode = unsafeProductCode,
                MultiCodeProductCode = "CHILD-TOMBSTONE",
                StoreMultiCodeProductCode = "001CHILD-TOMBSTONE",
                MultiBarcode = "9300000000035",
                PurchasePrice = 1.11m,
                MultiCodeRetailPrice = 6m,
                IsActive = false,
                IsDeleted = true,
                CreatedBy = "历史操作人",
                UpdatedBy = "历史操作人",
            }
        ).ExecuteCommandAsync();
        var service = CreateService(
            currentUserService: CreateCurrentUser("user-charles", "charles")
        );

        var result = await service.BatchUpdateDetailsDetailedAsync(
            "C-TEST",
            new List<UpdateContainerDetailDto>
            {
                new()
                {
                    HGUID = "D-SET-TOMBSTONE",
                    进口价格 = 8.88m,
                    贴牌价格 = 9.99m,
                },
                new() { HGUID = "D-NORMAL-SAVE", 进口价格 = 7.77m },
            }
        );

        var unsafeDetail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(row => row.DetailCode == "D-SET-TOMBSTONE");
        var unsafeProduct = await _localDb.Queryable<Product>()
            .SingleAsync(row => row.ProductCode == unsafeProductCode);
        var unsafeWarehouse = await _localDb.Queryable<WarehouseProduct>()
            .SingleAsync(row => row.ProductCode == unsafeProductCode);
        var unsafeStorePrices = await _localDb.Queryable<StoreRetailPrice>()
            .Where(row => row.ProductCode == unsafeProductCode)
            .ToListAsync();
        var tombstone = await _localDb.Queryable<StoreMultiCodeProduct>()
            .SingleAsync(row => row.UUID == "STORE-TOMBSTONE");
        var unsafeActiveProjectionCount = await _localDb.Queryable<StoreMultiCodeProduct>()
            .CountAsync(row =>
                row.ProductCode == unsafeProductCode && row.IsActive && !row.IsDeleted
            );
        var normalDetail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(row => row.DetailCode == "D-NORMAL-SAVE");
        var normalProduct = await _localDb.Queryable<Product>()
            .SingleAsync(row => row.ProductCode == normalProductCode);

        Assert.Equal(2, result.TotalUpdated);
        var error = Assert.Single(result.ValidationErrors);
        Assert.Equal("D-SET-TOMBSTONE", error.HGUID);
        Assert.Equal("进口价格", error.Field);
        Assert.Equal("SET_CHILD_STORE_RELATION_TOMBSTONED", error.Code);
        Assert.Equal(0, result.AutoRepairedStoreGroupCount);
        Assert.Equal(0, result.AutoRepairedRelationCount);
        Assert.Equal(1.23m, unsafeDetail.ImportPrice);
        Assert.Equal(9.99m, unsafeDetail.OEMPrice);
        Assert.Equal(1.11m, unsafeProduct.PurchasePrice);
        Assert.Equal(9.99m, unsafeProduct.RetailPrice);
        Assert.Equal(1.11m, unsafeWarehouse.ImportPrice);
        Assert.Equal(9.99m, unsafeWarehouse.OEMPrice);
        Assert.All(unsafeStorePrices, row => Assert.Equal(1.11m, row.PurchasePrice));
        Assert.True(tombstone.IsDeleted);
        Assert.False(tombstone.IsActive);
        Assert.Equal("历史操作人", tombstone.UpdatedBy);
        Assert.Equal(0, unsafeActiveProjectionCount);
        Assert.Equal(7.77m, normalDetail.ImportPrice);
        Assert.Equal(7.77m, normalProduct.PurchasePrice);
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_Type1关系完整但子项零售价为零_仅拒绝进口价并保存其它字段和正常行()
    {
        const string unsafeProductCode = "P-SET-ZERO-RETAIL";
        const string normalProductCode = "P-ZERO-RETAIL-NORMAL";
        await SeedDetailAndProductAsync("D-SET-ZERO-RETAIL", unsafeProductCode, "Old English");
        await SeedRelatedPriceRowsAsync(unsafeProductCode);
        await SeedDetailAndProductAsync("D-ZERO-RETAIL-NORMAL", normalProductCode, "Old English");
        await SeedRelatedPriceRowsAsync(normalProductCode);
        await _localDb.Insertable(
            new ProductSetCode
            {
                SetCodeId = "SET-ZERO-RETAIL-TYPE1",
                ProductCode = unsafeProductCode,
                SetProductCode = "CHILD-ZERO-RETAIL",
                SetItemNumber = "CHILD-ZERO-RETAIL",
                SetBarcode = "9300000000059",
                SetRetailPrice = 0m,
                SetType = 1,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new[] { "001", "002" }.Select(storeCode => new StoreMultiCodeProduct
            {
                UUID = $"STORE-ZERO-RETAIL-{storeCode}",
                StoreCode = storeCode,
                ProductCode = unsafeProductCode,
                MultiCodeProductCode = "CHILD-ZERO-RETAIL",
                StoreMultiCodeProductCode = storeCode + "CHILD-ZERO-RETAIL",
                MultiBarcode = "9300000000059",
                PurchasePrice = 1.11m,
                MultiCodeRetailPrice = 0m,
                IsActive = true,
                IsDeleted = false,
            }).ToList()
        ).ExecuteCommandAsync();
        var service = CreateService();

        var result = await service.BatchUpdateDetailsDetailedAsync(
            "C-TEST",
            new List<UpdateContainerDetailDto>
            {
                new()
                {
                    HGUID = "D-SET-ZERO-RETAIL",
                    进口价格 = 8.88m,
                    贴牌价格 = 9.99m,
                },
                new() { HGUID = "D-ZERO-RETAIL-NORMAL", 进口价格 = 7.77m },
            }
        );

        var unsafeDetail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(row => row.DetailCode == "D-SET-ZERO-RETAIL");
        var unsafeProduct = await _localDb.Queryable<Product>()
            .SingleAsync(row => row.ProductCode == unsafeProductCode);
        var normalDetail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(row => row.DetailCode == "D-ZERO-RETAIL-NORMAL");
        var normalProduct = await _localDb.Queryable<Product>()
            .SingleAsync(row => row.ProductCode == normalProductCode);

        Assert.Equal(2, result.TotalUpdated);
        var error = Assert.Single(result.ValidationErrors);
        Assert.Equal("D-SET-ZERO-RETAIL", error.HGUID);
        Assert.Equal("进口价格", error.Field);
        Assert.Equal("SET_CHILD_COST_RECALCULATION_INCOMPLETE", error.Code);
        Assert.Equal(1.23m, unsafeDetail.ImportPrice);
        Assert.Equal(9.99m, unsafeDetail.OEMPrice);
        Assert.Equal(1.11m, unsafeProduct.PurchasePrice);
        Assert.Equal(9.99m, unsafeProduct.RetailPrice);
        Assert.Equal(7.77m, normalDetail.ImportPrice);
        Assert.Equal(7.77m, normalProduct.PurchasePrice);
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_门店子项组合业务键超过列长度_仅拒绝进口价并保存其它字段()
    {
        const string productCode = "P-SET-LONG-STORE-KEY";
        var childCode = new string('C', 49);
        await SeedDetailAndProductAsync("D-SET-LONG-STORE-KEY", productCode, "Old English");
        await SeedRelatedPriceRowsAsync(productCode);
        await _localDb.Insertable(
            new ProductSetCode
            {
                SetCodeId = "SET-LONG-STORE-KEY-TYPE2",
                ProductCode = productCode,
                SetProductCode = childCode,
                SetItemNumber = childCode,
                SetBarcode = "9300000000073",
                SetRetailPrice = 6m,
                SetType = 2,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        var service = CreateService();

        var result = await service.BatchUpdateDetailsDetailedAsync(
            "C-TEST",
            new List<UpdateContainerDetailDto>
            {
                new()
                {
                    HGUID = "D-SET-LONG-STORE-KEY",
                    进口价格 = 8.88m,
                    贴牌价格 = 9.99m,
                },
            }
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(row => row.DetailCode == "D-SET-LONG-STORE-KEY");
        var product = await _localDb.Queryable<Product>()
            .SingleAsync(row => row.ProductCode == productCode);

        Assert.Equal(1, result.TotalUpdated);
        var error = Assert.Single(result.ValidationErrors);
        Assert.Equal("D-SET-LONG-STORE-KEY", error.HGUID);
        Assert.Equal("进口价格", error.Field);
        Assert.Equal("SET_CHILD_STORE_RELATION_INVALID", error.Code);
        Assert.Equal(1.23m, detail.ImportPrice);
        Assert.Equal(9.99m, detail.OEMPrice);
        Assert.Equal(1.11m, product.PurchasePrice);
        Assert.Equal(9.99m, product.RetailPrice);
        Assert.Equal(
            0,
            await _localDb.Queryable<StoreMultiCodeProduct>()
                .CountAsync(row => row.ProductCode == productCode)
        );
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_总部Type1仅剩历史关系但门店仍有活跃子项_仅拒绝进口价()
    {
        const string productCode = "P-HISTORICAL-TYPE1-ORPHAN";
        await SeedDetailAndProductAsync("D-HISTORICAL-TYPE1-ORPHAN", productCode, "Old English");
        await SeedRelatedPriceRowsAsync(productCode);
        await _localDb.Insertable(
            new ProductSetCode
            {
                SetCodeId = "SET-HISTORICAL-TYPE1",
                ProductCode = productCode,
                SetProductCode = "CHILD-HISTORICAL-TYPE1",
                SetItemNumber = "CHILD-HISTORICAL-TYPE1",
                SetBarcode = "9300000000066",
                SetRetailPrice = 6m,
                SetType = 1,
                IsActive = false,
                IsDeleted = true,
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new StoreMultiCodeProduct
            {
                UUID = "STORE-HISTORICAL-TYPE1-ORPHAN",
                StoreCode = "001",
                ProductCode = productCode,
                MultiCodeProductCode = "CHILD-HISTORICAL-TYPE1",
                StoreMultiCodeProductCode = "001CHILD-HISTORICAL-TYPE1",
                MultiBarcode = "9300000000066",
                PurchasePrice = 1.11m,
                MultiCodeRetailPrice = 6m,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        var service = CreateService();

        var result = await service.BatchUpdateDetailsDetailedAsync(
            "C-TEST",
            new List<UpdateContainerDetailDto>
            {
                new()
                {
                    HGUID = "D-HISTORICAL-TYPE1-ORPHAN",
                    进口价格 = 8.88m,
                    贴牌价格 = 9.99m,
                },
            }
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(row => row.DetailCode == "D-HISTORICAL-TYPE1-ORPHAN");
        var product = await _localDb.Queryable<Product>()
            .SingleAsync(row => row.ProductCode == productCode);
        var storeProjection = await _localDb.Queryable<StoreMultiCodeProduct>()
            .SingleAsync(row => row.UUID == "STORE-HISTORICAL-TYPE1-ORPHAN");

        Assert.Equal(1, result.TotalUpdated);
        var error = Assert.Single(result.ValidationErrors);
        Assert.Equal("D-HISTORICAL-TYPE1-ORPHAN", error.HGUID);
        Assert.Equal("进口价格", error.Field);
        Assert.Equal("SET_CHILD_STORE_RELATION_INVALID", error.Code);
        Assert.Equal(1.23m, detail.ImportPrice);
        Assert.Equal(9.99m, detail.OEMPrice);
        Assert.Equal(1.11m, product.PurchasePrice);
        Assert.Equal(9.99m, product.RetailPrice);
        Assert.True(storeProjection.IsActive);
        Assert.False(storeProjection.IsDeleted);
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_缺失多码门店关系_旧入口保持严格回滚语义()
    {
        const string productCode = "P-STRICT-MULTI-CODE";
        await SeedDetailAndProductAsync("D-STRICT-MULTI-CODE", productCode, "Old English");
        await SeedRelatedPriceRowsAsync(productCode);
        await _localDb.Insertable(
            new ProductSetCode
            {
                SetCodeId = "SET-STRICT-TYPE2",
                ProductCode = productCode,
                SetProductCode = "CHILD-STRICT-TYPE2",
                SetItemNumber = "CHILD-STRICT-TYPE2",
                SetBarcode = "9300000000042",
                SetRetailPrice = 6m,
                SetType = 2,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.BatchUpdateDetailsAsync(
                new List<UpdateContainerDetailDto>
                {
                    new()
                    {
                        HGUID = "D-STRICT-MULTI-CODE",
                        进口价格 = 8.88m,
                        贴牌价格 = 9.99m,
                    },
                }
            )
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(row => row.DetailCode == "D-STRICT-MULTI-CODE");
        var product = await _localDb.Queryable<Product>()
            .SingleAsync(row => row.ProductCode == productCode);
        var warehouse = await _localDb.Queryable<WarehouseProduct>()
            .SingleAsync(row => row.ProductCode == productCode);
        var storePrices = await _localDb.Queryable<StoreRetailPrice>()
            .Where(row => row.ProductCode == productCode)
            .ToListAsync();

        Assert.Contains("套装子项成本无法完整重算", exception.Message);
        Assert.Equal(1.23m, detail.ImportPrice);
        Assert.Null(detail.OEMPrice);
        Assert.Equal(1.11m, product.PurchasePrice);
        Assert.Equal(2.22m, product.RetailPrice);
        Assert.Equal(1.11m, warehouse.ImportPrice);
        Assert.Equal(2.22m, warehouse.OEMPrice);
        Assert.All(storePrices, row => Assert.Equal(1.11m, row.PurchasePrice));
        Assert.Equal(
            0,
            await _localDb.Queryable<StoreMultiCodeProduct>()
                .CountAsync(row => row.ProductCode == productCode && !row.IsDeleted)
        );
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_仅有DomesticProduct_保持旧入口英文名称和价格更新兼容()
    {
        await SeedDetailAndProductAsync("D-PRICE-EN", "P-PRICE-EN", englishName: "Old English");
        var service = CreateService();

        var totalUpdated = await service.BatchUpdateDetailsAsync(
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "D-PRICE-EN", 进口价格 = 3.45m, 英文名称 = "Translated Name" },
            }
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-PRICE-EN");
        var product = await _localDb.Queryable<DomesticProduct>()
            .SingleAsync(x => x.ProductCode == "P-PRICE-EN");
        Assert.Equal(1, totalUpdated);
        Assert.Equal(3.45m, detail.ImportPrice);
        Assert.Equal("Translated Name", product.EnglishProductName);
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_英文名称仍含中文_不覆盖DomesticProduct但保留其它明细更新()
    {
        await SeedDetailAndProductAsync("D-MIXED-EN", "P-MIXED-EN", englishName: "Old English");
        var service = CreateService();

        var totalUpdated = await service.BatchUpdateDetailsAsync(
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "D-MIXED-EN", 进口价格 = 4.56m, 英文名称 = "Large 草莓" },
            }
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-MIXED-EN");
        var product = await _localDb.Queryable<DomesticProduct>()
            .SingleAsync(x => x.ProductCode == "P-MIXED-EN");
        Assert.Equal(1, totalUpdated);
        Assert.Equal(4.56m, detail.ImportPrice);
        Assert.Equal("Old English", product.EnglishProductName);
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_英文名称为中文_应跳过且不翻译不覆盖()
    {
        await SeedDetailAndProductAsync("D-ZH-EN", "P-ZH-EN", englishName: "Old English");
        await SeedLocalProductAsync("P-ZH-EN", productName: "旧中文名", englishName: "Old Local English");
        var service = CreateService();

        var totalUpdated = await service.BatchUpdateDetailsAsync(
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "D-ZH-EN", 英文名称 = "草莓玩具" },
            }
        );

        var product = await _localDb.Queryable<DomesticProduct>()
            .SingleAsync(x => x.ProductCode == "P-ZH-EN");
        var localProduct = await _localDb.Queryable<Product>()
            .SingleAsync(x => x.ProductCode == "P-ZH-EN");
        Assert.Equal(0, totalUpdated);
        Assert.Equal("Old English", product.EnglishProductName);
        Assert.Equal("旧中文名", localProduct.ProductName);
        Assert.Equal("Old Local English", localProduct.EnglishName);
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_国内价格和贴牌价格变化_应更新货柜明细()
    {
        await SeedDetailAndProductAsync("D-DOMESTIC-OEM", "P-DOMESTIC-OEM", englishName: "Old English");
        var service = CreateService();

        var totalUpdated = await service.BatchUpdateDetailsAsync(
            new List<UpdateContainerDetailDto>
            {
                new()
                {
                    HGUID = "D-DOMESTIC-OEM",
                    国内价格 = 11.60m,
                    贴牌价格 = 6.99m,
                },
            }
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-DOMESTIC-OEM");

        Assert.Equal(1, totalUpdated);
        Assert.Equal(11.60m, detail.DomesticPrice);
        Assert.Equal(6.99m, detail.OEMPrice);
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_装箱体积和统计字段变化_应更新货柜明细()
    {
        await SeedDetailAndProductAsync("D-PACKING-VOLUME", "P-PACKING-VOLUME", englishName: "Old English");
        var service = CreateService();

        var totalUpdated = await service.BatchUpdateDetailsAsync(
            new List<UpdateContainerDetailDto>
            {
                new()
                {
                    HGUID = "D-PACKING-VOLUME",
                    单件装箱数 = 48m,
                    单件体积 = 0.118m,
                    装柜数量 = 96m,
                    合计装柜体积 = 0.236m,
                    合计装柜金额 = 1336.32m,
                },
            }
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-PACKING-VOLUME");

        Assert.Equal(1, totalUpdated);
        Assert.Equal(48m, detail.PackingQuantity);
        Assert.Equal(0.118m, detail.UnitVolume);
        Assert.Equal(96m, detail.LoadingQuantity);
        Assert.Equal(0.236m, detail.TotalVolume);
        Assert.Equal(1336.32m, detail.TotalAmount);
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_商品名称缺少关联国内商品_返回字段错误且不计成功()
    {
        await SeedDetailAsync("D-NAME-NO-DOMESTIC", "P-NAME-NO-DOMESTIC");
        var service = CreateService();

        var result = await service.BatchUpdateDetailsDetailedAsync(
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "D-NAME-NO-DOMESTIC", 商品名称 = "新商品名称" },
            }
        );

        Assert.Equal(0, result.TotalUpdated);
        var error = Assert.Single(result.ValidationErrors);
        Assert.Equal("D-NAME-NO-DOMESTIC", error.HGUID);
        Assert.Equal("商品名称", error.Field);
        Assert.Equal("RELATED_PRODUCT_NOT_FOUND", error.Code);
        Assert.Equal("关联国内商品不存在，无法更新商品名称", error.Message);
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_中包数缺少关联国内商品_返回字段错误且不计成功()
    {
        await SeedDetailAsync("D-MIDDLE-NO-DOMESTIC", "P-MIDDLE-NO-DOMESTIC");
        var service = CreateService();

        var result = await service.BatchUpdateDetailsDetailedAsync(
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "D-MIDDLE-NO-DOMESTIC", 中包数 = 12m },
            }
        );

        Assert.Equal(0, result.TotalUpdated);
        var error = Assert.Single(result.ValidationErrors);
        Assert.Equal("D-MIDDLE-NO-DOMESTIC", error.HGUID);
        Assert.Equal("中包数", error.Field);
        Assert.Equal("RELATED_PRODUCT_NOT_FOUND", error.Code);
        Assert.Equal("关联国内商品不存在，无法更新中包数", error.Message);
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_名称和中包数都缺少关联国内商品_分别返回字段错误()
    {
        await SeedDetailAsync("D-RELATED-BOTH-MISSING", "P-RELATED-BOTH-MISSING");
        var service = CreateService();

        var result = await service.BatchUpdateDetailsDetailedAsync(
            new List<UpdateContainerDetailDto>
            {
                new()
                {
                    HGUID = "D-RELATED-BOTH-MISSING",
                    商品名称 = "新商品名称",
                    中包数 = 15m,
                },
            }
        );

        Assert.Equal(0, result.TotalUpdated);
        Assert.Collection(
            result.ValidationErrors.OrderBy(error => error.Field),
            error =>
            {
                Assert.Equal("D-RELATED-BOTH-MISSING", error.HGUID);
                Assert.Equal("中包数", error.Field);
                Assert.Equal("RELATED_PRODUCT_NOT_FOUND", error.Code);
            },
            error =>
            {
                Assert.Equal("D-RELATED-BOTH-MISSING", error.HGUID);
                Assert.Equal("商品名称", error.Field);
                Assert.Equal("RELATED_PRODUCT_NOT_FOUND", error.Code);
            }
        );
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_关联字段失败时_其它明细字段仍按部分成功保存()
    {
        await SeedDetailAsync("D-RELATED-PARTIAL", "P-RELATED-PARTIAL");
        var service = CreateService();

        var result = await service.BatchUpdateDetailsDetailedAsync(
            new List<UpdateContainerDetailDto>
            {
                new()
                {
                    HGUID = "D-RELATED-PARTIAL",
                    商品名称 = "新商品名称",
                    中包数 = 18m,
                    备注 = "其它字段仍应保存",
                },
            }
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(item => item.DetailCode == "D-RELATED-PARTIAL");
        Assert.Equal(1, result.TotalUpdated);
        Assert.Equal(2, result.ValidationErrors.Count);
        Assert.All(
            result.ValidationErrors,
            error => Assert.Equal("RELATED_PRODUCT_NOT_FOUND", error.Code)
        );
        Assert.Equal("其它字段仍应保存", detail.Remarks);
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_已匹配商品中包数变化_应同步仓库和国内中包数且不改明细装箱数()
    {
        await SeedDetailAndProductAsync("D-MIN-ORDER", "P-MIN-ORDER", englishName: "Old English", middlePackQuantity: 6);
        await SeedRelatedPriceRowsAsync("P-MIN-ORDER", minOrderQuantity: 6, packingQuantity: 24);
        var service = CreateService();

        var totalUpdated = await service.BatchUpdateDetailsAsync(
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "D-MIN-ORDER", 中包数 = 12m },
            }
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-MIN-ORDER");
        var warehouseProduct = await _localDb.Queryable<WarehouseProduct>()
            .SingleAsync(x => x.ProductCode == "P-MIN-ORDER");
        var domesticProduct = await _localDb.Queryable<DomesticProduct>()
            .SingleAsync(x => x.ProductCode == "P-MIN-ORDER");

        Assert.Equal(1, totalUpdated);
        Assert.Null(detail.PackingQuantity);
        Assert.Equal(12, warehouseProduct.MinOrderQuantity);
        Assert.Equal(24, warehouseProduct.PackingQuantity);
        Assert.Equal(12, domesticProduct.MiddlePackQuantity);
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_未匹配商品中包数变化_应只更新国内中包数且不创建仓库商品()
    {
        await SeedDetailAndProductAsync("D-MIN-UNMATCHED", "P-MIN-UNMATCHED", englishName: "Old English", middlePackQuantity: 6);
        var service = CreateService();

        var totalUpdated = await service.BatchUpdateDetailsAsync(
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "D-MIN-UNMATCHED", 中包数 = 14m },
            }
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-MIN-UNMATCHED");
        var domesticProduct = await _localDb.Queryable<DomesticProduct>()
            .SingleAsync(x => x.ProductCode == "P-MIN-UNMATCHED");
        var warehouseProductCount = await _localDb.Queryable<WarehouseProduct>()
            .Where(x => x.ProductCode == "P-MIN-UNMATCHED")
            .CountAsync();

        Assert.Equal(1, totalUpdated);
        Assert.Null(detail.PackingQuantity);
        Assert.Equal(14, domesticProduct.MiddlePackQuantity);
        Assert.Equal(0, warehouseProductCount);
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_统计字段变化_应同步刷新货柜主表汇总()
    {
        await _localDb.Insertable(
            new Container
            {
                ContainerCode = "C-SUMMARY",
                ContainerNumber = "C-SUMMARY",
                TotalPieces = 99m,
                TotalQuantity = 99m,
                TotalAmount = 99m,
                TotalVolume = 99m,
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new List<ContainerDetail>
            {
                new()
                {
                    DetailCode = "D-SUMMARY-1",
                    ContainerCode = "C-SUMMARY",
                    ProductCode = "P-SUMMARY-1",
                    LoadingPieces = 2m,
                    LoadingQuantity = 20m,
                    TotalAmount = 100m,
                    TotalVolume = 0.5m,
                    IsDeleted = false,
                },
                new()
                {
                    DetailCode = "D-SUMMARY-2",
                    ContainerCode = "C-SUMMARY",
                    ProductCode = "P-SUMMARY-2",
                    LoadingPieces = 3m,
                    LoadingQuantity = 30m,
                    TotalAmount = 150m,
                    TotalVolume = 0.75m,
                    IsDeleted = false,
                },
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new DomesticProduct
            {
                ProductCode = "P-SUMMARY-1",
                HBProductNo = "P-SUMMARY-1",
                ProductName = "汇总商品",
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await SeedRelatedPriceRowsAsync("P-SUMMARY-1");
        var service = CreateService();

        var totalUpdated = await service.BatchUpdateDetailsAsync(
            new List<UpdateContainerDetailDto>
            {
                new()
                {
                    HGUID = "D-SUMMARY-1",
                    装柜数量 = 48m,
                    合计装柜体积 = 0.66m,
                    合计装柜金额 = 464.64m,
                    进口价格 = 2.10m,
                    SkipRelatedProductSync = true,
                },
            }
        );

        var container = await _localDb.Queryable<Container>()
            .SingleAsync(x => x.ContainerCode == "C-SUMMARY");
        var warehouseProduct = await _localDb.Queryable<WarehouseProduct>()
            .SingleAsync(x => x.ProductCode == "P-SUMMARY-1");

        Assert.Equal(1, totalUpdated);
        Assert.Equal(5m, container.TotalPieces);
        Assert.Equal(78m, container.TotalQuantity);
        Assert.Equal(614.64m, container.TotalAmount);
        Assert.Equal(1.41m, container.TotalVolume);
        Assert.Equal(1.11m, warehouseProduct.ImportPrice);
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_价格贴牌和上下架变化_应同步进货价但不写分店零售价()
    {
        await SeedDetailAndProductAsync("D-SYNC-PRICE", "P-SYNC-PRICE", englishName: "Old English");
        await SeedRelatedPriceRowsAsync("P-SYNC-PRICE");
        var service = CreateService();

        var totalUpdated = await service.BatchUpdateDetailsAsync(
            new List<UpdateContainerDetailDto>
            {
                new()
                {
                    HGUID = "D-SYNC-PRICE",
                    进口价格 = 8.88m,
                    贴牌价格 = 9.99m,
                    IsActive = false,
                },
            }
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-SYNC-PRICE");
        var warehouseProduct = await _localDb.Queryable<WarehouseProduct>()
            .SingleAsync(x => x.ProductCode == "P-SYNC-PRICE");
        var product = await _localDb.Queryable<Product>()
            .SingleAsync(x => x.ProductCode == "P-SYNC-PRICE");
        var storeRetailPrices = await _localDb.Queryable<StoreRetailPrice>()
            .Where(x => x.ProductCode == "P-SYNC-PRICE")
            .ToListAsync();

        Assert.Equal(1, totalUpdated);
        Assert.Equal(8.88m, detail.ImportPrice);
        Assert.Equal(9.99m, detail.OEMPrice);
        Assert.False(detail.IsActive);
        Assert.Equal(8.88m, warehouseProduct.ImportPrice);
        Assert.Equal(9.99m, warehouseProduct.OEMPrice);
        Assert.False(warehouseProduct.IsActive);
        Assert.Equal(8.88m, product.PurchasePrice);
        Assert.Equal(9.99m, product.RetailPrice);
        Assert.All(storeRetailPrices, row => Assert.Equal(8.88m, row.PurchasePrice));
        Assert.All(storeRetailPrices, row => Assert.Equal(2.22m, row.StoreRetailPriceValue));
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_明细价格未变化_仍应同步已有商品关联价格()
    {
        await SeedDetailAndProductAsync("D-SYNC-SAME-PRICE", "P-SYNC-SAME-PRICE", englishName: "Old English");
        await SeedRelatedPriceRowsAsync("P-SYNC-SAME-PRICE");
        await _localDb.Updateable<ContainerDetail>()
            .SetColumns(x => x.ImportPrice == 8.88m)
            .SetColumns(x => x.OEMPrice == 9.99m)
            .SetColumns(x => x.IsActive == false)
            .Where(x => x.DetailCode == "D-SYNC-SAME-PRICE")
            .ExecuteCommandAsync();
        var service = CreateService();

        var totalUpdated = await service.BatchUpdateDetailsAsync(
            new List<UpdateContainerDetailDto>
            {
                new()
                {
                    HGUID = "D-SYNC-SAME-PRICE",
                    进口价格 = 8.88m,
                    贴牌价格 = 9.99m,
                    IsActive = false,
                },
            }
        );

        var warehouseProduct = await _localDb.Queryable<WarehouseProduct>()
            .SingleAsync(x => x.ProductCode == "P-SYNC-SAME-PRICE");
        var product = await _localDb.Queryable<Product>()
            .SingleAsync(x => x.ProductCode == "P-SYNC-SAME-PRICE");
        var storeRetailPrices = await _localDb.Queryable<StoreRetailPrice>()
            .Where(x => x.ProductCode == "P-SYNC-SAME-PRICE")
            .ToListAsync();

        Assert.Equal(1, totalUpdated);
        Assert.Equal(8.88m, warehouseProduct.ImportPrice);
        Assert.Equal(9.99m, warehouseProduct.OEMPrice);
        Assert.False(warehouseProduct.IsActive);
        Assert.Equal(8.88m, product.PurchasePrice);
        Assert.Equal(9.99m, product.RetailPrice);
        Assert.All(storeRetailPrices, row => Assert.Equal(8.88m, row.PurchasePrice));
        Assert.All(storeRetailPrices, row => Assert.Equal(2.22m, row.StoreRetailPriceValue));
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_本地Product不存在_应只更新货柜明细且不回填仓库价格()
    {
        await SeedDetailAndProductAsync("D-NEW-PRICE", "P-NEW-PRICE", englishName: "New English");
        await _localDb.Insertable(
            new WarehouseProduct
            {
                ProductCode = "P-NEW-PRICE",
                ImportPrice = 1.11m,
                OEMPrice = 2.22m,
                IsActive = true,
            }
        ).ExecuteCommandAsync();
        var service = CreateService();

        var totalUpdated = await service.BatchUpdateDetailsAsync(
            new List<UpdateContainerDetailDto>
            {
                new()
                {
                    HGUID = "D-NEW-PRICE",
                    进口价格 = 8.88m,
                    贴牌价格 = 9.99m,
                },
            }
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-NEW-PRICE");
        var warehouseProduct = await _localDb.Queryable<WarehouseProduct>()
            .SingleAsync(x => x.ProductCode == "P-NEW-PRICE");
        var localProductCount = await _localDb.Queryable<Product>()
            .Where(x => x.ProductCode == "P-NEW-PRICE")
            .CountAsync();
        var storeRetailPriceCount = await _localDb.Queryable<StoreRetailPrice>()
            .Where(x => x.ProductCode == "P-NEW-PRICE")
            .CountAsync();

        Assert.Equal(1, totalUpdated);
        Assert.Equal(8.88m, detail.ImportPrice);
        Assert.Equal(9.99m, detail.OEMPrice);
        Assert.Equal(1.11m, warehouseProduct.ImportPrice);
        Assert.Equal(2.22m, warehouseProduct.OEMPrice);
        Assert.Equal(0, localProductCount);
        Assert.Equal(0, storeRetailPriceCount);
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_跳过关联同步_应阻止名称中包数和分类回填()
    {
        await SeedDetailAndProductAsync(
            "D-SKIP-MASTER-DATA",
            "P-SKIP-MASTER-DATA",
            englishName: "Old English",
            middlePackQuantity: 12
        );
        await SeedRelatedPriceRowsAsync("P-SKIP-MASTER-DATA", minOrderQuantity: 12);
        await SeedWarehouseCategoryAsync("CAT-SKIP-MASTER-DATA");
        var service = CreateService();

        var totalUpdated = await service.BatchUpdateDetailsAsync(
            new List<UpdateContainerDetailDto>
            {
                new()
                {
                    HGUID = "D-SKIP-MASTER-DATA",
                    商品名称 = "新商品名",
                    英文名称 = "New English",
                    中包数 = 24,
                    ProductCategoryGUID = "CAT-SKIP-MASTER-DATA",
                    SkipRelatedProductSync = true,
                },
            }
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-SKIP-MASTER-DATA");
        var domesticProduct = await _localDb.Queryable<DomesticProduct>()
            .SingleAsync(x => x.ProductCode == "P-SKIP-MASTER-DATA");
        var warehouseProduct = await _localDb.Queryable<WarehouseProduct>()
            .SingleAsync(x => x.ProductCode == "P-SKIP-MASTER-DATA");
        var product = await _localDb.Queryable<Product>()
            .SingleAsync(x => x.ProductCode == "P-SKIP-MASTER-DATA");

        Assert.Equal(1, totalUpdated);
        Assert.Equal("CAT-SKIP-MASTER-DATA", detail.TargetWarehouseCategoryGUID);
        Assert.Equal("商品 P-SKIP-MASTER-DATA", domesticProduct.ProductName);
        Assert.Equal("Old English", domesticProduct.EnglishProductName);
        Assert.Equal(12, domesticProduct.MiddlePackQuantity);
        Assert.Equal(12, warehouseProduct.MinOrderQuantity);
        Assert.Null(product.WarehouseCategoryGUID);
        Assert.Equal("本地商品 P-SKIP-MASTER-DATA", product.ProductName);
        Assert.Null(product.EnglishName);
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_跳过关联同步_应只更新货柜明细()
    {
        await SeedDetailAndProductAsync("D-SKIP-SYNC", "P-SKIP-SYNC", englishName: "Old English");
        await SeedRelatedPriceRowsAsync("P-SKIP-SYNC");
        var service = CreateService();

        var totalUpdated = await service.BatchUpdateDetailsAsync(
            new List<UpdateContainerDetailDto>
            {
                new()
                {
                    HGUID = "D-SKIP-SYNC",
                    进口价格 = 8.88m,
                    贴牌价格 = 9.99m,
                    IsActive = false,
                    SkipRelatedProductSync = true,
                },
            }
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-SKIP-SYNC");
        var warehouseProduct = await _localDb.Queryable<WarehouseProduct>()
            .SingleAsync(x => x.ProductCode == "P-SKIP-SYNC");
        var product = await _localDb.Queryable<Product>()
            .SingleAsync(x => x.ProductCode == "P-SKIP-SYNC");
        var storeRetailPrices = await _localDb.Queryable<StoreRetailPrice>()
            .Where(x => x.ProductCode == "P-SKIP-SYNC")
            .ToListAsync();

        Assert.Equal(1, totalUpdated);
        Assert.Equal(8.88m, detail.ImportPrice);
        Assert.Equal(9.99m, detail.OEMPrice);
        Assert.False(detail.IsActive);
        Assert.Equal(1.11m, warehouseProduct.ImportPrice);
        Assert.Equal(2.22m, warehouseProduct.OEMPrice);
        Assert.True(warehouseProduct.IsActive);
        Assert.Equal(1.11m, product.PurchasePrice);
        Assert.Equal(2.22m, product.RetailPrice);
        Assert.All(storeRetailPrices, row => Assert.Equal(1.11m, row.PurchasePrice));
        Assert.All(storeRetailPrices, row => Assert.Equal(2.22m, row.StoreRetailPriceValue));
    }

    [Fact]
    public async Task ApplyPricesByScopeAsync_仅修改进货价_不应同步旧零售价()
    {
        await SeedDetailAndProductAsync(
            "D-APPLY-IMPORT-ONLY",
            "P-APPLY-IMPORT-ONLY",
            englishName: "Old English"
        );
        await _localDb.Updateable<ContainerDetail>()
            .SetColumns(x => x.OEMPrice == 7.77m)
            .Where(x => x.DetailCode == "D-APPLY-IMPORT-ONLY")
            .ExecuteCommandAsync();
        await SeedRelatedPriceRowsAsync("P-APPLY-IMPORT-ONLY");
        var service = CreateService();

        var totalUpdated = await service.ApplyPricesByScopeAsync(
            "C-TEST",
            new ContainerDetailApplyPricesRequestDto
            {
                ImportPrice = 8.88m,
                SelectedHguids = new List<string> { "D-APPLY-IMPORT-ONLY" },
            }
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-APPLY-IMPORT-ONLY");
        var warehouseProduct = await _localDb.Queryable<WarehouseProduct>()
            .SingleAsync(x => x.ProductCode == "P-APPLY-IMPORT-ONLY");
        var product = await _localDb.Queryable<Product>()
            .SingleAsync(x => x.ProductCode == "P-APPLY-IMPORT-ONLY");
        var storeRetailPrices = await _localDb.Queryable<StoreRetailPrice>()
            .Where(x => x.ProductCode == "P-APPLY-IMPORT-ONLY")
            .ToListAsync();

        Assert.Equal(1, totalUpdated);
        Assert.Equal(8.88m, detail.ImportPrice);
        Assert.Equal(7.77m, detail.OEMPrice);
        Assert.Equal(8.88m, warehouseProduct.ImportPrice);
        Assert.Equal(2.22m, warehouseProduct.OEMPrice);
        Assert.Equal(8.88m, product.PurchasePrice);
        Assert.Equal(2.22m, product.RetailPrice);
        Assert.All(storeRetailPrices, row => Assert.Equal(8.88m, row.PurchasePrice));
        Assert.All(storeRetailPrices, row => Assert.Equal(2.22m, row.StoreRetailPriceValue));
    }

    [Fact]
    public async Task ApplyPricesByScopeAsync_仅修改零售价_不应同步旧进货价()
    {
        await SeedDetailAndProductAsync(
            "D-APPLY-OEM-ONLY",
            "P-APPLY-OEM-ONLY",
            englishName: "Old English"
        );
        await _localDb.Updateable<ContainerDetail>()
            .SetColumns(x => x.ImportPrice == 4.44m)
            .Where(x => x.DetailCode == "D-APPLY-OEM-ONLY")
            .ExecuteCommandAsync();
        await SeedRelatedPriceRowsAsync("P-APPLY-OEM-ONLY");
        var service = CreateService();

        var totalUpdated = await service.ApplyPricesByScopeAsync(
            "C-TEST",
            new ContainerDetailApplyPricesRequestDto
            {
                OemPrice = 9.99m,
                SelectedHguids = new List<string> { "D-APPLY-OEM-ONLY" },
            }
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-APPLY-OEM-ONLY");
        var warehouseProduct = await _localDb.Queryable<WarehouseProduct>()
            .SingleAsync(x => x.ProductCode == "P-APPLY-OEM-ONLY");
        var product = await _localDb.Queryable<Product>()
            .SingleAsync(x => x.ProductCode == "P-APPLY-OEM-ONLY");
        var storeRetailPrices = await _localDb.Queryable<StoreRetailPrice>()
            .Where(x => x.ProductCode == "P-APPLY-OEM-ONLY")
            .ToListAsync();

        Assert.Equal(1, totalUpdated);
        Assert.Equal(4.44m, detail.ImportPrice);
        Assert.Equal(9.99m, detail.OEMPrice);
        Assert.Equal(1.11m, warehouseProduct.ImportPrice);
        Assert.Equal(9.99m, warehouseProduct.OEMPrice);
        Assert.Equal(1.11m, product.PurchasePrice);
        Assert.Equal(9.99m, product.RetailPrice);
        Assert.All(storeRetailPrices, row => Assert.Equal(1.11m, row.PurchasePrice));
        Assert.All(storeRetailPrices, row => Assert.Equal(2.22m, row.StoreRetailPriceValue));
    }

    [Fact]
    public async Task ApplyPricesByScopeAsync_预览后本地主档价格变化_应拒绝且零写入()
    {
        await SeedDetailAndProductAsync(
            "D-PREVIEW-PRODUCT-STALE",
            "P-PREVIEW-PRODUCT-STALE",
            englishName: "Old English"
        );
        await SeedRelatedPriceRowsAsync("P-PREVIEW-PRODUCT-STALE");
        var service = CreateService();
        var preview = await service.PreviewBatchActionAsync(
            "C-TEST",
            new ContainerDetailBatchPreviewRequestDto
            {
                Operation = "apply-prices",
                Scope = new ContainerDetailBatchScopeDto
                {
                    SelectedHguids = new List<string> { "D-PREVIEW-PRODUCT-STALE" },
                },
                // 前端 JSON number 必须能够确定性绑定到执行参数。
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["importPrice"] = JsonDocument.Parse("8.88").RootElement.Clone(),
                },
            }
        );
        var previewJson = JsonSerializer.Serialize(
            preview,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        );
        Assert.Equal(new[] { "进口价格" }, preview.FieldSummary);
        Assert.Contains("\"fieldSummary\"", previewJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"fields\"", previewJson, StringComparison.Ordinal);

        await _localDb.Updateable<Product>()
            .SetColumns(product => product.PurchasePrice == 6.66m)
            .Where(product => product.ProductCode == "P-PREVIEW-PRODUCT-STALE")
            .ExecuteCommandAsync();

        await Assert.ThrowsAsync<ContainerDetailBatchPreviewConflictException>(() =>
            service.ApplyPricesByScopeAsync(
                "C-TEST",
                new ContainerDetailApplyPricesRequestDto
                {
                    ImportPrice = 8.88m,
                    SelectedHguids = new List<string> { "D-PREVIEW-PRODUCT-STALE" },
                    PreviewToken = preview.PreviewToken,
                }
            )
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(item => item.DetailCode == "D-PREVIEW-PRODUCT-STALE");
        var warehouse = await _localDb.Queryable<WarehouseProduct>()
            .SingleAsync(item => item.ProductCode == "P-PREVIEW-PRODUCT-STALE");
        var product = await _localDb.Queryable<Product>()
            .SingleAsync(item => item.ProductCode == "P-PREVIEW-PRODUCT-STALE");
        var storePrices = await _localDb.Queryable<StoreRetailPrice>()
            .Where(item => item.ProductCode == "P-PREVIEW-PRODUCT-STALE")
            .ToListAsync();

        Assert.Equal(1.23m, detail.ImportPrice);
        Assert.Equal(1.11m, warehouse.ImportPrice);
        Assert.Equal(6.66m, product.PurchasePrice);
        Assert.All(storePrices, item => Assert.Equal(1.11m, item.PurchasePrice));
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_进口价丢响应重试_套装多码全部已同步时幂等_子项变化则冲突()
    {
        const string productCode = "P-IMPORT-REPLAY-SET-MULTI";
        const string detailCode = "D-IMPORT-REPLAY-SET-MULTI";
        await SeedDetailAndProductAsync(detailCode, productCode, "Old English");
        await SeedRelatedPriceRowsAsync(productCode);
        var setRows = new[]
        {
            new ProductSetCode { SetCodeId = "SET-REPLAY-1", ProductCode = productCode, SetProductCode = "CHILD-1", SetItemNumber = "CHILD-1", SetType = 1, SetRetailPrice = 6m, SetPurchasePrice = 1m, IsActive = true },
            new ProductSetCode { SetCodeId = "SET-REPLAY-2", ProductCode = productCode, SetProductCode = "CHILD-2", SetItemNumber = "CHILD-2", SetType = 2, SetRetailPrice = 4m, SetPurchasePrice = 1m, IsActive = true },
        };
        await _localDb.Insertable(setRows).ExecuteCommandAsync();
        await _localDb.Insertable(new[]
        {
            new StoreMultiCodeProduct { UUID = "MULTI-REPLAY-1", StoreCode = "001", ProductCode = productCode, MultiCodeProductCode = "CHILD-1", MultiCodeRetailPrice = 6m, PurchasePrice = 1m, IsActive = true },
            new StoreMultiCodeProduct { UUID = "MULTI-REPLAY-2", StoreCode = "001", ProductCode = productCode, MultiCodeProductCode = "CHILD-2", MultiCodeRetailPrice = 4m, PurchasePrice = 1m, IsActive = true },
            new StoreMultiCodeProduct { UUID = "MULTI-REPLAY-3", StoreCode = "002", ProductCode = productCode, MultiCodeProductCode = "CHILD-1", MultiCodeRetailPrice = 6m, PurchasePrice = 1m, IsActive = true },
            new StoreMultiCodeProduct { UUID = "MULTI-REPLAY-4", StoreCode = "002", ProductCode = productCode, MultiCodeProductCode = "CHILD-2", MultiCodeRetailPrice = 4m, PurchasePrice = 1m, IsActive = true },
        }).ExecuteCommandAsync();
        var detail = await _localDb.Queryable<ContainerDetail>().SingleAsync(x => x.DetailCode == detailCode);
        var warehouse = await _localDb.Queryable<WarehouseProduct>().SingleAsync(x => x.ProductCode == productCode);
        var domestic = await _localDb.Queryable<DomesticProduct>().SingleAsync(x => x.ProductCode == productCode);
        var local = await _localDb.Queryable<Product>().SingleAsync(x => x.ProductCode == productCode);
        var stores = await _localDb.Queryable<StoreRetailPrice>().Where(x => x.ProductCode == productCode).ToListAsync();
        var baseline = ContainerDetailFieldConcurrencyGuard.CreateToken(
            detailCode, "进口价格",
            ContainerDetailFieldConcurrencyGuard.CreateSnapshots(detail, warehouse, domestic, local, stores, setRows, await _localDb.Queryable<StoreMultiCodeProduct>().Where(x => x.ProductCode == productCode).ToListAsync())["进口价格"].Value,
            ContainerDetailFieldConcurrencyGuard.CreateSnapshots(detail, warehouse, domestic, local, stores, setRows, await _localDb.Queryable<StoreMultiCodeProduct>().Where(x => x.ProductCode == productCode).ToListAsync())["进口价格"].RelatedValue
        );
        var service = CreateService(concurrencyEnabled: true);
        UpdateContainerDetailDto Request() => new()
        {
            HGUID = detailCode,
            进口价格 = 5m,
            ExpectedServerFieldTokens = new Dictionary<string, string> { ["进口价格"] = baseline },
        };

        Assert.Empty((await service.BatchUpdateDetailsDetailedAsync("C-TEST", new List<UpdateContainerDetailDto> { Request() })).Conflicts);
        var replay = await service.BatchUpdateDetailsDetailedAsync("C-TEST", new List<UpdateContainerDetailDto> { Request() });
        Assert.Empty(replay.Conflicts);
        Assert.Equal(1, replay.TotalUpdated);

        await _localDb.Updateable<ProductSetCode>().SetColumns(x => x.SetPurchasePrice == 4m).Where(x => x.SetCodeId == "SET-REPLAY-1").ExecuteCommandAsync();
        var staleReplay = await service.BatchUpdateDetailsDetailedAsync("C-TEST", new List<UpdateContainerDetailDto> { Request() });
        Assert.Contains(staleReplay.Conflicts, conflict => conflict.Field == "进口价格");
        Assert.Equal(4m, (await _localDb.Queryable<ProductSetCode>().SingleAsync(x => x.SetCodeId == "SET-REPLAY-1")).SetPurchasePrice);
    }

    [Fact]
    public async Task ApplyPricesByScopeAsync_预览scope意图变化即使目标相同也应拒绝()
    {
        await SeedDetailAndProductAsync("D-PREVIEW-SCOPE", "P-PREVIEW-SCOPE", englishName: "Old English");
        var service = CreateService();
        var preview = await service.PreviewBatchActionAsync(
            "C-TEST",
            new ContainerDetailBatchPreviewRequestDto
            {
                Operation = "apply-prices",
                Scope = new ContainerDetailBatchScopeDto
                {
                    SelectedHguids = new List<string> { "D-PREVIEW-SCOPE" },
                    Query = new ContainerDetailQueryDto { ProductName = "画布" },
                },
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["importPrice"] = JsonDocument.Parse("8.88").RootElement.Clone(),
                },
            }
        );

        await Assert.ThrowsAsync<ContainerDetailBatchPreviewConflictException>(() =>
            service.ApplyPricesByScopeAsync(
                "C-TEST",
                new ContainerDetailApplyPricesRequestDto
                {
                    ImportPrice = 8.88m,
                    SelectedHguids = new List<string> { "D-PREVIEW-SCOPE" },
                    Query = new ContainerDetailQueryDto { ProductName = "相框" },
                    PreviewToken = preview.PreviewToken,
                }
            )
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(item => item.DetailCode == "D-PREVIEW-SCOPE");
        Assert.Equal(1.23m, detail.ImportPrice);
    }

    [Fact]
    public async Task ApplyPricesByScopeAsync_有效预览令牌_应完整提交明细与关联价格()
    {
        await SeedDetailAndProductAsync("D-PREVIEW-COMMIT", "P-PREVIEW-COMMIT", englishName: "Old English");
        await SeedRelatedPriceRowsAsync("P-PREVIEW-COMMIT");
        var service = CreateService();
        var preview = await service.PreviewBatchActionAsync(
            "C-TEST",
            new ContainerDetailBatchPreviewRequestDto
            {
                Operation = "apply-prices",
                Scope = new ContainerDetailBatchScopeDto
                {
                    SelectedHguids = new List<string> { "D-PREVIEW-COMMIT" },
                },
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["importPrice"] = JsonDocument.Parse("8.88").RootElement.Clone(),
                },
            }
        );

        var totalUpdated = await service.ApplyPricesByScopeAsync(
            "C-TEST",
            new ContainerDetailApplyPricesRequestDto
            {
                ImportPrice = 8.88m,
                SelectedHguids = new List<string> { "D-PREVIEW-COMMIT" },
                PreviewToken = preview.PreviewToken,
            }
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(item => item.DetailCode == "D-PREVIEW-COMMIT");
        var warehouse = await _localDb.Queryable<WarehouseProduct>()
            .SingleAsync(item => item.ProductCode == "P-PREVIEW-COMMIT");
        var product = await _localDb.Queryable<Product>()
            .SingleAsync(item => item.ProductCode == "P-PREVIEW-COMMIT");
        var storePrices = await _localDb.Queryable<StoreRetailPrice>()
            .Where(item => item.ProductCode == "P-PREVIEW-COMMIT")
            .ToListAsync();

        Assert.Equal(1, totalUpdated);
        Assert.Equal(8.88m, detail.ImportPrice);
        Assert.Equal(8.88m, warehouse.ImportPrice);
        Assert.Equal(8.88m, product.PurchasePrice);
        Assert.All(storePrices, item => Assert.Equal(8.88m, item.PurchasePrice));
    }

    [Fact]
    public async Task RecalculateCostsByScopeAsync_预览后货柜成本输入变化_应拒绝且零写入()
    {
        await _localDb.Insertable(
            new Container
            {
                ContainerCode = "C-PREVIEW-HEADER",
                ContainerNumber = "C-PREVIEW-HEADER",
                ExchangeRate = 5m,
                ShippingFee = 100m,
                TotalVolume = 10m,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new ContainerDetail
            {
                DetailCode = "D-PREVIEW-HEADER",
                ContainerCode = "C-PREVIEW-HEADER",
                ProductCode = "P-PREVIEW-HEADER",
                DomesticPrice = 10m,
                TotalVolume = 2m,
                LoadingQuantity = 5m,
                AdjustmentRate = 1.3m,
                TransportCost = 0m,
                ImportPrice = 0m,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        var service = CreateService();
        var preview = await service.PreviewBatchActionAsync(
            "C-PREVIEW-HEADER",
            new ContainerDetailBatchPreviewRequestDto
            {
                Operation = "recalculate-costs",
                Scope = new ContainerDetailBatchScopeDto
                {
                    SelectedHguids = new List<string> { "D-PREVIEW-HEADER" },
                },
            }
        );

        await _localDb.Updateable<Container>()
            .SetColumns(container => container.ShippingFee == 200m)
            .Where(container => container.ContainerCode == "C-PREVIEW-HEADER")
            .ExecuteCommandAsync();

        await Assert.ThrowsAsync<ContainerDetailBatchPreviewConflictException>(() =>
            service.RecalculateCostsByScopeAsync(
                "C-PREVIEW-HEADER",
                new ContainerDetailBatchScopeDto
                {
                    SelectedHguids = new List<string> { "D-PREVIEW-HEADER" },
                    PreviewToken = preview.PreviewToken,
                }
            )
        );
        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(item => item.DetailCode == "D-PREVIEW-HEADER");
        Assert.Equal(0m, detail.ImportPrice);
        Assert.Equal(0m, detail.TransportCost);
    }

    [Fact]
    public async Task BackfillLastPricesByScopeAsync_预览后仓库来源变化_应拒绝且零写入()
    {
        await SeedDetailAndProductAsync("D-PREVIEW-BACKFILL", "P-PREVIEW-BACKFILL", englishName: "Old English");
        await SeedRelatedPriceRowsAsync("P-PREVIEW-BACKFILL");
        var service = CreateService();
        var preview = await service.PreviewBatchActionAsync(
            "C-TEST",
            new ContainerDetailBatchPreviewRequestDto
            {
                Operation = "backfill-last-prices",
                Scope = new ContainerDetailBatchScopeDto
                {
                    SelectedHguids = new List<string> { "D-PREVIEW-BACKFILL" },
                },
            }
        );

        await _localDb.Updateable<WarehouseProduct>()
            .SetColumns(product => product.ImportPrice == 9.99m)
            .Where(product => product.ProductCode == "P-PREVIEW-BACKFILL")
            .ExecuteCommandAsync();

        await Assert.ThrowsAsync<ContainerDetailBatchPreviewConflictException>(() =>
            service.BackfillLastPricesByScopeAsync(
                "C-TEST",
                new ContainerDetailBatchScopeDto
                {
                    SelectedHguids = new List<string> { "D-PREVIEW-BACKFILL" },
                    PreviewToken = preview.PreviewToken,
                }
            )
        );
        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(item => item.DetailCode == "D-PREVIEW-BACKFILL");
        Assert.Null(detail.LastImportPrice);
        Assert.Null(detail.LastOEMPrice);
    }

    [Fact]
    public async Task BatchDeleteDetailsScopedAsync_预览后关联分店进货价变化_应拒绝且零写入()
    {
        await SeedDetailAndProductAsync(
            "D-PREVIEW-DELETE-RELATED",
            "P-PREVIEW-DELETE-RELATED",
            englishName: "Old English"
        );
        await SeedRelatedPriceRowsAsync("P-PREVIEW-DELETE-RELATED");
        var service = CreateService();
        var preview = await service.PreviewBatchActionAsync(
            "C-TEST",
            new ContainerDetailBatchPreviewRequestDto
            {
                Operation = "delete-details",
                Scope = new ContainerDetailBatchScopeDto
                {
                    SelectedHguids = new List<string> { "D-PREVIEW-DELETE-RELATED" },
                },
            }
        );

        await _localDb.Updateable<StoreRetailPrice>()
            .SetColumns(price => price.PurchasePrice == 9.99m)
            .Where(price => price.ProductCode == "P-PREVIEW-DELETE-RELATED")
            .ExecuteCommandAsync();

        await Assert.ThrowsAsync<ContainerDetailBatchPreviewConflictException>(() =>
            service.BatchDeleteDetailsScopedAsync(
                "C-TEST",
                new ContainerDetailBatchScopeDto
                {
                    SelectedHguids = new List<string> { "D-PREVIEW-DELETE-RELATED" },
                    PreviewToken = preview.PreviewToken,
                }
            )
        );

        Assert.Equal(
            1,
            await _localDb.Queryable<ContainerDetail>()
                .CountAsync(detail => detail.DetailCode == "D-PREVIEW-DELETE-RELATED")
        );
    }

    [Fact]
    public async Task BatchDeleteDetailsScopedAsync_预览后套装多码关系变化_应拒绝且零写入()
    {
        const string productCode = "P-PREVIEW-DELETE-SET-MULTI";
        await SeedDetailAndProductAsync("D-PREVIEW-DELETE-SET-MULTI", productCode, "Old English");
        await SeedRelatedPriceRowsAsync(productCode);
        await _localDb.Insertable(new ProductSetCode
        {
            SetCodeId = "SET-PREVIEW-DELETE-SET-MULTI",
            ProductCode = productCode,
            SetProductCode = "CHILD-PREVIEW-DELETE-SET-MULTI",
            SetItemNumber = "CHILD-PREVIEW-DELETE-SET-MULTI",
            SetRetailPrice = 10m,
            SetPurchasePrice = 1m,
            SetType = 1,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new StoreMultiCodeProduct
        {
            UUID = "MULTI-PREVIEW-DELETE-SET-MULTI",
            ProductCode = productCode,
            StoreCode = "001",
            MultiCodeProductCode = "CHILD-PREVIEW-DELETE-SET-MULTI",
            MultiCodeRetailPrice = 10m,
            PurchasePrice = 1m,
            IsActive = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        var service = CreateService();
        var preview = await service.PreviewBatchActionAsync(
            "C-TEST",
            new ContainerDetailBatchPreviewRequestDto
            {
                Operation = "delete-details",
                Scope = new ContainerDetailBatchScopeDto
                {
                    SelectedHguids = new List<string> { "D-PREVIEW-DELETE-SET-MULTI" },
                },
            }
        );

        await _localDb.Updateable<StoreMultiCodeProduct>()
            .SetColumns(row => row.MultiCodeRetailPrice == 11m)
            .Where(row => row.UUID == "MULTI-PREVIEW-DELETE-SET-MULTI")
            .ExecuteCommandAsync();

        await Assert.ThrowsAsync<ContainerDetailBatchPreviewConflictException>(() =>
            service.BatchDeleteDetailsScopedAsync(
                "C-TEST",
                new ContainerDetailBatchScopeDto
                {
                    SelectedHguids = new List<string> { "D-PREVIEW-DELETE-SET-MULTI" },
                    PreviewToken = preview.PreviewToken,
                }
            )
        );

        Assert.Equal(1, await _localDb.Queryable<ContainerDetail>()
            .CountAsync(detail => detail.DetailCode == "D-PREVIEW-DELETE-SET-MULTI"));
    }

    [Fact]
    public async Task SetStatusByScopeAsync_有效预览令牌_应同步明细和仓库状态()
    {
        await SeedDetailAndProductAsync("D-SET-STATUS", "P-SET-STATUS", "Old English");
        await SeedRelatedPriceRowsAsync("P-SET-STATUS");
        var service = CreateService();
        var preview = await service.PreviewBatchActionAsync(
            "C-TEST",
            new ContainerDetailBatchPreviewRequestDto
            {
                Operation = "set-status",
                Scope = new ContainerDetailBatchScopeDto
                {
                    SelectedHguids = new List<string> { "D-SET-STATUS" },
                },
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["isActive"] = JsonDocument.Parse("false").RootElement.Clone(),
                },
            }
        );

        var totalUpdated = await service.SetStatusByScopeAsync(
            "C-TEST",
            new ContainerDetailSetStatusRequestDto
            {
                IsActive = false,
                SelectedHguids = new List<string> { "D-SET-STATUS" },
                PreviewToken = preview.PreviewToken,
            }
        );

        Assert.Equal(1, totalUpdated);
        Assert.False((await _localDb.Queryable<ContainerDetail>().SingleAsync(item => item.DetailCode == "D-SET-STATUS")).IsActive);
        Assert.False((await _localDb.Queryable<WarehouseProduct>().SingleAsync(item => item.ProductCode == "P-SET-STATUS")).IsActive);
    }

    [Fact]
    public async Task SetStatusByScopeAsync_下架带供货说明_同事务登记_上架关闭()
    {
        await BlazorApp.Api.Data.WarehouseProductSupplyNoticeSchemaMigrator.EnsureAsync(_localDb, NullLogger.Instance);
        await SeedDetailAndProductAsync("D-SET-NOTICE", "P-SET-NOTICE", "Old English");
        await SeedRelatedPriceRowsAsync("P-SET-NOTICE");
        var service = CreateService();

        var delistPreview = await service.PreviewBatchActionAsync("C-TEST", new ContainerDetailBatchPreviewRequestDto
        {
            Operation = "set-status",
            Scope = new ContainerDetailBatchScopeDto { SelectedHguids = new List<string> { "D-SET-NOTICE" } },
            Parameters = new Dictionary<string, JsonElement> { ["isActive"] = JsonDocument.Parse("false").RootElement.Clone() },
        });
        await service.SetStatusByScopeAsync("C-TEST", new ContainerDetailSetStatusRequestDto
        {
            IsActive = false,
            SelectedHguids = new List<string> { "D-SET-NOTICE" },
            PreviewToken = delistPreview.PreviewToken,
            SupplyNotice = new WarehouseProductSupplyNoticeInputDto
            {
                SupplyPlan = WarehouseProductSupplyPlans.WillRestock,
                ExpectedPrecision = WarehouseProductSupplyExpectedPrecisions.Day,
                ExpectedFrom = new DateOnly(2099, 10, 5),
                StoreFacingNote = "下一柜到货后开放",
            },
        });

        var notice = await _localDb.Queryable<WarehouseProductSupplyNotice>().SingleAsync(item => item.ProductCode == "P-SET-NOTICE");
        Assert.Equal(WarehouseProductSupplyPlans.WillRestock, notice.SupplyPlan);
        Assert.Equal("ContainerDetail", notice.Source);
        Assert.Null(notice.ClosedAtUtc);

        var relistPreview = await service.PreviewBatchActionAsync("C-TEST", new ContainerDetailBatchPreviewRequestDto
        {
            Operation = "set-status",
            Scope = new ContainerDetailBatchScopeDto { SelectedHguids = new List<string> { "D-SET-NOTICE" } },
            Parameters = new Dictionary<string, JsonElement> { ["isActive"] = JsonDocument.Parse("true").RootElement.Clone() },
        });
        await service.SetStatusByScopeAsync("C-TEST", new ContainerDetailSetStatusRequestDto
        {
            IsActive = true,
            SelectedHguids = new List<string> { "D-SET-NOTICE" },
            PreviewToken = relistPreview.PreviewToken,
        });

        // 货柜到货重新上架走裸 SQL 回写：说明必须被幂等关闭，否则下次无说明下架会重新浮现。
        notice = await _localDb.Queryable<WarehouseProductSupplyNotice>().SingleAsync(item => item.ProductCode == "P-SET-NOTICE");
        Assert.NotNull(notice.ClosedAtUtc);
    }

    [Fact]
    public async Task SetStatusByScopeAsync_供货说明录入无效_整单拒绝且商品保持在架()
    {
        await BlazorApp.Api.Data.WarehouseProductSupplyNoticeSchemaMigrator.EnsureAsync(_localDb, NullLogger.Instance);
        await SeedDetailAndProductAsync("D-SET-BAD", "P-SET-BAD", "Old English");
        await SeedRelatedPriceRowsAsync("P-SET-BAD");
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() => service.SetStatusByScopeAsync("C-TEST", new ContainerDetailSetStatusRequestDto
        {
            IsActive = false,
            SelectedHguids = new List<string> { "D-SET-BAD" },
            PreviewToken = "unused",
            SupplyNotice = new WarehouseProductSupplyNoticeInputDto { SupplyPlan = "" },
        }));

        Assert.True((await _localDb.Queryable<WarehouseProduct>().SingleAsync(item => item.ProductCode == "P-SET-BAD")).IsActive);
        Assert.Empty(await _localDb.Queryable<WarehouseProductSupplyNotice>().ToListAsync());
    }

    [Fact]
    public async Task AssignCategoryByScopeAsync_预览后本地主档分类变化_应拒绝且零写入()
    {
        await SeedDetailAndProductAsync("D-ASSIGN-CATEGORY", "P-ASSIGN-CATEGORY", "Old English");
        await SeedRelatedPriceRowsAsync("P-ASSIGN-CATEGORY");
        await SeedWarehouseCategoryAsync("CAT-ASSIGN");
        var service = CreateService();
        var preview = await service.PreviewBatchActionAsync(
            "C-TEST",
            new ContainerDetailBatchPreviewRequestDto
            {
                Operation = "assign-category",
                Scope = new ContainerDetailBatchScopeDto
                {
                    SelectedHguids = new List<string> { "D-ASSIGN-CATEGORY" },
                },
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["categoryGuid"] = JsonDocument.Parse("\"CAT-ASSIGN\"").RootElement.Clone(),
                },
            }
        );

        await _localDb.Updateable<Product>()
            .SetColumns(product => product.WarehouseCategoryGUID == "CAT-CHANGED")
            .Where(product => product.ProductCode == "P-ASSIGN-CATEGORY")
            .ExecuteCommandAsync();

        await Assert.ThrowsAsync<ContainerDetailBatchPreviewConflictException>(() =>
            service.AssignCategoryByScopeAsync(
                "C-TEST",
                new ContainerDetailAssignCategoryRequestDto
                {
                    CategoryGuid = "CAT-ASSIGN",
                    SelectedHguids = new List<string> { "D-ASSIGN-CATEGORY" },
                    PreviewToken = preview.PreviewToken,
                }
            )
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(item => item.DetailCode == "D-ASSIGN-CATEGORY");
        Assert.Null(detail.TargetWarehouseCategoryGUID);
    }

    [Fact]
    public async Task AssignCategoryByScopeAsync_有效预览令牌_应同步明细与本地主档分类()
    {
        await SeedDetailAndProductAsync("D-ASSIGN-CATEGORY-COMMIT", "P-ASSIGN-CATEGORY-COMMIT", "Old English");
        await SeedRelatedPriceRowsAsync("P-ASSIGN-CATEGORY-COMMIT");
        await SeedWarehouseCategoryAsync("CAT-ASSIGN-COMMIT");
        var service = CreateService();
        var preview = await service.PreviewBatchActionAsync(
            "C-TEST",
            new ContainerDetailBatchPreviewRequestDto
            {
                Operation = "assign-category",
                Scope = new ContainerDetailBatchScopeDto
                {
                    SelectedHguids = new List<string> { "D-ASSIGN-CATEGORY-COMMIT" },
                },
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["categoryGuid"] = JsonDocument.Parse("\"CAT-ASSIGN-COMMIT\"").RootElement.Clone(),
                },
            }
        );

        var totalUpdated = await service.AssignCategoryByScopeAsync(
            "C-TEST",
            new ContainerDetailAssignCategoryRequestDto
            {
                CategoryGuid = "CAT-ASSIGN-COMMIT",
                SelectedHguids = new List<string> { "D-ASSIGN-CATEGORY-COMMIT" },
                PreviewToken = preview.PreviewToken,
            }
        );

        Assert.Equal(1, totalUpdated);
        Assert.Equal(
            "CAT-ASSIGN-COMMIT",
            (await _localDb.Queryable<ContainerDetail>().SingleAsync(item => item.DetailCode == "D-ASSIGN-CATEGORY-COMMIT")).TargetWarehouseCategoryGUID
        );
        Assert.Equal(
            "CAT-ASSIGN-COMMIT",
            (await _localDb.Queryable<Product>().SingleAsync(item => item.ProductCode == "P-ASSIGN-CATEGORY-COMMIT")).WarehouseCategoryGUID
        );
    }

    [Fact]
    public async Task ApplyFloatRateByScopeAsync_系统重算进货价_应只更新货柜明细()
    {
        await _localDb.Insertable(
            new Container
            {
                ContainerCode = "C-FLOAT-SKIP",
                ContainerNumber = "C-FLOAT-SKIP",
                ExchangeRate = 5m,
                ShippingFee = 100m,
                TotalVolume = 10m,
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new ContainerDetail
            {
                DetailCode = "D-FLOAT-SKIP",
                ContainerCode = "C-FLOAT-SKIP",
                ProductCode = "P-FLOAT-SKIP",
                DomesticPrice = 10m,
                TotalVolume = 2m,
                LoadingQuantity = 5m,
                AdjustmentRate = 1.10m,
                TransportCost = 0m,
                ImportPrice = 0m,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await SeedRelatedPriceRowsAsync("P-FLOAT-SKIP");
        var service = CreateService();

        var totalUpdated = await service.ApplyFloatRateByScopeAsync(
            "C-FLOAT-SKIP",
            new ContainerDetailApplyFloatRateRequestDto
            {
                FloatRate = 1.50m,
                SelectedHguids = new List<string> { "D-FLOAT-SKIP" },
            }
        );

        var detail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-FLOAT-SKIP");
        var warehouseProduct = await _localDb.Queryable<WarehouseProduct>()
            .SingleAsync(x => x.ProductCode == "P-FLOAT-SKIP");
        var product = await _localDb.Queryable<Product>()
            .SingleAsync(x => x.ProductCode == "P-FLOAT-SKIP");
        var storeRetailPrices = await _localDb.Queryable<StoreRetailPrice>()
            .Where(x => x.ProductCode == "P-FLOAT-SKIP")
            .ToListAsync();

        Assert.Equal(1, totalUpdated);
        Assert.Equal(1.50m, detail.AdjustmentRate);
        Assert.Equal(4m, detail.TransportCost);
        Assert.Equal(8.18m, detail.ImportPrice);
        Assert.Equal(1.11m, warehouseProduct.ImportPrice);
        Assert.Equal(1.11m, product.PurchasePrice);
        Assert.All(storeRetailPrices, row => Assert.Equal(1.11m, row.PurchasePrice));
    }

    [Fact]
    public async Task RecalculateCostsByScopeAsync_空或低浮率_应托底到1点30并写回()
    {
        await _localDb.Insertable(
            new Container
            {
                ContainerCode = "C-RECALC-FLOAT",
                ContainerNumber = "C-RECALC-FLOAT",
                ExchangeRate = 5m,
                ShippingFee = 100m,
                TotalVolume = 10m,
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new List<ContainerDetail>
            {
                new()
                {
                    DetailCode = "D-RECALC-NULL",
                    ContainerCode = "C-RECALC-FLOAT",
                    ProductCode = "P-RECALC-NULL",
                    DomesticPrice = 10m,
                    TotalVolume = 2m,
                    LoadingQuantity = 5m,
                    AdjustmentRate = null,
                    TransportCost = 0m,
                    ImportPrice = 0m,
                    IsDeleted = false,
                },
                new()
                {
                    DetailCode = "D-RECALC-LOW",
                    ContainerCode = "C-RECALC-FLOAT",
                    ProductCode = "P-RECALC-LOW",
                    DomesticPrice = 10m,
                    TotalVolume = 2m,
                    LoadingQuantity = 5m,
                    AdjustmentRate = 1.29m,
                    TransportCost = 0m,
                    ImportPrice = 0m,
                    IsDeleted = false,
                },
                new()
                {
                    DetailCode = "D-RECALC-VALID",
                    ContainerCode = "C-RECALC-FLOAT",
                    ProductCode = "P-RECALC-VALID",
                    DomesticPrice = 10m,
                    TotalVolume = 2m,
                    LoadingQuantity = 5m,
                    AdjustmentRate = 1.50m,
                    TransportCost = 0m,
                    ImportPrice = 0m,
                    IsDeleted = false,
                },
            }
        ).ExecuteCommandAsync();
        await SeedRelatedPriceRowsAsync("P-RECALC-VALID");
        var service = CreateService();

        var totalUpdated = await service.RecalculateCostsByScopeAsync(
            "C-RECALC-FLOAT",
            new ContainerDetailBatchScopeDto
            {
                SelectedHguids = new List<string>
                {
                    "D-RECALC-NULL",
                    "D-RECALC-LOW",
                    "D-RECALC-VALID",
                },
            }
        );

        var details = await _localDb.Queryable<ContainerDetail>()
            .Where(x => x.ContainerCode == "C-RECALC-FLOAT")
            .OrderBy(x => x.DetailCode)
            .ToListAsync();
        var nullRateDetail = details.Single(x => x.DetailCode == "D-RECALC-NULL");
        var lowRateDetail = details.Single(x => x.DetailCode == "D-RECALC-LOW");
        var validRateDetail = details.Single(x => x.DetailCode == "D-RECALC-VALID");
        var warehouseProduct = await _localDb.Queryable<WarehouseProduct>()
            .SingleAsync(x => x.ProductCode == "P-RECALC-VALID");
        var product = await _localDb.Queryable<Product>()
            .SingleAsync(x => x.ProductCode == "P-RECALC-VALID");
        var storeRetailPrices = await _localDb.Queryable<StoreRetailPrice>()
            .Where(x => x.ProductCode == "P-RECALC-VALID")
            .ToListAsync();
        Assert.Equal(3, totalUpdated);
        Assert.Equal(1.30m, nullRateDetail.AdjustmentRate);
        Assert.Equal(1.30m, lowRateDetail.AdjustmentRate);
        Assert.Equal(1.50m, validRateDetail.AdjustmentRate);
        Assert.All(details, detail => Assert.Equal(4m, detail.TransportCost));
        Assert.Equal(7.09m, nullRateDetail.ImportPrice);
        Assert.Equal(7.09m, lowRateDetail.ImportPrice);
        Assert.Equal(8.18m, validRateDetail.ImportPrice);
        Assert.Equal(1.11m, warehouseProduct.ImportPrice);
        Assert.Equal(1.11m, product.PurchasePrice);
        Assert.All(storeRetailPrices, row => Assert.Equal(1.11m, row.PurchasePrice));
    }

    [Fact]
    public async Task RecalculateCostsByScopeAsync_缺少汇率或运费_应阻止成本重算()
    {
        await _localDb.Insertable(
            new List<Container>
            {
                new()
                {
                    ContainerCode = "C-RECALC-NO-RATE",
                    ContainerNumber = "C-RECALC-NO-RATE",
                    ExchangeRate = null,
                    ShippingFee = 100m,
                    TotalVolume = 10m,
                },
                new()
                {
                    ContainerCode = "C-RECALC-NO-FREIGHT",
                    ContainerNumber = "C-RECALC-NO-FREIGHT",
                    ExchangeRate = 5m,
                    ShippingFee = null,
                    TotalVolume = 10m,
                },
            }
        ).ExecuteCommandAsync();
        var service = CreateService();

        var missingRateError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecalculateCostsByScopeAsync(
                "C-RECALC-NO-RATE",
                new ContainerDetailBatchScopeDto()
            )
        );
        var missingFreightError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyFloatRateByScopeAsync(
                "C-RECALC-NO-FREIGHT",
                new ContainerDetailApplyFloatRateRequestDto { FloatRate = 1.3m }
            )
        );

        Assert.Equal("缺少汇率，无法重算成本", missingRateError.Message);
        Assert.Equal("缺少运费，无法重算成本", missingFreightError.Message);
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_明细或商品不存在_不抛异常()
    {
        await SeedDetailAsync("D-NO-PRODUCT", productCode: "P-MISSING");
        var service = CreateService();

        var totalUpdated = await service.BatchUpdateDetailsAsync(
            new List<UpdateContainerDetailDto>
            {
                new() { HGUID = "D-MISSING", 英文名称 = "Missing Detail" },
                new() { HGUID = "D-NO-PRODUCT", 英文名称 = "Missing Product" },
            }
        );

        Assert.Equal(0, totalUpdated);
    }

    [Fact]
    public async Task BatchUpdateDetailsAsync_全部明细不存在_应返回字段级错误且不抛异常()
    {
        var service = CreateService();
        var updates = new List<UpdateContainerDetailDto>
        {
            new() { HGUID = "D-MISSING-ONLY", 英文名称 = "Missing Detail" },
        };

        var detailedResult = await service.BatchUpdateDetailsDetailedAsync(updates);
        var totalUpdated = await service.BatchUpdateDetailsAsync(updates);

        Assert.Equal(1, detailedResult.TotalRequested);
        Assert.Equal(0, detailedResult.TotalUpdated);
        var error = Assert.Single(detailedResult.ValidationErrors);
        Assert.Equal("D-MISSING-ONLY", error.HGUID);
        Assert.Equal("*", error.Field);
        Assert.Equal("DETAIL_NOT_FOUND", error.Code);
        Assert.Equal(0, totalUpdated);
    }

    [Fact]
    public async Task BatchUpdateDetailsDetailedAsync_仅修改备注_应持久化且支持清空()
    {
        await SeedDetailAsync("D-REMARK-ONLY", productCode: null);
        var service = CreateService();

        var updateResult = await service.BatchUpdateDetailsDetailedAsync(
            "C-TEST",
            new List<UpdateContainerDetailDto>
            {
                new()
                {
                    HGUID = "D-REMARK-ONLY",
                    备注 = "连续编辑备注",
                    SkipRelatedProductSync = true,
                },
            }
        );
        var updatedDetail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(detail => detail.DetailCode == "D-REMARK-ONLY");

        Assert.Equal(1, updateResult.TotalUpdated);
        Assert.Empty(updateResult.ValidationErrors);
        Assert.Equal("连续编辑备注", updatedDetail.Remarks);

        var clearResult = await service.BatchUpdateDetailsDetailedAsync(
            "C-TEST",
            new List<UpdateContainerDetailDto>
            {
                new()
                {
                    HGUID = "D-REMARK-ONLY",
                    备注 = string.Empty,
                    SkipRelatedProductSync = true,
                },
            }
        );
        var clearedDetail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(detail => detail.DetailCode == "D-REMARK-ONLY");

        Assert.Equal(1, clearResult.TotalUpdated);
        Assert.Empty(clearResult.ValidationErrors);
        Assert.Equal(string.Empty, clearedDetail.Remarks);
    }

    public void Dispose()
    {
        _localDb.Dispose();
        _hbSalesDb.Dispose();
        _localConnection.Dispose();
        _hbSalesConnection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_localDbPath);
        SqliteTempFileCleanup.DeleteIfExists(_hbSalesDbPath);
    }

    private ContainerReactService CreateService(
        IWarehouseProductChangeHistoryService? historyService = null,
        ICurrentUserService? currentUserService = null,
        bool concurrencyEnabled = false
    )
    {
        return new ContainerReactService(
            CreateSqlSugarContext(_localDb),
            CreateHqSqlSugarContext(),
            CreateHBSalesSqlSugarContext(_hbSalesDb),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ContainerDetailConcurrency:Enabled"] = concurrencyEnabled.ToString(),
                })
                .Build(),
            Mock.Of<IMapper>(),
            NullLogger<ContainerReactService>.Instance,
            Mock.Of<IContainerHqSyncService>(),
            CreateTranslationServiceMock(),
            historyService ?? Mock.Of<IWarehouseProductChangeHistoryService>(),
            currentUserService ?? Mock.Of<ICurrentUserService>(),
            new EphemeralDataProtectionProvider()
        );
    }

    private static Mock<IWarehouseProductChangeHistoryService> CreateAlignHistoryMock(
        string oldProductCode,
        string targetProductCode,
        Action<
            IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>,
            IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>,
            WarehouseProductChangeHistoryContextDto
        > onRecord,
        bool throwWhenRecording = false
    )
    {
        var history = new Mock<IWarehouseProductChangeHistoryService>(MockBehavior.Strict);
        history
            .SetupSequence(service => service.CaptureSnapshotsAsync(
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(new Dictionary<string, WarehouseProductChangeSnapshotDto>(
                StringComparer.OrdinalIgnoreCase
            )
            {
                [oldProductCode] = new WarehouseProductChangeSnapshotDto
                {
                    ProductCode = oldProductCode,
                    ProductName = "国内商品",
                },
                [targetProductCode] = new WarehouseProductChangeSnapshotDto
                {
                    ProductCode = targetProductCode,
                    ProductName = "本地主档商品",
                },
            })
            .ReturnsAsync(new Dictionary<string, WarehouseProductChangeSnapshotDto>(
                StringComparer.OrdinalIgnoreCase
            )
            {
                [targetProductCode] = new WarehouseProductChangeSnapshotDto
                {
                    ProductCode = targetProductCode,
                    ProductName = "本地主档商品",
                },
            });
        var setup = history.Setup(service => service.RecordChangesAsync(
            It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
            It.IsAny<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>>(),
            It.IsAny<WarehouseProductChangeHistoryContextDto>(),
            It.IsAny<CancellationToken>()
        ));
        setup.Callback((
            IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto> before,
            IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto> after,
            WarehouseProductChangeHistoryContextDto context,
            CancellationToken _
        ) => onRecord(before, after, context));
        if (throwWhenRecording)
        {
            setup.ThrowsAsync(new InvalidOperationException("历史写入失败"));
        }
        else
        {
            setup.ReturnsAsync(1);
        }

        return history;
    }

    private static ICurrentUserService CreateCurrentUser(string userGuid, string username)
    {
        var currentUser = new Mock<ICurrentUserService>(MockBehavior.Strict);
        currentUser.Setup(service => service.GetCurrentUserGuid()).Returns(userGuid);
        currentUser.Setup(service => service.GetCurrentUsername()).Returns(username);
        return currentUser.Object;
    }

    private static ITranslationService CreateTranslationServiceMock()
    {
        var translationService = new Mock<ITranslationService>();
        translationService
            .Setup(x => x.ContainsChinese(It.IsAny<string>()))
            .Returns<string>(value => value.Any(c => c >= '\u4e00' && c <= '\u9fff'));
        translationService
            .Setup(x => x.BatchTranslateToEnglishAsync(It.IsAny<List<string>>()))
            .ReturnsAsync((List<string> texts) =>
                texts.ToDictionary(
                    text => text,
                    text => text == "草莓玩具" ? "Strawberry Toy" : text
                )
            );
        return translationService.Object;
    }

    private async Task SeedDetailAndProductAsync(
        string detailCode,
        string productCode,
        string? englishName,
        int? middlePackQuantity = null
    )
    {
        await SeedDetailAsync(detailCode, productCode);
        await _localDb.Insertable(
            new DomesticProduct
            {
                ProductCode = productCode,
                HBProductNo = productCode,
                ProductName = $"商品 {productCode}",
                EnglishProductName = englishName,
                MiddlePackQuantity = middlePackQuantity,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
    }

    private async Task SeedLocalProductAsync(
        string productCode,
        string productName,
        string? englishName,
        string? itemNumber = null,
        string? localSupplierCode = null
    )
    {
        await _localDb.Insertable(
            new Product
            {
                ProductCode = productCode,
                ProductName = productName,
                EnglishName = englishName,
                ItemNumber = itemNumber,
                LocalSupplierCode = localSupplierCode,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
    }

    private async Task SeedRelatedPriceRowsAsync(
        string productCode,
        int? minOrderQuantity = null,
        int? packingQuantity = null
    )
    {
        await _localDb.Insertable(
            new WarehouseProduct
            {
                ProductCode = productCode,
                ImportPrice = 1.11m,
                OEMPrice = 2.22m,
                MinOrderQuantity = minOrderQuantity,
                PackingQuantity = packingQuantity,
                IsActive = true,
            }
        ).ExecuteCommandAsync();

        await _localDb.Insertable(
            new Product
            {
                ProductCode = productCode,
                ProductName = $"本地商品 {productCode}",
                PurchasePrice = 1.11m,
                RetailPrice = 2.22m,
                IsActive = true,
            }
        ).ExecuteCommandAsync();

        await _localDb.Insertable(
            new List<StoreRetailPrice>
            {
                new()
                {
                    StoreCode = "001",
                    ProductCode = productCode,
                    PurchasePrice = 1.11m,
                    StoreRetailPriceValue = 2.22m,
                    IsActive = true,
                },
                new()
                {
                    StoreCode = "002",
                    ProductCode = productCode,
                    PurchasePrice = 1.11m,
                    StoreRetailPriceValue = 2.22m,
                    IsActive = true,
                },
            }
        ).ExecuteCommandAsync();
    }

    private async Task SeedDetailAsync(string detailCode, string? productCode, string? productType = null)
    {
        await _localDb.Insertable(
            new ContainerDetail
            {
                DetailCode = detailCode,
                ContainerCode = "C-TEST",
                ProductCode = productCode,
                ProductType = productType,
                ImportPrice = 1.23m,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
    }

    private async Task SeedWarehouseCategoryAsync(string categoryGuid, bool isDeleted = false)
    {
        await _localDb.Insertable(
            new WarehouseCategory
            {
                CategoryGUID = categoryGuid,
                CategoryName = $"分类 {categoryGuid}",
                IsActive = true,
                IsDeleted = isDeleted,
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
