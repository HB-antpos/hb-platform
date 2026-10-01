using System.Reflection;
using System.Runtime.CompilerServices;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Mappings.Profiles;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class ContainerReactServiceDetailQueryTests : IDisposable
{
    private readonly string _localDbPath;
    private readonly string _hbSalesDbPath;
    private readonly SqliteConnection _localConnection;
    private readonly SqliteConnection _hbSalesConnection;
    private readonly SqlSugarClient _localDb;
    private readonly SqlSugarScope _hbSalesDb;

    public ContainerReactServiceDetailQueryTests()
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
            typeof(WarehouseProduct),
            typeof(Product),
            typeof(WarehouseCategory),
            typeof(StoreRetailPrice)
        );
        // 明细查询用商品修改历史判定「本柜新品」；SQLite 下 long 自增主键需手写建表。
        _localDb.Ado.ExecuteCommand(
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
    public async Task QueryContainerDetailsAsync_本柜建档后的商品仍应计为新商品且可筛选排序()
    {
        await SeedContainerAsync("C-OWN", "TGBU5893494");
        // 已由本柜建档：主档存在，但仍是本柜新品。
        await SeedDetailAsync("D-OWN-CREATED", "C-OWN", "P-OWN-CREATED", "HB301", oemPrice: 0m, localExists: true);
        await SeedProductChangeHistoryAsync("P-OWN-CREATED", "Create", "C-OWN");
        // 尚未建档：两种口径都是新商品。
        await SeedDetailAsync("D-OWN-PENDING", "C-OWN", "P-OWN-PENDING", "HB302", oemPrice: 0m, localExists: false);
        // 由其它货柜建档：对本柜来说是已有商品。
        await SeedDetailAsync("D-OWN-OTHER", "C-OWN", "P-OWN-OTHER", "HB303", localExists: true);
        await SeedProductChangeHistoryAsync("P-OWN-OTHER", "Create", "C-EARLIER");
        // 本柜提交只更新了已有商品：不能被误判为新商品。
        await SeedDetailAsync("D-OWN-UPDATED", "C-OWN", "P-OWN-UPDATED", "HB304", localExists: true);
        await SeedProductChangeHistoryAsync("P-OWN-UPDATED", "BatchUpdate", "C-OWN");
        var service = CreateService();

        var all = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto { ContainerGuid = "C-OWN", PageSize = 50 }
        );

        var created = all.Items.Single(x => x.HGUID == "D-OWN-CREATED");
        Assert.False(created.是否新商品);
        Assert.True(created.IsContainerNewProduct);
        Assert.True(all.Items.Single(x => x.HGUID == "D-OWN-PENDING").IsContainerNewProduct);
        Assert.False(all.Items.Single(x => x.HGUID == "D-OWN-OTHER").IsContainerNewProduct);
        Assert.False(all.Items.Single(x => x.HGUID == "D-OWN-UPDATED").IsContainerNewProduct);
        Assert.Equal(2, all.TagStats.New);
        Assert.Equal(2, all.TagStats.Existing);
        // 缺零售价是建档前的操作提醒，仍只统计未建档商品。
        Assert.Equal(1, all.TagStats.NoOemPrice);

        var newOnly = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-OWN",
                PageSize = 50,
                NewProductStates = new List<string> { "new" },
            }
        );
        Assert.Equal(
            new[] { "D-OWN-CREATED", "D-OWN-PENDING" },
            newOnly.Items.Select(x => x.HGUID).OrderBy(x => x).ToArray()
        );

        var existingTag = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-OWN",
                PageSize = 50,
                SelectedTags = new List<string> { "existing" },
            }
        );
        Assert.Equal(
            new[] { "D-OWN-OTHER", "D-OWN-UPDATED" },
            existingTag.Items.Select(x => x.HGUID).OrderBy(x => x).ToArray()
        );

        var sorted = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-OWN",
                PageSize = 50,
                SortBy = "newProduct",
                SortOrder = "descend",
            }
        );
        Assert.All(sorted.Items.Take(2), item => Assert.True(item.IsContainerNewProduct));
        Assert.All(sorted.Items.Skip(2), item => Assert.False(item.IsContainerNewProduct));
    }

    [Fact]
    public async Task QueryContainerDetailsAsync_匹配方式全局路径的新商品统计与标签应按本柜新品口径()
    {
        await SeedContainerAsync("C-OWN-MATCH", "TGBU5893495");
        await SeedDetailAsync("D-OWN-MATCH-CREATED", "C-OWN-MATCH", "P-OWN-MATCH-CREATED", "HB311", localExists: true);
        await SeedProductChangeHistoryAsync("P-OWN-MATCH-CREATED", "Create", "C-OWN-MATCH");
        await SeedDetailAsync("D-OWN-MATCH-OTHER", "C-OWN-MATCH", "P-OWN-MATCH-OTHER", "HB312", localExists: true);
        var service = CreateService();

        var result = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-OWN-MATCH",
                PageSize = 50,
                MatchTypes = new List<string> { "productCode", "supplierItem", "unmatched" },
                SelectedTags = new List<string> { "new" },
            }
        );

        Assert.Equal(1, result.TagStats.New);
        Assert.Equal(1, result.TagStats.Existing);
        var item = Assert.Single(result.Items);
        Assert.Equal("D-OWN-MATCH-CREATED", item.HGUID);
        Assert.True(item.IsContainerNewProduct);
        Assert.False(item.是否新商品);
    }

    [Fact]
    public async Task GetContainerProductsAsync_即将上新列表应保留本柜建档商品的新品标记()
    {
        await SeedContainerAsync("C-OWN-SOON", "OOLU6966452");
        await SeedDetailAsync("D-OWN-SOON-CREATED", "C-OWN-SOON", "P-OWN-SOON-CREATED", "HB321", localExists: true);
        await SeedProductChangeHistoryAsync("P-OWN-SOON-CREATED", "Create", "C-OWN-SOON");
        await SeedDetailAsync("D-OWN-SOON-REPEAT", "C-OWN-SOON", "P-OWN-SOON-REPEAT", "HB322", localExists: true);
        var service = CreateService();

        var products = await service.GetContainerProductsAsync("C-OWN-SOON");

        var created = products.Single(x => x.HGUID == "D-OWN-SOON-CREATED");
        Assert.False(created.是否新商品);
        Assert.True(created.IsContainerNewProduct);
        Assert.False(products.Single(x => x.HGUID == "D-OWN-SOON-REPEAT").IsContainerNewProduct);
    }

    [Fact]
    public async Task GetContainerDetailAsync_应只返回货柜头且不预加载明细()
    {
        await SeedContainerAsync("C-HEAD", "CSLU6099486");
        await SeedDetailAsync("D-HEAD-1", "C-HEAD", "P-HEAD-1", "HB001");
        var service = CreateService();

        var detail = await service.GetContainerDetailAsync("C-HEAD");

        Assert.NotNull(detail);
        Assert.Equal("C-HEAD", detail!.HGUID);
        Assert.Equal("CSLU6099486", detail.货柜编号);
        Assert.Empty(detail.Details);
    }

    [Fact]
    public async Task GetContainersAsync_列头过滤应作用于全列表并更新总数()
    {
        await SeedContainerAsync(
            "C-LIST-1",
            "FFAU 7818368",
            loadingDate: new DateTime(2026, 6, 17),
            estimatedArrivalDate: new DateTime(2026, 7, 15),
            totalPieces: 909m,
            totalAmount: 339615m,
            totalVolume: 67.04m,
            status: 0
        );
        await SeedContainerAsync(
            "C-LIST-2",
            "CSGU7035442",
            loadingDate: new DateTime(2026, 6, 16),
            estimatedArrivalDate: new DateTime(2026, 7, 14),
            totalPieces: 1185m,
            totalAmount: 300397.50m,
            totalVolume: 68m,
            status: 1
        );
        await SeedContainerAsync(
            "C-LIST-3",
            "CSGU7030456",
            loadingDate: new DateTime(2026, 6, 16),
            estimatedArrivalDate: new DateTime(2026, 7, 14),
            totalPieces: 756m,
            totalAmount: 301886.40m,
            totalVolume: 63.56m,
            status: 1
        );
        var service = CreateService(CreateContainerListMapper());

        var result = await service.GetContainersAsync(
            new ContainerQueryRequest
            {
                Page = 1,
                PageSize = 20,
                ContainerNumberFilter = "CSGU",
                LoadingDateStart = new DateTime(2026, 6, 16),
                LoadingDateEnd = new DateTime(2026, 6, 16),
                EstimatedArrivalDateStart = new DateTime(2026, 7, 14),
                EstimatedArrivalDateEnd = new DateTime(2026, 7, 14),
                TotalPiecesMin = 1000m,
                TotalAmountMax = 310000m,
                TotalVolumeMin = 60m,
                TotalVolumeMax = 70m,
                Statuses = new List<int> { 1 },
            }
        );

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("CSGU7035442", Assert.Single(result.Containers).货柜编号);
    }

    [Fact]
    public async Task GetContainersAsync_实际到货日期结束日应包含整天()
    {
        await SeedContainerAsync(
            "C-ACTUAL-1",
            "OOLU9955404",
            actualArrivalDate: new DateTime(2026, 6, 8, 16, 30, 0),
            status: 2
        );
        await SeedContainerAsync(
            "C-ACTUAL-2",
            "FFAU2703638",
            actualArrivalDate: new DateTime(2026, 6, 9),
            status: 2
        );
        var service = CreateService(CreateContainerListMapper());

        var result = await service.GetContainersAsync(
            new ContainerQueryRequest
            {
                Page = 1,
                PageSize = 20,
                ActualArrivalDateStart = new DateTime(2026, 6, 8),
                ActualArrivalDateEnd = new DateTime(2026, 6, 8),
            }
        );

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("OOLU9955404", Assert.Single(result.Containers).货柜编号);
    }

    [Fact]
    public async Task GetContainersAsync_装柜日期应按装柜日期过滤结束日整天并倒序()
    {
        await SeedContainerAsync(
            "C-LOADING-1",
            "LOAD-1",
            loadingDate: new DateTime(2026, 6, 10, 23, 59, 0),
            estimatedArrivalDate: new DateTime(2026, 1, 1),
            status: 1
        );
        await SeedContainerAsync(
            "C-LOADING-2",
            "LOAD-2",
            loadingDate: new DateTime(2026, 6, 9, 8, 0, 0),
            estimatedArrivalDate: new DateTime(2026, 12, 31),
            status: 1
        );
        await SeedContainerAsync(
            "C-LOADING-3",
            "LOAD-3",
            loadingDate: new DateTime(2026, 6, 11),
            estimatedArrivalDate: new DateTime(2026, 6, 10),
            status: 1
        );
        var service = CreateService(CreateContainerListMapper());

        var result = await service.GetContainersAsync(
            new ContainerQueryRequest
            {
                Page = 1,
                PageSize = 20,
                DateType = "装柜日期",
                StartDate = new DateTime(2026, 6, 9),
                EndDate = new DateTime(2026, 6, 10),
            }
        );

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(new[] { "LOAD-1", "LOAD-2" }, result.Containers.Select(x => x.货柜编号).ToArray());
    }

    [Fact]
    public async Task GetContainersAsync_装柜日期同日分页应按货柜编码稳定排序()
    {
        await SeedContainerAsync(
            "C-SAME-B",
            "SAME-B",
            loadingDate: new DateTime(2026, 6, 10, 12, 0, 0),
            status: 1
        );
        await SeedContainerAsync(
            "C-SAME-A",
            "SAME-A",
            loadingDate: new DateTime(2026, 6, 10, 12, 0, 0),
            status: 1
        );
        var service = CreateService(CreateContainerListMapper());

        var result = await service.GetContainersAsync(
            new ContainerQueryRequest
            {
                Page = 1,
                PageSize = 1,
                DateType = "装柜日期",
                StartDate = new DateTime(2026, 6, 10),
                EndDate = new DateTime(2026, 6, 10),
            }
        );

        Assert.Equal(2, result.TotalCount);
        Assert.Equal("SAME-A", Assert.Single(result.Containers).货柜编号);
    }

    [Fact]
    public async Task GetContainersAsync_空列头过滤应保持原分页总数()
    {
        await SeedContainerAsync("C-EMPTY-1", "CSGU7035442", status: 1);
        await SeedContainerAsync("C-EMPTY-2", "CSGU7030456", status: 1);
        await SeedContainerAsync("C-EMPTY-3", "FFAU 7818368", status: 0);
        var service = CreateService(CreateContainerListMapper());

        var result = await service.GetContainersAsync(
            new ContainerQueryRequest
            {
                Page = 1,
                PageSize = 2,
                ContainerNumberFilter = " ",
                Statuses = new List<int>(),
            }
        );

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(2, result.Containers.Count);
    }

    [Fact]
    public async Task QueryContainerDetailsAsync_应服务端筛选排序分页并返回全量标签统计()
    {
        await SeedContainerAsync("C-QUERY", "CSLU6099486");
        await SeedDetailAsync("D-1", "C-QUERY", "P-1", "HB010", isActive: true, oemPrice: 3m, importPrice: 2m, localExists: true);
        await SeedDetailAsync("D-2", "C-QUERY", "P-2", "HB002", isActive: false, oemPrice: 0m, importPrice: 0m, localExists: false, minOrderQuantity: 12);
        await SeedDetailAsync("D-3", "C-QUERY", "P-3", "HB001", isActive: true, oemPrice: 4m, importPrice: 5m, localExists: true);
        var service = CreateService();

        var result = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-QUERY",
                PageNumber = 1,
                PageSize = 2,
                ItemNumber = "HB0",
                SortBy = "itemNumber",
                SortOrder = "ascend",
            }
        );

        Assert.Equal(3, result.ItemsTotal);
        Assert.True(result.TotalComputed);
        Assert.True(result.StatsComputed);
        Assert.Equal(1, result.PageNumber);
        Assert.Equal(2, result.PageSize);
        Assert.True(result.HasMore);
        Assert.Equal(new[] { "HB001", "HB002" }, result.Items.Select(x => x.商品信息?.货号).ToArray());
        Assert.Equal(12m, result.Items.Single(x => x.HGUID == "D-2").中包数);
        Assert.Equal(3, result.TagStats.All);
        Assert.Equal(1, result.TagStats.New);
        Assert.Equal(2, result.TagStats.Existing);
        Assert.Equal(1, result.TagStats.NoOemPrice);
        Assert.Equal(1, result.TagStats.AbnormalImport);
        Assert.Equal(2, result.TagStats.Active);
        Assert.Equal(1, result.TagStats.Inactive);
    }

    [Fact]
    public async Task QueryContainerDetailsAsync_无仓库记录的新品应计为下架并可按下架筛选()
    {
        await SeedContainerAsync("C-WH", "CSLU6099487");
        await SeedDetailAsync("D-WH-ACTIVE", "C-WH", "P-WH-ACTIVE", "HB601", isActive: true);
        await SeedDetailAsync("D-WH-INACTIVE", "C-WH", "P-WH-INACTIVE", "HB602", isActive: false);
        // 仓库未到货的新品：没有仓库商品记录，左连接后 IsActive 为 NULL。
        await SeedDetailAsync("D-WH-MISSING", "C-WH", "P-WH-MISSING", "HB603", localExists: false);
        await _localDb.Deleteable<WarehouseProduct>()
            .Where(x => x.ProductCode == "P-WH-MISSING")
            .ExecuteCommandAsync();
        var service = CreateService();

        var stats = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto { ContainerGuid = "C-WH", PageSize = 50, IncludeItems = false }
        );
        Assert.Equal(1, stats.TagStats.Active);
        Assert.Equal(2, stats.TagStats.Inactive);

        var inactiveTag = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-WH",
                PageSize = 50,
                SelectedTags = new List<string> { "inactive" },
            }
        );
        Assert.Equal(2, inactiveTag.ItemsTotal);
        Assert.Equal(
            new[] { "D-WH-INACTIVE", "D-WH-MISSING" },
            inactiveTag.Items.Select(x => x.HGUID).OrderBy(x => x).ToArray()
        );

        var inactiveStatus = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-WH",
                PageSize = 50,
                WarehouseStatus = new List<string> { "inactive" },
            }
        );
        Assert.Equal(
            new[] { "D-WH-INACTIVE", "D-WH-MISSING" },
            inactiveStatus.Items.Select(x => x.HGUID).OrderBy(x => x).ToArray()
        );

        var activeTag = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-WH",
                PageSize = 50,
                SelectedTags = new List<string> { "active" },
            }
        );
        Assert.Equal(new[] { "D-WH-ACTIVE" }, activeTag.Items.Select(x => x.HGUID).ToArray());
    }

    [Fact]
    public async Task QueryContainerDetailsAsync_禁用标签统计时应保留总数但标记未计算统计()
    {
        await SeedContainerAsync("C-NO-STATS", "CSLU6099488");
        await SeedDetailAsync("D-NO-STATS-1", "C-NO-STATS", "P-NO-STATS-1", "HB201", localExists: true);
        await SeedDetailAsync("D-NO-STATS-2", "C-NO-STATS", "P-NO-STATS-2", "HB202", localExists: false);
        var service = CreateService();

        var result = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-NO-STATS",
                PageNumber = 1,
                PageSize = 50,
                IncludeStats = false,
            }
        );

        Assert.Equal(2, result.ItemsTotal);
        Assert.True(result.TotalComputed);
        Assert.False(result.StatsComputed);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(0, result.TagStats.All);
        Assert.Equal(0, result.TagStats.New);
        Assert.Equal(0, result.TagStats.Existing);
    }

    [Fact]
    public async Task QueryContainerDetailsAsync_禁用总数时应多取一条判断是否还有下一页()
    {
        await SeedContainerAsync("C-NO-TOTAL", "CSLU6099489");
        await SeedDetailAsync("D-NO-TOTAL-1", "C-NO-TOTAL", "P-NO-TOTAL-1", "HB301");
        await SeedDetailAsync("D-NO-TOTAL-2", "C-NO-TOTAL", "P-NO-TOTAL-2", "HB302");
        await SeedDetailAsync("D-NO-TOTAL-3", "C-NO-TOTAL", "P-NO-TOTAL-3", "HB303");
        var service = CreateService();

        var firstPage = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-NO-TOTAL",
                PageNumber = 1,
                PageSize = 2,
                IncludeTotal = false,
                IncludeStats = false,
                SortBy = "itemNumber",
                SortOrder = "ascend",
            }
        );
        var lastPage = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-NO-TOTAL",
                PageNumber = 2,
                PageSize = 2,
                IncludeTotal = false,
                IncludeStats = false,
                SortBy = "itemNumber",
                SortOrder = "ascend",
            }
        );

        Assert.False(firstPage.TotalComputed);
        Assert.False(firstPage.StatsComputed);
        Assert.Equal(0, firstPage.ItemsTotal);
        Assert.True(firstPage.HasMore);
        Assert.Equal(new[] { "HB301", "HB302" }, firstPage.Items.Select(x => x.商品信息?.货号).ToArray());
        Assert.False(lastPage.TotalComputed);
        Assert.False(lastPage.StatsComputed);
        Assert.Equal(0, lastPage.ItemsTotal);
        Assert.False(lastPage.HasMore);
        Assert.Equal(new[] { "HB303" }, lastPage.Items.Select(x => x.商品信息?.货号).ToArray());
    }

    [Fact]
    public async Task QueryContainerDetailsAsync_请求标签统计时应同步返回统计内总数()
    {
        await SeedContainerAsync("C-STATS-TOTAL", "CSLU6099490");
        await SeedDetailAsync("D-STATS-TOTAL-1", "C-STATS-TOTAL", "P-STATS-TOTAL-1", "HB401", localExists: true);
        await SeedDetailAsync("D-STATS-TOTAL-2", "C-STATS-TOTAL", "P-STATS-TOTAL-2", "HB402", localExists: false);
        var service = CreateService();

        var result = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-STATS-TOTAL",
                PageNumber = 1,
                PageSize = 1,
                IncludeTotal = false,
                IncludeStats = true,
                SortBy = "itemNumber",
                SortOrder = "ascend",
            }
        );

        Assert.True(result.TotalComputed);
        Assert.True(result.StatsComputed);
        Assert.Equal(2, result.ItemsTotal);
        Assert.Equal(2, result.TagStats.All);
        Assert.True(result.HasMore);
        Assert.Equal(new[] { "HB401" }, result.Items.Select(x => x.商品信息?.货号).ToArray());
    }

    [Fact]
    public async Task QueryContainerDetailsAsync_实时仓库价列取仓库商品且保留明细快照()
    {
        await SeedContainerAsync("C-LAST-PRICE", "CSLU6099501");
        await SeedDetailAsync(
            "D-LAST-PRICE",
            "C-LAST-PRICE",
            "P-LAST-PRICE",
            "HB-LAST",
            oemPrice: 3m,
            importPrice: 2m,
            lastImportPrice: 1.25m,
            lastOemPrice: 2.75m
        );
        await _localDb.Updateable<WarehouseProduct>()
            .SetColumns(x => x.ImportPrice == 9.99m)
            .SetColumns(x => x.OEMPrice == 19.99m)
            .Where(x => x.ProductCode == "P-LAST-PRICE")
            .ExecuteCommandAsync();
        var service = CreateService(CreateContainerDetailMapper());

        var result = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-LAST-PRICE",
                PageNumber = 1,
                PageSize = 50,
            }
        );

        var item = Assert.Single(result.Items);
        Assert.Equal(1.25m, item.LastImportPrice);
        Assert.Equal(2.75m, item.LastOEMPrice);
        Assert.Equal(9.99m, item.WarehouseImportPrice);
        Assert.Equal(19.99m, item.WarehouseOEMPrice);

        var legacyItem = Assert.Single(await service.GetContainerProductsAsync("C-LAST-PRICE"));
        Assert.Equal(1.25m, legacyItem.LastImportPrice);
        Assert.Equal(2.75m, legacyItem.LastOEMPrice);
        Assert.Equal(9.99m, legacyItem.WarehouseImportPrice);
        Assert.Equal(19.99m, legacyItem.WarehouseOEMPrice);

        var filteredItem = Assert.Single(await service.GetFilteredContainerProductsAsync(new ContainerQueryRequest
        {
            SortBy = "ProductCode",
        }));
        Assert.Equal(1.25m, filteredItem.LastImportPrice);
        Assert.Equal(2.75m, filteredItem.LastOEMPrice);
        Assert.Equal(9.99m, filteredItem.WarehouseImportPrice);
        Assert.Equal(19.99m, filteredItem.WarehouseOEMPrice);
    }

    [Fact]
    public async Task QueryContainerDetailsAsync_实时仓库价筛选排序使用仓库商品表()
    {
        await SeedContainerAsync("C-REALTIME-PRICE", "CMAU0000002");
        await SeedDetailAsync(
            "D-REALTIME-A",
            "C-REALTIME-PRICE",
            "P-REALTIME-A",
            "HB-REAL-A",
            importPrice: 5m,
            oemPrice: 1m,
            lastImportPrice: 99m,
            lastOemPrice: 99m,
            warehouseImportPrice: 2.22m,
            warehouseOemPrice: 8.88m
        );
        await SeedDetailAsync(
            "D-REALTIME-B",
            "C-REALTIME-PRICE",
            "P-REALTIME-B",
            "HB-REAL-B",
            importPrice: 6m,
            oemPrice: 2m,
            lastImportPrice: 1m,
            lastOemPrice: 1m,
            warehouseImportPrice: 4.44m,
            warehouseOemPrice: 3.33m
        );
        var service = CreateService();

        var importFiltered = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-REALTIME-PRICE",
                PageNumber = 1,
                PageSize = 50,
                WarehouseImportPriceMin = 4m,
            }
        );
        var retailFiltered = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-REALTIME-PRICE",
                PageNumber = 1,
                PageSize = 50,
                LastOEMPriceMax = 5m,
            }
        );
        var importSorted = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-REALTIME-PRICE",
                PageNumber = 1,
                PageSize = 50,
                SortBy = "warehouseImportPrice",
                SortOrder = "ascend",
            }
        );
        var retailSorted = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-REALTIME-PRICE",
                PageNumber = 1,
                PageSize = 50,
                SortBy = "lastOEMPrice",
                SortOrder = "descend",
            }
        );

        Assert.Equal(new[] { "D-REALTIME-B" }, importFiltered.Items.Select(x => x.HGUID).ToArray());
        Assert.Equal(new[] { "D-REALTIME-B" }, retailFiltered.Items.Select(x => x.HGUID).ToArray());
        Assert.Equal(new[] { "D-REALTIME-A", "D-REALTIME-B" }, importSorted.Items.Select(x => x.HGUID).ToArray());
        Assert.Equal(new[] { "D-REALTIME-A", "D-REALTIME-B" }, retailSorted.Items.Select(x => x.HGUID).ToArray());
    }

    [Fact]
    public async Task QueryContainerDetailsAsync_零售价筛选排序新商品用明细已有商品用仓库()
    {
        await SeedContainerAsync("C-OEM-VISIBLE", "CMAU0000003");
        await SeedDetailAsync(
            "D-OEM-VISIBLE-NEW",
            "C-OEM-VISIBLE",
            "P-OEM-VISIBLE-NEW",
            "HB-OEM-NEW",
            oemPrice: 7m,
            warehouseOemPrice: 1m,
            localExists: false
        );
        await SeedDetailAsync(
            "D-OEM-VISIBLE-EXISTING-HIGH",
            "C-OEM-VISIBLE",
            "P-OEM-VISIBLE-EXISTING-HIGH",
            "HB-OEM-HIGH",
            oemPrice: 1m,
            warehouseOemPrice: 9m,
            localExists: true
        );
        await SeedDetailAsync(
            "D-OEM-VISIBLE-EXISTING-LOW",
            "C-OEM-VISIBLE",
            "P-OEM-VISIBLE-EXISTING-LOW",
            "HB-OEM-LOW",
            oemPrice: 9m,
            warehouseOemPrice: 2m,
            localExists: true
        );
        var service = CreateService();

        var filtered = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-OEM-VISIBLE",
                PageNumber = 1,
                PageSize = 50,
                OemPriceMin = 6m,
            }
        );
        var maxFiltered = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-OEM-VISIBLE",
                PageNumber = 1,
                PageSize = 50,
                OemPriceMax = 6m,
            }
        );
        var sorted = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-OEM-VISIBLE",
                PageNumber = 1,
                PageSize = 50,
                SortBy = "oemPrice",
                SortOrder = "ascend",
            }
        );

        Assert.Equal(
            new[] { "D-OEM-VISIBLE-EXISTING-HIGH", "D-OEM-VISIBLE-NEW" },
            filtered.Items.Select(x => x.HGUID).OrderBy(x => x).ToArray()
        );
        Assert.Equal(new[] { "D-OEM-VISIBLE-EXISTING-LOW" }, maxFiltered.Items.Select(x => x.HGUID).ToArray());
        Assert.Equal(
            new[] { "D-OEM-VISIBLE-EXISTING-LOW", "D-OEM-VISIBLE-NEW", "D-OEM-VISIBLE-EXISTING-HIGH" },
            sorted.Items.Select(x => x.HGUID).ToArray()
        );
    }

    [Fact]
    public async Task QueryContainerDetailsAsync_只读贴牌价新商品取国内已有商品取仓库()
    {
        await SeedContainerAsync("C-READONLY-OEM", "CMAU0000001");
        await SeedDetailAsync(
            "D-READONLY-NEW",
            "C-READONLY-OEM",
            "P-READONLY-NEW",
            "HB-RO-NEW",
            oemPrice: 3m,
            domesticOemPrice: 6.66m,
            warehouseOemPrice: 9.99m,
            localExists: false
        );
        await SeedDetailAsync(
            "D-READONLY-EXISTING",
            "C-READONLY-OEM",
            "P-READONLY-EXISTING",
            "HB-RO-EXISTING",
            oemPrice: 4m,
            domesticOemPrice: 7.77m,
            warehouseOemPrice: 8.88m,
            localExists: true
        );
        var service = CreateService();

        var result = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-READONLY-OEM",
                PageNumber = 1,
                PageSize = 50,
                SortBy = "itemNumber",
                SortOrder = "ascend",
            }
        );
        var legacyList = await service.GetContainerProductsAsync("C-READONLY-OEM");

        var newItem = result.Items.Single(x => x.HGUID == "D-READONLY-NEW");
        var existingItem = result.Items.Single(x => x.HGUID == "D-READONLY-EXISTING");
        Assert.Equal(6.66m, newItem.ReadonlyOemPrice);
        Assert.Equal(8.88m, existingItem.ReadonlyOemPrice);
        Assert.Equal(3m, newItem.贴牌价格);
        Assert.Equal(4m, existingItem.贴牌价格);
        Assert.Equal(6.66m, legacyList.Single(x => x.HGUID == "D-READONLY-NEW").ReadonlyOemPrice);
        Assert.Equal(8.88m, legacyList.Single(x => x.HGUID == "D-READONLY-EXISTING").ReadonlyOemPrice);
    }

    [Fact]
    public async Task QueryContainerDetailsAsync_已有商品英文名称应优先读取本地主档商品名称()
    {
        await SeedContainerAsync("C-LOCAL-ENGLISH", "CBHU8299137");
        await SeedDetailAsync(
            "D-LOCAL-ENGLISH",
            "C-LOCAL-ENGLISH",
            "P-LOCAL-ENGLISH",
            "8107779",
            localExists: true
        );
        await SeedDetailAsync(
            "D-DOMESTIC-ENGLISH",
            "C-LOCAL-ENGLISH",
            "P-DOMESTIC-ENGLISH",
            "8107780",
            localExists: false
        );
        await _localDb.Updateable<DomesticProduct>()
            .SetColumns(x => x.EnglishProductName == null)
            .Where(x => x.ProductCode == "P-LOCAL-ENGLISH")
            .ExecuteCommandAsync();
        await _localDb.Updateable<Product>()
            .SetColumns(x => x.ProductName == "Alpha Local Master Name")
            .Where(x => x.ProductCode == "P-LOCAL-ENGLISH")
            .ExecuteCommandAsync();
        await _localDb.Updateable<DomesticProduct>()
            .SetColumns(x => x.EnglishProductName == "Zulu Domestic English Name")
            .Where(x => x.ProductCode == "P-DOMESTIC-ENGLISH")
            .ExecuteCommandAsync();
        var service = CreateService();

        var sorted = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-LOCAL-ENGLISH",
                PageNumber = 1,
                PageSize = 50,
                SortBy = "englishName",
                SortOrder = "ascend",
            }
        );
        var filtered = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-LOCAL-ENGLISH",
                PageNumber = 1,
                PageSize = 50,
                EnglishName = "Alpha Local",
            }
        );
        var legacyList = await service.GetContainerProductsAsync("C-LOCAL-ENGLISH");

        Assert.Equal(new[] { "D-LOCAL-ENGLISH", "D-DOMESTIC-ENGLISH" }, sorted.Items.Select(x => x.HGUID).ToArray());
        Assert.Equal("Alpha Local Master Name", sorted.Items[0].商品信息?.英文名称);
        Assert.Equal("Zulu Domestic English Name", sorted.Items[1].商品信息?.英文名称);
        Assert.Equal("D-LOCAL-ENGLISH", Assert.Single(filtered.Items).HGUID);
        Assert.Equal(
            "Alpha Local Master Name",
            legacyList.Single(item => item.HGUID == "D-LOCAL-ENGLISH").商品信息?.英文名称
        );
    }

    [Fact]
    public async Task BackfillLastPricesByScopeAsync_只填空快照且不覆盖已有值()
    {
        await SeedContainerAsync("C-BACKFILL", "CSLU6099502");
        await SeedContainerAsync("C-BACKFILL-OTHER", "CSLU6099504");
        await SeedDetailAsync("D-BACKFILL-EMPTY", "C-BACKFILL", "P-BACKFILL-EMPTY", "HB-BE", oemPrice: 4m, importPrice: 3m);
        await SeedDetailAsync(
            "D-BACKFILL-EXISTING",
            "C-BACKFILL",
            "P-BACKFILL-EXISTING",
            "HB-BX",
            oemPrice: 8m,
            importPrice: 7m,
            lastImportPrice: 1.11m,
            lastOemPrice: 2.22m
        );
        await SeedDetailAsync("D-BACKFILL-OTHER", "C-BACKFILL-OTHER", "P-BACKFILL-OTHER", "HB-BO", oemPrice: 6m, importPrice: 5m);
        var service = CreateService();

        var updated = await service.BackfillLastPricesByScopeAsync(
            "C-BACKFILL",
            new ContainerDetailBatchScopeDto
            {
                SelectedHguids = new List<string> { "D-BACKFILL-EMPTY", "D-BACKFILL-EXISTING", "D-BACKFILL-OTHER" },
            }
        );

        var empty = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-BACKFILL-EMPTY");
        var existing = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-BACKFILL-EXISTING");
        var otherContainerDetail = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-BACKFILL-OTHER");
        Assert.Equal(1, updated);
        Assert.Equal(3m, empty.LastImportPrice);
        Assert.Equal(4m, empty.LastOEMPrice);
        Assert.Equal(1.11m, existing.LastImportPrice);
        Assert.Equal(2.22m, existing.LastOEMPrice);
        Assert.Null(otherContainerDetail.LastImportPrice);
        Assert.Null(otherContainerDetail.LastOEMPrice);
    }

    [Fact]
    public async Task QueryContainerDetailsAsync_单件运输成本筛选应按展示值两位小数比较()
    {
        await SeedContainerAsync("C-UNIT-TRANSPORT", "CSLU6099505");
        await SeedDetailAsync("D-UNIT-ROUND-UP", "C-UNIT-TRANSPORT", "P-UNIT-ROUND-UP", "HB-UR", oemPrice: 4m, importPrice: 3m);
        await SeedDetailAsync("D-UNIT-ROUND-DOWN", "C-UNIT-TRANSPORT", "P-UNIT-ROUND-DOWN", "HB-UD", oemPrice: 4m, importPrice: 3m);
        await _localDb.Updateable<ContainerDetail>()
            .SetColumns(x => x.TransportCost == 0.08m)
            .SetColumns(x => x.PackingQuantity == 6.20m)
            .Where(x => x.DetailCode == "D-UNIT-ROUND-UP")
            .ExecuteCommandAsync();
        await _localDb.Updateable<ContainerDetail>()
            .SetColumns(x => x.TransportCost == 0.08m)
            .SetColumns(x => x.PackingQuantity == 6.18m)
            .Where(x => x.DetailCode == "D-UNIT-ROUND-DOWN")
            .ExecuteCommandAsync();
        var service = CreateService();

        var result = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-UNIT-TRANSPORT",
                UnitTransportCostMin = 0.50m,
                UnitTransportCostMax = 0.50m,
                PageNumber = 1,
                PageSize = 50,
            }
        );

        var item = Assert.Single(result.Items);
        Assert.Equal("D-UNIT-ROUND-UP", item.HGUID);
    }

    [Fact]
    public async Task AssignProductsAsync_新建明细应写入上次价格快照且更新已有明细不覆盖()
    {
        await SeedContainerAsync("C-ASSIGN-LAST", "CSLU6099503");
        await _localDb.Insertable(new WarehouseProduct { ProductCode = "P-ASSIGN-NEW", ImportPrice = 6.6m, OEMPrice = 9.9m, IsActive = true }).ExecuteCommandAsync();
        await _localDb.Insertable(new DomesticProduct { ProductCode = "P-ASSIGN-NEW", HBProductNo = "HB-AN", IsDeleted = false }).ExecuteCommandAsync();
        await SeedDetailAsync(
            "D-ASSIGN-EXISTING",
            "C-ASSIGN-LAST",
            "P-ASSIGN-EXISTING",
            "HB-AE",
            oemPrice: 4m,
            importPrice: 3m,
            lastImportPrice: 1.23m,
            lastOemPrice: 4.56m
        );
        var service = CreateService();

        var result = await service.AssignProductsAsync(
            "C-ASSIGN-LAST",
            new List<AssignProductItemDto>
            {
                new() { ProductCode = "P-ASSIGN-NEW", Quantity = 1m, PackingQuantity = 12m, UnitVolume = 0.1m },
                new() { ProductCode = "P-ASSIGN-EXISTING", Quantity = 1m, PackingQuantity = 24m, UnitVolume = 0.2m },
            },
            "increase",
            null
        );

        var created = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.ContainerCode == "C-ASSIGN-LAST" && x.ProductCode == "P-ASSIGN-NEW");
        var existing = await _localDb.Queryable<ContainerDetail>()
            .SingleAsync(x => x.DetailCode == "D-ASSIGN-EXISTING");
        Assert.Equal(1, result.Created);
        Assert.Equal(1, result.Updated);
        Assert.Equal(6.6m, created.LastImportPrice);
        Assert.Equal(9.9m, created.LastOEMPrice);
        Assert.Equal(1.23m, existing.LastImportPrice);
        Assert.Equal(4.56m, existing.LastOEMPrice);
    }

    [Fact]
    public async Task QueryContainerDetailsAsync_应返回仓库分类到明细和商品信息()
    {
        await SeedContainerAsync("C-CATEGORY", "CSLU6099490");
        await SeedWarehouseCategoryAsync("CAT-LAUNDRY", "Laundry");
        await SeedDetailAsync(
            "D-CATEGORY",
            "C-CATEGORY",
            "P-CATEGORY",
            "HB-CATEGORY",
            warehouseCategoryGuid: " cat-laundry "
        );
        var service = CreateService();

        var result = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-CATEGORY",
                PageNumber = 1,
                PageSize = 50,
            }
        );

        var detail = Assert.Single(result.Items);
        Assert.Equal("CAT-LAUNDRY", detail.ProductCategoryGUID);
        Assert.Equal("Laundry", detail.ProductCategoryName);
        Assert.NotNull(detail.商品信息);
        Assert.Equal("CAT-LAUNDRY", detail.商品信息!.ProductCategoryGUID);
        Assert.Equal("Laundry", detail.商品信息.ProductCategoryName);
    }

    [Fact]
    public async Task QueryContainerDetailsAsync_目标分类应优先于本地商品分类展示()
    {
        await SeedContainerAsync("C-TARGET-CATEGORY", "CSLU6099491");
        await SeedWarehouseCategoryAsync("CAT-OLD", "Old Category");
        await SeedWarehouseCategoryAsync("CAT-TARGET", "Target Category");
        await SeedDetailAsync(
            "D-TARGET-CATEGORY",
            "C-TARGET-CATEGORY",
            "P-TARGET-CATEGORY",
            "HB-TARGET-CATEGORY",
            warehouseCategoryGuid: "CAT-OLD",
            targetWarehouseCategoryGuid: " cat-target "
        );
        var service = CreateService();

        var result = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-TARGET-CATEGORY",
                PageNumber = 1,
                PageSize = 50,
            }
        );

        var detail = Assert.Single(result.Items);
        Assert.Equal("CAT-TARGET", detail.ProductCategoryGUID);
        Assert.Equal("Target Category", detail.ProductCategoryName);
        Assert.NotNull(detail.商品信息);
        Assert.Equal("CAT-TARGET", detail.商品信息!.ProductCategoryGUID);
        Assert.Equal("Target Category", detail.商品信息.ProductCategoryName);
    }

    [Fact]
    public async Task QueryContainerDetailsAsync_应按国内商品页规则补齐商品图片()
    {
        await SeedContainerAsync("C-IMAGE", "OOLU9955404");
        await SeedDetailAsync(
            "D-IMAGE-GENERATED",
            "C-IMAGE",
            "P-IMAGE-GENERATED",
            "HB249-HM-001",
            productImage: null
        );
        await SeedDetailAsync(
            "D-IMAGE-EXISTING",
            "C-IMAGE",
            "P-IMAGE-EXISTING",
            "HB249-HM-002",
            productImage: "https://cdn.example.test/HB249-HM-002.jpg"
        );
        var service = CreateService();

        var result = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-IMAGE",
                PageNumber = 1,
                PageSize = 50,
                SortBy = "itemNumber",
                SortOrder = "ascend",
            }
        );

        Assert.Equal(
            "https://hotbargain-yw-2023-1300114625.cos.ap-shanghai.myqcloud.com/YW200/HB249-HM-001.jpg",
            result.Items.Single(item => item.HGUID == "D-IMAGE-GENERATED").商品信息?.商品图片
        );
        Assert.Equal(
            "https://cdn.example.test/HB249-HM-002.jpg",
            result.Items.Single(item => item.HGUID == "D-IMAGE-EXISTING").商品信息?.商品图片
        );
    }

    [Fact]
    public async Task GetContainerProductsAsync_应按国内商品页规则补齐商品图片()
    {
        await SeedContainerAsync("C-LIST-IMAGE", "OOLU9955404");
        await SeedDetailAsync(
            "D-LIST-IMAGE",
            "C-LIST-IMAGE",
            "P-LIST-IMAGE",
            "HB249-HM-001",
            productImage: null
        );
        var service = CreateService();

        var result = await service.GetContainerProductsAsync("C-LIST-IMAGE");

        var detail = Assert.Single(result);
        Assert.Equal(
            "https://hotbargain-yw-2023-1300114625.cos.ap-shanghai.myqcloud.com/YW200/HB249-HM-001.jpg",
            detail.商品信息?.商品图片
        );
    }

    [Fact]
    public async Task QueryContainerDetailsAsync_中包数应仓库优先并用国内中包数兜底筛选排序()
    {
        await SeedContainerAsync("C-MIDDLE-PACK", "CSLU6099488");
        await SeedDetailAsync("D-MIDDLE-WAREHOUSE", "C-MIDDLE-PACK", "P-MIDDLE-WAREHOUSE", "HB300", minOrderQuantity: 8, middlePackQuantity: 4);
        await SeedDetailAsync("D-MIDDLE-DOMESTIC", "C-MIDDLE-PACK", "P-MIDDLE-DOMESTIC", "HB100", minOrderQuantity: null, middlePackQuantity: 12);
        await SeedDetailAsync("D-MIDDLE-LOW", "C-MIDDLE-PACK", "P-MIDDLE-LOW", "HB200", minOrderQuantity: null, middlePackQuantity: 2);
        var service = CreateService();

        var result = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-MIDDLE-PACK",
                PageNumber = 1,
                PageSize = 50,
                MiddlePackQuantityMin = 5,
                SortBy = "middlePackQuantity",
                SortOrder = "descend",
            }
        );

        Assert.Equal(new[] { "D-MIDDLE-DOMESTIC", "D-MIDDLE-WAREHOUSE" }, result.Items.Select(x => x.HGUID).ToArray());
        Assert.Equal(12m, result.Items[0].中包数);
        Assert.Equal(8m, result.Items[1].中包数);
    }

    [Fact]
    public async Task QueryContainerDetailsAsync_商品类型应以国内商品表为准展示筛选排序()
    {
        await SeedContainerAsync("C-PRODUCT-TYPE", "CSLU6099489");
        await SeedDetailAsync("D-TYPE-SET", "C-PRODUCT-TYPE", "P-TYPE-SET", "HB137-480", domesticProductType: 1);
        await SeedDetailAsync("D-TYPE-NORMAL", "C-PRODUCT-TYPE", "P-TYPE-NORMAL", "HB137-470", domesticProductType: 0);
        await SeedDetailAsync("D-TYPE-MULTI", "C-PRODUCT-TYPE", "P-TYPE-MULTI", "HB137-481", domesticProductType: 2);
        await SeedDetailAsync(
            "D-TYPE-SET-CHILD",
            "C-PRODUCT-TYPE",
            "P-TYPE-SET-CHILD",
            "HB137-482",
            detailProductType: "套装子商品"
        );
        var service = CreateService();

        var displayResult = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-PRODUCT-TYPE",
                PageNumber = 1,
                PageSize = 50,
                ItemNumber = "HB137-480",
            }
        );

        var displayItem = Assert.Single(displayResult.Items);
        Assert.Equal("普通商品", displayItem.商品类型);
        Assert.Equal("套装商品", displayItem.商品信息?.商品类型);

        var setResult = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-PRODUCT-TYPE",
                PageNumber = 1,
                PageSize = 50,
                ProductTypes = new List<string> { "set" },
            }
        );

        Assert.Equal(new[] { "HB137-480" }, setResult.Items.Select(x => x.商品信息?.货号).ToArray());

        var multiResult = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-PRODUCT-TYPE",
                PageNumber = 1,
                PageSize = 50,
                ProductTypes = new List<string> { "multi" },
            }
        );

        Assert.Equal(new[] { "HB137-481" }, multiResult.Items.Select(x => x.商品信息?.货号).ToArray());

        var setChildResult = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-PRODUCT-TYPE",
                PageNumber = 1,
                PageSize = 50,
                ProductTypes = new List<string> { "setChild" },
            }
        );

        Assert.Equal(new[] { "HB137-482" }, setChildResult.Items.Select(x => x.商品信息?.货号).ToArray());

        var normalForSetChildResult = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-PRODUCT-TYPE",
                PageNumber = 1,
                PageSize = 50,
                ItemNumber = "HB137-482",
                ProductTypes = new List<string> { "normal" },
            }
        );

        Assert.Empty(normalForSetChildResult.Items);

        var normalForSetItemResult = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-PRODUCT-TYPE",
                PageNumber = 1,
                PageSize = 50,
                ItemNumber = "HB137-480",
                ProductTypes = new List<string> { "normal" },
            }
        );

        Assert.Empty(normalForSetItemResult.Items);

        var sortedResult = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-PRODUCT-TYPE",
                PageNumber = 1,
                PageSize = 50,
                SortBy = "productType",
                SortOrder = "ascend",
            }
        );

        Assert.Equal(
            new[] { "D-TYPE-NORMAL", "D-TYPE-SET", "D-TYPE-MULTI", "D-TYPE-SET-CHILD" },
            sortedResult.Items.Select(x => x.HGUID).ToArray()
        );
    }

    [Fact]
    public async Task GetDomesticSetCodesAsync_应从国内套装表返回条码价格和进货价()
    {
        await SeedDetailAsync("D-SET-CODES", "C-SET-CODES", "P-SET-CODES", "HB500", domesticProductType: 1);
        await SeedDomesticSetProductAsync("SET-2", "P-SET-CODES", "HB500", "HB500-02", "952700000002", domesticPrice: 12m, oemPrice: null, importPrice: 5m);
        await SeedDomesticSetProductAsync("SET-1", "P-SET-CODES", "HB500", "HB500-01", "952700000001", domesticPrice: 10m, oemPrice: 8.8m, importPrice: 4.5m);
        var service = CreateService();

        var result = await service.GetDomesticSetCodesAsync("P-SET-CODES");

        Assert.Equal(new[] { "HB500-01", "HB500-02" }, result.Select(x => x.SetItemNumber).ToArray());
        Assert.Equal("P-SET-CODES", result[0].ProductCode);
        Assert.Equal("HB500", result[0].ItemNumber);
        Assert.Equal(1, result[0].ProductType);
        Assert.Equal("952700000001", result[0].Barcode);
        Assert.Equal(8.8m, result[0].RetailPrice);
        Assert.Equal(4.5m, result[0].PurchasePrice);
        Assert.Equal(12m, result[1].RetailPrice);
    }

    [Fact]
    public async Task GetDomesticSetCodesAsync_商品不存在或无套装明细时返回空列表()
    {
        await SeedDetailAsync("D-NO-SET-CODES", "C-NO-SET-CODES", "P-NO-SET-CODES", "HB501", domesticProductType: 1);
        var service = CreateService();

        Assert.Empty(await service.GetDomesticSetCodesAsync("P-NO-SET-CODES"));
        Assert.Empty(await service.GetDomesticSetCodesAsync("P-MISSING"));
    }

    [Fact]
    public async Task UpdateDomesticSetCodePricesAsync_只回写国内套装价格字段且限制商品归属()
    {
        await SeedDetailAsync("D-SET-PRICE", "C-SET-PRICE", "P-SET-PRICE", "HB502", domesticProductType: 1);
        await SeedDetailAsync("D-OTHER-SET-PRICE", "C-SET-PRICE", "P-OTHER-SET-PRICE", "HB503", domesticProductType: 1);
        await SeedDomesticSetProductAsync("SET-PRICE-1", "P-SET-PRICE", "HB502", "HB502-01", "952700000101", domesticPrice: 13m, oemPrice: 9.9m, importPrice: 5.5m);
        await SeedDomesticSetProductAsync("SET-PRICE-OTHER", "P-OTHER-SET-PRICE", "HB503", "HB503-01", "952700000201", domesticPrice: 23m, oemPrice: 19.9m, importPrice: 15.5m);
        var service = CreateService();

        var updated = await service.UpdateDomesticSetCodePricesAsync(
            "P-SET-PRICE",
            new UpdateContainerDomesticSetCodePricesRequestDto
            {
                Items = new List<UpdateContainerDomesticSetCodePriceItemDto>
                {
                    new() { SetProductCode = "SET-PRICE-1", RetailPrice = 11.1m, PurchasePrice = 6.6m },
                    new() { SetProductCode = "SET-PRICE-OTHER", RetailPrice = 99m, PurchasePrice = 88m },
                },
            },
            "tester"
        );

        Assert.Equal(1, updated);
        var changed = await _localDb.Queryable<DomesticSetProduct>().FirstAsync(x => x.SetProductCode == "SET-PRICE-1");
        Assert.Equal(11.1m, changed.OEMPrice);
        Assert.Equal(6.6m, changed.ImportPrice);
        Assert.Equal(13m, changed.DomesticPrice);
        Assert.Equal("HB502-01", changed.SetProductNo);
        Assert.Equal("952700000101", changed.SetBarcode);
        Assert.Equal("tester", changed.UpdatedBy);

        var unchanged = await _localDb.Queryable<DomesticSetProduct>().FirstAsync(x => x.SetProductCode == "SET-PRICE-OTHER");
        Assert.Equal(19.9m, unchanged.OEMPrice);
        Assert.Equal(15.5m, unchanged.ImportPrice);
    }

    [Fact]
    public async Task QueryContainerDetailsAsync_标签筛选应同组取并集跨组取交集()
    {
        await SeedContainerAsync("C-TAGS", "CSLU6099487");
        await SeedDetailAsync("D-TAG-1", "C-TAGS", "P-TAG-1", "HB101", isActive: true, localExists: false);
        await SeedDetailAsync("D-TAG-2", "C-TAGS", "P-TAG-2", "HB102", isActive: false, localExists: false);
        await SeedDetailAsync("D-TAG-3", "C-TAGS", "P-TAG-3", "HB103", isActive: false, localExists: true);
        var service = CreateService();

        var result = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-TAGS",
                PageNumber = 1,
                PageSize = 50,
                SelectedTags = new List<string> { "new", "inactive" },
            }
        );

        Assert.Equal(new[] { "HB102" }, result.Items.Select(x => x.商品信息?.货号).ToArray());
        Assert.Equal(1, result.ItemsTotal);
    }

    [Fact]
    public async Task QueryContainerDetailsAsync_默认页大小100且请求可上限1000并只返回统计()
    {
        await SeedContainerAsync("C-PAGE-CONTRACT", "OOLU6966452");
        await SeedDetailAsync(
            "D-PAGE-CONTRACT",
            "C-PAGE-CONTRACT",
            "P-PAGE-CONTRACT",
            "HB-PAGE-CONTRACT"
        );
        var service = CreateService();

        Assert.Equal(100, new ContainerDetailQueryDto().PageSize);

        var result = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-PAGE-CONTRACT",
                PageSize = 5000,
                IncludeItems = false,
                IncludeStats = true,
            }
        );

        Assert.Equal(1000, result.PageSize);
        Assert.Empty(result.Items);
        Assert.Equal(1, result.ItemsTotal);
        Assert.Equal(1, result.TagStats.All);
    }

    [Fact]
    public async Task QueryContainerDetailsAsync_匹配方式应全局筛选排序分页并返回冲突字段()
    {
        await SeedContainerAsync("C-MATCH", "OOLU-MATCH");
        await SeedMatchScopeAsync("C-MATCH");
        var service = CreateService();

        var firstPage = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-MATCH",
                PageNumber = 1,
                PageSize = 1,
                SortBy = "matchType",
                SortOrder = "ascend",
            }
        );
        var secondPage = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-MATCH",
                PageNumber = 2,
                PageSize = 1,
                SortBy = "matchType",
                SortOrder = "ascend",
            }
        );
        var thirdPage = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-MATCH",
                PageNumber = 3,
                PageSize = 1,
                SortBy = "matchType",
                SortOrder = "ascend",
            }
        );
        var supplierOnly = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-MATCH",
                PageSize = 1,
                MatchTypes = new List<string> { "supplierItem" },
            }
        );

        Assert.Equal("D-MATCH-DIRECT", Assert.Single(firstPage.Items).HGUID);
        Assert.Equal("productCode", firstPage.Items[0].MatchType);
        Assert.Equal("D-MATCH-SUPPLIER", Assert.Single(secondPage.Items).HGUID);
        Assert.Equal("supplierItem", secondPage.Items[0].MatchType);
        Assert.True(secondPage.Items[0].HasProductCodeConflict);
        Assert.Equal("LOCAL-CANDIDATE", secondPage.Items[0].LocalProductCode);
        Assert.Equal("DOM-CANDIDATE", secondPage.Items[0].DomesticProductCode);
        Assert.Equal("D-MATCH-NONE", Assert.Single(thirdPage.Items).HGUID);
        Assert.Equal("unmatched", thirdPage.Items[0].MatchType);

        Assert.Equal(1, supplierOnly.ItemsTotal);
        Assert.Equal("D-MATCH-SUPPLIER", Assert.Single(supplierOnly.Items).HGUID);
        Assert.Equal(0, supplierOnly.TagStats.ProductCodeMatched);
        Assert.Equal(1, supplierOnly.TagStats.SupplierItemMatched);
        Assert.Equal(0, supplierOnly.TagStats.Unmatched);
    }

    [Fact]
    public async Task QueryContainerDetailsAsync_标签统计应排除SelectedTags并保留完整分类()
    {
        await SeedContainerAsync("C-TYPE-FACET", "OOLU-TYPE-FACET");
        await SeedDetailAsync("D-TYPE-NORMAL", "C-TYPE-FACET", "P-TYPE-NORMAL", "HB-NORMAL");
        await SeedDetailAsync("D-TYPE-SET", "C-TYPE-FACET", "P-TYPE-SET", "HB-SET", domesticProductType: 1);
        await SeedDetailAsync("D-TYPE-MULTI", "C-TYPE-FACET", "P-TYPE-MULTI", "HB-MULTI", domesticProductType: 2);
        await SeedDetailAsync(
            "D-TYPE-CHILD",
            "C-TYPE-FACET",
            "P-TYPE-CHILD",
            "HB-CHILD",
            detailProductType: "套装子商品"
        );
        var service = CreateService();

        var result = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-TYPE-FACET",
                SelectedTags = new List<string> { "set" },
                IncludeItems = false,
                IncludeStats = true,
            }
        );

        Assert.Empty(result.Items);
        Assert.Equal(1, result.ItemsTotal);
        Assert.Equal(4, result.TagStats.All);
        Assert.Equal(1, result.TagStats.Normal);
        Assert.Equal(1, result.TagStats.Set);
        Assert.Equal(1, result.TagStats.Multi);
        Assert.Equal(1, result.TagStats.SetChild);
    }

    [Fact]
    public async Task QueryContainerDetailsAsync_应将取消信号传递到查询()
    {
        await SeedContainerAsync("C-CANCEL", "OOLU-CANCEL");
        await SeedDetailAsync("D-CANCEL", "C-CANCEL", "P-CANCEL", "HB-CANCEL");
        var service = CreateService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.QueryContainerDetailsAsync(
                new ContainerDetailQueryDto { ContainerGuid = "C-CANCEL" },
                cancellation.Token
            )
        );
    }

    [Fact]
    public async Task QueryContainerDetailsAsync_普通分页应只为当前页解析匹配候选()
    {
        await SeedContainerAsync("C-MATCH-FAST", "OOLU-MATCH-FAST");
        await SeedMatchScopeAsync("C-MATCH-FAST");
        var service = CreateService();
        var statements = new List<string>();
        var parameterValues = new List<string>();
        _localDb.Aop.OnLogExecuting = (statement, parameters) =>
        {
            statements.Add(statement);
            parameterValues.AddRange(
                parameters
                    .Select(parameter => parameter.Value?.ToString())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
            );
        };

        try
        {
            var result = await service.QueryContainerDetailsAsync(
                new ContainerDetailQueryDto
                {
                    ContainerGuid = "C-MATCH-FAST",
                    PageNumber = 1,
                    PageSize = 1,
                    SortBy = "itemNumber",
                    SortOrder = "ascend",
                    IncludeStats = false,
                }
            );

            var item = Assert.Single(result.Items);
            Assert.Equal("D-MATCH-SUPPLIER", item.HGUID);
            Assert.Equal("supplierItem", item.MatchType);
            var queryTrace = string.Join('\n', statements.Concat(parameterValues));
            Assert.Contains("ITEM-CANDIDATE", queryTrace, StringComparison.Ordinal);
            Assert.DoesNotContain("ITEM-DIRECT", queryTrace, StringComparison.Ordinal);
            Assert.DoesNotContain("ITEM-NONE", queryTrace, StringComparison.Ordinal);
        }
        finally
        {
            _localDb.Aop.OnLogExecuting = null;
        }
    }

    [Fact]
    public async Task QueryContainerDetailsAsync_仅统计公共标签时不应解析全量匹配候选()
    {
        await SeedContainerAsync("C-STATS-FAST", "OOLU-STATS-FAST");
        await SeedMatchScopeAsync("C-STATS-FAST");
        var service = CreateService();
        var statements = new List<string>();
        var parameterValues = new List<string>();
        _localDb.Aop.OnLogExecuting = (statement, parameters) =>
        {
            statements.Add(statement);
            parameterValues.AddRange(
                parameters
                    .Select(parameter => parameter.Value?.ToString())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
            );
        };

        try
        {
            var result = await service.QueryContainerDetailsAsync(
                new ContainerDetailQueryDto
                {
                    ContainerGuid = "C-STATS-FAST",
                    PageNumber = 1,
                    PageSize = 1,
                    SortBy = "itemNumber",
                    SortOrder = "ascend",
                    IncludeStats = true,
                }
            );

            Assert.Equal(3, result.ItemsTotal);
            Assert.Equal(3, result.TagStats.All);
            Assert.Equal(2, result.TagStats.New);
            Assert.Equal(1, result.TagStats.Existing);
            var item = Assert.Single(result.Items);
            Assert.Equal("D-MATCH-SUPPLIER", item.HGUID);
            Assert.Equal("supplierItem", item.MatchType);
            var queryTrace = string.Join('\n', statements.Concat(parameterValues));
            Assert.Contains("ITEM-CANDIDATE", queryTrace, StringComparison.Ordinal);
            Assert.DoesNotContain("ITEM-DIRECT", queryTrace, StringComparison.Ordinal);
            Assert.DoesNotContain("ITEM-NONE", queryTrace, StringComparison.Ordinal);
        }
        finally
        {
            _localDb.Aop.OnLogExecuting = null;
        }
    }

    [Fact]
    public async Task QueryContainerDetailsAsync_匹配候选应限定供应商并要求仓库商品()
    {
        await SeedContainerAsync("C-MATCH-EDGE", "OOLU-MATCH-EDGE");
        await _localDb.Insertable(
            new List<ContainerDetail>
            {
                new()
                {
                    DetailCode = "D-MATCH-REAL-SUPPLIER",
                    ContainerCode = "C-MATCH-EDGE",
                    ProductCode = "DOM-REAL-SUPPLIER",
                    IsDeleted = false,
                },
                new()
                {
                    DetailCode = "D-MATCH-DELETED-WP",
                    ContainerCode = "C-MATCH-EDGE",
                    ProductCode = "P-DELETED-WP",
                    IsDeleted = false,
                },
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new List<DomesticProduct>
            {
                new()
                {
                    ProductCode = "DOM-REAL-SUPPLIER",
                    SupplierCode = "SUP-REAL",
                    HBProductNo = "ITEM-SHARED",
                    IsDeleted = false,
                },
                new()
                {
                    ProductCode = "P-DELETED-WP",
                    SupplierCode = "200",
                    HBProductNo = "ITEM-DIRECT",
                    IsDeleted = false,
                },
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new List<Product>
            {
                new()
                {
                    UUID = "LOCAL-DOM-REAL",
                    ProductCode = "DOM-REAL-SUPPLIER",
                    ItemNumber = "ITEM-SHARED",
                    LocalSupplierCode = "SUP-REAL",
                },
                new()
                {
                    UUID = "LOCAL-RIGHT",
                    ProductCode = "LOCAL-RIGHT",
                    ItemNumber = "ITEM-SHARED",
                    LocalSupplierCode = "SUP-REAL",
                    IsDeleted = true,
                },
                new()
                {
                    UUID = "LOCAL-WRONG-SUPPLIER",
                    ProductCode = "LOCAL-WRONG-SUPPLIER",
                    ItemNumber = "ITEM-SHARED",
                    LocalSupplierCode = "SUP-OTHER",
                },
                new()
                {
                    UUID = "LOCAL-WITHOUT-WAREHOUSE",
                    ProductCode = "LOCAL-WITHOUT-WAREHOUSE",
                    ItemNumber = "ITEM-SHARED",
                    LocalSupplierCode = "SUP-REAL",
                },
                new()
                {
                    UUID = "LOCAL-DELETED-WP-DIRECT",
                    ProductCode = "P-DELETED-WP",
                },
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new List<WarehouseProduct>
            {
                new() { ProductCode = "LOCAL-RIGHT", IsDeleted = true },
                new() { ProductCode = "LOCAL-WRONG-SUPPLIER", IsDeleted = false },
                new() { ProductCode = "P-DELETED-WP", IsDeleted = true },
            }
        ).ExecuteCommandAsync();
        var service = CreateService();

        var result = await service.QueryContainerDetailsAsync(
            new ContainerDetailQueryDto
            {
                ContainerGuid = "C-MATCH-EDGE",
                SortBy = "matchType",
                SortOrder = "ascend",
            }
        );

        var supplier = result.Items.Single(item => item.HGUID == "D-MATCH-REAL-SUPPLIER");
        Assert.Equal("supplierItem", supplier.MatchType);
        Assert.Equal("LOCAL-RIGHT", supplier.LocalProductCode);
        Assert.True(supplier.HasProductCodeConflict);
        var direct = result.Items.Single(item => item.HGUID == "D-MATCH-DELETED-WP");
        Assert.Equal("productCode", direct.MatchType);
        Assert.False(direct.HasProductCodeConflict);
    }

    [Fact]
    public async Task QueryContainerDetailsAsync_重复候选应稳定选择最小商品编码()
    {
        await SeedContainerAsync("C-MATCH-DETERMINISTIC", "OOLU-MATCH-DETERMINISTIC");
        await _localDb.Insertable(
            new ContainerDetail
            {
                DetailCode = "D-MATCH-DETERMINISTIC",
                ContainerCode = "C-MATCH-DETERMINISTIC",
                ProductCode = "DOM-SEED",
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new List<DomesticProduct>
            {
                new()
                {
                    ProductCode = "DOM-SEED",
                    SupplierCode = "200",
                    HBProductNo = "ITEM-DUPLICATE",
                    IsDeleted = true,
                },
                new()
                {
                    ProductCode = "Z-DOMESTIC-CANDIDATE",
                    SupplierCode = "200",
                    HBProductNo = "ITEM-DUPLICATE",
                    IsDeleted = false,
                },
                new()
                {
                    ProductCode = "A-DOMESTIC-CANDIDATE",
                    SupplierCode = "200",
                    HBProductNo = "ITEM-DUPLICATE",
                    IsDeleted = false,
                },
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new List<Product>
            {
                new()
                {
                    UUID = "LOCAL-Z",
                    ProductCode = "Z-LOCAL-CANDIDATE",
                    ItemNumber = "ITEM-DUPLICATE",
                    LocalSupplierCode = "200",
                },
                new()
                {
                    UUID = "LOCAL-A",
                    ProductCode = "A-LOCAL-CANDIDATE",
                    ItemNumber = "ITEM-DUPLICATE",
                    LocalSupplierCode = "200",
                },
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new List<WarehouseProduct>
            {
                new() { ProductCode = "Z-LOCAL-CANDIDATE" },
                new() { ProductCode = "A-LOCAL-CANDIDATE" },
            }
        ).ExecuteCommandAsync();
        var service = CreateService();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await service.QueryContainerDetailsAsync(
                new ContainerDetailQueryDto
                {
                    ContainerGuid = "C-MATCH-DETERMINISTIC",
                    SortBy = "matchType",
                    SortOrder = "ascend",
                }
            );

            var item = Assert.Single(result.Items);
            Assert.Equal("A-LOCAL-CANDIDATE", item.LocalProductCode);
            Assert.Equal("A-DOMESTIC-CANDIDATE", item.DomesticProductCode);
            Assert.Equal("supplierItem", item.MatchType);
            Assert.True(item.HasProductCodeConflict);
        }
    }

    [Fact]
    public async Task ApplyPricesByScopeAsync_匹配方式筛选应与列表作用域一致()
    {
        await SeedContainerAsync("C-MATCH-BATCH", "OOLU-MATCH-BATCH");
        await SeedMatchScopeAsync("C-MATCH-BATCH");
        var service = CreateService();

        var updated = await service.ApplyPricesByScopeAsync(
            "C-MATCH-BATCH",
            new ContainerDetailApplyPricesRequestDto
            {
                OemPrice = 9.99m,
                Query = new ContainerDetailQueryDto
                {
                    MatchTypes = new List<string> { "supplierItem" },
                },
            }
        );

        var rows = await _localDb.Queryable<ContainerDetail>()
            .Where(row => row.ContainerCode == "C-MATCH-BATCH")
            .ToListAsync();
        Assert.Equal(1, updated);
        Assert.Equal(9.99m, rows.Single(row => row.DetailCode == "D-MATCH-SUPPLIER").OEMPrice);
        Assert.Equal(1m, rows.Single(row => row.DetailCode == "D-MATCH-DIRECT").OEMPrice);
        Assert.Equal(1m, rows.Single(row => row.DetailCode == "D-MATCH-NONE").OEMPrice);
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

    private async Task SeedContainerAsync(
        string containerCode,
        string containerNumber,
        DateTime? loadingDate = null,
        DateTime? estimatedArrivalDate = null,
        DateTime? actualArrivalDate = null,
        decimal? totalPieces = null,
        decimal? totalAmount = null,
        decimal? totalVolume = null,
        int? status = null
    )
    {
        await _localDb.Insertable(
            new Container
            {
                ContainerCode = containerCode,
                ContainerNumber = containerNumber,
                LoadingDate = loadingDate ?? new DateTime(2026, 5, 12),
                EstimatedArrivalDate = estimatedArrivalDate ?? new DateTime(2026, 6, 2),
                ActualArrivalDate = actualArrivalDate ?? new DateTime(2026, 6, 8),
                ExchangeRate = 4.5m,
                ShippingFee = 12000m,
                TotalPieces = totalPieces,
                TotalAmount = totalAmount,
                TotalVolume = totalVolume ?? 69.868m,
                Status = status ?? 2,
            }
        ).ExecuteCommandAsync();
    }

    private async Task SeedDetailAsync(
        string detailCode,
        string containerCode,
        string productCode,
        string itemNumber,
        bool isActive = true,
        decimal? oemPrice = 1m,
        decimal? importPrice = 1m,
        bool localExists = true,
        int? minOrderQuantity = null,
        int? middlePackQuantity = null,
        int domesticProductType = 0,
        string detailProductType = "普通商品",
        string? warehouseCategoryGuid = null,
        string? targetWarehouseCategoryGuid = null,
        string? productImage = "__DEFAULT__",
        decimal? lastImportPrice = null,
        decimal? lastOemPrice = null,
        decimal? domesticOemPrice = null,
        decimal? warehouseImportPrice = null,
        decimal? warehouseOemPrice = null
    )
    {
        await _localDb.Insertable(
            new ContainerDetail
            {
                DetailCode = detailCode,
                ContainerCode = containerCode,
                ProductCode = productCode,
                ProductType = detailProductType,
                LoadingPieces = 1m,
                LoadingQuantity = 10m,
                DomesticPrice = 8m,
                AdjustmentRate = 1.1m,
                ImportPrice = importPrice,
                OEMPrice = oemPrice,
                LastImportPrice = lastImportPrice,
                LastOEMPrice = lastOemPrice,
                TransportCost = 0.5m,
                Remarks = $"备注 {itemNumber}",
                TargetWarehouseCategoryGUID = targetWarehouseCategoryGuid,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();

        await _localDb.Insertable(
            new DomesticProduct
            {
                ProductCode = productCode,
                HBProductNo = itemNumber,
                Barcode = $"9300000000{itemNumber}",
                ProductName = $"商品 {itemNumber}",
                EnglishProductName = $"Product {itemNumber}",
                ProductImage = productImage == "__DEFAULT__" ? $"https://example.test/{itemNumber}.jpg" : productImage,
                MiddlePackQuantity = middlePackQuantity,
                ProductType = domesticProductType,
                OEMPrice = domesticOemPrice ?? oemPrice,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();

        await _localDb.Insertable(
            new WarehouseProduct
            {
                ProductCode = productCode,
                ImportPrice = warehouseImportPrice ?? importPrice,
                OEMPrice = warehouseOemPrice ?? oemPrice,
                MinOrderQuantity = minOrderQuantity,
                IsActive = isActive,
            }
        ).ExecuteCommandAsync();

        if (localExists)
        {
            await _localDb.Insertable(
                new Product
                {
                    UUID = $"LOCAL-{productCode}",
                    ProductCode = productCode,
                    ProductName = $"本地商品 {itemNumber}",
                    WarehouseCategoryGUID = warehouseCategoryGuid,
                    IsActive = isActive,
                }
            ).ExecuteCommandAsync();
        }
    }

    private async Task SeedProductChangeHistoryAsync(
        string productCode,
        string action,
        string sourceReference
    )
    {
        await _localDb.Insertable(
            new WarehouseProductChangeHistory
            {
                ProductCode = productCode,
                Action = action,
                Source = "ContainerSubmit",
                SourceReference = sourceReference,
                ActorName = "测试",
                ActorType = "User",
            }
        ).ExecuteCommandAsync();
    }

    private async Task SeedMatchScopeAsync(string containerCode)
    {
        await _localDb.Insertable(
            new List<ContainerDetail>
            {
                new()
                {
                    DetailCode = "D-MATCH-DIRECT",
                    ContainerCode = containerCode,
                    ProductCode = "P-DIRECT",
                    OEMPrice = 1m,
                    ImportPrice = 1m,
                    IsDeleted = false,
                },
                new()
                {
                    DetailCode = "D-MATCH-SUPPLIER",
                    ContainerCode = containerCode,
                    ProductCode = "DOM-CANDIDATE",
                    OEMPrice = 1m,
                    ImportPrice = 1m,
                    IsDeleted = false,
                },
                new()
                {
                    DetailCode = "D-MATCH-NONE",
                    ContainerCode = containerCode,
                    ProductCode = "DOM-NONE",
                    OEMPrice = 1m,
                    ImportPrice = 1m,
                    IsDeleted = false,
                },
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new List<DomesticProduct>
            {
                new()
                {
                    ProductCode = "P-DIRECT",
                    SupplierCode = "200",
                    HBProductNo = "ITEM-DIRECT",
                    IsDeleted = false,
                },
                new()
                {
                    ProductCode = "DOM-CANDIDATE",
                    SupplierCode = "200",
                    HBProductNo = "ITEM-CANDIDATE",
                    IsDeleted = false,
                },
                new()
                {
                    ProductCode = "DOM-NONE",
                    SupplierCode = "200",
                    HBProductNo = "ITEM-NONE",
                    IsDeleted = false,
                },
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new List<Product>
            {
                new()
                {
                    UUID = "LOCAL-P-DIRECT",
                    ProductCode = "P-DIRECT",
                    ItemNumber = "ITEM-DIRECT",
                    LocalSupplierCode = "200",
                    ProductName = "直接编码商品",
                    IsDeleted = false,
                },
                new()
                {
                    UUID = "LOCAL-CANDIDATE-UUID",
                    ProductCode = "LOCAL-CANDIDATE",
                    ItemNumber = "ITEM-CANDIDATE",
                    LocalSupplierCode = "200",
                    ProductName = "供应商货号候选",
                    IsDeleted = false,
                },
            }
        ).ExecuteCommandAsync();
        await _localDb.Insertable(
            new List<WarehouseProduct>
            {
                new() { ProductCode = "P-DIRECT", IsDeleted = false },
                new() { ProductCode = "LOCAL-CANDIDATE", IsDeleted = false },
            }
        ).ExecuteCommandAsync();
    }

    private async Task SeedWarehouseCategoryAsync(string categoryGuid, string categoryName)
    {
        await _localDb.Insertable(
            new WarehouseCategory
            {
                CategoryGUID = categoryGuid,
                CategoryName = categoryName,
                IsActive = true,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
    }

    private async Task SeedDomesticSetProductAsync(
        string setProductCode,
        string productCode,
        string productNo,
        string setProductNo,
        string setBarcode,
        decimal? domesticPrice,
        decimal? oemPrice,
        decimal? importPrice
    )
    {
        await _localDb.Insertable(
            new DomesticSetProduct
            {
                SetProductCode = setProductCode,
                ProductCode = productCode,
                ProductNo = productNo,
                SetProductNo = setProductNo,
                SetBarcode = setBarcode,
                DomesticPrice = domesticPrice,
                OEMPrice = oemPrice,
                ImportPrice = importPrice,
                IsDeleted = false,
            }
        ).ExecuteCommandAsync();
    }

    private static IMapper CreateContainerListMapper()
    {
        var mapper = new Mock<IMapper>();
        mapper
            .Setup(x => x.Map<List<ContainerMainDto>>(It.IsAny<List<Container>>()))
            .Returns((List<Container> containers) =>
                containers
                    .Select(container => new ContainerMainDto
                    {
                        HGUID = container.ContainerCode,
                        货柜编号 = container.ContainerNumber,
                        装柜日期 = container.LoadingDate,
                        预计到岸日期 = container.EstimatedArrivalDate,
                        实际到货日期 = container.ActualArrivalDate,
                        合计件数 = container.TotalPieces,
                        合计金额 = container.TotalAmount,
                        总体积 = container.TotalVolume,
                        状态 = container.Status,
                    })
                    .ToList()
            );
        return mapper.Object;
    }

    private static IMapper CreateContainerDetailMapper() =>
        new MapperConfiguration(
            cfg => cfg.AddProfile<ContainerMappingProfile>(),
            NullLoggerFactory.Instance
        ).CreateMapper();

    private ContainerReactService CreateService(IMapper? mapper = null)
    {
        return new ContainerReactService(
            CreateSqlSugarContext(_localDb),
            CreateHqSqlSugarContext(),
            CreateHBSalesSqlSugarContext(_hbSalesDb),
            new ConfigurationBuilder().Build(),
            mapper ?? Mock.Of<IMapper>(),
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
            .Setup(x => x.ContainsChinese(It.IsAny<string>()))
            .Returns<string>(value => value.Any(c => c >= '\u4e00' && c <= '\u9fff'));
        translationService
            .Setup(x => x.BatchTranslateToEnglishAsync(It.IsAny<List<string>>()))
            .ReturnsAsync((List<string> texts) => texts.ToDictionary(text => text, text => text));
        return translationService.Object;
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
        var dbField = typeof(HBSalesSqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic);
        dbField!.SetValue(context, db);
        return context;
    }
}
