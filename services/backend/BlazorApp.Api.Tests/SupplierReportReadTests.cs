using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using BlazorApp.Api.Cache;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Data;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class SupplierReportReadTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"supplier-read-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly DateTime _day = new(2026, 8, 24);

    public SupplierReportReadTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_path}", DbType = DbType.Sqlite,
            IsAutoCloseConnection = true, InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables<SalesStatisticRefreshState>();
        _db.CodeFirst.InitTables<AustralianSupplierStoreSalesDetail>();
        _db.CodeFirst.InitTables<ChinaSupplierStoreSalesDetail>();
        _db.CodeFirst.InitTables<ProductStoreDailySalesStatistic>();
        _db.CodeFirst.InitTables<HBLocalSupplier>();
        _db.CodeFirst.InitTables<ChinaSupplier>();
        _db.CodeFirst.InitTables<Store>();
    }

    [Fact]
    public async Task 快照读取完成后释放自有事务且嵌套读取不提前提交()
    {
        var (db, ado) = CreateSnapshotDb();
        var service = CreateService(db: db.Object);
        var result = await ReadSnapshot(service, async () =>
        {
            Assert.Equal(System.Data.IsolationLevel.Snapshot, ado.Object.Transaction.IsolationLevel);
            var transaction = ado.Object.Transaction;
            var nested = await ReadSnapshot(service, () => Task.FromResult(42));
            Assert.Same(transaction, ado.Object.Transaction);
            ado.Verify(value => value.CommitTranAsync(), Times.Never);
            return nested;
        });
        Assert.Equal(42, result);
        Assert.Null(ado.Object.Transaction);
        ado.Verify(value => value.BeginTranAsync(System.Data.IsolationLevel.Snapshot), Times.Once);
        ado.Verify(value => value.CommitTranAsync(), Times.Once);
    }

    [Fact]
    public async Task 快照读取失败回滚且后续请求可以重新读取()
    {
        var (db, ado) = CreateSnapshotDb();
        var service = CreateService(db: db.Object);
        var failure = new InvalidOperationException("模拟读取失败");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ReadSnapshot(service, () => Task.FromException<int>(failure)));
        Assert.Same(failure, actual);
        Assert.Null(ado.Object.Transaction);
        ado.Verify(value => value.CommitTranAsync(), Times.Never);
        ado.Verify(value => value.RollbackTranAsync(), Times.Once);
        Assert.Equal(7, await ReadSnapshot(service, () => Task.FromResult(7)));
        ado.Verify(value => value.CommitTranAsync(), Times.Once);
    }

    [Fact]
    public async Task 未启用快照隔离时保持原读取且不更改数据库设置()
    {
        var (db, ado) = CreateSnapshotDb(available: false);
        var service = CreateService(db: db.Object);
        Assert.Equal(9, await ReadSnapshot(service, () => Task.FromResult(9)));
        Assert.Equal(10, await ReadSnapshot(service, () => Task.FromResult(10)));
        ado.Verify(value => value.BeginTranAsync(It.IsAny<System.Data.IsolationLevel>()), Times.Never);
        ado.Verify(value => value.GetIntAsync(It.IsAny<string>(), It.IsAny<SugarParameter[]>()), Times.Once);
    }

    [Fact]
    public async Task 主库与映射库分别检查快照能力且不混用事务()
    {
        var (mainDb, mainAdo) = CreateSnapshotDb(available: false);
        var (mappingDb, mappingAdo) = CreateSnapshotDb();
        var service = CreateService(db: mainDb.Object);
        await ReadSnapshot(service, () => Task.FromResult(1));
        Func<Task<int>> read = () =>
        {
            Assert.Null(mainAdo.Object.Transaction);
            Assert.Equal(System.Data.IsolationLevel.Snapshot, mappingAdo.Object.Transaction.IsolationLevel);
            return Task.FromResult(2);
        };
        var result = (Task<int>)typeof(SalesDashboardReactService)
            .GetMethod("ReadReportSnapshotOnConnectionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(int)).Invoke(service, new object[] { mappingDb.Object, read })!;
        Assert.Equal(2, await result);
        Assert.Null(mappingAdo.Object.Transaction);
        mainAdo.Verify(value => value.BeginTranAsync(It.IsAny<System.Data.IsolationLevel>()), Times.Never);
        mappingAdo.Verify(value => value.CommitTranAsync(), Times.Once);
    }

    private static Task<int> ReadSnapshot(SalesDashboardReactService service, Func<Task<int>> read) =>
        (Task<int>)typeof(SalesDashboardReactService).GetMethod("ReadReportSnapshotAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(int)).Invoke(service, new object[] { read })!;

    private static (Mock<ISqlSugarClient> Db, Mock<IAdo> Ado) CreateSnapshotDb(bool available = true)
    {
        var ado = new Mock<IAdo>(MockBehavior.Strict);
        ado.SetupProperty(value => value.Transaction);
        ado.Setup(value => value.GetIntAsync(It.IsAny<string>(), It.IsAny<SugarParameter[]>())).ReturnsAsync(available ? 1 : 0);
        ado.Setup(value => value.BeginTranAsync(System.Data.IsolationLevel.Snapshot)).Callback(() =>
        {
            var transaction = new Mock<System.Data.IDbTransaction>();
            transaction.SetupGet(value => value.IsolationLevel).Returns(System.Data.IsolationLevel.Snapshot);
            ado.Object.Transaction = transaction.Object;
        }).Returns(Task.CompletedTask);
        ado.Setup(value => value.CommitTranAsync()).Callback(() => ado.Object.Transaction = null).Returns(Task.CompletedTask);
        ado.Setup(value => value.RollbackTranAsync()).Callback(() => ado.Object.Transaction = null).Returns(Task.CompletedTask);
        var db = new Mock<ISqlSugarClient>(MockBehavior.Strict);
        db.SetupGet(value => value.Ado).Returns(ado.Object);
        db.SetupGet(value => value.CurrentConnectionConfig).Returns(new ConnectionConfig { DbType = DbType.SqlServer });
        return (db, ado);
    }

    [Fact]
    public async Task 供应商读取统计表并保留金额客单数和跨日分店去重()
    {
        SeedComplete(_day);
        SeedComplete(_day.AddDays(1));
        SeedComplete(_day.AddYears(-1));
        SeedRow(_day, "S1", "250", 100, 5, 40);
        SeedRow(_day.AddDays(1), "S1", "250", 200, 8, 60);
        SeedRow(_day.AddDays(1), "S2", "250", 50, 2, 10);
        SeedRow(_day.AddYears(-1), "S1", "250", 70, 4, 20);
        SeedRow(_day, "S3", "250", 999, 50, 100);
        var range = new DateRangeDto
        {
            StartDate = _day, EndDate = _day.AddDays(1),
            CompareStartDate = _day.AddYears(-1), CompareEndDate = _day.AddYears(-1),
        };
        var sql = new List<string>();
        _db.Aop.OnLogExecuting = (query, _) => sql.Add(query);
        var rows = await CreateService().GetSupplierSalesRankAsync(range, new() { "S1", "S2" }, 100);
        var row = Assert.Single(rows);
        Assert.Equal(350m, row.TotalAmount);
        Assert.Equal(15, row.OrderCount);
        Assert.Equal(2, row.StoreCount);
        Assert.Equal(110m, row.GrossProfit);
        Assert.Equal(70m, row.CompareTotalAmount);
        Assert.Equal(20m, row.CompareGrossProfit);
        Assert.Equal(350m / 15, row.AverageTransaction);
        Assert.DoesNotContain(sql, query => query.Contains("FROM [ProductStoreDailySalesStatistic]", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sql, query => query.Contains("SalesOrder", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task 中国供应商与本地供应商各读自己的统计表()
    {
        SeedComplete(_day);
        SeedRow(_day, "S1", "200", 900, 10, 300);
        _db.Insertable(new ChinaSupplierStoreSalesDetail
        {
            Date = _day, BranchCode = "S1", SupplierCode = "C001", TotalAmount = 125,
            TotalQuantity = 4, OrderCount = 3, GrossProfit = 25, TotalCost = 100,
            StatisticRowCount = 2, CostedRowCount = 2, GrossProfitRowCount = 2,
        }).ExecuteCommand();
        var service = CreateService();
        var local = Assert.Single(await service.GetSupplierSalesRankAsync(Range(), new() { "S1" }, 100));
        var china = Assert.Single(await service.GetChinaSupplierSalesRankAsync(Range(), new() { "S1" }, 100));
        Assert.Equal("200", local.SupplierCode);
        Assert.Equal(900m, local.TotalAmount);
        Assert.Equal("C001", china.SupplierCode);
        Assert.Equal(125m, china.TotalAmount);
        Assert.Equal(25m, china.GrossProfit);
        var stores = await service.GetChinaSupplierStoreSalesAsync(Range(), new() { "C001" }, new() { "S1" });
        Assert.Equal("S1", Assert.Single(stores).BranchCode);
    }

    [Fact]
    public async Task 中国货分店合计读汇总表且同期覆盖本期未出现的中国供应商()
    {
        var compareDay = _day.AddYears(-1);
        SeedComplete(_day);
        SeedComplete(compareDay);
        _db.Insertable(new[]
        {
            new ChinaSupplierStoreSalesDetail
            {
                Date = _day, BranchCode = "S1", SupplierCode = "C001", TotalAmount = 125, TotalQuantity = 4, OrderCount = 3,
                GrossProfit = 25, TotalCost = 100, StatisticRowCount = 2, CostedRowCount = 2, GrossProfitRowCount = 2,
            },
            new ChinaSupplierStoreSalesDetail
            {
                Date = _day, BranchCode = "S1", SupplierCode = "C002", TotalAmount = 75, TotalQuantity = 6, OrderCount = 2,
                GrossProfit = 15, TotalCost = 60, StatisticRowCount = 1, CostedRowCount = 1, GrossProfitRowCount = 1,
            },
            // S2 本期缺成本：整店毛利不可信，必须返回 null 而不是只算有成本的那部分。
            new ChinaSupplierStoreSalesDetail
            {
                Date = _day, BranchCode = "S2", SupplierCode = "C001", TotalAmount = 40, TotalQuantity = 2, OrderCount = 1,
                GrossProfit = null, TotalCost = null, StatisticRowCount = 1, CostedRowCount = 0, GrossProfitRowCount = 0,
            },
            // C009 只在同期出现：排行的同期会漏掉它，分店中国货同期必须计入。
            new ChinaSupplierStoreSalesDetail
            {
                Date = compareDay, BranchCode = "S1", SupplierCode = "C009", TotalAmount = 70, TotalQuantity = 7, OrderCount = 2,
                GrossProfit = 20, TotalCost = 50, StatisticRowCount = 1, CostedRowCount = 1, GrossProfitRowCount = 1,
            },
        }).ExecuteCommand();
        _db.Insertable(new Store { StoreGUID = "store-S1", StoreCode = "S1", StoreName = "分店一", IsActive = true }).ExecuteCommand();
        var service = CreateService();
        var range = new DateRangeDto { StartDate = _day, EndDate = _day, CompareStartDate = compareDay, CompareEndDate = compareDay };
        var status = await service.GetProductReportStatisticStatusAsync(range);

        var result = await service.GetChinaSupplierBranchTotalsAsync(range, new() { "S1", "S2" }, status);

        Assert.Equal(SalesStatisticRefreshStatus.Fresh, status.StatisticStatus);
        Assert.Equal(new[] { "S1", "S2" }, result.Select(row => row.BranchCode));
        var s1 = result[0];
        Assert.Equal("分店一", s1.BranchName);
        Assert.Equal(200m, s1.TotalAmount);
        Assert.Equal(10, s1.TotalQuantity);
        Assert.Equal(2, s1.SupplierCount);
        Assert.Equal(40m, s1.GrossProfit);
        Assert.Equal("Complete", s1.CostStatus);
        Assert.Equal(70m, s1.CompareTotalAmount);
        Assert.Equal(20m, s1.CompareGrossProfit);
        var s2 = result[1];
        Assert.Equal(40m, s2.TotalAmount);
        Assert.Null(s2.GrossProfit);
        Assert.Equal("Missing", s2.CostStatus);
        Assert.Equal(0m, s2.CompareTotalAmount);
        Assert.Equal("NoActivity", s2.CompareCostStatus);

        var rank = await service.GetChinaSupplierSalesRankAsync(range, new() { "S1", "S2" }, 1000);
        Assert.Equal(rank.Sum(row => row.TotalAmount), result.Sum(row => row.TotalAmount));
        Assert.Empty(await service.GetChinaSupplierBranchTotalsAsync(range, new(), status));
    }

    [Fact]
    public async Task 商品分页只保留本期同期授权分店内的遗留中国商品映射()
    {
        var compare = _day.AddYears(-1);
        _db.Insertable(new[]
        {
            new ProductStoreDailySalesStatistic { Date = _day, BranchCode = "S1", ProductCode = "current", SupplierCode = "200" },
            new ProductStoreDailySalesStatistic { Date = compare, BranchCode = "S1", ProductCode = "compare", SupplierCode = "200" },
            new ProductStoreDailySalesStatistic { Date = _day.AddDays(1), BranchCode = "S1", ProductCode = "other-date", SupplierCode = "200" },
            new ProductStoreDailySalesStatistic { Date = _day, BranchCode = "S2", ProductCode = "other-branch", SupplierCode = "200" },
            new ProductStoreDailySalesStatistic { Date = _day, BranchCode = "S1", ProductCode = "direct-china", SupplierCode = "C001" },
        }).ExecuteCommand();
        var range = Range();
        range.CompareStartDate = range.CompareEndDate = compare;
        var codes = await CreateService().GetReportLegacyChinaProductCodesAsync(range, new() { "S1" });
        Assert.Equal(new[] { "compare", "current" }, codes.OrderBy(value => value).ToArray());
        Assert.Contains("CURRENT", codes);
    }

    [Fact]
    public async Task 任一行成本不完整则整个期间毛利为空()
    {
        SeedComplete(_day);
        SeedRow(_day, "S1", "250", 100, 5, 40);
        SeedRow(_day, "S2", "250", 50, 2, null, costedRows: 0);
        var row = Assert.Single(await CreateService().GetSupplierSalesRankAsync(Range(), null, 100));
        Assert.Equal(150m, row.TotalAmount);
        Assert.Null(row.GrossProfit);
        Assert.Null(row.GrossMarginRate);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("mismatch")]
    [InlineData("running")]
    [InlineData("unversioned")]
    public async Task 三类统计未全部完成同一版本时不能返回Fresh(string scenario)
    {
        SeedComplete(_day);
        if (scenario == "missing")
            _db.Deleteable<SalesStatisticRefreshState>().Where(row => row.StatisticType == SalesStatisticType.ChinaSupplierStoreSales).ExecuteCommand();
        else if (scenario == "unversioned")
            _db.Updateable<SalesStatisticRefreshState>().SetColumns(row => row.SourceProductVersion == null).Where(row => row.Date == _day).ExecuteCommand();
        else
            _db.Updateable<SalesStatisticRefreshState>()
                .SetColumns(row => row.SourceProductVersion == (scenario == "mismatch" ? "another-version" : "version"))
                .SetColumns(row => row.Status == (scenario == "running" ? SalesStatisticRefreshStatus.Running : SalesStatisticRefreshStatus.Fresh))
                .Where(row => row.StatisticType == SalesStatisticType.ChinaSupplierStoreSales).ExecuteCommand();
        var status = await CreateService().GetProductReportStatisticStatusAsync(Range());
        Assert.Equal(SalesStatisticRefreshStatus.Pending, status.StatisticStatus);
    }

    [Fact]
    public async Task 失败商品替换后的空发布版本在排队时不能复用供应商快照()
    {
        SeedComplete(_day);
        SeedRow(_day, "S1", "250", 100, 5, 40);
        _db.Updateable<SalesStatisticRefreshState>()
            .SetColumns(row => row.Status == SalesStatisticRefreshStatus.Queued)
            .SetColumns(row => row.SourceProductVersion == null)
            .SetColumns(row => row.CompletedAtUtc == null)
            .Where(row => row.StatisticType == SalesStatisticType.ProductStoreDaily).ExecuteCommand();
        var service = CreateService();
        var status = await service.GetProductReportStatisticStatusAsync(Range());
        Assert.Equal(SalesStatisticRefreshStatus.Pending, status.StatisticStatus);
        Assert.Empty(await service.GetSupplierSalesRankAsync(Range(), null, 100));
    }

    [Theory]
    [InlineData(SalesStatisticRefreshStatus.Queued)]
    [InlineData(SalesStatisticRefreshStatus.Running)]
    public async Task 后台刷新期间首次请求直接读取上次完整版本并复用缓存(string progress)
    {
        SeedComplete(_day);
        SeedRow(_day, "S1", "250", 100, 5, 40);
        var service = CreateService();
        var before = await service.GetProductReportStatisticStatusAsync(Range());
        _db.Updateable<SalesStatisticRefreshState>()
            .SetColumns(row => row.Status == progress)
            .SetColumns(row => row.CompletedAtUtc == null)
            .Where(row => row.StatisticType == SalesStatisticType.ProductStoreDaily).ExecuteCommand();
        var reads = 0;
        _db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.Contains("FROM [AustralianSupplierStoreSalesDetail]", StringComparison.OrdinalIgnoreCase)) reads++;
        };
        var during = await service.GetProductReportStatisticStatusAsync(Range());
        Assert.Equal(SalesStatisticRefreshStatus.Fresh, during.StatisticStatus);
        Assert.Equal(before.CacheVersion, during.CacheVersion);
        Assert.Equal(100m, Assert.Single(await service.GetSupplierSalesRankAsync(Range(), null, 100)).TotalAmount);
        Assert.Equal(100m, Assert.Single(await service.GetSupplierSalesRankAsync(Range(), null, 100)).TotalAmount);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task 旧字段没有回填时不把空数据标记为Fresh()
    {
        SeedComplete(_day);
        SeedRow(_day, "S1", "250", 100, 5, 40);
        _db.Updateable<AustralianSupplierStoreSalesDetail>().SetColumns(row => row.CostedRowCount == null)
            .Where(row => row.Date == _day).ExecuteCommand();
        var service = CreateService();
        var status = await service.GetProductReportStatisticStatusAsync(Range());
        var data = await service.GetSupplierSalesRankAsync(Range(), null, 100, null, status);
        Assert.Empty(data);
        Assert.Equal(SalesStatisticRefreshStatus.Pending, status.StatisticStatus);
    }

    [Fact]
    public async Task 读取期间版本变化则丢弃本次结果并允许重试()
    {
        SeedComplete(_day);
        SeedRow(_day, "S1", "250", 100, 5, 40);
        var stateReads = 0;
        _db.Aop.OnLogExecuting = (sql, _) =>
        {
            // 在结果已物化、最后一次状态校验开始前，模拟另一事务发布新版本。
            if (!sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                || !sql.Contains("SalesStatisticRefreshState", StringComparison.OrdinalIgnoreCase)
                || ++stateReads != 3) return;
            using var writer = _db.CopyNew();
            writer.Aop.OnLogExecuting = null;
            writer.Updateable<SalesStatisticRefreshState>().SetColumns(row => row.SourceProductVersion == "new-version")
                .Where(row => row.Date == _day).ExecuteCommand();
        };
        var service = CreateService();
        var status = await service.GetProductReportStatisticStatusAsync(Range());
        Assert.Empty(await service.GetSupplierSalesRankAsync(Range(), null, 100, null, status));
        Assert.Equal(SalesStatisticRefreshStatus.Pending, status.StatisticStatus);
        Assert.Single(await service.GetSupplierSalesRankAsync(Range(), null, 100));
    }

    [Fact]
    public async Task 空授权分店不能命中全部分店缓存()
    {
        SeedComplete(_day);
        SeedRow(_day, "S1", "250", 100, 5, 40);
        var service = CreateService();
        Assert.Single(await service.GetSupplierSalesRankAsync(Range(), null, 100));
        Assert.Empty(await service.GetSupplierSalesRankAsync(Range(), new(), 100));
        Assert.Empty(await service.GetSupplierSalesRankAsync(Range(), new() { "S2" }, 100));
    }

    [Fact]
    public async Task 相同完整版本复用缓存而新版本重新读取()
    {
        SeedComplete(_day);
        SeedRow(_day, "S1", "250", 100, 5, 40);
        var reads = 0;
        _db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.Contains("FROM [AustralianSupplierStoreSalesDetail]", StringComparison.OrdinalIgnoreCase)) reads++;
        };
        var service = CreateService();
        await service.GetSupplierSalesRankAsync(Range(), null, 100);
        await service.GetSupplierSalesRankAsync(Range(), null, 100);
        Assert.Equal(1, reads);
        _db.Updateable<SalesStatisticRefreshState>().SetColumns(row => row.SourceProductVersion == "new-version")
            .Where(row => row.Date == _day).ExecuteCommand();
        await service.GetSupplierSalesRankAsync(Range(), null, 100);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task 统计刷新后的自动清理保留版本化完整报表条目而手动全量清理会移除()
    {
        SeedComplete(_day);
        SeedRow(_day, "S1", "250", 100, 5, 40);
        var reads = 0;
        _db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.Contains("FROM [AustralianSupplierStoreSalesDetail]", StringComparison.OrdinalIgnoreCase)) reads++;
        };
        var service = CreateService();
        var warmer = new SalesDashboardCacheWarmer(service, NullLogger<SalesDashboardCacheWarmer>.Instance, _cache);

        await service.GetSupplierSalesRankAsync(Range(), null, 100);
        Assert.Equal(1, reads);

        // 每半小时统计刷新后的清理：键里已含 cacheVersion 的条目不会陈旧，必须保留。
        await warmer.ClearCacheAsync();
        await service.GetSupplierSalesRankAsync(Range(), null, 100);
        Assert.Equal(1, reads);

        // 管理员手动全量清理仍然要能强制重读（例如门店名、商品图这类非版本化字段变了）。
        await warmer.ClearAllCacheAsync();
        await service.GetSupplierSalesRankAsync(Range(), null, 100);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task 开关默认关闭时不要求供应商版本()
    {
        SeedComplete(_day);
        _db.Deleteable<SalesStatisticRefreshState>().Where(row => row.StatisticType != SalesStatisticType.ProductStoreDaily).ExecuteCommand();
        var status = await CreateService(enabled: false).GetProductReportStatisticStatusAsync(Range());
        Assert.Equal(SalesStatisticRefreshStatus.Fresh, status.StatisticStatus);
    }

    [Fact]
    public async Task 相同版本的并发请求只执行一次供应商读取()
    {
        SeedComplete(_day);
        SeedRow(_day, "S1", "250", 100, 5, 40);
        using var release = new ManualResetEventSlim();
        var firstReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondVersionRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reportReads = 0;
        var secondStatusReads = 0;
        var leases = new ConcurrentBag<ReportTestDbScope>();
        using var provider = new ServiceCollection()
            .AddScoped(_ =>
            {
                var lease = new ReportTestDbScope(_db.CopyNew());
                leases.Add(lease);
                lease.Db.Aop.OnLogExecuting = (sql, _) =>
                {
                    if (!sql.Contains("FROM [AustralianSupplierStoreSalesDetail]", StringComparison.OrdinalIgnoreCase)) return;
                    Interlocked.Increment(ref reportReads);
                    firstReadStarted.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                };
                return lease;
            })
            .AddScoped<ISalesDashboardReactService>(services => CreateService(
                db: services.GetRequiredService<ReportTestDbScope>().Db,
                scopeFactory: services.GetRequiredService<IServiceScopeFactory>()))
            .BuildServiceProvider();
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        var firstService = firstScope.ServiceProvider.GetRequiredService<ISalesDashboardReactService>();
        var secondService = secondScope.ServiceProvider.GetRequiredService<ISalesDashboardReactService>();
        var firstLease = firstScope.ServiceProvider.GetRequiredService<ReportTestDbScope>();
        var secondLease = secondScope.ServiceProvider.GetRequiredService<ReportTestDbScope>();
        secondLease.Db.Aop.OnLogExecuted = (sql, _) =>
        {
            if (sql.Contains("SalesStatisticRefreshState", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Increment(ref secondStatusReads) == 2)
                secondVersionRead.TrySetResult();
        };
        var first = Task.Run(() => firstService.GetSupplierSalesRankAsync(Range(), null, 100));
        try
        {
            await firstReadStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            var second = Task.Run(() => secondService.GetSupplierSalesRankAsync(Range(), null, 100));
            await secondVersionRead.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            Assert.False(second.IsCompleted);
            var sharedLease = Assert.Single(leases, lease => lease != firstLease && lease != secondLease);
            firstScope.Dispose();
            Assert.True(firstLease.IsDisposed);
            Assert.False(sharedLease.IsDisposed);
            release.Set();
            var results = await Task.WhenAll(first, second);
            Assert.All(results, rows => Assert.Equal(100m, Assert.Single(rows).TotalAmount));
            Assert.Equal(1, reportReads);
            Assert.True(sharedLease.IsDisposed);
        }
        finally
        {
            release.Set();
            await first;
        }
    }

    [Fact]
    public async Task 不同授权筛选排行和同期不会复用其他条件缓存()
    {
        SeedComplete(_day);
        SeedComplete(_day.AddDays(1));
        SeedRow(_day, "S1", "250", 100, 5, 40);
        SeedRow(_day, "S1", "300", 50, 2, 10);
        SeedRow(_day, "S2", "250", 200, 8, 60);
        SeedRow(_day.AddDays(1), "S1", "250", 70, 3, 20);
        var service = CreateService();
        Assert.Equal(100m, Assert.Single(await service.GetSupplierSalesRankAsync(Range(), new() { "S1" }, 1)).TotalAmount);
        Assert.Equal(2, (await service.GetSupplierSalesRankAsync(Range(), new() { "S1" }, 100)).Count);
        Assert.Equal(200m, Assert.Single(await service.GetSupplierSalesRankAsync(Range(), new() { "S2" }, 100)).TotalAmount);
        var filtered = Assert.Single(await service.GetSupplierSalesRankAsync(Range(), new() { "S1" }, 100, "300"));
        Assert.Equal("300", filtered.SupplierCode);
        Assert.Equal(50m, filtered.TotalAmount);
        var nextRange = new DateRangeDto { StartDate = _day.AddDays(1), EndDate = _day.AddDays(1) };
        var next = Assert.Single(await service.GetSupplierSalesRankAsync(nextRange, new() { "S1" }, 100));
        Assert.Equal(70m, next.TotalAmount);
        Assert.Null(next.CompareTotalAmount);
        nextRange.CompareStartDate = _day;
        nextRange.CompareEndDate = _day;
        var withCompare = Assert.Single(await service.GetSupplierSalesRankAsync(nextRange, new() { "S1" }, 100));
        Assert.Equal(70m, withCompare.TotalAmount);
        Assert.Equal(100m, withCompare.CompareTotalAmount);
    }

    [Fact]
    public async Task 完整版本商品下钻不会读取或覆盖旧路径缓存()
    {
        SeedComplete(_day);
        _db.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = _day, BranchCode = "S1", SupplierCode = "250", ProductCode = "P1",
            TotalAmount = 100, TotalQuantity = 2, TotalCost = 60, GrossProfit = 40,
        }).ExecuteCommand();
        var service = CreateService();
        var status = await service.GetProductReportStatisticStatusAsync(Range());
        var rawKey = SalesDashboardCacheKeys.ProductBranch(Range(), "P1", new() { "S1" }, status.CacheVersion);
        _cache.Set(rawKey, new List<ProductBranchSalesDto> { new() { BranchCode = "S1", SalesAmount = 999 } });
        var row = Assert.Single(await service.GetProductSalesByAllBranchesAsync(Range(), "P1", new() { "S1" }, status));
        Assert.Equal(100m, row.SalesAmount);
        Assert.Equal(SalesStatisticRefreshStatus.Fresh, status.StatisticStatus);
        Assert.Equal(999m, Assert.Single(_cache.Get<List<ProductBranchSalesDto>>(rawKey)!).SalesAmount);
    }

    private sealed class ReportTestDbScope(SqlSugarClient db) : IDisposable
    {
        public SqlSugarClient Db { get; } = db;
        public bool IsDisposed { get; private set; }
        public void Dispose()
        {
            IsDisposed = true;
            Db.Dispose();
        }
    }

    private DateRangeDto Range() => new() { StartDate = _day, EndDate = _day };

    private void SeedComplete(DateTime date)
    {
        foreach (var type in new[] { SalesStatisticType.ProductStoreDaily, SalesStatisticType.AustralianSupplierStoreSales, SalesStatisticType.ChinaSupplierStoreSales })
            _db.Insertable(new SalesStatisticRefreshState
            {
                StatisticType = type, Date = date, Status = SalesStatisticRefreshStatus.Fresh,
                SourceProductVersion = "version", LastAggregatedAtUtc = date.AddDays(1), CompletedAtUtc = date.AddDays(1),
                LastCheckedAtUtc = date.AddDays(1),
            }).ExecuteCommand();
    }

    private void SeedRow(DateTime date, string branch, string supplier, decimal amount, int orders, decimal? profit, int costedRows = 1)
    {
        _db.Insertable(new AustralianSupplierStoreSalesDetail
        {
            Date = date, BranchCode = branch, SupplierCode = supplier, TotalAmount = amount, TotalQuantity = orders,
            OrderCount = orders, GrossProfit = profit, TotalCost = profit.HasValue ? amount - profit : null,
            StatisticRowCount = 1, CostedRowCount = costedRows, GrossProfitRowCount = profit.HasValue ? 1 : 0,
        }).ExecuteCommand();
    }

    private SalesDashboardReactService CreateService(bool enabled = true, ISqlSugarClient? db = null, IServiceScopeFactory? scopeFactory = null)
    {
        var local = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext).GetField("_db", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(local, db ?? _db);
        // 新供应商读取不应访问 POSM；未初始化的上下文使误用立即失败。
        var posm = (POSMSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(POSMSqlSugarContext));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Reports:UseSupplierRollups"] = enabled.ToString(),
        }).Build();
        return new SalesDashboardReactService(local, posm, null!, NullLogger<SalesDashboardReactService>.Instance, _cache, scopeFactory, configuration);
    }

    public void Dispose()
    {
        _db.Dispose();
        _cache.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_path);
    }
}
