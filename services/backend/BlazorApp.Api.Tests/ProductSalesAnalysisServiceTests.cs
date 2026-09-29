using System.Reflection;
using System.Runtime.CompilerServices;
using AutoMapper;
using BlazorApp.Api.Cache;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

[CollectionDefinition("SalesDashboardCache", DisableParallelization = true)]
public sealed class SalesDashboardCacheCollection { }

[Collection("SalesDashboardCache")]
public sealed class ProductSalesAnalysisServiceTests : IDisposable
{
    private readonly string _localDbPath;
    private readonly string _posmDbPath;
    private readonly SqliteConnection _localConnection;
    private readonly SqliteConnection _posmConnection;
    private readonly SqlSugarClient _localDb;
    private readonly SqlSugarClient _posmDb;

    public ProductSalesAnalysisServiceTests()
    {
        _localDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _posmDbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        _localConnection = new SqliteConnection($"Data Source={_localDbPath}");
        _posmConnection = new SqliteConnection($"Data Source={_posmDbPath}");
        _localConnection.Open();
        _posmConnection.Open();

        _localDb = new SqlSugarClient(CreateConnectionConfig(_localConnection.ConnectionString));
        _posmDb = new SqlSugarClient(CreateConnectionConfig(_posmConnection.ConnectionString));

        _localDb.CodeFirst.InitTables(
            typeof(SalesStatisticRefreshState),
            typeof(ProductStoreDailySalesStatistic),
            typeof(Product),
            typeof(HBLocalSupplier),
            typeof(ChinaSupplier),
            typeof(Store)
        );
        _posmDb.CodeFirst.InitTables(typeof(PosmProductSupplierMapping));
    }

    [Fact]
    public async Task GetProductSalesAnalysisSummaryAsync_非Fresh返回空数据与状态()
    {
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = new DateTime(2026, 8, 10),
            Status = SalesStatisticRefreshStatus.Pending,
            SourceTimeZone = "POSM_LOCAL",
        }).ExecuteCommandAsync();

        var service = CreateService();
        var result = await service.GetProductSalesAnalysisSummaryAsync(
            new ProductSalesAnalysisRequest
            {
                Filter = new ProductSalesAnalysisFilterDto
                {
                    StartDate = new DateTime(2026, 8, 10),
                    EndDate = new DateTime(2026, 8, 10),
                },
            },
            branchCodes: null
        );

        Assert.Equal(SalesStatisticRefreshStatus.Pending, result.StatisticStatus);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data!.Items);
        Assert.Equal(0, result.Data.Total);
        Assert.NotEqual("none", result.CacheVersion);
    }

    [Fact]
    public async Task GetProductSalesAnalysisSummaryAsync_日期范围混合状态返回Pending并清空数据()
    {
        await _localDb.Insertable(new[]
        {
            new SalesStatisticRefreshState
            {
                StatisticType = SalesStatisticType.ProductStoreDaily,
                Date = new DateTime(2026, 8, 10),
                Status = SalesStatisticRefreshStatus.Fresh,
                SourceTimeZone = "POSM_LOCAL",
            },
            new SalesStatisticRefreshState
            {
                StatisticType = SalesStatisticType.ProductStoreDaily,
                Date = new DateTime(2026, 8, 11),
                Status = SalesStatisticRefreshStatus.Pending,
                SourceTimeZone = "POSM_LOCAL",
            },
        }).ExecuteCommandAsync();

        var result = await CreateService().GetProductSalesAnalysisSummaryAsync(
            new ProductSalesAnalysisRequest
            {
                Filter = new ProductSalesAnalysisFilterDto
                {
                    StartDate = new DateTime(2026, 8, 10),
                    EndDate = new DateTime(2026, 8, 11),
                },
            },
            branchCodes: null
        );

        Assert.Equal(SalesStatisticRefreshStatus.Pending, result.StatisticStatus);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data!.Items);
        Assert.Equal(0, result.Data.Total);
    }

    [Fact]
    public async Task GetProductSalesAnalysisSummaryAsync_未知单值状态返回Pending并清空数据()
    {
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = new DateTime(2026, 8, 10),
            Status = "Unknown",
            SourceTimeZone = "POSM_LOCAL",
        }).ExecuteCommandAsync();

        var result = await CreateService().GetProductSalesAnalysisSummaryAsync(
            new ProductSalesAnalysisRequest
            {
                Filter = new ProductSalesAnalysisFilterDto
                {
                    StartDate = new DateTime(2026, 8, 10),
                    EndDate = new DateTime(2026, 8, 10),
                },
            },
            branchCodes: null
        );

        Assert.Equal(SalesStatisticRefreshStatus.Pending, result.StatisticStatus);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data!.Items);
        Assert.Equal(0, result.Data.Total);
    }

    [Fact]
    public async Task GetProductSalesAnalysisSummaryAsync_Fresh与未知混合返回Pending并清空数据()
    {
        await _localDb.Insertable(new[]
        {
            new SalesStatisticRefreshState
            {
                StatisticType = SalesStatisticType.ProductStoreDaily,
                Date = new DateTime(2026, 8, 10),
                Status = SalesStatisticRefreshStatus.Fresh,
                SourceTimeZone = "POSM_LOCAL",
            },
            new SalesStatisticRefreshState
            {
                StatisticType = SalesStatisticType.ProductStoreDaily,
                Date = new DateTime(2026, 8, 11),
                Status = "Unknown",
                SourceTimeZone = "POSM_LOCAL",
            },
        }).ExecuteCommandAsync();

        var result = await CreateService().GetProductSalesAnalysisSummaryAsync(
            new ProductSalesAnalysisRequest
            {
                Filter = new ProductSalesAnalysisFilterDto
                {
                    StartDate = new DateTime(2026, 8, 10),
                    EndDate = new DateTime(2026, 8, 11),
                },
            },
            branchCodes: null
        );

        Assert.Equal(SalesStatisticRefreshStatus.Pending, result.StatisticStatus);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data!.Items);
        Assert.Equal(0, result.Data.Total);
    }

    [Fact]
    public async Task GetProductSalesAnalysisSummaryAsync_无授权分店仍返回Fresh状态()
    {
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = new DateTime(2026, 8, 10),
            Status = SalesStatisticRefreshStatus.Fresh,
            SourceTimeZone = "POSM_LOCAL",
        }).ExecuteCommandAsync();

        var service = CreateService();
        var result = await service.GetProductSalesAnalysisSummaryAsync(
            new ProductSalesAnalysisRequest
            {
                Filter = new ProductSalesAnalysisFilterDto
                {
                    StartDate = new DateTime(2026, 8, 10),
                    EndDate = new DateTime(2026, 8, 10),
                },
            },
            branchCodes: new List<string>()
        );

        Assert.Equal(SalesStatisticRefreshStatus.Fresh, result.StatisticStatus);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data!.Items);
    }

    [Fact]
    public async Task GetProductSalesAnalysisSummaryAsync_按授权分店关键词与选择语义汇总净值()
    {
        var date = new DateTime(2026, 8, 10);
        await SeedFreshStatusAsync(date);
        await _localDb.Insertable(new HBLocalSupplier
        {
            Guid = "supplier-au1",
            LocalSupplierCode = "AU1",
            Name = "Australia One",
            Status = 1,
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new[]
        {
            CreateProduct("P1", "SKU-ALPHA", "Alpha Widget"),
            CreateProduct("P2", "SKU-RETURN", "Return Widget"),
        }).ExecuteCommandAsync();
        foreach (var statistic in new[]
        {
            CreateStatistic(date, "B1", "AU1", "P1", 5, 50m),
            CreateStatistic(date, "B2", "AU1", "P1", 9, 90m),
            CreateStatistic(date, "B1", "AU1", "P2", -2, -20m),
        })
        {
            await _localDb.Insertable(statistic).ExecuteCommandAsync();
        }

        var service = CreateService();
        var alpha = await service.GetProductSalesAnalysisSummaryAsync(
            CreateRequest(date, keyword: "Alpha"),
            branchCodes: new List<string> { "B1" }
        );

        Assert.Equal(SalesStatisticRefreshStatus.Fresh, alpha.StatisticStatus);
        var alphaRow = Assert.Single(alpha.Data!.Items);
        Assert.Equal("P1", alphaRow.ProductCode);
        Assert.Equal(5, alphaRow.Metrics.Quantity);
        Assert.Equal(50m, alphaRow.Metrics.SalesAmount);
        Assert.Equal(10m, alphaRow.Metrics.AverageUnitPrice);

        var returnsRequest = CreateRequest(date);
        returnsRequest.Selection = new ProductSalesAnalysisSelectionDto
        {
            Mode = "included",
            IncludedProductCodes = new List<string> { "P2" },
        };
        var returns = await service.GetProductSalesAnalysisSummaryAsync(
            returnsRequest,
            branchCodes: new List<string> { "B1" }
        );

        var returnRow = Assert.Single(returns.Data!.Items);
        Assert.Equal(-2, returnRow.Metrics.Quantity);
        Assert.Equal(-20m, returnRow.Metrics.SalesAmount);
        Assert.Equal(10m, returnRow.Metrics.AverageUnitPrice);
    }

    [Fact]
    public async Task GetProductSalesAnalysisSummaryAsync_allowNonFreshData_返回当前统计表数据并保留Pending状态()
    {
        var date = new DateTime(2026, 8, 10);
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = date,
            Status = SalesStatisticRefreshStatus.Pending,
            SourceTimeZone = "POSM_LOCAL",
        }).ExecuteCommandAsync();
        await _localDb.Insertable(CreateProduct("P1", "SKU-ALPHA", "Alpha Widget")).ExecuteCommandAsync();
        await _localDb.Insertable(CreateStatistic(date, "B1", "AU1", "P1", 5, 50m)).ExecuteCommandAsync();

        var result = await CreateService().GetProductSalesAnalysisSummaryAsync(
            CreateRequest(date),
            branchCodes: new List<string> { "B1" },
            allowNonFreshData: true
        );

        Assert.Equal(SalesStatisticRefreshStatus.Pending, result.StatisticStatus);
        Assert.Equal("商品统计正在生成中。", result.StatisticMessage);
        var row = Assert.Single(result.Data!.Items);
        Assert.Equal("P1", row.ProductCode);
        Assert.Equal(5, row.Metrics.Quantity);
        Assert.Equal(50m, row.Metrics.SalesAmount);
    }

    [Fact]
    public async Task GetProductSalesAnalysisProductDailyAsync_allowNonFreshData_Stale返回数据并保留状态()
    {
        var date = new DateTime(2026, 8, 10);
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = date,
            Status = SalesStatisticRefreshStatus.Stale,
            SourceTimeZone = "POSM_LOCAL",
        }).ExecuteCommandAsync();
        await _localDb.Insertable(CreateProduct("P1", "SKU-ALPHA", "Alpha Widget")).ExecuteCommandAsync();
        await _localDb.Insertable(CreateStatistic(date, "B1", "AU1", "P1", 7, 70m)).ExecuteCommandAsync();

        var request = CreateRequest(date);
        request.Scope = new ProductSalesAnalysisScopeDto { Mode = "currentProduct", ProductCode = "P1" };
        var result = await CreateService().GetProductSalesAnalysisProductDailyAsync(
            request,
            branchCodes: new List<string> { "B1" },
            allowNonFreshData: true
        );

        Assert.Equal(SalesStatisticRefreshStatus.Stale, result.StatisticStatus);
        Assert.Equal("商品统计正在等待延迟上传数据补算。", result.StatisticMessage);
        var day = Assert.Single(result.Data!);
        Assert.Equal(date, day.Date);
        Assert.Equal(7, day.Metrics.Quantity);
        Assert.Equal(70m, day.Metrics.SalesAmount);
    }

    [Fact]
    public async Task GetProductSalesAnalysisBranchesAsync_allowNonFreshData_金额不一致Failed返回数据并保留状态()
    {
        var date = new DateTime(2026, 8, 10);
        const string errorMessage =
            "商品统计与分店营业额统计不一致: 2026-08-10 B1, 商品金额 10, 分店营业额 11, 金额差 1";
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = date,
            Status = SalesStatisticRefreshStatus.Failed,
            ErrorMessage = errorMessage,
            SourceTimeZone = "POSM_LOCAL",
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new Store
        {
            StoreGUID = "store-b1",
            StoreCode = "B1",
            StoreName = "Branch One",
        }).ExecuteCommandAsync();
        await _localDb.Insertable(CreateProduct("P1", "SKU-ALPHA", "Alpha Widget")).ExecuteCommandAsync();
        await _localDb.Insertable(CreateStatistic(date, "B1", "AU1", "P1", 3, 45m)).ExecuteCommandAsync();

        var request = CreateRequest(date);
        request.Scope = new ProductSalesAnalysisScopeDto { Mode = "currentProduct", ProductCode = "P1" };
        var result = await CreateService().GetProductSalesAnalysisBranchesAsync(
            request,
            branchCodes: new List<string> { "B1" },
            allowNonFreshData: true
        );

        Assert.Equal(SalesStatisticRefreshStatus.Failed, result.StatisticStatus);
        Assert.Equal(errorMessage, result.StatisticMessage);
        var branch = Assert.Single(result.Data!);
        Assert.Equal("B1", branch.BranchCode);
        Assert.Equal("Branch One", branch.BranchName);
        Assert.Equal(3, branch.Metrics.Quantity);
        Assert.Equal(45m, branch.Metrics.SalesAmount);
    }

    [Fact]
    public async Task GetProductSalesAnalysisBranchDailyAsync_allowNonFreshData_Pending返回数据并保留状态()
    {
        var date = new DateTime(2026, 8, 10);
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = date,
            Status = SalesStatisticRefreshStatus.Pending,
            SourceTimeZone = "POSM_LOCAL",
        }).ExecuteCommandAsync();
        await _localDb.Insertable(CreateProduct("P1", "SKU-ALPHA", "Alpha Widget")).ExecuteCommandAsync();
        await _localDb.Insertable(CreateStatistic(date, "B1", "AU1", "P1", 4, 44m)).ExecuteCommandAsync();

        var request = CreateRequest(date);
        request.Scope = new ProductSalesAnalysisScopeDto { Mode = "currentProduct", ProductCode = "P1" };
        request.BranchCode = "B1";
        var result = await CreateService().GetProductSalesAnalysisBranchDailyAsync(
            request,
            branchCodes: new List<string> { "B1" },
            allowNonFreshData: true
        );

        Assert.Equal(SalesStatisticRefreshStatus.Pending, result.StatisticStatus);
        Assert.Equal("商品统计正在生成中。", result.StatisticMessage);
        var day = Assert.Single(result.Data!);
        Assert.Equal(date, day.Date);
        Assert.Equal(4, day.Metrics.Quantity);
        Assert.Equal(44m, day.Metrics.SalesAmount);
    }

    [Fact]
    public async Task GetProductSalesAnalysisSummaryAsync_allowNonFreshData_空授权分店仍返回空()
    {
        var date = new DateTime(2026, 8, 10);
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = date,
            Status = SalesStatisticRefreshStatus.Pending,
            SourceTimeZone = "POSM_LOCAL",
        }).ExecuteCommandAsync();
        await _localDb.Insertable(CreateStatistic(date, "B1", "AU1", "P1", 5, 50m)).ExecuteCommandAsync();

        var result = await CreateService().GetProductSalesAnalysisSummaryAsync(
            CreateRequest(date),
            branchCodes: new List<string>(),
            allowNonFreshData: true
        );

        Assert.Equal(SalesStatisticRefreshStatus.Pending, result.StatisticStatus);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data!.Items);
        Assert.Equal(0, result.Data.Total);
    }

    [Fact]
    public async Task GetProductSalesAnalysisSummaryAsync_allowNonFreshData_不读写ProductSalesAnalysis缓存()
    {
        SalesDashboardCacheKeys.ClearActiveKeys();
        var date = new DateTime(2026, 8, 10);
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = date,
            Status = SalesStatisticRefreshStatus.Pending,
            SourceTimeZone = "POSM_LOCAL",
        }).ExecuteCommandAsync();
        await _localDb.Insertable(CreateProduct("P1", "SKU-ALPHA", "Alpha Widget")).ExecuteCommandAsync();
        await _localDb.Insertable(CreateStatistic(date, "B1", "AU1", "P1", 5, 50m)).ExecuteCommandAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        IMemoryCache cacheApi = cache;
        var service = CreateService(cache);
        var request = CreateRequest(date);
        var branchCodes = new List<string> { "B1" };

        var result = await service.GetProductSalesAnalysisSummaryAsync(
            request,
            branchCodes,
            allowNonFreshData: true
        );

        Assert.Equal(SalesStatisticRefreshStatus.Pending, result.StatisticStatus);
        Assert.Single(result.Data!.Items);

        var key = SalesDashboardCacheKeys.ProductSalesAnalysisSummary(
            request,
            branchCodes,
            result.CacheVersion
        );
        Assert.False(cacheApi.TryGetValue(key, out _));
        Assert.DoesNotContain(key, SalesDashboardCacheKeys.ActiveKeys);

        var cachedResponse = new ProductSalesAnalysisResponse<
            ProductSalesAnalysisPagedDto<ProductSalesProductRowDto>
        >
        {
            StatisticStatus = SalesStatisticRefreshStatus.Fresh,
            Data = new ProductSalesAnalysisPagedDto<ProductSalesProductRowDto>
            {
                Items = new List<ProductSalesProductRowDto>
                {
                    new() { ProductCode = "CACHE-SENTINEL" },
                },
                Total = 1,
            },
        };
        cacheApi.Set(key, cachedResponse);

        var secondResult = await service.GetProductSalesAnalysisSummaryAsync(
            request,
            branchCodes,
            allowNonFreshData: true
        );

        Assert.Equal("P1", Assert.Single(secondResult.Data!.Items).ProductCode);
        Assert.Same(cachedResponse, cacheApi.Get<
            ProductSalesAnalysisResponse<ProductSalesAnalysisPagedDto<ProductSalesProductRowDto>>
        >(key));
        SalesDashboardCacheKeys.ClearActiveKeys();
    }

    [Fact]
    public async Task GetProductSalesAnalysisSummaryAsync_国内供应商兼容旧200与新直写且不误命中()
    {
        var date = new DateTime(2026, 8, 10);
        await SeedFreshStatusAsync(date);
        await _localDb.Insertable(new[]
        {
            new ChinaSupplier { Guid = "cn1", SupplierCode = "CN1", SupplierName = "China One" },
            new ChinaSupplier { Guid = "cn2", SupplierCode = "CN2", SupplierName = "China Two" },
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new[]
        {
            CreateProduct("P-OLD-CN1", "OLD-CN1", "Old CN1"),
            CreateProduct("P-NEW-CN1", "NEW-CN1", "New CN1"),
            CreateProduct("P-OLD-CN2", "OLD-CN2", "Old CN2"),
        }).ExecuteCommandAsync();
        await _posmDb.Insertable(new[]
        {
            new PosmProductSupplierMapping
            {
                ProductCode = "P-OLD-CN1",
                LocalSupplierCode = "200",
                ChinaSupplierCode = "CN1",
            },
            new PosmProductSupplierMapping
            {
                ProductCode = "P-OLD-CN2",
                LocalSupplierCode = "200",
                ChinaSupplierCode = "CN2",
            },
        }).ExecuteCommandAsync();
        foreach (var statistic in new[]
        {
            CreateStatistic(date, "B1", "200", "P-OLD-CN1", 3, 30m),
            CreateStatistic(date, "B1", "CN1", "P-NEW-CN1", 4, 44m),
            CreateStatistic(date, "B1", "200", "P-OLD-CN2", 8, 88m),
        })
        {
            await _localDb.Insertable(statistic).ExecuteCommandAsync();
        }

        var request = CreateRequest(date);
        request.Filter.ChinaSupplierCodes = new List<string> { "CN1" };
        var result = await CreateService().GetProductSalesAnalysisSummaryAsync(
            request,
            branchCodes: new List<string> { "B1" }
        );

        Assert.Equal(SalesStatisticRefreshStatus.Fresh, result.StatisticStatus);
        Assert.Equal(new[] { "P-NEW-CN1", "P-OLD-CN1" },
            result.Data!.Items.Select(row => row.ProductCode).OrderBy(code => code));
        Assert.DoesNotContain(result.Data.Items, row => row.ProductCode == "P-OLD-CN2");
    }

    [Fact]
    public async Task GetProductSalesAnalysisBranchesAsync_支持当前商品与所选商品合计两种口径()
    {
        var date = new DateTime(2026, 8, 10);
        await SeedFreshStatusAsync(date);
        await _localDb.Insertable(new[]
        {
            new Store { StoreGUID = "store-b1", StoreCode = "B1", StoreName = "Branch One" },
            new Store { StoreGUID = "store-b2", StoreCode = "B2", StoreName = "Branch Two" },
        }).ExecuteCommandAsync();
        foreach (var statistic in new[]
        {
            CreateStatistic(date, "B1", "AU1", "P1", 2, 20m),
            CreateStatistic(date, "B2", "AU1", "P1", 3, 45m),
            CreateStatistic(date, "B1", "AU1", "P2", 4, 32m),
        })
        {
            await _localDb.Insertable(statistic).ExecuteCommandAsync();
        }

        var service = CreateService();
        var currentRequest = CreateRequest(date);
        currentRequest.Scope = new ProductSalesAnalysisScopeDto
        {
            Mode = "currentProduct",
            ProductCode = "P1",
        };
        var current = await service.GetProductSalesAnalysisBranchesAsync(
            currentRequest,
            branchCodes: new List<string> { "B1", "B2" }
        );

        Assert.Equal(2, current.Data!.Count);
        Assert.Equal(2, current.Data.Single(row => row.BranchCode == "B1").Metrics.Quantity);
        Assert.Equal(3, current.Data.Single(row => row.BranchCode == "B2").Metrics.Quantity);

        var selectedRequest = CreateRequest(date);
        selectedRequest.Selection = new ProductSalesAnalysisSelectionDto
        {
            Mode = "included",
            IncludedProductCodes = new List<string> { "P1", "P2" },
        };
        selectedRequest.Scope = new ProductSalesAnalysisScopeDto { Mode = "selectedProducts" };
        var selected = await service.GetProductSalesAnalysisBranchesAsync(
            selectedRequest,
            branchCodes: new List<string> { "B1", "B2" }
        );

        var selectedRows = Assert.IsType<List<ProductSalesBranchDto>>(selected.Data);
        Assert.Equal(6, selectedRows.Single(row => row.BranchCode == "B1").Metrics.Quantity);
        Assert.Equal(52m, selectedRows.Single(row => row.BranchCode == "B1").Metrics.SalesAmount);
        Assert.Equal(3, selectedRows.Single(row => row.BranchCode == "B2").Metrics.Quantity);
    }

    [Fact]
    public async Task GetProductSalesAnalysisOptionsAsync_按期间实际供应商提取并隔离分店()
    {
        var date = new DateTime(2026, 8, 10);
        await SeedFreshStatusAsync(date);
        await _localDb.Insertable(new[]
        {
            new HBLocalSupplier { Guid = "au1", LocalSupplierCode = "AU1", Name = "Australia One", Status = 1 },
            new HBLocalSupplier { Guid = "au2", LocalSupplierCode = "AU2", Name = "Australia Two", Status = 1 },
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new ChinaSupplier
        {
            Guid = "cn1",
            SupplierCode = "CN1",
            SupplierName = "China One",
            Status = 1,
        }).ExecuteCommandAsync();
        await _posmDb.Insertable(new PosmProductSupplierMapping
        {
            ProductCode = "P-OLD",
            LocalSupplierCode = "200",
            ChinaSupplierCode = "CN2",
        }).ExecuteCommandAsync();
        foreach (var statistic in new[]
        {
            CreateStatistic(date, "B1", "AU1", "P1", 1, 10m),
            CreateStatistic(date, "B1", "CN1", "P2", 1, 10m),
            CreateStatistic(date, "B1", "200", "P-OLD", 1, 10m),
            CreateStatistic(date, "B2", "AU2", "P3", 1, 10m),
        })
        {
            await _localDb.Insertable(statistic).ExecuteCommandAsync();
        }

        var result = await CreateService().GetProductSalesAnalysisOptionsAsync(
            new ProductSalesAnalysisFilterDto { StartDate = date, EndDate = date },
            branchCodes: new List<string> { "B1" }
        );

        Assert.Equal(SalesStatisticRefreshStatus.Fresh, result.StatisticStatus);
        var australian = result.Data!.AustralianSuppliers;
        Assert.Equal(new[] { "AU1" }, australian.Select(x => x.Code));
        Assert.Equal("Australia One", australian.Single().Name);

        var china = result.Data.ChinaSuppliers;
        Assert.Equal(new[] { "CN1", "CN2" }, china.Select(x => x.Code).OrderBy(x => x));
        Assert.Equal("China One", china.Single(x => x.Code == "CN1").Name);
        Assert.Equal("CN2", china.Single(x => x.Code == "CN2").Name);
        Assert.DoesNotContain("200", china.Select(x => x.Code));
    }

    [Fact]
    public async Task GetProductSalesAnalysisOptionsAsync_停用或软删除供应商有销量仍返回()
    {
        var date = new DateTime(2026, 8, 10);
        await SeedFreshStatusAsync(date);
        await _localDb.Insertable(new[]
        {
            new ChinaSupplier { Guid = "cn-disabled", SupplierCode = "CN1", SupplierName = "China Disabled", Status = 0 },
            new ChinaSupplier { Guid = "cn-deleted", SupplierCode = "CN2", SupplierName = "China Deleted", IsDeleted = true },
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new HBLocalSupplier
        {
            Guid = "au-deleted",
            LocalSupplierCode = "AU1",
            Name = "Australia Deleted",
            IsDeleted = true,
        }).ExecuteCommandAsync();
        foreach (var statistic in new[]
        {
            CreateStatistic(date, "B1", "CN1", "P1", 1, 10m),
            CreateStatistic(date, "B1", "CN2", "P2", 1, 10m),
            CreateStatistic(date, "B1", "AU1", "P3", 1, 10m),
        })
        {
            await _localDb.Insertable(statistic).ExecuteCommandAsync();
        }

        var result = await CreateService().GetProductSalesAnalysisOptionsAsync(
            new ProductSalesAnalysisFilterDto { StartDate = date, EndDate = date },
            branchCodes: new List<string> { "B1" }
        );

        var china = result.Data!.ChinaSuppliers;
        Assert.Equal(new[] { "CN1", "CN2" }, china.Select(x => x.Code).OrderBy(x => x));
        Assert.Equal("China Disabled", china.Single(x => x.Code == "CN1").Name);
        Assert.Equal("China Deleted", china.Single(x => x.Code == "CN2").Name);

        var australian = result.Data.AustralianSuppliers;
        Assert.Equal(new[] { "AU1" }, australian.Select(x => x.Code));
        Assert.Equal("Australia Deleted", australian.Single().Name);
    }

    [Fact]
    public async Task GetProductSalesAnalysisSummaryAsync_软删除国内供应商保持历史供应商口径()
    {
        var date = new DateTime(2026, 8, 10);
        await SeedFreshStatusAsync(date);
        await _localDb.Insertable(new[]
        {
            new ChinaSupplier
            {
                Guid = "cn-soft",
                SupplierCode = "CN-SOFT",
                SupplierName = "China Soft Deleted",
                IsDeleted = true,
            },
            new ChinaSupplier
            {
                Guid = "cn-soft-dup",
                SupplierCode = "CN-SOFT",
                SupplierName = "China Soft Deleted Duplicate",
                IsDeleted = true,
            },
            new ChinaSupplier
            {
                Guid = "cn-disabled",
                SupplierCode = "CN-DISABLED",
                SupplierName = "China Disabled",
                Status = 0,
            },
        }).ExecuteCommandAsync();
        await _localDb.Insertable(new[]
        {
            CreateProduct("P-SOFT", "SOFT-CN", "Soft China Product"),
            CreateProduct("P-DISABLED", "DISABLED-CN", "Disabled China Product"),
        }).ExecuteCommandAsync();
        foreach (var statistic in new[]
        {
            CreateStatistic(date, "B1", "CN-SOFT", "P-SOFT", 2, 20m),
            CreateStatistic(date, "B1", "CN-DISABLED", "P-DISABLED", 3, 30m),
        })
        {
            await _localDb.Insertable(statistic).ExecuteCommandAsync();
        }

        var service = CreateService();

        var options = await service.GetProductSalesAnalysisOptionsAsync(
            new ProductSalesAnalysisFilterDto { StartDate = date, EndDate = date },
            branchCodes: new List<string> { "B1" }
        );
        Assert.Contains(
            options.Data!.ChinaSuppliers,
            supplier => supplier.Code == "CN-SOFT" && supplier.Name == "China Soft Deleted"
        );
        Assert.Contains(
            options.Data.ChinaSuppliers,
            supplier => supplier.Code == "CN-DISABLED" && supplier.Name == "China Disabled"
        );
        Assert.DoesNotContain(options.Data.AustralianSuppliers, supplier => supplier.Code == "CN-SOFT");
        Assert.DoesNotContain(
            options.Data.AustralianSuppliers,
            supplier => supplier.Code == "CN-DISABLED"
        );

        var unfilteredRequest = CreateRequest(date);
        var candidates = await service.GetProductSalesAnalysisCandidatesAsync(
            unfilteredRequest,
            branchCodes: new List<string> { "B1" }
        );
        Assert.Equal(2, candidates.Data!.Items.Count);
        var candidateRow = candidates.Data.Items.Single(row => row.ProductCode == "P-SOFT");
        Assert.Contains(
            candidateRow.ChinaSuppliers,
            supplier => supplier.Code == "CN-SOFT" && supplier.Name == "China Soft Deleted"
        );
        Assert.DoesNotContain(candidateRow.AustralianSuppliers, supplier => supplier.Code == "CN-SOFT");
        var disabledCandidateRow = candidates.Data.Items.Single(row => row.ProductCode == "P-DISABLED");
        Assert.Contains(
            disabledCandidateRow.ChinaSuppliers,
            supplier => supplier.Code == "CN-DISABLED" && supplier.Name == "China Disabled"
        );
        Assert.DoesNotContain(
            disabledCandidateRow.AustralianSuppliers,
            supplier => supplier.Code == "CN-DISABLED"
        );

        var summary = await service.GetProductSalesAnalysisSummaryAsync(
            unfilteredRequest,
            branchCodes: new List<string> { "B1" }
        );
        Assert.Equal(2, summary.Data!.Items.Count);
        var summaryRow = summary.Data.Items.Single(row => row.ProductCode == "P-SOFT");
        Assert.Contains(
            summaryRow.ChinaSuppliers,
            supplier => supplier.Code == "CN-SOFT" && supplier.Name == "China Soft Deleted"
        );
        Assert.DoesNotContain(summaryRow.AustralianSuppliers, supplier => supplier.Code == "CN-SOFT");
        var disabledSummaryRow = summary.Data.Items.Single(row => row.ProductCode == "P-DISABLED");
        Assert.Contains(
            disabledSummaryRow.ChinaSuppliers,
            supplier => supplier.Code == "CN-DISABLED" && supplier.Name == "China Disabled"
        );
        Assert.DoesNotContain(
            disabledSummaryRow.AustralianSuppliers,
            supplier => supplier.Code == "CN-DISABLED"
        );

        var filteredRequest = CreateRequest(date);
        filteredRequest.Filter.ChinaSupplierCodes = new List<string> { "CN-SOFT" };
        var filtered = await service.GetProductSalesAnalysisSummaryAsync(
            filteredRequest,
            branchCodes: new List<string> { "B1" }
        );
        Assert.Contains(filtered.Data!.Items, row => row.ProductCode == "P-SOFT");

        var disabledFilteredRequest = CreateRequest(date);
        disabledFilteredRequest.Filter.ChinaSupplierCodes = new List<string> { "CN-DISABLED" };
        var disabledFiltered = await service.GetProductSalesAnalysisSummaryAsync(
            disabledFilteredRequest,
            branchCodes: new List<string> { "B1" }
        );
        Assert.Contains(disabledFiltered.Data!.Items, row => row.ProductCode == "P-DISABLED");
    }

    [Fact]
    public async Task GetProductSalesAnalysisOptionsAsync_旧200缺失映射不生成国内选项()
    {
        var date = new DateTime(2026, 8, 10);
        await SeedFreshStatusAsync(date);
        await _localDb.Insertable(CreateStatistic(date, "B1", "200", "P-NOMAP", 1, 10m))
            .ExecuteCommandAsync();

        var result = await CreateService().GetProductSalesAnalysisOptionsAsync(
            new ProductSalesAnalysisFilterDto { StartDate = date, EndDate = date },
            branchCodes: new List<string> { "B1" }
        );

        Assert.Empty(result.Data!.ChinaSuppliers);
        Assert.Empty(result.Data.AustralianSuppliers);
    }

    [Fact]
    public async Task GetProductSalesAnalysisOptionsAsync_忽略关键字与供应商过滤()
    {
        var date = new DateTime(2026, 8, 10);
        await SeedFreshStatusAsync(date);
        await _localDb.Insertable(new[]
        {
            new ChinaSupplier { Guid = "cn1", SupplierCode = "CN1", SupplierName = "China One" },
            new ChinaSupplier { Guid = "cn2", SupplierCode = "CN2", SupplierName = "China Two" },
        }).ExecuteCommandAsync();
        foreach (var statistic in new[]
        {
            CreateStatistic(date, "B1", "CN1", "P1", 1, 10m),
            CreateStatistic(date, "B1", "CN2", "P2", 1, 10m),
        })
        {
            await _localDb.Insertable(statistic).ExecuteCommandAsync();
        }

        var filter = new ProductSalesAnalysisFilterDto
        {
            StartDate = date,
            EndDate = date,
            Keyword = "CN1",
            ChinaSupplierCodes = new List<string> { "CN1" },
        };
        var result = await CreateService().GetProductSalesAnalysisOptionsAsync(
            filter,
            branchCodes: new List<string> { "B1" }
        );

        Assert.Equal(
            new[] { "CN1", "CN2" },
            result.Data!.ChinaSuppliers.Select(x => x.Code).OrderBy(x => x)
        );
    }

    [Fact]
    public async Task GetChinaSupplierProductMapAsync_超过2100个国内供应商代码分批合并()
    {
        const int count = 2101;
        var mappings = Enumerable.Range(1, count)
            .Select(index => new PosmProductSupplierMapping
            {
                ProductCode = $"P{index:D4}",
                LocalSupplierCode = "200",
                ChinaSupplierCode = $"CN{index:D4}",
            })
            .ToArray();
        await _posmDb.Insertable(mappings).ExecuteCommandAsync();

        var service = CreateService();
        var supplierCodes = mappings.Select(mapping => mapping.ChinaSupplierCode!).ToList();
        var map = await service.GetChinaSupplierProductMapAsync(supplierCodes);

        Assert.Equal(count, map.Count);
        Assert.Equal("CN0001", map["P0001"]);
        Assert.Equal("CN2101", map["P2101"]);
    }

    [Fact]
    public async Task GetProductSalesAnalysisSummaryAsync_Fresh实际写入后登记活动键并在移除后释放()
    {
        SalesDashboardCacheKeys.ClearActiveKeys();
        var date = new DateTime(2026, 8, 10);
        await SeedFreshStatusAsync(date);

        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(cache);
        var request = CreateRequest(date);
        var branchCodes = new List<string> { "B1" };

        var result = await service.GetProductSalesAnalysisSummaryAsync(request, branchCodes);

        Assert.Equal(SalesStatisticRefreshStatus.Fresh, result.StatisticStatus);
        var activeKey = SalesDashboardCacheKeys.ProductSalesAnalysisSummary(
            request,
            branchCodes,
            result.CacheVersion
        );
        Assert.Contains(activeKey, SalesDashboardCacheKeys.ActiveKeys);

        ((IMemoryCache)cache).Remove(activeKey);
        await WaitUntilAsync(() => !SalesDashboardCacheKeys.ActiveKeys.Contains(activeKey));

        Assert.DoesNotContain(activeKey, SalesDashboardCacheKeys.ActiveKeys);
        SalesDashboardCacheKeys.ClearActiveKeys();
    }

    [Fact]
    public async Task GetProductSalesAnalysisSummaryAsync_cacheMiss查询期间切代_仍返回数据但不写缓存()
    {
        SalesDashboardCacheKeys.ClearActiveKeys();
        var date = new DateTime(2026, 8, 10);
        await SeedFreshStatusAsync(date);
        await _localDb
            .Insertable(new[] { CreateProduct("P1", "SKU-ALPHA", "Alpha Widget") })
            .ExecuteCommandAsync();
        await _localDb.Insertable(CreateStatistic(date, "B1", "AU1", "P1", 5, 50m))
            .ExecuteCommandAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        IMemoryCache cacheApi = cache;
        var service = CreateService(cache);
        var request = CreateRequest(date);
        var branchCodes = new List<string> { "B1" };

        // 在 cache miss 捕获代际后、写入缓存前切代，模拟查询期间发生 ClearActiveKeys。
        service.ProductSalesAnalysisCacheWriteInterceptor = () =>
            SalesDashboardCacheKeys.ClearActiveKeys();

        var result = await service.GetProductSalesAnalysisSummaryAsync(request, branchCodes);

        Assert.Equal(SalesStatisticRefreshStatus.Fresh, result.StatisticStatus);
        var row = Assert.Single(result.Data!.Items);
        Assert.Equal("P1", row.ProductCode);

        var activeKey = SalesDashboardCacheKeys.ProductSalesAnalysisSummary(
            request,
            branchCodes,
            result.CacheVersion
        );
        Assert.False(cacheApi.TryGetValue(activeKey, out _));
        Assert.DoesNotContain(activeKey, SalesDashboardCacheKeys.ActiveKeys);
        SalesDashboardCacheKeys.ClearActiveKeys();
    }

    [Fact]
    public async Task GetProductSalesAnalysisSummaryAsync_cacheMiss未切代_正常写入缓存()
    {
        SalesDashboardCacheKeys.ClearActiveKeys();
        var date = new DateTime(2026, 8, 10);
        await SeedFreshStatusAsync(date);
        await _localDb
            .Insertable(new[] { CreateProduct("P1", "SKU-ALPHA", "Alpha Widget") })
            .ExecuteCommandAsync();
        await _localDb.Insertable(CreateStatistic(date, "B1", "AU1", "P1", 5, 50m))
            .ExecuteCommandAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        IMemoryCache cacheApi = cache;
        var service = CreateService(cache);
        var request = CreateRequest(date);
        var branchCodes = new List<string> { "B1" };

        var result = await service.GetProductSalesAnalysisSummaryAsync(request, branchCodes);

        Assert.Equal(SalesStatisticRefreshStatus.Fresh, result.StatisticStatus);
        Assert.Single(result.Data!.Items);

        var activeKey = SalesDashboardCacheKeys.ProductSalesAnalysisSummary(
            request,
            branchCodes,
            result.CacheVersion
        );
        Assert.True(cacheApi.TryGetValue(activeKey, out _));
        Assert.Contains(activeKey, SalesDashboardCacheKeys.ActiveKeys);
        SalesDashboardCacheKeys.ClearActiveKeys();
    }

    [Fact]
    public void TryRegisterProductSalesAnalysisKey_预期代际不匹配时拒绝登记()
    {
        SalesDashboardCacheKeys.ClearActiveKeys();
        var key = "SalesDashboard:ProductSalesAnalysisSummary:stale-expected-generation";

        var captured = SalesDashboardCacheKeys.CaptureProductSalesAnalysisGeneration();
        SalesDashboardCacheKeys.ClearActiveKeys();

        var registered = SalesDashboardCacheKeys.TryRegisterProductSalesAnalysisKey(
            key,
            captured,
            out _,
            out var expirationToken
        );

        Assert.False(registered);
        Assert.NotNull(expirationToken);
        Assert.DoesNotContain(key, SalesDashboardCacheKeys.ActiveKeys);
        SalesDashboardCacheKeys.ClearActiveKeys();
    }

    [Fact]
    public void TryRegisterProductSalesAnalysisKey_预期代际匹配时登记()
    {
        SalesDashboardCacheKeys.ClearActiveKeys();
        var key = "SalesDashboard:ProductSalesAnalysisSummary:matching-expected-generation";

        var captured = SalesDashboardCacheKeys.CaptureProductSalesAnalysisGeneration();
        var registered = SalesDashboardCacheKeys.TryRegisterProductSalesAnalysisKey(
            key,
            captured,
            out var registrationToken,
            out var expirationToken
        );

        Assert.True(registered);
        Assert.NotNull(expirationToken);
        Assert.Contains(key, SalesDashboardCacheKeys.ActiveKeys);

        SalesDashboardCacheKeys.UnregisterProductSalesAnalysisKey(key, registrationToken);
        SalesDashboardCacheKeys.ClearActiveKeys();
    }

    [Fact]
    public void ProductSalesAnalysisCacheOptions_同键替换时旧条目驱逐不误删新登记()
    {
        SalesDashboardCacheKeys.ClearActiveKeys();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        IMemoryCache cacheApi = cache;
        var key = "SalesDashboard:ProductSalesAnalysisSummary:same-key-replacement";

        var firstOptions = SalesDashboardReactService.CreateProductSalesAnalysisCacheOptions(
            key,
            TimeSpan.FromMinutes(5)
        );
        cacheApi.Set(key, "first", firstOptions);

        var secondOptions = SalesDashboardReactService.CreateProductSalesAnalysisCacheOptions(
            key,
            TimeSpan.FromMinutes(5)
        );
        cacheApi.Set(key, "second", secondOptions);

        Assert.Contains(key, SalesDashboardCacheKeys.ActiveKeys);

        cacheApi.Remove(key);
        WaitUntil(() => !SalesDashboardCacheKeys.ActiveKeys.Contains(key));

        Assert.DoesNotContain(key, SalesDashboardCacheKeys.ActiveKeys);
        SalesDashboardCacheKeys.ClearActiveKeys();
    }

    [Fact]
    public async Task ProductSalesAnalysisCacheOptions_短TTL过期后释放活动登记()
    {
        SalesDashboardCacheKeys.ClearActiveKeys();
        using var cache = new MemoryCache(
            new MemoryCacheOptions { ExpirationScanFrequency = TimeSpan.FromMilliseconds(10) }
        );
        IMemoryCache cacheApi = cache;
        var key = "SalesDashboard:ProductSalesAnalysisOptions:short-ttl";
        var options = SalesDashboardReactService.CreateProductSalesAnalysisCacheOptions(
            key,
            TimeSpan.FromMilliseconds(30)
        );
        cacheApi.Set(key, new object(), options);

        Assert.Contains(key, SalesDashboardCacheKeys.ActiveKeys);

        await WaitUntilAsync(() =>
        {
            _ = cacheApi.TryGetValue(key, out _);
            return !SalesDashboardCacheKeys.ActiveKeys.Contains(key);
        });

        Assert.DoesNotContain(key, SalesDashboardCacheKeys.ActiveKeys);
        SalesDashboardCacheKeys.ClearActiveKeys();
    }

    [Fact]
    public void ProductSalesAnalysisCacheOptions_Clear后旧条目驱逐不得清掉新登记()
    {
        SalesDashboardCacheKeys.ClearActiveKeys();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        IMemoryCache cacheApi = cache;
        var key = "SalesDashboard:ProductSalesAnalysisSummary:clear-then-evict";

        var oldOptions = SalesDashboardReactService.CreateProductSalesAnalysisCacheOptions(
            key,
            TimeSpan.FromMinutes(5)
        );
        cacheApi.Set(key, "old", oldOptions);
        Assert.Contains(key, SalesDashboardCacheKeys.ActiveKeys);

        SalesDashboardCacheKeys.ClearActiveKeys();
        Assert.DoesNotContain(key, SalesDashboardCacheKeys.ActiveKeys);
        Assert.False(cacheApi.TryGetValue(key, out _));

        var newOptions = SalesDashboardReactService.CreateProductSalesAnalysisCacheOptions(
            key,
            TimeSpan.FromMinutes(5)
        );
        Assert.Contains(key, SalesDashboardCacheKeys.ActiveKeys);

        cacheApi.Set(key, "new", newOptions);

        // 直接触发旧 entry 绑定的驱逐回调（携带旧 State），证明 callback 绑定的
        // 旧 registrationToken 不会误删新代登记，而不是仅覆盖 Unregister 核心函数。
        var oldCallback = Assert.Single(oldOptions.PostEvictionCallbacks);
        oldCallback.EvictionCallback!(key, null, EvictionReason.TokenExpired, oldCallback.State);

        Assert.Contains(key, SalesDashboardCacheKeys.ActiveKeys);

        cacheApi.Remove(key);
        WaitUntil(() => !SalesDashboardCacheKeys.ActiveKeys.Contains(key));

        Assert.DoesNotContain(key, SalesDashboardCacheKeys.ActiveKeys);
        SalesDashboardCacheKeys.ClearActiveKeys();
    }

    [Fact]
    public void UnregisterProductSalesAnalysisKey_旧代token不释放新代登记()
    {
        SalesDashboardCacheKeys.ClearActiveKeys();
        var key = "SalesDashboard:ProductSalesAnalysisSummary:stale-generation";

        var oldToken = SalesDashboardCacheKeys.RegisterProductSalesAnalysisKey(key);
        SalesDashboardCacheKeys.ClearActiveKeys();
        SalesDashboardCacheKeys.RegisterProductSalesAnalysisKey(key);

        SalesDashboardCacheKeys.UnregisterProductSalesAnalysisKey(key, oldToken);

        Assert.Contains(key, SalesDashboardCacheKeys.ActiveKeys);
        SalesDashboardCacheKeys.UnregisterProductSalesAnalysisKey(key);
        Assert.DoesNotContain(key, SalesDashboardCacheKeys.ActiveKeys);
        SalesDashboardCacheKeys.ClearActiveKeys();
    }

    [Fact]
    public void ProductSalesAnalysisCacheOptions_登记旧代后清缓存再Set_旧条目不可命中()
    {
        SalesDashboardCacheKeys.ClearActiveKeys();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        IMemoryCache cacheApi = cache;
        var key = "SalesDashboard:ProductSalesAnalysisSummary:register-then-clear-then-set";

        var oldOptions = SalesDashboardReactService.CreateProductSalesAnalysisCacheOptions(
            key,
            TimeSpan.FromMinutes(5)
        );

        SalesDashboardCacheKeys.ClearActiveKeys();

        cacheApi.Set(key, "stale", oldOptions);

        Assert.False(cacheApi.TryGetValue(key, out _));
        Assert.DoesNotContain(key, SalesDashboardCacheKeys.ActiveKeys);
        SalesDashboardCacheKeys.ClearActiveKeys();
    }

    [Fact]
    public async Task TryExecuteProductSalesAnalysisCacheWrite_clear等待已校验写入后再切代()
    {
        SalesDashboardCacheKeys.ClearActiveKeys();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        IMemoryCache cacheApi = cache;
        var key = "SalesDashboard:ProductSalesAnalysisSummary:lifecycle-gated-set";
        var oldGeneration = SalesDashboardCacheKeys.CaptureProductSalesAnalysisGeneration();
        using var oldWriteEntered = new ManualResetEventSlim(false);
        using var allowOldWriteToFinish = new ManualResetEventSlim(false);
        using var clearAttempted = new ManualResetEventSlim(false);

        var oldWriteTask = Task.Run(() =>
            SalesDashboardCacheKeys.TryExecuteProductSalesAnalysisCacheWrite(
                key,
                oldGeneration,
                (_, expirationToken) =>
                {
                    oldWriteEntered.Set();
                    Assert.True(allowOldWriteToFinish.Wait(AsyncTestWaitSupport.DefaultTimeout));
                    cacheApi.Set(
                        key,
                        "old",
                        new MemoryCacheEntryOptions().AddExpirationToken(expirationToken)
                    );
                }
            )
        );
        Assert.True(oldWriteEntered.Wait(AsyncTestWaitSupport.DefaultTimeout));

        var clearTask = Task.Run(() =>
        {
            clearAttempted.Set();
            SalesDashboardCacheKeys.ClearActiveKeys();
        });
        Assert.True(clearAttempted.Wait(AsyncTestWaitSupport.DefaultTimeout));

        // clear 已开始但必须等待旧写入的短生命周期临界区结束。
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.False(clearTask.IsCompleted);
        allowOldWriteToFinish.Set();
        Assert.True(await oldWriteTask);
        await clearTask;

        var newGeneration = SalesDashboardCacheKeys.CaptureProductSalesAnalysisGeneration();
        var newWriteExecuted = SalesDashboardCacheKeys.TryExecuteProductSalesAnalysisCacheWrite(
            key,
            newGeneration,
            (_, expirationToken) =>
                cacheApi.Set(
                    key,
                    "new",
                    new MemoryCacheEntryOptions().AddExpirationToken(expirationToken)
                )
        );

        Assert.True(newWriteExecuted);
        Assert.Equal("new", cacheApi.Get<string>(key));
        Assert.Contains(key, SalesDashboardCacheKeys.ActiveKeys);
        SalesDashboardCacheKeys.ClearActiveKeys();
    }

    [Fact]
    public async Task ClearCacheAsync_登记旧代后清缓存再Set_旧条目不可命中()
    {
        SalesDashboardCacheKeys.ClearActiveKeys();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        IMemoryCache cacheApi = cache;
        var key = "SalesDashboard:ProductSalesAnalysisSummary:clear-cache-then-set";

        var oldOptions = SalesDashboardReactService.CreateProductSalesAnalysisCacheOptions(
            key,
            TimeSpan.FromMinutes(5)
        );

        var warmer = new SalesDashboardCacheWarmer(
            Mock.Of<ISalesDashboardReactService>(),
            NullLogger<SalesDashboardCacheWarmer>.Instance,
            cache
        );
        await warmer.ClearCacheAsync();

        cacheApi.Set(key, "stale", oldOptions);

        Assert.False(cacheApi.TryGetValue(key, out _));
        Assert.DoesNotContain(key, SalesDashboardCacheKeys.ActiveKeys);
        SalesDashboardCacheKeys.ClearActiveKeys();
    }

    [Fact]
    public void ClearActiveKeysAndGetKeysToClear_商品键不进入待移除列表()
    {
        SalesDashboardCacheKeys.ClearActiveKeys();
        var generalKey = SalesDashboardCacheKeys.Summary(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 8, 1),
                EndDate = new DateTime(2026, 8, 18),
            },
            null
        );
        var productKey = "SalesDashboard:ProductSalesAnalysisSummary:product-key";
        SalesDashboardCacheKeys.RegisterProductSalesAnalysisKey(productKey);

        var keysToRemove = SalesDashboardCacheKeys.ClearActiveKeysAndGetKeysToClear();

        Assert.Contains(generalKey, keysToRemove);
        Assert.DoesNotContain(productKey, keysToRemove);
        Assert.DoesNotContain(productKey, SalesDashboardCacheKeys.ActiveKeys);
        Assert.DoesNotContain(generalKey, SalesDashboardCacheKeys.ActiveKeys);
        SalesDashboardCacheKeys.ClearActiveKeys();
    }

    [Fact]
    public async Task ClearCacheAsync_切代后新代同key不会被普通清理移除()
    {
        SalesDashboardCacheKeys.ClearActiveKeys();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        IMemoryCache cacheApi = cache;
        var key = "SalesDashboard:ProductSalesAnalysisSummary:same-key-across-generation";

        var oldOptions = SalesDashboardReactService.CreateProductSalesAnalysisCacheOptions(
            key,
            TimeSpan.FromMinutes(5)
        );
        cacheApi.Set(key, "old", oldOptions);
        Assert.Contains(key, SalesDashboardCacheKeys.ActiveKeys);

        var generalKey = SalesDashboardCacheKeys.Summary(
            new DateRangeDto
            {
                StartDate = new DateTime(2026, 8, 1),
                EndDate = new DateTime(2026, 8, 18),
            },
            null
        );

        using var removeStarted = new ManualResetEventSlim(false);
        using var allowRemoveToFinish = new ManualResetEventSlim(false);

        var blockingCache = new Mock<IMemoryCache>();
        blockingCache
            .Setup(c => c.Remove(It.IsAny<object>()))
            .Callback<object>(k =>
            {
                if (k is string keyString && keyString == generalKey)
                {
                    removeStarted.Set();
                    allowRemoveToFinish.Wait(AsyncTestWaitSupport.DefaultTimeout);
                }

                cacheApi.Remove(k);
            });

        var warmer = new SalesDashboardCacheWarmer(
            Mock.Of<ISalesDashboardReactService>(),
            NullLogger<SalesDashboardCacheWarmer>.Instance,
            blockingCache.Object
        );

        var clearTask = Task.Run(async () => await warmer.ClearCacheAsync());
        Assert.True(removeStarted.Wait(AsyncTestWaitSupport.DefaultTimeout));

        // 清理已切代并正在移除普通键期间，新代 Set 同 key。
        var newOptions = SalesDashboardReactService.CreateProductSalesAnalysisCacheOptions(
            key,
            TimeSpan.FromMinutes(5)
        );
        cacheApi.Set(key, "new", newOptions);

        allowRemoveToFinish.Set();
        await clearTask;

        Assert.True(cacheApi.TryGetValue(key, out _));
        Assert.Contains(key, SalesDashboardCacheKeys.ActiveKeys);

        cacheApi.Remove(key);
        SalesDashboardCacheKeys.ClearActiveKeys();
    }

    private async Task SeedFreshStatusAsync(DateTime date)
    {
        await _localDb.Insertable(new SalesStatisticRefreshState
        {
            StatisticType = SalesStatisticType.ProductStoreDaily,
            Date = date,
            Status = SalesStatisticRefreshStatus.Fresh,
            SourceTimeZone = "POSM_LOCAL",
        }).ExecuteCommandAsync();
    }

    private static ProductSalesAnalysisRequest CreateRequest(DateTime date, string? keyword = null)
    {
        return new ProductSalesAnalysisRequest
        {
            Filter = new ProductSalesAnalysisFilterDto
            {
                StartDate = date,
                EndDate = date,
                Keyword = keyword,
            },
            Selection = new ProductSalesAnalysisSelectionDto { Mode = "allFiltered" },
        };
    }

    private static Product CreateProduct(string productCode, string itemNumber, string name)
    {
        return new Product
        {
            UUID = $"uuid-{productCode}",
            ProductCode = productCode,
            ItemNumber = itemNumber,
            ProductName = name,
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

    private SalesDashboardReactService CreateService()
    {
        return CreateService(new MemoryCache(new MemoryCacheOptions()));
    }

    private SalesDashboardReactService CreateService(IMemoryCache cache)
    {
        return new SalesDashboardReactService(
            CreateSqlSugarContext(_localDb),
            CreatePosmSqlSugarContext(_posmDb),
            Mock.Of<IMapper>(),
            NullLogger<SalesDashboardReactService>.Instance,
            cache
        );
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

    private static POSMSqlSugarContext CreatePosmSqlSugarContext(ISqlSugarClient db)
    {
        var context = (POSMSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(POSMSqlSugarContext));
        typeof(POSMSqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    public void Dispose()
    {
        _localConnection.Dispose();
        _posmConnection.Dispose();
        if (File.Exists(_localDbPath)) SqliteTempFileCleanup.DeleteIfExists(_localDbPath);
        if (File.Exists(_posmDbPath)) SqliteTempFileCleanup.DeleteIfExists(_posmDbPath);
    }
}
