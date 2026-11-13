using System.Reflection;
using System.Runtime.CompilerServices;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class WarehouseProductFlowAnalysisServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;

    public WarehouseProductFlowAnalysisServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        _db = new SqlSugarClient(CreateConnectionConfig(_connection.ConnectionString));
        _db.CodeFirst.InitTables(
            typeof(WarehouseProduct),
            typeof(Product),
            typeof(DomesticProduct),
            typeof(ChinaSupplier),
            typeof(WarehouseCategory),
            typeof(Container),
            typeof(ContainerDetail),
            typeof(WareHouseOrder),
            typeof(WareHouseOrderDetails),
            typeof(ProductStoreDailySalesStatistic),
            typeof(Store)
        );
    }

    [Fact]
    public async Task Candidates_纯主档返回且不包含指标()
    {
        await _db.Insertable(new WarehouseProduct { ProductCode = "P1", IsDeleted = false }).ExecuteCommandAsync();
        await _db.Insertable(new Product { UUID = "u1", ProductCode = "P1", ProductName = "零指标商品" }).ExecuteCommandAsync();

        var result = await CreateService().GetCandidatesAsync(CreateCandidateRequest());

        Assert.True(result.Success);
        var row = Assert.Single(result.Data!.Items);
        Assert.Equal("P1", row.ProductCode);
        Assert.Equal("零指标商品", row.ProductName);
    }

    [Fact]
    public async Task Candidates_契约不接收分店范围()
    {
        await _db.Insertable(new WarehouseProduct { ProductCode = "P1", IsDeleted = false }).ExecuteCommandAsync();
        await _db.Insertable(new Product { UUID = "u1", ProductCode = "P1", ProductName = "商品" }).ExecuteCommandAsync();

        var result = await CreateService().GetCandidatesAsync(CreateCandidateRequest());
        var method = typeof(WarehouseProductFlowAnalysisService).GetMethod(nameof(WarehouseProductFlowAnalysisService.GetCandidatesAsync));

        Assert.Single(result.Data!.Items);
        Assert.NotNull(method);
        Assert.Single(method!.GetParameters());
    }

    [Fact]
    public async Task Candidates_默认按货号升序且缺货号排后()
    {
        await _db.Insertable(new[]
        {
            new WarehouseProduct { ProductCode = "P1", IsDeleted = false },
            new WarehouseProduct { ProductCode = "P2", IsDeleted = false },
            new WarehouseProduct { ProductCode = "P3", IsDeleted = false },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new Product { UUID = "u1", ProductCode = "P1", ProductName = "货号B", ItemNumber = "B" },
            new Product { UUID = "u2", ProductCode = "P2", ProductName = "货号A", ItemNumber = "A" },
            new Product { UUID = "u3", ProductCode = "P3", ProductName = "缺货号" },
        }).ExecuteCommandAsync();

        var result = await CreateService().GetCandidatesAsync(CreateCandidateRequest());

        Assert.Equal(new[] { "P2", "P1", "P3" }, result.Data!.Items.Select(row => row.ProductCode));
    }

    [Fact]
    public async Task Candidates_重复ProductCode只计数一次并使用确定性主档()
    {
        await _db.Insertable(new WarehouseProduct { ProductCode = "P1", IsDeleted = false }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new Product { UUID = "z-product", ProductCode = "P1", ProductName = "后选主档", ItemNumber = "Z" },
            new Product { UUID = "a-product", ProductCode = "P1", ProductName = "确定主档", ItemNumber = "A" },
        }).ExecuteCommandAsync();

        var result = await CreateService().GetCandidatesAsync(CreateCandidateRequest());

        Assert.Equal(1, result.Data!.Total);
        var row = Assert.Single(result.Data.Items);
        Assert.Equal("P1", row.ProductCode);
        Assert.Equal("确定主档", row.ProductName);
        Assert.Equal("A", row.ItemNumber);
    }

    [Fact]
    public async Task Candidates_重复ProductCode筛选与确定性主档保持一致()
    {
        await _db.Insertable(new WarehouseProduct
        {
            ProductCode = "P1",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new Product
            {
                UUID = "a-product",
                ProductCode = "P1",
                ProductName = "确定主档",
                IsDeleted = false,
            },
            new Product
            {
                UUID = "z-product",
                ProductCode = "P1",
                ProductName = "仅非确定主档命中",
                IsDeleted = false,
            },
        }).ExecuteCommandAsync();

        var result = await CreateService().GetCandidatesAsync(
            CreateCandidateRequest(keyword: "仅非确定主档命中")
        );

        Assert.Equal(0, result.Data!.Total);
        Assert.Empty(result.Data.Items);
    }

    [Fact]
    public async Task Candidates_ForceRefresh立即重算重复ProductCode状态()
    {
        await _db.Insertable(new WarehouseProduct
        {
            ProductCode = "P1",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _db.Insertable(new Product
        {
            UUID = "z-product",
            ProductCode = "P1",
            ProductName = "原主档",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(cache);
        var request = CreateCandidateRequest();

        var first = await service.GetCandidatesAsync(request);
        Assert.Single(first.Data!.Items);

        await _db.Insertable(new Product
        {
            UUID = "a-product",
            ProductCode = "P1",
            ProductName = "刷新后确定主档",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        request.ForceRefresh = true;
        var refreshed = await service.GetCandidatesAsync(request);

        Assert.Equal(1, refreshed.Data!.Total);
        var item = Assert.Single(refreshed.Data.Items);
        Assert.Equal("刷新后确定主档", item.ProductName);
    }

    [Fact]
    public async Task Candidates_重复ProductCode排除软删除主档()
    {
        await _db.Insertable(new WarehouseProduct { ProductCode = "P1", IsDeleted = false }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new Product
            {
                UUID = "a-deleted",
                ProductCode = "P1",
                ProductName = "已删除旧主档",
                ItemNumber = "OLD",
                IsDeleted = true,
            },
            new Product
            {
                UUID = "z-active",
                ProductCode = "P1",
                ProductName = "有效主档",
                ItemNumber = "ACTIVE",
                IsDeleted = false,
            },
        }).ExecuteCommandAsync();

        var result = await CreateService().GetCandidatesAsync(
            CreateCandidateRequest(keyword: "有效主档")
        );

        Assert.Equal(1, result.Data!.Total);
        var row = Assert.Single(result.Data.Items);
        Assert.Equal("有效主档", row.ProductName);
        Assert.Equal("ACTIVE", row.ItemNumber);
    }

    [Theory]
    [InlineData("HB022-244", "P1")]
    [InlineData("火箭", "P1")]
    [InlineData("Rocket", "P1")]
    [InlineData("952790", "P1")]
    [InlineData("P1", "P1")]
    [InlineData("WH-ONLY", "WH-ONLY")]
    [InlineData("删除商品", null)]
    [InlineData("不存在", null)]
    public async Task Candidates_关键字集合匹配覆盖所有字段及缺失主档(string keyword, string? expectedCode)
    {
        await _db.Insertable(new[]
        {
            new WarehouseProduct { ProductCode = "P1", IsDeleted = false },
            new WarehouseProduct { ProductCode = "WH-ONLY", IsDeleted = false },
            new WarehouseProduct { ProductCode = "DELETED-PRODUCT", IsDeleted = false },
            new WarehouseProduct { ProductCode = "DELETED-WAREHOUSE", IsDeleted = true },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new Product
            {
                UUID = "u1", ProductCode = "P1", ItemNumber = "HB022-244",
                ProductName = "火箭玩具", EnglishName = "Jumbo Rocket", Barcode = "9527902201157",
            },
            new Product { UUID = "u2", ProductCode = "DELETED-PRODUCT", ProductName = "删除商品", IsDeleted = true },
            new Product { UUID = "u3", ProductCode = "DELETED-WAREHOUSE", ProductName = "删除商品" },
        }).ExecuteCommandAsync();

        var result = await CreateService().GetCandidatesAsync(CreateCandidateRequest(keyword: keyword));

        Assert.Equal(expectedCode == null ? 0 : 1, result.Data!.Total);
        if (expectedCode == null)
            Assert.Empty(result.Data.Items);
        else
            Assert.Equal(expectedCode, Assert.Single(result.Data.Items).ProductCode);
    }

    [Fact]
    public async Task Candidates_批量货号精确匹配按粘贴顺序并返回未找到()
    {
        await _db.Insertable(new[]
        {
            new WarehouseProduct { ProductCode = "P1", IsDeleted = false },
            new WarehouseProduct { ProductCode = "P2", IsDeleted = false },
            new WarehouseProduct { ProductCode = "P3", IsDeleted = false },
            new WarehouseProduct { ProductCode = "P4", IsDeleted = false },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new Product { UUID = "u1", ProductCode = "P1", ItemNumber = "10236", Barcode = "931001" },
            new Product { UUID = "u2", ProductCode = "P2", ItemNumber = "10235", Barcode = "931002" },
            // 关键字模糊匹配会命中，批量货号精确匹配不能命中。
            new Product { UUID = "u3", ProductCode = "P3", ItemNumber = "102350", Barcode = "931003" },
            new Product { UUID = "u4", ProductCode = "P4", ItemNumber = "000", Barcode = "931004" },
        }).ExecuteCommandAsync();

        var request = CreateCandidateRequest();
        request.Filter.ItemNumbers = new List<string> { "10235", " 10236 ", "X9", "10235", "" };
        var result = await CreateService().GetCandidatesAsync(request);

        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.Total);
        Assert.Equal(new[] { "P2", "P1" }, result.Data.Items.Select(item => item.ProductCode).ToArray());
        var match = Assert.IsType<WarehouseProductFlowItemNumberMatchDto>(result.Data.ItemNumberMatch);
        Assert.Equal(3, match.RequestedCount);
        Assert.Equal(2, match.MatchedCount);
        Assert.Equal(new[] { "X9" }, match.UnmatchedItemNumbers.ToArray());
    }

    [Fact]
    public async Task Candidates_批量货号any模式匹配条码与商品编码并与其他条件取交集()
    {
        await _db.Insertable(new[]
        {
            new WarehouseProduct { ProductCode = "P1", IsDeleted = false },
            new WarehouseProduct { ProductCode = "P2", IsDeleted = false },
            new WarehouseProduct { ProductCode = "P3", IsDeleted = false },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new Product { UUID = "u1", ProductCode = "P1", ItemNumber = "A1", Barcode = "931001", ProductName = "杯子" },
            new Product { UUID = "u2", ProductCode = "P2", ItemNumber = "A2", Barcode = "931002", ProductName = "杯子" },
            new Product { UUID = "u3", ProductCode = "P3", ItemNumber = "A3", Barcode = "931003", ProductName = "盒子" },
        }).ExecuteCommandAsync();

        var onlyItemNumber = CreateCandidateRequest();
        onlyItemNumber.Filter.ItemNumbers = new List<string> { "P3", "931002", "A1" };
        var itemResult = await CreateService().GetCandidatesAsync(onlyItemNumber);
        Assert.Equal(new[] { "P1" }, itemResult.Data!.Items.Select(item => item.ProductCode).ToArray());

        var any = CreateCandidateRequest(keyword: "杯子");
        any.Filter.ItemNumbers = new List<string> { "P3", "931002", "A1" };
        any.Filter.ItemNumberMatchMode = "any";
        var anyResult = await CreateService().GetCandidatesAsync(any);

        // P3 按商品编码命中，但被关键字「杯子」排除，算作未找到。
        Assert.Equal(new[] { "P2", "P1" }, anyResult.Data!.Items.Select(item => item.ProductCode).ToArray());
        Assert.Equal(new[] { "P3" }, anyResult.Data.ItemNumberMatch!.UnmatchedItemNumbers.ToArray());
    }

    [Fact]
    public async Task Candidates_批量货号全部未找到返回空页且超过上限报参数错误()
    {
        await _db.Insertable(new WarehouseProduct { ProductCode = "P1", IsDeleted = false }).ExecuteCommandAsync();
        await _db.Insertable(new Product { UUID = "u1", ProductCode = "P1", ItemNumber = "A1" }).ExecuteCommandAsync();

        var request = CreateCandidateRequest();
        request.Filter.ItemNumbers = new List<string> { "NOPE" };
        var result = await CreateService().GetCandidatesAsync(request);
        Assert.Equal(0, result.Data!.Total);
        Assert.Empty(result.Data.Items);
        Assert.Equal(new[] { "NOPE" }, result.Data.ItemNumberMatch!.UnmatchedItemNumbers.ToArray());

        var tooMany = CreateCandidateRequest();
        tooMany.Filter.ItemNumbers = Enumerable.Range(1, WarehouseProductFlowAnalysisService.MaxItemNumberFilterCount + 1)
            .Select(index => $"I{index}")
            .ToList();
        await Assert.ThrowsAsync<WarehouseProductFlowAnalysisValidationException>(
            () => CreateService().GetCandidatesAsync(tooMany)
        );
    }

    [Fact]
    public async Task Summary_批量货号只汇总命中商品()
    {
        var date = new DateTime(2026, 8, 10);
        await _db.Insertable(new[]
        {
            new WarehouseProduct { ProductCode = "P1", IsDeleted = false },
            new WarehouseProduct { ProductCode = "P2", IsDeleted = false },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new Product { UUID = "u1", ProductCode = "P1", ItemNumber = "A1" },
            new Product { UUID = "u2", ProductCode = "P2", ItemNumber = "A2" },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            CreateStatistic(date, "B1", "AU1", "P1", 5, 50m),
            CreateStatistic(date, "B1", "AU1", "P2", 7, 70m),
        }).ExecuteCommandAsync();

        var request = CreateRequest(date);
        request.Filter.ItemNumbers = new List<string> { "a1 " };
        var result = await CreateService().GetSummaryAsync(request, branchCodes: null);

        Assert.Equal("P1", Assert.Single(result.Data!.Items).ProductCode);
        Assert.Equal(5, result.Data.Totals.NetSalesQuantity);
    }

    [Fact]
    public async Task Candidates_宽泛关键字不因候选预读取截断()
    {
        foreach (var chunk in Enumerable.Range(1, 505).Chunk(500))
        {
            await _db.Insertable(chunk.Select(index => new WarehouseProduct
            {
                ProductCode = $"P{index:D4}", IsDeleted = false,
            }).ToList()).ExecuteCommandAsync();
            await _db.Insertable(chunk.Select(index => new Product
            {
                UUID = $"uuid-{index:D4}", ProductCode = $"P{index:D4}", ProductName = "批量关键字",
            }).ToList()).ExecuteCommandAsync();
        }
        var request = CreateCandidateRequest(keyword: "批量关键字");
        request.PageNumber = 6;
        request.PageSize = 100;

        var result = await CreateService().GetCandidatesAsync(request);

        Assert.Equal(505, result.Data!.Total);
        Assert.Equal(Enumerable.Range(501, 5).Select(index => $"P{index:D4}"),
            result.Data.Items.Select(row => row.ProductCode));
    }

    [Fact]
    public async Task Candidates_父分类包含子分类商品()
    {
        await _db.Insertable(new[]
        {
            new WarehouseCategory { CategoryGUID = "cat-parent", CategoryName = "父类", ParentGUID = null },
            new WarehouseCategory { CategoryGUID = "cat-child", CategoryName = "子类", ParentGUID = "cat-parent" },
        }).ExecuteCommandAsync();
        await _db.Insertable(new WarehouseProduct { ProductCode = "P1", IsDeleted = false }).ExecuteCommandAsync();
        await _db.Insertable(new Product
        {
            UUID = "u1",
            ProductCode = "P1",
            ProductName = "子类商品",
            WarehouseCategoryGUID = "cat-child",
        }).ExecuteCommandAsync();

        var result = await CreateService().GetCandidatesAsync(
            CreateCandidateRequest(categoryGuids: new List<string> { "cat-parent" })
        );

        Assert.Single(result.Data!.Items);
        Assert.Equal("P1", result.Data.Items.Single().ProductCode);
    }

    [Fact]
    public async Task Candidates_货柜编号documentKeyword筛选商品()
    {
        await _db.Insertable(new[]
        {
            new WarehouseProduct { ProductCode = "P1", IsDeleted = false },
            new WarehouseProduct { ProductCode = "P2", IsDeleted = false },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new Product { UUID = "u1", ProductCode = "P1", ProductName = "柜一" },
            new Product { UUID = "u2", ProductCode = "P2", ProductName = "柜二" },
        }).ExecuteCommandAsync();
        await _db.Insertable(new Container
        {
            ContainerCode = "C1",
            ContainerNumber = "OOLU001",
            ActualArrivalDate = new DateTime(2026, 8, 10),
            Status = 1,
        }).ExecuteCommandAsync();
        await _db.Insertable(new ContainerDetail
        {
            DetailCode = "D1",
            ContainerCode = "C1",
            ProductCode = "P1",
            LoadingQuantity = 12m,
        }).ExecuteCommandAsync();

        var result = await CreateService().GetCandidatesAsync(
            CreateCandidateRequest(documentKeyword: "OOLU")
        );

        Assert.Single(result.Data!.Items);
        Assert.Equal("P1", result.Data.Items.Single().ProductCode);
    }

    [Fact]
    public async Task Candidates_已取消货柜明细不命中文档筛选()
    {
        await _db.Insertable(new WarehouseProduct { ProductCode = "P1", IsDeleted = false }).ExecuteCommandAsync();
        await _db.Insertable(new Product { UUID = "u1", ProductCode = "P1", ProductName = "取消明细" }).ExecuteCommandAsync();
        await _db.Insertable(new Container { ContainerCode = "C1", ContainerNumber = "OOLU-CANCEL", ActualArrivalDate = new DateTime(2026, 8, 10), Status = 1 }).ExecuteCommandAsync();
        await _db.Insertable(new ContainerDetail { DetailCode = "CD1", ContainerCode = "C1", ProductCode = "P1", LoadingQuantity = 9m, Status = 6 }).ExecuteCommandAsync();

        var unfiltered = await CreateService().GetCandidatesAsync(CreateCandidateRequest());
        var filtered = await CreateService().GetCandidatesAsync(
            CreateCandidateRequest(documentKeyword: "OOLU-CANCEL")
        );

        Assert.Single(unfiltered.Data!.Items);
        Assert.Empty(filtered.Data!.Items);
    }

    [Fact]
    public async Task 货柜编号筛选不误过滤当前商品订单和发货明细()
    {
        var date = new DateTime(2026, 8, 10);
        await _db.Insertable(new WareHouseOrder { OrderGUID = "O1", OrderNo = "SO1", StoreCode = "B1", FlowStatus = 1, OrderDate = date, OutboundDate = date }).ExecuteCommandAsync();
        await _db.Insertable(new WareHouseOrderDetails { DetailGUID = "OD1", OrderGUID = "O1", ProductCode = "P1", Quantity = 8m, AllocQuantity = 7m }).ExecuteCommandAsync();
        var request = CreateRequest(date, documentKeyword: "OOLU", currentProductCode: "P1");

        var orders = await CreateService().GetOrdersAsync(request, branchCodes: null);
        var shipments = await CreateService().GetShipmentsAsync(request, branchCodes: null);

        Assert.Single(orders.Data!);
        Assert.Single(shipments.Data!);
    }

    [Fact]
    public async Task Summary_三套日期只读对应范围()
    {
        var containerDate = new DateTime(2026, 8, 10);
        var orderDate = new DateTime(2026, 8, 11);
        var salesDate = new DateTime(2026, 8, 12);
        await _db.Insertable(new WarehouseProduct { ProductCode = "P1", IsDeleted = false }).ExecuteCommandAsync();
        await _db.Insertable(new Product { UUID = "u1", ProductCode = "P1", ProductName = "商品" }).ExecuteCommandAsync();
        await _db.Insertable(new Container
        {
            ContainerCode = "C1",
            ContainerNumber = "OOLU001",
            ActualArrivalDate = containerDate,
            Status = 1,
        }).ExecuteCommandAsync();
        await _db.Insertable(new ContainerDetail
        {
            DetailCode = "D1",
            ContainerCode = "C1",
            ProductCode = "P1",
            LoadingQuantity = 30m,
        }).ExecuteCommandAsync();
        await _db.Insertable(new WareHouseOrder
        {
            OrderGUID = "O1",
            OrderNo = "SO1",
            FlowStatus = 1,
            OrderDate = orderDate,
        }).ExecuteCommandAsync();
        await _db.Insertable(new WareHouseOrderDetails
        {
            DetailGUID = "OD1",
            OrderGUID = "O1",
            ProductCode = "P1",
            Quantity = 10m,
        }).ExecuteCommandAsync();
        await _db.Insertable(CreateStatistic(salesDate, "B1", "AU1", "P1", 5, 50m)).ExecuteCommandAsync();

        var result = await CreateService().GetSummaryAsync(
            CreateRequestWithPeriods(containerDate, orderDate, salesDate),
            branchCodes: null
        );

        var row = result.Data!.Items.Single();
        Assert.Equal(30m, row.Metrics.InboundQuantity);
        Assert.Equal(10m, row.Metrics.OrderedQuantity);
        Assert.Equal(5, row.Metrics.NetSalesQuantity);
    }

    [Fact]
    public async Task Containers_只读containerPeriod()
    {
        var containerDate = new DateTime(2026, 8, 10);
        await _db.Insertable(new Container
        {
            ContainerCode = "C1",
            ContainerNumber = "OOLU001",
            ActualArrivalDate = containerDate,
            Status = 1,
        }).ExecuteCommandAsync();
        await _db.Insertable(new ContainerDetail
        {
            DetailCode = "D1",
            ContainerCode = "C1",
            ProductCode = "P1",
            LoadingQuantity = 12m,
        }).ExecuteCommandAsync();

        var outside = await CreateService().GetContainersAsync(
            CreateRequestWithPeriods(
                containerDate.AddDays(1),
                containerDate,
                containerDate,
                currentProductCode: "P1"
            ),
            branchCodes: null
        );

        Assert.Empty(outside.Data!);
    }

    [Fact]
    public async Task Orders_只读orderShipmentPeriod()
    {
        var orderDate = new DateTime(2026, 8, 11);
        await _db.Insertable(new WareHouseOrder
        {
            OrderGUID = "O1",
            OrderNo = "SO1",
            StoreCode = "B1",
            FlowStatus = 1,
            OrderDate = orderDate,
        }).ExecuteCommandAsync();
        await _db.Insertable(new WareHouseOrderDetails
        {
            DetailGUID = "OD1",
            OrderGUID = "O1",
            ProductCode = "P1",
            Quantity = 8m,
        }).ExecuteCommandAsync();

        var outside = await CreateService().GetOrdersAsync(
            CreateRequestWithPeriods(
                orderDate.AddDays(1),
                orderDate.AddDays(-1),
                orderDate,
                currentProductCode: "P1"
            ),
            branchCodes: null
        );

        Assert.Empty(outside.Data!);
    }

    [Fact]
    public async Task OrderShipmentDaily_增加订货量()
    {
        var date = new DateTime(2026, 8, 10);
        await _db.Insertable(new WareHouseOrder
        {
            OrderGUID = "O1",
            OrderNo = "SO1",
            StoreCode = "B1",
            FlowStatus = 1,
            OrderDate = date,
        }).ExecuteCommandAsync();
        await _db.Insertable(new WareHouseOrderDetails
        {
            DetailGUID = "OD1",
            OrderGUID = "O1",
            ProductCode = "P1",
            Quantity = 8m,
        }).ExecuteCommandAsync();

        var result = await CreateService().GetOrderShipmentDailyAsync(
            CreateRequest(date, currentProductCode: "P1"),
            branchCodes: null
        );

        var row = Assert.Single(result.Data!);
        Assert.Equal(8m, row.OrderedQuantity);
    }

    [Fact]
    public async Task ProductDaily_只返回货柜期间进货量()
    {
        var date = new DateTime(2026, 8, 10);
        await _db.Insertable(new Container
        {
            ContainerCode = "C1",
            ContainerNumber = "OOLU001",
            ActualArrivalDate = date,
            Status = 1,
        }).ExecuteCommandAsync();
        await _db.Insertable(new ContainerDetail
        {
            DetailCode = "D1",
            ContainerCode = "C1",
            ProductCode = "P1",
            LoadingQuantity = 12m,
        }).ExecuteCommandAsync();

        var result = await CreateService().GetProductDailyAsync(
            CreateRequest(date, currentProductCode: "P1"),
            branchCodes: null
        );

        var row = Assert.Single(result.Data!);
        Assert.Equal(12m, row.InboundQuantity);
        Assert.Equal(0m, row.OrderedQuantity);
        Assert.Equal(0, row.NetSalesQuantity);
    }

    [Fact]
    public async Task Summary_结束日时分秒按半开区间计入()
    {
        var start = new DateTime(2026, 8, 10);
        await _db.Insertable(new WarehouseProduct { ProductCode = "P1", IsDeleted = false }).ExecuteCommandAsync();
        await _db.Insertable(new Product { UUID = "u1", ProductCode = "P1", ProductName = "商品" }).ExecuteCommandAsync();
        await _db.Insertable(new WareHouseOrder
        {
            OrderGUID = "O1",
            OrderNo = "SO1",
            FlowStatus = 1,
            OrderDate = new DateTime(2026, 8, 10, 23, 30, 0),
        }).ExecuteCommandAsync();
        await _db.Insertable(new WareHouseOrderDetails
        {
            DetailGUID = "OD1",
            OrderGUID = "O1",
            ProductCode = "P1",
            Quantity = 8m,
        }).ExecuteCommandAsync();

        var result = await CreateService().GetSummaryAsync(CreateRequest(start), branchCodes: null);

        Assert.Equal(8m, result.Data!.Items.Single().Metrics.OrderedQuantity);
    }

    [Fact]
    public async Task Branches_只返回销售期间指标()
    {
        var date = new DateTime(2026, 8, 10);
        await _db.Insertable(new Store { StoreGUID = "s1", StoreCode = "B1", StoreName = "分店一" }).ExecuteCommandAsync();
        await _db.Insertable(new WarehouseProduct { ProductCode = "P1", IsDeleted = false }).ExecuteCommandAsync();
        await _db.Insertable(new WareHouseOrder
        {
            OrderGUID = "O1",
            OrderNo = "SO1",
            StoreCode = "B1",
            FlowStatus = 1,
            OrderDate = date,
            OutboundDate = date,
        }).ExecuteCommandAsync();
        await _db.Insertable(new WareHouseOrderDetails
        {
            DetailGUID = "OD1",
            OrderGUID = "O1",
            ProductCode = "P1",
            Quantity = 70m,
            AllocQuantity = 70m,
        }).ExecuteCommandAsync();
        await _db.Insertable(CreateStatistic(date, "B1", "AU1", "P1", 60, 600m)).ExecuteCommandAsync();

        var result = await CreateService().GetBranchesAsync(
            CreateRequest(date, currentProductCode: "P1"),
            branchCodes: null
        );

        var branch = Assert.Single(result.Data!);
        Assert.Equal(0m, branch.OrderedQuantity);
        Assert.Equal(0m, branch.ShippedQuantity);
        Assert.Equal(60, branch.NetSalesQuantity);
        Assert.Equal(600m, branch.NetSalesAmount);
        Assert.Null(branch.SellThroughRate);
        Assert.Equal(10m, branch.AverageUnitPrice);
    }

    [Fact]
    public async Task Branches_空授权分店范围不得退化为全分店()
    {
        var date = new DateTime(2026, 8, 10);
        await _db.Insertable(new Store { StoreGUID = "s1", StoreCode = "B1", StoreName = "分店一" }).ExecuteCommandAsync();
        await _db.Insertable(new WarehouseProduct { ProductCode = "P1", IsDeleted = false }).ExecuteCommandAsync();
        await _db.Insertable(CreateStatistic(date, "B1", "AU1", "P1", 5, 50m)).ExecuteCommandAsync();

        var request = CreateRequest(date, currentProductCode: "P1");
        var branches = await CreateService().GetBranchesAsync(request, branchCodes: new List<string>());
        var summary = await CreateService().GetSummaryAsync(request, branchCodes: new List<string>());

        Assert.Empty(branches.Data!);
        var metrics = Assert.Single(summary.Data!.Items).Metrics;
        Assert.Equal(0, metrics.NetSalesQuantity);
        Assert.Equal(0m, metrics.NetSalesAmount);
        Assert.Null(metrics.AverageUnitPrice);
    }

    [Fact]
    public async Task Shipments_返回分店销量和POS状态且销售日期参与缓存键()
    {
        var shipmentDate = new DateTime(2026, 8, 10);
        var firstSalesDate = new DateTime(2026, 8, 11);
        var secondSalesDate = new DateTime(2026, 8, 12);
        await _db.Insertable(new[]
        {
            new Store { StoreGUID = "s1", StoreCode = "B1", StoreName = "启用", IsActive = true },
            new Store { StoreGUID = "s2", StoreCode = "B2", StoreName = "停用", IsActive = false },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new WareHouseOrder { OrderGUID = "O1", OrderNo = "SO1", StoreCode = "B1", FlowStatus = 1, OutboundDate = shipmentDate },
            new WareHouseOrder { OrderGUID = "O4", OrderNo = "SO4", StoreCode = "B1", FlowStatus = 1, OutboundDate = shipmentDate },
            new WareHouseOrder { OrderGUID = "O2", OrderNo = "SO2", StoreCode = "B2", FlowStatus = 1, OutboundDate = shipmentDate },
            new WareHouseOrder { OrderGUID = "O3", OrderNo = "SO3", StoreCode = "B3", FlowStatus = 1, OutboundDate = shipmentDate },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new WareHouseOrderDetails { DetailGUID = "D1", OrderGUID = "O1", ProductCode = "P1", AllocQuantity = 1m },
            new WareHouseOrderDetails { DetailGUID = "D4", OrderGUID = "O4", ProductCode = "P1", AllocQuantity = 2m },
            new WareHouseOrderDetails { DetailGUID = "D2", OrderGUID = "O2", ProductCode = "P1", AllocQuantity = 1m },
            new WareHouseOrderDetails { DetailGUID = "D3", OrderGUID = "O3", ProductCode = "P1", AllocQuantity = 1m },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            CreateStatistic(firstSalesDate, "B1", "AU1", "P1", 5, 50m),
            CreateStatistic(firstSalesDate, "B2", "AU1", "P1", -2, -20m),
            CreateStatistic(firstSalesDate, "B1", "AU1", "P2", 100, 1000m),
            CreateStatistic(secondSalesDate, "B1", "AU1", "P1", 9, 90m),
        }).ExecuteCommandAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(cache);
        var firstRequest = CreateRequestWithPeriods(shipmentDate, shipmentDate, firstSalesDate, "P1");
        var first = await service.GetShipmentsAsync(firstRequest, branchCodes: null);
        var firstRows = first.Data!;
        Assert.Equal(4, firstRows.Count);
        Assert.Equal(new[] { 5, 5 }, firstRows.Where(row => row.BranchCode == "B1").Select(row => row.NetSalesQuantity));
        Assert.Equal(-2, firstRows.Single(row => row.BranchCode == "B2").NetSalesQuantity);
        Assert.All(firstRows.Where(row => row.BranchCode == "B1"), row => Assert.True(row.PosEnabled));
        Assert.False(firstRows.Single(row => row.BranchCode == "B2").PosEnabled);
        Assert.Null(firstRows.Single(row => row.BranchCode == "B3").PosEnabled);
        Assert.Equal(0, firstRows.Single(row => row.BranchCode == "B3").NetSalesQuantity);

        var unauthorized = await service.GetShipmentsAsync(firstRequest, branchCodes: new List<string>());
        Assert.Empty(unauthorized.Data!);

        var secondRequest = CreateRequestWithPeriods(shipmentDate, shipmentDate, secondSalesDate, "P1");
        var second = await service.GetShipmentsAsync(secondRequest, branchCodes: null);
        Assert.Equal(
            new[] { 9, 9 },
            second.Data!.Where(row => row.BranchCode == "B1").Select(row => row.NetSalesQuantity)
        );
    }

    [Fact]
    public async Task Branches_按净销量降序并以分店代码稳定排序()
    {
        var date = new DateTime(2026, 8, 10);
        await _db.Insertable(new[]
        {
            CreateStatistic(date, "B2", "AU1", "P1", 3, 30m),
            CreateStatistic(date, "B1", "AU1", "P1", 3, 30m),
            CreateStatistic(date, "B3", "AU1", "P1", 8, 80m),
        }).ExecuteCommandAsync();

        var result = await CreateService().GetBranchesAsync(CreateRequest(date, currentProductCode: "P1"), null);

        Assert.Equal(new[] { "B3", "B1", "B2" }, result.Data!.Select(row => row.BranchCode));
    }

    [Fact]
    public async Task SalesDaily_只返回销售期间并保留负销量()
    {
        var date = new DateTime(2026, 8, 10);
        await _db.Insertable(CreateStatistic(date, "B1", "AU1", "P1", -2, -20m)).ExecuteCommandAsync();

        var result = await CreateService().GetSalesDailyAsync(
            CreateRequest(date, currentProductCode: "P1"),
            branchCodes: null
        );

        var row = Assert.Single(result.Data!);
        Assert.Equal(-2, row.NetSalesQuantity);
        Assert.Equal(-20m, row.NetSalesAmount);
        Assert.Equal(10m, row.AverageUnitPrice);
        Assert.Equal(0m, row.InboundQuantity);
        Assert.Equal(0m, row.OrderedQuantity);
    }

    [Fact]
    public async Task Summary_负销量保留且零销量均价为null()
    {
        var date = new DateTime(2026, 8, 10);
        await _db.Insertable(new[]
        {
            new WarehouseProduct { ProductCode = "P1", IsDeleted = false },
            new WarehouseProduct { ProductCode = "P2", IsDeleted = false },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new Product { UUID = "u1", ProductCode = "P1", ProductName = "负销量" },
            new Product { UUID = "u2", ProductCode = "P2", ProductName = "零销量" },
        }).ExecuteCommandAsync();
        await _db.Insertable(CreateStatistic(date, "B1", "AU1", "P1", -2, -20m)).ExecuteCommandAsync();

        var result = await CreateService().GetSummaryAsync(CreateRequest(date), branchCodes: null);

        var negative = result.Data!.Items.Single(row => row.ProductCode == "P1");
        Assert.Equal(-2, negative.Metrics.NetSalesQuantity);
        Assert.Equal(10m, negative.Metrics.AverageUnitPrice);
        var zero = result.Data.Items.Single(row => row.ProductCode == "P2");
        Assert.Equal(0, zero.Metrics.NetSalesQuantity);
        Assert.Null(zero.Metrics.AverageUnitPrice);
    }

    [Theory]
    [InlineData("P1")]
    [InlineData(" P1 ")]
    [InlineData("\tP1\t")]
    [InlineData("p1")]
    public async Task Summary_编码预查询保留旧编码归一化语义(string storedCode)
    {
        var date = new DateTime(2026, 8, 10);
        var normalizedCode = storedCode.Trim();
        await _db.Insertable(new[]
        {
            new WarehouseProduct { ProductCode = storedCode, IsDeleted = false },
            new WarehouseProduct { ProductCode = "UNSELECTED", IsDeleted = false },
        }).ExecuteCommandAsync();
        await _db.Insertable(new Product
        {
            UUID = "u1", ProductCode = normalizedCode, ProductName = "所选商品",
        }).ExecuteCommandAsync();
        await _db.Insertable(CreateStatistic(date, "B1", "AU1", normalizedCode, 3, 45m)).ExecuteCommandAsync();
        var request = CreateRequest(date);
        request.Selection = new WarehouseProductFlowAnalysisSelectionDto
        {
            Mode = "included", IncludedProductCodes = new List<string> { " P1 " },
        };

        var result = await CreateService().GetSummaryAsync(request, branchCodes: null);

        var row = Assert.Single(result.Data!.Items);
        Assert.Equal(normalizedCode, row.ProductCode);
        Assert.Equal("所选商品", row.ProductName);
        Assert.Equal(3, row.Metrics.NetSalesQuantity);
        Assert.Equal(45m, row.Metrics.NetSalesAmount);
    }

    [Fact]
    public async Task Summary_单选查询量不随未选商品数增加()
    {
        var date = new DateTime(2026, 8, 10);
        await _db.Insertable(new WarehouseProduct { ProductCode = "P1", IsDeleted = false }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new Product { UUID = "a-product", ProductCode = "P1", ProductName = "确定主档" },
            new Product { UUID = "z-product", ProductCode = "P1", ProductName = "另一条主档" },
        }).ExecuteCommandAsync();
        await _db.Insertable(CreateStatistic(date, "B1", "AU1", "P1", -2, -20m)).ExecuteCommandAsync();
        var request = CreateRequest(date, currentProductCode: "P1");
        request.Selection = new WarehouseProductFlowAnalysisSelectionDto
        {
            Mode = " included ",
            IncludedProductCodes = new List<string> { " p1 ", "P1", "不存在" },
        };
        var queryCount = 0;
        _db.Aop.OnLogExecuting = (_, _) => queryCount++;
        var before = await CreateService().GetSummaryAsync(request, branchCodes: null);
        var singleProductQueryCount = queryCount;
        _db.Aop.OnLogExecuting = null;

        // 未选择的主档跨多个批次，不能让单商品请求逐批补读无关商品及供应商。
        foreach (var chunk in Enumerable.Range(1, 1001).Select(index => new WarehouseProduct
        {
            ProductCode = $"UNSELECTED-{index:D4}",
            IsDeleted = false,
        }).Chunk(500))
            await _db.Insertable(chunk).ExecuteCommandAsync();

        queryCount = 0;
        _db.Aop.OnLogExecuting = (_, _) => queryCount++;
        var after = await CreateService().GetSummaryAsync(request, branchCodes: null);
        Assert.Equal(singleProductQueryCount, queryCount);
        Assert.Equal(before.Data!.Total, after.Data!.Total);
        var row = Assert.Single(after.Data.Items);
        Assert.Equal("P1", row.ProductCode);
        Assert.Equal("确定主档", row.ProductName);
        Assert.Equal(-2, row.Metrics.NetSalesQuantity);
        Assert.Equal(-20m, row.Metrics.NetSalesAmount);
    }

    [Theory]
    [InlineData("商品", 1)]
    [InlineData("不匹配", 0)]
    public async Task Summary_单选仍与筛选条件取交集(string keyword, int expectedCount)
    {
        var date = new DateTime(2026, 8, 10);
        await _db.Insertable(new[]
        {
            new WarehouseProduct { ProductCode = "P1", IsDeleted = false },
            new WarehouseProduct { ProductCode = "P2", IsDeleted = false },
            new WarehouseProduct { ProductCode = "DELETED", IsDeleted = true },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new Product { UUID = "u1", ProductCode = "P1", ProductName = "商品" },
            new Product { UUID = "u2", ProductCode = "P2", ProductName = "商品" },
        }).ExecuteCommandAsync();
        var request = CreateRequest(date);
        request.Filter.Keyword = keyword;
        request.Selection = new WarehouseProductFlowAnalysisSelectionDto
        {
            Mode = "included",
            IncludedProductCodes = new List<string> { "P1", "DELETED", "不存在" },
        };

        var result = await CreateService().GetSummaryAsync(request, branchCodes: null);

        Assert.Equal(expectedCount, result.Data!.Total);
        Assert.All(result.Data.Items, row => Assert.Equal("P1", row.ProductCode));
    }

    [Fact]
    public async Task Summary_当前商品独立于分页返回()
    {
        var date = new DateTime(2026, 8, 10);
        await _db.Insertable(new[]
        {
            new WarehouseProduct { ProductCode = "P1", IsDeleted = false },
            new WarehouseProduct { ProductCode = "P2", IsDeleted = false },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new Product { UUID = "u1", ProductCode = "P1", ItemNumber = "A" },
            new Product { UUID = "u2", ProductCode = "P2", ItemNumber = "B" },
        }).ExecuteCommandAsync();

        var request = CreateRequest(date, currentProductCode: "P2");
        request.PageSize = 1;
        request.SortBy = "itemNumber";
        request.SortDirection = "asc";
        var result = await CreateService().GetSummaryAsync(request, branchCodes: null);

        Assert.Equal("P1", Assert.Single(result.Data!.Items).ProductCode);
        Assert.Equal("P2", result.Data.CurrentProduct?.ProductCode);
    }

    [Fact]
    public async Task Candidates_结构化缓存键区分列表边界()
    {
        await _db.Insertable(new[]
        {
            new WarehouseCategory { CategoryGUID = "A", CategoryName = "A" },
            new WarehouseCategory { CategoryGUID = "B", CategoryName = "B" },
            new WarehouseCategory { CategoryGUID = "A,B", CategoryName = "A,B" },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new WarehouseProduct { ProductCode = "P1", IsDeleted = false },
            new WarehouseProduct { ProductCode = "P2", IsDeleted = false },
            new WarehouseProduct { ProductCode = "P3", IsDeleted = false },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new Product { UUID = "u1", ProductCode = "P1", WarehouseCategoryGUID = "A" },
            new Product { UUID = "u2", ProductCode = "P2", WarehouseCategoryGUID = "B" },
            new Product { UUID = "u3", ProductCode = "P3", WarehouseCategoryGUID = "A,B" },
        }).ExecuteCommandAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(cache);

        var listRequest = CreateCandidateRequest(categoryGuids: new List<string> { "A", "B" });
        var commaRequest = CreateCandidateRequest(categoryGuids: new List<string> { "A,B" });
        var listResult = await service.GetCandidatesAsync(listRequest);
        var commaResult = await service.GetCandidatesAsync(commaRequest);

        Assert.Equal(new[] { "P1", "P2" }, listResult.Data!.Items.Select(row => row.ProductCode).OrderBy(code => code));
        Assert.Equal("P3", Assert.Single(commaResult.Data!.Items).ProductCode);
    }

    [Fact]
    public async Task Candidates_等价分页排序请求共享归一化缓存键()
    {
        await _db.Insertable(new WarehouseProduct { ProductCode = "P1", IsDeleted = false }).ExecuteCommandAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(cache);
        var firstRequest = CreateCandidateRequest();
        firstRequest.PageNumber = 0;
        firstRequest.PageSize = 600;
        firstRequest.SortBy = "ItemNumber";
        firstRequest.SortDirection = "ASC";

        var first = await service.GetCandidatesAsync(firstRequest);
        await _db.Insertable(new WarehouseProduct { ProductCode = "P2", IsDeleted = false }).ExecuteCommandAsync();

        var equivalentRequest = CreateCandidateRequest();
        equivalentRequest.PageNumber = 1;
        equivalentRequest.PageSize = 500;
        equivalentRequest.SortBy = "itemnumber";
        equivalentRequest.SortDirection = "asc";
        var cached = await service.GetCandidatesAsync(equivalentRequest);

        Assert.Same(first, cached);
        Assert.Equal(1, cached.Data!.Total);
        Assert.Equal(2, cache.Count); // 候选响应 + 活跃 ProductCode 重复状态元数据。
    }

    [Fact]
    public async Task Candidates_2100条以上只返回当前页且分页稳定无重复遗漏()
    {
        const int total = 2105;
        const int pageSize = 50;
        var warehouseProducts = Enumerable
            .Range(1, total)
            .Select(index => new WarehouseProduct
            {
                ProductCode = $"W{index:D5}",
                IsDeleted = false,
            })
            .ToList();
        foreach (var chunk in warehouseProducts.Chunk(500))
            await _db.Insertable(chunk).ExecuteCommandAsync();

        var service = CreateService();
        var request = CreateCandidateRequest();
        request.PageNumber = 1;
        request.PageSize = pageSize;
        var firstPageQueryCount = 0;
        _db.Aop.OnLogExecuting = (_, _) => firstPageQueryCount++;

        var firstPage = await service.GetCandidatesAsync(request);
        Assert.True(firstPage.Success);
        Assert.Equal(total, firstPage.Data!.Total);
        Assert.Equal(pageSize, firstPage.Data.Items.Count);
        Assert.InRange(firstPageQueryCount, 1, 8);
        Assert.Equal(
            Enumerable.Range(1, pageSize).Select(index => $"W{index:D5}"),
            firstPage.Data.Items.Select(row => row.ProductCode)
        );
        _db.Aop.OnLogExecuting = null;

        var pageCount = (total + pageSize - 1) / pageSize;
        var allPageCodes = new List<string>();
        for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
        {
            request.PageNumber = pageNumber;
            var page = await service.GetCandidatesAsync(request);
            Assert.Equal(pageNumber, page.Data!.PageNumber);
            allPageCodes.AddRange(page.Data.Items.Select(row => row.ProductCode));
        }

        Assert.Equal(total, allPageCodes.Count);
        Assert.Equal(allPageCodes.Count, allPageCodes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(
            Enumerable.Range(1, total).Select(index => $"W{index:D5}"),
            allPageCodes
        );
    }

    [Fact]
    public async Task Candidates_缺失商品分类供应商关联仍显示主档()
    {
        await _db.Insertable(new[]
        {
            new WarehouseProduct { ProductCode = "P1", IsDeleted = false },
            new WarehouseProduct { ProductCode = "P2", IsDeleted = false },
            new WarehouseProduct { ProductCode = "P3", IsDeleted = false },
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new Product { UUID = "u2", ProductCode = "P2", ProductName = "有商品无分类" },
            new Product
            {
                UUID = "u3",
                ProductCode = "P3",
                ProductName = "缺失分类和供应商",
                WarehouseCategoryGUID = "cat-missing",
            },
        }).ExecuteCommandAsync();
        await _db.Insertable(new DomesticProduct
        {
            ProductCode = "P3",
            SupplierCode = "SUP-MISSING",
            IsDeleted = false,
        }).ExecuteCommandAsync();

        var result = await CreateService().GetCandidatesAsync(CreateCandidateRequest());

        var rows = result.Data!.Items.ToDictionary(row => row.ProductCode, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(3, rows.Count);

        var missingAll = rows["P1"];
        Assert.Null(missingAll.ItemNumber);
        Assert.Null(missingAll.ProductName);
        Assert.Null(missingAll.CategoryGuid);
        Assert.Null(missingAll.CategoryName);
        Assert.Null(missingAll.SupplierCode);
        Assert.Null(missingAll.SupplierName);

        var missingCategory = rows["P2"];
        Assert.Equal("有商品无分类", missingCategory.ProductName);
        Assert.Null(missingCategory.CategoryGuid);
        Assert.Null(missingCategory.CategoryName);

        var missingNames = rows["P3"];
        Assert.Equal("cat-missing", missingNames.CategoryGuid);
        Assert.Null(missingNames.CategoryName);
        Assert.Equal("SUP-MISSING", missingNames.SupplierCode);
        Assert.Equal("SUP-MISSING", missingNames.SupplierName);
    }

    [Fact]
    public async Task Candidates_供应商编码去空格并确定性优先有效记录()
    {
        await _db.Insertable(new WarehouseProduct
        {
            ProductCode = "P1",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _db.Insertable(new Product
        {
            UUID = "u1",
            ProductCode = "P1",
            ProductName = "供应商归一化商品",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _db.Insertable(new DomesticProduct
        {
            ProductCode = "P1",
            SupplierCode = " SUP1 ",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _db.Insertable(new[]
        {
            new ChinaSupplier
            {
                Guid = "001",
                SupplierCode = " SUP1 ",
                SupplierName = "旧供应商名称",
                IsDeleted = true,
            },
            new ChinaSupplier
            {
                Guid = "002",
                SupplierCode = "SUP1",
                SupplierName = "有效供应商名称",
                IsDeleted = false,
            },
        }).ExecuteCommandAsync();

        var result = await CreateService().GetCandidatesAsync(CreateCandidateRequest());

        var item = Assert.Single(result.Data!.Items);
        Assert.Equal("SUP1", item.SupplierCode);
        Assert.Equal("有效供应商名称", item.SupplierName);

        var filtered = await CreateService().GetCandidatesAsync(
            CreateCandidateRequest(supplierCodes: new List<string> { "SUP1" })
        );
        Assert.Equal("P1", Assert.Single(filtered.Data!.Items).ProductCode);
    }

    [Fact]
    public async Task Options_缓存键不随商品筛选或分店范围变化()
    {
        await _db.Insertable(new WarehouseCategory
        {
            CategoryGUID = "cat-1",
            CategoryName = "分类一",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _db.Insertable(new DomesticProduct
        {
            ProductCode = "DP1",
            SupplierCode = "CN1",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _db.Insertable(new ChinaSupplier
        {
            Guid = "CS1",
            SupplierCode = "CN1",
            SupplierName = "供应商一",
            IsDeleted = false,
        }).ExecuteCommandAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(cache);

        var first = await service.GetOptionsAsync(
            new WarehouseProductFlowAnalysisFilterDto { Keyword = "第一次筛选" },
            null
        );
        var second = await service.GetOptionsAsync(
            new WarehouseProductFlowAnalysisFilterDto
            {
                Keyword = "第二次筛选",
                WarehouseCategoryGuids = new List<string> { "cat-1" },
                SupplierCodes = new List<string> { "CN1" },
            },
            new List<string> { "B1", "B2" }
        );

        Assert.Same(first, second);
        // options 响应与可复用的分类层级元数据各占一个固定缓存项。
        Assert.Equal(2, cache.Count);
        Assert.Single(second.Data!.WarehouseCategories);
        Assert.Single(second.Data.DomesticSuppliers);

        await _db.Insertable(new DomesticProduct
        {
            ProductCode = "DP2",
            SupplierCode = "CN2",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _db.Insertable(new ChinaSupplier
        {
            Guid = "CS2",
            SupplierCode = "CN2",
            SupplierName = "供应商二",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _db.Insertable(new WarehouseCategory
        {
            CategoryGUID = "cat-2",
            CategoryName = "分类二",
            IsDeleted = false,
        }).ExecuteCommandAsync();

        var refreshed = await service.GetOptionsAsync(
            new WarehouseProductFlowAnalysisFilterDto(),
            null,
            forceRefresh: true
        );

        Assert.NotSame(first, refreshed);
        Assert.Equal(2, refreshed.Data!.WarehouseCategories.Count);
        Assert.Equal(2, refreshed.Data!.DomesticSuppliers.Count);
        Assert.Equal(new[] { "CN1", "CN2" }, refreshed.Data.DomesticSuppliers.Select(row => row.Code));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public async Task Candidates_ForceRefresh重新查询并覆盖缓存()
    {
        await _db.Insertable(new WarehouseProduct { ProductCode = "P1", IsDeleted = false }).ExecuteCommandAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(cache);
        var request = CreateCandidateRequest();

        var first = await service.GetCandidatesAsync(request);
        Assert.Single(first.Data!.Items);
        Assert.Equal(1, first.Data.Total);

        await _db.Insertable(new WarehouseProduct { ProductCode = "P2", IsDeleted = false }).ExecuteCommandAsync();

        var cached = await service.GetCandidatesAsync(request);
        Assert.Single(cached.Data!.Items);
        Assert.Equal(1, cached.Data.Total);

        request.ForceRefresh = true;
        var refreshed = await service.GetCandidatesAsync(request);
        Assert.Equal(2, refreshed.Data!.Total);
        Assert.Equal(new[] { "P1", "P2" }, refreshed.Data.Items.Select(row => row.ProductCode));
    }

    [Fact]
    public async Task Candidates_异常日志记录当前阶段()
    {
        var db = new Mock<ISqlSugarClient>();
        db.Setup(client => client.Queryable<WarehouseProduct>())
            .Throws(new InvalidOperationException("boom"));
        var logger = new RecordingLogger<WarehouseProductFlowAnalysisService>();
        var service = new WarehouseProductFlowAnalysisService(
            CreateSqlSugarContext(db.Object),
            new MemoryCache(new MemoryCacheOptions()),
            logger
        );

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetCandidatesAsync(CreateCandidateRequest())
        );

        Assert.Contains(
            logger.Messages,
            message =>
                message.Level == LogLevel.Error
                && message.Text.Contains("Stage=filter", StringComparison.OrdinalIgnoreCase)
        );
    }

    private WarehouseProductFlowAnalysisService CreateService(IMemoryCache? cache = null)
    {
        return new WarehouseProductFlowAnalysisService(
            CreateSqlSugarContext(_db),
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            NullLogger<WarehouseProductFlowAnalysisService>.Instance
        );
    }

    private static WarehouseProductFlowAnalysisRequest CreateRequest(
        DateTime date,
        DateTime? endDate = null,
        string? categoryGuid = null,
        string? documentKeyword = null,
        string? currentProductCode = null
    )
    {
        return new WarehouseProductFlowAnalysisRequest
        {
            Filter = new WarehouseProductFlowAnalysisFilterDto
            {
                WarehouseCategoryGuids = string.IsNullOrWhiteSpace(categoryGuid)
                    ? null
                    : new List<string> { categoryGuid },
                DocumentKeyword = documentKeyword,
            },
            Periods = CreatePeriods(date, endDate ?? date),
            Selection = new WarehouseProductFlowAnalysisSelectionDto { Mode = "allFiltered" },
            CurrentProductCode = currentProductCode,
        };
    }

    private static WarehouseProductFlowAnalysisRequest CreateRequestWithPeriods(
        DateTime containerDate,
        DateTime orderShipmentDate,
        DateTime salesDate,
        string? currentProductCode = null,
        string? documentKeyword = null
    )
    {
        return new WarehouseProductFlowAnalysisRequest
        {
            Filter = new WarehouseProductFlowAnalysisFilterDto
            {
                DocumentKeyword = documentKeyword,
            },
            Periods = new WarehouseProductFlowPeriodsDto
            {
                ContainerPeriod = CreatePeriod(containerDate, containerDate),
                OrderShipmentPeriod = CreatePeriod(orderShipmentDate, orderShipmentDate),
                SalesPeriod = CreatePeriod(salesDate, salesDate),
            },
            Selection = new WarehouseProductFlowAnalysisSelectionDto { Mode = "allFiltered" },
            CurrentProductCode = currentProductCode,
        };
    }

    private static WarehouseProductFlowCandidateRequest CreateCandidateRequest(
        string? keyword = null,
        List<string>? categoryGuids = null,
        List<string>? supplierCodes = null,
        string? documentKeyword = null,
        string? sortBy = null,
        string? sortDirection = null
    )
    {
        return new WarehouseProductFlowCandidateRequest
        {
            Filter = new WarehouseProductFlowAnalysisFilterDto
            {
                Keyword = keyword,
                WarehouseCategoryGuids = categoryGuids,
                SupplierCodes = supplierCodes,
                DocumentKeyword = documentKeyword,
            },
            SortBy = sortBy,
            SortDirection = sortDirection,
        };
    }

    private static WarehouseProductFlowPeriodsDto CreatePeriods(DateTime startDate, DateTime endDate)
    {
        return new WarehouseProductFlowPeriodsDto
        {
            ContainerPeriod = CreatePeriod(startDate, endDate),
            OrderShipmentPeriod = CreatePeriod(startDate, endDate),
            SalesPeriod = CreatePeriod(startDate, endDate),
        };
    }

    private static WarehouseProductFlowDatePeriodDto CreatePeriod(DateTime startDate, DateTime endDate)
    {
        return new WarehouseProductFlowDatePeriodDto
        {
            StartDate = startDate,
            EndDate = endDate,
        };
    }

    private static ProductStoreDailySalesStatistic CreateStatistic(
        DateTime date,
        string branchCode,
        string supplierCode,
        string productCode,
        int quantity,
        decimal amount
    )
    {
        return new ProductStoreDailySalesStatistic
        {
            Date = date,
            BranchCode = branchCode,
            SupplierCode = supplierCode,
            ProductCode = productCode,
            TotalQuantity = quantity,
            TotalAmount = amount,
        };
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

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (File.Exists(_dbPath))
            SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Text)> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Messages.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
